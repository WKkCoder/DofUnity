using System;
using System.Collections.Generic;
using Dnf.Game;

// 城镇里的宠物窗口:名册列表(等级、经验、饱食、出战状态)与三个操作入口——出战/收起、喂食、关上。
// 全部复用状态层的接口(PetSession.Equip / Unequip / Feed),这里不加规则:
//   · 喂食喂的是"出战的那只"(服务端 UseFeed 就是对装备着的宠物用),与光标停在谁身上无关——光标在没出战的宠物上按喂食,吃的仍是出战的那只;
//   · 喂的是哪件消耗品由窗口判断(状态层把这一步留给宿主):背包里第一件满足判断的物品,成功喂进去(吃下或复活)才扣一件,没喂进去不扣;
//   · 饱食读数一律经状态层按时间源给的秒数结算。
// 观察(本机数据,只记真假、不记编号):服务端喂食用的是一个写死的物品编号,库里可叠加物品类型 [feed] 的物品恰好只有一件、并且就是那个编号,
// 所以缺省的判断是"可叠加物品、类型为 [feed]"(StackableTypes 表里的 10);宿主想收紧成某个编号时自己传判断函数。
// 窗口的版式(位置、行高、按钮)是本项目自己的占位设计(与商店窗口同一块位置),画面由以后的窗口画面类来画,这里只管数据、操作与点击判定。
namespace Dnf.Play
{
    /// <summary>宠物窗口一次操作的结果。</summary>
    public enum PetWindowResult
    {
        /// <summary>还没做过操作。</summary>
        None = 0,

        /// <summary>光标处的宠物出战了(原来出战的先收起)。</summary>
        Equipped = 1,

        /// <summary>出战的宠物收起了。</summary>
        Unequipped = 2,

        /// <summary>喂进去了,扣了一件饲料。</summary>
        Fed = 3,

        /// <summary>喂进去了,而且喂之前宠物是饿死状态(复活),扣了一件饲料。</summary>
        Revived = 4,

        /// <summary>没有可操作的宠物(名册是空的、光标处没有宠物、没有出战的宠物)。</summary>
        NoPet = 5,

        /// <summary>光标处的宠物已经出战了。</summary>
        AlreadyEquipped = 6,

        /// <summary>出战的这只宠物不吃饲料(选择成长型)。</summary>
        NotFeedable = 7,

        /// <summary>背包里没有饲料。</summary>
        NoFeedItem = 8,
    }

    /// <summary>窗口右边的按钮。</summary>
    public enum PetWindowButton
    {
        None = 0,

        /// <summary>出战/收起(光标处的宠物已出战就是收起)。</summary>
        Equip = 1,

        /// <summary>喂食(喂出战的宠物)。</summary>
        Feed = 2,

        Close = 3,
    }

    /// <summary>窗口名册里的一行(每次取都按此刻的时间重新结算)。</summary>
    public readonly struct PetWindowRow
    {
        public PetWindowRow(int uid, int itemCode, int petId, string name, int level, int maxLevel, int exp, int stomach, bool starved, bool equipped, bool feedable)
        {
            Uid = uid;
            ItemCode = itemCode;
            PetId = petId;
            Name = name;
            Level = level;
            MaxLevel = maxLevel;
            Exp = exp;
            Stomach = stomach;
            Starved = starved;
            Equipped = equipped;
            Feedable = feedable;
        }

        /// <summary>实例号(状态层里认的键)。</summary>
        public int Uid { get; }

        public int ItemCode { get; }

        public int PetId { get; }

        /// <summary>宠物脚本的 <c>[name]</c>(字符串表的键,显示用的文字要宿主按字符串表翻)。</summary>
        public string Name { get; }

        public int Level { get; }

        public int MaxLevel { get; }

        /// <summary>累计经验。</summary>
        public int Exp { get; }

        /// <summary>此刻的整数饱食(向零取整,0 到 100)。</summary>
        public int Stomach { get; }

        /// <summary>饿死状态。</summary>
        public bool Starved { get; }

        /// <summary>是出战的那只。</summary>
        public bool Equipped { get; }

        /// <summary>吃饲料(普通型;选择成长型不吃)。</summary>
        public bool Feedable { get; }
    }

