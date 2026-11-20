using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 区域 C 界面测试共用夹具(从 OnlineDungeonSelectTests 里的合成世界地图与"行号编码"色块复制而来,既有测试文件原样不动;
/// 只给 OnlineDungeonScreenGap* 系列测试用)。另带一套"随字号变化的合成字体"(<see cref="ProbeFonts"/>),
/// 让精灵的高度能读出请求的字号、宽度能读出文字内容,这样字号与限宽这类常量一旦被改动,帧的尺寸就会跟着变,测试量得出来。
/// </summary>
internal static class OnlineDungeonScreenGapKit
{
    public const string MapImage = "sprite/test/map.img";
    public const string SlotImage = "sprite/test/slots.img";

    internal sealed class FakeSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) => Frames.TryGetValue((imageName, frame), out sprite!);
    }

    /// <summary>像素 (x, y) = (R=y%256, G=x%256, B=tag, A=255)。</summary>
    public static SpriteFrame Coded(int width, int height, byte tag, int offsetX = 0, int offsetY = 0)
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

    /// <summary>界面用到的全部帧(尺寸与偏移同量过的界面图,内容是合成的)。</summary>
    public static WorldMapScene SampleWorldScene()
    {
        var f = new FakeSprites();
        f.Frames[(MapImage, 0)] = Coded(800, 600, 1);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.CardBorderFrame)] = Coded(174, 79, 10);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.BubbleFrameBase + WorldMapArt.MeasuredBubbleStyle)] = Coded(118, 80, 11);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.ProperChannelBannerFrame)] = Coded(553, 28, 12, 124, 0);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.KeyGuideBannerFrame)] = Coded(553, 28, 13, 86, 0);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.MarkerFrame)] = Coded(34, 34, 14, 1, 1);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.TopBottomBannerFrame)] = Coded(800, 600, 15);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.MapSelectBannerFrame)] = Coded(800, 72, 16);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.DifficultyBarFrame)] = Coded(181, 21, 17, 13, 0);
        f.Frames[(WorldMapArt.UiImage, WorldMapArt.BadgeFrame)] = Coded(35, 39, 18);
        f.Frames[(WorldMapArt.ButtonImage, WorldMapArt.ButtonNormalFrame)] = Coded(97, 23, 19);
        f.Frames[(WorldMapArt.ButtonImage, WorldMapArt.ButtonDisabledFrame)] = Coded(97, 23, 20);
        f.Frames[(WorldMapArt.PartyImage, WorldMapArt.PartyIconFrame)] = Coded(15, 15, 21);
        f.Frames[(SlotImage, 0)] = Coded(168, 73, 30);
        f.Frames[(SlotImage, 1)] = Coded(168, 73, 31);
        return new WorldMapScene(f, MapImage, 0, "LORIEN");
    }

    /// <summary>有卡片图的条目(世界地图上画图包里的卡片)。</summary>
    public static DungeonEntry CardEntry(string name, int id, int x, int y, int slot = 0, int level = 5)
    {
        return new DungeonEntry(
            "dungeon/test/" + name + ".dgn",
            name.ToUpperInvariant(),
            new DungeonCard(id, x, y, WorldMapArt.MeasuredBubbleStyle, SlotImage, slot, level));
    }

    /// <summary>取不到卡片图的条目:世界地图上画占位卡片(纯色块 + 像素字副本名,标签 map-card-name)。</summary>
    public static DungeonEntry FallbackCardEntry(string name, int id, int x, int y, int level = 5)
    {
        return new DungeonEntry(
            "dungeon/test/" + name + ".dgn",
            name.ToUpperInvariant(),
            new DungeonCard(id, x, y, WorldMapArt.MeasuredBubbleStyle, null, 0, level));
    }

    /// <summary>没有卡片的普通条目(占位列表画面用)。</summary>
    public static DungeonEntry PlainEntry(string name)
    {
        return new DungeonEntry("dungeon/test/" + name.ToLowerInvariant() + ".dgn", name);
    }

    /// <summary>
    /// 随字号变化的合成字体集:字号 px 的字体 PixelSize = px、Ascent = px、Descent = 0、LineHeight = px。
    /// 窄字(非全角)是 2 × px 的实心块、前进 3;全角字(<see cref="GlyphText.IsWide"/>)是 3 × px 的实心块、前进 4;空格没有墨迹、前进 2。
    /// 用它排一行字(不折行)得到的帧:宽 = 各字前进之和 + 2(描边两边各 1),高 = px + 2。
    /// 所以从精灵的高度能读出请求的字号,从宽度能读出文字内容(与字号无关)。<see cref="Requested"/> 记下被问过的字号。
    /// </summary>
    internal sealed class ProbeFonts : IGlyphFonts
    {
        public List<int> Requested { get; } = new List<int>();

        public IGlyphSource ForSize(int pixelSize)
        {
            Requested.Add(pixelSize);
            return new ProbeFont(pixelSize);
        }
    }

    internal sealed class ProbeFont : IGlyphSource
    {
        private readonly int _px;

        public ProbeFont(int px)
        {
            _px = px;
        }

        public int PixelSize => _px;

        public int Ascent => _px;

        public int Descent => 0;

        public int LineHeight => _px;

        public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
        {
            if (codePoint == ' ')
            {
                glyph = GlyphBitmap.Blank(2);
                return true;
            }
            bool wide = GlyphText.IsWide(codePoint);
            int w = wide ? 3 : 2;
            var alpha = new byte[w * _px];
            Array.Fill(alpha, (byte)255);
            glyph = new GlyphBitmap(w, _px, 0, _px, wide ? 4 : 3, alpha);
            return true;
        }
    }

    /// <summary>帧里所有完全不透明像素的 0xRRGGBB 颜色集合(用来看一段文字的填色、一块纯色的底色)。</summary>
    public static HashSet<uint> OpaqueColors(SpriteFrame frame)
    {
        var colors = new HashSet<uint>();
        for (int i = 0; i < frame.Width * frame.Height; i++)
        {
            if (frame.Rgba[i * 4 + 3] == 255)
            {
                colors.Add(((uint)frame.Rgba[i * 4] << 16) | ((uint)frame.Rgba[i * 4 + 1] << 8) | frame.Rgba[i * 4 + 2]);
            }
        }
        return colors;
    }

    /// <summary>精灵列表里按标签挑出的子列表(保持原来的先后)。</summary>
    public static List<SceneSprite> Tagged(IEnumerable<SceneSprite> sprites, string tag)
    {
        return sprites.Where(s => s.AnimationPath == tag).ToList();
    }

    public static string[] Tags(IEnumerable<SceneSprite> sprites)
    {
        return sprites.Select(s => s.AnimationPath).ToArray();
    }
}
