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
        SpriteFrame blank = art.Text("A", 1);
        Assert.False(OverlayArt.HasGlyph('A'));
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
