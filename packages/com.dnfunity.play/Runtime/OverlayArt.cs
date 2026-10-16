using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>小地图上一个迷宫格的显示状态:看不见、只看见入口、进过但没清、清掉了、当前所在。</summary>
    public enum MiniMapState : byte
    {
        Hidden = 0,
        Seen = 1,
        Visited = 2,
        Cleared = 3,
        Current = 4,
    }

    /// <summary>小地图上的一格:出口组合(<see cref="ExitBits"/>)、显示状态、是不是首领格。</summary>
    public readonly struct MiniMapCell
    {
        public int Exits { get; }
        public MiniMapState State { get; }
        public bool Boss { get; }

        public MiniMapCell(int exits, MiniMapState state, bool boss)
        {
            Exits = exits;
            State = state;
            Boss = boss;
        }
    }

    /// <summary>
    /// 战斗画面上的文字、血条与副本小地图的像素。字库是自带的 5×7 点阵(我们自己画的,不是原版的字体、数字图与界面),
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
            ['A'] = new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
            ['B'] = new[] { "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." },
            ['C'] = new[] { ".###.", "#...#", "#....", "#....", "#....", "#...#", ".###." },
            ['E'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#####" },
            ['M'] = new[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#" },
            ['O'] = new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
            ['R'] = new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" },
            ['S'] = new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." },
            ['.'] = new[] { ".....", ".....", ".....", ".....", ".....", ".##..", ".##.." },
            ['!'] = new[] { "..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#.." },
            ['/'] = new[] { "....#", "....#", "...#.", "..#..", ".#...", "#....", "#...." },
            [' '] = new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." },
        };

        private readonly Dictionary<string, SpriteFrame> _texts = new Dictionary<string, SpriteFrame>(StringComparer.Ordinal);
        private readonly Dictionary<long, SpriteFrame> _bars = new Dictionary<long, SpriteFrame>();
        private readonly Dictionary<string, SpriteFrame> _maps = new Dictionary<string, SpriteFrame>(StringComparer.Ordinal);

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

        public const int MiniCellWidth = 12;
        public const int MiniCellHeight = 9;
        public const int MiniGap = 4;
        public const int MiniPad = 3;

        public static int MiniMapWidth(int cols)
        {
            return cols * MiniCellWidth + (cols - 1) * MiniGap + MiniPad * 2;
        }

        public static int MiniMapHeight(int rows)
        {
            return rows * MiniCellHeight + (rows - 1) * MiniGap + MiniPad * 2;
        }

        /// <summary>
        /// 副本小地图(占位的界面,不是原版的样子):半透明底,每个看得见的迷宫格一个色块(灰 = 只看见入口、橙 = 进过没清、绿 = 清掉了、黄 = 当前所在),
        /// 首领格带红框,相邻两格有出口的地方画一段连线。<paramref name="cells"/> 按行优先,个数要等于 列 × 行。
        /// </summary>
        public SpriteFrame MiniMap(int cols, int rows, IReadOnlyList<MiniMapCell> cells)
        {
            if (cols < 1 || rows < 1 || cells == null || cells.Count != cols * rows)
            {
                throw new ArgumentException("小地图需要 列 × 行 个格子", nameof(cells));
            }
            var key = new System.Text.StringBuilder();
            key.Append(cols).Append('x').Append(rows).Append(':');
            foreach (MiniMapCell c in cells)
            {
                key.Append((int)c.State).Append(',').Append(c.Exits).Append(',').Append(c.Boss ? 'b' : 'n').Append(';');
            }
            string k = key.ToString();
            if (_maps.TryGetValue(k, out SpriteFrame cached))
            {
                return cached;
            }
            int w = MiniMapWidth(cols);
            int h = MiniMapHeight(rows);
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                    PutAlpha(rgba, w, h, x, y, edge ? 0x606060u : 0x101010u, edge ? (byte)220 : (byte)150);
                }
            }
            for (int cy = 0; cy < rows; cy++)
            {
                for (int cx = 0; cx < cols; cx++)
                {
                    MiniMapCell cell = cells[cy * cols + cx];
                    if (cell.State == MiniMapState.Hidden)
                    {
                        continue;
                    }
                    int ox = MiniPad + cx * (MiniCellWidth + MiniGap);
                    int oy = MiniPad + cy * (MiniCellHeight + MiniGap);
                    uint fill = cell.State == MiniMapState.Current ? 0xFFE23Cu
                        : cell.State == MiniMapState.Cleared ? 0x3CA03Cu
                        : cell.State == MiniMapState.Visited ? 0xC87820u
                        : 0x585858u;
                    for (int y = 0; y < MiniCellHeight; y++)
                    {
                        for (int x = 0; x < MiniCellWidth; x++)
                        {
                            bool border = x == 0 || y == 0 || x == MiniCellWidth - 1 || y == MiniCellHeight - 1;
                            uint color = border ? (cell.Boss ? 0xE02020u : 0x181818u) : fill;
                            Put(rgba, w, h, ox + x, oy + y, color);
                        }
                    }
                    if ((cell.Exits & ExitBits.Right) != 0 && cx + 1 < cols && cells[cy * cols + cx + 1].State != MiniMapState.Hidden)
                    {
                        FillRect(rgba, w, h, ox + MiniCellWidth, oy + MiniCellHeight / 2 - 1, MiniGap, 3, 0x909090u);
                    }
                    if ((cell.Exits & ExitBits.Down) != 0 && cy + 1 < rows && cells[(cy + 1) * cols + cx].State != MiniMapState.Hidden)
                    {
                        FillRect(rgba, w, h, ox + MiniCellWidth / 2 - 1, oy + MiniCellHeight, 3, MiniGap, 0x909090u);
                    }
                }
            }
            var frame = new SpriteFrame(w, h, 0, 0, w, h, rgba);
            _maps[k] = frame;
            return frame;
        }

        private static void FillRect(byte[] rgba, int w, int h, int x0, int y0, int rw, int rh, uint rgb)
        {
            for (int y = 0; y < rh; y++)
            {
                for (int x = 0; x < rw; x++)
                {
                    Put(rgba, w, h, x0 + x, y0 + y, rgb);
                }
            }
        }

        private static void PutAlpha(byte[] rgba, int w, int h, int x, int y, uint rgb, byte alpha)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h)
            {
                return;
            }
            int o = (y * w + x) * 4;
            rgba[o] = (byte)((rgb >> 16) & 0xFF);
            rgba[o + 1] = (byte)((rgb >> 8) & 0xFF);
            rgba[o + 2] = (byte)(rgb & 0xFF);
            rgba[o + 3] = alpha;
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
