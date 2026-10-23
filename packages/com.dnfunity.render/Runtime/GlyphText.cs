using System;
using System.Collections.Generic;

namespace Dnf.Render
{
    /// <summary>
    /// 把一段文字用 <see cref="IGlyphSource"/> 的字形排成一张 RGBA 精灵:量宽、折行(汉字之间任意处可断,拉丁文按空格断,
    /// 标点不出现在行首、开括号不留在行尾)、截断加省略号、填色加 1 像素描边(和叠加层的点阵字同样的描边样式)。
    /// 全部在 CPU 上做、不依赖引擎,所以输出和别的精灵一样走同一条绘制管线(CPU 合成与 GPU 逐字节一致)。
    /// 本项目自己的排版(原版的字体渲染没有读),文字内容由调用方给。
    /// </summary>
    public static class GlyphText
    {
        /// <summary>描边宽度(像素)。</summary>
        public const int OutlinePad = 1;

        /// <summary>多行之间默认多留的空隙(像素)。</summary>
        public const int DefaultLineGap = 2;

        // 禁则:这些字符不能出现在行首(遇到行满就挂在上一行末尾)
        private const string NoLineStart = "，。、；：！？）］｝】〕》〉」』”’…·．,.;:!?)]}%";

        // 这些字符不能留在行尾(开括号、开引号)
        private const string NoLineEnd = "（［｛【〔《〈「『“‘([{";

        /// <summary>一个码点在字体里的前进宽度;字体没有这个字时按字号估:全角字一个字号宽,其余半个字号。</summary>
        public static int AdvanceOf(IGlyphSource font, int codePoint)
        {
            if (font == null)
            {
                throw new ArgumentNullException(nameof(font));
            }
            if (font.TryGetGlyph(codePoint, out GlyphBitmap glyph) && glyph != null)
            {
                return glyph.Advance;
            }
            return IsWide(codePoint) ? font.PixelSize : font.PixelSize / 2;
        }

        /// <summary>文字的像素宽(不折行,不含描边):按 \n 分行取最宽的一行。</summary>
        public static int Measure(IGlyphSource font, string text)
        {
            if (font == null)
            {
                throw new ArgumentNullException(nameof(font));
            }
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            int best = 0;
            int line = 0;
            for (int i = 0; i < text.Length;)
            {
                int cp = CodePointAt(text, i, out int len);
                i += len;
                if (cp == '\n')
                {
                    best = Math.Max(best, line);
                    line = 0;
                    continue;
                }
                if (cp == '\r')
                {
                    continue;
                }
                line += AdvanceOf(font, cp);
            }
            return Math.Max(best, line);
        }

        /// <summary>
        /// 折行:先按 \n 分段,再把每一段按 <paramref name="maxWidth"/>(像素)折;<paramref name="maxWidth"/> 不大于 0 时只按 \n 分。
        /// 空段(连续的 \n)保留成空行;整段文字末尾的空行去掉;行尾的空格去掉。
        /// </summary>
        public static List<string> Wrap(IGlyphSource font, string text, int maxWidth)
        {
            if (font == null)
            {
                throw new ArgumentNullException(nameof(font));
            }
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return lines;
            }
            string normal = text.Replace("\r\n", "\n").Replace('\r', '\n');
            int paragraph = 0;
            while (true)
            {
                int newline = normal.IndexOf('\n', paragraph);
                string part = newline < 0 ? normal.Substring(paragraph) : normal.Substring(paragraph, newline - paragraph);
                WrapParagraph(font, part, maxWidth, lines);
                if (newline < 0)
                {
                    break;
                }
                paragraph = newline + 1;
            }
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            return lines;
        }

        /// <summary>单行截断:放得下原样返回;放不下就从后面去字、补一个省略号,直到不超过 <paramref name="maxWidth"/>。</summary>
        public static string Truncate(IGlyphSource font, string text, int maxWidth)
        {
            if (font == null)
            {
                throw new ArgumentNullException(nameof(font));
            }
            if (string.IsNullOrEmpty(text) || maxWidth <= 0 || Measure(font, text) <= maxWidth)
            {
                return text ?? string.Empty;
            }
            return WithEllipsis(font, text, maxWidth);
        }

