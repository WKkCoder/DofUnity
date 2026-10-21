using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 合成的数字图:每套十帧("0" 到 "9"),每帧是一块纯色不透明的方块,宽度 <see cref="Wide"/>(数字 1 是 <see cref="Narrow"/>)、高度 <see cref="Tall"/>,
/// 颜色 R = 10 + 20 × 数字、G = 这一套的首帧号(取低 8 位)、B = 0x40,所以从像素就能看出这一格画的是哪个数字、哪一套。
/// 数据与原版图包无关;帧号借用原版 combo_big.img 里各套的首帧号,只为了让标志到数字套的对应看得出来。
/// </summary>
internal sealed class DigitSprites : ISpriteSource
{
    public const int Wide = 20;
    public const int Narrow = 14;
    public const int Tall = 13;

    private readonly Dictionary<int, SpriteFrame> _frames = new Dictionary<int, SpriteFrame>();

    public DigitSprites(params int[] firstFrames)
    {
        if (firstFrames.Length == 0)
        {
            firstFrames = new[]
            {
                DamageNumberFlags.GreyFirstFrame, DamageNumberFlags.RedFirstFrame, DamageNumberFlags.GreenFirstFrame,
                DamageNumberFlags.CyanFirstFrame, DamageNumberFlags.YellowFirstFrame,
            };
        }
        foreach (int first in firstFrames)
        {
            for (int digit = 0; digit < 10; digit++)
            {
                _frames[first + digit] = Glyph(first, digit);
            }
        }
    }

    public static byte[] Color(int firstFrame, int digit)
    {
        return new[] { (byte)(10 + 20 * digit), (byte)(firstFrame & 0xFF), (byte)0x40, (byte)255 };
    }

    private static SpriteFrame Glyph(int firstFrame, int digit)
    {
        int w = digit == 1 ? Narrow : Wide;
        var rgba = new byte[w * Tall * 4];
        byte[] c = Color(firstFrame, digit);
        for (int i = 0; i < w * Tall; i++)
        {
            Array.Copy(c, 0, rgba, i * 4, 4);
        }
        return new SpriteFrame(w, Tall, 0, 0, w, Tall, rgba);
    }

    public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
    {
        sprite = null;
        return string.Equals(imageName, DamageNumberArt.Image, StringComparison.OrdinalIgnoreCase) && _frames.TryGetValue(frame, out sprite);
    }
}

/// <summary>
/// 伤害数字(证据 E-107):标志位选数字套与动画模板、五个关键帧的动画(线性插值、到点消失)、位图字的拼法(相邻数字叠 6 个像素、数字 1 叠 5 个)、
/// 放在屏幕上的位置(原点在命中点横坐标减推进宽度之半,缩放以原点为中心)。数据全是合成的,与原版数值无关。
/// </summary>
public class DamageNumberTests
{
    private static readonly TownProjection Projection = new TownProjection(50, 200);

    private static HitPopup Popup(int value, int flags = DamageNumberFlags.AttackerIsHero, double ageMs = 250, double x = 200, double y = 120, int z = 90)
    {
        return new HitPopup(value, flags, x, y, z) { AgeMs = ageMs };
    }

    private static byte[] Pixel(SpriteFrame f, int x, int y)
    {
        int o = (y * f.Width + x) * 4;
        return new[] { f.Rgba[o], f.Rgba[o + 1], f.Rgba[o + 2], f.Rgba[o + 3] };
    }

    // ------------------------------------------------------------------ 标志位

