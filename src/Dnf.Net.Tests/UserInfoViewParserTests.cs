using Dnf.Net.Session;
using static Dnf.Net.Tests.ItemWire;

namespace Dnf.Net.Tests;

/// <summary>
/// 用户信息模式 0 / 模式 1 装备视图解析器测试(<see cref="UserInfoViewParser"/>)。夹具手工拼合成字节:
/// 名字是合成的几个字节,不含真实角色名;物品号 900001 起;非物品尾巴是合成填充。
/// 既有的 <see cref="UserInfoParser"/> 仍只认模式 2,本文件里有回归断言。
/// </summary>
public class UserInfoViewParserTests
{
    private static readonly byte[] Name9 = { 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49 };
    private static readonly byte[] Header6 = { 1, 2, 3, 4, 5, 6 };

    private static UserInfoViewNotice ParseOk(byte[] payload, IItemKindResolver resolver = null)
    {
        Assert.True(UserInfoViewParser.TryParse(payload, resolver, out UserInfoViewNotice notice, out UserInfoViewStatus status), "拒绝原因 " + status);
        Assert.Equal(UserInfoViewStatus.Ok, status);
        Assert.NotNull(notice);
        return notice;
    }

    private static void AssertRejected(byte[] payload, UserInfoViewStatus expected)
    {
        Assert.False(UserInfoViewParser.TryParse(payload, null, out UserInfoViewNotice notice, out UserInfoViewStatus status));
        Assert.Null(notice);
        Assert.Equal(expected, status);
    }

    // ---- 模式 0:公共视图 ----

    [Fact]
    public void PublicViewLocatesTheCompactListAtFifteenPlusTheNameLength()
    {
        // 九字节名字:列表在偏移 24(与笔记 §2.2.6 里的样本几何相同:1 条 22 字节,止于 47,再加 1 个尾字节)。
        byte[] tail = new byte[50];
        byte[] payload = PublicViewPacket(1, 0x1234, Name9, Header6, 0x66, tail, (4, ItemA, 0x2B));
        Assert.Equal(24 + 1 + 22 + 1 + 50, payload.Length);
        UserInfoViewNotice n = ParseOk(payload);
        Assert.Equal(0, n.Mode);
        Assert.True(n.IsPublicView);
        Assert.False(n.IsSelfView);
        Assert.Equal(24, n.EquipListOffset);
        Assert.Equal(1 + 22 + 1, n.EquipListLength);
        Assert.Equal(48, n.ConsumedByteCount);
        Assert.Equal(50, n.TrailingByteCount);
        Assert.Equal(payload.Length, n.ConsumedByteCount + n.TrailingByteCount);
    }

    [Fact]
    public void PublicViewReadsUidNameHeaderBytesRecordsAndTheTrailingByte()
    {
        byte[] payload = PublicViewPacket(1, 0xA1B2, Name9, Header6, 0x66, new byte[4], (0, ItemA, 0x03), (11, ItemB, 0x40));
        UserInfoViewNotice n = ParseOk(payload);
        Assert.Equal(0xA1B2, n.Uid);
        Assert.Equal(Name9, n.Name.ToArray());
        Assert.Equal(Header6, n.HeaderBytes.ToArray());
        Assert.Equal(2, n.CompactEquips.Count);
        Assert.Equal(0, n.CompactEquips[0].Slot);
        Assert.Equal(1, n.CompactEquips[0].UpgradeLevel);
        Assert.True(n.CompactEquips[0].IsAmplified);
        Assert.Equal(11, n.CompactEquips[1].Slot);
        Assert.Equal(32, n.CompactEquips[1].UpgradeLevel);
        Assert.False(n.CompactEquips[1].IsAmplified);
        Assert.Equal(0x66, n.CompactTrailingByte);
        Assert.Empty(n.DetailedEquips);
        Assert.Equal(0, n.StatBlockLength);
    }

    [Fact]
    public void PublicViewWithAnEmptyNameAndAnEmptyListIsAccepted()
    {
        byte[] payload = PublicViewPacket(1, 7, new byte[0], Header6, 0, new byte[0]);
        UserInfoViewNotice n = ParseOk(payload);
        Assert.Equal(15, n.EquipListOffset);
        Assert.Empty(n.CompactEquips);
        Assert.Empty(n.Name);
        Assert.Equal(2, n.EquipListLength);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(0x0101)]
    public void PublicViewEntryCountMustBeExactlyOne(int count)
    {
        AssertRejected(PublicViewPacket(count, 1, Name9, Header6, 0, new byte[8]), UserInfoViewStatus.Malformed);
    }

    [Fact]
    public void PublicViewNameLengthThatLiesAboutTheRemainingBytesIsRejected()
    {
        byte[] payload = new NoticePayloadBuilder().U8(0).U16(1).U16(5).U32(0xFFFFFFFFL).Raw(new byte[40]).ToArray();
        AssertRejected(payload, UserInfoViewStatus.Malformed);
        byte[] justOver = new NoticePayloadBuilder().U8(0).U16(1).U16(5).U32(11).Raw(new byte[10]).ToArray();
        AssertRejected(justOver, UserInfoViewStatus.Malformed);
    }

