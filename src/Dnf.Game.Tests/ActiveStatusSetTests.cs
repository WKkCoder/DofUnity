using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 异常状态集合(M43,证据 E-136):概率与时长、同种不叠加、失去控制、持续伤害的间隔与总量、不超过服务端允许值、摇脱、睡眠被打醒、减速、清空。
/// 数值全是合成的:威力 1 里冰冻一级 2 满级 12、眩晕一级 1 满级 11(最高等级 11,等级 k 的进度是 (k − 1) / 10),默认秒数 = 3 + 种类号。
/// </summary>
public sealed class ActiveStatusSetTests
{
    private static ActiveStatusTable Table(int lightningMs = 400)
    {
        var power1 = new float[ActiveStatusTable.KindCount * 2];
        power1[(int)ActiveStatusKind.Freeze * 2] = 2f;
        power1[(int)ActiveStatusKind.Freeze * 2 + 1] = 12f;
        power1[(int)ActiveStatusKind.Stun * 2] = 1f;
        power1[(int)ActiveStatusKind.Stun * 2 + 1] = 11f;
        var seconds = new int[ActiveStatusTable.KindCount];
        for (int k = 0; k < seconds.Length; k++)
        {
            seconds[k] = 3 + k;
        }
        return ActiveStatusTable.Synthetic(11, seconds, power1, new float[ActiveStatusTable.KindCount * 2], null, 1f, 1f, lightningMs);
    }

    private static ActiveStatusEntry E(ActiveStatusKind kind, int duration = 3000, float probability = 100f, int level = 0, params int[] extra)
    {
        return ActiveStatusEntry.Create(kind, probability, level, duration, extra);
    }

    private static ActiveStatusSet Set(int lightningMs = 400)
    {
        return new ActiveStatusSet(Table(lightningMs));
    }

    private static int Total(List<ActiveStatusTick> ticks)
    {
        return ticks.Sum(t => t.Damage);
    }

    // ---- 概率 ----

    [Fact]
    public void TheChanceIsThePercentOfTheAttackInfo()
    {
        ActiveStatusEntry forty = E(ActiveStatusKind.Stun, probability: 40f);
        Assert.Equal(ActiveStatusApply.Applied, Set().Apply(forty, 0, 0.399));
        Assert.Equal(ActiveStatusApply.Missed, Set().Apply(forty, 0, 0.4));
        // 100% 永远中,0% 永远不中,随机数不是数也不中
        Assert.Equal(ActiveStatusApply.Applied, Set().Apply(E(ActiveStatusKind.Stun), 0, 0.9999999));
        Assert.Equal(ActiveStatusApply.Missed, Set().Apply(E(ActiveStatusKind.Stun, probability: 0f), 0, 0.0));
        Assert.Equal(ActiveStatusApply.Missed, Set().Apply(forty, 0, double.NaN));
        var set = Set();
        set.Apply(forty, 0, 0.9);
        Assert.Equal((0, 1), (set.Count, set.MissedCount));
        // 负数概率取绝对值(客户端 ANDPS 去符号)
        Assert.Equal(ActiveStatusApply.Applied, Set().Apply(E(ActiveStatusKind.Stun, probability: -40f), 0, 0.399));
    }

    // ---- 时长 ----

    [Fact]
    public void TheDurationComesFromTheAttackInfoElseFromTheTable()
    {
        // 攻击信息写了毫秒数就用它
        var a = Set();
        a.Apply(E(ActiveStatusKind.Stun, 2500, level: 11), 0, 0.0);
        Assert.Equal(2500, a.Find(ActiveStatusKind.Stun).DurationMs);

        // 没写(0):眩晕按威力 1 取等级插值再乘 1000;等级 0 当一级
        foreach ((int level, int ms) in new[] { (0, 1000), (1, 1000), (6, 6000), (11, 11000), (50, 11000) })
        {
            var s = Set();
            Assert.Equal(ActiveStatusApply.Applied, s.Apply(E(ActiveStatusKind.Stun, 0, level: level), 0, 0.0));
            Assert.Equal(ms, s.Find(ActiveStatusKind.Stun).DurationMs);
        }

        // 表型的种类(中毒)按默认秒数
        var poison = Set();
        poison.Apply(E(ActiveStatusKind.Poison, 0), 0, 0.0);
        Assert.Equal(5000, poison.Find(ActiveStatusKind.Poison).DurationMs);
    }

