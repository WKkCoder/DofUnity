using Dnf.Data;

namespace Dnf.Game.Tests;

/// <summary>
/// 配方脚本读取:认配方物品、整数串切成三张表与种类标志、各附加标签(成功率、失败产物、额外材料、制作类别、制作熟练度)的读法与缺省值、残缺的认定。
/// 标记流全是合成的(编号 900 起),期望值按服务端装载函数的规则手算,不含任何原版数据。
/// </summary>
public class CraftScriptTests
{
    private const string Path = "stackable/test/r900.stk";

    private static RecipeDef Read(TokenStreamBuilder b)
    {
        (ScriptToken[] tokens, PvfStringTable strings) = b.Build();
        return RecipeScript.Parse(tokens, strings, 900, Path);
    }

    /// <summary>配方物品的开头 + 一段 <c>[int data]</c>。</summary>
    private static TokenStreamBuilder Recipe(params int[] data)
    {
        return new TokenStreamBuilder().Tag("[stackable type]").Str("[recipe]").Int(0).Tag("[int data]").Ints(data).Tag("[/int data]");
    }

    // ------------------------------------------------------------------ 认配方物品

    [Fact]
    public void OnlyAStackableOfTypeRecipeIsARecipe()
    {
        Assert.NotNull(Read(Recipe(0, 0, 0)));
        Assert.Null(Read(new TokenStreamBuilder().Tag("[stackable type]").Str("[material]").Int(0).Tag("[int data]").Ints(0, 0, 0).Tag("[/int data]")));
        Assert.Null(Read(new TokenStreamBuilder().Tag("[int data]").Ints(0, 0, 0).Tag("[/int data]")));
        Assert.Null(Read(new TokenStreamBuilder()));
    }

    [Fact]
    public void TheLastStackableTypeWins()
    {
        Assert.NotNull(Read(new TokenStreamBuilder().Tag("[stackable type]").Str("[material]").Int(0)
            .Tag("[stackable type]").Str("[recipe]").Int(0)));
        Assert.Null(Read(new TokenStreamBuilder().Tag("[stackable type]").Str("[recipe]").Int(0)
            .Tag("[stackable type]").Str("[material]").Int(0)));
    }

    [Fact]
    public void TheIntegerAfterTheTypeIsKeptAsTheTypeIndex()
    {
        RecipeDef def = Read(new TokenStreamBuilder().Tag("[stackable type]").Str("[recipe]").Int(5));
        Assert.Equal(5, def.TypeIndex);
        Assert.Equal(900, def.Code);
        Assert.Equal(Path, def.Path);
    }

    [Fact]
    public void ABinaryOrTextScriptIsNotARecipe()
    {
        PvfStringTable strings = new TokenStreamBuilder().Build().Strings;
        Assert.Null(RecipeScript.Parse(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, strings, 900, Path));
        // 没有字符串表读不了标签名;标记流脚本配上字符串表就能读(同一份字节走字节入口与标记入口结果一样)。
        TokenStreamBuilder recipe = Recipe(1, 901, 2, 1, 910, 1, 0);
        Assert.Null(RecipeScript.Parse(recipe.Bytes(), null, 900, Path));
        RecipeDef def = RecipeScript.Parse(recipe.Bytes(), recipe.Build().Strings, 900, Path);
        Assert.NotNull(def);
        Assert.Equal(new[] { new RecipeItem(901, 2) }, def.Inputs);
    }

    // ------------------------------------------------------------------ 三张表

    [Fact]
    public void TheThreeTablesAreCutFromTheIntegerRun()
    {
        // 输入 2 对:901×3、902×1;产物 1 对:910×2;技能要求 1 对:技能 70 要 5 级。
        RecipeDef def = Read(Recipe(2, 901, 3, 902, 1, 1, 910, 2, 1, 70, 5));
        Assert.Equal(new[] { new RecipeItem(901, 3), new RecipeItem(902, 1) }, def.Inputs);
        Assert.Equal(new[] { new RecipeItem(910, 2) }, def.Outputs);
        Assert.Equal(new[] { new RecipeSkill(70, 5) }, def.Requirements);
        Assert.Equal(RecipeKind.Normal, def.Kind);
        Assert.False(def.Malformed);
        Assert.Empty(def.ExtraInputs);
        Assert.Empty(def.FailOutputs);
        Assert.Equal(0, def.SuccessRate);
        Assert.False(def.HasSuccessRoll);
    }

    [Fact]
    public void EmptyTablesAreStillCutCorrectly()
    {
        RecipeDef def = Read(Recipe(0, 1, 910, 1, 0));
        Assert.Empty(def.Inputs);
        Assert.Equal(new[] { new RecipeItem(910, 1) }, def.Outputs);
        Assert.Empty(def.Requirements);
        Assert.False(def.Malformed);
    }

    [Theory]
    [InlineData(1, RecipeKind.Premium)]
    [InlineData(2, RecipeKind.ExpertJob)]
    [InlineData(0, RecipeKind.Normal)]
    [InlineData(7, RecipeKind.Normal)]
    public void ATrailingFlagPicksTheKind(int flag, RecipeKind expected)
    {
        RecipeDef def = Read(Recipe(1, 901, 1, 1, 910, 1, 0, flag));
        Assert.Equal(expected, def.Kind);
        Assert.False(def.Malformed);
    }

