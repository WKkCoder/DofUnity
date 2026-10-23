using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 无人值守的流程自动机:替玩家走完整条流程(选角 → 城镇 → 副本选择 → 副本 → 结算 → 再来一次 / 选择其它地下城 / 返回城镇),
    /// 用来在没有人的环境里检查流程(命令行 flow-check、播放器的 -dnfflow -dnfbot)。
    /// 选角界面上停一会儿(<see cref="CharacterSelectDwellMs"/>)再确认选中的角色;
    /// 副本里交给 <see cref="DungeonBot"/> 打;结算菜单出现之后等一会儿,按预设的清单依次做选择(再次挑战、选择其它地下城、返回城镇);
    /// 清单用完就算结束(<see cref="Done"/>):清单以返回城镇收尾时停在城镇,否则停在最后一局的结算菜单上(那一次不再选)。
    /// 城镇里沿出口走到地下城入口区域、走出通往世界地图的出口(证据 E-095;城镇世界不给路线时退回站一会儿再走下一步的占位);
    /// 副本选择里是占位:停一会儿再确认(选择界面上的按键由玩家按,自动机直接确认);加载画面等它停够时间。
    /// 和 <see cref="DungeonBot"/> 一样,输入只来自它自己的决定,不碰随机数,所以同样的种子与清单总是走出同样的流程。
    /// </summary>
    public sealed class FlowBot
    {
        /// <summary>城镇世界不给路线时,在城镇里站多久再走进副本选择(占位)。</summary>
        public const double TownDwellMs = 1500.0;

        /// <summary>沿路线走了这么久还没走到地下城入口就当卡住了(<see cref="Stuck"/>):直接进副本选择让流程接着走。</summary>
        public const double TownTimeoutMs = 120000.0;

        /// <summary>选角界面上停多久再确认(占位;让背景光束与头像的动画先走一会儿)。</summary>
        public const double CharacterSelectDwellMs = 1500.0;

        /// <summary>副本选择界面上停多久再确认(占位;要比世界地图的开场动画长,卡片淡入之后才确认)。</summary>
        public const double SelectDwellMs = 2500.0;

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

        /// <summary>城镇里沿路线走了 <see cref="TownTimeoutMs"/> 还没走到入口(走不通的地图、路线与可走区域对不上)。</summary>
        public bool Stuck { get; private set; }

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
                case GameStage.Loading:
                    flow.Splash.Advance(deltaMs);
                    break;
                case GameStage.CharacterSelect:
                    flow.CharacterSelect.Advance(deltaMs);
                    if (_stageMs >= CharacterSelectDwellMs)
                    {
                        flow.CharacterSelect.Confirm();      // 选中的是第一个角色;确认之后流程登录进城
                    }
                    break;
                case GameStage.Town:
                    AdvanceTown(flow, deltaMs);
                    break;
                case GameStage.DungeonSelect:
                    flow.Select.Advance(deltaMs);
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
            bool routed = false;
            int dirX = 0, dirY = 0;
            if (!(ChoicesMade >= _choices.Count && ChoicesMade > 0) && flow.Place.HasValue && flow.Session is ITownExitSource source
                && flow.TownWorld.TryRoute(flow.Place.Value, out TownExit exit))
            {
                routed = true;
                source.OutwardOf(exit, out int outwardX, out int outwardY);
                TownBot.Direction(source.HeroX, source.HeroY, exit.Rect, outwardX, outwardY, out dirX, out dirY);
            }
            flow.Session.Update(deltaMs, dirX, dirY);
            if (ChoicesMade >= _choices.Count && ChoicesMade > 0)
            {
                Done = true;                      // 选择用完、回到了城镇
                return;
            }
            if (routed)
            {
                if (_stageMs >= TownTimeoutMs)
                {
                    Stuck = true;
                    flow.EnterSelect("bot stuck");
                }
                return;                           // 走到出口之后由流程在 Poll 里接手(换区域或打开世界地图)
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
            if (_bot != null && _bot.WantsToCollect(dungeon))
            {
                _bot.Advance(dungeon, deltaMs);  // 副本走完:先把地上的金币捡完(限时),时间照样走,结算画面随之出现
            }
            else
            {
                dungeon.Update(deltaMs, 0, 0);   // 主角站着不动,只让时间走
            }
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
