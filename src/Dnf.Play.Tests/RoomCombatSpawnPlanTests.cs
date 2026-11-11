using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// <see cref="RoomCombatSession.Create"/> 的出生点计划(<c>spawnPlan</c>,r15-dungeonhost):走生产路径造房间,验证"第 i 项造出来的怪序号恒为 i"、
/// 造不出来的项缺席但不让后面的序号前移、不给计划时与以前一字不差(地图自己的 [monster]、造不出来的跳过且后面的序号前移)。
/// 房间与怪物目录都是夹具里的合成数据(<see cref="HostedRoomFixture"/>)。
/// </summary>
public class RoomCombatSpawnPlanTests
{
    private const int Ok = HostedRoomFixture.Buildable;
    private const int Bad = HostedRoomFixture.Unbuildable;

    private static RoomMonsterSpawn S(int code, int x) => HostedRoomFixture.Spawn(code, x);

    private static int[] Indexes(RoomCombatSession session) => session.Monsters.Select(m => m.Index).ToArray();

    [Fact]
    public void A_plan_builds_exactly_the_planned_monsters_with_the_plan_position_as_index()
    {
        // 地图自己有 1 个出生点;计划要 3 只:房间里只能有计划里的 3 只,地图自己的那只不出现
        using var fx = new HostedRoomFixture(new[] { S(Ok, 999) });
        var plan = new[] { S(Ok, 340), S(Ok, 380), S(Ok, 420) };

        RoomCombatSession session = fx.Create(map => plan);

        Assert.Equal(3, session.Monsters.Count);
        Assert.Equal(new[] { 0, 1, 2 }, Indexes(session));
        Assert.Equal(new[] { 340, 380, 420 }, session.Monsters.Select(m => (int)m.X).ToArray());
        Assert.All(session.Monsters, m => Assert.False(m.IsSummoned));
        Assert.Equal(3, session.AliveCount);
    }

    [Fact]
    public void An_unbuildable_plan_entry_is_absent_but_the_later_indexes_do_not_shift()
    {
        using var fx = new HostedRoomFixture();
        var plan = new[] { S(Ok, 340), S(Bad, 380), S(Ok, 420) };

        RoomCombatSession session = fx.Create(map => plan);

        Assert.Equal(2, session.Monsters.Count);
        Assert.Equal(new[] { 0, 2 }, Indexes(session));                       // 序号 1 缺席,第三项仍是 2,不是 1
        Assert.Equal(new[] { 340, 420 }, session.Monsters.Select(m => (int)m.X).ToArray());
    }

    [Fact]
    public void The_plan_is_asked_once_and_gets_the_rooms_own_map()
    {
        using var fx = new HostedRoomFixture(new[] { S(Ok, 340) });
        var seen = new List<RoomMap>();

        fx.Create(map =>
        {
            seen.Add(map);
            return RoomSpawnPlan.Fill(2, map.Monsters);
        });

        Assert.Single(seen);
        Assert.Same(fx.Map, seen[0]);
    }

    [Fact]
    public void A_plan_made_of_the_map_templates_cycles_them_to_the_requested_count()
    {
        using var fx = new HostedRoomFixture(new[] { S(Ok, 340), S(Ok, 380) });

        RoomCombatSession session = fx.Create(map => RoomSpawnPlan.Fill(5, map.Monsters));

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, Indexes(session));
        Assert.Equal(new[] { 340, 380, 340, 380, 340 }, session.Monsters.Select(m => (int)m.X).ToArray());
    }

    [Fact]
    public void A_plan_that_returns_null_or_nothing_builds_an_empty_room()
    {
        using var fx = new HostedRoomFixture(new[] { S(Ok, 340) });

        Assert.Empty(fx.Create(map => null).Monsters);
        Assert.Empty(fx.Create(map => new List<RoomMonsterSpawn>()).Monsters);
    }

    [Fact]
    public void Without_a_plan_the_map_own_monsters_are_built_and_unbuildable_ones_shift_the_later_indexes()
    {
        // 缺省路径一字不变:造不出来的跳过、后面的序号前移(这是联网计划路径特意不同的地方,所以钉死缺省行为)
        using var fx = new HostedRoomFixture(new[] { S(Ok, 340), S(Bad, 380), S(Ok, 420) });

        RoomCombatSession session = fx.Create(null);

        Assert.Equal(2, session.Monsters.Count);
        Assert.Equal(new[] { 0, 1 }, Indexes(session));
        Assert.Equal(new[] { 340, 420 }, session.Monsters.Select(m => (int)m.X).ToArray());
    }

    [Fact]
    public void A_planned_room_is_an_ordinary_fightable_room()
    {
        // 计划只换了"怪从哪来":怪物是活的、能被打死、死了被击杀观察者认到,房间照常推进
        using var fx = new HostedRoomFixture();
        RoomCombatSession session = fx.Create(map => new[] { S(Ok, 340), S(Ok, 380) });
        session.Update(10, 0, 0);
        Assert.Equal(2, session.AliveCount);

        HostedRoomFixture.Kill(session.Monsters[1]);

        Assert.Equal(1, session.AliveCount);
        var found = new List<int>();
        new RoomKillWatcher(session).Poll(found);
        Assert.Equal(new[] { 1 }, found.ToArray());
    }
}
