using System;

// 证据:服务端两种仓库的格子操作是同一套规则——
//   个人仓库 CCargo:IsOperatorable 0x0850d39c(格号 ≥ 0、小于现有容量、不大于 0x97)、get_empty_slot 0x0850c406(找第一个空格,找到的格号还要通过 IsOperatorable)、
//     check_item_exist 0x0850bc14(按编号找第一格同编号的物品)、_tryAddStackItem 0x0850b4b0 与 _AddStackItem 0x0850c546(同编号才并,并进去之后要过 checkStackableLimit,不过返回 −10,不拆开)、
//     tryInsertItemSpecialSlot 0x0850b570 / insert_item_special_slot 0x0850b672(指定格是空的才放进去)、delete_item 0x0850b794(整格拿光就清格、拿得比有的多就失败、装备按整件清格)、move_item 0x0850b904(两格互换);
//   账号金库 CAccountCargo:CheckValidSlot 0x0828a554、CheckSlotEmpty / GetEmptySlot、GetSpecificItemSlot 0x0828a61a(按编号找第一格)、CheckStackLimit 0x0828a670、InsertItem、DeleteItem 0x08289e3c、MoveItem 0x08289f26。
//   存入的选格顺序(CUser::exchange_interspace 0x0865f7b0 个人仓库一支与账号金库一支各有一份,写法相同):装备——指定格是空的就用它、否则第一个空格、都没有返回"放不下"(4);
//   可叠加——先找仓库里第一格同编号的:有就并进去、并完超过上限整件放不下(不会拆到别的格,也不会因为有空格就另开一格)、没有同编号才按装备那样选空格。
// 本类用 Inventory 同一个叠加上限(Inventory.StackLimitOf);服务端的缺省上限与它不同,见 NOTES(推断:服务端缺省为 9999999,这里不改 Inventory)。
namespace Dnf.Game
{
    /// <summary>"存入"这一步算出来的放法(没有改动任何东西):放在第几格、是并进已有的一叠还是占一个空格。</summary>
    public readonly struct StoragePlacement
    {
        public StoragePlacement(StorageResult result, int slot, bool merge)
        {
            Result = result;
            Slot = slot;
            Merge = merge;
        }

        /// <summary><see cref="StorageResult.Ok"/> 才能放;其他值就是放不了的原因(这时 <see cref="Slot"/> 是 −1)。</summary>
        public StorageResult Result { get; }

        /// <summary>要放进的格号。</summary>
        public int Slot { get; }

        /// <summary>为真是并进这一格里已有的同编号一叠,为假是占这个空格。</summary>
        public bool Merge { get; }

        public bool IsOk => Result == StorageResult.Ok;

        internal static StoragePlacement Fail(StorageResult result)
        {
            return new StoragePlacement(result, -1, false);
        }
    }

    /// <summary>
    /// 一个仓库的格子:定长的一排,从 0 起;现在开放的是前 <see cref="Capacity"/> 格(其余都是关着的,什么也放不进、拿不出)。
    /// 个人仓库最多 <see cref="StorageRules.PersonalMaxSlots"/> 格,账号金库最多 <see cref="StorageRules.AccountMaxSlots"/> 格;
    /// 格子不像背包那样往前补,放在哪一格就一直在哪一格。物品还是 <see cref="ItemStack"/>(同一个类),叠加上限同 <see cref="Inventory.StackLimitOf"/>。
    /// </summary>
    public sealed class StorageVault
    {
        private readonly ItemStack[] _slots;

        public StorageVault(StorageKind kind, int capacity)
        {
            Kind = kind;
            _slots = new ItemStack[StorageRules.MaxSlotsOf(kind)];
            Capacity = Math.Max(0, Math.Min(capacity, _slots.Length));
        }

        public StorageKind Kind { get; }

        /// <summary>现在开放的格数。</summary>
        public int Capacity { get; private set; }

        /// <summary>这种仓库最多能开放的格数。</summary>
        public int MaxSlots => _slots.Length;

