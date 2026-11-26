using System;
using System.Collections.Generic;
using System.Linq;
using Dnf.Render;
using Xunit;

namespace Dnf.Play.Tests;

/// <summary>
/// r20-e2e：<see cref="HostedRoomBot.MinClearTimeMs"/>（最短清图时间）的参数校验、缺省逐字不变性、
/// 逻辑时钟边界（0、1、上限、上限 ±1）、超 60 秒等待不误触 <see cref="HostedBotStop.NoProgress"/> 与多房间仅末房生效。
/// 全部使用合成房间与假逻辑时钟，不碰墙钟、不连网。
/// </summary>
public sealed class HostedRoomBotMinClearTimeTests
{
    private const double FrameMs = 1000.0 / 60.0;

    private static int Drive(HostedRoomBot bot, HostedRoomRun run, Func<bool> until, string what, double frameMs = FrameMs, int maxSeconds = 200)
    {
        int frames = 0;
        int max = (int)(maxSeconds * 1000.0 / frameMs);
        while (!until() && frames < max)
        {
            bot.Advance(run, frameMs);
            frames++;
        }
        Assert.True(until(), what + " (" + maxSeconds + "s 内未成立; " + bot.LogLine() + ")");
        return frames;
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(-1.0)]
    [InlineData(-60000.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void MinClearTimeMs_rejects_negative_NaN_and_infinity(double bad)
    {
        var bot = new HostedRoomBot();
        Assert.Throws<ArgumentOutOfRangeException>(() => bot.MinClearTimeMs = bad);
        Assert.Equal(0.0, bot.MinClearTimeMs);
    }

    [Fact]
    public void MinClearTimeMs_default_zero_is_verbatim_identical_to_baseline()
    {
        var monsters = new[] { (340, 100), (420, 110) };

        using var h1 = new HostedRoomBotTests.Hosted(monsters, settleAfter: 2);
        using var h2 = new HostedRoomBotTests.Hosted(monsters, settleAfter: 2);

        var baseline = new HostedRoomBot();
        var explicitZero = new HostedRoomBot { MinClearTimeMs = 0.0 };

        Drive(baseline, h1.Run, () => baseline.Reason == HostedBotStop.Settled, "baseline settled");
        Drive(explicitZero, h2.Run, () => explicitZero.Reason == HostedBotStop.Settled, "explicit zero settled");

        Assert.Equal(baseline.Steps, explicitZero.Steps);
        Assert.Equal(baseline.ElapsedMs, explicitZero.ElapsedMs);
        Assert.Equal(baseline.Reason, explicitZero.Reason);
        Assert.Equal(baseline.LogLine(), explicitZero.LogLine());
        Assert.Equal(h1.Posted, h2.Posted);
    }

    [Fact]
    public void MinClearTimeMs_small_positive_40ms_holds_before_threshold_and_releases_at_exact_limit()
    {
        // 用 IEEE-754 精确整数毫秒 20+20==40ms 与远处静止怪（每拍恒输出 dirX=+1），
        // 同时钉住 >0 生效（杀 >100 变异）与 < vs <=（在恰好 ElapsedMs == limitMs 时立刻放行移动）
        const double limitMs = 40.0;
        using var h = new HostedRoomBotTests.Hosted(new[] { (700, 100) }, hp: 40, settleAfter: 1, monsterSpeed: 0);
        var bot = new HostedRoomBot { MinClearTimeMs = limitMs };
        double startX = h.Room.Hero.X;

        // 第 1 步 ElapsedMs == 20.0 < 40.0：必须压制，不得移动也不得出手
        bot.Advance(h.Run, 20.0);
        Assert.Equal(20.0, bot.ElapsedMs);
        Assert.Equal(startX, h.Room.Hero.X, 6);
        Assert.False(h.Room.Hero.IsAttacking);

        // 第 2 步恰好到达 ElapsedMs == 40.0 == limitMs：必须立刻解除压制并向右走近目标
        bot.Advance(h.Run, 20.0);
        Assert.Equal(limitMs, bot.ElapsedMs);
        Assert.True(h.Room.Hero.X > startX, "恰好到达 ElapsedMs == limitMs 时应解除压制并立刻行动");

        Drive(bot, h.Run, () => bot.Reason == HostedBotStop.Settled, "40ms limit clears after threshold");
        Assert.Equal(1, h.Run.Reported);
        Assert.Equal(HostedBotStop.Settled, bot.Reason);
    }

    [Fact]
    public void MinClearTimeMs_holds_single_far_monster_without_walking_towards_it()
    {
        // 仅剩 1 只远处静止怪（|760 - 300| = 460 > 160）：AliveCount == 1 必须原地等待不往前走
        const double limitMs = 5_000.0;
        using var h = new HostedRoomBotTests.Hosted(new[] { (760, 100) }, hp: 40, settleAfter: 1, monsterSpeed: 0);
        var bot = new HostedRoomBot { MinClearTimeMs = limitMs };
        double startX = h.Room.Hero.X;

        Drive(bot, h.Run, () => bot.ElapsedMs >= 2_000.0, "wait while holding lone far monster", frameMs: FrameMs);
        Assert.Equal(startX, h.Room.Hero.X, 4);
        Assert.Equal(1, h.Room.AliveCount);

        Drive(bot, h.Run, () => bot.Reason == HostedBotStop.Settled, "walk and kill after limit", frameMs: FrameMs);
        Assert.Equal(1, h.Run.Reported);
        Assert.True(bot.ElapsedMs >= limitMs);
    }

    [Fact]
    public void MinClearTimeMs_holds_last_monster_until_exact_threshold_boundary()
    {
        // 两只分开的静止怪：先清掉近处第一只，远处第二只在 ElapsedMs < limit 时必须活着，达到 limit 后击杀
        const double limitMs = 10_000.0;
        using var h = new HostedRoomBotTests.Hosted(new[] { (340, 100), (760, 100) }, hp: 40, settleAfter: 2, monsterSpeed: 0);
        var bot = new HostedRoomBot(totalLimitMs: 120_000.0, noProgressLimitMs: 60_000.0)
        {
            MinClearTimeMs = limitMs,
        };

        // 推进到恰好 limitMs - 1 ms（用 1ms 步进精细逼近边界）
        Drive(bot, h.Run, () => bot.ElapsedMs >= limitMs - 50.0, "approach limit", frameMs: FrameMs);
        while (bot.ElapsedMs + 1.0 < limitMs)
        {
            bot.Advance(h.Run, 1.0);
        }
        double rem = (limitMs - 1.0) - bot.ElapsedMs;
        if (rem > 0.0)
        {
            bot.Advance(h.Run, rem);
        }

        // 严格在 limit - 1 毫秒：第一只已死，最后一只仍存活且未停手
        Assert.True(bot.ElapsedMs < limitMs);
        Assert.Equal(1, h.Room.AliveCount);
        Assert.Equal(1, h.Run.Reported);
        Assert.False(bot.Stopped);

        // 跨过 limit 与 limit + 1 毫秒后，自动机解除压制并击杀最后一只怪
        bot.Advance(h.Run, 1.0); // ElapsedMs == limitMs
        Assert.True(bot.ElapsedMs >= limitMs);
        bot.Advance(h.Run, 1.0); // ElapsedMs == limitMs + 1

        Drive(bot, h.Run, () => bot.Reason == HostedBotStop.Settled, "kills last monster after limit", frameMs: FrameMs);
        Assert.Equal(2, h.Run.Reported);
        Assert.Equal(0, h.Room.AliveCount);
        Assert.Equal(HostedBotStop.Settled, bot.Reason);
    }

    [Fact]
    public void MinClearTimeMs_holds_stacked_monsters_so_single_cleave_cannot_clear_early()
    {
        // 两只挤在近身范围内的怪：即使 AliveCount == 2，在达到 limitMs 之前也不许一刀同时清空
        const double limitMs = 8_000.0;
        using var h = new HostedRoomBotTests.Hosted(new[] { (340, 100), (360, 102) }, hp: 40, settleAfter: 2);
        var bot = new HostedRoomBot(totalLimitMs: 120_000.0, noProgressLimitMs: 60_000.0)
        {
            MinClearTimeMs = limitMs,
        };

        Drive(bot, h.Run, () => bot.ElapsedMs >= limitMs - 50.0, "hold stacked monsters", frameMs: FrameMs);
        Assert.True(h.Room.AliveCount >= 1);
        Assert.False(bot.Stopped);

        Drive(bot, h.Run, () => bot.Reason == HostedBotStop.Settled, "clear stacked after limit", frameMs: FrameMs);
        Assert.Equal(2, h.Run.Reported);
        Assert.True(bot.ElapsedMs >= limitMs);
    }

    [Fact]
    public void MinClearTimeMs_does_not_mask_NoSettlement_when_room_already_empty()
    {
        // 空房（AliveCount == 0）且服务端不发结算：即使 MinClearTimeMs 很大，也必须按 NoProgressLimitMs 触发 NoSettlement
        using var h = new HostedRoomBotTests.Hosted(Array.Empty<(int, int)>(), settleAfter: -1);
        var bot = new HostedRoomBot(totalLimitMs: 120_000.0, noProgressLimitMs: 5_000.0)
        {
            MinClearTimeMs = 60_000.0,
        };

        Drive(bot, h.Run, () => bot.Stopped, "empty room triggers NoSettlement", frameMs: 50.0, maxSeconds: 20);
        Assert.Equal(HostedBotStop.NoSettlement, bot.Reason);
        Assert.True(bot.ElapsedMs < 10_000.0);
    }

    [Fact]
    public void MinClearTimeMs_exceeding_NoProgressLimitMs_does_not_false_trigger_NoProgress()
    {
        // 最短清图时间 75 秒 > 默认无进展上限 60 秒：等待期间刷新进度基线，不得误报 NoProgress
        const double limitMs = 75_000.0;
        using var h = new HostedRoomBotTests.Hosted(new[] { (340, 100) }, hp: 40, settleAfter: 1);
        var bot = new HostedRoomBot(totalLimitMs: 180_000.0, noProgressLimitMs: HostedRoomBot.DefaultNoProgressLimitMs)
        {
            MinClearTimeMs = limitMs,
        };

        // 推到 74 秒（已超过 60 秒无进展上限）
        Drive(bot, h.Run, () => bot.ElapsedMs >= 74_000.0, "reach 74s", frameMs: 50.0, maxSeconds: 100);
        Assert.Equal(1, h.Room.AliveCount);
        Assert.False(bot.Stopped);
        Assert.Equal(HostedBotStop.None, bot.Reason);

        // 推过 75 秒后完成击杀并结算
        Drive(bot, h.Run, () => bot.Reason == HostedBotStop.Settled, "clear after 75s", frameMs: 50.0, maxSeconds: 100);
        Assert.Equal(1, h.Run.Reported);
        Assert.True(bot.ElapsedMs >= limitMs);
        Assert.Equal(HostedBotStop.Settled, bot.Reason);
    }

    [Fact]
    public void MinClearTimeMs_does_not_hold_non_final_room_with_ExitSide()
    {
        // 非最终房（ExitSide != null）：即使 MinClearTimeMs = 60 秒，也必须立刻清完本房怪并准备过门，不得卡在前置房
        using var h = new HostedRoomBotTests.Hosted(new[] { (340, 100) }, hp: 40);
        h.Run.SetExit(GateSide.Right);
        var bot = new HostedRoomBot(totalLimitMs: 180_000.0, noProgressLimitMs: 60_000.0)
        {
            MinClearTimeMs = 60_000.0,
        };

        Drive(bot, h.Run, () => h.Room.AliveCount == 0, "non-final room clears immediately", frameMs: FrameMs, maxSeconds: 20);
        Assert.True(bot.ElapsedMs < 15_000.0, "非最终房不应被 60s 最短清图时间拖住，实际耗时 " + bot.ElapsedMs);
        Assert.Equal(1, h.Run.Reported);
    }
}
