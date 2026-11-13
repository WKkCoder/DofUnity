using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网房间自动机(<see cref="HostedRoomBot"/>,r16-onlinebot):在 <see cref="HostedRoomRun"/> 托管的房间里复用离线自动机(<see cref="DungeonBot"/>)的决策打怪、
/// 本地怪死亡按序号上报且数目与击杀数一致、结算信号一到就停手(时间照走让流程回城)、以及"打不过 / 长时间没进展 / 整段超时"的上限兜底。
/// 房间是 <see cref="RoomCombatTests.Arena"/>(合成房间、迷你怪物,怪物序号 = 位置);"联网"一侧用委托代替(与联网层的真接线见客户端程序集的端到端测试)。
/// 数据全是合成的,不含任何原版数据与服务端编号。
/// </summary>
public class HostedRoomBotTests
{
    private const double FrameMs = 1000.0 / 60.0;

    private static readonly (int X, int Y)[] ThreeMonsters = { (340, 100), (420, 120), (500, 100) };

    /// <summary>一间托管房间 + 记下上报的外部委托:可以拒收、可以按"已上报几只"宣布结算(<paramref name="settleAfter"/>,负数不自动结算)或手动宣布。(流程自动机的测试也用它。)</summary>
    internal sealed class Hosted : IDisposable
    {
        public readonly RoomCombatTests.Arena Arena;
        public readonly List<int> Posted = new List<int>();
        public bool Accept = true;
        public bool Settled;
        public int SettleAfter;
        public readonly HostedRoomRun Run;

        /// <param name="monsterSpeed">怪物的走速;0 是怪物原地不动(主角不走过去就打不到),默认与 <see cref="RoomCombatTests.Arena"/> 一样会追过来。</param>
        public Hosted((int X, int Y)[] monsters, int hp = 40, double returnDelayMs = 0.0, bool frozen = false, HeroVitals vitals = null, int settleAfter = -1,
            double monsterSpeed = 222.5)
        {
            Arena = new RoomCombatTests.Arena(monsters, hp: hp, frozen: frozen, vitals: vitals, monsterSpeed: monsterSpeed);
            SettleAfter = settleAfter;
            Run = new HostedRoomRun(
                Arena.Session,
                id =>
                {
                    Posted.Add(id);
                    return Accept;
                },
                () => Settled || (SettleAfter >= 0 && Posted.Count >= SettleAfter),
                returnDelayMs);
        }

        public RoomCombatSession Room => Arena.Session;

        public void Dispose()
        {
            Arena.Dispose();
        }
    }

    /// <summary>按 <paramref name="frameMs"/> 一帧一帧推进,直到条件成立;超过 <paramref name="maxSeconds"/> 模拟秒还不成立就让测试失败。返回用了几帧。</summary>
    private static int Drive(HostedRoomBot bot, HostedRoomRun run, Func<bool> until, string what, double frameMs = FrameMs, int maxSeconds = 120)
    {
        int frames = 0;
        int max = (int)(maxSeconds * 1000.0 / frameMs);
        while (!until() && frames < max)
        {
            bot.Advance(run, frameMs);
            frames++;
        }
        Assert.True(until(), what + "(" + maxSeconds + " 模拟秒内没有成立;" + bot.LogLine() + ")");
        return frames;
    }

