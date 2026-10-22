using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 城镇世界与加载画面接进游戏流程(证据 E-095):走进出口换区域、地下城入口区域的出口打开世界地图、加载阶段、自动机沿出口走到入口,
/// 以及加载画面本身的画法与素材读取。城镇的几何、出口与路线是合成的(三块区域排成一串),会话是不画任何东西的假会话
/// (用真实的出口触发器与图册);不含任何原版数据。
/// </summary>
public class TownFlowTests : IDisposable
{
    private const double FrameMs = 1000.0 / 60.0;

    private static readonly DungeonEntry A = new DungeonEntry("dungeon/test/a.dgn", "AAA");

    private sealed class FakeSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) => Frames.TryGetValue((imageName, frame), out sprite);
    }

    private static SpriteFrame Block(int width, int height, int offsetX = 0, int offsetY = 0)
    {
        return new SpriteFrame(width, height, offsetX, offsetY, width, height, new byte[width * height * 4]);
    }

    /// <summary>会走路的假城镇会话:每秒 220 像素,按 1 像素一小步在可走区域(矩形并集)里移动,出口用真实的触发器。</summary>
    private sealed class FakeTownSession : IHeroSession, ITownExitSource
    {
        private readonly TownExitTracker _tracker;
        private readonly IReadOnlyList<WorldRect> _walk;
        private TownExit? _pending;

        public string Label { get; }
        public double X { get; private set; }
        public double Y { get; private set; }
        public bool FacingLeft { get; private set; }

        public FakeTownSession(string label, IReadOnlyList<WorldRect> walk, IReadOnlyList<TownExit> exits, WorldRect? bounds, TownArrival arrival)
        {
            Label = label;
            _walk = walk;
            _tracker = new TownExitTracker(exits, bounds);
            X = arrival.X;
            Y = arrival.Y;
            FacingLeft = arrival.FacingLeft;
            _tracker.Arrive(X, Y);
        }

        public string MapPath => "fake/" + Label;
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;
        public TownExit? PendingExit => _pending;
        public double HeroX => X;
        public double HeroY => Y;
        public bool HeroFacingLeft => FacingLeft;

        public void OutwardOf(TownExit exit, out int dx, out int dy) => _tracker.OutwardOf(exit, out dx, out dy);

        public void Update(double deltaMs, int dirX, int dirY)
        {
            if (dirX != 0)
            {
                FacingLeft = dirX < 0;
            }
            double step = 220.0 * deltaMs / 1000.0;
            Move(dirX * step, true);
            Move(dirY * step, false);
            if (!_pending.HasValue)
            {
                _pending = _tracker.Step(X, Y, dirX, dirY);
            }
        }

        private void Move(double distance, bool horizontal)
        {
            double remaining = Math.Abs(distance);
            double sign = Math.Sign(distance);
            while (remaining > 1e-9)
            {
                double d = Math.Min(1.0, remaining);
                double nx = horizontal ? X + sign * d : X;
                double ny = horizontal ? Y : Y + sign * d;
                if (!_walk.Any(r => r.Contains(nx, ny)))
                {
                    return;
                }
                X = nx;
                Y = ny;
                remaining -= d;
            }
        }

        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
    }

    /// <summary>只会等结算选择的假副本。</summary>
    private sealed class FakeRun : IRunSession
    {
        public string Path { get; }
        public int Run { get; }
        public SettlementChoice Choice { get; set; }

        public bool Failed => false;

        public FakeRun(string path, int run)
        {
            Path = path;
            Run = run;
        }

        public string MapPath => "fake/run " + Path + " #" + Run;
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;

        public void Update(double deltaMs, int dirX, int dirY)
        {
        }

        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
    }

    private static ScriptLine Line(string tag, params object[] values)
    {
        var list = new List<ScriptValue>();
        foreach (object v in values)
        {
            list.Add(v is int i ? new ScriptValue(ScriptValueKind.Int, i, null) : new ScriptValue(ScriptValueKind.String, 0, (string)v));
        }
        return new ScriptLine(tag, list.ToArray());
    }

    /// <summary>
    /// 城镇 1 有三块区域:0 网关(登录点在里面,出口在下边)、1 广场(上边通网关、右边通入口区域)、2 地下城入口区域(左边回广场、右边通世界地图);
    /// 城镇 2 只有一块区域。
    /// </summary>
    private static TownAtlas SampleAtlas()
    {
        var scripts = new Dictionary<string, ScriptLine[]>
        {
            ["town/t.twn"] = new[]
            {
                Line("[area]", 0, "T/Gate.map", "[gate]", 200, 150), Line("[area]", 1, "T/Square.map", "[normal]"),
                Line("[area]", 2, "T/Dg.map", "[dungeon gate]", 1),
            },
            ["town/u.twn"] = new[] { Line("[area]", 0, "U/Only.map", "[normal]") },
            ["map/t/gate.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 400, 200),
                Line("[town movable area]", 100, 290, 200, 30, 1, 1),
            },
            ["map/t/square.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 600, 200),
                Line("[town movable area]", 200, 90, 100, 30, 1, 0, 590, 120, 30, 160, 1, 2),
            },
            ["map/t/dg.map"] = new[]
            {
                Line("[virtual movable area]", 0, 100, 300, 200),
                Line("[town movable area]", -10, 120, 20, 160, 1, 1, 290, 120, 20, 160, -1, -1),
            },
            ["map/u/only.map"] = new[] { Line("[virtual movable area]", 0, 0, 200, 200) },
        };
        return new TownAtlas(new Dictionary<int, string> { [1] = "town/t.twn", [2] = "town/u.twn" },
            p => scripts.TryGetValue(p, out ScriptLine[] doc) ? doc : null, _ => false);
    }

    /// <summary>用合成图册造的城镇世界:换区域要不要加载画面用真实的规则 <see cref="AtlasTownWorld.NeedsSplash(TownAtlas, TownPlace?, TownPlace)"/>。</summary>
    private sealed class SyntheticWorld : ITownWorld
    {
        private readonly TownAtlas _atlas = SampleAtlas();
        private readonly SplashScene _splash = new SplashScene("town", Block(800, 600), null, null, 0, 0, 1000.0);

        public List<TownPlace> Created { get; } = new List<TownPlace>();

        /// <summary>为 false 时所有换区域都不加载(老式行为)。</summary>
        public bool WithSplash { get; set; } = true;

        public TownPlace Start => new TownPlace(1, 0);

        public TownPlace Return => _atlas.FindDungeonGate(1).Value;

        public IHeroSession Create(TownPlace place)
        {
            Created.Add(place);
            TownAreaInfo info = _atlas.GetArea(place.Town, place.Area);
            return new FakeTownSession(place.Town + "/" + place.Area, info.Walkable, info.Exits, info.Bounds, _atlas.Arrive(place));
        }

        public SplashScene Splash(TownPlace? from, TownPlace to)
        {
            return WithSplash && AtlasTownWorld.NeedsSplash(_atlas, from, to) ? _splash : null;
        }

        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft)
        {
            return exit.HasTarget ? new TownPlace(exit.Town, exit.Area, TownEntry.Exit, from.Town, from.Area, facingLeft) : (TownPlace?)null;
        }

        public bool TryRoute(TownPlace from, out TownExit exit) => _atlas.TryRouteToDungeonGate(from, out exit);
    }

    private readonly List<DungeonSessionTests.Dungeon> _dungeons = new List<DungeonSessionTests.Dungeon>();

    public void Dispose()
    {
        foreach (DungeonSessionTests.Dungeon d in _dungeons)
        {
            d.Dispose();
        }
    }

    private static GameFlow FlowWithFakeRuns(ITownWorld world, List<FakeRun> runs = null, Func<string, SplashScene> dungeonSplash = null)
    {
        return new GameFlow(world, (path, run) =>
        {
            var r = new FakeRun(path, run);
            runs?.Add(r);
            return r;
        }, new[] { A }, null, dungeonSplash);
    }

    /// <summary>让加载画面走完(一帧一帧推进并轮询),直到离开加载阶段。</summary>
    private static void FinishSplash(GameFlow flow)
    {
        for (int i = 0; i < 600 && flow.Stage == GameStage.Loading; i++)
        {
            flow.Splash.Advance(FrameMs);
            flow.Poll();
        }
        Assert.NotEqual(GameStage.Loading, flow.Stage);
    }

    /// <summary>按住方向键走,直到流程换了阶段(出口被流程接手)或走满帧数。返回是否换了。</summary>
    private static bool Push(GameFlow flow, int dirX, int dirY, int maxFrames = 600)
    {
        int serial = flow.StageSerial;
        for (int i = 0; i < maxFrames && flow.StageSerial == serial; i++)
        {
            flow.Session.Update(FrameMs, dirX, dirY);
            flow.Poll();
        }
        return flow.StageSerial != serial;
    }

    // ---- 进城与加载 ----

    [Fact]
    public void EnteringTheTownFirstShowsTheLoadingSplashThenArrivesAtTheGatePoint()
    {
        var world = new SyntheticWorld();
        GameFlow flow = FlowWithFakeRuns(world);
        flow.EnterTown("start");

        Assert.Equal(GameStage.Loading, flow.Stage);
        Assert.NotNull(flow.Splash);
        Assert.Same(flow.Splash, flow.Scene);
        Assert.Null(flow.Session);
        Assert.Empty(world.Created);                                              // 会话在加载画面结束时才造
        flow.Splash.Advance(500);
        Assert.False(flow.Poll());                                                // 还没停够

        FinishSplash(flow);
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Null(flow.Splash);
        Assert.Equal(new TownPlace(1, 0), flow.Place);
        Assert.Equal(new[] { new TownPlace(1, 0) }, world.Created);
        Assert.Equal(new[] { (GameStage.Loading, "start"), (GameStage.Town, "start") }, flow.History.Select(e => (e.To, e.Reason)).ToArray());
        var town = (FakeTownSession)flow.Session;
        Assert.Equal((200.0, 150.0), (town.X, town.Y));                           // 网关登录:[gate] 给的位置
    }

    [Fact]
    public void AWorldWithoutSplashesEntersTheTownInstantly()
    {
        var world = new SyntheticWorld { WithSplash = false };
        GameFlow flow = FlowWithFakeRuns(world);
        flow.EnterTown("start");
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Null(flow.Splash);
        Assert.Single(flow.History);
    }

    [Fact]
    public void TheSplashRuleLoadsFromOutsideOnATownChangeAndAroundTheGateAreaButNotBetweenOrdinaryAreas()
    {
        TownAtlas atlas = SampleAtlas();
        Assert.True(AtlasTownWorld.NeedsSplash(atlas, null, new TownPlace(1, 1)));                        // 从城镇之外来(登录、副本)
        Assert.True(AtlasTownWorld.NeedsSplash(atlas, new TownPlace(1, 1), new TownPlace(2, 0)));         // 换城镇
        Assert.True(AtlasTownWorld.NeedsSplash(atlas, new TownPlace(1, 0), new TownPlace(1, 1)));         // 网关 → 普通
        Assert.True(AtlasTownWorld.NeedsSplash(atlas, new TownPlace(1, 1), new TownPlace(1, 0)));         // 普通 → 网关
        Assert.False(AtlasTownWorld.NeedsSplash(atlas, new TownPlace(1, 1), new TownPlace(1, 2)));        // 普通 → 地下城入口区域
        Assert.False(AtlasTownWorld.NeedsSplash(atlas, new TownPlace(1, 2), new TownPlace(1, 2)));        // 同一块(世界地图返回)
        Assert.False(AtlasTownWorld.NeedsSplash(atlas, new TownPlace(1, 2), new TownPlace(1, 9)));        // 图册里没有的区域不认作网关
    }

    // ---- 出口 ----

    [Fact]
    public void WalkingIntoAnExitChangesTheAreaCarriesTheFacingAndLoadsBetweenGateAndSquareOnly()
    {
        var world = new SyntheticWorld();
        GameFlow flow = FlowWithFakeRuns(world);
        flow.EnterTown("start");
        FinishSplash(flow);

        for (int i = 0; i < 6; i++)
        {
            flow.Session.Update(FrameMs, -1, 0);                                  // 先往左走几步:朝左
        }
        Assert.True(Push(flow, 0, 1));                                            // 再一直往下走到下边的出口
        Assert.Equal(GameStage.Loading, flow.Stage);                              // 网关 → 广场:加载画面
        Assert.Equal("exit 1/1", flow.History[flow.History.Count - 1].Reason);
        FinishSplash(flow);
        Assert.Equal(new TownPlace(1, 1), flow.Place);
        var square = (FakeTownSession)flow.Session;
        Assert.True(square.FacingLeft);                                           // 朝向带过出口
        Assert.Equal((250.0, 105.0), (square.X, square.Y));                       // 落在广场里通回网关的出口(上边)的中心
        Assert.Null(square.PendingExit);

        Assert.False(Push(flow, 0, 1, 30));                                       // 往下走离开落脚的出口矩形(往下不是它的外面方向)
        Assert.True(Push(flow, 1, 0));                                            // 一路往右走到右边的出口
        Assert.Equal(GameStage.Town, flow.Stage);                                 // 广场 → 入口区域:都是普通区域,不加载
        Assert.Equal(new TownPlace(1, 2), flow.Place);
        Assert.Equal("exit 1/2", flow.History[flow.History.Count - 1].Reason);
        Assert.False(((FakeTownSession)flow.Session).FacingLeft);                 // 最后一段是往右走的
    }

    [Fact]
    public void ArrivingInsideAnExitDoesNotBounceTheHeroBackWhileTheOutwardKeyIsStillHeld()
    {
        var world = new SyntheticWorld { WithSplash = false };
        GameFlow flow = FlowWithFakeRuns(world);
        flow.EnterTown("start");
        Assert.True(Push(flow, 0, 1));                                            // 网关 → 广场,落在广场上边的出口矩形里
        Assert.Equal(new TownPlace(1, 1), flow.Place);
        Assert.False(Push(flow, 0, -1, 120));                                     // 继续按着"上":落在出口里时不触发,必须先走出去再回来
        Assert.Equal(new TownPlace(1, 1), flow.Place);
    }

    [Fact]
    public void TheWorldMapExitOpensTheSelectScreenAndBackReturnsToTheDungeonGateAreaWithoutASplash()
    {
        var world = new SyntheticWorld();
        GameFlow flow = FlowWithFakeRuns(world);
        flow.EnterTown("start");
        FinishSplash(flow);
        Assert.True(Push(flow, 0, 1));                                            // 网关 → 广场(加载画面)
        FinishSplash(flow);
        Assert.False(Push(flow, 0, 1, 30));
        Assert.True(Push(flow, 1, 0));                                            // 广场 → 入口区域
        Assert.Equal(new TownPlace(1, 2), flow.Place);
        Assert.True(Push(flow, 1, 0));                                            // 往右走到通世界地图的出口

        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal("gate", flow.History[flow.History.Count - 1].Reason);
        Assert.Null(flow.Session);
        Assert.Same(flow.Select, flow.Scene);

        flow.Select.Back();
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.Town, flow.Stage);                                 // 同一块区域:没有加载画面
        Assert.Equal(new TownPlace(1, 2), flow.Place);
        Assert.Equal("select Back", flow.History[flow.History.Count - 1].Reason);
        var town = (FakeTownSession)flow.Session;
        Assert.Equal((300.0, 200.0), (town.X, town.Y));                           // 落在世界地图出口的中心(夹进可走区域里)
        Assert.True(town.FacingLeft);                                             // 朝向区域里面
    }

    [Fact]
    public void AnExitWithoutADestinationDoesNothingAndTheSessionKeepsGoing()
    {
        var world = new SyntheticWorld();
        GameFlow flow = FlowWithFakeRuns(world);
        flow.EnterTown("start");
        FinishSplash(flow);
        flow.Session.Update(FrameMs, 0, 0);
        Assert.Null(((FakeTownSession)flow.Session).PendingExit);
        Assert.False(flow.Poll());
        Assert.Equal(GameStage.Town, flow.Stage);
    }

    // ---- 副本进出 ----

    [Fact]
    public void StartingADungeonWaitsBehindItsLoadingSplashWhenOneIsGiven()
    {
        var world = new SyntheticWorld { WithSplash = false };
        var runs = new List<FakeRun>();
        GameFlow flow = FlowWithFakeRuns(world, runs, path => new SplashScene(path, Block(800, 600), null, null, 0, 0, 700.0));
        flow.StartDungeon(A.Path, "start");

        Assert.Equal(GameStage.Loading, flow.Stage);
        Assert.Equal(A.Path, flow.DungeonPath);
        Assert.Empty(runs);                                                       // 副本在加载画面之后才造
        Assert.Equal(0, flow.Runs);
        Assert.Null(flow.Run);
        FinishSplash(flow);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Single(runs);
        Assert.Equal(1, flow.Runs);
        Assert.Same(runs[0], flow.Run);
    }

    [Fact]
    public void RetryingTheSameDungeonShowsItsSplashAgainAndCountsTheRun()
    {
        var world = new SyntheticWorld { WithSplash = false };
        var runs = new List<FakeRun>();
        GameFlow flow = FlowWithFakeRuns(world, runs, path => new SplashScene(path, Block(800, 600), null, null, 0, 0, 700.0));
        flow.StartDungeon(A.Path, "start");
        FinishSplash(flow);
        runs[0].Choice = SettlementChoice.Retry;
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.Loading, flow.Stage);
        Assert.Equal(1, flow.Settlements);
        FinishSplash(flow);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Equal(new[] { 0, 1 }, runs.Select(r => r.Run).ToArray());           // 局序号一路往上
    }

    [Fact]
    public void NoDungeonSplashMeansTheDungeonStartsImmediately()
    {
        var world = new SyntheticWorld { WithSplash = false };
        GameFlow flow = FlowWithFakeRuns(world, dungeonSplash: path => null);
        flow.StartDungeon(A.Path, "start");
        Assert.Equal(GameStage.Dungeon, flow.Stage);
    }

    [Fact]
    public void ReturningFromADungeonShowsTheTownSplashAndLandsInTheDungeonGateArea()
    {
        var world = new SyntheticWorld();
        var runs = new List<FakeRun>();
        GameFlow flow = FlowWithFakeRuns(world, runs);
        flow.StartDungeon(A.Path, "start");
        runs[0].Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());

        Assert.Equal(GameStage.Loading, flow.Stage);                              // 从副本回来:来源不在城镇里,要加载画面
        FinishSplash(flow);
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal(new TownPlace(1, 2), flow.Place);
        Assert.Equal(TownEntry.Return, flow.Place.Value.Entry);
        var town = (FakeTownSession)flow.Session;
        Assert.Equal((300.0, 200.0), (town.X, town.Y));
    }

    // ---- 自动机 ----

    [Fact]
    public void TheBotWalksFromTheLoginAreaThroughTheSquareToTheWorldMapAndOnThroughTheDungeonAndBack()
    {
        var world = new SyntheticWorld();
        GameFlow flow = new GameFlow(world, (path, run) =>
        {
            var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.TwoRoomBossLayout(), (cx, cy) => DungeonSessionTests.None());
            _dungeons.Add(d);
            return d.Session;
        }, new[] { A });
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown });
        flow.EnterTown("start");

        int frames = 0;
        while (!bot.Done && frames < 60 * 60 * 10)
        {
            bot.Advance(flow, FrameMs);
            frames++;
        }

        Assert.True(bot.Done, "流程没有在 10 分钟(模拟)内走完");
        Assert.False(bot.Stuck);
        Assert.Equal(
            new[]
            {
                (GameStage.Loading, "start"),                    // 登录:加载画面
                (GameStage.Town, "start"),                       // 网关
                (GameStage.Loading, "exit 1/1"),                 // 网关 → 广场:加载画面
                (GameStage.Town, "exit 1/1"),
                (GameStage.Town, "exit 1/2"),                    // 广场 → 入口区域:瞬间
                (GameStage.DungeonSelect, "gate"),               // 入口区域的出口 → 世界地图
                (GameStage.Dungeon, "select AAA"),
                (GameStage.Loading, "choice ReturnToTown"),      // 回城:加载画面
                (GameStage.Town, "choice ReturnToTown"),
            },
            flow.History.Select(e => (e.To, e.Reason)).ToArray());
        Assert.Equal(new TownPlace(1, 2), flow.Place);
        Assert.Equal((1, 1, 1), (flow.Runs, flow.Settlements, bot.ChoicesMade));
    }

    [Fact]
    public void TheBotGivesUpOnAnUnreachableRouteAfterTheTimeoutAndLetsTheFlowContinue()
    {
        // 主角被困在一个点上(可走区域只有一个点),路线指向的出口永远够不着:走满超时就直接进副本选择
        var trapped = new FakeTownSession("trapped", new[] { new WorldRect(200, 150, 0, 0) }, Array.Empty<TownExit>(), null, new TownArrival(200, 150, false));
        GameFlow flow = FlowWithFakeRuns(new TrappedWorld(new SyntheticWorld { WithSplash = false }, trapped));
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown });
        flow.EnterTown("start");

        int frames = 0;
        while (flow.Stage == GameStage.Town && frames < 60 * 200)
        {
            bot.Advance(flow, FrameMs);
            frames++;
        }
        Assert.True(bot.Stuck);
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal("bot stuck", flow.History[flow.History.Count - 1].Reason);
        Assert.InRange(frames * FrameMs, FlowBot.TownTimeoutMs - 20.0, FlowBot.TownTimeoutMs + 1000.0);
        Assert.Equal((200.0, 150.0), (trapped.X, trapped.Y));
    }

    /// <summary>城镇世界的包装:所有区域都造同一个被困住的会话,路线照旧。</summary>
    private sealed class TrappedWorld : ITownWorld
    {
        private readonly SyntheticWorld _inner;
        private readonly IHeroSession _session;

        public TrappedWorld(SyntheticWorld inner, IHeroSession session)
        {
            _inner = inner;
            _session = session;
        }

        public TownPlace Start => _inner.Start;
        public TownPlace Return => _inner.Return;
        public IHeroSession Create(TownPlace place) => _session;
        public SplashScene Splash(TownPlace? from, TownPlace to) => _inner.Splash(from, to);
        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft) => _inner.Through(from, exit, facingLeft);
        public bool TryRoute(TownPlace from, out TownExit exit) => _inner.TryRoute(from, out exit);
    }

    [Fact]
    public void TheTownBotPushesOutwardOnTheExitAxisAndAlignsOnTheOtherOne()
    {
        var exit = new WorldRect(100, 90, 100, 30);                               // 上边的出口:中心 (150, 105)
        TownBot.Direction(40, 250, exit, 0, -1, out int dx, out int dy);
        Assert.Equal((1, -1), (dx, dy));                                          // 往右对准、往上推
        TownBot.Direction(151, 250, exit, 0, -1, out dx, out dy);
        Assert.Equal((0, -1), (dx, dy));                                          // 已对准(死区内):只推
        TownBot.Direction(300, 105, exit, 0, -1, out dx, out dy);
        Assert.Equal((-1, -1), (dx, dy));
        var right = new WorldRect(590, 120, 30, 160);                             // 右边的出口:中心 (605, 200)
        TownBot.Direction(10, 100, right, 1, 0, out dx, out dy);
        Assert.Equal((1, 1), (dx, dy));
        TownBot.Direction(10, 203, right, 1, 0, out dx, out dy);
        Assert.Equal((1, 0), (dx, dy));
        TownBot.Direction(10, 10, right, 0, 0, out dx, out dy);                   // 没有外面方向(没有可走区域):走向中心
        Assert.Equal((1, 1), (dx, dy));
    }

    // ---- 加载画面 ----

    [Fact]
    public void TheSplashDrawsTheCutsceneThenTheStripsThenTheTitleAndFinishesAfterItsDuration()
    {
        SpriteFrame cut = Block(800, 600), strips = Block(800, 600), title = Block(100, 40);
        var screen = new SplashScreen(new SplashScene("x", cut, strips, title, 259, 296, 1500.0));
        List<SceneSprite> sprites = screen.Build(0);

        Assert.Equal(3, sprites.Count);
        Assert.Same(cut, sprites[0].Frame);
        Assert.Same(strips, sprites[1].Frame);
        Assert.Same(title, sprites[2].Frame);
        Assert.Equal((0, 0), (sprites[0].ScreenX, sprites[0].ScreenY));
        Assert.Equal((259, 296), (sprites[2].ScreenX, sprites[2].ScreenY));
        Assert.True(sprites[0].SortKey < sprites[1].SortKey && sprites[1].SortKey < sprites[2].SortKey);

        Assert.False(screen.Finished);
        screen.Advance(1499);
        Assert.False(screen.Finished);
        screen.Advance(1);
        Assert.True(screen.Finished);
        screen.Advance(-50);                                                       // 负的时间差不倒退
        Assert.Equal(1500.0, screen.AgeMs);
    }

    [Fact]
    public void ASplashWithoutACutsceneFallsBackToABlackScreenAndOmitsTheMissingParts()
    {
        var screen = new SplashScreen(new SplashScene("x", null, null, null, 0, 0, 100.0));
        List<SceneSprite> sprites = screen.Build(0);
        Assert.Single(sprites);
        Assert.Equal((800, 600), (sprites[0].Frame.Width, sprites[0].Frame.Height));
        Assert.All(sprites[0].Frame.Rgba.Where((b, i) => i % 4 != 3), b => Assert.Equal((byte)0, b));   // 黑
        Assert.Throws<ArgumentNullException>(() => new SplashScreen(null));
    }

    [Fact]
    public void TheTownSplashPlacesTheTitleRelativeToTheScreenCenterPlusTheFrameAndImageOffsets()
    {
        var sprites = new FakeSprites();
        SpriteFrame cut = Block(800, 600), strips = Block(800, 600);
        sprites.Frames[("sprite/map/cutscene/demo.img", 2)] = cut;
        sprites.Frames[(WorldMapArt.UiImage, WorldMapArt.TopBottomBannerFrame)] = strips;
        SpriteFrame title = Block(120, 50, 7, 9);                                  // 图自带的偏移 (7, 9)
        sprites.Frames[("sprite/map/title/demo.img", 3)] = title;
        AniFile ani = AniFile.Parse(TestData.BuildAni(new[] { "Map/Title/Demo.img" }, Array.Empty<(ushort, byte[])>(),
            new[] { new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = 3, X = -100, Y = -20 } }));
        TownDef town = TownDef.Parse("town/demo.twn", new[]
        {
            Line("[entering title]", "Title/Demo.ani"),
            Line("[cutscene image]", "Map/CutScene/Demo.img", 2),
        });

        SplashScene scene = SplashCatalog.ForTown(sprites, p => p == "town/title/demo.ani" ? ani : null, town);

        Assert.NotNull(scene);
        Assert.Same(cut, scene.Cutscene);
        Assert.Same(strips, scene.Strips);
        Assert.Same(title, scene.Title);
        Assert.Equal((400 - 100 + 7, 300 - 20 + 9), (scene.TitleX, scene.TitleY));  // 屏幕中心 + 动画帧偏移 + 图自带偏移
        Assert.Equal(SplashCatalog.TownDurationMs, scene.DurationMs);
        Assert.Equal("town/demo.twn", scene.Name);
    }

    [Fact]
    public void ATownSplashNeedsACutsceneAndToleratesAMissingTitleOrStrips()
    {
        var sprites = new FakeSprites();
        sprites.Frames[("sprite/map/cutscene/demo.img", 0)] = Block(800, 600);
        TownDef onlyTitle = TownDef.Parse("t", new[] { Line("[entering title]", "Title/X.ani") });
        Assert.Null(SplashCatalog.ForTown(sprites, p => null, onlyTitle));
        Assert.Null(SplashCatalog.ForTown(sprites, p => null, null));

        TownDef noTitle = TownDef.Parse("t", new[] { Line("[cutscene image]", "Map/CutScene/Demo.img", 0) });
        SplashScene scene = SplashCatalog.ForTown(sprites, p => null, noTitle);
        Assert.NotNull(scene);
        Assert.NotNull(scene.Cutscene);
        Assert.Null(scene.Title);
        Assert.Null(scene.Strips);                                                 // 边条图缺失:不画

        TownDef missingTitleArt = TownDef.Parse("t", new[] { Line("[entering title]", "Title/Y.ani"), Line("[cutscene image]", "Map/CutScene/Demo.img", 0) });
        Assert.Null(SplashCatalog.ForTown(sprites, p => null, missingTitleArt).Title);   // 标题动画读不到
    }

    [Fact]
    public void ADungeonSplashReadsTheCutsceneImageTagAndHasNoTitle()
    {
        var sprites = new FakeSprites();
        SpriteFrame cut = Block(800, 600);
        sprites.Frames[("sprite/map/cutscene/forest.img", 1)] = cut;
        var doc = new[]
        {
            Line("[entering title]", "Title/Forest.ani"),
            Line("[cutscene image]", "Map/CutScene/Forest.img", 1),
        };
        SplashScene scene = SplashCatalog.ForDungeon(sprites, "dungeon/x.dgn", doc);
        Assert.Same(cut, scene.Cutscene);
        Assert.Null(scene.Title);
        Assert.Equal(SplashCatalog.DungeonDurationMs, scene.DurationMs);
        Assert.Null(SplashCatalog.ForDungeon(sprites, "dungeon/x.dgn", new[] { doc[0] }));   // 没写 [cutscene image]
        Assert.Null(SplashCatalog.ForDungeon(sprites, "dungeon/x.dgn", null));
    }
}
