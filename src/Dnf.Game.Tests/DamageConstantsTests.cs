using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 伤害常量的读取与召唤物倍率函数(证据 E-049)。标记流是手工拼的合成数据,数值不是原版数据。
/// </summary>
public sealed class DamageConstantsTests
{
    private const string FormulaTag = "[damage fomula constant]";

    [Fact]
    public void FormulaConstantsAreReadInFileOrder()
    {
        // 前面夹着无关的标签与数值(会被丢弃),26 项里混用整数与浮点(整数转成 float)。
        TokenStreamBuilder b = new TokenStreamBuilder().Tag("[unrelated]").Int(5).Float(0.5f).Tag(FormulaTag);
        for (int i = 1; i <= 26; i++)
        {
            if (i % 2 == 0)
            {
                b.Int(i);
            }
            else
            {
                b.Float(i + 0.5f);
            }
        }
        b.Tag("[/damage fomula constant]");
        ScriptCursor cursor = b.Cursor();

        var k = new DamageConstants();
        DamageConstantsLoadReport report = k.LoadEtcParameter(cursor);

        Assert.True(report.Complete);
        Assert.Equal(0, report.BadValues);
        Assert.Equal(1.5f, k.PhysicalDamageRate);
        Assert.Equal(2f, k.PhysicalDamageAdd);
        Assert.Equal(3.5f, k.PhysicalReduceRate);
        Assert.Equal(4f, k.PhysicalReduceAdd);
        Assert.Equal(5.5f, k.PhysicalRandomBase);
        Assert.Equal(6f, k.PhysicalRandomRate);
        Assert.Equal(7.5f, k.MagicalDamageRate);
        Assert.Equal(8f, k.MagicalDamageAdd);
        Assert.Equal(9.5f, k.MagicalReduceRate);
        Assert.Equal(10f, k.MagicalReduceAdd);
        Assert.Equal(11.5f, k.MagicalRandomBase);
        Assert.Equal(12f, k.MagicalRandomRate);
        Assert.Equal(13.5f, k.CommonElementBase);
        Assert.Equal(14f, k.CommonElementRate);
        Assert.Equal(15.5f, k.CommonElementRateInPvp);
        Assert.Equal(16f, k.CommonElementRateInLadderPvp);
        Assert.Equal(17.5f, k.HpIncreaseRate);
        Assert.Equal(18f, k.HpRegenRate);
        Assert.Equal(19.5f, k.MpIncreaseRate);
        Assert.Equal(20f, k.MpRegenRate);
        Assert.Equal(21.5f, k.ActiveStatusDamageRate);
        Assert.Equal(22f, k.ActiveStatusDefenseRate);
        Assert.Equal(23.5f, k.PhysicalDefenseAdd);
        Assert.Equal(24f, k.MagicalDefenseAdd);
        Assert.Equal(25.5f, k.HpRegenRateAddRate);
        Assert.Equal(26f, k.MpRegenRateAddRate);
        // 读完 26 项就停:结束标签还留在流里。
        Assert.Equal(cursor.Length - 1, cursor.Position);
    }

    [Fact]
    public void MissingFormulaTagIsReportedIncomplete()
    {
        ScriptCursor cursor = new TokenStreamBuilder().Tag("[other]").Float(1f).Tag("[/other]").Cursor();
        DamageConstantsLoadReport report = new DamageConstants().LoadEtcParameter(cursor);
        Assert.False(report.Complete);
    }

    [Fact]
    public void WrongTokenInAValueSlotCountsAsBadAndReadsZero()
    {
        TokenStreamBuilder b = new TokenStreamBuilder().Tag(FormulaTag).Float(1f).Str("oops");
        for (int i = 0; i < 24; i++)
        {
            b.Float(2f);
        }
        var k = new DamageConstants();
        DamageConstantsLoadReport report = k.LoadEtcParameter(b.Cursor());

        Assert.True(report.Complete);
        Assert.Equal(1, report.BadValues);
        Assert.Equal(1f, k.PhysicalDamageRate);
        Assert.Equal(0f, k.PhysicalDamageAdd);
        Assert.Equal(2f, k.PhysicalReduceRate);
        Assert.Equal(2f, k.MpRegenRateAddRate);
    }

