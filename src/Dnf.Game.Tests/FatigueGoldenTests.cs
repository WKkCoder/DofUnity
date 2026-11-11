using System.Globalization;

namespace Dnf.Game.Tests;

/// <summary>
/// 疲劳每日恢复判定的对拍:<see cref="FatigueGoldenVectors"/> 是用 Python 照服务端 CheckDailyScheduleTime(0x0846c0a8)的反编译独立写出来的(localtime + mktime + 减 86400 秒),
/// 这里把同一批 (整点, 现在, 上次) 喂给 <see cref="FatigueRules.DueForReset"/> 与 <see cref="FatigueState.Refresh"/>,结论必须逐条一致。
/// 向量是合成的固定时刻,覆盖四个恢复整点、年末与月末的进位、边界前一秒、边界上、边界后一秒、从没记过。
/// </summary>
public class FatigueGoldenTests
{
    private sealed record Vector(int Hour, DateTime Now, DateTime? Last, bool Due, string Raw);

    private static DateTime? Stamp(string text)
    {
        return text == "0" ? null : DateTime.ParseExact(text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture);
    }

    private static List<Vector> Vectors()
    {
        var list = new List<Vector>();
        foreach (string raw in FatigueGoldenVectors.Lines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            string[] f = line.Split('|');
            list.Add(new Vector(int.Parse(f[0], CultureInfo.InvariantCulture), Stamp(f[1]).Value, Stamp(f[2]), f[3] == "1", line));
        }
        return list;
    }

    [Fact]
    public void TheVectorsCoverEveryResetHourAndBothOutcomes()
    {
        List<Vector> vectors = Vectors();
        Assert.True(vectors.Count >= 500, "向量太少:" + vectors.Count);
        Assert.Equal(new[] { 0, 6, 12, 23 }, vectors.Select(v => v.Hour).Distinct().OrderBy(h => h).ToArray());
        foreach (int hour in new[] { 0, 6, 12, 23 })
        {
            Assert.Contains(vectors, v => v.Hour == hour && v.Due);
            Assert.Contains(vectors, v => v.Hour == hour && !v.Due);
        }
        Assert.Contains(vectors, v => v.Last == null);
        Assert.Contains(vectors, v => v.Now.Month == 12 && v.Now.Day == 31);
        Assert.Contains(vectors, v => v.Now.Month == 2 && v.Now.Day == 28);
    }

    [Fact]
    public void TheRuleAgreesWithTheIndependentReadingOnEveryVector()
    {
        var misses = new List<string>();
        foreach (Vector v in Vectors())
        {
            bool actual = new FatigueRules(FatigueRules.DefaultMax, v.Hour).DueForReset(v.Last, v.Now);
            if (actual != v.Due)
            {
                misses.Add(v.Raw + " -> " + actual);
            }
        }
        Assert.True(misses.Count == 0, "不一致 " + misses.Count + " 条,头几条:" + string.Join("; ", misses.Take(5)));
    }

    [Fact]
    public void TheStateRecoversExactlyWhenTheIndependentReadingSaysDue()
    {
        var misses = new List<string>();
        foreach (Vector v in Vectors())
        {
            var state = new FatigueState(new FatigueRules(10, v.Hour));
            state.Restore(7, v.Last);
            bool due = state.Refresh(v.Now);
            bool consumedOk = state.Consumed == (v.Due ? 0 : 7);
            bool stampOk = state.LastPlay == v.Now;
            if (due != v.Due || !consumedOk || !stampOk)
            {
                misses.Add(v.Raw + " -> due " + due + ", consumed " + state.Consumed + ", stamp " + state.LastPlay);
            }
        }
        Assert.True(misses.Count == 0, "不一致 " + misses.Count + " 条,头几条:" + string.Join("; ", misses.Take(5)));
    }
}
