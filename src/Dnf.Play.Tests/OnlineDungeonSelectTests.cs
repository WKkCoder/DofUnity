using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 在线选图策略、动态权限表、选图屏灰显与文字系统接入、GameFlow 在线钩子及离线零回归测试。
/// 全部用合成数据与方块字体,不改全进程共享状态(<see cref="OverlayArt.SharedFonts"/>)。
/// </summary>
public class OnlineDungeonSelectTests
{
    private sealed class FakeSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) => Frames.TryGetValue((imageName, frame), out sprite!);
    }

    private sealed class StubHero : IHeroSession
    {
        public string Label { get; }
        public StubHero(string label) => Label = label;
        public string MapPath => "fake/" + Label;
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;
        public void Update(double deltaMs, int dirX, int dirY) { }
        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
    }

    private sealed class StubRun : IRunSession
    {
        public StubRun(string path) => MapPath = path;
        public string MapPath { get; }
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;
        public void Update(double deltaMs, int dirX, int dirY) { }
        public List<SceneSprite> Build(long timeMs) => new List<SceneSprite>();
        public SettlementChoice Choice { get; set; }
        public bool Failed { get; set; }
    }

    private static SpriteFrame Coded(int width, int height, byte tag, int offsetX = 0, int offsetY = 0)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                rgba[o] = (byte)(y % 256);
                rgba[o + 1] = (byte)(x % 256);
                rgba[o + 2] = tag;
                rgba[o + 3] = 255;
            }
        }
        return new SpriteFrame(width, height, offsetX, offsetY, width, height, rgba);
    }

    private const string MapImage = "sprite/test/map.img";
    private const string SlotImage = "sprite/test/slots.img";

    private static WorldMapScene SampleWorldScene()
    {
        var f = new FakeSprites();
        f.Frames[(MapImage, 0)] = Coded(800, 600, 1);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.CardBorderFrame)] = Coded(174, 79, 10);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.BubbleFrameBase + WorldMapArt.MeasuredBubbleStyle)] = Coded(118, 80, 11);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.ProperChannelBannerFrame)] = Coded(553, 28, 12, 124, 0);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.KeyGuideBannerFrame)] = Coded(553, 28, 13, 86, 0);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.MarkerFrame)] = Coded(34, 34, 14, 1, 1);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.TopBottomBannerFrame)] = Coded(800, 600, 15);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.MapSelectBannerFrame)] = Coded(800, 72, 16);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.DifficultyBarFrame)] = Coded(181, 21, 17, 13, 0);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.BadgeFrame)] = Coded(35, 39, 18);
        f.Frames[(WorldMapArt.ButtonImage, WorldMapArt.ButtonNormalFrame)] = Coded(97, 23, 19);
        f.Frames[(WorldMapArt.ButtonImage, WorldMapArt.ButtonDisabledFrame)] = Coded(97, 23, 20);
        f.Frames[(WorldMapArt.PartyImage, WorldMapArt.PartyIconFrame)] = Coded(15, 15, 21);
        f.Frames[(SlotImage, 0)] = Coded(168, 73, 30);
        f.Frames[(SlotImage, 1)] = Coded(168, 73, 31);
        return new WorldMapScene(f, MapImage, 0, "LORIEN");
    }

    private static DungeonEntry CardEntry(string name, int id, int x, int y, int slot = 0, int level = 5)
    {
        return new DungeonEntry(
            "dungeon/test/" + name + ".dgn",
            name.ToUpperInvariant(),
            new DungeonCard(id, x, y, WorldMapArt.MeasuredBubbleStyle, SlotImage, slot, level));
    }

    [Theory]
    [InlineData(0, true, 0)]
    [InlineData(0, false, 0)]
    [InlineData(1, true, 1)]
    [InlineData(1, false, 1)]
    [InlineData(2, true, 2)]
    [InlineData(2, false, 2)]
    [InlineData(3, true, 3)]
    [InlineData(3, false, 2)]
    [InlineData(255, true, 3)]
    [InlineData(255, false, 2)]
    public void Permission_tier_maps_to_expected_difficulty_limit(byte tier, bool hasUltimate, int expectedMax)
    {
        Assert.Equal(expectedMax, OnlineDungeonPermissionRules.DifficultyLimitForTier(tier, hasUltimate));
    }

    [Fact]
    public void Permission_table_handles_missing_upsert_rejection_and_malformed_notices()
    {
        var table = new OnlineDungeonPermissionTable();
        Assert.False(table.HasSnapshot);
        Assert.Equal(0, table.GetTier(9101));
        Assert.Equal(0, table.DifficultyLimit(9101, hasUltimate: true));
        Assert.False(table.IsServerRejected(9101, out _));

        table.LoadInitial(new[]
        {
            new KeyValuePair<int, byte>(9101, 2),
            new KeyValuePair<int, byte>(9102, 3),
        }, hasSnapshot: true, noticeCount: 1, malformedCount: 0);

        Assert.True(table.HasSnapshot);
        Assert.Equal(2, table.EntryCount);
        Assert.Equal(2, table.DifficultyLimit(9101, hasUltimate: true));
        Assert.Equal(3, table.DifficultyLimit(9102, hasUltimate: true));
        Assert.Equal(2, table.DifficultyLimit(9102, hasUltimate: false));
        Assert.Equal(0, table.DifficultyLimit(9199, hasUltimate: true));

        // CMD 16 拒绝标记该副本不可进入;后续 (0,5) 更新仅解除对应副本的拒绝标记
        table.MarkServerRejected(9101, 7);
        table.MarkServerRejected(9102, 4);
        Assert.True(table.IsServerRejected(9101, out byte? code1));
        Assert.Equal((byte)7, code1);

        table.ApplyUpdate(new[] { new KeyValuePair<int, byte>(9101, 3) });
        Assert.False(table.IsServerRejected(9101, out _));
        Assert.Equal(3, table.DifficultyLimit(9101, hasUltimate: true));
        Assert.True(table.IsServerRejected(9102, out byte? code2));
        Assert.Equal((byte)4, code2);

        // 坏帧保留已有表内容并递增 MalformedNoticeCount
        int vBefore = table.Version;
        table.RecordMalformedNotice();
        Assert.Equal(1, table.MalformedNoticeCount);
        Assert.Equal(2, table.EntryCount);
        Assert.Equal(3, table.GetTier(9101));
        Assert.Equal(vBefore, table.Version);

        table.ClearServerRejections();
        Assert.False(table.IsServerRejected(9102, out _));
    }

    [Fact]
    public void Select_policy_blocks_only_missing_id_missing_data_and_server_rejected()
    {
        var table = new OnlineDungeonPermissionTable();
        // 注意 9101 未在表中 -> Tier 为 0,但 Allowed(不是 Disabled)
        DungeonEntry allowedMissingPerm = CardEntry("a", 9101, 100, 100);
        DungeonEntry rejectedEntry = CardEntry("b", 9102, 200, 100);
        DungeonEntry missingIdEntry = new DungeonEntry("dungeon/test/noid.dgn", "NOID");
        DungeonEntry missingLocalEntry = CardEntry("nolocal", 9103, 300, 100);

        table.MarkServerRejected(9102, 12);

        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(
            table,
            idResolver: e => e == missingIdEntry ? -1 : (e.Card?.DungeonId ?? -1),
            hasLocalData: e => e != missingLocalEntry,
            hasUltimate: _ => true);

        DungeonEntryStatus sA = policy.EntryStatus(allowedMissingPerm);
        Assert.False(sA.Disabled);
        Assert.Equal(0, policy.DifficultyLimit(allowedMissingPerm));

        DungeonEntryStatus sB = policy.EntryStatus(rejectedEntry);
        Assert.True(sB.Disabled);
        Assert.Equal(DungeonEntryBlockKind.ServerRejected, sB.Kind);
        Assert.Equal((byte)12, sB.Code);
        Assert.Contains("12", sB.ReasonText(hasFonts: false));
        Assert.Contains("12", sB.ReasonText(hasFonts: true));

        DungeonEntryStatus sNoId = policy.EntryStatus(missingIdEntry);
        Assert.True(sNoId.Disabled);
        Assert.Equal(DungeonEntryBlockKind.MissingDungeonId, sNoId.Kind);

        DungeonEntryStatus sNoLocal = policy.EntryStatus(missingLocalEntry);
        Assert.True(sNoLocal.Disabled);
        Assert.Equal(DungeonEntryBlockKind.MissingLocalData, sNoLocal.Kind);
    }

    [Fact]
    public void Script_display_name_reads_both_keyed_and_string_name_lines()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["dungeon/dungeon.kor.str"] = Encoding.ASCII.GetBytes("name_100>KeyedDungeon\r\nplain_key>StringDungeon\r\n"),
            ["worldmap/worldmap.kor.str"] = Encoding.ASCII.GetBytes("map_200>GrandFlorest\r\n"),
        };
        var texts = new WorldTexts(new StrLibrary(p => map.TryGetValue(p, out byte[] bytes) ? bytes : null));

        var keyedDoc = new[]
        {
            new ScriptLine("[name]", new[] { new ScriptValue(ScriptValueKind.Keyed, StrLibrary.KindDungeon, "name_100") }),
        };
        var stringDoc = new[]
        {
            new ScriptLine("[name]", new[] { new ScriptValue(ScriptValueKind.String, 0, "plain_key") }),
        };
        var mapDoc = new[]
        {
            new ScriptLine("[name]", new[] { new ScriptValue(ScriptValueKind.Keyed, StrLibrary.KindWorldMap, "map_200") }),
        };

        Assert.Equal("KeyedDungeon", OnlineDungeonSelectPolicy.ResolveScriptDisplayName(keyedDoc, StrLibrary.KindDungeon, texts));
        Assert.Equal("StringDungeon", OnlineDungeonSelectPolicy.ResolveScriptDisplayName(stringDoc, StrLibrary.KindDungeon, texts));
        Assert.Equal("GrandFlorest", OnlineDungeonSelectPolicy.ResolveScriptDisplayName(mapDoc, StrLibrary.KindWorldMap, texts));
    }

    [Fact]
    public void Select_screen_blocks_disabled_and_busy_inputs_clamps_on_refresh_and_clears_notice_on_move()
    {
        var table = new OnlineDungeonPermissionTable();
        table.LoadInitial(new[]
        {
            new KeyValuePair<int, byte>(9101, 3),
            new KeyValuePair<int, byte>(9102, 1),
        }, hasSnapshot: true);
        table.MarkServerRejected(9102, 5);

        DungeonEntry e1 = CardEntry("a", 9101, 100, 100, slot: 0);
        DungeonEntry e2 = CardEntry("b", 9102, 200, 200, slot: 1);

        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(table, hasUltimate: _ => true);
        var screen = new DungeonSelectScreen(new[] { e1, e2 }, 0, SampleWorldScene(), policy.DifficultyLimit, 3)
        {
            EntryStatus = policy.EntryStatus,
        };
        Assert.Equal(3, screen.Difficulty);

        // 动态降权 (tier 3 -> 1) 后调 RefreshDifficulty 必须重新夹紧当前难度
        table.ApplyUpdate(new[] { new KeyValuePair<int, byte>(9101, 1) });
        screen.RefreshDifficulty();
        Assert.Equal(1, screen.Difficulty);
        Assert.Equal(1, screen.MaxDifficulty);

        // 在第一项挂一条旧错误提示,换项后清掉并展示第二项(disabled)的原因
        screen.Notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Timeout);
        screen.Move(1);
        Assert.Equal(1, screen.Index);
        Assert.Null(screen.Notice);
        Assert.True(screen.IsSelectedDisabled);

        // 禁用条目按 Confirm 不触发 Enter
        screen.Confirm();
        Assert.Equal(SelectOutcome.None, screen.Outcome);

        // 再点禁用卡片也不触发 Enter,只保留在禁用项
        HitRect cardRect = WorldMapArt.CardRect(e2.Card!, true);
        Assert.True(screen.Click(cardRect.X + cardRect.Width / 2, cardRect.Y + cardRect.Height / 2));
        Assert.Equal(SelectOutcome.None, screen.Outcome);

        // 回到可用项并置 Busy=true 时,Move / Difficulty / Confirm / Back / Click 全被屏蔽
        screen.Move(-1);
        Assert.Equal(0, screen.Index);
        screen.Busy = true;
        screen.Move(1);
        screen.MoveDifficulty(-1);
        screen.Confirm();
        screen.Back();
        Assert.False(screen.Click(cardRect.X + cardRect.Width / 2, cardRect.Y + cardRect.Height / 2));
        Assert.Equal(0, screen.Index);
        Assert.Equal(SelectOutcome.None, screen.Outcome);

        screen.Busy = false;
        screen.Confirm();
        Assert.Equal(SelectOutcome.Enter, screen.Outcome);
    }

    [Fact]
    public void WorldMap_with_fonts_enhances_text_and_disabled_overlays_while_offline_nofont_is_pixel_identical()
    {
        WorldMapScene scene = SampleWorldScene();
        DungeonEntry e1 = CardEntry("lorien", 9101, 160, 160, slot: 0, level: 4);
        DungeonEntry e2 = CardEntry("vilmark", 9102, 420, 220, slot: 1, level: 8);
        var entries = new[] { e1, e2 };

        // 离线基线 A vs 通过 DungeonSelectScreen 无在线/无字体构建的结果 B:必须逐精灵、逐像素完全一致
        var artDirect = new List<SceneSprite>();
        int orderDirect = 0;
        WorldMapArt.For(scene.Sprites)!.Build(artDirect, ref orderDirect, new OverlayArt(), scene, entries, 0, 2000, 2);

        var offlineScreen = new DungeonSelectScreen(entries, 0, scene, _ => 3, 2);
        offlineScreen.Advance(2000);
        List<SceneSprite> offlineBuilt = offlineScreen.Build(9999);

        Assert.Equal(artDirect.Count, offlineBuilt.Count);
        for (int i = 0; i < artDirect.Count; i++)
        {
            Assert.Equal(artDirect[i].AnimationPath, offlineBuilt[i].AnimationPath);
            Assert.Equal(artDirect[i].ScreenX, offlineBuilt[i].ScreenX);
            Assert.Equal(artDirect[i].ScreenY, offlineBuilt[i].ScreenY);
            Assert.Equal(artDirect[i].SortKey, offlineBuilt[i].SortKey);
            Assert.Equal(artDirect[i].TintAlpha, offlineBuilt[i].TintAlpha);
            Assert.Equal(artDirect[i].Frame.Width, offlineBuilt[i].Frame.Width);
            Assert.Equal(artDirect[i].Frame.Height, offlineBuilt[i].Frame.Height);
            Assert.Equal(artDirect[i].Frame.Rgba, offlineBuilt[i].Frame.Rgba);
        }

        // 离线占位画面同样逐像素稳定
        var p1 = new DungeonSelectScreen(entries, 0, null, _ => 2, 1);
        var p2 = new DungeonSelectScreen(entries, 0, null, _ => 2, 1);
        List<SceneSprite> sp1 = p1.Build(0);
        List<SceneSprite> sp2 = p2.Build(0);
        Assert.Equal(sp1.Count, sp2.Count);
        Assert.DoesNotContain(sp1, s => s.AnimationPath == "select-online-notice");

        // 带系统字体(BlockFonts)与联网禁用项:地区名/等级/卡片名/禁用灰显与顶部提示条应渲染出来
        var fonts = new BlockFonts();
        var onlineScreen = new DungeonSelectScreen(entries, 1, scene, _ => 3, 1)
        {
            Fonts = fonts,
            RegionDisplayName = "艾尔文防线",
            EntryDisplayName = e => e == e1 ? "洛兰" : "比尔马克帝国试验场",
            EntryStatus = e => e == e2 ? new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, 9) : DungeonEntryStatus.Allowed,
        };
        onlineScreen.Advance(2000);
        List<SceneSprite> enhanced = onlineScreen.Build(1000);

        Assert.True(fonts.Font.Lookups > 0);
        Assert.Contains(enhanced, s => s.AnimationPath == "map-card-disabled-selected");
        Assert.Contains(enhanced, s => s.AnimationPath == "map-card-disabled-tag");
        Assert.Contains(enhanced, s => s.AnimationPath != null && s.AnimationPath.StartsWith("select-online-notice", StringComparison.Ordinal));
        Assert.Contains(enhanced, s => s.AnimationPath == "map-region-name");

        // 验证 OnlineDungeonOverlay 生成与空输入不生成
        var oArt = new OverlayArt { Fonts = fonts };
        Assert.Null(OnlineDungeonOverlay.Build(oArt, null, 0));
        Assert.Null(OnlineDungeonOverlay.Build(oArt, new DungeonSelectNotice(DungeonSelectNoticeKind.None), 0));
        List<SceneSprite> banner = OnlineDungeonOverlay.Build(oArt, new DungeonSelectNotice(DungeonSelectNoticeKind.Entering), 100, inTown: true);
        Assert.NotNull(banner);
        Assert.Contains(banner, s => s.AnimationPath != null && s.AnimationPath.StartsWith("onlinedungeon-notice", StringComparison.Ordinal));
    }

    [Fact]
    public void GameFlow_online_hooks_manage_select_loading_dungeon_giveup_and_settlement()
    {
        var entries = new[]
        {
            new DungeonEntry("dungeon/test/a.dgn", "AAA"),
            new DungeonEntry("dungeon/test/b.dgn", "BBB"),
        };
        var towns = new List<StubHero>();
        var offlineStarts = new List<string>();
        var splashScene = new SplashScene("test", Coded(800, 600, 77), null, null, 0, 0, 500.0);

        var flow = new GameFlow(
            new LegacyTownWorld(() =>
            {
                var t = new StubHero("town#" + towns.Count);
                towns.Add(t);
                return t;
            }),
            (path, run) =>
            {
                offlineStarts.Add(path);
                return new StubRun(path);
            },
            entries,
            dungeonSplash: _ => splashScene);

        int configured = 0;
        var startedOnline = new List<(DungeonEntry Entry, int Diff)>();
        int giveUpCalls = 0;
        flow.ConfigureSelect = s =>
        {
            configured++;
            s.RegionDisplayName = "ConfiguredRegion";
        };
        flow.OnlineStartDungeon = (entry, diff) => startedOnline.Add((entry, diff));
        flow.OnlineGiveUpDungeon = () => giveUpCalls++;

        flow.EnterTown("boot");
        Assert.Equal(GameStage.Town, flow.Stage);

        flow.EnterSelect("gate");
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal(1, configured);
        Assert.Equal("ConfiguredRegion", flow.Select!.RegionDisplayName);

        // 选择确认时转交 OnlineStartDungeon 而不直接开离线 Run
        flow.Select.Confirm();
        Assert.True(flow.Poll());
        Assert.Single(startedOnline);
        Assert.Empty(offlineStarts);

        // 联网加载屏即便时间走完(Splash.Finished=true),没有外部 CompleteOnlineDungeonStart 前也不能自己切出 Loading
        flow.BeginOnlineDungeonLoading(entries[0], 2);
        Assert.Equal(GameStage.Loading, flow.Stage);
        flow.Splash!.Advance(5000);
        Assert.True(flow.Splash.Finished);
        Assert.False(flow.Poll());
        Assert.Equal(GameStage.Loading, flow.Stage);

        // 进副本失败回到原选图与难度,且必须解除 Busy 让玩家可继续切换/确认
        flow.FailOnlineDungeonStart(entries[1], 1, new DungeonSelectNotice(DungeonSelectNoticeKind.Rejected));
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal(2, configured);
        Assert.Equal(1, flow.Select!.Index);
        Assert.False(flow.Select.Busy);
        Assert.Equal(DungeonSelectNoticeKind.Rejected, flow.Select.Notice!.Kind);

        // 进副本成功切入 Dungeon
        var run = new StubRun(entries[0].Path);
        flow.CompleteOnlineDungeonStart(entries[0].Path, 2, run);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Equal(1, flow.Runs);
        Assert.Equal(2, flow.Difficulty);

        // 系统菜单 ReturnToTown 走 OnlineGiveUpDungeon 而不立刻离城
        Assert.True(flow.ToggleMenu());
        flow.Menu.Move(1);
        flow.Menu.Choose();
        Assert.True(flow.Poll());
        Assert.Equal(1, giveUpCalls);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Equal(0, flow.GiveUps);

        // 完成放弃回城
        flow.CompleteOnlineGiveUp();
        Assert.Equal(1, flow.GiveUps);
        Assert.Equal(GameStage.Town, flow.Stage);

        // HoldForMenu 阻塞 run.Choice 自动离开,CompleteOnlineSettlement 负责放行并支持 Retry/SelectDungeon/ReturnToTown
        var run2 = new StubRun(entries[0].Path) { Choice = SettlementChoice.ReturnToTown };
        flow.CompleteOnlineDungeonStart(entries[0].Path, 1, run2);
        flow.OnlineHoldDungeonExit = true;
        Assert.False(flow.Poll());
        Assert.Equal(GameStage.Dungeon, flow.Stage);

        startedOnline.Clear();
        flow.CompleteOnlineSettlement(SettlementChoice.Retry);
        Assert.Equal(1, flow.Settlements);
        Assert.Single(startedOnline);

        flow.CompleteOnlineDungeonStart(entries[0].Path, 1, new StubRun(entries[0].Path));
        flow.CompleteOnlineDungeonAbort();
        Assert.Equal(1, flow.Failures);
        Assert.Equal(GameStage.Town, flow.Stage);
    }

    [Fact]
    public void OnlineDungeonLog_outputs_sanitized_tokens_without_sensitive_ids_or_paths()
    {
        string[] lines =
        {
            OnlineDungeonLog.Select("enter", 2, 3),
            OnlineDungeonLog.Enter("DungeonGate", true, 2),
            OnlineDungeonLog.Room("Ready", true, 4, 4, 3),
            OnlineDungeonLog.Leave("ReturnReady", 4, 0, 3, "Succeeded", "Succeeded"),
            OnlineDungeonLog.ReturnArea("match"),
            OnlineDungeonLog.Fail("Rejected", "WaitStartMap", 14),
            OnlineDungeonLog.PermissionUpdate(12, 0),
        };
        foreach (string line in lines)
        {
            Assert.StartsWith("DNF_ONLINE stage=dungeon event=", line);
            Assert.DoesNotContain("/", line);
            Assert.DoesNotContain("\\", line);
        }
    }
}
