using Dnf.Data;
using Dnf.Game;
using static Dnf.Play.Tests.EnhanceKit;

namespace Dnf.Play.Tests;

/// <summary>
/// 强化等级与品级值换算成属性后接进主角:强化会话默认挂的 <see cref="EquipInstanceBonus"/> 把装备栏上每件装备的强化与品级结果加进装备汇总,
/// 角色属性块(<see cref="HeroProgress.WornStats"/>、<see cref="HeroProgress.Stats"/>)与攻击面板(<see cref="HeroPanel.WornEquipment(System.Func{string, EquipmentStats}, TownHero, HeroProgress)"/>)
/// 随之变化。换算本身(分路、取整、随机浮动)在 Dnf.Game.Tests 的 EquipInstanceBonusTests 里钉住;这里只看接线。物品编号 900 起,数值是合成的。
/// 强化表第 r 行(r 从 0 起):A 组 a = 100(r+1)、f1 = 7(r+1)、f2 = 0.5、f3 = 5(r+1)、f4 = 0.25、f5 = 3(r+1);B 组 b = 200(r+1)、g1 = 14(r+1)、g2 = 1.5、g3 = 10(r+1)、g4 = 2.5、g5 = 6(r+1)。
/// 增幅表与普通表同形,只是每行的强化数值(a、f1、f3、f5、b、g1、g3、g5,不含只乘权重的浮点项)整体放大 5 倍,用来看换算取的是普通表。
/// </summary>
public class EnhanceEffectTests
{
    private const int Weapon = 900;
    private const int Coat = 901;
    private const int Ring = 902;
    private const int Support = 903;

    private static ScriptValue I(int value) => new(ScriptValueKind.Int, value, null);

    private static ScriptValue F(float value) => new(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(value), null);

    /// <summary>失败阈值 20000、惩罚 1、材料 910 要 3 个的强化表,但每一行的强化数值各不相同(见类注释);<paramref name="scale"/> 把每一行的强化数值整体放大若干倍(增幅表用)。</summary>
    private static UpgradeTable EffectTable(int rows = 12, int scale = 1)
    {
        var values = new List<ScriptValue>();
        for (int r = 0; r < rows; r++)
        {
            int k = (r + 1) * scale;
            values.Add(I(100 * k));
            values.Add(F(7f * k));
            values.Add(F(0.5f));
            values.Add(F(5f * k));
            values.Add(F(0.25f));
            values.Add(F(3f * k));
            values.Add(I(20000));
            values.Add(I(1));
            values.Add(I(2));
            values.Add(I(Material));
            values.Add(I(3));
            values.Add(I(200 * k));
            values.Add(F(14f * k));
            values.Add(F(1.5f));
            values.Add(F(10f * k));
            values.Add(F(2.5f));
            values.Add(F(6f * k));
        }
        var costs = new ScriptValue[UpgradeTable.CostSlots];
        Array.Fill(costs, I(100));
        Assert.True(UpgradeTable.TryParse(new[] { new ScriptLine("[table]", values.ToArray()), new ScriptLine("[cost]", costs) }, out UpgradeTable table));
        return table;
    }

    private sealed class Rig
    {
        public HeroProgress Hero;
        public EnhanceSession Session;
        public Dictionary<int, ItemInfo> Defs;

        public ItemInfo Def(int code) => Defs[code];

        public ItemInfo Lookup(int code) => Defs.TryGetValue(code, out ItemInfo item) ? item : null;
    }

    /// <summary>成长等级 0 的装备:强化表的数值不按装备等级放大(放大的规则在 Dnf.Game.Tests 里钉住),这里的期望值才好手算。</summary>
    private static ItemInfo Piece(int code, int part, EquipStats stats = null)
    {
        ItemInfo item = Gear(code, part, stats);
        item.Grade = 0;
        return item;
    }

