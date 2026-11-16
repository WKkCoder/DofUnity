using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.ItemWire;

namespace Dnf.Net.Tests;

/// <summary>
/// 物品数据聚合解析器测试(<see cref="ItemSyncParser"/>):整份状态覆盖、增量逐槽补丁(不是"后到者覆盖整个空间")、货币单独存放、
/// 未知帧跳过、错位计数、物品类型回调注入。每一帧都是手工拼的合成载荷(正文 + 4 字节 PI 尾 + 补零到块长,即直连帧载荷的形态),
/// 再走真实的 <see cref="ServerFrameDecoder"/> 解码成 <see cref="DecodedFrame"/>;物品号 900001 起,唯一号 9001 起,不含任何真实数据。
/// </summary>
public class ItemSyncParserTests
{
    // 包号在这里写成字面量,不引用产品常量:产品常量被改动时,测试要能发现。
    private const int SpaceId = 13;
    private const int UpdateId = 14;
    private const int CeraId = 53;
    private const int PetTableId = 105;
    private const int EnterCompleteId = 124;
    private const int CoolTimeId = 172;
    private const int EffectId = 174;
    private const int LockId = 251;
    private const int ExpandId = 339;
    private const int MaxLevelId = 438;
    private const int HotkeyId = 452;
    private const int CoinId = 587;

    private static DecodedFrame Noti(int id, byte[] body)
    {
        return FrameOf(ClassNoti, id, DirectFramePayload(body, id));
    }

    private static DecodedFrame Cmd(int id, byte[] body)
    {
        return FrameOf(ClassCmd, id, DirectFramePayload(body, id));
    }

    private static ItemSyncSnapshot Parse(params DecodedFrame[] frames)
    {
        return ItemSyncParser.Parse(frames);
    }

    private static ItemSyncSnapshot ParseWith(IItemKindResolver resolver, params DecodedFrame[] frames)
    {
        return ItemSyncParser.Parse(frames, resolver);
    }

    private static DictionaryKindResolver Resolver()
    {
        return new DictionaryKindResolver();
    }

    private static ItemSpec AvatarItem(int slot, long itemId)
    {
        ItemSpec spec = Item(slot, itemId);
        spec.SocketBlock = Socket();
        spec.ExpansionBlock = Expansion();
        return spec;
    }

    private static int[] Slots(ItemSpaceState state)
    {
        return state.Entries.Select(e => (int)e.Slot).ToArray();
    }

    private static uint[] Ids(ItemSpaceState state)
    {
        return state.Entries.Select(e => e.ItemId).ToArray();
    }

    // 各个小包的最小合成正文
    private static readonly byte[] CeraBody = { 1, 0x10, 0, 0, 0, 0x20, 0, 0, 0, 0x30, 0, 0, 0 };
    private static readonly byte[] CoinBody = { 1, 0x78, 0x56, 0x34, 0x12 };
    private static readonly byte[] OnePair = { 1, 0x11, 0, 0, 0, 0x22, 0, 0, 0 };
    private static readonly byte[] TwoPairs = { 2, 0x11, 0, 0, 0, 0x22, 0, 0, 0, 0x33, 0, 0, 0, 0x44, 0, 0, 0 };
    private static readonly byte[] EmptyLockTable = { 0, 0 };
    private static readonly byte[] MaxLevelBody = { 0x34, 0x12 };

