using Dnf.Game;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 被打中之后叫同伴的整条链路(房间会话里主角的一次命中 → 怪物大脑的靠近与请求 → 会话按副本难度叫同伴;规则与单元见 <see cref="MonsterAlertTests"/>)。
/// 数全是合成的:位置、视野、随机数都是测试自己定的,与原版数据无关。
/// </summary>
public class MonsterAlertSessionTests
{
    private sealed class Dial
    {
        public int Value;

        public Dial(int value)
        {
            Value = value;
        }

        public Func<int> AsFunc() => () => Value;
    }

    private static MonsterBrain NewBrain(Func<int> random, int sight = 200)
    {
        var traits = new MonsterBrainTraits(true, false, false, false, false, false, false, 40);
        return new MonsterBrain(sight, 50, traits, 2000, false, random);
    }

    private static MonsterMoveMode ModeOf(MonsterActor m) => m.Brain.Mode;

    /// <summary>
    /// 竞技场里主角站在 (300, 100) 朝右,第 0 号怪物在 (340, 100)(普通一击打得到),其余的在它身后 140 到 240 像素处。
    /// 给 0 号怪物的大脑塞一个远处的主角位置(100, 100)再把它定住,它自己的思考就不会把位置刷新回真实的近处:这一击在大脑看来是"远处打来的"。
    /// </summary>
    private static (RoomCombatTests.Arena Arena, MonsterActor Caller) FarStrikeArena(int allies, int difficulty, int callerRoll = 10)
    {
        var positions = new List<(int X, int Y)> { (340, 100) };
        for (int i = 0; i < allies; i++)
        {
            positions.Add((480 + i * 20, 100));
        }
        var arena = new RoomCombatTests.Arena(positions, frozen: true, expDifficulty: difficulty);
        MonsterActor caller = arena.Session.Monsters[0];
        caller.SetDefaultAi(NewBrain(new Dial(callerRoll).AsFunc()));
        for (int i = 1; i < arena.Session.Monsters.Count; i++)
        {
            arena.Session.Monsters[i].SetDefaultAi(NewBrain(new Dial(99).AsFunc()));
        }
        caller.Brain.Update(0, true, caller.X, caller.Y, 100, 100);
        caller.Pause(1e9);
        return (arena, caller);
    }

    private static void SwingUntilFirstHit(RoomCombatTests.Arena arena)
    {
        RoomCombatSession s = arena.Session;
        s.Update(10, 0, 0, true);
        for (int i = 0; i < 200 && s.Hits < 1; i++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.Equal(1, s.Hits);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 4)]
    [InlineData(4, 5)]
    public void AFarHitInTheSessionCallsAlliesByTheSessionsDifficultyInRoomOrder(int difficulty, int expected)
    {
        var (arena, caller) = FarStrikeArena(6, difficulty);
        using (arena)
        {
            SwingUntilFirstHit(arena);
            Assert.Equal(MonsterMoveMode.Approach, ModeOf(caller));
            for (int i = 1; i <= 6; i++)
            {
                Assert.Equal(i <= expected ? MonsterMoveMode.Approach : MonsterMoveMode.Wander, ModeOf(arena.Session.Monsters[i]));
            }
        }
    }

    [Fact]
    public void AFarHitInTheSessionWithAFailedRollCallsNobody()
    {
        var (arena, caller) = FarStrikeArena(3, 4, callerRoll: 70);
        using (arena)
        {
            SwingUntilFirstHit(arena);
            Assert.Equal(MonsterMoveMode.Approach, ModeOf(caller));         // 自己照靠近
            for (int i = 1; i <= 3; i++)
            {
                Assert.Equal(MonsterMoveMode.Wander, ModeOf(arena.Session.Monsters[i]));
            }
        }
    }

    [Fact]
    public void AHitFromCloseByInTheSessionCallsNobody()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100), (480, 100), (500, 100) }, frozen: true, expDifficulty: 4);
        foreach (MonsterActor m in arena.Session.Monsters)
        {
            m.SetDefaultAi(NewBrain(new Dial(0).AsFunc()));
        }
        SwingUntilFirstHit(arena);                                          // 主角离 0 号怪物只有 40,不算远处打来的
        foreach (MonsterActor m in arena.Session.Monsters)
        {
            Assert.NotEqual(MonsterMoveMode.Approach, ModeOf(m));
        }
    }
}