    [Theory]
    [InlineData(0, 5, 44)]                                                 // 都没有:灰白
    [InlineData(DamageNumberFlags.AttackerIsHero, 5, 143)]                 // 位 0:黄
    [InlineData(DamageNumberFlags.VictimIsHero, 5, 54)]                    // 位 1:红橙
    [InlineData(DamageNumberFlags.AttackerIsHero | DamageNumberFlags.VictimIsHero, 5, 143)]   // 位 0 先于位 1
    [InlineData(DamageNumberFlags.Cyan, 5, 74)]                            // 位 3:青
    [InlineData(DamageNumberFlags.BySign, 5, 64)]                          // 位 2 且为正:绿
    [InlineData(DamageNumberFlags.BySign, -5, 44)]                         // 位 2 且为负:灰白
    [InlineData(DamageNumberFlags.BySign | DamageNumberFlags.Cyan, 5, 74)] // 位 3 先于位 2
    [InlineData(DamageNumberFlags.VictimIsHero | DamageNumberFlags.Cyan, 5, 54)]   // 位 1 先于位 3
    [InlineData(DamageNumberFlags.Critical, 5, 44)]                        // 暴击位不选套
    [InlineData(DamageNumberFlags.Steady, 5, 44)]
    [InlineData(DamageNumberFlags.AttackerIsHero | DamageNumberFlags.Critical, 5, 143)]
    public void TheFlagsPickTheDigitSetInTheOriginalPriorityOrder(int flags, int value, int expectedFirstFrame)
    {
        Assert.Equal(expectedFirstFrame, DamageNumberFlags.FirstFrame(flags, value));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(DamageNumberFlags.AttackerIsHero, 0)]
    [InlineData(DamageNumberFlags.VictimIsHero, 0)]
    [InlineData(DamageNumberFlags.Critical, 1)]
    [InlineData(DamageNumberFlags.Steady, 2)]
    [InlineData(DamageNumberFlags.Critical | DamageNumberFlags.Steady, 1)]  // 暴击先于固定缩放
    [InlineData(DamageNumberFlags.AttackerIsHero | DamageNumberFlags.Critical, 1)]
    public void TheFlagsPickTheAnimationTemplate(int flags, int expectedTemplate)
    {
        Assert.Equal(expectedTemplate, DamageNumberFlags.Template(flags));
    }

    // ------------------------------------------------------------------ 动画

    [Fact]
    public void TheOrdinaryTemplatePopsInShrinksHoldsThenRisesAndFadesOverOneSecond()
    {
        Assert.Equal(1000, DamageNumberAnimation.DurationMs(0));
        // 前 200 毫秒:缩放 2.0 线性回到 1.0
        Assert.True(DamageNumberAnimation.TryPose(0, 0, out DamageNumberPose p));
        Assert.Equal((2.0, 0.0, 255), (p.Scale, p.YOffset, p.Alpha));
        Assert.True(DamageNumberAnimation.TryPose(0, 100, out p));
        Assert.Equal((1.5, 0.0, 255), (p.Scale, p.YOffset, p.Alpha));
        // 之后 400 毫秒停住
        foreach (double age in new[] { 200.0, 399.0, 599.0 })
        {
            Assert.True(DamageNumberAnimation.TryPose(0, age, out p));
            Assert.Equal((1.0, 0.0, 255), (p.Scale, p.YOffset, p.Alpha));
        }
        // 再 200 毫秒:偏移 0 → -39、alpha 255 → 128(线性,alpha 四舍五入)
        Assert.True(DamageNumberAnimation.TryPose(0, 600, out p));
        Assert.Equal((1.0, 0.0, 255), (p.Scale, p.YOffset, p.Alpha));
        Assert.True(DamageNumberAnimation.TryPose(0, 700, out p));
        Assert.Equal((1.0, -19.5, 192), (p.Scale, p.YOffset, p.Alpha));
        // 最后 200 毫秒:偏移 -39 → -69、alpha 128 → 0
        Assert.True(DamageNumberAnimation.TryPose(0, 800, out p));
        Assert.Equal((1.0, -39.0, 128), (p.Scale, p.YOffset, p.Alpha));
        Assert.True(DamageNumberAnimation.TryPose(0, 900, out p));
        Assert.Equal((1.0, -54.0, 64), (p.Scale, p.YOffset, p.Alpha));
        Assert.True(DamageNumberAnimation.TryPose(0, 999, out p));
        Assert.InRange(p.YOffset, -69.0, -68.0);
        Assert.InRange(p.Alpha, 0, 1);
        // 到末帧就播完
        Assert.False(DamageNumberAnimation.TryPose(0, 1000, out _));
        Assert.False(DamageNumberAnimation.TryPose(0, 5000, out _));
    }

