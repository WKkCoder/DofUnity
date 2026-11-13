using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;

namespace Dnf.Client.Tests;

// 联网房间自动机(r16-onlinebot)的内存端到端:与 OnlineDungeonRoomTests 共用同一套装置(Rig:假服务端 + 假会话 + 联网战斗 + 合成房间),
// 只是房间不再由测试代码直接"打死怪",而是让自动机(HostedRoomBot / FlowBot)按它自己的决策去打。
// 联网一侧的往返发生在后台线程、按真实时间走;自动机的时间是模拟时间(每次轮询给一帧 100 毫秒,宿主一帧的上限),所以正常跑完的用例把两个上限设成"有限的足够大",
// 不让上限抢在网络往返之前;专门测上限的用例才用小的数。不连任何真实服务,怪编号是 9100 起的合成值。
public partial class OnlineDungeonRoomTests
{
    private const double BotFrameMs = 100.0;

    /// <summary>"有限的足够大"的上限(毫秒):构造时不许无限,又不会在正常用例里被碰到。</summary>
    private const double Plenty = 1e12;

    private static HostedRoomBot PatientBot()
    {
        return new HostedRoomBot(Plenty, Plenty);
    }

    /// <summary>每轮询一次给自动机一帧(100 毫秒),直到条件成立;超时让测试失败(失败信息里带上自动机日志行、阶段与服务端流水)。</summary>
    private static async Task DriveBot(Rig rig, HostedRoomBot bot, Func<bool> until, string what, int milliseconds = 15000)
    {
        try
        {
            await CombatKit.WaitUntil(() =>
            {
                bot.Advance(rig.Run, BotFrameMs);
                return until();
            }, what, milliseconds);
        }
        catch (Xunit.Sdk.XunitException e)
        {
            throw new Xunit.Sdk.XunitException(e.Message + " [" + bot.LogLine() + " phase=" + rig.Machine?.Phase + " choice=" + rig.Run.Choice
                + " cmd42=" + rig.Session.Commands(42) + " cmd49=" + rig.Session.Commands(49) + " journal=" + string.Join(",", rig.Server.Journal) + "]");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 五、自动机打联网房间:打死怪、上报数 == 击杀数、结算信号到后停手
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_bot_kills_every_monster_of_a_synthetic_online_room_and_each_kill_reaches_the_server_exactly_once()
    {
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok, Ok));
        Assert.Null(rig.Run.Room.Vitals);                                          // 与 -dnfnodamage 一样的房间:主角没有生命值,怪物打不到他
        HostedRoomBot bot = PatientBot();

        await DriveBot(rig, bot, () => rig.Run.Choice == SettlementChoice.ReturnToTown, "自动机打完、联网结算收齐之后回城");
        await CombatKit.WaitUntil(() => rig.Combat.Ended, "战斗跑器收场");

        Assert.Equal(3, rig.Run.Room.Kills);                                       // 本地杀了三只
        Assert.Equal(3, rig.Run.Reported);                                         // 交了三只:上报数 == 击杀数
        Assert.Equal(0, rig.Run.Dropped);
        Assert.Equal(3, rig.Session.Commands(42));                                 // 假服务端收到三个击杀包:包数 == 击杀数
        Assert.Equal(ids, rig.KillsSeenByServer.Select(k => (int)k.MobId).OrderBy(i => i).ToArray());   // 每只恰好一次,各用自己的服务端编号
        Assert.Equal(ids.Select(i => "kill:" + i).OrderBy(s => s).ToArray(), rig.Server.Journal.Take(3).OrderBy(s => s).ToArray());
        Assert.Equal(new[] { "clear", "settle" }, rig.Server.Journal.Skip(3).ToArray());   // 三只都报了,服务端才清图、才结算
        Assert.Equal(1, rig.Session.Commands(49));
        Assert.Equal(0, rig.Server.PrematureSettlements);
        Assert.Equal(0, rig.Server.Refusals);
        Assert.Equal(0, rig.Machine.Blocked);
        Assert.Equal(OnlineCombatPhase.Settled, rig.Machine.Phase);
        Assert.Equal(HostedBotStop.Settled, bot.Reason);
        Assert.True(rig.Run.Room.Hits >= 3, "是自动机真的打中了怪,不是测试代码直接扣血");
    }

