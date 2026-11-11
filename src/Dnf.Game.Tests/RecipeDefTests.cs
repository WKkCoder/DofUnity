namespace Dnf.Game.Tests;

/// <summary>
/// 配方定义的值语义与形状:配方项、技能项的相等与文本,总输入表的次序,缺省的空表,类别名,成功率要不要掷骰。
/// 全是合成数据(编号 900 起)。
/// </summary>
public class RecipeDefTests
{
    // ------------------------------------------------------------------ 配方项

    [Fact]
    public void ARecipeItemIsEqualOnlyWhenBothCodeAndCountMatch()
    {
        var item = new RecipeItem(901, 2);
        Assert.True(item.Equals(new RecipeItem(901, 2)));
        Assert.True(item.Equals((object)new RecipeItem(901, 2)));
        Assert.False(item.Equals(new RecipeItem(901, 3)));
        Assert.False(item.Equals(new RecipeItem(902, 2)));
        Assert.False(item.Equals(new RecipeItem(902, 3)));
        Assert.False(item.Equals((object)"901x2"));
        Assert.False(item.Equals(null));
    }

    [Fact]
    public void RecipeItemsWorkAsSetKeys()
    {
        var set = new HashSet<RecipeItem> { new(901, 2), new(901, 2), new(901, 3), new(902, 2), new(2, 901) };
        Assert.Equal(4, set.Count);
        Assert.Contains(new RecipeItem(902, 2), set);
        Assert.Equal(new RecipeItem(901, 2).GetHashCode(), new RecipeItem(901, 2).GetHashCode());
    }

    [Fact]
    public void ARecipeItemPrintsItsCodeThenItsCount()
    {
        Assert.Equal("901x2", new RecipeItem(901, 2).ToString());
        Assert.Equal("0x500", new RecipeItem(RecipeItem.GoldCode, 500).ToString());
    }

    [Fact]
    public void OnlyItemCodeZeroIsGold()
    {
        Assert.Equal(0, RecipeItem.GoldCode);
        Assert.Equal(2, RecipeItem.PvpPointCode);
        Assert.True(new RecipeItem(0, 1).IsGold);
        Assert.False(new RecipeItem(1, 1).IsGold);
        Assert.False(new RecipeItem(RecipeItem.PvpPointCode, 1).IsGold);
        Assert.False(new RecipeItem(-1, 1).IsGold);
    }

    // ------------------------------------------------------------------ 技能项

    [Fact]
    public void ARecipeSkillIsEqualOnlyWhenBothSkillAndLevelMatch()
    {
        var skill = new RecipeSkill(70, 5);
        Assert.True(skill.Equals(new RecipeSkill(70, 5)));
        Assert.True(skill.Equals((object)new RecipeSkill(70, 5)));
        Assert.False(skill.Equals(new RecipeSkill(70, 6)));
        Assert.False(skill.Equals(new RecipeSkill(71, 5)));
        Assert.False(skill.Equals(new RecipeSkill(71, 6)));
        Assert.False(skill.Equals((object)"70x5"));
        Assert.False(skill.Equals(null));
        Assert.Equal(70, skill.Skill);
        Assert.Equal(5, skill.Level);
    }

    [Fact]
    public void RecipeSkillsWorkAsSetKeys()
    {
        var set = new HashSet<RecipeSkill> { new(70, 5), new(70, 5), new(70, 6), new(71, 5), new(5, 70) };
        Assert.Equal(4, set.Count);
        Assert.Equal(new RecipeSkill(70, 5).GetHashCode(), new RecipeSkill(70, 5).GetHashCode());
    }

    // ------------------------------------------------------------------ 配方定义

    [Fact]
    public void AllInputsAreTheInputsFollowedByTheExtraInputs()
    {
        var def = new RecipeDef(900, new[] { new RecipeItem(901, 1), new RecipeItem(902, 2) }, new[] { new RecipeItem(910, 1) },
            extraInputs: new[] { new RecipeItem(903, 3) });
        Assert.Equal(new[] { new RecipeItem(901, 1), new RecipeItem(902, 2), new RecipeItem(903, 3) }, def.AllInputs);
        Assert.Equal(2, def.Inputs.Count);
        Assert.Single(def.ExtraInputs);
        Assert.Single(def.Outputs);
    }

    [Fact]
    public void MissingListsAreEmptyNotNull()
    {
        var def = new RecipeDef(900, null, null);
        Assert.Empty(def.Inputs);
        Assert.Empty(def.Outputs);
        Assert.Empty(def.ExtraInputs);
        Assert.Empty(def.AllInputs);
        Assert.Empty(def.FailOutputs);
        Assert.Empty(def.Requirements);
        Assert.Empty(def.Categories);
        Assert.Null(def.Category);
        Assert.Equal(RecipeKind.Normal, def.Kind);
        Assert.Equal(ItemMakingTypes.None, def.MakingType);
        Assert.Equal(0, def.MakingLevel);
        Assert.Equal(0, def.SuccessRate);
        Assert.False(def.Malformed);
    }

    [Fact]
    public void TheDefinitionKeepsACopyOfTheListsItWasGiven()
    {
        var inputs = new List<RecipeItem> { new(901, 1) };
        var def = new RecipeDef(900, inputs, null);
        inputs.Add(new RecipeItem(902, 1));
        inputs[0] = new RecipeItem(903, 9);
        Assert.Equal(new[] { new RecipeItem(901, 1) }, def.Inputs);
        Assert.Equal(new[] { new RecipeItem(901, 1) }, def.AllInputs);
    }

    [Fact]
    public void TheFirstCategoryIsTheCategory()
    {
        var two = new RecipeDef(900, null, null, categories: new[] { "weaving", "enchant" });
        Assert.Equal("weaving", two.Category);
        Assert.Equal(new[] { "weaving", "enchant" }, two.Categories);
        Assert.Equal("enchant", new RecipeDef(900, null, null, categories: new[] { "enchant" }).Category);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(99, true)]
    [InlineData(100, true)]
    [InlineData(150, true)]
    public void OnlyANonZeroRateMeansTheCraftRolls(int rate, bool rolls)
    {
        var def = new RecipeDef(900, null, null, successRate: rate);
        Assert.Equal(rolls, def.HasSuccessRoll);
        Assert.Equal(rate, def.SuccessRate);
    }

    [Fact]
    public void EveryScalarFieldIsStoredAsGiven()
    {
        var def = new RecipeDef(900, null, null, RecipeKind.ExpertJob, new[] { new RecipeSkill(70, 5) }, null, 40,
            new[] { new RecipeItem(920, 2) }, broadcastType: 2, maintainAbility: 1, makingType: 8, makingLevel: 3,
            categories: new[] { "enchant" }, typeIndex: 6, path: "stackable/test/r900.stk", malformed: true);
        Assert.Equal(900, def.Code);
        Assert.Equal(RecipeKind.ExpertJob, def.Kind);
        Assert.Equal(new[] { new RecipeSkill(70, 5) }, def.Requirements);
        Assert.Equal(40, def.SuccessRate);
        Assert.Equal(new[] { new RecipeItem(920, 2) }, def.FailOutputs);
        Assert.Equal(2, def.BroadcastType);
        Assert.Equal(1, def.MaintainAbility);
        Assert.Equal(8, def.MakingType);
        Assert.Equal(3, def.MakingLevel);
        Assert.Equal(6, def.TypeIndex);
        Assert.Equal("stackable/test/r900.stk", def.Path);
        Assert.True(def.Malformed);
    }
}
