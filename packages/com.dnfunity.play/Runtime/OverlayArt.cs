using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 战斗画面上的文字与血条的像素。字库是自带的 5×7 点阵(我们自己画的,不是原版的字体、数字图与界面),
    /// 血条也是纯色块;这样叠加层和别的精灵一样走同一条绘制管线(CPU 合成与 GPU 逐字节一致),不依赖任何原版素材。
    /// 帧按内容缓存,所以每一帧反复取同一个文字或血条拿到的是同一个 <see cref="SpriteFrame"/> 对象(GPU 端按对象缓存纹理)。
    /// </summary>
    public sealed class OverlayArt
    {
        /// <summary>叠加层的图层大类:排在所有地图图层之后(<see cref="TownScene.SortByDepth"/> 按大类升序画,同大类保持列表顺序)。</summary>
        public const int UiRank = 6;

        public const int GlyphWidth = 5;
        public const int GlyphHeight = 7;

        public const uint Yellow = 0xFFE23Cu;
        public const uint White = 0xF4F4F4u;
        public const uint Orange = 0xFF9A2Eu;
        public const uint Black = 0x000000u;

        private static readonly Dictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
        {
            ['0'] = new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." },
            ['1'] = new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
            ['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
            ['3'] = new[] { "####.", "....#", "....#", ".###.", "....#", "....#", "####." },
            ['4'] = new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." },
            ['5'] = new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." },
            ['6'] = new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." },
            ['7'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." },
            ['8'] = new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." },
            ['9'] = new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." },
            ['L'] = new[] { "#....", "#....", "#....", "#....", "#....", "#....", "#####" },
            ['v'] = new[] { ".....", ".....", "#...#", "#...#", ".#.#.", ".#.#.", "..#.." },
            ['H'] = new[] { "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
            ['I'] = new[] { ".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###." },
            ['T'] = new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." },
            ['x'] = new[] { ".....", ".....", "#...#", ".#.#.", "..#..", ".#.#.", "#...#" },
            ['.'] = new[] { ".....", ".....", ".....", ".....", ".....", ".##..", ".##.." },
            ['!'] = new[] { "..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#.." },
            ['/'] = new[] { "....#", "....#", "...#.", "..#..", ".#...", "#....", "#...." },
            [' '] = new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        };

        private readonly Dictionary<string, SpriteFrame> _texts = new Dictionary<string, SpriteFrame>(StringComparer.Ordinal);
        private readonly Dictionary<long, SpriteFrame> _bars = new Dictionary<long, SpriteFrame>();

        /// <summary>字库里有没有这个字符(没有的字符画成空格)。</summary>
        public static bool HasGlyph(char c)
        {
            return Glyphs.ContainsKey(c);
        }

        /// <summary>文字的像素宽(含描边):每个字符 5 格,字间 1 格,各按 <paramref name="scale"/> 放大,左右各加 1 像素描边。</summary>
        public static int TextWidth(string text, int scale)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            return (text.Length * (GlyphWidth + 1) - 1) * scale + 2;
        }

        public static int TextHeight(int scale)
        {
            return GlyphHeight * scale + 2;
        }

        /// <summary>一段文字(<paramref name="fill"/> 填色,1 像素黑色描边);空串返回 null。同样的参数总是返回同一个对象。</summary>
        public SpriteFrame Text(string text, int scale, uint fill = Yellow, uint outline = Black)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            if (scale < 1)
            {
                scale = 1;
            }
            string key = scale + "|" + fill + "|" + outline + "|" + text;
            if (_texts.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            int w = TextWidth(text, scale);
            int h = TextHeight(scale);
            var rgba = new byte[w * h * 4];
            // 先画描边(每个点向八个方向各扩一格),再画填色
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    if (!Glyphs.TryGetValue(text[i], out string[] rows))
                    {
                        rows = Glyphs[' '];
                    }
                    int ox = 1 + i * (GlyphWidth + 1) * scale;
                    for (int gy = 0; gy < GlyphHeight; gy++)
                    {
                        for (int gx = 0; gx < GlyphWidth; gx++)
                        {
                            if (rows[gy][gx] != '#')
                            {
                                continue;
                            }
                            for (int dy = 0; dy < scale; dy++)
                            {
                                for (int dx = 0; dx < scale; dx++)
                                {
                                    int px = ox + gx * scale + dx;
                                    int py = 1 + gy * scale + dy;
                                    if (pass == 0)
                                    {
                                        for (int oy = -1; oy <= 1; oy++)
                                        {
                                            for (int oxx = -1; oxx <= 1; oxx++)
                                            {
                                                Put(rgba, w, h, px + oxx, py + oy, outline);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        Put(rgba, w, h, px, py, fill);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            var frame = new SpriteFrame(w, h, 0, 0, w, h, rgba);
            _texts[key] = frame;
            return frame;
        }

        /// <summary>
        /// 血条:外框 1 像素深灰、底色暗红、左边 <paramref name="filled"/> 个像素填亮红(<paramref name="width"/> 与 <paramref name="height"/> 含外框)。
        /// 填充像素数夹在 [0, 内宽]。
        /// </summary>
        public SpriteFrame Bar(int width, int height, int filled)
        {
            if (width < 3 || height < 3)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }
            int inner = width - 2;
            filled = filled < 0 ? 0 : (filled > inner ? inner : filled);
            long key = ((long)width << 40) | ((long)height << 20) | (uint)filled;
            if (_bars.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    uint color;
                    if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                    {
                        color = 0x181818u;
                    }
                    else if (x - 1 < filled)
                    {
                        // 上半亮、下半稍暗,做一点立体感
                        color = y - 1 < (height - 2 + 1) / 2 ? 0xE23030u : 0xB01818u;
                    }
                    else
                    {
                        color = 0x3A0808u;
                    }
                    Put(rgba, width, height, x, y, color);
                }
            }
            var frame = new SpriteFrame(width, height, 0, 0, width, height, rgba);
            _bars[key] = frame;
            return frame;
        }

        /// <summary>叠加层精灵:屏幕左上角为 (<paramref name="x"/>, <paramref name="y"/>),大类 <see cref="UiRank"/>。</summary>
        public static SceneSprite Place(SpriteFrame frame, int x, int y, int order = 0, string tag = "overlay")
        {
            return new SceneSprite(frame, x, y, order, -3, tag, 0, false, SpriteBlend.Normal, 255, UiRank);
        }

        private static void Put(byte[] rgba, int w, int h, int x, int y, uint rgb)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h)
            {
                return;
            }
            int o = (y * w + x) * 4;
            rgba[o] = (byte)((rgb >> 16) & 0xFF);
            rgba[o + 1] = (byte)((rgb >> 8) & 0xFF);
            rgba[o + 2] = (byte)(rgb & 0xFF);
            rgba[o + 3] = 255;
        }
    }
}
