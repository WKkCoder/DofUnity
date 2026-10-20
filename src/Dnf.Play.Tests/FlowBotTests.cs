using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 无人值守的流程自动机(<see cref="FlowBot"/>):按清单走完 城镇 → 副本选择 → 副本 → 结算 → 再来一次 / 选择其它地下城 / 返回城镇。
/// 副本是合成的两格迷你副本(没有怪物,向右走进首领格就通关),城镇是假会话;验证的是流程的次序、停止条件与确定性。
/// </summary>
public class FlowBotTests : IDisposable
{
    private const double FrameMs = 1000.0 / 60.0;
    private const int MaxFrames = 60 * 60 * 10;                  // 最多模拟 10 分钟,防止死循环

    private static readonly DungeonEntry A = new DungeonEntry("dungeon/test/a.dgn", "AAA");
    private static readonly DungeonEntry B = new DungeonEntry("dungeon/test/b.dgn", "BBB");

    private readonly List<DungeonSessionTests.Dungeon> _dungeons = new List<DungeonSessionTests.Dungeon>();

    public void Dispose()
    {
        foreach (DungeonSessionTests.Dungeon d in _dungeons)
        {
            d.Dispose();
        }
    }

    private GameFlow NewFlow(List<(string Path, int Run)> started = null)
    {
        return new GameFlow(
            () => new GameFlowTests.FakeHero("town"),
            (path, run) =>
            {
                started?.Add((path, run));
                var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.TwoRoomBossLayout(), (cx, cy) => DungeonSessionTests.None());
                _dungeons.Add(d);
                return d.Session;
            },
            new[] { A, B });
    }

    private static int Drive(FlowBot bot, GameFlow flow, double frameMs = FrameMs)
    {
        int frames = 0;
        while (!bot.Done && frames < MaxFrames)
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
    public void TheBotWalksTheWholeTourInTheListedOrderAndStopsInTheTown()
    {
        var started = new List<(string Path, int Run)>();
        GameFlow flow = NewFlow(started);
        var bot = new FlowBot(false, new[] { SettlementChoice.Retry, SettlementChoice.SelectDungeon, SettlementChoice.ReturnToTown });
        flow.EnterTown("start");

        int frames = Drive(bot, flow);

        Assert.True(bot.Done, "流程没有在 " + MaxFrames + " 帧内走完");
        Assert.True(frames < MaxFrames);
        Assert.Equal(
            new[]
            {
                (GameStage.Town, GameStage.Town, "start"),
                (GameStage.Town, GameStage.DungeonSelect, "bot"),
                (GameStage.DungeonSelect, GameStage.Dungeon, "select AAA"),
                (GameStage.Dungeon, GameStage.Dungeon, "choice Retry"),
                (GameStage.Dungeon, GameStage.DungeonSelect, "choice SelectDungeon"),
                (GameStage.DungeonSelect, GameStage.Dungeon, "select AAA"),
                (GameStage.Dungeon, GameStage.Town, "choice ReturnToTown"),
            },
            Tour(flow));
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal((3, 3, 3), (flow.Runs, flow.Settlements, bot.ChoicesMade));
        Assert.Equal(new[] { (A.Path, 0), (A.Path, 1), (A.Path, 2) }, started);
        Assert.All(_dungeons, d => Assert.True(d.Session.BossCleared));        // 每一局都真的打到了首领清空

        // 结束之后再推也没有动作
        int serial = flow.StageSerial;
        bot.Advance(flow, 5000);
        Assert.Equal(serial, flow.StageSerial);
    }

    [Fact]
    public void AnEmptyListStopsOnTheFirstSettlementMenuWithoutChoosing()
    {
        GameFlow flow = NewFlow();
        var bot = new FlowBot(false, Array.Empty<SettlementChoice>());
        flow.EnterTown("start");

        Drive(bot, flow);

        Assert.True(bot.Done);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        var run = (DungeonSession)flow.Run;
        Assert.True(run.SettlementActive);
        Assert.Equal(SettlementChoice.None, run.Choice);
        Assert.True(run.SettlementAgeMs >= FlowBot.SettlementDwellMs);
        Assert.Equal((1, 0, 0), (flow.Runs, flow.Settlements, bot.ChoicesMade));
    }

    [Fact]
    public void AListThatDoesNotEndInTheTownStopsOnTheLastRunsSettlementMenu()
    {
        GameFlow flow = NewFlow();
        var bot = new FlowBot(false, new[] { SettlementChoice.Retry });
        flow.EnterTown("start");

        Drive(bot, flow);

        Assert.True(bot.Done);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Equal((2, 1, 1), (flow.Runs, flow.Settlements, bot.ChoicesMade));
        Assert.True(((DungeonSession)flow.Run).SettlementActive);
    }

    [Fact]
    public void ABotCanStartInTheDungeonOrOnTheSelectScreen()
    {
        GameFlow fromDungeon = NewFlow();
        var a = new FlowBot(false, new[] { SettlementChoice.ReturnToTown });
        fromDungeon.StartDungeon(A.Path, "start");
        Drive(a, fromDungeon);
        Assert.Equal(new[] { (GameStage.Dungeon, GameStage.Dungeon, "start"), (GameStage.Dungeon, GameStage.Town, "choice ReturnToTown") }, Tour(fromDungeon));

        GameFlow fromSelect = NewFlow();
        var b = new FlowBot(false, new[] { SettlementChoice.ReturnToTown });
        fromSelect.EnterSelect("start");
        Drive(b, fromSelect);
        Assert.Equal(GameStage.Town, fromSelect.Stage);
        Assert.Equal((GameStage.DungeonSelect, GameStage.Dungeon, "select AAA"), Tour(fromSelect)[1]);
    }

    [Fact]
    public void TheTourDoesNotDependOnTheFrameLength()
    {
        var choices = new[] { SettlementChoice.Retry, SettlementChoice.SelectDungeon, SettlementChoice.ReturnToTown };
        GameFlow fine = NewFlow();
        var fineBot = new FlowBot(false, choices);
        fine.EnterTown("start");
        Drive(fineBot, fine, FrameMs);

        GameFlow coarse = NewFlow();
        var coarseBot = new FlowBot(false, choices);
        coarse.EnterTown("start");
        Drive(coarseBot, coarse, 40.0);

        Assert.True(fineBot.Done && coarseBot.Done);
        Assert.Equal(Tour(fine), Tour(coarse));
        Assert.Equal((fine.Runs, fine.Settlements), (coarse.Runs, coarse.Settlements));
    }

    [Fact]
    public void OnlyTheThreeRealMenuChoicesAreAccepted()
    {
        Assert.Throws<ArgumentException>(() => new FlowBot(false, new[] { SettlementChoice.QuickParty }));
        Assert.Throws<ArgumentException>(() => new FlowBot(false, new[] { SettlementChoice.None }));
        Assert.Throws<ArgumentNullException>(() => new FlowBot(false, null));
        Assert.Throws<ArgumentNullException>(() => new FlowBot(false, Array.Empty<SettlementChoice>()).Advance(null, 10));
    }

    [Fact]
    public void AFlowThatHasNotStartedIsLeftAlone()
    {
        GameFlow flow = NewFlow();
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown });
        bot.Advance(flow, 10000);
        Assert.Equal(0, flow.StageSerial);
        Assert.False(bot.Done);
    }

    [Fact]
    public void TheDwellTimesInTheTownAndOnTheSelectScreenAreHonoured()
    {
        GameFlow flow = NewFlow();
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown });
        flow.EnterTown("start");

        bot.Advance(flow, FlowBot.TownDwellMs - 1);
        Assert.Equal(GameStage.Town, flow.Stage);
        bot.Advance(flow, 1);
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);

        bot.Advance(flow, FlowBot.SelectDwellMs - 1);
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        bot.Advance(flow, 1);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
    }
}
