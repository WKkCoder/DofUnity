using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Client.Tests;

/// <summary>
/// <see cref="OnlineSettlementInputGate"/> 真值表、边沿状态机(首帧松触屏/单帧冻结/解冻无残留/立即再冻结/Reset)
/// 以及与 <see cref="OnlineSettlementController"/> 公开状态(<see cref="OnlineSettlementController.IsActive"/>)联动验证。
/// </summary>
public sealed class OnlineSettlementInputGateTests
{
    public static IEnumerable<object[]> DistinctRawInputs()
    {
        yield return new object[] { -1, 1, true, false, SkillKeys.A | SkillKeys.CommandSkill, SkillKeys.S | SkillKeys.Attack, true };
        yield return new object[] { 1, -1, false, true, SkillKeys.D | SkillKeys.F, SkillKeys.G | SkillKeys.CommandBuff, false };
        yield return new object[] { 0, 1, true, true, SkillKeys.H | SkillKeys.CommandBuff, SkillKeys.None, true };
        yield return new object[] { -1, 0, false, false, SkillKeys.None, SkillKeys.A | SkillKeys.H, false };
        yield return new object[] { 0, 0, false, false, SkillKeys.None, SkillKeys.None, false };
        yield return new object[] { 1, 1, true, true, SkillKeys.Quick | SkillKeys.CommandSkill, SkillKeys.Quick | SkillKeys.CommandBuff | SkillKeys.Attack, true };
    }

    [Theory]
    [MemberData(nameof(DistinctRawInputs))]
    public void Inactive_gate_passes_all_raw_inputs_verbatim(
        int dirX,
        int dirY,
        bool attack,
        bool jump,
        SkillKeys skills,
        SkillKeys skillsHeld,
        bool commandsEnabled)
    {
        var gate = new OnlineSettlementInputGate();

        OnlineSettlementInputDecision first = gate.Evaluate(false, dirX, dirY, attack, jump, skills, skillsHeld, commandsEnabled);
        Assert.False(first.Frozen);
        Assert.False(first.ReleaseTouch);
        Assert.False(first.JustUnfrozen);
        Assert.Equal(dirX, first.DirX);
        Assert.Equal(dirY, first.DirY);
        Assert.Equal(attack, first.Attack);
        Assert.Equal(jump, first.Jump);
        Assert.Equal(skills, first.Skills);
        Assert.Equal(skillsHeld, first.SkillsHeld);
        Assert.Equal(commandsEnabled, first.CommandsEnabled);

        // 连续未激活帧同样保持原样透传、边沿均为 false
        OnlineSettlementInputDecision second = gate.Evaluate(false, dirX, dirY, attack, jump, skills, skillsHeld, commandsEnabled);
        Assert.False(second.Frozen);
        Assert.False(second.ReleaseTouch);
        Assert.False(second.JustUnfrozen);
        Assert.Equal((dirX, dirY, attack, jump, skills, skillsHeld, commandsEnabled),
            (second.DirX, second.DirY, second.Attack, second.Jump, second.Skills, second.SkillsHeld, second.CommandsEnabled));
    }

    [Theory]
    [MemberData(nameof(DistinctRawInputs))]
    public void Active_gate_zeros_all_outputs_and_releases_touch_only_on_first_frame(
        int dirX,
        int dirY,
        bool attack,
        bool jump,
        SkillKeys skills,
        SkillKeys skillsHeld,
        bool commandsEnabled)
    {
        var gate = new OnlineSettlementInputGate();

        OnlineSettlementInputDecision entry = gate.Evaluate(true, dirX, dirY, attack, jump, skills, skillsHeld, commandsEnabled);
        Assert.True(entry.Frozen);
        Assert.True(entry.ReleaseTouch);
        Assert.False(entry.JustUnfrozen);
        Assert.Equal(0, entry.DirX);
        Assert.Equal(0, entry.DirY);
        Assert.False(entry.Attack);
        Assert.False(entry.Jump);
        Assert.Equal(SkillKeys.None, entry.Skills);
        Assert.Equal(SkillKeys.None, entry.SkillsHeld);
        Assert.False(entry.CommandsEnabled);

        OnlineSettlementInputDecision sustained = gate.Evaluate(true, dirX, dirY, attack, jump, skills, skillsHeld, commandsEnabled);
        Assert.True(sustained.Frozen);
        Assert.False(sustained.ReleaseTouch);
        Assert.False(sustained.JustUnfrozen);
        Assert.Equal(0, sustained.DirX);
        Assert.Equal(0, sustained.DirY);
        Assert.False(sustained.Attack);
        Assert.False(sustained.Jump);
        Assert.Equal(SkillKeys.None, sustained.Skills);
        Assert.Equal(SkillKeys.None, sustained.SkillsHeld);
        Assert.False(sustained.CommandsEnabled);

        OnlineSettlementInputDecision third = gate.Evaluate(true, -1, 1, true, true, SkillKeys.H, SkillKeys.Attack, true);
        Assert.True(third.Frozen);
        Assert.False(third.ReleaseTouch);
        Assert.False(third.JustUnfrozen);
        Assert.Equal((0, 0, false, false, SkillKeys.None, SkillKeys.None, false),
            (third.DirX, third.DirY, third.Attack, third.Jump, third.Skills, third.SkillsHeld, third.CommandsEnabled));
    }

