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

        /// <summary>atk-check --dump:把每个 .atk 读出的字段逐行写到这个文件(放 out/,不入库),用来与 Python 参考实现对拍。</summary>
        public string? DumpFile { get; set; }

        /// <summary>damage-check --cases:`tools/py/damage_ref.py --gen` 生成的随机对拍用例(放 out/,不入库)。</summary>
        public string? CasesFile { get; set; }

        /// <summary>script-stats --ext:要统计的脚本扩展名(默认 ai)。</summary>
        public string? Ext { get; set; }

        // render-town(合成城镇里的 NPC,可与截图对照)
        public string MapPath { get; set; } = "map/elvengard/(r)gate.map";
        public int CameraX { get; set; } = 24;
        public int GroundY { get; set; } = 200;
        public long TimeMs { get; set; }
        public string? PngFile { get; set; }
        public bool BestFrame { get; set; }
        public int Radius { get; set; } = 3;

        /// <summary>朝向镜像:auto = 朝向 [left] 的 NPC 镜像;on = 全部镜像;off = 都不镜像。帧自己的 x 缩放为负(帧属性 7)总是生效。</summary>
        public string Mirror { get; set; } = "auto";

        /// <summary>render-town --scene:合成整个场景(地图图层 + NPC)而不只是 NPC,与截图整屏对照。</summary>
        public bool Scene { get; set; }

        /// <summary>render-town --scene --hero x,y,left|right,rest|move,动作毫秒:把默认外观的主角摆在世界 (x, y),朝向、动作与动作已播放的毫秒数照给的值。</summary>
        public string? Hero { get; set; }

        /// <summary>hero-check:原版连拍的 index.tsv、截图目录、要用的截图名前缀。</summary>
        public string? IndexFile { get; set; }
        public string? ShotsDir { get; set; }
        public string? Prefix { get; set; }

        /// <summary>hero-check --start x,y,left|right:第一张截图时主角的世界位置与朝向(站立)。</summary>
        public string? Start { get; set; }

        /// <summary>hero-check --keys 毫秒:方向,…:相对第一张截图时刻的按键时间表。</summary>
        public string? Keys { get; set; }

        /// <summary>hero-check --phase auto|毫秒:第一张截图时站立动画已播放的毫秒数。</summary>
        public string? Phase { get; set; }

        /// <summary>hero-check --cam-range 最小,最大:镜头 x 的范围(赛丽亚房间是 0,96)。</summary>
        public string? CamRange { get; set; }

        /// <summary>render-town --scene --hero 的武器装备脚本(相对 equipment/character/swordman/;默认起始武器 weapon/katana/kat_damaged.equ)。</summary>
        public string HeroWeapon { get; set; } = "weapon/katana/kat_damaged.equ";

        /// <summary>game-check --sweep:对每个已登记的技能状态各跑一遍,列出错误与缺的接口。</summary>
        public bool Sweep { get; set; }

        /// <summary>game-check --sweep --only 后缀(可重复):只扫状态后缀(不分大小写)匹配的技能状态,调试单个技能用。</summary>
        public List<string> Only { get; } = new();

        /// <summary>render-town --scene --eval x0,y0,x1,y1(可重复):额外统计这些矩形(含 x0,y0,不含 x1,y1)内的吻合率。</summary>
        public List<(int X0, int Y0, int X1, int Y1)> EvalRects { get; } = new();

        // fit-map(在截图里搜索地图动画层的位置)
        public int FitTolerance { get; set; } = 8;
        public int RangeX { get; set; } = 400;
        public int RangeY { get; set; } = 250;
        public int Samples { get; set; } = 2000;
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
            "  dnf render-town [--map <PVF 里的地图路径>] [--cam-x N] [--ground-y N] [--time <毫秒>] [--png <输出.png>] [--shot <截图.png> [--best-frame] [--radius N]] [--mirror auto|on|off]\n" +
            "      用 CPU 合成器画出地图 [NPC] 列表里的 NPC(模型见证据 E-024);带 --shot 时与原版截图逐像素对照,\n" +
            "      --best-frame 在每个 NPC 的全部帧里选精确一致像素最多的帧;--png 带 --shot 时输出 左=我们 中=原版 右=对照 的三联图。\n" +
            "  dnf render-town --scene --shot <截图.png> [--time <毫秒>] [--map <地图>] [--png <输出.png>] [--eval x0,y0,x1,y1 ...]\n" +
            "  dnf render-town --scene --png <输出.png> [--time <毫秒>] [--map <地图>]      (不带截图:只输出 800×600 的 CPU 合成结果)\n" +
            "      --hero x,y,left|right,rest|move,动作毫秒 [--hero-weapon <武器 .equ>]:把默认外观的主角(证据 E-065)摆在世界 (x, y) 一起合成;镜头 x 用 --cam-x。\n" +
            "      合成整个场景(地图 [animation] 图层 + NPC;加法混合光效与染色 alpha 的帧间插值见证据 E-030),与截图整屏逐像素对照并按格子给出吻合率。\n" +
            "  dnf hero-check --index <index.tsv> --shots <截图目录> --prefix <截图名前缀> --start x,y,left|right [--keys 毫秒:方向,…] [--phase auto|毫秒] [--cam-range 0,96] [--map <地图>]\n" +
            "      按\"起始状态 + 按键时间表\"模拟主角与镜头,在每张连拍截图的时刻合成主角,逐张给出主角像素的吻合率与 ±3 像素内的最佳偏移(证据 E-065、E-066;截图只在本机,不入库)。\n" +
            "  dnf fit-map --shot <截图.png> [--map <地图>] [--fit-tol N] [--range-x N] [--range-y N] [--samples N] [--png <输出.png>]\n" +
            "      对地图 [animation] 里每个动画的每一帧,在截图里搜索不透明像素在容差内一致最多的位置,并与位置模型的预测比较(证据 E-030)。\n" +
            "  dnf sq-check [--json <文件>]\n" +
            "      用 Squirrel 3.2 检查 PVF 里全部明文 .nut:逐个编译;再模拟引擎加载入口脚本 sqr/loadstate.nut,统计登记的状态、回调前缀与缺少的接口(证据 E-033、E-034)。\n" +
            "  dnf game-check [--sweep] [--json <文件>]\n" +
            "      用 Dnf.Game 的最小世界,在原版 Squirrel 状态脚本不改动的情况下跑男魔法师的 IceCrash 技能,记录回调与接口调用序列、没实现的接口与接口覆盖率(证据 E-036、E-037)。\n" +
            "      --only <后缀>:配合 --sweep,只跑状态后缀匹配的技能状态(可重复),调试单个技能。\n" +
            "      --sweep:再对每个登记了技能的状态各跑一遍(假人在前 70 处,技能等级 10,最多 12 秒),给出每个技能的结果、脚本错误与缺的接口。\n" +
            "  dnf atk-check [--dump <文件>] [--json <文件>]\n" +
            "      用 Dnf.Game 的 .atk 加载器读全库 .atk(证据 E-047),统计不规整情况;--dump 写出每个文件读到的字段,可与 tools/py/atk_ref.py --dump 逐行对拍。\n" +
            "  dnf act-check [--dump <文件>] [--json <文件>]\n" +
            "      用 Dnf.Game 的语法树解析器读全库 .act(证据 E-050),统计节点、块、多余与笔误闭合;--json 写出\"父标签 → 子标签 → 次数\"表,可与 tools/py/act_tree.py 逐项对拍。\n" +
            "  dnf ai-check [--dump <文件>] [--json <文件>]\n" +
            "      用 Dnf.Game 的 .ai 解析器读全库 .ai(证据 E-052):能否读完、错误、剩余标记、关键字与运算符直方图,以及 [void] 函数真假两路的返回形状(用来判断真假方向);名表运行时从数据包的 AICharacter/OperatingValue.lst 读。\n" +
            "  dnf mob-check [--dump <文件>] [--json <文件>]\n" +
            "      用 Dnf.Game 的怪物定义读 monster/monster.lst 列出的全部 .mob(证据 E-062):成对标签规整度、[ability category] 类别与运算符、基础属性表命中、引用的 .act/.ani/.atk/.ai 路径是否存在;\n" +
            "      --dump 写出每个怪物在最小/中间/最大等级、普通/首领种类下算出的完整数值块,可与 tools/py/mob_ability_ref.py --dump 逐行对拍(输出含原版派生数值,放 out/,不入库)。\n" +
            "  dnf mob-check --cases <文件>\n" +
            "      读 tools/py/mob_ability_oracle.py --write-cases 写出的合成用例(期望值来自服务端原版函数在模拟器里的运行结果),用 C# 重算并逐槽比较。\n" +
            "  dnf script-stats --ext <扩展名> [--top N] [--json <文件>]\n" +
            "      按标签统计某一类标记流脚本的词汇:次数、文件数、每个值位置上的值种类与字符串直方图、\"第一个字符串 → 形状\"表。用来在读客户端解释器前弄清脚本的词汇;输出放 out/,不入库。\n" +
            "  dnf damage-check [--cases <文件>]\n" +
            "      伤害公式(证据 E-049)。不带 --cases:读 PVF 里的 Etc/EtcParameter.etc 与 Creature/Common/creatureCommon.cre,只报告个数与完整性(不输出原版数值);\n" +
            "      带 --cases:读 tools/py/damage_ref.py --gen 生成的随机用例,用 C# 公式复算并与 Python 参考实现逐项比较。\n" +
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
                    case "--dump":
                        opts.DumpFile = NextValue(args, ref i);
                        break;
                    case "--cases":
                        opts.CasesFile = NextValue(args, ref i);
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
                    case "--sweep":
                        opts.Sweep = true;
                        break;
                    case "--only":
                        opts.Only.Add(NextValue(args, ref i));
                        break;
                    case "--ext":
                        opts.Ext = NextValue(args, ref i);
                        break;
                    case "--scene":
                        opts.Scene = true;
                        break;
                    case "--hero":
                        opts.Hero = NextValue(args, ref i);
                        break;
                    case "--hero-weapon":
                        opts.HeroWeapon = NextValue(args, ref i);
                        break;
                    case "--index":
                        opts.IndexFile = NextValue(args, ref i);
                        break;
                    case "--shots":
                        opts.ShotsDir = NextValue(args, ref i);
                        break;
                    case "--prefix":
                        opts.Prefix = NextValue(args, ref i);
                        break;
                    case "--start":
                        opts.Start = NextValue(args, ref i);
                        break;
                    case "--keys":
                        opts.Keys = NextValue(args, ref i);
                        break;
                    case "--phase":
                        opts.Phase = NextValue(args, ref i);
                        break;
                    case "--cam-range":
                        opts.CamRange = NextValue(args, ref i);
                        break;
                    case "--eval":
                        opts.EvalRects.Add(ParseRect(NextValue(args, ref i)));
                        break;
                    case "--mirror":
                        opts.Mirror = NextValue(args, ref i) is var mv && (mv is "auto" or "on" or "off") ? mv : throw new UserError("--mirror 需要 auto、on 或 off");
                        break;
                    case "--radius":
                        opts.Radius = int.TryParse(NextValue(args, ref i), out int rd) && rd >= 0 && rd <= 16 ? rd : throw new UserError("--radius 需要 0 到 16 的整数");
                        break;
                    case "--fit-tol":
                        opts.FitTolerance = int.TryParse(NextValue(args, ref i), out int ft) && ft >= 0 && ft <= 255 ? ft : throw new UserError("--fit-tol 需要 0 到 255 的整数");
                        break;
                    case "--range-x":
                        opts.RangeX = int.TryParse(NextValue(args, ref i), out int rx) && rx >= 0 && rx <= 2000 ? rx : throw new UserError("--range-x 需要 0 到 2000 的整数");
                        break;
                    case "--range-y":
                        opts.RangeY = int.TryParse(NextValue(args, ref i), out int ry) && ry >= 0 && ry <= 2000 ? ry : throw new UserError("--range-y 需要 0 到 2000 的整数");
                        break;
                    case "--samples":
                        opts.Samples = int.TryParse(NextValue(args, ref i), out int sm) && sm >= 100 && sm <= 100000 ? sm : throw new UserError("--samples 需要 100 到 100000 的整数");
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
                    return opts.Scene
                        ? SceneCompare.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts)
                        : TownRender.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "fit-map":
                    return MapFit.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "hero-check":
                    return HeroCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "sq-check":
                    return SquirrelCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "game-check":
                    return GameCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "atk-check":
                    return AtkCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "act-check":
                    return ActCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "ai-check":
                    return AiCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "mob-check":
                    return opts.CasesFile != null
                        ? MobCheck.RunCases(Locator.Resolve(opts.DnfDir, opts.OutDir), opts)
                        : MobCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "script-stats":
                    return ScriptStats.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "damage-check":
                    return opts.CasesFile != null
                        ? DamageCheck.RunCases(opts)
                        : DamageCheck.RunConstants(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
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

        private static (int, int, int, int) ParseRect(string text)
        {
            string[] parts = text.Split(',');
            if (parts.Length != 4 || !int.TryParse(parts[0], out int x0) || !int.TryParse(parts[1], out int y0)
                || !int.TryParse(parts[2], out int x1) || !int.TryParse(parts[3], out int y1) || x1 <= x0 || y1 <= y0)
            {
                throw new UserError("--eval 需要 x0,y0,x1,y1 且 x1 > x0、y1 > y0");
            }
            return (x0, y0, x1, y1);
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
