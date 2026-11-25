using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Game;
using Dnf.Game.Tests;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// Stage 1 联网主角物品同步测试(<see cref="HeroProgress.ApplyOnlineItems"/>、<see cref="OnlineItemKindLookup"/>、
/// <see cref="OnlineHeroProfile.ApplyItemSyncState"/>、<see cref="OnlineTownOverlay.StatusBadgeText"/>、<see cref="OnlineTownLog.Enter"/>)。
/// 全用合成编号(900001+)，预期值全部手算字面量，绝不触碰 <see cref="HeroSave"/>，绝不改既有测试或离线路径。
/// </summary>
public sealed class OnlineHeroItemSyncTests
{
    private const int WeaponCode = 900001;
    private const int MagicStoneCode = 900002;
    private const int FighterCoatCode = 900003;
    private const int HatAvatarCode = 900004;
    private const int CreatureCode = 900005;
    private const int ArtifactRedCode = 900006;
    private const int PotionCode = 901001;
    private const int MaterialCode = 901002;
    private const int QuestItemCode = 901003;
    private const int ThrowItemCode = 901004;
    private const int UnknownCode = 909999;

    private const int ExtraEquipBase = 900100;
    private const int ExtraEquipCount = 58;

    private const string SwordmanChrInList = "swordman/test.chr";
    private const string SwordmanChr = "character/swordman/test.chr";

    private static GameData BuildSyntheticGameData()
    {
        var thresholds = new int[99];
        for (int i = 0; i < thresholds.Length; i++)
        {
            thresholds[i] = (i + 1) * 100;
        }
        var fx = new Fixture();
        fx.List(GameData.CharacterListPath, (EquipRules.SwordmanJob, SwordmanChrInList))
            .Tokens(SwordmanChr, w => w.Tag("[job]").Str("[swordman]"));
        fx.Tokens(ExpTable.Path, w => w.Ints(thresholds))
            .Tokens(SpTable.Path, w =>
            {
                w.Tag("[sp table]");
                for (int level = 1; level <= 60; level++)
                {
                    w.Int(level).Int(3);
                }
            });

        var equipList = new List<(int Id, string Path)>
        {
            (WeaponCode, "test/weapon.equ"),
            (MagicStoneCode, "test/stone.equ"),
            (FighterCoatCode, "test/fighter_coat.equ"),
            (HatAvatarCode, "test/hat_avatar.equ"),
            (CreatureCode, "test/creature.equ"),
            (ArtifactRedCode, "test/artifact_red.equ"),
        };
        for (int i = 0; i < ExtraEquipCount; i++)
        {
            int code = ExtraEquipBase + i;
            equipList.Add((code, "test/extra_" + i + ".equ"));
        }
        fx.List(ItemCatalog.EquipmentListPath, equipList.ToArray());

        fx.Tokens("equipment/test/weapon.equ", w => w
            .Tag("[equipment type]").Str("[weapon]").Int(0)
            .Tag("[usable job]").Str("[swordman]")
            .Tag("[minimum level]").Int(10));
        fx.Tokens("equipment/test/stone.equ", w => w
            .Tag("[equipment type]").Str("[magic stone]").Int(0)
            .Tag("[usable job]").Str("[all]")
            .Tag("[minimum level]").Int(60));
        fx.Tokens("equipment/test/fighter_coat.equ", w => w
            .Tag("[equipment type]").Str("[coat]").Int(0)
            .Tag("[usable job]").Str("[fighter]")
            .Tag("[minimum level]").Int(70));
        fx.Tokens("equipment/test/hat_avatar.equ", w => w
            .Tag("[equipment type]").Str("[hat avatar]").Int(0)
            .Tag("[usable job]").Str("[all]"));
        fx.Tokens("equipment/test/creature.equ", w => w
            .Tag("[equipment type]").Str("[creature]").Int(0)
            .Tag("[usable job]").Str("[all]"));
        fx.Tokens("equipment/test/artifact_red.equ", w => w
            .Tag("[equipment type]").Str("[artifact red]").Int(0)
            .Tag("[usable job]").Str("[all]"));
        for (int i = 0; i < ExtraEquipCount; i++)
        {
            int idx = i;
            fx.Tokens("equipment/test/extra_" + idx + ".equ", w => w
                .Tag("[equipment type]").Str("[ring]").Int(0)
                .Tag("[usable job]").Str("[all]"));
        }

        fx.List(ItemCatalog.StackableListPath,
            (PotionCode, "test/potion.stk"),
            (MaterialCode, "test/material.stk"),
            (QuestItemCode, "test/quest.stk"),
            (ThrowItemCode, "test/throw.stk"));

        fx.Tokens("stackable/test/potion.stk", w => w
            .Tag("[stackable type]").Str("[waste]").Int(0)
            .Tag("[stack limit]").Int(100)
            .Tag("[hp recovery]").Str("+").Int(50).Int(0).Str("myself"));
        fx.Tokens("stackable/test/material.stk", w => w
            .Tag("[stackable type]").Str("[material]").Int(0)
            .Tag("[stack limit]").Int(1000));
        fx.Tokens("stackable/test/quest.stk", w => w
            .Tag("[stackable type]").Str("[quest]").Int(0)
            .Tag("[stack limit]").Int(50));
        fx.Tokens("stackable/test/throw.stk", w => w
            .Tag("[stackable type]").Str("[throw]").Int(0)
            .Tag("[stack limit]").Int(99));

        return new GameData(fx.Build());
    }

