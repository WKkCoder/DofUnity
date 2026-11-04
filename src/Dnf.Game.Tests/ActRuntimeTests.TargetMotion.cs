using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// 怪物出招里朝目标的转向与移动(M70,证据 E-169):[CHANGE DIRECTION BY TARGET](行为码 0xab)与 [IS DIRECTION TO MOVE](0xc5)是拥有者身上的两个朝向开关,
/// [SET SAVED TARGET](0xf5)把存下的目标软设成 AI 目标,[CHANGE MY TARGET](0xda)把集合里的对象指定成 AI 目标,
/// [MOVE TO TARGET](0xf6)按"角度(整数度)× 速度"给对象一个平面速度并排上一个"距离 ÷ 速度"的计时,计时到了清零平面速度。脚本内容全是合成的。
/// </summary>
public sealed partial class ActRuntimeTests
{
    /// <summary>读得回速度、记得下 AI 开关与目标的对象(缺省的 <see cref="Entity"/> 只把速度写进日志)。</summary>
    private sealed class Mover : Entity, IActEntity
    {
        public float Vx { get; set; }
        public float Vy { get; set; }
        public float Vz { get; set; }
        public bool? FollowsTarget { get; private set; }
        public bool? AttackFaces { get; private set; }
        public List<(IActEntity target, bool designated)> AiTargets { get; } = new();

        float IActEntity.SpeedX => Vx;

        float IActEntity.SpeedY => Vy;

        float IActEntity.SpeedZ => Vz;

        void IActEntity.SetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz)
        {
            if (hasX)
            {
                Vx = vx;
            }
            if (hasY)
            {
                Vy = vy;
            }
            if (hasZ)
            {
                Vz = vz;
            }
        }

        void IActEntity.SetFacingFollowsTarget(bool on) => FollowsTarget = on;

        void IActEntity.SetAttackFacing(bool on) => AttackFaces = on;

