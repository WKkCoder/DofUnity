using System;

// 证据:E-136。服务端的共享公式库 SDCActiveStatusSyncCommon(客户端同样编进去了,持续时间那一个在客户端返回 0 = 时长由服务端算好发来)
// 的函数都读过:GetUniformVelocityFloatSDC 0x088924c1、getActiveStatusPower1/2 0x0808d37a/0x0808d3dc、getActiveStatusEtcParameter 0x0808d43e、
// getActiveStatusDurationServer 0x0808d272、makeEtcParameters 0x0808d50e、getPoisonDamage 0x0808dcc4、getDiseaseDamage 0x0808dd5c、
// getBurnDamage 0x0808de08、getBleedingDamageClient 0x0808de9e、getBleedingDamageServer 0x0808df1a、getUserToUserDamageRate 0x0808df5c、
// isSyncActiveStatus 0x0808d074、getActiveStatusDamageIndex 0x089bcfed(静态读码,非 REA)。浮点按 x87 扩展精度算(X87 类),
// 与服务端机器码在模拟器里逐位对拍(tools/py/active_status_oracle.py,非 REA)。反编译器把这些公式里的整数转换写成 ROUND,
// 实际是 C 的强制转换(FISTP 前临时改成截断):对拍显示一律向零取整。
namespace Dnf.Game
{
    /// <summary>
    /// 一次持续伤害的输入(服务端的 STSDCActiveStatusSyncDataSend 里被这些公式读到的几个字段;字节偏移 +3、+7、+0xb、+0xc、+0x1d)。
    /// 各字段的角色是推断:<see cref="Base"/> 是附加参数第 0 项(一次伤害的数),<see cref="Span"/> 是持续时间(毫秒),
    /// <see cref="ObjectType"/> 为 10 时按死亡之塔取玩家对玩家系数,<see cref="UserToUser"/> 为真时乘该系数,
    /// <see cref="Guard"/> 不小于 100 时毒与病的伤害是 0(推断是"免疫百分数")。
    /// </summary>
    public readonly struct ActiveStatusDamageInput
    {
        public int Base { get; }
        public int Span { get; }
        public byte ObjectType { get; }
        public bool UserToUser { get; }
        public int Guard { get; }

        public ActiveStatusDamageInput(int baseDamage, int span, byte objectType = 0, bool userToUser = false, int guard = 0)
        {
            Base = baseDamage;
            Span = span;
            ObjectType = objectType;
            UserToUser = userToUser;
            Guard = guard;
        }
    }

    public static class ActiveStatusFormulas
    {
        /// <summary>毫秒换算(0x08af4c10 的 float 常量)。</summary>
        public const float SecondsToMs = 1000f;

        /// <summary>持续时间对怪物减半用的除数(0x08af4c14 的 float 常量)。</summary>
        public const float MonsterDurationDivisor = 2f;

        /// <summary>服务端校验出血伤害用的倍数(0x08af4c18 的 float 常量)。</summary>
        public const float BleedingServerRate = 1.5f;

        /// <summary>出血每隔多少毫秒掉一次血(BLEEDING_DAMAGE_TERM,整数常量,0x08af45e0)。</summary>
        public const int BleedingTermMs = 500;

        /// <summary>出血时受击者在行动中的伤害倍数(BLEEDING_DAMAGE_IN_ACTION_RATE,float 常量,0x08af45e4;用法是按名字推断)。</summary>
        public const float BleedingInActionRate = 1.5f;

        /// <summary>持续时间由"威力 1 按等级插值"得出的种类集合(位掩码,0x385aa):冰冻、眩晕、失明、石化、睡眠、武器破坏、混乱、束缚、防具破坏。</summary>
        public const int DurationFromPowerMask = 0x385AA;

        /// <summary>持续时间取表里默认秒数的种类集合(位掩码,0x3a55):减速、中毒、诅咒、感电、灼伤、出血、加速、祝福。</summary>
        public const int DurationFromTableMask = 0x3A55;