    [Fact]
    public void StartsWithTheFixedServerConstants()
    {
        var k = new DamageConstants();
        // @0x0936E4A8 / @0x0936E4AC 的初值(加载函数从不写它们):0.01 与 1.5。
        Assert.Equal(0x3C23D70A, BitConverter.SingleToInt32Bits(k.MaxTotalDefenseRate));
        Assert.Equal(0x3FC00000, BitConverter.SingleToInt32Bits(k.CriticalDamageDefaultRate));
        Assert.Equal(0f, k.PhysicalDamageRate);
        Assert.Empty(k.CreatureDamageVector);
    }

    [Fact]
    public void CreatureCommonLoadsTheThreeDamageSections()
    {
        ScriptCursor cursor = new TokenStreamBuilder()
            .Tag("[attack constant]").Float(0.5f)
            .Tag("[response delay]").Int(3)
            .Tag("[ambient in pvp]").Int(1).Int(2)
            .Tag("[damage]").Ints(2, 3, 5, 7, 11, 13, 17).Tag("[/damage]")
            .Tag("[mana]").Ints(4, 5).Tag("[/mana]")
            .Tag("[module damage percent]").Str("[assault]").Int(40).Str("[pvp]").Int(150).Tag("[/module damage percent]")
            .Tag("[assault creature damage penalty]").Ints(2, 25, 3, 60).Tag("[/assault creature damage penalty]")
            .Cursor();

        var k = new DamageConstants();
        DamageConstantsLoadReport report = k.LoadCreatureCommon(cursor);

        Assert.True(report.Complete);
        Assert.Null(report.UnknownTag);
        Assert.Equal(0, report.BadValues);
        Assert.Equal(new[] { 2, 3, 5, 7, 11, 13, 17 }, k.CreatureDamageVector);
        Assert.Equal((float)(40 / 100.0), k.CreatureModuleRate(9));
        Assert.Equal((float)(150 / 100.0), k.CreatureModuleRate(4));
        Assert.Equal(1f, k.CreatureModuleRate(0));
        Assert.Equal(0.25f, k.CreatureIndexRate(2));
        Assert.Equal(0.6f, k.CreatureIndexRate(3));
        Assert.Equal(1f, k.CreatureIndexRate(4));
    }

    [Fact]
    public void CreatureCommonStopsOnUnknownTag()
    {
        ScriptCursor cursor = new TokenStreamBuilder()
            .Tag("[damage]").Ints(1, 2, 3).Tag("[/damage]")
            .Tag("[no such section]").Int(1)
            .Tag("[module damage percent]").Str("[assault]").Int(40).Tag("[/module damage percent]")
            .Cursor();

        var k = new DamageConstants();
        DamageConstantsLoadReport report = k.LoadCreatureCommon(cursor);

        Assert.False(report.Complete);
        Assert.Equal("[no such section]", report.UnknownTag);
        Assert.Equal(new[] { 1, 2, 3 }, k.CreatureDamageVector);
        // 中止之后的内容读不到。
        Assert.Equal(1f, k.CreatureModuleRate(9));
    }

    [Fact]
    public void CreatureCommonStopsQuietlyOnAStrayValue()
    {
        // 顶层循环里夹着游离的值时,原版按"加载成功"停止,后面的节读不到。
        ScriptCursor cursor = new TokenStreamBuilder()
            .Tag("[damage]").Ints(1, 2, 3).Tag("[/damage]")
            .Int(99)
            .Tag("[module damage percent]").Str("[assault]").Int(40).Tag("[/module damage percent]")
            .Cursor();

        var k = new DamageConstants();
        DamageConstantsLoadReport report = k.LoadCreatureCommon(cursor);

        Assert.True(report.Complete);
        Assert.Null(report.UnknownTag);
        Assert.Equal(1f, k.CreatureModuleRate(9));
    }

