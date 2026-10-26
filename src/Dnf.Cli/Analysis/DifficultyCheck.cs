using System.Globalization;
using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// difficulty-check:副本难度的检查(M33,证据 E-125)。
/// 读真实数据里的难度加成表(monster/monsterapcdifficultybonus.tbl)、全部副本的 [monster difficulty bonus] 与 etc/ultimatedungeonlist.etc,检查结构
/// (表读得到、五档都有按等级的行、副本自带的加成都是 13×5 个数、难度 3 的清单里的编号都在 dungeon.lst 里);
/// 再把 --dungeon 副本以难度 0 到 4 各造一遍,比较第一个房间里每只怪物的最大生命(同一只怪,难度高的不低于难度低的)。
/// 表里的数值是用户自己的数据,只打印在本机的终端上(不要把它们抄进入库的文件);通过与否只看结构与单调性。
/// 难度加成的算法本身用服务端机器码对拍(tools/py/mob_difficulty_oracle.py):
/// <c>--cases 文件</c> 读它的 <c>--write-cases</c> 输出(合成的系数表、合成的用例、原版函数在模拟器里跑出的结果),让 C# 的
/// <see cref="MonsterDifficultyTable.Apply"/> 重算一遍逐槽比较(<see cref="RunCases"/>);不需要数据目录。
/// </summary>
internal static class DifficultyCheck
{
    /// <summary>压力用例里被动过的 25 个槽(与 tools/py/mob_difficulty_oracle.py 的 TOUCHED 同序;其余槽放哨兵值,确认没被碰)。</summary>
    private static readonly int[] Touched =
    {
        1, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x46, 0x48, 0x4C, 0x56, 0x58, 0x5A, 0x5C,
    };

    private const int Sentinel = 0x5A5A5A5A;

    private static int[] ParseInts(string csv)
    {
        string[] parts = csv.Split(',');
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            result[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
        }
        return result;
    }

    private static float[] ParseFloats(string csv)
    {
        int[] bits = ParseInts(csv);
        var result = new float[bits.Length];
        for (int i = 0; i < bits.Length; i++)
        {
            result[i] = BitConverter.Int32BitsToSingle(bits[i]);
        }
        return result;
    }

    /// <summary>
    /// difficulty-check --cases 文件:把 <c>tools/py/mob_difficulty_oracle.py --write-cases</c> 写出的用例(TABLE、ROWS、CASE 三种行,与金标准文件同格式)
    /// 用 C# 的 <see cref="MonsterDifficultyTable.Apply"/> 重算,逐槽与文件里"原版函数在模拟器里的输出"比较;最后一行是 "一致 N,不一致 M"(verify-all 靠它汇总)。
    /// </summary>
    public static int RunCases(Options opts)
    {
        string path = opts.CasesFile ?? throw new UserError("difficulty-check --cases 需要用例文件");
        if (!File.Exists(path))
        {
            throw new UserError("找不到用例文件 " + path + "(先跑 re/venv/bin/python tools/py/mob_difficulty_oracle.py --stress 20000 --write-cases " + path + ")");
        }
        int groups = MonsterDifficultyTable.Groups;
        int levels = MonsterDifficultyTable.Difficulties;
        var globals = new Dictionary<int, float[]>();
        var rowSets = new Dictionary<int, List<float[]>[]>();
        int cases = 0, bad = 0, shown = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            string[] f = line.Split('\t');
            if (f[0] == "TABLE")
            {
                int id = int.Parse(f[1], CultureInfo.InvariantCulture);
                globals[id] = ParseFloats(f[2]);
                var rows = new List<float[]>[levels];
                for (int d = 0; d < levels; d++)
                {
                    rows[d] = new List<float[]>();
                }
                rowSets[id] = rows;
            }
            else if (f[0] == "ROWS")
            {
                int id = int.Parse(f[1], CultureInfo.InvariantCulture);
                int d = int.Parse(f[2], CultureInfo.InvariantCulture);
                int n = int.Parse(f[3], CultureInfo.InvariantCulture);
                if (n > 0)
                {
                    float[] flat = ParseFloats(f[4]);
                    for (int r = 0; r < n; r++)
                    {
                        var row = new float[groups];
                        Array.Copy(flat, r * groups, row, 0, groups);
                        rowSets[id][d].Add(row);
                    }
                }
            }
            else if (f[0] == "CASE")
            {
                int id = int.Parse(f[1], CultureInfo.InvariantCulture);
                int d = int.Parse(f[2], CultureInfo.InvariantCulture);
                int level = int.Parse(f[3], CultureInfo.InvariantCulture);
                DungeonDifficultyBonus? bonus = f[4] == "-" ? null : new DungeonDifficultyBonus(ParseFloats(f[4]));
                int[] input = ParseInts(f[5]);
                int[] expected = ParseInts(f[6]);
                var table = new MonsterDifficultyTable(globals[id], rowSets[id]);
                var stats = new MonsterStats();
                for (int i = 0; i < MonsterStats.Size; i++)
                {
                    stats.Raw[i] = Sentinel + i;
                }
                for (int i = 0; i < Touched.Length; i++)
                {
                    stats.Raw[Touched[i]] = input[i];
                }
                table.Apply(stats, d, level, bonus);
                cases++;
                int wrong = -1;
                for (int i = 0; i < MonsterStats.Size && wrong < 0; i++)
                {
                    int index = Array.IndexOf(Touched, i);
                    int want = index >= 0 ? expected[index] : Sentinel + i;
                    if (stats.Raw[i] != want)
                    {
                        wrong = i;
                    }
                }
                if (wrong >= 0)
                {
                    bad++;
                    if (shown++ < 5)
                    {
                        Console.WriteLine("  不一致:表 " + id + " 难度 " + d + " 等级 " + level + (bonus != null ? " 自带加成" : "") + " 槽 0x" + wrong.ToString("X", CultureInfo.InvariantCulture));
                    }
                }
            }
        }
        Console.WriteLine("C# 重算 " + path + ":一致 " + (cases - bad) + ",不一致 " + bad);
        return cases > 0 && bad == 0 ? 0 : 1;
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        // 1. 难度加成表
        MonsterDifficultyTable table = data.MonsterDifficulty;
        Console.WriteLine("难度加成表 " + MonsterDifficultyTable.Path + ":" + (table.IsLoaded ? "已读到" : "没有读到"));
        Report(table.IsLoaded, "表读得到(" + MonsterDifficultyTable.Difficulties + " 个难度、" + MonsterDifficultyTable.Groups + " 个组)");
        if (table.IsLoaded)
        {
            var rows = new List<string>();
            bool allRows = true;
            for (int d = 0; d < MonsterDifficultyTable.Difficulties; d++)
            {
                int n = table.RowCount(d);
                rows.Add(n.ToString());
                allRows &= n > 0;
            }
            Report(allRows, "每个难度都有按等级的行(行数 " + string.Join("/", rows) + ")");
        }

