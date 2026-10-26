using Dnf.Game;
using Dnf.Render;
using static Dnf.Play.Tests.DungeonSessionTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 副本通关结算(M32,证据 E-124):评分里基础分的估计(<see cref="ScoreEstimate"/>)、评分与通关经验的汇总(<see cref="DungeonResult"/>)、
/// 评分字母图的摆放(<see cref="ResultArt"/>)、首领清空时的结算与发经验(<see cref="DungeonSession.Result"/>)、结算页的文字行。
/// 门槛、平均用时、奖励档、比例全是合成的:门槛 90、80、70、60、50、40、30、20,用时奖励档 (0, 1)、(10000, 2)、(20000, 3)、(40000, 5),
/// 副本 7 单人的平均用时 100000 毫秒,评分加成比例 0.1 到 0.5(档位 0 到 4),基础经验 400(1 级怪物)。
/// </summary>
public class DungeonResultTests
{
    private static readonly int[] Thresholds = { 90, 80, 70, 60, 50, 40, 30, 20 };

    private static RankSystem Rank(int[] thresholds = null)
    {
        var steps = new[] { new TimeBonusStep(0, 1), new TimeBonusStep(10000, 2), new TimeBonusStep(20000, 3), new TimeBonusStep(40000, 5) };
        var averages = new[] { (7, 1, new[] { 100000, 110000, 120000, 130000, 140000 }) };
        return new RankSystem(thresholds ?? Thresholds, steps, averages);
    }

