using System;
using System.Collections.Generic;

// 本项目自己的占位设计(M31):原版任务窗口与 NPC 对话界面的版式没有读到(客户端界面脚本里有定义,还没解);
// 任务的规则与数据来自服务端静态读码(证据 E-123,非 REA),窗口只是把规则摆给玩家看、让玩家点接取/完成/放弃。
namespace Dnf.Play
{
    /// <summary>任务窗口的两种用法。</summary>
    public enum QuestWindowMode
    {
        /// <summary>和 NPC 说话:左边是这个 NPC 这里的任务(可以交的、可以接的、进行中的),右边是对白与奖励,底下是接取/完成按钮。</summary>
        Npc = 0,

        /// <summary>任务日志(L 键):左边是进行中的全部任务,右边是对白、进度与奖励,底下是放弃按钮。</summary>
        Journal = 1,
    }

    /// <summary>窗口底部的按钮种类(从左到右的次序见 <see cref="QuestWindow.Buttons"/>)。</summary>
    public enum QuestButton
    {
        Accept = 0,
        Complete = 1,
        Shop = 2,
        Abandon = 3,
        Close = 4,
    }

    /// <summary>
    /// 任务窗口(城镇里走到 NPC 旁按空格、或按 L 看日志):左边任务列表,右边选中任务的对白、进度与奖励,底下按钮。
    /// 方向键上下选任务,左右在多选一奖励之间换(交任务时领选中的那一件),回车或 X 接取/完成(日志里不用),
    /// Tab 去这个 NPC 的商店(NPC 同时开着商店时),Delete 放弃日志里选中的任务(第一次按只是确认,再按一次才放弃),Esc 关上;
    /// 鼠标点任务行(再点一下选中的行等于回车)、奖励行、按钮。开着的时候游戏暂停(宿主负责,见 <see cref="GameFlow.Quests"/>)。
    /// 窗口只管状态、判定与点击判定,画面由 <see cref="QuestWindowArt"/> 画,规则与数据在 <see cref="QuestBook"/> 里。文字是占位英文(有字体时 <see cref="UiText"/> 换成中文),
    /// 任务名与对白是数据文字(原版字符串表)。
    /// </summary>
    public sealed class QuestWindow
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

        /// <summary>窗口的位置与大小(画布像素,居中,和背包、商店窗口同一块)。</summary>
        public const int WindowX = 98;
        public const int WindowY = 30;
        public const int WindowWidth = 604;
        public const int WindowHeight = 540;

        /// <summary>标题条的高。</summary>
        public const int TitleHeight = 44;

        /// <summary>左边任务列表:左上角、宽、每行的高与间隔、一屏看到的行数。</summary>
        public const int ListX = 114;
        public const int ListY = 86;
        public const int ListWidth = 212;
        public const int RowHeight = 48;
        public const int RowGap = 4;
        public const int VisibleRows = 8;

        /// <summary>右边详情栏:左上角、宽高。</summary>
        public const int DetailX = 342;
        public const int DetailY = 86;
        public const int DetailWidth = 344;
        public const int DetailHeight = 384;

        /// <summary>多选一奖励每一行的高。</summary>
        public const int ChoiceHeight = 24;

        /// <summary>底部按钮:顶、高、宽、间隔(从详情栏右边缘往左排)。</summary>
        public const int ButtonY = 480;
        public const int ButtonHeight = 32;
        public const int ButtonWidth = 112;
        public const int ButtonGap = 8;

        private QuestBook _book;
        private List<QuestRow> _rows = new List<QuestRow>();
        private readonly List<QuestButton> _buttons = new List<QuestButton>();
        private int _lastQuestId = -1;

        /// <summary>开着。</summary>
        public bool Open { get; private set; }

        public QuestWindowMode Mode { get; private set; }

        /// <summary>说话的 NPC 编号(日志模式是 −1)。</summary>
        public int NpcId { get; private set; } = -1;

        /// <summary>NPC 的名字(数据文字;不知道是 null)。</summary>
        public string NpcName { get; private set; }

        /// <summary>这个 NPC 自己开的商店编号(<c>itemshop.lst</c> 的号);没有是 −1。有商店时窗口多一个"商店"按钮,Tab 键去商店。</summary>
        public int ShopIndex { get; private set; } = -1;

        /// <summary>列表里的任务行(开着时是刚查的;接、交、放弃之后自动重新查)。</summary>
        public IReadOnlyList<QuestRow> Rows => _rows;

        /// <summary>选中的行号;没有行时是 −1。</summary>
        public int Selected { get; private set; } = -1;

        /// <summary>一屏最上面一行是第几行。</summary>
        public int ScrollRow { get; private set; }

