using System;
using System.Collections.Generic;
using Dnf.Render;

// 角色技能里"身体命中 / 升空双窗 / 两段式"这一族状态(客户端状态 0x3c、0x3e、0x3f)比单段配方多出来的几条规则的数据与纯函数。
// 配方(SkillRecipe)只带一个可选的 SkillBodyHitSpec;没带的配方所有规则都不生效,行为与以前逐位一致。
// 表里的数都是客户端状态码里写死的常量(段号、[static data] 下标、帧号),不含 PVF 里的数据值。
namespace Dnf.Play
{
    /// <summary>接段时认哪些键的"刚按下"(可以组合,任何一个满足就算)。</summary>
    [Flags]
    public enum SkillChainKeys
    {
        None = 0,

        /// <summary>这个技能自己的快捷键;没放进快捷栏、只靠指令放的技能没有这一项(客户端先在快捷栏管理器里查技能的位置,查不到就不算,0x00cb1ae7)。</summary>
        Own = 1,

        /// <summary>热键表里的"技能"键(默认 Z;主角对象 +0x39c4 的键对象,0x00cc22a0)。</summary>
        SkillKey = 2,

        /// <summary>攻击键(主角对象 +0x388c 的键对象,0x00cb1ad8)。</summary>
        AttackKey = 4,
    }

    /// <summary>动画播完之后状态还要保持多久的两种算法。</summary>
    public enum SkillHoldKind
    {
        /// <summary>动画播完后再等 N 毫秒(进入块把"动画总时长 + N"记下来,守卫等状态计时器到它才结束,0x00cd6f40、0x00cc21e8)。</summary>
        AfterAnimation,

        /// <summary>整段至少持续 N 毫秒:动画比它短就等到它,动画比它长以动画为准(逐帧块按固定毫秒推进、到点才换下一段)。</summary>
        AtLeast,
    }

    /// <summary>
    /// 一条"按键接段"规则:当前段在 <see cref="FromFirstStage"/> 到 <see cref="FromLastStage"/> 之间、动画至少播到第 <see cref="FromFrame"/> 帧、
    /// 按下了 <see cref="Keys"/> 里的键,就换成 <see cref="ToStage"/> 段。带这种规则的配方不再走"再按自己的键接下一段"的旧路径。
    /// </summary>
    public sealed class SkillKeyChain
    {
        public SkillKeyChain(int fromFirstStage, int fromLastStage, int toStage, int fromFrame, SkillChainKeys keys)
        {
            if (fromFirstStage < 0 || fromLastStage < fromFirstStage)
            {
                throw new ArgumentException("接段的起始段范围不对", nameof(fromLastStage));
            }
            if (toStage <= fromLastStage)
            {
                throw new ArgumentException("接段只能往后接", nameof(toStage));
            }
            if (fromFrame < 0)
            {
                throw new ArgumentException("帧号不能为负", nameof(fromFrame));
            }
            if (keys == SkillChainKeys.None)
            {
                throw new ArgumentException("接段至少要认一个键", nameof(keys));
            }
            FromFirstStage = fromFirstStage;
            FromLastStage = fromLastStage;
            ToStage = toStage;
            FromFrame = fromFrame;
            Keys = keys;
        }

        public int FromFirstStage { get; }
        public int FromLastStage { get; }
        public int ToStage { get; }
        public int FromFrame { get; }
        public SkillChainKeys Keys { get; }
    }

    /// <summary>
    /// 定间隔重武装:这一段里命中名单按计时器清空 <see cref="CountStaticIndex"/> 次(取 [static data] 的这个下标),第一次在动画第 <see cref="StartFrame"/> 帧的开头,
    /// 之后每隔 <see cref="IntervalStaticIndex"/> 个数那么多毫秒再清一次(计时器的两个标志都为真:起点那一刻就响,每次响都把"上次"改成此刻,0x0112f290、0x0112f310、0x0112f3d0)。
    /// </summary>
    public sealed class SkillRearmSpec
    {
        public SkillRearmSpec(int stage, int startFrame, int intervalStaticIndex, int countStaticIndex)
        {
            if (stage < 0 || startFrame < 0 || intervalStaticIndex < 0 || countStaticIndex < 0)
            {
                throw new ArgumentException("段号、起始帧与两个下标都不能为负");
            }
            Stage = stage;
            StartFrame = startFrame;
            IntervalStaticIndex = intervalStaticIndex;
            CountStaticIndex = countStaticIndex;
        }

