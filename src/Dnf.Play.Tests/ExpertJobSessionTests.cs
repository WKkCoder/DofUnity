using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Tests;
using static Dnf.Game.Tests.ExpertJobTestData;

namespace Dnf.Play.Tests;

/// <summary>
/// 副职业会话(本项目自己的接线,规则在 Dnf.Game 的 ExpertJob;依据见那边各文件开头的服务端地址):就职、放弃、加经验、分解、便携分解、预览、整修与升档、存档的接线,
/// 以及"要么全做、要么什么都不动"。数据全是合成的(见 <see cref="ExpertJobTestData"/>),期望值是手算,随机数用脚本化来源钉死次序与范围。
/// <para>
/// 手算基准(价值 1000、物品等级 20、稀有度 1、机器档 0、倍率中性):价格先乘 1.1 取整 1100,收购价 1100,乘 1000 除基数 100 得 11000,乘行系数 1.5 除 1000 得 16(向零取整);
/// 大成功线 10% = 1000(抽值小于它走大成功组,否则走普通组);普通组 951(权重 6000、除数 10)与 952(权重 4000、除数 5),大成功组 960(权重 10000、除数 2)。
/// 一次分解的抽取次序:大成功、组内挑选、(稀有度不低于 2)自用额外产物、耐久消耗(1 到 3)、经验(2 到 4)、额外经验(0 到 99)。
/// </para>
/// </summary>
public class ExpertJobSessionTests
{
    // ------------------------------------------------------------------ 夹具

    private sealed class Rig
    {
        public HeroProgress Hero;
        public ExpertJobSession Jobs;
        public ScriptedDisjointRandom Rng;
        public readonly Dictionary<int, ItemInfo> Items = new Dictionary<int, ItemInfo>();
        public readonly Dictionary<int, ExpertJobItemTags> Tags = new Dictionary<int, ExpertJobItemTags>();

        /// <summary>把物品放进背包(同时登记进"物品数据"与标签表)。</summary>
        public ItemInfo Put(ItemInfo item, int count = 1, ExpertJobItemTags tags = null)
        {
            Items[item.Code] = item;
            if (tags != null)
            {
                Tags[item.Code] = tags;
            }
            Assert.True(Hero.AddItem(item, count));
            return item;
        }
    }

    private static Rig Make(int charLevel = 30, long gold = 0, bool hired = true, int[] rolls = null, DisjointPolicy policy = null, ItemPriceRates rates = null,
        ExpertJobCatalog catalog = null)
    {
        var rig = new Rig();
        rig.Hero = ExpTests.Progress(level: charLevel);
        rig.Hero.AddGold(gold);
        rig.Rng = new ScriptedDisjointRandom(rolls ?? new int[0]);
        for (int code = 948; code <= 970; code++)
        {
            rig.Items[code] = Material(code);
        }
        rig.Jobs = new ExpertJobSession(
            rig.Hero,
            catalog ?? Catalog(rates),
            code => rig.Items.TryGetValue(code, out ItemInfo info) ? info : null,
            item => rig.Tags.TryGetValue(item.Code, out ExpertJobItemTags tags) ? tags : null,
            rig.Rng,
            policy);
        if (hired)
        {
            Assert.True(rig.Jobs.TakeJob(ExpertJobKind.Disjointer).Ok);
        }
        return rig;
    }

    private static KeyValuePair<int, int> P(int item, int count) => new KeyValuePair<int, int>(item, count);

    private static ExpertJobItemTags TagsOf(params ScriptLine[] lines) => ExpertJobItemTags.Parse(lines);

    /// <summary>用互不相同的材料把材料页占到只剩 <paramref name="free"/> 个空格(已经在页里的格子也算占着)。</summary>
    private static void FillMaterialTab(Rig rig, int free)
    {
        int next = 0;
        while (rig.Hero.Bag.Tab(InventoryTab.Material).Count < Inventory.SlotsPerTab - free)
        {
            rig.Put(Material(3000 + next++));
        }
    }

    /// <summary>世界的指纹:金币、耐久、经验、机器档、背包每一格。拒绝类的结果要求指纹一点没变。</summary>
    private static string World(Rig rig)
    {
        var sb = new StringBuilder();
        sb.Append("gold=").Append(rig.Hero.Gold);
        sb.Append(" endurance=").Append(rig.Jobs.State.MachineEndurance);
        sb.Append(" exp=").Append(rig.Jobs.State.Exp);
        sb.Append(" grade=").Append(rig.Jobs.State.MachineGrade);
        sb.Append(" kind=").Append((int)rig.Jobs.State.Kind);
        for (int t = 0; t < Inventory.TabCount; t++)
        {
            foreach (ItemStack stack in rig.Hero.Bag.Tab((InventoryTab)t))
            {
                sb.Append(" [").Append(t).Append(':').Append(stack.Info.Code).Append('x').Append(stack.Count).Append(']');
            }
        }
        return sb.ToString();
    }

    private static readonly int[] OneDisjoint = { 1000, 5000, 1, 2, 99 };

    // ------------------------------------------------------------------ 就职

    [Fact]
    public void TakingTheJobNeedsTheCharacterLevelAndGrantsTheSkillsAndInitialisesTheMachine()
    {
        Rig rig = Make(charLevel: 30, hired: false);
        ExpertJobChange low = rig.Jobs.TakeJob(ExpertJobKind.Disjointer, 31);
        Assert.Equal(ExpertJobError.Refused, low.Error);
        Assert.False(rig.Jobs.State.HasJob);
        Assert.Equal(0, rig.Jobs.Level);
        Assert.Equal(ExpertJobKind.None, rig.Jobs.Kind);

        ExpertJobChange ok = rig.Jobs.TakeJob(ExpertJobKind.Disjointer, 30);
        Assert.True(ok.Ok);
        Assert.Equal(new[] { 901, 902 }, ok.SkillsToGrant.Select(s => s.SkillId).ToArray());
        Assert.Equal(new[] { 1, 1 }, ok.SkillsToGrant.Select(s => s.Level).ToArray());
        Assert.Equal(ExpertJobKind.Disjointer, rig.Jobs.Kind);
        Assert.Equal(0, rig.Jobs.State.Exp);
        Assert.Equal(5, rig.Jobs.State.MachineEndurance);
        Assert.Equal(0, rig.Jobs.State.MachineGrade);
        Assert.Equal(1, rig.Jobs.Level);
    }

    [Fact]
    public void ASecondJobAnUnknownKindOrNoKindIsRefusedWithoutChangingTheState()
    {
        Rig rig = Make();
        Assert.Equal(40, rig.Jobs.SetExp(40).NewExp);
        string before = World(rig);
        Assert.Equal(ExpertJobError.NotAvailable, rig.Jobs.TakeJob(ExpertJobKind.Disjointer).Error);
        Assert.Equal(ExpertJobError.NotAvailable, rig.Jobs.TakeJob(ExpertJobKind.Alchemist).Error);
        Assert.Equal(before, World(rig));

        Rig none = Make(hired: false);
        Assert.Equal(ExpertJobError.NotAvailable, none.Jobs.TakeJob(ExpertJobKind.Alchemist).Error);     // 目录里没有炼金师的脚本
        Assert.Equal(ExpertJobError.NotAvailable, none.Jobs.TakeJob(ExpertJobKind.None).Error);
        Assert.False(none.Jobs.State.HasJob);
    }

    // ------------------------------------------------------------------ 放弃

    [Fact]
    public void GivingUpChecksGoldThenQuestsThenSpendsAndClears()
    {
        Rig rig = Make(gold: 999);
        ExpertJobChange poor = rig.Jobs.GiveUp();               // 第一次放弃 1000 金币
        Assert.Equal(ExpertJobError.NotEnoughGold, poor.Error);
        Assert.True(rig.Jobs.State.HasJob);
        Assert.Equal(999, rig.Hero.Gold);

        rig.Hero.AddGold(4001);                                  // 5000
        Assert.Equal(ExpertJobError.NotAvailable, rig.Jobs.GiveUp().Error);       // [clear quests] 901、902 还没完成
        rig.Hero.Quests.MarkCleared(901);
        Assert.Equal(ExpertJobError.NotAvailable, rig.Jobs.GiveUp().Error);       // 只完成一个也不行
        Assert.Equal(5000, rig.Hero.Gold);
        Assert.True(rig.Jobs.State.HasJob);

        rig.Hero.Quests.MarkCleared(902);
        ExpertJobChange done = rig.Jobs.GiveUp();
        Assert.True(done.Ok);
        Assert.Equal(1000, done.GoldCost);
        Assert.Equal(4000, rig.Hero.Gold);
        Assert.Equal(new[] { 901, 902 }, done.SkillsToRemove.ToArray());
        Assert.Equal(new[] { 901, 902 }, done.QuestsToReset.ToArray());
        Assert.False(rig.Jobs.State.HasJob);
        Assert.Equal(-1, rig.Jobs.State.Exp);
        Assert.Equal(1, rig.Jobs.State.GiveupCount);
        Assert.Equal(0, rig.Jobs.State.MachineEndurance);
        Assert.Equal(0, rig.Jobs.Level);
    }

    [Fact]
    public void GivingUpWithoutAJobIsRefusedAndTheLastPriceRepeatsAfterwards()
    {
        Rig none = Make(gold: 99999, hired: false);
        Assert.Equal(ExpertJobError.NotAvailable, none.Jobs.GiveUp().Error);
        Assert.Equal(99999, none.Hero.Gold);

        // 费用表 1000、2000、3000:放弃次数最多涨到 2(表长减一),所以第 4 次起仍按 3000 收,放弃次数用不完。
        Rig rig = Make(gold: 100000);
        rig.Hero.Quests.MarkCleared(901);
        rig.Hero.Quests.MarkCleared(902);
        int[] expectedCosts = { 1000, 2000, 3000, 3000 };
        long gold = 100000;
        foreach (int cost in expectedCosts)
        {
            ExpertJobChange give = rig.Jobs.GiveUp();
            Assert.True(give.Ok);
            Assert.Equal(cost, give.GoldCost);
            gold -= cost;
            Assert.Equal(gold, rig.Hero.Gold);
            Assert.True(rig.Jobs.TakeJob(ExpertJobKind.Disjointer).Ok);       // 放弃后可以重新就职(经验从 −1 回到 0)
            Assert.Equal(0, rig.Jobs.State.Exp);
            Assert.Equal(5, rig.Jobs.State.MachineEndurance);
        }
        Assert.Equal(2, rig.Jobs.State.GiveupCount);
    }

