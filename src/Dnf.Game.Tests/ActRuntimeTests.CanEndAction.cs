using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// 程序能不能靠 [SET ACTION] 结束或换掉当前动作(M70b,证据 E-170):宿主拿它决定"循环动画的出招要不要一直放到脚本收尾"。
/// 回待机([STAND]/[DEFAULT])与换招([ATTACK] n/[CUSTOM] n)算,立刻的与 [END] 的都算;别的动作族(走、受击、倒地……)出招阶段接不了,不算。
/// 脚本内容全是合成的。
/// </summary>
public sealed partial class ActRuntimeTests
{
    private static ActProgram SetActionProgram(params ScriptLine[] action) => P(Me(new[] { L("[SET ACTION]") }.Concat(action).ToArray()));

    [Fact]
    public void AProgramCanEndItsActionThroughAStandDefaultAttackOrCustomSetAction()
    {
        Assert.True(SetActionProgram(L("[STAND]"), L("[NOW]")).CanEndAction);
        Assert.True(SetActionProgram(L("[STAND]")).CanEndAction);
        Assert.True(SetActionProgram(L("[DEFAULT]")).CanEndAction);
        Assert.True(SetActionProgram(L("[ATTACK]", 1)).CanEndAction);
        Assert.True(SetActionProgram(L("[ATTACK]", 1), L("[END]")).CanEndAction);
        Assert.True(SetActionProgram(L("[CUSTOM]", 2), L("[END]")).CanEndAction);
    }

    [Fact]
    public void AProgramWhoseSetActionsCannotLeaveAnAttackCannotEndItsAction()
    {
        Assert.False(SetActionProgram(L("[MOVE]")).CanEndAction);
        Assert.False(SetActionProgram(L("[DAMAGE]")).CanEndAction);
        Assert.False(SetActionProgram(L("[DOWN]")).CanEndAction);
        Assert.False(SetActionProgram(L("[JUMP]")).CanEndAction);
        Assert.False(ActProgram.Empty.CanEndAction);
        Assert.False(P(Me(Speed(5))).CanEndAction);                    // 没有 [SET ACTION]
        Assert.False(P(Me(L("[DESTROY]"))).CanEndAction);              // [DESTROY] 是对象的事,不是动作的
    }

    [Fact]
    public void AnyOneEndingSetActionAmongOthersIsEnough()
    {
        ActProgram p = P(Cat(
            Me(L("[SET ACTION]"), L("[MOVE]")),
            Behavior(L("[SET ACTION]"), L("[DOWN]")),
            Behavior(L("[SET ACTION]"), L("[STAND]"), L("[NOW]"))));
        Assert.True(p.CanEndAction);
    }
}
