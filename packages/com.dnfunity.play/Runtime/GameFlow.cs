using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>游戏流程的阶段。城镇与副本是有可操作主角的场景;副本选择是一张没有主角的整屏界面;加载是切换场景时的整屏过场图。</summary>
    public enum GameStage
    {
        Town = 0,
        DungeonSelect = 1,
        Dungeon = 2,

        /// <summary>加载画面(<see cref="SplashScreen"/>):停够时间之后流程切到目标阶段(证据 E-095)。只有给了加载画面素材的流程才会出现这个阶段。</summary>
        Loading = 3,

        /// <summary>选角界面(<see cref="CharacterSelectScreen"/>):流程的起点,确认之后用选中的角色登录进城(证据 E-096)。只有给了选角界面工厂的流程才会出现这个阶段。</summary>
        CharacterSelect = 4,
    }

    /// <summary>
    /// 一局副本游戏:有主角的场景,通关后给出结算菜单上选了什么(<see cref="DungeonSession"/> 实现它;流程只依赖这个接口,
    /// 所以流程的逻辑用不带任何原版数据的假会话就能测)。
    /// </summary>
    public interface IRunSession : IHeroSession
    {
        /// <summary>结算菜单上选定的项;还没选(或还没到结算)是 <see cref="SettlementChoice.None"/>。</summary>
        SettlementChoice Choice { get; }

        /// <summary>
        /// 副本失败(证据 E-115):主角死后复活窗口的失败计时走完了还没复活。流程据此让主角出副本回城,不进结算菜单、不计入 <see cref="GameFlow.Settlements"/>。
        /// </summary>
        bool Failed { get; }
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
    /// 城镇由 <see cref="ITownWorld"/> 供给(证据 E-095):登录落在第一站,走进出口换区域或(地下城入口区域的出口)打开世界地图,
    /// 从副本回来落在地下城入口区域;换城镇、进出网关区域、进副本时先停在加载画面(<see cref="GameStage.Loading"/>:过场图 + 边条 + 标题)。
    /// 没有城镇世界(老式的单会话工厂,<see cref="LegacyTownWorld"/>)或没给副本加载画面时,对应的切换是瞬间的,与之前一致。
    /// 城镇里的出口触发由会话判断(<see cref="ITownExitSource"/>),流程在 <see cref="Poll"/> 里接手;宿主与自动机只管推进当前阶段。
    /// 流程的起点可以是选角界面(<see cref="EnterCharacterSelect"/>,证据 E-096):确认之后用选中的角色登录,落在城镇世界的第一站
    /// (与 <see cref="EnterTown"/> 在流程刚开始时一样,中间照样经过加载画面);选角界面里"结束游戏"只是界面的结果(<see cref="CharacterSelectOutcome.Quit"/>),
    /// 流程不换阶段,由宿主决定怎么退出。
    /// 流程也是声音源(<see cref="ISoundSource"/>,证据 E-097):进选角界面发选角的背景音乐、进城镇区域发那块地图 [sound] 里的音乐与环境声
    /// (<see cref="SoundCueKind.RoomEnter"/> 标记加每一项 <see cref="SoundCueKind.RoomSound"/>),其余时候转发当前会话(副本房间、结算)发出的提示;
    /// 加载画面、副本选择界面没有自己的声音(宿主在换阶段时把旧声音停掉)。
    /// </summary>
    public sealed class GameFlow : ISoundSource
    {
        private readonly SoundCueQueue _sounds = new SoundCueQueue();
        private readonly List<SoundCue> _soundScratch = new List<SoundCue>();
        private readonly ITownWorld _townWorld;
        private readonly Func<string, int, IRunSession> _dungeon;
        private readonly Func<string, SplashScene> _dungeonSplash;
        private readonly Func<CharacterSelectScreen> _characterSelect;
        private readonly List<DungeonEntry> _entries;
        private readonly WorldMapScene _world;
        private readonly List<FlowEvent> _history = new List<FlowEvent>();
        private Action _afterSplash;

        /// <summary>
        /// 老式构造:<paramref name="town"/> 造城镇会话(每次回城造一个新的),没有出口与加载画面。其余参数同新式构造。
        /// </summary>
        public GameFlow(Func<IHeroSession> town, Func<string, int, IRunSession> dungeon, IEnumerable<DungeonEntry> entries, WorldMapScene world = null)
            : this(new LegacyTownWorld(town ?? throw new ArgumentNullException(nameof(town))), dungeon, entries, world, null)
        {
        }

        /// <summary>
        /// <paramref name="townWorld"/> 供给城镇;<paramref name="dungeon"/> 按(副本路径, 局序号)造一局副本;
        /// <paramref name="entries"/> 是副本选择界面上的副本列表;<paramref name="world"/> 不为空时选择界面画原版的世界地图(<see cref="WorldMapArt"/>),
        /// 为空是占位画面;<paramref name="dungeonSplash"/> 按副本路径给进图加载画面的素材(返回 null 或不给就直接进);
        /// <paramref name="characterSelect"/> 每次造一个新的选角界面(不给就没有选角这个阶段,<see cref="EnterCharacterSelect"/> 会报错)。
        /// 造好之后还要调 <see cref="EnterCharacterSelect"/>、<see cref="EnterTown"/>、<see cref="EnterSelect"/> 或 <see cref="StartDungeon"/> 开始。
        /// </summary>
        public GameFlow(ITownWorld townWorld, Func<string, int, IRunSession> dungeon, IEnumerable<DungeonEntry> entries, WorldMapScene world = null,
            Func<string, SplashScene> dungeonSplash = null, Func<CharacterSelectScreen> characterSelect = null)
        {
            _townWorld = townWorld ?? throw new ArgumentNullException(nameof(townWorld));
            _dungeon = dungeon ?? throw new ArgumentNullException(nameof(dungeon));
            _entries = new List<DungeonEntry>(entries ?? throw new ArgumentNullException(nameof(entries)));
            _world = world;
            _dungeonSplash = dungeonSplash;
            _characterSelect = characterSelect;
            Bag.UseItem = UseBagItem;             // 背包窗口里按回车用消耗品:交给当前阶段的会话(副本里能用,城镇里不能)
        }

        private UseResult UseBagItem(ItemInfo item)
        {
            return Session is IItemSession items ? items.TryUseItem(item) : UseResult.Unavailable;
        }

        /// <summary>
        /// 键 1 到 6:用当前副本会话里快捷栏第 <paramref name="slot"/> 格(0 到 5)的消耗品(证据 E-120),返回结果名(<see cref="UseResult"/> 的枚举名;宿主层不引用 Dnf.Game,用名字交接)。
        /// 只在副本阶段、系统菜单与背包窗口都没开的时候有效,其余返回 <see cref="ItemUse.UnavailableName"/>。
        /// </summary>
        public string UseQuickSlot(int slot)
        {
            if (StageSerial == 0 || Stage != GameStage.Dungeon || Menu.Open || Bag.Open || Shop.Open || Quests.Open)
            {
                return ItemUse.UnavailableName;
            }
            return ItemUse.QuickSlot(Session, slot);
        }

        /// <summary>城镇世界(自动机用它查出口路线)。</summary>
        public ITownWorld TownWorld => _townWorld;

        /// <summary>主角现在(或最后一次)所在的城镇区域;从没进过城镇时为 null。</summary>
        public TownPlace? Place { get; private set; }

        /// <summary>加载阶段里的画面;其它阶段是 null。</summary>
        public SplashScreen Splash { get; private set; }

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

        /// <summary>选角阶段里的界面;其它阶段是 null。</summary>
        public CharacterSelectScreen CharacterSelect { get; private set; }

        /// <summary>当前阶段要画的内容(带主角的会话、整屏界面或加载画面);还没开始时是 null。</summary>
        public ISceneSource Scene => Splash != null ? (ISceneSource)Splash
            : Select != null ? (ISceneSource)Select
            : CharacterSelect != null ? (ISceneSource)CharacterSelect
            : Session;

        /// <summary>正在玩或刚玩过的副本路径;还没进过副本是 null。</summary>
        public string DungeonPath { get; private set; }

        /// <summary>开过多少局副本(含"再次挑战")。</summary>
        public int Runs { get; private set; }

        /// <summary>结算菜单上做过多少次选择(再次挑战、选择其它地下城、返回城镇各算一次)。</summary>
        public int Settlements { get; private set; }

        /// <summary>副本失败(主角死后没复活、失败计时走完)回城的次数(证据 E-115);不计入 <see cref="Settlements"/>。</summary>
        public int Failures { get; private set; }

        /// <summary>从系统菜单主动放弃副本回城的次数(原版放弃有疲劳惩罚,这里没做);不计入 <see cref="Settlements"/> 与 <see cref="Failures"/>。</summary>
        public int GiveUps { get; private set; }

        /// <summary>
        /// 系统菜单(Esc;本项目自己的占位设计,见 <see cref="SystemMenu"/>):只在城镇与副本阶段能打开,开着的时候宿主让游戏暂停;
        /// 选定的项在下一次 <see cref="Poll"/> 里执行。换阶段时自动关上。
        /// </summary>
        public SystemMenu Menu { get; } = new SystemMenu();

        /// <summary>
        /// 背包窗口(I 键;本项目自己的占位设计,见 <see cref="BagWindow"/>,证据 E-118):只在城镇与副本阶段能打开,开着的时候宿主让游戏暂停
        /// (和系统菜单一样,<see cref="Poll"/> 在它开着时不处理各阶段的结果);换阶段时自动关上。
        /// </summary>
        public BagWindow Bag { get; } = new BagWindow();

        /// <summary>
        /// I 键:背包开着就关上;没开且当前阶段有背包(城镇、副本)、系统菜单也没开就打开。返回有没有处理这次按键。
        /// </summary>
        public bool ToggleBag()
        {
            if (StageSerial == 0)
            {
                return false;
            }
            if (Bag.Open)
            {
                Bag.Close();
                return true;
            }
            return !Menu.Open && !Shop.Open && !Quests.Open && Bag.Show(Stage);
        }

        /// <summary>
        /// NPC 商店窗口(本项目自己的占位设计,见 <see cref="ShopWindow"/>,证据 E-119):只在城镇阶段能打开(<see cref="OpenShop"/>、<see cref="TryOpenNearbyShop"/>),
        /// 开着的时候宿主让游戏暂停(和背包一样,<see cref="Poll"/> 在它开着时不处理各阶段的结果);换阶段时自动关上。
        /// 宿主要先接上数据本(<see cref="ShopWindow.UseAssets"/>)与"NPC → 商店"的对应(<see cref="UseShopNpcs"/>)。
        /// </summary>
        public ShopWindow Shop { get; } = new ShopWindow();

        private TownShopNpcs _shopNpcs;

        /// <summary>接上"NPC → 商店"的对应(宿主用游戏素材造,见 <see cref="TownShopNpcs.FromAssets"/>);没接就没有 NPC 商店。</summary>
        public void UseShopNpcs(TownShopNpcs npcs)
        {
            _shopNpcs = npcs ?? throw new ArgumentNullException(nameof(npcs));
        }

        /// <summary>
        /// 城镇里主角身边最近的商店 NPC 开的商店编号;不在城镇、会话没有 NPC 列表(<see cref="ITownNpcSource"/>)、没接对应
        /// 或范围里没有商店 NPC 时返回 −1。宿主每帧可以问一次,用来画"空格打开商店"的提示。
        /// </summary>
        public int FindNearbyShop()
        {
            if (StageSerial == 0 || Stage != GameStage.Town || _shopNpcs == null || !(Session is ITownNpcSource source))
            {
                return -1;
            }
            return _shopNpcs.TryFindNear(source.Npcs, source.HeroX, source.HeroY, out TownNpc _, out int shop) ? shop : -1;
        }

        /// <summary>
        /// 打开编号为 <paramref name="shopIndex"/> 的商店:只在城镇阶段、没有别的窗口(系统菜单、背包、商店)开着、数据本读得出这家店时成功。
        /// 返回有没有打开。
        /// </summary>
        public bool OpenShop(int shopIndex)
        {
            if (StageSerial == 0 || Menu.Open || Bag.Open || Shop.Open || Quests.Open)
            {
                return false;
            }
            return Shop.Show(Stage, shopIndex);
        }

        /// <summary>
        /// 当前城镇会话里摆着的 NPC 有几个开商店(返回值),<paramref name="npcs"/> 是摆着的 NPC 总数;不在城镇、会话没有 NPC 列表或没接对应时返回 0、总数照给(没有列表是 0)。
        /// 宿主进城时记一行日志用。
        /// </summary>
        public int CountShopNpcs(out int npcs)
        {
            npcs = 0;
            if (StageSerial == 0 || Stage != GameStage.Town || !(Session is ITownNpcSource source))
            {
                return 0;
            }
            npcs = source.Npcs.Count;
            if (_shopNpcs == null)
            {
                return 0;
            }
            int shops = 0;
            for (int i = 0; i < source.Npcs.Count; i++)
            {
                if (_shopNpcs.ShopOf(source.Npcs[i].Id) >= 0)
                {
                    shops++;
                }
            }
            return shops;
        }

        /// <summary>空格键:主角身边有商店 NPC 就打开它的商店(<see cref="FindNearbyShop"/> + <see cref="OpenShop"/>);返回有没有打开。</summary>
        public bool TryOpenNearbyShop()
        {
            int shop = FindNearbyShop();
            return shop >= 0 && OpenShop(shop);
        }

        /// <summary>
        /// 任务窗口(M31,本项目自己的占位设计,见 <see cref="QuestWindow"/>,证据 E-123):城镇里走到 NPC 旁按空格(<see cref="TryTalk"/>)打开和这个 NPC 的对话,
        /// L 键(<see cref="ToggleQuests"/>)在城镇与副本里打开任务日志;开着的时候宿主让游戏暂停(和背包、商店一样,<see cref="Poll"/> 在它开着时不处理各阶段的结果);换阶段时自动关上。
        /// 宿主要先接上任务系统(<see cref="UseQuests"/>)。
        /// </summary>
        public QuestWindow Quests { get; } = new QuestWindow();

        private QuestBook _quests;
        private Func<int, string> _npcName;

        /// <summary>接上任务系统(宿主用游戏素材造,见 <see cref="QuestBook.FromAssets"/>);没接就没有任务窗口、NPC 头顶没有标记。</summary>
        public void UseQuests(QuestBook book)
        {
            _quests = book ?? throw new ArgumentNullException(nameof(book));
            Quests.Use(book);
        }

        /// <summary>接上的任务系统;没接是 null。</summary>
        public QuestBook QuestBook => _quests;

        /// <summary>接上"NPC 编号 → 对话窗口里显示的名字"(数据文字;没接或返回 null 时窗口标题用占位词)。</summary>
        public void UseNpcNames(Func<int, string> nameOf)
        {
            _npcName = nameOf ?? throw new ArgumentNullException(nameof(nameOf));
        }

        /// <summary>NPC 头顶该画的任务标记;没接任务系统是 <see cref="QuestMark.None"/>。</summary>
        public QuestMark QuestMarkOf(int npcId)
        {
            return _quests == null ? QuestMark.None : _quests.MarkOf(npcId);
        }

        /// <summary>主角身边的 NPC 能做什么(<see cref="FindNearbyTalk"/>)。</summary>
        public enum TalkKind
        {
            /// <summary>范围里没有能说话的 NPC。</summary>
            None = 0,

            /// <summary>最近的是有任务可接、可交或在进行的 NPC(也可能同时开着商店,窗口里有"商店"按钮)。</summary>
            Quest = 1,

            /// <summary>最近的是只开商店的 NPC。</summary>
            Shop = 2,
        }

        /// <summary>
        /// 城镇里主角身边最近的、能说话的 NPC(有任务标记,或开着商店;范围同 <see cref="TownShopNpcs"/> 的椭圆)。
        /// 不在城镇、会话没有 NPC 列表或范围里没有时返回 <see cref="TalkKind.None"/>。宿主每帧可以问一次,用来画"空格对话 / 空格商店"的提示。
        /// </summary>
        public TalkKind FindNearbyTalk(out TownNpc npc)
        {
            npc = default;
            if (StageSerial == 0 || Stage != GameStage.Town || !(Session is ITownNpcSource source))
            {
                return TalkKind.None;
            }
            TalkKind found = TalkKind.None;
            double best = double.MaxValue;
            IReadOnlyList<TownNpc> npcs = source.Npcs;
            for (int i = 0; i < npcs.Count; i++)
            {
                TownNpc n = npcs[i];
                double dx = (n.X - source.HeroX) / TownShopNpcs.RangeX, dy = (n.Y - source.HeroY) / TownShopNpcs.RangeY;
                double d = dx * dx + dy * dy;
                if (d > 1.0 || d >= best)
                {
                    continue;
                }
                TalkKind kind = TalkKind.None;
                if (_quests != null && _quests.MarkOf(n.Id) != QuestMark.None)
                {
                    kind = TalkKind.Quest;
                }
                else if (_shopNpcs != null && _shopNpcs.ShopOf(n.Id) >= 0)
                {
                    kind = TalkKind.Shop;
                }
                if (kind == TalkKind.None)
                {
                    continue;
                }
                best = d;
                npc = n;
                found = kind;
            }
            return found;
        }

        /// <summary>
        /// 空格键:和主角身边最近的能说话的 NPC 说话(<see cref="FindNearbyTalk"/>)。有任务的先算一次"拜访"(<see cref="QuestBook.Talk"/>),再打开任务窗口(这个 NPC 同时开着商店时窗口里有商店按钮);
        /// 任务窗口没有可显示的任务(例如拜访任务刚完成、窗口里已经没有它的行)就退到它的商店;只开商店的直接开商店。
        /// 已有窗口开着、没有可说话的 NPC 时什么也不做。返回有没有打开窗口。
        /// </summary>
        public bool TryTalk()
        {
            if (StageSerial == 0 || Menu.Open || Bag.Open || Shop.Open || Quests.Open)
            {
                return false;
            }
            TalkKind kind = FindNearbyTalk(out TownNpc npc);
            switch (kind)
            {
                case TalkKind.Quest:
                    return OpenTalk(npc.Id);
                case TalkKind.Shop:
                    return OpenShop(_shopNpcs.ShopOf(npc.Id));
                default:
                    return false;
            }
        }

        /// <summary>和编号为 <paramref name="npcId"/> 的 NPC 说话:算一次拜访,开任务窗口(它的行都没有了就退到它的商店)。</summary>
        private bool OpenTalk(int npcId)
        {
            _quests.Talk(npcId);
            int shop = _shopNpcs == null ? -1 : _shopNpcs.ShopOf(npcId);
            if (Quests.ShowNpc(Stage, npcId, _npcName?.Invoke(npcId), shop))
            {
                return true;
            }
            return shop >= 0 && OpenShop(shop);
        }

        /// <summary>
        /// 不看距离,直接打开和编号为 <paramref name="npcId"/> 的 NPC 的任务对话(无人值守探针用;只在城镇阶段、没有别的窗口开着、接了任务系统时成功)。
        /// </summary>
        public bool OpenQuestNpc(int npcId)
        {
            if (StageSerial == 0 || _quests == null || Stage != GameStage.Town || Menu.Open || Bag.Open || Shop.Open || Quests.Open)
            {
                return false;
            }
            return OpenTalk(npcId);
        }

        /// <summary>
        /// 当前城镇会话里摆着的 NPC 有几个头顶带任务标记(返回值),按可接、可交、进行中分开数;不在城镇、会话没有 NPC 列表或没接任务系统时全是 0。宿主进城时记一行日志用。
        /// </summary>
        public int CountQuestMarks(out int available, out int ready, out int inProgress)
        {
            available = 0;
            ready = 0;
            inProgress = 0;
            if (StageSerial == 0 || Stage != GameStage.Town || _quests == null || !(Session is ITownNpcSource source))
            {
                return 0;
            }
            IReadOnlyList<TownNpc> npcs = source.Npcs;
            for (int i = 0; i < npcs.Count; i++)
            {
                switch (_quests.MarkOf(npcs[i].Id))
                {
                    case QuestMark.Available:
                        available++;
                        break;
                    case QuestMark.Ready:
                        ready++;
                        break;
                    case QuestMark.InProgress:
                        inProgress++;
                        break;
                }
            }
            return available + ready + inProgress;
        }

        /// <summary>城镇里摆着的 NPC 中第一个头顶是 <paramref name="mark"/> 的编号;没有(或不在城镇)返回 −1。无人值守探针用。</summary>
        public int FirstNpcWithMark(QuestMark mark)
        {
            if (StageSerial == 0 || Stage != GameStage.Town || _quests == null || !(Session is ITownNpcSource source))
            {
                return -1;
            }
            IReadOnlyList<TownNpc> npcs = source.Npcs;
            for (int i = 0; i < npcs.Count; i++)
            {
                if (_quests.MarkOf(npcs[i].Id) == mark)
                {
                    return npcs[i].Id;
                }
            }
            return -1;
        }

        /// <summary>
        /// L 键:任务日志开着就关上;没开且当前阶段有日志(城镇、副本)、别的窗口也没开就打开。返回有没有处理这次按键(没接任务系统不处理)。
        /// </summary>
        public bool ToggleQuests()
        {
            if (StageSerial == 0 || _quests == null)
            {
                return false;
            }
            if (Quests.Open)
            {
                Quests.Close();
                return true;
            }
            return !Menu.Open && !Bag.Open && !Shop.Open && Quests.ShowJournal(Stage);
        }

        /// <summary>任务窗口里按了"商店"(或 Tab):关上任务窗口、打开这个 NPC 的商店;窗口没开或这个 NPC 没有商店返回 false。</summary>
        public bool QuestsToShop()
        {
            if (!Quests.Open || Quests.ShopIndex < 0)
            {
                return false;
            }
            int shop = Quests.ShopIndex;
            Quests.Close();
            return OpenShop(shop);
        }

        /// <summary>系统菜单里选了"退出游戏":宿主看到它就退出(流程自己不退出)。</summary>
        public bool QuitRequested { get; private set; }

        /// <summary>
        /// Esc 键:菜单开着就关上;没开且当前阶段有系统菜单(城镇、副本)就打开。返回有没有处理这次按键
        /// (加载、选角、副本选择阶段返回 false,Esc 在那些界面里另有含义,由宿主照旧处理)。
        /// </summary>
        public bool ToggleMenu()
        {
            if (StageSerial == 0)
            {
                return false;
            }
            if (Shop.Open)
            {
                Shop.Close();            // 商店开着时 Esc 先关商店(数量框开着时 Esc 只取消数量框,由宿主的输入处理)
                return true;
            }
            if (Quests.Open)
            {
                Quests.Close();          // 任务窗口开着时 Esc 先关任务窗口
                return true;
            }
            if (Bag.Open)
            {
                Bag.Close();             // 背包开着时 Esc 先关背包
                return true;
            }
            if (Menu.Open)
            {
                Menu.Close();
                return true;
            }
            return Menu.Show(Stage);
        }

        public IReadOnlyList<DungeonEntry> Entries => _entries;

        /// <summary>每次阶段切换的记录,按先后排。</summary>
        public IReadOnlyList<FlowEvent> History => _history;

        /// <summary>
        /// 最近一次切换是不是城镇换区域(城镇 → 城镇,没有经过加载阶段):宿主这时不必把旧声音全停掉——
        /// 新区域的地图声音与旧的有相同的音乐标签时音乐接着放,只换环境声(调度器按标签对比,E-097;原版这一步的行为是推断)。
        /// 流程的第一次切换(没有"上一个阶段")不算。
        /// </summary>
        public bool LastSwitchStaysInTown => _history.Count > 1 && _history[_history.Count - 1].From == GameStage.Town && _history[_history.Count - 1].To == GameStage.Town;

        /// <summary>
        /// 打开选角界面(造一个新的,选中第一个角色)。通常是流程的起点;确认之后 <see cref="Poll"/> 用选中的角色登录进城
        /// (落在城镇世界的第一站,世界给了加载画面就先停在加载阶段)。没给选角界面工厂时报错。
        /// </summary>
        public void EnterCharacterSelect(string reason = "start")
        {
            if (_characterSelect == null)
            {
                throw new InvalidOperationException("这个流程没有选角界面(构造时没给 characterSelect)");
            }
            CharacterSelectScreen screen = _characterSelect() ?? throw new InvalidOperationException("选角界面工厂返回了 null");
            Switch(GameStage.CharacterSelect, reason, null, null, null, screen);
        }

        /// <summary>
        /// 进城镇:流程刚开始时是登录的第一站,之后(副本、选择界面之后)是回城的落脚点。造一个新的城镇会话;
        /// 世界给了加载画面就先停在加载阶段。
        /// </summary>
        public void EnterTown(string reason = "town")
        {
            GoTown(StageSerial == 0 ? _townWorld.Start : _townWorld.Return, null, reason);
        }

        private void GoTown(TownPlace to, TownPlace? from, string reason)
        {
            void Arrive()
            {
                IHeroSession session = _townWorld.Create(to);
                Place = to;
                Switch(GameStage.Town, reason, session, null);
            }

            SplashScene splash = _townWorld.Splash(from, to);
            if (splash == null)
            {
                Arrive();
            }
            else
            {
                BeginSplash(splash, reason, Arrive);
            }
        }

        private void BeginSplash(SplashScene scene, string reason, Action after)
        {
            _afterSplash = after;
            Switch(GameStage.Loading, reason, null, null, new SplashScreen(scene));
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
            Switch(GameStage.DungeonSelect, reason, null, new DungeonSelectScreen(_entries, index, _world));
        }

        /// <summary>开一局新的副本(局序号 = 已开过的局数)。</summary>
        public void StartDungeon(string dungeonPath, string reason = "start")
        {
            if (string.IsNullOrEmpty(dungeonPath))
            {
                throw new ArgumentException("需要副本路径", nameof(dungeonPath));
            }
            DungeonPath = dungeonPath;

            void Begin()
            {
                IRunSession run = _dungeon(dungeonPath, Runs);
                Runs++;
                Switch(GameStage.Dungeon, reason, run, null);
            }

            SplashScene splash = _dungeonSplash?.Invoke(dungeonPath);
            if (splash == null)
            {
                Begin();
            }
            else
            {
                BeginSplash(splash, reason, Begin);
            }
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
            SystemMenuAction picked = Menu.TakeAction();
            if (picked != SystemMenuAction.None)
            {
                return ApplyMenuAction(picked);
            }
            if (Menu.Open || Bag.Open || Shop.Open || Quests.Open)
            {
                return false;   // 菜单、背包、商店或任务窗口开着:游戏暂停,各阶段的结果(出口、结算)等关上之后再处理
            }
            switch (Stage)
            {
                case GameStage.Town:
                    return PollTown();
                case GameStage.Loading:
                    return PollLoading();
                case GameStage.Dungeon:
                    return PollDungeon();
                case GameStage.DungeonSelect:
                    return PollSelect();
                case GameStage.CharacterSelect:
                    return PollCharacterSelect();
                default:
                    return false;
            }
        }

        /// <summary>
        /// 执行系统菜单选中的项:继续什么也不做;副本里"放弃回城"算一次放弃、回城镇(没有结算,也不算失败);城镇里"回选角"回到选角界面;
        /// "退出"只置 <see cref="QuitRequested"/>。项与当前阶段对不上(菜单开着时阶段变了)就忽略。
        /// </summary>
        private bool ApplyMenuAction(SystemMenuAction action)
        {
            switch (action)
            {
                case SystemMenuAction.ReturnToTown when Stage == GameStage.Dungeon:
                    GiveUps++;
                    EnterTown("menu return");
                    return true;
                case SystemMenuAction.CharacterSelect when Stage == GameStage.Town && _characterSelect != null:
                    EnterCharacterSelect("menu");
                    return true;
                case SystemMenuAction.Quit:
                    QuitRequested = true;
                    return false;
                default:
                    return false;
            }
        }

        private bool PollCharacterSelect()
        {
            // 开始游戏:用选中的角色登录,落在城镇世界的第一站(没有"从哪来",所以只有登录用的加载画面);结束游戏是界面的结果,流程不动
            if (CharacterSelect == null || CharacterSelect.Outcome != CharacterSelectOutcome.Start)
            {
                return false;
            }
            GoTown(_townWorld.Start, null, "login");
            return true;
        }

        private bool PollLoading()
        {
            if (Splash == null || !Splash.Finished || _afterSplash == null)
            {
                return false;
            }
            Action after = _afterSplash;
            _afterSplash = null;
            after();
            return true;
        }

        private bool PollTown()
        {
            if (!(Session is ITownExitSource source) || !source.PendingExit.HasValue || !Place.HasValue)
            {
                return false;
            }
            TownExit exit = source.PendingExit.Value;
            if (exit.IsDungeonGate)
            {
                EnterSelect("gate");
                return true;
            }
            TownPlace? to = _townWorld.Through(Place.Value, exit, source.HeroFacingLeft);
            if (!to.HasValue)
            {
                return false;
            }
            GoTown(to.Value, Place, "exit " + exit.Town + "/" + exit.Area);
            return true;
        }

        private bool PollDungeon()
        {
            IRunSession run = Run;
            if (run == null)
            {
                return false;
            }
            if (run.Failed)
            {
                // 失败:服务端失败计时到点让所有人出副本回城(party_dungeon_fail,E-115);没有结算,不算结算选择
                Failures++;
                EnterTown("dungeon fail");
                return true;
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
                    GoTown(_townWorld.Return, Place, "select Back");     // 从世界地图返回:来源是城镇里的入口区域,同一块区域不加载
                    return true;
                default:
                    return false;
            }
        }

        private void Switch(GameStage to, string reason, IHeroSession session, DungeonSelectScreen select, SplashScreen splash = null,
            CharacterSelectScreen characterSelect = null)
        {
            GameStage from = Stage;
            bool first = StageSerial == 0;
            Menu.Close();                        // 换阶段时系统菜单作废(没取走的选择也一起丢)
            Bag.Close();                         // 背包窗口也关上
            Shop.Close();                        // 商店窗口也关上
            Quests.Close();                      // 任务窗口也关上
            Stage = to;
            Session = session;
            Select = select;
            Splash = splash;
            CharacterSelect = characterSelect;
            StageSerial++;
            _history.Add(new FlowEvent(StageSerial, first ? to : from, to, reason));
            AddStageSounds(to, session);
        }

        /// <summary>
        /// 新阶段自己的声音提示(证据 E-097):选角界面放选角的音乐;城镇区域放那块地图 [sound] 里的音乐与环境声(会话不带地图声音的老式单张地图没有)。
        /// 副本的声音由副本会话自己发(进房间、结算),加载画面与副本选择界面没有声音。
        /// </summary>
        private void AddStageSounds(GameStage to, IHeroSession session)
        {
            _sounds.Drain(_soundScratch);        // 上一阶段还没被取走的提示作废(它们属于已经离开的场景)
            _soundScratch.Clear();
            if (to == GameStage.CharacterSelect)
            {
                _sounds.Add(SceneMusic.CharacterSelectScene, SoundCueKind.RoomEnter);
                _sounds.Add(SceneMusic.CharacterSelect, SoundCueKind.RoomSound);
            }
            else if (to == GameStage.Town && session is IMapSoundSource source)
            {
                // 地图没写 [sound] 也发场景标记:调度器据此停掉上一块区域的环境声(音乐沿用),宿主换区域时不必把旧声音全停
                _sounds.Add(session.MapPath, SoundCueKind.RoomEnter);
                foreach (string id in source.MapSounds)
                {
                    _sounds.Add(id, SoundCueKind.RoomSound);
                }
            }
        }

        /// <summary>
        /// 取走流程这一刻等着的声音提示:先是阶段自己的(选角、城镇区域的音乐),再是当前会话(副本房间、结算)发出的。
        /// 每次阶段切换时,上一阶段还没被取走的提示作废;换了阶段之后,宿主要先停掉旧声音(城镇换区域除外,见 <see cref="LastSwitchStaysInTown"/>)、再取新提示。
        /// </summary>
        public void DrainSounds(List<SoundCue> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }
            _sounds.Drain(into);
            (Session as ISoundSource)?.DrainSounds(into);
        }
    }
}
