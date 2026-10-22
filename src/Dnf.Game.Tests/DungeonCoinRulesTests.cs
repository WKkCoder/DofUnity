using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 副本的复活币规则(证据 E-115,静态读码):.dgn 的三个标签与服务端检查的顺序。数值全是合成的,只检验规则的形状,与原版数据无关。
/// </summary>
public class DungeonCoinRulesTests
{
    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptValue S(string text) => new ScriptValue(ScriptValueKind.String, 0, text);

    private static ScriptLine Line(string tag, params int[] values)
    {
        var v = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            v[i] = I(values[i]);
        }
        return new ScriptLine(tag, v);
    }

    /// <summary>[coin info] 行:难度名与两个整数交替给出。</summary>
    private static ScriptLine CoinInfo(params object[] items)
    {
        var v = new ScriptValue[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            v[i] = items[i] is string s ? S(s) : I((int)items[i]);
        }
        return new ScriptLine("[coin info]", v);
    }

    [Fact]
    public void WithoutAnyTagTheCoinsAreUnlimitedButNeverFree()
    {
        DungeonCoinRules rules = DungeonCoinRules.Parse(new[] { Line("[name]", 1) });
        Assert.Equal(-1, rules.CoinLimit);
        Assert.Equal(0, rules.PartyMemberCoinLimit);
        Assert.Equal(0, rules.DifficultyEntries);
        Assert.Equal(CoinVerdict.Pay, rules.Check(0, 0, 1, true));
        Assert.Equal(CoinVerdict.Pay, rules.Check(0, 500, 1, true));
        Assert.Equal(CoinVerdict.NoCoin, rules.Check(0, 0, 1, false));
        Assert.Equal(CoinVerdict.Pay, DungeonCoinRules.Parse(null).Check(2, 9, 1, true));
    }

    [Fact]
    public void TheDungeonLimitBlocksOnceTheUsedCountReachesIt()
    {
        DungeonCoinRules rules = DungeonCoinRules.Parse(new[] { Line("[coin limit]", 3) });
        Assert.Equal(3, rules.CoinLimit);
        Assert.Equal(CoinVerdict.Pay, rules.Check(0, 0, 1, true));
        Assert.Equal(CoinVerdict.Pay, rules.Check(0, 2, 1, true));
        Assert.Equal(CoinVerdict.LimitReached, rules.Check(0, 3, 1, true));
        Assert.Equal(CoinVerdict.LimitReached, rules.Check(0, 4, 1, true));
    }

    [Fact]
    public void AZeroLimitMeansNotEvenTheFirstCoinAndMinusOneMeansUnlimited()
    {
        Assert.Equal(CoinVerdict.LimitReached, DungeonCoinRules.Parse(new[] { Line("[coin limit]", 0) }).Check(0, 0, 1, true));
        Assert.Equal(CoinVerdict.Pay, DungeonCoinRules.Parse(new[] { Line("[coin limit]", -1) }).Check(0, 99, 1, true));
    }

    [Fact]
    public void ThePartyLimitIsMembersTimesTheMemberLimitAndZeroMeansNoSuchLimit()
    {
        DungeonCoinRules rules = DungeonCoinRules.Parse(new[] { Line("[party member coin limit]", 2) });
        Assert.Equal(CoinVerdict.Pay, rules.Check(0, 1, 1, true));
        Assert.Equal(CoinVerdict.LimitReached, rules.Check(0, 2, 1, true));
        Assert.Equal(CoinVerdict.Pay, rules.Check(0, 3, 2, true));          // 两个人:上限 4
        Assert.Equal(CoinVerdict.LimitReached, rules.Check(0, 4, 2, true));
        DungeonCoinRules none = DungeonCoinRules.Parse(new[] { Line("[party member coin limit]", 0) });
        Assert.Equal(CoinVerdict.Pay, none.Check(0, 50, 1, true));
    }

    [Fact]
    public void CoinInfoIsReadPerDifficultyWithTheNamesMappingToZeroThroughThree()
    {
        DungeonCoinRules rules = DungeonCoinRules.Parse(new[]
        {
            CoinInfo("[easy]", -1, 0, "[medium]", 8, 1, "[hard]", 6, 1, "[ultimate]", 4, 1),
        });
        Assert.Equal(4, rules.DifficultyEntries);
        Assert.True(rules.TryGetDifficulty(0, out int limit0, out bool pay0));
        Assert.Equal((-1, false), (limit0, pay0));
        Assert.True(rules.TryGetDifficulty(1, out int limit1, out bool pay1));
        Assert.Equal((8, true), (limit1, pay1));
        Assert.True(rules.TryGetDifficulty(3, out int limit3, out bool pay3));
        Assert.Equal((4, true), (limit3, pay3));
        Assert.False(rules.TryGetDifficulty(4, out int limit4, out bool pay4));
        Assert.Equal((-1, true), (limit4, pay4));
        Assert.True(rules.IsFree(0));
        Assert.False(rules.IsFree(1));
        Assert.False(rules.IsFree(4));
    }

    [Fact]
    public void ADifficultyLimitBlocksOnlyThatDifficulty()
    {
        DungeonCoinRules rules = DungeonCoinRules.Parse(new[] { CoinInfo("[medium]", 2, 1, "[hard]", -1, 1) });
        Assert.Equal(CoinVerdict.Pay, rules.Check(1, 1, 1, true));
        Assert.Equal(CoinVerdict.LimitReached, rules.Check(1, 2, 1, true));
        Assert.Equal(CoinVerdict.Pay, rules.Check(2, 40, 1, true));   // −1:不限
        Assert.Equal(CoinVerdict.Pay, rules.Check(0, 40, 1, true));   // 没写的难度:不受这一条管
    }

    [Fact]
    public void AFreeRevivalDifficultyIgnoresMissingCoinsAndEveryLimit()
    {
        DungeonCoinRules rules = DungeonCoinRules.Parse(new[]
        {
            Line("[coin limit]", 0),
            Line("[party member coin limit]", 1),
            CoinInfo("[easy]", 0, 0, "[medium]", 8, 1),
        });
        Assert.Equal(CoinVerdict.Free, rules.Check(0, 99, 1, false));
        Assert.Equal(CoinVerdict.LimitReached, rules.Check(1, 99, 1, true));
    }

    [Fact]
    public void TheCheckOrderIsFreeThenNoCoinThenPartyThenDifficultyThenDungeonLimit()
    {
        // 没有币永远先于上限
        DungeonCoinRules rules = DungeonCoinRules.Parse(new[] { Line("[coin limit]", 1), Line("[party member coin limit]", 1) });
        Assert.Equal(CoinVerdict.NoCoin, rules.Check(0, 5, 1, false));
        Assert.Equal(CoinVerdict.LimitReached, rules.Check(0, 5, 1, true));
    }

    [Fact]
    public void MalformedCoinInfoIsIgnoredPieceByPiece()
    {
        // 未知的难度名、缺数字的尾巴、数字前没有难度名:都不产生条目;重复的难度先写的算数(std::map::insert 不覆盖)
        DungeonCoinRules rules = DungeonCoinRules.Parse(new[]
        {
            CoinInfo(7, 7, "[nightmare]", 1, 1, "[easy]", 5, 1, "[easy]", 9, 0, "[hard]", 3),
        });
        Assert.Equal(1, rules.DifficultyEntries);
        Assert.True(rules.TryGetDifficulty(0, out int limit, out bool pay));
        Assert.Equal((5, true), (limit, pay));
    }

    [Fact]
    public void TagLinesWithoutNumbersLeaveTheDefaults()
    {
        DungeonCoinRules rules = DungeonCoinRules.Parse(new[] { Line("[coin limit]"), Line("[party member coin limit]"), Line("[coin info]") });
        Assert.Equal((-1, 0, 0), (rules.CoinLimit, rules.PartyMemberCoinLimit, rules.DifficultyEntries));
    }
}
