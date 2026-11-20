using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 测试补强(区域 C,第五轮):
/// 1) <see cref="GameFlow.FailOnlineDungeonStart"/> 重开的选择界面"一定不忙、只带传入提示"。
///    第一轮(GameFlowOnlineGapTests)里"解除忙等"的断言是在"马上要被丢弃的旧界面"上预置 Busy,而重开的新界面本来就是不忙的,
///    所以那条断言约束不到 FailOnlineDungeonStart 自己有没有解除;本文件改为让宿主挂钩 <see cref="GameFlow.ConfigureSelect"/>
///    在"新界面上"先把 Busy 置真、挂上别的提示,这样只有 FailOnlineDungeonStart 自己的收尾才能把界面救回"可操作、显示失败原因"的状态。
/// 2) <see cref="GameFlow.BeginOnlineDungeonLoading"/> 切进加载阶段时丢弃遗留的"加载走完后的后续动作"(旧的离线开局不能在联网加载画面走完时冒出来)。
/// 会话是假的、数据是合成的,时间只用流程自己的逻辑推进,不依赖墙钟。辅助类复制自 GameFlowOnlineGapTests(那边是私有嵌套类),改了名。
/// </summary>
public class GameFlowOnlineGapR5Tests
{
    private sealed class R5Hero : IHeroSession
    {
        public R5Hero(string label) => Label = label;
        public string Label { get; }
        public string MapPath => "fake/" + Label;
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;
        public void Update(double deltaMs, int dirX, int dirY) { }
        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
    }

    private sealed class R5Run : IRunSession
    {
        public R5Run(string path) => MapPath = path;
        public string MapPath { get; }
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;
        public void Update(double deltaMs, int dirX, int dirY) { }
        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
        public SettlementChoice Choice { get; set; }
        public bool Failed { get; set; }
    }