        /// <summary>开放的格子里有物品的格数。</summary>
        public int UsedSlots
        {
            get
            {
                int used = 0;
                for (int i = 0; i < Capacity; i++)
                {
                    if (_slots[i] != null)
                    {
                        used++;
                    }
                }
                return used;
            }
        }

        /// <summary>开放的格子里空着的格数。</summary>
        public int FreeSlots => Capacity - UsedSlots;

        /// <summary>全部格子里物品的件数之和。</summary>
        public long TotalCount
        {
            get
            {
                long total = 0;
                for (int i = 0; i < Capacity; i++)
                {
                    if (_slots[i] != null)
                    {
                        total += _slots[i].Count;
                    }
                }
                return total;
            }
        }

        /// <summary>这一格开放吗(格号从 0 起、小于 <see cref="Capacity"/>)。</summary>
        public bool IsOpen(int slot)
        {
            return slot >= 0 && slot < Capacity;
        }

        /// <summary>某一格里的东西;没有开放或者是空格返回 null。</summary>
        public ItemStack SlotAt(int slot)
        {
            return IsOpen(slot) ? _slots[slot] : null;
        }

        /// <summary>第一个空着的开放格;没有返回 −1。</summary>
        public int FirstEmptySlot()
        {
            for (int i = 0; i < Capacity; i++)
            {
                if (_slots[i] == null)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>第一格装着这个编号的物品;没有返回 −1(服务端 check_item_exist / GetSpecificItemSlot)。</summary>
        public int FindStack(int code)
        {
            for (int i = 0; i < Capacity; i++)
            {
                if (_slots[i] != null && _slots[i].Info.Code == code)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>某个编号的物品在仓库里一共有多少件。</summary>
        public long CountOf(int code)
        {
            long total = 0;
            for (int i = 0; i < Capacity; i++)
            {
                if (_slots[i] != null && _slots[i].Info.Code == code)
                {
                    total += _slots[i].Count;
                }
            }
            return total;
        }

        /// <summary>
        /// 算出把 <paramref name="count"/> 件 <paramref name="info"/> 存进来放哪(不改动仓库)。<paramref name="preferredSlot"/> 是想放的格(不想指定给 −1)。
        /// 件数不大于 0、或大于一格最多能放的(装备是 1 件)返回 <see cref="StorageResult.BadQuantity"/>;可叠加物品仓库里已有同编号的,
        /// 并进第一格同编号的、并完不超过叠加上限才行,超过返回 <see cref="StorageResult.VaultFull"/>(不会拆到别的格);
        /// 否则放想放的那格(得是开放的空格),不行换第一个空格,没有空格返回 <see cref="StorageResult.VaultFull"/>。
        /// </summary>
        public StoragePlacement PlanPut(ItemInfo info, int count, int preferredSlot)
        {
            if (info == null)
            {
                return StoragePlacement.Fail(StorageResult.NoItem);
            }
            int limit = Inventory.StackLimitOf(info);
            if (count < 1 || count > limit)
            {
                return StoragePlacement.Fail(StorageResult.BadQuantity);
            }
            if (info.IsStackable)
            {
                int existing = FindStack(info.Code);
                if (existing >= 0)
                {
                    return (long)_slots[existing].Count + count > limit
                        ? StoragePlacement.Fail(StorageResult.VaultFull)
                        : new StoragePlacement(StorageResult.Ok, existing, true);
                }
            }
            int slot = IsOpen(preferredSlot) && _slots[preferredSlot] == null ? preferredSlot : FirstEmptySlot();
            return slot < 0 ? StoragePlacement.Fail(StorageResult.VaultFull) : new StoragePlacement(StorageResult.Ok, slot, false);
        }

        /// <summary>
        /// 把 <see cref="PlanPut"/> 算好的放法真正做掉(<paramref name="placement"/> 必须是刚刚对同样的物品与件数、在仓库没有变过的情况下算出来的 Ok)。
        /// 返回放进的格号;放法不是 Ok 或者对不上现在的格子时什么也不做、返回 −1。
        /// </summary>
        public int CommitPut(ItemInfo info, int count, StoragePlacement placement)
        {
            if (!placement.IsOk || info == null || count < 1 || !IsOpen(placement.Slot))
            {
                return -1;
            }
            ItemStack cell = _slots[placement.Slot];
            if (placement.Merge)
            {
                if (cell == null || cell.Info.Code != info.Code || (long)cell.Count + count > Inventory.StackLimitOf(info))
                {
                    return -1;
                }
                cell.Count += count;
                return placement.Slot;
            }
            if (cell != null || count > Inventory.StackLimitOf(info))
            {
                return -1;
            }
            _slots[placement.Slot] = new ItemStack(info, count);
            return placement.Slot;
        }

        /// <summary>
        /// 能不能从 <paramref name="slot"/> 拿走 <paramref name="count"/> 件:格子没开放返回 <see cref="StorageResult.BadSlot"/>,空格返回 <see cref="StorageResult.NoItem"/>,
        /// 件数不大于 0 或比这一格有的多返回 <see cref="StorageResult.BadQuantity"/>。不改动仓库。
        /// </summary>
        public StorageResult PlanTake(int slot, int count)
        {
            if (!IsOpen(slot))
            {
                return StorageResult.BadSlot;
            }
            ItemStack cell = _slots[slot];
            if (cell == null)
            {
                return StorageResult.NoItem;
            }
            return count < 1 || count > cell.Count ? StorageResult.BadQuantity : StorageResult.Ok;
        }

        /// <summary>从 <paramref name="slot"/> 拿走 <paramref name="count"/> 件(条件同 <see cref="PlanTake"/>);拿光整格清空。返回结果,不是 Ok 时什么也没动。</summary>
        public StorageResult Take(int slot, int count)
        {
            StorageResult result = PlanTake(slot, count);
            if (result != StorageResult.Ok)
            {
                return result;
            }
            ItemStack cell = _slots[slot];
            if (count == cell.Count)
            {
                _slots[slot] = null;
            }
            else
            {
                cell.Count -= count;
            }
            return StorageResult.Ok;
        }

        /// <summary>
        /// 两格互换(服务端 move_item / MoveItem):有一格没开放返回 <see cref="StorageResult.BadSlot"/>,两格都是空的返回 <see cref="StorageResult.NoItem"/>;
        /// 同一格什么也不动(算成功,格里得有东西)。
        /// </summary>
        public StorageResult Swap(int a, int b)
        {
            if (!IsOpen(a) || !IsOpen(b))
            {
                return StorageResult.BadSlot;
            }
            if (_slots[a] == null && _slots[b] == null)
            {
                return StorageResult.NoItem;
            }
            ItemStack temp = _slots[a];
            _slots[a] = _slots[b];
            _slots[b] = temp;
            return StorageResult.Ok;
        }

        /// <summary>
        /// 把容量扩到 <paramref name="capacity"/>(只会变大,超过上限的部分去掉);没有变化返回 false。
        /// 个人仓库扩容、账号金库升档、读档时用。
        /// </summary>
        public bool GrowTo(int capacity)
        {
            int target = Math.Min(capacity, _slots.Length);
            if (target <= Capacity)
            {
                return false;
            }
            Capacity = target;
            return true;
        }

        /// <summary>
        /// 读档用:把一格直接放成 <paramref name="count"/> 件 <paramref name="info"/>。格子没开放、已经有东西、物品为空、件数不在 1 到一格上限之间时返回 false、什么也不动。
        /// </summary>
        public bool RestoreSlot(int slot, ItemInfo info, int count)
        {
            if (!IsOpen(slot) || _slots[slot] != null || info == null || count < 1 || count > Inventory.StackLimitOf(info))
            {
                return false;
            }
            _slots[slot] = new ItemStack(info, count);
            return true;
        }
    }
}