    /// <summary>空转 <paramref name="ms"/> 毫秒(自动机已经停手时就是只让时间走)。</summary>
    private static void Idle(HostedRoomBot bot, HostedRoomRun run, double ms)
    {
        for (double t = 0; t < ms; t += FrameMs)
        {
            bot.Advance(run, FrameMs);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 一、打怪:复用离线自动机的决策,上报数与击杀数一致
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_bot_kills_every_monster_of_a_hosted_room_and_each_kill_is_posted_exactly_once_by_index()
    {
        using var h = new Hosted(ThreeMonsters);
        var bot = new HostedRoomBot();

        Drive(bot, h.Run, () => h.Posted.Count >= 3, "三只怪都被打死并上报");
        Idle(bot, h.Run, 5000);                                                   // 之后再多走几秒也不会有重复的上报

        Assert.Equal(new[] { 0, 1, 2 }, h.Posted.OrderBy(i => i).ToArray());
        Assert.Equal(3, h.Posted.Count);
        Assert.Equal((3, 0), (h.Run.Reported, h.Run.Dropped));
        Assert.Equal(3, h.Room.Kills);
        Assert.Equal(h.Room.Kills, h.Run.Reported);                               // 上报数 == 击杀数
        Assert.Equal(0, h.Room.AliveCount);
        Assert.True(h.Room.Hits >= 6, "每只 40 点血、一击约 25 点,至少要六下");
        Assert.False(bot.Stopped);                                                 // 本地杀光不等于结束:结算还没来
        Assert.Equal(SettlementChoice.None, h.Run.Choice);
        Assert.Equal(HostedBotStop.None, bot.Reason);
        Assert.True(bot.Steps > 0);
    }

    [Fact]
    public void A_report_the_outside_refuses_is_dropped_once_and_the_dropped_plus_reported_still_equal_the_kills()
    {
        using var h = new Hosted(ThreeMonsters) { Accept = false };
        var bot = new HostedRoomBot();

        Drive(bot, h.Run, () => h.Posted.Count >= 3, "三只怪都被打死并被拒收");
        Idle(bot, h.Run, 3000);

        Assert.Equal(3, h.Posted.Count);                                           // 每只只交一次,被拒收的也不会重交
        Assert.Equal((0, 3), (h.Run.Reported, h.Run.Dropped));
        Assert.Equal(h.Room.Kills, h.Run.Reported + h.Run.Dropped);
    }

    [Fact]
    public void The_bot_fights_without_any_navigation_data_because_a_hosted_room_has_no_maze()
    {
        // 联网房间只有一间,没有迷宫、没有出口:清完怪之后原地不动,等外面的结算(不会去找出口,也不会因为没有会话而出错)
        using var h = new Hosted(new[] { (340, 100) });
        var bot = new HostedRoomBot();
        Drive(bot, h.Run, () => h.Room.AliveCount == 0, "怪被打死");
        Idle(bot, h.Run, 2000);                                                    // 等死亡动画与出手收完,再看站没站稳

        double x = h.Room.Hero.X, y = h.Room.Hero.Y;
        Idle(bot, h.Run, 3000);

        Assert.Equal((x, y), (h.Room.Hero.X, h.Room.Hero.Y));
        Assert.False(bot.Stopped);
    }

    [Fact]
    public void The_bot_walks_over_to_monsters_that_never_come_to_it_along_both_axes_instead_of_standing_in_place()
    {
        // 怪原地不动(速度 0):一只远在同一排、一只又远又差着一段纵深。主角若只会原地出手,既打不到、也等不来,整局一只也打不死
        using var h = new Hosted(new[] { (700, 100), (900, 130) }, monsterSpeed: 0);
        var bot = new HostedRoomBot();
        double x0 = h.Room.Hero.X, y0 = h.Room.Hero.Y;

        Drive(bot, h.Run, () => h.Posted.Count >= 2, "走过去把两只不会动的怪都打死并上报", maxSeconds: 60);

        Assert.Equal(new[] { 0, 1 }, h.Posted.OrderBy(i => i).ToArray());
        Assert.Equal((2, 0), (h.Run.Reported, h.Run.Dropped));
        Assert.Equal(h.Room.Kills, h.Run.Reported);
        Assert.True(h.Room.Hero.X > x0 + 400, "主角横向走了过去:" + x0 + " → " + h.Room.Hero.X);
        Assert.True(h.Room.Hero.Y > y0 + 15, "主角在纵深方向也走了:" + y0 + " → " + h.Room.Hero.Y);
    }

    // ---------------------------------------------------------------------------------------------
    // 二、结算信号到了:停手,时间照走,直到回城
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Once_the_settlement_signal_arrives_the_bot_stops_acting_even_with_monsters_left_and_time_keeps_running_until_the_return()
    {
        using var h = new Hosted(ThreeMonsters, returnDelayMs: 500, settleAfter: 1);      // 第一只怪上报之后,"服务端"宣布结算收齐
        var bot = new HostedRoomBot();

        Drive(bot, h.Run, () => bot.Stopped, "收到结算信号后自动机停手");
        Assert.Equal(HostedBotStop.Settled, bot.Reason);
        Assert.True(h.Run.Finished);
        Assert.True(h.Room.AliveCount > 0, "停手时房间里还有怪:停手是因为结算信号,不是因为打完了");
        Assert.Equal(SettlementChoice.None, h.Run.Choice);                                 // 回城延迟还没走完
        int stepsAtStop = bot.Steps;
        double elapsedAtStop = bot.ElapsedMs;

        Idle(bot, h.Run, 1000);                                                            // 先让正在挥的那一击收完(那是模拟本身,不是自动机在出手)
        Assert.Equal(SettlementChoice.ReturnToTown, h.Run.Choice);                         // 时间照走:延迟走完,流程可以回城
        int hits = h.Room.Hits;
        double x = h.Room.Hero.X, y = h.Room.Hero.Y;
        Idle(bot, h.Run, 5000);

        Assert.Equal(stepsAtStop, bot.Steps);                                              // 一步决策也没再做
        Assert.Equal(elapsedAtStop, bot.ElapsedMs);                                        // 停手之后不再累计
        Assert.Equal(hits, h.Room.Hits);                                                   // 不再出手
        Assert.Equal((x, y), (h.Room.Hero.X, h.Room.Hero.Y));                              // 不再走位
        Assert.Equal(HostedBotStop.Settled, bot.Reason);                                   // 原因不再变
        Assert.Equal(0, bot.Advance(h.Run, 200));
    }

    [Fact]
    public void The_return_delay_still_counts_after_the_bot_stopped_and_a_huge_frame_cannot_skip_it()
    {
        using var h = new Hosted(new[] { (340, 100) }, returnDelayMs: 1000, settleAfter: 1);
        var bot = new HostedRoomBot();
        Drive(bot, h.Run, () => bot.Stopped, "结算后停手");
        Assert.Equal(SettlementChoice.None, h.Run.Choice);

        // 停手后一帧也只让时间走 DungeonBot.MaxDebtMs(卡了几秒的帧不会一步跨过回城延迟,与按心跳补步同一个规矩)
        Assert.Equal(0, bot.Advance(h.Run, 5000));
        Assert.Equal(SettlementChoice.None, h.Run.Choice);

        Idle(bot, h.Run, 1000);
        Assert.Equal(SettlementChoice.ReturnToTown, h.Run.Choice);
    }

    [Fact]
    public void A_run_that_settled_before_the_first_frame_costs_the_bot_exactly_one_beat_because_the_run_only_learns_it_when_stepped()
    {
        // HostedRoomRun.Finished 是在它自己的一步里看到外面的信号才置位的:第一帧的第一个心跳照常决策并走一步,走完这一步才知道已经结算,之后一个心跳也不再补
        using var h = new Hosted(ThreeMonsters) { Settled = true };
        var bot = new HostedRoomBot();

        Assert.Equal(1, bot.Advance(h.Run, FrameMs));
        Assert.Equal(0, bot.Advance(h.Run, FrameMs));

        Assert.Equal(HostedBotStop.Settled, bot.Reason);
        Assert.Equal(1, bot.Steps);
        Assert.Equal(0, h.Room.Hits);
    }

    // ---------------------------------------------------------------------------------------------
    // 三、上限兜底:不许无限挂机
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_hero_that_goes_down_without_a_revive_window_stops_the_bot_at_once()
    {
        // 怪放得远、还没打起来:直接改血量让主角倒下绕过了受击动作,若正在挥击,那一击还会照常命中,与自动机无关
        using var h = new Hosted(new[] { (900, 100) }, vitals: new HeroVitals(1, 1000, 500, 0, 0, 0));
        var bot = new HostedRoomBot();
        Idle(bot, h.Run, 200);
        Assert.False(bot.Stopped);
        Assert.Null(h.Room.Revive);                                                        // 联网房间默认没有复活窗口

        h.Room.Vitals.Damage(h.Room.Vitals.Hp);
        Assert.True(h.Room.HeroDead);
        bot.Advance(h.Run, FrameMs);

        Assert.Equal(HostedBotStop.HeroDown, bot.Reason);
        Assert.Contains("hero=down", bot.LogLine());
        int steps = bot.Steps;
        Idle(bot, h.Run, 2000);
        Assert.Equal(steps, bot.Steps);                                                    // 倒了就不再决策
        Assert.Empty(h.Posted);
        Assert.Equal(SettlementChoice.None, h.Run.Choice);                                 // 没结算:不会回城,由宿主收尾(FlowBot 置 Done)
    }

    [Fact]
    public void A_hero_with_no_vitals_as_under_nodamage_never_goes_down_and_the_limits_still_apply()
    {
        using var h = new Hosted(ThreeMonsters);                                           // 与 -dnfnodamage 一样:主角没有生命值,怪物打不到他
        Assert.Null(h.Room.Vitals);
        var bot = new HostedRoomBot();
        Drive(bot, h.Run, () => h.Posted.Count >= 3, "没有生命值的主角照样打完");
        Assert.False(h.Room.HeroDead);
        Assert.NotEqual(HostedBotStop.HeroDown, bot.Reason);
        Assert.Contains("hero=up", bot.LogLine());

        // 上限照常生效:怪都死了、结算不来,无进展上限一到就停手
        var impatient = new HostedRoomBot(noProgressLimitMs: 2000);
        Drive(impatient, h.Run, () => impatient.Stopped, "无进展上限兜底");
        Assert.Equal(HostedBotStop.NoSettlement, impatient.Reason);
    }

    [Fact]
    public void A_revive_window_with_a_coin_is_used_like_offline_so_the_bot_gets_up_and_keeps_fighting()
    {
        using var h = new Hosted(new[] { (340, 100) }, vitals: new HeroVitals(1, 1000, 500, 0, 0, 0));
        h.Room.Revive = new HeroRevive(null, new ReviveCoins(1));
        var bot = new HostedRoomBot();
        Idle(bot, h.Run, 200);

        h.Room.Vitals.Damage(h.Room.Vitals.Hp);
        Assert.True(h.Room.HeroDead);
        Drive(bot, h.Run, () => h.Room.Revived == 1, "复活窗口里按攻击键复活");

        Assert.False(h.Room.HeroDead);
        Assert.NotEqual(HostedBotStop.HeroDown, bot.Reason);
        Drive(bot, h.Run, () => h.Room.AliveCount == 0, "复活之后继续把怪打完");
        Assert.Equal(1, h.Run.Reported);
        Assert.False(bot.Stopped);
    }

    [Fact]
    public void A_revive_window_that_runs_out_fails_the_run_and_the_bot_stops_with_failed()
    {
        using var h = new Hosted(ThreeMonsters, vitals: new HeroVitals(1, 1000, 500, 0, 0, 0));
        h.Room.Revive = new HeroRevive(null, new ReviveCoins(0));                          // 没有币:只能等失败计时走完
        var bot = new HostedRoomBot();
        Idle(bot, h.Run, 200);

        h.Room.Vitals.Damage(h.Room.Vitals.Hp);
        Drive(bot, h.Run, () => bot.Stopped, "失败计时走完后自动机停手");

        Assert.Equal(HostedBotStop.Failed, bot.Reason);
        Assert.True(h.Run.Failed);
        Assert.NotEqual(HostedBotStop.HeroDown, bot.Reason);                               // 有复活窗口时不算"没有复活窗口的倒下"
    }

    [Fact]
    public void Monsters_the_hero_can_never_reach_end_with_no_progress_after_the_limit()
    {
        using var h = new Hosted(new[] { (900, 100) }, frozen: true);                      // 主角的可走区域只有脚下一小块,够不着怪
        var bot = new HostedRoomBot(noProgressLimitMs: 2000);

        Drive(bot, h.Run, () => bot.Stopped, "够不着怪的自动机在无进展上限到点时停手");

        Assert.Equal(HostedBotStop.NoProgress, bot.Reason);
        Assert.InRange(bot.ElapsedMs, 2000, 2000 + 2 * FrameMs);
        Assert.Equal(0, h.Room.Hits);
        Assert.Empty(h.Posted);
        Assert.True(h.Room.AliveCount > 0);
    }

    [Fact]
    public void A_fight_that_keeps_landing_hits_is_not_cut_off_for_lack_of_progress_but_the_whole_segment_limit_ends_it()
    {
        using var h = new Hosted(new[] { (340, 100) }, hp: 10_000_000);                    // 打不死的肉墙:一直在挨打
        var bot = new HostedRoomBot(totalLimitMs: 8000, noProgressLimitMs: 2500);

        Drive(bot, h.Run, () => bot.Stopped, "整段上限到点时停手");

        Assert.Equal(HostedBotStop.TimedOut, bot.Reason);
        Assert.InRange(bot.ElapsedMs, 8000, 8000 + 2 * FrameMs);
        Assert.True(h.Room.Hits >= 3, "一直有命中,所以没有被当成没进展");
        Assert.Equal(0, h.Room.Kills);
        Assert.Empty(h.Posted);
    }

    [Fact]
    public void Clearing_the_room_without_a_settlement_ends_with_no_settlement_and_says_all_were_reported()
    {
        using var h = new Hosted(ThreeMonsters);
        var bot = new HostedRoomBot(noProgressLimitMs: 3000);

        Drive(bot, h.Run, () => bot.Stopped, "怪都死了、结算不来:停手");

        Assert.Equal(HostedBotStop.NoSettlement, bot.Reason);
        Assert.Equal(3, h.Run.Reported);
        Assert.Equal(0, h.Room.AliveCount);
        Assert.Equal(SettlementChoice.None, h.Run.Choice);
        Assert.Contains("reason=NoSettlement", bot.LogLine());
        Assert.Contains("reported=3", bot.LogLine());
    }

    [Fact]
    public void The_whole_segment_limit_stops_the_bot_on_the_frame_where_the_elapsed_time_reaches_it_and_not_one_frame_later()
    {
        using var h = new Hosted(new[] { (900, 100) }, frozen: true);                      // 够不着、没进展;无进展上限比整段上限长,所以只有整段上限会让它停手
        var bot = new HostedRoomBot(totalLimitMs: 5000, noProgressLimitMs: 6000);

        Drive(bot, h.Run, () => bot.Stopped, "整段上限到点时停手", frameMs: 100);           // 100 毫秒一帧:累计的时间全是整数,没有浮点误差

        Assert.Equal(HostedBotStop.TimedOut, bot.Reason);
        Assert.Equal(5000.0, bot.ElapsedMs);                                               // 累计时间等于上限的那一帧就停(>=),不是再多走一帧
    }

    [Fact]
    public void The_no_progress_limit_stops_the_bot_on_the_frame_exactly_one_limit_after_the_last_progress()
    {
        using var h = new Hosted(ThreeMonsters);                                           // 三只都打死之后再没有进展,结算又不来
        var bot = new HostedRoomBot(noProgressLimitMs: 3000);
        long mark = -1;
        double lastProgressAt = 0.0;
        int frames = 0;
        while (!bot.Stopped && frames++ < 1200)
        {
            bot.Advance(h.Run, 100);                                                       // 100 毫秒一帧:累计的时间全是整数
            long now = (long)h.Room.Hits + h.Room.Kills + h.Room.ObjectsDestroyed + h.Run.Reported + h.Run.Dropped;   // 进展 = 命中、击杀、上报(含被拒收)、打翻物件
            if (now != mark)
            {
                mark = now;
                lastProgressAt = bot.ElapsedMs;
            }
        }

        Assert.Equal(HostedBotStop.NoSettlement, bot.Reason);
        Assert.Equal(3, h.Run.Reported);
        Assert.True(lastProgressAt > 0.0);
        Assert.Equal(lastProgressAt + 3000.0, bot.ElapsedMs);                              // 距最后一次进展恰好一个上限的那一帧就停(>=),不是再多走一帧
    }

    [Fact]
    public void The_limits_must_be_finite_positive_numbers_so_the_bot_can_never_be_told_to_wait_forever()
    {
        Assert.Equal((HostedRoomBot.DefaultTotalLimitMs, HostedRoomBot.DefaultNoProgressLimitMs), (new HostedRoomBot().TotalLimitMs, new HostedRoomBot().NoProgressLimitMs));
        Assert.True(HostedRoomBot.DefaultNoProgressLimitMs < HostedRoomBot.DefaultTotalLimitMs);
        foreach (double bad in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new HostedRoomBot(totalLimitMs: bad));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HostedRoomBot(noProgressLimitMs: bad));
        }
        var ok = new HostedRoomBot(1, 1);
        Assert.Equal((1.0, 1.0), (ok.TotalLimitMs, ok.NoProgressLimitMs));
    }

