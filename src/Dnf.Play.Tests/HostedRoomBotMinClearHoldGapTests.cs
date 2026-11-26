using System;
using System.Linq;
using Xunit;

namespace Dnf.Play.Tests;

/// <summary>
/// r20fix-e2e C-4:<see cref="HostedRoomBot.MinClearTimeMs"/> 压制判定三处细节的补测——
/// ① 压制期间主角在 X 与 Y 两个方向都站着不动;
/// ② 判"是否挤在近身范围里"要逐只看,第 0 只离得远(超过 160)也要解除压制;
/// ③ 距离 160 的边界两侧(159、160、161,左右两边各一组)。
/// 合成房间:主角站在 (300, 100)(<see cref="RoomCombatTests.Arena"/> 的夹具,下面用 <see cref="HeroX"/> 对一遍),怪物原地不动(走速 0),
/// 近怪放在 340(与主角差 40),边界怪放在 300 加减 159、160、161;逻辑时钟按 60 帧每秒推进,不碰墙钟、不连网。
/// 期望值都是手算:压制的充要条件是"只剩 1 只活怪,或所有活怪与主角的横向距离都不超过 160";压制时一只都打不掉,不压制时近怪会在几秒内被打死。
/// </summary>
public sealed class HostedRoomBotMinClearHoldGapTests
{
    private const double FrameMs = 1000.0 / 60.0;

    /// <summary>夹具里主角的起点(与 <see cref="RoomCombatTests.Arena"/> 一致);每条用例都先对一遍,夹具一旦改动这里先红。</summary>
    private const int HeroX = 300;

    private const int HeroY = 100;

    /// <summary>最短清图时间,远大于每条用例推进的时间,压制是否生效只看判定本身。</summary>
    private const double LimitMs = 60_000.0;

    private static HostedRoomBot NewBot()
    {
        return new HostedRoomBot(totalLimitMs: 180_000.0, noProgressLimitMs: 120_000.0) { MinClearTimeMs = LimitMs };
    }

    private static void CheckStart(HostedRoomBotTests.Hosted h)
    {
        Assert.Equal(HeroX, h.Room.Hero.X, 6);
        Assert.Equal(HeroY, h.Room.Hero.Y, 6);
    }

    /// <summary>每帧推进,直到条件成立;超过 <paramref name="maxSeconds"/> 模拟秒还不成立就让测试失败。</summary>
    private static void Drive(HostedRoomBot bot, HostedRoomRun run, Func<bool> until, string what, int maxSeconds = 30)
    {
        int frames = 0;
        int max = (int)(maxSeconds * 1000.0 / FrameMs);
        while (!until() && frames < max)
        {
            bot.Advance(run, FrameMs);
            frames++;
        }
        Assert.True(until(), what + "(" + maxSeconds + " 模拟秒内没有成立;" + bot.LogLine() + ")");
    }

    // ------------------------------------------------------------------ ① 压制时两个方向都不动

    [Fact]
    public void Holding_the_lone_far_monster_keeps_the_hero_still_on_both_axes_every_frame()
    {
        // 只剩 1 只远处静止怪(横向差 460,纵向差 0):不压制的话每拍都是 dirX = +1、dirY = 0 向它走;压制时 X 与 Y 都必须一动不动
        using var h = new HostedRoomBotTests.Hosted(new[] { (760, HeroY) }, hp: 40, settleAfter: 1, monsterSpeed: 0);
        CheckStart(h);
        HostedRoomBot bot = NewBot();

        for (int frame = 0; frame < 120; frame++)          // 120 帧 = 2 秒逻辑时间,每一帧之后都看一遍
        {
            bot.Advance(h.Run, FrameMs);
            Assert.Equal(HeroX, h.Room.Hero.X, 6);
            Assert.Equal(HeroY, h.Room.Hero.Y, 6);
            Assert.False(h.Room.Hero.IsAttacking);
        }

        Assert.True(bot.ElapsedMs > 1_900.0 && bot.ElapsedMs < 2_100.0, "两秒的推进都在压制期内,实际 " + bot.ElapsedMs);
        Assert.Equal(1, h.Room.AliveCount);
        Assert.Empty(h.Posted);
        Assert.False(bot.Stopped);
    }

