using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 流程自动机(<see cref="FlowBot"/>)对联网房间(<see cref="HostedRoomRun"/>)的接线(r16-onlinebot):
/// 开关(<see cref="FlowBot.HostedRooms"/>)关着时对联网房间一概不动、与以前一字不变;开着时交给 <see cref="HostedRoomBot"/> 打,
/// 联网结算选定回城、自动机放弃(够不着、整段超时、主角倒下、失败)或这一局被外面切走时流程走完(<see cref="FlowBot.Done"/>),不会无限挂着。
/// 房间是合成的(<see cref="HostedRoomBotTests.Hosted"/>),"联网"一侧是委托;与联网层的真接线见客户端程序集的端到端测试。
/// </summary>
public class FlowBotHostedTests : IDisposable
{
    private const double FrameMs = 1000.0 / 60.0;
    private const int MaxFrames = 60 * 60 * 15;                  // 最多模拟 15 分钟,防止死循环

    private static readonly DungeonEntry A = new DungeonEntry("dungeon/test/a.dgn", "AAA");
    private static readonly (int X, int Y)[] ThreeMonsters = { (340, 100), (420, 120), (500, 100) };

    private readonly List<DungeonSessionTests.Dungeon> _dungeons = new List<DungeonSessionTests.Dungeon>();

    public void Dispose()
    {
        foreach (DungeonSessionTests.Dungeon d in _dungeons)
        {
            d.Dispose();
        }
    }