        /// <summary>需要和服务端同步伤害的种类集合(isSyncActiveStatus,位掩码 0xac4,只认编号小于 12 的):中毒、感电、石化、灼伤、出血。</summary>
        public const int SyncMask = 0xAC4;

        /// <summary>对象类型码里"怪物"的整套位(0x211:活动对象位 + 怪物位;isObjectType 是"位都在"的判断)。</summary>
        public const int MonsterObjectType = 0x211;

        /// <summary>isSyncActiveStatus:这个种类的持续伤害要和服务端同步。</summary>
        public static bool IsSync(int kind)
        {
            return kind >= 0 && kind < 12 && ((SyncMask >> kind) & 1) != 0;
        }

        /// <summary>getActiveStatusDamageIndex:同步种类在伤害表里的下标(中毒 0、感电 1、石化 2、灼伤 3、出血 4),其余 -1。</summary>
        public static int DamageIndex(int kind)
        {
            switch (kind)
            {
                case 2:
                    return 0;
                case 6:
                    return 1;
                case 7:
                    return 2;
                case 9:
                    return 3;
                case 11:
                    return 4;
                default:
                    return -1;
            }
        }

        /// <summary>
        /// ACTIVESTATUS_SHAKABLE(服务端数据段 0x0936e494 的 18 个字节标志):能靠乱按方向键摇脱的种类。读出来是冰冻(1)、眩晕(3)、石化(7)。
        /// 摇脱的具体规则(每按一次缩短多少)在客户端的受保护区域里,没读到。
        /// </summary>
        public static bool IsShakable(int kind)
        {
            return kind == 1 || kind == 3 || kind == 7;
        }

        /// <summary>GetUniformVelocityFloatSDC(a, b, i, n):i 小于 1 取 a,i 不小于 n 取 b,中间 (b − a) × i / n + a(x87 扩展精度,返回值保持扩展精度)。</summary>
        public static X87Value UniformVelocity(float a, float b, int i, int n)
        {
            if (i < n)
            {
                if (i < 1)
                {
                    return X87.FromSingle(a);
                }
                X87Value diff = X87.Sub(X87.FromSingle(b), X87.FromSingle(a));
                X87Value scaled = X87.Div(X87.Mul(diff, X87.FromInt(i)), X87.FromInt(n));
                return X87.Add(scaled, X87.FromSingle(a));
            }
            return X87.FromSingle(b);
        }

        /// <summary><see cref="UniformVelocity"/> 落到 float(几个调用者把结果转成 float 再用)。</summary>
        public static float UniformVelocitySingle(float a, float b, int i, int n)
        {
            return X87.ToSingle(UniformVelocity(a, b, i, n));
        }

        /// <summary>getActiveStatusPower1:威力 1 按等级(1 到最高等级)在"一级值"与"满级值"之间线性插值,结果落到 float。</summary>
        public static float Power1(ActiveStatusTable table, int kind, int level)
        {
            return UniformVelocitySingle(table.Power1Start(kind), table.Power1End(kind), level - 1, table.MaxLevel - 1);
        }

        /// <summary>getActiveStatusPower2:同 <see cref="Power1"/>,取威力 2 那一对。</summary>
        public static float Power2(ActiveStatusTable table, int kind, int level)
        {
            return UniformVelocitySingle(table.Power2Start(kind), table.Power2End(kind), level - 1, table.MaxLevel - 1);
        }

        /// <summary>getActiveStatusEtcParameter:第 <paramref name="slot"/> 项附加参数按等级插值,结果落到 float。</summary>
        public static float EtcParameter(ActiveStatusTable table, int kind, int level, int slot)
        {
            return UniformVelocitySingle(table.EtcStart(kind, slot), table.EtcEnd(kind, slot), level - 1, table.MaxLevel - 1);
        }

