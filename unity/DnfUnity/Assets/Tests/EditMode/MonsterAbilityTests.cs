using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Dnf.Data;
using Dnf.Game;
using NUnit.Framework;
using UnityEngine;

namespace DnfUnity.Tests
{
    /// <summary>
    /// 怪物数值与 x87 精确浮点模型在 Unity(Mono)里的跨运行时核对(证据 E-062、E-063):
    /// 同一份全合成的金标准(src/Dnf.Game.Tests/Golden,期望值是服务端原版机器码在模拟器里的输出)必须逐槽一致;
    /// x87 模型的几个关键算例在 Mono 的 BigInteger 与 BitConverter 上结果与 .NET 相同。
    /// 完整的合成单元测试在 src/Dnf.Game.Tests(xunit,.NET)。这里不需要游戏数据。
    /// </summary>
    public class MonsterAbilityTests
    {
        private static int PercentOf(int field, int percent)
        {
            // 与 MonsterAbility.ApplyCategories 的乘法同一条路径:数值 / 100 留在扩展精度里,乘 (float)字段,截断
            return X87.TruncateToInt(X87.Mul(X87.Div(X87.FromInt(percent), X87.FromInt(100)), X87.FromSingle((float)field)));
        }

        [TestCase(100, 53, 52)]
        [TestCase(100, 29, 29)]
        [TestCase(1005, 150, 1507)]
        [TestCase(200, 29, 58)]
        [TestCase(1000, 53, 530)]
        public void X87_PercentOf_FollowsTheOriginalRounding(int field, int percent, int expected)
        {
            Assert.AreEqual(expected, PercentOf(field, percent));
        }

        [Test]
        public void X87_FloatRoundingAndTruncation_MatchDotNet()
        {
            Assert.AreEqual(16777216f, X87.ToSingle(X87.FromInt(16777217)));
            Assert.AreEqual(16777220f, X87.ToSingle(X87.FromInt(16777219)));
            Assert.AreEqual(0.3f, X87.ToSingle(X87.Div(X87.FromInt(300), X87.FromInt(1000))));
            Assert.AreEqual(1f / 3f, X87.ToSingle(X87.Div(X87.FromInt(1), X87.FromInt(3))));
            Assert.AreEqual(-0.5f, X87.ToSingle(X87.Div(X87.FromInt(-1), X87.FromInt(2))));
            Assert.AreEqual(int.MinValue, X87.TruncateToInt(X87.Mul(X87.FromInt(int.MaxValue), X87.FromSingle(2f))));
            Assert.AreEqual(1, X87.TruncateToInt(X87.Mul(X87.Div(X87.FromInt(1), X87.FromInt(3)), X87.FromInt(3))));
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

        private static int[] ParseInts(string csv)
        {
            return csv.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
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

        [Test]
        public void MonsterAbility_GoldenCases_MatchTheOriginalFunctionsBitForBit()
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "src", "Dnf.Game.Tests", "Golden", "mob_ability_golden.txt"));
            Assert.IsTrue(File.Exists(path), "缺少金标准文件 " + path);

            var tableRows = new MonsterBaseTable[MonsterBaseTables.TableCount];
            var failures = new List<string>();
            MonsterBaseTables tables = null;
            int cases = 0;
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                string[] f = line.Split('\t');
                if (f[0] == "TABLE")
                {
                    tableRows[int.Parse(f[1], CultureInfo.InvariantCulture)] =
                        MonsterBaseTable.Parse(new[] { new ScriptLine(null, IntValues(ParseInts(f[2]))) });
                    continue;
                }
                if (f[0] != "CASE")
                {
                    continue;
                }
                if (tables == null)
                {
                    tables = new MonsterBaseTables(tableRows);
                }

                int id = int.Parse(f[1], CultureInfo.InvariantCulture);
                var lines = new List<ScriptLine>();
                foreach (string entry in f[5].Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
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
                var def = new MonsterDef(id, "synthetic/case.mob", lines.ToArray());
                MonsterBaseTable own = f[6] == "-"
                    ? null
                    : MonsterBaseTable.Parse(new[] { new ScriptLine(null, IntValues(ParseInts(f[6]))) });
                var kind = (MonsterKind)int.Parse(f[2], CultureInfo.InvariantCulture);
                int level = int.Parse(f[3], CultureInfo.InvariantCulture);
                int dungeon = int.Parse(f[4], CultureInfo.InvariantCulture);
                int[] expected = ParseInts(f[7]);

                MonsterStats got = MonsterAbility.Compute(def, tables, own, level, dungeon, kind);
                cases++;
                for (int i = 0; i < MonsterStats.Size; i++)
                {
                    int g = got.Raw[i];
                    int w = expected[i];
                    if (i == MonsterStats.IxFlag)
                    {
                        g &= 0xFF;
                        w &= 0xFF;
                    }
                    if (g != w)
                    {
                        failures.Add("编号 " + id + " 等级 " + level + " 槽 0x" + i.ToString("X", CultureInfo.InvariantCulture) + " 得到 " + g + " 期望 " + w);
                        break;
                    }
                }
            }
            Assert.GreaterOrEqual(cases, 200, "金标准用例太少");
            Assert.AreEqual(0, failures.Count, failures.Count + " 个用例不一致,例如:" + string.Join("; ", failures.Take(5).ToArray()));
        }
    }
}
