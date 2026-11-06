using Dnf.Data;
using Dnf.Game;
using static Dnf.Play.Tests.LearningFixture;

namespace Dnf.Play.Tests;

/// <summary>
/// 靠指令放与快捷栏占用的次序(<see cref="SkillLearning"/> 的 <c>ReachOf</c>、<c>CommandAware</c>、<c>ArrangeBar</c>;本项目自己的设计,E-140 的指令层是判定的真源)。
/// 夹具见 <see cref="LearningFixture.BarSkills"/>:901 到 906 靠指令放得出来,907 到 913 只能靠快捷键,914 被动;主角 20 级(210 点,每级 1 点,够学满)。
/// </summary>
public class SkillLearningBarTests
{
    private static (HeroProgress Hero, SkillLearning Service) Setup(bool aware, Func<int, bool> implemented = null, ScriptLine[] chr = null, int level = 20)
    {
        HeroProgress hero = Hero(BarSkills(), level, chr);
        var service = new SkillLearning(hero) { CommandAware = aware, Implemented = implemented ?? (_ => true) };
        return (hero, service);
    }

    private static void LearnOne(SkillLearning service, params int[] skillIds)
    {
        foreach (int id in skillIds)
        {
            Assert.Equal(SkillOpResult.Ok, service.Learn(id, 1, out _));
        }
    }

    // ------------------------------------------------------------------ 靠什么放

    [Fact]
    public void ReachTellsWhichRouteCanCastASkill()
    {
        (HeroProgress hero, SkillLearning service) = Setup(aware: false);
        Assert.Equal(SkillReach.NotLearned, service.ReachOf(901));                         // 认识但没学
        Assert.Equal(SkillReach.NotLearned, service.ReachOf(999));                         // 不认识
        LearnOne(service, 901, 902, 903, 904, 905, 906, 907, 908, 909, 910, 911, 912, 913, 914);
        Assert.Equal("901,902,903,904,905,906", Bar(hero));                                // 先到先占:前六个上栏
        Assert.Equal(SkillReach.OnBar, service.ReachOf(901));
        Assert.Equal(SkillReach.Passive, service.ReachOf(914));
        for (int slot = 0; slot < SkillBook.QuickSlots; slot++)
        {
            Assert.True(hero.Skills.ClearQuick(slot));                                      // 全部拿下栏:剩下看指令放不放得出来
        }
        // 能由指令层放:技能键 / 下 + 技能 / 增益键 / 下下攻击(攻击收尾的多键) / 左 + 跳跃(跳跃收尾的多键) / 上 + 增益
        foreach (int id in new[] { 901, 902, 903, 904, 905, 906 })
        {
            Assert.Equal(SkillReach.CommandOnly, service.ReachOf(id));
        }
        // 放不出:没有指令(907、910、913)、单个跳跃(908、911)与单个攻击(909、912)留给普通跳跃与普攻
        foreach (int id in new[] { 907, 908, 909, 910, 911, 912, 913 })
        {
            Assert.Equal(SkillReach.Unreachable, service.ReachOf(id));
        }
        Assert.Equal(7, service.Stranded());
        Assert.True(hero.Skills.AssignQuick(0, 901));
        Assert.Equal(SkillReach.OnBar, service.ReachOf(901));
    }

    [Fact]
    public void ASkillWhoseCastingIsNotImplementedIsNeverReachableOrStranded()
    {
        // 默认的"放法实现了没有"是 HeroSkills.IsImplemented(默认配方表):合成技能一个也没实现
        HeroProgress hero = Hero(BarSkills(), 20);
        var service = new SkillLearning(hero) { CommandAware = true };
        Assert.False(service.Implemented(901));
        LearnOne(service, 901, 902, 903, 904, 905, 906, 907);
        Assert.Equal("901,902,903,904,905,906", Bar(hero));                                // 没有只能靠快捷键的"已实现"技能,所以没人让位
        Assert.Equal(0, service.Stranded());
        Assert.Equal(SkillReach.Unreachable, service.ReachOf(907));                        // 不在栏里,指令也放不出东西
        hero.Skills.ClearQuick(0);
        Assert.Equal(SkillReach.Unreachable, service.ReachOf(901));                        // 指令写法对,但放法没实现
        service.Implemented = _ => true;
        Assert.Equal(SkillReach.CommandOnly, service.ReachOf(901));
        service.Implemented = null;                                                        // 设成 null 回到默认
        Assert.False(service.Implemented(901));
    }

    // ------------------------------------------------------------------ 占用次序

