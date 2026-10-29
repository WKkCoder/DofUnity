using System;
using System.Collections.Generic;

// 证据:E-129(客户端原生技能状态码的读码结果:技能起手函数、状态进入块、逐帧块里的连击判定);E-135(波系技能状态 0x18:施法蓄力与主角放出的被动对象);
// E-137(挂多个特效、对象等特效关键帧的那一类技能,状态 0x23:一进状态就挂四个特效、被动对象等特效动画的关键帧标志、帧号到数就结束状态);
// E-138(蓄力后跳起来砸下去的那一类技能,状态 0x24:按住技能键蓄力、松开或蓄满才出招,跳跃距离与高度按蓄力档位插值,位移由动画时间脚本化,命中窗口由计时器重武装);
// E-139(单段位移攻击类技能,状态 0x1d:起滑的"顺着按"两个数替换基础两个数、学了某个被动技能后命中名单按计时器多次重新武装);
// E-141(增益类技能,状态 0x11:施法动作只播 [buff motion],状态正常结束时才挂属性附加体,持续时间与数值取自技能数据的哪一列由技能自己的挂附加体代码决定)。
namespace Dnf.Play
{
    /// <summary>
    /// 多段技能里的一段(证据 E-129)。下标是客户端状态码里写死的常量,指的是角色脚本与技能脚本里按序排的表,不是数据值:
    /// 动作取 [etc motion] 的第几项,攻击信息取 [etc attack info] 的第几项,伤害取 .skl [level info] 当前等级的哪两列(百分比列、固定值列)。
    /// 攻击信息下标为 -1 的段没有自己的命中(主角的身体不打人,伤害由它放出的被动对象结算,<see cref="SkillObjectSpec"/>,证据 E-135),这时两个伤害列也填 -1。
    /// </summary>
    public sealed class SkillStageSpec
    {
        public SkillStageSpec(int motionIndex, int attackInfoIndex, int damageRateColumn, int damageFixedColumn, int effectIndex, int effectFrame, int requiresSkill = 0,
            int castHoldFrames = 0, int[] moreEffects = null, int endFrame = -1)
        {
            MotionIndex = motionIndex;
            AttackInfoIndex = attackInfoIndex;
            DamageRateColumn = damageRateColumn;
            DamageFixedColumn = damageFixedColumn;
            EffectIndex = effectIndex;
            EffectFrame = effectFrame;
            RequiresSkill = requiresSkill;
            CastHoldFrames = castHoldFrames < 0 ? 0 : castHoldFrames;
            var effects = new List<int>();
            if (effectIndex >= 0)
            {
                effects.Add(effectIndex);
            }
            if (moreEffects != null)
            {
                effects.AddRange(moreEffects);
            }
            EffectIndices = effects.AsReadOnly();
            EndFrame = endFrame;
        }

        /// <summary>[etc motion] 里的下标:这一段播的技能动作。</summary>
        public int MotionIndex { get; }

        /// <summary>[etc attack info] 里的下标:这一段命中用的攻击信息(.atk)。</summary>
        public int AttackInfoIndex { get; }

        /// <summary>.skl [level info] 里这一段伤害的百分比列。</summary>
        public int DamageRateColumn { get; }

        /// <summary>.skl [level info] 里这一段伤害的固定值列。</summary>
        public int DamageFixedColumn { get; }

        /// <summary>这一段放的特效在技能预载特效表(<see cref="Dnf.Game.SkillEffectTable"/>)里的下标;-1 表示没有特效。</summary>
        public int EffectIndex { get; }

        /// <summary>
        /// 这一段放的全部特效在预载表里的下标(<see cref="EffectIndex"/> 排第一,后面是额外要同时放的,证据 E-137:状态 0x23 一进状态就把四个预载特效各放进一个图层);
        /// 没有特效为空表。
        /// </summary>
        public IReadOnlyList<int> EffectIndices { get; }

        /// <summary>特效在这一段的动画播到第几帧(从 0 数)时放;0 就是这一段一开始就放。</summary>
        public int EffectFrame { get; }

