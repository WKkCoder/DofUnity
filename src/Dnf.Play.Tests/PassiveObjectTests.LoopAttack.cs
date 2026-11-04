using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 出招动画带循环属性的怪物出招(M70b,证据 E-170),带着真的 .act 程序在迷你房间里跑:
/// 脚本里有收尾的 [SET ACTION] 时,出招不在动画放完一遍时结束,等脚本收尾;脚本里没有收尾的写法(或根本没有程序)、
/// 或脚本用 [END] 排好了切招时,仍按一遍算,不让怪物一直挂在出招里;绕回开头的新一遍里攻击框重新判。
/// 怪物与 .act 都是合成的(循环动画一遍 200 毫秒,带攻击框的一遍 320 毫秒)。
/// </summary>
public partial class PassiveObjectTests
{
    /// <summary>怪物进了出招阶段之后,到离开出招阶段(或 <paramref name="max"/> 步用完返回 -1)用了几步(每步 10 毫秒)。</summary>
    private static int AttackSteps(ActArena arena, int max = 400)
    {
        Assert.True(arena.Until(() => arena.Monster.Phase == MonsterPhase.Attack) > 0);
        return arena.Until(() => arena.Monster.Phase != MonsterPhase.Attack, max);
    }

    [Fact]
    public void ALoopingAttackWithAScriptedEndKeepsPlayingUntilTheScriptEndsIt()
    {
        using var arena = new ActArena("xloopend", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        int wraps = 0;
        int last = m.CurrentFrame;
        int steps = 0;
        while (m.Phase == MonsterPhase.Attack && steps < 200)
        {
            arena.Steps(1);
            steps++;
            if (m.Phase == MonsterPhase.Attack && m.CurrentFrame < last)
            {
                wraps++;
            }
            last = m.CurrentFrame;
            if (steps == 30)
            {
                Assert.Equal(MonsterPhase.Attack, m.Phase);          // 一遍 200 毫秒早过了,还在出招
            }
        }
        Assert.InRange(steps, 49, 52);                               // 脚本在 500 毫秒回待机
        Assert.Equal(MonsterPhase.Wait, m.Phase);
        Assert.InRange(wraps, 2, 3);                                 // 动画绕了两三遍
    }

    [Fact]
    public void ALoopingAttackWhoseScriptHasNoEndStillEndsAfterOnePass()
    {
        using var arena = new ActArena("xloopnoend", useVitals: false);
        Assert.True(arena.Monster.HasActs);
        Assert.InRange(AttackSteps(arena), 18, 22);
    }

    [Fact]
    public void ALoopingAttackWithoutAScriptEndsAfterOnePass()
    {
        using var arena = new ActArena("xloopplain", useVitals: false);
        Assert.False(arena.Monster.HasActs);
        Assert.InRange(AttackSteps(arena), 18, 22);
    }

    [Fact]
    public void AQueuedSwitchEndsALoopingAttackAfterOnePass()
    {
        using var arena = new ActArena("xloopqueue", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.Equal(0, m.AttackIndex);
        int steps = arena.Until(() => m.AttackIndex == 1);
        Assert.InRange(steps, 18, 22);                               // 排好的切招等这一遍播完才接,不会等到天荒地老
        Assert.Equal(MonsterPhase.Attack, m.Phase);
    }

    [Fact]
    public void AQueuedCustomActionEndsALoopingAttackAfterOnePassToo()
    {
        using var arena = new ActArena("f_loopqcust", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.False(m.InCustomAction);
        Assert.Equal(0, arena.Session.PassivesSpawned);
        int steps = arena.Until(() => m.InCustomAction);
        Assert.InRange(steps, 18, 24);                               // 排好的自定义动作也等这一遍播完才接
        arena.Steps(2);
        Assert.Equal(1, arena.Session.PassivesSpawned);              // 自定义动作 0 一进来放一个对象
    }

    [Fact]
    public void ASwingThatMissedOnTheFirstPassOfALoopingAttackCanHitOnALaterPass()
    {
        // 主角站在攻击框够不着的地方(攻击框在怪物面前 10 到 50 像素,这里离 90 像素),第一遍(0 到 320 毫秒)挥空,
        // 然后主角走进来:新的一遍里第 0 帧的攻击框重新判(包括这一遍的第 0 帧),打中。绕回开头不重判的话,后面的几遍永远不会打到人
        using var arena = new ActArena("xloophit", ranged: true, heroX: 250, hp: 1000);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack, 800) > 0);
        RoomCombatSession s = arena.Session;
        int before = s.Vitals.Hp;
        int attackStep = 0;
        arena.Steps(32);
        attackStep += 32;
        Assert.Equal(before, s.Vitals.Hp);                           // 第一遍挥空
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        int hitStep = -1;
        for (int i = 0; i < 60 && m.Phase == MonsterPhase.Attack; i++)
        {
            bool inReach = s.Hero.X >= m.X - 40;
            s.Update(10, inReach ? 0 : 1, 0, false, false);
            attackStep++;
            if (s.Vitals.Hp < before)
            {
                hitStep = attackStep;
                break;
            }
        }
        Assert.True(hitStep > 32, "a later pass hits: " + hitStep);
        Assert.InRange(hitStep, 33, 100);
    }
}
