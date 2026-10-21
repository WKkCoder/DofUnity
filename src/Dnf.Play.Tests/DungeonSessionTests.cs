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
/// 房间宽 1120(五块瓦片),镜头最大 320;出口位置:左 (16,120)、右 (1104,120)、上 (560,12)、下 (560,348)(上下出口紧贴可走带的上下边缘:y = 0 与 y = 360,
/// 这样顶着边缘走正好落在出口的识别矩形里,与原版地图一致)。主角出生在 (300,100) 朝右。
/// </summary>
public class DungeonSessionTests
{
    private const int HeroX = 300;
    private const int HeroY = 100;

    /// <summary>进房间落点的随机种子用的主角对象编号(种子 = 它 × 地图编号);下面的参考落点由独立的 Python 实现按客户端的公式算出(证据 E-074)。</summary>
    private const int TestHeroId = 1002;

    // 带碰撞体的合成出口:左边的墙占格 1 到 4(x ∈ [16, 80))并向左延伸到地图左缘,右边的墙占格 67 到 69(x ∈ [1072, 1120));
    // 主角占位盒 40 × 10(上下各 5):盒的左沿贴住墙(x − 20 = 80)时脚下原点 x = 100;盒的右沿 x + 20 必须 ≤ 1071,所以实数坐标停在 1052 之前一点点(向下取整 ≤ 1051)
    private const int LeftWedgeColumns = 4;
    private const int RightWedgeColumns = 3;
    private const double BesideLeftWallX = 16 + LeftWedgeColumns * 16 + 20;
    private const double BesideRightWallX = 1104 - 13 * 16 + (14 - RightWedgeColumns) * 16 - 20;

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

    /// <summary>
    /// 一横排两格的副本:(0,0) 起点、只有右出口(B);(1,0) 首领格、只有左出口(E)。出口码:右 1、上 2、左 4、下 8。
    /// 起点与首领不能是同一格,所以一个格的副本造不出首领;这里两格都没有怪物,向右走进首领格的那一刻首领格就清空了。
    /// </summary>
    internal static DungeonLayout TwoRoomBossLayout()
    {
        var doc = new[]
        {
            Line("[size]", 2, 1),
            new ScriptLine("[greed]", new[] { new ScriptValue(ScriptValueKind.String, 0, "BBEE") }),
            Line("[start map]", 0, 0, 0, 0),
            Line("[boss map]", 1, 0, 1, 0),
        };
        DungeonMaze maze = DungeonMaze.Parse("dungeon/test/tworoom.dgn", doc)!;
        var pool = new DungeonRoomPool(new[]
        {
            new RoomCandidate("map/t/start.map", 1, 1, 1, false, true),
            new RoomCandidate("map/t/boss.map", 2, 4, 4, true, false),
        });
        return DungeonLayout.Create(maze, pool);
    }

    /// <summary>用夹具造出每一格的迷你房间;<paramref name="monstersOf"/> 给每一格的怪物位置。</summary>
    internal sealed class Dungeon : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();
        private readonly Func<int, int, (int X, int Y)[]> _monstersOf;
        private readonly int _hp;
        private readonly bool _bodies;
        private readonly Func<HeroVitals> _vitalsOf;
        private readonly Func<int, int, string[]> _soundsOf;

        public DnfAssets Assets { get; }
        public DungeonLayout Layout { get; }
        public DungeonSession Session { get; }

        /// <summary>每格的房间只造一次(会话也只会造一次),方便测试直接取到房间里的对象。</summary>
        public Dictionary<(int X, int Y), RoomCombatSession> Rooms { get; } = new();

        /// <summary>
        /// <paramref name="bodies"/> 为真时给左右出口装上合成的碰撞体:左边是 4 列宽的墙(x 从 16 到 80),右边是 3 列宽的墙(x 从 1072 到 1120),行数覆盖整个房间;
        /// 为假时左右出口没有碰撞体(退回近似的触发)。
        /// </summary>
        public Dungeon(DungeonLayout layout, Func<int, int, (int X, int Y)[]> monstersOf, int hp = 10, bool bodies = false, int heroObjectId = TestHeroId,
            Func<HeroVitals> vitalsOf = null, Func<int, int, string[]> soundsOf = null)
        {
            Assets = DnfAssets.Open(_fx.Root);
            Layout = layout;
            _monstersOf = monstersOf;
            _hp = hp;
            _bodies = bodies;
            _vitalsOf = vitalsOf;
            _soundsOf = soundsOf;
            Session = new DungeonSession(layout, Build, heroObjectId, Assets);
        }

        /// <summary>整个房间高(30 行,y 从 0 到 360)的墙:左边的墙占前 <see cref="LeftWedgeColumns"/> 列,右边的墙占最后 <see cref="RightWedgeColumns"/> 列。</summary>
        private static TileFile WallTile(GateSide side)
        {
            var pass = new byte[14 * 30];
            for (int r = 0; r < 30; r++)
            {
                for (int c = 0; c < 14; c++)
                {
                    bool solid = side == GateSide.Left ? c < LeftWedgeColumns : c >= 14 - RightWedgeColumns;
                    pass[r * 14 + c] = solid ? (byte)2 : (byte)0;
                }
            }
            return new TileFile("wall", new List<KeyValuePair<string, int>>(), 80, pass);
        }

