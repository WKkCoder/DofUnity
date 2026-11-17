using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>选服务器、选频道两张界面的内容状态。</summary>
    public enum PickerPhase
    {
        /// <summary>正在读目录(没有内容可选)。</summary>
        Loading = 0,

        /// <summary>有内容可选。</summary>
        Ready = 1,

        /// <summary>目录读到了,但里面没有服务器(或这个服务器没有频道)。</summary>
        Empty = 2,

        /// <summary>目录读失败(原因在 <see cref="ServerSelectScreen.Notice"/>)。</summary>
        Failed = 3,
    }

    /// <summary>选服务器界面攒下的、等阶段机去办的事(<see cref="ServerSelectScreen.TakeAction"/> 取走即清)。</summary>
    public enum ServerSelectAction
    {
        None = 0,

        /// <summary>用选中的服务器进入选频道("游戏开始")。</summary>
        Choose = 1,

        /// <summary>返回(Esc):回登录。</summary>
        Back = 2,

        /// <summary>重新读目录。</summary>
        Retry = 3,

        /// <summary>目录读不到时的出口:不选了,直接连默认频道。</summary>
        DirectConnect = 4,
    }

    /// <summary>
    /// 选服务器界面(基线 S02-client-00 到 02,<see cref="ISceneSource"/>,叠在 Play 层):选角界面同一张整屏背景与光束、50×28 的窗口边框里一块更暗的面板,
    /// 面板左边是选中服务器的大插画,右边是"选择你的命运"标题、一行说明、一排服务器入口(每个入口一张牌,选中的发光、有活动的挂 EVENT 角标),
    /// 顶部是"选择服务器"标题与服务器名横幅,底下是"游戏开始"大按钮。素材与位置见 <see cref="PickerArt"/>、<see cref="ServerArt"/>。
    /// <list type="bullet">
    /// <item>入口来自目录(<see cref="SetDirectory"/>):每个服务器一个入口,2 列 × 5 行(基线只有一个入口,多出来的排法是自己定的,"无基线、非对拍"),
    /// 超过 10 个时右边出现上下滚动箭头;方向键在能选的入口之间移动,选中项始终滚进可见区域。</item>
    /// <item>没有频道的服务器画成灰牌、不能选;"游戏开始"在没有选中项、或正在读目录时是灰的、点了没反应。</item>
    /// <item>目录是空的:面板里写"没有可用的服务器";读失败:写原因,给"重试"与"默认频道"两个出口(回车重试、空格直连默认频道、Esc 回登录)。</item>
    /// <item>界面只攒意图(<see cref="TakeAction"/>),阶段在阶段机的 Poll 里才变;时间由宿主用 <see cref="Advance"/> 推进(光晕与光束的动画按它走)。</item>
    /// </list>
    /// 没有图包(<see cref="ServerPickerScene"/> 为 null 或缺帧)时,背景铺纯色、入口画程序底板与文字,逻辑完全一样。没有字体集时文字退回英文点阵占位。
    /// </summary>
    public sealed class ServerSelectScreen : ISceneSource
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

        // ----- 入口网格(原点是第一个入口的左上角:基线上唯一的入口就在这里)
        public const int EntryX = 332;

        public const int EntryY = 214;
        public const int EntryWidth = 170;
        public const int EntryHeight = 41;
        public const int EntryPitchX = 180;
        public const int EntryPitchY = 46;
        public const int Columns = 2;
        public const int VisibleRows = 5;
        public const int PageSize = Columns * VisibleRows;

        /// <summary>活动角标相对入口左上角的位置(基线:入口 (332,214),角标 (465,241))。</summary>
        public const int EventMarkDX = 133;

        public const int EventMarkDY = 27;

        // ----- 标题区(基线量出来的原点)
        public const int TopTitleX = 345;

        public const int TopTitleY = 33;
        public const int HeadingX = 388;
        public const int HeadingY = 138;
        public const int RuleX = 346;
        public const int RuleY = 202;
        public const int SubtitleX = 438;
        public const int SubtitleY = 187;

        /// <summary>插画的原点(server_bg.img 的帧)。</summary>
        public const int IllustrationX = 106;

        public const int IllustrationY = 129;

        // ----- 滚动箭头(无基线、非对拍)
        public const int ScrollX = 684;

        public const int ScrollUpY = 212;
        public const int ScrollDownY = 417;
        public const int ScrollWidth = 14;
        public const int ScrollHeight = 22;

        /// <summary>面板右半的水平中心(消息、提示条、出口按钮都居中在这里)。</summary>
        public const int ContentCenterX = 507;

        public const int MessageY = 290;
        public const int NoticeY = 446;
        public const int ExitButtonsY = 372;

        private const int PressMs = 140;

        // 热点
        private enum Target
        {
            None,
            Entry,
            Start,
            Retry,
            Direct,
            ScrollUp,
            ScrollDown,
        }

        private readonly OverlayArt _art;
        private readonly ShellPaint _paint;
        private readonly ServerPickerScene _scene;
        private readonly PickerArt _pickerArt;

        private IReadOnlyList<DirectoryServer> _servers = Array.Empty<DirectoryServer>();
        private ServerSelectAction _action;
        private Target _hover;
        private int _hoverEntry = -1;
        private double _ageMs;
        private double _pressUntilMs = -1;

        /// <param name="scene">素材;为 null 时画占位(纯色底、程序板)。</param>
        /// <param name="fonts">这张画面自己的字体集(测试用);为 null 时用全进程共用的。</param>
        public ServerSelectScreen(ServerPickerScene scene = null, IGlyphFonts fonts = null)
        {
            _scene = scene;
            _pickerArt = scene == null ? null : PickerArt.For(scene.Sprites);
            _art = new OverlayArt { Localize = false, Fonts = fonts };
            _paint = new ShellPaint(_art);
            Phase = PickerPhase.Loading;
            Selected = -1;
        }

        public PickerPhase Phase { get; private set; }

        /// <summary>选中的服务器在目录里的序号;没有为 -1。</summary>
        public int Selected { get; private set; }

        /// <summary>第一行可见入口是第几行(从 0 起)。</summary>
        public int ScrollRow { get; private set; }

        /// <summary>现在在显示的提示(读目录失败的原因等);没有为 null。</summary>
        public OnlineNotice Notice { get; private set; }

        public IReadOnlyList<DirectoryServer> Servers => _servers;

        /// <summary>选中的服务器;没有为 null。</summary>
        public DirectoryServer SelectedServer => Selected >= 0 && Selected < _servers.Count ? _servers[Selected] : null;

        /// <summary>界面打开以来的毫秒数(<see cref="Advance"/> 累加)。</summary>
        public long AgeMs => (long)_ageMs;

        /// <summary>目录里一共有几行入口。</summary>
        public int RowCount => (_servers.Count + Columns - 1) / Columns;

        /// <summary>"游戏开始"现在能不能点:目录读到了、选中了一个能选的服务器。</summary>
        public bool CanStart => Phase == PickerPhase.Ready && SelectedServer != null && SelectedServer.IsSelectable;

        // ------------------------------------------------------------------ 内容

        /// <summary>开始(重新)读目录:显示"正在读"。已有的服务器列表与选中项保留着(读完时按原来的选中项重新选)。</summary>
        public void SetLoading()
        {
            Phase = PickerPhase.Loading;
            _action = ServerSelectAction.None;
        }

        /// <summary>目录读到了:空目录进入"没有服务器"状态,否则选中 <paramref name="select"/>(不能选就往后找第一个能选的)。</summary>
        public void SetDirectory(ServerDirectory directory, int select = 0)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }
            _servers = directory.Servers;
            _action = ServerSelectAction.None;
            Notice = null;
            if (_servers.Count == 0)
            {
                Phase = PickerPhase.Empty;
                Selected = -1;
                ScrollRow = 0;
                return;
            }
            Phase = PickerPhase.Ready;
            Selected = FirstSelectableFrom(Math.Max(0, Math.Min(select, _servers.Count - 1)));
            ScrollRow = 0;
            EnsureVisible();
        }

        /// <summary>目录读失败了:面板里显示失败,原因由 <see cref="SetNotice"/> 给。</summary>
        public void SetFailed()
        {
            Phase = PickerPhase.Failed;
            _action = ServerSelectAction.None;
        }

        /// <summary>要显示的提示;null 清掉。</summary>
        public void SetNotice(OnlineNotice notice)
        {
            Notice = notice;
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
        /// 方向键:<paramref name="dx"/> 列、<paramref name="dy"/> 行(每次只会有一个不为 0);按目录顺序一格一格找下一个能选的入口,到头不循环。
        /// 还没有选中项时先选第一个能选的。只在有内容可选时响应。
        /// </summary>
        public void Move(int dx, int dy = 0)
        {
            if (Phase != PickerPhase.Ready)
            {
                return;
            }
            if (Selected < 0)
            {
                Selected = FirstSelectableFrom(0);
                EnsureVisible();
                return;
            }
            int step = dx != 0 ? Math.Sign(dx) : Math.Sign(dy) * Columns;
            if (step == 0)
            {
                return;
            }
            for (int i = Selected + step; i >= 0 && i < _servers.Count; i += step)
            {
                if (_servers[i].IsSelectable)
                {
                    Selected = i;
                    EnsureVisible();
                    return;
                }
            }
        }

        /// <summary>回车、空格或点"游戏开始":有内容可选时选定当前服务器;读失败或目录为空时是重试。读目录期间没有反应。</summary>
        public void Confirm()
        {
            switch (Phase)
            {
                case PickerPhase.Ready:
                    if (CanStart)
                    {
                        _action = ServerSelectAction.Choose;
                    }
                    break;
                case PickerPhase.Failed:
                case PickerPhase.Empty:
                    _action = ServerSelectAction.Retry;
                    break;
            }
        }

        /// <summary>Esc:返回。任何状态都响应。</summary>
        public void Back()
        {
            _action = ServerSelectAction.Back;
        }

        /// <summary>读失败或目录为空时的"重试"。</summary>
        public void Retry()
        {
            if (Phase == PickerPhase.Failed || Phase == PickerPhase.Empty)
            {
                _action = ServerSelectAction.Retry;
            }
        }

        /// <summary>读失败或目录为空时的"直连默认频道"。</summary>
        public void DirectConnect()
        {
            if (Phase == PickerPhase.Failed || Phase == PickerPhase.Empty)
            {
                _action = ServerSelectAction.DirectConnect;
            }
        }

        /// <summary>取走攒下的事(没有返回 <see cref="ServerSelectAction.None"/>)。取走即清:同一件事只办一次。</summary>
        public ServerSelectAction TakeAction()
        {
            ServerSelectAction action = _action;
            _action = ServerSelectAction.None;
            return action;
        }

        /// <summary>
        /// 鼠标(触摸)点在画布坐标 (<paramref name="x"/>, <paramref name="y"/>):点入口选中它(灰牌不响应)、点滚动箭头翻行、点"游戏开始"选定、
        /// 失败或空目录时点"重试"/"默认频道"。返回这一下有没有落在界面元素上。
        /// </summary>
        public bool Click(int x, int y)
        {
            Target target = HitTest(x, y, out int entry);
            switch (target)
            {
                case Target.Entry:
                    if (_servers[entry].IsSelectable)
                    {
                        Selected = entry;
                        EnsureVisible();
                    }
                    return true;
                case Target.Start:
                    if (CanStart)
                    {
                        _pressUntilMs = _ageMs + PressMs;
                        _action = ServerSelectAction.Choose;
                    }
                    return true;     // 灰着的"游戏开始"吃掉这一下、什么也不办
                case Target.Retry:
                    _pressUntilMs = _ageMs + PressMs;
                    Retry();
                    return true;
                case Target.Direct:
                    _pressUntilMs = _ageMs + PressMs;
                    DirectConnect();
                    return true;
                case Target.ScrollUp:
                    Scroll(-1);
                    return true;
                case Target.ScrollDown:
                    Scroll(1);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>鼠标指针在 (<paramref name="x"/>, <paramref name="y"/>) 上(只影响悬停的画法)。</summary>
        public void Hover(int x, int y)
        {
            _hover = HitTest(x, y, out int entry);
            _hoverEntry = _hover == Target.Entry ? entry : -1;
        }

        /// <summary>指针移出了画面(没有悬停)。</summary>
        public void HoverNone()
        {
            _hover = Target.None;
            _hoverEntry = -1;
        }

        // ------------------------------------------------------------------ 几何

        /// <summary>第 <paramref name="index"/> 个服务器的入口矩形;不在可见行里返回 false。</summary>
        public bool TryEntryRect(int index, out HitRect rect)
        {
            rect = new HitRect(0, 0, 0, 0);
            if (index < 0 || index >= _servers.Count)
            {
                return false;
            }
            int row = index / Columns - ScrollRow;
            if (row < 0 || row >= VisibleRows)
            {
                return false;
            }
            rect = new HitRect(EntryX + EntryPitchX * (index % Columns), EntryY + EntryPitchY * row, EntryWidth, EntryHeight);
            return true;
        }

        /// <summary>"重试"小按钮的矩形(读失败或目录为空时)。</summary>
        public static HitRect RetryRect()
        {
            return new HitRect(ContentCenterX - PickerArt.SmallButtonWidth - 6, ExitButtonsY, PickerArt.SmallButtonWidth, PickerArt.SmallButtonHeight);
        }

        /// <summary>"默认频道"小按钮的矩形。</summary>
        public static HitRect DirectRect()
        {
            return new HitRect(ContentCenterX + 6, ExitButtonsY, PickerArt.SmallButtonWidth, PickerArt.SmallButtonHeight);
        }

        public static HitRect ScrollUpRect()
        {
            return new HitRect(ScrollX, ScrollUpY, ScrollWidth, ScrollHeight);
        }

        public static HitRect ScrollDownRect()
        {
            return new HitRect(ScrollX, ScrollDownY, ScrollWidth, ScrollHeight);
        }

        private bool CanScrollUp => Phase == PickerPhase.Ready && ScrollRow > 0;

        private bool CanScrollDown => Phase == PickerPhase.Ready && ScrollRow < RowCount - VisibleRows;

        private Target HitTest(int x, int y, out int entry)
        {
            entry = -1;
            if (PickerArt.StartRect().Contains(x, y))
            {
                return Target.Start;
            }
            if (Phase == PickerPhase.Failed || Phase == PickerPhase.Empty)
            {
                if (RetryRect().Contains(x, y))
                {
                    return Target.Retry;
                }
                if (DirectRect().Contains(x, y))
                {
                    return Target.Direct;
                }
                return Target.None;
            }
            if (Phase != PickerPhase.Ready)
            {
                return Target.None;
            }
            if (ScrollUpRect().Contains(x, y) && RowCount > VisibleRows)
            {
                return Target.ScrollUp;
            }
            if (ScrollDownRect().Contains(x, y) && RowCount > VisibleRows)
            {
                return Target.ScrollDown;
            }
            for (int i = 0; i < _servers.Count; i++)
            {
                if (TryEntryRect(i, out HitRect rect) && rect.Contains(x, y))
                {
                    entry = i;
                    return Target.Entry;
                }
            }
            return Target.None;
        }

        // ------------------------------------------------------------------ 选中与滚动

        private int FirstSelectableFrom(int start)
        {
            for (int i = start; i < _servers.Count; i++)
            {
                if (_servers[i].IsSelectable)
                {
                    return i;
                }
            }
            for (int i = Math.Min(start, _servers.Count) - 1; i >= 0; i--)
            {
                if (_servers[i].IsSelectable)
                {
                    return i;
                }
            }
            return -1;
        }

        private void Scroll(int rows)
        {
            int max = Math.Max(0, RowCount - VisibleRows);
            ScrollRow = Math.Max(0, Math.Min(ScrollRow + rows, max));
        }

        private void EnsureVisible()
        {
            if (Selected < 0)
            {
                return;
            }
            int row = Selected / Columns;
            if (row < ScrollRow)
            {
                ScrollRow = row;
            }
            else if (row >= ScrollRow + VisibleRows)
            {
                ScrollRow = row - VisibleRows + 1;
            }
            int max = Math.Max(0, RowCount - VisibleRows);
            ScrollRow = Math.Max(0, Math.Min(ScrollRow, max));
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

        public List<SceneSprite> Build(long timeMs)
        {
            var sprites = new List<SceneSprite>();
            int order = 0;
            bool cjk = _paint.HasFonts;
            long age = AgeMs;
            PickerArt art = _pickerArt ?? PickerArt.None;
            DirectoryServer server = Phase == PickerPhase.Ready ? SelectedServer : null;
            ServerArtEntry serverArt = server == null ? null : ServerArt.Find(server.Name);

            art.AddBackground(sprites, ref order, _art, _scene, age, "server-background");
            art.AddBoard(sprites, ref order, "server-board");
            art.AddDimPanel(sprites, ref order, "server-panel");
            if (serverArt != null)
            {
                art.AddFrame(sprites, ref order, ServerArt.BackgroundImage, serverArt.BackgroundFrame, IllustrationX, IllustrationY, "server-illustration");
            }
            art.AddBanner(sprites, ref order, _paint, serverArt, server == null ? string.Empty : server.Name,
                server == null ? string.Empty : ServerPickerText.ServerPlaceholder(Selected));
            art.AddFrame(sprites, ref order, ServerArt.BaseImage, 6, TopTitleX, TopTitleY, "server-title");
            art.AddFrame(sprites, ref order, ServerArt.BaseImage, 0, HeadingX, HeadingY, "server-heading");
            art.AddFrame(sprites, ref order, ServerArt.BaseImage, 1, HeadingX, HeadingY, "server-heading-glow", true, PickerArt.TitleGlowTint(age));   // 光晕盖在字上面(基线逐像素验证)
            art.AddFrame(sprites, ref order, ServerArt.BaseImage, 2, RuleX, RuleY, "server-rule", true);
            SpriteFrame subtitle = _paint.Line(ServerPickerText.Label(PickerLabel.ServerSubtitle, cjk), 12, PickerArt.SubtitleColor, ShellPaint.Navy);
            OnlineShellArt.Add(sprites, ref order, subtitle, SubtitleX, SubtitleY, "server-subtitle");

            switch (Phase)
            {
                case PickerPhase.Ready:
                    AddEntries(sprites, ref order, art, cjk, age);
                    break;
                case PickerPhase.Loading:
                    AddMessage(sprites, ref order, PickerLabel.ServerListLoading, true, timeMs, cjk);
                    break;
                case PickerPhase.Empty:
                    AddMessage(sprites, ref order, PickerLabel.ServerListEmpty, false, timeMs, cjk);
                    AddExitButtons(sprites, ref order, art);
                    break;
                default:
                    AddMessage(sprites, ref order, PickerLabel.ServerListFailed, false, timeMs, cjk);
                    AddExitButtons(sprites, ref order, art);
                    break;
            }
            AddNotice(sprites, ref order, timeMs, cjk);
            art.AddStartButton(sprites, ref order, _art, StartState(), age, "server-start");
            return sprites;
        }

        private void AddEntries(List<SceneSprite> into, ref int order, PickerArt art, bool cjk, long age)
        {
            for (int i = 0; i < _servers.Count; i++)
            {
                if (!TryEntryRect(i, out HitRect rect))
                {
                    continue;
                }
                DirectoryServer server = _servers[i];
                ServerArtEntry serverArt = ServerArt.Find(server.Name);
                bool selected = i == Selected;
                bool hover = i == _hoverEntry && !selected && server.IsSelectable;
                AddEntryFace(into, ref order, art, cjk, i, rect, server, serverArt);
                if (selected)
                {
                    if (!art.AddFrame(into, ref order, ServerArt.BaseImage, 5, rect.X, rect.Y, "server-entry-glow", true))
                    {
                        var edge = new ShellBox(0, 0, 0xFFE9C46Au, 0xFFE9C46Au, 2);
                        OnlineShellArt.Add(into, ref order, _paint.Box(EntryWidth, EntryHeight, 4, edge), rect.X, rect.Y, "server-entry-select");
                    }
                }
                else if (hover)
                {
                    art.AddFrame(into, ref order, ServerArt.BaseImage, 5, rect.X, rect.Y, "server-entry-hover", true, 110);
                }
                if (server.HasEventMark)
                {
                    // 角标底下垫一圈加法光(基线逐像素验证:强度与标题光晕是同一个呼吸值)
                    art.AddFrame(into, ref order, ServerArt.BaseImage, 4, rect.X + EventMarkDX, rect.Y + EventMarkDY, "server-entry-event-glow", true, PickerArt.TitleGlowTint(age));
                    art.AddFrame(into, ref order, ServerArt.BaseImage, 3, rect.X + EventMarkDX, rect.Y + EventMarkDY, "server-entry-event");
                }
            }
            if (RowCount > VisibleRows)
            {
                AddScrollArrows(into, ref order, art);
            }
        }

        private void AddEntryFace(List<SceneSprite> into, ref int order, PickerArt art, bool cjk, int index, HitRect rect, DirectoryServer server, ServerArtEntry serverArt)
        {
            bool selectable = server.IsSelectable;
            int frame = serverArt == null ? -1 : (selectable ? serverArt.SlotFrame : (serverArt.SlotGrayFrame >= 0 ? serverArt.SlotGrayFrame : serverArt.SlotFrame));
            bool drawn = art.AddFrame(into, ref order, ServerArt.SlotImage, frame, rect.X, rect.Y, selectable ? "server-entry" : "server-entry-gray");
            if (drawn)
            {
                if (!selectable && serverArt.SlotGrayFrame < 0)
                {
                    var shade = new ShellBox(0xB4000000u, 0xB4000000u, 0, 0, 0);
                    OnlineShellArt.Add(into, ref order, _paint.Box(EntryWidth, EntryHeight, 2, shade), rect.X, rect.Y, "server-entry-dim");
                }
                return;
            }
            // 没有这个服务器的素材:程序画的底板与名字
            var plate = selectable
                ? new ShellBox(0xFF21324Eu, 0xFF121C30u, 0xFF6F98D4u, 0xFF2D4875u, 1, 0, 60)
                : new ShellBox(0xFF2A2F38u, 0xFF1A1E26u, 0xFF4A505Cu, 0xFF30353Fu, 1);
            OnlineShellArt.Add(into, ref order, _paint.Box(EntryWidth, EntryHeight, 4, plate), rect.X, rect.Y, "server-entry-plate");
            string name = cjk || IsAscii(server.Name) ? server.Name : ServerPickerText.ServerPlaceholder(index);
            SpriteFrame line = _paint.Line(name, 14, selectable ? ShellPaint.GoldPale : ShellPaint.Muted, ShellPaint.Navy, EntryWidth - 20, true);
            if (line != null)
            {
                OnlineShellArt.Add(into, ref order, line, rect.X + 10, rect.Y + (EntryHeight - line.Height) / 2, "server-entry-name");
            }
            if (!selectable)
            {
                SpriteFrame none = _paint.Line(ServerPickerText.Label(PickerLabel.ServerNoChannels, cjk), 12, ShellPaint.Faint, ShellPaint.Navy);
                OnlineShellArt.Add(into, ref order, none, rect.X + EntryWidth - 10 - (none == null ? 0 : none.Width), rect.Y + EntryHeight - 18, "server-entry-note");
            }
        }

        private void AddScrollArrows(List<SceneSprite> into, ref int order, PickerArt art)
        {
            bool up = CanScrollUp, down = CanScrollDown;
            art.AddFrame(into, ref order, PickerArt.WindowImage, CharacterSelectArt.ScrollUpFrame, ScrollX + 2, ScrollUpY + 2, "server-scroll-up");
            art.AddFrame(into, ref order, PickerArt.WindowImage, CharacterSelectArt.ScrollDownFrame, ScrollX + 2, ScrollDownY + ScrollHeight - 15, "server-scroll-down");
            if (!up)
            {
                OnlineShellArt.Add(into, ref order, _paint.Box(10, 14, 1, new ShellBox(0x90000000u, 0x90000000u, 0, 0, 0)), ScrollX + 2, ScrollUpY + 2, "server-scroll-up-dim");
            }
            if (!down)
            {
                OnlineShellArt.Add(into, ref order, _paint.Box(10, 14, 1, new ShellBox(0x90000000u, 0x90000000u, 0, 0, 0)), ScrollX + 2, ScrollDownY + ScrollHeight - 15, "server-scroll-down-dim");
            }
        }

        private void AddMessage(List<SceneSprite> into, ref int order, PickerLabel label, bool spinner, long timeMs, bool cjk)
        {
            if (spinner)
            {
                OnlineShellArt.Add(into, ref order, _paint.Spinner(32, OnlineShellArt.SpinnerPhase(timeMs), ShellPaint.Gold), ContentCenterX - 16, MessageY - 44, "server-spinner");
            }
            SpriteFrame text = _paint.Line(ServerPickerText.Label(label, cjk), 16, ShellPaint.Ink, ShellPaint.Navy, 340, true);
            if (text != null)
            {
                OnlineShellArt.Add(into, ref order, text, ContentCenterX - text.Width / 2, MessageY, "server-message");
            }
        }

        private void AddExitButtons(List<SceneSprite> into, ref int order, PickerArt art)
        {
            bool cjk = _paint.HasFonts;
            PickerButtonState retry = _ageMs < _pressUntilMs && _hover == Target.Retry ? PickerButtonState.Pressed
                : (_hover == Target.Retry ? PickerButtonState.Hover : PickerButtonState.Normal);
            PickerButtonState direct = _ageMs < _pressUntilMs && _hover == Target.Direct ? PickerButtonState.Pressed
                : (_hover == Target.Direct ? PickerButtonState.Hover : PickerButtonState.Normal);
            art.AddSmallButton(into, ref order, _art, _paint, RetryRect(), ServerPickerText.Label(PickerLabel.RetryButton, cjk), retry, "server-retry");
            art.AddSmallButton(into, ref order, _art, _paint, DirectRect(), ServerPickerText.Label(PickerLabel.DirectButton, cjk), direct, "server-direct");
        }

        private void AddNotice(List<SceneSprite> into, ref int order, long timeMs, bool cjk)
        {
            if (Notice == null)
            {
                return;
            }
            string text = OnlineShellText.Notice(Notice, cjk);
            int width = OnlineShellArt.NoticeBarWidth(_paint, text, 440);
            if (width <= 0)
            {
                return;
            }
            int y = Phase == PickerPhase.Ready || Phase == PickerPhase.Loading ? NoticeY : MessageY + 40;
            OnlineShellArt.AddNoticeBar(into, ref order, _paint, OnlineShellArt.ToneOf(Notice), text, ContentCenterX - width / 2, y, width, timeMs, "server-notice");
        }

        private static bool IsAscii(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return true;
            }
            foreach (char c in text)
            {
                if (c < 0x20 || c >= 0x7F)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
