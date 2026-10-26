using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// 任务系统的数据与规则(证据 E-123,服务端静态读码):.qst 的标记、分类与类型的数值次序、职业与转职目标表、可接条件的检查次序、
/// 接受/完成/放弃、猎杀与通关与拜访的计数、任务物品掉落、奖励公式、奖励参数表、任务目录与按 NPC 的索引。
/// 数值、名字与字符串全是合成的,只检验规则的形状,与原版数据无关。
/// </summary>
public class QuestTests
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

    private static QuestDef Make(int id, QuestKind kind, QuestGrade grade = QuestGrade.Epic, int subType = -1, params int[] data)
    {
        var q = new QuestDef(id, "n_quest/q" + id + ".qst")
        {
            Grade = grade,
            Kind = kind,
            SubType = subType,
            IntData = data,
            NpcId = 5,
            CompleteNpcId = 5,
            MinLevel = 1,
            MaxLevel = 99,
        };
        q.Targets.Add(new QuestTarget(EquipRules.AllJobs, -1, -1));
        return q;
    }

    private static Func<int, QuestDef> Finder(params QuestDef[] defs)
    {
        var map = defs.ToDictionary(d => d.Id);
        return id => map.TryGetValue(id, out QuestDef d) ? d : null;
    }

    private static QuestHero Hero(int level = 10, int job = 0, int grow = 0) => new QuestHero(level, job, grow);

    /// <summary>合成的奖励参数:经验表 下标 × 100,金币表 下标 × 10,权重 S 110、A 90,绿色惩罚 80,灰色惩罚 30。</summary>
    private static QuestParameters Params()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[difficulty]").Str("S").Int(110).Str("A").Int(90).Tag("[/difficulty]");
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

    // ------------------------------------------------------------------ 分类与类型

    [Fact]
    public void GradeKeywordsMapToServerNumbering()
    {
        Assert.True(QuestNames.TryGrade("[epic]", out QuestGrade g));
        Assert.Equal(QuestGrade.Epic, g);
        Assert.Equal(0, (int)g);
        Assert.True(QuestNames.TryGrade("[training]", out g));
        Assert.Equal(1, (int)g);
        Assert.True(QuestNames.TryGrade("[achievement]", out g));
        Assert.Equal(2, (int)g);
        Assert.True(QuestNames.TryGrade("[normaly repeat]", out g));
        Assert.Equal(4, (int)g);
        Assert.True(QuestNames.TryGrade("[common unique]", out g));
        Assert.Equal(5, (int)g);
        Assert.True(QuestNames.TryGrade("[title]", out g));
        Assert.Equal(7, (int)g);
        Assert.True(QuestNames.TryGrade("[urgent]", out g));
        Assert.Equal(8, (int)g);
    }

    [Theory]
    [InlineData("[daily]")]
    [InlineData("[daily random]")]
    [InlineData("[special daily]")]
    public void DailyKeywordsAllMapToTheDailyGrade(string tag)
    {
        Assert.True(QuestNames.TryGrade(tag, out QuestGrade g));
        Assert.Equal(QuestGrade.Daily, g);
        Assert.Equal(3, (int)g);
    }

    [Fact]
    public void GradeKeywordMatchingIgnoresCaseAndRejectsUnknown()
    {
        Assert.True(QuestNames.TryGrade("[EPIC]", out QuestGrade g));
        Assert.Equal(QuestGrade.Epic, g);
        Assert.False(QuestNames.TryGrade("[nonsense]", out _));
        Assert.False(QuestNames.TryGrade(null, out _));
    }

    [Fact]
    public void KindKeywordsMapToServerNumbering()
    {
        Assert.Equal(0, (int)QuestNames.KindOf("[seeking]"));
        Assert.Equal(1, (int)QuestNames.KindOf("[condition under clear]"));
        Assert.Equal(2, (int)QuestNames.KindOf("[hunt monster]"));
        Assert.Equal(3, (int)QuestNames.KindOf("[clear map]"));
        Assert.Equal(4, (int)QuestNames.KindOf("[meet npc]"));
        Assert.Equal(6, (int)QuestNames.KindOf("[hunt enemy]"));
        Assert.Equal(12, (int)QuestNames.KindOf("[clear quest]"));
        Assert.Equal(25, (int)QuestNames.KindOf("[seek n meet npc]"));
        Assert.Equal(26, (int)QuestNames.KindOf("[assault count]"));
        Assert.Equal(QuestKind.Unknown, QuestNames.KindOf("[nonsense]"));
        Assert.Equal(QuestKind.Unknown, QuestNames.KindOf(null));
    }

    [Fact]
    public void OnlyRepeatAndUrgentAreRepeatable()
    {
        foreach (QuestGrade g in Enum.GetValues<QuestGrade>())
        {
            Assert.Equal(g == QuestGrade.NormalRepeat || g == QuestGrade.Urgent, QuestNames.IsRepeatable(g));
        }
    }

    // ------------------------------------------------------------------ 脚本解析

    [Fact]
    public void ParsesTheCommonTags()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[name]").Keyed(11, "name_x");
        b.Tag("[grade]").Str("[common unique]");
        b.Tag("[type]").Str("[hunt monster]");
        b.Tag("[sub type]").Int(3);
        b.Tag("[npc index]").Int(21);
        b.Tag("[complete npc index]").Int(22);
        b.Tag("[level]").Int(5).Int(30);
        b.Tag("[int data]").Ints(7, -1, 100, 3).Tag("[/int data]");
        b.Tag("[depend message]").Keyed(11, "dep_x");
        b.Tag("[condition message]").Keyed(11, "cond_x");
        b.Tag("[solve message]").Keyed(11, "solve_x");
        b.Tag("[gold multiple]").Int(150);
        b.Tag("[difficulty]").Str("A");
        b.Tag("[quest point]").Int(2);
        b.Tag("[quest piece]").Int(1);
        b.Tag("[cant giveup]").Int(1);
        b.Tag("[ignore quest level 4 exp]").Int(1);
        b.Tag("[event]").Int(1);
        b.Tag("[exposed by npc]").Int(0);
        QuestDef q = Parse(b, 42);

        Assert.Equal(42, q.Id);
        Assert.Equal("n_quest/q42.qst", q.Path);
        Assert.Equal(QuestGrade.CommonUnique, q.Grade);
        Assert.Equal(QuestKind.HuntMonster, q.Kind);
        Assert.Equal("[hunt monster]", q.KindTag);
        Assert.Equal(3, q.SubType);
        Assert.Equal(21, q.NpcId);
        Assert.Equal(22, q.CompleteNpcId);
        Assert.Equal(5, q.MinLevel);
        Assert.Equal(30, q.MaxLevel);
        Assert.Equal(new[] { 7, -1, 100, 3 }, q.IntData);
        Assert.Equal(11, q.Name.Kind);
        Assert.Equal("name_x", q.Name.Key);
        Assert.Equal("dep_x", q.DependMessage.Key);
        Assert.Equal("cond_x", q.ConditionMessage.Key);
        Assert.Equal("solve_x", q.SolveMessage.Key);
        Assert.Equal(150, q.GoldPercent);
        Assert.Equal('A', q.DifficultyLetter);
        Assert.Equal(2, q.QuestPoint);
        Assert.Equal(1, q.QuestPiece);
        Assert.Equal(1, q.CantGiveUp);
        Assert.True(q.ExpUsesHeroLevel);
        Assert.True(q.IsEvent);
        Assert.False(q.Exposed);
    }

    [Fact]
    public void DefaultsMatchTheServerClearValues()
    {
        QuestDef q = Parse(new TokenStreamBuilder().Tag("[type]").Str("[meet npc]"));
        Assert.Equal(QuestGrade.NormalRepeat, q.Grade);
        Assert.Equal(-1, q.NpcId);
        Assert.Equal(-1, q.CompleteNpcId);
        Assert.Equal(-1, q.SubType);
        Assert.Equal(100, q.GoldPercent);
        Assert.True(q.Exposed);
        Assert.False(q.ExpUsesHeroLevel);
        Assert.False(q.IsEvent);
        Assert.Equal(0, q.CantGiveUp);
        Assert.Equal("[item]", q.RewardType);
        Assert.Equal('\0', q.DifficultyLetter);
        Assert.True(q.Name.IsEmpty);
        QuestTarget target = Assert.Single(q.Targets);
        Assert.Equal(EquipRules.AllJobs, target.Job);
        Assert.Equal(-1, target.GrowType);
        Assert.Equal(-1, target.Awakening);
    }

    [Fact]
    public void NullScriptGivesAnEmptyDefinition()
    {
        QuestDef q = QuestScript.Parse(null, 9, "n_quest/x.qst");
        Assert.Equal(9, q.Id);
        Assert.Equal(QuestKind.Unknown, q.Kind);
        Assert.Empty(q.Targets);
    }

    [Fact]
    public void JobAndGrowTypeBuildTheSingleTarget()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[job]").Str("[thief]");
        b.Tag("[grow type]").Int(2);
        QuestTarget t = Assert.Single(Parse(b).Targets);
        Assert.Equal(EquipRules.JobOf("[thief]"), t.Job);
        Assert.Equal(2, t.GrowType);
        Assert.Equal(-1, t.Awakening);
    }

    [Fact]
    public void AllJobKeywordMeansEveryJob()
    {
        QuestTarget t = Assert.Single(Parse(new TokenStreamBuilder().Tag("[job]").Str("[all]")).Targets);
        Assert.Equal(EquipRules.AllJobs, t.Job);
    }

    [Fact]
    public void TargetCharacterReadsTriplesAndOverridesJobTag()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[job]").Str("[mage]");
        b.Tag("[target character]").Str("[swordman]").Int(-1).Int(-1).Str("[priest]").Int(1).Int(-1);
        b.Tag("[/target character]");
        QuestDef q = Parse(b);
        Assert.Equal(2, q.Targets.Count);
        Assert.Equal(EquipRules.JobOf("[swordman]"), q.Targets[0].Job);
        Assert.Equal(-1, q.Targets[0].GrowType);
        Assert.Equal(EquipRules.JobOf("[priest]"), q.Targets[1].Job);
        Assert.Equal(1, q.Targets[1].GrowType);
    }

    [Fact]
    public void PreRequiredLinesAreSeparateGroupsAndCollisionIsOneList()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[pre required quest]").Ints(10, 11).Tag("[/pre required quest]");
        b.Tag("[pre required quest]").Int(12).Tag("[/pre required quest]");
        b.Tag("[collision quest]").Ints(30, 31, 32).Tag("[/collision quest]");
        QuestDef q = Parse(b);
        Assert.Equal(2, q.PreRequired.Count);
        Assert.Equal(new[] { 10, 11 }, q.PreRequired[0]);
        Assert.Equal(new[] { 12 }, q.PreRequired[1]);
        Assert.Equal(new[] { 30, 31, 32 }, q.Collision);
    }

    [Fact]
    public void RewardListsReadIdCountPairsAndDropAnOddTail()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[reward type]").Str("[item]");
        b.Tag("[reward int data]").Ints(0, 1, 500, 2, 7).Tag("[/reward int data]");
        b.Tag("[reward selection int data]").Ints(600, 1, 601, 1).Tag("[/reward selection int data]");
        b.Tag("[depend give item]").Ints(700, 3).Tag("[/depend give item]");
        QuestDef q = Parse(b);
        Assert.Equal("[item]", q.RewardType);
        Assert.Equal(2, q.Rewards.Count);
        Assert.Equal(0, q.Rewards[0].Id);
        Assert.Equal(500, q.Rewards[1].Id);
        Assert.Equal(2, q.Rewards[1].Count);
        Assert.Equal(2, q.SelectRewards.Count);
        Assert.Equal(601, q.SelectRewards[1].Id);
        QuestItem give = Assert.Single(q.AcceptItems);
        Assert.Equal(700, give.Id);
        Assert.Equal(3, give.Count);
    }

    [Fact]
    public void RewardItemsCanCarryAJobAndGrowTypeFilter()
    {
        var b = new TokenStreamBuilder();
        // 编号 [job] 职业 转职方向 数量:只给这个职业与转职方向;不写 [job] 的是所有职业
        b.Tag("[reward int data]").Ints(0, 500, 700).Str("[job]").Ints(9, -1, 1).Ints(701).Str("[job]").Ints(2, 3, 2).Ints(702, 4).Tag("[/reward int data]");
        b.Tag("[reward selection int data]").Ints(800).Str("[job]").Ints(0, -1, 1).Ints(801, 1).Tag("[/reward selection int data]");
        QuestDef q = Parse(b);

        Assert.Equal(4, q.Rewards.Count);
        Assert.Equal(0, q.Rewards[0].Id);
        Assert.Equal(500, q.Rewards[0].Count);
        Assert.Equal(EquipRules.AllJobs, q.Rewards[0].Job);
        Assert.Equal(-1, q.Rewards[0].GrowType);
        Assert.Equal(700, q.Rewards[1].Id);
        Assert.Equal(1, q.Rewards[1].Count);
        Assert.Equal(9, q.Rewards[1].Job);
        Assert.Equal(-1, q.Rewards[1].GrowType);
        Assert.Equal(701, q.Rewards[2].Id);
        Assert.Equal(2, q.Rewards[2].Count);
        Assert.Equal(2, q.Rewards[2].Job);
        Assert.Equal(3, q.Rewards[2].GrowType);
        Assert.Equal(702, q.Rewards[3].Id);
        Assert.Equal(4, q.Rewards[3].Count);
        Assert.Equal(EquipRules.AllJobs, q.Rewards[3].Job);
        Assert.Equal(2, q.SelectRewards.Count);
        Assert.Equal(0, q.SelectRewards[0].Job);
        Assert.Equal(801, q.SelectRewards[1].Id);
    }

    [Fact]
    public void MalformedJobFormStopsTheList()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[reward int data]").Ints(500, 2, 700).Str("[job]").Int(9).Tag("[/reward int data]");
        QuestDef q = Parse(b);
        QuestItem only = Assert.Single(q.Rewards);
        Assert.Equal(500, only.Id);
        Assert.Equal(2, only.Count);
    }

    [Fact]
    public void ItemAppliesToTheRightJobAndGrowType()
    {
        Assert.True(new QuestItem(1, 1).AppliesTo(0, 0));
        Assert.True(new QuestItem(1, 1).AppliesTo(7, 3));
        var forJob = new QuestItem(1, 1, 2, -1);
        Assert.True(forJob.AppliesTo(2, 0));
        Assert.True(forJob.AppliesTo(2, 5));
        Assert.False(forJob.AppliesTo(1, 0));
        var forGrow = new QuestItem(1, 1, 2, 3);
        Assert.True(forGrow.AppliesTo(2, 3));
        Assert.False(forGrow.AppliesTo(2, 0));
        Assert.False(forGrow.AppliesTo(1, 3));
    }

    [Fact]
    public void DropTablesReadTheirGroupWidths()
    {
        var b = new TokenStreamBuilder();
        // 每组 7 个:怪物、副本、难度、物品、数量、百分率、最后一个值
        b.Tag("[monster reward item]").Ints(100, 7, 1, 900, 2, 40, -1, 101, -1, -1, 901, 1, 100, 5).Tag("[/monster reward item]");
        // 每组 8 个:怪物、敌人种类、副本、难度、物品、数量、百分率、最后一个值
        b.Tag("[enemy reward item]").Ints(102, 2, 8, 0, 902, 3, 25, -1).Tag("[/enemy reward item]");
        // 每组 6 个:副本、难度、物品、数量、百分率、最后一个值
        b.Tag("[clear reward item]").Ints(9, -1, 903, 4, 170, -1).Tag("[/clear reward item]");
        QuestDef q = Parse(b);

        Assert.Equal(3, q.MonsterDrops.Count);
        QuestMonsterDrop first = q.MonsterDrops[0];
        Assert.Equal(100, first.Monster);
        Assert.Equal(0, first.EnemyType);
        Assert.Equal(7, first.Dungeon);
        Assert.Equal(1, first.Difficulty);
        Assert.Equal(900, first.Item);
        Assert.Equal(2, first.Count);
        Assert.Equal(40, first.Rate);
        Assert.Equal(-1, first.Extra);
        Assert.Equal(5, q.MonsterDrops[1].Extra);
        QuestMonsterDrop enemy = q.MonsterDrops[2];
        Assert.Equal(102, enemy.Monster);
        Assert.Equal(2, enemy.EnemyType);
        Assert.Equal(8, enemy.Dungeon);
        Assert.Equal(0, enemy.Difficulty);
        Assert.Equal(902, enemy.Item);
        Assert.Equal(25, enemy.Rate);

        QuestClearDrop clear = Assert.Single(q.ClearDrops);
        Assert.Equal(9, clear.Dungeon);
        Assert.Equal(-1, clear.Difficulty);
        Assert.Equal(903, clear.Item);
        Assert.Equal(4, clear.Count);
        Assert.Equal(170, clear.Rate);
    }

    [Fact]
    public void IncompleteDropGroupsAreIgnored()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[monster reward item]").Ints(100, 7, 1, 900, 2, 40);
        Assert.Empty(Parse(b).MonsterDrops);
    }

    [Fact]
    public void DungeonInfoReadsPairs()
    {
        QuestDef q = Parse(new TokenStreamBuilder().Tag("[dungeon info]").Ints(14, -1, 31, 2).Tag("[/dungeon info]"));
        Assert.Equal(2, q.DungeonHints.Count);
        Assert.Equal(14, q.DungeonHints[0].Key);
        Assert.Equal(-1, q.DungeonHints[0].Value);
        Assert.Equal(31, q.DungeonHints[1].Key);
        Assert.Equal(2, q.DungeonHints[1].Value);
    }

    [Fact]
    public void UnknownTagsAreIgnoredAndTheLastLevelWins()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[go guide]").Int(1);
        b.Tag("[level]").Int(1).Int(10);
        b.Tag("[level]").Int(3).Int(40);
        QuestDef q = Parse(b);
        Assert.Equal(3, q.MinLevel);
        Assert.Equal(40, q.MaxLevel);
    }

    // ------------------------------------------------------------------ 整数数据的分组

    [Fact]
    public void HuntMonsterGroupsAreFourIntegersEach()
    {
        QuestDef q = Make(1, QuestKind.HuntMonster, data: new[] { 7, 1, 100, 3, -1, -1, 101, 5 });
        List<QuestKillGroup> groups = q.KillGroups();
        Assert.Equal(2, groups.Count);
        Assert.Equal(7, groups[0].Dungeon);
        Assert.Equal(1, groups[0].Difficulty);
        Assert.Equal(100, groups[0].Monster);
        Assert.Equal(0, groups[0].EnemyType);
        Assert.Equal(3, groups[0].Count);
        Assert.Equal(5, groups[1].Count);
    }

    [Fact]
    public void HuntEnemyGroupsAreFiveIntegersEach()
    {
        QuestDef q = Make(1, QuestKind.HuntEnemy, data: new[] { 7, -1, 100, 2, 4 });
        QuestKillGroup g = Assert.Single(q.KillGroups());
        Assert.Equal(100, g.Monster);
        Assert.Equal(2, g.EnemyType);
        Assert.Equal(4, g.Count);
    }

    [Fact]
    public void OtherKindsHaveNoKillGroupsAndSeekingItemsArePairs()
    {
        Assert.Empty(Make(1, QuestKind.Seeking, data: new[] { 900, 3 }).KillGroups());
        QuestDef seek = Make(2, QuestKind.Seeking, data: new[] { 900, 3, 901, 1 });
        Assert.Equal(2, seek.SeekingItems().Count);
        Assert.Equal(901, seek.SeekingItems()[1].Id);
        Assert.Empty(Make(3, QuestKind.HuntMonster, data: new[] { 7, 1, 100, 3 }).SeekingItems());
    }

    [Fact]
    public void MeetNpcAndClearMapTargets()
    {
        Assert.Equal(9, Make(1, QuestKind.MeetNpc, data: new[] { 9 }).MeetNpcId());
        QuestDef noData = Make(2, QuestKind.MeetNpc);
        noData.CompleteNpcId = 12;
        Assert.Equal(12, noData.MeetNpcId());
        Assert.Equal(-1, Make(3, QuestKind.HuntMonster).MeetNpcId());
        Assert.Equal(30, Make(4, QuestKind.ClearMap, data: new[] { 30, 202 }).ClearMapId());
        Assert.Equal(-1, Make(5, QuestKind.ClearMap).ClearMapId());
        Assert.Equal(-1, Make(6, QuestKind.MeetNpc, data: new[] { 9 }).ClearMapId());
    }

    // ------------------------------------------------------------------ 支持范围

    [Fact]
    public void SupportedShapesHaveNoUnsupportedReason()
    {
        Assert.Null(QuestRules.UnsupportedReason(Make(1, QuestKind.Seeking, data: new[] { 900, 3 })));
        Assert.Null(QuestRules.UnsupportedReason(Make(2, QuestKind.HuntMonster, data: new[] { 7, 1, 100, 3 })));
        Assert.Null(QuestRules.UnsupportedReason(Make(3, QuestKind.HuntEnemy, data: new[] { 7, 1, 100, 2, 3 })));
        Assert.Null(QuestRules.UnsupportedReason(Make(4, QuestKind.ClearMap, data: new[] { 30 })));
        Assert.Null(QuestRules.UnsupportedReason(Make(5, QuestKind.MeetNpc, data: new[] { 9 })));
        Assert.Null(QuestRules.UnsupportedReason(Make(6, QuestKind.ConditionUnderClear, subType: QuestRules.SubClearDungeon, data: new[] { 7, 1 })));
        Assert.Null(QuestRules.UnsupportedReason(Make(7, QuestKind.ConditionUnderClear, subType: QuestRules.SubClearDungeon, data: new[] { 7, 1, 3 })));
        Assert.Null(QuestRules.UnsupportedReason(Make(8, QuestKind.ConditionUnderClear, subType: QuestRules.SubClearInTime, data: new[] { 7, 1, 600 })));
        Assert.True(QuestRules.IsSupported(Make(9, QuestKind.ClearMap, QuestGrade.CommonUnique, data: new[] { 30 })));
        Assert.True(QuestRules.IsSupported(Make(10, QuestKind.ClearMap, QuestGrade.NormalRepeat, data: new[] { 30 })));
    }

    [Fact]
    public void UnsupportedReasonsAreSpecific()
    {
        Assert.Equal("missing", QuestRules.UnsupportedReason(null));
        Assert.Equal("grade", QuestRules.UnsupportedReason(Make(1, QuestKind.ClearMap, QuestGrade.Daily, data: new[] { 30 })));
        Assert.Equal("grade", QuestRules.UnsupportedReason(Make(2, QuestKind.ClearMap, QuestGrade.Achievement, data: new[] { 30 })));
        QuestDef noNpc = Make(3, QuestKind.ClearMap, data: new[] { 30 });
        noNpc.NpcId = -1;
        Assert.Equal("no-npc", QuestRules.UnsupportedReason(noNpc));
        QuestDef hidden = Make(4, QuestKind.ClearMap, data: new[] { 30 });
        hidden.Exposed = false;
        Assert.Equal("hidden", QuestRules.UnsupportedReason(hidden));
        QuestDef contradiction = Make(5, QuestKind.ClearMap, data: new[] { 30 });
        contradiction.JobChange = 1;
        Assert.Equal("job-change", QuestRules.UnsupportedReason(contradiction));
        QuestDef title = Make(6, QuestKind.ClearMap, data: new[] { 30 });
        title.RewardType = "[title]";
        Assert.Equal("reward-type", QuestRules.UnsupportedReason(title));
        Assert.Equal("shape", QuestRules.UnsupportedReason(Make(7, QuestKind.HuntMonster, data: new[] { 7, 1, 100 })));
        Assert.Equal("shape", QuestRules.UnsupportedReason(Make(8, QuestKind.HuntEnemy, data: new[] { 7, 1, 100, 3 })));
        Assert.Equal("shape", QuestRules.UnsupportedReason(Make(9, QuestKind.Seeking, data: new[] { 900 })));
        Assert.Equal("shape", QuestRules.UnsupportedReason(Make(10, QuestKind.Seeking, data: new[] { 900, 0 })));
        Assert.Equal("shape", QuestRules.UnsupportedReason(Make(11, QuestKind.ClearMap)));
        // 拜访类没写整数数据时用交任务 NPC;两个都没有才算数据形状不对
        Assert.Null(QuestRules.UnsupportedReason(Make(12, QuestKind.MeetNpc, data: Array.Empty<int>())));
        QuestDef meetNothing = Make(18, QuestKind.MeetNpc, data: Array.Empty<int>());
        meetNothing.CompleteNpcId = -1;
        Assert.Equal("shape", QuestRules.UnsupportedReason(meetNothing));
        Assert.Equal("shape", QuestRules.UnsupportedReason(Make(13, QuestKind.ConditionUnderClear, subType: 6, data: new[] { 7 })));
        Assert.Equal("shape", QuestRules.UnsupportedReason(Make(14, QuestKind.ConditionUnderClear, subType: 0, data: new[] { 7, 1, 0 })));
        Assert.Equal("sub-type", QuestRules.UnsupportedReason(Make(15, QuestKind.ConditionUnderClear, subType: 4, data: new[] { 7, 1 })));
        Assert.Equal("kind", QuestRules.UnsupportedReason(Make(16, QuestKind.GetItem, data: new[] { 1 })));
        Assert.Equal("kind", QuestRules.UnsupportedReason(Make(17, QuestKind.Unknown)));
    }

    // ------------------------------------------------------------------ 职业与转职

    [Fact]
    public void AllJobsTargetMatchesEveryone()
    {
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        Assert.True(QuestRules.JobMatches(q, 0, 0));
        Assert.True(QuestRules.JobMatches(q, 5, 3));
    }

    [Fact]
    public void GrowTypeIsOnlyCheckedWhenTheTargetNamesOne()
    {
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.Targets.Clear();
        q.Targets.Add(new QuestTarget(0, 1, -1));
        Assert.False(QuestRules.JobMatches(q, 0, 0));
        Assert.True(QuestRules.JobMatches(q, 0, 1));
        Assert.False(QuestRules.JobMatches(q, 1, 1));

        q.Targets.Add(new QuestTarget(0, -1, -1));
        Assert.True(QuestRules.JobMatches(q, 0, 5));
    }

    [Fact]
    public void JobChangeTenAndTwentySkipTheGrowTypeCheck()
    {
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.Targets.Clear();
        q.Targets.Add(new QuestTarget(0, 2, -1));
        Assert.False(QuestRules.JobMatches(q, 0, 0));
        q.JobChange = 10;
        Assert.True(QuestRules.JobMatches(q, 0, 0));
        q.JobChange = 20;
        Assert.True(QuestRules.JobMatches(q, 0, 0));
        q.JobChange = 2;
        Assert.False(QuestRules.JobMatches(q, 0, 0));
        Assert.False(QuestRules.JobMatches(q, 1, 0));
    }

    [Fact]
    public void AwakeningEntriesNeverMatchBecauseTheServerPassesMinusOne()
    {
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.Targets.Clear();
        q.Targets.Add(new QuestTarget(0, 1, 3));
        Assert.False(QuestRules.JobMatches(q, 0, 1));
    }

    // ------------------------------------------------------------------ 可接条件

    [Fact]
    public void FreshQuestIsOffered()
    {
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(Make(1, QuestKind.ClearMap, data: new[] { 30 }), Hero(), new QuestLog()));
    }

    [Fact]
    public void LevelRangeIsInclusive()
    {
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.MinLevel = 10;
        q.MaxLevel = 20;
        var log = new QuestLog();
        Assert.Equal(QuestOffer.Level, QuestRules.CheckOffer(q, Hero(9), log));
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(q, Hero(10), log));
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(q, Hero(20), log));
        Assert.Equal(QuestOffer.Level, QuestRules.CheckOffer(q, Hero(21), log));
    }

    [Fact]
    public void JobMismatchIsReportedBeforeLevel()
    {
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.Targets.Clear();
        q.Targets.Add(new QuestTarget(1, -1, -1));
        q.MinLevel = 50;
        Assert.Equal(QuestOffer.Job, QuestRules.CheckOffer(q, Hero(1, 0), new QuestLog()));
    }

    [Fact]
    public void UnsupportedIsReportedFirst()
    {
        QuestDef q = Make(1, QuestKind.GetItem, data: new[] { 1 });
        q.MinLevel = 50;
        Assert.Equal(QuestOffer.Unsupported, QuestRules.CheckOffer(q, Hero(1), new QuestLog()));
    }

    [Fact]
    public void ActiveAndClearedBlockTheOffer()
    {
        QuestDef unique = Make(1, QuestKind.ClearMap, QuestGrade.CommonUnique, data: new[] { 30 });
        var log = new QuestLog();
        log.Add(1, new[] { 1 });
        Assert.Equal(QuestOffer.Active, QuestRules.CheckOffer(unique, Hero(), log));
        log.Remove(1);
        log.MarkCleared(1);
        Assert.Equal(QuestOffer.Cleared, QuestRules.CheckOffer(unique, Hero(), log));

        QuestDef repeat = Make(2, QuestKind.ClearMap, QuestGrade.NormalRepeat, data: new[] { 30 });
        log.MarkCleared(2);
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(repeat, Hero(), log));
    }

    [Fact]
    public void PreRequiredGroupsAreOrOfAnds()
    {
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.PreRequired.Add(new[] { 10, 11 });
        q.PreRequired.Add(new[] { 12 });
        var log = new QuestLog();
        Assert.Equal(QuestOffer.PreRequired, QuestRules.CheckOffer(q, Hero(), log));
        log.MarkCleared(10);
        Assert.Equal(QuestOffer.PreRequired, QuestRules.CheckOffer(q, Hero(), log));
        log.MarkCleared(11);
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(q, Hero(), log));

        var other = new QuestLog();
        other.MarkCleared(12);
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(q, Hero(), other));
    }

    [Fact]
    public void CollisionBlocksWhenAnyIsCleared()
    {
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.Collision.AddRange(new[] { 30, 31 });
        var log = new QuestLog();
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(q, Hero(), log));
        log.MarkCleared(31);
        Assert.Equal(QuestOffer.Collision, QuestRules.CheckOffer(q, Hero(), log));
    }

    [Fact]
    public void ActiveSlotsAreLimitedToTwenty()
    {
        var log = new QuestLog();
        for (int i = 100; i < 100 + QuestLog.MaxActive; i++)
        {
            Assert.NotNull(log.Add(i, new[] { 1 }));
        }
        Assert.False(log.HasFreeSlot);
        Assert.Null(log.Add(999, new[] { 1 }));
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        Assert.Equal(QuestOffer.NoSlot, QuestRules.CheckOffer(q, Hero(), log));
        Assert.Null(QuestRules.Accept(q, Hero(), log, out QuestOffer why));
        Assert.Equal(QuestOffer.NoSlot, why);
    }

    [Fact]
    public void AcceptAddsTheQuestWithInitialCounters()
    {
        QuestDef q = Make(1, QuestKind.HuntMonster, data: new[] { 7, 1, 100, 3, -1, -1, 101, 2 });
        var log = new QuestLog();
        ActiveQuest a = QuestRules.Accept(q, Hero(), log, out QuestOffer why);
        Assert.Equal(QuestOffer.Ok, why);
        Assert.NotNull(a);
        Assert.Equal(new[] { 3, 2 }, a.Remaining);
        Assert.True(log.IsActive(1));
        Assert.Null(QuestRules.Accept(q, Hero(), log, out why));
        Assert.Equal(QuestOffer.Active, why);
        Assert.Single(log.Active);
    }

    [Fact]
    public void InitialCountersByKind()
    {
        Assert.Equal(new[] { 4 }, QuestRules.InitialRemaining(Make(1, QuestKind.HuntEnemy, data: new[] { 7, 1, 100, 2, 4 })));
        Assert.Equal(new[] { 1 }, QuestRules.InitialRemaining(Make(2, QuestKind.ClearMap, data: new[] { 30 })));
        Assert.Equal(new[] { 1 }, QuestRules.InitialRemaining(Make(3, QuestKind.MeetNpc, data: new[] { 9 })));
        Assert.Empty(QuestRules.InitialRemaining(Make(4, QuestKind.Seeking, data: new[] { 900, 3 })));
        Assert.Equal(new[] { 1 }, QuestRules.InitialRemaining(Make(5, QuestKind.ConditionUnderClear, subType: 6, data: new[] { 7, 1 })));
        Assert.Equal(new[] { 3 }, QuestRules.InitialRemaining(Make(6, QuestKind.ConditionUnderClear, subType: 6, data: new[] { 7, 1, 3 })));
        Assert.Equal(new[] { 1 }, QuestRules.InitialRemaining(Make(7, QuestKind.ConditionUnderClear, subType: 0, data: new[] { 7, 1, 600 })));
        Assert.Equal(new[] { 1 }, QuestRules.InitialRemaining(Make(8, QuestKind.ConditionUnderClear, subType: 6, data: new[] { 7, 1, 0 })));
    }

    // ------------------------------------------------------------------ 击杀、通关、拜访

    [Fact]
    public void KillsCountTowardTheFirstMatchingGroup()
    {
        QuestDef q = Make(1, QuestKind.HuntMonster, data: new[] { 5, -1, 100, 2, -1, -1, 100, 1, -1, -1, 101, 1 });
        var log = new QuestLog();
        ActiveQuest a = QuestRules.Accept(q, Hero(), log, out _);
        Func<int, QuestDef> find = Finder(q);

        // 副本不对时第一组不匹配,第二组(任意副本)接住
        Assert.Equal(new[] { 1 }, QuestTracker.OnKill(log, find, 6, 0, 100, 1));
        Assert.Equal(new[] { 2, 0, 1 }, a.Remaining);

        // 两组都匹配时只算第一组,一只怪只减一次
        Assert.Equal(new[] { 1 }, QuestTracker.OnKill(log, find, 5, 0, 100, 1));
        Assert.Equal(new[] { 1, 0, 1 }, a.Remaining);
        QuestTracker.OnKill(log, find, 5, 0, 100, 1);
        Assert.Equal(new[] { 0, 0, 1 }, a.Remaining);
        Assert.False(a.CountersDone);
        // 前两组都杀够了,同一种怪不再有变化;别的怪还差一只
        Assert.Empty(QuestTracker.OnKill(log, find, 5, 0, 100, 1));
        Assert.Empty(QuestTracker.OnKill(log, find, 5, 0, 999, 1));
        QuestTracker.OnKill(log, find, 9, 3, 101, 1);
        Assert.True(a.CountersDone);
        Assert.Empty(QuestTracker.OnKill(log, find, 9, 3, 101, 1));
    }

    [Fact]
    public void DifficultyWildcardAndEquality()
    {
        QuestDef q = Make(1, QuestKind.HuntMonster, data: new[] { -1, 2, 100, 1 });
        var log = new QuestLog();
        QuestRules.Accept(q, Hero(), log, out _);
        Func<int, QuestDef> find = Finder(q);
        Assert.Empty(QuestTracker.OnKill(log, find, 7, 1, 100, 1));
        Assert.Single(QuestTracker.OnKill(log, find, 7, 2, 100, 1));
    }

    [Fact]
    public void HuntMonsterIgnoresEnemyTypeButHuntEnemyMatchesIt()
    {
        QuestDef monster = Make(1, QuestKind.HuntMonster, data: new[] { -1, -1, 100, 2 });
        QuestDef enemy = Make(2, QuestKind.HuntEnemy, data: new[] { -1, -1, 100, 2, 1 });
        var log = new QuestLog();
        QuestRules.Accept(monster, Hero(), log, out _);
        QuestRules.Accept(enemy, Hero(), log, out _);
        Func<int, QuestDef> find = Finder(monster, enemy);

        List<int> changed = QuestTracker.OnKill(log, find, 7, 0, 100, QuestRules.EnemyNormal);
        Assert.Equal(new[] { 1 }, changed);
        changed = QuestTracker.OnKill(log, find, 7, 0, 100, QuestRules.EnemyNamed);
        Assert.Equal(new[] { 1, 2 }, changed);
    }

    [Fact]
    public void NegativeMonsterCodeMatchesAnyMonster()
    {
        QuestDef q = Make(1, QuestKind.HuntEnemy, data: new[] { -1, -1, -1, 2, 2 });
        var log = new QuestLog();
        ActiveQuest a = QuestRules.Accept(q, Hero(), log, out _);
        Func<int, QuestDef> find = Finder(q);
        Assert.Empty(QuestTracker.OnKill(log, find, 7, 0, 100, QuestRules.EnemyNormal));
        Assert.Single(QuestTracker.OnKill(log, find, 7, 0, 100, QuestRules.EnemyNamed));
        Assert.Single(QuestTracker.OnKill(log, find, 9, 1, 555, QuestRules.EnemyNamed));
        Assert.True(a.CountersDone);
    }

    [Fact]
    public void MonstersThatDoNotExistMakeAHuntUnsupportedWhenChecked()
    {
        QuestDef q = Make(1, QuestKind.HuntMonster, data: new[] { -1, -1, 100, 2, -1, -1, 101, 1 });
        Assert.Null(QuestRules.UnsupportedReason(q));
        Assert.Null(QuestRules.UnsupportedReason(q, m => true));
        Assert.Equal("monster", QuestRules.UnsupportedReason(q, m => m == 100));
        Assert.False(QuestRules.IsSupported(q, m => m == 100));
        // 负数的怪物编号是"任何怪物",不用查
        QuestDef any = Make(2, QuestKind.HuntEnemy, data: new[] { -1, -1, -1, 2, 1 });
        Assert.Null(QuestRules.UnsupportedReason(any, m => false));
        // 数量为 0 的目标组算数据不对
        QuestDef zero = Make(3, QuestKind.HuntMonster, data: new[] { -1, -1, 100, 0 });
        Assert.Equal("shape", QuestRules.UnsupportedReason(zero));
    }

    [Fact]
    public void EnemyTypeComesFromTheMonsterRank()
    {
        Assert.Equal(1, QuestRules.EnemyTypeOfRank(0));
        Assert.Equal(1, QuestRules.EnemyTypeOfRank(4));
        Assert.Equal(2, QuestRules.EnemyTypeOfRank(5));
        Assert.Equal(2, QuestRules.EnemyTypeOfRank(8));
        Assert.Equal(1, QuestRules.EnemyTypeOfRank(9));
    }

    [Fact]
    public void KillsDoNotTouchOtherKindsOrUnknownQuests()
    {
        QuestDef seek = Make(1, QuestKind.Seeking, data: new[] { 900, 1 });
        var log = new QuestLog();
        QuestRules.Accept(seek, Hero(), log, out _);
        log.Add(77, new[] { 1 });
        Assert.Empty(QuestTracker.OnKill(log, Finder(seek), 7, 0, 100, 1));
    }

    [Fact]
    public void MapClearCompletesTheMatchingQuestOnce()
    {
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30, 202 });
        var log = new QuestLog();
        ActiveQuest a = QuestRules.Accept(q, Hero(), log, out _);
        Func<int, QuestDef> find = Finder(q);
        Assert.Empty(QuestTracker.OnMapClear(log, find, 31));
        Assert.Equal(new[] { 1 }, QuestTracker.OnMapClear(log, find, 30));
        Assert.True(a.CountersDone);
        Assert.Empty(QuestTracker.OnMapClear(log, find, 30));
    }

    [Fact]
    public void DungeonClearCountsTimesAndHonoursWildcards()
    {
        QuestDef once = Make(1, QuestKind.ConditionUnderClear, subType: 6, data: new[] { 7, -1 });
        QuestDef thrice = Make(2, QuestKind.ConditionUnderClear, subType: 6, data: new[] { -1, 2, 3 });
        var log = new QuestLog();
        ActiveQuest a = QuestRules.Accept(once, Hero(), log, out _);
        ActiveQuest b = QuestRules.Accept(thrice, Hero(), log, out _);
        Func<int, QuestDef> find = Finder(once, thrice);

        Assert.Empty(QuestTracker.OnDungeonClear(log, find, 8, 0, 1000));
        Assert.Equal(new[] { 1 }, QuestTracker.OnDungeonClear(log, find, 7, 0, 1000));
        Assert.True(a.CountersDone);
        Assert.Equal(new[] { 3 }, b.Remaining);
        Assert.Equal(new[] { 2 }, QuestTracker.OnDungeonClear(log, find, 9, 2, 1000));
        QuestTracker.OnDungeonClear(log, find, 9, 2, 1000);
        Assert.False(b.CountersDone);
        QuestTracker.OnDungeonClear(log, find, 3, 2, 1000);
        Assert.True(b.CountersDone);
    }

    [Fact]
    public void TimedClearUsesSecondsTimesOneThousandInclusive()
    {
        QuestDef q = Make(1, QuestKind.ConditionUnderClear, subType: 0, data: new[] { 7, -1, 600 });
        var log = new QuestLog();
        ActiveQuest a = QuestRules.Accept(q, Hero(), log, out _);
        Func<int, QuestDef> find = Finder(q);
        Assert.Empty(QuestTracker.OnDungeonClear(log, find, 7, 0, 600_001));
        Assert.False(a.CountersDone);
        Assert.Single(QuestTracker.OnDungeonClear(log, find, 7, 0, 600_000));
        Assert.True(a.CountersDone);
    }

    [Fact]
    public void TalkingToTheTargetNpcCompletesMeetNpc()
    {
        QuestDef q = Make(1, QuestKind.MeetNpc, data: new[] { 9 });
        var log = new QuestLog();
        ActiveQuest a = QuestRules.Accept(q, Hero(), log, out _);
        Func<int, QuestDef> find = Finder(q);
        Assert.Empty(QuestTracker.OnTalk(log, find, 8));
        Assert.Equal(new[] { 1 }, QuestTracker.OnTalk(log, find, 9));
        Assert.True(a.CountersDone);
        Assert.Empty(QuestTracker.OnTalk(log, find, 9));
    }

    // ------------------------------------------------------------------ 完成、放弃

    [Fact]
    public void SeekingIsReadyOnlyWithEnoughItems()
    {
        QuestDef q = Make(1, QuestKind.Seeking, data: new[] { 900, 3, 901, 1 });
        var log = new QuestLog();
        ActiveQuest a = QuestRules.Accept(q, Hero(), log, out _);
        var bag = new Dictionary<int, int> { [900] = 3 };
        int Count(int id) => bag.TryGetValue(id, out int n) ? n : 0;
        Assert.False(QuestRules.IsReady(q, a, Count));
        bag[901] = 1;
        Assert.True(QuestRules.IsReady(q, a, Count));
        bag[900] = 2;
        Assert.False(QuestRules.IsReady(q, a, Count));
        Assert.False(QuestRules.IsReady(q, a, null));
    }

    [Fact]
    public void CountedQuestsAreReadyWhenCountersAreZero()
    {
        QuestDef q = Make(1, QuestKind.HuntMonster, data: new[] { -1, -1, 100, 1 });
        var log = new QuestLog();
        ActiveQuest a = QuestRules.Accept(q, Hero(), log, out _);
        Assert.False(QuestRules.IsReady(q, a, null));
        QuestTracker.OnKill(log, Finder(q), 7, 0, 100, 1);
        Assert.True(QuestRules.IsReady(q, a, null));
        Assert.False(QuestRules.IsReady(null, a, null));
        Assert.False(QuestRules.IsReady(q, null, null));
    }

    [Fact]
    public void HandInNpcFollowsTheKind()
    {
        QuestDef hunt = Make(1, QuestKind.HuntMonster, data: new[] { -1, -1, 100, 1 });
        hunt.CompleteNpcId = 12;
        Assert.Equal(12, QuestRules.HandInNpc(hunt));
        hunt.CompleteNpcId = -1;
        Assert.Equal(-1, QuestRules.HandInNpc(hunt));
        QuestDef meet = Make(2, QuestKind.MeetNpc, data: new[] { 9 });
        meet.CompleteNpcId = 3;
        Assert.Equal(9, QuestRules.HandInNpc(meet));
    }

    [Fact]
    public void CompletingRecordsUniqueQuestsAndNotRepeatableOnes()
    {
        QuestDef unique = Make(1, QuestKind.ClearMap, QuestGrade.Epic, data: new[] { 30 });
        QuestDef repeat = Make(2, QuestKind.ClearMap, QuestGrade.NormalRepeat, data: new[] { 30 });
        var log = new QuestLog();
        QuestRules.Accept(unique, Hero(), log, out _);
        QuestRules.Accept(repeat, Hero(), log, out _);
        Assert.True(QuestRules.Complete(unique, log));
        Assert.True(QuestRules.Complete(repeat, log));
        Assert.True(log.IsCleared(1));
        Assert.False(log.IsCleared(2));
        Assert.Empty(log.Active);
        Assert.False(QuestRules.Complete(unique, log));
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(repeat, Hero(), log));
        Assert.Equal(QuestOffer.Cleared, QuestRules.CheckOffer(unique, Hero(), log));
    }

    [Fact]
    public void AbandoningFreesTheSlotUnlessForbidden()
    {
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        QuestDef locked = Make(2, QuestKind.ClearMap, data: new[] { 31 });
        locked.CantGiveUp = 1;
        var log = new QuestLog();
        QuestRules.Accept(q, Hero(), log, out _);
        QuestRules.Accept(locked, Hero(), log, out _);
        Assert.False(QuestRules.Abandon(locked, log));
        Assert.True(QuestRules.Abandon(q, log));
        Assert.False(QuestRules.Abandon(q, log));
        Assert.False(log.IsCleared(1));
        Assert.Single(log.Active);
        Assert.Equal(QuestOffer.Ok, QuestRules.CheckOffer(q, Hero(), log));
    }

    // ------------------------------------------------------------------ 任务物品掉落

    [Fact]
    public void KillDropsRollBelowTheRate()
    {
        QuestDef q = Make(1, QuestKind.HuntMonster, data: new[] { -1, -1, 100, 5 });
        q.MonsterDrops.Add(new QuestMonsterDrop(100, 0, -1, -1, 900, 2, 30, -1));
        var log = new QuestLog();
        QuestRules.Accept(q, Hero(), log, out _);
        Func<int, QuestDef> find = Finder(q);

        List<QuestItem> hit = QuestTracker.RollKillDrops(log, find, 7, 0, 100, 1, () => 29, null);
        QuestItem drop = Assert.Single(hit);
        Assert.Equal(900, drop.Id);
        Assert.Equal(2, drop.Count);
        Assert.Empty(QuestTracker.RollKillDrops(log, find, 7, 0, 100, 1, () => 30, null));
        Assert.Empty(QuestTracker.RollKillDrops(log, find, 7, 0, 101, 1, () => 0, null));
    }

    [Fact]
    public void KillDropsHonourDungeonDifficultyAndEnemyType()
    {
        QuestDef q = Make(1, QuestKind.HuntEnemy, data: new[] { -1, -1, 100, 2, 5 });
        q.MonsterDrops.Add(new QuestMonsterDrop(100, 2, 7, 1, 900, 1, 100, -1));
        var log = new QuestLog();
        QuestRules.Accept(q, Hero(), log, out _);
        Func<int, QuestDef> find = Finder(q);

        Assert.Single(QuestTracker.RollKillDrops(log, find, 7, 1, 100, 2, () => 99, null));
        Assert.Empty(QuestTracker.RollKillDrops(log, find, 7, 1, 100, 1, () => 0, null));
        Assert.Empty(QuestTracker.RollKillDrops(log, find, 8, 1, 100, 2, () => 0, null));
        Assert.Empty(QuestTracker.RollKillDrops(log, find, 7, 0, 100, 2, () => 0, null));
    }

    [Fact]
    public void SeekingQuestsStopDroppingOnceTheyHaveEnough()
    {
        QuestDef q = Make(1, QuestKind.Seeking, data: new[] { 900, 3 });
        q.MonsterDrops.Add(new QuestMonsterDrop(100, 0, -1, -1, 900, 1, 100, -1));
        var log = new QuestLog();
        QuestRules.Accept(q, Hero(), log, out _);
        Func<int, QuestDef> find = Finder(q);
        Assert.Single(QuestTracker.RollKillDrops(log, find, 7, 0, 100, 1, () => 0, id => 2));
        Assert.Empty(QuestTracker.RollKillDrops(log, find, 7, 0, 100, 1, () => 0, id => 3));
    }

    [Fact]
    public void DropsOnlyComeFromActiveQuests()
    {
        QuestDef q = Make(1, QuestKind.Seeking, data: new[] { 900, 3 });
        q.MonsterDrops.Add(new QuestMonsterDrop(100, 0, -1, -1, 900, 1, 100, -1));
        Assert.Empty(QuestTracker.RollKillDrops(new QuestLog(), Finder(q), 7, 0, 100, 1, () => 0, null));
    }

    [Fact]
    public void ClearDropsMatchDungeonAndDifficulty()
    {
        QuestDef q = Make(1, QuestKind.Seeking, data: new[] { 903, 10 });
        q.ClearDrops.Add(new QuestClearDrop(9, -1, 903, 4, 170, -1));
        q.ClearDrops.Add(new QuestClearDrop(10, 2, 903, 1, 100, -1));
        var log = new QuestLog();
        QuestRules.Accept(q, Hero(), log, out _);
        Func<int, QuestDef> find = Finder(q);

        QuestItem any = Assert.Single(QuestTracker.RollClearDrops(log, find, 9, 3, () => 99, null));
        Assert.Equal(4, any.Count);
        Assert.Empty(QuestTracker.RollClearDrops(log, find, 10, 1, () => 0, null));
        Assert.Single(QuestTracker.RollClearDrops(log, find, 10, 2, () => 0, null));
        Assert.Empty(QuestTracker.RollClearDrops(log, find, 9, 3, () => 0, id => 10));
    }

    // ------------------------------------------------------------------ 奖励

    [Fact]
    public void ParametersReadWeightsPenaltiesAndTables()
    {
        QuestParameters p = Params();
        Assert.Equal(110, p.Weight('S'));
        Assert.Equal(90, p.Weight('A'));
        Assert.Equal(100, p.Weight('Q'));
        Assert.Equal(100, p.Weight('\0'));
        Assert.Equal(80, p.GreenPenalty);
        Assert.Equal(30, p.GreyPenalty);
        Assert.Equal(100, p.ExpTableLength);
        Assert.Equal(100, p.GoldTableLength);
        Assert.Equal(1000, p.ExpAt(10));
        Assert.Equal(100, p.GoldAt(10));
    }

    [Fact]
    public void TableLookupClampsAndEmptyParametersAreNeutral()
    {
        QuestParameters p = Params();
        Assert.Equal(0, p.ExpAt(-5));
        Assert.Equal(9900, p.ExpAt(1000));
        Assert.Equal(990, p.GoldAt(1000));

        QuestParameters empty = QuestParameters.Parse(null);
        Assert.Equal(0, empty.ExpAt(10));
        Assert.Equal(0, empty.GoldAt(10));
        Assert.Equal(100, empty.Weight('S'));
        Assert.Equal(100, empty.LevelPenalty(50));
    }

    [Fact]
    public void LevelPenaltyThresholds()
    {
        QuestParameters p = Params();
        Assert.Equal(100, p.LevelPenalty(-20));
        Assert.Equal(100, p.LevelPenalty(0));
        Assert.Equal(100, p.LevelPenalty(6));
        Assert.Equal(80, p.LevelPenalty(7));
        Assert.Equal(80, p.LevelPenalty(11));
        Assert.Equal(30, p.LevelPenalty(12));
        Assert.Equal(30, p.LevelPenalty(80));
    }

    [Fact]
    public void ExpIsTableTimesWeightTimesPenalty()
    {
        QuestParameters p = Params();
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.MinLevel = 10;
        q.DifficultyLetter = 'S';
        Assert.Equal(1100, QuestRules.ComputeReward(q, Hero(10), p, -1).Exp);
        Assert.Equal(1100, QuestRules.ComputeReward(q, Hero(16), p, -1).Exp);
        Assert.Equal(880, QuestRules.ComputeReward(q, Hero(17), p, -1).Exp);
        Assert.Equal(880, QuestRules.ComputeReward(q, Hero(21), p, -1).Exp);
        Assert.Equal(330, QuestRules.ComputeReward(q, Hero(22), p, -1).Exp);
        Assert.Equal(1100, QuestRules.ComputeReward(q, Hero(3), p, -1).Exp);
        q.DifficultyLetter = '\0';
        Assert.Equal(1000, QuestRules.ComputeReward(q, Hero(10), p, -1).Exp);
    }

    [Fact]
    public void ExpDividesStepByStep()
    {
        QuestParameters p = Params();
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.MinLevel = 3;
        q.DifficultyLetter = 'A';
        // 300 × 90 / 100 = 270;再 × 80 / 100 = 216(逐步整除)
        Assert.Equal(216, QuestRules.ComputeReward(q, Hero(10), p, -1).Exp);
    }

    [Fact]
    public void ExpUsesHeroLevelWhenFlagged()
    {
        QuestParameters p = Params();
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.MinLevel = 10;
        q.DifficultyLetter = 'S';
        q.ExpUsesHeroLevel = true;
        // 取表用角色等级 17(1700)× 110 / 100 = 1870,惩罚仍按等级差 7 → × 80 / 100 = 1496
        Assert.Equal(1496, QuestRules.ComputeReward(q, Hero(17), p, -1).Exp);
    }

    [Fact]
    public void EventQuestsUseHeroLevelAndSkipThePenalty()
    {
        QuestParameters p = Params();
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.MinLevel = 10;
        q.DifficultyLetter = 'S';
        q.IsEvent = true;
        q.Rewards.Add(new QuestItem(0, 1));
        QuestReward r = QuestRules.ComputeReward(q, Hero(30), p, -1);
        Assert.Equal(3300, r.Exp);
        Assert.Equal(300, r.Gold);
    }

    [Fact]
    public void RepeatableQuestsGiveNoExp()
    {
        QuestParameters p = Params();
        QuestDef q = Make(1, QuestKind.ClearMap, QuestGrade.NormalRepeat, data: new[] { 30 });
        q.MinLevel = 10;
        q.Rewards.Add(new QuestItem(0, 1));
        QuestReward r = QuestRules.ComputeReward(q, Hero(10), p, -1);
        Assert.Equal(0, r.Exp);
        Assert.Equal(100, r.Gold);
        // 紧急类算可重复(完成后能再接),但经验照给(服务端只对"可重复"这一个分类给 0 经验)
        QuestDef urgent = Make(2, QuestKind.ClearMap, QuestGrade.Urgent, data: new[] { 30 });
        urgent.MinLevel = 10;
        Assert.Equal(1000, QuestRules.ComputeReward(urgent, Hero(10), p, -1).Exp);
    }

    [Fact]
    public void GoldNeedsTheZeroIdMarkerAndScalesWithTheMultiple()
    {
        QuestParameters p = Params();
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.MinLevel = 10;
        Assert.Equal(0, QuestRules.ComputeReward(q, Hero(10), p, -1).Gold);

        q.Rewards.Add(new QuestItem(0, 99999));
        q.Rewards.Add(new QuestItem(500, 2));
        q.GoldPercent = 150;
        QuestReward r = QuestRules.ComputeReward(q, Hero(10), p, -1);
        // 金币表[10] = 100,惩罚 100,倍率 150 → 150;数量 99999 不是金额
        Assert.Equal(150, r.Gold);
        QuestItem item = Assert.Single(r.Items);
        Assert.Equal(500, item.Id);
        Assert.Equal(2, item.Count);
        // 惩罚在倍率之前
        Assert.Equal(120, QuestRules.ComputeReward(q, Hero(17), p, -1).Gold);
    }

    [Fact]
    public void SelectionRewardAddsTheChosenEntry()
    {
        QuestParameters p = Params();
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.Rewards.Add(new QuestItem(500, 1));
        q.SelectRewards.Add(new QuestItem(600, 1));
        q.SelectRewards.Add(new QuestItem(601, 2));
        Assert.Single(QuestRules.ComputeReward(q, Hero(10), p, -1).Items);
        Assert.Single(QuestRules.ComputeReward(q, Hero(10), p, 5).Items);
        QuestReward second = QuestRules.ComputeReward(q, Hero(10), p, 1);
        Assert.Equal(2, second.Items.Count);
        Assert.Equal(601, second.Items[1].Id);
        Assert.Equal(2, second.Items[1].Count);
    }

    [Fact]
    public void NonItemRewardTypesGiveNoGoldOrFixedItems()
    {
        QuestParameters p = Params();
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.RewardType = "[title]";
        q.Rewards.Add(new QuestItem(0, 1));
        q.Rewards.Add(new QuestItem(500, 1));
        QuestReward r = QuestRules.ComputeReward(q, Hero(10), p, -1);
        Assert.Equal(0, r.Gold);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void FixedRewardsAreFilteredByJobAndGrowType()
    {
        QuestParameters p = Params();
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.Rewards.Add(new QuestItem(500, 1));
        q.Rewards.Add(new QuestItem(501, 1, 0, -1));
        q.Rewards.Add(new QuestItem(502, 1, 1, -1));
        q.Rewards.Add(new QuestItem(503, 1, 0, 2));
        q.Rewards.Add(new QuestItem(-1, 1));

        QuestReward swordman = QuestRules.ComputeReward(q, Hero(10, 0, 0), p, -1);
        Assert.Equal(new[] { 500, 501 }, swordman.Items.Select(i => i.Id).ToArray());
        QuestReward advanced = QuestRules.ComputeReward(q, Hero(10, 0, 2), p, -1);
        Assert.Equal(new[] { 500, 501, 503 }, advanced.Items.Select(i => i.Id).ToArray());
        QuestReward other = QuestRules.ComputeReward(q, Hero(10, 1, 0), p, -1);
        Assert.Equal(new[] { 500, 502 }, other.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void SelectionIndexCountsOnlyTheEntriesTheHeroSees()
    {
        QuestParameters p = Params();
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.SelectRewards.Add(new QuestItem(600, 1, 1, -1));
        q.SelectRewards.Add(new QuestItem(601, 1, 0, -1));
        q.SelectRewards.Add(new QuestItem(602, 1));
        q.SelectRewards.Add(new QuestItem(603, 1, 0, 4));

        List<QuestItem> seen = QuestRules.SelectableRewards(q, Hero(10, 0, 0));
        Assert.Equal(new[] { 601, 602 }, seen.Select(i => i.Id).ToArray());
        // 下标 0 是这个角色看到的第一项(601),不是脚本里的第一项(600)
        Assert.Equal(601, Assert.Single(QuestRules.ComputeReward(q, Hero(10, 0, 0), p, 0).Items).Id);
        Assert.Equal(602, Assert.Single(QuestRules.ComputeReward(q, Hero(10, 0, 0), p, 1).Items).Id);
        Assert.Empty(QuestRules.ComputeReward(q, Hero(10, 0, 0), p, 2).Items);
        Assert.Equal(3, QuestRules.SelectableRewards(q, Hero(10, 0, 4)).Count);
        Assert.Equal(2, QuestRules.SelectableRewards(q, Hero(10, 1, 0)).Count);
    }

    [Fact]
    public void QuestPointAndPieceAreReportedNotApplied()
    {
        QuestDef q = Make(1, QuestKind.ClearMap, data: new[] { 30 });
        q.QuestPoint = 3;
        q.QuestPiece = 2;
        QuestReward r = QuestRules.ComputeReward(q, Hero(10), Params(), -1);
        Assert.Equal(3, r.QuestPoint);
        Assert.Equal(2, r.QuestPiece);
    }

    // ------------------------------------------------------------------ 目录

    private static GameData CatalogData(bool withParameters)
    {
        var fx = new Fixture();
        fx.List("n_quest/quest.lst", (1, "a.qst"), (2, "b.qst"), (3, "missing.qst"), (4, "c.qst"));
        fx.Tokens("n_quest/a.qst", w => w.Tag("[grade]").Str("[epic]").Tag("[type]").Str("[meet npc]")
            .Tag("[npc index]").Int(4).Tag("[complete npc index]").Int(8).Tag("[int data]").Int(9).Tag("[/int data]"));
        fx.Tokens("n_quest/b.qst", w => w.Tag("[grade]").Str("[common unique]").Tag("[type]").Str("[hunt monster]")
            .Tag("[npc index]").Int(4).Tag("[complete npc index]").Int(4).Tag("[int data]").Ints(7, 1, 100, 3).Tag("[/int data]"));
        fx.Tokens("n_quest/c.qst", w => w.Tag("[grade]").Str("[normaly repeat]").Tag("[type]").Str("[seeking]")
            .Tag("[complete npc index]").Int(-1).Tag("[int data]").Ints(900, 3).Tag("[/int data]"));
        if (withParameters)
        {
            fx.Tokens("n_quest/questparameter.etc", w => w.Tag("[green level penalty]").Int(55).Tag("[exp reward table]").Ints(0, 10, 20)
                .Tag("[quest parameter end]"));
        }
        return new GameData(fx.Build());
    }

    [Fact]
    public void CatalogListsAndParsesQuests()
    {
        var catalog = new QuestCatalog(CatalogData(false));
        Assert.Equal(4, catalog.ListedCount);
        Assert.Equal(new[] { 1, 2, 3, 4 }, catalog.Ids());
        QuestDef a = catalog.Get(1);
        Assert.Equal(QuestKind.MeetNpc, a.Kind);
        Assert.Equal("n_quest/a.qst", a.Path);
        Assert.Null(catalog.Get(3));
        Assert.Null(catalog.Get(99));
        Assert.Same(a, catalog.Get(1));
        Assert.Equal(3, catalog.All().Count);
    }

    [Fact]
    public void CatalogIndexesByOfferingAndHandInNpc()
    {
        var catalog = new QuestCatalog(CatalogData(false));
        Assert.Equal(new[] { 1, 2 }, catalog.OfferedBy(4).Select(q => q.Id).ToArray());
        Assert.Empty(catalog.OfferedBy(99));
        // 拜访类的交付 NPC 是被拜访的那个(9),不是 [complete npc index](8)
        QuestDef meet = Assert.Single(catalog.CompletedAt(9));
        Assert.Equal(1, meet.Id);
        Assert.Empty(catalog.CompletedAt(8));
        Assert.Equal(new[] { 2 }, catalog.CompletedAt(4).Select(q => q.Id).ToArray());
        // 没有发布 NPC 的任务(-1)不进索引
        Assert.Empty(catalog.OfferedBy(-1));
        Assert.Empty(catalog.CompletedAt(-1));
    }

    [Fact]
    public void CatalogReadsParametersLazilyAndToleratesAMissingScript()
    {
        QuestParameters p = new QuestCatalog(CatalogData(true)).Parameters;
        Assert.Equal(55, p.GreenPenalty);
        Assert.Equal(10, p.ExpAt(1));

        QuestParameters none = new QuestCatalog(CatalogData(false)).Parameters;
        Assert.Equal(0, none.ExpAt(1));
        Assert.Equal(100, none.LevelPenalty(8));
    }

    [Fact]
    public void CatalogWithoutAListIsEmpty()
    {
        var catalog = new QuestCatalog(new GameData(new MemoryScriptSource()));
        Assert.Equal(0, catalog.ListedCount);
        Assert.Empty(catalog.All());
        Assert.Empty(catalog.OfferedBy(1));
    }

    // ------------------------------------------------------------------ 整条流程

    [Fact]
    public void AHuntQuestRunsFromOfferToReward()
    {
        QuestParameters p = Params();
        QuestDef q = Make(1, QuestKind.HuntMonster, QuestGrade.CommonUnique, data: new[] { 7, -1, 100, 2 });
        q.MinLevel = 10;
        q.DifficultyLetter = 'A';
        q.Rewards.Add(new QuestItem(0, 1));
        q.Rewards.Add(new QuestItem(500, 1));
        var log = new QuestLog();
        Func<int, QuestDef> find = Finder(q);

        Assert.Equal(QuestOffer.Level, QuestRules.CheckOffer(q, Hero(9), log));
        ActiveQuest a = QuestRules.Accept(q, Hero(10), log, out _);
        QuestTracker.OnKill(log, find, 7, 0, 100, 1);
        Assert.False(QuestRules.IsReady(q, a, null));
        QuestTracker.OnKill(log, find, 7, 0, 100, 1);
        Assert.True(QuestRules.IsReady(q, a, null));

        QuestReward r = QuestRules.ComputeReward(q, Hero(10), p, -1);
        Assert.Equal(900, r.Exp);
        Assert.Equal(100, r.Gold);
        Assert.Single(r.Items);
        Assert.True(QuestRules.Complete(q, log));
        Assert.Equal(QuestOffer.Cleared, QuestRules.CheckOffer(q, Hero(10), log));
    }

    [Fact]
    public void ALogCanBeClearedAndRefilled()
    {
        var log = new QuestLog();
        log.Add(1, new[] { 1 });
        log.MarkCleared(2);
        log.Clear();
        Assert.Empty(log.Active);
        Assert.Empty(log.Cleared);
        Assert.True(log.HasFreeSlot);
        Assert.Null(log.Find(1));
    }

    [Fact]
    public void DuplicateAddIsRejected()
    {
        var log = new QuestLog();
        Assert.NotNull(log.Add(1, new[] { 1 }));
        Assert.Null(log.Add(1, new[] { 5 }));
        Assert.Equal(new[] { 1 }, log.Find(1).Remaining);
        Assert.False(log.Remove(77));
    }

    [Fact]
    public void ActiveQuestWithoutCountersIsDone()
    {
        Assert.True(new ActiveQuest(1, null).CountersDone);
        Assert.False(new ActiveQuest(1, new[] { 0, 1 }).CountersDone);
        Assert.True(new ActiveQuest(1, new[] { 0, 0 }).CountersDone);
    }

    // ------------------------------------------------------------------ 背包的试放(交任务先试再真放)

    private static ItemInfo Single(int code)
    {
        return new ItemInfo(code, "stackable/test/s" + code + ".stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste, StackLimit = 1 };
    }

    private static KeyValuePair<ItemInfo, int> Give(int code, int count = 1) => new KeyValuePair<ItemInfo, int>(Single(code), count);

    [Fact]
    public void ACloneIsIndependentOfTheOriginal()
    {
        var bag = new Inventory(3);
        bag.Add(Single(1), 1);
        bag.Add(Single(2), 1);
        Inventory copy = bag.Clone();
        Assert.Equal(bag.TotalCount, copy.TotalCount);
        Assert.Equal(bag.Capacity, copy.Capacity);
        Assert.Equal(1, copy.CountOf(2));
        Assert.Equal(1, copy.Add(Single(3), 1));
        Assert.True(copy.Remove(1));
        Assert.Equal(2, bag.TotalCount);                         // 原来的背包没动
        Assert.Equal(1, bag.CountOf(1));
        Assert.Equal(0, bag.CountOf(3));
        Assert.Equal(2, copy.TotalCount);
    }

    [Fact]
    public void CanSwapChecksRoomAfterTheTakenItemsAreGone()
    {
        var bag = new Inventory(2);
        bag.Add(Single(1), 1);
        bag.Add(Single(2), 1);                                   // 消耗品页满了
        Assert.False(bag.CanSwap(null, new[] { Give(3) }));
        Assert.True(bag.CanSwap(new[] { new KeyValuePair<int, int>(1, 1) }, new[] { Give(3) }));
        Assert.False(bag.CanSwap(new[] { new KeyValuePair<int, int>(1, 1) }, new[] { Give(3), Give(4) }));
        Assert.True(bag.CanSwap(new[] { new KeyValuePair<int, int>(1, 1), new KeyValuePair<int, int>(2, 1) }, new[] { Give(3), Give(4) }));
        Assert.Equal(2, bag.TotalCount);                         // 试放不动真的背包
        Assert.Equal(1, bag.CountOf(1));
    }

    [Fact]
    public void CanSwapStacksOntoExistingStacksAndIgnoresJunkEntries()
    {
        var bag = new Inventory(1);
        var many = new ItemInfo(9, "stackable/test/s9.stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste, StackLimit = 10 };
        bag.Add(many, 4);                                        // 只有一格,里面有 4 件(上限 10)
        Assert.True(bag.CanSwap(null, new[] { new KeyValuePair<ItemInfo, int>(many, 6) }));
        Assert.False(bag.CanSwap(null, new[] { new KeyValuePair<ItemInfo, int>(many, 7) }));
        Assert.True(bag.CanSwap(new[] { new KeyValuePair<int, int>(9, 100) }, new[] { Give(5) }));   // 拿的数量多于有的:当作拿光
        Assert.True(bag.CanSwap(null, new[] { new KeyValuePair<ItemInfo, int>(null, 5), new KeyValuePair<ItemInfo, int>(many, 0) }));
        Assert.True(bag.CanSwap(null, null));
    }
}
