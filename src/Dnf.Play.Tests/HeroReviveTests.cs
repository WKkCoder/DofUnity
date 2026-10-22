using Dnf.Game;

namespace Dnf.Play.Tests;

/// <summary>
/// 主角死后的复活窗口(<see cref="HeroRevive"/>)与复活币库存(<see cref="ReviveCoins"/>,证据 E-115):
/// 窗口开着时失败计时在走(10 秒,走完置失败)、按键复活要过币规则(免费 → 没币 → 上限)、复活作废计时、已用次数在整座副本里累计。
/// 数据全是合成的(规则用 <see cref="DungeonCoinRules"/> 的构造函数直接给,数字不是原版的)。
/// </summary>
public class HeroReviveTests
{
    private static DungeonCoinRules Rules(int coinLimit = -1, int partyLimit = 0, bool freeAtNormal = false)
    {
        var difficulties = new Dictionary<int, (int Limit, bool Pay)>();
        if (freeAtNormal)
        {
            difficulties[0] = (-1, false);
        }
        return new DungeonCoinRules(coinLimit, partyLimit, difficulties);
    }

    // ------------------------------------------------------------------ ReviveCoins

    [Fact]
    public void TheCoinStockCountsDownAndNeverGoesNegative()
    {
        var coins = new ReviveCoins(2);
        Assert.Equal((2, true), (coins.Count, coins.Has));
        Assert.True(coins.TryTake());
        Assert.True(coins.TryTake());
        Assert.Equal((0, false), (coins.Count, coins.Has));
        Assert.False(coins.TryTake());
        coins.Add(3);
        Assert.Equal(3, coins.Count);
        coins.Add(-10);
        Assert.Equal(0, coins.Count);
        Assert.Equal(ReviveCoins.DefaultStock, new ReviveCoins().Count);
        Assert.Equal(0, new ReviveCoins(-4).Count);
    }

    // ------------------------------------------------------------------ 失败计时

    [Fact]
    public void TheWindowIsClosedUntilTheHeroLiesDownThenCountsTenSecondsDown()
    {
        var revive = new HeroRevive(Rules(), new ReviveCoins(1));
        Assert.Equal((false, false, 0, 0.0), (revive.Open, revive.Failed, revive.Seconds, revive.RemainingMs));
        revive.Tick(5000);                                                   // 没开窗口时计时不走
        Assert.Equal((false, false), (revive.Open, revive.Failed));

        revive.Begin();
        Assert.Equal((true, false, 1, HeroRevive.CountdownMs), (revive.Open, revive.Failed, revive.Deaths, revive.RemainingMs));
        Assert.Equal(10, revive.Seconds);
        revive.Tick(1);
        Assert.Equal(10, revive.Seconds);                                     // 9.999 秒向上取整还是 10
        revive.Tick(999);
        Assert.Equal(9, revive.Seconds);
        revive.Tick(8000);
        Assert.Equal((true, 1), (revive.Open, revive.Seconds));
        revive.Begin();                                                        // 已经开着再 Begin 不重置、不多算一次死亡
        Assert.Equal((1, 1), (revive.Deaths, revive.Seconds));
    }

    [Fact]
    public void TheCountdownRunningOutFailsTheDungeonAndNothingReopensIt()
    {
        var revive = new HeroRevive(Rules(), new ReviveCoins(1));
        revive.Begin();
        revive.Tick(HeroRevive.CountdownMs - 1);
        Assert.False(revive.Failed);
        revive.Tick(1);
        Assert.Equal((false, true, 0, 0.0), (revive.Open, revive.Failed, revive.Seconds, revive.RemainingMs));
        Assert.False(revive.TryRevive());                                      // 窗口关了:按键没用
        revive.Begin();                                                        // 已经失败:不再开窗口
        Assert.Equal((false, 1), (revive.Open, revive.Deaths));
        Assert.Equal(1, revive.Coins.Count);                                   // 没有扣币
    }

    [Fact]
    public void ATickBiggerThanTheWholeCountdownStillJustFails()
    {
        var revive = new HeroRevive(Rules(), new ReviveCoins(0));
        revive.Begin();
        revive.Tick(1e9);
        Assert.Equal((false, true), (revive.Open, revive.Failed));
        revive.Tick(-5);                                                       // 负的时间步什么也不做
        Assert.True(revive.Failed);
    }

    // ------------------------------------------------------------------ 复活

