using System.Reflection;
using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Game;
using Dnf.Net.Crypto;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Client.Tests;

/// <summary>
/// Stage 1 Client 适配器与后端/宿主/城镇控制器接线测试:
/// 用合成字节夹具 <see cref="SynthItemWire"/> + <see cref="ItemSyncParser"/> 构造合成 <see cref="ItemSyncSnapshot"/>,
/// 检验 <see cref="OnlineItemSyncAdapter.ToPlayData"/>、<see cref="OnlineShellBackend"/>/<see cref="OnlineShellHost"/> 透传,
/// 以及 <see cref="OnlineTownController.BindOnlineItems"/>。
/// </summary>
public sealed class OnlineItemSyncAdapterTests
{
    private const int WeaponCode = 900001;
    private const int CoatCode = 900003;
    private const int HatAvatarCode = 900004;
    private const int CreatureCode = 900005;
    private const int PotionCode = 901001;
    private const int MaterialCode = 901002;

    private static ItemWireKindResolver KindResolver()
    {
        return new ItemWireKindResolver()
            .With(WeaponCode, ResolvedItemKind.Other)
            .With(CoatCode, ResolvedItemKind.Other)
            .With(HatAvatarCode, ResolvedItemKind.Avatar)
            .With(CreatureCode, ResolvedItemKind.Pet)
            .With(PotionCode, ResolvedItemKind.Other)
            .With(MaterialCode, ResolvedItemKind.Other);
    }

    private sealed class ItemWireKindResolver : IItemKindResolver
    {
        private readonly Dictionary<uint, ResolvedItemKind> _kinds = new();

        public ItemWireKindResolver With(long itemId, ResolvedItemKind kind)
        {
            _kinds[(uint)itemId] = kind;
            return this;
        }

        public ResolvedItemKind Resolve(uint itemId)
        {
            return _kinds.TryGetValue(itemId, out ResolvedItemKind kind) ? kind : ResolvedItemKind.Unknown;
        }
    }

    internal static class SynthItemWire
    {
        public const long DeleteMarker = 0xFFFFFFFFL;

        public sealed class ItemSpec
        {
            public int Slot { get; set; }
            public long ItemId { get; set; }
            public long Data { get; set; }
            public int Ext1 { get; set; }
            public int Durability { get; set; }
            public int Sealed { get; set; }
            public long ClearAvatar { get; set; }
            public long Enchant { get; set; }
            public int AmpType { get; set; }
            public int AmpValue { get; set; }
            public byte[] SocketBlock { get; set; }
            public byte[] ExpansionBlock { get; set; }
            public long? PetTime { get; set; }
            public int Separate { get; set; }
            public int Trade { get; set; }
        }

        public static byte[] Socket()
        {
            var b = new byte[30];
            for (int i = 0; i < b.Length; i++) b[i] = (byte)(0x40 + i);
            return b;
        }

        public static byte[] Expansion() => new byte[] { 0xA0, 0xA1, 0xA2, 0xA3 };

        public static ItemSpec Pseudo(int slot, long amount) => new ItemSpec { Slot = slot, ItemId = slot, Data = amount };

        public static ItemSpec Item(int slot, long itemId) => new ItemSpec { Slot = slot, ItemId = itemId };

        private static NoticePayloadBuilder AppendBagEntry(NoticePayloadBuilder b, ItemSpec s)
        {
            b.U16(s.Slot).U32(s.ItemId).U32(s.Data).U8(s.Ext1).U16(s.Durability).U8(s.Sealed)
                .U32(s.Enchant).U8(s.AmpType).U16(s.AmpValue);
            return AppendTail(b, s);
        }

        private static NoticePayloadBuilder AppendDetailEntry(NoticePayloadBuilder b, ItemSpec s)
        {
            b.U8(s.Slot).U32(s.ItemId).U32(s.Data).U8(s.Ext1).U16(s.Durability).U32(s.ClearAvatar)
                .U32(s.Enchant).U8(s.AmpType).U16(s.AmpValue);
            return AppendTail(b, s);
        }

