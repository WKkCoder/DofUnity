using Dnf.Render;
using Dnf.Render.Tests;

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

    // ------------------------------------------------------------------ 验收后补测:走位的细节(目标点怎么取、卡住与寻路接管的边界);表格式:主角在这里、出口在那里 → 方向

    /// <summary>新建一个走位对象、主角摆在 (<paramref name="heroX"/>, <paramref name="heroY"/>),只走一步:第一步的方向。</summary>
    private static (int X, int Y) FirstStep(GateSide side, double heroX, double heroY)
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession room = Room(fx, new[] { side }, heroX, heroY);
        Assert.True(new HostedRoomDoorWalker().Next(room, side, out int dx, out int dy));
        return (dx, dy);
    }

    /// <summary>主角一步也不动,连着叫 <paramref name="calls"/> 次(想动却挪不动:卡住的步数一路涨);返回最后一次的方向。</summary>
    private static (int X, int Y) CallWithoutMoving(HostedRoomDoorWalker walker, RoomCombatSession room, GateSide side, int calls)
    {
        int dx = 0, dy = 0;
        for (int i = 0; i < calls; i++)
        {
            Assert.True(walker.Next(room, side, out dx, out dy));
        }
        return (dx, dy);
    }

    [Theory]
    [InlineData(GateSide.Right, 1100.0, 1)]       // 目标点 x = 出口 1096 + 8 = 1104;离它 4 像素(在 6 像素的死区里)也照样往门顶
    [InlineData(GateSide.Right, 1108.0, -1)]      // 越过目标点 4 像素:往回顶向目标点
    [InlineData(GateSide.Right, 1104.0, 0)]       // 正好在目标点上:不动
    [InlineData(GateSide.Left, 20.0, -1)]         // 目标点 x = 出口 24 − 8 = 16
    [InlineData(GateSide.Left, 12.0, 1)]
    [InlineData(GateSide.Left, 16.0, 0)]
    public void On_a_side_exit_a_hero_within_the_dead_band_of_the_target_still_pushes_towards_it(GateSide side, double heroX, int expectedDirX)
    {
        Assert.Equal((expectedDirX, 0), FirstStep(side, heroX, 100));
    }

    [Theory]
    [InlineData(100.0, 0)]                        // 出口行 y = 100
    [InlineData(101.4, 0)]                        // 差 1.4 像素:不用挪(只有差得多于 1.5 像素才挪)
    [InlineData(98.6, 0)]
    [InlineData(101.6, -1)]                       // 差 1.6 像素:往上挪
    [InlineData(98.4, 1)]                         // 往下挪
    [InlineData(105.0, -1)]
    [InlineData(95.0, 1)]
    [InlineData(112.0, -1)]
    public void On_a_side_exit_a_hero_more_than_a_pixel_and_a_half_off_the_exit_row_is_nudged_into_it(double heroY, int expectedDirY)
    {
        Assert.Equal((1, expectedDirY), FirstStep(GateSide.Right, 1050, heroY));
    }

    [Theory]
    [InlineData(GateSide.Up, 520.0, 1, -1)]       // 上出口 x = 560;横向差 40 像素:算对准了,开始往上顶(y 目标 40 − 200 在可走带外)
    [InlineData(GateSide.Up, 519.0, 1, 0)]        // 差 41 像素:还没对准,只挪 x
    [InlineData(GateSide.Up, 600.0, -1, -1)]
    [InlineData(GateSide.Up, 601.0, -1, 0)]
    [InlineData(GateSide.Up, 555.0, 0, -1)]       // 差 5 像素:死区(6 像素)里不再挪 x,y 照样往外顶
    [InlineData(GateSide.Up, 566.0, 0, -1)]
    [InlineData(GateSide.Up, 567.0, -1, -1)]      // 差 7 像素:出了死区,x 往回挪
    [InlineData(GateSide.Down, 520.0, 1, 1)]      // 下出口 x = 560,y 目标 170 + 200
    [InlineData(GateSide.Down, 519.0, 1, 0)]
    [InlineData(GateSide.Down, 600.0, -1, 1)]
    [InlineData(GateSide.Down, 601.0, -1, 0)]
    [InlineData(GateSide.Down, 555.0, 0, 1)]
    public void On_a_top_or_bottom_exit_the_hero_only_starts_pushing_out_once_it_is_within_forty_pixels_across(GateSide side, double heroX, int expectedDirX, int expectedDirY)
    {
        Assert.Equal((expectedDirX, expectedDirY), FirstStep(side, heroX, 100));
    }

    /// <summary>一块只在舞台第 9 行(y 从 108 到 119)有挡格的门体表:2 行 × 14 列,第 0 行空、第 1 行全挡;用锚点行 0 摆在出口 y = 100(第 8 行)那一行起。</summary>
    private static TileFile WedgeRow9()
    {
        var pass = new byte[14 * 2];
        for (int c = 0; c < 14; c++)
        {
            pass[14 + c] = 2;
        }
        return new TileFile("wedge", new List<KeyValuePair<string, int>>(), 80, pass);
    }

    [Theory]
    [InlineData(105.2, 0)]                        // 门体最先盖到占位盒的是出口 y 往下 3 像素那一行,再加 1 像素的余量:目标行 104;差 1.2 像素,不挪
    [InlineData(102.4, 1)]                        // 差 1.6 像素:往下挪
    [InlineData(104.0, 0)]
    [InlineData(108.0, -1)]
    public void Pushing_into_a_door_body_the_hero_aims_one_pixel_below_the_first_row_the_body_reaches(double heroY, int expectedDirY)
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession room = Room(fx, new[] { GateSide.Right }, 1050, heroY);
        GateOf(room, GateSide.Right).SetBody(WedgeRow9(), 13, 0);
        Assert.True(GateOf(room, GateSide.Right).BodyReachesGate(room.Hero.BodyWidth, room.Hero.BodyDepth));      // 夹具自检:这块门体确实盖着出口那一带

        Assert.True(new HostedRoomDoorWalker().Next(room, GateSide.Right, out int dx, out int dy));

        Assert.Equal((1, expectedDirY), (dx, dy));
    }

    [Fact]
    public void A_hero_that_cannot_move_makes_the_walker_take_the_path_on_the_fourteenth_call_and_not_before()
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession room = Room(fx, new[] { GateSide.Right }, 300, 100);
        var walker = new HostedRoomDoorWalker();

        for (int call = 1; call <= 13; call++)
        {
            CallWithoutMoving(walker, room, GateSide.Right, 1);
            Assert.Equal(0, walker.PathEngaged);                                  // 第 2 次起每次累加一步"想动却没动",第 13 次之后是 12 步,要到下一次叫才接管
        }

        CallWithoutMoving(walker, room, GateSide.Right, 1);
        Assert.Equal(1, walker.PathEngaged);
        Assert.Equal(14, walker.Steps);
    }

    [Theory]
    [InlineData(0.0, 61)]                         // 一直在动(纵向来回 0.5 像素)、离门的距离却不变:连着 60 步没变近,第 61 次叫的时候接管
    [InlineData(0.01, 61)]                        // 每步只近 0.01 像素,60 步一共 0.6 像素,不到 1 像素:仍算没变近
    [InlineData(0.5, 0)]                          // 每步近 0.5 像素:每隔几步就攒够 1 像素,一直在变近,不接管
    [InlineData(2.0, 0)]
    public void Creeping_closer_by_less_than_a_pixel_does_not_count_as_getting_closer(double closerPerCall, int engagedAtCall)
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession room = Room(fx, new[] { GateSide.Right }, 300, 150);
        var walker = new HostedRoomDoorWalker();

        int engagedAt = 0;
        for (int call = 1; call <= 200 && engagedAt == 0; call++)
        {
            // 横向每次靠近 closerPerCall 像素;纵向在 150 与 150.5 之间来回,让主角每次都比上一次动了 0.5 像素(不算卡住,只看有没有变近)
            room.Hero.Place(300 + closerPerCall * (call - 1), call % 2 == 1 ? 150.0 : 150.5, false, HeroMotion.Rest, 0);
            walker.Next(room, GateSide.Right, out _, out _);
            if (walker.PathEngaged > 0)
            {
                engagedAt = call;
            }
        }

        Assert.Equal(engagedAtCall, engagedAt);
    }

    [Theory]
    [InlineData("attack")]
    [InlineData("hurt")]
    [InlineData("down")]
    public void A_hero_locked_by_an_animation_is_never_counted_as_stuck(string lockedBy)
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession room = Room(fx, new[] { GateSide.Right }, 300, 100);
        TownHero hero = room.Hero;
        hero.LoadHurtMotions(fx.Assets, AvatarFixture.ActionChrPath);             // 夹具主角的脚本没有受击与倒地动作:借带这两个动作的脚本补上
        switch (lockedBy)
        {
            case "attack":
                hero.Place(300, 100, false, HeroMotion.Attack, 0);
                Assert.True(hero.IsAttacking);
                break;
            case "hurt":
                Assert.True(hero.BeginHurt(100000, 0, 0, false));
                Assert.True(hero.IsHurt);
                break;
            default:
                Assert.True(hero.BeginDown());
                Assert.True(hero.IsDown);
                break;
        }
        var walker = new HostedRoomDoorWalker();

        for (int call = 1; call <= 45; call++)
        {
            Assert.True(walker.Next(room, GateSide.Right, out int dx, out int dy));
            Assert.Equal((1, 0), (dx, dy));                                       // 被动画锁着动不了不算"卡住":一直只顶着门,不上下晃
        }

        Assert.Equal(0, walker.PathEngaged);                                      // 也没有因为"卡住"而改走寻路
    }

    [Fact]
    public void Standing_just_above_the_goal_band_the_path_walks_down_into_it_and_just_inside_it_goes_back_to_pushing_into_the_door()
    {
        // 右出口无门体:寻路的目标是出口 x 往里 12 像素(x ≥ 1084)、纵向在识别矩形内缩 6 像素(y ∈ [53, 147))的那一带
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession room = Room(fx, new[] { GateSide.Right }, 1090, 52);
        var walker = new HostedRoomDoorWalker();

        for (int call = 1; call <= 13; call++)
        {
            Assert.Equal((1, 1), CallWithoutMoving(walker, room, GateSide.Right, 1));      // 前 13 次是直线:往门顶、往出口行(y = 100)挪
        }
        Assert.Equal(0, walker.PathEngaged);

        // 第 14 次接管寻路:主角离目标带只差一格(2 像素),算"到了"(代价 ≤ 1):只顶着出口走,不再动 y
        Assert.Equal((1, 0), CallWithoutMoving(walker, room, GateSide.Right, 1));
        Assert.Equal(1, walker.PathEngaged);
        Assert.Equal((1, 0), CallWithoutMoving(walker, room, GateSide.Right, 1));          // 到了之后一直是这样
    }

    [Fact]
    public void Standing_a_few_cells_above_the_goal_band_the_path_walks_straight_down_towards_it()
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession room = Room(fx, new[] { GateSide.Right }, 1090, 48);
        var walker = new HostedRoomDoorWalker();

        CallWithoutMoving(walker, room, GateSide.Right, 13);
        Assert.Equal(0, walker.PathEngaged);

        // 第 14 次接管寻路:离目标带还有三格(6 像素):不算到,顺着代价最小的方向走,路点就在正下方,只往下
        Assert.Equal((0, 1), CallWithoutMoving(walker, room, GateSide.Right, 1));
        Assert.Equal(1, walker.PathEngaged);
        Assert.Equal(0, walker.PathBlockedSteps);
    }

    [Fact]
    public void Switching_rooms_drops_the_path_takeover_of_the_previous_room()
    {
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession first = Room(fx, new[] { GateSide.Right }, 1090, 52);
        RoomCombatSession second = Room(fx, new[] { GateSide.Right }, 1090, 48);
        var walker = new HostedRoomDoorWalker();
        CallWithoutMoving(walker, first, GateSide.Right, 14);
        Assert.Equal(1, walker.PathEngaged);                                               // 在第一间房里已经改走寻路了

        // 换到第二间房的第一步:从头来,先走直线(往门顶、往出口行挪);若还留着上一间的接管,这一步会走寻路的路点,方向是 (0, 1)
        Assert.Equal((1, 1), CallWithoutMoving(walker, second, GateSide.Right, 1));
        Assert.Equal(1, walker.PathEngaged);                                               // 也没有新的接管
    }

    [Theory]
    [InlineData(GateSide.Right, 300.0, 1)]
    [InlineData(GateSide.Left, 900.0, -1)]
    public void When_the_path_takes_over_in_front_of_a_door_with_a_body_the_spot_by_the_door_can_be_reached(GateSide side, double heroX, int expectedDirX)
    {
        // 门体是一道墙(舞台第 9 行,y 从 108 到 119;右边的从 x = 880 起一直到地图边,左边的到 x = 240 止),主角自己的碰撞体也算它:
        // 寻路的目标是"往门这一侧挪 12 像素占位盒就压到门体"的那些格子,它们在门体外面、够得着;方向反了(往里挪 12 像素)的话落进门体里,没有一格能站,找不到路
        using var fx = new HostedGatedRoomFixture();
        RoomCombatSession room = Room(fx, new[] { side }, heroX, 100);
        GateOf(room, side).SetBody(WedgeRow9(), side == GateSide.Left ? 0 : 13, 0);
        room.Hero.Blocker = new GateBodies(room.Room.Content.Gates);
        var walker = new HostedRoomDoorWalker();

        (int x, int y) last = CallWithoutMoving(walker, room, side, 14);                   // 主角不动:第 14 次起寻路接管
        Assert.Equal(1, walker.PathEngaged);
        for (int i = 0; i < 5; i++)
        {
            last = CallWithoutMoving(walker, room, side, 1);
            Assert.Equal(expectedDirX, last.x);                                            // 朝着门走
        }

        Assert.Equal(0, walker.PathBlockedSteps);                                          // 路是通的:没有一步"找不到路"
    }
}