    [Fact]
    public async Task After_the_settlement_signal_the_bot_stops_deciding_and_sends_nothing_more_while_the_return_delay_still_runs()
    {
        int[] ids = CombatKit.Sequential(2);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok), returnDelayMs: 1500);
        HostedRoomBot bot = PatientBot();

        await DriveBot(rig, bot, () => bot.Stopped, "结算信号到后自动机停手");
        Assert.Equal(HostedBotStop.Settled, bot.Reason);
        Assert.True(rig.Run.Finished);
        Assert.Equal(SettlementChoice.None, rig.Run.Choice);                       // 回城延迟 1.5 秒还没走完
        int steps = bot.Steps;
        int hits = rig.Run.Room.Hits;
        double x = rig.Run.Room.Hero.X, y = rig.Run.Room.Hero.Y;
        string stopLine = bot.TakeLogLine(false);
        Assert.StartsWith("DNF_NET stage=room bot state=stopped reason=Settled ", stopLine);

        await DriveBot(rig, bot, () => rig.Run.Choice == SettlementChoice.ReturnToTown, "停手之后时间照走、延迟走完回城");
        for (int i = 0; i < 20; i++)
        {
            bot.Advance(rig.Run, BotFrameMs);
        }
        await rig.Settle(steps: 10);

        Assert.Equal(steps, bot.Steps);                                            // 停手之后一个决策也没有
        Assert.Equal(hits, rig.Run.Room.Hits);
        Assert.Equal((x, y), (rig.Run.Room.Hero.X, rig.Run.Room.Hero.Y));          // 主角没有再动
        Assert.Equal(2, rig.Session.Commands(42));
        Assert.Equal(1, rig.Session.Commands(49));
        Assert.Equal((2, 0), (rig.Run.Reported, rig.Run.Dropped));
        Assert.Null(bot.TakeLogLine(false));                                       // 日志只给一次
        Assert.Equal(HostedBotStop.Settled, bot.Reason);
    }

    [Fact]
    public async Task A_bot_that_clears_the_room_but_never_gets_the_settlement_ends_with_no_settlement_and_leaves_the_server_untouched_beyond_the_kills()
    {
        int[] ids = CombatKit.Sequential(2);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok), withholdClear: true);   // 服务端表清空了也不发清图通知
        var bot = new HostedRoomBot(Plenty, noProgressLimitMs: 2000);

        await DriveBot(rig, bot, () => bot.Stopped, "怪都死了、结算不来:无进展上限一到停手");
        await CombatKit.WaitUntil(() => rig.Session.Commands(42) >= 2, "两个击杀包到达假服务端");
        await rig.Settle(steps: 10);

        Assert.Equal(HostedBotStop.NoSettlement, bot.Reason);
        Assert.Equal(0, rig.Run.Room.AliveCount);
        Assert.Equal((2, 2), (rig.Run.Reported, rig.Run.Room.Kills));
        Assert.Equal(2, rig.Session.Commands(42));
        Assert.Equal(0, rig.Session.Commands(49));                                 // 服务端没清图:不会结算
        Assert.False(rig.Server.Cleared);
        Assert.Equal(SettlementChoice.None, rig.Run.Choice);                       // 不回城:由流程自动机置 Done、宿主收尾
        Assert.Equal(OnlineCombatPhase.Fighting, rig.Machine.Phase);
        string line = bot.LogLine();
        Assert.Contains("state=stopped reason=NoSettlement", line);
        Assert.Contains("reported=2 dropped=0 alive=0", line);
        Assert.DoesNotContain(ids[0].ToString(), line);                            // 日志里没有服务端编号
    }

    [Fact]
    public async Task A_record_the_room_could_not_build_keeps_the_server_from_clearing_so_the_bot_clears_what_it_can_and_ends_with_no_settlement()
    {
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Bad, Ok));              // 第二条记录本地造不出怪
        var bot = new HostedRoomBot(Plenty, noProgressLimitMs: 2000);

        await DriveBot(rig, bot, () => bot.Stopped, "能打的都打完、结算不来:停手");
        await CombatKit.WaitUntil(() => rig.Session.Commands(42) >= 2, "两个击杀包到达假服务端");

        Assert.Equal(HostedBotStop.NoSettlement, bot.Reason);
        Assert.Equal(new[] { ids[0], ids[2] }, rig.KillsSeenByServer.Select(k => (int)k.MobId).OrderBy(i => i).ToArray());   // 缺的那条记录没有报
        Assert.Equal((2, 2), (rig.Run.Reported, rig.Run.Room.Kills));
        Assert.False(rig.Server.Cleared);                                          // 服务端表里还剩一只:不清图
        Assert.Equal(1, rig.Server.Remaining);
        Assert.Equal(0, rig.Session.Commands(49));
        Assert.Equal(SettlementChoice.None, rig.Run.Choice);
    }

    // ---------------------------------------------------------------------------------------------
    // 六、超时兜底:整段上限一到就停手,不会无限挂机
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_whole_segment_limit_stops_a_bot_that_cannot_finish_in_time_and_what_it_posted_matches_what_the_server_saw()
    {
        int[] ids = CombatKit.Sequential(3);
        // 三只怪隔得远:第一只一下就倒,后两只要走过去才打得到;整段只给 0.5 秒的模拟时间,打完第一只就到点了
        using Rig rig = await Rig.Start(ids, new[] { HostedRoomFixture.Spawn(Ok, 340), HostedRoomFixture.Spawn(Ok, 600), HostedRoomFixture.Spawn(Ok, 900) });
        var bot = new HostedRoomBot(totalLimitMs: 500, noProgressLimitMs: Plenty);

        await DriveBot(rig, bot, () => bot.Stopped, "整段上限一到停手");
        int steps = bot.Steps;
        int reported = rig.Run.Reported;
        await CombatKit.WaitUntil(() => rig.Session.Commands(42) == reported, "交出去的击杀都到了假服务端");
        for (int i = 0; i < 30; i++)
        {
            bot.Advance(rig.Run, BotFrameMs);
        }
        await rig.Settle(steps: 20);

        Assert.Equal(HostedBotStop.TimedOut, bot.Reason);
        Assert.Equal(500.0, bot.ElapsedMs);                                        // 每帧 100 毫秒、累计到整 500 的那一帧就停(>=),不是再多一帧
        Assert.Equal(steps, bot.Steps);                                            // 停手之后不再决策
        Assert.InRange(reported, 1, 2);                                            // 打死了第一只、交了;其余没来得及:不可能三只都打完
        Assert.Equal(reported, rig.Session.Commands(42));                          // 交了几只、服务端就收到几个包,多一个少一个都不对
        Assert.Equal(reported, rig.Run.Room.Kills);                                // 上报数 == 击杀数
        Assert.Equal(0, rig.Session.Commands(49));
        Assert.False(rig.Server.Cleared);
        Assert.Equal(SettlementChoice.None, rig.Run.Choice);
        Assert.Contains("state=stopped reason=TimedOut", bot.LogLine());
    }

    // ---------------------------------------------------------------------------------------------
    // 七、流程自动机(FlowBot)接联网房间:开关关着不动,开着打完回城
    // ---------------------------------------------------------------------------------------------

    private static GameFlow FlowAround(Rig rig, double returnDelayMs = 0.0)
    {
        var flow = new GameFlow(
            () => new FakeTown(),
            (path, run) => rig.Room.CreateRun(plan => rig.Fixture.Create(plan), returnDelayMs),
            new[] { new DungeonEntry("dungeon/test/online.dgn", "ONLINE") });
        flow.EnterTown("start");
        flow.StartDungeon("dungeon/test/online.dgn", "net room");
        rig.Run = rig.Room.Run;
        return flow;
    }

    [Fact]
    public async Task The_flow_bot_with_the_switch_on_clears_the_online_room_and_the_flow_returns_to_town_with_the_counts_matching()
    {
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok, Ok), autoCreate: false);
        GameFlow flow = FlowAround(rig);
        var bot = new FlowBot(false, new[] { SettlementChoice.Retry }) { HostedRooms = true, HostedTotalLimitMs = Plenty, HostedNoProgressLimitMs = Plenty };
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Same(rig.Room.Run, flow.Run);

        await CombatKit.WaitUntil(() =>
        {
            bot.Advance(flow, BotFrameMs);
            return flow.Stage == GameStage.Town;
        }, "流程回到城镇");

        Assert.True(bot.Done);
        Assert.Equal((1, 0, 0), (flow.Settlements, flow.Failures, bot.ChoicesMade));
        Assert.Equal("choice ReturnToTown", flow.History[flow.History.Count - 1].Reason);
        Assert.Equal(3, rig.Run.Room.Kills);
        Assert.Equal((3, 0), (rig.Run.Reported, rig.Run.Dropped));
        Assert.Equal(3, rig.Session.Commands(42));
        Assert.Equal(1, rig.Session.Commands(49));
        Assert.Equal(HostedBotStop.Settled, bot.HostedBot.Reason);

        int serial = flow.StageSerial;
        for (int i = 0; i < 20; i++)
        {
            bot.Advance(flow, BotFrameMs);
        }
        Assert.Equal(serial, flow.StageSerial);                                    // 回城之后自动机不再有动作(联网房间只有一局)
        Assert.Equal(3, rig.Session.Commands(42));
    }

    [Fact]
    public async Task The_flow_bot_with_the_switch_off_leaves_the_online_room_exactly_as_it_was_before_this_work()
    {
        int[] ids = CombatKit.Sequential(3);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok, Ok), autoCreate: false);
        GameFlow flow = FlowAround(rig);
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown });     // 开关缺省关
        double x = rig.Run.Room.Hero.X, y = rig.Run.Room.Hero.Y;
        string before = rig.Room.LogLine();

        for (int i = 0; i < 150; i++)
        {
            bot.Advance(flow, BotFrameMs);
            await Task.Delay(5);
        }

        Assert.False(bot.Done);
        Assert.Null(bot.HostedBot);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Equal(0, rig.Run.Room.Hits);                                        // 没人推进房间:一击没出、主角没动、怪没被打
        Assert.Equal((x, y), (rig.Run.Room.Hero.X, rig.Run.Room.Hero.Y));
        Assert.Equal((0, 0), (rig.Run.Reported, rig.Run.Dropped));
        Assert.Equal(0, rig.Session.Commands(42));
        Assert.Empty(rig.Server.Journal);                                          // 服务端什么也没收到
        Assert.Equal(OnlineCombatPhase.Fighting, rig.Machine.Phase);
        Assert.Equal(before, rig.Room.LogLine());                                  // 房间接线日志也一字没变
    }

    [Fact]
    public async Task The_flow_bot_gives_up_in_the_online_room_when_the_whole_segment_runs_out_and_the_flow_is_left_in_the_room()
    {
        int[] ids = CombatKit.Sequential(2);
        using Rig rig = await Rig.Start(ids, Templates(Ok, Ok), autoCreate: false);
        GameFlow flow = FlowAround(rig);
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown }) { HostedRooms = true, HostedTotalLimitMs = 300, HostedNoProgressLimitMs = Plenty };

        await CombatKit.WaitUntil(() =>
        {
            bot.Advance(flow, BotFrameMs);
            return bot.Done;
        }, "整段超时之后流程自动机收尾");

        Assert.Equal(HostedBotStop.TimedOut, bot.HostedBot.Reason);
        Assert.Equal(GameStage.Dungeon, flow.Stage);                               // 没有结算就不回城:停在房间里,宿主据此收尾退出
        Assert.Equal((0, 0), (flow.Settlements, flow.Failures));
        Assert.Equal(SettlementChoice.None, rig.Run.Choice);
        Assert.Contains("reason=TimedOut", bot.HostedBot.LogLine());
    }
}
