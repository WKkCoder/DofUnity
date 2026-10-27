using Dnf.Game;

namespace Dnf.Play.Tests;

/// <summary>自动机的技能加点策略(M35,<see cref="SkillPlanner"/>;本项目自己的占位策略):次序、花光、守恒、可重复、前置技能、觉醒点、没有数据。技能与点数全是合成的(见 <see cref="SkillWindowTests"/>)。</summary>
public class SkillPlannerTests
{
    [Fact]
    public void TheOrderPutsActiveSkillsFirstThenByRequiredLevelAndId()
    {
        // 成长类型 0 能学的:1、2、3、5 号与 6 到 14 号(4 号只有成长类型 1 学得了);2 号是唯一的被动技能,排在最后
        List<SkillDef> order = SkillPlanner.Order(SkillWindowTests.Catalog(), 0);
        Assert.Equal(new[] { 1, 6, 7, 8, 9, 10, 11, 12, 13, 14, 3, 5, 2 }.OrderBy(i => i).ToArray(), order.Select(d => d.Id).OrderBy(i => i).ToArray());
        Assert.Equal(2, order[order.Count - 1].Id);
        Assert.All(order.Take(order.Count - 1), d => Assert.True(d.IsActive));
        // 主动技能之间按要求等级、再按编号:1 与 6 号(1 级)、7 号(2 级)、8 号(3 级)……3 号(5 级)与 10 号(5 级)编号小的在前
        int[] actives = order.Take(order.Count - 1).Select(d => d.Id).ToArray();
        Assert.Equal(new[] { 1, 6, 7, 8, 9, 3, 10, 11, 12, 13, 14, 5 }, actives);
        Assert.Contains(4, SkillPlanner.Order(SkillWindowTests.Catalog(), 1).Select(d => d.Id));   // 成长类型 1 学得了 4 号
        Assert.Throws<ArgumentNullException>(() => SkillPlanner.Order(null, 0));
    }

    [Fact]
    public void SpendAllUsesUpThePointsAndConservesThem()
    {
        HeroProgress hero = SkillWindowTests.Hero(level: 20);
        int sp = hero.SkillPoints;
        int levels = SkillPlanner.SpendAll(hero);
        Assert.True(levels > 0);
        // 花掉的点数 = 每个技能比白送等级多出来的级数的花费之和
        long spent = 0;
        foreach (SkillDef def in hero.Skills.Catalog.Skills)
        {
            int cost = SkillRules.NeedPoints(def, hero.Skills.GivenLevelOf(def.Id), hero.Skills.LevelOf(def.Id));
            spent += Math.Max(0, cost);
        }
        Assert.Equal(sp - hero.SkillPoints, spent);
        // 停下时每个技能都再学不了一级
        foreach (SkillDef def in hero.Skills.Catalog.ForGrowtype(hero.Growtype))
        {
            Assert.NotEqual(SkillOpResult.Ok, hero.Skills.CheckLearn(def.Id, hero.Level, hero.Growtype, 1, out _));
        }
        Assert.Equal(0, SkillPlanner.SpendAll(hero));                                // 再跑一遍不再加
    }

    [Fact]
    public void PrerequisitesAreMetOverTheRoundsAndNewActiveSkillsFillTheQuickBar()
    {
        HeroProgress hero = SkillWindowTests.Hero(level: 20);
        SkillPlanner.SpendAll(hero);
        Assert.True(hero.Skills.LevelOf(3) >= 1);                                    // 3 号要求 1 号 2 级加 2 号 1 级
        Assert.True(hero.Skills.LevelOf(1) >= 2);
        Assert.True(hero.Skills.LevelOf(2) >= 1);
        int filled = 0;
        for (int i = 0; i < SkillBook.QuickSlots; i++)
        {
            if (hero.Skills.QuickAt(i) > 0)
            {
                filled++;
                Assert.True(hero.Skills.Catalog.Find(hero.Skills.QuickAt(i)).IsActive);
            }
        }
        Assert.Equal(SkillBook.QuickSlots, filled);                                  // 学会的主动技能多于六个,快捷栏排满
        Assert.Equal(-1, hero.Skills.QuickSlotOf(2));
    }

    [Fact]
    public void AwakeningPointsGoToSpecialSkillsAndAHeroWithoutDataLearnsNothing()
    {
        HeroProgress hero = SkillWindowTests.Hero(level: 55);
        Assert.Equal(6, hero.AwakeningPoints);
        SkillPlanner.SpendAll(hero);
        Assert.Equal(3, hero.Skills.LevelOf(5));                                     // 5 号是觉醒技能:最高 3 级,每级 1 点
        Assert.Equal(3, hero.AwakeningPoints);                                       // 6 - 3
        Assert.Equal(0, SkillPlanner.SpendAll(ExpTests.Progress(level: 10)));        // 没有技能数据
        Assert.Throws<ArgumentNullException>(() => SkillPlanner.SpendAll(null));
    }
}
