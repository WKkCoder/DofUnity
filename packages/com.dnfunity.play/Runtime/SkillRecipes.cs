using System;
using System.Collections.Generic;
using Dnf.Game;

// 证据:E-129(客户端原生技能状态码的读码结果:技能起手函数、状态进入块、逐帧块里的连击判定);E-135(波系技能状态 0x18:施法蓄力与主角放出的被动对象);
// E-137(挂多个特效、对象等特效关键帧的那一类技能,状态 0x23:一进状态就挂四个特效、被动对象等特效动画的关键帧标志、帧号到数就结束状态);
// E-138(蓄力后跳起来砸下去的那一类技能,状态 0x24:按住技能键蓄力、松开或蓄满才出招,跳跃距离与高度按蓄力档位插值,位移由动画时间脚本化,命中窗口由计时器重武装);
// E-139(单段位移攻击类技能,状态 0x1d:起滑的"顺着按"两个数替换基础两个数、学了某个被动技能后命中名单按计时器多次重新武装);
// E-141(增益类技能,状态 0x11:施法动作只播 [buff motion],状态正常结束时才挂属性附加体,持续时间与数值取自技能数据的哪一列由技能自己的挂附加体代码决定);
// E-149(腾空悬停后俯冲类技能,状态 0x19:只在跳跃状态里放,动画定在空中攻击动作的最后一帧,悬停一小段后水平与竖直一起冲下去,落地按起跳高度放脚下的冲击对象,再进坐姿);
// E-154(两段自动接续类技能,状态 0x20:第一段动画播完、不用再按键就接第二段,第二段一开始放一个主角的被动对象,身体只在第一段里打人);
// E-162(单段身体命中类技能,状态 0x41 与 0x42:进入块设好动作、攻击信息、伤害列与特效,动画播完状态结束);
// E-163(关键帧标志驱动的命中窗口:第二个逐帧函数 0x00cb0700 读动画此刻这一帧的标志字,对上就清命中名单、换攻击信息与伤害列;状态 0x40 的"姿势→升空挥击":第 0 帧停住、主角按 [static data] 脚本升空与后滑;冷却取 [cooltime level info] 指的 [level info] 列)。
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
            int castHoldFrames = 0, int[] moreEffects = null, int endFrame = -1, SkillFlagEvent[] flagEvents = null, SkillRiseSpec rise = null)
        {
            var keyEvents = new List<SkillFlagEvent>();
            if (flagEvents != null)
            {
                keyEvents.AddRange(flagEvents);
            }
            FlagEvents = keyEvents.AsReadOnly();
            Rise = rise;
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

        /// <summary>
        /// 这一段的关键帧标志事件(M65,证据 E-163):动画走到带对应标志的帧时清命中名单、可选地换攻击信息与伤害列(<see cref="SkillFlagEvent"/>);
        /// 没有为空表。同一个标志出现多次时取表里第一项。
        /// </summary>
        public IReadOnlyList<SkillFlagEvent> FlagEvents { get; }

        /// <summary>这一段是脚本升空(M65,证据 E-163,<see cref="SkillRiseSpec"/>);不是为 null。</summary>
        public SkillRiseSpec Rise { get; }
    }

    /// <summary>
    /// 关键帧标志事件(M65,证据 E-163):技能的逐帧代码每步读动画此刻这一帧的标志字,对上就做事。三个已读的技能里做的事都是同一类:
    /// 清命中名单(同一个目标能再挨一下),以及(有的)换攻击信息与伤害列(之后的命中用新的)。
    /// <see cref="AttackInfoIndex"/>(角色 [etc attack info] 的下标)、<see cref="DamageRateColumn"/>、<see cref="DamageFixedColumn"/>(.skl [level info] 的列)
    /// 为 -1 的项沿用这一段原来的。
    /// </summary>
    public sealed class SkillFlagEvent
    {
        public SkillFlagEvent(int flag, int attackInfoIndex = -1, int damageRateColumn = -1, int damageFixedColumn = -1)
        {
            Flag = flag;
            AttackInfoIndex = attackInfoIndex;
            DamageRateColumn = damageRateColumn;
            DamageFixedColumn = damageFixedColumn;
        }

        /// <summary>动画帧上的关键帧标志值。</summary>
        public int Flag { get; }

        /// <summary>事件之后命中用的攻击信息(角色 [etc attack info] 的下标);-1 沿用这一段原来的。</summary>
        public int AttackInfoIndex { get; }

        /// <summary>事件之后命中用的伤害百分比列;-1 沿用这一段原来的。</summary>
        public int DamageRateColumn { get; }

        /// <summary>事件之后命中用的伤害固定值列;-1 沿用这一段原来的。</summary>
        public int DamageFixedColumn { get; }

        /// <summary>事件换了攻击信息或伤害列(只清命中名单的事件为 false)。</summary>
        public bool ChangesStrike => AttackInfoIndex >= 0 || DamageRateColumn >= 0 || DamageFixedColumn >= 0;
    }

    /// <summary>
    /// 脚本升空的配方参数(M65,证据 E-163):三个数都是技能 [static data] 的下标(取值本身在技能数据里,不进代码)——
    /// 升空高度(像素)、升空时间(毫秒,动画第 0 帧停留这么久)、向后滑的距离(像素)。运行期翻成 <see cref="Dnf.Render.SkillRisePlan"/>。
    /// </summary>
    public sealed class SkillRiseSpec
    {
        public SkillRiseSpec(int heightStaticIndex, int durationStaticIndex, int backStaticIndex)
        {
            HeightStaticIndex = heightStaticIndex;
            DurationStaticIndex = durationStaticIndex;
            BackStaticIndex = backStaticIndex;
        }

        /// <summary>[static data] 里升空高度的下标。</summary>
        public int HeightStaticIndex { get; }

        /// <summary>[static data] 里升空时间(毫秒)的下标。</summary>
        public int DurationStaticIndex { get; }

        /// <summary>[static data] 里后滑距离的下标。</summary>
        public int BackStaticIndex { get; }
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
            int afterEffect = -1, int afterEffectFlag = 1, bool onLanding = false)
        {
            OnLanding = onLanding;
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

        /// <summary>
        /// 不按动画帧放,而是在俯冲落地那一刻放(<see cref="SkillRecipe.Dive"/>,证据 E-149):放的位置是主角脚下(三个偏移都是 0)、朝向同主角;
        /// 只有离地高度够了才放(见 <see cref="SkillDiveSpec.ImpactHeightIndex"/>)。这类对象的 <see cref="Stage"/>、<see cref="Frame"/> 不起作用。
        /// </summary>
        public bool OnLanding { get; }
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
    /// 腾空悬停后俯冲类技能的特别部分(状态 0x19,证据 E-149)。放法:只能在跳跃状态里放(腾空、没有在空中挥击);
    /// 放出来那一刻水平与竖直速度清零,动画定在角色脚本里的空中攻击动作(没有职业脚本函数改它时就是 [jumpattack motion])的最后一帧,身体一直保持这个姿势;
    /// 过了 <see cref="HoverMs"/> 毫秒(状态计时器)开始俯冲:水平沿朝向以 <see cref="SpeedX"/>(引擎速度表的数,与别的技能一样乘移动速度)匀速冲,
    /// 竖直速度一次设成 <see cref="SpeedZ"/> × 移动速度属性千分比 / 1000(向下为负),之后照常受重力;同时在主角身上放预载表的第 <see cref="EffectIndex"/> 个特效。
    /// 俯冲期间主角的攻击框一直是那一帧(武器图层动画里)的;命中名单只在多段命中的被动技能打开时才重武装,基础职业没有,所以一个目标只挨一下。
    /// 落地:放出来那一刻的离地高度不小于 [static data] 第 <see cref="ImpactHeightIndex"/> 个数,就在脚下放配方对象表里标了落地的被动对象
    /// (<see cref="SkillObjectSpec.OnLanding"/>);不论放没放,落地后都进入坐姿([sit motion]),播完回到站立。
    /// 常数(200、470、−900、0.001)是状态码里写死的引擎常量,下标也是;没有的:另一个职业成长类型的伤害加成、第二段命中的被动技能带来的次数、落地震屏(状态码里没找到)。
    /// </summary>
    public sealed class SkillDiveSpec
    {
        public SkillDiveSpec(int hoverMs, int speedX, int speedZ, int effectIndex, int impactHeightIndex)
        {
            HoverMs = hoverMs < 0 ? 0 : hoverMs;
            SpeedX = speedX;
            SpeedZ = speedZ;
            EffectIndex = effectIndex;
            ImpactHeightIndex = impactHeightIndex;
        }

        /// <summary>放出来之后悬停多久(毫秒)开始俯冲。</summary>
        public int HoverMs { get; }

        /// <summary>俯冲的水平速度(引擎速度表的数,乘移动速度换成像素/秒)。</summary>
        public int SpeedX { get; }

        /// <summary>俯冲的竖直速度(像素/秒,向下为负),再乘移动速度属性的千分比 / 1000。</summary>
        public int SpeedZ { get; }

        /// <summary>俯冲开始时在主角身上放的特效,在技能预载表里的下标;负数表示不放。</summary>
        public int EffectIndex { get; }

        /// <summary>[static data] 里"落地放对象所需的最小离地高度(像素)"的下标。</summary>
        public int ImpactHeightIndex { get; }
    }

    /// <summary>
    /// 空中攻击强化类技能的特别部分(客户端状态 7,即跳跃攻击状态,带一个技能号参数;证据 E-150)。放法:在腾空的跳跃状态里按攻击键(普通的空中攻击键),
    /// 技能学过、魔法值够就把这一击换成技能:动作不再是 [jumpattack motion],而是 [etc motion] 里两项交替(<see cref="MotionIndexA"/>、<see cref="MotionIndexB"/>)——
    /// 客户端有一个"上一次放了哪种"的字段(-1 没放过、0 刚放了第一种、1 刚放了第二种),下一次放的种类是:字段为 0 就放第二种,否则第一种;
    /// 每放一次就把字段改成这次的种类,所以两种动作交替,而且跨跳跃保留(字段只在主角对象创建时与一个我们没读通的特殊状态里清回 -1)。
    /// 命中用空中攻击自己的攻击信息(进入块里没有换攻击信息),伤害换成技能的百分比(.skl [level info] 当前等级的 <see cref="SkillStageSpec.DamageRateColumn"/> 列,
    /// 取配方第 0 段的列;客户端把"这一列减 100"存成伤害加成,我们与别的技能一致按这一列当总百分比)。
    /// 特效:动画播到第 <see cref="EffectFrame"/> 帧时放一次,第一种放 <see cref="EffectsA"/> 里的预载特效,第二种放 <see cref="EffectsB"/> 里的。
    /// 下标与帧号都是客户端状态码里写死的常量,不是数据值。没有的:某个成长类型学了别的技能时的伤害加成与速度调整、普通空中攻击在学了这个技能后多出的一个特效、
    /// 特效的图层(上下两层)与播放倍率、命中后的成长类型专属附加效果、对战模式的变体。
    /// </summary>
    public sealed class SkillJumpAttackSpec
    {
        public SkillJumpAttackSpec(int motionIndexA, int motionIndexB, int effectFrame, IEnumerable<int> effectsA, IEnumerable<int> effectsB)
        {
            MotionIndexA = motionIndexA;
            MotionIndexB = motionIndexB;
            EffectFrame = effectFrame < 0 ? 0 : effectFrame;
            EffectsA = new List<int>(effectsA ?? Array.Empty<int>()).AsReadOnly();
            EffectsB = new List<int>(effectsB ?? Array.Empty<int>()).AsReadOnly();
        }

        /// <summary>第一种动作在 [etc motion] 里的下标。</summary>
        public int MotionIndexA { get; }

        /// <summary>第二种动作在 [etc motion] 里的下标。</summary>
        public int MotionIndexB { get; }

        /// <summary>特效在动画播到第几帧(从 0 数)时放。</summary>
        public int EffectFrame { get; }

        /// <summary>第一种动作放的预载特效下标。</summary>
        public IReadOnlyList<int> EffectsA { get; }

        /// <summary>第二种动作放的预载特效下标。</summary>
        public IReadOnlyList<int> EffectsB { get; }
    }

    /// <summary>
    /// 一个多段地面技能的放法(证据 E-129):各段(<see cref="Stages"/>)、起滑速度与加速度取 .skl [static data] 的哪几个数、接下一段的条件。
    /// 放法:起手放第 0 段;这一段的动画播过 <see cref="ChainFromFrame"/> 帧之后再按一次同一个技能键,就换成下一段(不再扣蓝、不再进冷却)。
    /// 滑行:起滑速度与加速度取 [static data] 的第 <see cref="SlideSpeedIndex"/>、<see cref="SlideAccelIndex"/> 个数;进入每一段那一刻看方向键——
    /// 没按方向键用这两个数,按着朝向一侧的方向键再加上第 <see cref="ForwardSpeedIndex"/>、<see cref="ForwardAccelIndex"/> 个数,按着别的方向(反向、上下)就不滑。
    /// 标了 <see cref="AutoChain"/> 的配方不看按键:每一段的动画播完就自己接下一段(证据 E-154)。
    /// </summary>
    public sealed class SkillRecipe
    {
        public SkillRecipe(int skillId, IEnumerable<SkillStageSpec> stages, int slideSpeedIndex, int slideAccelIndex, int forwardSpeedIndex, int forwardAccelIndex,
            int chainFromFrame, bool slideFollowsHeldDirection = true, IEnumerable<SkillObjectSpec> objects = null, SkillHopSpec hop = null,
            bool forwardReplacesBase = false, int multiHitPassiveSkillId = 0, int multiHitCountIndex = -1, SkillDiveSpec dive = null,
            SkillJumpAttackSpec jumpAttack = null, bool autoChain = false, IEnumerable<SkillPassivePowerLink> passivePowerLinks = null)
        {
            if (dive != null && hop != null)
            {
                throw new ArgumentException("一个配方不能既是蓄力跳跃又是悬停俯冲", nameof(dive));
            }
            if (jumpAttack != null && (hop != null || dive != null))
            {
                throw new ArgumentException("空中攻击强化类的配方不能同时是蓄力跳跃或悬停俯冲", nameof(jumpAttack));
            }
            if (autoChain && (hop != null || dive != null || jumpAttack != null))
            {
                throw new ArgumentException("自动接续的配方不能同时是蓄力跳跃、悬停俯冲或空中攻击强化", nameof(autoChain));
            }
            Dive = dive;
            JumpAttack = jumpAttack;
            AutoChain = autoChain;
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
            PassivePowerLinks = new List<SkillPassivePowerLink>(passivePowerLinks ?? Array.Empty<SkillPassivePowerLink>()).AsReadOnly();
            Hop = hop;
            if (hop != null && Stages.Count < 2)
            {
                throw new ArgumentException("蓄力跳跃的配方要有蓄力与跳跃两段", nameof(stages));
            }
            if (autoChain && Stages.Count < 2)
            {
                throw new ArgumentException("自动接续的配方至少要有两段", nameof(stages));
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
        /// 自动接续(证据 E-154,状态 0x20):这一段的动画播完(本来要结束技能的那一刻)就自己换成下一段,不看按键,再按技能键也不接段
        /// (<see cref="ChainFromFrame"/> 不起作用)。接段与按键接段一样不扣蓝、不进冷却、算新的一击;中途被打断就不接了。最后一段播完技能才结束。
        /// </summary>
        public bool AutoChain { get; }

        /// <summary>
        /// 进入一段时的起滑要不要看按着的方向键(证据 E-129、E-131):true 时按"没按/顺着按/按别的方向就不滑"算;
        /// false 时不看方向键,每一段都按基础的两个数朝着主角的朝向滑(状态进入块里没有读按住的方向)。
        /// </summary>
        public bool SlideFollowsHeldDirection { get; }

        /// <summary>这个技能放出的被动对象(证据 E-135);没有为空表。</summary>
        public IReadOnlyList<SkillObjectSpec> Objects { get; }

        /// <summary>被动技能对威力列的倍率加成(E-181);没有为空表。</summary>
        public IReadOnlyList<SkillPassivePowerLink> PassivePowerLinks { get; }

        /// <summary>
        /// 蓄力跳跃类技能的参数(证据 E-138);不是这一类技能为 null。有它的配方第 0 段是蓄力姿势、第 1 段是跳跃,
        /// 两段之间由会话按"松开或蓄满"自动衔接(再按技能键不接段)。
        /// </summary>
        public SkillHopSpec Hop { get; }

        /// <summary>
        /// 悬停俯冲类技能的参数(证据 E-149);不是这一类技能为 null。有它的配方只有一段(攻击信息与伤害列取这一段,<see cref="SkillStageSpec.MotionIndex"/> 不用,
        /// 动画是空中攻击动作),只能在跳跃状态里放(<see cref="HeroSkills.Cast"/> 走 <see cref="TownHero.BeginSkillDive"/>)。
        /// </summary>
        public SkillDiveSpec Dive { get; }

        /// <summary>
        /// 空中攻击强化类技能的参数(证据 E-150);不是这一类技能为 null。有它的配方只有一段占位(伤害百分比列取这一段的
        /// <see cref="SkillStageSpec.DamageRateColumn"/>、固定值列取 <see cref="SkillStageSpec.DamageFixedColumn"/>,没有自己的攻击信息:命中沿用空中攻击的攻击信息),
        /// 由攻击键在空中接管(<see cref="HeroSkills.TakeOverJumpAttack"/>)或快捷键直接放(<see cref="HeroSkills.Cast"/> 走 <see cref="TownHero.BeginJumpAttackSkill"/>)。
        /// </summary>
        public SkillJumpAttackSpec JumpAttack { get; }
    }

    /// <summary>
    /// 增益类技能的放法(状态 0x11,证据 E-141):主角原地播 [buff motion],动画播完(状态正常结束)那一刻才挂上一个属性增益——
    /// 改哪个属性是 <see cref="Stat"/>,持续时间(毫秒)取 .skl [level info] 当前等级的 <see cref="DurationColumn"/> 列,
    /// 数值取 [static data] 的第 <see cref="AmountStaticIndex"/> 个数(不小于 0 时)或 [level info] 当前等级的 <see cref="AmountColumn"/> 列(否则)。
    /// 下标与列号都是客户端挂附加体的代码里写死的常量,不是数据值。耗蓝与冷却同别的技能,在放出来(进入施法状态)那一刻算(推断)。
    /// </summary>
    public sealed class BuffRecipe
    {
        public BuffRecipe(int skillId, BuffStat stat, int durationColumn, int amountStaticIndex, int amountColumn, double amountScale = 1.0, int guardSkillId = 0)
        {
            SkillId = skillId;
            Stat = stat;
            DurationColumn = durationColumn;
            AmountStaticIndex = amountStaticIndex;
            AmountColumn = amountColumn;
            AmountScale = amountScale;
            GuardSkillId = guardSkillId;
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

        /// <summary>
        /// 自动防御类增益(<see cref="BuffStat.AutoGuardChance"/>)借用的防御姿态技能编号:挡下时的穿过百分比、姿势动作、最短保持与后滑都取那个技能的数据,
        /// 那个技能没学会就不触发(客户端钩子里写死的常量);别的增益为 0。
        /// </summary>
        public int GuardSkillId { get; }
    }

    /// <summary>
    /// 防御姿态类技能的放法(状态 0x13,证据 E-164):按住攻击键维持的姿势——主角原地播 [etc motion] 第 <see cref="MotionIndex"/> 项
    /// (两帧、每帧停很久:姿态阶段定在第 0 帧,挡下一击后的后滑阶段定在第 1 帧),姿态里迎面挨到能挡的一击就挡下:
    /// 伤害乘 (100 − .skl [level info] 的第 <see cref="PhysicalColumn"/> 列 / <see cref="MagicalColumn"/> 列) 百分之一(物理/魔法两种攻击类型各取一列,无视防御的一击不折算),
    /// 不产生受击反应,主角沿朝向的反方向后滑——速度取 [level info] 第 <see cref="RecoilSpeedColumn"/> 列、持续毫秒取第 <see cref="RecoilMsColumn"/> 列;
    /// 进入时最短保持毫秒取 [static data] 第 <see cref="MinHoldStaticIndex"/> 个数。下标与列号都是客户端状态码里写死的常量,不是数据值。
    /// 耗蓝与冷却同别的技能,在放出来那一刻算(推断)。
    /// </summary>
    public sealed class GuardRecipe
    {
        public GuardRecipe(int skillId, int motionIndex, int minHoldStaticIndex, int recoilSpeedColumn, int recoilMsColumn, int physicalColumn, int magicalColumn)
        {
            SkillId = skillId;
            MotionIndex = motionIndex;
            MinHoldStaticIndex = minHoldStaticIndex;
            RecoilSpeedColumn = recoilSpeedColumn;
            RecoilMsColumn = recoilMsColumn;
            PhysicalColumn = physicalColumn;
            MagicalColumn = magicalColumn;
        }

        public int SkillId { get; }

        /// <summary>姿势动作在 [etc motion] 里的下标。</summary>
        public int MotionIndex { get; }

        /// <summary>最短保持毫秒在 [static data] 里的下标。</summary>
        public int MinHoldStaticIndex { get; }

        /// <summary>挡下后后滑的起滑速度(像素/秒)在 [level info] 里的列。</summary>
        public int RecoilSpeedColumn { get; }

        /// <summary>挡下后后滑的持续毫秒在 [level info] 里的列。</summary>
        public int RecoilMsColumn { get; }

        /// <summary>物理攻击折算用的列:穿过的百分比 = 100 − 这一列的值。</summary>
        public int PhysicalColumn { get; }

        /// <summary>魔法攻击折算用的列:穿过的百分比 = 100 − 这一列的值。</summary>
        public int MagicalColumn { get; }
    }

    /// <summary>
    /// 技能配方表。<see cref="Default"/> 是读码移植出来的技能(证据 E-129);测试可以自己造一张。
    /// 表里的数都是客户端状态码里的常量(下标、列号),不含 PVF 里的数据值,也不含任何文件名——文件名按下标到角色脚本与技能预载表里取。
    /// 增益类技能(<see cref="BuffRecipe"/>)与防御姿态类技能(<see cref="GuardRecipe"/>)各放一张表:它们没有多段动作与攻击信息,只有一个动作。
    /// </summary>
    public sealed class SkillRecipes
    {
        private readonly Dictionary<int, SkillRecipe> _byId = new Dictionary<int, SkillRecipe>();
        private readonly Dictionary<int, BuffRecipe> _buffs = new Dictionary<int, BuffRecipe>();
        private readonly Dictionary<int, GuardRecipe> _guards = new Dictionary<int, GuardRecipe>();
        private readonly Dictionary<int, BuffRecipe> _classBuffs = new Dictionary<int, BuffRecipe>();

        public SkillRecipes(IEnumerable<SkillRecipe> recipes, IEnumerable<BuffRecipe> buffs = null, IEnumerable<GuardRecipe> guards = null,
            IEnumerable<BuffRecipe> classBuffs = null)
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
            if (guards != null)
            {
                foreach (GuardRecipe g in guards)
                {
                    _guards[g.SkillId] = g;
                }
            }
            if (classBuffs != null)
            {
                foreach (BuffRecipe b in classBuffs)
                {
                    _classBuffs[b.SkillId] = b;
                }
            }
        }

        /// <summary>
        /// 职业专属增益的个数。客户端里增益有两条挂法:主角基类的起手(<c>0x0085deb0</c>)按技能号挂的通用附加体(<see cref="AllBuffs"/>,E-141、E-142),
        /// 和职业自己的起手覆盖(鬼剑士的 <c>0x00ce2290</c>)里按技能号挂的职业专属附加体类——后者单放一张表,和前者的统计口径分开。
        /// </summary>
        public int ClassBuffCount => _classBuffs.Count;

        /// <summary>全部职业专属增益的放法(<see cref="AllBuffs"/> 不含它们)。</summary>
        public IEnumerable<BuffRecipe> AllClassBuffs => _classBuffs.Values;

        /// <summary>这个技能的职业专属增益放法;不是返回 null。</summary>
        public BuffRecipe FindClassBuff(int skillId)
        {
            return _classBuffs.TryGetValue(skillId, out BuffRecipe b) ? b : null;
        }

        /// <summary>防御姿态类技能的个数。</summary>
        public int GuardCount => _guards.Count;

        /// <summary>全部防御姿态类技能的放法。</summary>
        public IEnumerable<GuardRecipe> AllGuards => _guards.Values;

        /// <summary>这个技能的防御姿态放法;不是防御姿态类技能返回 null。</summary>
        public GuardRecipe FindGuard(int skillId)
        {
            return _guards.TryGetValue(skillId, out GuardRecipe g) ? g : null;
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

            // 波系技能的第二个(状态 0x18,证据 E-135/E-232):与第一个同族进入块 0xcd19ec、逐帧 0xcbb54f;施法动作暂与第一个共用 [etc motion] 第 9 项(第 10 项首帧非蓄力占位,skill-check 不通过),
            // 对象帧常量为 4;伤害列与第一个相同族(百分比第 2 列、固定值第 0 列);基础成长类型的对象编号取进入块二选一里的那一支(非 PVP)。
            new SkillRecipe(21,
                new[]
                {
                    new SkillStageSpec(motionIndex: 9, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0, castHoldFrames: 3),
                },
                slideSpeedIndex: 0, slideAccelIndex: 1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1, slideFollowsHeldDirection: false,
                objects: new[]
                {
                    new SkillObjectSpec(objectId: 20002, stage: 0, frame: 4, damageRateColumn: 2, damageFixedColumn: 0),
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

            // 腾空悬停后俯冲类技能(状态 0x19,证据 E-149):起手函数只在跳跃状态放(状态码进入块把动画定在空中攻击动作的最后一帧,没有 [etc motion] 的下标)。
            // 攻击信息取 [etc attack info] 第 10 项,伤害取 [level info] 的百分比第 0 列(没有固定值列);一进状态速度清零、悬停 200 毫秒,之后水平 470(引擎速度表的数)、
            // 竖直 −900(都乘移动速度的千分比)俯冲,同时放预载表的第 0 个特效;落地时离地高度够 [static data] 第 3 个数,就在脚下放被动对象 20016(非 PVP 模式的那个),
            // 伤害取 [level info] 的百分比第 2 列与固定值第 1 列。落地后进入坐姿。没做:某个成长类型学了另一个技能时落地伤害的放大、PVP 模式的对象、落地震屏、音效。
            new SkillRecipe(16,
                new[]
                {
                    new SkillStageSpec(motionIndex: -1, attackInfoIndex: 10, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                objects: new[]
                {
                    new SkillObjectSpec(objectId: 20016, stage: 0, frame: 0, damageRateColumn: 2, damageFixedColumn: 1, onLanding: true),
                },
                dive: new SkillDiveSpec(hoverMs: 200, speedX: 470, speedZ: -900, effectIndex: 0, impactHeightIndex: 3)),

            // 空中攻击强化类技能(状态 7 带技能号参数,证据 E-150):在跳跃状态里按攻击键,这一击换成 [etc motion] 第 23、24 项(两项交替,上一次放了哪项记在
            // 主角字段里);命中沿用空中攻击的攻击信息,伤害取 [level info] 的百分比第 0 列(没有固定值列);动画播到第 2 帧放一次预载表里的特效:第一种放第 4、6 个,
            // 第二种放第 5、7 个。PassivePowerLinks 未填(E-181):+0x5938 由 sq_AddPassiveSkillAttackBonusRate(0x689f70)在技能脚本侧登记,非默认表字段;保持惰性;显式 PassivePowerLinks 仍生效。
            // 没做:某个成长类型学了别的技能时的非威力列伤害加成与速度调整、普通空中攻击学了这个技能后多出的一个特效、特效图层与倍率、成长类型专属的命中附加效果、对战模式变体。
            new SkillRecipe(17,
                new[]
                {
                    new SkillStageSpec(motionIndex: -1, attackInfoIndex: -1, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                jumpAttack: new SkillJumpAttackSpec(motionIndexA: 23, motionIndexB: 24, effectFrame: 2, effectsA: new[] { 4, 6 }, effectsB: new[] { 5, 7 })),

            // 两段自动接续类技能(状态 0x20,证据 E-154):第一段是 [etc motion] 第 14 项(动画里带攻击框),攻击信息取 [etc attack info] 第 17 项,
            // 伤害取 [level info] 的百分比第 0 列(没有固定值列),一进状态就把预载表第 0、1 个特效放进两个图层;这一段播完不用再按键,客户端的逐帧块看到动画结束标志
            // 就换第二段:[etc motion] 第 15 项(整个动作里没有攻击框,所以身体不打人),放预载表第 2 个特效,并在主角脚下放被动对象(编号是基础职业的那个,
            // 状态码按职业判断二选一,另一个职业的没做),伤害取 [level info] 的百分比第 2 列与固定值第 3 列。两段都不滑行(进入块把两个滑行速度清零)。
            // 没做:对象创建参数里按两个引擎参数名查表得到的前向与离地偏移(表的来源没找到,按 0 放)、传给对象的 [static data] 第 1 个数(用途没读到)、
            // 第二段第 3 帧起的武器特效与声音、动画速度比率、第二段的 [level info] 第 1 列伤害(身体没有攻击框所以用不上)、不是"客户端权威"时的第一种变体。
            new SkillRecipe(58,
                new[]
                {
                    new SkillStageSpec(motionIndex: 14, attackInfoIndex: 17, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: 0, effectFrame: 0,
                        moreEffects: new[] { 1 }),
                    new SkillStageSpec(motionIndex: 15, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: 2, effectFrame: 0),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                objects: new[]
                {
                    new SkillObjectSpec(objectId: 20022, stage: 1, frame: 0, damageRateColumn: 2, damageFixedColumn: 3),
                },
                autoChain: true),

            // 单段身体命中类技能(状态 0x41,证据 E-162、E-163):进入块设好动作、攻击信息、伤害列与特效,命中一次;之后每步有两处逐帧逻辑——
            // 公共后段里的守卫块(0x00cc2961:动画播完发"状态 0"请求站立),和公共后段末尾调用的第二个逐帧函数 0x00cb0700(跳转表 0x00cb2068 里这个状态的分支在 0x00cb1e08):
            // 动画走到带关键帧标志 1 的帧,就清命中名单并把伤害列换成百分比第 1 列(攻击信息不变)——所以一次挥击有两个命中窗口,后一个窗口的伤害数不同。
            // 动作取 [etc motion] 第 120 项,攻击信息取 [etc attack info] 第 84 项,第一个窗口的伤害取 [level info] 的百分比第 0 列(没有固定值列);
            // 一进状态就把预载表第 0、1 个特效放进第 1、2 两个图层(都跟着主角);不滑行(进入块把两个轴的滑行速度都清零)。
            // 没做:进入块把主角的一个保护浮点字段(同一个字段在被动对象的创建参数里也有乘法,角色一侧的用途没读通)乘上 [static data] 第 1 个数的千分之一、
            // 按 [static data] 第 2 个数 / 100 设动画变速与特效缩放、非基础职业的音效与特效助手。
            new SkillRecipe(111,
                new[]
                {
                    new SkillStageSpec(motionIndex: 120, attackInfoIndex: 84, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: 0, effectFrame: 0,
                        moreEffects: new[] { 1 }, flagEvents: new[] { new SkillFlagEvent(flag: 1, damageRateColumn: 1) }),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false),

            // 单段身体命中类技能(状态 0x42,证据 E-162、E-163):同上(守卫块在 0x00cc2a4d;第二个逐帧函数里的分支在 0x00cb1e7b)。动作取 [etc motion] 第 121 项,
            // 攻击信息第 85 项,第一个窗口的伤害取 [level info] 的百分比第 0 列;关键帧标志 1:清命中名单(攻击信息、伤害列不变);标志 2:清命中名单并把攻击信息换成第 86 项、
            // 伤害换成百分比第 1 列——所以一次挥击有三个命中窗口。标志 9 只记一个时间戳给 120 毫秒的白闪(纯画面,没做)。没有预载特效
            // (进入块只挂两个写死参数的残影特效,不在预载表里);不滑行。没做:同一个保护浮点字段乘上 [static data] 第 0 个数的千分之一、残影与白闪、非基础职业的音效助手。
            new SkillRecipe(112,
                new[]
                {
                    new SkillStageSpec(motionIndex: 121, attackInfoIndex: 85, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0,
                        flagEvents: new[]
                        {
                            new SkillFlagEvent(flag: 1),
                            new SkillFlagEvent(flag: 2, attackInfoIndex: 86, damageRateColumn: 1),
                        }),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false),

            // 两段自动接续的"姿势→挥击"类技能(状态 0x40,证据 E-163):进入块按参数分两个模式。模式 0 播 [etc motion] 第 112 项(姿势动作,不设攻击信息,身体不打人);
            // 公共后段里守卫"当前状态 == 0x40"的块(比较在 0x00cc24ef)在这个动画播完那一刻(动画标志位 8)自己发"状态 0x40、参数 [1]、优先级 1",就是模式 1:
            // 动作取 [etc motion] 第 113 项,攻击信息取 [etc attack info] 第 78 项(进入块调的是虚表槽 0x778 的"动作加攻击信息"包装),伤害取 [level info] 的百分比第 0 列
            // (没有固定值列),命中只在这一段的开头判一次(默认武器类别:命中计时器的最多次数是 1,第一次响正好清空还没命中过的名单)。
            // 模式 1 的逐帧逻辑在第二个逐帧函数 0x00cb0700(跳转表 0x00cb2068 里这个状态的分支在 0x00cb1c56):动画第 0 帧(带攻击框,动画里写着很长的停留)停住不动,
            // 同时主角按 [static data] 的三个数脚本升空——升空高度(第 0 个数)、升空时间(第 1 个数,毫秒)、后滑距离(第 2 个数,像素):
            // 高度走缓出弧(0x011b9ef0 的缓出模式)、水平沿朝向的反方向整数线性后滑;升空时间一到把动画拨到第 1 帧的开头,之后动画播完剩下的帧。
            // 模式 1 的动画播完,同一个守卫块发"状态 6、参数 [1, 0, 0]"(状态 6 是腾空的跳跃状态,见 E-149):主角从当时的高度落下来。起手函数 0x0049ead0 只发模式 0。
            // 冷却不走 [cool time]:这个技能带 [cooltime level info] 1,取 [level info] 第 1 列(SkillDef.CoolTimeColumn,取列值当毫秒是推断)。
            // 没做:模式 0 把三个取值(虚表槽 0xa0 的前三项)存进主角的保护字段与非基础职业的音效、特效助手;按武器类别(虚表槽 0x7ac,值 1/5)命中计时器的最多次数改取 [static data] 第 5 个数、
            // 类别 3 的升空高度改乘第 8 个数的百分之一(几个类别各对应哪种武器没读通,这一版按默认类别);模式 1 的音效;状态 6 参数的含义(这一版是普通下落,落地后站立)。
            // 起手没有技能自己的状态要求:客户端的"能不能用"检查(0x0049c990 转 0x00854460)只排除跳跃、受击、倒地这几类状态。
            new SkillRecipe(109,
                new[]
                {
                    new SkillStageSpec(motionIndex: 112, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0),
                    new SkillStageSpec(motionIndex: 113, attackInfoIndex: 78, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0,
                        rise: new SkillRiseSpec(heightStaticIndex: 0, durationStaticIndex: 1, backStaticIndex: 2)),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                autoChain: true),

            // 单段身体命中类技能的另一个(状态 0x15,与 E-162、E-163 同一族):处理函数分派 0x004a02d0 里这个技能号走默认的空分支,起手不经起手函数,
            // 而在主角逐帧函数公共后段的按键块 0x00cc1c6d:守卫(0x00cc1bbc 起)要求本地主角,当前状态与待发状态都不是 0x15、9,也不是 4、5;
            // 键位检查(虚表槽 0x918 取快捷栏键信息,0x00cc1c7e)与"能不能用"检查(虚表槽 0x864,0x00cc1c8e)通过后发"状态 0x15、参数 [朝向, 标志]、优先级 2"(0x00cc1d7e)。
            // 进入块 0x00cd03c0 起:动作取 [etc motion] 第 2 项,攻击信息取 [etc attack info] 第 1 项,伤害取 [level info] 的百分比第 1 列
            // (调 0x00911dc0,返回值减 100.0 后存进主角的 +0x2f8;没有固定值列),两个轴的滑行速度都清零,没有预载特效、没有被动对象;
            // 逐帧分支(逐帧函数 0x00cb9980 按状态分派,字节表 0x00cca368、跳转表 0x00cca2dc,这个状态的分支在 0x00cbad0b)只做一件事:
            // 动画结束标志位(位 8)一亮就发"状态 0"回到站立——所以只有一段、没有接段帧。
            // 没做:参数里的标志(按块里的关联技能对象与一个计时器置位,进入块存进主角的 +0x6650,用途没读到)、进入块开头按两个格式串取的命名声音(虚表槽 0x154)、
            // 动画变速与方向的常量设置(1000、1.0,等同默认)、取伤害百分比时多传的虚表槽 0x238 返回值的影响(没读通)。
            new SkillRecipe(7,
                new[]
                {
                    new SkillStageSpec(motionIndex: 2, attackInfoIndex: 1, damageRateColumn: 1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false),
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
        },
        new[]
        {
            // 防御姿态类技能(状态 0x13,证据 E-164):动作取 [etc motion] 第 0 项(两帧,姿态阶段定第 0 帧、后滑阶段定第 1 帧);进入块的最短保持毫秒取 [static data] 第 0 个数;
            // 受击前钩子挡下一击时,物理攻击穿过的百分比 = 100 − [level info] 第 2 列、魔法攻击 = 100 − 第 3 列,后滑起滑速度取第 0 列、持续毫秒取第 1 列。
            // 没做:同一个状态码的另一个技能(阶段 1 带标志 1 并放被动对象)、挡下时的音效与特效(被动技能触发的自动防御见下面的职业专属增益)、受击方向以外的挡下条件(这一击"能不能挡"的受保护判断,按"有受击反应"近似)。
            new GuardRecipe(1, motionIndex: 0, minHoldStaticIndex: 0, recoilSpeedColumn: 0, recoilMsColumn: 1, physicalColumn: 2, magicalColumn: 3),
        },
        new[]
        {
            // 职业专属增益(自动防御,静态读码,非 REA):施法状态仍是 0x11(枢纽块 0x00cc2db7 发"状态 0x11、参数 [技能号]、优先级 1",0x00cc2e33),
            // 但挂附加体走鬼剑士自己的起手覆盖(0x00ce2290,先调基类起手 0x0085deb0)里这个技能号的分支(0x00ce272b):主角的 +0x6bfc 还没有附加体就先建一个
            // 专属附加体类的对象(虚表 0x015661bc,构造 0x0042d720,生效标志是对象 +0x40 的字节,判断函数 0x00ca6440),再把 [level info] 当前等级的第 1 列(转整数)
            // 写进附加体取到的子对象的保护整数里、挂上。这一列当持续毫秒是推断(中):数据里它每级同一个量、量级是毫秒,附加体里写的数也只有它。
            // 效果在主角受击前钩子(虚表槽 0x2d8,0x00cb20a0)的 0x00cb23a6 起一段:附加体生效、本技能与防御姿态技能(编号是代码里写死的常量)都学过、
            // 这一击能挡(黑盒 0x010ee67f,或攻击信息的 +0x4c 非 0)、主角不在状态 4 与 9(4 是倒地,E-105;9 是倒地结束时带异常状态 3 请求的状态,E-082,冰冻类,推断)、
            // 掷中:0x00cb2487 起取本技能 [level info] 第 0 列转整数乘 100,与 0x00e5bf90 在 [0, 10000) 里掷出的数比,掷出的数小才算(所以概率 = 列值 / 100,缩放 0.01);
            // 随后调防御姿态技能的"能不能用"(0x0049c920 → 脚本函数,没读通),通过就发"状态 0x13、带已挡下标志、优先级 0"(0x00cb2661);
            // 穿过的百分比取防御姿态技能自己的第 2、3 列(同 E-164 的 GuardRecipe),攻击方在背后时 0x00cb25fc 一段让主角先转向攻击方。
            // 没做:出招、跳跃、受击硬直中触发(状态请求的优先级是 0,能不能顶掉当前状态没读通,这里只在站立、走、跑时触发)、已在姿态里被打到背面时再挡、
            // 触发时 0x01136b10 发的事件、防御姿态技能的"能不能用"(冷却与蓝算不算在内没读到,按学会就行)、PK 区那一段 [level info](这里不分区)。
            new BuffRecipe(3, BuffStat.AutoGuardChance, durationColumn: 1, amountStaticIndex: -1, amountColumn: 0, amountScale: 0.01, guardSkillId: 1),
        });
    }
}
