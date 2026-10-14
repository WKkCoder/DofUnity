using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>原生接口的合成测试:变量表、计时器、数学、技能数据、依附体。脚本是测试自己写的。</summary>
public sealed class NativeTests
{
    private static string StubAnis(Fixture fx)
    {
        fx.Ani("character/test/animation/foo_start.ani", new AniBuilder().Frame(50));
        fx.Ani("character/test/animation/foo_loop.ani", new AniBuilder().Loop().Frame(1000));
        fx.Ani("character/test/animation/foo_end.ani", new AniBuilder().Frame(80));
        return "";
    }

    /// <summary>onSetState_Foo 里跑 body,再触发技能,返回世界。body 里用 ::R_xxx 写结果。</summary>
    internal static (GameWorld World, GameObject Hero) Run(string body, Action<GameWorld, GameObject> prepare = null, Action<Fixture> extra = null)
    {
        Fixture fx = World.Basic();
        StubAnis(fx);
        extra?.Invoke(fx);
        fx.WithFooScript(@"
function checkExecutableSkill_Foo(obj)
{
	if (obj.sq_IsUseSkill(SKILL_FOO)) {
		obj.sq_AddSetStatePacket(STATE_FOO, STATE_PRIORITY_USER, true);
		return true;
	}
	return false;
}
function onSetState_Foo(obj, state, datas, isResetTimer)
{
" + body + @"
}
");
        GameWorld w = World.Create(fx.Build(), out ScriptLoadSummary summary);
        Assert.True(summary.EntryOk);
        GameObject hero = w.SpawnCharacter(World.Job, "hero", 100, 100, 0, Direction.Right);
        hero.SkillLevels[World.SkillFoo] = 2;
        prepare?.Invoke(w, hero);
        w.RequestSkill(hero, World.SkillFoo);
        w.Step(1);
        return (w, hero);
    }

    internal static long Result(GameWorld w, string name)
    {
        Assert.True(w.Vm.TryGetGlobalInteger(name, out long v), "没有结果变量 " + name + ";脚本错误:" + string.Join(" | ", w.Errors));
        return v;
    }

    [Fact]
    public void VarStoreScalarsAndVectors()
    {
        (GameWorld w, _) = Run(@"
	local v = obj.getVar();
	v.setInt(1, 42);        ::R_INT <- v.getInt(1);
	::R_INT_UNSET <- v.getInt(99);
	v.setBool(2, true);     ::R_BOOL <- v.getBool(2) ? 1 : 0;
	::R_BOOL_UNSET <- v.getBool(98) ? 1 : 0;
	v.setFloat(3, 1.5);     ::R_FLOAT100 <- (v.getFloat(3) * 100).tointeger();
	v.push_vector(5); v.push_vector(6); v.set_vector(4, 9);
	::R_VECSIZE <- v.size_vector();
	::R_V0 <- v.get_vector(0);
	::R_V4 <- v.get_vector(4);
	::R_V_OUT <- v.get_vector(100);
	v.clear_vector();
	::R_VECSIZE_AFTER <- v.size_vector();
");
        Assert.Empty(w.Errors);
        Assert.Equal(42, Result(w, "R_INT"));
        Assert.Equal(0, Result(w, "R_INT_UNSET"));
        Assert.Equal(1, Result(w, "R_BOOL"));
        Assert.Equal(0, Result(w, "R_BOOL_UNSET"));
        Assert.Equal(150, Result(w, "R_FLOAT100"));
        Assert.Equal(5, Result(w, "R_VECSIZE"));
        Assert.Equal(5, Result(w, "R_V0"));
        Assert.Equal(9, Result(w, "R_V4"));
        Assert.Equal(0, Result(w, "R_V_OUT"));
        Assert.Equal(0, Result(w, "R_VECSIZE_AFTER"));
    }

    [Fact]
    public void NamedVarStoresAreSeparateAndSqVarIsTheDefaultOne()
    {
        (GameWorld w, GameObject hero) = Run(@"
	obj.getVar().setInt(1, 10);
	obj.getVar(""other"").setInt(1, 20);
	::R_DEFAULT <- obj.getVar().getInt(1);
	::R_OTHER <- obj.getVar(""other"").getInt(1);
	::R_SQVAR <- obj.sq_var.getInt(1);
	obj.sq_var.setInt(2, 33);
	::R_BACK <- obj.getVar().getInt(2);
	::R_SAME <- isSameObject(obj.getVar(), obj.sq_var) ? 1 : 0;
");
        Assert.Empty(w.Errors);
        Assert.Equal(10, Result(w, "R_DEFAULT"));
        Assert.Equal(20, Result(w, "R_OTHER"));
        Assert.Equal(10, Result(w, "R_SQVAR"));
        Assert.Equal(33, Result(w, "R_BACK"));
        Assert.Equal(1, Result(w, "R_SAME"));
        Assert.Equal(33, hero.GetVar(null).GetInt(2));
    }

    [Fact]
    public void ObjectVectorHoldsTheSameObjectsTheHostPushed()
    {
        GameObject dummy = null;
        (GameWorld w, GameObject hero) = Run(@"
	local v = obj.getVar();
	::R_SIZE <- v.get_obj_vector_size();
	local d = v.get_obj_vector(0);
	::R_FOUND <- d ? 1 : 0;
	::R_SAME_POS <- (d.getXPos() == 150) ? 1 : 0;
	::R_IS <- v.is_obj_vector(d) ? 1 : 0;
	::R_INDEX <- v.get_obj_vector_index(d);
	v.push_obj_vector(obj);
	::R_SIZE2 <- v.get_obj_vector_size();
	v.remove_obj_vector(d);
	::R_SIZE3 <- v.get_obj_vector_size();
	::R_IS_AFTER <- v.is_obj_vector(d) ? 1 : 0;
	::R_NULL <- v.get_obj_vector(5) == null ? 1 : 0;
	v.clear_obj_vector();
	::R_SIZE4 <- v.get_obj_vector_size();
", (world, h) =>
        {
            dummy = world.SpawnDummy("dummy", 150, 100, 0, new Aabb(-10, 10, -10, 10, 0, 60));
            h.GetVar(null).ObjVector.Add(dummy);
        });
        Assert.Empty(w.Errors);
        Assert.Equal(1, Result(w, "R_SIZE"));
        Assert.Equal(1, Result(w, "R_FOUND"));
        Assert.Equal(1, Result(w, "R_SAME_POS"));
        Assert.Equal(1, Result(w, "R_IS"));
        Assert.Equal(0, Result(w, "R_INDEX"));
        Assert.Equal(2, Result(w, "R_SIZE2"));
        Assert.Equal(1, Result(w, "R_SIZE3"));
        Assert.Equal(0, Result(w, "R_IS_AFTER"));
        Assert.Equal(1, Result(w, "R_NULL"));
        Assert.Equal(0, Result(w, "R_SIZE4"));
        Assert.NotNull(dummy);
        Assert.NotNull(hero);
    }

    [Fact]
    public void ObjectVectorDropsDestroyedObjectsFromTheNextStepOn()
    {
        Fixture fx = World.Basic();
        StubAnis(fx);
        fx.WithFooScript(@"
function checkExecutableSkill_Foo(obj)
{
	if (obj.sq_IsUseSkill(SKILL_FOO)) {
		obj.sq_AddSetStatePacket(STATE_FOO, STATE_PRIORITY_USER, true);
		return true;
	}
	return false;
}
function onSetState_Foo(obj, state, datas, isResetTimer)
{
	local v = obj.getVar();
	v.get_obj_vector(0).setValid(false);
	::R_SAME_SIZE <- v.get_obj_vector_size();
	::R_SAME_GET <- v.get_obj_vector(0) == null ? 0 : 1;
}
function onProc_Foo(obj)
{
	local v = obj.getVar();
	::R_LATER_SIZE <- v.get_obj_vector_size();
	::R_LATER_GET <- v.get_obj_vector(0) == null ? 1 : 0;
}
");
        GameWorld w = World.Create(fx.Build(), out ScriptLoadSummary summary);
        Assert.True(summary.EntryOk);
        GameObject hero = w.SpawnCharacter(World.Job, "hero", 100, 100, 0, Direction.Right);
        hero.SkillLevels[World.SkillFoo] = 2;
        GameObject dummy = w.SpawnDummy("dummy", 150, 100, 0, new Aabb(-10, 10, -10, 10, 0, 60));
        hero.GetVar(null).ObjVector.Add(dummy);
        w.RequestSkill(hero, World.SkillFoo);
        w.Step(1);
        Assert.Empty(w.Errors);

        // 销毁发生的同一个时刻内,对象还在向量里(推断:一个回调里按下标遍历时不会少元素)
        Assert.False(dummy.Valid);
        Assert.Equal(1, Result(w, "R_SAME_SIZE"));
        Assert.Equal(1, Result(w, "R_SAME_GET"));

        // 之后的时刻:已销毁的对象被清出向量,大小为 0,取第 0 个是 null(ChainLightning 靠'向量变空'知道光束全部消失)
        w.Step(20);
        Assert.Empty(w.Errors);
        Assert.Equal(0, Result(w, "R_LATER_SIZE"));
        Assert.Equal(1, Result(w, "R_LATER_GET"));
    }

    [Fact]
    public void SkillDataComesFromTheSkillFileByLevel()
    {
        (GameWorld w, _) = Run(@"
	::R_STATIC0 <- sq_GetIntData(obj, SKILL_FOO, 0);
	::R_STATIC1 <- obj.sq_GetIntData(SKILL_FOO, 1);
	::R_STATIC_OUT <- sq_GetIntData(obj, SKILL_FOO, 9);
	::R_LEVEL <- sq_GetSkillLevel(obj, SKILL_FOO);
	::R_LV_COL0 <- sq_GetLevelData(obj, SKILL_FOO, 0, 2);
	::R_LV_COL1 <- sq_GetLevelData(obj, SKILL_FOO, 1, 2);
	::R_LV_CUR <- obj.sq_GetLevelData(SKILL_FOO, 0);
	::R_LV_STATE <- obj.sq_GetLevelData(1);
	::R_LV_CLAMP <- sq_GetLevelData(obj, SKILL_FOO, 0, 50);
	::R_BONUS <- (obj.sq_GetBonusRateWithPassive(SKILL_FOO, STATE_FOO, 1, 1.0) * 10).tointeger();
	::R_UNKNOWN_SKILL <- sq_GetLevelData(obj, 999, 0, 1);
");
        Assert.Empty(w.Errors);
        Assert.Equal(300, Result(w, "R_STATIC0"));
        Assert.Equal(150, Result(w, "R_STATIC1"));
        Assert.Equal(0, Result(w, "R_STATIC_OUT"));
        Assert.Equal(2, Result(w, "R_LEVEL"));
        // 数据表:两列、三级:(100,5)(120,7)(140,9)
        Assert.Equal(120, Result(w, "R_LV_COL0"));
        Assert.Equal(7, Result(w, "R_LV_COL1"));
        Assert.Equal(120, Result(w, "R_LV_CUR"));
        Assert.Equal(7, Result(w, "R_LV_STATE"));
        Assert.Equal(140, Result(w, "R_LV_CLAMP"));
        Assert.Equal(70, Result(w, "R_BONUS"));
        Assert.Equal(0, Result(w, "R_UNKNOWN_SKILL"));
    }

    [Fact]
    public void MathHelpers()
    {
        (GameWorld w, _) = Run(@"
	::R_FRONT_R <- sq_GetDistancePos(100, ENUM_DIRECTION_RIGHT, 30);
	::R_FRONT_L <- sq_GetDistancePos(100, ENUM_DIRECTION_LEFT, 30);
	::R_FRONT_N <- sq_GetDistancePos(100, ENUM_DIRECTION_NEUTRAL, 30);
	::R_OPP_L <- sq_GetOppositeDirection(ENUM_DIRECTION_LEFT);
	::R_OPP_R <- sq_GetOppositeDirection(ENUM_DIRECTION_RIGHT);
	::R_OPP_N <- sq_GetOppositeDirection(ENUM_DIRECTION_NEUTRAL);
	::R_SH0 <- sq_GetShuttleValue(0, 10, 0, 50);
	::R_SH_MID <- sq_GetShuttleValue(0, 10, 25, 50);
	::R_SH_Q <- sq_GetShuttleValue(0, 10, 12, 50);
	::R_SH_WRAP <- sq_GetShuttleValue(0, 10, 50, 50);
	::R_UNI <- sq_GetUniformVelocity(100, 200, 50, 100);
	::R_UNI_END <- sq_GetUniformVelocity(100, 200, 500, 100);
	::R_ABS <- sq_Abs(-7);
	local r = sq_getRandom(3, 5);
	::R_RAND_OK <- (r >= 3 && r <= 5) ? 1 : 0;
");
        Assert.Empty(w.Errors);
        Assert.Equal(130, Result(w, "R_FRONT_R"));
        Assert.Equal(70, Result(w, "R_FRONT_L"));
        Assert.Equal(100, Result(w, "R_FRONT_N"));
        Assert.Equal(Direction.Right, Result(w, "R_OPP_L"));
        Assert.Equal(Direction.Left, Result(w, "R_OPP_R"));
        Assert.Equal(Direction.Neutral, Result(w, "R_OPP_N"));
        Assert.Equal(0, Result(w, "R_SH0"));
        Assert.Equal(10, Result(w, "R_SH_MID"));
        Assert.Equal(5, Result(w, "R_SH_Q"));
        Assert.Equal(0, Result(w, "R_SH_WRAP"));
        Assert.Equal(150, Result(w, "R_UNI"));
        Assert.Equal(200, Result(w, "R_UNI_END"));
        Assert.Equal(7, Result(w, "R_ABS"));
        Assert.Equal(1, Result(w, "R_RAND_OK"));
    }

    [Fact]
    public void PositionAndDirectionAccessorsWorkInMemberAndGlobalForms()
    {
        (GameWorld w, GameObject hero) = Run(@"
	::R_X <- obj.getXPos();
	::R_X_G <- sq_GetXPos(obj);
	::R_Y <- obj.getYPos();
	::R_Z <- obj.getZPos();
	::R_DIR <- obj.getDirection();
	sq_SetCurrentPos(obj, 11, 22, 33);
	::R_X2 <- obj.getXPos();
	obj.setCurrentDirection(ENUM_DIRECTION_LEFT);
	::R_DIR2 <- sq_GetDirection(obj);
	::R_STATE <- obj.sq_GetState();
	::R_MY <- obj.isMyControlObject() ? 1 : 0;
");
        Assert.Empty(w.Errors);
        Assert.Equal(100, Result(w, "R_X"));
        Assert.Equal(100, Result(w, "R_X_G"));
        Assert.Equal(100, Result(w, "R_Y"));
        Assert.Equal(0, Result(w, "R_Z"));
        Assert.Equal(Direction.Right, Result(w, "R_DIR"));
        Assert.Equal(11, Result(w, "R_X2"));
        Assert.Equal(Direction.Left, Result(w, "R_DIR2"));
        Assert.Equal(World.StateFoo, Result(w, "R_STATE"));
        Assert.Equal(1, Result(w, "R_MY"));
        Assert.Equal((11, 22, 33), (hero.X, hero.Y, hero.Z));
        Assert.Equal(Direction.Left, hero.Direction);
    }

    [Fact]
    public void StaticMoveAdvancesThePositionByTheGivenSpeed()
    {
        (GameWorld w, GameObject hero) = Run(@"
	obj.sq_SetStaticMoveInfo(0, 200, 100, false);
	obj.sq_SetMoveDirection(obj.getDirection(), ENUM_DIRECTION_NEUTRAL);
");
        Assert.Empty(w.Errors);
        for (int i = 0; i < 500; i++)
        {
            w.Step(1);
        }
        // 速度 200/秒,走了约 0.5 秒;最初那 1 ms 也在内
        Assert.InRange(hero.X, 100 + 99, 100 + 102);
        Assert.Equal(100, hero.Y);
    }

    [Fact]
    public void TimerObjectsCountTimeAndFirePeriodicEvents()
    {
        (GameWorld w, GameObject hero) = Run(@"
	local t = obj.getTimer();
	t.Reset();
	t.setParameter(100, 3);
	obj.getVar().push_timer_vector();
	::R_SAME_TIMER <- isSameObject(obj.getTimer(), obj.timer_) ? 1 : 0;
	::R_SQ_TIMER <- isSameObject(obj.getTimer(), obj.sq_timer_) ? 1 : 0;
	::R_VEC <- obj.getVar().get_timer_vector(0) ? 1 : 0;
");
        Assert.Empty(w.Errors);
        Assert.Equal(1, Result(w, "R_SAME_TIMER"));
        Assert.Equal(1, Result(w, "R_SQ_TIMER"));
        Assert.Equal(1, Result(w, "R_VEC"));

        GameTimer timer = hero.Timer;
        int events = 0;
        for (int i = 0; i < 500; i++)
        {
            w.Step(1);
            if (timer.IsOnEvent(timer.Get()))
            {
                events++;
            }
        }
        // 参数 (100 毫秒, 3 次):在约 100、200、300 毫秒各一次,之后不再触发
        Assert.Equal(3, events);
        Assert.InRange(timer.Get(), 495, 505);
    }

    [Fact]
    public void ParametersWrittenByStatePacketsStartTheTimerWhenResetIsRequested()
    {
        (GameWorld w, GameObject hero) = Run("");
        for (int i = 0; i < 100; i++)
        {
            w.Step(1);
        }
        long before = hero.Timer.Get();
        Assert.InRange(before, 95, 105);
    }

    [Fact]
    public void AppendageScriptsDefineTheSameGlobalNamesAndEachCreationUsesItsOwn()
    {
        const string apA = @"
function sq_AddFunctionName(appendage)
{
	appendage.sq_AddFunctionName(""proc"", ""proc_a"");
	appendage.sq_AddFunctionName(""onStart"", ""start_a"");
	appendage.sq_AddFunctionName(""onEnd"", ""end_a"");
}
function sq_AddEffect(appendage) {}
function start_a(appendage) { appendage.getParent().getVar().push_vector(1); }
function proc_a(appendage) { appendage.getParent().getVar().push_vector(2); }
function end_a(appendage) { appendage.getParent().getVar().push_vector(3); }
";
        const string apB = @"
function sq_AddFunctionName(appendage)
{
	appendage.sq_AddFunctionName(""proc"", ""proc_b"");
	appendage.sq_AddFunctionName(""onStart"", ""start_b"");
	appendage.sq_AddFunctionName(""onEnd"", ""end_b"");
}
function sq_AddEffect(appendage) {}
function start_b(appendage) { appendage.getParent().getVar().push_vector(11); }
function proc_b(appendage) { appendage.getParent().getVar().push_vector(12); }
function end_b(appendage) { appendage.getParent().getVar().push_vector(13); }
";
        GameObject dummy = null;
        (GameWorld w, GameObject hero) = Run(@"
	local d = obj.getVar().get_obj_vector(0);
	local a = CNSquirrelAppendage.sq_AppendAppendage(d, obj, SKILL_FOO, false, ""test/ap_a.nut"", true);
	::R_A <- a ? 1 : 0;
	local b = CNSquirrelAppendage.sq_AppendAppendage(d, obj, SKILL_FOO, false, ""test/ap_b.nut"", true);
	::R_B <- b ? 1 : 0;
	::R_DUP <- isSameObject(a, CNSquirrelAppendage.sq_AppendAppendage(d, obj, SKILL_FOO, false, ""test/ap_a.nut"", true)) ? 1 : 0;
	::R_IS <- CNSquirrelAppendage.sq_IsAppendAppendage(d, ""test/ap_b.nut"") ? 1 : 0;
	::R_SRC <- isSameObject(a.getSource(), obj) ? 1 : 0;
	::R_PARENT <- isSameObject(a.getParent(), d) ? 1 : 0;
	CNSquirrelAppendage.sq_RemoveAppendage(d, ""test/ap_a.nut"");
	::R_STILL_A <- CNSquirrelAppendage.sq_IsAppendAppendage(d, ""test/ap_a.nut"") ? 1 : 0;
", (world, h) =>
        {
            dummy = world.SpawnDummy("dummy", 150, 100, 0, new Aabb(-10, 10, -10, 10, 0, 60));
            h.GetVar(null).ObjVector.Add(dummy);
        }, fx =>
        {
            fx.Nut("sqr/test/ap_a.nut", apA);
            fx.Nut("sqr/test/ap_b.nut", apB);
        });
        Assert.Empty(w.Errors);
        Assert.Equal(1, Result(w, "R_A"));
        Assert.Equal(1, Result(w, "R_B"));
        Assert.Equal(1, Result(w, "R_DUP"));
        Assert.Equal(1, Result(w, "R_IS"));
        Assert.Equal(1, Result(w, "R_SRC"));
        Assert.Equal(1, Result(w, "R_PARENT"));
        Assert.Equal(0, Result(w, "R_STILL_A"));
        Assert.Single(dummy.Appendages);

        // 各自用自己的函数:a 启动(1)→ b 启动(11)→ 移除 a 时 a 的 onEnd(3)→ 同一步里 b 的 proc(12);a 的 proc 不会再出现
        Assert.Equal(new long[] { 1, 11, 3, 12 }, dummy.GetVar(null).Vector.ToArray());
        w.Step(1);
        Assert.Equal(new long[] { 1, 11, 3, 12, 12 }, dummy.GetVar(null).Vector.ToArray());
    }

    [Fact]
    public void AppendageEndsWhenItsParentSourceIsGoneOrItIsSetInvalid()
    {
        const string ap = @"
function sq_AddFunctionName(appendage)
{
	appendage.sq_AddFunctionName(""proc"", ""proc_x"");
	appendage.sq_AddFunctionName(""onEnd"", ""end_x"");
}
function sq_AddEffect(appendage) {}
function proc_x(appendage)
{
	local n = appendage.getVar().getInt(0) + 1;
	appendage.getVar().setInt(0, n);
	if (n >= 3) appendage.setValid(false);
}
function end_x(appendage) { appendage.getParent().getVar().push_vector(77); }
";
        GameObject dummy = null;
        (GameWorld w, _) = Run(@"
	local d = obj.getVar().get_obj_vector(0);
	CNSquirrelAppendage.sq_AppendAppendage(d, obj, SKILL_FOO, false, ""test/ap_x.nut"", true);
", (world, h) =>
        {
            dummy = world.SpawnDummy("dummy", 150, 100, 0, new Aabb(-10, 10, -10, 10, 0, 60));
            h.GetVar(null).ObjVector.Add(dummy);
        }, fx => fx.Nut("sqr/test/ap_x.nut", ap));
        Assert.Empty(w.Errors);
        // 创建那一步里依附体已经 proc 过一次(n = 1);再两次 proc 后(n = 3)脚本把自己设为无效,依附体结束
        Assert.Single(dummy.Appendages);
        w.Step(1);
        Assert.Single(dummy.Appendages);
        w.Step(1);
        Assert.Empty(dummy.Appendages);
        Assert.Equal(new long[] { 77 }, dummy.GetVar(null).Vector.ToArray());
    }

    [Fact]
    public void ForcedHitPacketRecordsAHitAndCallsOnAttack()
    {
        GameObject dummy = null;
        (GameWorld w, GameObject hero) = Run(@"
	obj.sq_SetCurrentAttackInfo(CUSTOM_ATTACK_INFO_FOO);
	local info = sq_GetCurrentAttackInfo(obj);
	sq_SetCurrentAttackBonusRate(info, 250.0);
	local d = obj.getVar().get_obj_vector(0);
	sq_SendHitObjectPacket(obj, d, 0, 0, 0);
	::R_SENT <- 1;
", (world, h) =>
        {
            dummy = world.SpawnDummy("dummy", 150, 100, 0, new Aabb(-10, 10, -10, 10, 0, 60));
            h.GetVar(null).ObjVector.Add(dummy);
        });
        Assert.Empty(w.Errors);
        Assert.Equal(1, Result(w, "R_SENT"));
        Assert.Single(w.Hits);
        Assert.True(w.Hits[0].Forced);
        Assert.Equal("dummy", w.Hits[0].Target);
        Assert.Equal(250f, w.Hits[0].BonusRate!.Value);
        Assert.NotNull(hero);
    }

    [Fact]
    public void UnknownObjectMembersAreStubbedAndCounted()
    {
        (GameWorld w, _) = Run(@"
	obj.someFutureMethod(1, 2);
	obj.someFutureMethod();
	obj.anotherOne();
	::R_AFTER <- 1;
");
        Assert.Empty(w.Errors);
        Assert.Equal(1, Result(w, "R_AFTER"));
        Assert.Equal(2, w.UnknownMembers["someFutureMethod"]);
        Assert.Equal(1, w.UnknownMembers["anotherOne"]);
    }

    [Fact]
    public void ParticleAndSoundCallsBecomeWorldEvents()
    {
        var events = new RecordingEvents();
        (GameWorld w, _) = Run(@"
	obj.sq_PlaySound(""SND_KEY"");
	sq_CreateParticle(""effect/a.ptl"", obj, 1, 2, 3, true, 100, 500, 100);
	sq_RemoveParticle(""effect/a.ptl"", obj);
	sq_SetMyShake(obj, 2, 120);
", (world, h) => world.Events = events);
        Assert.Empty(w.Errors);
        Assert.Equal(new[] { "sound SND_KEY", "particle effect/a.ptl 1,2,3", "remove effect/a.ptl", "shake 2 120" }, events.Log);
    }

    private sealed class RecordingEvents : IWorldEvents
    {
        public List<string> Log { get; } = new();

        public void PlaySound(GameObject source, string soundKey) => Log.Add("sound " + soundKey);

        public void CreateParticle(GameObject source, string path, int x, int y, int z) => Log.Add("particle " + path + " " + x + "," + y + "," + z);

        public void RemoveParticle(GameObject source, string path) => Log.Add("remove " + path);

        public void Shake(GameObject source, int strength, int durationMs) => Log.Add("shake " + strength + " " + durationMs);
    }
}
