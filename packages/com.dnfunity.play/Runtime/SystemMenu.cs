using System;
using System.Collections.Generic;

namespace Dnf.Play
{
    /// <summary>系统菜单上的一项(选中之后由 <see cref="GameFlow"/> 执行)。</summary>
    public enum SystemMenuAction
    {
        /// <summary>没有选择(还没选、或者已经取走)。</summary>
        None = 0,

        /// <summary>继续游戏(关上菜单)。</summary>
        Resume = 1,

        /// <summary>放弃副本、回城镇(只在副本里有)。</summary>
        ReturnToTown = 2,

        /// <summary>回到选角界面(只在城镇里有)。</summary>
        CharacterSelect = 3,

        /// <summary>退出游戏(流程只标记 <see cref="GameFlow.QuitRequested"/>,宿主去退出)。</summary>
        Quit = 4,
    }

    /// <summary>
    /// 按 Esc 在城镇与副本里弹出的系统菜单:竖排几行,上下键(或鼠标悬停)选,回车、数字键或点击确定,Esc 再按一下关掉;开着的时候游戏暂停(宿主负责暂停)。
    /// <b>本项目自己的占位设计</b>:原版 Esc 菜单的版式与条目没有读到(客户端界面脚本里有它的定义,还没解),
    /// 条目按阶段取最小的一组——副本里:继续、放弃回城、退出;城镇里:继续、回选角、退出。文字是占位英文。
    /// 菜单只管状态与点击判定(<see cref="SystemMenuArt"/> 画它),选中的项由 <see cref="TakeAction"/> 取走,不直接改流程。
    /// </summary>
    public sealed class SystemMenu
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

        /// <summary>面板宽(含边框之内的底)。</summary>
        public const int PanelWidth = 340;

        /// <summary>标题那一块的高。</summary>
        public const int TitleHeight = 64;

        /// <summary>每一行的高。</summary>
        public const int RowHeight = 48;

        /// <summary>最后一行下面的留白。</summary>
        public const int BottomPad = 20;

        private readonly List<SystemMenuAction> _items = new List<SystemMenuAction>();
        private SystemMenuAction _pending;

        /// <summary>菜单开着。</summary>
        public bool Open { get; private set; }

        /// <summary>选中的行(从 0 数);没开时是 0。</summary>
        public int Selected { get; private set; }

        /// <summary>当前这一组菜单项(没开时为空)。</summary>
        public IReadOnlyList<SystemMenuAction> Items => _items;

        /// <summary>面板左边(画布 x)。</summary>
        public int PanelX => (ScreenWidth - PanelWidth) / 2;

        /// <summary>面板高(按当前的行数)。</summary>
        public int PanelHeight => TitleHeight + _items.Count * RowHeight + BottomPad;

        /// <summary>面板上边(画布 y,垂直居中)。</summary>
        public int PanelY => (ScreenHeight - PanelHeight) / 2;

        /// <summary>某一阶段的菜单项:副本里 继续、放弃回城、退出;城镇里 继续、回选角、退出;其它阶段(加载、选角、副本选择)没有系统菜单。</summary>
        public static IReadOnlyList<SystemMenuAction> ItemsFor(GameStage stage)
        {
            switch (stage)
            {
                case GameStage.Dungeon:
                    return new[] { SystemMenuAction.Resume, SystemMenuAction.ReturnToTown, SystemMenuAction.Quit };
                case GameStage.Town:
                    return new[] { SystemMenuAction.Resume, SystemMenuAction.CharacterSelect, SystemMenuAction.Quit };
                default:
                    return Array.Empty<SystemMenuAction>();
            }
        }

        /// <summary>菜单项的占位英文文字。</summary>
        public static string LabelOf(SystemMenuAction action)
        {
            switch (action)
            {
                case SystemMenuAction.Resume:
                    return "RESUME";
                case SystemMenuAction.ReturnToTown:
                    return "RETURN TO TOWN";
                case SystemMenuAction.CharacterSelect:
                    return "CHARACTER SELECT";
                case SystemMenuAction.Quit:
                    return "QUIT GAME";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// 在 <paramref name="stage"/> 阶段打开菜单,选中第一行(继续);这个阶段没有系统菜单就什么也不做并返回 false。
        /// 打开时丢掉没取走的旧选择。
        /// </summary>
        public bool Show(GameStage stage)
        {
            IReadOnlyList<SystemMenuAction> items = ItemsFor(stage);
            if (items.Count == 0)
            {
                return false;
            }
            _items.Clear();
            _items.AddRange(items);
            Selected = 0;
            _pending = SystemMenuAction.None;
            Open = true;
            return true;
        }

        /// <summary>关上菜单(什么也不选);没取走的选择一并丢掉。</summary>
        public void Close()
        {
            Open = false;
            _pending = SystemMenuAction.None;
            _items.Clear();
            Selected = 0;
        }

        /// <summary>上下移动选中行(到头回绕)。没开时什么也不做。</summary>
        public void Move(int delta)
        {
            if (!Open || _items.Count == 0)
            {
                return;
            }
            Selected = ((Selected + delta) % _items.Count + _items.Count) % _items.Count;
        }

        /// <summary>确定选中的行:关上菜单,把这一项留给 <see cref="TakeAction"/>。</summary>
        public void Choose()
        {
            if (Open)
            {
                Pick(Selected);
            }
        }

        /// <summary>按数字键确定(从 1 数);超出范围什么也不做。</summary>
        public void ChooseNumber(int number)
        {
            if (Open && number >= 1 && number <= _items.Count)
            {
                Pick(number - 1);
            }
        }

        /// <summary>鼠标(触摸)停在画布上的 (x, y):在某一行里就选中那一行。</summary>
        public void SetPointer(int x, int y)
        {
            int row = RowAt(x, y);
            if (Open && row >= 0)
            {
                Selected = row;
            }
        }

        /// <summary>点击画布上的 (x, y):点在某一行里就确定那一行;点在别处没有反应(不关菜单)。</summary>
        public void Click(int x, int y)
        {
            int row = RowAt(x, y);
            if (Open && row >= 0)
            {
                Pick(row);
            }
        }

        /// <summary>取走选定的项(只给一次);没选返回 <see cref="SystemMenuAction.None"/>。</summary>
        public SystemMenuAction TakeAction()
        {
            SystemMenuAction a = _pending;
            _pending = SystemMenuAction.None;
            return a;
        }

        /// <summary>第 <paramref name="index"/> 行的矩形(画布坐标):x、y、宽、高。</summary>
        public (int X, int Y, int Width, int Height) RowRect(int index)
        {
            return (PanelX + 16, PanelY + TitleHeight + index * RowHeight, PanelWidth - 32, RowHeight - 6);
        }

        /// <summary>画布上的 (x, y) 落在哪一行里;不在任何一行(或菜单没开)返回 −1。</summary>
        public int RowAt(int x, int y)
        {
            if (!Open)
            {
                return -1;
            }
            for (int i = 0; i < _items.Count; i++)
            {
                (int rx, int ry, int rw, int rh) = RowRect(i);
                if (x >= rx && x < rx + rw && y >= ry && y < ry + rh)
                {
                    return i;
                }
            }
            return -1;
        }

        private void Pick(int index)
        {
            _pending = _items[index];
            Open = false;
            _items.Clear();
            Selected = 0;
        }
    }
}