        /// <summary>
        /// 排成一张精灵:<paramref name="fill"/> 填色(0xRRGGBB)、<paramref name="outline"/> 描边。<paramref name="maxWidth"/> 大于 0 时按它折行(像素,不含描边);
        /// <paramref name="maxLines"/> 大于 0 且行数超了就截到那么多行、最后一行补省略号;<paramref name="lineGap"/> 是行间多留的像素。
        /// 空文字返回 null。
        /// </summary>
        public static SpriteFrame Render(IGlyphSource font, string text, uint fill, uint outline, int maxWidth = 0, int maxLines = 0, int lineGap = DefaultLineGap)
        {
            if (font == null)
            {
                throw new ArgumentNullException(nameof(font));
            }
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            List<string> lines = Wrap(font, text, maxWidth);
            if (lines.Count == 0)
            {
                return null;
            }
            if (maxLines > 0 && lines.Count > maxLines)
            {
                lines.RemoveRange(maxLines, lines.Count - maxLines);
                lines[maxLines - 1] = WithEllipsis(font, lines[maxLines - 1] + Ellipsis, maxWidth > 0 ? maxWidth : int.MaxValue);
            }
            if (lineGap < 0)
            {
                lineGap = 0;
            }

            // 先收集每个字形的落点,算出能装下全部墨迹的范围,免得带悬挑的字被画布边缘切掉
            int lineHeight = Math.Max(1, font.LineHeight);
            int layoutWidth = 0;
            for (int l = 0; l < lines.Count; l++)
            {
                layoutWidth = Math.Max(layoutWidth, Measure(font, lines[l]));
            }
            int layoutHeight = lines.Count * lineHeight + (lines.Count - 1) * lineGap;
            int minX = 0, minY = 0, maxX = layoutWidth, maxY = layoutHeight;
            var placed = new List<Placement>();
            for (int l = 0; l < lines.Count; l++)
            {
                int baseline = l * (lineHeight + lineGap) + font.Ascent;
                int pen = 0;
                string line = lines[l];
                for (int i = 0; i < line.Length;)
                {
                    int cp = CodePointAt(line, i, out int len);
                    i += len;
                    if (font.TryGetGlyph(cp, out GlyphBitmap glyph) && glyph != null)
                    {
                        if (glyph.Width > 0 && glyph.Height > 0)
                        {
                            int gx = pen + glyph.BearingX;
                            int gy = baseline - glyph.BearingY;
                            placed.Add(new Placement(gx, gy, glyph));
                            minX = Math.Min(minX, gx);
                            minY = Math.Min(minY, gy);
                            maxX = Math.Max(maxX, gx + glyph.Width);
                            maxY = Math.Max(maxY, gy + glyph.Height);
                        }
                        pen += glyph.Advance;
                    }
                    else
                    {
                        pen += IsWide(cp) ? font.PixelSize : font.PixelSize / 2;
                    }
                }
            }
            int width = maxX - minX + 2 * OutlinePad;
            int height = maxY - minY + 2 * OutlinePad;
            int originX = OutlinePad - minX;
            int originY = OutlinePad - minY;

            var cover = new byte[width * height];
            foreach (Placement p in placed)
            {
                GlyphBitmap g = p.Glyph;
                for (int row = 0; row < g.Height; row++)
                {
                    int y = p.Y + originY + row;
                    for (int col = 0; col < g.Width; col++)
                    {
                        byte a = g.Alpha[row * g.Width + col];
                        if (a == 0)
                        {
                            continue;
                        }
                        int x = p.X + originX + col;
                        int o = y * width + x;
                        if (a > cover[o])
                        {
                            cover[o] = a;
                        }
                    }
                }
            }
            var rgba = new byte[width * height * 4];
            int fr = (int)((fill >> 16) & 0xFF), fg = (int)((fill >> 8) & 0xFF), fb = (int)(fill & 0xFF);
            int orr = (int)((outline >> 16) & 0xFF), og = (int)((outline >> 8) & 0xFF), ob = (int)(outline & 0xFF);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int fa = cover[y * width + x];
                    int oa = 0;
                    for (int dy = -OutlinePad; dy <= OutlinePad; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= height)
                        {
                            continue;
                        }
                        for (int dx = -OutlinePad; dx <= OutlinePad; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= width)
                            {
                                continue;
                            }
                            int c = cover[yy * width + xx];
                            if (c > oa)
                            {
                                oa = c;
                            }
                        }
                    }
                    if (fa == 0 && oa == 0)
                    {
                        continue;
                    }
                    // 填色盖在描边上:按直通 alpha 合成
                    int a = fa + oa * (255 - fa) / 255;
                    int o = (y * width + x) * 4;
                    rgba[o] = (byte)((fr * fa * 255 + orr * oa * (255 - fa)) / (a * 255));
                    rgba[o + 1] = (byte)((fg * fa * 255 + og * oa * (255 - fa)) / (a * 255));
                    rgba[o + 2] = (byte)((fb * fa * 255 + ob * oa * (255 - fa)) / (a * 255));
                    rgba[o + 3] = (byte)a;
                }
            }
            return new SpriteFrame(width, height, 0, 0, width, height, rgba);
        }

        /// <summary>省略号(U+2026)。</summary>
        public const string Ellipsis = "\u2026";

        private readonly struct Placement
        {
            public readonly int X;
            public readonly int Y;
            public readonly GlyphBitmap Glyph;

            public Placement(int x, int y, GlyphBitmap glyph)
            {
                X = x;
                Y = y;
                Glyph = glyph;
            }
        }

        /// <summary>把 <paramref name="text"/> 从后面去字,直到 去字后的文字 + 省略号 不超过 <paramref name="maxWidth"/>。</summary>
        private static string WithEllipsis(IGlyphSource font, string text, int maxWidth)
        {
            // 调用时传进来的文字可能已经带着省略号(截行用),先去掉,统一补一个
            string body = text.EndsWith(Ellipsis, StringComparison.Ordinal) ? text.Substring(0, text.Length - Ellipsis.Length) : text;
            string mark = font.TryGetGlyph(0x2026, out GlyphBitmap dots) && dots != null ? Ellipsis : "...";
            int markWidth = Measure(font, mark);
            int end = body.Length;
            while (end > 0 && Measure(font, body.Substring(0, end)) + markWidth > maxWidth)
            {
                end--;
                if (end > 0 && char.IsLowSurrogate(body[end]) && char.IsHighSurrogate(body[end - 1]))
                {
                    end--;
                }
            }
            return body.Substring(0, end).TrimEnd(' ') + mark;
        }

        private static void WrapParagraph(IGlyphSource font, string part, int maxWidth, List<string> lines)
        {
            if (part.Length == 0)
            {
                lines.Add(string.Empty);
                return;
            }
            if (maxWidth <= 0)
            {
                lines.Add(part.TrimEnd(' '));
                return;
            }
            int start = 0;
            int width = 0;
            int breakAt = -1;
            int prev = -1;
            int i = 0;
            while (i < part.Length)
            {
                int cp = CodePointAt(part, i, out int len);
                int advance = AdvanceOf(font, cp);
                if (i > start && prev >= 0 && CanBreakBetween(prev, cp))
                {
                    breakAt = i;
                }
                bool overflow = width + advance > maxWidth && i > start && cp != ' ' && !IsNoLineStart(cp);
                if (overflow)
                {
                    int cut = breakAt > start ? breakAt : i;
                    lines.Add(part.Substring(start, cut - start).TrimEnd(' '));
                    start = cut;
                    breakAt = -1;
                    width = 0;
                    for (int k = start; k < i;)
                    {
                        int c2 = CodePointAt(part, k, out int l2);
                        width += AdvanceOf(font, c2);
                        k += l2;
                    }
                }
                width += advance;
                prev = cp;
                i += len;
            }
            lines.Add(part.Substring(start).TrimEnd(' '));
        }

        private static bool CanBreakBetween(int prev, int next)
        {
            if (prev == ' ')
            {
                return next != ' ';
            }
            if (IsNoLineStart(next) || IsNoLineEnd(prev))
            {
                return false;
            }
            if (IsWide(prev))
            {
                return next != ' ';
            }
            return IsWide(next);
        }

        private static bool IsNoLineStart(int cp)
        {
            return cp < 0x10000 && NoLineStart.IndexOf((char)cp) >= 0;
        }

        private static bool IsNoLineEnd(int cp)
        {
            return cp < 0x10000 && NoLineEnd.IndexOf((char)cp) >= 0;
        }

        /// <summary>全角(汉字、假名、谚文、全角标点)码点:排版时每个字都是一个可以断行的位置。</summary>
        public static bool IsWide(int cp)
        {
            return (cp >= 0x2E80 && cp <= 0x9FFF)
                || (cp >= 0xAC00 && cp <= 0xD7AF)
                || (cp >= 0xF900 && cp <= 0xFAFF)
                || (cp >= 0xFE30 && cp <= 0xFE4F)
                || (cp >= 0xFF00 && cp <= 0xFFEF)
                || (cp >= 0x20000 && cp <= 0x2FFFF)
                || cp == 0x2026 || cp == 0x2014 || cp == 0x2018 || cp == 0x2019 || cp == 0x201C || cp == 0x201D;
        }

        private static int CodePointAt(string s, int index, out int length)
        {
            char c = s[index];
            if (char.IsHighSurrogate(c) && index + 1 < s.Length && char.IsLowSurrogate(s[index + 1]))
            {
                length = 2;
                return char.ConvertToUtf32(c, s[index + 1]);
            }
            length = 1;
            return c;
        }
    }
}
