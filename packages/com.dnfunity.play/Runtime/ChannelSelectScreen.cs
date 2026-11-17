using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>选频道界面在忙什么(忙着的时候除了 Esc 取消、结束游戏,控件都是灰的)。</summary>
    public enum ChannelBusy
    {
        None = 0,

        /// <summary>正在连接选定的频道。</summary>
        Connecting = 1,

        /// <summary>正在重读目录(刷新)。</summary>
        Refreshing = 2,
    }

    /// <summary>选频道界面攒下的、等阶段机去办的事(<see cref="ChannelSelectScreen.TakeAction"/> 取走即清)。</summary>
    public enum ChannelSelectAction
    {
        None = 0,

        /// <summary>连选中的频道("游戏开始";第一次是连接,从选角回来是换频道)。</summary>
        Start = 1,

        /// <summary>回选服务器("变更伺服器"或 Esc)。</summary>
        Back = 2,

        /// <summary>结束游戏:断开并退出。</summary>
        Quit = 3,

        /// <summary>重读目录(刷新,或读失败后的重试)。</summary>
        Refresh = 4,

        /// <summary>目录读不到时的出口:直接连默认频道。</summary>
        DirectConnect = 5,
    }

    /// <summary>
    /// 选频道界面(基线 S02-client-03,<see cref="ISceneSource"/>,叠在 Play 层):整屏,左边是服务器的竖插画,顶上"选择频道"标题与服务器名横幅,
    /// 一条带高亮字的说明、一块深色面板,面板里 3 列 × 5 行的频道项(每项:骰子图标、两行字[序号与频道名、推荐等级]、右边的拥挤档位词),
    /// 右下"刷新"(带图标),底下"变更伺服器"、"游戏开始"、"结束游戏"三个按钮。素材与位置见 <see cref="PickerArt"/>、<see cref="ServerArt"/>。
    /// <list type="bullet">
    /// <item>每页 15 项(E-424);超过一页时面板下方出现翻页箭头与页码(基线上没有,"无基线、非对拍"),方向键按目录顺序一格一格移动选中项、越过页边自动翻页。</item>
    /// <item>满载或没有可用端口的频道不能选(画暗,点它给一句说明);"游戏开始"在没有选中项、正在忙、读目录期间是灰的、点了没反应。</item>
    /// <item>忙着的时候(正在连接、正在刷新)显示等待提示,所有控件(除"结束游戏")置灰;阶段由阶段机管,界面只攒意图(<see cref="TakeAction"/>)。</item>
    /// <item>服务器没有频道、目录读失败:面板里写原因,给"重试"与"默认频道"两个出口。</item>
    /// </list>
    /// 没有图包时背景铺纯色、频道项画程序底板,逻辑完全一样;没有字体集时文字退回英文点阵占位(中文频道名换成只写序号)。
    /// </summary>
    public sealed class ChannelSelectScreen : ISceneSource
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

        // ----- 频道网格(E-424:每页 15 项;基线量出来的原点与格距)
        public const int Columns = 3;

        public const int Rows = 5;
        public const int PageSize = Columns * Rows;
        public const int ItemX = 116;
        public const int ItemY = 196;
        public const int ItemPitchX = 190;
        public const int ItemPitchY = 44;
        public const int ItemWidth = 186;
        public const int ItemHeight = 40;

        /// <summary>频道项里的字:相对项左上角的位置(基线上字的墨迹框:第一行 (159,203)、第二行 (159,217)、拥挤词右端 294)。</summary>
        public const int TitleDX = 43;

        public const int TitleDY = 7;
        public const int LevelDY = 21;
        public const int CongestionRightDX = 178;
        public const int CongestionDY = 15;
        public const int TextPixel = 12;

        /// <summary>两行字的最大宽度(右边是拥挤词,二者在 y 上有交叠,字不能伸进去)。</summary>
        public const int TextMaxWidth = 112;

        // ----- 版面(基线量出来的原点)
        public const int IllustrationX = 2;

        public const int IllustrationY = 2;
        public const int TitleX = 344;
        public const int TitleY = 33;
        public const int RuleX = 118;
        public const int RuleY = 417;
        public const int RefreshX = 619;
        public const int RefreshY = 466;
        public const int RefreshWidth = 64;
        public const int RefreshHeight = 20;

        // channelbase.img 的帧号
        private const int TitleFrame = 0;

        private const int SelectFrame = 2;
        private const int RefreshNormalFrame = 3;
        private const int RefreshHoverFrame = 4;
        private const int RefreshPressedFrame = 5;
        private const int RefreshDisabledFrame = 6;
        private const int RuleFrame = 8;
        private const int StripFrame = 14;

        /// <summary>频道项的底(channelslot.img 里带骰子图标的那一张)。</summary>
        public const int ItemFrame = 16;

        // ----- 翻页控件与提示(无基线、非对拍)
        public const int PagerY = 438;

        public const int PagerCenterX = 399;
        public const int PagerArrowWidth = PickerArt.ArrowWidth;
        public const int PagerArrowHeight = PickerArt.ArrowHeight;
        public const int PagerGap = 34;
        public const int ContentCenterX = 399;
        public const int MessageY = 280;
        public const int ExitButtonsY = 350;
        public const int NoticeY = 462;

        private const int PressMs = 140;

        private enum Target
        {
            None,
            Item,
            Start,
            Change,
            Quit,
            Refresh,
            PrevPage,
            NextPage,
            Retry,
            Direct,
        }

        private readonly OverlayArt _art;
        private readonly ShellPaint _paint;
        private readonly ServerPickerScene _scene;
        private readonly PickerArt _pickerArt;

        private DirectoryServer _server;
        private IReadOnlyList<DirectoryChannel> _channels = Array.Empty<DirectoryChannel>();
        private ChannelSelectAction _action;
        private ChannelTarget _actionTarget;
        private Target _hover;
        private int _hoverItem = -1;
        private double _ageMs;
        private double _pressUntilMs = -1;
        private PickerLabel? _hint;

        /// <param name="scene">素材;为 null 时画占位(纯色底、程序板)。</param>
        /// <param name="fonts">这张画面自己的字体集(测试用);为 null 时用全进程共用的。</param>
        public ChannelSelectScreen(ServerPickerScene scene = null, IGlyphFonts fonts = null)
        {
            _scene = scene;
            _pickerArt = scene == null ? null : PickerArt.For(scene.Sprites);
            _art = new OverlayArt { Localize = false, Fonts = fonts };
            _paint = new ShellPaint(_art);
            Phase = PickerPhase.Loading;
            Selected = -1;
        }

        public PickerPhase Phase { get; private set; }

        public ChannelBusy Busy { get; private set; }

        /// <summary>现在显示的服务器;还没有为 null。</summary>
        public DirectoryServer Server => _server;

        public IReadOnlyList<DirectoryChannel> Channels => _channels;

        /// <summary>选中的频道在服务器频道列表里的序号;没有为 -1。</summary>
        public int Selected { get; private set; }

        /// <summary>现在在第几页(从 0 起)。</summary>
        public int Page { get; private set; }

        /// <summary>现在在显示的提示(连接失败、已取消、读目录失败的原因等);没有为 null。</summary>
        public OnlineNotice Notice { get; private set; }

        /// <summary>界面自己给的一句说明(点了满载的频道等);没有为 null。任何选择、翻页、忙起来都会清掉它。</summary>
        public PickerLabel? Hint => _hint;

        public long AgeMs => (long)_ageMs;

        public int PageCount => Math.Max(1, (_channels.Count + PageSize - 1) / PageSize);

        public DirectoryChannel SelectedChannel => Selected >= 0 && Selected < _channels.Count ? _channels[Selected] : null;

        /// <summary>"游戏开始"现在能不能点:有内容、没在忙、选中了一个进得去的频道。</summary>
        public bool CanStart => Phase == PickerPhase.Ready && Busy == ChannelBusy.None && SelectedChannel != null && SelectedChannel.CanEnter;

        /// <summary>"刷新"现在能不能点:不在读目录的第一次、没在忙。</summary>
        public bool CanRefresh => Phase != PickerPhase.Loading && Busy == ChannelBusy.None;

        // ------------------------------------------------------------------ 内容

        /// <summary>还没有目录(第一次读):显示"正在读"。</summary>
        public void SetLoading()
        {
            Phase = PickerPhase.Loading;
            _action = ChannelSelectAction.None;
            _hint = null;
        }

        /// <summary>
        /// 显示这个服务器的频道:回到第一页,先选 <paramref name="prefer"/> 指的那个频道(进得去才算),没有就选第一个进得去的;
        /// 没有任何频道是"没有频道"状态。同时清掉忙状态与说明。
        /// </summary>
        public void SetServer(DirectoryServer server, ChannelTarget prefer = null)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _channels = server.Channels;
            _action = ChannelSelectAction.None;
            _hint = null;
            Busy = ChannelBusy.None;
            Notice = null;
            if (_channels.Count == 0)
            {
                Phase = PickerPhase.Empty;
                Selected = -1;
                Page = 0;
                return;
            }
            Phase = PickerPhase.Ready;
            Selected = -1;
            if (prefer != null)
            {
                for (int i = 0; i < _channels.Count; i++)
                {
                    if (_channels[i].CanEnter && prefer.Equals(_channels[i].Target))
                    {
                        Selected = i;
                        break;
                    }
                }
            }
            if (Selected < 0)
            {
                Selected = NextEnterable(-1, 1);
            }
            Page = Selected >= 0 ? Selected / PageSize : 0;
        }

        /// <summary>读目录失败:面板里显示失败,原因由 <see cref="SetNotice"/> 给。清掉忙状态。</summary>
        public void SetFailed()
        {
            Phase = PickerPhase.Failed;
            Busy = ChannelBusy.None;
            _action = ChannelSelectAction.None;
            _hint = null;
        }

        /// <summary>设置忙状态(连接中、刷新中);忙起来时清掉旧的说明。</summary>
        public void SetBusy(ChannelBusy busy)
        {
            Busy = busy;
            if (busy != ChannelBusy.None)
            {
                _hint = null;
                _action = ChannelSelectAction.None;
            }
        }

        /// <summary>要显示的提示;null 清掉。</summary>
        public void SetNotice(OnlineNotice notice)
        {
            Notice = notice;
            if (notice != null)
            {
                _hint = null;
            }
        }

        // ------------------------------------------------------------------ 输入

        /// <summary>界面的时间往前走 <paramref name="deltaMs"/> 毫秒(负数当 0)。</summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs > 0.0)
            {
                _ageMs += deltaMs;
            }
        }

        /// <summary>
        /// 方向键:按目录顺序一格一格找下一个进得去的频道(右、左各一格,下、上各一行即 <see cref="Columns"/> 格),到头不循环;
        /// 越过页边自动翻页。没有选中项时先选第一个进得去的。没在 Ready、或在忙时不响应。
        /// </summary>
        public void Move(int dx, int dy = 0)
        {
            if (Phase != PickerPhase.Ready || Busy != ChannelBusy.None)
            {
                return;
            }
            if (Selected < 0)
            {
                Select(NextEnterable(-1, 1));
                return;
            }
            int step = dx != 0 ? Math.Sign(dx) : Math.Sign(dy) * Columns;
            if (step == 0)
            {
                return;
            }
            for (int i = Selected + step; i >= 0 && i < _channels.Count; i += step)
            {
                if (_channels[i].CanEnter)
                {
                    Select(i);
                    return;
                }
            }
        }

        /// <summary>翻到下一页(选中该页第一个进得去的频道;该页没有进得去的就不选);已经是最后一页不动。</summary>
        public void NextPage()
        {
            GoToPage(Page + 1);
        }

        /// <summary>翻到上一页;已经是第一页不动。</summary>
        public void PrevPage()
        {
            GoToPage(Page - 1);
        }

        /// <summary>回车、空格或点"游戏开始":选中的频道进得去时连它;读失败或没有频道时是重试;在忙或第一次读目录期间没有反应。</summary>
        public void Confirm()
        {
            if (Busy != ChannelBusy.None)
            {
                return;
            }
            switch (Phase)
            {
                case PickerPhase.Ready:
                    if (CanStart)
                    {
                        _action = ChannelSelectAction.Start;
                        _actionTarget = SelectedChannel.Target;
                    }
                    break;
                case PickerPhase.Failed:
                case PickerPhase.Empty:
                    _action = ChannelSelectAction.Refresh;
                    break;
            }
        }

        /// <summary>Esc:返回(阶段机决定是取消在途操作还是回选服务器)。任何状态都响应。</summary>
        public void Back()
        {
            _action = ChannelSelectAction.Back;
        }

        /// <summary>结束游戏。任何状态都响应。</summary>
        public void Quit()
        {
            _action = ChannelSelectAction.Quit;
        }

        /// <summary>刷新(重读目录)。第一次读目录期间、或在忙时不响应。</summary>
        public void Refresh()
        {
            if (CanRefresh)
            {
                _action = ChannelSelectAction.Refresh;
            }
        }

        /// <summary>读失败或没有频道时的"直连默认频道"。</summary>
        public void DirectConnect()
        {
            if ((Phase == PickerPhase.Failed || Phase == PickerPhase.Empty) && Busy == ChannelBusy.None)
            {
                _action = ChannelSelectAction.DirectConnect;
            }
        }

        /// <summary>取走攒下的事(没有返回 <see cref="ChannelSelectAction.None"/>);<paramref name="target"/> 只有 <see cref="ChannelSelectAction.Start"/> 时不为 null。取走即清。</summary>
        public ChannelSelectAction TakeAction(out ChannelTarget target)
        {
            ChannelSelectAction action = _action;
            target = action == ChannelSelectAction.Start ? _actionTarget : null;
            _action = ChannelSelectAction.None;
            _actionTarget = null;
            return action;
        }

        /// <summary>
        /// 鼠标(触摸)点在画布坐标 (<paramref name="x"/>, <paramref name="y"/>)。灰着的控件吃掉这一下、什么也不办;
        /// 点进得去的频道选中它,点满载或没有端口的频道给一句说明。返回这一下有没有落在界面元素上。
        /// </summary>
        public bool Click(int x, int y)
        {
            Target target = HitTest(x, y, out int item);
            switch (target)
            {
                case Target.Item:
                    if (Busy == ChannelBusy.None)
                    {
                        ClickItem(item);
                    }
                    return true;
                case Target.Start:
                    if (CanStart)
                    {
                        _pressUntilMs = _ageMs + PressMs;
                        Confirm();
                    }
                    return true;
                case Target.Change:
                    if (Busy == ChannelBusy.None)
                    {
                        _pressUntilMs = _ageMs + PressMs;
                        _action = ChannelSelectAction.Back;
                    }
                    return true;
                case Target.Quit:
                    _pressUntilMs = _ageMs + PressMs;
                    _action = ChannelSelectAction.Quit;
                    return true;
                case Target.Refresh:
                    if (CanRefresh)
                    {
                        _pressUntilMs = _ageMs + PressMs;
                        _action = ChannelSelectAction.Refresh;
                    }
                    return true;
                case Target.PrevPage:
                    if (Busy == ChannelBusy.None)
                    {
                        PrevPage();
                    }
                    return true;
                case Target.NextPage:
                    if (Busy == ChannelBusy.None)
                    {
                        NextPage();
                    }
                    return true;
                case Target.Retry:
                    _pressUntilMs = _ageMs + PressMs;
                    _action = ChannelSelectAction.Refresh;
                    return true;
                case Target.Direct:
                    _pressUntilMs = _ageMs + PressMs;
                    _action = ChannelSelectAction.DirectConnect;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>鼠标指针在 (<paramref name="x"/>, <paramref name="y"/>) 上(只影响悬停的画法)。</summary>
        public void Hover(int x, int y)
        {
            _hover = HitTest(x, y, out int item);
            _hoverItem = _hover == Target.Item ? item : -1;
        }

        /// <summary>指针移出了画面。</summary>
        public void HoverNone()
        {
            _hover = Target.None;
            _hoverItem = -1;
        }

        private void ClickItem(int index)
        {
            DirectoryChannel channel = _channels[index];
            if (channel.CanEnter)
            {
                Selected = index;
                _hint = null;
            }
            else
            {
                _hint = channel.IsFull ? PickerLabel.ChannelFull : PickerLabel.ChannelUnavailable;
            }
        }

        // ------------------------------------------------------------------ 几何

        /// <summary>第 <paramref name="index"/> 个频道的项矩形;不在当前页返回 false。</summary>
        public bool TryItemRect(int index, out HitRect rect)
        {
            rect = new HitRect(0, 0, 0, 0);
            if (index < 0 || index >= _channels.Count || index / PageSize != Page)
            {
                return false;
            }
            int pos = index % PageSize;
            rect = new HitRect(ItemX + ItemPitchX * (pos % Columns), ItemY + ItemPitchY * (pos / Columns), ItemWidth, ItemHeight);
            return true;
        }

        public static HitRect RefreshRect()
        {
            return new HitRect(RefreshX, RefreshY, RefreshWidth, RefreshHeight);
        }

        public static HitRect ChangeServerRect()
        {
            return PickerArt.LeftButtonRect();
        }

        public static HitRect QuitRect()
        {
            return PickerArt.RightButtonRect();
        }

        public static HitRect PrevPageRect()
        {
            return new HitRect(PagerCenterX - PagerGap - PagerArrowWidth / 2, PagerY, PagerArrowWidth, PagerArrowHeight);
        }

        public static HitRect NextPageRect()
        {
            return new HitRect(PagerCenterX + PagerGap - PagerArrowWidth / 2, PagerY, PagerArrowWidth, PagerArrowHeight);
        }

        public static HitRect RetryRect()
        {
            return new HitRect(ContentCenterX - PickerArt.SmallButtonWidth - 6, ExitButtonsY, PickerArt.SmallButtonWidth, PickerArt.SmallButtonHeight);
        }

        public static HitRect DirectRect()
        {
            return new HitRect(ContentCenterX + 6, ExitButtonsY, PickerArt.SmallButtonWidth, PickerArt.SmallButtonHeight);
        }

        private Target HitTest(int x, int y, out int item)
        {
            item = -1;
            if (PickerArt.StartRect().Contains(x, y))
            {
                return Target.Start;
            }
            if (ChangeServerRect().Contains(x, y))
            {
                return Target.Change;
            }
            if (QuitRect().Contains(x, y))
            {
                return Target.Quit;
            }
            if (RefreshRect().Contains(x, y) && Phase != PickerPhase.Loading)
            {
                return Target.Refresh;
            }
            if (Phase == PickerPhase.Failed || Phase == PickerPhase.Empty)
            {
                if (RetryRect().Contains(x, y))
                {
                    return Target.Retry;
                }
                return DirectRect().Contains(x, y) ? Target.Direct : Target.None;
            }
            if (Phase != PickerPhase.Ready)
            {
                return Target.None;
            }
            if (PageCount > 1)
            {
                if (PrevPageRect().Contains(x, y))
                {
                    return Target.PrevPage;
                }
                if (NextPageRect().Contains(x, y))
                {
                    return Target.NextPage;
                }
            }
            for (int i = Page * PageSize; i < _channels.Count && i < (Page + 1) * PageSize; i++)
            {
                if (TryItemRect(i, out HitRect rect) && rect.Contains(x, y))
                {
                    item = i;
                    return Target.Item;
                }
            }
            return Target.None;
        }

        // ------------------------------------------------------------------ 选中与翻页

        /// <summary>从 <paramref name="from"/> 的下一个起、按 <paramref name="step"/>(1 或 -1)找第一个进得去的频道;没有返回 -1。</summary>
        private int NextEnterable(int from, int step)
        {
            for (int i = from + step; i >= 0 && i < _channels.Count; i += step)
            {
                if (_channels[i].CanEnter)
                {
                    return i;
                }
            }
            return -1;
        }

        private void Select(int index)
        {
            if (index < 0)
            {
                return;
            }
            Selected = index;
            Page = index / PageSize;
            _hint = null;
        }

        private void GoToPage(int page)
        {
            if (Phase != PickerPhase.Ready || Busy != ChannelBusy.None || page < 0 || page >= PageCount || page == Page)
            {
                return;
            }
            Page = page;
            _hint = null;
            int first = page * PageSize;
            Selected = -1;
            for (int i = first; i < _channels.Count && i < first + PageSize; i++)
            {
                if (_channels[i].CanEnter)
                {
                    Selected = i;
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ 画

        private PickerButtonState StartState()
        {
            if (!CanStart)
            {
                return PickerButtonState.Disabled;
            }
            if (_ageMs < _pressUntilMs)
            {
                return PickerButtonState.Pressed;
            }
            return _hover == Target.Start ? PickerButtonState.Hover : PickerButtonState.Normal;
        }

        private PickerButtonState SmallState(Target target, bool enabled)
        {
            if (!enabled)
            {
                return PickerButtonState.Disabled;
            }
            if (_hover == target)
            {
                return _ageMs < _pressUntilMs ? PickerButtonState.Pressed : PickerButtonState.Hover;
            }
            return PickerButtonState.Normal;
        }

        public List<SceneSprite> Build(long timeMs)
        {
            var sprites = new List<SceneSprite>();
            int order = 0;
            bool cjk = _paint.HasFonts;
            long age = AgeMs;
            PickerArt art = _pickerArt ?? PickerArt.None;
            ServerArtEntry serverArt = _server == null ? null : ServerArt.Find(_server.Name);

            art.AddBackground(sprites, ref order, _art, _scene, age, "channel-background");
            if (serverArt != null)
            {
                art.AddFrame(sprites, ref order, ServerArt.ChannelBackgroundImage, serverArt.ChannelIllustrationFrame, IllustrationX, IllustrationY, "channel-illustration");
            }
            art.AddBoard(sprites, ref order, "channel-board");
            art.AddFrame(sprites, ref order, ServerArt.ChannelBaseImage, TitleFrame, TitleX, TitleY, "channel-title");
            art.AddBanner(sprites, ref order, _paint, serverArt, _server == null ? string.Empty : _server.Name, "SERVER");
            art.AddFrame(sprites, ref order, ServerArt.ChannelBaseImage, StripFrame, ServerPickerScene.StripX, ServerPickerScene.StripY, "channel-strip");
            AddStripGlow(sprites, ref order, age);
            art.AddFrame(sprites, ref order, ServerArt.ChannelBaseImage, RuleFrame, RuleX, RuleY, "channel-rule");

            switch (Phase)
            {
                case PickerPhase.Ready:
                    AddItems(sprites, ref order, art, cjk);
                    AddPager(sprites, ref order, cjk);
                    break;
                case PickerPhase.Loading:
                    AddMessage(sprites, ref order, PickerLabel.ChannelListLoading, true, timeMs, cjk);
                    break;
                case PickerPhase.Empty:
                    AddMessage(sprites, ref order, PickerLabel.ChannelListEmpty, false, timeMs, cjk);
                    AddExitButtons(sprites, ref order, art, cjk);
                    break;
                default:
                    AddMessage(sprites, ref order, PickerLabel.ChannelListFailed, false, timeMs, cjk);
                    AddExitButtons(sprites, ref order, art, cjk);
                    break;
            }
            AddRefresh(sprites, ref order, art, cjk);
            bool idle = Busy == ChannelBusy.None;
            art.AddSmallButton(sprites, ref order, _art, _paint, ChangeServerRect(), ServerPickerText.Label(PickerLabel.ChangeServer, cjk),
                SmallState(Target.Change, idle), "channel-change");
            art.AddSmallButton(sprites, ref order, _art, _paint, QuitRect(), ServerPickerText.Label(PickerLabel.QuitGame, cjk),
                SmallState(Target.Quit, true), "channel-quit");
            AddStatus(sprites, ref order, timeMs, cjk);
            art.AddStartButton(sprites, ref order, _art, StartState(), age, "channel-start");
            return sprites;
        }

        private void AddStripGlow(List<SceneSprite> into, ref int order, long age)
        {
            SceneSprite glow = _scene?.StripGlow?.Sprite(age, order);
            if (glow != null)
            {
                into.Add(glow);
                order++;
            }
        }

        private void AddItems(List<SceneSprite> into, ref int order, PickerArt art, bool cjk)
        {
            int first = Page * PageSize;
            for (int i = first; i < _channels.Count && i < first + PageSize; i++)
            {
                if (!TryItemRect(i, out HitRect rect))
                {
                    continue;
                }
                DirectoryChannel channel = _channels[i];
                bool enterable = channel.CanEnter;
                bool selected = i == Selected;
                if (!art.AddFrame(into, ref order, ServerArt.ChannelSlotImage, ItemFrame, rect.X, rect.Y, "channel-item"))
                {
                    var plate = new ShellBox(0xFF21324Eu, 0xFF121C30u, 0xFF3F68A8u, 0xFF2D4C7Eu, 1, 0, 40);
                    OnlineShellArt.Add(into, ref order, _paint.Box(ItemWidth, ItemHeight, 3, plate), rect.X, rect.Y, "channel-item-plate");
                }
                if (selected)
                {
                    if (!art.AddFrame(into, ref order, ServerArt.ChannelBaseImage, SelectFrame, rect.X, rect.Y, "channel-item-select", true))
                    {
                        var edge = new ShellBox(0, 0, 0xFF4DB8FFu, 0xFF4DB8FFu, 2);
                        OnlineShellArt.Add(into, ref order, _paint.Box(ItemWidth, ItemHeight, 3, edge), rect.X, rect.Y, "channel-item-select-edge");
                    }
                }
                else if (i == _hoverItem && enterable && Busy == ChannelBusy.None)
                {
                    art.AddFrame(into, ref order, ServerArt.ChannelBaseImage, SelectFrame, rect.X, rect.Y, "channel-item-hover", true, 110);
                }
                AddItemTexts(into, ref order, channel, rect, cjk);
                if (!enterable || Busy != ChannelBusy.None)
                {
                    int alpha = enterable ? 0x70 : 0xA0;
                    var shade = new ShellBox((uint)alpha << 24, (uint)alpha << 24, 0, 0, 0);
                    OnlineShellArt.Add(into, ref order, _paint.Box(ItemWidth, ItemHeight, 2, shade), rect.X, rect.Y, enterable ? "channel-item-busy" : "channel-item-dim");
                }
            }
        }

        private void AddItemTexts(List<SceneSprite> into, ref int order, DirectoryChannel channel, HitRect rect, bool cjk)
        {
            SpriteFrame title = _paint.Line(ServerPickerText.ChannelTitle(channel, cjk), TextPixel, PickerArt.ItemTextColor, ShellPaint.Navy, TextMaxWidth);
            OnlineShellArt.Add(into, ref order, title, rect.X + TitleDX - 1, rect.Y + TitleDY - 2, "channel-item-title");
            string level = ServerPickerText.Level(channel);
            if (level.Length > 0)
            {
                SpriteFrame levelLine = _paint.Line(level, TextPixel, PickerArt.ItemTextColor, ShellPaint.Navy, TextMaxWidth);
                OnlineShellArt.Add(into, ref order, levelLine, rect.X + TitleDX - 1, rect.Y + LevelDY - 2, "channel-item-level");
            }
            SpriteFrame word = _paint.Line(ServerPickerText.Congestion(channel.Congestion, cjk), TextPixel,
                ServerPickerText.CongestionColor(channel.Congestion), ShellPaint.Navy, 56);
            if (word != null)
            {
                OnlineShellArt.Add(into, ref order, word, rect.X + CongestionRightDX - word.Width + 1, rect.Y + CongestionDY - 2, "channel-item-congestion");
            }
        }

        private void AddPager(List<SceneSprite> into, ref int order, bool cjk)
        {
            if (PageCount <= 1)
            {
                return;
            }
            bool idle = Busy == ChannelBusy.None;
            bool canPrev = idle && Page > 0, canNext = idle && Page < PageCount - 1;
            HitRect prev = PrevPageRect(), next = NextPageRect();
            OnlineShellArt.Add(into, ref order, PickerArt.Arrow(true, canPrev, _hover == Target.PrevPage), prev.X, prev.Y, "channel-page-prev");
            OnlineShellArt.Add(into, ref order, PickerArt.Arrow(false, canNext, _hover == Target.NextPage), next.X, next.Y, "channel-page-next");
            SpriteFrame label = _paint.Line(ServerPickerText.Page(Page, PageCount), 12, ShellPaint.Muted, ShellPaint.Navy);
            if (label != null)
            {
                OnlineShellArt.Add(into, ref order, label, PagerCenterX - label.Width / 2, PagerY + (PagerArrowHeight - label.Height) / 2, "channel-page-label");
            }
        }

        private void AddRefresh(List<SceneSprite> into, ref int order, PickerArt art, bool cjk)
        {
            PickerButtonState state = SmallState(Target.Refresh, CanRefresh);
            int frame;
            switch (state)
            {
                case PickerButtonState.Disabled: frame = RefreshDisabledFrame; break;
                case PickerButtonState.Pressed: frame = RefreshPressedFrame; break;
                case PickerButtonState.Hover: frame = RefreshHoverFrame; break;
                default: frame = RefreshNormalFrame; break;
            }
            if (art.AddFrame(into, ref order, ServerArt.ChannelBaseImage, frame, RefreshX, RefreshY, "channel-refresh"))
            {
                return;
            }
            SpriteFrame text = _paint.Line(cjk ? "刷新" : "REFRESH", 12, state == PickerButtonState.Disabled ? ShellPaint.Faint : ShellPaint.Green, ShellPaint.Navy);
            OnlineShellArt.Add(into, ref order, text, RefreshX + 3, RefreshY + 3, "channel-refresh-text");
        }

        private void AddMessage(List<SceneSprite> into, ref int order, PickerLabel label, bool spinner, long timeMs, bool cjk)
        {
            if (spinner)
            {
                OnlineShellArt.Add(into, ref order, _paint.Spinner(32, OnlineShellArt.SpinnerPhase(timeMs), ShellPaint.Gold), ContentCenterX - 16, MessageY - 44, "channel-spinner");
            }
            SpriteFrame text = _paint.Line(ServerPickerText.Label(label, cjk), 16, ShellPaint.Ink, ShellPaint.Navy, 420, true);
            if (text != null)
            {
                OnlineShellArt.Add(into, ref order, text, ContentCenterX - text.Width / 2, MessageY, "channel-message");
            }
        }

        private void AddExitButtons(List<SceneSprite> into, ref int order, PickerArt art, bool cjk)
        {
            bool idle = Busy == ChannelBusy.None;
            art.AddSmallButton(into, ref order, _art, _paint, RetryRect(), ServerPickerText.Label(PickerLabel.RetryButton, cjk),
                SmallState(Target.Retry, idle), "channel-retry");
            art.AddSmallButton(into, ref order, _art, _paint, DirectRect(), ServerPickerText.Label(PickerLabel.DirectButton, cjk),
                SmallState(Target.Direct, idle), "channel-direct");
        }

        /// <summary>提示条:忙着时是等待提示(转圈),否则是流程给的提示或界面自己的说明;都没有什么也不画。</summary>
        private void AddStatus(List<SceneSprite> into, ref int order, long timeMs, bool cjk)
        {
            string text;
            ShellTone tone;
            if (Busy != ChannelBusy.None)
            {
                text = ServerPickerText.Label(Busy == ChannelBusy.Connecting ? PickerLabel.ChannelConnecting : PickerLabel.ChannelRefreshing, cjk);
                tone = ShellTone.Wait;
            }
            else if (Notice != null)
            {
                text = OnlineShellText.Notice(Notice, cjk);
                tone = OnlineShellArt.ToneOf(Notice);
            }
            else if (_hint.HasValue)
            {
                text = ServerPickerText.Label(_hint.Value, cjk);
                tone = ShellTone.Info;
            }
            else
            {
                return;
            }
            int width = OnlineShellArt.NoticeBarWidth(_paint, text, 560);
            if (width <= 0)
            {
                return;
            }
            int y = Phase == PickerPhase.Failed || Phase == PickerPhase.Empty ? ExitButtonsY - 44 : NoticeY;
            OnlineShellArt.AddNoticeBar(into, ref order, _paint, tone, text, ContentCenterX - width / 2, y, width, timeMs, "channel-notice");
        }
    }
}