        /// <summary>多选一奖励里选中的是第几件(没有多选一奖励时没有意义)。</summary>
        public int ChoiceIndex { get; private set; }

        /// <summary>最近一次操作的结果文字(占位英文,显示在窗口底部);没有是空串。</summary>
        public string Message { get; private set; } = string.Empty;

        /// <summary>最近一次接取、交任务、放弃的结果(没做过是 <see cref="QuestActionResult.Ok"/>;日志与探针用)。</summary>
        public QuestActionResult LastResult { get; private set; }

        /// <summary>日志里按过一次放弃、等着再按一次确认。</summary>
        public bool AbandonArmed { get; private set; }

        /// <summary>累计(窗口对象一直在,不随每次打开清零)打开过几次、接取过几个、交过几个、放弃过几个。</summary>
        public int Opens { get; private set; }

        public int Accepts { get; private set; }

        public int TurnIns { get; private set; }

        public int Abandons { get; private set; }

        /// <summary>接上任务系统;没接就打不开。</summary>
        public QuestBook Book => _book;

        public void Use(QuestBook book)
        {
            _book = book ?? throw new ArgumentNullException(nameof(book));
        }

        /// <summary>选中的那一行;没有是 null。</summary>
        public QuestRow SelectedRow => (uint)Selected < (uint)_rows.Count ? _rows[Selected] : null;

        // ------------------------------------------------------------------ 打开与关闭

        /// <summary>
        /// 打开和 <paramref name="npcId"/> 的对话:只有城镇有;这个 NPC 这里一个任务也没有(没有可接的、可交的、进行中的)时不打开(返回 false,由调用方去开商店或什么也不做)。
        /// <paramref name="npcName"/> 是 NPC 的名字(可为 null),<paramref name="shopIndex"/> 是它自己的商店编号(没有传 −1)。
        /// </summary>
        public bool ShowNpc(GameStage stage, int npcId, string npcName, int shopIndex = -1)
        {
            if (stage != GameStage.Town || _book == null)
            {
                return false;
            }
            List<QuestRow> rows = _book.RowsFor(npcId);
            if (rows.Count == 0)
            {
                return false;
            }
            Mode = QuestWindowMode.Npc;
            NpcId = npcId;
            NpcName = npcName;
            ShopIndex = shopIndex;
            Begin(rows);
            return true;
        }

        /// <summary>打开任务日志(城镇与副本里都能开;进行中的任务一个也没有也开,画"没有任务")。</summary>
        public bool ShowJournal(GameStage stage)
        {
            if ((stage != GameStage.Town && stage != GameStage.Dungeon) || _book == null)
            {
                return false;
            }
            Mode = QuestWindowMode.Journal;
            NpcId = -1;
            NpcName = null;
            ShopIndex = -1;
            Begin(_book.LogRows());
            return true;
        }

        private void Begin(List<QuestRow> rows)
        {
            Open = true;
            Opens++;
            _rows = rows;
            Selected = rows.Count == 0 ? -1 : 0;
            ScrollRow = 0;
            ChoiceIndex = 0;
            Message = string.Empty;
            AbandonArmed = false;
            _lastQuestId = Selected < 0 ? -1 : rows[0].Id;
            RebuildButtons();
        }

        public void Close()
        {
            Open = false;
            AbandonArmed = false;
        }

