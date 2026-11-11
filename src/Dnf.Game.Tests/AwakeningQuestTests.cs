using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// 觉醒任务的规则(r15-awaken,服务端 <c>CUser::quest_reward</c> 0x0866ea92 的奖励类型 2:<c>[awakening type]</c>,把 <c>[reward int data]</c> 的第一个整数当新的第二成长类型,
/// 走 <c>set_grow_type</c>;任务脚本自己的 <c>[level]</c> 区间、<c>[pre required quest]</c> 链与 <c>[job]</c>/<c>[grow type]</c> 目标是仅有的门)。
/// 任务编号、数值与名字全是合成的(911 起),只检验规则的形状,与原版数据无关。
/// </summary>
public class AwakeningQuestTests
{
    // ------------------------------------------------------------------ 工具

    private static ScriptLine[] Lines(TokenStreamBuilder b)
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return ScriptDocument.Parse(b.Bytes(), strings);
    }

    private static QuestDef Parse(TokenStreamBuilder b, int id = 911)
    {
        return QuestScript.Parse(Lines(b), id, "n_quest/q" + id + ".qst");
    }

    /// <summary>
    /// 合成的觉醒任务链上的一环:成就类、标志 2、等级 50 到 99、过房间 30 就完成(没有交任务 NPC)、任何职业都能接;
    /// <paramref name="awakening"/> 大于 0 时奖励是觉醒到这个号(链尾),否则是普通物品奖励;<paramref name="pre"/> 大于 0 时要先完成它。
    /// </summary>
    private static QuestDef Link(int id, int awakening, int pre = 0, int minLevel = 50, int maxLevel = 99)
    {
        var q = new QuestDef(id, "n_quest/q" + id + ".qst")
        {
            Grade = QuestGrade.Achievement,
            Kind = QuestKind.ClearMap,
            IntData = new[] { 30 },
            NpcId = 905,
            CompleteNpcId = -1,
            MinLevel = minLevel,
            MaxLevel = maxLevel,
            JobChange = AwakeningRules.QuestFlag,
            RewardType = awakening > 0 ? QuestDef.AwakeningRewardType : "[item]",
            DifficultyLetter = 'S',
        };
        q.Targets.Add(new QuestTarget(EquipRules.AllJobs, -1, -1));
        if (awakening > 0)
        {
            q.RewardData.Add(awakening);
        }
        if (pre > 0)
        {
            q.PreRequired.Add(new[] { pre });
        }
        return q;
    }

    private static QuestHero Hero(int level = 50, int job = 0, int grow = 1) => new QuestHero(level, job, grow);

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

    // ------------------------------------------------------------------ 读脚本

    [Fact]
    public void AnAwakeningRewardKeepsItsIntegerAndGivesNoItems()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[grade]").Str("[achievement]");
        b.Tag("[job change quest]").Int(2);
        b.Tag("[level]").Int(50).Int(99);
        b.Tag("[reward type]").Str("[awakening type]");
        b.Tag("[reward int data]").Int(1).Tag("[/reward int data]");
        QuestDef q = Parse(b);
        Assert.Equal(QuestGrade.Achievement, q.Grade);
        Assert.True(q.IsAwakeningReward);
        Assert.False(q.IsGrowtypeReward);
        Assert.Equal(AwakeningRules.QuestFlag, q.JobChange);
        Assert.Equal(new[] { 1 }, q.RewardData.ToArray());
        Assert.Empty(q.Rewards);                                  // 单个整数不是"编号 数量"
        Assert.Equal(1, QuestRules.AwakeningOf(q));
        Assert.Equal(-1, QuestRules.GrowtypeOf(q));
        Assert.Equal((50, 99), (q.MinLevel, q.MaxLevel));
    }

    [Fact]
    public void TheAwakeningKeywordIgnoresCaseAndOtherRewardTypesAreNotAwakening()
    {
        var q = new QuestDef(911, "n_quest/q911.qst") { RewardType = "[AWAKENING TYPE]" };
        q.RewardData.Add(2);
        Assert.True(q.IsAwakeningReward);
        Assert.Equal(2, QuestRules.AwakeningOf(q));
        Assert.False(new QuestDef(912, "n_quest/q912.qst").IsAwakeningReward);                                  // 默认是 [item]
        Assert.False(new QuestDef(913, "n_quest/q913.qst") { RewardType = QuestDef.GrowtypeRewardType }.IsAwakeningReward);
        Assert.Equal("[awakening type]", QuestDef.AwakeningRewardType);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(0, 0)]                                            // 0 是没觉醒,不是觉醒的目标
    [InlineData(3, 0)]                                            // 没有三觉
    [InlineData(-1, 0)]
    [InlineData(100, 0)]
    public void OnlyAwakeningsOneAndTwoAreValidTargets(int value, int expected)
    {
        Assert.Equal(expected, QuestRules.AwakeningOf(Link(911, value)));
    }

    [Fact]
    public void OnlyTheFirstIntegerIsTheNewAwakeningAndOtherQuestsHaveNone()
    {
        QuestDef q = Link(911, 1);
        q.RewardData.Add(2);
        Assert.Equal(1, QuestRules.AwakeningOf(q));
        var item = new QuestDef(912, "n_quest/q912.qst");
        item.RewardData.Add(2);
        Assert.Equal(0, QuestRules.AwakeningOf(item));            // 奖励种类是 [item]:整数是物品数据
        Assert.Equal(0, QuestRules.AwakeningOf(null));
        var noData = new QuestDef(913, "n_quest/q913.qst") { RewardType = QuestDef.AwakeningRewardType };
        Assert.Equal(0, QuestRules.AwakeningOf(noData));          // 没有 [reward int data]
    }

    // ------------------------------------------------------------------ 支持范围

    [Fact]
    public void AnAwakeningQuestOfTheChainIsSupported()
    {
        Assert.Null(QuestRules.UnsupportedReason(Link(911, 1)));
        Assert.Null(QuestRules.UnsupportedReason(Link(912, 2, pre: 911)));
        Assert.Null(QuestRules.UnsupportedReason(Link(913, 0)));                // 链上没有觉醒奖励的中间环节
        Assert.True(QuestRules.IsSupported(Link(914, 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-2)]
    public void AnAwakeningRewardOutsideTheTableIsUnsupported(int value)
    {
        QuestDef q = Link(911, 1);
        q.RewardData.Clear();
        q.RewardData.Add(value);
        Assert.Equal("reward-shape", QuestRules.UnsupportedReason(q));
    }

    [Fact]
    public void AnAwakeningRewardWithoutIntegersIsUnsupported()
    {
        QuestDef q = Link(911, 1);
        q.RewardData.Clear();
        Assert.Equal("reward-shape", QuestRules.UnsupportedReason(q));
    }

    [Fact]
    public void ATransferQuestCannotCarryAnAwakeningReward()
    {
        QuestDef q = Link(911, 1);
        q.JobChange = 1;                                          // 标志 1 的奖励只能是换成长类型
        q.Grade = QuestGrade.CommonUnique;
        Assert.Equal("job-change", QuestRules.UnsupportedReason(q));
    }

    [Fact]
    public void AwakeningRewardsDoNotNeedTheChainFlagButOtherNonItemRewardsStayUnsupported()
    {
        QuestDef q = Link(911, 1);
        q.JobChange = 0;
        q.Grade = QuestGrade.CommonUnique;
        Assert.Null(QuestRules.UnsupportedReason(q));             // 奖励种类才决定是不是觉醒,标志不是
        Assert.Equal(1, QuestRules.AwakeningOf(q));
        q.RewardType = "[title]";
        Assert.Equal("reward-type", QuestRules.UnsupportedReason(q));
    }

    [Fact]
    public void OnlyTheAwakeningChainGetsThroughTheAchievementGrade()
    {
        Assert.True(QuestRules.IsAwakeningChain(Link(911, 1)));
        Assert.True(QuestRules.IsAwakeningChain(Link(912, 0)));
        QuestDef plain = Link(913, 0);
        plain.JobChange = 0;
        Assert.False(QuestRules.IsAwakeningChain(plain));                         // 普通成就类不放行
        Assert.Equal("grade", QuestRules.UnsupportedReason(plain));
        QuestDef transfer = Link(914, 0);
        transfer.JobChange = 1;
        Assert.False(QuestRules.IsAwakeningChain(transfer));
        QuestDef daily = Link(915, 0);
        daily.Grade = QuestGrade.Daily;
        Assert.False(QuestRules.IsAwakeningChain(daily));                         // 标志 2 的每日类也不放行:只放行成就类
        Assert.Equal("grade", QuestRules.UnsupportedReason(daily));
        Assert.False(QuestRules.IsAwakeningChain(null));
    }

    [Fact]
    public void TheOtherGradesAreStillOfferedAsBefore()
    {
        Assert.True(QuestRules.IsGradeOffered(QuestGrade.Epic));
        Assert.True(QuestRules.IsGradeOffered(QuestGrade.CommonUnique));
        Assert.True(QuestRules.IsGradeOffered(QuestGrade.NormalRepeat));
        Assert.False(QuestRules.IsGradeOffered(QuestGrade.Achievement));          // 放行的是觉醒任务链,不是整个分类
    }

    // ------------------------------------------------------------------ 奖励

    [Fact]
    public void TheRewardCarriesTheAwakeningAndTheUsualExpButNoGrowtypeAndNoItems()
    {
        QuestReward reward = QuestRules.ComputeReward(Link(911, 2), Hero(), Params(), -1);
        Assert.Equal(2, reward.Awakening);
        Assert.Equal(-1, reward.Growtype);
        Assert.Equal(5500, reward.Exp);                           // 经验表[50] 5000 × 权重 110 / 100,等级差 0 不扣
        Assert.Equal(0, reward.Gold);                             // 不是物品类奖励:没有金币标记
        Assert.Empty(reward.Items);
    }

    [Fact]
    public void ItemQuestsAndInvalidAwakeningRewardsComputeNoAwakening()
    {
        Assert.Equal(0, QuestRules.ComputeReward(Link(911, 0), Hero(), Params(), -1).Awakening);
        Assert.Equal(0, QuestRules.ComputeReward(Link(912, 3), Hero(), Params(), -1).Awakening);
        Assert.Equal(0, new QuestReward().Awakening);
    }

    [Fact]
    public void AGrowtypeRewardComputesNoAwakeningAndTheAwakeningRewardNoGrowtype()
    {
        QuestDef transfer = Link(911, 0);
        transfer.JobChange = 1;
        transfer.Grade = QuestGrade.CommonUnique;
        transfer.RewardType = QuestDef.GrowtypeRewardType;
        transfer.RewardData.Add(3);
        QuestReward reward = QuestRules.ComputeReward(transfer, Hero(20, 0, 0), Params(), -1);
        Assert.Equal(3, reward.Growtype);
        Assert.Equal(0, reward.Awakening);
    }

    // ------------------------------------------------------------------ 任务门:等级区间、前置链

    [Fact]
    public void TheQuestGateIsTheLevelRangeOfTheScript()
    {
        QuestDef first = Link(911, 0);
        var log = new QuestLog();
        Assert.Equal(QuestOffer.Level, QuestRules.CheckOffer(first, Hero(49), log));        // 差 1 级不行
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(first, Hero(50), log));
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(first, Hero(99), log));
        Assert.Equal(QuestOffer.Level, QuestRules.CheckOffer(first, Hero(100), log));
    }

    [Fact]
    public void TheChainIsOfferedInOrderAndTheLastLinkCarriesTheAwakening()
    {
        QuestDef a = Link(911, 0);
        QuestDef b = Link(912, 0, pre: 911);
        QuestDef c = Link(913, 1, pre: 912);
        var log = new QuestLog();
        Func<int, QuestDef> find = id => id == 911 ? a : id == 912 ? b : id == 913 ? c : null;
        Assert.Equal(QuestOffer.PreRequired, QuestRules.CheckOffer(b, Hero(), log));
        Assert.Equal(QuestOffer.PreRequired, QuestRules.CheckOffer(c, Hero(), log));
        foreach (QuestDef q in new[] { a, b, c })
        {
            Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(q, Hero(), log));
            ActiveQuest active = QuestRules.Accept(q, Hero(), log, out QuestOffer why);
            Assert.Equal(QuestOffer.Ok, why);
            Assert.False(QuestRules.IsReady(q, active, _ => 0));
            QuestTracker.OnMapClear(log, find, 30);
            Assert.True(QuestRules.IsReady(q, active, _ => 0));
            QuestReward reward = QuestRules.ComputeReward(q, Hero(), Params(), -1);
            Assert.Equal(q.Id == 913 ? 1 : 0, reward.Awakening);       // 只有链尾给觉醒
            Assert.True(QuestRules.Complete(q, log));
        }
        Assert.True(log.IsCleared(913));
        Assert.Equal(QuestOffer.Cleared, QuestRules.CheckOffer(c, Hero(), log));
    }

    [Fact]
    public void TheJobOfTheTargetListStillGatesAnAwakeningQuest()
    {
        QuestDef q = Link(911, 1);
        q.Targets.Clear();
        q.Targets.Add(new QuestTarget(2, 3, -1));                 // 职业 2 的转职方向 3,觉醒不限
        var log = new QuestLog();
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(q, Hero(50, 2, 3), log));
        Assert.Equal(QuestOffer.Job, QuestRules.CheckOffer(q, Hero(50, 2, 4), log));
        Assert.Equal(QuestOffer.Job, QuestRules.CheckOffer(q, Hero(50, 1, 3), log));
        Assert.Equal(QuestOffer.Job, QuestRules.CheckOffer(q, Hero(50, 2, 0), log));      // 没转职的接不了
    }
}