    [Fact]
    public void TheCriticalTemplateHoldsEveryStepOneAndAHalfTimesAsLong()
    {
        Assert.Equal(1500, DamageNumberAnimation.DurationMs(1));
        Assert.True(DamageNumberAnimation.TryPose(1, 150, out DamageNumberPose p));
        Assert.Equal(1.5, p.Scale);                                        // 300 毫秒里缩放 2.0 → 1.0,走到一半
        Assert.True(DamageNumberAnimation.TryPose(1, 899, out p));
        Assert.Equal((1.0, 0.0, 255), (p.Scale, p.YOffset, p.Alpha));      // 300 + 600:停住到 900
        Assert.True(DamageNumberAnimation.TryPose(1, 1050, out p));
        Assert.Equal((-19.5, 192), (p.YOffset, p.Alpha));                   // 第 3 步(900 起 300 毫秒)走到一半
        Assert.True(DamageNumberAnimation.TryPose(1, 1499, out _));
        Assert.False(DamageNumberAnimation.TryPose(1, 1500, out _));
    }

    [Fact]
    public void TheSteadyTemplateDoesNotPopAndKeepsItsScaleAllTheWay()
    {
        Assert.Equal(1000, DamageNumberAnimation.DurationMs(2));
        foreach (double age in new[] { 0.0, 100.0, 500.0, 700.0, 900.0 })
        {
            Assert.True(DamageNumberAnimation.TryPose(2, age, out DamageNumberPose p));
            Assert.Equal(1.2, p.Scale);
        }
        Assert.True(DamageNumberAnimation.TryPose(2, 700, out DamageNumberPose rising));
        Assert.Equal((-19.5, 192), (rising.YOffset, rising.Alpha));        // 上飘与淡出同普通模板
    }

    [Fact]
    public void ANegativeAgeIsTheFirstPoseAndAnUnknownTemplateClampsToTheNearestOne()
    {
        Assert.True(DamageNumberAnimation.TryPose(0, -50, out DamageNumberPose p));
        Assert.Equal((2.0, 0.0, 255), (p.Scale, p.YOffset, p.Alpha));
        Assert.Equal(DamageNumberAnimation.DurationMs(0), DamageNumberAnimation.DurationMs(-3));
        Assert.Equal(DamageNumberAnimation.DurationMs(2), DamageNumberAnimation.DurationMs(9));
    }

