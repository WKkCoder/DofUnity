using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>叠加层的像素(自带 5×7 点阵字库与纯色血条)与占位伤害(证据 E-069)。不依赖任何游戏数据。</summary>
public class PlayArtTests
{
    [Fact]
    public void TextSizeAndCachingAreDeterministic()
    {
        Assert.Equal((5 * 1 + 2), OverlayArt.TextWidth("1", 1));
        Assert.Equal((2 * 6 - 1) * 3 + 2, OverlayArt.TextWidth("12", 3));      // 35
        Assert.Equal(7 * 3 + 2, OverlayArt.TextHeight(3));                      // 23
        Assert.Equal(0, OverlayArt.TextWidth("", 3));
        Assert.Equal(0, OverlayArt.TextWidth(null, 3));

        var art = new OverlayArt();
        Assert.Null(art.Text("", 3));
        Assert.Null(art.Text(null, 3));
        SpriteFrame a = art.Text("12", 3);
        Assert.Same(a, art.Text("12", 3));                                      // 同样的参数拿到同一个对象(GPU 端按对象缓存纹理)
        Assert.NotSame(a, art.Text("12", 3, OverlayArt.White));
        Assert.NotSame(a, art.Text("12", 2));
        Assert.NotSame(a, art.Text("21", 3));
        Assert.Same(art.Text("1", 1), art.Text("1", 0));                        // 比例小于 1 按 1
        Assert.Equal((35, 23), (a.Width, a.Height));

        // 另一个实例画出同样的像素
        Assert.Equal(a.Rgba, new OverlayArt().Text("12", 3).Rgba);
    }

    [Fact]
    public void GlyphPixelsAreFilledWithAOneAndAHalfPixelBlackOutlineAroundThem()
    {
        var art = new OverlayArt();
        SpriteFrame one = art.Text("1", 1, OverlayArt.White, OverlayArt.Black);
        Assert.Equal((7, 9), (one.Width, one.Height));
        int At(int x, int y) => (y * one.Width + x) * 4;
        // '1' 的第一行是 "..#..":填色像素在 (1 + 2, 1 + 0)
        Assert.Equal(new byte[] { 0xF4, 0xF4, 0xF4, 255 }, one.Rgba.Skip(At(3, 1)).Take(4).ToArray());
        // 它左边一格是描边(黑色不透明);画布角落没有任何东西(透明)
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, one.Rgba.Skip(At(2, 1)).Take(4).ToArray());
        Assert.Equal(0, one.Rgba[At(0, 0) + 3]);
        Assert.Equal(0, one.Rgba[At(6, 8) + 3]);