    private static ExpRates Rates() => new ExpRates(null, null, null, new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f });

    private static HeroProgress Progress(int level = 1) => ExpTests.Progress(level: level, mobExp: 400, mobLevel: 1, rates: Rates());

    private static RunStats Stats(int clearMs = 95000, int kills = 5, int comboKills = 5, int maxCombo = 12, int hits = 0, int revives = 0)
    {
        return new RunStats(clearMs, kills, comboKills, maxCombo, hits, revives);
    }

    private static (int X, int Y)[] Monsters(int cx, int cy)
    {
        return cx == 0 && cy == 0 ? new[] { (340, 100), (420, 120) }
            : cx == 1 && cy == 0 ? new[] { (300, 200) }
            : cx == 1 && cy == 1 ? new[] { (400, 150), (500, 180) }
            : None();
    }

    // ------------------------------------------------------------------ 基础分的估计

    [Fact]
    public void TheScoreEstimateStartsAtFullMarksAndLosesPointsForHitsRevivesAndKillsOutsideCombos()
    {
        Assert.Equal(ScoreEstimate.Full, ScoreEstimate.BaseScore(Stats()));
        Assert.Equal(90, ScoreEstimate.BaseScore(Stats(hits: 5)));                          // 每被打中一次 −2
        Assert.Equal(85, ScoreEstimate.BaseScore(Stats(revives: 1)));                       // 每枚复活币 −15
        Assert.Equal(95, ScoreEstimate.BaseScore(Stats(kills: 10, comboKills: 5)));          // 一半的击杀不在连击里:−10 × 1/2
        Assert.Equal(90, ScoreEstimate.BaseScore(Stats(comboKills: 0)));                    // 全都不在连击里:−10
        Assert.Equal(100 - 8 - 15 - 6, ScoreEstimate.BaseScore(Stats(kills: 5, comboKills: 2, hits: 4, revives: 1)));
    }

    [Fact]
    public void TheEstimateCapsEachPenaltyAndNeverLeavesTheZeroToFullRange()
    {
        Assert.Equal(100 - ScoreEstimate.HitPenaltyCap, ScoreEstimate.BaseScore(Stats(hits: 20)));
        Assert.Equal(100 - ScoreEstimate.HitPenaltyCap, ScoreEstimate.BaseScore(Stats(hits: 1000)));
        Assert.Equal(100 - ScoreEstimate.RevivePenaltyCap, ScoreEstimate.BaseScore(Stats(revives: 3)));
        Assert.Equal(100 - ScoreEstimate.RevivePenaltyCap, ScoreEstimate.BaseScore(Stats(revives: 99)));
        int worst = ScoreEstimate.BaseScore(Stats(hits: int.MaxValue, revives: int.MaxValue, comboKills: 0));
        Assert.Equal(100 - ScoreEstimate.HitPenaltyCap - ScoreEstimate.RevivePenaltyCap - ScoreEstimate.ComboPenaltyMax, worst);
        Assert.InRange(worst, 0, ScoreEstimate.Full);
    }

    [Fact]
    public void NonsenseCountersAreTreatedAsZeroOrClamped()
    {
        Assert.Equal(100, ScoreEstimate.BaseScore(Stats(hits: -3, revives: -1)));
        Assert.Equal(100, ScoreEstimate.BaseScore(Stats(kills: 5, comboKills: 99)));           // 连击杀不会比击杀多
        Assert.Equal(90, ScoreEstimate.BaseScore(Stats(kills: 5, comboKills: -4)));            // 负的连击杀当 0
        Assert.Equal(100, ScoreEstimate.BaseScore(Stats(kills: 0, comboKills: 0)));            // 没打死怪物:没有这一项扣分
    }

    // ------------------------------------------------------------------ 评分与通关经验的汇总

    [Theory]
    [InlineData(0, 0, 5, 100, 101, 90, "SSS", 0, 4)]
    [InlineData(5, 0, 5, 90, 91, 90, "SSS", 0, 4)]
    [InlineData(6, 0, 5, 88, 89, 80, "SS", 1, 3)]
    [InlineData(11, 0, 5, 78, 79, 70, "S", 2, 2)]
    [InlineData(16, 0, 5, 68, 69, 60, "A", 3, 1)]
    [InlineData(10, 2, 5, 50, 51, 50, "B", 4, 0)]
    [InlineData(20, 1, 5, 45, 46, 40, "C", 5, -1)]
    [InlineData(20, 2, 5, 30, 31, 30, "D", 6, -1)]
    [InlineData(20, 2, 0, 20, 21, 20, "E", 7, -1)]
    [InlineData(20, 3, 5, 15, 16, 0, "F", 8, -1)]
    public void TheGradeLadderFollowsTheEstimatePlusTheTimeBonus(int hits, int revives, int comboKills, int baseScore, int point, int value, string letter, int frame, int bonusIndex)
    {
        // 95000 毫秒:比平均快 5000,过用时奖励的第一档,加 1 分
        DungeonResult r = DungeonResult.Compute(Stats(comboKills: comboKills, hits: hits, revives: revives), Rank(), Progress(), 7, 1, 0, 1f);
        Assert.Equal((100000, 1), (r.AverageMs, r.TimeBonus));
        Assert.Equal((baseScore, point, value), (r.BaseScore, r.RankPoint, r.RankValue));
        Assert.Equal((letter, frame, 8 - frame, bonusIndex), (r.Letter, r.Frame, r.Grade, r.BonusIndex));
        Assert.True(r.HasRank);
    }

    [Fact]
    public void ASlowRunGetsNoTimeBonusAndTheClearExpFollowsTheServerOrder()
    {
        DungeonResult fast = DungeonResult.Compute(Stats(95000), Rank(), Progress(), 7, 1, 0, 1f);
        // 5 次击杀 × 基础经验 400 ÷ 2 = 1000;等级差 0(1 级对 1 级)倍率 1,单人;最高档(4)的评分加成比例 0.5
        Assert.Equal((1000L, 1000L, 500L, 1500L), (fast.ExpDungeonTotal, fast.ExpBase, fast.ExpRankBonus, fast.ExpTotal));
        Assert.False(fast.Granted);                                                       // 只算不发
        Assert.Equal((0, 0, 0), (fast.LevelBefore, fast.LevelAfter, fast.LevelsGained));

        DungeonResult slow = DungeonResult.Compute(Stats(150000), Rank(), Progress(), 7, 1, 0, 1f);
        Assert.Equal((0, 100), (slow.TimeBonus, slow.RankPoint));                          // 比平均慢:没有用时分,评分点 = 基础分
        Assert.Equal(90, slow.RankValue);

        DungeonResult heavy = DungeonResult.Compute(Stats(95000), Rank(), Progress(), 7, 1, 0, 2f);
        Assert.Equal((2000L, 2000L, 1000L), (heavy.ExpDungeonTotal, heavy.ExpBase, heavy.ExpRankBonus));   // 副本经验权重翻倍
    }

    [Fact]
    public void ResultOnlyComputesAndDoesNotTouchTheHeroProgress()
    {
        HeroProgress progress = Progress();
        DungeonResult.Compute(Stats(), Rank(), progress, 7, 1, 0, 1f);
        Assert.Equal((1, 0, 0L, 0), (progress.Level, progress.Exp, progress.TotalGained, progress.Version));
    }

    [Fact]
    public void ADungeonNotInTheListHasNoAverageSoNoTimeBonus()
    {
        DungeonResult r = DungeonResult.Compute(Stats(1000), Rank(), Progress(), -1, 1, 0, 1f);
        Assert.Equal((0, 0), (r.AverageMs, r.TimeBonus));
        Assert.Equal(-1, r.DungeonId);
        DungeonResult unknown = DungeonResult.Compute(Stats(1000), Rank(), Progress(), 99, 1, 0, 1f);   // 有编号但没有这个副本的平均用时
        Assert.Equal((0, 0), (unknown.AverageMs, unknown.TimeBonus));
    }

    [Fact]
    public void WithoutRankDataThereIsNoGradeAndNoBonusButTheBaseClearExpStays()
    {
        DungeonResult r = DungeonResult.Compute(Stats(), null, Progress(), 7, 1, 0, 1f);
        Assert.False(r.HasRank);
        Assert.Equal(("F", 8, -1), (r.Letter, r.Frame, r.BonusIndex));
        Assert.Equal((1000L, 0L), (r.ExpBase, r.ExpRankBonus));
        DungeonResult empty = DungeonResult.Compute(Stats(), RankSystem.Empty, Progress(), 7, 1, 0, 1f);
        Assert.Equal((false, "F", 1000L, 0L), (empty.HasRank, empty.Letter, empty.ExpBase, empty.ExpRankBonus));
    }

    [Fact]
    public void WithoutProgressOrAStandardLevelOrKillsTheClearExpIsZero()
    {
        DungeonResult noProgress = DungeonResult.Compute(Stats(), Rank(), null, 7, 1, 0, 1f);
        Assert.Equal((0L, 0L, 0L, 0L), (noProgress.ExpDungeonTotal, noProgress.ExpBase, noProgress.ExpRankBonus, noProgress.ExpTotal));
        Assert.Equal("SSS", noProgress.Letter);                                           // 评分照样有
        DungeonResult noLevel = DungeonResult.Compute(Stats(), Rank(), Progress(), 7, 0, 0, 1f);
        Assert.Equal(0L, noLevel.ExpTotal);
        DungeonResult noKills = DungeonResult.Compute(Stats(kills: 0, comboKills: 0), Rank(), Progress(), 7, 1, 0, 1f);
        Assert.Equal(0L, noKills.ExpTotal);
    }

    [Fact]
    public void TheHeroLevelAtTheClearSetsTheLevelGapOfTheMemberShare()
    {
        // 主角 30 级、副本标准等级 1:等级差倍率比 1 小(表在 docs/exp.md),分到的基础经验比整队总经验少
        DungeonResult low = DungeonResult.Compute(Stats(), Rank(), Progress(1), 7, 1, 0, 1f);
        DungeonResult high = DungeonResult.Compute(Stats(), Rank(), Progress(30), 7, 1, 0, 1f);
        Assert.Equal(low.ExpDungeonTotal, high.ExpDungeonTotal);
        Assert.True(high.ExpBase < low.ExpBase, high.ExpBase + " 应小于 " + low.ExpBase);
    }

    // ------------------------------------------------------------------ 评分字母图

    private sealed class FakeSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) => Frames.TryGetValue((imageName, frame), out sprite!);
    }

    private static SpriteFrame Block(int width, int height, int offsetX, int offsetY)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 200;
            rgba[i + 3] = 255;
        }
        return new SpriteFrame(width, height, offsetX, offsetY, width, height, rgba);
    }

    [Fact]
    public void TheRankLetterIsPlacedByItsFrameOffsetAndMissingFramesGiveNothing()
    {
        var sprites = new FakeSprites();
        sprites.Frames[(ResultArt.RankLetterImage, 3)] = Block(150, 100, -5, 7);
        SceneSprite letter = ResultArt.PlaceRankLetter(sprites, 3, 24, 9);
        Assert.NotNull(letter);
        Assert.Equal((24 - 5, ResultArt.LetterTop + 7, ResultArt.Tag, OverlayArt.UiRank), (letter.ScreenX, letter.ScreenY, letter.AnimationPath, letter.LayerRank));
        Assert.Equal((150, 100), (letter.Frame.Width, letter.Frame.Height));
        Assert.Null(ResultArt.PlaceRankLetter(sprites, 4, 24, 9));                         // 这一帧没有
        Assert.Null(ResultArt.PlaceRankLetter(sprites, -1, 24, 9));
        Assert.Null(ResultArt.PlaceRankLetter(sprites, ResultArt.FrameCount, 24, 9));      // 越界
        Assert.Null(ResultArt.PlaceRankLetter(null, 3, 24, 9));
        Assert.False(ResultArt.TryRankLetter(new FakeSprites(), 0, out SpriteFrame none));
        Assert.Null(none);
    }

    [Fact]
    public void TheLetterImageIsTheNineFrameRankLetterPackAndTheTextStartsBelowTheLetter()
    {
        Assert.Equal("sprite/interface2/result_ver2.0/rank_letter/rank_letter.img", ResultArt.RankLetterImage);
        Assert.Equal(RankSystem.GradeCount, ResultArt.FrameCount);
        Assert.True(ResultArt.LetterTop < ResultArt.TextTop);
        Assert.True(ResultArt.TextTop > DungeonSession.RewardPanelTop);                    // 有字母时,文字行比没有字母时靠下
    }

    // ------------------------------------------------------------------ 会话:首领清空时结算

    [Fact]
    public void TheCombatCountersFeedTheRunStats()
    {
        HeroProgress progress = Progress();
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40, progress: progress);
        DungeonSession s = d.Session;
        Assert.Equal((0, 0), (s.MaxCombo, s.ComboKills));
        new DungeonBot(true).RunUntil(s, 600_000);
        Assert.True(s.BossCleared);
        Assert.True(s.MaxCombo >= 1);
        Assert.InRange(s.ComboKills, 0, s.Kills);
        Assert.Equal(d.Rooms.Values.Max(r => r.MaxCombo), s.MaxCombo);                    // 各房间取最大
        Assert.Equal(d.Rooms.Values.Sum(r => r.ComboKills), s.ComboKills);                // 各房间求和
    }

    [Fact]
    public void ClearingTheBossRoomSettlesOnceAndGrantsTheClearExpToTheHero()
    {
        HeroProgress progress = Progress();
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40, progress: progress);
        DungeonSession s = d.Session;
        s.UseRanking(Rank(), 7, 1);
        Assert.Null(s.Result);
        Assert.Equal(0L, s.ClearExpGranted);
        new DungeonBot(true).RunUntil(s, 600_000);
        Assert.True(s.BossCleared);

        DungeonResult r = s.Result;
        Assert.NotNull(r);
        Assert.Equal(5, r.Stats.Kills);
        Assert.Equal((7, 1, 0, 1), (r.DungeonId, r.StandardLevel, r.Difficulty, r.Members));
        Assert.Equal((s.MaxCombo, s.ComboKills), (r.Stats.MaxCombo, r.Stats.ComboKills));
        Assert.Equal(s.HeroHits, r.Stats.HitsTaken);
        Assert.True(r.Stats.ClearMs > 0);
        Assert.True(r.HasRank);
        Assert.True(r.Granted);
        Assert.True(r.ExpBase > 0);
        Assert.Equal(1000L, r.ExpDungeonTotal);                                            // 5 次击杀 × 400 ÷ 2
        Assert.Equal((long)ClearReward.MemberBase(1000, r.LevelBefore, 1, 1), r.ExpBase);  // 等级差按清空那一刻(发经验之前)的主角等级算
        Assert.Equal(r.ExpTotal, s.ClearExpGranted);
        Assert.True(s.ExpEarned >= s.ClearExpGranted);
        Assert.Equal(progress.TotalGained, s.ExpEarned);                                   // 进度里的总得经验 = 逐只击杀的 + 通关的
        Assert.Equal(progress.Level, r.LevelAfter);
        Assert.True(r.LevelAfter >= r.LevelBefore);

        // 再走下去结算的结果不变,经验不会再发一次
        long total = progress.TotalGained;
        for (int i = 0; i < 600; i++)
        {
            s.Update(CombatScript.StepMs, 0, 0);
        }
        Assert.Same(r, s.Result);
        Assert.Equal(total, progress.TotalGained);
    }

    [Fact]
    public void TheKillExpAndTheClearExpAreBothInTheExpEarned()
    {
        HeroProgress progress = Progress();
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40, progress: progress);
        DungeonSession s = d.Session;
        s.UseRanking(Rank(), 7, 1);
        new DungeonBot(true).RunUntil(s, 600_000);
        long killExp = s.ExpEarned - s.ClearExpGranted;
        Assert.True(killExp > 0, "逐只击杀也给了经验");
        Assert.True(s.ClearExpGranted > 0);
    }

    [Fact]
    public void WithoutUseRankingTheClearStillSettlesButGrantsNothing()
    {
        HeroProgress progress = Progress();
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40, progress: progress);
        DungeonSession s = d.Session;
        new DungeonBot(true).RunUntil(s, 600_000);
        Assert.True(s.BossCleared);
        DungeonResult r = s.Result;
        Assert.NotNull(r);
        Assert.False(r.HasRank);
        Assert.False(r.Granted);
        Assert.Equal(0L, r.ExpTotal);                                                      // 没有标准等级:基础经验 0
        Assert.Equal(0L, s.ClearExpGranted);
        Assert.Equal(progress.TotalGained, s.ExpEarned);                                   // 只有逐只击杀的经验
    }

    [Fact]
    public void TheClearIsSettledEvenWhenTheRunHasNoHeroProgress()
    {
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40);
        DungeonSession s = d.Session;
        s.UseRanking(Rank(), 7, 1);
        new DungeonBot(true).RunUntil(s, 600_000);
        DungeonResult r = s.Result;
        Assert.NotNull(r);
        Assert.False(r.Granted);
        Assert.Equal(0L, r.ExpTotal);
        Assert.True(r.HasRank);
        Assert.Equal(0L, s.ExpEarned);
    }

    [Fact]
    public void ARunThatEndsInTheBossRoomWithoutMonstersIsSettledOnEntry()
    {
        using var d = new Dungeon(TwoRoomBossLayout(), (cx, cy) => None(), progress: Progress());
        DungeonSession s = d.Session;
        s.UseRanking(Rank(), 7, 1);
        for (int i = 0; i < 400 && !s.BossCleared; i++)
        {
            s.Update(CombatScript.StepMs, 1, 0);
        }
        Assert.True(s.BossCleared);
        Assert.NotNull(s.Result);
        Assert.Equal(0, s.Result.Stats.Kills);
        Assert.Equal(0L, s.Result.ExpTotal);                                               // 一只也没打死:没有通关经验
        Assert.False(s.Result.Granted);
    }

    // ------------------------------------------------------------------ 结算页

    [Fact]
    public void TheSettlementPanelListsTheClearExpTheRankBonusAndTheLevelUp()
    {
        HeroProgress progress = Progress();
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40, progress: progress);
        DungeonSession s = d.Session;
        // 所有门槛都是 0:任何评分点都在最高档,评分加成一定大于 0(与基础分的估计无关)
        s.UseRanking(Rank(new[] { 0, 0, 0, 0, 0, 0, 0, 0 }), 7, 1);
        new DungeonBot(true).RunUntil(s, 600_000, 12_000);
        for (int i = 0; i < 600 && !s.SettlementActive; i++)
        {
            s.Update(CombatScript.StepMs, 0, 0);
        }
        Assert.True(s.SettlementActive);
        DungeonResult r = s.Result;
        Assert.True(r.Granted && r.ExpBase > 0 && r.ExpRankBonus > 0 && r.LevelsGained > 0);
        Assert.Equal(4, r.BonusIndex);

        List<SceneSprite> lines = s.Build(0).Where(x => x.AnimationPath == "reward").ToList();
        // 评分(图包里没有字母图时是一行文字)、用时、击杀、经验、通关经验、评分加成、金币、物品、升级
        Assert.Equal(9, lines.Count);
        Assert.All(lines, x => Assert.Equal((DungeonSession.RewardPanelLeft, OverlayArt.UiRank), (x.ScreenX, x.LayerRank)));
        Assert.Equal(DungeonSession.RewardPanelTop, lines[0].ScreenY);
        for (int i = 1; i < lines.Count; i++)
        {
            Assert.True(lines[i - 1].ScreenY < lines[i].ScreenY, "第 " + i + " 行没有排在上一行下面");
        }
        Assert.DoesNotContain(s.Build(0), x => x.AnimationPath == ResultArt.Tag);          // 合成图包里没有字母图
    }

    [Fact]
    public void ThePanelSkipsTheLinesThatHaveNothingToShow()
    {
        HeroProgress progress = Progress();
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40, progress: progress);
        DungeonSession s = d.Session;
        new DungeonBot(true).RunUntil(s, 600_000, 12_000);                                 // 没接评分数据:没有评分、通关经验与评分加成
        for (int i = 0; i < 600 && !s.SettlementActive; i++)
        {
            s.Update(CombatScript.StepMs, 0, 0);
        }
        Assert.True(s.SettlementActive);
        DungeonResult r = s.Result;
        int expected = 5 + (r.LevelsGained > 0 ? 1 : 0);                                   // 用时、击杀、经验、金币、物品 (+ 升级)
        Assert.Equal(expected, s.Build(0).Count(x => x.AnimationPath == "reward"));
    }
}
