using Dnf.Net.Session;
using static Dnf.Net.Tests.ItemWire;

namespace Dnf.Net.Tests;

/// <summary>
/// 物品增量包(类 0、包号 14)解析器测试:按槽 upsert、删除标记 0xFFFFFFFF、一包多槽、空间 0 的货币槽刷新、空间 3 身上装备分支。
/// 夹具全是手工拼的合成字节,合成物品号 900001 起,不含任何真实物品号、名字或数值。
/// </summary>
public class ItemUpdateParserTests
{
    private static ItemUpdateNotice ParseOk(byte[] payload, IItemKindResolver resolver = null)
    {
        Assert.True(ItemUpdateParser.TryParse(payload, resolver, out ItemUpdateNotice notice, out ItemParseStatus status), "拒绝原因 " + status);
        Assert.Equal(ItemParseStatus.Ok, status);
        Assert.NotNull(notice);
        return notice;
    }

    private static void AssertRejected(byte[] payload, ItemParseStatus expected, IItemKindResolver resolver = null)
    {
        Assert.False(ItemUpdateParser.TryParse(payload, resolver, out ItemUpdateNotice notice, out ItemParseStatus status));
        Assert.Null(notice);
        Assert.Equal(expected, status);
    }

    // ---- 背包增量:upsert / 删除 / 多槽 ----

    [Fact]
    public void SingleSlotUpsertHasThreeByteHeaderAndOneEntry()
    {
        ItemSpec s = Item(9, ItemA);
        s.Data = 5;
        byte[] payload = UpdatePacket(0, s);
        Assert.Equal(3 + 25, payload.Length);
        Assert.Equal(0, payload[0]);
        Assert.Equal(1, payload[1]);
        Assert.Equal(0, payload[2]);
        ItemUpdateNotice n = ParseOk(payload);
        Assert.Equal(ItemSpace.Bag, n.Space);
        Assert.Equal(1, n.DeclaredEntryCount);
        ItemUpdateOperation op = Assert.Single(n.Operations);
        Assert.False(op.IsDelete);
        Assert.Equal(9, op.Slot);
        Assert.Equal(900001u, op.Entry.ItemId);
        Assert.Equal(5u, op.Entry.Data);
        Assert.True(n.Currency.IsEmpty);
        Assert.False(n.IsEmpty);
        Assert.Equal(payload.Length, n.ConsumedByteCount);
    }

    [Fact]
    public void MultiSlotUpdateKeepsTheWireOrder()
    {
        byte[] payload = UpdatePacket(0, Item(40, ItemA), Item(12, ItemB), Item(300, ItemC));
        ItemUpdateNotice n = ParseOk(payload);
        Assert.Equal(3, n.Operations.Count);
        Assert.Equal(new[] { 40, 12, 300 }, n.Operations.Select(o => (int)o.Slot).ToArray());
        Assert.All(n.Operations, o => Assert.False(o.IsDelete));
    }

    [Fact]
    public void ItemIdOfAllOnesClearsTheSlot()
    {
        byte[] payload = UpdatePacket(0, Item(7, DeleteMarker));
        ItemUpdateNotice n = ParseOk(payload);
        ItemUpdateOperation op = Assert.Single(n.Operations);
        Assert.True(op.IsDelete);
        Assert.Equal(7, op.Slot);
        Assert.Null(op.Entry);
    }

    [Fact]
    public void ItemIdJustBelowTheMarkerIsAnOrdinaryUpsert()
    {
        ItemUpdateNotice n = ParseOk(UpdatePacket(0, Item(7, DeleteMarker - 1)));
        ItemUpdateOperation op = Assert.Single(n.Operations);
        Assert.False(op.IsDelete);
        Assert.Equal(0xFFFFFFFEu, op.Entry.ItemId);
    }

