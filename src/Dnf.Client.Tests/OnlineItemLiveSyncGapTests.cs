using Dnf.Game;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using SynthItemWire = Dnf.Client.Tests.OnlineItemSyncAdapterTests.SynthItemWire;

namespace Dnf.Client.Tests;

/// <summary>
/// <see cref="OnlineItemLiveSync"/> 补测(r20fix-onlineitems):既有用例只走背包与身上装备,这里钉住
/// ① 初始快照 <c>Seed</c> 里的身上装备(BodyEquip 与 SelfView)、装扮背包/仓库/宠物栏/账号仓库四个"未建模空间"与备用装备组;
/// ② NOTI/13 整空间包、NOTI/14 增量包落到四个未建模空间(只维护槽位集合,以计数投影)的全部分支;
/// ③ "没有任何有效变化的包"(空的背包增量、非金币的货币刷新)不触发投影;
/// ④ 帧过滤、退订后的静默、解析器拒绝整包时不半个生效。
/// 全用合成编号(900+/901+)与内存里的未连接会话,没有任何网络与墙钟依赖;预期值全是手算字面量。
/// </summary>
public sealed class OnlineItemLiveSyncGapTests
{
    // 与 SyntheticBindAssets 登记的物品表一致:900001 武器、900003 上衣、900004 装扮、900005 宠物、901001 药水、901002 材料
    private const int WeaponCode = 900001;
    private const int CoatCode = 900003;
    private const int HatAvatarCode = 900004;
    private const int CreatureCode = 900005;
    private const int PotionCode = 901001;
    private const int MaterialCode = 901002;
    private const int UnknownCode = 909999;

    // 只出现在影子槽表里(不进合成 PVF)的装备编号:让初始快照里"哪一条胜出"一眼可辨
    private const int SelfViewPart9Code = 900041;
    private const int SelfViewPart10Code = 900042;
    private const int SelfViewPart12Code = 900043;
    private const int SelfViewPart22Code = 900044;
    private const int BodyPart9Code = 900051;
    private const int BodyPart10Code = 900052;
    private const int BodyPart21Code = 900053;
    private const int BodyPart22Code = 900054;

    // ───────────────────────── 夹具 ─────────────────────────

    /// <summary>解析初始快照用的物品类型回调:装扮、宠物、未知各有专用编号,其余都是普通物品。</summary>
    private sealed class SeedKinds : IItemKindResolver
    {
        public ResolvedItemKind Resolve(uint itemId)
        {
            switch (itemId)
            {
                case HatAvatarCode:
                    return ResolvedItemKind.Avatar;
                case CreatureCode:
                    return ResolvedItemKind.Pet;
                case UnknownCode:
                    return ResolvedItemKind.Unknown;
                default:
                    return ResolvedItemKind.Other;
            }
        }
    }

    /// <summary>覆盖解析器:只认登记过的编号,没登记的一律是 Unknown。</summary>
    private sealed class FixedKinds : IItemKindResolver
    {
        private readonly Dictionary<uint, ResolvedItemKind> _kinds = new Dictionary<uint, ResolvedItemKind>();

        public FixedKinds With(long itemId, ResolvedItemKind kind)
        {
            _kinds[(uint)itemId] = kind;
            return this;
        }

        public ResolvedItemKind Resolve(uint itemId)
        {
            return _kinds.TryGetValue(itemId, out ResolvedItemKind kind) ? kind : ResolvedItemKind.Unknown;
        }
    }

    /// <summary>同步器 + 假会话 + 合成资产 + 联网主角;<c>Full</c>/<c>Update</c> 推一个帧并在"主线程"(本线程)排空。</summary>
    private sealed class Rig : IDisposable
    {
        private readonly OnlineItemSyncAdapterTests.SyntheticBindAssets _assets = new OnlineItemSyncAdapterTests.SyntheticBindAssets();

        public Rig(ItemSyncSnapshot snapshot, ushort? selfUid = null, IItemKindResolver overrideResolver = null)
        {
            Session = SessionProbe.NewUnconnected();
            Live = overrideResolver == null
                ? new OnlineItemLiveSync(Session, snapshot, selfUid, _assets.Assets)
                : new OnlineItemLiveSync(Session, snapshot, selfUid, _assets.Assets, overrideResolver);
            OnlineHeroProfile profile = OnlineHeroProfile.FromEntry(
                new OnlineCharacterEntry(9401, System.Text.Encoding.ASCII.GetBytes("synth"), 0, 0, 0, 20, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                0);
            Progress = HeroProgress.CreateForOnlineHero(_assets.Assets, profile);
        }

        public OnlineClientSession Session { get; }

        public OnlineItemLiveSync Live { get; }

        public HeroProgress Progress { get; }

        public void Push(int packetId, byte[] payload)
        {
            Dispatch(Session, PacketClass.Noti, packetId, payload);
        }

        public OnlineItemSyncReport Full(byte[] payload)
        {
            Push(ItemListParser.PacketId, payload);
            return Live.DrainAndApply(Progress);
        }

        public OnlineItemSyncReport Update(byte[] payload)
        {
            Push(ItemUpdateParser.PacketId, payload);
            return Live.DrainAndApply(Progress);
        }

        public void Dispose()
        {
            Live.Dispose();
            Session.Dispose();
            _assets.Dispose();
        }
    }

    private static void Dispatch(OnlineClientSession session, PacketClass cls, int packetId, byte[] payload)
    {
        OnlineReceivedFrame frame = NetObjects.Frame(cls, packetId, payload);
        foreach (Delegate d in SessionProbe.Handlers(session, "FrameReceived"))
        {
            ((Action<OnlineReceivedFrame>)d)(frame);
        }
    }

    private static DecodedFrame Full(byte[] payload) => SynthItemWire.FrameOf(PacketClass.Noti, ItemListParser.PacketId, payload);

    private static DecodedFrame Update(byte[] payload) => SynthItemWire.FrameOf(PacketClass.Noti, ItemUpdateParser.PacketId, payload);

    private static DecodedFrame SelfView(params SynthItemWire.ItemSpec[] equips) =>
        SynthItemWire.FrameOf(PacketClass.Noti, UserInfoViewParser.PacketId,
            SynthItemWire.SelfViewPacket(1, 1, 2, new byte[4], 0, Array.Empty<byte>(), equips));

    /// <summary>把一串帧归约成初始快照,并自检每一帧都被解析器接受(免得后面的断言在一个残缺快照上空转)。</summary>
    private static ItemSyncSnapshot Parse(params DecodedFrame[] frames)
    {
        ItemSyncSnapshot snapshot = ItemSyncParser.Parse(frames, new SeedKinds());
        Assert.Equal(frames.Length, snapshot.FramesParsed);
        Assert.Equal(0, snapshot.FramesMalformed);
        return snapshot;
    }

