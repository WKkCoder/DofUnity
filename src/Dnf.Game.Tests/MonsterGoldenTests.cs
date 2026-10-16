using System.Globalization;
using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 与服务端原版函数的逐位对拍(证据 E-062):Golden/mob_ability_golden.txt 里是全合成的基础属性表与随机合成怪物,
/// 期望的 188 个数值槽是 setBasicAbilityByLevel + upgradeAbilityByMonsterParameterCategory 的机器码在模拟器里跑出来的
/// (tools/py/mob_ability_oracle.py --golden)。文件不含任何原版数据;换了机器、没有模拟器也能回归。
/// </summary>
public sealed class MonsterGoldenTests
{
    private sealed class Case
    {
        public MonsterDef Def;
        public MonsterBaseTable Own;
        public int Level;
        public int Dungeon;
        public MonsterKind Kind;
        public int[] Expected;
        public string Description;
    }

    private static int[] ParseInts(string csv)
    {
        string[] parts = csv.Split(',');
        var r = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            r[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
        }
        return r;
    }

    private static ScriptValue[] IntValues(int[] ints)
    {
        var r = new ScriptValue[ints.Length];
        for (int i = 0; i < ints.Length; i++)
        {
            r[i] = new ScriptValue(ScriptValueKind.Int, ints[i], null);
        }
        return r;
    }

    private static IEnumerable<string> SplitValues(string text)
    {
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            if (c == ',' && !quoted)
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static Case ParseCase(string[] f)
    {
        int id = int.Parse(f[0], CultureInfo.InvariantCulture);
        var lines = new List<ScriptLine>();
        foreach (string entry in f[4].Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = entry.IndexOf('=');
            var values = new List<ScriptValue>();
            foreach (string token in SplitValues(entry.Substring(eq + 1)))
            {
                values.Add(token.StartsWith("\"", StringComparison.Ordinal)
                    ? new ScriptValue(ScriptValueKind.String, 0, token.Substring(1, token.Length - 2))
                    : new ScriptValue(ScriptValueKind.Int, int.Parse(token, CultureInfo.InvariantCulture), null));
            }
            lines.Add(new ScriptLine(entry.Substring(0, eq), values.ToArray()));
        }
        MonsterBaseTable own = f[5] == "-"
            ? null
            : MonsterBaseTable.Parse(new[] { new ScriptLine(null, IntValues(ParseInts(f[5]))) });
        return new Case
        {
            Def = new MonsterDef(id, "synthetic/case.mob", lines.ToArray()),
            Own = own,
            Kind = (MonsterKind)int.Parse(f[1], CultureInfo.InvariantCulture),
            Level = int.Parse(f[2], CultureInfo.InvariantCulture),
            Dungeon = int.Parse(f[3], CultureInfo.InvariantCulture),
            Expected = ParseInts(f[6]),
            Description = "编号 " + id + " 种类 " + f[1] + " 等级 " + f[2] + " 副本参数 " + f[3],
        };
    }

    [Fact]
    public void MatchesTheOriginalFunctionsBitForBitOnSyntheticCases()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Golden", "mob_ability_golden.txt");
        Assert.True(File.Exists(path), "缺少金标准文件 " + path);

        var tableRows = new MonsterBaseTable[MonsterBaseTables.TableCount];
        var cases = new List<Case>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            string[] f = line.Split('\t');
            if (f[0] == "TABLE")
            {
                int[] ints = ParseInts(f[2]);
                tableRows[int.Parse(f[1], CultureInfo.InvariantCulture)] =
                    MonsterBaseTable.Parse(new[] { new ScriptLine(null, IntValues(ints)) });
            }
            else if (f[0] == "CASE")
            {
                cases.Add(ParseCase(f.Skip(1).ToArray()));
            }
        }
        Assert.All(tableRows, t => Assert.NotNull(t));
        Assert.True(cases.Count >= 200, "金标准用例太少:" + cases.Count);
        var tables = new MonsterBaseTables(tableRows);

        var failures = new List<string>();
        int withRow = 0;
        foreach (Case c in cases)
        {
            MonsterStats got = MonsterAbility.Compute(c.Def, tables, c.Own, c.Level, c.Dungeon, c.Kind);
            if (got.Raw[MonsterStats.IxHpBase] != 0)
            {
                withRow++;
            }
            for (int i = 0; i < MonsterStats.Size; i++)
            {
                int g = got.Raw[i];
                int w = c.Expected[i];
                if (i == MonsterStats.IxFlag)
                {
                    // 槽 5 是字节字段,其余 3 个字节在原版里未初始化
                    g &= 0xFF;
                    w &= 0xFF;
                }
                if (g != w)
                {
                    failures.Add(c.Description + " 槽 0x" + i.ToString("X", CultureInfo.InvariantCulture) + " 得到 " + g + " 期望 " + w);
                    break;
                }
            }
        }
        Assert.True(failures.Count == 0, failures.Count + " 个用例不一致,例如:" + string.Join("; ", failures.Take(5)));
        Assert.True(withRow > cases.Count / 2, "太多用例没有命中表行,金标准覆盖不足");
    }
}
