namespace Dnf.Game.Tests;

/// <summary>
/// 宠物实例的状态机:新建与读档、饱食随时间结算、喂食、加经验与升级、状态切换(装备、卸下、战斗开始与结束)与复活。
/// 宠物脚本与经验表全是合成的:经验表 10、30、60、100(第 2 到 5 级),所以经验 0 到 9 是 1 级,10 到 29 是 2 级,30 到 59 是 3 级,
/// 60 到 99 是 4 级,100 起表等级跳到 98、被脚本的 [max level](这里是 5)夹住。期望值手算。
/// </summary>
public class PetInstanceTests
{
    private static readonly PetExpTable Table = new PetExpTable(10, 30, 60, 100);

    private static PetDef Def(int max = 5)
    {
        return new PetDef(901, "creature/test/p901.cre") { MaxLevel = max };
    }

    private static PetDef GrowDef(int max = 5, int perLevel = 1)
    {
        PetDef def = Def(max);
        def.Grow.UsesExpItem = true;
        def.Grow.ATypeItemIndex = 1;
        def.Grow.ItemsPerLevel = perLevel;
        return def;
    }

    private static PetInstance Hatch(PetDef def = null, int now = 0) => PetInstance.Hatch(1, 950, def ?? Def(), Table, now);

    private static PetInstance Load(int exp, int stomach, PetDef def = null, int now = 0) => PetInstance.Load(1, 950, def ?? Def(), Table, exp, stomach, now);

    // ------------------------------------------------------------------ 新建与读档

    [Fact]
    public void AHatchedPetIsLevelOneWithNoExpAFullStomachAndNotEquipped()
    {
        PetInstance pet = Hatch();
        Assert.Equal(1, pet.Uid);
        Assert.Equal(950, pet.ItemCode);
        Assert.Equal(PetKind.Normal, pet.Kind);
        Assert.Equal(1, pet.Level);
        Assert.Equal(0, pet.Exp);
        Assert.Equal(PetState.Stored, pet.State);
        Assert.Equal(100, pet.Stomach(0));
        Assert.False(pet.IsStarved(0));
    }

    [Fact]
    public void HatchingIgnoresTheScriptsStartLevel()
    {
        PetDef def = Def(5);
        def.StartLevel = 4;
        Assert.Equal(1, Hatch(def).Level);
    }