    /// <summary>
    /// 宠物窗口:光标在名册上移动、出战/收起、喂食。窗口开着时游戏要不要暂停由宿主决定(与商店窗口一样)。
    /// </summary>
    public sealed class PetWindow
    {
        /// <summary>可叠加物品类型 [feed] 的编号(取自 <see cref="StackableTypes"/> 的标签表,不另抄一份数)。</summary>
        public static readonly int FeedStackableType = StackableTypes.FromTag("[feed]");

        public const int WindowX = 98;
        public const int WindowY = 30;
        public const int WindowWidth = 604;
        public const int WindowHeight = 540;

        /// <summary>名册列表:第一行的左上角、行宽、行高、行距,一屏看到几行。</summary>
        public const int RowsX = 114;
        public const int RowsY = 86;
        public const int RowWidth = 380;
        public const int RowHeight = 56;
        public const int RowGap = 6;
        public const int VisibleRows = 6;

        /// <summary>右边的按钮:第一个按钮的左上角、宽、高、间隔(从上到下依次是出战/收起、喂食、关闭)。</summary>
        public const int ButtonX = 514;
        public const int ButtonY = 86;
        public const int ButtonWidth = 172;
        public const int ButtonHeight = 40;
        public const int ButtonGap = 12;

        private readonly PetSession _pets;
        private readonly IPetClock _clock;
        private readonly Func<ItemInfo, bool> _isFeed;
        private int _cursor;
        private int _scroll;

        /// <param name="pets">状态层的宠物会话。</param>
        /// <param name="clock">时间源(与城镇宠物场共用同一个)。</param>
        /// <param name="isFeed">哪件物品算饲料;不给用 <see cref="IsFeedItem"/>。</param>
        public PetWindow(PetSession pets, IPetClock clock, Func<ItemInfo, bool> isFeed = null)
        {
            _pets = pets ?? throw new ArgumentNullException(nameof(pets));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _isFeed = isFeed ?? IsFeedItem;
        }

        /// <summary>缺省的饲料判断:可叠加物品、类型是 [feed]。</summary>
        public static bool IsFeedItem(ItemInfo item)
        {
            return item != null && item.IsStackable && item.StackableType == FeedStackableType;
        }

        /// <summary>开着。</summary>
        public bool Open { get; private set; }

        /// <summary>出战时用户是不是正在战斗中(状态层据此让新出战的宠物进"消耗中"或"回补");窗口只在城镇里用,缺省 false。</summary>
        public bool UserFighting { get; set; }

        /// <summary>最近一次出战、收起、喂食的结果。</summary>
        public PetWindowResult LastResult { get; private set; }

        /// <summary>光标在名册上的序号(名册缩短时读出来会夹回最后一行;名册是空的为 0)。</summary>
        public int Cursor => ClampedCursor();

        /// <summary>列表最上面一行是名册的第几只(光标出了屏就跟着滚)。</summary>
        public int ScrollRow => _scroll;

        /// <summary>光标处的宠物;名册是空的为 null。</summary>
        public PetInstance Selected
        {
            get
            {
                IReadOnlyList<PetInstance> all = _pets.Roster.Pets;
                return all.Count == 0 ? null : all[ClampedCursor()];
            }
        }

        /// <summary>打开窗口;光标先落在出战的那只上(没有就在第一只)。</summary>
        public void Show()
        {
            Open = true;
            int index = IndexOf(_pets.Equipped);
            _cursor = index >= 0 ? index : 0;
            KeepCursorVisible();
        }

        public void Hide()
        {
            Open = false;
        }

        public void Toggle()
        {
            if (Open)
            {
                Hide();
            }
            else
            {
                Show();
            }
        }

        /// <summary>光标上下移动 <paramref name="delta"/> 行(夹在名册范围内,不循环)。</summary>
        public void MoveCursor(int delta)
        {
            int count = _pets.Roster.Count;
            if (count == 0)
            {
                _cursor = 0;
                _scroll = 0;
                return;
            }
            long target = (long)ClampedCursor() + delta;
            _cursor = (int)Math.Max(0L, Math.Min(count - 1, target));
            KeepCursorVisible();
        }

