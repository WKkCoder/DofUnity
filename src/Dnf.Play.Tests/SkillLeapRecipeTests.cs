namespace Dnf.Play.Tests;

/// <summary>
/// 默认配方表里的脚本前冲登记(客户端状态 0x2d 的"姿势 → 跳劈"类技能)。按结构找,不写真实技能号:
/// 表里只有一个配方的某一段带前冲,它是两段自动接续的第二段,前冲的落地帧就是客户端放那一段被动对象的帧号(逐帧块 0x00cbd751 判"动画帧 ≥ 5"、
/// 落地块 0x00cbd5be 判"已过 ≥ 第 5 帧开头"是同一个第 5 帧)。
/// </summary>
public class SkillLeapRecipeTests
{
    [Fact]
    public void TheDefaultTableGivesAScriptedLeapToExactlyOneStageAndItLandsOnTheFrameWhereItsObjectAppears()
    {
        var leaping = new List<(SkillRecipe Recipe, int Stage)>();
        foreach (SkillRecipe r in SkillRecipes.Default.All)
        {
            for (int i = 0; i < r.Stages.Count; i++)
            {
                if (r.Stages[i].Leap != null)
                {
                    leaping.Add((r, i));
                }
            }
        }
        Assert.Single(leaping);
        (SkillRecipe recipe, int stage) = leaping[0];
        SkillLeapSpec leap = recipe.Stages[stage].Leap;
        Assert.Equal(2, recipe.Stages.Count);
        Assert.Equal(1, stage);                                  // 第二段才前冲;第一段是原地姿势
        Assert.True(recipe.AutoChain);
        Assert.Equal((0, 1, 5), (leap.DistanceStaticIndex, leap.HeightStaticIndex, leap.LandFrame));   // [static data] 第 0 个数是距离、第 1 个数是最大高度;第 5 帧落地
        Assert.Contains(recipe.Objects, o => o.Stage == stage && o.Frame == leap.LandFrame);
        Assert.True(HeroSkills.IsImplemented(recipe.SkillId));
        Assert.Null(recipe.BodyHit);                             // 前冲不挂身体命中一族的规则表(那一族的配方个数另有断言)
    }

    [Fact]
    public void TheLandingBlockOfTheDefaultLeapCarriesTheEngineConstantsReadFromTheClient()
    {
        SkillRecipe recipe = SkillRecipes.Default.All.Single(r => r.Stages.Any(s => s.Leap != null));
        SkillStageSpec stage = recipe.Stages.Single(s => s.Leap != null);
        SkillLeapLanding landing = stage.Leap.Landing;
        Assert.NotNull(landing);
        Assert.Equal((5, 300), (landing.ShakeStrength, landing.ShakeMs));                    // 0x008e9950 的两个实参(0x00cbd664、0x00cbd669)
        Assert.Equal(2, landing.Sounds.Count);                                                // 落地块按名字放两个声音(0x00cbd5f0、0x00cbd624);名字是客户端写死的常量,这里只验个数与非空
        Assert.All(landing.Sounds, tag => Assert.False(string.IsNullOrEmpty(tag)));
        Assert.Equal(new[] { 4, 5, 6 }, landing.Effects.Select(e => e.Index).ToArray());    // 预载表第 4、5、6 项(0x00cbd67a、0x00cbd689、0x00cbd6b4)
        Assert.Equal(new[] { (0, 0), (0, 0), (121, 10) }, landing.Effects.Select(e => (e.Forward, e.Down)).ToArray());   // 只有第 6 项带偏移(0x79、0x0a)
        Assert.Equal((4, 5), (landing.Trim.WatchIndex, landing.Trim.Frame));                  // 预载第 4 项播到第 5 帧(0x00cbda3b 起的判断)
        Assert.Equal(new[] { 4, 5 }, landing.Trim.RemoveIndices.ToArray());                   // 撤第 4、5 项(0x00cbda4e、0x00cbda59)
        Assert.DoesNotContain(stage.EffectIndices, i => landing.Effects.Any(e => e.Index == i));   // 落地的特效不与这一段开始时放的重复
    }

    [Fact]
    public void OnlyTheFirstStageOfTheDefaultLeapSkillPlaysAnEntrySoundAndTheLeapStageHasNone()
    {
        SkillRecipe recipe = SkillRecipes.Default.All.Single(r => r.Stages.Any(s => s.Leap != null));
        Assert.Single(recipe.Stages[0].StartSounds);                                          // 模式 0 的进入块(0x00cd4c71 起)按名字放一个声音
        Assert.False(string.IsNullOrEmpty(recipe.Stages[0].StartSounds[0]));
        Assert.Empty(recipe.Stages[1].StartSounds);                                           // 模式 1 的进入块没有声音
    }
}