    private static SpriteFrame R5Coded(int width, int height, byte tag)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            rgba[i * 4 + 2] = tag;
            rgba[i * 4 + 3] = 255;
        }
        return new SpriteFrame(width, height, 0, 0, width, height, rgba);
    }

    private static readonly DungeonEntry A = new DungeonEntry("dungeon/test/a.dgn", "AAA");
    private static readonly DungeonEntry B = new DungeonEntry("dungeon/test/b.dgn", "BBB");
    private static readonly DungeonEntry C = new DungeonEntry("dungeon/test/c.dgn", "CCC");

    /// <summary>一条流程:城镇每次造新会话,离线开局只记录(路径, 局序号),可选带加载过渡图;联网开局钩子记录(条目, 难度)。</summary>
    private sealed class R5Rig
    {
        public GameFlow Flow { get; }
        public List<(string Path, int Run)> OfflineStarts { get; } = new List<(string Path, int Run)>();
        public List<(DungeonEntry Entry, int Difficulty)> OnlineStarts { get; } = new List<(DungeonEntry Entry, int Difficulty)>();

        public R5Rig(bool withSplash = false)
        {
            SplashScene splash = new SplashScene("test", R5Coded(800, 600, 77), null, null, 0, 0, 500.0);
            Flow = new GameFlow(
                new LegacyTownWorld(() => new R5Hero("town")),
                (path, run) =>
                {
                    OfflineStarts.Add((path, run));
                    return new R5Run(path);
                },
                new[] { A, B, C },
                dungeonSplash: withSplash ? (Func<string, SplashScene>)(_ => splash) : null);
            Flow.OnlineStartDungeon = (entry, difficulty) => OnlineStarts.Add((entry, difficulty));
        }
    }

    // ------------------------------------------------------------------ FailOnlineDungeonStart:重开的界面不忙

    [Fact]
    public void FailingTheOnlineStartLeavesTheFreshSelectScreenIdleWithOnlyTheGivenNoticeEvenWhenTheConfigureHookMarksItBusy()
    {
        // 保护:联网进副本失败后重开的选择界面"不忙"且"只带传入的提示":宿主的 ConfigureSelect 挂钩在新界面上先跑
        // (这里的挂钩故意每次都把新界面标成忙、挂上一条别的提示),FailOnlineDungeonStart 随后必须自己把 Busy 清成 false、
        // 把 Notice 换成传入值(含 null);否则玩家在失败之后既动不了光标、也看不到失败原因。
        var rig = new R5Rig();
        GameFlow flow = rig.Flow;
        var hookNotice = new DungeonSelectNotice(DungeonSelectNoticeKind.Info, "HOOK");
        var configured = new List<DungeonSelectScreen>();
        flow.DifficultyLimit = _ => 3;                              // 让选择界面能承载难度 0 到 3
        flow.ConfigureSelect = screen =>
        {
            configured.Add(screen);
            screen.Busy = true;
            screen.Notice = hookNotice;
        };
        flow.EnterTown("boot");                                     // 序号 1
        flow.EnterSelect("gate");                                   // 序号 2:挂钩在界面 #1 上先跑

        // 前置:挂钩确实把界面 #1 标成了忙并挂上自己的提示(下面的"不忙"因此不是默认值造成的)
        DungeonSelectScreen first = flow.Select;
        Assert.Single(configured);
        Assert.Same(configured[0], first);
        Assert.True(first.Busy);
        Assert.Same(hookNotice, first.Notice);
        flow.OnlineHoldDungeonExit = true;

        // 失败回退到 B(序号 1)、难度 2:新界面 #2,挂钩又在它上面跑过一次,但收尾之后不忙、只带"超时"提示
        var timeout = new DungeonSelectNotice(DungeonSelectNoticeKind.Timeout);
        flow.FailOnlineDungeonStart(B, 2, timeout, "online fail 1");
        DungeonSelectScreen second = flow.Select;
        Assert.NotSame(first, second);
        Assert.Equal(2, configured.Count);
        Assert.Same(configured[1], second);
        Assert.False(second.Busy);
        Assert.Same(timeout, second.Notice);
        Assert.Equal((1, 2), (second.Index, second.Difficulty));
        Assert.Same(B, second.Selected);
        Assert.Equal((GameStage.DungeonSelect, 3), (flow.Stage, flow.StageSerial));
        Assert.False(flow.OnlineHoldDungeonExit);
        Assert.Equal((GameStage.DungeonSelect, GameStage.DungeonSelect, "online fail 1"),
            (flow.History[^1].From, flow.History[^1].To, flow.History[^1].Reason));

        // 界面确实能操作了:上移一格生效(忙着的界面会忽略),并清掉失败提示;难度也能调
        second.Move(-1);
        Assert.Equal(0, second.Index);
        Assert.Null(second.Notice);
        second.MoveDifficulty(1);
        Assert.Equal(3, second.Difficulty);

        // 没给提示:新界面既不忙、也没有提示(挂钩挂的那条被换成 null)
        flow.FailOnlineDungeonStart(null, 0, null);
        DungeonSelectScreen third = flow.Select;
        Assert.Equal(3, configured.Count);
        Assert.Same(configured[2], third);
        Assert.False(third.Busy);
        Assert.Null(third.Notice);
        Assert.Equal((0, 0), (third.Index, third.Difficulty));
        Assert.Equal((GameStage.DungeonSelect, 4), (flow.Stage, flow.StageSerial));     // boot、gate、失败 1、失败 2
        Assert.Equal("online enter failed", flow.History[^1].Reason);
    }

    [Fact]
    public void FailingTheOnlineStartFromTheLoadingStageAlsoLeavesTheNewSelectScreenIdle()
    {
        // 保护:有过渡图时联网请求先进加载阶段(选择界面消失),随后失败回到的新选择界面同样不忙、只带传入提示:
        // 加载画面走完与否都不再把流程带进副本;挂钩对新界面照样先跑一次;难度被夹在新界面的上限内。
        var rig = new R5Rig(withSplash: true);
        GameFlow flow = rig.Flow;
        int configured = 0;
        flow.DifficultyLimit = _ => 3;
        flow.ConfigureSelect = screen =>
        {
            configured++;
            screen.Busy = true;
            screen.Notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Info, "HOOK");
        };
        flow.EnterTown("boot");
        flow.EnterSelect("gate");
        Assert.Equal(1, configured);

        flow.BeginOnlineDungeonLoading(C, 2, "online enter x");
        Assert.Equal(GameStage.Loading, flow.Stage);
        Assert.Null(flow.Select);
        Assert.NotNull(flow.Splash);
        flow.Splash.Advance(5000);
        Assert.True(flow.Splash.Finished);

        var rejected = new DungeonSelectNotice(DungeonSelectNoticeKind.Rejected);
        flow.FailOnlineDungeonStart(C, 2, rejected, "online fail x");

        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal(4, flow.StageSerial);                          // boot、gate、加载、失败回退
        Assert.Null(flow.Splash);
        Assert.Equal(2, configured);
        Assert.False(flow.Select.Busy);
        Assert.Same(rejected, flow.Select.Notice);
        Assert.Equal((2, 2), (flow.Select.Index, flow.Select.Difficulty));
        Assert.Same(C, flow.Select.Selected);
        Assert.Equal((GameStage.Loading, GameStage.DungeonSelect, "online fail x"),
            (flow.History[^1].From, flow.History[^1].To, flow.History[^1].Reason));
        Assert.False(flow.Poll());                                  // 加载画面已经没了:什么也不发生
        Assert.Empty(rig.OnlineStarts);
        Assert.Equal(0, flow.Runs);

        // 新界面可以再确认一次:忙着的界面会忽略确认,这里能正常交给联网钩子
        flow.Select.Confirm();
        Assert.True(flow.Poll());
        Assert.Equal((C, 2), Assert.Single(rig.OnlineStarts));
    }

    // ------------------------------------------------------------------ BeginOnlineDungeonLoading:丢弃遗留的加载后续动作

    [Fact]
    public void BeginningOnlineLoadingWithASplashDiscardsAnOfflineStartLeftPendingBehindAnOlderSplash()
    {
        // 保护:联网请求进副本切到加载阶段时,先清掉"加载画面走完后要接着做的事":此前离线开局挂起的后续动作
        // (StartDungeon 带过渡图时停在加载阶段、开局挂起;随后宿主直接 EnterSelect 离开了加载阶段,没人让它走完)
        // 不能在联网加载画面走完时冒出来、把流程带进一局离线副本;联网加载只能由宿主握手完成后用 CompleteOnlineDungeonStart 收尾。
        var rig = new R5Rig(withSplash: true);
        GameFlow flow = rig.Flow;
        flow.EnterTown("boot");                                     // 序号 1

        flow.StartDungeon(A.Path, "offline start");                 // 有过渡图:先停在加载阶段,离线开局挂起(序号 2)
        Assert.Equal((GameStage.Loading, 2), (flow.Stage, flow.StageSerial));
        Assert.Empty(rig.OfflineStarts);
        flow.EnterSelect("leave loading");                          // 宿主改去选择界面:挂起的离线开局没有被收尾(序号 3)
        Assert.Equal((GameStage.DungeonSelect, 3), (flow.Stage, flow.StageSerial));

        flow.BeginOnlineDungeonLoading(B, 1, "online enter x");     // 联网加载:再次进加载阶段(序号 4)
        Assert.Equal((GameStage.Loading, 4), (flow.Stage, flow.StageSerial));
        Assert.Null(flow.Select);
        flow.Splash.Advance(5000);
        Assert.True(flow.Splash.Finished);

        // 加载画面走完了,但没有任何后续动作:流程原地等宿主,不开离线局、不记局数、不调联网钩子
        Assert.False(flow.Poll());
        Assert.Equal((GameStage.Loading, 4), (flow.Stage, flow.StageSerial));
        Assert.Empty(rig.OfflineStarts);
        Assert.Empty(rig.OnlineStarts);
        Assert.Equal(0, flow.Runs);
        Assert.Equal((B.Path, 1), (flow.DungeonPath, flow.Difficulty));

        // 宿主握手完成后收尾:进副本、计一局,仍然没有离线开局
        flow.CompleteOnlineDungeonStart(B.Path, 1, new R5Run(B.Path), "online enter ok");
        Assert.Equal((GameStage.Dungeon, 5, 1), (flow.Stage, flow.StageSerial, flow.Runs));
        Assert.Empty(rig.OfflineStarts);
        Assert.Equal((GameStage.Loading, GameStage.Dungeon, "online enter ok"), (flow.History[^1].From, flow.History[^1].To, flow.History[^1].Reason));
    }
}
