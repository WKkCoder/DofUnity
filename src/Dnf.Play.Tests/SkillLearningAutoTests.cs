using Dnf.Game;
using static Dnf.Play.Tests.LearningFixture;

namespace Dnf.Play.Tests;

/// <summary>
/// 自动学(<see cref="SkillLearning.AutoLearn"/> 与 <see cref="SkillLearning.Update"/>):升级之后按 <see cref="SkillPlanner"/> 的策略把点花光(本项目自己的自动机策略,不是原版行为)。
/// 学习树里计划器的一轮次序是 901、903、905、902(主动在前,按要求等级再按编号;904 只有成长类型 1 学得了,不在成长类型 0 的名单里)。
/// </summary>
public class SkillLearningAutoTests
{
    private static HeroProgress NewHero() => Hero(Tree(), 1, TreeChr());                   // 1 级:1 点,901 白送一级

    [Fact]
    public void NothingIsLearnedWhileAutoLearnIsOff()
    {
        HeroProgress hero = NewHero();
        var service = new SkillLearning(hero);
        Assert.False(service.AutoLearn);
        hero.Gain(2000);                                                                   // 升到 6 级(1500 ≤ 2000 < 2100),送 2 + 3 + 4 + 5 + 6 = 20 点
        Assert.Equal(6, hero.Level);
        Assert.Equal(21, hero.SkillPoints);
        Assert.Equal(0, service.Update());
        Assert.Equal("sp=21 sfp=0 levels=901:1, bar=901,0,0,0,0,0,", State(hero));
    }

    [Fact]
    public void ALevelUpSpendsTheNewPointsInThePlannersOrderAndFillsTheBar()
    {
        HeroProgress hero = NewHero();
        var service = new SkillLearning(hero) { AutoLearn = true };
        Assert.Equal(0, service.Update());                                                 // 还没升级
        hero.Gain(2000);                                                                   // 6 级,21 点
        // 6 级主角、21 点,每轮按 901、903、905、902:
        //   第 1 轮 901→2(3 点)、903 缺前置 902、905 要 10 级、902→1(4 点);
        //   第 2 轮 901→3(3 点)、903→1(5 点)、902→2(4 点,第 2 级要 6 级);
        //   第 3 轮 901→4 要 7 级、903→2 差 3 点(余 2 点不够 5 点)、902→3 要 9 级:停。共 5 级、19 点。
        Assert.Equal(5, service.Update());
        Assert.Equal("sp=2 sfp=0 levels=901:3,902:2,903:1, bar=901,903,0,0,0,0,", State(hero));
        Assert.Equal(0, service.Update());                                                 // 升级只对账一次:没有新的升级就不再学
        hero.Skills.AddPoints(100, 0);                                                     // 之后只加点不升级:点数够学 903 第 2 级等等,但不该学
        Assert.Equal(0, service.Update());
        Assert.Equal(102, hero.SkillPoints);
    }

    [Fact]
    public void SettingItOnAgainDoesNotForgetALevelUpThatIsStillPending()
    {
        HeroProgress hero = NewHero();
        var service = new SkillLearning(hero) { AutoLearn = true };
        hero.Gain(2000);
        service.AutoLearn = true;                                                          // 已经开着,再设一次开不重新对账
        Assert.Equal(5, service.Update());
    }

    [Fact]
    public void PointsWithoutALevelUpAreLeftAloneUntilTheNextLevelUp()
    {
        HeroProgress hero = NewHero();
        var service = new SkillLearning(hero) { AutoLearn = true };
        hero.Skills.AddPoints(50, 0);                                                      // 点数多了,但没升级(比如任务奖励)
        Assert.Equal(0, service.Update());
        Assert.Equal(51, hero.SkillPoints);
        Assert.Equal("sp=51 sfp=0 levels=901:1, bar=901,0,0,0,0,0,", State(hero));
        hero.Gain(650);                                                                    // 4 级(600 ≤ 650 < 1000),再送 2 + 3 + 4 = 9 点,共 60 点
        Assert.Equal(4, hero.Level);
        // 4 级主角:第 1 轮 901→2(3 点,第 2 级要 3 级)、902→1(4 点);第 2 轮 901→3 要 5 级、903 要 5 级、902→2 要 6 级:停。手里的点多,但被等级卡住,只学 2 级
        Assert.Equal(2, service.Update());
        Assert.Equal("sp=53 sfp=0 levels=901:2,902:1, bar=901,0,0,0,0,0,", State(hero));
        int version = hero.SkillsVersion;
        Assert.Equal(0, service.Update());
        Assert.Equal(version, hero.SkillsVersion);                                         // 没有新升级:对技能簿一下都没碰
    }

