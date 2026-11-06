using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 被动威力加成的登记表与累加规则(M106,静态读码,非 REA):全部用合成技能号 901–914,
/// 期望值来自手算(列取值 × 0.01 逐项累加;消费时 取值 × 倍率 − 100 再向零截断)。
/// </summary>
public sealed class PassiveAttackBonusTests
{
    private const int NativeState = 5;
    private const int OtherState = 6;

    // 901:每级 3 列,2 级。第 1 级 19/33/44,第 2 级 58/59/60;可执行状态 {5}
    private static readonly int[] TableA = { 3, 19, 33, 44, 58, 59, 60 };
    // 902:每级 2 列,1 级。第 1 级 7/9;可执行状态 {5,6}
    private static readonly int[] TableB = { 2, 7, 9 };

    private static SkillDef Skill(int id, int[] levelInfo, params int[] executableStates)
    {
        var lines = new List<ScriptLine>
        {
            Str("[type]", "[passive]"),
            Ints("[level info]", levelInfo),
        };
        if (executableStates.Length > 0)
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

    private static readonly Dictionary<int, SkillDef> Defs = new Dictionary<int, SkillDef>
    {
        [901] = Skill(901, TableA, NativeState),
        [902] = Skill(902, TableB, NativeState, OtherState),
        [903] = Skill(903, TableA),                 // 没有可执行状态表
        [904] = Skill(904, TableB, OtherState),     // 只在另一个状态生效
    };

    private static SkillDef Resolve(int id) => Defs.TryGetValue(id, out SkillDef d) ? d : null;

    private static IReadOnlyList<KeyValuePair<int, int>> Entries(params (int skill, int column)[] items)
    {
        var list = new List<KeyValuePair<int, int>>();
        foreach (var (skill, column) in items)
        {
            list.Add(new KeyValuePair<int, int>(skill, column));
        }
        return list;
    }

    private static float Sum(float baseRate, int state, IReadOnlyList<KeyValuePair<int, int>> entries,
        Func<int, int> levelOf, PassiveBonusFamily family = PassiveBonusFamily.Standard, bool pvp = false) =>
        PassiveAttackBonus.Accumulate(baseRate, state, entries, Resolve, levelOf, family, pvp);

    [Fact]
    public void EmptyTableKeepsBaseRate()
    {
        Assert.Equal(1.25f, Sum(1.25f, NativeState, Entries(), _ => 1));
        Assert.Equal(1.25f, PassiveAttackBonus.Accumulate(1.25f, NativeState, null, Resolve, _ => 1, PassiveBonusFamily.Standard, false));
    }

    [Fact]
    public void RegisteredSkillAddsLevelColumnTimesScale()
    {
        // 901 第 2 级第 1 列 = 59,加成 0.59
        float rate = Sum(1f, NativeState, Entries((901, 1)), id => id == 901 ? 2 : 0);
        Assert.Equal(1.59f, rate, 5);
    }

    [Fact]
    public void EntriesAccumulateAcrossSkills()
    {
        // 901 第 1 级第 0 列 = 19(0.19),902 第 1 级第 1 列 = 9(0.09)
        float rate = Sum(1f, NativeState, Entries((901, 0), (902, 1)), _ => 1);
        Assert.Equal(1.28f, rate, 5);
    }

    [Fact]
    public void SkillWithoutMatchingStateIsSkipped()
    {
        // 904 只在另一个状态生效;903 没有可执行状态表(空表为否)
        Assert.Equal(1f, Sum(1f, NativeState, Entries((904, 0), (903, 0)), _ => 1));
        Assert.Equal(1.07f, Sum(1f, OtherState, Entries((904, 0), (903, 0)), _ => 1), 5);
        // 被跳过的项排在前面,后面生效的项照样累加(跳过不是中止)
        Assert.Equal(1.19f, Sum(1f, NativeState, Entries((903, 0), (904, 0), (901, 0)), _ => 1), 5);
    }

    [Fact]
    public void UnresolvedSkillIsSkipped()
    {
        Assert.Equal(1f, Sum(1f, NativeState, Entries((914, 0)), _ => 1));
        Assert.Equal(1.19f, Sum(1f, NativeState, Entries((901, 0), (914, 0)), _ => 1), 5);
        Assert.Equal(1.19f, Sum(1f, NativeState, Entries((913, 0), (901, 0)), _ => 1), 5);
    }

    [Fact]
    public void NonPositiveOrOutOfTableLevelContributesNothing()
    {
        Assert.Equal(1f, Sum(1f, NativeState, Entries((901, 0)), _ => 0));
        Assert.Equal(1f, Sum(1f, NativeState, Entries((901, 0)), _ => -1));
        // 取值表只有 2 级:第 3 级按客户端读取函数返回 0(而不是取最后一级)
        Assert.Equal(1f, Sum(1f, NativeState, Entries((901, 0)), _ => 3));
    }

    [Fact]
    public void OutOfRangeColumnContributesNothing()
    {
        Assert.Equal(1f, Sum(1f, NativeState, Entries((901, 3)), _ => 1));
        Assert.Equal(1f, Sum(1f, NativeState, Entries((901, -1)), _ => 1));
    }

    [Fact]
    public void FamiliesDifferOnlyInFloatRounding()
    {
        // 19 × 0.01 + 1:通用类在 x87 里一次舍入,另一类先把乘积舍入成单精度再相加。两个结果恰好相差 1 个最低位
        float standard = Sum(1f, NativeState, Entries((901, 0)), _ => 1, PassiveBonusFamily.Standard);
        float truncating = Sum(1f, NativeState, Entries((901, 0)), _ => 1, PassiveBonusFamily.Truncating);
        Assert.Equal(1.1899999380111694f, standard);
        Assert.Equal(1.190000057220459f, truncating);
    }

    [Fact]
    public void PvpUsesPvpTableWhenPresent()
    {
        SkillDef withPvp = new SkillDef(905, "skill/test/p905.skl", new[]
        {
            Str("[type]", "[passive]"),
            Ints("[executable states]", NativeState),
            Ints("[level info]", 1, 10),
            Ints("[pvp]"),
            Ints("[level info]", 1, 40, 45),
            Ints("[/pvp]"),
        });
        float pve = PassiveAttackBonus.Accumulate(1f, NativeState, Entries((905, 0)), _ => withPvp, _ => 1, PassiveBonusFamily.Standard, false);
        float pvp = PassiveAttackBonus.Accumulate(1f, NativeState, Entries((905, 0)), _ => withPvp, _ => 1, PassiveBonusFamily.Standard, true);
        Assert.Equal(1.1f, pve, 5);
        Assert.Equal(1.4f, pvp, 5);
        // 决斗场表有 2 级、普通表只有 1 级:第 2 级在决斗场取 45,在普通模式超出级数得 0
        float pvpLevel2 = PassiveAttackBonus.Accumulate(1f, NativeState, Entries((905, 0)), _ => withPvp, _ => 2, PassiveBonusFamily.Standard, true);
        float pveLevel2 = PassiveAttackBonus.Accumulate(1f, NativeState, Entries((905, 0)), _ => withPvp, _ => 2, PassiveBonusFamily.Standard, false);
        Assert.Equal(1.45f, pvpLevel2, 5);
        Assert.Equal(1f, pveLevel2);
    }

    [Fact]
    public void BonusPercentIsLevelValueTimesRateMinusHundredTruncatedTowardZero()
    {
        Assert.Equal(125, PassiveAttackBonus.BonusPercent(150f, 1.5f));
        Assert.Equal(-50, PassiveAttackBonus.BonusPercent(50f, 1f));
        Assert.Equal(-1, PassiveAttackBonus.BonusPercent(99f, 1f));
        // 99.9 − 100 = −0.1:向零截断得 0(不是向下取整的 −1)
        Assert.Equal(0, PassiveAttackBonus.BonusPercent(100f, 0.999f));
        // 101.9 − 100 = 1.9:截断得 1(不是四舍五入的 2)
        Assert.Equal(1, PassiveAttackBonus.BonusPercent(100f, 1.019f));
    }

    [Fact]
    public void RegisterOverwritesColumnAndRemoveErasesByKey()
    {
        var map = new PassiveAttackBonusMap();
        map.Set(PassiveBonusFamily.Standard, 902, 1);
        map.Set(PassiveBonusFamily.Standard, 902, 0);
        Assert.Equal(1, map.Count(PassiveBonusFamily.Standard));
        Assert.Equal(0, map.Entries(PassiveBonusFamily.Standard)[0].Value);
        Assert.False(map.Remove(PassiveBonusFamily.Standard, 901));
        Assert.True(map.Remove(PassiveBonusFamily.Standard, 902));
        Assert.Equal(0, map.Count(PassiveBonusFamily.Standard));
    }

    [Fact]
    public void EntriesAreAscendingBySkillIdAndFamiliesAreIndependent()
    {
        var map = new PassiveAttackBonusMap();
        map.Set(PassiveBonusFamily.Standard, 903, 0);
        map.Set(PassiveBonusFamily.Standard, 901, 2);
        map.Set(PassiveBonusFamily.Standard, 902, 1);
        map.Set(PassiveBonusFamily.Truncating, 910, 0);
        IReadOnlyList<KeyValuePair<int, int>> standard = map.Entries(PassiveBonusFamily.Standard);
        Assert.Equal(new[] { 901, 902, 903 }, standard.Select(e => e.Key).ToArray());
        Assert.Equal(1, map.Count(PassiveBonusFamily.Truncating));
        Assert.False(map.Remove(PassiveBonusFamily.Truncating, 901));
        Assert.Equal(3, map.Count(PassiveBonusFamily.Standard));
    }
}
