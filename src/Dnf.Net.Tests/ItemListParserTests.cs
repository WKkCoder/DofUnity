using Dnf.Net.Crypto;
using Dnf.Net.Session;
using static Dnf.Net.Tests.ItemWire;

namespace Dnf.Net.Tests;

/// <summary>
/// 物品空间包(类 0、包号 13)解析器测试。夹具全是手工拼的合成字节,合成物品号 900001 起,不含任何真实物品号、名字或数值。
/// 背包包长 = 5 + 25N(N 含三个货币伪条目);装扮背包 3 + 67N;仓库 5 + 25N;账号仓库 9 + 25N。
/// </summary>
public class ItemListParserTests
{
    private static ItemListNotice ParseOk(byte[] payload, IItemKindResolver resolver = null)
    {
        Assert.True(ItemListParser.TryParse(payload, resolver, out ItemListNotice notice, out ItemParseStatus status), "拒绝原因 " + status);
        Assert.Equal(ItemParseStatus.Ok, status);
        Assert.NotNull(notice);
        return notice;
    }

    private static void AssertRejected(byte[] payload, ItemParseStatus expected, IItemKindResolver resolver = null)
    {
        Assert.False(ItemListParser.TryParse(payload, resolver, out ItemListNotice notice, out ItemParseStatus status));
        Assert.Null(notice);
        Assert.Equal(expected, status);
    }

    // ---- 背包(空间 0) ----

    [Fact]
    public void BagPacketHeaderIsModeCapacityAndCountAtLiteralOffsets()
    {
        // 空间 0,容量 8,个数 3(只有三个货币伪条目)。
        byte[] payload = new NoticePayloadBuilder().U8(0).U16(8).U16(3)
            .AppendBagEntry(Pseudo(0, 11)).AppendBagEntry(Pseudo(1, 22)).AppendBagEntry(Pseudo(2, 33))
            .ToArray();
        Assert.Equal(5 + 25 * 3, payload.Length);
        Assert.Equal(0, payload[0]);
        Assert.Equal(8, payload[1]);
        Assert.Equal(0, payload[2]);
        Assert.Equal(3, payload[3]);
        Assert.Equal(0, payload[4]);
        ItemListNotice n = ParseOk(payload);
        Assert.Equal(ItemSpace.Bag, n.Space);
        Assert.True(n.HasCapacity);
        Assert.Equal(8, n.Capacity);
        Assert.Equal(3, n.DeclaredEntryCount);
        Assert.Empty(n.Entries);
        Assert.Equal(payload.Length, n.ConsumedByteCount);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(16)]
    public void BagCapacityIsTheHeaderValue(int capacity)
    {
        ItemListNotice n = ParseOk(BagSpacePacket(capacity, 1, 2, 3, Item(3)));
        Assert.True(n.HasCapacity);
        Assert.Equal(capacity, n.Capacity);
        Assert.Single(n.Entries);
    }

