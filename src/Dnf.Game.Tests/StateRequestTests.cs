using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 状态切换请求的仲裁(E-099):对象只有一个待处理的请求;新请求在"没有待处理请求,或待处理请求的优先级不高于新请求"时替换它,
/// 否则被丢弃;怪物在死亡状态下拒绝所有请求。脚本与数字都是测试自己编的。
/// </summary>
public sealed class StateRequestTests
{
    /// <summary>每次进入 onSetState_Foo 先数一次,从第二次起把数据数组的第 0 项记进变量表的数组里(第一次是技能请求)。</summary>
    private const string Log = @"
	local v = obj.getVar();
	local n = v.getInt(1);
	v.setInt(1, n + 1);
	if (n > 0) {
		v.push_vector(obj.sq_GetVectorData(datas, 0));
	}
";

    private static string Request(int data, int priority) => @"
	obj.sq_IntVectClear(); obj.sq_IntVectPush(" + data + @");
	obj.sq_AddSetStatePacket(STATE_FOO, " + priority + @", true);
";

    private static (GameWorld World, GameObject Hero) RunTwoRequests(int priorityA, int priorityB)
    {
        return NativeTests.Run(Log + "if (n == 0) {" + Request(11, priorityA) + Request(22, priorityB) + "}");
    }

    [Fact]
    public void TwoRequestsOfTheSamePriorityInOneCallbackKeepOnlyTheLastOne()
    {
        (GameWorld w, GameObject hero) = RunTwoRequests(1, 1);
        Assert.Empty(w.Errors);
        Assert.Equal(new long[] { 22 }, hero.GetVar(null).Vector.ToArray());
        Assert.Equal(2, hero.GetVar(null).GetInt(1));    // 技能请求那一次 + 留下来的那一个请求
    }

    [Fact]
    public void ALowerPriorityRequestIsDroppedWhileAHigherOneIsPending()
    {
        (GameWorld w, GameObject hero) = RunTwoRequests(3, 1);
        Assert.Empty(w.Errors);
        Assert.Equal(new long[] { 11 }, hero.GetVar(null).Vector.ToArray());
        Assert.Equal(2, hero.GetVar(null).GetInt(1));
    }

    [Fact]
    public void AHigherPriorityRequestReplacesALowerPendingOne()
    {
        (GameWorld w, GameObject hero) = RunTwoRequests(1, 3);
        Assert.Empty(w.Errors);
        Assert.Equal(new long[] { 22 }, hero.GetVar(null).Vector.ToArray());
        Assert.Equal(2, hero.GetVar(null).GetInt(1));
    }

    [Fact]
    public void ARequestMadeInALaterCallbackIsNotBlockedByAnEarlierHigherPriorityOne()
    {
        // 第一次的请求 (11, 优先级 3) 已经执行完,第二次回调里再请求 (22, 优先级 1):待处理位置是空的,照常接受
        (GameWorld w, GameObject hero) = NativeTests.Run(Log
            + "if (n == 0) {" + Request(11, 3) + "}"
            + "else if (n == 1) {" + Request(22, 1) + "}");
        Assert.Empty(w.Errors);
        Assert.Equal(new long[] { 11, 22 }, hero.GetVar(null).Vector.ToArray());
        Assert.Equal(3, hero.GetVar(null).GetInt(1));
    }

    [Fact]
    public void MonstersRefuseRequestsOnceDyingButCharactersDoNot()
    {
        (GameWorld w, GameObject hero) = NativeTests.Run("");
        GameObject dummy = w.SpawnDummy("dummy", 300, 100, 0, new Aabb(-10, 10, -10, 10, 0, 60));
        w.ForceState(dummy, 3);
        Assert.Equal(3, dummy.State);
        w.ForceState(dummy, 5);                  // STATE_DIE
        Assert.Equal(5, dummy.State);
        w.ForceState(dummy, 0);                  // 死了就不再接受请求
        Assert.Equal(5, dummy.State);

        w.ForceState(hero, 5);
        Assert.Equal(5, hero.State);
        w.ForceState(hero, 0);                   // 玩家角色没有这个限制
        Assert.Equal(0, hero.State);
    }

    [Fact]
    public void RequestsThroughTheMemberInterfacesIgnoreTheFourthArgumentAndAlwaysResetTheTimer()
    {
        // addSetStatePacket 的第 4 个参数在客户端是"非本机控制也发送",不是重置计时器;状态执行时 isResetTimer 一律为真
        (GameWorld w, GameObject hero) = NativeTests.Run(Log + @"
	if (n == 0) {
		::R_FIRST <- 1;
		local vec = sq_GetGlobalIntVector();
		sq_IntVectorClear(vec);
		sq_IntVectorPush(vec, 9);
		obj.addSetStatePacket(STATE_FOO, vec, STATE_PRIORITY_USER, false, """");
	} else {
		::R_RESET <- isResetTimer ? 1 : 0;
		::R_DATA <- obj.sq_GetVectorData(datas, 0);
	}
");
        Assert.Empty(w.Errors);
        Assert.Equal(1, NativeTests.Result(w, "R_RESET"));
        Assert.Equal(9, NativeTests.Result(w, "R_DATA"));
        Assert.Equal(new long[] { 9 }, hero.GetVar(null).Vector.ToArray());
    }
}