    [Fact]
    public void PublicViewWithASlotAboveElevenIsRejected()
    {
        AssertRejected(PublicViewPacket(1, 1, Name9, Header6, 0, new byte[8], (12, ItemA, 0)), UserInfoViewStatus.Malformed);
    }

    [Fact]
    public void EveryTruncationOfAPublicViewUpToTheTrailingByteIsRejected()
    {
        byte[] full = PublicViewPacket(1, 9, Name9, Header6, 0x11, new byte[0], (2, ItemA, 1), (3, ItemB, 2));
        ParseOk(full);
        for (int length = 0; length < full.Length; length++)
        {
            AssertRejected(full.Take(length).ToArray(), UserInfoViewStatus.Malformed);
        }
    }

    // ---- 模式 1:本人详细视图 ----

    [Fact]
    public void SelfViewLocatesTheDetailedListAtFourteenPlusTheStatBlockLength()
    {
        // 与笔记 §2.2.6 的几何相同:装备列表 = 1 + 3×27 = 82 字节,槽号均 ≥ 10 且互异升序,列表之后是非物品尾巴。
        byte[] stat = new byte[20];
        byte[] tail = new byte[49 + 4];
        byte[] payload = SelfViewPacket(1, 0x0102, 0x11223344, stat, 0x5A, tail, Item(10, ItemA), Item(12, ItemB), Item(17, ItemC));
        UserInfoViewNotice n = ParseOk(payload);
        Assert.Equal(1, n.Mode);
        Assert.True(n.IsSelfView);
        Assert.Equal(20, n.StatBlockLength);
        Assert.Equal(14 + 20, n.EquipListOffset);
        Assert.Equal(1 + 3 * 27, n.EquipListLength);
        Assert.Equal(34 + 82, n.ConsumedByteCount);
        Assert.Equal(53, n.TrailingByteCount);
        Assert.Equal(0x0102, n.X);
        Assert.Equal(0x11223344u, n.Y);
        Assert.Equal(0x5A, n.SelfByte);
        Assert.Equal(new[] { 10, 12, 17 }, n.DetailedEquips.Select(e => (int)e.Slot).ToArray());
        Assert.Empty(n.CompactEquips);
        Assert.Equal(0, n.Uid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(200)]
    public void SelfViewListOffsetFollowsTheStatBlockLengthPrefix(int statLength)
    {
        byte[] payload = SelfViewPacket(1, 1, 2, new byte[statLength], 0, new byte[4], Item(10, ItemA));
        UserInfoViewNotice n = ParseOk(payload);
        Assert.Equal(statLength, n.StatBlockLength);
        Assert.Equal(14 + statLength, n.EquipListOffset);
        Assert.Single(n.DetailedEquips);
    }

    [Fact]
    public void SelfViewStatBlockLengthThatLiesAboutTheRemainingBytesIsRejected()
    {
        byte[] payload = new NoticePayloadBuilder().U8(1).U16(1).U16(1).U32(2).U32(0xFFFFFFF0L).Raw(new byte[60]).ToArray();
        AssertRejected(payload, UserInfoViewStatus.Malformed);
        byte[] justOver = new NoticePayloadBuilder().U8(1).U16(1).U16(1).U32(2).U32(30).Raw(new byte[29]).ToArray();
        AssertRejected(justOver, UserInfoViewStatus.Malformed);
    }

    [Fact]
    public void SelfViewDetailedSlotAboveTwentyFiveIsRejected()
    {
        AssertRejected(SelfViewPacket(1, 1, 2, new byte[3], 0, new byte[4], Item(26, ItemA)), UserInfoViewStatus.Malformed);
    }

    [Fact]
    public void SelfViewAcceptsTheWholeSlotRangeZeroToTwentyFive()
    {
        ItemSpec avatar = Item(0, ItemA);
        avatar.SocketBlock = Socket();
        avatar.ExpansionBlock = Expansion();
        byte[] payload = SelfViewPacket(1, 1, 2, new byte[3], 0, new byte[4], avatar, Item(10, ItemB), Item(25, ItemC));
        UserInfoViewNotice n = ParseOk(payload);
        Assert.Equal(3, n.DetailedEquips.Count);
        Assert.True(n.DetailedEquips[0].HasAvatarBlocks);
    }

    [Fact]
    public void SelfViewResolverOverridesTheSlotApproximation()
    {
        // 槽 2 按近似是装扮,回调说是普通物品:不读装扮块,否则后面的字节全部错位。
        byte[] payload = SelfViewPacket(1, 1, 2, new byte[3], 0, new byte[4], Item(2, ItemB), Item(10, ItemA));
        DictionaryKindResolver resolver = new DictionaryKindResolver().With(ItemB, ResolvedItemKind.Other).With(ItemA, ResolvedItemKind.Other);
        UserInfoViewNotice n = ParseOk(payload, resolver);
        Assert.False(n.DetailedEquips[0].HasAvatarBlocks);
        Assert.Equal(10, n.DetailedEquips[1].Slot);
    }

    [Fact]
    public void SelfViewEntryCountMustBeExactlyOne()
    {
        AssertRejected(SelfViewPacket(0, 1, 2, new byte[3], 0, new byte[4]), UserInfoViewStatus.Malformed);
        AssertRejected(SelfViewPacket(2, 1, 2, new byte[3], 0, new byte[4]), UserInfoViewStatus.Malformed);
    }

    [Fact]
    public void EveryTruncationOfASelfViewUpToTheListEndIsRejected()
    {
        byte[] full = SelfViewPacket(1, 1, 2, new byte[5], 0x22, new byte[0], Item(10, ItemA), Item(11, ItemB));
        ParseOk(full);
        for (int length = 0; length < full.Length; length++)
        {
            AssertRejected(full.Take(length).ToArray(), UserInfoViewStatus.Malformed);
        }
    }

    [Fact]
    public void SelfViewWithAnEmptyListIsAccepted()
    {
        UserInfoViewNotice n = ParseOk(SelfViewPacket(1, 1, 2, new byte[0], 0, new byte[4]));
        Assert.Empty(n.DetailedEquips);
        Assert.Equal(14, n.EquipListOffset);
        Assert.Equal(1, n.EquipListLength);
    }

    // ---- 模式与分工 ----

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(0x78)]
    [InlineData(0xFF)]
    public void OtherModesAreUnsupportedAndNeverReadWithTheViewLayouts(int mode)
    {
        byte[] payload = new NoticePayloadBuilder().U8(mode).U16(1).Raw(new byte[80]).ToArray();
        AssertRejected(payload, UserInfoViewStatus.UnsupportedMode);
    }

