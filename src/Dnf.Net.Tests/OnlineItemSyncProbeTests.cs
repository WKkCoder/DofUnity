using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.ItemWire;

namespace Dnf.Net.Tests;

/// <summary>
/// r20-probeitems 端到端与脱敏测试:
/// 1) <see cref="OnlineEntryResult.ItemSync"/> 与 <see cref="OnlineEntryResult"/> 构造器向后兼容;
/// 2) 经 <see cref="HostTestKit.EnterTown"/>(<see cref="OnlineTownEntry.EnterOnSessionAsync"/>) 覆盖背包、货币 3 槽、增量、
///    BodyEquip/SelfView/ExpandEquips 三条路径、畸形帧、未知包号、无物品帧;
/// 3) <see cref="OnlineClientSession.Detach"/> 与 <see cref="OnlineSelectCharacterResult.ItemSync"/> 在完整跑法与分阶段跑法下保留快照;
/// 4) <see cref="OnlineItemSyncLog.FormatProbeLine"/> 整行逐字断言(null、空、典型、畸形+未决+装扮币失败、11 类尾字节逐项叠加、货币各槽独立组合);
/// 5) 专门脱敏用例:往合成数据注入醒目的物品号/金额/槽号/强化/名字,断言输出行里一个都不泄漏。
/// 全是合成值(物品号 900001 起,账号 905,无任何真实数据)。
/// </summary>
public sealed class OnlineItemSyncProbeTests
{
    private const int SpaceId = 13;
    private const int UpdateId = 14;
    private const int CeraId = 53;
    private const int PetTableId = 105;
    private const int CoolTimeId = 172;
    private const int EffectId = 174;
    private const int LockId = 251;
    private const int ExpandId = 339;
    private const int MaxLevelId = 438;
    private const int HotkeyId = 452;
    private const int CoinId = 587;

    private static byte[] NotiDirect(int id, byte[] body)
    {
        return DungeonEntryWire.Notice((ushort)id, DirectFramePayload(body, id));
    }

    private static byte[] CmdDirect(int id, byte[] body)
    {
        return DungeonEntryWire.Reply((ushort)id, DirectFramePayload(body, id));
    }

    private static DecodedFrame FrameNoti(int id, byte[] body, int extraTail = 0)
    {
        byte[] payload = extraTail == 0
            ? DirectFramePayload(body, id)
            : body.Concat(new byte[extraTail]).ToArray();
        return FrameOf(ClassNoti, id, payload);
    }

    private static DecodedFrame FrameCmd(int id, byte[] body, int extraTail = 0)
    {
        byte[] payload = extraTail == 0
            ? DirectFramePayload(body, id)
            : body.Concat(new byte[extraTail]).ToArray();
        return FrameOf(ClassCmd, id, payload);
    }

    private static ItemSpec AvatarItem(int slot, long itemId)
    {
        ItemSpec spec = Item(slot, itemId);
        spec.SocketBlock = Socket();
        spec.ExpansionBlock = Expansion();
        return spec;
    }

    private static byte[] CeraBody(long cera = 0x10, long mileage = 0x20, long ceraPoint = 0x30)
    {
        return new NoticePayloadBuilder().U8(1).U32(cera).U32(mileage).U32(ceraPoint).ToArray();
    }

    private static byte[] CoinOkBody(long coin)
    {
        return new NoticePayloadBuilder().U8(1).U32(coin).ToArray();
    }

    private static byte[] CoinFailBody(int errorCode)
    {
        return new NoticePayloadBuilder().U8(0).U8(errorCode).ToArray();
    }

