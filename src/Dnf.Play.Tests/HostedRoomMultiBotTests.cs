using System.Text.RegularExpressions;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网多房间副本里的自动机(<see cref="HostedRoomBot"/>,r18-hostmulti):清完房间的怪之后自己走向通向下一间的门、触发换房、站着等结果、换房被拒就再来、
/// 进新房间接着打;整段上限与无进展上限仍按整局算,宿主放弃或出口走不成都有明确的停手原因,日志只有状态名与只数。
/// 房间是带出口的合成房间(<see cref="HostedGatedRoomFixture"/>),"服务端"是一个 <see cref="HostedRoomRun.Pump"/>:排了换房请求之后过几步就把下一间房换上(或一直不回应)。
/// 单房间的老行为由 <see cref="HostedRoomBotTests"/> 原样把关(这里不动它)。数据全是合成的,不含任何原版数据与服务端编号。
/// </summary>
public class HostedRoomMultiBotTests
{
    private const double FrameMs = 1000.0 / 60.0;

    /// <summary>一间房的剧本:几只怪、摆哪几侧出口、通向下一间的出口(最后一间 null)、主角从哪一侧进来(第一间 null)。</summary>
    internal readonly struct RoomPlan
    {
        public RoomPlan(int monsters, GateSide[] gates, GateSide? exit, GateSide? entry)
        {
            Monsters = monsters;
            Gates = gates;
            Exit = exit;
            Entry = entry;
        }

        public int Monsters { get; }
        public GateSide[] Gates { get; }
        public GateSide? Exit { get; }
        public GateSide? Entry { get; }
    }

    private static readonly GateSide[] LeftRight = { GateSide.Left, GateSide.Right };

    /// <summary>一座多房间副本的"服务端":排了换房请求之后等 <see cref="AnswerAfterSteps"/> 步,把剧本里的下一间房换上;<see cref="AnswerAfterSteps"/> 为负就一直不回应。</summary>
    internal sealed class Dungeon : IDisposable
    {
        private readonly HostedRoomMultiRunTests.Rig _rig;
        private readonly RoomPlan[] _rooms;
        private int _waited;

        public Dungeon(params RoomPlan[] rooms)
        {
            _rooms = rooms;
            _rig = new HostedRoomMultiRunTests.Rig(rooms[0].Monsters, rooms[0].Exit, rooms[0].Gates);
            _rig.Run.Pump = Pump;
        }

        public HostedRoomMultiRunTests.Rig Rig => _rig;
        public HostedRoomRun Run => _rig.Run;

        /// <summary>排了换房请求之后几步给新房间;负数不回应。</summary>
        public int AnswerAfterSteps { get; set; } = 6;

        /// <summary>下一间房是剧本里的第几间(进过的最后一间 + 1)。</summary>
        public int Next { get; private set; } = 1;

        private void Pump(HostedRoomRun run)
        {
            if (!run.MovePending)
            {
                _waited = 0;
                return;
            }
            if (AnswerAfterSteps < 0 || _waited++ < AnswerAfterSteps || Next >= _rooms.Length)
            {
                return;
            }
            RoomPlan plan = _rooms[Next];
            run.EnterRoom(_rig.NewRoom(plan.Monsters, plan.Gates), Next, 900 + Next, plan.Entry, plan.Exit);
            Next++;
            _waited = 0;
        }

        public void Dispose()
        {
            _rig.Dispose();
        }
    }

    /// <summary>按 <paramref name="frameMs"/> 一帧一帧推进,直到条件成立;超过 <paramref name="maxSeconds"/> 模拟秒还不成立就让测试失败。</summary>
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

    private static void Idle(HostedRoomBot bot, HostedRoomRun run, double ms)
    {
        for (double t = 0; t < ms; t += FrameMs)
        {
            bot.Advance(run, FrameMs);
        }
    }

    // ------------------------------------------------------------------ 清完怪之后自己走向门、触发换房

    [Fact]
    public void After_the_last_monster_dies_the_bot_walks_to_the_planned_door_and_triggers_the_move_exactly_once()
    {
        using var d = new Dungeon(new RoomPlan(3, LeftRight, GateSide.Right, null), new RoomPlan(1, LeftRight, null, GateSide.Left)) { AnswerAfterSteps = -1 };
        var bot = new HostedRoomBot();

        Drive(bot, d.Run, () => d.Rig.Requests.Count > 0, "清完怪、走到门口、触发换房");

        (GateSide Side, int X, int Y) request = Assert.Single(d.Rig.Requests);
        Assert.Equal(GateSide.Right, request.Side);                               // 计划的出口
        Assert.True(request.X >= HostedGatedRoomFixture.GatePosition(GateSide.Right)[0] - DungeonSession.TriggerReachSide, "主角在门口");
        Assert.Equal(3, d.Run.Reported);                                          // 先把怪打完交完才去开门
        Assert.True(d.Run.RoomCleared);
        Assert.True(d.Run.MovePending);
        Assert.False(bot.Stopped);
    }

