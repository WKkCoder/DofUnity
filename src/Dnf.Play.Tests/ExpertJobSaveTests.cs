using System.Globalization;
using Dnf.Game;

namespace Dnf.Play.Tests;

/// <summary>
/// 副职业的存档字段(本项目自己的设计;服务端存的是 charac_info.expert_job、charac_stat.expert_job_exp 与 charac_expert_job 表的放弃次数、耐久、机器档与 30 字节配方位图):
/// <c>expertjob</c> 与 <c>exrecipe</c> 两种行的文本写出与读回、范围检查、旧存档与不认得的键。数值全是合成的。
/// </summary>
public class ExpertJobSaveTests
{
    private const string Header = "DNFUNITY-SAVE 1\n";

    private static HeroSave Parse(string text)
    {
        Assert.True(HeroSave.TryParse(text, out HeroSave save, out string error), error);
        return save;
    }

    private static HeroSave Hired()
    {
        var save = new HeroSave { Level = 7, Gold = 123 };
        save.ExpertJob = new SavedExpertJob(3, 120, 1, 250, 2);
        save.ExpertRecipes.AddRange(new[] { 0, 7, 8, 239 });
        return save;
    }

    [Fact]
    public void TheJobAndTheRecipesAreWrittenOnePerLineInOrder()
    {
        string text = Hired().ToText();
        Assert.Contains("expertjob 3 120 1 250 2\n", text);
        Assert.Contains("exrecipe 0\n", text);
        Assert.Contains("exrecipe 7\n", text);
        Assert.Contains("exrecipe 8\n", text);
        Assert.Contains("exrecipe 239\n", text);
        Assert.True(text.IndexOf("expertjob 3", StringComparison.Ordinal) < text.IndexOf("exrecipe 0", StringComparison.Ordinal));
        Assert.True(text.IndexOf("exrecipe 0", StringComparison.Ordinal) < text.IndexOf("exrecipe 7", StringComparison.Ordinal));
        Assert.True(text.IndexOf("exrecipe 8", StringComparison.Ordinal) < text.IndexOf("exrecipe 239", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTextRoundTripsFieldForField()
    {
        string text = Hired().ToText();
        HeroSave back = Parse(text);
        Assert.Equal(new SavedExpertJob(3, 120, 1, 250, 2), back.ExpertJob.Value);
        Assert.Equal(new[] { 0, 7, 8, 239 }, back.ExpertRecipes.ToArray());
        Assert.Equal(7, back.Level);
        Assert.Equal(123L, back.Gold);
        Assert.Equal(text, back.ToText());
    }

    [Fact]
    public void ASaveWithoutTheLinesHasNoJobAndWritesNone()
    {
        string text = new HeroSave().ToText();
        Assert.DoesNotContain("expertjob", text, StringComparison.Ordinal);
        Assert.DoesNotContain("exrecipe", text, StringComparison.Ordinal);
        HeroSave back = Parse(text);
        Assert.Null(back.ExpertJob);
        Assert.Empty(back.ExpertRecipes);
        // 旧存档(只有头一行)照样读得懂。
        HeroSave old = Parse(Header + "level 3\n");
        Assert.Equal(3, old.Level);
        Assert.Null(old.ExpertJob);
        Assert.Empty(old.ExpertRecipes);
    }

    [Fact]
    public void TheLinesDoNotDisturbTheOtherSections()
    {
        HeroSave save = Hired();
        save.Bag.Add(new SavedStack(5, 1));
        save.Worn.Add(new SavedWorn(10, 5));
        save.Quick.Add(new SavedQuick(0, 100));
        save.Pets.Add(new SavedPet(1, 950, 30, 80));
        save.PetSlots.Add(new SavedPetSlot(22, 1));
        string text = save.ToText();
        HeroSave back = Parse(text);
        Assert.Single(back.Bag);
        Assert.Single(back.Worn);
        Assert.Equal(10, back.Worn[0].Part);
        Assert.Single(back.Quick);
        Assert.Single(back.Pets);
        Assert.Single(back.PetSlots);
        Assert.Equal(new SavedExpertJob(3, 120, 1, 250, 2), back.ExpertJob.Value);
        Assert.Equal(4, back.ExpertRecipes.Count);
        Assert.Equal(text, back.ToText());
    }

    [Fact]
    public void UnknownNeighbourKeysAreStillIgnored()
    {
        HeroSave back = Parse(Header + "expertjobs 1\nexrecipes 5\nexpertjob 3 0 0 5 0\nexrecipe 4\n");
        Assert.Equal(new SavedExpertJob(3, 0, 0, 5, 0), back.ExpertJob.Value);
        Assert.Equal(new[] { 4 }, back.ExpertRecipes.ToArray());
    }

    [Fact]
    public void ALaterJobLineReplacesAnEarlierOneAndRecipeLinesAccumulate()
    {
        HeroSave back = Parse(Header + "expertjob 3 10 0 5 0\nexrecipe 4\nexpertjob 2 20 1 6 1\nexrecipe 4\nexrecipe 9\n");
        Assert.Equal(new SavedExpertJob(2, 20, 1, 6, 1), back.ExpertJob.Value);
        Assert.Equal(new[] { 4, 4, 9 }, back.ExpertRecipes.ToArray());
    }

    [Theory]
    [InlineData("expertjob 0 0 0 0 0", 0, 0, 0, 0, 0)]
    [InlineData("expertjob 0 -1 1 0 0", 0, -1, 1, 0, 0)]                                                // 放弃之后:经验 −1
    [InlineData("expertjob 3 120 1 250 2", 3, 120, 1, 250, 2)]
    [InlineData("expertjob 4 2147483647 65535 2147483647 255", 4, int.MaxValue, 65535, int.MaxValue, 255)]
    public void JobRowsAtTheBoundariesAreAccepted(string line, int kind, int exp, int giveup, int endurance, int grade)
    {
        HeroSave back = Parse(Header + line + "\n");
        Assert.Equal(new SavedExpertJob(kind, exp, giveup, endurance, grade), back.ExpertJob.Value);
    }

    [Theory]
    [InlineData("exrecipe 0", 0)]
    [InlineData("exrecipe 239", 239)]
    [InlineData("exrecipe 120", 120)]
    public void RecipeRowsAtTheBoundariesAreAccepted(string line, int index)
    {
        HeroSave back = Parse(Header + line + "\n");
        Assert.Equal(index, Assert.Single(back.ExpertRecipes));
    }

    [Theory]
    [InlineData("expertjob 3 0 0 5")]                      // 少一个值
    [InlineData("expertjob 3 0 0 5 0 1")]                  // 多一个值
    [InlineData("expertjob")]                              // 一个值也没有
    [InlineData("expertjob x 0 0 5 0")]                    // 不是整数
    [InlineData("expertjob 3 0 0 5 1.5")]                  // 小数
    [InlineData("expertjob 5 0 0 5 0")]                    // 种类只有 0 到 4
    [InlineData("expertjob -1 0 0 5 0")]
    [InlineData("expertjob 3 -2 0 5 0")]                   // 经验最小是 −1
    [InlineData("expertjob 3 0 -1 5 0")]                   // 放弃次数不能是负的
    [InlineData("expertjob 3 0 65536 5 0")]                // 放弃次数最多 65535(服务端 16 位)
    [InlineData("expertjob 3 0 0 -1 0")]                   // 耐久不能是负的
    [InlineData("expertjob 3 0 0 5 -1")]                   // 机器档不能是负的
    [InlineData("expertjob 3 0 0 5 256")]                  // 机器档最多 255(服务端按一个字节查表)
    [InlineData("expertjob 3 2147483648 0 5 0")]           // 超出 32 位整数
    [InlineData("expertjob 3 0 0 2147483648 0")]
    [InlineData("exrecipe")]
    [InlineData("exrecipe 1 2")]
    [InlineData("exrecipe x")]
    [InlineData("exrecipe 1.5")]
    [InlineData("exrecipe -1")]                            // 位图只有 240 位
    [InlineData("exrecipe 240")]
    public void ABadRowMakesTheWholeSaveUnreadableWithALineNumber(string line)
    {
        // 前面垫一行合法的内容,错误应报在第 3 行(头一行是第 1 行)。
        Assert.False(HeroSave.TryParse(Header + "level 2\n" + line + "\n", out HeroSave save, out string error));
        Assert.Null(save);
        Assert.StartsWith("第 3 行", error);
        Assert.Contains(line.Split(' ')[0], error);
    }

    [Fact]
    public void TheFormatKnowsItsOwnKeysAndLimits()
    {
        Assert.True(ExpertJobSaveFormat.Handles("expertjob"));
        Assert.True(ExpertJobSaveFormat.Handles("exrecipe"));
        Assert.False(ExpertJobSaveFormat.Handles("expertjobs"));
        Assert.False(ExpertJobSaveFormat.Handles("pet"));
        Assert.False(ExpertJobSaveFormat.Handles(""));
        Assert.Equal(ExpertJobState.RecipeCount, ExpertJobSaveFormat.RecipeCount);       // 位图的位数与状态一致
        Assert.Equal(240, ExpertJobSaveFormat.RecipeCount);
        Assert.Equal(4, ExpertJobSaveFormat.MaxKind);
        Assert.Equal(ExpertJobKinds.Count - 1, ExpertJobSaveFormat.MaxKind);        // 种类上限与种类枚举一致(0 = 没有,1 到 4 四种)
        Assert.Equal(65535, ExpertJobSaveFormat.MaxGiveup);
        Assert.Equal(255, ExpertJobSaveFormat.MaxGrade);
    }

    [Fact]
    public void NegativeNumbersAreWrittenWithTheInvariantMinusSignWhateverTheCurrentCulture()
    {
        var save = new HeroSave { ExpertJob = new SavedExpertJob(0, -1, 2, 0, 0) };
        CultureInfo original = CultureInfo.CurrentCulture;
        var odd = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        odd.NumberFormat.NegativeSign = "\u2212";                    // 有的语言的负号不是 ASCII 的 '-'
        try
        {
            CultureInfo.CurrentCulture = odd;
            Assert.Equal("\u22121", (-1).ToString());                // 先确认这个"怪文化"确实生效
            Assert.Contains("expertjob 0 -1 2 0 0\n", save.ToText());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
        Assert.True(HeroSave.TryParse(Header + "expertjob 0 -1 2 0 0\n", out HeroSave back, out string error), error);
        Assert.Equal(-1, back.ExpertJob.Value.Exp);
    }

    [Fact]
    public void NegativeNumbersAreReadWithTheInvariantMinusSignWhateverTheCurrentCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        var odd = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        odd.NumberFormat.NegativeSign = "~";                         // 这个负号不会被当成 '-' 的别名
        try
        {
            CultureInfo.CurrentCulture = odd;
            Assert.False(long.TryParse("-1", NumberStyles.Integer, CultureInfo.CurrentCulture, out _));   // 先确认这个"怪文化"确实不认 '-'
            Assert.True(HeroSave.TryParse(Header + "expertjob 0 -1 2 0 0\nexrecipe 4\n", out HeroSave back, out string error), error);
            Assert.Equal(-1, back.ExpertJob.Value.Exp);
            Assert.Equal(new[] { 4 }, back.ExpertRecipes.ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