    [Fact]
    public void Single_frame_freeze_releases_touch_once_and_unfreezes_without_buffering_frozen_inputs()
    {
        var gate = new OnlineSettlementInputGate();

        OnlineSettlementInputDecision before = gate.Evaluate(false, 1, 0, true, false, SkillKeys.A, SkillKeys.A, true);
        Assert.False(before.Frozen);
        Assert.False(before.ReleaseTouch);
        Assert.False(before.JustUnfrozen);
        Assert.Equal((1, 0, true, false, SkillKeys.A, SkillKeys.A, true),
            (before.DirX, before.DirY, before.Attack, before.Jump, before.Skills, before.SkillsHeld, before.CommandsEnabled));

        // 只冻结一帧,期间玩家按了移动/普攻/跳跃/技能/指令
        OnlineSettlementInputDecision frozen = gate.Evaluate(
            true,
            -1,
            1,
            true,
            true,
            SkillKeys.S | SkillKeys.CommandSkill,
            SkillKeys.D | SkillKeys.CommandBuff | SkillKeys.Attack,
            true);
        Assert.True(frozen.Frozen);
        Assert.True(frozen.ReleaseTouch);
        Assert.False(frozen.JustUnfrozen);
        Assert.Equal((0, 0, false, false, SkillKeys.None, SkillKeys.None, false),
            (frozen.DirX, frozen.DirY, frozen.Attack, frozen.Jump, frozen.Skills, frozen.SkillsHeld, frozen.CommandsEnabled));

        // 下一帧立刻解冻且此刻按键已松开:绝不记忆或补偿冻结期间的按键
        OnlineSettlementInputDecision afterZero = gate.Evaluate(false, 0, 0, false, false, SkillKeys.None, SkillKeys.None, true);
        Assert.False(afterZero.Frozen);
        Assert.False(afterZero.ReleaseTouch);
        Assert.True(afterZero.JustUnfrozen);
        Assert.Equal((0, 0, false, false, SkillKeys.None, SkillKeys.None, true),
            (afterZero.DirX, afterZero.DirY, afterZero.Attack, afterZero.Jump, afterZero.Skills, afterZero.SkillsHeld, afterZero.CommandsEnabled));

        // 解冻后的稳定帧恢复按真实实时输入透传
        OnlineSettlementInputDecision steady = gate.Evaluate(false, -1, 1, false, true, SkillKeys.F, SkillKeys.F | SkillKeys.Attack, true);
        Assert.False(steady.Frozen);
        Assert.False(steady.ReleaseTouch);
        Assert.False(steady.JustUnfrozen);
        Assert.Equal((-1, 1, false, true, SkillKeys.F, SkillKeys.F | SkillKeys.Attack, true),
            (steady.DirX, steady.DirY, steady.Attack, steady.Jump, steady.Skills, steady.SkillsHeld, steady.CommandsEnabled));
    }

