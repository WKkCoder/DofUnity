using System;
using Dnf.Data;

// 证据:E-136(服务端加载函数 0x08906f88 读 Etc/ActiveStatusTable_ex.etc,静态读码,非 REA;客户端另有一份只有 17 种的旧表,服务端读的是 _ex 这份)。
namespace Dnf.Game
{
    /// <summary>
    /// 异常状态数值表(<c>etc/activestatustable_ex.etc</c>):最高等级、每种状态的默认持续秒数、"威力 1"与"威力 2"(每种状态一对:一级的值、满级的值)、
    /// 每种状态最多四项的"附加参数"(每项也是一对)、几个比率。种类顺序与 <see cref="ActiveStatusKind"/> 相同(一共 18 种)。
    /// 表里的数是用户自己的数据:这里只给结构与读法,文件里没写的标签保持 0(服务端的这些数组是零初始化的全局数组)。
    /// </summary>
    public sealed class ActiveStatusTable
    {
        public const string Path = "etc/activestatustable_ex.etc";

        /// <summary>种类数(<see cref="ActiveStatusKind"/> 里 0 到 17)。</summary>
        public const int KindCount = 18;

        /// <summary>每种状态的附加参数最多几项。</summary>
        public const int EtcSlots = 4;

        public static readonly ActiveStatusTable Empty = new ActiveStatusTable();

        private readonly int[] _duration = new int[KindCount];
        private readonly float[] _power1 = new float[KindCount * 2];
        private readonly float[] _power2 = new float[KindCount * 2];
        private readonly float[] _etc = new float[KindCount * EtcSlots * 2];

        /// <summary>[active status max level]:威力一类的等级插值走到这一级封顶(原版常量 150)。没读到时是 0(插值退成取满级值)。</summary>
        public int MaxLevel { get; private set; }

        /// <summary>
        /// [active status level rate for prob]:文件里的整数(客户端读表时转 float 再乘 0.01,见 <see cref="ActiveStatusFormulas.LevelRate"/>);
        /// 状态等级与受害者等级之差每一级改多少概率(<see cref="ActiveStatusFormulas.LevelAdjustedProbability"/>)。
        /// </summary>
        public float LevelRateForProb { get; private set; }

        /// <summary>[active status level rate for power]:同上,改持续时间与部分附加参数(<see cref="ActiveStatusFormulas.LevelAdjustedDuration"/>)。</summary>
        public float LevelRateForPower { get; private set; }

        /// <summary>[active status level absolute rate]:同上,每一级差加减的绝对量(概率与部分种类的时长)。</summary>
        public float LevelAbsoluteRate { get; private set; }

        /// <summary>[lightning time per number]:感电伤害按"每多少毫秒一次"数(推断)。</summary>
        public int LightningTimePerNumber { get; private set; }

        /// <summary>[active status user to user damage rate]:玩家打玩家时持续伤害乘的系数。</summary>
        public float UserToUserDamageRate { get; private set; }

        /// <summary>[dead tower active status user to user damage rate]:同上,死亡之塔里用。</summary>
        public float DeathTowerUserToUserDamageRate { get; private set; }

        /// <summary>某种状态的默认持续秒数([active status duration] 的一项)。种类越界返回 0。</summary>
        public int DurationSeconds(int kind)
        {
            return kind >= 0 && kind < KindCount ? _duration[kind] : 0;
        }

        /// <summary>威力 1 的一级值。</summary>
        public float Power1Start(int kind)
        {
            return kind >= 0 && kind < KindCount ? _power1[kind * 2] : 0f;
        }

        /// <summary>威力 1 的满级值。</summary>
        public float Power1End(int kind)
        {
            return kind >= 0 && kind < KindCount ? _power1[kind * 2 + 1] : 0f;
        }

        public float Power2Start(int kind)
        {
            return kind >= 0 && kind < KindCount ? _power2[kind * 2] : 0f;
        }

        public float Power2End(int kind)
        {
            return kind >= 0 && kind < KindCount ? _power2[kind * 2 + 1] : 0f;
        }

        /// <summary>第 <paramref name="slot"/> 项附加参数的一级值(每种状态四项,每项一对)。越界返回 0。</summary>
        public float EtcStart(int kind, int slot)
        {
            return InEtcRange(kind, slot) ? _etc[(kind * EtcSlots + slot) * 2] : 0f;
        }

        /// <summary>第 <paramref name="slot"/> 项附加参数的满级值。</summary>
        public float EtcEnd(int kind, int slot)
        {
            return InEtcRange(kind, slot) ? _etc[(kind * EtcSlots + slot) * 2 + 1] : 0f;
        }

