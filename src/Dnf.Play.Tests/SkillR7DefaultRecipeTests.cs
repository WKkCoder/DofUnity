using Dnf.Game;

namespace Dnf.Play.Tests;

/// <summary>
/// 默认配方表里第七轮(M105,R7-he-01 到 R7-he-11)新增的五个配方的常量:动作、攻击信息、伤害列、特效、施法方式、起滑、对象都是客户端状态码里写死的下标,
/// 期望值取自静态读码的记录(状态进入块、逐帧块、守卫块),与实现里的写法无关。按动作项找配方(不写技能编号),每个只有一份。
/// 其中两个(姿势→跳劈、蓄力放对象)的对象只给一列伤害(另一列写 <see cref="SkillObjectSpec.NoColumn"/>),集成者批准后放进了默认表,
/// 默认表里两条既有的表形状检查按这个事实放宽了(见 SkillChainTests 与 SkillAutoChainTests 里写明依据的那两处)。
/// </summary>
public class SkillR7DefaultRecipeTests
{
    private static SkillRecipe Find(Func<SkillRecipe, bool> match)
    {
        return FindIn(SkillRecipes.Default, match);
    }

    private static SkillRecipe FindIn(SkillRecipes table, Func<SkillRecipe, bool> match)
    {
        SkillRecipe found = null;
        foreach (SkillRecipe r in table.All)
        {
            if (match(r))
            {
                Assert.Null(found);                                  // 只有一份
                found = r;
            }
        }
        Assert.NotNull(found);
        Assert.True(HeroSkills.IsImplemented(found.SkillId));      // 默认表里的算"已实现"
        Assert.Same(found, SkillRecipes.Default.Find(found.SkillId));
        return found;
    }

    private static void AssertPlainStage(SkillStageSpec st, int motion, int atk, int rateColumn, int fixedColumn, int[] effects, int effectFrame, SkillCastMode mode, bool slides = true)
    {
        Assert.Equal((motion, atk, rateColumn, fixedColumn), (st.MotionIndex, st.AttackInfoIndex, st.DamageRateColumn, st.DamageFixedColumn));
        Assert.Equal(effects, st.EffectIndices.ToArray());
        Assert.Equal((effectFrame, mode, slides), (st.EffectFrame, st.CastMode, st.Slides));
        Assert.Equal((-1, 0, 0), (st.EndFrame, st.CastHoldFrames, st.RequiresSkill));
        Assert.Empty(st.FlagEvents);
        Assert.Null(st.Rise);
    }

    private static void AssertNoOtherMechanics(SkillRecipe r, bool autoChain, int slideSpeedIndex = -1, int slideAccelIndex = -1)
    {
        Assert.Equal(autoChain, r.AutoChain);
        Assert.Equal((slideSpeedIndex, slideAccelIndex, -1, -1), (r.SlideSpeedIndex, r.SlideAccelIndex, r.ForwardSpeedIndex, r.ForwardAccelIndex));
        Assert.False(r.SlideFollowsHeldDirection);
        Assert.Equal(int.MaxValue, r.ChainFromFrame);
        Assert.Null(r.Hop);
        Assert.Null(r.Dive);
        Assert.Null(r.JumpAttack);
        Assert.Equal(0, r.MultiHitPassiveSkillId);
    }

    [Fact]
    public void ThePoseThenSwingRecipeStretchesThePoseToTheCastingTimeAndSwingsWithAttackInfo15()
    {
        // 状态 0x1f:姿势(动作 12,按施法时间伸缩,放预载特效第 0 个)→ 自动接挥击(动作 13,攻击信息 15,[level info] 百分比第 0 列,预载特效第 1、2 个)
        SkillRecipe r = Find(x => x.Stages.Count == 2 && x.Stages[0].MotionIndex == 12 && x.Stages[1].MotionIndex == 13);
        AssertPlainStage(r.Stages[0], 12, -1, -1, -1, new[] { 0 }, 0, SkillCastMode.Stretch);
        AssertPlainStage(r.Stages[1], 13, 15, 0, -1, new[] { 1, 2 }, 0, SkillCastMode.Placeholder);
        Assert.False(r.Stages[0].HasOwnStrike);
        Assert.True(r.Stages[1].HasOwnStrike);
        AssertNoOtherMechanics(r, autoChain: true);
        Assert.Empty(r.Objects);
    }

    [Fact]
    public void ThePoseThenLeapRecipeDropsOneObjectFromFrame5OfTheSecondStageWithOnlyAFixedColumn()
    {
        // 状态 0x2d:姿势(动作 66,特效第 0、1 个)→ 自动接跳劈(动作 67,攻击信息 57,百分比第 0 列,特效第 2、3 个)
        SkillRecipe r = Find(x => x.Stages.Count == 2 && x.Stages[0].MotionIndex == 66 && x.Stages[1].MotionIndex == 67);
        AssertPlainStage(r.Stages[0], 66, -1, -1, -1, new[] { 0, 1 }, 0, SkillCastMode.Placeholder);
        AssertPlainStage(r.Stages[1], 67, 57, 0, -1, new[] { 2, 3 }, 0, SkillCastMode.Placeholder);
        AssertNoOtherMechanics(r, autoChain: true);

        // 第 2 段动画播到第 5 帧起放一个被动对象 20043:前方 150、y 与离地偏移 0,伤害只取固定值第 1 列(没有百分比列)
        SkillObjectSpec o = Assert.Single(r.Objects);
        Assert.Equal((20043, 1, 5), (o.ObjectId, o.Stage, o.Frame));
        Assert.Equal((-1, 1), (o.DamageRateColumn, o.DamageFixedColumn));
        Assert.Equal((150, 0, 0), (o.ForwardX, o.OffsetY, o.OffsetZ));
        Assert.Equal((-1, false), (o.AfterEffect, o.OnLanding));
    }