        /// <summary>
        /// 这一段动画播到第几帧(从 0 数)的开头状态就结束、主角回到站立(证据 E-137:状态码在帧号到数时直接请求站立状态,动画没播完也不管);
        /// 负数表示播到动画结束。
        /// </summary>
        public int EndFrame { get; }

        /// <summary>进入这一段要求学会的技能(编号);0 表示没有要求。</summary>
        public int RequiresSkill { get; }

        /// <summary>
        /// 施法蓄力(证据 E-135):大于 0 时这一段动作的第 0 帧是蓄力占位(帧里写着很长的停留),按"施法时间减去第 1 到第 n 帧的时长"算出要停多久再放行
        /// (n 就是这个数,波系技能是 3);0 表示动画照帧时长走,没有蓄力。
        /// </summary>
        public int CastHoldFrames { get; }

        /// <summary>这一段有自己的命中(攻击信息下标非负);没有的段主角的身体不打人。</summary>
        public bool HasOwnStrike => AttackInfoIndex >= 0;
    }

    /// <summary>
    /// 技能放出的被动对象(证据 E-135):主角自己放出去、打怪物的老式对象(.obj 只有 [basic motion] 与 [attack info],没有 .act)。
    /// 编号是客户端状态码里写死的被动对象编号(passiveobject.lst 的键);动画播到 <see cref="Frame"/> 帧时放一次,放在主角前方 <see cref="ForwardX"/> 像素、
    /// 朝向与主角一致;伤害取 .skl [level info] 当前等级的 <see cref="DamageRateColumn"/>(百分比)与 <see cref="DamageFixedColumn"/>(固定值)两列,
    /// 命中用对象自己 .obj 的 [attack info]。<see cref="Frame"/> 是下限;带 <see cref="AfterEffect"/> 时还要等那个特效的动画播到关键帧标志。
    /// </summary>
    public sealed class SkillObjectSpec
    {
        public SkillObjectSpec(int objectId, int stage, int frame, int damageRateColumn, int damageFixedColumn, int forwardX = 0, int offsetY = 0, int offsetZ = 0,
            int afterEffect = -1, int afterEffectFlag = 1)
        {
            ObjectId = objectId;
            Stage = stage;
            Frame = frame < 0 ? 0 : frame;
            DamageRateColumn = damageRateColumn;
            DamageFixedColumn = damageFixedColumn;
            ForwardX = forwardX;
            OffsetY = offsetY;
            OffsetZ = offsetZ;
            AfterEffect = afterEffect;
            AfterEffectFlag = afterEffectFlag;
        }

        /// <summary>被动对象编号(passiveobject.lst 的键)。</summary>
        public int ObjectId { get; }

        /// <summary>在配方的第几段放(从 0 数);越界按最后一段。</summary>
        public int Stage { get; }

        /// <summary>这一段的动画播到第几帧(从 0 数)时放;一次挥击只放一次。</summary>
        public int Frame { get; }

        /// <summary>.skl [level info] 里对象伤害的百分比列。</summary>
        public int DamageRateColumn { get; }

        /// <summary>.skl [level info] 里对象伤害的固定值列。</summary>
        public int DamageFixedColumn { get; }

        /// <summary>放在主角世界 x 的前方多少像素(朝左往左,朝右往右)。</summary>
        public int ForwardX { get; }

        /// <summary>y(纵深)方向的偏移。</summary>
        public int OffsetY { get; }

        /// <summary>离地高度的偏移。</summary>
        public int OffsetZ { get; }

        /// <summary>
        /// 放这个对象还要等预载表里第几个特效的动画播到关键帧标志 <see cref="AfterEffectFlag"/>(证据 E-137:状态 0x23 的对象在第 1 个特效播到带标志 1 的帧时才放);
        /// -1 表示只看 <see cref="Frame"/>。特效没放出来(读不到动画)时退回只看 <see cref="Frame"/>。
        /// </summary>
        public int AfterEffect { get; }

