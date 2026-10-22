using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 选角界面(证据 E-096):名册、界面逻辑(移动/确认/返回/点击)、原版画法的层次与位置、合成的大图(边框、滚动条、小按钮)、
/// 界面动画(<see cref="UiAnimation"/>)、头像来源、流程里的选角阶段与自动机。精灵是合成的"行号编码"色块
/// (像素 (x, y) = (R=y%256, G=x%256, B=tag, A=255),只有尺寸取自量过的界面图),数据是编的;验证的是布局规则、层次顺序、时间线与流程。
/// </summary>
public class CharacterSelectTests
{
    private sealed class FakeSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) => Frames.TryGetValue((imageName, frame), out sprite);
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

    private static (int R, int G, int B, int A) Px(SpriteFrame f, int x, int y)
    {
        int o = (y * f.Width + x) * 4;
        return (f.Rgba[o], f.Rgba[o + 1], f.Rgba[o + 2], f.Rgba[o + 3]);
    }

    private const string UiImage = CharacterSelectArt.UiImage;
    private const string WinImage = CharacterSelectArt.WindowImage;

    /// <summary>界面用到的全部帧(尺寸同量过的界面图,标题、旗的偏移是编的,用来验证"原点 + 帧偏移")。</summary>
    private static FakeSprites FullArt()
    {
        var f = new FakeSprites();
        f.Frames[(UiImage, CharacterSelectArt.BackgroundFrame)] = Coded(800, 600, 1);
        f.Frames[(UiImage, CharacterSelectArt.TitleFrame)] = Coded(111, 28, 2, 3, 4);
        f.Frames[(UiImage, CharacterSelectArt.GlowFrame)] = Coded(292, 38, 3);
        f.Frames[(UiImage, CharacterSelectArt.SlotFrameFrame)] = Coded(119, 207, 4);
        f.Frames[(UiImage, CharacterSelectArt.StartNormalFrame)] = Coded(125, 43, 5);
        f.Frames[(UiImage, CharacterSelectArt.PedestalBrightFrame)] = Coded(119, 44, 6);
        f.Frames[(UiImage, CharacterSelectArt.PedestalDimFrame)] = Coded(119, 44, 7);
        f.Frames[(CharacterSelectArt.FlagImage, CharacterSelectArt.FlagFrame)] = Coded(36, 53, 8, -1, 2);
        // 窗口边框九宫格:四角 11×11,上下边 12×11,左右边 11×13,中心 12×13
        (int W, int H)[] sizes = { (11, 11), (12, 11), (11, 11), (11, 13), (12, 13), (11, 13), (11, 11), (12, 11), (11, 11) };
        for (int i = 0; i < 9; i++)
        {
            f.Frames[(WinImage, CharacterSelectArt.BoardFirstFrame + i)] = Coded(sizes[i].W, sizes[i].H, (byte)(20 + i));
        }
        f.Frames[(WinImage, CharacterSelectArt.ScrollUpFrame)] = Coded(9, 13, 30);
        f.Frames[(WinImage, CharacterSelectArt.ScrollDownFrame)] = Coded(9, 13, 31);
        f.Frames[(WinImage, CharacterSelectArt.ScrollThumbFrame)] = Coded(9, 23, 32);
        f.Frames[(WinImage, CharacterSelectArt.ScrollTrackFrame)] = Coded(9, 1, 33);
        f.Frames[(WinImage, CharacterSelectArt.ButtonLeftFrame)] = Coded(28, 23, 40);
        f.Frames[(WinImage, CharacterSelectArt.ButtonMiddleFrame)] = Coded(2, 23, 41);
        f.Frames[(WinImage, CharacterSelectArt.ButtonRightFrame)] = Coded(28, 23, 42);
        return f;
    }

    private static CharacterRoster Roster(int count)
    {
        var slots = new List<CharacterSlot>();
        for (int i = 0; i < count; i++)
        {
            slots.Add(new CharacterSlot("Hero" + i, "Slayer", i + 1));
        }
        return new CharacterRoster(slots);
    }

    private static string[] Tags(List<SceneSprite> sprites) => sprites.Select(s => s.AnimationPath).ToArray();

    private static SceneSprite Only(List<SceneSprite> sprites, string tag, int nth = 0) => sprites.Where(s => s.AnimationPath == tag).ElementAt(nth);

    // ------------------------------------------------------------------------------------------------ 名册

    [Fact]
    public void ASlotClampsTheLevelAndWritesTheCaption()
    {
        Assert.Equal("LV1 HERO", new CharacterSlot("Hero", "Slayer", 0).Caption);
        Assert.Equal("LV70 ABC", new CharacterSlot("abc", "Slayer", 70).Caption);
        Assert.Equal(1, new CharacterSlot("x", "y", -5).Level);
        Assert.Throws<ArgumentNullException>(() => new CharacterSlot(null, "y", 1));
        Assert.Throws<ArgumentNullException>(() => new CharacterSlot("x", null, 1));
    }

    [Fact]
    public void ARosterHoldsAtMostTwelveSlotsAndNoNulls()
    {
        Assert.Equal(12, CharacterRoster.Capacity);
        Assert.Equal(12, Roster(12).Count);
        Assert.Throws<ArgumentException>(() => Roster(13));
        Assert.Throws<ArgumentException>(() => new CharacterRoster(new CharacterSlot[] { null }));
        Assert.Throws<ArgumentNullException>(() => new CharacterRoster(null));
        Assert.Equal(0, new CharacterRoster(new CharacterSlot[0]).Count);
    }

    [Fact]
    public void TheLocalRosterIsOneLevelOneSlayer()
    {
        CharacterRoster local = CharacterRoster.Local();
        Assert.Single(local.Slots);
        Assert.Equal((CharacterRoster.LocalName, CharacterRoster.LocalJob, 1), (local[0].Name, local[0].JobName, local[0].Level));
    }

    [Fact]
    public void SlotsFillTheGridLeftToRightThenDown()
    {
        CharacterRoster.Cell(0, out int c0, out int r0);
        CharacterRoster.Cell(5, out int c5, out int r5);
        CharacterRoster.Cell(6, out int c6, out int r6);
        CharacterRoster.Cell(11, out int c11, out int r11);
        Assert.Equal(new[] { (0, 0), (5, 0), (0, 1), (5, 1) }, new[] { (c0, r0), (c5, r5), (c6, r6), (c11, r11) });
    }

    // ------------------------------------------------------------------------------------------------ 界面逻辑(占位画面与原版画面一样)

    [Fact]
    public void TheScreenStartsOnTheFirstCharacterOrNothingForAnEmptyRoster()
    {
        var screen = new CharacterSelectScreen(Roster(3));
        Assert.Equal(0, screen.Index);
        Assert.Equal("Hero0", screen.Selected.Name);
        Assert.Equal(CharacterSelectOutcome.None, screen.Outcome);

        var empty = new CharacterSelectScreen(new CharacterRoster(new CharacterSlot[0]));
        Assert.Equal(-1, empty.Index);
        Assert.Null(empty.Selected);
        empty.Confirm();
        Assert.Equal(CharacterSelectOutcome.None, empty.Outcome);             // 没有角色可进
        empty.Move(1);
        Assert.Equal(-1, empty.Index);

        Assert.Equal(2, new CharacterSelectScreen(Roster(3), null, 2).Index);
        Assert.Equal(2, new CharacterSelectScreen(Roster(3), null, 9).Index);  // 夹在名册里
        Assert.Equal(0, new CharacterSelectScreen(Roster(3), null, -4).Index);
        Assert.Throws<ArgumentNullException>(() => new CharacterSelectScreen(null));
    }

    [Fact]
    public void MovingStaysInsideTheGridAndNeverWraps()
    {
        var screen = new CharacterSelectScreen(Roster(8));        // 第一行 6 个,第二行 2 个
        screen.Move(-1);
        Assert.Equal(0, screen.Index);                           // 到头不循环
        screen.Move(1);
        screen.Move(1);
        Assert.Equal(2, screen.Index);
        screen.Move(0, 1);
        Assert.Equal(2, screen.Index);                           // 同列下一行(第 8 格)没有角色:不动
        screen.Move(-1);
        screen.Move(0, 1);
        Assert.Equal(7, screen.Index);                           // 第 1 列的下一行有角色
        screen.Move(1);
        Assert.Equal(7, screen.Index);                           // 往右没有角色了:停在最后一个
        screen.Move(0, -1);
        Assert.Equal(1, screen.Index);
        screen.Move(0, -1);
        Assert.Equal(1, screen.Index);                           // 已经在最上面一行
        for (int i = 0; i < 20; i++)
        {
            screen.Move(1);
        }
        Assert.Equal(5, screen.Index);                           // 一行最右边
    }

    [Fact]
    public void ConfirmingStartsTheGameOnceAndFreezesTheScreen()
    {
        var screen = new CharacterSelectScreen(Roster(3));
        screen.Move(1);
        screen.Confirm();
        Assert.Equal(CharacterSelectOutcome.Start, screen.Outcome);
        Assert.Equal("Hero1", screen.Selected.Name);
        screen.Move(1);
        screen.Back();
        screen.Confirm();
        Assert.Equal(CharacterSelectOutcome.Start, screen.Outcome);   // 有结果之后什么都不再响应
        Assert.Equal(1, screen.Index);
    }

    [Fact]
    public void GoingBackQuitsTheGame()
    {
        var screen = new CharacterSelectScreen(Roster(2));
        screen.Back();
        Assert.Equal(CharacterSelectOutcome.Quit, screen.Outcome);
        screen.Confirm();
        Assert.Equal(CharacterSelectOutcome.Quit, screen.Outcome);
    }

    [Fact]
    public void TheClockOnlyMovesForward()
    {
        var screen = new CharacterSelectScreen(Roster(1));
        Assert.Equal(0, screen.AgeMs);
        screen.Advance(250.5);
        screen.Advance(-100);
        screen.Advance(0);
        screen.Advance(749.5);
        Assert.Equal(1000, screen.AgeMs);
    }

    [Fact]
    public void ThePlaceholderScreenListsTheCharactersAndMarksTheSelection()
    {
        var screen = new CharacterSelectScreen(Roster(3), null, 1);
        List<SceneSprite> sprites = screen.Build(0);
        Assert.Equal(7, sprites.Count);                                 // 底板、标题、高亮、3 行名字、提示
        Assert.Equal(1, sprites.Count(s => s.AnimationPath == "select-char-highlight"));
        Assert.Equal(3, sprites.Count(s => s.AnimationPath == "select-char-name"));
        Assert.Equal(800, Only(sprites, "select-char-backdrop").Frame.Width);
        Assert.Equal(600, Only(sprites, "select-char-backdrop").Frame.Height);
        Assert.Equal(Only(sprites, "select-char-name", 1).ScreenY - 6, Only(sprites, "select-char-highlight").ScreenY);   // 高亮垫在选中的第 2 行名字上
        Assert.All(sprites, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        Assert.Equal(sprites.Select(s => s.SortKey).OrderBy(k => k).ToArray(), sprites.Select(s => s.SortKey).ToArray());   // 列表顺序 = 画的先后

        var none = new CharacterSelectScreen(new CharacterRoster(new CharacterSlot[0]));
        Assert.Contains("select-char-empty", Tags(none.Build(0)));
        Assert.False(screen.Click(10, 10));                             // 占位画面不响应点击
        Assert.Equal(1, screen.Index);
    }

    // ------------------------------------------------------------------------------------------------ 原版画法

    [Fact]
    public void TheArtLayersAreDrawnInOrderWithTheSelectedSlotHighlighted()
    {
        var scene = new CharacterSelectScene(FullArt());
        var screen = new CharacterSelectScreen(Roster(2), scene);
        List<SceneSprite> sprites = screen.Build(0);

        var expected = new List<string>
        {
            "select-char-background", "select-char-board", "select-char-title", "select-char-flag", "select-char-glow",
            "select-char-scroll-up", "select-char-scroll-down", "select-char-scroll-track", "select-char-scroll-thumb",
            "select-char-pedestal-bright", "select-char-slot-frame",
        };
        for (int i = 1; i < CharacterRoster.Capacity; i++)
        {
            expected.Add("select-char-pedestal");
        }
        expected.AddRange(new[] { "select-char-caption", "select-char-caption", "select-char-caption", "select-char-caption", "select-char-start" });
        for (int i = 0; i < 4; i++)
        {
            expected.Add("select-char-button");
            expected.Add("select-char-button-text");
        }
        Assert.Equal(expected, Tags(sprites).ToList());
        Assert.All(sprites, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        Assert.Equal(Enumerable.Range(0, sprites.Count).ToArray(), sprites.Select(s => s.SortKey).ToArray());
    }

    [Fact]
    public void OnlyTheGlowThePedestalsAndTheSlotFrameBlendAdditively()
    {
        var screen = new CharacterSelectScreen(Roster(1), new CharacterSelectScene(FullArt()));
        List<SceneSprite> sprites = screen.Build(0);
        var additive = new HashSet<string>(sprites.Where(s => s.Blend == SpriteBlend.Additive).Select(s => s.AnimationPath));
        Assert.Equal(new HashSet<string> { "select-char-glow", "select-char-pedestal-bright", "select-char-pedestal", "select-char-slot-frame" }, additive);
    }

    [Fact]
    public void FramesAreAnchoredAtTheControlOriginPlusTheirOwnOffsets()
    {
        var screen = new CharacterSelectScreen(Roster(1), new CharacterSelectScene(FullArt()));
        List<SceneSprite> sprites = screen.Build(0);
        SceneSprite title = Only(sprites, "select-char-title");
        Assert.Equal((CharacterSelectArt.TitleX + 3, CharacterSelectArt.TitleY + 4), (title.ScreenX, title.ScreenY));
        SceneSprite flag = Only(sprites, "select-char-flag");
        Assert.Equal((CharacterSelectArt.FlagX - 1, CharacterSelectArt.FlagY + 2), (flag.ScreenX, flag.ScreenY));
        SceneSprite board = Only(sprites, "select-char-board");
        Assert.Equal((CharacterSelectArt.BoardX, CharacterSelectArt.BoardY), (board.ScreenX, board.ScreenY));
        SceneSprite start = Only(sprites, "select-char-start");
        Assert.Equal((CharacterSelectArt.StartX, CharacterSelectArt.StartY), (start.ScreenX, start.ScreenY));
        SceneSprite glow = Only(sprites, "select-char-glow");
        Assert.Equal((CharacterSelectArt.GlowX, CharacterSelectArt.GlowY), (glow.ScreenX, glow.ScreenY));
    }

    [Fact]
    public void SlotsSitOnAGridWithTheMeasuredPitch()
    {
        CharacterSelectArt.SlotOrigin(0, out int x0, out int y0);
        CharacterSelectArt.SlotOrigin(7, out int x7, out int y7);
        CharacterSelectArt.SlotOrigin(11, out int x11, out int y11);
        Assert.Equal((CharacterSelectArt.SlotX, CharacterSelectArt.SlotY), (x0, y0));
        Assert.Equal((CharacterSelectArt.SlotX + CharacterSelectArt.SlotPitchX, CharacterSelectArt.SlotY + CharacterSelectArt.SlotPitchY), (x7, y7));
        Assert.Equal((CharacterSelectArt.SlotX + 5 * CharacterSelectArt.SlotPitchX, CharacterSelectArt.SlotY + CharacterSelectArt.SlotPitchY), (x11, y11));
        CharacterSelectArt.FootPoint(7, out int fx, out int fy);
        Assert.Equal((x7 + CharacterSelectArt.FootDX, y7 + CharacterSelectArt.FootDY), (fx, fy));

        var screen = new CharacterSelectScreen(Roster(8), new CharacterSelectScene(FullArt()), 7);
        List<SceneSprite> sprites = screen.Build(0);
        // 底座按槽位顺序摆(选中的第 7 格是亮的):第 7 个底座精灵在第 7 格的位置上
        var pedestals = sprites.Where(s => s.AnimationPath.StartsWith("select-char-pedestal", StringComparison.Ordinal)).ToList();
        Assert.Equal(CharacterRoster.Capacity, pedestals.Count);
        Assert.Equal("select-char-pedestal-bright", pedestals[7].AnimationPath);
        Assert.Equal((x7 + CharacterSelectArt.PedestalDX, y7 + CharacterSelectArt.PedestalDY), (pedestals[7].ScreenX, pedestals[7].ScreenY));
        Assert.Equal((CharacterSelectArt.SlotX + CharacterSelectArt.PedestalDX, CharacterSelectArt.SlotY + CharacterSelectArt.PedestalDY),
            (pedestals[0].ScreenX, pedestals[0].ScreenY));
        SceneSprite frame = Only(sprites, "select-char-slot-frame");
        Assert.Equal((x7, y7), (frame.ScreenX, frame.ScreenY));
    }

    [Fact]
    public void AnEmptySelectionDrawsOnlyDimPedestals()
    {
        FakeSprites art = FullArt();
        var scene = new CharacterSelectScene(art);
        var screen = new CharacterSelectScreen(new CharacterRoster(new CharacterSlot[0]), scene);
        List<SceneSprite> sprites = screen.Build(0);
        Assert.Equal(CharacterRoster.Capacity, sprites.Count(s => s.AnimationPath == "select-char-pedestal"));
        Assert.DoesNotContain("select-char-slot-frame", Tags(sprites));
        Assert.DoesNotContain("select-char-caption", Tags(sprites));

        // 选中的序号超出名册(没有这个角色)也不亮
        var list = new List<SceneSprite>();
        int order = 0;
        CharacterSelectArt.For(art).Build(list, ref order, new OverlayArt(), scene, Roster(2), 5, 0);
        Assert.DoesNotContain("select-char-slot-frame", Tags(list));
        Assert.DoesNotContain("select-char-pedestal-bright", Tags(list));
    }

    [Fact]
    public void CaptionsAreCentredOnTheSlotInTwoLines()
    {
        var screen = new CharacterSelectScreen(Roster(2), new CharacterSelectScene(FullArt()));
        List<SceneSprite> sprites = screen.Build(0);
        CharacterSelectArt.SlotOrigin(1, out int sx, out int sy);
        SceneSprite name = Only(sprites, "select-char-caption", 2);     // 第 1 个角色的第一行
        SceneSprite job = Only(sprites, "select-char-caption", 3);
        int centre = sx + CharacterSelectArt.SlotWidth / 2;
        Assert.Equal(centre - name.Frame.Width / 2, name.ScreenX);
        Assert.Equal(centre - job.Frame.Width / 2, job.ScreenX);
        Assert.Equal(sy + CharacterSelectArt.CaptionDY, name.ScreenY);
        Assert.Equal(sy + CharacterSelectArt.CaptionDY + CharacterSelectArt.CaptionLineStep, job.ScreenY);
        Assert.True(name.Frame.Width > job.Frame.Width);                // "LV2 HERO1" 比 "SLAYER" 长
    }

    [Fact]
    public void MissingFramesAreSkippedAndTheBackdropFallsBackToASolid()
    {
        var empty = new FakeSprites();
        var screen = new CharacterSelectScreen(Roster(1), new CharacterSelectScene(empty));
        List<SceneSprite> sprites = screen.Build(0);
        Assert.Contains("select-char-backdrop", Tags(sprites));
        Assert.Equal(800, Only(sprites, "select-char-backdrop").Frame.Width);
        foreach (string absent in new[] { "select-char-background", "select-char-board", "select-char-title", "select-char-flag", "select-char-glow",
                     "select-char-scroll-up", "select-char-scroll-track", "select-char-scroll-thumb", "select-char-slot-frame", "select-char-start" })
        {
            Assert.DoesNotContain(absent, Tags(sprites));
        }
        // 没有图的小按钮用纯色块顶替,字照样写
        Assert.Equal(4, sprites.Count(s => s.AnimationPath == "select-char-button"));
        Assert.Equal(4, sprites.Count(s => s.AnimationPath == "select-char-button-text"));
        Assert.Equal(CharacterSelectArt.SmallButtonWidth, Only(sprites, "select-char-button").Frame.Width);
        Assert.DoesNotContain("select-char-pedestal", Tags(sprites));
        Assert.DoesNotContain("select-char-pedestal-bright", Tags(sprites));
    }

    [Fact]
    public void TheArtObjectIsSharedPerSpriteSource()
    {
        FakeSprites a = FullArt();
        FakeSprites b = FullArt();
        Assert.Same(CharacterSelectArt.For(a), CharacterSelectArt.For(a));
        Assert.NotSame(CharacterSelectArt.For(a), CharacterSelectArt.For(b));
        Assert.Null(CharacterSelectArt.For(null));
        Assert.Throws<ArgumentNullException>(() => new CharacterSelectScene(null));
    }

    // ------------------------------------------------------------------------------------------------ 合成的大图

    [Fact]
    public void TheBoardIsOneComposedImageOfCornersEdgesAndTheCentreTile()
    {
        var screen = new CharacterSelectScreen(Roster(1), new CharacterSelectScene(FullArt()));
        SpriteFrame board = Only(screen.Build(0), "select-char-board").Frame;
        Assert.Equal((11 + 62 * 12 + 11, 11 + 33 * 13 + 11), (board.Width, board.Height));
        Assert.Equal((0, 0, 20, 255), Px(board, 0, 0));                                       // 左上角(帧 0)
        Assert.Equal((2, 1, 22, 255), Px(board, board.Width - 11 + 1, 2));                     // 右上角(帧 2)
        Assert.Equal((2, 1, 26, 255), Px(board, 1, board.Height - 11 + 2));                    // 左下角(帧 6)
        Assert.Equal((2, 1, 28, 255), Px(board, board.Width - 11 + 1, board.Height - 11 + 2)); // 右下角(帧 8)
        Assert.Equal((2, 1, 21, 255), Px(board, 11 + 12 * 2 + 1, 2));                          // 上边第 3 块(帧 1)
        Assert.Equal((4, 3, 27, 255), Px(board, 11 + 12 * 40 + 3, board.Height - 11 + 4));      // 下边(帧 7)
        Assert.Equal((5, 2, 23, 255), Px(board, 2, 11 + 13 * 4 + 5));                          // 左边(帧 3)
        Assert.Equal((5, 2, 25, 255), Px(board, board.Width - 11 + 2, 11 + 13 * 4 + 5));       // 右边(帧 5)
        Assert.Equal((4, 3, 24, 255), Px(board, 11 + 12 * 5 + 3, 11 + 13 * 7 + 4));            // 中心块(帧 4)重复
        Assert.Equal((0, 0, 24, 255), Px(board, 11 + 12 * 61, 11 + 13 * 32));                  // 最后一块中心块的左上角
    }

    [Fact]
    public void TheBoardIsMissingIfAnyTileIsMissing()
    {
        FakeSprites art = FullArt();
        art.Frames.Remove((WinImage, CharacterSelectArt.BoardFirstFrame + 4));
        var screen = new CharacterSelectScreen(Roster(1), new CharacterSelectScene(art));
        Assert.DoesNotContain("select-char-board", Tags(screen.Build(0)));
    }

    [Fact]
    public void TheScrollTrackRepeatsOneRowAndTheThumbStretchesItsMiddleRow()
    {
        var screen = new CharacterSelectScreen(Roster(1), new CharacterSelectScene(FullArt()));
        List<SceneSprite> sprites = screen.Build(0);
        SceneSprite track = Only(sprites, "select-char-scroll-track");
        int trackHeight = CharacterSelectArt.ScrollBottom - CharacterSelectArt.ScrollTop - CharacterSelectArt.ScrollArrowHeight;
        Assert.Equal((9, trackHeight), (track.Frame.Width, track.Frame.Height));
        Assert.Equal((CharacterSelectArt.ScrollX, CharacterSelectArt.ScrollTop + CharacterSelectArt.ScrollArrowHeight), (track.ScreenX, track.ScreenY));
        Assert.Equal((0, 4, 33, 255), Px(track.Frame, 4, 0));
        Assert.Equal((0, 4, 33, 255), Px(track.Frame, 4, trackHeight - 1));                    // 每一行都是轨道那一行

        SceneSprite thumb = Only(sprites, "select-char-scroll-thumb");
        Assert.Equal((9, CharacterSelectArt.ScrollThumbHeight), (thumb.Frame.Width, thumb.Frame.Height));
        int h = CharacterSelectArt.ScrollThumbHeight;
        Assert.Equal(0, Px(thumb.Frame, 0, 0).R);                                              // 上沿原样
        Assert.Equal(10, Px(thumb.Frame, 0, 10).R);
        Assert.Equal(11, Px(thumb.Frame, 0, 11).R);                                            // 中间重复第 11 行
        Assert.Equal(11, Px(thumb.Frame, 0, h / 2).R);
        Assert.Equal(11, Px(thumb.Frame, 0, h - 12).R);                                        // 下沿 12 行原样(帧的第 11 到 22 行)从这里开始
        Assert.Equal(12, Px(thumb.Frame, 0, h - 11).R);
        Assert.Equal(22, Px(thumb.Frame, 0, h - 1).R);
    }

    [Fact]
    public void TheSmallButtonIsALeftCapTiledMiddleAndARightCap()
    {
        var screen = new CharacterSelectScreen(Roster(1), new CharacterSelectScene(FullArt()));
        SpriteFrame button = Only(screen.Build(0), "select-char-button").Frame;
        Assert.Equal((CharacterSelectArt.SmallButtonWidth, 23), (button.Width, button.Height));
        Assert.Equal((5, 3, 40, 255), Px(button, 3, 5));                                       // 左帽
        Assert.Equal((5, 27, 40, 255), Px(button, 27, 5));
        Assert.Equal(41, Px(button, 28, 5).B);                                                 // 中间(2 像素宽的块铺到右帽之前)
        Assert.Equal(41, Px(button, 48, 5).B);
        Assert.Equal((5, 0, 42, 255), Px(button, 49, 5));                                      // 右帽从 77 − 28 = 49 开始
        Assert.Equal((5, 27, 42, 255), Px(button, 76, 5));
    }

    [Fact]
    public void ComposedImagesAreBuiltOnceAndReused()
    {
        FakeSprites art = FullArt();
        var screen = new CharacterSelectScreen(Roster(1), new CharacterSelectScene(art));
        SpriteFrame first = Only(screen.Build(0), "select-char-board").Frame;
        SpriteFrame second = Only(screen.Build(500), "select-char-board").Frame;
        Assert.Same(first, second);
        var other = new CharacterSelectScreen(Roster(2), new CharacterSelectScene(art));
        Assert.Same(first, Only(other.Build(0), "select-char-board").Frame);      // 同一份图包共用
    }

    // ------------------------------------------------------------------------------------------------ 点击

    [Fact]
    public void ClickingTheStartButtonStartsAndTheExitButtonQuits()
    {
        var screen = new CharacterSelectScreen(Roster(2), new CharacterSelectScene(FullArt()));
        HitRect start = CharacterSelectArt.ButtonRect(CharacterSelectButton.Start);
        Assert.True(screen.Click(start.X + 3, start.Y + 3));
        Assert.Equal(CharacterSelectOutcome.Start, screen.Outcome);
        Assert.False(screen.Click(start.X + 3, start.Y + 3));                       // 有结果之后不再响应

        var quitting = new CharacterSelectScreen(Roster(2), new CharacterSelectScene(FullArt()));
        HitRect quit = CharacterSelectArt.ButtonRect(CharacterSelectButton.Quit);
        Assert.True(quitting.Click(quit.X + quit.Width - 1, quit.Y + quit.Height - 1));
        Assert.Equal(CharacterSelectOutcome.Quit, quitting.Outcome);
    }

    [Fact]
    public void TheOtherSmallButtonsAreHitButDoNothing()
    {
        foreach (CharacterSelectButton b in new[] { CharacterSelectButton.Create, CharacterSelectButton.Delete, CharacterSelectButton.Channel })
        {
            var screen = new CharacterSelectScreen(Roster(2), new CharacterSelectScene(FullArt()), 1);
            HitRect r = CharacterSelectArt.ButtonRect(b);
            Assert.True(screen.Click(r.X + 2, r.Y + 2));
            Assert.Equal(CharacterSelectOutcome.None, screen.Outcome);
            Assert.Equal(1, screen.Index);
        }
    }

    [Fact]
    public void ClickingACharacterSlotSelectsItAndAnEmptySlotDoesNothing()
    {
        var screen = new CharacterSelectScreen(Roster(3), new CharacterSelectScene(FullArt()));
        CharacterSelectArt.SlotOrigin(2, out int x2, out int y2);
        Assert.True(screen.Click(x2 + 10, y2 + 10));
        Assert.Equal(2, screen.Index);
        CharacterSelectArt.SlotOrigin(4, out int x4, out int y4);
        Assert.True(screen.Click(x4 + 10, y4 + 10));                                 // 空格子:点在界面上,但没有角色
        Assert.Equal(2, screen.Index);
        Assert.False(screen.Click(5, 5));                                           // 界面外的空白
        Assert.Equal(CharacterSelectOutcome.None, screen.Outcome);
    }

    [Fact]
    public void HitGeometryUsesHalfOpenRectangles()
    {
        CharacterSelectArt.SlotOrigin(0, out int x, out int y);
        Assert.Equal(0, CharacterSelectArt.HitSlot(x, y));
        Assert.Equal(0, CharacterSelectArt.HitSlot(x + CharacterSelectArt.SlotWidth - 1, y + CharacterSelectArt.SlotHeight - 1));
        Assert.Equal(-1, CharacterSelectArt.HitSlot(x + CharacterSelectArt.SlotWidth, y));             // 格子之间的缝
        Assert.Equal(-1, CharacterSelectArt.HitSlot(x, y + CharacterSelectArt.SlotHeight));
        Assert.Equal(1, CharacterSelectArt.HitSlot(x + CharacterSelectArt.SlotPitchX, y));
        Assert.Equal(6, CharacterSelectArt.HitSlot(x, y + CharacterSelectArt.SlotPitchY));
        Assert.Equal(CharacterSelectButton.None, CharacterSelectArt.HitButton(0, 0));
        foreach (CharacterSelectButton b in new[] { CharacterSelectButton.Start, CharacterSelectButton.Create, CharacterSelectButton.Delete, CharacterSelectButton.Channel, CharacterSelectButton.Quit })
        {
            HitRect r = CharacterSelectArt.ButtonRect(b);
            Assert.Equal(b, CharacterSelectArt.HitButton(r.X, r.Y));
            Assert.Equal(b, CharacterSelectArt.HitButton(r.X + r.Width - 1, r.Y + r.Height - 1));
            Assert.NotEqual(b, CharacterSelectArt.HitButton(r.X + r.Width, r.Y));
        }
        Assert.Equal(0, CharacterSelectArt.ButtonRect(CharacterSelectButton.None).Width);
    }

    // ------------------------------------------------------------------------------------------------ 头像

    private sealed class FakeAvatars : ICharacterAvatars
    {
        public List<(string Name, long AgeMs, int FootX, int FootY)> Calls { get; } = new List<(string, long, int, int)>();

        public IReadOnlyList<SceneSprite> Pose(CharacterSlot slot, long ageMs, int footX, int footY)
        {
            Calls.Add((slot.Name, ageMs, footX, footY));
            return new List<SceneSprite>
            {
                new SceneSprite(Coded(10, 20, 90), footX - 5, footY - 20, 3, 0, "fake/a", 0),
                new SceneSprite(Coded(6, 6, 91), footX, footY - 10, 9, 0, "fake/b", 2, true, SpriteBlend.Additive, 128, 2),
            };
        }
    }

    [Fact]
    public void AvatarsAreAskedPerCharacterAtTheFootPointAndRewrappedAsUiSprites()
    {
        var avatars = new FakeAvatars();
        var screen = new CharacterSelectScreen(Roster(2), new CharacterSelectScene(FullArt(), avatars));
        screen.Advance(1234);
        List<SceneSprite> sprites = screen.Build(0);

        CharacterSelectArt.FootPoint(0, out int fx0, out int fy0);
        CharacterSelectArt.FootPoint(1, out int fx1, out int fy1);
        Assert.Equal(new[] { ("Hero0", 1234L, fx0, fy0), ("Hero1", 1234L, fx1, fy1) }, avatars.Calls.ToArray());

        List<SceneSprite> avatarSprites = sprites.Where(s => s.AnimationPath == "select-char-avatar").ToList();
        Assert.Equal(4, avatarSprites.Count);
        Assert.All(avatarSprites, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        Assert.Equal((fx0 - 5, fy0 - 20), (avatarSprites[0].ScreenX, avatarSprites[0].ScreenY));
        SceneSprite second = avatarSprites[1];
        Assert.Equal((fx0, fy0 - 10, 2, true, SpriteBlend.Additive, 128), (second.ScreenX, second.ScreenY, second.AniFrame, second.Mirrored, second.Blend, second.TintAlpha));
        // 每个角色:头像 → 两行字;所有精灵的先后编号是连续的
        string[] tags = Tags(sprites).SkipWhile(t => t != "select-char-avatar").TakeWhile(t => t != "select-char-start").ToArray();
        Assert.Equal(new[]
        {
            "select-char-avatar", "select-char-avatar", "select-char-caption", "select-char-caption",
            "select-char-avatar", "select-char-avatar", "select-char-caption", "select-char-caption",
        }, tags);
        Assert.Equal(Enumerable.Range(0, sprites.Count).ToArray(), sprites.Select(s => s.SortKey).ToArray());
    }

    [Fact]
    public void WithoutAnAvatarSourceOnlyThePedestalAndTheCaptionsAreDrawn()
    {
        var screen = new CharacterSelectScreen(Roster(1), new CharacterSelectScene(FullArt()));
        Assert.DoesNotContain("select-char-avatar", Tags(screen.Build(0)));
    }

    [Fact]
    public void TheDefaultAvatarsStandInTheRestPoseAtTheFootPointAndLoopWithTheAnimation()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var avatars = new DnfCharacterAvatars(assets, TownHero.TownRestTag, AvatarFixture.ChrPath, MiniOutfit);
        var slot = new CharacterSlot("Hero", "Slayer", 1);

        IReadOnlyList<SceneSprite> at0 = avatars.Pose(slot, 0, 100, 200);
        Assert.Null(avatars.Failure);
        Assert.NotEmpty(at0);

        // 与直接摆同一套换装、同一份站立动画的第 0 帧一样,脚下点就是给定的屏幕坐标
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        AniFile rest = assets.LoadAni(AvatarFixture.RestAni);
        List<SceneSprite> direct = outfit.SpritesAt(assets, rest, AvatarFixture.RestAni, 0, new TownProjection(0, 0), 100, 200, 0, false, 0, -3);
        Assert.Equal(direct.Select(s => (s.ScreenX, s.ScreenY, s.AnimationPath, s.AniFrame)).ToArray(), at0.Select(s => (s.ScreenX, s.ScreenY, s.AnimationPath, s.AniFrame)).ToArray());

        // 换脚下点,所有精灵整体平移
        IReadOnlyList<SceneSprite> moved = avatars.Pose(slot, 0, 130, 150);
        Assert.Equal(at0.Select(s => (s.ScreenX + 30, s.ScreenY - 50)).ToArray(), moved.Select(s => (s.ScreenX, s.ScreenY)).ToArray());

        // 站立动画循环:第 0 帧 100 毫秒、第 1 帧 300 毫秒
        Assert.Equal(0, avatars.Pose(slot, 99, 0, 0).Max(s => s.AniFrame));
        Assert.Equal(1, avatars.Pose(slot, 100, 0, 0).Max(s => s.AniFrame));
        Assert.Equal(1, avatars.Pose(slot, 399, 0, 0).Max(s => s.AniFrame));
        Assert.Equal(0, avatars.Pose(slot, 400, 0, 0).Max(s => s.AniFrame));
    }

    private static AvatarOutfit MiniOutfit(DnfAssets assets)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        return AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
    }

    [Fact]
    public void AvatarsThatCannotBeReadGiveAnEmptyPoseAndTheReason()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var slot = new CharacterSlot("Hero", "Slayer", 1);

        var noTag = new DnfCharacterAvatars(assets, "[no such motion]", AvatarFixture.ChrPath, MiniOutfit);
        Assert.Empty(noTag.Pose(slot, 0, 10, 10));
        Assert.Contains("[no such motion]", noTag.Failure);
        Assert.Empty(noTag.Pose(slot, 50, 10, 10));                                // 失败只试一次,之后一直空

        var noChr = new DnfCharacterAvatars(assets, TownHero.TownRestTag, "character/none/none.chr", MiniOutfit);
        Assert.Empty(noChr.Pose(slot, 0, 10, 10));
        Assert.NotNull(noChr.Failure);

        Assert.Throws<ArgumentNullException>(() => new DnfCharacterAvatars(null));
        Assert.Throws<ArgumentNullException>(() => new DnfCharacterAvatars(assets, null));
    }

    // ------------------------------------------------------------------------------------------------ 界面动画

    private const string BeamAniPath = "ui/test/beam.ani";
    private const string BeamImage = "sprite/interface2/test/beam.img";

    /// <summary>两帧循环:第 0 帧 500 毫秒、位置 (10, 20)、加法混合;第 1 帧 500 毫秒、位置 (30, 60)、普通混合。<paramref name="interpolate"/> 时第 0 帧向第 1 帧插值。</summary>
    private static AniFile BeamAni(bool interpolate = false, int alpha = -1)
    {
        var f0 = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = 0, X = 10, Y = 20 };
        f0.Props.Add((11, new byte[] { 2, 0 }));
        f0.Props.Add((12, TestData.LE32(500)));
        if (interpolate)
        {
            f0.Props.Add((10, new byte[] { 1 }));
        }
        if (alpha >= 0)
        {
            f0.Props.Add((9, new byte[] { 255, 255, 255, (byte)alpha }));
        }
        var f1 = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = 1, X = 30, Y = 60 };
        f1.Props.Add((12, TestData.LE32(500)));
        return AniFile.Parse(TestData.BuildAni(new[] { "Interface2\\Test\\Beam.img" }, new (ushort, byte[])[] { (0, new byte[] { 1 }) }, new[] { f0, f1 }));
    }

    private static FakeSprites BeamSprites()
    {
        var f = new FakeSprites();
        f.Frames[(BeamImage, 0)] = Coded(40, 30, 50, 2, 3);
        f.Frames[(BeamImage, 1)] = Coded(20, 10, 51, -4, 6);
        return f;
    }

    [Fact]
    public void ANullOrUnreadableAnimationCreatesNothing()
    {
        FakeSprites sprites = BeamSprites();
        Assert.Null(UiAnimation.Create(sprites, null, BeamAniPath, 0, 0));
        Assert.Null(UiAnimation.Create(sprites, p => BeamAni(), "", 0, 0));
        Assert.Null(UiAnimation.Create(sprites, p => null, BeamAniPath, 0, 0));
        var noFrames = AniFile.Parse(TestData.BuildAni(new[] { "A.img" }, Array.Empty<(ushort, byte[])>(), Array.Empty<TestData.AniFrameSpec>()));
        Assert.Null(UiAnimation.Create(sprites, p => noFrames, BeamAniPath, 0, 0));
        Assert.Throws<ArgumentNullException>(() => UiAnimation.Create(null, p => BeamAni(), BeamAniPath, 0, 0));
    }

    [Fact]
    public void AnAnimationFrameIsPlacedAtTheOriginPlusTheFrameOffsetPlusTheImageOffset()
    {
        UiAnimation beam = UiAnimation.Create(BeamSprites(), p => p == BeamAniPath ? BeamAni() : null, BeamAniPath, 100, 200);
        Assert.Equal(1000, beam.DurationMs);
        Assert.Equal(BeamAniPath, beam.Path);

        SceneSprite a = beam.Sprite(0, 7);
        Assert.Equal((100 + 10 + 2, 200 + 20 + 3), (a.ScreenX, a.ScreenY));
        Assert.Equal((SpriteBlend.Additive, 7, BeamAniPath, 0, OverlayArt.UiRank, 255), (a.Blend, a.SortKey, a.AnimationPath, a.AniFrame, a.LayerRank, a.TintAlpha));
        Assert.Equal(50, Px(a.Frame, 0, 0).B);

        SceneSprite b = beam.Sprite(500, 8);
        Assert.Equal((100 + 30 - 4, 200 + 60 + 6), (b.ScreenX, b.ScreenY));
        Assert.Equal((SpriteBlend.Normal, 1), (b.Blend, b.AniFrame));
        Assert.Equal(51, Px(b.Frame, 0, 0).B);
    }

    [Fact]
    public void ALoopingAnimationWrapsAroundItsPeriod()
    {
        UiAnimation beam = UiAnimation.Create(BeamSprites(), p => BeamAni(), BeamAniPath, 0, 0);
        Assert.Equal(1, beam.Sprite(999, 0).AniFrame);
        Assert.Equal(0, beam.Sprite(1000, 0).AniFrame);                               // 一个周期 1000 毫秒,之后回到第 0 帧
        Assert.Equal(0, beam.Sprite(1499, 0).AniFrame);
        Assert.Equal(1, beam.Sprite(1500, 0).AniFrame);
        Assert.Equal(0, beam.Sprite(7 * 1000L + 120, 0).AniFrame);
    }

    [Fact]
    public void FrameOffsetsInterpolateOnlyWhenTheFrameAsksForIt()
    {
        UiAnimation plain = UiAnimation.Create(BeamSprites(), p => BeamAni(), BeamAniPath, 0, 0);
        UiAnimation smooth = UiAnimation.Create(BeamSprites(), p => BeamAni(interpolate: true), BeamAniPath, 0, 0);
        Assert.Equal(10 + 2, plain.Sprite(250, 0).ScreenX);                              // 不插值:停在第 0 帧
        SceneSprite mid = smooth.Sprite(250, 0);                                        // 插值:一半时落在两帧中间
        Assert.Equal((20 + 2, 40 + 3), (mid.ScreenX, mid.ScreenY));
        Assert.Equal(10 + 2, smooth.Sprite(0, 0).ScreenX);
    }

    [Fact]
    public void TheTintAlphaOfAFrameIsCarriedToTheSprite()
    {
        UiAnimation beam = UiAnimation.Create(BeamSprites(), p => BeamAni(alpha: 128), BeamAniPath, 0, 0);
        Assert.Equal(128, beam.Sprite(0, 0).TintAlpha);
    }

    [Fact]
    public void AFrameWhoseImageIsMissingDrawsNothing()
    {
        var sprites = new FakeSprites();
        sprites.Frames[(BeamImage, 1)] = Coded(20, 10, 51);          // 只有第 1 帧的图
        UiAnimation beam = UiAnimation.Create(sprites, p => BeamAni(), BeamAniPath, 0, 0);
        Assert.Null(beam.Sprite(0, 0));
        Assert.NotNull(beam.Sprite(500, 0));
    }

    [Fact]
    public void TheBeamsAreDrawnRightAfterTheBackgroundInOrderAndFollowTheScreenClock()
    {
        FakeSprites art = FullArt();
        foreach (KeyValuePair<(string, int), SpriteFrame> kv in BeamSprites().Frames)
        {
            art.Frames[kv.Key] = kv.Value;
        }
        var scene = new CharacterSelectScene(art, null, p => BeamAni());
        Assert.Equal(3, scene.Beams.Count);
        var screen = new CharacterSelectScreen(Roster(1), scene);
        List<SceneSprite> sprites = screen.Build(0);
        Assert.Equal("select-char-background", sprites[0].AnimationPath);
        Assert.Equal(new[] { CharacterSelectArt.BeamBackPath1, CharacterSelectArt.BeamBackPath2, CharacterSelectArt.BeamSparklePath }, Tags(sprites).Skip(1).Take(3).ToArray());
        Assert.Equal("select-char-board", sprites[4].AnimationPath);
        Assert.Equal((CharacterSelectArt.EffectX + 10 + 2, CharacterSelectArt.EffectY + 20 + 3), (sprites[1].ScreenX, sprites[1].ScreenY));

        screen.Advance(500);
        SceneSprite moved = screen.Build(0)[1];
        Assert.Equal((CharacterSelectArt.EffectX + 30 - 4, CharacterSelectArt.EffectY + 60 + 6), (moved.ScreenX, moved.ScreenY));

        Assert.Empty(new CharacterSelectScene(art).Beams);                                // 没给读 .ani 的函数:没有光束
        Assert.Empty(new CharacterSelectScene(art, null, p => null).Beams);
        Assert.Equal(2, new CharacterSelectScene(art, null, p => p == CharacterSelectArt.BeamBackPath2 ? null : BeamAni()).Beams.Count);
    }

    // ------------------------------------------------------------------------------------------------ 流程

    private sealed class StubWorld : ITownWorld
    {
        public SplashScene LoginSplash { get; set; }

        public List<TownPlace> Created { get; } = new List<TownPlace>();

        public TownPlace Start => new TownPlace(1, 0);

        public TownPlace Return => new TownPlace(1, 2, TownEntry.Return);

        public IHeroSession Create(TownPlace place)
        {
            Created.Add(place);
            return new GameFlowTests.FakeHero("town " + place.Town + "/" + place.Area);
        }

        public SplashScene Splash(TownPlace? from, TownPlace to) => from == null ? LoginSplash : null;

        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft) => null;

        public bool TryRoute(TownPlace from, out TownExit exit)
        {
            exit = default;
            return false;
        }
    }

    private sealed class FakeRun : GameFlowTests.FakeHero, IRunSession
    {
        public FakeRun(string label) : base(label)
        {
        }

        public SettlementChoice Choice => SettlementChoice.None;

        public bool Failed => false;
    }

    private static GameFlow NewFlow(StubWorld world, List<CharacterSelectScreen> screens = null, bool withFactory = true)
    {
        return new GameFlow(
            world,
            (path, run) => new FakeRun("run"),
            new[] { new DungeonEntry("dungeon/test/a.dgn", "AAA") },
            null,
            null,
            withFactory ? () =>
            {
                var screen = new CharacterSelectScreen(Roster(3));
                screens?.Add(screen);
                return screen;
            } : (Func<CharacterSelectScreen>)null);
    }

    private static SplashScene Splash() => new SplashScene("login", Coded(800, 600, 1), null, null, 0, 0, 1000.0);

    [Fact]
    public void TheFlowCanStartOnTheCharacterSelectScreen()
    {
        var screens = new List<CharacterSelectScreen>();
        GameFlow flow = NewFlow(new StubWorld(), screens);
        flow.EnterCharacterSelect();

        Assert.Equal(GameStage.CharacterSelect, flow.Stage);
        Assert.Equal(1, flow.StageSerial);
        Assert.Same(screens[0], flow.CharacterSelect);
        Assert.Same(screens[0], flow.Scene);
        Assert.Null(flow.Session);
        Assert.Null(flow.Select);
        Assert.Null(flow.Splash);
        Assert.Null(flow.Run);
        Assert.False(flow.Poll());                                   // 还没确认
        Assert.Single(flow.History);
        Assert.Equal("start", flow.History[0].Reason);
        Assert.Equal(GameStage.CharacterSelect, flow.History[0].To);
    }

    [Fact]
    public void ConfirmingTheCharacterLogsInThroughTheLoginSplashToTheFirstTownArea()
    {
        var world = new StubWorld { LoginSplash = Splash() };
        GameFlow flow = NewFlow(world);
        flow.EnterCharacterSelect();
        flow.CharacterSelect.Move(1);
        flow.CharacterSelect.Confirm();
        Assert.True(flow.Poll());

        Assert.Equal(GameStage.Loading, flow.Stage);
        Assert.NotNull(flow.Splash);
        Assert.Null(flow.CharacterSelect);                           // 离开选角阶段之后界面清掉
        Assert.Equal("login", flow.History[^1].Reason);
        Assert.Empty(world.Created);                                 // 加载画面停着,城镇还没造

        flow.Splash.Advance(1000);
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal(new TownPlace(1, 0), world.Created.Single());   // 落在城镇世界的第一站
        Assert.Same(flow.Session, flow.Scene);
        Assert.Null(flow.CharacterSelect);
    }

    [Fact]
    public void WithoutALoginSplashTheFlowGoesStraightToTheTown()
    {
        var world = new StubWorld();
        GameFlow flow = NewFlow(world);
        flow.EnterCharacterSelect();
        flow.CharacterSelect.Confirm();
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Equal("login", flow.History[^1].Reason);
        Assert.Equal(new[] { GameStage.CharacterSelect, GameStage.Town }, flow.History.Select(e => e.To).ToArray());
        Assert.Single(world.Created);
    }

    [Fact]
    public void QuittingIsOnlyAScreenOutcomeAndTheFlowStaysOnTheScreen()
    {
        GameFlow flow = NewFlow(new StubWorld());
        flow.EnterCharacterSelect();
        flow.CharacterSelect.Back();
        Assert.False(flow.Poll());
        Assert.Equal(GameStage.CharacterSelect, flow.Stage);
        Assert.Equal(CharacterSelectOutcome.Quit, flow.CharacterSelect.Outcome);
        Assert.Single(flow.History);
    }

    [Fact]
    public void EnteringTheCharacterSelectNeedsAFactoryThatBuildsAScreen()
    {
        GameFlow bare = NewFlow(new StubWorld(), withFactory: false);
        Assert.Throws<InvalidOperationException>(() => bare.EnterCharacterSelect());

        var nulls = new GameFlow(new StubWorld(), (path, run) => new FakeRun("run"), new[] { new DungeonEntry("dungeon/test/a.dgn", "AAA") },
            null, null, () => null);
        Assert.Throws<InvalidOperationException>(() => nulls.EnterCharacterSelect());
        Assert.Equal(0, nulls.StageSerial);
    }

    [Fact]
    public void EachVisitToTheCharacterSelectBuildsAFreshScreen()
    {
        var screens = new List<CharacterSelectScreen>();
        GameFlow flow = NewFlow(new StubWorld(), screens);
        flow.EnterCharacterSelect();
        flow.CharacterSelect.Move(1);
        flow.EnterCharacterSelect("again");
        Assert.Equal(2, screens.Count);
        Assert.NotSame(screens[0], screens[1]);
        Assert.Equal(0, flow.CharacterSelect.Index);
        Assert.Equal(2, flow.StageSerial);
        Assert.Equal("again", flow.History[^1].Reason);
    }

    [Fact]
    public void TheTownAfterLoginIsTheOneAfterTheFirstStartNotAReturn()
    {
        var world = new StubWorld();
        GameFlow flow = NewFlow(world);
        flow.EnterCharacterSelect();
        flow.CharacterSelect.Confirm();
        flow.Poll();
        flow.EnterSelect();
        flow.Select.Back();                                          // 从选择界面回城:回城的落脚点,不再是登录的第一站
        Assert.True(flow.Poll());
        Assert.Equal(new[] { new TownPlace(1, 0), new TownPlace(1, 2, TownEntry.Return) }, world.Created.ToArray());
    }

    // ------------------------------------------------------------------------------------------------ 自动机

    [Fact]
    public void TheBotDwellsOnTheCharacterSelectThenConfirmsTheFirstCharacter()
    {
        var world = new StubWorld();
        GameFlow flow = NewFlow(world);
        flow.EnterCharacterSelect();
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown });

        int steps = (int)(FlowBot.CharacterSelectDwellMs / 100.0);
        for (int i = 0; i < steps - 1; i++)
        {
            bot.Advance(flow, 100);
        }
        Assert.Equal(GameStage.CharacterSelect, flow.Stage);          // 还没停够
        Assert.Equal(CharacterSelectOutcome.None, flow.CharacterSelect.Outcome);
        Assert.Equal((steps - 1) * 100, flow.CharacterSelect.AgeMs);   // 自动机推着界面的时钟走

        bot.Advance(flow, 100);
        Assert.Equal(GameStage.Town, flow.Stage);                     // 停够了:确认、登录、进城
        Assert.Equal("login", flow.History[^1].Reason);
        Assert.Equal(0, world.Created.Single().Area);
        Assert.False(bot.Done);
        Assert.Equal(0, bot.ChoicesMade);
    }

    [Fact]
    public void TheBotWaitsOutTheLoginSplashAfterTheCharacterSelect()
    {
        var world = new StubWorld { LoginSplash = Splash() };
        GameFlow flow = NewFlow(world);
        flow.EnterCharacterSelect();
        var bot = new FlowBot(false, new[] { SettlementChoice.ReturnToTown });
        for (int i = 0; i < 15; i++)
        {
            bot.Advance(flow, 100);
        }
        Assert.Equal(GameStage.Loading, flow.Stage);
        for (int i = 0; i < 10; i++)
        {
            bot.Advance(flow, 100);
        }
        Assert.Equal(GameStage.Town, flow.Stage);
    }
}