        /// <summary>
        /// getActiveStatusDurationServer 去掉"怪物减半"那一步的部分:按种类的位掩码取持续毫秒数。
        /// 在 <see cref="DurationFromPowerMask"/> 里的:1000 × 威力 1(等级插值),向零取整;在 <see cref="DurationFromTableMask"/> 里的:表里的秒数 × 1000;
        /// 元素(14)与越界的种类是 0。
        /// </summary>
        public static int BaseDurationMs(ActiveStatusTable table, int kind, int level)
        {
            if (kind < 0 || kind >= ActiveStatusTable.KindCount)
            {
                return 0;
            }
            int bit = 1 << kind;
            if ((bit & DurationFromPowerMask) != 0)
            {
                return X87.TruncateToInt(X87.Mul(X87.FromSingle(SecondsToMs), X87.FromSingle(Power1(table, kind, level))));
            }
            if ((bit & DurationFromTableMask) != 0)
            {
                return unchecked(table.DurationSeconds(kind) * 1000);
            }
            return 0;
        }

        /// <summary>
        /// getActiveStatusDurationServer 的全部:在 <see cref="BaseDurationMs"/> 上,受害者的对象类型码带满 <see cref="MonsterObjectType"/> 的位(怪物)时
        /// 减去它的一半(向零取整;<paramref name="ratioArg"/> 是原版第 6 个参数,公式里先除 2 再除自己,为 0 时得非数),负数按 0。
        /// </summary>
        public static int DurationMs(ActiveStatusTable table, int kind, int level, int victimObjectType, int ratioArg)
        {
            int ms = BaseDurationMs(table, kind, level);
            if ((victimObjectType & MonsterObjectType) == MonsterObjectType)
            {
                // 机器码:t = float(ratio ÷ 2);r = float(t ÷ ratio)(正常是 0.5);half = 向零取整(ms × r);ms -= half(32 位回绕)。
                // ratio 为 0 时 r 是非数,FISTP 给出"整数不确定值" int.MinValue,于是 ms 回绕成负数(ms 非负时)再被下面的负数归零吃掉。
                int half;
                if (ratioArg == 0)
                {
                    half = int.MinValue;
                }
                else
                {
                    float t = X87.ToSingle(X87.Div(X87.FromInt(ratioArg), X87.FromSingle(MonsterDurationDivisor)));
                    float r = X87.ToSingle(X87.Div(X87.FromSingle(t), X87.FromInt(ratioArg)));
                    half = X87.TruncateToInt(X87.Mul(X87.FromInt(ms), X87.FromSingle(r)));
                }
                ms = unchecked(ms - half);
            }
            return ms < 0 ? 0 : ms;
        }

        /// <summary>
        /// makeEtcParameters:某种状态按等级生成的附加参数(整数,向零取整)。个数与顺序:减速 2、中毒 1、诅咒 4、失明 2、感电 1、石化 1、
        /// 灼伤 4(第 3、4 项是常量 1 与 3000)、武器破坏 1、出血 1、加速 2、祝福 4、混乱 1(常量 1)、防具破坏 1;冰冻、眩晕、睡眠、束缚、元素没有。
        /// 与 .atk 里 [active status] 后面跟的整数个数一致(<c>AttackInfoLoader.ExtraIntegers</c>)。
        /// </summary>
        public static int[] MakeEtcParameters(ActiveStatusTable table, int kind, int level)
        {
            switch (kind)
            {
                case 0:
                case 5:
                case 12:
                    return new[] { Etc(table, kind, level, 0), Etc(table, kind, level, 1) };
                case 2:
                case 6:
                case 7:
                case 10:
                case 11:
                case 17:
                    return new[] { Etc(table, kind, level, 0) };
                case 4:
                case 13:
                    return new[] { Etc(table, kind, level, 0), Etc(table, kind, level, 1), Etc(table, kind, level, 2), Etc(table, kind, level, 3) };
                case 9:
                    return new[] { Etc(table, kind, level, 0), Etc(table, kind, level, 1), 1, 3000 };
                case 15:
                    return new[] { 1 };
                default:
                    return Array.Empty<int>();
            }
        }

        private static int Etc(ActiveStatusTable table, int kind, int level, int slot)
        {
            return X87.TruncateToInt(X87.FromSingle(EtcParameter(table, kind, level, slot)));
        }