        private static NoticePayloadBuilder AppendTail(NoticePayloadBuilder b, ItemSpec s)
        {
            if (s.SocketBlock != null) b.U32(30).Raw(s.SocketBlock);
            if (s.ExpansionBlock != null) b.U32(4).Raw(s.ExpansionBlock);
            if (s.PetTime.HasValue) b.U32(s.PetTime.Value);
            b.U8(0).U8(0).U8(s.Separate).U8(s.Trade); // 空 C3 + 空随机选项 + 单独强化 + 交易限制
            return b;
        }

        public static byte[] BagSpacePacket(int capacity, long gold, long coin, long winPoint, params ItemSpec[] items)
        {
            NoticePayloadBuilder b = new NoticePayloadBuilder().U8(0).U16(capacity).U16(3 + items.Length);
            AppendBagEntry(b, Pseudo(0, gold));
            AppendBagEntry(b, Pseudo(1, coin));
            AppendBagEntry(b, Pseudo(2, winPoint));
            foreach (ItemSpec item in items) AppendBagEntry(b, item);
            return b.ToArray();
        }

        public static byte[] CargoSpacePacket(int capacity, params ItemSpec[] items)
        {
            NoticePayloadBuilder b = new NoticePayloadBuilder().U8(2).U16(capacity).U16(items.Length);
            foreach (ItemSpec item in items) AppendBagEntry(b, item);
            return b.ToArray();
        }

        public static byte[] AvatarSpacePacket(params ItemSpec[] items)
        {
            NoticePayloadBuilder b = new NoticePayloadBuilder().U8(1).U16(items.Length);
            foreach (ItemSpec item in items) AppendBagEntry(b, item);
            return b.ToArray();
        }

        public static byte[] PetSpacePacket(params ItemSpec[] items)
        {
            NoticePayloadBuilder b = new NoticePayloadBuilder().U8(7).U16(items.Length);
            foreach (ItemSpec item in items) AppendBagEntry(b, item);
            return b.ToArray();
        }

        public static byte[] AccountCargoSpacePacket(int word, long dword, params ItemSpec[] items)
        {
            NoticePayloadBuilder b = new NoticePayloadBuilder().U8(0x0C).U16(word).U32(dword).U16(items.Length);
            foreach (ItemSpec item in items) AppendBagEntry(b, item);
            return b.ToArray();
        }

        public static byte[] UpdatePacket(int space, params ItemSpec[] items)
        {
            NoticePayloadBuilder b = new NoticePayloadBuilder().U8(space).U16(items.Length);
            foreach (ItemSpec item in items) AppendBagEntry(b, item);
            return b.ToArray();
        }

        public static byte[] SelfViewPacket(int count, int x, long y, byte[] stat, int selfByte, byte[] tail, params ItemSpec[] entries)
        {
            NoticePayloadBuilder b = new NoticePayloadBuilder().U8(1).U16(count).U16(x).U32(y)
                .U32(stat.Length).Raw(stat).U8(selfByte).U8(entries.Length);
            foreach (ItemSpec e in entries) AppendDetailEntry(b, e);
            return b.Raw(tail).ToArray();
        }

        public static byte[] ExpandEquipPacket(ushort uid, params ItemSpec[] group0Entries)
        {
            var b = new NoticePayloadBuilder().U16(uid);
            b.U8(0).U8(group0Entries.Length);
            foreach (ItemSpec e in group0Entries) AppendDetailEntry(b, e);
            b.U8(1).U8(0);
            b.U8(2).U8(0);
            return b.ToArray();
        }