    [Fact]
    public void ReloadingReplacesTheDamageSections()
    {
        var k = new DamageConstants();
        k.LoadCreatureCommon(new TokenStreamBuilder()
            .Tag("[damage]").Ints(1, 2, 3, 4, 5, 6, 7).Tag("[/damage]")
            .Tag("[assault creature damage penalty]").Ints(2, 25).Tag("[/assault creature damage penalty]")
            .Cursor());
        k.LoadCreatureCommon(new TokenStreamBuilder()
            .Tag("[damage]").Ints(9).Tag("[/damage]")
            .Tag("[assault creature damage penalty]").Ints(5, 50).Tag("[/assault creature damage penalty]")
            .Cursor());

        Assert.Equal(new[] { 9 }, k.CreatureDamageVector);
        Assert.Equal(1f, k.CreatureIndexRate(2));
        Assert.Equal(0.5f, k.CreatureIndexRate(5));
    }

    [Fact]
    public void ModuleTagsMapToTheFirstMatchingModule()
    {
        Assert.Equal(0, DamageConstants.ModuleTypeFromTag("[dungeon]"));
        Assert.Equal(4, DamageConstants.ModuleTypeFromTag("[pvp]"));
        Assert.Equal(7, DamageConstants.ModuleTypeFromTag("[warroom]"));
        Assert.Equal(9, DamageConstants.ModuleTypeFromTag("[assault]"));
        Assert.Equal(10, DamageConstants.ModuleTypeFromTag("[death tower]"));
        Assert.Equal(15, DamageConstants.ModuleTypeFromTag("[boss tower]"));
        Assert.Equal(16, DamageConstants.ModuleTypeFromTag(string.Empty));
        Assert.Equal(-1, DamageConstants.ModuleTypeFromTag("[nothing]"));
    }

    [Fact]
    public void RegisteringTheSameModuleTwiceMultiplies()
    {
        var k = new DamageConstants();
        k.AddCreatureModuleRate(9, 0.5f);
        k.AddCreatureModuleRate(9, 0.5f);
        Assert.Equal(0.25f, k.CreatureModuleRate(9));
        k.AddCreatureModuleRate(4, 2f);
        Assert.Equal(2f, k.CreatureModuleRate(4));
    }

    [Fact]
    public void CreatureLevelRateFollowsTheServerFormula()
    {
        var k = new DamageConstants();
        k.SetCreatureDamageVector(2, 3, 5, 7, 11, 13, 17);

        // L = 4 + 1;((2 + 6 + 3L) * ((5 + 7L) / 11 + 1) + (13 + 7L) / 17) / 100,全程双精度,最后转 float。
        float expected = (float)((48.0 / 17 + (40.0 / 11 + 1.0) * 23.0) / 100.0);
        Assert.Equal(expected, k.CreatureLevelRate(4, 1, 6));
    }

    [Fact]
    public void CreatureLevelRateCapsTheLevelAtFiftyBeforeAddingTheExtraLevel()
    {
        var k = new DamageConstants();
        k.SetCreatureDamageVector(0, 1, 0, 0, 1, 0, 1);
        // 向量退化成 rate = L / 100。
        Assert.Equal(0.5f, k.CreatureLevelRate(50, 0, 0));
        Assert.Equal(0.5f, k.CreatureLevelRate(90, 0, 0));
        Assert.Equal(0.55f, k.CreatureLevelRate(90, 5, 0));
        Assert.Equal(0.2f, k.CreatureLevelRate(20, 0, 0));
    }

    [Fact]
    public void CreatureLevelRateNeedsSevenEntries()
    {
        var k = new DamageConstants();
        Assert.Equal(1f, k.CreatureLevelRate(10, 0, 0));
        k.SetCreatureDamageVector(1, 2, 3, 4, 5, 6);
        Assert.Equal(1f, k.CreatureLevelRate(10, 0, 0));
        k.SetCreatureDamageVector(0, 1, 0, 0, 1, 0, 1);
        Assert.Equal(0.1f, k.CreatureLevelRate(10, 0, 0));
    }
}