    [Fact]
    public void MixedUpsertAndDeleteInOnePacketAreBothReported()
    {
        ItemUpdateNotice n = ParseOk(UpdatePacket(0, Item(5, ItemA), Item(6, DeleteMarker), Item(8, ItemB)));
        Assert.Equal(3, n.Operations.Count);
        Assert.False(n.Operations[0].IsDelete);
        Assert.True(n.Operations[1].IsDelete);
        Assert.False(n.Operations[2].IsDelete);
        Assert.Equal(6, n.Operations[1].Slot);
    }

    [Fact]
    public void UpsertsAndDeletesSplitTheOperationsAndKeepTheirOwnWireOrder()
    {
        ItemUpdateNotice n = ParseOk(UpdatePacket(0, Item(9, ItemA), Item(6, DeleteMarker), Item(5, ItemB), Item(12, DeleteMarker), Item(7, ItemC)));
        Assert.Equal(5, n.Operations.Count);
        Assert.Equal(3, n.Upserts.Count);
        Assert.Equal(new ushort[] { 9, 5, 7 }, n.Upserts.Select(o => o.Slot).ToArray());
        Assert.Equal(new uint[] { 900001u, 900002u, 900003u }, n.Upserts.Select(o => o.Entry.ItemId).ToArray());
        Assert.All(n.Upserts, o => Assert.False(o.IsDelete));
        Assert.Equal(2, n.Deletes.Count);
        Assert.Equal(new ushort[] { 6, 12 }, n.Deletes.ToArray());
    }

    [Fact]
    public void ADeleteOnlyPacketHasDeletesButNoUpsertsAndAnUpsertOnlyPacketTheOpposite()
    {
        ItemUpdateNotice deleteOnly = ParseOk(UpdatePacket(0, Item(4, DeleteMarker), Item(4, DeleteMarker)));
        Assert.Empty(deleteOnly.Upserts);
        Assert.Equal(new ushort[] { 4, 4 }, deleteOnly.Deletes.ToArray());

        ItemUpdateNotice upsertOnly = ParseOk(UpdatePacket(0, Item(4, ItemA)));
        Assert.Single(upsertOnly.Upserts);
        Assert.Empty(upsertOnly.Deletes);
    }

    [Fact]
    public void ACurrencyOnlyOrEmptyPacketHasNeitherUpsertsNorDeletes()
    {
        ItemUpdateNotice currencyOnly = ParseOk(UpdatePacket(0, Pseudo(1, 777)));
        Assert.Empty(currencyOnly.Upserts);
        Assert.Empty(currencyOnly.Deletes);

        ItemUpdateNotice empty = ParseOk(UpdatePacket(0));
        Assert.Empty(empty.Upserts);
        Assert.Empty(empty.Deletes);
    }

    [Fact]
    public void UpsertAndDeleteViewsAreReadOnly()
    {
        ItemUpdateNotice n = ParseOk(UpdatePacket(0, Item(5, ItemA), Item(6, DeleteMarker)));
        Assert.True(((System.Collections.IList)n.Upserts).IsReadOnly);
        Assert.True(((System.Collections.IList)n.Deletes).IsReadOnly);
    }

    [Fact]
    public void TheSameSlotTwiceInOnePacketKeepsTheWireOrderSoTheLaterOneWinsWhenApplied()
    {
        ItemUpdateNotice a = ParseOk(UpdatePacket(0, Item(5, ItemA), Item(5, DeleteMarker)));
        Assert.False(a.Operations[0].IsDelete);
        Assert.True(a.Operations[1].IsDelete);
        ItemUpdateNotice b = ParseOk(UpdatePacket(0, Item(5, DeleteMarker), Item(5, ItemB)));
        Assert.True(b.Operations[0].IsDelete);
        Assert.False(b.Operations[1].IsDelete);
        Assert.Equal(900002u, b.Operations[1].Entry.ItemId);
    }

    [Fact]
    public void ZeroCountPacketIsAcceptedAndEmpty()
    {
        ItemUpdateNotice n = ParseOk(new byte[] { 0x00, 0x00, 0x00 });
        Assert.Empty(n.Operations);
        Assert.True(n.IsEmpty);
        Assert.Equal(3, n.ConsumedByteCount);
    }