    [Fact]
    public void Holding_two_stacked_monsters_keeps_the_hero_still_on_both_axes_even_when_they_stand_off_his_row()
    {
        // 两只都在近身范围里(差 40 与 50),其中一只的纵向与主角错开 30:不压制的话主角会朝它们走位;压制时一动不动
        using var h = new HostedRoomBotTests.Hosted(new[] { (HeroX + 40, HeroY + 30), (HeroX + 50, HeroY) }, hp: 40, settleAfter: 2, monsterSpeed: 0);
        CheckStart(h);
        HostedRoomBot bot = NewBot();

        for (int frame = 0; frame < 120; frame++)
        {
            bot.Advance(h.Run, FrameMs);
            Assert.Equal(HeroX, h.Room.Hero.X, 6);
            Assert.Equal(HeroY, h.Room.Hero.Y, 6);
            Assert.False(h.Room.Hero.IsAttacking);
        }

        Assert.Equal(2, h.Room.AliveCount);
        Assert.Empty(h.Posted);
        Assert.False(bot.Stopped);
    }

    // ------------------------------------------------------------------ ② 逐只检查:第 0 只离得远也要解除压制

    [Fact]
    public void A_far_monster_listed_first_releases_the_hold_so_the_near_one_dies_long_before_the_limit()
    {
        // 第 0 只在 760(差 460,超过 160),第 1 只在 340(差 40):判定必须从第 0 只看起——第 0 只就够远,不压制;
        // 近怪被打死(上报的是编号 1),打死之后只剩 1 只活怪,又回到压制,远怪活着、主角不再往前走
        using var h = new HostedRoomBotTests.Hosted(new[] { (760, HeroY), (HeroX + 40, HeroY) }, hp: 40, settleAfter: 2, monsterSpeed: 0);
        CheckStart(h);
        HostedRoomBot bot = NewBot();

        Drive(bot, h.Run, () => h.Posted.Count >= 1, "近处的第 1 只被打死");

        Assert.Equal(new[] { 1 }, h.Posted.ToArray());
        Assert.Equal(1, h.Room.AliveCount);
        Assert.True(bot.ElapsedMs < 20_000.0, "近怪应在压制上限之前很久就被打死,实际 " + bot.ElapsedMs);

        double xAfterKill = h.Room.Hero.X;
        for (int frame = 0; frame < 120; frame++)           // 只剩远怪:压制回来,主角站着不动,远怪不被碰
        {
            bot.Advance(h.Run, FrameMs);
        }
        Assert.Equal(xAfterKill, h.Room.Hero.X, 6);
        Assert.Equal(new[] { 1 }, h.Posted.ToArray());
        Assert.Equal(1, h.Room.AliveCount);
    }

    [Fact]
    public void A_far_monster_listed_second_releases_the_hold_the_same_way()
    {
        // 与上一条对称:近怪在第 0 位(340),远怪在第 1 位(760)。被打死的是编号 0
        using var h = new HostedRoomBotTests.Hosted(new[] { (HeroX + 40, HeroY), (760, HeroY) }, hp: 40, settleAfter: 2, monsterSpeed: 0);
        CheckStart(h);
        HostedRoomBot bot = NewBot();

        Drive(bot, h.Run, () => h.Posted.Count >= 1, "近处的第 0 只被打死");

        Assert.Equal(new[] { 0 }, h.Posted.ToArray());
        Assert.Equal(1, h.Room.AliveCount);
        Assert.True(bot.ElapsedMs < 20_000.0, "近怪应在压制上限之前很久就被打死,实际 " + bot.ElapsedMs);
    }