    /// <summary>副本工厂交出这一间联网房间(只交一次,像宿主的 TakeNetRun);之后再要副本就造离线的两格迷你副本。</summary>
    private GameFlow NewFlow(HostedRoomBotTests.Hosted net, List<string> started = null)
    {
        bool given = false;
        return new GameFlow(
            () => new GameFlowTests.FakeHero("town"),
            (path, run) =>
            {
                started?.Add(path + "#" + run);
                if (net != null && !given)
                {
                    given = true;
                    return net.Run;
                }
                var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.TwoRoomBossLayout(), (cx, cy) => DungeonSessionTests.None());
                _dungeons.Add(d);
                return d.Session;
            },
            new[] { A });
    }

    private static int Drive(FlowBot bot, GameFlow flow, Func<bool> until = null, double frameMs = FrameMs)
    {
        int frames = 0;
        while (!(until != null ? until() : bot.Done) && frames < MaxFrames)
        {
            bot.Advance(flow, frameMs);
            frames++;
        }
        return frames;
    }

    private static (GameStage From, GameStage To, string Reason)[] Tour(GameFlow flow)
    {
        return flow.History.Select(e => (e.From, e.To, e.Reason)).ToArray();
    }

    [Fact]
    public void The_hosted_switch_is_off_by_default_and_the_limits_default_to_the_hosted_bots_own()
    {
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown });

        Assert.False(bot.HostedRooms);
        Assert.Null(bot.HostedBot);
        Assert.Equal((HostedRoomBot.DefaultTotalLimitMs, HostedRoomBot.DefaultNoProgressLimitMs), (bot.HostedTotalLimitMs, bot.HostedNoProgressLimitMs));
    }

    [Fact]
    public void With_the_switch_off_the_bot_leaves_a_hosted_room_alone_exactly_as_before()
    {
        using var net = new HostedRoomBotTests.Hosted(ThreeMonsters) { Settled = true };    // 就算外面说结算收齐了,没人推进它,房间也原地不动
        GameFlow flow = NewFlow(net);
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown });
        flow.StartDungeon(A.Path, "net room");
        double x = net.Room.Hero.X, y = net.Room.Hero.Y;

        for (int i = 0; i < 60 * 30; i++)
        {
            bot.Advance(flow, FrameMs);
        }

        Assert.False(bot.Done);
        Assert.Null(bot.HostedBot);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Same(net.Run, flow.Run);
        Assert.Equal((x, y), (net.Room.Hero.X, net.Room.Hero.Y));
        Assert.Equal(0, net.Room.Hits);
        Assert.Empty(net.Posted);
        Assert.False(net.Run.Finished);
        Assert.Equal(SettlementChoice.None, net.Run.Choice);
        Assert.Equal((0, 0, 1), (flow.Settlements, flow.Failures, flow.Runs));
    }

    [Fact]
    public void With_the_switch_on_the_bot_clears_the_room_posts_every_kill_and_the_flow_returns_to_town_once_the_settlement_is_in()
    {
        using var net = new HostedRoomBotTests.Hosted(ThreeMonsters, returnDelayMs: 1000, settleAfter: 3);
        var started = new List<string>();
        GameFlow flow = NewFlow(net, started);
        var bot = new FlowBot(false, new[] { SettlementChoice.Retry, SettlementChoice.ReturnToTown }) { HostedRooms = true };   // 结算清单对联网房间不起作用
        flow.StartDungeon(A.Path, "net room");

        int frames = Drive(bot, flow);

        Assert.True(bot.Done, "流程没有在 " + MaxFrames + " 帧内走完");
        Assert.True(frames < MaxFrames);
        Assert.Equal(new[] { 0, 1, 2 }, net.Posted.OrderBy(i => i).ToArray());
        Assert.Equal(3, net.Room.Kills);
        Assert.Equal((net.Room.Kills, 0), (net.Run.Reported, net.Run.Dropped));            // 上报数 == 击杀数
        Assert.Equal(HostedBotStop.Settled, bot.HostedBot.Reason);
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal((GameStage.Dungeon, GameStage.Town, "choice ReturnToTown"), Tour(flow).Last());
        Assert.Equal((1, 0, 1), (flow.Settlements, flow.Failures, flow.Runs));
        Assert.Equal(0, bot.ChoicesMade);                                                  // 清单没动:联网房间只有一局,回城就是终点
        Assert.Equal(new[] { A.Path + "#0" }, started);                                    // 没有"再次挑战"、也没有接着去打离线副本

        int serial = flow.StageSerial;
        int posted = net.Posted.Count;
        for (int i = 0; i < 600; i++)
        {
            bot.Advance(flow, FrameMs);
        }
        Assert.Equal((serial, posted, 1), (flow.StageSerial, net.Posted.Count, flow.Runs)); // 走完之后再推也没有动作
    }

    [Fact]
    public void The_hosted_limits_are_handed_to_the_hosted_bot_and_a_bot_that_cannot_reach_the_monsters_gives_up_with_the_flow_left_in_the_room()
    {
        using var net = new HostedRoomBotTests.Hosted(new[] { (900, 100) }, frozen: true);   // 主角的可走区域只有脚下一小块,够不着怪
        GameFlow flow = NewFlow(net);
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown }) { HostedRooms = true, HostedNoProgressLimitMs = 3000, HostedTotalLimitMs = 100000 };
        flow.StartDungeon(A.Path, "net room");

        Drive(bot, flow);

        Assert.True(bot.Done);
        Assert.Equal(HostedBotStop.NoProgress, bot.HostedBot.Reason);
        Assert.Equal((100000.0, 3000.0), (bot.HostedBot.TotalLimitMs, bot.HostedBot.NoProgressLimitMs));
        Assert.Equal(GameStage.Dungeon, flow.Stage);                                       // 放弃不等于回城:流程停在房间里,由宿主收尾退出,日志里写明原因
        Assert.Same(net.Run, flow.Run);
        Assert.Equal((0, 0), (flow.Settlements, flow.Failures));
        Assert.Contains("reason=NoProgress", bot.HostedBot.LogLine());
        Assert.Equal(0, net.Room.Hits);

        double elapsed = bot.HostedBot.ElapsedMs;
        for (int i = 0; i < 600; i++)
        {
            bot.Advance(flow, FrameMs);
        }
        Assert.Equal(elapsed, bot.HostedBot.ElapsedMs);                                    // 走完之后不再推进房间
    }

    [Fact]
    public void The_smash_objects_switch_of_the_flow_bot_is_handed_to_the_hosted_bot_both_ways()
    {
        foreach (bool smash in new[] { false, true })
        {
            using var net = new HostedRoomBotTests.Hosted(ThreeMonsters);
            GameFlow flow = NewFlow(net);
            var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown }) { HostedRooms = true, SmashObjects = smash };
            flow.StartDungeon(A.Path, "net room");

            bot.Advance(flow, FrameMs);

            Assert.NotNull(bot.HostedBot);
            Assert.Equal(smash, bot.HostedBot.SmashObjects);
        }
    }

    [Fact]
    public void The_whole_segment_limit_ends_a_fight_that_never_finishes_and_never_reports_a_settlement()
    {
        using var net = new HostedRoomBotTests.Hosted(new[] { (340, 100) }, hp: 10_000_000);    // 打不死的肉墙:一直在挨打,不会被当成没进展
        GameFlow flow = NewFlow(net);
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown }) { HostedRooms = true, HostedTotalLimitMs = 9000 };
        flow.StartDungeon(A.Path, "net room");

        Drive(bot, flow);

        Assert.True(bot.Done);
        Assert.Equal(HostedBotStop.TimedOut, bot.HostedBot.Reason);
        Assert.InRange(bot.HostedBot.ElapsedMs, 9000, 9000 + 2 * FrameMs);
        Assert.True(net.Room.Hits >= 3);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Equal((0, 0), (flow.Settlements, flow.Failures));
    }

    [Fact]
    public void A_hero_down_with_no_revive_window_ends_the_flow_in_the_room()
    {
        using var net = new HostedRoomBotTests.Hosted(new[] { (900, 100) }, vitals: new HeroVitals(1, 1000, 500, 0, 0, 0));
        GameFlow flow = NewFlow(net);
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown }) { HostedRooms = true };
        flow.StartDungeon(A.Path, "net room");
        Drive(bot, flow, until: () => bot.HostedBot != null && bot.HostedBot.Steps >= 12);
        Assert.False(bot.Done);

        net.Room.Vitals.Damage(net.Room.Vitals.Hp);
        bot.Advance(flow, FrameMs);

        Assert.True(bot.Done);
        Assert.Equal(HostedBotStop.HeroDown, bot.HostedBot.Reason);
        Assert.Equal(GameStage.Dungeon, flow.Stage);                                       // 没有复活窗口就没有失败计时:不会自己回城,所以由自动机收尾
        Assert.Equal((0, 0), (flow.Settlements, flow.Failures));
    }

    [Fact]
    public void A_run_that_fails_returns_to_town_counts_a_failure_and_ends_the_flow()
    {
        using var net = new HostedRoomBotTests.Hosted(ThreeMonsters, vitals: new HeroVitals(1, 1000, 500, 0, 0, 0));
        net.Room.Revive = new HeroRevive(null, new ReviveCoins(0));                        // 有复活窗口但没有币:失败计时走完整局失败
        GameFlow flow = NewFlow(net);
        var bot = new FlowBot(false, new[] { SettlementChoice.Retry }) { HostedRooms = true };
        flow.StartDungeon(A.Path, "net room");
        Drive(bot, flow, until: () => bot.HostedBot != null && bot.HostedBot.Steps >= 12);

        net.Room.Vitals.Damage(net.Room.Vitals.Hp);
        Drive(bot, flow);

        Assert.True(bot.Done);
        Assert.Equal(HostedBotStop.Failed, bot.HostedBot.Reason);
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal((GameStage.Dungeon, GameStage.Town, "dungeon fail"), Tour(flow).Last());
        Assert.Equal((0, 1, 1), (flow.Settlements, flow.Failures, flow.Runs));
    }

    [Fact]
    public void A_hosted_stage_the_flow_leaves_for_any_other_reason_ends_the_flow_instead_of_walking_on_to_offline_dungeons()
    {
        using var net = new HostedRoomBotTests.Hosted(ThreeMonsters);
        var started = new List<string>();
        GameFlow flow = NewFlow(net, started);
        var bot = new FlowBot(false, new[] { SettlementChoice.Retry, SettlementChoice.ReturnToTown }) { HostedRooms = true };
        flow.StartDungeon(A.Path, "net room");
        Drive(bot, flow, until: () => bot.HostedBot != null && bot.HostedBot.Steps >= 30);
        Assert.False(bot.Done);

        flow.EnterTown("somebody else");                                                  // 外面把流程切走了(例如系统菜单选了回城)
        bot.Advance(flow, FrameMs);

        Assert.True(bot.Done);
        Assert.Equal(GameStage.Town, flow.Stage);
        int serial = flow.StageSerial;
        for (int i = 0; i < 60 * 60; i++)
        {
            bot.Advance(flow, FrameMs);
        }
        Assert.Equal(serial, flow.StageSerial);                                            // 一分钟里什么也没做:没有走去选副本、没有再进副本
        Assert.Equal(new[] { A.Path + "#0" }, started);
        Assert.False(bot.HostedBot.Stopped);                                               // 自动机本身还没停手:是流程被切走了(宿主收尾时留一行 running)
    }

    [Fact]
    public void With_the_switch_on_an_offline_dungeon_is_played_exactly_as_with_it_off_and_no_hosted_bot_appears()
    {
        (GameStage From, GameStage To, string Reason)[] Play(bool hostedRooms, out int frames, out FlowBot bot, out int settlements)
        {
            GameFlow flow = NewFlow(null);
            bot = new FlowBot(false, new[] { SettlementChoice.Retry, SettlementChoice.SelectDungeon, SettlementChoice.ReturnToTown }) { HostedRooms = hostedRooms };
            flow.EnterTown("start");
            frames = Drive(bot, flow);
            settlements = flow.Settlements;
            return Tour(flow);
        }

        var off = Play(false, out int offFrames, out FlowBot offBot, out int offSettlements);
        var on = Play(true, out int onFrames, out FlowBot onBot, out int onSettlements);

        Assert.True(offBot.Done);
        Assert.Equal(off, on);
        Assert.Equal((offFrames, offSettlements, offBot.ChoicesMade), (onFrames, onSettlements, onBot.ChoicesMade));
        Assert.Equal(3, onSettlements);                                                    // 清单照常走完(再来一次、选副本、回城)
        Assert.Null(onBot.HostedBot);
    }
}
