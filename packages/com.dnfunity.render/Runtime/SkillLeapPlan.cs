using System;

namespace Dnf.Render
{
    /// <summary>
    /// 脚本前冲的参数(客户端状态 0x2d 的模式 1,"姿势 → 跳劈"一类技能的第二段;读码与地址见 Play 包的 SkillLeapSpec):
    /// 水平距离 <see cref="Distance"/>、最大高度 <see cref="Height"/>(像素),以及第 <see cref="LandFrame"/> 帧开头落地。
    /// 位移完全由动画时间脚本化(不走跳跃物理):落地时刻之前每一步 水平 = 起点 ± 整数线性插值(0, 距离, 已过, 落地时刻)(横轴走带碰撞的设位置,见 <see cref="TownHero"/>),
    /// 高度 = 前半段缓出到最大高度、后半段缓入回 0,两个半段共用同一个范围;落地那一步起高度归 0、不再摆位置——
    /// 落地块(0x00cbd5be 起)不写水平位置,所以水平停在落地前最后一步算出的位置,不补满距离。
    /// 四个静态函数是客户端的取整规则(逐个照读码复刻);插值与缓动沿用 <see cref="SkillHopPlan"/> 里的两个(0x011b98c0、0x011b9ef0)。
    /// </summary>
    public sealed class SkillLeapPlan
    {
        public SkillLeapPlan(int distance, int height, int landFrame)
        {
            if (landFrame < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(landFrame), "落地帧要在第 1 帧以后(落在第 0 帧开头就没有空中段)");
            }
            Distance = distance;
            Height = height < 0 ? 0 : height;       // 数据兜底(客户端不夹):负的最大高度会让主角钻到地下
            LandFrame = landFrame;
        }

        /// <summary>水平距离(像素,带符号:为负时朝向的反方向走)。</summary>
        public int Distance { get; }

        /// <summary>最大高度(像素)。</summary>
        public int Height { get; }

        /// <summary>在这一段动画的第几帧(从 0 数)开头落地:空中段 = 前 <see cref="LandFrame"/> 帧的墙钟总时长。</summary>
        public int LandFrame { get; }

        /// <summary>这一步还在空中(要摆位置)吗:已过毫秒严格小于落地时刻(0x00cbd50e 的 JGE:大于等于就走落地块)。</summary>
        public static bool InFlight(long elapsedMs, int landMs)
        {
            return elapsedMs < landMs;
        }

        /// <summary>水平:离起点的距离(像素,不带朝向),夹住的整数线性插值(0, <paramref name="distance"/>, 已过, 落地时刻)(0x011b98c0)。</summary>
        public static int OffsetAt(int distance, long elapsedMs, int landMs)
        {
            return SkillHopPlan.IntLerp(0, distance, Clamp(elapsedMs), landMs);
        }

        /// <summary>高度弧的折半点:落地时刻的有符号折半——先减去符号扩展(负数加 1)再算术右移一位(0x00cbd57b CDQ、0x00cbd57c SUB、0x00cbd57e SAR)。</summary>
        public static int HalfMs(int landMs)
        {
            return (landMs - (landMs >> 31)) >> 1;
        }

        /// <summary>
        /// 高度(像素):已过 &lt; 折半点走缓出 0 → <paramref name="height"/>(范围取折半点),否则走缓入 <paramref name="height"/> → 0(已过 − 折半点,范围仍取折半点);
        /// 两个半段用同一个范围,所以落地时刻是奇数时后半段在落地前 1 毫秒就先到 0(客户端同)。取整规则同 <see cref="SkillHopPlan.EasedLerp"/>(0x011b9ef0)。
        /// </summary>
        public static int HeightAt(int height, long elapsedMs, int landMs)
        {
            int elapsed = Clamp(elapsedMs);
            int half = HalfMs(landMs);
            return elapsed < half
                ? SkillHopPlan.EasedLerp(0, height, elapsed, half, true)
                : SkillHopPlan.EasedLerp(height, 0, elapsed - half, half, false);
        }

        private static int Clamp(long ms)
        {
            return ms > int.MaxValue ? int.MaxValue : ms < int.MinValue ? int.MinValue : (int)ms;
        }
    }
}
