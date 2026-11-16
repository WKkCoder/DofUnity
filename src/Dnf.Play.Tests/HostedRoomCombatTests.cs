using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 宿主自己拼房间之后交给 <see cref="HostedRoomCombat"/> 摆怪与战斗的转手(r18-hostmulti):参数原样交给 <see cref="RoomCombatSession.Create"/>,
/// 这里只证明"没有哪个参数在转手时丢了或错位",房间、怪物、战斗都还是生产路径。
/// 它存在的原因(宿主程序集不能直接调 <see cref="RoomCombatSession.Create"/>,Unity 里会 CS0012)见 <see cref="HostedRoomCombat"/> 的说明。
/// </summary>
public class HostedRoomCombatTests : IDisposable
{
    private readonly HostedGatedRoomFixture _fx = new HostedGatedRoomFixture();

    public void Dispose()
    {
        _fx.Dispose();
    }

    private static RoomMonsterSpawn[] Templates(int count)
    {
        return Enumerable.Range(0, count).Select(i => HostedRoomFixture.Spawn(HostedRoomFixture.Buildable, 340 + 40 * i)).ToArray();
    }

    [Fact]
    public void The_room_the_host_put_together_is_the_one_that_gets_the_fight_and_the_monsters_follow_the_spawn_plan()
    {
        RoomSession room = _fx.CreateRoomSession(Templates(6), new[] { GateSide.Right });

        RoomCombatSession combat = HostedRoomCombat.CreateSession(_fx.Assets, room, 7, true, null, map => RoomSpawnPlan.Fill(3, map.Monsters));

        Assert.Same(room, combat.Room);                                           // 房间原样,没有另造
        Assert.Equal(3, combat.Monsters.Count);                                   // 出生点计划要 3 只,不是地图自己的 6 个模板
        Assert.Equal(new[] { 0, 1, 2 }, combat.Monsters.Select(m => m.Index).ToArray());
        Assert.NotNull(combat.Vitals);                                            // 主角有生命值
    }

    [Fact]
    public void Without_a_spawn_plan_the_monsters_are_the_maps_own_and_without_vitals_the_hero_cannot_be_hurt()
    {
        RoomSession room = _fx.CreateRoomSession(Templates(2), new[] { GateSide.Left });

        RoomCombatSession combat = HostedRoomCombat.CreateSession(_fx.Assets, room, 1, false, null, null);

        Assert.Same(room, combat.Room);
        Assert.Equal(2, combat.Monsters.Count);                                   // 地图自己的两个模板
        Assert.Null(combat.Vitals);                                               // -dnfnodamage:没有生命值
    }

    [Fact]
    public void The_level_progress_of_the_hero_is_handed_over_and_sets_the_vitals()
    {
        RoomSession room = _fx.CreateRoomSession(Templates(1), new[] { GateSide.Right });
        HeroProgress progress = ExpTests.Progress(level: 3);

        RoomCombatSession combat = HostedRoomCombat.CreateSession(_fx.Assets, room, 1, true, progress, null);

        Assert.Same(progress, combat.Progress);                                   // 几间房共用的等级进度原样挂上
        Assert.Equal(3, combat.Vitals.Level);                                     // 生命值按这份进度的等级算,不是角色文件的缺省等级
    }

    [Fact]
    public void The_fixture_splits_building_the_room_from_putting_the_fight_on_it_without_changing_what_Create_makes()
    {
        // 夹具拆成"拼房间"与"摆怪"两步之后,Create 还是原来的样子(别的测试都靠它):全新的房间、地图自己的模板、主角没有生命值
        RoomCombatSession first = _fx.Create(Templates(4), null, new[] { GateSide.Right });
        RoomCombatSession second = _fx.Create(Templates(4), null, new[] { GateSide.Right });

        Assert.NotSame(first.Room, second.Room);
        Assert.Equal(4, first.Monsters.Count);
        Assert.Null(first.Vitals);
        Assert.Equal(2, _fx.RoomsCreated);
    }
}
