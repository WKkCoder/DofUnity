using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;

// 证据:E-271、E-272(技能起手函数与分派表 0x004a02d0);本批状态族(0x21、0x25、0x27)的进入块、逐帧块与公共后段里的守卫块的读码结果写在各配方注释里。
// 这里只放"在已有多段配方上再加一点"的扩展:每段可以不滑行、一段开头可以瞬移、两段之间可以混用"自己接"与"按键接"、
// 一组"起手 + 打击"可以按 [static data] 里的击数重复。扩展项默认都不起作用,已有配方的放法不变。
namespace Dnf.Play
{
    /// <summary>
    /// 一段开头的瞬移(状态 0x21 阶段 1,进入块 0x00cd370d 起,到 0x00cd3781 把横坐标设成落点):
    /// 目标横坐标 = 现在的横坐标 ∓ [static data] 第 <see cref="DistanceStaticIndex"/> 个数(朝向为 0 即朝左取减),
    /// 然后调 0x00871250(横轴、起点、终点)——它从起点一像素一像素往终点走,每一步问虚表槽 +0xe0"这一点站不站得住",
    /// 走到第一个站不住的点就停在前一个点(第一步就站不住则停在起点),走到终点站得住就停在终点;返回的就是落点。
    /// 落点与现在横坐标的差存进对象 +0x5f84,横坐标由虚表槽 +0x94 直接设成落点,纵向不变。没有做:瞬移前在原地放的残影、落点前后的差值给后续阶段用。
    /// </summary>
    public sealed class SkillWarpSpec
    {
        public SkillWarpSpec(int distanceStaticIndex)
        {
            if (distanceStaticIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(distanceStaticIndex), "瞬移距离要有 [static data] 下标");
            }
            DistanceStaticIndex = distanceStaticIndex;
        }

        /// <summary>瞬移距离(像素)在 [static data] 里的下标。</summary>
        public int DistanceStaticIndex { get; }