        // 2. 全部副本里自带的 [monster difficulty bonus]
        var listed = new List<KeyValuePair<int, string>>(WorldMapDef.ParseList(assets.ReadScript(DungeonRoomPool.DungeonListPath), "dungeon/"));
        int withTag = 0, malformed = 0;
        foreach (KeyValuePair<int, string> kv in listed)
        {
            ScriptLine[]? script = assets.ReadScript(kv.Value);
            if (script == null || !script.Any(l => l.TagIs(DungeonDifficultyBonus.Tag)))
            {
                continue;
            }
            withTag++;
            if (DungeonDifficultyBonus.Parse(script) == null)
            {
                malformed++;
            }
        }
        Console.WriteLine("副本定义 " + listed.Count + " 个,自带难度加成的 " + withTag + " 个");
        Report(malformed == 0, "自带的难度加成都是 " + MonsterDifficultyTable.Groups + "×" + MonsterDifficultyTable.Difficulties + " 个数(格式不对 " + malformed + " 个)");

        // 3. 难度 3 的副本清单
        UltimateDungeonList ultimate = data.UltimateDungeons;
        // 清单只有 Contains,没有枚举:用 dungeon.lst 的编号反过来数有几个在清单里,与清单自己的编号个数比
        int inList = listed.Count(kv => ultimate.Contains(kv.Key));
        Console.WriteLine("难度 3 清单 " + UltimateDungeonList.Path + ":" + ultimate.Count + " 个编号,其中 " + inList + " 个在 dungeon.lst 里");
        int unknown = ultimate.Count - inList;
        Report(ultimate.Count > 0 && unknown == 0, "清单里的编号都在 dungeon.lst 里(不在的 " + unknown + " 个)");

        // 4. 同一座副本以各难度造出来,第一个房间里每只怪物的最大生命只升不降
        Console.WriteLine("副本 " + opts.Dungeon + " 第一个房间的怪物(种子 " + opts.Seed + "):");
        List<int>? previous = null;
        bool monotone = true;
        var sums = new List<long>();
        for (int d = 0; d < DungeonSession.DifficultyCount; d++)
        {
            DungeonSession session = DungeonSession.Create(assets, opts.Dungeon, opts.Seed, false, opts.HeroWeapon, heroVitals: false, progress: null, difficulty: d);
            var hp = session.Current.Monsters.Select(m => m.HpMax).ToList();
            sums.Add(hp.Sum(x => (long)x));
            if (previous != null && (previous.Count != hp.Count || previous.Where((v, i) => hp[i] < v).Any()))
            {
                monotone = false;
            }
            previous = hp;
            Console.WriteLine("  难度 " + d + ":怪物 " + hp.Count + " 只,最大生命合计 " + sums[d]);
        }
        Report(sums.Count == DungeonSession.DifficultyCount && sums[0] > 0 && monotone, "每只怪物的最大生命随难度只升不降(合计大于 0)");

        Console.WriteLine(bad == 0 ? "难度检查通过。" : "难度检查失败 " + bad + " 项。");
        return bad == 0 ? 0 : 1;
    }
}
