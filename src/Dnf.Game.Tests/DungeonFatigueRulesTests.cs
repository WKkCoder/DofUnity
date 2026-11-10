using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 副本的疲劳规则(r15-growth,证据见 <see cref="DungeonFatigueRules"/> 文件头:服务端 dungeon_start、canFatigueUp、选择副本处理里的门槛与副本脚本标签的导入)。
/// 标签与数值全是合成的,只检验规则的形状,与原版数据无关。
/// </summary>
public class DungeonFatigueRulesTests
{
    private static ScriptLine Flag(string tag) => new ScriptLine(tag, new ScriptValue[0]);

    private static ScriptLine Line(string tag, params int[] values)
    {
        var v = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            v[i] = new ScriptValue(ScriptValueKind.Int, values[i], null);
        }
        return new ScriptLine(tag, v);
    }

    private static DungeonFatigueRules Parse(params ScriptLine[] lines) => DungeonFatigueRules.Parse(lines);

    [Fact]
    public void WithoutAnyTagTheDungeonCostsOnePointToEnterAndOnePerRoomChangeAndTheGateChecks()
    {
        foreach (DungeonFatigueRules rules in new[] { DungeonFatigueRules.Default, Parse(Line("[name]", 1)), DungeonFatigueRules.Parse(null) })
        {
            Assert.Equal((1, 1, false, false), (rules.StartCost, rules.MoveCost, rules.ChargeExempt, rules.GateExempt));
        }
    }

    [Fact]
    public void TheNoFatigueFlagIsJustItsPresenceAndSkipsBothTheChargeAndTheGate()
    {
        DungeonFatigueRules rules = Parse(Flag("[no fatigue]"));
        Assert.Equal((0, 0, true, true), (rules.StartCost, rules.MoveCost, rules.ChargeExempt, rules.GateExempt));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public void ABloodDungeonOfAnyNonZeroKindIsNotChargedButTheGateStillChecks(int kind)
    {
        DungeonFatigueRules rules = Parse(Line("[blood dungeon]", kind));
        Assert.Equal((0, 0, true, false), (rules.StartCost, rules.MoveCost, rules.ChargeExempt, rules.GateExempt));
    }

    [Fact]
    public void AZeroOrValuelessBloodTagDoesNotExempt()
    {
        Assert.Equal((1, 1), (Parse(Line("[blood dungeon]", 0)).StartCost, Parse(Line("[blood dungeon]", 0)).MoveCost));
        Assert.Equal(1, Parse(Flag("[blood dungeon]")).StartCost);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    public void ARevengeDungeonIsNotChargedOnlyWhenTheFirstValueIsExactlyOne(int first, int expectedCost)
    {
        DungeonFatigueRules rules = Parse(Line("[village attack revenge dungeon]", first, 0));
        Assert.Equal(expectedCost, rules.StartCost);
        Assert.Equal(expectedCost, rules.MoveCost);
        Assert.False(rules.GateExempt);
    }

    [Fact]
    public void ATournamentDungeonChargesItsRoundFatigueToEnterAndStillOnePerRoomChange()
    {
        DungeonFatigueRules rules = Parse(Line("[tournament dungeon]", 1), Line("[tournament round fatigue]", 2));
        Assert.Equal((2, 1, false, false), (rules.StartCost, rules.MoveCost, rules.ChargeExempt, rules.GateExempt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void AMissingOrNonPositiveRoundFatigueMeansOnePoint(int round)
    {
        Assert.Equal(1, Parse(Line("[tournament dungeon]", 1), Line("[tournament round fatigue]", round)).StartCost);
        Assert.Equal(1, Parse(Line("[tournament dungeon]", 1)).StartCost);
    }

    [Fact]
    public void OnlyAnExactOneMakesATournamentDungeonSoTheRoundFatigueIsIgnoredOtherwise()
    {
        Assert.Equal(1, Parse(Line("[tournament dungeon]", 2), Line("[tournament round fatigue]", 5)).StartCost);
        Assert.Equal(1, Parse(Line("[tournament dungeon]", 0), Line("[tournament round fatigue]", 5)).StartCost);
        Assert.Equal(1, Parse(Line("[tournament round fatigue]", 5)).StartCost);                 // 没有锦标赛标志:不看它
    }

    [Fact]
    public void ANoFatigueFlagBeatsTheTournamentCost()
    {
        Assert.Equal(0, Parse(Flag("[no fatigue]"), Line("[tournament dungeon]", 1), Line("[tournament round fatigue]", 3)).StartCost);
    }

    [Fact]
    public void TheFatigueTagsTheServerOnlyParsesDoNotChangeAnything()
    {
        DungeonFatigueRules rules = Parse(Line("[fatigue]", 4), Line("[fatigue result]", 9), Line("[phase fatigue]", 3));
        Assert.Equal((1, 1, false, false), (rules.StartCost, rules.MoveCost, rules.ChargeExempt, rules.GateExempt));
    }

    [Fact]
    public void TagOrderDoesNotMatterAndTheTagsAreFoundAnywhereInTheScript()
    {
        DungeonFatigueRules a = Parse(Line("[name]", 1), Flag("[no fatigue]"), Line("[size]", 2, 2));
        DungeonFatigueRules b = Parse(Flag("[no fatigue]"));
        Assert.Equal((a.StartCost, a.MoveCost, a.GateExempt), (b.StartCost, b.MoveCost, b.GateExempt));
    }
}