    // ------------------------------------------------------------------ 经验

    [Fact]
    public void ExpAddsUpInsideALevelAndStopsAtTheGateUntilTheCharacterGrows()
    {
        Rig rig = Make(charLevel: 5);
        Assert.Equal(1, rig.Jobs.Level);
        ExpertExpResult a = rig.Jobs.GainExp(50);
        Assert.True(a.Changed);
        Assert.Equal(50, a.NewExp);
        Assert.Equal(50, rig.Jobs.State.Exp);

        ExpertExpResult b = rig.Jobs.GainExp(500);      // 550 超过第 1 级的阈值 100,角色 5 级过不去 → 夹到 100
        Assert.True(b.Changed);
        Assert.Equal(100, b.NewExp);
        Assert.False(b.LeveledUp);
        Assert.Equal(1, b.Level);
        Assert.Equal(100, rig.Jobs.State.Exp);

        ExpertExpResult c = rig.Jobs.GainExp(10);       // 停在阈值上,再加也没用
        Assert.False(c.Changed);
        Assert.Equal(100, rig.Jobs.State.Exp);
        Assert.Equal(1, rig.Jobs.Level);

        rig.Hero.SetLevel(10);                          // 角色长到 10 级:等级闸门随之打开
        Assert.Equal(2, rig.Jobs.Level);
        ExpertExpResult d = rig.Jobs.GainExp(10);
        Assert.True(d.Changed);
        Assert.Equal(110, d.NewExp);
        Assert.False(d.LeveledUp);
        Assert.Equal(2, d.Level);
    }

    [Fact]
    public void CrossingAThresholdGrantsTheSkillsAgainAtTheNewLevel()
    {
        Rig rig = Make(charLevel: 10);
        Assert.Equal(90, rig.Jobs.SetExp(90).NewExp);
        ExpertExpResult up = rig.Jobs.GainExp(10);
        Assert.True(up.Changed);
        Assert.True(up.LeveledUp);
        Assert.Equal(100, up.NewExp);
        Assert.Equal(2, up.Level);
        Assert.Equal(new[] { 901, 902 }, up.SkillGrowth.Select(s => s.SkillId).ToArray());
        Assert.Equal(new[] { 2, 2 }, up.SkillGrowth.Select(s => s.Level).ToArray());
        Assert.Equal(100, rig.Jobs.State.Exp);
        Assert.Equal(2, rig.Jobs.Level);
    }

    [Fact]
    public void NoJobZeroNegativeAndNegativeTotalsChangeNothing()
    {
        Rig none = Make(hired: false);
        Assert.False(none.Jobs.GainExp(50).Changed);
        Assert.False(none.Jobs.SetExp(50).Changed);
        Assert.Equal(0, none.Jobs.GainExp(50).Level);
        Assert.Equal(0, none.Jobs.State.Exp);

        Rig rig = Make();
        Assert.Equal(40, rig.Jobs.SetExp(40).NewExp);
        Assert.False(rig.Jobs.GainExp(0).Changed);
        Assert.False(rig.Jobs.GainExp(-5).Changed);
        ExpertExpResult negative = rig.Jobs.SetExp(-5);        // 负的新经验不是合法写入
        Assert.False(negative.Changed);
        Assert.Equal(40, negative.NewExp);
        Assert.Equal(40, rig.Jobs.State.Exp);
        Assert.False(rig.Jobs.SetExp(int.MinValue).Changed);
        Assert.Equal(40, rig.Jobs.State.Exp);
        Assert.Equal(40, rig.Jobs.SetExp(40).NewExp);         // 同一个值再写:没有改动,结果里仍是 40
        Assert.False(rig.Jobs.SetExp(40).Changed);
    }

    [Fact]
    public void AddingNothingOnABoundaryIsNotALevelUpAndReportsTheRealLevel()
    {
        // 服务端的写法:新经验落在边界(阈值 100)上、角色等级又恰好是某个上限值(10)时,同级也算"升级"。增量不大于 0 的加经验不走这条路。
        Rig rig = Make(charLevel: 10);
        Assert.Equal(100, rig.Jobs.SetExp(100).NewExp);
        Assert.Equal(2, rig.Jobs.Level);
        foreach (int nothing in new[] { 0, -1, int.MinValue })
        {
            ExpertExpResult r = rig.Jobs.GainExp(nothing);
            Assert.False(r.Changed);
            Assert.False(r.LeveledUp);
            Assert.Empty(r.SkillGrowth);
            Assert.Equal(100, r.NewExp);
            Assert.Equal(2, r.Level);               // 没有改动的结果里也是此刻真实的等级
            Assert.Equal(100, rig.Jobs.State.Exp);
        }
        ExpertExpResult negative = rig.Jobs.SetExp(-7);
        Assert.False(negative.Changed);
        Assert.Equal(2, negative.Level);
    }

    [Fact]
    public void TheLevelGateIsCheckedAgainWhenTheCharacterReachesTheLimitLevel()
    {
        // 服务端在角色升级(gain_exp_sp 0x0866a905)与改等级(SetCharacLevel 0x0867aea6)之后,都用"当前经验"再调一次 IncreaseExpertJobExp:
        // 经验停在边界(阈值 250)上、角色等级恰好是某个上限值(20)时算一次升级,把技能按新的副职业等级重新授予。
        Rig rig = Make(charLevel: 19);
        Assert.Equal(250, rig.Jobs.SetExp(250).NewExp);        // 0 → 250:1 级变 2 级(角色 19 级过不了第 3 级的闸门 20),经验停在边界上
        Assert.Equal(2, rig.Jobs.Level);

        ExpertExpResult same = rig.Jobs.RecheckLevelGate();    // 角色等级没变,闸门没动:没有新的升级
        Assert.False(same.Changed);
        Assert.False(same.LeveledUp);
        Assert.Equal(250, same.NewExp);
        Assert.Equal(2, same.Level);

        rig.Hero.SetLevel(20);                                  // 角色长到 20 级:闸门打开,副职业等级随之变成 3
        Assert.Equal(3, rig.Jobs.Level);
        ExpertExpResult up = rig.Jobs.RecheckLevelGate();
        Assert.True(up.Changed);
        Assert.True(up.LeveledUp);
        Assert.Equal(250, up.NewExp);
        Assert.Equal(3, up.Level);
        Assert.Equal(new[] { 901, 902 }, up.SkillGrowth.Select(s => s.SkillId).ToArray());
        Assert.Equal(new[] { 3, 3 }, up.SkillGrowth.Select(s => s.Level).ToArray());
        Assert.Equal(250, rig.Jobs.State.Exp);                  // 经验一点没动

        rig.Hero.SetLevel(21);                                  // 21 不是任何一个上限值:再查一次,不再算升级
        ExpertExpResult after = rig.Jobs.RecheckLevelGate();
        Assert.False(after.Changed);
        Assert.False(after.LeveledUp);
        Assert.Equal(3, after.Level);
    }

    [Fact]
    public void TheLevelGateCheckOffTheBoundaryOrWithoutAJobChangesNothing()
    {
        Rig rig = Make(charLevel: 10);
        Assert.Equal(90, rig.Jobs.SetExp(90).NewExp);          // 阈值 100 之前,不在边界上;10 是上限值也没用
        ExpertExpResult off = rig.Jobs.RecheckLevelGate();
        Assert.False(off.Changed);
        Assert.False(off.LeveledUp);
        Assert.Empty(off.SkillGrowth);
        Assert.Equal(90, rig.Jobs.State.Exp);

        Rig none = Make(charLevel: 10, hired: false);
        ExpertExpResult nothing = none.Jobs.RecheckLevelGate();
        Assert.False(nothing.Changed);
        Assert.Equal(0, nothing.Level);
        Assert.Equal(0, none.Jobs.State.Exp);

        Rig gaveUp = Make(charLevel: 10, gold: 1000);          // 放弃后经验是 −1,没有副职业:什么都不变
        gaveUp.Hero.Quests.MarkCleared(901);
        gaveUp.Hero.Quests.MarkCleared(902);
        Assert.True(gaveUp.Jobs.GiveUp().Ok);
        Assert.Equal(-1, gaveUp.Jobs.State.Exp);
        Assert.False(gaveUp.Jobs.RecheckLevelGate().Changed);
        Assert.Equal(-1, gaveUp.Jobs.State.Exp);
    }

    [Fact]
    public void SettingTheExpToZeroIsAnOrdinaryWriteNotTheNegativeCase()
    {
        Rig rig = Make();
        Assert.Equal(40, rig.Jobs.SetExp(40).NewExp);
        ExpertExpResult down = rig.Jobs.SetExp(0);            // 0 不是负数:走同一条路,经验写回 0(服务端的参数是新总值,可以比当前小)
        Assert.True(down.Changed);
        Assert.Equal(0, down.NewExp);
        Assert.Equal(0, rig.Jobs.State.Exp);
        Assert.False(rig.Jobs.SetExp(0).Changed);             // 再写一次 0:已经是 0,没有改动
    }

