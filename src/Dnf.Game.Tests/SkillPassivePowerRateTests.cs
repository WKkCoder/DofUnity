using System.Globalization;
using System.IO;
using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 被动技能对主动技能威力列的倍率(M81,证据 E-181):合成技能;期望来自 Golden/passive_power_golden.txt 与手算。
/// </summary>
public sealed class SkillPassivePowerRateTests
{
    private const int Active = 901;
    private const int Passive = 902;
    private const float Scale = SkillPassivePowerRate.ProtectedRateScale;

    private static SkillDef ActiveDef(int rateCol = 0, int fixedCol = 1) => Skill(Active, "[active]", rateCol, fixedCol);
    private static SkillDef PassiveDef(int static0, params int[] executableStates) => Skill(Passive, "[passive]", staticIndex: static0, executableStates: executableStates);

    private static SkillDef Skill(int id, string type, int rateCol = 0, int fixedCol = 1, int staticIndex = 0, int[] executableStates = null)
    {
        var lines = new List<ScriptLine>
        {
            Str("[type]", type),
            Ints("[static data]", staticIndex, 0, 0),
            Ints("[level info]", 100, 50, 200, 80),
        };
        if (executableStates != null && executableStates.Length > 0)
        {
            lines.Add(Ints("[executable states]", executableStates));
        }
        return new SkillDef(id, "skill/test/p" + id + ".skl", lines.ToArray());
    }

