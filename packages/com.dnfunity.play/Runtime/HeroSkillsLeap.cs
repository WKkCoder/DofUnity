using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 脚本前冲(客户端状态 0x2d 的模式 1,"姿势 → 跳劈"一类技能的第二段)的配方规格。读码结果(静态读码,地址都是客户端 0x00 开头的虚拟地址)——
    /// 进入块 0x00cd4e1e 起:记下主角此刻的横坐标(0x00cd4e58)、取 [static data] 第 0 个数当水平距离(0x00cd4e68)、第 1 个数当最大高度(0x00cd4e73)、清"已落地"标志(0x00cd4e8d);
    /// 逐帧块 0x00cbd4d2 起(只在模式 1、没落地时):动画此刻已过的毫秒(0x005b7250)与动画第 <see cref="LandFrame"/> 帧开头的时刻(0x01090600,按播放倍率折成墙钟毫秒)比,
    /// 已过 &lt; 第 5 帧开头(比较在 0x00cbd50e,JGE 去落地块)就摆位置:水平 = 起点 ± 整数线性插值(0, 距离, 已过, 第 5 帧开头)(0x00cbd521 调 0x011b98c0;朝向取自虚槽 +0xf8,为真取加),
    /// 落点走带碰撞的 0x00871250 再设横轴(0x00cbd577);高度:折半点 = 第 5 帧开头的有符号折半(CDQ/SUB/SAR,0x00cbd57b–0x00cbd57e),
    /// 前半缓出 0 → 最大高度(0x00cbd58a–0x00cbd5a2)、后半缓入 最大高度 → 0(0x00cbd593–0x00cbd5a2),设竖轴在 0x00cbd5b7;
    /// 已过 ≥ 第 5 帧开头走落地块(0x00cbd5be 起):高度设 0、置已落地标志(0x00cbd64a)、放落地的事(<see cref="SkillLeapLanding"/>);落地块不写水平位置,
    /// 所以水平停在落地前最后一步算出的位置——<b>不补满距离</b>(与 <see cref="SkillTravelSpec"/> 那种夹到整段距离的线性位移不同)。
    /// 位置与高度的逐步计算在渲染包的 <see cref="SkillLeapPlan"/>(主角的高度只有 <see cref="TownHero"/> 自己能改)。
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

        /// <summary>落地块里除了位置以外办的事(震屏、声音、特效);没有为 null。</summary>
        public SkillLeapLanding Landing { get; private set; }

        /// <summary>复制一份并带上落地块的事。</summary>
        public SkillLeapSpec WithLanding(SkillLeapLanding landing)
        {
            var copy = (SkillLeapSpec)MemberwiseClone();
            copy.Landing = landing ?? throw new ArgumentNullException(nameof(landing));
            return copy;
        }
    }

    /// <summary>
    /// 前冲落地那一步办的事(状态 0x2d 的落地块 0x00cbd5be 起,基础职业那一支):
    /// 先放两个声音(虚槽 +0x154 按名字放,0x00cbd5f0 与 0x00cbd624;名字是客户端写死的加密常量,解码地址见配方),
    /// 有镜头时震屏(0x008e9950,幅度 <see cref="ShakeStrength"/>、时长 <see cref="ShakeMs"/> 毫秒,0x00cbd66b),
    /// 再往主角的动画图层里放预载特效(0x00690210/0x006902a0,<see cref="SkillLandingEffect"/>);
    /// 落地之后每一步(0x00cbda30 起)看"盯着的那个特效"是不是已经播到 <see cref="SkillEffectTrim.Frame"/> 帧以后,是就把那几个特效一起撤掉。
    /// 另有脚下的粒子(.ptl)与第 5 帧起放的被动对象:粒子没有渲染设施,被动对象走配方的 <see cref="SkillObjectSpec"/>(按帧放,不在落地回调里)。
    /// </summary>
    public sealed class SkillLeapLanding
    {
        public SkillLeapLanding(int shakeStrength, int shakeMs, IReadOnlyList<string> sounds, IReadOnlyList<SkillLandingEffect> effects, SkillEffectTrim trim = null)
        {
            if (shakeStrength < 0 || shakeMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(shakeStrength), "震屏的幅度与时长不能为负");
            }
            ShakeStrength = shakeStrength;
            ShakeMs = shakeMs;
            Sounds = sounds ?? Array.Empty<string>();
            Effects = effects ?? Array.Empty<SkillLandingEffect>();
            Trim = trim;
        }

        /// <summary>震屏幅度(像素);0 表示不震。</summary>
        public int ShakeStrength { get; }

        /// <summary>震屏时长(毫秒)。</summary>
        public int ShakeMs { get; }

        /// <summary>落地时依次放的声音标签(客户端按名字放;空串不放)。</summary>
        public IReadOnlyList<string> Sounds { get; }

        /// <summary>落地时放的预载特效。</summary>
        public IReadOnlyList<SkillLandingEffect> Effects { get; }

        /// <summary>落地之后撤特效的规则;没有为 null。</summary>
        public SkillEffectTrim Trim { get; }
    }

    /// <summary>
    /// 落地时放的一个预载特效:预载表下标 <see cref="Index"/>、沿朝向向前的偏移 <see cref="Forward"/> 与向下的偏移 <see cref="Down"/>(像素)。
    /// 客户端把它挂进主角的动画图层(参数 x、y 偏移写进图层记录,0x007fee50),偏移为 0 的随主角走,带偏移的这里摆在落地点(落地后主角原地不动,两者没有差别)。
    /// 偏移的两个轴按"动画图像坐标:x 随朝向翻转、y 向下为正"取(图层记录的消费者没有逐条读到,10 像素量级的取法差别不影响位置判断)。
    /// </summary>
    public sealed class SkillLandingEffect
    {
        public SkillLandingEffect(int index, int forward = 0, int down = 0)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "预载表下标不能为负");
            }
            Index = index;
            Forward = forward;
            Down = down;
        }

        public int Index { get; }

        public int Forward { get; }

        public int Down { get; }

        /// <summary>偏移为 0:跟着主角走;否则摆在落地点不动。</summary>
        public bool Follows => Forward == 0 && Down == 0;
    }

    /// <summary>
    /// 落地之后撤特效的规则(0x00cbda30 起,每一步):预载表里 <see cref="WatchIndex"/> 号特效的动画播到第 <see cref="Frame"/> 帧(含)以后,
    /// 把 <see cref="RemoveIndices"/> 里的特效(含被盯着的那个)一起撤掉。动画不足 <see cref="Frame"/> + 1 帧的永远播不到,不撤(客户端同:帧号永远小于 5)。
    /// </summary>
    public sealed class SkillEffectTrim
    {
        public SkillEffectTrim(int watchIndex, int frame, IReadOnlyList<int> removeIndices)
        {
            if (watchIndex < 0 || frame < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(frame), "盯着的特效下标要非负、帧号要在第 1 帧以后");
            }
            WatchIndex = watchIndex;
            Frame = frame;
            RemoveIndices = removeIndices ?? Array.Empty<int>();
        }

        public int WatchIndex { get; }

        public int Frame { get; }

        public IReadOnlyList<int> RemoveIndices { get; }
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

        /// <summary>这一段一开始(进入块)放的声音标签,依次放;空串不放。没有为空表。</summary>
        public IReadOnlyList<string> StartSounds { get; private set; } = Array.Empty<string>();

        /// <summary>复制一份并让这一段开始时放这些声音。</summary>
        public SkillStageSpec WithStartSounds(params string[] sounds)
        {
            SkillStageSpec copy = Clone();
            copy.StartSounds = sounds ?? throw new ArgumentNullException(nameof(sounds));
            return copy;
        }
    }

    public sealed partial class HeroSkills
    {
        /// <summary>
        /// 这一段的脚本前冲计划:水平距离、最大高度取 [static data] 的下标(<see cref="SkillLeapSpec"/>);这一段不是前冲段、读不到技能定义时返回 null。
        /// </summary>
        internal static SkillLeapPlan LeapPlanOf(HeroSkillSlot slot, SkillStageSpec spec)
        {
            SkillLeapSpec leap = spec.Leap;
            if (leap == null || slot.Def == null)
            {
                return null;
            }
            SkillDef def = slot.Def;
            return new SkillLeapPlan(def.GetStatic(leap.DistanceStaticIndex, false), def.GetStatic(leap.HeightStaticIndex, false), leap.LandFrame);
        }
    }
}
