using Dnf.Net.Crypto;
using Dnf.Net.Session;
using static Dnf.Net.Tests.ItemWire;

namespace Dnf.Net.Tests;

/// <summary>
/// 宠物属性表包(类 0、包号 105)解析器测试:每只宠物多出的两个字节取决于成长标志。夹具手工拼合成字节:
/// 宠物 uid 从 9001 起,名字是合成的几个字节,不含真实宠物名或数值。
/// </summary>
public class CreatureAttributeParserTests
{
    private static readonly byte[] PetName = { 0x50, 0x65, 0x74 };

    private static NoticePayloadBuilder Pet(NoticePayloadBuilder b, long uid, int stomach, int flag, long exp, int g1, int g2, int level, byte[] name, int noCharge)
    {
        b.U32(uid).U8(stomach).U8(flag).U32(exp);
        if (flag == 0)
        {
            b.U8(g1).U8(g2);
        }
        return b.U8(level).U32(name.Length).Raw(name).U8(noCharge);
    }

    private static CreatureAttributeNotice ParseOk(byte[] payload)
    {
        Assert.True(CreatureAttributeParser.TryParse(payload, out CreatureAttributeNotice notice));
        Assert.NotNull(notice);
        return notice;
    }

    [Fact]
    public void AnEmptyTableIsOneCountByteAndTheDirectFrameAddsTailAndPaddingToEight()
    {
        // 直连帧:正文 1 + PI 尾 4 + 零填充 3 = 8(槽 = 105 % 14 = 7,块长 8)。
        Assert.Equal(8, CipherSlots.BlockSizes[CipherSlots.SlotForPacket(105)]);
        byte[] payload = DirectFramePayload(new byte[] { 0x00 }, 105);
        Assert.Equal(8, payload.Length);
        CreatureAttributeNotice n = ParseOk(payload);
        Assert.Empty(n.Creatures);
        Assert.Equal(1, n.ConsumedByteCount);
        Assert.Equal(7, n.TrailingByteCount);
    }

    [Fact]
    public void ANonGrowthPetHasNoTwoExtraBytes()
    {
        byte[] payload = Pet(new NoticePayloadBuilder().U8(1), 9001, 80, 1, 123456, 0, 0, 12, PetName, 1).ToArray();
        Assert.Equal(1 + (4 + 1 + 1 + 4 + 1 + 4 + 3 + 1), payload.Length);
        CreatureAttributeNotice n = ParseOk(payload);
        CreatureAttributeEntry e = Assert.Single(n.Creatures);
        Assert.Equal(9001u, e.Uid);
        Assert.Equal(80, e.Stomach);
        Assert.Equal(1, e.GrowthFlag);
        Assert.False(e.HasGrowthBytes);
        Assert.Equal(123456u, e.Exp);
        Assert.Equal(0, e.GrowthByte1);
        Assert.Equal(0, e.GrowthByte2);
        Assert.Equal(12, e.Level);
        Assert.Equal(PetName, e.Name.ToArray());
        Assert.True(e.IsNoCharge);
        Assert.Equal(payload.Length, n.ConsumedByteCount);
    }

    [Fact]
    public void AGrowthPetCarriesTwoExtraBytesAfterTheExperience()
    {
        byte[] payload = Pet(new NoticePayloadBuilder().U8(1), 9002, 50, 0, 777, 0x31, 0x32, 20, PetName, 0).ToArray();
        Assert.Equal(1 + (4 + 1 + 1 + 4 + 2 + 1 + 4 + 3 + 1), payload.Length);
        CreatureAttributeEntry e = Assert.Single(ParseOk(payload).Creatures);
        Assert.True(e.HasGrowthBytes);
        Assert.Equal(0x31, e.GrowthByte1);
        Assert.Equal(0x32, e.GrowthByte2);
        Assert.Equal(20, e.Level);
        Assert.Equal(PetName, e.Name.ToArray());
        Assert.False(e.IsNoCharge);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(0x80)]
    [InlineData(0xFF)]
    public void AnyNonZeroGrowthFlagMeansNoExtraBytes(int flag)
    {
        byte[] payload = Pet(new NoticePayloadBuilder().U8(1), 9003, 1, flag, 5, 0, 0, 3, new byte[0], 0).ToArray();
        CreatureAttributeEntry e = Assert.Single(ParseOk(payload).Creatures);
        Assert.False(e.HasGrowthBytes);
        Assert.Equal(flag, e.GrowthFlag);
        Assert.Empty(e.Name);
    }