    [Fact]
    public void KindsWithoutAnEffectOrWithoutADurationAreIgnored()
    {
        var s = Set();
        Assert.Equal(ActiveStatusApply.Ignored, s.Apply(E(ActiveStatusKind.None), 0, 0.0));
        Assert.Equal(ActiveStatusApply.Ignored, s.Apply(E(ActiveStatusKind.Element), 0, 0.0));
        Assert.Equal(ActiveStatusApply.Ignored, s.Apply(null, 0, 0.0));
        // 元素没有时长;空表又没写毫秒数的状态时长是 0
        Assert.Equal(ActiveStatusApply.Ignored, new ActiveStatusSet(null).Apply(E(ActiveStatusKind.Stun, 0), 0, 0.0));
        Assert.Equal(0, s.Count);
        Assert.Equal(0, s.MissedCount);   // 没掷概率
    }

    // ---- 同种不叠加 ----

    [Fact]
    public void TheSameKindDoesNotStackAndTheLongerOneStays()
    {
        var s = Set();
        Assert.Equal(ActiveStatusApply.Applied, s.Apply(E(ActiveStatusKind.Stun, 3000), 0, 0.0));
        s.Advance(1000, null);
        Assert.Equal(2000.0, s.Find(ActiveStatusKind.Stun).RemainingMs);
        Assert.Equal(ActiveStatusApply.Kept, s.Apply(E(ActiveStatusKind.Stun, 1500), 0, 0.0));
        Assert.Equal(2000.0, s.Find(ActiveStatusKind.Stun).RemainingMs);
        Assert.Equal(ActiveStatusApply.Refreshed, s.Apply(E(ActiveStatusKind.Stun, 4000), 0, 0.0));
        Assert.Equal((1, 4000.0), (s.Count, s.Find(ActiveStatusKind.Stun).RemainingMs));
        Assert.Equal(2, s.AppliedCount);   // 挂上与刷新都算,"保持"不算
    }

    [Fact]
    public void DifferentKindsLiveSideBySide()
    {
        var s = Set();
        s.Apply(E(ActiveStatusKind.Stun, 3000), 0, 0.0);
        s.Apply(E(ActiveStatusKind.Bleeding, 3000, extra: 60), 0, 0.0);
        s.Apply(E(ActiveStatusKind.Slow, 3000, extra: new[] { 20, 10 }), 0, 0.0);
        Assert.Equal(3, s.Count);
        Assert.True(s.Has(ActiveStatusKind.Bleeding));
        Assert.False(s.Has(ActiveStatusKind.Freeze));
    }

    // ---- 失去控制 ----

    [Fact]
    public void OnlyTheFiveLockingKindsLockControl()
    {
        var locking = new HashSet<ActiveStatusKind>
        {
            ActiveStatusKind.Freeze, ActiveStatusKind.Stun, ActiveStatusKind.Stone, ActiveStatusKind.Sleep, ActiveStatusKind.Hold,
        };
        for (int k = 0; k < (int)ActiveStatusKind.None; k++)
        {
            var kind = (ActiveStatusKind)k;
            var s = Set();
            ActiveStatusApply r = s.Apply(E(kind, 1000), 0, 0.0);
            if (kind == ActiveStatusKind.Element)
            {
                Assert.Equal(ActiveStatusApply.Ignored, r);
                continue;
            }
            Assert.Equal(locking.Contains(kind), s.ControlLocked);
            Assert.Equal(locking.Contains(kind) ? 1000.0 : 0.0, s.LockRemainingMs);
        }
    }

