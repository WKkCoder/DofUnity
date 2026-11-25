using System;
using System.Collections.Generic;
using System.Text;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 联网外壳各个画面共用的画法:背景(选角界面的底图 <see cref="CharacterSelectArt.UiImage"/> 的 <see cref="CharacterSelectArt.BackgroundFrame"/>,
    /// 取不到就铺纯色)加一层暗角、面板("卡片")、提示条、底部的按键说明。图都来自用户自备的图包,其余全是 <see cref="ShellPaint"/> 现算的形状,
    /// 仓库里没有任何新的素材路径。
    /// </summary>
    public static class OnlineShellArt
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

        /// <summary>卡片的圆角半径(像素)。</summary>
        public const int CardRadius = 14;

        /// <summary>提示条的高度(登录界面里一条;状态画面与叠层里一条)。</summary>
        public const int NoticeBarHeight = 32;

        /// <summary>底部按键说明那一排的顶边 y。</summary>
        public const int HintTop = 548;

        private const int HintCapHeight = 22;
        private const int HintCapMinWidth = 30;
        private const int HintGap = 8;
        private const int HintSpacing = 26;
        private const int CornerArm = 18;

        /// <summary>卡片的样式:深蓝玻璃底、偏亮的上沿、内沿一圈淡淡的高光。</summary>
        public static readonly ShellBox CardStyle = new ShellBox(0xF21B2B49u, 0xF40B1427u, 0xFF6F98D4u, 0xFF2D4875u, 1, 0, 70);

        private static readonly ShellBox KeyCapStyle = new ShellBox(0xFF24365Au, 0xFF16233Eu, 0xFF5B7DB3u, 0xFF3A5586u, 1, 18, 0);

        /// <summary>整屏背景:有图包就用选角底图(原点 + 帧自带偏移,与选角界面同一摆法),否则铺 <see cref="CharacterSelectArt.FallbackBackdrop"/> 纯色。</summary>
        public static void AddBackground(List<SceneSprite> into, ref int order, OverlayArt art, ISpriteSource sprites)
        {
            if (sprites != null
                && sprites.TryGetFrame(CharacterSelectArt.UiImage, CharacterSelectArt.BackgroundFrame, out SpriteFrame frame)
                && frame.Width > 0 && frame.Height > 0)
            {
                into.Add(new SceneSprite(frame, frame.OffsetX, frame.OffsetY, order++, -3, "online-background", 0, false,
                    SpriteBlend.Normal, 255, OverlayArt.UiRank));
                return;
            }
            into.Add(OverlayArt.Place(art.Solid(ScreenWidth, ScreenHeight, CharacterSelectArt.FallbackBackdrop), 0, 0, order++, "online-backdrop"));
        }

        /// <summary>背景上的暗角(中间亮、四周暗),让中间的卡片更突出。</summary>
        public static void AddVignette(List<SceneSprite> into, ref int order, ShellPaint paint)
        {
            Add(into, ref order, paint.Vignette(ScreenWidth, ScreenHeight, 46, 205, 0x050A1Au), 0, 0, "online-vignette");
        }

        /// <summary>放一帧;帧为空什么也不放(返回 null)。<paramref name="tag"/> 是精灵的标签(测试与排错用)。</summary>
        public static SceneSprite Add(List<SceneSprite> into, ref int order, SpriteFrame frame, int x, int y, string tag)
        {
            if (frame == null)
            {
                return null;
            }
            SceneSprite sprite = OverlayArt.Place(frame, x, y, order++, tag);
            into.Add(sprite);
            return sprite;
        }

        /// <summary>
        /// 卡片:投影、玻璃底、四个角上的金色括号。标签是 <paramref name="tag"/>(底)、<paramref name="tag"/>-shadow、<paramref name="tag"/>-corner-0 到 3。
        /// </summary>
        public static void AddCard(List<SceneSprite> into, ref int order, ShellPaint paint, int x, int y, int width, int height, string tag)
        {
            Add(into, ref order, paint.Glow(width, height, CardRadius, 26, 0x000000u, 150, true), x - 26, y - 14, tag + "-shadow");
            Add(into, ref order, paint.Box(width, height, CardRadius, CardStyle), x, y, tag);
            for (int i = 0; i < 4; i++)
            {
                int cx = (i & 1) == 0 ? x - 3 : x + width - CornerArm + 3;
                int cy = (i & 2) == 0 ? y - 3 : y + height - CornerArm + 3;
                Add(into, ref order, paint.Corner(i, CornerArm, 2, ShellPaint.Gold), cx, cy, tag + "-corner-" + i);
            }
        }

        /// <summary>提示的语气:出错、成功、其余是一般说明;没有提示按一般说明算。</summary>
        public static ShellTone ToneOf(OnlineNotice notice)
        {
            if (notice == null)
            {
                return ShellTone.Info;
            }
            if (notice.IsError)
            {
                return ShellTone.Error;
            }
            return notice.Kind == OnlineNoticeKind.RegisterOk || notice.Kind == OnlineNoticeKind.PasswordChanged ? ShellTone.Success : ShellTone.Info;
        }

        /// <summary>提示条上的字色:每种语气一个偏亮的同色系颜色。</summary>
        public static uint ToneTextColor(ShellTone tone)
        {
            switch (tone)
            {
                case ShellTone.Error: return 0xFFC4A6u;
                case ShellTone.Success: return 0xB8F3CEu;
                case ShellTone.Wait: return 0xC6DCFFu;
                default: return 0xFFE9A0u;
            }
        }

        /// <summary>提示条的底与边:每种语气一个深色底加同色系的边。</summary>
        public static ShellBox ToneBox(ShellTone tone)
        {
            switch (tone)
            {
                case ShellTone.Error: return new ShellBox(0xF23C1A12u, 0xF22A120Du, 0xFF93412Au, 0xFF6A2E1Eu, 1);
                case ShellTone.Success: return new ShellBox(0xF2123423u, 0xF20D2418u, 0xFF2E7D50u, 0xFF21593Au, 1);
                case ShellTone.Wait: return new ShellBox(0xF2152A4Cu, 0xF20F1D38u, 0xFF3F68A8u, 0xFF2D4C7Eu, 1);
                default: return new ShellBox(0xF2332C12u, 0xF2241F0Eu, 0xFF8A7326u, 0xFF645220u, 1);
            }
        }

        /// <summary>提示条需要的宽度:左边的色条与图标、文字、右边的留白;不超过 <paramref name="maxWidth"/>。文字为空返回 0。</summary>
        public static int NoticeBarWidth(ShellPaint paint, string text, int maxWidth, bool icon = true)
        {
            int chrome = icon ? NoticeBarChrome : NoticeBarChromePlain;
            SpriteFrame line = paint.Line(text, 14, ShellPaint.Ink, ShellPaint.Navy, maxWidth - chrome);
            return line == null ? 0 : Math.Min(maxWidth, chrome + line.Width);
        }

        private const int NoticeBarChrome = 56;
        private const int NoticeBarChromePlain = 34;

        /// <summary>
        /// 提示条:圆角底、左边的语气色条、图标(等待是转圈,<paramref name="timeMs"/> 决定转到哪一格)、文字。
        /// 标签:<paramref name="tag"/>(文字)、<paramref name="tag"/>-bar、-accent、-icon(等待时是 -spinner)。文字为空什么也不画。
        /// <paramref name="icon"/> 为 false 时不画图标(状态画面顶上已经有一个大图标,条里就不再重复)。
        /// </summary>
        public static void AddNoticeBar(List<SceneSprite> into, ref int order, ShellPaint paint, ShellTone tone, string text,
            int x, int y, int width, long timeMs, string tag, bool icon = true)
        {
            int chrome = icon ? NoticeBarChrome : NoticeBarChromePlain;
            SpriteFrame line = paint.Line(text, 14, ToneTextColor(tone), ShellPaint.Navy, width - chrome);
            if (line == null)
            {
                return;
            }
            int height = NoticeBarHeight;
            Add(into, ref order, paint.Box(width, height, 8, ToneBox(tone)), x, y, tag + "-bar");
            uint color = ShellPaint.ToneColor(tone);
            var accent = new ShellBox(ShellPaint.Argb(255, color), ShellPaint.Argb(255, color), 0, 0, 0);
            Add(into, ref order, paint.Box(4, height - 2, 8, accent, ShellCorners.Left), x + 1, y + 1, tag + "-accent");
            const int Icon = 20;
            if (icon)
            {
                if (tone == ShellTone.Wait)
                {
                    Add(into, ref order, paint.Spinner(Icon, SpinnerPhase(timeMs), color), x + 16, y + (height - Icon) / 2, tag + "-spinner");
                }
                else
                {
                    Add(into, ref order, paint.Icon(tone, Icon), x + 16, y + (height - Icon) / 2, tag + "-icon");
                }
            }
            Add(into, ref order, line, x + (icon ? 44 : 20), y + (height - line.Height) / 2, tag);
        }

        /// <summary>转圈在 <paramref name="timeMs"/> 时刻的头位置(0 到 9,每 80 毫秒走一格)。</summary>
        public static int SpinnerPhase(long timeMs)
        {
            return (int)((timeMs < 0 ? 0 : timeMs) / 80 % 10);
        }

        /// <summary>
        /// 底部的按键说明:一排"键帽 + 说明",整排水平居中于 <paramref name="centerX"/>,顶边 <paramref name="top"/>。
        /// 标签:<paramref name="tag"/>-key-i(键帽)、-keytext-i(键名)、-text-i(说明),i 从 0 起。
        /// </summary>
        public static void AddHints(List<SceneSprite> into, ref int order, ShellPaint paint, ShellHint[] hints, int centerX, int top, string tag)
        {
            if (hints == null || hints.Length == 0)
            {
                return;
            }
            var keys = new SpriteFrame[hints.Length];
            var texts = new SpriteFrame[hints.Length];
            var widths = new int[hints.Length];
            int total = 0;
            for (int i = 0; i < hints.Length; i++)
            {
                keys[i] = paint.Line(hints[i].Key, 12, ShellPaint.Ink, ShellPaint.Navy);
                texts[i] = paint.Line(hints[i].Text, 14, ShellPaint.Muted, ShellPaint.Navy);
                widths[i] = Math.Max(HintCapMinWidth, (keys[i] == null ? 0 : keys[i].Width) + 16);
                total += widths[i] + HintGap + (texts[i] == null ? 0 : texts[i].Width) + (i > 0 ? HintSpacing : 0);
            }
            int x = centerX - total / 2;
            for (int i = 0; i < hints.Length; i++)
            {
                Add(into, ref order, paint.Box(widths[i], HintCapHeight, 5, KeyCapStyle), x, top, tag + "-key-" + i);
                if (keys[i] != null)
                {
                    Add(into, ref order, keys[i], x + (widths[i] - keys[i].Width) / 2, top + (HintCapHeight - keys[i].Height) / 2, tag + "-keytext-" + i);
                }
                x += widths[i] + HintGap;
                if (texts[i] != null)
                {
                    Add(into, ref order, texts[i], x, top + (HintCapHeight - texts[i].Height) / 2, tag + "-text-" + i);
                    x += texts[i].Width;
                }
                x += HintSpacing;
            }
        }

        /// <summary>点阵字库画得出的写法:大写;下划线换成短横;字库里没有的换成问号。</summary>
        public static string GlyphSafe(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                char upper = char.ToUpperInvariant(c);
                if (OverlayArt.HasGlyph(upper))
                {
                    sb.Append(upper);
                }
                else
                {
                    sb.Append(c == '_' ? '-' : '?');
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 联网外壳的"状态画面":选服务器、连频道、加载这几个等待阶段共用一张画面——背景加暗角、居中的卡片(转圈或图标、标题、失败原因的提示条)和底部的按键说明。
    /// 内容由外壳随时改(换阶段、失败、倒计时),每帧按当前内容重画。
    /// </summary>
    public sealed class OnlineShellStatusScreen : ISceneSource
    {
        private const int CardWidth = 440;
        private const int CardX = (OnlineShellArt.ScreenWidth - CardWidth) / 2;
        private const int CardY = 200;
        private const int PlainHeight = 150;
        private const int NoticeHeight = 196;
        private const int GlyphSize = 38;
        private const int GlyphY = CardY + 26;
        private const int TitleY = CardY + 78;
        private const int NoticeY = CardY + 126;

        private readonly OverlayArt _art;
        private readonly ShellPaint _paint;
        private readonly ISpriteSource _sprites;

        /// <param name="sprites">取背景底图的图包;为 null 时背景是纯色。</param>
        /// <param name="fonts">这张画面自己的字体集(测试用);为 null 时用全进程共用的。</param>
        public OnlineShellStatusScreen(ISpriteSource sprites, IGlyphFonts fonts = null)
        {
            _sprites = sprites;
            _art = new OverlayArt { Localize = false, Fonts = fonts };
            _paint = new ShellPaint(_art);
        }

        /// <summary>标题(居中的大字)。</summary>
        public OnlineShellLabel Title { get; set; } = OnlineShellLabel.Loading;

        /// <summary>标题下面的提示条(失败原因等);没有为 null。</summary>
        public OnlineNotice Notice { get; set; }

        /// <summary>底部的按键说明。</summary>
        public OnlineShellLabel Hint { get; set; } = OnlineShellLabel.LoadingHint;

        public List<SceneSprite> Build(long timeMs)
        {
            var sprites = new List<SceneSprite>();
            int order = 0;
            bool cjk = _paint.HasFonts;
            OnlineNotice notice = Notice;
            OnlineShellArt.AddBackground(sprites, ref order, _art, _sprites);
            OnlineShellArt.AddVignette(sprites, ref order, _paint);
            OnlineShellArt.AddCard(sprites, ref order, _paint, CardX, CardY, CardWidth, notice == null ? PlainHeight : NoticeHeight, "online-status-panel");

            int centerX = OnlineShellArt.ScreenWidth / 2;
            if (notice == null)
            {
                OnlineShellArt.Add(sprites, ref order, _paint.Spinner(GlyphSize, OnlineShellArt.SpinnerPhase(timeMs), ShellPaint.Gold), centerX - GlyphSize / 2, GlyphY, "online-status-spinner");
            }
            else
            {
                OnlineShellArt.Add(sprites, ref order, _paint.Icon(OnlineShellArt.ToneOf(notice), GlyphSize), centerX - GlyphSize / 2, GlyphY, "online-status-icon");
            }
            SpriteFrame title = _paint.Line(OnlineShellText.Label(Title, cjk), 24, ShellPaint.Ink, ShellPaint.Navy, CardWidth - 48, true);
            if (title != null)
            {
                OnlineShellArt.Add(sprites, ref order, title, centerX - title.Width / 2, TitleY, "online-status-title");
            }
            if (notice != null)
            {
                int width = OnlineShellArt.NoticeBarWidth(_paint, OnlineShellText.Notice(notice, cjk), CardWidth - 48, false);
                OnlineShellArt.AddNoticeBar(sprites, ref order, _paint, OnlineShellArt.ToneOf(notice), OnlineShellText.Notice(notice, cjk),
                    centerX - width / 2, NoticeY, width, timeMs, "online-status-notice", false);
            }
            OnlineShellArt.AddHints(sprites, ref order, _paint, OnlineShellText.Hints(Hint, cjk), centerX, OnlineShellArt.HintTop, "online-status-hint");
            return sprites;
        }
    }

    /// <summary>
    /// 在另一张画面(占位选角界面)上叠一条提示条(选角失败、账号下没有角色):底部居中,在底座与按钮之间。没有提示时原样返回里面那张画面的内容。
    /// </summary>
    public sealed class OnlineShellNoticeOverlay : ISceneSource
    {
        /// <summary>提示条的顶边 y:条的底边正好落在选角界面"开始"按钮的顶边上(<see cref="CharacterSelectArt.StartY"/>),不压按钮。</summary>
        public const int NoticeY = CharacterSelectArt.StartY - OnlineShellArt.NoticeBarHeight;

        private readonly ISceneSource _inner;
        private readonly OverlayArt _art;
        private readonly ShellPaint _paint;

        /// <param name="inner">被叠提示的那张画面。</param>
        /// <param name="fonts">这张叠层自己的字体集(测试用);为 null 时用全进程共用的。</param>
        public OnlineShellNoticeOverlay(ISceneSource inner, IGlyphFonts fonts = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _art = new OverlayArt { Localize = false, Fonts = fonts };
            _paint = new ShellPaint(_art);
        }

        /// <summary>要叠的提示;没有为 null。</summary>
        public OnlineNotice Notice { get; set; }

        /// <summary>在选角界面之上叠画的创建/删除模态面板;没有为 null。</summary>
        public ICharacterManageModal Modal { get; set; }

        public List<SceneSprite> Build(long timeMs)
        {
            List<SceneSprite> sprites = _inner.Build(timeMs);
            int order = 0;
            foreach (SceneSprite sprite in sprites)
            {
                if (sprite.SortKey >= order)
                {
                    order = sprite.SortKey + 1;      // 叠在最上面:排序键比里面任何一张都大
                }
            }
            if (Notice != null)
            {
                // 联网选角界面自己的提示条(不能进入、暂未开放、正在进入)与这里的提示在同一个位置:外壳有提示时只留外壳的,不叠两条
                sprites.RemoveAll(s => CharacterSelectArt.IsOwnBar(s.AnimationPath));
                order = 0;
                foreach (SceneSprite sprite in sprites)
                {
                    if (sprite.SortKey >= order)
                    {
                        order = sprite.SortKey + 1;
                    }
                }
                string text = OnlineShellText.Notice(Notice, _paint.HasFonts);
                int width = OnlineShellArt.NoticeBarWidth(_paint, text, 560);
                OnlineShellArt.AddNoticeBar(sprites, ref order, _paint, OnlineShellArt.ToneOf(Notice), text,
                    OnlineShellArt.ScreenWidth / 2 - width / 2, NoticeY, width, timeMs, "online-select-notice");
            }
            if (Modal != null)
            {
                Modal.BuildInto(sprites, ref order, timeMs);
            }
            return sprites;
        }
    }
}
