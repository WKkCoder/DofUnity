using Dnf.Render;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网多房间里宿主为某一间房挑房间地图(<see cref="OnlineDungeonRoom.PickEntryMap"/>)、为迷宫某一格某一侧定出口物件(<see cref="OnlineDungeonRoom.GateKindAt"/>)的纯逻辑:
/// 合成的候选表头、路径与编号(9xx),不读任何脚本。
/// 服务端起始地图通知里的地图编号"是 map/map.lst 的编号"只是推断,所以编号对不上、或那张图没有怪时必须能退而求其次(先找登记在这一格出口码下的普通房间,再找所有普通房间),
/// 而不是让宿主退回城镇;读脚本有成本,测试盯着读了几张。
/// </summary>
public class OnlineRoomEntryPickTests
{
    private const int R = ExitBits.Right;
    private const int U = ExitBits.Up;
    private const int L = ExitBits.Left;
    private const int D = ExitBits.Down;

    private static RoomCandidate Room(string path, int id, int exits) => new RoomCandidate(path, id, exits, 15, false, false);

    private static RoomCandidate Boss(string path, int id, int exits) => new RoomCandidate(path, id, exits, 15, true, false);

    private static RoomCandidate Dummy(string path, int id, int exits) => new RoomCandidate(path, id, RoomType.Dummy, new[] { new GreedPair(exits, 15) }, false, null);

    /// <summary>按路径查"有几项 [monster]"的假读取,同时记下被读过的路径;没登记的路径读不出来(-1)。</summary>
    private sealed class Counts
    {
        private readonly Dictionary<string, int> _table;

        public Counts(params (string Path, int Monsters)[] entries)
        {
            _table = entries.ToDictionary(e => e.Path, e => e.Monsters);
        }

        public List<string> Reads { get; } = new List<string>();

        public int Count(string path)
        {
            Reads.Add(path);
            return _table.TryGetValue(path, out int n) ? n : -1;
        }
    }

    // ================================================================== PickEntryMap

    [Fact]
    public void The_room_with_the_notice_number_is_taken_unread_when_the_room_needs_no_monsters()
    {
        RoomCandidate[] rooms = { Room("map/t/a.map", 900, R), Room("map/t/b.map", 901, L | D), Room("map/t/c.map", 902, U) };
        var counts = new Counts();

        string picked = OnlineDungeonRoom.PickEntryMap(rooms, 901, L | D, false, counts.Count, out bool exact);

        Assert.Equal("map/t/b.map", picked);
        Assert.True(exact);
        Assert.Empty(counts.Reads);                                                  // 房间不要怪就不读脚本
    }

    [Fact]
    public void The_room_with_the_notice_number_is_taken_when_it_has_monsters_and_the_room_needs_some()
    {
        RoomCandidate[] rooms = { Room("map/t/a.map", 900, R), Room("map/t/b.map", 901, L | D), Room("map/t/c.map", 902, U) };
        var counts = new Counts(("map/t/a.map", 5), ("map/t/b.map", 3), ("map/t/c.map", 1));

        string picked = OnlineDungeonRoom.PickEntryMap(rooms, 901, L | D, true, counts.Count, out bool exact);

        Assert.Equal("map/t/b.map", picked);
        Assert.True(exact);
        Assert.Equal(new[] { "map/t/b.map" }, counts.Reads);                         // 对上了就不再读别的图
    }

    [Fact]
    public void The_exact_match_wins_over_an_earlier_room_registered_under_the_exit_code()
    {
        RoomCandidate[] rooms = { Room("map/t/a.map", 900, R), Room("map/t/b.map", 901, U) };
        var counts = new Counts(("map/t/a.map", 5), ("map/t/b.map", 2));

        string picked = OnlineDungeonRoom.PickEntryMap(rooms, 901, R, true, counts.Count, out bool exact);

        Assert.Equal("map/t/b.map", picked);
        Assert.True(exact);
    }

