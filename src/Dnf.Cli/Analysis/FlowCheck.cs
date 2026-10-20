using System.Globalization;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// flow-check:整条流程的无人值守检查(证据 E-093)。
/// 用真实数据造城镇与副本会话,交给 <see cref="GameFlow"/>,由 <see cref="FlowBot"/> 按固定的 1/60 秒步长走完
/// 城镇 → 副本选择 → 副本(自动机打到首领清空)→ 结算 → 按 --choices 清单依次选再次挑战、选择其它地下城、返回城镇;
/// 打印每次阶段切换与每一局的战绩,清单用完或到时间为止。--shots-at 在指定时刻输出当时阶段的 800×600 画面。
/// 战斗用占位伤害(<see cref="PlaceholderDamage"/>)。城镇是量过出生点与镜头范围的赛丽亚房间(目前唯一可走的城镇地图),
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
        var flow = new GameFlow(
            () => DemoHero.CreateSession(assets, TownMap, DemoHero.SeriaRoom),
            (path, run) => DungeonSession.Create(assets, path, opts.Seed + run, opts.RandomLayout, opts.HeroWeapon, heroVitals: !opts.NoDamage),
            entries,
            realSelect ? world!.Scene : null);
        var bot = new FlowBot(opts.Explore, choices);

        double seconds = opts.SecondsGiven ? opts.Seconds : DefaultSeconds;
        double frameMs = opts.FrameMs > 0 ? opts.FrameMs : CombatScript.StepMs;
        Console.WriteLine("流程检查:副本 " + opts.Dungeon + ",战斗种子 " + opts.Seed + (opts.Explore ? ",自动机先逛遍所有房间" : string.Empty)
            + ",从 " + opts.FlowFrom + " 开始,结算选择清单 " + string.Join(",", choices.Select(Name)) + ",最长 " + F(seconds) + " 秒");

        switch (opts.FlowFrom)
        {
            case "select":
                flow.EnterSelect("start");
                break;
            case "dungeon":
                flow.StartDungeon(opts.Dungeon, "start");
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
            + flow.Settlements + " 次选择(清单 " + choices.Count + " 个),阶段切换 " + flow.History.Count + " 次");
        bool allCleared = stats.Count > 0 && stats.All(s => s.BossCleared);
        Console.WriteLine("  每一局都打到首领清空:" + (allCleared ? "是" : "否"));
        return bot.Done && allCleared && bot.ChoicesMade == choices.Count ? 0 : 2;
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

        public static RunStats Of(DungeonSession s, int run)
        {
            return new RunStats
            {
                Run = run,
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
            return "第 " + Run + " 局 " + (BossCleared ? "首领清空" : "没打完") + ",用时 " + Sec(ClockMs) + " 秒,清空 " + RoomsCleared + " 个房间,击杀 " + Kills
                + " 个,命中 " + Hits + " 次;结算菜单 " + (SettlementShown ? "出现过" : "没出现") + ",选择 " + Name(Choice);
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
            case GameStage.Town:
                return "  地图 " + flow.Session!.MapPath;
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
