using System.Globalization;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// flow-check:整条流程的无人值守检查(证据 E-093)。
/// 用真实数据造城镇与副本会话,交给 <see cref="GameFlow"/>,由 <see cref="FlowBot"/> 按固定的 1/60 秒步长走完
/// (选角 →)城镇 → 副本选择 → 副本(自动机打到首领清空)→ 结算 → 按 --choices 清单依次选再次挑战、选择其它地下城、返回城镇;
/// --from char 从选角界面开始(证据 E-096:自动机停一会儿确认第一个角色,登录进城);
/// 打印每次阶段切换与每一局的战绩,清单用完或到时间为止。--shots-at 在指定时刻输出当时阶段的 800×600 画面。
/// 战斗里主角打怪物按原版公式(证据 E-110,主角面板只对一级的新角色成立),--no-damage 时是占位伤害(<see cref="PlaceholderDamage"/>)。城镇默认走城镇图册(<see cref="AtlasTownWorld"/>,证据 E-095:登录在网关区域、沿出口换区域、地下城入口区域的出口通世界地图、换区域与进图有加载画面,
/// 自动机沿出口走过去);--legacy-town 退回量过出生点与镜头范围的赛丽亚房间一张图(没有出口与加载画面),
/// 副本选择默认画原版的世界地图界面(洛兰,<see cref="WorldMapArt"/>,证据 E-094;--worldmap none 退回占位名单),阶段之间瞬间切换:
/// 这些不都是原版的样子,验证的是流程的次序与每一段能不能接上。
/// </summary>
internal static class FlowCheck
{
    /// <summary>不给 --choices 时的清单:再来一次,选择其它地下城(再进同一座),最后回城。</summary>
    public const string DefaultChoices = "retry,select,town";

    /// <summary>不给 --seconds 时最长模拟多少秒(三局副本绰绰有余)。</summary>
    private const double DefaultSeconds = 1800;

    /// <summary>城镇用的地图:目前唯一量过出生点与镜头范围的城镇(<see cref="DemoHero.SeriaRoom"/>)。</summary>
    public const string TownMap = "map/elvengard/(r)gate.map";

    /// <summary>城镇图册的起始城镇(--town 可改):town.lst 里的 1 号,基线 S03 拍到的那座。</summary>
    public const int DefaultTown = 1;

    /// <summary>副本选择界面用的世界地图(基线 S05 拍的就是洛兰这张)。</summary>
    public const string DefaultWorldMap = "worldmap/lorien.wdm";

