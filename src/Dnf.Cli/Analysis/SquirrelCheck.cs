using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dnf.Data;
using Dnf.Script;
using Dnf.Squirrel;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf sq-check`:用 Squirrel 3.2 检查 PVF 里全部明文 `.nut` 脚本(证据 E-033、E-035)。
///   A. 逐个编译(只检查语法);
///   B. 用脚本宿主(Dnf.Script.ScriptHost)模拟引擎的加载过程:运行入口脚本 sqr/loadstate.nut,统计登记了哪些状态与被动对象,脚本路径是否都在 PVF 里;
///   C. 统计每个状态里定义了哪些回调(按函数名 &lt;回调&gt;_&lt;后缀&gt; 的前缀归类);
///   D. 没被加载链运行的脚本,以及别的脚本里有没有写着它们路径的字符串(引擎按路径按需加载的线索)。
/// 只输出名字与数字,不输出脚本内容;--json 写到文件。
/// </summary>
internal static class SquirrelCheck
{
    public static int Run(DnfLocation loc, Options opts)
    {
        var sw = Stopwatch.StartNew();
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        List<PvfEntry> nuts = pvf.Entries.Where(e => e.Path.EndsWith(".nut", StringComparison.OrdinalIgnoreCase)).ToList();

        // ---- A. 逐个编译 ----
        var compileFailures = new List<string>();
        long totalBytes = 0;
        using (var vm = new SqVm())
        {
            foreach (PvfEntry e in nuts)
            {
                byte[] data = pvf.Read(e);
                totalBytes += data.Length;
                if (!vm.TryCompile(e.Path, data, out string? error))
                {
                    compileFailures.Add(e.Path + ": " + error);
                }
            }
        }
        Console.WriteLine("A. 语法:共 " + nuts.Count + " 个 .nut(" + totalBytes + " 字节),Squirrel " + SqVm.Version + " 编译失败 " + compileFailures.Count + " 个");
        foreach (string f in compileFailures.Take(10))
        {
            Console.WriteLine("   " + f);
        }

        // ---- B. 模拟加载 ----
        using var host = new ScriptHost(new PvfScriptSource(pvf));
        ScriptLoadSummary summary = host.LoadAll();
        var states = host.Registrations.Where(r => r.Kind == RegistrationKind.State).ToList();
        var scriptFiles = host.Registrations.Where(r => r.Kind == RegistrationKind.ScriptFile).ToList();
        var passives = host.Registrations.Where(r => r.Kind == RegistrationKind.PassiveObject).ToList();
        Console.WriteLine("B. 入口 sqr/" + ScriptHost.EntryScript + ":" + (summary.EntryOk ? "运行成功" : "失败")
            + ";运行了 " + summary.LoadedScripts + " 个脚本,登记 " + host.Registrations.Count + " 项(加载错误 " + summary.Errors + ",PVF 里找不到 " + summary.Missing + ")");
        Console.WriteLine("   pushScriptFiles " + scriptFiles.Count + " 项,pushState " + states.Count + " 项,pushPassiveObj " + passives.Count + " 项");
        Console.WriteLine("   按职业编号统计 pushState:" + string.Join(", ", states.GroupBy(s => s.Job).OrderBy(g => g.Key).Select(g => g.Key + "→" + g.Count())));
        Console.WriteLine("   登记的静态表:" + string.Join(", ", host.Registrations.GroupBy(r => r.Table).Select(g => g.Key + "→" + g.Count())));
        foreach (string e in host.Errors.Take(15))
        {
            Console.WriteLine("   错误: " + e);
        }
        foreach (string m in host.MissingScripts.Take(15))
        {
            Console.WriteLine("   缺少: " + m);
        }

        // ---- C. 每个状态里的回调:函数名 = <前缀>_<后缀>,后缀就是 pushState 的第三个参数 ----
        var prefixStates = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var prefixSignatures = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        var stateReport = new List<object>();
        int statesWithAnyCallback = 0;
        foreach (ScriptRegistration s in states)
        {
            host.TryGetDefinedFunctions(s.ResolvedPath, out List<ScriptFunction>? funcs);
            funcs ??= new List<ScriptFunction>();
            string tail = "_" + s.Suffix;
            var callbacks = new List<string>();
            foreach (ScriptFunction f in funcs.Where(f => f.Name.EndsWith(tail, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                string prefix = f.Name.Substring(0, f.Name.Length - tail.Length);
                if (callbacks.Contains(prefix))
                {
                    continue;
                }
                callbacks.Add(prefix);
                prefixStates[prefix] = prefixStates.TryGetValue(prefix, out int n) ? n + 1 : 1;
                string signature = string.Join(", ", f.Parameters);
                if (!prefixSignatures.TryGetValue(prefix, out SortedDictionary<string, int>? variants))
                {
                    prefixSignatures[prefix] = variants = new SortedDictionary<string, int>(StringComparer.Ordinal);
                }
                variants[signature] = variants.TryGetValue(signature, out int c) ? c + 1 : 1;
            }
            if (callbacks.Count > 0)
            {
                statesWithAnyCallback++;
            }
            stateReport.Add(new { job = s.Job, state = s.Id, skill = s.Skill, suffix = s.Suffix, script = s.ResolvedPath, callbacks });
        }
        Console.WriteLine("C. 有回调的状态 " + statesWithAnyCallback + " / " + states.Count + ";回调前缀共 " + prefixStates.Count + " 种,按出现的状态数排序:");
        foreach (KeyValuePair<string, int> kv in prefixStates.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(40))
        {
            string sigs = string.Join(" | ", prefixSignatures[kv.Key].OrderByDescending(v => v.Value).Select(v => "(" + v.Key + ")×" + v.Value));
            Console.WriteLine("     " + kv.Key.PadRight(24) + kv.Value.ToString().PadLeft(3) + "  " + sigs);
        }

        // ---- D. 没被加载链运行的脚本 ----
        var loadedSet = new HashSet<string>(host.LoadedScripts, StringComparer.OrdinalIgnoreCase);
        List<PvfEntry> notLoaded = nuts.Where(e => !loadedSet.Contains(PvfPath.Normalize(e.Path))).ToList();
        var sourceByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PvfEntry e in nuts)
        {
            sourceByPath[PvfPath.Normalize(e.Path)] = ScriptText.FromBytes(pvf.Read(e)).Replace('\\', '/').ToLowerInvariant();
        }
        var notLoadedReport = new List<object>();
        int referenced = 0;
        foreach (PvfEntry e in notLoaded)
        {
            string path = PvfPath.Normalize(e.Path);
            string needle = path.Substring("sqr/".Length);
            var referrers = sourceByPath.Where(kv => kv.Key != path && kv.Value.Contains(needle, StringComparison.Ordinal)).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (referrers.Count > 0)
            {
                referenced++;
            }
            notLoadedReport.Add(new { script = path, referrers });
        }
        Console.WriteLine("D. 加载链没有运行的脚本 " + notLoaded.Count + " 个,其中路径被别的脚本写成字符串的 " + referenced + " 个;按目录:"
            + string.Join(", ", notLoaded.GroupBy(e => PvfPath.Normalize(e.Path).Split('/')[1]).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + "→" + g.Count())));
        Console.WriteLine("   加载过程中向脚本索要、但没有定义的名字:" + host.UnknownNames.Count + " 个");

        if (!string.IsNullOrEmpty(opts.JsonFile))
        {
            var report = new
            {
                squirrel = SqVm.Version,
                nutFiles = nuts.Count,
                nutBytes = totalBytes,
                compileFailures,
                loadedScripts = host.LoadedScripts.Count,
                registrations = new { scriptFiles = scriptFiles.Count, states = states.Count, passiveObjects = passives.Count },
                missingScripts = host.MissingScripts,
                loadErrors = host.Errors,
                notLoaded = notLoadedReport,
                callbackPrefixStateCounts = prefixStates,
                callbackSignatures = prefixSignatures,
                unresolvedNames = host.UnknownNames,
                states = stateReport,
            };
            File.WriteAllText(opts.JsonFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Console.WriteLine("写入 " + opts.JsonFile);
        }
        Console.WriteLine("用时 " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
        return compileFailures.Count == 0 && summary.EntryOk && summary.Errors == 0 ? 0 : 1;
    }
}
