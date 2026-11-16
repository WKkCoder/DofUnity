using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网多房间副本的本地迷宫对位与路线规划(r18-hostmulti,<see cref="HostedRoomMazePlan"/>)。迷宫全是合成的小格子(出口位:右 1、上 2、左 4、下 8),
/// 不读任何数据文件、不含任何原版数值。
/// </summary>
public class HostedRoomMazePlanTests
{
    private const int R = ExitBits.Right;
    private const int U = ExitBits.Up;
    private const int L = ExitBits.Left;
    private const int D = ExitBits.Down;

    private static List<CellRect> Cells(params (int X, int Y)[] cells)
    {
        return cells.Select(c => new CellRect(c.X, c.Y, c.X, c.Y)).ToList();
    }

    private static DungeonMaze Maze(int cols, int rows, int[] exits, (int X, int Y)[] start, (int X, int Y)[] boss, IDictionary<int, int[]> specs = null, int quest = -1)
    {
        return new DungeonMaze("dungeon/test/m.dgn", cols, rows, exits, null, Cells(start), Cells(boss), null, null, 1f, quest, specs, null);
    }

    /// <summary>一条四格的横线:(0,0) → (3,0),起点 (0,0)、首领 (3,0)。</summary>
    private static DungeonMaze Line4() => Maze(4, 1, new[] { R, R | L, R | L, L }, new[] { (0, 0) }, new[] { (3, 0) });

    /// <summary>
    /// 三列两行:
    /// (0,0)右下 (1,0)左右 (2,0)左下
    /// (0,1)上右 (1,1)左右 (2,1)上左;起点 (0,0)、首领 (2,1)。(0,0) 到 (2,1) 有两条同样长的路:右-右-下 与 下-右-右。
    /// </summary>
    private static DungeonMaze Grid32() => Maze(3, 2, new[] { R | D, R | L, L | D, U | R, R | L, U | L }, new[] { (0, 0) }, new[] { (2, 1) });

    /// <summary>L 形:(0,0) 右、(1,0) 左下、(0,1) 空格、(1,1) 上;起点 (0,0)、首领 (1,1)。</summary>
    private static DungeonMaze LShape() => Maze(2, 2, new[] { R, L | D, 0, U }, new[] { (0, 0) }, new[] { (1, 1) });

    private static IReadOnlyList<DungeonMaze> One(DungeonMaze maze) => new[] { maze };

    private static HashSet<(int X, int Y)> Seen(params (int X, int Y)[] cells) => new HashSet<(int X, int Y)>(cells);

    // ------------------------------------------------------------------ 对位

    [Fact]
    public void Align_keeps_every_candidate_that_explains_the_start_cell_and_keeps_their_order()
    {
        DungeonMaze a = Line4();
        DungeonMaze b = Grid32();
        DungeonMaze c = Maze(2, 1, new[] { R, L }, new[] { (1, 0) }, new[] { (1, 0) });     // 起点是 (1,0),(0,0) 不是它的起点格
        IReadOnlyList<DungeonMaze> kept = HostedRoomMazePlan.Align(new[] { a, b, c }, 0, 0, -1, true, null, out HostedMazeStatus status);

        Assert.Equal(HostedMazeStatus.Aligned, status);
        Assert.Equal(new[] { a, b }, kept);
    }

    [Fact]
    public void Align_with_no_candidates_reports_NoMaze()
    {
        Assert.Equal(HostedMazeStatus.NoMaze, Status(null, 0, 0));
        Assert.Equal(HostedMazeStatus.NoMaze, Status(new DungeonMaze[0], 0, 0));
        Assert.Equal(HostedMazeStatus.NoMaze, Status(new DungeonMaze[] { null }, 0, 0));
        IReadOnlyList<DungeonMaze> kept = HostedRoomMazePlan.Align(null, 0, 0, -1, true, null, out _);
        Assert.Empty(kept);
    }

