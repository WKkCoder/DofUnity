using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Render;

namespace Dnf.Render.Tests;

public class AniTimelineTests
{
    private static AniFile Ani(bool loop, params int[] delays)
    {
        var frames = new List<TestData.AniFrameSpec>();
        foreach (int d in delays)
        {
            var f = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = frames.Count };
            if (d >= 0)
            {
                f.Props.Add((12, TestData.LE32(d)));
            }
            frames.Add(f);
        }
        var globals = loop ? new (ushort, byte[])[] { (0, new byte[] { 1 }) } : Array.Empty<(ushort, byte[])>();
        return AniFile.Parse(TestData.BuildAni(new[] { "A.img" }, globals, frames));
    }

    [Fact]
    public void LoopingAnimationWrapsAround()
    {
        var t = new AniTimeline(Ani(true, 100, 200, 300));
        Assert.True(t.Loops);
        Assert.Equal(600, t.TotalMilliseconds);
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2, 0, 0, 1 }, new[] { t.FrameAt(0), t.FrameAt(99), t.FrameAt(100), t.FrameAt(299), t.FrameAt(300), t.FrameAt(599), t.FrameAt(600), t.FrameAt(699), t.FrameAt(700) });
        Assert.Equal(0, t.FrameAt(600 * 1000L));
    }

    [Fact]
    public void NonLoopingAnimationStopsOnTheLastFrame()
    {
        var t = new AniTimeline(Ani(false, 100, 200, 300));
        Assert.False(t.Loops);
        Assert.Equal(2, t.FrameAt(600));
        Assert.Equal(2, t.FrameAt(1_000_000));
        Assert.Equal(0, t.FrameAt(-5));
    }

    [Fact]
    public void FramesWithoutDelayTakeNoTime()
    {
        var t = new AniTimeline(Ani(true, 100, -1, 100));   // 第 1 帧没有延时属性
        Assert.Equal(200, t.TotalMilliseconds);
        Assert.Equal(0, t.FrameAt(50));
        Assert.Equal(2, t.FrameAt(100));                   // 时长为 0 的帧不会被选中
    }

    [Fact]
    public void AllZeroDelaysStayOnFrameZero()
    {
        var t = new AniTimeline(Ani(true, -1, -1));
        Assert.Equal(0, t.FrameAt(12345));
    }

    [Fact]
    public void TheHoldOverloadReplacesOnlyTheFirstFramesDelay()
    {
        // 第 0 帧是 10000 毫秒的蓄力占位,换成 150 毫秒:后面的帧时长不动,总时长跟着变(波系技能的施法蓄力,证据 E-135)
        AniFile ani = Ani(false, 10000, 50, 50, 50, 50);
        var plain = new AniTimeline(ani);
        var held = new AniTimeline(ani, 150);
        Assert.Equal(10200, plain.TotalMilliseconds);
        Assert.Equal(350, held.TotalMilliseconds);
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 3, 4, 4 }, new[] { held.FrameAt(0), held.FrameAt(149), held.FrameAt(150), held.FrameAt(199), held.FrameAt(200), held.FrameAt(250), held.FrameAt(300), held.FrameAt(5000) });
        Assert.Equal(new long[] { 0, 150, 200, 250, 300 }, Enumerable.Range(0, 5).Select(held.StartMs).ToArray());
        // 不换(负数)与第一个构造函数一样
        var same = new AniTimeline(ani, -1);
        Assert.Equal(plain.TotalMilliseconds, same.TotalMilliseconds);
        Assert.Equal(plain.FrameAt(9999), same.FrameAt(9999));
    }

    [Fact]
    public void AZeroHoldSkipsTheFirstFrameAndFrameLengthsAreReported()
    {
        AniFile ani = Ani(false, 10000, 50, 70);
        var held = new AniTimeline(ani, 0);
        Assert.Equal(120, held.TotalMilliseconds);
        Assert.Equal(1, held.FrameAt(0));                      // 时长为 0 的帧不会被选中
        Assert.Equal(new long[] { 0, 50, 70, 0, 0 }, new[] { held.FrameLengthMs(0), held.FrameLengthMs(1), held.FrameLengthMs(2), held.FrameLengthMs(3), held.FrameLengthMs(-1) });
        var plain = new AniTimeline(ani);
        Assert.Equal(new long[] { 10000, 50, 70 }, new[] { plain.FrameLengthMs(0), plain.FrameLengthMs(1), plain.FrameLengthMs(2) });
    }

    [Fact]
    public void NoFramesGivesMinusOne()
    {
        var t = new AniTimeline(AniFile.Parse(TestData.BuildAni(new[] { "A.img" }, Array.Empty<(ushort, byte[])>(), Array.Empty<TestData.AniFrameSpec>())));
        Assert.Equal(-1, t.FrameAt(0));
        Assert.Equal(-1, t.Sample(0).Frame);
        Assert.Equal(255, t.TintAlphaAt(0));
    }

    /// <summary>与城镇 GateOutLight、GatePillarLight 同构:alpha 0 → 255(没有染色)→ 0,帧 0、1 带插值开关且各 1000 ms,帧 2 没有时长,循环。</summary>
    private static AniFile GlowAni(bool lastFrameInterpolates = false)
    {
        var f0 = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = 0 };
        f0.Props.Add((9, new byte[] { 255, 255, 255, 0 }));
        f0.Props.Add((10, new byte[] { 1 }));
        f0.Props.Add((12, TestData.LE32(1000)));
        var f1 = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = 0 };
        f1.Props.Add((10, new byte[] { 1 }));
        f1.Props.Add((12, TestData.LE32(1000)));
        var f2 = new TestData.AniFrameSpec { ImageIndex = 0, FrameIndex = 0 };
        f2.Props.Add((9, new byte[] { 255, 255, 255, 0 }));
        if (lastFrameInterpolates)
        {
            f2.Props.Add((10, new byte[] { 1 }));
            f2.Props.Add((12, TestData.LE32(500)));
        }
        return AniFile.Parse(TestData.BuildAni(new[] { "A.img" }, new (ushort, byte[])[] { (0, new byte[] { 1 }) }, new[] { f0, f1, f2 }));
    }

    [Fact]
    public void SampleReportsTheFrameInterpolationTargetAndProgress()
    {
        var t = new AniTimeline(GlowAni());
        Assert.Equal(2000, t.TotalMilliseconds);

        AniSample a = t.Sample(545);
        Assert.Equal((0, 1, true), (a.Frame, a.NextFrame, a.Interpolates));
        Assert.Equal(0.545, a.Progress, 6);

        AniSample b = t.Sample(1000);
        Assert.Equal((1, 2, true), (b.Frame, b.NextFrame, b.Interpolates));
        Assert.Equal(0.0, b.Progress, 6);

        // 循环:2000 回到帧 0;帧 2 时长为 0,不会被选中
        Assert.Equal(0, t.Sample(2000).Frame);
        Assert.Equal(1, t.Sample(1999).Frame);
        // FrameAt 就是 Sample().Frame
        for (long ms = 0; ms < 4200; ms += 37)
        {
            Assert.Equal(t.Sample(ms).Frame, t.FrameAt(ms));
        }
    }

    [Fact]
    public void TintAlphaInterpolatesLinearlyBetweenKeyFramesAndRoundsToNearest()
    {
        var t = new AniTimeline(GlowAni());
        // 帧 0 → 帧 1:0 → 255;545 ms 处 255 × 0.545 = 138.975 → 139(城镇截图里光效层的 alpha,证据 E-030)
        Assert.Equal(0, t.TintAlphaAt(0));
        Assert.Equal(139, t.TintAlphaAt(545));
        Assert.Equal(255, t.TintAlphaAt(1000));
        // 帧 1 → 帧 2:255 → 0,对称
        Assert.Equal(139, t.TintAlphaAt(1455));
        Assert.Equal(0, t.TintAlphaAt(1999));
        Assert.Equal(0, t.TintAlphaAt(2000));       // 循环回帧 0
        Assert.Equal(139, t.TintAlphaAt(2545));
    }

    [Fact]
    public void FramesWithoutTheInterpolationFlagUseTheirOwnTintAndTheLastFrameNeverInterpolates()
    {
        // 没有插值开关:染色 alpha 就是本帧的(没有染色属性按 255),Interpolates 为假
        var plain = new AniTimeline(Ani(true, 100, 200));
        AniSample s = plain.Sample(150);
        Assert.False(s.Interpolates);
        Assert.Equal(s.Frame, s.NextFrame);
        Assert.Equal(255, plain.TintAlphaAt(150));

        // 最后一帧即使带开关也没有下一帧可插:不插值
        var t = new AniTimeline(GlowAni(lastFrameInterpolates: true));
        Assert.Equal(2500, t.TotalMilliseconds);
        AniSample last = t.Sample(2200);
        Assert.Equal(2, last.Frame);
        Assert.False(last.Interpolates);
        Assert.Equal(0, t.TintAlphaAt(2200));
    }
}

