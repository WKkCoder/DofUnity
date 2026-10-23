using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 消耗品的数据与规则(证据 E-120):物品脚本里的回复与冷却字段、回复的线性发放、冷却表、快捷栏、使用检查。
/// 数值与名字全是合成的,只检验规则的形状,与原版数据无关。
/// </summary>
public class ConsumableTests
{
    // ------------------------------------------------------------------ 工具

    private static ItemInfo ParseStackable(TokenStreamBuilder b)
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return ItemScript.Parse(b.Bytes(), strings, ItemKind.Stackable, 5001, "stackable/test/p.stk");
    }

    private static ItemInfo Potion(int code, RecoveryEffect hp = null, RecoveryEffect mp = null, int coolMs = 0, int group = -1, int minLevel = 0)
    {
        return new ItemInfo(code, "stackable/test/p" + code + ".stk", ItemKind.Stackable)
        {
            StackableType = StackableTypes.Waste,
            HpRecovery = hp,
            MpRecovery = mp,
            CoolTimeMs = coolMs,
            CoolGroup = group,
            MinimumLevel = minLevel,
            StackLimit = 100,
        };
    }

    private static RecoveryEffect Flat(int amount, int ms) => new RecoveryEffect(false, amount, ms);

    // ------------------------------------------------------------------ 脚本字段

    [Fact]
    public void ScriptReadsHpAndMpRecoveryWithSignAmountAndDuration()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[stackable type]").Str("[waste]").Int(0);
        b.Tag("[hp recovery]").Str("+").Int(120).Int(3000).Str("myself");
        b.Tag("[mp recovery]").Str("%").Int(25).Int(1500).Str("myself");
        b.Tag("[cool time]").Int(8000);
        b.Tag("[cooltime group]").Int(4);
        ItemInfo item = ParseStackable(b);

        Assert.NotNull(item.HpRecovery);
        Assert.False(item.HpRecovery.Percent);
        Assert.Equal(120, item.HpRecovery.Amount);
        Assert.Equal(3000, item.HpRecovery.DurationMs);
        Assert.NotNull(item.MpRecovery);
        Assert.True(item.MpRecovery.Percent);
        Assert.Equal(25, item.MpRecovery.Amount);
        Assert.Equal(1500, item.MpRecovery.DurationMs);
        Assert.Equal(8000, item.CoolTimeMs);
        Assert.Equal(4, item.CoolGroup);
    }

    [Fact]
    public void ScriptWithoutRecoveryTagsHasNoEffectNoCooldownAndNoGroup()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[stackable type]").Str("[material]").Int(0);
        ItemInfo item = ParseStackable(b);

        Assert.Null(item.HpRecovery);
        Assert.Null(item.MpRecovery);
        Assert.Equal(0, item.CoolTimeMs);
        Assert.Equal(-1, item.CoolGroup);
    }

    [Fact]
    public void LaterRecoveryTagOverridesTheEarlierOne()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[hp recovery]").Str("+").Int(10).Int(500).Str("myself");
        b.Tag("[hp recovery]").Str("%").Int(50).Int(900).Str("myself");
        ItemInfo item = ParseStackable(b);

        Assert.True(item.HpRecovery.Percent);
        Assert.Equal(50, item.HpRecovery.Amount);
        Assert.Equal(900, item.HpRecovery.DurationMs);
    }

    [Fact]
    public void RecoveryWithOnlyAnAmountHasZeroDurationAndNegativeAmountsAreKept()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[hp recovery]").Str("+").Int(40);
        b.Tag("[mp recovery]").Str("+").Int(-30).Int(2000).Str("myself");
        ItemInfo item = ParseStackable(b);

        Assert.Equal(40, item.HpRecovery.Amount);
        Assert.Equal(0, item.HpRecovery.DurationMs);
        Assert.Equal(-30, item.MpRecovery.Amount);
    }

    [Fact]
    public void RecoveryIgnoresExtraValuesAndTagsAfterwardsStillParse()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[hp recovery]").Str("+").Int(7).Int(100).Int(999).Str("myself").Str("extra");
        b.Tag("[cool time]").Int(1234);
        ItemInfo item = ParseStackable(b);

        Assert.Equal(7, item.HpRecovery.Amount);
        Assert.Equal(100, item.HpRecovery.DurationMs);
        Assert.Equal(1234, item.CoolTimeMs);
    }

    // ------------------------------------------------------------------ 回复总量

    [Fact]
    public void FlatRecoveryIgnoresTheMaximum()
    {
        Assert.Equal(120, Flat(120, 0).TotalFor(1));
        Assert.Equal(120, Flat(120, 0).TotalFor(99999));
    }

    [Theory]
    [InlineData(1000, 30, 300)]
    [InlineData(100, 33, 33)]
    [InlineData(7, 50, 3)]          // 3.5 向零取整
    [InlineData(100, -10, -10)]
    [InlineData(7, -50, -3)]        // −3.5 向零取整
    [InlineData(0, 50, 0)]
    public void PercentRecoveryIsMaxTimesAmountOverHundredTruncated(int max, int amount, int expected)
    {
        Assert.Equal(expected, new RecoveryEffect(true, amount, 0).TotalFor(max));
    }

    [Fact]
    public void PercentRecoveryDoesNotOverflowOnLargeMaximum()
    {
        Assert.Equal(1_000_000_000, new RecoveryEffect(true, 100, 0).TotalFor(1_000_000_000));
        Assert.Equal(2_000_000_000, new RecoveryEffect(true, 100, 0).TotalFor(2_000_000_000));
    }

    // ------------------------------------------------------------------ 线性发放

    [Fact]
    public void ChannelDeliversLinearlyOverTheDuration()
    {
        var ch = new RecoveryChannel();
        ch.Start(100, 1000);
        Assert.Equal(25, ch.Advance(250));
        Assert.Equal(25, ch.Advance(250));
        Assert.Equal(1, ch.Active);
        Assert.Equal(50, ch.Advance(500));
        Assert.Equal(0, ch.Active);
        Assert.Equal(0L, ch.Pending);
    }

    [Fact]
    public void ChannelTruncatesAndTotalsAlwaysAddUp()
    {
        var ch = new RecoveryChannel();
        ch.Start(10, 3000);
        int a = ch.Advance(1000);      // 3.33 → 3
        int b = ch.Advance(1000);      // 累计 6.66 → 6,这一步 3
        int c = ch.Advance(1000);      // 发完 10,这一步 4
        Assert.Equal(3, a);
        Assert.Equal(3, b);
        Assert.Equal(4, c);
        Assert.Equal(0, ch.Active);
    }

    [Fact]
    public void ChannelWithZeroDurationDeliversEverythingAtOnce()
    {
        var ch = new RecoveryChannel();
        ch.Start(77, 0);
        Assert.Equal(77L, ch.Pending);
        Assert.Equal(77, ch.Advance(0));
        Assert.Equal(0, ch.Active);
    }

    [Fact]
    public void ChannelDeliversNothingBeforeOneWholeMillisecond()
    {
        var ch = new RecoveryChannel();
        ch.Start(100, 1000);
        Assert.Equal(0, ch.Advance(0.5));
        Assert.Equal(0, ch.Advance(0.0));
        Assert.Equal(10, ch.Advance(99.5));          // 已过 100.0 毫秒 → 10
    }

    [Fact]
    public void ChannelSumsOverlappingEffectsAndEachRunsOnItsOwnClock()
    {
        var ch = new RecoveryChannel();
        ch.Start(100, 1000);
        int first = ch.Advance(500);                  // 50
        ch.Start(40, 400);                            // 第二批晚半秒开始,自己从 0 数起
        int second = ch.Advance(400);                 // 第一批再发 40(已过 900 → 90),第二批发完 40
        Assert.Equal(50, first);
        Assert.Equal(40 + 40, second);
        Assert.Equal(1, ch.Active);
        Assert.Equal(10L, ch.Pending);
        Assert.Equal(10, ch.Advance(100));
        Assert.Equal(0, ch.Active);
    }

    [Fact]
    public void ChannelDeliversNegativeTotalsAsDrain()
    {
        var ch = new RecoveryChannel();
        ch.Start(-30, 600);
        Assert.Equal(-15, ch.Advance(300));
        Assert.Equal(-15, ch.Advance(300));
        Assert.Equal(0, ch.Active);
    }

    [Fact]
    public void ChannelIgnoresZeroTotalsAndNegativeTime()
    {
        var ch = new RecoveryChannel();
        ch.Start(0, 1000);
        Assert.Equal(0, ch.Active);
        ch.Start(10, 1000);
        Assert.Equal(0, ch.Advance(-50));
        Assert.Equal(1, ch.Active);
    }

    [Fact]
    public void ChannelCopyIsDeepAndClearDropsEverything()
    {
        var ch = new RecoveryChannel();
        ch.Start(100, 1000);
        ch.Advance(300);
        var copy = new RecoveryChannel();
        copy.Start(5, 5);
        copy.CopyFrom(ch);
        Assert.Equal(1, copy.Active);
        Assert.Equal(70L, copy.Pending);
        Assert.Equal(20, copy.Advance(200));
        Assert.Equal(70L, ch.Pending);               // 原来那份不受影响
        ch.Clear();
        Assert.Equal(0, ch.Active);
        Assert.Throws<ArgumentNullException>(() => copy.CopyFrom(null));
    }

    // ------------------------------------------------------------------ 冷却表

    [Fact]
    public void CooldownStartsAdvancesAndEnds()
    {
        var cd = new ItemCooldowns();
        ItemInfo p = Potion(10, Flat(10, 0), coolMs: 4000);
        Assert.False(cd.IsCooling(p));
        cd.Start(p);
        Assert.True(cd.IsCooling(p));
        Assert.Equal(4000, cd.RemainingMs(p));
        Assert.Equal(1.0, cd.Fraction(p));
        cd.Advance(1000);
        Assert.Equal(3000, cd.RemainingMs(p));
        Assert.Equal(0.75, cd.Fraction(p), 6);
        cd.Advance(3000);
        Assert.False(cd.IsCooling(p));
        Assert.Equal(0, cd.Active);
        Assert.Equal(0.0, cd.Fraction(p));
    }

    [Fact]
    public void CooldownWithoutCoolTimeNeverStarts()
    {
        var cd = new ItemCooldowns();
        cd.Start(Potion(10, Flat(10, 0), coolMs: 0));
        Assert.Equal(0, cd.Active);
        cd.Start(null);
        Assert.Equal(0, cd.Active);
    }

    [Fact]
    public void ItemsInTheSameGroupShareACooldownAndOthersDoNot()
    {
        var cd = new ItemCooldowns();
        ItemInfo a = Potion(10, Flat(10, 0), coolMs: 3000, group: 2);
        ItemInfo b = Potion(11, Flat(20, 0), coolMs: 5000, group: 2);
        ItemInfo c = Potion(12, null, Flat(10, 0), coolMs: 3000, group: 3);
        ItemInfo d = Potion(13, Flat(10, 0), coolMs: 3000);       // 没写组:按自己的编号
        cd.Start(a);
        Assert.True(cd.IsCooling(b));
        Assert.False(cd.IsCooling(c));
        Assert.False(cd.IsCooling(d));
        cd.Start(d);
        Assert.True(cd.IsCooling(d));
        Assert.Equal(2, cd.Active);
        cd.Clear();
        Assert.Equal(0, cd.Active);
    }

    [Fact]
    public void GroupKeysNeverCollideWithItemCodes()
    {
        ItemInfo grouped = Potion(10, Flat(1, 0), coolMs: 1000, group: 0);
        ItemInfo codeZeroLike = Potion(0, Flat(1, 0), coolMs: 1000);
        Assert.NotEqual(ItemCooldowns.KeyOf(grouped), ItemCooldowns.KeyOf(codeZeroLike));
        Assert.Equal(0L, ItemCooldowns.KeyOf(null));
    }

    // ------------------------------------------------------------------ 使用检查

    [Fact]
    public void CheckNamesTheFirstReasonItCannotBeUsed()
    {
        Assert.Equal(UseResult.NotConsumable, ConsumableRules.Check(null, 0, 1));
        Assert.Equal(UseResult.NotConsumable, ConsumableRules.Check(new ItemInfo(1, "equipment/a.equ", ItemKind.Equipment), 0, 1));
        var material = new ItemInfo(2, "stackable/m.stk", ItemKind.Stackable) { StackableType = StackableTypes.Material, HpRecovery = Flat(5, 0) };
        Assert.Equal(UseResult.NotConsumable, ConsumableRules.Check(material, 0, 1));
        var plain = new ItemInfo(3, "stackable/w.stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste };
        Assert.Equal(UseResult.NoEffect, ConsumableRules.Check(plain, 0, 1));
        Assert.Equal(UseResult.LevelTooLow, ConsumableRules.Check(Potion(4, Flat(5, 0), minLevel: 20), 0, 19));
        Assert.Equal(UseResult.Ok, ConsumableRules.Check(Potion(4, Flat(5, 0), minLevel: 20), 0, 20));
    }

    [Fact]
    public void CheckTreatsAnEmptyJobListAsEveryoneButHonoursAWrittenList()
    {
        ItemInfo open = Potion(5, Flat(5, 0));
        Assert.Equal(UseResult.Ok, ConsumableRules.Check(open, 3, 1));

        ItemInfo mageOnly = Potion(6, Flat(5, 0));
        mageOnly.UsableJobs.Add("[mage]");
        Assert.Equal(UseResult.WrongJob, ConsumableRules.Check(mageOnly, EquipRules.SwordmanJob, 1));
        Assert.Equal(UseResult.Ok, ConsumableRules.Check(mageOnly, EquipRules.JobOf("[mage]"), 1));

        ItemInfo everyone = Potion(7, Flat(5, 0));
        everyone.UsableJobs.Add("[all]");
        Assert.Equal(UseResult.Ok, ConsumableRules.Check(everyone, 2, 1));
    }

    [Fact]
    public void ThrowTypeIsAConsumableTypeLikeWaste()
    {
        var thrown = new ItemInfo(8, "stackable/t.stk", ItemKind.Stackable) { StackableType = StackableTypes.Throw, MpRecovery = Flat(1, 0) };
        Assert.True(ConsumableRules.IsConsumableType(thrown));
        Assert.True(ConsumableRules.HasRecovery(thrown));
        Assert.True(QuickSlots.Eligible(thrown));
    }

    // ------------------------------------------------------------------ 快捷栏

    [Fact]
    public void QuickSlotsFillTheFirstEmptySlotAndDoNotDuplicate()
    {
        var q = new QuickSlots();
        ItemInfo a = Potion(10, Flat(1, 0));
        ItemInfo b = Potion(11, null, Flat(1, 0));
        Assert.Equal(0, q.Place(a));
        Assert.Equal(1, q.Place(b));
        Assert.Equal(0, q.Place(a));                 // 已经在 0 号格
        Assert.Equal(2, q.Filled);
        Assert.Equal(1, q.IndexOf(11));
        Assert.Equal(-1, q.IndexOf(99));
        q.Clear(0);
        Assert.Equal(0, q.Place(Potion(12, Flat(1, 0))));   // 空出来的第一格先补
        Assert.Equal(2, q.Filled);
    }

    [Fact]
    public void QuickSlotsHoldSixAndRefuseTheSeventh()
    {
        var q = new QuickSlots();
        for (int i = 0; i < QuickSlots.Count; i++)
        {
            Assert.Equal(i, q.Place(Potion(100 + i, Flat(1, 0))));
        }
        Assert.Equal(-1, q.Place(Potion(200, Flat(1, 0))));
        Assert.Equal(QuickSlots.Count, q.Filled);
        Assert.Equal(6, QuickSlots.Count);
    }

    [Fact]
    public void QuickSlotsRefuseIneligibleItems()
    {
        var q = new QuickSlots();
        Assert.Equal(-1, q.Place(null));
        Assert.Equal(-1, q.Place(new ItemInfo(1, "equipment/a.equ", ItemKind.Equipment)));
        Assert.Equal(-1, q.Place(new ItemInfo(2, "stackable/m.stk", ItemKind.Stackable) { StackableType = StackableTypes.Material, HpRecovery = Flat(1, 0) }));
        Assert.Equal(-1, q.Place(new ItemInfo(3, "stackable/w.stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste }));
        Assert.Equal(0, q.Filled);
    }

    [Fact]
    public void QuickSlotsSetClearAndBounds()
    {
        var q = new QuickSlots();
        ItemInfo a = Potion(10, Flat(1, 0));
        ItemInfo b = Potion(11, Flat(1, 0));
        Assert.True(q.Set(4, a));
        Assert.Same(a, q[4]);
        Assert.False(q.Set(2, a));                   // 同一种物品不能占两格
        Assert.True(q.Set(4, b));                    // 同一格可以改放别的
        Assert.False(q.Set(-1, a));
        Assert.False(q.Set(6, a));
        Assert.Null(q[-1]);
        Assert.Null(q[6]);
        q.Clear(9);                                  // 越界不抛
        Assert.True(q.Set(4, null));
        Assert.Equal(0, q.Filled);
        q.Set(1, a);
        q.Set(2, b);
        q.ClearAll();
        Assert.Equal(0, q.Filled);
    }
}