    private static HostedMazeStatus Status(IReadOnlyList<DungeonMaze> mazes, int x, int y, int mapId = -1, bool atStart = true, Func<int, int, bool> allows = null)
    {
        HostedRoomMazePlan.Align(mazes, x, y, mapId, atStart, allows, out HostedMazeStatus status);
        return status;
    }

    [Fact]
    public void Align_rejects_cells_outside_the_maze_and_empty_cells()
    {
        DungeonMaze maze = LShape();
        Assert.Equal(HostedMazeStatus.CellMismatch, Status(One(maze), 0, 1, atStart: false));     // 空格:出口码 0
        Assert.Equal(HostedMazeStatus.CellMismatch, Status(One(maze), 5, 5, atStart: false));     // 越界
        Assert.Equal(HostedMazeStatus.CellMismatch, Status(One(maze), -1, 0, atStart: false));
        Assert.Equal(HostedMazeStatus.CellMismatch, Status(One(maze), 0, 2, atStart: false));
        Assert.Equal(HostedMazeStatus.Aligned, Status(One(maze), 1, 1, atStart: false));
    }

    [Fact]
    public void Align_asks_for_a_listed_start_cell_only_for_the_first_room()
    {
        DungeonMaze maze = Line4();
        Assert.Equal(HostedMazeStatus.CellMismatch, Status(One(maze), 1, 0, atStart: true));      // (1,0) 不是 [start map] 里的格
        Assert.Equal(HostedMazeStatus.Aligned, Status(One(maze), 1, 0, atStart: false));          // 后面的房间不要求
        Assert.Equal(HostedMazeStatus.Aligned, Status(One(maze), 0, 0, atStart: true));
    }

    [Fact]
    public void Align_accepts_any_non_empty_cell_for_a_maze_that_lists_no_start_cells()
    {
        DungeonMaze maze = Maze(2, 1, new[] { R, L }, new (int, int)[0], new[] { (1, 0) });
        Assert.Equal(HostedMazeStatus.Aligned, Status(One(maze), 1, 0, atStart: true));
    }

