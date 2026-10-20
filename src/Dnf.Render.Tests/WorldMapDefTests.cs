using Dnf.Data;

namespace Dnf.Render.Tests;

/// <summary>
/// 世界地图定义(.wdm)、界面脚本(.ui)与副本卡片信息(.dgn 的 [worldmap pattern info])的读法,以及精灵缩放(证据 E-094)。
/// 数据全是合成的(标签是格式本身的名字,数值是随便编的),不含任何原版内容。
/// </summary>
public class WorldMapDefTests
{
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
    public void AWorldMapReadsItsBackgroundUiScriptAndDungeonPairs()
    {
        WorldMapDef def = WorldMapDef.Parse("worldmap/x.wdm", new[]
        {
            Line("[map image]", "World\\Foo.img", 3),
            Line("[ui path]", "WorldMap/UI/Foo.ui"),
            Line("[dungeon]", 11, -1, 12, 700, 13, -1),
            Line("[name]", "ignored"),
        })!;
        Assert.Equal("sprite/world/foo.img", def.MapImage);       // 加 sprite/、小写、正斜杠
        Assert.Equal(3, def.MapFrame);
        Assert.Equal("worldmap/ui/foo.ui", def.UiPath);
        Assert.Equal(3, def.Dungeons.Count);
        Assert.Equal((11, -1, true), (def.Dungeons[0].DungeonId, def.Dungeons[0].Condition, def.Dungeons[0].Unconditional));
        Assert.Equal((12, 700, false), (def.Dungeons[1].DungeonId, def.Dungeons[1].Condition, def.Dungeons[1].Unconditional));
        Assert.Equal(13, def.Dungeons[2].DungeonId);
    }

    [Fact]
    public void ATrailingUnpairedDungeonNumberIsDroppedAndAnUnrelatedScriptIsNotAWorldMap()
    {
        WorldMapDef def = WorldMapDef.Parse("w", new[] { Line("[dungeon]", 5, -1, 6) })!;
        Assert.Single(def.Dungeons);
        Assert.Null(def.MapImage);
        Assert.Null(def.UiPath);
        Assert.Null(WorldMapDef.Parse("w", new[] { Line("[something else]", 1) }));
        Assert.Null(WorldMapDef.Parse("w", null!));
    }

    [Fact]
    public void ANumberedListMapsEachNumberToItsPathUnderTheListDirectoryLowercased()
    {
        Dictionary<int, string> list = WorldMapDef.ParseList(new[]
        {
            new ScriptLine(null, new[]
            {
                new ScriptValue(ScriptValueKind.Int, 1, null), new ScriptValue(ScriptValueKind.String, 0, "Act1\\Alpha.dgn"),
                new ScriptValue(ScriptValueKind.Int, 2, null), new ScriptValue(ScriptValueKind.String, 0, "Beta.dgn"),
                new ScriptValue(ScriptValueKind.Int, 1, null), new ScriptValue(ScriptValueKind.String, 0, "Duplicate.dgn"),
            }),
        }, "dungeon/");
        Assert.Equal(2, list.Count);
        Assert.Equal("dungeon/act1/alpha.dgn", list[1]);          // 重复的编号取第一个
        Assert.Equal("dungeon/beta.dgn", list[2]);
        Assert.Empty(WorldMapDef.ParseList(null!, "x/"));
    }

    [Fact]
    public void AUiControlHasTypeNameOriginVisibilityImageAndTheRestOfTheValues()
    {
        UiScript ui = UiScript.Parse(new[]
        {
            Line("[ui controls]", "[Balloon]", "IDC_A", 100, 200, 1, "World/Ui.img", 3, 1, 7, 55),
            Line("[common action]", "Action/x.act"),
            Line("[/ui controls]"),
            Line("[ui controls]", "[image]", "IDC_B", -4, 5, 0, "World/Bg.img", 1, 0),
            Line("[ui controls]", "[text]", "IDC_C", 23, 556, 0, 9, 36187),         // 没有图名
            Line("[ui controls]", "[broken]"),                                         // 缺名字:丢掉
        })!;
        Assert.Equal(3, ui.Controls.Count);

        UiControl a = ui.Find("IDC_A")!;
        Assert.Equal(("[balloon]", 100, 200, true, "World/Ui.img"), (a.Type, a.X, a.Y, a.Visible, a.Image));
        Assert.Equal(new[] { 3, 1, 7, 55 }, a.Params());

        UiControl b = ui.Find("IDC_B")!;
        Assert.Equal((-4, 5, false), (b.X, b.Y, b.Visible));
        Assert.Equal(new[] { 1, 0 }, b.Params());

        UiControl c = ui.Find("IDC_C")!;
        Assert.Null(c.Image);
        Assert.Equal(new[] { 9, 36187 }, c.Params());
        Assert.Null(ui.Find("IDC_NONE"));
        Assert.Null(UiScript.Parse(new[] { Line("[other]", 1) }));
    }

    [Fact]
    public void ADungeonCardReadsTheSlotArtThePatternPositionAndTheLevels()
    {
        DungeonCardDef card = DungeonCardDef.Parse("dungeon/a.dgn", new[]
        {
            Line("[name]", "ignored"),
            Line("[minimum required level]", 17),
            Line("[basis level]", 21),
            Line("[worldmap pattern info]", 2, 3, "WorldMap\\Slots/Foo.img", 4),
        })!;
        Assert.Equal("sprite/worldmap/slots/foo.img", card.SlotImage);
        Assert.Equal((4, 2, 3), (card.SlotFrame, card.PatternGroup, card.PatternSlot));
        Assert.Equal((17, 21), (card.MinimumLevel, card.BasisLevel));
    }