        // 字库里没有的字符画成空格:整张都是透明的
        SpriteFrame blank = art.Text("Z", 1);
        Assert.False(OverlayArt.HasGlyph('Z'));
        Assert.True(OverlayArt.HasGlyph('7'));
        Assert.All(Enumerable.Range(0, blank.Width * blank.Height), i => Assert.Equal(0, blank.Rgba[i * 4 + 3]));
    }

    [Fact]
    public void TheBarHasAFrameAFilledPartAndAnEmptyPartAndClampsTheFill()
    {
        var art = new OverlayArt();
        SpriteFrame bar = art.Bar(10, 5, 4);
        Assert.Equal((10, 5), (bar.Width, bar.Height));
        int At(int x, int y) => (y * bar.Width + x) * 4;
        Assert.Equal(new byte[] { 0x18, 0x18, 0x18, 255 }, bar.Rgba.Skip(At(0, 0)).Take(4).ToArray());     // 外框
        Assert.Equal(new byte[] { 0xE2, 0x30, 0x30, 255 }, bar.Rgba.Skip(At(1, 1)).Take(4).ToArray());     // 填充的上半:亮
        Assert.Equal(new byte[] { 0xB0, 0x18, 0x18, 255 }, bar.Rgba.Skip(At(1, 3)).Take(4).ToArray());     // 填充的下半:稍暗
        Assert.Equal(new byte[] { 0x3A, 0x08, 0x08, 255 }, bar.Rgba.Skip(At(5, 1)).Take(4).ToArray());     // 空的部分:暗红
        Assert.Equal(0xE2, bar.Rgba[At(4, 1)]);                                                           // 第 4 个填充像素(x = 1..4)
        Assert.Same(bar, art.Bar(10, 5, 4));

        Assert.Same(art.Bar(10, 5, 8), art.Bar(10, 5, 999));                    // 夹在 [0, 内宽]
        Assert.Same(art.Bar(10, 5, 0), art.Bar(10, 5, -3));
        Assert.Throws<ArgumentOutOfRangeException>(() => art.Bar(2, 5, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => art.Bar(10, 2, 1));
    }

    [Fact]
    public void TheBannerLettersAreInTheFontAndThePixelsFollowTheirDesign()
    {
        foreach (char c in "CLEAR BOSS ROOM")
        {
            Assert.True(OverlayArt.HasGlyph(c), "缺字符 " + c);
        }
        var art = new OverlayArt();
        SpriteFrame a = art.Text("A", 1, OverlayArt.White, OverlayArt.Black);
        Assert.Equal((7, 9), (a.Width, a.Height));
        int At(int x, int y) => (y * a.Width + x) * 4;
        // 'A' 的第一行是 ".###.":填色像素在 (1 + 1..3, 1);最底下一行是 "#...#"
        Assert.Equal(new byte[] { 0xF4, 0xF4, 0xF4, 255 }, a.Rgba.Skip(At(2, 1)).Take(4).ToArray());
        Assert.Equal(new byte[] { 0xF4, 0xF4, 0xF4, 255 }, a.Rgba.Skip(At(1 + 0, 1 + 6)).Take(4).ToArray());
        Assert.Equal(new byte[] { 0xF4, 0xF4, 0xF4, 255 }, a.Rgba.Skip(At(1 + 4, 1 + 6)).Take(4).ToArray());
        Assert.NotEqual(art.Text("R", 1).Rgba, art.Text("B", 1).Rgba);          // 相近的字母画得不一样
        Assert.NotEqual(art.Text("O", 1).Rgba, art.Text("C", 1).Rgba);
    }

    private static int PixelAt(SpriteFrame f, int x, int y)
    {
        int o = (y * f.Width + x) * 4;
        return (f.Rgba[o] << 16) | (f.Rgba[o + 1] << 8) | f.Rgba[o + 2];
    }

    [Fact]
    public void TheMiniMapDrawsVisibleCellsWithTheirStateColourAConnectorBetweenVisibleNeighboursAndABossFrame()
    {
        var art = new OverlayArt();
        // 2 列 × 2 行:(0,0) 当前、右 + 下;(1,0) 看得见(灰)、左 + 下;(0,1) 清了(绿)、右 + 上;(1,1) 看不见、首领格
        var cells = new[]
        {
            new MiniMapCell(ExitBits.Right | ExitBits.Down, MiniMapState.Current, false),
            new MiniMapCell(ExitBits.Left | ExitBits.Down, MiniMapState.Seen, false),
            new MiniMapCell(ExitBits.Right | ExitBits.Up, MiniMapState.Cleared, false),
            new MiniMapCell(ExitBits.Up | ExitBits.Left, MiniMapState.Hidden, true),
        };
        SpriteFrame map = art.MiniMap(2, 2, cells);
        Assert.Equal((OverlayArt.MiniMapWidth(2), OverlayArt.MiniMapHeight(2)), (map.Width, map.Height));
        Assert.Equal((2 * 12 + 4 + 6, 2 * 9 + 4 + 6), (map.Width, map.Height));          // 34 × 28
        Assert.Same(map, art.MiniMap(2, 2, cells));                                       // 同样的内容同一个对象

        int cellX(int cx) => OverlayArt.MiniPad + cx * (OverlayArt.MiniCellWidth + OverlayArt.MiniGap);
        int cellY(int cy) => OverlayArt.MiniPad + cy * (OverlayArt.MiniCellHeight + OverlayArt.MiniGap);
        // 色块中心
        Assert.Equal(0xFFE23C, PixelAt(map, cellX(0) + 5, cellY(0) + 4));                // 当前:黄
        Assert.Equal(0x585858, PixelAt(map, cellX(1) + 5, cellY(0) + 4));                // 看得见:灰
        Assert.Equal(0x3CA03C, PixelAt(map, cellX(0) + 5, cellY(1) + 4));                // 清了:绿
        Assert.Equal(0xC87820, PixelAt(art.MiniMap(1, 1, new[] { new MiniMapCell(0, MiniMapState.Visited, false) }), OverlayArt.MiniPad + 5, OverlayArt.MiniPad + 4));   // 进过没清:橙
        // 看不见的格子留着半透明的底
        int o = ((cellY(1) + 4) * map.Width + cellX(1) + 5) * 4;
        Assert.Equal(150, map.Rgba[o + 3]);
        Assert.Equal(0x101010, (map.Rgba[o] << 16) | (map.Rgba[o + 1] << 8) | map.Rgba[o + 2]);
        // 色块的边:普通格是深色,首领格是红框(这里首领格是看不见的,所以不画;换成看得见的再看)
        Assert.Equal(0x181818, PixelAt(map, cellX(0), cellY(0)));
        SpriteFrame boss = art.MiniMap(1, 1, new[] { new MiniMapCell(0, MiniMapState.Seen, true) });
        Assert.Equal(0xE02020, PixelAt(boss, OverlayArt.MiniPad, OverlayArt.MiniPad));
        // 连线:(0,0) 的右出口通向看得见的 (1,0):两格之间有一段灰线(中间 3 像素高);(0,0) 的下出口通向 (0,1) 也有
        Assert.Equal(0x909090, PixelAt(map, cellX(0) + OverlayArt.MiniCellWidth + 1, cellY(0) + OverlayArt.MiniCellHeight / 2));
        Assert.Equal(0x909090, PixelAt(map, cellX(0) + OverlayArt.MiniCellWidth / 2, cellY(0) + OverlayArt.MiniCellHeight + 1));
        // 通向看不见的格子的出口不画线:(1,0) 的下出口、(0,1) 的右出口都通向 (1,1)(看不见)
        Assert.NotEqual(0x909090, PixelAt(map, cellX(1) + OverlayArt.MiniCellWidth / 2, cellY(0) + OverlayArt.MiniCellHeight + 1));
        Assert.NotEqual(0x909090, PixelAt(map, cellX(0) + OverlayArt.MiniCellWidth + 1, cellY(1) + OverlayArt.MiniCellHeight / 2));

        Assert.NotSame(map, art.MiniMap(2, 2, cells.Select(c => new MiniMapCell(c.Exits, MiniMapState.Hidden, c.Boss)).ToArray()));
        Assert.Throws<ArgumentException>(() => art.MiniMap(2, 2, new[] { cells[0] }));
        Assert.Throws<ArgumentException>(() => art.MiniMap(0, 1, new MiniMapCell[0]));
        Assert.Throws<ArgumentException>(() => art.MiniMap(1, 1, null));
    }

    [Fact]
    public void OverlaySpritesSitInTheUiRankWithTheirOrderAsTheSortKey()
    {
        var art = new OverlayArt();
        SpriteFrame f = art.Text("1", 1);
        SceneSprite s = OverlayArt.Place(f, 5, 6, 3, "tag");
        Assert.Equal((5, 6, 3), (s.ScreenX, s.ScreenY, s.SortKey));
        Assert.Equal((-3, "tag", OverlayArt.UiRank), (s.NpcId, s.AnimationPath, s.LayerRank));
        Assert.Equal((SpriteBlend.Normal, 255, false), (s.Blend, s.TintAlpha, s.Mirrored));
        Assert.Same(f, s.Frame);
        Assert.True(OverlayArt.UiRank > TownScene.NormalRank);
    }

    [Fact]
    public void PlaceholderDamageScalesByTheBonusAndRoundsRandomlyAroundTheFraction()
    {
        // 25.3 × 0.85 = 21.505:只会出 21 或 22,平均 21.505
        var rng = new Random(123);
        var seen = new HashSet<int>();
        long sum = 0;
        const int n = 20000;
        for (int i = 0; i < n; i++)
        {
            int v = PlaceholderDamage.Roll(-15, rng);
            seen.Add(v);
            sum += v;
        }
        Assert.Equal(new[] { 21, 22 }, seen.OrderBy(x => x).ToArray());
        Assert.InRange((double)sum / n, 21.505 - 0.05, 21.505 + 0.05);

        // 加成 100%:50.6 → 50 或 51
        var seen2 = new HashSet<int>();
        var rng2 = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            seen2.Add(PlaceholderDamage.Roll(100, rng2));
        }
        Assert.Equal(new[] { 50, 51 }, seen2.OrderBy(x => x).ToArray());

        // 伤害至少是 1(负加成压到 0 以下也一样)
        Assert.Equal(1, PlaceholderDamage.Roll(-100, new Random(1)));
        Assert.Equal(1, PlaceholderDamage.Roll(-300, new Random(1)));

        // 同一个种子给出同一个序列
        var a = new Random(99);
        var b = new Random(99);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(PlaceholderDamage.Roll(0, a), PlaceholderDamage.Roll(0, b));
        }
    }

    // 下面两组序列由 .NET 的 System.Random(1) 算出,Unity(Mono)里的同名测试(DnfUnity.Tests.CombatRuntimeTests)钉死同样的数:
    // 占位伤害的概率进位依赖带种子的随机数序列,两个运行时必须给出同一批数,命令行与播放器的战斗画面才会逐字节一致。
    internal static readonly int[] ReferenceBonuses = { 0, -15, 40, 0, -15, 40, 0, -15, 40, 0, -15, 40, 0, -15, 40, 0, -15, 40, 0, -15, 40, 0, -15, 40 };
    internal static readonly int[] ReferenceRolls = { 26, 22, 35, 25, 21, 35, 25, 21, 36, 25, 22, 36, 25, 21, 35, 25, 22, 35, 25, 21, 35, 26, 22, 36 };

    [Fact]
    public void PlaceholderDamageFollowsTheSeededRandomSequence()
    {
        var random = new Random(1);
        var rolls = new List<int>();
        foreach (int bonus in ReferenceBonuses)
        {
            rolls.Add(PlaceholderDamage.Roll(bonus, random));
        }
        Assert.Equal(ReferenceRolls, rolls.ToArray());

        var r2 = new Random(1);
        Assert.Equal(0.24866858415709278, r2.NextDouble());
        Assert.Equal(0.11074397718102856, r2.NextDouble());
        Assert.Equal(0.46701067987224587, r2.NextDouble());
    }

    [Fact]
    public void PlaceholderDamageStaysWithinTheTwoNeighbouringIntegers()
    {
        var random = new Random(7);
        for (int i = 0; i < 500; i++)
        {
            int normal = PlaceholderDamage.Roll(0, random);                     // 25.3 → 25 或 26
            Assert.InRange(normal, 25, 26);
            int weak = PlaceholderDamage.Roll(-15, random);                     // 21.505 → 21 或 22
            Assert.InRange(weak, 21, 22);
            Assert.Equal(1, PlaceholderDamage.Roll(-100, random));              // 下限 1
        }
    }
}