    private static ScriptLine Ints(string tag, params int[] values)
    {
        var list = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            list[i] = new ScriptValue(ScriptValueKind.Int, values[i], null);
        }
        return new ScriptLine(tag, list);
    }

    private static ScriptLine Str(string tag, string text) =>
        new ScriptLine(tag, new[] { new ScriptValue(ScriptValueKind.String, 0, text) });

    [Fact]
    public void MergeIncludesRegisteredLinks()
    {
        var recipe = new List<SkillPassivePowerRate.Link> { new SkillPassivePowerRate.Link(Passive, Active, 0) };
        var reg = new List<SkillPassivePowerRate.Link> { new SkillPassivePowerRate.Link(903, Active, 1) };
        IReadOnlyList<SkillPassivePowerRate.Link> merged = SkillPassivePowerRate.MergePassiveLinks(recipe, reg);
        Assert.Equal(2, merged.Count);
    }

    private static float Accumulate(int passiveLevel, int static0, int nativeState, IReadOnlyList<SkillPassivePowerRate.Link> links, params int[] executableStates)
    {
        SkillDef passive = PassiveDef(static0, executableStates);
        return SkillPassivePowerRate.Accumulate(1f, Active, nativeState, id => id == Passive ? passiveLevel : 0, id => id == Passive ? passive : ActiveDef(), links);
    }

    [Fact]
    public void LearnedPassiveAddsStaticTimesProtectedScale()
    {
        var links = new List<SkillPassivePowerRate.Link> { new SkillPassivePowerRate.Link(Passive, Active, 0) };
        float rate = Accumulate(1, 50, nativeState: 0, links, 0);
        Assert.Equal(1f + 50 * Scale, rate, 5);
    }

    [Fact]
    public void UnlearnedPassiveOrWrongTypeDoesNotChangeRate()
    {
        var links = new List<SkillPassivePowerRate.Link> { new SkillPassivePowerRate.Link(Passive, Active, 0) };
        Assert.Equal(1f, Accumulate(0, 50, 0, links, 0));
        SkillDef activeAsPassive = Skill(Passive, "[active]");
        float rate = SkillPassivePowerRate.Accumulate(1f, Active, 0, _ => 1, _ => activeAsPassive, links);
        Assert.Equal(1f, rate);
    }

    [Fact]
    public void NativeStateMustMatchPassiveExecutableStates()
    {
        var links = new List<SkillPassivePowerRate.Link> { new SkillPassivePowerRate.Link(Passive, Active, 0) };
        Assert.Equal(1f, Accumulate(1, 50, nativeState: 7, links, 0));
        Assert.Equal(1f + 50 * Scale, Accumulate(1, 50, nativeState: 0, links, 0));
    }

    [Fact]
    public void EmptyExecutableStatesNeverAdds()
    {
        SkillDef passive = PassiveDef(50);
        var links = new List<SkillPassivePowerRate.Link> { new SkillPassivePowerRate.Link(Passive, Active, 0) };
        float rate = SkillPassivePowerRate.Accumulate(1f, Active, 0, _ => 1, _ => passive, links);
        Assert.Equal(1f, rate);
    }

    [Fact]
    public void TerminatesChainStopsLaterLinks()
    {
        const int PassiveB = 903;
        SkillDef passiveA = PassiveDef(10, 0);
        SkillDef passiveB = Skill(PassiveB, "[passive]", staticIndex: 100, executableStates: new[] { 0 });
        var links = new List<SkillPassivePowerRate.Link>
        {
            new SkillPassivePowerRate.Link(Passive, Active, 0, terminatesChain: true),
            new SkillPassivePowerRate.Link(PassiveB, Active, 0),
        };
        float rate = SkillPassivePowerRate.Accumulate(1f, Active, 0, id => 1, id => id == Passive ? passiveA : id == PassiveB ? passiveB : ActiveDef(), links);
        Assert.Equal(1f + 10 * Scale, rate, 5);
    }

    [Theory]
    [InlineData(false, 30)]
    [InlineData(true, 10)]
    public void ChainTerminateOnFirstOrMiddleEntry(bool terminateOnFirst, int expectStaticSum)
    {
        const int PassiveB = 903;
        SkillDef passiveA = PassiveDef(10, 0);
        SkillDef passiveB = Skill(PassiveB, "[passive]", staticIndex: 20, executableStates: new[] { 0 });
        var links = new List<SkillPassivePowerRate.Link>
        {
            new SkillPassivePowerRate.Link(Passive, Active, 0, terminatesChain: terminateOnFirst),
            new SkillPassivePowerRate.Link(PassiveB, Active, 0, terminatesChain: !terminateOnFirst),
        };
        float rate = SkillPassivePowerRate.Accumulate(1f, Active, 0, id => 1, id => id == Passive ? passiveA : id == PassiveB ? passiveB : ActiveDef(), links);
        Assert.Equal(1f + expectStaticSum * Scale, rate, 5);
    }

    [Fact]
    public void LinksFromRecipesCopiesPassiveStaticIndexAndTerminateFlag()
    {
        var recipeLinks = new[] { new SkillPassivePowerLink(Passive, 2, terminatesChain: true) };
        IReadOnlyList<SkillPassivePowerRate.Link> links = SkillPassivePowerRate.LinksFromRecipes(recipeLinks, Active);
        Assert.Single(links);
        Assert.Equal(Passive, links[0].PassiveSkillId);
        Assert.Equal(2, links[0].StaticIndex);
        Assert.True(links[0].TerminatesChain);
    }

    [Fact]
    public void GoldenMultiplierMatchesOracleFile()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden", "passive_power_golden.txt");
        path = Path.GetFullPath(path);
        float multiplier = 0f;
        float caseDelta = 0f;
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.StartsWith("MULTIPLIER", StringComparison.Ordinal))
            {
                multiplier = float.Parse(line.Split('\t')[1], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("CASE", StringComparison.Ordinal))
            {
                caseDelta = float.Parse(line.Split('\t')[4], CultureInfo.InvariantCulture);
            }
        }
        Assert.Equal(Scale, multiplier, 5);
        Assert.Equal(50 * multiplier, caseDelta, 3);
        var links = new List<SkillPassivePowerRate.Link> { new SkillPassivePowerRate.Link(Passive, Active, 0) };
        float rate = Accumulate(1, 50, nativeState: 0, links, 0);
        Assert.Equal(1f + caseDelta, rate, 5);
    }
}