    [Fact]
    public void BagCapacityIsNotRejectedWhenItIsOutsideTheKnownTiers()
    {
        // 容量档 0/8/16 是服务端表里的取值;解析器不据此拒包,原值照给。
        ItemListNotice n = ParseOk(BagSpacePacket(24, 1, 2, 3));
        Assert.Equal(24, n.Capacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(26)]
    public void BagPacketTotalLengthIsFivePlus25NAndEveryItemSurvivesAscending(int items)
    {
        ItemSpec[] list = Enumerable.Range(0, items).Select(i => Item(3 + i * 2, ItemA + i)).ToArray();
        byte[] payload = BagSpacePacket(8, 100, 200, 300, list);
        Assert.Equal(5 + 25 * (3 + items), payload.Length);
        ItemListNotice n = ParseOk(payload);
        Assert.Equal(items, n.Entries.Count);
        Assert.Equal(3 + items, n.DeclaredEntryCount);
        for (int i = 0; i < items; i++)
        {
            Assert.Equal(3 + i * 2, n.Entries[i].Slot);
            Assert.Equal((uint)(ItemA + i), n.Entries[i].ItemId);
        }
    }

    [Fact]
    public void BagCurrencyPseudoEntriesAreSplitOutOfTheEntryList()
    {
        ItemListNotice n = ParseOk(BagSpacePacket(8, 123456, 789, 42, Item(3), Item(4, ItemB)));
        Assert.Equal(2, n.Entries.Count);
        Assert.All(n.Entries, e => Assert.True(e.Slot >= 3));
        Assert.NotNull(n.Currency);
        Assert.Equal(123456u, n.Currency.Gold);
        Assert.Equal(789u, n.Currency.CoinTotal);
        Assert.Equal(42u, n.Currency.WinPoint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EachOfTheThreeCurrencySlotsIsSplitOutOnItsOwn(int slot)
    {
        // 只发一个货币槽:其余货币缺席(null),条目列表仍为空。
        byte[] payload = new NoticePayloadBuilder().U8(0).U16(0).U16(1).AppendBagEntry(Pseudo(slot, 5000 + slot)).ToArray();
        ItemListNotice n = ParseOk(payload);
        Assert.Empty(n.Entries);
        Assert.Equal(slot == 0 ? 5000u : (uint?)null, n.Currency.Gold);
        Assert.Equal(slot == 1 ? 5001u : (uint?)null, n.Currency.CoinTotal);
        Assert.Equal(slot == 2 ? 5002u : (uint?)null, n.Currency.WinPoint);
    }

    [Fact]
    public void BagCurrencyAmountsAreKeptAsUnsignedRawValues()
    {
        ItemListNotice n = ParseOk(BagSpacePacket(0, 0xFFFFFFFFL, 0x80000000L, 0));
        Assert.Equal(0xFFFFFFFFu, n.Currency.Gold);
        Assert.Equal(0x80000000u, n.Currency.CoinTotal);
        Assert.Equal(0u, n.Currency.WinPoint);
    }

    [Fact]
    public void BagPseudoEntryWhoseItemIdIsNotItsSlotIsRejected()
    {
        ItemSpec odd = Pseudo(1, 50);
        odd.ItemId = ItemA;
        byte[] payload = new NoticePayloadBuilder().U8(0).U16(0).U16(1).AppendBagEntry(odd).ToArray();
        AssertRejected(payload, ItemParseStatus.Malformed);
    }

    // 货币伪条目的物品号必须恰好等于槽号。反例刻意取"物品号不大、但不等于槽号"(0 与 1、1 与 0、2 与 3),
    // 以及"低 16 位等于槽号、高位不同"(槽 1 配 0x10001):只检查"物品号不超过 0xFFFF"或只比低 16 位的弱化写法都会把它们放过去。
    // 数字全是字面值。包里三个货币槽都在,只有一个配错,后面还跟一个普通条目,所以拒绝只可能来自这条检查。
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 3)]
    [InlineData(1, 0x10001)]
    public void ACurrencySlotWhoseItemIdIsNotTheSlotRejectsTheWholeBagPacket(int slot, int itemId)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(0).U16(8).U16(4);
        for (int s = 0; s < 3; s++)
        {
            b.AppendBagEntry(new ItemSpec { Slot = s, ItemId = s == slot ? itemId : s, Data = 100 + s });
        }
        b.AppendBagEntry(Item(5, ItemA));
        AssertRejected(b.ToArray(), ItemParseStatus.Malformed);
    }

    [Fact]
    public void ThreeCurrencySlotsWhoseItemIdsEqualTheirSlotsAreTakenIntoTheCurrencyNotTheEntryTable()
    {
        byte[] payload = new NoticePayloadBuilder().U8(0).U16(8).U16(4)
            .AppendBagEntry(new ItemSpec { Slot = 0, ItemId = 0, Data = 1100 })
            .AppendBagEntry(new ItemSpec { Slot = 1, ItemId = 1, Data = 2200 })
            .AppendBagEntry(new ItemSpec { Slot = 2, ItemId = 2, Data = 3300 })
            .AppendBagEntry(Item(3, ItemA))
            .ToArray();
        ItemListNotice n = ParseOk(payload);
        Assert.Equal(4, n.DeclaredEntryCount);
        Assert.Equal(1100u, n.Currency.Gold);
        Assert.Equal(2200u, n.Currency.CoinTotal);
        Assert.Equal(3300u, n.Currency.WinPoint);
        // 货币不进条目表:表里只有槽 3 的普通条目。
        ItemEntry only = Assert.Single(n.Entries);
        Assert.Equal(3, only.Slot);
        Assert.Equal(900001u, only.ItemId);
    }

