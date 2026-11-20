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
/// 3) <see cref="GameFlow.CompleteOnlineGiveUp"/> 与 <see cref="GameFlow.CompleteOnlineDungeonAbort"/> 回城时放开"联网出口停留"旗标(否则之后的副本卡住不结算)。
/// 4) 重开选择界面时多项同路径取第一个匹配项;城镇系统菜单里选"回选角"才切到选角阶段(这两条是额外探索时发现没人钉的)。
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

        public R5Rig(bool withSplash = false, bool withCharacterSelect = false, DungeonEntry[] entries = null)
        {
            SplashScene splash = new SplashScene("test", R5Coded(800, 600, 77), null, null, 0, 0, 500.0);
            Flow = new GameFlow(
                new LegacyTownWorld(() => new R5Hero("town")),
                (path, run) =>
                {
                    OfflineStarts.Add((path, run));
                    return new R5Run(path);
                },
                entries ?? new[] { A, B, C },
                dungeonSplash: withSplash ? (Func<string, SplashScene>)(_ => splash) : null,
                characterSelect: withCharacterSelect
                    ? () => new CharacterSelectScreen(new CharacterRoster(new List<CharacterSlot> { new CharacterSlot("Hero0", "Slayer", 1), new CharacterSlot("Hero1", "Slayer", 2) }))
                    : (Func<CharacterSelectScreen>)null);
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

    // ------------------------------------------------------------------ CompleteOnlineGiveUp / CompleteOnlineDungeonAbort:放开联网出口停留

    [Fact]
    public void FinishingAnOnlineGiveUpOrAbortReleasesTheHeldDungeonExitSoTheNextOfflineRunIsNotStuck()
    {
        // 保护:联网放弃回城(CompleteOnlineGiveUp)与联网中途中止收尾(CompleteOnlineDungeonAbort)回城时必须把"联网出口停留"旗标放开:
        // 旗标若留着为真,回城之后再开的副本在 PollDungeon 里会一直因为这个旗标而不处理结算选择,玩家被卡在副本里出不来。
        // 这里每一次都在握手期间先把旗标置真(模拟控制器持有出口),收尾之后既断言旗标本身为假,也走一遍"下一局离线副本能正常结算离开"。
        var rig = new R5Rig();
        GameFlow flow = rig.Flow;
        flow.EnterTown("boot");                                                                         // 序号 1

        // ---- 放弃回城
        flow.CompleteOnlineDungeonStart(A.Path, 0, new R5Run(A.Path), "online enter 1");                // 序号 2,局数 1(联网开局不经过离线工厂)
        Assert.Equal((GameStage.Dungeon, 2, 1), (flow.Stage, flow.StageSerial, flow.Runs));
        flow.OnlineHoldDungeonExit = true;                                                              // 控制器在放弃握手期间持有出口
        flow.CompleteOnlineGiveUp("online giveup 1");                                                   // 序号 3:回城,放弃计 1 次
        Assert.False(flow.OnlineHoldDungeonExit);
        Assert.Equal((GameStage.Town, 3, 1, 0), (flow.Stage, flow.StageSerial, flow.GiveUps, flow.Failures));
        Assert.Equal((GameStage.Dungeon, GameStage.Town, "online giveup 1"), (flow.History[^1].From, flow.History[^1].To, flow.History[^1].Reason));

        // 旗标放开之后,离线开的副本里结算选择会被正常处理
        flow.StartDungeon(B.Path, "offline start 1");                                                   // 序号 4:工厂收到局序号 1(此前联网开局已计 1 局),局数变 2
        Assert.Equal((GameStage.Dungeon, 4, 2), (flow.Stage, flow.StageSerial, flow.Runs));
        Assert.IsType<R5Run>(flow.Run).Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());                                                                       // 序号 5:回城,结算计 1 次
        Assert.Equal((GameStage.Town, 5, 1), (flow.Stage, flow.StageSerial, flow.Settlements));

        // ---- 中途中止收尾
        flow.CompleteOnlineDungeonStart(C.Path, 0, new R5Run(C.Path), "online enter 2");                // 序号 6,局数 3
        Assert.Equal((GameStage.Dungeon, 6, 3), (flow.Stage, flow.StageSerial, flow.Runs));
        flow.OnlineHoldDungeonExit = true;                                                              // 控制器在中止收尾期间持有出口
        flow.CompleteOnlineDungeonAbort("online abort 2");                                              // 序号 7:回城,失败计 1 次
        Assert.False(flow.OnlineHoldDungeonExit);
        Assert.Equal((GameStage.Town, 7, 1, 1), (flow.Stage, flow.StageSerial, flow.GiveUps, flow.Failures));
        Assert.Equal((GameStage.Dungeon, GameStage.Town, "online abort 2"), (flow.History[^1].From, flow.History[^1].To, flow.History[^1].Reason));

        flow.StartDungeon(A.Path, "offline start 2");                                                   // 序号 8:工厂收到局序号 3,局数变 4
        Assert.Equal((GameStage.Dungeon, 8, 4), (flow.Stage, flow.StageSerial, flow.Runs));
        Assert.IsType<R5Run>(flow.Run).Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());                                                                       // 序号 9:回城,结算计 2 次
        Assert.Equal((GameStage.Town, 9, 2), (flow.Stage, flow.StageSerial, flow.Settlements));

        // 两次离线开局的(路径, 局序号)就是上面推算的,联网开局没有走离线工厂
        Assert.Equal(2, rig.OfflineStarts.Count);
        Assert.Equal((B.Path, 1), rig.OfflineStarts[0]);
        Assert.Equal((A.Path, 3), rig.OfflineStarts[1]);
    }

    // ------------------------------------------------------------------ ReopenSelect:同路径条目取第一个

    [Fact]
    public void FailingTheOnlineStartReopensTheSelectScreenOnTheFirstEntryThatHasTheSamePath()
    {
        // 保护:联网失败后重开选择界面时,按"同一实例或同一路径"从列表开头找条目;多项同路径(这里第 0、1 项同路径)时落在第一个匹配项,
        // 与 EnterSelect 找"刚打过的副本"一致;传入的是后一个同路径条目的实例也一样落在第 0 项;列表里没有的路径回到第 0 项;别的路径落在自己的位置。
        var first = new DungeonEntry("dungeon/test/dup.dgn", "FIRST");
        var second = new DungeonEntry("dungeon/test/dup.dgn", "SECOND");
        var rig = new R5Rig(entries: new[] { first, second, B });
        GameFlow flow = rig.Flow;
        flow.EnterTown("boot");                                                     // 序号 1

        flow.FailOnlineDungeonStart(first, 0, null, "fail first");                  // 序号 2
        Assert.Equal((GameStage.DungeonSelect, 2, 0), (flow.Stage, flow.StageSerial, flow.Select.Index));
        flow.FailOnlineDungeonStart(second, 0, null, "fail second");                // 序号 3:同路径,第一个匹配项仍是第 0 项
        Assert.Equal((GameStage.DungeonSelect, 3, 0), (flow.Stage, flow.StageSerial, flow.Select.Index));
        Assert.Same(first, flow.Select.Selected);
        flow.FailOnlineDungeonStart(B, 0, null, "fail B");                          // 序号 4:别的路径落在它自己的位置
        Assert.Equal((GameStage.DungeonSelect, 4, 2), (flow.Stage, flow.StageSerial, flow.Select.Index));
        flow.FailOnlineDungeonStart(new DungeonEntry("dungeon/test/none.dgn", "NONE"), 0, null, "fail none");   // 序号 5:列表里没有 → 第 0 项
        Assert.Equal((GameStage.DungeonSelect, 5, 0), (flow.Stage, flow.StageSerial, flow.Select.Index));
        flow.FailOnlineDungeonStart(null, 0, null, "fail null");                    // 序号 6:没给条目 → 第 0 项
        Assert.Equal((GameStage.DungeonSelect, 6, 0), (flow.Stage, flow.StageSerial, flow.Select.Index));
    }

    // ------------------------------------------------------------------ 系统菜单:城镇里选"回选角"

    [Fact]
    public void ChoosingCharacterSelectInTheTownMenuOpensTheCharacterSelectScreenOnlyWhenTheFlowHasOne()
    {
        // 保护:城镇系统菜单里选第 2 行"回选角":流程配了选角界面工厂时 Poll 把阶段切到选角("menu"),并换上一块新的选角界面;
        // 流程没配工厂时忽略这次选择(不抛异常、阶段与序号都不变)。
        var withSelect = new R5Rig(withCharacterSelect: true);
        GameFlow flow = withSelect.Flow;
        flow.EnterTown("boot");                                                     // 序号 1
        Assert.True(flow.ToggleMenu());                                             // 城镇菜单:继续、回选角、退出
        Assert.True(flow.Menu.Open);
        flow.Menu.ChooseNumber(2);                                                  // 第 2 行 = 回选角
        Assert.True(flow.Poll());
        Assert.Equal((GameStage.CharacterSelect, 2), (flow.Stage, flow.StageSerial));
        Assert.NotNull(flow.CharacterSelect);
        Assert.Equal((GameStage.Town, GameStage.CharacterSelect, "menu"), (flow.History[^1].From, flow.History[^1].To, flow.History[^1].Reason));
        Assert.False(flow.Poll());                                                  // 选角界面还没确认:流程不动

        var bare = new R5Rig();
        GameFlow noSelect = bare.Flow;
        noSelect.EnterTown("boot");                                                 // 序号 1
        Assert.True(noSelect.ToggleMenu());
        noSelect.Menu.ChooseNumber(2);
        Assert.False(noSelect.Poll());                                              // 没有选角这个阶段:忽略
        Assert.Equal((GameStage.Town, 1), (noSelect.Stage, noSelect.StageSerial));
        Assert.Null(noSelect.CharacterSelect);
    }
}
