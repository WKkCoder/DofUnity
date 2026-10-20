using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>游戏流程的阶段。城镇与副本是有可操作主角的场景;副本选择是一张没有主角的整屏界面。</summary>
    public enum GameStage
    {
        Town = 0,
        DungeonSelect = 1,
        Dungeon = 2,
    }

    /// <summary>
    /// 一局副本游戏:有主角的场景,通关后给出结算菜单上选了什么(<see cref="DungeonSession"/> 实现它;流程只依赖这个接口,
    /// 所以流程的逻辑用不带任何原版数据的假会话就能测)。
    /// </summary>
    public interface IRunSession : IHeroSession
    {
        /// <summary>结算菜单上选定的项;还没选(或还没到结算)是 <see cref="SettlementChoice.None"/>。</summary>
        SettlementChoice Choice { get; }
    }

    /// <summary>流程里的一次阶段切换(日志与检查用)。</summary>
    public readonly struct FlowEvent
    {
        /// <summary>切换之后的 <see cref="GameFlow.StageSerial"/>。</summary>
        public int Serial { get; }

        public GameStage From { get; }

        public GameStage To { get; }

        /// <summary>为什么切换:start、choice Retry、select LORIEN、select Back……</summary>
        public string Reason { get; }

        public FlowEvent(int serial, GameStage from, GameStage to, string reason)
        {
            Serial = serial;
            From = from;
            To = to;
            Reason = reason ?? string.Empty;
        }

        public override string ToString()
        {
            return "#" + Serial + " " + From + " -> " + To + " (" + Reason + ")";
        }
    }

    /// <summary>
    /// 游戏流程骨架:城镇 → 副本选择 → 副本 → 结算 → 再来一次 / 选择其它地下城 / 返回城镇,在阶段之间切换并持有当前阶段的内容。
    /// 纯逻辑,不画任何东西、不读键盘:宿主(Unity 的 TownDemo 或命令行检查)每帧推进当前阶段的会话,然后调 <see cref="Poll"/>;
    /// 阶段变了(<see cref="StageSerial"/> 变化)就换掉视图。会话由外面的工厂造(带数据目录的真会话,或测试里的假会话)。
    ///
    /// 结算菜单的去向(证据 E-091 的菜单,原版热键 F13 再次挑战、F11 选择其它地下城、F12 返回城镇):
    /// 再次挑战 = 同一座副本重新开一局(<see cref="Runs"/> 加一,工厂拿到局序号好换随机种子);选择其它地下城 = 回到副本选择界面,
    /// 选中刚打过的那座;返回城镇 = 回到城镇。快速组队在原版里没有队伍时是灰的,这里不会被选中。
    /// 阶段之间原版有加载画面(基线 S06 的进图过渡),这里先瞬间切换(没做)。
    /// 从城镇进副本选择、在原版里是走到副本入口,这里由宿主调 <see cref="EnterSelect"/> 占位(城镇的真实入口触发还没做)。
    /// </summary>
    public sealed class GameFlow
    {
        private readonly Func<IHeroSession> _town;
        private readonly Func<string, int, IRunSession> _dungeon;
        private readonly List<DungeonEntry> _entries;
        private readonly List<FlowEvent> _history = new List<FlowEvent>();

        /// <summary>
        /// <paramref name="town"/> 造城镇会话;<paramref name="dungeon"/> 按(副本路径, 局序号)造一局副本;
        /// <paramref name="entries"/> 是副本选择界面上的副本列表。造好之后还要调 <see cref="EnterTown"/>、<see cref="EnterSelect"/> 或 <see cref="StartDungeon"/> 开始。
        /// </summary>
        public GameFlow(Func<IHeroSession> town, Func<string, int, IRunSession> dungeon, IEnumerable<DungeonEntry> entries)
        {
            _town = town ?? throw new ArgumentNullException(nameof(town));
            _dungeon = dungeon ?? throw new ArgumentNullException(nameof(dungeon));
            _entries = new List<DungeonEntry>(entries ?? throw new ArgumentNullException(nameof(entries)));
        }

        /// <summary>当前阶段;开始之前(<see cref="StageSerial"/> 为 0)没有意义。</summary>
        public GameStage Stage { get; private set; }

        /// <summary>每次阶段切换(包括"再次挑战"重开同一座副本)加一;从 0 开始,0 表示还没开始。宿主靠它判断要不要换视图。</summary>
        public int StageSerial { get; private set; }

        /// <summary>城镇或副本阶段里有主角的会话;副本选择阶段是 null。</summary>
        public IHeroSession Session { get; private set; }

        /// <summary>副本阶段里的那局副本;其它阶段是 null。</summary>
        public IRunSession Run => Stage == GameStage.Dungeon ? Session as IRunSession : null;

        /// <summary>副本选择阶段里的界面;其它阶段是 null。</summary>
        public DungeonSelectScreen Select { get; private set; }

        /// <summary>当前阶段要画的内容(带主角的会话或整屏界面);还没开始时是 null。</summary>
        public ISceneSource Scene => Select != null ? (ISceneSource)Select : Session;

        /// <summary>正在玩或刚玩过的副本路径;还没进过副本是 null。</summary>
        public string DungeonPath { get; private set; }

        /// <summary>开过多少局副本(含"再次挑战")。</summary>
        public int Runs { get; private set; }

        /// <summary>结算菜单上做过多少次选择(再次挑战、选择其它地下城、返回城镇各算一次)。</summary>
        public int Settlements { get; private set; }

        public IReadOnlyList<DungeonEntry> Entries => _entries;

        /// <summary>每次阶段切换的记录,按先后排。</summary>
        public IReadOnlyList<FlowEvent> History => _history;

        /// <summary>回到城镇(造一个新的城镇会话)。</summary>
        public void EnterTown(string reason = "town")
        {
            Switch(GameStage.Town, reason, _town(), null);
        }

        /// <summary>打开副本选择界面;选中刚打过的那座副本(没打过则选第一个)。</summary>
        public void EnterSelect(string reason = "select")
        {
            int index = 0;
            if (DungeonPath != null)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (string.Equals(_entries[i].Path, DungeonPath, StringComparison.Ordinal))
                    {
                        index = i;
                        break;
                    }
                }
            }
            Switch(GameStage.DungeonSelect, reason, null, new DungeonSelectScreen(_entries, index));
        }

        /// <summary>开一局新的副本(局序号 = 已开过的局数)。</summary>
        public void StartDungeon(string dungeonPath, string reason = "start")
        {
            if (string.IsNullOrEmpty(dungeonPath))
            {
                throw new ArgumentException("需要副本路径", nameof(dungeonPath));
            }
            IRunSession run = _dungeon(dungeonPath, Runs);
            DungeonPath = dungeonPath;
            Runs++;
            Switch(GameStage.Dungeon, reason, run, null);
        }

        /// <summary>
        /// 看当前阶段有没有出结果:副本里结算菜单选定了、副本选择界面确认或返回了,就切到相应的下一个阶段。
        /// 宿主每帧在推进完当前会话之后调一次;返回这次有没有切换。
        /// </summary>
        public bool Poll()
        {
            if (StageSerial == 0)
            {
                return false;
            }
            switch (Stage)
            {
                case GameStage.Dungeon:
                    return PollDungeon();
                case GameStage.DungeonSelect:
                    return PollSelect();
                default:
                    return false;
            }
        }

        private bool PollDungeon()
        {
            IRunSession run = Run;
            if (run == null)
            {
                return false;
            }
            SettlementChoice choice = run.Choice;
            switch (choice)
            {
                case SettlementChoice.Retry:
                    Settlements++;
                    StartDungeon(DungeonPath, "choice " + choice);
                    return true;
                case SettlementChoice.SelectDungeon:
                    Settlements++;
                    EnterSelect("choice " + choice);
                    return true;
                case SettlementChoice.ReturnToTown:
                    Settlements++;
                    EnterTown("choice " + choice);
                    return true;
                default:
                    return false; // 没选;快速组队在这里没有对应的流程
            }
        }

        private bool PollSelect()
        {
            switch (Select.Outcome)
            {
                case SelectOutcome.Enter:
                    DungeonEntry entry = Select.Selected;
                    StartDungeon(entry.Path, "select " + entry.Name);
                    return true;
                case SelectOutcome.Back:
                    EnterTown("select Back");
                    return true;
                default:
                    return false;
            }
        }

        private void Switch(GameStage to, string reason, IHeroSession session, DungeonSelectScreen select)
        {
            GameStage from = Stage;
            bool first = StageSerial == 0;
            Stage = to;
            Session = session;
            Select = select;
            StageSerial++;
            _history.Add(new FlowEvent(StageSerial, first ? to : from, to, reason));
        }
    }
}