    [Fact]
    public void NoTrailingFlagIsANormalRecipe()
    {
        Assert.Equal(RecipeKind.Normal, Read(Recipe(1, 901, 1, 1, 910, 1, 0)).Kind);
    }

    [Fact]
    public void GoldIsJustItemCodeZeroInTheInputTable()
    {
        RecipeDef def = Read(Recipe(2, 0, 500, 901, 2, 1, 910, 1, 0));
        Assert.True(def.Inputs[0].IsGold);
        Assert.Equal(500, def.Inputs[0].Count);
        Assert.False(def.Inputs[1].IsGold);
    }

    [Fact]
    public void ADeclaredCountLargerThanTheIntegersGivenMakesTheRecipeMalformed()
    {
        // 声明 3 对输入,实际只给了 2 对:已读到的两项留下,后面的表读不到,配方残缺。
        RecipeDef def = Read(Recipe(3, 901, 1, 902, 1));
        Assert.True(def.Malformed);
        Assert.Equal(2, def.Inputs.Count);
        Assert.Empty(def.Outputs);
    }

    [Fact]
    public void AMissingSkillTableMakesTheRecipeMalformedButKeepsTheOtherTables()
    {
        RecipeDef def = Read(Recipe(1, 901, 1, 1, 910, 1));
        Assert.True(def.Malformed);
        Assert.Single(def.Inputs);
        Assert.Single(def.Outputs);
    }

    [Fact]
    public void AnEmptyOrMissingIntDataIsNotMalformedJustEmpty()
    {
        RecipeDef empty = Read(Recipe());
        Assert.False(empty.Malformed);
        Assert.Empty(empty.Inputs);
        RecipeDef none = Read(new TokenStreamBuilder().Tag("[stackable type]").Str("[recipe]").Int(0));
        Assert.False(none.Malformed);
        Assert.Empty(none.Outputs);
    }

    [Fact]
    public void ALaterIntDataSectionReplacesAnEarlierOne()
    {
        RecipeDef def = Read(Recipe(1, 901, 1, 1, 910, 1, 0).Tag("[int data]").Ints(1, 902, 2, 1, 911, 1, 0).Tag("[/int data]"));
        Assert.Equal(new[] { new RecipeItem(902, 2) }, def.Inputs);
        Assert.Equal(new[] { new RecipeItem(911, 1) }, def.Outputs);
    }

    [Fact]
    public void AFloatInAnIntegerPositionIsTruncatedTowardZero()
    {
        RecipeDef def = Read(new TokenStreamBuilder().Tag("[stackable type]").Str("[recipe]").Int(0)
            .Tag("[int data]").Int(1).Int(901).Float(2.9f).Int(1).Int(910).Int(1).Int(0).Tag("[/int data]")
            .Tag("[success rate]").Float(40.9f));
        Assert.Equal(new[] { new RecipeItem(901, 2) }, def.Inputs);
        Assert.Equal(40, def.SuccessRate);
    }

    [Fact]
    public void TheTagThatEndsAnIntegerRunIsConsumedAsTheTerminator()
    {
        // [int data] 后面没有结束标签、直接是 [success rate]:这个标签被当作"读不出整数"的结尾吃掉,它的数 50 成了游离标记,成功率保持缺省。
        RecipeDef def = Read(new TokenStreamBuilder().Tag("[stackable type]").Str("[recipe]").Int(0)
            .Tag("[int data]").Ints(1, 901, 1, 1, 910, 1, 0).Tag("[success rate]").Int(50));
        Assert.Equal(0, def.SuccessRate);
        Assert.Single(def.Inputs);
    }

    [Fact]
    public void UnknownTagsAndStrayValuesAreSkipped()
    {
        RecipeDef def = Read(new TokenStreamBuilder().Tag("[weight]").Int(5).Int(6).Tag("[stackable type]").Str("[recipe]").Int(0)
            .Tag("[unknown tag]").Str("x").Tag("[success rate]").Int(30));
        Assert.Equal(30, def.SuccessRate);
    }

    // ------------------------------------------------------------------ 附加标签

    [Fact]
    public void SuccessBroadcastAndMaintainEachReadOneInteger()
    {
        RecipeDef def = Read(Recipe(0, 0, 0).Tag("[success rate]").Int(40).Tag("[broadcast type]").Int(2).Tag("[maintain ability]").Int(1));
        Assert.Equal(40, def.SuccessRate);
        Assert.True(def.HasSuccessRoll);
        Assert.Equal(2, def.BroadcastType);
        Assert.Equal(1, def.MaintainAbility);
    }

    [Fact]
    public void FailOutputsAreReadAsPairsAndALoneLastIntegerIsDropped()
    {
        RecipeDef def = Read(Recipe(0, 0, 0).Tag("[fail output]").Ints(920, 2, 921).Tag("[/fail output]"));
        Assert.Equal(new[] { new RecipeItem(920, 2) }, def.FailOutputs);
    }

