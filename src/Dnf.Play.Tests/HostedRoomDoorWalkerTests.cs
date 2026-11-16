using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网房间里走向出口的走位(<see cref="HostedRoomDoorWalker"/>,r18-hostmulti):清完怪之后朝通向下一间房的门走,左右出口顶着门走、上下出口往边缘外顶,
/// 走到离出口足够近时就是离线同一个判定(<see cref="DungeonSession.HeroTriggers"/>)触发;房间地图没有那一侧出口的位置时不动也不出错;换了房间自己清掉上一间的状态。
/// 房间是带出口的合成房间(<see cref="HostedGatedRoomFixture"/>:全可走、没有门体、没有障碍),不连任何联网层;数据全是合成的。
/// </summary>
public class HostedRoomDoorWalkerTests
{
    private static RoomMonsterSpawn[] NoMonsters => new RoomMonsterSpawn[0];

    private static RoomCombatSession Room(HostedGatedRoomFixture fx, IEnumerable<GateSide> gates, double heroX, double heroY)
    {
        return fx.Create(NoMonsters, null, gates, false, heroX, heroY);
    }

    private static RoomGate GateOf(RoomCombatSession room, GateSide side)
    {
        return room.Room.Content.Gates.Single(g => g.Side == side);
    }

    private static bool Triggers(RoomCombatSession room, GateSide side)
    {
        return DungeonSession.HeroTriggers(GateOf(room, side), room.Hero);
    }

    /// <summary>让走位一步一步带着主角走(每步 10 毫秒),直到触发这一侧的出口;返回用了几步,超过 <paramref name="maxSteps"/> 步还没触发就让测试失败。</summary>
    private static int WalkUntilTriggered(HostedRoomDoorWalker walker, RoomCombatSession room, GateSide side, int maxSteps = 1500)
    {
        for (int i = 0; i < maxSteps; i++)
        {
            Assert.True(walker.Next(room, side, out int dx, out int dy));
            Assert.InRange(dx, -1, 1);
            Assert.InRange(dy, -1, 1);
            room.Update(10, dx, dy, false, false);
            if (Triggers(room, side))
            {
                return i + 1;
            }
        }
        Assert.Fail("走了 " + maxSteps + " 步还没触发 " + side + " 出口(主角在 " + room.Hero.X + ", " + room.Hero.Y + ")");
        return maxSteps;
    }

    [Theory]
    [InlineData(GateSide.Left, 900.0, 100.0)]
    [InlineData(GateSide.Right, 300.0, 100.0)]
    [InlineData(GateSide.Right, 300.0, 20.0)]
    [InlineData(GateSide.Left, 300.0, 170.0)]
    [InlineData(GateSide.Up, 560.0, 120.0)]
    [InlineData(GateSide.Up, 200.0, 120.0)]
    [InlineData(GateSide.Down, 560.0, 60.0)]
    [InlineData(GateSide.Down, 900.0, 60.0)]
    public void The_walker_brings_the_hero_to_every_kind_of_exit_until_the_offline_trigger_rule_fires(GateSide side, double heroX, double heroY)
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession room = Room(fx, new[] { GateSide.Left, GateSide.Right, GateSide.Up, GateSide.Down }, heroX, heroY);
        Assert.False(Triggers(room, side), "起点不应当已经触发");
        var walker = new HostedRoomDoorWalker();

        int steps = WalkUntilTriggered(walker, room, side);

