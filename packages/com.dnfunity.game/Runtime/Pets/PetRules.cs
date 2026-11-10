using System;

// 服务端静态读码:CStomach(饱食)GetStomachValue 0x08336cfc、IncrementValue 0x08336e66、SetStomachValue 0x0833eef6;
// CCreature::IsDieCreature 0x08338478、UseFeed 0x083373d4、SetState 0x0833715a、CalculateExp 0x083377b6。
// 下面的数值是服务端代码里的字面量(.rodata 里的 float / double 常量),不是用户数据里的取值。
namespace Dnf.Game
{
    /// <summary>
    /// 宠物状态,按"对饱食的作用"命名(服务端状态枚举里出现的三个值;别的取值在饱食计算里什么也不做,也没有出现在任何调用里)。
    /// </summary>
    public enum PetState
    {
        /// <summary>1:消耗中。装备时用户在战斗里,或战斗开始时从 <see cref="Charging"/> 切过来;饱食随时间下降。</summary>
        Draining = 1,

        /// <summary>2:没装备(新建、读档、卸下后都是它);饱食随时间回升。</summary>
        Stored = 2,

        /// <summary>3:装备着且不在战斗里,或战斗结束时从 <see cref="Draining"/> 切回来;饱食随时间回升。</summary>
        Charging = 3,
    }

    /// <summary>宠物成长与饱食的纯计算与引擎常量。</summary>
    public static class PetRules
    {
        /// <summary>饱食满值(服务端写死 100.0f)。</summary>
        public const float MaxStomach = 100f;

        /// <summary>消耗中每 60 秒掉 1 点(服务端写死 −60.0f 作除数)。</summary>
        public const float DrainSecondsPerPoint = 60f;

        /// <summary>未消耗时每 360 秒回 1 点(服务端写死 360.0f)。</summary>
        public const float ChargeSecondsPerPoint = 360f;

        /// <summary>喂一次加的点数(服务端写死 30.0f;数据里"吃饲料所需"一项与它不同,含义没有读到)。</summary>
        public const float FeedPoints = 30f;

        /// <summary>饱食低于它就是"饿死"状态(严格小于;恰好等于不算)。</summary>
        public const float StarvedBelow = 1f;

        /// <summary>切到 <see cref="PetState.Charging"/> 时复活一只饿死的宠物,饱食被置成这个值。</summary>
        public const float RevivedStomach = 1f;

        /// <summary>选择成长型的饱食恒等于它(不随时间变,也不会饿死)。</summary>
        public const float GrowKindStomach = 100f;

        /// <summary>孵化出来的宠物饱食(服务端孵化后 <c>SetStomach(100)</c>)。</summary>
        public const int HatchStomach = 100;

        /// <summary>新建宠物的等级(服务端 <c>Reset</c> 写 1;脚本里的 <c>[start level]</c> 不参与)。</summary>
        public const int StartLevel = 1;

        /// <summary>每点经验加成按百分数算(服务端除以 100.0f)。</summary>
        public const float ExpPercentDivisor = 100f;

        /// <summary>全局经验表固定读 99 个整数(见 <see cref="PetExpTable"/>)。</summary>
        public const int ExpTableEntries = 99;

        /// <summary>成长型宠物的"每级物品数"写 0 时,服务端换算等级按它。</summary>
        public const int DefaultGrowItemsPerLevel = 25;

        /// <summary>进入消耗中时排死亡检查的延迟 = 饱食 × 它(服务端写死双精度 60.0,取整;延迟的单位没有读到)。</summary>
        public const double DeathCheckDelayFactor = 60.0;

        /// <summary>
        /// 把 <paramref name="elapsedSeconds"/> 秒折算进饱食(服务端 <c>GetStomachValue</c>):
        /// 消耗中 <c>value − elapsed/60</c>,不低于 0;未消耗 <c>value + elapsed/360</c>,不高于 100。
        /// 服务端在 x87 扩展精度里算完再存成 float,这里用 double 算完再转 float。
        /// 时钟倒退(<paramref name="elapsedSeconds"/> 为负)按 0 秒算(服务端没有这道保护,会让饱食反向变化,也可能变成负数)。
        /// </summary>
        public static float Settle(float value, long elapsedSeconds, PetState state)
        {
            if (elapsedSeconds < 0)
            {
                elapsedSeconds = 0;
            }
            if (state == PetState.Draining)
            {
                float drained = (float)((double)elapsedSeconds / -(double)DrainSecondsPerPoint + value);
                return drained < 0f ? 0f : drained;
            }
            if (state == PetState.Stored || state == PetState.Charging)
            {
                float charged = (float)((double)elapsedSeconds / (double)ChargeSecondsPerPoint + value);
                return charged > MaxStomach ? MaxStomach : charged;
            }
            return value;
        }

        /// <summary>饿死状态:饱食严格小于 <see cref="StarvedBelow"/>。</summary>
        public static bool IsStarved(float stomach)
        {
            return stomach < StarvedBelow;
        }

        /// <summary>
        /// 一次加经验实际增加的数量(服务端 <c>CalculateExp</c>):<c>(int)截断(float(量 × 加成 / 100)) + 量</c>,
        /// 加成是三个神器格各自加成的 float 之和。服务端 32 位整数相加会回绕、浮点超出整数范围时得 0x80000000;
        /// 这里取饱和(超出范围的加成按整数上限算),返回 long,由调用的一方再把总经验夹进 int。
        /// </summary>
        public static long ExpGain(int amount, float bonusSum)
        {
            float scaled = (float)((double)amount * bonusSum / ExpPercentDivisor);
            return TruncateSaturating(scaled) + amount;
        }

        /// <summary>float 向零取整,超出 int 范围时饱和,非数得 0。</summary>
        public static long TruncateSaturating(float value)
        {
            if (float.IsNaN(value))
            {
                return 0;
            }
            if (value >= 2147483648f)
            {
                return int.MaxValue;
            }
            if (value <= -2147483648f)
            {
                return int.MinValue;
            }
            return (long)value;
        }

        /// <summary>进入消耗中时排死亡检查的延迟:<c>(int)截断(饱食 × 60.0)</c>(单位未知)。</summary>
        public static int DeathCheckDelay(float stomach)
        {
            return (int)(stomach * DeathCheckDelayFactor);
        }
    }
}
