using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>选角界面的素材:图包、头像来源与背景光束动画(原版数据由 <see cref="From"/> 接上;合成数据的测试自己拼)。</summary>
    public sealed class CharacterSelectScene
    {
        public ISpriteSource Sprites { get; }

        /// <summary>头像来源;没有(为 null)就只画底座与文字。</summary>
        public ICharacterAvatars Avatars { get; }

        /// <summary>背景光束的三个动画(读得到的才有);没有读 .ani 的函数或读不到就是空的。</summary>
        public IReadOnlyList<UiAnimation> Beams { get; }

        public CharacterSelectScene(ISpriteSource sprites, ICharacterAvatars avatars = null, Func<string, AniFile> loadAni = null)
        {
            Sprites = sprites ?? throw new ArgumentNullException(nameof(sprites));
            Avatars = avatars;
            var beams = new List<UiAnimation>(3);
            foreach (string path in new[] { CharacterSelectArt.BeamBackPath1, CharacterSelectArt.BeamBackPath2, CharacterSelectArt.BeamSparklePath })
            {
                UiAnimation animation = UiAnimation.Create(sprites, loadAni, path, CharacterSelectArt.EffectX, CharacterSelectArt.EffectY);
                if (animation != null)
                {
                    beams.Add(animation);
                }
            }
            Beams = beams;
        }

        /// <summary>用户自备数据的选角界面:原版图包、鬼剑士头像与背景光束动画。</summary>
        public static CharacterSelectScene From(DnfAssets assets)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            return new CharacterSelectScene(assets, new DnfCharacterAvatars(assets), assets.LoadAni);
        }
    }

    /// <summary>选角界面上的按钮。</summary>
    public enum CharacterSelectButton
    {
        None = 0,

        /// <summary>游戏开始:用选中的角色进城。</summary>
        Start = 1,

        /// <summary>创立角色(单机占位,点了没反应)。</summary>
        Create = 2,

        /// <summary>删除角色(单机占位,点了没反应)。</summary>
        Delete = 3,

        /// <summary>选择频道(单机没有频道,点了没反应)。</summary>
        Channel = 4,

        /// <summary>结束游戏。</summary>
        Quit = 5,
    }

    /// <summary>
    /// 选角界面的画法(证据 E-096,基线 S02):整屏背景图上有窗口边框、一排排底座(每个槽位一个,选中的亮、其它暗),选中的槽位加一个发光的框,
    /// 角色站在底座上,脚下写两行字(等级加名字、职业),下方一排按钮;背景中间有一道缓缓飘动的光束,右上角挂着旗,右边是滚动条。
    /// 所有图都来自用户自备的图包(<c>sprite/interface2/selectcharacter/selectcharacter.img</c>、<c>sprite/interface/windowcommon.img</c>、
    /// <c>sprite/interface2/mercenary/flag.img</c>、光束的 <c>…/effect/selectcharactereffectanimation.img</c>),位置是拿基线帧用 <c>dnf locate</c> / 逐像素试摆量出来的
    /// (背景、边框、标题、旗、滚动条箭头、底座、选中框与辉光、按钮在基线上逐像素一致;光束与头像只是近似,见 E-096 的"没做")。
    /// 图里没有的字(角色名、等级、职业、按钮文字)是中文系统字体,这里用自带的像素字(<see cref="OverlayArt"/>)写英文占位,位置是近似。
    /// 窗口边框、滚动条与按钮是几十个小图块拼的,第一次画时各合成一张大图缓存起来(同一份图包共用一个对象,见 <see cref="For"/>)。
    /// 取不到的帧就不画(合成数据的测试只放要测的帧)。
    /// </summary>
    public sealed class CharacterSelectArt
    {
        public const string UiImage = "sprite/interface2/selectcharacter/selectcharacter.img";
        public const string WindowImage = "sprite/interface/windowcommon.img";
        public const string FlagImage = "sprite/interface2/mercenary/flag.img";

        /// <summary>背景光束的三个动画(同一张图的不同帧;两个宽的在背景里左右飘,一个是粒子),都以 <see cref="EffectX"/>, <see cref="EffectY"/> 为原点。</summary>
        public const string BeamBackPath1 = "ui/selectcharacter/animation/selectcharactereffect_back1.ani";

        public const string BeamBackPath2 = "ui/selectcharacter/animation/selectcharactereffect_back2.ani";
        public const string BeamSparklePath = "ui/selectcharacter/animation/selectcharactereffect.ani";

        // ----- selectcharacter.img 的帧号
        public const int BackgroundFrame = 0;
        public const int TitleFrame = 1;

        /// <summary>左上角的辉光(292×38,加法混合),不随选中的槽位走。</summary>
        public const int GlowFrame = 2;

        /// <summary>选中槽位的发光框(119×207,加法混合)。</summary>
        public const int SlotFrameFrame = 3;

        /// <summary>"游戏开始"按钮:常态、悬停、按下、灰(都是 125×43,字在图里)。</summary>
        public const int StartNormalFrame = 7;

        public const int StartHoverFrame = 8;
        public const int StartPressedFrame = 9;
        public const int StartDisabledFrame = 10;

        /// <summary>底座(119×44,加法混合):亮的给选中的槽位,暗的给其它槽位(没有角色的空槽也画暗的)。</summary>
        public const int PedestalBrightFrame = 11;

        public const int PedestalDimFrame = 12;

        // ----- windowcommon.img 的帧号
        /// <summary>窗口边框九宫格:0 1 2 / 3 4 5 / 6 7 8(四角、上下边、左右边、中心)。</summary>
        public const int BoardFirstFrame = 0;

        public const int ScrollUpFrame = 16;
        public const int ScrollDownFrame = 22;

        /// <summary>滚动条的滑块(9×23,上 11 行是上沿、下 12 行是下沿,中间拉伸)与轨道(9×1,半透明黑)。</summary>
        public const int ScrollThumbFrame = 20;

        public const int ScrollTrackFrame = 28;

        /// <summary>小按钮的三段:左帽(28×23)、中间(2×23,横向铺)、右帽(28×23)。</summary>
        public const int ButtonLeftFrame = 172;

        public const int ButtonMiddleFrame = 173;
        public const int ButtonRightFrame = 174;

        /// <summary>旗(36×53)在 flag.img 里的帧号。</summary>
        public const int FlagFrame = 2;

        // ----- 位置(800×600 画布;各帧自带的偏移在摆放时再加上)
        public const int BoardX = 17;

        public const int BoardY = 68;

        /// <summary>窗口边框中间的铺法:上下边与中心每行铺 62 块、左右边与中心每列铺 33 块(中心块 12×13)。</summary>
        public const int BoardColumns = 62;

        public const int BoardRows = 33;

        public const int TitleX = 344;
        public const int TitleY = 33;
        public const int FlagX = 744;
        public const int FlagY = 10;
        public const int GlowX = 26;
        public const int GlowY = 77;
        public const int EffectX = 343;
        public const int EffectY = 2;

        /// <summary>槽位网格:第 0 格(左上)的框左上角,格距(横向 122、纵向 210)。</summary>
        public const int SlotX = 28;

        public const int SlotY = 85;
        public const int SlotPitchX = 122;
        public const int SlotPitchY = 210;
        public const int SlotWidth = 119;
        public const int SlotHeight = 207;

        /// <summary>底座相对槽位框左上角的位置。</summary>
        public const int PedestalDX = 1;

        public const int PedestalDY = 128;

        /// <summary>
        /// 脚下点(头像的原点)相对槽位框左上角的位置:拿基线第 0 格的角色逐像素量出的(头像层滑动对比,偏移 0 像素时头部、上身与小腿一致的像素最多,
        /// 偏一像素就掉到三分之一;腰腹那一块被悬停提示与鼠标指针遮住,对不了)。
        /// </summary>
        public const int FootDX = 60;

        public const int FootDY = 151;

        /// <summary>两行字相对槽位框左上角:水平居中于槽位,上沿分别在这两个高度(近似)。</summary>
        public const int CaptionDY = 173;

        public const int CaptionLineStep = 13;

        public const int ScrollX = 763;
        public const int ScrollTop = 85;
        public const int ScrollBottom = 489;
        public const int ScrollArrowHeight = 13;

        /// <summary>滑块的高度:基线上账号的槽位比界面能同时显示的多一点,滑块没有占满轨道(351 / 391);占位名册不会滚动,照这个画。</summary>
        public const int ScrollThumbHeight = 351;

        public const int StartX = 337;
        public const int StartY = 526;
        public const int StartWidth = 125;
        public const int StartHeight = 43;

        public const int SmallButtonY = 536;
        public const int SmallButtonWidth = 77;
        public const int SmallButtonHeight = 23;
        public const int CreateButtonX = 137;
        public const int DeleteButtonX = 216;
        public const int ChannelButtonX = 508;
        public const int QuitButtonX = 587;

        /// <summary>两行字与按钮字的颜色(拿基线上字的像素量的)。</summary>
        public const uint CaptionColor = 0xD1B994u;

        public const uint ButtonTextColor = 0xDDC593u;
        public const uint FallbackBackdrop = 0x0A1220u;
        public const uint FallbackButtonColor = 0x1A3A60u;

        private readonly ISpriteSource _sprites;
        private SpriteFrame _board;
        private SpriteFrame _track;
        private SpriteFrame _thumb;
        private SpriteFrame _button;
        private bool _boardTried, _trackTried, _thumbTried, _buttonTried;

        public CharacterSelectArt(ISpriteSource sprites)
        {
            _sprites = sprites ?? throw new ArgumentNullException(nameof(sprites));
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISpriteSource, CharacterSelectArt> Shared =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ISpriteSource, CharacterSelectArt>();

        /// <summary>同一份图包共用一个对象(合成好的大图跟着它,界面反复打开不会重算);<paramref name="sprites"/> 为 null 返回 null。</summary>
        public static CharacterSelectArt For(ISpriteSource sprites)
        {
            if (sprites == null)
            {
                return null;
            }
            if (!Shared.TryGetValue(sprites, out CharacterSelectArt art))
            {
                art = new CharacterSelectArt(sprites);
                Shared.Add(sprites, art);
            }
            return art;
        }

        // ------------------------------------------------------------------ 几何(点击测试与测试共用)

        /// <summary>第 <paramref name="index"/> 个槽位的发光框左上角。</summary>
        public static void SlotOrigin(int index, out int x, out int y)
        {
            CharacterRoster.Cell(index, out int column, out int row);
            x = SlotX + SlotPitchX * column;
            y = SlotY + SlotPitchY * row;
        }

        /// <summary>第 <paramref name="index"/> 个槽位里头像的脚下点(屏幕坐标)。</summary>
        public static void FootPoint(int index, out int x, out int y)
        {
            SlotOrigin(index, out int sx, out int sy);
            x = sx + FootDX;
            y = sy + FootDY;
        }

        /// <summary>画布坐标落在哪个槽位的框里(0 到 <see cref="CharacterRoster.Capacity"/> − 1);不在任何槽位里返回 -1。</summary>
        public static int HitSlot(int x, int y)
        {
            for (int i = 0; i < CharacterRoster.Capacity; i++)
            {
                SlotOrigin(i, out int sx, out int sy);
                if (new HitRect(sx, sy, SlotWidth, SlotHeight).Contains(x, y))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>按钮的左上角与大小。</summary>
        public static HitRect ButtonRect(CharacterSelectButton button)
        {
            switch (button)
            {
                case CharacterSelectButton.Start:
                    return new HitRect(StartX, StartY, StartWidth, StartHeight);
                case CharacterSelectButton.Create:
                    return new HitRect(CreateButtonX, SmallButtonY, SmallButtonWidth, SmallButtonHeight);
                case CharacterSelectButton.Delete:
                    return new HitRect(DeleteButtonX, SmallButtonY, SmallButtonWidth, SmallButtonHeight);
                case CharacterSelectButton.Channel:
                    return new HitRect(ChannelButtonX, SmallButtonY, SmallButtonWidth, SmallButtonHeight);
                case CharacterSelectButton.Quit:
                    return new HitRect(QuitButtonX, SmallButtonY, SmallButtonWidth, SmallButtonHeight);
                default:
                    return new HitRect(0, 0, 0, 0);
            }
        }

        /// <summary>画布坐标落在哪个按钮上;不在任何按钮上返回 <see cref="CharacterSelectButton.None"/>。</summary>
        public static CharacterSelectButton HitButton(int x, int y)
        {
            for (CharacterSelectButton b = CharacterSelectButton.Start; b <= CharacterSelectButton.Quit; b++)
            {
                if (ButtonRect(b).Contains(x, y))
                {
                    return b;
                }
            }
            return CharacterSelectButton.None;
        }

        // ------------------------------------------------------------------ 画

        /// <summary>
        /// 把界面的精灵加进 <paramref name="into"/>(按背景、光束、窗口边框、标题与旗、辉光、滚动条、各槽位的底座与发光框、头像、文字、按钮的顺序,
        /// <paramref name="order"/> 接着往下编号)。<paramref name="selected"/> 是选中角色的序号(-1 = 没有),<paramref name="ageMs"/> 是界面打开以来的毫秒数
        /// (光束与头像的动画按它走)。
        /// </summary>
        public void Build(List<SceneSprite> into, ref int order, OverlayArt font, CharacterSelectScene scene, CharacterRoster roster, int selected, long ageMs)
        {
            if (!AddFrame(into, ref order, UiImage, BackgroundFrame, 0, 0, "select-char-background", false))
            {
                into.Add(OverlayArt.Place(font.Solid(CharacterSelectScreen.ScreenWidth, CharacterSelectScreen.ScreenHeight, FallbackBackdrop), 0, 0, order++, "select-char-backdrop"));
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

            SpriteFrame board = Board();
            if (board != null)
            {
                into.Add(OverlayArt.Place(board, BoardX, BoardY, order++, "select-char-board"));
            }
            AddFrame(into, ref order, UiImage, TitleFrame, TitleX, TitleY, "select-char-title", false);
            AddFrame(into, ref order, FlagImage, FlagFrame, FlagX, FlagY, "select-char-flag", false);
            AddFrame(into, ref order, UiImage, GlowFrame, GlowX, GlowY, "select-char-glow", true);
            AddScrollbar(into, ref order);

            for (int i = 0; i < CharacterRoster.Capacity; i++)
            {
                SlotOrigin(i, out int sx, out int sy);
                bool isSelected = i == selected && i < roster.Count;
                AddFrame(into, ref order, UiImage, isSelected ? PedestalBrightFrame : PedestalDimFrame, sx + PedestalDX, sy + PedestalDY,
                    isSelected ? "select-char-pedestal-bright" : "select-char-pedestal", true);
                if (isSelected)
                {
                    AddFrame(into, ref order, UiImage, SlotFrameFrame, sx, sy, "select-char-slot-frame", true);
                }
            }

            for (int i = 0; i < roster.Count; i++)
            {
                AddAvatar(into, ref order, scene, roster[i], i, ageMs);
                AddCaptions(into, ref order, font, roster[i], i);
            }

            AddButtons(into, ref order, font);
        }

        private void AddScrollbar(List<SceneSprite> into, ref int order)
        {
            AddFrame(into, ref order, WindowImage, ScrollUpFrame, ScrollX, ScrollTop, "select-char-scroll-up", false);
            AddFrame(into, ref order, WindowImage, ScrollDownFrame, ScrollX, ScrollBottom, "select-char-scroll-down", false);
            SpriteFrame track = Track();
            if (track != null)
            {
                into.Add(OverlayArt.Place(track, ScrollX, ScrollTop + ScrollArrowHeight, order++, "select-char-scroll-track"));
            }
            SpriteFrame thumb = Thumb();
            if (thumb != null)
            {
                into.Add(OverlayArt.Place(thumb, ScrollX, ScrollTop + ScrollArrowHeight, order++, "select-char-scroll-thumb"));
            }
        }

        private static void AddAvatar(List<SceneSprite> into, ref int order, CharacterSelectScene scene, CharacterSlot slot, int index, long ageMs)
        {
            if (scene.Avatars == null)
            {
                return;
            }
            FootPoint(index, out int fx, out int fy);
            foreach (SceneSprite s in scene.Avatars.Pose(slot, ageMs, fx, fy))
            {
                into.Add(new SceneSprite(s.Frame, s.ScreenX, s.ScreenY, order++, -3, "select-char-avatar", s.AniFrame, s.Mirrored, s.Blend, s.TintAlpha, OverlayArt.UiRank));
            }
        }

        private static void AddCaptions(List<SceneSprite> into, ref int order, OverlayArt font, CharacterSlot slot, int index)
        {
            SlotOrigin(index, out int sx, out int sy);
            int centre = sx + SlotWidth / 2;
            string[] lines = { slot.Caption, slot.JobName.ToUpperInvariant() };
            for (int i = 0; i < lines.Length; i++)
            {
                SpriteFrame text = font.Text(lines[i], 1, CaptionColor);
                into.Add(OverlayArt.Place(text, centre - text.Width / 2, sy + CaptionDY + CaptionLineStep * i, order++, "select-char-caption"));
            }
        }

        private void AddButtons(List<SceneSprite> into, ref int order, OverlayArt font)
        {
            AddFrame(into, ref order, UiImage, StartNormalFrame, StartX, StartY, "select-char-start", false);
            AddSmallButton(into, ref order, font, CreateButtonX, "CREATE");
            AddSmallButton(into, ref order, font, DeleteButtonX, "DELETE");
            AddSmallButton(into, ref order, font, ChannelButtonX, "CHANNEL");
            AddSmallButton(into, ref order, font, QuitButtonX, "EXIT");
        }

        private void AddSmallButton(List<SceneSprite> into, ref int order, OverlayArt font, int x, string caption)
        {
            SpriteFrame button = Button();
            SpriteFrame face = button ?? font.Solid(SmallButtonWidth, SmallButtonHeight, FallbackButtonColor);
            into.Add(OverlayArt.Place(face, x, SmallButtonY, order++, "select-char-button"));
            SpriteFrame text = font.Text(caption, 1, ButtonTextColor);
            into.Add(OverlayArt.Place(text, x + (SmallButtonWidth - text.Width) / 2, SmallButtonY + (SmallButtonHeight - text.Height) / 2, order++, "select-char-button-text"));
        }

        /// <summary>按控件原点摆一张图:屏幕左上角 = 原点 + 帧自带的偏移。取到并加进列表返回 true。</summary>
        private bool AddFrame(List<SceneSprite> into, ref int order, string image, int frame, int x, int y, string tag, bool additive)
        {
            if (!_sprites.TryGetFrame(image, frame, out SpriteFrame sprite) || sprite.Width == 0 || sprite.Height == 0)
            {
                return false;
            }
            into.Add(new SceneSprite(sprite, x + sprite.OffsetX, y + sprite.OffsetY, order++, -3, tag, 0, false,
                additive ? SpriteBlend.Additive : SpriteBlend.Normal, 255, OverlayArt.UiRank));
            return true;
        }

        // ------------------------------------------------------------------ 合成的大图(各一次)

        private SpriteFrame Board()
        {
            if (!_boardTried)
            {
                _boardTried = true;
                _board = ComposeBoard();
            }
            return _board;
        }

        private SpriteFrame Track()
        {
            if (!_trackTried)
            {
                _trackTried = true;
                _track = ComposeTrack();
            }
            return _track;
        }

        private SpriteFrame Thumb()
        {
            if (!_thumbTried)
            {
                _thumbTried = true;
                _thumb = ComposeThumb();
            }
            return _thumb;
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

        private bool TryFrame(string image, int frame, out SpriteFrame sprite)
        {
            return _sprites.TryGetFrame(image, frame, out sprite) && sprite.Width > 0 && sprite.Height > 0;
        }

        /// <summary>
        /// 窗口边框:四角各一块,上下边与左右边各铺 <see cref="BoardColumns"/> / <see cref="BoardRows"/> 块,中心块铺满;
        /// 图块互不重叠,所以直接拷像素(与逐块画等价,基线上中心区域逐像素一致)。缺任何一块返回 null。
        /// </summary>
        private SpriteFrame ComposeBoard()
        {
            var p = new SpriteFrame[9];
            for (int i = 0; i < 9; i++)
            {
                if (!TryFrame(WindowImage, BoardFirstFrame + i, out p[i]))
                {
                    return null;
                }
            }
            int tileW = p[4].Width, tileH = p[4].Height;
            int left = p[0].Width, top = p[0].Height;
            int width = left + BoardColumns * tileW + p[2].Width;
            int height = top + BoardRows * tileH + p[6].Height;
            var rgba = new byte[width * height * 4];
            Blit(rgba, width, height, p[0], 0, 0);
            Blit(rgba, width, height, p[2], width - p[2].Width, 0);
            Blit(rgba, width, height, p[6], 0, height - p[6].Height);
            Blit(rgba, width, height, p[8], width - p[8].Width, height - p[8].Height);
            for (int c = 0; c < BoardColumns; c++)
            {
                Blit(rgba, width, height, p[1], left + c * tileW, 0);
                Blit(rgba, width, height, p[7], left + c * tileW, height - p[7].Height);
            }
            for (int r = 0; r < BoardRows; r++)
            {
                Blit(rgba, width, height, p[3], 0, top + r * tileH);
                Blit(rgba, width, height, p[5], width - p[5].Width, top + r * tileH);
                for (int c = 0; c < BoardColumns; c++)
                {
                    Blit(rgba, width, height, p[4], left + c * tileW, top + r * tileH);
                }
            }
            return new SpriteFrame(width, height, 0, 0, width, height, rgba);
        }

        /// <summary>滚动条轨道:1 像素高的半透明黑条重复铺满两个箭头之间。</summary>
        private SpriteFrame ComposeTrack()
        {
            if (!TryFrame(WindowImage, ScrollTrackFrame, out SpriteFrame row))
            {
                return null;
            }
            int height = ScrollBottom - ScrollTop - ScrollArrowHeight;
            var rgba = new byte[row.Width * height * 4];
            for (int y = 0; y < height; y++)
            {
                Blit(rgba, row.Width, height, row, 0, y, 0, 1);
            }
            return new SpriteFrame(row.Width, height, 0, 0, row.Width, height, rgba);
        }

        /// <summary>滚动条滑块:滑块图的上 11 行是上沿、其余是下沿,中间重复第 11 行拉到 <see cref="ScrollThumbHeight"/>。</summary>
        private SpriteFrame ComposeThumb()
        {
            if (!TryFrame(WindowImage, ScrollThumbFrame, out SpriteFrame cap) || cap.Height < 12)
            {
                return null;
            }
            const int topRows = 11;
            int bottomRows = cap.Height - topRows;
            int height = ScrollThumbHeight;
            var rgba = new byte[cap.Width * height * 4];
            Blit(rgba, cap.Width, height, cap, 0, 0, 0, topRows);
            for (int y = topRows; y < height - bottomRows; y++)
            {
                Blit(rgba, cap.Width, height, cap, 0, y, topRows, topRows + 1);
            }
            Blit(rgba, cap.Width, height, cap, 0, height - bottomRows, topRows, cap.Height);
            return new SpriteFrame(cap.Width, height, 0, 0, cap.Width, height, rgba);
        }

        /// <summary>小按钮:左帽、中间横向铺、右帽(帽盖在铺的中间上面),总宽 <see cref="SmallButtonWidth"/>。</summary>
        private SpriteFrame ComposeButton()
        {
            if (!TryFrame(WindowImage, ButtonLeftFrame, out SpriteFrame left)
                || !TryFrame(WindowImage, ButtonMiddleFrame, out SpriteFrame middle)
                || !TryFrame(WindowImage, ButtonRightFrame, out SpriteFrame right))
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

        /// <summary>
        /// 把 <paramref name="src"/> 的第 [<paramref name="row0"/>, <paramref name="row1"/>) 行(默认整张)原样拷到 <paramref name="dst"/> 的 (x, y);
        /// 忽略帧自带的偏移(这些图块的偏移都是 0),超出目标范围的部分裁掉。
        /// </summary>
        private static void Blit(byte[] dst, int dstWidth, int dstHeight, SpriteFrame src, int x, int y, int row0 = 0, int row1 = -1)
        {
            if (row1 < 0 || row1 > src.Height)
            {
                row1 = src.Height;
            }
            int x0 = Math.Max(0, -x);
            int x1 = Math.Min(src.Width, dstWidth - x);
            if (x1 <= x0)
            {
                return;
            }
            for (int r = row0; r < row1; r++)
            {
                int dy = y + (r - row0);
                if (dy < 0 || dy >= dstHeight)
                {
                    continue;
                }
                Buffer.BlockCopy(src.Rgba, (r * src.Width + x0) * 4, dst, (dy * dstWidth + x + x0) * 4, (x1 - x0) * 4);
            }
        }
    }
}
