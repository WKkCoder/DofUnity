using Dnf.Data;
using static Dnf.Game.Tests.ExpertJobTestData;

namespace Dnf.Game.Tests;

/// <summary>
/// 副职业脚本(.exj)与等级上限表(expertjob.etc)的读取。数据全是合成的(见 <see cref="ExpertJobTestData"/>):
/// 经验阈值 100、250、450、700;分解结果表六行;费用表、耐久表、升档表等,期望值手算。
/// 读取规则的依据:服务端 ImportExpertJobScript 0x0896b0e4、ImportExpertJobEtcScript 0x0896ae21(静态读码,地址见各处注释)。
/// </summary>
public class ExpertJobScriptTests
{
    private static ExpertJobScript Parse(params ScriptLine[] lines) => ExpertJobScript.Parse(ExpertJobKind.Disjointer, lines);

    // ------------------------------------------------------------------ 经验表与等级

    [Fact]
    public void TheLevelTableIsReadInThresholdExtraNameTriples()
    {
        ExpertJobScript s = Disjointer();
        Assert.Equal(4, s.MaxLevel);
        Assert.Equal(new[] { 100, 250, 450, 700 }, s.Levels.Select(l => l.Threshold).ToArray());
        Assert.Equal("lv100", s.Levels[0].Name);
        Assert.Equal(0, s.Levels[0].Extra);

        ExpertJobScript custom = Parse(L("[expertness exp]", 5, 7, "x", 9, 8, "y"));
        Assert.Equal(2, custom.MaxLevel);
        Assert.Equal(7, custom.Levels[0].Extra);
        Assert.Equal(8, custom.Levels[1].Extra);
        Assert.Equal("y", custom.Levels[1].Name);
    }