public class RgbaCanvasTests
{
    private static SpriteFrame Sprite(int w, int h, params byte[] rgba) => new SpriteFrame(w, h, 0, 0, w, h, rgba);

    [Fact]
    public void OpaquePixelsOverwriteAndTransparentOnesAreSkipped()
    {
        var c = new RgbaCanvas(3, 1);
        c.Clear(50, 60, 70, 255);
        c.Draw(Sprite(3, 1, 1, 2, 3, 255, 9, 9, 9, 0, 4, 5, 6, 255), 0, 0);
        Assert.Equal(new byte[] { 1, 2, 3, 255, 50, 60, 70, 255, 4, 5, 6, 255 }, c.Pixels);
    }

    [Fact]
    public void HalfTransparentPixelBlendsOverAnOpaqueBackground()
    {
        var c = new RgbaCanvas(1, 1);
        c.Clear(100, 200, 0, 255);
        c.Draw(Sprite(1, 1, 200, 0, 100, 128), 0, 0);
        // (200*128 + 100*127 + 127)/255 = 150;(0*128 + 200*127 + 127)/255 = 100;(100*128 + 0*127 + 127)/255 = 50
        Assert.Equal(new byte[] { 150, 100, 50, 255 }, c.Pixels);
    }

    [Fact]
    public void DrawingOntoATransparentCanvasKeepsTheStraightColor()
    {
        var c = new RgbaCanvas(1, 1);
        c.Draw(Sprite(1, 1, 200, 0, 100, 128), 0, 0);
        Assert.Equal(new byte[] { 200, 0, 100, 128 }, c.Pixels);
    }