        /// <summary>getUserToUserDamageRate:玩家打玩家时持续伤害的系数(死亡之塔里是另一个数)。</summary>
        public static float UserToUserRate(ActiveStatusTable table, bool deathTower)
        {
            return deathTower ? table.DeathTowerUserToUserDamageRate : table.UserToUserDamageRate;
        }

        /// <summary>getPoisonDamage:Guard 不小于 100 时是 0;否则 UniformVelocity(0, Base, <paramref name="step"/>, Span) 落到 float,玩家对玩家再乘系数(再落 float),向零取整。</summary>
        public static int PoisonDamage(ActiveStatusTable table, ActiveStatusDamageInput data, int step)
        {
            if (data.Guard >= 100)
            {
                return 0;
            }
            float v = UniformVelocitySingle(0f, data.Base, step, data.Span);
            if (data.UserToUser)
            {
                v = X87.ToSingle(X87.Mul(X87.FromSingle(v), X87.FromSingle(UserToUserRate(table, data.ObjectType == 10))));
            }
            return X87.TruncateToInt(X87.FromSingle(v));
        }

        /// <summary>getDiseaseDamage:同 <see cref="PoisonDamage"/>,但中间结果不落 float,玩家对玩家时先取整、再乘系数、再取整。</summary>
        public static int DiseaseDamage(ActiveStatusTable table, ActiveStatusDamageInput data, int step)
        {
            if (data.Guard >= 100)
            {
                return 0;
            }
            int result = X87.TruncateToInt(UniformVelocity(0f, data.Base, step, data.Span));
            if (data.UserToUser)
            {
                // 机器码:先把整数结果落成 float(超过 2^24 会丢低位),再乘系数,乘积直接向零取整(不落 float)。
                result = X87.TruncateToInt(X87.Mul(X87.FromSingle(UserToUserRate(table, data.ObjectType == 10)), X87.FromSingle(result)));
            }
            return result;
        }

        /// <summary>
        /// getBurnDamage:UniformVelocity(0, Base, step, Span) 落到 float,玩家对玩家乘系数(再落 float),向零取整。
        /// (原版还带一个引用出参,Base 为正时把它清成假;调用者先清成假再传,所以对外没有可见效果,这里不移植。)
        /// </summary>
        public static int BurnDamage(ActiveStatusTable table, ActiveStatusDamageInput data, int step)
        {
            float v = UniformVelocitySingle(0f, data.Base, step, data.Span);
            if (data.UserToUser)
            {
                v = X87.ToSingle(X87.Mul(X87.FromSingle(v), X87.FromSingle(UserToUserRate(table, data.ObjectType == 10))));
            }
            return X87.TruncateToInt(X87.FromSingle(v));
        }

        /// <summary>
        /// getBleedingDamageClient:出血每一跳的伤害 = Base ÷ max(1, Span ÷ 500)(Span 是持续毫秒,整数除法;结果向零取整):
        /// 一次出血的总量是 Base,按每 500 毫秒一跳摊开(<see cref="BleedingTermMs"/>)。
        /// </summary>
        public static int BleedingDamagePerTick(ActiveStatusDamageInput data)
        {
            return EvenTickDamage(data, BleedingTermMs);
        }

        /// <summary>
        /// "总量摊开":<see cref="ActiveStatusDamageInput.Base"/> 按每 <paramref name="intervalMs"/> 毫秒一跳摊给整个持续时间,每跳 = Base ÷ max(1, Span ÷ 间隔)
        /// (Span 是持续毫秒,整数除法;商落 float 再向零取整)。间隔取 <see cref="BleedingTermMs"/> 时就是客户端的出血公式 <see cref="BleedingDamagePerTick"/>(对拍过);
        /// 中毒、感电、石化、灼伤的每跳伤害也按这个形状算是推断(客户端的这几个持续伤害对象没读到,服务端只给"允许值",见 <see cref="ServerAllowedDamage"/>)。
        /// </summary>
        public static int EvenTickDamage(ActiveStatusDamageInput data, int intervalMs)
        {
            int ticks = Math.Max(1, intervalMs <= 0 ? 1 : data.Span / intervalMs);
            // 机器码:FILD Base、FILD 跳数、FDIVP,商 FSTP 到 float 再 FISTP 取整:整数直接进 x87(不先落 float),商要落 float。
            float quotient = X87.ToSingle(X87.Div(X87.FromInt(data.Base), X87.FromInt(ticks)));
            return X87.TruncateToInt(X87.FromSingle(quotient));
        }

