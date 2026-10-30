using Dnf.Game.Act;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 被动对象各动作的触发器开关板(M62,证据 E-158):每个动作一份开关,出生时按 [ENABLE] 初值建好,动作来回切换时保留;
/// [TRIGGER CHECK] [CUSTOM] a i 读别的自定义动作的开关,[TRIGGER] [CUSTOM] a i 写它们,[DEFAULT] 是基础动作;
/// 被动对象没有 [ATTACK]/[MOVE]/[STAY]/[SIT]/[DOWN]/[PROC] 池(客户端问被动对象只有自定义、默认、过程三种,而被动对象没有过程动作)。
/// 对象与 .act 全是合成的:actbread / actbwrite / actbloop(见 <see cref="AvatarFixture"/> 的 ActBoardReadId 等)。
/// </summary>
public partial class PassiveObjectTests
{
    [Fact]
    public void AnObjectReadsAnotherCustomActionsInitialSwitchesWhenItIsBorn()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActBoardReadId, facingLeft: false);
        Assert.Equal((0, 0, 0, 0), (o.BoardReads, o.BoardReadMisses, o.BoardWrites, o.BoardWriteMisses));
        o.Update(10);
        Assert.Equal(100.0, o.VelocityX);                           // [CUSTOM] 0 0 [ON]:自定义动作 0 的触发器 0 出生时开着,通过
        Assert.Equal(0.0, o.VelocityY);                             // 同一条触发器的 [OFF] 读法不通过
        Assert.Equal(6, o.BoardReads);                              // 一共问了 6 次
    }

    [Fact]
    public void ReadsOfOutOfRangeSwitchesMissingActionsOrActionsWithoutTriggersFailAndAreCounted()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActBoardReadId, facingLeft: false);
        o.Update(10);
        Assert.Equal(0.0, o.VelocityZ);                             // 越界的序号、不存在的动作、没装触发器的动作:三条都不触发
        Assert.Equal(3, o.BoardReadMisses);
        Assert.Equal(2, o.TriggersFired);                           // 6 条里只有两条通过:自定义动作 0 开着的那条,与动画读不到的自定义动作 2
        Assert.Single(rig.Host.Spawns);                             // 自定义动作 2:.act 读得到、动画读不到,池照样有答案,通过
        Assert.Equal(AvatarFixture.ActGraveId, rig.Host.Spawns[0].Index);
    }

    [Fact]
    public void ACustomActionWhoseAnimationCannotBeReadHasAPoolButCannotBeEntered()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActBoardReadId, facingLeft: false);
        Assert.Null(o.Template.Etc(2));                             // 放不出来
        Assert.NotNull(o.Template.EtcAction(2));                    // 但 .act 读得到
        Assert.Null(o.Template.Etc(9));
        Assert.Null(o.Template.EtcAction(9));
        Assert.Null(o.Template.EtcAction(-1));
        Assert.Null(o.Template.EtcAction(o.Template.EtcCount));     // 正好越过末尾
        Assert.NotNull(o.Template.EtcAction(o.Template.EtcCount - 1));
        PassiveAction before = o.Action;
        ((IActEntity)o).SetAction(ActActionKind.Custom, 2, true, false);
        Assert.Same(before, o.Action);                              // 切不过去
        Assert.Equal(true, ((IActEntity)o).GetActionTrigger(ActTriggerPool.Custom, 2, 0));
    }

    [Fact]
    public void EnteringACustomActionFiresItsTriggersWhoseSwitchesAreOn()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActBoardReadId, facingLeft: false);
        o.Update(10);
        Assert.Equal(100.0, o.VelocityX);
        ((IActEntity)o).SetAction(ActActionKind.Custom, 0, true, false);
        o.Update(10);
        Assert.Equal(7.0, o.VelocityX);                             // 对照:自定义动作 0 的触发器 0 一直开着,进来就触发
    }

    [Fact]
    public void ACrossActionSwitchOffStaysOffUntilThatActionIsEntered()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActBoardWriteId);
        var e = (IActEntity)o;
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Custom, 0, 0));
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Custom, 0, 1));
        o.Update(10);                                               // 基础动作:把自定义动作 0 的触发器 0 关掉、再写一次越界的触发器 9(落空),然后切过去
        Assert.Equal((1, 1), (o.BoardWrites, o.BoardWriteMisses));
        Assert.NotSame(o.Template.Basic, o.Action);
        Assert.Equal(0.0, o.VelocityX);
        Assert.Equal(0.0, o.VelocityY);
        o.Update(10);                                               // 自定义动作 0 的出生触发器:0 被关着不触发,1 照常触发
        Assert.Equal(0.0, o.VelocityX);
        Assert.Equal(22.0, o.VelocityY);
        Assert.Equal(false, e.GetActionTrigger(ActTriggerPool.Custom, 0, 0));
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Custom, 0, 1));
        Assert.Equal(1, o.TriggersFired);                           // 正在跑的是自定义动作 0 的运行器,只触发了一条
    }

    [Fact]
    public void TheSwitchesOfAnActionSurviveLeavingAndEnteringItAgain()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActBoardLoopId);
        for (int i = 0; i < 12; i++)
        {
            o.Update(10);
        }
        Assert.Single(rig.Host.Spawns);                             // 自定义动作 0 放了一个对象、关了自己的触发器 0;第二次进来触发器 0 还关着,不再放
        Assert.NotSame(o.Template.Basic, o.Action);                 // 也就不再切回基础动作,停在自定义动作 0
        Assert.Equal(false, ((IActEntity)o).GetActionTrigger(ActTriggerPool.Custom, 0, 0));
    }

    [Fact]
    public void TheDefaultPoolIsTheBasicActionAndSharesTheRunningRunnersSwitches()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActBoardReadId, facingLeft: false);
        var e = (IActEntity)o;
        o.Update(10);                                               // 基础动作的触发器 0 触发了一次、触发器 3 也通过了(放一个对象)
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Default, 0, 0));
        Assert.Equal(2, o.TriggersFired);
        e.SetActionTrigger(ActTriggerPool.Default, 0, 0, ActTriggerCommand.Off);
        Assert.Equal(false, e.GetActionTrigger(ActTriggerPool.Default, 0, 0));
        e.SetActionTrigger(ActTriggerPool.Default, 0, 0, ActTriggerCommand.On);
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Default, 0, 0));
        Assert.Equal(2, o.TriggersFired);
        e.SetActionTrigger(ActTriggerPool.Default, 0, 0, ActTriggerCommand.Reset);
        Assert.Equal(1, o.TriggersFired);                           // 正在跑的动作走运行器:触发器 0 的触发次数清零(它触发过 1 次),开关保持
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Default, 0, 0));
    }

    [Fact]
    public void ASwitchWrittenToAnIdleActionChangesItsBoardWithoutTouchingTheRunningOne()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActBoardReadId, facingLeft: false);
        var e = (IActEntity)o;
        o.Update(10);
        e.SetActionTrigger(ActTriggerPool.Custom, 0, 0, ActTriggerCommand.Off);
        Assert.Equal(false, e.GetActionTrigger(ActTriggerPool.Custom, 0, 0));
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Default, 0, 0));       // 基础动作的那份没动
        e.SetActionTrigger(ActTriggerPool.Custom, 0, 0, ActTriggerCommand.On);
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Custom, 0, 0));
        e.SetActionTrigger(ActTriggerPool.Custom, 0, 0, ActTriggerCommand.Reset);   // 没在跑的动作:重置只清触发次数,开关保持
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Custom, 0, 0));
        Assert.Equal((3, 0), (o.BoardWrites, o.BoardWriteMisses));
    }

    [Fact]
    public void OtherActionFamiliesHaveNoPoolOnAPassiveObject()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActBoardReadId, facingLeft: false);
        var e = (IActEntity)o;
        foreach (ActTriggerPool pool in new[] { ActTriggerPool.Attack, ActTriggerPool.Move, ActTriggerPool.Stay, ActTriggerPool.Sit, ActTriggerPool.Down, ActTriggerPool.Proc })
        {
            Assert.Null(e.GetActionTrigger(pool, 0, 0));
            e.SetActionTrigger(pool, 0, 0, ActTriggerCommand.Off);
        }
        Assert.Equal((6, 6), (o.BoardReads, o.BoardReadMisses));
        Assert.Equal((0, 6), (o.BoardWrites, o.BoardWriteMisses));
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Default, 0, 0));       // 没有误伤基础动作的开关
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Custom, 0, 0));
    }

    [Fact]
    public void AnActionWithoutTriggersOrOutOfRangeHasNoPoolForReadsOrWrites()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActBoardReadId, facingLeft: false);
        var e = (IActEntity)o;
        Assert.Null(e.GetActionTrigger(ActTriggerPool.Custom, 1, 0));               // 自定义动作 1 只有动画
        Assert.Null(e.GetActionTrigger(ActTriggerPool.Custom, 9, 0));               // 没有第 9 个自定义动作
        Assert.Null(e.GetActionTrigger(ActTriggerPool.Custom, -1, 0));
        Assert.Null(e.GetActionTrigger(ActTriggerPool.Custom, 0, 1));               // 触发器序号正好越过末尾(自定义动作 0 只有一条)
        Assert.NotNull(e.GetActionTrigger(ActTriggerPool.Custom, 0, 0));
        Assert.Null(e.GetActionTrigger(ActTriggerPool.Custom, 0, 99));              // 触发器序号越界
        Assert.Null(e.GetActionTrigger(ActTriggerPool.Custom, 0, -1));
        e.SetActionTrigger(ActTriggerPool.Custom, 1, 0, ActTriggerCommand.On);
        e.SetActionTrigger(ActTriggerPool.Custom, 9, 0, ActTriggerCommand.On);
        e.SetActionTrigger(ActTriggerPool.Custom, 0, 1, ActTriggerCommand.On);
        e.SetActionTrigger(ActTriggerPool.Custom, 0, 99, ActTriggerCommand.On);
        e.SetActionTrigger(ActTriggerPool.Custom, 0, -1, ActTriggerCommand.On);
        Assert.Equal((7, 6), (o.BoardReads, o.BoardReadMisses));
        Assert.Equal((0, 5), (o.BoardWrites, o.BoardWriteMisses));
        Assert.Equal(true, e.GetActionTrigger(ActTriggerPool.Custom, 0, 0));
    }

    [Fact]
    public void TheSessionCountsTheBoardReadsOfEveryObjectEvenAfterTheyDisappear()
    {
        using var arena = new ActArena("spawn9207", useVitals: false);
        RoomCombatSession s = arena.Session;
        int Live() => s.Passives.Count(p => p.Template.Index == AvatarFixture.ActBoardReadId);
        Assert.True(arena.Until(() => s.Passives.Any(p => p.Template.Index == AvatarFixture.ActBoardReadId && p.BoardReads > 0)) > 0);
        Assert.Equal((6, 3), (s.PassiveBoardReads, s.PassiveBoardReadMisses));     // 第一个对象还在场上,它的读数也算
        // 每个读对象出生时读 6 次(3 次落空)、限时 1 秒;读过的对象比场上还在的多,说明消失了的对象也计在内
        Assert.True(arena.Until(() => s.PassiveBoardReads / 6 >= 2 && s.PassiveBoardReads / 6 > Live(), 4000) > 0);
        Assert.Equal(0, s.PassiveBoardReads % 6);
        Assert.Equal(s.PassiveBoardReads, 2 * s.PassiveBoardReadMisses);
        Assert.Equal((0, 0), (s.PassiveBoardWrites, s.PassiveBoardWriteMisses));
    }

    [Fact]
    public void TheSessionCountsTheBoardWritesOfEveryObjectEvenAfterTheyDisappear()
    {
        using var arena = new ActArena("spawn9208", useVitals: false);
        RoomCombatSession s = arena.Session;
        int Live() => s.Passives.Count(p => p.Template.Index == AvatarFixture.ActBoardWriteId);
        Assert.True(arena.Until(() => s.Passives.Any(p => p.Template.Index == AvatarFixture.ActBoardWriteId && p.BoardWrites > 0)) > 0);
        Assert.Equal((1, 1), (s.PassiveBoardWrites, s.PassiveBoardWriteMisses));   // 第一个对象还在场上,它的写数也算
        // 每个写对象出生时写 2 次(1 次生效、1 次落空)、限时 1 秒;写过的对象比场上还在的多,说明消失了的对象也计在内
        Assert.True(arena.Until(() => s.PassiveBoardWrites >= 2 && s.PassiveBoardWrites > Live(), 4000) > 0);
        Assert.Equal(s.PassiveBoardWrites, s.PassiveBoardWriteMisses);
        Assert.Equal((0, 0), (s.PassiveBoardReads, s.PassiveBoardReadMisses));
    }

    [Fact]
    public void ReattachingTheActsClearsTheBoardCounts()
    {
        using var arena = new ActArena("spawn9207", useVitals: false);
        RoomCombatSession s = arena.Session;
        int Live() => s.Passives.Count(p => p.Template.Index == AvatarFixture.ActBoardReadId);
        Assert.True(arena.Until(() => s.PassiveBoardReads / 6 >= 2 && s.PassiveBoardReads / 6 > Live(), 4000) > 0);
        Assert.True(s.PassiveBoardReadMisses > 0);
        s.AttachActs(null);                                         // 撤掉程序库:场上的对象清空,已消失对象的累计也清零
        Assert.Equal((0, 0, 0, 0), (s.PassiveBoardReads, s.PassiveBoardReadMisses, s.PassiveBoardWrites, s.PassiveBoardWriteMisses));
    }

    [Fact]
    public void TheInitialEnableValuesAreVisibleBeforeAnActionIsEnteredAndAnIdleWriteTakesEffectOnTheNextEntry()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActBoardInitId, facingLeft: false);
        var e = (IActEntity)o;
        Assert.Equal(false, e.GetActionTrigger(ActTriggerPool.Custom, 0, 0));   // 还没进过的动作,[ENABLE] [OFF] 的初值也读得到
        o.Update(10);
        Assert.Equal(31.0, o.VelocityX);                            // 读 [OFF] 通过
        Assert.Equal(0.0, o.VelocityY);                             // 读 [ON] 不通过
        e.SetAction(ActActionKind.Custom, 0, true, false);
        o.Update(10);
        Assert.Equal(31.0, o.VelocityX);                            // 进去之后初值为关的触发器不触发(速度没变成 41)
        e.SetActionTrigger(ActTriggerPool.Custom, 0, 0, ActTriggerCommand.On);      // 正在跑的动作:开关立刻生效,但 [ON SET ACTION] 要下次进入才算
        e.SetAction(ActActionKind.Stand, 0, true, false);
        o.Update(10);
        Assert.Equal(32.0, o.VelocityY);                            // 回到基础动作,再读 [ON]:现在通过了
        e.SetAction(ActActionKind.Custom, 0, true, false);
        o.Update(10);
        Assert.Equal(41.0, o.VelocityX);                            // 再进自定义动作 0:触发器 0 现在开着,触发
    }
}