    [Fact]
    public void TheChargeThenObjectRecipePausesTheFirstFrameAndDropsOneObjectFromFrame4()
    {
        // 状态 0x26:一段(动作 46,身体不打人,特效第 0、1 个,暂停施法);动画播到第 4 帧起放对象 20032:前方 150、y 偏移 0、离地 68,伤害只取百分比第 1 列
        SkillRecipe r = Find(x => x.Stages.Count == 1 && x.Stages[0].MotionIndex == 46);
        AssertPlainStage(r.Stages[0], 46, -1, -1, -1, new[] { 0, 1 }, 0, SkillCastMode.Pause);
        AssertNoOtherMechanics(r, autoChain: false);
        SkillObjectSpec o = Assert.Single(r.Objects);
        Assert.Equal((20032, 0, 4), (o.ObjectId, o.Stage, o.Frame));
        Assert.Equal((1, -1), (o.DamageRateColumn, o.DamageFixedColumn));
        Assert.Equal((150, 0, 68), (o.ForwardX, o.OffsetY, o.OffsetZ));
        Assert.Equal((-1, false), (o.AfterEffect, o.OnLanding));
    }

    [Fact]
    public void TheFourStageRecipeChainsMotions70To73WithAttackInfos58To60AndSlidesOnlyInTheLastStage()
    {
        // 状态 0x2c 的模式 2:动作 70(姿势,不打人)→ 71(攻击信息 58,百分比第 2 列)→ 72(59,第 3 列)→ 73(60,第 4 列),没有固定值列、没有对象;
        // 特效按"段号 × 4 + 0 到 3"(预载表共 16 个);只有换进第 73 项那一步设起滑,速度与加速度取 [static data] 第 1、2 个数,不看按住的方向
        SkillRecipe r = Find(x => x.Stages.Count == 4 && x.Stages[0].MotionIndex == 70);
        AssertPlainStage(r.Stages[0], 70, -1, -1, -1, new[] { 0, 1, 2, 3 }, 0, SkillCastMode.Placeholder, slides: false);
        AssertPlainStage(r.Stages[1], 71, 58, 2, -1, new[] { 4, 5, 6, 7 }, 0, SkillCastMode.Placeholder, slides: false);
        AssertPlainStage(r.Stages[2], 72, 59, 3, -1, new[] { 8, 9, 10, 11 }, 0, SkillCastMode.Placeholder, slides: false);
        AssertPlainStage(r.Stages[3], 73, 60, 4, -1, new[] { 12, 13, 14, 15 }, 0, SkillCastMode.Placeholder, slides: true);
        AssertNoOtherMechanics(r, autoChain: true, slideSpeedIndex: 1, slideAccelIndex: 2);
        Assert.Empty(r.Objects);
    }

    [Fact]
    public void TheSingleStrikeRecipeHoldsTheFirstFrameForStaticEntry0AndPlacesEffects0And2FromFrame2()
    {
        // 状态 0x17:一段(动作 8,攻击信息 8,百分比第 0 列),第 0 帧停留取 [static data] 第 0 个数;动画播到第 2 帧放预载特效第 0、2 个;不滑行、不放对象
        SkillRecipe r = Find(x => x.Stages.Count == 1 && x.Stages[0].CastMode == SkillCastMode.StaticHold);
        AssertPlainStage(r.Stages[0], 8, 8, 0, -1, new[] { 0, 2 }, 2, SkillCastMode.StaticHold);
        Assert.Equal(0, r.Stages[0].HoldStaticIndex);
        AssertNoOtherMechanics(r, autoChain: false);
        Assert.Empty(r.Objects);
    }

    [Fact]
    public void TheRecipesThatDoNotHoldOrStretchDoNotCarryAStaticHoldIndex()
    {
        // 反例:别的新配方的段没有静态停留下标(默认 -1),也没有一个旧的 Placeholder 段误带施法方式
        foreach (SkillRecipe r in SkillRecipes.Default.All)
        {
            foreach (SkillStageSpec st in r.Stages)
            {
                if (st.CastMode != SkillCastMode.StaticHold)
                {
                    Assert.Equal(-1, st.HoldStaticIndex);
                }
                if (st.CastMode != SkillCastMode.Placeholder)
                {
                    Assert.Equal(0, st.CastHoldFrames);              // 两种写法互斥:新施法方式的段不再按"蓄力帧数"算
                }
            }
        }
    }
}
