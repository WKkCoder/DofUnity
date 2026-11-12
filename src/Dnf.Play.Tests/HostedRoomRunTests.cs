using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网副本的房间托管(<see cref="HostedRoomRun"/>,r15-dungeonhost):每步盯房间击杀、每只怪的序号只交一次、结束之后(加延迟)才回城、其余原样转给房间会话。
/// 房间是 <see cref="RoomCombatTests.Arena"/>(合成房间与迷你怪物,怪物序号 = 位置);"联网"一侧用两个委托代替(这里只验托管本身,与联网层的接线见客户端程序集的测试)。
/// </summary>
public class HostedRoomRunTests
{
    private static readonly (int X, int Y)[] ThreeInARow = { (340, 100), (380, 100), (420, 100) };

    /// <summary>托管的两个外部委托:记下交过来的序号、可以拒收、可以随时宣布"结束"。</summary>
    private sealed class Outside
    {
        public readonly List<int> Posted = new List<int>();
        public bool Accept = true;
        public bool Finished;
        public int FinishedChecks;

        public bool Post(int id)
        {
            Posted.Add(id);
            return Accept;
        }

        public bool IsFinished()
        {
            FinishedChecks++;
            return Finished;
        }
    }

    private static HostedRoomRun Host(RoomCombatTests.Arena arena, Outside outside, double returnDelayMs = 0.0)
    {
        return new HostedRoomRun(arena.Session, outside.Post, outside.IsFinished, returnDelayMs);
    }

    private static void Kill(RoomCombatTests.Arena arena, int position)
    {
        MonsterActor monster = arena.Session.Monsters[position];
        Assert.True(monster.ApplyHit(1_000_000, false, 0, false), "这一下应当把它打死");
    }

    [Fact]
    public void A_dead_monster_is_posted_once_with_its_index_however_many_steps_follow()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var outside = new Outside();
        HostedRoomRun run = Host(arena, outside);

        Kill(arena, 1);
        for (int i = 0; i < 20; i++)
        {
            run.Update(10, 0, 0);
        }

