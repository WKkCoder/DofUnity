using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 被动属性的登记与折算(r15-growth,边界与证据见 <see cref="PassiveStatBonus"/> 文件头:服务端没有被动 → 属性的计算,本机技能数据里没有属性指向的字段,
/// 所以只做机制、登记表默认为空)。技能编号全是合成的 901 起,[level info] 的数也是合成的。
/// </summary>
public class PassiveStatBonusTests
{
    private static ScriptLine Line(string tag, params int[] values)
    {
        var v = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            v[i] = new ScriptValue(ScriptValueKind.Int, values[i], null);
        }
        return new ScriptLine(tag, v);
    }

    /// <summary>合成被动:每级 <paramref name="columns"/> 列,<paramref name="values"/> 按级依次排开(第 1 级在最前)。</summary>
    private static SkillDef Passive(int id, int columns, params int[] values)
    {
        var info = new int[values.Length + 1];
        info[0] = columns;
        values.CopyTo(info, 1);
        return new SkillDef(id, "skill/synthetic/p" + id + ".skl", new[]
        {
            new ScriptLine("[type]", new[] { new ScriptValue(ScriptValueKind.String, 0, "[passive]") }),
            Line("[level info]", info),
        });
    }

    // 901:两列,1 到 3 级 = (10,5) (20,7) (30,9);902:一列,1 到 2 级 = 4、6
    private static readonly SkillDef S901 = Passive(901, 2, 10, 5, 20, 7, 30, 9);
    private static readonly SkillDef S902 = Passive(902, 1, 4, 6);

    private static SkillDef Resolve(int id) => id == 901 ? S901 : id == 902 ? S902 : null;

    private static Func<int, int> Levels(int l901, int l902 = 0) => id => id == 901 ? l901 : id == 902 ? l902 : 0;

    private static StatBlock Compute(PassiveStatRegistry registry, Func<int, int> levels) => PassiveStatBonus.Compute(registry, Resolve, levels);

    private static StatBlock Only(string tag, long tenths)
    {
        Assert.True(default(StatBlock).TrySet(tag, tenths, out StatBlock block));
        return block;
    }

    [Fact]
    public void AnEmptyRegistryAddsNothingWhateverTheLevels()
    {
        Assert.Equal(default, Compute(new PassiveStatRegistry(), Levels(3, 2)));
        Assert.Equal(default, PassiveStatBonus.Compute(null, Resolve, Levels(3)));
    }

    [Fact]
    public void ARuleAddsTheLevelsColumnTimesTheScaleToTheNamedStat()
    {
        var registry = new PassiveStatRegistry();
        Assert.True(registry.Register(new PassiveStatRule(901, "[physical attack]", 1)));      // 第 1 列(0 起),每个单位 10 个十分之一
        Assert.Equal(Only("[physical attack]", 50), Compute(registry, Levels(1)));
        Assert.Equal(Only("[physical attack]", 70), Compute(registry, Levels(2)));
        Assert.Equal(Only("[physical attack]", 90), Compute(registry, Levels(3)));
    }

    [Fact]
    public void AnUnlearnedSkillAddsNothingAndALevelBeyondTheTableAddsNothing()
    {
        var registry = new PassiveStatRegistry();
        registry.Register(new PassiveStatRule(901, "[hp max]", 0));
        Assert.Equal(default, Compute(registry, Levels(0)));
        Assert.Equal(default, Compute(registry, Levels(-2)));
        Assert.Equal(Only("[hp max]", 300), Compute(registry, Levels(3)));
        Assert.Equal(default, Compute(registry, Levels(4)));         // 取值表只有 3 级:再高不是"沿用最后一级"而是 0(客户端取值函数的边界)
    }

    [Fact]
    public void ARuleCanUseAnotherScaleIncludingNegativeValues()
    {
        var registry = new PassiveStatRegistry();
        registry.Register(new PassiveStatRule(902, "[magical defense]", 0, tenthsPerValue: 1));
        Assert.Equal(Only("[magical defense]", 6), Compute(registry, Levels(0, 2)));
        registry.Register(new PassiveStatRule(902, "[magical defense]", 0, tenthsPerValue: -10));   // 同一技能同一项再登记 = 覆盖
        Assert.Equal(Only("[magical defense]", -60), Compute(registry, Levels(0, 2)));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void OneSkillCanFeedSeveralStatsAndSeveralSkillsAddUpInTheSameStat()
    {
        var registry = new PassiveStatRegistry();
        registry.Register(new PassiveStatRule(901, "[hp max]", 0));
        registry.Register(new PassiveStatRule(901, "[mp max]", 1));
        registry.Register(new PassiveStatRule(902, "[hp max]", 0));
        StatBlock block = Compute(registry, Levels(2, 1));
        Assert.Equal(200 + 40, block.HpMax);                          // 901 二级第 0 列 20 + 902 一级 4,各乘 10
        Assert.Equal(70, block.MpMax);
        Assert.Equal((0, 0, 0, 0, 0), (block.PhysicalAttack, block.PhysicalDefense, block.MagicalAttack, block.MagicalDefense, block.HitRecovery));
    }

    [Fact]
    public void ASkillTheCatalogDoesNotKnowAddsNothing()
    {
        var registry = new PassiveStatRegistry();
        registry.Register(new PassiveStatRule(999, "[hp max]", 0));
        Assert.Equal(default, PassiveStatBonus.Compute(registry, Resolve, id => 5));
    }

    [Fact]
    public void ARuleNamingAStatTheBlockDoesNotHaveIsRefusedAndLeavesTheRegistryAlone()
    {
        var registry = new PassiveStatRegistry();
        Assert.False(registry.Register(new PassiveStatRule(901, "[critical hit]", 0)));
        Assert.False(registry.Register(new PassiveStatRule(901, null, 0)));
        Assert.Equal((0, 0), (registry.Count, registry.Version));
    }

    [Fact]
    public void TheStatTagIsMatchedWithoutRegardToCaseWhenReplacing()
    {
        var registry = new PassiveStatRegistry();
        registry.Register(new PassiveStatRule(901, "[HP MAX]", 0));
        registry.Register(new PassiveStatRule(901, "[hp max]", 1));
        Assert.Equal(1, registry.Count);
        Assert.Equal(Only("[hp max]", 50), Compute(registry, Levels(1)));
    }

    [Fact]
    public void ReplacingARuleMovesTheVersionJustLikeAddingOne()
    {
        var registry = new PassiveStatRegistry();
        registry.Register(new PassiveStatRule(901, "[hp max]", 0));
        Assert.Equal((1, 1), (registry.Count, registry.Version));
        registry.Register(new PassiveStatRule(901, "[hp max]", 1));            // 同一技能同一项:覆盖,条数不变,版本照样加一
        Assert.Equal((1, 2), (registry.Count, registry.Version));
        Assert.Equal(Only("[hp max]", 50), Compute(registry, Levels(1)));
    }

    [Fact]
    public void TheRegistryVersionMovesOnRealChangesOnly()
    {
        var registry = new PassiveStatRegistry();
        registry.Register(new PassiveStatRule(901, "[hp max]", 0));
        registry.Register(new PassiveStatRule(902, "[hp max]", 0));
        Assert.Equal(2, registry.Version);
        Assert.Equal(0, registry.Remove(555));                        // 没有这个技能的规则:没变
        Assert.Equal(2, registry.Version);
        Assert.Equal(1, registry.Remove(901));
        Assert.Equal((1, 3), (registry.Count, registry.Version));
        registry.Clear();
        Assert.Equal((0, 4), (registry.Count, registry.Version));
        registry.Clear();                                             // 已经空了:没变
        Assert.Equal(4, registry.Version);
    }

    [Fact]
    public void MissingInputsGiveAnEmptyBlockInsteadOfFailing()
    {
        var registry = new PassiveStatRegistry();
        registry.Register(new PassiveStatRule(901, "[hp max]", 0));
        Assert.Equal(default, PassiveStatBonus.Compute(registry, null, Levels(1)));
        Assert.Equal(default, PassiveStatBonus.Compute(registry, Resolve, null));
    }
}