    [Fact]
    public void WithoutTheSwitchTheBarIsFirstComeFirstServed()
    {
        (HeroProgress hero, SkillLearning service) = Setup(aware: false);
        LearnOne(service, 901, 902, 903, 904, 905, 906, 907);
        Assert.Equal("901,902,903,904,905,906", Bar(hero));                                // 第七个只能靠快捷键的技能既不上栏
        Assert.Equal(SkillReach.Unreachable, service.ReachOf(907));                        // 也放不出来
        Assert.Equal(1, service.Stranded());
        // 开关只管"自动调不调":手动整理照样可用
        Assert.Equal(1, service.ArrangeBar());
        Assert.Equal("901,902,903,904,905,907", Bar(hero));
        Assert.Equal(0, service.Stranded());
    }

    [Fact]
    public void ABarOnlySkillTakesTheRightmostSlotOfASkillThatAlsoCastsByCommand()
    {
        (HeroProgress hero, SkillLearning service) = Setup(aware: true);
        LearnOne(service, 901, 902, 903, 904, 905, 906);
        Assert.Equal("901,902,903,904,905,906", Bar(hero));
        LearnOne(service, 907);
        Assert.Equal("901,902,903,904,905,907", Bar(hero));                                // 906 靠指令放得出来,让出最靠右的一格
        Assert.Equal(SkillReach.OnBar, service.ReachOf(907));
        Assert.Equal(SkillReach.CommandOnly, service.ReachOf(906));                        // 被顶掉的仍然学着,改靠指令放
        Assert.Equal(1, hero.Skills.LevelOf(906));
        Assert.Equal(0, service.Stranded());
    }

    [Fact]
    public void AnEmptySlotIsUsedBeforeAnyoneIsAskedToGiveWay()
    {
        (HeroProgress hero, SkillLearning service) = Setup(aware: false);
        LearnOne(service, 901, 902, 903, 904, 905, 906, 907);                              // 栏满,907 放不下
        Assert.True(hero.Skills.ClearQuick(2));                                            // 玩家把第 2 格(903)清空
        Assert.Equal("901,902,0,904,905,906", Bar(hero));
        Assert.Equal(1, service.ArrangeBar());
        Assert.Equal("901,902,907,904,905,906", Bar(hero));                                // 907 占空格,906 不用让
        Assert.Equal(SkillReach.OnBar, service.ReachOf(906));
        Assert.Equal(SkillReach.CommandOnly, service.ReachOf(903));                        // 903 是玩家自己清下去的,没人动它,仍然靠指令放
    }

    [Fact]
    public void SkillsWhoseCastingIsNotImplementedGiveWayBeforeThoseThatCastByCommand()
    {
        (HeroProgress hero, SkillLearning service) = Setup(aware: true, implemented: id => id != 903);
        LearnOne(service, 901, 902, 903, 904, 905, 906);
        LearnOne(service, 907);
        Assert.Equal("901,902,907,904,905,906", Bar(hero));                                // 903 占着也放不出东西,最先让位(哪怕 906 更靠右)
        Assert.Equal(SkillReach.Unreachable, service.ReachOf(903));
        Assert.Equal(0, service.Stranded());                                               // 没实现的不算"够不着"
        LearnOne(service, 908);
        Assert.Equal("901,902,907,904,905,908", Bar(hero));                                // 没有没实现的占格了,这回顶掉靠指令放的最靠右一个
    }

    [Fact]
    public void SeveralStrandedSkillsTakeTheYieldingSlotsLeftToRightInIdOrder()
    {
        (HeroProgress hero, SkillLearning service) = Setup(aware: false);
        LearnOne(service, 901, 902, 903, 904, 905, 906, 907, 908, 909);
        Assert.Equal("901,902,903,904,905,906", Bar(hero));
        Assert.Equal(3, service.Stranded());
        Assert.Equal(3, service.ArrangeBar());
        // 要三格:靠指令放的里面最靠右的三个(下标 3、4、5),从左到右依次给 907、908、909
        Assert.Equal("901,902,903,907,908,909", Bar(hero));
        foreach (int id in new[] { 904, 905, 906 })
        {
            Assert.Equal(SkillReach.CommandOnly, service.ReachOf(id));
        }
        Assert.Equal(0, service.ArrangeBar());                                             // 再整理一次不再动
    }