        public static DecodedFrame FrameOf(PacketClass cls, int packetId, byte[] payload)
        {
            if (cls == PacketClass.Noti && packetId == UserInfoViewParser.PacketId)
            {
                return UserInfoTestData.DecodedFrameOf(payload);
            }
            uint crc = Crc32N.Compute(payload);
            var wire = new byte[15 + payload.Length];
            wire[0] = (byte)cls;
            wire[1] = (byte)packetId;
            wire[2] = (byte)(packetId >> 8);
            BitConverter.GetBytes((uint)wire.Length).CopyTo(wire, 3);
            BitConverter.GetBytes(crc).CopyTo(wire, 7);
            BitConverter.GetBytes(crc).CopyTo(wire, 11);
            payload.CopyTo(wire, 15);
            var decoder = new ServerFrameDecoder { Cipher = new NullPayloadCipher() };
            Assert.Equal(FrameStatus.Ok, decoder.Decode(wire, out DecodedFrame frame));
            return frame;
        }
    }

    private static OnlineSelectCharacterResult SelectResultWithItems(
        bool entered, bool mayTryAnotherSlot, int slot,
        SelectCharacterResult selectCharacter, TownEntrySnapshot snapshot, DungeonPermissionSnapshot permissions, ItemSyncSnapshot itemSync)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object entry = Activator.CreateInstance(typeof(OnlineEntryResult), Any, null, new object[]
        {
            entered ? OnlineStage.Done : OnlineStage.SelectSlot,
            entered ? OnlineFailure.None : OnlineFailure.NoUsableSlot,
            (byte?)null,
            entered ? slot : -1,
            null,
            null,
            0,
            0,
            snapshot,
            permissions,
            selectCharacter,
            itemSync,
        }, null);
        object result = Activator.CreateInstance(typeof(OnlineSelectCharacterResult), Any, null,
            new object[] { slot, mayTryAnotherSlot, entry }, null);
        Assert.NotNull(result);
        return (OnlineSelectCharacterResult)result;
    }

    [Fact]
    public void Null_snapshot_adapts_to_empty_unsynced_data()
    {
        OnlineItemSyncData data = OnlineItemSyncAdapter.ToPlayData(null);

        Assert.False(data.HasGold);
        Assert.Equal(0L, data.Gold);
        Assert.False(data.HasBagData);
        Assert.False(data.HasEquipData);
        Assert.Empty(data.BagEntries);
        Assert.Empty(data.QuickEntries);
        Assert.Empty(data.WornEquips);
        Assert.Equal(0, data.UnmodeledAvatarBagCount);
        Assert.Equal(0, data.UnmodeledCargoCount);
        Assert.Equal(0, data.UnmodeledPetBagCount);
        Assert.Equal(0, data.UnmodeledAccountCargoCount);
        Assert.Equal(0, data.UnmodeledExpandEquipCount);

        // 非 null 但各空间均为空的快照:各项未建模计数也应全部为 0
        ItemSyncSnapshot emptySnap = ItemSyncParser.Parse(Array.Empty<DecodedFrame>());
        OnlineItemSyncData emptyData = OnlineItemSyncAdapter.ToPlayData(emptySnap, selfUid: 1203);
        Assert.False(emptyData.HasGold);
        Assert.False(emptyData.HasBagData);
        Assert.False(emptyData.HasEquipData);
        Assert.Equal(0, emptyData.UnmodeledAvatarBagCount);
        Assert.Equal(0, emptyData.UnmodeledCargoCount);
        Assert.Equal(0, emptyData.UnmodeledPetBagCount);
        Assert.Equal(0, emptyData.UnmodeledAccountCargoCount);
        Assert.Equal(0, emptyData.UnmodeledExpandEquipCount);
    }

    [Fact]
    public void Snapshot_routes_currency_quickbar_bag_selfview_bodyequip_and_unmodeled_counts_accurately()
    {
        byte[] bagBody = SynthItemWire.BagSpacePacket(
            capacity: 48,
            gold: 54321,
            coin: 7,
            winPoint: 99,
            new SynthItemWire.ItemSpec { Slot = 3, ItemId = PotionCode, Data = 15 },
            new SynthItemWire.ItemSpec { Slot = 8, ItemId = PotionCode, Data = 3 },
            new SynthItemWire.ItemSpec { Slot = 9, ItemId = WeaponCode, Data = 72, Ext1 = 11 },
            new SynthItemWire.ItemSpec { Slot = 15, ItemId = MaterialCode, Data = 40 });

        byte[] avatarBody = SynthItemWire.AvatarSpacePacket(
            new SynthItemWire.ItemSpec
            {
                Slot = 0,
                ItemId = HatAvatarCode,
                Data = 30,
                SocketBlock = SynthItemWire.Socket(),
                ExpansionBlock = SynthItemWire.Expansion(),
            });

        byte[] cargoBody = SynthItemWire.CargoSpacePacket(
            24,
            SynthItemWire.Item(0, MaterialCode),
            SynthItemWire.Item(1, WeaponCode));

        byte[] petBody = SynthItemWire.PetSpacePacket(
            new SynthItemWire.ItemSpec { Slot = 0, ItemId = CreatureCode, PetTime = 1234 });

        byte[] acctCargoBody = SynthItemWire.AccountCargoSpacePacket(16, 1000, SynthItemWire.Item(0, MaterialCode));

        byte[] selfViewBody = SynthItemWire.SelfViewPacket(
            count: 1,
            x: 1,
            y: 2,
            stat: new byte[4],
            selfByte: 0,
            tail: Array.Empty<byte>(),
            new SynthItemWire.ItemSpec { Slot = 0, ItemId = HatAvatarCode, Data = 10, SocketBlock = SynthItemWire.Socket(), ExpansionBlock = SynthItemWire.Expansion() },
            new SynthItemWire.ItemSpec { Slot = 10, ItemId = WeaponCode, Data = 50, Ext1 = 5 },
            new SynthItemWire.ItemSpec { Slot = 21, ItemId = CoatCode, Data = 66, Ext1 = 3 },
            new SynthItemWire.ItemSpec { Slot = 22, ItemId = CreatureCode, PetTime = 999 });

        byte[] bodyEquipUpdate = SynthItemWire.UpdatePacket(
            space: (int)ItemSpace.BodyEquip,
            new SynthItemWire.ItemSpec
            {
                Slot = 0,
                ItemId = HatAvatarCode,
                Data = 15,
                SocketBlock = SynthItemWire.Socket(),
                ExpansionBlock = SynthItemWire.Expansion(),
            },
            new SynthItemWire.ItemSpec
            {
                Slot = 10,
                ItemId = WeaponCode,
                Data = 85,
                Ext1 = 0x20 | 12,
                Enchant = 905001,
                AmpType = 0x80,
            },
            new SynthItemWire.ItemSpec { Slot = 12, ItemId = CoatCode, Data = 44, Ext1 = 9 },
            new SynthItemWire.ItemSpec { Slot = 22, ItemId = CreatureCode, PetTime = 888 });

        byte[] ownExpand = SynthItemWire.ExpandEquipPacket(1203, SynthItemWire.Item(10, WeaponCode), SynthItemWire.Item(12, CoatCode));
        byte[] otherExpand = SynthItemWire.ExpandEquipPacket(9999, SynthItemWire.Item(10, WeaponCode));

        var frames = new DecodedFrame[]
        {
            SynthItemWire.FrameOf(PacketClass.Noti, ItemListParser.PacketId, bagBody),
            SynthItemWire.FrameOf(PacketClass.Noti, ItemListParser.PacketId, avatarBody),
            SynthItemWire.FrameOf(PacketClass.Noti, ItemListParser.PacketId, cargoBody),
            SynthItemWire.FrameOf(PacketClass.Noti, ItemListParser.PacketId, petBody),
            SynthItemWire.FrameOf(PacketClass.Noti, ItemListParser.PacketId, acctCargoBody),
            SynthItemWire.FrameOf(PacketClass.Noti, UserInfoViewParser.PacketId, selfViewBody),
            SynthItemWire.FrameOf(PacketClass.Noti, ItemUpdateParser.PacketId, bodyEquipUpdate),
            SynthItemWire.FrameOf(PacketClass.Noti, ExpandEquipParser.PacketId, ownExpand),
            SynthItemWire.FrameOf(PacketClass.Noti, ExpandEquipParser.PacketId, otherExpand),
        };

        ItemSyncSnapshot snapshot = ItemSyncParser.Parse(frames, KindResolver());
        Assert.Equal(0, snapshot.FramesMalformed);

        OnlineItemSyncData data = OnlineItemSyncAdapter.ToPlayData(snapshot, selfUid: 1203);

        Assert.True(data.HasGold);
        Assert.Equal(54321L, data.Gold);
        Assert.True(data.HasBagData);
        Assert.True(data.HasEquipData);

        Assert.Equal(2, data.QuickEntries.Count);
        Assert.Equal((ushort)3, data.QuickEntries[0].Slot);
        Assert.Equal((uint)PotionCode, data.QuickEntries[0].ItemCode);
        Assert.Equal(15, data.QuickEntries[0].Count);
        Assert.Equal((ushort)8, data.QuickEntries[1].Slot);
        Assert.Equal(3, data.QuickEntries[1].Count);

        Assert.Equal(2, data.BagEntries.Count);
        Assert.Equal((ushort)9, data.BagEntries[0].Slot);
        Assert.Equal((uint)WeaponCode, data.BagEntries[0].ItemCode);
        Assert.Equal(11, data.BagEntries[0].UpgradeLevel);
        Assert.Equal((ushort)15, data.BagEntries[1].Slot);
        Assert.Equal((uint)MaterialCode, data.BagEntries[1].ItemCode);
        Assert.Equal(40, data.BagEntries[1].Count);

        // 0/9 装扮与 22 宠物不进 WornEquips; BodyEquip 槽 10 覆盖 SelfView 槽 10
        Assert.Equal(3, data.WornEquips.Count);
        Assert.Equal((ushort)10, data.WornEquips[0].Slot);
        Assert.Equal(12, data.WornEquips[0].UpgradeLevel);
        Assert.True(data.WornEquips[0].IsAmplified);
        Assert.False(data.WornEquips[0].IsIdentified);
        Assert.Equal(905001u, data.WornEquips[0].EnchantCode);
        Assert.Equal(85, data.WornEquips[0].GradeValue);
        Assert.Equal((ushort)12, data.WornEquips[1].Slot);
        Assert.Equal((ushort)21, data.WornEquips[2].Slot);

        Assert.Equal(1, data.UnmodeledAvatarBagCount);
        Assert.Equal(2, data.UnmodeledCargoCount);
        Assert.Equal(1, data.UnmodeledPetBagCount);
        Assert.Equal(1, data.UnmodeledAccountCargoCount);
        Assert.Equal(2, data.UnmodeledExpandEquipCount);

        // selfUid 为 null 时汇总所有 ExpandEquips 条目
        OnlineItemSyncData unfiltered = OnlineItemSyncAdapter.ToPlayData(snapshot);
        Assert.Equal(3, unfiltered.UnmodeledExpandEquipCount);
    }

    [Fact]
    public async Task Shell_backend_and_host_propagate_item_sync_on_select_and_clear_on_failure_and_reconnect()
    {
        ItemSyncSnapshot snap = ItemSyncParser.Parse(new[]
        {
            SynthItemWire.FrameOf(PacketClass.Noti, ItemListParser.PacketId,
                SynthItemWire.BagSpacePacket(40, 12345, 0, 0, new SynthItemWire.ItemSpec { Slot = 9, ItemId = MaterialCode, Data = 6 })),
        });

        using var rig = new TownHostRig(slot =>
            slot == 0
                ? new OnlineSelectReport(true, false, null, OnlineTownHostTests.SelectPayload(1203, 1), null, null, snap)
                : new OnlineSelectReport(false, true, null));

        rig.EnterInGame(0);

        Assert.Same(snap, rig.Backend.ItemSync);
        Assert.Same(snap, rig.Host.ItemSync);

        // ToReport 静态映射直接透传 OnlineSelectCharacterResult.ItemSync
        OnlineSelectCharacterResult realResult = SelectResultWithItems(true, false, 0, OnlineTownHostTests.SelectPayload(1203, 1), null, null, snap);
        OnlineSelectReport mapped = RealOnlineSessionPort.ToReport(realResult);
        Assert.Same(snap, mapped.ItemSync);

        // 换到失败分支时清掉旧 ItemSync
        rig.Host.ReturnToCharacterSelect();
        rig.Until(() => rig.Host.Stage == OnlineShellStage.CharacterSelect);
        Assert.Null(rig.Host.ItemSync);

        OnlineSelectOutcome failedSelect = await rig.Backend.SelectCharacterAsync(1, CancellationToken.None);
        Assert.False(failedSelect.Entered);
        Assert.Null(rig.Backend.ItemSync);
    }

    [Fact]
    public void Town_controller_binds_initial_items_updates_profile_flags_and_fires_equip_callback()
    {
        using var assets = new SyntheticBindAssets();
        ItemSyncSnapshot snap = ItemSyncParser.Parse(new[]
        {
            SynthItemWire.FrameOf(PacketClass.Noti, ItemListParser.PacketId,
                SynthItemWire.BagSpacePacket(40, 9876, 0, 0,
                    new SynthItemWire.ItemSpec { Slot = 3, ItemId = PotionCode, Data = 4 },
                    new SynthItemWire.ItemSpec { Slot = 9, ItemId = MaterialCode, Data = 7 })),
            SynthItemWire.FrameOf(PacketClass.Noti, UserInfoViewParser.PacketId,
                SynthItemWire.SelfViewPacket(1, 0, 0, Array.Empty<byte>(), 0, Array.Empty<byte>(),
                    new SynthItemWire.ItemSpec { Slot = 10, ItemId = WeaponCode, Data = 10 })),
        });

        using var rig = new TownHostRig(_ =>
            new OnlineSelectReport(true, false, null, OnlineTownHostTests.SelectPayload(1203, 1), null, null, snap));
        rig.EnterInGame(0);

        using OnlineTownController controller = OnlineTownController.Create(rig.Host, OnlineTownHostTests.BuildAtlas(), log: rig.Log);
        HeroProgress progress = HeroProgress.CreateForOnlineHero(assets.Assets, controller.Profile);
        int equipCallbacks = 0;

        OnlineItemSyncReport report = controller.BindOnlineItems(progress, assets.Assets, () => equipCallbacks++, applyInitial: true);

        Assert.NotNull(report);
        Assert.True(report.GoldSynced);
        Assert.True(report.InventorySynced);
        Assert.True(report.EquipsSynced);
        Assert.Equal(9876L, progress.Gold);
        Assert.Equal(4, progress.QuickCountAt(0));
        Assert.Equal(7, progress.Bag.CountOf(MaterialCode));
        Assert.Equal(WeaponCode, progress.Worn.Get(EquipmentParts.Weapon)?.Code);
        Assert.Equal(1, equipCallbacks);
        Assert.True(controller.Profile.GoldSynced);
        Assert.True(controller.Profile.InventorySynced);
        Assert.True(controller.Profile.EquipsSynced);
        Assert.Equal("联机城镇 · 技能未同步", OnlineTownOverlay.StatusBadgeText(controller.Profile, hasFonts: true));
    }

    internal sealed class SyntheticBindAssets : IDisposable
    {
        private readonly string _root;
        private readonly List<string> _strings = new List<string>();
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<TestData.PvfFileSpec> _files = new List<TestData.PvfFileSpec>();

        public SyntheticBindAssets()
        {
            _root = Path.Combine(Path.GetTempPath(), "dnf-clientitems-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "ImagePacks2"));

            var thresholds = new int[99];
            for (int i = 0; i < thresholds.Length; i++)
            {
                thresholds[i] = (i + 1) * 100;
            }
            AddTokens(ExpTable.Path, thresholds.Select(t => ((byte)TokenType.Int, t)).ToArray());
            AddTokens(GameData.CharacterListPath,
                ((byte)TokenType.Int, EquipRules.SwordmanJob),
                ((byte)TokenType.String, Intern("swordman/test.chr")));
            AddTokens("character/swordman/test.chr",
                ((byte)TokenType.Tag, Intern("[job]")),
                ((byte)TokenType.String, Intern("[swordman]")));
            AddTokens(ItemCatalog.EquipmentListPath,
                ((byte)TokenType.Int, WeaponCode), ((byte)TokenType.String, Intern("test/weapon.equ")),
                ((byte)TokenType.Int, CoatCode), ((byte)TokenType.String, Intern("test/coat.equ")),
                ((byte)TokenType.Int, HatAvatarCode), ((byte)TokenType.String, Intern("test/hat.equ")),
                ((byte)TokenType.Int, CreatureCode), ((byte)TokenType.String, Intern("test/creature.equ")));
            AddTokens("equipment/test/weapon.equ",
                ((byte)TokenType.Tag, Intern("[equipment type]")), ((byte)TokenType.String, Intern("[weapon]")), ((byte)TokenType.Int, 0),
                ((byte)TokenType.Tag, Intern("[usable job]")), ((byte)TokenType.String, Intern("[swordman]")));
            AddTokens("equipment/test/coat.equ",
                ((byte)TokenType.Tag, Intern("[equipment type]")), ((byte)TokenType.String, Intern("[coat]")), ((byte)TokenType.Int, 0),
                ((byte)TokenType.Tag, Intern("[usable job]")), ((byte)TokenType.String, Intern("[all]")));
            AddTokens("equipment/test/hat.equ",
                ((byte)TokenType.Tag, Intern("[equipment type]")), ((byte)TokenType.String, Intern("[hat avatar]")), ((byte)TokenType.Int, 0));
            AddTokens("equipment/test/creature.equ",
                ((byte)TokenType.Tag, Intern("[equipment type]")), ((byte)TokenType.String, Intern("[creature]")), ((byte)TokenType.Int, 0));
            AddTokens(ItemCatalog.StackableListPath,
                ((byte)TokenType.Int, PotionCode), ((byte)TokenType.String, Intern("test/potion.stk")),
                ((byte)TokenType.Int, MaterialCode), ((byte)TokenType.String, Intern("test/material.stk")));
            AddTokens("stackable/test/potion.stk",
                ((byte)TokenType.Tag, Intern("[stackable type]")), ((byte)TokenType.String, Intern("[waste]")), ((byte)TokenType.Int, 0),
                ((byte)TokenType.Tag, Intern("[stack limit]")), ((byte)TokenType.Int, 99));
            AddTokens("stackable/test/material.stk",
                ((byte)TokenType.Tag, Intern("[stackable type]")), ((byte)TokenType.String, Intern("[material]")), ((byte)TokenType.Int, 0),
                ((byte)TokenType.Tag, Intern("[stack limit]")), ((byte)TokenType.Int, 1000));

            byte[][] rawStrings = _strings.Select(s => Encoding.Latin1.GetBytes(s)).ToArray();
            _files.Add(new TestData.PvfFileSpec(GameData.StringTablePath, TestData.BuildStringTable(rawStrings), 0));
            (byte[] pvf, int _) = TestData.BuildPvf(_files, validChecksums: true);
            File.WriteAllBytes(Path.Combine(_root, "Script.pvf"), pvf);
            Assets = DnfAssets.Open(_root);
        }

        public DnfAssets Assets { get; }

        private int Intern(string text)
        {
            if (!_index.TryGetValue(text, out int idx))
            {
                idx = _strings.Count;
                _strings.Add(text);
                _index[text] = idx;
            }
            return idx;
        }

        private void AddTokens(string path, params (byte Type, int Value)[] tokens)
        {
            _files.Add(new TestData.PvfFileSpec(path, TestData.BuildTokenScript(tokens), 0));
        }

        public void Dispose()
        {
            Assets.Dispose();
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
