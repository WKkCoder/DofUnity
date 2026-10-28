using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 异常状态的等级差修正(M43 续,证据 E-136):状态等级(攻击信息槽的第二个数)与受害者等级之差改概率、持续时间与部分附加参数。
/// 公式是客户端 0x008b58d0 与 0x008b6f80 读到的,单精度逐步落 float。数值全是合成的:概率比率 250(读入后每级 2.5%)、威力比率 50(每级 0.5%),
/// 绝对比率各测试自己设;选的等级差(概率相关取 10 的倍数,时长相关取 50 的倍数)让每一步的 float 乘积都正好落在可精确表示的数上,断言可以写精确值。
/// </summary>
public sealed class ActiveStatusLevelTests
{
    private static ActiveStatusTable Table(float prob = 250f, float power = 50f, float absolute = 0f)
    {
        return ActiveStatusTable.Synthetic(11, new int[ActiveStatusTable.KindCount], new float[ActiveStatusTable.KindCount * 2],
            new float[ActiveStatusTable.KindCount * 2], null, 1f, 1f, 0, prob, power, absolute);
    }

    private static ActiveStatusEntry E(ActiveStatusKind kind, int duration = 3000, float probability = 100f, int level = 0, params int[] extra)
    {
        return ActiveStatusEntry.Create(kind, probability, level, duration, extra);
    }

    // ---- 公式 ----

    [Fact]
    public void TheTableRatesAreWholeNumbersScaledByOneHundredthWhenRead()
    {
        Assert.Equal(2.5f, ActiveStatusFormulas.LevelRate(250f));
        Assert.Equal(0.5f, ActiveStatusFormulas.LevelRate(50f));
        Assert.Equal(0f, ActiveStatusFormulas.LevelRate(0f));
        Assert.Equal(2f, ActiveStatusFormulas.LevelRate(200f));
        Assert.Equal(ActiveStatusFormulas.LevelRate(50f), ActiveStatusFormulas.LevelRate(50.9f));    // 客户端按整数读,小数部分丢掉
    }

    [Fact]
    public void ProbabilityRisesForALowerVictimAndFallsForAHigherOneAndStopsAtZero()
    {
        ActiveStatusTable t = Table();
        Assert.Equal(40f, ActiveStatusFormulas.LevelAdjustedProbability(t, 40f, 30, 30));
        Assert.Equal(50f, ActiveStatusFormulas.LevelAdjustedProbability(t, 40f, 30, 20));     // 低 10 级:× 1.25
        Assert.Equal(30f, ActiveStatusFormulas.LevelAdjustedProbability(t, 40f, 30, 40));     // 高 10 级:× 0.75
        Assert.Equal(0f, ActiveStatusFormulas.LevelAdjustedProbability(t, 40f, 30, 70));      // 高 40 级:× 0
        Assert.Equal(0f, ActiveStatusFormulas.LevelAdjustedProbability(t, 40f, 30, 130));     // 再高:不出负数
        Assert.Equal(0f, ActiveStatusFormulas.LevelAdjustedProbability(t, 0f, 30, 20));
    }

    [Fact]
    public void TheAbsoluteRateAddsOrSubtractsPerLevelOfDifference()
    {
        ActiveStatusTable t = Table(prob: 0f, power: 0f, absolute: 200f);                      // 每级 2 个百分点
        Assert.Equal(50f, ActiveStatusFormulas.LevelAdjustedProbability(t, 40f, 10, 5));
        Assert.Equal(30f, ActiveStatusFormulas.LevelAdjustedProbability(t, 40f, 10, 15));
        Assert.Equal(0f, ActiveStatusFormulas.LevelAdjustedProbability(t, 5f, 10, 15));
        Assert.Equal(40f, ActiveStatusFormulas.LevelAdjustedProbability(t, 40f, 10, 10));
    }