    // ---- 空间 0 的货币槽刷新 ----

    [Fact]
    public void SlotOneRefreshInTheBagIsACurrencyRefreshNotABagCell()
    {
        // 与进城流里的那一帧同形:空间 0、个数 1、槽 1(物品号 = 槽号)的 25 字节条目;整包 3 + 25,加 4 字节 PI 尾 = 32。
        byte[] body = UpdatePacket(0, Pseudo(1, 4242));
        Assert.Equal(28, body.Length);
        byte[] payload = InnerFramePayload(body);
        Assert.Equal(32, payload.Length);
        ItemUpdateNotice n = ParseOk(payload);
        Assert.Empty(n.Operations);
        Assert.Equal(4242u, n.Currency.CoinTotal);
        Assert.Null(n.Currency.Gold);
        Assert.Null(n.Currency.WinPoint);
        Assert.Equal(4, n.TrailingByteCount);
        Assert.False(n.IsEmpty);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EachCurrencySlotRefreshLandsInItsOwnCurrencyField(int slot)
    {
        ItemUpdateNotice n = ParseOk(UpdatePacket(0, Pseudo(slot, 900 + slot)));
        Assert.Empty(n.Operations);
        Assert.Equal(slot == 0 ? 900u : (uint?)null, n.Currency.Gold);
        Assert.Equal(slot == 1 ? 901u : (uint?)null, n.Currency.CoinTotal);
        Assert.Equal(slot == 2 ? 902u : (uint?)null, n.Currency.WinPoint);
    }

    [Fact]
    public void CurrencySlotsAndBagCellsCanShareOnePacket()
    {
        ItemUpdateNotice n = ParseOk(UpdatePacket(0, Pseudo(0, 10), Item(5, ItemA), Pseudo(2, 30), Item(6, DeleteMarker)));
        Assert.Equal(2, n.Operations.Count);
        Assert.Equal(10u, n.Currency.Gold);
        Assert.Equal(30u, n.Currency.WinPoint);
        Assert.Null(n.Currency.CoinTotal);
        Assert.Equal(4, n.DeclaredEntryCount);
    }

    [Fact]
    public void TheSameCurrencySlotTwiceKeepsTheLaterAmount()
    {
        ItemUpdateNotice n = ParseOk(UpdatePacket(0, Pseudo(1, 100), Pseudo(1, 250)));
        Assert.Equal(250u, n.Currency.CoinTotal);
    }

    [Fact]
    public void CurrencySlotWithAForeignItemIdIsRejected()
    {
        ItemSpec odd = Pseudo(1, 5);
        odd.ItemId = ItemA;
        AssertRejected(UpdatePacket(0, odd), ItemParseStatus.Malformed);
    }

    [Fact]
    public void ADeleteMarkerInACurrencySlotIsRejectedBecauseTheServerNeverWritesIt()
    {
        AssertRejected(UpdatePacket(0, Item(1, DeleteMarker)), ItemParseStatus.Malformed);
        AssertRejected(UpdatePacket(0, Item(0, DeleteMarker)), ItemParseStatus.Malformed);
    }

    [Fact]
    public void SlotsZeroToTwoInTheCargoAreOrdinarySlots()
    {
        ItemUpdateNotice n = ParseOk(UpdatePacket(2, Item(0, ItemA), Item(1, ItemB), Item(2, DeleteMarker)));
        Assert.Equal(ItemSpace.Cargo, n.Space);
        Assert.Equal(3, n.Operations.Count);
        Assert.True(n.Currency.IsEmpty);
        Assert.True(n.Operations[2].IsDelete);
    }

    // ---- 其它空间的形状 ----

    [Fact]
    public void AvatarBagUpdateReadsTheTwoFixedBlocks()
    {
        ItemSpec s = Item(3, ItemA);
        s.Data = 14;
        s.SocketBlock = Socket();
        s.ExpansionBlock = Expansion();
        ItemSpec t = Item(4, ItemB);
        t.SocketBlock = Socket(0x11);
        t.ExpansionBlock = Expansion(0x22);
        byte[] payload = UpdatePacket(1, s, t);
        Assert.Equal(3 + 67 * 2, payload.Length);
        ItemUpdateNotice n = ParseOk(payload);
        Assert.Equal(ItemSpace.AvatarBag, n.Space);
        Assert.Equal(2, n.Operations.Count);
        Assert.True(n.Operations[0].Entry.HasAvatarBlocks);
        Assert.True(n.Operations[0].Entry.DataIsRemainingTime);
        Assert.Equal(Socket(0x11), n.Operations[1].Entry.SocketBlock.ToArray());
    }

    [Fact]
    public void AvatarBagUpdateWithAnInlineC3ItemIsAccepted()
    {
        ItemSpec s = Item(3, ItemA);
        s.SocketBlock = Socket();
        s.ExpansionBlock = Expansion();
        s.C3 = new[] { (9L, 1, 2, 3, 4) };
        ItemUpdateNotice n = ParseOk(UpdatePacket(1, s));
        Assert.Single(n.Operations[0].Entry.C3Records);
    }

    [Fact]
    public void AccountCargoUpdateUsesTheBagShape()
    {
        ItemUpdateNotice n = ParseOk(UpdatePacket(0x0C, Item(2, ItemA)));
        Assert.Equal(ItemSpace.AccountCargo, n.Space);
        Assert.Single(n.Operations);
    }

    // ---- 宠物栏(空间 7) ----

    [Fact]
    public void PetBagUpdateWithAResolverReadsThePetU32()
    {
        ItemSpec pet = Item(1, ItemC);
        pet.PetTime = 120;
        DictionaryKindResolver resolver = new DictionaryKindResolver().With(ItemC, ResolvedItemKind.Pet);
        ItemUpdateNotice n = ParseOk(UpdatePacket(7, pet), resolver);
        Assert.Equal(ItemSpace.PetBag, n.Space);
        Assert.True(n.Operations[0].Entry.HasPetRemainTime);
        Assert.Equal(120u, n.Operations[0].Entry.PetRemainTime);
    }

    [Fact]
    public void PetBagUpdateWithoutAResolverIsRejectedAsKindUnresolved()
    {
        AssertRejected(UpdatePacket(7, Item(1, ItemC)), ItemParseStatus.KindUnresolved);
    }

    [Fact]
    public void PetBagUpdateWithZeroEntriesAndDeleteOnlyPacketsNeedNoResolver()
    {
        ParseOk(UpdatePacket(7));
        ItemUpdateNotice n = ParseOk(UpdatePacket(7, Item(4, DeleteMarker), Item(5, DeleteMarker)));
        Assert.Equal(2, n.Operations.Count);
        Assert.All(n.Operations, o => Assert.True(o.IsDelete));
    }

    // ---- 身上装备(空间 3) ----

    [Fact]
    public void BodyEquipUpdateOfAnAvatarItemReadsBlocksWhenTheResolverSaysAvatar()
    {
        ItemSpec s = Item(2, ItemB);
        s.SocketBlock = Socket();
        s.ExpansionBlock = Expansion();
        ItemUpdateNotice n = ParseOk(UpdatePacket(3, s), new DictionaryKindResolver().With(ItemB, ResolvedItemKind.Avatar));
        Assert.Equal(ItemSpace.BodyEquip, n.Space);
        ItemUpdateOperation op = Assert.Single(n.Operations);
        Assert.Equal(2, op.Slot);
        Assert.True(op.Entry.HasAvatarBlocks);
        Assert.True(op.Entry.HasSealedByte);
    }

    [Fact]
    public void BodyEquipUpdateOfAnOrdinaryItemHasNoOptionalBlocks()
    {
        byte[] payload = UpdatePacket(3, Item(11, ItemA));
        Assert.Equal(3 + 25, payload.Length);
        ItemUpdateNotice n = ParseOk(payload, new DictionaryKindResolver().With(ItemA, ResolvedItemKind.Other));
        Assert.False(n.Operations[0].Entry.HasAvatarBlocks);
        Assert.False(n.Operations[0].Entry.HasPetRemainTime);
    }

    [Fact]
    public void BodyEquipUpdateOfThePetSlotReadsThePetU32WhenTheResolverSaysPet()
    {
        ItemSpec pet = Item(22, ItemC);
        pet.PetTime = 9999;
        ItemUpdateNotice n = ParseOk(UpdatePacket(3, pet), new DictionaryKindResolver().With(ItemC, ResolvedItemKind.Pet));
        Assert.Equal(22, n.Operations[0].Slot);
        Assert.Equal(9999u, n.Operations[0].Entry.PetRemainTime);
    }

    [Fact]
    public void BodyEquipClearSlotNeedsNoResolver()
    {
        ItemUpdateNotice n = ParseOk(UpdatePacket(3, Item(4, DeleteMarker)));
        ItemUpdateOperation op = Assert.Single(n.Operations);
        Assert.True(op.IsDelete);
        Assert.Equal(4, op.Slot);
    }

    [Fact]
    public void BodyEquipUpsertWithoutAResolverIsRejectedAsKindUnresolved()
    {
        AssertRejected(UpdatePacket(3, Item(4, ItemA)), ItemParseStatus.KindUnresolved);
    }

    [Fact]
    public void BodyEquipUpdateWithZeroEntriesNeedsNoResolver()
    {
        ParseOk(UpdatePacket(3));
    }

    // ---- 拒绝 ----

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(0x0B)]
    [InlineData(0x0D)]
    [InlineData(0x13)]
    [InlineData(0xFF)]
    public void UnknownSpacesAreRejectedAsUnsupported(int space)
    {
        byte[] payload = new NoticePayloadBuilder().U8(space).U16(0).Raw(new byte[40]).ToArray();
        AssertRejected(payload, ItemParseStatus.UnsupportedSpace);
    }

