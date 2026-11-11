using Dnf.Data;
using static Dnf.Game.Tests.ExpertJobTestData;

namespace Dnf.Game.Tests;

/// <summary>
/// 副职业的等级与经验。依据:服务端 CUser::GetCurExpertJobLevel 0x0868bc7c、STExpertJobScript::GetLevel 0x08693ba0、isBoundaryExpValue 0x0849f420、
/// STExpertJobEtcScript::isExpertJobLimitLevel 0x0849f47c、CExpertJob::IncreaseExpertJobExp 0x0849ae40(静态读码)。
/// 合成数据:经验阈值 100、250、450、700(第 1 级从 0 到 100 不含,第 4 级从 450 到 700 封顶);
/// 等级上限:副职业 1 到 4 级分别要求角色 10、10、20、30 级;技能 901、902(脚本里写的等级 1)。
/// </summary>
public class ExpertJobLevelTests
{
    private static ExpertExpResult Inc(int cur, int requested, int charLevel)
        => ExpertJobLevels.Increase(ExpertJobKind.Disjointer, Disjointer(), Etc(), cur, requested, charLevel);

    private static int Level(int exp, int charLevel)
        => ExpertJobLevels.CurrentLevel(ExpertJobKind.Disjointer, Disjointer(), Etc(), exp, charLevel);