    [Fact]
    public void While_a_move_is_pending_the_bot_stands_still_and_never_asks_again()
    {
        using var d = new Dungeon(new RoomPlan(1, LeftRight, GateSide.Right, null), new RoomPlan(1, LeftRight, null, GateSide.Left)) { AnswerAfterSteps = -1 };
        var bot = new HostedRoomBot();
        Drive(bot, d.Run, () => d.Run.MovePending, "排上了换房请求");
        (double x, double y) at = (d.Run.Hero.X, d.Run.Hero.Y);

        Idle(bot, d.Run, 5000);

        Assert.Single(d.Rig.Requests);
        Assert.Equal(at, (d.Run.Hero.X, d.Run.Hero.Y));                           // 站着等
        Assert.Equal(1, d.Run.MoveRequests);
        Assert.False(bot.Stopped);
    }

    [Fact]
    public void A_refused_move_makes_the_bot_ask_again_once_the_cooldown_is_over_and_it_is_still_at_the_door()
    {
        using var d = new Dungeon(new RoomPlan(1, LeftRight, GateSide.Right, null), new RoomPlan(1, LeftRight, null, GateSide.Left)) { AnswerAfterSteps = -1 };
        var bot = new HostedRoomBot();
        Drive(bot, d.Run, () => d.Run.MovePending, "排上了换房请求");
        double refusedAt = bot.ElapsedMs;

        d.Run.MoveRefused();
        Drive(bot, d.Run, () => d.Rig.Requests.Count == 2, "冷却过后在门口再排一次");

        Assert.True(bot.ElapsedMs - refusedAt >= HostedRoomRun.RetryCooldownMs - 1.0, "冷却期间不再排");
        Assert.True(bot.ElapsedMs - refusedAt < HostedRoomRun.RetryCooldownMs + 1000.0, "冷却一过就再排,不用走远");
        Assert.Equal(GateSide.Right, d.Rig.Requests[1].Side);
        Assert.Equal((2, 1), (d.Run.MoveRequests, d.Run.MoveRefusals));
        Assert.False(bot.Stopped);
    }

    // ------------------------------------------------------------------ 整局流程:三间房,换房、接着打、最后清图结算

    [Fact]
    public void A_whole_three_room_dungeon_is_cleared_room_by_room_and_the_totals_add_up()
    {
        using var d = new Dungeon(
            new RoomPlan(1, LeftRight, GateSide.Right, null),
            new RoomPlan(2, LeftRight, GateSide.Right, GateSide.Left),
            new RoomPlan(1, LeftRight, null, GateSide.Left));
        var bot = new HostedRoomBot();

        Drive(bot, d.Run, () => d.Run.RoomIndex == 2 && d.Run.RoomCleared, "三间房都清了");
        d.Rig.Done = true;                                                        // "服务端"在最后一间清图结算
        Drive(bot, d.Run, () => d.Run.Choice == SettlementChoice.ReturnToTown, "结算收齐之后回城");

        Assert.Equal(new[] { (0, 0), (1, 0), (1, 1), (2, 0) }, d.Rig.Kills.OrderBy(k => k.Room).ThenBy(k => k.Id).ToArray());   // 每间房的每只怪各一次,按房间序号交
        Assert.Equal(4, d.Run.Reported);                                          // 整局累计
        Assert.Equal(0, d.Run.Dropped);
        Assert.Equal(1, d.Run.RoomReported);                                      // 最后一间的
        Assert.Equal(3, d.Run.RoomsEntered);
        Assert.Equal(2, d.Run.MoveRequests);
        Assert.Equal(new[] { GateSide.Right, GateSide.Right }, d.Rig.Requests.Select(r => r.Side).ToArray());
        Assert.Equal(HostedBotStop.Settled, bot.Reason);
        Assert.False(d.Run.Failed);
    }

