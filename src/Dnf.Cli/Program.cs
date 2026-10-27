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

        /// <summary>render-town --scene --room --cam-y:纵向镜头(只有带 [extended tile] 的房间有意义,范围 0 到扩展部分的高度)。</summary>
        public int CameraY { get; set; }

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

        /// <summary>hero-find --motion rest|move|dashattack|attack&lt;N&gt;|etc&lt;N&gt;|ani:&lt;路径&gt;[#帧号]:主角的动作(默认 rest);--search N:以 --start 为中心搜索的半径(像素,默认 24,最大 80)。</summary>
        public string Motion { get; set; } = "rest";
        public int Search { get; set; } = 24;

        /// <summary>render-town --scene --hero 的武器装备脚本(相对 equipment/character/swordman/;默认起始武器 weapon/katana/kat_damaged.equ)。</summary>
        public string HeroWeapon { get; set; } = "weapon/katana/kat_damaged.equ";

        /// <summary>--hero-wait:站立用战斗待机动作 [waiting motion](副本),不带则用城镇的 [rest motion]。</summary>
        public bool HeroWait { get; set; }

        /// <summary>--monsters left|right:房间里按地图 [monster] 在出生位置画出怪物(待机第一帧),朝向统一指定(证据 E-069)。</summary>
        public string? Monsters { get; set; }

        /// <summary>combat-check --play 秒:动作,…:按时间表替主角按键(动作 = left/right/up/down/none,可用 + 组合,attack 表示在那一刻按一下攻击键)。</summary>
        public string? Play { get; set; }

        /// <summary>combat-check --seconds N:模拟多少秒(默认 10);--shots-at 秒,秒,…:这些时刻各写一张 PNG(文件名前缀取 --png)。</summary>
        public double Seconds { get; set; } = 10;

        /// <summary>命令行里明确给了 --seconds(dungeon-check 没给时默认跑 900 秒,够打完一座副本)。</summary>
        public bool SecondsGiven { get; set; }
        public string? ShotsAt { get; set; }

        /// <summary>dungeon-check --watch 秒:从这一秒起的 6 秒里每 0.1 秒打印一行主角位置、动作与自动机的意图(诊断自动机卡在哪里)。</summary>
        public double? Watch { get; set; }

        /// <summary>combat-check --trace:每 0.25 秒打印一次主角与怪物的位置、阶段与血量。</summary>
        public bool Trace { get; set; }

        /// <summary>dungeon-check --dungeon:副本定义的 PVF 路径(默认洛兰);--bot:让 <see cref="Dnf.Play.DungeonBot"/> 自动打(否则按 --play 的时间表);--seed:战斗与随机选图的种子;--random-layout:按种子随机选每格的房间。</summary>
        public string Dungeon { get; set; } = "dungeon/act1/lorien.dgn";
        public bool Bot { get; set; }

        /// <summary>dungeon-check --explore:自动机先把首领格以外的每一格都走一遍,再去首领格(默认直接走最短路)。</summary>
        public bool Explore { get; set; }

        /// <summary>combat-check、dungeon-check --no-damage:主角没有生命值,怪物打不到他(与怪物出招功能加入之前的行为一致,E-086);用来让按时刻比对的回归检查不受还手影响。</summary>
        public bool NoDamage { get; set; }

        /// <summary>combat-check、dungeon-check、flow-check、sound-check --no-exp:不给主角等级进度(杀怪不涨经验、没有经验条,E-111 之前的行为);--no-damage 时本来就没有(主角没有生命值与底栏)。</summary>
        public bool NoExp { get; set; }

        /// <summary>--level N:主角的起始等级(1 到 85,默认 1);exp-check 用它看某一级的属性与升级所需经验。</summary>
        public int HeroLevel { get; set; } = 1;

        /// <summary>dungeon-check --damage-scale N:测试旋钮,主角打出的每一击伤害乘上 N(默认 1;用于全库副本扫描排除"一级主角打不动高等级怪物"的干扰,不是原版规则)。</summary>
        public int DamageScale { get; set; } = 1;

        /// <summary>dungeon-check、flow-check、sound-check --coins N:复活币的起始库存(0 到 99,默认 5;占位,没有背包,证据 E-115);0 就是主角死了只能等失败计时走完。</summary>
        public int Coins { get; set; } = Dnf.Play.ReviveCoins.DefaultStock;

        /// <summary>dungeon-check、flow-check、sound-check --monster-damage-scale N:测试旋钮,怪物打中主角的每一击伤害乘上 N(默认 1;死亡、复活与失败回城的无人值守检查用它让主角几下就死,不是原版规则)。</summary>
        public int MonsterDamageScale { get; set; } = 1;

        /// <summary>dungeon-check、flow-check --difficulty N:副本难度(0 到 4,默认 0;M33,证据 E-125):怪物数值的难度加成、经验、掉落、复活币规则、任务与评分都按它走;命令行直接指定,不检查通关记录。</summary>
        public int Difficulty { get; set; }

        /// <summary>flow-check --raise-difficulty:流程自动机在副本选择界面把难度调到当前能选的最高一档(每通关一次解锁下一档)。</summary>
        public bool RaiseDifficulty { get; set; }

        /// <summary>dungeon-check、flow-check --smash:自动机清房、捡完金币之后顺手把房间里的可破坏物件打翻(M34,证据 E-126;默认关)。</summary>
        public bool Smash { get; set; }

        /// <summary>dungeon-check、flow-check、combat-check --learn:开局先让主角按自动机策略(<see cref="SkillPlanner"/>)把技能点与觉醒点花光(M35,证据 E-127;默认关)。</summary>
        public bool LearnSkills { get; set; }

        public int Seed { get; set; } = 1;
        public bool RandomLayout { get; set; }

        /// <summary>dungeon-check --frame-ms:每一帧推进多少毫秒(默认 1000/60);用来模拟播放器里帧率不固定的实时运行(自动机按固定心跳补步,不受影响;时间表按帧长取样)。</summary>
        public double FrameMs { get; set; }

        /// <summary>dungeon-check --jitter:每一帧的毫秒数在 ±这个比例内随机浮动(0 到 0.9,随机数由 --seed 决定);模拟播放器里帧时间的抖动。</summary>
        public double Jitter { get; set; }

        /// <summary>dungeon-check --after-clear:首领清空之后再空转多少秒(主角不动),让结算画面出现;--shots-at 可以取这段时间里的画面。</summary>
        public double AfterClearSeconds { get; set; }

        /// <summary>dungeon-check --layout-only:只打印迷宫与每格选中的房间,不模拟。</summary>
        public bool LayoutOnly { get; set; }

        /// <summary>flow-check --choices:结算菜单依次做的选择(retry、select、town,逗号分隔);没给时用 <see cref="Dnf.Cli.Analysis.FlowCheck.DefaultChoices"/>。</summary>
        public string? Choices { get; set; }

        /// <summary>flow-check --from:流程从哪个阶段开始(char 选角、town、select、dungeon;默认 town)。</summary>
        public string FlowFrom { get; set; } = "town";

        /// <summary>town-check --town:只详细打印这座城镇(town.lst 里的编号);默认 -1 = 只打印全部城镇的汇总。</summary>
        public int TownId { get; set; } = -1;

        /// <summary>town-shot --area:城镇图册里的一块区域,写 城镇/区域(例如 1/0)。</summary>
        public string? TownArea { get; set; }

        /// <summary>charselect-shot --chars:名册里有几个角色(1 到 12,默认 1;多出来的都是同样的默认角色,看格子摆位用)。</summary>
        public int CharCount { get; set; } = 1;

        /// <summary>charselect-shot --select:选中名册里的第几个角色(从 1 数,默认 1)。</summary>
        public int SelectIndex { get; set; } = 1;

        /// <summary>charselect-shot --no-avatars:不画角色头像(只剩底座、框与文字),拿来和带头像的画面相减量出头像的位置。</summary>
        public bool NoAvatars { get; set; }

        /// <summary>bag-shot --tab:背包窗口翻到第几个页签(0 到 3,默认 0 = 装备页)。</summary>
        public int BagTab { get; set; }

        /// <summary>bag-shot --panel:光标放进装备栏(默认在物品格子上)。</summary>
        public bool BagPanel { get; set; }

        /// <summary>shop-shot --shop:商店编号(<c>itemshop.lst</c> 的编号);−1 = 第一家有商品的商店。</summary>
        public int ShopIndex { get; set; } = -1;

        /// <summary>shop-shot --sell:画出售页(默认买入页)。</summary>
        public bool ShopSell { get; set; }

        /// <summary>shop-shot --prompt:在光标处按一次回车(可叠加物品弹出数量框)。</summary>
        public bool ShopPrompt { get; set; }

        /// <summary>town-shot --hud:在城镇画面上叠城镇底栏(<see cref="Dnf.Play.TownHud"/>,M30;等级取 --level)。</summary>
        public bool TownHudOn { get; set; }

        /// <summary>town-shot --npc-rects:逐个打印画面上 NPC 精灵的编号与屏幕矩形(量名牌位置用)。</summary>
        public bool NpcRects { get; set; }

        /// <summary>flow-check --legacy-town:城镇只用赛丽亚房间一张图(没有出口与加载画面,城镇图册之前的行为);默认走城镇图册(证据 E-095)。</summary>
        public bool LegacyTown { get; set; }

        /// <summary>flow-check --worldmap:副本选择界面用的世界地图定义(PVF 路径,默认 <see cref="Dnf.Cli.Analysis.FlowCheck.DefaultWorldMap"/>);写 none 用占位界面(只有名字列表)。</summary>
        public string? WorldMap { get; set; }

        /// <summary>arrival-check --hero-id:主角对象编号(进房间落点的随机种子 = 它 × 地图编号,证据 E-074);默认 <see cref="Dnf.Render.GateArrival.DefaultHeroObjectId"/>。</summary>
        public int HeroId { get; set; } = Dnf.Render.GateArrival.DefaultHeroObjectId;

        /// <summary>arrival-check --scan 起:止(不含):在这个编号范围里找满足所有 --expect 的主角编号。</summary>
        public string? Scan { get; set; }

        /// <summary>arrival-check --expect 房间:侧:x:y:容差,…(侧 = start|left|right|up|down;y 写 * 表示不限):--scan 要满足的落点。</summary>
        public string? Expect { get; set; }

        /// <summary>arrival-check --probe 房间:侧:y,…:在高度 y 上沿水平线找出口门体挡路的边界(从出口向房间里找第一个主角站得下的 x)。</summary>
        public string? Probe { get; set; }

        /// <summary>render-town --scene --room:按副本房间合成(瓦片、视差背景),而不是城镇(证据 E-067)。</summary>
        public bool Room { get; set; }

        /// <summary>--gate 左|右|上|下 对应 left|right|up|down 加编号,如 right:364:closed(可重复):把这个出口按地图 [pathgate pos] 的位置画出来。</summary>
        public List<string> Gates { get; } = new();

        /// <summary>--demo-gates:按 <see cref="DemoRoom"/> 里量过的摆法放出口(与 Unity 演示一致)。</summary>
        public bool DemoGates { get; set; }

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

        /// <summary>breakable-check:实机部分最多造几个有可破坏物件的房间。</summary>
        public int Rooms { get; set; } = 3;

        /// <summary>breakable-check --pick:只打印第一座适合"自动机清房后打翻物件"检查的副本路径(给链路检查脚本用,不做别的检查)。</summary>
        public bool Pick { get; set; }

        // fit-sprite(在截图里搜索某张 IMG 帧的位置)
        public string? Img { get; set; }
        public int Frame { get; set; }

        /// <summary>--frame-to N:fit-sprite 扫描 --frame 到 N 的每一帧,只列出吻合度较高的。</summary>
        public int FrameTo { get; set; } = -1;

        /// <summary>--scan-min P:扫描时只保留精确吻合率不低于 P% 的帧(默认 30);--scan-top N:按精确像素数列前 N 个(默认 8)。</summary>
        public int ScanMinPercent { get; set; } = 30;
        public int ScanTop { get; set; } = 8;
        public string? Pred { get; set; }
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
            "      --room [--gate left|right|up|down:编号[:closed|open] …] [--demo-gates] [--monsters left|right]:把地图当副本房间合成(瓦片、视差背景、图层、障碍物、出口,证据 E-067、E-068);--demo-gates 按 Unity 演示里量过的摆法放出口;--monsters 在地图 [monster] 的位置画出怪物(证据 E-069)。\n" +
            "      --hero x,y,left|right,rest|move,动作毫秒 [--hero-weapon <武器 .equ>] [--hero-wait]:把默认外观的主角(证据 E-065)摆在世界 (x, y) 一起合成;镜头 x 用 --cam-x;--hero-wait 用战斗待机动作(副本)。\n" +
            "      合成整个场景(地图 [animation] 图层 + NPC;加法混合光效与染色 alpha 的帧间插值见证据 E-030),与截图整屏逐像素对照并按格子给出吻合率。\n" +
            "  dnf hero-check --index <index.tsv> --shots <截图目录> --prefix <截图名前缀> --start x,y,left|right [--keys 毫秒:方向,…] [--phase auto|毫秒] [--cam-range 0,96] [--map <地图>]\n" +
            "      按\"起始状态 + 按键时间表\"模拟主角与镜头,在每张连拍截图的时刻合成主角,逐张给出主角像素的吻合率与 ±3 像素内的最佳偏移(证据 E-065、E-066;截图只在本机,不入库)。\n" +
            "  dnf hero-find --shot <截图.png> --start x,y,left|right [--map <房间地图>] [--cam-x N] [--hero-wait] [--motion rest|move|dashattack|attack<N>|etc<N>|ani:<路径>[#帧号]] [--search N]\n" +
            "      在一张截图里找主角:以 --start 为中心搜 ±N 像素(默认 24)、同时试遍这个动作(站立/行走/奔跑攻击/连招第 N 段/[etc motion] 第 N 项)的每个动画帧,列出主角不透明像素精确一致率最高的位置(证据 E-070、E-076)。\n" +
            "  dnf room-check --shots <截图目录> --prefix <截图名前缀> [--index <index.tsv>] --map <房间地图> [--gate right:364:closed …]\n" +
            "      对一组原版连拍,逐张量出副本房间的镜头 x(背景、瓦片、图层动画与门按候选镜头合成,在没有界面的区域数精确一致的像素,取最大;证据 E-067、E-068)。\n" +
            "  dnf fit-map --shot <截图.png> [--map <地图>] [--fit-tol N] [--range-x N] [--range-y N] [--samples N] [--png <输出.png>]\n" +
            "      对地图 [animation] 里每个动画的每一帧,在截图里搜索不透明像素在容差内一致最多的位置,并与位置模型的预测比较(证据 E-030)。\n" +
            "  dnf combat-check --map <房间地图> [--start x,y,left|right] [--play 秒:动作,…] [--seconds N] [--shots-at 秒,…] [--png 前缀] [--no-damage]\n" +
            "      (副本房间里的战斗模拟:按时间表按键,打印命中与怪物状态,可在指定时刻输出 800×600 画面;证据 E-069。怪物会还手(E-086),--no-damage 让主角没有生命值、打不到,回到还手功能加入之前的行为)\n" +
            "  dnf dungeon-check [--dungeon <副本定义>] [--seed N] [--random-layout] [--bot [--explore] | --play 秒:动作,…] [--seconds N(默认 900)] [--frame-ms 毫秒] [--jitter 比例] [--shots-at 秒,…] [--png 前缀] [--layout-only] [--no-damage] [--damage-scale N] [--after-clear 秒] [--smash] [--learn] [--watch 秒]\n" +
            "      (整座副本:读迷宫、选每格的房间、打印布局;--bot 让自动机从起点打到首领清空,逐个房间打印进出与清房时刻;--after-clear 清空之后再空转几秒(看结算画面);--smash 清房、捡完金币之后顺手打翻房间里的可破坏物件(M34,证据 E-126);--learn 开局先让主角把技能点花光(M35,证据 E-127,自动机策略 SkillPlanner,快捷栏随之排满);--watch 从这一秒起的 6 秒里每 0.1 秒打印一行主角位置与自动机的意图(诊断自动机卡在哪);--shots-at 在指定时刻输出画面;--frame-ms、--jitter 改每帧的毫秒数与抖动,模拟帧率不固定的实时运行(自动机按固定心跳补步,不受影响;时间表按帧长取样);证据 E-070、E-072)\n" +
            "  dnf flow-check [--dungeon <副本定义>] [--from char|town|select|dungeon] [--town N] [--legacy-town] [--worldmap <世界地图定义>|none] [--choices retry,select,town] [--explore] [--smash] [--learn] [--seed N] [--no-damage] [--seconds N(默认 1800)] [--frame-ms 毫秒] [--shots-at 秒,… --png 前缀]\n" +
            "      (整条流程的无人值守检查:城镇(默认从城镇图册的登录区域出发,自动机沿出口走到地下城入口,换区域与进图有加载画面;--legacy-town 退回只有赛丽亚房间一张图、阶段瞬间切换的老行为;--town 指定起始城镇) → 副本选择 → 副本 → 结算 → 按 --choices 清单依次选再次挑战(retry)、选择其它地下城(select)、返回城镇(town),打印每次阶段切换与每局的战绩;清单用完或到时间为止;默认清单 retry,select,town;--shots-at 在指定时刻输出当时阶段的画面;证据 E-093)\n" +
            "  dnf town-shot --area <城镇>/<区域> --png <输出.png> [--time <毫秒>] [--hero x,y,left|right,rest|move,毫秒[,镜头x]] [--hud [--level N]]\n" +
            "      (城镇图册里一块区域的 800×600 画面:主角在登录落脚点,房间式合成,证据 E-095;Unity 的 -dnftownarea 离屏渲染以它为 CPU 参照)\n" +
            "  dnf charselect-shot --png <输出.png> [--time <毫秒>] [--chars N(1 到 12,默认 1)] [--select N] [--no-avatars]\n" +
            "      (选角界面在界面打开 --time 毫秒时的 800×600 画面,证据 E-096:名册是 N 个默认角色,--select 选中第几个(从 1 数);Unity 的 -dnfflowfrom char 离屏渲染以它为 CPU 参照)\n" +
            "  dnf town-check [--town N]\n" +
            "      (城镇图册检查:读 town.lst 与每座 .twn,汇总区域类型、出口的去处与对称性、到地下城入口的路线;--town N 逐区域打印出口矩形、触发方向与按规则算出的落脚点(本机查看,数值不入库))\n" +
            "  dnf arrival-check [--dungeon <副本定义>] [--hero-id N] [--scan 起:止 --expect 房间:侧:x:y:容差,…] [--probe 房间:侧:y,…]\n" +
            "      (进房间的落点:对副本每个房间打印各出口的识别矩形与按客户端规则算出的落点(随机点、最终点);--scan 在主角编号范围里找同时满足所有 --expect 的编号,房间写地图文件名如 s3,侧为 start/left/right/up/down,y 写 * 不限;--probe 沿水平线 y 找出口门体挡路的边界(左右出口,y 写 * 表示出口矩形里每 6 像素一条);证据 E-074)\n" +
            "  dnf fit-sprite --shot <截图.png> --img <NPK 里的 IMG 路径,如 sprite/map/01tile03.img> [--frame N] [--pred x,y] [--fit-tol N] [--range-x N] [--range-y N] [--samples N]\n" +
            "      在截图里搜索某一张 IMG 帧的不透明像素在容差内一致最多的位置(以 --pred 为中心,默认 0,0),打印前几个峰;用来量地面瓦片、远景/中景与门的摆放。\n" +
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
            "  dnf sound-check [--bot [--dungeon <副本定义>] [--seconds N] [--seed N] [--no-damage]] [--json <文件>]\n" +
            "      声音数据与触发(证据 E-089、E-090):audio.xml 各类条目数、文件是否在 SoundPacks/Music 里且是 Ogg、RANDOM/GROUP 能否展开;全库脚本里 .map [sound]、.mob 的声音标签、.atk [hit wav]、.chr [weapon wav]、.ani 帧声音在 audio.xml 里的命中率;\n" +
            "      代码里写死的语音标签是否存在;--bot 让自动机打一座副本并统计战斗发出的声音提示(没有录音可对照:验证的是数据对得上、触发有发出)。\n" +
            "  dnf hero-damage [--map <房间地图>] [--weapon <.equ 路径>] [--samples N] [--seed N]\n" +
            "      主角打这间房间每种怪物的伤害分布(证据 E-110):每种招式各掷 N 次,按\"第一击(算额外命中)\"与\"之后\"两组打印最小、平均、最大与暴击占比。\n" +
            "      数值来自本机的数据,只打印在终端,不要抄进入库的文件。\n" +
            "  dnf exp-check [--level N]\n" +
            "      经验与升级(证据 E-111):读升级经验表、技能点表、三张经验倍率表、主角 .chr 的成长块与全部副本的经验权重,检查结构(递增、读得到、一级属性与老路径一致),\n" +
            "      再把升级循环从 1 级走到封顶;--level N 另外打印这一级的属性、升级所需经验与同级怪物的击杀经验(数值来自本机的数据,只打印在终端,不要抄进入库的文件)。\n" +
            "      combat-check、dungeon-check、flow-check、sound-check 默认带主角的等级进度(杀怪给经验、升级、底栏有经验条):--level N 指定起始等级,--no-exp 关掉(--no-damage 时本来就没有)。\n" +
            "      dungeon-check、flow-check、sound-check 里主角死了会开复活窗口(证据 E-115:躺平后 10 秒失败计时,攻击键用币复活,计时走完副本失败、流程回城):--coins N 指定复活币的起始库存(默认 5,占位),\n" +
            "      --monster-damage-scale N(测试旋钮,不是原版规则)让怪物打中主角的伤害乘上 N,用来无人值守地检查死亡、复活与失败回城。\n" +
            "  dnf difficulty-check [--dungeon <副本定义>] [--seed N] | --cases <用例文件>\n" +
            "      副本难度(证据 E-125):读难度加成表、全部副本自带的 [monster difficulty bonus] 与难度 3 清单,检查结构;再把 --dungeon 副本以难度 0 到 4 各造一遍,比较第一个房间里每只怪物的最大生命只升不降\n" +
            "      (数值来自本机的数据,只打印在终端,不要抄进入库的文件)。--cases 读 mob_difficulty_oracle.py --write-cases 的输出,用 C# 重算并与原版函数在模拟器里的结果逐槽比较(不需要数据目录)。dungeon-check、flow-check 的 --difficulty N(0 到 4,不检查通关记录)让整局按这个难度打;flow-check 的 --raise-difficulty 让自动机在选择界面把难度调到当前能选的最高一档。\n" +
            "  dnf breakable-check [--dungeon <副本定义>] [--seed N] [--rooms N] [--pick]\n" +
            "  dnf skill-check [--level N]\n" +
            "      学技能(M35,证据 E-127):读主角职业的技能目录与六个成长类型的白送技能,检查每个技能到最高级都有花费(补齐规则)、前置技能都在且没有环;再对每个成长类型给无限点数从白送状态学到当前等级的上限,\n" +
            "      核对点数守恒、全部退回白送等级后点数完全还回、重置后与新建的一致;再检查本地主角的进度(点数是该级总额、快捷栏与旧的初始技能表排法一致、存档往返不变);最后让自动机策略(SkillPlanner)把点数花光,核对花费守恒、停下时再没有能学的、再跑一遍不再加、快捷栏排满。--level N 指定主角等级(默认 85;数字来自本机的数据,只打印在终端,不要抄进入库的文件)。\n" +
            "  dnf gate-body-check [--trace]   各副本房间的出口与墙的碰撞体挡掉了多少可走区域(只打聚合;--trace 列出被挡大块的房间)\n" +
            "      可破坏物件与物件掉物品(M34,证据 E-126):读物件掉落表(Etc/ItemDropInfo_Object.etc)并建池;全部地图的 [special passive object] 文法(与 RoomMap 读出的项数一致、没读完的值只停在关键字上)\n" +
            "      以及每座副本里 [item] 动作的掉落项下标是否都在 .dgn 的 [special passive object item] 范围内;再在 --dungeon(没有就依次换别的副本)的布局里挑 --rooms 个有可破坏物件的房间,\n" +
            "      用播放器同一条路径造房间会话,把主角摆到每个物件左边挥刀,看打中、打碎、触发与掉出的物品(数值来自本机的数据,只打印在终端,不要抄进入库的文件)。--pick 不做检查,只打印第一座适合\"自动机清房后打翻物件\"的无人值守检查的副本路径(链路检查脚本用)。\n" +
            "  dnf item-check [--level N] [--samples N] [--seed N]\n" +
            "      怪物物品掉落(证据 E-117):整库扫描装备与可叠加物品的脚本头(创建率、稀有度、等级、类型、地上图与图标),建掉落池,按怪物等级 --level 掷 --samples 只怪物\n" +
            "      并汇总各类物品与稀有度的件数(数值与物品名来自本机的数据,只打印在终端,不要抄进入库的文件;通过与否只看结构)。\n" +
            "  dnf bag-shot --png <输出.png> [--level N] [--seed N] [--tab 0..3] [--select N(从 1 数的格子)] [--panel]\n" +
            "      背包窗口(证据 E-118)的一帧:剑魂 --level 级穿满能穿的装备,背包里放些穿不上与能穿的装备和可叠加物品(用户数据,只在本机看,不入库)。\n" +
            "  dnf equip-check [--level N] [--seed N]\n" +
            "      装备栏与穿脱(证据 E-118):整库扫描能掉落的装备,检查类型、可用职业与穿戴规则的结构,剑魂能穿的武器有没有外观图层与每个攻击动作的动画,\n" +
            "      再让一个 --level 级的剑魂把每个部位随机一件穿得上的穿上、全部脱下,核对装备栏、背包与面板加成的账(数值、物品名与件数来自本机的数据,只打印在终端,不要抄进入库的文件;通过与否只看结构)。\n" +
            "  dnf shop-check [--level N]\n" +
            "      NPC 商店(证据 E-119):整库读 itemshop.lst 与每家店的脚本、npc.lst 与每个 NPC 的 [role]、城镇区域里摆着的 NPC、价格倍率表,检查结构(每家店读得出来、商品都认得、NPC 与商店编号互相指回、\n" +
            "      走到商店 NPC 身边总在交互范围内);再对每家店的每件商品演习买一件卖回去、买三件卖三件,核对钱与背包的账,检查商店窗口的光标滚动、无人值守探针与画面\n" +
            "      (商店内容、价格与 NPC 对应来自本机的数据,只打印在终端,不要抄进入库的文件;通过与否只看结构)。\n" +
            "  dnf shop-shot --png <输出.png> [--shop N] [--sell] [--tab 0..3] [--select N(从 1 数的格子)] [--prompt] [--level N] [--seed N]\n" +
            "      商店窗口(证据 E-119)的一帧:--shop 商店编号(默认第一家有商品的),--sell 出售页,--prompt 在光标处按一次回车(可叠加物品弹出数量框)(用户数据,只在本机看,不入库)。\n" +
            "  dnf consumable-check [--level N]\n" +
            "      消耗品(证据 E-120):整库读 stackable.lst 里的脚本,检查回复类物品([hp recovery]、[mp recovery])的字段(持续时间与冷却不为负、图标帧都在图包里、种类认得),按 --level 逐件检查使用规则,\n" +
            "      再让 85 级剑魂对每一种用得了的回复药演习:放进背包自动进快捷栏、压低生命魔法、用一件、每 10 毫秒推进到发完,核对终值与背包的账、冷却登记与冷却中拒绝、冷却走完再用,\n" +
            "      最后快捷栏放满六种做存档往返(数值、物品名与件数来自本机的数据,只打印在终端,不要抄进入库的文件;通过与否只看结构与账)。\n" +
            "  dnf text-check\n" +
            "      汉字文字系统(证据 E-121):整库读每个种类的 *.kor.str 文字表(GB18030),与 .NET 自带编码加独立写的行解析逐条比对,值里不应有解码失败的字符;" +
            "      物品目录里每件物品的名字、说明、背景故事,商店的页名都要查得到;打印每张表解码后文字的 SHA-256 供 Python 参考实现比对" +
            "      (文字与条数是用户自己的数据,只打印在终端,不要抄进入库的文件;通过与否只看比对结果与查得到与否)。\n" +
            "  dnf quest-check\n" +
            "      任务系统(证据 E-123):整库读 n_quest/quest.lst 与每个 .qst 脚本、questparameter.etc,检查分类与类型都认得、这一步支持的任务引用的 NPC、物品、副本、怪物、地图都在各自的列表里、发布任务的 NPC 摆在城镇里;" +
            "      再对每个支持的任务演习完整的生命周期(挑合格的角色、满足前置、接取、逐个触发击杀与通关与拜访与收集事件、未做完不能交、做完能交、算奖励、完成后的状态)" +
            "      (数量、编号与分布来自本机的数据,只打印在终端,不要抄进入库的文件;通过与否只看结构与演习的对错)。\n" +
            "  dnf quest-flow-check [--level N(默认 40)] [--dungeon <副本定义>] [--seed N] [--explore]\n" +
            "      任务系统在副本里的一整圈(证据 E-123):挑一个目标正好是这座副本第一间房里怪物的猎杀任务与一个通关任务,用任务系统接下,让自动机把整座副本打完;" +
            "      击杀、通关事件走玩家实际用的那条路(副本会话的任务钩子),核对计数推进、任务达成、交任务领奖励、提示条与追踪取得到内容" +
            "      (任务与怪物的编号、数量来自本机的数据,只打印在终端,不要抄进入库的文件;通过与否只看这一圈走得通)。\n" +
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
                    case "--cam-y":
                        opts.CameraY = int.TryParse(NextValue(args, ref i), out int cy) && cy >= 0 ? cy : throw new UserError("--cam-y 需要非负整数");
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
                    case "--gate":
                        opts.Gates.Add(NextValue(args, ref i));
                        break;
                    case "--demo-gates":
                        opts.DemoGates = true;
                        break;
                    case "--room":
                        opts.Room = true;
                        break;
                    case "--hero-wait":
                        opts.HeroWait = true;
                        break;
                    case "--monsters":
                        opts.Monsters = NextValue(args, ref i);
                        break;
                    case "--play":
                        opts.Play = NextValue(args, ref i);
                        break;
                    case "--seconds":
                        opts.Seconds = double.TryParse(NextValue(args, ref i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double secs) && secs > 0
                            ? secs : throw new UserError("--seconds 需要正数");
                        opts.SecondsGiven = true;
                        break;
                    case "--shots-at":
                        opts.ShotsAt = NextValue(args, ref i);
                        break;
                    case "--trace":
                        opts.Trace = true;
                        break;
                    case "--watch":
                        opts.Watch = double.TryParse(NextValue(args, ref i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double watchAt) && watchAt >= 0
                            ? watchAt : throw new UserError("--watch 需要非负的秒数");
                        break;
                    case "--dungeon":
                        opts.Dungeon = NextValue(args, ref i);
                        break;
                    case "--bot":
                        opts.Bot = true;
                        break;
                    case "--random-layout":
                        opts.RandomLayout = true;
                        break;
                    case "--layout-only":
                        opts.LayoutOnly = true;
                        break;
                    case "--choices":
                        opts.Choices = NextValue(args, ref i);
                        break;
                    case "--from":
                        opts.FlowFrom = NextValue(args, ref i) is var from && (from is "char" or "town" or "select" or "dungeon") ? from : throw new UserError("--from 需要 char、town、select 或 dungeon");
                        break;
                    case "--worldmap":
                        opts.WorldMap = NextValue(args, ref i);
                        break;
                    case "--legacy-town":
                        opts.LegacyTown = true;
                        break;
                    case "--area":
                        opts.TownArea = NextValue(args, ref i);
                        break;
                    case "--chars":
                        opts.CharCount = int.TryParse(NextValue(args, ref i), out int chars) && chars >= 1 && chars <= 12 ? chars : throw new UserError("--chars 需要 1 到 12 的整数");
                        break;
                    case "--select":
                        opts.SelectIndex = int.TryParse(NextValue(args, ref i), out int selectIndex) && selectIndex >= 1 ? selectIndex : throw new UserError("--select 需要从 1 数起的正整数");
                        break;
                    case "--no-avatars":
                        opts.NoAvatars = true;
                        break;
                    case "--tab":
                        opts.BagTab = int.TryParse(NextValue(args, ref i), out int bagTab) && bagTab >= 0 && bagTab <= 3 ? bagTab : throw new UserError("--tab 需要 0 到 3 的整数");
                        break;
                    case "--panel":
                        opts.BagPanel = true;
                        break;
                    case "--shop":
                        opts.ShopIndex = int.TryParse(NextValue(args, ref i), out int shopIndex) && shopIndex >= 0 ? shopIndex : throw new UserError("--shop 需要非负整数(itemshop.lst 里的商店编号)");
                        break;
                    case "--sell":
                        opts.ShopSell = true;
                        break;
                    case "--prompt":
                        opts.ShopPrompt = true;
                        break;
                    case "--hud":
                        opts.TownHudOn = true;
                        break;
                    case "--npc-rects":
                        opts.NpcRects = true;
                        break;
                    case "--town":
                        opts.TownId = int.TryParse(NextValue(args, ref i), out int townId) && townId >= 0 ? townId : throw new UserError("--town 需要非负整数");
                        break;
                    case "--hero-id":
                        opts.HeroId = int.TryParse(NextValue(args, ref i), out int heroId) ? heroId : throw new UserError("--hero-id 需要整数");
                        break;
                    case "--scan":
                        opts.Scan = NextValue(args, ref i);
                        break;
                    case "--expect":
                        opts.Expect = NextValue(args, ref i);
                        break;
                    case "--probe":
                        opts.Probe = NextValue(args, ref i);
                        break;
                    case "--explore":
                        opts.Explore = true;
                        opts.Bot = true;
                        break;
                    case "--no-damage":
                        opts.NoDamage = true;
                        break;
                    case "--no-exp":
                        opts.NoExp = true;
                        break;
                    case "--damage-scale":
                        opts.DamageScale = int.TryParse(NextValue(args, ref i), out int scaleValue) && scaleValue >= 1 && scaleValue <= 100000
                            ? scaleValue
                            : throw new UserError("--damage-scale 需要 1 到 100000 的整数");
                        break;
                    case "--coins":
                        opts.Coins = int.TryParse(NextValue(args, ref i), out int coinsValue) && coinsValue >= 0 && coinsValue <= 99
                            ? coinsValue
                            : throw new UserError("--coins 需要 0 到 99 的整数");
                        break;
                    case "--monster-damage-scale":
                        opts.MonsterDamageScale = int.TryParse(NextValue(args, ref i), out int monsterScaleValue) && monsterScaleValue >= 1 && monsterScaleValue <= 100000
                            ? monsterScaleValue
                            : throw new UserError("--monster-damage-scale 需要 1 到 100000 的整数");
                        break;
                    case "--difficulty":
                        opts.Difficulty = int.TryParse(NextValue(args, ref i), out int difficultyValue) && difficultyValue >= 0 && difficultyValue <= 4
                            ? difficultyValue
                            : throw new UserError("--difficulty 需要 0 到 4 的整数");
                        break;
                    case "--raise-difficulty":
                        opts.RaiseDifficulty = true;
                        break;
                    case "--smash":
                        opts.Smash = true;
                        break;
                    case "--learn":
                        opts.LearnSkills = true;
                        break;
                    case "--pick":
                        opts.Pick = true;
                        break;
                    case "--level":
                        opts.HeroLevel = int.TryParse(NextValue(args, ref i), out int levelValue) && levelValue >= 1 && levelValue <= 85
                            ? levelValue
                            : throw new UserError("--level 需要 1 到 85 的整数");
                        break;
                    case "--seed":
                        opts.Seed = int.TryParse(NextValue(args, ref i), out int seedValue) ? seedValue : throw new UserError("--seed 需要整数");
                        break;
                    case "--frame-ms":
                        opts.FrameMs = double.TryParse(NextValue(args, ref i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double frameMs) && frameMs > 0
                            ? frameMs
                            : throw new UserError("--frame-ms 需要大于 0 的毫秒数");
                        break;
                    case "--jitter":
                        opts.Jitter = double.TryParse(NextValue(args, ref i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double jitter) && jitter >= 0 && jitter <= 0.9
                            ? jitter
                            : throw new UserError("--jitter 需要 0 到 0.9 之间的小数");
                        break;
                    case "--after-clear":
                        opts.AfterClearSeconds = double.TryParse(NextValue(args, ref i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double afterClear) && afterClear >= 0
                            ? afterClear
                            : throw new UserError("--after-clear 需要不小于 0 的秒数");
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
                    case "--motion":
                        opts.Motion = NextValue(args, ref i) is var mo2 && HeroFind.IsMotionSpec(mo2) ? mo2 : throw new UserError("--motion 需要 rest、move、dashattack、attack<N>(连招第 N 段,从 0 数)、etc<N>([etc motion] 第 N 项)或 ani:<PVF 动画路径>[#帧号]");
                        break;
                    case "--search":
                        opts.Search = int.TryParse(NextValue(args, ref i), out int sr) && sr >= 0 && sr <= 80 ? sr : throw new UserError("--search 需要 0 到 80 的整数");
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
                    case "--rooms":
                        opts.Rooms = int.TryParse(NextValue(args, ref i), out int rooms) && rooms >= 1 && rooms <= 200 ? rooms : throw new UserError("--rooms 需要 1 到 200 的整数");
                        break;
                    case "--img":
                        opts.Img = NextValue(args, ref i);
                        break;
                    case "--frame":
                        opts.Frame = int.TryParse(NextValue(args, ref i), out int fr) && fr >= 0 ? fr : throw new UserError("--frame 需要非负整数");
                        break;
                    case "--scan-min":
                        opts.ScanMinPercent = int.TryParse(NextValue(args, ref i), out int smin) && smin >= 0 ? smin : throw new UserError("--scan-min 需要非负整数");
                        break;
                    case "--scan-top":
                        opts.ScanTop = int.TryParse(NextValue(args, ref i), out int stop) && stop > 0 ? stop : throw new UserError("--scan-top 需要正整数");
                        break;
                    case "--frame-to":
                        opts.FrameTo = int.TryParse(NextValue(args, ref i), out int frTo) && frTo >= 0 ? frTo : throw new UserError("--frame-to 需要非负整数");
                        break;
                    case "--pred":
                        opts.Pred = NextValue(args, ref i);
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
                case "town-shot":
                    return TownShot.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "charselect-shot":
                    return CharSelectShot.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "fit-map":
                    return MapFit.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "fit-sprite":
                    return MapFit.RunSprite(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "hero-check":
                    return HeroCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "hero-find":
                    return HeroFind.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "room-check":
                    return RoomCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "combat-check":
                    return CombatCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "dungeon-check":
                    return DungeonCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "flow-check":
                    return FlowCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "arrival-check":
                    return ArrivalCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "town-check":
                    return TownCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
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
                case "sound-check":
                    return SoundCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "hero-damage":
                    return HeroDamageCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "exp-check":
                    return ExpCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "item-check":
                    return ItemCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "equip-check":
                    return EquipCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "bag-shot":
                    return BagShot.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "shop-check":
                    return ShopCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "shop-shot":
                    return ShopShot.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "consumable-check":
                    return ConsumableCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "text-check":
                    return TextCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "quest-check":
                    return QuestCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "quest-flow-check":
                    return QuestFlowCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "breakable-check":
                    return BreakableCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "skill-check":
                    return SkillCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "gate-body-check":
                    return GateBodyCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
                case "difficulty-check":
                    return opts.CasesFile != null
                        ? DifficultyCheck.RunCases(opts)
                        : DifficultyCheck.Run(Locator.Resolve(opts.DnfDir, opts.OutDir), opts);
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
