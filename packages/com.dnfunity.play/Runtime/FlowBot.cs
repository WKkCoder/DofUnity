using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 无人值守的流程自动机:替玩家走完整条流程(城镇 → 副本选择 → 副本 → 结算 → 再来一次 / 选择其它地下城 / 返回城镇),
    /// 用来在没有人的环境里检查流程(命令行 flow-check、播放器的 -dnfflow -dnfbot)。
    /// 副本里交给 <see cref="DungeonBot"/> 打;结算菜单出现之后等一会儿,按预设的清单依次做选择(再次挑战、选择其它地下城、返回城镇);
    /// 清单用完就算结束(<see cref="Done"/>):清单以返回城镇收尾时停在城镇,否则停在最后一局的结算菜单上(那一次不再选)。
    /// 城镇与副本选择里是占位:站一会儿再走下一步(城镇的真实入口触发、选择界面上的按键还没做)。
    /// 和 <see cref="DungeonBot"/> 一样,输入只来自它自己的决定,不碰随机数,所以同样的种子与清单总是走出同样的流程。
    /// </summary>
    public sealed class FlowBot
    {
        /// <summary>城镇里站多久再走进副本选择(占位)。</summary>
        public const double TownDwellMs = 1500.0;

        /// <summary>副本选择界面上停多久再确认(占位)。</summary>
        public const double SelectDwellMs = 1000.0;

        /// <summary>结算菜单出现之后等多久再做选择(占位)。</summary>
        public const double SettlementDwellMs = 1500.0;

        private readonly bool _explore;
        private readonly List<SettlementChoice> _choices;
        private DungeonBot _bot;
        private int _serial;
        private double _stageMs;

        /// <summary>
        /// <paramref name="explore"/> 让副本里的自动机先逛遍所有房间再打首领;<paramref name="choices"/> 是每次结算依次做的选择
        /// (只有再次挑战、选择其它地下城、返回城镇有意义),用完之后不再进副本。
        /// </summary>
        public FlowBot(bool explore, IEnumerable<SettlementChoice> choices)
        {
            _explore = explore;
            _choices = new List<SettlementChoice>();
            foreach (SettlementChoice c in choices ?? throw new ArgumentNullException(nameof(choices)))
            {
                if (c != SettlementChoice.Retry && c != SettlementChoice.SelectDungeon && c != SettlementChoice.ReturnToTown)
                {
                    throw new ArgumentException("结算选项只能是再次挑战、选择其它地下城、返回城镇: " + c, nameof(choices));
                }
                _choices.Add(c);
            }
        }

        /// <summary>清单里已经用掉几个选择。</summary>
        public int ChoicesMade { get; private set; }

        /// <summary>清单用完了(回到了城镇,或者停在没有选择可做的结算菜单上):流程走完,不会再有动作。</summary>
        public bool Done { get; private set; }

        /// <summary>
        /// 把流程往前推 <paramref name="deltaMs"/> 毫秒:推进当前阶段的会话、必要时替玩家做决定,最后调 <see cref="GameFlow.Poll"/> 应用结果。
        /// 副本里的自动机按固定心跳决策,所以帧长不影响路线。已经 <see cref="Done"/> 时不做任何事。
        /// </summary>
        public void Advance(GameFlow flow, double deltaMs)
        {
            if (flow == null)
            {
                throw new ArgumentNullException(nameof(flow));
            }
            if (Done || flow.StageSerial == 0)
            {
                return;
            }
            if (flow.StageSerial != _serial)
            {
                _serial = flow.StageSerial;
                _stageMs = 0.0;
                _bot = null;
            }
            _stageMs += deltaMs;
            switch (flow.Stage)
            {
                case GameStage.Town:
                    AdvanceTown(flow, deltaMs);
                    break;
                case GameStage.DungeonSelect:
                    if (_stageMs >= SelectDwellMs)
                    {
                        flow.Select.Confirm();
                    }
                    break;
                case GameStage.Dungeon:
                    AdvanceDungeon(flow, deltaMs);
                    break;
            }
            flow.Poll();
        }

        private void AdvanceTown(GameFlow flow, double deltaMs)
        {
            flow.Session.Update(deltaMs, 0, 0);
            if (ChoicesMade >= _choices.Count && ChoicesMade > 0)
            {
                Done = true;                      // 选择用完、回到了城镇
                return;
            }
            if (_stageMs >= TownDwellMs)
            {
                flow.EnterSelect("bot");
            }
        }

        private void AdvanceDungeon(GameFlow flow, double deltaMs)
        {
            if (!(flow.Run is DungeonSession dungeon))
            {
                return;
            }
            if (!dungeon.Finished)
            {
                _bot = _bot ?? new DungeonBot(_explore);
                _bot.Advance(dungeon, deltaMs);
                return;
            }
            dungeon.Update(deltaMs, 0, 0);       // 副本走完:主角站着不动,只让时间走,结算画面随之出现
            if (dungeon.SettlementActive && dungeon.Choice == SettlementChoice.None && dungeon.SettlementAgeMs >= SettlementDwellMs)
            {
                if (ChoicesMade < _choices.Count)
                {
                    dungeon.Choose(_choices[ChoicesMade]);
                    ChoicesMade++;
                }
                else
                {
                    Done = true;                  // 清单用完了、停在这一次的结算菜单上(清单没有以返回城镇收尾,或者本来就是空的)
                }
            }
        }
    }
}