        Assert.True(steps > 0);
        Assert.Equal(steps, walker.Steps);
    }

    [Theory]
    [InlineData(GateSide.Up, 560.0, 120.0)]
    [InlineData(GateSide.Down, 560.0, 60.0)]
    public void The_walk_to_an_up_or_down_exit_goes_straight_out_without_the_pathfinder_taking_over(GateSide side, double heroX, double heroY)
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession room = Room(fx, new[] { GateSide.Up, GateSide.Down }, heroX, heroY);
        var walker = new HostedRoomDoorWalker();

        WalkUntilTriggered(walker, room, side);

        Assert.Equal(0, walker.PathEngaged);                                      // 走错方向最终也能靠寻路走到,所以得盯着"没有接管"
    }

    [Fact]
    public void Standing_in_front_of_a_side_exit_the_walker_only_pushes_towards_it()
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession right = Room(fx, new[] { GateSide.Right }, 1090, 100);
        RoomCombatSession left = Room(fx, new[] { GateSide.Left }, 30, 100);
        var walker = new HostedRoomDoorWalker();

        Assert.True(walker.Next(right, GateSide.Right, out int rx, out int ry));
        Assert.Equal((1, 0), (rx, ry));                                           // 顶着右出口走,不上下晃

        Assert.True(walker.Next(left, GateSide.Left, out int lx, out int ly));
        Assert.Equal((-1, 0), (lx, ly));
    }

    [Fact]
    public void A_hero_off_the_exit_row_is_moved_into_the_row_while_pushing_towards_the_side_exit()
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession below = Room(fx, new[] { GateSide.Right }, 300, 170);
        RoomCombatSession above = Room(fx, new[] { GateSide.Right }, 300, 20);
        var walker = new HostedRoomDoorWalker();

        Assert.True(walker.Next(below, GateSide.Right, out int bx, out int by));
        Assert.Equal((1, -1), (bx, by));                                          // 出口在上面:往上挪
        Assert.True(walker.Next(above, GateSide.Right, out int ax, out int ay));
        Assert.Equal((1, 1), (ax, ay));                                           // 出口在下面:往下挪
    }

    [Fact]
    public void Without_a_position_for_that_side_the_walker_stands_still_and_says_so()
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession room = Room(fx, new[] { GateSide.Left }, 300, 100);
        var walker = new HostedRoomDoorWalker();

        foreach (GateSide side in new[] { GateSide.Right, GateSide.Up, GateSide.Down })
        {
            Assert.False(walker.Next(room, side, out int dx, out int dy));
            Assert.Equal((0, 0), (dx, dy));
        }
        Assert.Equal(0, walker.Steps);                                            // 没走成的不算一步
        Assert.True(walker.Next(room, GateSide.Left, out _, out _));
        Assert.Equal(1, walker.Steps);
    }

    [Fact]
    public void A_null_room_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new HostedRoomDoorWalker().Next(null, GateSide.Right, out _, out _));
    }

    [Fact]
    public void Switching_to_another_room_starts_the_walk_over_but_keeps_counting_steps()
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession first = Room(fx, new[] { GateSide.Right }, 300, 100);
        RoomCombatSession second = Room(fx, new[] { GateSide.Left }, 900, 100);
        var walker = new HostedRoomDoorWalker();

        WalkUntilTriggered(walker, first, GateSide.Right);
        int firstSteps = walker.Steps;
        int secondSteps = WalkUntilTriggered(walker, second, GateSide.Left);

        Assert.True(secondSteps > 0);
        Assert.Equal(firstSteps + secondSteps, walker.Steps);                     // 同一个走位对象,步数累计
        Assert.Equal(0, walker.PathEngaged);                                      // 没有障碍:一直是直线走过去,没有接管寻路
        Assert.Equal(0, walker.PathBlockedSteps);
    }

    [Fact]
    public void Switching_to_another_room_forgets_how_long_the_hero_had_been_stuck_in_the_previous_one()
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession first = Room(fx, new[] { GateSide.Right }, 1090, 100);
        RoomCombatSession second = Room(fx, new[] { GateSide.Right }, 1090, 100);
        var walker = new HostedRoomDoorWalker();

        for (int i = 0; i < 25; i++)
        {
            walker.Next(first, GateSide.Right, out _, out _);                     // 主角一步也没动(没有 Update):想动却挪不动,"卡住"的步数一路涨
        }
        for (int i = 0; i < 20; i++)
        {
            Assert.True(walker.Next(second, GateSide.Right, out int dx, out int dy));
            Assert.Equal((1, 0), (dx, dy));                                       // 新房间从头数:不会借着上一间攒的步数提前上下晃
        }
    }

    [Fact]
    public void A_hero_that_keeps_moving_without_getting_closer_makes_the_walker_take_the_path_after_the_stall_limit()
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession room = Room(fx, new[] { GateSide.Right }, 1050, 100);
        var walker = new HostedRoomDoorWalker();

        for (int i = 0; i < DungeonBot.StallSteps; i++)
        {
            walker.Next(room, GateSide.Right, out _, out _);
            room.Update(10, -1, 0, false, false);                                 // 主角自己一直往远离出口的方向走:动着(不算卡住)、但离出口越来越远
        }
        Assert.Equal(0, walker.PathEngaged);                                      // 第一步只是起算,要连着 StallSteps 步没变近才接管

        walker.Next(room, GateSide.Right, out _, out _);
        Assert.Equal(1, walker.PathEngaged);
    }

    [Fact]
    public void A_hero_that_cannot_move_for_a_while_is_shaken_up_and_down_and_the_direction_flips_every_forty_steps()
    {
        // 与离线自动机同一套摆脱卡住的做法:想动却一步没动超过 30 步,上下晃 40 步(晃的方向每 40 步换一次)
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession room = Room(fx, new[] { GateSide.Right }, 1090, 100);
        var walker = new HostedRoomDoorWalker();

        var dirsY = new List<int>();
        for (int i = 0; i < 80; i++)
        {
            Assert.True(walker.Next(room, GateSide.Right, out int dx, out int dy));
            Assert.Equal(1, dx);
            dirsY.Add(dy);                                                        // 主角没有 Update:想动却一步没动
        }

        Assert.All(dirsY.Take(31), dy => Assert.Equal(0, dy));                    // 前 31 步只顶着出口(卡住的步数还没超过 30)
        Assert.All(dirsY.Skip(31).Take(8), dy => Assert.Equal(1, dy));            // 第 32 步起上下晃,先往下
        Assert.All(dirsY.Skip(39).Take(40), dy => Assert.Equal(-1, dy));          // 第 40 步起换方向,再过 40 步(第 80 步)又换回来
        Assert.Equal(1, dirsY[79]);
    }

    [Fact]
    public void The_walk_to_a_side_exit_is_the_same_walk_every_time()
    {
        // 同样的起点、同样的房间,走出来的方向序列一样(没有随机、没有时间):联网的批量测试要能复现
        var walks = new List<string>();
        for (int run = 0; run < 2; run++)
        {
            using var fx = new HostedGatedRoomFixture();
            RoomCombatSession room = Room(fx, new[] { GateSide.Right }, 300, 20);
            var walker = new HostedRoomDoorWalker();
            var dirs = new System.Text.StringBuilder();
            for (int i = 0; i < 400; i++)
            {
                walker.Next(room, GateSide.Right, out int dx, out int dy);
                room.Update(10, dx, dy, false, false);
                dirs.Append(dx).Append(',').Append(dy).Append(';');
            }
            walks.Add(dirs.ToString());
        }

        Assert.Equal(walks[0], walks[1]);
    }
}
