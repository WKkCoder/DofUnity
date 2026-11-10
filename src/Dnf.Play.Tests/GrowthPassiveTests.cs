using Dnf.Data;
using Dnf.Game;

namespace Dnf.Play.Tests;

/// <summary>
/// 被动技能的属性加成叠在主角面板上(r15-growth;规则、证据与"登记表默认为空"的原因见 Dnf.Game 的 PassiveStatBonus 文件头)。
/// 技能编号全是合成的(921 到 924),加成对应的属性、列和数值是测试自己登记的,与原版数据无关;这里检验的是接线:
/// 学会与退掉时面板立刻跟着变、版本只在面板真的变了才加、叠在"等级 + 装备"之上、读档与换技能簿后重算。
/// </summary>
public class GrowthPassiveTests
{
    /// <summary>合成被动:1 级起、间隔 1、每级 1 点,各成长类型最高级就是 <paramref name="values"/> 的行数;<paramref name="columns"/> 列、按级排开的数。</summary>
    private static SkillDef Passive(int id, int columns, params int[] values)
    {
        var info = new int[values.Length + 1];
        info[0] = columns;
        values.CopyTo(info, 1);
        int max = values.Length / columns;
        return new SkillDef(id, "skill/synthetic/g" + id + ".skl", new[]
        {
            LearningFixture.Str("[type]", "[passive]"),
            LearningFixture.Ints("[required level]", 1),
            LearningFixture.Ints("[required level range]", 1),
            LearningFixture.Ints("[growtype maximum level]", max, max, max, max, max, max),
            LearningFixture.Ints("[purchase cost]", 1),
            LearningFixture.Ints("[level info]", info),
        });
    }

    /// <summary>921:一列、三级(10、25、45);922:两列、两级((3,100)、(5,200));923:一列、两级(7、8);924:普通主动技能,没有等级数据。</summary>
    private static SkillCatalog Catalog()
    {
        return new SkillCatalog(0, new[]
        {
            Passive(921, 1, 10, 25, 45),
            Passive(922, 2, 3, 100, 5, 200),
            Passive(923, 1, 7, 8),
            LearningFixture.Skill(924, 1, 1, LearningFixture.Five, 1),
        });
    }

    private static HeroProgress Hero(int level = 20)
    {
        return LearningFixture.Hero(Catalog(), level);
    }

    private static PassiveStatRule Atk(int skill = 921, int column = 0, long scale = 10) => new PassiveStatRule(skill, "[physical attack]", column, scale);

    [Fact]
    public void WithoutAnyRuleLearningAPassiveChangesNeitherTheStatsNorTheVersion()
    {
        HeroProgress hero = Hero();
        StatBlock before = hero.Stats;
        int version = hero.Version;
        Assert.Equal(SkillOpResult.Ok, hero.LearnSkill(921, 2, out _));
        Assert.Equal(before, hero.Stats);
        Assert.Equal(default(StatBlock), hero.PassiveStatBlock);
        Assert.Equal(version, hero.Version);
    }

    [Fact]
    public void ARegisteredPassiveRaisesTheStatWhenLearnedAndLowersItWhenRefundedAndTheVersionFollows()
    {
        HeroProgress hero = Hero();
        hero.PassiveRegistry.Register(Atk());
        long baseAtk = hero.Stats.PhysicalAttack;
        int version = hero.Version;

        Assert.Equal(SkillOpResult.Ok, hero.LearnSkill(921, 1, out _));
        Assert.Equal(baseAtk + 100, hero.Stats.PhysicalAttack);                // 1 级:数 10,每个数 10 个十分之一单位
        Assert.Equal(version + 1, hero.Version);

        Assert.Equal(SkillOpResult.Ok, hero.LearnSkill(921, 1, out _));
        Assert.Equal(baseAtk + 250, hero.Stats.PhysicalAttack);                // 2 级:数 25
        Assert.Equal(version + 2, hero.Version);

        Assert.Equal(SkillOpResult.Ok, hero.RefundSkill(921, 1, out _));
        Assert.Equal(baseAtk + 100, hero.Stats.PhysicalAttack);
        Assert.Equal(SkillOpResult.Ok, hero.RefundSkill(921, 1, out _));
        Assert.Equal(baseAtk, hero.Stats.PhysicalAttack);
        Assert.Equal(version + 4, hero.Version);
    }

