using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 合成的复活倒计时图:大数字(0 到 9,画布 20×30,块 18×28、偏移 (1, 1),像素红 = 10 + 数字)、币图标(28×28)、
/// 币存量的小数字(画布 8×10,块 6×8、偏移 (1, 1),像素红 = 100 + 数字)与乘号(8×8,红 = 200)。数据与原版图包无关。
/// </summary>
internal sealed class ReviveSprites : ISpriteSource
{
    public const int DigitWidth = 20;
    public const int DigitHeight = 30;
    public const int IconSize = 28;
    public const int StockWidth = 8;
    public const int StockHeight = 10;
    public const int TimesSize = 8;

    private readonly Dictionary<(string, int), SpriteFrame> _frames = new Dictionary<(string, int), SpriteFrame>();

    /// <param name="digits">有大数字图。</param>
    /// <param name="icon">有币图标。</param>
    /// <param name="stock">有小数字与乘号。</param>
    /// <param name="missingDigit">大数字里缺的一帧(−1 不缺)。</param>
    public ReviveSprites(bool digits = true, bool icon = true, bool stock = true, int missingDigit = -1)
    {
        if (digits)
        {
            for (int d = 0; d < 10; d++)
            {
                if (d != missingDigit)
                {
                    _frames[(ReviveArt.DigitImage, d)] = Block(18, 28, 1, 1, DigitWidth, DigitHeight, 10 + d);
                }
            }
        }
        if (icon)
        {
            _frames[(ReviveArt.IconImage, ReviveArt.IconFrame)] = Block(IconSize, IconSize, 0, 0, IconSize, IconSize, 250);
        }
        if (stock)
        {
            for (int d = 0; d < 10; d++)
            {
                _frames[(ReviveArt.StockImage, d)] = Block(6, 8, 1, 1, StockWidth, StockHeight, 100 + d);
            }
            _frames[(ReviveArt.StockImage, ReviveArt.TimesFrame)] = Block(TimesSize, TimesSize, 0, 0, TimesSize, TimesSize, 200);
        }
    }

    private static SpriteFrame Block(int w, int h, int offsetX, int offsetY, int canvasW, int canvasH, int red)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = (byte)red;
            rgba[i * 4 + 3] = 255;
        }
        return new SpriteFrame(w, h, offsetX, offsetY, canvasW, canvasH, rgba);
    }

    public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
    {
        return _frames.TryGetValue((imageName.ToLowerInvariant(), frame), out sprite);
    }
}

/// <summary>复活倒计时的画法(证据 E-115):大数字居中、提示行、币存量行;缺图时退回占位的像素字。数据全合成。</summary>
public class ReviveArtTests
{
    private static List<SceneSprite> Build(ReviveArt art, int seconds, CoinVerdict verdict = CoinVerdict.Pay, int stock = 3)
    {
        var into = new List<SceneSprite>();
        int order = 0;
        Assert.True(art.Build(into, ref order, new OverlayArt(), seconds, verdict, stock));
        Assert.Equal(into.Count, order);                                        // 每个精灵占一个序号
        return into;
    }

    private static List<SceneSprite> Tagged(List<SceneSprite> sprites, string tag)
    {
        return sprites.Where(s => s.AnimationPath == tag).ToList();
    }

    [Fact]
    public void TheArtNeedsTheBigDigitImageAndNothingElse()
    {
        Assert.Null(ReviveArt.For(null));
        Assert.Null(ReviveArt.For(new NoSprites()));
        Assert.Null(ReviveArt.For(new ReviveSprites(digits: false)));
        Assert.NotNull(ReviveArt.For(new ReviveSprites(icon: false, stock: false)));
        Assert.NotNull(ReviveArt.For(new ReviveSprites()));
    }

    [Fact]
    public void TheDisplayedSecondsAreClampedToTwoDigits()
    {
        Assert.Equal("10", ReviveArt.Digits(10));
        Assert.Equal("7", ReviveArt.Digits(7));
        Assert.Equal("0", ReviveArt.Digits(0));
        Assert.Equal("0", ReviveArt.Digits(-4));
        Assert.Equal("99", ReviveArt.Digits(150));
    }

