using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// 引擎核心的合成测试:状态机、技能请求、计时事件、关键帧标志、动画结束、命中判定。
/// 脚本与数据都是测试自己写的,只模仿原版的约定(证据 E-035、E-036);引擎语义里推断的部分用名字标明。
/// </summary>
public sealed class EngineTests
{
    private const string FooScript = @"
function checkExecutableSkill_Foo(obj)
{
	if (!obj) return false;
	if (obj.sq_IsUseSkill(SKILL_FOO)) {
		obj.sq_IntVectClear();
		obj.sq_IntVectPush(0);
		obj.sq_AddSetStatePacket(STATE_FOO, STATE_PRIORITY_USER, true);
		return true;
	}
	return false;
}

function onSetState_Foo(obj, state, datas, isResetTimer)
{
	local sub = sq_GetVectorData(datas, 0);
	obj.setSkillSubState(sub);
	if (sub == 0) {
		obj.sq_SetCurrentAnimation(CUSTOM_ANI_FOO_START);
		obj.sq_SetCurrentAttackInfo(CUSTOM_ATTACK_INFO_FOO);
	}
	else if (sub == 1) {
		obj.sq_SetCurrentAnimation(CUSTOM_ANI_FOO_LOOP);
		obj.setTimeEvent(0, 100, 3, false);
	}
	else if (sub == 2) {
		obj.sq_SetCurrentAnimation(CUSTOM_ANI_FOO_END);
	}
}

function onEndCurrentAni_Foo(obj)
{
	local sub = obj.getSkillSubState();
	if (sub == 0) {
		obj.sq_IntVectClear();
		obj.sq_IntVectPush(1);
		obj.sq_AddSetStatePacket(STATE_FOO, STATE_PRIORITY_USER, true);
	}
	else if (sub == 2) {
		obj.sq_AddSetStatePacket(STATE_STAND, STATE_PRIORITY_USER, false);
	}
}

function onTimeEvent_Foo(obj, index, count)
{
	obj.getVar().push_vector(count);
	if (count == 3) {
		obj.sq_IntVectClear();
		obj.sq_IntVectPush(2);
		obj.sq_AddSetStatePacket(STATE_FOO, STATE_PRIORITY_USER, true);
		return true;
	}
	return false;
}

function onKeyFrameFlag_Foo(obj, flag)
{
	obj.getVar().push_vector(100 + flag);
	return true;
}
";

    private static Fixture FooFixture()
    {
        Fixture fx = World.Basic();
        fx.Ani("character/test/animation/foo_start.ani", new AniBuilder().Frame(50, 1).Frame(50, 2));
        fx.Ani("character/test/animation/foo_loop.ani", new AniBuilder().Loop().Frame(1000));
        fx.Ani("character/test/animation/foo_end.ani", new AniBuilder().Frame(80, 3));
        return fx.WithFooScript(FooScript);
    }

    private static GameWorld Create(Fixture fx)
    {
        GameWorld w = World.Create(fx.Build(), out ScriptLoadSummary summary);
        Assert.True(summary.EntryOk);
        Assert.Equal(1, summary.States);
        return w;
    }

    private static GameObject Hero(GameWorld w, int level = 2)
    {
        GameObject hero = w.SpawnCharacter(World.Job, "hero", 100, 100, 0, Direction.Right);
        hero.SkillLevels[World.SkillFoo] = level;
        return hero;
    }

    private static void Run(GameWorld w, int ms)
    {
        for (int i = 0; i < ms; i++)
        {
            w.Step(1);
        }
    }

    [Fact]
    public void CharacterStartsStandingWithTheWaitingAnimation()
    {
        using GameWorld w = Create(FooFixture());
        GameObject hero = Hero(w);
        Assert.Equal(0, hero.State);
        Assert.Equal("character/test/animation/stand.ani", hero.Animator.Path);
        Assert.Empty(w.Errors);
    }

