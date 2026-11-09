namespace Dnf.Play.Tests;

/// <summary>
/// 抓取类技能"已抓住"那一半的阶段机 <see cref="GrabRun"/>:100 毫秒起算、命中计时器(第一次立刻响、从此刻重数、次数用完收尾)、
/// 阶段 1 到 750 毫秒释放、阶段 2 到总时长结束、出血记录。总时长、间隔、次数都是合成的小数,期望值全是手算(每条用例旁边写了算式)。
/// </summary>
public sealed class GrabRunTests
{
    private static GrabStep Go(GrabRun run, int ms, bool present = true, bool alive = true, bool bleeding = false, bool controlled = true)
        => run.Step(ms, present, alive, bleeding, controlled);

    // ------------------------------------------------------------------ 起算与命中计时器

    [Fact]
    public void NothingHappensBeforeTheHoldStartAndTheFirstTickFiresRightAtIt()
    {
        var run = new GrabRun(durationMs: 1000, tickIntervalMs: 200, tickCount: 3, multiHit: true);

        var early = Go(run, 99);
        Assert.Equal((false, 0, 0), (early.Hit, run.Phase, run.Ticks));          // 99 < 100:什么也不发生

        var start = Go(run, 100);
        // 起点算在 0 之前一个间隔:上次到点 = 0 - 200 = -200,100 ≥ -200 + 200 = 0,所以 100 这一帧就到点
        Assert.Equal((true, 0, 1), (start.Hit, run.Phase, run.Ticks));
    }

    [Fact]
    public void EachTickCountsTheNextIntervalFromTheMomentItActuallyFired()
    {
        var run = new GrabRun(1000, 200, 3, true);

        Assert.True(Go(run, 130).Hit);                  // 第一次在 130 到点(帧来得晚):上次到点 = 130
        Assert.False(Go(run, 300).Hit);                 // 若按原计划 100、300、500 这一帧该响;实际要等 130 + 200 = 330
        Assert.False(Go(run, 329).Hit);                 // 329 < 330
        Assert.True(Go(run, 330).Hit);                  // 330 ≥ 330:第二次到点,上次到点 = 330
        Assert.False(Go(run, 529).Hit);                 // 529 < 330 + 200 = 530
        Assert.True(Go(run, 530).Hit);                  // 第三次
        Assert.Equal(3, run.Ticks);
    }

    [Fact]
    public void WhenTheTickCountRunsOutTheNextFrameEndsTheCarryWithoutAHit()
    {
        var run = new GrabRun(1000, 200, 2, true);

        Assert.True(Go(run, 100).Hit);                  // 第一次
        var second = Go(run, 300);                      // 100 + 200 = 300:第二次,次数用完
        Assert.Equal((true, 0, 2, true), (second.Hit, run.Phase, run.Ticks, run.TicksExhausted));

        var after = Go(run, 301);                       // 用完之后的下一帧:不再响,进阶段 1
        Assert.Equal((false, 1), (after.Hit, run.Phase));
    }

    [Fact]
    public void TickCountOfOneGivesExactlyOneHitAndZeroGivesNone()
    {
        var one = new GrabRun(1000, 200, 1, true);
        Assert.True(Go(one, 100).Hit);                  // 次数 1:第一次响完就用完
        Assert.Equal(0, one.Phase);                     // 这一帧还在阶段 0
        Assert.Equal((false, 1), (Go(one, 101).Hit, one.Phase));

        var zero = new GrabRun(1000, 200, 0, true);
        var step = Go(zero, 100);                       // 次数 0:到时第一帧就是"已用完",一下都没有
        Assert.Equal((false, 1, 0), (step.Hit, zero.Phase, zero.Ticks));
    }

    [Fact]
    public void MinusOneTickCountNeverRunsOut()
    {
        var run = new GrabRun(5000, 100, -1, true);

        for (int ms = 100; ms <= 2000; ms += 100)       // 100、200、……、2000 共 20 个点,每个点都是上一次响后正好一个间隔
        {
            Assert.True(Go(run, ms).Hit);
        }
        Assert.Equal((20, 0, false), (run.Ticks, run.Phase, run.TicksExhausted));
    }