    [Fact]
    public void A_boss_room_is_an_exact_match_for_the_last_rooms_number_but_is_never_a_fallback()
    {
        RoomCandidate[] rooms = { Boss("map/t/boss.map", 910, U), Room("map/t/normal.map", 912, U) };
        var counts = new Counts(("map/t/boss.map", 1), ("map/t/normal.map", 2));

        Assert.Equal("map/t/boss.map", OnlineDungeonRoom.PickEntryMap(rooms, 910, U, true, counts.Count, out bool bossExact));   // 服务端说的就是首领房间:按编号用它
        Assert.True(bossExact);

        counts.Reads.Clear();
        Assert.Equal("map/t/normal.map", OnlineDungeonRoom.PickEntryMap(rooms, 999, U, true, counts.Count, out bool fallbackExact));   // 编号对不上:首领房间连读都不读
        Assert.False(fallbackExact);
        Assert.DoesNotContain("map/t/boss.map", counts.Reads);
    }

    [Fact]
    public void A_numbered_room_without_monsters_falls_back_to_the_first_room_of_the_exit_code_that_has_some_and_is_not_read_twice()
    {
        RoomCandidate[] rooms =
        {
            Room("map/t/a.map", 900, R),           // 不在这一格的出口码下:第一轮不看
            Room("map/t/b.map", 901, L | D),       // 编号对上、但没有怪
            Room("map/t/c.map", 902, L | D),
            Room("map/t/d.map", 903, L | D),
        };
        var counts = new Counts(("map/t/a.map", 7), ("map/t/b.map", 0), ("map/t/c.map", 4), ("map/t/d.map", 5));

        string picked = OnlineDungeonRoom.PickEntryMap(rooms, 901, L | D, true, counts.Count, out bool exact);

        Assert.Equal("map/t/c.map", picked);
        Assert.False(exact);
        Assert.Equal(new[] { "map/t/b.map", "map/t/c.map" }, counts.Reads);          // 按编号读了 b;退而求其次时跳过读过的 b,在出口码下的房间里 c 就有怪
    }

    [Fact]
    public void The_fallback_looks_at_the_rooms_of_the_exit_code_first_and_then_at_all_the_other_normal_rooms()
    {
        RoomCandidate[] rooms = { Room("map/t/a.map", 900, R), Room("map/t/b.map", 901, L | D), Room("map/t/c.map", 902, L | D) };
        var counts = new Counts(("map/t/a.map", 5), ("map/t/b.map", 0), ("map/t/c.map", 0));

        string picked = OnlineDungeonRoom.PickEntryMap(rooms, -1, L | D, true, counts.Count, out bool exact);

        Assert.Equal("map/t/a.map", picked);
        Assert.False(exact);
        Assert.Equal(new[] { "map/t/b.map", "map/t/c.map", "map/t/a.map" }, counts.Reads);   // 出口码下的两张都没有怪,第二轮才轮到 a
    }

    [Fact]
    public void Without_a_need_for_monsters_the_first_room_of_the_exit_code_is_taken_unread()
    {
        RoomCandidate[] rooms = { Room("map/t/a.map", 900, U), Room("map/t/b.map", 901, R), Room("map/t/c.map", 902, R) };
        var counts = new Counts();

        string picked = OnlineDungeonRoom.PickEntryMap(rooms, 999, R, false, counts.Count, out bool exact);

        Assert.Equal("map/t/b.map", picked);
        Assert.False(exact);
        Assert.Empty(counts.Reads);
    }

    [Fact]
    public void Without_a_need_for_monsters_and_no_room_under_the_exit_code_the_first_normal_room_is_taken()
    {
        RoomCandidate[] rooms = { Boss("map/t/boss.map", 900, U), Room("map/t/a.map", 901, U), Room("map/t/b.map", 902, L) };

        string picked = OnlineDungeonRoom.PickEntryMap(rooms, -1, R, false, new Counts().Count, out bool exact);

        Assert.Equal("map/t/a.map", picked);                                         // 没有登记在右出口码下的:退到第一张普通房间(首领房间不算)
        Assert.False(exact);
    }