        public int Stage { get; }

        /// <summary>计时器起点在动画的第几帧(0 是段的开头)。</summary>
        public int StartFrame { get; }

        public int IntervalStaticIndex { get; }
        public int CountStaticIndex { get; }
    }

    /// <summary>一段的伤害百分比再乘 [static data] 的第 <see cref="PercentStaticIndex"/> 个数 / 100(0x009138c0)。</summary>
    public sealed class SkillStageScale
    {
        public SkillStageScale(int stage, int percentStaticIndex)
        {
            if (stage < 0 || percentStaticIndex < 0)
            {
                throw new ArgumentException("段号与下标都不能为负");
            }
            Stage = stage;
            PercentStaticIndex = percentStaticIndex;
        }

        public int Stage { get; }
        public int PercentStaticIndex { get; }
    }

    /// <summary>一段播完之后的保持,见 <see cref="SkillHoldKind"/>;毫秒数取 [static data] 的第 <see cref="StaticIndex"/> 个数。</summary>
    public sealed class SkillStageHold
    {
        public SkillStageHold(int stage, SkillHoldKind kind, int staticIndex)
        {
            if (stage < 0 || staticIndex < 0)
            {
                throw new ArgumentException("段号与下标都不能为负");
            }
            Stage = stage;
            Kind = kind;
            StaticIndex = staticIndex;
        }

        public int Stage { get; }
        public SkillHoldKind Kind { get; }
        public int StaticIndex { get; }
    }

    /// <summary>
    /// 关键帧标志起滑:这一段的动画走到带 <see cref="Flag"/> 标志的帧,没按"反方向"就起一段滑行(速度取 [static data] 第 <see cref="SpeedStaticIndex"/> 个数、
    /// 加速度取第 <see cref="AccelStaticIndex"/> 个数,朝向前方,0x00cc6df6 起)。标志无论滑不滑都当次消费。
    /// </summary>
    public sealed class SkillScriptSlide
    {
        public SkillScriptSlide(int stage, int flag, int speedStaticIndex, int accelStaticIndex)
        {
            if (stage < 0 || speedStaticIndex < 0 || accelStaticIndex < 0)
            {
                throw new ArgumentException("段号与下标都不能为负");
            }
            Stage = stage;
            Flag = flag;
            SpeedStaticIndex = speedStaticIndex;
            AccelStaticIndex = accelStaticIndex;
        }

        public int Stage { get; }
        public int Flag { get; }
        public int SpeedStaticIndex { get; }
        public int AccelStaticIndex { get; }
    }

    /// <summary>
    /// 线性位移:这一段里每帧把主角摆在"段起点沿朝向 ± 整数线性插值(0, 距离, 已过毫秒, 总毫秒)"的位置(0x011b98c0;客户端逐帧用带碰撞的位置函数 0x00871250 取落点)。
    /// 距离取 [static data] 第 <see cref="DistanceStaticIndex"/> 个数、总毫秒取第 <see cref="DurationStaticIndex"/> 个数。
    /// </summary>
    public sealed class SkillTravelSpec
    {
        public SkillTravelSpec(int stage, int distanceStaticIndex, int durationStaticIndex)
        {
            if (stage < 0 || distanceStaticIndex < 0 || durationStaticIndex < 0)
            {
                throw new ArgumentException("段号与下标都不能为负");
            }
            Stage = stage;
            DistanceStaticIndex = distanceStaticIndex;
            DurationStaticIndex = durationStaticIndex;
        }

        public int Stage { get; }
        public int DistanceStaticIndex { get; }
        public int DurationStaticIndex { get; }
    }