    [Fact]
    public void Unfreeze_passes_live_inputs_verbatim_and_immediate_refreeze_retriggers_release_touch()
    {
        var gate = new OnlineSettlementInputGate();

        OnlineSettlementInputDecision f1 = gate.Evaluate(true, 1, -1, true, false, SkillKeys.A, SkillKeys.A, true);
        Assert.True(f1.Frozen);
        Assert.True(f1.ReleaseTouch);
        Assert.False(f1.JustUnfrozen);

        OnlineSettlementInputDecision f2 = gate.Evaluate(true, 1, -1, true, false, SkillKeys.A, SkillKeys.A, true);
        Assert.True(f2.Frozen);
        Assert.False(f2.ReleaseTouch);
        Assert.False(f2.JustUnfrozen);

        // 解冻那一帧正好按着实时输入:JustUnfrozen=true,实时输入原样输出
        OnlineSettlementInputDecision unfrozen = gate.Evaluate(false, -1, 1, true, false, SkillKeys.G, SkillKeys.G | SkillKeys.CommandBuff, true);
        Assert.False(unfrozen.Frozen);
        Assert.False(unfrozen.ReleaseTouch);
        Assert.True(unfrozen.JustUnfrozen);
        Assert.Equal((-1, 1, true, false, SkillKeys.G, SkillKeys.G | SkillKeys.CommandBuff, true),
            (unfrozen.DirX, unfrozen.DirY, unfrozen.Attack, unfrozen.Jump, unfrozen.Skills, unfrozen.SkillsHeld, unfrozen.CommandsEnabled));

        // 仅隔一帧立刻再冻结:必须再次触发 ReleaseTouch=true
        OnlineSettlementInputDecision refrozen = gate.Evaluate(true, -1, 1, true, false, SkillKeys.G, SkillKeys.G | SkillKeys.CommandBuff, true);
        Assert.True(refrozen.Frozen);
        Assert.True(refrozen.ReleaseTouch);
        Assert.False(refrozen.JustUnfrozen);
        Assert.Equal((0, 0, false, false, SkillKeys.None, SkillKeys.None, false),
            (refrozen.DirX, refrozen.DirY, refrozen.Attack, refrozen.Jump, refrozen.Skills, refrozen.SkillsHeld, refrozen.CommandsEnabled));

        OnlineSettlementInputDecision refrozenNext = gate.Evaluate(true, 1, 0, false, true, SkillKeys.H, SkillKeys.H, false);
        Assert.True(refrozenNext.Frozen);
        Assert.False(refrozenNext.ReleaseTouch);
        Assert.False(refrozenNext.JustUnfrozen);
    }

    [Fact]
    public void Reset_clears_frozen_history_for_both_reentry_and_inactive_continuation()
    {
        var gate = new OnlineSettlementInputGate();

        // 先进入冻结并停留两帧
        Assert.True(gate.Evaluate(true, 0, 0, false, false, SkillKeys.None, SkillKeys.None, true).ReleaseTouch);
        Assert.False(gate.Evaluate(true, 0, 0, false, false, SkillKeys.None, SkillKeys.None, true).ReleaseTouch);

        // Reset 后再次激活:视为新进入,ReleaseTouch 必须再次为 true
        gate.Reset();
        OnlineSettlementInputDecision afterResetActive = gate.Evaluate(true, 1, 1, true, true, SkillKeys.A, SkillKeys.A, true);
        Assert.True(afterResetActive.Frozen);
        Assert.True(afterResetActive.ReleaseTouch);
        Assert.False(afterResetActive.JustUnfrozen);

        // 再次 Reset 后处于未激活:JustUnfrozen 必须为 false(不能残余上一次的冻结记忆)
        gate.Reset();
        OnlineSettlementInputDecision afterResetInactive = gate.Evaluate(false, -1, 1, false, true, SkillKeys.S, SkillKeys.S, true);
        Assert.False(afterResetInactive.Frozen);
        Assert.False(afterResetInactive.ReleaseTouch);
        Assert.False(afterResetInactive.JustUnfrozen);
        Assert.Equal((-1, 1, false, true, SkillKeys.S, SkillKeys.S, true),
            (afterResetInactive.DirX, afterResetInactive.DirY, afterResetInactive.Attack, afterResetInactive.Jump,
                afterResetInactive.Skills, afterResetInactive.SkillsHeld, afterResetInactive.CommandsEnabled));
    }