        /// <summary>等到的关键帧标志编号。</summary>
        public int AfterEffectFlag { get; }
    }

    /// <summary>
    /// 蓄力跳跃类技能的特别部分(证据 E-138)。配方的第 0 段是蓄力姿势(没有命中;按住技能键就停在这里),第 1 段是跳跃。
    /// 蓄力时长在 [<see cref="HoldMinIndex"/>, <see cref="HoldMaxIndex"/>] 两个 [static data] 数之间:松开技能键或到了最长时间就出招,
    /// 蓄力档位 = 夹住的整数插值(0, 100, 已蓄毫秒 − 最短, 最长 − 最短);跳跃的水平距离、最大高度、动画变速(百分比)与命中窗口数,
    /// 都在各自的一对 [static data] 数之间按档位(0 到 100)插值:前一个数对应档位 0、后一个对应档位 100(名字里的"最小/最大"只是这个次序,
    /// 两个数不一定前小后大,实现按它们在 [static data] 里的次序插值)。
    /// 命中窗口:第一个窗口在跳跃动画的 <see cref="HitStartFrame"/> 帧开头打开,每隔 (<see cref="HitEndFrame"/> − <see cref="HitStartFrame"/>) 两帧开头的时间差 / 窗口数
    /// 重新武装一次,共 窗口数 − 1 次;第一个窗口在窗口数大于 1 时用第 1 段的攻击信息,重武装的窗口(以及窗口数为 1 时唯一的窗口)用
    /// <see cref="RepeatAttackInfoIndex"/> 那一项;伤害百分比都取第 1 段的列。下标与帧号都是客户端状态码里的常量。
    /// </summary>
    public sealed class SkillHopSpec
    {
        public SkillHopSpec(int holdMinIndex, int holdMaxIndex, int distanceMinIndex, int distanceMaxIndex, int heightMinIndex, int heightMaxIndex,
            int speedMinIndex, int speedMaxIndex, int hitsMinIndex, int hitsMaxIndex, int riseFrame, int landFrame, int hitStartFrame, int hitEndFrame,
            int repeatAttackInfoIndex)
        {
            HoldMinIndex = holdMinIndex;
            HoldMaxIndex = holdMaxIndex;
            DistanceMinIndex = distanceMinIndex;
            DistanceMaxIndex = distanceMaxIndex;
            HeightMinIndex = heightMinIndex;
            HeightMaxIndex = heightMaxIndex;
            SpeedMinIndex = speedMinIndex;
            SpeedMaxIndex = speedMaxIndex;
            HitsMinIndex = hitsMinIndex;
            HitsMaxIndex = hitsMaxIndex;
            RiseFrame = riseFrame;
            LandFrame = landFrame;
            HitStartFrame = hitStartFrame;
            HitEndFrame = hitEndFrame;
            RepeatAttackInfoIndex = repeatAttackInfoIndex;
        }

        /// <summary>[static data] 里最短蓄力毫秒的下标(松开得早于它也算蓄力档位 0)。</summary>
        public int HoldMinIndex { get; }

        /// <summary>[static data] 里最长蓄力毫秒的下标(到了就自动出招)。</summary>
        public int HoldMaxIndex { get; }

        public int DistanceMinIndex { get; }
        public int DistanceMaxIndex { get; }
        public int HeightMinIndex { get; }
        public int HeightMaxIndex { get; }

        /// <summary>跳跃动画变速(百分比,100 是原速,在攻击速度的倍率之上再乘)在档位 0、档位 100 两端的值的下标。</summary>
        public int SpeedMinIndex { get; }

        public int SpeedMaxIndex { get; }
        public int HitsMinIndex { get; }
        public int HitsMaxIndex { get; }

        /// <summary>跳跃动画里上升结束(也是下落开始)的帧号:这一帧的开头走完了上升段。</summary>
        public int RiseFrame { get; }

        /// <summary>落地的帧号:这一帧的开头主角落回地面。</summary>
        public int LandFrame { get; }