    private static byte[] HotkeyBody()
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(3).U32(0xBC);
        for (int i = 0; i < 94; i++)
        {
            b.U16(i + 1);
        }
        return b.ToArray();
    }

    private static byte[] ExpandBody(int uid)
    {
        return new NoticePayloadBuilder().U16(uid).U8(10).AppendDetailList().U8(11).AppendDetailList().U8(12).AppendDetailList().ToArray();
    }

    private static byte[] OnePetTable(long uid)
    {
        return new NoticePayloadBuilder().U8(1).U32(uid).U8(80).U8(1).U32(500).U8(7).U32(2).Raw(0x41, 0x42).U8(0).ToArray();
    }

    private static readonly byte[] Name4 = { 0x41, 0x42, 0x43, 0x44 };

    // ---- 空输入与未知帧 ----

    [Fact]
    public void NullAndEmptyFrameListsGiveAnEmptySnapshot()
    {
        foreach (ItemSyncSnapshot s in new[] { ItemSyncParser.Parse(null), ItemSyncParser.Parse(new DecodedFrame[0]) })
        {
            Assert.Equal(0, s.FramesSeen);
            Assert.Equal(0, s.FramesParsed);
            Assert.Equal(0, s.FramesMalformed);
            Assert.Equal(0, s.FramesKindUnresolved);
            Assert.Null(s.Bag);
            Assert.Null(s.AvatarBag);
            Assert.Null(s.Cargo);
            Assert.Null(s.PetBag);
            Assert.Null(s.AccountCargo);
            Assert.Null(s.BodyEquip);
            Assert.Null(s.Cera);
            Assert.Null(s.AvatarCoin);
            Assert.Null(s.CoolTimeItems);
            Assert.Null(s.EffectItems);
            Assert.Null(s.ItemLocks);
            Assert.Null(s.MaxEquipLevel);
            Assert.Null(s.HotkeyOption);
            Assert.Null(s.SelfView);
            Assert.Null(s.Creatures);
            Assert.Empty(s.PublicViews);
            Assert.Empty(s.ExpandEquips);
            Assert.NotNull(s.Currency);
            Assert.True(s.Currency.IsEmpty);
        }
    }

    [Fact]
    public void NullFrameEntriesAreIgnoredAndNotCounted()
    {
        ItemSyncSnapshot s = Parse(null, Noti(CeraId, CeraBody), null);
        Assert.Equal(1, s.FramesSeen);
        Assert.Equal(1, s.FramesParsed);
        Assert.NotNull(s.Cera);
    }

    [Fact]
    public void UnknownFramesAreSkippedAndOnlyCountedAsSeen()
    {
        ItemSyncSnapshot s = Parse(
            Noti(EnterCompleteId, new byte[0]),
            Noti(561, new byte[] { 1, 2, 3, 4, 5, 6 }),
            Noti(7, new byte[] { 1, 2, 3 }),
            Cmd(4, new byte[] { 1, 3, 3, 3, 3 }));
        Assert.Equal(4, s.FramesSeen);
        Assert.Equal(0, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
        Assert.Null(s.Bag);
        Assert.Null(s.Cera);
    }

    // ---- 类字节既不是 0(通知)也不是 1(命令)的帧 ----
    // PacketHeader.Class 是 (PacketClass)ClassByte,可以是任意字节(服务端接收时不按类拒收,取值原样保留)。
    // 聚合器只认类 0 的通知与类 1 的 587,其余类一律只算"看过"——哪怕包号与载荷都合法,也不能被当成通知解析。

    /// <summary>每个通知类物品包各一份合法载荷(包号、正文);按通知类送进去全都解得通。</summary>
    private static (int Id, byte[] Body)[] LegalNotiBodies()
    {
        return new (int, byte[])[]
        {
            (SpaceId, BagSpacePacket(8, 100, 200, 300, Item(5, ItemA))),
            (UpdateId, UpdatePacket(0, Item(6, ItemB))),
            (CeraId, CeraBody),
            (PetTableId, OnePetTable(9001)),
            (CoolTimeId, OnePair),
            (EffectId, TwoPairs),
            (LockId, EmptyLockTable),
            (ExpandId, ExpandBody(9001)),
            (MaxLevelId, MaxLevelBody),
            (HotkeyId, HotkeyBody()),
        };
    }

    private static DecodedFrame FrameOfClass(byte cls, int id, byte[] body)
    {
        DecodedFrame frame = FrameOf(cls, id, DirectFramePayload(body, id));
        Assert.Equal(cls, frame.Header.ClassByte);
        return frame;
    }

    private static void AssertNothingWasTakenIntoTheSnapshot(ItemSyncSnapshot s)
    {
        Assert.Null(s.Bag);
        Assert.Null(s.AvatarBag);
        Assert.Null(s.Cargo);
        Assert.Null(s.PetBag);
        Assert.Null(s.AccountCargo);
        Assert.Null(s.BodyEquip);
        Assert.Null(s.Cera);
        Assert.Null(s.AvatarCoin);
        Assert.Null(s.CoolTimeItems);
        Assert.Null(s.EffectItems);
        Assert.Null(s.ItemLocks);
        Assert.Null(s.MaxEquipLevel);
        Assert.Null(s.HotkeyOption);
        Assert.Null(s.SelfView);
        Assert.Null(s.Creatures);
        Assert.Empty(s.PublicViews);
        Assert.Empty(s.ExpandEquips);
        Assert.True(s.Currency.IsEmpty);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public void FramesOfAClassThatIsNeitherNotiNorCmdAreOnlyCountedAsSeenWhateverTheirPacketIdAndPayload(byte cls)
    {
        (int Id, byte[] Body)[] legal = LegalNotiBodies();

        // 对照:同样的载荷按通知类(类字节 0)全都解得通——说明下面的"只算看过"不是夹具本身有毛病。
        ItemSyncSnapshot control = Parse(legal.Select(f => FrameOfClass(0, f.Id, f.Body)).ToArray());
        Assert.Equal(legal.Length, control.FramesSeen);
        Assert.Equal(legal.Length, control.FramesParsed);
        Assert.Equal(0, control.FramesMalformed);
        Assert.NotNull(control.Bag);
        Assert.NotNull(control.Cera);

        // 同样的包号与载荷换成未知类,再加一个 587(它只在类 1 才是装扮币应答)。
        DecodedFrame[] frames = legal.Select(f => FrameOfClass(cls, f.Id, f.Body))
            .Append(FrameOfClass(cls, CoinId, CoinBody)).ToArray();
        ItemSyncSnapshot s = Parse(frames);
        Assert.Equal(frames.Length, s.FramesSeen);
        Assert.Equal(0, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
        Assert.Equal(0, s.FramesKindUnresolved);
        AssertNothingWasTakenIntoTheSnapshot(s);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public void AnUnknownClassCeraFrameAndSpaceFrameLeaveTheSnapshotFieldsAtTheirDefaults(byte cls)
    {
        // 点券包(53)与物品空间包(13)各一帧,类字节是 2 或 255:计入"看过",不计解析成功、不计错位、不计缺回调,快照对应字段仍是默认。
        ItemSyncSnapshot s = Parse(
            FrameOfClass(cls, CeraId, CeraBody),
            FrameOfClass(cls, SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA))));
        Assert.Equal(2, s.FramesSeen);
        Assert.Equal(0, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
        Assert.Equal(0, s.FramesKindUnresolved);
        Assert.Null(s.Cera);
        Assert.Null(s.Bag);
        Assert.True(s.Currency.IsEmpty);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public void FramesOfAnUnknownClassNeverChangeTheStateBuiltByTheFramesAroundThem(byte cls)
    {
        byte[] otherCera = (byte[])CeraBody.Clone();
        otherCera[1] = 0x99;
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 100, 200, 300, Item(5, ItemA))),
            // 若被当成通知:会把整个背包换成槽 7 的一份、再删掉槽 5、改掉金币、改掉点券。
            FrameOfClass(cls, SpaceId, BagSpacePacket(16, 1, 2, 3, Item(7, ItemC))),
            FrameOfClass(cls, UpdateId, UpdatePacket(0, Item(5, DeleteMarker), Pseudo(0, 7))),
            Noti(CeraId, CeraBody),
            FrameOfClass(cls, CeraId, otherCera));
        Assert.Equal(5, s.FramesSeen);
        Assert.Equal(2, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
        Assert.Equal(new[] { 5 }, Slots(s.Bag));
        Assert.Equal(8, s.Bag.Capacity);
        Assert.Equal(1, s.Bag.FullPacketCount);
        Assert.Equal(0, s.Bag.UpdatePacketCount);
        Assert.Equal(100u, s.Currency.Gold);
        Assert.Equal(0x10u, s.Cera.Cera);
    }

    [Fact]
    public void TheClassOfTheFrameDecidesWhichPacketItIs()
    {
        // 类 1 的 13 / 53 不是物品空间包 / 点券;类 0 的 587 也不是装扮币应答。
        ItemSyncSnapshot s = Parse(
            Cmd(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5))),
            Cmd(CeraId, CeraBody),
            Noti(CoinId, CoinBody));
        Assert.Equal(3, s.FramesSeen);
        Assert.Equal(0, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
        Assert.Null(s.Bag);
        Assert.Null(s.Cera);
        Assert.Null(s.AvatarCoin);
    }

    // ---- 物品空间包:整份状态 ----

    [Fact]
    public void ABagPacketBecomesTheBagStateWithCapacityAndAscendingSlots()
    {
        ItemSyncSnapshot s = Parse(Noti(SpaceId, BagSpacePacket(16, 1000, 2000, 3000, Item(30, ItemC), Item(9, ItemA), Item(12, ItemB))));
        Assert.NotNull(s.Bag);
        Assert.Equal(ItemSpace.Bag, s.Bag.Space);
        Assert.True(s.Bag.HasFullState);
        Assert.True(s.Bag.HasCapacity);
        Assert.Equal(16, s.Bag.Capacity);
        Assert.Equal(3, s.Bag.Count);
        Assert.Equal(new[] { 9, 12, 30 }, Slots(s.Bag));
        Assert.Equal(new uint[] { 900001, 900002, 900003 }, Ids(s.Bag));
        Assert.Equal(1, s.Bag.FullPacketCount);
        Assert.Equal(0, s.Bag.UpdatePacketCount);
        Assert.Equal(1, s.FramesSeen);
        Assert.Equal(1, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(16)]
    public void TheBagCapacityIsKeptAsSent(int capacity)
    {
        ItemSyncSnapshot s = Parse(Noti(SpaceId, BagSpacePacket(capacity, 1, 2, 3)));
        Assert.Equal(capacity, s.Bag.Capacity);
        Assert.True(s.Bag.HasCapacity);
        Assert.Equal(0, s.Bag.Count);
    }

    [Fact]
    public void BagCurrencyIsStoredSeparatelyAndIsNotABagEntry()
    {
        ItemSyncSnapshot s = Parse(Noti(SpaceId, BagSpacePacket(8, 1000, 2000, 3000, Item(5))));
        Assert.Equal(1000u, s.Currency.Gold);
        Assert.Equal(2000u, s.Currency.CoinTotal);
        Assert.Equal(3000u, s.Currency.WinPoint);
        Assert.Equal(1, s.Bag.Count);
        Assert.False(s.Bag.IsOccupied(0));
        Assert.False(s.Bag.IsOccupied(1));
        Assert.False(s.Bag.IsOccupied(2));
        Assert.Null(s.Bag.GetEntry(1));
    }

    [Fact]
    public void ALaterSpacePacketReplacesTheWholeSpace()
    {
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA), Item(6, ItemB))),
            Noti(SpaceId, BagSpacePacket(16, 1, 2, 3, Item(7, ItemC))));
        Assert.Equal(new[] { 7 }, Slots(s.Bag));
        Assert.Equal(16, s.Bag.Capacity);
        Assert.Equal(2, s.Bag.FullPacketCount);
        Assert.Null(s.Bag.GetEntry(5));
        Assert.Null(s.Bag.GetEntry(6));
    }

    [Fact]
    public void SpacePacketsOfDifferentSpacesDoNotInterfere()
    {
        ItemSyncSnapshot s = ParseWith(
            Resolver().With(ItemC, ResolvedItemKind.Other),
            Noti(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA))),
            Noti(SpaceId, CargoSpacePacket(24, Item(5, ItemB), Item(6, ItemB))),
            Noti(SpaceId, AvatarSpacePacket(AvatarItem(3, ItemA))),
            Noti(SpaceId, PetSpacePacket(Item(1, ItemC))),
            Noti(SpaceId, AccountCargoSpacePacket(7, 70000, Item(2, ItemC))));
        Assert.Equal(5, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
        Assert.Equal(new uint[] { 900001 }, Ids(s.Bag));
        Assert.Equal(new uint[] { 900002, 900002 }, Ids(s.Cargo));
        Assert.Equal(new uint[] { 900001 }, Ids(s.AvatarBag));
        Assert.Equal(new uint[] { 900003 }, Ids(s.PetBag));
        Assert.Equal(new uint[] { 900003 }, Ids(s.AccountCargo));
        Assert.Null(s.BodyEquip);
        // 同一个槽号 5 在背包与仓库里是两个不同的条目。
        Assert.Equal(900001u, s.Bag.GetEntry(5).ItemId);
        Assert.Equal(900002u, s.Cargo.GetEntry(5).ItemId);
    }

    [Fact]
    public void CargoCapacityAndTheAccountCargoHeaderAreKept()
    {
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, CargoSpacePacket(24, Item(5))),
            Noti(SpaceId, AccountCargoSpacePacket(0x1234, 0x01020304, Item(2))));
        Assert.True(s.Cargo.HasCapacity);
        Assert.Equal(24, s.Cargo.Capacity);
        Assert.False(s.Cargo.HasAccountCargoHeader);
        Assert.False(s.AccountCargo.HasCapacity);
        Assert.True(s.AccountCargo.HasAccountCargoHeader);
        Assert.Equal(0x1234, s.AccountCargo.AccountCargoWord);
        Assert.Equal(0x01020304u, s.AccountCargo.AccountCargoDword);
        Assert.Null(s.AvatarBag);
    }

    [Fact]
    public void DuplicateSlotsInOnePacketKeepTheLaterEntry()
    {
        ItemSyncSnapshot s = Parse(Noti(SpaceId, CargoSpacePacket(8, Item(5, ItemA), Item(5, ItemB))));
        Assert.Equal(1, s.Cargo.Count);
        Assert.Equal(900002u, s.Cargo.GetEntry(5).ItemId);
    }

    [Fact]
    public void EmptySlotsAreNullAndOccupiedSlotsAreReported()
    {
        ItemSyncSnapshot s = Parse(Noti(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA))));
        Assert.NotNull(s.Bag.GetEntry(5));
        Assert.True(s.Bag.IsOccupied(5));
        Assert.Null(s.Bag.GetEntry(6));
        Assert.False(s.Bag.IsOccupied(6));
        Assert.Null(s.Bag.GetEntry(ushort.MaxValue));
    }

    // ---- 物品增量包:逐槽补丁 ----

    [Fact]
    public void AnUpsertReplacesOnlyItsOwnSlot()
    {
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA), Item(6, ItemB))),
            Noti(UpdateId, UpdatePacket(0, Item(6, ItemC))));
        Assert.Equal(new[] { 5, 6 }, Slots(s.Bag));
        Assert.Equal(900001u, s.Bag.GetEntry(5).ItemId);
        Assert.Equal(900003u, s.Bag.GetEntry(6).ItemId);
        Assert.Equal(1, s.Bag.UpdatePacketCount);
        Assert.Equal(1, s.Bag.FullPacketCount);
    }

    [Fact]
    public void AnUpdateIsAPatchAndNeverReplacesTheWholeSpace()
    {
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA), Item(6, ItemB), Item(7, ItemC))),
            Noti(UpdateId, UpdatePacket(0, Item(20, ItemA))));
        Assert.Equal(new[] { 5, 6, 7, 20 }, Slots(s.Bag));
        Assert.Equal(8, s.Bag.Capacity);
    }

    [Fact]
    public void TheDeleteMarkerEmptiesThatSlotOnly()
    {
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA), Item(6, ItemB))),
            Noti(UpdateId, UpdatePacket(0, Item(5, DeleteMarker))));
        Assert.Equal(new[] { 6 }, Slots(s.Bag));
        Assert.Null(s.Bag.GetEntry(5));
        Assert.False(s.Bag.IsOccupied(5));
    }

    [Fact]
    public void DeletingAnAlreadyEmptySlotIsHarmless()
    {
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA))),
            Noti(UpdateId, UpdatePacket(0, Item(40, DeleteMarker))));
        Assert.Equal(new[] { 5 }, Slots(s.Bag));
        Assert.Equal(2, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
    }

    [Fact]
    public void OperationsInOnePacketApplyInWireOrder()
    {
        // 槽 8:先放 A、再清空、再放 C → 最终是 C;槽 9:先放 A 再清空 → 空。
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 1, 2, 3)),
            Noti(UpdateId, UpdatePacket(0, Item(8, ItemA), Item(9, ItemA), Item(8, DeleteMarker), Item(9, DeleteMarker), Item(8, ItemC))));
        Assert.Equal(new[] { 8 }, Slots(s.Bag));
        Assert.Equal(900003u, s.Bag.GetEntry(8).ItemId);
    }

    [Fact]
    public void AnUpdateBeforeAnySpacePacketLayersOnAnEmptyStateAndIsMarkedPartial()
    {
        ItemSyncSnapshot s = Parse(Noti(UpdateId, UpdatePacket(0, Item(6, ItemB))));
        Assert.NotNull(s.Bag);
        Assert.False(s.Bag.HasFullState);
        Assert.False(s.Bag.HasCapacity);
        Assert.Equal(new[] { 6 }, Slots(s.Bag));
        Assert.Equal(0, s.Bag.FullPacketCount);
        Assert.Equal(1, s.Bag.UpdatePacketCount);
    }

    [Fact]
    public void ASpacePacketAfterAnUpdateOverridesWhatTheUpdateLaidDown()
    {
        ItemSyncSnapshot s = Parse(
            Noti(UpdateId, UpdatePacket(0, Item(6, ItemB))),
            Noti(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA))));
        Assert.Equal(new[] { 5 }, Slots(s.Bag));
        Assert.True(s.Bag.HasFullState);
        Assert.Equal(1, s.Bag.UpdatePacketCount);
        Assert.Equal(1, s.Bag.FullPacketCount);
    }

    [Fact]
    public void ACurrencyOnlyUpdateAfterTheBagPacketOverlaysThePresentSlotAndKeepsTheItems()
    {
        // 抓帧里唯一的增量包就是这个形态:进城完成帧之后,背包槽 1 的货币刷新。
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 100, 200, 300, Item(5, ItemA))),
            Noti(EnterCompleteId, new byte[0]),
            Noti(UpdateId, UpdatePacket(0, Pseudo(1, 250))));
        Assert.Equal(100u, s.Currency.Gold);
        Assert.Equal(250u, s.Currency.CoinTotal);
        Assert.Equal(300u, s.Currency.WinPoint);
        Assert.Equal(new[] { 5 }, Slots(s.Bag));
        Assert.Equal(1, s.Bag.UpdatePacketCount);
        Assert.Equal(3, s.FramesSeen);
        Assert.Equal(2, s.FramesParsed);
    }

    [Fact]
    public void EachCurrencySlotUpdatesItsOwnFieldOnly()
    {
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 100, 200, 300)),
            Noti(UpdateId, UpdatePacket(0, Pseudo(0, 111))),
            Noti(UpdateId, UpdatePacket(0, Pseudo(2, 333))));
        Assert.Equal(111u, s.Currency.Gold);
        Assert.Equal(200u, s.Currency.CoinTotal);
        Assert.Equal(333u, s.Currency.WinPoint);
    }

    [Fact]
    public void ABagPacketThatLacksACurrencySlotKeepsTheOlderValue()
    {
        // 本线的选择:缺席的货币槽保持原值(真实背包包恒带三个槽,没有缺席样本)。
        byte[] partial = new NoticePayloadBuilder().U8(0).U16(8).U16(2).AppendBagEntry(Pseudo(0, 111)).AppendBagEntry(Pseudo(1, 222)).ToArray();
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 100, 200, 300)),
            Noti(SpaceId, partial));
        Assert.Equal(111u, s.Currency.Gold);
        Assert.Equal(222u, s.Currency.CoinTotal);
        Assert.Equal(300u, s.Currency.WinPoint);
    }

    [Fact]
    public void ACurrencySlotInANonBagSpaceIsAnOrdinaryEntry()
    {
        // 仓库的槽 0、1、2 是普通格,不是货币。
        ItemSyncSnapshot s = Parse(Noti(SpaceId, CargoSpacePacket(8, Item(0, ItemA), Item(1, ItemB), Item(2, ItemC))));
        Assert.Equal(new[] { 0, 1, 2 }, Slots(s.Cargo));
        Assert.True(s.Currency.IsEmpty);
    }

    [Fact]
    public void BodyEquipUpdatesNeedAResolverAndNeverBecomeAFullState()
    {
        DecodedFrame frame = Noti(UpdateId, UpdatePacket(3, Item(2, ItemA)));
        ItemSyncSnapshot without = Parse(frame);
        Assert.Null(without.BodyEquip);
        Assert.Equal(1, without.FramesMalformed);
        Assert.Equal(1, without.FramesKindUnresolved);

        ItemSyncSnapshot with = ParseWith(Resolver().With(ItemA, ResolvedItemKind.Other), frame);
        Assert.NotNull(with.BodyEquip);
        Assert.False(with.BodyEquip.HasFullState);
        Assert.Equal(new[] { 2 }, Slots(with.BodyEquip));
        Assert.Equal(0, with.FramesMalformed);
    }

    [Fact]
    public void ADeleteOnlyBodyEquipUpdateNeedsNoResolver()
    {
        ItemSyncSnapshot s = Parse(Noti(UpdateId, UpdatePacket(3, Item(2, DeleteMarker))));
        Assert.Equal(0, s.FramesMalformed);
        Assert.NotNull(s.BodyEquip);
        Assert.Equal(0, s.BodyEquip.Count);
    }

    // ---- 物品类型回调 ----

    [Fact]
    public void PetBagWithoutAResolverIsMalformedAndCountedAsKindUnresolved()
    {
        ItemSyncSnapshot s = Parse(Noti(SpaceId, PetSpacePacket(Item(1, ItemA))));
        Assert.Null(s.PetBag);
        Assert.Equal(1, s.FramesSeen);
        Assert.Equal(0, s.FramesParsed);
        Assert.Equal(1, s.FramesMalformed);
        Assert.Equal(1, s.FramesKindUnresolved);
    }

    [Fact]
    public void PetBagWithAResolverIsParsedAndTheResolverIsConsulted()
    {
        DictionaryKindResolver resolver = Resolver().With(ItemA, ResolvedItemKind.Other);
        ItemSyncSnapshot s = ParseWith(resolver, Noti(SpaceId, PetSpacePacket(Item(1, ItemA))));
        Assert.NotNull(s.PetBag);
        Assert.Equal(1, s.PetBag.Count);
        Assert.Equal(0, s.FramesMalformed);
        Assert.True(resolver.CallCount > 0);
    }

    [Fact]
    public void AResolverThatDoesNotKnowTheItemRejectsThePacketToo()
    {
        ItemSyncSnapshot s = ParseWith(Resolver(), Noti(SpaceId, PetSpacePacket(Item(1, ItemA))));
        Assert.Null(s.PetBag);
        Assert.Equal(1, s.FramesMalformed);
        Assert.Equal(1, s.FramesKindUnresolved);
    }

    [Fact]
    public void AnEmptyPetBagNeedsNoResolver()
    {
        ItemSyncSnapshot s = Parse(Noti(SpaceId, PetSpacePacket()));
        Assert.NotNull(s.PetBag);
        Assert.Equal(0, s.PetBag.Count);
        Assert.Equal(1, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
    }

    [Fact]
    public void AKindUnresolvedFrameDoesNotDisturbTheEarlierState()
    {
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA))),
            Noti(SpaceId, PetSpacePacket(Item(1, ItemB))),
            Noti(UpdateId, UpdatePacket(7, Item(1, ItemB))));
        Assert.Equal(new[] { 5 }, Slots(s.Bag));
        Assert.Null(s.PetBag);
        Assert.Equal(2, s.FramesMalformed);
        Assert.Equal(2, s.FramesKindUnresolved);
        Assert.Equal(1, s.FramesParsed);
    }

    // ---- 错位计数 ----

    [Fact]
    public void MalformedItemFramesAreCountedAndLeaveTheEarlierStateAlone()
    {
        byte[] lying = new NoticePayloadBuilder().U8(0).U16(8).U16(500).ToArray();
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA))),
            Noti(SpaceId, lying),
            Noti(UpdateId, new byte[] { 0 }),
            Noti(UpdateId, new byte[0]));
        Assert.Equal(4, s.FramesSeen);
        Assert.Equal(1, s.FramesParsed);
        Assert.Equal(3, s.FramesMalformed);
        Assert.Equal(0, s.FramesKindUnresolved);
        Assert.Equal(new[] { 5 }, Slots(s.Bag));
        Assert.Equal(1, s.Bag.FullPacketCount);
    }

    [Fact]
    public void UnsupportedSpacesAreMalformed()
    {
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, new NoticePayloadBuilder().U8(3).U16(0).ToArray()),
            Noti(SpaceId, new NoticePayloadBuilder().U8(9).U16(0).ToArray()),
            Noti(UpdateId, new NoticePayloadBuilder().U8(5).U16(0).ToArray()));
        Assert.Equal(3, s.FramesMalformed);
        Assert.Equal(0, s.FramesParsed);
        Assert.Null(s.BodyEquip);
        Assert.Null(s.Bag);
    }

    [Fact]
    public void ADirectFrameTailAndZeroPaddingNeverMakeAFrameMalformed()
    {
        // 每个包都带 4 字节 PI 尾与补零(DirectFramePayload):读完字段即停,绝不当条目读。
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA))),
            Noti(UpdateId, UpdatePacket(0, Item(6, ItemB))),
            Noti(CeraId, CeraBody),
            Noti(CoolTimeId, TwoPairs),
            Noti(LockId, EmptyLockTable));
        Assert.Equal(5, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
        Assert.Equal(new[] { 5, 6 }, Slots(s.Bag));
    }

    [Fact]
    public void ParsedMalformedAndIgnoredFramesAddUpToTheSeenFrames()
    {
        ItemSyncSnapshot s = Parse(
            Noti(CeraId, CeraBody),
            Noti(CeraId, new byte[] { 1, 2 }),
            Noti(7, new byte[] { 1 }),
            Cmd(CoinId, CoinBody));
        Assert.Equal(4, s.FramesSeen);
        Assert.Equal(2, s.FramesParsed);
        Assert.Equal(1, s.FramesMalformed);
        Assert.Equal(1, s.FramesSeen - s.FramesParsed - s.FramesMalformed);
    }

    // ---- 小包落点 ----

    [Fact]
    public void EachSmallPacketLandsInItsOwnProperty()
    {
        ItemSyncSnapshot s = Parse(
            Noti(CeraId, CeraBody),
            Cmd(CoinId, CoinBody),
            Noti(CoolTimeId, OnePair),
            Noti(EffectId, TwoPairs),
            Noti(LockId, EmptyLockTable),
            Noti(MaxLevelId, MaxLevelBody),
            Noti(HotkeyId, HotkeyBody()),
            Noti(PetTableId, OnePetTable(9001)));
        Assert.Equal(8, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
        Assert.Equal(0x30u, s.Cera.CeraPoint);
        Assert.Equal(0x12345678u, s.AvatarCoin.Coin);
        Assert.Single(s.CoolTimeItems.Entries);
        Assert.Equal(2, s.EffectItems.Entries.Count);
        Assert.Empty(s.ItemLocks.Entries);
        Assert.Equal(0x1234, s.MaxEquipLevel.Level);
        Assert.Equal(94, s.HotkeyOption.Bindings.Count);
        Assert.Single(s.Creatures.Creatures);
        Assert.Equal(9001u, s.Creatures.Creatures[0].Uid);
    }

    [Fact]
    public void ALaterSmallPacketOverridesAndAMalformedOneDoesNotClobber()
    {
        byte[] second = (byte[])CeraBody.Clone();
        second[1] = 0x99;
        ItemSyncSnapshot s = Parse(
            Noti(CeraId, CeraBody),
            Noti(CeraId, second),
            Noti(CeraId, new byte[] { 1, 2, 3 }));
        Assert.Equal(0x99u, s.Cera.Cera);
        Assert.Equal(1, s.FramesMalformed);
        Assert.Equal(2, s.FramesParsed);
    }

    [Fact]
    public void CoolTimeAndEffectTablesAreKeptSeparately()
    {
        ItemSyncSnapshot s = Parse(Noti(CoolTimeId, OnePair), Noti(EffectId, TwoPairs));
        Assert.Single(s.CoolTimeItems.Entries);
        Assert.Equal(2, s.EffectItems.Entries.Count);
    }

    [Fact]
    public void ACoinFailureReplyIsKeptWithItsErrorCode()
    {
        ItemSyncSnapshot s = Parse(Cmd(CoinId, new byte[] { 0, 7 }));
        Assert.NotNull(s.AvatarCoin);
        Assert.False(s.AvatarCoin.Success);
        Assert.Equal(7, s.AvatarCoin.ErrorCode);
    }

    [Fact]
    public void ExpandEquipNoticesAreAllKeptInArrivalOrder()
    {
        ItemSyncSnapshot s = Parse(Noti(ExpandId, ExpandBody(0x0101)), Noti(ExpandId, ExpandBody(0x0202)));
        Assert.Equal(2, s.ExpandEquips.Count);
        Assert.Equal(0x0101, s.ExpandEquips[0].Uid);
        Assert.Equal(0x0202, s.ExpandEquips[1].Uid);
        Assert.True(s.ExpandEquips[0].AllGroupsEmpty);
    }

    [Fact]
    public void TheResolverReachesTheSpareEquipGroups()
    {
        // 备用组里槽 12 的记录:回调说它是装扮类 → 带两个块;没有回调时按槽位近似(槽 12 不是装扮类)读不通,整包错位。
        ItemSpec avatarInSpare = Item(12, ItemA);
        avatarInSpare.SocketBlock = Socket();
        avatarInSpare.ExpansionBlock = Expansion();
        byte[] body = new NoticePayloadBuilder().U16(0x0101).U8(10).AppendDetailList(avatarInSpare).U8(11).AppendDetailList().U8(12).AppendDetailList().ToArray();

        ItemSyncSnapshot with = ParseWith(Resolver().With(ItemA, ResolvedItemKind.Avatar), Noti(ExpandId, body));
        Assert.Equal(1, with.FramesParsed);
        Assert.Single(with.ExpandEquips[0].Groups[0].Entries);
        Assert.True(with.ExpandEquips[0].Groups[0].Entries[0].HasAvatarBlocks);

        ItemSyncSnapshot without = Parse(Noti(ExpandId, body));
        Assert.Equal(1, without.FramesMalformed);
        Assert.Empty(without.ExpandEquips);
    }

    // ---- 用户信息视图 ----

    private static DecodedFrame PublicViewFrame(int uid, int slot, long itemId)
    {
        return UserInfoTestData.DecodedFrameOf(PublicViewPacket(1, uid, Name4, new byte[] { 1, 2, 3, 4, 5, 6 }, 0x66, new byte[20], (slot, itemId, 0x03)));
    }

    private static DecodedFrame SelfViewFrame(int slot, long itemId)
    {
        return UserInfoTestData.DecodedFrameOf(SelfViewPacket(1, 7, 1234, new byte[10], 0x55, new byte[20], Item(slot, itemId)));
    }

    [Fact]
    public void PublicViewsKeepArrivalOrderAndTheSelfViewKeepsTheLatest()
    {
        ItemSyncSnapshot s = Parse(
            PublicViewFrame(0x0101, 4, ItemA),
            SelfViewFrame(12, ItemB),
            PublicViewFrame(0x0202, 5, ItemB),
            SelfViewFrame(13, ItemC));
        Assert.Equal(4, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
        Assert.Equal(2, s.PublicViews.Count);
        Assert.Equal(0x0101, s.PublicViews[0].Uid);
        Assert.Equal(0x0202, s.PublicViews[1].Uid);
        Assert.True(s.PublicViews[0].IsPublicView);
        Assert.Single(s.PublicViews[0].CompactEquips);
        Assert.True(s.SelfView.IsSelfView);
        Assert.Equal(13, s.SelfView.DetailedEquips[0].Slot);
        Assert.Equal(900003u, s.SelfView.DetailedEquips[0].ItemId);
    }

    [Fact]
    public void ACharacterListFrameIsNotMineAndNotMalformed()
    {
        // 模式 2(角色列表)归既有的 UserInfoParser:这里只算"看过"。
        ItemSyncSnapshot s = Parse(UserInfoTestData.DecodedFrameOf(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.Equal(1, s.FramesSeen);
        Assert.Equal(0, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
        Assert.Empty(s.PublicViews);
        Assert.Null(s.SelfView);
    }

    [Fact]
    public void ABrokenViewFrameIsMalformed()
    {
        ItemSyncSnapshot s = Parse(UserInfoTestData.DecodedFrameOf(new byte[] { 0, 1, 0 }));
        Assert.Equal(1, s.FramesMalformed);
        Assert.Empty(s.PublicViews);
    }

    // ---- 只读 ----

    [Fact]
    public void TheSnapshotDoesNotShareStateWithTheFramePayloads()
    {
        ItemSpec avatar = AvatarItem(3, ItemA);
        DecodedFrame frame = Noti(SpaceId, AvatarSpacePacket(avatar));
        ItemSyncSnapshot s = Parse(frame);
        byte first = s.AvatarBag.GetEntry(3).SocketBlock[0];

        Array.Clear(frame.Payload, 0, frame.Payload.Length);
        Assert.Equal(first, s.AvatarBag.GetEntry(3).SocketBlock[0]);
        Assert.Equal(900001u, s.AvatarBag.GetEntry(3).ItemId);
    }

    [Fact]
    public void TheSnapshotCollectionsAreReadOnly()
    {
        ItemSyncSnapshot s = Parse(
            Noti(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA))),
            Noti(ExpandId, ExpandBody(1)));
        Assert.True(((ICollection<ItemEntry>)s.Bag.Entries).IsReadOnly);
        Assert.True(((ICollection<ExpandEquipNotice>)s.ExpandEquips).IsReadOnly);
        Assert.True(((ICollection<UserInfoViewNotice>)s.PublicViews).IsReadOnly);
    }

    [Fact]
    public void ParsingTheSameFramesTwiceGivesIndependentEqualSnapshots()
    {
        DecodedFrame[] frames =
        {
            Noti(SpaceId, BagSpacePacket(8, 1, 2, 3, Item(5, ItemA))),
            Noti(UpdateId, UpdatePacket(0, Item(6, ItemB))),
        };
        ItemSyncSnapshot a = ItemSyncParser.Parse(frames);
        ItemSyncSnapshot b = ItemSyncParser.Parse(frames);
        Assert.NotSame(a, b);
        Assert.NotSame(a.Bag, b.Bag);
        Assert.Equal(Slots(a.Bag), Slots(b.Bag));
        Assert.Equal(Ids(a.Bag), Ids(b.Bag));
    }
}