    [Fact]
    public void ExpGainSaturatesInsteadOfWrappingAround()
    {
        Rig rig = Make(charLevel: 99);
        Assert.Equal(100, rig.Jobs.SetExp(100).NewExp);
        ExpertExpResult huge = rig.Jobs.GainExp(int.MaxValue);       // 100 + 2147483647 装不进 32 位:先封顶,再夹到最后一级的阈值 700
        Assert.True(huge.Changed);
        Assert.Equal(700, huge.NewExp);
        Assert.Equal(4, huge.Level);
        Assert.Equal(700, rig.Jobs.State.Exp);
        Assert.False(rig.Jobs.GainExp(int.MaxValue).Changed);        // 已在封顶处,角色 99 级不是上限值:不变
    }

    // ------------------------------------------------------------------ 分解:成功

    [Fact]
    public void ADisjointPutsTheProductsInTheBagTakesTheItemAndSettlesEnduranceAndExp()
    {
        Rig rig = Make(rolls: OneDisjoint);
        rig.Put(Gear(900));
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);

        Assert.True(r.Ok);
        Assert.Equal(new[] { P(950, 16), P(951, 2) }, r.Products.ToArray());
        Assert.Equal(2, r.EnduranceLost);
        Assert.Equal(4, r.ExpGain);
        Assert.False(r.PortableUsed);
        Assert.NotNull(r.Exp);
        Assert.Equal(4, r.Exp.NewExp);
        Assert.Equal(1, r.Exp.Level);

        Assert.Empty(rig.Hero.Bag.Tab(InventoryTab.Equipment));                // 原物品没了
        Assert.Equal(16, rig.Hero.Bag.CountOf(950));
        Assert.Equal(2, rig.Hero.Bag.CountOf(951));
        Assert.Equal(new[] { 950, 951 }, rig.Hero.Bag.Tab(InventoryTab.Material).Select(s => s.Info.Code).ToArray());
        Assert.Equal(3, rig.Jobs.State.MachineEndurance);
        Assert.Equal(4, rig.Jobs.State.Exp);
        Assert.Equal(new[] { 10000, 10000, 3, 3, 100 }, rig.Rng.Ranges);
        Assert.Equal(0, rig.Rng.Remaining);