        /// <summary>第一个命中窗口打开的帧号。</summary>
        public int HitStartFrame { get; }

        /// <summary>命中窗口全部结束的帧号(算重武装间隔用)。</summary>
        public int HitEndFrame { get; }

        /// <summary>[etc attack info] 里重武装窗口用的攻击信息的下标。</summary>
        public int RepeatAttackInfoIndex { get; }
    }

    /// <summary>
    /// 一个多段地面技能的放法(证据 E-129):各段(<see cref="Stages"/>)、起滑速度与加速度取 .skl [static data] 的哪几个数、接下一段的条件。
    /// 放法:起手放第 0 段;这一段的动画播过 <see cref="ChainFromFrame"/> 帧之后再按一次同一个技能键,就换成下一段(不再扣蓝、不再进冷却)。
    /// 滑行:起滑速度与加速度取 [static data] 的第 <see cref="SlideSpeedIndex"/>、<see cref="SlideAccelIndex"/> 个数;进入每一段那一刻看方向键——
    /// 没按方向键用这两个数,按着朝向一侧的方向键再加上第 <see cref="ForwardSpeedIndex"/>、<see cref="ForwardAccelIndex"/> 个数,按着别的方向(反向、上下)就不滑。
    /// </summary>
    public sealed class SkillRecipe
    {
        public SkillRecipe(int skillId, IEnumerable<SkillStageSpec> stages, int slideSpeedIndex, int slideAccelIndex, int forwardSpeedIndex, int forwardAccelIndex,
            int chainFromFrame, bool slideFollowsHeldDirection = true, IEnumerable<SkillObjectSpec> objects = null, SkillHopSpec hop = null,
            bool forwardReplacesBase = false, int multiHitPassiveSkillId = 0, int multiHitCountIndex = -1)
        {
            ForwardReplacesBase = forwardReplacesBase;
            MultiHitPassiveSkillId = multiHitPassiveSkillId;
            MultiHitCountIndex = multiHitCountIndex;
            SkillId = skillId;
            Stages = new List<SkillStageSpec>(stages ?? throw new ArgumentNullException(nameof(stages))).AsReadOnly();
            if (Stages.Count == 0)
            {
                throw new ArgumentException("配方至少要有一段", nameof(stages));
            }
            SlideSpeedIndex = slideSpeedIndex;
            SlideAccelIndex = slideAccelIndex;
            ForwardSpeedIndex = forwardSpeedIndex;
            ForwardAccelIndex = forwardAccelIndex;
            ChainFromFrame = chainFromFrame;
            SlideFollowsHeldDirection = slideFollowsHeldDirection;
            Objects = new List<SkillObjectSpec>(objects ?? Array.Empty<SkillObjectSpec>()).AsReadOnly();
            Hop = hop;
            if (hop != null && Stages.Count < 2)
            {
                throw new ArgumentException("蓄力跳跃的配方要有蓄力与跳跃两段", nameof(stages));
            }
        }

        public int SkillId { get; }
        public IReadOnlyList<SkillStageSpec> Stages { get; }

        /// <summary>起滑速度在 [static data] 里的下标;负数表示不滑行。</summary>
        public int SlideSpeedIndex { get; }

        /// <summary>滑行加速度在 [static data] 里的下标;负数表示加速度为 0。</summary>
        public int SlideAccelIndex { get; }

        /// <summary>按着朝向一侧的方向键时,起滑速度的加成在 [static data] 里的下标;负数表示没有加成。</summary>
        public int ForwardSpeedIndex { get; }

        /// <summary>按着朝向一侧的方向键时,加速度的加成在 [static data] 里的下标;负数表示没有加成。</summary>
        public int ForwardAccelIndex { get; }

        /// <summary>
        /// "顺着按"的两个数是替换基础的两个数(证据 E-139:状态 0x1d 的进入块里,朝向一侧的方向键把起滑速度与加速度整个换成第三、第四个数),
        /// 而不是像状态 0x2a 那样加在基础的两个数上;false 是加成(默认)。只在 <see cref="SlideFollowsHeldDirection"/> 为 true 时有意义。
        /// </summary>
        public bool ForwardReplacesBase { get; }

