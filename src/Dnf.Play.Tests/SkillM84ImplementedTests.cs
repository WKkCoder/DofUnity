using Dnf.Game;

namespace Dnf.Play.Tests;

public sealed class SkillM84ImplementedTests
{
    [Fact]
    public void SecondWaveStateSkillHasDefaultRecipe()
    {
        Assert.True(HeroSkills.IsImplemented(21));
        Assert.NotNull(SkillRecipes.Default.Find(21));
    }
}
