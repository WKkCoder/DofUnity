using System;
using System.Collections.Generic;

// 证据:E-129(客户端原生技能状态码的读码结果:技能起手函数、状态进入块、逐帧块里的连击判定);E-135(波系技能状态 0x18:施法蓄力与主角放出的被动对象)。
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
            int castHoldFrames = 0)
        {
            MotionIndex = motionIndex;
            AttackInfoIndex = attackInfoIndex;
            DamageRateColumn = damageRateColumn;
            DamageFixedColumn = damageFixedColumn;
            EffectIndex = effectIndex;
            EffectFrame = effectFrame;
            RequiresSkill = requiresSkill;
            CastHoldFrames = castHoldFrames < 0 ? 0 : castHoldFrames;
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

        /// <summary>特效在这一段的动画播到第几帧(从 0 数)时放;0 就是这一段一开始就放。</summary>
        public int EffectFrame { get; }

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
    /// 命中用对象自己 .obj 的 [attack info]。
    /// </summary>
    public sealed class SkillObjectSpec
    {
        public SkillObjectSpec(int objectId, int stage, int frame, int damageRateColumn, int damageFixedColumn, int forwardX = 0, int offsetY = 0, int offsetZ = 0)
        {
            ObjectId = objectId;
            Stage = stage;
            Frame = frame < 0 ? 0 : frame;
            DamageRateColumn = damageRateColumn;
            DamageFixedColumn = damageFixedColumn;
            ForwardX = forwardX;
            OffsetY = offsetY;
            OffsetZ = offsetZ;
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
            int chainFromFrame, bool slideFollowsHeldDirection = true, IEnumerable<SkillObjectSpec> objects = null)
        {
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

        /// <summary>当前段的动画播到第几帧(从 0 数)以后,再按同一个技能键才算接下一段。</summary>
        public int ChainFromFrame { get; }

        /// <summary>
        /// 进入一段时的起滑要不要看按着的方向键(证据 E-129、E-131):true 时按"没按/顺着按/按别的方向就不滑"算;
        /// false 时不看方向键,每一段都按基础的两个数朝着主角的朝向滑(状态进入块里没有读按住的方向)。
        /// </summary>
        public bool SlideFollowsHeldDirection { get; }

        /// <summary>这个技能放出的被动对象(证据 E-135);没有为空表。</summary>
        public IReadOnlyList<SkillObjectSpec> Objects { get; }
    }

    /// <summary>
    /// 技能配方表。<see cref="Default"/> 是读码移植出来的技能(证据 E-129);测试可以自己造一张。
    /// 表里的数都是客户端状态码里的常量(下标、列号),不含 PVF 里的数据值,也不含任何文件名——文件名按下标到角色脚本与技能预载表里取。
    /// </summary>
    public sealed class SkillRecipes
    {
        private readonly Dictionary<int, SkillRecipe> _byId = new Dictionary<int, SkillRecipe>();

        public SkillRecipes(IEnumerable<SkillRecipe> recipes)
        {
            foreach (SkillRecipe r in recipes ?? throw new ArgumentNullException(nameof(recipes)))
            {
                _byId[r.SkillId] = r;
            }
        }

        /// <summary>配方的个数。</summary>
        public int Count => _byId.Count;

        /// <summary>全部配方(检查表本身的结构用)。</summary>
        public IEnumerable<SkillRecipe> All => _byId.Values;

        /// <summary>这个技能的配方;没有返回 null。</summary>
        public SkillRecipe Find(int skillId)
        {
            return _byId.TryGetValue(skillId, out SkillRecipe r) ? r : null;
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
        });
    }
}
