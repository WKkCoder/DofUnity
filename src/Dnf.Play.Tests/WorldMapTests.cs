using Dnf.Data;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 副本选择界面的世界地图画法(<see cref="WorldMapArt"/>、<see cref="WorldMapCatalog"/>、<see cref="DungeonSelectScreen"/> 的地图模式,证据 E-094)。
/// 精灵是合成的"行号编码"色块(只有尺寸与偏移取自量过的界面图),数据是编的;验证的是布局规则、时间线、点击与导航、缺图时的占位。
/// </summary>
public class WorldMapTests
{
    private sealed class FakeSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) => Frames.TryGetValue((imageName, frame), out sprite!);
    }

    /// <summary>像素 (x, y) = (R=y%256, G=x%256, B=tag, A=255)。</summary>
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

    /// <summary>界面用到的全部帧(尺寸与偏移同量过的界面图,内容是合成的)。</summary>
    private static FakeSprites FullArt()
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
        return f;
    }

    private static DungeonEntry Entry(string name, int id, int x, int y, int style = WorldMapArt.MeasuredBubbleStyle, int slot = 0, int level = 0, bool art = true)
    {
        return new DungeonEntry(
            "dungeon/test/" + name + ".dgn",
            name.ToUpperInvariant(),
            new DungeonCard(id, x, y, style, art ? SlotImage : null, slot, level));
    }

    private static WorldMapScene Scene(FakeSprites sprites, string region = "TESTLAND")
    {
        return new WorldMapScene(sprites, MapImage, 0, region);
    }

    private static string[] Tags(List<SceneSprite> sprites) => sprites.Select(s => s.AnimationPath).ToArray();

    private static SceneSprite Only(List<SceneSprite> sprites, string tag, int nth = 0)
    {
        return sprites.Where(s => s.AnimationPath == tag).ElementAt(nth);
    }

    private static List<SceneSprite> BuildAt(WorldMapArt art, FakeSprites sprites, DungeonEntry[] entries, int selected, long ageMs)
    {
        var list = new List<SceneSprite>();
        int order = 0;
        art.Build(list, ref order, new OverlayArt(), Scene(sprites), entries, selected, ageMs);
        return list;
    }

    // ------------------------------------------------------------------ 时间线

    [Fact]
    public void TheMarkersAndCardsAppearAfterTheIntroBannerLeavesAndFadeInOverThreeHundredMilliseconds()
    {
        Assert.Equal(1300, WorldMapArt.BalloonDelayMs);
        Assert.Equal(0, WorldMapArt.BalloonAlpha(0));
        Assert.Equal(0, WorldMapArt.BalloonAlpha(1299));
        Assert.Equal(0, WorldMapArt.BalloonAlpha(1300));
        Assert.Equal(1, WorldMapArt.BalloonAlpha(1301));
        Assert.Equal(128, WorldMapArt.BalloonAlpha(1450));         // 127.5 往上取整
        Assert.Equal(255, WorldMapArt.BalloonAlpha(1600));
        Assert.Equal(255, WorldMapArt.BalloonAlpha(60000));
    }

    [Fact]
    public void TheIntroBannerFlashesInHoldsAndFlashesOutThenStaysGone()
    {
        Assert.Equal(0, WorldMapArt.MapSelectBannerAlpha(0));
        Assert.Equal(128, WorldMapArt.MapSelectBannerAlpha(150));
        Assert.Equal(255, WorldMapArt.MapSelectBannerAlpha(300));
        Assert.Equal(255, WorldMapArt.MapSelectBannerAlpha(999));
        Assert.Equal(255, WorldMapArt.MapSelectBannerAlpha(1000));
        Assert.Equal(127, WorldMapArt.MapSelectBannerAlpha(1150));
        Assert.Equal(1, WorldMapArt.MapSelectBannerAlpha(1299));
        Assert.Equal(0, WorldMapArt.MapSelectBannerAlpha(1300));
        Assert.Equal(0, WorldMapArt.MapSelectBannerAlpha(9000));
        Assert.Equal(0, WorldMapArt.MapSelectBannerAlpha(-5));
    }

    [Fact]
    public void TheTwoBottomBannersTakeTurnsOnAnEightSecondCycleAndAlwaysAddUpToFullOpacity()
    {
        Assert.Equal(255, WorldMapArt.KeyGuideAlpha(0));
        Assert.Equal(255, WorldMapArt.KeyGuideAlpha(2999));
        Assert.Equal(127, WorldMapArt.KeyGuideAlpha(3500));        // 淡出
        Assert.Equal(0, WorldMapArt.KeyGuideAlpha(4000));
        Assert.Equal(0, WorldMapArt.KeyGuideAlpha(6999));
        Assert.Equal(128, WorldMapArt.KeyGuideAlpha(7500));        // 淡入
        Assert.Equal(255, WorldMapArt.KeyGuideAlpha(8000));        // 下一个周期
        Assert.Equal(127, WorldMapArt.KeyGuideAlpha(8000 * 5 + 3500));
        for (long t = 0; t < 20000; t += 137)
        {
            Assert.Equal(255, WorldMapArt.KeyGuideAlpha(t) + WorldMapArt.ProperChannelAlpha(t));
        }
        Assert.Equal(255, WorldMapArt.ProperChannelAlpha(5000));
    }

    [Fact]
    public void TheLevelTextIsThreeDigitsWithARangeOfOneAndTheTildeIsInTheFont()
    {
        Assert.Equal("Lv. 007~008", WorldMapArt.LevelText(7));
        Assert.Equal("Lv. 000~001", WorldMapArt.LevelText(0));
        Assert.Equal("Lv. 000~001", WorldMapArt.LevelText(-5));
        Assert.Equal("Lv. 099~100", WorldMapArt.LevelText(99));
        Assert.All(WorldMapArt.LevelText(12), c => Assert.True(OverlayArt.HasGlyph(c), "字库缺 " + c));
    }

    // ------------------------------------------------------------------ 点击测试与矩形

    [Fact]
    public void TheSelectedCardCoversTheWholeArtAndTheOthersTheShrunkArtAtTheBubbleOrigin()
    {
        var card = new DungeonCard(1, 300, 200, 13, null, 0, 0);      // 气泡原点 = (264, 123)
        HitRect selected = WorldMapArt.CardRect(card, true);
        Assert.Equal((237, 103, 168, 73), (selected.X, selected.Y, selected.Width, selected.Height));
        HitRect small = WorldMapArt.CardRect(card, false);
        Assert.Equal((264, 123, 118, 52), (small.X, small.Y, small.Width, small.Height));    // 168 × 0.7 = 117.6 → 118,73 × 0.7 = 51.1 → 52
        Assert.True(small.Contains(264, 123));
        Assert.False(small.Contains(264 + 118, 123));                                          // 右、下边不含
        Assert.False(small.Contains(264, 123 + 52));
    }

    [Fact]
    public void ButtonsAreFoundByTheirRectanglesAndEmptySpaceIsNothing()
    {
        Assert.Equal(WorldMapButton.QuickParty, WorldMapArt.HitButton(700, 500));
        Assert.Equal(WorldMapButton.QuickParty, WorldMapArt.HitButton(700 + 96, 500 + 22));
        Assert.Equal(WorldMapButton.None, WorldMapArt.HitButton(700 + 97, 500));
        Assert.Equal(WorldMapButton.Town, WorldMapArt.HitButton(750, 550));
        Assert.Equal(WorldMapButton.Abyss, WorldMapArt.HitButton(650, 550));
        Assert.Equal(WorldMapButton.None, WorldMapArt.HitButton(10, 10));
    }

    [Fact]
    public void TheSelectedCardIsOnTopSoItWinsWhereCardsOverlapAndEntriesWithoutCardsNeverHit()
    {
        // A 与 B 的卡片重叠:B 选中时它的大卡片盖住 A 的小卡片,A 选中时反过来
        var entries = new[] { Entry("a", 1, 300, 200), Entry("b", 2, 320, 210), new DungeonEntry("dungeon/test/c.dgn", "C") };
        Assert.Equal(1, WorldMapArt.HitCard(entries, 1, 270, 130));
        Assert.Equal(0, WorldMapArt.HitCard(entries, 0, 290, 140));
        Assert.Equal(-1, WorldMapArt.HitCard(entries, 0, 5, 5));
        Assert.Equal(-1, WorldMapArt.HitCard(null!, 0, 270, 130));
        Assert.Equal(-1, WorldMapArt.HitCard(entries, -1, 5, 5));
    }

    // ------------------------------------------------------------------ 摆放

    [Fact]
    public void TheScreenIsBuiltBottomToTopInTheMeasuredOrder()
    {
        FakeSprites sprites = FullArt();
        var art = new WorldMapArt(sprites);
        var entries = new[] { Entry("a", 1, 300, 200, level: 7), Entry("b", 2, 500, 350, slot: 1) };
        List<SceneSprite> list = BuildAt(art, sprites, entries, 0, 5000);

        Assert.Equal(
            new[]
            {
                "map-background",
                "map-bubble", "map-bubble",
                "map-marker", "map-marker",
                "map-card", "map-badge",                                    // 没选中的卡片(B)
                "map-card-selected", "map-card-border", "map-badge-selected", "map-level",
                "map-edges",
                "map-channel",                                              // 5 秒时轮到"适当频道"横幅,按键提示是 0 不画
                "map-difficulty-bar", "map-party-icon", "map-region-name",
                "map-button", "map-button-text", "map-button", "map-button-text", "map-button", "map-button-text",
            },
            Tags(list));
        Assert.All(list, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        Assert.Equal(Enumerable.Range(0, list.Count).ToArray(), list.Select(s => s.SortKey).ToArray());
    }

    [Fact]
    public void EveryFrameIsPlacedAtItsControlOriginPlusTheFramesOwnOffset()
    {
        FakeSprites sprites = FullArt();
        var art = new WorldMapArt(sprites);
        var entries = new[] { Entry("a", 1, 300, 200, level: 7), Entry("b", 2, 500, 350, slot: 1) };
        List<SceneSprite> list = BuildAt(art, sprites, entries, 0, 5000);

        // 底图
        Assert.Equal((0, 0), (Only(list, "map-background").ScreenX, Only(list, "map-background").ScreenY));
        // 气泡 = 标记原点 + (-36, -77);标记图自带偏移 (1, 1)
        Assert.Equal((264, 123), (Only(list, "map-bubble", 0).ScreenX, Only(list, "map-bubble", 0).ScreenY));
        Assert.Equal((464, 273), (Only(list, "map-bubble", 1).ScreenX, Only(list, "map-bubble", 1).ScreenY));
        Assert.Equal((301, 201), (Only(list, "map-marker", 0).ScreenX, Only(list, "map-marker", 0).ScreenY));
        Assert.Equal((501, 351), (Only(list, "map-marker", 1).ScreenX, Only(list, "map-marker", 1).ScreenY));

        // 选中的卡片(A):画在气泡原点 + (-27, -20),边框 + (-30, -23),徽章 + (120, -40),等级文字在画的左上角 + (6, 26)
        SceneSprite card = Only(list, "map-card-selected");
        Assert.Equal((237, 103, 168, 73), (card.ScreenX, card.ScreenY, card.Frame.Width, card.Frame.Height));
        Assert.Equal(30, card.Frame.Rgba[2]);                                            // A 的画是 0 号帧(B 通道 = 30)
        Assert.Equal((234, 100), (Only(list, "map-card-border").ScreenX, Only(list, "map-card-border").ScreenY));
        Assert.Equal((384, 83), (Only(list, "map-badge-selected").ScreenX, Only(list, "map-badge-selected").ScreenY));
        Assert.Equal((243, 129), (Only(list, "map-level").ScreenX, Only(list, "map-level").ScreenY));

        // 没选中的卡片(B):缩到 0.7 倍的画在气泡原点,徽章缩小后在原点 + (100, -17)
        SceneSprite small = Only(list, "map-card");
        Assert.Equal((464, 273, 118, 52), (small.ScreenX, small.ScreenY, small.Frame.Width, small.Frame.Height));
        SceneSprite badge = Only(list, "map-badge");
        Assert.Equal((564, 256, 25, 28), (badge.ScreenX, badge.ScreenY, badge.Frame.Width, badge.Frame.Height));

        // 上下边装饰、横幅、难度条、图标、地图名:横幅自带偏移 (124, 0),难度条自带偏移 (13, 0)
        Assert.Equal((124, 572), (Only(list, "map-channel").ScreenX, Only(list, "map-channel").ScreenY));
        Assert.Equal((617, 476), (Only(list, "map-difficulty-bar").ScreenX, Only(list, "map-difficulty-bar").ScreenY));
        Assert.Equal((745, 479), (Only(list, "map-party-icon").ScreenX, Only(list, "map-party-icon").ScreenY));
        // "TESTLAND" 49 像素宽:右对齐会压住图标,所以挪到图标左边(745 - 3 - 49)
        Assert.Equal((693, 482), (Only(list, "map-region-name").ScreenX, Only(list, "map-region-name").ScreenY));

        // 三个按钮与居中的文字(快速组队、返回城镇是常态图,深渊派对是灰的 69 号图)
        Assert.Equal((700, 500), (Only(list, "map-button", 0).ScreenX, Only(list, "map-button", 0).ScreenY));
        Assert.Equal((700, 541), (Only(list, "map-button", 1).ScreenX, Only(list, "map-button", 1).ScreenY));
        Assert.Equal((600, 541), (Only(list, "map-button", 2).ScreenX, Only(list, "map-button", 2).ScreenY));
        Assert.Equal(19, Only(list, "map-button", 0).Frame.Rgba[2]);
        Assert.Equal(20, Only(list, "map-button", 2).Frame.Rgba[2]);
        Assert.Equal((715, 507), (Only(list, "map-button-text", 0).ScreenX, Only(list, "map-button-text", 0).ScreenY));   // 67 宽 × 9 高居中在 97 × 23 里
        Assert.Equal((736, 548), (Only(list, "map-button-text", 1).ScreenX, Only(list, "map-button-text", 1).ScreenY));
    }

    [Fact]
    public void ABubbleStyleChoosesTheBubbleFrameButTheGeometryStaysTheMeasuredOne()
    {
        FakeSprites sprites = FullArt();
        sprites.Frames[(WorldMapArt.UiImage, WorldMapArt.BubbleFrameBase + 4)] = Coded(100, 90, 50);
        var art = new WorldMapArt(sprites);
        List<SceneSprite> list = BuildAt(art, sprites, new[] { Entry("a", 1, 300, 200, style: 4) }, 0, 5000);
        SceneSprite bubble = Only(list, "map-bubble");
        Assert.Equal(50, bubble.Frame.Rgba[2]);                                            // 取的是样式 4 的图
        Assert.Equal((264, 123), (bubble.ScreenX, bubble.ScreenY));                        // 位置仍按样式 13 量出的偏移
    }

    [Fact]
    public void BeforeTheBannerLeavesThereAreNoMarkersOrCardsAndDuringTheFadeTheyAreTranslucent()
    {
        FakeSprites sprites = FullArt();
        var art = new WorldMapArt(sprites);
        DungeonEntry[] entries = { Entry("a", 1, 300, 200), Entry("b", 2, 500, 350) };

        List<SceneSprite> early = BuildAt(art, sprites, entries, 0, 500);
        Assert.DoesNotContain(early, s => s.AnimationPath == "map-marker" || s.AnimationPath == "map-card" || s.AnimationPath == "map-card-selected" || s.AnimationPath == "map-bubble");
        Assert.Equal(255, Only(early, "map-intro").TintAlpha);                              // 地图名横幅正在停留
        Assert.Equal(255, Only(early, "map-key-guide").TintAlpha);                          // 开场先是按键提示横幅
        Assert.DoesNotContain(early, s => s.AnimationPath == "map-channel");                 // 不透明度 0 的不画

        List<SceneSprite> fading = BuildAt(art, sprites, entries, 0, 1450);
        Assert.All(fading.Where(s => s.AnimationPath is "map-marker" or "map-bubble" or "map-card" or "map-card-selected" or "map-badge" or "map-level"), s => Assert.Equal(128, s.TintAlpha));
        Assert.DoesNotContain(fading, s => s.AnimationPath == "map-intro");                 // 地图名横幅已经退场

        List<SceneSprite> settled = BuildAt(art, sprites, entries, 0, 1700);
        Assert.All(settled.Where(s => s.AnimationPath is "map-marker" or "map-card"), s => Assert.Equal(255, s.TintAlpha));
    }

    [Fact]
    public void WithoutAnySelectionEveryCardIsSmallAndTheScaledArtIsSharedBetweenBuilds()
    {
        FakeSprites sprites = FullArt();
        var art = WorldMapArt.For(sprites)!;
        Assert.Same(art, WorldMapArt.For(sprites));
        DungeonEntry[] entries = { Entry("a", 1, 300, 200), Entry("b", 2, 500, 350, slot: 1) };

        List<SceneSprite> first = BuildAt(art, sprites, entries, -1, 5000);
        Assert.Equal(2, first.Count(s => s.AnimationPath == "map-card"));
        Assert.DoesNotContain(first, s => s.AnimationPath == "map-card-selected" || s.AnimationPath == "map-level");
        List<SceneSprite> second = BuildAt(art, sprites, entries, -1, 5016);
        Assert.Same(Only(first, "map-card", 0).Frame, Only(second, "map-card", 0).Frame);       // 同一个缩放结果对象,GPU 端不会反复建贴图
        Assert.Same(Only(first, "map-badge", 1).Frame, Only(second, "map-badge", 1).Frame);
        Assert.Null(WorldMapArt.For(null!));
    }

    [Fact]
    public void MissingFramesAreSkippedAndCardsWithoutArtGetAPlaceholderWithTheName()
    {
        var empty = new FakeSprites();
        var art = new WorldMapArt(empty);
        var entries = new[] { Entry("alpha", 1, 300, 200, art: false), Entry("beta", 2, 500, 350, art: false) };
        List<SceneSprite> list = BuildAt(art, empty, entries, 0, 5000);

        Assert.Contains(list, s => s.AnimationPath == "map-backdrop");                       // 没有底图:纯色底
        Assert.DoesNotContain(list, s => s.AnimationPath == "map-marker" || s.AnimationPath == "map-bubble" || s.AnimationPath == "map-badge");
        SceneSprite selected = Only(list, "map-card-selected");
        Assert.Equal((237, 103, 168, 73), (selected.ScreenX, selected.ScreenY, selected.Frame.Width, selected.Frame.Height));
        SceneSprite small = Only(list, "map-card");
        Assert.Equal((464, 273, 118, 52), (small.ScreenX, small.ScreenY, small.Frame.Width, small.Frame.Height));
        Assert.Equal(2, list.Count(s => s.AnimationPath == "map-card-name"));                // 两张占位卡片各写着副本名
        Assert.Equal(3, list.Count(s => s.AnimationPath == "map-button"));                   // 按钮缺图也有占位块,文字照写
        Assert.Equal(3, list.Count(s => s.AnimationPath == "map-button-text"));
        Assert.All(list, s => Assert.NotNull(s.Frame));
    }

    [Fact]
    public void AShortRegionNameIsRightAlignedAtTheEndOfTheDifficultyBar()
    {
        FakeSprites sprites = FullArt();
        var art = new WorldMapArt(sprites);
        var list = new List<SceneSprite>();
        int order = 0;
        art.Build(list, ref order, new OverlayArt(), Scene(sprites, "ABC"), new DungeonEntry[0], -1, 5000);
        SceneSprite name = Only(list, "map-region-name");
        Assert.Equal(797 - OverlayArt.TextWidth("ABC", 1), name.ScreenX);                    // 右端对齐在 797
    }

    // ------------------------------------------------------------------ 界面逻辑

    [Fact]
    public void TheArrowKeysWalkTheMarkersFromTopToBottomAndEntriesWithoutCardsComeLast()
    {
        FakeSprites sprites = FullArt();
        var entries = new[]
        {
            Entry("low", 1, 300, 400),                   // 0:最下面
            Entry("right", 2, 500, 100),                 // 1:最上面一排的右边
            Entry("left", 3, 100, 100),                  // 2:最上面一排的左边
            new DungeonEntry("dungeon/test/none.dgn", "NONE"),   // 3:没有卡片
        };
        var screen = new DungeonSelectScreen(entries, 0, Scene(sprites));
        Assert.Equal(0, screen.Index);
        screen.Move(-1);
        Assert.Equal(1, screen.Index);                   // 比最下面的高一格:同一高度里靠右的那个
        screen.Move(-1);
        Assert.Equal(2, screen.Index);
        screen.Move(-1);
        Assert.Equal(2, screen.Index);                   // 到头不循环
        screen.Move(1);
        screen.Move(1);
        Assert.Equal(0, screen.Index);
        screen.Move(1);
        Assert.Equal(3, screen.Index);
        screen.Move(5);
        Assert.Equal(3, screen.Index);
        screen.Move(-9);
        Assert.Equal(2, screen.Index);

        // 占位界面按列表顺序走
        var placeholder = new DungeonSelectScreen(entries, 0);
        placeholder.Move(1);
        Assert.Equal(1, placeholder.Index);
    }

    [Fact]
    public void ClickingACardSelectsItAndClickingTheSelectedOneEntersTheTownButtonGoesBackAndPartyDoesNothing()
    {
        FakeSprites sprites = FullArt();
        var entries = new[] { Entry("a", 1, 300, 200), Entry("b", 2, 500, 350) };
        var screen = new DungeonSelectScreen(entries, 0, Scene(sprites));

        Assert.True(screen.Click(470, 280));             // B 的小卡片
        Assert.Equal((1, SelectOutcome.None), (screen.Index, screen.Outcome));
        Assert.True(screen.Click(500, 300));             // B 现在是选中的大卡片
        Assert.Equal(SelectOutcome.Enter, screen.Outcome);
        Assert.False(screen.Click(750, 550));            // 已经有结果,不再响应

        var other = new DungeonSelectScreen(entries, 0, Scene(sprites));
        Assert.True(other.Click(750, 500));              // 快速组队:单机没有,点了没反应但算点中了界面
        Assert.True(other.Click(650, 550));              // 深渊派对:灰的
        Assert.Equal(SelectOutcome.None, other.Outcome);
        Assert.False(other.Click(5, 5));                 // 点在空处
        Assert.True(other.Click(750, 550));              // 返回城镇
        Assert.Equal(SelectOutcome.Back, other.Outcome);
    }

    [Fact]
    public void ThePlaceholderScreenIgnoresClicksAndTheClockOnlyMovesWhenAdvanced()
    {
        var placeholder = new DungeonSelectScreen(new[] { new DungeonEntry("dungeon/test/a.dgn", null) });
        Assert.False(placeholder.Click(750, 550));
        Assert.Equal(SelectOutcome.None, placeholder.Outcome);
        Assert.Null(placeholder.World);

        FakeSprites sprites = FullArt();
        var screen = new DungeonSelectScreen(new[] { Entry("a", 1, 300, 200) }, 0, Scene(sprites));
        Assert.Equal(0, screen.AgeMs);
        screen.Advance(100.5);
        screen.Advance(-50);                             // 负数当 0
        screen.Advance(0.5);
        Assert.Equal(101, screen.AgeMs);
        // Build 的参数是宿主的墙钟,界面自己的时间只看 Advance:参数不同、画出来一样
        Assert.Equal(Tags(screen.Build(0)), Tags(screen.Build(987654)));
    }

    [Fact]
    public void AnEmptyWorldMapStillDrawsTheMapAndTheButtons()
    {
        FakeSprites sprites = FullArt();
        var screen = new DungeonSelectScreen(new DungeonEntry[0], 0, Scene(sprites));
        Assert.Equal(-1, screen.Index);
        screen.Move(1);
        screen.Confirm();
        Assert.Equal(SelectOutcome.None, screen.Outcome);
        Assert.Contains(screen.Build(0), s => s.AnimationPath == "map-background");
        Assert.Contains(screen.Build(0), s => s.AnimationPath == "map-button");
    }

    // ------------------------------------------------------------------ 目录:数据怎么拼成界面

    private static ScriptLine Line(string tag, params object[] values)
    {
        var list = new List<ScriptValue>();
        foreach (object v in values)
        {
            list.Add(v is int i ? new ScriptValue(ScriptValueKind.Int, i, null) : new ScriptValue(ScriptValueKind.String, 0, (string)v));
        }
        return new ScriptLine(tag, list.ToArray());
    }

    [Fact]
    public void TheCatalogKeepsOnlyUnconditionalDungeonsThatHaveAMarkerAndAPathInTheListedOrder()
    {
        var def = new WorldMapDef("worldmap/foo.wdm", "sprite/world/foo.img", 2, "worldmap/ui/foo.ui", new[]
        {
            new WorldMapDungeon(41, -1),
            new WorldMapDungeon(42, -1),
            new WorldMapDungeon(43, 900),     // 有解锁条件:先不显示
            new WorldMapDungeon(44, -1),      // 界面脚本里没有它的标记
            new WorldMapDungeon(45, -1),      // 副本列表里没有它的路径
        });
        UiScript ui = UiScript.Parse(new[]
        {
            Line("[ui controls]", "[image]", "IDC_BG", 0, 0, 1, "World/Foo.img", 1, 0),
            Line("[ui controls]", "[balloon]", "IDC_B1", 410, 305, 1, "World/Ui.img", 3, 1, 13, 41),
            Line("[ui controls]", "[balloon]", "IDC_B2", 220, 140, 1, "World/Ui.img", 3, 1, 4, 42),
            Line("[ui controls]", "[balloon]", "IDC_B3", 10, 20, 1, "World/Ui.img", 3, 1, 13, 43),
            Line("[ui controls]", "[balloon]", "IDC_B5", 30, 40, 1, "World/Ui.img", 3, 1, 13, 45),
        })!;
        var list = new Dictionary<int, string> { [41] = "dungeon/act/one.dgn", [42] = "dungeon/act/two.dgn", [43] = "dungeon/act/three.dgn", [44] = "dungeon/act/four.dgn" };
        var asked = new List<string>();
        var sprites = new FakeSprites();

        WorldMapSet set = WorldMapCatalog.Build(def, ui, list, path =>
        {
            asked.Add(path);
            return path.EndsWith("one.dgn") ? new DungeonCardDef(path, "sprite/world/slots/foo.img", 3, 1, 1, 17, 21) : null!;
        }, sprites);

        Assert.Equal(new[] { "dungeon/act/one.dgn", "dungeon/act/two.dgn" }, set.Entries.Select(e => e.Path).ToArray());
        Assert.Equal(new[] { "dungeon/act/one.dgn", "dungeon/act/two.dgn" }, asked);          // 只读留下来的副本
        Assert.Equal(new[] { "ONE", "TWO" }, set.Entries.Select(e => e.Name).ToArray());

        DungeonCard one = set.Entries[0].Card!;
        Assert.Equal((41, 410, 305, 13), (one.DungeonId, one.MarkerX, one.MarkerY, one.BubbleStyle));
        Assert.Equal(("sprite/world/slots/foo.img", 3, 17), (one.SlotImage, one.SlotFrame, one.MinimumLevel));
        DungeonCard two = set.Entries[1].Card!;
        Assert.Equal((42, 220, 140, 4), (two.DungeonId, two.MarkerX, two.MarkerY, two.BubbleStyle));      // 样式取标记里"气泡样式"那一格
        Assert.Null(two.SlotImage);                                                                          // 副本定义读不到:卡片没有画
        Assert.Equal(0, two.MinimumLevel);

        Assert.Equal(("sprite/world/foo.img", 2, "FOO"), (set.Scene.MapImage, set.Scene.MapFrame, set.Scene.RegionName));
        Assert.Same(def, set.Def);
    }

    [Fact]
    public void ABalloonWithTooFewValuesUsesTheMeasuredBubbleStyleAndAMissingUiScriptMeansNoEntries()
    {
        var def = new WorldMapDef("worldmap/bar.wdm", null, 0, "worldmap/ui/bar.ui", new[] { new WorldMapDungeon(9, -1) });
        UiScript ui = UiScript.Parse(new[] { Line("[ui controls]", "[balloon]", "IDC_B", 1, 2, 1, "World/Ui.img", 9) })!;
        var list = new Dictionary<int, string> { [9] = "dungeon/x/nine.dgn" };
        var sprites = new FakeSprites();

        WorldMapSet set = WorldMapCatalog.Build(def, ui, list, null!, sprites);
        Assert.Single(set.Entries);
        Assert.Equal(WorldMapArt.MeasuredBubbleStyle, set.Entries[0].Card!.BubbleStyle);
        Assert.Null(set.Entries[0].Card!.SlotImage);                                 // 没有读副本定义的函数:没有画

        Assert.Empty(WorldMapCatalog.Build(def, null, list, null!, sprites).Entries);
        Assert.Null(set.Scene.MapImage);
        Assert.Throws<ArgumentNullException>(() => WorldMapCatalog.Build(null!, ui, list, null!, sprites));
    }

    // ------------------------------------------------------------------ 流程:选择界面带上世界地图,自动机等到卡片出来才确认

    [Fact]
    public void TheFlowOpensTheSelectionOnTheWorldMapWhenGivenOneAndThePlaceholderOtherwise()
    {
        FakeSprites sprites = FullArt();
        var entries = new[] { Entry("a", 1, 300, 200), Entry("b", 2, 500, 350) };
        var withMap = new GameFlow(() => new GameFlowTests.FakeHero("town"), (p, r) => null!, entries, Scene(sprites));
        withMap.EnterSelect();
        Assert.NotNull(withMap.Select.World);
        Assert.Equal("TESTLAND", withMap.Select.World.RegionName);

        var plain = new GameFlow(() => new GameFlowTests.FakeHero("town"), (p, r) => null!, entries);
        plain.EnterSelect();
        Assert.Null(plain.Select.World);
    }

    [Fact]
    public void TheBotWaitsUntilTheCardsHaveFadedInBeforeConfirmingAndTheScreenClockRuns()
    {
        Assert.True(FlowBot.SelectDwellMs >= WorldMapArt.BalloonDelayMs + WorldMapArt.BalloonFadeMs);

        FakeSprites sprites = FullArt();
        var entries = new[] { Entry("a", 1, 300, 200), Entry("b", 2, 500, 350) };
        var flow = new GameFlow(() => new GameFlowTests.FakeHero("town"), (p, r) => null!, entries, Scene(sprites));
        flow.EnterSelect("start");
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown });

        double t = 0;
        while (t + 16.0 < FlowBot.SelectDwellMs)
        {
            bot.Advance(flow, 16.0);
            t += 16.0;
            Assert.Equal(GameStage.DungeonSelect, flow.Stage);
            Assert.Equal(SelectOutcome.None, flow.Select.Outcome);
        }
        Assert.InRange(flow.Select.AgeMs, (long)(t - 1), (long)(t + 1));                 // 界面的时钟跟着自动机走
        Assert.True(flow.Select.AgeMs >= WorldMapArt.BalloonDelayMs + WorldMapArt.BalloonFadeMs);
    }
}