    // ------------------------------------------------------------------ 单次命中

    [Fact]
    public void SingleHitModeHitsOnceAtTheHoldStartAndMovesToThePhaseOfWaiting()
    {
        var run = new GrabRun(1000, 200, 3, false);

        Assert.Equal((false, 0), (Go(run, 99).Hit, run.Phase));       // 99 < 100:不动
        Assert.Equal((true, 1), (Go(run, 100).Hit, run.Phase));       // 100:打一次,进阶段 1
        Assert.Equal((false, 1), (Go(run, 101).Hit, run.Phase));      // 之后不再打
    }

    [Fact]
    public void ADeadOrUncontrolledTargetGetsNoHitButTheSingleChanceIsStillUsedUp()
    {
        var dead = new GrabRun(1000, 200, 3, false);
        Assert.Equal((false, 1), (Go(dead, 100, alive: false).Hit, dead.Phase));

        var remote = new GrabRun(1000, 200, 3, false);
        Assert.Equal((false, 1), (Go(remote, 100, controlled: false).Hit, remote.Phase));
    }

    // ------------------------------------------------------------------ 目标没了、死了、不归本机

    [Fact]
    public void WithoutATargetTheCarryEndsAtTheHoldStartInBothModesButNotBefore()
    {
        var multi = new GrabRun(1000, 200, 3, true);
        Assert.Equal((false, 0), (Go(multi, 99, present: false).Hit, multi.Phase));     // 99 < 100:还没到起算,目标没了也不收尾
        Assert.Equal((false, 1, 0), (Go(multi, 100, present: false).Hit, multi.Phase, multi.Ticks));

        var single = new GrabRun(1000, 200, 3, false);
        Assert.Equal((false, 1), (Go(single, 100, present: false).Hit, single.Phase));
    }

    [Fact]
    public void ATargetThatDisappearsMidwayEndsTheCarryOnTheNextFrame()
    {
        var run = new GrabRun(1000, 200, -1, true);

        Assert.True(Go(run, 100).Hit);
        var gone = Go(run, 150, present: false);        // 150 ≥ 100 且目标没了
        Assert.Equal((false, 1), (gone.Hit, run.Phase));
    }

    [Fact]
    public void ADeadOrUncontrolledTargetStillUsesUpATickInMultiHitMode()
    {
        var run = new GrabRun(1000, 200, 3, true);

        var first = Go(run, 100, alive: false);         // 目标死了:到点计数,但不真打
        Assert.Equal((false, 1), (first.Hit, run.Ticks));
        var second = Go(run, 300, controlled: false);   // 不归本机控制:同样到点计数不真打
        Assert.Equal((false, 2), (second.Hit, run.Ticks));
        var third = Go(run, 500);                       // 300 + 200 = 500:活着且本机控制,真打
        Assert.Equal((true, 3), (third.Hit, run.Ticks));
    }

    // ------------------------------------------------------------------ 释放与结束

    [Fact]
    public void ReleaseNeedsPhaseOneAndSevenHundredFiftyMillisecondsAndHappensOnce()
    {
        var run = new GrabRun(2000, 200, 1, true);
        Assert.True(Go(run, 100).Hit);                  // 次数 1,一下就用完
        Go(run, 101);                                   // 下一帧进阶段 1
        Assert.Equal(1, run.Phase);

        Assert.False(Go(run, 749).Release);             // 749 < 750
        var release = Go(run, 750);                     // 750 ≥ 750
        Assert.Equal((true, 2), (release.Release, run.Phase));
        Assert.False(Go(run, 751).Release);             // 释放只一次
    }

    [Fact]
    public void ThereIsNoReleaseWhileStillCarryingEvenPastSevenHundredFifty()
    {
        var run = new GrabRun(2000, 200, -1, true);

        Assert.True(Go(run, 100).Hit);
        var late = Go(run, 800);                        // 阶段 0:800 ≥ 750 也不释放(上次响在 100,800 ≥ 300,这一帧又响一次)
        Assert.Equal((true, false, 0), (late.Hit, late.Release, run.Phase));
    }

