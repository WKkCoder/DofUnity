using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>大按钮("游戏开始")与小按钮的状态。</summary>
    public enum PickerButtonState
    {
        Normal = 0,
        Hover = 1,
        Pressed = 2,
        Disabled = 3,
    }

    /// <summary>
    /// 选服务器与选频道两张界面共用的画法与几何(r18-serverui):整屏背景与光束、窗口边框(与选角界面同一套九宫格,只是更小:50 列 × 28 行)、
    /// 横幅与服务器名牌、"游戏开始"大按钮(常态/悬停/按下/灰,外加一圈呼吸的加法光)、77×23 的小按钮、提示条的位置。
    /// 位置是拿基线 S02-client-00 到 03 用图包逐像素试摆量出来的(见 <see cref="CharacterSelectArt"/> 同样的办法):背景、边框、暗面板、横幅、名牌、
    /// 顶部标题、大按钮在基线上逐像素一致;光晕的强度随时间呼吸:基线 S02-client-00、01、02 三个时刻量到(逐像素相等)的强度是
    /// 标题光晕与活动角标光晕 76、104、87(两者同一个值),大按钮光 30、0、17——两个值加起来几乎恒为 105,所以按"一个呼吸值 w、按钮 = w、标题 = 105 - w"画;
    /// 呼吸的周期与形状基线没有给(三个时刻的界面时间未知),三角波、周期 2 秒是推的;鼠标在大按钮上时光是满的 39(基线 S02-client-03 量到)。</summary>
    /// 窗口边框、暗面板与小按钮是几十个小图块拼的,第一次画时各合成一张大图缓存起来(同一份图包共用一个对象,见 <see cref="For"/>)。取不到的帧就不画。
    /// </summary>
    public sealed class PickerArt
    {
        // ----- windowcommon.img
        public const string WindowImage = CharacterSelectArt.WindowImage;

        /// <summary>窗口边框:九宫格从第 0 帧起(四角、上下边、左右边、中心),中间铺 50 列 × 28 行(622×386)。</summary>
        public const int BoardFirstFrame = 0;

        public const int BoardColumns = 50;
        public const int BoardRows = 28;
        public const int BoardX = 88;
        public const int BoardY = 114;

        /// <summary>选服务器界面里更暗的一块面板:九宫格从第 213 帧起,中心块铺 285 × 168(594×361)。</summary>
        public const int DimFirstFrame = 213;

        public const int DimColumns = 285;
        public const int DimRows = 168;
        public const int DimX = 102;
        public const int DimY = 126;

        // ----- 横幅与名牌
        public const int BannerFrame = 1;       // channelbase.img:横幅底(256×36)
        public const int BannerX = 273;
        public const int BannerY = 73;
        public const int LabelX = 344;          // channelbackground.img 奇数帧:服务器名牌
        public const int LabelY = 72;

        // ----- 大按钮(与选角界面是同一张图 selectcharacter.img)
        public const int StartX = CharacterSelectArt.StartX;
        public const int StartY = CharacterSelectArt.StartY;
        public const int StartWidth = CharacterSelectArt.StartWidth;
        public const int StartHeight = CharacterSelectArt.StartHeight;

        /// <summary>大按钮上的光(加法混合),呼吸:强度在 0 到 <see cref="GlowHigh"/> 之间来回。</summary>
        public const int StartGlowFrame = 13;

        // ----- 小按钮
        public const int SmallButtonY = CharacterSelectArt.SmallButtonY;
        public const int SmallButtonWidth = CharacterSelectArt.SmallButtonWidth;
        public const int SmallButtonHeight = CharacterSelectArt.SmallButtonHeight;

        /// <summary>左边那个小按钮(选频道界面的"变更伺服器")。</summary>
        public const int LeftButtonX = CharacterSelectArt.CreateButtonX;

        /// <summary>右边那个小按钮(选频道界面的"结束游戏")。</summary>
        public const int RightButtonX = CharacterSelectArt.QuitButtonX;

        // ----- 呼吸光:一个三角波 w(0 到 GlowHigh,周期 GlowPeriodMs),大按钮光 = w,标题与活动角标的光 = TitleGlowSum - w(见类注释;曲线是推的)
        public const int GlowHigh = 39;

        public const int GlowPeriodMs = 2000;
        public const int TitleGlowSum = 105;

        // ----- 颜色(0xRRGGBB;字色拿基线上字的像素量的)
        public const uint SubtitleColor = 0xFFFFFFu;

        public const uint ItemTextColor = 0x96FF1Eu;

        /// <summary>常规区域/特殊栏槽位字色（原版 <c>0x010f83c3</c> 暖灰白；仅骰子区域用 <see cref="ItemTextColor"/> 亮绿）。</summary>
        public const uint NormalItemTextColor = 0xC8C3A9u;

        public const uint ButtonTextColor = CharacterSelectArt.ButtonTextColor;
        public const uint FallbackBackdrop = CharacterSelectArt.FallbackBackdrop;

        private readonly ISpriteSource _sprites;
        private SpriteFrame _board, _dim, _button;
        private bool _boardTried, _dimTried, _buttonTried;

        public PickerArt(ISpriteSource sprites)
        {
            _sprites = sprites ?? throw new ArgumentNullException(nameof(sprites));
        }

        private static readonly ConditionalWeakTable<ISpriteSource, PickerArt> Shared = new ConditionalWeakTable<ISpriteSource, PickerArt>();

        /// <summary>同一份图包共用一个对象(合成好的大图跟着它);<paramref name="sprites"/> 为 null 返回 null。</summary>
        public static PickerArt For(ISpriteSource sprites)
        {
            if (sprites == null)
            {
                return null;
            }
            if (!Shared.TryGetValue(sprites, out PickerArt art))
            {
                art = new PickerArt(sprites);
                Shared.Add(sprites, art);
            }
            return art;
        }

        public ISpriteSource Sprites => _sprites;

        /// <summary>没有图包时用的"空"素材(什么帧都取不到:背景铺纯色、各件退回程序画的)。</summary>
        public static PickerArt None { get; } = new PickerArt(new NoSprites());

        private sealed class NoSprites : ISpriteSource
        {
            public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
            {
                sprite = null;
                return false;
            }
        }

        // ------------------------------------------------------------------ 曲线

        /// <summary>三角波:周期 <paramref name="periodMs"/>,从 <paramref name="low"/> 升到 <paramref name="high"/> 再降回(时刻 0 是 low)。</summary>
        public static int Pulse(long ageMs, int periodMs, int low, int high)
        {
            if (periodMs <= 0 || high <= low)
            {
                return Math.Max(0, Math.Min(255, low));
            }
            long t = ageMs < 0 ? 0 : ageMs % periodMs;
            double phase = (double)t / periodMs;
            double tri = 1.0 - Math.Abs(2.0 * phase - 1.0);
            return (int)Math.Round(low + (high - low) * tri, MidpointRounding.AwayFromZero);
        }

        /// <summary>大按钮上的光的强度(呼吸值 w:0 到 <see cref="GlowHigh"/>)。</summary>
        public static int StartGlowTint(long ageMs)
        {
            return Pulse(ageMs, GlowPeriodMs, 0, GlowHigh);
        }

        /// <summary>标题光晕与活动角标光晕的强度(<see cref="TitleGlowSum"/> 减去大按钮光的强度)。</summary>
        public static int TitleGlowTint(long ageMs)
        {
            return TitleGlowSum - StartGlowTint(ageMs);
        }

        // ------------------------------------------------------------------ 几何

        public static HitRect StartRect()
        {
            return new HitRect(StartX, StartY, StartWidth, StartHeight);
        }

        public static HitRect LeftButtonRect()
        {
            return new HitRect(LeftButtonX, SmallButtonY, SmallButtonWidth, SmallButtonHeight);
        }

        public static HitRect RightButtonRect()
        {
            return new HitRect(RightButtonX, SmallButtonY, SmallButtonWidth, SmallButtonHeight);
        }

        /// <summary>大按钮在某个状态下用的帧号(selectcharacter.img)。</summary>
        public static int StartFrame(PickerButtonState state)
        {
            switch (state)
            {
                case PickerButtonState.Hover: return CharacterSelectArt.StartHoverFrame;
                case PickerButtonState.Pressed: return CharacterSelectArt.StartPressedFrame;
                case PickerButtonState.Disabled: return CharacterSelectArt.StartDisabledFrame;
                default: return CharacterSelectArt.StartNormalFrame;
            }
        }

        // ------------------------------------------------------------------ 画

        /// <summary>按控件原点摆一张图:屏幕左上角 = 原点 + 帧自带的偏移。取到并加进列表返回 true。加法混合时 <paramref name="tint"/> 是染色 alpha。</summary>
        public bool AddFrame(List<SceneSprite> into, ref int order, string image, int frame, int x, int y, string tag, bool additive = false, int tint = 255)
        {
            if (frame < 0 || !_sprites.TryGetFrame(image, frame, out SpriteFrame sprite) || sprite.Width == 0 || sprite.Height == 0)
            {
                return false;
            }
            into.Add(new SceneSprite(sprite, x + sprite.OffsetX, y + sprite.OffsetY, order++, -3, tag, 0, false,
                additive ? SpriteBlend.Additive : SpriteBlend.Normal, Math.Max(0, Math.Min(255, tint)), OverlayArt.UiRank));
            return true;
        }

        /// <summary>整屏背景(选角界面的底图),取不到就铺纯色;然后是三束背景光(按 <paramref name="ageMs"/> 走)。</summary>
        public void AddBackground(List<SceneSprite> into, ref int order, OverlayArt font, ServerPickerScene scene, long ageMs, string tag)
        {
            if (!AddFrame(into, ref order, CharacterSelectArt.UiImage, CharacterSelectArt.BackgroundFrame, 0, 0, tag, false))
            {
                into.Add(OverlayArt.Place(font.Solid(CharacterSelectScreen.ScreenWidth, CharacterSelectScreen.ScreenHeight, FallbackBackdrop), 0, 0, order++, tag + "-backdrop"));
            }
            if (scene == null)
            {
                return;
            }
            foreach (UiAnimation beam in scene.Beams)
            {
                SceneSprite sprite = beam.Sprite(ageMs, order);
                if (sprite != null)
                {
                    into.Add(sprite);
                    order++;
                }
            }
        }

        /// <summary>窗口边框(622×386)。</summary>
        public void AddBoard(List<SceneSprite> into, ref int order, string tag)
        {
            SpriteFrame board = Board();
            if (board != null)
            {
                into.Add(OverlayArt.Place(board, BoardX, BoardY, order++, tag));
            }
        }

        /// <summary>选服务器界面里更暗的面板(594×361)。</summary>
        public void AddDimPanel(List<SceneSprite> into, ref int order, string tag)
        {
            SpriteFrame dim = Dim();
            if (dim != null)
            {
                into.Add(OverlayArt.Place(dim, DimX, DimY, order++, tag));
            }
        }

        /// <summary>
        /// 横幅与服务器名牌:横幅底 + 名牌图(有这个服务器的素材时);没有素材或名牌图取不到时,在横幅正中写服务器名(没有字体时写序号占位)。
        /// </summary>
        public void AddBanner(List<SceneSprite> into, ref int order, ShellPaint paint, ServerArtEntry art, string serverName, string placeholder)
        {
            AddFrame(into, ref order, ServerArt.ChannelBaseImage, BannerFrame, BannerX, BannerY, "picker-banner");
            if (art != null && AddFrame(into, ref order, ServerArt.ChannelBackgroundImage, art.ChannelLabelFrame, LabelX, LabelY, "picker-banner-label"))
            {
                return;
            }
            string text = paint.HasFonts ? serverName : placeholder;
            SpriteFrame line = paint.Line(text, 14, ShellPaint.GoldPale, ShellPaint.Navy, 220, true);
            if (line != null)
            {
                OnlineShellArt.Add(into, ref order, line, BannerX + (256 - line.Width) / 2, BannerY + (36 - line.Height) / 2, "picker-banner-text");
            }
        }

        /// <summary>
        /// "游戏开始"大按钮:按状态取帧(常态、悬停、按下、灰);常态与悬停时再盖一层呼吸的光(加法混合,强度见 <see cref="StartGlowTint"/>)。
        /// </summary>
        public void AddStartButton(List<SceneSprite> into, ref int order, OverlayArt font, PickerButtonState state, long ageMs, string tag)
        {
            if (!AddFrame(into, ref order, CharacterSelectArt.UiImage, StartFrame(state), StartX, StartY, tag))
            {
                SpriteFrame face = font.Solid(StartWidth, StartHeight, state == PickerButtonState.Disabled ? 0x303840u : CharacterSelectArt.FallbackButtonColor);
                into.Add(OverlayArt.Place(face, StartX, StartY, order++, tag));
            }
            if (state == PickerButtonState.Normal || state == PickerButtonState.Hover)
            {
                // 悬停时光是满的(基线 S02-client-03 鼠标在按钮上,逐像素对上的强度是 39 = GlowHigh);常态按呼吸值走
                AddFrame(into, ref order, CharacterSelectArt.UiImage, StartGlowFrame, StartX, StartY, tag + "-glow", true,
                    state == PickerButtonState.Hover ? GlowHigh : StartGlowTint(ageMs));
            }
        }

        /// <summary>
        /// 77×23 的小按钮(窗口图块拼的)加居中的文字;灰态在上面盖一层半透明黑、字变暗,悬停时字更亮。取不到图块时退回纯色块。
        /// 标签:<paramref name="tag"/>(底)、<paramref name="tag"/>-text(字)、<paramref name="tag"/>-dim(灰态的暗层)。
        /// </summary>
        public void AddSmallButton(List<SceneSprite> into, ref int order, OverlayArt font, ShellPaint paint, HitRect rect, string text, PickerButtonState state, string tag)
        {
            SpriteFrame button = Button();
            SpriteFrame face = button ?? font.Solid(SmallButtonWidth, SmallButtonHeight, CharacterSelectArt.FallbackButtonColor);
            into.Add(OverlayArt.Place(face, rect.X, rect.Y, order++, tag));
            uint color = state == PickerButtonState.Hover ? 0xFFEBB0u : ButtonTextColor;
            SpriteFrame line = paint.Line(text, 12, color, ShellPaint.Navy, SmallButtonWidth - 8);
            if (line != null)
            {
                OnlineShellArt.Add(into, ref order, line, rect.X + (SmallButtonWidth - line.Width) / 2, rect.Y + (SmallButtonHeight - line.Height) / 2, tag + "-text");
            }
            if (state == PickerButtonState.Disabled)
            {
                var shade = new ShellBox(0xA8000000u, 0xA8000000u, 0, 0, 0);
                OnlineShellArt.Add(into, ref order, paint.Box(SmallButtonWidth, SmallButtonHeight, 3, shade), rect.X, rect.Y, tag + "-dim");
            }
        }

        // ------------------------------------------------------------------ 翻页箭头(程序画的,基线上没有:无基线、非对拍)

        public const int ArrowWidth = 22;
        public const int ArrowHeight = 18;

        private static readonly Dictionary<int, SpriteFrame> Arrows = new Dictionary<int, SpriteFrame>();

        /// <summary>
        /// 选频道界面翻页用的小箭头:深色圆角底(22×18)里一个朝左或朝右的三角;灰态三角是暗灰、底边也暗,悬停时三角更亮。
        /// 同样的参数总是同一个对象。
        /// </summary>
        public static SpriteFrame Arrow(bool left, bool enabled, bool hover)
        {
            int key = (left ? 1 : 0) | (enabled ? 2 : 0) | (hover && enabled ? 4 : 0);
            lock (Arrows)
            {
                if (Arrows.TryGetValue(key, out SpriteFrame cached))
                {
                    return cached;
                }
                var rgba = new byte[ArrowWidth * ArrowHeight * 4];
                uint plate = enabled ? 0xFF121C30u : 0xFF161A22u;
                uint edge = enabled ? 0xFF3F68A8u : 0xFF2B313Cu;
                uint ink = !enabled ? 0xFF4A505Cu : (hover ? 0xFFFFE9A0u : 0xFFE9C46Au);
                const int ArrowDepth = 8;      // 三角的宽
                const int ArrowHalf = 5;       // 三角的半高
                double cx = ArrowWidth / 2.0, cy = ArrowHeight / 2.0;
                for (int y = 0; y < ArrowHeight; y++)
                {
                    for (int x = 0; x < ArrowWidth; x++)
                    {
                        bool border = x == 0 || y == 0 || x == ArrowWidth - 1 || y == ArrowHeight - 1;
                        bool corner = (x == 0 || x == ArrowWidth - 1) && (y == 0 || y == ArrowHeight - 1);
                        if (corner)
                        {
                            continue;
                        }
                        // 三角:尖朝左时,尖在 x = cx - ArrowDepth/2,底在 x = cx + ArrowDepth/2
                        double along = left ? (x + 0.5 - (cx - ArrowDepth / 2.0)) : ((cx + ArrowDepth / 2.0) - (x + 0.5));
                        bool inTriangle = along >= 0 && along <= ArrowDepth && Math.Abs(y + 0.5 - cy) <= along / ArrowDepth * ArrowHalf;
                        uint argb = inTriangle ? ink : (border ? edge : plate);
                        int at = (y * ArrowWidth + x) * 4;
                        rgba[at] = (byte)(argb >> 16);
                        rgba[at + 1] = (byte)(argb >> 8);
                        rgba[at + 2] = (byte)argb;
                        rgba[at + 3] = 0xFF;
                    }
                }
                var frame = new SpriteFrame(ArrowWidth, ArrowHeight, 0, 0, ArrowWidth, ArrowHeight, rgba);
                Arrows[key] = frame;
                return frame;
            }
        }

        // ------------------------------------------------------------------ 合成的大图(各一次)

        private SpriteFrame Board()
        {
            if (!_boardTried)
            {
                _boardTried = true;
                _board = ComposeNine(BoardFirstFrame, BoardColumns, BoardRows);
            }
            return _board;
        }

        private SpriteFrame Dim()
        {
            if (!_dimTried)
            {
                _dimTried = true;
                _dim = ComposeNine(DimFirstFrame, DimColumns, DimRows);
            }
            return _dim;
        }

        private SpriteFrame Button()
        {
            if (!_buttonTried)
            {
                _buttonTried = true;
                _button = ComposeButton();
            }
            return _button;
        }

        private bool TryFrame(int frame, out SpriteFrame sprite)
        {
            return _sprites.TryGetFrame(WindowImage, frame, out sprite) && sprite.Width > 0 && sprite.Height > 0;
        }

        /// <summary>
        /// 九宫格(windowcommon 从 <paramref name="first"/> 起的 9 帧:四角、上下边、左右边、中心):四角各一块,上下边铺 <paramref name="columns"/> 块、
        /// 左右边铺 <paramref name="rows"/> 块,中心铺满;图块互不重叠,所以直接拷像素(基线上逐像素一致)。缺任何一块返回 null。
        /// </summary>
        private SpriteFrame ComposeNine(int first, int columns, int rows)
        {
            var p = new SpriteFrame[9];
            for (int i = 0; i < 9; i++)
            {
                if (!TryFrame(first + i, out p[i]))
                {
                    return null;
                }
            }
            int tileW = p[4].Width, tileH = p[4].Height;
            int left = p[0].Width, top = p[0].Height;
            int width = left + columns * tileW + p[2].Width;
            int height = top + rows * tileH + p[6].Height;
            var rgba = new byte[width * height * 4];
            Blit(rgba, width, height, p[0], 0, 0);
            Blit(rgba, width, height, p[2], width - p[2].Width, 0);
            Blit(rgba, width, height, p[6], 0, height - p[6].Height);
            Blit(rgba, width, height, p[8], width - p[8].Width, height - p[8].Height);
            for (int c = 0; c < columns; c++)
            {
                Blit(rgba, width, height, p[1], left + c * tileW, 0);
                Blit(rgba, width, height, p[7], left + c * tileW, height - p[7].Height);
            }
            for (int r = 0; r < rows; r++)
            {
                Blit(rgba, width, height, p[3], 0, top + r * tileH);
                Blit(rgba, width, height, p[5], width - p[5].Width, top + r * tileH);
                for (int c = 0; c < columns; c++)
                {
                    Blit(rgba, width, height, p[4], left + c * tileW, top + r * tileH);
                }
            }
            return new SpriteFrame(width, height, 0, 0, width, height, rgba);
        }

        /// <summary>小按钮:左帽、中间横向铺、右帽(帽盖在铺的中间上面),总宽 <see cref="SmallButtonWidth"/>。</summary>
        private SpriteFrame ComposeButton()
        {
            if (!TryFrame(CharacterSelectArt.ButtonLeftFrame, out SpriteFrame left)
                || !TryFrame(CharacterSelectArt.ButtonMiddleFrame, out SpriteFrame middle)
                || !TryFrame(CharacterSelectArt.ButtonRightFrame, out SpriteFrame right))
            {
                return null;
            }
            int width = SmallButtonWidth;
            int height = left.Height;
            var rgba = new byte[width * height * 4];
            for (int x = left.Width; x < width - right.Width; x += middle.Width)
            {
                Blit(rgba, width, height, middle, x, 0);
            }
            Blit(rgba, width, height, left, 0, 0);
            Blit(rgba, width, height, right, width - right.Width, 0);
            return new SpriteFrame(width, height, 0, 0, width, height, rgba);
        }

        /// <summary>把 <paramref name="src"/> 原样拷到 <paramref name="dst"/> 的 (x, y);忽略帧自带的偏移(这些图块的偏移都是 0),超出目标范围的部分裁掉。</summary>
        private static void Blit(byte[] dst, int dstWidth, int dstHeight, SpriteFrame src, int x, int y)
        {
            int x0 = Math.Max(0, -x);
            int x1 = Math.Min(src.Width, dstWidth - x);
            if (x1 <= x0)
            {
                return;
            }
            for (int r = 0; r < src.Height; r++)
            {
                int dy = y + r;
                if (dy < 0 || dy >= dstHeight)
                {
                    continue;
                }
                Buffer.BlockCopy(src.Rgba, (r * src.Width + x0) * 4, dst, (dy * dstWidth + x + x0) * 4, (x1 - x0) * 4);
            }
        }
    }
}
