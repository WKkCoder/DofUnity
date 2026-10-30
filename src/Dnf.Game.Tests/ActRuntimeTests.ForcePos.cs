using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [SET POS FORCE](M58,证据 E-152):编译(记号按文件顺序留下、读不懂的写法整块记成不支持)与运行器的强制位移表
/// (每个目标一条记录,每次更新按目标与拥有者当时的位置重新算落点)。客户端执行块在 0x010bfa89,每帧消费者在 0x010a5042 起。脚本内容全是合成的。
/// </summary>
public sealed partial class ActRuntimeTests
{
    private static ScriptLine[] Force(params ScriptLine[] body)
    {
        var list = new List<ScriptLine> { L("[SET POS FORCE]") };
        list.AddRange(body);
        list.Add(L("[/SET POS FORCE]"));
        return list.ToArray();
    }

    /// <summary>拥有者 me 在 (100,50,0) 朝右,目标 hero 在 (300,80,10) 朝右;帧 <paramref name="frame"/> 上对 <paramref name="selector"/> 选中的对象执行这块位移。</summary>
    private static (Entity me, Entity hero, ActRunner runner) ForceSetup(ScriptLine[] force, string selector = "[TARGET]", int frame = 0, Host host = null)
    {
        var hero = new Entity { Kind = ActEntityKind.Character, X = 300, Y = 80, Z = 10 };
        var me = new Entity { X = 100, Y = 50, Z = 0, Target = hero };
        ActProgram p = P(Cat(Trigger(L("[FRAME]", frame, frame), L("[DO BEHAVIOR]"), L(selector, 0)), Behavior(force)));
        return (me, hero, new ActRunner(p, me, host ?? new Host()));
    }

    private static ScriptLine[] OnMapRight(params ScriptLine[] axes) => Cat(new[] { L("[USE MAP POS]"), L("[NOT USE OBJECT DIRECTION]") }, axes);

    // ------------------------------------------------------------------ 编译

    [Fact]
    public void ASetPosForceBlockKeepsItsTokensInFileOrder()
    {
        ActProgram p = P(Behavior(Force(L("[X START]", 5), L("[Y END]", 7), L("[USE MY BASEPOS]"), L("[Z START]", 2), L("[MOVE ME]"), L("[MOVE UNIFORM]"))));
        ActBehavior b = OnlyBehavior(p);
        Assert.Equal(ActBehaviorKind.SetPosForce, b.Kind);
        Assert.Equal(
            new[] { ActForceCode.XStart, ActForceCode.YEnd, ActForceCode.UseMyBasePos, ActForceCode.ZStart, ActForceCode.MoveMe, ActForceCode.MoveUniform },
            b.ForceTokens.Select(t => t.Code).ToArray());
        Assert.Equal(5, b.ForceTokens[0].Value.First);
        Assert.Equal(7, b.ForceTokens[1].Value.First);
        Assert.Null(b.ForceTokens[2].Value);
        Assert.Empty(p.Behaviors[0].UnsupportedTags);
    }

    [Fact]
    public void TheForceCodesFollowTheClientTokenNumbers()
    {
        // 码取自客户端装载函数 0x01340140 推进记号向量的数(E-152);代码里按名字用,这里钉住数值
        Assert.Equal(0, (int)ActForceCode.XStart);
        Assert.Equal(1, (int)ActForceCode.YStart);
        Assert.Equal(2, (int)ActForceCode.ZStart);
        Assert.Equal(3, (int)ActForceCode.XEnd);
        Assert.Equal(4, (int)ActForceCode.YEnd);
        Assert.Equal(5, (int)ActForceCode.ZEnd);
        Assert.Equal(7, (int)ActForceCode.UseMyDirection);
        Assert.Equal(8, (int)ActForceCode.NotUseObjectDirection);
        Assert.Equal(9, (int)ActForceCode.UseTargetPos);
        Assert.Equal(10, (int)ActForceCode.UseMyBasePos);
        Assert.Equal(11, (int)ActForceCode.UseMapPos);
        Assert.Equal(12, (int)ActForceCode.MoveUniform);
        Assert.Equal(13, (int)ActForceCode.MoveAccelate);
        Assert.Equal(14, (int)ActForceCode.MoveReduce);
        Assert.Equal(15, (int)ActForceCode.MoveTarget);
        Assert.Equal(16, (int)ActForceCode.MoveMe);
    }