        Assert.Equal(new[] { 1 }, outside.Posted.ToArray());
        Assert.Equal(1, run.Reported);
        Assert.Equal(0, run.Dropped);
    }

    [Fact]
    public void Nothing_is_posted_while_every_monster_lives()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var outside = new Outside();
        HostedRoomRun run = Host(arena, outside);

        for (int i = 0; i < 10; i++)
        {
            run.Update(10, 0, 0);
        }

        Assert.Empty(outside.Posted);
        Assert.Equal((0, 0), (run.Reported, run.Dropped));
    }

    [Fact]
    public void Kills_inside_one_step_are_posted_in_list_order_and_each_update_overload_does_the_watching()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var outside = new Outside();
        HostedRoomRun run = Host(arena, outside);

        Kill(arena, 2);
        Kill(arena, 0);
        run.Update(10, 0, 0);                                   // 三参数的重载:同一步里的两只按列表顺序
        Assert.Equal(new[] { 0, 2 }, outside.Posted.ToArray());

        Kill(arena, 1);
        run.Update(10, 0, 0, false, false);                     // 五参数的重载
        Assert.Equal(new[] { 0, 2, 1 }, outside.Posted.ToArray());
        Assert.Equal(3, run.Reported);

        using var arena2 = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var outside2 = new Outside();
        HostedRoomRun run2 = Host(arena2, outside2);
        Kill(arena2, 1);
        run2.Update(10, 0, 0, false, false, SkillKeys.None);    // 六参数的重载
        Assert.Equal(new[] { 1 }, outside2.Posted.ToArray());
    }

    [Fact]
    public void A_kill_the_outside_refuses_is_dropped_and_not_offered_again()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var outside = new Outside { Accept = false };
        HostedRoomRun run = Host(arena, outside);

        Kill(arena, 0);
        run.Update(10, 0, 0);
        run.Update(10, 0, 0);
        outside.Accept = true;                                  // 外面后来又肯收了,也不会再收到这一只(观察者每只怪只交一次)
        run.Update(10, 0, 0);

        Assert.Equal(new[] { 0 }, outside.Posted.ToArray());
        Assert.Equal((0, 1), (run.Reported, run.Dropped));
    }

    [Fact]
    public void Killing_every_local_monster_does_not_by_itself_return_to_town()
    {
        // 本地怪杀光不等于可以回城:清图要等服务端,回不回由"外面说结束"决定
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var outside = new Outside();
        HostedRoomRun run = Host(arena, outside);

        for (int i = 0; i < 3; i++)
        {
            Kill(arena, i);
        }
        for (int i = 0; i < 50; i++)
        {
            run.Update(10, 0, 0);
        }

        Assert.Equal(3, run.Reported);
        Assert.Equal(0, arena.Session.AliveCount);
        Assert.Equal(SettlementChoice.None, run.Choice);
        Assert.False(run.Finished);
    }

    [Fact]
    public void Once_the_outside_says_finished_the_choice_becomes_return_to_town_on_that_step()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var outside = new Outside();
        HostedRoomRun run = Host(arena, outside);
        run.Update(10, 0, 0);
        Assert.Equal(SettlementChoice.None, run.Choice);

        outside.Finished = true;
        run.Update(10, 0, 0);

        Assert.True(run.Finished);
        Assert.Equal(SettlementChoice.ReturnToTown, run.Choice);
        Assert.False(run.Failed);
    }

    [Fact]
    public void The_return_waits_for_the_delay_counted_from_the_step_that_saw_the_end()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var outside = new Outside();
        HostedRoomRun run = Host(arena, outside, returnDelayMs: 300);

        run.Update(100, 0, 0);
        run.Update(100, 0, 0);
        Assert.Equal(SettlementChoice.None, run.Choice);        // 还没结束:时间照走也不回

        outside.Finished = true;
        run.Update(100, 0, 0);                                  // 看到结束的这一步算第一个 100 毫秒
        run.Update(100, 0, 0);
        Assert.True(run.Finished);
        Assert.Equal(SettlementChoice.None, run.Choice);        // 一共 200 毫秒,不到 300

        run.Update(100, 0, 0);
        Assert.Equal(SettlementChoice.ReturnToTown, run.Choice);
    }

    [Fact]
    public void The_end_is_remembered_even_if_the_outside_stops_saying_it()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var outside = new Outside();
        HostedRoomRun run = Host(arena, outside, returnDelayMs: 200);

        outside.Finished = true;
        run.Update(100, 0, 0);
        outside.Finished = false;
        run.Update(100, 0, 0);

        Assert.Equal(SettlementChoice.ReturnToTown, run.Choice);
    }

    [Fact]
    public void A_negative_delay_counts_as_zero_and_the_choice_never_goes_back()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var outside = new Outside { Finished = true };
        HostedRoomRun run = Host(arena, outside, returnDelayMs: -500);

        run.Update(10, 0, 0);
        Assert.Equal(SettlementChoice.ReturnToTown, run.Choice);
        int checks = outside.FinishedChecks;

        outside.Finished = false;
        run.Update(10, 0, 0);
        run.Update(10, 0, 0);

        Assert.Equal(SettlementChoice.ReturnToTown, run.Choice);
        Assert.Equal(checks, outside.FinishedChecks);           // 选定之后不再问
    }

    [Fact]
    public void The_last_kill_of_a_step_is_posted_before_that_step_asks_whether_the_run_is_finished()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 100);
        var posted = new List<int>();
        int postedWhenAsked = -1;
        var run = new HostedRoomRun(arena.Session, id => { posted.Add(id); return true; }, () =>
        {
            postedWhenAsked = posted.Count;
            return true;
        });

        Kill(arena, 0);
        run.Update(10, 0, 0);

        Assert.Equal(1, postedWhenAsked);                       // 最后一只怪的击杀排在"结束"的判断之前
        Assert.Equal(SettlementChoice.ReturnToTown, run.Choice);
    }

    [Fact]
    public void Everything_else_is_the_rooms_own()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        RoomCombatSession room = arena.Session;
        HostedRoomRun run = Host(arena, new Outside());

        Assert.Same(room, run.Room);
        Assert.Equal(room.MapPath, run.MapPath);
        Assert.Same(room.Hero, run.Hero);
        Assert.Same(room.Camera, run.Camera);
        Assert.Equal(room.LayerCount, run.LayerCount);
        Assert.Equal(room.NpcCount, run.NpcCount);
        Assert.Equal(3, run.NpcCount);
        Assert.NotEmpty(run.Build(0));

        run.SkillsHeld = SkillKeys.A | SkillKeys.D;
        Assert.Equal(SkillKeys.A | SkillKeys.D, room.SkillsHeld);
        Assert.Equal(SkillKeys.A | SkillKeys.D, run.SkillsHeld);
        run.CommandsEnabled = true;
        Assert.True(room.CommandsEnabled);
        run.CommandsEnabled = false;
        Assert.False(room.CommandsEnabled);

        Assert.Equal(room.TryUseQuickSlot(0), run.TryUseQuickSlot(0));
        var mine = new List<SoundCue>();
        var theirs = new List<SoundCue>();
        run.DrainSounds(mine);                                  // 经托管取声音,取的就是房间自己的队列
        room.DrainSounds(theirs);
        Assert.NotEmpty(mine);                                  // 迷你怪物出场的叫声提示
        Assert.Empty(theirs);                                   // 已经被托管取走了
    }

    [Fact]
    public void Updating_the_run_moves_the_hero_in_the_room()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 100);
        HostedRoomRun run = Host(arena, new Outside());
        double before = run.Hero.X;

        for (int i = 0; i < 20; i++)
        {
            run.Update(10, 1, 0);
        }

        Assert.True(run.Hero.X > before, "朝右走了,主角的 x 应当变大");
    }

    [Fact]
    public void Failed_follows_the_rooms_revive_window_and_is_false_without_one()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        HostedRoomRun run = Host(arena, new Outside());
        Assert.Null(arena.Session.Revive);
        Assert.False(run.Failed);

        var revive = new HeroRevive(null, new ReviveCoins(0));
        arena.Session.Revive = revive;
        Assert.False(run.Failed);
        revive.Begin();
        revive.Tick(HeroRevive.CountdownMs);

        Assert.True(revive.Failed);
        Assert.True(run.Failed);
    }

    [Fact]
    public void The_flow_returns_to_town_through_its_own_poll_when_the_run_says_return_to_town()
    {
        // 流程一字没改:GameFlow.PollDungeon 看到 Choice 就回城镇,计一次结算
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        var outside = new Outside();
        HostedRoomRun run = Host(arena, outside);
        var towns = new List<GameFlowTests.FakeHero>();
        var flow = new GameFlow(
            () =>
            {
                var town = new GameFlowTests.FakeHero("town #" + towns.Count);
                towns.Add(town);
                return town;
            },
            (path, n) => run,
            new[] { new DungeonEntry("dungeon/test/net.dgn", "NET") });
        flow.EnterTown("start");
        flow.StartDungeon("dungeon/test/net.dgn", "net room");
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Same(run, flow.Run);

        run.Update(10, 0, 0);
        Assert.False(flow.Poll());
        Assert.Equal(GameStage.Dungeon, flow.Stage);

        outside.Finished = true;
        run.Update(10, 0, 0);
        Assert.True(flow.Poll());

        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal(1, flow.Settlements);
        Assert.Equal(0, flow.Failures);
        Assert.Equal(2, towns.Count);                           // 回城造了新的城镇会话
        Assert.Same(towns[1], flow.Session);
    }

    [Fact]
    public void The_constructor_refuses_missing_pieces()
    {
        using var arena = new RoomCombatTests.Arena(ThreeInARow, hp: 100);
        Assert.Throws<ArgumentNullException>(() => new HostedRoomRun(null!, id => true, () => false));
        Assert.Throws<ArgumentNullException>(() => new HostedRoomRun(arena.Session, null!, () => false));
        Assert.Throws<ArgumentNullException>(() => new HostedRoomRun(arena.Session, id => true, null!));
    }
}