    [Fact]
    public void SpritesAreClippedAtEveryEdge()
    {
        var c = new RgbaCanvas(4, 4);
        var s = Sprite(2, 2, 1, 1, 1, 255, 2, 2, 2, 255, 3, 3, 3, 255, 4, 4, 4, 255);
        c.Draw(s, -1, -1);   // 只有右下角那一个像素落在画布里
        Assert.Equal(new byte[] { 4, 4, 4, 255 }, c.Pixels.Take(4).ToArray());
        Assert.Equal(0, c.Pixels[4]);
        c.Draw(s, 3, 3);     // 只有左上角那一个像素落在画布里
        Assert.Equal(new byte[] { 1, 1, 1, 255 }, c.Pixels.Skip((3 * 4 + 3) * 4).Take(4).ToArray());
        c.Draw(s, 100, 100); // 完全在外面:什么也不发生
        c.Draw(s, -100, 0);
    }

    [Fact]
    public void MirroredDrawFlipsLeftAndRight()
    {
        var f = new SpriteFrame(3, 1, 0, 0, 3, 1, new byte[] { 1, 0, 0, 255, 0, 2, 0, 255, 0, 0, 3, 255 });
        var c = new RgbaCanvas(5, 2);
        c.Clear(9, 9, 9, 255);
        c.Draw(f, 1, 1, mirrored: true);
        Assert.Equal(new byte[] { 0, 0, 3, 255, 0, 2, 0, 255, 1, 0, 0, 255 }, c.Pixels.Skip((1 * 5 + 1) * 4).Take(12).ToArray());
        Assert.Equal(new byte[] { 9, 9, 9, 255 }, c.Pixels.Skip((1 * 5 + 0) * 4).Take(4).ToArray());
        Assert.Equal(new byte[] { 9, 9, 9, 255 }, c.Pixels.Skip((1 * 5 + 4) * 4).Take(4).ToArray());
    }

    [Fact]
    public void MirroredDrawIsClippedLikeTheNormalOne()
    {
        var f = new SpriteFrame(3, 1, 0, 0, 3, 1, new byte[] { 1, 0, 0, 255, 0, 2, 0, 255, 0, 0, 3, 255 });
        var c = new RgbaCanvas(4, 1);
        c.Clear(9, 9, 9, 255);
        c.Draw(f, -1, 0, mirrored: true);              // 翻转后是 [B,G,R],左边一格被裁掉,剩下 [G,R]
        Assert.Equal(new byte[] { 0, 2, 0, 255, 1, 0, 0, 255, 9, 9, 9, 255, 9, 9, 9, 255 }, c.Pixels);
    }

    [Fact]
    public void SpriteFrameRejectsAWrongBufferLength()
    {
        Assert.Throws<ArgumentException>(() => new SpriteFrame(2, 2, 0, 0, 2, 2, new byte[15]));
    }
}