    [Fact]
    public void ABlockWithAnUnreadableValueOrAnAcceleratedMoveIsLeftUndoneAndTheNextBehaviorStillParses()
    {
        // [LASTATTACK POSITION] 是客户端值读取函数的另一种写法,[MOVE ACCELATE] 的插值函数没读:整块不做,后面的行为照常读
        ActProgram last = P(Behavior(Cat(Force(L("[X START]"), L("[LASTATTACK POSITION]"), L("[MOVE ME]")), Speed(3))));
        ActBehaviorGroup group = Assert.Single(last.Behaviors);
        Assert.Equal(new[] { ActBehaviorKind.Unsupported, ActBehaviorKind.SetSpeed }, group.Items.Select(i => i.Kind).ToArray());
        Assert.Contains("[SET POS FORCE]", group.UnsupportedTags);

        ActProgram accel = P(Behavior(Cat(Force(L("[X START]", 1), L("[X END]", 9), L("[MOVE ACCELATE]")), Speed(3))));
        Assert.Equal(new[] { ActBehaviorKind.Unsupported, ActBehaviorKind.SetSpeed }, accel.Behaviors[0].Items.Select(i => i.Kind).ToArray());

        ActProgram noValue = P(Behavior(Force(L("[Y START]"), L("[MOVE ME]"))));
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(noValue).Kind);