    private static SynthItemWire.ItemSpec Spec(int slot, long itemId, long data, int ext1 = 0) =>
        new SynthItemWire.ItemSpec { Slot = slot, ItemId = itemId, Data = data, Ext1 = ext1 };

    /// <summary>某个未建模空间里"放进一格"的条目(各空间的线格式要求不同:装扮背包带两个块,宠物栏的宠物带剩余时长)。</summary>
    private static SynthItemWire.ItemSpec Put(ItemSpace space, int slot)
    {
        switch (space)
        {
            case ItemSpace.AvatarBag:
                return new SynthItemWire.ItemSpec
                {
                    Slot = slot,
                    ItemId = HatAvatarCode,
                    Data = 30,
                    SocketBlock = SynthItemWire.Socket(),
                    ExpansionBlock = SynthItemWire.Expansion(),
                };
            case ItemSpace.PetBag:
                return new SynthItemWire.ItemSpec { Slot = slot, ItemId = CreatureCode, PetTime = 1234 };
            case ItemSpace.Cargo:
            case ItemSpace.AccountCargo:
                return new SynthItemWire.ItemSpec { Slot = slot, ItemId = MaterialCode, Data = 7 };
            default:
                throw new ArgumentOutOfRangeException(nameof(space));
        }
    }

    /// <summary>某个未建模空间里"清空一格"的条目(装扮背包的线格式不论是不是删除标记都带两个块)。</summary>
    private static SynthItemWire.ItemSpec Remove(ItemSpace space, int slot)
    {
        var spec = new SynthItemWire.ItemSpec { Slot = slot, ItemId = SynthItemWire.DeleteMarker };
        if (space == ItemSpace.AvatarBag)
        {
            spec.SocketBlock = SynthItemWire.Socket();
            spec.ExpansionBlock = SynthItemWire.Expansion();
        }
        return spec;
    }

    private static byte[] FullPacket(ItemSpace space, params SynthItemWire.ItemSpec[] items)
    {
        switch (space)
        {
            case ItemSpace.AvatarBag:
                return SynthItemWire.AvatarSpacePacket(items);
            case ItemSpace.Cargo:
                return SynthItemWire.CargoSpacePacket(24, items);
            case ItemSpace.PetBag:
                return SynthItemWire.PetSpacePacket(items);
            case ItemSpace.AccountCargo:
                return SynthItemWire.AccountCargoSpacePacket(16, 1000, items);
            default:
                throw new ArgumentOutOfRangeException(nameof(space));
        }
    }

    private static byte[] UpdatePacket(ItemSpace space, params SynthItemWire.ItemSpec[] items) =>
        SynthItemWire.UpdatePacket((int)space, items);

    /// <summary>
    /// 一个没有金币槽的整份背包包:货币伪条目(槽 0、1、2)只出现调用方给的那几个。
    /// 整份包头是 u8 空间号 + u16 容量 + u16 个数,增量包头是 u8 空间号 + u16 个数,条目写法完全相同,所以在个数前补上容量即可。
    /// </summary>
    private static byte[] BagFullWithoutGold(int capacity, params SynthItemWire.ItemSpec[] entries)
    {
        byte[] update = SynthItemWire.UpdatePacket((int)ItemSpace.Bag, entries);
        var full = new List<byte> { (byte)ItemSpace.Bag, (byte)capacity, (byte)(capacity >> 8) };
        full.AddRange(update.Skip(1));
        return full.ToArray();
    }

    /// <summary>进度基线:金币 1000、快捷槽 0 里 5 瓶药水、20 份材料、身上武器(强化 6);推送未建模空间的包之后它们必须一字不变。</summary>
    private static ItemSyncSnapshot BaseSnapshot(params DecodedFrame[] extra)
    {
        var frames = new List<DecodedFrame>
        {
            Full(SynthItemWire.BagSpacePacket(48, 1000, 7, 9,
                Spec(3, PotionCode, 5),
                Spec(9, MaterialCode, 20))),
            Update(SynthItemWire.UpdatePacket((int)ItemSpace.BodyEquip, Spec(10, WeaponCode, 90, 6))),
        };
        frames.AddRange(extra);
        return Parse(frames.ToArray());
    }

    private static void AssertBaseline(HeroProgress progress)
    {
        Assert.Equal(1000L, progress.Gold);
        Assert.Equal(5, progress.QuickCountAt(0));
        Assert.Equal(5, progress.Bag.CountOf(PotionCode));
        Assert.Equal(20, progress.Bag.CountOf(MaterialCode));
        Assert.Equal(WeaponCode, progress.Worn.Get(EquipmentParts.Weapon)?.Code);
        Assert.Equal(6, progress.Worn.Get(EquipmentParts.Weapon)?.Instance?.Upgrade);
        Assert.Equal(1, progress.Worn.EquippedCount);
    }

    private static int[] Counts(OnlineItemSyncData data) => new[]
    {
        data.UnmodeledAvatarBagCount, data.UnmodeledCargoCount, data.UnmodeledPetBagCount, data.UnmodeledAccountCargoCount,
    };

    private static int[] Counts(OnlineItemSyncReport report) => new[]
    {
        report.UnmodeledAvatarBagCount, report.UnmodeledCargoCount, report.UnmodeledPetBagCount, report.UnmodeledAccountCargoCount,
    };

    /// <summary>四个未建模空间的计数依次是 装扮背包、仓库、宠物栏、账号仓库。</summary>
    private static void AssertUnmodeled(OnlineItemSyncData data, int avatarBag, int cargo, int petBag, int accountCargo)
    {
        Assert.Equal(new[] { avatarBag, cargo, petBag, accountCargo }, Counts(data));
    }

    /// <summary>只有 <paramref name="space"/> 的计数是 <paramref name="expected"/>,其余三个空间必须是 0(空间互相隔离)。</summary>
    private static int[] Only(ItemSpace space, int expected) => new[]
    {
        space == ItemSpace.AvatarBag ? expected : 0,
        space == ItemSpace.Cargo ? expected : 0,
        space == ItemSpace.PetBag ? expected : 0,
        space == ItemSpace.AccountCargo ? expected : 0,
    };

    private static void AssertOnly(OnlineItemSyncData data, ItemSpace space, int expected)
    {
        Assert.Equal(Only(space, expected), Counts(data));
    }

    private static void AssertOnly(OnlineItemSyncReport report, ItemSpace space, int expected)
    {
        Assert.Equal(Only(space, expected), Counts(report));
    }

    private static void AssertWorn(OnlineItemSlotEntry actual, int slot, int code, int upgrade, int grade)
    {
        Assert.Equal((ushort)slot, actual.Slot);
        Assert.Equal((uint)code, actual.ItemCode);
        Assert.Equal(1, actual.Count);            // 身上装备恒为 1 件,与 Data 位无关
        Assert.Equal(upgrade, actual.UpgradeLevel);
        Assert.Equal(grade, actual.GradeValue);
    }

    // ───────────────────────── 簇 A:初始快照(Seed) ─────────────────────────

