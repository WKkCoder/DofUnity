using System.Collections.Generic;
using System.Text;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 联网外壳各个画面共用的画法:背景(选角界面的底图 <see cref="CharacterSelectArt.UiImage"/> 的 <see cref="CharacterSelectArt.BackgroundFrame"/>,
    /// 取不到就铺纯色)、文字(标签走文字表,玩家输入的数据文字不做占位翻译)。图都来自用户自备的图包,仓库里没有任何新的素材路径。
    /// </summary>
    public static class OnlineShellArt
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

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

        /// <summary>把固定文字(标签、提示)按 (x, y) 放上去;文字为空什么也不放。返回放上去的帧(没放是 null)。</summary>
        public static SpriteFrame AddText(List<SceneSprite> into, ref int order, OverlayArt art, string text, int x, int y, int scale, uint color, string tag)
        {
            SpriteFrame frame = art.Text(text, scale, color);
            if (frame == null)
            {
                return null;
            }
            into.Add(OverlayArt.Place(frame, x, y, order++, tag));
            return frame;
        }

        /// <summary>同 <see cref="AddText"/>,水平居中于 <paramref name="centerX"/>。</summary>
        public static SpriteFrame AddCentered(List<SceneSprite> into, ref int order, OverlayArt art, string text, int centerX, int y, int scale, uint color, string tag)
        {
            SpriteFrame frame = art.Text(text, scale, color);
            if (frame == null)
            {
                return null;
            }
            into.Add(OverlayArt.Place(frame, centerX - frame.Width / 2, y, order++, tag));
            return frame;
        }

        /// <summary>
        /// 玩家输入的数据文字(账号):有字体时原样画(不翻译);没有字体时点阵字库只有大写字母与数字,所以画成大写、下划线画成短横、画不出的字符画问号
        /// (只影响显示,输入框里的内容不变)。空串返回 null。
        /// </summary>
        public static SpriteFrame DataText(OverlayArt art, string text, int scale, uint color)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            if (art.HasFonts)
            {
                SpriteFrame glyphs = art.Wrapped(text, scale, color, 0);
                if (glyphs != null)
                {
                    return glyphs;
                }
            }
            return art.Text(GlyphSafe(text), scale, color);
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

        /// <summary>提示的颜色:错误橙色,提示黄色。</summary>
        public static uint NoticeColor(OnlineNotice notice)
        {
            return notice != null && notice.IsError ? OverlayArt.Orange : OverlayArt.Yellow;
        }
    }

    /// <summary>
    /// 联网外壳的"状态画面":选服务器、连频道、加载这几个等待阶段共用一张画面——底图、居中的标题、一行提示(失败原因)和底部的按键说明。
    /// 内容由外壳随时改(换阶段、失败、倒计时),每帧按当前内容重画。
    /// </summary>
    public sealed class OnlineShellStatusScreen : ISceneSource
    {
        private const int TitleY = 250;
        private const int NoticeY = 310;
        private const int HintY = 540;

        private readonly OverlayArt _art;
        private readonly ISpriteSource _sprites;

        /// <param name="sprites">取背景底图的图包;为 null 时背景是纯色。</param>
        /// <param name="fonts">这张画面自己的字体集(测试用);为 null 时用全进程共用的。</param>
        public OnlineShellStatusScreen(ISpriteSource sprites, IGlyphFonts fonts = null)
        {
            _sprites = sprites;
            _art = new OverlayArt { Localize = false, Fonts = fonts };
        }

        /// <summary>标题(居中的大字)。</summary>
        public OnlineShellLabel Title { get; set; } = OnlineShellLabel.Loading;

        /// <summary>标题下面的一行提示(失败原因等);没有为 null。</summary>
        public OnlineNotice Notice { get; set; }

        /// <summary>底部的按键说明。</summary>
        public OnlineShellLabel Hint { get; set; } = OnlineShellLabel.LoadingHint;

        public List<SceneSprite> Build(long timeMs)
        {
            var sprites = new List<SceneSprite>();
            int order = 0;
            bool cjk = _art.HasFonts;
            OnlineShellArt.AddBackground(sprites, ref order, _art, _sprites);
            OnlineShellArt.AddCentered(sprites, ref order, _art, OnlineShellText.Label(Title, cjk), OnlineShellArt.ScreenWidth / 2, TitleY, 3, OverlayArt.White, "online-status-title");
            if (Notice != null)
            {
                OnlineShellArt.AddCentered(sprites, ref order, _art, OnlineShellText.Notice(Notice, cjk), OnlineShellArt.ScreenWidth / 2, NoticeY, 2,
                    OnlineShellArt.NoticeColor(Notice), "online-status-notice");
            }
            OnlineShellArt.AddCentered(sprites, ref order, _art, OnlineShellText.Label(Hint, cjk), OnlineShellArt.ScreenWidth / 2, HintY, cjk ? 2 : 1, OverlayArt.White, "online-status-hint");
            return sprites;
        }
    }

    /// <summary>
    /// 在另一张画面(占位选角界面)上叠一行提示(选角失败、账号下没有角色):底部居中,在底座与按钮之间。没有提示时原样返回里面那张画面的内容。
    /// </summary>
    public sealed class OnlineShellNoticeOverlay : ISceneSource
    {
        private const int NoticeY = 506;

        private readonly ISceneSource _inner;
        private readonly OverlayArt _art;

        /// <param name="inner">被叠提示的那张画面。</param>
        /// <param name="fonts">这张叠层自己的字体集(测试用);为 null 时用全进程共用的。</param>
        public OnlineShellNoticeOverlay(ISceneSource inner, IGlyphFonts fonts = null)
        {
            _inner = inner ?? throw new System.ArgumentNullException(nameof(inner));
            _art = new OverlayArt { Localize = false, Fonts = fonts };
        }

        /// <summary>要叠的提示;没有为 null。</summary>
        public OnlineNotice Notice { get; set; }

        public List<SceneSprite> Build(long timeMs)
        {
            List<SceneSprite> sprites = _inner.Build(timeMs);
            if (Notice == null)
            {
                return sprites;
            }
            int order = 0;
            foreach (SceneSprite sprite in sprites)
            {
                if (sprite.SortKey >= order)
                {
                    order = sprite.SortKey + 1;      // 叠在最上面:排序键比里面任何一张都大
                }
            }
            OnlineShellArt.AddCentered(sprites, ref order, _art, OnlineShellText.Notice(Notice, _art.HasFonts), OnlineShellArt.ScreenWidth / 2, NoticeY, 2,
                OnlineShellArt.NoticeColor(Notice), "online-select-notice");
            return sprites;
        }
    }
}