        ActProgram stray = P(Behavior(Force(L("[LASTATTACK POSITION]"), L("[MOVE ME]"))));     // 它是值的写法;游离着出现也不当没事
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(stray).Kind);
    }

    // ------------------------------------------------------------------ 基准、朝向、被移动的对象

    [Fact]
    public void ByDefaultTheTargetIsMovedRelativeToItselfSoTheOffsetAddsUpEveryUpdate()
    {
        // 缺省:基准取目标自己的当前位置,朝向取目标的,被移动的是目标;每次更新都加一遍偏移(速度的效果),朝左时 X 取反
        var (me, hero, runner) = ForceSetup(Force(L("[X START]", 5), L("[Y START]", 1), L("[Z START]", 2)));
        runner.Update(0, 0.25f);
        runner.Update(0, 0.25f);
        Assert.Equal(new[] { (305f, 81f, 12f), (310f, 82f, 14f) }, hero.Forced.ToArray());
        Assert.Empty(me.Forced);

        var (_, left, leftRunner) = ForceSetup(Force(L("[X START]", 5), L("[Y START]", 1), L("[Z START]", 2)));
        left.FacingLeft = true;
        leftRunner.Update(0, 0.25f);
        leftRunner.Update(0, 0.25f);
        Assert.Equal(new[] { (295f, 81f, 12f), (290f, 82f, 14f) }, left.Forced.ToArray());
    }

    [Fact]
    public void TheOwnersBasePointAndDirectionPlaceTheTargetInFrontOfTheOwnerAndFollowTheOwner()
    {
        var (me, hero, runner) = ForceSetup(Force(L("[USE MY BASEPOS]"), L("[USE MY DIRECTION]"), L("[X START]", 60), L("[Y START]", 3), L("[Z START]", 20)));
        runner.Update(0, 0.25f);
        runner.Update(0, 0.25f);
        Assert.Equal(new[] { (160f, 53f, 20f), (160f, 53f, 20f) }, hero.Forced.ToArray());     // 基准是拥有者:不累加

        me.X = 200;
        runner.Update(0, 0.25f);
        Assert.Equal((260f, 53f, 20f), hero.Forced[2]);     // 拥有者动了,下一次更新跟着走

        me.FacingLeft = true;
        me.X = 100;
        runner.Update(0, 0.25f);
        Assert.Equal((40f, 53f, 20f), hero.Forced[3]);      // 朝向取拥有者的:朝左时 X 偏移取反,Y、Z 不取反
    }

    [Fact]
    public void MoveMeMovesTheOwnerOntoTheTargetsPosition()
    {
        var (me, hero, runner) = ForceSetup(Force(L("[MOVE ME]"), L("[X START]", 20), L("[Y START]", -5), L("[Z START]", 40)));
        runner.Update(0, 0.25f);
        Assert.Equal(new[] { (320f, 75f, 50f) }, me.Forced.ToArray());     // 基准缺省取目标:拥有者落在目标旁
        Assert.Empty(hero.Forced);
    }

    [Fact]
    public void MapPositionsAreAbsoluteAndOnlyTheXSignFollowsTheDirectionSource()
    {
        var (_, right, rightRunner) = ForceSetup(Force(L("[USE MAP POS]"), L("[X START]", 60), L("[Y START]", 5), L("[Z START]", 7)));
        rightRunner.Update(0, 0.25f);
        Assert.Equal((60f, 5f, 7f), right.Forced[0]);

        var (_, left, leftRunner) = ForceSetup(Force(L("[USE MAP POS]"), L("[X START]", 60), L("[Y START]", 5), L("[Z START]", 7)));
        left.FacingLeft = true;
        leftRunner.Update(0, 0.25f);
        Assert.Equal((-60f, 5f, 7f), left.Forced[0]);      // 朝向缺省取目标的

        var (_, fixedRight, fixedRunner) = ForceSetup(Force(L("[USE MAP POS]"), L("[NOT USE OBJECT DIRECTION]"), L("[X START]", 60), L("[Y START]", 5), L("[Z START]", 7)));
        fixedRight.FacingLeft = true;
        fixedRunner.Update(0, 0.25f);
        Assert.Equal((60f, 5f, 7f), fixedRight.Forced[0]);  // [NOT USE OBJECT DIRECTION]:恒当朝右

        var (ownerLeft, ownerLeftHero, ownerLeftRunner) = ForceSetup(Force(L("[USE MAP POS]"), L("[NOT USE OBJECT DIRECTION]"), L("[X START]", 60)));
        ownerLeft.FacingLeft = true;
        ownerLeftRunner.Update(0, 0.25f);
        Assert.Equal(60f, ownerLeftHero.Forced[0].x);       // 与双方的朝向都无关:拥有者朝左也是朝右

        var (mine, mineHero, mineRunner) = ForceSetup(Force(L("[USE MAP POS]"), L("[USE MY DIRECTION]"), L("[X START]", 60)));
        mine.FacingLeft = true;
        mineRunner.Update(0, 0.25f);
        Assert.Equal(-60f, mineHero.Forced[0].x);
    }

    [Fact]
    public void AnAxisWithNoValueTakesTheTargetsOwnCoordinateEvenWhenTheOwnerIsTheMover()
    {
        var (me, hero, runner) = ForceSetup(Force(L("[MOVE ME]"), L("[X START]", 10)));
        runner.Update(0, 0.25f);
        Assert.Equal((310f, 80f, 10f), me.Forced[0]);      // Y、Z 没写:取目标此刻的 Y、Z,不是拥有者自己的

        var (_, target, targetRunner) = ForceSetup(Force(L("[X START]", 10)));
        targetRunner.Update(0, 0.25f);
        Assert.Equal((310f, 80f, 10f), target.Forced[0]);
    }

    [Fact]
    public void TheLastMoveFlagWins()
    {
        var (me1, hero1, runner1) = ForceSetup(Force(L("[MOVE ME]"), L("[MOVE TARGET]"), L("[X START]", 1)));
        runner1.Update(0, 0.25f);
        Assert.Single(hero1.Forced);
        Assert.Empty(me1.Forced);

        var (me2, hero2, runner2) = ForceSetup(Force(L("[MOVE TARGET]"), L("[MOVE ME]"), L("[X START]", 1)));
        runner2.Update(0, 0.25f);
        Assert.Single(me2.Forced);
        Assert.Empty(hero2.Forced);
    }

    // ------------------------------------------------------------------ 起止值与插值

    [Fact]
    public void AnEndWithoutAStartSetsBothAndTheOrderOfStartAndEndDoesNotMatterForTwoValues()
    {
        var (_, alone, aloneRunner) = ForceSetup(Force(Cat(OnMapRight(L("[X END]", 30)))));
        aloneRunner.Update(0, 0.25f);
        Assert.Equal(30f, alone.Forced[0].x);               // 只写终点:一个值,起点同取它

        // 起点 10、终点 30,全程 400 ms,过了 250 ms:10 + 20 × 250 / 400 = 22.5,向零截断成 22
        var (endFirst, endFirstHero, endFirstRunner) = ForceSetup(Force(Cat(OnMapRight(L("[X END]", 30), L("[X START]", 10)))));
        endFirst.MotionDurationMs = 400;
        endFirstRunner.Update(0, 0.25f);
        Assert.Equal(22f, endFirstHero.Forced[0].x);

        var (startFirst, startFirstHero, startFirstRunner) = ForceSetup(Force(Cat(OnMapRight(L("[X START]", 10), L("[X END]", 30)))));
        startFirst.MotionDurationMs = 400;
        startFirstRunner.Update(0, 0.25f);
        Assert.Equal(22f, startFirstHero.Forced[0].x);
    }

    [Fact]
    public void InterpolationRunsLinearlyOverTheActionsWholeDurationAndStopsAtTheEnd()
    {
        var (me, hero, runner) = ForceSetup(Force(Cat(OnMapRight(
            L("[X START]", 0), L("[X END]", 100), L("[Y START]", 100), L("[Y END]", 0), L("[Z START]", 0), L("[Z END]", 40)))));
        me.MotionDurationMs = 1000;
        for (int i = 0; i < 5; i++)
        {
            runner.Update(0, 0.25f);
        }
        Assert.Equal(
            new[] { (25f, 75f, 10f), (50f, 50f, 20f), (75f, 25f, 30f), (100f, 0f, 40f), (100f, 0f, 40f) },
            hero.Forced.ToArray());
    }

    [Fact]
    public void InterpolationTruncatesTowardZeroInBothDirections()
    {
        var (me, hero, runner) = ForceSetup(Force(Cat(OnMapRight(L("[X START]", 0), L("[X END]", 100), L("[Y START]", 100), L("[Y END]", 0)))));
        me.MotionDurationMs = 400;
        runner.Update(0, 0.25f);
        Assert.Equal(62f, hero.Forced[0].x);     // 62.5
        Assert.Equal(37f, hero.Forced[0].y);     // 37.5:从 100 降向 0
        runner.Update(0, 0.25f);
        Assert.Equal((100f, 0f), (hero.Forced[1].x, hero.Forced[1].y));     // 已过 500 ms 超过全程:取终点
    }

    [Fact]
    public void AnUnknownDurationTakesTheEndImmediately()
    {
        // 客户端的插值函数:已过时间不小于总长(含两者都是 0)就返回终点
        var (_, hero, runner) = ForceSetup(Force(Cat(OnMapRight(L("[X START]", 0), L("[X END]", 100)))));
        runner.Update(0, 0.25f);
        Assert.Equal(100f, hero.Forced[0].x);

        // 已过时间和总长都是 0:同样取终点(不是起点)
        var (_, zeroHero, zeroRunner) = ForceSetup(Force(Cat(OnMapRight(L("[X START]", 0), L("[X END]", 100)))));
        zeroRunner.Update(0, 0f);
        Assert.Equal(100f, zeroHero.Forced[0].x);
    }

    [Fact]
    public void BeforeAnyTimeHasPassedARecordSitsAtItsStartPoint()
    {
        var (me, hero, runner) = ForceSetup(Force(Cat(OnMapRight(L("[X START]", 10), L("[X END]", 90)))));
        me.MotionDurationMs = 400;
        runner.Update(0, 0f);
        Assert.Equal((10f, 80f, 10f), hero.Forced[0]);      // 已过时间 0、总长 400:起点;Y、Z 没写:目标自己的
        runner.Update(0, 0.2f);
        Assert.Equal(50f, hero.Forced[1].x);                // 过了 200 ms:一半
    }

    [Fact]
    public void UniformAndReduceBothInterpolateLinearly()
    {
        var results = new List<float>();
        foreach (ScriptLine[] mode in new[] { new ScriptLine[0], new[] { L("[MOVE UNIFORM]") }, new[] { L("[MOVE REDUCE]") } })
        {
            var (me, hero, runner) = ForceSetup(Force(Cat(OnMapRight(L("[X START]", 0), L("[X END]", 100)), mode)));
            me.MotionDurationMs = 400;
            runner.Update(0, 0.25f);
            results.Add(hero.Forced[0].x);
        }
        Assert.Equal(new[] { 62f, 62f, 62f }, results.ToArray());
    }

    [Fact]
    public void MoreThanTwoValuesOnOneAxisLeavesThatCoordinateAtZero()
    {
        // 客户端的消费者只认 0、1、2 个值,其余的分支不给这个坐标赋值,初值是 0
        var (_, hero, runner) = ForceSetup(Force(Cat(OnMapRight(L("[X START]", 1), L("[X START]", 2), L("[X START]", 3), L("[Y START]", 5)))));
        runner.Update(0, 0.25f);
        Assert.Equal((0f, 5f, 10f), hero.Forced[0]);
    }

    // ------------------------------------------------------------------ 位移表的生命周期与目标

    [Fact]
    public void ARecordKeepsApplyingOnLaterUpdatesWithoutTheTriggerFiringAgain()
    {
        var (_, hero, runner) = ForceSetup(Force(Cat(OnMapRight(L("[X START]", 7)))));
        runner.Update(0, 0.25f);
        runner.Update(1, 0.25f);
        runner.Update(2, 0.25f);
        Assert.Equal(3, hero.Forced.Count);
    }

    [Fact]
    public void ExecutingASetPosForceAgainReplacesTheWholeTable()
    {
        // 客户端每次执行先清空这个动作上的位移表(0x010a7560):后一次执行(哪怕换了目标)之后,前一次登记的记录不再生效
        var hero = new Entity { Kind = ActEntityKind.Character, X = 300, Y = 80, Z = 10 };
        var me = new Entity { X = 100, Y = 50, Z = 0, Target = hero };
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[TARGET]", 0)),
            Trigger(L("[FRAME]", 1, 1), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(Force(Cat(OnMapRight(L("[X START]", 1), L("[Y START]", 1), L("[Z START]", 1))))),
            Behavior(Force(Cat(OnMapRight(L("[X START]", 9), L("[Y START]", 9), L("[Z START]", 9)))))));
        var runner = new ActRunner(p, me, new Host());
        runner.Update(0, 0.25f);
        runner.Update(0, 0.25f);
        Assert.Equal(2, hero.Forced.Count);
        Assert.Empty(me.Forced);

        runner.Update(1, 0.25f);                              // 第二块对拥有者自己执行:表被清空重建
        runner.Update(2, 0.25f);
        Assert.Equal(2, hero.Forced.Count);                   // 目标不再被摆
        Assert.Equal(new[] { (9f, 9f, 9f), (9f, 9f, 9f) }, me.Forced.ToArray());
    }

    [Fact]
    public void RestartingTheActionClearsTheTable()
    {
        var (_, hero, runner) = ForceSetup(Force(Cat(OnMapRight(L("[X START]", 7)))), "[TARGET]", 2);
        runner.Update(2, 0.25f);                              // 补跑 0、1 帧,第 2 帧触发
        Assert.Single(hero.Forced);
        runner.Begin();
        runner.Update(1, 0.25f);
        Assert.Single(hero.Forced);
    }

    [Fact]
    public void PassiveObjectsAreNotRegisteredAndADeathStopsTheForce()
    {
        var (_, passive, passiveRunner) = ForceSetup(Force(Cat(OnMapRight(L("[X START]", 7)))));
        passive.Kind = ActEntityKind.Passive;
        passiveRunner.Update(0, 0.25f);
        Assert.Empty(passive.Forced);     // 客户端只收"活动对象"(类型位 0x11):被动对象不收

        var (_, hero, runner) = ForceSetup(Force(Cat(OnMapRight(L("[X START]", 7)))));
        runner.Update(0, 0.25f);
        hero.IsAlive = false;
        runner.Update(0, 0.25f);
        Assert.Single(hero.Forced);
    }

    [Fact]
    public void ValuesAreDrawnOnceInTokenOrderAndSharedByEveryTarget()
    {
        // 客户端的执行块在目标循环之前把记号读完:随机数只抽一次,所有目标共用
        var a = new Entity { Kind = ActEntityKind.Character, X = 10, Y = 20, Z = 0 };
        var b = new Entity { Kind = ActEntityKind.Monster, X = 50, Y = 60, Z = 0 };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b };
        host.Randoms.Enqueue(3);
        host.Randoms.Enqueue(7);
        var me = new Entity();
        ActProgram p = P(OnEnemies(Force(Cat(OnMapRight(L("[X START]"), L("[RANDOM]", 0, 10), L("[Y START]", 0), L("[Z START]", 0))))));
        new ActRunner(p, me, host).Update(0, 0.25f);
        Assert.Equal((3f, 0f, 0f), a.Forced[0]);
        Assert.Equal((3f, 0f, 0f), b.Forced[0]);
        Assert.Equal(new[] { 7 }, host.Randoms.ToArray());
    }
}
