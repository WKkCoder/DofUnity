using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 合成的连击图:三个颜色档、每档十一帧(帧号 = 档 × 11 + 数字;档 × 11 + 10 是标签),每帧是一块纯色不透明的方块。
/// 数字 d 的画布宽 = 20 + d,方块与画布一样宽;只有数字 1 例外:方块 12 宽、水平偏移 6、画布 21 宽(与原版 1 的图块比画布窄同一个形状)。
/// 标签 100 × 19,偏移 (3, 1)。像素 R = 10 + 20 × 数字(标签是 250)、G = 档、B = 0x40。数据与原版图包无关。
/// </summary>
internal sealed class ComboSprites : ISpriteSource
{
    public const int Tall = 13;
    public const int LabelWidth = 100;
    public const int LabelHeight = 19;
    public const int LabelOffsetX = 3;
    public const int LabelOffsetY = 1;
    public const int OneBlock = 12;
    public const int OneOffsetX = 6;

    private readonly Dictionary<int, SpriteFrame> _frames = new Dictionary<int, SpriteFrame>();

    /// <param name="tiers">要有哪几档(缺省三档都有)。</param>
    public ComboSprites(params int[] tiers)
    {
        if (tiers.Length == 0)
        {
            tiers = new[] { 0, 1, 2 };
        }
        foreach (int tier in tiers)
        {
            for (int digit = 0; digit < 10; digit++)
            {
                _frames[tier * ComboArt.FramesPerTier + digit] = Digit(tier, digit);
            }
            _frames[tier * ComboArt.FramesPerTier + 10] = Solid(LabelWidth, LabelHeight, LabelOffsetX, LabelOffsetY, LabelWidth, 250, tier);
        }
    }

    public static int CanvasWidth(int digit)
    {
        return 20 + digit;
    }

    private static SpriteFrame Digit(int tier, int digit)
    {
        return digit == 1
            ? Solid(OneBlock, Tall, OneOffsetX, 0, CanvasWidth(1), 10 + 20 * digit, tier)
            : Solid(CanvasWidth(digit), Tall, 0, 0, CanvasWidth(digit), 10 + 20 * digit, tier);
    }

    private static SpriteFrame Solid(int w, int h, int offsetX, int offsetY, int canvasW, int red, int tier)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = (byte)red;
            rgba[i * 4 + 1] = (byte)tier;
            rgba[i * 4 + 2] = 0x40;
            rgba[i * 4 + 3] = 255;
        }
        return new SpriteFrame(w, h, offsetX, offsetY, canvasW, h, rgba);
    }

    /// <summary>从像素看出这一帧是哪一档的哪个数字(标签的数字为 −1)。</summary>
    public static (int Tier, int Digit) Decode(SpriteFrame frame)
    {
        int red = frame.Rgba[0];
        return (frame.Rgba[1], red == 250 ? -1 : (red - 10) / 20);
    }

    public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
    {
        sprite = null;
        return string.Equals(imageName, ComboArt.Image, StringComparison.OrdinalIgnoreCase) && _frames.TryGetValue(frame, out sprite);
    }
}

/// <summary>什么图都没有的图源。</summary>
internal sealed class NoSprites : ISpriteSource
{
    public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
    {
        sprite = null;
        return false;
    }
}

/// <summary>连击计数的状态机、缓动与滑入抖动的时间线、画法(证据 E-108);全部用合成数据。</summary>
public class ComboCounterTests
{
    // ---- 计数与窗口 ----

    [Fact]
    public void TheFirstHitCountsButShowsNothingAndTheSecondHitShowsTwo()
    {
        var c = new ComboCounter();
        Assert.Equal((0, 0), (c.Count, c.Shown));
        c.Hit(100);
        Assert.Equal((1, 0), (c.Count, c.Shown));
        c.Update(110);
        Assert.Equal((1, 0), (c.Count, c.Shown));
        c.Hit(120);
        Assert.Equal((2, 2), (c.Count, c.Shown));
        c.Hit(130);
        Assert.Equal((3, 3), (c.Count, c.Shown));
    }