    [Fact]
    public void MixedPetsKeepTheCursorAlignedAcrossBothBranches()
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(3);
        Pet(b, 9001, 10, 0, 100, 1, 2, 1, PetName, 0);
        Pet(b, 9002, 20, 1, 200, 0, 0, 2, new byte[] { 0x41 }, 1);
        Pet(b, 9003, 30, 0, 300, 3, 4, 3, new byte[0], 0);
        byte[] payload = b.ToArray();
        CreatureAttributeNotice n = ParseOk(payload);
        Assert.Equal(3, n.Creatures.Count);
        Assert.Equal(new uint[] { 9001, 9002, 9003 }, n.Creatures.Select(c => c.Uid).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, n.Creatures.Select(c => (int)c.Level).ToArray());
        Assert.Equal(3, n.Creatures[2].GrowthByte1);
        Assert.Equal(4, n.Creatures[2].GrowthByte2);
        Assert.Equal(payload.Length, n.ConsumedByteCount);
    }

    [Fact]
    public void ACountThatCannotFitIsRejectedBeforeAnyEntryIsRead()
    {
        byte[] payload = new NoticePayloadBuilder().U8(5).Raw(new byte[3 * 16 + 15]).ToArray();
        Assert.False(CreatureAttributeParser.TryParse(payload, out CreatureAttributeNotice n));
        Assert.Null(n);
    }

    [Fact]
    public void ANameLengthThatLiesAboutTheRemainingBytesIsRejected()
    {
        byte[] payload = new NoticePayloadBuilder().U8(1).U32(9001).U8(1).U8(1).U32(5).U8(1).U32(0xFFFFFFFFL).Raw(new byte[20]).ToArray();
        Assert.False(CreatureAttributeParser.TryParse(payload, out _));
    }

    [Fact]
    public void EveryShorterPrefixIsRejected()
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(2);
        Pet(b, 9001, 10, 0, 100, 1, 2, 1, PetName, 0);
        Pet(b, 9002, 20, 1, 200, 0, 0, 2, PetName, 1);
        byte[] full = b.ToArray();
        ParseOk(full);
        for (int length = 0; length < full.Length; length++)
        {
            Assert.False(CreatureAttributeParser.TryParse(full.Take(length).ToArray(), out CreatureAttributeNotice n), "长度 " + length);
            Assert.Null(n);
        }
    }

    [Fact]
    public void TrailingBytesAreOnlyCounted()
    {
        byte[] body = Pet(new NoticePayloadBuilder().U8(1), 9001, 10, 1, 100, 0, 0, 1, PetName, 0).ToArray();
        byte[] payload = body.Concat(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0, 0 }).ToArray();
        CreatureAttributeNotice n = ParseOk(payload);
        Assert.Single(n.Creatures);
        Assert.Equal(body.Length, n.ConsumedByteCount);
        Assert.Equal(6, n.TrailingByteCount);
    }

    [Fact]
    public void NullAndEmptyPayloadsAreRejectedAndTheListIsReadOnly()
    {
        Assert.False(CreatureAttributeParser.TryParse(null, out CreatureAttributeNotice a));
        Assert.Null(a);
        Assert.False(CreatureAttributeParser.TryParse(new byte[0], out _));
        CreatureAttributeNotice n = ParseOk(new byte[] { 0x00 });
        Assert.True(((System.Collections.IList)n.Creatures).IsReadOnly);
        Assert.Equal(105, CreatureAttributeParser.PacketId);
    }
}
