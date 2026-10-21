using Dnf.Game;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物出招决策(证据 E-100、E-101):出生后 3 秒内不决策、到点抽一次签并定下一次、逐条记录判断"能不能用"、能用的里面随机挑一个、进入敌对状态时概率乘 1.3。
/// 随机数用事先排好的序列替身,既能精确构造"中与不中",又能检验每一步花了几个随机数、顺序对不对。记录与坐标全是合成的。
/// </summary>
public class MonsterAttackChooserTests
{
    /// <summary>按顺序给出事先排好的随机数;多要就抛异常(说明多花了随机数)。</summary>
    private sealed class Script
    {
        private readonly Queue<int> _values;

        public Script(params int[] values)
        {
            _values = new Queue<int>(values);
        }

        public int Remaining => _values.Count;

        public int Next()
        {
            if (_values.Count == 0)
            {
                throw new InvalidOperationException("随机数序列用完了:决策多花了随机数");
            }
            return _values.Dequeue();
        }
    }

    private static readonly AttackTarget Near = new AttackTarget(450, 300, 40, 20);
    private static readonly AttackTarget Far = new AttackTarget(100, 300, 40, 20);

    /// <summary>正面 100、背面 20、纵向各 30 的记录(自己在 (500, 300) 朝左时,x = 450 在射程里、x = 100 不在)。</summary>
    private static AttackKindRecord Ranged(float inRange = 100f, float anywhere = 0f, bool ready = true, float vsDown = 0f)
    {
        return new AttackKindRecord(inRange, anywhere, 0f, ready, 100, 0, 20, 0, 30, 0, 30, 0, vsDown);
    }