    [Fact]
    public void ThePromptFollowsTheVerdict()
    {
        string ok = ReviveArt.PromptOf(CoinVerdict.Pay);
        Assert.Equal(ok, ReviveArt.PromptOf(CoinVerdict.Free));                 // 免费与用币都是"按键复活"
        Assert.NotEqual(ok, ReviveArt.PromptOf(CoinVerdict.NoCoin));
        Assert.NotEqual(ReviveArt.PromptOf(CoinVerdict.NoCoin), ReviveArt.PromptOf(CoinVerdict.LimitReached));
    }

    [Fact]
    public void TheBigDigitsAreCenteredWithTheOverlapBetweenNeighbours()
    {
        ReviveArt art = ReviveArt.For(new ReviveSprites())!;
        List<SceneSprite> two = Tagged(Build(art, 10), "revive-digit");
        Assert.Equal(2, two.Count);
        int total = ReviveSprites.DigitWidth + (ReviveSprites.DigitWidth - ReviveArt.DigitOverlap);
        int x0 = ReviveArt.CenterX - total / 2;
        Assert.Equal((x0 + 1, ReviveArt.DigitTop + 1), (two[0].ScreenX, two[0].ScreenY));                           // 块偏移 (1, 1)
        Assert.Equal((x0 + ReviveSprites.DigitWidth - ReviveArt.DigitOverlap + 1, ReviveArt.DigitTop + 1), (two[1].ScreenX, two[1].ScreenY));
        Assert.Equal((11, 10), (two[0].Frame.Rgba[0], two[1].Frame.Rgba[0]));                                       // "1" 然后 "0"

        List<SceneSprite> one = Tagged(Build(art, 7), "revive-digit");
        Assert.Single(one);
        Assert.Equal(ReviveArt.CenterX - ReviveSprites.DigitWidth / 2 + 1, one[0].ScreenX);
        Assert.Equal(17, one[0].Frame.Rgba[0]);
    }

    [Fact]
    public void ThePromptIsCenteredUnderTheDigitsAndTheStockLineUnderThePrompt()
    {
        ReviveArt art = ReviveArt.For(new ReviveSprites())!;
        List<SceneSprite> all = Build(art, 10, CoinVerdict.Pay, 12);
        SceneSprite prompt = Assert.Single(Tagged(all, "revive-prompt"));
        Assert.Equal(ReviveArt.DigitTop + ReviveSprites.DigitHeight + ReviveArt.PromptGap, prompt.ScreenY);
        Assert.Equal(ReviveArt.CenterX - prompt.Frame.Width / 2, prompt.ScreenX);

        List<SceneSprite> stock = Tagged(all, "revive-stock");
        Assert.Equal(4, stock.Count);                                           // 币图标、乘号、两位数的存量
        int lineTop = prompt.ScreenY + prompt.Frame.Height + ReviveArt.LineGap;
        int total = ReviveSprites.IconSize + 3 + ReviveSprites.TimesSize + 3 + 2 * ReviveSprites.StockWidth;
        int x = ReviveArt.CenterX - total / 2;
        Assert.Equal((x, lineTop, 250), (stock[0].ScreenX, stock[0].ScreenY, stock[0].Frame.Rgba[0]));              // 币图标
        x += ReviveSprites.IconSize + 3;
        Assert.Equal((x, lineTop + (ReviveSprites.IconSize - ReviveSprites.TimesSize) / 2, 200), (stock[1].ScreenX, stock[1].ScreenY, stock[1].Frame.Rgba[0]));   // 乘号,垂直居中到币图标
        x += ReviveSprites.TimesSize + 3;
        Assert.Equal((x + 1, lineTop + (ReviveSprites.IconSize - ReviveSprites.StockHeight) / 2 + 1, 101), (stock[2].ScreenX, stock[2].ScreenY, stock[2].Frame.Rgba[0]));
        Assert.Equal((x + ReviveSprites.StockWidth + 1, 102), (stock[3].ScreenX, stock[3].Frame.Rgba[0]));
    }