    [Fact]
    public void LearningAPassiveWithoutARuleLeavesTheVersionAloneEvenWhenAnotherOneHasARule()
    {
        HeroProgress hero = Hero();
        hero.PassiveRegistry.Register(Atk(921));
        int version = hero.Version;
        Assert.Equal(SkillOpResult.Ok, hero.LearnSkill(923, 2, out _));          // 923 没登记
        Assert.Equal(version, hero.Version);
        Assert.Equal(default(StatBlock), hero.PassiveStatBlock);
    }

    [Fact]
    public void ThePassivePartSitsOnTopOfTheLevelAndEquipmentPartsAndNothingElse()
    {
        HeroProgress hero = Hero();
        hero.PassiveRegistry.Register(Atk());
        hero.LearnSkill(921, 3, out _);
        Assert.Equal(hero.BaseStats + hero.Worn.TotalStatBlock() + hero.PassiveStatBlock, hero.Stats);
        Assert.Equal(450, hero.PassiveStatBlock.PhysicalAttack);                 // 3 级:数 45
        Assert.Equal(0, hero.PassiveStatBlock.HpMax);
        Assert.Equal(0, hero.PassiveStatBlock.MagicalAttack);
    }

    [Fact]
    public void ASkillWithTwoColumnsFeedsTwoDifferentStats()
    {
        HeroProgress hero = Hero();
        hero.PassiveRegistry.Register(new PassiveStatRule(922, "[hp max]", 0));
        hero.PassiveRegistry.Register(new PassiveStatRule(922, "[mp max]", 1));
        hero.LearnSkill(922, 2, out _);
        Assert.Equal((50, 2000, 0), (hero.PassiveStatBlock.HpMax, hero.PassiveStatBlock.MpMax, hero.PassiveStatBlock.PhysicalAttack));
    }

    [Fact]
    public void TwoSkillsOnTheSameStatAdd()
    {
        HeroProgress hero = Hero();
        hero.PassiveRegistry.Register(Atk(921));
        hero.PassiveRegistry.Register(Atk(923));
        hero.LearnSkill(921, 1, out _);
        hero.LearnSkill(923, 2, out _);
        Assert.Equal(100 + 80, hero.PassiveStatBlock.PhysicalAttack);            // 921 一级 10;923 两级 8
    }

    [Fact]
    public void RegisteringARuleAfterTheSkillWasLearnedTakesEffectOnTheNextRead()
    {
        HeroProgress hero = Hero();
        hero.LearnSkill(921, 2, out _);
        int version = hero.Version;
        hero.PassiveRegistry.Register(Atk());
        Assert.Equal(250, hero.PassiveStatBlock.PhysicalAttack);
        Assert.Equal(version + 1, hero.Version);
        hero.PassiveRegistry.Remove(921);
        Assert.Equal(0, hero.PassiveStatBlock.PhysicalAttack);
        Assert.Equal(version + 2, hero.Version);
        hero.PassiveRegistry.Register(Atk());
        hero.PassiveRegistry.Clear();
        Assert.Equal(default(StatBlock), hero.PassiveStatBlock);
    }

    [Fact]
    public void LearningThroughTheSkillBookDirectlyIsSeenToo()
    {
        HeroProgress hero = Hero();
        hero.PassiveRegistry.Register(Atk());
        int version = hero.Version;
        Assert.Equal(SkillOpResult.Ok, hero.Skills.Learn(921, hero.Level, hero.Growtype, 2, out _));
        Assert.Equal(250, hero.PassiveStatBlock.PhysicalAttack);
        Assert.Equal(version + 1, hero.Version);
    }

    [Fact]
    public void ARuleForASkillThatIsNotInTheCatalogContributesNothing()
    {
        HeroProgress hero = Hero();
        hero.PassiveRegistry.Register(Atk(skill: 999));
        hero.LearnSkill(921, 1, out _);
        Assert.Equal(default(StatBlock), hero.PassiveStatBlock);
    }

    [Fact]
    public void AnActiveSkillWithoutLevelDataNeverFeedsAStat()
    {
        HeroProgress hero = Hero();
        hero.PassiveRegistry.Register(Atk(skill: 924));
        Assert.Equal(SkillOpResult.Ok, hero.LearnSkill(924, 2, out _));
        Assert.Equal(default(StatBlock), hero.PassiveStatBlock);
    }