        void IActEntity.SetAiTarget(IActEntity target, bool designated) => AiTargets.Add((target, designated));
    }

    private static ActProgram MoveProgram(int speed) => P(Me(L("[MOVE TO TARGET]", speed)));

    // ------------------------------------------------------------------ 编译

    [Fact]
    public void TheTargetFacingTagsCompileToTheirOwnKindsAndFlags()
    {
        ActProgram p = P(Behavior(
            L("[CHANGE DIRECTION BY TARGET]", 0), L("[CHANGE DIRECTION BY TARGET]", 1), L("[CHANGE DIRECTION BY TARGET]", 5),
            L("[IS DIRECTION TO MOVE]", 1), L("[IS DIRECTION TO MOVE]", 0)));
        ActBehaviorGroup group = Assert.Single(p.Behaviors);
        Assert.Equal(new[]
        {
            ActBehaviorKind.SetFacingByTarget, ActBehaviorKind.SetFacingByTarget, ActBehaviorKind.SetFacingByTarget,
            ActBehaviorKind.SetFacingInAttack, ActBehaviorKind.SetFacingInAttack,
        }, group.Items.Select(i => i.Kind).ToArray());
        Assert.Equal(new[] { false, true, true, true, false }, group.Items.Select(i => i.FlagOn).ToArray());      // 只看是不是 0
        Assert.Empty(group.UnsupportedTags);
    }

    [Fact]
    public void TheTargetFacingTagsWithoutAValueAreLeftUndoneAndRecorded()
    {
        ActProgram a = P(Behavior(L("[CHANGE DIRECTION BY TARGET]")));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(a).Kind);
        Assert.Contains("[CHANGE DIRECTION BY TARGET]", a.Behaviors[0].UnsupportedTags);

        ActProgram b = P(Behavior(L("[IS DIRECTION TO MOVE]")));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(b).Kind);
        Assert.Contains("[IS DIRECTION TO MOVE]", b.Behaviors[0].UnsupportedTags);
    }

    [Fact]
    public void SavedTargetAndChangeMyTargetCompile()
    {
        ActProgram p = P(Behavior(L("[SET SAVED TARGET]", 3), L("[CHANGE MY TARGET]"), L("[SET SAVED TARGET]", 0)));
        ActBehaviorGroup group = Assert.Single(p.Behaviors);
        Assert.Equal(new[] { ActBehaviorKind.UseSavedTarget, ActBehaviorKind.ChangeMyTarget, ActBehaviorKind.UseSavedTarget },
            group.Items.Select(i => i.Kind).ToArray());
        Assert.Equal(3, group.Items[0].TargetSlot);
        Assert.Equal(0, group.Items[2].TargetSlot);
        Assert.Empty(group.UnsupportedTags);

        ActProgram bare = P(Behavior(L("[SET SAVED TARGET]")));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(bare).Kind);
        Assert.Contains("[SET SAVED TARGET]", bare.Behaviors[0].UnsupportedTags);
    }

    [Fact]
    public void MoveToTargetCompilesWithItsSpeed()
    {
        ActProgram p = P(Behavior(L("[MOVE TO TARGET]", 500)));
        ActBehavior b = OnlyBehavior(p);
        Assert.Equal(ActBehaviorKind.MoveToTarget, b.Kind);
        Assert.Equal(500, b.MoveSpeed);
        Assert.Empty(p.Behaviors[0].UnsupportedTags);

        ActProgram bare = P(Behavior(L("[MOVE TO TARGET]")));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(bare).Kind);
        Assert.Contains("[MOVE TO TARGET]", bare.Behaviors[0].UnsupportedTags);
    }

    [Fact]
    public void TheTargetMotionTagsSitBesideTheirNeighboursInTheSameGroup()
    {
        ActProgram p = P(Behavior(Cat(Speed(5), new[] { L("[MOVE TO TARGET]", 100), L("[CHANGE MY TARGET]") }, Speed(7))));
        ActBehaviorGroup group = Assert.Single(p.Behaviors);
        Assert.Equal(new[] { ActBehaviorKind.SetSpeed, ActBehaviorKind.MoveToTarget, ActBehaviorKind.ChangeMyTarget, ActBehaviorKind.SetSpeed },
            group.Items.Select(i => i.Kind).ToArray());
        Assert.Empty(group.UnsupportedTags);
    }

    // ------------------------------------------------------------------ 朝向开关

    [Fact]
    public void TheFacingSwitchesReachTheOwnerAndNoOneElse()
    {
        var owner = new Mover();
        var other = new Mover();
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { other };
        Run(P(OnEnemies(L("[CHANGE DIRECTION BY TARGET]", 0), L("[IS DIRECTION TO MOVE]", 1))), owner, host);
        Assert.False(owner.FollowsTarget);
        Assert.True(owner.AttackFaces);
        Assert.Null(other.FollowsTarget);
        Assert.Null(other.AttackFaces);

        var me = new Mover();
        Run(P(Me(L("[CHANGE DIRECTION BY TARGET]", 1), L("[IS DIRECTION TO MOVE]", 0))), me);
        Assert.True(me.FollowsTarget);
        Assert.False(me.AttackFaces);
    }

    // ------------------------------------------------------------------ AI 目标

    [Fact]
    public void ASavedTargetSlotBecomesTheOwnersSoftAiTarget()
    {
        var hero = new Mover();
        var m = new Mover();
        m.SavedTargets[2] = hero;
        Run(P(Me(L("[SET SAVED TARGET]", 2))), m);
        (IActEntity target, bool designated) set = Assert.Single(m.AiTargets);
        Assert.Same(hero, set.target);
        Assert.False(set.designated);
    }

    [Fact]
    public void AnEmptySavedTargetSlotChangesNothing()
    {
        var hero = new Mover();
        var m = new Mover();
        m.SavedTargets[2] = hero;
        Run(P(Me(L("[SET SAVED TARGET]", 5))), m);
        Assert.Empty(m.AiTargets);
    }

    [Fact]
    public void ChangeMyTargetDesignatesEveryObjectOfTheSetAndTheLastOneWins()
    {
        var owner = new Mover();
        var a = new Mover();
        var b = new Mover();
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b };
        Run(P(OnEnemies(L("[CHANGE MY TARGET]"))), owner, host);
        Assert.Equal(new IActEntity[] { a, b }, owner.AiTargets.Select(t => t.target).ToArray());
        Assert.All(owner.AiTargets, t => Assert.True(t.designated));
        Assert.Empty(a.AiTargets);          // 改的是拥有者的目标,不是被选中对象的
        Assert.Empty(b.AiTargets);
    }

    [Fact]
    public void ChangeMyTargetOnAnEmptySetDoesNothing()
    {
        // [CHECKED NO] [=] 0 让空的工作集也能触发;同组里的朝向开关落在拥有者身上,证明这一组确实跑了
        var owner = new Mover();
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity>();
        new ActRunner(P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[CHECKED NO]"), L("[=]", 0), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[CHANGE MY TARGET]"), L("[CHANGE DIRECTION BY TARGET]", 0)))), owner, host).Update(0, 0.016f);
        Assert.False(owner.FollowsTarget);
        Assert.Empty(owner.AiTargets);
    }

    // ------------------------------------------------------------------ 朝目标冲

    [Fact]
    public void MoveToTargetPushesTheOwnerTowardTheTargetAndZeroesItAtTheTimer()
    {
        var hero = new Mover { X = 300, Y = 0 };
        var m = new Mover { X = 0, Y = 0, Target = hero };
        var host = new Host();
        var runner = new ActRunner(MoveProgram(600), m, host);

        runner.Update(0, 0f);
        Assert.Equal((600f, 0f, 0f), (m.Vx, m.Vy, m.Vz));
        (IActEntity mover, int speed, int ms) started = Assert.Single(host.MovesStarted);
        Assert.Same(m, started.mover);
        Assert.Equal((600, 500), (started.speed, started.ms));          // 距离 300 ÷ 600 × 1000

        runner.Update(0, 0.49f);                                         // 490 毫秒:还没到
        Assert.Equal(600f, m.Vx);
        Assert.Empty(host.MovesStopped);

        runner.Update(0, 0.02f);                                         // 510 毫秒:到点,平面速度清零
        Assert.Equal((0f, 0f), (m.Vx, m.Vy));
        Assert.Same(m, Assert.Single(host.MovesStopped));
    }

    [Fact]
    public void TheStopZeroesBothPlanarComponentsOfADiagonalPush()
    {
        var hero = new Mover { X = 3, Y = 4 };
        var m = new Mover { Target = hero };
        var host = new Host();
        var runner = new ActRunner(MoveProgram(600), m, host);
        runner.Update(0, 0f);
        Assert.True(m.Vx > 0f && m.Vy > 0f, "a diagonal push: " + m.Vx + "," + m.Vy);
        Assert.Equal(21, host.MovesStarted[0].ms);                       // 距离 13(纵深还原后)÷ 600 × 1000 = 21.67 向零截断
        runner.Update(0, 0.03f);
        Assert.Equal((0f, 0f), (m.Vx, m.Vy));
        Assert.Same(m, Assert.Single(host.MovesStopped));
    }

    [Fact]
    public void MoveToTargetStopsOnTheExactDueTime()
    {
        var hero = new Mover { X = 300 };
        var m = new Mover { Target = hero };
        var runner = new ActRunner(MoveProgram(600), m, new Host());
        runner.Update(0, 0f);
        runner.Update(0, 0.5f);                                          // 正好 500 毫秒:到点就停(≥,不是 >)
        Assert.Equal(0f, m.Vx);
    }

    [Fact]
    public void MoveToTargetStopsOnlyOnce()
    {
        var hero = new Mover { X = 300 };
        var m = new Mover { Target = hero };
        var host = new Host();
        var runner = new ActRunner(MoveProgram(600), m, host);
        runner.Update(0, 0f);
        runner.Update(0, 1f);
        runner.Update(0, 1f);
        Assert.Single(host.MovesStarted);
        Assert.Single(host.MovesStopped);
    }

    [Fact]
    public void MoveToTargetKeepsTheClientsDepthQuirk()
    {
        // 目标只在纵深上差 29:速度向量按未还原的差(朝画面下方)走,计时里的距离却把纵深还原成 100 → 冲过头(客户端的写法)
        var hero = new Mover { X = 0, Y = 29 };
        var m = new Mover { Target = hero };
        var host = new Host();
        new ActRunner(MoveProgram(600), m, host).Update(0, 0f);
        Assert.Equal((0f, 600f), (m.Vx, m.Vy));
        Assert.Equal(166, host.MovesStarted[0].ms);                      // 100 ÷ 600 × 1000 = 166.67 向零截断
    }

    [Fact]
    public void MoveToTargetOnADiagonalTruncatesEachComponent()
    {
        var hero = new Mover { X = 100, Y = 100 };
        var m = new Mover { Target = hero };
        var host = new Host();
        new ActRunner(MoveProgram(100), m, host).Update(0, 0f);
        Assert.Equal((70f, 70f), (m.Vx, m.Vy));
        Assert.Equal(3580, host.MovesStarted[0].ms);                     // 距离(纵深还原后)358 ÷ 100 × 1000
    }

    [Fact]
    public void MoveToTargetTruncatesPositionsToWholeNumbersFirst()
    {
        var hero = new Mover { X = 300.9f, Y = 0.9f };
        var m = new Mover { X = 0.9f, Y = 0.9f, Target = hero };
        var host = new Host();
        new ActRunner(MoveProgram(600), m, host).Update(0, 0f);
        Assert.Equal((600f, 0f), (m.Vx, m.Vy));                          // 0.9 → 0、300.9 → 300:正右方
        Assert.Equal(500, host.MovesStarted[0].ms);
    }

    [Fact]
    public void MoveToTargetDoesNotTouchHeight()
    {
        var hero = new Mover { X = 300 };
        var m = new Mover { Target = hero, Vz = 7f };
        new ActRunner(MoveProgram(600), m, new Host()).Update(0, 0f);
        Assert.Equal(7f, m.Vz);
    }

    [Fact]
    public void MoveToTargetWithoutALiveTargetDoesNothing()
    {
        var none = new Mover { X = 0 };
        var hostA = new Host();
        new ActRunner(MoveProgram(600), none, hostA).Update(0, 0f);
        Assert.Equal(0f, none.Vx);
        Assert.Empty(hostA.MovesStarted);

        var dead = new Mover { X = 300, IsAlive = false };
        var gone = new Mover { Target = dead };
        var hostB = new Host();
        new ActRunner(MoveProgram(600), gone, hostB).Update(0, 0f);
        Assert.Equal(0f, gone.Vx);
        Assert.Empty(hostB.MovesStarted);
    }

    [Fact]
    public void MoveToTargetNeedsAPositiveSpeed()
    {
        var hero = new Mover { X = 300 };
        var m = new Mover { Target = hero };
        var host = new Host();
        new ActRunner(MoveProgram(0), m, host).Update(0, 0f);
        Assert.Empty(host.MovesStarted);
        var n = new Mover { Target = hero };
        new ActRunner(MoveProgram(-50), n, host).Update(0, 0f);
        Assert.Empty(host.MovesStarted);
        Assert.Equal((0f, 0f), (m.Vx, n.Vx));
    }

    [Fact]
    public void MoveToTargetPushesEveryObjectOfTheSetAndTheLastTimerStays()
    {
        var hero = new Mover { X = 100 };
        var owner = new Mover { Target = hero };
        var a = new Mover { X = 0 };                // 距离 100,往右
        var b = new Mover { X = 400 };              // 距离 300,往左
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b };
        var runner = new ActRunner(P(OnEnemies(L("[MOVE TO TARGET]", 100))), owner, host);

        runner.Update(0, 0f);
        Assert.Equal((100f, 0f), (a.Vx, a.Vy));
        Assert.Equal((-100f, 0f), (b.Vx, b.Vy));
        Assert.Equal(0f, owner.Vx);
        Assert.Equal(new[] { 1000, 3000 }, host.MovesStarted.Select(s => s.ms).ToArray());

        runner.Update(0, 1.5f);                     // 1500 毫秒:留下的是最后一个的 3000 毫秒,还没到
        Assert.Equal(100f, a.Vx);
        Assert.Equal(-100f, b.Vx);
        runner.Update(0, 1.6f);                     // 3100 毫秒:两个一起清零
        Assert.Equal((0f, 0f), (a.Vx, b.Vx));
        Assert.Equal(new IActEntity[] { a, b }, host.MovesStopped.ToArray());
    }

    [Fact]
    public void ASecondPushRestartsTheTimerAndForgetsTheFirstPush()
    {
        var hero = new Mover { X = 300 };
        var m = new Mover { Target = hero };
        var other = new Mover { X = 0 };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { other };
        // 第 0 帧推拥有者自己(500 毫秒),第 1 帧推集合里的 other(500 毫秒;从 X=0 去 300)
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 1, 1), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[MOVE TO TARGET]", 600))));
        var runner = new ActRunner(p, m, host);

        runner.Update(0, 0f);
        Assert.Equal(600f, m.Vx);
        runner.Update(1, 0.4f);                     // 400 毫秒:第二次推(other),重启计时:到期时刻 = 400 + 500
        Assert.Equal(600f, other.Vx);
        runner.Update(1, 0.2f);                     // 600 毫秒:超过第一次的到期(500),但计时已被第二次顶掉
        Assert.Equal(600f, m.Vx);
        Assert.Equal(600f, other.Vx);
        Assert.Empty(host.MovesStopped);
        runner.Update(1, 0.31f);                    // 910 毫秒:到点;只有第二次推的对象被清零,第一次的登记已被忘掉
        Assert.Equal(0f, other.Vx);
        Assert.Equal(600f, m.Vx);
        Assert.Same(other, Assert.Single(host.MovesStopped));
    }

    [Fact]
    public void TheStopLeavesAnObjectWhoseSpeedWasChangedInTheMeantimeAlone()
    {
        var hero = new Mover { X = 300 };
        var m = new Mover { Target = hero };
        var host = new Host();
        var runner = new ActRunner(MoveProgram(600), m, host);
        runner.Update(0, 0f);
        ((IActEntity)m).SetSpeed(true, 5f, true, 0f, false, 0f);        // 别的行为后来改了速度
        runner.Update(0, 1f);
        Assert.Equal(5f, m.Vx);
        Assert.Empty(host.MovesStopped);
    }

    [Fact]
    public void TheStopSkipsAnObjectThatDiedInTheMeantime()
    {
        var hero = new Mover { X = 300 };
        var m = new Mover { Target = hero };
        var host = new Host();
        var runner = new ActRunner(MoveProgram(600), m, host);
        runner.Update(0, 0f);
        m.IsAlive = false;
        runner.Update(0, 1f);
        Assert.Equal(600f, m.Vx);
        Assert.Empty(host.MovesStopped);
    }

    [Fact]
    public void TheMoveTimerDoesNotSurviveARestartOfTheAction()
    {
        // 触发器在第 3 帧(帧号跳着走时运行器会补跑被跳过的帧,所以重来之后只走到第 2 帧,补跑的 0 到 2 帧都没有触发器)
        var hero = new Mover { X = 300 };
        var m = new Mover { Target = hero };
        var host = new Host();
        var runner = new ActRunner(P(Cat(
            Trigger(L("[FRAME]", 3, 3), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[MOVE TO TARGET]", 600)))), m, host);
        runner.Update(3, 0f);
        Assert.Equal(600f, m.Vx);
        runner.Begin();                             // 动作重来:计时作废,没有人再来清零(速度的重置属于新动作的开始,不在这里)
        runner.Update(2, 5f);
        Assert.Equal(600f, m.Vx);
        Assert.Single(host.MovesStarted);           // 没有重新推
        Assert.Empty(host.MovesStopped);
    }

    [Fact]
    public void TheMoveStopRunsAfterTheTriggersOfTheSameUpdate()
    {
        // 到点的那一次更新里,同一次更新里的触发器先跑:这里第 1 帧的触发器在到点时把速度改成 5,于是收尾不再碰它
        var hero = new Mover { X = 300 };
        var m = new Mover { Target = hero };
        var host = new Host();
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 1, 1), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(L("[MOVE TO TARGET]", 600)),
            Behavior(Speed(5))));
        var runner = new ActRunner(p, m, host);
        runner.Update(0, 0f);
        runner.Update(1, 0.6f);
        Assert.Equal(5f, m.Vx);
        Assert.Empty(host.MovesStopped);
    }
}