    [Fact]
    public void LoadingKeepsExpAndStomachAndRecomputesTheLevel()
    {
        PetInstance pet = Load(30, 80);
        Assert.Equal(30, pet.Exp);
        Assert.Equal(3, pet.Level);
        Assert.Equal(80, pet.Stomach(0));
        Assert.Equal(PetState.Stored, pet.State);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 2)]
    [InlineData(29, 2)]
    [InlineData(30, 3)]
    [InlineData(59, 3)]
    [InlineData(60, 4)]
    [InlineData(99, 4)]
    [InlineData(100, 5)]
    [InlineData(100000, 5)]
    public void TheLevelIsComputedFromTheTotalExpAndClampedToTheMaxLevel(int exp, int level)
    {
        Assert.Equal(level, Load(exp, 100).Level);
    }

    [Fact]
    public void TheLevelIsNeverAboveTheScriptsMaxLevelAndNeverBelowOne()
    {
        Assert.Equal(3, Load(100000, 100, Def(3)).Level);
        Assert.Equal(1, Load(100000, 100, Def(1)).Level);
        Assert.Equal(1, Load(100000, 100, Def(0)).Level);
        Assert.Equal(1, Load(100000, 100, Def(-4)).Level);
    }

    [Fact]
    public void LoadingClampsOutOfRangeValues()
    {
        PetInstance negative = Load(-5, -3);
        Assert.Equal(0, negative.Exp);
        Assert.Equal(1, negative.Level);
        Assert.Equal(0, negative.Stomach(0));
        Assert.True(negative.Settle(0) >= 0f);
        Assert.Equal(100, Load(0, 250).Stomach(0));
        Assert.Equal(100, Load(0, int.MaxValue).Stomach(0));
        Assert.Equal(0, Load(0, int.MinValue).Stomach(0));
    }

    [Fact]
    public void AMissingDefinitionOrTableIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => PetInstance.Hatch(1, 950, null, Table, 0));
        Assert.Throws<ArgumentNullException>(() => PetInstance.Hatch(1, 950, Def(), null, 0));
        Assert.Throws<ArgumentNullException>(() => PetInstance.Load(1, 950, null, Table, 0, 0, 0));
        Assert.Throws<ArgumentNullException>(() => PetInstance.Load(1, 950, Def(), null, 0, 0, 0));
    }

    [Fact]
    public void AGrowKindLevelIsTheItemsFedDividedByTheItemsPerLevelPlusOne()
    {
        // 成长型:经验的高 16 位是已喂物品数。每级 3 个物品:0、2 个是 1 级,3 个是 2 级,6 个是 3 级。
        PetDef def = GrowDef(10, 3);
        Assert.Equal(1, Load(0, 100, def).Level);
        Assert.Equal(1, Load(2 << 16, 100, def).Level);
        Assert.Equal(2, Load(3 << 16, 100, def).Level);
        Assert.Equal(3, Load(6 << 16, 100, def).Level);
        // 低 16 位不参与;每级物品数写 0 按 25;再被最高等级夹住。
        Assert.Equal(2, Load((3 << 16) | 0xFFFF, 100, def).Level);
        Assert.Equal(1, Load(24 << 16, 100, GrowDef(10, 0)).Level);
        Assert.Equal(2, Load(25 << 16, 100, GrowDef(10, 0)).Level);
        Assert.Equal(4, Load(60 << 16, 100, GrowDef(4, 1)).Level);
    }

    // ------------------------------------------------------------------ 饱食随时间

    [Fact]
    public void StomachFallsWhileDrainingAndRisesWhileChargingOrStored()
    {
        PetInstance draining = Load(0, 50);
        draining.Equip(true, 0);
        Assert.Equal(38, draining.Stomach(720));

        PetInstance charging = Load(0, 50);
        charging.Equip(false, 0);
        Assert.Equal(52, charging.Stomach(720));

        PetInstance stored = Load(0, 50);
        Assert.Equal(52, stored.Stomach(720));
    }

    [Fact]
    public void ReadingTheStomachTwiceDoesNotApplyTheSameTimeTwice()
    {
        PetInstance pet = Load(0, 50);
        pet.Equip(true, 0);
        Assert.Equal(49f, pet.Settle(60));
        Assert.Equal(49f, pet.Settle(60));
        Assert.Equal(48f, pet.Settle(120));
    }

    [Fact]
    public void TheStomachStaysBetweenZeroAndOneHundred()
    {
        PetInstance draining = Load(0, 3);
        draining.Equip(true, 0);
        Assert.Equal(0f, draining.Settle(100000));
        Assert.Equal(0, draining.Stomach(200000));

        PetInstance charging = Load(0, 99);
        charging.Equip(false, 0);
        Assert.Equal(100f, charging.Settle(100000));
    }

    [Fact]
    public void TheIntegerStomachIsTheTruncatedSettledValue()
    {
        PetInstance pet = Load(0, 100);
        pet.Equip(true, 0);
        Assert.Equal(99, pet.Stomach(59));
        Assert.Equal(98, pet.Stomach(61));
    }

    [Fact]
    public void AClockGoingBackwardsDoesNotChangeTheStomach()
    {
        PetInstance pet = Load(0, 50, null, 100);   // 计时起点就是第 100 秒
        pet.Equip(true, 100);
        float settled = pet.Settle(220);
        Assert.Equal(48f, settled);
        Assert.Equal(48f, pet.Settle(100));
        Assert.Equal(48f, pet.Settle(-1000));
    }

    [Fact]
    public void AGrowKindStomachIsAlwaysFull()
    {
        PetInstance pet = Load(0, 7, GrowDef());
        Assert.Equal(100f, pet.Settle(0));
        pet.Equip(true, 0);
        Assert.Equal(100f, pet.Settle(100000));
        Assert.Equal(100, pet.Stomach(100000));
        Assert.False(pet.IsStarved(100000));
    }

    // ------------------------------------------------------------------ 饿死的边界

    [Fact]
    public void StarvedMeansStrictlyBelowOneAndOneItselfIsAlive()
    {
        Assert.True(Load(0, 0).IsStarved(0));
        Assert.False(Load(0, 1).IsStarved(0));

        PetInstance pet = Load(0, 2);
        pet.Equip(true, 0);
        Assert.False(pet.IsStarved(60));
        Assert.True(pet.IsStarved(61));
    }

    // ------------------------------------------------------------------ 喂食

    [Fact]
    public void FeedingAddsThirtyAndCapsAtOneHundredWithoutRefusingAFullPet()
    {
        PetInstance pet = Load(0, 50);
        Assert.Equal(PetFeedResult.Fed, pet.Feed(0));
        Assert.Equal(80, pet.Stomach(0));
        Assert.Equal(PetFeedResult.Fed, pet.Feed(0));
        Assert.Equal(100, pet.Stomach(0));
        Assert.Equal(PetFeedResult.Fed, pet.Feed(0));
        Assert.Equal(100, pet.Stomach(0));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(69, 99)]
    [InlineData(70, 100)]
    [InlineData(71, 100)]
    [InlineData(99, 100)]
    public void FeedingStopsExactlyAtTheCap(int before, int after)
    {
        PetInstance pet = Load(0, before);
        pet.Feed(0);
        Assert.Equal(after, pet.Stomach(0));
    }

    [Fact]
    public void FeedingAStarvedPetRevivesItAndSaysSo()
    {
        PetInstance pet = Load(0, 0);
        Assert.Equal(PetFeedResult.Revived, pet.Feed(0));
        Assert.Equal(30, pet.Stomach(0));
        Assert.False(pet.IsStarved(0));
        // 刚好是 1 的不算饿死:喂了只是 Fed。
        Assert.Equal(PetFeedResult.Fed, Load(0, 1).Feed(0));
    }

    [Fact]
    public void FeedingSettlesTheElapsedTimeFirst()
    {
        PetInstance pet = Load(0, 40);
        pet.Equip(true, 0);
        pet.Feed(600);
        Assert.Equal(60f, pet.Settle(600));
    }

    [Fact]
    public void FeedingWhileDrainingStillStopsAtTheCapBecauseDrainingNeverCapsOnRead()
    {
        // 消耗中读取只夹下限、不夹上限,所以喂食这一步自己要夹到 100;存放或回补时读取会把超出的部分夹掉,验不出这一点。
        PetInstance pet = Load(0, 90);
        pet.Equip(true, 0);
        Assert.Equal(PetFeedResult.Fed, pet.Feed(0));
        Assert.Equal(100f, pet.Settle(0));
    }

    [Fact]
    public void FeedingAPetThatStarvedWhileDrainingRevivesItFromZeroNotFromTheStaleValue()
    {
        // 5 点饱食在消耗中过了 600 秒(本该掉 10 点)已经耗尽、夹在 0:喂食先结算时间,看到的是饿死,加 30 得 30;
        // 拿结算前的 5 点来判断,就会当成没饿死,加完再结算只剩 25。
        PetInstance pet = Load(0, 5);
        pet.Equip(true, 0);
        Assert.Equal(PetFeedResult.Revived, pet.Feed(600));
        Assert.Equal(30f, pet.Settle(600));
        Assert.False(pet.IsStarved(600));
    }

    [Fact]
    public void AGrowKindDoesNotEat()
    {
        PetInstance pet = Load(0, 100, GrowDef());
        Assert.Equal(PetFeedResult.NotFeedable, pet.Feed(0));
        Assert.Equal(100, pet.Stomach(0));
    }

    // ------------------------------------------------------------------ 加经验

    [Fact]
    public void GainingExpAddsToTheTotal()
    {
        PetInstance pet = Hatch();
        PetGainResult r = pet.GainExp(5, 0, 0f);
        Assert.Equal(PetGainOutcome.Gained, r.Outcome);
        Assert.True(r.Gained);
        Assert.False(r.LeveledUp);
        Assert.Equal(5, r.Added);
        Assert.Equal(5, pet.Exp);
        Assert.Equal(1, pet.Level);
    }

    [Fact]
    public void LevellingUpDoesNotDeductExpBecauseExpIsACumulativeTotal()
    {
        PetInstance pet = Hatch();
        pet.GainExp(5, 0, 0f);
        PetGainResult up = pet.GainExp(5, 0, 0f);
        Assert.True(up.LeveledUp);
        Assert.Equal(2, pet.Level);
        Assert.Equal(10, pet.Exp);

        PetGainResult more = pet.GainExp(20, 0, 0f);
        Assert.True(more.LeveledUp);
        Assert.Equal(30, pet.Exp);
        Assert.Equal(3, pet.Level);

        PetGainResult same = pet.GainExp(1, 0, 0f);
        Assert.False(same.LeveledUp);
        Assert.Equal(31, pet.Exp);
        Assert.Equal(3, pet.Level);
    }

    [Fact]
    public void AMultiLevelJumpIsOneLevelUpEventAndTheLevelIsRecomputedFromTheTotal()
    {
        PetInstance pet = Hatch();
        PetGainResult r = pet.GainExp(70, 0, 0f);
        Assert.True(r.LeveledUp);
        Assert.Equal(4, pet.Level);
        Assert.Equal(70, pet.Exp);
    }

    [Theory]
    [InlineData(10, 0f, 10)]
    [InlineData(10, 50f, 15)]
    [InlineData(10, 25f, 12)]
    [InlineData(1, 99.9f, 1)]
    [InlineData(7, 50f, 10)]
    public void TheBonusIsAPercentOfTheBaseAmountTruncated(int amount, float bonus, int added)
    {
        PetInstance pet = Hatch();
        PetGainResult r = pet.GainExp(amount, 0, bonus);
        Assert.Equal(added, r.Added);
        Assert.Equal(added, pet.Exp);
    }

    [Fact]
    public void ANegativeBonusCannotDriveTheTotalBelowZero()
    {
        PetInstance pet = Hatch();
        PetGainResult r = pet.GainExp(10, 0, -500f);
        Assert.Equal(PetGainOutcome.Gained, r.Outcome);
        Assert.Equal(0, pet.Exp);
        Assert.Equal(0, r.Added);
        Assert.Equal(1, pet.Level);
    }

    [Fact]
    public void AtTheMaxLevelNothingIsGained()
    {
        PetInstance pet = Load(60, 100, Def(4));
        Assert.Equal(4, pet.Level);
        PetGainResult r = pet.GainExp(10, 0, 0f);
        Assert.Equal(PetGainOutcome.AtMaxLevel, r.Outcome);
        Assert.False(r.Gained);
        Assert.Equal(0, r.Added);
        Assert.Equal(60, pet.Exp);
    }

    [Fact]
    public void OneLevelBelowTheMaxLevelStillGainsAndTheLevelIsClamped()
    {
        // 99 经验是 4 级(上限 5):加 10 以后总经验 109,表等级跳到 98,被夹成 5;之后满级,不再涨。
        PetInstance pet = Load(99, 100);
        Assert.Equal(4, pet.Level);
        PetGainResult r = pet.GainExp(10, 0, 0f);
        Assert.Equal(PetGainOutcome.Gained, r.Outcome);
        Assert.True(r.LeveledUp);
        Assert.Equal(5, pet.Level);
        Assert.Equal(109, pet.Exp);
        Assert.Equal(PetGainOutcome.AtMaxLevel, pet.GainExp(1, 0, 0f).Outcome);
        Assert.Equal(109, pet.Exp);
    }

    [Fact]
    public void AStarvedPetGainsNothingUntilItIsFed()
    {
        PetInstance pet = Load(0, 0);
        PetGainResult r = pet.GainExp(5, 0, 0f);
        Assert.Equal(PetGainOutcome.Starved, r.Outcome);
        Assert.Equal(0, pet.Exp);
        pet.Feed(0);
        Assert.Equal(PetGainOutcome.Gained, pet.GainExp(5, 0, 0f).Outcome);
        Assert.Equal(5, pet.Exp);
    }

    [Fact]
    public void ADrainedPetStartsRefusingExpTheMomentItsStomachDropsBelowOne()
    {
        PetInstance pet = Load(0, 2);
        pet.Equip(true, 0);
        Assert.Equal(PetGainOutcome.Gained, pet.GainExp(1, 60, 0f).Outcome);
        Assert.Equal(PetGainOutcome.Starved, pet.GainExp(1, 61, 0f).Outcome);
        Assert.Equal(1, pet.Exp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ANonPositiveAmountIsRejected(int amount)
    {
        PetInstance pet = Hatch();
        PetGainResult r = pet.GainExp(amount, 0, 50f);
        Assert.Equal(PetGainOutcome.InvalidAmount, r.Outcome);
        Assert.Equal(0, pet.Exp);
    }

    [Fact]
    public void AGrowKindDoesNotTakePlainExp()
    {
        PetInstance pet = Load(0, 100, GrowDef());
        PetGainResult r = pet.GainExp(5, 0, 0f);
        Assert.Equal(PetGainOutcome.NotGainable, r.Outcome);
        Assert.Equal(0, pet.Exp);
        Assert.Equal(1, pet.Level);
    }

    [Fact]
    public void TheGainGatesAreCheckedInTheDocumentedOrder()
    {
        // 成长型先于数量检查。
        Assert.Equal(PetGainOutcome.NotGainable, Load(0, 100, GrowDef()).GainExp(0, 0, 0f).Outcome);
        // 数量检查先于满级。
        Assert.Equal(PetGainOutcome.InvalidAmount, Load(60, 100, Def(4)).GainExp(0, 0, 0f).Outcome);
        // 满级先于饿死。
        Assert.Equal(PetGainOutcome.AtMaxLevel, Load(60, 0, Def(4)).GainExp(5, 0, 0f).Outcome);
    }

    [Fact]
    public void TheTotalExpSaturatesAtTheIntRangeInsteadOfWrapping()
    {
        // 全零经验表:任何经验的表等级都是 98,最高等级 99 时一直还能涨。
        var zeroTable = new PetExpTable();
        PetInstance pet = PetInstance.Load(1, 950, Def(99), zeroTable, int.MaxValue - 5, 100, 0);
        Assert.Equal(98, pet.Level);
        PetGainResult r = pet.GainExp(100, 0, 0f);
        Assert.Equal(PetGainOutcome.Gained, r.Outcome);
        Assert.Equal(int.MaxValue, pet.Exp);
        Assert.Equal(5, r.Added);
        PetGainResult again = pet.GainExp(1, 0, 0f);
        Assert.Equal(PetGainOutcome.Gained, again.Outcome);
        Assert.Equal(0, again.Added);
        Assert.Equal(int.MaxValue, pet.Exp);

        // 巨大的加成同样饱和。
        PetInstance big = PetInstance.Load(1, 950, Def(99), zeroTable, 7, 100, 0);
        big.GainExp(1000, 0, 1e9f);
        Assert.Equal(int.MaxValue, big.Exp);
    }

    // ------------------------------------------------------------------ 状态切换

    [Fact]
    public void EquippingChoosesDrainingInBattleAndChargingOtherwise()
    {
        PetInstance fighting = Load(0, 50);
        fighting.Equip(true, 0);
        Assert.Equal(PetState.Draining, fighting.State);

        PetInstance idle = Load(0, 50);
        idle.Equip(false, 0);
        Assert.Equal(PetState.Charging, idle.State);
    }

    [Fact]
    public void DismantlingGoesToStored()
    {
        PetInstance pet = Load(0, 50);
        pet.Equip(true, 0);
        PetStateChange change = pet.Dismantle(0);
        Assert.Equal(PetState.Stored, pet.State);
        Assert.False(change.Revived);
        Assert.Equal(0, change.DeathCheckDelay);
    }

    [Fact]
    public void EnteringDrainingSchedulesADeathCheckOfTheSettledStomachTimesSixty()
    {
        PetInstance pet = Load(0, 50);
        Assert.Equal(3000, pet.Equip(true, 0).DeathCheckDelay);

        // 先把离上次结算的时间折算进去:存放中 3600 秒回 10 点,到 60 点再进战斗。
        PetInstance stored = Load(0, 50);
        Assert.Equal(3600, stored.Equip(true, 3600).DeathCheckDelay);
    }

    [Fact]
    public void TheDeathCheckDelayTruncatesAFractionalStomachInsteadOfRounding()
    {
        // 存放中 4 秒回 4/360 点:饱食 50.0111…,× 60 = 3000.67,向零取整得 3000(四舍五入会是 3001)。
        PetInstance pet = Load(0, 50);
        Assert.Equal(3000, pet.Equip(true, 4).DeathCheckDelay);
    }

    [Fact]
    public void ReEnteringTheSameStateDoesNothingSpecial()
    {
        PetInstance pet = Load(0, 50);
        Assert.Equal(3000, pet.Equip(true, 0).DeathCheckDelay);
        PetStateChange again = pet.SetState(PetState.Draining, 0);
        Assert.Equal(0, again.DeathCheckDelay);
        Assert.False(again.Revived);
    }

    [Fact]
    public void EquippingAStarvedPetOutOfBattleRevivesItToExactlyOne()
    {
        PetInstance pet = Load(0, 0);
        PetStateChange change = pet.Equip(false, 0);
        Assert.True(change.Revived);
        Assert.Equal(PetState.Charging, pet.State);
        Assert.Equal(1f, pet.Settle(0));
        Assert.False(pet.IsStarved(0));
    }

    [Fact]
    public void ReviveHappensOnlyWhenEnteringChargingNotDrainingOrStored()
    {
        PetInstance pet = Load(0, 0);
        PetStateChange inBattle = pet.Equip(true, 0);
        Assert.False(inBattle.Revived);
        Assert.True(pet.IsStarved(0));
        Assert.Equal(0, inBattle.DeathCheckDelay);

        // 战斗结束切回回补状态:这时才复活。
        PetStateChange ended = pet.SetFighting(false, 0);
        Assert.True(ended.Revived);
        Assert.Equal(1f, pet.Settle(0));

        // 卸下不复活:一只饿死的宠物卸下后仍是饿死的。
        PetInstance other = Load(0, 0);
        other.Equip(true, 0);
        PetStateChange dismantled = other.Dismantle(0);
        Assert.False(dismantled.Revived);
        Assert.True(other.IsStarved(0));
    }

    [Fact]
    public void ReviveSettlesTimeBeforeDecidingWhetherThePetIsStarved()
    {
        // 饱食 0 的宠物在存放中放了 720 秒,回了 2 点,装备时已经不是饿死状态,不触发复活,饱食保持结算后的 2。
        PetInstance pet = Load(0, 0);
        PetStateChange change = pet.Equip(false, 720);
        Assert.False(change.Revived);
        Assert.Equal(2f, pet.Settle(720));
    }

    [Fact]
    public void AGrowKindHasNeitherReviveNorDeathCheck()
    {
        PetInstance pet = Load(0, 0, GrowDef());
        PetStateChange fight = pet.Equip(true, 0);
        Assert.False(fight.Revived);
        Assert.Equal(0, fight.DeathCheckDelay);
        PetStateChange idle = pet.SetFighting(false, 0);
        Assert.False(idle.Revived);
        Assert.Equal(PetState.Charging, pet.State);
        Assert.Equal(100f, pet.Settle(0));
    }

    [Fact]
    public void SettingTheFightingFlagOnlyMovesBetweenDrainingAndCharging()
    {
        PetInstance stored = Load(0, 50);
        Assert.Equal(0, stored.SetFighting(true, 0).DeathCheckDelay);
        Assert.Equal(PetState.Stored, stored.State);
        stored.SetFighting(false, 0);
        Assert.Equal(PetState.Stored, stored.State);

        PetInstance charging = Load(0, 50);
        charging.Equip(false, 0);
        charging.SetFighting(false, 0);
        Assert.Equal(PetState.Charging, charging.State);
        PetStateChange start = charging.SetFighting(true, 0);
        Assert.Equal(PetState.Draining, charging.State);
        Assert.Equal(3000, start.DeathCheckDelay);
        PetStateChange again = charging.SetFighting(true, 0);
        Assert.Equal(PetState.Draining, charging.State);
        Assert.Equal(0, again.DeathCheckDelay);
    }

    [Fact]
    public void EachSegmentOfTheTimelineIsSettledAtItsOwnRate()
    {
        PetInstance pet = Load(0, 50);
        pet.Equip(false, 0);                        // 回补
        PetStateChange start = pet.SetFighting(true, 720);   // 50 + 2 = 52,进战斗
        Assert.Equal(3120, start.DeathCheckDelay);
        pet.SetFighting(false, 720 + 600);          // 52 - 10 = 42,战斗结束
        Assert.Equal(PetState.Charging, pet.State);
        Assert.Equal(42, pet.Stomach(720 + 600));
        Assert.Equal(44, pet.Stomach(720 + 600 + 720));
    }
}
