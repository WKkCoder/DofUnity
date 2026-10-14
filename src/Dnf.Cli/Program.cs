using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dnf.Cli;
using Dnf.Cli.Analysis;
using Dnf.Cli.Verification;
using Dnf.Data;

return CommandLine.Run(args);

namespace Dnf.Cli
{
    internal sealed class Options
    {
        public string? DnfDir { get; set; }
        public string? OutDir { get; set; }
        public int Threads { get; set; } = Math.Max(2, Environment.ProcessorCount - 2);
        public string? ExpectAggregate { get; set; }

        // locate(用截图反向识别精灵)
        public string? Screenshot { get; set; }
        public string? NpkFilter { get; set; }
        public int MinOpaque { get; set; } = 150;
        public double MinRatio { get; set; } = 0.85;
        public int MaxDim { get; set; } = 1200;
        public int MaxAnchor { get; set; } = 300;
        public int Tolerance { get; set; }
        public int Top { get; set; } = 60;
        public string? JsonFile { get; set; }

        // render-town(合成城镇里的 NPC,可与截图对照)
        public string MapPath { get; set; } = "map/elvengard/(r)gate.map";
        public int CameraX { get; set; } = 24;
        public int GroundY { get; set; } = 200;
        public long TimeMs { get; set; }
        public string? PngFile { get; set; }
        public bool BestFrame { get; set; }
        public int Radius { get; set; } = 3;
    }

    internal static class CommandLine
    {
        private const string Usage =
            "用法:\n" +
            "  dnf info\n" +
            "      打印数据目录,以及 Script.pvf / NPK 的基本事实。\n" +
            "  dnf verify <pvf|tags|ani|imgs|pixels|all> [选项]\n" +
            "      用 C# 数据层重新解析全部用户自备数据,与 Python 参考实现的输出逐项对拍。\n" +
            "      参考输出在 <仓库>/out/(见 docs/roadmap.md 里 P1 的生成命令)。\n" +
            "  dnf locate --shot <截图.png> [选项]\n" +
            "      拿一张原版客户端的截图,在全部 IMG 帧里找出不透明像素与截图逐像素相同的帧,并给出位置。\n" +
            "      locate 的选项: --npk-filter <子串>(只扫文件名含它的 NPK)、--min-opaque <N>(默认 150)、\n" +
            "      --min-ratio <0..1>(默认 0.85)、--tol <0..7>(每通道允许的差,默认 0=逐像素相同)、--max-dim <像素>(默认 1200)、--top <N>(打印前 N 个,默认 60)、--json <文件>\n" +
            "  dnf render-town [--map <PVF 里的地图路径>] [--cam-x N] [--ground-y N] [--time <毫秒>] [--png <输出.png>] [--shot <截图.png> [--best-frame] [--radius N]]\n" +
            "      用 CPU 合成器画出地图 [NPC] 列表里的 NPC(模型见证据 E-024);带 --shot 时与原版截图逐像素对照,\n" +
            "      --best-frame 在每个 NPC 的全部帧里选精确一致像素最多的帧;--png 带 --shot 时输出 左=我们 中=原版 右=对照 的三联图。\n" +
            "选项:\n" +
            "  --dnf-dir <目录>          数据目录(默认读环境变量 DNF_DIR,再读仓库根的 local.env)\n" +
            "  --out <目录>              参考输出目录(默认 <仓库>/out)\n" +
            "  --threads <N>             并行线程数(默认 CPU 核数 - 2)\n" +
            "  --expect-aggregate <hex>  PVF 清单聚合哈希的期望值(可选)\n";

        public static int Run(string[] args)
        {
            try
            {
                return RunCore(args);
            }
            catch (UserError ex)
            {
                Console.Error.WriteLine("错误: " + ex.Message);
                return 2;
            }
        }