    // ------------------------------------------------------------------ 位图字

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1234, "1234")]
    [InlineData(-56, "56")]                                                // 取绝对值
    [InlineData(9999999, "9999999")]
    [InlineData(12345678, "9999999")]                                      // 夹在 9999999
    [InlineData(int.MinValue, "9999999")]
    public void TheDigitsAreTheClampedAbsoluteValue(int value, string expected)
    {
        Assert.Equal(expected, DamageNumberArt.Digits(value));
    }

    [Fact]
    public void TheArtNeedsTheYellowSetAndAnyMissingGlyphMeansNoArt()
    {
        Assert.Null(DamageNumberArt.For(null));
        Assert.Null(DamageNumberArt.For(new DigitSprites(DamageNumberFlags.RedFirstFrame)));      // 没有黄色那一套的 "0"
        DamageNumberArt art = DamageNumberArt.For(new DigitSprites(DamageNumberFlags.YellowFirstFrame));
        Assert.NotNull(art);
        Assert.Equal(-1, art.AdvanceWidth(7, DamageNumberFlags.RedFirstFrame));                  // 红色那一套缺帧
        Assert.Null(art.Place(Popup(7, DamageNumberFlags.VictimIsHero), Projection, 0));
        Assert.NotNull(art.Place(Popup(7), Projection, 0));
    }

    [Fact]
    public void NeighbouringDigitsOverlapBySixPixelsAndTheDigitOneBySix_MinusOne()
    {
        DamageNumberArt art = DamageNumberArt.For(new DigitSprites());
        int first = DamageNumberFlags.YellowFirstFrame;
        Assert.Equal(DigitSprites.Wide - 6, art.AdvanceWidth(7, first));                          // 一位:自己的宽减 6
        Assert.Equal(DigitSprites.Narrow - 5, art.AdvanceWidth(1, first));                        // 数字 1:减 5
        Assert.Equal((DigitSprites.Wide - 6) + (DigitSprites.Narrow - 5), art.AdvanceWidth(21, first));
        Assert.Equal(2 * (DigitSprites.Narrow - 5), art.AdvanceWidth(11, first));
        Assert.Equal((DigitSprites.Narrow - 5) + 2 * (DigitSprites.Wide - 6), art.AdvanceWidth(100, first));
        Assert.Equal(art.AdvanceWidth(56, first), art.AdvanceWidth(-56, first));                  // 负数取绝对值
    }

    [Fact]
    public void TheLaterDigitIsDrawnOverTheEarlierOneAndThePaddingIsTransparent()
    {
        DamageNumberArt art = DamageNumberArt.For(new DigitSprites());
        int first = DamageNumberFlags.YellowFirstFrame;
        // "21":数字 2 在贴图的 [0, 20),数字 1 从推进宽度 14 起;两者重叠的 [14, 20) 里是后画的 1
        SceneSprite s = art.Place(Popup(21, ageMs: 250), Projection, 0);
        SpriteFrame f = s.Frame;
        Assert.Equal((28 + 2, DigitSprites.Tall + 2), (f.Width, f.Height));                      // 四周各留 1 个像素的透明边
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(f, 0, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(f, f.Width - 1, f.Height - 1));
        Assert.Equal(DigitSprites.Color(first, 2), Pixel(f, 1, 1));                              // 左上角是 2
        Assert.Equal(DigitSprites.Color(first, 2), Pixel(f, 14, 5));                             // 重叠区之前还是 2
        Assert.Equal(DigitSprites.Color(first, 1), Pixel(f, 15, 5));                             // 重叠区从 1 起(14 + 内边 1)
        Assert.Equal(DigitSprites.Color(first, 1), Pixel(f, 20, 5));
        Assert.Equal(DigitSprites.Color(first, 1), Pixel(f, 28, DigitSprites.Tall));             // 右下角是 1
        // 同样两个数字换个顺序:"12" 里 2 盖在 1 上
        SpriteFrame g = art.Place(Popup(12, ageMs: 250), Projection, 0).Frame;
        Assert.Equal(DigitSprites.Color(first, 1), Pixel(g, 1, 1));
        Assert.Equal(DigitSprites.Color(first, 1), Pixel(g, 9, 5));                              // 1 在 [1, 15)、2 从推进 9 起 → 内边 10
        Assert.Equal(DigitSprites.Color(first, 2), Pixel(g, 10, 5));
    }

    [Fact]
    public void EachFlagSetReadsItsOwnDigitFrames()
    {
        DamageNumberArt art = DamageNumberArt.For(new DigitSprites());
        foreach ((int flags, int first) in new[]
        {
            (DamageNumberFlags.AttackerIsHero, 143), (DamageNumberFlags.VictimIsHero, 54), (DamageNumberFlags.Cyan, 74),
            (DamageNumberFlags.BySign, 64), (0, 44),
        })
        {
            SceneSprite s = art.Place(Popup(7, flags), Projection, 0);
            Assert.Equal(DigitSprites.Color(first, 7), Pixel(s.Frame, 1, 1));
        }
    }

    // ------------------------------------------------------------------ 位置与姿态

    [Fact]
    public void AtNativeSizeTheTextureSitsAtTheOriginMinusHalfTheAdvanceWidthAndTheOnePixelPadding()
    {
        DamageNumberArt art = DamageNumberArt.For(new DigitSprites());
        // "21":推进宽度 14 + 9 = 23,半宽 11。世界 (200, 120, 90),相机 50、地面 200:
        // 原点 = (200 - 11 - 50, 120 + 200 - 90) = (139, 230);贴图左上角 = 原点 - (11 + 1, 1) = (127, 229)
        SceneSprite s = art.Place(Popup(21, ageMs: 250), Projection, 17);
        Assert.Equal((127, 229), (s.ScreenX, s.ScreenY));
        Assert.Equal((17, -3, "damage", 255, OverlayArt.DimRank), (s.SortKey, s.NpcId, s.AnimationPath, s.TintAlpha, s.LayerRank));
        Assert.Equal(SpriteBlend.Normal, s.Blend);
        Assert.False(s.Mirrored);
    }

    [Fact]
    public void ScalingKeepsTheOriginFixedAndTheSizeFollowsTheAnimation()
    {
        DamageNumberArt art = DamageNumberArt.For(new DigitSprites());
        int unscaledW = 28 + 2, unscaledH = DigitSprites.Tall + 2;
        foreach (double age in new[] { 0.0, 50.0, 100.0, 150.0, 200.0, 250.0 })
        {
            Assert.True(DamageNumberAnimation.TryPose(0, age, out DamageNumberPose pose));
            SceneSprite s = art.Place(Popup(21, ageMs: age), Projection, 0);
            double scale = s.Frame.Width / (double)unscaledW;
            Assert.InRange(scale, pose.Scale - 0.04, pose.Scale + 0.04);                        // 尺寸跟着缩放走(量化到 1/32 再取整)
            // 原点(139, 230)在放大后的贴图里仍是 (内边 + 半宽) × 缩放 处:水平居中的缩放中心,竖直在顶边
            Assert.InRange(s.ScreenX + (1 + 11) * scale, 139 - 1.0, 139 + 1.0);
            Assert.InRange(s.ScreenY + 1 * scale, 230 - 1.0, 230 + 1.0);
        }
        // 弹出时是 2 倍,原尺寸时是 1 倍
        Assert.Equal((unscaledW * 2, unscaledH * 2), SizeAt(art, 0));
        Assert.Equal((unscaledW, unscaledH), SizeAt(art, 250));
    }

    private static (int, int) SizeAt(DamageNumberArt art, double age)
    {
        SceneSprite s = art.Place(Popup(21, ageMs: age), Projection, 0);
        return (s.Frame.Width, s.Frame.Height);
    }

    [Fact]
    public void ThePopupRisesAndFadesOnTheOriginalTimetable()
    {
        DamageNumberArt art = DamageNumberArt.For(new DigitSprites());
        SceneSprite hold = art.Place(Popup(21, ageMs: 500), Projection, 0);
        SceneSprite half = art.Place(Popup(21, ageMs: 700), Projection, 0);                      // 偏移 -19.5、alpha 192
        SceneSprite late = art.Place(Popup(21, ageMs: 900), Projection, 0);                      // 偏移 -54、alpha 64
        Assert.Equal((229, 255), (hold.ScreenY, hold.TintAlpha));
        Assert.Equal((210, 192), (half.ScreenY, half.TintAlpha));                                // 229 - 19.5 = 209.5 → 四舍五入 210
        Assert.Equal((175, 64), (late.ScreenY, late.TintAlpha));                                 // 229 - 54
        Assert.Equal(hold.ScreenX, half.ScreenX);                                                // 上飘时水平不动
        Assert.Equal(hold.ScreenX, late.ScreenX);
        Assert.Null(art.Place(Popup(21, ageMs: 1000), Projection, 0));                           // 播完了:不画
        Assert.Null(art.Place(null, Projection, 0));
    }

    [Fact]
    public void ThePositionFollowsTheCameraAndTheGround()
    {
        DamageNumberArt art = DamageNumberArt.For(new DigitSprites());
        SceneSprite a = art.Place(Popup(21, ageMs: 250), new TownProjection(50, 200), 0);
        SceneSprite b = art.Place(Popup(21, ageMs: 250), new TownProjection(80, 230), 0);
        Assert.Equal((a.ScreenX - 30, a.ScreenY + 30), (b.ScreenX, b.ScreenY));
        SceneSprite higher = art.Place(Popup(21, ageMs: 250, z: 120), new TownProjection(50, 200), 0);
        Assert.Equal(a.ScreenY - 30, higher.ScreenY);                                            // z 越高越靠上
    }

    [Fact]
    public void ScaledFramesAndComposedFramesAreCachedByContent()
    {
        DamageNumberArt art = DamageNumberArt.For(new DigitSprites());
        // 原尺寸:同一串数字同一套图是同一个帧对象(Unity 端按帧对象缓存纹理,不会随画面增长)
        Assert.Same(art.Place(Popup(21, ageMs: 250), Projection, 0).Frame, art.Place(Popup(21, ageMs: 300), Projection, 5).Frame);
        // 放大:缩放量化到 1/32,0 毫秒的 2.0 与 1 毫秒的 1.995 量化后是同一个比例,也是同一个帧对象
        Assert.Same(art.Place(Popup(21, ageMs: 0), Projection, 0).Frame, art.Place(Popup(21, ageMs: 1), Projection, 0).Frame);
        // 换了数字串或者换了一套图就是另一个帧
        Assert.NotSame(art.Place(Popup(21, ageMs: 250), Projection, 0).Frame, art.Place(Popup(12, ageMs: 250), Projection, 0).Frame);
        Assert.NotSame(art.Place(Popup(21, ageMs: 250), Projection, 0).Frame,
            art.Place(Popup(21, DamageNumberFlags.VictimIsHero, ageMs: 250), Projection, 0).Frame);
    }

    // ------------------------------------------------------------------ 双线性放缩

    private static SpriteFrame Solid(int w, int h, byte r, byte g, byte b, byte a)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = a;
        }
        return new SpriteFrame(w, h, 0, 0, w, h, rgba);
    }

    [Fact]
    public void TheBilinearResizeIsTheIdentityAtTheSameSizeAndKeepsAFlatColourFlat()
    {
        var rgba = new byte[3 * 2 * 4];
        for (int i = 0; i < rgba.Length; i++)
        {
            rgba[i] = (byte)(i * 11);
        }
        var src = new SpriteFrame(3, 2, 0, 0, 3, 2, rgba);
        Assert.Equal(rgba, DamageNumberArt.Bilinear(src, 3, 2).Rgba);

        SpriteFrame flat = DamageNumberArt.Bilinear(Solid(4, 3, 200, 100, 50, 255), 9, 7);
        Assert.Equal((9, 7), (flat.Width, flat.Height));
        for (int i = 0; i < 9 * 7; i++)
        {
            Assert.Equal(new byte[] { 200, 100, 50, 255 }, new[] { flat.Rgba[i * 4], flat.Rgba[i * 4 + 1], flat.Rgba[i * 4 + 2], flat.Rgba[i * 4 + 3] });
        }
    }

    [Fact]
    public void TheBilinearResizeInterpolatesBetweenPixelCentresAndClampsAtTheEdges()
    {
        // 两个像素 (红 0、红 255)放大到 4 个:采样点在源坐标 -0.25、0.25、0.75、1.25,夹在边上
        var src = new SpriteFrame(2, 1, 0, 0, 2, 1, new byte[] { 0, 0, 0, 255, 255, 0, 0, 255 });
        SpriteFrame up = DamageNumberArt.Bilinear(src, 4, 1);
        Assert.Equal(new byte[] { 0, 64, 191, 255 }, new[] { up.Rgba[0], up.Rgba[4], up.Rgba[8], up.Rgba[12] });
        Assert.All(new[] { 3, 7, 11, 15 }, o => Assert.Equal(255, up.Rgba[o]));                 // alpha 全程 255
    }
}
