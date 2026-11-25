using Dnf.Game;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using SynthItemWire = Dnf.Client.Tests.OnlineItemSyncAdapterTests.SynthItemWire;

namespace Dnf.Client.Tests;

/// <summary>
/// Stage 2 城镇实时物品同步(<see cref="OnlineItemLiveSync"/> + <see cref="OnlineTownController.Poll"/>)测试:
/// 后台帧回调只入队不直接改动 <see cref="HeroProgress"/>,主线程 <c>Poll</c> 排空按线序应用
/// (<c>NOTI/14</c> upsert/delete/货币刷新、<c>NOTI/13</c> 整空间覆盖、畸形包忽略、Dispose 成对退订)。
/// </summary>
public sealed class OnlineItemLiveSyncTests
{
    private const int WeaponCode = 900001;
    private const int CoatCode = 900003;
    private const int HatAvatarCode = 900004;
    private const int CreatureCode = 900005;
    private const int PotionCode = 901001;
    private const int MaterialCode = 901002;
    private const int UnknownCode = 909999;

    private static void DispatchToSessionSubscribers(OnlineClientSession session, PacketClass cls, int packetId, byte[] payload)
    {
        OnlineReceivedFrame frame = NetObjects.Frame(cls, packetId, payload);
        foreach (Delegate d in SessionProbe.Handlers(session, "FrameReceived"))
        {
            ((Action<OnlineReceivedFrame>)d)(frame);
        }
    }

    [Fact]
    public void Live_sync_implements_kind_resolver_via_online_item_kind_lookup()
    {
        using var assets = new OnlineItemSyncAdapterTests.SyntheticBindAssets();
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        using var live = new OnlineItemLiveSync(session, null, null, assets.Assets);

        Assert.Equal(ResolvedItemKind.Other, live.Resolve(WeaponCode));
        Assert.Equal(ResolvedItemKind.Other, live.Resolve(PotionCode));
        Assert.Equal(ResolvedItemKind.Avatar, live.Resolve(HatAvatarCode));
        Assert.Equal(ResolvedItemKind.Pet, live.Resolve(CreatureCode));
        Assert.Equal(ResolvedItemKind.Unknown, live.Resolve(UnknownCode));
    }

