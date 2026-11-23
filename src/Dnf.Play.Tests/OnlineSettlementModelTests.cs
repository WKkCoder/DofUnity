using System.Globalization;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui Play 层中性结算模型、评分字母归属、经验不相加、未知字段不画、
/// <see cref="SettlementArt"/> 数据注入重载、<see cref="HostedRoomRun"/> 累计本地战绩与 HUD 挂钩、
/// 以及 <see cref="OnlineSettlementTownWorld"/> 回城落位覆盖的单测。全部使用合成数据与 900+/9100+ 编号。
/// </summary>
public sealed class OnlineSettlementModelTests
{
    private const int SynthDungeonId = 9101;
    private const ushort SynthSelfUid = 9501;
    private const ushort SynthOtherUid = 9502;
    private const int SynthQuestItemId = 9801;
    private const int SynthRewardItemId = 9802;
    private const int SynthUnknownItemId = 9803;

    private sealed class FakeSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) =>
            Frames.TryGetValue((imageName, frame), out sprite!);
    }

    private static SpriteFrame Coded(int width, int height, byte tag, int offsetX = 0, int offsetY = 0)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            rgba[i * 4 + 2] = tag;
            rgba[i * 4 + 3] = 255;
        }
        return new SpriteFrame(width, height, offsetX, offsetY, width, height, rgba);
    }

    private static FakeSprites SettlementPanelSprites()
    {
        var f = new FakeSprites();
        f.Frames[(SettlementArt.Image, SettlementArt.HeaderFrame)] = Coded(268, 54, 1, 1, 0);
        f.Frames[(SettlementArt.Image, SettlementArt.RowFrame)] = Coded(270, 30, 2);
        f.Frames[(SettlementArt.Image, SettlementArt.FooterFrame)] = Coded(268, 16, 3, 1, 0);
        foreach (int first in new[] { SettlementArt.RetryLabelFrame, SettlementArt.SelectLabelFrame, SettlementArt.TownLabelFrame, SettlementArt.PartyLabelFrame })
        {
            for (int state = 0; state < 4; state++)
            {
                f.Frames[(SettlementArt.Image, first + state)] = Coded(66, 19, (byte)(100 + first + state), 90, 2);
            }
        }
        for (int i = 0; i < ResultArt.FrameCount; i++)
        {
            f.Frames[(ResultArt.RankLetterImage, i)] = Coded(48, 48, (byte)(200 + i));
        }
        return f;
    }

    private static RankSystem SyntheticRankSystem()
    {
        var thresholds = new[] { 90, 80, 70, 60, 50, 40, 30, 20 };
        var steps = new[] { new TimeBonusStep(0, 1), new TimeBonusStep(10000, 2), new TimeBonusStep(20000, 3), new TimeBonusStep(40000, 5) };
        var averages = new[] { (SynthDungeonId, 1, new[] { 100000, 110000, 120000, 130000, 140000 }) };
        return new RankSystem(thresholds, steps, averages);
    }

    private static HeroProgress SyntheticProgress(int level = 1)
    {
        var expTable = new ExpTable(new[] { 1000, 3000, 7000, 15000, 30000 });
        var spTable = new SpTable(new[] { 0, 10, 20, 30, 40, 50 });
        return new HeroProgress(expTable, spTable, CharacterGrowth.Parse(null), MobRewardTable.Placeholder(expTable), ExpRates.Neutral, level);
    }

    private static OnlineDungeonSettlementData FullServerData(
        byte serverRankValue = 0,
        int clearTimeMs = 45000,
        int bestTimeMs = 42000,
        bool isNewRecord = true,
        int reportedKills = 7,
        int droppedKills = 1,
        int baseExp = 1800,
        int rankBonusExp = 350,
        int unknownSlot2Exp = 99999,
        int avatarExp = 120,
        int channelExp = 80,
        int rewardGold = 640)
    {
        var expValues = new int[16];
        expValues[0] = baseExp;
        expValues[1] = rankBonusExp;
        expValues[2] = unknownSlot2Exp;
        expValues[3] = avatarExp;
        expValues[8] = channelExp;
        expValues[14] = 55;

        var result = new OnlineSettlementResultData(
            rankValue: serverRankValue,
            clearTime: clearTimeMs,
            timeBonus: 18,
            baseScore: 0,
            questMazeFlag: 0,
            hasBestTimeBlock: true,
            entries: new[]
            {
                new OnlineSettlementBestTime(SynthOtherUid, 99000, false),
                new OnlineSettlementBestTime(SynthSelfUid, bestTimeMs, isNewRecord),
            });

        var reward = new OnlineSettlementRewardData(
            expValues: expValues,
            expKind: 1,
            questItems: new[] { new OnlineSettlementItemData(1, SynthQuestItemId, 3, 0, 0, 0) },
            rewards: new[]
            {
                new OnlineSettlementRewardSlotData(2, 0, rewardGold, SynthRewardItemId, 2),
                new OnlineSettlementRewardSlotData(2, 0, 111, SynthUnknownItemId, 1),
                new OnlineSettlementRewardSlotData(0, 0, 0, 0, 0),
                new OnlineSettlementRewardSlotData(0, 0, 0, 0, 0),
            },
            receiverValue: 77777,
            reservedBytes: new byte[] { 0, 0, 0, 0 },
            premiumRewards: new[]
            {
                new OnlineSettlementRewardSlotData(2, 0, 66666, 9899, 1),
                new OnlineSettlementRewardSlotData(0, 0, 0, 0, 0),
                new OnlineSettlementRewardSlotData(0, 0, 0, 0, 0),
                new OnlineSettlementRewardSlotData(0, 0, 0, 0, 0),
            },
            percent: 88888,
            isReturnUserParty: true,
            isEventCharacParty: true,
            hasDeadUserPenalty: true);

        return new OnlineDungeonSettlementData(
            SynthDungeonId,
            "dungeon/test/synth.dgn",
            difficulty: 2,
            reportedKills: reportedKills,
            droppedKills: droppedKills,
            roomsEntered: 3,
            resultParseState: OnlineSettlementParseState.Succeeded,
            result: result,
            rewardParseState: OnlineSettlementParseState.Succeeded,
            reward: reward);
    }

    [Fact]
    public void Full_server_data_prefers_server_over_local_keeps_local_rank_never_sums_16_exp_and_hides_unknowns()
    {
        HeroProgress progress = SyntheticProgress(level: 1);
        var localStats = new RunStats(clearMs: 30000, kills: 5, comboKills: 5, maxCombo: 12, hitsTaken: 0, revivesUsed: 0);
        DungeonResult localResult = DungeonResult.Compute(localStats, SyntheticRankSystem(), progress, SynthDungeonId, standardLevel: 1, difficulty: 0, expWeight: 1f);
        Assert.True(localResult.HasRank);
        Assert.NotEqual("F", localResult.Letter); // 本地打出高评分(Frame=0=SSS),但服务端 0x22 故意给完全不同的 RankValue=7
        const byte fakeServerRank = 7;
        Assert.NotEqual((int)fakeServerRank, localResult.Frame);

        OnlineDungeonSettlementData serverData = FullServerData(serverRankValue: fakeServerRank);
        var localCtx = new OnlineSettlementLocalContext
        {
            LocalStats = localStats,
            LocalResult = localResult,
            LocalGoldEarned = 123,
            SelfUniqueId = SynthSelfUid,
            PartySlot = 0,
        };

        Func<int, string> itemLookup = id => id switch
        {
            SynthQuestItemId => "合成任务晶石",
            SynthRewardItemId => "合成勇者短剑",
            _ => null,
        };

        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(serverData, localCtx, itemLookup);

        // 1. 评分字母必须沿用本地离线同款算法,绝不采用服务端 RankValue=0
        Assert.True(summary.HasRank);
        Assert.Equal(localResult.Letter, summary.RankLetter);
        Assert.Equal(localResult.Frame, summary.RankFrame);
        Assert.Equal(SettlementFieldSource.Local, summary.RankSource);
        Assert.Equal(SettlementFieldConfidence.Unknown, summary.ServerRankValue.Confidence);
        Assert.False(summary.ServerRankValue.ShouldDisplay);

        // 2. 服务端覆盖本地的字段全部以服务端为准
        Assert.Equal(45000, summary.ClearTimeMs.Value);
        Assert.Equal(SettlementFieldSource.Server, summary.ClearTimeMs.Source);
        Assert.Equal(42000, summary.PersonalBestTimeMs.Value);
        Assert.True(summary.IsNewRecord.Value);
        Assert.Equal(7, summary.Kills.Value);
        Assert.Equal(1, summary.DroppedKills);
        Assert.Equal(1800, summary.BaseExp.Value);
        Assert.Equal(350, summary.RankBonusExp.Value);
        Assert.Equal(640, summary.RewardGold.Value);
        Assert.Equal(SettlementFieldConfidence.Inferred, summary.RewardGold.Confidence);

        // 3. 差异清单覆盖 5 个服务端覆盖字段,且脱敏日志不含数值
        Assert.Equal(new[] { "clear_time", "kills", "base_exp", "rank_bonus_exp", "reward_gold" }, summary.DiffFields);
        foreach (string field in summary.DiffFields)
        {
            string line = OnlineSettlementLog.Diff(field);
            Assert.StartsWith("DNF_ONLINE stage=settle event=diff field=", line);
            Assert.DoesNotContain("45000", line);
            Assert.DoesNotContain("1800", line);
            Assert.DoesNotContain("640", line);
        }

        // 4. 经验块 16 项绝不相加(下标 2 的 99999 被标 Unknown 跳过),有把握增量仅 BaseExp + RankBonusExp = 2150
        Assert.Equal(1800 + 350, summary.ConfidentExpGrant);
        Assert.DoesNotContain(summary.NamedBonusExpLines, l => l.Index == 2);
        Assert.Contains(summary.NamedBonusExpLines, l => l.Index == 3 && l.Amount == 120);
        Assert.Contains(summary.NamedBonusExpLines, l => l.Index == 8 && l.Amount == 80);

        // 5. 未知字段一律 Confidence == Unknown 且 ShouldDisplay == false
        Assert.False(summary.UnknownExpSlot2.ShouldDisplay);
        Assert.False(summary.UnknownReceiverValue.ShouldDisplay);
        Assert.False(summary.UnknownPercent.ShouldDisplay);
        Assert.False(summary.UnknownIsEventCharacParty.ShouldDisplay);
        Assert.False(summary.UnknownPremiumRewardActiveCount.ShouldDisplay);

        // 6. 掉落物品按物品号查本地名字,查不到显示占位且不漏原始物品号
        Assert.Equal(3, summary.Drops.Count);
        Assert.True(summary.Drops[0].NameResolved);
        Assert.Equal("合成任务晶石 x3", summary.Drops[0].DisplayLabel(hasFonts: true));
        Assert.True(summary.Drops[1].NameResolved);
        Assert.Equal("合成勇者短剑 x2", summary.Drops[1].DisplayLabel(hasFonts: true));
        Assert.False(summary.Drops[2].NameResolved);
        Assert.Equal("未知道具", summary.Drops[2].DisplayLabel(hasFonts: true));
        Assert.Equal("UNKNOWN ITEM", summary.Drops[2].DisplayLabel(hasFonts: false));

        // 7. 渲染文案行核对:未知数值(99999/77777/88888/66666/9899/16 项之和 102404)与原始未知物品号(9803)绝不出现
        int sumAll16 = serverData.Reward.ExpValues.Sum();
        List<string> lines = OnlineSettlementOverlay.BuildResultLines(summary, hasFonts: false, rankLetterDrawn: false);
        string joined = string.Join("\n", lines);
        Assert.Contains("RANK " + localResult.Letter, joined);
        Assert.Contains("CLEAR TIME 00:45", joined);
        Assert.Contains("BEST TIME 00:42 [NEW RECORD!]", joined);
        Assert.Contains("CLEAR EXP +1800", joined);
        Assert.Contains("RANK BONUS +350", joined);
        Assert.Contains("REWARD GOLD(EST) +640", joined);
        Assert.Contains("UNKNOWN ITEM", joined);
        Assert.DoesNotContain("99999", joined);
        Assert.DoesNotContain("77777", joined);
        Assert.DoesNotContain("88888", joined);
        Assert.DoesNotContain("66666", joined);
        Assert.DoesNotContain("9899", joined);
        Assert.DoesNotContain(SynthUnknownItemId.ToString(CultureInfo.InvariantCulture), joined);
        Assert.DoesNotContain(sumAll16.ToString(CultureInfo.InvariantCulture), joined);
    }

    [Fact]
    public void Missing_or_failed_server_payloads_never_invent_fake_numbers_and_fallback_cleanly()
    {
        var failedData = new OnlineDungeonSettlementData(
            SynthDungeonId,
            "dungeon/test/synth.dgn",
            difficulty: 0,
            reportedKills: 3,
            droppedKills: 0,
            roomsEntered: 1,
            resultParseState: OnlineSettlementParseState.Failed,
            result: null,
            rewardParseState: OnlineSettlementParseState.Failed,
            reward: null);

        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(failedData, local: null, resolveItemName: null);

        Assert.False(summary.HasRank);
        Assert.False(summary.ClearTimeMs.ShouldDisplay);
        Assert.False(summary.PersonalBestTimeMs.ShouldDisplay);
        Assert.False(summary.BaseExp.ShouldDisplay);
        Assert.False(summary.RankBonusExp.ShouldDisplay);
        Assert.False(summary.RewardGold.ShouldDisplay);
        Assert.Empty(summary.Drops);
        Assert.Equal(0, summary.ConfidentExpGrant);
        Assert.Empty(summary.DiffFields);

        List<string> lines = OnlineSettlementOverlay.BuildResultLines(summary, hasFonts: false, rankLetterDrawn: false);
        string joined = string.Join("\n", lines);
        Assert.Contains("CLEAR TIME --:--", joined);
        Assert.Contains("CLEAR EXP --", joined);
        Assert.Contains("DROPS: --", joined);
        Assert.DoesNotContain("BEST TIME", joined);
        Assert.DoesNotContain("REWARD GOLD", joined);

        // 7 字节全零兜底形(HasBestTimeBlock == false, ClearTime == 0):不把 0 当成服务端真实通关用时,退回本地用时
        var fallbackResult = new OnlineSettlementResultData(0, 0, 0, 0, 0, hasBestTimeBlock: false, Array.Empty<OnlineSettlementBestTime>());
        var fallbackData = new OnlineDungeonSettlementData(
            SynthDungeonId, "dungeon/test/synth.dgn", 0, 2, 0, 1,
            OnlineSettlementParseState.Succeeded, fallbackResult,
            OnlineSettlementParseState.NotApplicable, null);
        var localCtx = new OnlineSettlementLocalContext { LocalStats = new RunStats(51000, 2, 2, 4, 0, 0) };
        OnlineSettlementSummary fallbackSummary = OnlineSettlementSummaryBuilder.Build(fallbackData, localCtx);
        Assert.True(fallbackSummary.ClearTimeMs.ShouldDisplay);
        Assert.Equal(51000, fallbackSummary.ClearTimeMs.Value);
        Assert.Equal(SettlementFieldSource.Local, fallbackSummary.ClearTimeMs.Source);
        Assert.False(fallbackSummary.PersonalBestTimeMs.ShouldDisplay);
    }

    [Fact]
    public void ApplyConfidentExp_grants_only_BaseExp_plus_RankBonusExp_once_and_updates_hero_level()
    {
        HeroProgress progress = SyntheticProgress(level: 1);
        OnlineDungeonSettlementData data = FullServerData(baseExp: 1100, rankBonusExp: 200, unknownSlot2Exp: 50000, avatarExp: 9000);
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(data);

        // 1 级升 2 级需要 1000 经验;仅发 1100+200=1300 → 升到 2 级(若误加 50000 或 9000 会飙到 5 级)
        int applied1 = OnlineSettlementHeroHelpers.ApplyConfidentExp(progress, summary, out int before, out int after);
        Assert.Equal(1300, applied1);
        Assert.Equal(1, before);
        Assert.Equal(2, after);
        Assert.Equal(2, progress.Level);
        Assert.Equal(1300, progress.Exp);
        Assert.Equal(1, summary.LevelsGained);
        Assert.Equal(0, progress.Gold); // 金币不同步

        // 第二次调用必须幂等返回 0,不得重复加经验
        int applied2 = OnlineSettlementHeroHelpers.ApplyConfidentExp(progress, summary, out _, out _);
        Assert.Equal(0, applied2);
        Assert.Equal(1300, progress.Exp);
    }

    [Fact]
    public void SettlementArt_injection_overload_disables_rows_and_overlay_builds_dim_menu_secretshop_countdown_and_rank()
    {
        FakeSprites sprites = SettlementPanelSprites();
        SettlementArt panel = SettlementArt.TryCreate(sprites)!;
        var art = new OverlayArt();

        // 仅允许 ReturnToTown(例如结算超时态):Retry/QuickParty/SelectDungeon 均画 StateDisabled(base+3),鼠标停在 Retry 也不悬停
        var spritesOnlyTown = new List<SceneSprite>();
        int order = 0;
        panel.Build(spritesOnlyTown, ref order, art, hoverRow: 0, choice => choice == SettlementChoice.ReturnToTown);
        var labels = spritesOnlyTown.Where(s => s.AnimationPath == "settle-label").ToList();
        Assert.Equal(4, labels.Count);
        Assert.Equal(100 + SettlementArt.RetryLabelFrame + SettlementArt.StateDisabled, labels[0].Frame.Rgba[2]);
        Assert.Equal(100 + SettlementArt.PartyLabelFrame + SettlementArt.StateDisabled, labels[1].Frame.Rgba[2]);
        Assert.Equal(100 + SettlementArt.SelectLabelFrame + SettlementArt.StateDisabled, labels[2].Frame.Rgba[2]);
        Assert.Equal(100 + SettlementArt.TownLabelFrame + SettlementArt.StateNormal, labels[3].Frame.Rgba[2]);

        // 鼠标停在启用行 ReturnToTown(row 3) 时画 StateHover(base+1)
        var spritesHoverTown = new List<SceneSprite>();
        int order2 = 0;
        panel.Build(spritesHoverTown, ref order2, art, hoverRow: 3, choice => choice == SettlementChoice.ReturnToTown);
        var labels2 = spritesHoverTown.Where(s => s.AnimationPath == "settle-label").ToList();
        Assert.Equal(100 + SettlementArt.TownLabelFrame + SettlementArt.StateHover, labels2[3].Frame.Rgba[2]);

        // 完整联网结算叠层渲染:两层黑幕(BackDimRank & DimRank)、菜单、灰显神秘商店、倒计时、本地评分字母、提示条
        var localStats = new RunStats(25000, 3, 3, 5, 0, 0);
        DungeonResult localResult = DungeonResult.Compute(localStats, SyntheticRankSystem(), SyntheticProgress(), SynthDungeonId, 1, 0, 1f);
        Assert.NotEqual(7, localResult.Frame);
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(
            FullServerData(serverRankValue: 7),
            new OnlineSettlementLocalContext { LocalStats = localStats, LocalResult = localResult, SelfUniqueId = SynthSelfUid });

        List<SceneSprite> hud = OnlineSettlementOverlay.Build(art, new OnlineSettlementRenderState
        {
            Summary = summary,
            Notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.EplpRejectedNotLeader, 8),
            AgeMs = DungeonSession.SettlementDimMs,
            HoverRow = 0,
            IsChoiceEnabled = SettlementArt.IsEnabled,
            RemainingCountdownMs = 42500,
            ShowCountdown = true,
            Sprites = sprites,
        }, timeMs: 1000);

        Assert.NotNull(hud);
        SceneSprite backDim = Assert.Single(hud, s => s.AnimationPath == "settle-dim-back");
        SceneSprite frontDim = Assert.Single(hud, s => s.AnimationPath == "settle-dim");
        Assert.Equal(OverlayArt.BackDimRank, backDim.LayerRank);
        Assert.Equal(OverlayArt.DimRank, frontDim.LayerRank);
        Assert.Equal(DungeonSession.SettlementDimAlpha, backDim.TintAlpha);
        Assert.Contains(hud, s => s.AnimationPath == "settle-secretshop-disabled");
        Assert.Contains(hud, s => s.AnimationPath == "settle-countdown");
        Assert.Contains(hud, s => s.AnimationPath == "onlinesettle-notice");
        SceneSprite rankSprite = Assert.Single(hud, s => s.AnimationPath == ResultArt.Tag);
        Assert.Equal((byte)(200 + localResult.Frame), rankSprite.Frame.Rgba[2]);
    }

    [Fact]
    public void HostedRoomRun_accumulates_local_stats_across_rooms_and_injects_HudOverlay_in_depth_order()
    {
        using var fixture = new HostedGatedRoomFixture();
        RoomMonsterSpawn[] sp = { HostedRoomFixture.Spawn(HostedRoomFixture.Buildable, 320) };
        RoomCombatSession room0 = fixture.Create(sp, map => RoomSpawnPlan.Fill(1, map.Monsters), new[] { GateSide.Right });
        RoomCombatSession room1 = fixture.Create(sp, map => RoomSpawnPlan.Fill(1, map.Monsters), new[] { GateSide.Left });

        var run = new HostedRoomRun(room0, _ => true, () => false, returnDelayMs: 1000);
        var bot = new HostedRoomBot();
        for (int i = 0; i < 400 && room0.Kills < 1; i++)
        {
            bot.Advance(run, 16.0);
        }
        Assert.Equal(1, room0.Kills);
        Assert.Equal(1, run.LocalKills);

        // 未设 HudOverlay 时 Build 不含任何 settle-dim
        List<SceneSprite> plain = run.Build(200);
        Assert.DoesNotContain(plain, s => s.AnimationPath == "settle-dim-back");

        // 换房后时钟、本地击杀累计不丢
        double clockBeforeSwitch = run.ClockMs;
        run.EnterRoom(room1, roomIndex: 1, mapId: 906, entry: GateSide.Left, exit: null);
        Assert.Equal(0, room1.Kills);
        Assert.Equal(1, run.LocalKills);

        for (int i = 0; i < 400 && room1.Kills < 1; i++)
        {
            bot.Advance(run, 16.0);
        }
        Assert.Equal(1, room1.Kills);

        RunStats stats = run.SnapshotLocalStats();
        Assert.True(stats.ClearMs > (int)clockBeforeSwitch);
        Assert.Equal(2, run.RoomsEntered);
        Assert.Equal(2, run.Reported);
        Assert.Equal(2, run.LocalKills);
        Assert.Equal(2, stats.Kills);

        // 挂上 HudOverlay 后,BackDimRank 排在 DimRank 之前,DimRank 排在 UiRank 之前
        var art = new OverlayArt();
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(FullServerData());
        run.HudOverlay = (r, t) => OnlineSettlementOverlay.Build(art, new OnlineSettlementRenderState
        {
            Summary = summary,
            AgeMs = 500,
            IsChoiceEnabled = SettlementArt.IsEnabled,
            ShowCountdown = true,
            RemainingCountdownMs = 30000,
        }, t);

        List<SceneSprite> withSettle = run.Build(400);
        int idxBack = withSettle.FindIndex(s => s.AnimationPath == "settle-dim-back");
        int idxFront = withSettle.FindIndex(s => s.AnimationPath == "settle-dim");
        int idxFallback = withSettle.FindIndex(s => s.AnimationPath == "settle-fallback");
        Assert.True(idxBack >= 0 && idxFront > idxBack && idxFallback > idxFront);
    }

    private sealed class StubTownWorld : ITownWorld
    {
        public StubTownWorld(TownPlace start, TownPlace ret)
        {
            Start = start;
            Return = ret;
        }

        public TownPlace Start { get; }
        public TownPlace Return { get; }
        public List<TownPlace> Created { get; } = new List<TownPlace>();

        public IHeroSession Create(TownPlace place)
        {
            Created.Add(place);
            return new StubHeroSession(place);
        }

        public SplashScene Splash(TownPlace? from, TownPlace to) => null;
        public TownPlace? Through(TownPlace from, TownExit exit, bool heroFacingLeft) => null;
        public bool TryRoute(TownPlace from, out TownExit exit) { exit = default; return false; }
    }

    private sealed class StubHeroSession : IHeroSession
    {
        public StubHeroSession(TownPlace place) => MapPath = "fake/" + place.Town + "/" + place.Area;
        public string MapPath { get; }
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;
        public void Update(double deltaMs, int dirX, int dirY) { }
        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
    }

    [Fact]
    public void OnlineSettlementTownWorld_applies_one_shot_return_override_then_restores_default()
    {
        var defaultReturn = new TownPlace(1, 1, TownEntry.Return);
        var inner = new StubTownWorld(new TownPlace(1, 0), defaultReturn);
        var wrapped = new OnlineSettlementTownWorld(inner);

        Assert.Equal(defaultReturn, wrapped.Return);
        Assert.False(wrapped.HasPendingReturn);

        var serverPlace = new TownPlace(2, 3, TownEntry.Return);
        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, serverPlace, 412, 265, true));
        Assert.True(wrapped.HasPendingReturn);
        Assert.Equal(serverPlace, wrapped.Return);

        // 消费一次 Create 后自动复位回默认 Return
        wrapped.Create(wrapped.Return);
        Assert.Single(inner.Created);
        Assert.Equal(serverPlace, inner.Created[0]);
        Assert.False(wrapped.HasPendingReturn);
        Assert.Equal(defaultReturn, wrapped.Return);

        // ClearReturn 清除尚未消费的覆盖
        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.PreDungeonArea, new TownPlace(1, 3, TownEntry.Return)));
        Assert.True(wrapped.HasPendingReturn);
        wrapped.ClearReturn();
        Assert.False(wrapped.HasPendingReturn);
        Assert.Equal(defaultReturn, wrapped.Return);
    }

    [Fact]
    public void Equal_server_and_local_values_produce_no_diffs_and_best_time_matching_handles_single_and_unmatched_entries()
    {
        HeroProgress progress = SyntheticProgress(level: 1);
        var localStats = new RunStats(clearMs: 45000, kills: 7, comboKills: 7, maxCombo: 10, hitsTaken: 0, revivesUsed: 0);
        DungeonResult localResult = DungeonResult.Compute(localStats, SyntheticRankSystem(), progress, SynthDungeonId, 1, 0, 1f);

        // 构造与本地完全一致的服务端数值 → DiffFields 必须为空
        OnlineDungeonSettlementData matching = FullServerData(
            clearTimeMs: 45000,
            reportedKills: 7,
            baseExp: (int)localResult.ExpBase,
            rankBonusExp: (int)localResult.ExpRankBonus,
            rewardGold: 500);
        var localCtx = new OnlineSettlementLocalContext
        {
            LocalStats = localStats,
            LocalResult = localResult,
            LocalGoldEarned = 500,
            SelfUniqueId = 9999, // 故意不匹配 Entries 中任何成员
            PartySlot = 0,
        };

        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(matching, localCtx);
        Assert.Empty(summary.DiffFields);
        Assert.False(summary.PersonalBestTimeMs.HasValue);
        Assert.False(summary.IsNewRecord.HasValue);

        // SelfUniqueId 为 null 且仅有 1 条记录时采用唯一记录;有 2 条记录时不瞎猜
        var singleEntryResult = new OnlineSettlementResultData(
            0, 41000, 10, 0, 0, true,
            new[] { new OnlineSettlementBestTime(SynthSelfUid, 38000, false) });
        var singleData = new OnlineDungeonSettlementData(
            SynthDungeonId, "dungeon/test/synth.dgn", 0, 2, 0, 1,
            OnlineSettlementParseState.Succeeded, singleEntryResult,
            OnlineSettlementParseState.NotApplicable, null);
        OnlineSettlementSummary singleSum = OnlineSettlementSummaryBuilder.Build(singleData, new OnlineSettlementLocalContext { SelfUniqueId = null });
        Assert.True(singleSum.PersonalBestTimeMs.ShouldDisplay);
        Assert.Equal(38000, singleSum.PersonalBestTimeMs.Value);
        Assert.False(singleSum.IsNewRecord.Value);

        OnlineSettlementSummary multiNoUid = OnlineSettlementSummaryBuilder.Build(matching, new OnlineSettlementLocalContext { SelfUniqueId = null });
        Assert.False(multiNoUid.PersonalBestTimeMs.HasValue);
    }

    [Fact]
    public void Overlay_helpers_handle_chinese_lines_rank_drawn_omission_fallback_panel_disabled_and_boundaries()
    {
        Assert.Equal(0, OnlineSettlementOverlay.DimAlpha(0));
        Assert.Equal(0, OnlineSettlementOverlay.DimAlpha(-10));
        Assert.Equal(28, OnlineSettlementOverlay.DimAlpha(DungeonSession.SettlementDimMs / 2));
        Assert.Equal(DungeonSession.SettlementDimAlpha, OnlineSettlementOverlay.DimAlpha(DungeonSession.SettlementDimMs));
        Assert.Equal(DungeonSession.SettlementDimAlpha, OnlineSettlementOverlay.DimAlpha(9999));

        Assert.Equal("00:00", OnlineSettlementOverlay.FormatTimeMs(-50));
        Assert.Equal("00:00", OnlineSettlementOverlay.FormatTimeMs(0));
        Assert.Equal("01:05", OnlineSettlementOverlay.FormatTimeMs(65000));

        // 已画 RankLetter 精灵时,BuildResultLines 不重复输出文字 "评分"/"RANK"
        var localStats = new RunStats(25000, 3, 3, 5, 0, 0);
        DungeonResult localResult = DungeonResult.Compute(localStats, SyntheticRankSystem(), SyntheticProgress(), SynthDungeonId, 1, 0, 1f);
        Assert.NotEqual(7, localResult.Frame);
        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(
            FullServerData(serverRankValue: 7),
            new OnlineSettlementLocalContext { LocalStats = localStats, LocalResult = localResult, SelfUniqueId = SynthSelfUid });
        List<string> zhDrawn = OnlineSettlementOverlay.BuildResultLines(summary, hasFonts: true, rankLetterDrawn: true);
        Assert.DoesNotContain(zhDrawn, l => l.StartsWith("评分 ", StringComparison.Ordinal));
        Assert.Contains(zhDrawn, l => l.Contains("通关用时 00:45"));
        Assert.Contains(zhDrawn, l => l.Contains("个人最佳 00:42 [新纪录!]"));
        Assert.Contains(zhDrawn, l => l.Contains("通关基础经验 +1800"));
        Assert.Contains(zhDrawn, l => l.Contains("奖励金币(推) +640"));

        // BuildFallback 可注入启用判定:禁用行即使鼠标悬停也保持灰显(0x808080)
        var art = new OverlayArt();
        var sprites = new List<SceneSprite>();
        int order = 0;
        SettlementArt.BuildFallback(sprites, ref order, art, hoverRow: 0, choice => choice == SettlementChoice.ReturnToTown);
        var labels = sprites.Where(s => s.AnimationPath == "settle-label").ToList();
        Assert.Equal(4, labels.Count);
        Assert.NotEqual(labels[0].Frame.Rgba, labels[3].Frame.Rgba);

        // 脱敏日志格式化全部事件不含数值泄漏
        Assert.StartsWith("DNF_ONLINE stage=settle event=show", OnlineSettlementLog.Show(false, OnlineSettlementParseState.Succeeded, OnlineSettlementParseState.Failed, 2, 1));
        Assert.Equal("DNF_ONLINE stage=settle event=advance step=score_scroll status=ok", OnlineSettlementLog.Advance("score_scroll", "ok"));
        Assert.Equal("DNF_ONLINE stage=settle event=choose choice=Retry source=player", OnlineSettlementLog.Choose(SettlementChoice.Retry, "player"));
        Assert.Equal("DNF_ONLINE stage=settle event=retry status=reenter cycle=2", OnlineSettlementLog.Retry("reenter", 2));
        Assert.Equal("DNF_ONLINE stage=settle event=town reason=confirmed area=server_area signal=area_pair", OnlineSettlementLog.Town("confirmed", "server_area", "area_pair"));
        Assert.Equal("DNF_ONLINE stage=settle event=fail reason=eplp_rejected step=Retry code=8", OnlineSettlementLog.Fail("eplp_rejected", 8, "Retry"));
    }

    [Fact]
    public void OnlineSettlementTownWorld_places_real_hero_at_0_23_coordinates_and_restarts_camera()
    {
        using var fixture = new HostedGatedRoomFixture();
        RoomCombatSession room = fixture.Create(
            new[] { HostedRoomFixture.Spawn(HostedRoomFixture.Buildable, 320) },
            map => RoomSpawnPlan.Fill(1, map.Monsters),
            Array.Empty<GateSide>());
        var inner = new HeroBearingTownWorld(room, new TownPlace(1, 1, TownEntry.Return));
        var wrapped = new OnlineSettlementTownWorld(inner);

        var targetPlace = new TownPlace(2, 4, TownEntry.Return);
        wrapped.ArmReturn(new OnlineSettlementReturnTarget(OnlineSettlementReturnSource.ServerUserArea, targetPlace, 275.0, 190.0, true));
        IHeroSession created = wrapped.Create(wrapped.Return);

        Assert.Equal(275.0, created.Hero.X);
        Assert.Equal(190.0, created.Hero.Y);
        Assert.True(created.Hero.FacingLeft);
        Assert.False(wrapped.HasPendingReturn);
    }

    private sealed class HeroBearingTownWorld : ITownWorld
    {
        private readonly IHeroSession _session;
        public HeroBearingTownWorld(IHeroSession session, TownPlace ret) { _session = session; Start = ret; Return = ret; }
        public TownPlace Start { get; }
        public TownPlace Return { get; }
        public IHeroSession Create(TownPlace place) => _session;
        public SplashScene Splash(TownPlace? from, TownPlace to) => null;
        public TownPlace? Through(TownPlace from, TownExit exit, bool heroFacingLeft) => null;
        public bool TryRoute(TownPlace from, out TownExit exit) { exit = default; return false; }
    }
}
