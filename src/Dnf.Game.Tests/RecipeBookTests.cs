namespace Dnf.Game.Tests;

/// <summary>配方簿:学会的配方编号集合。编号全是合成的(900 起)。</summary>
public class RecipeBookTests
{
    [Fact]
    public void ANewBookKnowsNothing()
    {
        var book = new RecipeBook();
        Assert.Equal(0, book.Count);
        Assert.Empty(book.Codes);
        Assert.False(book.IsLearned(900));
    }

    [Fact]
    public void LearningRecordsTheRecipeOnceAndReportsWhetherItWasNew()
    {
        var book = new RecipeBook();
        Assert.True(book.Learn(900));
        Assert.True(book.IsLearned(900));
        Assert.False(book.Learn(900));
        Assert.Equal(1, book.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidCodesAreNeverLearned(int code)
    {
        var book = new RecipeBook();
        Assert.False(book.Learn(code));
        Assert.Equal(0, book.Count);
        Assert.False(book.IsLearned(code));
    }

    [Fact]
    public void CodesComeBackInAscendingOrder()
    {
        var book = new RecipeBook();
        foreach (int code in new[] { 905, 901, 903, 902 })
        {
            Assert.True(book.Learn(code));
        }
        Assert.Equal(new[] { 901, 902, 903, 905 }, book.Codes);
    }

    [Fact]
    public void ForgettingAndClearingRemoveRecipes()
    {
        var book = new RecipeBook();
        book.Learn(900);
        book.Learn(901);
        Assert.True(book.Forget(900));
        Assert.False(book.Forget(900));
        Assert.False(book.IsLearned(900));
        Assert.True(book.IsLearned(901));
        book.Clear();
        Assert.Equal(0, book.Count);
        Assert.False(book.IsLearned(901));
    }
}
