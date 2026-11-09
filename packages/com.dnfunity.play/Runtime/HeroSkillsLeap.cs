using System;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 脚本前冲(客户端状态 0x2d 的模式 1,"姿势 → 跳劈"一类技能的第二段):读码结果(静态读码,地址都是客户端 0x00 开头的虚拟地址)——
    /// 进入块 0x00cd4e1e 起:记下主角此刻的横坐标(0x00cd4e58)、取 [static data] 第 0 个数当水平距离(0x00cd4e68)、第 1 个数当最大高度(0x00cd4e73)、清"已落地"标志(0x00cd4e8d);
    /// 逐帧块 0x00cbd4d2 起(只在模式 1、没落地时):动画此刻已过的毫秒(0x005b7250)与动画第 <see cref="LandFrame"/> 帧开头的时刻(0x01090600,按播放倍率折成墙钟毫秒)比,
    /// 已过 &lt; 第 5 帧开头(比较在 0x00cbd50e,JGE 去落地块)就摆位置:水平 = 起点 ± 整数线性插值(0, 距离, 已过, 第 5 帧开头)(0x00cbd521 调 0x011b98c0;朝向取自虚槽 +0xf8,为真取加),
    /// 落点走带碰撞的 0x00871250 再设横轴(0x00cbd577);高度:折半点 = 第 5 帧开头的有符号折半(CDQ/SUB/SAR,0x00cbd57b–0x00cbd57e),
    /// 前半缓出 0 → 最大高度(0x00cbd58a–0x00cbd5a2)、后半缓入 最大高度 → 0(0x00cbd593–0x00cbd5a2),设竖轴在 0x00cbd5b7;
    /// 已过 ≥ 第 5 帧开头走落地块(0x00cbd5be 起):高度设 0、置已落地标志(0x00cbd64a)、有镜头时震屏(0x00cbd66b)、放脚下特效;落地块不写水平位置,
    /// 所以水平停在落地前最后一步算出的位置——<b>不补满距离</b>(与 <see cref="SkillTravelSpec"/> 那种夹到整段距离的线性位移不同)。
    /// 本类型只放读码得到的引擎常量(静态数据下标、落地帧号),不含 PVF 里的数据值。
    /// </summary>
    public sealed class SkillLeapSpec
    {
        public SkillLeapSpec(int distanceStaticIndex, int heightStaticIndex, int landFrame)
        {
            if (distanceStaticIndex < 0 || heightStaticIndex < 0)
            {
                throw new ArgumentException("距离与高度都要有 [static data] 下标");
            }
            if (landFrame < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(landFrame), "落地帧要在第 1 帧以后(落在第 0 帧开头就没有空中段)");
            }
            DistanceStaticIndex = distanceStaticIndex;
            HeightStaticIndex = heightStaticIndex;
            LandFrame = landFrame;
        }

        /// <summary>水平距离(像素)在 [static data] 里的下标。</summary>
        public int DistanceStaticIndex { get; }

        /// <summary>最大高度(像素)在 [static data] 里的下标。</summary>
        public int HeightStaticIndex { get; }

        /// <summary>在这一段动画的第几帧(从 0 数)开头落地:空中段 = 前 <see cref="LandFrame"/> 帧的总时长。</summary>
        public int LandFrame { get; }
    }

    /// <summary>脚本前冲各条规则的纯函数(不碰主角对象,单测直接喂数);出处见 <see cref="SkillLeapSpec"/>。</summary>
    public static class SkillLeapRules
    {
        /// <summary>落地时刻(墙钟毫秒,从这一段起算):前 <c>LandFrame</c> 帧的动画毫秒之和按播放倍率折算(0x01090600,同 <see cref="SkillBodyHitRules.FrameStartWallMs"/>)。</summary>
        public static int LandMs(int animationMsBeforeLandFrame, double rate)
        {
            return SkillBodyHitRules.FrameStartWallMs(animationMsBeforeLandFrame, rate);
        }

        /// <summary>这一步还在空中(要摆位置)吗:已过毫秒严格小于落地时刻(0x00cbd50e 的 JGE:大于等于就走落地块)。</summary>
        public static bool InFlight(long elapsedMs, int landMs)
        {
            return elapsedMs < landMs;
        }

        /// <summary>水平:离起点的距离(像素,不带方向),夹住的整数线性插值(0, 距离, 已过, 落地时刻)(0x011b98c0)。</summary>
        public static int Offset(int distance, long elapsedMs, int landMs)
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
        public static int Height(int height, long elapsedMs, int landMs)
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

    public sealed partial class SkillStageSpec
    {
        /// <summary>这一段是脚本前冲(<see cref="SkillLeapSpec"/>);没有为 null。</summary>
        public SkillLeapSpec Leap { get; private set; }

        /// <summary>复制一份并让这一段带脚本前冲。</summary>
        public SkillStageSpec WithLeap(SkillLeapSpec leap)
        {
            SkillStageSpec copy = Clone();
            copy.Leap = leap ?? throw new ArgumentNullException(nameof(leap));
            return copy;
        }
    }

    public sealed partial class HeroSkills
    {
        /// <summary>
        /// 这一步脚本前冲(<see cref="SkillLeapSpec"/>)要把主角摆到的横坐标:主角正在放配方技能、当前这一段带前冲、还在空中(已过毫秒 &lt; 落地时刻)时返回 true;
        /// 横坐标 = <paramref name="startX"/>(这一段开头的横坐标)沿朝向 ± 线性插值。落地那一步起返回 false(不补满距离,主角停在最后一次摆的位置)。
        /// 落地时刻 = 这一段动画前 <see cref="SkillLeapSpec.LandFrame"/> 帧的动画毫秒之和按主角当前的播放倍率折成墙钟毫秒。
        /// </summary>
        public bool TryLeapX(TownHero hero, double startX, out double x)
        {
            x = startX;
            if (hero == null || hero.Motion != HeroMotion.Skill || hero.SkillWindingUp)
            {
                return false;
            }
            HeroSkillSlot slot = SlotOfSkill(hero.SkillId);
            if (slot?.Recipe == null || slot.Def == null || hero.SkillStage >= slot.Recipe.Stages.Count)
            {
                return false;
            }
            SkillStageSpec stage = slot.Recipe.Stages[hero.SkillStage];
            SkillLeapSpec leap = stage.Leap;
            if (leap == null)
            {
                return false;
            }
            int animationMs = 0;
            for (int frame = 0; frame < leap.LandFrame; frame++)
            {
                animationMs += hero.EtcFrameDelayMs(stage.MotionIndex, frame);
            }
            int landMs = SkillLeapRules.LandMs(animationMs, hero.AttackAnimationRate);
            if (!SkillLeapRules.InFlight(hero.MotionMs, landMs))
            {
                return false;
            }
            int offset = SkillLeapRules.Offset(slot.Def.GetStatic(leap.DistanceStaticIndex, false), hero.MotionMs, landMs);
            x = startX + (hero.FacingLeft ? -offset : offset);
            return true;
        }
    }
}
