using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf game-check`:用用户自备的 Script.pvf,在 Dnf.Game 的最小世界里跑原版 Squirrel 状态脚本(不修改脚本),
/// 记录回调与原生接口的调用序列、没实现的接口,以及接口覆盖率(证据 E-036、E-037)。
/// 当前场景:男魔法师(职业 8)的 IceCrash 技能,起手→旋转→(抓到敌人)→收尾→站立。
/// 只输出名字与数字,不输出脚本内容。
/// </summary>
internal static class GameCheck
{
    private const int Job = 8; // 男魔法师 AT-mage(证据 E-036)

    public static int Run(DnfLocation loc, Options opts)
    {
        var sw = Stopwatch.StartNew();
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var source = new PvfScriptSource(pvf);
        using var world = new GameWorld(new GameData(source), new ScriptHost(source));
        ScriptLoadSummary load = world.LoadScripts();
        Console.WriteLine("A. 加载:入口 " + (load.EntryOk ? "成功" : "失败") + ";运行 " + load.LoadedScripts + " 个脚本,登记状态 " + load.States
            + ",被动对象 " + load.PassiveObjects + ",加载错误 " + load.Errors + ",缺少 " + load.Missing + "(" + sw.ElapsedMilliseconds + " ms)");

        var report = new Dictionary<string, object?>();
        bool ok = true;
        ok &= IceCrash(world, report);
        Coverage(world, loc, report);

        if (opts.JsonFile != null)
        {
            File.WriteAllText(opts.JsonFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine("已写入 " + opts.JsonFile);
        }
        return ok ? 0 : 1;
    }

    private static long Const(GameWorld world, string name)
    {
        return world.Vm.TryGetGlobalInteger(name, out long v) ? v : -1;
    }

    private static bool IceCrash(GameWorld world, Dictionary<string, object?> report)
    {
        long skill = Const(world, "SKILL_ICE_CRASH");
        long state = Const(world, "STATE_ICE_CRASH");
        long standState = Const(world, "STATE_STAND");
        Console.WriteLine("B. IceCrash:SKILL_ICE_CRASH=" + skill + " STATE_ICE_CRASH=" + state + " STATE_STAND=" + standState);
        if (skill < 0 || state < 0)
        {
            Console.WriteLine("   常量没有定义,跳过");
            return false;
        }
        SkillDef? def = world.Data.GetSkill(Job, (int)skill);
        Console.WriteLine("   技能文件:" + (def == null ? "找不到" : def.Path + ";静态数据 " + def.StaticData.Length + " 项,等级表 " + def.LevelInfo.Columns + " 列 × " + def.LevelInfo.Levels + " 级;可从状态 [" + string.Join(",", def.ExecutableStates) + "] 发出"));

        GameObject hero = world.SpawnCharacter(Job, "hero", 200, 150, 0, Direction.Right);
        hero.SkillLevels[(int)skill] = Math.Max(1, def?.MaximumLevel > 0 ? Math.Min(def.MaximumLevel, 10) : 10);
        GameObject dummy = world.SpawnDummy("dummy", 270, 150, 0, new Aabb(-20, 20, -10, 10, 0, 80));
        Console.WriteLine("   角色 " + hero + ",假人 " + dummy);

        world.TraceCallbacks = true;
        var natives = new List<string>();
        world.OnNativeCall = (name, self) => natives.Add(name);

        var timeline = new List<string>();
        int lastState = hero.State;
        int lastSub = hero.SkillSubState;
        world.RequestSkill(hero, (int)skill);
        int maxMs = 6000;
        int left = 0;
        for (int t = 0; t < maxMs; t += 10)
        {
            world.Step(10);
            if (hero.State != lastState || hero.SkillSubState != lastSub)
            {
                timeline.Add(world.TimeMs + " ms: 状态 " + lastState + "→" + hero.State + ",子状态 " + lastSub + "→" + hero.SkillSubState + ",动画 " + Path.GetFileName(hero.Animator.Path ?? "(无)"));
                lastState = hero.State;
                lastSub = hero.SkillSubState;
            }
            if (t > 50 && hero.State == (int)standState && hero.StateInfo == null)
            {
                left = world.TimeMs;
                break;
            }
        }
        world.TraceCallbacks = false;
        world.OnNativeCall = null;

        foreach (string line in timeline)
        {
            Console.WriteLine("   " + line);
        }
        Console.WriteLine("   结果:" + (left > 0 ? "在 " + left + " ms 回到站立" : "没有回到站立(" + maxMs + " ms 内)")
            + ";位置 " + hero + ";假人 " + dummy + ";命中 " + world.Hits.Count + " 次,抓取的依附体 " + dummy.Appendages.Count);
        Console.WriteLine("   脚本错误 " + world.Errors.Count + " 条" + (world.Errors.Count == 0 ? "" : ":"));
        foreach (string e in world.Errors.Distinct().Take(10))
        {
            Console.WriteLine("     " + e);
        }

        // 回调与接口的调用序列(去掉每帧都会有的 onProc 类)
        List<string> callbacks = world.CallbackTrace.Where(c => !c.StartsWith("onProc", StringComparison.Ordinal)).ToList();
        Console.WriteLine("   回调序列(前 40 条,共 " + callbacks.Count + "):");
        foreach (string c in callbacks.Take(40))
        {
            Console.WriteLine("     " + c);
        }
        var counts = natives.GroupBy(n => n).OrderByDescending(g => g.Count()).ToList();
        Console.WriteLine("   原生接口调用 " + natives.Count + " 次,不同接口 " + counts.Count + " 个:" + string.Join(", ", counts.Select(g => g.Key + "×" + g.Count())));
        Console.WriteLine("   没实现的对象成员(脚本调用了但没有登记):" + (world.UnknownMembers.Count == 0 ? "无" : string.Join(", ", world.UnknownMembers.Select(kv => kv.Key + "×" + kv.Value))));
        IReadOnlyDictionary<string, int> unknownGlobals = world.Host.UnknownNames;
        Console.WriteLine("   没实现的全局名字(被替身函数接住):" + (unknownGlobals.Count == 0 ? "无" : string.Join(", ", unknownGlobals.Select(kv => kv.Key + "×" + kv.Value))));

        report["icecrash"] = new
        {
            returnedToStandMs = left,
            states = timeline,
            hits = world.Hits.Count,
            errors = world.Errors.Distinct().ToList(),
            callbackSequence = callbacks,
            nativeCalls = counts.ToDictionary(g => g.Key, g => g.Count()),
            unknownMembers = world.UnknownMembers,
            unknownGlobals,
        };
        return left > 0 && world.Errors.Count == 0;
    }

    /// <summary>接口覆盖率:对照 docs/sq-api-catalog.tsv(704 个名字及调用次数),看已登记的接口占多少。</summary>
    private static void Coverage(GameWorld world, DnfLocation loc, Dictionary<string, object?> report)
    {
        string path = Path.Combine(loc.RepoRoot, "docs", "sq-api-catalog.tsv");
        if (!File.Exists(path))
        {
            Console.WriteLine("C. 没有 docs/sq-api-catalog.tsv,跳过覆盖率");
            return;
        }
        var implemented = new HashSet<string>(StringComparer.Ordinal);
        var statusOf = new Dictionary<string, NativeStatus>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, NativeInfo> kv in world.Natives)
        {
            string name = kv.Key.Contains('.') ? kv.Key[(kv.Key.LastIndexOf('.') + 1)..] : kv.Key;
            implemented.Add(name);
            statusOf[name] = kv.Value.Status;
        }
        long totalCalls = 0;
        long coveredCalls = 0;
        int totalNames = 0;
        int coveredNames = 0;
        int sqNames = 0;
        int sqCovered = 0;
        var missing = new List<(string Name, long Calls)>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] f = line.Split('\t');
            if (f.Length < 3 || !long.TryParse(f[2], out long calls))
            {
                continue;
            }
            totalNames++;
            totalCalls += calls;
            bool isSq = f[0].StartsWith("sq_", StringComparison.Ordinal);
            if (isSq)
            {
                sqNames++;
            }
            if (implemented.Contains(f[0]))
            {
                coveredNames++;
                coveredCalls += calls;
                if (isSq)
                {
                    sqCovered++;
                }
            }
            else
            {
                missing.Add((f[0], calls));
            }
        }
        Console.WriteLine("C. 接口覆盖率(对照 docs/sq-api-catalog.tsv):已登记 " + coveredNames + "/" + totalNames + " 个名字("
            + (100.0 * coveredNames / Math.Max(1, totalNames)).ToString("F1") + "%),覆盖调用点 " + coveredCalls + "/" + totalCalls
            + "(" + (100.0 * coveredCalls / Math.Max(1, totalCalls)).ToString("F1") + "%);其中 sq_ 名字 " + sqCovered + "/" + sqNames);
        Console.WriteLine("   登记状态:Verified " + statusOf.Values.Count(s => s == NativeStatus.Verified)
            + ",Inferred " + statusOf.Values.Count(s => s == NativeStatus.Inferred) + ",Trace(占位) " + statusOf.Values.Count(s => s == NativeStatus.Trace));
        Console.WriteLine("   调用最多的未登记接口:" + string.Join(", ", missing.OrderByDescending(m => m.Calls).Take(25).Select(m => m.Name + "×" + m.Calls)));
        report["coverage"] = new
        {
            names = totalNames,
            implemented = coveredNames,
            calls = totalCalls,
            coveredCalls,
            topMissing = missing.OrderByDescending(m => m.Calls).Take(60).Select(m => new { name = m.Name, calls = m.Calls }).ToList(),
        };
    }
}