    [Fact]
    public void The_stopped_reason_never_changes_and_the_elapsed_time_stops_counting()
    {
        using var h = new Hosted(new[] { (900, 100) }, frozen: true);
        var bot = new HostedRoomBot(totalLimitMs: 1500, noProgressLimitMs: 1000);
        Drive(bot, h.Run, () => bot.Stopped, "无进展停手");
        HostedBotStop first = bot.Reason;
        double elapsed = bot.ElapsedMs;

        h.Settled = true;                                                                  // 之后外面再说"结算收齐"也不改原因
        Idle(bot, h.Run, 5000);

        Assert.Equal(first, bot.Reason);
        Assert.Equal(HostedBotStop.NoProgress, first);
        Assert.Equal(elapsed, bot.ElapsedMs);
        Assert.True(h.Run.Finished);                                                       // 托管局自己照样看到结束(时间照走)
    }

    // ---------------------------------------------------------------------------------------------
    // 四、节奏与决策:与离线同一份
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_bot_plays_the_same_fight_at_any_frame_length_because_it_advances_in_fixed_beats()
    {
        (int Hits, int Kills, double X, double Y, int Steps, string Posted) Play(Func<double> frameMs)
        {
            using var h = new Hosted(ThreeMonsters, settleAfter: 3);                      // 最后一只上报的那一步宣布结算:自动机在同一个心跳停手,与帧长无关
            var bot = new HostedRoomBot();
            for (int frame = 0; frame < 400000 && !bot.Stopped; frame++)
            {
                bot.Advance(h.Run, frameMs());
            }
            Assert.Equal(HostedBotStop.Settled, bot.Reason);
            return (h.Room.Hits, h.Room.Kills, h.Room.Hero.X, h.Room.Hero.Y, bot.Steps, string.Join(",", h.Posted));
        }

        var reference = Play(() => CombatScript.StepMs);
        Assert.True(reference.Steps > 40);
        Assert.Equal(reference, Play(() => 5));
        Assert.Equal(reference, Play(() => 33.3));
        Assert.Equal(reference, Play(() => 100));
        var random = new Random(11);
        Assert.Equal(reference, Play(() => 1 + random.NextDouble() * 99));
    }