public class AdditiveBlendTests
{
    private static SpriteFrame Sprite(int w, int h, params byte[] rgba) => new SpriteFrame(w, h, 0, 0, w, h, rgba);

    [Fact]
    public void AddsSourceTimesAlphaTimesTintAndClampsAt255()
    {
        // dst + src × src.a/255 × A/255 = dst + src × 128 × 139 / 65025,最后四舍五入一次
        var c = new RgbaCanvas(1, 1);
        c.Clear(100, 200, 250, 255);
        c.Draw(Sprite(1, 1, 200, 100, 50, 128), 0, 0, false, SpriteBlend.Additive, 139);
        // R: 100 + 54.72 = 154.72 → 155;G: 200 + 27.36 = 227.36 → 227;B: 250 + 13.68 = 263.68 → 255
        Assert.Equal(new byte[] { 155, 227, 255, 255 }, c.Pixels);
    }

    [Fact]
    public void RoundsOnceAtTheEndNotAfterEachMultiplication()
    {
        // 130 × 1 × 139 / 65025 = 0.28:一次取整得 100;若先把 a × A 取整成 1 再乘,会得到 101(在原版截图上是前者,证据 E-030)
        var c = new RgbaCanvas(1, 1);
        c.Clear(100, 0, 0, 255);
        c.Draw(Sprite(1, 1, 130, 0, 0, 1), 0, 0, false, SpriteBlend.Additive, 139);
        Assert.Equal(100, c.Pixels[0]);
    }

    [Fact]
    public void FullyTransparentPixelsAndZeroTintChangeNothing()
    {
        var c = new RgbaCanvas(2, 1);
        c.Clear(10, 20, 30, 255);
        c.Draw(Sprite(2, 1, 255, 255, 255, 0, 255, 255, 255, 255), 0, 0, false, SpriteBlend.Additive, 0);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 10, 20, 30, 255 }, c.Pixels);
        c.Draw(Sprite(2, 1, 255, 255, 255, 0, 1, 1, 1, 255), 0, 0, false, SpriteBlend.Additive, 255);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 11, 21, 31, 255 }, c.Pixels);
    }

    [Fact]
    public void FullAlphaAndFullTintAddTheSourceColour()
    {
        var c = new RgbaCanvas(1, 1);
        c.Clear(5, 250, 0, 255);
        c.Draw(Sprite(1, 1, 7, 7, 7, 255), 0, 0, false, SpriteBlend.Additive, 255);
        Assert.Equal(new byte[] { 12, 255, 7, 255 }, c.Pixels);
    }

    [Fact]
    public void AdditiveDrawIsClippedAndCanBeMirrored()
    {
        var f = Sprite(3, 1, 10, 0, 0, 255, 0, 20, 0, 255, 0, 0, 30, 255);
        var c = new RgbaCanvas(4, 1);
        c.Clear(1, 1, 1, 255);
        c.Draw(f, -1, 0, true, SpriteBlend.Additive, 255);       // 翻转后 [B,G,R],左边一格被裁掉 → [G,R]
        Assert.Equal(new byte[] { 1, 21, 1, 255, 11, 1, 1, 255, 1, 1, 1, 255, 1, 1, 1, 255 }, c.Pixels);
        c.Draw(f, 100, 100, false, SpriteBlend.Additive, 255);   // 完全在外面
    }

    [Fact]
    public void AdditiveLeavesTheCanvasAlphaAlone()
    {
        var c = new RgbaCanvas(1, 1);
        c.Clear(10, 10, 10, 77);
        c.Draw(Sprite(1, 1, 10, 10, 10, 255), 0, 0, false, SpriteBlend.Additive, 255);
        Assert.Equal(new byte[] { 20, 20, 20, 77 }, c.Pixels);
    }

    [Fact]
    public void NormalBlendScalesTheTextureAlphaByTheTintAlpha()
    {
        // 权重 w = 贴图 alpha × 染色 alpha;out = (src*w + dst*(65025-w) + 32512) / 65025,只在最后取整一次
        var c = new RgbaCanvas(2, 1);
        c.Clear(100, 100, 100, 255);
        // 不透明像素、染色 128:w = 255*128 = 32640 → R: (200*32640 + 100*32385 + 32512)/65025 = 150.2 → 150
        c.Draw(Sprite(2, 1, 200, 0, 0, 255, 200, 0, 0, 128), 0, 0, false, SpriteBlend.Normal, 128);
        // 第二个像素:w = 128*128 = 16384 → R: (200*16384 + 100*48641 + 32512)/65025 = 125.2 → 125;G、B: 100*(48641)+0 → 74.8 → 75
        Assert.Equal(new byte[] { 150, 50, 50, 255, 125, 75, 75, 255 }, c.Pixels);
    }

    [Fact]
    public void TintingAnOpaquePixelEqualsGivingItThatTextureAlpha()
    {
        // 不透明像素配染色 t,与"贴图 alpha = t、不染色"逐位相同:两条取整路径(除以 65025 与除以 255)在整数上给出同一个值
        byte[][] colours = { new byte[] { 255, 3, 90 }, new byte[] { 12, 200, 7 }, new byte[] { 0, 0, 0 }, new byte[] { 250, 251, 252 } };
        byte[][] backgrounds = { new byte[] { 37, 91, 203 }, new byte[] { 0, 255, 128 }, new byte[] { 255, 255, 255 } };
        foreach (byte[] src in colours)
        {
            foreach (byte[] dst in backgrounds)
            {
                for (int t = 1; t <= 254; t++)
                {
                    var tinted = new RgbaCanvas(1, 1);
                    var textured = new RgbaCanvas(1, 1);
                    tinted.Clear(dst[0], dst[1], dst[2], 255);
                    textured.Clear(dst[0], dst[1], dst[2], 255);
                    tinted.Draw(Sprite(1, 1, src[0], src[1], src[2], 255), 0, 0, false, SpriteBlend.Normal, t);
                    textured.Draw(Sprite(1, 1, src[0], src[1], src[2], (byte)t), 0, 0, false, SpriteBlend.Normal, 255);
                    Assert.Equal(textured.Pixels, tinted.Pixels);
                }
            }
        }
    }

    [Fact]
    public void NormalBlendWithZeroTintDrawsNothing()
    {
        var c = new RgbaCanvas(1, 1);
        c.Clear(100, 100, 100, 255);
        c.Draw(Sprite(1, 1, 200, 0, 0, 255), 0, 0, false, SpriteBlend.Normal, 0);
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, c.Pixels);
    }

    [Fact]
    public void NormalBlendTintStaysBetweenTheBackgroundAndTheSourceAndIsMonotonic()
    {
        // 染色 alpha 从 0 升到 255,R 通道应当单调地从背景值走到源值(门上的铁栅栏淡出就是靠这个)
        int previous = 100;
        for (int tint = 1; tint <= 255; tint++)
        {
            var c = new RgbaCanvas(1, 1);
            c.Clear(100, 100, 100, 255);
            c.Draw(Sprite(1, 1, 220, 100, 100, 255), 0, 0, false, SpriteBlend.Normal, tint);
            int r = c.Pixels[0];
            Assert.InRange(r, previous, 220);
            previous = r;
        }
        Assert.Equal(220, previous);
    }
}

