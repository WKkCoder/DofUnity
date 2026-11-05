using System.Collections.Generic;

namespace Dnf.Play
{
    // M105(R7-he):读通了、配方与测试都写好了,但还没放进 Default 的两个技能。
    // 原因(必须由集成者决定,见第七轮 he 的交付报告"需要用户决定/批准"一节):这两个技能放出的被动对象在客户端里只用一列伤害(一个只取固定值列、一个只取百分比列),
    // 而默认表里已有两条表形状检查对所有配方一视同仁——SkillChainTests.WithoutARecipeTheSkillIsNotImplementedAndTheDefaultTableIsWellFormed 要求每个对象的百分比列与固定值列都非负,
    // SkillAutoChainTests.TheDefaultTableHasAnAutoChainRecipeWithABodyStrikeFirstAndAHeroObjectOnTheSecondStage 要求"自动接续且带对象"的配方只有一个而且是那一种形状。
    // 本轮的规定是现有测试必须原样通过,所以这两个配方不放进 Default(放进去会让这两条测试变红)。
    // 批准后的启用办法:把 Pending 里的两项挪进 SkillRecipes.cs 里 Default 的数组,并按下面的事实放宽那两条测试(对象的"没有这一列"用 -1;带对象的自动接续配方不再唯一)。
    public sealed partial class SkillRecipes
    {
        /// <summary>读通了但还没放进 <see cref="Default"/> 的配方(原因见本文件开头的注释);只有测试与检查命令用它,运行时的技能表不含它们。</summary>
        public static SkillRecipes PendingApproval { get; } = new SkillRecipes(new[]
        {
            // 两段自动接续的"姿势→跳劈"类技能(状态 0x2d,R7-he-05、R7-he-06):起手函数 0x0049ddc0 只发模式 0;进入块 0x00cd4bb6:模式 0 播 [etc motion] 第 66 项(姿势,不设攻击信息),
            // 放预载表第 0、1 个特效(第 2、1 层),不蓄力;守卫块 0x00cc8c99:模式 0 的动画播完那一刻自己发"状态 0x2d、参数 [1]、优先级 1";模式 1 的动画播完发"状态 0"。
            // 模式 1:动作第 67 项,攻击信息第 57 项,伤害取 [level info] 百分比第 0 列;放预载表第 2、3 个特效(第 2、1 层)。逐帧块 0x00cbd4d2:动画播到第 5 帧(含)以后、本机控制时,
            // 放一个被动对象:主角前方 150 像素、y 与离地偏移 0,朝向同主角,伤害取 [level info] 固定值第 1 列(创建参数的"威力"位置是固定值列 0x00913d70 的结果,没有百分比列;
            // 参数包里另有第 2 列、两个 [static data] 数与一个被动技能倍率,用途没读到);编号是基础职业的那个(0x0049c8f0 为真取其一,另一个职业的没做)。
            // 没做:模式 1 按 [static data] 前两个数脚本位移(水平前冲到第 5 帧开头、竖直一段上升一段下降的弧;Play 层改不了主角离地高度)、第 5 帧放的三个特效、与另一个被动技能联动的威力倍率与对象尺寸、
            // 震屏(0x008e9950)、音效。
            new SkillRecipe(81,
                new[]
                {
                    new SkillStageSpec(motionIndex: 66, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: 0, effectFrame: 0,
                        moreEffects: new[] { 1 }),
                    new SkillStageSpec(motionIndex: 67, attackInfoIndex: 57, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: 2, effectFrame: 0,
                        moreEffects: new[] { 3 }),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                objects: new[]
                {
                    new SkillObjectSpec(objectId: 20043, stage: 1, frame: 5, damageRateColumn: -1, damageFixedColumn: 1, forwardX: 150, offsetY: 0, offsetZ: 0),
                },
                autoChain: true),

            // 蓄力放对象类技能(状态 0x26,R7-he-03、R7-he-04):起手函数 0x0049caa0 发"状态 0x26"(无参数);进入块 0x00cda8d9:播 [etc motion] 第 46 项,不设攻击信息(身体不打人),
            // 把动画拨到第 0 帧并暂停,施法时间取受保护函数 0x009114e0(技能数据的 [casting time]),记下"当前时刻 + 施法时间"并起一个读条;放预载表第 0 个特效。
            // 逐帧块 0x00cc09e0:动画暂停且当前时刻 ≥ 记下的时刻就解除暂停(Pause:停留 = 施法时间 + 第 0 帧自己的时长);动画播到第 1 帧放预载表第 1 个特效(第 2 层);
            // 动画播到第 4 帧(含)以后、本机控制时放一次被动对象:主角前方 150 像素、y 偏移 0、离地 68,朝向同主角,编号是基础职业的那个(0x0049c8f0 为真取其一,另一个职业的没做);
            // 伤害取 [level info] 百分比第 1 列(创建参数的"威力"位置就是这一列的 0x009138c0 结果,没有固定值列)。动画播完守卫块发"状态 0"。
            // 没做:对象创建参数包里的尺寸与其余列(第 2、7 列与两个 [static data] 数,用途没读到)、另一处用 0x00b5b82b(受保护函数)的返回值减 1 当次数、
            // 对第 1、3 列等做的按次数累加(次数读不到,按 1 即不累加)、特效偏移(进入块里第 0 个特效带 −35、−68 偏移)、第 1 个特效比第 0 个晚放、读条界面、音效。
            new SkillRecipe(2,
                new[]
                {
                    new SkillStageSpec(motionIndex: 46, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: 0, effectFrame: 0,
                        moreEffects: new[] { 1 }, castMode: SkillCastMode.Pause),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                objects: new[]
                {
                    new SkillObjectSpec(objectId: 20032, stage: 0, frame: 4, damageRateColumn: 1, damageFixedColumn: -1, forwardX: 150, offsetY: 0, offsetZ: 68),
                }),
        });
    }
}
