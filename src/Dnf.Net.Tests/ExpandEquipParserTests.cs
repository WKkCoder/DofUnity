using Dnf.Net.Crypto;
using Dnf.Net.Frame;
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

    // ---- 槽位范围 10 到 21 的边界:拒绝只能来自范围本身 ----
    // 上面那条越界用例的夹具是光秃秃的记录:无回调时槽 9 缺省按装扮读(要多两个块)、槽 22 缺省按宠物读(要多一个 u32),
    // 光秃秃的记录会先死在"块长度不对 / 读不通"上,范围检查放宽到 9 或 22 时它照样被拒绝,看不出来。
    // 下面的夹具对各自的读法都是自洽的(带块、带宠物 u32,或回调答 Other),并用宽范围的读取器对照证明记录本身读得通。
    // 期望值写字面数字 9、10、21、22,不引用 EquipListReader 的常量。

    private static ItemSpec SelfConsistentRecordWithoutAResolver(int slot)
    {
        ItemSpec s = Item(slot, ItemA);
        if (slot <= 9)
        {
            s.SocketBlock = Socket();
            s.ExpansionBlock = Expansion();
        }
        if (slot == 22)
        {
            s.PetTime = 600;
        }
        return s;
    }

    [Theory]
    [InlineData(9)]
    [InlineData(22)]
    public void ASelfConsistentRecordJustOutsideTheRangeRejectsTheWholePacketWhateverGroupItIsIn(int slot)
    {
        ItemSpec record = SelfConsistentRecordWithoutAResolver(slot);

        // 对照:同样的记录交给宽范围(0 到 25)的详细列表读取器读得通,说明下面的拒绝只来自备用组的槽位范围。
        byte[] listBytes = new NoticePayloadBuilder().AppendDetailList(record).ToArray();
        Assert.True(EquipListReader.TryReadDetailed(new PacketReader(listBytes), null, 0, 25, out ItemEntry[] wide));
        Assert.Equal(slot, wide[0].Slot);

        for (int group = 0; group < 3; group++)
        {
            ItemSpec[][] groups = { None, None, None };
            groups[group] = new[] { record };
            byte[] payload = Packet(1, (1, groups[0]), (2, groups[1]), (3, groups[2]));
            Assert.False(ExpandEquipParser.TryParse(payload, out ExpandEquipNotice n), "组下标 " + group);
            Assert.Null(n);
        }
    }

    [Theory]
    [InlineData(9)]
    [InlineData(22)]
    public void ARecordJustOutsideTheRangeIsRejectedWhenTheResolverAnswersOtherAndNoBlocksAreExpected(int slot)
    {
        // 回调答 Other:任何槽位都不带可选块,光秃秃的 27 字节记录就是自洽的;此时只有槽位范围能拒绝它。
        DictionaryKindResolver resolver = new DictionaryKindResolver().With(ItemA, ResolvedItemKind.Other);
        byte[] bare = new NoticePayloadBuilder().AppendDetailList(Item(slot, ItemA)).ToArray();
        Assert.True(EquipListReader.TryReadDetailed(new PacketReader(bare), resolver, 0, 25, out _));

        byte[] payload = Packet(1, (1, None), (2, new[] { Item(slot, ItemA) }), (3, None));
        Assert.False(ExpandEquipParser.TryParse(payload, resolver, out ExpandEquipNotice n));
        Assert.Null(n);
    }

    [Fact]
    public void TheBoundarySlotsTenAndTwentyOneAreAcceptedAndVisibleInTheResult()
    {
        // 槽 10 与槽 21 在同一组里:都收下,结果里看得到这两个槽,线上顺序不变。
        ExpandEquipNotice n = ParseOk(Packet(9001, (1, None), (2, new[] { Item(10, ItemA), Item(21, ItemB) }), (3, None)));
        Assert.Empty(n.Groups[0].Entries);
        Assert.Equal(2, n.Groups[1].Entries.Count);
        Assert.Empty(n.Groups[2].Entries);
        Assert.Equal(new[] { 10, 21 }, n.Groups[1].Entries.Select(e => (int)e.Slot).ToArray());
        Assert.Equal(new uint[] { 900001, 900002 }, n.Groups[1].Entries.Select(e => e.ItemId).ToArray());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(21)]
    public void EachBoundarySlotIsAcceptedOnItsOwnInEveryGroup(int slot)
    {
        for (int group = 0; group < 3; group++)
        {
            ItemSpec[][] groups = { None, None, None };
            groups[group] = new[] { Item(slot, ItemA) };
            ExpandEquipNotice n = ParseOk(Packet(9001, (1, groups[0]), (2, groups[1]), (3, groups[2])));
            for (int g = 0; g < 3; g++)
            {
                Assert.Equal(g == group ? 1 : 0, n.Groups[g].Entries.Count);
            }
            Assert.Equal(slot, n.Groups[group].Entries[0].Slot);
        }
    }

    // 三个槽位上界/下界常量的线上数值表(备用装备组槽位 = 循环下标 + 10,下标 0 到 11;本人视图当前装备组槽 0 到 25)。
    // 字面数字,不从产品常量推:常量被改动时测试要能发现。

    [Fact]
    public void TheExpandGroupMinimumSlotIsTenOnTheWire()
    {
        // 线上数值表
        Assert.Equal(10, EquipListReader.ExpandGroupMinSlot);
    }

    [Fact]
    public void TheExpandGroupMaximumSlotIsTwentyOneOnTheWire()
    {
        // 线上数值表
        Assert.Equal(21, EquipListReader.ExpandGroupMaxSlot);
    }

    [Fact]
    public void TheSelfViewDetailMaximumSlotIsTwentyFiveOnTheWire()
    {
        // 线上数值表
        Assert.Equal(25, EquipListReader.SelfDetailMaxSlot);
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
        ExpandEquipNotice n = ParseOk(payload, new DictionaryKindResolver().With(ItemB, ResolvedItemKind.Avatar));
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
