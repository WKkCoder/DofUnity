using Dnf.Net.Crypto;
using Dnf.Net.Session;
using static Dnf.Net.Tests.ItemWire;

namespace Dnf.Net.Tests;

/// <summary>
/// 备用装备组包(类 0、包号 339)解析器测试:<c>u16 唯一号 + 3 × (u8 组号 + 详细装备列表)</c>。
/// 夹具手工拼合成字节,物品号 900001 起,不含真实数值。
/// </summary>
public class ExpandEquipParserTests
{
    private static byte[] Packet(int uid, params (int GroupId, ItemSpec[] Entries)[] groups)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U16(uid);
        foreach ((int groupId, ItemSpec[] entries) in groups)
        {
            b.U8(groupId).AppendDetailList(entries);
        }
        return b.ToArray();
    }

    private static ItemSpec[] None => new ItemSpec[0];

    private static ExpandEquipNotice ParseOk(byte[] payload, IItemKindResolver resolver = null)
    {
        Assert.True(ExpandEquipParser.TryParse(payload, resolver, out ExpandEquipNotice notice));
        Assert.NotNull(notice);
        return notice;
    }

    [Fact]
    public void ThreeEmptyGroupsAreEightBodyBytesAndTheDirectFrameAddsTailAndPaddingToSixteen()
    {
        byte[] body = Packet(0x2468, (1, None), (2, None), (3, None));
        Assert.Equal(8, body.Length);
        // 直连帧:正文 8 + PI 尾 4 + 零填充 4 = 16(槽 = 339 % 14 = 3,块长 16)。
        Assert.Equal(16, CipherSlots.BlockSizes[CipherSlots.SlotForPacket(339)]);
        byte[] payload = DirectFramePayload(body, 339);
        Assert.Equal(16, payload.Length);
        ExpandEquipNotice n = ParseOk(payload);
        Assert.Equal(0x2468, n.Uid);
        Assert.Equal(3, n.Groups.Count);
        Assert.True(n.AllGroupsEmpty);
        Assert.Equal(8, n.ConsumedByteCount);
        Assert.Equal(8, n.TrailingByteCount);
    }

    [Fact]
    public void GroupIdsAreHandedOutRawIncludingTheZeroTheServerWritesForTheFirstGroup()
    {
        ExpandEquipNotice a = ParseOk(Packet(1, (1, None), (2, None), (3, None)));
        Assert.Equal(new[] { 1, 2, 3 }, a.Groups.Select(g => (int)g.GroupId).ToArray());
        ExpandEquipNotice b = ParseOk(Packet(1, (0, None), (2, None), (3, None)));
        Assert.Equal(new[] { 0, 2, 3 }, b.Groups.Select(g => (int)g.GroupId).ToArray());
    }

    [Fact]
    public void OneNonEmptyGroupKeepsItsRecordsAndTheOthersStayEmpty()
    {
        ItemSpec a = Item(10, ItemA);
        a.Ext1 = 7;
        a.Durability = 55;
        a.Enchant = 21;
        a.ClearAvatar = 8;
        ItemSpec b = Item(21, ItemB);
        ExpandEquipNotice n = ParseOk(Packet(9001, (1, None), (2, new[] { a, b }), (3, None)));
        Assert.False(n.AllGroupsEmpty);
        Assert.Empty(n.Groups[0].Entries);
        Assert.Equal(2, n.Groups[1].Entries.Count);
        Assert.Empty(n.Groups[2].Entries);
        ItemEntry first = n.Groups[1].Entries[0];
        Assert.Equal(10, first.Slot);
        Assert.Equal(7, first.UpgradeLevel);
        Assert.Equal(55, first.Durability);
        Assert.Equal(21u, first.EnchantIndex);
        Assert.Equal(8u, first.ClearAvatar);
        Assert.Equal(21, n.Groups[1].Entries[1].Slot);
    }

    [Fact]
    public void EveryGroupCanCarryRecordsAndTheCursorStaysAlignedAcrossGroups()
    {
        ExpandEquipNotice n = ParseOk(Packet(9002,
            (1, new[] { Item(10, ItemA) }),
            (2, new[] { Item(11, ItemB), Item(12, ItemC) }),
            (3, new[] { Item(21, ItemA) })));
        Assert.Single(n.Groups[0].Entries);
        Assert.Equal(2, n.Groups[1].Entries.Count);
        Assert.Equal(21, n.Groups[2].Entries[0].Slot);
        // 2 字节唯一号 + 3 × (组号 1 + 个数 1) + 4 条 27 字节记录。
        Assert.Equal(2 + 3 * (1 + 1) + 4 * 27, n.ConsumedByteCount);
    }

    [Fact]
    public void TheUniqueIdIsHandedOutWithoutAnyFiltering()
    {
        Assert.Equal(0, ParseOk(Packet(0, (1, None), (2, None), (3, None))).Uid);
        Assert.Equal(0xFFFF, ParseOk(Packet(0xFFFF, (1, None), (2, None), (3, None))).Uid);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(22)]
    [InlineData(0)]
    [InlineData(25)]
    public void RecordsOutsideSlotsTenToTwentyOneRejectTheWholePacket(int slot)
    {
        byte[] payload = Packet(1, (1, None), (2, new[] { Item(slot, ItemA) }), (3, None));
        Assert.False(ExpandEquipParser.TryParse(payload, out ExpandEquipNotice n));
        Assert.Null(n);
    }

    [Fact]
    public void AGroupCountThatLiesAboutTheRemainingBytesIsRejected()
    {
        byte[] payload = new NoticePayloadBuilder().U16(1).U8(1).U8(12).Raw(new byte[40]).ToArray();
        Assert.False(ExpandEquipParser.TryParse(payload, out _));
    }

    [Fact]
    public void EveryShorterPrefixIsRejected()
    {
        byte[] full = Packet(1, (1, new[] { Item(10, ItemA) }), (2, None), (3, new[] { Item(15, ItemB) }));
        ParseOk(full);
        for (int length = 0; length < full.Length; length++)
        {
            Assert.False(ExpandEquipParser.TryParse(full.Take(length).ToArray(), out ExpandEquipNotice n), "长度 " + length);
            Assert.Null(n);
        }
    }

    [Fact]
    public void TrailingBytesAreOnlyCounted()
    {
        byte[] body = Packet(1, (1, None), (2, None), (3, None));
        byte[] payload = body.Concat(BagEntryBytes(Item(10, ItemA))).ToArray();
        ExpandEquipNotice n = ParseOk(payload);
        Assert.True(n.AllGroupsEmpty);
        Assert.Equal(body.Length, n.ConsumedByteCount);
        Assert.Equal(25, n.TrailingByteCount);
    }

    [Fact]
    public void ResolverVerdictAppliesInsideTheGroups()
    {
        // 槽 12 缺省不是装扮;回调说它是装扮时要读装扮块。
        ItemSpec avatar = Item(12, ItemB);
        avatar.SocketBlock = Socket();
        avatar.ExpansionBlock = Expansion();
        byte[] payload = Packet(1, (1, new[] { avatar }), (2, None), (3, None));
        ExpandEquipNotice n = ParseOk(payload, new DictionaryKindResolver().With(ItemB, ItemKind.Avatar));
        Assert.True(n.Groups[0].Entries[0].HasAvatarBlocks);
        Assert.False(ExpandEquipParser.TryParse(payload, out _));
    }

    [Fact]
    public void NullAndEmptyPayloadsAreRejected()
    {
        Assert.False(ExpandEquipParser.TryParse(null, out ExpandEquipNotice a));
        Assert.Null(a);
        Assert.False(ExpandEquipParser.TryParse(new byte[0], out _));
        Assert.False(ExpandEquipParser.TryParse(new byte[] { 0x01, 0x00 }, out _));
        Assert.False(ExpandEquipParser.TryParse(new byte[] { 0x01, 0x00, 0x01 }, out _));
    }

    [Fact]
    public void GroupListsAreReadOnlyViewsAndTheConstantsMatch()
    {
        ExpandEquipNotice n = ParseOk(Packet(1, (1, None), (2, None), (3, None)));
        Assert.True(((System.Collections.IList)n.Groups).IsReadOnly);
        Assert.True(((System.Collections.IList)n.Groups[0].Entries).IsReadOnly);
        Assert.Equal(339, ExpandEquipParser.PacketId);
        Assert.Equal(3, ExpandEquipParser.GroupCount);
    }
}
