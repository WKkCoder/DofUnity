using System;
using System.Collections.Generic;

namespace Dnf.Game.Act
{
    /// <summary>
    /// 怪物未决簇 1 的宿主扩展口:逻辑状态读写([SET VISIBLITY]、[SET GRABABLE]、[SET DAMAGE CHECK]、[SET UNBEATABLE]、
    /// [SET PASSTYPE]、[SET WIDTH]、[CHANGE TEAM]、[DEFAULT TEAM])与条件读口([CHECK GHOST]、[CHARGED TIME])。
    /// 没有接入此接口的替身或无关实体安全退回默认语义。
    /// </summary>
    public interface IActRestStateEntity
    {
        bool IsActGhost { get; }

        int ActChargedTimeMs { get; }

        void SetActVisibility(bool visible);

        void SetActGrabbable(bool grabbable);

        void SetActDamageCheck(bool on, int durationMs);

        void SetActUnbeatable(bool on, int durationMs);

        void SetActPassType(int passType);

        void SetActWidth(int widthX, int widthY);

        bool TryBeginActChangeTeam(int newTeam, int durationMs, int hpMaxPercent, IActEntity source, out int originalTeam);

        bool TryEndActChangeTeam(out int originalTeam, out int hpMaxPercent, out IActEntity source);
    }

    public sealed partial class ActRunner
    {
        /// <summary>此对象移动碰壁或落地(axis: 0=X, 1=Y, 2=Z);唤醒带 [ON MOVE COLLISION] 的触发器。</summary>
        public void OnMoveCollision(int axis)
        {
            if (_inert.Length == 0)
            {
                return;
            }
            _aborted = false;
            for (int i = 0; i < _inert.Length && !_aborted; i++)
            {
                if (_event[i] == ActRecordKind.OnMoveCollision)
                {
                    Evaluate(i, _frame, ActRecordKind.OnMoveCollision, axis);
                }
            }
        }

        private static void ApplyAxisIndex(int axis, ref bool allowX, ref bool allowY, ref bool allowZ)
        {
            if (axis == 0)
            {
                allowX = true;
                allowY = false;
                allowZ = false;
            }
            else if (axis == 1)
            {
                allowX = false;
                allowY = true;
                allowZ = false;
            }
            else if (axis == 2)
            {
                allowX = false;
                allowY = false;
                allowZ = true;
            }
        }

        private static bool IsMoveAxisAllowed(int axis, bool allowX, bool allowY, bool allowZ, bool seenEvent)
        {
            switch (axis)
            {
                case 0:
                    return allowX;
                case 1:
                    return allowY;
                case 2:
                    return allowZ;
                default:
                    return seenEvent;
            }
        }

