using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;

// 证据:E-122。版式取自基线结算帧(副本清空后地上有物品与金币,主角站在营火旁)上的像素量,只记几何、颜色与位置,不记画面上的任何名字:
//   · 每件地上的东西头顶一块"名牌":深色半透明的底(黑色、不选中时约 80% 不透明),1 像素浅米色边框,四个角各削掉两层(最外层 2 个、次外层 1 个像素),
//     字 12 像素高、不描边、左右各留 4 像素;盒子 23 像素高(含上下边框);
//   · 盒子水平居中在地上那张图的中点;盒子下沿(边框那一行)比图的上沿高 8 行;
//   · 主角够得着、按下攻击键就会捡的那一件(被选中的)多一圈 1 像素的亮黄色外框(沿着盒子的削角走),底更透明(约 55% 不透明),边框不透明;
//   · 字色:普通品质白色,稀有品质(2)紫色 0xB36BFF(量到的);其余品质的颜色没有量到,下面是我按这款游戏界面的惯例补的(低置信度);
//   · 金币堆的名牌是"金额 + 金币"一行白字;两张名牌重叠时画得靠下(进深更大)的盖在上面。
// 没量过、没做:名牌随镜头外缘被挤进屏内的规则、物品落在空中时的名牌(这里落地之后才画)、字体与基线的像素字不同(用系统字体)。
namespace Dnf.Play
{
    /// <summary>
    /// 地上物品与金币的名牌(M30)。只在有汉字字体、也装了文字表(<see cref="WorldTexts.Shared"/>)的运行里画;没有时一律不画,离屏出图与对拍不受影响。
    /// 一个实例对应一个房间会话,里面缓存按"文字、颜色、是否选中"做好的名牌图。
    /// </summary>
    public sealed class DropLabels
    {
        /// <summary>边框宽(像素)。</summary>
        public const int BorderWidth = 1;

        /// <summary>边框内侧到文字的左右空隙(像素)。</summary>
        public const int PadX = 4;

        /// <summary>盒子高(像素,含上下边框)。</summary>
        public const int BoxHeight = 23;

        /// <summary>文字墨迹第一行距盒子上沿的行数(含边框那一行)。</summary>
        public const int InkTop = 5;

        /// <summary>基线 12 像素字的墨迹高(行);比它矮的文字在这一段里居中。</summary>
        public const int InkRows = 12;

        /// <summary>盒子下沿(边框那一行)比地上那张图的上沿高几行。</summary>
        public const int GapAboveSprite = 8;

        /// <summary>选中时外框的宽(像素)。</summary>
        public const int SelectWidth = 1;

        /// <summary>没选中时底的不透明度(0 到 255;量到约 80%)。</summary>
        public const int FillAlpha = 204;

        /// <summary>选中时底的不透明度(量到约 55%)。</summary>
        public const int SelectedFillAlpha = 140;

        /// <summary>没选中时边框的不透明度(推断:与底同为 80%;选中时边框不透明)。</summary>
        public const int BorderAlpha = 204;

        /// <summary>边框色(RGB;选中时量到 (230,210,160))。</summary>
        public const uint BorderRgb = 0xE6D2A0u;

        /// <summary>选中的外框色(RGB;量到 (252,252,0) 上下)。</summary>
        public const uint SelectRgb = 0xFCFC00u;

        /// <summary>稀有品质 0 到 5 的字色:0(白)与 2(紫 0xB36BFF)是在基线上量到的并与物品脚本的 [rarity] 对上了,其余是惯例色(低置信度)。</summary>
        public static readonly uint[] RarityColors = { 0xFFFFFFu, 0x68D5EDu, 0xB36BFFu, 0xFF00FFu, 0xFFB400u, 0xFF7800u };

        /// <summary>金币的字色。</summary>
        public const uint GoldColor = 0xFFFFFFu;

        private readonly OverlayArt _art;
        private readonly Dictionary<string, SpriteFrame> _cache = new Dictionary<string, SpriteFrame>();
        private readonly List<Entry> _scratch = new List<Entry>();

        public DropLabels(OverlayArt art)
        {
            _art = art ?? throw new ArgumentNullException(nameof(art));
        }

        /// <summary>稀有品质对应的字色;越界按两头取。</summary>
        public static uint RarityColor(int rarity)
        {
            return RarityColors[rarity < 0 ? 0 : rarity >= RarityColors.Length ? RarityColors.Length - 1 : rarity];
        }

        /// <summary>这件地上的东西的名牌字:物品是物品名(取不到返回 null),金币是"金额 + 金币"(取不到返回 null)。</summary>
        public static string TextOf(FieldDrop drop, WorldTexts texts)
        {
            if (drop == null || texts == null)
            {
                return null;
            }
            return drop.IsItem ? texts.ItemName(drop.Item) : texts.GoldAmount(drop.Amount);
        }

