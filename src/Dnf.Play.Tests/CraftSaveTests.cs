namespace Dnf.Play.Tests;

/// <summary>
/// 配方簿的存档字段(本项目自己的设计,没有原版证据):<c>recipe</c> 行的文本写出与读回、范围检查、旧存档与别的段落互不干扰。编号全是合成的。
/// </summary>
public class CraftSaveTests
{
    private const string Header = "DNFUNITY-SAVE 1\n";

    private static HeroSave Parse(string text)
    {
        Assert.True(HeroSave.TryParse(text, out HeroSave save, out string error), error);
        return save;
    }

    private static HeroSave WithRecipes()
    {
        var save = new HeroSave { Level = 9, Gold = 456 };
        save.Recipes.Add(900);
        save.Recipes.Add(905);
        save.Recipes.Add(940);
        return save;
    }

    [Fact]
    public void RecipesAreWrittenOnePerLineInOrder()
    {
        string text = WithRecipes().ToText();
        Assert.Contains("recipe 900\n", text);
        Assert.Contains("recipe 905\n", text);
        Assert.Contains("recipe 940\n", text);
        Assert.True(text.IndexOf("recipe 900", StringComparison.Ordinal) < text.IndexOf("recipe 905", StringComparison.Ordinal));
        Assert.True(text.IndexOf("recipe 905", StringComparison.Ordinal) < text.IndexOf("recipe 940", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTextRoundTripsFieldForField()
    {
        string text = WithRecipes().ToText();
        HeroSave back = Parse(text);
        Assert.Equal(new[] { 900, 905, 940 }, back.Recipes);
        Assert.Equal(9, back.Level);
        Assert.Equal(456L, back.Gold);
        Assert.Equal(text, back.ToText());
    }

    [Fact]
    public void ASaveWithoutRecipeLinesHasNoRecipesAndWritesNone()
    {
        string text = new HeroSave().ToText();
        Assert.DoesNotContain("recipe", text, StringComparison.Ordinal);
        Assert.Empty(Parse(text).Recipes);
        // 旧存档(只有头一行)照样读得懂。
        HeroSave old = Parse(Header + "level 3\n");
        Assert.Equal(3, old.Level);
        Assert.Empty(old.Recipes);
    }

    [Fact]
    public void RecipeLinesDoNotDisturbTheOtherSections()
    {
        HeroSave save = WithRecipes();
        save.Bag.Add(new SavedStack(5, 1));
        save.Worn.Add(new SavedWorn(10, 5));
        save.Quick.Add(new SavedQuick(0, 100));
        save.Pets.Add(new SavedPet(1, 950, 30, 80));
        save.ClearedQuests.Add(7);
        save.Skills.Add(new SavedSkill(15, 3));
        string text = save.ToText();
        HeroSave back = Parse(text);
        Assert.Single(back.Bag);
        Assert.Single(back.Worn);
        Assert.Single(back.Quick);
        Assert.Single(back.Pets);
        Assert.Single(back.ClearedQuests);
        Assert.Single(back.Skills);
        Assert.Equal(3, back.Recipes.Count);
        Assert.Equal(text, back.ToText());
    }

    [Fact]
    public void RecipeLinesMayBeInterleavedWithOtherLinesWhenReading()
    {
        HeroSave back = Parse(Header + "recipe 900\nlevel 4\nrecipe 901\n# 注释\n\nbag 5 2\nrecipe 902\n");
        Assert.Equal(new[] { 900, 901, 902 }, back.Recipes);
        Assert.Equal(4, back.Level);
        Assert.Single(back.Bag);
    }

    [Theory]
    [InlineData("recipe\n", "需要 1 个值")]
    [InlineData("recipe 900 901\n", "需要 1 个值")]
    [InlineData("recipe abc\n", "不是整数")]
    [InlineData("recipe 1.5\n", "不是整数")]
    [InlineData("recipe 0\n", "超出范围")]
    [InlineData("recipe -3\n", "超出范围")]
    [InlineData("recipe 2147483648\n", "超出范围")]
    public void ABadRecipeLineMakesTheWholeSaveUnreadable(string line, string reason)
    {
        Assert.False(HeroSave.TryParse(Header + "level 2\n" + line, out HeroSave save, out string error));
        Assert.Null(save);
        Assert.Contains("第 3 行", error);
        Assert.Contains(reason, error);
    }

    [Fact]
    public void TheLargestValidCodeIsAccepted()
    {
        Assert.Equal(new[] { int.MaxValue }, Parse(Header + "recipe 2147483647\n").Recipes);
        Assert.Equal(new[] { 1 }, Parse(Header + "recipe 1\n").Recipes);
    }

    [Fact]
    public void RepeatedRecipeLinesAreKeptAsWrittenAndLeftToTheSession()
    {
        HeroSave back = Parse(Header + "recipe 900\nrecipe 900\n");
        Assert.Equal(new[] { 900, 900 }, back.Recipes);
    }
}
