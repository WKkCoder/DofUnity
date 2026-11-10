using Dnf.Game;

namespace Dnf.Play.Tests;

/// <summary>
/// 称号图鉴与成就进度的存档行(本项目自己的设计,没有原版证据;范围取自服务端的存档上限:格数 70/200/120/60、成就计数 16 位):
/// 行格式与写读往返、旧存档没有这些行、越界与缺字段的行被拒、读错的行带行号。物品与编号全是合成的。
/// </summary>
public class TitleSaveTests
{
    private static HeroSave Sample()
    {
        var save = new HeroSave();
        save.Titles.Add(new SavedTitle(0, 0, 901));
        save.Titles.Add(new SavedTitle(0, 69, 905));
        save.Titles.Add(new SavedTitle(1, 199, 906));
        save.Titles.Add(new SavedTitle(2, 119, 908));
        save.Titles.Add(new SavedTitle(3, 59, 970));
        save.Achievements.Add(new SavedAchievement(9001, 0, 0, 0));
        save.Achievements.Add(new SavedAchievement(9002, 2, 0, 0));
        save.Achievements.Add(new SavedAchievement(9003, 65535, 1, 65535));
        return save;
    }

    private static string[] LinesWith(string text, string key) => text.Split('\n').Where(l => l.StartsWith(key + " ")).ToArray();

    // ------------------------------------------------------------------ 写与读

    [Fact]
    public void TitleAndAchievementLinesAreWrittenOnePerEntryInTheGivenOrder()
    {
        string text = Sample().ToText();
        Assert.Equal(new[] { "title 0 0 901", "title 0 69 905", "title 1 199 906", "title 2 119 908", "title 3 59 970" }, LinesWith(text, "title"));
        Assert.Equal(new[] { "achv 9001 0 0 0", "achv 9002 2 0 0", "achv 9003 65535 1 65535" }, LinesWith(text, "achv"));
    }

    [Fact]
    public void TheLinesRoundTripThroughTheTextFormat()
    {
        HeroSave save = Sample();
        Assert.True(HeroSave.TryParse(save.ToText(), out HeroSave back, out string error), error);
        Assert.Equal(save.Titles.Select(t => (t.Category, t.Slot, t.Code)).ToArray(), back.Titles.Select(t => (t.Category, t.Slot, t.Code)).ToArray());
        Assert.Equal(save.Achievements.Select(a => (a.Id, a.Remaining0, a.Remaining1, a.Remaining2)).ToArray(),
            back.Achievements.Select(a => (a.Id, a.Remaining0, a.Remaining1, a.Remaining2)).ToArray());
        Assert.Equal(save.ToText(), back.ToText());                                                      // 再写一遍一字不差
    }

    [Fact]
    public void ASaveWithoutTitleFieldsWritesNoLinesAndStillLoads()
    {
        string text = new HeroSave().ToText();
        Assert.Empty(LinesWith(text, "title"));
        Assert.Empty(LinesWith(text, "achv"));
        Assert.True(HeroSave.TryParse(text, out HeroSave back, out string error), error);               // 旧存档(没有这些行)照常读
        Assert.Empty(back.Titles);
        Assert.Empty(back.Achievements);
    }

    [Fact]
    public void TheOtherSaveFieldsAreUntouchedByTheTitleLines()
    {
        HeroSave save = Sample();
        save.Level = 33;
        save.Gold = 12345;
        Assert.True(HeroSave.TryParse(save.ToText(), out HeroSave back, out string error), error);
        Assert.Equal((33, 12345L), (back.Level, back.Gold));
    }

    // ------------------------------------------------------------------ 坏行

    [Theory]
    [InlineData("title 0 1")]                       // 少一个值
    [InlineData("title 0 1 901 5")]                 // 多一个值
    [InlineData("title a 1 901")]                   // 不是整数
    [InlineData("title 0 1 9.5")]
    [InlineData("title 4 0 901")]                   // 分类越界
    [InlineData("title -1 0 901")]
    [InlineData("title 0 70 901")]                  // 普通分类只有 70 格
    [InlineData("title 1 200 906")]
    [InlineData("title 2 120 908")]
    [InlineData("title 3 60 970")]
    [InlineData("title 0 -1 901")]
    [InlineData("title 4294967297 0 901")]          // 分类 2^32+1:截成 32 位会变成 1,不能蒙混过关
    [InlineData("title 0 4294967296 901")]          // 格 2^32:截成 32 位会变成 0
    [InlineData("title 0 0 0")]                     // 物品编号必须是正数
    [InlineData("title 0 0 -5")]
    [InlineData("title 0 0 99999999999")]           // 超出 32 位
    [InlineData("achv 9001 1 2")]                   // 少一个值
    [InlineData("achv 9001 1 2 3 4")]
    [InlineData("achv x 1 2 3")]
    [InlineData("achv 0 1 2 3")]                    // 成就编号必须是正数
    [InlineData("achv -1 1 2 3")]
    [InlineData("achv 4294967297 0 0 0")]           // 编号 2^32+1:截成 32 位会变成 1
    [InlineData("achv 9001 -1 0 0")]                // 计数 0 到 65535
    [InlineData("achv 9001 65536 0 0")]
    [InlineData("achv 9001 0 65536 0")]
    [InlineData("achv 9001 0 0 70000")]
    public void AMalformedOrOutOfRangeLineIsRejectedWithItsLineNumber(string bad)
    {
        string text = new HeroSave().ToText() + bad + "\n";
        int lineNo = text.TrimEnd('\n').Split('\n').Length;
        Assert.False(HeroSave.TryParse(text, out HeroSave back, out string error));
        Assert.Contains(lineNo.ToString(), error);
        Assert.Contains(bad.Split(' ')[0], error);
    }

    [Theory]
    [InlineData("title 0 0 1")]
    [InlineData("title 3 59 2147483647")]
    [InlineData("achv 1 0 0 0")]
    [InlineData("achv 2147483647 65535 65535 65535")]
    public void TheBoundaryValuesOfTheRangesAreAccepted(string good)
    {
        string text = new HeroSave().ToText() + good + "\n";
        Assert.True(HeroSave.TryParse(text, out HeroSave back, out string error), error);
        Assert.Equal(1, back.Titles.Count + back.Achievements.Count);
    }

    [Fact]
    public void RepeatedLinesAreKeptAsWrittenAndTheSessionDecidesWhichWins()
    {
        string text = new HeroSave().ToText() + "title 0 0 901\ntitle 0 0 902\nachv 9002 2 0 0\nachv 9002 1 0 0\n";
        Assert.True(HeroSave.TryParse(text, out HeroSave back, out string error), error);
        Assert.Equal(new[] { 901, 902 }, back.Titles.Select(t => t.Code).ToArray());
        Assert.Equal(new[] { 2, 1 }, back.Achievements.Select(a => a.Remaining0).ToArray());
    }
}
