using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>
    /// 怪物 .mob 的 [attack kind] 里的一条记录(证据 E-100):由 12 个数组成,第 i 条对应第 i 个 [attack motion]。
    /// 各项的位置取自客户端加载这个标签的函数(把 12 个数依次写进记录:三个浮点、一个整数、八个整数),
    /// 含义取自"出招决策"一族函数(每隔一个 [attack delay] 抽一次签的那个函数、"这一招现在能不能用"、"目标在不在这一招的射程里")对各项的读法;
    /// 下面的命名是我们自己起的。数值是原版数据,不进仓库。
    /// <para>
    /// 数的顺序:
    /// t0 站得到射程里时的出招概率(百分数);t1 不论远近的出招概率(百分数);t2 对非角色对象(木桩一类)的出招概率(百分数);t3 初始"可用"标志;
    /// t4 正面射程;t5 左侧死区;t6 背面射程;t7 右侧死区;t8 朝 −y 的射程;t9 −y 侧死区;t10 朝 +y 的射程;t11 +y 侧死区。
    /// 横向:朝向左时正面是 −x、背面是 +x,朝向右时相反;两个死区是绝对的左右(不随朝向镜像)。
    /// </para>
    /// </summary>
    public readonly struct AttackKindRecord
    {
        /// <summary>一条记录由多少个数组成。</summary>
        public const int TokensPerRecord = 12;

        /// <summary>t0:目标在射程里时,每次抽签出这一招的概率(百分数;客户端读成浮点)。</summary>
        public float ChanceInRange { get; }

        /// <summary>t1:不论目标远近,抽签时出这一招的概率(百分数;所有目标都没通过射程判断后再掷一次)。</summary>
        public float ChanceAnywhere { get; }

        /// <summary>t2:目标是非角色对象时,射程里出这一招的概率(百分数)。</summary>
        public float ChanceObject { get; }

        /// <summary>
        /// "对倒在地上的目标出招"的概率(百分数)。记录构造时是 0 且 [attack kind] 不写它(单独的 [attack down prob] 标签才会改),
        /// 所以缺省时怪物不会对倒地的目标出招。
        /// </summary>
        public float ChanceVsDown { get; }

        /// <summary>t3:初始"可用"标志(出招决策只在可用的记录里挑)。</summary>
        public bool InitiallyReady { get; }

        public int FrontReach { get; }
        public int LeftDead { get; }
        public int BackReach { get; }
        public int RightDead { get; }
        public int YMinusReach { get; }
        public int YMinusDead { get; }
        public int YPlusReach { get; }
        public int YPlusDead { get; }

        /// <summary>
        /// 射程判断有没有可用的范围:横向的正面与背面射程不全为 0、并且纵向两个方向的射程不全为 0
        /// (客户端的判断函数在这里直接回答"不在射程里")。有的基础攻击写成全 0,这时这一招不会因为"站在射程里"被选中。
        /// </summary>
        public bool HasExtents => (BackReach != 0 || FrontReach != 0) && (YMinusReach != 0 || YPlusReach != 0);

        public AttackKindRecord(float chanceInRange, float chanceAnywhere, float chanceObject, bool initiallyReady,
            int frontReach, int leftDead, int backReach, int rightDead, int yMinusReach, int yMinusDead, int yPlusReach, int yPlusDead,
            float chanceVsDown = 0f)
        {
            ChanceInRange = chanceInRange;
            ChanceAnywhere = chanceAnywhere;
            ChanceObject = chanceObject;
            ChanceVsDown = chanceVsDown;
            InitiallyReady = initiallyReady;
            FrontReach = frontReach;
            LeftDead = leftDead;
            BackReach = backReach;
            RightDead = rightDead;
            YMinusReach = yMinusReach;
            YMinusDead = yMinusDead;
            YPlusReach = yPlusReach;
            YPlusDead = yPlusDead;
        }

        /// <summary>改"对倒地目标出招的概率"后的副本。</summary>
        public AttackKindRecord WithChanceVsDown(float percent)
        {
            return new AttackKindRecord(ChanceInRange, ChanceAnywhere, ChanceObject, InitiallyReady, FrontReach, LeftDead, BackReach, RightDead,
                YMinusReach, YMinusDead, YPlusReach, YPlusDead, percent);
        }

        /// <summary>改"在射程里出招的概率"后的副本(进入敌对状态时客户端把它乘 1.3,见 <see cref="MonsterAttackRules.HostileChanceScale"/>)。</summary>
        public AttackKindRecord WithChanceInRange(float percent)
        {
            return new AttackKindRecord(percent, ChanceAnywhere, ChanceObject, InitiallyReady, FrontReach, LeftDead, BackReach, RightDead,
                YMinusReach, YMinusDead, YPlusReach, YPlusDead, ChanceVsDown);
        }

        /// <summary>
        /// 把 [attack kind] 里依次排列的数切成记录,每 <see cref="TokensPerRecord"/> 个一条;末尾凑不满一条的数丢掉。
        /// 数值取整数或浮点(原版把前三项读成浮点、其余读成整数)。
        /// </summary>
        public static AttackKindRecord[] Parse(IReadOnlyList<double> tokens)
        {
            if (tokens == null || tokens.Count < TokensPerRecord)
            {
                return Array.Empty<AttackKindRecord>();
            }
            int count = tokens.Count / TokensPerRecord;
            var result = new AttackKindRecord[count];
            for (int i = 0; i < count; i++)
            {
                int b = i * TokensPerRecord;
                result[i] = new AttackKindRecord(
                    (float)tokens[b], (float)tokens[b + 1], (float)tokens[b + 2], (int)tokens[b + 3] != 0,
                    (int)tokens[b + 4], (int)tokens[b + 5], (int)tokens[b + 6], (int)tokens[b + 7],
                    (int)tokens[b + 8], (int)tokens[b + 9], (int)tokens[b + 10], (int)tokens[b + 11]);
            }
            return result;
        }

        /// <summary>读一个 .mob 的 [attack kind] 标签;没有这个标签返回空数组。</summary>
        public static AttackKindRecord[] Parse(TagTable tags)
        {
            if (tags == null || !tags.TryGetLine("[attack kind]", out ScriptLine line))
            {
                return Array.Empty<AttackKindRecord>();
            }
            var numbers = new List<double>(line.Values.Length);
            foreach (ScriptValue v in line.Values)
            {
                if (v.IsNumber)
                {
                    numbers.Add(v.Int);
                }
                else if (v.Kind == ScriptValueKind.Float)
                {
                    numbers.Add(v.Float);
                }
            }
            return Parse(numbers);
        }
    }

    /// <summary>
    /// 怪物出招决策里的规则与常数(证据 E-100、E-101):射程判断、抽签、抽签间隔。全是纯函数,取自客户端的出招决策一族函数。
    /// </summary>
    public static class MonsterAttackRules
    {
        /// <summary>怪物出生后这么久之内不做出招决策(毫秒;队伍号不在 0 到 11 之间的对象才有这个限制,怪物属于这一类)。</summary>
        public const int SpawnLockoutMs = 3000;

        /// <summary>进入敌对状态时,每条记录的"射程里出招概率"乘这个数(客户端的状态进入函数)。</summary>
        public const float HostileChanceScale = 1.3f;

        /// <summary>抽签间隔的随机范围占 [attack delay] 的比例:下一次在 delay + [−范围/2, 范围/2) 之后。</summary>
        public const float JitterFraction = 0.2f;

        /// <summary>抽签用的模数:随机数 % 10000 &lt; 概率 × 100 算中。</summary>
        public const int RollModulus = 10000;

        /// <summary>
        /// 怪物每次进入出招状态时放出招语音的概率(百分数):客户端的出招状态进入函数掷一次"随机数 % 100 &lt; 15",中了才放(证据 E-104)。
        /// 重复投掷时每次重新进入出招状态都各掷一次。
        /// </summary>
        public const int AttackVoicePercent = 15;

        /// <summary>
        /// 目标是否落在第 <paramref name="r"/> 条记录描述的射程里(客户端的"目标在这一招射程里"函数,证据 E-101)。
        /// 坐标与尺寸都是整数(客户端取整后的位置、目标的碰撞宽度与进深);目标的半边按宽度、进深各除以 4(向零取整)放宽。
        /// <paramref name="margin"/> 是怪物自己的一个沿朝向方向推移射程窗口的偏移(客户端里缺省视为 0)。
        /// 记录没有可用范围(<see cref="AttackKindRecord.HasExtents"/> 为假)时直接不在射程里。
        /// </summary>
        public static bool IsInRange(in AttackKindRecord r, int selfX, int selfY, bool facingLeft,
            int targetX, int targetY, int targetWidth, int targetDepth, int margin = 0)
        {
            if (!r.HasExtents)
            {
                return false;
            }
            int quarterW = targetWidth / 4;
            if (facingLeft)
            {
                if (selfX + r.BackReach < targetX + margin - quarterW)
                {
                    return false;
                }
                if (targetX + quarterW + margin < selfX - r.FrontReach)
                {
                    return false;
                }
            }
            else
            {
                if (targetX + quarterW - margin < selfX - r.BackReach)
                {
                    return false;
                }
                if (selfX + r.FrontReach < targetX - quarterW - margin)
                {
                    return false;
                }
            }
            if (selfX - r.LeftDead < targetX && targetX < selfX + r.RightDead)
            {
                return false;
            }
            int quarterD = targetDepth / 4;
            if (selfY - r.YMinusReach > quarterD + targetY)
            {
                return false;
            }
            if (targetY - quarterD > selfY + r.YPlusReach)
            {
                return false;
            }
            if (selfY - r.YMinusDead < targetY && targetY < selfY + r.YPlusDead)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// 抽签:随机数对 <see cref="RollModulus"/> 取余,小于 <paramref name="percent"/> × 100 算中(单精度,与客户端一致)。
        /// </summary>
        public static bool Roll(int random, float percent)
        {
            return (float)(random % RollModulus) < percent * 100.0f;
        }

        /// <summary>出招语音的抽签:随机数对 100 取余小于 <paramref name="percent"/> 算中(证据 E-104)。</summary>
        public static bool RollAttackVoice(int random, int percent = AttackVoicePercent)
        {
            return random % 100 < percent;
        }

        /// <summary>
        /// 下一次出招决策的时刻:现在 + 随机抖动 + [attack delay];抖动 = 随机数 % 范围 − 范围 / 2,范围 = (int)(delay × 0.2)(客户端写法,范围为 0 时不抖动)。
        /// </summary>
        public static long NextDecisionMs(long nowMs, int delayMs, int random)
        {
            int span = (int)((float)delayMs * JitterFraction);
            int jitter = span > 0 ? random % span - span / 2 : 0;
            return nowMs + jitter + delayMs;
        }
    }
}