    [Fact]
    public void Initial_snapshot_projects_each_unmodeled_space_count_from_its_own_entries()
    {
        ItemSyncSnapshot snapshot = Parse(
            Full(SynthItemWire.AvatarSpacePacket(Put(ItemSpace.AvatarBag, 2), Put(ItemSpace.AvatarBag, 5), Put(ItemSpace.AvatarBag, 9))),
            Full(SynthItemWire.CargoSpacePacket(24, Put(ItemSpace.Cargo, 1), Put(ItemSpace.Cargo, 7))),
            Full(SynthItemWire.PetSpacePacket(Put(ItemSpace.PetBag, 4))),
            Full(SynthItemWire.AccountCargoSpacePacket(16, 1000,
                Put(ItemSpace.AccountCargo, 0), Put(ItemSpace.AccountCargo, 3), Put(ItemSpace.AccountCargo, 6), Put(ItemSpace.AccountCargo, 11))));
        using var live = new OnlineItemLiveSync(null, snapshot, null, null);

        OnlineItemSyncData data = live.ProjectData();

        AssertUnmodeled(data, avatarBag: 3, cargo: 2, petBag: 1, accountCargo: 4);
        Assert.Equal(0, data.UnmodeledExpandEquipCount);
        // 这四个空间不是背包与装备:既不带出金币、背包、装备,也不把它们标成已同步
        Assert.False(data.HasGold);
        Assert.False(data.HasBagData);
        Assert.False(data.HasEquipData);
        Assert.Empty(data.BagEntries);
        Assert.Empty(data.QuickEntries);
        Assert.Empty(data.WornEquips);
    }

    [Theory]
    [InlineData(ItemSpace.AvatarBag, 3, 0, 0, 0)]
    [InlineData(ItemSpace.Cargo, 0, 3, 0, 0)]
    [InlineData(ItemSpace.PetBag, 0, 0, 3, 0)]
    [InlineData(ItemSpace.AccountCargo, 0, 0, 0, 3)]
    public void Initial_snapshot_with_a_single_unmodeled_space_leaves_the_other_three_at_zero(
        ItemSpace space, int avatarBag, int cargo, int petBag, int accountCargo)
    {
        ItemSyncSnapshot snapshot = Parse(Full(FullPacket(space, Put(space, 4), Put(space, 8), Put(space, 12))));
        using var live = new OnlineItemLiveSync(null, snapshot, null, null);

        AssertUnmodeled(live.ProjectData(), avatarBag, cargo, petBag, accountCargo);
    }

    [Fact]
    public void Initial_snapshot_with_present_but_empty_unmodeled_spaces_projects_zero_counts()
    {
        ItemSyncSnapshot snapshot = Parse(
            Full(SynthItemWire.AvatarSpacePacket()),
            Full(SynthItemWire.CargoSpacePacket(24)),
            Full(SynthItemWire.PetSpacePacket()),
            Full(SynthItemWire.AccountCargoSpacePacket(16, 1000)));
        // 四个空间的状态都在、只是没有条目:循环要在空列表上安全地一次也不进
        Assert.NotNull(snapshot.AvatarBag);
        Assert.NotNull(snapshot.Cargo);
        Assert.NotNull(snapshot.PetBag);
        Assert.NotNull(snapshot.AccountCargo);
        using var live = new OnlineItemLiveSync(null, snapshot, null, null);

        AssertUnmodeled(live.ProjectData(), 0, 0, 0, 0);
    }

    [Theory]
    [InlineData(ItemSpace.AvatarBag)]
    [InlineData(ItemSpace.Cargo)]
    [InlineData(ItemSpace.PetBag)]
    [InlineData(ItemSpace.AccountCargo)]
    public void Initial_snapshot_slot_numbers_are_the_ones_later_increments_upsert_and_delete(ItemSpace space)
    {
        ItemSyncSnapshot snapshot = Parse(Full(FullPacket(space, Put(space, 2), Put(space, 5), Put(space, 9))));
        using var rig = new Rig(snapshot);
        AssertOnly(rig.Live.ProjectData(), space, 3);

        // 重放已有的槽 9:集合语义,不重复计数
        rig.Update(UpdatePacket(space, Put(space, 9)));
        AssertOnly(rig.Live.ProjectData(), space, 3);

        // 清一个从没放过的槽 7:计数不变
        rig.Update(UpdatePacket(space, Remove(space, 7)));
        AssertOnly(rig.Live.ProjectData(), space, 3);

        // 清初始快照里的槽 5:计数 3 → 2(种进去的必须是条目的槽号,不是下标)
        Assert.NotNull(rig.Update(UpdatePacket(space, Remove(space, 5))));
        AssertOnly(rig.Live.ProjectData(), space, 2);

        // 清槽 2 与槽 9:计数归 0
        Assert.NotNull(rig.Update(UpdatePacket(space, Remove(space, 2), Remove(space, 9))));
        AssertOnly(rig.Live.ProjectData(), space, 0);

        // 未建模空间里槽 0 是普通槽(只有背包空间的槽 0、1、2 是货币伪条目)
        Assert.NotNull(rig.Update(UpdatePacket(space, Put(space, 0))));
        AssertOnly(rig.Live.ProjectData(), space, 1);
    }

    [Fact]
    public void Initial_body_equip_state_seeds_only_parts_ten_through_twentyone_in_ascending_order()
    {
        // 线上顺序故意打乱;9 与 22 是紧挨着合法区间的两个越界部位
        ItemSyncSnapshot snapshot = Parse(Update(SynthItemWire.UpdatePacket((int)ItemSpace.BodyEquip,
            Spec(22, BodyPart22Code, 59, 9),
            Spec(21, BodyPart21Code, 83, 5),
            Spec(10, BodyPart10Code, 71, 2),
            Spec(9, BodyPart9Code, 95, 7))));
        Assert.NotNull(snapshot.BodyEquip);
        Assert.Null(snapshot.SelfView);
        using var live = new OnlineItemLiveSync(null, snapshot, null, null);

        OnlineItemSyncData data = live.ProjectData();

        Assert.True(data.HasEquipData);
        Assert.False(data.HasBagData);
        Assert.False(data.HasGold);
        Assert.Collection(data.WornEquips,
            e => AssertWorn(e, slot: 10, code: BodyPart10Code, upgrade: 2, grade: 71),
            e => AssertWorn(e, slot: 21, code: BodyPart21Code, upgrade: 5, grade: 83));
    }

