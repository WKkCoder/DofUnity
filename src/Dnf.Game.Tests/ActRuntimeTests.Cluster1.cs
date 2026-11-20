using System;
using System.Collections.Generic;
using System.Linq;
using Dnf.Data;
using Dnf.Game.Act;
using Xunit;

namespace Dnf.Game.Tests;

/// <summary>
/// 怪物未决簇 1 的编译期与运行期测试:
/// 1. 解析器保留闭合标签同行尾随值(<see cref="ActNode.CloserValues"/>)与 [DELAY DO BEHAVIOR] 值表达式;
/// 2. [ON MOVE COLLISION] + [AXIS INDEX] 轴掩码与 [DO BEHAVIOR]/[DIRECT DO BEHAVIOR]/[DELAY DO BEHAVIOR] 门闩;
/// 3. [CHECK GHOST] 与 [CHARGED TIME] 条件;
/// 4. [SET VISIBLITY]/[SET GRABABLE]/[SET DAMAGE CHECK]/[SET UNBEATABLE]/[SET PASSTYPE]/[SET WIDTH]/[CHANGE TEAM]/[DEFAULT TEAM]。
/// 脚本与编号全部合成,不含原版数据。
/// </summary>
public sealed partial class ActRuntimeTests
{
    private sealed class RestEntity : Entity, IActRestStateEntity
    {
        public RestEntity()
        {
            ActVisibilityByte = 1;
            Grabbable = true;
        }

        public bool IsActGhost { get; set; }
        public int ActChargedTimeMs { get; set; }
        public int? LastDamageCheckDuration { get; private set; }
        public bool DamageCheckOn { get; private set; }
        public bool UnbeatableOn { get; private set; }
        public int UnbeatableDuration { get; private set; }
        public int PassType { get; private set; } = -1;
        public int WidthX { get; private set; } = -1;
        public int WidthY { get; private set; } = -1;

        public bool ChangeTeamActive { get; private set; }
        public int ChangeTeamOrigTeam { get; private set; }
        public int ChangeTeamDuration { get; private set; }
        public int ChangeTeamHpPct { get; private set; }
        public IActEntity ChangeTeamSource { get; private set; }

        public int VisCalls { get; private set; }
        public int GrabCalls { get; private set; }
        public int DamageCheckCalls { get; private set; }
        public int UnbeatableCalls { get; private set; }
        public int PassTypeCalls { get; private set; }
        public int WidthCalls { get; private set; }
        public int BeginTeamCalls { get; private set; }
        public int EndTeamCalls { get; private set; }

        public void SetActVisibility(bool visible)
        {
            ActVisibilityByte = visible ? 1 : 0;
            VisCalls++;
            Log.Add("vis:" + (visible ? 1 : 0));
        }

        public void SetActGrabbable(bool grabbable)
        {
            Grabbable = grabbable;
            GrabCalls++;
            Log.Add("grab:" + (grabbable ? 1 : 0));
        }

        public void SetActDamageCheck(bool on, int durationMs)
        {
            DamageCheckOn = on;
            LastDamageCheckDuration = durationMs;
            DamageCheckCalls++;
            Log.Add("dmgchk:" + (on ? 1 : 0) + ":" + durationMs);
        }

        public void SetActUnbeatable(bool on, int durationMs)
        {
            UnbeatableOn = on;
            UnbeatableDuration = durationMs;
            UnbeatableCalls++;
            Log.Add("unbeat:" + (on ? 1 : 0) + ":" + durationMs);
        }

        public void SetActPassType(int passType)
        {
            PassType = passType;
            PassTypeCalls++;
            Log.Add("pass:" + passType);
        }

        public void SetActWidth(int widthX, int widthY)
        {
            WidthX = widthX;
            WidthY = widthY;
            WidthCalls++;
            Log.Add("width:" + widthX + "," + widthY);
        }

        public bool TryBeginActChangeTeam(int newTeam, int durationMs, int hpMaxPercent, IActEntity source, out int originalTeam)
        {
            BeginTeamCalls++;
            if (ChangeTeamActive)
            {
                originalTeam = 0;
                return false;
            }
            originalTeam = ((IActEntity)this).Team;
            ChangeTeamActive = true;
            ChangeTeamOrigTeam = originalTeam;
            ChangeTeamDuration = durationMs;
            ChangeTeamHpPct = hpMaxPercent;
            ChangeTeamSource = source;
            return true;
        }

        public bool TryEndActChangeTeam(out int originalTeam, out int hpMaxPercent, out IActEntity source)
        {
            EndTeamCalls++;
            if (!ChangeTeamActive)
            {
                originalTeam = 0;
                hpMaxPercent = 0;
                source = null;
                return false;
            }
            ChangeTeamActive = false;
            originalTeam = ChangeTeamOrigTeam;
            hpMaxPercent = ChangeTeamHpPct;
            source = ChangeTeamSource;
            ChangeTeamSource = null;
            return true;
        }
    }

    private sealed class ResolvePartySpyHost : IActHost
    {
        private readonly Host _inner;
        public List<(ActSelector selector, int partyIndex)> Calls { get; } = new();

        public ResolvePartySpyHost(Host inner) => _inner = inner;

        public long NowMs => _inner.NowMs;
        public int Random(int upperBound) => _inner.Random(upperBound);
        public void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft) => _inner.SpawnPassive(owner, index, level, x, y, z, facingLeft);
        public void Shake(int strength, int durationMs) => _inner.Shake(strength, durationMs);
        public void SummonMonster(IActEntity master, int index, int level, float x, float y, float z, bool facingLeft, int lifetimeMs) => _inner.SummonMonster(master, index, level, x, y, z, facingLeft, lifetimeMs);