    [Fact]
    public void The_bot_does_not_walk_to_the_door_of_a_room_that_has_no_planned_exit()
    {
        // 最后一间(或单房间):出口没定就是没有门可走,打完原地等结算,与以前一样
        using var d = new Dungeon(new RoomPlan(1, LeftRight, null, null));
        var bot = new HostedRoomBot();
        Drive(bot, d.Run, () => d.Run.Reported == 1, "打死唯一的怪");
        Idle(bot, d.Run, 500);
        (double x, double y) at = (d.Run.Hero.X, d.Run.Hero.Y);

        Idle(bot, d.Run, 3000);

        Assert.Equal(at, (d.Run.Hero.X, d.Run.Hero.Y));
        Assert.Empty(d.Rig.Requests);
        Assert.False(bot.Stopped);
    }

    // ------------------------------------------------------------------ 上限仍按整局算

    [Fact]
    public void When_the_move_never_gets_an_answer_the_bot_stops_with_NoExit_one_no_progress_limit_after_the_request()
    {
        using var d = new Dungeon(new RoomPlan(1, LeftRight, GateSide.Right, null), new RoomPlan(1, LeftRight, null, GateSide.Left)) { AnswerAfterSteps = -1 };
        var bot = new HostedRoomBot(noProgressLimitMs: 12000.0);                  // 比"打怪加走到门口"的几秒宽裕

        Drive(bot, d.Run, () => d.Run.MovePending, "排上了换房请求");
        double requestedAt = bot.ElapsedMs;
        Drive(bot, d.Run, () => bot.Stopped, "停手");

        Assert.Equal(HostedBotStop.NoExit, bot.Reason);
        Assert.Equal(1, d.Run.MoveRequests);
        Assert.Single(d.Rig.Requests);
        Assert.Contains("reason=NoExit", bot.LogLine());
        // 无进展的计时从"排了换房请求"(上一次有进展)那一刻算起,不是从开局算的
        Assert.True(bot.ElapsedMs - requestedAt >= 12000.0 - 2 * FrameMs, "等满一个上限才停手:" + bot.LogLine());
        Assert.True(bot.ElapsedMs - requestedAt < 12000.0 + 2 * FrameMs, "上限一到就停手:" + bot.LogLine());
    }

    [Fact]
    public void The_total_limit_counts_the_whole_dungeon_including_the_wait_for_the_next_room()
    {
        using var d = new Dungeon(new RoomPlan(1, LeftRight, GateSide.Right, null), new RoomPlan(1, LeftRight, null, GateSide.Left)) { AnswerAfterSteps = -1 };
        var bot = new HostedRoomBot(totalLimitMs: 12000.0);

        Drive(bot, d.Run, () => bot.Stopped, "整段超时");

        Assert.Equal(HostedBotStop.TimedOut, bot.Reason);
        Assert.True(d.Run.MovePending);                                           // 超时的时候还在等
        Assert.True(bot.ElapsedMs >= 12000.0 && bot.ElapsedMs < 12000.0 + 2 * FrameMs);
    }

    [Fact]
    public void Entering_a_new_room_and_asking_for_a_move_both_count_as_progress()
    {
        // 无进展上限 8 秒:每间房打怪、走到门口、等回应都远不到 8 秒没动静,三间房走下来不能被误判成"没进展"
        using var d = new Dungeon(
            new RoomPlan(1, LeftRight, GateSide.Right, null),
            new RoomPlan(1, LeftRight, GateSide.Right, GateSide.Left),
            new RoomPlan(1, LeftRight, null, GateSide.Left));
        var bot = new HostedRoomBot(noProgressLimitMs: 8000.0);

        Drive(bot, d.Run, () => d.Run.RoomIndex == 2 && d.Run.Reported == 3, "打穿三间房");

        Assert.False(bot.Stopped);
        Assert.Equal(3, d.Run.RoomsEntered);
    }

    [Fact]
    public void Entering_a_new_room_on_its_own_restarts_the_no_progress_clock()
    {
        // 没有怪也没有出口的两间房:自动机什么也不做,命中、击杀、上报一个数都不会动,只有"进了新房间"这一件事能算进展
        using var d = new Dungeon(new RoomPlan(0, LeftRight, null, null), new RoomPlan(0, LeftRight, null, GateSide.Left)) { AnswerAfterSteps = -1 };
        var bot = new HostedRoomBot(noProgressLimitMs: 5000.0);

        Idle(bot, d.Run, 3000);
        Assert.False(bot.Stopped, bot.LogLine());
        d.Run.EnterRoom(d.Rig.NewRoom(0, LeftRight), 1, 901, GateSide.Left, null);
        double enteredAt = bot.ElapsedMs;
        Idle(bot, d.Run, 3000);                                                   // 一共 6 秒,但进新房间重新计了时:只过了 3 秒

        Assert.False(bot.Stopped, bot.LogLine());
        Drive(bot, d.Run, () => bot.Stopped, "再等满一个上限才停手");
        Assert.Equal(HostedBotStop.NoSettlement, bot.Reason);
        Assert.True(bot.ElapsedMs - enteredAt >= 5000.0 - 2 * FrameMs, "从进新房间那一刻起等满一个上限:" + bot.LogLine());
    }

