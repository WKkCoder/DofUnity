using Dnf.Data;
using Dnf.Game;
using static Dnf.Play.Tests.ExpTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 技能簿在主角进度里的接线(M35,证据 E-127):升级给的点数进技能点余额、接数据时给白送的技能与应有的总点数、学、退、重置、转职、存档快照与还原(含旧存档)。
/// 技能与点数全是合成的:点数表是"第 L 级给 L 点"(<see cref="ExpTests.Sp"/>),所以 4 级累计 1 + 2 + 3 + 4 = 10 点。
/// </summary>
public class SkillProgressTests
{
    private static ScriptLine Ints(string tag, params int[] values)
    {
        var list = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            list[i] = new ScriptValue(ScriptValueKind.Int, values[i], null);
        }
        return new ScriptLine(tag, list);
    }

    private static ScriptLine Str(string tag, string text)
    {
        return new ScriptLine(tag, new[] { new ScriptValue(ScriptValueKind.String, 0, text) });
    }

    private static SkillDef Skill(int id, int req, int range, int[] max, int cost, bool active = true, int[] pre = null)
    {
        var lines = new List<ScriptLine>
        {
            Str("[type]", active ? "[active]" : "[passive]"),
            Ints("[required level]", req),
            Ints("[required level range]", range),
            Ints("[growtype maximum level]", max),
            Ints("[purchase cost]", cost),
        };
        if (pre != null)
        {
            lines.Add(Ints("[pre required skill]", pre));
        }
        return new SkillDef(id, "skill/test/s" + id + ".skl", lines.ToArray());
    }

    /// <summary>1 号:1 级起、间隔 2、最高 5、每级 3 点;2 号:被动,3 级起、间隔 3、最高 3、每级 4 点;3 号:要求 1 号 2 级、5 级起;4 号:只有成长类型 1 学得了。</summary>
    private static SkillCatalog Catalog()
    {
        var five = new[] { 5, 5, 5, 5, 5, 5 };
        return new SkillCatalog(0, new[]
        {
            Skill(1, 1, 2, five, 3),
            Skill(2, 3, 3, new[] { 3, 3, 3, 3, 3, 3 }, 4, active: false),
            Skill(3, 5, 1, five, 5, pre: new[] { 1, 2 }),
            Skill(4, 1, 1, new[] { 0, 3, 0, 0, 0, 0 }, 2),
        });
    }

    /// <summary>白送:开局 1 号 1 级;成长类型 1(下标 1)再给 4 号 1 级。</summary>
    private static ScriptLine[] ChrLines()
    {
        return new[]
        {
            Ints("[initial value]"),
            Ints("[skill]", 1, 1),
            Ints("[growtype 1]"),
            Ints("[growtype 2]"),
            Ints("[skill]", 4, 1),
        };
    }

    private static HeroProgress Hero(int level, bool grant = true)
    {
        HeroProgress p = Progress(level: level);
        p.AttachSkills(Catalog(), ChrLines(), grant);
        return p;
    }

    [Fact]
    public void LevelUpsFeedThePointPool()
    {
        HeroProgress p = Progress();
        Assert.Equal(0, p.SkillPoints);
        p.Gain(100);                                   // 100 经验升到 2 级,给 2 点
        Assert.Equal(2, p.Level);
        Assert.Equal(2, p.SkillPoints);
        Assert.Equal(p.Skills.Sp, p.SkillPoints);
        Assert.Equal(0, p.AwakeningPoints);
    }

    [Fact]
    public void AttachingGivesTheGivenSkillsAndTheTotalPointsOfTheLevel()
    {
        HeroProgress p = Hero(4);
        Assert.True(p.HasSkillData);
        Assert.Equal(10, p.SkillPoints);                // 1 + 2 + 3 + 4
        Assert.Equal(1, p.Skills.LevelOf(1));
        Assert.Equal(1, p.Skills.GivenLevelOf(1));
        Assert.Equal(1, p.Skills.QuickAt(0));
        // 不给总点数时沿用已有的余额
        HeroProgress q = Progress(level: 4);
        q.Gain(0);
        q.AttachSkills(Catalog(), ChrLines(), grantTotalPoints: false);
        Assert.Equal(0, q.SkillPoints);
        Assert.False(Progress().HasSkillData);
    }

    [Fact]
    public void LearnAndRefundSpendAndReturnPoints()
    {
        HeroProgress p = Hero(4);                       // 10 点;1 号现在 1 级
        Assert.Equal(SkillOpResult.Ok, p.LearnSkill(1, 1, out int spent));   // 第 2 级要求 3 级
        Assert.Equal(3, spent);
        Assert.Equal(7, p.SkillPoints);
        Assert.Equal(2, p.Skills.LevelOf(1));
        Assert.Equal(SkillOpResult.NotAllowed, p.LearnSkill(1, 1, out _));    // 第 3 级要求 5 级
        Assert.Equal(SkillOpResult.Ok, p.RefundSkill(1, 1, out int back));
        Assert.Equal(3, back);
        Assert.Equal(10, p.SkillPoints);
        Assert.Equal(SkillOpResult.BelowGiven, p.RefundSkill(1, 1, out _));   // 退到 0 低于白送的 1 级
        Assert.Equal(1, p.Skills.LevelOf(1));
    }

    [Fact]
    public void ResettingGivesBackAllSpentPoints()
    {
        HeroProgress p = Hero(5);                       // 15 点
        p.LearnSkill(1, 2, out _);                      // 学到 3 级:6 点
        p.LearnSkill(2, 1, out _);                      // 4 点
        p.LearnSkill(3, 1, out _);                      // 5 点
        Assert.Equal(0, p.SkillPoints);
        Assert.Equal(3, p.Skills.LearnedCount);
        p.ResetSkills();
        Assert.Equal(15, p.SkillPoints);
        Assert.Equal(1, p.Skills.LearnedCount);
        Assert.Equal(1, p.Skills.LevelOf(1));
        Assert.Equal(1, p.Skills.QuickAt(0));
        Assert.Equal(0, p.Skills.QuickAt(1));
    }

    [Fact]
    public void ChangingGrowtypeResetsTheSkillsAndGivesTheNewOnes()
    {
        HeroProgress p = Hero(5);
        p.LearnSkill(1, 1, out _);
        int version = p.Version;
        Assert.False(p.ChangeGrowtype(0));              // 没变
        Assert.False(p.ChangeGrowtype(-1));
        Assert.False(p.ChangeGrowtype(6));
        Assert.Equal(version, p.Version);
        Assert.True(p.ChangeGrowtype(1));
        Assert.Equal(1, p.Growtype);
        Assert.True(p.Version > version);
        Assert.Equal(15, p.SkillPoints);                // 清空重来:总额回来
        Assert.Equal(2, p.Skills.LearnedCount);         // 开局的 1 号与这个成长类型白送的 4 号
        Assert.Equal(1, p.Skills.LevelOf(4));
        Assert.Equal(1, p.Skills.QuickAt(0));
        Assert.Equal(4, p.Skills.QuickAt(1));
        // 新成长类型学不了的技能(第 4 号只有成长类型 1;换回 0 以后它不再白送也学不了)
        Assert.True(p.ChangeGrowtype(0));
        Assert.Equal(0, p.Skills.LevelOf(4));
        Assert.Equal(SkillOpResult.NotAllowed, p.LearnSkill(4, 1, out _));
    }

    [Fact]
    public void ASnapshotCarriesSkillsQuickBarGrowtypeAndPoints()
    {
        HeroProgress p = Hero(5);
        p.ChangeGrowtype(1);
        p.LearnSkill(1, 2, out _);                      // 3 级:6 点
        p.LearnSkill(2, 1, out _);                      // 被动 4 点
        p.Skills.AssignQuick(5, 1);                     // 1 号挪到第 5 格
        HeroSave save = p.Snapshot();
        Assert.Equal(1, save.Growtype);
        Assert.Equal(5, save.SkillPoints);
        Assert.Equal(new[] { 1, 2, 4 }, save.Skills.Select(s => s.Id).ToArray());
        Assert.Equal(new[] { 3, 1, 1 }, save.Skills.Select(s => s.Level).ToArray());
        Assert.Equal(new[] { 1, 5 }, save.SkillQuick.Select(s => s.Slot).Order().ToArray());

        // 文本往返之后还原到新的进度
        Assert.True(HeroSave.TryParse(save.ToText(), out HeroSave parsed, out string error), error);
        HeroProgress q = Hero(1, grant: false);
        q.Restore(parsed, code => null);
        Assert.Equal(1, q.Growtype);
        Assert.Equal(5, q.SkillPoints);
        Assert.Equal(3, q.Skills.LevelOf(1));
        Assert.Equal(1, q.Skills.LevelOf(2));
        Assert.Equal(1, q.Skills.LevelOf(4));
        Assert.Equal(4, q.Skills.QuickAt(1));
        Assert.Equal(1, q.Skills.QuickAt(5));
        Assert.Equal(-1, q.Skills.QuickSlotOf(2));
    }

    [Fact]
    public void AnOldSaveWithoutSkillRowsKeepsOnlyTheGivenSkills()
    {
        var old = new HeroSave { Level = 5, Exp = 1000, SkillPoints = 15 };
        HeroProgress p = Hero(5);
        p.LearnSkill(1, 2, out _);                      // 先学一点,证明还原会换掉现状
        Assert.Equal(3, p.Skills.LevelOf(1));
        p.Restore(old, code => null);
        Assert.Equal(5, p.Level);
        Assert.Equal(15, p.SkillPoints);                // 旧存档的点数就是余额
        Assert.Equal(1, p.Skills.LearnedCount);
        Assert.Equal(1, p.Skills.LevelOf(1));
        Assert.Equal(1, p.Skills.QuickAt(0));
    }

    [Fact]
    public void RestoringWithoutSkillDataStillRestoresThePoints()
    {
        HeroProgress p = Progress();
        var save = new HeroSave { Level = 3, SkillPoints = 12, AwakeningPoints = 4 };
        save.Skills.Add(new SavedSkill(1, 2));
        p.Restore(save, code => null);
        Assert.Equal(12, p.SkillPoints);
        Assert.Equal(4, p.AwakeningPoints);
        Assert.Equal(0, p.Skills.LearnedCount);         // 没有目录:技能行丢掉
    }

    [Fact]
    public void TheSaveTextOmitsGrowtypeAndSkillRowsWhenThereAreNone()
    {
        string text = new HeroSave().ToText();
        Assert.DoesNotContain("growtype", text);
        Assert.DoesNotContain("skill", text);
        Assert.DoesNotContain("skquick", text);
        var save = new HeroSave { Growtype = 2 };
        save.Skills.Add(new SavedSkill(7, 3));
        save.SkillQuick.Add(new SavedSkillSlot(4, 7));
        text = save.ToText();
        Assert.Contains("growtype 2\n", text);
        Assert.Contains("skill 7 3\n", text);
        Assert.Contains("skquick 4 7\n", text);
    }

    [Theory]
    [InlineData("growtype 6")]
    [InlineData("growtype -1")]
    [InlineData("skill 0 1")]
    [InlineData("skill 5 0")]
    [InlineData("skill 5 1001")]
    [InlineData("skquick 6 1")]
    [InlineData("skquick 0 0")]
    public void SkillRowsOutOfRangeMakeTheSaveUnreadable(string row)
    {
        Assert.False(HeroSave.TryParse("DNFUNITY-SAVE 1\n" + row + "\n", out HeroSave save, out string error));
        Assert.Null(save);
        Assert.Contains("超出范围", error);
    }

    [Fact]
    public void SkillRowsWithTheWrongArityAreRejected()
    {
        Assert.False(HeroSave.TryParse("DNFUNITY-SAVE 1\nskill 5\n", out _, out string error));
        Assert.Contains("skill", error);
        Assert.False(HeroSave.TryParse("DNFUNITY-SAVE 1\ngrowtype 1 2\n", out _, out error));
        Assert.False(HeroSave.TryParse("DNFUNITY-SAVE 1\nskquick a b\n", out _, out error));
    }
}