    private static Rig Make(params int[] rolls)
    {
        var rig = new Rig
        {
            Defs = new Dictionary<int, ItemInfo>
            {
                [Weapon] = Piece(Weapon, EquipmentParts.Weapon, new EquipStats.Builder().Set(EquipStat.EquipmentPhysicalAttack, 50).Build()),
                [Coat] = Piece(Coat, EquipmentParts.Coat, Stats(EquipStat.HpMax, 40)),
                [Ring] = Piece(Ring, EquipmentParts.Ring),
                [Support] = Piece(Support, EquipmentParts.Support),
                [Material] = Stack(Material),
            },
        };
        rig.Hero = ExpTests.Progress(level: 50);
        rig.Hero.AddGold(100000);
        rig.Hero.Bag.Add(rig.Def(Material), 30);
        var settings = new UpgradeSettings { NormalTable = EffectTable(), AmplifyTable = EffectTable(scale: 5), Random = new EnhanceRoll(rolls) };
        rig.Session = new EnhanceSession(rig.Hero, rig.Lookup, settings);
        return rig;
    }

    private static void Wear(Rig rig, int code, int gradeValue = 0)
    {
        ItemInfo def = rig.Def(code);
        ItemInfo worn = gradeValue == 0 ? def : def.WithInstance(new EquipInstance(0, gradeValue: gradeValue));
        Assert.True(rig.Hero.Worn.Restore(def.Part, worn));
    }

    private static void Succeed(Rig rig, int part)
    {
        EnhanceOutcome outcome = rig.Session.Upgrade(EnhanceTarget.OnBody(part), false, Material);
        Assert.Equal(0, outcome.Code);
        Assert.True(outcome.Upgrade.Success);
    }

    private sealed class Rates : IWeaponUpgradeRate
    {
        public float Physical { get; set; } = 1f;

        public float RateOf(ItemInfo weapon, bool physical) => physical ? Physical : 0f;
    }

    private static EquipmentStats ReadWeapon(string path)
    {
        return path.EndsWith("g900.equ", StringComparison.Ordinal) ? new EquipmentStats(50, 0) : default;
    }

    // ------------------------------------------------------------------ 防具:强化等级换成物防

    [Fact]
    public void AnArmorUpgradeRaisesThePhysicalDefenseOfTheHero()
    {
        Rig r = Make(100000, 100000);
        Wear(r, Coat);
        long baseDefense = r.Hero.Stats.PhysicalDefense;
        Assert.Equal(0.0, r.Hero.WornStats[EquipStat.EquipmentPhysicalDefense]);
        Succeed(r, EquipmentParts.Coat);
        Assert.Equal(1, r.Hero.Worn.Get(EquipmentParts.Coat).Instance.Upgrade);
        Assert.Equal(7.0, r.Hero.WornStats[EquipStat.EquipmentPhysicalDefense]);
        Assert.Equal(baseDefense + 70, r.Hero.Stats.PhysicalDefense);                       // 十分之一单位
        Succeed(r, EquipmentParts.Coat);
        Assert.Equal(14.0, r.Hero.WornStats[EquipStat.EquipmentPhysicalDefense]);           // 第 2 行
        Assert.Equal(baseDefense + 140, r.Hero.Stats.PhysicalDefense);
        Assert.Equal(40.0, r.Hero.WornStats[EquipStat.HpMax]);                              // 装备自己的生命不动
    }

    [Fact]
    public void AnAccessoryUpgradeRaisesTheMagicalDefenseAndSupportGearRaisesAllFourStats()
    {
        Rig r = Make(100000, 100000);
        Wear(r, Ring);
        Wear(r, Support);
        Quad before = Quad.Of(r.Hero.Stats);
        Succeed(r, EquipmentParts.Ring);
        Assert.Equal(5.0, r.Hero.WornStats[EquipStat.EquipmentMagicalDefense]);
        Assert.Equal(before.MagicalDefense + 50, r.Hero.Stats.MagicalDefense);
        Assert.Equal(before.PhysicalDefense, r.Hero.Stats.PhysicalDefense);
        Succeed(r, EquipmentParts.Support);
        Quad after = Quad.Of(r.Hero.Stats);
        Assert.Equal((before.PhysicalAttack + 30, before.MagicalAttack + 30, before.PhysicalDefense + 30, before.MagicalDefense + 80),
            (after.PhysicalAttack, after.MagicalAttack, after.PhysicalDefense, after.MagicalDefense));
    }