    [Fact]
    public void Initial_self_view_equips_seed_only_parts_ten_through_twentyone_in_ascending_order()
    {
        ItemSyncSnapshot snapshot = Parse(SelfView(
            Spec(22, SelfViewPart22Code, 59, 9),
            Spec(21, BodyPart21Code, 83, 5),
            Spec(10, SelfViewPart10Code, 71, 2),
            Spec(9, SelfViewPart9Code, 95, 7)));
        Assert.NotNull(snapshot.SelfView);
        Assert.Null(snapshot.BodyEquip);
        using var live = new OnlineItemLiveSync(null, snapshot, null, null);

        OnlineItemSyncData data = live.ProjectData();

        Assert.True(data.HasEquipData);
        Assert.False(data.HasBagData);
        Assert.Collection(data.WornEquips,
            e => AssertWorn(e, slot: 10, code: SelfViewPart10Code, upgrade: 2, grade: 71),
            e => AssertWorn(e, slot: 21, code: BodyPart21Code, upgrade: 5, grade: 83));
    }

    [Fact]
    public void Initial_equip_lists_holding_a_single_entry_keep_that_first_entry()
    {
        // 列表里只有一条、而且是下标 0 的那条:两个循环都必须从下标 0 开始,不能把它跳过
        ItemSyncSnapshot selfView = Parse(SelfView(Spec(12, SelfViewPart12Code, 33, 4)));
        using var fromSelfView = new OnlineItemLiveSync(null, selfView, null, null);
        AssertWorn(Assert.Single(fromSelfView.ProjectData().WornEquips), slot: 12, code: SelfViewPart12Code, upgrade: 4, grade: 33);

        ItemSyncSnapshot bodyEquip = Parse(Update(SynthItemWire.UpdatePacket((int)ItemSpace.BodyEquip, Spec(21, BodyPart21Code, 77, 12))));
        using var fromBodyEquip = new OnlineItemLiveSync(null, bodyEquip, null, null);
        AssertWorn(Assert.Single(fromBodyEquip.ProjectData().WornEquips), slot: 21, code: BodyPart21Code, upgrade: 12, grade: 77);
    }

    [Fact]
    public void Initial_body_equip_entry_overrides_the_self_view_entry_of_the_same_part()
    {
        ItemSyncSnapshot snapshot = Parse(
            SelfView(
                Spec(9, SelfViewPart9Code, 11, 1),
                Spec(10, SelfViewPart10Code, 22, 3),
                Spec(12, SelfViewPart12Code, 33, 4),
                Spec(22, SelfViewPart22Code, 44, 6)),
            Update(SynthItemWire.UpdatePacket((int)ItemSpace.BodyEquip,
                Spec(9, BodyPart9Code, 55, 8),
                Spec(10, BodyPart10Code, 66, 10),
                Spec(21, BodyPart21Code, 77, 12),
                Spec(22, BodyPart22Code, 88, 14))));
        using var live = new OnlineItemLiveSync(null, snapshot, null, null);

        OnlineItemSyncData data = live.ProjectData();

        Assert.True(data.HasEquipData);
        // 部位 10 以 BodyEquip 为准;部位 12 只有 SelfView 有,保留;21 只有 BodyEquip 有;9 与 22 两个越界部位两边都被丢
        Assert.Collection(data.WornEquips,
            e => AssertWorn(e, slot: 10, code: BodyPart10Code, upgrade: 10, grade: 66),
            e => AssertWorn(e, slot: 12, code: SelfViewPart12Code, upgrade: 4, grade: 33),
            e => AssertWorn(e, slot: 21, code: BodyPart21Code, upgrade: 12, grade: 77));
    }

    [Theory]
    [InlineData(4321, 3)]
    [InlineData(8765, 1)]
    [InlineData(1111, 0)]
    [InlineData(null, 4)]
    public void Initial_expand_equip_entries_are_counted_for_the_self_uid_only_when_it_is_known(int? selfUid, int expected)
    {
        ItemSyncSnapshot snapshot = Parse(
            SynthItemWire.FrameOf(PacketClass.Noti, ExpandEquipParser.PacketId,
                SynthItemWire.ExpandEquipPacket(4321, SynthItemWire.Item(10, WeaponCode), SynthItemWire.Item(12, CoatCode), SynthItemWire.Item(14, WeaponCode))),
            SynthItemWire.FrameOf(PacketClass.Noti, ExpandEquipParser.PacketId,
                SynthItemWire.ExpandEquipPacket(8765, SynthItemWire.Item(10, CoatCode))));
        using var live = new OnlineItemLiveSync(null, snapshot, (ushort?)selfUid, null);

        Assert.Equal(expected, live.ProjectData().UnmodeledExpandEquipCount);
    }

    [Fact]
    public void Projection_of_a_null_or_empty_initial_snapshot_carries_no_synced_data()
    {
        foreach (ItemSyncSnapshot snapshot in new[] { null, ItemSyncParser.Parse(Array.Empty<DecodedFrame>()) })
        {
            using var live = new OnlineItemLiveSync(null, snapshot, 1203, null);

            OnlineItemSyncData data = live.ProjectData();

            Assert.False(data.HasGold);
            Assert.Equal(0L, data.Gold);
            Assert.False(data.HasBagData);
            Assert.False(data.HasEquipData);
            Assert.Empty(data.BagEntries);
            Assert.Empty(data.QuickEntries);
            Assert.Empty(data.WornEquips);
            AssertUnmodeled(data, 0, 0, 0, 0);
            Assert.Equal(0, data.UnmodeledExpandEquipCount);
        }
    }

    [Theory]
    [InlineData(0u, 0L)]
    [InlineData(1u, 1L)]
    [InlineData(4294967295u, 4294967295L)]
    public void Initial_gold_is_projected_exactly_including_zero_and_the_unsigned_maximum(uint gold, long expected)
    {
        ItemSyncSnapshot snapshot = Parse(Full(SynthItemWire.BagSpacePacket(40, gold, 11, 22)));
        using var live = new OnlineItemLiveSync(null, snapshot, null, null);

        OnlineItemSyncData data = live.ProjectData();

        Assert.True(data.HasGold);
        Assert.Equal(expected, data.Gold);
        Assert.True(data.HasBagData);
        Assert.Empty(data.BagEntries);
        Assert.Empty(data.QuickEntries);
        Assert.False(data.HasEquipData);
    }

    [Fact]
    public void Initial_bag_without_a_gold_slot_projects_no_gold_but_keeps_the_bag()
    {
        // 只带硬币合计(槽 1)与胜点(槽 2)两个货币伪条目,再加一格材料
        ItemSyncSnapshot snapshot = Parse(Full(BagFullWithoutGold(40, SynthItemWire.Pseudo(1, 777), SynthItemWire.Pseudo(2, 888), Spec(12, MaterialCode, 33))));
        Assert.Null(snapshot.Currency.Gold);
        using var live = new OnlineItemLiveSync(null, snapshot, null, null);

        OnlineItemSyncData data = live.ProjectData();

        Assert.False(data.HasGold);
        Assert.Equal(0L, data.Gold);
        Assert.True(data.HasBagData);
        OnlineItemSlotEntry only = Assert.Single(data.BagEntries);
        Assert.Equal((ushort)12, only.Slot);
        Assert.Equal((uint)MaterialCode, only.ItemCode);
        Assert.Equal(33, only.Count);
    }

