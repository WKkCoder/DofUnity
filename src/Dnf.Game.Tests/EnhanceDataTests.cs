using Dnf.Data;

namespace Dnf.Game.Tests;

/// <summary>
/// 物品脚本里强化、附魔、套装周边标签的读法:套装成员与加成块、附魔卡属性与部位、强化券、强化等级区间。
/// 数据全是合成的(物品编号 900 起,数值随手写的小整数)。读法的整库对拍在 enhance-check 里,这里钉住规则本身。
/// </summary>
public class EnhanceDataTests
{
    private static EnhanceData Read(TokenStreamBuilder b)
    {
        (ScriptToken[] tokens, PvfStringTable strings) = b.Build();
        return EnhanceData.Read(tokens, strings);
    }

    private static ItemInfo Parse(TokenStreamBuilder b, ItemKind kind = ItemKind.Equipment, int code = 940)
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return ItemScript.Parse(b.Bytes(), strings, kind, code, "equipment/test/e" + code + ".equ");
    }

    // ------------------------------------------------------------------ 没有周边标签

    [Fact]
    public void AnItemWithoutAnyPeripheryTagHasNoData()
    {
        var b = new TokenStreamBuilder().Tag("[hp max]").Int(5).Tag("[grade]").Int(2);
        Assert.Null(Read(b));
        Assert.Null(Parse(b).Enhance);
    }

    [Fact]
    public void AnEmptyScriptHasNoData()
    {
        Assert.Null(Read(new TokenStreamBuilder()));
    }

    [Fact]
    public void ATagWithNothingUsableAfterItDoesNotCreateData()
    {
        // 关键标签在,但读不出东西:不造空壳
        Assert.Null(Read(new TokenStreamBuilder().Tag("[set item master]")));
        Assert.Null(Read(new TokenStreamBuilder().Tag("[monster card id]").Str("x")));
        Assert.Null(Read(new TokenStreamBuilder().Tag("[equipment reinforcement ticket]").Int(5)));   // 只有一个数
        Assert.Null(Read(new TokenStreamBuilder().Tag("[limit upgradable level]").Str("unknown name").Int(1).Int(2)));
    }

    // ------------------------------------------------------------------ 套装

    [Fact]
    public void ASetMasterReadsItsMembersAndBothBonusKinds()
    {
        var b = new TokenStreamBuilder()
            .Tag("[set item]").Ints(940, 941, 942)
            .Tag("[set ability]").Tag("[hp max]").Int(100).Tag("[/set ability]")
            .Tag("[piece set ability]").Int(3).Tag("[physical attack]").Int(20).Tag("[/piece set ability]");
        EnhanceData d = Read(b);
        Assert.Equal(new[] { 940, 941, 942 }, d.SetMembers);
        Assert.Equal(100.0, d.SetFullBonus[EquipStat.HpMax]);
        Assert.Single(d.SetPieceBonuses);
        Assert.Equal(3, d.SetPieceBonuses[0].Pieces);
        Assert.Equal(20.0, d.SetPieceBonuses[0].Stats[EquipStat.PhysicalAttack]);
        Assert.True(d.IsSetMaster);
        Assert.Equal(0, d.SetMaster);
    }

    [Fact]
    public void AMemberOnlyRecordsWhichSetItBelongsTo()
    {
        EnhanceData d = Read(new TokenStreamBuilder().Tag("[set item master]").Int(940));
        Assert.Equal(940, d.SetMaster);
        Assert.False(d.IsSetMaster);
        Assert.Empty(d.SetMembers);
        Assert.Null(d.SetFullBonus);
    }

    [Fact]
    public void ASetBlockAloneMakesAMaster()
    {
        Assert.True(Read(new TokenStreamBuilder().Tag("[set ability]").Tag("[hp max]").Int(1).Tag("[/set ability]")).IsSetMaster);
        Assert.True(Read(new TokenStreamBuilder().Tag("[piece set ability]").Int(3).Tag("[hp max]").Int(1).Tag("[/piece set ability]")).IsSetMaster);
        Assert.True(Read(new TokenStreamBuilder().Tag("[set item]").Int(1)).IsSetMaster);
    }

    [Fact]
    public void AnEmptyFullBonusBlockIsAnEmptyBonusNotNothing()
    {
        EnhanceData d = Read(new TokenStreamBuilder().Tag("[set ability]").Tag("[/set ability]"));
        Assert.NotNull(d.SetFullBonus);
        Assert.True(d.SetFullBonus.IsEmpty);
    }

    [Fact]
    public void PieceBonusesComeOutSortedByPieceCount()
    {
        var b = new TokenStreamBuilder()
            .Tag("[piece set ability]").Int(8).Tag("[hp max]").Int(8).Tag("[/piece set ability]")
            .Tag("[piece set ability]").Int(3).Tag("[hp max]").Int(3).Tag("[/piece set ability]")
            .Tag("[piece set ability]").Int(5).Tag("[hp max]").Int(5).Tag("[/piece set ability]");
        EnhanceData d = Read(b);
        Assert.Equal(new[] { 3, 5, 8 }, d.SetPieceBonuses.Select(p => p.Pieces));
        Assert.Equal(new[] { 3.0, 5.0, 8.0 }, d.SetPieceBonuses.Select(p => p.Stats[EquipStat.HpMax]));
    }

    [Fact]
    public void ARepeatedPieceCountKeepsTheLaterBlock()
    {
        var b = new TokenStreamBuilder()
            .Tag("[piece set ability]").Int(3).Tag("[hp max]").Int(1).Tag("[/piece set ability]")
            .Tag("[piece set ability]").Int(3).Tag("[hp max]").Int(2).Tag("[/piece set ability]");
        EnhanceData d = Read(b);
        Assert.Single(d.SetPieceBonuses);
        Assert.Equal(2.0, d.SetPieceBonuses[0].Stats[EquipStat.HpMax]);
    }

    [Fact]
    public void ALaterSetItemListReplacesTheEarlierOne()
    {
        EnhanceData d = Read(new TokenStreamBuilder().Tag("[set item]").Ints(1, 2, 3).Tag("[set item]").Ints(7, 8));
        Assert.Equal(new[] { 7, 8 }, d.SetMembers);
    }

    [Fact]
    public void AMemberListTakesEveryIntegerUntilTheNextTag()
    {
        // 无符号整数也算;夹在中间的字符串跳过
        var b = new TokenStreamBuilder().Tag("[set item]").Int(11).Str("x").UInt(12).Int(13).Tag("[hp max]").Int(99);
        Assert.Equal(new[] { 11, 12, 13 }, Read(b).SetMembers);
    }

    [Fact]
    public void OnlyDirectlyWrittenStatsInsideASetBlockCount()
    {
        // 块里再嵌的条件块(触发效果一类)不算
        var b = new TokenStreamBuilder()
            .Tag("[set ability]").Tag("[hp max]").Int(100)
            .Tag("[trigger]").Tag("[mp max]").Int(50).Tag("[/trigger]")
            .Tag("[physical attack]").Int(7).Tag("[/set ability]");
        EquipStats full = Read(b).SetFullBonus;
        Assert.Equal(100.0, full[EquipStat.HpMax]);
        Assert.Equal(7.0, full[EquipStat.PhysicalAttack]);
        Assert.False(full.Has(EquipStat.MpMax));
    }

    [Fact]
    public void AnUnclosedBlockGivesNothing()
    {
        Assert.Null(Read(new TokenStreamBuilder().Tag("[set ability]").Tag("[hp max]").Int(100)));
        Assert.Null(Read(new TokenStreamBuilder().Tag("[piece set ability]").Int(3).Tag("[hp max]").Int(1)));
        Assert.Null(Read(new TokenStreamBuilder().Tag("[enchant]").Tag("[hp max]").Int(1)));
    }

    [Fact]
    public void ATagInsideAnotherBlockIsIgnored()
    {
        // 深度不为 0 的周边标签不算(规则同基础属性)
        var b = new TokenStreamBuilder().Tag("[wrapper]").Tag("[set item]").Ints(1, 2).Tag("[monster card id]").Int(9).Tag("[/wrapper]");
        Assert.Null(Read(b));
    }

    [Fact]
    public void TagNamesAreCaseInsensitive()
    {
        EnhanceData d = Read(new TokenStreamBuilder().Tag("[SET ITEM MASTER]").Int(5).Tag("[Monster Card ID]").Int(6));
        Assert.Equal((5, 6), (d.SetMaster, d.MonsterCardId));
    }

    // ------------------------------------------------------------------ 附魔

    [Fact]
    public void AnEnchantCardReadsItsStatsAndTheNamedParts()
    {
        var b = new TokenStreamBuilder()
            .Tag("[enchant]").Tag("[physical attack]").Int(7).Tag("[hp max]").Int(30).Tag("[/enchant]")
            .Tag("[string data]").Str("[weapon]").Str("[ring]").Str("sprite/card.img");
        EnhanceData d = Read(b);
        Assert.Equal(7.0, d.EnchantStats[EquipStat.PhysicalAttack]);
        Assert.Equal(30.0, d.EnchantStats[EquipStat.HpMax]);
        Assert.Equal(new[] { EquipmentParts.Weapon, EquipmentParts.Ring }, d.EnchantParts);
    }

    [Fact]
    public void PartTagsAreDeduplicatedInOrderAndNonPartWordsAreSkipped()
    {
        var b = new TokenStreamBuilder()
            .Tag("[enchant]").Tag("[hp max]").Int(1).Tag("[/enchant]")
            .Tag("[string data]").Str("[ring]").Str("[foil]").Str("[ring]").Str("[coat]").Str("[unlimited challenge]").Str("[weapon]");
        Assert.Equal(new[] { EquipmentParts.Ring, EquipmentParts.Coat, EquipmentParts.Weapon }, Read(b).EnchantParts);
    }

    [Fact]
    public void PartsAreOnlyReadWhenThereIsAnEnchantBlock()
    {
        // 宝珠(只有卡片编号)与别的物品的 [string data] 不当成附魔部位
        var bead = new TokenStreamBuilder().Tag("[monster card id]").Int(930).Tag("[string data]").Str("[weapon]");
        EnhanceData d = Read(bead);
        Assert.Equal(930, d.MonsterCardId);
        Assert.Empty(d.EnchantParts);
        Assert.Null(Read(new TokenStreamBuilder().Tag("[string data]").Str("[weapon]")));
    }

    [Fact]
    public void AStringDataTagInsideAnotherBlockIsNotUsed()
    {
        var b = new TokenStreamBuilder()
            .Tag("[enchant]").Tag("[hp max]").Int(1).Tag("[/enchant]")
            .Tag("[wrapper]").Tag("[string data]").Str("[weapon]").Tag("[/wrapper]");
        Assert.Empty(Read(b).EnchantParts);
    }

    [Fact]
    public void AnEnchantBlockWithoutStatsIsAnEmptyEnchantNotNull()
    {
        EnhanceData d = Read(new TokenStreamBuilder().Tag("[enchant]").Tag("[/enchant]"));
        Assert.NotNull(d.EnchantStats);
        Assert.True(d.EnchantStats.IsEmpty);
    }

    [Fact]
    public void ALaterEnchantBlockReplacesAnEarlierOne()
    {
        var b = new TokenStreamBuilder()
            .Tag("[enchant]").Tag("[hp max]").Int(1).Tag("[/enchant]")
            .Tag("[enchant]").Tag("[mp max]").Int(2).Tag("[/enchant]");
        EquipStats stats = Read(b).EnchantStats;
        Assert.False(stats.Has(EquipStat.HpMax));
        Assert.Equal(2.0, stats[EquipStat.MpMax]);
    }

    [Fact]
    public void TheMonsterCardIdIsTheFirstInteger()
    {
        Assert.Equal(931, Read(new TokenStreamBuilder().Tag("[monster card id]").Int(931).Int(5)).MonsterCardId);
        Assert.Equal(932, Read(new TokenStreamBuilder().Tag("[monster card id]").Int(931).Tag("[monster card id]").Int(932)).MonsterCardId);
    }

    // ------------------------------------------------------------------ 强化券

    [Fact]
    public void TicketsReadTheLevelFirstThenTheRate()
    {
        EnhanceData d = Read(new TokenStreamBuilder().Tag("[equipment reinforcement ticket]").Int(7).Int(50));
        Assert.Equal((7, 50), (d.ReinforceTicket.Value.Level, d.ReinforceTicket.Value.RatePercent));
        Assert.Null(d.AmplifyTicket);

        EnhanceData a = Read(new TokenStreamBuilder().Tag("[equipment amplify reinforcement ticket]").Int(9).Int(30));
        Assert.Equal((9, 30), (a.AmplifyTicket.Value.Level, a.AmplifyTicket.Value.RatePercent));
        Assert.Null(a.ReinforceTicket);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(31, 100, 31, 100)]    // 上边界合法
    [InlineData(32, 100, 0, 100)]     // 等级超出 0 到 31 读作 0
    [InlineData(-1, 50, 0, 50)]
    [InlineData(5, 101, 5, 0)]        // 成功率超出 0 到 100 读作 0
    [InlineData(5, -1, 5, 0)]
    public void TicketValuesOutsideTheServersRangesBecomeZero(int level, int rate, int expectedLevel, int expectedRate)
    {
        ReinforceTicket t = Read(new TokenStreamBuilder().Tag("[equipment reinforcement ticket]").Int(level).Int(rate)).ReinforceTicket.Value;
        Assert.Equal((expectedLevel, expectedRate), (t.Level, t.RatePercent));
        ReinforceTicket a = Read(new TokenStreamBuilder().Tag("[equipment amplify reinforcement ticket]").Int(level).Int(rate)).AmplifyTicket.Value;
        Assert.Equal((expectedLevel, expectedRate), (a.Level, a.RatePercent));
    }

    [Fact]
    public void ALaterTicketLineWithTooFewNumbersDoesNotEraseAnEarlierOne()
    {
        var b = new TokenStreamBuilder().Tag("[equipment reinforcement ticket]").Int(7).Int(50).Tag("[equipment reinforcement ticket]").Int(3);
        Assert.Equal(7, Read(b).ReinforceTicket.Value.Level);
    }

    // ------------------------------------------------------------------ 强化等级区间

    [Fact]
    public void LimitRangesReadNamedTriples()
    {
        var b = new TokenStreamBuilder().Tag("[limit upgradable level]").Str("normal upgrade").Int(10).Int(20).Str("amplify upgrade").Int(30).Int(40);
        EnhanceData d = Read(b);
        Assert.Equal(new[] { (1, 10, 20), (2, 30, 40) }, d.UpgradeRanges.Select(r => (r.Type, r.Min, r.Max)));
        Assert.Equal(30, d.RangeOf(2).Value.Min);
        Assert.Null(d.RangeOf(3));
        Assert.Null(d.RangeOf(0));
    }

    [Fact]
    public void MinusOneMeansOneAndSeventy()
    {
        EnhanceData d = Read(new TokenStreamBuilder().Tag("[limit upgradable level]").Str("normal upgrade").Int(-1).Int(-1).Str("amplify upgrade").Int(-1).Int(50));
        Assert.Equal((1, 70), (d.RangeOf(1).Value.Min, d.RangeOf(1).Value.Max));
        Assert.Equal((1, 50), (d.RangeOf(2).Value.Min, d.RangeOf(2).Value.Max));
    }

    [Fact]
    public void AllFourUpgradeKindsAreNamed()
    {
        Assert.Equal(1, EnhanceData.UpgradeTypeOf("normal upgrade"));
        Assert.Equal(2, EnhanceData.UpgradeTypeOf("Amplify Upgrade"));
        Assert.Equal(3, EnhanceData.UpgradeTypeOf("SEPARATE UPGRADE"));
        Assert.Equal(4, EnhanceData.UpgradeTypeOf("3rd chronicle upgrade"));
        Assert.Equal(0, EnhanceData.UpgradeTypeOf("anything else"));
        Assert.Equal(0, EnhanceData.UpgradeTypeOf(null));
    }

    [Fact]
    public void AnUnknownNameSkipsItsNumbersButTheNextKnownNameStillCounts()
    {
        var b = new TokenStreamBuilder().Tag("[limit upgradable level]").Str("mystery").Int(5).Int(6).Str("normal upgrade").Int(7).Int(8);
        Assert.Equal(new[] { (1, 7, 8) }, Read(b).UpgradeRanges.Select(r => (r.Type, r.Min, r.Max)));
    }

    [Fact]
    public void ThePreferredRangeOfAKindIsTheFirstOneWritten()
    {
        // 服务端:同一种强化方式已经有条目就不覆盖
        var same = new TokenStreamBuilder().Tag("[limit upgradable level]").Str("normal upgrade").Int(1).Int(2).Str("normal upgrade").Int(3).Int(4);
        Assert.Equal(new[] { (1, 1, 2) }, Read(same).UpgradeRanges.Select(r => (r.Type, r.Min, r.Max)));
        var lines = new TokenStreamBuilder().Tag("[limit upgradable level]").Str("normal upgrade").Int(1).Int(2).Tag("[limit upgradable level]").Str("normal upgrade").Int(3).Int(4);
        Assert.Equal(new[] { (1, 1, 2) }, Read(lines).UpgradeRanges.Select(r => (r.Type, r.Min, r.Max)));
    }

    [Fact]
    public void ARangeIsInclusiveAtBothEnds()
    {
        var range = new UpgradeLevelRange(1, 10, 20);
        Assert.False(range.Contains(9));
        Assert.True(range.Contains(10));
        Assert.True(range.Contains(20));
        Assert.False(range.Contains(21));
    }

    // ------------------------------------------------------------------ 构造与共享

    [Fact]
    public void ADefaultInstanceIsEmptyButNeverNull()
    {
        var d = new EnhanceData();
        Assert.Empty(d.SetMembers);
        Assert.Empty(d.SetPieceBonuses);
        Assert.Empty(d.UpgradeRanges);
        Assert.Empty(d.EnchantParts);
        Assert.False(d.IsSetMaster);
        Assert.Null(d.ReinforceTicket);
        Assert.Null(d.AmplifyTicket);
        Assert.Null(d.EnchantStats);
        Assert.Null(d.SetFullBonus);
        Assert.Equal((0, 0), (d.SetMaster, d.MonsterCardId));
    }

    [Fact]
    public void TheConstructorSortsPieceBonusesAndTreatsNullStatsAsEmpty()
    {
        var d = new EnhanceData(setPieceBonuses: new[] { new SetPieceBonus(5, null), new SetPieceBonus(3, Stats(EquipStat.HpMax, 1)) });
        Assert.Equal(new[] { 3, 5 }, d.SetPieceBonuses.Select(p => p.Pieces));
        Assert.True(d.SetPieceBonuses[1].Stats.IsEmpty);
    }

    [Fact]
    public void ParsedDataRidesAlongOnEveryInstanceCopy()
    {
        ItemInfo item = Parse(new TokenStreamBuilder().Tag("[set item master]").Int(940));
        Assert.NotNull(item.Enhance);
        ItemInfo copy = item.WithInstance(new EquipInstance(3));
        Assert.Same(item.Enhance, copy.Enhance);
    }

    [Fact]
    public void StackableItemsReadThePeripheryTagsToo()
    {
        ItemInfo card = Parse(new TokenStreamBuilder().Tag("[enchant]").Tag("[hp max]").Int(5).Tag("[/enchant]").Tag("[string data]").Str("[ring]"), ItemKind.Stackable, 931);
        Assert.Equal(5.0, card.Enhance.EnchantStats[EquipStat.HpMax]);
        Assert.Equal(new[] { EquipmentParts.Ring }, card.Enhance.EnchantParts);
        ItemInfo ticket = Parse(new TokenStreamBuilder().Tag("[equipment reinforcement ticket]").Int(4).Int(80), ItemKind.Stackable, 921);
        Assert.Equal(4, ticket.Enhance.ReinforceTicket.Value.Level);
    }

    private static EquipStats Stats(EquipStat stat, double value) => EnhanceFixtures.Stats(stat, value);
}