    /// <summary>
    /// 身体命中一族的额外规则(每条的出处与没做的部分写在配方表里对应技能的注释上)。没有的规则传 null / 空,表示这个配方不用它。
    /// </summary>
    public sealed class SkillBodyHitSpec
    {
        private static readonly SkillStageScale[] NoScales = new SkillStageScale[0];
        private static readonly SkillRearmSpec[] NoRearms = new SkillRearmSpec[0];
        private static readonly SkillKeyChain[] NoChains = new SkillKeyChain[0];
        private static readonly SkillStageHold[] NoHolds = new SkillStageHold[0];
        private static readonly SkillScriptSlide[] NoSlides = new SkillScriptSlide[0];
        private static readonly SkillTravelSpec[] NoTravels = new SkillTravelSpec[0];

        public SkillBodyHitSpec(bool horizontalHeldSlide = false, IEnumerable<SkillStageScale> scales = null, IEnumerable<SkillRearmSpec> rearms = null,
            IEnumerable<SkillKeyChain> chains = null, IEnumerable<SkillStageHold> holds = null, IEnumerable<SkillScriptSlide> scriptSlides = null,
            IEnumerable<SkillTravelSpec> travels = null, int autoChainLastFromStage = -1, bool slideFirstStageOnly = false)
        {
            HorizontalHeldSlide = horizontalHeldSlide;
            SlideFirstStageOnly = slideFirstStageOnly;
            Scales = new List<SkillStageScale>(scales ?? NoScales).AsReadOnly();
            Rearms = new List<SkillRearmSpec>(rearms ?? NoRearms).AsReadOnly();
            Chains = new List<SkillKeyChain>(chains ?? NoChains).AsReadOnly();
            Holds = new List<SkillStageHold>(holds ?? NoHolds).AsReadOnly();
            ScriptSlides = new List<SkillScriptSlide>(scriptSlides ?? NoSlides).AsReadOnly();
            Travels = new List<SkillTravelSpec>(travels ?? NoTravels).AsReadOnly();
            AutoChainLastFromStage = autoChainLastFromStage;
        }

        /// <summary>
        /// 进入一段时的起滑只看横轴按住的方向:没按横轴(只按了上下也一样)取基础的速度与加速度,顺着朝向按只把速度换成配方 <see cref="SkillRecipe.ForwardSpeedIndex"/> 那个数
        /// (加速度仍是基础的),反着按不滑(0x00cd6baf–0x00cd6c14)。false 是旧规则(<see cref="HeroSkills.Slide"/> 的原样)。
        /// </summary>
        public bool HorizontalHeldSlide { get; }

        /// <summary>
        /// 只有第 0 段起滑:后面的段进入块把两个轴的滑行速度都清零(0x00cd6e80 起的第二个模式),按键接段进来时不再按配方的速度滑。false 是每一段都按配方起滑(旧规则)。
        /// </summary>
        public bool SlideFirstStageOnly { get; }

        public IReadOnlyList<SkillStageScale> Scales { get; }
        public IReadOnlyList<SkillRearmSpec> Rearms { get; }
        public IReadOnlyList<SkillKeyChain> Chains { get; }
        public IReadOnlyList<SkillStageHold> Holds { get; }
        public IReadOnlyList<SkillScriptSlide> ScriptSlides { get; }
        public IReadOnlyList<SkillTravelSpec> Travels { get; }

        /// <summary>
        /// 自动接续只从不大于这个段号的段发生;负数表示不设上界(旧规则:每一段播完都接下一段)。设了上界时更后面的段只能靠 <see cref="Chains"/> 到达。
        /// </summary>
        public int AutoChainLastFromStage { get; }