        public void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into)
        {
            Calls.Add((selector, partyIndex));
            _inner.Resolve(owner, selector, partyIndex, into);
        }
    }

    // ------------------------------------------------------------------ CloserValues + [DELAY DO BEHAVIOR]

    [Fact]
    public void ActParserPreservesCloserValuesAndFlattensThemAfterTheCloseToken()
    {
        ActScript script = ActParser.Parse(new[]
        {
            L("[TRIGGER]"),
            L("[FRAME]", 0, 0),
            L("[DELAY DO BEHAVIOR]"),
            L("[ME]"),
            L("[RANDOM SELECT]", 0, 1),
            L("[/RANDOM SELECT]", 250),
            L("[/TRIGGER]"),
            L("[BEHAVIOR]"),
            L("[SET SPEED]"), L("[X AXIS]", 910), L("[/SET SPEED]"),
            L("[/BEHAVIOR]"),
            L("[BEHAVIOR]"),
            L("[SET SPEED]"), L("[X AXIS]", 920), L("[/SET SPEED]"),
            L("[/BEHAVIOR]"),
        });

        ActNode trig = script.Roots.First(n => n.Is("[TRIGGER]"));
        ActNode randNode = trig.Find("[RANDOM SELECT]");
        Assert.NotNull(randNode);
        Assert.Equal("[/RANDOM SELECT]", randNode.Closer);
        Assert.Single(randNode.CloserValues);
        Assert.Equal(250, randNode.CloserValues[0].Int);
        Assert.Empty(trig.CloserValues);

        ActProgram prog = ActProgram.Compile("delay_closer.act", script);
        Assert.True(prog.Triggers[0].Supported);
        ActRecord rec = prog.Triggers[0].Records.Single(r => r.Kind == ActRecordKind.DelayDoBehavior);
        Assert.NotNull(rec.DelayMs);
        Assert.True(rec.DelayMs.IsConstant);
        Assert.Equal(250, rec.DelayMs.First);
        Assert.Equal(250, rec.Number);

        var owner = new Entity();
        var host = new Host { NowMs = 1000 };
        host.Randoms.Enqueue(1);     // 选到行为组 1(speed:920)
        var runner = new ActRunner(prog, owner, host);
        runner.Update(0, 0.016f);
        Assert.Empty(owner.Log);

        host.NowMs = 1249;
        runner.Update(1, 0.016f);
        Assert.Empty(owner.Log);

        host.NowMs = 1250;
        runner.Update(2, 0.016f);
        Assert.Equal(new[] { "speed:920,-,-" }, owner.Log);
    }

    [Fact]
    public void DelayDoBehaviorEvaluatesNonConstantDelayExpressionAtRuntime()
    {
        ActProgram prog = P(Cat(
            Trigger(
                L("[FRAME]", 0, 0),
                L("[DELAY DO BEHAVIOR]"),
                L("[ME]", 0),
                L("[RANDOM SELECT]", 300, 600),
                L("[/RANDOM SELECT]")),
            Behavior(Speed(931))));

        Assert.True(prog.Triggers[0].Supported);
        ActRecord rec = prog.Triggers[0].Records.Single(r => r.Kind == ActRecordKind.DelayDoBehavior);
        Assert.NotNull(rec.DelayMs);
        Assert.False(rec.DelayMs.IsConstant);
        Assert.Equal(0, rec.Number);

        // 选项 0 -> 300ms
        var owner0 = new Entity();
        var host0 = new Host { NowMs = 1000 };
        host0.Randoms.Enqueue(0);
        var runner0 = new ActRunner(prog, owner0, host0);
        runner0.Update(0, 0.016f);
        Assert.Empty(owner0.Log);
        host0.NowMs = 1299;
        runner0.Update(1, 0.016f);
        Assert.Empty(owner0.Log);
        host0.NowMs = 1300;
        runner0.Update(2, 0.016f);
        Assert.Equal(new[] { "speed:931,-,-" }, owner0.Log);

        // 选项 1 -> 600ms(如果误用 rec.Number == 0 则 1000ms 时就误触发)
        var owner1 = new Entity();
        var host1 = new Host { NowMs = 1000 };
        host1.Randoms.Enqueue(1);
        var runner1 = new ActRunner(prog, owner1, host1);
        runner1.Update(0, 0.016f);
        Assert.Empty(owner1.Log);
        host1.NowMs = 1599;
        runner1.Update(1, 0.016f);
        Assert.Empty(owner1.Log);
        host1.NowMs = 1600;
        runner1.Update(2, 0.016f);
        Assert.Equal(new[] { "speed:931,-,-" }, owner1.Log);
    }

    [Fact]
    public void DelayDoBehaviorWithoutDelayIsMarkedUnsupported()
    {
        ActProgram prog = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DELAY DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(932))));
        Assert.False(prog.Triggers[0].Supported);
        Assert.Contains("[DELAY DO BEHAVIOR] (no delay)", prog.Triggers[0].UnsupportedTags);
    }

    // ------------------------------------------------------------------ [ON MOVE COLLISION] + [AXIS INDEX]

    [Fact]
    public void OnMoveCollisionDefaultsToXAndYAllowedAndZBlocked()
    {
        ActProgram prog = P(Cat(
            Trigger(L("[ON MOVE COLLISION]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(940))));
        Assert.True(prog.Triggers[0].Supported);

        var owner = new Entity();
        var runner = new ActRunner(prog, owner, new Host());
        // 逐帧 Update 与别的事件不能唤醒 [ON MOVE COLLISION]
        runner.Update(0, 0.016f);
        runner.OnSetAction();
        runner.OnDamage(new Entity { Kind = ActEntityKind.Character });
        Assert.Empty(owner.Log);

        // Z 轴(2)默认关闭
        runner.OnMoveCollision(2);
        Assert.Empty(owner.Log);

        // X 轴(0)默认打开
        runner.OnMoveCollision(0);
        Assert.Equal(new[] { "speed:940,-,-" }, owner.Log);

        // Y 轴(1)默认打开
        var ownerY = new Entity();
        new ActRunner(prog, ownerY, new Host()).OnMoveCollision(1);
        Assert.Equal(new[] { "speed:940,-,-" }, ownerY.Log);

        // 轴号越界(非 0/1/2)回退到 seenMoveCollision(本触发器已见过 [ON MOVE COLLISION],为真)
        var ownerOther = new Entity();
        new ActRunner(prog, ownerOther, new Host()).OnMoveCollision(7);
        Assert.Equal(new[] { "speed:940,-,-" }, ownerOther.Log);
    }

    [Theory]
    [InlineData(0, true, false, false)]
    [InlineData(1, false, true, false)]
    [InlineData(2, false, false, true)]
    [InlineData(3, true, true, false)]   // 超出 0..2 不改默认掩码 (X=true, Y=true, Z=false)
    [InlineData(-1, true, true, false)]
    public void AxisIndexMutuallyExclusivelySelectsTheAllowedCollisionAxis(int axisArg, bool xOk, bool yOk, bool zOk)
    {
        // 真实脚本形状:[TRIGGER] [ON MOVE COLLISION] [CHECKUP] [AXIS INDEX] k [/CHECKUP] [DO BEHAVIOR] [ME] 0 [/TRIGGER]
        ActProgram prog = P(Cat(
            Trigger(
                L("[ON MOVE COLLISION]"),
                L("[CHECKUP]"),
                L("[AXIS INDEX]", axisArg),
                L("[/CHECKUP]"),
                L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(941))));
        Assert.True(prog.Triggers[0].Supported);

        int Fire(int axis)
        {
            var e = new Entity();
            new ActRunner(prog, e, new Host()).OnMoveCollision(axis);
            return e.Log.Count;
        }

        Assert.Equal(xOk ? 1 : 0, Fire(0));
        Assert.Equal(yOk ? 1 : 0, Fire(1));
        Assert.Equal(zOk ? 1 : 0, Fire(2));
    }

    [Fact]
    public void SecondAxisIndexOverridesFirstAndMissingArgumentIsUnsupported()
    {
        ActProgram overridden = P(Cat(
            Trigger(L("[ON MOVE COLLISION]"), L("[AXIS INDEX]", 0), L("[AXIS INDEX]", 2), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(942))));
        Assert.True(overridden.Triggers[0].Supported);

        var eX = new Entity();
        new ActRunner(overridden, eX, new Host()).OnMoveCollision(0);
        Assert.Empty(eX.Log);

        var eZ = new Entity();
        new ActRunner(overridden, eZ, new Host()).OnMoveCollision(2);
        Assert.Single(eZ.Log);

        ActProgram bad = P(Cat(
            Trigger(L("[ON MOVE COLLISION]"), L("[AXIS INDEX]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(942))));
        Assert.False(bad.Triggers[0].Supported);
        Assert.Contains("[AXIS INDEX]", bad.Triggers[0].UnsupportedTags);
    }

    [Fact]
    public void OnMoveCollisionGatesDirectAndDelayDoBehaviorAndRespectsLimitAndAbort()
    {
        // 1. [DO BEHAVIOR] 遇不允许的轴会把 seenMoveCollision 清零,导致后面的 [DIRECT DO BEHAVIOR] 也跳过;
        //    遇允许的轴则两项都执行。
        ActProgram directChain = P(Cat(
            Trigger(
                L("[ON MOVE COLLISION]"),
                L("[AXIS INDEX]", 2),
                L("[DO BEHAVIOR]"), L("[ME]", 0),
                L("[DIRECT DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(Speed(943)),
            Behavior(Speed(944))));

        var blocked = new Entity();
        new ActRunner(directChain, blocked, new Host()).OnMoveCollision(0);
        Assert.Empty(blocked.Log);

        var allowed = new Entity();
        new ActRunner(directChain, allowed, new Host()).OnMoveCollision(2);
        Assert.Equal(new[] { "speed:943,-,-", "speed:944,-,-" }, allowed.Log);

        // 2. [DIRECT DO BEHAVIOR] 若在 [ON MOVE COLLISION] 之前(尚未 seen),不触发;
        //    但后面的 [DO BEHAVIOR] 命轴时把 seen 置真,使随后的 [DIRECT DO BEHAVIOR] 触发;
        //    轴号越界(9)且尚未 seen 时 [DO BEHAVIOR] 也不触发。
        ActProgram directBeforeEvent = P(Cat(
            Trigger(
                L("[AXIS INDEX]", 0),
                L("[DIRECT DO BEHAVIOR]"), L("[ME]", 0),
                L("[DO BEHAVIOR]"), L("[ME]", 1),
                L("[DIRECT DO BEHAVIOR]"), L("[ME]", 2),
                L("[ON MOVE COLLISION]")),
            Behavior(Speed(945)),
            Behavior(Speed(946)),
            Behavior(Speed(947))));

        var beforeEvt = new Entity();
        new ActRunner(directBeforeEvent, beforeEvt, new Host()).OnMoveCollision(0);
        Assert.Equal(new[] { "speed:946,-,-", "speed:947,-,-" }, beforeEvt.Log);

        var unknownAxisBeforeSeen = new Entity();
        new ActRunner(directBeforeEvent, unknownAxisBeforeSeen, new Host()).OnMoveCollision(9);
        Assert.Empty(unknownAxisBeforeSeen.Log);

        // 3. [DELAY DO BEHAVIOR] 在 [ON MOVE COLLISION] 下受轴门控、次数上限与延迟队列约束,
        //    且不允许的轴会把 seen 清零拦截后面的 [DIRECT DO BEHAVIOR],允许的轴会把 seen 置真放行后面的 [DIRECT DO BEHAVIOR]。
        ActProgram delayed = P(Cat(
            Trigger(
                L("[LIMIT]", 2),
                L("[AXIS INDEX]", 1),
                L("[DELAY DO BEHAVIOR]"), L("[ME]", 0, 150),
                L("[DIRECT DO BEHAVIOR]"), L("[ME]", 1),
                L("[ON MOVE COLLISION]")),
            Behavior(Speed(948)),
            Behavior(Speed(949))));

        var dOwner = new Entity();
        var dHost = new Host { NowMs = 500 };
        var dRunner = new ActRunner(delayed, dOwner, dHost);
        // 轴 0 不允许:不排队,且 seen=false 导致后面的 [DIRECT DO BEHAVIOR] 也不执行
        dRunner.OnMoveCollision(0);
        Assert.Empty(dOwner.Log);
        dHost.NowMs = 700;
        dRunner.Update(0, 0.016f);
        Assert.Empty(dOwner.Log);

        // 轴 1 允许:[DELAY DO BEHAVIOR] 排队并置 seen=true -> 同轮紧跟的 [DIRECT DO BEHAVIOR] 立即执行(共消耗 2 次触发上限)
        dRunner.OnMoveCollision(1);
        Assert.Equal(new[] { "speed:949,-,-" }, dOwner.Log);
        dHost.NowMs = 849;
        dRunner.Update(1, 0.016f);
        Assert.Equal(new[] { "speed:949,-,-" }, dOwner.Log);
        dHost.NowMs = 850;
        dRunner.Update(2, 0.016f);
        Assert.Equal(new[] { "speed:949,-,-", "speed:948,-,-" }, dOwner.Log);

        // Limit=2 已耗尽,再次撞 Y 轴不再排队也不再立即执行
        dRunner.OnMoveCollision(1);
        dHost.NowMs = 1500;
        dRunner.Update(3, 0.016f);
        Assert.Equal(new[] { "speed:949,-,-", "speed:948,-,-" }, dOwner.Log);

        // 3b. [ON MOVE COLLISION] 在前 + 轴 0 不允许 -> [DELAY DO BEHAVIOR] 把 seen 清零拦截紧跟的 [DIRECT DO BEHAVIOR]
        ActProgram delayedClearSeen = P(Cat(
            Trigger(
                L("[ON MOVE COLLISION]"),
                L("[AXIS INDEX]", 1),
                L("[DELAY DO BEHAVIOR]"), L("[ME]", 0, 100),
                L("[DIRECT DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(Speed(948)),
            Behavior(Speed(949))));
        var dcOwner = new Entity();
        new ActRunner(delayedClearSeen, dcOwner, new Host()).OnMoveCollision(0);
        Assert.Empty(dcOwner.Log);

        // 4. 空程序安全返回 + [DESTROY] 中止后续触发器 + 非碰撞触发器不被 OnMoveCollision 唤醒
        var emptyRunner = new ActRunner(P(), new Entity(), new Host());
        emptyRunner.OnMoveCollision(0);

        ActProgram abortProg = P(Cat(
            Trigger(L("[ON DAMAGE]"), L("[DO BEHAVIOR]"), L("[ME]", 2)),
            Trigger(L("[ON MOVE COLLISION]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[ON MOVE COLLISION]"), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(L("[DESTROY]")),
            Behavior(Speed(950)),
            Behavior(Speed(951))));
        var abortOwner = new Entity();
        new ActRunner(abortProg, abortOwner, new Host()).OnMoveCollision(0);
        Assert.Equal(new[] { "destroy" }, abortOwner.Log);
    }

    /// <summary>
    /// 行为执行后拥有者的 MotionVersion 变了(切动作/改帧)就会把中止位置真(同一次入口里后面的触发器不再求值),中止位一直留到下一次入口才复位。
    /// 上一条用例每次都新建 runner,而且测试实体的 [DESTROY] 只置死亡、不改 MotionVersion,并没有真的置中止位;
    /// 这里在同一个 runner 上、中止位确实为真的状态下再调 <see cref="ActRunner.OnMoveCollision"/>:入口必须先复位,触发器照常求值。
    /// 测试实体里 [SET FRAME] 取正数会让 MotionVersion 加一,所以用它置中止位。
    /// </summary>
    [Fact]
    public void OnMoveCollisionEntryClearsTheAbortFlagLeftSetByAnEarlierEntryOnTheSameRunner()
    {
        // 1. 同一个 runner 连续两次碰壁:第 1 次触发器 0 改帧置中止位,同次入口里排在后面的触发器 1 没轮到求值;
        //    第 2 次入口时中止位仍为真,必须先复位 -> 触发器 0 已满 [LIMIT] 1 跳过,触发器 1 照常求值并触发。
        ActProgram twoHits = P(Cat(
            Trigger(L("[LIMIT]", 1), L("[ON MOVE COLLISION]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[ON MOVE COLLISION]"), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(L("[SET FRAME]", 3)),
            Behavior(Speed(960))));
        Assert.True(twoHits.Triggers[0].Supported);
        Assert.True(twoHits.Triggers[1].Supported);

        var owner = new Entity();
        var runner = new ActRunner(twoHits, owner, new Host());
        runner.OnMoveCollision(0);
        Assert.Equal(new[] { "frame:3" }, owner.Log);          // 改帧置了中止位:触发器 1 在这次入口里没有求值
        Assert.Equal(1, runner.FiredCount(0));
        Assert.Equal(0, runner.FiredCount(1));
        runner.OnMoveCollision(0);                             // 同一个 runner 的第 2 次入口(此刻中止位还是真)
        Assert.Equal(new[] { "frame:3", "speed:960,-,-" }, owner.Log);
        Assert.Equal(1, runner.FiredCount(0));                 // 触发器 0:[LIMIT] 1 已满,不再触发
        Assert.Equal(1, runner.FiredCount(1));                 // 触发器 1:这次入口里照常触发

        // 1b. 不设 [LIMIT]:同一个触发器改帧后(没有单次位)第 2 次碰壁照样再触发一次,总共两次 "frame:3"。
        ActProgram sameTrigger = P(Cat(
            Trigger(L("[ON MOVE COLLISION]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[SET FRAME]", 3))));
        var ownerSame = new Entity();
        var runnerSame = new ActRunner(sameTrigger, ownerSame, new Host());
        runnerSame.OnMoveCollision(0);
        runnerSame.OnMoveCollision(0);
        Assert.Equal(new[] { "frame:3", "frame:3" }, ownerSame.Log);
        Assert.Equal(2, runnerSame.FiredCount(0));

        // 2. 逐帧 Update 里的触发器改帧置了中止位,Update 返回后中止位还留着;紧接着同一个 runner 收到碰壁(Y 轴默认允许)必须照常求值。
        ActProgram afterUpdate = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[ON MOVE COLLISION]"), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(L("[SET FRAME]", 3)),
            Behavior(Speed(961))));
        var ownerUpdate = new Entity();
        var runnerUpdate = new ActRunner(afterUpdate, ownerUpdate, new Host());
        runnerUpdate.Update(0, 0.016f);
        Assert.Equal(new[] { "frame:3" }, ownerUpdate.Log);
        runnerUpdate.OnMoveCollision(1);
        Assert.Equal(new[] { "frame:3", "speed:961,-,-" }, ownerUpdate.Log);

        // 3. [ON DAMAGE] 唤醒里改帧置了中止位:之后同一个 runner 先撞 X 墙(触发器只许 Y 轴,不触发,但入口已复位中止位),
        //    再撞 Y 墙 -> 触发。入口若不复位,两次碰壁都不会求值。
        ActProgram afterDamage = P(Cat(
            Trigger(L("[ON DAMAGE]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[ON MOVE COLLISION]"), L("[AXIS INDEX]", 1), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(L("[SET FRAME]", 3)),
            Behavior(Speed(962))));
        var ownerDamage = new Entity();
        var runnerDamage = new ActRunner(afterDamage, ownerDamage, new Host());
        runnerDamage.OnDamage(new Entity { Kind = ActEntityKind.Character });
        Assert.Equal(new[] { "frame:3" }, ownerDamage.Log);
        runnerDamage.OnMoveCollision(0);
        Assert.Equal(new[] { "frame:3" }, ownerDamage.Log);
        runnerDamage.OnMoveCollision(1);
        Assert.Equal(new[] { "frame:3", "speed:962,-,-" }, ownerDamage.Log);
    }

    /// <summary>
    /// 触发器的事件取第一条事件记录。第一条是别的事件、后面才有 [ON MOVE COLLISION] 时,那个事件到来求值会走到
    /// [ON MOVE COLLISION] 记录,事件种类对不上当前事件必须直接放弃(触发器不触发,也不能把"见过碰壁"置真去放行后面的 [DO BEHAVIOR])。
    /// </summary>
    [Theory]
    [InlineData("[ON DAMAGE]", 0)]
    [InlineData("[ON ATTACKSUCCESS]", 1)]
    [InlineData("[ON SET ACTION]", 2)]
    public void OnMoveCollisionRecordBehindAnotherEventRecordDoesNotFireTheTriggerWhenThatFirstEventArrives(string firstEvent, int which)
    {
        void Raise(ActRunner r)
        {
            switch (which)
            {
                case 0:
                    r.OnDamage(new Entity { Kind = ActEntityKind.Character });
                    break;
                case 1:
                    r.OnAttackSuccess(new Entity { Kind = ActEntityKind.Character });
                    break;
                default:
                    r.OnSetAction();
                    break;
            }
        }

        // 触发器 0:<第一条事件> + [ON MOVE COLLISION] + [DO BEHAVIOR] 0;触发器 1(对照):只有 <第一条事件> + 同样形状的 [DO BEHAVIOR] 1。
        ActProgram prog = P(Cat(
            Trigger(L(firstEvent), L("[ON MOVE COLLISION]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L(firstEvent), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(Speed(970)),
            Behavior(Speed(971))));
        Assert.True(prog.Triggers[0].Supported);
        Assert.True(prog.Triggers[1].Supported);

        var owner = new Entity();
        var runner = new ActRunner(prog, owner, new Host());
        Raise(runner);
        // 事件确实送达(对照触发器 1 触发),触发器 0 因 [ON MOVE COLLISION] 记录的事件对不上而不触发(没有 "speed:970")
        Assert.Equal(new[] { "speed:971,-,-" }, owner.Log);
        Assert.Equal(0, runner.FiredCount(0));
        Assert.Equal(1, runner.FiredCount(1));

        // 碰壁入口只唤醒第一条事件就是 [ON MOVE COLLISION] 的触发器:这两个触发器的第一条事件不是碰壁,谁都不触发
        runner.OnMoveCollision(0);
        runner.OnMoveCollision(1);
        Assert.Equal(new[] { "speed:971,-,-" }, owner.Log);
        Assert.Equal(0, runner.FiredCount(0));
        Assert.Equal(1, runner.FiredCount(1));
    }

    /// <summary>对称情形:[ON MOVE COLLISION] 在前、别的事件记录在后,碰壁到来走到后一条事件记录时事件对不上,同样不触发。</summary>
    [Theory]
    [InlineData("[ON DAMAGE]", 0)]
    [InlineData("[ON ATTACKSUCCESS]", 1)]
    [InlineData("[ON SET ACTION]", 2)]
    public void AnotherEventRecordBehindOnMoveCollisionRecordDoesNotFireTheTriggerWhenCollisionArrives(string secondEvent, int which)
    {
        void Raise(ActRunner r)
        {
            switch (which)
            {
                case 0:
                    r.OnDamage(new Entity { Kind = ActEntityKind.Character });
                    break;
                case 1:
                    r.OnAttackSuccess(new Entity { Kind = ActEntityKind.Character });
                    break;
                default:
                    r.OnSetAction();
                    break;
            }
        }

        // 触发器 0:[ON MOVE COLLISION] + <第二条事件> + [DO BEHAVIOR] 0;触发器 1(对照):只有 [ON MOVE COLLISION] + 同样形状的 [DO BEHAVIOR] 1。
        ActProgram prog = P(Cat(
            Trigger(L("[ON MOVE COLLISION]"), L(secondEvent), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[ON MOVE COLLISION]"), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(Speed(972)),
            Behavior(Speed(973))));
        Assert.True(prog.Triggers[0].Supported);
        Assert.True(prog.Triggers[1].Supported);

        var owner = new Entity();
        var runner = new ActRunner(prog, owner, new Host());
        runner.OnMoveCollision(0);
        // 碰壁确实送达(对照触发器 1 触发),触发器 0 在第二条事件记录处放弃(没有 "speed:972")
        Assert.Equal(new[] { "speed:973,-,-" }, owner.Log);
        Assert.Equal(0, runner.FiredCount(0));
        Assert.Equal(1, runner.FiredCount(1));

        // 另一个事件到来:两个触发器的第一条事件都是碰壁,不被它唤醒
        Raise(runner);
        Assert.Equal(new[] { "speed:973,-,-" }, owner.Log);
        Assert.Equal(0, runner.FiredCount(0));
        Assert.Equal(1, runner.FiredCount(1));
    }

    // ------------------------------------------------------------------ [CHECK GHOST]

    [Fact]
    public void CheckGhostPassesOnlyForActiveOwnerWhenRoomContainsAGhostPassive()
    {
        ActProgram prog = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[CHECK GHOST]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(951))));
        Assert.True(prog.Triggers[0].Supported);

        // 1. 房间里没有鬼魂:不通过
        var hostEmpty = new Host();
        hostEmpty.Sets[ActSelector.Passive] = new List<IActEntity>
        {
            new RestEntity { Kind = ActEntityKind.Passive, IsActGhost = false },
            new Entity { Kind = ActEntityKind.Passive },
        };
        var mon1 = new RestEntity { Kind = ActEntityKind.Monster };
        new ActRunner(prog, mon1, hostEmpty).Update(0, 0.016f);
        Assert.Empty(mon1.Log);

        // 2. 房间里有鬼魂被动对象(位于索引 0):怪物拥有者通过,且 Resolve 收到 partyIndex == -1
        var hostGhost = new Host();
        hostGhost.Sets[ActSelector.Passive] = new List<IActEntity>
        {
            new RestEntity { Kind = ActEntityKind.Passive, IsActGhost = true },
            new RestEntity { Kind = ActEntityKind.Passive, IsActGhost = false },
        };
        var spyGhostHost = new ResolvePartySpyHost(hostGhost);
        var mon2 = new RestEntity { Kind = ActEntityKind.Monster };
        new ActRunner(prog, mon2, spyGhostHost).Update(0, 0.016f);
        Assert.Equal(new[] { "speed:951,-,-" }, mon2.Log);
        Assert.Equal(new[] { (ActSelector.Passive, -1) }, spyGhostHost.Calls);

        // 3. 角色拥有者也属于 ActiveObject:通过
        var charOwner = new RestEntity { Kind = ActEntityKind.Character };
        new ActRunner(prog, charOwner, hostGhost).Update(0, 0.016f);
        Assert.Equal(new[] { "speed:951,-,-" }, charOwner.Log);

        // 4. 被动对象拥有者不满足 ActiveObject 强转:即使场上有鬼魂也失败
        var passiveOwner = new RestEntity { Kind = ActEntityKind.Passive };
        new ActRunner(prog, passiveOwner, hostGhost).Update(0, 0.016f);
        Assert.Empty(passiveOwner.Log);

        // 5. 列表含 null 或非 Passive 实体(哪怕 IsActGhost=true)不能误判为鬼魂
        var badHost = new Host { ListDeadToo = true };
        badHost.Sets[ActSelector.Passive] = new List<IActEntity>
        {
            null,
            new RestEntity { Kind = ActEntityKind.Monster, IsActGhost = true },
            new RestEntity { Kind = ActEntityKind.Character, IsActGhost = true },
        };
        var mon3 = new RestEntity { Kind = ActEntityKind.Monster };
        new ActRunner(prog, mon3, badHost).Update(0, 0.016f);
        Assert.Empty(mon3.Log);
    }

    // ------------------------------------------------------------------ [CHARGED TIME]

    [Fact]
    public void ChargedTimeFiltersMonsterTargetsByChargedMsAndRejectsNonMonsters()
    {
        // 真实脚本形状:[ON SET ACTION] [CHARGED TIME] [=>] 500 [DO BEHAVIOR] [CHECKUP OBJECT] 0
        ActProgram ge500 = P(Cat(
            Trigger(L("[ON SET ACTION]"), L("[WHICH]"), L("[ALL ENEMY]"), L("[CHARGED TIME]"), L("[=>]", 500), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(952))));
        Assert.True(ge500.Triggers[0].Supported);

        var owner = new RestEntity { Kind = ActEntityKind.Monster };
        var monBelow = new RestEntity { Kind = ActEntityKind.Monster, ActChargedTimeMs = 499 };
        var monExact = new RestEntity { Kind = ActEntityKind.Monster, ActChargedTimeMs = 500 };
        var monAbove = new RestEntity { Kind = ActEntityKind.Monster, ActChargedTimeMs = 501 };
        var charHigh = new RestEntity { Kind = ActEntityKind.Character, ActChargedTimeMs = 900 };
        var passiveHigh = new RestEntity { Kind = ActEntityKind.Passive, ActChargedTimeMs = 900 };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { monBelow, monExact, monAbove, charHigh, passiveHigh };

        new ActRunner(ge500, owner, host).OnSetAction();
        Assert.Empty(monBelow.Log);
        Assert.Equal(new[] { "speed:952,-,-" }, monExact.Log);
        Assert.Equal(new[] { "speed:952,-,-" }, monAbove.Log);
        Assert.Empty(charHigh.Log);
        Assert.Empty(passiveHigh.Log);

        // 不实现 IActRestStateEntity 的怪物蓄力时间按 0 计算
        ActProgram lt1 = P(Cat(
            Trigger(L("[ON SET ACTION]"), L("[CHARGED TIME]"), L("[<]", 1), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(953))));
        var plainMon = new Entity { Kind = ActEntityKind.Monster };
        new ActRunner(lt1, plainMon, new Host()).OnSetAction();
        Assert.Equal(new[] { "speed:953,-,-" }, plainMon.Log);

        ActProgram eq1 = P(Cat(
            Trigger(L("[ON SET ACTION]"), L("[CHARGED TIME]"), L("[==]", 1), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(953))));
        var plainMon2 = new Entity { Kind = ActEntityKind.Monster };
        new ActRunner(eq1, plainMon2, new Host()).OnSetAction();
        Assert.Empty(plainMon2.Log);

        // 缺比较符则不支持
        ActProgram broken = P(Cat(
            Trigger(L("[ON SET ACTION]"), L("[CHARGED TIME]", 500), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(954))));
        Assert.False(broken.Triggers[0].Supported);
        Assert.Contains("[CHARGED TIME]", broken.Triggers[0].UnsupportedTags);
    }

    // ------------------------------------------------------------------ [SET VISIBLITY]

    [Fact]
    public void SetVisibilityUpdatesAliveRestTargetsAndRoundTripsWithIsVisible()
    {
        ActProgram hide = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL OUR TEAM]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[SET VISIBLITY]", 0))));
        ActProgram showNeg = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[IS VISIBLE]", 0), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[SET VISIBLITY]", -5))));
        ActProgram showIfOne = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[IS VISIBLE]", 1), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(Speed(955))));

        var alive = new RestEntity { Kind = ActEntityKind.Monster };
        var dead = new RestEntity { Kind = ActEntityKind.Monster, IsAlive = false };
        var plain = new Entity { Kind = ActEntityKind.Monster };
        var host = new Host { ListDeadToo = true };
        host.Sets[ActSelector.AllOurTeam] = new List<IActEntity> { alive, dead, plain };

        new ActRunner(hide, alive, host).Update(0, 0.016f);
        Assert.Equal(0, alive.ActVisibilityByte);
        Assert.Equal(1, alive.VisCalls);
        Assert.Equal(1, dead.ActVisibilityByte);
        Assert.Equal(0, dead.VisCalls);

        // 现在 visible == 0:[IS VISIBLE] 1 不通过,[IS VISIBLE] 0 通过并把 -5(非 0)写成 true
        new ActRunner(showIfOne, alive, host).Update(0, 0.016f);
        Assert.DoesNotContain("speed:955,-,-", alive.Log);

        new ActRunner(showNeg, alive, host).Update(0, 0.016f);
        Assert.Equal(1, alive.ActVisibilityByte);

        new ActRunner(showIfOne, alive, host).Update(0, 0.016f);
        Assert.Contains("speed:955,-,-", alive.Log);

        // 缺参报错
        ActProgram bad = P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[SET VISIBLITY]"))));
        Assert.Equal(ActBehaviorKind.Unsupported, bad.Behaviors[0].Items[0].Kind);
    }

    // ------------------------------------------------------------------ [SET GRABABLE]

    [Fact]
    public void SetGrabableWritesMonsterOwnerOnlyWithExactEqualityToOne()
    {
        ActProgram Make(int val) => P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[SET GRABABLE]", val))));

        var owner = new RestEntity { Kind = ActEntityKind.Monster };
        var enemy = new RestEntity { Kind = ActEntityKind.Monster };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { enemy };

        // 值 0 -> false(作用在 owner,不改目标集里的 enemy)
        new ActRunner(Make(0), owner, host).Update(0, 0.016f);
        Assert.Equal(false, owner.Grabbable);
        Assert.Equal(1, owner.GrabCalls);
        Assert.Equal(true, enemy.Grabbable);
        Assert.Equal(0, enemy.GrabCalls);

        // 值 1 -> true
        new ActRunner(Make(1), owner, host).Update(0, 0.016f);
        Assert.Equal(true, owner.Grabbable);

        // 值 2 -> false(客户端 CMP EAX,1; 只有 == 1 为真,非 != 0)
        new ActRunner(Make(2), owner, host).Update(0, 0.016f);
        Assert.Equal(false, owner.Grabbable);

        // 非怪物拥有者(Character / Passive)与无接口怪物均安全忽略
        var charOwner = new RestEntity { Kind = ActEntityKind.Character };
        var passiveOwner = new RestEntity { Kind = ActEntityKind.Passive };
        new ActRunner(Make(0), charOwner, host).Update(0, 0.016f);
        new ActRunner(Make(0), passiveOwner, host).Update(0, 0.016f);
        Assert.Equal(0, charOwner.GrabCalls);
        Assert.Equal(0, passiveOwner.GrabCalls);
        new ActRunner(Make(0), new Entity { Kind = ActEntityKind.Monster }, host).Update(0, 0.016f);

        // 缺参不支持
        ActProgram bad = P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[SET GRABABLE]"))));
        Assert.Equal(ActBehaviorKind.Unsupported, bad.Behaviors[0].Items[0].Kind);
    }

    // ------------------------------------------------------------------ [SET DAMAGE CHECK]

    [Fact]
    public void SetDamageCheckWritesMonsterOwnerOnlyAndParsesModeOneAsOn()
    {
        ActProgram Make(int mode, int dur) => P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[SET DAMAGE CHECK]", mode, dur))));

        var owner = new RestEntity { Kind = ActEntityKind.Monster };
        new ActRunner(Make(1, 2400), owner, new Host()).Update(0, 0.016f);
        Assert.True(owner.DamageCheckOn);
        Assert.Equal(2400, owner.LastDamageCheckDuration);

        // mode == 2 不是 1 -> off
        new ActRunner(Make(2, 900), owner, new Host()).Update(0, 0.016f);
        Assert.False(owner.DamageCheckOn);
        Assert.Equal(900, owner.LastDamageCheckDuration);

        new ActRunner(Make(0, 0), owner, new Host()).Update(0, 0.016f);
        Assert.False(owner.DamageCheckOn);
        Assert.Equal(0, owner.LastDamageCheckDuration);

        // 非怪物拥有者与无接口怪物忽略
        var ch = new RestEntity { Kind = ActEntityKind.Character };
        var po = new RestEntity { Kind = ActEntityKind.Passive };
        new ActRunner(Make(1, 1000), ch, new Host()).Update(0, 0.016f);
        new ActRunner(Make(1, 1000), po, new Host()).Update(0, 0.016f);
        Assert.Equal(0, ch.DamageCheckCalls);
        Assert.Equal(0, po.DamageCheckCalls);
        new ActRunner(Make(1, 1000), new Entity { Kind = ActEntityKind.Monster }, new Host()).Update(0, 0.016f);

        // 缺第一或第二参不支持
        ActProgram bad0 = P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[SET DAMAGE CHECK]"))));
        ActProgram bad1 = P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[SET DAMAGE CHECK]", 1))));
        Assert.Equal(ActBehaviorKind.Unsupported, bad0.Behaviors[0].Items[0].Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, bad1.Behaviors[0].Items[0].Kind);
    }

    // ------------------------------------------------------------------ [SET UNBEATABLE]

    [Fact]
    public void SetUnbeatableSupportsOpenTagAndStringFormsAndAppliesToActiveObjectsIncludingDead()
    {
        ActProgram onOpen = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL OUR TEAM]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[SET UNBEATABLE]"), L("[ON]", 1750))));
        ActProgram offOpen = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[SET UNBEATABLE]"), L("[OFF]"))));
        ActProgram onStr = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[SET UNBEATABLE]", "[ON]", 820))));
        ActProgram offStr = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[SET UNBEATABLE]", "[OFF]"))));

        var mon = new RestEntity { Kind = ActEntityKind.Monster };
        var deadChar = new RestEntity { Kind = ActEntityKind.Character, IsAlive = false };
        var passive = new RestEntity { Kind = ActEntityKind.Passive };
        var host = new Host { ListDeadToo = true };
        host.Sets[ActSelector.AllOurTeam] = new List<IActEntity> { mon, deadChar, passive };

        new ActRunner(onOpen, mon, host).Update(0, 0.016f);
        Assert.True(mon.UnbeatableOn);
        Assert.Equal(1750, mon.UnbeatableDuration);
        Assert.True(deadChar.UnbeatableOn);
        Assert.Equal(1750, deadChar.UnbeatableDuration);
        Assert.Equal(0, passive.UnbeatableCalls);

        new ActRunner(offOpen, mon, host).Update(0, 0.016f);
        Assert.False(mon.UnbeatableOn);
        Assert.Equal(0, mon.UnbeatableDuration);

        new ActRunner(onStr, mon, host).Update(0, 0.016f);
        Assert.True(mon.UnbeatableOn);
        Assert.Equal(820, mon.UnbeatableDuration);

        new ActRunner(offStr, mon, host).Update(0, 0.016f);
        Assert.False(mon.UnbeatableOn);
        Assert.Equal(0, mon.UnbeatableDuration);

        // 畸形参数不支持
        Assert.Equal(ActBehaviorKind.Unsupported, P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[SET UNBEATABLE]"), L("[ON]")))).Behaviors[0].Items[0].Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[SET UNBEATABLE]", "[BAD]")))).Behaviors[0].Items[0].Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[SET UNBEATABLE]")))).Behaviors[0].Items[0].Kind);
    }

    // ------------------------------------------------------------------ [SET PASSTYPE]

    [Theory]
    [InlineData("[pass all]", 0, 0)]
    [InlineData("[pass only float type]", 1, 1)]
    [InlineData("[do not pass]", 2, 2)]
    [InlineData("[pass only red team]", 3, 3)]
    [InlineData("[pass only blue team]", 4, 4)]
    [InlineData("[pass only character]", 5, 7)]
    public void SetPassTypeMapsAllSixKeywordsInBothOpenTagAndStringFormsForAlivePassivesOnly(string kw, int parsedCode, int deliveredCode)
    {
        ActProgram openForm = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL OUR TEAM]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[SET PASSTYPE]"), L(kw))));
        ActProgram strForm = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[SET PASSTYPE]", kw))));

        Assert.Equal(ActBehaviorKind.SetPassType, openForm.Behaviors[0].Items[0].Kind);
        Assert.Equal(parsedCode, openForm.Behaviors[0].Items[0].PassType);
        Assert.Equal(parsedCode, strForm.Behaviors[0].Items[0].PassType);

        var alivePassive = new RestEntity { Kind = ActEntityKind.Passive };
        var deadPassive = new RestEntity { Kind = ActEntityKind.Passive, IsAlive = false };
        var monster = new RestEntity { Kind = ActEntityKind.Monster };
        var host = new Host { ListDeadToo = true };
        host.Sets[ActSelector.AllOurTeam] = new List<IActEntity> { alivePassive, deadPassive, monster };

        new ActRunner(openForm, alivePassive, host).Update(0, 0.016f);
        Assert.Equal(deliveredCode, alivePassive.PassType);
        Assert.Equal(1, alivePassive.PassTypeCalls);
        Assert.Equal(0, deadPassive.PassTypeCalls);
        Assert.Equal(0, monster.PassTypeCalls);

        var passive2 = new RestEntity { Kind = ActEntityKind.Passive };
        new ActRunner(strForm, passive2, new Host()).Update(0, 0.016f);
        Assert.Equal(deliveredCode, passive2.PassType);
    }

    [Fact]
    public void SetPassTypeRejectsUnknownOrMissingKeyword()
    {
        Assert.Equal(ActBehaviorKind.Unsupported, P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[SET PASSTYPE]"), L("[pass unknown]")))).Behaviors[0].Items[0].Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[SET PASSTYPE]", "[pass unknown]")))).Behaviors[0].Items[0].Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[SET PASSTYPE]")))).Behaviors[0].Items[0].Kind);

        // 紧跟其后是别的已知行为标签([SET FRAME])而非穿透关键字时,[SET PASSTYPE] 不能吞掉下一条行为的开标签
        ActProgram followedByFrame = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[SET PASSTYPE]"), L("[SET FRAME]", 5))));
        Assert.Equal(2, followedByFrame.Behaviors[0].Items.Count);
        Assert.Equal(ActBehaviorKind.Unsupported, followedByFrame.Behaviors[0].Items[0].Kind);
        Assert.Equal(ActBehaviorKind.SetFrame, followedByFrame.Behaviors[0].Items[1].Kind);
        Assert.Equal(5, followedByFrame.Behaviors[0].Items[1].Frame);
    }

    // ------------------------------------------------------------------ [SET WIDTH]

    [Fact]
    public void SetWidthEvaluatesTwoActValuesOnceAndAppliesToAliveTargets()
    {
        ActProgram prog = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL OUR TEAM]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[SET WIDTH]", 143, 67))));

        var a = new RestEntity { Kind = ActEntityKind.Monster };
        var b = new RestEntity { Kind = ActEntityKind.Passive };
        var dead = new RestEntity { Kind = ActEntityKind.Passive, IsAlive = false };
        var host = new Host { ListDeadToo = true };
        host.Sets[ActSelector.AllOurTeam] = new List<IActEntity> { a, b, dead };

        new ActRunner(prog, a, host).Update(0, 0.016f);
        Assert.Equal(143, a.WidthX);
        Assert.Equal(67, a.WidthY);
        Assert.Equal(143, b.WidthX);
        Assert.Equal(67, b.WidthY);
        Assert.Equal(0, dead.WidthCalls);

        // 随机表达式也支持(闭合行尾随值)
        ActProgram randProg = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[SET WIDTH]"), L("[RANDOM SELECT]", 80, 90), L("[/RANDOM SELECT]", 44))));
        var c = new RestEntity { Kind = ActEntityKind.Passive };
        var rHost = new Host();
        rHost.Randoms.Enqueue(1);
        new ActRunner(randProg, c, rHost).Update(0, 0.016f);
        Assert.Equal(90, c.WidthX);
        Assert.Equal(44, c.WidthY);

        // 缺值不支持
        Assert.Equal(ActBehaviorKind.Unsupported, P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[SET WIDTH]")))).Behaviors[0].Items[0].Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[SET WIDTH]", 50)))).Behaviors[0].Items[0].Kind);
    }

    // ------------------------------------------------------------------ [CHANGE TEAM] & [DEFAULT TEAM]

    [Fact]
    public void ChangeTeamAndDefaultTeamUpdateTeamHpMaxAppendageAndCascadeCharacterChildren()
    {
        ActProgram change = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[CHARACTER]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[CHANGE TEAM]", 7, 3500, 40))));
        ActProgram restore = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[CHARACTER]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[DEFAULT TEAM]"))));

        var caster = new RestEntity { Kind = ActEntityKind.Monster };
        var hero = new RestEntity { Kind = ActEntityKind.Character };
        var heroPassiveSame = new RestEntity { Kind = ActEntityKind.Passive, OverrideActTeam = 0, Master = hero };
        var heroPassiveOtherTeam = new RestEntity { Kind = ActEntityKind.Passive, OverrideActTeam = 5, Master = hero };
        var heroMonsterSame = new RestEntity { Kind = ActEntityKind.Monster, OverrideActTeam = 0, Master = hero };
        var heroMonsterOtherTeam = new RestEntity { Kind = ActEntityKind.Monster, OverrideActTeam = 8, Master = hero };
        var otherMasterPassive = new RestEntity { Kind = ActEntityKind.Passive, OverrideActTeam = 0, Master = caster };
        var otherMasterMonster = new RestEntity { Kind = ActEntityKind.Monster, OverrideActTeam = 0, Master = caster };

        // 匹配子对象放在索引 0(确保循环从 i=0 起不漏首项),null 放在后续位置确保容错
        var host = new Host { ListDeadToo = true };
        host.Sets[ActSelector.Character] = new List<IActEntity> { hero };
        host.Sets[ActSelector.Passive] = new List<IActEntity> { heroPassiveSame, null, hero, heroPassiveOtherTeam, otherMasterPassive };
        host.Sets[ActSelector.Monster] = new List<IActEntity> { heroMonsterSame, null, heroPassiveSame, heroMonsterOtherTeam, otherMasterMonster };
        var spyHost = new ResolvePartySpyHost(host);

        // 1. 首次 [CHANGE TEAM]:切到 7,同原队子对象级联切 7,加 HpMax +40%(source == target)
        new ActRunner(change, caster, spyHost).Update(0, 0.016f);
        Assert.Contains((ActSelector.Passive, -1), spyHost.Calls);
        Assert.Contains((ActSelector.Monster, -1), spyHost.Calls);
        Assert.Equal(7, ((IActEntity)hero).Team);
        Assert.Equal(7, ((IActEntity)heroPassiveSame).Team);
        Assert.Equal(5, ((IActEntity)heroPassiveOtherTeam).Team);
        Assert.Equal(7, ((IActEntity)heroMonsterSame).Team);
        Assert.Equal(8, ((IActEntity)heroMonsterOtherTeam).Team);
        Assert.Equal(0, ((IActEntity)otherMasterPassive).Team);
        Assert.Equal(0, ((IActEntity)otherMasterMonster).Team);
        Assert.Single(hero.StatAdds);
        Assert.Equal(new StatChange(StatChangeTypes.HpMax, true, 40), hero.StatAdds[0].change);
        Assert.Equal(3500, hero.StatAdds[0].durationMs);
        Assert.Same(hero, hero.StatAdds[0].source);

        // 2. 已挂换队附加物时再调 [CHANGE TEAM]:TryBegin 返回 false,不覆盖也不重复挂
        ActProgram changeAgain = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[CHARACTER]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[CHANGE TEAM]", 9, 1000, 80))));
        new ActRunner(changeAgain, caster, host).Update(0, 0.016f);
        Assert.Equal(7, ((IActEntity)hero).Team);
        Assert.Single(hero.StatAdds);

        // 3. [DEFAULT TEAM]:恢复原队 0,级联恢复子对象,移除对应 HpMax 附加物
        new ActRunner(restore, caster, host).Update(0, 0.016f);
        Assert.Equal(0, ((IActEntity)hero).Team);
        Assert.Equal(0, ((IActEntity)heroPassiveSame).Team);
        Assert.Equal(5, ((IActEntity)heroPassiveOtherTeam).Team);
        Assert.Equal(0, ((IActEntity)heroMonsterSame).Team);
        Assert.Equal(8, ((IActEntity)heroMonsterOtherTeam).Team);
        Assert.Single(hero.StatRemoves);
        Assert.Equal(new StatChange(StatChangeTypes.HpMax, true, 40), hero.StatRemoves[0].change);
        Assert.Same(hero, hero.StatRemoves[0].source);

        // 4. 未挂时再调 [DEFAULT TEAM]:安全忽略
        int endBefore = hero.EndTeamCalls;
        hero.SetActTeam(12);
        new ActRunner(restore, caster, host).Update(0, 0.016f);
        Assert.Equal(endBefore + 1, hero.EndTeamCalls);
        Assert.Equal(12, ((IActEntity)hero).Team);
    }

    [Fact]
    public void ChangeTeamSkipsDeadAndPassiveTargetsDoesNotCascadeMonsterChildrenAndRejectsMissingArgs()
    {
        ActProgram change = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL OUR TEAM]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[CHANGE TEAM]", 4, 2000, 15))));
        ActProgram restore = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL OUR TEAM]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(L("[DEFAULT TEAM]"))));

        var mon = new RestEntity { Kind = ActEntityKind.Monster };
        var monChild = new RestEntity { Kind = ActEntityKind.Passive, OverrideActTeam = 100, Master = mon };
        var deadMon = new RestEntity { Kind = ActEntityKind.Monster, IsAlive = false };
        var host = new Host { ListDeadToo = true };
        host.Sets[ActSelector.AllOurTeam] = new List<IActEntity> { mon, monChild, deadMon };
        host.Sets[ActSelector.Passive] = new List<IActEntity> { monChild };

        new ActRunner(change, mon, host).Update(0, 0.016f);
        Assert.Equal(4, ((IActEntity)mon).Team);
        Assert.Equal(100, ((IActEntity)monChild).Team);      // 怪物不是 Character,不级联子被动对象
        Assert.Equal(0, monChild.BeginTeamCalls);
        Assert.Equal(100, ((IActEntity)deadMon).Team);
        Assert.Equal(0, deadMon.BeginTeamCalls);

        // 怪物死后 [DEFAULT TEAM] 跳过
        mon.IsAlive = false;
        new ActRunner(restore, mon, host).Update(0, 0.016f);
        Assert.Equal(0, mon.EndTeamCalls);

        // 复活后 [DEFAULT TEAM] 恢复怪物自身,不级联 monChild
        mon.IsAlive = true;
        monChild.SetActTeam(4);
        new ActRunner(restore, mon, host).Update(0, 0.016f);
        Assert.Equal(100, ((IActEntity)mon).Team);
        Assert.Equal(4, ((IActEntity)monChild).Team);

        // 缺参不支持
        Assert.Equal(ActBehaviorKind.Unsupported, P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[CHANGE TEAM]")))).Behaviors[0].Items[0].Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[CHANGE TEAM]", 1)))).Behaviors[0].Items[0].Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[CHANGE TEAM]", 1, 2000)))).Behaviors[0].Items[0].Kind);
    }
}