        /// <summary>
        /// 服务端校验一次持续伤害时认的"允许值"(getActiveStatusDamageDurationForSDC 0x0808df82 的分派:中毒 = <see cref="PoisonDamage"/> 取步数等于时长(即 Base),
        /// 感电与石化 = Base 原样,灼伤 = <see cref="BurnDamage"/> 同样取步数等于时长,出血 = <see cref="BleedingDamageServerLimit"/>;其余种类没有持续伤害,是 0)。
        /// 运行期每一跳的伤害都不能超过它(单元测试与 status-check 在整个参数范围上核对)。
        /// </summary>
        public static int ServerAllowedDamage(ActiveStatusTable table, int kind, ActiveStatusDamageInput data)
        {
            switch (kind)
            {
                case 2:
                    return PoisonDamage(table, data, data.Span);
                case 6:
                case 7:
                    return data.Base;
                case 9:
                    return BurnDamage(table, data, data.Span);
                case 11:
                    return BleedingDamageServerLimit(data);
                default:
                    return 0;
            }
        }

        /// <summary>getBleedingDamageServer:服务端校验出血时的"允许伤害" = 1.5 × Base,向零取整(客户端实际掉的是 <see cref="BleedingDamagePerTick"/>)。</summary>
        public static int BleedingDamageServerLimit(ActiveStatusDamageInput data)
        {
            // 机器码:FILD Base、FSTP float(先落 float),乘 1.5,乘积 FSTP 到 float 再 FISTP 取整。
            float product = X87.ToSingle(X87.Mul(X87.FromSingle(data.Base), X87.FromSingle(BleedingServerRate)));
            return X87.TruncateToInt(X87.FromSingle(product));
        }

        // ------------------------------------------------------------------------------------------------------------
        // 等级差修正(M43 续,E-136):状态等级(攻击信息 [active status] 槽的第二个数)与受害者等级的差,改概率、持续时间与部分附加参数。
        // 出处:客户端受击方挂状态的入口 0x008184e0 → 概率修正 0x008b58d0、时长与附加参数修正 0x008b6f80(静态读码,非 REA;浮点是单精度 SSE,
        // 逐步落 float)。三个比率来自数值表的 [active status level rate for prob/power] 与 [active status level absolute rate]:客户端读表时先把
        // 整数乘 0.01 存成 float(0x0122d630),公式里再乘一次 0.01。这些修正在服务端没有读者(三个比率只在服务端的读表函数里被写),所以是客户端侧的规则。
        // ------------------------------------------------------------------------------------------------------------

        /// <summary>客户端公式里与比率相乘的 0.01(0x01565cc0 的 float 常量);读表时也乘同一个常量。</summary>
        public const float LevelRateScale = 0.01f;

        /// <summary>
        /// 攻击信息里状态概率写成这个数(浮点 1000,客户端 0x0156a6bc 的常量)表示"必中":不做等级差修正,掷骰按百分之百
        /// (0x0081efc0 里这个值直接换成 100)。
        /// </summary>
        public const float CertainProbability = 1000f;

        /// <summary>客户端读表后保存的比率:表里的整数先转 float 再乘 <see cref="LevelRateScale"/>。</summary>
        public static float LevelRate(float tableValue)
        {
            return (float)(int)tableValue * LevelRateScale;
        }

        // 受害者等级低于状态等级:1 + 差 × 比率 × 0.01;否则 1 − 差 × 比率 × 0.01(两个分支的差都取正数)。
        private static float LevelFactor(float rate, int statusLevel, int victimLevel)
        {
            if (victimLevel < statusLevel)
            {
                float up = statusLevel - victimLevel;
                float scaled = up * rate;
                scaled = scaled * LevelRateScale;
                return scaled + 1f;
            }
            float down = victimLevel - statusLevel;
            float cut = down * rate;
            cut = cut * LevelRateScale;
            return 1f - cut;
        }

