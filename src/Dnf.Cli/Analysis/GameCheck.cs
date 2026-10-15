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
        if (opts.Sweep)
        {
            Sweep(world, report, opts.Only);
        }
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

    private sealed record SweepRow(string Module, int Job, string Suffix, int State, int Skill, string Result, int Ms, int Natives, List<string> Errors, List<string> NewUnknown, List<string> TraceCalled);

    /// <summary>
    /// 逐技能扫描:对每个登记了技能的状态,在全新的角色与假人上请求该技能,最多 12 秒,
    /// 记录是否进入状态、是否回到站立、脚本错误与新出现的没实现接口。结果用来排接口实现的优先级,也是'支持的脚本数'的度量。
    /// </summary>
    private static void Sweep(GameWorld world, Dictionary<string, object?> report, List<string> only)
    {
        var rows = new List<SweepRow>();
        List<StateInfo> states = world.AllStates.Where(s => s.IsSkill && (only.Count == 0 || only.Any(o => string.Equals(o, s.Suffix, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(s => s.Module, StringComparer.Ordinal).ThenBy(s => s.Job).ThenBy(s => s.State).ToList();
        bool trace = only.Count > 0;
        foreach (StateInfo s in states)
        {
            world.TraceCallbacks = trace;
            world.CallbackTrace.Clear();
            var calls = new List<string>();
            if (trace)
            {
                world.OnNativeCall = (name, self) => calls.Add(name);
            }
            SweepRow row = SweepOne(world, s);
            if (row.Result.StartsWith("没进入状态(", StringComparison.Ordinal) && !row.Result.Contains("没有登记"))
            {
                // 正常请求进不去(多半是只有别的流程才能触发的状态):再强制进一次,看状态本身会不会出错
                SweepRow forced = SweepOne(world, s, true);
                row = row with { Result = row.Result + ";" + forced.Result, Errors = forced.Errors, NewUnknown = forced.NewUnknown, TraceCalled = forced.TraceCalled, Natives = forced.Natives, Ms = forced.Ms };
            }
            rows.Add(row);
            world.OnNativeCall = null;
            if (trace)
            {
                Console.WriteLine("   [" + s.Suffix + "] 原生接口调用序列(连续重复合并;前 80 项,共 " + calls.Count + " 次):");
                var compact = new List<string>();
                for (int i = 0; i < calls.Count; )
                {
                    int j = i;
                    while (j < calls.Count && calls[j] == calls[i])
                    {
                        j++;
                    }
                    compact.Add(j - i > 1 ? calls[i] + "×" + (j - i) : calls[i]);
                    i = j;
                }
                Console.WriteLine("     " + string.Join(" ", compact.Take(80)));
                List<string> sequence = world.CallbackTrace.Where(c => !c.StartsWith("onProc", StringComparison.Ordinal) && !c.StartsWith("procAppend", StringComparison.Ordinal)).ToList();
                Console.WriteLine("   [" + s.Suffix + "] 回调序列(去掉每帧的 onProc 与 procAppend;前 60 条,共 " + sequence.Count + "):");
                foreach (string c in sequence.Take(60))
                {
                    Console.WriteLine("     " + c);
                }
                if (sequence.Count > 60)
                {
                    Console.WriteLine("     ……最后 20 条:");
                    foreach (string c in sequence.Skip(Math.Max(60, sequence.Count - 20)))
                    {
                        Console.WriteLine("     " + c);
                    }
                }
            }
        }
        world.TraceCallbacks = false;

        Console.WriteLine("D. 逐技能扫描(" + states.Count + " 个登记了技能的状态;假人在前 70 处,技能等级 10,最多 12 秒):");
        foreach (IGrouping<string, SweepRow> g in rows.GroupBy(r => System.Text.RegularExpressions.Regex.Replace(r.Result, @"\([^)]*\)", "")).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine("   " + g.Key + ":" + g.Count());
        }
        foreach (SweepRow r in rows)
        {
            Console.WriteLine("   [" + r.Result + "] " + (r.Module == GameWorld.GenericModule ? "" : r.Module + " ") + "job " + r.Job + " " + r.Suffix + " (状态 " + r.State + ",技能 " + r.Skill + ") "
                + r.Ms + " ms,接口调用 " + r.Natives + (r.Errors.Count > 0 ? ",错误 " + r.Errors.Count + ":" + r.Errors[0] : "")
                + (r.NewUnknown.Count > 0 ? ",缺:" + string.Join("/", r.NewUnknown) : "")
                + (r.TraceCalled.Count > 0 ? ",占位:" + string.Join("/", r.TraceCalled) : ""));
        }
        var needed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SweepRow r in rows)
        {
            foreach (string n in r.NewUnknown)
            {
                needed[n] = needed.TryGetValue(n, out int c) ? c + 1 : 1;
            }
        }
        Console.WriteLine("   扫描里缺的接口(按需要它的技能数):" + string.Join(", ", needed.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "×" + kv.Value)));
        var traced = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SweepRow r in rows)
        {
            foreach (string n in r.TraceCalled)
            {
                traced[n] = traced.TryGetValue(n, out int c) ? c + 1 : 1;
            }
        }
        Console.WriteLine("   扫描里调用到的占位接口(Trace,按用到它的技能数):" + string.Join(", ", traced.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "×" + kv.Value)));
        report["sweep"] = rows;
    }

    private static SweepRow SweepOne(GameWorld world, StateInfo s, bool force = false)
    {
        var errors = new List<string>();
        var unknownBefore = new HashSet<string>(world.UnknownMembers.Keys.Concat(world.Host.UnknownNames.Keys), StringComparer.Ordinal);
        long nativesBefore = world.Natives.Values.Sum(n => n.Calls);
        Dictionary<string, long> traceBefore = world.Natives.Where(kv => kv.Value.Status == NativeStatus.Trace).ToDictionary(kv => kv.Key, kv => kv.Value.Calls, StringComparer.Ordinal);
        world.Errors.Clear();
        world.ResetScriptRandom(); // 每个技能从同一个随机序列起点开始,结果不依赖扫描顺序

        GameObject hero = world.SpawnCharacter(s.Job, "hero", 200, 150, 0, Direction.Right, s.Module);
        if (hero.Character == null)
        {
            Cleanup(world);
            return new SweepRow(s.Module, s.Job, s.Suffix, s.State, s.Skill, "未测:没有角色数据", 0, 0, errors, new List<string>(), new List<string>());
        }
        SkillDef? def = world.Data.GetSkill(s.Job, s.Skill);
        hero.SkillLevels[s.Skill] = Math.Max(1, def != null && def.MaximumLevel > 0 ? Math.Min(def.MaximumLevel, 10) : 10);
        world.SpawnDummy("dummy", 270, 150, 0, new Aabb(-20, 20, -10, 10, 0, 80));
        if (force)
        {
            // 数据给一个 0(子状态 0):状态脚本都是用 sq_GetVectorData(datas, 0) 取子状态,客户端里越界读到 -1(E-046),
            // 空数据会让脚本停在"子状态 -1";正常进入技能时脚本自己先压进 0 再发请求
            world.ForceState(hero, s.State, 0L);
        }
        else
        {
            world.RequestSkill(hero, s.Skill);
        }
        bool entered = false;
        int returned = -1;
        int start = world.TimeMs;
        for (int t = 0; t < SweepLimitMs; t += 10)
        {
            world.Step(10);
            if (hero.State == s.State)
            {
                entered = true;
            }
            if (entered && hero.State != s.State && hero.State == 0)
            {
                returned = world.TimeMs - start;
                break;
            }
            if (!hero.Valid)
            {
                break;
            }
        }
        int ms = world.TimeMs - start;
        errors.AddRange(world.Errors.Distinct().Take(5));
        string result = errors.Count > 0 ? "错误" : !entered ? "没进入状态(" + (force ? "强制也没进" : world.LastSkillRequest) + ")" : returned >= 0 ? "跑完" : "超时(没回到站立;" + WhereIs(hero) + ")";
        if (force)
        {
            result = "强制进入-" + result;
        }
        long natives = world.Natives.Values.Sum(n => n.Calls) - nativesBefore;
        List<string> newUnknown = world.UnknownMembers.Keys.Concat(world.Host.UnknownNames.Keys).Where(n => !unknownBefore.Contains(n)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        List<string> traceCalled = world.Natives
            .Where(kv => kv.Value.Status == NativeStatus.Trace && kv.Value.Calls > (traceBefore.TryGetValue(kv.Key, out long b) ? b : 0))
            .Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Cleanup(world);
        return new SweepRow(s.Module, s.Job, s.Suffix, s.State, s.Skill, result, returned >= 0 ? returned : ms, (int)natives, errors, newUnknown, traceCalled);
    }

    /// <summary>逐技能扫描里每个技能最多跑多久(游戏时间);6 秒时冰轮、元素雨等长技能会被误判成卡住。</summary>
    private const int SweepLimitMs = 12000;

    private static string WhereIs(GameObject hero)
    {
        string anim = hero.Animator.Clip == null ? "无动画" : hero.Animator.Ended ? "动画已结束" : "动画第 " + hero.Animator.FrameIndex + " 帧/" + hero.Animator.Clip.Frames.Length;
        return "停在状态 " + hero.State + " 子状态 " + hero.SkillSubState + "," + anim + ",计时事件 " + hero.TimeEvents.Count + " 个";
    }

    private static void Cleanup(GameWorld world)
    {
        foreach (GameObject o in world.Objects.ToList())
        {
            world.Destroy(o);
        }
        world.Step(1);
        world.Errors.Clear();
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
        // 加载入口脚本时由脚本宿主(ScriptHost)自己登记的接口,不在原生接口表里
        var implemented = new HashSet<string>(StringComparer.Ordinal) { "pushScriptFiles", "pushState", "pushPassiveObj", "sq_RunScript", "isDebugMode" };
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
            registered = implemented.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            missing = missing.OrderByDescending(m => m.Calls).Select(m => new { name = m.Name, calls = m.Calls }).ToList(),
            // 每个已登记原生接口的可信程度与依据(审计用:拿它和客户端注册表、实现逐个对照)
            natives = world.Natives.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new { name = kv.Key, status = kv.Value.Status.ToString(), forms = kv.Value.Forms.ToString(), note = kv.Value.Note })
                .ToList(),
        };
    }
}
