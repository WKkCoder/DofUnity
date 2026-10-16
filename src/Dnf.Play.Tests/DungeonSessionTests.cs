using Dnf.Data;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 整座副本的会话(证据 E-070):出口随清房打开、走进打开的出口换房间并按对侧出口摆好主角、房间状态保留、首领清空与走出迷宫的收尾、小地图、
/// 帧长无关、自动机的路线。迷宫是合成的 2 × 2 的环,房间是合成的迷你房间(五块全可走的瓦片、四个出口位置),怪物是夹具里的迷你怪物,数据全是合成的。
///
///   (0,0) J = 右 + 下(起点)      (1,0) M = 左 + 下
///   (0,1) D = 右 + 上            (1,1) G = 上 + 左(首领)
///
/// 房间宽 1120(五块瓦片),镜头最大 320;出口位置:左 (16,120)、右 (1104,120)、上 (560,144)、下 (560,348)。主角出生在 (300,100) 朝右。
/// </summary>
public class DungeonSessionTests
{
    private const int HeroX = 300;
    private const int HeroY = 100;

    private static ScriptLine Line(string tag, params int[] numbers)
    {
        var values = new ScriptValue[numbers.Length];
        for (int i = 0; i < numbers.Length; i++)
        {
            values[i] = new ScriptValue(ScriptValueKind.Int, numbers[i], null);
        }
        return new ScriptLine(tag, values);
    }

    private static DungeonLayout RingLayout()
    {
        var doc = new[]
        {
            Line("[size]", 2, 2),
            new ScriptLine("[greed]", new[] { new ScriptValue(ScriptValueKind.String, 0, "JJMM DDGG") }),
            Line("[start map]", 0, 0, 0, 0),
            Line("[boss map]", 1, 1, 1, 1),
        };
        DungeonMaze maze = DungeonMaze.Parse("dungeon/test/ring.dgn", doc)!;
        var pool = new DungeonRoomPool(new[]
        {
            new RoomCandidate("map/t/a.map", 1, 9, 9, false, true),
            new RoomCandidate("map/t/b.map", 2, 12, 12, false, false),
            new RoomCandidate("map/t/c.map", 3, 3, 3, false, false),
            new RoomCandidate("map/t/d.map", 4, 6, 6, true, false),
        });
        return DungeonLayout.Create(maze, pool);
    }

    private static DungeonLayout SingleLayout(string greed)
    {
        var doc = new[]
        {
            Line("[size]", 1, 1),
            new ScriptLine("[greed]", new[] { new ScriptValue(ScriptValueKind.String, 0, greed) }),
        };
        DungeonMaze maze = DungeonMaze.Parse("dungeon/test/single.dgn", doc)!;
        int code = greed[0] - 'A';
        var pool = new DungeonRoomPool(new[] { new RoomCandidate("map/t/only.map", 1, code, code, false, true) });
        return DungeonLayout.Create(maze, pool);
    }

    /// <summary>用夹具造出每一格的迷你房间;<paramref name="monstersOf"/> 给每一格的怪物位置。</summary>
    private sealed class Dungeon : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();
        private readonly Func<int, int, (int X, int Y)[]> _monstersOf;
        private readonly int _hp;

        public DnfAssets Assets { get; }
        public DungeonLayout Layout { get; }
        public DungeonSession Session { get; }

        /// <summary>每格的房间只造一次(会话也只会造一次),方便测试直接取到房间里的对象。</summary>
        public Dictionary<(int X, int Y), RoomCombatSession> Rooms { get; } = new();

        public Dungeon(DungeonLayout layout, Func<int, int, (int X, int Y)[]> monstersOf, int hp = 10)
        {
            Assets = DnfAssets.Open(_fx.Root);
            Layout = layout;
            _monstersOf = monstersOf;
            _hp = hp;
            Session = new DungeonSession(layout, Build);
        }