    // 货币拆分只属于背包(空间 0):其它空间的槽 0 到 2 即使"物品号不等于槽号"也是普通条目,按线上顺序全部收下,货币为 null。
    // 装扮背包带两个固定块,宠物栏需要物品类型回调(这里一律答 Other),所以每种空间的夹具都是自洽的、只差"是不是背包"。
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(0x0C)]
    public void SlotsZeroToTwoOutsideTheBagWithForeignItemIdsAreOrdinaryEntries(int space)
    {
        (int Slot, long ItemId)[] wire = { (0, 1), (1, 0), (2, 3), (1, 0x10001) };
        ItemSpec[] items = wire.Select(w =>
        {
            ItemSpec s = Item(w.Slot, w.ItemId);
            if (space == 1)
            {
                s.SocketBlock = Socket();
                s.ExpansionBlock = Expansion();
            }
            return s;
        }).ToArray();
        byte[] payload = space switch
        {
            1 => AvatarSpacePacket(items),
            2 => CargoSpacePacket(8, items),
            7 => PetSpacePacket(items),
            _ => AccountCargoSpacePacket(1, 2, items),
        };
        DictionaryKindResolver resolver = new DictionaryKindResolver()
            .With(1, ResolvedItemKind.Other).With(0, ResolvedItemKind.Other)
            .With(3, ResolvedItemKind.Other).With(0x10001, ResolvedItemKind.Other);

        ItemListNotice n = ParseOk(payload, resolver);
        Assert.Equal(4, n.DeclaredEntryCount);
        Assert.Null(n.Currency);
        Assert.Equal(new[] { 0, 1, 2, 1 }, n.Entries.Select(e => (int)e.Slot).ToArray());
        Assert.Equal(new uint[] { 1, 0, 3, 0x10001 }, n.Entries.Select(e => e.ItemId).ToArray());
    }

    [Fact]
    public void BagEntriesWithC3AndRandomOptionVariantsAreAllParsedInOnePacket()
    {
        ItemSpec withC3 = Item(3, ItemA);
        withC3.C3 = new[] { (7L, 1, 2, 3, 4) };
        ItemSpec withRoNoTail = Item(4, ItemB);
        withRoNoTail.RoFields = new[] { (1, 2, 3), (4, 5, 6) };
        withRoNoTail.RoSelector = 2;
        withRoNoTail.RoTailMarker = 0xFF;
        ItemSpec withRoTail = Item(5, ItemC);
        withRoTail.RoFields = new[] { (9, 8, 7) };
        withRoTail.RoSelector = 1;
        withRoTail.RoTailMarker = 0x02;
        withRoTail.RoTail = new byte[] { 1, 2, 3, 4 };
        withRoTail.Trade = 1;
        ItemListNotice n = ParseOk(BagSpacePacket(8, 1, 2, 3, withC3, withRoNoTail, withRoTail, Item(6)));
        Assert.Equal(4, n.Entries.Count);
        Assert.Single(n.Entries[0].C3Records);
        Assert.Equal(2, n.Entries[1].RandomOptionFields.Count);
        Assert.False(n.Entries[1].HasRandomOptionTail);
        Assert.True(n.Entries[2].HasRandomOptionTail);
        Assert.Equal(1, n.Entries[2].TradeRestriction);
        Assert.Equal(6, n.Entries[3].Slot);
    }

    [Fact]
    public void BagEntryAmplifyBooleansPassThroughThePacket()
    {
        ItemSpec none = Item(3);
        ItemSpec unidentified = Item(4);
        unidentified.AmpType = 0x80;
        ItemSpec identified = Item(5);
        identified.AmpType = 9;
        identified.AmpValue = 150;
        ItemListNotice n = ParseOk(BagSpacePacket(8, 0, 0, 0, none, unidentified, identified));
        Assert.False(n.Entries[0].IsAmplified);
        Assert.True(n.Entries[1].IsAmplified);
        Assert.False(n.Entries[1].IsIdentified);
        Assert.True(n.Entries[2].IsAmplified);
        Assert.True(n.Entries[2].IsIdentified);
        Assert.Equal(150, n.Entries[2].AmplifyValue);
    }

    // ---- 装扮背包(空间 1) ----

    [Fact]
    public void AvatarBagPacketHasAThreeByteHeaderAndSixtySevenByteEntries()
    {
        ItemSpec a = Item(0, ItemA);
        a.Data = 30;
        a.SocketBlock = Socket();
        a.ExpansionBlock = Expansion();
        ItemSpec b = Item(1, ItemB);
        b.SocketBlock = Socket(0x20);
        b.ExpansionBlock = Expansion(0x30);
        byte[] payload = AvatarSpacePacket(a, b);
        Assert.Equal(3 + 67 * 2, payload.Length);
        ItemListNotice n = ParseOk(payload);
        Assert.Equal(ItemSpace.AvatarBag, n.Space);
        Assert.False(n.HasCapacity);
        Assert.Equal(0, n.Capacity);
        Assert.Null(n.Currency);
        Assert.Equal(2, n.Entries.Count);
        Assert.True(n.Entries[0].DataIsRemainingTime);
        Assert.Equal(30u, n.Entries[0].Data);
        Assert.True(n.Entries[1].HasAvatarBlocks);
        Assert.Equal(Socket(0x20), n.Entries[1].SocketBlock.ToArray());
    }

    [Fact]
    public void AvatarBagPacketWithZeroEntriesIsAccepted()
    {
        byte[] payload = new byte[] { 0x01, 0x00, 0x00 };
        ItemListNotice n = ParseOk(payload);
        Assert.Equal(ItemSpace.AvatarBag, n.Space);
        Assert.Empty(n.Entries);
        Assert.Equal(3, n.ConsumedByteCount);
    }

    [Fact]
    public void AvatarBagEntryWithAWrongBlockLengthRejectsTheWholePacket()
    {
        ItemSpec a = Item(0, ItemA);
        a.SocketBlock = Socket();
        a.ExpansionBlock = Expansion();
        byte[] good = AvatarSpacePacket(a);
        byte[] bad = (byte[])good.Clone();
        // 偏移 3 是条目起点,条目内偏移 21 起是镶嵌块的 u32 长度:改成 29。
        bad[3 + 21] = 29;
        ParseOk(good);
        AssertRejected(bad, ItemParseStatus.Malformed);
    }

    // ---- 仓库(空间 2)与账号仓库(空间 0xC) ----

    [Fact]
    public void CargoPacketReadsCapacityAndCountLikeTheBag()
    {
        byte[] payload = CargoSpacePacket(112, Item(0, ItemA), Item(5, ItemB));
        Assert.Equal(5 + 25 * 2, payload.Length);
        ItemListNotice n = ParseOk(payload);
        Assert.Equal(ItemSpace.Cargo, n.Space);
        Assert.True(n.HasCapacity);
        Assert.Equal(112, n.Capacity);
        Assert.Null(n.Currency);
        Assert.Equal(2, n.Entries.Count);
        Assert.Equal(5, n.Entries[1].Slot);
    }

    [Fact]
    public void CargoSlotsZeroToTwoAreOrdinaryEntriesNotCurrency()
    {
        // 货币拆分只属于背包:仓库的槽 0、1、2 是普通格子。
        ItemListNotice n = ParseOk(CargoSpacePacket(8, Item(0, ItemA), Item(1, ItemB), Item(2, ItemC)));
        Assert.Equal(3, n.Entries.Count);
        Assert.Null(n.Currency);
    }

    [Fact]
    public void AccountCargoPacketReadsTheTwoExtraHeaderFieldsThenTheCount()
    {
        byte[] payload = AccountCargoSpacePacket(0x0123, 0x89ABCDEFL, Item(1, ItemA));
        Assert.Equal(9 + 25, payload.Length);
        ItemListNotice n = ParseOk(payload);
        Assert.Equal(ItemSpace.AccountCargo, n.Space);
        Assert.True(n.HasAccountCargoHeader);
        Assert.Equal(0x0123, n.AccountCargoWord);
        Assert.Equal(0x89ABCDEFu, n.AccountCargoDword);
        Assert.False(n.HasCapacity);
        Assert.Single(n.Entries);
    }

    [Fact]
    public void AccountCargoHeaderTruncatedInsideTheU32IsRejected()
    {
        byte[] payload = new byte[] { 0x0C, 0x01, 0x00, 0xEF, 0xCD };
        AssertRejected(payload, ItemParseStatus.Malformed);
    }

    // ---- 宠物栏(空间 7):需要类型回调 ----

    [Fact]
    public void PetBagWithZeroEntriesNeedsNoResolver()
    {
        ItemListNotice n = ParseOk(PetSpacePacket());
        Assert.Equal(ItemSpace.PetBag, n.Space);
        Assert.Empty(n.Entries);
    }

    [Fact]
    public void PetBagEntriesWithAResolverReadThePetU32OnlyForPets()
    {
        ItemSpec pet = Item(0, ItemA);
        pet.PetTime = 777;
        ItemSpec egg = Item(1, ItemB);
        DictionaryKindResolver resolver = new DictionaryKindResolver().With(ItemA, ResolvedItemKind.Pet).With(ItemB, ResolvedItemKind.Other);
        ItemListNotice n = ParseOk(PetSpacePacket(pet, egg), resolver);
        Assert.Equal(2, n.Entries.Count);
        Assert.True(n.Entries[0].HasPetRemainTime);
        Assert.Equal(777u, n.Entries[0].PetRemainTime);
        Assert.False(n.Entries[1].HasPetRemainTime);
    }

    [Fact]
    public void PetBagEntriesWithoutAResolverRejectTheWholePacketAsKindUnresolved()
    {
        AssertRejected(PetSpacePacket(Item(0, ItemA)), ItemParseStatus.KindUnresolved);
    }

    [Fact]
    public void PetBagEntryWhoseKindTheResolverDoesNotKnowRejectsTheWholePacket()
    {
        ItemSpec known = Item(0, ItemA);
        ItemSpec unknown = Item(1, ItemB);
        DictionaryKindResolver resolver = new DictionaryKindResolver().With(ItemA, ResolvedItemKind.Other);
        AssertRejected(PetSpacePacket(known, unknown), ItemParseStatus.KindUnresolved, resolver);
    }

    // ---- 空间号与头部 ----

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(0x0B)]
    [InlineData(0x0D)]
    [InlineData(0x13)]
    [InlineData(0xFF)]
    public void UnknownSpacesAreRejectedAsUnsupportedWhateverFollows(int space)
    {
        byte[] payload = new NoticePayloadBuilder().U8(space).U16(0).U16(0).Raw(new byte[40]).ToArray();
        AssertRejected(payload, ItemParseStatus.UnsupportedSpace);
    }

    [Fact]
    public void BodyEquipSpaceIsNotASpacePacketSpace()
    {
        // 空间 3(身上装备)只在增量包出现,物品空间包没有写入者。
        AssertRejected(new NoticePayloadBuilder().U8(3).U16(0).U16(0).ToArray(), ItemParseStatus.UnsupportedSpace);
    }

    [Fact]
    public void NullAndEmptyPayloadsAreMalformedWithoutException()
    {
        AssertRejected(null, ItemParseStatus.Malformed);
        AssertRejected(new byte[0], ItemParseStatus.Malformed);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(0, 4)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(7, 2)]
    [InlineData(0x0C, 3)]
    [InlineData(0x0C, 6)]
    [InlineData(0x0C, 8)]
    public void HeaderCutShortIsMalformed(int space, int length)
    {
        byte[] header = space switch
        {
            0 => new NoticePayloadBuilder().U8(0).U16(8).U16(0).ToArray(),
            1 => new NoticePayloadBuilder().U8(1).U16(0).ToArray(),
            2 => new NoticePayloadBuilder().U8(2).U16(8).U16(0).ToArray(),
            7 => new NoticePayloadBuilder().U8(7).U16(0).ToArray(),
            _ => new NoticePayloadBuilder().U8(0x0C).U16(1).U32(2).U16(0).ToArray(),
        };
        // 头完整时是合法的空包;截短一个字节以上就读不全。
        Assert.True(ItemListParser.TryParse(header, out _));
        AssertRejected(header.Take(length).ToArray(), ItemParseStatus.Malformed);
    }

    // ---- 防谎报与截断 ----

    [Fact]
    public void CountThatCannotFitInTheRemainingBytesIsRejectedBeforeAnyEntryIsRead()
    {
        byte[] payload = new NoticePayloadBuilder().U8(0).U16(8).U16(0xFFFF).Raw(new byte[200]).ToArray();
        AssertRejected(payload, ItemParseStatus.Malformed);
    }

    [Fact]
    public void CountTimesMinimumEntryLengthDecidesTheBoundaryExactly()
    {
        // 个数 2,剩余恰好 50 字节(两个最小条目):通过;少 1 字节:拒绝。
        byte[] fits = new NoticePayloadBuilder().U8(2).U16(8).U16(2)
            .AppendBagEntry(Item(0)).AppendBagEntry(Item(1)).ToArray();
        Assert.Equal(5 + 50, fits.Length);
        ParseOk(fits);
        AssertRejected(fits.Take(fits.Length - 1).ToArray(), ItemParseStatus.Malformed);
    }

    [Fact]
    public void AvatarBagCountIsCheckedAgainstTheSixtySevenByteMinimum()
    {
        // 个数 2,但只有 100 字节(两个 67 字节条目要 134):整包拒绝。
        byte[] payload = new NoticePayloadBuilder().U8(1).U16(2).Raw(new byte[100]).ToArray();
        AssertRejected(payload, ItemParseStatus.Malformed);
    }

    [Fact]
    public void EveryTruncationOfARichBagPacketIsMalformedWithoutException()
    {
        ItemSpec rich = Item(4, ItemB);
        rich.C3 = new[] { (1L, 2, 3, 4, 5) };
        rich.RoFields = new[] { (1, 2, 3) };
        rich.RoSelector = 1;
        rich.RoTailMarker = 0x05;
        rich.RoTail = new byte[] { 9, 9, 9, 9 };
        byte[] full = BagSpacePacket(8, 1, 2, 3, Item(3), rich);
        ParseOk(full);
        for (int length = 0; length < full.Length; length++)
        {
            Assert.False(ItemListParser.TryParse(full.Take(length).ToArray(), out ItemListNotice n, out ItemParseStatus status));
            Assert.Null(n);
            Assert.Equal(ItemParseStatus.Malformed, status);
        }
    }

    [Fact]
    public void RandomOptionCountAboveThreeInsideAPacketRejectsTheWholePacket()
    {
        byte[] payload = new NoticePayloadBuilder().U8(2).U16(8).U16(1)
            .U16(0).U32(ItemA).U32(0).U8(0).U16(0).U8(0).U32(0).U8(0).U16(0)
            .U8(0).U8(4).Raw(new byte[64]).ToArray();
        AssertRejected(payload, ItemParseStatus.Malformed);
    }

    // ---- 尾部:直连帧的 4 字节包尾与补零只计数,不当条目 ----

    [Fact]
    public void TrailingBytesAreOnlyCountedNeverParsedAsEntries()
    {
        // 头声明 3 个条目;后面跟着两个"看上去像条目"的 25 字节块,绝不能被读成第 4、5 个条目。
        byte[] body = BagSpacePacket(8, 1, 2, 3);
        byte[] extra = BagEntryBytes(Item(9, ItemA)).Concat(BagEntryBytes(Item(10, ItemB))).ToArray();
        ItemListNotice n = ParseOk(body.Concat(extra).ToArray());
        Assert.Empty(n.Entries);
        Assert.Equal(3, n.DeclaredEntryCount);
        Assert.Equal(body.Length, n.ConsumedByteCount);
        Assert.Equal(50, n.TrailingByteCount);
    }

    [Fact]
    public void AFourByteTailPlusZeroPaddingIsToleratedAndCounted()
    {
        byte[] body = BagSpacePacket(8, 1, 2, 3, Item(3), Item(4));
        byte[] payload = DirectFramePayload(body, 13);
        ItemListNotice n = ParseOk(payload);
        Assert.Equal(2, n.Entries.Count);
        Assert.Equal(body.Length, n.ConsumedByteCount);
        Assert.Equal(payload.Length - body.Length, n.TrailingByteCount);
        Assert.True(n.TrailingByteCount >= 4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(23)]
    public void ConsumedBytesPlusFourPlusPaddingEqualTheDirectFramePayloadLength(int items)
    {
        // 直连帧载荷 = 包体 + 4 字节 PI 尾 + 零填充到槽块长(槽 = 包号 % 14;块长表来自 CipherSlots)。
        ItemSpec[] list = Enumerable.Range(0, items).Select(i => Item(3 + i)).ToArray();
        byte[] body = BagSpacePacket(8, 1, 2, 3, list);
        byte[] payload = DirectFramePayload(body, 13);
        int block = CipherSlots.BlockSizes[CipherSlots.SlotForPacket(13)];
        Assert.Equal(16, block);
        ItemListNotice n = ParseOk(payload);
        int padding = payload.Length - (n.ConsumedByteCount + 4);
        Assert.True(padding >= 0 && padding < block);
        Assert.Equal(payload.Length, n.ConsumedByteCount + 4 + padding);
        Assert.Equal(0, payload.Length % block);
        Assert.Equal(n.TrailingByteCount, 4 + padding);
        // 补零段确实全是零。
        Assert.All(payload.Skip(n.ConsumedByteCount + 4), v => Assert.Equal(0, v));
    }

    [Fact]
    public void InnerFramePayloadHasOnlyTheFourByteTailAndNoPadding()
    {
        byte[] body = BagSpacePacket(8, 1, 2, 3, Item(3));
        ItemListNotice n = ParseOk(InnerFramePayload(body));
        Assert.Equal(4, n.TrailingByteCount);
        Assert.Equal(body.Length, n.ConsumedByteCount);
    }

    // ---- 其它 ----

    [Fact]
    public void NoticeListIsAReadOnlyView()
    {
        ItemListNotice n = ParseOk(BagSpacePacket(8, 1, 2, 3, Item(3)));
        Assert.True(((System.Collections.IList)n.Entries).IsReadOnly);
    }

    [Fact]
    public void TheThreeOverloadsAgreeOnASuccessfulParse()
    {
        byte[] payload = BagSpacePacket(16, 10, 20, 30, Item(7));
        Assert.True(ItemListParser.TryParse(payload, out ItemListNotice a));
        Assert.True(ItemListParser.TryParse(payload, out ItemListNotice b, out ItemParseStatus sb));
        Assert.True(ItemListParser.TryParse(payload, null, out ItemListNotice c, out ItemParseStatus sc));
        Assert.Equal(ItemParseStatus.Ok, sb);
        Assert.Equal(ItemParseStatus.Ok, sc);
        Assert.Equal(a.Entries[0].Slot, b.Entries[0].Slot);
        Assert.Equal(a.Capacity, c.Capacity);
    }

    [Fact]
    public void PacketIdConstantIsThirteen()
    {
        Assert.Equal(13, ItemListParser.PacketId);
    }

    [Fact]
    public void DeterministicGarbageNeverThrowsAndConsistentSuccessesAccountForEveryByte()
    {
        // 固定种子的伪随机字节(合成,与任何真实抓包无关):任何输入都不抛异常;成功时 消耗 + 剩余 = 载荷长度。
        uint state = 0x12345678;
        for (int round = 0; round < 3000; round++)
        {
            state = state * 1664525u + 1013904223u;
            int length = (int)((state >> 16) % 160);
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                state = state * 1664525u + 1013904223u;
                data[i] = (byte)(state >> 24);
            }
            if (length > 0)
            {
                // 让空间号落在合法集合里的概率高一些,否则几乎全是 UnsupportedSpace。
                data[0] = (byte)(new[] { 0, 1, 2, 7, 0x0C }[data[0] % 5]);
            }
            if (ItemListParser.TryParse(data, out ItemListNotice notice, out ItemParseStatus status))
            {
                Assert.Equal(ItemParseStatus.Ok, status);
                Assert.Equal(data.Length, notice.ConsumedByteCount + notice.TrailingByteCount);
            }
            else
            {
                Assert.Null(notice);
                Assert.NotEqual(ItemParseStatus.Ok, status);
            }
        }
    }
}
