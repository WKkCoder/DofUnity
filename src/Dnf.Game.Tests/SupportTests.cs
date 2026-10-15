using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// P4 后半段新增的引擎语义与原生接口的合成测试(脚本是测试自己写的,不含原版内容):
/// 计时事件次数 0、动画循环标记与图层、攻击信息包的属性赋值、对象位置的成员写法、GetAnimationMap、消息与界面提示、
/// 强制进入状态、引擎常量、回调名大小写回退。
/// </summary>
public sealed class SupportTests
{
    private static AniFile Ani(AniBuilder b) => AniFile.Parse(b.ToBytes());

    private static long Result(GameWorld w, string name) => NativeTests.Result(w, name);

    // ------------------------------------------------------------------ 计时事件

    [Fact]
    public void TimeEventWithCountZeroRepeatsUntilTheScriptStopsIt()
    {
        var set = new TimeEventSet();
        set.Set(0, 100, 0, false);
        int fired = 0;
        for (int t = 0; t < 1000; t++)
        {
            set.Advance(1, (i, n) =>
            {
                fired++;
                return n >= 7; // 第 7 次回调"返回真"自己停止
            });
        }
        Assert.Equal(7, fired);
        Assert.False(set.Exists(0));

        var unlimited = new TimeEventSet();
        unlimited.Set(1, 50, 0, false);
        int count = 0;
        for (int t = 0; t < 1000; t++)
        {
            unlimited.Advance(1, (i, n) =>
            {
                count++;
                return false;
            });
        }
        Assert.Equal(20, count);
        Assert.True(unlimited.Exists(1));
    }

    // ------------------------------------------------------------------ 动画:循环标记与图层

    [Fact]
    public void AnimatorLoopMarkersRepeatTheMarkedFramesThenContinue()
    {
        var a = new Animator();
        // 帧 0 | 帧 1(起点) 帧 2(终点,再重复 2 次) | 帧 3
        a.Set(Ani(new AniBuilder().Frame(100).Frame(100).LoopStart().Frame(100).LoopEnd(2).Frame(100)), "marked.ani");
        var frames = new List<int>();
        bool ended = false;
        for (int step = 0; step < 12 && !ended; step++)
        {
            frames.Add(a.FrameIndex);
            a.Advance(100, null, () => ended = true);
        }
        Assert.Equal(new[] { 0, 1, 2, 1, 2, 1, 2, 3 }, frames);
        Assert.True(ended);
        Assert.Equal(800, a.TotalPlayedMs);
    }

    [Fact]
    public void LoopMarkerCountsStartOverWhenALoopingClipWrapsAround()
    {
        var a = new Animator();
        // 整段循环;帧 1 是起点,帧 2 是终点(再重复 1 次)
        a.Set(Ani(new AniBuilder().Loop().Frame(100).Frame(100).LoopStart().Frame(100).LoopEnd(1).Frame(100)), "wrap.ani");
        var frames = new List<int>();
        for (int step = 0; step < 12; step++)
        {
            frames.Add(a.FrameIndex);
            a.Advance(100, null, null);
        }
        Assert.Equal(new[] { 0, 1, 2, 1, 2, 3, 0, 1, 2, 1, 2, 3 }, frames);
    }

    [Fact]
    public void AnimatorLoopEndWithoutStartGoesBackToFrameZeroAndLargeCountsNeverEnd()
    {
        var a = new Animator();
        a.Set(Ani(new AniBuilder().Frame(50).Frame(50).LoopEnd(99999)), "forever.ani");
        int ends = 0;
        for (int t = 0; t < 10000; t++)
        {
            a.Advance(1, null, () => ends++);
        }
        Assert.Equal(0, ends);
        Assert.False(a.Ended);
        Assert.InRange(a.FrameIndex, 0, 1);
    }

    [Fact]
    public void LayerAnimationsAdvanceWithTheirHostAndAreDroppedWhenTheHostChangesClip()
    {
        var host = new Animator();
        host.Set(Ani(new AniBuilder().Loop().Frame(100)), "host.ani");
        var layer = new Animator();
        layer.Set(Ani(new AniBuilder().Frame(100).Frame(100)), "layer.ani");
        host.AddLayer(layer);
        host.AddLayer(layer); // 重复添加不会翻倍推进
        Assert.Equal(1, host.LayerCount);
        for (int i = 0; i < 3; i++)
        {
            host.Advance(100, null, null);
        }
        Assert.True(layer.Ended);
        Assert.Equal(300, layer.TotalPlayedMs);

        host.Set(Ani(new AniBuilder().Loop().Frame(100)), "other.ani");
        Assert.Equal(0, host.LayerCount);
    }

    // ------------------------------------------------------------------ 脚本接口