    private readonly struct Quad
    {
        public long PhysicalAttack { get; init; }

        public long MagicalAttack { get; init; }

        public long PhysicalDefense { get; init; }

        public long MagicalDefense { get; init; }

        public static Quad Of(StatBlock s) => new() { PhysicalAttack = s.PhysicalAttack, MagicalAttack = s.MagicalAttack, PhysicalDefense = s.PhysicalDefense, MagicalDefense = s.MagicalDefense };
    }

    [Fact]
    public void AnUpgradeLeftInTheBagDoesNotCountUntilTheItemIsWorn()
    {
        Rig r = Make(100000);
        r.Hero.Bag.Add(r.Def(Coat), 1);
        long baseDefense = r.Hero.Stats.PhysicalDefense;
        EnhanceOutcome o = r.Session.Upgrade(EnhanceTarget.InBag(0), false, Material);
        Assert.Equal(0, o.Code);
        Assert.Equal(baseDefense, r.Hero.Stats.PhysicalDefense);
        Assert.True(r.Hero.Worn.Restore(EquipmentParts.Coat, r.Hero.Bag.Tab(InventoryTab.Equipment)[0].Info));
        Assert.Equal(baseDefense + 70, r.Hero.Stats.PhysicalDefense);
    }

    [Fact]
    public void TakingTheItemOffTakesItsUpgradeBonusAway()
    {
        Rig r = Make(100000);
        Wear(r, Coat);
        Succeed(r, EquipmentParts.Coat);
        long boosted = r.Hero.Stats.PhysicalDefense;
        Assert.True(r.Hero.Unequip(EquipmentParts.Coat));
        Assert.Equal(boosted - 70, r.Hero.Stats.PhysicalDefense);
        Assert.True(r.Hero.WornStats.IsEmpty);
    }

    // ------------------------------------------------------------------ 武器:倍率与攻击面板

    [Fact]
    public void AWeaponUpgradeUsesTheHostsRateAndTheDefaultRateIsOne()
    {
        Rig r = Make(100000);
        Wear(r, Weapon);
        Assert.Equal(50.0, r.Hero.WornStats[EquipStat.EquipmentPhysicalAttack]);
        Succeed(r, EquipmentParts.Weapon);
        Assert.Equal(150.0, r.Hero.WornStats[EquipStat.EquipmentPhysicalAttack]);           // 50 + 100 × 1
        r.Session.InstanceEffects.WeaponRate = new Rates { Physical = 3f };
        r.Session.Overlay.Invalidate();
        Assert.Equal(350.0, r.Hero.WornStats[EquipStat.EquipmentPhysicalAttack]);           // 50 + 100 × 3
    }

    [Fact]
    public void TheAttackPanelTakesTheWeaponUpgradeAsAnAdditionalBase()
    {
        Rig r = Make(100000);
        Wear(r, Weapon);
        Assert.Equal((50, 0), Pair(HeroPanel.WornEquipment(ReadWeapon, null, r.Hero)));
        Succeed(r, EquipmentParts.Weapon);
        Assert.Equal((150, 0), Pair(HeroPanel.WornEquipment(ReadWeapon, null, r.Hero)));
        Assert.True(r.Hero.Unequip(EquipmentParts.Weapon));
        Assert.Equal((0, 0), Pair(HeroPanel.WornEquipment(ReadWeapon, null, r.Hero)));
    }

    private static (int, int) Pair(EquipmentStats stats) => (stats.PhysicalAttack, stats.MagicalAttack);

