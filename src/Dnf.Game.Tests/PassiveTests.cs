using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// 被动对象与二进制消息的合成测试:数据按小端写入、读取端有符号,创建后调用 setCustomData / setState / procAppend,
/// 销毁时调用 onDestroyObject,父子关系与按编号查询。脚本都是测试自己写的。
/// </summary>
public sealed class PassiveTests
{
    private const int PassiveId = 24001;

    private static (GameWorld World, GameObject Hero) Build(string heroBody, string passiveScript, string heroExtra = "")
    {
        Fixture fx = World.Basic();
        fx.Ani("character/test/animation/foo_start.ani", new AniBuilder().Frame(50));
        fx.Ani("character/test/animation/foo_loop.ani", new AniBuilder().Loop().Frame(1000));
        fx.Ani("character/test/animation/foo_end.ani", new AniBuilder().Frame(80));
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
" + heroBody + @"
}
" + heroExtra + @"
");
        // 在 WithFooScript 的登记链上再登记一个被动对象脚本
        fx.Nut("sqr/loadstate.nut", @"
IRDSQRCharacter.pushScriptFiles(""test/header.nut"");
IRDSQRCharacter.pushState(JOB_TEST, ""test/foo.nut"", ""Foo"", STATE_FOO, SKILL_FOO);
IRDSQRCharacter.pushPassiveObj(""test/po_bar.nut"", 24001);
");
        fx.Nut("sqr/test/po_bar.nut", passiveScript);
        fx.List("passiveobject/passiveobject.lst", (PassiveId, "character/test/bar.obj"));
        fx.Tokens("passiveobject/character/test/bar.obj", w => w
            .Tag("[basic motion]").Str("animation/bar.ani")
            .Tag("[attack info]").Str("attackinfo/bar.atk"));
        fx.Ani("passiveobject/character/test/animation/bar.ani", new AniBuilder().Frame(50).Frame(50));
        fx.Tokens("passiveobject/character/test/attackinfo/bar.atk", w => w.Tag("[damage type]").Str("[physical]"));

        GameWorld w = World.Create(fx.Build(), out ScriptLoadSummary summary);
        Assert.True(summary.EntryOk);
        GameObject hero = w.SpawnCharacter(World.Job, "hero", 100, 100, 0, Direction.Right);
        hero.SkillLevels[World.SkillFoo] = 1;
        w.RequestSkill(hero, World.SkillFoo);
        w.Step(1);
        return (w, hero);
    }

    private static long Result(GameWorld w, string name) => NativeTests.Result(w, name);

    private static GameObject FindPassive(GameWorld w)
    {
        return w.Objects.FirstOrDefault(o => o.Kind == ObjectKind.PassiveObject && o.PassiveId == PassiveId);
    }