    [Fact]
    public void NullEmptyAndHeaderOnlyPayloadsAreMalformed()
    {
        AssertRejected(null, ItemParseStatus.Malformed);
        AssertRejected(new byte[0], ItemParseStatus.Malformed);
        AssertRejected(new byte[] { 0x00 }, ItemParseStatus.Malformed);
        AssertRejected(new byte[] { 0x00, 0x01 }, ItemParseStatus.Malformed);
    }

    [Fact]
    public void CountThatCannotFitIsRejectedBeforeAnyEntryIsRead()
    {
        byte[] payload = new NoticePayloadBuilder().U8(0).U16(0xFFFF).Raw(new byte[300]).ToArray();
        AssertRejected(payload, ItemParseStatus.Malformed);
    }

    [Fact]
    public void CountTimesMinimumEntryLengthDecidesTheBoundaryExactly()
    {
        byte[] fits = UpdatePacket(0, Item(5), Item(6));
        Assert.Equal(3 + 50, fits.Length);
        ParseOk(fits);
        AssertRejected(fits.Take(fits.Length - 1).ToArray(), ItemParseStatus.Malformed);
    }

    [Fact]
    public void EveryTruncationOfARichUpdateIsMalformedWithoutException()
    {
        ItemSpec rich = Item(4, ItemB);
        rich.C3 = new[] { (1L, 2, 3, 4, 5) };
        rich.RoFields = new[] { (1, 2, 3), (4, 5, 6) };
        rich.RoSelector = 1;
        rich.RoTailMarker = 0x07;
        rich.RoTail = new byte[] { 8, 8, 8, 8 };
        byte[] full = UpdatePacket(0, Pseudo(1, 3), rich, Item(9, DeleteMarker));
        ParseOk(full);
        for (int length = 0; length < full.Length; length++)
        {
            Assert.False(ItemUpdateParser.TryParse(full.Take(length).ToArray(), out ItemUpdateNotice n, out ItemParseStatus status));
            Assert.Null(n);
            Assert.Equal(ItemParseStatus.Malformed, status);
        }
    }

