using System;
using Dnf.Game;

namespace Dnf.Play
{
    /// <summary>背包窗口(<see cref="BagWindow"/>)的两页:物品页(装备栏加四个页签,原来的样子)与装扮页(r15-avatarhost)。</summary>
    public enum BagPage
    {
        Items = 0,
        Avatars = 1,
    }

    /// <summary>
    /// 背包的装扮页(r15-avatarhost,<b>本项目自己的占位设计</b>):左边装扮栏 10 格(部位 0 到 9),右边是装备页里全部的时装(<see cref="AvatarBagView.Avatars"/>,
    /// 按格的先后排,8 列 × 7 行,与物品页同一套版面),方向键移动光标(格子最左一列再按左键进装扮栏),回车在装扮栏上脱下、在时装上穿上(换穿时旧件回原来的格)。
    /// 页面只管光标与点击判定、把动作交给 <see cref="AvatarSession"/>:穿得上穿不上、换下来的放哪、画面上穿不穿得出来,都是规则层(<see cref="AvatarRules"/>、<see cref="AvatarActions"/>)
    /// 与衣柜的结论,这里一概不重判——格子上暗不暗(<see cref="AvatarBagEntry.CanWear"/>)、提示文字(<see cref="AvatarBagEntry.Result"/>)也直接取它们的结果。
    /// 画面由 <see cref="BagAvatarArt"/> 画;宿主用 <see cref="BagWindow.UseAvatars"/> 把它接到窗口上。
    /// </summary>
    public sealed class BagAvatarPage
    {
        private readonly AvatarSession _session;
        private AvatarBagView _view;

        public BagAvatarPage(AvatarSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _view = session.View();
        }

        public AvatarSession Session => _session;

        /// <summary>最近一次建的视图(<see cref="Refresh"/>、每次动作前后都会重建;背包别处变了要自己 <see cref="Refresh"/>)。</summary>
        public AvatarBagView View => _view;

        /// <summary>时装格子上的光标(0 到 55;空格子上也可以停)。</summary>
        public int Cursor { get; private set; }

        /// <summary>光标在装扮栏上(而不是时装格子上)。</summary>
        public bool OnSlots { get; private set; }

        /// <summary>装扮栏上的光标(0 到 9,对应部位 <see cref="AvatarSlots.First"/> 起)。</summary>
        public int SlotCursor { get; private set; }

        /// <summary>最近一次操作的结果文字(占位英文;显示在窗口底部)。</summary>
        public string Message { get; private set; } = string.Empty;

        /// <summary>最近一次穿时装的结果(没穿过是 <see cref="EquipResult.NoItem"/>)。</summary>
        public EquipResult LastResult { get; private set; } = EquipResult.NoItem;

        /// <summary>成功穿上、脱下的次数(<see cref="BagWindow"/> 据此累加它自己的 <see cref="BagWindow.Changes"/>)。</summary>
        public int Changes { get; private set; }

        /// <summary>光标指着的装扮栏部位号(光标不在装扮栏上返回 −1)。</summary>
        public int SelectedPart => OnSlots ? AvatarSlots.First + SlotCursor : -1;

        /// <summary>光标指着的时装格(光标在装扮栏上、或格子是空的返回 null)。</summary>
        public AvatarBagEntry? SelectedEntry
        {
            get
            {
                if (OnSlots || (uint)Cursor >= (uint)_view.Avatars.Count)
                {
                    return null;
                }
                return _view.Avatars[Cursor];
            }
        }

        /// <summary>光标指着的物品(装扮栏上那一格穿的、或时装格子里那一格);没有返回 null。</summary>
        public ItemInfo SelectedItem
        {
            get
            {
                if (OnSlots)
                {
                    return _session.Slots.Get(SelectedPart);
                }
                AvatarBagEntry? entry = SelectedEntry;
                return entry.HasValue ? entry.Value.Item : null;
            }
        }

        /// <summary>重建视图并返回(背包与装扮栏现在的样子)。</summary>
        public AvatarBagView Refresh()
        {
            _view = _session.View();
            return _view;
        }

        /// <summary>回到开页时的样子:光标在第一个格子、清掉提示、重建视图。</summary>
        public void Reset()
        {
            Cursor = 0;
            OnSlots = false;
            SlotCursor = 0;
            Message = string.Empty;
            Refresh();
        }