    [Fact]
    public void A_dead_monster_lying_far_away_does_not_release_the_hold_for_the_living_ones_near_the_hero()
    {
        // 三只:第 0 只在 760(差 460),第 1、2 只在近身范围里(差 40 与 50)。开打之前在带外把第 0 只直接打死:尸体还留在怪物列表里、横向差仍是 460,
        // 但判定只看活着的怪——剩下两只活怪都在近身范围里,必须压制,主角站着不动,一只也打不到
        using var h = new HostedRoomBotTests.Hosted(new[] { (760, HeroY), (HeroX + 40, HeroY), (HeroX + 50, HeroY) }, hp: 40, settleAfter: -1, monsterSpeed: 0);
        CheckStart(h);
        Assert.True(h.Room.Monsters[0].ApplyHit(1_000_000, false, 0, false), "这一下应当把第 0 只打死");
        Assert.False(h.Room.Monsters[0].IsAlive);
        Assert.Equal(2, h.Room.AliveCount);
        HostedRoomBot bot = NewBot();

        Drive(bot, h.Run, () => bot.ElapsedMs >= 5_000.0, "压制期内推进 5 秒");

        Assert.Equal(new[] { 0 }, h.Posted.ToArray());      // 只有带外打死的第 0 只被上报
        Assert.Equal(2, h.Room.AliveCount);
        Assert.Equal(HeroX, h.Room.Hero.X, 6);
        Assert.Equal(HeroY, h.Room.Hero.Y, 6);
        Assert.False(h.Room.Hero.IsAttacking);
        Assert.False(bot.Stopped);
    }

    // ------------------------------------------------------------------ ③ 距离 160 的边界

    [Theory]
    [InlineData(HeroX + 159)]    // 459:差 159,在范围里 → 压制
    [InlineData(HeroX + 160)]    // 460:差恰好 160,不算"超过" → 压制
    [InlineData(HeroX - 159)]    // 141:左边差 159 → 压制
    [InlineData(HeroX - 160)]    // 140:左边差恰好 160 → 压制
    public void Monsters_at_most_160_away_keep_the_hold_so_nobody_dies_before_the_limit(int boundaryX)
    {
        using var h = new HostedRoomBotTests.Hosted(new[] { (HeroX + 40, HeroY), (boundaryX, HeroY) }, hp: 40, settleAfter: 2, monsterSpeed: 0);
        CheckStart(h);
        HostedRoomBot bot = NewBot();

        Drive(bot, h.Run, () => bot.ElapsedMs >= 5_000.0, "压制期内推进 5 秒");

        Assert.Empty(h.Posted);
        Assert.Equal(2, h.Room.AliveCount);
        Assert.Equal(HeroX, h.Room.Hero.X, 6);
        Assert.Equal(HeroY, h.Room.Hero.Y, 6);
        Assert.False(bot.Stopped);
    }

    [Theory]
    [InlineData(HeroX + 161)]    // 461:差 161,超过 160 → 不压制,近怪被打死
    [InlineData(HeroX - 161)]    // 139:左边差 161 → 不压制
    public void A_monster_more_than_160_away_releases_the_hold_and_the_near_one_dies_early(int boundaryX)
    {
        using var h = new HostedRoomBotTests.Hosted(new[] { (HeroX + 40, HeroY), (boundaryX, HeroY) }, hp: 40, settleAfter: 2, monsterSpeed: 0);
        CheckStart(h);
        HostedRoomBot bot = NewBot();

        Drive(bot, h.Run, () => h.Posted.Count >= 1, "近处的第 0 只被打死");

        Assert.Equal(new[] { 0 }, h.Posted.ToArray());
        Assert.Equal(1, h.Room.AliveCount);
        Assert.True(bot.ElapsedMs < 20_000.0, "近怪应在压制上限之前很久就被打死,实际 " + bot.ElapsedMs);
    }
}