    private sealed class FixedBonus : IEquipInstanceBonus
    {
        public EquipStats Bonus { get; set; }

        public EquipStats BonusOf(ItemInfo worn) => worn.Instance == null ? null : Bonus;
    }

    [Fact]
    public void TheAttackPanelReadsAttackAndCritFromTheOverlayTermsAndCutsAttackTowardZero()
    {
        Rig r = Make(100000);
        r.Session.Overlay.InstanceBonus = new FixedBonus
        {
            Bonus = new EquipStats.Builder()
                .Set(EquipStat.EquipmentPhysicalAttack, 8.9)
                .Set(EquipStat.EquipmentMagicalAttack, -1.5)
                .Set(EquipStat.PhysicalCriticalHit, 4)
                .Set(EquipStat.MagicalCriticalHit, 2.5)
                .Set(EquipStat.HpMax, 99)                                                    // 不进攻击面板
                .Build(),
        };
        Wear(r, Ring, gradeValue: 3);                                                        // 带个体状态才有加成
        EquipmentStats panel = HeroPanel.WornEquipment(ReadWeapon, null, r.Hero);
        Assert.Equal((8, -1, 4f, 2.5f), (panel.PhysicalAttack, panel.MagicalAttack, panel.PhysicalCritical, panel.MagicalCritical));
    }

    [Fact]
    public void ABodyWithoutAnOverlayKeepsThePanelAsItWas()
    {
        HeroProgress plain = ExpTests.Progress(level: 50);
        Assert.Null(plain.Worn.Overlay);
        Assert.True(plain.Worn.Restore(EquipmentParts.Weapon, Piece(Weapon, EquipmentParts.Weapon)));
        Assert.Equal((50, 0), Pair(HeroPanel.WornEquipment(ReadWeapon, null, plain)));
    }

    // ------------------------------------------------------------------ 品级值

    [Fact]
    public void AGradeValueVariesTheBaseStatsOnlyOnceTheHostSuppliesTheVariationData()
    {
        Rig r = Make();
        Wear(r, Coat, gradeValue: 7);
        Assert.Equal(40.0, r.Hero.WornStats[EquipStat.HpMax]);                               // 没有数据:不浮动
        r.Session.InstanceEffects.GradeVariation = new EquipGradeVariation(Enumerable.Repeat(10, 17).ToArray(), Enumerable.Repeat(50, 17).ToArray());
        r.Session.Overlay.Invalidate();
        Assert.Equal(37.0, r.Hero.WornStats[EquipStat.HpMax]);                               // 种子 7:档 1,生命 40 × 10% = 4,浮动 −3(Python 参考实现算的)
        long hp = r.Hero.Stats.HpMax;
        r.Session.InstanceEffects.GradeEnabled = false;
        r.Session.Overlay.Invalidate();
        Assert.Equal(hp + 30, r.Hero.Stats.HpMax);
        Assert.Equal(40.0, r.Hero.WornStats[EquipStat.HpMax]);
    }

    [Fact]
    public void AGradeValueAndAnUpgradeStackOnTheSameItem()
    {
        Rig r = Make(100000);
        r.Session.InstanceEffects.GradeVariation = new EquipGradeVariation(Enumerable.Repeat(10, 17).ToArray(), Enumerable.Repeat(50, 17).ToArray());
        Wear(r, Weapon, gradeValue: 7);
        r.Session.Overlay.Invalidate();
        Assert.Equal(47.0, r.Hero.WornStats[EquipStat.EquipmentPhysicalAttack]);             // 50 − 3(种子 7、档 1、幅度 5,Python 参考实现算的)
        Succeed(r, EquipmentParts.Weapon);
        Assert.Equal(7, r.Hero.Worn.Get(EquipmentParts.Weapon).Instance.GradeValue);         // 强化不丢品级值
        Assert.Equal(147.0, r.Hero.WornStats[EquipStat.EquipmentPhysicalAttack]);            // 47 + 100
    }
}
