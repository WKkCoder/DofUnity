using System;
using System.Collections.Generic;
using Dnf.Game;

namespace Dnf.Play
{
    /// <summary>
    /// 背包窗口(I 键):左边装备栏(12 格,部位 10 到 21),右边四页签的物品格子(每页 8 列 × 7 行 = 56 格),底下几行说明。
    /// 方向键移动光标(在格子最左一列再按左键就进入装备栏,在装备栏最右一列再按右键回到格子),Q、E(或 Tab)换页,回车穿上或脱下,I 或 Esc 关上;
    /// 鼠标点页签、格子、装备格(点已选中的再点一下等于回车)。开着的时候游戏暂停(宿主负责,见 <see cref="GameFlow.Bag"/>)。
    /// <b>本项目自己的占位设计</b>:原版背包与装备栏的界面没有读到(客户端界面脚本里有它的定义,还没解);文字是占位英文,物品名要等中文文字系统。
    /// 窗口只管状态与点击判定,画面由 <see cref="BagWindowArt"/> 画,物品数据在 <see cref="HeroProgress"/> 里。
    /// </summary>
    public sealed class BagWindow
    {
        public const int ScreenWidth = 800;
        public const int ScreenHeight = 600;

        /// <summary>窗口的位置与大小(画布像素,居中)。</summary>
        public const int WindowX = 98;
        public const int WindowY = 30;
        public const int WindowWidth = 604;
        public const int WindowHeight = 540;

        /// <summary>标题条的高。</summary>
        public const int TitleHeight = 44;

        /// <summary>物品格子的列数与行数(列数 × 行数 = <see cref="Inventory.SlotsPerTab"/>)。</summary>
        public const int Columns = 8;
        public const int Rows = Inventory.SlotsPerTab / Columns;

        /// <summary>物品格子的边长与间隔,以及第一格左上角。</summary>
        public const int CellSize = 48;
        public const int CellGap = 4;
        public const int GridX = 274;
        public const int GridY = 124;

        /// <summary>页签条:每个页签的大小、间隔与第一个页签左上角。</summary>
        public const int TabWidth = 96;
        public const int TabHeight = 28;
        public const int TabGap = 6;
        public const int TabX = 274;
        public const int TabY = 86;

        /// <summary>装备栏:每行 3 格,格子边长与间隔,以及第一格左上角;标题在 <see cref="CaptionY"/>。</summary>
        public const int SlotColumns = 3;
        public const int SlotSize = 40;
        public const int SlotGap = 8;
        public const int SlotX = 114;
        public const int SlotY = 124;
        public const int CaptionY = 86;

        /// <summary>开着。</summary>
        public bool Open { get; private set; }

        /// <summary>当前页签。</summary>
        public InventoryTab Tab { get; private set; }

        /// <summary>物品格子上的光标(0 到 55;空格子上也可以停)。</summary>
        public int Cursor { get; private set; }

        /// <summary>光标在装备栏上(而不是物品格子上)。</summary>
        public bool OnEquipment { get; private set; }

        /// <summary>装备栏上的光标(0 到 11,对应部位 <see cref="EquipmentSlots.First"/> 起)。</summary>
        public int EquipCursor { get; private set; }

        /// <summary>最近一次操作的结果文字(占位英文;显示在窗口底部)。</summary>
        public string Message { get; private set; } = string.Empty;

        /// <summary>最近一次穿装备的结果(没穿过是 <see cref="EquipResult.NoItem"/>;日志与探针用)。</summary>
        public EquipResult LastResult { get; private set; } = EquipResult.NoItem;

        /// <summary>开窗以来成功穿上、脱下的次数。</summary>
        public int Changes { get; private set; }

        /// <summary>穿一件装备前问一句"画面上穿得出来吗"(<see cref="HeroProgress.Equip"/> 的 canShow);没给就不问。</summary>
        public Func<ItemInfo, bool> CanShow { get; set; }