        /// <summary>所有规则里引用的段号都落在 [0, <paramref name="stageCount"/>) 里,否则抛异常(造配方时就发现写错的段号)。</summary>
        internal void CheckStages(int stageCount)
        {
            foreach (SkillStageScale s in Scales) { CheckStage(s.Stage, stageCount); }
            foreach (SkillRearmSpec s in Rearms) { CheckStage(s.Stage, stageCount); }
            foreach (SkillStageHold s in Holds) { CheckStage(s.Stage, stageCount); }
            foreach (SkillScriptSlide s in ScriptSlides) { CheckStage(s.Stage, stageCount); }
            foreach (SkillTravelSpec s in Travels) { CheckStage(s.Stage, stageCount); }
            foreach (SkillKeyChain c in Chains)
            {
                CheckStage(c.FromFirstStage, stageCount);
                CheckStage(c.ToStage, stageCount);
            }
        }

        private static void CheckStage(int stage, int stageCount)
        {
            if (stage < 0 || stage >= stageCount)
            {
                throw new ArgumentException("身体命中规则引用的段号 " + stage + " 超出配方的 " + stageCount + " 段");
            }
        }

        public SkillStageScale ScaleOf(int stage)
        {
            foreach (SkillStageScale s in Scales) { if (s.Stage == stage) { return s; } }
            return null;
        }

        public SkillRearmSpec RearmOf(int stage)
        {
            foreach (SkillRearmSpec s in Rearms) { if (s.Stage == stage) { return s; } }
            return null;
        }

        public SkillStageHold HoldOf(int stage)
        {
            foreach (SkillStageHold s in Holds) { if (s.Stage == stage) { return s; } }
            return null;
        }

        public SkillTravelSpec TravelOf(int stage)
        {
            foreach (SkillTravelSpec s in Travels) { if (s.Stage == stage) { return s; } }
            return null;
        }

        public SkillScriptSlide ScriptSlideAt(int stage, int flag)
        {
            foreach (SkillScriptSlide s in ScriptSlides) { if (s.Stage == stage && s.Flag == flag) { return s; } }
            return null;
        }
    }

    /// <summary>
    /// 定间隔重武装的计时器(客户端那个 0x0112f290 起的小计时器的行为):最多响 <c>count</c> 次;第一次在 <c>startMs</c>,之后每次响都把"上次"改成响的那一刻,
    /// 下一次要等到那一刻再过 <c>intervalMs</c>(不是固定节拍:步长比间隔粗时后面的响会顺延)。
    /// </summary>
    public sealed class SkillRearmClock
    {
        private readonly int _intervalMs;
        private readonly int _count;
        private long _nextMs;
        private int _fired;

        public SkillRearmClock(long startMs, int intervalMs, int count)
        {
            _nextMs = startMs;
            _intervalMs = intervalMs;
            _count = count;
        }

        /// <summary>已经响了几次。</summary>
        public int Fired => _fired;

        /// <summary>按此刻的毫秒数查一次,到点返回 true(并记一次);一次查询最多响一次。</summary>
        public bool Poll(long nowMs)
        {
            if (_fired >= _count || nowMs < _nextMs)
            {
                return false;
            }
            _fired++;
            _nextMs = nowMs + _intervalMs;
            return true;
        }
    }

    /// <summary>身体命中一族各条规则的纯函数(不碰主角对象,单测直接喂数)。</summary>
    public static class SkillBodyHitRules
    {
        /// <summary>
        /// 只看横轴按住方向的起滑(<see cref="SkillBodyHitSpec.HorizontalHeldSlide"/>):<paramref name="heldX"/> 取符号,0 是没按;
        /// 顺着朝向按且配方有"顺着按"的速度(<paramref name="hasForwardSpeed"/>)时只换速度,反着按两个数都是 0。
        /// </summary>
        public static void HeldSlide(double baseSpeed, double baseAccel, bool hasForwardSpeed, double forwardSpeed, int heldX, bool facingLeft,
            out double speed, out double accel)
        {
            speed = baseSpeed;
            accel = baseAccel;
            int held = Math.Sign(heldX);
            if (held == 0)
            {
                return;
            }
            bool forward = (held < 0) == facingLeft;
            if (!forward)
            {
                speed = 0;
                accel = 0;
                return;
            }
            if (hasForwardSpeed)
            {
                speed = forwardSpeed;
            }
        }

