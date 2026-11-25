using Dnf.Render;

namespace Dnf.Client
{
    /// <summary>
    /// 联网结算输入闸门的单帧裁决结果:给出实际应交给会话的英雄输入、是否需要在本帧松开触屏以及是否刚解除冻结。
    /// </summary>
    public readonly struct OnlineSettlementInputDecision
    {
        public OnlineSettlementInputDecision(
            bool frozen,
            bool releaseTouch,
            bool justUnfrozen,
            int dirX,
            int dirY,
            bool attack,
            bool jump,
            SkillKeys skills,
            SkillKeys skillsHeld,
            bool commandsEnabled)
        {
            Frozen = frozen;
            ReleaseTouch = releaseTouch;
            JustUnfrozen = justUnfrozen;
            DirX = dirX;
            DirY = dirY;
            Attack = attack;
            Jump = jump;
            Skills = skills;
            SkillsHeld = skillsHeld;
            CommandsEnabled = commandsEnabled;
        }

        /// <summary>当前帧是否处于联网结算冻结期。</summary>
        public bool Frozen { get; }

        /// <summary>是否刚进入冻结期(仅第一帧为 true,用于松开触屏残留手指)。</summary>
        public bool ReleaseTouch { get; }

        /// <summary>是否刚离开冻结期(仅恢复后的第一帧为 true)。</summary>
        public bool JustUnfrozen { get; }

        /// <summary>实际交给会话的横向方向输入。</summary>
        public int DirX { get; }

        /// <summary>实际交给会话的纵向方向输入。</summary>
        public int DirY { get; }

        /// <summary>实际交给会话的攻击按下边沿。</summary>
        public bool Attack { get; }

        /// <summary>实际交给会话的跳跃按下边沿。</summary>
        public bool Jump { get; }

        /// <summary>实际交给会话的技能按下边沿。</summary>
        public SkillKeys Skills { get; }

        /// <summary>实际交给会话的按住技能集合。</summary>
        public SkillKeys SkillsHeld { get; }

        /// <summary>实际交给会话的出招指令总开关。</summary>
        public bool CommandsEnabled { get; }
    }

    /// <summary>
    /// 联网结算期间的纯逻辑英雄输入冻结闸门(r20-settlefreeze):
    /// 结算控制器激活期间将方向、攻击、跳跃、技能边沿、按住技能与出招指令全部归零,
    /// 仅在进入冻结的那一帧置 <see cref="OnlineSettlementInputDecision.ReleaseTouch"/>;
    /// 结算结束后立即按实时输入恢复,不记忆或补偿冻结期间的按键。
    /// </summary>
    public sealed class OnlineSettlementInputGate
    {
        private bool _wasFrozen;

        /// <summary>评估本帧输入并更新冻结边沿状态。</summary>
        public OnlineSettlementInputDecision Evaluate(
            bool settlementActive,
            int rawDirX,
            int rawDirY,
            bool rawAttack,
            bool rawJump,
            SkillKeys rawSkills,
            SkillKeys rawSkillsHeld,
            bool rawCommandsEnabled)
        {
            bool wasFrozen = _wasFrozen;
            _wasFrozen = settlementActive;
            if (settlementActive)
            {
                return new OnlineSettlementInputDecision(
                    frozen: true,
                    releaseTouch: !wasFrozen,
                    justUnfrozen: false,
                    dirX: 0,
                    dirY: 0,
                    attack: false,
                    jump: false,
                    skills: SkillKeys.None,
                    skillsHeld: SkillKeys.None,
                    commandsEnabled: false);
            }
            return new OnlineSettlementInputDecision(
                frozen: false,
                releaseTouch: false,
                justUnfrozen: wasFrozen,
                dirX: rawDirX,
                dirY: rawDirY,
                attack: rawAttack,
                jump: rawJump,
                skills: rawSkills,
                skillsHeld: rawSkillsHeld,
                commandsEnabled: rawCommandsEnabled);
        }

        /// <summary>重置边沿状态(例如重新启动流程时)。</summary>
        public void Reset()
        {
            _wasFrozen = false;
        }
    }
}