    [Fact]
    public void TheDurationOfFreezeStunStoneSleepConfuseAndHoldIsOnlyScaled()
    {
        ActiveStatusTable t = Table(absolute: 200f);                                           // 绝对比率对这几种不起作用
        foreach (ActiveStatusKind kind in new[] { ActiveStatusKind.Freeze, ActiveStatusKind.Stun, ActiveStatusKind.Stone, ActiveStatusKind.Sleep, ActiveStatusKind.Confuse, ActiveStatusKind.Hold })
        {
            int k = (int)kind;
            Assert.Equal(3000, ActiveStatusFormulas.LevelAdjustedDuration(t, k, 3000, 60, 60, 0));
            Assert.Equal(2250, ActiveStatusFormulas.LevelAdjustedDuration(t, k, 3000, 60, 110, 0));   // 高 50 级:× 0.75
            Assert.Equal(3750, ActiveStatusFormulas.LevelAdjustedDuration(t, k, 3000, 60, 10, 0));    // 低 50 级:× 1.25
            Assert.Equal(0, ActiveStatusFormulas.LevelAdjustedDuration(t, k, 3000, 60, 260, 0));      // 高 200 级:× 0
            Assert.Equal(0, ActiveStatusFormulas.LevelAdjustedDuration(t, k, 3000, 60, 360, 0));      // 再高:不出负数
        }
    }

    [Fact]
    public void SlowBlindAndTheTwoBreaksAlsoTakeTheAbsoluteRateAndSlowAndBlindNeedExtras()
    {
        ActiveStatusTable t = Table(absolute: 200f);                                           // 每级差 2 毫秒
        foreach (ActiveStatusKind kind in new[] { ActiveStatusKind.Slow, ActiveStatusKind.Blind })
        {
            int k = (int)kind;
            Assert.Equal(2150, ActiveStatusFormulas.LevelAdjustedDuration(t, k, 3000, 60, 110, 2));   // 2250 − 50 × 2
            Assert.Equal(3850, ActiveStatusFormulas.LevelAdjustedDuration(t, k, 3000, 60, 10, 2));    // 3750 + 50 × 2
            Assert.Equal(3000, ActiveStatusFormulas.LevelAdjustedDuration(t, k, 3000, 60, 110, 0));   // 附加参数为空:整段不修正
        }
        foreach (ActiveStatusKind kind in new[] { ActiveStatusKind.WeaponBreak, ActiveStatusKind.ArmorBreak })
        {
            int k = (int)kind;
            Assert.Equal(2150, ActiveStatusFormulas.LevelAdjustedDuration(t, k, 3000, 60, 110, 1));
            Assert.Equal(3850, ActiveStatusFormulas.LevelAdjustedDuration(t, k, 3000, 60, 10, 1));
            Assert.Equal(2150, ActiveStatusFormulas.LevelAdjustedDuration(t, k, 3000, 60, 110, 0));   // 这两种不看附加参数是否为空
        }
        Assert.Equal(0, ActiveStatusFormulas.LevelAdjustedDuration(Table(absolute: 0f), (int)ActiveStatusKind.Slow, 3000, 60, 360, 2));
    }

    [Fact]
    public void OtherKindsKeepTheirDurationWhateverTheLevelDifference()
    {
        ActiveStatusTable t = Table(absolute: 200f);
        foreach (ActiveStatusKind kind in new[] { ActiveStatusKind.Poison, ActiveStatusKind.Curse, ActiveStatusKind.Lightning, ActiveStatusKind.Burn,
                     ActiveStatusKind.Bleeding, ActiveStatusKind.Haste, ActiveStatusKind.Bless, ActiveStatusKind.Element })
        {
            Assert.Equal(3000, ActiveStatusFormulas.LevelAdjustedDuration(t, (int)kind, 3000, 50, 200, 4));
            Assert.Equal(3000, ActiveStatusFormulas.LevelAdjustedDuration(t, (int)kind, 3000, 50, 1, 4));
        }
    }