    private static OnlineHeroProfile Profile(int job = 0, int growTypeByte = 0x00, int level = 20)
    {
        var entry = new OnlineCharacterEntry(9301, Encoding.UTF8.GetBytes("测试角色1"), 0, (byte)job, (byte)growTypeByte, (byte)level,
            0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        return OnlineHeroProfile.FromEntry(entry, 0);
    }

    private static HeroProgress OnlineHero(int job = 0, int level = 20)
    {
        return HeroProgress.CreateOnline(BuildSyntheticGameData(), null, Profile(job: job, level: level));
    }

    private sealed class SyntheticItemAssets : IDisposable
    {
        private readonly string _root;
        private readonly List<string> _strings = new List<string>();
        private readonly Dictionary<string, int> _stringIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<TestData.PvfFileSpec> _files = new List<TestData.PvfFileSpec>();

        public SyntheticItemAssets()
        {
            _root = Path.Combine(Path.GetTempPath(), "dnf-onlineitems-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "ImagePacks2"));

            var thresholds = new int[99];
            for (int i = 0; i < thresholds.Length; i++)
            {
                thresholds[i] = (i + 1) * 100;
            }
            AddTokens(ExpTable.Path, thresholds.Select(t => ((byte)TokenType.Int, t)).ToArray());
            AddTokens(GameData.CharacterListPath,
                ((byte)TokenType.Int, EquipRules.SwordmanJob),
                ((byte)TokenType.String, Intern(SwordmanChrInList)));
            AddTokens(SwordmanChr,
                ((byte)TokenType.Tag, Intern("[job]")),
                ((byte)TokenType.String, Intern("[swordman]")));
            AddTokens(ItemCatalog.EquipmentListPath,
                ((byte)TokenType.Int, WeaponCode), ((byte)TokenType.String, Intern("test/weapon.equ")),
                ((byte)TokenType.Int, HatAvatarCode), ((byte)TokenType.String, Intern("test/hat.equ")),
                ((byte)TokenType.Int, CreatureCode), ((byte)TokenType.String, Intern("test/creature.equ")),
                ((byte)TokenType.Int, ArtifactRedCode), ((byte)TokenType.String, Intern("test/artifact.equ")));
            AddTokens("equipment/test/weapon.equ",
                ((byte)TokenType.Tag, Intern("[equipment type]")), ((byte)TokenType.String, Intern("[weapon]")), ((byte)TokenType.Int, 0),
                ((byte)TokenType.Tag, Intern("[usable job]")), ((byte)TokenType.String, Intern("[swordman]")));
            AddTokens("equipment/test/hat.equ",
                ((byte)TokenType.Tag, Intern("[equipment type]")), ((byte)TokenType.String, Intern("[hat avatar]")), ((byte)TokenType.Int, 0));
            AddTokens("equipment/test/creature.equ",
                ((byte)TokenType.Tag, Intern("[equipment type]")), ((byte)TokenType.String, Intern("[creature]")), ((byte)TokenType.Int, 0));
            AddTokens("equipment/test/artifact.equ",
                ((byte)TokenType.Tag, Intern("[equipment type]")), ((byte)TokenType.String, Intern("[artifact red]")), ((byte)TokenType.Int, 0));
            AddTokens(ItemCatalog.StackableListPath,
                ((byte)TokenType.Int, PotionCode), ((byte)TokenType.String, Intern("test/potion.stk")));
            AddTokens("stackable/test/potion.stk",
                ((byte)TokenType.Tag, Intern("[stackable type]")), ((byte)TokenType.String, Intern("[waste]")), ((byte)TokenType.Int, 0),
                ((byte)TokenType.Tag, Intern("[stack limit]")), ((byte)TokenType.Int, 99));

            byte[][] rawStrings = _strings.Select(s => Encoding.Latin1.GetBytes(s)).ToArray();
            _files.Add(new TestData.PvfFileSpec(GameData.StringTablePath, TestData.BuildStringTable(rawStrings), 0));
            (byte[] pvf, int _) = TestData.BuildPvf(_files, validChecksums: true);
            File.WriteAllBytes(Path.Combine(_root, "Script.pvf"), pvf);
            Assets = DnfAssets.Open(_root);
        }

        public DnfAssets Assets { get; }

        private int Intern(string text)
        {
            if (!_stringIndex.TryGetValue(text, out int idx))
            {
                idx = _strings.Count;
                _strings.Add(text);
                _stringIndex[text] = idx;
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

    // ───────────────────────── 离线保护与参数守卫 ─────────────────────────

    [Fact]
    public void Offline_hero_progress_is_not_online_and_rejects_apply_online_items()
    {
        GameData data = BuildSyntheticGameData();
        CharacterDef chr = data.GetCharacter(EquipRules.SwordmanJob);
        HeroProgress offlineFromFactory = HeroProgress.Create(data, chr, null, level: 5);
        HeroProgress offlineCtor = new HeroProgress(data.ExpTable, data.SpTable, data.GetGrowth(chr), null, data.ExpRates);
        using var fx = new HostedRoomFixture();
        HeroProgress localHero = HeroProgress.CreateForLocalHero(fx.Assets, 3);

        Assert.False(offlineFromFactory.IsOnline);
        Assert.False(offlineCtor.IsOnline);
        Assert.False(localHero.IsOnline);

        var payload = new OnlineItemSyncData(true, 500, null, null, null, false, false);
        Assert.Throws<InvalidOperationException>(() => offlineFromFactory.ApplyOnlineItems(payload));
        Assert.Throws<InvalidOperationException>(() => offlineCtor.ApplyOnlineItems(payload));
        Assert.Throws<InvalidOperationException>(() => localHero.ApplyOnlineItems(payload));
        Assert.Equal(0L, offlineFromFactory.Gold);
        Assert.Equal(0L, localHero.Gold);
    }

    [Fact]
    public void Apply_online_items_rejects_null_data_by_name_and_marks_online_flag()
    {
        HeroProgress hero = OnlineHero();

        Assert.True(hero.IsOnline);
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => hero.ApplyOnlineItems(null));
        Assert.Equal("data", ex.ParamName);
    }

    // ───────────────────────── 金币同步与零落盘隔离 ─────────────────────────

    [Theory]
    [InlineData(true, 0L, 0L, true)]
    [InlineData(true, 1L, 1L, true)]
    [InlineData(true, 4294967295L, 4294967295L, true)]
    [InlineData(true, -77L, 0L, true)]
    [InlineData(false, 9999L, 777L, false)]
    public void Gold_sync_sets_or_preserves_gold_clamps_negatives_and_never_touches_gold_gained(
        bool hasGold, long incomingGold, long expectedGold, bool expectedSynced)
    {
        HeroProgress hero = OnlineHero();
        hero.ApplyOnlineItems(new OnlineItemSyncData(true, 777L, null, null, null, false, false));
        Assert.Equal(777L, hero.Gold);

        OnlineItemSyncReport report = hero.ApplyOnlineItems(new OnlineItemSyncData(hasGold, incomingGold, null, null, null, false, false));

        Assert.Equal(expectedSynced, report.GoldSynced);
        Assert.Equal(expectedGold, hero.Gold);
        Assert.Equal(0L, hero.GoldGained);
        Assert.Equal(0, hero.ItemsGained);
    }

    [Fact]
    public void Create_for_online_hero_with_items_applies_sync_without_writing_any_save_file()
    {
        using var assets = new SyntheticItemAssets();
        string saveDir = Path.Combine(Path.GetTempPath(), "dnf-onlineitems-save-" + Guid.NewGuid().ToString("N"));
        string savePath = Path.Combine(saveDir, "hero.sav");

        var data = new OnlineItemSyncData(
            hasGold: true,
            gold: 88888L,
            bagEntries: new[] { new OnlineItemSlotEntry(9, PotionCode, 12) },
            quickEntries: new[] { new OnlineItemSlotEntry(3, PotionCode, 5) },
            wornEquips: new[] { new OnlineItemSlotEntry(10, WeaponCode, 1, upgradeLevel: 7) },
            hasBagData: true,
            hasEquipData: true);

        HeroProgress hero = HeroProgress.CreateForOnlineHero(assets.Assets, Profile(), data);

        Assert.NotNull(hero);
        Assert.True(hero.IsOnline);
        Assert.Equal(88888L, hero.Gold);
        Assert.Equal(0L, hero.GoldGained);
        Assert.Equal(17, hero.Bag.CountOf(PotionCode));
        Assert.Equal(PotionCode, hero.QuickCodeAt(0));
        Assert.Equal(17, hero.QuickCountAt(0));
        Assert.Equal(WeaponCode, hero.Worn.Get(EquipmentParts.Weapon)?.Code);
        Assert.Equal(7, hero.Worn.Get(EquipmentParts.Weapon)?.Instance?.Upgrade);
        Assert.Equal(0, hero.ItemsGained);
        Assert.False(File.Exists(savePath));
        Assert.False(Directory.Exists(saveDir));
    }

    // ───────────────────────── 快捷栏 3..8 与背包同步 ─────────────────────────

    [Fact]
    public void Quick_slots_three_through_eight_populate_bag_and_quickbar_and_ignore_out_of_range_quick_indices()
    {
        HeroProgress hero = OnlineHero();
        var data = new OnlineItemSyncData(
            hasGold: false,
            gold: 0L,
            bagEntries: new[]
            {
                new OnlineItemSlotEntry(9, PotionCode, 10),
                new OnlineItemSlotEntry(10, MaterialCode, 250),
                new OnlineItemSlotEntry(11, QuestItemCode, 0),    // count 0 -> 夹成 1
                new OnlineItemSlotEntry(12, WeaponCode, 5),       // 装备无论 Data 多大只加 1 件
                new OnlineItemSlotEntry(13, UnknownCode, 3),      // 未知物号跳过并计数
            },
            quickEntries: new[]
            {
                new OnlineItemSlotEntry(3, PotionCode, 4),        // 快捷槽 0
                new OnlineItemSlotEntry(8, ThrowItemCode, 7),     // 快捷槽 5
                new OnlineItemSlotEntry(4, PotionCode, 2),        // 同编号重复放进另一快捷槽:背包加 2,Quick.Set 返回 false
                new OnlineItemSlotEntry(2, MaterialCode, 9),      // 越界快捷槽:只入背包,不设 Quick
                new OnlineItemSlotEntry(7, UnknownCode, 1),       // 未知快捷物号跳过并计数
            },
            wornEquips: null,
            hasBagData: true,
            hasEquipData: false);

        OnlineItemSyncReport report = hero.ApplyOnlineItems(data);

        Assert.True(report.InventorySynced);
        Assert.False(report.GoldSynced);
        Assert.False(report.EquipsSynced);
        Assert.Equal(2, report.QuickApplied);
        Assert.Equal(4, report.BagApplied);
        Assert.Equal(2, report.UnknownItemCount);
        Assert.Equal(0, report.OverflowItemCount);

        Assert.Equal(PotionCode, hero.QuickCodeAt(0));
        Assert.Equal(ThrowItemCode, hero.QuickCodeAt(5));
        Assert.Equal(0, hero.QuickCodeAt(1));
        Assert.Equal(16, hero.QuickCountAt(0)); // 4 + 2 + 10 = 16
        Assert.Equal(7, hero.QuickCountAt(5));
        Assert.Equal(2, hero.QuickFilled);

        Assert.Equal(16, hero.Bag.CountOf(PotionCode));
        Assert.Equal(259, hero.Bag.CountOf(MaterialCode)); // 9 + 250
        Assert.Equal(1, hero.Bag.CountOf(QuestItemCode));
        Assert.Equal(1, hero.Bag.CountOf(WeaponCode));
        Assert.Equal(0, hero.ItemsGained);
    }

    [Fact]
    public void Bag_overflow_across_fifty_six_tab_slots_counts_overflow_items_and_keeps_valid_ones()
    {
        HeroProgress hero = OnlineHero();
        var bagEntries = new List<OnlineItemSlotEntry>();
        // 58 件不同编号装备 -> 装备页上限 56 格,前 56 件入包,后 2 件溢出
        for (int i = 0; i < ExtraEquipCount; i++)
        {
            bagEntries.Add(new OnlineItemSlotEntry((ushort)(9 + i), (uint)(ExtraEquipBase + i), 1));
        }
        // 材料页单格上限 1000:56 格满 + 第 57 条 75 件全部溢出
        for (int i = 0; i < Inventory.SlotsPerTab; i++)
        {
            bagEntries.Add(new OnlineItemSlotEntry((ushort)(100 + i), MaterialCode, 1000));
        }
        bagEntries.Add(new OnlineItemSlotEntry(200, MaterialCode, 75));

        OnlineItemSyncReport report = hero.ApplyOnlineItems(new OnlineItemSyncData(
            false, 0L, bagEntries, null, null, hasBagData: true, hasEquipData: false));

        Assert.Equal(Inventory.SlotsPerTab, hero.Bag.Tab(InventoryTab.Equipment).Count);
        Assert.Equal(Inventory.SlotsPerTab, hero.Bag.Tab(InventoryTab.Material).Count);
        Assert.Equal(56 * 2, report.BagApplied);
        Assert.Equal(2 + 75, report.OverflowItemCount);
        Assert.Equal(56000, hero.Bag.CountOf(MaterialCode));
    }

    [Fact]
    public void Reapplying_bag_data_clears_previous_bag_and_quick_while_false_preserves_them()
    {
        HeroProgress hero = OnlineHero();
        hero.ApplyOnlineItems(new OnlineItemSyncData(
            false, 0L,
            new[] { new OnlineItemSlotEntry(9, MaterialCode, 11) },
            new[] { new OnlineItemSlotEntry(3, PotionCode, 5) },
            null,
            hasBagData: true,
            hasEquipData: false));

        Assert.Equal(11, hero.Bag.CountOf(MaterialCode));
        Assert.Equal(5, hero.QuickCountAt(0));

        // HasBagData = false:保留现有背包与快捷栏
        hero.ApplyOnlineItems(new OnlineItemSyncData(true, 100L, null, null, null, hasBagData: false, hasEquipData: false));
        Assert.Equal(11, hero.Bag.CountOf(MaterialCode));
        Assert.Equal(5, hero.QuickCountAt(0));

        // HasBagData = true 且空表:彻底清空
        hero.ApplyOnlineItems(new OnlineItemSyncData(false, 0L, null, null, null, hasBagData: true, hasEquipData: false));
        Assert.Equal(0, hero.Bag.TotalCount);
        Assert.Equal(0, hero.QuickFilled);
    }

    [Fact]
    public void Item_code_above_int_max_is_treated_as_unknown_without_throwing()
    {
        HeroProgress hero = OnlineHero();
        OnlineItemSyncReport report = hero.ApplyOnlineItems(new OnlineItemSyncData(
            false, 0L,
            new[] { new OnlineItemSlotEntry(9, uint.MaxValue, 1) },
            null,
            new[] { new OnlineItemSlotEntry(10, uint.MaxValue, 1) },
            hasBagData: true,
            hasEquipData: true));

        Assert.Equal(2, report.UnknownItemCount);
        Assert.Equal(0, report.BagApplied);
        Assert.Equal(0, report.EquipsApplied);
    }

    // ───────────────────────── 身上装备 10..21 与边界 ─────────────────────────

    [Fact]
    public void Worn_equips_boundary_slots_ten_and_twentyone_equip_while_nine_and_twentytwo_are_counted_invalid()
    {
        HeroProgress hero = OnlineHero(job: 2, level: 1); // 枪手退回鬼剑士、等级 1,仍可穿戴 70 级格斗上衣与 60 级魔法石
        Assert.True(hero.IsJobFallback);

        OnlineItemSyncReport report = hero.ApplyOnlineItems(new OnlineItemSyncData(
            hasGold: false,
            gold: 0L,
            bagEntries: null,
            quickEntries: null,
            wornEquips: new[]
            {
                new OnlineItemSlotEntry(9, HatAvatarCode, 1),                                                     // 越界: 9
                new OnlineItemSlotEntry(10, WeaponCode, 1, upgradeLevel: 12, isAmplified: true, isIdentified: false, enchantCode: 905001, gradeValue: 88),
                new OnlineItemSlotEntry(12, FighterCoatCode, 1),                                                  // 跨职业+超等级
                new OnlineItemSlotEntry(21, MagicStoneCode, 1),                                                   // 边界上界 21
                new OnlineItemSlotEntry(22, CreatureCode, 1),                                                     // 越界: 22
                new OnlineItemSlotEntry(13, UnknownCode, 1),                                                      // 未知物号
                new OnlineItemSlotEntry(14, PotionCode, 1),                                                       // 非装备
            },
            hasBagData: false,
            hasEquipData: true));

        Assert.True(report.EquipsSynced);
        Assert.Equal(3, report.EquipsApplied);
        Assert.Equal(2, report.InvalidEquipSlotCount);
        Assert.Equal(2, report.UnknownItemCount);

        ItemInfo weapon = hero.Worn.Get(EquipmentParts.Weapon);
        Assert.NotNull(weapon);
        Assert.Equal(WeaponCode, weapon.Code);
        Assert.NotNull(weapon.Instance);
        Assert.Equal(12, weapon.Instance.Upgrade);
        Assert.True(weapon.Instance.Amplified);
        Assert.False(weapon.Instance.Identified);
        Assert.Equal(905001, weapon.Instance.EnchantCode);
        Assert.Equal(88, weapon.Instance.GradeValue);

        Assert.Equal(FighterCoatCode, hero.Worn.Get(EquipmentParts.Coat)?.Code);
        Assert.Equal(MagicStoneCode, hero.Worn.Get(EquipmentParts.MagicStone)?.Code);
        Assert.Null(hero.Worn.Get(EquipmentParts.Shoulder));
        // 默认实例属性(升级 0、未增幅、已鉴定、附魔 0、品级 0)保持共享定义(Instance 为 null);
        // 仅品级为 1 时也生成个体拷贝;升级等级 255 保持 255、256 夹成 255 不越界。
        HeroProgress instanceEdgeHero = OnlineHero();
        instanceEdgeHero.ApplyOnlineItems(new OnlineItemSyncData(
            false, 0L, null, null,
            new[]
            {
                new OnlineItemSlotEntry(10, WeaponCode, 1),
                new OnlineItemSlotEntry(12, FighterCoatCode, 1, gradeValue: 1),
                new OnlineItemSlotEntry(21, MagicStoneCode, 1, upgradeLevel: 255),
            },
            false, true));

        Assert.Null(instanceEdgeHero.Worn.Get(EquipmentParts.Weapon)?.Instance);
        Assert.False(instanceEdgeHero.Worn.Get(EquipmentParts.Weapon)?.IsInstance ?? true);
        Assert.Equal(1, instanceEdgeHero.Worn.Get(EquipmentParts.Coat)?.Instance?.GradeValue);
        Assert.Equal(255, instanceEdgeHero.Worn.Get(EquipmentParts.MagicStone)?.Instance?.Upgrade);

        instanceEdgeHero.ApplyOnlineItems(new OnlineItemSyncData(
            false, 0L, null, null,
            new[]
            {
                new OnlineItemSlotEntry(10, WeaponCode, 1, upgradeLevel: 256),
                new OnlineItemSlotEntry(21, MagicStoneCode, 1, isAmplified: true),
            },
            false, true));
        Assert.Equal(255, instanceEdgeHero.Worn.Get(EquipmentParts.Weapon)?.Instance?.Upgrade);
        Assert.True(instanceEdgeHero.Worn.Get(EquipmentParts.MagicStone)?.Instance?.Amplified);
    }

    [Fact]
    public void Unmodeled_counts_are_passed_through_verbatim_and_negatives_clamped_to_zero()
    {
        HeroProgress hero = OnlineHero();
        var data = new OnlineItemSyncData(false, 0L, null, null, null, false, false,
            unmodeledAvatarBagCount: 11,
            unmodeledCargoCount: 22,
            unmodeledPetBagCount: 33,
            unmodeledAccountCargoCount: 44,
            unmodeledExpandEquipCount: 55);

        OnlineItemSyncReport report = hero.ApplyOnlineItems(data);

        Assert.Equal(11, report.UnmodeledAvatarBagCount);
        Assert.Equal(22, report.UnmodeledCargoCount);
        Assert.Equal(33, report.UnmodeledPetBagCount);
        Assert.Equal(44, report.UnmodeledAccountCargoCount);
        Assert.Equal(55, report.UnmodeledExpandEquipCount);

        var clamped = new OnlineItemSyncData(false, 0L, null, null, null, false, false, -1, -2, -3, -4, -5);
        Assert.Equal(0, clamped.UnmodeledAvatarBagCount);
        Assert.Equal(0, clamped.UnmodeledCargoCount);
        Assert.Equal(0, clamped.UnmodeledPetBagCount);
        Assert.Equal(0, clamped.UnmodeledAccountCargoCount);
        Assert.Equal(0, clamped.UnmodeledExpandEquipCount);
    }

    // ───────────────────────── 物品类型查询 ─────────────────────────

    [Fact]
    public void Online_item_kind_lookup_resolves_avatar_pet_other_and_unknown_without_throwing()
    {
        using var assets = new SyntheticItemAssets();

        Assert.Equal(0, OnlineItemKindLookup.ResolveKindCode(null, WeaponCode));
        Assert.Equal(0, OnlineItemKindLookup.ResolveKindCode(assets.Assets, UnknownCode));
        Assert.Equal(0, OnlineItemKindLookup.ResolveKindCode(assets.Assets, uint.MaxValue));
        Assert.Equal(1, OnlineItemKindLookup.ResolveKindCode(assets.Assets, WeaponCode));
        Assert.Equal(1, OnlineItemKindLookup.ResolveKindCode(assets.Assets, PotionCode));
        Assert.Equal(1, OnlineItemKindLookup.ResolveKindCode(assets.Assets, ArtifactRedCode));
        Assert.Equal(2, OnlineItemKindLookup.ResolveKindCode(assets.Assets, HatAvatarCode));
        Assert.Equal(3, OnlineItemKindLookup.ResolveKindCode(assets.Assets, CreatureCode));
    }

    // ───────────────────────── 同步标志与文案/日志不变量 ─────────────────────────

    [Theory]
    [InlineData(0, false, false, false, true, "联机城镇 · 道具/装备/金币暂未同步")]
    [InlineData(0, false, false, false, false, "ONLINE TOWN / ITEMS EQUIPS GOLD UNSYNCED")]
    [InlineData(2, false, false, false, true, "当前仅支持鬼剑士外观与技能，暂按鬼剑士显示 · 道具/装备/金币未同步")]
    [InlineData(2, false, false, false, false, "SWORDMAN FALLBACK / ITEMS EQUIPS GOLD UNSYNCED")]
    [InlineData(0, true, true, true, true, "联机城镇 · 技能未同步")]
    [InlineData(0, true, true, true, false, "ONLINE TOWN / SKILLS UNSYNCED")]
    [InlineData(2, true, true, true, true, "当前仅支持鬼剑士外观与技能，暂按鬼剑士显示 · 技能未同步")]
    [InlineData(2, true, true, true, false, "SWORDMAN FALLBACK / SKILLS UNSYNCED")]
    [InlineData(0, true, false, false, true, "联机城镇 · 道具/装备/技能未同步")]
    [InlineData(0, true, false, false, false, "ONLINE TOWN / ITEMS EQUIPS SKILLS UNSYNCED")]
    [InlineData(0, false, true, false, true, "联机城镇 · 装备/金币/技能未同步")]
    [InlineData(0, false, true, false, false, "ONLINE TOWN / EQUIPS GOLD SKILLS UNSYNCED")]
    [InlineData(0, false, false, true, true, "联机城镇 · 道具/金币/技能未同步")]
    [InlineData(0, false, false, true, false, "ONLINE TOWN / ITEMS GOLD SKILLS UNSYNCED")]
    public void Status_badge_text_matches_exact_literal_for_every_sync_combination(
        int job, bool gold, bool inventory, bool equips, bool hasFonts, string expected)
    {
        OnlineHeroProfile profile = Profile(job: job);
        profile.ApplyItemSyncState(gold, inventory, equips);

        Assert.Equal(expected, OnlineTownOverlay.StatusBadgeText(profile, hasFonts));
    }

    [Theory]
    [InlineData(false, false, false, "DNF_ONLINE stage=town event=enter mode=online unsynced=inventory,equips,gold jobfallback=false townfallback=false areasync=on perms=none snap=none slot=na level=na job=na grow=na")]
    [InlineData(true, true, true, "DNF_ONLINE stage=town event=enter mode=online unsynced=skills jobfallback=false townfallback=false areasync=on perms=none snap=none slot=na level=na job=na grow=na")]
    [InlineData(true, false, false, "DNF_ONLINE stage=town event=enter mode=online unsynced=inventory,equips,skills jobfallback=false townfallback=false areasync=on perms=none snap=none slot=na level=na job=na grow=na")]
    [InlineData(false, true, true, "DNF_ONLINE stage=town event=enter mode=online unsynced=gold,skills jobfallback=false townfallback=false areasync=on perms=none snap=none slot=na level=na job=na grow=na")]
    public void Enter_log_updates_unsynced_list_dynamically_while_preserving_legacy_default_byte_for_byte(
        bool gold, bool inventory, bool equips, string expected)
    {
        OnlineHeroProfile profile = Profile(job: 0);
        profile.ApplyItemSyncState(gold, inventory, equips);

        string line = OnlineTownLog.Enter(profile, townFallback: false, areaSyncEnabled: true);

        Assert.Equal(expected, line);
        Assert.DoesNotContain(line, char.IsDigit);
    }
}