    [Fact]
    public void TheCountClearsExactlyWhenTheWindowHasRunOutSinceTheLastHit()
    {
        var c = new ComboCounter();
        Assert.Equal(1200, c.WindowMs);
        c.Hit(1000);
        c.Hit(1050);
        c.Update(1050 + 1199);
        Assert.Equal(2, c.Count);                                          // 窗口差 1 毫秒走满
        c.Update(1050 + 1200);
        Assert.Equal((0, 0), (c.Count, c.Shown));                          // 走满即清零(不小于)
        c.Hit(5000);
        Assert.Equal(1, c.Count);                                          // 清零之后重新从 1 数起
    }

    [Fact]
    public void EveryHitInsideTheWindowRestartsTheWindow()
    {
        var c = new ComboCounter();
        double t = 0;
        for (int i = 0; i < 6; i++)
        {
            c.Hit(t);
            t += 1100;                                                     // 每击隔 1100 毫秒,比窗口短
            c.Update(t);
        }
        Assert.Equal(6, c.Count);                                          // 总共 6.6 秒,连击没断
        c.Update(t - 1100 + 1200);
        Assert.Equal(0, c.Count);
    }

    [Fact]
    public void ACustomWindowIsHonouredAndNeverBelowOneMillisecond()
    {
        var c = new ComboCounter(500);
        c.Hit(0);
        c.Hit(10);
        c.Update(509);
        Assert.Equal(2, c.Count);
        c.Update(510);
        Assert.Equal(0, c.Count);
        Assert.Equal(1, new ComboCounter(-5).WindowMs);
        Assert.Equal(1, new ComboCounter(0).WindowMs);
    }

    [Fact]
    public void ResetClearsTheCount()
    {
        var c = new ComboCounter();
        c.Hit(0);
        c.Hit(1);
        c.Reset();
        Assert.Equal((0, 0), (c.Count, c.Shown));
    }

    [Fact]
    public void TheDisplayTimerRestartsOnEveryHitFromTheSecondOne()
    {
        var c = new ComboCounter();
        c.Hit(1000);                                                       // 第一击不显示,不重启显示计时器
        c.Hit(1200);
        c.Update(1200);
        Assert.Equal(0, c.AgeMs);
        c.Update(1450);
        Assert.Equal(250, c.AgeMs);
        c.Update(1450.9);
        Assert.Equal(250, c.AgeMs);                                        // 毫秒向下取整
        c.Hit(1460);                                                       // 第三击:重启
        Assert.Equal(0, c.AgeMs);
        c.Update(1475);
        Assert.Equal(15, c.AgeMs);
    }

    [Fact]
    public void TheTierFollowsTheShownCount()
    {
        foreach ((int shown, int tier) in new[] { (0, 0), (2, 0), (10, 0), (11, 1), (20, 1), (21, 2), (999, 2) })
        {
            Assert.Equal(tier, ComboCounter.TierOf(shown));
        }
        var c = new ComboCounter();
        for (int i = 0; i < 21; i++)
        {
            c.Hit(i);
        }
        Assert.Equal((21, 2), (c.Shown, c.Tier));
    }

    // ---- 缓动 ----

    [Fact]
    public void EaseReturnsTheEndsAtTheBoundsAndTheQuadraticCurveBetween()
    {
        Assert.Equal(20, ComboCounter.Ease(10, 20, 5, 5, true));           // 已过 ≥ 总长 → 终点
        Assert.Equal(20, ComboCounter.Ease(10, 20, 6, 5, false));
        Assert.Equal(10, ComboCounter.Ease(10, 20, 0, 50, true));          // 已过不到 1 → 起点
        Assert.Equal(10, ComboCounter.Ease(10, 20, -3, 50, false));
        // 先快后慢:位移比例 1 − (1 − t)²,t = 30 / 80
        Assert.Equal(60, ComboCounter.Ease(0, 100, 30, 80, true));         // 60.9375 向零取整
        Assert.Equal(39, ComboCounter.Ease(100, 0, 30, 80, true));         // 100 − 60.9375 = 39.0625
        // 先慢后快:位移比例 t²
        Assert.Equal(14, ComboCounter.Ease(0, 100, 30, 80, false));        // 14.0625
        Assert.Equal(85, ComboCounter.Ease(100, 0, 30, 80, false));        // 100 − 14.0625 = 85.9375
        Assert.Equal(5, ComboCounter.Ease(5, 5, 20, 80, true));            // 起点终点相同
    }