        /// <summary>把光标放到实例号为 <paramref name="uid"/> 的宠物上;名册里没有返回 false、光标不动。</summary>
        public bool SelectUid(int uid)
        {
            int index = IndexOf(_pets.Roster.Find(uid));
            if (index < 0)
            {
                return false;
            }
            _cursor = index;
            KeepCursorVisible();
            return true;
        }

        /// <summary>名册的每一只此刻的样子(饱食经状态层按时间源结算)。</summary>
        public List<PetWindowRow> Rows()
        {
            int now = _clock.NowSeconds;
            PetInstance equipped = _pets.Equipped;
            IReadOnlyList<PetInstance> all = _pets.Roster.Pets;
            var rows = new List<PetWindowRow>(all.Count);
            foreach (PetInstance pet in all)
            {
                rows.Add(new PetWindowRow(pet.Uid, pet.ItemCode, pet.Def.Id, pet.Def.Name, pet.Level, pet.Def.MaxLevel, pet.Exp, pet.Stomach(now),
                    pet.IsStarved(now), ReferenceEquals(pet, equipped), pet.Kind == PetKind.Normal));
            }
            return rows;
        }

        /// <summary>光标处的宠物出战(原来出战的先收起);结果也记在 <see cref="LastResult"/>。</summary>
        public PetWindowResult Equip()
        {
            PetInstance pet = Selected;
            if (pet == null)
            {
                return Remember(PetWindowResult.NoPet);
            }
            switch (_pets.Equip(pet.Uid, _clock.NowSeconds, UserFighting))
            {
                case PetEquipResult.Ok:
                    return Remember(PetWindowResult.Equipped);
                case PetEquipResult.AlreadyEquipped:
                    return Remember(PetWindowResult.AlreadyEquipped);
                default:
                    return Remember(PetWindowResult.NoPet);
            }
        }

        /// <summary>出战的宠物收起来;没有出战的返回 <see cref="PetWindowResult.NoPet"/>。</summary>
        public PetWindowResult Unequip()
        {
            return Remember(_pets.Unequip(_clock.NowSeconds) == PetEquipResult.Ok ? PetWindowResult.Unequipped : PetWindowResult.NoPet);
        }

        /// <summary>光标处的宠物已经出战就收起,否则让它出战。</summary>
        public PetWindowResult ToggleEquip()
        {
            PetInstance pet = Selected;
            if (pet != null && ReferenceEquals(pet, _pets.Equipped))
            {
                return Unequip();
            }
            return Equip();
        }

        /// <summary>
        /// 喂食:背包里拿第一件饲料喂给<b>出战的那只</b>宠物;喂进去(吃下或复活)才从背包里扣一件。
        /// 没有出战的宠物、它不吃饲料、背包里没有饲料时什么都不动。
        /// </summary>
        public PetWindowResult Feed(Inventory bag)
        {
            PetInstance pet = _pets.Equipped;
            if (pet == null)
            {
                return Remember(PetWindowResult.NoPet);
            }
            if (pet.Kind != PetKind.Normal)
            {
                return Remember(PetWindowResult.NotFeedable);
            }
            ItemInfo feed = FeedItemIn(bag);
            if (feed == null)
            {
                return Remember(PetWindowResult.NoFeedItem);
            }
            PetFeedResult fed = _pets.Feed(_clock.NowSeconds);
            switch (fed)
            {
                case PetFeedResult.Fed:
                    bag.Remove(feed.Code, 1);
                    return Remember(PetWindowResult.Fed);
                case PetFeedResult.Revived:
                    bag.Remove(feed.Code, 1);
                    return Remember(PetWindowResult.Revived);
                case PetFeedResult.NotFeedable:
                    return Remember(PetWindowResult.NotFeedable);
                default:
                    return Remember(PetWindowResult.NoPet);
            }
        }

        /// <summary>背包里第一件饲料(按页签、格子的先后);没有或背包为 null 返回 null。</summary>
        public ItemInfo FeedItemIn(Inventory bag)
        {
            if (bag == null)
            {
                return null;
            }
            for (int tab = 0; tab < Inventory.TabCount; tab++)
            {
                foreach (ItemStack stack in bag.Tab((InventoryTab)tab))
                {
                    if (stack.Count > 0 && _isFeed(stack.Info))
                    {
                        return stack.Info;
                    }
                }
            }
            return null;
        }