        /// <summary>移动光标:在时装格子里夹在边界内;在最左一列再往左进入装扮栏,在装扮栏最右一列(或最后一格)再往右回到格子。</summary>
        public void Move(int dx, int dy)
        {
            int slotRows = (AvatarSlots.Count + BagWindow.SlotColumns - 1) / BagWindow.SlotColumns;
            if (OnSlots)
            {
                int col = SlotCursor % BagWindow.SlotColumns, row = SlotCursor / BagWindow.SlotColumns;
                if (dx > 0 && (col == BagWindow.SlotColumns - 1 || SlotCursor == AvatarSlots.Count - 1))
                {
                    OnSlots = false;
                    Cursor = Math.Min(row, BagWindow.Rows - 1) * BagWindow.Columns;
                    return;
                }
                col = Clamp(col + dx, 0, BagWindow.SlotColumns - 1);
                row = Clamp(row + dy, 0, slotRows - 1);
                SlotCursor = Math.Min(row * BagWindow.SlotColumns + col, AvatarSlots.Count - 1);
                return;
            }
            int c = Cursor % BagWindow.Columns, r = Cursor / BagWindow.Columns;
            if (dx < 0 && c == 0)
            {
                OnSlots = true;
                SlotCursor = Math.Min(Math.Min(r, slotRows - 1) * BagWindow.SlotColumns + BagWindow.SlotColumns - 1, AvatarSlots.Count - 1);
                return;
            }
            c = Clamp(c + dx, 0, BagWindow.Columns - 1);
            r = Clamp(r + dy, 0, BagWindow.Rows - 1);
            Cursor = r * BagWindow.Columns + c;
        }

        /// <summary>
        /// 回车:光标在装扮栏上就脱下那一格(空格提示 EMPTY SLOT,背包装备页没空格提示 BAG FULL);在时装格子上就穿上那一件(<see cref="AvatarSession.Wear"/>,
        /// 结果与提示见 <see cref="MessageOf"/>),空格子什么也不做。动作之前先重建视图,免得按过期的格下标穿错件。返回这一次有没有真的穿上或脱下。
        /// </summary>
        public bool Activate()
        {
            Refresh();
            if (OnSlots)
            {
                int part = SelectedPart;
                if (_session.Slots.Get(part) == null)
                {
                    Message = "EMPTY SLOT";
                    return false;
                }
                bool done = _session.TakeOff(part);
                Message = done ? "TAKEN OFF" : "BAG FULL";
                if (done)
                {
                    Changes++;
                }
                Refresh();
                return done;
            }
            AvatarBagEntry? entry = SelectedEntry;
            if (!entry.HasValue)
            {
                Message = string.Empty;
                return false;
            }
            EquipResult result = _session.Wear(entry.Value.BagIndex);
            LastResult = result;
            Message = MessageOf(result);
            if (result == EquipResult.Ok)
            {
                Changes++;
            }
            Refresh();
            return result == EquipResult.Ok;
        }

        /// <summary>
        /// 鼠标点击(画布坐标):点时装格或装扮格先选中、再点一下已选中的等于回车。返回点中的是不是页面里的某个控件(点在别处返回 false)。
        /// 页面切换的按钮在窗口上(<see cref="BagWindow.PageButtonRect"/>),不在这里。
        /// </summary>
        public bool Click(int x, int y)
        {
            int cell = BagWindow.HitCell(x, y);
            if (cell >= 0)
            {
                bool again = !OnSlots && Cursor == cell;
                OnSlots = false;
                Cursor = cell;
                if (again)
                {
                    Activate();
                }
                return true;
            }
            int slot = BagWindow.HitSlot(x, y);
            if (slot >= 0 && slot < AvatarSlots.Count)
            {
                bool again = OnSlots && SlotCursor == slot;
                OnSlots = true;
                SlotCursor = slot;
                if (again)
                {
                    Activate();
                }
                return true;
            }
            return false;
        }

        /// <summary>穿时装结果的占位英文提示:通过是 WORN,其余同物品页(<see cref="BagWindow.MessageOf(EquipResult)"/>)。</summary>
        public static string MessageOf(EquipResult result)
        {
            return result == EquipResult.Ok ? "WORN" : BagWindow.MessageOf(result);
        }

        // 下标就是部位号 0 到 9,次序同 EquipmentParts.TagOf:帽子、头发、脸、上衣、裤子、鞋、胸饰、腰饰、皮肤、灵气
        private static readonly string[] SlotLabels = { "HAT", "HAIR", "FACE", "COAT", "PANT", "SHOE", "BRST", "WAIST", "SKIN", "AURA" };

        /// <summary>装扮栏格子的占位英文缩写(部位号 0 到 9;其余是空串)。</summary>
        public static string SlotLabel(int part)
        {
            return AvatarSlots.IsSlot(part) ? SlotLabels[part - AvatarSlots.First] : string.Empty;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