    [Fact]
    public void ALockEndsExactlyWhenItsTimeIsUp()
    {
        var s = Set();
        s.Apply(E(ActiveStatusKind.Stun, 1000), 0, 0.0);
        s.Advance(999, null);
        Assert.True(s.ControlLocked);
        Assert.Equal(1.0, s.LockRemainingMs);
        s.Advance(1, null);
        Assert.False(s.ControlLocked);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void TheLongestLockDecidesTheRemainingLockTime()
    {
        var s = Set();
        s.Apply(E(ActiveStatusKind.Stun, 1000), 0, 0.0);
        s.Apply(E(ActiveStatusKind.Freeze, 4000), 0, 0.0);
        s.Apply(E(ActiveStatusKind.Slow, 9000), 0, 0.0);      // 减速不锁,不算
        Assert.Equal(4000.0, s.LockRemainingMs);
        s.Advance(1500, null);
        Assert.Equal(2500.0, s.LockRemainingMs);
    }

    // ---- 持续伤害 ----

    [Fact]
    public void BleedingSpreadsItsTotalEvery500Ms()
    {
        var one = Set();
        one.Apply(E(ActiveStatusKind.Bleeding, 5000, extra: 480), 0, 0.0);
        ActiveStatusInstance inst = one.Find(ActiveStatusKind.Bleeding);
        Assert.Equal((500, 10, 48), (inst.IntervalMs, inst.TickCount, inst.TickDamage));

        // 一次推完与每 10 毫秒推一次,掉的跳一模一样
        var big = new List<ActiveStatusTick>();
        one.Advance(5000, big);
        Assert.Equal(10, big.Count);
        Assert.All(big, t => Assert.Equal((ActiveStatusKind.Bleeding, 48), (t.Kind, t.Damage)));
        Assert.Equal(480, Total(big));
        Assert.Equal(0, one.Count);

        var small = Set();
        small.Apply(E(ActiveStatusKind.Bleeding, 5000, extra: 480), 0, 0.0);
        var stepped = new List<ActiveStatusTick>();
        var timeline = new List<int>();
        for (int t = 10; t <= 5000; t += 10)
        {
            int before = stepped.Count;
            small.Advance(10, stepped);
            if (stepped.Count > before)
            {
                timeline.Add(t);
            }
        }
        Assert.Equal(10, stepped.Count);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => i * 500), timeline);
        Assert.Equal(480L, small.TickDamageTotal);
    }

    [Fact]
    public void ATotalThatDoesNotDivideEvenlyLosesTheRemainderToTruncation()
    {
        var s = Set();
        s.Apply(E(ActiveStatusKind.Bleeding, 3000, extra: 100), 0, 0.0);
        var ticks = new List<ActiveStatusTick>();
        s.Advance(3000, ticks);
        Assert.Equal(6, ticks.Count);
        Assert.All(ticks, t => Assert.Equal(16, t.Damage));   // 100 / 6 向零取整
        Assert.True(Total(ticks) <= 100);
    }

    [Fact]
    public void ADurationShorterThanOneIntervalStillTicksOnceWithTheWholeTotal()
    {
        var s = Set();
        s.Apply(E(ActiveStatusKind.Bleeding, 300, extra: 70), 0, 0.0);
        var ticks = new List<ActiveStatusTick>();
        s.Advance(299, ticks);
        Assert.Empty(ticks);
        s.Advance(1, ticks);
        Assert.Equal(new[] { 70 }, ticks.Select(t => t.Damage));
    }

    [Fact]
    public void PoisonAndBurnTickEverySecondAndLightningAndStoneUseTheTablesInterval()
    {
        var s = Set(400);
        s.Apply(E(ActiveStatusKind.Poison, 3000, extra: 300), 0, 0.0);
        s.Apply(E(ActiveStatusKind.Burn, 3000, extra: new[] { 600, 0, 1, 3000 }), 0, 0.0);
        s.Apply(E(ActiveStatusKind.Lightning, 2000, extra: 500), 0, 0.0);
        s.Apply(E(ActiveStatusKind.Stone, 2000, extra: 250), 0, 0.0);
        Assert.Equal((1000, 3, 100), Triple(s, ActiveStatusKind.Poison));
        Assert.Equal((1000, 3, 200), Triple(s, ActiveStatusKind.Burn));
        Assert.Equal((400, 5, 100), Triple(s, ActiveStatusKind.Lightning));
        Assert.Equal((400, 5, 50), Triple(s, ActiveStatusKind.Stone));

        // 表里没有间隔时感电、石化退回 500
        var fallback = Set(0);
        fallback.Apply(E(ActiveStatusKind.Lightning, 2000, extra: 500), 0, 0.0);
        Assert.Equal((500, 4, 125), Triple(fallback, ActiveStatusKind.Lightning));

        var ticks = new List<ActiveStatusTick>();
        s.Advance(3000, ticks);
        Assert.Equal(3 + 3 + 5 + 5, ticks.Count);
        Assert.Equal(300 + 600 + 500 + 250, Total(ticks));
    }

    private static (int Interval, int Count, int Damage) Triple(ActiveStatusSet s, ActiveStatusKind kind)
    {
        ActiveStatusInstance i = s.Find(kind);
        return (i.IntervalMs, i.TickCount, i.TickDamage);
    }

    [Fact]
    public void ADamageKindWithoutAnAmountJustOccupiesItsTime()
    {
        var s = Set();
        s.Apply(E(ActiveStatusKind.Poison, 2000), 0, 0.0);
        s.Apply(E(ActiveStatusKind.Burn, 2000, extra: new[] { 0, 0, 1, 3000 }), 0, 0.0);
        var ticks = new List<ActiveStatusTick>();
        s.Advance(2000, ticks);
        Assert.Empty(ticks);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void KindsThatDoNotHurtNeverTick()
    {
        var s = Set();
        foreach (ActiveStatusKind kind in new[] { ActiveStatusKind.Stun, ActiveStatusKind.Freeze, ActiveStatusKind.Slow, ActiveStatusKind.Curse, ActiveStatusKind.Blind })
        {
            s.Apply(E(kind, 2000, extra: new[] { 500, 500, 500, 500 }), 0, 0.0);
        }
        var ticks = new List<ActiveStatusTick>();
        s.Advance(2000, ticks);
        Assert.Empty(ticks);
    }

    [Fact]
    public void EveryTickStaysWithinWhatTheServerAllowsAndTheTotalNeverPassesTheAmount()
    {
        ActiveStatusTable table = Table();
        var random = new Random(12345);
        foreach (ActiveStatusKind kind in new[] { ActiveStatusKind.Poison, ActiveStatusKind.Lightning, ActiveStatusKind.Stone, ActiveStatusKind.Burn, ActiveStatusKind.Bleeding })
        {
            for (int n = 0; n < 400; n++)
            {
                int amount = random.Next(1, 30001);
                int duration = random.Next(50, 30001);
                var s = new ActiveStatusSet(table);
                s.Apply(E(kind, duration, extra: amount), 0, 0.0);
                ActiveStatusInstance i = s.Find(kind);
                int allowed = ActiveStatusFormulas.ServerAllowedDamage(table, (int)kind, new ActiveStatusDamageInput(amount, duration));
                Assert.True(i.TickDamage <= allowed, kind + " 每跳 " + i.TickDamage + " 超过服务端允许值 " + allowed);
                Assert.True((long)i.TickDamage * i.TickCount <= amount);
                var ticks = new List<ActiveStatusTick>();
                s.Advance(duration, ticks);
                Assert.Equal(i.TickCount, ticks.Count);
            }
        }
    }

    [Fact]
    public void TheTimeStepDoesNotChangeTheDamageThatDrops()
    {
        foreach (int step in new[] { 1, 7, 33, 100, 499, 500, 2500, 100000 })
        {
            var s = Set();
            s.Apply(E(ActiveStatusKind.Bleeding, 4000, extra: 800), 0, 0.0);
            s.Apply(E(ActiveStatusKind.Poison, 4000, extra: 400), 0, 0.0);
            var ticks = new List<ActiveStatusTick>();
            for (int t = 0; t < 4000 + step; t += step)
            {
                s.Advance(step, ticks);
            }
            Assert.Equal((800 / 8) * 8 + (400 / 4) * 4, Total(ticks));
            Assert.Equal(0, s.Count);
        }
    }

    // ---- 摇脱 ----

    [Fact]
    public void ShakingShortensOnlyFreezeStunAndStone()
    {
        var s = Set();
        s.Apply(E(ActiveStatusKind.Freeze, 3000), 0, 0.0);
        s.Apply(E(ActiveStatusKind.Sleep, 3000), 0, 0.0);
        s.Apply(E(ActiveStatusKind.Hold, 3000), 0, 0.0);
        Assert.True(s.Shake(1000));
        Assert.Equal(2000.0, s.Find(ActiveStatusKind.Freeze).RemainingMs);
        Assert.Equal(3000.0, s.Find(ActiveStatusKind.Sleep).RemainingMs);
        Assert.Equal(3000.0, s.Find(ActiveStatusKind.Hold).RemainingMs);
        Assert.True(s.Shake(5000));                      // 摇过头:冰冻没了,其余照旧
        Assert.False(s.Has(ActiveStatusKind.Freeze));
        Assert.Equal(2, s.Count);
        Assert.False(s.Shake(100));                      // 剩下的都摇不脱
        Assert.True(s.ControlLocked);
    }

    [Fact]
    public void AShakenStoneStopsTickingWhenItsShortenedLifeEnds()
    {
        var s = Set();
        s.Apply(E(ActiveStatusKind.Stone, 4000, extra: 800), 0, 0.0);     // 间隔 400,10 跳,每跳 80
        var ticks = new List<ActiveStatusTick>();
        s.Advance(1000, ticks);
        Assert.Equal(2, ticks.Count);
        s.Shake(2000);                                   // 4000 缩短成 2000,已过 1000,还剩 1000
        s.Advance(1000, ticks);
        Assert.Equal(0, s.Count);
        Assert.Equal(5, ticks.Count);                    // 400、800、1200、1600、2000 五跳都在缩短后的 2000 毫秒内;2400 以后的不再掉
        Assert.Equal(5 * 80L, s.TickDamageTotal);
    }

    // ---- 睡眠、减速、清空 ----

    [Fact]
    public void AHitWakesASleeperButNothingElse()
    {
        var s = Set();
        s.Apply(E(ActiveStatusKind.Sleep, 5000), 0, 0.0);
        s.Apply(E(ActiveStatusKind.Stun, 5000), 0, 0.0);
        s.Apply(E(ActiveStatusKind.Bleeding, 5000, extra: 100), 0, 0.0);
        s.NotifyHit();
        Assert.False(s.Has(ActiveStatusKind.Sleep));
        Assert.True(s.Has(ActiveStatusKind.Stun));
        Assert.True(s.Has(ActiveStatusKind.Bleeding));
        Assert.True(s.ControlLocked);
    }

    [Fact]
    public void SlowComesFromTheTwoExtrasAndIsClamped()
    {
        var none = Set();
        Assert.Equal((0, 0), (none.MoveSlowPercent, none.AttackSlowPercent));

        var s = Set();
        s.Apply(E(ActiveStatusKind.Slow, 3000, extra: new[] { 30, 20 }), 0, 0.0);
        Assert.Equal((30, 20), (s.MoveSlowPercent, s.AttackSlowPercent));
        Assert.False(s.ControlLocked);

        var big = Set();
        big.Apply(E(ActiveStatusKind.Slow, 3000, extra: new[] { 300, -5 }), 0, 0.0);
        Assert.Equal((90, 0), (big.MoveSlowPercent, big.AttackSlowPercent));

        var oneExtra = Set();
        oneExtra.Apply(E(ActiveStatusKind.Slow, 3000, extra: 40), 0, 0.0);
        Assert.Equal((40, 0), (oneExtra.MoveSlowPercent, oneExtra.AttackSlowPercent));

        s.Advance(3000, null);
        Assert.Equal(0, s.MoveSlowPercent);
    }

    [Fact]
    public void ClearDropsEverythingAndNonPositiveTimeDoesNothing()
    {
        var s = Set();
        s.Apply(E(ActiveStatusKind.Stun, 3000), 0, 0.0);
        s.Apply(E(ActiveStatusKind.Poison, 3000, extra: 90), 0, 0.0);
        var ticks = new List<ActiveStatusTick>();
        s.Advance(0, ticks);
        s.Advance(-50, ticks);
        Assert.Empty(ticks);
        Assert.Equal(3000.0, s.Find(ActiveStatusKind.Stun).RemainingMs);
        s.Clear();
        Assert.Equal((0, false), (s.Count, s.ControlLocked));
    }

    [Fact]
    public void AppliedExtrasAreCopiedSoLaterEditsToTheEntryDoNotLeakIn()
    {
        var s = Set();
        ActiveStatusEntry entry = E(ActiveStatusKind.Bleeding, 3000, extra: 120);
        s.Apply(entry, 0, 0.0);
        entry.Extra[0] = 99999;
        Assert.Equal(120, s.Find(ActiveStatusKind.Bleeding).Extra[0]);
    }

    // ---- 规则表与新增公式 ----

    [Fact]
    public void RulesAgreeWithTheServersSyncKindsAndIntervals()
    {
        ActiveStatusTable table = Table(450);
        for (int k = 0; k < (int)ActiveStatusKind.None; k++)
        {
            var kind = (ActiveStatusKind)k;
            Assert.Equal(ActiveStatusFormulas.IsSync(k), ActiveStatusRules.DealsDamage(kind));
            Assert.Equal(ActiveStatusRules.DealsDamage(kind), ActiveStatusRules.TickIntervalMs(table, kind) > 0);
        }
        Assert.Equal(500, ActiveStatusRules.TickIntervalMs(table, ActiveStatusKind.Bleeding));
        Assert.Equal(450, ActiveStatusRules.TickIntervalMs(table, ActiveStatusKind.Lightning));
        Assert.Equal(450, ActiveStatusRules.TickIntervalMs(table, ActiveStatusKind.Stone));
        Assert.Equal(ActiveStatusRules.PoisonBurnTickMs, ActiveStatusRules.TickIntervalMs(table, ActiveStatusKind.Poison));
        Assert.Equal(ActiveStatusRules.PoisonBurnTickMs, ActiveStatusRules.TickIntervalMs(table, ActiveStatusKind.Burn));
        Assert.Equal(ActiveStatusRules.FallbackLightningTickMs, ActiveStatusRules.TickIntervalMs(null, ActiveStatusKind.Lightning));
        Assert.True(ActiveStatusRules.IsBeneficial(ActiveStatusKind.Haste) && ActiveStatusRules.IsBeneficial(ActiveStatusKind.Bless));
        Assert.False(ActiveStatusRules.IsBeneficial(ActiveStatusKind.Stun));
    }

    [Fact]
    public void EvenTickDamageAtFiveHundredIsTheClientBleedingFormula()
    {
        var random = new Random(777);
        for (int n = 0; n < 500; n++)
        {
            var data = new ActiveStatusDamageInput(random.Next(0, 100000), random.Next(0, 60000));
            Assert.Equal(ActiveStatusFormulas.BleedingDamagePerTick(data), ActiveStatusFormulas.EvenTickDamage(data, ActiveStatusFormulas.BleedingTermMs));
        }
        // 间隔不是正数当一跳
        Assert.Equal(900, ActiveStatusFormulas.EvenTickDamage(new ActiveStatusDamageInput(900, 5000), 0));
    }

    [Fact]
    public void ServerAllowedDamageFollowsTheServersDispatch()
    {
        ActiveStatusTable table = Table();
        var data = new ActiveStatusDamageInput(1000, 4000);
        Assert.Equal(1000, ActiveStatusFormulas.ServerAllowedDamage(table, (int)ActiveStatusKind.Poison, data));
        Assert.Equal(1000, ActiveStatusFormulas.ServerAllowedDamage(table, (int)ActiveStatusKind.Lightning, data));
        Assert.Equal(1000, ActiveStatusFormulas.ServerAllowedDamage(table, (int)ActiveStatusKind.Stone, data));
        Assert.Equal(1000, ActiveStatusFormulas.ServerAllowedDamage(table, (int)ActiveStatusKind.Burn, data));
        Assert.Equal(1500, ActiveStatusFormulas.ServerAllowedDamage(table, (int)ActiveStatusKind.Bleeding, data));
        Assert.Equal(0, ActiveStatusFormulas.ServerAllowedDamage(table, (int)ActiveStatusKind.Stun, data));
        Assert.Equal(0, ActiveStatusFormulas.ServerAllowedDamage(table, 99, data));
    }
}