    [Fact]
    public void PassiveObjectIsCreatedAfterTheCallbackWithTheWrittenDataInPlace()
    {
        (GameWorld w, GameObject hero) = Build(@"
	sq_BinaryStartWrite();
	sq_BinaryWriteDword(1234);
	sq_BinaryWriteDword(-5);
	sq_BinaryWriteWord(65535);
	sq_BinaryWriteByte(250);
	sq_BinaryWriteFloat(1.5);
	sq_SendCreatePassiveObjectPacket(obj, 24001, 0, 30, 5, 7, ENUM_DIRECTION_RIGHT);
	::R_BEFORE_CREATION <- (obj.sq_GetPassiveObject(24001) == null) ? 1 : 0;
", @"
function setCustomData_po_Bar(obj, reciveData)
{
	::R_DWORD <- reciveData.readDword();
	::R_NEG <- reciveData.readDword();
	::R_WORD <- reciveData.readWord();
	::R_BYTE <- reciveData.readByte();
	::R_FLOAT100 <- (reciveData.readFloat() * 100).tointeger();
	::R_PAST_END <- reciveData.readDword();
	::R_TOP_IS_PARENT <- (obj.getTopCharacter() == obj.getParent()) ? 1 : 0;
}
");
        Assert.Empty(w.Errors);
        Assert.Equal(1, Result(w, "R_BEFORE_CREATION")); // 创建消息在发出它的回调结束后才处理
        Assert.Equal(1234, Result(w, "R_DWORD"));
        Assert.Equal(-5, Result(w, "R_NEG"));
        Assert.Equal(65535, Result(w, "R_WORD"));
        Assert.Equal(250, Result(w, "R_BYTE"));
        Assert.Equal(150, Result(w, "R_FLOAT100"));
        Assert.Equal(0, Result(w, "R_PAST_END")); // 读过头得 0
        Assert.Equal(1, Result(w, "R_TOP_IS_PARENT"));

        GameObject po = FindPassive(w);
        Assert.NotNull(po);
        Assert.Equal((130, 105, 7), (po.X, po.Y, po.Z));
        Assert.Same(hero, po.Parent);
        Assert.Equal("passiveobject/character/test/animation/bar.ani", po.Animator.Path);
        Assert.NotNull(po.CurrentAttackInfo);
    }

    [Fact]
    public void LeftFacingCreatorMirrorsTheOffsetButNeutralDirectionDoesNot()
    {
        (GameWorld w, GameObject hero) = Build(@"
	sq_SendCreatePassiveObjectPacket(obj, 24001, 0, 30, 0, 0, ENUM_DIRECTION_LEFT);
	sq_SendCreatePassiveObjectPacket(obj, 24001, 0, 30, 0, 0, ENUM_DIRECTION_NEUTRAL);
", @"function setCustomData_po_Bar(obj, reciveData) {}");
        Assert.Empty(w.Errors);
        List<GameObject> made = w.Objects.Where(o => o.Kind == ObjectKind.PassiveObject).OrderBy(o => o.Id).ToList();
        Assert.Equal(2, made.Count);
        Assert.Equal(70, made[0].X);   // 朝左:向前的偏移镜像
        Assert.Equal(Direction.Left, made[0].Direction);
        Assert.Equal(130, made[1].X);  // NEUTRAL:偏移不镜像,朝向取创建者的
        Assert.Equal(hero.Direction, made[1].Direction);
    }

    [Fact]
    public void PassiveObjectRunsProcEveryStepAndCanDestroyItself()
    {
        (GameWorld w, _) = Build(@"
	sq_SendCreatePassiveObjectPacket(obj, 24001, 0, 0, 0, 0, ENUM_DIRECTION_RIGHT);
", @"
function setCustomData_po_Bar(obj, reciveData) {}
function procAppend_po_Bar(obj)
{
	local v = obj.getVar();
	v.setInt(0, v.getInt(0) + 1);
	::R_PROCS <- v.getInt(0);
	if (v.getInt(0) >= 3)
		sq_SendDestroyPacketPassiveObject(obj);
}
function onDestroyObject_po_Bar(obj, object)
{
	::R_DESTROYED <- 1;
}
");
        Assert.NotNull(FindPassive(w));
        w.Step(10);
        w.Step(10);
        w.Step(10);
        w.Step(10);
        Assert.Empty(w.Errors);
        Assert.Equal(3, Result(w, "R_PROCS"));
        Assert.Equal(1, Result(w, "R_DESTROYED"));
        Assert.Null(FindPassive(w));
    }

    [Fact]
    public void CreatorFindsItsPassiveObjectsByIdAndIndexInCreationOrder()
    {
        (GameWorld w, GameObject hero) = Build(@"
	sq_SendCreatePassiveObjectPacket(obj, 24001, 0, 10, 0, 0, ENUM_DIRECTION_RIGHT);
	sq_SendCreatePassiveObjectPacket(obj, 24001, 0, 20, 0, 0, ENUM_DIRECTION_RIGHT);
	obj.setTimeEvent(0, 20, 1, false);
", @"function setCustomData_po_Bar(obj, reciveData) {}", @"
function onTimeEvent_Foo(obj, timeEventIndex, timeEventCount)
{
	local a = obj.getMyPassiveObject(24001, 0);
	local b = sq_GetPassiveObject(obj, 24001, 1);
	local c = obj.getMyPassiveObject(24001, 2);
	local d = obj.getMyPassiveObject(99999, 0);
	::R_A_X <- a.getXPos();
	::R_B_X <- b.getXPos();
	::R_C_NULL <- (c == null) ? 1 : 0;
	::R_D_NULL <- (d == null) ? 1 : 0;
	return true;
}
");
        w.Step(30);
        Assert.Empty(w.Errors);
        Assert.Equal(110, Result(w, "R_A_X")); // 第 0 个 = 先创建的
        Assert.Equal(120, Result(w, "R_B_X"));
        Assert.Equal(1, Result(w, "R_C_NULL")); // 只有两个
        Assert.Equal(1, Result(w, "R_D_NULL")); // 没有这个编号
        Assert.All(w.Objects.Where(o => o.Kind == ObjectKind.PassiveObject), o => Assert.Same(hero, o.Parent));
    }

    [Fact]
    public void DefineAnyNativesTakeTheObjectInBothForms()
    {
        var events = new ShakeEvents();
        Fixture fx = World.Basic();
        fx.Ani("character/test/animation/foo_start.ani", new AniBuilder().Frame(50));
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
	sq_SetShake(obj, 6, 400);
	obj.sq_SetShake(obj, 3, 100);
	obj.sq_setShake(obj, 2, 50);
	sq_SetSkillSubState(obj, 4);
	::R_SUB_GLOBAL <- obj.getSkillSubState();
	obj.sq_SetSkillSubState(obj, 5);
	::R_SUB_MEMBER <- obj.getSkillSubState();
}
");
        GameWorld w = World.Create(fx.Build(), out ScriptLoadSummary summary);
        Assert.True(summary.EntryOk);
        w.Events = events;
        GameObject hero = w.SpawnCharacter(World.Job, "hero", 100, 100, 0, Direction.Right);
        hero.SkillLevels[World.SkillFoo] = 1;
        w.RequestSkill(hero, World.SkillFoo);
        w.Step(1);
        Assert.Empty(w.Errors);
        Assert.Equal(new[] { "6/400", "3/100", "2/50" }, events.Log);
        Assert.Equal(4, Result(w, "R_SUB_GLOBAL"));
        Assert.Equal(5, Result(w, "R_SUB_MEMBER"));
    }

    private sealed class ShakeEvents : IWorldEvents
    {
        public List<string> Log { get; } = new();

        public void PlaySound(GameObject source, string soundKey) { }

        public void CreateParticle(GameObject source, string path, int x, int y, int z) { }

        public void RemoveParticle(GameObject source, string path) { }

        public void Shake(GameObject source, int strength, int durationMs) => Log.Add(strength + "/" + durationMs);
    }
}