    [Fact]
    public void FinishNeedsPhaseTwoAndTheFullDuration()
    {
        var run = new GrabRun(1000, 200, 1, true);
        Go(run, 100);
        Go(run, 101);
        var release = Go(run, 750);
        Assert.Equal((true, false), (release.Release, release.Finished));   // 阶段 2 了,但 750 < 1000

        Assert.False(Go(run, 999).Finished);                                // 999 < 1000
        Assert.True(Go(run, 1000).Finished);                                // 1000 ≥ 1000
        Assert.True(run.Finished);
    }

    [Fact]
    public void ADurationShorterThanTheReleaseDelayFinishesInTheVeryFrameOfTheRelease()
    {
        var run = new GrabRun(500, 200, 1, true);
        Go(run, 100);
        Go(run, 101);

        var step = Go(run, 750);                        // 先释放进阶段 2,同一帧守卫看到 750 ≥ 500 就结束
        Assert.Equal((true, true), (step.Release, step.Finished));
    }

    [Fact]
    public void BeingPastTheDurationDoesNotFinishBeforePhaseTwo()
    {
        var run = new GrabRun(0, 200, -1, true);        // 总时长 0:到阶段 2 才有资格结束

        Assert.False(Go(run, 50).Finished);             // 阶段 0
        Assert.True(Go(run, 100).Hit);                  // 还在带着走(次数不限)
        Assert.False(run.Finished);
        Assert.Equal(0, run.Phase);
    }

    [Fact]
    public void PhaseOneNeverFinishesEvenPastTheDurationUntilTheReleaseHappens()
    {
        var run = new GrabRun(300, 200, 1, true);       // 总时长 300 比释放延迟 750 短
        Assert.True(Go(run, 100).Hit);                  // 次数 1:一下就用完
        Go(run, 101);                                   // 下一帧进阶段 1
        Assert.Equal(1, run.Phase);

        var atDuration = Go(run, 300);                  // 阶段 1:300 ≥ 总时长 300,但不到阶段 2 不结束;300 < 750 也不释放
        Assert.Equal((false, false, 1), (atDuration.Release, atDuration.Finished, run.Phase));
        var beforeRelease = Go(run, 749);               // 749 < 750:同上
        Assert.Equal((false, false, 1), (beforeRelease.Release, beforeRelease.Finished, run.Phase));

        var release = Go(run, 750);                     // 750:先释放进阶段 2,同一帧 750 ≥ 300 就结束
        Assert.Equal((true, true, 2), (release.Release, release.Finished, run.Phase));
    }

    [Fact]
    public void AFinishedRunIgnoresFurtherSteps()
    {
        var run = new GrabRun(500, 200, 1, true);
        Go(run, 100);
        Go(run, 101);
        Assert.True(Go(run, 750).Finished);

        var after = Go(run, 5000, bleeding: true);
        Assert.Equal((false, false, true, 2), (after.Hit, after.Release, after.Finished, run.Phase));
        Assert.False(run.BleedSeen);                    // 结束之后什么都不再记
    }

    // ------------------------------------------------------------------ 出血记录

    [Fact]
    public void BleedingSeenIsStickyAndAlsoRecordedBeforeTheHoldStart()
    {
        var run = new GrabRun(1000, 200, 3, true);

        Go(run, 10, bleeding: true);                    // 10 < 100:命中不算,出血记录照常更新
        Assert.True(run.BleedSeen);
        Go(run, 20, bleeding: false);                   // 之后不带出血了,记录不撤
        Assert.True(run.BleedSeen);
    }

    [Fact]
    public void BleedingIsOnlyRecordedWhileATargetIsPresentAndOnlyInPhaseZero()
    {
        var noTarget = new GrabRun(1000, 200, 3, true);
        Go(noTarget, 10, present: false, bleeding: true);
        Assert.False(noTarget.BleedSeen);               // 目标没了:不记

        var phaseOne = new GrabRun(1000, 200, 3, true);
        Go(phaseOne, 100, present: false);              // 进阶段 1
        Go(phaseOne, 200, present: true, bleeding: true);
        Assert.False(phaseOne.BleedSeen);               // 阶段 1 的帧不更新
    }
}
