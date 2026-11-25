using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 最短清图时间(<see cref="OnlineFightOptions.MinClearDuration"/>)与 <see cref="OnlineFightSummary.ClearDuration"/> 测试:
/// 全部用手拨假时钟(<see cref="ManualFightClock"/>)验证击杀摊开、下限守卫、取更慢者与边界值(N=0/1/2/3/4, S=0/1/3600),
/// 以及未设置时与既有输出逐字一致。
/// </summary>
public class OnlineFightPacingTests
{
    [Fact]
    public void MinClearDuration_defaults_to_null_and_rejects_negative_or_overflow()
    {
        var o = new OnlineFightOptions();
        Assert.Null(o.MinClearDuration);

        var session = new CombatFakeSession();
        var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options());
        StartMapNotice map = CombatKit.Map(2);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineFightDriver(runner, map, new OnlineFightOptions { MinClearDuration = TimeSpan.FromMilliseconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineFightDriver(runner, map, new OnlineFightOptions { MinClearDuration = TimeSpan.FromMilliseconds((double)int.MaxValue + 1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OnlineFightDriver(runner, map, new OnlineFightOptions { MinClearDuration = TimeSpan.FromDays(40) }));

        // int.MaxValue 毫秒与 TimeSpan.Zero 均合法
        Assert.NotNull(new OnlineFightDriver(runner, map, new OnlineFightOptions { MinClearDuration = TimeSpan.FromMilliseconds(int.MaxValue) }));
        Assert.NotNull(new OnlineFightDriver(runner, map, new OnlineFightOptions { MinClearDuration = TimeSpan.Zero }));
    }

    [Fact]
    public async Task Null_and_zero_MinClearDuration_preserve_exact_legacy_output_and_pacing()
    {
        foreach (TimeSpan? dur in new TimeSpan?[] { null, TimeSpan.Zero })
        {
            var rig = new FightRig(3, paceMs: 400, minClearDuration: dur);
            Task<OnlineFightSummary> run = await rig.Start();
            await rig.PostKills(3);
            OnlineFightSummary s = await rig.Pump(run);

            Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
            Assert.Equal(new[] { 0, 400, 800 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray(), s.PostTimes.ToArray());
            string[] own = rig.Log.Lines.Where(l => l.StartsWith(OnlineFightLog.Prefix)).ToArray();
            Assert.Equal(
                new[]
                {
                    OnlineFightLog.Prefix + " begin monsters=3 paceMs=400 clearWaitMs=25000 settleWaitMs=25000 continueOnRefuse=0",
                    OnlineFightLog.Prefix + " phase=kills posted=0",
                    OnlineFightLog.Prefix + " phase=clearWait posted=3",
                    OnlineFightLog.Prefix + " phase=settleWait posted=3",
                    OnlineFightLog.Prefix + " end outcome=AllDone monsters=3 sent=3 confirmed=3 refused=0 blocked=0 clearNotice=1 settlement=complete returnReady=1 posted=3 pending=0 heartbeats=0 runner=ReturnReady",
                },
                own);
        }
    }

    [Fact]
    public async Task MinClearDuration_spreads_kills_evenly_using_ceiling_when_slower_than_paceMs()
    {
        // N = 4 (3 个间隔), PaceMs = 400, MinClearDuration = 10s -> ceil(10000 / 3) = 3334ms
        var rig = new FightRig(4, paceMs: 400, minClearDuration: TimeSpan.FromSeconds(10));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(4);

        OnlineFightSummary s = await rig.Pump(run);

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Equal(
            new[] { 0, 3334, 6668, 10002 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray(),
            s.PostTimes.ToArray());
        Assert.Equal(
            Enumerable.Repeat(TimeSpan.FromMilliseconds(3334), 3).ToArray(),
            rig.Clock.Requests.Take(3).ToArray());
        Assert.True(s.PostTimes[3] - s.PostTimes[0] >= TimeSpan.FromSeconds(10));
        Assert.True(s.ClearDuration >= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task MinClearDuration_with_exact_division_and_zero_paceMs_hits_target_exactly()
    {
        // N = 3 (2 个间隔), PaceMs = 0, MinClearDuration = 1s (S=1 边界) -> 500ms 每段
        var rig = new FightRig(3, paceMs: 0, minClearDuration: TimeSpan.FromSeconds(1));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(3);

        OnlineFightSummary s = await rig.Pump(run);

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Equal(
            new[] { 0, 500, 1000 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray(),
            s.PostTimes.ToArray());
        Assert.Equal(TimeSpan.FromMilliseconds(1000), s.PostTimes[2] - s.PostTimes[0]);
        Assert.DoesNotContain(TimeSpan.Zero, rig.Clock.Requests);
    }

    [Fact]
    public async Task Pace_of_one_millisecond_still_waits_one_millisecond_between_kills()
    {
        var rig = new FightRig(3, paceMs: 1);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(3);

        OnlineFightSummary s = await rig.Pump(run);

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Equal(new[] { 0, 1, 2 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray(), s.PostTimes.ToArray());
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1) }, rig.Clock.Requests.Take(2).ToArray());
    }

    [Fact]
    public async Task MinClearDuration_smaller_than_existing_pace_adds_no_extra_wait()
    {
        // N = 4, PaceMs = 400 -> 现有节奏耗时 1200ms >= 1000ms (S=1), 不额外等
        var rig = new FightRig(4, paceMs: 400, minClearDuration: TimeSpan.FromSeconds(1));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(4);

        OnlineFightSummary s = await rig.Pump(run);

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Equal(
            new[] { 0, 400, 800, 1200 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray(),
            s.PostTimes.ToArray());
        Assert.Equal(
            Enumerable.Repeat(TimeSpan.FromMilliseconds(400), 3).ToArray(),
            rig.Clock.Requests.Take(3).ToArray());
    }

    [Fact]
    public async Task Slower_paceMs_wins_over_MinClearDuration()
    {
        // N = 4, PaceMs = 4500, MinClearDuration = 10s (需 3334ms) -> 取更慢者 4500ms (避开 FinishWait=5000ms 过滤)
        var rig = new FightRig(4, paceMs: 4500, minClearDuration: TimeSpan.FromSeconds(10));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(4);

        OnlineFightSummary s = await rig.Pump(run);

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Equal(
            new[] { 0, 4500, 9000, 13500 }.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray(),
            s.PostTimes.ToArray());
    }

    [Fact]
    public async Task Max_boundary_3600_seconds_is_honoured_on_two_monsters()
    {
        // S = 3600s 上界, N = 2 -> 第一只立刻发, 第二只在 3600s 发
        var rig = new FightRig(2, paceMs: 400, minClearDuration: TimeSpan.FromSeconds(3600));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(2);

        OnlineFightSummary s = await rig.Pump(run);

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(3600) }, s.PostTimes.ToArray());
        Assert.True(s.ClearDuration >= TimeSpan.FromSeconds(3600));
    }

    [Fact]
    public async Task Single_monster_posts_immediately_even_when_MinClearDuration_exceeds_clock_now()
    {
        // N == 1 时没有"第一只到最后一只"的间隔, 即使 MinClearDuration > Clock.Now 也必须立即发(不挂在最后一只下限等待上)
        var rig = new FightRig(1, paceMs: 400, minClearDuration: TimeSpan.FromSeconds(2000));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(1);

        OnlineFightSummary s = await rig.Pump(run);

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Equal(new[] { TimeSpan.Zero }, s.PostTimes.ToArray());
        Assert.True(s.ClearDuration < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Zero_monsters_still_goes_directly_to_clearWait_with_MinClearDuration()
    {
        var rig = new FightRig(0, paceMs: 400, minClearDuration: TimeSpan.FromSeconds(10));
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.WaitParked();

        rig.Session.Push(rig.Server.ReleaseClear());
        OnlineFightSummary s = await rig.Pump(run);

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Empty(s.PostTimes);
    }

    [Fact]
    public async Task ClearDuration_tracks_clear_observation_separately_from_settleWait_elapsed()
    {
        var rig = new FightRig(2, paceMs: 0, minClearDuration: TimeSpan.FromSeconds(2));
        rig.Session.Respond = (cls, id, payload) => id == 49 ? null : rig.Server.Handle(cls, id, payload);
        Task<OnlineFightSummary> run = await rig.Start();
        await rig.PostKills(2);
        await rig.PumpUntil(() => rig.Log.Lines.Any(l => l.Contains("phase=settleWait")));
        await rig.WaitParked();

        // 在等结算阶段推进 25 秒超时
        rig.Clock.Advance(TimeSpan.FromSeconds(26));
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineFightOutcome.SettleIncomplete, s.Outcome);
        Assert.True(s.ClearDuration >= TimeSpan.FromSeconds(2));
        Assert.True(s.ClearDuration < TimeSpan.FromSeconds(3));
        Assert.True(s.Elapsed >= TimeSpan.FromSeconds(28));
    }

    [Fact]
    public async Task Final_kill_floor_guard_waits_remaining_time_if_clock_woke_early()
    {
        // 用自定义时钟在第一次 500ms 间隔时只推进 480ms(少推 20ms): 驱动在发最后一只(第 1 只)前必须再等足剩下的 20ms
        var inner = new ManualFightClock();
        var clock = new ShortStepClock(inner, shortFirstPaceByMs: 20);
        int[] ids = CombatKit.Sequential(2);
        var server = new FakeCombatServer(ids);
        var session = CombatFakeSession.ForServer(server);
        var runner = new OnlineDungeonCombatRunner(session, null, CombatKit.Options(overallSeconds: 60));
        var driver = new OnlineFightDriver(runner, CombatKit.Map(ids), new OnlineFightOptions
        {
            PaceMs = 0,
            MinClearDuration = TimeSpan.FromMilliseconds(500),
            Clock = clock,
        });

        Task<OnlineFightSummary> run = Task.Run(() => driver.RunAsync());
        await CombatKit.WaitUntil(() => runner.Machine != null && runner.Machine.Confirmed >= 1, "第 0 只已确认");
        await CombatKit.WaitUntil(() => inner.HasPending(FightRig.NotFinishWait), "停在间隔等待");
        inner.AdvanceToNext(FightRig.NotFinishWait); // 只推进了 480ms

        // 此时第 1 只还没发: 驱动停在 20ms 兜底等待上
        await CombatKit.WaitUntil(() => inner.HasPending(d => d == TimeSpan.FromMilliseconds(20)), "停在最后一只的 20ms 下限守卫上");
        Assert.Equal(1, runner.Machine.Sent);

        inner.AdvanceToNext(FightRig.NotFinishWait);
        while (!run.IsCompleted)
        {
            inner.AdvanceToNext(FightRig.NotFinishWait);
            await Task.Delay(1);
        }
        OnlineFightSummary s = await run;

        Assert.Equal(OnlineFightOutcome.AllDone, s.Outcome);
        Assert.Equal(new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(500) }, s.PostTimes.ToArray());
        Assert.Contains(TimeSpan.FromMilliseconds(20), inner.Requests);
    }

    [Fact]
    public async Task Final_kill_floor_guard_aborts_if_map_clears_or_kill_refused_during_remaining_wait()
    {
        // 1. 下限守卫等待期间服务端发来清图(仅 1 只怪, 结算命令被门挡在 Cleared 阶段): 不再发第 2 只, 清图时间被记下
        var inner1 = new ManualFightClock();
        var clock1 = new ShortStepClock(inner1, shortFirstPaceByMs: 20);
        int[] ids = CombatKit.Sequential(2);
        var server1 = new FakeCombatServer(new[] { ids[0] }) { WithholdClear = true };
        var session1 = CombatFakeSession.ForServer(server1);
        var held = new HeldSendSession(session1, 49);
        var runner1 = new OnlineDungeonCombatRunner(held, null, CombatKit.Options(overallSeconds: 60));
        var driver1 = new OnlineFightDriver(runner1, CombatKit.Map(ids), new OnlineFightOptions
        {
            PaceMs = 0,
            MinClearDuration = TimeSpan.FromMilliseconds(500),
            Clock = clock1,
        });
        Task<OnlineFightSummary> run1 = Task.Run(() => driver1.RunAsync());
        await CombatKit.WaitUntil(() => runner1.Machine != null && runner1.Machine.Confirmed >= 1, "第 0 只已确认");
        await CombatKit.WaitUntil(() => inner1.HasPending(FightRig.NotFinishWait), "停在间隔等待");
        inner1.AdvanceToNext(FightRig.NotFinishWait);
        await CombatKit.WaitUntil(() => inner1.HasPending(d => d == TimeSpan.FromMilliseconds(20)), "停在 20ms 下限守卫");
        session1.Push(server1.ReleaseClear());
        await CombatKit.WaitUntil(() => runner1.Machine.Phase == OnlineCombatPhase.Cleared, "等待期间进入 Cleared");
        inner1.AdvanceToNext(FightRig.NotFinishWait);
        await CombatKit.WaitUntil(() => inner1.HasPending(d => d == TimeSpan.FromMilliseconds(50)), "跳过第 2 只并进入等结算");
        Assert.Equal(1, runner1.Machine.Sent);
        inner1.Advance(TimeSpan.FromSeconds(1)); // 在 settleWait 中推 1 秒,验证 ClearDuration 仍钉在 500ms
        held.Release();
        while (!run1.IsCompleted)
        {
            inner1.AdvanceToNext(FightRig.NotFinishWait);
            await Task.Delay(1);
        }
        OnlineFightSummary s1 = await run1;
        Assert.Equal(OnlineFightOutcome.AllDone, s1.Outcome);
        Assert.Equal(1, s1.Posted);
        Assert.Equal(TimeSpan.FromMilliseconds(500), s1.ClearDuration);
        Assert.True(s1.Elapsed >= TimeSpan.FromMilliseconds(1500));

        // 2. 下限守卫等待期间发现第 0 只被拒(缺省即停): 立即 RefusedStop, 不发第 1 只
        var inner2 = new ManualFightClock();
        var clock2 = new ShortStepClock(inner2, shortFirstPaceByMs: 20);
        var server2 = new FakeCombatServer(new[] { ids[0] }) { WithholdClear = true };
        var session2 = CombatFakeSession.ForServer(server2);
        session2.Respond = (_, _, _) => null; // 先不回击杀回复, 等停在 20ms 下限守卫后再推拒绝
        var runner2 = new OnlineDungeonCombatRunner(session2, null, CombatKit.Options(overallSeconds: 60));
        var driver2 = new OnlineFightDriver(runner2, CombatKit.Map(ids), new OnlineFightOptions
        {
            PaceMs = 0,
            MinClearDuration = TimeSpan.FromMilliseconds(500),
            Clock = clock2,
        });
        Task<OnlineFightSummary> run2 = Task.Run(() => driver2.RunAsync());
        await CombatKit.WaitUntil(() => runner2.Machine != null && runner2.Machine.Sent >= 1, "第 0 只已发");
        await CombatKit.WaitUntil(() => inner2.HasPending(FightRig.NotFinishWait), "停在间隔等待");
        inner2.AdvanceToNext(FightRig.NotFinishWait);
        await CombatKit.WaitUntil(() => inner2.HasPending(d => d == TimeSpan.FromMilliseconds(20)), "停在 20ms 下限守卫");
        session2.Push(CombatKit.Refusal(ids[0]));
        await CombatKit.WaitUntil(() => runner2.Machine.Refused >= 1, "等待期间第 0 只被拒");
        inner2.AdvanceToNext(FightRig.NotFinishWait);
        OnlineFightSummary s2 = await GatewayTestSupport.Bounded(run2, 10);
        Assert.Equal(OnlineFightOutcome.RefusedStop, s2.Outcome);
        Assert.Equal(1, s2.Posted);
    }

    private sealed class ShortStepClock : IFightClock
    {
        private readonly ManualFightClock _inner;
        private readonly int _shortByMs;
        private int _calls;

        public ShortStepClock(ManualFightClock inner, int shortFirstPaceByMs)
        {
            _inner = inner;
            _shortByMs = shortFirstPaceByMs;
        }

        public TimeSpan Now => _inner.Now;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1 && delay.TotalMilliseconds > _shortByMs)
            {
                return _inner.DelayAsync(delay - TimeSpan.FromMilliseconds(_shortByMs), cancellationToken);
            }
            return _inner.DelayAsync(delay, cancellationToken);
        }
    }
}