    [Fact]
    public void ALevelGroupWithOnlyOneIntegerIsALoadError()
    {
        ExpertJobScript s = Parse(L("[expertness exp]", 5), L("[base const]", 9));
        Assert.False(s.IsValid);
        Assert.Single(s.Errors);
        Assert.Contains("[expertness exp]", s.Errors[0]);
        Assert.Equal(0, s.BaseConst);   // 装载失败后不再往下读
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 1)]
    [InlineData(100, 2)]      // 恰在阈值上就算过了这一级
    [InlineData(101, 2)]
    [InlineData(249, 2)]
    [InlineData(250, 3)]
    [InlineData(449, 3)]
    [InlineData(450, 4)]
    [InlineData(699, 4)]
    [InlineData(700, 4)]      // 阈值个数加一封顶在表长
    [InlineData(5000, 4)]
    public void TheScriptLevelIsOnePlusTheNumberOfThresholdsAtOrBelowTheExpCappedAtTheTableLength(int exp, int level)
    {
        Assert.Equal(level, Disjointer().GetLevel(exp));
    }

    [Fact]
    public void ExpIsComparedAsAnUnsignedNumber()
    {
        // 服务端 GetLevel 0x08693ba0 用无符号比较:负经验当成很大的数,等级是表长。
        ExpertJobScript s = Disjointer();
        Assert.Equal(4, s.GetLevel(-1));
        Assert.Equal(4, s.GetLevel(int.MinValue));
    }

    [Fact]
    public void AnEmptyTableHasLevelZeroAndNoBoundary()
    {
        ExpertJobScript s = Parse();
        Assert.Equal(0, s.MaxLevel);
        Assert.Equal(0, s.GetLevel(0));
        Assert.Equal(0, s.GetLevel(12345));
        Assert.False(s.IsBoundaryExp(0));
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(250, true)]
    [InlineData(450, true)]
    [InlineData(700, true)]
    [InlineData(0, false)]
    [InlineData(99, false)]
    [InlineData(101, false)]
    [InlineData(699, false)]
    [InlineData(701, false)]
    [InlineData(-1, false)]
    public void ABoundaryExpIsExactlyAThreshold(int exp, bool boundary)
    {
        Assert.Equal(boundary, Disjointer().IsBoundaryExp(exp));
    }

    // ------------------------------------------------------------------ 技能、任务、放弃费用

    [Fact]
    public void SkillsAndClearQuestsAreReadInOrder()
    {
        ExpertJobScript s = Disjointer();
        Assert.Equal(2, s.Skills.Count);
        Assert.Equal(901, s.Skills[0].SkillId);
        Assert.Equal(1, s.Skills[0].Level);
        Assert.Equal(902, s.Skills[1].SkillId);
        Assert.Equal(new[] { 901, 902 }, s.ClearQuests.ToArray());
    }

    [Fact]
    public void AnOddSkillListIsALoadError()
    {
        ExpertJobScript s = Parse(L("[skill]", 901, 1, 902), L("[base const]", 9));
        Assert.False(s.IsValid);
        Assert.Contains("[skill]", s.Errors[0]);
        Assert.Equal(0, s.BaseConst);
    }

    [Fact]
    public void ARepeatedTagStartsOverInsteadOfAppending()
    {
        // 服务端对同一个标签第二次出现先 clear() 再读。
        ExpertJobScript s = Parse(L("[skill]", 901, 1), L("[skill]", 903, 2), L("[giveup cost]", 1, 2, 3), L("[giveup cost]", 7), L("[clear quests]", 5), L("[clear quests]", 6, 7));
        Assert.Single(s.Skills);
        Assert.Equal(903, s.Skills[0].SkillId);
        Assert.Equal(new[] { 7 }, s.GiveupCosts.ToArray());
        Assert.Equal(new[] { 6, 7 }, s.ClearQuests.ToArray());
    }

    [Fact]
    public void ARepeatedUpgradeCostOrRepairTableStartsOverToo()
    {
        // 升档费用表要求序号从 1 起依次排:不先清空的话,第二行会因为"序号对不上"装载失败。
        ExpertJobScript s = Parse(
            L("[upgrade cost]", 1, 5, 2, 6),
            L("[upgrade cost]", 1, 9),
            L("[endurance repair cost]", 100, 10, 200, 20),
            L("[endurance repair cost]", 300, 30));
        Assert.True(s.IsValid);
        Assert.Equal(new[] { 9 }, s.UpgradeCosts.ToArray());
        Assert.True(s.TryGetEnduranceRepair(1, out EnduranceRepairCost only));
        Assert.Equal(300, only.Cost);
        Assert.Equal(30, only.MaxEndurance);
        Assert.False(s.TryGetEnduranceRepair(2, out _));
    }

    [Fact]
    public void NineGiveUpCostsLoadButTenIsALoadError()
    {
        int[] nine = Enumerable.Range(1, 9).ToArray();
        ExpertJobScript ok = Parse(L("[giveup cost]", nine.Cast<object>().ToArray()), L("[base const]", 4));
        Assert.True(ok.IsValid);
        Assert.Equal(9, ok.GiveupCosts.Count);
        Assert.Equal(4, ok.BaseConst);

        ExpertJobScript bad = Parse(L("[giveup cost]", Enumerable.Range(1, 10).Cast<object>().ToArray()), L("[base const]", 4));
        Assert.False(bad.IsValid);
        Assert.Contains("[giveup cost]", bad.Errors[0]);
        Assert.Equal(0, bad.BaseConst);   // 读到第 10 项就装载失败,后面的标签不再读
    }

    // ------------------------------------------------------------------ 分解结果表

    [Fact]
    public void TheDisjointResultTableHasOneRowPerKeyAndTheFirstRowWins()
    {
        ExpertJobScript s = Disjointer();
        Assert.Equal(6, s.Rows.Count);

        Assert.True(s.TryGetRow(new DisjointKey(0, 1, false), out DisjointRow row));
        Assert.Equal(950, row.Item);              // 重复的 (0,1,已鉴定) 行 948 不生效
        Assert.Equal(1.5f, row.Factor);
        Assert.Equal(10, row.BigWinPercent);
        Assert.Equal(new[] { 951, 952 }, row.Additional.Select(r => r.Item).ToArray());
        Assert.Equal(new[] { 960 }, row.BigWin.Select(r => r.Item).ToArray());

        Assert.True(s.TryGetRow(new DisjointKey(0, 3, false), out DisjointRow noBig));
        Assert.Empty(noBig.BigWin);               // 组号 0 表示没有这一组
        Assert.Equal(0, noBig.BigWinPercent);
        Assert.Equal(new[] { 954, 955 }, noBig.Additional.Select(r => r.Item).ToArray());

        Assert.True(s.TryGetRow(new DisjointKey(0, 1, true), out DisjointRow unidentified));
        Assert.Equal(949, unidentified.Item);
        Assert.Empty(unidentified.Additional);

        Assert.False(s.TryGetRow(new DisjointKey(0, 5, false), out _));
        Assert.False(s.TryGetRow(new DisjointKey(2, 1, false), out _));
        Assert.False(s.TryGetRow(new DisjointKey(0, 2, true), out _));
    }

    [Fact]
    public void ARowKeyIsTruncatedToBytes()
    {
        // 机器档与稀有度在服务端各占一个字节,所以 256 与 0 是同一个键。
        ExpertJobScript s = Parse(L("[disjoint result]", 256, 257, 0, 950, 1.0f, 0, 0, 0));
        Assert.True(s.TryGetRow(new DisjointKey(0, 1, false), out DisjointRow row));
        Assert.Equal(950, row.Item);
        Assert.Equal(new DisjointKey(0, 1, false), new DisjointKey(256, 257, false));
        Assert.NotEqual(new DisjointKey(0, 1, false), new DisjointKey(0, 1, true));
        Assert.NotEqual(new DisjointKey(1, 0, false), new DisjointKey(0, 1, false));
    }

    [Fact]
    public void TheUnidentifiedColumnIsTruncatedToAByteToo()
    {
        // 键的第三个字节是"未鉴定":256 的低字节是 0(已鉴定),257 的低字节是 1(未鉴定);所以两行不是同一个键。
        ExpertJobScript s = Parse(L("[disjoint result]", 0, 1, 256, 950, 1.0f, 0, 0, 0, 0, 1, 257, 951, 1.0f, 0, 0, 0));
        Assert.Equal(2, s.Rows.Count);
        Assert.True(s.TryGetRow(new DisjointKey(0, 1, false), out DisjointRow identified));
        Assert.Equal(950, identified.Item);
        Assert.True(s.TryGetRow(new DisjointKey(0, 1, true), out DisjointRow unidentified));
        Assert.Equal(951, unidentified.Item);
    }

    [Fact]
    public void ARowThatNamesAMissingAdditionalGroupIsALoadError()
    {
        ExpertJobScript s = Parse(L("[disjoint result]", 0, 1, 0, 950, 1.5f, 7, 0, 0), L("[base const]", 5));
        Assert.False(s.IsValid);
        Assert.Contains("[additional result]", s.Errors[0]);
        Assert.Contains("7", s.Errors[0]);
        Assert.Equal(0, s.BaseConst);
    }

    [Fact]
    public void ARowThatNamesAMissingBigWinGroupIsALoadError()
    {
        ExpertJobScript s = Parse(L("[additional result]", Rate(1, 1, 50, 951, 10000, 2.0f)), L("[disjoint result]", 0, 1, 0, 950, 1.5f, 1, 4, 0));
        Assert.False(s.IsValid);
        Assert.Contains("[big win result]", s.Errors[0]);
    }

    [Fact]
    public void ARowReferencesOnlyGroupsDefinedBeforeIt()
    {
        // 组表出现在行表之后就等于没有:服务端读行时就要查组。
        ExpertJobScript late = Parse(L("[disjoint result]", 0, 1, 0, 950, 1.5f, 1, 0, 0), L("[additional result]", Rate(1, 1, 50, 951, 10000, 2.0f)));
        Assert.False(late.IsValid);

        ExpertJobScript early = Parse(L("[additional result]", Rate(1, 1, 50, 951, 10000, 2.0f)), L("[disjoint result]", 0, 1, 0, 950, 1.5f, 1, 0, 0));
        Assert.True(early.IsValid);
    }

    [Fact]
    public void ARepeatedResultTableStartsOver()
    {
        ExpertJobScript s = Parse(
            L("[additional result]", Rate(1, 1, 50, 951, 10000, 2.0f)),
            L("[additional result]", Rate(2, 1, 50, 953, 10000, 2.0f)),
            L("[disjoint result]", 0, 1, 0, 950, 1.5f, 2, 0, 0),
            L("[disjoint result]", 0, 2, 0, 950, 1.5f, 0, 0, 0));
        Assert.True(s.IsValid);
        Assert.Single(s.Rows);                                 // 第二份行表替换了第一份
        Assert.True(s.TryGetRow(new DisjointKey(0, 2, false), out _));

        // 第二份组表清掉了组 1:引用它的行装载失败。
        ExpertJobScript gone = Parse(
            L("[additional result]", Rate(1, 1, 50, 951, 10000, 2.0f)),
            L("[additional result]", Rate(2, 1, 50, 953, 10000, 2.0f)),
            L("[disjoint result]", 0, 1, 0, 950, 1.5f, 1, 0, 0));
        Assert.False(gone.IsValid);
    }

    [Fact]
    public void AGroupLineKeepsEveryEntryInWritingOrderWithItsGradeRangeWeightAndDivisor()
    {
        ExpertJobScript s = Disjointer();
        Assert.True(s.TryGetRow(new DisjointKey(0, 3, false), out DisjointRow row));
        Assert.Equal(2, row.Additional.Count);
        Assert.Equal(954, row.Additional[0].Item);
        Assert.Equal(10, row.Additional[0].MinGrade);
        Assert.Equal(20, row.Additional[0].MaxGrade);
        Assert.Equal(5000, row.Additional[0].Weight);
        Assert.Equal(4.0f, row.Additional[0].Divisor);
        Assert.Equal(955, row.Additional[1].Item);
        Assert.Equal(30, row.Additional[1].MinGrade);
        Assert.Equal(40, row.Additional[1].MaxGrade);
        Assert.Equal(8.0f, row.Additional[1].Divisor);
    }

    [Fact]
    public void AGroupEntryThatStopsEarlyIsALoadError()
    {
        ExpertJobScript s = Parse(L("[additional result]", 1, 1, 50, 951, 10000), L("[base const]", 5));
        Assert.False(s.IsValid);
        Assert.Contains("[additional result]", s.Errors[0]);
        Assert.Equal(0, s.BaseConst);
    }

    // ------------------------------------------------------------------ 机器:耐久、升档、便携

    [Fact]
    public void TheMachineTablesAndConstantsAreRead()
    {
        ExpertJobScript s = Disjointer();
        Assert.Equal(100, s.BaseConst);
        Assert.Equal(7, s.AdditionalConst);
        Assert.Equal(1, s.EnduranceReduceMin);
        Assert.Equal(3, s.EnduranceReduceMax);
        Assert.Equal(5, s.EnduranceInitial);
        Assert.Equal(2, s.GainExpMin);
        Assert.Equal(4, s.GainExpMax);
        Assert.Equal(5000, s.LimitDisjointGold);
        Assert.True(s.HasSelfService);
        Assert.Equal(10, s.SelfService.Chance);
        Assert.Equal(970, s.SelfService.Item);
        Assert.Equal(2, s.SelfService.Count);
        Assert.Equal(new[] { 1000, 2000, 3000 }, s.GiveupCosts.ToArray());
        Assert.Equal(3, s.MachineMaxGrade);
    }

    [Fact]
    public void AMissingSelfServiceLineLeavesTheFlagOff()
    {
        ExpertJobScript s = Parse(L("[base const]", 5));
        Assert.False(s.HasSelfService);
        Assert.Equal(0, s.SelfService.Item);
    }

    [Fact]
    public void TheEnduranceRepairTableIsKeyedFromOne()
    {
        ExpertJobScript s = Disjointer();
        Assert.True(s.TryGetEnduranceRepair(1, out EnduranceRepairCost first));
        Assert.Equal(10000, first.Cost);
        Assert.Equal(300, first.MaxEndurance);
        Assert.True(s.TryGetEnduranceRepair(3, out EnduranceRepairCost last));
        Assert.Equal(20000, last.Cost);
        Assert.Equal(400, last.MaxEndurance);
        Assert.False(s.TryGetEnduranceRepair(0, out _));
        Assert.False(s.TryGetEnduranceRepair(4, out _));
        Assert.False(s.TryGetEnduranceRepair(-1, out _));
    }

    [Fact]
    public void AnOddRepairTableIsALoadError()
    {
        ExpertJobScript s = Parse(L("[endurance repair cost]", 100, 10, 200), L("[base const]", 5));
        Assert.False(s.IsValid);
        Assert.Contains("[endurance repair cost]", s.Errors[0]);
    }

    [Fact]
    public void UpgradeCostsAreIndexedFromOneAndMustBeConsecutive()
    {
        ExpertJobScript s = Disjointer();
        Assert.Equal(new[] { 0, 30000, 50000 }, s.UpgradeCosts.ToArray());
        Assert.Equal(0, s.GetUpgradeCost(0));
        Assert.Equal(30000, s.GetUpgradeCost(1));
        Assert.Equal(50000, s.GetUpgradeCost(2));
        Assert.Equal(0, s.GetUpgradeCost(3));
        Assert.Equal(0, s.GetUpgradeCost(-1));

        foreach (ScriptLine bad in new[]
        {
            L("[upgrade cost]", 2, 5),              // 不是从 1 起
            L("[upgrade cost]", 0, 5),
            L("[upgrade cost]", 1, 5, 3, 9),        // 跳号
            L("[upgrade cost]", 1, 5, 1, 9),        // 重复
        })
        {
            ExpertJobScript s2 = Parse(bad, L("[base const]", 5));
            Assert.False(s2.IsValid);
            Assert.Contains("[upgrade cost]", s2.Errors[0]);
            Assert.Equal(0, s2.BaseConst);
        }
    }

    [Fact]
    public void TheUpgradeCostAtIndexZeroIsTheFirstListedEntry()
    {
        // 表里第一项(序号 1)在下标 0;升档只会查下标 1 起,但按下标取的函数本身对 0 也照常取值。
        ExpertJobScript s = Parse(L("[upgrade cost]", 1, 700, 2, 800));
        Assert.Equal(700, s.GetUpgradeCost(0));
        Assert.Equal(800, s.GetUpgradeCost(1));
        Assert.Equal(0, s.GetUpgradeCost(2));
    }

    [Fact]
    public void PortableDisjointInfoMapsAnIndexToTheHighestDisjointableLevel()
    {
        ExpertJobScript s = Disjointer();
        Assert.Equal(30, s.GetPortableLevel(0));
        Assert.Equal(60, s.GetPortableLevel(1));
        Assert.Equal(-1, s.GetPortableLevel(2));
        Assert.Equal(-1, s.GetPortableLevel(-1));
    }

    [Fact]
    public void ADuplicatePortableIndexIsALoadErrorAndTheFirstLevelStays()
    {
        ExpertJobScript s = Parse(L("[portable disjoint info]", 0, 30, 0, 40), L("[base const]", 5));
        Assert.False(s.IsValid);
        Assert.Contains("[portable disjoint info]", s.Errors[0]);
        Assert.Equal(30, s.GetPortableLevel(0));
        Assert.Equal(0, s.BaseConst);
    }

    [Fact]
    public void PortableLevelsAreSixteenBitUnsigned()
    {
        ExpertJobScript s = Parse(L("[portable disjoint info]", 0, 70000, 1, -1));
        Assert.Equal(70000 - 65536, s.GetPortableLevel(0));
        Assert.Equal(65535, s.GetPortableLevel(1));
    }

    [Fact]
    public void AShortSingleValueTagIsALoadError()
    {
        foreach (string tag in new[] { "[base const]", "[additional const]", "[endurance initial value]", "[limit disjoint gold]" })
        {
            ExpertJobScript s = Parse(L(tag), L("[giveup cost]", 5));
            Assert.False(s.IsValid);
            Assert.Contains(tag, s.Errors[0]);
            Assert.Empty(s.GiveupCosts);
        }
        foreach (string tag in new[] { "[endurance reduce]", "[gain exp]" })
        {
            ExpertJobScript s = Parse(L(tag, 1), L("[giveup cost]", 5));
            Assert.False(s.IsValid);
            Assert.Contains(tag, s.Errors[0]);
        }
        ExpertJobScript self = Parse(L("[disjoint self service]", 10, 970), L("[giveup cost]", 5));
        Assert.False(self.IsValid);
        Assert.False(self.HasSelfService);
    }

    // ------------------------------------------------------------------ 其余标签

    [Fact]
    public void TagsOfOtherLinesAreRegisteredNotParsedAndClosersAreIgnored()
    {
        ExpertJobScript s = Disjointer();
        Assert.True(s.IsValid);
        Assert.Contains("[compound rate]", s.RegisteredTags);
        Assert.DoesNotContain("[/compound rate]", s.RegisteredTags);
        Assert.DoesNotContain("[base const]", s.RegisteredTags);   // 读懂的标签不算"登记"
        Assert.Single(s.RegisteredTags);
    }

    [Fact]
    public void TheKindIsRecordedAndNullInputGivesAnEmptyScript()
    {
        Assert.Equal(ExpertJobKind.Enchanter, ExpertJobScript.Parse(ExpertJobKind.Enchanter, DisjointerLines()).Kind);
        ExpertJobScript empty = ExpertJobScript.Parse(ExpertJobKind.Alchemist, null);
        Assert.Equal(ExpertJobKind.Alchemist, empty.Kind);
        Assert.Equal(0, empty.MaxLevel);
        Assert.True(empty.IsValid);
    }

    // ------------------------------------------------------------------ expertjob.etc

    [Fact]
    public void TheEtcReadsLevelLimitsAndBigWinRates()
    {
        ExpertJobEtc etc = Etc();
        Assert.True(etc.IsValid);
        Assert.Equal(4, etc.LevelLimitCount);
        Assert.True(etc.TryGetLevelLimit(1, out int one));
        Assert.Equal(10, one);
        Assert.True(etc.TryGetLevelLimit(4, out int four));
        Assert.Equal(30, four);
        Assert.False(etc.TryGetLevelLimit(0, out _));
        Assert.False(etc.TryGetLevelLimit(5, out _));
        Assert.Equal(0, etc.BigWinRate(1));
        Assert.Equal(5, etc.BigWinRate(2));
        Assert.Equal(10, etc.BigWinRate(3));
        Assert.Equal(0, etc.BigWinRate(4));    // 没有这一项就不加
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(20, true)]
    [InlineData(30, true)]
    [InlineData(9, false)]
    [InlineData(11, false)]
    [InlineData(0, false)]
    [InlineData(31, false)]
    public void ALevelIsALimitLevelWhenSomeEntryHasExactlyThatValue(int charLevel, bool limit)
    {
        Assert.Equal(limit, Etc().IsLimitLevel(charLevel));
    }

    [Fact]
    public void EtcKeysAreBytesAndLevelLimitsAreSixteenBitSigned()
    {
        ExpertJobEtc etc = ExpertJobEtc.Parse(new[] { L("[expertjob level limit]", 257, 70000), L("[disjointer big win rate]", 1, 300) });
        Assert.True(etc.TryGetLevelLimit(1, out int limit));
        Assert.Equal(70000 - 65536, limit);
        Assert.True(etc.TryGetLevelLimit(257, out int same));
        Assert.Equal(limit, same);
        Assert.Equal(300 - 256, etc.BigWinRate(1));
    }

    [Fact]
    public void ADuplicateEtcKeyOrAHalfPairIsALoadError()
    {
        ExpertJobEtc dup = ExpertJobEtc.Parse(new[] { L("[expertjob level limit]", 1, 10, 1, 20), L("[disjointer big win rate]", 2, 9) });
        Assert.False(dup.IsValid);
        Assert.Contains("[expertjob level limit]", dup.Errors[0]);
        Assert.True(dup.TryGetLevelLimit(1, out int first));
        Assert.Equal(10, first);
        Assert.Equal(0, dup.BigWinRate(2));   // 出错后不再往下读

        ExpertJobEtc half = ExpertJobEtc.Parse(new[] { L("[expertjob level limit]", 1, 10, 2) });
        Assert.False(half.IsValid);

        ExpertJobEtc dupRate = ExpertJobEtc.Parse(new[] { L("[disjointer big win rate]", 1, 5, 1, 6) });
        Assert.False(dupRate.IsValid);
        Assert.Contains("[disjointer big win rate]", dupRate.Errors[0]);
    }

    [Fact]
    public void AnEmptyOrMissingEtcHasNoLimitsAndNoErrors()
    {
        Assert.Equal(0, ExpertJobEtc.Empty.LevelLimitCount);
        Assert.True(ExpertJobEtc.Empty.IsValid);
        Assert.False(ExpertJobEtc.Empty.IsLimitLevel(0));
        ExpertJobEtc fromNull = ExpertJobEtc.Parse(null);
        Assert.Equal(0, fromNull.LevelLimitCount);
        Assert.False(fromNull.TryGetLevelLimit(1, out _));
    }

    // ------------------------------------------------------------------ 种类

    [Theory]
    [InlineData("[enchanter]", ExpertJobKind.Enchanter)]
    [InlineData("[alchemist]", ExpertJobKind.Alchemist)]
    [InlineData("[disjointer]", ExpertJobKind.Disjointer)]
    [InlineData("[doll_controller]", ExpertJobKind.DollController)]
    [InlineData("[DISJOINTER]", ExpertJobKind.Disjointer)]
    [InlineData("disjointer", ExpertJobKind.None)]       // 类型标签必须带括号
    [InlineData("(disjointer)", ExpertJobKind.None)]     // 括号必须是方括号:首尾各一个别的字符不能当括号剥掉
    [InlineData("xdisjointerx", ExpertJobKind.None)]
    [InlineData("[disjointer)", ExpertJobKind.None)]
    [InlineData("(disjointer]", ExpertJobKind.None)]
    [InlineData("[]", ExpertJobKind.None)]
    [InlineData("[smith]", ExpertJobKind.None)]
    [InlineData(null, ExpertJobKind.None)]
    public void TheExpertTypeTagNamesAKind(string tag, ExpertJobKind kind)
    {
        Assert.Equal(kind, ExpertJobKinds.FromTypeTag(tag));
    }

    [Theory]
    [InlineData("enchanter", 1)]
    [InlineData("alchemist", 2)]
    [InlineData("disjointer", 3)]
    [InlineData("doll_controller", 4)]
    [InlineData("all", 5)]
    [InlineData("ALL", 5)]
    [InlineData("smith", -1)]
    [InlineData("", -1)]
    [InlineData(null, -1)]
    public void TheExpertJobOnlyNamesMapToUsableTypes(string name, int type)
    {
        Assert.Equal(type, ExpertJobKinds.UsableTypeOf(name));
    }

    [Fact]
    public void OnlyTheDisjointerIsExecutedHereAndOnlyFourKindsAreJobs()
    {
        Assert.True(ExpertJobKinds.IsExecutedHere(ExpertJobKind.Disjointer));
        Assert.False(ExpertJobKinds.IsExecutedHere(ExpertJobKind.Alchemist));
        Assert.False(ExpertJobKinds.IsExecutedHere(ExpertJobKind.Enchanter));
        Assert.False(ExpertJobKinds.IsExecutedHere(ExpertJobKind.DollController));
        Assert.False(ExpertJobKinds.IsExecutedHere(ExpertJobKind.None));
        Assert.False(ExpertJobKinds.IsJob(ExpertJobKind.None));
        Assert.True(ExpertJobKinds.IsJob(ExpertJobKind.Enchanter));
        Assert.True(ExpertJobKinds.IsJob(ExpertJobKind.DollController));
        Assert.False(ExpertJobKinds.IsJob((ExpertJobKind)5));
        Assert.True(ExpertJobKinds.IsKnown(0));
        Assert.True(ExpertJobKinds.IsKnown(4));
        Assert.False(ExpertJobKinds.IsKnown(5));
        Assert.False(ExpertJobKinds.IsKnown(-1));
    }
}