public class TownProjectionTests
{
    [Fact]
    public void ReproducesTheMailboxPlacementFromE024()
    {
        // 世界 (691,163,0),.ani 偏移 (−45,−55),IMG 帧头 (1,1) → 屏幕左上角 (623,309)
        TownProjection.FromE024.TopLeft(691, 163, 0, -45, -55, 1, 1, out int x, out int y);
        Assert.Equal((623, 309), (x, y));
    }

    [Fact]
    public void ReproducesTheSeriaPlacementFromE024()
    {
        // 世界 (477,160,0),偏移 (−51,−162),帧头 (23,55) → (425,253)
        TownProjection.FromE024.TopLeft(477, 160, 0, -51, -162, 23, 55, out int x, out int y);
        Assert.Equal((425, 253), (x, y));
    }

    [Fact]
    public void MirroringUsesTheWorldXAsTheAxis()
    {
        TownProjection p = TownProjection.FromE024;
        // 不镜像:左边缘 = 100 - 24 + (-10) + 2 = 68,宽 8,所以占 [68, 76)
        p.TopLeft(100, 50, 0, -10, -20, 2, 3, 8, false, out int x0, out int y0);
        Assert.Equal((68, 50 + 200 - 20 + 3), (x0, y0));
        // 镜像:轴在 100 - 24 = 76,占 [68, 76) 的镜像是 [76, 84)
        p.TopLeft(100, 50, 0, -10, -20, 2, 3, 8, true, out int x1, out int y1);
        Assert.Equal((76, y0), (x1, y1));
        // 镜像两次回到原处:用"镜像后的左边缘"再镜像一次(此时宽度不变,偏移取反)
        Assert.Equal(x0, 2 * 76 - x1 - 8);
    }