        /// <summary>
        /// 落点的横坐标:从 <paramref name="x"/> 起往朝向一侧一像素一像素走 <paramref name="distance"/> 步,
        /// 第一个站不住(<see cref="IWalkArea.Contains"/> 为假)的点之前停下;<paramref name="walk"/> 为空或不限制移动(<see cref="IWalkArea.IsUnrestricted"/>)时当作处处站得住。
        /// 距离为负时方向反过来(客户端按"目标 − 起点"的符号选方向);距离为 0 原地不动。
        /// </summary>
        public static double Destination(double x, double y, int distance, bool facingLeft, IWalkArea walk)
        {
            int offset = facingLeft ? -distance : distance;
            int direction = Math.Sign(offset);
            double landing = x;
            for (int step = 1; step <= Math.Abs(offset); step++)
            {
                double next = x + direction * step;
                if (walk != null && !walk.IsUnrestricted && !walk.Contains(next, y))
                {
                    break;
                }
                landing = next;
            }
            return landing;
        }
    }

    /// <summary>
    /// 逐击连段(状态 0x27,进入块 0x00cd5323 起,守卫块 0x00cc642a 起):每一击都是"起手 + 打击"两个阶段,击数取 [static data] 第 <see cref="CountStaticIndex"/> 个数(进入块 0x00cd5486);
    /// 配方里写四段当模板——非最后一击的起手、非最后一击的打击、最后一击的起手、最后一击的打击(进入块按"第几击 &lt; 击数 − 1"二选一动作、攻击信息与伤害列)——
    /// 槽建好时按击数展开成 击数 × 2 段(<see cref="SkillRecipe.ExpandFor"/>)。
    /// 起手段播完自己接打击段(守卫块阶段 0 看动画结束标志),打击段里动画播过第 2 帧后再按技能键接下一击(阶段 1 的守卫:帧号 ≥ 2、不是最后一击、技能键按下);
    /// [static data] 第 <see cref="LockPenultimateStaticIndex"/> 个数非 0 时倒数第二击的打击段不许再按键接(守卫块 0x00cc6587 起:该数非 0 且"第几击 == 击数 − 2"时放弃接段)。
    /// 没有做:非基础职业时击数改成 1(进入块 0x00cd5499)、学了两个被动技能时击数加 1(只在成长类型 0 时判,本技能学不到成长类型 0)。
    /// </summary>
    public sealed class SkillRepeatSpec
    {
        /// <summary>模板的段数:非最后一击(起手、打击)与最后一击(起手、打击)。</summary>
        public const int TemplateStages = 4;

        public SkillRepeatSpec(int countStaticIndex, int lockPenultimateStaticIndex = -1)
        {
            if (countStaticIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(countStaticIndex), "击数要有 [static data] 下标");
            }
            CountStaticIndex = countStaticIndex;
            LockPenultimateStaticIndex = lockPenultimateStaticIndex;
        }

        /// <summary>击数在 [static data] 里的下标。</summary>
        public int CountStaticIndex { get; }

        /// <summary>"倒数第二击不许按键接最后一击"的开关在 [static data] 里的下标;负数表示没有这条规则。</summary>
        public int LockPenultimateStaticIndex { get; }

        /// <summary>这个技能这一次放出来的击数:读不到技能定义或数不大于 0 时按一击算(至少打一次)。</summary>
        public int HitsOf(SkillDef def)
        {
            return def == null ? 1 : Math.Max(1, def.GetStatic(CountStaticIndex, false));
        }

        /// <summary>倒数第二击的打击段是否被锁住(不许按键接最后一击)。</summary>
        public bool LocksPenultimate(SkillDef def)
        {
            return def != null && LockPenultimateStaticIndex >= 0 && def.GetStatic(LockPenultimateStaticIndex, false) != 0;
        }
    }

    public sealed partial class SkillObjectSpec
    {
        /// <summary>
        /// 放在这一段起点瞬移(<see cref="SkillWarpSpec"/>)之前主角站的位置,而不是放对象那一刻主角的位置(默认 false)。
        /// 状态 0x21 阶段 1 的进入块先在原地创建被动对象(0x00cd35db 起),再在 0x00cd370d 起瞬移,所以对象留在出发点。
        /// 这一段没有瞬移或没记到出发点(读不到技能定义)时仍按放对象那一刻主角的位置放。
        /// </summary>
        public bool FromWarpOrigin { get; private set; }

        /// <summary>复制一份并让对象放在起点瞬移之前的位置(<see cref="FromWarpOrigin"/> = true)。</summary>
        public SkillObjectSpec AtWarpOrigin()
        {
            SkillObjectSpec copy = (SkillObjectSpec)MemberwiseClone();
            copy.FromWarpOrigin = true;
            return copy;
        }
    }

    public sealed partial class SkillStageSpec
    {
        /// <summary>
        /// 进入这一段要不要带起滑(默认要,按配方上的起滑速度与加速度算);false 时这一段把两个轴的滑行速度清零
        /// (状态 0x25 阶段 1、状态 0x27 阶段 0 的进入块都先把滑行清零,不继承上一段的)。
        /// </summary>
        public bool Slides { get; private set; } = true;

        /// <summary>这一段一开头的瞬移(<see cref="SkillWarpSpec"/>);没有为 null。</summary>
        public SkillWarpSpec Warp { get; private set; }

        /// <summary>
        /// 这一段的动画播完就自己接下一段(不管配方是不是整体自动接续,也不再看按键);默认 false。
        /// 状态 0x27 的起手段:守卫块阶段 0 在动画结束标志上直接发"阶段 1"。
        /// </summary>
        public bool AutoAdvance { get; private set; }

        /// <summary>
        /// 这一段还允许再按技能键接下一段(默认 true);false 时按键不接(只能等动画播完结束),状态 0x27 的"倒数第二击被锁"用它。
        /// 对 <see cref="AutoAdvance"/> 的段无意义(那种段本来就不看按键)。
        /// </summary>
        public bool KeyChainOut { get; private set; } = true;

        /// <summary>复制一份并让这一段不带起滑(<see cref="Slides"/> = false)。</summary>
        public SkillStageSpec WithoutSlide()
        {
            SkillStageSpec copy = Clone();
            copy.Slides = false;
            return copy;
        }

        /// <summary>复制一份并在这一段开头加瞬移。</summary>
        public SkillStageSpec WithWarp(SkillWarpSpec warp)
        {
            SkillStageSpec copy = Clone();
            copy.Warp = warp ?? throw new ArgumentNullException(nameof(warp));
            return copy;
        }

        /// <summary>复制一份并让这一段播完自己接下一段。</summary>
        public SkillStageSpec WithAutoAdvance()
        {
            SkillStageSpec copy = Clone();
            copy.AutoAdvance = true;
            return copy;
        }

        /// <summary>复制一份并设这一段能不能按键接下一段。</summary>
        public SkillStageSpec WithKeyChainOut(bool allowed)
        {
            SkillStageSpec copy = Clone();
            copy.KeyChainOut = allowed;
            return copy;
        }

        private SkillStageSpec Clone()
        {
            return (SkillStageSpec)MemberwiseClone();
        }
    }

    public sealed partial class SkillRecipe
    {
        /// <summary>逐击连段的参数(<see cref="SkillRepeatSpec"/>);不是这一类为 null。有它的配方 <see cref="Stages"/> 是四段模板,槽建好时才展开。</summary>
        public SkillRepeatSpec Repeat { get; private set; }

        /// <summary>
        /// [static data] 里这个配方额外用到的最大下标 + 1(瞬移距离、击数与锁开关;没用到为 0):检查工具用来核对真实数据里的数够不够。
        /// </summary>
        public int ExtraStaticNeed
        {
            get
            {
                int need = 0;
                foreach (SkillStageSpec stage in Stages)
                {
                    if (stage.Warp != null)
                    {
                        need = Math.Max(need, stage.Warp.DistanceStaticIndex + 1);
                    }
                }
                if (Repeat != null)
                {
                    need = Math.Max(need, Math.Max(Repeat.CountStaticIndex, Repeat.LockPenultimateStaticIndex) + 1);
                }
                return need;
            }
        }

        /// <summary>复制一份并标成逐击连段:当前的四段是模板(<see cref="SkillRepeatSpec.TemplateStages"/>),不能同时是自动接续、蓄力跳跃、悬停俯冲或空中攻击强化。</summary>
        public SkillRecipe WithRepeat(SkillRepeatSpec repeat)
        {
            if (repeat == null)
            {
                throw new ArgumentNullException(nameof(repeat));
            }
            if (Stages.Count != SkillRepeatSpec.TemplateStages)
            {
                throw new ArgumentException("逐击连段的模板要正好四段", nameof(repeat));
            }
            if (AutoChain || Hop != null || Dive != null || JumpAttack != null)
            {
                throw new ArgumentException("逐击连段的配方不能同时是自动接续、蓄力跳跃、悬停俯冲或空中攻击强化", nameof(repeat));
            }
            SkillRecipe copy = (SkillRecipe)MemberwiseClone();
            copy.Repeat = repeat;
            return copy;
        }

        /// <summary>
        /// 按这个技能的 [static data] 把逐击连段的模板展开成击数 × 2 段(<see cref="Repeat"/> 为空就原样返回自己):
        /// 前 击数 − 1 击用模板的第 0、1 段,最后一击用第 2、3 段;倒数第二击的打击段在 <see cref="SkillRepeatSpec.LocksPenultimate"/> 时关掉按键接段。
        /// 返回的配方不再带 <see cref="Repeat"/>,其余内容(起滑下标、对象、被动威力等)都与模板相同。
        /// </summary>
        public SkillRecipe ExpandFor(SkillDef def)
        {
            if (Repeat == null)
            {
                return this;
            }
            int hits = Repeat.HitsOf(def);
            bool locked = Repeat.LocksPenultimate(def);
            var expanded = new List<SkillStageSpec>(hits * 2);
            for (int hit = 0; hit < hits; hit++)
            {
                bool last = hit == hits - 1;
                int baseIndex = last ? 2 : 0;
                expanded.Add(Stages[baseIndex]);
                SkillStageSpec strike = Stages[baseIndex + 1];
                expanded.Add(locked && hit == hits - 2 ? strike.WithKeyChainOut(false) : strike);
            }
            SkillRecipe copy = (SkillRecipe)MemberwiseClone();
            copy.Stages = expanded.AsReadOnly();
            copy.Repeat = null;
            return copy;
        }
    }
}