    [Fact]
    public void A_frame_adds_at_most_the_beat_cap_and_short_or_negative_frames_add_none()
    {
        using var h = new Hosted(new[] { (900, 100) });
        var bot = new HostedRoomBot();

        Assert.Equal(0, bot.Advance(h.Run, CombatScript.StepMs * 0.99));                 // 不足一个心跳
        Assert.Equal(1, bot.Advance(h.Run, CombatScript.StepMs * 0.02));                 // 凑够了
        int burst = bot.Advance(h.Run, 5000);                                              // 卡了 5 秒:只补 200 毫秒
        Assert.InRange(burst, DungeonBot.MaxBeatsPerFrame - 1, DungeonBot.MaxBeatsPerFrame);
        Assert.Equal(0, bot.Advance(h.Run, 0));
        Assert.Equal(0, bot.Advance(h.Run, -50));                                          // 负的时间当 0
        Assert.Equal(0, bot.Advance(h.Run, double.NaN));
        Assert.Throws<ArgumentNullException>(() => bot.Advance(null!, 16));
    }

    [Fact]
    public void The_room_decision_is_the_same_code_as_the_offline_one_step_for_step_while_monsters_live()
    {
        // 差分对拍:同一个离线副本会话,一个自动机走离线的 Next,另一个"影子"自动机在同一时刻对同一间房走 NextInRoom;
        // 房间里还有活着的怪时,每一步的方向、出手与意图必须一模一样(影子一步不落地跟着,所以两边的步数、内部状态始终对齐)。
        byte[] Wall(int cx, int cy, int tile)
        {
            if (tile != 2)
            {
                return null;
            }
            var pass = new byte[14 * 30];
            for (int r = 0; r < 25; r++)
            {
                for (int c = 0; c < 6; c++)
                {
                    pass[r * 14 + c] = 2;
                }
            }
            return pass;
        }

        // MinSteps:各场景至少要真对拍这么多步(实测 35 / 35 / 269),防止场景退化成一两步就打完而对拍形同虚设
        var scenarios = new List<(string Name, (int X, int Y)[] Monsters, Func<int, int, int, byte[]> Pass, int MinSteps)>
        {
            ("两只怪、空旷", new[] { (340, 100), (420, 120) }, null, 20),
            ("怪在左边背对着(要先退再转)", new[] { (297, 100) }, null, 20),
            ("怪在墙后:直线走不通、改走寻路", new[] { (600, 100) }, Wall, 150),
        };
        foreach (var scenario in scenarios)
        {
            var monsters = scenario.Monsters;
            using var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.RingLayout(), (cx, cy) => cx == 0 && cy == 0 ? monsters : DungeonSessionTests.None(),
                hp: 40, passOf: scenario.Pass);
            DungeonSession s = d.Session;
            var offline = new DungeonBot(false);
            var shadow = new DungeonBot(false);
            int compared = 0;
            for (int step = 0; step < 60 * 90 && s.Current.AliveCount > 0; step++)
            {
                bool a = offline.Next(s, out int ax, out int ay);
                bool b = shadow.NextInRoom(s.Current, out int bx, out int by);
                Assert.True((a, ax, ay, offline.Intent) == (b, bx, by, shadow.Intent),
                    scenario.Name + " 第 " + step + " 步:离线 (" + a + "," + ax + "," + ay + "," + offline.Intent + ") 房间 (" + b + "," + bx + "," + by + "," + shadow.Intent + ")");
                s.Update(CombatScript.StepMs, ax, ay, a);
                compared++;
            }
            Assert.Equal(0, s.Current.AliveCount);
            Assert.True(compared >= scenario.MinSteps, scenario.Name + ":对拍的步数太少(" + compared + ")");
            Assert.Equal(offline.Steps, shadow.Steps);
        }
    }

    [Fact]
    public void The_offline_bot_decides_to_press_attack_every_sixth_beat_while_a_monster_is_in_line_and_so_does_the_room_decision()
    {
        // 钉住离线自动机的出手间隔(DungeonBot.AttackEvery = 6):既有的离线自动机测试没有一条会因为它变了而失败(变异 M22 实测),
        // 而"离线决策的数值不变"是这次改动的红线之一。肉墙站在主角够得着的地方,主角原地不动、一直挥;这里只看自动机"按不按攻击键"的决定。
        using var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.RingLayout(), (cx, cy) => cx == 0 && cy == 0 ? new[] { (340, 100) } : DungeonSessionTests.None(), hp: 10_000_000);
        DungeonSession s = d.Session;
        var offline = new DungeonBot(false);
        var shadow = new DungeonBot(false);
        var offlinePresses = new List<int>();
        var roomPresses = new List<int>();
        for (int step = 0; step < 60; step++)
        {
            bool a = offline.Next(s, out int ax, out int ay);
            bool b = shadow.NextInRoom(s.Current, out int bx, out int by);
            if (a)
            {
                offlinePresses.Add(step);
            }
            if (b)
            {
                roomPresses.Add(step);
            }
            Assert.Equal((0, 0), (ax, ay));                                                // 够得着、不用走位
            Assert.Equal((0, 0), (bx, by));
            s.Update(CombatScript.StepMs, ax, ay, a);
        }

        int[] expected = Enumerable.Range(0, 10).Select(i => 5 + 6 * i).ToArray();       // 第 5、11、17……59 步
        Assert.Equal(expected, offlinePresses.ToArray());
        Assert.Equal(expected, roomPresses.ToArray());
        Assert.Equal(6, DungeonBot.AttackEvery);
    }

    [Fact]
    public void In_a_cleared_hosted_room_the_decision_is_to_stand_still_while_offline_it_would_walk_to_the_exit()
    {
        using var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.RingLayout(), (cx, cy) => DungeonSessionTests.None());
        DungeonSession s = d.Session;
        var hosted = new DungeonBot(false);
        var offline = new DungeonBot(false);

        bool attack = hosted.NextInRoom(s.Current, out int hx, out int hy);
        Assert.Equal((false, 0, 0), (attack, hx, hy));                                     // 没有怪、没有迷宫:原地不动

        offline.Next(s, out int ox, out int oy);
        Assert.NotEqual((0, 0), (ox, oy));                                                 // 离线同一间空房会朝首领格的出口走(路线没动)
    }

    [Fact]
    public void The_room_decision_handles_a_dead_hero_like_offline_and_refuses_a_missing_room()
    {
        using var h = new Hosted(new[] { (340, 100) }, vitals: new HeroVitals(1, 1000, 500, 0, 0, 0));
        var bot = new DungeonBot(false);
        h.Room.Vitals.Damage(h.Room.Vitals.Hp);

        Assert.False(bot.NextInRoom(h.Room, out int dx, out int dy));
        Assert.Equal((0, 0), (dx, dy));
        Assert.Equal("等待复活窗口", bot.Intent);

        Assert.Throws<ArgumentNullException>(() => bot.NextInRoom(null!, out _, out _));
        Assert.Equal(1, bot.Steps);                                                        // 房间为空的那次调用不计步
    }

    // ---------------------------------------------------------------------------------------------
    // 五、日志与开关
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_log_line_has_only_state_names_and_counts()
    {
        using var h = new Hosted(ThreeMonsters, settleAfter: 3);
        var bot = new HostedRoomBot();

        Assert.StartsWith(HostedRoomBot.LogPrefix, bot.LogLine());
        Assert.StartsWith("DNF_NET stage=room bot state=running reason=None steps=0 ", bot.LogLine());

        Drive(bot, h.Run, () => bot.Stopped, "结算后停手");
        string line = bot.LogLine();

        Assert.StartsWith("DNF_NET stage=room bot state=stopped reason=Settled steps=", line);
        Assert.Contains(" reported=3 dropped=0 alive=0 ", line);
        Assert.Contains(" kills=3 hero=up", line);
        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("map/", line);
    }

    [Fact]
    public void The_host_gets_the_stop_line_exactly_once_and_nothing_while_the_bot_is_still_fighting()
    {
        using var h = new Hosted(ThreeMonsters, settleAfter: 3);
        var bot = new HostedRoomBot();

        Assert.Null(bot.TakeLogLine(false));                                               // 还在打、不是收尾:不写
        Drive(bot, h.Run, () => h.Posted.Count >= 1, "已经开打、有击杀");
        Assert.Null(bot.TakeLogLine(false));

        Drive(bot, h.Run, () => bot.Stopped, "结算后停手");
        string line = bot.TakeLogLine(false);
        Assert.StartsWith("DNF_NET stage=room bot state=stopped reason=Settled ", line);
        Assert.Equal(bot.LogLine(), line);
        Assert.Null(bot.TakeLogLine(false));                                               // 只给一次
        Assert.Null(bot.TakeLogLine(true));                                                // 收尾时也不会重复
        Idle(bot, h.Run, 500);
        Assert.Null(bot.TakeLogLine(false));
    }

    [Fact]
    public void At_quit_a_bot_that_is_still_fighting_leaves_one_running_line_and_nothing_after()
    {
        using var h = new Hosted(ThreeMonsters);
        var bot = new HostedRoomBot();
        Idle(bot, h.Run, 300);

        string line = bot.TakeLogLine(true);

        Assert.StartsWith("DNF_NET stage=room bot state=running reason=None ", line);
        Assert.Null(bot.TakeLogLine(true));
        Assert.Null(bot.TakeLogLine(false));
    }

    [Fact]
    public void The_switch_only_counts_when_the_bot_the_net_and_the_net_dungeon_are_all_on_and_names_the_first_missing_one()
    {
        Assert.Null(HostedRoomBot.IgnoredReason(true, true, true));
        Assert.Equal("NeedsDnfbot", HostedRoomBot.IgnoredReason(false, true, true));
        Assert.Equal("NeedsDnfbot", HostedRoomBot.IgnoredReason(false, false, false));
        Assert.Equal("NeedsDnfnet", HostedRoomBot.IgnoredReason(true, false, true));
        Assert.Equal("NeedsDnfnet", HostedRoomBot.IgnoredReason(true, false, false));
        Assert.Equal("NeedsDnfnetdungeon", HostedRoomBot.IgnoredReason(true, true, false));
    }

    [Fact]
    public void The_smash_switch_is_handed_to_the_inner_offline_bot()
    {
        var bot = new HostedRoomBot();
        Assert.False(bot.SmashObjects);
        bot.SmashObjects = true;
        Assert.True(bot.SmashObjects);
    }
}