    [Fact]
    public void TurningItOnSpendsNothingByItselfAndDoesNotCountEarlierLevelUps()
    {
        HeroProgress hero = NewHero();
        var service = new SkillLearning(hero);
        hero.Gain(2000);                                                                   // 关着的时候升到 6 级
        service.AutoLearn = true;
        Assert.Equal(0, service.Update());                                                 // 打开不追溯
        Assert.Equal("sp=21 sfp=0 levels=901:1, bar=901,0,0,0,0,0,", State(hero));
        hero.Gain(150);                                                                    // 2150 ≥ 2100:7 级,再送 7 点,共 28 点
        Assert.Equal(7, hero.Level);
        // 7 级主角、28 点,每轮按 901、903、905、902:
        //   第 1 轮 901→2(3)、902→1(4)共 7 点;第 2 轮 901→3(3)、903→1(5)、902→2(4)共 12 点;
        //   第 3 轮 901→4(3,第 4 级要 7 级)、903→2(5)共 8 点;第 4 轮 901→5 要 9 级、903→3 差 4 点、902→3 要 9 级:停。
        //   共 7 级、27 点,余 1 点。
        Assert.Equal(7, service.Update());
        Assert.Equal("sp=1 sfp=0 levels=901:4,902:2,903:2, bar=901,903,0,0,0,0,", State(hero));
    }

    [Fact]
    public void TurningItOffStopsItAndTurningItBackOnStartsFromNow()
    {
        HeroProgress hero = NewHero();
        var service = new SkillLearning(hero) { AutoLearn = true };
        hero.Gain(2000);
        service.AutoLearn = false;
        Assert.Equal(0, service.Update());
        Assert.Equal(21, hero.SkillPoints);
        service.AutoLearn = true;                                                          // 关着时升的级不补算
        Assert.Equal(0, service.Update());
        Assert.Equal(21, hero.SkillPoints);
    }

    [Fact]
    public void AHeroWithoutSkillDataLearnsNothingOnLevelUp()
    {
        HeroProgress hero = Bare(1);
        Assert.False(hero.HasSkillData);
        var service = new SkillLearning(hero) { AutoLearn = true };
        hero.Gain(2000);
        Assert.Equal(0, service.Update());
    }

    [Fact]
    public void AutoLearningArrangesTheBarWhenTheServiceIsCommandAware()
    {
        // 1 级主角升到 6 级共 21 点;技能夹具每级 1 点:第 1 轮 13 个主动与 1 个被动各 1 级(14 点),第 2 轮 901 到 907 各第 2 级(7 点),共 21 级
        foreach (bool aware in new[] { false, true })
        {
            HeroProgress hero = Hero(BarSkills(), 1);
            var service = new SkillLearning(hero) { AutoLearn = true, CommandAware = aware, Implemented = _ => true };
            hero.Gain(2000);
            Assert.Equal(21, service.Update());
            Assert.Equal(0, hero.SkillPoints);
            if (aware)
            {
                // 默认先到先占:901 到 906 上栏,907 到 913 七个只能靠快捷键;整理:没有空格,六个靠指令放的都让位,907 到 912 上栏,913 放不下
                Assert.Equal("907,908,909,910,911,912", Bar(hero));
                Assert.Equal(1, service.Stranded());
                Assert.Equal(SkillReach.CommandOnly, service.ReachOf(901));
                Assert.Equal(SkillReach.Unreachable, service.ReachOf(913));
            }
            else
            {
                Assert.Equal("901,902,903,904,905,906", Bar(hero));
                Assert.Equal(7, service.Stranded());
            }
        }
    }
}

/// <summary>宿主入口(<see cref="SkillLearn"/>):结果是枚举名、批量是并行数组、状态是只有数的一行。</summary>
public class SkillLearnHostTests
{
    private static SkillLearning Service(HeroProgress hero) => new SkillLearning(hero);

    [Fact]
    public void ResultsComeBackAsTheNamesOfTheOperationResult()
    {
        HeroProgress hero = Hero(Tree(), 10, TreeChr());
        SkillLearning service = Service(hero);
        Assert.Equal("Ok", SkillLearn.OkName);
        Assert.Equal("Ok", SkillLearn.Learn(service, 901, 1));                             // 901 到 2 级:3 点
        Assert.Equal(52, hero.SkillPoints);
        Assert.Equal("NotAllowed", SkillLearn.Learn(service, 903, 1));                     // 前置 902 没学
        Assert.Equal("Unknown", SkillLearn.Learn(service, 999, 1));
        Assert.Equal("Unknown", SkillLearn.Learn(service, 901, 0));
        Assert.Equal("NotLearned", SkillLearn.Forget(service, 902, 1));
        Assert.Equal("BelowGiven", SkillLearn.Forget(service, 901, 2));                    // 退到 0 级,低于白送的 1 级
        Assert.Equal("Ok", SkillLearn.Forget(service, 901, 1));
        Assert.Equal(55, hero.SkillPoints);
        hero.Skills.SetPoints(2, 0);
        Assert.Equal("NotEnoughPoints", SkillLearn.Learn(service, 901, 1));                // 要 3 点只有 2 点
        Assert.Throws<ArgumentNullException>(() => SkillLearn.Learn(null, 901, 1));
        Assert.Throws<ArgumentNullException>(() => SkillLearn.Forget(null, 901, 1));
    }