        /// <summary>背包里一共有多少件饲料。</summary>
        public int FeedCount(Inventory bag)
        {
            int total = 0;
            if (bag == null)
            {
                return total;
            }
            for (int tab = 0; tab < Inventory.TabCount; tab++)
            {
                foreach (ItemStack stack in bag.Tab((InventoryTab)tab))
                {
                    if (_isFeed(stack.Info))
                    {
                        total += stack.Count;
                    }
                }
            }
            return total;
        }

        /// <summary>喂食按钮能不能按:有出战的普通型宠物、背包里有饲料。</summary>
        public bool CanFeed(Inventory bag)
        {
            PetInstance pet = _pets.Equipped;
            return pet != null && pet.Kind == PetKind.Normal && FeedItemIn(bag) != null;
        }

        // ------------------------------------------------------------------ 点击

        /// <summary>窗口矩形里(含边,右下边不含)。</summary>
        public static bool InWindow(int x, int y)
        {
            return x >= WindowX && x < WindowX + WindowWidth && y >= WindowY && y < WindowY + WindowHeight;
        }

        /// <summary>点在列表的哪一行上(返回名册序号);不在行上、行是空的返回 -1。</summary>
        public int RowAt(int x, int y)
        {
            if (x < RowsX || x >= RowsX + RowWidth || y < RowsY)
            {
                return -1;
            }
            int offset = y - RowsY;
            int slot = offset / (RowHeight + RowGap);
            if (slot >= VisibleRows || offset % (RowHeight + RowGap) >= RowHeight)
            {
                return -1;
            }
            int index = _scroll + slot;
            return index < _pets.Roster.Count ? index : -1;
        }

        /// <summary>点在哪个按钮上。</summary>
        public static PetWindowButton ButtonAt(int x, int y)
        {
            if (x < ButtonX || x >= ButtonX + ButtonWidth || y < ButtonY)
            {
                return PetWindowButton.None;
            }
            int offset = y - ButtonY;
            int slot = offset / (ButtonHeight + ButtonGap);
            if (slot > 2 || offset % (ButtonHeight + ButtonGap) >= ButtonHeight)
            {
                return PetWindowButton.None;
            }
            return (PetWindowButton)(slot + 1);
        }

        /// <summary>
        /// 鼠标点击(画布坐标):点列表行就选中那一行,点按钮就做对应的操作(喂食要用 <paramref name="bag"/>),点窗口别处什么也不做;
        /// 窗口没开或点在窗口外返回 false(点击没被窗口接住),在窗口里一律返回 true。
        /// </summary>
        public bool Click(int x, int y, Inventory bag)
        {
            if (!Open || !InWindow(x, y))
            {
                return false;
            }
            int row = RowAt(x, y);
            if (row >= 0)
            {
                _cursor = row;
                KeepCursorVisible();
                return true;
            }
            switch (ButtonAt(x, y))
            {
                case PetWindowButton.Equip:
                    ToggleEquip();
                    break;
                case PetWindowButton.Feed:
                    Feed(bag);
                    break;
                case PetWindowButton.Close:
                    Hide();
                    break;
            }
            return true;
        }

        // ------------------------------------------------------------------ 内部

        private PetWindowResult Remember(PetWindowResult result)
        {
            LastResult = result;
            return result;
        }

        private int ClampedCursor()
        {
            int count = _pets.Roster.Count;
            return count == 0 ? 0 : Math.Min(_cursor, count - 1);
        }

        private int IndexOf(PetInstance pet)
        {
            if (pet == null)
            {
                return -1;
            }
            IReadOnlyList<PetInstance> all = _pets.Roster.Pets;
            for (int i = 0; i < all.Count; i++)
            {
                if (ReferenceEquals(all[i], pet))
                {
                    return i;
                }
            }
            return -1;
        }

        private void KeepCursorVisible()
        {
            int cursor = ClampedCursor();
            if (cursor < _scroll)
            {
                _scroll = cursor;
            }
            else if (cursor >= _scroll + VisibleRows)
            {
                _scroll = cursor - VisibleRows + 1;
            }
        }
    }
}