    /// <summary>没有射程范围(全 0)的基础攻击。</summary>
    private static AttackKindRecord Basic(float anywhere = 0f)
    {
        return new AttackKindRecord(15f, anywhere, 1f, true, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private static MonsterAttackChooser Chooser(Script script, int delay, params AttackKindRecord[] records)
    {
        return new MonsterAttackChooser(records, delay, script.Next);
    }

    private static int Decide(MonsterAttackChooser c, long clock, AttackTarget target, bool hasTarget = true, Func<int, bool> reach = null)
    {
        return c.Decide(clock, 500, 300, true, hasTarget, target, reach);
    }

    [Fact]
    public void NothingIsDecidedBeforeTheSpawnLockoutAndNoRandomNumberIsSpent()
    {
        var s = new Script();
        MonsterAttackChooser c = Chooser(s, 1000, Ranged());
        Assert.Equal(-1, Decide(c, 0, Near));
        Assert.Equal(-1, Decide(c, MonsterAttackRules.SpawnLockoutMs - 1, Near));
        Assert.Equal(0L, c.NextDecisionMs);
        Assert.Equal(-1, Chooser(s, 1000).Decide(10000, 0, 0, true, true, Near, null));   // 没有记录:永远不出招
    }

    [Fact]
    public void TheFirstDecisionAfterTheLockoutFixesTheNextTimeWithOneRandomNumberThenRollsEachCandidateThenPicksOne()
    {
        // 随机数顺序:下一次的抖动(0 → −100)、在射程里的掷骰(0 必中)、挑选(0 % 1)
        var s = new Script(0, 0, 0);
        MonsterAttackChooser c = Chooser(s, 1000, Ranged(inRange: 50f));
        Assert.Equal(0, Decide(c, 3000, Near));
        Assert.Equal(3000 - 100 + 1000, c.NextDecisionMs);
        Assert.Equal(0, s.Remaining);
    }

    [Fact]
    public void DecisionsOnlyComeWhenTheClockReachesTheNextTime()
    {
        var s = new Script(0, 9999, 9999);                 // 第一次:抖动、射程里的掷骰没中(9999 ≥ 5000)、不论远近的掷骰(0%)也没中
        MonsterAttackChooser c = Chooser(s, 1000, Ranged(inRange: 50f));
        Assert.Equal(-1, Decide(c, 3000, Near));
        Assert.Equal(0, s.Remaining);
        Assert.Equal(3900L, c.NextDecisionMs);
        Assert.Equal(-1, Decide(c, 3899, Near));            // 没到点:不花随机数
        var again = new Script(100, 0, 0);                  // 3900 到点:抖动 100 % 200 − 100 = 0、掷骰、挑选
        MonsterAttackChooser c2 = Chooser(again, 1000, Ranged(inRange: 50f));
        Assert.Equal(0, Decide(c2, 3900, Near));
        Assert.Equal(3900 + 0 + 1000, c2.NextDecisionMs);
    }

    [Fact]
    public void AFailedInRangeRollFallsThroughToTheAnywhereRollAndTheAnywhereRollCanSaveIt()
    {
        // 射程里 15%、不论远近 5%:抖动 0;射程里掷 1500 没中;不论远近掷 499 中;挑选 0
        var s = new Script(0, 1500, 499, 0);
        MonsterAttackChooser c = Chooser(s, 1000, Ranged(inRange: 15f, anywhere: 5f));
        Assert.Equal(0, Decide(c, 3000, Near));
        Assert.Equal(0, s.Remaining);

        var miss = new Script(0, 1500, 500);                // 不论远近掷 500 也没中:不出招
        MonsterAttackChooser c2 = Chooser(miss, 1000, Ranged(inRange: 15f, anywhere: 5f));
        Assert.Equal(-1, Decide(c2, 3000, Near));
        Assert.Equal(0, miss.Remaining);
    }

    [Fact]
    public void OutOfRangeSkipsTheInRangeRollAndOnlyTheAnywhereChanceCanFire()
    {
        var s = new Script(0, 99, 0);                       // 抖动、不论远近的掷骰(99 < 100:1% 中)、挑选
        MonsterAttackChooser c = Chooser(s, 1000, Ranged(inRange: 100f, anywhere: 1f));
        Assert.Equal(0, Decide(c, 3000, Far));
        Assert.Equal(0, s.Remaining);

        var none = new Script(0, 5000);
        MonsterAttackChooser c2 = Chooser(none, 1000, Ranged(inRange: 100f, anywhere: 1f));
        Assert.Equal(-1, Decide(c2, 3000, Far));
        Assert.Equal(0, none.Remaining);
    }

    [Fact]
    public void WithoutATargetOnlyTheAnywhereRollRuns()
    {
        var s = new Script(0, 0, 0);
        MonsterAttackChooser c = Chooser(s, 1000, Ranged(inRange: 100f, anywhere: 50f));
        Assert.Equal(0, Decide(c, 3000, Near, hasTarget: false));
        Assert.Equal(0, s.Remaining);
    }

    [Fact]
    public void RecordsThatAreNotReadyOrAreDisabledNeverEnterTheDrawAndSeveralCandidatesArePickedByRandomModuloCount()
    {
        // 三条记录:第 0 条没准备好、第 1 条被禁用、第 2、3 条都能用;各掷一次射程里的骰(都中),再挑 5 % 2 = 1 → 第 3 条
        var s = new Script(0, 0, 0, 5);
        MonsterAttackChooser c = Chooser(s, 1000, Ranged(ready: false), Ranged(), Ranged(), Ranged());
        c.Disable(1);
        Assert.False(c.IsReady(0));
        Assert.False(c.IsEnabled(1));
        Assert.Equal(3, Decide(c, 3000, Near));
        Assert.Equal(0, s.Remaining);

        // 标志可以改回来(原版按 [cooltime] 恢复,这里只提供开关)
        c.SetReady(0, true);
        Assert.True(c.IsReady(0));
    }

    [Fact]
    public void EnteringHostileMultipliesTheInRangeChanceByOnePointThreeExactlyOnce()
    {
        var s = new Script();
        MonsterAttackChooser c = Chooser(s, 1000, Ranged(inRange: 15f, anywhere: 3f));
        c.EnterHostile();
        c.EnterHostile();                                   // 只放大一次
        Assert.True(c.Hostile);
        Assert.Equal(15f * 1.3f, c.Record(0).ChanceInRange);
        Assert.Equal(3f, c.Record(0).ChanceAnywhere);        // 只放大射程里的概率
        // 掷 1520:放大前(15%)不中,放大后(19.5%)中
        var s2 = new Script(0, 1520, 0);
        MonsterAttackChooser hostile = Chooser(s2, 1000, Ranged(inRange: 15f));
        hostile.EnterHostile();
        Assert.Equal(0, Decide(hostile, 3000, Near));
        var s3 = new Script(0, 1520, 5000);                 // 没放大:射程里没中,不论远近的概率 0 也没中
        Assert.Equal(-1, Decide(Chooser(s3, 1000, Ranged(inRange: 15f)), 3000, Near));
    }

    [Fact]
    public void ADownTargetOnlyPassesTheRangeCheckThroughTheDownGateWhichDefaultsToNever()
    {
        var down = new AttackTarget(450, 300, 40, 20, isDown: true);
        // 缺省 0%:过不了倒地门槛(掷骰花一个随机数),射程里的那次不再掷;只剩不论远近的那次
        var s = new Script(0, 0, 5000);
        Assert.Equal(-1, Decide(Chooser(s, 1000, Ranged(inRange: 100f)), 3000, down));
        Assert.Equal(0, s.Remaining);

        // 倒地门槛 100%:过了门槛再掷射程里的骰
        var open = new Script(0, 0, 0, 0);
        Assert.Equal(0, Decide(Chooser(open, 1000, Ranged(inRange: 100f, vsDown: 100f)), 3000, down));
        Assert.Equal(0, open.Remaining);
    }

    [Fact]
    public void ARecordWithoutExtentsUsesTheCallersReachJudgmentWithoutARollAndNeverReachesOtherwise()
    {
        // 推断:全 0 范围的记录靠调用方的几何判断;够得着就算能用、不掷射程里的骰
        var s = new Script(0, 0);                           // 抖动、挑选
        Assert.Equal(0, Decide(Chooser(s, 1000, Basic()), 3000, Near, reach: _ => true));
        Assert.Equal(0, s.Remaining);

        // 够不着(或没给判断):只剩不论远近的掷骰(0% 也花一个随机数)
        var out1 = new Script(0, 0);
        Assert.Equal(-1, Decide(Chooser(out1, 1000, Basic()), 3000, Near, reach: _ => false));
        Assert.Equal(0, out1.Remaining);
        var out2 = new Script(0, 0);
        Assert.Equal(-1, Decide(Chooser(out2, 1000, Basic()), 3000, Near, reach: null));
        Assert.Equal(0, out2.Remaining);
        // 基础攻击自己写了 1% 的不论远近概率:0 中
        var any = new Script(0, 0, 0);
        Assert.Equal(0, Decide(Chooser(any, 1000, Basic(anywhere: 1f)), 3000, Far, reach: _ => false));
    }

    [Fact]
    public void AnyInRangeSpendsNoRandomNumbersAndSkipsRecordsThatAreNotReadyOrDisabled()
    {
        var s = new Script();
        MonsterAttackChooser c = Chooser(s, 1000, Ranged(ready: false), Ranged(), Basic());
        Assert.True(c.AnyInRange(500, 300, true, Near, null));
        Assert.False(c.AnyInRange(500, 300, true, Far, null));                       // 第 2 条没有范围、没给几何判断
        Assert.True(c.AnyInRange(500, 300, true, Far, i => i == 2));                 // 几何判断说够得着第 2 条
        c.Disable(1);
        Assert.False(c.AnyInRange(500, 300, true, Near, null));                      // 第 0 条没准备好,第 1 条被禁用
        Assert.True(c.AnyInRange(500, 300, true, Near, i => i == 2));
        Assert.Equal(3, c.Count);
    }
}