    [Fact]
    public void AnEntryWithAnIllegalBlockLengthRejectsTheWholeUpdate()
    {
        ItemSpec s = Item(2, ItemB);
        s.SocketBlock = Socket();
        s.ExpansionBlock = Expansion();
        byte[] bad = UpdatePacket(3, s);
        // 偏移 3 是条目起点,条目内偏移 21 是镶嵌块长度:改成 31。
        bad[3 + 21] = 31;
        AssertRejected(bad, ItemParseStatus.Malformed, new DictionaryKindResolver().With(ItemB, ResolvedItemKind.Avatar));
    }

    // ---- 尾部 ----

    [Fact]
    public void TrailingBytesAreOnlyCountedNeverParsedAsMoreEntries()
    {
        byte[] body = UpdatePacket(0, Item(5, ItemA));
        byte[] extra = BagEntryBytes(Item(6, ItemB));
        ItemUpdateNotice n = ParseOk(body.Concat(extra).ToArray());
        Assert.Single(n.Operations);
        Assert.Equal(body.Length, n.ConsumedByteCount);
        Assert.Equal(25, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void ConsumedBytesPlusFourPlusPaddingEqualTheDirectFramePayloadLength(int items)
    {
        byte[] body = UpdatePacket(0, Enumerable.Range(0, items).Select(i => Item(3 + i)).ToArray());
        byte[] payload = DirectFramePayload(body, 14);
        int block = Dnf.Net.Crypto.CipherSlots.BlockSizes[Dnf.Net.Crypto.CipherSlots.SlotForPacket(14)];
        Assert.Equal(4, block);
        ItemUpdateNotice n = ParseOk(payload);
        int padding = payload.Length - (n.ConsumedByteCount + 4);
        Assert.True(padding >= 0 && padding < block);
        Assert.Equal(n.TrailingByteCount, 4 + padding);
        Assert.Equal(payload.Length, n.ConsumedByteCount + 4 + padding);
    }

    // ---- 其它 ----

    [Fact]
    public void OperationListIsAReadOnlyView()
    {
        ItemUpdateNotice n = ParseOk(UpdatePacket(0, Item(5, ItemA)));
        Assert.True(((System.Collections.IList)n.Operations).IsReadOnly);
    }

    [Fact]
    public void PacketIdConstantIsFourteen()
    {
        Assert.Equal(14, ItemUpdateParser.PacketId);
    }

    [Fact]
    public void TheShortOverloadsAgreeWithTheFullOne()
    {
        byte[] payload = UpdatePacket(0, Item(5, ItemA));
        Assert.True(ItemUpdateParser.TryParse(payload, out ItemUpdateNotice a));
        Assert.True(ItemUpdateParser.TryParse(payload, out ItemUpdateNotice b, out ItemParseStatus s));
        Assert.Equal(ItemParseStatus.Ok, s);
        Assert.Equal(a.Operations[0].Slot, b.Operations[0].Slot);
    }

    [Fact]
    public void DeterministicGarbageNeverThrowsAndConsistentSuccessesAccountForEveryByte()
    {
        uint state = 0x9E3779B9;
        DictionaryKindResolver resolver = new DictionaryKindResolver();
        for (int round = 0; round < 3000; round++)
        {
            state = state * 1664525u + 1013904223u;
            int length = (int)((state >> 16) % 180);
            byte[] data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                state = state * 1664525u + 1013904223u;
                data[i] = (byte)(state >> 24);
            }
            if (length > 0)
            {
                data[0] = (byte)(new[] { 0, 1, 2, 3, 7, 0x0C }[data[0] % 6]);
            }
            if (ItemUpdateParser.TryParse(data, resolver, out ItemUpdateNotice notice, out ItemParseStatus status))
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