    [Fact]
    public void WhenSixBarOnlySkillsHoldTheBarTheRestStayOut()
    {
        (HeroProgress hero, SkillLearning service) = Setup(aware: true);
        LearnOne(service, 907, 908, 909, 910, 911, 912);
        Assert.Equal("907,908,909,910,911,912", Bar(hero));
        LearnOne(service, 913);                                                            // 六格全是只能靠快捷键的:没有可让的
        Assert.Equal("907,908,909,910,911,912", Bar(hero));
        Assert.Equal(SkillReach.Unreachable, service.ReachOf(913));
        Assert.Equal(1, service.Stranded());
        Assert.Equal(0, service.ArrangeBar());
    }

    [Fact]
    public void AFreedSlotGoesToAStrandedSkillOnlyWhenTheSwitchIsOn()
    {
        foreach (bool aware in new[] { false, true })
        {
            (HeroProgress hero, SkillLearning service) = Setup(aware);
            LearnOne(service, 907, 908, 909, 910, 911, 912, 913);                          // 913 放不下
            Assert.Equal(1, service.Stranded());
            Assert.Equal(SkillOpResult.Ok, service.Forget(907, 1, out int back));          // 907 退光,腾出第 0 格
            Assert.Equal(1, back);
            if (aware)
            {
                Assert.Equal("913,908,909,910,911,912", Bar(hero));                        // 补给够不着的 913
                Assert.Equal(0, service.Stranded());
            }
            else
            {
                Assert.Equal("0,908,909,910,911,912", Bar(hero));                          // 旧行为:空着
                Assert.Equal(1, service.Stranded());
            }
        }
    }

    [Fact]
    public void ArrangingLeavesAHandMadeBarAloneWhenNothingIsStranded()
    {
        (HeroProgress hero, SkillLearning service) = Setup(aware: true);
        LearnOne(service, 901, 902, 903);
        Assert.True(hero.Skills.AssignQuick(4, 903));                                      // 玩家把 903 挪到第 4 格
        Assert.Equal("901,902,0,0,903,0", Bar(hero));
        LearnOne(service, 907);                                                            // 新技能放进第一个空格(第 2 格),不动别的
        Assert.Equal("901,902,907,0,903,0", Bar(hero));
        int version = hero.SkillsVersion;
        Assert.Equal(0, service.ArrangeBar());
        Assert.Equal("901,902,907,0,903,0", Bar(hero));
        Assert.Equal(version, hero.SkillsVersion);                                         // 什么也没动,不记变化
    }

    [Fact]
    public void ResettingRearrangesTheGivenSkillsToo()
    {
        // 白送七个:901 到 906(靠指令放得出来)加 907(只能靠快捷键)。技能簿按白送次序先到先占,907 排第七放不下
        ScriptLine[] chr = { Ints("[initial value]"), Ints("[skill]", 901, 1, 902, 1, 903, 1, 904, 1, 905, 1, 906, 1, 907, 1) };
        (HeroProgress hero, SkillLearning service) = Setup(aware: true, chr: chr);
        Assert.Equal("901,902,903,904,905,906", Bar(hero));
        Assert.Equal(1, service.Stranded());
        LearnOne(service, 908);                                                            // 学 908 之后整理:907 和 908 都要格,顶掉靠指令放的最靠右两个(906、905),按号从左到右放
        Assert.Equal("901,902,903,904,907,908", Bar(hero));
        Assert.Equal(0, service.Stranded());
        service.Reset();                                                                   // 重置:清回白送的七个,快捷栏按白送次序重排(907 放不下),再整理一次
        Assert.Equal("901,902,903,904,905,907", Bar(hero));
        Assert.Equal(0, service.Stranded());
        Assert.Equal(0, hero.Skills.LevelOf(908));
        Assert.Equal(210, hero.SkillPoints);                                               // 20 级总额 1 + … + 20
    }

    [Fact]
    public void ABatchArrangesTheBarOnlyWhenItCommits()
    {
        (HeroProgress hero, SkillLearning service) = Setup(aware: true);
        LearnOne(service, 901, 902, 903, 904, 905, 906);
        int version = hero.SkillsVersion;
        SkillBatchResult tried = service.TryLearnAll(new[] { new SkillStep(907, 1) });
        Assert.True(tried.Ok);
        Assert.Equal("901,902,903,904,905,906", Bar(hero));                                // 演算不整理
        Assert.Equal(version, hero.SkillsVersion);
        SkillBatchResult done = service.LearnAll(new[] { new SkillStep(907, 1) });
        Assert.True(done.Ok);
        Assert.Equal("901,902,903,904,905,907", Bar(hero));
        Assert.Equal(0, service.Stranded());
    }
}