        /// <summary>这件地上的东西的字色:物品按稀有品质,金币白色。</summary>
        public static uint ColorOf(FieldDrop drop)
        {
            return drop != null && drop.IsItem ? RarityColor(drop.Item.Rarity) : GoldColor;
        }

        /// <summary>某个点在盒子里吗(盒子 <paramref name="w"/> × <paramref name="h"/>,四个角各削掉最外层 2 个、次外层 1 个像素)。</summary>
        public static bool InBox(int x, int y, int w, int h)
        {
            if (x < 0 || y < 0 || x >= w || y >= h)
            {
                return false;
            }
            int dy = Math.Min(y, h - 1 - y);
            int dx = Math.Min(x, w - 1 - x);
            if (dy == 0 && dx < 2)
            {
                return false;
            }
            return !(dy == 1 && dx < 1);
        }

        /// <summary>
        /// 一块名牌的图:盒子 + 文字,四周多留 <see cref="SelectWidth"/> 像素给选中的外框(没选中时那一圈是空的,所以选中与否图的大小和对位都一样)。
        /// 盒子宽 = 文字的排版宽 + 左右各 <see cref="PadX"/> 与边框;没有字体或文字为空返回 null。同样的参数总是同一个对象。
        /// </summary>
        public SpriteFrame Frame(string text, uint textColor, bool selected)
        {
            if (!_art.HasFonts || string.IsNullOrEmpty(text))
            {
                return null;
            }
            string key = (selected ? "s|" : "n|") + textColor + "|" + text;
            if (_cache.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            SpriteFrame glyphs = _art.Wrapped(text, 1, textColor, 0, 0, OverlayArt.Black);
            if (glyphs == null)
            {
                return null;
            }
            SpriteFrame frame = Compose(glyphs, selected);
            _cache[key] = frame;
            return frame;
        }

        private static SpriteFrame Compose(SpriteFrame glyphs, bool selected)
        {
            int pad = GlyphText.OutlinePad;
            int boxW = glyphs.Width - 2 * pad + 2 * (BorderWidth + PadX);
            int boxH = BoxHeight;
            int margin = SelectWidth;
            int w = boxW + 2 * margin;
            int h = boxH + 2 * margin;
            var rgba = new byte[w * h * 4];

            int fillA = selected ? SelectedFillAlpha : FillAlpha;
            int borderA = selected ? 255 : BorderAlpha;
            for (int y = -margin; y < boxH + margin; y++)
            {
                for (int x = -margin; x < boxW + margin; x++)
                {
                    int o = ((y + margin) * w + (x + margin)) * 4;
                    if (InBox(x, y, boxW, boxH))
                    {
                        if (Interior(x, y, boxW, boxH))
                        {
                            Put(rgba, o, 0x000000u, fillA);
                        }
                        else
                        {
                            Put(rgba, o, BorderRgb, borderA);
                        }
                    }
                    else if (selected && TouchesBox(x, y, boxW, boxH))
                    {
                        Put(rgba, o, SelectRgb, 255);
                    }
                }
            }

            // 文字:墨迹第一行放在盒子上沿下 InkTop 行(墨迹比 InkRows 矮时在这一段里居中),排版原点在左边框内侧 PadX 处
            int firstRow = FirstInkRow(glyphs);
            int lastRow = LastInkRow(glyphs);
            int inkHeight = Math.Max(1, lastRow - firstRow + 1 - 2 * pad);
            int inkTop = InkTop + Math.Max(0, (InkRows - inkHeight) / 2);
            int textX = margin + BorderWidth + PadX - pad;
            int textY = margin + inkTop - (firstRow + pad);
            for (int gy = 0; gy < glyphs.Height; gy++)
            {
                int y = textY + gy;
                if (y < 0 || y >= h)
                {
                    continue;
                }
                for (int gx = 0; gx < glyphs.Width; gx++)
                {
                    int x = textX + gx;
                    if (x < 0 || x >= w)
                    {
                        continue;
                    }
                    int s = (gy * glyphs.Width + gx) * 4;
                    int sa = glyphs.Rgba[s + 3];
                    if (sa == 0)
                    {
                        continue;
                    }
                    Over(rgba, (y * w + x) * 4, glyphs.Rgba[s], glyphs.Rgba[s + 1], glyphs.Rgba[s + 2], sa);
                }
            }
            return new SpriteFrame(w, h, 0, 0, w, h, rgba);
        }

        /// <summary>盒子里面、八个邻居也都在盒子里的点是"底",其余盒子里的点是"边框"。</summary>
        private static bool Interior(int x, int y, int w, int h)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!InBox(x + dx, y + dy, w, h))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>盒子外面、八个邻居里有盒子里的点:选中时画外框的地方。</summary>
        private static bool TouchesBox(int x, int y, int w, int h)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (InBox(x + dx, y + dy, w, h))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static void Put(byte[] rgba, int o, uint rgb, int alpha)
        {
            rgba[o] = (byte)((rgb >> 16) & 0xFF);
            rgba[o + 1] = (byte)((rgb >> 8) & 0xFF);
            rgba[o + 2] = (byte)(rgb & 0xFF);
            rgba[o + 3] = (byte)alpha;
        }

