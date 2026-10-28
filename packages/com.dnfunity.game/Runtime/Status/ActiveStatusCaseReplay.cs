using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

// 证据:E-136。用例文件由 tools/py/active_status_oracle.py 生成(期望值是服务端原版函数在 Unicorn 里的输出,全部是合成输入);
// 命令行 `dnf status-check --cases` 与单元测试 ActiveStatusGoldenTests 共用这一份回放。
namespace Dnf.Game
{
    /// <summary>
    /// 把对拍用例文件(TABLE、UV、POW、ETC、DUR、BLEED、SYNC、POISON、DISEASE、BURN 十种行,制表符分隔)逐条用 <see cref="ActiveStatusFormulas"/> 重算,
    /// 与文件里的期望值比较。浮点一律按位模式传(有符号 32 位整数,正负零视为相等);扩展精度的结果写成"奇数尾数 e 二的指数"。
    /// </summary>
    public static class ActiveStatusCaseReplay
    {
        /// <summary>回放的结果:总条数、不一致条数、前几条不一致的说明。</summary>
        public sealed class Result
        {
            public int Cases { get; internal set; }
            public int Bad { get; internal set; }
            public List<string> Samples { get; } = new List<string>();
            public Dictionary<string, int> PerKind { get; } = new Dictionary<string, int>();
        }

        public static Result Replay(IEnumerable<string> lines)
        {
            var result = new Result();
            var tables = new Dictionary<int, ActiveStatusTable>();
            foreach (string line in lines)
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                string[] f = line.Split('\t');
                string kind = f[0];
                if (kind == "TABLE")
                {
                    tables[Int(f[1])] = ActiveStatusTable.Synthetic(Int(f[2]), Ints(f[3]), Floats(f[4]), Floats(f[5]), Floats(f[6]),
                        BitConverter.Int32BitsToSingle(Int(f[7])), BitConverter.Int32BitsToSingle(Int(f[8])));
                    continue;
                }
                string mismatch = Check(kind, f, tables);
                result.Cases++;
                result.PerKind[kind] = result.PerKind.TryGetValue(kind, out int n) ? n + 1 : 1;
                if (mismatch != null)
                {
                    result.Bad++;
                    if (result.Samples.Count < 12)
                    {
                        result.Samples.Add(kind + ":" + mismatch);
                    }
                }
            }
            return result;
        }

        private static string Check(string kind, string[] f, Dictionary<int, ActiveStatusTable> tables)
        {
            switch (kind)
            {
                case "UV":
                {
                    X87Value v = ActiveStatusFormulas.UniformVelocity(Single(f[1]), Single(f[2]), Int(f[3]), Int(f[4]));
                    string got = Normalize(v);
                    return got == f[5] ? null : "输入 " + f[1] + "," + f[2] + "," + f[3] + "," + f[4] + " 期望 " + f[5] + " 实得 " + got;
                }
                case "POW":
                {
                    ActiveStatusTable t = tables[Int(f[1])];
                    float got = Int(f[2]) == 1 ? ActiveStatusFormulas.Power1(t, Int(f[3]), Int(f[4])) : ActiveStatusFormulas.Power2(t, Int(f[3]), Int(f[4]));
                    return SameFloat(got, Int(f[5])) ? null : "种类 " + f[3] + " 等级 " + f[4] + " 期望位 " + f[5] + " 实得 " + BitConverter.SingleToInt32Bits(got);
                }
                case "ETC":
                {
                    ActiveStatusTable t = tables[Int(f[1])];
                    float got = ActiveStatusFormulas.EtcParameter(t, Int(f[2]), Int(f[3]), Int(f[4]));
                    return SameFloat(got, Int(f[5])) ? null : "种类 " + f[2] + " 等级 " + f[3] + " 项 " + f[4] + " 期望位 " + f[5] + " 实得 " + BitConverter.SingleToInt32Bits(got);
                }
                case "DUR":
                {
                    ActiveStatusTable t = tables[Int(f[1])];
                    int got = ActiveStatusFormulas.DurationMs(t, Int(f[2]), Int(f[3]), Int(f[4]), Int(f[5]));
                    return got == Int(f[6]) ? null : "种类 " + f[2] + " 等级 " + f[3] + " 类型 " + f[4] + " 比 " + f[5] + " 期望 " + f[6] + " 实得 " + got;
                }
                case "BLEED":
                {
                    var data = new ActiveStatusDamageInput(Int(f[1]), Int(f[2]));
                    int client = ActiveStatusFormulas.BleedingDamagePerTick(data);
                    int server = ActiveStatusFormulas.BleedingDamageServerLimit(data);
                    return client == Int(f[3]) && server == Int(f[4])
                        ? null
                        : "总量 " + f[1] + " 时长 " + f[2] + " 期望 " + f[3] + "/" + f[4] + " 实得 " + client + "/" + server;
                }
                case "SYNC":
                {
                    int want = Int(f[2]);
                    int got = ActiveStatusFormulas.IsSync(Int(f[1])) ? 1 : 0;
                    return got == want ? null : "种类 " + f[1] + " 期望 " + want + " 实得 " + got;
                }
                case "POISON":
                case "DISEASE":
                case "BURN":
                {
                    ActiveStatusTable t = tables[Int(f[1])];
                    var data = new ActiveStatusDamageInput(Int(f[2]), Int(f[3]), (byte)Int(f[4]), Int(f[5]) != 0, Int(f[6]));
                    int step = Int(f[7]);
                    int got = kind == "POISON"
                        ? ActiveStatusFormulas.PoisonDamage(t, data, step)
                        : kind == "DISEASE" ? ActiveStatusFormulas.DiseaseDamage(t, data, step) : ActiveStatusFormulas.BurnDamage(t, data, step);
                    return got == Int(f[8])
                        ? null
                        : "基础 " + f[2] + " 时长 " + f[3] + " 类型 " + f[4] + " 玩对玩 " + f[5] + " 防 " + f[6] + " 步 " + f[7] + " 期望 " + f[8] + " 实得 " + got;
                }
                default:
                    return "不认识的用例行";
            }
        }

        /// <summary>扩展精度值写成"奇数尾数 e 指数"(零是 "0 e 0");与 active_status_oracle.py 的 norm 同格式。</summary>
        public static string Normalize(X87Value v)
        {
            if (v.IsZero)
            {
                return "0 e 0";
            }
            BigInteger m = v.Mantissa;
            int e = v.Exponent;
            while (m.IsEven)
            {
                m >>= 1;
                e++;
            }
            return m.ToString(CultureInfo.InvariantCulture) + " e " + e.ToString(CultureInfo.InvariantCulture);
        }

        private static bool SameFloat(float got, int expectedBits)
        {
            float want = BitConverter.Int32BitsToSingle(expectedBits);
            return BitConverter.SingleToInt32Bits(got) == expectedBits || got == want;
        }

        private static int Int(string text)
        {
            return int.Parse(text, CultureInfo.InvariantCulture);
        }

        private static float Single(string text)
        {
            return BitConverter.Int32BitsToSingle(Int(text));
        }

        private static int[] Ints(string csv)
        {
            string[] parts = csv.Split(',');
            var result = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                result[i] = Int(parts[i]);
            }
            return result;
        }

        private static float[] Floats(string csv)
        {
            int[] bits = Ints(csv);
            var result = new float[bits.Length];
            for (int i = 0; i < bits.Length; i++)
            {
                result[i] = BitConverter.Int32BitsToSingle(bits[i]);
            }
            return result;
        }
    }
}
