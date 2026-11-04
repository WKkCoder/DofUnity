using Dnf.Game;

namespace Dnf.Play.Tests;

public sealed class SkillM84DefaultRecipeTests
{
    [Fact]
    public void SecondWaveRecipeUsesFrameFourObject()
    {
        SkillRecipe recipe = SkillRecipes.Default.Find(21);
        Assert.NotNull(recipe);
        Assert.Single(recipe.Objects);
        Assert.Equal(4, recipe.Objects[0].Frame);
        Assert.Equal(3, recipe.Stages[0].CastHoldFrames);
    }
}