        /// <summary>
        /// 多段命中要学会的被动技能编号(证据 E-139);0 表示这个配方没有多段命中。学过这个被动技能(等级大于 0)的主角放这个技能时,
        /// 命中名单在动画播放期间按计时器重新武装,同一个目标可以再挨几下,次数取 [static data] 的第 <see cref="MultiHitCountIndex"/> 个数。
        /// </summary>
        public int MultiHitPassiveSkillId { get; }

        /// <summary>多段命中次数在 [static data] 里的下标;负数表示没有。</summary>
        public int MultiHitCountIndex { get; }

        /// <summary>当前段的动画播到第几帧(从 0 数)以后,再按同一个技能键才算接下一段。</summary>
        public int ChainFromFrame { get; }

        /// <summary>
        /// 进入一段时的起滑要不要看按着的方向键(证据 E-129、E-131):true 时按"没按/顺着按/按别的方向就不滑"算;
        /// false 时不看方向键,每一段都按基础的两个数朝着主角的朝向滑(状态进入块里没有读按住的方向)。
        /// </summary>
        public bool SlideFollowsHeldDirection { get; }

        /// <summary>这个技能放出的被动对象(证据 E-135);没有为空表。</summary>
        public IReadOnlyList<SkillObjectSpec> Objects { get; }

        /// <summary>
        /// 蓄力跳跃类技能的参数(证据 E-138);不是这一类技能为 null。有它的配方第 0 段是蓄力姿势、第 1 段是跳跃,
        /// 两段之间由会话按"松开或蓄满"自动衔接(再按技能键不接段)。
        /// </summary>
        public SkillHopSpec Hop { get; }
    }

    /// <summary>
    /// 增益类技能的放法(状态 0x11,证据 E-141):主角原地播 [buff motion],动画播完(状态正常结束)那一刻才挂上一个属性增益——
    /// 改哪个属性是 <see cref="Stat"/>,持续时间(毫秒)取 .skl [level info] 当前等级的 <see cref="DurationColumn"/> 列,
    /// 数值取 [static data] 的第 <see cref="AmountStaticIndex"/> 个数(不小于 0 时)或 [level info] 当前等级的 <see cref="AmountColumn"/> 列(否则)。
    /// 下标与列号都是客户端挂附加体的代码里写死的常量,不是数据值。耗蓝与冷却同别的技能,在放出来(进入施法状态)那一刻算(推断)。
    /// </summary>
    public sealed class BuffRecipe
    {
        public BuffRecipe(int skillId, BuffStat stat, int durationColumn, int amountStaticIndex, int amountColumn, double amountScale = 1.0)
        {
            SkillId = skillId;
            Stat = stat;
            DurationColumn = durationColumn;
            AmountStaticIndex = amountStaticIndex;
            AmountColumn = amountColumn;
            AmountScale = amountScale;
        }

        public int SkillId { get; }

        /// <summary>改哪个属性。</summary>
        public BuffStat Stat { get; }

        /// <summary>.skl [level info] 里持续时间(毫秒)的列。</summary>
        public int DurationColumn { get; }

        /// <summary>数值在 [static data] 里的下标;负数表示数值取 <see cref="AmountColumn"/> 列。</summary>
        public int AmountStaticIndex { get; }

        /// <summary>数值在 [level info] 里的列(<see cref="AmountStaticIndex"/> 为负时用)。</summary>
        public int AmountColumn { get; }

        /// <summary>读出来的整数乘它才是挂在属性上的数(缺省 1;概率类增益的数是千分比,乘 0.001 换成 0 到 1,E-142)。</summary>
        public double AmountScale { get; }
    }

