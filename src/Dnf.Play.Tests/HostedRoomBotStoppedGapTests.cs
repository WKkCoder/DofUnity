using System;
using Dnf.Render.Tests;
using Xunit;

namespace Dnf.Play.Tests;

/// <summary>
/// r20fix-e2e C-4 补扫(压制判定邻近的旧逻辑):<see cref="HostedRoomBot.Advance"/> 里两处的补测——
/// ① 帧开头(累计完时间之后)就先检查一遍、该停就停:整段上限在这一帧已经到了,这一帧一拍也不补(返回 0、一步决策也没做);上限恰好等于累计时间也算到了;
/// ② 因整段上限停手之后(房间还在打,不是结算收齐),每一帧交给房间的攻击键与跳跃键都是松开的:主角不出手、不起跳、不走位。
/// 合成房间:近怪放在主角右边 40 处原地不动(走速 0),主角用带跳跃动作的角色;逻辑时钟按 60 帧每秒推进,不碰墙钟、不连网。
/// 期望值都是手算:一帧 1000/60 毫秒恰好补一个心跳(欠账恰等于一个步长)。
/// </summary>
public sealed class HostedRoomBotStoppedGapTests
{
    private const double FrameMs = 1000.0 / 60.0;

    /// <summary>夹具里主角的起点(与 <see cref="RoomCombatTests.Arena"/> 一致);每条用例都先对一遍,夹具一旦改动这里先红。</summary>
    private const int HeroX = 300;

    private const int HeroY = 100;

    private static RoomCombatTests.Arena NewArena()
    {
        var arena = new RoomCombatTests.Arena(new[] { (HeroX + 40, HeroY) }, hp: 40, chr: AvatarFixture.ActionChrPath, monsterSpeed: 0);
        Assert.True(arena.Hero.CanJump, "夹具自检:这个主角能跳");
        Assert.Equal(HeroX, arena.Hero.X, 6);
        Assert.Equal(HeroY, arena.Hero.Y, 6);
        Assert.Equal(1, arena.Session.AliveCount);
        return arena;
    }

    // ------------------------------------------------------------------ ① 帧开头的检查先于补步

    [Theory]
    [InlineData(1.0, 0, 0, 0)]                      // 上限远小于一帧:第一帧开头就到了,一拍也不补
    [InlineData(1000.0 / 60.0, 0, 0, 0)]            // 上限恰好等于第一帧的累计时间:算到了(>=),同样一拍不补
    [InlineData(20.0, 1, 0, 1)]                     // 上限在一帧与两帧之间:第一帧照常补一拍,第二帧开头才到,第二帧一拍不补
    public void The_total_limit_is_checked_when_the_frame_starts_so_the_frame_that_reaches_it_takes_no_beat(
        double totalLimitMs, int beatsInFirstFrame, int beatsInSecondFrame, int expectedSteps)
    {
        using var arena = NewArena();
        var run = new HostedRoomRun(arena.Session, id => true, () => false);
        var bot = new HostedRoomBot(totalLimitMs: totalLimitMs, noProgressLimitMs: 120_000.0);

        Assert.Equal(beatsInFirstFrame, bot.Advance(run, FrameMs));
        Assert.Equal(beatsInSecondFrame, bot.Advance(run, FrameMs));

        Assert.Equal(HostedBotStop.TimedOut, bot.Reason);
        Assert.Equal(expectedSteps, bot.Steps);
    }

    // ------------------------------------------------------------------ ② 停手之后不出手、不起跳

    [Fact]
    public void A_bot_that_stopped_for_the_total_limit_stands_still_neither_attacking_nor_jumping_in_every_later_frame()
    {
        using var arena = NewArena();
        var run = new HostedRoomRun(arena.Session, id => true, () => false);
        var bot = new HostedRoomBot(totalLimitMs: 1.0, noProgressLimitMs: 120_000.0);

        for (int frame = 1; frame <= 120; frame++)
        {
            Assert.Equal(0, bot.Advance(run, FrameMs));
            Assert.Equal(HostedBotStop.TimedOut, bot.Reason);
            Assert.False(run.Finished, "没有结算收齐:房间还在打,键位会交给房间");
            Assert.Equal(0, arena.Session.Hits);
            Assert.Equal(1, arena.Session.AliveCount);
            Assert.False(arena.Hero.IsAirborne);
            Assert.Equal(0.0, arena.Hero.Z);
            Assert.Equal(HeroX, arena.Hero.X, 6);
            Assert.Equal(HeroY, arena.Hero.Y, 6);
        }

        Assert.Equal(0, bot.Steps);
    }
}