        // 同一件东西不能再分解第二次:格子已经空了。
        string after = World(rig);
        DisjointReport again = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.Equal(ExpertJobError.InvalidItem, again.Error);
        Assert.False(again.Ok);
        Assert.Empty(again.Products);
        Assert.Equal(after, World(rig));
    }

    [Fact]
    public void TheChosenSlotIsTakenAndLaterItemsMoveUp()
    {
        Rig rig = Make(rolls: OneDisjoint);
        rig.Put(Gear(900));
        rig.Put(Gear(901, grade: 10));
        rig.Put(Gear(902, grade: 12));
        Assert.True(rig.Jobs.Disjoint(InventoryTab.Equipment, 1).Ok);          // 拿走中间那件
        Assert.Equal(new[] { 900, 902 }, rig.Hero.Bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code).ToArray());
    }

    [Fact]
    public void YourOwnMachineAddsTheSelfServiceProductAndUsesTheBigWinGroup()
    {
        // 稀有度 2、价值 5000、物品等级 30:价格 5500 → 55000 ÷ 100 → ×2.0 ÷ 1000 = 110;大成功线 20% = 2000,抽值 1999 小于它 → 大成功组 2(961 权重 3000 除数 1)→ 30;
        // 自用抽值 9 小于 10 → 970 × 2。结算:耐久抽值 0 → 1、经验抽值 0 → 2、额外经验抽值 99(没有概率,不中)。
        Rig rig = Make(rolls: new[] { 1999, 3000, 9, 0, 0, 99 });
        rig.Put(Gear(901, grade: 30, rarity: 2, value: 5000));
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.True(r.Ok);
        Assert.Equal(new[] { P(950, 110), P(961, 30), P(970, 2) }, r.Products.ToArray());
        Assert.Equal(new[] { 10000, 10000, 100, 3, 3, 100 }, rig.Rng.Ranges);
        Assert.Equal(1, r.EnduranceLost);
        Assert.Equal(2, r.ExpGain);
        Assert.Equal(110, rig.Hero.Bag.CountOf(950));
        Assert.Equal(30, rig.Hero.Bag.CountOf(961));
        Assert.Equal(2, rig.Hero.Bag.CountOf(970));
    }

    [Fact]
    public void TheSameProductInTwoSlotsLandsInTheBagAsOneSum()
    {
        // 稀有度 4:主产物 950 × 11,普通组 4 抽到 950(权重 5000 以内)× 20 ÷ 2 = 10;自用抽值 50 不小于 10,不中。
        Rig rig = Make(rolls: new[] { 0, 5000, 50, 0, 0, 99 });
        rig.Put(Gear(900, rarity: 4));
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.True(r.Ok);
        Assert.Equal(new[] { P(950, 11), P(950, 10) }, r.Products.ToArray());
        Assert.Equal(21, rig.Hero.Bag.CountOf(950));
        Assert.Single(rig.Hero.Bag.Tab(InventoryTab.Material));
    }

    [Fact]
    public void WornGearAddsBigWinChanceResultVariationAndExtraExp()
    {
        Rig rig = Make(rolls: new[] { 1500, 5000, 0, 1, 50 });
        ItemInfo ring = Gear(930, part: EquipmentParts.Ring);
        ring.UsableJobs.Add("[swordman]");
        rig.Put(ring, 1, TagsOf(
            L("[prof disjoint big win rate]", 10.0f),
            L("[prof disjoint result variation]", 50.0f),
            L("[prof additional gain exp]", 100.0f, 3)));
        Assert.Equal(EquipResult.Ok, rig.Hero.Equip(0));
        Assert.Same(ring, rig.Hero.Worn.Get(EquipmentParts.Ring));
        rig.Put(Gear(900));

        // 大成功线 1000 + 穿戴 10.0 × 100 = 2000,抽值 1500 走大成功组(960 × 20 ÷ 2 = 10);个数各加 50%:16 → 24、10 → 15;
        // 经验抽值 1 → 3,额外经验抽值 50 小于概率 100 → 再加 3,共 6。
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.True(r.Ok);
        Assert.Equal(new[] { P(950, 24), P(960, 15) }, r.Products.ToArray());
        Assert.Equal(1, r.EnduranceLost);
        Assert.Equal(6, r.ExpGain);
        Assert.Equal(6, rig.Jobs.State.Exp);
        Assert.Equal(4, rig.Jobs.State.MachineEndurance);
    }

    [Fact]
    public void WornGearTheJobLevelCannotUseAddsNothingUntilTheLevelIsReached()
    {
        Rig rig = Make(rolls: new[] { 1500, 5000, 0, 1, 50, 1500, 5000, 0, 1, 50 });
        ItemInfo ring = Gear(931, part: EquipmentParts.Ring);
        ring.UsableJobs.Add("[swordman]");
        rig.Put(ring, 1, TagsOf(L("[expertjob only]", "disjointer", 2), L("[prof disjoint big win rate]", 10.0f)));
        Assert.Equal(EquipResult.Ok, rig.Hero.Equip(0));

        // 副职业 1 级:要求 2 级的加成不算,大成功线仍是 1000,抽值 1500 走普通组(951 × 2)。
        rig.Put(Gear(900));
        DisjointReport low = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.Equal(new[] { P(950, 16), P(951, 2) }, low.Products.ToArray());

        // 升到 2 级:加成算进来,同样的抽值走大成功组(960 × 10)。
        Assert.Equal(120, rig.Jobs.SetExp(120).NewExp);
        Assert.Equal(2, rig.Jobs.Level);
        rig.Put(Gear(901));
        DisjointReport high = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.Equal(new[] { P(950, 16), P(960, 10) }, high.Products.ToArray());
        Assert.Equal(123, rig.Jobs.State.Exp);
    }

    [Fact]
    public void TheResultVariationAndTheExtraExpOfWornGearWaitForTheJobLevelToo()
    {
        Rig rig = Make(rolls: new[] { 1500, 5000, 0, 1, 50, 1500, 5000, 0, 1, 50 });
        ItemInfo ring = Gear(932, part: EquipmentParts.Ring);
        ring.UsableJobs.Add("[swordman]");
        rig.Put(ring, 1, TagsOf(
            L("[expertjob only]", "disjointer", 2),
            L("[prof disjoint result variation]", 50.0f),
            L("[prof additional gain exp]", 100.0f, 3)));
        Assert.Equal(EquipResult.Ok, rig.Hero.Equip(0));

        // 副职业 1 级:要求 2 级的变化与额外经验都不算(大成功线仍是 1000,抽值 1500 走普通组 951 × 2;经验抽值 1 → 3,额外经验不触发)。
        rig.Put(Gear(900));
        DisjointReport low = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.Equal(new[] { P(950, 16), P(951, 2) }, low.Products.ToArray());
        Assert.Equal(3, low.ExpGain);

        // 升到 2 级:个数各加 50%(16 → 24、2 → 3);额外经验抽值 50 小于概率 100 → 3 + 3 = 6。
        Assert.Equal(120, rig.Jobs.SetExp(120).NewExp);
        Assert.Equal(2, rig.Jobs.Level);
        rig.Put(Gear(901));
        DisjointReport high = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.Equal(new[] { P(950, 24), P(951, 3) }, high.Products.ToArray());
        Assert.Equal(6, high.ExpGain);
        Assert.Equal(126, rig.Jobs.State.Exp);
    }

    [Fact]
    public void TheMachineGradeSelectsTheResultRowAndAddsItsBigWinBonus()
    {
        Rig rig = Make(gold: 30000, rolls: new[] { 3499, 5000, 0, 0, 99 });
        Assert.Equal(120, rig.Jobs.SetExp(120).NewExp);
        MachineUpgradePlan up = rig.Jobs.UpgradeMachine();
        Assert.True(up.Ok);
        Assert.Equal(1, rig.Jobs.State.MachineGrade);

        // 机器档 1 的行:950 × 3.0 → 33;大成功线 30% = 3000 + 机器档加成(键 2 → 5)× 100 = 3500,抽值 3499 小于它 → 960 × 10。
        rig.Put(Gear(900));
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.True(r.Ok);
        Assert.Equal(new[] { P(950, 33), P(960, 10) }, r.Products.ToArray());
        Assert.Equal(299, rig.Jobs.State.MachineEndurance);       // 升档后耐久是 300,这次扣 1
        Assert.Equal(122, rig.Jobs.State.Exp);
    }

    [Fact]
    public void ThePriceRatesScaleTheMainProduct()
    {
        // 倍率 500‰:1100 × 500 ÷ 1000 = 550 → 550 × 1000 ÷ 100 = 5500 → × 1.5 ÷ 1000 = 8.25 → 8。
        Rig rig = Make(rolls: new[] { 1000, 5000, 0, 0, 99 }, rates: new ItemPriceRates(500, 500, 500));
        rig.Put(Gear(900));
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.Equal(new[] { P(950, 8), P(951, 2) }, r.Products.ToArray());
    }

    [Fact]
    public void WithoutAnInjectedRandomSourceTheSystemOneIsUsed()
    {
        HeroProgress hero = ExpTests.Progress(level: 30);
        var items = new Dictionary<int, ItemInfo>();
        for (int code = 948; code <= 970; code++)
        {
            items[code] = Material(code);
        }
        var jobs = new ExpertJobSession(hero, Catalog(), code => items.TryGetValue(code, out ItemInfo info) ? info : null);
        Assert.Same(DisjointPolicy.Default, jobs.Policy);
        Assert.True(jobs.TakeJob(ExpertJobKind.Disjointer).Ok);
        hero.AddItem(Gear(900));
        DisjointReport r = jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.True(r.Ok);
        Assert.Equal(P(950, 16), r.Products[0]);            // 主产物个数不靠随机
        Assert.InRange(r.EnduranceLost, 1, 3);
        Assert.InRange(r.ExpGain, 2, 4);
    }

    // ------------------------------------------------------------------ 分解:拒绝(什么都不动)

    [Fact]
    public void OnlyAnActiveDisjointerCanUseTheMachine()
    {
        Rig none = Make(hired: false);
        none.Put(Gear(900));
        string before = World(none);
        Assert.Equal(ExpertJobError.NotPermitted, none.Jobs.Disjoint(InventoryTab.Equipment, 0).Error);
        Assert.Equal(ExpertJobError.NotPermitted, none.Jobs.Disjoint(InventoryTab.Equipment, 7).Error);      // 机主检查先于格子检查
        Assert.Equal(before, World(none));
        Assert.Empty(none.Rng.Ranges);

        // 存档里是炼金师:这一版不执行炼金,分解也不行。
        Rig alchemist = Make(hired: false);
        alchemist.Put(Gear(900));
        var save = new HeroSave { ExpertJob = new SavedExpertJob(2, 10, 0, 5, 0) };
        Assert.True(alchemist.Jobs.Restore(save));
        Assert.Equal(ExpertJobKind.Alchemist, alchemist.Jobs.Kind);
        string alchemistBefore = World(alchemist);
        Assert.Equal(ExpertJobError.NotPermitted, alchemist.Jobs.Disjoint(InventoryTab.Equipment, 0).Error);
        Assert.Equal(alchemistBefore, World(alchemist));
    }

    [Fact]
    public void ABrokenMachineIsRefusedBeforeTheSlotIsLookedAtAndRepairingFixesIt()
    {
        Rig rig = Make(gold: 20000, rolls: OneDisjoint);
        rig.Put(Gear(900));
        rig.Jobs.State.SetMachine(0, 0);
        string before = World(rig);
        Assert.Equal(ExpertJobError.MachineBroken, rig.Jobs.Disjoint(InventoryTab.Equipment, 0).Error);
        Assert.Equal(ExpertJobError.MachineBroken, rig.Jobs.Disjoint(InventoryTab.Equipment, 9).Error);     // 耐久检查先于格子检查
        Assert.Equal(before, World(rig));
        Assert.Empty(rig.Rng.Ranges);

        Assert.True(rig.Jobs.RepairMachine().Ok);
        Assert.True(rig.Jobs.Disjoint(InventoryTab.Equipment, 0).Ok);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(100)]
    public void AnEmptyOrMissingSlotIsInvalid(int index)
    {
        Rig rig = Make(rolls: OneDisjoint);
        rig.Put(Gear(900));
        string before = World(rig);
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, index);
        Assert.Equal(ExpertJobError.InvalidItem, r.Error);
        Assert.Equal(before, World(rig));
        Assert.Empty(rig.Rng.Ranges);
    }

    [Fact]
    public void ALockedItemIsRefusedBeforeTheItemRulesAreLookedAt()
    {
        Rig rig = Make(rolls: OneDisjoint);
        rig.Put(Gear(900), 1, TagsOf(L("[impossible contents]", "disjoint")));         // 同时是禁分解的
        rig.Put(Gear(901));
        rig.Jobs.ItemLock = info => info.Code == 900;
        string before = World(rig);
        Assert.Equal(ExpertJobError.ItemLocked, rig.Jobs.Disjoint(InventoryTab.Equipment, 0).Error);
        Assert.Equal(before, World(rig));
        Assert.Empty(rig.Rng.Ranges);

        Assert.True(rig.Jobs.Disjoint(InventoryTab.Equipment, 1).Ok);                  // 没锁的那件照常分解
        rig.Jobs.ItemLock = null;
        Assert.Equal(ExpertJobError.NotPermitted, rig.Jobs.Disjoint(InventoryTab.Equipment, 0).Error);
    }

    private static ItemInfo Refused(string kind, out ExpertJobItemTags tags)
    {
        tags = null;
        switch (kind)
        {
            case "impossible":
                tags = TagsOf(L("[impossible contents]", "trade", "disjoint"));
                return Gear(920);
            case "tradedelete":
                return Gear(920, attach: "[trade delete]");
            case "creature":
                return Gear(920, part: EquipmentParts.Creature);
            case "artifact-red":
                return Gear(920, part: EquipmentParts.ArtifactRed);
            case "artifact-blue":
                return Gear(920, part: EquipmentParts.ArtifactBlue);
            case "artifact-green":
                return Gear(920, part: EquipmentParts.ArtifactGreen);
            case "avatar-hat":
                return Gear(920, part: EquipmentParts.HatAvatar);
            case "avatar-aurora":
                return Gear(920, part: EquipmentParts.AuroraAvatar);
            default:
                throw new ArgumentException(kind);
        }
    }

    [Theory]
    [InlineData("impossible", ExpertJobError.NotPermitted)]
    [InlineData("tradedelete", ExpertJobError.InvalidItem)]
    [InlineData("creature", ExpertJobError.InvalidItem)]
    [InlineData("artifact-red", ExpertJobError.InvalidItem)]
    [InlineData("artifact-blue", ExpertJobError.InvalidItem)]
    [InlineData("artifact-green", ExpertJobError.InvalidItem)]
    [InlineData("avatar-hat", ExpertJobError.InvalidItem)]
    [InlineData("avatar-aurora", ExpertJobError.InvalidItem)]
    public void TheDisjointRulesRefuseTheRightItemsAndNothingMoves(string kind, ExpertJobError expected)
    {
        Rig rig = Make(rolls: OneDisjoint);
        ItemInfo item = Refused(kind, out ExpertJobItemTags tags);
        rig.Put(item, 1, tags);
        string before = World(rig);
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.Equal(expected, r.Error);
        Assert.Empty(r.Products);
        Assert.Equal(before, World(rig));
        Assert.Empty(rig.Rng.Ranges);
    }

    [Theory]
    [InlineData("artifact-red", true)]
    [InlineData("artifact-green", true)]
    [InlineData("avatar-hat", true)]
    [InlineData("avatar-aurora", true)]
    [InlineData("creature", false)]
    [InlineData("impossible", false)]
    [InlineData("tradedelete", false)]
    public void TheServerParityPolicyLetsAvatarsAndArtifactsThroughButNotPetsOrTheOtherRefusals(string kind, bool accepted)
    {
        Rig rig = Make(rolls: OneDisjoint, policy: DisjointPolicy.ServerParity);
        ItemInfo item = Refused(kind, out ExpertJobItemTags tags);
        rig.Put(item, 1, tags);
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.Equal(accepted, r.Ok);
        Assert.Equal(accepted, rig.Hero.Bag.Tab(InventoryTab.Equipment).Count == 0);
    }

    [Fact]
    public void ItemsThatAreNotEquipmentAreRefusedWhicheverPageTheyAreOn()
    {
        Rig rig = Make(rolls: OneDisjoint);
        rig.Put(Material(940), 3);
        rig.Put(new ItemInfo(941, "stackable/test/q941.stk", ItemKind.Stackable) { StackableType = StackableTypes.Quest }, 1);
        rig.Put(new ItemInfo(942, "stackable/test/w942.stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste }, 5);
        string before = World(rig);
        Assert.Equal(ExpertJobError.InvalidItem, rig.Jobs.Disjoint(InventoryTab.Material, 0).Error);
        Assert.Equal(ExpertJobError.InvalidItem, rig.Jobs.Disjoint(InventoryTab.Quest, 0).Error);
        Assert.Equal(ExpertJobError.InvalidItem, rig.Jobs.Disjoint(InventoryTab.Consumable, 0).Error);
        Assert.Equal(before, World(rig));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[free]")]
    [InlineData("[trade]")]
    [InlineData("[sealing]")]
    [InlineData("[sealing trade]")]
    [InlineData("[account]")]
    [InlineData("[something else]")]
    public void EveryAttachTypeExceptTradeDeleteIsAccepted(string attach)
    {
        Rig rig = Make(rolls: new[] { 1000, 5000, 0, 0, 99 });
        rig.Put(Gear(900, attach: attach));
        Assert.True(rig.Jobs.Disjoint(InventoryTab.Equipment, 0).Ok);
        Assert.Empty(rig.Hero.Bag.Tab(InventoryTab.Equipment));
    }

    [Fact]
    public void ARarityWithoutAResultRowIsInvalidAndDrawsNothing()
    {
        Rig rig = Make(rolls: OneDisjoint);
        rig.Put(Gear(900, rarity: 5));
        string before = World(rig);
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.Equal(ExpertJobError.InvalidItem, r.Error);
        Assert.Equal(before, World(rig));
        Assert.Empty(rig.Rng.Ranges);
    }

    [Fact]
    public void AProductMissingFromTheItemDataCancelsTheWholeDisjoint()
    {
        Rig rig = Make(rolls: OneDisjoint);
        rig.Put(Gear(900));
        rig.Items.Remove(951);                       // 附加产物 951 在数据里找不到
        string before = World(rig);
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.Equal(ExpertJobError.UnknownProduct, r.Error);
        Assert.Empty(r.Products);
        Assert.Equal(before, World(rig));            // 主产物 950 也没有放进去,原物品还在,耐久经验不动
        Assert.Single(rig.Hero.Bag.Tab(InventoryTab.Equipment));
        Assert.Equal(0, rig.Hero.Bag.CountOf(950));
    }

    // ------------------------------------------------------------------ 分解:背包放不下

    [Fact]
    public void AFullBagRefusesAndNothingMoves()
    {
        Rig rig = Make(rolls: new[] { 1000, 5000, 0, 0, 99 });
        FillMaterialTab(rig, free: 0);
        rig.Put(Gear(900));
        string before = World(rig);
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.Equal(ExpertJobError.NoSpace, r.Error);
        Assert.Empty(r.Products);
        Assert.Equal(before, World(rig));
    }

    [Fact]
    public void ARoomForOnlySomeProductsRefusesAllOfThem()
    {
        // 材料页留一个空格并且已有 950:950 × 16 叠得进去,951 × 2 要新格子 → 放不下,950 也不放。
        Rig rig = Make(rolls: new[] { 1000, 5000, 0, 0, 99 });
        FillMaterialTab(rig, free: 1);
        rig.Put(rig.Items[950], 1);
        Assert.Equal(Inventory.SlotsPerTab, rig.Hero.Bag.Tab(InventoryTab.Material).Count);
        rig.Put(Gear(900));
        string before = World(rig);
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.Equal(ExpertJobError.NoSpace, r.Error);
        Assert.Equal(before, World(rig));
        Assert.Equal(1, rig.Hero.Bag.CountOf(950));
    }

    [Fact]
    public void AStackWithoutRoomForTheWholeCountIsNotFilledPartially()
    {
        // 950 已有 995(叠加上限 1000,只剩 5 个位置),又没有空格:主产物 11 个放不下,一个也不放。
        Rig rig = Make(rolls: new[] { 0, 5000, 50, 0, 0, 99 });
        FillMaterialTab(rig, free: 1);
        rig.Put(rig.Items[950], 995);
        rig.Put(Gear(900, rarity: 4));
        string before = World(rig);
        Assert.Equal(ExpertJobError.NoSpace, rig.Jobs.Disjoint(InventoryTab.Equipment, 0).Error);
        Assert.Equal(before, World(rig));
        Assert.Equal(995, rig.Hero.Bag.CountOf(950));
    }

    [Fact]
    public void TwoProductsOfTheSameKindShareTheRoomLeftInOneStack()
    {
        // 950 已有 979,正好留下 21 个位置 = 主产物 11 + 附加产物 10;材料页没有空格。
        Rig rig = Make(rolls: new[] { 0, 5000, 50, 0, 0, 99 });
        FillMaterialTab(rig, free: 1);
        rig.Put(rig.Items[950], 979);
        rig.Put(Gear(900, rarity: 4));
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.True(r.Ok);
        Assert.Equal(1000, rig.Hero.Bag.CountOf(950));
        Assert.Empty(rig.Hero.Bag.Tab(InventoryTab.Equipment));
    }

    [Fact]
    public void ProductsThatOnlyPartlyFitInTheLastStackCancelTheWholeDisjoint()
    {
        // 材料页留两格:主产物 950 占一格;附加产物 951 每格只能放 1 个,要两格却只剩一格 —— 只放得下一半也算放不下。
        Rig rig = Make(rolls: OneDisjoint);
        rig.Items[951] = Material(951, limit: 1);
        FillMaterialTab(rig, free: 2);
        rig.Put(Gear(900));
        string before = World(rig);
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.Equal(ExpertJobError.NoSpace, r.Error);
        Assert.Empty(r.Products);
        Assert.Equal(before, World(rig));
        Assert.Equal(0, rig.Hero.Bag.CountOf(950));
        Assert.Equal(0, rig.Hero.Bag.CountOf(951));
    }

    [Fact]
    public void TheSlotOfTheDisjointedItemIsFreeForAProductOfTheSameKind()
    {
        // 装备页放满(56 格),主产物 950 在这里是一件装备(价值 1 的原物品只出 1 个):原物品拿走空出的那一格正好放下它。
        Rig rig = Make(rolls: OneDisjoint);
        rig.Items[950] = Gear(950);
        rig.Put(Gear(900, value: 1));
        for (int i = 1; i < Inventory.SlotsPerTab; i++)
        {
            rig.Put(Gear(3000 + i));
        }
        Assert.Equal(Inventory.SlotsPerTab, rig.Hero.Bag.Tab(InventoryTab.Equipment).Count);
        DisjointReport r = rig.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.True(r.Ok);
        Assert.Equal(new[] { P(950, 1), P(951, 2) }, r.Products.ToArray());
        Assert.Equal(0, rig.Hero.Bag.CountOf(900));
        Assert.Equal(1, rig.Hero.Bag.CountOf(950));
        Assert.Equal(Inventory.SlotsPerTab, rig.Hero.Bag.Tab(InventoryTab.Equipment).Count);
    }

    // ------------------------------------------------------------------ 便携分解器

    private static ExpertJobItemTags PortableTag(int index) => TagsOf(L("[portable disjoint]", index));

    private static Rig PortableRig(int[] rolls = null, bool hired = false, int portableCount = 2)
    {
        Rig rig = Make(hired: hired, rolls: rolls);
        rig.Put(Portable(980), portableCount, PortableTag(0));     // 最高分解 30 级
        return rig;
    }

    [Fact]
    public void ThePortableRouteUsesTheLegacyTableConsumesOnePortableAndNeedsNoJob()
    {
        // 稀有度 1:主产物 16(系数 1.5);附加产物组 [951, 952],抽值 1500 → 下标 1 → 952;大成功线 20% = 2000,抽值 1999 小于它 → 除数 10 → 20 ÷ 10 = 2。
        Rig rig = PortableRig(new[] { 1500, 1999 });
        rig.Put(Gear(900));
        DisjointReport r = rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0);
        Assert.True(r.Ok);
        Assert.Equal(new[] { P(950, 16), P(952, 2) }, r.Products.ToArray());
        Assert.True(r.PortableUsed);
        Assert.Equal(0, r.EnduranceLost);
        Assert.Equal(0, r.ExpGain);
        Assert.Null(r.Exp);
        Assert.Equal(new[] { 2000, 10000 }, rig.Rng.Ranges);

        Assert.Equal(1, rig.Hero.Bag.CountOf(980));                                    // 分解器扣一个
        Assert.Empty(rig.Hero.Bag.Tab(InventoryTab.Equipment));                        // 原物品没了
        Assert.Equal(new[] { 980, 950, 952 }, rig.Hero.Bag.Tab(InventoryTab.Material).Select(s => s.Info.Code).ToArray());
        Assert.Equal(16, rig.Hero.Bag.CountOf(950));
        Assert.Equal(2, rig.Hero.Bag.CountOf(952));
        Assert.False(rig.Jobs.State.HasJob);                                            // 没有副职业,状态一点没动
        Assert.Equal(0, rig.Jobs.State.Exp);
    }

    [Fact]
    public void ThePortableRouteDoesNotTouchAMachineOwnersEnduranceOrExp()
    {
        Rig rig = PortableRig(new[] { 0, 0 }, hired: true);
        rig.Put(Gear(900));
        DisjointReport r = rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0);
        Assert.True(r.Ok);
        Assert.Equal(5, rig.Jobs.State.MachineEndurance);
        Assert.Equal(0, rig.Jobs.State.Exp);
    }

    [Theory]
    [InlineData("avatar-hat", true)]
    [InlineData("avatar-aurora", true)]
    [InlineData("artifact-red", true)]
    [InlineData("artifact-green", true)]
    [InlineData("creature", false)]
    [InlineData("impossible", false)]
    [InlineData("tradedelete", false)]
    public void TheServerParityPolicyAppliesToThePortableRouteToo(string kind, bool accepted)
    {
        Rig rig = Make(hired: false, rolls: new[] { 0, 0 }, policy: DisjointPolicy.ServerParity);
        rig.Put(Portable(980), 2, PortableTag(0));
        ItemInfo item = Refused(kind, out ExpertJobItemTags tags);
        rig.Put(item, 1, tags);
        DisjointReport r = rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0);
        Assert.Equal(accepted, r.Ok);
        Assert.Equal(accepted, rig.Hero.Bag.Tab(InventoryTab.Equipment).Count == 0);
        Assert.Equal(accepted ? 1 : 2, rig.Hero.Bag.CountOf(980));
    }

    [Fact]
    public void ThePortableRouteDefaultsToTheStrictPolicyAndRefusesAvatarsAndArtifacts()
    {
        Rig rig = Make(hired: false, rolls: new[] { 0, 0 });
        rig.Put(Portable(980), 2, PortableTag(0));
        rig.Put(Gear(920, part: EquipmentParts.HatAvatar));
        string before = World(rig);
        Assert.Equal(ExpertJobError.InvalidItem, rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0).Error);
        Assert.Equal(before, World(rig));
    }

    [Fact]
    public void ThePortableRouteUsesTheCatalogPriceRates()
    {
        // 收购价倍率 800‰:价值 1000 → 1100 → 880 → 8800 → 乘 1.5 除 1000 = 13.2 → 13 个(中性倍率是 16 个)。
        Rig rig = Make(hired: false, rolls: new[] { 1500, 1999 }, rates: new ItemPriceRates(800, 900, 700));
        rig.Put(Portable(980), 1, PortableTag(0));
        rig.Put(Gear(900));
        DisjointReport r = rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0);
        Assert.True(r.Ok);
        Assert.Equal(P(950, 13), r.Products[0]);
    }

    [Fact]
    public void ThePortableRouteStopsWhenTheLegacyTableCannotGiveAResult()
    {
        // 旧版分解表里没有主产物与基数:分解器与物品都不动,不返回成功。
        var catalog = new ExpertJobCatalog(Etc(), new[] { Disjointer() }, new LegacyDisjointScript(), ItemPriceRates.Neutral);
        Rig empty = Make(hired: false, rolls: new[] { 0, 0 }, catalog: catalog);
        empty.Put(Portable(980), 2, PortableTag(0));
        empty.Put(Gear(900));
        string before = World(empty);
        DisjointReport none = empty.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0);
        Assert.Equal(ExpertJobError.NotPermitted, none.Error);
        Assert.Empty(none.Products);
        Assert.False(none.PortableUsed);
        Assert.Equal(before, World(empty));

        // 稀有度不在旧版表的 0 到 5 里(这里 6):0x11,同样什么都不动。
        Rig odd = Make(hired: false, rolls: new[] { 0, 0 });
        odd.Put(Portable(980), 2, PortableTag(0));
        odd.Put(Gear(900, rarity: 6));
        string beforeOdd = World(odd);
        DisjointReport bad = odd.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0);
        Assert.Equal(ExpertJobError.InvalidItem, bad.Error);
        Assert.False(bad.PortableUsed);
        Assert.Equal(beforeOdd, World(odd));
        Assert.Empty(odd.Rng.Ranges);
    }

    [Fact]
    public void APortableDisjointOfARarityWithoutAdditionalItemsDrawsNothing()
    {
        Rig rig = PortableRig(portableCount: 1);
        rig.Put(Gear(900, rarity: 0));
        DisjointReport r = rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0);
        Assert.True(r.Ok);
        Assert.Equal(new[] { P(950, 11) }, r.Products.ToArray());
        Assert.Empty(rig.Rng.Ranges);
        Assert.Equal(0, rig.Hero.Bag.CountOf(980));                                    // 最后一个分解器用掉了
    }

    [Theory]
    [InlineData(30, 20, ExpertJobError.Ok)]           // 要求等级不超过 30
    [InlineData(31, 20, ExpertJobError.LevelTooHigh)]
    [InlineData(1, 99, ExpertJobError.Ok)]            // 有要求等级时看它,不看物品等级
    [InlineData(0, 30, ExpertJobError.Ok)]            // 要求等级为 0 才看物品等级
    [InlineData(0, 31, ExpertJobError.LevelTooHigh)]
    public void ThePortableDisjointerHasAMaximumItemLevel(int minimumLevel, int grade, ExpertJobError expected)
    {
        Rig rig = PortableRig(new[] { 0, 0 });
        rig.Put(Gear(900, level: minimumLevel, grade: grade));
        string before = World(rig);
        DisjointReport r = rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0);
        Assert.Equal(expected, r.Error);
        if (expected != ExpertJobError.Ok)
        {
            Assert.Equal(before, World(rig));
        }
    }

    [Fact]
    public void ASecondPortableIndexAllowsHigherLevels()
    {
        Rig rig = Make(hired: false, rolls: new[] { 0, 0 });
        rig.Put(Portable(981), 1, PortableTag(1));          // 最高分解 60 级
        rig.Put(Gear(900, level: 60));
        Assert.True(rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0).Ok);
    }

    [Fact]
    public void ThePortableRouteChecksItsPiecesInTheServerOrder()
    {
        Rig rig = PortableRig(new[] { 0, 0 });
        rig.Put(Gear(900, part: EquipmentParts.Creature), 1);                       // 装备页 0:宠物
        rig.Put(Gear(901), 1, TagsOf(L("[impossible contents]", "disjoint")));      // 装备页 1:禁分解
        rig.Put(Gear(902, attach: "[trade delete]"));                               // 装备页 2:交易即删除
        rig.Put(Gear(903));                                                          // 装备页 3:普通
        string before = World(rig);

        // 物品锁最先;然后物品类型(格子空也是 0x11);然后分解器;最后物品规则。
        rig.Jobs.ItemLock = info => info.Code == 900;
        Assert.Equal(ExpertJobError.ItemLocked, rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0).Error);
        rig.Jobs.ItemLock = null;
        Assert.Equal(ExpertJobError.InvalidItem, rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0).Error);
        Assert.Equal(ExpertJobError.InvalidItem, rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 9).Error);
        Assert.Equal(ExpertJobError.NotPermitted, rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 1).Error);
        Assert.Equal(ExpertJobError.InvalidItem, rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 2).Error);
        Assert.Equal(ExpertJobError.NotEnoughGold, rig.Jobs.DisjointPortable(InventoryTab.Material, 5, InventoryTab.Equipment, 3).Error);     // 分解器格是空的
        Assert.Equal(ExpertJobError.NotEnoughGold, rig.Jobs.DisjointPortable(InventoryTab.Equipment, 3, InventoryTab.Equipment, 3).Error);    // 分解器格里是装备
        Assert.Equal(before, World(rig));
        Assert.Empty(rig.Rng.Ranges);
    }

    [Fact]
    public void ADisjointerWithoutAnIndexOrWithAnUnregisteredOneIsRefused()
    {
        Rig rig = Make(hired: false, rolls: new[] { 0, 0 });
        rig.Put(Portable(982), 1);                           // 没有 [portable disjoint]
        rig.Put(Portable(983), 1, PortableTag(5));           // 序号 5 没有登记
        rig.Put(Gear(900));
        string before = World(rig);
        Assert.Equal(ExpertJobError.LimitReached, rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0).Error);
        Assert.Equal(ExpertJobError.LimitReached, rig.Jobs.DisjointPortable(InventoryTab.Material, 1, InventoryTab.Equipment, 0).Error);
        Assert.Equal(before, World(rig));
    }

    [Fact]
    public void ThePortableRouteChecksTheDisjointBanBeforeTheTradeDeleteRule()
    {
        // 物品规则的次序(CheckItemRules):禁分解(0x13)先于交易即删除(0x11);便携路线没有机器,所以未鉴定的另有 0xae。
        Rig rig = PortableRig(new[] { 0, 0 });
        rig.Put(Gear(900, attach: "[trade delete]"), 1, TagsOf(L("[impossible contents]", "disjoint")));
        Assert.Equal(ExpertJobError.NotPermitted, rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0).Error);
    }

    [Fact]
    public void WhenTheProductsDoNotFitThePortableDisjointerIsNotConsumed()
    {
        Rig rig = PortableRig(new[] { 0, 0 }, portableCount: 2);
        FillMaterialTab(rig, free: 0);       // 材料页满了(分解器 980 在第一格,后面 55 格是别的材料)
        Assert.Equal(Inventory.SlotsPerTab, rig.Hero.Bag.Tab(InventoryTab.Material).Count);
        rig.Put(Gear(900));
        string before = World(rig);
        DisjointReport r = rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0);
        Assert.Equal(ExpertJobError.NoSpace, r.Error);
        Assert.Equal(before, World(rig));
        Assert.Equal(2, rig.Hero.Bag.CountOf(980));
    }

    [Fact]
    public void ConsumingTheLastPortableDisjointerMakesRoomForTheProduct()
    {
        // 材料页 56 格全占着,其中一格是最后一个分解器:用掉它腾出一格,正好放下主产物。
        Rig rig = PortableRig(portableCount: 1);
        FillMaterialTab(rig, free: 0);
        Assert.Equal(Inventory.SlotsPerTab, rig.Hero.Bag.Tab(InventoryTab.Material).Count);
        rig.Put(Gear(900, rarity: 0));
        DisjointReport r = rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, 0);
        Assert.True(r.Ok);
        Assert.Equal(11, rig.Hero.Bag.CountOf(950));
        Assert.Equal(0, rig.Hero.Bag.CountOf(980));
        Assert.Equal(Inventory.SlotsPerTab, rig.Hero.Bag.Tab(InventoryTab.Material).Count);
    }

    [Theory]
    [InlineData(-1)]               // 负序号
    [InlineData(1)]                // 恰好等于已有格数:紧挨着最后一件的空格
    [InlineData(2)]
    [InlineData(Inventory.SlotsPerTab)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void AnIndexBelowZeroOrPastTheLastStackIsAnEmptySlotNotACrash(int index)
    {
        // 装备页只有一件(序号 0),材料页第 0 格是便携分解器。
        Rig rig = PortableRig(new[] { 0, 0 }, hired: true);
        rig.Put(Gear(900));
        string before = World(rig);

        Assert.Equal(ExpertJobError.InvalidItem, rig.Jobs.Disjoint(InventoryTab.Equipment, index).Error);
        Assert.Equal(ExpertJobError.InvalidItem, rig.Jobs.DisjointPortable(InventoryTab.Material, 0, InventoryTab.Equipment, index).Error);
        Assert.Equal(ExpertJobError.NotEnoughGold, rig.Jobs.DisjointPortable(InventoryTab.Material, index, InventoryTab.Equipment, 0).Error);
        Assert.Equal(before, World(rig));
        Assert.Empty(rig.Rng.Ranges);
    }

    // ------------------------------------------------------------------ 预览

    [Fact]
    public void ThePreviewListsTheMainProductThenTheAdditionalAndBigWinGroups()
    {
        Rig rig = Make();
        Assert.Equal(new[] { 970, 950, 953, 961, 962 }, rig.Jobs.Predict(Gear(900, rarity: 2)).ToArray());
        Assert.Equal(new[] { 950, 951, 952, 960 }, rig.Jobs.Predict(Gear(900, rarity: 1)).ToArray());
        Assert.Equal(new[] { 970 }, rig.Jobs.Predict(Gear(900, rarity: 5)).ToArray());          // 没有这一行:只有自用额外产物
        Assert.Empty(rig.Jobs.Predict(null));
    }

    [Fact]
    public void ThePreviewFollowsTheMachineGradeAndNeedsTheJob()
    {
        Rig rig = Make();
        rig.Jobs.State.SetMachine(2, 5);                                                       // 机器档 2 没有稀有度 1 的行
        Assert.Empty(rig.Jobs.Predict(Gear(900, rarity: 1)));
        Assert.Empty(Make(hired: false).Jobs.Predict(Gear(900, rarity: 2)));
    }

    [Fact]
    public void ThePreviewLeavesOutTheEmptyPlaceholderOfAMissingSelfServiceItem()
    {
        ScriptLine[] lines = DisjointerLines().Where(l => l.Tag != "[disjoint self service]").ToArray();
        var catalog = new ExpertJobCatalog(Etc(), new[] { ExpertJobScript.Parse(ExpertJobKind.Disjointer, lines) }, Legacy(), ItemPriceRates.Neutral);
        Rig rig = Make(catalog: catalog);
        Assert.Equal(new[] { 950, 953, 961, 962 }, rig.Jobs.Predict(Gear(900, rarity: 2)).ToArray());
    }

    [Fact]
    public void ThePortablePreviewIsTheMainProductPlusTheRaritysAdditionalGroup()
    {
        Rig rig = Make(hired: false);
        Assert.Equal(new[] { 950, 951, 952 }, rig.Jobs.PredictPortable(Gear(900, rarity: 1)).ToArray());
        Assert.Equal(new[] { 950 }, rig.Jobs.PredictPortable(Gear(900, rarity: 0)).ToArray());
        Assert.Equal(new[] { 950, 960 }, rig.Jobs.PredictPortable(Gear(900, rarity: 4)).ToArray());
        Assert.Empty(rig.Jobs.PredictPortable(null));
    }

    // ------------------------------------------------------------------ 整修与升档

    [Fact]
    public void RepairingSpendsTheMissingEnduranceShareAndRestoresTheCap()
    {
        Rig rig = Make(gold: 20000);
        Assert.Equal(9834, rig.Jobs.RepairQuote());            // 10000 − 10000 × 5 ÷ 300(整除 166)
        MachineRepairPlan plan = rig.Jobs.RepairMachine();
        Assert.True(plan.Ok);
        Assert.Equal(9834, plan.Cost);
        Assert.Equal(300, plan.NewEndurance);
        Assert.Equal(300, rig.Jobs.State.MachineEndurance);
        Assert.Equal(10166, rig.Hero.Gold);
        Assert.Equal(0, rig.Jobs.RepairQuote());               // 修满了
    }

    [Fact]
    public void ShortOfTheFullPriceTheRepairBuysWholeUnitsOfEndurance()
    {
        // 单价 10000 ÷ 300 = 33;金币 1000 → 30 点,实付 1000 − 1000 % 33 = 990;原来的 5 点耐久保留 → 35。
        Rig rig = Make(gold: 1000);
        MachineRepairPlan plan = rig.Jobs.RepairMachine();
        Assert.True(plan.Ok);
        Assert.Equal(990, plan.Cost);
        Assert.Equal(35, rig.Jobs.State.MachineEndurance);
        Assert.Equal(10, rig.Hero.Gold);
    }

    [Fact]
    public void ARepairThatCannotBuyAnyEnduranceOfABrokenMachineIsRefusedAndKeepsTheGold()
    {
        Rig rig = Make(gold: 20);
        rig.Jobs.State.SetMachine(0, 0);
        MachineRepairPlan plan = rig.Jobs.RepairMachine();
        Assert.Equal(ExpertJobError.Refused, plan.Error);
        Assert.Equal(20, rig.Hero.Gold);
        Assert.Equal(0, rig.Jobs.State.MachineEndurance);
    }

    [Fact]
    public void MachineServicesNeedTheDisjointerJob()
    {
        Rig rig = Make(gold: 99999, hired: false);
        Assert.Equal(0, rig.Jobs.RepairQuote());
        Assert.Equal(ExpertJobError.NotPermitted, rig.Jobs.RepairMachine().Error);
        Assert.Equal(ExpertJobError.NotPermitted, rig.Jobs.UpgradeMachine().Error);
        Assert.Equal(99999, rig.Hero.Gold);
        Assert.Equal(0, rig.Jobs.State.MachineEndurance);
    }

    [Theory]
    [InlineData(ExpertJobKind.Enchanter)]
    [InlineData(ExpertJobKind.Alchemist)]
    [InlineData(ExpertJobKind.DollController)]
    public void AnotherJobAtTheTopLevelCannotUseTheDisjointMachineServices(ExpertJobKind other)
    {
        // 目录里这个副职业也有一份脚本(等级表同分解师):它满级、有金币、机器档与耐久都有值,仍然不是分解师
        var catalog = new ExpertJobCatalog(Etc(), new[] { Disjointer(), ExpertJobScript.Parse(other, DisjointerLines()) }, Legacy(), ItemPriceRates.Neutral);
        Rig rig = Make(gold: 99999, hired: false, catalog: catalog);
        Assert.True(rig.Jobs.Restore(new HeroSave { ExpertJob = new SavedExpertJob((int)other, 700, 0, 100, 1) }));
        Assert.Equal(4, rig.Jobs.Level);
        Assert.Equal(0, rig.Jobs.RepairQuote());
        Assert.Equal(ExpertJobError.NotPermitted, rig.Jobs.RepairMachine().Error);
        Assert.Equal(ExpertJobError.NotPermitted, rig.Jobs.UpgradeMachine().Error);
        rig.Put(Gear(900));
        Assert.Equal(ExpertJobError.NotPermitted, rig.Jobs.Disjoint(InventoryTab.Equipment, 0).Error);
        Assert.Equal(99999, rig.Hero.Gold);
        Assert.Equal(100, rig.Jobs.State.MachineEndurance);
        Assert.Equal(1, rig.Jobs.State.MachineGrade);
    }

    [Fact]
    public void UpgradingNeedsTheExpertLevelTheCharacterLevelAndTheGoldAndSetsTheNewCap()
    {
        Rig rig = Make(gold: 29999);
        Assert.Equal(ExpertJobError.NotPermitted, rig.Jobs.UpgradeMachine().Error);            // 副职业 1 级:新档 1 必须小于副职业等级
        Assert.Equal(120, rig.Jobs.SetExp(120).NewExp);
        Assert.Equal(ExpertJobError.Refused, rig.Jobs.UpgradeMachine().Error);                 // 差 1 金币
        Assert.Equal(29999, rig.Hero.Gold);
        Assert.Equal(0, rig.Jobs.State.MachineGrade);

        rig.Hero.AddGold(1);
        MachineUpgradePlan plan = rig.Jobs.UpgradeMachine();
        Assert.True(plan.Ok);
        Assert.Equal(30000, plan.Cost);
        Assert.Equal(1, plan.NewGrade);
        Assert.Equal(0, rig.Hero.Gold);
        Assert.Equal(1, rig.Jobs.State.MachineGrade);
        Assert.Equal(300, rig.Jobs.State.MachineEndurance);
    }

    [Fact]
    public void UpgradingIsRefusedWhenTheCharacterIsBelowTheLimitOfTheNewGrade()
    {
        Rig rig = Make(charLevel: 9, gold: 99999, hired: false);
        Assert.True(rig.Jobs.Restore(new HeroSave { ExpertJob = new SavedExpertJob(3, 120, 0, 5, 0) }));
        Assert.Equal(2, rig.Jobs.Level);                      // 不在阈值上:角色等级再低也不退级
        MachineUpgradePlan plan = rig.Jobs.UpgradeMachine();
        Assert.Equal(ExpertJobError.LevelTooLow, plan.Error);
        Assert.Equal(99999, rig.Hero.Gold);
        Assert.Equal(0, rig.Jobs.State.MachineGrade);
    }

    [Fact]
    public void TheTopGradeCannotBeUpgradedFurther()
    {
        Rig rig = Make(gold: 99999);
        Assert.Equal(700, rig.Jobs.SetExp(700).NewExp);
        Assert.Equal(4, rig.Jobs.Level);
        rig.Jobs.State.SetMachine(2, 100);
        Assert.Equal(ExpertJobError.NotPermitted, rig.Jobs.UpgradeMachine().Error);
        Assert.Equal(99999, rig.Hero.Gold);
        Assert.Equal(2, rig.Jobs.State.MachineGrade);
        Assert.Equal(100, rig.Jobs.State.MachineEndurance);
    }

    // ------------------------------------------------------------------ 存档

    [Fact]
    public void CaptureWritesNothingForAFreshStateAndClearsStaleValues()
    {
        Rig fresh = Make(hired: false);
        var save = new HeroSave { ExpertJob = new SavedExpertJob(1, 1, 1, 1, 1) };
        save.ExpertRecipes.Add(9);
        fresh.Jobs.Capture(save);
        Assert.Null(save.ExpertJob);
        Assert.Empty(save.ExpertRecipes);
        Assert.DoesNotContain("expertjob", save.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("exrecipe", save.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureWritesTheNumbersAndTheRecipesInOrder()
    {
        Rig rig = Make();
        Assert.Equal(60, rig.Jobs.GainExp(60).NewExp);
        rig.Jobs.State.SetMachine(2, 77);
        foreach (int index in new[] { 239, 8, 0, 7 })
        {
            Assert.True(rig.Jobs.State.TrySetRecipe(index));
        }
        var save = new HeroSave();
        rig.Jobs.Capture(save);
        Assert.Equal(new SavedExpertJob(3, 60, 0, 77, 2), save.ExpertJob.Value);
        Assert.Equal(new[] { 0, 7, 8, 239 }, save.ExpertRecipes.ToArray());
        string text = save.ToText();
        Assert.Contains("expertjob 3 60 0 77 2\n", text);
        Assert.Contains("exrecipe 239\n", text);
    }

    [Theory]
    [InlineData(3, 0, 0, 0, 0, -1)]       // 只有种类
    [InlineData(0, 5, 0, 0, 0, -1)]       // 只有经验
    [InlineData(0, -1, 0, 0, 0, -1)]      // 只有放弃的标记(经验 −1)
    [InlineData(0, 0, 2, 0, 0, -1)]       // 只有放弃次数
    [InlineData(0, 0, 0, 4, 0, -1)]       // 只有机器耐久
    [InlineData(0, 0, 0, 0, 2, -1)]       // 只有机器档
    [InlineData(0, 0, 0, 0, 0, 9)]        // 只有一个配方
    public void AnySingleNonDefaultFieldMakesTheStateWorthSaving(int kind, int exp, int giveup, int endurance, int grade, int recipe)
    {
        Rig rig = Make(hired: false);
        var source = new HeroSave { ExpertJob = new SavedExpertJob(kind, exp, giveup, endurance, grade) };
        if (recipe >= 0)
        {
            source.ExpertRecipes.Add(recipe);
        }
        Assert.True(rig.Jobs.Restore(source));

        var save = new HeroSave();
        rig.Jobs.Capture(save);
        Assert.True(save.ExpertJob.HasValue);
        Assert.Equal(new SavedExpertJob(kind, exp, giveup, endurance, grade), save.ExpertJob.Value);
        Assert.Equal(recipe >= 0 ? new[] { recipe } : new int[0], save.ExpertRecipes.ToArray());
        Assert.Contains("expertjob ", save.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public void CapturingTwiceIntoTheSameSnapshotReplacesInsteadOfAppending()
    {
        Rig rig = Make();
        Assert.True(rig.Jobs.State.TrySetRecipe(4));
        var save = new HeroSave();
        save.ExpertRecipes.Add(77);                 // 快照里原有的旧配方
        rig.Jobs.Capture(save);
        rig.Jobs.Capture(save);
        Assert.Equal(new[] { 4 }, save.ExpertRecipes.ToArray());
        Assert.Equal(new SavedExpertJob(3, 0, 0, 5, 0), save.ExpertJob.Value);
    }

    [Fact]
    public void AGaveUpStateIsSavedWithItsMinusOneExpAndTheGiveUpCount()
    {
        Rig rig = Make(gold: 5000);
        rig.Hero.Quests.MarkCleared(901);
        rig.Hero.Quests.MarkCleared(902);
        Assert.True(rig.Jobs.GiveUp().Ok);
        var save = new HeroSave();
        rig.Jobs.Capture(save);
        Assert.Equal(new SavedExpertJob(0, -1, 1, 0, 0), save.ExpertJob.Value);
        Assert.Contains("expertjob 0 -1 1 0 0\n", save.ToText());

        Assert.True(HeroSave.TryParse(save.ToText(), out HeroSave back, out string error), error);
        Rig other = Make(hired: false);
        Assert.True(other.Jobs.Restore(back));
        Assert.False(other.Jobs.State.HasJob);
        Assert.Equal(-1, other.Jobs.State.Exp);
        Assert.Equal(1, other.Jobs.State.GiveupCount);
    }

    [Fact]
    public void RestoreReplacesTheWholeState()
    {
        Rig rig = Make();
        rig.Jobs.State.TrySetRecipe(3);
        Assert.Equal(25, rig.Jobs.GainExp(25).NewExp);
        Assert.True(rig.Jobs.Restore(new HeroSave()));                 // 没有副职业那一行 = 全新
        Assert.False(rig.Jobs.State.HasJob);
        Assert.Equal(0, rig.Jobs.State.Exp);
        Assert.Equal(0, rig.Jobs.State.MachineEndurance);
        Assert.Equal(0, rig.Jobs.State.GiveupCount);
        Assert.Empty(rig.Jobs.State.LearnedRecipes());
        Assert.Equal(0, rig.Jobs.Level);
    }

    [Fact]
    public void RestoreFixesInvalidValuesAndReportsThem()
    {
        Rig rig = Make(hired: false);
        var save = new HeroSave { ExpertJob = new SavedExpertJob(9, 50, 70000, 7, -3) };
        save.ExpertRecipes.AddRange(new[] { 5, -1, 240, 5 });
        Assert.False(rig.Jobs.Restore(save));
        Assert.Equal(ExpertJobKind.None, rig.Jobs.Kind);              // 种类不认得 = 没有副职业
        Assert.Equal(50, rig.Jobs.State.Exp);
        Assert.Equal(65535, rig.Jobs.State.GiveupCount);
        Assert.Equal(7, rig.Jobs.State.MachineEndurance);
        Assert.Equal(0, rig.Jobs.State.MachineGrade);
        Assert.Equal(new[] { 5 }, rig.Jobs.State.LearnedRecipes().ToArray());     // 越界的序号丢掉,重复的合成一个

        var clean = new HeroSave { ExpertJob = new SavedExpertJob(4, 0, 0, 0, 255) };
        Assert.True(rig.Jobs.Restore(clean));
        Assert.Equal(ExpertJobKind.DollController, rig.Jobs.Kind);
        Assert.Equal(255, rig.Jobs.State.MachineGrade);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(240)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void AnOutOfRangeRecipeAloneMakesTheRestoreUnclean(int recipe)
    {
        Rig rig = Make(hired: false);

        // 副职业那一行本身完全合法,只有一个配方序号越界:丢掉它,并且报告"有改动"。
        var withJob = new HeroSave { ExpertJob = new SavedExpertJob(3, 10, 0, 5, 0) };
        withJob.ExpertRecipes.AddRange(new[] { 7, recipe });
        Assert.False(rig.Jobs.Restore(withJob));
        Assert.Equal(ExpertJobKind.Disjointer, rig.Jobs.Kind);
        Assert.Equal(new[] { 7 }, rig.Jobs.State.LearnedRecipes().ToArray());

        // 没有副职业那一行(全新)时同样。
        var fresh = new HeroSave();
        fresh.ExpertRecipes.Add(recipe);
        Assert.False(rig.Jobs.Restore(fresh));
        Assert.Equal(ExpertJobKind.None, rig.Jobs.Kind);
        Assert.Empty(rig.Jobs.State.LearnedRecipes());

        // 位图两端的序号 0 与 239 都是合法的。
        var edges = new HeroSave { ExpertJob = new SavedExpertJob(3, 10, 0, 5, 0) };
        edges.ExpertRecipes.AddRange(new[] { 0, 239 });
        Assert.True(rig.Jobs.Restore(edges));
        Assert.Equal(new[] { 0, 239 }, rig.Jobs.State.LearnedRecipes().ToArray());
    }

    [Fact]
    public void TheStateSurvivesASaveThroughTheHeroSnapshotAndTheTextFormat()
    {
        Rig rig = Make(gold: 777, rolls: OneDisjoint);
        rig.Put(Gear(900));
        Assert.True(rig.Jobs.Disjoint(InventoryTab.Equipment, 0).Ok);
        rig.Jobs.State.TrySetRecipe(5);
        rig.Jobs.State.TrySetRecipe(17);

        HeroSave save = rig.Hero.Snapshot();
        rig.Jobs.Capture(save);
        string text = save.ToText();
        Assert.True(HeroSave.TryParse(text, out HeroSave back, out string error), error);

        Rig other = Make(charLevel: 1, hired: false, rolls: new[] { 1000, 5000, 0, 0, 99 });
        other.Hero.Restore(back, code => rig.Items.TryGetValue(code, out ItemInfo info) ? info : null);
        Assert.True(other.Jobs.Restore(back));

        Assert.Equal(ExpertJobKind.Disjointer, other.Jobs.Kind);
        Assert.Equal(4, other.Jobs.State.Exp);
        Assert.Equal(3, other.Jobs.State.MachineEndurance);
        Assert.Equal(0, other.Jobs.State.MachineGrade);
        Assert.Equal(0, other.Jobs.State.GiveupCount);
        Assert.Equal(new[] { 5, 17 }, other.Jobs.State.LearnedRecipes().ToArray());
        Assert.Equal(1, other.Jobs.Level);                           // 等级不存,读档后由经验重算
        Assert.Equal(777, other.Hero.Gold);
        Assert.Equal(16, other.Hero.Bag.CountOf(950));
        Assert.Equal(2, other.Hero.Bag.CountOf(951));

        // 读回来的会话接着分解:耐久与经验从存档里的数继续(耐久抽值 0 → 扣 1、经验抽值 0 → 2)。
        other.Put(Gear(901));
        DisjointReport next = other.Jobs.Disjoint(InventoryTab.Equipment, 0);
        Assert.True(next.Ok);
        Assert.Equal(2, other.Jobs.State.MachineEndurance);
        Assert.Equal(6, other.Jobs.State.Exp);
        Assert.Equal(32, other.Hero.Bag.CountOf(950));
    }

    // ------------------------------------------------------------------ 构造

    [Fact]
    public void TheConstructorRejectsMissingRequiredArguments()
    {
        HeroProgress hero = ExpTests.Progress();
        ExpertJobCatalog catalog = Catalog();
        Func<int, ItemInfo> lookup = code => null;
        Assert.Throws<ArgumentNullException>(() => new ExpertJobSession(null, catalog, lookup));
        Assert.Throws<ArgumentNullException>(() => new ExpertJobSession(hero, null, lookup));
        Assert.Throws<ArgumentNullException>(() => new ExpertJobSession(hero, catalog, null));
        Rig rig = Make();
        Assert.Throws<ArgumentNullException>(() => rig.Jobs.Capture(null));
        Assert.Throws<ArgumentNullException>(() => rig.Jobs.Restore(null));
    }
}