    [Fact]
    public void BatchesTakeParallelArraysAndReportTheFirstFailedStep()
    {
        HeroProgress hero = Hero(Tree(), 10, TreeChr());
        SkillLearning service = Service(hero);
        Assert.Equal("Ok", SkillLearn.LearnAll(service, new[] { 901, 902, 903 }, new[] { 1, 1, 1 }, out int failed));
        Assert.Equal(-1, failed);
        Assert.Equal(1, hero.Skills.LevelOf(903));
        Assert.Equal("NeededByOther", SkillLearn.ForgetAll(service, new[] { 902, 903 }, new[] { 1, 1 }, out failed));   // 903 还要 902
        Assert.Equal(0, failed);
        Assert.Equal(1, hero.Skills.LevelOf(902));                                         // 一步都没生效
        Assert.Equal("Ok", SkillLearn.ForgetAll(service, new[] { 903, 902 }, new[] { 1, 1 }, out failed));
        Assert.Equal(-1, failed);
        Assert.Equal(0, hero.Skills.LevelOf(903));
        Assert.Equal("NotLearned", SkillLearn.ForgetAll(service, new[] { 901, 902 }, new[] { 1, 1 }, out failed));      // 901 退一级后还剩 1 级;902 已经退光
        Assert.Equal(1, failed);
        Assert.Equal("Ok", SkillLearn.LearnAll(service, new int[0], new int[0], out failed));
        Assert.Equal(-1, failed);
    }

    [Fact]
    public void MalformedBatchArgumentsAreRejectedLoudly()
    {
        SkillLearning service = Service(Hero(Tree(), 10, TreeChr()));
        Assert.Throws<ArgumentException>(() => SkillLearn.LearnAll(service, new[] { 901, 902 }, new[] { 1 }, out _));
        Assert.Throws<ArgumentException>(() => SkillLearn.ForgetAll(service, new[] { 901 }, new[] { 1, 1 }, out _));
        Assert.Throws<ArgumentNullException>(() => SkillLearn.LearnAll(service, null, new[] { 1 }, out _));
        Assert.Throws<ArgumentNullException>(() => SkillLearn.ForgetAll(service, new[] { 901 }, null, out _));
        Assert.Throws<ArgumentNullException>(() => SkillLearn.LearnAll(null, new[] { 901 }, new[] { 1 }, out _));
    }

    [Fact]
    public void ReachAndStatusAreNamesAndCountsOnly()
    {
        HeroProgress hero = Hero(BarSkills(), 20);                                         // 210 点
        var service = new SkillLearning(hero) { Implemented = _ => true };
        Assert.Equal("NotLearned", SkillLearn.Reach(service, 901));
        for (int id = 901; id <= 907; id++)
        {
            Assert.Equal("Ok", SkillLearn.Learn(service, id, 1));
        }
        Assert.Equal("OnBar", SkillLearn.Reach(service, 901));
        Assert.Equal("Unreachable", SkillLearn.Reach(service, 907));                       // 第七个,只能靠快捷键,栏满放不下
        Assert.Equal("NotLearned", SkillLearn.Reach(service, 999));
        // 点数 210 − 7 = 203;学会 7 个;栏上 6 格;靠指令放 0 个;只能靠快捷键却没上栏 1 个;开关都关
        Assert.Equal("sp=203 sfp=0 learned=7 bar=6 command=0 stranded=1 aware=0 auto=0", SkillLearn.Status(service));
        service.CommandAware = true;
        service.AutoLearn = true;
        Assert.Equal(1, service.ArrangeBar());                                             // 906 让位给 907:906 改靠指令放
        Assert.Equal("sp=203 sfp=0 learned=7 bar=6 command=1 stranded=0 aware=1 auto=1", SkillLearn.Status(service));
        Assert.Equal("CommandOnly", SkillLearn.Reach(service, 906));
        Assert.Throws<ArgumentNullException>(() => SkillLearn.Status(null));
        Assert.Throws<ArgumentNullException>(() => SkillLearn.Reach(null, 901));
    }
}