    [Fact]
    public void NullAndEmptyPayloadsAreMalformed()
    {
        AssertRejected(null, UserInfoViewStatus.Malformed);
        AssertRejected(new byte[0], UserInfoViewStatus.Malformed);
        AssertRejected(new byte[] { 0x00 }, UserInfoViewStatus.Malformed);
        AssertRejected(new byte[] { 0x01, 0x01 }, UserInfoViewStatus.Malformed);
    }

    [Fact]
    public void TheExistingCharacterListParserStillRefusesModesZeroAndOne()
    {
        // 既有 UserInfoParser 保持"只认模式 2":这里的视图包喂给它,必须按"不支持的模式"拒绝,不能被读成别的东西。
        byte[] publicView = PublicViewPacket(1, 1, Name9, Header6, 0, new byte[8], (1, ItemA, 0));
        byte[] selfView = SelfViewPacket(1, 1, 2, new byte[3], 0, new byte[4], Item(10, ItemA));
        Assert.False(UserInfoParser.TryParse(publicView, out UserInfoNotice a, out UserInfoParseStatus sa));
        Assert.False(UserInfoParser.TryParse(selfView, out UserInfoNotice b, out UserInfoParseStatus sb));
        Assert.Null(a);
        Assert.Null(b);
        Assert.Equal(UserInfoParseStatus.UnsupportedMode, sa);
        Assert.Equal(UserInfoParseStatus.UnsupportedMode, sb);
    }

    [Fact]
    public void ViewListsAreReadOnlyViews()
    {
        UserInfoViewNotice n = ParseOk(PublicViewPacket(1, 1, Name9, Header6, 0, new byte[4], (1, ItemA, 0)));
        Assert.True(((System.Collections.IList)n.CompactEquips).IsReadOnly);
        Assert.True(((System.Collections.IList)n.DetailedEquips).IsReadOnly);
        Assert.True(((System.Collections.IList)n.Name).IsReadOnly);
        Assert.True(((System.Collections.IList)n.HeaderBytes).IsReadOnly);
    }

    [Fact]
    public void ShortOverloadsAgreeWithTheFullOne()
    {
        byte[] payload = PublicViewPacket(1, 1, Name9, Header6, 0, new byte[4], (1, ItemA, 0));
        Assert.True(UserInfoViewParser.TryParse(payload, out UserInfoViewNotice a));
        Assert.True(UserInfoViewParser.TryParse(payload, out UserInfoViewNotice b, out UserInfoViewStatus s));
        Assert.Equal(UserInfoViewStatus.Ok, s);
        Assert.Equal(a.Uid, b.Uid);
    }

    [Fact]
    public void DeterministicGarbageNeverThrowsAndSuccessesAccountForEveryByte()
    {
        uint state = 0xC0FFEE11;
        for (int round = 0; round < 3000; round++)
        {
            state = state * 1664525u + 1013904223u;
            int length = (int)((state >> 16) % 220);
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                state = state * 1664525u + 1013904223u;
                data[i] = (byte)(state >> 24);
            }
            if (length > 3)
            {
                data[0] = (byte)(data[0] & 1);
                data[1] = 1;
                data[2] = 0;
            }
            if (UserInfoViewParser.TryParse(data, out UserInfoViewNotice notice, out UserInfoViewStatus status))
            {
                Assert.Equal(UserInfoViewStatus.Ok, status);
                Assert.Equal(data.Length, notice.ConsumedByteCount + notice.TrailingByteCount);
            }
            else
            {
                Assert.Null(notice);
                Assert.NotEqual(UserInfoViewStatus.Ok, status);
            }
        }
    }
}