    [Fact]
    public void SkillRequestRunsTheScriptedFlowFromStartThroughLoopAndEndBackToStand()
    {
        using GameWorld w = Create(FooFixture());
        GameObject hero = Hero(w);
        var states = new List<int> { hero.State };
        w.RequestSkill(hero, World.SkillFoo);
        for (int i = 0; i < 1000 && !(i > 10 && hero.State == 0); i++)
        {
            w.Step(1);
            if (hero.State != states[^1])
            {
                states.Add(hero.State);
            }
        }

        Assert.Empty(w.Errors);
        Assert.Equal(new[] { 0, World.StateFoo, 0 }, states);
        // 关键帧标志 1、2(起手动画)→ 三次计时事件(第 1、2、3 次)→ 标志 3(收尾动画)
        Assert.Equal(new long[] { 101, 102, 1, 2, 3, 103 }, hero.GetVar(null).Vector.ToArray());
        Assert.Equal("character/test/animation/stand.ani", hero.Animator.Path);
    }

    [Fact]
    public void TimeEventsFireAtTheirIntervalsAndStopWhenTheCallbackReturnsTrue()
    {
        using GameWorld w = Create(FooFixture());
        GameObject hero = Hero(w);
        w.RequestSkill(hero, World.SkillFoo);
        var times = new List<int>();
        int lastCount = 0;
        for (int i = 0; i < 700; i++)
        {
            w.Step(1);
            int n = hero.GetVar(null).Vector.Count;
            for (; lastCount < n; lastCount++)
            {
                times.Add(w.TimeMs);
            }
        }
        // 起手 100 ms 后进入循环,时间事件每 100 ms 一次:约在 200、300、400 ms;标志 101、102 在 1 与 50 ms 附近
        Assert.Equal(6, times.Count);
        Assert.InRange(times[2], 195, 215);
        Assert.InRange(times[3] - times[2], 99, 101);
        Assert.InRange(times[4] - times[3], 99, 101);
    }

    [Fact]
    public void SkillThatIsNotLearnedDoesNothing()
    {
        using GameWorld w = Create(FooFixture());
        GameObject hero = Hero(w, level: 0);
        w.RequestSkill(hero, World.SkillFoo);
        Run(w, 20);
        Assert.Equal(0, hero.State);
        Assert.Empty(w.Errors);
    }

    [Fact]
    public void RequestingAnotherSkillDoesNotTriggerThisOne()
    {
        using GameWorld w = Create(FooFixture());
        GameObject hero = Hero(w);
        w.RequestSkill(hero, 999);
        Run(w, 20);
        Assert.Equal(0, hero.State);
    }