        /// <summary>
        /// 重新查一遍任务行(接、交、放弃之后宿主与窗口自己都会调):选中的任务还在就继续选它,不在了就选原来的行号(夹在范围内)。
        /// 窗口没开什么也不做。
        /// </summary>
        public void Refresh()
        {
            if (!Open || _book == null)
            {
                return;
            }
            int keepIndex = Math.Max(0, Selected);
            int keepId = _lastQuestId;
            _rows = Mode == QuestWindowMode.Npc ? _book.RowsFor(NpcId) : _book.LogRows();
            int found = -1;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Id == keepId)
                {
                    found = i;
                    break;
                }
            }
            Selected = _rows.Count == 0 ? -1 : (found >= 0 ? found : Math.Min(keepIndex, _rows.Count - 1));
            _lastQuestId = Selected < 0 ? -1 : _rows[Selected].Id;
            if (found < 0)
            {
                ChoiceIndex = 0;
            }
            FollowSelection();
            RebuildButtons();
        }

        // ------------------------------------------------------------------ 按钮

        /// <summary>现在窗口底部有的按钮,从左到右(最右边总是关闭)。</summary>
        public IReadOnlyList<QuestButton> Buttons => _buttons;

        private void RebuildButtons()
        {
            _buttons.Clear();
            QuestRow row = SelectedRow;
            if (Mode == QuestWindowMode.Npc)
            {
                if (row != null && row.State == QuestRowState.Available)
                {
                    _buttons.Add(QuestButton.Accept);
                }
                else if (row != null && row.State == QuestRowState.Ready)
                {
                    _buttons.Add(QuestButton.Complete);
                }
                if (ShopIndex >= 0)
                {
                    _buttons.Add(QuestButton.Shop);
                }
            }
            else if (row != null)
            {
                _buttons.Add(QuestButton.Abandon);
            }
            _buttons.Add(QuestButton.Close);
        }

        /// <summary>第 <paramref name="index"/> 个按钮(共 <paramref name="count"/> 个)的矩形:从详情栏右边缘往左排,最后一个(关闭)贴着右边。</summary>
        public static (int X, int Y, int W, int H) ButtonRect(int index, int count)
        {
            int right = DetailX + DetailWidth;
            int x = right - (count - index) * ButtonWidth - (count - 1 - index) * ButtonGap;
            return (x, ButtonY, ButtonWidth, ButtonHeight);
        }

        /// <summary>第 <paramref name="visibleIndex"/> 个可见行(0 起)的矩形。</summary>
        public static (int X, int Y, int W, int H) RowRect(int visibleIndex)
        {
            return (ListX, ListY + visibleIndex * (RowHeight + RowGap), ListWidth, RowHeight);
        }

        private static bool Inside((int X, int Y, int W, int H) r, int x, int y)
        {
            return x >= r.X && y >= r.Y && x < r.X + r.W && y < r.Y + r.H;
        }

        /// <summary>点在哪个按钮上(没点中返回 −1,点中了返回它在 <see cref="Buttons"/> 里的序号)。</summary>
        public int HitButton(int x, int y)
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                if (Inside(ButtonRect(i, _buttons.Count), x, y))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>点在哪个可见行上(没点中返回 −1);返回的是行号(含滚动)。</summary>
        public int HitRow(int x, int y)
        {
            for (int v = 0; v < VisibleRows; v++)
            {
                int index = ScrollRow + v;
                if (index >= _rows.Count)
                {
                    break;
                }
                if (Inside(RowRect(v), x, y))
                {
                    return index;
                }
            }
            return -1;
        }

        /// <summary>
        /// 多选一奖励第 <paramref name="index"/> 件的矩形(画与点击判定共用):从详情栏里奖励区的底往上排,
        /// <paramref name="top"/> 是第一件的上沿(<see cref="QuestWindowArt.ChoiceTop"/> 算出来)。
        /// </summary>
        public static (int X, int Y, int W, int H) ChoiceRect(int top, int index)
        {
            return (DetailX + 8, top + index * ChoiceHeight, DetailWidth - 16, ChoiceHeight - 2);
        }

        // ------------------------------------------------------------------ 输入

        /// <summary>上下选任务(<paramref name="dy"/> 向下为正),左右在多选一奖励之间换(<paramref name="dx"/>);其它方向什么也不做。</summary>
        public void Move(int dx, int dy)
        {
            if (!Open)
            {
                return;
            }
            AbandonArmed = false;
            if (dy != 0 && _rows.Count > 0)
            {
                int next = Math.Max(0, Math.Min(_rows.Count - 1, Selected + dy));
                if (next != Selected)
                {
                    Selected = next;
                    _lastQuestId = _rows[next].Id;
                    ChoiceIndex = 0;
                    Message = string.Empty;
                    FollowSelection();
                    RebuildButtons();
                }
            }
            if (dx != 0)
            {
                QuestRow row = SelectedRow;
                if (row != null && row.State == QuestRowState.Ready && row.Choices.Count > 1)
                {
                    ChoiceIndex = Math.Max(0, Math.Min(row.Choices.Count - 1, ChoiceIndex + dx));
                }
            }
        }

        private void FollowSelection()
        {
            if (Selected < 0)
            {
                ScrollRow = 0;
                return;
            }
            if (Selected < ScrollRow)
            {
                ScrollRow = Selected;
            }
            else if (Selected >= ScrollRow + VisibleRows)
            {
                ScrollRow = Selected - VisibleRows + 1;
            }
            ScrollRow = Math.Max(0, Math.Min(ScrollRow, Math.Max(0, _rows.Count - VisibleRows)));
        }

        /// <summary>
        /// 回车:对话窗口里,选中的任务可以接就接、可以交就交(多选一奖励领选中的那件)、进行中的提示还没做完;日志里没有回车操作。
        /// 返回这一次有没有真的接下或交掉一个任务。
        /// </summary>
        public bool Activate()
        {
            if (!Open || _book == null)
            {
                return false;
            }
            AbandonArmed = false;
            QuestRow row = SelectedRow;
            if (row == null || Mode != QuestWindowMode.Npc)
            {
                return false;
            }
            QuestActionResult result;
            switch (row.State)
            {
                case QuestRowState.Available:
                    result = _book.Accept(row.Id);
                    LastResult = result;
                    Message = MessageOf(result, "QUEST ACCEPTED");
                    if (result == QuestActionResult.Ok)
                    {
                        Accepts++;
                    }
                    break;
                case QuestRowState.Ready:
                    result = _book.TurnIn(row.Id, row.Choices.Count > 0 ? ChoiceIndex : -1);
                    LastResult = result;
                    Message = MessageOf(result, "QUEST COMPLETE");
                    if (result == QuestActionResult.Ok)
                    {
                        TurnIns++;
                    }
                    break;
                default:
                    Message = "NOT DONE YET";
                    return false;
            }
            Refresh();
            return result == QuestActionResult.Ok;
        }

        /// <summary>
        /// 放弃日志里选中的任务:第一次只是确认(<see cref="AbandonArmed"/>),再来一次才放弃;<c>[cant giveup]</c> 的任务不能放弃。
        /// 返回这一次有没有真的放弃。
        /// </summary>
        public bool Abandon()
        {
            if (!Open || _book == null || Mode != QuestWindowMode.Journal)
            {
                return false;
            }
            QuestRow row = SelectedRow;
            if (row == null)
            {
                return false;
            }
            if (!row.CanAbandon)
            {
                AbandonArmed = false;
                LastResult = QuestActionResult.CannotGiveUp;
                Message = "CANNOT ABANDON";
                return false;
            }
            if (!AbandonArmed)
            {
                AbandonArmed = true;
                Message = "PRESS ABANDON AGAIN";
                return false;
            }
            AbandonArmed = false;
            QuestActionResult result = _book.Abandon(row.Id);
            LastResult = result;
            Message = MessageOf(result, "QUEST ABANDONED");
            if (result == QuestActionResult.Ok)
            {
                Abandons++;
            }
            Refresh();
            return result == QuestActionResult.Ok;
        }

        /// <summary>某个按钮被按下(键盘的 Tab 与鼠标点按钮都走这里)。返回要不要去商店:为真时调用方应关上这个窗口、打开 <see cref="ShopIndex"/> 的商店。</summary>
        public bool Press(QuestButton button)
        {
            switch (button)
            {
                case QuestButton.Accept:
                case QuestButton.Complete:
                    Activate();
                    return false;
                case QuestButton.Abandon:
                    Abandon();
                    return false;
                case QuestButton.Shop:
                    return ShopIndex >= 0;
                default:
                    Close();
                    return false;
            }
        }

        /// <summary>
        /// 鼠标点击(画布坐标):点按钮按下它,点任务行选中它(再点一下已选中的行等于回车),点多选一奖励选中那一件。
        /// 返回点中的是不是窗口里的某个控件(点在别处返回 false,窗口不关)。<paramref name="wantsShop"/> 为真表示点的是"商店"按钮,调用方照 <see cref="Press"/> 的约定去开商店。
        /// </summary>
        public bool Click(int x, int y, out bool wantsShop)
        {
            wantsShop = false;
            if (!Open)
            {
                return false;
            }
            int button = HitButton(x, y);
            if (button >= 0)
            {
                wantsShop = Press(_buttons[button]);
                return true;
            }
            int row = HitRow(x, y);
            if (row >= 0)
            {
                AbandonArmed = false;
                bool again = row == Selected;
                if (!again)
                {
                    Selected = row;
                    _lastQuestId = _rows[row].Id;
                    ChoiceIndex = 0;
                    Message = string.Empty;
                    FollowSelection();
                    RebuildButtons();
                }
                else
                {
                    Activate();
                }
                return true;
            }
            QuestRow selected = SelectedRow;
            if (selected != null && selected.State == QuestRowState.Ready && selected.Choices.Count > 1)
            {
                int top = QuestWindowArt.ChoiceTop(selected);
                int shown = Math.Min(selected.Choices.Count, QuestWindowArt.MaxChoiceLines);
                for (int i = 0; i < shown; i++)
                {
                    if (Inside(ChoiceRect(top, i), x, y))
                    {
                        ChoiceIndex = i;
                        return true;
                    }
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ 文字

        /// <summary>动作结果的占位英文:成功用 <paramref name="ok"/>,其余按结果。</summary>
        public static string MessageOf(QuestActionResult result, string ok)
        {
            switch (result)
            {
                case QuestActionResult.Ok:
                    return ok;
                case QuestActionResult.NoRoom:
                    return "BAG IS FULL";
                case QuestActionResult.NotReady:
                    return "NOT DONE YET";
                case QuestActionResult.CannotGiveUp:
                    return "CANNOT ABANDON";
                case QuestActionResult.NotFound:
                    return "NO SUCH QUEST";
                default:
                    return "NOT AVAILABLE";
            }
        }
    }
}