    [Fact]
    public void TheExtrasOfSlowBlindAndTheBreaksAreScaledOthersAreLeftAlone()
    {
        ActiveStatusTable t = Table();
        Assert.Equal(new[] { 15, 7 }, ActiveStatusFormulas.LevelAdjustedExtras(t, (int)ActiveStatusKind.Slow, new[] { 20, 10 }, 60, 110));          // × 0.75 向零取整
        Assert.Equal(new[] { 30, 22 }, ActiveStatusFormulas.LevelAdjustedExtras(t, (int)ActiveStatusKind.Blind, new[] { 40, 30 }, 60, 110));
        Assert.Equal(new[] { 25, 12 }, ActiveStatusFormulas.LevelAdjustedExtras(t, (int)ActiveStatusKind.Slow, new[] { 20, 10 }, 60, 10));        // × 1.25
        Assert.Equal(new[] { 75 }, ActiveStatusFormulas.LevelAdjustedExtras(t, (int)ActiveStatusKind.WeaponBreak, new[] { 100 }, 60, 110));
        Assert.Equal(new[] { 6 }, ActiveStatusFormulas.LevelAdjustedExtras(t, (int)ActiveStatusKind.ArmorBreak, new[] { 8 }, 60, 110));
        // 减速只改前两项;失明同
        Assert.Equal(new[] { 15, 7, 99 }, ActiveStatusFormulas.LevelAdjustedExtras(t, (int)ActiveStatusKind.Slow, new[] { 20, 10, 99 }, 60, 110));
        // 武器破坏只改第一项
        Assert.Equal(new[] { 75, 100 }, ActiveStatusFormulas.LevelAdjustedExtras(t, (int)ActiveStatusKind.WeaponBreak, new[] { 100, 100 }, 60, 110));

        int[] poison = { 100, 5 };
        Assert.Same(poison, ActiveStatusFormulas.LevelAdjustedExtras(t, (int)ActiveStatusKind.Poison, poison, 60, 110));
        int[] empty = Array.Empty<int>();
        Assert.Same(empty, ActiveStatusFormulas.LevelAdjustedExtras(t, (int)ActiveStatusKind.Slow, empty, 60, 110));
        Assert.Null(ActiveStatusFormulas.LevelAdjustedExtras(t, (int)ActiveStatusKind.Slow, null, 60, 110));
        // 不改传进来的数组
        int[] original = { 20, 10 };
        ActiveStatusFormulas.LevelAdjustedExtras(t, (int)ActiveStatusKind.Slow, original, 60, 110);
        Assert.Equal(new[] { 20, 10 }, original);
    }

    // ---- 挂到身上 ----

    [Fact]
    public void TheLevelDifferenceChangesTheChanceOfAnEntryWithALevel()
    {
        ActiveStatusEntry forty = E(ActiveStatusKind.Stun, probability: 40f, level: 30);
        Assert.Equal(ActiveStatusApply.Applied, new ActiveStatusSet(Table()).Apply(forty, 30, 0.39));        // 同级:40%
        Assert.Equal(ActiveStatusApply.Missed, new ActiveStatusSet(Table()).Apply(forty, 30, 0.41));
        Assert.Equal(ActiveStatusApply.Applied, new ActiveStatusSet(Table()).Apply(forty, 40, 0.29));        // 高 10 级:30%
        Assert.Equal(ActiveStatusApply.Missed, new ActiveStatusSet(Table()).Apply(forty, 40, 0.31));
        Assert.Equal(ActiveStatusApply.Applied, new ActiveStatusSet(Table()).Apply(forty, 20, 0.49));        // 低 10 级:50%
        Assert.Equal(ActiveStatusApply.Missed, new ActiveStatusSet(Table()).Apply(forty, 20, 0.51));
        var never = new ActiveStatusSet(Table());
        Assert.Equal(ActiveStatusApply.Missed, never.Apply(forty, 70, 0.0));                                 // 高 40 级:一次也不中
        Assert.Equal((0, 1), (never.Count, never.MissedCount));
    }