    [Fact]
    public void The_fallback_skips_boss_and_dummy_rooms_without_reading_them()
    {
        RoomCandidate[] rooms = { Boss("map/t/boss.map", 910, R), Dummy("map/t/dummy.map", 911, R), Room("map/t/normal.map", 912, R) };
        var counts = new Counts(("map/t/boss.map", 1), ("map/t/dummy.map", 5), ("map/t/normal.map", 2));

        Assert.Equal("map/t/normal.map", OnlineDungeonRoom.PickEntryMap(rooms, -1, R, true, counts.Count, out bool exact));
        Assert.False(exact);
        Assert.Equal(new[] { "map/t/normal.map" }, counts.Reads);
    }

    [Fact]
    public void A_negative_number_is_no_number_even_when_a_candidate_carries_it()
    {
        RoomCandidate[] rooms = { Room("map/t/a.map", -1, R) };
        var counts = new Counts(("map/t/a.map", 2));

        string picked = OnlineDungeonRoom.PickEntryMap(rooms, -1, R, true, counts.Count, out bool exact);

        Assert.Equal("map/t/a.map", picked);
        Assert.False(exact);                                                         // 是退而求其次挑到的,不能算"编号对上"
    }

    [Fact]
    public void Each_fallback_pass_reads_at_most_the_scan_limit_and_gives_up_without_a_guess()
    {
        var rooms = new List<RoomCandidate>();
        var entries = new List<(string, int)>();
        for (int i = 0; i < OnlineDungeonRoom.MaxMapScan + 3; i++)
        {
            string path = "map/t/r" + i + ".map";
            rooms.Add(Room(path, 900 + i, R));                                       // 出口码下的:第一轮
            entries.Add((path, 0));
        }
        for (int i = 0; i < OnlineDungeonRoom.MaxMapScan + 3; i++)
        {
            string path = "map/t/u" + i + ".map";
            rooms.Add(Room(path, 1000 + i, U));                                      // 别的出口码下的:第二轮
            entries.Add((path, 0));
        }
        string late = "map/t/late.map";
        rooms.Add(Room(late, 1100, U));                                              // 唯一有怪的房间排在两轮的上限之后
        entries.Add((late, 3));
        var counts = new Counts(entries.ToArray());

        string picked = OnlineDungeonRoom.PickEntryMap(rooms, -1, R, true, counts.Count, out bool exact);

        Assert.Null(picked);
        Assert.False(exact);
        Assert.Equal(2 * OnlineDungeonRoom.MaxMapScan, counts.Reads.Count);
        Assert.Equal(counts.Reads.Count, counts.Reads.Distinct().Count());           // 同一张图不读第二遍
        Assert.DoesNotContain(late, counts.Reads);
    }

    [Fact]
    public void Nothing_to_pick_gives_null_for_empty_pools_unreadable_maps_and_null_entries()
    {
        var none = new Counts();

        Assert.Null(OnlineDungeonRoom.PickEntryMap(null, 900, R, true, none.Count, out bool nullExact));
        Assert.False(nullExact);
        Assert.Null(OnlineDungeonRoom.PickEntryMap(new RoomCandidate[0], 900, R, true, none.Count, out _));
        Assert.Empty(none.Reads);

        RoomCandidate[] unreadable = { null!, Room("map/t/gone.map", 900, R) };      // 候选里夹着 null、地图读不出来(-1,当作没有怪)
        Assert.Null(OnlineDungeonRoom.PickEntryMap(unreadable, 900, R, true, none.Count, out bool unreadableExact));
        Assert.False(unreadableExact);
    }

    [Fact]
    public void A_missing_reader_is_an_error_whatever_the_rest_is()
    {
        Assert.Throws<ArgumentNullException>(() => OnlineDungeonRoom.PickEntryMap(new[] { Room("map/t/a.map", 900, R) }, 900, R, true, null!, out _));
        Assert.Throws<ArgumentNullException>(() => OnlineDungeonRoom.PickEntryMap(null, 900, R, false, null!, out _));
    }