        private RoomCombatSession Build(int cx, int cy)
        {
            var tiles = new List<TileFile>();
            for (int i = 0; i < 5; i++)
            {
                tiles.Add(new TileFile("t" + i, new List<KeyValuePair<string, int>>(), 80, new byte[14 * 30]));
            }
            var gatePos = new List<int[]> { new[] { 16, 120 }, new[] { 1104, 120 }, new[] { 560, 12 }, new[] { 560, 348 } };
            var map = new RoomMap("map/test/cell" + cx + cy + ".map", "[normal]", tiles, 50, 80, 120, new List<RoomBackground>(), new List<MapAnimation>(),
                gatePos, null, sounds: _soundsOf?.Invoke(cx, cy));
            var gates = new List<RoomGate>();
            foreach (GateSide side in new[] { GateSide.Left, GateSide.Right, GateSide.Up, GateSide.Down })
            {
                if (Layout.GateKindAt(cx, cy, side) == GateKind.None)
                {
                    continue;
                }
                int[] p = gatePos[(int)side];
                var gate = new RoomGate(Assets, new GateObject(800 + (int)side, "x.obj", "[normal]", new List<GatePart>(), null), side, p[0], p[1]);
                if (_bodies && (side == GateSide.Left || side == GateSide.Right))
                {
                    // 锚点:左边的墙在 (0 列, 10 行),右边在 (13 列, 10 行) → 通行格的左上角是 (出口 x − 列 × 16, 0)
                    gate.SetBody(WallTile(side), side == GateSide.Left ? 0 : 13, 10);
                }
                gates.Add(gate);
            }
            var content = new RoomContent(map, null, null, null, gates);
            AvatarClass cls = AvatarClass.FromChr(Assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
            AvatarOutfit outfit = AvatarOutfit.CreateDefault(Assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
            TownHero hero = TownHero.Create(Assets, AvatarFixture.ChrPath, outfit, new RoomWalkArea(map), HeroX, HeroY, false);
            hero.LoadBattleRest(Assets, AvatarFixture.ChrPath);
            hero.BattleStance = true;
            hero.Blocker = new GateBodies(gates);
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
            var session = new RoomCombatSession(Assets, room, actors, strikes, 1 + cy * 2 + cx, vitals: _vitalsOf?.Invoke());
            Rooms[(cx, cy)] = session;
            return session;
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    internal static (int X, int Y)[] None() => Array.Empty<(int, int)>();

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
        Assert.True(s.Hero.X > 1092);                                    // 走到了右出口跟前(出口的触发范围是 12 像素)
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
    public void LeavingThroughTheRightGateEntersTheNextRoomAtTheClientsRandomPointInItsLeftGateAreaFacingRight()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart);
        DungeonSession s = d.Session;
        Assert.Equal(TestHeroId, s.HeroObjectId);
        Run(s, 1000, 0, 0, true);
        Run(s, 600, 0, 1);                                               // 往下走一点,让 y 与出生时不同
        double y = s.Hero.Y;
        Assert.True(y > HeroY + 20);
        RunUntil(s, () => s.CellX == 1, 1, 0);
        Assert.Equal((1, 0), (s.CellX, s.CellY));
        TownHero hero = s.Hero;
        // 格 (1,0) 的地图编号是 2,种子 = 1002 × 2;左出口矩形 [16, 136) × [67, 173),占位盒 40 × 10 → 客户端的随机数取到 (36, 129),站得下,不用挪
        Assert.Equal((36.0, 129.0), (hero.X, hero.Y));
        Assert.NotEqual(y, hero.Y, 1);                                   // y 不是沿用的
        Assert.False(hero.FacingLeft);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        Assert.Equal(0, s.Camera.IntX);                                  // 居中再夹住:36 − 400 < 0
    }

    [Fact]
    public void TheArrivalPointDependsOnlyOnTheHeroIdAndTheMapSoTheSameHeroAlwaysLandsOnTheSameSpot()
    {
        double[] Arrive(int heroId)
        {
            using var d = new Dungeon(RingLayout(), OneWeakAtStart, heroObjectId: heroId);
            DungeonSession s = d.Session;
            Run(s, 1000, 0, 0, true);
            RunUntil(s, () => s.CellX == 1, 1, 0);
            return new[] { s.Hero.X, s.Hero.Y };
        }

        Assert.Equal(Arrive(TestHeroId), Arrive(TestHeroId));
        Assert.Equal(new[] { 110.0, 158.0 }, Arrive(1000));              // 种子 = 1000 × 2
        Assert.Equal(new[] { 96.0, 80.0 }, Arrive(1001));
        Assert.NotEqual(Arrive(1000), Arrive(1001));
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
        // 格 (0,0) 的地图编号是 1,种子 = 1002 × 1;右出口矩形 [984, 1104) × [67, 173) → (1061, 104)
        Assert.Equal((1061.0, 104.0), (hero.X, hero.Y));
        Assert.True(hero.FacingLeft);
        Assert.Equal(320, s.Camera.IntX);                                // 1061 − 450 = 611,夹到最大 320
        Assert.Same(d.Rooms[(0, 0)], s.Current);
        Assert.Equal((0, 1), (s.Current.AliveCount, s.Current.Monsters.Count(m => m.IsGone)));   // 怪物没有复活
        Assert.False(hero.BattleStance);
        Assert.Equal(GateState.Open, GateOf(s, GateSide.Right).State);   // 出口一直开着
        Assert.Equal((2, 2), (s.RoomsEntered, s.RoomsCleared));          // 回来不重复计数
    }

    [Fact]
    public void TheDungeonSoundQueueCarriesEachRoomsMapSoundsAndReplaysThemWhenReturning()
    {
        // 起点格 (0,0) 有一只弱怪物;每个房间的地图声音是合成的:一首自己的音乐加一个共用的环境声
        using var d = new Dungeon(RingLayout(), OneWeakAtStart, soundsOf: (x, y) => new[] { "MUSIC_" + x + y, "AMB_SHARED" });
        DungeonSession s = d.Session;
        var cues = new List<SoundCue>();
        s.DrainSounds(cues);
        Assert.Equal(new[] { "RoomEnter:map/test/cell00.map", "RoomSound:MUSIC_00", "RoomSound:AMB_SHARED" }, cues.Select(c => c.ToString()).ToArray());

        Run(s, 1000, 0, 0, true);                                        // 打死怪物(挥剑的语音等也进队列;夹具没有给武器声音标签,所以只有语音)
        s.DrainSounds(cues);
        Assert.Contains(cues, c => c.Kind == SoundCueKind.Voice && c.Key == Dnf.Game.SwordmanVoice.Attack);
        cues.Clear();

        RunUntil(s, () => s.CellX == 1, 1, 0);
        s.DrainSounds(cues);
        // 走进格 (1,0):房间标记在前,它的地图声音紧跟着;走过去的这一路上没有别的房间声音
        int enter = cues.FindIndex(c => c.Kind == SoundCueKind.RoomEnter);
        Assert.True(enter >= 0);
        Assert.Equal(new[] { "RoomEnter:map/test/cell10.map", "RoomSound:MUSIC_10", "RoomSound:AMB_SHARED" },
            cues.Skip(enter).Take(3).Select(c => c.ToString()).ToArray());
        Assert.Equal(1, cues.Count(c => c.Kind == SoundCueKind.RoomEnter));
        cues.Clear();

        RunUntil(s, () => s.CellX == 0, -1, 0);                          // 走回去过的房间:地图声音再发一遍,怪物出场声不再发
        s.DrainSounds(cues);
        int back = cues.FindIndex(c => c.Kind == SoundCueKind.RoomEnter);
        Assert.Equal(new[] { "RoomEnter:map/test/cell00.map", "RoomSound:MUSIC_00", "RoomSound:AMB_SHARED" },
            cues.Skip(back).Take(3).Select(c => c.ToString()).ToArray());
        Assert.DoesNotContain(cues, c => c.Kind == SoundCueKind.Voice);
    }

    [Fact]
    public void TheHeroesLifeAndManaTravelWithTheHeroFromRoomToRoomAndBack()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart, vitalsOf: () => new HeroVitals(1, 100, 50, 0, 0, 0));
        DungeonSession s = d.Session;
        HeroVitals first = s.Vitals!;
        Assert.Equal((100, 50, false), (first.Hp, first.Mp, s.HeroDead));
        Run(s, 1000, 0, 0, true);                                        // 杀掉怪物,清房
        first.Damage(35);                                                // 在起点格受了伤
        RunUntil(s, () => s.CellX == 1, 1, 0);
        Assert.NotSame(first, s.Vitals);                                 // 新房间的会话有自己的一份……
        Assert.Equal((65, 50), (s.Vitals!.Hp, s.Vitals.Mp));             // ……当前值跟着人走,不是回满
        s.Vitals.Damage(20);
        RunUntil(s, () => s.CellX == 0, -1, 0);                          // 走回去:起点格那一份也同步到最新
        Assert.Same(first, s.Vitals);
        Assert.Equal((45, false), (first.Hp, s.HeroDead));
    }

    [Fact]
    public void TheHeroDeadFlagFollowsTheCurrentRoomsVitalsAndIsFalseWithoutVitals()
    {
        using var withVitals = new Dungeon(RingLayout(), OneWeakAtStart, vitalsOf: () => new HeroVitals(1, 10, 0, 0, 0, 0));
        withVitals.Session.Vitals!.Damage(10);
        Assert.True(withVitals.Session.HeroDead);

        using var without = new Dungeon(RingLayout(), OneWeakAtStart);
        Assert.Null(without.Session.Vitals);
        Assert.False(without.Session.HeroDead);
        Assert.Equal(0, without.Session.HeroHits);
    }

    [Fact]
    public void VerticalGatesPlaceTheHeroInsideTheOppositeGateAreaAndKeepTheFacing()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart);
        DungeonSession s = d.Session;
        Run(s, 1000, 0, 0, true);
        s.Hero.Place(560, 300, true, HeroMotion.Rest, 0);                // 站到下出口(560, 348)的正上方,朝左
        RunUntil(s, () => s.CellY == 1, 0, 1);
        Assert.Equal((0, 1), (s.CellX, s.CellY));
        // (0,1) D 的地图编号是 3,种子 = 1002 × 3;上出口 (560, 12) 的识别矩形 [490, 630) × [−8, 32) → (593, 17),朝向保持
        Assert.Equal((593.0, 17.0, true), (s.Hero.X, s.Hero.Y, s.Hero.FacingLeft));
        Assert.Equal(143, s.Camera.IntX);                                // 镜头先放在 0 再按死区跟随:593 − 450
        // 再回上去:进入 (0,0)(地图编号 1)的下出口 (560, 348) 的识别矩形 [490, 630) × [328, 368):取到的点 (543, 361) 在地图下沿之外 → 找最近的可站点 (543, 358)
        s.Build(0);
        RunUntil(s, () => s.CellY == 0, 0, -1);
        Assert.Equal((543.0, 358.0), (s.Hero.X, s.Hero.Y));
    }

    [Fact]
    public void TheUpGateTriggersOnlyByPushingUpInsideItsRecognitionRectangle()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart);
        DungeonSession s = d.Session;
        Run(s, 1000, 0, 0, true);
        s.Hero.Place(560, 300, false, HeroMotion.Rest, 0);
        RunUntil(s, () => s.CellY == 1, 0, 1);
        Run(s, 700, 0, 0);                                               // 等转场结束
        Assert.False(s.InTransition);
        // (0,1) 的上出口 (560, 12):识别矩形 [490, 630) × [−8, 32)
        s.Hero.Place(560, 25, false, HeroMotion.Rest, 0);                // 站在矩形里、没有往上顶:不触发
        Run(s, 300, 0, 0);
        Assert.False(s.InTransition);
        s.Hero.Place(200, 25, false, HeroMotion.Rest, 0);                // 离矩形横向太远,顶到边缘也不触发
        Run(s, 600, 0, -1);
        Assert.False(s.InTransition);
        Assert.Equal((0, 1), (s.CellX, s.CellY));
        s.Hero.Place(560, 25, false, HeroMotion.Rest, 0);                // 回到矩形里往上顶:触发
        Run(s, 600, 0, -1);
        Assert.True(s.InTransition);
        Run(s, 700, 0, 0);
        Assert.Equal((0, 0, 2), (s.CellX, s.CellY, s.Transitions));
    }

    [Fact]
    public void PushingIntoTheOpenGateBodyFadesOutSwapsTheRoomAtTheEndOfTheFadeOutAndFadesBackIn()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart, bodies: true);
        DungeonSession s = d.Session;
        Run(s, 1000, 0, 0, true);                                        // 杀掉怪物,清房
        Assert.True(s.IsCleared(0, 0));
        Assert.False(s.InTransition);
        Assert.Equal(0, s.FadeAlpha);
        Assert.DoesNotContain(s.Build(5000), x => x.AnimationPath == "fade");

        int guard = 0;
        while (!s.InTransition && guard++ < 2000)
        {
            s.Update(10, 1, 0, false);                                   // 往右走,顶在右出口的墙上
        }
        Assert.True(s.InTransition);
        Assert.Equal(BesideRightWallX, s.Hero.X, 3);                     // 墙的左沿 1072 − 占位盒半宽 20(实数坐标停在它之前一点点)
        Assert.Equal((0, 0, 0, 0), (s.CellX, s.CellY, s.Transitions, s.FadeAlpha));

        // 淡出 250 毫秒:房间还是原来的,主角照常被操作(一直顶着墙),黑幕线性变浓
        for (int k = 1; k <= 12; k++)
        {
            s.Update(10, 1, 0, false);
        }
        Assert.Equal((0, 0, 0), (s.CellX, s.CellY, s.Transitions));
        Assert.Equal(122, s.FadeAlpha);                                  // 255 × 120 / 250 = 122.4
        Assert.Equal(BesideRightWallX, s.Hero.X, 3);
        List<SceneSprite> frame = s.Build(5000);
        SceneSprite fade = Assert.Single(frame, x => x.AnimationPath == "fade");
        Assert.Equal((122, 800, 600, 0, 0), (fade.TintAlpha, fade.Frame.Width, fade.Frame.Height, fade.ScreenX, fade.ScreenY));
        Assert.Equal(OverlayArt.UiRank, fade.LayerRank);
        Assert.True(fade.SortKey > frame.Where(x => x.LayerRank == OverlayArt.UiRank && x.AnimationPath != "fade").Max(x => x.SortKey));   // 盖在小地图上面
        for (int k = 13; k <= 24; k++)
        {
            s.Update(10, 1, 0, false);
        }
        Assert.Equal((0, 0, 0), (s.CellX, s.CellY, s.Transitions));
        Assert.Equal(245, s.FadeAlpha);                                  // 255 × 240 / 250 = 244.8

        // 第 25 步 = 250 毫秒:这一步的末尾换房间;主角落在对侧(左)出口的识别矩形里、朝右、站姿待机,镜头在 0。
        // 客户端的随机数取到的点是 (36, 129),在左边的墙里(墙盖住 x < 80,占位盒的左沿 x − 20 要 ≥ 80)→ 由近到远找最近的可站点:半径 66 处的 (102, 129)
        s.Update(10, 1, 0, false);
        Assert.Equal((1, 0, 1), (s.CellX, s.CellY, s.Transitions));
        Assert.True(s.InTransition);
        Assert.Equal(255, s.FadeAlpha);
        TownHero hero = s.Hero;
        Assert.Equal((102.0, 129.0, false, HeroMotion.Rest), (hero.X, hero.Y, hero.FacingLeft, hero.Motion));
        Assert.Equal(0, s.Camera.IntX);

        // 全黑 130 毫秒,再淡入 250 毫秒
        for (int k = 1; k <= 12; k++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.Equal(255, s.FadeAlpha);
        s.Update(10, 0, 0, false);                                       // 全黑满 130 毫秒 → 淡入开始
        Assert.Equal(255, s.FadeAlpha);
        for (int k = 1; k <= 12; k++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.Equal(133, s.FadeAlpha);                                  // 255 × (1 − 120 / 250) = 132.6
        for (int k = 13; k <= 24; k++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.Equal(10, s.FadeAlpha);                                   // 255 × (1 − 240 / 250) = 10.2
        Assert.True(s.InTransition);
        s.Update(10, 0, 0, false);
        Assert.False(s.InTransition);
        Assert.Equal(0, s.FadeAlpha);
        Assert.DoesNotContain(s.Build(5000), x => x.AnimationPath == "fade");
        Assert.Equal((1, 0, 1), (s.CellX, s.CellY, s.Transitions));
    }

    [Fact]
    public void ALongFrameCrossesTheWholeTransitionAtTheSameMomentsAsSmallSteps()
    {
        using var a = new Dungeon(RingLayout(), OneWeakAtStart, bodies: true);
        using var b = new Dungeon(RingLayout(), OneWeakAtStart, bodies: true);
        Run(a.Session, 1000, 0, 0, true);
        Run(b.Session, 1000, 0, 0, true);
        int guard = 0;
        while (!a.Session.InTransition && guard++ < 2000)
        {
            a.Session.Update(10, 1, 0, false);
            b.Session.Update(10, 1, 0, false);
        }
        Assert.True(b.Session.InTransition);
        a.Session.Update(630, 0, 0, false);                              // 一帧就走完 淡出 + 全黑 + 淡入(两边都按 10 毫秒一小步推进)
        for (int k = 0; k < 63; k++)
        {
            b.Session.Update(10, 0, 0, false);
        }
        Assert.False(a.Session.InTransition);
        Assert.False(b.Session.InTransition);
        Assert.Equal((a.Session.CellX, a.Session.CellY, a.Session.Transitions), (b.Session.CellX, b.Session.CellY, b.Session.Transitions));
        Assert.Equal(a.Session.Hero.X, b.Session.Hero.X, 6);
    }

    [Fact]
    public void AGateBodyBlocksTheHeroEvenWhileTheGateIsShutAndNothingTriggersBeforeTheRoomIsCleared()
    {
        using var d = new Dungeon(RingLayout(), (cx, cy) => new[] { (900, 300) }, hp: 100000, bodies: true);
        DungeonSession s = d.Session;
        Run(s, 6000, 1, 0);                                              // 右出口关着:挡住,不触发
        Assert.Equal(BesideRightWallX, s.Hero.X, 3);
        Assert.False(s.InTransition);
        Assert.Equal((0, 0, 0), (s.CellX, s.CellY, s.Transitions));
        Run(s, 6000, -1, 0);                                             // 左边是墙:同样
        Assert.Equal(BesideLeftWallX, s.Hero.X, 3);
        Assert.False(s.InTransition);
        Assert.Equal((0, 0, 0), (s.CellX, s.CellY, s.Transitions));
        Assert.False(s.IsCleared(0, 0));
    }

    [Fact]
    public void ThePushIntoAWallNeverTriggersEvenAfterTheRoomIsCleared()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart, bodies: true);
        DungeonSession s = d.Session;
        Run(s, 1000, 0, 0, true);
        Assert.True(s.IsCleared(0, 0));
        Run(s, 6000, -1, 0);                                             // 起点格的左边是墙(不可开)
        Assert.Equal(BesideLeftWallX, s.Hero.X, 3);
        Assert.False(s.InTransition);
        Assert.Equal((0, 0, 0), (s.CellX, s.CellY, s.Transitions));
    }

    [Fact]
    public void GoingBackThroughABodiedGateArrivesInsideTheOppositeGateAreaClearOfTheBodyFacingLeftWithTheCameraRule()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart, bodies: true);
        DungeonSession s = d.Session;
        Run(s, 1000, 0, 0, true);
        Run(s, 600, 0, 1);                                               // 往下走一点,让 y 与出生时不同
        RunUntil(s, () => s.CellX == 1, 1, 0);
        Assert.Equal((102.0, 129.0, false), (s.Hero.X, s.Hero.Y, s.Hero.FacingLeft));   // 与上一个测试同样:随机点 (36, 129) 在墙里 → 挪到 (102, 129)
        Run(s, 700, 0, 0);                                               // 等转场结束
        Assert.False(s.InTransition);
        RunUntil(s, () => s.CellX == 0, -1, 0);                          // 顶着左边的墙(这一格的左出口是普通出口,开着)回去
        Assert.Equal((0, 0, 2), (s.CellX, s.CellY, s.Transitions));
        TownHero hero = s.Hero;
        // 右出口矩形 [984, 1104) × [67, 173) 里取到的点是 (1061, 104),在右边的墙里(盒的右沿 x + 20 要 ≤ 1071)→ 半径 12 处的 (1049, 104)
        Assert.Equal((1049.0, 104.0, true, HeroMotion.Rest), (hero.X, hero.Y, hero.FacingLeft, hero.Motion));
        Assert.Equal(320, s.Camera.IntX);                                // 镜头在 0,跟随 1049 夹到最大 320
    }

    [Fact]
    public void TheUpAndDownGatesTriggerOnlyByPushingAgainstTheBandEdgeNearTheGate()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart, bodies: true);
        DungeonSession s = d.Session;
        Run(s, 1000, 0, 0, true);
        Assert.True(s.IsCleared(0, 0));
        // 站在下出口(560, 348)的正上方,没有往下顶:不触发
        s.Hero.Place(560, 345, false, HeroMotion.Rest, 0);
        Run(s, 500, 0, 0);
        Assert.False(s.InTransition);
        Assert.Equal((0, 0, 0), (s.CellX, s.CellY, s.Transitions));
        // 离出口横向太远,往下顶也不触发
        s.Hero.Place(200, 345, false, HeroMotion.Rest, 0);
        Run(s, 1000, 0, 1);
        Assert.False(s.InTransition);
        Assert.Equal((0, 0), (s.CellX, s.CellY));
        // 回到出口跟前往下顶:触发
        s.Hero.Place(560, 345, false, HeroMotion.Rest, 0);
        Run(s, 200, 0, 1);
        Assert.True(s.InTransition);
        Run(s, 700, 0, 0);
        Assert.Equal((0, 1, 1), (s.CellX, s.CellY, s.Transitions));
    }

    [Fact]
    public void ClearingTheBossRoomSetsBossClearedFinishesTheDungeonAndShowsTheBanner()
    {
        // 路线:起点格杀怪 → 下到 (0,1)(从上出口进来)→ 右边是首领出口 → 进首领格 (1,1),从它的左出口进来:地图编号 4、种子 = 1002 × 4,
        // 左出口矩形里取到的点是 (70, 113),怪物就摆在主角前面 40 像素
        using var d = new Dungeon(RingLayout(), (cx, cy) => cx == 0 && cy == 0 ? new[] { (340, 100) } : (cx == 1 && cy == 1 ? new[] { (110, 113) } : None()));
        DungeonSession s = d.Session;
        Run(s, 1000, 0, 0, true);
        s.Hero.Place(560, 300, false, HeroMotion.Rest, 0);
        RunUntil(s, () => s.CellY == 1, 0, 1);
        Assert.Equal((0, 1), (s.CellX, s.CellY));
        Assert.Equal(GateKind.Boss, d.Layout.GateKindAt(0, 1, GateSide.Right));
        RunUntil(s, () => s.CellX == 1, 1, 0);
        Assert.Equal((1, 1), (s.CellX, s.CellY));
        Assert.Equal((70.0, 113.0), (s.Hero.X, s.Hero.Y));
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
    public void TheBotPlaysTheSameGameAtAnyFrameLengthBecauseItAdvancesInFixedBeats()
    {
        // 自动机的走位与出手间隔按"心跳"数写:实时运行里帧长不固定(甚至一帧 100 毫秒),Advance 必须把它们折成固定的 1/60 秒心跳,
        // 打出的是同一局(证据 E-072:按帧长直接喂它,帧长到 60 毫秒时会在怪物旁来回晃)
        (int X, int Y)[] Monsters(int cx, int cy)
        {
            return cx == 0 && cy == 0 ? new[] { (340, 100), (420, 120) }
                : cx == 1 && cy == 0 ? new[] { (300, 200) }
                : cx == 1 && cy == 1 ? new[] { (400, 150), (500, 180) }
                : None();
        }

        (double Clock, int Hits, int Kills, int Transitions, int Entered, double HeroX, double HeroY, int Beats) Play(Func<double> frameMs)
        {
            using var d = new Dungeon(RingLayout(), Monsters, hp: 40);
            DungeonSession s = d.Session;
            var bot = new DungeonBot(true);
            for (int frame = 0; frame < 400000 && !s.Finished; frame++)
            {
                bot.Advance(s, frameMs());
            }
            Assert.True(s.BossCleared, "没有打通");
            return (s.ClockMs, s.Hits, s.Kills, s.Transitions, s.RoomsEntered, s.Hero.X, s.Hero.Y, bot.Steps);
        }

        var reference = Play(() => CombatScript.StepMs);
        Assert.True(reference.Beats > 600);
        Assert.Equal(reference, Play(() => 5));
        Assert.Equal(reference, Play(() => 33.3));
        Assert.Equal(reference, Play(() => 100));
        var random = new Random(7);
        Assert.Equal(reference, Play(() => 1 + random.NextDouble() * 99));

        // 一帧的 deltaMs 记进欠账,欠账超过 MaxDebtMs 的部分丢掉;一帧最多补 MaxBeatsPerFrame 个心跳
        using var d2 = new Dungeon(RingLayout(), Monsters, hp: 40);
        var bot2 = new DungeonBot(false);
        Assert.Equal(0, bot2.Advance(d2.Session, CombatScript.StepMs * 0.99));          // 不足一个心跳
        Assert.Equal(1, bot2.Advance(d2.Session, CombatScript.StepMs * 0.02));          // 凑够了
        int burst = bot2.Advance(d2.Session, 5000);                                      // 卡了 5 秒:只补 200 毫秒(约 12 个心跳,浮点舍入可能少一个)
        Assert.InRange(burst, DungeonBot.MaxBeatsPerFrame - 1, DungeonBot.MaxBeatsPerFrame);
        Assert.Equal(0, bot2.Advance(d2.Session, 0));                                    // 其余的丢掉了,下一帧不会接着补
        Assert.Equal(0, bot2.Advance(d2.Session, -50));                                  // 负的时间当 0
        Assert.Throws<ArgumentNullException>(() => bot2.Advance(null!, 16));
    }

    [Fact]
    public void TheBotStopsWhenTheDungeonIsDoneUnlessAskedToKeepTheClockRunningSoTheSettlementCanAppear()
    {
        (int X, int Y)[] Monsters(int cx, int cy)
        {
            return cx == 0 && cy == 0 ? new[] { (340, 100), (420, 120) }
                : cx == 1 && cy == 0 ? new[] { (300, 200) }
                : cx == 1 && cy == 1 ? new[] { (400, 150), (500, 180) }
                : None();
        }

        // 默认:副本走完就停(命令行 dungeon-check 与播放器离屏渲染对拍的老规则)
        using var a = new Dungeon(RingLayout(), Monsters, hp: 40);
        double stopped = new DungeonBot(true).RunUntil(a.Session, 600_000);
        Assert.True(a.Session.BossCleared);
        Assert.False(a.Session.SettlementActive);

        // 带"清空之后再走多久":主角站着不动,时间照走,结算随之开始(清空后 3 秒)
        using var b = new Dungeon(RingLayout(), Monsters, hp: 40);
        double end = new DungeonBot(true).RunUntil(b.Session, 600_000, 4000);
        Assert.True(b.Session.SettlementActive);
        Assert.InRange(end - stopped, 4000 - 1, 4000 + CombatScript.StepMs);   // 从走完那一步起算 4 秒,按 1/60 秒的步长凑整
        Assert.InRange(b.Session.ClockMs - b.Session.BossClearedAtMs, 4000 - CombatScript.StepMs, 4000 + CombatScript.StepMs);
        Assert.Equal(a.Session.BossClearedAtMs, b.Session.BossClearedAtMs, 6);   // 打到清空为止与默认那一局完全一样
        Assert.Equal((a.Session.Hits, a.Session.Kills, a.Session.Hero.X, a.Session.Hero.Y), (b.Session.Hits, b.Session.Kills, b.Session.Hero.X, b.Session.Hero.Y));

        // 总时限先到就停在总时限(此时结算还没开始)
        using var c = new Dungeon(RingLayout(), Monsters, hp: 40);
        double early = new DungeonBot(true).RunUntil(c.Session, stopped + 1000, 4000);
        Assert.InRange(early, stopped + 1000, stopped + 1000 + CombatScript.StepMs);
        Assert.True(c.Session.BossCleared);
        Assert.False(c.Session.SettlementActive);

        // 负数与 0 的"清空之后"都当没有
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40);
        Assert.Equal(stopped, new DungeonBot(true).RunUntil(d.Session, 600_000, -5), 3);
    }

    [Fact]
    public void TheSettlementStartsThreeSecondsAfterTheBossIsClearedAndSwapsTheMiniMapForTheDimmedSceneTheCampAndTheMenu()
    {
        using var d = new Dungeon(TwoRoomBossLayout(), (cx, cy) => None());
        DungeonSession s = d.Session;
        Assert.False(s.BossCleared);
        Assert.Equal(-1.0, s.BossClearedAtMs);
        RunUntil(s, () => s.BossCleared, 1, 0);                          // 向右走进首领格:房间里没有怪物,进来就清空
        Assert.Equal((1, 0), (s.CellX, s.CellY));
        Assert.Equal(s.ClockMs, s.BossClearedAtMs, 6);
        Assert.False(s.SettlementActive);
        Assert.Equal((0, 0.0), (s.SettlementDim, s.SettlementAgeMs));
        Assert.Null(s.Camp);
        List<SceneSprite> before = s.Build(0);
        Assert.Contains(before, x => x.AnimationPath == "minimap");
        Assert.Contains(before, x => x.AnimationPath == "clear");        // 结算之前先是占位的 CLEAR 字样
        Assert.DoesNotContain(before, x => x.AnimationPath.StartsWith("settle"));

        Run(s, 2990);
        Assert.False(s.SettlementActive);
        Assert.Null(s.Camp);
        Run(s, 10);                                                       // 3000 毫秒整
        Assert.True(s.SettlementActive);
        Assert.Equal(0.0, s.SettlementAgeMs, 6);
        Assert.Equal(0, s.SettlementDim);                                 // 刚开始还没变暗
        Assert.NotNull(s.Camp);
        Assert.Equal((s.Hero.X, s.Hero.Y), (s.Camp.X, s.Camp.Y));         // 营火生在主角脚下
        Assert.Equal(3, s.Camp.FireLayers);                               // 夹具里有营火动作脚本;房间地图不在 PVF 里,没有 NPC
        Assert.Equal(0, s.Camp.NpcCount);

        Run(s, 250);
        Assert.Equal(250.0, s.SettlementAgeMs, 6);
        Assert.Equal(DungeonSession.SettlementDimAlpha / 2, s.SettlementDim);   // 淡入 500 毫秒线性升到每层 56:一半
        Run(s, 250);
        Assert.Equal(DungeonSession.SettlementDimAlpha, s.SettlementDim);
        Run(s, 2000);
        Assert.Equal(DungeonSession.SettlementDimAlpha, s.SettlementDim);       // 之后保持
        Assert.Equal(2500.0, s.Camp.AgeMs, 6);                            // 营地从结算开始起播放

        List<SceneSprite> after = s.Build(9000);
        Assert.DoesNotContain(after, x => x.AnimationPath == "minimap" || x.AnimationPath == "minimap-count" || x.AnimationPath == "clear");
        // 两层相同的黑幕:一层在地图图层之上、物件之下,一层在物件之上、界面之下;每层 alpha 56,叠起来 ×0.609(基线上量到地图图层 ×0.61)
        SceneSprite dim = after.Single(x => x.AnimationPath == "settle-dim");
        SceneSprite back = after.Single(x => x.AnimationPath == "settle-dim-back");
        Assert.Equal((OverlayArt.DimRank, 56, 800, 600, 0, 0), (dim.LayerRank, dim.TintAlpha, dim.Frame.Width, dim.Frame.Height, dim.ScreenX, dim.ScreenY));
        Assert.Equal((OverlayArt.BackDimRank, 56, 800, 600, 0, 0), (back.LayerRank, back.TintAlpha, back.Frame.Width, back.Frame.Height, back.ScreenX, back.ScreenY));
        double both = (1 - 56 / 255.0) * (1 - 56 / 255.0);
        Assert.InRange(both, 0.605, 0.612);
        int dimAt = after.IndexOf(dim);
        int backAt = after.IndexOf(back);
        Assert.True(backAt < dimAt);
        // 第一层黑幕盖在全部地图图层(大类 0 到 3)之后、物件(主角)之前;第二层黑幕盖在物件之后
        Assert.All(after.Where(x => x.LayerRank <= OverlayArt.BackDimRank && x != back), x => Assert.True(after.IndexOf(x) < backAt));
        List<SceneSprite> objects = after.Where(x => x.LayerRank == TownScene.NormalRank).ToList();
        Assert.NotEmpty(objects);                                          // 至少有主角
        Assert.All(objects, x => Assert.True(after.IndexOf(x) > backAt && after.IndexOf(x) < dimAt));
        // 营火三层不被压暗:大类与第二层黑幕相同、在它之后;菜单(夹具里没有 eplpnew.img,退回占位面板)在界面大类
        var fire = after.Where(x => x.AnimationPath == AvatarFixture.CampBaseAni || x.AnimationPath == AvatarFixture.CampGlowAni).ToList();
        Assert.Equal(3, fire.Count);
        Assert.All(fire, x => Assert.Equal(OverlayArt.DimRank, x.LayerRank));
        Assert.All(fire, x => Assert.True(after.IndexOf(x) > dimAt));
        SceneSprite panel = after.Single(x => x.AnimationPath == "settle-fallback");
        Assert.True(after.IndexOf(panel) > after.IndexOf(fire[^1]));
        Assert.Equal(OverlayArt.UiRank, panel.LayerRank);
    }

    [Fact]
    public void TheSettlementStartPlaysTheResultSoundExactlyOnceAsAUiSoundWithoutTouchingTheRoomSounds()
    {
        using var d = new Dungeon(TwoRoomBossLayout(), (cx, cy) => None());
        DungeonSession s = d.Session;
        var cues = new List<SoundCue>();
        RunUntil(s, () => s.BossCleared, 1, 0);
        s.DrainSounds(cues);                                              // 进房间时的提示先取走
        cues.Clear();

        Run(s, 2990);
        s.DrainSounds(cues);
        Assert.DoesNotContain(cues, c => c.Key == SceneMusic.ResultSound);   // 结算还没开始

        Run(s, 10);                                                       // 清空后整 3 秒:结算开始
        Assert.True(s.SettlementActive);
        s.DrainSounds(cues);
        SoundCue result = Assert.Single(cues, c => c.Key == SceneMusic.ResultSound);
        Assert.Equal(SoundCueKind.Ui, result.Kind);                       // 界面音:不是背景音乐
        Assert.DoesNotContain(cues, c => c.Kind == SoundCueKind.RoomEnter || c.Kind == SoundCueKind.RoomSound);   // 房间的音乐与环境声照旧

        cues.Clear();
        Run(s, 3000);                                                     // 之后不再重复
        s.DrainSounds(cues);
        Assert.DoesNotContain(cues, c => c.Key == SceneMusic.ResultSound);
    }

    [Fact]
    public void TheMenuChoiceIsOnlyAcceptedOnceAfterTheSettlementStartsAndOnlyForEnabledRows()
    {
        using var d = new Dungeon(TwoRoomBossLayout(), (cx, cy) => None());
        DungeonSession s = d.Session;
        RunUntil(s, () => s.BossCleared, 1, 0);
        // 结算开始之前什么都选不了,鼠标也没有悬停
        Assert.False(s.Choose(SettlementChoice.Retry));
        Assert.Equal(SettlementChoice.None, s.Click(600, 80));
        s.SetPointer(600, 80);
        Assert.Equal(-1, s.HoverRow);

        Run(s, 3000);
        Assert.True(s.SettlementActive);
        s.SetPointer(600, 80);
        Assert.Equal(0, s.HoverRow);
        s.SetPointer(600, 110);
        Assert.Equal(1, s.HoverRow);
        s.SetPointer(10, 10);
        Assert.Equal(-1, s.HoverRow);
        s.SetPointer(600, 190);
        Assert.Equal(3, s.HoverRow);
        s.ClearPointer();
        Assert.Equal(-1, s.HoverRow);

        // 快速组队没有:选不了、点不了;点在菜单外也没反应
        Assert.False(s.Choose(SettlementChoice.QuickParty));
        Assert.False(s.Choose(SettlementChoice.None));
        Assert.Equal(SettlementChoice.None, s.Click(600, 110));
        Assert.Equal(SettlementChoice.None, s.Click(10, 10));
        Assert.Equal(SettlementChoice.None, s.Choice);

        s.SetPointer(600, 190);
        Assert.Equal(SettlementChoice.ReturnToTown, s.Click(600, 190));
        Assert.Equal(SettlementChoice.ReturnToTown, s.Choice);
        Assert.Equal(-1, s.HoverRow);                                     // 选定之后不再悬停
        // 只接受第一次
        Assert.False(s.Choose(SettlementChoice.Retry));
        Assert.Equal(SettlementChoice.None, s.Click(600, 80));
        Assert.Equal(SettlementChoice.ReturnToTown, s.Choice);
    }

    [Fact]
    public void EachEnabledChoiceCanBeMadeByItsHotkeyPathAndALongFrameStartsTheSettlementLikeManyShortOnes()
    {
        foreach (SettlementChoice choice in new[] { SettlementChoice.Retry, SettlementChoice.SelectDungeon, SettlementChoice.ReturnToTown })
        {
            using var d = new Dungeon(TwoRoomBossLayout(), (cx, cy) => None());
            DungeonSession s = d.Session;
            RunUntil(s, () => s.BossCleared, 1, 0);
            s.Update(5000, 0, 0);                                         // 一帧 5 秒:切成小步,结算照样在 3 秒处开始
            Assert.True(s.SettlementActive);
            Assert.Equal(DungeonSession.SettlementDimAlpha, s.SettlementDim);
            Assert.NotNull(s.Camp);
            Assert.True(s.Choose(choice));
            Assert.Equal(choice, s.Choice);
        }
    }

    [Fact]
    public void TheSettlementNeverStartsBeforeTheBossRoomIsCleared()
    {
        using var d = new Dungeon(RingLayout(), OneWeakAtStart);
        DungeonSession s = d.Session;
        Run(s, 6000);                                                     // 起点格有一只没打的怪物,首领格还远着
        Assert.False(s.BossCleared);
        Assert.Equal(-1.0, s.BossClearedAtMs);
        Assert.False(s.SettlementActive);
        Assert.Null(s.Camp);
        Assert.False(s.Choose(SettlementChoice.Retry));
        Assert.Contains(s.Build(0), x => x.AnimationPath == "minimap");
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