        // 绝对量项:受害者等级低于状态等级时加 差 × 绝对比率,否则减(带符号返回,加上它就是原版的"加"或"减")。
        private static float LevelAbsolute(float absoluteRate, int statusLevel, int victimLevel)
        {
            if (victimLevel < statusLevel)
            {
                float up = statusLevel - victimLevel;
                return up * absoluteRate;
            }
            float down = victimLevel - statusLevel;
            float cut = down * absoluteRate;
            return -cut;
        }

        /// <summary>
        /// 概率的等级差修正(0x008b58d0 的非决斗分支):<c>(1 ± 差 × 比率 × 0.01) × 概率 ± 差 × 绝对比率</c>,不会小于 0。
        /// 调用方负责"为 0 的状态等级不修正、必中不修正、加速与祝福不修正"(见 <see cref="ActiveStatusSet.Apply"/>)。
        /// </summary>
        public static float LevelAdjustedProbability(ActiveStatusTable table, float probability, int statusLevel, int victimLevel)
        {
            float factor = LevelFactor(LevelRate(table.LevelRateForProb), statusLevel, victimLevel);
            float absolute = LevelAbsolute(LevelRate(table.LevelAbsoluteRate), statusLevel, victimLevel);
            float p = factor * probability;
            p = p + absolute;
            return p > 0f ? p : 0f;
        }

        /// <summary>
        /// 持续时间的等级差修正(0x008b6f80)。减速、失明:<c>(1 ± 差 × 比率 × 0.01) × 时长 ± 差 × 绝对比率</c>(附加参数为空时整段不修正);
        /// 武器破坏、防具破坏:同一个式子;冰冻、眩晕、石化、睡眠、混乱、束缚:只乘系数;其余种类不变。结果向零取整,负数取 0。
        /// <paramref name="extraCount"/> 是这个槽附加参数的个数。
        /// </summary>
        public static int LevelAdjustedDuration(ActiveStatusTable table, int kind, int duration, int statusLevel, int victimLevel, int extraCount)
        {
            float factor = LevelFactor(LevelRate(table.LevelRateForPower), statusLevel, victimLevel);
            float result;
            switch (kind)
            {
                case 0:
                case 5:
                    if (extraCount <= 0)
                    {
                        return duration;
                    }
                    result = factor * duration;
                    result = result + LevelAbsolute(LevelRate(table.LevelAbsoluteRate), statusLevel, victimLevel);
                    break;
                case 10:
                case 17:
                    result = factor * duration;
                    result = result + LevelAbsolute(LevelRate(table.LevelAbsoluteRate), statusLevel, victimLevel);
                    break;
                case 1:
                case 3:
                case 7:
                case 8:
                case 15:
                case 16:
                    result = factor * duration;
                    break;
                default:
                    return duration;
            }
            int truncated = (int)result;
            return truncated < 0 ? 0 : truncated;
        }

        /// <summary>
        /// 附加参数的等级差修正(0x008b6f80):减速与失明的前两项、武器破坏与防具破坏的第一项乘 <c>1 ± 差 × 比率 × 0.01</c>(各自向零取整),
        /// 其余项与其余种类原样。返回新数组(没有要改的返回原数组)。
        /// </summary>
        public static int[] LevelAdjustedExtras(ActiveStatusTable table, int kind, int[] extra, int statusLevel, int victimLevel)
        {
            int count = kind == 0 || kind == 5 ? 2 : (kind == 10 || kind == 17 ? 1 : 0);
            if (count == 0 || extra == null || extra.Length == 0)
            {
                return extra;
            }
            float factor = LevelFactor(LevelRate(table.LevelRateForPower), statusLevel, victimLevel);
            var adjusted = (int[])extra.Clone();
            for (int i = 0; i < count && i < adjusted.Length; i++)
            {
                adjusted[i] = (int)(factor * adjusted[i]);
            }
            return adjusted;
        }
    }
}