    [Fact]
    public void Projection_splits_quick_and_bag_slots_at_their_boundaries_and_orders_entries_by_slot()
    {
        ItemSyncSnapshot snapshot = Parse(Full(SynthItemWire.BagSpacePacket(312, 4000, 0, 0,
            Spec(311, MaterialCode, 61),
            Spec(9, PotionCode, 62),
            Spec(8, MaterialCode, 63),
            Spec(3, PotionCode, 64))));
        using var rig = new Rig(snapshot);

        // 增量按 30、12、21 的顺序放进去:影子表里的插入次序不是槽号次序,投影必须按槽号升序给出
        Assert.NotNull(rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.Bag,
            Spec(30, MaterialCode, 71),
            Spec(12, PotionCode, 72),
            Spec(21, MaterialCode, 73))));
        OnlineItemSyncData data = rig.Live.ProjectData();

        Assert.Collection(data.QuickEntries,
            e => AssertBagEntry(e, 3, PotionCode, 64),
            e => AssertBagEntry(e, 8, MaterialCode, 63));
        Assert.Collection(data.BagEntries,
            e => AssertBagEntry(e, 9, PotionCode, 62),
            e => AssertBagEntry(e, 12, PotionCode, 72),
            e => AssertBagEntry(e, 21, MaterialCode, 73),
            e => AssertBagEntry(e, 30, MaterialCode, 71),
            e => AssertBagEntry(e, 311, MaterialCode, 61));
        Assert.Equal(4000L, data.Gold);
    }

    private static void AssertBagEntry(OnlineItemSlotEntry actual, int slot, int code, int count)
    {
        Assert.Equal((ushort)slot, actual.Slot);
        Assert.Equal((uint)code, actual.ItemCode);
        Assert.Equal(count, actual.Count);
    }

    // ───────────────────────── 簇 B:NOTI/13 整空间包落到未建模空间 ─────────────────────────

    [Theory]
    [InlineData(ItemSpace.AvatarBag)]
    [InlineData(ItemSpace.Cargo)]
    [InlineData(ItemSpace.PetBag)]
    [InlineData(ItemSpace.AccountCargo)]
    public void Noti13_full_space_packet_replaces_the_whole_slot_set_of_the_unmodeled_space(ItemSpace space)
    {
        using var rig = new Rig(BaseSnapshot());
        rig.Live.ApplyCurrent(rig.Progress);
        AssertBaseline(rig.Progress);

        // 先整包 2 条
        OnlineItemSyncReport first = rig.Full(FullPacket(space, Put(space, 4), Put(space, 8)));
        Assert.NotNull(first);
        AssertOnly(rig.Live.ProjectData(), space, 2);
        AssertOnly(first, space, 2);
        AssertBaseline(rig.Progress);

        // 再整包 3 条,槽号与上一包全不同:整体覆盖,计数是 3 而不是 5
        OnlineItemSyncReport second = rig.Full(FullPacket(space, Put(space, 1), Put(space, 2), Put(space, 3)));
        Assert.NotNull(second);
        AssertOnly(rig.Live.ProjectData(), space, 3);
        AssertOnly(second, space, 3);
        AssertBaseline(rig.Progress);

        // 整包 0 条:清空;这类包照样算有变化(未建模计数变了)
        OnlineItemSyncReport third = rig.Full(FullPacket(space));
        Assert.NotNull(third);
        AssertOnly(rig.Live.ProjectData(), space, 0);
        AssertOnly(third, space, 0);
        AssertBaseline(rig.Progress);

        // 它们不碰背包、装备与金币的同步标志
        Assert.True(third.GoldSynced);
        Assert.True(third.InventorySynced);
        Assert.True(third.EquipsSynced);
        Assert.Equal(3, rig.Live.AppliedFrames);
        Assert.Equal(0, rig.Live.IgnoredFrames);
    }

    [Theory]
    [InlineData(ItemSpace.AvatarBag)]
    [InlineData(ItemSpace.Cargo)]
    [InlineData(ItemSpace.PetBag)]
    [InlineData(ItemSpace.AccountCargo)]
    public void Noti13_to_an_unmodeled_space_alone_never_marks_gold_bag_or_equips_as_synced(ItemSpace space)
    {
        using var rig = new Rig(null);

        OnlineItemSyncReport report = rig.Full(FullPacket(space, Put(space, 6), Put(space, 10)));

        Assert.NotNull(report);
        AssertOnly(report, space, 2);
        Assert.False(report.GoldSynced);
        Assert.False(report.InventorySynced);
        Assert.False(report.EquipsSynced);
        Assert.Equal(0L, rig.Progress.Gold);
        Assert.Equal(0, rig.Progress.Bag.TotalCount);
        Assert.Equal(0, rig.Progress.Worn.EquippedCount);
    }

    [Fact]
    public void Noti13_bag_packet_without_a_gold_slot_keeps_the_previous_gold_and_replaces_the_bag()
    {
        using var rig = new Rig(BaseSnapshot());

        // 整包背包只带胜点伪条目(槽 2)与一格材料,没有金币槽
        OnlineItemSyncReport report = rig.Full(BagFullWithoutGold(48, SynthItemWire.Pseudo(2, 5), Spec(12, MaterialCode, 33)));

        Assert.NotNull(report);
        Assert.True(report.GoldSynced);
        Assert.Equal(1000L, rig.Progress.Gold);
        Assert.Equal(33, rig.Progress.Bag.CountOf(MaterialCode));
        Assert.Equal(0, rig.Progress.Bag.CountOf(PotionCode));
        Assert.Equal(0, rig.Progress.QuickFilled);
        // 对照:金币槽显式给 0 是一个有效取值,要写成 0
        Assert.NotNull(rig.Full(SynthItemWire.BagSpacePacket(48, 0, 5, 6)));
        Assert.Equal(0L, rig.Progress.Gold);
    }

    /// <summary>快捷栏 6 格里只有 <paramref name="quickIndex"/> 那一格放着 <paramref name="code"/>,其余都空(<paramref name="quickIndex"/> 为 −1 表示 6 格全空)。</summary>
    private static void AssertQuickBar(HeroProgress progress, int quickIndex, int code)
    {
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(i == quickIndex ? code : 0, progress.QuickCodeAt(i));
        }
        Assert.Equal(quickIndex >= 0 ? 1 : 0, progress.QuickFilled);
    }

    [Theory]
    [InlineData(3, 0)]      // 快捷栏第一格
    [InlineData(4, 1)]
    [InlineData(7, 4)]
    [InlineData(8, 5)]      // 快捷栏最后一格
    [InlineData(9, -1)]     // 背包第一格
    [InlineData(10, -1)]
    [InlineData(311, -1)]   // 背包最后一格(容量 312)
    public void Noti13_bag_packet_on_a_fresh_sync_places_each_slot_in_the_quick_bar_or_the_bag(int slot, int quickIndex)
    {
        using var rig = new Rig(null);   // 没有初始快照:背包、金币都还没同步过

        OnlineItemSyncReport report = rig.Full(SynthItemWire.BagSpacePacket(312, 2500, 7, 9, Spec(slot, MaterialCode, 6)));

        Assert.NotNull(report);
        // 背包空间包本身就给出整份背包与金币:两个标志都成立,装备没有
        Assert.True(report.InventorySynced);
        Assert.True(report.GoldSynced);
        Assert.False(report.EquipsSynced);
        Assert.Equal(2500L, rig.Progress.Gold);
        Assert.Equal(6, rig.Progress.Bag.CountOf(MaterialCode));
        int quick = quickIndex >= 0 ? 1 : 0;
        Assert.Equal(quick, report.QuickApplied);
        Assert.Equal(1 - quick, report.BagApplied);
        AssertQuickBar(rig.Progress, quickIndex, MaterialCode);
        Assert.Equal(1, rig.Live.AppliedFrames);
    }

    // ───────────────────────── 簇 C:NOTI/14 增量包 ─────────────────────────

    [Theory]
    [InlineData(3, 0)]
    [InlineData(4, 1)]
    [InlineData(7, 4)]
    [InlineData(8, 5)]
    [InlineData(9, -1)]
    [InlineData(10, -1)]
    [InlineData(311, -1)]
    public void Noti14_single_bag_operation_on_a_fresh_sync_upserts_then_deletes_that_slot(int slot, int quickIndex)
    {
        using var rig = new Rig(null);   // 没有初始快照:背包还没同步过

        // 恰好一条 upsert:有效变化,背包标记为已同步,金币不在这个包里
        OnlineItemSyncReport put = rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.Bag, Spec(slot, MaterialCode, 6)));

        Assert.NotNull(put);
        Assert.True(put.InventorySynced);
        Assert.False(put.GoldSynced);
        Assert.False(put.EquipsSynced);
        Assert.Equal(6, rig.Progress.Bag.CountOf(MaterialCode));
        int quick = quickIndex >= 0 ? 1 : 0;
        Assert.Equal(quick, put.QuickApplied);
        Assert.Equal(1 - quick, put.BagApplied);
        AssertQuickBar(rig.Progress, quickIndex, MaterialCode);

        // 再恰好一条 delete 同一个槽:仍然是有效变化,条目消失
        OnlineItemSyncReport removed = rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.Bag, Remove(ItemSpace.Bag, slot)));

        Assert.NotNull(removed);
        Assert.True(removed.InventorySynced);
        Assert.Equal(0, removed.QuickApplied);
        Assert.Equal(0, removed.BagApplied);
        Assert.Equal(0, rig.Progress.Bag.CountOf(MaterialCode));
        AssertQuickBar(rig.Progress, -1, MaterialCode);
        Assert.Equal(2, rig.Live.AppliedFrames);
        Assert.Equal(0, rig.Live.IgnoredFrames);
    }

    [Fact]
    public void Empty_bag_increment_and_non_gold_currency_refresh_are_not_a_change()
    {
        using var rig = new Rig(BaseSnapshot());
        rig.Live.ApplyCurrent(rig.Progress);
        AssertBaseline(rig.Progress);

        // 条数为 0 的背包增量:没有任何有效变化 → null,但这一帧算已应用
        Assert.Null(rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.Bag)));
        Assert.Equal(1, rig.Live.AppliedFrames);
        Assert.Equal(0, rig.Live.IgnoredFrames);
        Assert.Equal(0, rig.Live.QueuedCount);
        AssertBaseline(rig.Progress);

        // 只刷新硬币合计(槽 1)、只刷新胜点(槽 2):都不是金币
        Assert.Null(rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.Bag, SynthItemWire.Pseudo(1, 777))));
        Assert.Null(rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.Bag, SynthItemWire.Pseudo(2, 888))));
        Assert.Equal(3, rig.Live.AppliedFrames);
        AssertBaseline(rig.Progress);

        // 对照:只刷新金币就是有效变化,且只改金币
        OnlineItemSyncReport goldOnly = rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.Bag, SynthItemWire.Pseudo(0, 1500)));
        Assert.NotNull(goldOnly);
        Assert.Equal(1500L, rig.Progress.Gold);
        Assert.Equal(20, rig.Progress.Bag.CountOf(MaterialCode));
        Assert.Equal(5, rig.Progress.QuickCountAt(0));

        // 金币刷新成 0 也是有效取值
        Assert.NotNull(rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.Bag, SynthItemWire.Pseudo(0, 0))));
        Assert.Equal(0L, rig.Progress.Gold);
        Assert.Equal(5, rig.Live.AppliedFrames);
    }

    [Theory]
    [InlineData(ItemSpace.AvatarBag)]
    [InlineData(ItemSpace.Cargo)]
    [InlineData(ItemSpace.PetBag)]
    [InlineData(ItemSpace.AccountCargo)]
    public void Noti14_increment_to_an_unmodeled_space_follows_wire_order_and_leaves_other_spaces_alone(ItemSpace space)
    {
        using var rig = new Rig(BaseSnapshot());
        rig.Live.ApplyCurrent(rig.Progress);
        AssertBaseline(rig.Progress);

        // ① 条数为 0:没有有效变化 → null,计数不变,这一帧算已应用
        Assert.Null(rig.Update(UpdatePacket(space)));
        AssertOnly(rig.Live.ProjectData(), space, 0);
        Assert.Equal(1, rig.Live.AppliedFrames);

        // ② 恰好 1 条 upsert 一个新槽:非 null,计数 +1
        OnlineItemSyncReport added = rig.Update(UpdatePacket(space, Put(space, 5)));
        Assert.NotNull(added);
        AssertOnly(rig.Live.ProjectData(), space, 1);
        AssertOnly(added, space, 1);

        // ③ 再 1 条 delete 同一槽:非 null,计数 −1
        OnlineItemSyncReport removed = rig.Update(UpdatePacket(space, Remove(space, 5)));
        Assert.NotNull(removed);
        AssertOnly(rig.Live.ProjectData(), space, 0);
        AssertOnly(removed, space, 0);

        // ④ 同一包内按线上顺序放槽 5、删槽 5、放槽 6:净增 1(只剩槽 6)
        Assert.NotNull(rig.Update(UpdatePacket(space, Put(space, 5), Remove(space, 5), Put(space, 6))));
        AssertOnly(rig.Live.ProjectData(), space, 1);

        // ⑤ 先删槽 6、再放槽 6、再放槽 7:槽 6 仍在,净 2(删除不能挪到所有放入之后)
        Assert.NotNull(rig.Update(UpdatePacket(space, Remove(space, 6), Put(space, 6), Put(space, 7))));
        AssertOnly(rig.Live.ProjectData(), space, 2);

        // ⑥ 同一个槽在一个包里放两次:集合语义,只算一个
        Assert.NotNull(rig.Update(UpdatePacket(space, Put(space, 8), Put(space, 8))));
        AssertOnly(rig.Live.ProjectData(), space, 3);

        // ⑦ 又一个条数为 0 的包:仍是 null,已有计数不变
        Assert.Null(rig.Update(UpdatePacket(space)));
        AssertOnly(rig.Live.ProjectData(), space, 3);

        // 整个过程不动金币、背包与装备,也没有任何帧被当成畸形
        AssertBaseline(rig.Progress);
        Assert.Equal(7, rig.Live.AppliedFrames);
        Assert.Equal(0, rig.Live.IgnoredFrames);
    }

    [Fact]
    public void Noti14_body_equip_upserts_and_deletes_only_touch_parts_ten_through_twentyone()
    {
        using var rig = new Rig(null);

        // 9 与 22 是紧挨着合法区间的两个越界部位,必须被丢掉
        OnlineItemSyncReport equipped = rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.BodyEquip,
            Spec(22, WeaponCode, 94, 7),
            Spec(21, CoatCode, 83, 5),
            Spec(10, WeaponCode, 72, 2),
            Spec(9, CoatCode, 11, 1)));

        Assert.NotNull(equipped);
        Assert.True(equipped.EquipsSynced);
        Assert.Collection(rig.Live.ProjectData().WornEquips,
            e => AssertWorn(e, slot: 10, code: WeaponCode, upgrade: 2, grade: 72),
            e => AssertWorn(e, slot: 21, code: CoatCode, upgrade: 5, grade: 83));
        Assert.Equal(2, rig.Progress.Worn.EquippedCount);
        Assert.Equal(WeaponCode, rig.Progress.Worn.Get(EquipmentParts.Weapon)?.Code);
        Assert.Equal(CoatCode, rig.Progress.Worn.Get(EquipmentParts.MagicStone)?.Code);

        // 清部位 10,再清两个越界部位(从来没有过):只剩部位 21
        OnlineItemSyncReport cleared = rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.BodyEquip,
            new SynthItemWire.ItemSpec { Slot = 22, ItemId = SynthItemWire.DeleteMarker },
            new SynthItemWire.ItemSpec { Slot = 10, ItemId = SynthItemWire.DeleteMarker },
            new SynthItemWire.ItemSpec { Slot = 9, ItemId = SynthItemWire.DeleteMarker }));

        Assert.NotNull(cleared);
        OnlineItemSlotEntry left = Assert.Single(rig.Live.ProjectData().WornEquips);
        Assert.Equal((ushort)21, left.Slot);
        Assert.Equal(1, rig.Progress.Worn.EquippedCount);
        Assert.Null(rig.Progress.Worn.Get(EquipmentParts.Weapon));
        Assert.Equal(CoatCode, rig.Progress.Worn.Get(EquipmentParts.MagicStone)?.Code);
    }

    [Fact]
    public void Noti14_body_equip_operations_are_applied_one_by_one_starting_with_the_first()
    {
        using var rig = new Rig(null);

        // 恰好一条 upsert,而且就是下标 0 的那条
        Assert.NotNull(rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.BodyEquip, Spec(10, WeaponCode, 72, 2))));
        AssertWorn(Assert.Single(rig.Live.ProjectData().WornEquips), slot: 10, code: WeaponCode, upgrade: 2, grade: 72);
        Assert.Equal(1, rig.Progress.Worn.EquippedCount);

        // 第一条就是合法部位的整包:放部位 12、放部位 21
        Assert.NotNull(rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.BodyEquip,
            Spec(12, CoatCode, 40, 1),
            Spec(21, CoatCode, 83, 5))));
        Assert.Collection(rig.Live.ProjectData().WornEquips,
            e => AssertWorn(e, slot: 10, code: WeaponCode, upgrade: 2, grade: 72),
            e => AssertWorn(e, slot: 12, code: CoatCode, upgrade: 1, grade: 40),
            e => AssertWorn(e, slot: 21, code: CoatCode, upgrade: 5, grade: 83));
        Assert.Equal(3, rig.Progress.Worn.EquippedCount);

        // 恰好一条 delete,而且就是下标 0 的那条:只清部位 10
        Assert.NotNull(rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.BodyEquip, Remove(ItemSpace.BodyEquip, 10))));
        Assert.Collection(rig.Live.ProjectData().WornEquips,
            e => AssertWorn(e, slot: 12, code: CoatCode, upgrade: 1, grade: 40),
            e => AssertWorn(e, slot: 21, code: CoatCode, upgrade: 5, grade: 83));
        Assert.Equal(2, rig.Progress.Worn.EquippedCount);
        Assert.Null(rig.Progress.Worn.Get(EquipmentParts.Weapon));

        // 两条 delete(各自都是合法部位):全清
        Assert.NotNull(rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.BodyEquip,
            Remove(ItemSpace.BodyEquip, 12),
            Remove(ItemSpace.BodyEquip, 21))));
        Assert.Empty(rig.Live.ProjectData().WornEquips);
        Assert.Equal(0, rig.Progress.Worn.EquippedCount);
        Assert.Equal(4, rig.Live.AppliedFrames);
        Assert.Equal(0, rig.Live.IgnoredFrames);
    }

    // ───────────────────────── 帧过滤、退订与整包拒绝 ─────────────────────────

    [Fact]
    public void Frames_that_are_not_item_space_or_item_update_notices_are_never_queued()
    {
        using var rig = new Rig(null);
        byte[] bagUpdate = SynthItemWire.UpdatePacket((int)ItemSpace.Bag, SynthItemWire.Pseudo(0, 4321));
        byte[] bagFull = SynthItemWire.BagSpacePacket(40, 4321, 0, 0);

        // 同为通知但包号与物品包差一位:不入队
        rig.Push(12, bagFull);
        rig.Push(15, bagUpdate);
        // 包号对但是命令类:不入队
        Dispatch(rig.Session, PacketClass.Cmd, ItemListParser.PacketId, bagFull);
        Dispatch(rig.Session, PacketClass.Cmd, ItemUpdateParser.PacketId, bagUpdate);
        // 空帧:不入队、不抛
        foreach (Delegate d in SessionProbe.Handlers(rig.Session, "FrameReceived"))
        {
            ((Action<OnlineReceivedFrame>)d)(null);
        }

        Assert.Equal(0, rig.Live.QueuedCount);
        Assert.Null(rig.Live.DrainAndApply(rig.Progress));
        Assert.Equal(0, rig.Live.AppliedFrames);
        Assert.Equal(0, rig.Live.IgnoredFrames);
        Assert.Equal(0L, rig.Progress.Gold);

        // 对照:通知类的 13 与 14 各入队一个
        rig.Push(ItemListParser.PacketId, bagFull);
        Assert.Equal(1, rig.Live.QueuedCount);
        rig.Push(ItemUpdateParser.PacketId, bagUpdate);
        Assert.Equal(2, rig.Live.QueuedCount);
        Assert.NotNull(rig.Live.DrainAndApply(rig.Progress));
        Assert.Equal(0, rig.Live.QueuedCount);
        Assert.Equal(2, rig.Live.AppliedFrames);
        Assert.Equal(4321L, rig.Progress.Gold);
    }

    [Fact]
    public void Dispose_unsubscribes_discards_queued_frames_and_drops_callbacks_already_in_flight()
    {
        using var rig = new Rig(null);
        Delegate[] inFlight = SessionProbe.Handlers(rig.Session, "FrameReceived");
        Delegate callback = Assert.Single(inFlight);
        byte[] bagUpdate = SynthItemWire.UpdatePacket((int)ItemSpace.Bag, SynthItemWire.Pseudo(0, 4321));
        rig.Push(ItemUpdateParser.PacketId, bagUpdate);
        Assert.Equal(1, rig.Live.QueuedCount);

        rig.Live.Dispose();

        Assert.Equal(0, SessionProbe.FrameReceivedCount(rig.Session));
        Assert.Equal(0, rig.Live.QueuedCount);

        // 退订之前已经抓在手里的回调(后台线程里正在飞的那一帧)事后才被调用:直接丢弃
        ((Action<OnlineReceivedFrame>)callback)(NetObjects.Frame(PacketClass.Noti, ItemUpdateParser.PacketId, bagUpdate));
        Assert.Equal(0, rig.Live.QueuedCount);
        Assert.Null(rig.Live.DrainAndApply(rig.Progress));
        Assert.Equal(0L, rig.Progress.Gold);
        Assert.Equal(0, rig.Live.AppliedFrames);

        // 重复释放是安全的
        rig.Live.Dispose();
        Assert.Equal(0, SessionProbe.FrameReceivedCount(rig.Session));
    }

    [Fact]
    public void Packets_the_parser_rejects_are_counted_as_ignored_and_never_half_applied()
    {
        using var rig = new Rig(BaseSnapshot());
        rig.Live.ApplyCurrent(rig.Progress);

        // 宠物栏增量:第 1 条是认得的宠物,第 2 条物品号不认识 → 整包拒绝,第 1 条也不生效
        Assert.Null(rig.Update(UpdatePacket(ItemSpace.PetBag, Put(ItemSpace.PetBag, 1), Spec(2, UnknownCode, 1))));
        Assert.Equal(1, rig.Live.IgnoredFrames);
        AssertOnly(rig.Live.ProjectData(), ItemSpace.PetBag, 0);

        // 宠物栏整包:同样的拒绝
        Assert.Null(rig.Full(SynthItemWire.PetSpacePacket(Put(ItemSpace.PetBag, 1), Spec(2, UnknownCode, 1))));
        Assert.Equal(2, rig.Live.IgnoredFrames);
        AssertOnly(rig.Live.ProjectData(), ItemSpace.PetBag, 0);

        // 身上装备增量:物品号不认识 → 整包拒绝,已有的武器不动
        Assert.Null(rig.Update(SynthItemWire.UpdatePacket((int)ItemSpace.BodyEquip, Spec(12, CoatCode, 40), Spec(10, UnknownCode, 1))));
        Assert.Equal(3, rig.Live.IgnoredFrames);
        AssertWorn(Assert.Single(rig.Live.ProjectData().WornEquips), slot: 10, code: WeaponCode, upgrade: 6, grade: 90);

        // 物品空间号不在支持集合里
        Assert.Null(rig.Update(new byte[] { 0x05, 0x00, 0x00 }));
        Assert.Equal(4, rig.Live.IgnoredFrames);

        Assert.Equal(0, rig.Live.AppliedFrames);
        AssertBaseline(rig.Progress);

        // 对照:同一个会话里合法的宠物栏增量照常生效
        Assert.NotNull(rig.Update(UpdatePacket(ItemSpace.PetBag, Put(ItemSpace.PetBag, 1))));
        AssertOnly(rig.Live.ProjectData(), ItemSpace.PetBag, 1);
        Assert.Equal(1, rig.Live.AppliedFrames);
        Assert.Equal(4, rig.Live.IgnoredFrames);
    }

    [Fact]
    public void An_override_resolver_replaces_the_asset_catalog_for_lookups_and_for_parsing_pushed_packets()
    {
        // 覆盖解析器的答案与资产里的物品表故意相反:武器说成宠物、药水说成装扮、宠物编号说成不认识、不认识的编号说成普通物品
        var kinds = new FixedKinds()
            .With(WeaponCode, ResolvedItemKind.Pet)
            .With(PotionCode, ResolvedItemKind.Avatar)
            .With(CreatureCode, ResolvedItemKind.Unknown)
            .With(UnknownCode, ResolvedItemKind.Other);
        using var rig = new Rig(null, overrideResolver: kinds);

        Assert.Equal(ResolvedItemKind.Pet, rig.Live.Resolve(WeaponCode));
        Assert.Equal(ResolvedItemKind.Avatar, rig.Live.Resolve(PotionCode));
        Assert.Equal(ResolvedItemKind.Unknown, rig.Live.Resolve(CreatureCode));
        Assert.Equal(ResolvedItemKind.Other, rig.Live.Resolve(UnknownCode));
        // 覆盖解析器没登记的编号不回落到资产(资产里它是普通物品)
        Assert.Equal(ResolvedItemKind.Unknown, rig.Live.Resolve(MaterialCode));

        // 推进来的包也按覆盖解析器解析:武器编号被当成宠物,宠物栏的线格式多一个剩余时长字段,整包被接受
        OnlineItemSyncReport accepted = rig.Update(UpdatePacket(ItemSpace.PetBag,
            new SynthItemWire.ItemSpec { Slot = 4, ItemId = WeaponCode, PetTime = 4321 }));
        Assert.NotNull(accepted);
        AssertOnly(accepted, ItemSpace.PetBag, 1);
        Assert.Equal(1, rig.Live.AppliedFrames);
        Assert.Equal(0, rig.Live.IgnoredFrames);

        // 真正的宠物编号被覆盖解析器说成不认识:宠物栏增量整包被拒,计数不变
        Assert.Null(rig.Update(UpdatePacket(ItemSpace.PetBag, Put(ItemSpace.PetBag, 5))));
        Assert.Equal(1, rig.Live.IgnoredFrames);
        Assert.Equal(1, rig.Live.AppliedFrames);
        AssertOnly(rig.Live.ProjectData(), ItemSpace.PetBag, 1);
    }

    [Fact]
    public void Null_progress_is_rejected_by_parameter_name()
    {
        using var live = new OnlineItemLiveSync(null, null, null, null);

        ArgumentNullException drain = Assert.Throws<ArgumentNullException>(() => live.DrainAndApply(null));
        ArgumentNullException current = Assert.Throws<ArgumentNullException>(() => live.ApplyCurrent(null));

        Assert.Equal("progress", drain.ParamName);
        Assert.Equal("progress", current.ParamName);
    }
}