    // ------------------------------------------------------------------ 当前等级(带闸门)

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(99, 99, 1)]
    [InlineData(120, 1, 2)]       // 不在边界上:脚本等级,角色等级再低也不挡
    [InlineData(449, 1, 3)]
    [InlineData(100, 9, 1)]       // 恰在阈值 100 上:脚本等级 2 要角色 10 级,9 级退一级
    [InlineData(100, 10, 2)]
    [InlineData(250, 19, 2)]      // 阈值 250:脚本等级 3 要 20 级
    [InlineData(250, 20, 3)]
    [InlineData(450, 29, 3)]      // 阈值 450:脚本等级 4 要 30 级
    [InlineData(450, 30, 4)]
    [InlineData(700, 29, 3)]      // 封顶的阈值 700 仍是脚本等级 4,要 30 级
    [InlineData(700, 30, 4)]
    [InlineData(700, 99, 4)]
    [InlineData(701, 1, 4)]       // 越过最后阈值又不在边界上:脚本等级 4,不闸
    public void TheCurrentLevelIsGatedByTheCharacterLevelOnlyAtABoundary(int exp, int charLevel, int level)
    {
        Assert.Equal(level, Level(exp, charLevel));
    }

    [Fact]
    public void AMissingLimitEntryGatesTheBoundaryEvenForAHighCharacter()
    {
        // 边界上、[expertjob level limit] 里没有这一级:一律退一级。
        int level = ExpertJobLevels.CurrentLevel(ExpertJobKind.Disjointer, Disjointer(), ExpertJobEtc.Empty, 100, 99);
        Assert.Equal(1, level);
        int ungated = ExpertJobLevels.CurrentLevel(ExpertJobKind.Disjointer, Disjointer(), null, 120, 1);
        Assert.Equal(2, ungated);
    }

    [Fact]
    public void WithoutAJobOrScriptTheLevelIsZero()
    {
        Assert.Equal(0, ExpertJobLevels.CurrentLevel(ExpertJobKind.None, Disjointer(), Etc(), 500, 99));
        Assert.Equal(0, ExpertJobLevels.CurrentLevel(ExpertJobKind.Disjointer, null, Etc(), 500, 99));
    }

    // ------------------------------------------------------------------ 增加经验

    [Fact]
    public void ExpInsideALevelJustAddsUp()
    {
        ExpertExpResult r = Inc(0, 50, 5);
        Assert.True(r.Changed);
        Assert.Equal(50, r.NewExp);
        Assert.False(r.LeveledUp);
        Assert.Equal(1, r.Level);
        Assert.Empty(r.SkillGrowth);
    }

    [Fact]
    public void ExpStopsAtTheThresholdWhileTheCharacterIsTooLowForTheNextLevel()
    {
        ExpertExpResult r = Inc(50, 500, 5);
        Assert.True(r.Changed);
        Assert.Equal(100, r.NewExp);       // 夹到改前等级对应的阈值
        Assert.False(r.LeveledUp);
        Assert.Equal(1, r.Level);          // 停在阈值上、角色等级不够,当前等级仍是 1
    }

    [Fact]
    public void MoreExpAtAGatedThresholdChangesNothing()
    {
        ExpertExpResult r = Inc(100, 150, 5);
        Assert.False(r.Changed);
        Assert.Equal(100, r.NewExp);
        Assert.False(r.LeveledUp);
        Assert.Equal(1, r.Level);
        Assert.Empty(r.SkillGrowth);
    }

    [Fact]
    public void OnceTheCharacterIsHighEnoughTheExpMovesOnInsideTheNewLevel()
    {
        ExpertExpResult r = Inc(100, 150, 10);
        Assert.True(r.Changed);
        Assert.Equal(150, r.NewExp);
        Assert.False(r.LeveledUp);
        Assert.Equal(2, r.Level);
    }

    [Fact]
    public void CrossingAThresholdTheCharacterMayPassIsALevelUpThatRegrantsSkillsAtTheNewLevel()
    {
        ExpertExpResult r = Inc(90, 100, 10);
        Assert.True(r.Changed);
        Assert.Equal(100, r.NewExp);
        Assert.True(r.LeveledUp);
        Assert.Equal(2, r.Level);
        // 重新授予用的是当前副职业等级(2),不是脚本 [skill] 里写的 1。
        Assert.Equal(2, r.SkillGrowth.Count);
        Assert.Equal(901, r.SkillGrowth[0].SkillId);
        Assert.Equal(2, r.SkillGrowth[0].Level);
        Assert.Equal(902, r.SkillGrowth[1].SkillId);
        Assert.Equal(2, r.SkillGrowth[1].Level);
    }

    [Fact]
    public void ALevelUpIsJudgedOnTheRequestedExpNotOnTheClampedOne()
    {
        // 角色 20 级:一次要到 300(脚本等级 3,要 20 级)→ 允许,不夹。
        ExpertExpResult far = Inc(90, 300, 20);
        Assert.True(far.LeveledUp);
        Assert.Equal(300, far.NewExp);
        Assert.Equal(3, far.Level);

        // 角色 10 级:300 对应的脚本等级 3 要 20 级 → 不算升级,经验夹到 100;
        // 夹完停在阈值 100 上、角色 10 级够第 2 级,所以当前等级变成 2,却没有"升级"标志(服务端的写法,照搬)。
        ExpertExpResult near = Inc(90, 300, 10);
        Assert.True(near.Changed);
        Assert.False(near.LeveledUp);
        Assert.Equal(100, near.NewExp);
        Assert.Equal(2, near.Level);
        Assert.Empty(near.SkillGrowth);
    }

    [Fact]
    public void TheExpIsClampedToTheLastThreshold()
    {
        ExpertExpResult r = Inc(0, 1_000_000_000, 99);
        Assert.True(r.Changed);
        Assert.Equal(700, r.NewExp);
        Assert.True(r.LeveledUp);
        Assert.Equal(4, r.Level);

        Assert.Equal(700, Inc(690, int.MaxValue, 99).NewExp);
    }

    [Fact]
    public void AtTheCapNothingChangesUnlessTheCharacterLevelIsALimitValue()
    {
        ExpertExpResult r = Inc(700, 800, 99);     // 99 不是任何一个上限值
        Assert.False(r.Changed);
        Assert.False(r.LeveledUp);
        Assert.Equal(700, r.NewExp);
        Assert.Equal(4, r.Level);
    }

    [Fact]
    public void AtTheCapACharacterAtALimitValueKeepsRegrantingSkillsBecauseTheLevelIsNotTheLiteralEleven()
    {
        // 服务端用写死的 11 判断"不是最高级才可能升级",表长为 4 时最高级 4 ≠ 11,所以边界 + 上限值的角色等级每次都算"升级"。
        ExpertExpResult r = Inc(700, 800, 30);
        Assert.True(r.Changed);
        Assert.True(r.LeveledUp);
        Assert.Equal(700, r.NewExp);
        Assert.Equal(4, r.Level);
        Assert.Equal(2, r.SkillGrowth.Count);
        Assert.Equal(4, r.SkillGrowth[0].Level);
    }

    [Fact]
    public void ATableWithElevenLevelsDoesNotRegrantAtTheCap()
    {
        ExpertJobScript script = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[]
        {
            ExpTable(10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110),
            L("[skill]", 901, 1),
        });
        ExpertJobEtc etc = ExpertJobEtc.Parse(new[] { L("[expertjob level limit]", 11, 50) });
        Assert.Equal(11, script.MaxLevel);
        Assert.Equal(11, ExpertJobLevels.CurrentLevel(ExpertJobKind.Disjointer, script, etc, 110, 50));
        ExpertExpResult r = ExpertJobLevels.Increase(ExpertJobKind.Disjointer, script, etc, 110, 200, 50);
        Assert.False(r.Changed);
        Assert.False(r.LeveledUp);
        Assert.Equal(110, r.NewExp);
        Assert.Equal(11, ExpertJobLevels.LevelLiteral);
    }

    [Fact]
    public void AnEqualLevelBoundaryAtALimitValueCountsAsALevelUpEvenThoughTheLevelStays()
    {
        // 角色 10 级(是上限值):经验从 100 升到边界 250,脚本等级 3 要 20 级,当前等级仍是 2;
        // 改前改后等级相同,但新经验在边界上、角色等级是上限值 → 算升级(重新授予技能,经验不夹)。
        ExpertExpResult r = Inc(100, 250, 10);
        Assert.True(r.Changed);
        Assert.True(r.LeveledUp);
        Assert.Equal(250, r.NewExp);
        Assert.Equal(2, r.Level);
        Assert.Equal(2, r.SkillGrowth[0].Level);

        // 角色 11 级不是上限值:同样的经验变化不算升级。
        ExpertExpResult plain = Inc(100, 250, 11);
        Assert.True(plain.Changed);
        Assert.False(plain.LeveledUp);
        Assert.Equal(250, plain.NewExp);
        Assert.Equal(2, plain.Level);
    }

    [Fact]
    public void WithoutAJobOrAScriptOrAnyLevelsNothingChanges()
    {
        ExpertExpResult none = ExpertJobLevels.Increase(ExpertJobKind.None, Disjointer(), Etc(), 5, 50, 10);
        Assert.False(none.Changed);
        Assert.Equal(5, none.NewExp);
        Assert.Equal(0, none.Level);

        ExpertExpResult noScript = ExpertJobLevels.Increase(ExpertJobKind.Disjointer, null, Etc(), 5, 50, 10);
        Assert.False(noScript.Changed);

        ExpertJobScript empty = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new ScriptLine[0]);
        ExpertExpResult noLevels = ExpertJobLevels.Increase(ExpertJobKind.Disjointer, empty, Etc(), 5, 50, 10);
        Assert.False(noLevels.Changed);
        Assert.Equal(5, noLevels.NewExp);
    }
}