        private static int RunCore(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.Out.Write(Usage);
                return args.Length == 0 ? 2 : 0;
            }
            string command = args[0];
            var opts = new Options();
            var positional = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--dnf-dir":
                        opts.DnfDir = NextValue(args, ref i);
                        break;
                    case "--out":
                        opts.OutDir = NextValue(args, ref i);
                        break;
                    case "--threads":
                        opts.Threads = int.TryParse(NextValue(args, ref i), out int t) && t > 0 ? t : throw new UserError("--threads 需要正整数");
                        break;
                    case "--expect-aggregate":
                        opts.ExpectAggregate = NextValue(args, ref i);
                        break;
                    case "--shot":
                        opts.Screenshot = NextValue(args, ref i);
                        break;
                    case "--npk-filter":
                        opts.NpkFilter = NextValue(args, ref i);
                        break;
                    case "--min-opaque":
                        opts.MinOpaque = int.TryParse(NextValue(args, ref i), out int mo) && mo > 0 ? mo : throw new UserError("--min-opaque 需要正整数");
                        break;
                    case "--min-ratio":
                        opts.MinRatio = double.TryParse(NextValue(args, ref i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mr) && mr > 0 && mr <= 1
                            ? mr : throw new UserError("--min-ratio 需要 (0,1] 之间的小数");
                        break;
                    case "--tol":
                        opts.Tolerance = int.TryParse(NextValue(args, ref i), out int tl) && tl >= 0 && tl <= 7 ? tl : throw new UserError("--tol 需要 0 到 7 的整数");
                        break;
                    case "--max-dim":
                        opts.MaxDim = int.TryParse(NextValue(args, ref i), out int md) && md > 0 ? md : throw new UserError("--max-dim 需要正整数");
                        break;
                    case "--top":
                        opts.Top = int.TryParse(NextValue(args, ref i), out int tp) && tp > 0 ? tp : throw new UserError("--top 需要正整数");
                        break;
                    case "--json":
                        opts.JsonFile = NextValue(args, ref i);
                        break;
                    case "--map":
                        opts.MapPath = NextValue(args, ref i);
                        break;
                    case "--cam-x":
                        opts.CameraX = int.TryParse(NextValue(args, ref i), out int cx) ? cx : throw new UserError("--cam-x 需要整数");
                        break;
                    case "--ground-y":
                        opts.GroundY = int.TryParse(NextValue(args, ref i), out int gy) ? gy : throw new UserError("--ground-y 需要整数");
                        break;
                    case "--time":
                        opts.TimeMs = long.TryParse(NextValue(args, ref i), out long tm) && tm >= 0 ? tm : throw new UserError("--time 需要非负整数毫秒");
                        break;
                    case "--png":
                        opts.PngFile = NextValue(args, ref i);
                        break;
                    case "--best-frame":
                        opts.BestFrame = true;
                        break;
                    case "--radius":
                        opts.Radius = int.TryParse(NextValue(args, ref i), out int rd) && rd >= 0 && rd <= 16 ? rd : throw new UserError("--radius 需要 0 到 16 的整数");
                        break;
                    default:
                        if (args[i].StartsWith("--", StringComparison.Ordinal))
                        {
                            throw new UserError("未知选项 " + args[i] + "\n" + Usage);
                        }
                        positional.Add(args[i]);
                        break;
                }
            }