    [Fact]
    public void EaseOutIsAheadOfEaseInAndBothAreMonotonic()
    {
        int lastOut = int.MinValue;
        int lastIn = int.MinValue;
        for (int e = 1; e < 250; e++)
        {
            int o = ComboCounter.Ease(0, 1000, e, 250, true);
            int i = ComboCounter.Ease(0, 1000, e, 250, false);
            Assert.True(o >= lastOut && i >= lastIn);
            Assert.True(o >= i);
            lastOut = o;
            lastIn = i;
        }
        Assert.True(lastOut > 990 && lastIn < 1000);
    }

    // ---- 滑入与抖动 ----

    [Fact]
    public void TheFirstShowSlidesInFromTheRightAndSettlesAtTheRestPosition()
    {
        // 显示数 2:年龄 < 250 先快后慢 110 → 0;250 到 300 先慢后快 0 → 5;之后静止
        foreach ((int age, int x) in new[]
        {
            (0, 780), (1, 779), (125, 697), (200, 674), (249, 670),
            (250, 670), (275, 671), (299, 674), (300, 675), (301, 675), (5000, 675),
        })
        {
            Assert.Equal(x, ComboCounter.LabelXAt(2, age));
        }
    }

    [Fact]
    public void EveryFurtherHitShakesTheLabelByFivePixels()
    {
        // 显示数 > 2:年龄 < 50 用 250 毫秒的先快后慢 5 → 0 的开头;50 到 100 先慢后快 0 → 5;之后静止
        foreach (int shown in new[] { 3, 11, 25 })
        {
            foreach ((int age, int x) in new[]
            {
                (0, 675), (1, 674), (25, 674), (49, 673),
                (50, 670), (51, 670), (75, 671), (99, 674), (100, 675), (101, 675), (5000, 675),
            })
            {
                Assert.Equal(x, ComboCounter.LabelXAt(shown, age));
            }
        }
    }

    [Fact]
    public void TheLabelPositionNeverLeavesTheSlideRange()
    {
        for (int age = 0; age < 400; age++)
        {
            Assert.InRange(ComboCounter.LabelXAt(2, age), ComboCounter.BaseX, ComboCounter.BaseX + ComboCounter.SlideFrom);
            Assert.InRange(ComboCounter.LabelXAt(3, age), ComboCounter.BaseX, ComboCounter.RestX);
        }
    }

    [Fact]
    public void TheCounterExposesTheLabelXAtItsOwnAge()
    {
        var c = new ComboCounter();
        c.Hit(0);
        c.Hit(100);
        c.Update(100);
        Assert.Equal(780, c.LabelX);                                       // 刚出现:在最右
        c.Update(400);
        Assert.Equal(675, c.LabelX);
        c.Hit(410);                                                        // 第三击:抖动开始
        Assert.Equal(675, c.LabelX);
        c.Update(460);
        Assert.Equal(670, c.LabelX);
    }

    // ---- 画法 ----

    [Fact]
    public void ForReturnsNullWithoutTheComboImage()
    {
        Assert.Null(ComboArt.For(null));
        Assert.Null(ComboArt.For(new NoSprites()));
        Assert.NotNull(ComboArt.For(new ComboSprites()));
    }

    [Fact]
    public void TheLabelIsDrawnFirstThenTheDigitsRightAlignedWithTheSixPixelOverlap()
    {
        ComboArt art = ComboArt.For(new ComboSprites());
        var into = new List<SceneSprite>();
        int order = 7;
        Assert.True(art.Build(23, 1000, into, ref order));                 // 红色一档,静止位置 675
        Assert.Equal(10, order);
        Assert.Equal(3, into.Count);
        Assert.All(into, s => Assert.Equal(("combo", OverlayArt.UiRank), (s.AnimationPath, s.LayerRank)));
        Assert.Equal(new[] { 7, 8, 9 }, into.Select(s => s.SortKey).ToArray());

        // 标签:左边缘 675 加画布内偏移,顶边 243 + 8 加画布内偏移
        SceneSprite label = into[0];
        Assert.Equal((2, -1), ComboSprites.Decode(label.Frame));
        Assert.Equal((675 + ComboSprites.LabelOffsetX, 251 + ComboSprites.LabelOffsetY), (label.ScreenX, label.ScreenY));

        // 数字从最后一位往前画:3 的画布 23 宽,推进 23 − 6 = 17;2 的画布 22 宽,再推进 16
        SceneSprite last = into[1];
        SceneSprite first = into[2];
        Assert.Equal((2, 3), ComboSprites.Decode(last.Frame));
        Assert.Equal((2, 2), ComboSprites.Decode(first.Frame));
        Assert.Equal((675 - 17, 243), (last.ScreenX, last.ScreenY));
        Assert.Equal((675 - 33, 243), (first.ScreenX, first.ScreenY));
    }