        /// <summary>把一个直通 alpha 的像素盖到已有像素上(直通 alpha 合成)。</summary>
        private static void Over(byte[] rgba, int o, int r, int g, int b, int a)
        {
            int da = rgba[o + 3];
            int outA = a + da * (255 - a) / 255;
            if (outA == 0)
            {
                return;
            }
            rgba[o] = (byte)((r * a * 255 + rgba[o] * da * (255 - a)) / (outA * 255));
            rgba[o + 1] = (byte)((g * a * 255 + rgba[o + 1] * da * (255 - a)) / (outA * 255));
            rgba[o + 2] = (byte)((b * a * 255 + rgba[o + 2] * da * (255 - a)) / (outA * 255));
            rgba[o + 3] = (byte)outA;
        }

        private static int FirstInkRow(SpriteFrame f)
        {
            for (int y = 0; y < f.Height; y++)
            {
                for (int x = 0; x < f.Width; x++)
                {
                    if (f.Rgba[(y * f.Width + x) * 4 + 3] != 0)
                    {
                        return y;
                    }
                }
            }
            return 0;
        }

        private static int LastInkRow(SpriteFrame f)
        {
            for (int y = f.Height - 1; y >= 0; y--)
            {
                for (int x = 0; x < f.Width; x++)
                {
                    if (f.Rgba[(y * f.Width + x) * 4 + 3] != 0)
                    {
                        return y;
                    }
                }
            }
            return f.Height - 1;
        }

        /// <summary>
        /// 给所有已落地的地上物品与金币加名牌:取每一件当前的精灵(<see cref="FieldDrop.Sprite"/>)算位置——水平居中在精灵中点、盒子下沿比精灵上沿高
        /// <see cref="GapAboveSprite"/> 行——名牌按进深从小到大加(靠下的盖在上面)。<paramref name="selected"/> 那一件画成选中的样子。
        /// 返回加了几块;没有字体、没有文字表、没有图或取不到文字的都跳过。
        /// </summary>
        public int Add(List<SceneSprite> into, ref int order, IReadOnlyList<FieldDrop> drops, ISpriteSource sprites, TownProjection projection, FieldDrop selected,
            WorldTexts texts)
        {
            if (into == null || drops == null || drops.Count == 0 || texts == null || !_art.HasFonts)
            {
                return 0;
            }
            _scratch.Clear();
            foreach (FieldDrop drop in drops)
            {
                if (!drop.Landed)
                {
                    continue;
                }
                SceneSprite sprite = drop.Sprite(sprites, projection);
                if (sprite == null)
                {
                    continue;
                }
                string text = TextOf(drop, texts);
                SpriteFrame frame = Frame(text, ColorOf(drop), ReferenceEquals(drop, selected));
                if (frame == null)
                {
                    continue;
                }
                int boxW = frame.Width - 2 * SelectWidth;
                int centerX = sprite.ScreenX + sprite.Frame.Width / 2;
                int x = centerX - boxW / 2 - SelectWidth;
                int y = sprite.ScreenY - GapAboveSprite - (BoxHeight - 1) - SelectWidth;
                _scratch.Add(new Entry(drop, frame, x, y));
            }
            _scratch.Sort(CompareDepth);
            foreach (Entry e in _scratch)
            {
                into.Add(OverlayArt.Place(e.Frame, e.X, e.Y, order++, ReferenceEquals(e.Drop, selected) ? "drop-label-selected" : "drop-label"));
            }
            return _scratch.Count;
        }

        private static int CompareDepth(Entry a, Entry b)
        {
            int byY = a.Drop.Y.CompareTo(b.Drop.Y);
            return byY != 0 ? byY : a.Drop.Id.CompareTo(b.Drop.Id);
        }

        private readonly struct Entry
        {
            public readonly FieldDrop Drop;
            public readonly SpriteFrame Frame;
            public readonly int X;
            public readonly int Y;

            public Entry(FieldDrop drop, SpriteFrame frame, int x, int y)
            {
                Drop = drop;
                Frame = frame;
                X = x;
                Y = y;
            }
        }
    }
}