        /// <summary>
        /// 在背包里按回车用一件消耗品(证据 E-120):流程(<see cref="GameFlow"/>)把它接到当前会话(<see cref="IItemSession.TryUseItem"/>);
        /// 没接(城镇会话、测试)时消耗品一律提示"还不能用"。
        /// </summary>
        public Func<ItemInfo, UseResult> UseItem { get; set; }

        /// <summary>最近一次在背包里用消耗品的结果(没用过是 <see cref="UseResult.Ok"/>;日志与探针用)。</summary>
        public UseResult LastUse { get; private set; }

        /// <summary>累计在背包里成功用掉的件数。</summary>
        public int Uses { get; private set; }

        /// <summary>
        /// 用游戏数据做 <see cref="CanShow"/>(<see cref="HeroLook.CanShow"/>):武器要能造出外观、各个动作的动画都在。
        /// 宿主(客户端)只认识渲染层的类型,所以从这里接,不直接写 <see cref="Func{T, TResult}"/>。
        /// </summary>
        public void UseAssets(Dnf.Render.DnfAssets assets)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            CanShow = item => HeroLook.CanShow(assets, item);
        }

        /// <summary>按阶段打开:只有城镇与副本里有背包窗口;返回有没有打开。每次打开都回到装备页、光标在第一格。</summary>
        public bool Show(GameStage stage)
        {
            if (stage != GameStage.Town && stage != GameStage.Dungeon)
            {
                return false;
            }
            Open = true;
            Tab = InventoryTab.Equipment;
            Cursor = 0;
            OnEquipment = false;
            EquipCursor = 0;
            Message = string.Empty;
            return true;
        }

        public void Close()
        {
            Open = false;
        }

        // ------------------------------------------------------------------ 版面(画与点击判定共用)

        /// <summary>第 <paramref name="index"/> 个物品格子的矩形(x、y、宽、高)。</summary>
        public static (int X, int Y, int W, int H) CellRect(int index)
        {
            int col = index % Columns, row = index / Columns;
            return (GridX + col * (CellSize + CellGap), GridY + row * (CellSize + CellGap), CellSize, CellSize);
        }

        /// <summary>第 <paramref name="index"/> 个页签的矩形。</summary>
        public static (int X, int Y, int W, int H) TabRect(int index)
        {
            return (TabX + index * (TabWidth + TabGap), TabY, TabWidth, TabHeight);
        }

        /// <summary>第 <paramref name="index"/> 个装备格(0 到 11)的矩形。</summary>
        public static (int X, int Y, int W, int H) SlotRect(int index)
        {
            int col = index % SlotColumns, row = index / SlotColumns;
            return (SlotX + col * (SlotSize + SlotGap), SlotY + row * (SlotSize + SlotGap), SlotSize, SlotSize);
        }

        private static bool Inside((int X, int Y, int W, int H) r, int x, int y)
        {
            return x >= r.X && y >= r.Y && x < r.X + r.W && y < r.Y + r.H;
        }

        /// <summary>画布上的点落在哪个物品格子里;不在任何格子上返回 −1。</summary>
        public static int HitCell(int x, int y)
        {
            for (int i = 0; i < Inventory.SlotsPerTab; i++)
            {
                if (Inside(CellRect(i), x, y))
                {
                    return i;
                }
            }
            return -1;
        }

        public static int HitTab(int x, int y)
        {
            for (int i = 0; i < Inventory.TabCount; i++)
            {
                if (Inside(TabRect(i), x, y))
                {
                    return i;
                }
            }
            return -1;
        }