    // ------------------------------------------------------------------ 放弃、出口走不成

    [Fact]
    public void An_aborted_run_stops_the_bot_with_Aborted_even_when_the_settlement_also_arrived()
    {
        using var d = new Dungeon(new RoomPlan(1, LeftRight, GateSide.Right, null), new RoomPlan(1, LeftRight, null, GateSide.Left)) { AnswerAfterSteps = -1 };
        var bot = new HostedRoomBot();
        Drive(bot, d.Run, () => d.Run.MovePending, "排上了换房请求");

        d.Run.Abort(HostedAbort.MoveRefused);
        d.Rig.Done = true;                                                        // 同时结算也"到了":放弃的原因优先
        int beats = bot.Advance(d.Run, FrameMs);

        Assert.Equal(HostedBotStop.Aborted, bot.Reason);
        Assert.True(bot.Stopped);
        Assert.True(d.Run.Failed);                                                // 流程据此回城
        Assert.Equal(0, bot.Advance(d.Run, FrameMs));                             // 停手之后不再补步
        Assert.True(beats >= 0);
        string line = bot.LogLine();
        Assert.Contains("reason=Aborted", line);
        Assert.EndsWith(" abort=MoveRefused", line);
    }

    [Fact]
    public void After_the_host_aborted_the_run_the_bot_does_not_take_another_step_towards_the_door()
    {
        using var d = new Dungeon(new RoomPlan(1, LeftRight, GateSide.Right, null), new RoomPlan(1, LeftRight, null, GateSide.Left)) { AnswerAfterSteps = -1 };
        var bot = new HostedRoomBot();
        Drive(bot, d.Run, () => d.Run.RoomCleared, "清房成立(主角离门还远,正要往门走)");
        (double x, double y) at = (d.Run.Hero.X, d.Run.Hero.Y);
        Assert.True(at.x < HostedGatedRoomFixture.GatePosition(GateSide.Right)[0] - 300, "主角离右出口还远");

        d.Run.Abort(HostedAbort.MoveTimeout);
        bot.Advance(d.Run, FrameMs);

        Assert.Equal(HostedBotStop.Aborted, bot.Reason);
        Assert.Equal(at, (d.Run.Hero.X, d.Run.Hero.Y));                           // 放弃了:这一拍也不再往门走
    }