        private bool CheckGhostPasses()
        {
            if (_owner.Kind == ActEntityKind.Passive)
            {
                return false;
            }
            _scratch.Clear();
            _host.Resolve(_owner, ActSelector.Passive, -1, _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                IActEntity e = _scratch[i];
                if (e != null && e.Kind == ActEntityKind.Passive && e is IActRestStateEntity rest && rest.IsActGhost)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool KeepChargedTime(ActRecord rec, IActEntity e)
        {
            if (e.Kind != ActEntityKind.Monster)
            {
                return false;
            }
            int charged = e is IActRestStateEntity rest ? rest.ActChargedTimeMs : 0;
            return ActCompares.Test(charged, rec.Sign, rec.Number);
        }

        private void ExecuteCluster1Behavior(ActBehavior b, List<IActEntity> targets)
        {
            switch (b.Kind)
            {
                case ActBehaviorKind.SetVisibility:
                    foreach (IActEntity t in targets)
                    {
                        if (t.IsAlive && t is IActRestStateEntity rest)
                        {
                            rest.SetActVisibility(b.RestFlag);
                        }
                    }
                    break;
                case ActBehaviorKind.SetGrabable:
                    if (_owner.Kind == ActEntityKind.Monster && _owner is IActRestStateEntity ownerGrab)
                    {
                        ownerGrab.SetActGrabbable(b.RestFlag);
                    }
                    break;
                case ActBehaviorKind.SetDamageCheck:
                    if (_owner.Kind == ActEntityKind.Monster && _owner is IActRestStateEntity ownerDmg)
                    {
                        ownerDmg.SetActDamageCheck(b.RestFlag, b.RestDurationMs);
                    }
                    break;
                case ActBehaviorKind.SetUnbeatable:
                    foreach (IActEntity t in targets)
                    {
                        if (t.Kind != ActEntityKind.Passive && t is IActRestStateEntity rest)
                        {
                            rest.SetActUnbeatable(b.RestFlag, b.RestDurationMs);
                        }
                    }
                    break;
                case ActBehaviorKind.SetPassType:
                {
                    int mapped = b.PassType == 5 ? 7 : b.PassType;
                    foreach (IActEntity t in targets)
                    {
                        if (t.IsAlive && t.Kind == ActEntityKind.Passive && t is IActRestStateEntity rest)
                        {
                            rest.SetActPassType(mapped);
                        }
                    }
                    break;
                }
                case ActBehaviorKind.SetWidth:
                {
                    int w0 = b.WidthX.Eval(_rand);
                    int w1 = b.WidthY.Eval(_rand);
                    foreach (IActEntity t in targets)
                    {
                        if (t.IsAlive && t is IActRestStateEntity rest)
                        {
                            rest.SetActWidth(w0, w1);
                        }
                    }
                    break;
                }
                case ActBehaviorKind.ChangeTeam:
                    ExecuteChangeTeam(b, targets);
                    break;
                case ActBehaviorKind.DefaultTeam:
                    ExecuteDefaultTeam(targets);
                    break;
            }
        }

        private void ExecuteChangeTeam(ActBehavior b, List<IActEntity> targets)
        {
            foreach (IActEntity t in targets)
            {
                if (!t.IsAlive || t.Kind == ActEntityKind.Passive || !(t is IActRestStateEntity rest))
                {
                    continue;
                }
                if (!rest.TryBeginActChangeTeam(b.RestTeam, b.RestDurationMs, b.HpMaxPercent, t, out int origTeam))
                {
                    continue;
                }
                if (t.Kind == ActEntityKind.Character)
                {
                    CascadeRoomChildTeams(t, origTeam, b.RestTeam);
                }
                t.SetActTeam(b.RestTeam);
                t.AddStatAppendage(new StatChange(StatChangeTypes.HpMax, true, b.HpMaxPercent), b.RestDurationMs, t);
            }
        }

        private void ExecuteDefaultTeam(List<IActEntity> targets)
        {
            foreach (IActEntity t in targets)
            {
                if (!t.IsAlive || t.Kind == ActEntityKind.Passive || !(t is IActRestStateEntity rest))
                {
                    continue;
                }
                int currentTeam = t.Team;
                if (!rest.TryEndActChangeTeam(out int origTeam, out int hpPct, out IActEntity source))
                {
                    continue;
                }
                if (t.Kind == ActEntityKind.Character)
                {
                    CascadeRoomChildTeams(t, currentTeam, origTeam);
                }
                t.SetActTeam(origTeam);
                t.RemoveStatAppendage(new StatChange(StatChangeTypes.HpMax, true, hpPct), source);
            }
        }

        private void CascadeRoomChildTeams(IActEntity master, int matchTeam, int newTeam)
        {
            _scratch.Clear();
            _host.Resolve(master, ActSelector.Passive, -1, _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                IActEntity c = _scratch[i];
                if (c != null && c.Kind == ActEntityKind.Passive && ReferenceEquals(c.Master, master) && c.Team == matchTeam)
                {
                    c.SetActTeam(newTeam);
                }
            }
            _scratch.Clear();
            _host.Resolve(master, ActSelector.Monster, -1, _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                IActEntity c = _scratch[i];
                if (c != null && c.Kind == ActEntityKind.Monster && ReferenceEquals(c.Master, master) && c.Team == matchTeam)
                {
                    c.SetActTeam(newTeam);
                }
            }
        }
    }
}