        private static bool InEtcRange(int kind, int slot)
        {
            return kind >= 0 && kind < KindCount && slot >= 0 && slot < EtcSlots;
        }

        /// <summary>
        /// 合成一张表(单元测试与对拍用,不含任何原版数据)。数组按文件里的顺序平铺:<paramref name="power1"/> 与 <paramref name="power2"/> 每种两个数,
        /// <paramref name="etc"/> 每种 8 个数(四项,每项两个);长度不够的部分保持 0,多出来的忽略。
        /// </summary>
        public static ActiveStatusTable Synthetic(int maxLevel, int[] durationSeconds, float[] power1, float[] power2, float[] etc = null,
            float userToUser = 1f, float deathTowerUserToUser = 1f, int lightningTimePerNumber = 0,
            float levelRateForProb = 0f, float levelRateForPower = 0f, float levelAbsoluteRate = 0f)
        {
            var table = new ActiveStatusTable
            {
                MaxLevel = maxLevel,
                UserToUserDamageRate = userToUser,
                DeathTowerUserToUserDamageRate = deathTowerUserToUser,
                LightningTimePerNumber = lightningTimePerNumber,
                LevelRateForProb = levelRateForProb,
                LevelRateForPower = levelRateForPower,
                LevelAbsoluteRate = levelAbsoluteRate,
            };
            Copy(durationSeconds, table._duration);
            Copy(power1, table._power1);
            Copy(power2, table._power2);
            Copy(etc, table._etc);
            return table;
        }

        private static void Copy(int[] from, int[] to)
        {
            if (from != null)
            {
                Array.Copy(from, to, Math.Min(from.Length, to.Length));
            }
        }

        private static void Copy(float[] from, float[] to)
        {
            if (from != null)
            {
                Array.Copy(from, to, Math.Min(from.Length, to.Length));
            }
        }

        /// <summary>
        /// 读表。标签:[active status max level]、[active status duration]、[active status power 1]、[active status power 2]、
        /// [active status etc parameters]、三个 [active status level …]、[lightning time per number]、两个 user to user 比率。
        /// 数值既可以写整数也可以写浮点(原版按浮点读)。<paramref name="lines"/> 为 null 返回 <see cref="Empty"/>。
        /// </summary>
        public static ActiveStatusTable Parse(ScriptLine[] lines)
        {
            if (lines == null)
            {
                return Empty;
            }
            var table = new ActiveStatusTable();
            foreach (ScriptLine line in lines)
            {
                if (line.Tag == null)
                {
                    continue;
                }
                float[] numbers = Numbers(line);
                switch (line.Tag.ToLowerInvariant())
                {
                    case "[active status max level]":
                        table.MaxLevel = numbers.Length > 0 ? (int)numbers[0] : 0;
                        break;
                    case "[active status duration]":
                        for (int i = 0; i < KindCount && i < numbers.Length; i++)
                        {
                            table._duration[i] = (int)numbers[i];
                        }
                        break;
                    case "[active status power 1]":
                        Array.Copy(numbers, table._power1, Math.Min(numbers.Length, table._power1.Length));
                        break;
                    case "[active status power 2]":
                        Array.Copy(numbers, table._power2, Math.Min(numbers.Length, table._power2.Length));
                        break;
                    case "[active status etc parameters]":
                        Array.Copy(numbers, table._etc, Math.Min(numbers.Length, table._etc.Length));
                        break;
                    case "[active status level rate for prob]":
                        table.LevelRateForProb = First(numbers);
                        break;
                    case "[active status level rate for power]":
                        table.LevelRateForPower = First(numbers);
                        break;
                    case "[active status level absolute rate]":
                        table.LevelAbsoluteRate = First(numbers);
                        break;
                    case "[lightning time per number]":
                        table.LightningTimePerNumber = (int)First(numbers);
                        break;
                    case "[active status user to user damage rate]":
                        table.UserToUserDamageRate = First(numbers);
                        break;
                    case "[dead tower active status user to user damage rate]":
                        table.DeathTowerUserToUserDamageRate = First(numbers);
                        break;
                }
            }
            return table;
        }

        private static float First(float[] numbers)
        {
            return numbers.Length > 0 ? numbers[0] : 0f;
        }

        private static float[] Numbers(ScriptLine line)
        {
            var list = new System.Collections.Generic.List<float>(line.Values.Length);
            foreach (ScriptValue v in line.Values)
            {
                if (v.IsNumber)
                {
                    list.Add(v.Int);
                }
                else if (v.Kind == ScriptValueKind.Float)
                {
                    list.Add(v.Float);
                }
            }
            return list.ToArray();
        }
    }
}