    [Fact]
    public void ALevelOfZeroOrAnUnknownVictimLevelIsNotAdjusted()
    {
        ActiveStatusEntry zeroLevel = E(ActiveStatusKind.Stun, probability: 40f, level: 0);
        Assert.Equal(ActiveStatusApply.Applied, new ActiveStatusSet(Table()).Apply(zeroLevel, 90, 0.39));    // 状态等级 0:不修正,按 40%
        Assert.Equal(ActiveStatusApply.Missed, new ActiveStatusSet(Table()).Apply(zeroLevel, 90, 0.41));
        var sixty = new ActiveStatusSet(Table());
        sixty.Apply(E(ActiveStatusKind.Stun, probability: 100f, level: 0), 90, 0.0);
        Assert.Equal(3000, sixty.Find(ActiveStatusKind.Stun).DurationMs);                                     // 时长也不修正

        ActiveStatusEntry leveled = E(ActiveStatusKind.Stun, probability: 40f, level: 10);
        Assert.Equal(ActiveStatusApply.Applied, new ActiveStatusSet(Table()).Apply(leveled, 0, 0.39));       // 受害者等级不知道(0):不修正
        Assert.Equal(ActiveStatusApply.Missed, new ActiveStatusSet(Table()).Apply(leveled, -3, 0.41));
    }

    [Fact]
    public void ACertainProbabilityAlwaysHitsEvenWhenTheVictimIsFarAboveTheLevel()
    {
        var s = new ActiveStatusSet(Table());
        ActiveStatusEntry certain = E(ActiveStatusKind.Stun, probability: ActiveStatusFormulas.CertainProbability, level: 10);
        Assert.Equal(ActiveStatusApply.Applied, s.Apply(certain, 100, 0.9999999));
        Assert.InRange(s.Find(ActiveStatusKind.Stun).DurationMs, 1, 2999);                                    // 概率不修正,时长照样按等级差缩短
        // 普通的 100 就不一样:同样的等级差把概率压到 0
        Assert.Equal(ActiveStatusApply.Missed, new ActiveStatusSet(Table()).Apply(E(ActiveStatusKind.Stun, probability: 100f, level: 10), 100, 0.0));
    }

    [Fact]
    public void HasteAndBlessAreNotAdjustedInProbability()
    {
        foreach (ActiveStatusKind kind in new[] { ActiveStatusKind.Haste, ActiveStatusKind.Bless })
        {
            var s = new ActiveStatusSet(Table());
            Assert.Equal(ActiveStatusApply.Applied, s.Apply(E(kind, probability: 40f, level: 10, extra: new[] { 1, 2, 3, 4 }), 100, 0.39));
            Assert.Equal(3000, s.Find(kind).DurationMs);
        }
    }

    [Fact]
    public void TheAdjustedDurationAndExtrasLandOnTheInstance()
    {
        var s = new ActiveStatusSet(Table(prob: 0f));                                                        // 概率比率设 0:只看时长与附加参数
        Assert.Equal(ActiveStatusApply.Applied, s.Apply(E(ActiveStatusKind.Slow, level: 60, extra: new[] { 20, 10 }), 110, 0.0));
        ActiveStatusInstance slow = s.Find(ActiveStatusKind.Slow);
        Assert.Equal(2250, slow.DurationMs);
        Assert.Equal(new[] { 15, 7 }, slow.Extra);
        Assert.Equal(15, s.MoveSlowPercent);

        var src = E(ActiveStatusKind.Slow, level: 60, extra: new[] { 20, 10 });
        new ActiveStatusSet(Table(prob: 0f)).Apply(src, 110, 0.0);
        Assert.Equal(new[] { 20, 10 }, src.Extra);                                                            // 攻击信息里的原数组不被改
    }

    [Fact]
    public void ADurationAdjustedToNothingIsIgnoredAfterTheRoll()
    {
        var s = new ActiveStatusSet(Table(prob: 0f));                                                        // 概率不受等级差影响,时长受
        Assert.Equal(ActiveStatusApply.Ignored, s.Apply(E(ActiveStatusKind.Stun, level: 60), 260, 0.0));
        Assert.Equal((0, 0, 0), (s.Count, s.MissedCount, s.AppliedCount));
        Assert.Equal(ActiveStatusApply.Applied, s.Apply(E(ActiveStatusKind.Stun, level: 60), 110, 0.0));     // 低一点就有
    }
}