    [Fact]
    public void ADungeonWithoutPatternInfoHasNoSlotArtAndZeroLevels()
    {
        DungeonCardDef card = DungeonCardDef.Parse("dungeon/a.dgn", new[] { Line("[name]", "x") })!;
        Assert.Null(card.SlotImage);
        Assert.Equal((0, 0, 0), (card.SlotFrame, card.MinimumLevel, card.BasisLevel));
        Assert.Null(DungeonCardDef.Parse("d", null!));
    }

    // ------------------------------------------------------------------ 缩放

    private static SpriteFrame Row(params byte[] values)
    {
        // 一行灰度像素(R=G=B=值,A=255)
        var rgba = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = values[i];
            rgba[i * 4 + 3] = 255;
        }
        return new SpriteFrame(values.Length, 1, 0, 0, values.Length, 1, rgba);
    }

    private static byte[] Reds(SpriteFrame frame)
    {
        var list = new List<byte>();
        for (int i = 0; i < frame.Width * frame.Height; i++)
        {
            list.Add(frame.Rgba[i * 4]);
        }
        return list.ToArray();
    }

    [Fact]
    public void ScalingByOneReturnsTheSameFrameAndABadScaleIsRejected()
    {
        SpriteFrame f = Row(1, 2, 3);
        Assert.Same(f, SpriteScale.Bilinear(f, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpriteScale.Bilinear(f, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpriteScale.Bilinear(f, -1.0));
        Assert.Throws<ArgumentNullException>(() => SpriteScale.Bilinear(null!, 0.5));
    }

    [Fact]
    public void ShrinkingSamplesAtTheCentreOfEachTargetPixelAndTheSizeIsTheCeiling()
    {
        // 4 个像素缩到一半:目标像素 x 的源坐标 = (x + 0.5) / 0.5 - 0.5 = 2x + 0.5,正好落在两个源像素中间
        SpriteFrame half = SpriteScale.Bilinear(Row(0, 100, 200, 40), 0.5);
        Assert.Equal((2, 1), (half.Width, half.Height));
        Assert.Equal(new byte[] { 50, 120 }, Reds(half));

        // 5 个像素 × 0.7 = 3.5 → 宽 4
        Assert.Equal(4, SpriteScale.Bilinear(Row(1, 2, 3, 4, 5), 0.7).Width);
        // 100 × 0.7 在浮点里是 70.00000000000001,不能因此多出一个像素
        Assert.Equal(70, SpriteScale.Bilinear(Row(Enumerable.Repeat((byte)9, 100).ToArray()), 0.7).Width);
    }

    [Fact]
    public void EnlargingInterpolatesLinearlyAndClampsAtTheEdges()
    {
        // 2 个像素放大一倍:源坐标 = x / 2 - 0.25,两头夹在边缘像素上
        SpriteFrame twice = SpriteScale.Bilinear(Row(0, 200), 2.0);
        Assert.Equal((4, 2), (twice.Width, twice.Height));                        // 高度也放大(两行一样)
        Assert.Equal(new byte[] { 0, 50, 150, 200, 0, 50, 150, 200 }, Reds(twice));
    }

    [Fact]
    public void ChannelsAreInterpolatedIndependentlyWithoutPremultiplyingAlpha()
    {
        // 一个不透明红点和一个全透明蓝点(RGB 照样参与平均):正中间 R = 128、B = 128、A = 128
        var rgba = new byte[] { 255, 0, 0, 255, 0, 0, 255, 0 };
        SpriteFrame half = SpriteScale.Bilinear(new SpriteFrame(2, 1, 0, 0, 2, 1, rgba), 0.5);
        Assert.Equal((1, 1), (half.Width, half.Height));
        Assert.Equal(new byte[] { 128, 0, 128, 128 }, half.Rgba);                 // 127.5 往远离 0 的方向舍入
    }

    [Fact]
    public void TheOffsetAndCanvasScaleWithTheFrame()
    {
        var rgba = new byte[10 * 6 * 4];
        var frame = new SpriteFrame(10, 6, 5, 3, 30, 20, rgba);
        SpriteFrame small = SpriteScale.Bilinear(frame, 0.5);
        Assert.Equal((5, 3), (small.Width, small.Height));
        Assert.Equal((3, 2), (small.OffsetX, small.OffsetY));                     // 2.5 → 3、1.5 → 2(远离 0 舍入)
        Assert.Equal((15, 10), (small.CanvasWidth, small.CanvasHeight));
    }

    [Fact]
    public void TheCacheReturnsTheSameScaledObjectForTheSameFrameAndScale()
    {
        var cache = new ScaledSpriteCache();
        SpriteFrame f = Row(0, 100, 200, 40);
        SpriteFrame a = cache.Get(f, 0.5);
        Assert.Same(a, cache.Get(f, 0.5));
        Assert.NotSame(a, cache.Get(f, 0.25));
        Assert.Same(f, cache.Get(f, 1.0));
        Assert.Null(cache.Get(null!, 0.5));
        Assert.NotSame(a, cache.Get(Row(0, 100, 200, 40), 0.5));                  // 不同的帧对象各算各的
    }
}
