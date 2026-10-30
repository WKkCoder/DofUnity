using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// 转职任务的奖励(M59,证据 E-153:服务端 <c>CUser::quest_reward</c> 0x0866ea92 的奖励类型 1 把 <c>[reward int data]</c> 的第一个整数当新的成长类型下标,
/// 走 <c>set_grow_type</c>;<c>UserQuest::isDoingJobChangeQuest</c> 0x086ad48a 把 <c>[job change quest]</c> 的 1 与 2 当转职任务)。
/// 数值、编号与名字全是合成的,只检验规则的形状,与原版数据无关。
/// </summary>
public class QuestJobChangeTests
{
    // ------------------------------------------------------------------ 工具

    private static ScriptLine[] Lines(TokenStreamBuilder b)
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return ScriptDocument.Parse(b.Bytes(), strings);
    }

    private static QuestDef Parse(TokenStreamBuilder b, int id = 1)
    {
        return QuestScript.Parse(Lines(b), id, "n_quest/q" + id + ".qst");
    }

    /// <summary>合成的转职任务:通关副本 7(任意难度),等级 15 起,NPC 5 发布、条件满足即完成,奖励是换成 <paramref name="growtype"/>,与 <paramref name="rivals"/> 互斥。</summary>
    private static QuestDef JobQuest(int id, int growtype, params int[] rivals)
    {
        var q = new QuestDef(id, "n_quest/q" + id + ".qst")
        {
            Grade = QuestGrade.CommonUnique,
            Kind = QuestKind.ConditionUnderClear,
            SubType = QuestRules.SubClearDungeon,
            IntData = new[] { 7, -1 },
            NpcId = 5,
            CompleteNpcId = -1,
            MinLevel = 15,
            MaxLevel = 99,
            JobChange = 1,
            RewardType = QuestDef.GrowtypeRewardType,
            DifficultyLetter = 'S',
        };
        q.Targets.Add(new QuestTarget(EquipRules.AllJobs, -1, -1));
        q.RewardData.Add(growtype);
        q.Collision.AddRange(rivals);
        return q;
    }

    private static QuestHero Hero(int level = 15, int job = 0, int grow = 0) => new QuestHero(level, job, grow);

    /// <summary>合成的奖励参数:经验表 下标 × 100,金币表 下标 × 10,权重 S 110,绿色惩罚 80,灰色惩罚 30。</summary>
    private static QuestParameters Params()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[difficulty]").Str("S").Int(110).Tag("[/difficulty]");
        b.Tag("[green level penalty]").Int(80);
        b.Tag("[grey level penalty]").Int(30);
        b.Tag("[exp reward table]");
        for (int i = 0; i < 100; i++)
        {
            b.Int(i * 100);
        }
        b.Tag("[gold reward table]");
        for (int i = 0; i < 100; i++)
        {
            b.Int(i * 10);
        }
        b.Tag("[quest parameter end]");
        return QuestParameters.Parse(Lines(b));
    }

    private static Func<int, QuestDef> Finder(params QuestDef[] defs)
    {
        var map = defs.ToDictionary(d => d.Id);
        return id => map.TryGetValue(id, out QuestDef d) ? d : null;
    }

    // ------------------------------------------------------------------ 读脚本

    [Fact]
    public void AGrowtypeRewardKeepsItsIntegerAndGivesNoItems()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[job change quest]").Int(1);
        b.Tag("[reward type]").Str("[grow type]");
        b.Tag("[reward int data]").Int(3).Tag("[/reward int data]");
        b.Tag("[reward selection int data]").Tag("[/reward selection int data]");
        QuestDef q = Parse(b);
        Assert.True(q.IsGrowtypeReward);
        Assert.Equal(1, q.JobChange);
        Assert.Equal(new[] { 3 }, q.RewardData.ToArray());
        Assert.Empty(q.Rewards);                         // 单个整数不是"编号 数量"
        Assert.Empty(q.SelectRewards);
        Assert.Equal(3, QuestRules.GrowtypeOf(q));
    }

    [Fact]
    public void RewardIntegersKeepEveryNumberInScriptOrder()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[reward int data]").Ints(0, 500, 700).Str("[job]").Ints(9, -1, 1).Tag("[/reward int data]");
        QuestDef q = Parse(b);
        Assert.Equal(new[] { 0, 500, 700, 9, -1, 1 }, q.RewardData.ToArray());
        Assert.Equal(2, q.Rewards.Count);                // 物品奖励的解析不受影响
        Assert.False(q.IsGrowtypeReward);
    }

    [Fact]
    public void ARewardWithoutADataBlockHasNoIntegers()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[reward type]").Str("[grow type]");
        QuestDef q = Parse(b);
        Assert.True(q.IsGrowtypeReward);
        Assert.Empty(q.RewardData);
        Assert.Equal(-1, QuestRules.GrowtypeOf(q));
    }

    [Fact]
    public void TheRewardKeywordIgnoresCase()
    {
        var q = new QuestDef(1, "n_quest/q1.qst") { RewardType = "[GROW TYPE]" };
        q.RewardData.Add(2);
        Assert.True(q.IsGrowtypeReward);
        Assert.Equal(2, QuestRules.GrowtypeOf(q));
        Assert.False(new QuestDef(2, "n_quest/q2.qst").IsGrowtypeReward);          // 默认是 [item]
    }

    // ------------------------------------------------------------------ 新成长类型的合法范围

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(0, -1)]                                  // 0 是转职前,不是转出去的目标
    [InlineData(6, -1)]                                  // 只有 0 到 5 六个成长块
    [InlineData(-1, -1)]
    [InlineData(100, -1)]
    public void OnlyGrowtypesOneToFiveAreValidTargets(int value, int expected)
    {
        QuestDef q = JobQuest(1, value);
        Assert.Equal(expected, QuestRules.GrowtypeOf(q));
    }

    [Fact]
    public void OnlyTheFirstIntegerIsTheNewGrowtype()
    {
        QuestDef q = JobQuest(1, 2);
        q.RewardData.Add(4);
        Assert.Equal(2, QuestRules.GrowtypeOf(q));
    }

    [Fact]
    public void ItemQuestsAndMissingQuestsHaveNoGrowtype()
    {
        var q = new QuestDef(1, "n_quest/q1.qst");
        q.RewardData.Add(2);
        Assert.Equal(-1, QuestRules.GrowtypeOf(q));      // 奖励种类是 [item]:整数是物品数据
        Assert.Equal(-1, QuestRules.GrowtypeOf(null));
    }

    // ------------------------------------------------------------------ 支持范围

    [Fact]
    public void AJobChangeQuestWithAGrowtypeRewardIsSupported()
    {
        Assert.Null(QuestRules.UnsupportedReason(JobQuest(1, 1)));
        Assert.True(QuestRules.IsSupported(JobQuest(2, 4)));
    }

    [Fact]
    public void TheJobChangeFlagIsNotWhatMakesTheRewardWork()
    {
        QuestDef q = JobQuest(1, 2);
        q.JobChange = 0;
        Assert.Null(QuestRules.UnsupportedReason(q));
        Assert.Equal(2, QuestRules.GrowtypeOf(q));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-3)]
    public void AGrowtypeRewardOutsideTheTableIsUnsupported(int value)
    {
        Assert.Equal("reward-shape", QuestRules.UnsupportedReason(JobQuest(1, value)));
    }

    [Fact]
    public void AGrowtypeRewardWithoutIntegersIsUnsupported()
    {
        QuestDef q = JobQuest(1, 2);
        q.RewardData.Clear();
        Assert.Equal("reward-shape", QuestRules.UnsupportedReason(q));
    }

    [Fact]
    public void AJobChangeQuestWithAnotherRewardTypeStaysUnsupported()
    {
        QuestDef q = JobQuest(1, 2);
        q.RewardType = "[item]";
        Assert.Equal("job-change", QuestRules.UnsupportedReason(q));
        q.RewardType = "[title]";
        Assert.Equal("job-change", QuestRules.UnsupportedReason(q));
        q.RewardType = "[awakening type]";
        Assert.Equal("job-change", QuestRules.UnsupportedReason(q));
    }

    [Fact]
    public void OtherNonItemRewardTypesStayUnsupportedEvenWithIntegers()
    {
        QuestDef q = JobQuest(1, 2);
        q.JobChange = 0;
        q.RewardType = "[awakening type]";               // 觉醒(第二成长类型)没做
        Assert.Equal("reward-type", QuestRules.UnsupportedReason(q));
        q.RewardType = "[title]";
        Assert.Equal("reward-type", QuestRules.UnsupportedReason(q));
    }

    [Fact]
    public void TheOtherJobChangeFlagsAreNotTouched()
    {
        // 标志 2、10、20 的任务原来怎样现在还怎样(没有换成长类型的奖励,按奖励种类判断)
        foreach (int flag in new[] { 2, 10, 20 })
        {
            QuestDef q = JobQuest(1, 2);
            q.JobChange = flag;
            q.RewardType = "[item]";
            Assert.Null(QuestRules.UnsupportedReason(q));
        }
    }

    // ------------------------------------------------------------------ 奖励

    [Fact]
    public void TheRewardCarriesTheNewGrowtypeAndTheUsualExp()
    {
        QuestReward reward = QuestRules.ComputeReward(JobQuest(1, 3), Hero(), Params(), -1);
        Assert.Equal(3, reward.Growtype);
        Assert.Equal(1650, reward.Exp);                  // 经验表[15] 1500 × 权重 110 / 100,等级差 0 不扣
        Assert.Equal(0, reward.Gold);                    // 不是物品类奖励:没有金币标记
        Assert.Empty(reward.Items);
    }

    [Fact]
    public void ItemQuestsRewardNoGrowtype()
    {
        QuestDef q = JobQuest(1, 3);
        q.RewardType = "[item]";
        q.Rewards.Add(new QuestItem(500, 2));
        QuestReward reward = QuestRules.ComputeReward(q, Hero(), Params(), -1);
        Assert.Equal(-1, reward.Growtype);
        Assert.Equal(500, Assert.Single(reward.Items).Id);
    }

    [Fact]
    public void AnInvalidGrowtypeRewardComputesNoGrowtype()
    {
        QuestReward reward = QuestRules.ComputeReward(JobQuest(1, 9), Hero(), Params(), -1);
        Assert.Equal(-1, reward.Growtype);
    }

    [Fact]
    public void ANewRewardStartsWithoutAGrowtype()
    {
        Assert.Equal(-1, new QuestReward().Growtype);
    }

    [Fact]
    public void SelectionRewardsStillComeAlongWithTheGrowtype()
    {
        QuestDef q = JobQuest(1, 2);
        q.SelectRewards.Add(new QuestItem(600, 1));
        QuestReward reward = QuestRules.ComputeReward(q, Hero(), Params(), 0);
        Assert.Equal(2, reward.Growtype);
        Assert.Equal(600, Assert.Single(reward.Items).Id);
    }

    // ------------------------------------------------------------------ 生命周期与互斥

    [Fact]
    public void ALevelFifteenHeroOfAnyGrowtypeCanTakeTheQuest()
    {
        var log = new QuestLog();
        QuestDef q = JobQuest(1, 1);
        Assert.Equal(QuestOffer.Level, QuestRules.CheckOffer(q, Hero(14), log));
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(q, Hero(15), log));
        // 脚本写的是 [grow type] -1:任何转职方向都对得上(服务端 check_possible 就是这样,挡住重复转职的是互斥与"完成过"两条)
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(q, Hero(15, 0, 3), log));
    }

    [Fact]
    public void ClearingTheDungeonFinishesTheQuestAndTheRewardSaysWhereToGo()
    {
        QuestDef a = JobQuest(1, 1, 2, 3);
        QuestDef b = JobQuest(2, 2, 1, 3);
        var log = new QuestLog();
        Func<int, QuestDef> find = Finder(a, b);
        ActiveQuest active = QuestRules.Accept(a, Hero(), log, out QuestOffer why);
        Assert.Equal(QuestOffer.Ok, why);
        Assert.False(QuestRules.IsReady(a, active, _ => 0));
        QuestTracker.OnDungeonClear(log, find, 7, 2, 1);
        Assert.True(QuestRules.IsReady(a, active, _ => 0));
        Assert.Equal(1, QuestRules.ComputeReward(a, Hero(), Params(), -1).Growtype);
        Assert.True(QuestRules.Complete(a, log));
    }

    [Fact]
    public void FinishingOneOfTheQuestsBlocksItsRivalsAndItself()
    {
        QuestDef a = JobQuest(1, 1, 2, 3);
        QuestDef b = JobQuest(2, 2, 1, 3);
        QuestDef c = JobQuest(3, 3, 1, 2);
        var log = new QuestLog();
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(b, Hero(), log));
        log.MarkCleared(1);
        Assert.Equal(QuestOffer.Collision, QuestRules.CheckOffer(b, Hero(), log));
        Assert.Equal(QuestOffer.Collision, QuestRules.CheckOffer(c, Hero(), log));
        Assert.Equal(QuestOffer.Cleared, QuestRules.CheckOffer(a, Hero(15, 0, 1), log));
    }

    [Fact]
    public void ARivalThatIsOnlyInProgressDoesNotBlock()
    {
        // 服务端的互斥只看"完成过":两个转职任务可以同时进行,先完成的那个决定转去哪
        QuestDef a = JobQuest(1, 1, 2);
        QuestDef b = JobQuest(2, 2, 1);
        var log = new QuestLog();
        Assert.NotNull(QuestRules.Accept(a, Hero(), log, out _));
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(b, Hero(), log));
    }
}