    [Fact]
    public void Align_checks_the_map_against_the_exit_code_of_the_cell()
    {
        DungeonMaze maze = Grid32();
        var seen = new List<(int Map, int Exits)>();
        Func<int, int, bool> allows = (map, exits) =>
        {
            seen.Add((map, exits));
            return exits != (R | D);
        };

        Assert.Equal(HostedMazeStatus.MapMismatch, Status(One(maze), 0, 0, 905, true, allows));            // (0,0) 的出口码是右下,这张图没登记在它下面
        Assert.Equal((905, R | D), seen.Single());                                                           // 查的就是(地图编号, 这一格的出口码)
        Assert.Equal(HostedMazeStatus.Aligned, Status(One(maze), 1, 0, 905, false, allows));
        Assert.Equal(HostedMazeStatus.Aligned, Status(One(maze), 0, 0, -1, true, allows));                  // 没有地图编号:不查
        Assert.Equal(HostedMazeStatus.Aligned, Status(One(maze), 0, 0, 905, true, null));                    // 没有查询:不查
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void Align_skips_the_exit_code_check_for_cells_with_a_map_specification()
    {
        DungeonMaze maze = Maze(2, 1, new[] { R, L }, new[] { (0, 0) }, new[] { (1, 0) }, new Dictionary<int, int[]> { { 0, new[] { 905 } } });
        Func<int, int, bool> never = (map, exits) => false;

        Assert.Equal(HostedMazeStatus.Aligned, Status(One(maze), 0, 0, 905, true, never));                  // 指定了候选地图的格不看出口码
        Assert.Equal(HostedMazeStatus.MapMismatch, Status(One(maze), 1, 0, 905, false, never));             // 没指定的格照查
    }

    [Fact]
    public void Align_tells_the_failing_gate_apart_when_candidates_fail_differently()
    {
        // 候选一:格对得上、图对不上;候选二:格就不对。只要有一个格对得上,报的是地图对不上
        DungeonMaze a = Line4();
        DungeonMaze b = Maze(1, 1, new[] { R }, new[] { (0, 0) }, new[] { (0, 0) });
        Func<int, int, bool> never = (map, exits) => false;
        Assert.Equal(HostedMazeStatus.MapMismatch, Status(new[] { a, b }, 2, 0, 905, false, never));
        Assert.Equal(HostedMazeStatus.CellMismatch, Status(new[] { a, b }, 9, 0, 905, false, never));
    }

    // ------------------------------------------------------------------ 规划

    [Fact]
    public void Plan_walks_toward_the_boss_one_cell_at_a_time_on_a_line()
    {
        IReadOnlyList<DungeonMaze> mazes = One(Line4());

        HostedMazeStep s0 = HostedRoomMazePlan.Plan(mazes, 0, 0, Seen((0, 0)), false);
        Assert.Equal((HostedMazeNext.Step, GateSide.Right, 1, 0), (s0.Kind, s0.Side, s0.TargetX, s0.TargetY));
        HostedMazeStep s1 = HostedRoomMazePlan.Plan(mazes, 1, 0, Seen((0, 0), (1, 0)), false);
        Assert.Equal((HostedMazeNext.Step, GateSide.Right, 2, 0), (s1.Kind, s1.Side, s1.TargetX, s1.TargetY));
        HostedMazeStep s2 = HostedRoomMazePlan.Plan(mazes, 2, 0, Seen((0, 0), (1, 0), (2, 0)), false);
        Assert.Equal((HostedMazeNext.Step, GateSide.Right, 3, 0), (s2.Kind, s2.Side, s2.TargetX, s2.TargetY));
        Assert.True(s2.IsStep);
        HostedMazeStep s3 = HostedRoomMazePlan.Plan(mazes, 3, 0, Seen((0, 0), (1, 0), (2, 0), (3, 0)), false);
        Assert.Equal(HostedMazeNext.Last, s3.Kind);                                                          // 首领格:最后一间
        Assert.False(s3.IsStep);
    }

    [Fact]
    public void Plan_breaks_ties_between_equally_short_routes_by_a_fixed_order_right_up_left_down()
    {
        IReadOnlyList<DungeonMaze> mazes = One(Grid32());

        HostedMazeStep first = HostedRoomMazePlan.Plan(mazes, 0, 0, Seen((0, 0)), false);
        Assert.Equal((GateSide.Right, 1, 0), (first.Side, first.TargetX, first.TargetY));                    // 右-右-下 与 下-右-右 一样长:右优先
        // 另一端:从 (2,1) 往回看起点方向不是目标;这里换个首领,让两条路都是"上"与"左"
        DungeonMaze toStart = Maze(3, 2, new[] { R | D, R | L, L | D, U | R, R | L, U | L }, new[] { (2, 1) }, new[] { (0, 0) });
        HostedMazeStep back = HostedRoomMazePlan.Plan(One(toStart), 2, 1, Seen((2, 1)), false);
        Assert.Equal(GateSide.Up, back.Side);                                                                // 上-左-左 与 左-左-上:上优先(次序是右、上、左、下)
        Assert.Equal((2, 0), (back.TargetX, back.TargetY));
    }

    [Fact]
    public void Plan_breaks_the_other_ties_too_Right_before_Up_and_Left_before_Down()
    {
        // 二乘二全通:任何两个对角格之间都有两条同样长的路
        int[] open = { R | D, L | D, U | R, U | L };
        DungeonMaze toTopRight = Maze(2, 2, open, new[] { (0, 0) }, new[] { (1, 0) });
        HostedMazeStep rightFirst = HostedRoomMazePlan.Plan(One(toTopRight), 0, 1, Seen((0, 1)), false);   // (0,1) → (1,0):右-上 与 上-右:右优先
        Assert.Equal((GateSide.Right, 1, 1), (rightFirst.Side, rightFirst.TargetX, rightFirst.TargetY));

        DungeonMaze toBottomLeft = Maze(2, 2, open, new[] { (0, 0) }, new[] { (0, 1) });
        HostedMazeStep leftFirst = HostedRoomMazePlan.Plan(One(toBottomLeft), 1, 0, Seen((1, 0)), false);  // (1,0) → (0,1):左-下 与 下-左:左优先
        Assert.Equal((GateSide.Left, 0, 0), (leftFirst.Side, leftFirst.TargetX, leftFirst.TargetY));

        DungeonMaze toTopLeft = Maze(2, 2, open, new[] { (0, 0) }, new[] { (0, 0) });
        HostedMazeStep upFirst = HostedRoomMazePlan.Plan(One(toTopLeft), 1, 1, Seen((1, 1)), false);       // (1,1) → (0,0):上-左 与 左-上:上优先
        Assert.Equal((GateSide.Up, 1, 0), (upFirst.Side, upFirst.TargetX, upFirst.TargetY));
    }

    [Fact]
    public void Plan_never_steps_on_a_visited_cell()
    {
        IReadOnlyList<DungeonMaze> mazes = One(Grid32());

        HostedMazeStep around = HostedRoomMazePlan.Plan(mazes, 0, 0, Seen((0, 0), (1, 0)), false);           // 右边走过了:绕下面
        Assert.Equal((HostedMazeNext.Step, GateSide.Down, 0, 1), (around.Kind, around.Side, around.TargetX, around.TargetY));
        HostedMazeStep blocked = HostedRoomMazePlan.Plan(mazes, 1, 0, Seen((0, 0), (1, 0), (2, 0)), false);  // 两头都走过了:无路可走
        Assert.Equal(HostedMazeNext.Unreachable, blocked.Kind);
    }

    [Fact]
    public void Plan_is_Last_on_a_boss_map_wherever_the_cell_is()
    {
        IReadOnlyList<DungeonMaze> mazes = One(Line4());
        Assert.Equal(HostedMazeNext.Last, HostedRoomMazePlan.Plan(mazes, 0, 0, Seen((0, 0)), true).Kind);    // 地图已知是首领房间:不再往前走
        Assert.Equal(HostedMazeNext.Step, HostedRoomMazePlan.Plan(mazes, 0, 0, Seen((0, 0)), false).Kind);
    }

    [Fact]
    public void Plan_keeps_going_to_another_boss_candidate_until_none_is_left()
    {
        DungeonMaze twoBosses = Maze(4, 1, new[] { R, R | L, R | L, L }, new[] { (0, 0) }, new[] { (2, 0), (3, 0) });
        IReadOnlyList<DungeonMaze> mazes = One(twoBosses);

        // 站在第一个首领候选格上、地图又没说它是首领房间:还有一个候选没走过,继续走
        HostedMazeStep onFirst = HostedRoomMazePlan.Plan(mazes, 2, 0, Seen((0, 0), (1, 0), (2, 0)), false);
        Assert.Equal((HostedMazeNext.Step, GateSide.Right, 3, 0), (onFirst.Kind, onFirst.Side, onFirst.TargetX, onFirst.TargetY));
        // 地图说它是首领房间:到头
        Assert.Equal(HostedMazeNext.Last, HostedRoomMazePlan.Plan(mazes, 2, 0, Seen((0, 0), (1, 0), (2, 0)), true).Kind);
        // 两个候选都走过了:到头
        Assert.Equal(HostedMazeNext.Last, HostedRoomMazePlan.Plan(mazes, 3, 0, Seen((0, 0), (1, 0), (2, 0), (3, 0)), false).Kind);
    }

    [Fact]
    public void Plan_targets_the_nearest_boss_candidate()
    {
        DungeonMaze twoBosses = Maze(4, 1, new[] { R, R | L, R | L, L }, new[] { (1, 0) }, new[] { (0, 0), (3, 0) });
        HostedMazeStep toNearest = HostedRoomMazePlan.Plan(One(twoBosses), 1, 0, Seen((1, 0)), false);       // (0,0) 一步、(3,0) 两步
        Assert.Equal((GateSide.Left, 0, 0), (toNearest.Side, toNearest.TargetX, toNearest.TargetY));
    }

    [Fact]
    public void Plan_reports_Unreachable_when_no_unvisited_boss_cell_can_be_reached()
    {
        DungeonMaze noBoss = Maze(3, 1, new[] { R, R | L, L }, new[] { (0, 0) }, new (int, int)[0]);
        Assert.Equal(HostedMazeNext.Unreachable, HostedRoomMazePlan.Plan(One(noBoss), 0, 0, Seen((0, 0)), false).Kind);

        // 首领格被一堵墙隔开:出口码在两侧不一致,走不过去
        DungeonMaze walled = Maze(3, 1, new[] { R, 0 | L, L }, new[] { (0, 0) }, new[] { (2, 0) });
        Assert.Equal(HostedMazeNext.Unreachable, HostedRoomMazePlan.Plan(One(walled), 0, 0, Seen((0, 0)), false).Kind);
    }

    [Fact]
    public void Plan_treats_a_neighbour_that_is_an_empty_cell_as_a_wall()
    {
        // (0,0) 说右边有出口,可右边是空格:不能当路走
        DungeonMaze broken = Maze(2, 1, new[] { R, 0 }, new[] { (0, 0) }, new[] { (1, 0) });
        Assert.Equal(HostedMazeNext.Unreachable, HostedRoomMazePlan.Plan(One(broken), 0, 0, Seen((0, 0)), false).Kind);
    }

    [Fact]
    public void Plan_on_a_single_cell_maze_is_Last_even_with_an_outward_exit()
    {
        DungeonMaze single = Maze(1, 1, new[] { R }, new[] { (0, 0) }, new[] { (0, 0) });                    // 右出口通向迷宫外,不算邻格
        Assert.Equal(HostedMazeNext.Last, HostedRoomMazePlan.Plan(One(single), 0, 0, Seen((0, 0)), false).Kind);
    }

    [Fact]
    public void Plan_with_no_mazes_is_Unreachable()
    {
        Assert.Equal(HostedMazeNext.Unreachable, HostedRoomMazePlan.Plan(null, 0, 0, null, false).Kind);
        Assert.Equal(HostedMazeNext.Unreachable, HostedRoomMazePlan.Plan(new DungeonMaze[0], 0, 0, null, false).Kind);
        Assert.Equal(HostedMazeNext.Unreachable, HostedRoomMazePlan.Plan(new DungeonMaze[] { null }, 0, 0, null, false).Kind);
    }

    [Fact]
    public void Plan_agrees_only_when_every_candidate_maze_says_the_same_thing()
    {
        DungeonMaze rightFirst = Grid32();
        DungeonMaze alsoRight = Maze(3, 2, new[] { R | D, R | L, L | D, U | R, R | L, U | L }, new[] { (0, 0) }, new[] { (2, 1) });
        DungeonMaze downward = Maze(3, 2, new[] { R | D, R | L, L | D, U | R, R | L, U | L }, new[] { (0, 0) }, new[] { (0, 1) });   // 首领在 (0,1):往下一步

        HostedMazeStep agree = HostedRoomMazePlan.Plan(new[] { rightFirst, alsoRight }, 0, 0, Seen((0, 0)), false);
        Assert.Equal((HostedMazeNext.Step, GateSide.Right), (agree.Kind, agree.Side));
        HostedMazeStep disagree = HostedRoomMazePlan.Plan(new[] { rightFirst, downward }, 0, 0, Seen((0, 0)), false);
        Assert.Equal(HostedMazeNext.Ambiguous, disagree.Kind);
        // 一个说"到头了"、一个说"往前走":也是各说各话
        DungeonMaze bossHere = Maze(3, 2, new[] { R | D, R | L, L | D, U | R, R | L, U | L }, new[] { (0, 0) }, new[] { (0, 0) });
        Assert.Equal(HostedMazeNext.Ambiguous, HostedRoomMazePlan.Plan(new[] { rightFirst, bossHere }, 0, 0, Seen((0, 0)), false).Kind);
        // 与候选先后无关
        Assert.Equal(HostedMazeNext.Ambiguous, HostedRoomMazePlan.Plan(new[] { downward, rightFirst }, 0, 0, Seen((0, 0)), false).Kind);
        // 同样的结论只要出口不同就是各说各话
        DungeonMaze leftward = Maze(3, 1, new[] { R, R | L, L }, new[] { (1, 0) }, new[] { (0, 0) });
        DungeonMaze rightward = Maze(3, 1, new[] { R, R | L, L }, new[] { (1, 0) }, new[] { (2, 0) });
        Assert.Equal(HostedMazeNext.Ambiguous, HostedRoomMazePlan.Plan(new[] { leftward, rightward }, 1, 0, Seen((1, 0)), false).Kind);
    }

    [Fact]
    public void Plan_does_not_modify_the_visited_set_it_is_given()
    {
        HashSet<(int X, int Y)> seen = Seen((0, 0));
        HostedRoomMazePlan.Plan(One(Grid32()), 0, 0, seen, false);
        Assert.Equal(new[] { (0, 0) }, seen.ToArray());
    }

    [Fact]
    public void Replanning_from_every_new_cell_walks_the_shortest_route_to_the_boss_without_repeating_a_cell()
    {
        IReadOnlyList<DungeonMaze> mazes = One(Grid32());
        int x = 0, y = 0;
        var seen = Seen((0, 0));
        var route = new List<(int, int)> { (0, 0) };
        for (int guard = 0; guard < 20; guard++)
        {
            HostedMazeStep step = HostedRoomMazePlan.Plan(mazes, x, y, seen, false);
            if (step.Kind == HostedMazeNext.Last)
            {
                break;
            }
            Assert.Equal(HostedMazeNext.Step, step.Kind);
            Assert.True(seen.Add((step.TargetX, step.TargetY)), "不重复走格子");
            ExitBits.Step(step.Side, out int dx, out int dy);
            Assert.Equal((x + dx, y + dy), (step.TargetX, step.TargetY));                                    // 目标格就是沿这一侧出口走出的邻格
            x = step.TargetX;
            y = step.TargetY;
            route.Add((x, y));
        }

        Assert.Equal(new[] { (0, 0), (1, 0), (2, 0), (2, 1) }, route.ToArray());                            // 最短路 3 步
    }

    [Fact]
    public void Every_planned_target_is_inside_the_maze_and_reachable_through_a_real_exit()
    {
        foreach (DungeonMaze maze in new[] { Line4(), Grid32(), LShape() })
        {
            int x = maze.StartCells[0].X0, y = maze.StartCells[0].Y0;
            var seen = Seen((x, y));
            for (int guard = 0; guard < 20; guard++)
            {
                HostedMazeStep step = HostedRoomMazePlan.Plan(One(maze), x, y, seen, false);
                if (step.Kind != HostedMazeNext.Step)
                {
                    break;
                }
                Assert.True(maze.InBounds(step.TargetX, step.TargetY));
                Assert.True(maze.HasExit(x, y, step.Side));
                Assert.False(maze.IsEmptyCell(step.TargetX, step.TargetY));
                seen.Add((step.TargetX, step.TargetY));
                x = step.TargetX;
                y = step.TargetY;
            }
            Assert.True(maze.IsBossCell(x, y), "走到了首领格");
        }
    }
}