    [Fact]
    public void TheDigitOneAdvancesByItsCanvasWidthAndDrawsItsBlockAtTheOffset()
    {
        ComboArt art = ComboArt.For(new ComboSprites());
        var into = new List<SceneSprite>();
        int order = 0;
        Assert.True(art.Build(11, 1000, into, ref order));                 // 黄色一档,"11"
        Assert.Equal(3, into.Count);
        SceneSprite right = into[1];
        SceneSprite left = into[2];
        Assert.Equal((1, 1), ComboSprites.Decode(right.Frame));
        Assert.Equal((1, 1), ComboSprites.Decode(left.Frame));
        Assert.Equal(ComboSprites.OneBlock, right.Frame.Width);
        // 推进量是画布宽 21 − 6 = 15,图块画在 x − 累计 + 画布内偏移 6
        Assert.Equal(675 - 15 + ComboSprites.OneOffsetX, right.ScreenX);
        Assert.Equal(675 - 30 + ComboSprites.OneOffsetX, left.ScreenX);
    }

    [Fact]
    public void TheColourTierPicksTheFrameSet()
    {
        ComboArt art = ComboArt.For(new ComboSprites());
        foreach ((int shown, int tier) in new[] { (2, 0), (9, 0), (10, 0), (11, 1), (20, 1), (21, 2), (30, 2) })
        {
            var into = new List<SceneSprite>();
            int order = 0;
            Assert.True(art.Build(shown, 1000, into, ref order));
            Assert.All(into, s => Assert.Equal(tier, ComboSprites.Decode(s.Frame).Tier));
        }
    }

    [Fact]
    public void TheLabelFollowsTheSlideOnTheFirstShow()
    {
        ComboArt art = ComboArt.For(new ComboSprites());
        var into = new List<SceneSprite>();
        int order = 0;
        Assert.True(art.Build(2, 0, into, ref order));
        Assert.Equal(780 + ComboSprites.LabelOffsetX, into[0].ScreenX);    // 刚出现:在最右
        Assert.Equal(780 - (ComboSprites.CanvasWidth(2) - ComboArt.DigitOverlap), into[1].ScreenX);
        into.Clear();
        Assert.True(art.Build(2, 300, into, ref order));
        Assert.Equal(675 + ComboSprites.LabelOffsetX, into[0].ScreenX);
    }

    [Fact]
    public void NothingIsDrawnBelowTheThresholdOrWhenAFrameIsMissing()
    {
        ComboArt art = ComboArt.For(new ComboSprites(0));                  // 只有青色一档
        var into = new List<SceneSprite>();
        int order = 5;
        Assert.False(art.Build(0, 0, into, ref order));
        Assert.False(art.Build(1, 0, into, ref order));
        Assert.False(art.Build(11, 0, into, ref order));                   // 黄色一档缺帧:不画一半
        Assert.False(art.Build(2, 0, null, ref order));
        Assert.Empty(into);
        Assert.Equal(5, order);
        Assert.True(art.Build(2, 0, into, ref order));
        Assert.Equal(2, into.Count);
    }

    [Fact]
    public void TheDigitStringClampsToTheClientLimit()
    {
        Assert.Equal("7", ComboArt.Digits(7));
        Assert.Equal("123", ComboArt.Digits(123));
        Assert.Equal("9999999", ComboArt.Digits(9999999));
        Assert.Equal("9999999", ComboArt.Digits(12345678));
        Assert.Equal("0", ComboArt.Digits(-4));
    }
}