    /// <summary>
    /// 技能配方表。<see cref="Default"/> 是读码移植出来的技能(证据 E-129);测试可以自己造一张。
    /// 表里的数都是客户端状态码里的常量(下标、列号),不含 PVF 里的数据值,也不含任何文件名——文件名按下标到角色脚本与技能预载表里取。
    /// 增益类技能(<see cref="BuffRecipe"/>)另放一张表:它们没有多段动作与攻击信息,只有一个施法动作与一个附加体。
    /// </summary>
    public sealed class SkillRecipes
    {
        private readonly Dictionary<int, SkillRecipe> _byId = new Dictionary<int, SkillRecipe>();
        private readonly Dictionary<int, BuffRecipe> _buffs = new Dictionary<int, BuffRecipe>();

        public SkillRecipes(IEnumerable<SkillRecipe> recipes, IEnumerable<BuffRecipe> buffs = null)
        {
            foreach (SkillRecipe r in recipes ?? throw new ArgumentNullException(nameof(recipes)))
            {
                _byId[r.SkillId] = r;
            }
            if (buffs != null)
            {
                foreach (BuffRecipe b in buffs)
                {
                    _buffs[b.SkillId] = b;
                }
            }
        }

        /// <summary>配方的个数(不含增益类)。</summary>
        public int Count => _byId.Count;

        /// <summary>全部配方(检查表本身的结构用;不含增益类)。</summary>
        public IEnumerable<SkillRecipe> All => _byId.Values;

        /// <summary>增益类技能的个数。</summary>
        public int BuffCount => _buffs.Count;

        /// <summary>全部增益类技能的放法。</summary>
        public IEnumerable<BuffRecipe> AllBuffs => _buffs.Values;

        /// <summary>这个技能的配方;没有返回 null。</summary>
        public SkillRecipe Find(int skillId)
        {
            return _byId.TryGetValue(skillId, out SkillRecipe r) ? r : null;
        }

        /// <summary>这个技能的增益放法;不是增益类技能返回 null。</summary>
        public BuffRecipe FindBuff(int skillId)
        {
            return _buffs.TryGetValue(skillId, out BuffRecipe b) ? b : null;
        }