    [Fact]
    public void ResettingTheSkillsDropsTheBonusAndAChangeOfGrowtypeDoesToo()
    {
        HeroProgress hero = Hero();
        hero.PassiveRegistry.Register(Atk());
        hero.LearnSkill(921, 2, out _);
        int version = hero.Version;
        hero.ResetSkills();
        Assert.Equal(0, hero.PassiveStatBlock.PhysicalAttack);
        Assert.Equal(version + 1, hero.Version);

        hero.LearnSkill(921, 1, out _);
        Assert.Equal(100, hero.PassiveStatBlock.PhysicalAttack);
        Assert.True(hero.ChangeGrowtype(1));
        Assert.Equal(0, hero.PassiveStatBlock.PhysicalAttack);
    }

    [Fact]
    public void TheBonusDoesNotDependOnTheHeroLevel()
    {
        HeroProgress hero = Hero(level: 10);
        hero.PassiveRegistry.Register(Atk());
        hero.LearnSkill(921, 2, out _);
        StatBlock before = hero.PassiveStatBlock;
        hero.Gain(1_000_000);
        Assert.True(hero.Level > 10);
        Assert.Equal(before, hero.PassiveStatBlock);
        Assert.Equal(250, before.PhysicalAttack);
    }

    [Fact]
    public void TheLearnedLevelsComeBackFromASaveAndSoDoesTheBonus()
    {
        HeroProgress hero = Hero();
        hero.PassiveRegistry.Register(Atk());
        hero.LearnSkill(921, 2, out _);
        Assert.True(HeroSave.TryParse(hero.Snapshot().ToText(), out HeroSave save, out string error), error);

        HeroProgress other = Hero();
        other.PassiveRegistry.Register(Atk());
        Assert.Equal(0, other.PassiveStatBlock.PhysicalAttack);
        other.Restore(save, id => null);
        Assert.Equal(hero.PassiveStatBlock, other.PassiveStatBlock);
        Assert.Equal(250, other.PassiveStatBlock.PhysicalAttack);
        Assert.Equal(hero.Stats, other.Stats);
    }

    [Fact]
    public void AttachingANewSkillBookRecomputesTheBonusFromTheNewBook()
    {
        HeroProgress hero = Hero();
        hero.PassiveRegistry.Register(Atk());
        hero.LearnSkill(921, 2, out _);
        Assert.Equal(250, hero.PassiveStatBlock.PhysicalAttack);
        hero.AttachSkills(Catalog(), null, true);                                // 换成一本新的:什么都没学
        Assert.Equal(0, hero.PassiveStatBlock.PhysicalAttack);
    }

    [Fact]
    public void ANewBookWithTheSameVersionNumberButOtherLevelsStillRecomputes()
    {
        HeroProgress hero = Hero();
        hero.PassiveRegistry.Register(Atk(921));
        hero.LearnSkill(921, 1, out _);
        int learnedVersion = hero.Skills.Version;
        Assert.Equal(100, hero.PassiveStatBlock.PhysicalAttack);
        hero.AttachSkills(Catalog(), null, true);
        hero.LearnSkill(923, 1, out _);                                          // 新簿也学了一次:版本数与旧簿一样,内容不同(923 没有规则)
        Assert.Equal(learnedVersion, hero.Skills.Version);
        Assert.Equal(0, hero.PassiveStatBlock.PhysicalAttack);
    }

    [Fact]
    public void ABareHeroWithoutSkillDataHasNoBonusAndNoTrouble()
    {
        HeroProgress hero = LearningFixture.Bare(20);
        hero.PassiveRegistry.Register(Atk());
        Assert.False(hero.HasSkillData);
        Assert.Equal(default(StatBlock), hero.PassiveStatBlock);
        Assert.Equal(hero.BaseStats + hero.Worn.TotalStatBlock(), hero.Stats);
    }

    [Fact]
    public void TheVersionIsStableWhenNothingChangesBetweenReads()
    {
        HeroProgress hero = Hero();
        hero.PassiveRegistry.Register(Atk());
        hero.LearnSkill(921, 1, out _);
        int version = hero.Version;
        for (int i = 0; i < 3; i++)
        {
            _ = hero.Stats;
            _ = hero.PassiveStatBlock;
        }
        Assert.Equal(version, hero.Version);
    }
}
