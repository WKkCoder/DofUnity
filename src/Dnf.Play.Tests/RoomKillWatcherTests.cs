using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 房间里出生点怪死亡的只读观察者(<see cref="RoomKillWatcher"/>,联网战斗的 Play 层接线):新死的怪只交一次、按怪物列表顺序、召唤物不交、
/// 观察者不改房间。怪物与房间都是夹具里的合成数据;"打死"一部分用 <see cref="MonsterActor.ApplyHit(int, bool, double, bool)"/> 直接扣血,
/// 一部分走主角真实的挥击,两条路在观察者眼里是同一个事实(血量归零)。
/// </summary>
public class RoomKillWatcherTests
{
    private static readonly (int X, int Y)[] ThreeInARow = { (340, 100), (380, 100), (420, 100) };

    private static GameData DataOf(DnfAssets assets) => new GameData(new PvfScriptSource(assets.Pvf));

    private static void Kill(RoomCombatSession session, int position)
    {
        MonsterActor monster = session.Monsters[position];
        Assert.True(monster.ApplyHit(1_000_000, false, 0, false), "这一下应当把它打死");
        Assert.False(monster.IsAlive);
    }

    private static int[] PollOnce(RoomKillWatcher watcher)
    {
        var into = new List<int>();
        int found = watcher.Poll(into);
        Assert.Equal(found, into.Count);
        return into.ToArray();
    }

    [Fact]
    public void A_watcher_reports_nothing_while_every_monster_lives()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var watcher = new RoomKillWatcher(arena.Session);

        Assert.Empty(PollOnce(watcher));
        Assert.Empty(PollOnce(watcher));
        Assert.Equal(0, watcher.Reported);
        Assert.Same(arena.Session, watcher.Room);
    }

    [Fact]
    public void A_dead_monster_is_reported_once_with_its_index_and_never_again()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var watcher = new RoomKillWatcher(arena.Session);

        Kill(arena.Session, 1);
        Assert.Equal(new[] { 1 }, PollOnce(watcher));
        Assert.Empty(PollOnce(watcher));                       // 同一只不再交
        Assert.Equal(1, watcher.Reported);

        Kill(arena.Session, 2);
        Assert.Equal(new[] { 2 }, PollOnce(watcher));          // 只交新死的
        Assert.Equal(2, watcher.Reported);
    }

    [Fact]
    public void Several_deaths_between_two_polls_come_out_in_list_order_not_in_death_order()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var watcher = new RoomKillWatcher(arena.Session);

        Kill(arena.Session, 2);
        Kill(arena.Session, 0);

        Assert.Equal(new[] { 0, 2 }, PollOnce(watcher));
        Assert.Equal(2, watcher.Reported);
    }

    [Fact]
    public void Monsters_that_died_before_the_watcher_existed_are_reported_at_its_first_poll()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        Kill(arena.Session, 0);
        Kill(arena.Session, 1);

        var late = new RoomKillWatcher(arena.Session);

        Assert.Equal(new[] { 0, 1 }, PollOnce(late));
    }

    [Fact]
    public void The_index_a_watcher_reports_is_the_monsters_position_in_the_room_list()
    {
        // 联网对应表按"位置对号":出生点怪的 Index 必须等于它在列表里的位置,观察者交出的就是这个数
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        for (int i = 0; i < arena.Session.Monsters.Count; i++)
        {
            Assert.Equal(i, arena.Session.Monsters[i].Index);
        }
        var watcher = new RoomKillWatcher(arena.Session);
        for (int i = arena.Session.Monsters.Count - 1; i >= 0; i--)
        {
            Kill(arena.Session, i);
            Assert.Equal(new[] { i }, PollOnce(watcher));
        }
    }

    [Fact]
    public void A_kill_made_by_the_heros_real_swing_is_reported_too()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 1);
        RoomCombatSession s = arena.Session;
        var watcher = new RoomKillWatcher(s);

        s.Update(10, 0, 0, true);
        for (int i = 0; i < 100 && s.Kills == 0; i++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.Equal(1, s.Kills);                              // 战斗核心自己认的击杀

        Assert.Equal(new[] { 0 }, PollOnce(watcher));
    }

    [Fact]
    public void A_summoned_monster_is_never_reported_but_the_map_monster_beside_it_is()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        s.AttachActs(new ActLibrary(arena.Assets, DataOf(arena.Assets)));
        MonsterModel model = MonsterModel.Read(arena.Assets, AvatarFixture.BlobMobPath);
        s.UseSummons((code, level, index) =>
            new MonsterActor(index, new RoomMonsterSpawn(code, 0, 0, 0, 0, 0, 1, 0, "[fixed]", "[normal]"), model, level, 1, 222.5, 1500, 0));
        s.ActHost.SummonMonster(s.Monsters[0].ActSelf, 7, 10, 340, 100, 0, true, 0);
        s.Update(10, 0, 0, true);
        Assert.Equal(2, s.Monsters.Count);
        Assert.True(s.Monsters[1].IsSummoned);
        var watcher = new RoomKillWatcher(s);

        Kill(s, 1);                                            // 召唤物死了
        Assert.Empty(PollOnce(watcher));
        Assert.Equal(0, watcher.Reported);

        Kill(s, 0);                                            // 出生点的怪死了
        Assert.Equal(new[] { 0 }, PollOnce(watcher));
    }

    [Fact]
    public void Polling_changes_nothing_in_the_room()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        RoomCombatSession s = arena.Session;
        Kill(s, 0);
        (int Kills, int Hits, int Alive, int Count, int Hp1, int Hp2) before = (s.Kills, s.Hits, s.AliveCount, s.Monsters.Count, s.Monsters[1].Hp, s.Monsters[2].Hp);
        var watcher = new RoomKillWatcher(s);

        PollOnce(watcher);
        PollOnce(watcher);

        (int, int, int, int, int, int) after = (s.Kills, s.Hits, s.AliveCount, s.Monsters.Count, s.Monsters[1].Hp, s.Monsters[2].Hp);
        Assert.Equal(before, after);
    }

    [Fact]
    public void The_watcher_follows_the_room_it_was_given_not_the_session_that_owns_it()
    {
        using var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.RingLayout(),
            (cx, cy) => cx == 0 && cy == 0 ? ThreeInARow : DungeonSessionTests.None(), hp: 100);
        RoomCombatSession startRoom = d.Session.Current;
        var watcher = new RoomKillWatcher(startRoom);

        Kill(startRoom, 2);
        Kill(startRoom, 0);

        Assert.Equal(new[] { 0, 2 }, PollOnce(watcher));
        Assert.Same(startRoom, watcher.Room);
    }

    [Fact]
    public void The_constructor_and_poll_refuse_null()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        Assert.Throws<ArgumentNullException>(() => new RoomKillWatcher(null!));
        Assert.Throws<ArgumentNullException>(() => new RoomKillWatcher(arena.Session).Poll(null!));
    }
}
