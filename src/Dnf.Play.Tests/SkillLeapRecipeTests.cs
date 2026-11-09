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
}