    public static int Run(DnfLocation loc, Options opts)
    {
        List<SettlementChoice> choices = ParseChoices(opts.Choices ?? DefaultChoices);
        var shotTimes = new List<double>();
        if (opts.ShotsAt != null)
        {
            foreach (string item in opts.ShotsAt.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                shotTimes.Add(double.Parse(item, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            shotTimes.Sort();
            if (opts.PngFile == null)
            {
                throw new UserError("--shots-at 需要 --png <文件名前缀>(每个时刻写成 前缀-秒.png)");
            }
        }

        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        WorldMapSet? world = null;
        string worldPath = opts.WorldMap ?? DefaultWorldMap;
        if (!string.Equals(worldPath, "none", StringComparison.OrdinalIgnoreCase))
        {
            world = WorldMapCatalog.Load(assets, worldPath) ?? throw new UserError("读不到世界地图 " + worldPath + "(--worldmap none 画占位界面)");
        }
        bool realSelect = world != null && world.Entries.Count > 0;
        var entries = realSelect ? new List<DungeonEntry>(world!.Entries) : new List<DungeonEntry> { new DungeonEntry(opts.Dungeon, null) };
        ITownWorld townWorld = opts.LegacyTown
            ? new LegacyTownWorld(() => DemoHero.CreateSession(assets, TownMap, DemoHero.SeriaRoom))
            : new AtlasTownWorld(assets, TownAtlas.Open(assets), opts.TownId >= 0 ? opts.TownId : DefaultTown);
        CharacterSelectScene characterScene = CharacterSelectScene.From(assets);
        // 主角的等级进度跟着整个流程(几局副本之间等级与经验留着;E-111)
        HeroProgress? progress = ExpCheck.ProgressFor(assets, opts);
        // 复活币库存也跟着整个流程(用掉的币不会回来;每局副本的"已用次数"在副本里重新数,证据 E-115)
        var coins = new ReviveCoins(opts.Coins);
        var flow = new GameFlow(
            townWorld,
            (path, run) => DungeonSession.Create(assets, path, opts.Seed + run, opts.RandomLayout, opts.HeroWeapon, heroVitals: !opts.NoDamage, progress: progress,
                coins: coins, monsterDamageScale: opts.MonsterDamageScale),
            entries,
            realSelect ? world!.Scene : null,
            opts.LegacyTown ? null : path => SplashCatalog.ForDungeon(assets, path),
            () => new CharacterSelectScreen(CharacterRoster.Local(), characterScene),
            (path, run, difficulty) => DungeonSession.Create(assets, path, opts.Seed + run, opts.RandomLayout, opts.HeroWeapon, heroVitals: !opts.NoDamage, progress: progress,
                coins: coins, monsterDamageScale: opts.MonsterDamageScale, difficulty: difficulty));
        flow.DifficultyLimit = DifficultyGate.For(() => progress, assets);       // 选择界面能选到哪一档:按主角的通关记录(M33,E-125)
        var bot = new FlowBot(opts.Explore, choices) { RaiseDifficulty = opts.RaiseDifficulty, SmashObjects = opts.Smash };
        // 背景音乐(E-097):照播放器的做法把流程发出的声音提示交给调度器,只记音乐通道开始放的那几次(不出声、不读音频文件内容)
        using SoundLibrary soundLibrary = SoundLibrary.Open(loc.DnfDir);
        MusicTrace? music = soundLibrary.HasCatalog ? new MusicTrace(soundLibrary) : null;

        double seconds = opts.SecondsGiven ? opts.Seconds : DefaultSeconds;
        double frameMs = opts.FrameMs > 0 ? opts.FrameMs : CombatScript.StepMs;
        Console.WriteLine("流程检查:副本 " + opts.Dungeon + ",战斗种子 " + opts.Seed + (opts.Explore ? ",自动机先逛遍所有房间" : string.Empty)
            + ",从 " + opts.FlowFrom + " 开始,结算选择清单 " + string.Join(",", choices.Select(Name)) + ",最长 " + F(seconds) + " 秒");

        switch (opts.FlowFrom)
        {
            case "char":
                flow.EnterCharacterSelect("start");
                break;
            case "select":
                flow.EnterSelect("start");
                break;
            case "dungeon":
                flow.StartDungeon(opts.Dungeon, "start", opts.Difficulty);
                break;
            default:
                flow.EnterTown("start");
                break;
        }

        var stats = new List<RunStats>();
        double t = 0;
        double totalMs = seconds * 1000.0;
        int printed = 0;
        int shotIndex = 0;
        PrintNew(flow, t, ref printed);
        music?.Step(flow, t, 0, false);
        while (t < totalMs && !bot.Done)
        {
            DungeonSession? before = flow.Run as DungeonSession;
            int serialBefore = flow.StageSerial;
            bot.Advance(flow, frameMs);
            t += frameMs;
            if (flow.StageSerial != serialBefore && before != null)
            {
                stats.Add(RunStats.Of(before, stats.Count));
                Console.WriteLine("  上一局:" + stats[^1]);
            }
            PrintNew(flow, t, ref printed);
            music?.Step(flow, t, frameMs, flow.StageSerial != serialBefore);
            while (shotIndex < shotTimes.Count && shotTimes[shotIndex] * 1000.0 <= t)
            {
                WriteShot(flow, shotTimes[shotIndex], opts.PngFile!);
                shotIndex++;
            }
        }
        if (flow.Run is DungeonSession last)
        {
            stats.Add(RunStats.Of(last, stats.Count));
            Console.WriteLine("  最后一局(停在结算菜单上):" + stats[^1]);
        }
        while (shotIndex < shotTimes.Count)
        {
            WriteShot(flow, shotTimes[shotIndex], opts.PngFile!);
            shotIndex++;
        }

        Console.WriteLine();
        Console.WriteLine("结束:t=" + Sec(t) + "s  " + (bot.Done ? "流程走完" : "到时间了没走完") + ",停在 " + flow.Stage + ";开了 " + flow.Runs + " 局副本,结算上做了 "
            + flow.Settlements + " 次选择(清单 " + choices.Count + " 个),副本失败回城 " + flow.Failures + " 次,复活币剩 " + coins.Count + " 个,阶段切换 " + flow.History.Count + " 次");
        bool allCleared = stats.Count > 0 && stats.All(s => s.BossCleared);
        Console.WriteLine("  每一局都打到首领清空:" + (allCleared ? "是" : "否"));
        Console.WriteLine("  金币(E-116):各局掉出 " + stats.Sum(s => s.GoldDropped) + " 堆,捡起 " + stats.Sum(s => s.GoldPicked) + " 堆共 " + stats.Sum(s => s.GoldEarned)
            + (progress != null ? ",身上 " + progress.Gold : string.Empty));
        Console.WriteLine("  物品(E-117):各局掉出 " + stats.Sum(s => s.ItemsDropped) + " 件,捡起 " + stats.Sum(s => s.ItemsPicked) + " 件"
            + (progress != null ? ",背包 " + progress.Bag.TotalCount + " 件占 " + progress.Bag.StackCount + " 格" : string.Empty));
        if (opts.Smash)
        {
            Console.WriteLine("  物件(E-126):各局挥中 " + stats.Sum(s => s.ObjectsHit) + " 次,打翻 " + stats.Sum(s => s.ObjectsDestroyed) + " 个,物件掉出 " + stats.Sum(s => s.ObjectItemsDropped) + " 件");
        }
        bool musicOk = true;
        if (music == null)
        {
            Console.WriteLine("  背景音乐:数据目录里没有 audio.xml,没有检查");
        }
        else
        {
            int settlementsShown = stats.Count(s => s.SettlementShown);
            musicOk = music.Check(opts.FlowFrom == "char", settlementsShown);
            Console.WriteLine("  背景音乐:开始 " + music.Starts.Count + " 次,标签都在 audio.xml 里且文件读得到:" + (music.Starts.All(s => s.Playable) ? "是" : "否")
                + ";调度器里找不到的标签 " + music.UnknownCount + " 个" + (opts.FlowFrom == "char" ? ";第一首是选角音乐:" + (music.Starts.Count > 0 && music.Starts[0].Id == SceneMusic.CharacterSelect ? "是" : "否") : string.Empty));
            Console.WriteLine("  结算音:放了 " + music.ResultSounds + " 次(结算界面出现 " + settlementsShown + " 次),文件读得到:" + (music.ResultPlayable ? "是" : "否"));
        }
        return bot.Done && allCleared && bot.ChoicesMade == choices.Count && musicOk ? 0 : 2;
    }

    /// <summary>
    /// 把流程发出的声音提示照播放器的样子交给 <see cref="SoundScheduler"/>(换阶段先停掉旧声音,城镇换区域除外),
    /// 只记背景音乐开始放的几次:哪个阶段、哪个标签、标签在不在 audio.xml 里、文件读不读得到。
    /// </summary>
    private sealed class MusicTrace
    {
        public readonly record struct Start(double T, GameStage Stage, string Id, bool Playable);

        private readonly SoundLibrary _library;
        private readonly SoundScheduler _scheduler;
        private readonly List<SoundCue> _cues = new List<SoundCue>();
        private readonly List<SoundCommand> _commands = new List<SoundCommand>();

        public List<Start> Starts { get; } = new List<Start>();

        public long UnknownCount => _scheduler.UnknownCount;

        public MusicTrace(SoundLibrary library)
        {
            _library = library;
            var random = new Random(1);
            _scheduler = new SoundScheduler(library.Catalog, () => random.NextDouble());
        }

        public void Step(GameFlow flow, double t, double deltaMs, bool switched)
        {
            _commands.Clear();
            if (switched && !flow.LastSwitchStaysInTown)
            {
                _scheduler.StopAll(_commands);
            }
            _cues.Clear();
            flow.DrainSounds(_cues);
            _scheduler.Update(deltaMs, _cues, _commands);
            foreach (SoundCommand c in _commands)
            {
                if (c.Kind != SoundCommandKind.Play)
                {
                    continue;
                }
                if (c.Channel == SoundChannel.Ui && string.Equals(c.Id, SceneMusic.ResultSound, StringComparison.OrdinalIgnoreCase))
                {
                    ResultSounds++;
                    bool resultPlayable = _library.Catalog.CollectFiles(c.Id).Any(_library.Exists);
                    ResultPlayable &= resultPlayable;
                    Console.WriteLine("  t=" + Sec(t) + "s  结算音 " + c.Id + "(界面音)  阶段 " + flow.Stage + (resultPlayable ? string.Empty : "  (文件读不到)"));
                    continue;
                }
                if (c.Channel != SoundChannel.Music)
                {
                    continue;
                }
                bool playable = _library.Catalog.CollectFiles(c.Id).Any(_library.Exists);
                Starts.Add(new Start(t, flow.Stage, c.Id, playable));
                Console.WriteLine("  t=" + Sec(t) + "s  音乐 " + c.Id + "  阶段 " + flow.Stage + (playable ? string.Empty : "  (文件读不到)"));
            }
        }

        /// <summary>结算界面出现时放过几次 M_RESULT(界面通道),以及它的文件是不是都读得到。</summary>
        public int ResultSounds { get; private set; }

        public bool ResultPlayable { get; private set; } = true;

        /// <summary>音乐都放得出来;从选角开始时第一首是选角音乐;结算音放的次数等于结算出现的次数、文件读得到。</summary>
        public bool Check(bool fromCharacterSelect, int settlementsShown)
        {
            if (Starts.Count == 0 || Starts.Any(s => !s.Playable))
            {
                return false;
            }
            if (ResultSounds != settlementsShown || !ResultPlayable)
            {
                return false;
            }
            return !fromCharacterSelect || Starts[0].Id == SceneMusic.CharacterSelect;
        }
    }

    private sealed class RunStats
    {
        public int Run { get; private set; }
        public bool BossCleared { get; private set; }
        public int Kills { get; private set; }
        public int Hits { get; private set; }
        public int RoomsCleared { get; private set; }
        public double ClockMs { get; private set; }
        public bool SettlementShown { get; private set; }
        public SettlementChoice Choice { get; private set; }
        public bool Failed { get; private set; }
        public int Deaths { get; private set; }
        public int Revives { get; private set; }
        public int GoldDropped { get; private set; }
        public int GoldPicked { get; private set; }
        public long GoldEarned { get; private set; }
        public int ItemsDropped { get; private set; }
        public int ItemsPicked { get; private set; }
        public int ObjectsHit { get; private set; }
        public int ObjectsDestroyed { get; private set; }
        public int ObjectItemsDropped { get; private set; }

        public static RunStats Of(DungeonSession s, int run)
        {
            return new RunStats
            {
                Run = run,
                Failed = s.Failed,
                Deaths = s.Revive?.Deaths ?? 0,
                Revives = s.Revive?.Revives ?? 0,
                GoldDropped = s.GoldDropped,
                GoldPicked = s.GoldPicked,
                GoldEarned = s.GoldEarned,
                ItemsDropped = s.ItemsDropped,
                ItemsPicked = s.ItemsPicked,
                ObjectsHit = s.ObjectsHit,
                ObjectsDestroyed = s.ObjectsDestroyed,
                ObjectItemsDropped = s.ObjectItemsDropped,
                BossCleared = s.BossCleared,
                Kills = s.Kills,
                Hits = s.Hits,
                RoomsCleared = s.RoomsCleared,
                ClockMs = s.ClockMs,
                SettlementShown = s.SettlementActive,
                Choice = s.Choice,
            };
        }

        public override string ToString()
        {
            return "第 " + Run + " 局 " + (BossCleared ? "首领清空" : Failed ? "副本失败" : "没打完") + ",用时 " + Sec(ClockMs) + " 秒,清空 " + RoomsCleared + " 个房间,击杀 " + Kills
                + " 个,命中 " + Hits + " 次;金币掉 " + GoldDropped + " 堆、捡 " + GoldPicked + " 堆共 " + GoldEarned + ";物品掉 " + ItemsDropped + " 件、捡 " + ItemsPicked + " 件;" + (Deaths > 0 ? "死亡 " + Deaths + " 次、复活 " + Revives + " 次;" : string.Empty)
                + "结算菜单 " + (SettlementShown ? "出现过" : "没出现") + ",选择 " + Name(Choice);
        }
    }

    private static void PrintNew(GameFlow flow, double t, ref int printed)
    {
        while (printed < flow.History.Count)
        {
            FlowEvent e = flow.History[printed++];
            Console.WriteLine("  t=" + Sec(t) + "s  阶段 #" + e.Serial + " " + e.From + " -> " + e.To + "  (" + e.Reason + ")" + Describe(flow));
        }
    }

    /// <summary>刚进入的阶段的内容:城镇看地图、副本看路径与种子、选择界面看列表。</summary>
    private static string Describe(GameFlow flow)
    {
        switch (flow.Stage)
        {
            case GameStage.CharacterSelect:
                return "  角色 " + flow.CharacterSelect!.Roster.Count + " 个,选中第 " + (flow.CharacterSelect.Index + 1) + " 个"
                    + (flow.CharacterSelect.Scene != null ? "(原版素材)" : "(占位界面)");
            case GameStage.Loading:
                return "  加载画面 " + flow.Splash!.Scene.Name + "(" + Sec(flow.Splash.Scene.DurationMs) + " 秒"
                    + (flow.Splash.Scene.Cutscene == null ? ",没有过场图" : string.Empty) + (flow.Splash.Scene.Title != null ? ",带标题" : string.Empty) + ")";
            case GameStage.Town:
                return "  区域 " + (flow.Place.HasValue ? flow.Place.Value.Town + "/" + flow.Place.Value.Area + " " : string.Empty) + "地图 " + flow.Session!.MapPath;
            case GameStage.DungeonSelect:
                return "  副本 " + flow.Select!.Entries.Count + " 个,选中 " + (flow.Select.Selected?.Name ?? "无") + (flow.Select.World != null ? "(世界地图 " + flow.Select.World.RegionName + ")" : "(占位界面)");
            default:
                return "  副本 " + flow.DungeonPath + ",第 " + (flow.Runs - 1) + " 局";
        }
    }

    private static List<SettlementChoice> ParseChoices(string text)
    {
        var list = new List<SettlementChoice>();
        foreach (string item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (item.ToLowerInvariant())
            {
                case "retry":
                    list.Add(SettlementChoice.Retry);
                    break;
                case "select":
                    list.Add(SettlementChoice.SelectDungeon);
                    break;
                case "town":
                    list.Add(SettlementChoice.ReturnToTown);
                    break;
                default:
                    throw new UserError("--choices 里只能写 retry、select、town: " + item);
            }
        }
        return list;
    }

    private static string Name(SettlementChoice choice) => choice switch
    {
        SettlementChoice.Retry => "retry",
        SettlementChoice.SelectDungeon => "select",
        SettlementChoice.ReturnToTown => "town",
        SettlementChoice.QuickParty => "party",
        _ => "无",
    };

    private static void WriteShot(GameFlow flow, double seconds, string prefix)
    {
        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 40, 40, 255);
        TownScene.Render(canvas, flow.Scene!.Build((long)(seconds * 1000.0)));
        string path = prefix + "-" + seconds.ToString("0.00", CultureInfo.InvariantCulture) + ".png";
        PngWriter.Write(path, 800, 600, canvas.Pixels);
        Console.WriteLine("  已写 " + path + "(阶段 " + flow.Stage + ")");
    }

    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    private static string Sec(double ms) => (ms / 1000.0).ToString("F2", CultureInfo.InvariantCulture);
}