    [Fact]
    public void ReviveSpendsACoinCountsItAndCancelsTheFailureTimer()
    {
        var coins = new ReviveCoins(2);
        var revive = new HeroRevive(Rules(), coins);
        revive.Begin();
        revive.Tick(4000);
        Assert.Equal((CoinVerdict.Pay, true), (revive.Verdict, revive.CanRevive));

        Assert.True(revive.TryRevive());
        Assert.Equal((false, false, 1, 1, 1), (revive.Open, revive.Failed, revive.Used, revive.Revives, coins.Count));
        Assert.Equal(0, revive.Seconds);
        revive.Tick(HeroRevive.CountdownMs * 2);                               // 复活之后计时作废:不会再失败
        Assert.False(revive.Failed);
        Assert.False(revive.TryRevive());                                      // 窗口关了:再按没有用,也不多扣币
        Assert.Equal(1, coins.Count);
    }

    [Fact]
    public void ADeathAfterARevivalOpensAFreshWindowAndTheUsedCountKeepsAccumulating()
    {
        var coins = new ReviveCoins(5);
        var revive = new HeroRevive(Rules(coinLimit: 2), coins);
        revive.Begin();
        revive.Tick(7000);
        Assert.True(revive.TryRevive());
        revive.Begin();                                                        // 又死了:新窗口,从头数 10 秒
        Assert.Equal((true, 2, HeroRevive.CountdownMs), (revive.Open, revive.Deaths, revive.RemainingMs));
        Assert.True(revive.TryRevive());
        Assert.Equal((2, 2, 3), (revive.Used, revive.Revives, coins.Count));

        revive.Begin();                                                        // 第三次死:副本上限 2 次已用完
        Assert.Equal((CoinVerdict.LimitReached, false), (revive.Verdict, revive.CanRevive));
        Assert.False(revive.TryRevive());
        Assert.Equal((true, 3), (revive.Open, coins.Count));                   // 窗口还开着、币没动
        revive.Tick(HeroRevive.CountdownMs);
        Assert.True(revive.Failed);
    }

    [Fact]
    public void WithoutCoinsOrAStockObjectOnlyAFreeRevivalWorks()
    {
        var empty = new HeroRevive(Rules(), new ReviveCoins(0));
        empty.Begin();
        Assert.Equal((CoinVerdict.NoCoin, false), (empty.Verdict, empty.CanRevive));
        Assert.False(empty.TryRevive());
        Assert.True(empty.Open);

        var none = new HeroRevive(Rules(), null);                              // 没给库存:按没有币算
        none.Begin();
        Assert.Equal(CoinVerdict.NoCoin, none.Verdict);
        Assert.False(none.TryRevive());

        var free = new HeroRevive(Rules(freeAtNormal: true), null);            // 免费复活不需要库存
        free.Begin();
        Assert.Equal((CoinVerdict.Free, true), (free.Verdict, free.CanRevive));
        Assert.True(free.TryRevive());
        Assert.Equal((0, 1), (free.Used, free.Revives));
    }

    [Fact]
    public void AFreeRevivalCostsNothingAndIsNotCountedAgainstTheLimits()
    {
        var coins = new ReviveCoins(1);
        var revive = new HeroRevive(Rules(coinLimit: 0, partyLimit: 1, freeAtNormal: true), coins);
        for (int i = 0; i < 4; i++)
        {
            revive.Begin();
            Assert.True(revive.TryRevive());
        }
        Assert.Equal((0, 4, 1), (revive.Used, revive.Revives, coins.Count));
    }

    [Fact]
    public void ThePartyLimitScalesWithTheMemberCountAndTheLimitsAreCheckedByUsedCount()
    {
        var solo = new HeroRevive(Rules(partyLimit: 1), new ReviveCoins(9));
        solo.Begin();
        Assert.True(solo.TryRevive());                                         // 1 人 × 1 次
        solo.Begin();
        Assert.Equal(CoinVerdict.LimitReached, solo.Verdict);

        var pair = new HeroRevive(Rules(partyLimit: 1), new ReviveCoins(9), members: 2);
        pair.Begin();
        Assert.True(pair.TryRevive());
        pair.Begin();
        Assert.Equal(CoinVerdict.Pay, pair.Verdict);                           // 2 人 × 1 次 = 2
        Assert.True(pair.TryRevive());
        pair.Begin();
        Assert.Equal(CoinVerdict.LimitReached, pair.Verdict);

        var closed = new HeroRevive(Rules(coinLimit: 0), new ReviveCoins(9));   // 上限 0:一次也不能用
        closed.Begin();
        Assert.Equal(CoinVerdict.LimitReached, closed.Verdict);
    }

    [Fact]
    public void ARulesObjectIsOptional()
    {
        var revive = new HeroRevive(null, new ReviveCoins(1));
        revive.Begin();
        Assert.Equal(CoinVerdict.Pay, revive.Verdict);                          // 没有规则 = 默认:不限次数、不免费
        Assert.True(revive.TryRevive());
    }
}