    // ================================================================== GateKindAt

    private static DungeonMaze Maze(int cols, int rows, int[] exits, (int X, int Y) boss)
    {
        return new DungeonMaze(
            "dungeon/test/m.dgn", cols, rows, exits, null,
            new List<CellRect> { new CellRect(0, 0, 0, 0) },
            new List<CellRect> { new CellRect(boss.X, boss.Y, boss.X, boss.Y) },
            null, null, 1f, -1, null, null);
    }

    /// <summary>二乘二的 L 形:(0,0) → (1,0) → (1,1),起点 (0,0)、首领 (1,1);(0,1) 是空格。</summary>
    private static DungeonMaze LShape() => Maze(2, 2, new[] { R, L | D, 0, U }, (1, 1));

    [Fact]
    public void An_exit_to_an_ordinary_cell_is_a_normal_gate_and_an_exit_to_the_boss_cell_is_a_boss_gate()
    {
        DungeonMaze maze = LShape();

        Assert.Equal(GateKind.Normal, OnlineDungeonRoom.GateKindAt(maze, 0, 0, GateSide.Right));   // (0,0) 右 → (1,0)
        Assert.Equal(GateKind.Normal, OnlineDungeonRoom.GateKindAt(maze, 1, 0, GateSide.Left));    // (1,0) 左 → (0,0)
        Assert.Equal(GateKind.Boss, OnlineDungeonRoom.GateKindAt(maze, 1, 0, GateSide.Down));      // (1,0) 下 → (1,1) 首领格
        Assert.Equal(GateKind.Normal, OnlineDungeonRoom.GateKindAt(maze, 1, 1, GateSide.Up));      // 首领格往回走的出口:通向的是普通格
    }

    [Fact]
    public void A_side_without_an_exit_is_a_wall_on_the_left_and_right_and_nothing_up_and_down()
    {
        DungeonMaze maze = LShape();

        Assert.Equal(GateKind.Wall, OnlineDungeonRoom.GateKindAt(maze, 0, 0, GateSide.Left));
        Assert.Equal(GateKind.None, OnlineDungeonRoom.GateKindAt(maze, 0, 0, GateSide.Up));
        Assert.Equal(GateKind.None, OnlineDungeonRoom.GateKindAt(maze, 0, 0, GateSide.Down));
        Assert.Equal(GateKind.Wall, OnlineDungeonRoom.GateKindAt(maze, 1, 0, GateSide.Right));
        Assert.Equal(GateKind.None, OnlineDungeonRoom.GateKindAt(maze, 1, 0, GateSide.Up));
        Assert.Equal(GateKind.Wall, OnlineDungeonRoom.GateKindAt(maze, 1, 1, GateSide.Right));
        Assert.Equal(GateKind.Wall, OnlineDungeonRoom.GateKindAt(maze, 1, 1, GateSide.Left));
        Assert.Equal(GateKind.None, OnlineDungeonRoom.GateKindAt(maze, 1, 1, GateSide.Down));
    }

    [Fact]
    public void An_exit_that_leads_out_of_the_grid_or_a_cell_outside_the_grid_never_throws()
    {
        DungeonMaze lonely = Maze(1, 1, new[] { R }, (0, 0));

        Assert.Equal(GateKind.Normal, OnlineDungeonRoom.GateKindAt(lonely, 0, 0, GateSide.Right));  // 出口码指向迷宫外:邻格取不到,按普通出口
        Assert.Equal(GateKind.Wall, OnlineDungeonRoom.GateKindAt(lonely, 5, 5, GateSide.Left));     // 迷宫外的格没有出口
        Assert.Equal(GateKind.None, OnlineDungeonRoom.GateKindAt(lonely, -1, 0, GateSide.Up));
    }

    [Fact]
    public void A_missing_maze_is_an_error()
    {
        Assert.Throws<ArgumentNullException>(() => OnlineDungeonRoom.GateKindAt(null!, 0, 0, GateSide.Right));
    }
}