    [Fact]
    public void ABlockedVerdictDrawsADifferentPromptAndAFreeRevivalHasNoCoinLine()
    {
        ReviveArt art = ReviveArt.For(new ReviveSprites())!;
        SpriteFrame pay = Assert.Single(Tagged(Build(art, 5, CoinVerdict.Pay), "revive-prompt")).Frame;
        SpriteFrame none = Assert.Single(Tagged(Build(art, 5, CoinVerdict.NoCoin, 0), "revive-prompt")).Frame;
        SpriteFrame limit = Assert.Single(Tagged(Build(art, 5, CoinVerdict.LimitReached, 2), "revive-prompt")).Frame;
        Assert.NotSame(pay, none);
        Assert.NotSame(none, limit);
        Assert.True(HasInk(none, ReviveArt.BlockedColor));                      // 不能复活的提示是红色,能复活的不是
        Assert.True(HasInk(limit, ReviveArt.BlockedColor));
        Assert.False(HasInk(pay, ReviveArt.BlockedColor));
        Assert.True(HasInk(pay, OverlayArt.Yellow));

        List<SceneSprite> free = Build(art, 5, CoinVerdict.Free, 0);
        SceneSprite line = Assert.Single(Tagged(free, "revive-stock"));         // 免费复活:没有币图标与存量图,只写一行 FREE
        Assert.True(line.Frame.Width > 0);
        Assert.Equal(new[] { "revive-digit", "revive-prompt", "revive-stock" }, free.Select(s => s.AnimationPath).ToArray());
    }

    /// <summary>文字帧里有没有这个颜色(0xRRGGBB)的可见像素(像素字带黑色描边,不能只看第一个有墨的像素)。</summary>
    private static bool HasInk(SpriteFrame frame, uint rgb)
    {
        for (int i = 0; i + 3 < frame.Rgba.Length; i += 4)
        {
            if (frame.Rgba[i + 3] != 0 && frame.Rgba[i] == (byte)(rgb >> 16) && frame.Rgba[i + 1] == (byte)(rgb >> 8) && frame.Rgba[i + 2] == (byte)rgb)
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void MissingFramesFallBackWithoutHalfDrawnLines()
    {
        var into = new List<SceneSprite>();
        int order = 0;
        ReviveArt missingDigit = ReviveArt.For(new ReviveSprites(missingDigit: 5))!;
        Assert.False(missingDigit.Build(into, ref order, new OverlayArt(), 5, CoinVerdict.Pay, 1));
        Assert.Equal((0, 0), (into.Count, order));                               // 缺一个数字:整个不画,由调用方退回占位版

        ReviveArt noIcon = ReviveArt.For(new ReviveSprites(icon: false))!;
        List<SceneSprite> text = Build(noIcon, 5, CoinVerdict.Pay, 4);
        Assert.Single(Tagged(text, "revive-stock"));                             // 没有币图标:存量写成文字一行,不是缺半截的图
        Assert.Single(Tagged(text, "revive-prompt"));
    }

    [Fact]
    public void ThePlaceholderDrawsTheSameThreeLinesInPixelText()
    {
        var into = new List<SceneSprite>();
        int order = 100;
        ReviveArt.BuildPlaceholder(into, ref order, new OverlayArt(), 10, CoinVerdict.Pay, 3);
        Assert.Equal(103, order);
        Assert.Equal(new[] { "revive-digit", "revive-prompt", "revive-stock" }, into.Select(s => s.AnimationPath).ToArray());
        Assert.Equal(ReviveArt.CenterX - into[0].Frame.Width / 2, into[0].ScreenX);
        Assert.Equal(ReviveArt.CenterX - into[1].Frame.Width / 2, into[1].ScreenX);

        var free = new List<SceneSprite>();
        order = 0;
        ReviveArt.BuildPlaceholder(free, ref order, new OverlayArt(), 3, CoinVerdict.Free, 0);
        Assert.Equal(3, free.Count);
    }
}