    [Fact]
    public async Task Background_frame_received_only_enqueues_and_poll_applies_noti14_upsert_delete_currency_and_body_equip_in_wire_order()
    {
        using var assets = new OnlineItemSyncAdapterTests.SyntheticBindAssets();
        ItemSyncSnapshot initial = ItemSyncParser.Parse(new[]
        {
            SynthItemWire.FrameOf(PacketClass.Noti, ItemListParser.PacketId,
                SynthItemWire.BagSpacePacket(40, 1000, 0, 0,
                    new SynthItemWire.ItemSpec { Slot = 3, ItemId = PotionCode, Data = 5 },
                    new SynthItemWire.ItemSpec { Slot = 9, ItemId = MaterialCode, Data = 20 })),
        });

        using var rig = new TownHostRig(_ =>
            new OnlineSelectReport(true, false, null, OnlineTownHostTests.SelectPayload(1203, 1), null, null, initial),
            realSession: true);
        rig.EnterInGame(0);
        OnlineClientSession session = rig.Host.Session;
        Assert.NotNull(session);

        using OnlineTownController controller = OnlineTownController.Create(rig.Host, OnlineTownHostTests.BuildAtlas(), log: rig.Log);
        Assert.Equal(1, SessionProbe.FrameReceivedCount(session));

        HeroProgress progress = HeroProgress.CreateForOnlineHero(assets.Assets, controller.Profile);
        int equipChanges = 0;
        controller.BindOnlineItems(progress, assets.Assets, () => equipChanges++, applyInitial: true);
        Assert.Equal(2, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(1000L, progress.Gold);
        Assert.Equal(5, progress.QuickCountAt(0));
        Assert.Equal(20, progress.Bag.CountOf(MaterialCode));

        // 后台线程推入 NOTI/14:
        // 1) 背包同一包裹内对槽 9 先删后放、对槽 10 放后删,并把金币更新为 4321
        byte[] bagUpdate = SynthItemWire.UpdatePacket(
            (int)ItemSpace.Bag,
            SynthItemWire.Pseudo(0, 4321),
            new SynthItemWire.ItemSpec { Slot = 9, ItemId = SynthItemWire.DeleteMarker },
            new SynthItemWire.ItemSpec { Slot = 9, ItemId = MaterialCode, Data = 99 },
            new SynthItemWire.ItemSpec { Slot = 10, ItemId = MaterialCode, Data = 50 },
            new SynthItemWire.ItemSpec { Slot = 10, ItemId = SynthItemWire.DeleteMarker },
            new SynthItemWire.ItemSpec { Slot = 3, ItemId = SynthItemWire.DeleteMarker });

        // 2) 身上装备 NOTI/14 (需要 IItemKindResolver 识别 Weapon/Avatar/Pet)
        byte[] bodyUpdate = SynthItemWire.UpdatePacket(
            (int)ItemSpace.BodyEquip,
            new SynthItemWire.ItemSpec { Slot = 0, ItemId = HatAvatarCode, SocketBlock = SynthItemWire.Socket(), ExpansionBlock = SynthItemWire.Expansion() },
            new SynthItemWire.ItemSpec { Slot = 10, ItemId = WeaponCode, Data = 90, Ext1 = 10 },
            new SynthItemWire.ItemSpec { Slot = 12, ItemId = CoatCode, Data = 40 });

        await Task.Run(() =>
        {
            DispatchToSessionSubscribers(session, PacketClass.Noti, ItemUpdateParser.PacketId, bagUpdate);
            DispatchToSessionSubscribers(session, PacketClass.Noti, ItemUpdateParser.PacketId, bodyUpdate);
        }).WaitAsync(TimeSpan.FromSeconds(10));

        // 尚未在主线程 Poll 之前,HeroProgress 绝不能被后台线程改动
        Assert.Equal(1000L, progress.Gold);
        Assert.Equal(5, progress.QuickCountAt(0));
        Assert.Equal(20, progress.Bag.CountOf(MaterialCode));
        Assert.Null(progress.Worn.Get(EquipmentParts.Weapon));
        Assert.False(controller.Profile.EquipsSynced);

        // 主线程 Poll 消费队列并应用到 HeroProgress 与 Profile
        controller.Poll(rig.Now += 16);

        Assert.Equal(4321L, progress.Gold);
        Assert.Equal(0, progress.QuickCountAt(0));
        Assert.Equal(0, progress.QuickFilled);
        Assert.Equal(99, progress.Bag.CountOf(MaterialCode)); // 槽 9 被替换成 99,槽 10 先放后删被清空
        Assert.Equal(WeaponCode, progress.Worn.Get(EquipmentParts.Weapon)?.Code);
        Assert.Equal(10, progress.Worn.Get(EquipmentParts.Weapon)?.Instance?.Upgrade);
        Assert.Equal(CoatCode, progress.Worn.Get(EquipmentParts.Coat)?.Code);
        Assert.Equal(1, equipChanges);
        Assert.True(controller.Profile.GoldSynced);
        Assert.True(controller.Profile.InventorySynced);
        Assert.True(controller.Profile.EquipsSynced);

        // 仅改金币与背包、不动装备栏的 NOTI/14:不应误触发装备变化回调
        byte[] goldAndBagOnly = SynthItemWire.UpdatePacket(
            (int)ItemSpace.Bag,
            SynthItemWire.Pseudo(0, 5678),
            new SynthItemWire.ItemSpec { Slot = 9, ItemId = MaterialCode, Data = 101 });
        await Task.Run(() => DispatchToSessionSubscribers(session, PacketClass.Noti, ItemUpdateParser.PacketId, goldAndBagOnly))
            .WaitAsync(TimeSpan.FromSeconds(10));
        controller.Poll(rig.Now += 16);

        Assert.Equal(5678L, progress.Gold);
        Assert.Equal(101, progress.Bag.CountOf(MaterialCode));
        Assert.Equal(1, equipChanges);

        // 再推一条 BodyEquip 删除槽 10
        byte[] unequipWeapon = SynthItemWire.UpdatePacket(
            (int)ItemSpace.BodyEquip,
            new SynthItemWire.ItemSpec { Slot = 10, ItemId = SynthItemWire.DeleteMarker });
        await Task.Run(() => DispatchToSessionSubscribers(session, PacketClass.Noti, ItemUpdateParser.PacketId, unequipWeapon))
            .WaitAsync(TimeSpan.FromSeconds(10));
        controller.Poll(rig.Now += 16);

        Assert.Null(progress.Worn.Get(EquipmentParts.Weapon));
        Assert.Equal(CoatCode, progress.Worn.Get(EquipmentParts.Coat)?.Code);
        Assert.Equal(2, equipChanges);
    }

    [Fact]
    public void Noti13_overwrites_prior_incremental_state_and_malformed_frames_do_not_corrupt_shadow_state()
    {
        using var assets = new OnlineItemSyncAdapterTests.SyntheticBindAssets();
        using OnlineClientSession session = SessionProbe.NewUnconnected();
        using var live = new OnlineItemLiveSync(session, null, null, assets.Assets);
        var profile = OnlineHeroProfile.FromEntry(
            new OnlineCharacterEntry(9401, System.Text.Encoding.UTF8.GetBytes("测试"), 0, 0, 0, 20, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            0);
        HeroProgress progress = HeroProgress.CreateForOnlineHero(assets.Assets, profile);

        // 先收到仅含货币槽 0 的 NOTI/14: 只更新金币,不应错误把未同步的背包当成已清空
        byte[] goldOnly = SynthItemWire.UpdatePacket((int)ItemSpace.Bag, SynthItemWire.Pseudo(0, 555));
        DispatchToSessionSubscribers(session, PacketClass.Noti, ItemUpdateParser.PacketId, goldOnly);
        OnlineItemSyncReport goldReport = live.DrainAndApply(progress, profile);

        Assert.NotNull(goldReport);
        Assert.True(goldReport.GoldSynced);
        Assert.False(goldReport.InventorySynced);
        Assert.Equal(555L, progress.Gold);
        Assert.True(profile.GoldSynced);
        Assert.False(profile.InventorySynced);

        // 再收到 NOTI/14 放进槽 9 物品
        byte[] addSlot9 = SynthItemWire.UpdatePacket((int)ItemSpace.Bag, new SynthItemWire.ItemSpec { Slot = 9, ItemId = MaterialCode, Data = 33 });
        DispatchToSessionSubscribers(session, PacketClass.Noti, ItemUpdateParser.PacketId, addSlot9);
        OnlineItemSyncReport incReport = live.DrainAndApply(progress, profile);
        Assert.NotNull(incReport);
        Assert.True(incReport.InventorySynced);
        Assert.Equal(33, progress.Bag.CountOf(MaterialCode));

        // 畸形 NOTI/13、畸形 NOTI/14、不支持的帧都安全忽略,不冲掉已有影子状态
        DispatchToSessionSubscribers(session, PacketClass.Noti, ItemListParser.PacketId, new byte[] { 0x00, 0x01 });
        DispatchToSessionSubscribers(session, PacketClass.Noti, ItemUpdateParser.PacketId, new byte[] { 0x03, 0x01, 0x00 });
        DispatchToSessionSubscribers(session, PacketClass.Cmd, ItemListParser.PacketId, SynthItemWire.BagSpacePacket(40, 1, 0, 0));

        Assert.Null(live.DrainAndApply(progress, profile));
        Assert.Equal(2, live.IgnoredFrames);
        Assert.Equal(555L, progress.Gold);
        Assert.Equal(33, progress.Bag.CountOf(MaterialCode));

        // 完整 NOTI/13 整体覆盖先前增量状态(槽 9 旧物品被替掉,金币更新为 7777)
        byte[] fullBag = SynthItemWire.BagSpacePacket(48, 7777, 0, 0,
            new SynthItemWire.ItemSpec { Slot = 3, ItemId = PotionCode, Data = 9 });
        DispatchToSessionSubscribers(session, PacketClass.Noti, ItemListParser.PacketId, fullBag);
        OnlineItemSyncReport fullReport = live.DrainAndApply(progress, profile);

        Assert.NotNull(fullReport);
        Assert.Equal(7777L, progress.Gold);
        Assert.Equal(0, progress.Bag.CountOf(MaterialCode));
        Assert.Equal(9, progress.Bag.CountOf(PotionCode));
        Assert.Equal(9, progress.QuickCountAt(0));
    }

    [Fact]
    public void Controller_dispose_unsubscribes_live_sync_cleanly_and_ignores_late_frames()
    {
        using var assets = new OnlineItemSyncAdapterTests.SyntheticBindAssets();
        ItemSyncSnapshot initial = ItemSyncParser.Parse(new[]
        {
            SynthItemWire.FrameOf(PacketClass.Noti, ItemListParser.PacketId,
                SynthItemWire.BagSpacePacket(40, 777, 0, 0)),
        });
        using var rig = new TownHostRig(_ =>
            new OnlineSelectReport(true, false, null, OnlineTownHostTests.SelectPayload(1203, 1), null, null, initial),
            realSession: true);
        rig.EnterInGame(0);
        OnlineClientSession session = rig.Host.Session;
        Assert.NotNull(session);

        OnlineTownController controller = OnlineTownController.Create(rig.Host, OnlineTownHostTests.BuildAtlas(), log: rig.Log);
        HeroProgress progress = HeroProgress.CreateForOnlineHero(assets.Assets, controller.Profile);
        controller.BindOnlineItems(progress, assets.Assets);
        Assert.Equal(777L, progress.Gold);
        Assert.Equal(2, SessionProbe.FrameReceivedCount(session));

        controller.Dispose();

        Assert.Equal(0, SessionProbe.FrameReceivedCount(session));
        Assert.Equal(0, SessionProbe.DisconnectedCount(session));

        byte[] bagUpdate = SynthItemWire.UpdatePacket((int)ItemSpace.Bag, SynthItemWire.Pseudo(0, 99999));
        DispatchToSessionSubscribers(session, PacketClass.Noti, ItemUpdateParser.PacketId, bagUpdate);
        controller.Poll(rig.Now += 16);

        Assert.Equal(777L, progress.Gold);
    }

    [Fact]
    public void Controller_poll_skips_live_item_drain_while_exiting()
    {
        using var assets = new OnlineItemSyncAdapterTests.SyntheticBindAssets();
        using var rig = new TownHostRig(realSession: true);
        rig.EnterInGame(0);
        OnlineClientSession session = rig.Host.Session;
        Assert.NotNull(session);

        HeroProgress progress = null;
        OnlineTownController controller = null;
        bool checkedDuringExit = false;
        controller = OnlineTownController.Create(rig.Host, OnlineTownHostTests.BuildAtlas(), log: line =>
        {
            rig.Log(line);
            if (line.Contains("target=charselect outcome=requested"))
            {
                // ReturnToCharacterSelect 已把 _exiting 置位、尚未 Dispose: 此时有入队帧且 Poll 不得消费
                byte[] bagUpdate = SynthItemWire.UpdatePacket((int)ItemSpace.Bag, SynthItemWire.Pseudo(0, 88888));
                DispatchToSessionSubscribers(session, PacketClass.Noti, ItemUpdateParser.PacketId, bagUpdate);
                controller.Poll(rig.Now += 16);
                checkedDuringExit = true;
            }
        });
        progress = HeroProgress.CreateForOnlineHero(assets.Assets, controller.Profile);
        controller.BindOnlineItems(progress, assets.Assets);

        controller.ReturnToCharacterSelect();

        Assert.True(checkedDuringExit);
        Assert.Equal(0L, progress.Gold);
    }
}
