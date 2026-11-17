using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网选角界面的画法(<see cref="CharacterSelectArt"/> 的联网 <c>Build</c>):文字走文字系统(<see cref="ShellPaint"/>),位置按"墨迹中心"摆(E-096 基线上量出的行与列,
/// 下面的表格式用例把基线数字写成数据),不能进入的角色多一行红字标记,滑块按行数与窗口位置画,创立与删除灰着,开始按钮等待时灰着,提示条居中在"开始"按钮上方;
/// 没有字体时是英文占位;没有素材帧时退回纯色块。离线画法不在这里测(<see cref="CharacterSelectTests"/> 与离线出图逐字节回归覆盖)。
/// 精灵与字体都是合成的,验证的是布局与接线,不含任何真实素材。
/// </summary>
public class CharacterSelectOnlineArtTests
{
    private static ShellPaint PaintOf(IGlyphFonts fonts) => new ShellPaint(new OverlayArt { Localize = false, Fonts = fonts });

    private static List<SceneSprite> Build(CharacterSelectScreen screen) => screen.Build(0);

    private static CharacterRoster RosterWith(params OnlineCharacterEntry[] entries) => OnlineCharacterRoster.From(RosterKit.List(entries));

    private static List<SceneSprite> Tagged(List<SceneSprite> sprites, string tag) => SpriteQuery.Tagged(sprites, tag);

    private static (int X, int Y) InkCentre(SceneSprite sprite)
    {
        var ink = SpriteQuery.Ink(sprite);
        return ((ink.Left + ink.Right + 1) / 2, (ink.Top + ink.Bottom + 1) / 2);
    }

    // ------------------------------------------------------------------ 滑块几何

    [Theory]
    [InlineData(0, 351)]
    [InlineData(1, 351)]
    [InlineData(2, 351)]        // 两行以内与离线界面画的 351 高一模一样(基线上账号的槽位比一屏多一点,E-096)
    [InlineData(3, 237)]
    [InlineData(4, 179)]
    [InlineData(5, 143)]
    [InlineData(6, 120)]
    [InlineData(10, 72)]
    [InlineData(100, 23)]       // 再多的行也不比滑块图本身(23)更矮
    public void TheThumbHeightShrinksWithTheNumberOfRows(int rows, int expected)
    {
        Assert.Equal(expected, CharacterSelectArt.ThumbHeightFor(rows));
    }

    [Fact]
    public void TheThumbHeightNeverGrowsAsRowsAreAdded()
    {
        int previous = int.MaxValue;
        for (int rows = 0; rows < 80; rows++)
        {
            int height = CharacterSelectArt.ThumbHeightFor(rows);
            Assert.InRange(height, CharacterSelectArt.ScrollThumbMinHeight, CharacterSelectArt.ScrollTrackHeight);
            Assert.True(height <= previous);
            previous = height;
        }
    }

    [Fact]
    public void TheThumbTravelsFromTheTopOfTheTrackToItsBottom()
    {
        int trackTop = CharacterSelectArt.ScrollTop + CharacterSelectArt.ScrollArrowHeight;
        Assert.Equal(98, trackTop);
        Assert.Equal(trackTop, CharacterSelectArt.ThumbTop(0, 2));                    // 两行:不动,贴着上沿(基线 y 98)
        Assert.Equal(trackTop, CharacterSelectArt.ThumbTop(0, 3));

        foreach (int rows in new[] { 3, 4, 5, 6, 10, 40 })
        {
            int height = CharacterSelectArt.ThumbHeightFor(rows);
            int maxFirst = rows - CharacterRoster.Rows;
            Assert.Equal(trackTop, CharacterSelectArt.ThumbTop(0, rows));
            Assert.Equal(CharacterSelectArt.ScrollBottom, CharacterSelectArt.ThumbTop(maxFirst, rows) + height);   // 滚到底:滑块下沿贴着轨道下沿
            int last = trackTop - 1;
            for (int first = 0; first <= maxFirst; first++)
            {
                int top = CharacterSelectArt.ThumbTop(first, rows);
                Assert.True(top > last);                                              // 每滚一行滑块往下走
                last = top;
            }
        }
    }

    [Fact]
    public void TheThumbTopClampsTheWindowPosition()
    {
        Assert.Equal(CharacterSelectArt.ThumbTop(0, 5), CharacterSelectArt.ThumbTop(-3, 5));
        Assert.Equal(CharacterSelectArt.ThumbTop(3, 5), CharacterSelectArt.ThumbTop(99, 5));
    }

    [Fact]
    public void ScrollbarHitsDependOnTheBarThePageAndTheThumb()
    {
        const int rows = 5;                                    // 滑块 143 高;窗口第 1 行时滑块在 181 到 323
        int x = CharacterSelectArt.ScrollX + 4;
        int top = CharacterSelectArt.ThumbTop(1, rows);
        int height = CharacterSelectArt.ThumbHeightFor(rows);
        Assert.Equal((181, 143), (top, height));

        Assert.Equal(CharacterSelectScrollHit.None, CharacterSelectArt.HitScroll(x, CharacterSelectArt.ScrollTop - 1, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.Up, CharacterSelectArt.HitScroll(x, CharacterSelectArt.ScrollTop, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.Up, CharacterSelectArt.HitScroll(x, CharacterSelectArt.ScrollTop + 12, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.PageUp, CharacterSelectArt.HitScroll(x, CharacterSelectArt.ScrollTop + 13, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.PageUp, CharacterSelectArt.HitScroll(x, top - 1, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.Thumb, CharacterSelectArt.HitScroll(x, top, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.Thumb, CharacterSelectArt.HitScroll(x, top + height - 1, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.PageDown, CharacterSelectArt.HitScroll(x, top + height, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.PageDown, CharacterSelectArt.HitScroll(x, CharacterSelectArt.ScrollBottom - 1, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.Down, CharacterSelectArt.HitScroll(x, CharacterSelectArt.ScrollBottom, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.Down, CharacterSelectArt.HitScroll(x, CharacterSelectArt.ScrollBottom + 12, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.None, CharacterSelectArt.HitScroll(x, CharacterSelectArt.ScrollBottom + 13, 1, rows));

        Assert.Equal(CharacterSelectScrollHit.None, CharacterSelectArt.HitScroll(CharacterSelectArt.ScrollX - 1, top + 5, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.Thumb, CharacterSelectArt.HitScroll(CharacterSelectArt.ScrollX, top + 5, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.Thumb, CharacterSelectArt.HitScroll(CharacterSelectArt.ScrollX + 8, top + 5, 1, rows));
        Assert.Equal(CharacterSelectScrollHit.None, CharacterSelectArt.HitScroll(CharacterSelectArt.ScrollX + 9, top + 5, 1, rows));
    }

    // ------------------------------------------------------------------ 墨迹中心

    private static SpriteFrame Frame(int width, int height, params (int X, int Y, byte Alpha)[] inked)
    {
        var rgba = new byte[width * height * 4];
        foreach (var (x, y, alpha) in inked)
        {
            rgba[(y * width + x) * 4 + 3] = alpha;
        }
        return new SpriteFrame(width, height, 0, 0, width, height, rgba);
    }

    [Fact]
    public void TheInkCentreIsTheCentreOfTheBoxAroundTheNonTransparentPixels()
    {
        CharacterSelectArt.InkCenter(Frame(6, 4, (1, 1, 255), (4, 2, 255)), out int x, out int y);
        Assert.Equal((3, 2), (x, y));                            // (1 + 4 + 1) / 2、(1 + 2 + 1) / 2

        CharacterSelectArt.InkCenter(Frame(10, 10, (5, 7, 255)), out x, out y);
        Assert.Equal((5, 7), (x, y));                            // 一个像素:就是它自己

        CharacterSelectArt.InkCenter(Frame(10, 10, (2, 3, 1), (7, 8, 255)), out x, out y);
        Assert.Equal((5, 6), (x, y));                            // 透明度 1 也算墨迹;偶数跨度向下取整:(2 + 7 + 1) / 2、(3 + 8 + 1) / 2

        CharacterSelectArt.InkCenter(Frame(10, 10, (0, 0, 255), (9, 9, 255)), out x, out y);
        Assert.Equal((5, 5), (x, y));                            // 铺满整张帧
    }

    [Fact]
    public void ABlankFrameUsesTheFrameCentreAndTheResultIsStable()
    {
        SpriteFrame blank = Frame(7, 9);
        CharacterSelectArt.InkCenter(blank, out int x, out int y);
        Assert.Equal((3, 4), (x, y));
        CharacterSelectArt.InkCenter(blank, out int x2, out int y2);
        Assert.Equal((x, y), (x2, y2));

        SpriteFrame ink = Frame(8, 8, (3, 3, 255));
        CharacterSelectArt.InkCenter(ink, out x, out y);
        CharacterSelectArt.InkCenter(ink, out x2, out y2);
        Assert.Equal((3, 3), (x2, y2));
        Assert.Equal((x, y), (x2, y2));
    }

    [Fact]
    public void TheInkCentreOfNothingIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => CharacterSelectArt.InkCenter(null, out _, out _));
    }

    // ------------------------------------------------------------------ 两行字

    [Fact]
    public void EachCharacterGetsAnInkCentredNameLineAndClassLineAtTheMeasuredBaselinePosition()
    {
        CharacterSelectScreen screen = CharSelectKit.Online(7);                 // 第 0 到 6 格:第二行有一个
        List<SceneSprite> captions = Tagged(Build(screen), "select-char-caption");
        Assert.Equal(14, captions.Count);

        // 基线 S02-client-04 第 0 格:第一行墨迹在槽位框内第 172 到 183 行、第二行 185 到 196 行,列中心在槽位框左边 + 60 上下(E-096)
        (int FirstInkRow, int LastInkRow)[] baselineRows = { (172, 183), (185, 196) };
        for (int cell = 0; cell < 7; cell++)
        {
            CharacterSelectArt.SlotOrigin(cell, out int sx, out int sy);
            for (int line = 0; line < 2; line++)
            {
                int baselineCentreY = (baselineRows[line].FirstInkRow + baselineRows[line].LastInkRow + 1) / 2;   // 178、191
                (int x, int y) = InkCentre(captions[cell * 2 + line]);
                Assert.Equal(sx + CharacterSelectArt.FootDX, x);
                Assert.Equal(sy + baselineCentreY, y);
            }
        }
    }

    [Fact]
    public void TheCaptionLinesAreTheSharedPaintLinesWithTheMeasuredColourAndSize()
    {
        var fonts = new SizedFonts();
        CharacterSelectScreen screen = CharSelectKit.Online(3, 0, fonts);
        List<SceneSprite> captions = Tagged(Build(screen), "select-char-caption");
        ShellPaint expected = PaintOf(new SizedFonts());
        const int width = CharacterSelectArt.SlotWidth - 4;
        string[] jobs = { "SWORDMAN", "FIGHTER", "GUNNER" };

        for (int i = 0; i < 3; i++)
        {
            SpriteFrame name = expected.Line("Lv" + (i + 1) + " 角色" + (i + 1), CharacterSelectArt.OnlineCaptionPx, CharacterSelectArt.CaptionColor, OverlayArt.Black, width);
            SpriteFrame job = expected.Line(jobs[i], CharacterSelectArt.OnlineCaptionPx, CharacterSelectArt.CaptionColor, OverlayArt.Black, width);
            Assert.True(ShellFrames.Same(name, captions[i * 2].Frame), "第一行 " + i);
            Assert.True(ShellFrames.Same(job, captions[i * 2 + 1].Frame), "第二行 " + i);
            Assert.True(ShellFrames.HasColor(captions[i * 2].Frame, CharacterSelectArt.CaptionColor));
        }
        Assert.Equal(12, CharacterSelectArt.OnlineCaptionPx);
        Assert.Equal(0xD1B994u, CharacterSelectArt.CaptionColor);
        Assert.Contains(12, fonts.Requested);
    }

    [Fact]
    public void TheClassLineUsesTheTitleSourceWhenItKnowsTheName()
    {
        var titles = new StubTitles();
        CharacterSelectScreen screen = CharSelectKit.Online(2, 0, new SizedFonts(), null, titles);
        List<SceneSprite> captions = Tagged(Build(screen), "select-char-caption");
        ShellPaint expected = PaintOf(new SizedFonts());
        SpriteFrame job = expected.Line("职0转0觉0", 12, CharacterSelectArt.CaptionColor, OverlayArt.Black, 115);
        Assert.True(ShellFrames.Same(job, captions[1].Frame));
    }

    [Fact]
    public void ANameOrTitleThatIsInTheTranslationTableIsDrawnAsItIsNotTranslated()
    {
        // 名字与称号是数据文字、不是界面占位词:就算恰好与 UiText 里某句占位英文一模一样,也要原样画出(这张画面的画笔不翻译)
        const string phrase = "COOLING DOWN";                                  // 整句例子(TextSystemTests 里也用它),译文是"冷却中"
        Assert.True(UiText.HasTranslation(phrase));                            // 样本失效(表里没有了)时在这里响亮地报出来,而不是让下面的断言悄悄变弱
        Assert.NotEqual(phrase, UiText.Translate(phrase));

        var titles = new StubTitles { Name = (job, grow, awakening) => phrase };
        CharacterRoster roster = OnlineCharacterRoster.From(RosterKit.List(RosterKit.Entry(phrase)), titles);
        List<SceneSprite> captions = Tagged(Build(CharSelectKit.OnlineOf(roster, new SizedFonts())), "select-char-caption");
        Assert.Equal(2, captions.Count);

        ShellPaint asIs = PaintOf(new SizedFonts());
        SpriteFrame name = asIs.Line("Lv1 " + phrase, 12, CharacterSelectArt.CaptionColor, OverlayArt.Black, 115);
        SpriteFrame job = asIs.Line(phrase, 12, CharacterSelectArt.CaptionColor, OverlayArt.Black, 115);
        Assert.True(ShellFrames.Same(name, captions[0].Frame), "第一行");
        Assert.True(ShellFrames.Same(job, captions[1].Frame), "第二行");

        // 对照:把译文画出来是另一张图——上面的"原样"断言分得出译文与原文
        SpriteFrame translated = asIs.Line(UiText.Translate(phrase), 12, CharacterSelectArt.CaptionColor, OverlayArt.Black, 115);
        Assert.False(ShellFrames.Same(translated, captions[1].Frame));
    }

    [Fact]
    public void AnEmptyNameShowsOnlyTheLevel()
    {
        CharacterRoster roster = OnlineCharacterRoster.From(RosterKit.List(RosterKit.EntryBytes(new byte[] { 0xF8 }, level: 9)));    // 非法名字 → 空名字
        CharacterSelectScreen screen = CharSelectKit.OnlineOf(roster);
        List<SceneSprite> captions = Tagged(Build(screen), "select-char-caption");
        SpriteFrame level = PaintOf(new SizedFonts()).Line("Lv9", 12, CharacterSelectArt.CaptionColor, OverlayArt.Black, 115);
        Assert.True(ShellFrames.Same(level, captions[0].Frame));
    }

    [Fact]
    public void ALongNameShrinksThenTruncatesAndStaysInsideItsSlot()
    {
        string name = new string('W', 30);
        CharacterRoster roster = OnlineCharacterRoster.From(RosterKit.List(RosterKit.Entry(name), RosterKit.Entry("短")));
        var fonts = new SizedFonts();
        List<SceneSprite> captions = Tagged(Build(CharSelectKit.OnlineOf(roster, fonts)), "select-char-caption");

        Assert.True(captions[0].Frame.Width <= CharacterSelectArt.SlotWidth, "宽 " + captions[0].Frame.Width);
        Assert.Contains(8, fonts.Requested);                    // 先缩小字号(最多缩 4 像素:12 → 8)
        CharacterSelectArt.SlotOrigin(0, out int sx, out _);
        var ink = SpriteQuery.Ink(captions[0]);
        Assert.True(ink.Left >= sx && ink.Right < sx + CharacterSelectArt.SlotWidth, "墨迹 " + ink);
        Assert.True(captions[2].Frame.Width < captions[0].Frame.Width);       // 短名字没有被拉宽
    }

    // ------------------------------------------------------------------ 不能进入的标记

    [Fact]
    public void ABlockedCharacterGetsARedMarkerLineAndAnEnterableOneDoesNot()
    {
        CharacterRoster roster = OnlineCharacterRoster.From(RosterKit.List(
            RosterKit.Entry("甲"), RosterKit.Entry("乙", x1: 1), RosterKit.Entry("丙", d2: 4), RosterKit.Entry("丁", status: 8), RosterKit.Entry("戊")));
        var fonts = new RecordingFonts();
        List<SceneSprite> sprites = Build(CharSelectKit.OnlineOf(roster, fonts));
        List<SceneSprite> markers = Tagged(sprites, "select-char-marker");

        Assert.Equal(3, markers.Count);                          // 乙、丙、丁
        Assert.True(fonts.Asked("不可进入"));
        Assert.True(fonts.Asked("待删除"));
        foreach (SceneSprite marker in markers)
        {
            Assert.True(ShellFrames.HasColor(marker.Frame, ShellPaint.Red));
        }
        int[] cells = { 1, 2, 3 };
        for (int i = 0; i < 3; i++)
        {
            CharacterSelectArt.SlotOrigin(cells[i], out int sx, out int sy);
            (int x, int y) = InkCentre(markers[i]);
            Assert.Equal((sx + 60, sy + 178 + 2 * 13), (x, y));                 // 第三行:再往下一个行距
        }
    }

    [Fact]
    public void TheMarkerWordsDependOnTheBlockAndTheLanguage()
    {
        Assert.Equal(string.Empty, CharacterSelectText.Marker(CharacterBlock.None, true));
        Assert.Equal("待删除", CharacterSelectText.Marker(CharacterBlock.PendingDelete, true));
        Assert.Equal("不可进入", CharacterSelectText.Marker(CharacterBlock.NameFlag, true));
        Assert.Equal("不可进入", CharacterSelectText.Marker(CharacterBlock.Status, true));
        Assert.Equal(string.Empty, CharacterSelectText.Marker(CharacterBlock.None, false));
        Assert.Equal("DELETING", CharacterSelectText.Marker(CharacterBlock.PendingDelete, false));
        Assert.Equal("LOCKED", CharacterSelectText.Marker(CharacterBlock.NameFlag, false));
        Assert.Equal("LOCKED", CharacterSelectText.Marker(CharacterBlock.Status, false));
    }

    // ------------------------------------------------------------------ 按钮

    [Fact]
    public void TheFourSmallButtonsCarryInkCentredTextAndCreateAndDeleteAreGrey()
    {
        List<SceneSprite> sprites = Build(CharSelectKit.Online(3));
        List<SceneSprite> grey = Tagged(sprites, "select-char-button-disabled");
        List<SceneSprite> live = Tagged(sprites, "select-char-button");
        List<SceneSprite> texts = Tagged(sprites, "select-char-button-text");

        Assert.Equal(new[] { CharacterSelectArt.CreateButtonX, CharacterSelectArt.DeleteButtonX }, grey.Select(s => s.ScreenX));
        Assert.Equal(new[] { CharacterSelectArt.ChannelButtonX, CharacterSelectArt.QuitButtonX }, live.Select(s => s.ScreenX));
        Assert.All(grey.Concat(live), s => Assert.Equal(CharacterSelectArt.SmallButtonY, s.ScreenY));
        Assert.Equal(4, texts.Count);

        int[] xs = { CharacterSelectArt.CreateButtonX, CharacterSelectArt.DeleteButtonX, CharacterSelectArt.ChannelButtonX, CharacterSelectArt.QuitButtonX };
        for (int i = 0; i < 4; i++)
        {
            (int x, int y) = InkCentre(texts[i]);
            // 基线上"选择频道/结束游戏"的墨迹中心在按钮内 (39, 11) 上下
            Assert.Equal((xs[i] + 39, CharacterSelectArt.SmallButtonY + 11), (x, y));
        }
    }

    [Fact]
    public void TheButtonWordsAreTheSharedPaintLinesInTheEnabledOrDisabledColour()
    {
        List<SceneSprite> texts = Tagged(Build(CharSelectKit.Online(3)), "select-char-button-text");
        ShellPaint expected = PaintOf(new SizedFonts());
        const int width = CharacterSelectArt.SmallButtonWidth - 8;
        string[] words = { "创立角色", "删除角色", "选择频道", "结束游戏" };
        uint[] colours = { CharacterSelectArt.DisabledButtonTextColor, CharacterSelectArt.DisabledButtonTextColor, CharacterSelectArt.ButtonTextColor, CharacterSelectArt.ButtonTextColor };
        for (int i = 0; i < 4; i++)
        {
            SpriteFrame line = expected.Line(words[i], CharacterSelectArt.OnlineCaptionPx, colours[i], OverlayArt.Black, width);
            Assert.True(ShellFrames.Same(line, texts[i].Frame), words[i]);
            Assert.True(ShellFrames.HasColor(texts[i].Frame, colours[i]));
        }
        Assert.Equal(0xDDC593u, CharacterSelectArt.ButtonTextColor);
        Assert.Equal(0x8A8F99u, CharacterSelectArt.DisabledButtonTextColor);
    }

    [Fact]
    public void ThePlaceholderButtonWordsAreEnglishWithoutFonts()
    {
        CharacterSelectScreen screen = CharacterSelectScreen.Online(RosterKit.Roster(2), CharSelectKit.Scene(), 0, null);
        List<SceneSprite> texts = Tagged(Build(screen), "select-char-button-text");
        ShellPaint expected = PaintOf(null);
        string[] words = { "CREATE", "DELETE", "CHANNEL", "EXIT" };
        for (int i = 0; i < 4; i++)
        {
            SpriteFrame line = expected.Line(words[i], 12, i < 2 ? CharacterSelectArt.DisabledButtonTextColor : CharacterSelectArt.ButtonTextColor, OverlayArt.Black, 69);
            Assert.True(ShellFrames.Same(line, texts[i].Frame), words[i]);
        }
    }

    [Fact]
    public void TheGreyButtonIsTheButtonDesaturatedToFiftyFivePercent()
    {
        List<SceneSprite> sprites = Build(CharSelectKit.Online(2));
        SpriteFrame grey = Tagged(sprites, "select-char-button-disabled")[0].Frame;
        SpriteFrame live = Tagged(sprites, "select-char-button")[0].Frame;
        Assert.Equal((grey.Width, grey.Height), (live.Width, live.Height));
        Assert.Equal((77, 23), (grey.Width, grey.Height));

        for (int i = 0; i < live.Rgba.Length; i += 4)
        {
            int luma = (live.Rgba[i] * 30 + live.Rgba[i + 1] * 59 + live.Rgba[i + 2] * 11) / 100;
            byte expected = (byte)(luma * 55 / 100);
            Assert.Equal((expected, expected, expected, live.Rgba[i + 3]), (grey.Rgba[i], grey.Rgba[i + 1], grey.Rgba[i + 2], grey.Rgba[i + 3]));
        }
        Assert.Equal((3, 3, 3, 255), ShellFrames.Pixel(grey, 2, 3));      // 帽上 (x 2, y 3) 是 (R 3, G 2, B 40):亮度 6 → 6 × 55 / 100 = 3
    }

    [Fact]
    public void GreyingNothingGivesNothingAndNeverChangesTheSource()
    {
        Assert.Null(CharacterSelectArt.Greyed(null));
        SpriteFrame source = CharSelectKit.Coded(4, 4, 200, 1, 2);
        byte[] before = (byte[])source.Rgba.Clone();
        SpriteFrame grey = CharacterSelectArt.Greyed(source);
        Assert.NotSame(source, grey);
        Assert.Equal(before, source.Rgba);
        Assert.Equal((source.Width, source.Height, source.OffsetX, source.OffsetY, source.CanvasWidth, source.CanvasHeight),
            (grey.Width, grey.Height, grey.OffsetX, grey.OffsetY, grey.CanvasWidth, grey.CanvasHeight));
    }

    [Fact]
    public void TheStartButtonIsGreyWhileWaitingOrWithNoCharacterAndNormalOtherwise()
    {
        CharacterSelectScreen screen = CharSelectKit.Online(3);
        List<SceneSprite> normal = Build(screen);
        Assert.Single(Tagged(normal, "select-char-start"));
        Assert.Empty(Tagged(normal, "select-char-start-disabled"));
        Assert.Equal(5, ShellFrames.Pixel(Tagged(normal, "select-char-start")[0].Frame, 1, 1).B);

        screen.Confirm();
        List<SceneSprite> busy = Build(screen);
        Assert.Empty(Tagged(busy, "select-char-start"));
        SceneSprite disabled = Assert.Single(Tagged(busy, "select-char-start-disabled"));
        Assert.Equal(10, ShellFrames.Pixel(disabled.Frame, 1, 1).B);
        Assert.Equal((CharacterSelectArt.StartX, CharacterSelectArt.StartY), (disabled.ScreenX, disabled.ScreenY));

        List<SceneSprite> empty = Build(CharSelectKit.Online(0));
        Assert.Single(Tagged(empty, "select-char-start-disabled"));
        Assert.Empty(Tagged(empty, "select-char-start"));
    }

    [Fact]
    public void WithoutArtFramesTheButtonsFallBackToPlainColourBlocks()
    {
        var scene = new CharacterSelectScene(new ShellFakeSprites());
        CharacterSelectScreen screen = CharacterSelectScreen.Online(RosterKit.Roster(2), scene, 0, new SizedFonts());
        List<SceneSprite> sprites = Build(screen);

        SpriteFrame grey = Tagged(sprites, "select-char-button-disabled")[0].Frame;
        SpriteFrame live = Tagged(sprites, "select-char-button")[0].Frame;
        Assert.Equal(77 * 23, ShellFrames.Solid(grey, CharacterSelectArt.FallbackDisabledButtonColor));
        Assert.Equal(77 * 23, ShellFrames.Solid(live, CharacterSelectArt.FallbackButtonColor));
        Assert.Single(Tagged(sprites, "select-char-backdrop"));
        Assert.Equal(4, Tagged(sprites, "select-char-button-text").Count);
    }

    // ------------------------------------------------------------------ 提示条

    [Fact]
    public void ThePromptBarSitsCentredRightAboveTheStartButtonAndSaysWhyInChinese()
    {
        var fonts = new RecordingFonts();
        CharacterSelectScreen screen = CharSelectKit.OnlineOf(RosterWith(RosterKit.Entry("甲", x1: 1)), fonts);
        Assert.Empty(Tagged(Build(screen), "select-char-prompt"));            // 没有提示时没有条

        screen.Confirm();
        List<SceneSprite> sprites = Build(screen);
        SceneSprite bar = Assert.Single(Tagged(sprites, "select-char-prompt-bar"));
        Assert.Single(Tagged(sprites, "select-char-prompt"));
        Assert.Single(Tagged(sprites, "select-char-prompt-icon"));
        Assert.True(fonts.Asked("该角色暂时不能进入游戏"));
        Assert.Equal(494, CharacterSelectArt.PromptY);
        Assert.Equal(CharacterSelectArt.PromptY, bar.ScreenY);
        Assert.Equal(32, bar.Frame.Height);
        Assert.Equal(CharacterSelectArt.StartY, bar.ScreenY + bar.Frame.Height);   // 条的底边落在"开始"按钮的顶边上
        Assert.InRange(bar.ScreenX + bar.Frame.Width / 2, 399, 401);              // 居中于画布
        Assert.Equal(OnlineShellNoticeOverlay.NoticeY, bar.ScreenY);              // 与外壳自己的提示条同一个位置

        screen.Advance(CharacterSelectScreen.PromptMs);
        Assert.Empty(Tagged(Build(screen), "select-char-prompt-bar"));
    }

    [Theory]
    [InlineData(1, ShellTone.Error)]                                                  // X1 = 1:不能进入,出错语气
    [InlineData(2, ShellTone.Error)]                                                  // 待删除
    [InlineData(3, ShellTone.Info)]                                                   // 点灰着的创立:暂未开放,一般说明
    public void ThePromptBarHasTheToneOfItsKind(int kind, ShellTone tone)
    {
        var fonts = new SizedFonts();
        OnlineCharacterEntry entry = RosterKit.Entry("甲", x1: (byte)(kind == 1 ? 1 : 0), d2: kind == 2 ? 5u : 0u);
        CharacterSelectScreen screen = CharSelectKit.OnlineOf(RosterWith(entry), fonts);
        if (kind == 3)
        {
            CharSelectKit.ClickButton(screen, CharacterSelectButton.Create);
        }
        else
        {
            screen.Confirm();
        }
        List<SceneSprite> sprites = Build(screen);

        var paint = new ShellPaint(new OverlayArt { Localize = false });
        SceneSprite icon = Assert.Single(Tagged(sprites, "select-char-prompt-icon"));
        Assert.True(ShellFrames.Same(paint.Icon(tone, 20), icon.Frame));
        foreach (ShellTone other in new[] { ShellTone.Error, ShellTone.Info, ShellTone.Success, ShellTone.Wait })
        {
            if (other != tone)
            {
                Assert.False(ShellFrames.Same(paint.Icon(other, 20), icon.Frame), "图标不是 " + other + " 语气的");
            }
        }
        Assert.Empty(Tagged(sprites, "select-char-prompt-spinner"));                  // 不是等待语气:没有转圈
    }

    [Theory]
    [InlineData(CharacterSelectPrompt.CannotEnter, "该角色暂时不能进入游戏", "THIS CHARACTER CANNOT ENTER")]
    [InlineData(CharacterSelectPrompt.PendingDelete, "该角色正在删除中", "THIS CHARACTER IS BEING DELETED")]
    [InlineData(CharacterSelectPrompt.NotAvailable, "暂未开放", "NOT AVAILABLE YET")]
    [InlineData(CharacterSelectPrompt.None, "", "")]
    public void ThePromptWordsAreChineseWithFontsAndEnglishWithout(CharacterSelectPrompt prompt, string chinese, string english)
    {
        Assert.Equal(chinese, CharacterSelectText.Prompt(prompt, true));
        Assert.Equal(english, CharacterSelectText.Prompt(prompt, false));
    }

    [Theory]
    [InlineData(CharacterBlock.None, CharacterSelectPrompt.None)]
    [InlineData(CharacterBlock.PendingDelete, CharacterSelectPrompt.PendingDelete)]
    [InlineData(CharacterBlock.NameFlag, CharacterSelectPrompt.CannotEnter)]
    [InlineData(CharacterBlock.Status, CharacterSelectPrompt.CannotEnter)]
    public void TheBlockDecidesThePrompt(CharacterBlock block, CharacterSelectPrompt prompt)
    {
        Assert.Equal(prompt, CharacterSelectText.PromptFor(block));
    }

    [Fact]
    public void WhileWaitingTheBarSaysEnteringWithASpinner()
    {
        var fonts = new RecordingFonts();
        CharacterSelectScreen screen = CharSelectKit.Online(2, 0, fonts);
        screen.Confirm();
        List<SceneSprite> sprites = Build(screen);
        Assert.Single(Tagged(sprites, "select-char-busy"));
        Assert.Single(Tagged(sprites, "select-char-busy-bar"));
        Assert.Single(Tagged(sprites, "select-char-busy-spinner"));
        Assert.Empty(Tagged(sprites, "select-char-prompt"));
        Assert.True(fonts.Asked("正在进入…"));
        Assert.Equal(CharacterSelectArt.PromptY, Tagged(sprites, "select-char-busy-bar")[0].ScreenY);
    }

    [Fact]
    public void WaitingBeatsAPromptThatIsStillShowing()
    {
        CharacterSelectScreen screen = CharSelectKit.Online(2);
        CharSelectKit.ClickButton(screen, CharacterSelectButton.Create);
        screen.Confirm();
        List<SceneSprite> sprites = Build(screen);
        Assert.Single(Tagged(sprites, "select-char-busy"));
        Assert.Empty(Tagged(sprites, "select-char-prompt"));
    }

    [Fact]
    public void ThePlaceholderBarIsEnglishWithoutFonts()
    {
        CharacterSelectScreen screen = CharacterSelectScreen.Online(RosterKit.Roster(2), CharSelectKit.Scene(), 0, null);
        screen.Confirm();
        Assert.Equal("ENTERING...", CharacterSelectText.Busy(false));
        Assert.Equal("正在进入…", CharacterSelectText.Busy(true));
        Assert.Single(Tagged(Build(screen), "select-char-busy"));
    }

    [Fact]
    public void TheShellNoticeWinsOverTheScreenPromptAtTheSameSpot()
    {
        var fonts = new SizedFonts();
        CharacterSelectScreen screen = CharSelectKit.Online(2, 0, fonts);
        CharSelectKit.ClickButton(screen, CharacterSelectButton.Create);
        var overlay = new OnlineShellNoticeOverlay(screen, fonts);

        List<SceneSprite> alone = overlay.Build(0);
        Assert.Single(Tagged(alone, "select-char-prompt"));                      // 外壳没有提示:屏幕自己的提示留着
        Assert.Empty(Tagged(alone, "online-select-notice"));

        overlay.Notice = new OnlineNotice(OnlineNoticeKind.NoCharacters);
        List<SceneSprite> both = overlay.Build(0);
        Assert.DoesNotContain(both, s => CharacterSelectArt.IsOwnBar(s.AnimationPath));      // 不叠两条
        Assert.Single(Tagged(both, "online-select-notice"));
        Assert.NotEmpty(Tagged(both, "select-char-caption"));                    // 别的东西都还在
    }

    [Fact]
    public void OnlyTheScreensOwnBarTagsAreRecognised()
    {
        foreach (string tag in new[] { "select-char-prompt", "select-char-prompt-bar", "select-char-prompt-accent", "select-char-prompt-icon",
                     "select-char-busy", "select-char-busy-bar", "select-char-busy-accent", "select-char-busy-spinner" })
        {
            Assert.True(CharacterSelectArt.IsOwnBar(tag), tag);
        }
        foreach (string tag in new[] { null, "", "online-select-notice", "select-char-caption", "select-char-marker", "select-char-button", "select-char-start-disabled", "prompt" })
        {
            Assert.False(CharacterSelectArt.IsOwnBar(tag), tag ?? "null");
        }
    }

    // ------------------------------------------------------------------ 窗口、头像、滑块

    [Fact]
    public void TheWindowShowsTheRowsFromTheFirstRowAndAvatarsFollowTheirCells()
    {
        var avatars = new CharSelectKit.FakeAvatars();
        CharacterSelectScreen screen = CharSelectKit.Online(13, 0, null, avatars);
        screen.Scroll(1);                                                        // 窗口第 1、2 行:名册第 6 到 12 个
        avatars.Calls.Clear();
        List<SceneSprite> sprites = Build(screen);

        Assert.Equal(7, avatars.Calls.Count);
        for (int cell = 0; cell < 7; cell++)
        {
            CharacterSelectArt.FootPoint(cell, out int fx, out int fy);
            Assert.Equal(("角色" + (6 + cell + 1), fx, fy), avatars.Calls[cell]);
        }
        Assert.Equal(7, Tagged(sprites, "select-char-avatar").Count);
        Assert.Equal(14, Tagged(sprites, "select-char-caption").Count);
    }

    [Fact]
    public void TheBrightPedestalAndTheSlotFrameAreOnTheSelectedCharactersCellInTheWindow()
    {
        CharacterSelectScreen screen = CharSelectKit.Online(13, 9);              // 第 1 行第 3 列;窗口 0、1
        List<SceneSprite> sprites = Build(screen);
        CharacterSelectArt.SlotOrigin(9, out int sx, out int sy);
        SceneSprite bright = Assert.Single(Tagged(sprites, "select-char-pedestal-bright"));
        SceneSprite frame = Assert.Single(Tagged(sprites, "select-char-slot-frame"));
        Assert.Equal((sx + CharacterSelectArt.PedestalDX, sy + CharacterSelectArt.PedestalDY), (bright.ScreenX, bright.ScreenY));
        Assert.Equal((sx, sy), (frame.ScreenX, frame.ScreenY));
        Assert.Equal(11, Tagged(sprites, "select-char-pedestal").Count);

        screen.Scroll(1);                                                        // 窗口 1、2:选中项(第 9 个)还在窗口里,在窗口第一行第 3 格
        sprites = Build(screen);
        CharacterSelectArt.SlotOrigin(3, out sx, out sy);
        SceneSprite moved = Assert.Single(Tagged(sprites, "select-char-slot-frame"));
        Assert.Equal((sx, sy), (moved.ScreenX, moved.ScreenY));
    }

    [Fact]
    public void AnEmptyRosterDrawsOnlyDimPedestals()
    {
        // 空名册没有选中项:不画发光框与亮底座,十二个空槽都画暗底座
        List<SceneSprite> sprites = Build(CharSelectKit.Online(0));
        Assert.Empty(Tagged(sprites, "select-char-slot-frame"));
        Assert.Empty(Tagged(sprites, "select-char-pedestal-bright"));
        Assert.Equal(12, Tagged(sprites, "select-char-pedestal").Count);
        Assert.Empty(Tagged(sprites, "select-char-caption"));
    }

    [Fact]
    public void TheThumbIsDrawnWithTheHeightAndPositionForTheRowsAndTheWindow()
    {
        CharacterSelectScreen screen = CharSelectKit.Online(25);                 // 五行
        SceneSprite thumb = Assert.Single(Tagged(Build(screen), "select-char-scroll-thumb"));
        Assert.Equal((CharacterSelectArt.ScrollX, CharacterSelectArt.ThumbTop(0, 5), 143), (thumb.ScreenX, thumb.ScreenY, thumb.Frame.Height));

        screen.Scroll(3);
        thumb = Assert.Single(Tagged(Build(screen), "select-char-scroll-thumb"));
        Assert.Equal((CharacterSelectArt.ThumbTop(3, 5), 143), (thumb.ScreenY, thumb.Frame.Height));
        Assert.Equal(CharacterSelectArt.ScrollBottom, thumb.ScreenY + thumb.Frame.Height);
    }

    [Fact]
    public void WithUpToTwelveCharactersTheOnlineThumbIsTheOfflineThumb()
    {
        SceneSprite online = Assert.Single(Tagged(Build(CharSelectKit.Online(12)), "select-char-scroll-thumb"));
        var offline = new CharacterSelectScreen(RosterKit.Roster(3), CharSelectKit.Scene());
        SceneSprite old = Assert.Single(Tagged(offline.Build(0), "select-char-scroll-thumb"));
        Assert.Equal((old.ScreenX, old.ScreenY, old.Frame.Height), (online.ScreenX, online.ScreenY, online.Frame.Height));
        Assert.True(ShellFrames.Same(old.Frame, online.Frame));
        Assert.Equal((763, 98, 351), (online.ScreenX, online.ScreenY, online.Frame.Height));
    }

    [Fact]
    public void TheOfflineDrawingIsStillOfflineText()
    {
        // 离线名册走原来的画法:没有联网的灰按钮、标记、提示条,第一行字是 LV 加名字的大写
        var screen = new CharacterSelectScreen(CharacterRoster.Local(), CharSelectKit.Scene());
        List<SceneSprite> sprites = screen.Build(0);
        Assert.Equal(2, Tagged(sprites, "select-char-caption").Count);
        Assert.Equal(4, Tagged(sprites, "select-char-button").Count);
        Assert.Single(Tagged(sprites, "select-char-start"));
        Assert.Empty(Tagged(sprites, "select-char-button-disabled"));
        Assert.Empty(Tagged(sprites, "select-char-start-disabled"));
        Assert.Empty(Tagged(sprites, "select-char-marker"));
        Assert.DoesNotContain(sprites, s => CharacterSelectArt.IsOwnBar(s.AnimationPath));
        SpriteFrame expected = new OverlayArt().Text(CharacterRoster.Local()[0].Caption, 1, CharacterSelectArt.CaptionColor);
        Assert.True(ShellFrames.Same(expected, Tagged(sprites, "select-char-caption")[0].Frame));
    }

    // ------------------------------------------------------------------ 没有字体

    [Fact]
    public void WithoutFontsTheCaptionsAreUppercaseEnglishPlaceholders()
    {
        CharacterRoster roster = OnlineCharacterRoster.From(RosterKit.List(RosterKit.Entry("abc", level: 12, job: 1), RosterKit.Entry("角色", x1: 1)));
        CharacterSelectScreen screen = CharacterSelectScreen.Online(roster, CharSelectKit.Scene(), 0, null);
        List<SceneSprite> sprites = Build(screen);
        List<SceneSprite> captions = Tagged(sprites, "select-char-caption");
        ShellPaint expected = PaintOf(null);

        Assert.True(ShellFrames.Same(expected.Line("LV12 ABC", 12, CharacterSelectArt.CaptionColor, OverlayArt.Black, 115), captions[0].Frame));
        Assert.True(ShellFrames.Same(expected.Line("FIGHTER", 12, CharacterSelectArt.CaptionColor, OverlayArt.Black, 115), captions[1].Frame));
        Assert.True(ShellFrames.Same(expected.Line("LV1 角色", 12, CharacterSelectArt.CaptionColor, OverlayArt.Black, 115), captions[2].Frame));
        SceneSprite marker = Assert.Single(Tagged(sprites, "select-char-marker"));
        Assert.True(ShellFrames.Same(expected.Line("LOCKED", 12, ShellPaint.Red, OverlayArt.Black, 115), marker.Frame));
    }

    [Fact]
    public void FontsDecideBetweenChineseAndEnglishThroughTheView()
    {
        Assert.True(new CharacterSelectView(new SizedFonts()).Cjk);
        Assert.False(new CharacterSelectView(null).Cjk);                          // 测试进程里没有全进程字体
    }

    [Fact]
    public void ACaptionIsTheLevelWordThenTheNameForChineseAndTheOfflineCaptionOtherwise()
    {
        var slot = new CharacterSlot("Hero", "Slayer", 70);
        Assert.Equal("Lv70 Hero", CharacterSelectText.Caption(slot, true));
        Assert.Equal("LV70 HERO", CharacterSelectText.Caption(slot, false));
        Assert.Equal("Lv1", CharacterSelectText.Caption(new CharacterSlot("", "Slayer", 0), true));
        Assert.Equal("LV1", CharacterSelectText.Caption(new CharacterSlot("", "Slayer", 0), false));       // 空名字不留尾部空格
    }

    [Theory]
    [InlineData(CharacterSelectButton.Create, "创立角色", "CREATE")]
    [InlineData(CharacterSelectButton.Delete, "删除角色", "DELETE")]
    [InlineData(CharacterSelectButton.Channel, "选择频道", "CHANNEL")]
    [InlineData(CharacterSelectButton.Quit, "结束游戏", "EXIT")]
    [InlineData(CharacterSelectButton.Start, "", "")]
    [InlineData(CharacterSelectButton.None, "", "")]
    public void TheButtonWordsAreChineseWithFontsAndEnglishWithout(CharacterSelectButton button, string chinese, string english)
    {
        Assert.Equal(chinese, CharacterSelectText.Button(button, true));
        Assert.Equal(english, CharacterSelectText.Button(button, false));
    }
}