        public static int HitSlot(int x, int y)
        {
            for (int i = 0; i < EquipmentSlots.Count; i++)
            {
                if (Inside(SlotRect(i), x, y))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>页签的占位英文名。</summary>
        public static string TabLabel(InventoryTab tab)
        {
            switch (tab)
            {
                case InventoryTab.Equipment:
                    return "EQUIP";
                case InventoryTab.Consumable:
                    return "USE";
                case InventoryTab.Material:
                    return "MATERIAL";
                default:
                    return "QUEST";
            }
        }

        /// <summary>装备格的占位英文缩写(部位号 10 到 21)。</summary>
        public static string SlotLabel(int part)
        {
            switch (part)
            {
                case EquipmentParts.Weapon:
                    return "WPN";
                case EquipmentParts.TitleName:
                    return "TITLE";
                case EquipmentParts.Coat:
                    return "COAT";
                case EquipmentParts.Shoulder:
                    return "SHLD";
                case EquipmentParts.Pants:
                    return "PANT";
                case EquipmentParts.Shoes:
                    return "SHOE";
                case EquipmentParts.Waist:
                    return "BELT";
                case EquipmentParts.Amulet:
                    return "NECK";
                case EquipmentParts.Wrist:
                    return "WRST";
                case EquipmentParts.Ring:
                    return "RING";
                case EquipmentParts.Support:
                    return "SUPT";
                case EquipmentParts.MagicStone:
                    return "STONE";
                default:
                    return string.Empty;
            }
        }

        /// <summary>穿戴结果的占位英文提示(<see cref="EquipResult.NoItem"/> 是空串)。</summary>
        public static string MessageOf(EquipResult result)
        {
            switch (result)
            {
                case EquipResult.Ok:
                    return "EQUIPPED";
                case EquipResult.NotEquipment:
                    return "CANNOT USE YET";
                case EquipResult.NotWearable:
                    return "NO SLOT FOR THIS";
                case EquipResult.WrongSlot:
                    return "WRONG SLOT";
                case EquipResult.WrongJob:
                    return "WRONG CLASS";
                case EquipResult.LevelTooLow:
                    return "LEVEL TOO LOW";
                case EquipResult.NotShowable:
                    return "NO ANIMATION FOR THIS";
                default:
                    return string.Empty;
            }
        }

        /// <summary>使用结果的占位英文提示(<see cref="UseResult.Ok"/> 是 USED)。</summary>
        public static string MessageOf(UseResult result)
        {
            switch (result)
            {
                case UseResult.Ok:
                    return "USED";
                case UseResult.Cooling:
                    return "COOLING DOWN";
                case UseResult.LevelTooLow:
                    return "LEVEL TOO LOW";
                case UseResult.WrongJob:
                    return "WRONG CLASS";
                case UseResult.Unavailable:
                    return "CANNOT USE HERE";
                case UseResult.NotInBag:
                    return "NONE LEFT";
                default:
                    return MessageOf(EquipResult.NotEquipment);       // 不是消耗品、没有这个版本做了的效果:还不能用
            }
        }

        private bool UseStackable(ItemInfo item)
        {
            if (UseItem == null)
            {
                Message = MessageOf(EquipResult.NotEquipment);
                return false;
            }
            UseResult result = UseItem(item);
            LastUse = result;
            Message = MessageOf(result);
            if (result == UseResult.Ok)
            {
                Uses++;
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ 输入

        /// <summary>移动光标:在物品格子里夹在边界内;在最左一列再往左进入装备栏,在装备栏最右一列再往右回到物品格子。</summary>
        public void Move(int dx, int dy)
        {
            if (!Open)
            {
                return;
            }
            if (OnEquipment)
            {
                int col = EquipCursor % SlotColumns, row = EquipCursor / SlotColumns;
                int rows = (EquipmentSlots.Count + SlotColumns - 1) / SlotColumns;
                if (dx > 0 && col == SlotColumns - 1)
                {
                    OnEquipment = false;
                    Cursor = Math.Min(row, Rows - 1) * Columns;
                    return;
                }
                col = Clamp(col + dx, 0, SlotColumns - 1);
                row = Clamp(row + dy, 0, rows - 1);
                EquipCursor = Math.Min(row * SlotColumns + col, EquipmentSlots.Count - 1);
                return;
            }
            int c = Cursor % Columns, r = Cursor / Columns;
            if (dx < 0 && c == 0)
            {
                OnEquipment = true;
                EquipCursor = Math.Min(r, (EquipmentSlots.Count + SlotColumns - 1) / SlotColumns - 1) * SlotColumns + SlotColumns - 1;
                EquipCursor = Math.Min(EquipCursor, EquipmentSlots.Count - 1);
                return;
            }
            c = Clamp(c + dx, 0, Columns - 1);
            r = Clamp(r + dy, 0, Rows - 1);
            Cursor = r * Columns + c;
        }

        /// <summary>换页(+1 下一页、−1 上一页,循环);光标回到第一格。</summary>
        public void NextTab(int delta)
        {
            if (!Open)
            {
                return;
            }
            int t = ((int)Tab + delta) % Inventory.TabCount;
            if (t < 0)
            {
                t += Inventory.TabCount;
            }
            SelectTab((InventoryTab)t);
        }

        private void SelectTab(InventoryTab tab)
        {
            Tab = tab;
            Cursor = 0;
            OnEquipment = false;
        }

        /// <summary>
        /// 回车:光标在装备栏上就脱下那一格的装备;在物品格子上、是装备就穿上(规则与结果见 <see cref="EquipRules"/>、<see cref="MessageOf"/>),
        /// 是消耗品就用一件(<see cref="UseItem"/>,证据 E-120;没接就提示"还不能用")。返回这一次有没有真的改变装备栏或用掉了一件。画面上穿不穿得出来按 <see cref="CanShow"/>。
        /// </summary>
        public bool Activate(HeroProgress progress)
        {
            if (!Open || progress == null)
            {
                return false;
            }
            if (OnEquipment)
            {
                int part = EquipmentSlots.First + EquipCursor;
                if (progress.Worn.Get(part) == null)
                {
                    Message = "EMPTY SLOT";
                    return false;
                }
                bool done = progress.Unequip(part);
                Message = done ? "UNEQUIPPED" : "BAG FULL";
                if (done)
                {
                    Changes++;
                }
                return done;
            }
            IReadOnlyList<ItemStack> stacks = progress.Bag.Tab(Tab);
            if ((uint)Cursor >= (uint)stacks.Count)
            {
                Message = string.Empty;
                return false;
            }
            if (!stacks[Cursor].Info.IsEquipment)
            {
                return UseStackable(stacks[Cursor].Info);
            }
            EquipResult result = progress.Equip(Cursor, CanShow);
            LastResult = result;
            Message = MessageOf(result);
            if (result == EquipResult.Ok)
            {
                Changes++;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 无人值守地试穿(宿主的探针用):切到装备页,光标按顺序走过每一格,每格按一次回车,穿上第一件就停;返回有没有穿上。
        /// <paramref name="tried"/> 是按过回车的格数。窗口没开或没有进度时什么也不做。
        /// </summary>
        public bool TryEquipAny(HeroProgress progress, out int tried)
        {
            tried = 0;
            if (!Open || progress == null)
            {
                return false;
            }
            SelectTab(InventoryTab.Equipment);
            int count = progress.Bag.Tab(InventoryTab.Equipment).Count;
            for (int i = 0; i < count; i++)
            {
                Cursor = i;
                tried++;
                if (Activate(progress))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 鼠标点击(画布坐标):点页签换页,点物品格或装备格先选中、再点一下已选中的等于回车。返回点中的是不是窗口里的某个控件
        /// (点在别处返回 false,窗口不关)。
        /// </summary>
        public bool Click(int x, int y, HeroProgress progress)
        {
            if (!Open)
            {
                return false;
            }
            int tab = HitTab(x, y);
            if (tab >= 0)
            {
                SelectTab((InventoryTab)tab);
                return true;
            }
            int cell = HitCell(x, y);
            if (cell >= 0)
            {
                bool again = !OnEquipment && Cursor == cell;
                OnEquipment = false;
                Cursor = cell;
                if (again)
                {
                    Activate(progress);
                }
                return true;
            }
            int slot = HitSlot(x, y);
            if (slot >= 0)
            {
                bool again = OnEquipment && EquipCursor == slot;
                OnEquipment = true;
                EquipCursor = slot;
                if (again)
                {
                    Activate(progress);
                }
                return true;
            }
            return false;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