    [Fact]
    public void AttackInfoPacketsTakePropertyAssignmentsAndOtherObjectsRejectThem()
    {
        (GameWorld w, GameObject hero) = NativeTests.Run(@"
	local info = sq_getNewAttackInfoPacket();
	info.sizeRate = 150;
	info.changeStatusProb = 12.5;
	info.changeStatusType = 3;
	info.label = ""frozen"";
	sq_SetCurrentAttackInfo(obj, info);
	::R_ASSIGNED <- 1;
");
        Assert.Empty(w.Errors);
        Assert.Equal(1, Result(w, "R_ASSIGNED"));
        AttackInfo info = hero.CurrentAttackInfo;
        Assert.NotNull(info);
        Assert.Equal(150L, info.Properties["sizeRate"]);
        Assert.Equal(12.5f, info.Properties["changeStatusProb"]);
        Assert.Equal(3L, info.Properties["changeStatusType"]);
        Assert.Equal("frozen", info.Properties["label"]);

        // 别的宿主对象不接受随意赋属性
        (GameWorld w2, _) = NativeTests.Run(@"
	obj.notAField = 5;
	::R_AFTER <- 1;
");
        Assert.NotEmpty(w2.Errors);
    }

    [Fact]
    public void SetCurrentPosTakesTheObjectInBothMemberAndGlobalForms()
    {
        (GameWorld w, GameObject hero) = NativeTests.Run(@"
	obj.sq_SetCurrentPos(obj, 10, 20, 30);
	::R_X1 <- obj.getXPos(); ::R_Y1 <- obj.getYPos(); ::R_Z1 <- obj.getZPos();
	sq_SetCurrentPos(obj, 40, 50, 60);
	::R_X2 <- obj.getXPos(); ::R_Z2 <- obj.getZPos();
	obj.setCurrentPos(7, 8, 9);
	::R_X3 <- obj.getXPos(); ::R_Z3 <- obj.getZPos();
");
        Assert.Empty(w.Errors);
        Assert.Equal(10, Result(w, "R_X1"));
        Assert.Equal(20, Result(w, "R_Y1"));
        Assert.Equal(30, Result(w, "R_Z1"));
        Assert.Equal(40, Result(w, "R_X2"));
        Assert.Equal(60, Result(w, "R_Z2"));
        Assert.Equal(7, Result(w, "R_X3"));
        Assert.Equal(9, Result(w, "R_Z3"));
        Assert.Equal(7, hero.X);
    }

    [Fact]
    public void GetAnimationMapLoadsFromTheDataRootOnceAndHandsOutTheSameObject()
    {
        (GameWorld w, GameObject hero) = NativeTests.Run(@"
	local v = obj.getVar();
	local a = v.GetAnimationMap(""Loop1"", ""Character/Test/Animation/Foo_Loop.ani"");
	local b = v.GetAnimationMap(""Loop1"", ""somewhere/else.ani"");
	::R_SAME <- (a == b) ? 1 : 0;
	obj.setCurrentAnimation(a);
	::R_LOOPS <- a.isLoop() ? 1 : 0;
");
        Assert.Empty(w.Errors);
        Assert.Equal(1, Result(w, "R_SAME"));
        Assert.Equal(1, Result(w, "R_LOOPS"));
        Assert.Equal("character/test/animation/foo_loop.ani", hero.Animator.Path);
    }

    [Fact]
    public void ObjectMessagesApplyNowOrAfterTheDelay()
    {
        (GameWorld w, GameObject hero) = NativeTests.Run(@"
	sq_SendMessage(obj, 1, 1, 0);
	sq_PostDelayedMessage(obj, 1, 0, 0, 500);
	sq_PostDelayedMessage(obj, 7, 5, 0, 100);
");
        Assert.Empty(w.Errors);
        Assert.Equal(1, hero.ObjectMessages[1]);
        w.Step(60);
        Assert.False(hero.ObjectMessages.ContainsKey(7));
        w.Step(100);
        Assert.Equal(5, hero.ObjectMessages[7]);
        Assert.Equal(1, hero.ObjectMessages[1]);
        w.Step(400);
        Assert.Equal(0, hero.ObjectMessages[1]);
    }

    [Fact]
    public void UiMessagesAndPreloadedEffectsAreRecorded()
    {
        (GameWorld w, _) = NativeTests.Run(@"
	sq_AddMessage(29002);
	sq_AddMessage(414);
	obj.sq_LoadSkillEffectAni(SKILL_FOO, ""Effect/Animation/A.ani"");
	obj.sq_LoadSkillEffectAni(SKILL_FOO, ""Effect/Animation/B.ani"");
");
        Assert.Empty(w.Errors);
        Assert.Equal(new[] { 29002, 414 }, w.UiMessages);
        Assert.Equal(new[] { "Effect/Animation/A.ani", "Effect/Animation/B.ani" }, w.PreloadedSkillEffects[World.SkillFoo]);
    }

    [Fact]
    public void ResettingTheScriptRandomRepeatsTheSequence()
    {
        (GameWorld w, _) = NativeTests.Run("");
        int a = w.ScriptRandom.Next(1000000);
        int b = w.ScriptRandom.Next(1000000);
        Assert.NotEqual(a, b);
        w.ResetScriptRandom();
        Assert.Equal(a, w.ScriptRandom.Next(1000000));
        Assert.Equal(b, w.ScriptRandom.Next(1000000));
        w.ResetScriptRandom(7);
        Assert.NotEqual(a, w.ScriptRandom.Next(1000000));
    }

    [Fact]
    public void GrabableDefaultsToMonstersOnlyAndTheScriptCanOverrideIt()
    {
        (GameWorld w, GameObject hero) = NativeTests.Run(@"
	::R_DEFAULT <- sq_IsGrabable(obj, obj) ? 1 : 0;
	sq_SetGrabable(obj, true);
	::R_TRUE <- sq_IsGrabable(obj, obj) ? 1 : 0;
	obj.sq_SetGrabable(obj, false);
	::R_FALSE <- sq_IsGrabable(obj, obj) ? 1 : 0;
");
        Assert.Empty(w.Errors);
        Assert.Equal(0, Result(w, "R_DEFAULT")); // 角色默认不能被抓
        Assert.Equal(1, Result(w, "R_TRUE"));
        Assert.Equal(0, Result(w, "R_FALSE"));
        Assert.False(hero.GrabableOverride);
    }

    [Fact]
    public void CustomRotateAndSkillLevelQueries()
    {
        (GameWorld w, GameObject hero) = NativeTests.Run(@"
	::R_APPLY0 <- obj.isApplyCustomRotate() ? 1 : 0;
	obj.setCustomRotate(true, 1.5);
	::R_APPLY1 <- obj.isApplyCustomRotate() ? 1 : 0;
	::R_ANGLE100 <- (obj.getCustomRotateAngle() * 100).tointeger();
	sq_SetCustomRotate(obj, 0.25);
	::R_ANGLE2 <- (obj.getCustomRotateAngle() * 100).tointeger();
	::R_OVER_LOW <- obj.isOverSkillLevel(SKILL_FOO, 2) ? 1 : 0;
	::R_OVER_HIGH <- obj.isOverSkillLevel(SKILL_FOO, 3) ? 1 : 0;
	::R_PASSIVE_INDEX <- 0;
");
        Assert.Empty(w.Errors);
        Assert.Equal(0, Result(w, "R_APPLY0"));
        Assert.Equal(1, Result(w, "R_APPLY1"));
        Assert.Equal(150, Result(w, "R_ANGLE100"));
        Assert.Equal(25, Result(w, "R_ANGLE2"));
        Assert.Equal(1, Result(w, "R_OVER_LOW"));
        Assert.Equal(0, Result(w, "R_OVER_HIGH"));
        Assert.True(hero.ApplyCustomRotate);
    }

    // ------------------------------------------------------------------ 世界

    [Fact]
    public void ForceStateEntersAStateEvenWhenTheSkillChecksSayNo()
    {
        Fixture fx = World.Basic();
        fx.Ani("character/test/animation/foo_start.ani", new AniBuilder().Frame(50));
        fx.Ani("character/test/animation/foo_loop.ani", new AniBuilder().Loop().Frame(1000));
        fx.Ani("character/test/animation/foo_end.ani", new AniBuilder().Frame(80));
        fx.WithFooScript(@"
function checkExecutableSkill_Foo(obj) { return false; }
function checkCommandEnable_Foo(obj) { return false; }
function onSetState_Foo(obj, state, datas, isResetTimer)
{
	::R_ENTERED <- 1;
	::R_DATA_0 <- obj.sq_GetVectorData(datas, 0);
	::R_DATA_1 <- obj.sq_GetVectorData(datas, 1);
}
");
        GameWorld w = World.Create(fx.Build(), out ScriptLoadSummary summary);
        Assert.True(summary.EntryOk);
        GameObject hero = w.SpawnCharacter(World.Job, "hero", 100, 100, 0, Direction.Right);
        hero.SkillLevels[World.SkillFoo] = 1;

        w.RequestSkill(hero, World.SkillFoo);
        w.Step(10);
        Assert.NotEqual(World.StateFoo, hero.State); // 正常请求被检查函数拒绝

        w.ForceState(hero, World.StateFoo, 3, 4);
        w.Step(10);
        Assert.Equal(World.StateFoo, hero.State);
        Assert.Empty(w.Errors);
        Assert.Equal(1, Result(w, "R_ENTERED"));
        Assert.Equal(3, Result(w, "R_DATA_0"));
        Assert.Equal(4, Result(w, "R_DATA_1"));
    }

    [Fact]
    public void ClientRegisteredAppendageIdConstantsHaveTheirOriginalValues()
    {
        // 证据 E-045:取值读自客户端注册函数里的"名字 + 立即数"(APID_COMMON 例外,见 EngineConstants 的注释)
        (GameWorld w, _) = NativeTests.Run(@"
	::R_COMMON <- APID_COMMON;
	::R_ENCHANT <- APID_SKILL_LIGHT_ENCHANT_WEAPON;
	::R_RESONANCE <- APID_RESONANCE;
	::R_SHIELD <- APID_AT_MAGE_ELEMENT_SHIELD;
	::R_CHAIN_LIGHT <- APID_SKILL_ELEMENTAL_CHAIN_LIGHT;
");
        Assert.Empty(w.Errors);
        Assert.Equal(0, Result(w, "R_COMMON"));
        Assert.Equal(1, Result(w, "R_ENCHANT"));
        Assert.Equal(191, Result(w, "R_RESONANCE"));
        Assert.Equal(328, Result(w, "R_SHIELD"));
        Assert.Equal(195, Result(w, "R_CHAIN_LIGHT"));
        Assert.Equal(10, Dnf.Script.EngineConstants.AppendageIds.Count);
        Assert.Equal(10, Dnf.Script.EngineConstants.AppendageIds.Values.Distinct().Count());
    }

    [Fact]
    public void EngineProvidedAppendageIdConstantsAreSynthesizedPerName()
    {
        (GameWorld w, _) = NativeTests.Run(@"
	::R_A <- APID_FIRST_THING;
	::R_B <- APID_SECOND_THING;
	::R_A_AGAIN <- APID_FIRST_THING;
");
        Assert.Empty(w.Errors);
        long a = Result(w, "R_A");
        long b = Result(w, "R_B");
        Assert.True(a >= 100000);
        Assert.NotEqual(a, b);
        Assert.Equal(a, Result(w, "R_A_AGAIN"));
    }

    [Fact]
    public void StateCallbacksAreFoundWhenOnlyTheLetterCaseDiffers()
    {
        Fixture fx = World.Basic();
        fx.Ani("character/test/animation/foo_start.ani", new AniBuilder().Frame(50));
        fx.Ani("character/test/animation/foo_loop.ani", new AniBuilder().Loop().Frame(1000));
        fx.Ani("character/test/animation/foo_end.ani", new AniBuilder().Frame(80));
        fx.WithFooScript(@"
function checkexecutableskill_foo(obj)
{
	if (obj.sq_IsUseSkill(SKILL_FOO)) {
		obj.sq_AddSetStatePacket(STATE_FOO, STATE_PRIORITY_USER, true);
		return true;
	}
	return false;
}
function onsetstate_FOO(obj, state, datas, isResetTimer)
{
	::R_LOWER_CASE_RAN <- 1;
}
");
        GameWorld w = World.Create(fx.Build(), out ScriptLoadSummary summary);
        Assert.True(summary.EntryOk);
        GameObject hero = w.SpawnCharacter(World.Job, "hero", 100, 100, 0, Direction.Right);
        hero.SkillLevels[World.SkillFoo] = 1;
        w.RequestSkill(hero, World.SkillFoo);
        w.Step(10);
        Assert.Empty(w.Errors);
        Assert.Equal(1, Result(w, "R_LOWER_CASE_RAN"));
        Assert.Equal(World.StateFoo, hero.State);
    }

    [Fact]
    public void ExecutableStatesAreTheUnionOfEveryTagOccurrence()
    {
        Fixture fx = World.Basic();
        fx.Tokens("skill/test/foo.skl", w => w
            .Tag("[executable states]").Ints(0, 8)
            .Tag("[static data]").Ints(300, 150)
            .Tag("[executable states]").Ints(0, 14)
            .Tag("[level info]").Ints(2, 100, 5, 120, 7, 140, 9));
        fx.Ani("character/test/animation/foo_start.ani", new AniBuilder().Frame(50));
        fx.WithFooScript("function onSetState_Foo(obj, state, datas, isResetTimer) {}");
        GameWorld w = World.Create(fx.Build(), out ScriptLoadSummary summary);
        Assert.True(summary.EntryOk);
        SkillDef def = w.Data.GetSkill(World.Job, World.SkillFoo);
        Assert.NotNull(def);
        Assert.Equal(new[] { 0, 8, 14 }, def.ExecutableStates.Distinct().OrderBy(x => x).ToArray());
    }
}