    private static byte[] HotkeyBody()
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U8(3).U32(0xBC);
        for (int i = 0; i < 94; i++)
        {
            b.U16(i + 1);
        }
        return b.ToArray();
    }

    private static byte[] ExpandBody(int uid, params ItemSpec[] group0Entries)
    {
        return new NoticePayloadBuilder()
            .U16(uid)
            .U8(10).AppendDetailList(group0Entries)
            .U8(11).AppendDetailList()
            .U8(12).AppendDetailList()
            .ToArray();
    }

    private static byte[] OnePetTable(long uid)
    {
        return new NoticePayloadBuilder()
            .U8(1).U32(uid).U8(80).U8(1).U32(500).U8(7).U32(2).Raw(0x41, 0x42).U8(0)
            .ToArray();
    }

    // ======== 1. 向后兼容 ========

    [Fact]
    public void Legacy_eleven_argument_OnlineEntryResult_constructor_defaults_ItemSync_to_null()
    {
        var legacy = new OnlineEntryResult(
            OnlineStage.Done,
            OnlineFailure.None,
            null,
            2,
            null,
            null,
            15,
            1,
            TownEntrySnapshot.Empty,
            null,
            null);

        Assert.True(legacy.Entered);
        Assert.Equal(2, legacy.Slot);
        Assert.Equal(15, legacy.FramesSeen);
        Assert.Equal(1, legacy.SecondLayerMismatches);
        Assert.Null(legacy.ItemSync);
    }

    // ======== 2. 端到端进城(HostTestKit.EnterTown) ========

    [Fact]
    public async Task EnterTown_without_item_frames_produces_empty_ItemSync_snapshot_and_zeroed_probe_line()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(),
            slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        Assert.NotNull(result.ItemSync);
        Assert.Equal(2, result.ItemSync.FramesSeen);      // 登录回包(CMD/1) + 进城完成(NOTI/124)
        Assert.Equal(0, result.ItemSync.FramesParsed);
        Assert.Equal(0, result.ItemSync.FramesMalformed);
        Assert.Equal(0, result.ItemSync.FramesKindUnresolved);
        Assert.Null(result.ItemSync.Bag);
        Assert.Null(result.ItemSync.AvatarBag);
        Assert.Null(result.ItemSync.Cargo);
        Assert.Null(result.ItemSync.PetBag);
        Assert.Null(result.ItemSync.AccountCargo);
        Assert.Null(result.ItemSync.BodyEquip);
        Assert.Null(result.ItemSync.Cera);
        Assert.Null(result.ItemSync.AvatarCoin);
        Assert.True(result.ItemSync.Currency.IsEmpty);
        Assert.Equal(
            "PROBE_ITEMS seen=2 parsed=0 malformed=0 unresolved=0 bag=0 avatarBag=0 cargo=0 petBag=0 bodyEquip=0 accountCargo=0 selfEquip=0 gold=0 coin=0 winPoint=0 cera=0 avatarCoin=0 tailBytes=0 nonOkEntries=0",
            OnlineItemSyncLog.FormatProbeLine(result.ItemSync));
    }

    [Fact]
    public async Task EnterTown_aggregates_spaces_currency_updates_and_small_packets_across_town_windows()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        // 计算好本用例各个小包的直连块补零尾字节,手工核对 tailBytes 期望值:
        // CeraBody(13B): slot(53%14=11)->block=8; 13+4=17 -> padded=24 -> trailing = 24-13 = 11
        // CoinOkBody(5B): slot(587%14=13)->block=16; 5+4=9 -> padded=16 -> trailing = 16-5 = 11
        // CoolTime(9B): slot(172%14=4)->block=16; 9+4=13 -> padded=16 -> trailing = 16-9 = 7
        // Lock(2B): slot(251%14=13)->block=16; 2+4=6 -> padded=16 -> trailing = 16-2 = 14
        // MaxLevel(2B): slot(438%14=4)->block=16; 2+4=6 -> padded=16 -> trailing = 16-2 = 14
        // 合计 tailBytes = 11 + 11 + 7 + 14 + 14 = 57
        byte[] unknownNoti = DungeonEntryWire.Notice(910, new byte[] { 1, 2, 3, 4 });
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(
                HostTestKit.DirectListNotice(),
                NotiDirect(SpaceId, BagSpacePacket(8, 1000, 2000, 3000, Item(5, ItemA), Item(9, ItemB))),
                NotiDirect(SpaceId, AvatarSpacePacket(AvatarItem(3, ItemA))),
                NotiDirect(SpaceId, CargoSpacePacket(16, Item(4, ItemB), Item(5, ItemC), Item(6, ItemA)))),
            slot => HostTestKit.Frames(
                DungeonEntryWire.Notice(124, new byte[] { 0 }),
                NotiDirect(SpaceId, AccountCargoSpacePacket(1, 7000, Item(2, ItemC))),
                NotiDirect(SpaceId, PetSpacePacket()),
                NotiDirect(CeraId, CeraBody(500, 60, 70)),
                CmdDirect(CoinId, CoinOkBody(80)),
                NotiDirect(CoolTimeId, new byte[] { 1, 0x11, 0, 0, 0, 0x22, 0, 0, 0 }),
                NotiDirect(LockId, new byte[] { 0, 0 }),
                NotiDirect(MaxLevelId, new byte[] { 85, 0 }),
                // 尾随增量:刷新硬币合计,删掉槽 5,写入槽 12
                NotiDirect(UpdateId, UpdatePacket(0, Pseudo(1, 2500), Item(5, DeleteMarker), Item(12, ItemC))),
                unknownNoti));

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        ItemSyncSnapshot s = result.ItemSync;
        Assert.NotNull(s);
        // 帧总数: 登录回包(1) + 列表窗(4: 列表模式2 + Bag + AvatarBag + Cargo) + 选角窗(10: 124 + AccountCargo + PetBag(空) + Cera + Coin + CoolTime + Lock + MaxLevel + Update + 910) = 15
        // 解析成功: Bag + AvatarBag + Cargo + AccountCargo + PetBag(空) + Cera + Coin + CoolTime + Lock + MaxLevel + Update = 11
        Assert.Equal(15, s.FramesSeen);
        Assert.Equal(11, s.FramesParsed);
        Assert.Equal(0, s.FramesMalformed);
        Assert.Equal(0, s.FramesKindUnresolved);

        Assert.Equal(2, s.Bag.Count);                 // 槽 9, 12 (槽 5 被删)
        Assert.Equal(new ushort[] { 9, 12 }, s.Bag.Entries.Select(e => e.Slot).ToArray());
        Assert.Equal(1, s.AvatarBag.Count);
        Assert.Equal(3, s.Cargo.Count);
        Assert.Equal(0, s.PetBag.Count);
        Assert.Equal(1, s.AccountCargo.Count);
        Assert.Null(s.BodyEquip);

        Assert.Equal(1000u, s.Currency.Gold);
        Assert.Equal(2500u, s.Currency.CoinTotal);
        Assert.Equal(3000u, s.Currency.WinPoint);
        Assert.Equal(500u, s.Cera.Cera);
        Assert.True(s.AvatarCoin.Success);
        Assert.Equal(80u, s.AvatarCoin.Coin);

        Assert.Equal(
            "PROBE_ITEMS seen=15 parsed=11 malformed=0 unresolved=0 bag=2 avatarBag=1 cargo=3 petBag=0 bodyEquip=0 accountCargo=1 selfEquip=0 gold=1 coin=1 winPoint=1 cera=1 avatarCoin=1 tailBytes=57 nonOkEntries=0",
            OnlineItemSyncLog.FormatProbeLine(s));
    }

    [Fact]
    public async Task EnterTown_handles_BodyEquip_SelfView_and_ExpandEquips_three_paths_with_null_kindResolver()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        // SelfView(NOTI/2, 压缩包号): 载荷走 zlib 压缩,10 字节非物品尾 -> TrailingByteCount = 10
        byte[] selfViewPlain = SelfViewPacket(1, 7, 1234, new byte[8], 0x55, new byte[10], Item(12, ItemA), Item(14, ItemB));
        byte[] selfViewFrame = DungeonEntryWire.Notice(2, UserInfoTestData.Zlib(selfViewPlain));
        // ExpandBody(uid=9001, slot10=Item(10, ItemC)):
        //   u16(2) + u8(10)+u8(1)+DetailEntry(27) + u8(11)+u8(0) + u8(12)+u8(0) = 2 + 29 + 2 + 2 = 35B
        //   slot(339%14=3)->block=16; 35+4=39 -> padded=48 -> trailing = 48-35 = 13
        byte[] expandBody = ExpandBody(9001, Item(10, ItemC));

        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(),
            slot => HostTestKit.Frames(
                DungeonEntryWire.Notice(124, new byte[] { 0 }),
                // 路径1a: 身上装备非删除增量(无 kindResolver → 预期计入 FramesKindUnresolved 与 FramesMalformed)
                NotiDirect(UpdateId, UpdatePacket(3, Item(11, ItemA))),
                // 路径1b: 身上装备仅删除增量(无需 kindResolver → 解析成功,BodyEquip 存在且为 0)
                NotiDirect(UpdateId, UpdatePacket(3, Item(11, DeleteMarker))),
                // 路径2: SelfView(模式 1)无 kindResolver 按槽位近似解析
                selfViewFrame,
                // 路径3: ExpandEquips(NOTI/339)无 kindResolver 按槽位近似解析
                NotiDirect(ExpandId, expandBody),
                // 宠物栏非空无 kindResolver → 同样计入 FramesKindUnresolved
                NotiDirect(SpaceId, PetSpacePacket(Item(1, ItemB)))));

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        ItemSyncSnapshot s = result.ItemSync;
        Assert.Equal(7, s.FramesSeen);          // LoginOk(1) + 124 + BodyEquipUpsert + BodyEquipDelete + SelfView + ExpandEquip + PetBag
        Assert.Equal(3, s.FramesParsed);        // BodyEquipDelete + SelfView + ExpandEquip
        Assert.Equal(2, s.FramesMalformed);     // BodyEquipUpsert + PetBag
        Assert.Equal(2, s.FramesKindUnresolved);
        Assert.NotNull(s.BodyEquip);
        Assert.Equal(0, s.BodyEquip.Count);
        Assert.Null(s.PetBag);
        Assert.NotNull(s.SelfView);
        Assert.Equal(2, s.SelfView.DetailedEquips.Count);
        Assert.Single(s.ExpandEquips);
        Assert.Single(s.ExpandEquips[0].Groups[0].Entries);

        // tailBytes = SelfView(10) + ExpandEquip(13) = 23
        Assert.Equal(
            "PROBE_ITEMS seen=7 parsed=3 malformed=2 unresolved=2 bag=0 avatarBag=0 cargo=0 petBag=0 bodyEquip=0 accountCargo=0 selfEquip=2 gold=0 coin=0 winPoint=0 cera=0 avatarCoin=0 tailBytes=23 nonOkEntries=0",
            OnlineItemSyncLog.FormatProbeLine(s));
    }

    [Fact]
    public async Task EnterTown_keeps_prior_valid_state_when_malformed_frames_and_avatar_coin_failure_arrive()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        // 谎报 500 个条目的背包包(畸形) + 截断的点券包(畸形) + 装扮币失败应答(2B; slot(587%14=13)->block=16; 2+4=6 -> padded=16 -> trailing=14)
        byte[] lyingBag = new NoticePayloadBuilder().U8(0).U16(8).U16(500).ToArray();
        byte[] shortCera = { 1, 2, 3 };
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(
                NotiDirect(SpaceId, BagSpacePacket(8, 100, 0, 0, Item(5, ItemA)))),
            slot => HostTestKit.Frames(
                DungeonEntryWire.Notice(124, new byte[] { 0 }),
                DungeonEntryWire.Notice(SpaceId, lyingBag),
                DungeonEntryWire.Notice(CeraId, shortCera),
                CmdDirect(CoinId, CoinFailBody(7))));

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        ItemSyncSnapshot s = result.ItemSync;
        Assert.Equal(6, s.FramesSeen);
        Assert.Equal(2, s.FramesParsed);        // Bag + CoinFailBody
        Assert.Equal(2, s.FramesMalformed);     // lyingBag + shortCera
        Assert.Equal(0, s.FramesKindUnresolved);
        Assert.Equal(1, s.Bag.Count);           // 畸形背包包没有冲掉先前的 1 个物品
        Assert.NotNull(s.AvatarCoin);
        Assert.False(s.AvatarCoin.Success);
        Assert.Equal(7, s.AvatarCoin.ErrorCode);

        Assert.Equal(
            "PROBE_ITEMS seen=6 parsed=2 malformed=2 unresolved=0 bag=1 avatarBag=0 cargo=0 petBag=0 bodyEquip=0 accountCargo=0 selfEquip=0 gold=1 coin=1 winPoint=1 cera=0 avatarCoin=0 tailBytes=14 nonOkEntries=1",
            OnlineItemSyncLog.FormatProbeLine(s));
    }

    [Fact]
    public async Task EnterTown_failure_leaves_ItemSync_null()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(NotiDirect(SpaceId, BagSpacePacket(8, 100, 200, 300, Item(5, ItemA)))),
            slot => HostTestKit.Frames());

        OnlineEntryResult result = await HostTestKit.EnterTown(session, maxSlots: 2);

        Assert.False(result.Entered);
        Assert.Equal(OnlineFailure.NoUsableSlot, result.Failure);
        Assert.Null(result.ItemSync);
    }

    [Fact]
    public async Task EnterTown_includes_tail_window_frames_after_slot_window_cap()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        // 选角窗口上限 64 帧:放 1 帧 124 + 63 帧无关帧(910)填满 64 帧,第 65 帧(Cera)必然落入 tail 窗口
        var burst = new List<byte[]> { DungeonEntryWire.Notice(124, new byte[] { 0 }) };
        for (int i = 0; i < 63; i++)
        {
            burst.Add(DungeonEntryWire.Notice(910, new byte[] { 1 }));
        }
        burst.Add(NotiDirect(CeraId, CeraBody(909, 10, 20)));

        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(),
            slot => burst.ToArray());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        Assert.NotNull(result.ItemSync.Cera);
        Assert.Equal(909u, result.ItemSync.Cera.Cera);
        Assert.Equal(66, result.ItemSync.FramesSeen); // LoginOk(1) + 65
        Assert.Equal(1, result.ItemSync.FramesParsed);
    }

    // ======== 3. OnlineClientSession.Detach 与 OnlineSelectCharacterResult ========

    [Fact]
    public async Task OnlineTownEntry_RunAsync_preserves_ItemSync_across_OnlineClientSession_Detach()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(
                HostTestKit.DirectListNotice(),
                NotiDirect(SpaceId, BagSpacePacket(8, 321, 0, 654, Item(4, ItemA), Item(7, ItemB)))),
            slot => HostTestKit.Frames(
                DungeonEntryWire.Notice(124, new byte[] { 0 }),
                NotiDirect(CeraId, CeraBody(111, 222, 333))));

        using OnlineEntryResult result = await GatewayTestSupport.Bounded(rig.CreateTownEntry().RunAsync(), 20);

        Assert.True(result.Entered);
        Assert.NotNull(result.ItemSync);
        Assert.Equal(2, result.ItemSync.Bag.Count);
        Assert.Equal(321u, result.ItemSync.Currency.Gold);
        Assert.Equal(654u, result.ItemSync.Currency.WinPoint);
        Assert.Equal(111u, result.ItemSync.Cera.Cera);
        // Cera trailing = 11
        Assert.Equal(
            "PROBE_ITEMS seen=5 parsed=2 malformed=0 unresolved=0 bag=2 avatarBag=0 cargo=0 petBag=0 bodyEquip=0 accountCargo=0 selfEquip=0 gold=1 coin=1 winPoint=1 cera=1 avatarCoin=0 tailBytes=11 nonOkEntries=0",
            OnlineItemSyncLog.FormatProbeLine(result.ItemSync));
    }

    [Fact]
    public async Task Staged_SelectCharacterAsync_exposes_ItemSync_on_success_and_null_on_failure()
    {
        var rig = new OnlineFlowRig();
        rig.ScriptFactory = _ => HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.DirectListNotice()),
            slot => slot == 1
                ? HostTestKit.Frames(
                    DungeonEntryWire.Notice(124, new byte[] { 0 }),
                    NotiDirect(SpaceId, CargoSpacePacket(24, Item(0, ItemB))))
                : HostTestKit.Frames(DungeonEntryWire.Reply(4, new byte[] { 0, 4 })));

        using OnlineClientSession client = await rig.LoggedIn();
        OnlineConnectResult connected = await GatewayTestSupport.Bounded(client.ConnectAsync(9010), 20);
        Assert.True(connected.IsSuccess);

        OnlineSelectCharacterResult failed = await GatewayTestSupport.Bounded(client.SelectCharacterAsync(0), 20);
        Assert.False(failed.Entered);
        Assert.Null(failed.ItemSync);

        OnlineSelectCharacterResult ok = await GatewayTestSupport.Bounded(client.SelectCharacterAsync(1), 20);
        Assert.True(ok.Entered);
        Assert.False(ok.MayTryAnotherSlot);
        Assert.NotNull(ok.ItemSync);
        Assert.Equal(1, ok.ItemSync.Cargo.Count);
        Assert.Equal(900002u, ok.ItemSync.Cargo.GetEntry(0).ItemId);

        using OnlineEntryResult detachedFailed = client.Detach(OnlineTownEntry.Fail(
            OnlineStage.SelectSlot, OnlineFailure.NoUsableSlot, null, -1, 0, null, null, 0));
        Assert.Null(detachedFailed.ItemSync);
    }

    // ======== 4. FormatProbeLine 整行逐字、各空间/各货币/各尾字节/非Ok 分支 ========

    [Fact]
    public void FormatProbeLine_handles_null_and_empty_snapshots_verbatim()
    {
        const string zeroLine =
            "PROBE_ITEMS seen=0 parsed=0 malformed=0 unresolved=0 bag=0 avatarBag=0 cargo=0 petBag=0 bodyEquip=0 accountCargo=0 selfEquip=0 gold=0 coin=0 winPoint=0 cera=0 avatarCoin=0 tailBytes=0 nonOkEntries=0";

        Assert.Equal(zeroLine, OnlineItemSyncLog.FormatProbeLine(null));
        Assert.Equal(zeroLine, OnlineItemSyncLog.FormatProbeLine(ItemSyncParser.Parse(Array.Empty<DecodedFrame>())));
    }

    [Fact]
    public void FormatProbeLine_sums_all_eleven_tail_sources_and_all_spaces_with_distinct_counts()
    {
        // 用回调解出 PetBag(3) 与 BodyEquip(4),并用互不相同的条数与素数尾字节验证每一项都不会串槽或漏加:
        //   bag=1, avatarBag=2, cargo=5, petBag=3, bodyEquip=4, accountCargo=6, selfEquip=7
        //   tailBytes 素数尾字节:
        //     Cera(5) + AvatarCoin(7) + CoolTime(11) + Effect(13) + ItemLocks(17) + MaxEquipLevel(19)
        //     + HotkeyOption(23) + PublicViews(29 + 31) + SelfView(37) + ExpandEquips(41 + 43) + Creatures(47)
        //     = 323
        DictionaryKindResolver resolver = new DictionaryKindResolver()
            .With(ItemA, ResolvedItemKind.Other)
            .With(ItemB, ResolvedItemKind.Other)
            .With(ItemC, ResolvedItemKind.Other);

        byte[] publicViewA = PublicViewPacket(1, 901, Encoding.ASCII.GetBytes("n901"), new byte[] { 1, 2, 3, 4, 5, 6 }, 1, new byte[29], (10, ItemA, 0));
        byte[] publicViewB = PublicViewPacket(1, 902, Encoding.ASCII.GetBytes("n902"), new byte[] { 1, 2, 3, 4, 5, 6 }, 1, new byte[31], (11, ItemB, 0));
        byte[] selfView = SelfViewPacket(1, 1, 2, new byte[4], 0, new byte[37],
            Item(10, ItemA), Item(11, ItemA), Item(12, ItemA), Item(13, ItemA), Item(14, ItemA), Item(15, ItemA), Item(16, ItemA));

        DecodedFrame[] frames =
        {
            FrameNoti(SpaceId, BagSpacePacket(8, 10, 20, 30, Item(5, ItemA)), extraTail: 1),
            FrameNoti(SpaceId, AvatarSpacePacket(AvatarItem(1, ItemA), AvatarItem(2, ItemB)), extraTail: 1),
            FrameNoti(SpaceId, CargoSpacePacket(16, Item(0, ItemA), Item(1, ItemA), Item(2, ItemA), Item(3, ItemA), Item(4, ItemA)), extraTail: 1),
            FrameNoti(SpaceId, PetSpacePacket(Item(0, ItemA), Item(1, ItemB), Item(2, ItemC)), extraTail: 1),
            FrameNoti(UpdateId, UpdatePacket(3, Item(10, ItemA), Item(11, ItemA), Item(12, ItemB), Item(13, ItemC)), extraTail: 1),
            FrameNoti(SpaceId, AccountCargoSpacePacket(1, 2, Item(0, ItemA), Item(1, ItemA), Item(2, ItemA), Item(3, ItemA), Item(4, ItemA), Item(5, ItemA)), extraTail: 1),
            FrameNoti(CeraId, CeraBody(1, 2, 3), extraTail: 5),
            FrameCmd(CoinId, CoinOkBody(4), extraTail: 7),
            FrameNoti(CoolTimeId, new byte[] { 0 }, extraTail: 11),
            FrameNoti(EffectId, new byte[] { 0 }, extraTail: 13),
            FrameNoti(LockId, new byte[] { 0, 0 }, extraTail: 17),
            FrameNoti(MaxLevelId, new byte[] { 80, 0 }, extraTail: 19),
            FrameNoti(HotkeyId, HotkeyBody(), extraTail: 23),
            UserInfoTestData.DecodedFrameOf(publicViewA),
            UserInfoTestData.DecodedFrameOf(publicViewB),
            UserInfoTestData.DecodedFrameOf(selfView),
            FrameNoti(ExpandId, ExpandBody(901), extraTail: 41),
            FrameNoti(ExpandId, ExpandBody(902), extraTail: 43),
            FrameNoti(PetTableId, OnePetTable(9001), extraTail: 47),
        };

        ItemSyncSnapshot snapshot = ItemSyncParser.Parse(frames, resolver);

        Assert.Equal(
            "PROBE_ITEMS seen=19 parsed=19 malformed=0 unresolved=0 bag=1 avatarBag=2 cargo=5 petBag=3 bodyEquip=4 accountCargo=6 selfEquip=7 gold=1 coin=1 winPoint=1 cera=1 avatarCoin=1 tailBytes=323 nonOkEntries=0",
            OnlineItemSyncLog.FormatProbeLine(snapshot));
    }

    [Theory]
    [InlineData(true, false, false, "gold=1 coin=0 winPoint=0")]
    [InlineData(false, true, false, "gold=0 coin=1 winPoint=0")]
    [InlineData(false, false, true, "gold=0 coin=0 winPoint=1")]
    [InlineData(false, false, false, "gold=0 coin=0 winPoint=0")]
    public void FormatProbeLine_reports_each_currency_slot_flag_independently(bool hasGold, bool hasCoin, bool hasWinPoint, string expectedSlice)
    {
        var entries = new List<ItemSpec>();
        if (hasGold) entries.Add(Pseudo(0, 100));
        if (hasCoin) entries.Add(Pseudo(1, 200));
        if (hasWinPoint) entries.Add(Pseudo(2, 300));
        DecodedFrame frame = FrameNoti(UpdateId, UpdatePacket(0, entries.ToArray()), extraTail: 1);

        ItemSyncSnapshot snapshot = ItemSyncParser.Parse(new[] { frame });

        Assert.Equal(
            "PROBE_ITEMS seen=1 parsed=1 malformed=0 unresolved=0 bag=0 avatarBag=0 cargo=0 petBag=0 bodyEquip=0 accountCargo=0 selfEquip=0 "
            + expectedSlice
            + " cera=0 avatarCoin=0 tailBytes=0 nonOkEntries=0",
            OnlineItemSyncLog.FormatProbeLine(snapshot));
    }

    [Fact]
    public void FormatProbeLine_handles_null_Currency_defensively()
    {
        var snapshot = new ItemSyncSnapshot(
            new Dictionary<ItemSpace, ItemSpaceState>(),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            3,
            1,
            1,
            0);

        Assert.Equal(
            "PROBE_ITEMS seen=3 parsed=1 malformed=1 unresolved=0 bag=0 avatarBag=0 cargo=0 petBag=0 bodyEquip=0 accountCargo=0 selfEquip=0 gold=0 coin=0 winPoint=0 cera=0 avatarCoin=0 tailBytes=0 nonOkEntries=0",
            OnlineItemSyncLog.FormatProbeLine(snapshot));
    }

    // ======== 5. 专门脱敏用例 ========

    [Fact]
    public async Task FormatProbeLine_never_leaks_item_ids_amounts_slots_upgrades_names_or_payload_bytes()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        var secretItem = new ItemSpec
        {
            Slot = 321,
            ItemId = 912345,
            Data = 881122,
            Ext1 = 29,
            Durability = 4455,
            Sealed = 77,
            Enchant = 4321098,
            AmpType = 42,
            AmpValue = 177,
            Separate = 66,
            Trade = 88,
        };
        byte[] secretName = Encoding.ASCII.GetBytes("SECRET_CHAR_909");
        byte[] publicView = PublicViewPacket(1, 9988, secretName, new byte[] { 1, 2, 3, 4, 5, 6 }, 1, new byte[4], (10, 912345, 0x3A));
        byte[] lockWithExtra = new NoticePayloadBuilder().U16(1).U8(0).U16(234).U8(2).U32(556677).ToArray();
        byte[] petWithUid = new NoticePayloadBuilder().U8(1).U32(998877).U8(95).U8(1).U32(667788).U8(33).U32(4).Raw(0x5A, 0x5B, 0x5C, 0x5D).U8(1).ToArray();

        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(),
            slot => HostTestKit.Frames(
                DungeonEntryWire.Notice(124, new byte[] { 0 }),
                NotiDirect(SpaceId, BagSpacePacket(8, 9876543, 7654321, 6543210, secretItem)),
                NotiDirect(CeraId, CeraBody(8765432, 7766554, 6655443)),
                CmdDirect(CoinId, CoinOkBody(5432109)),
                DungeonEntryWire.Notice(2, UserInfoTestData.Zlib(publicView)),
                NotiDirect(LockId, lockWithExtra),
                NotiDirect(PetTableId, petWithUid)));

        OnlineEntryResult result = await HostTestKit.EnterTown(session);
        Assert.True(result.Entered);

        string line = OnlineItemSyncLog.FormatProbeLine(result.ItemSync);
        Assert.StartsWith("PROBE_ITEMS ", line);

        string[] forbiddenTokens =
        {
            "9876543", "7654321", "6543210",
            "912345", "321", "881122", "4455", "4321098", "177",
            "8765432", "7766554", "6655443", "5432109",
            "SECRET", "909", "9988", "234", "556677", "998877", "667788",
        };
        foreach (string token in forbiddenTokens)
        {
            Assert.DoesNotContain(token, line);
        }
    }
}