        /// <summary>
        /// 一段的伤害百分比乘倍率(0x009138c0):倍率 = 单精度的 <paramref name="percent"/> × 0.01f(0x01565cc0),
        /// 客户端算 列值 × 倍率 − 100 向零取整,得到"超出 100% 的部分",这里的百分比口径是总百分比,所以再加回 100。倍率恰为 100 时与不乘逐位一致。
        /// </summary>
        public static int ScaleRate(int rate, int percent)
        {
            float scale = percent * 0.01f;
            int bonus = (int)((double)rate * scale - 100.0);
            return bonus + 100;
        }

        /// <summary>
        /// 按键接段的目标段:第一条"当前段在范围内、动画帧够、按下了认的键"的规则;没有返回 -1。
        /// </summary>
        public static int ChainTarget(SkillBodyHitSpec spec, int stage, int frame, SkillChainKeys pressed)
        {
            if (spec == null)
            {
                return -1;
            }
            foreach (SkillKeyChain c in spec.Chains)
            {
                if (stage >= c.FromFirstStage && stage <= c.FromLastStage && frame >= c.FromFrame && (c.Keys & pressed) != SkillChainKeys.None)
                {
                    return c.ToStage;
                }
            }
            return -1;
        }

        /// <summary>
        /// 保持到状态计时器的哪个毫秒数为止:<see cref="SkillHoldKind.AfterAnimation"/> 是动画墙钟时长加 <paramref name="value"/>(负数当 0),
        /// <see cref="SkillHoldKind.AtLeast"/> 是动画时长与 <paramref name="value"/> 里大的那个。
        /// </summary>
        public static long HoldUntilMs(SkillHoldKind kind, double animationMs, int value)
        {
            long anim = (long)animationMs;
            if (kind == SkillHoldKind.AfterAnimation)
            {
                return anim + Math.Max(0, value);
            }
            return Math.Max(anim, value);
        }

        /// <summary>关键帧标志起滑要不要起:横轴按住的方向恰好是"朝向的反方向"就不起(0x00cc6e11–0x00cc6e1b);没按、顺着按、只按上下都起。</summary>
        public static bool ScriptSlideStarts(int heldX, bool facingLeft)
        {
            int held = Math.Sign(heldX);
            return held == 0 || (held < 0) == facingLeft;
        }

        /// <summary>线性位移在已过 <paramref name="elapsedMs"/> 毫秒时离段起点的距离(像素,不带方向):夹住的整数线性插值(<see cref="SkillHopPlan.IntLerp"/>,0x011b98c0)。</summary>
        public static int TravelOffset(int distance, int durationMs, long elapsedMs)
        {
            int elapsed = elapsedMs > int.MaxValue ? int.MaxValue : (int)elapsedMs;
            return SkillHopPlan.IntLerp(0, distance, elapsed, durationMs);
        }

        /// <summary>进入 <paramref name="stage"/> 段时要不要按配方起滑(<see cref="SkillBodyHitSpec.SlideFirstStageOnly"/>):没带规则的配方每段都起滑。</summary>
        public static bool SlidesInStage(SkillBodyHitSpec spec, int stage)
        {
            return spec == null || !spec.SlideFirstStageOnly || stage == 0;
        }

        /// <summary>从 <paramref name="fromStage"/> 段播完时能不能自动接下一段(<see cref="SkillBodyHitSpec.AutoChainLastFromStage"/> 的上界)。</summary>
        public static bool AutoChainAllowed(SkillBodyHitSpec spec, int fromStage)
        {
            return spec == null || spec.AutoChainLastFromStage < 0 || fromStage <= spec.AutoChainLastFromStage;
        }

        /// <summary>动画前 <paramref name="animationMs"/> 动画毫秒(若干帧时长之和)折成墙钟毫秒(按播放倍率,0x01090600;倍率不为正按 1)。</summary>
        public static int FrameStartWallMs(int animationMs, double rate)
        {
            double r = rate > 0.0 ? rate : 1.0;
            return (int)(animationMs * 100.0 / (r * 100.0));
        }
    }
}