    [Fact]
    public void AttackBoxHitsAnEnemyOnceAndAgainAfterTheHitListIsReset()
    {
        Fixture fx = World.Basic();
        // 起手动画:出招的一帧带攻击框(相对对象位置,x 向前 10..60、y 纵深 ±5、z 高 0..50)
        fx.Ani("character/test/animation/foo_start.ani", new AniBuilder().Frame(50, null, AniBuilder.Attack(10, -5, 0, 50, 10, 50)).Frame(50));
        fx.Ani("character/test/animation/foo_loop.ani", new AniBuilder().Loop().Frame(1000));
        fx.Ani("character/test/animation/foo_end.ani", new AniBuilder().Frame(80));
        fx.WithFooScript(FooScript + @"
function onAttack_Foo(obj, damager, boundingBox, isStuck)
{
	obj.getVar().push_vector(500);
	return 0;
}
");
        using GameWorld w = Create(fx);
        GameObject hero = Hero(w);
        // 假人在前方 50 处,受击框相对它自己 x ±10,y ±10,z 0..60
        GameObject dummy = w.SpawnDummy("dummy", 150, 100, 0, new Aabb(-10, 10, -10, 10, 0, 60));
        GameObject faraway = w.SpawnDummy("faraway", 400, 100, 0, new Aabb(-10, 10, -10, 10, 0, 60));
        w.RequestSkill(hero, World.SkillFoo);
        Run(w, 30);

        Assert.Empty(w.Errors);
        Assert.Single(w.Hits);
        Assert.Equal("dummy", w.Hits[0].Target);
        Assert.Equal("custom:0", w.Hits[0].AttackInfo);
        Assert.Equal(1, hero.GetVar(null).Vector.Count(v => v == 500));

        // 同一次攻击不重复命中;清空命中列表后再次命中
        Run(w, 5);
        Assert.Single(w.Hits);
        hero.HitObjects.Clear();
        Run(w, 5);
        Assert.Equal(2, w.Hits.Count);
        Assert.DoesNotContain(w.Hits, h => h.Target == faraway.Name);
        Assert.True(dummy.Valid);
    }

    [Fact]
    public void AttackBoxIsMirroredWhenFacingLeft()
    {
        Fixture fx = World.Basic();
        fx.Ani("character/test/animation/foo_start.ani", new AniBuilder().Frame(50, null, AniBuilder.Attack(10, -5, 0, 50, 10, 50)).Frame(50));
        fx.Ani("character/test/animation/foo_loop.ani", new AniBuilder().Loop().Frame(1000));
        fx.Ani("character/test/animation/foo_end.ani", new AniBuilder().Frame(80));
        fx.WithFooScript(FooScript);
        using GameWorld w = Create(fx);
        GameObject hero = w.SpawnCharacter(World.Job, "hero", 300, 100, 0, Direction.Left);
        hero.SkillLevels[World.SkillFoo] = 2;
        w.SpawnDummy("left", 250, 100, 0, new Aabb(-10, 10, -10, 10, 0, 60));
        w.SpawnDummy("right", 350, 100, 0, new Aabb(-10, 10, -10, 10, 0, 60));
        w.RequestSkill(hero, World.SkillFoo);
        Run(w, 30);
        Assert.Single(w.Hits);
        Assert.Equal("left", w.Hits[0].Target);
    }

    [Fact]
    public void ScriptErrorsAreRecordedNotThrown()
    {
        Fixture fx = World.Basic();
        fx.Ani("character/test/animation/foo_start.ani", new AniBuilder().Frame(50));
        fx.WithFooScript(@"
function checkExecutableSkill_Foo(obj) { local x = null; return x.boom(); }
");
        using GameWorld w = Create(fx);
        GameObject hero = Hero(w);
        w.RequestSkill(hero, World.SkillFoo);
        Run(w, 5);
        Assert.Single(w.Errors);
        Assert.Contains("checkExecutableSkill_Foo", w.Errors[0]);
        Assert.Equal(0, hero.State);
    }

    [Fact]
    public void CallbacksWithFewerParametersThanTheEngineSuppliesStillRun()
    {
        Fixture fx = World.Basic();
        fx.Ani("character/test/animation/foo_start.ani", new AniBuilder().Frame(50));
        fx.Ani("character/test/animation/foo_loop.ani", new AniBuilder().Loop().Frame(50));
        fx.WithFooScript(@"
function checkExecutableSkill_Foo(obj)
{
	if (obj.sq_IsUseSkill(SKILL_FOO)) {
		obj.sq_AddSetStatePacket(STATE_FOO, STATE_PRIORITY_USER, true);
		return true;
	}
	return false;
}
function onSetState_Foo(obj, state) { ::CALLED <- 1; obj.sq_SetCurrentAnimation(CUSTOM_ANI_FOO_LOOP); }
");
        using GameWorld w = Create(fx);
        GameObject hero = Hero(w);
        w.RequestSkill(hero, World.SkillFoo);
        Run(w, 5);
        Assert.Empty(w.Errors);
        Assert.Equal(World.StateFoo, hero.State);
        Assert.True(w.Vm.TryGetGlobalInteger("CALLED", out long called));
        Assert.Equal(1, called);
    }
}