        /// <summary>读码移植出来的配方。</summary>
        public static SkillRecipes Default { get; } = new SkillRecipes(new[]
        {
            // 两段连击技能:起手函数 0x0049d520 → 状态 0x2a(证据 E-129)。第 0 段在动画第 2 帧放第 0 个特效,第 1 段一开始放第 1 个;
            // 第 2 段要另一个被动技能与动画变速,还没做。
            new SkillRecipe(77,
                new[]
                {
                    new SkillStageSpec(motionIndex: 61, attackInfoIndex: 54, damageRateColumn: 0, damageFixedColumn: 3, effectIndex: 0, effectFrame: 2),
                    new SkillStageSpec(motionIndex: 62, attackInfoIndex: 55, damageRateColumn: 1, damageFixedColumn: 4, effectIndex: 1, effectFrame: 0),
                },
                slideSpeedIndex: 0, slideAccelIndex: 1, forwardSpeedIndex: 2, forwardAccelIndex: 3, chainFromFrame: 3),

            // 连斩技能的默认三段版本:起手函数 0x0049d3d0 → 状态 0x16(证据 E-131)。动作取 [etc motion] 的 3+段号,
            // 攻击信息取 [etc attack info] 的 段号+2(三段版的最后一段取第 7 项),伤害列(百分比,固定值=百分比列+3)三段版是 0、0、1;
            // 特效取预载表的第"段号"个;不看方向键、朝向一侧滑 [static data] 头两个数。接段帧表(0x016f8cd0)前四项都是 3。
            // 五段版要求学会两个被动技能中的一个并持对应武器,三段版没有预载的移动特效(脚下的)与音效,这些都还没做。
            new SkillRecipe(8,
                new[]
                {
                    new SkillStageSpec(motionIndex: 3, attackInfoIndex: 2, damageRateColumn: 0, damageFixedColumn: 3, effectIndex: 0, effectFrame: 0),
                    new SkillStageSpec(motionIndex: 4, attackInfoIndex: 3, damageRateColumn: 0, damageFixedColumn: 3, effectIndex: 1, effectFrame: 0),
                    new SkillStageSpec(motionIndex: 5, attackInfoIndex: 7, damageRateColumn: 1, damageFixedColumn: 4, effectIndex: 2, effectFrame: 0),
                },
                slideSpeedIndex: 0, slideAccelIndex: 1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 3, slideFollowsHeldDirection: false),

            // 波系技能的第一个(状态 0x18,证据 E-135):施放动作是 [etc motion] 第 9 项,第 0 帧是蓄力占位(施法时间减去第 1 到 3 帧时长后放行),
            // 蓄力一放行就在主角身上放预载表第 0 个特效(进状态时建好、放行时挂上);动画播到第 3 帧放一个被动对象(编号在下面的对象表里;主角脚下、朝向同主角),
            // 对象伤害取 [level info] 的百分比第 2 列、固定值第 0 列;[static data] 第 1、2 个数是起滑速度与加速度,不看方向键。
            // 主角自己的动画不命中(状态里没有设攻击信息);对象的大小比率(第 1 列)与施法速度还没做。
            new SkillRecipe(20,
                new[]
                {
                    new SkillStageSpec(motionIndex: 9, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: 0, effectFrame: 1, castHoldFrames: 3),
                },
                slideSpeedIndex: 1, slideAccelIndex: 2, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1, slideFollowsHeldDirection: false,
                objects: new[]
                {
                    new SkillObjectSpec(objectId: 20001, stage: 0, frame: 3, damageRateColumn: 2, damageFixedColumn: 0),
                }),

            // 挂多个特效、对象等特效关键帧的技能(状态 0x23,证据 E-137):施放动作是 [etc motion] 第 25 项,这个动作里一个攻击框也没有(状态码设的攻击信息没有框可打),
            // 所以主角的身体不打人,伤害全在放出的被动对象上。一进状态就把预载表的四个特效各放进一个图层(都跟着主角);
            // 动画播到第 6 帧以后、第 1 个特效播到关键帧标志 1 的那一帧,放一个被动对象:主角前方 85 像素、离地 82,朝向同主角,
            // 编号是基础职业的那个(状态码按职业判断二选一,另一个职业的没做),伤害取 [level info] 第 1 列(百分比)与第 7 列(固定值);
            // 动画播到第 10 帧的开头状态就结束。不滑行(进入块把速度清零)。
            // 没做:放到一半再按一次技能键的续放(状态里有一条"关键帧标志 1 之后再按键"的分支,含义没读透)、对象按 [level info] 其余列放大、被动技能加成与音效。
            new SkillRecipe(64,
                new[]
                {
                    new SkillStageSpec(motionIndex: 25, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: 0, effectFrame: 0,
                        moreEffects: new[] { 1, 2, 3 }, endFrame: 10),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1, slideFollowsHeldDirection: false,
                objects: new[]
                {
                    new SkillObjectSpec(objectId: 20028, stage: 0, frame: 6, damageRateColumn: 1, damageFixedColumn: 7, forwardX: 85, offsetY: 0, offsetZ: 82,
                        afterEffect: 1, afterEffectFlag: 1),
                }),

            // 蓄力跳跃类技能(状态 0x24,证据 E-138):第 0 段是蓄力姿势(动作取 [etc motion] 第 26 项,没有命中),第 1 段是跳跃(第 27 项,
            // 一进来就放预载表第 0、1 个特效)。蓄力时长取 [static data] 第 0、1 个数,距离第 2、3、高度第 4、5、动画变速百分比第 6、7、命中窗口数第 8、9;
            // 跳跃动画的第 2 帧开头是上升结束、第 4 帧开头落地、第 3 到第 5 帧是命中窗口;第一个窗口(窗口数大于 1 时)用 [etc attack info] 第 25 项,
            // 重武装的窗口用第 26 项;伤害取 [level info] 的百分比第 0 列(没有固定值列)。落地震屏按窗口数分三档(见 RoomCombatSession.HopLandingShake)。
            // 没做:跳跃特效按蓄力档位放大(渲染层没有特效缩放)、两个被动技能带来的落地冲击对象与额外伤害、特定职业的特效放大、
            // 重武装计时器响的那一刻那条受保护代码里的判断(当作成立)、起手函数里的先决检查。
            new SkillRecipe(65,
                new[]
                {
                    new SkillStageSpec(motionIndex: 26, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0),
                    new SkillStageSpec(motionIndex: 27, attackInfoIndex: 25, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: 0, effectFrame: 0,
                        moreEffects: new[] { 1 }),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                hop: new SkillHopSpec(holdMinIndex: 0, holdMaxIndex: 1, distanceMinIndex: 2, distanceMaxIndex: 3, heightMinIndex: 4, heightMaxIndex: 5,
                    speedMinIndex: 6, speedMaxIndex: 7, hitsMinIndex: 8, hitsMaxIndex: 9, riseFrame: 2, landFrame: 4, hitStartFrame: 3, hitEndFrame: 5,
                    repeatAttackInfoIndex: 26)),

            // 单段位移攻击类技能(状态 0x1d,证据 E-139):施放动作是 [etc motion] 第 11 项,攻击信息取 [etc attack info] 第 14 项,
            // 伤害取 [level info] 的百分比第 0 列与固定值第 2 列。起滑:没按方向键取 [static data] 头两个数(速度、加速度),
            // 按着朝向一侧的方向键换成第 2、3 个数(替换,不是加成),按着别的方向不滑。
            // 多段命中:学过一个被动技能(不在 PVP 模式)时,进入块起一个计时器,间隔 = 整段动画时长 / [static data] 第 4 个数,次数就是这个数;
            // 每次到点调虚表槽 0x208 清命中名单,同一个目标能再挨一下。
            // 没做:状态码在动画播到第 1 帧时挂两个特效(一个垫在主角下面、一个盖在上面),但它们是按写死的名字载入的,不在预载表里,名字不入库,所以这里不放特效;
            // [level info] 第 1 列(客户端乘 0.1 写进主角的一个浮点字段,用途没读到)、某个成长类型才有的额外被动对象、技能音效。
            new SkillRecipe(11,
                new[]
                {
                    new SkillStageSpec(motionIndex: 11, attackInfoIndex: 14, damageRateColumn: 0, damageFixedColumn: 2, effectIndex: -1, effectFrame: 0),
                },
                slideSpeedIndex: 0, slideAccelIndex: 1, forwardSpeedIndex: 2, forwardAccelIndex: 3, chainFromFrame: 1, forwardReplacesBase: true,
                multiHitPassiveSkillId: 162, multiHitCountIndex: 4),
        },
        new[]
        {
            // 增益类技能(状态 0x11,证据 E-141):施法动作都是 [buff motion],状态结束钩子里按技能编号分支挂附加体。
            // 跳跃力类:附加体类型 8,数值取 [static data] 第 0 个数、时长取 [level info] 第 0 列。
            new BuffRecipe(175, BuffStat.JumpPower, durationColumn: 0, amountStaticIndex: 0, amountColumn: -1),

            // 魔法攻击力类:附加体类型 1,时长取 [level info] 第 0 列、数值取第 1 列。
            new BuffRecipe(176, BuffStat.MagicalAttack, durationColumn: 0, amountStaticIndex: -1, amountColumn: 1),

            // 概率触发类(E-142):专门的附加体类(不走属性号);[level info] 第 0 列是千分比的概率(客户端乘 0.1 存进主角的一个浮点字段,
            // 主角受击前的钩子里再乘 100 与 [0, 10000) 的随机数比,即概率 = 列值 / 1000)、第 1 列是持续毫秒。效果是被打时有这个概率让这一击不产生受击反应(霸体)。
            new BuffRecipe(180, BuffStat.SuperArmorChance, durationColumn: 1, amountStaticIndex: -1, amountColumn: 0, amountScale: 0.001),
            // 没做:另一个走状态 0x32 的增益(另一套代码)、增益的特效与音效、增益图标。
        });
    }
}
