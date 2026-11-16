using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>提示的语气:决定提示条的颜色与图标。</summary>
    public enum ShellTone
    {
        /// <summary>一般的说明(已取消、账号下没有角色……)。</summary>
        Info,

        /// <summary>出错了。</summary>
        Error,

        /// <summary>做成了(注册成功、密码已修改)。</summary>
        Success,

        /// <summary>正在等一个请求(画转圈,不是图标)。</summary>
        Wait,
    }

    /// <summary>按键说明里的一项:键帽上写的键名,和这个键做什么。</summary>
    public readonly struct ShellHint
    {
        public readonly string Key;
        public readonly string Text;

        public ShellHint(string key, string text)
        {
            Key = key;
            Text = text;
        }
    }

    /// <summary>圆角块只圆哪些角。</summary>
    public enum ShellCorners
    {
        /// <summary>四个角都圆。</summary>
        All,

        /// <summary>只圆左边两个角(右边是直边,用来做输入框左端的标签格)。</summary>
        Left,
    }

    /// <summary>
    /// 圆角块的样式。颜色一律 0xAARRGGBB:填充从上到下在 <see cref="Top"/> 与 <see cref="Bottom"/> 之间渐变,
    /// 边线(<see cref="EdgeWidth"/> 像素)在 <see cref="EdgeTop"/> 与 <see cref="EdgeBottom"/> 之间渐变;
    /// <see cref="Gloss"/>(0 到 255)在上半部分叠一层白,是按钮的"玻璃感";<see cref="Lip"/>(0 到 255)是紧贴边线内侧的一圈高光。
    /// </summary>
    public readonly struct ShellBox
    {
        public readonly uint Top;
        public readonly uint Bottom;
        public readonly uint EdgeTop;
        public readonly uint EdgeBottom;
        public readonly int EdgeWidth;
        public readonly int Gloss;
        public readonly int Lip;

        public ShellBox(uint top, uint bottom, uint edgeTop, uint edgeBottom, int edgeWidth, int gloss = 0, int lip = 0)
        {
            Top = top;
            Bottom = bottom;
            EdgeTop = edgeTop;
            EdgeBottom = edgeBottom;
            EdgeWidth = edgeWidth < 0 ? 0 : edgeWidth;
            Gloss = gloss;
            Lip = lip;
        }

        /// <summary>同样样式总是同一个键(帧缓存用)。</summary>
        public string Key => Top + "," + Bottom + "," + EdgeTop + "," + EdgeBottom + "," + EdgeWidth + "," + Gloss + "," + Lip;
    }

    /// <summary>
    /// 联网外壳各个画面共用的"画笔":全部是 CPU 上现算的 RGBA 精灵(圆角块、发光、转圈、图标、标题字……)与文字,
    /// 和别的精灵一样走同一条绘制管线(CPU 合成与 GPU 逐字节一致),不依赖引擎,不读任何素材。
    /// 同样的参数总是返回同一个对象(Unity 里的贴图按帧对象缓存,所以每一帧都必须拿到同一个对象)。
    /// 文字:有字体集(<see cref="OverlayArt.Fonts"/> 或 <see cref="OverlayArt.SharedFonts"/>)时用字体按像素字号画,
    /// 没有时退回点阵字库(按字号取放大倍数,放不下就缩小一档,还放不下就截断);标签的中英文由调用方按 <see cref="HasFonts"/> 选。
    /// 注意:密码从不经过这里的任何文字方法。
    /// </summary>
    public sealed class ShellPaint
    {
        // ---- 调色板(0xRRGGBB)
        public const uint Ink = 0xF3F7FF;
        public const uint Muted = 0x9FB4D6;
        public const uint Faint = 0x62789C;
        public const uint Gold = 0xFFD25E;
        public const uint GoldPale = 0xFFEBB0;
        public const uint GoldDeep = 0xD98E24;
        public const uint Navy = 0x0A1224;
        public const uint Warm = 0x3B2505;
        public const uint Red = 0xFF7A45;
        public const uint Green = 0x4FD17F;
        public const uint Blue = 0x5B9BFF;

        private readonly OverlayArt _art;
        private readonly Dictionary<string, SpriteFrame> _frames = new Dictionary<string, SpriteFrame>(StringComparer.Ordinal);

        public ShellPaint(OverlayArt art)
        {
            _art = art ?? throw new ArgumentNullException(nameof(art));
        }

        /// <summary>有没有可用的字体集(实例的或全进程共用的);没有时标签要用英文占位。</summary>
        public bool HasFonts => _art.HasFonts;

        /// <summary>0xRRGGBB 加上不透明度(0 到 255)→ 0xAARRGGBB。</summary>
        public static uint Argb(int alpha, uint rgb)
        {
            int a = alpha < 0 ? 0 : (alpha > 255 ? 255 : alpha);
            return ((uint)a << 24) | (rgb & 0xFFFFFFu);
        }

        // ================================================================== 文字

        /// <summary>
        /// 一行固定文字(标签、按钮、提示):<paramref name="px"/> 是字体字号(像素),<paramref name="edge"/> 是 1 像素描边色;
        /// <paramref name="maxWidth"/> 大于 0 时放不下就缩小字号(最多缩 4 像素),还放不下就截断;
        /// <paramref name="bold"/> 为 true 时把笔画向右加粗一像素(字体只有一种粗细,标题与主按钮用它拉开层次;点阵占位不加粗)。空串返回 null。
        /// </summary>
        public SpriteFrame Line(string text, int px, uint fill, uint edge, int maxWidth = 0, bool bold = false)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            IGlyphFonts fonts = _art.Fonts ?? OverlayArt.SharedFonts;
            if (fonts != null)
            {
                SpriteFrame glyphs = GlyphLine(fonts, text, px, fill, edge, maxWidth, false, bold);
                if (glyphs != null)
                {
                    return glyphs;
                }
            }
            return BlockLine(OnlineShellArt.GlyphSafe(text), px, fill, edge, maxWidth, false);
        }

        /// <summary>
        /// 玩家输入的数据文字(账号):有字体时原样画(不翻译、不转大写),没有字体时画成大写点阵(画不出的字符画问号,见 <see cref="OnlineShellArt.GlyphSafe"/>)。
        /// 一行放不下时只留结尾(输入框里光标在最后,看到的是刚打的字),前面补省略号。空串返回 null。
        /// </summary>
        public SpriteFrame Data(string text, int px, uint fill, uint edge, int maxWidth)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            IGlyphFonts fonts = _art.Fonts ?? OverlayArt.SharedFonts;
            if (fonts != null)
            {
                SpriteFrame glyphs = GlyphLine(fonts, text, px, fill, edge, maxWidth, true, false);
                if (glyphs != null)
                {
                    return glyphs;
                }
            }
            return BlockLine(OnlineShellArt.GlyphSafe(text), px, fill, edge, maxWidth, true);
        }

        /// <summary>点阵字库的放大倍数:按字体字号换算(16 以下 1 倍,到 26 是 2 倍,依此类推)。</summary>
        public static int BlockScale(int px)
        {
            if (px <= 16)
            {
                return 1;
            }
            if (px <= 26)
            {
                return 2;
            }
            if (px <= 36)
            {
                return 3;
            }
            return px <= 44 ? 4 : 5;
        }

        private SpriteFrame GlyphLine(IGlyphFonts fonts, string text, int px, uint fill, uint edge, int maxWidth, bool tail, bool bold)
        {
            string key = "t|" + px + "|" + fill + "|" + edge + "|" + maxWidth + "|" + (tail ? "d" : "l") + (bold ? "b" : "n") + "|" + text;
            if (_frames.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            int size = px;
            IGlyphSource font = fonts.ForSize(size);
            if (font == null)
            {
                return null;
            }
            string shown = text;
            if (maxWidth > 0)
            {
                while (GlyphText.Measure(font, shown) > maxWidth && size > px - 4 && size > 8)
                {
                    size--;
                    IGlyphSource smaller = fonts.ForSize(size);
                    if (smaller == null)
                    {
                        break;
                    }
                    font = smaller;
                }
                if (GlyphText.Measure(font, shown) > maxWidth)
                {
                    shown = tail ? TailFit(font, text, maxWidth) : GlyphText.Truncate(font, text, maxWidth);
                }
            }
            SpriteFrame frame = GlyphText.Render(font, shown, fill, edge);
            if (frame != null && bold)
            {
                frame = Embolden(frame, fill);
            }
            if (frame != null)
            {
                _frames[key] = frame;
            }
            return frame;
        }

        /// <summary>把一张文字帧的笔画向右加粗一像素:每个像素取自己与左邻里更像填充色的那个(描边与填充在帧里已经合成,这样不会把描边当成笔画)。</summary>
        private static SpriteFrame Embolden(SpriteFrame src, uint fill)
        {
            int w = src.Width + 1;
            int h = src.Height;
            var rgba = new byte[w * h * 4];
            int fr = (int)((fill >> 16) & 0xFF), fg = (int)((fill >> 8) & 0xFF), fb = (int)(fill & 0xFF);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int a = x < src.Width ? (y * src.Width + x) * 4 : -1;
                    int b = x > 0 ? (y * src.Width + x - 1) * 4 : -1;
                    bool hasA = a >= 0 && src.Rgba[a + 3] != 0;
                    bool hasB = b >= 0 && src.Rgba[b + 3] != 0;
                    if (!hasA && !hasB)
                    {
                        continue;
                    }
                    int pick = a;
                    if (!hasA || (hasB && Distance(src.Rgba, b, fr, fg, fb) < Distance(src.Rgba, a, fr, fg, fb)))
                    {
                        pick = b;
                    }
                    int o = (y * w + x) * 4;
                    rgba[o] = src.Rgba[pick];
                    rgba[o + 1] = src.Rgba[pick + 1];
                    rgba[o + 2] = src.Rgba[pick + 2];
                    rgba[o + 3] = (byte)Math.Max(hasA ? src.Rgba[a + 3] : 0, hasB ? src.Rgba[b + 3] : 0);
                }
            }
            return new SpriteFrame(w, h, 0, 0, w, h, rgba);
        }

        private static int Distance(byte[] rgba, int at, int r, int g, int b)
        {
            int dr = rgba[at] - r, dg = rgba[at + 1] - g, db = rgba[at + 2] - b;
            return dr * dr + dg * dg + db * db;
        }

        /// <summary>从后面往前留:前面补省略号,直到放得下。</summary>
        private static string TailFit(IGlyphSource font, string text, int maxWidth)
        {
            int mark = GlyphText.Measure(font, GlyphText.Ellipsis);
            int start = 0;
            while (start < text.Length - 1 && GlyphText.Measure(font, text.Substring(start)) + mark > maxWidth)
            {
                start++;
                if (start < text.Length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
                {
                    start++;
                }
            }
            return GlyphText.Ellipsis + text.Substring(start);
        }

        private SpriteFrame BlockLine(string text, int px, uint fill, uint edge, int maxWidth, bool tail)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            int scale = BlockScale(px);
            string shown = text;
            if (maxWidth > 0)
            {
                while (scale > 1 && OverlayArt.TextWidth(shown, scale) > maxWidth)
                {
                    scale--;
                }
                int fit = Math.Max(1, (maxWidth - 2 + scale) / (6 * scale));
                if (shown.Length > fit)
                {
                    shown = tail ? shown.Substring(shown.Length - fit) : shown.Substring(0, fit);
                }
            }
            return _art.Text(shown, scale, fill, edge);
        }

        // ================================================================== 形状

        /// <summary>圆角块(渐变填充、渐变边线、可选的玻璃高光),边缘抗锯齿;<paramref name="radius"/> 超过半边长时按半边长。</summary>
        public SpriteFrame Box(int width, int height, int radius, ShellBox style, ShellCorners corners = ShellCorners.All)
        {
            if (width < 2 || height < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }
            string key = "box|" + width + "|" + height + "|" + radius + "|" + (int)corners + "|" + style.Key;
            if (_frames.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            int overscan = corners == ShellCorners.Left ? radius * 2 : 0;
            float halfW = (width + overscan) / 2f;
            float halfH = height / 2f;
            float r = Math.Min(radius, Math.Min(halfW, halfH));
            float bw = style.EdgeWidth;
            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                float t = (y + 0.5f) / height;
                Mix(style.Top, style.Bottom, t, out float fa, out float fr, out float fg, out float fb);
                Mix(style.EdgeTop, style.EdgeBottom, t, out float ea, out float er, out float eg, out float eb);
                float gloss = style.Gloss <= 0 ? 0f : style.Gloss / 255f * (1f - Smooth(0.40f, 0.56f, t));
                float lipRow = style.Lip <= 0 ? 0f : style.Lip / 255f * (1f - Clamp01(t * 2f));
                for (int x = 0; x < width; x++)
                {
                    float d = Sdf(x + 0.5f - halfW, y + 0.5f - halfH, halfW, halfH, r);
                    float outer = Clamp01(0.5f - d);
                    if (outer <= 0f)
                    {
                        continue;
                    }
                    float inner = bw > 0f ? Clamp01(0.5f - (d + bw)) : outer;
                    float lift = gloss;
                    if (lipRow > 0f && bw > 0f)
                    {
                        float band = inner - Clamp01(0.5f - (d + bw + 1f));
                        lift = Math.Max(lift, lipRow * band);
                    }
                    float pr = fr + (255f - fr) * lift;
                    float pg = fg + (255f - fg) * lift;
                    float pb = fb + (255f - fb) * lift;
                    float fillW = fa * inner;
                    float edgeW = ea * (outer - inner);
                    float a = fillW + edgeW;
                    if (a <= 0f)
                    {
                        continue;
                    }
                    int o = (y * width + x) * 4;
                    rgba[o] = ToByte((pr * fillW + er * edgeW) / a);
                    rgba[o + 1] = ToByte((pg * fillW + eg * edgeW) / a);
                    rgba[o + 2] = ToByte((pb * fillW + eb * edgeW) / a);
                    rgba[o + 3] = ToByte(a * 255f);
                }
            }
            return Keep(key, width, height, rgba);
        }

        /// <summary>
        /// 圆角块外面的一圈柔和光晕(<paramref name="spread"/> 是向外扩的像素数,<paramref name="peak"/> 是最浓处的不透明度 0 到 255),
        /// 图的大小是块的四边各加 <paramref name="spread"/>,放的时候左上角要往外退 <paramref name="spread"/>。
        /// <paramref name="fillInside"/> 为 true 时块的里面也铺满最浓的颜色(用来做投影垫在半透明块底下),否则里面是空的(用来做聚焦的光圈)。
        /// </summary>
        public SpriteFrame Glow(int width, int height, int radius, int spread, uint rgb, int peak, bool fillInside)
        {
            if (width < 2 || height < 2 || spread < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(spread));
            }
            string key = "glow|" + width + "|" + height + "|" + radius + "|" + spread + "|" + rgb + "|" + peak + "|" + fillInside;
            if (_frames.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            int w = width + spread * 2;
            int h = height + spread * 2;
            float r = Math.Min(radius, Math.Min(width / 2f, height / 2f));
            var rgba = new byte[w * h * 4];
            byte cr = (byte)((rgb >> 16) & 0xFF), cg = (byte)((rgb >> 8) & 0xFF), cb = (byte)(rgb & 0xFF);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float d = Sdf(x + 0.5f - w / 2f, y + 0.5f - h / 2f, width / 2f, height / 2f, r);
                    float a;
                    if (d <= 0f)
                    {
                        a = fillInside ? 1f : 0f;
                    }
                    else
                    {
                        float t = 1f - d / spread;
                        a = t <= 0f ? 0f : t * t;
                        if (!fillInside)
                        {
                            a *= Clamp01(d + 0.5f);
                        }
                    }
                    if (a <= 0f)
                    {
                        continue;
                    }
                    int o = (y * w + x) * 4;
                    rgba[o] = cr;
                    rgba[o + 1] = cg;
                    rgba[o + 2] = cb;
                    rgba[o + 3] = ToByte(a * peak);
                }
            }
            return Keep(key, w, h, rgba);
        }

        /// <summary>实心圆点(抗锯齿)。</summary>
        public SpriteFrame Dot(int diameter, uint rgb, int alpha = 255)
        {
            if (diameter < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(diameter));
            }
            string key = "dot|" + diameter + "|" + rgb + "|" + alpha;
            if (_frames.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            var rgba = new byte[diameter * diameter * 4];
            float c = diameter / 2f;
            for (int y = 0; y < diameter; y++)
            {
                for (int x = 0; x < diameter; x++)
                {
                    float dx = x + 0.5f - c, dy = y + 0.5f - c;
                    float a = Clamp01(c + 0.5f - (float)Math.Sqrt(dx * dx + dy * dy));
                    if (a > 0f)
                    {
                        PutRgb(rgba, (y * diameter + x) * 4, rgb, a * alpha);
                    }
                }
            }
            return Keep(key, diameter, diameter, rgba);
        }

        /// <summary>菱形(抗锯齿),外接正方形边长 <paramref name="size"/>。</summary>
        public SpriteFrame Diamond(int size, uint rgb)
        {
            if (size < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }
            string key = "diamond|" + size + "|" + rgb;
            if (_frames.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            var rgba = new byte[size * size * 4];
            float c = size / 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float m = Math.Abs(x + 0.5f - c) + Math.Abs(y + 0.5f - c);
                    float a = Clamp01((c - m) * 0.7071f + 0.5f);
                    if (a > 0f)
                    {
                        PutRgb(rgba, (y * size + x) * 4, rgb, a * 255f);
                    }
                }
            }
            return Keep(key, size, size, rgba);
        }

        /// <summary>水平渐变的细线:不透明度从左端 <paramref name="alphaStart"/> 经中点 <paramref name="alphaMid"/> 到右端 <paramref name="alphaEnd"/>。</summary>
        public SpriteFrame Rule(int width, int height, uint rgb, int alphaStart, int alphaMid, int alphaEnd)
        {
            if (width < 1 || height < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }
            string key = "rule|" + width + "|" + height + "|" + rgb + "|" + alphaStart + "|" + alphaMid + "|" + alphaEnd;
            if (_frames.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            var rgba = new byte[width * height * 4];
            for (int x = 0; x < width; x++)
            {
                float t = (x + 0.5f) / width;
                float a = t < 0.5f ? Lerp(alphaStart, alphaMid, t * 2f) : Lerp(alphaMid, alphaEnd, (t - 0.5f) * 2f);
                for (int y = 0; y < height; y++)
                {
                    PutRgb(rgba, (y * width + x) * 4, rgb, a);
                }
            }
            return Keep(key, width, height, rgba);
        }

        /// <summary>
        /// 面板角上的直角括号(<paramref name="corner"/>:0 左上、1 右上、2 左下、3 右下),两条臂各长 <paramref name="arm"/>、粗 <paramref name="thick"/>,
        /// 越往臂的末端越淡。
        /// </summary>
        public SpriteFrame Corner(int corner, int arm, int thick, uint rgb)
        {
            if (arm < 2 || thick < 1 || thick >= arm)
            {
                throw new ArgumentOutOfRangeException(nameof(arm));
            }
            string key = "corner|" + corner + "|" + arm + "|" + thick + "|" + rgb;
            if (_frames.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            var rgba = new byte[arm * arm * 4];
            for (int y = 0; y < arm; y++)
            {
                for (int x = 0; x < arm; x++)
                {
                    int lx = (corner & 1) != 0 ? arm - 1 - x : x;
                    int ly = (corner & 2) != 0 ? arm - 1 - y : y;
                    if (lx >= thick && ly >= thick)
                    {
                        continue;
                    }
                    float along = Math.Max(lx, ly) / (float)(arm - 1);
                    PutRgb(rgba, (y * arm + x) * 4, rgb, Lerp(255f, 90f, along));
                }
            }
            return Keep(key, arm, arm, rgba);
        }

        /// <summary>
        /// 整屏的暗角:中心是 <paramref name="centerAlpha"/>、四周是 <paramref name="edgeAlpha"/> 的不透明度(椭圆形渐变),颜色 <paramref name="rgb"/>。
        /// 盖在背景上让中间的面板更突出。
        /// </summary>
        public SpriteFrame Vignette(int width, int height, int centerAlpha, int edgeAlpha, uint rgb)
        {
            if (width < 2 || height < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }
            string key = "vignette|" + width + "|" + height + "|" + centerAlpha + "|" + edgeAlpha + "|" + rgb;
            if (_frames.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            var rgba = new byte[width * height * 4];
            byte cr = (byte)((rgb >> 16) & 0xFF), cg = (byte)((rgb >> 8) & 0xFF), cb = (byte)(rgb & 0xFF);
            for (int y = 0; y < height; y++)
            {
                float ny = (y + 0.5f) / height * 2f - 1f;
                for (int x = 0; x < width; x++)
                {
                    float nx = (x + 0.5f) / width * 2f - 1f;
                    float r = (float)Math.Sqrt(nx * nx * 0.85f + ny * ny);
                    float a = Lerp(centerAlpha, edgeAlpha, Smooth(0.30f, 1.30f, r));
                    int o = (y * width + x) * 4;
                    rgba[o] = cr;
                    rgba[o + 1] = cg;
                    rgba[o + 2] = cb;
                    rgba[o + 3] = ToByte(a);
                }
            }
            return Keep(key, width, height, rgba);
        }

        /// <summary>
        /// 转圈:<paramref name="phase"/>(0 到 9,越界按圈取模)是头的位置,尾巴逐个变淡变小;正方形边长 <paramref name="size"/>。
        /// 画面按时间每隔几十毫秒换一个 phase,10 张图各自缓存。
        /// </summary>
        public SpriteFrame Spinner(int size, int phase, uint rgb)
        {
            const int Dots = 10;
            if (size < 8)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }
            phase = ((phase % Dots) + Dots) % Dots;
            string key = "spin|" + size + "|" + phase + "|" + rgb;
            if (_frames.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            var rgba = new byte[size * size * 4];
            float c = size / 2f;
            float dotR = Math.Max(1.4f, size * 0.115f);
            float ring = c - dotR - 0.5f;
            for (int k = 0; k < Dots; k++)
            {
                int place = ((phase - k) % Dots + Dots) % Dots;
                double angle = place * (2.0 * Math.PI / Dots) - Math.PI / 2.0;
                float cx = c + ring * (float)Math.Cos(angle);
                float cy = c + ring * (float)Math.Sin(angle);
                float strength = Math.Max(0.14f, 1f - k * 0.092f);
                float radius = dotR * (1f - k * 0.045f);
                int x0 = Math.Max(0, (int)Math.Floor(cx - radius - 1f)), x1 = Math.Min(size - 1, (int)Math.Ceiling(cx + radius + 1f));
                int y0 = Math.Max(0, (int)Math.Floor(cy - radius - 1f)), y1 = Math.Min(size - 1, (int)Math.Ceiling(cy + radius + 1f));
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                        float a = Clamp01(radius + 0.5f - (float)Math.Sqrt(dx * dx + dy * dy)) * strength * 255f;
                        int o = (y * size + x) * 4;
                        if (a > rgba[o + 3])
                        {
                            PutRgb(rgba, o, rgb, a);
                        }
                    }
                }
            }
            return Keep(key, size, size, rgba);
        }

        /// <summary>
        /// 提示图标:实心圆盘(颜色由语气定)加一个深色的符号——出错是感叹号,说明是 i,成功是对勾。
        /// <see cref="ShellTone.Wait"/> 没有图标(画面上画转圈),这里给一个空圆盘。
        /// </summary>
        public SpriteFrame Icon(ShellTone tone, int size)
        {
            if (size < 8)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }
            string key = "icon|" + (int)tone + "|" + size;
            if (_frames.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            uint disc = ToneColor(tone);
            var rgba = new byte[size * size * 4];
            float c = size / 2f;
            float radius = c - 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float a = Clamp01(radius + 0.5f - (float)Math.Sqrt((px - c) * (px - c) + (py - c) * (py - c)));
                    if (a <= 0f)
                    {
                        continue;
                    }
                    float ink = SymbolCover(tone, px / size, py / size, size);
                    int o = (y * size + x) * 4;
                    float mixed = ink;
                    rgba[o] = ToByte(Lerp((disc >> 16) & 0xFF, 0x14, mixed));
                    rgba[o + 1] = ToByte(Lerp((disc >> 8) & 0xFF, 0x10, mixed));
                    rgba[o + 2] = ToByte(Lerp(disc & 0xFF, 0x0A, mixed));
                    rgba[o + 3] = ToByte(a * 255f);
                }
            }
            return Keep(key, size, size, rgba);
        }

        /// <summary>语气的主色(0xRRGGBB):出错橙红、说明金黄、成功绿、等待蓝。</summary>
        public static uint ToneColor(ShellTone tone)
        {
            switch (tone)
            {
                case ShellTone.Error: return Red;
                case ShellTone.Success: return Green;
                case ShellTone.Wait: return Blue;
                default: return Gold;
            }
        }

        private static float SymbolCover(ShellTone tone, float u, float v, int size)
        {
            switch (tone)
            {
                case ShellTone.Error:
                    return Math.Max(Capsule(u, v, 0.5f, 0.25f, 0.5f, 0.55f, 0.075f, size), Capsule(u, v, 0.5f, 0.74f, 0.5f, 0.74f, 0.088f, size));
                case ShellTone.Info:
                    return Math.Max(Capsule(u, v, 0.5f, 0.27f, 0.5f, 0.27f, 0.088f, size), Capsule(u, v, 0.5f, 0.45f, 0.5f, 0.74f, 0.075f, size));
                case ShellTone.Success:
                    return Math.Max(Capsule(u, v, 0.27f, 0.52f, 0.43f, 0.68f, 0.075f, size), Capsule(u, v, 0.43f, 0.68f, 0.74f, 0.34f, 0.075f, size));
                default:
                    return 0f;
            }
        }

        /// <summary>线段 (ax, ay)-(bx, by)(单位正方形坐标)加上半径 <paramref name="radius"/> 的覆盖率,按像素抗锯齿。</summary>
        private static float Capsule(float u, float v, float ax, float ay, float bx, float by, float radius, int size)
        {
            float px = u * size, py = v * size;
            float x0 = ax * size, y0 = ay * size, x1 = bx * size, y1 = by * size;
            float dx = x1 - x0, dy = y1 - y0;
            float len2 = dx * dx + dy * dy;
            float t = len2 <= 0f ? 0f : Clamp01(((px - x0) * dx + (py - y0) * dy) / len2);
            float qx = x0 + dx * t - px, qy = y0 + dy * t - py;
            return Clamp01(radius * size + 0.5f - (float)Math.Sqrt(qx * qx + qy * qy));
        }

        // ================================================================== 标题字

        /// <summary>
        /// 大标题字(游戏名):字形加粗一像素,填充自上而下三段渐变(<paramref name="top"/>、<paramref name="mid"/>、<paramref name="bottom"/>),
        /// 外面一圈 2 像素的 <paramref name="edge"/> 色描边,再加一层向下偏的投影;字距略放开。没有字体集时退回点阵字库(单色)。空串返回 null。
        /// </summary>
        public SpriteFrame Banner(string text, int px, uint top, uint mid, uint bottom, uint edge)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            IGlyphFonts fonts = _art.Fonts ?? OverlayArt.SharedFonts;
            IGlyphSource font = fonts?.ForSize(px);
            if (font == null)
            {
                return BlockLine(OnlineShellArt.GlyphSafe(text), px, mid, edge, 0, false);
            }
            string key = "banner|" + px + "|" + top + "|" + mid + "|" + bottom + "|" + edge + "|" + text;
            if (_frames.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }

            const int Pad = 6;
            int tracking = Math.Max(1, px / 14);
            var placed = new List<KeyValuePair<int, int>>();
            var glyphs = new List<GlyphBitmap>();
            int pen = 0;
            int minX = 0, minY = 0, maxX = 0, maxY = font.Ascent + font.Descent;
            for (int i = 0; i < text.Length; i++)
            {
                if (font.TryGetGlyph(text[i], out GlyphBitmap g) && g != null)
                {
                    if (g.Width > 0 && g.Height > 0)
                    {
                        int gx = pen + g.BearingX;
                        int gy = font.Ascent - g.BearingY;
                        placed.Add(new KeyValuePair<int, int>(gx, gy));
                        glyphs.Add(g);
                        minX = Math.Min(minX, gx);
                        minY = Math.Min(minY, gy);
                        maxX = Math.Max(maxX, gx + g.Width);
                        maxY = Math.Max(maxY, gy + g.Height);
                    }
                    pen += g.Advance + tracking;
                }
                else
                {
                    pen += (GlyphText.IsWide(text[i]) ? font.PixelSize : font.PixelSize / 2) + tracking;
                }
            }
            maxX = Math.Max(maxX, pen - tracking);
            int w = maxX - minX + Pad * 2;
            int h = maxY - minY + Pad * 2 + 3;
            var cover = new byte[w * h];
            for (int i = 0; i < glyphs.Count; i++)
            {
                GlyphBitmap g = glyphs[i];
                int ox = placed[i].Key - minX + Pad;
                int oy = placed[i].Value - minY + Pad;
                for (int row = 0; row < g.Height; row++)
                {
                    for (int col = 0; col < g.Width; col++)
                    {
                        byte a = g.Alpha[row * g.Width + col];
                        int o = (oy + row) * w + ox + col;
                        if (a > cover[o])
                        {
                            cover[o] = a;
                        }
                    }
                }
            }
            byte[] body = px >= 28 ? Dilate(cover, w, h, 1) : cover;
            byte[] rim = Dilate(body, w, h, 5);
            int bodyTop = h, bodyBottom = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (body[y * w + x] > 40)
                    {
                        bodyTop = Math.Min(bodyTop, y);
                        bodyBottom = Math.Max(bodyBottom, y);
                    }
                }
            }
            int span = Math.Max(1, bodyBottom - bodyTop);
            var rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                float t = Clamp01((y - bodyTop) / (float)span);
                float cr, cg, cb;
                if (t < 0.5f)
                {
                    Blend(top, mid, t * 2f, out cr, out cg, out cb);
                }
                else
                {
                    Blend(mid, bottom, (t - 0.5f) * 2f, out cr, out cg, out cb);
                }
                for (int x = 0; x < w; x++)
                {
                    float shadow = y >= 3 ? rim[(y - 3) * w + x] / 255f * 0.55f : 0f;
                    float rimA = rim[y * w + x] / 255f;
                    float bodyA = body[y * w + x] / 255f;
                    // 由后往前叠:投影、描边、字体本身(预乘形式的 over,最后除回直通 alpha)
                    float oa = shadow;
                    float pr = 0x04 * shadow;
                    float pg = 0x08 * shadow;
                    float pb = 0x14 * shadow;
                    OverPre(ref oa, ref pr, ref pg, ref pb, rimA, (edge >> 16) & 0xFF, (edge >> 8) & 0xFF, edge & 0xFF);
                    OverPre(ref oa, ref pr, ref pg, ref pb, bodyA, cr, cg, cb);
                    if (oa <= 0f)
                    {
                        continue;
                    }
                    int o = (y * w + x) * 4;
                    rgba[o] = ToByte(pr / oa);
                    rgba[o + 1] = ToByte(pg / oa);
                    rgba[o + 2] = ToByte(pb / oa);
                    rgba[o + 3] = ToByte(oa * 255f);
                }
            }
            return Keep(key, w, h, rgba);
        }

        /// <summary>把覆盖率图向外扩:每个像素取半径平方不超过 <paramref name="radiusSquared"/> 的邻域里的最大值。</summary>
        private static byte[] Dilate(byte[] src, int w, int h, int radiusSquared)
        {
            int reach = (int)Math.Ceiling(Math.Sqrt(radiusSquared));
            var offsets = new List<int[]>();
            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    if (dx * dx + dy * dy <= radiusSquared)
                    {
                        offsets.Add(new[] { dx, dy });
                    }
                }
            }
            var dst = new byte[src.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte best = 0;
                    foreach (int[] off in offsets)
                    {
                        int xx = x + off[0], yy = y + off[1];
                        if ((uint)xx < (uint)w && (uint)yy < (uint)h)
                        {
                            byte v = src[yy * w + xx];
                            if (v > best)
                            {
                                best = v;
                            }
                        }
                    }
                    dst[y * w + x] = best;
                }
            }
            return dst;
        }

        /// <summary>预乘形式的 over:在 (<paramref name="oa"/>, 预乘的 pr/pg/pb) 上盖一层不透明度 <paramref name="a"/>、颜色 (r, g, b) 的东西。</summary>
        private static void OverPre(ref float oa, ref float pr, ref float pg, ref float pb, float a, float r, float g, float b)
        {
            pr = r * a + pr * (1f - a);
            pg = g * a + pg * (1f - a);
            pb = b * a + pb * (1f - a);
            oa = a + oa * (1f - a);
        }

        private static void Blend(uint c0, uint c1, float t, out float r, out float g, out float b)
        {
            r = Lerp((c0 >> 16) & 0xFF, (c1 >> 16) & 0xFF, t);
            g = Lerp((c0 >> 8) & 0xFF, (c1 >> 8) & 0xFF, t);
            b = Lerp(c0 & 0xFF, c1 & 0xFF, t);
        }

        // ================================================================== 小工具

        private SpriteFrame Keep(string key, int w, int h, byte[] rgba)
        {
            var frame = new SpriteFrame(w, h, 0, 0, w, h, rgba);
            _frames[key] = frame;
            return frame;
        }

        private static void PutRgb(byte[] rgba, int o, uint rgb, float alpha)
        {
            rgba[o] = (byte)((rgb >> 16) & 0xFF);
            rgba[o + 1] = (byte)((rgb >> 8) & 0xFF);
            rgba[o + 2] = (byte)(rgb & 0xFF);
            rgba[o + 3] = ToByte(alpha);
        }

        private static void Mix(uint c0, uint c1, float t, out float a, out float r, out float g, out float b)
        {
            a = Lerp((c0 >> 24) & 0xFF, (c1 >> 24) & 0xFF, t) / 255f;
            r = Lerp((c0 >> 16) & 0xFF, (c1 >> 16) & 0xFF, t);
            g = Lerp((c0 >> 8) & 0xFF, (c1 >> 8) & 0xFF, t);
            b = Lerp(c0 & 0xFF, c1 & 0xFF, t);
        }

        private static byte ToByte(float v)
        {
            return (byte)(v <= 0f ? 0 : (v >= 255f ? 255 : (int)(v + 0.5f)));
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        private static float Smooth(float edge0, float edge1, float x)
        {
            float t = Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        /// <summary>点 (<paramref name="px"/>, <paramref name="py"/>)(相对块中心)到圆角矩形边的有向距离:里面是负数。</summary>
        private static float Sdf(float px, float py, float halfW, float halfH, float radius)
        {
            float qx = Math.Abs(px) - (halfW - radius);
            float qy = Math.Abs(py) - (halfH - radius);
            float ox = qx > 0f ? qx : 0f;
            float oy = qy > 0f ? qy : 0f;
            float inside = Math.Min(Math.Max(qx, qy), 0f);
            return (float)Math.Sqrt(ox * ox + oy * oy) + inside - radius;
        }
    }
}