    [Fact]
    public void HeightRaisesTheSpriteOnScreen()
    {
        var p = new TownProjection(0, 200);
        p.TopLeft(10, 100, 0, 0, 0, 0, 0, out _, out int y0);
        p.TopLeft(10, 100, 30, 0, 0, 0, 0, out _, out int y30);
        Assert.Equal(30, y0 - y30);
    }
}

public class TownSceneTests
{
    [Fact]
    public void BuildsAndRendersTheMailboxAtTheModelPosition()
    {
        using var fx = new SceneFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        List<TownNpc> npcs = TownScene.ReadMapNpcs(assets, "map/test/town.map");
        Assert.Equal(new[] { 32, 99 }, npcs.Select(n => n.Id).ToArray());
        Assert.Equal((691, 163, 0), (npcs[0].X, npcs[0].Y, npcs[0].Z));

        Dictionary<int, string> catalog = TownScene.ReadNpcCatalog(assets);
        Assert.Equal("postbox.npc", catalog[32]);
        Assert.Equal("npc/animation/postbox.ani", TownScene.FieldAnimationPath(assets, "npc/postbox.npc"));

        List<SceneSprite> sprites = TownScene.Build(assets, npcs, TownProjection.FromE024, 0);
        Assert.Single(sprites);                    // NPC 99 的脚本不存在,被跳过
        SceneSprite s = sprites[0];
        Assert.Equal((623, 309), (s.ScreenX, s.ScreenY));
        Assert.Equal(0, s.AniFrame);

        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 50, 60, 255);
        TownScene.Render(canvas, sprites);
        byte[] P(int x, int y) => canvas.Pixels.Skip((y * 800 + x) * 4).Take(4).ToArray();
        // 第 0 个像素是 (10,20,30,128),背景 (40,50,60):
        // R = (10*128 + 40*127 + 127)/255 = 25;G = (20*128 + 50*127 + 127)/255 = 35;B = (30*128 + 60*127 + 127)/255 = 45
        Assert.Equal(new byte[] { 25, 35, 45, 255 }, P(623, 309));
    }

    [Fact]
    public void ReadsTheRealMapNpcFormatWithFacingStringsAndTheLegacyFourNumberForm()
    {
        using var fx = new SceneFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);

        List<TownNpc> real = TownScene.ReadMapNpcs(assets, "map/test/facing.map");
        Assert.Equal(new[] { 32, 32, 33, 33 }, real.Select(n => n.Id).ToArray());
        Assert.Equal(new[] { false, true, false, true }, real.Select(n => n.FacingLeft).ToArray());
        Assert.All(real, n => Assert.Equal((691, 163, 0), (n.X, n.Y, n.Z)));

        List<TownNpc> legacy = TownScene.ReadMapNpcs(assets, "map/test/town.map");
        Assert.Equal(new[] { 32, 99 }, legacy.Select(n => n.Id).ToArray());
        Assert.All(legacy, n => Assert.False(n.FacingLeft));
    }

    [Fact]
    public void FacingLeftAndANegativeFrameScaleEachMirrorAndTwoMirrorsCancel()
    {
        using var fx = new SceneFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        List<TownNpc> npcs = TownScene.ReadMapNpcs(assets, "map/test/facing.map");
        List<SceneSprite> sprites = TownScene.Build(assets, npcs, TownProjection.FromE024, 0);

        // 同一个 y,按列表顺序:[右,无缩放] [左,无缩放] [右,x 缩放 -1] [左,x 缩放 -1]
        Assert.Equal(new[] { false, true, true, false }, sprites.Select(s => s.Mirrored).ToArray());
        // 不翻转的左边缘 623;翻转的 = 691 - 24 - (-45 + 1 + 4) = 707
        Assert.Equal(new[] { 623, 707, 707, 623 }, sprites.Select(s => s.ScreenX).ToArray());

        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 50, 60, 255);
        TownScene.Render(canvas, new[] { sprites[1] });             // 只画那个翻转的
        byte[] P(int x, int y) => canvas.Pixels.Skip((y * 800 + x) * 4).Take(4).ToArray();
        // 第 0 行原来是 [半透明, 透明, (200,100,50), (1,2,3)],翻转后是 [(1,2,3), (200,100,50), 透明, 半透明]
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, P(707, 309));
        Assert.Equal(new byte[] { 200, 100, 50, 255 }, P(708, 309));
        Assert.Equal(new byte[] { 40, 50, 60, 255 }, P(709, 309));
        Assert.Equal(new byte[] { 25, 35, 45, 255 }, P(710, 309));
    }

    [Fact]
    public void OpaquePixelsLandExactlyAndTransparentOnesLeaveTheBackground()
    {
        using var fx = new SceneFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var npcs = new List<TownNpc> { new TownNpc(32, 691, 163, 0) };
        List<SceneSprite> sprites = TownScene.Build(assets, npcs, TownProjection.FromE024, 0);
        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 50, 60, 255);
        TownScene.Render(canvas, sprites);
        byte[] P(int x, int y) => canvas.Pixels.Skip((y * 800 + x) * 4).Take(4).ToArray();
        Assert.Equal(new byte[] { 40, 50, 60, 255 }, P(624, 309));     // 第 1 个像素全透明
        Assert.Equal(new byte[] { 200, 100, 50, 255 }, P(625, 309));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, P(623, 310));
        Assert.Equal(new byte[] { 20, 21, 22, 255 }, P(626, 311));
        Assert.Equal(new byte[] { 40, 50, 60, 255 }, P(627, 309));     // 精灵右边一格:背景
        Assert.Equal(new byte[] { 40, 50, 60, 255 }, P(623, 312));     // 精灵下面一格:背景
    }

    [Fact]
    public void SameYSpritesKeepTheListOrderAndSmallerYIsDrawnFirst()
    {
        using var fx = new SceneFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var npcs = new List<TownNpc> { new TownNpc(32, 100, 300, 0), new TownNpc(32, 200, 100, 0), new TownNpc(32, 300, 100, 0) };
        List<SceneSprite> sprites = TownScene.Build(assets, npcs, TownProjection.FromE024, 0);
        Assert.Equal(new[] { 200 - 24 - 45 + 1, 300 - 24 - 45 + 1, 100 - 24 - 45 + 1 }, sprites.Select(s => s.ScreenX).ToArray());
    }

    [Fact]
    public void AssetsReportMissingThingsInsteadOfThrowing()
    {
        using var fx = new SceneFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        Assert.Null(assets.ReadScript("npc/nope.npc"));
        Assert.Null(assets.LoadAni("npc/animation/nope.ani"));
        Assert.False(assets.TryGetFrame("sprite/map/nope.img", 0, out _));
        Assert.False(assets.TryGetFrame("sprite/map/postbox.img", 7, out _));   // 帧号越界
        Assert.False(assets.TryGetFrame("sprite/map/postbox.img", -1, out _));
        Assert.True(assets.TryGetFrame("SPRITE/Map/PostBox.IMG", 0, out SpriteFrame f));   // 名字不区分大小写
        Assert.Equal((4, 3, 1, 1, 6, 5), (f.Width, f.Height, f.OffsetX, f.OffsetY, f.CanvasWidth, f.CanvasHeight));
    }

    [Fact]
    public void DuplicateImageNamesTakeTheLastNpkInFileNameOrder()
    {
        using var fx = new SceneFixture(twoNpks: true);
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        Assert.Equal(1, assets.Images.DuplicateNames);
        Assert.Equal(1, assets.Images.ImageCount);
        Assert.True(assets.TryGetFrame("sprite/map/postbox.img", 0, out SpriteFrame f));
        Assert.Equal(255, f.Rgba[8]);            // 第 2 个像素 R 通道:第一个 NPK 里是 200,第二个 NPK 里加 100 后封顶 255;取到 255 说明用的是后者
    }

    [Fact]
    public void ReadsMapAnimationGroupsOfFiveValues()
    {
        using var fx = new SceneFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        List<MapAnimation> anims = MapScript.ReadAnimations(assets, "map/test/layers.map");
        Assert.Equal(3, anims.Count);
        // 路径相对地图所在目录解析并规范成小写
        Assert.Equal(new[] { "map/test/animation/glow.ani", "map/test/animation/back.ani", "map/test/animation/back.ani" },
            anims.Select(a => a.AnimationPath).ToArray());
        Assert.Equal("Animation/Glow.ani", anims[0].RawPath);
        Assert.Equal(new[] { "[close]", "[closeback]", "[normal]" }, anims.Select(a => a.Layer).ToArray());
        Assert.Equal((5, 6, 7), (anims[0].X, anims[0].Y, anims[0].Z));
        Assert.Equal((100, 50, 0), (anims[2].X, anims[2].Y, anims[2].Z));

        // 没有 [animation] 的地图返回空列表;不存在的地图抛 DnfFormatException
        Assert.Empty(MapScript.ReadAnimations(assets, "map/test/town.map"));
        Assert.Throws<DnfFormatException>(() => MapScript.ReadAnimations(assets, "map/test/nope.map"));
    }

    [Fact]
    public void LayerNamesMapToTheSixKnownRanksAndUnknownOnesAreNormal()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, MapScript.KnownLayers.Select(TownScene.LayerRank).ToArray());
        Assert.Equal(TownScene.NormalRank, TownScene.LayerRank("[something else]"));
        Assert.Equal(TownScene.NormalRank, TownScene.LayerRank(string.Empty));
    }

    [Fact]
    public void BuildSceneOrdersByLayerThenYAndDrawsGlowAdditivelyWithInterpolatedTint()
    {
        using var fx = new SceneFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        // 书写顺序:光效 [close]、背景 [closeback]、背景 [normal](y = 50)、NPC(y = 163)
        List<SceneSprite> sprites = TownScene.BuildScene(assets, "map/test/layers.map", TownProjection.FromE024, 545);

        Assert.Equal(new[] { 2, 4, 4, 5 }, sprites.Select(s => s.LayerRank).ToArray());
        Assert.Equal(new[] { "map/test/animation/back.ani", "map/test/animation/back.ani", "npc/animation/postbox.ani", "map/test/animation/glow.ani" },
            sprites.Select(s => s.AnimationPath).ToArray());
        // normal 大类里:背景(y = 50)在 NPC(y = 163)前面
        Assert.Equal(new[] { -1, -1, 32, -1 }, sprites.Select(s => s.NpcId).ToArray());

        // 位置模型与 NPC 相同:x = 世界 x − 24 + ani x + 帧头 x;y = 世界 y − z + 200 + ani y + 帧头 y
        Assert.Equal((-23, 196), (sprites[0].ScreenX, sprites[0].ScreenY));      // 背景 (0,0,0),ani (0,−5),帧头 (1,1)
        Assert.Equal((77, 246), (sprites[1].ScreenX, sprites[1].ScreenY));       // 背景 (100,50,0)
        Assert.Equal((-28, 180), (sprites[3].ScreenX, sprites[3].ScreenY));      // 光效 (5,6,7),ani (−10,−20)

        // 光效:加法混合,545 ms 处染色 alpha 139;背景与 NPC 是普通混合
        Assert.Equal(new[] { SpriteBlend.Normal, SpriteBlend.Normal, SpriteBlend.Normal, SpriteBlend.Additive }, sprites.Select(s => s.Blend).ToArray());
        Assert.Equal(139, sprites[3].TintAlpha);
        Assert.Equal(0, sprites[3].AniFrame);
    }

    [Fact]
    public void RenderingTheSceneAddsTheGlowOnTopOfEverythingElse()
    {
        using var fx = new SceneFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        // 相机放到 −100:光效的像素块左上角 (5 + 100 − 10 + 1, 6 − 7 + 200 − 20 + 1) = (96, 180)
        var projection = new TownProjection(-100, 200);
        List<SceneSprite> sprites = TownScene.BuildScene(assets, "map/test/layers.map", projection, 1000);   // 1000 ms:帧 1,染色 alpha 255
        SceneSprite glow = sprites.Single(s => s.Blend == SpriteBlend.Additive);
        Assert.Equal((96, 180), (glow.ScreenX, glow.ScreenY));
        Assert.Equal(255, glow.TintAlpha);

        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 50, 60, 255);
        TownScene.Render(canvas, sprites);
        byte[] P(int x, int y) => canvas.Pixels.Skip((y * 800 + x) * 4).Take(4).ToArray();
        // 两个背景的像素块分别在 (101, 196) 与 (201, 246) 起,都与光效的像素块 (96..99, 180..182) 不重叠,所以光效下面是清屏色 (40,50,60):
        // 第 2 个像素 (200,100,50,255) 加上它,alpha 255、染色 255:→ (240,150,110)
        Assert.Equal(new byte[] { 240, 150, 110, 255 }, P(98, 180));
        // 第 1 个像素全透明:不变
        Assert.Equal(new byte[] { 40, 50, 60, 255 }, P(97, 180));
        // 第 3 个像素 (1,2,3):加法 → (41,52,63)
        Assert.Equal(new byte[] { 41, 52, 63, 255 }, P(99, 180));
    }
}