            switch (command)
            {
                case "info":
                    return Info(opts);
                case "locate":
                    return SpriteLocator.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "render-town":
                    return TownRender.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "verify":
                    if (positional.Count != 1)
                    {
                        throw new UserError("verify 需要一个目标: pvf|tags|ani|imgs|pixels|all\n" + Usage);
                    }
                    return Verify(positional[0], opts);
                default:
                    throw new UserError("未知命令 " + command + "\n" + Usage);
            }
        }

        private static string NextValue(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
            {
                throw new UserError("选项 " + args[i] + " 缺少取值");
            }
            return args[++i];
        }

        private static int Info(Options opts)
        {
            DnfLocation loc = Locator.Resolve(opts.DnfDir, opts.OutDir);
            Console.WriteLine("数据目录: " + loc.DnfDir);
            using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
            Console.WriteLine("Script.pvf: 版本 " + pvf.Version + ",目录 CRC 0x" + pvf.DirectoryCrc.ToString("X8") + ",文件数 " + pvf.Count
                + ",目录区多出 " + pvf.DirectoryTrailingBytes + " 字节");
            int npkCount = 0;
            long entries = 0;
            foreach (string p in Directory.GetFiles(loc.NpkDir, "*.*"))
            {
                if (!p.EndsWith(".npk", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                using NpkArchive npk = NpkArchive.Open(p);
                npkCount++;
                entries += npk.Count;
            }
            Console.WriteLine("ImagePacks2: " + npkCount + " 个 NPK,共 " + entries + " 个条目");
            return 0;
        }

        private static int Verify(string target, Options opts)
        {
            DnfLocation loc = Locator.Resolve(opts.DnfDir, opts.OutDir);
            var runners = new List<(string Name, Func<VerificationResult> Run)>();
            void Add(string name, Func<DnfLocation, Options, VerificationResult> run) => runners.Add((name, () => run(loc, opts)));
            switch (target)
            {
                case "pvf": Add("pvf", PvfVerifier.Run); break;
                case "tags": Add("tags", TagVerifier.Run); break;
                case "ani": Add("ani", AniVerifier.Run); break;
                case "imgs": Add("imgs", ImgVerifier.Run); break;
                case "pixels": Add("pixels", PixelVerifier.Run); break;
                case "all":
                    Add("pvf", PvfVerifier.Run);
                    Add("tags", TagVerifier.Run);
                    Add("ani", AniVerifier.Run);
                    Add("imgs", ImgVerifier.Run);
                    Add("pixels", PixelVerifier.Run);
                    break;
                default:
                    throw new UserError("未知验证目标 " + target + "\n" + Usage);
            }

            var results = new List<VerificationResult>();
            var total = Stopwatch.StartNew();
            foreach ((string name, Func<VerificationResult> run) in runners)
            {
                Console.Error.WriteLine("== 验证 " + name + " ==");
                VerificationResult result = run();
                results.Add(result);
                Console.WriteLine((result.Passed ? "[通过] " : "[失败] ") + result.Name + "  用时 " + result.Seconds.ToString("F1") + "s");
                foreach (KeyValuePair<string, object?> kv in result.Metrics)
                {
                    if (kv.Value is not System.Collections.IDictionary and not IEnumerable<KeyValuePair<string, long>>)
                    {
                        Console.WriteLine("    " + kv.Key + " = " + kv.Value);
                    }
                }
                foreach (string f in result.Failures)
                {
                    Console.WriteLine("    ✗ " + f);
                }
                if (result.SuppressedFailures > 0)
                {
                    Console.WriteLine("    ✗ ……还有 " + result.SuppressedFailures + " 条失败说明未列出");
                }
            }

            WriteReport(loc, results, total.Elapsed.TotalSeconds, target);
            bool allPassed = results.All(r => r.Passed);
            Console.WriteLine(allPassed ? "全部通过" : "存在失败项");
            return allPassed ? 0 : 1;
        }

        private static void WriteReport(DnfLocation loc, List<VerificationResult> results, double seconds, string target)
        {
            string path = Path.Combine(loc.OutDir, "p1_verification_" + target + ".json");
            using FileStream fs = File.Create(path);
            using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
            w.WriteStartObject();
            w.WriteString("generated_utc", DateTime.UtcNow.ToString("o"));
            w.WriteString("target", target);
            w.WriteString("runtime", RuntimeInformation.FrameworkDescription);
            w.WriteString("os", RuntimeInformation.OSDescription);
            w.WriteNumber("logical_cores", Environment.ProcessorCount);
            w.WriteNumber("seconds_total", Math.Round(seconds, 2));
            w.WriteBoolean("all_passed", results.All(r => r.Passed));
            w.WritePropertyName("checks");
            w.WriteStartArray();
            foreach (VerificationResult r in results)
            {
                r.WriteJson(w);
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.Flush();
            Console.Error.WriteLine("报告已写入 " + path);
        }
    }
}
