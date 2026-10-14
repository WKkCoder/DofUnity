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
    public void NoFramesGivesMinusOne()
    {
        var t = new AniTimeline(AniFile.Parse(TestData.BuildAni(new[] { "A.img" }, Array.Empty<(ushort, byte[])>(), Array.Empty<TestData.AniFrameSpec>())));
        Assert.Equal(-1, t.FrameAt(0));
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
    public void SpriteFrameRejectsAWrongBufferLength()
    {
        Assert.Throws<ArgumentException>(() => new SpriteFrame(2, 2, 0, 0, 2, 2, new byte[15]));
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
}