    [Fact]
    public void ALaterFailOutputSectionReplacesAnEarlierOne()
    {
        RecipeDef def = Read(Recipe(0, 0, 0).Tag("[fail output]").Ints(920, 2).Tag("[/fail output]").Tag("[fail output]").Ints(921, 1).Tag("[/fail output]"));
        Assert.Equal(new[] { new RecipeItem(921, 1) }, def.FailOutputs);
    }

    [Fact]
    public void BeadItemsAreExtraInputsAppendedAfterTheNormalOnes()
    {
        RecipeDef def = Read(Recipe(1, 901, 1, 1, 910, 1, 0).Tag("[bead item]").Ints(902, 2, 903, 1).Tag("[/bead item]"));
        Assert.Equal(new[] { new RecipeItem(902, 2), new RecipeItem(903, 1) }, def.ExtraInputs);
        Assert.Equal(new[] { new RecipeItem(901, 1), new RecipeItem(902, 2), new RecipeItem(903, 1) }, def.AllInputs);
        Assert.Single(def.Inputs);
    }

    [Fact]
    public void SeveralBeadItemSectionsAccumulateAndALonePairHalfIsDropped()
    {
        RecipeDef def = Read(Recipe(0, 0, 0)
            .Tag("[bead item]").Ints(902, 2).Tag("[/bead item]")
            .Tag("[bead item]").Ints(903, 1, 904).Tag("[/bead item]"));
        Assert.Equal(new[] { new RecipeItem(902, 2), new RecipeItem(903, 1) }, def.ExtraInputs);
    }

    [Fact]
    public void ACategoryIsTheStringInStringData()
    {
        RecipeDef def = Read(Recipe(0, 0, 0).Tag("[string data]").Str("craftmanship").Tag("[/string data]"));
        Assert.Equal("craftmanship", def.Category);
        Assert.Equal(new[] { "craftmanship" }, def.Categories);
        Assert.Null(Read(Recipe(0, 0, 0)).Category);
    }

    [Fact]
    public void ALaterStringDataSectionReplacesAnEarlierOne()
    {
        RecipeDef def = Read(Recipe(0, 0, 0)
            .Tag("[string data]").Str("craftmanship").Tag("[/string data]")
            .Tag("[string data]").Str("weaving").Str("enchant").Tag("[/string data]"));
        Assert.Equal(new[] { "weaving", "enchant" }, def.Categories);
        Assert.Equal("weaving", def.Category);
    }

    [Fact]
    public void AMakingLevelReadsATypeNameAndALevel()
    {
        RecipeDef def = Read(Recipe(0, 0, 0).Tag("[minimum itemmaking level]").Str("weapon").Int(3));
        Assert.Equal(0, def.MakingType);
        Assert.Equal(3, def.MakingLevel);
        RecipeDef stone = Read(Recipe(0, 0, 0).Tag("[minimum itemmaking level]").Str("magic stone").Int(7));
        Assert.Equal(10, stone.MakingType);
        Assert.Equal(7, stone.MakingLevel);
        Assert.Equal(ItemMakingTypes.None, Read(Recipe(0, 0, 0)).MakingType);
        Assert.Equal(0, Read(Recipe(0, 0, 0)).MakingLevel);
    }

    [Fact]
    public void AnUnknownMakingTypeNameMakesTheWholeScriptMalformed()
    {
        // 服务端认不得类别名就整个脚本装载失败;这里记成残缺,后面的标签不再读(成功率保持缺省)。
        RecipeDef def = Read(Recipe(0, 0, 0).Tag("[minimum itemmaking level]").Str("sword").Int(3).Tag("[success rate]").Int(40));
        Assert.True(def.Malformed);
        Assert.Equal(ItemMakingTypes.None, def.MakingType);
        Assert.Equal(0, def.SuccessRate);
    }

    [Fact]
    public void AMakingLevelCutShortMakesTheScriptMalformed()
    {
        RecipeDef def = Read(Recipe(0, 0, 0).Tag("[minimum itemmaking level]").Str("ring"));
        Assert.True(def.Malformed);
        Assert.Equal(8, def.MakingType);
        Assert.Equal(0, def.MakingLevel);
    }

    [Fact]
    public void MakingTypeNamesAreCaseSensitiveAndMatchTheElevenServerNames()
    {
        Assert.Equal(11, ItemMakingTypes.Count);
        string[] names = { "weapon", "cloth", "leather", "larmor", "harmor", "plate", "amulet", "wrist", "ring", "support", "magic stone" };
        for (int i = 0; i < names.Length; i++)
        {
            Assert.Equal(i, ItemMakingTypes.FromName(names[i]));
            Assert.Equal(names[i], ItemMakingTypes.NameOf(i));
        }
        Assert.Equal(ItemMakingTypes.None, ItemMakingTypes.FromName("Weapon"));
        Assert.Equal(ItemMakingTypes.None, ItemMakingTypes.FromName(null));
        Assert.Null(ItemMakingTypes.NameOf(11));
        Assert.Null(ItemMakingTypes.NameOf(-1));
    }
}