    [Fact]
    public void SettleRig_reaches_MenuReady_and_public_IsActive_matches_gate_freeze_and_unfreeze()
    {
        using var rig = new OnlineSettlementGapAKit.SettleRig();
        OnlineSettlementGapAKit.WireStandardDungeonResponder(rig, monsterCount: 1);
        OnlineSettlementGapAKit.AutoAdvance(rig.Transport, OnlineSettlementGapAKit.CardRightBytes(1));

        var gate = new OnlineSettlementInputGate();

        // 进副本前与战斗中:结算未激活,门控透传输入
        rig.EnterTownAndDungeon();
        Assert.False(rig.SettleCtrl.IsActive);
        OnlineSettlementInputDecision combat = gate.Evaluate(
            rig.SettleCtrl.IsActive,
            1,
            -1,
            true,
            false,
            SkillKeys.A,
            SkillKeys.A | SkillKeys.Attack,
            true);
        Assert.False(combat.Frozen);
        Assert.False(combat.ReleaseTouch);
        Assert.False(combat.JustUnfrozen);
        Assert.Equal((1, -1, true, false, SkillKeys.A, SkillKeys.A | SkillKeys.Attack, true),
            (combat.DirX, combat.DirY, combat.Attack, combat.Jump, combat.Skills, combat.SkillsHeld, combat.CommandsEnabled));

        // 杀光怪物并推进到结算菜单就绪(MenuReady)
        rig.KillAllInCurrentRun();
        rig.PumpUntil(() => rig.SettleCtrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.True(rig.SettleCtrl.IsActive);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.SettleCtrl.Phase);

        // 结算激活第一帧:ReleaseTouch=true,所有动作与指令输出归零
        OnlineSettlementInputDecision freezeEntry = gate.Evaluate(
            rig.SettleCtrl.IsActive,
            1,
            1,
            true,
            true,
            SkillKeys.S | SkillKeys.CommandSkill,
            SkillKeys.D | SkillKeys.CommandBuff | SkillKeys.Attack,
            true);
        Assert.True(freezeEntry.Frozen);
        Assert.True(freezeEntry.ReleaseTouch);
        Assert.False(freezeEntry.JustUnfrozen);
        Assert.Equal((0, 0, false, false, SkillKeys.None, SkillKeys.None, false),
            (freezeEntry.DirX, freezeEntry.DirY, freezeEntry.Attack, freezeEntry.Jump,
                freezeEntry.Skills, freezeEntry.SkillsHeld, freezeEntry.CommandsEnabled));

        // 结算激活第二帧:ReleaseTouch=false,仍然保持冻结
        OnlineSettlementInputDecision freezeHold = gate.Evaluate(
            rig.SettleCtrl.IsActive,
            -1,
            0,
            true,
            false,
            SkillKeys.H,
            SkillKeys.H,
            true);
        Assert.True(freezeHold.Frozen);
        Assert.False(freezeHold.ReleaseTouch);
        Assert.False(freezeHold.JustUnfrozen);
        Assert.Equal((0, 0, false, false, SkillKeys.None, SkillKeys.None, false),
            (freezeHold.DirX, freezeHold.DirY, freezeHold.Attack, freezeHold.Jump,
                freezeHold.Skills, freezeHold.SkillsHeld, freezeHold.CommandsEnabled));

        // 在菜单上选回城镇并完成服务端回城信号,控制器回到 Idle(IsActive=false)
        Assert.True(rig.SettleCtrl.Choose(SettlementChoice.ReturnToTown));
        rig.Transport.Push(PacketClass.Cmd, SettlementMenuPayloads.EplpCommandPacketId,
            OnlineSettlementGapAKit.EplpOk((byte)EplpCommand.ReturnToTown));
        OnlineSettlementGapAKit.PushNoti2(rig.Transport);
        rig.PumpUntil(() => !rig.SettleCtrl.IsActive && rig.Flow.Stage == GameStage.Town);
        Assert.Equal(OnlineSettlementPhase.Idle, rig.SettleCtrl.Phase);
        Assert.False(rig.SettleCtrl.IsActive);

        // 控制器退出激活后:JustUnfrozen=true,按当前真实输入恢复,不残留冻结期间按键
        OnlineSettlementInputDecision unfrozen = gate.Evaluate(
            rig.SettleCtrl.IsActive,
            0,
            0,
            false,
            false,
            SkillKeys.None,
            SkillKeys.None,
            true);
        Assert.False(unfrozen.Frozen);
        Assert.False(unfrozen.ReleaseTouch);
        Assert.True(unfrozen.JustUnfrozen);
        Assert.Equal((0, 0, false, false, SkillKeys.None, SkillKeys.None, true),
            (unfrozen.DirX, unfrozen.DirY, unfrozen.Attack, unfrozen.Jump,
                unfrozen.Skills, unfrozen.SkillsHeld, unfrozen.CommandsEnabled));
    }
}
