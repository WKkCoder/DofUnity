using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>选角界面的结果。</summary>
    public enum CharacterSelectOutcome
    {
        /// <summary>还没选。</summary>
        None = 0,

        /// <summary>用选中的角色开始游戏(进城)。</summary>
        Start = 1,

        /// <summary>结束游戏。</summary>
        Quit = 2,
    }

    /// <summary>
    /// 选角界面(基线 S02:一排排底座上站着账号里的角色,选中的亮着发光框,底下"游戏开始"按钮,两侧有创立、删除、选择频道、结束游戏的小按钮)。
    /// 有 <see cref="CharacterSelectScene"/> 时画原版的界面(<see cref="CharacterSelectArt"/>,证据 E-096),开场之后光束与头像的动画按
    /// <see cref="AgeMs"/> 走(由宿主用 <see cref="Advance"/> 推进);没有时是一张占位画面(深色底、像素字名单)。
    /// 逻辑两种模式一样:方向键在有角色的格子之间移动选中项(到头不循环),确认(回车、空格、点"游戏开始")用选中的角色开始游戏,
    /// 返回(Esc、点"结束游戏")是结束游戏;鼠标点角色的格子选中它;创立、删除、选择频道三个按钮单机里没有对应的功能,点了没有反应。
    /// 原版键盘怎么操作选角没有读到客户端代码,这里的键位是按界面形态推的。
    /// <para>
    /// 联网模式(<see cref="Online"/>,名册来自 <see cref="OnlineCharacterRoster"/>):文字走文字系统(有字体是中文,没有是英文占位),
    /// 角色多于 <see cref="CharacterRoster.Capacity"/> 个时按行滚动(方向键跟着选中项走,滚动条的箭头与轨道可以点),
    /// 选中不能进入的角色点开始只弹提示、不给结果(<see cref="CharacterBlock"/>),创立与删除两个按钮灰着、点了提示"暂未开放",
    /// 点了开始之后(<see cref="CharacterSelectOutcome.Start"/>)开始按钮变灰、画面上写"正在进入…"(<see cref="Busy"/>)。
    /// 离线模式(公开的构造函数)与以前一模一样。
    /// </para>
    /// </summary>
    public sealed class CharacterSelectScreen : ISceneSource
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

        private const uint BackgroundColor = 0x0A1220u;
        private const uint SelectionColor = 0x23406Au;

        private readonly OverlayArt _art = new OverlayArt();
        private readonly CharacterRoster _roster;
        private readonly CharacterSelectScene _scene;
        private readonly CharacterSelectArt _sceneArt;
        private readonly CharacterSelectView _view;          // 联网模式才有;离线是 null
        private double _ageMs;
        private double _promptLeftMs;
        private CharacterSelectPrompt _prompt;

        /// <summary>提示条显示多久(毫秒)。</summary>
        public const int PromptMs = 2400;

        public CharacterRoster Roster => _roster;

        /// <summary>选中的角色在名册里的序号;名册是空的时候是 -1。</summary>
        public int Index { get; private set; }

        public CharacterSelectOutcome Outcome { get; private set; }

        /// <summary>选中的角色;名册是空的时候为 null。</summary>
        public CharacterSlot Selected => Index >= 0 && Index < _roster.Count ? _roster[Index] : null;

        /// <summary>画原版界面时的画面数据;占位画面是 null。</summary>
        public CharacterSelectScene Scene => _scene;

        /// <summary>界面打开以来的毫秒数(<see cref="Advance"/> 累加),光束与头像的动画按它走。</summary>
        public long AgeMs => (long)_ageMs;

        public CharacterSelectScreen(CharacterRoster roster, CharacterSelectScene scene = null, int index = 0)
            : this(roster, scene, index, null)
        {
        }

        private CharacterSelectScreen(CharacterRoster roster, CharacterSelectScene scene, int index, CharacterSelectView view)
        {
            _roster = roster ?? throw new ArgumentNullException(nameof(roster));
            _scene = scene;
            _sceneArt = scene == null ? null : CharacterSelectArt.For(scene.Sprites);
            _view = view;
            Index = _roster.Count == 0 ? -1 : Math.Max(0, Math.Min(index, _roster.Count - 1));
            KeepIndexVisible();
        }

        /// <summary>
        /// 联网模式的选角界面(见类说明):<paramref name="roster"/> 通常来自 <see cref="OnlineCharacterRoster.From"/>(可以多于 <see cref="CharacterRoster.Capacity"/> 个),
        /// <paramref name="index"/> 是一开始选中的序号(越界夹进名册),窗口自动滚到能看见它。
        /// <paramref name="fonts"/> 是这张画面自己的字体集(测试用);为 null 时用全进程共用的字体。
        /// </summary>
        public static CharacterSelectScreen Online(CharacterRoster roster, CharacterSelectScene scene, int index = 0, IGlyphFonts fonts = null)
        {
            return new CharacterSelectScreen(roster, scene, index, new CharacterSelectView(fonts));
        }

        /// <summary>联网模式。</summary>
        public bool IsOnline => _view != null;

        /// <summary>窗口里第一行是名册的第几行(0 起);离线模式恒为 0。</summary>
        public int FirstRow { get; private set; }

        /// <summary>已经点了"游戏开始"(<see cref="Outcome"/> 是 <see cref="CharacterSelectOutcome.Start"/>)、在等进城的结果:联网模式下开始按钮灰着、写着"正在进入…"。</summary>
        public bool Busy => _view != null && Outcome == CharacterSelectOutcome.Start;

        /// <summary>正在显示的提示(显示 <see cref="PromptMs"/> 毫秒,<see cref="Advance"/> 推进);没有是 <see cref="CharacterSelectPrompt.None"/>。离线模式恒为 None。</summary>
        public CharacterSelectPrompt Prompt => _promptLeftMs > 0.0 ? _prompt : CharacterSelectPrompt.None;

        /// <summary>界面的时间往前走 <paramref name="deltaMs"/> 毫秒(负数当 0)。</summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs > 0.0)
            {
                _ageMs += deltaMs;
                _promptLeftMs = Math.Max(0.0, _promptLeftMs - deltaMs);
            }
        }

        /// <summary>
        /// 选中项在网格里移动:<paramref name="dx"/> 列、<paramref name="dy"/> 行(各夹在 6 列 × 2 行的网格里,不循环;联网模式的行数是名册的行数,至少 2 行);
        /// 目标格子没有角色时停在名册里最后一个角色(竖着移时同列没有角色就不动)。联网模式下窗口跟着选中项滚(选中项出了窗口就把窗口挪到刚好看见它)。
        /// 已经有结果之后不再响应。
        /// </summary>
        public void Move(int dx, int dy = 0)
        {
            if (Outcome != CharacterSelectOutcome.None || _roster.Count == 0)
            {
                return;
            }
            CharacterRoster.Cell(Index, out int column, out int row);
            int rows = _view == null ? CharacterRoster.Rows : Math.Max(CharacterRoster.Rows, _roster.TotalRows);
            int newColumn = Math.Max(0, Math.Min(column + dx, CharacterRoster.Columns - 1));
            int newRow = Math.Max(0, Math.Min(row + dy, rows - 1));
            int target = newRow * CharacterRoster.Columns + newColumn;
            if (target >= _roster.Count)
            {
                if (dy != 0)
                {
                    return;                  // 竖着移到了没有角色的格子:不动
                }
                target = _roster.Count - 1;  // 横着移出了名册尾:停在最后一个角色
            }
            Index = target;
            KeepIndexVisible();
        }

        /// <summary>
        /// 窗口(联网模式)往下滚 <paramref name="rows"/> 行(负数往上),夹在 0 到 <see cref="CharacterRoster.MaxFirstRow"/> 之间。
        /// 选中项滚出窗口时把选中项挪到窗口里同一列的最近一行(不让看不见的角色被"游戏开始"带走)。真的滚动了返回 true;离线模式、没有可滚的行、已有结果时不响应。
        /// </summary>
        public bool Scroll(int rows)
        {
            if (_view == null || Outcome != CharacterSelectOutcome.None)
            {
                return false;
            }
            int next = Math.Max(0, Math.Min(FirstRow + rows, _roster.MaxFirstRow));
            if (next == FirstRow)
            {
                return false;
            }
            FirstRow = next;
            if (Index >= 0)
            {
                CharacterRoster.Cell(Index, out int column, out int row);
                int clamped = Math.Max(FirstRow, Math.Min(row, FirstRow + CharacterRoster.Rows - 1));
                if (clamped != row)
                {
                    Index = Math.Min(clamped * CharacterRoster.Columns + column, _roster.Count - 1);
                }
            }
            return true;
        }

        // 联网模式:选中项在窗口外时把窗口挪到刚好看见它(往上挪到它那一行在最上、往下挪到它那一行在最下)
        private void KeepIndexVisible()
        {
            if (_view == null || Index < 0)
            {
                return;
            }
            int row = Index / CharacterRoster.Columns;
            if (row < FirstRow)
            {
                FirstRow = row;
            }
            else if (row >= FirstRow + CharacterRoster.Rows)
            {
                FirstRow = row - CharacterRoster.Rows + 1;
            }
            FirstRow = Math.Max(0, Math.Min(FirstRow, _roster.MaxFirstRow));
        }

        private void ShowPrompt(CharacterSelectPrompt prompt)
        {
            _prompt = prompt;
            _promptLeftMs = PromptMs;
        }

        /// <summary>
        /// 用选中的角色开始游戏;名册是空的、或已经有结果时不响应。联网模式下选中的角色不能进入(<see cref="CharacterSlotOnline.CanEnter"/> 为 false)时
        /// 不给结果、只弹提示(<see cref="Prompt"/>),与客户端点"游戏开始"被拦住时一样不发选角命令。
        /// </summary>
        public void Confirm()
        {
            if (Outcome != CharacterSelectOutcome.None || Selected == null)
            {
                return;
            }
            CharacterSlotOnline online = Selected.Online;
            if (_view != null && online != null && !online.CanEnter)
            {
                ShowPrompt(CharacterSelectText.PromptFor(online.Block));
                return;
            }
            Outcome = CharacterSelectOutcome.Start;
        }

        /// <summary>结束游戏;已经有结果时不响应。</summary>
        public void Back()
        {
            if (Outcome == CharacterSelectOutcome.None)
            {
                Outcome = CharacterSelectOutcome.Quit;
            }
        }

        /// <summary>
        /// 鼠标(触摸)点在画布坐标 (<paramref name="x"/>, <paramref name="y"/>) 上:点"游戏开始"开始游戏、点"结束游戏"结束;点有角色的格子选中它;
        /// 点没有角色的格子、创立、删除、选择频道没有反应(单机里没有创角、删角与频道)。返回这一下有没有落在界面元素上。占位画面不响应点击。
        /// </summary>
        public bool Click(int x, int y)
        {
            if (_scene == null || Outcome != CharacterSelectOutcome.None)
            {
                return false;
            }
            switch (CharacterSelectArt.HitButton(x, y))
            {
                case CharacterSelectButton.Start:
                    Confirm();
                    return true;
                case CharacterSelectButton.Quit:
                    Back();
                    return true;
                case CharacterSelectButton.Create:
                case CharacterSelectButton.Delete:
                    if (_view != null)
                    {
                        ShowPrompt(CharacterSelectPrompt.NotAvailable);   // 联网版没有创角与删角:按钮灰着,点了提示
                    }
                    return true;
                case CharacterSelectButton.None:
                    break;
                default:
                    return true;
            }
            if (_view != null)
            {
                switch (CharacterSelectArt.HitScroll(x, y, FirstRow, _roster.TotalRows))
                {
                    case CharacterSelectScrollHit.Up:
                        Scroll(-1);
                        return true;
                    case CharacterSelectScrollHit.Down:
                        Scroll(1);
                        return true;
                    case CharacterSelectScrollHit.PageUp:
                        Scroll(-CharacterRoster.Rows);
                        return true;
                    case CharacterSelectScrollHit.PageDown:
                        Scroll(CharacterRoster.Rows);
                        return true;
                    case CharacterSelectScrollHit.Thumb:
                        return true;
                }
            }
            int cell = CharacterSelectArt.HitSlot(x, y);
            if (cell < 0)
            {
                return false;
            }
            int slot = FirstRow * CharacterRoster.Columns + cell;   // 窗口里的格号 → 名册序号(离线模式 FirstRow 恒为 0)
            if (slot < _roster.Count)
            {
                Index = slot;
            }
            return true;
        }

        public List<SceneSprite> Build(long timeMs)
        {
            var sprites = new List<SceneSprite>();
            int order = 0;
            if (_sceneArt != null)
            {
                if (_view != null)
                {
                    _view.FirstRow = FirstRow;
                    _view.Busy = Busy;
                    _view.Prompt = Prompt;
                }
                _sceneArt.Build(sprites, ref order, _art, _scene, _roster, Index, AgeMs, _view);
                return sprites;
            }
            sprites.Add(OverlayArt.Place(_art.Solid(ScreenWidth, ScreenHeight, BackgroundColor), 0, 0, order++, "select-char-backdrop"));
            sprites.Add(OverlayArt.Place(_art.Text("SELECT CHARACTER", 3, OverlayArt.Yellow), 40, 30, order++, "select-char-title"));

            const int rowTop = 110;
            const int rowStep = 40;
            for (int i = 0; i < _roster.Count; i++)
            {
                int y = rowTop + rowStep * i;
                bool selected = i == Index;
                if (selected)
                {
                    sprites.Add(OverlayArt.Place(_art.Solid(400, rowStep - 6, SelectionColor), 36, y - 6, order++, "select-char-highlight"));
                }
                string line = _roster[i].Caption + "  " + _roster[i].JobName.ToUpperInvariant();
                sprites.Add(OverlayArt.Place(_art.Text(line, 3, selected ? OverlayArt.Yellow : OverlayArt.White), 48, y, order++, "select-char-name"));
            }
            if (_roster.Count == 0)
            {
                sprites.Add(OverlayArt.Place(_art.Text("NO CHARACTER", 3, OverlayArt.Orange), 48, rowTop, order++, "select-char-empty"));
            }
            sprites.Add(OverlayArt.Place(_art.Text("ARROWS SELECT   ENTER START   ESC QUIT", 2, OverlayArt.White), 40, ScreenHeight - 40, order++, "select-char-hint"));
            return sprites;
        }
    }
}