    [Fact]
    public void A_planned_exit_the_map_has_no_position_for_stops_the_bot_with_NoExit_as_soon_as_the_room_is_clear()
    {
        // 计划的出口在右,但这间房的地图只有左出口的位置:没法走过去,清完怪就停手,不用等整整一个无进展上限
        using var rig = new HostedRoomMultiRunTests.Rig(1, GateSide.Right, new[] { GateSide.Left });
        var bot = new HostedRoomBot();

        Drive(bot, rig.Run, () => bot.Stopped, "停手", maxSeconds: 30);

        Assert.Equal(HostedBotStop.NoExit, bot.Reason);
        Assert.True(bot.ElapsedMs < bot.NoProgressLimitMs / 2, "立刻停手:" + bot.LogLine());
        Assert.Empty(rig.Requests);
        Assert.Equal(1, rig.Run.Reported);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void When_the_exit_cannot_be_walked_to_in_the_very_beat_the_run_is_aborted_or_settled_that_earlier_reason_is_kept(bool settle)
    {
        // 计划的出口在右,但这间房的地图只有左出口的位置(走不过去)。托管的"推进"回调在清房之后第二次被叫的那一拍放弃(或把结算收齐):
        // 同一拍里自动机既发现出口走不成、又发现放弃/结算,停手的原因要留放弃/结算那个,不能被"出口走不成"盖掉
        using var rig = new HostedRoomMultiRunTests.Rig(1, GateSide.Right, new[] { GateSide.Left });
        int clearedCalls = 0;
        rig.Run.Pump = run =>
        {
            if (run.RoomCleared && ++clearedCalls == 2)
            {
                if (settle)
                {
                    rig.Done = true;
                }
                else
                {
                    run.Abort(HostedAbort.MoveTimeout);
                }
            }
        };
        var bot = new HostedRoomBot();

        Drive(bot, rig.Run, () => bot.Stopped, "停手", maxSeconds: 30);

        Assert.Equal(2, clearedCalls);                                            // 夹具自检:放弃/结算就是在"出口走不成"的那一拍发生的
        Assert.Equal(settle ? HostedBotStop.Settled : HostedBotStop.Aborted, bot.Reason);
        Assert.Contains("reason=" + (settle ? "Settled" : "Aborted"), bot.LogLine());
        Assert.Empty(rig.Requests);
    }

    [Fact]
    public void Gold_left_on_the_floor_of_a_clear_room_is_picked_up_before_the_bot_heads_for_the_door()
    {
        using var d = new Dungeon(new RoomPlan(1, LeftRight, GateSide.Right, null), new RoomPlan(1, LeftRight, null, GateSide.Left)) { AnswerAfterSteps = -1 };
        var bot = new HostedRoomBot();
        Drive(bot, d.Run, () => d.Run.RoomCleared, "打死房间里的怪、尸体消失");
        Assert.Empty(d.Run.Room.Drops);                                           // 夹具自检:这间房的怪没有掉东西,下面的金币是测试自己放的
        double startX = d.Run.Hero.X;
        d.Run.Room.SpawnGold(10, startX - 250, d.Run.Hero.Y);                     // 金币落在主角左边 250 像素,门在右边几百像素外

        Idle(bot, d.Run, 150);                                                    // 只走一小会儿:还在去捡金币的路上

        Assert.True(d.Run.Hero.X < startX - 5, "先往左去捡金币,不是往右走向门:主角从 " + startX + " 到了 " + d.Run.Hero.X);
        Assert.Empty(d.Rig.Requests);
        Assert.StartsWith("捡金币", bot.Intent);

        Drive(bot, d.Run, () => d.Rig.Requests.Count > 0, "捡完金币、走到门口、触发换房");

        Assert.Equal(1, d.Run.Room.GoldPicked);                                   // 触发换房的时候金币已经在手里
        Assert.Empty(d.Run.Room.Drops);
        Assert.Equal(GateSide.Right, Assert.Single(d.Rig.Requests).Side);
    }

    // ------------------------------------------------------------------ 日志

    [Fact]
    public void The_multi_room_log_line_adds_only_room_counts_and_state_names()
    {
        using var d = new Dungeon(
            new RoomPlan(1, LeftRight, GateSide.Right, null),
            new RoomPlan(1, LeftRight, null, GateSide.Left));
        var bot = new HostedRoomBot();
        Drive(bot, d.Run, () => d.Run.RoomIndex == 1 && d.Run.Reported == 2, "打穿两间房");

        string line = bot.LogLine();

        Assert.Matches(new Regex(@"^DNF_NET stage=room bot state=running reason=None steps=\d+ elapsedMs=\d+ reported=2 dropped=0 alive=0 hits=\d+ kills=\d+ hero=up room=1 rooms=2 moves=1 refused=0 exit=none$"), line);
        Assert.DoesNotContain("abort=", line);
        Assert.DoesNotContain("map/", line);
        Assert.DoesNotContain("/", line);
    }

    [Fact]
    public void The_single_room_log_line_has_no_room_fields()
    {
        using var rig = new HostedRoomMultiRunTests.Rig(1, exit: null, wire: false);
        var bot = new HostedRoomBot();
        Drive(bot, rig.Run, () => rig.Run.Reported == 1, "打死唯一的怪");

        string line = bot.LogLine();

        Assert.Matches(new Regex(@"^DNF_NET stage=room bot state=running reason=None steps=\d+ elapsedMs=\d+ reported=1 dropped=0 alive=0 hits=\d+ kills=\d+ hero=up$"), line);
    }

    [Fact]
    public void The_exit_name_in_the_log_follows_the_room_the_bot_is_in()
    {
        using var d = new Dungeon(new RoomPlan(1, LeftRight, GateSide.Right, null), new RoomPlan(1, LeftRight, null, GateSide.Left)) { AnswerAfterSteps = -1 };
        var bot = new HostedRoomBot();
        Drive(bot, d.Run, () => d.Run.MovePending, "排上了换房请求");

        Assert.EndsWith(" room=0 rooms=1 moves=1 refused=0 exit=Right", bot.LogLine());
    }
}