        private RoomCombatSession Build(int cx, int cy)
        {
            var tiles = new List<TileFile>();
            for (int i = 0; i < 5; i++)
            {
                tiles.Add(new TileFile("t" + i, new List<KeyValuePair<string, int>>(), 80, new byte[14 * 30]));
            }
            var gatePos = new List<int[]> { new[] { 16, 120 }, new[] { 1104, 120 }, new[] { 560, 144 }, new[] { 560, 348 } };
            var map = new RoomMap("map/test/cell" + cx + cy + ".map", "[normal]", tiles, 50, 80, 120, new List<RoomBackground>(), new List<MapAnimation>(),
                gatePos, null);
            var gates = new List<RoomGate>();
            foreach (GateSide side in new[] { GateSide.Left, GateSide.Right, GateSide.Up, GateSide.Down })
            {
                if (Layout.GateKindAt(cx, cy, side) == GateKind.None)
                {
                    continue;
                }
                int[] p = gatePos[(int)side];
                gates.Add(new RoomGate(Assets, new GateObject(800 + (int)side, "x.obj", "[normal]", new List<GatePart>(), null), side, p[0], p[1]));
            }
            var content = new RoomContent(map, null, null, null, gates);
            AvatarClass cls = AvatarClass.FromChr(Assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
            AvatarOutfit outfit = AvatarOutfit.CreateDefault(Assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
            TownHero hero = TownHero.Create(Assets, AvatarFixture.ChrPath, outfit, new RoomWalkArea(map), HeroX, HeroY, false);
            hero.LoadBattleRest(Assets, AvatarFixture.ChrPath);
            hero.BattleStance = true;
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));

            MonsterModel model = MonsterModel.Read(Assets, AvatarFixture.BlobMobPath);
            var actors = new List<MonsterActor>();
            (int X, int Y)[] at = _monstersOf(cx, cy);
            for (int i = 0; i < at.Length; i++)
            {
                var spawn = new RoomMonsterSpawn(1, 0, 0, at[i].X, at[i].Y, 0, 1, 0, "[fixed]", "[normal]");
                actors.Add(new MonsterActor(i, spawn, model, 1, _hp, 222.5, 1500, 0));
            }
            var strikes = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
            var session = new RoomCombatSession(Assets, room, actors, strikes, 1 + cy * 2 + cx);
            Rooms[(cx, cy)] = session;
            return session;
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    private static (int X, int Y)[] None() => Array.Empty<(int, int)>();

    /// <summary>起点格有一只离主角 40 像素远的弱怪物,其余格没有怪物。</summary>
    private static (int X, int Y)[] OneWeakAtStart(int cx, int cy) => cx == 0 && cy == 0 ? new[] { (340, 100) } : None();

    private static void Run(DungeonSession s, int ms, int dirX = 0, int dirY = 0, bool attackFirst = false)
    {
        for (int t = 0; t < ms; t += 10)
        {
            s.Update(10, dirX, dirY, attackFirst && t == 0);
        }
    }

    private static void RunUntil(DungeonSession s, Func<bool> done, int dirX, int dirY, int maxMs = 20000)
    {
        for (int t = 0; t < maxMs && !done(); t += 10)
        {
            s.Update(10, dirX, dirY, false);
        }
        Assert.True(done(), "条件在 " + maxMs + " 毫秒内没有满足");
    }

    private static RoomGate GateOf(DungeonSession s, GateSide side)
    {
        return s.Current.Room.Content.Gates.First(g => g.Side == side);
    }

    [Fact]
    public void TheDungeonStartsInTheStartCellWithAllItsGatesShutAndTheBattleStance()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart);
        DungeonSession s = d.Session;
        Assert.Equal((0, 0), (s.CellX, s.CellY));
        Assert.Equal((1, 0, 0, 0), (s.RoomsEntered, s.RoomsCleared, s.Transitions, s.Hits));
        Assert.False(s.Finished);
        Assert.Equal(1, s.Current.AliveCount);
        Assert.True(s.Hero.BattleStance);
        Assert.Equal("map/test/cell00.map", s.MapPath);
        // 起点格 J:左边是墙、右与下是普通出口、上边没有 → 三个出口物件(墙、右、下)
        Assert.Equal(new[] { GateSide.Left, GateSide.Right, GateSide.Down }, s.Current.Room.Content.Gates.Select(g => g.Side).ToArray());
        Assert.All(s.Current.Room.Content.Gates, g => Assert.Equal(GateState.Closed, g.State));
        Assert.Single(d.Rooms);                                         // 只造了起点格
    }

    [Fact]
    public void TheNormalGatesOpenWhenTheLastMonsterIsGoneAndTheWallStaysShut()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart);
        DungeonSession s = d.Session;
        double clearedAt = -1;
        s.Update(10, 0, 0, true);
        for (int t = 0; t < 1000; t += 10)
        {
            s.Update(10, 0, 0, false);
            if (s.RoomsCleared == 1 && clearedAt < 0)
            {
                clearedAt = s.ClockMs;
            }
        }
        Assert.True(clearedAt > 0);
        // 命中在挥击开始后 200 毫秒,怪物死亡动画 500 毫秒:清房在 700 毫秒上下,而不是挥击的当时
        Assert.InRange(clearedAt, 690, 760);
        Assert.Equal((1, 1, true), (s.RoomsCleared, s.Kills, s.IsCleared(0, 0)));
        Assert.False(s.Hero.BattleStance);                               // 杀光之后站普通待机

        // 第一次出画面时才开门,开门时刻按"清房到现在过了多久"倒推到 Build 的时间线上
        Assert.Equal(GateState.Closed, GateOf(s, GateSide.Right).State);
        s.Build(5000);
        double sinceCleared = s.ClockMs - clearedAt;
        Assert.Equal(GateState.Open, GateOf(s, GateSide.Right).State);
        Assert.Equal(GateState.Open, GateOf(s, GateSide.Down).State);
        Assert.Equal(GateState.Closed, GateOf(s, GateSide.Left).State);  // 墙不开
        Assert.InRange(GateOf(s, GateSide.Right).OpenedAtMs, 5000 - sinceCleared - 1, 5000 - sinceCleared + 1);
        // 再出一次画面不会重新计时
        long first = GateOf(s, GateSide.Right).OpenedAtMs;
        s.Build(9000);
        Assert.Equal(first, GateOf(s, GateSide.Right).OpenedAtMs);
    }

    [Fact]
    public void WalkingIntoAShutGateWhileMonstersLiveDoesNotLeaveTheRoom()
    {
        using var d = new Dungeon(RingLayout(), (cx, cy) => new[] { (900, 300) }, hp: 100000);
        DungeonSession s = d.Session;
        Run(s, 6000, 1, 0);
        Assert.True(s.Hero.X > 1090);                                    // 走到了右出口跟前(出口的触发范围是 24 像素)
        Assert.Equal((0, 0, 0, false), (s.CellX, s.CellY, s.Transitions, s.IsCleared(0, 0)));
        Run(s, 3000, 0, 1);                                              // 下出口也一样
        Assert.Equal((0, 0, 0), (s.CellX, s.CellY, s.Transitions));
        Assert.Single(d.Rooms);
    }

    [Fact]
    public void ARoomWithoutMonstersIsClearedOnEntryAndItsGatesAreSettledOpen()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart);
        DungeonSession s = d.Session;
        Run(s, 1000, 0, 0, true);
        RunUntil(s, () => s.CellX == 1, 1, 0);                           // 出右出口
        Assert.Equal((1, 0, 1), (s.CellX, s.CellY, s.Transitions));
        Assert.Equal((2, 2), (s.RoomsEntered, s.RoomsCleared));          // 进来就清空了
        Assert.True(s.IsCleared(1, 0));
        Assert.Empty(s.Current.Monsters);
        Assert.False(s.Hero.BattleStance);
        s.Build(100);
        // (1,0) M:左 = 普通(回起点格)、下 = 首领出口、右是墙。没有怪物:直接开着,不播淡出
        Assert.Equal(new[] { GateSide.Left, GateSide.Right, GateSide.Down }, s.Current.Room.Content.Gates.Select(g => g.Side).ToArray());
        Assert.Equal((GateState.Open, RoomGate.SettledOpenedAtMs), (GateOf(s, GateSide.Left).State, GateOf(s, GateSide.Left).OpenedAtMs));
        Assert.Equal((GateState.Open, RoomGate.SettledOpenedAtMs), (GateOf(s, GateSide.Down).State, GateOf(s, GateSide.Down).OpenedAtMs));
        Assert.Equal(GateState.Closed, GateOf(s, GateSide.Right).State);
    }

    [Fact]
    public void LeavingThroughTheRightGateEntersTheNextRoomBy74PixelsFromItsLeftGateKeepingYAndFacing()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart);
        DungeonSession s = d.Session;
        Run(s, 1000, 0, 0, true);
        Run(s, 600, 0, 1);                                               // 往下走一点,让 y 与出生时不同
        double y = s.Hero.Y;
        Assert.True(y > HeroY + 20);
        RunUntil(s, () => s.CellX == 1, 1, 0);
        Assert.Equal((1, 0), (s.CellX, s.CellY));
        TownHero hero = s.Hero;
        Assert.Equal(16 + DungeonSession.EntryOffsetLeftGate, hero.X);   // 左出口 x = 16
        Assert.Equal(90, hero.X);
        Assert.InRange(hero.Y, y - 1.5, y + 1.5);                        // y 保持(离开那一步之后最多再走一小步)
        Assert.False(hero.FacingLeft);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        Assert.Equal(0, s.Camera.IntX);                                  // 居中再夹住:90 − 400 < 0
    }

    [Fact]
    public void GoingBackEntersByTheRightGateFacingLeftAndTheClearedRoomStaysClearedAndOpen()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart);
        DungeonSession s = d.Session;
        Run(s, 1000, 0, 0, true);
        Run(s, 600, 0, 1);
        s.Build(100);
        RunUntil(s, () => s.CellX == 1, 1, 0);
        RunUntil(s, () => s.CellX == 0, -1, 0);                          // 走左出口回去
        Assert.Equal((0, 0, 2), (s.CellX, s.CellY, s.Transitions));
        TownHero hero = s.Hero;
        Assert.Equal(1104 - DungeonSession.EntryOffsetRightGate, hero.X);
        Assert.Equal(1034, hero.X);
        Assert.True(hero.FacingLeft);
        Assert.Equal(320, s.Camera.IntX);                                // 1034 − 400 = 634,夹到最大 320
        Assert.Same(d.Rooms[(0, 0)], s.Current);
        Assert.Equal((0, 1), (s.Current.AliveCount, s.Current.Monsters.Count(m => m.IsGone)));   // 怪物没有复活
        Assert.False(hero.BattleStance);
        Assert.Equal(GateState.Open, GateOf(s, GateSide.Right).State);   // 出口一直开着
        Assert.Equal((2, 2), (s.RoomsEntered, s.RoomsCleared));          // 回来不重复计数
    }

    [Fact]
    public void VerticalGatesPlaceTheHeroOnTheInsideOfTheOppositeGateAndKeepTheFacing()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart);
        DungeonSession s = d.Session;
        Run(s, 1000, 0, 0, true);
        s.Hero.Place(560, 300, true, HeroMotion.Rest, 0);                // 站到下出口(560, 348)的正上方,朝左
        RunUntil(s, () => s.CellY == 1, 0, 1);
        Assert.Equal((0, 1), (s.CellX, s.CellY));
        // (0,1) D 的上出口在 (560, 144):主角在它下面 24 像素、x 与出口相同、朝向保持
        Assert.Equal((560.0, 144.0 + DungeonSession.EntryOffsetVertical, true), (s.Hero.X, s.Hero.Y, s.Hero.FacingLeft));
        Assert.Equal(160, s.Camera.IntX);                                // 560 − 400
        // 再回上去:从上出口出来,进入 (0,0) 的下出口(560, 348)上面 24 像素
        s.Build(0);
        RunUntil(s, () => s.CellY == 0, 0, -1);
        Assert.Equal((560.0, 348.0 - DungeonSession.EntryOffsetVertical), (s.Hero.X, s.Hero.Y));
    }

    [Fact]
    public void ClearingTheBossRoomSetsBossClearedFinishesTheDungeonAndShowsTheBanner()
    {
        // 路线:起点格杀怪 → 下到 (0,1) → 右边是首领出口 → 进首领格 (1,1),从它的左出口进来,怪物就在主角前面 40 像素
        using var d = new Dungeon(RingLayout(), (cx, cy) => cx == 0 && cy == 0 ? new[] { (340, 100) } : (cx == 1 && cy == 1 ? new[] { (130, 168) } : None()));
        DungeonSession s = d.Session;
        Run(s, 1000, 0, 0, true);
        s.Hero.Place(560, 300, false, HeroMotion.Rest, 0);
        RunUntil(s, () => s.CellY == 1, 0, 1);
        Assert.Equal((0, 1), (s.CellX, s.CellY));
        Assert.Equal(GateKind.Boss, d.Layout.GateKindAt(0, 1, GateSide.Right));
        RunUntil(s, () => s.CellX == 1, 1, 0);
        Assert.Equal((1, 1), (s.CellX, s.CellY));
        Assert.Equal((90.0, 168.0), (s.Hero.X, s.Hero.Y));
        Assert.False(s.BossCleared);
        Assert.DoesNotContain(s.Build(10), x => x.AnimationPath == "clear");
        Assert.Equal(1, s.Current.AliveCount);

        Run(s, 1000, 0, 0, true);
        Assert.Equal((true, true), (s.BossCleared, s.Finished));
        Assert.False(s.ExitReached);
        Assert.Equal(3, s.RoomsCleared);                                 // 起点格、(0,1)、首领格
        Assert.Contains(s.Build(2000), x => x.AnimationPath == "clear");
    }

    [Fact]
    public void AnExitOutsideTheMazeEndsTheDungeonWithoutChangingRooms()
    {
        using var d = new Dungeon(SingleLayout("BB"), (cx, cy) => None());
        DungeonSession s = d.Session;
        Assert.Equal((1, 1), (s.RoomsEntered, s.RoomsCleared));          // 没有怪物:进来就清空
        Assert.False(s.Finished);
        RunUntil(s, () => s.ExitReached, 1, 0);
        Assert.Equal((true, true, false), (s.Finished, s.ExitReached, s.BossCleared));
        Assert.Equal((0, 0, 0), (s.CellX, s.CellY, s.Transitions));
        Run(s, 500, 1, 0);                                               // 走完之后继续更新也没事
        Assert.True(s.ExitReached);
    }

    [Fact]
    public void ALongFrameGivesTheSameResultAsManySmallOnes()
    {
        using var a = new Dungeon(RingLayout(), OneWeakAtStart);
        using var b = new Dungeon(RingLayout(), OneWeakAtStart);
        a.Session.Update(10, 0, 0, true);
        b.Session.Update(10, 0, 0, true);
        for (int i = 0; i < 100; i++)
        {
            a.Session.Update(10, 1, 0, false);
        }
        for (int i = 0; i < 10; i++)
        {
            b.Session.Update(100, 1, 0, false);                          // 同样的 1000 毫秒,一帧 100 毫秒
        }
        Assert.Equal(a.Session.ClockMs, b.Session.ClockMs, 6);
        Assert.Equal((a.Session.CellX, a.Session.CellY, a.Session.Transitions), (b.Session.CellX, b.Session.CellY, b.Session.Transitions));
        Assert.Equal(a.Session.Hero.X, b.Session.Hero.X, 6);
        Assert.Equal((a.Session.Hits, a.Session.Kills, a.Session.TotalDamage), (b.Session.Hits, b.Session.Kills, b.Session.TotalDamage));
        Assert.Equal(a.Session.RoomsCleared, b.Session.RoomsCleared);
        // 零长度或负的一帧不推进时间
        double clock = a.Session.ClockMs;
        a.Session.Update(0, 1, 0);
        a.Session.Update(-3, 1, 0);
        Assert.Equal(clock, a.Session.ClockMs, 6);
    }

    [Fact]
    public void TheMiniMapShowsTheCurrentRoomAndWhatIsNextToVisitedRoomsAndCountsClearedRooms()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart);
        DungeonSession s = d.Session;
        Assert.Equal(MiniMapState.Current, s.MiniStateOf(0, 0));
        Assert.Equal(MiniMapState.Seen, s.MiniStateOf(1, 0));            // 起点格的右出口通向它
        Assert.Equal(MiniMapState.Seen, s.MiniStateOf(0, 1));
        Assert.Equal(MiniMapState.Hidden, s.MiniStateOf(1, 1));          // 与起点格不相邻
        List<SceneSprite> hud = s.Build(0).Where(x => x.AnimationPath != null && x.AnimationPath.StartsWith("minimap", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "minimap", "minimap-count" }, hud.Select(x => x.AnimationPath).ToArray());
        Assert.Equal(800 - OverlayArt.MiniMapWidth(2) - DungeonSession.MiniMapMarginRight, hud[0].ScreenX);
        Assert.Equal(OverlayArt.MiniMapWidth(2), hud[0].Frame.Width);
        Assert.Equal(OverlayArt.MiniMapHeight(2), hud[0].Frame.Height);
        Assert.Equal(OverlayArt.UiRank, hud[0].LayerRank);

        Run(s, 1000, 0, 0, true);
        RunUntil(s, () => s.CellX == 1, 1, 0);
        Assert.Equal((1, 0), (s.CellX, s.CellY));
        Assert.Equal(MiniMapState.Cleared, s.MiniStateOf(0, 0));
        Assert.Equal(MiniMapState.Current, s.MiniStateOf(1, 0));
        Assert.Equal(MiniMapState.Seen, s.MiniStateOf(1, 1));            // (1,0) 的下出口通向首领格
        Assert.Equal(MiniMapState.Seen, s.MiniStateOf(0, 1));            // 与已进过的起点格相邻
        // 数字是 已清/总数
        var art = new OverlayArt();
        SpriteFrame expected = art.Text("2/4", 2, OverlayArt.White);
        SceneSprite count = s.Build(0).First(x => x.AnimationPath == "minimap-count");
        Assert.Equal(expected.Rgba, count.Frame.Rgba);
    }

    [Fact]
    public void TheBotFindsTheShortestRouteToTheBossAndTheExploringBotVisitsEveryRoomFirst()
    {
        using var d = new Dungeon(RingLayout(), (cx, cy) => None());
        DungeonSession s = d.Session;
        Assert.True(DungeonBot.NextSide(s, false, out GateSide toBoss));
        Assert.Equal(GateSide.Right, toBoss);                            // 两条一样短的路:先试右
        Assert.True(DungeonBot.NextSide(s, true, out GateSide firstStop));
        Assert.Equal(GateSide.Right, firstStop);                         // 最近的没进过的格是 (1,0)

        // 把 (1,0) 与 (0,1) 都走一遍之后,探索模式也直接去首领格
        s.Hero.Place(560, 300, false, HeroMotion.Rest, 0);
        RunUntil(s, () => s.CellY == 1, 0, 1);                           // 到 (0,1)
        Assert.True(DungeonBot.NextSide(s, true, out GateSide next));
        Assert.Equal(GateSide.Up, next);                                 // 还剩 (1,0) 没进过:回到起点格再右,第一步是上
    }

    [Fact]
    public void TheBotPlaysTheWholeSyntheticDungeonThroughToTheBossClear()
    {
        (int X, int Y)[] Monsters(int cx, int cy)
        {
            return cx == 0 && cy == 0 ? new[] { (340, 100), (420, 120) }
                : cx == 1 && cy == 0 ? new[] { (300, 200) }
                : cx == 1 && cy == 1 ? new[] { (400, 150), (500, 180) }
                : None();
        }

        foreach (bool explore in new[] { false, true })
        {
            using var d = new Dungeon(RingLayout(), Monsters, hp: 40);
            DungeonSession s = d.Session;
            var bot = new DungeonBot(explore);
            for (int step = 0; step < 60 * 120 && !s.Finished; step++)
            {
                bool attack = bot.Next(s, out int dx, out int dy);
                s.Update(CombatScript.StepMs, dx, dy, attack);
            }
            Assert.True(s.BossCleared, "探索 = " + explore + ":2 分钟内没有打通");
            Assert.Equal(5, s.Kills);
            Assert.InRange(s.RoomsEntered, explore ? 4 : 3, 4);
            if (explore)
            {
                Assert.True(s.IsVisited(1, 0) && s.IsVisited(0, 1) && s.IsVisited(0, 0) && s.IsVisited(1, 1));
            }
            // 走完之后自动机什么也不按
            Assert.False(bot.Next(s, out int ex, out int ey));
            Assert.Equal((0, 0), (ex, ey));
        }
    }

    [Fact]
    public void ConstructorAndFactoryRejectBadArguments()
    {
        DungeonLayout layout = RingLayout();
        Assert.Throws<ArgumentNullException>(() => new DungeonSession(null!, (x, y) => null!));
        Assert.Throws<ArgumentNullException>(() => new DungeonSession(layout, null!));
        Assert.Throws<InvalidOperationException>(() => new DungeonSession(layout, (x, y) => null!));
    }
}
