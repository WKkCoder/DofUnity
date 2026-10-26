using System;
using System.Collections.Generic;

// 证据:E-117 只确认了物品怎么掉(服务端 _MakeDropItems 0x0830adf6 把每件掉落包成 map_item、没有格子概念);背包的页签、格子数与叠加上限
// 没有读到客户端或服务端的实现,下面的数值与分页全是推断。
namespace Dnf.Game
{
    /// <summary>背包的页签(推断:原版有装备、消耗品、材料、任务等页;具体怎么分没有查明)。</summary>
    public enum InventoryTab
    {
        Equipment = 0,
        Consumable = 1,
        Material = 2,
        Quest = 3,
    }

    /// <summary>背包里的一格:一种物品和它的数量。</summary>
    public sealed class ItemStack
    {
        public ItemStack(ItemInfo info, int count)
        {
            Info = info ?? throw new ArgumentNullException(nameof(info));
            Count = count;
        }

        public ItemInfo Info { get; }

        public int Count { get; internal set; }
    }

    /// <summary>
    /// 主角的背包:按页签存格子(每页最多 <see cref="SlotsPerTab"/> 格),可叠加物品同编号叠在一起(上限见 <see cref="StackLimitOf"/>),装备一件一格。
    /// 满了放不下就放不进(<see cref="Add"/> 返回实际放进去的数量),东西留在地上。<b>推断</b>:页签的划分、每页格数、叠加上限的缺省值都没有读到原版实现。
    /// </summary>
    public sealed class Inventory
    {
        /// <summary>页签数。</summary>
        public const int TabCount = 4;

        /// <summary>每页格数(推断)。</summary>
        public const int SlotsPerTab = 56;

        /// <summary>脚本没写 <c>[stack limit]</c> 时一格最多叠多少(推断)。</summary>
        public const int DefaultStackLimit = 1000;

        private readonly List<ItemStack>[] _tabs = new List<ItemStack>[TabCount];
        private readonly int _slotsPerTab;

        public Inventory(int slotsPerTab = SlotsPerTab)
        {
            _slotsPerTab = slotsPerTab < 1 ? 1 : slotsPerTab;
            for (int i = 0; i < TabCount; i++)
            {
                _tabs[i] = new List<ItemStack>();
            }
        }

        /// <summary>每页的格数上限。</summary>
        public int Capacity => _slotsPerTab;

        /// <summary>
        /// 物品放进哪一页(推断):装备放装备页;可叠加物品里,任务类放任务页,材料、设计图、杂项放材料页,其余放消耗品页。
        /// </summary>
        public static InventoryTab TabOf(ItemInfo info)
        {
            if (info == null || info.IsEquipment)
            {
                return InventoryTab.Equipment;
            }
            switch (info.StackableType)
            {
                case StackableTypes.Quest:
                case 19:   // [quest receive]
                    return InventoryTab.Quest;
                case StackableTypes.Material:
                case 2:    // [material expert job]
                case StackableTypes.Recipe:
                case StackableTypes.Etc:
                case 21:   // [unlimited etc]
                    return InventoryTab.Material;
                default:
                    return InventoryTab.Consumable;
            }
        }

        /// <summary>一格最多放几个:装备 1,可叠加物品取 <c>[stack limit]</c>(没写按 <see cref="DefaultStackLimit"/>)。</summary>
        public static int StackLimitOf(ItemInfo info)
        {
            if (info == null || info.IsEquipment)
            {
                return 1;
            }
            return info.StackLimit > 0 ? info.StackLimit : DefaultStackLimit;
        }

        /// <summary>这一页的全部格子(按放入的先后)。</summary>
        public IReadOnlyList<ItemStack> Tab(InventoryTab tab)
        {
            return _tabs[(int)tab];
        }

        /// <summary>背包里一共有多少件(各格数量之和)。</summary>
        public int TotalCount
        {
            get
            {
                int total = 0;
                foreach (List<ItemStack> tab in _tabs)
                {
                    foreach (ItemStack stack in tab)
                    {
                        total += stack.Count;
                    }
                }
                return total;
            }
        }

        /// <summary>占了多少格。</summary>
        public int StackCount
        {
            get
            {
                int total = 0;
                foreach (List<ItemStack> tab in _tabs)
                {
                    total += tab.Count;
                }
                return total;
            }
        }

        /// <summary>某个编号的物品一共有多少件。</summary>
        public int CountOf(int code)
        {
            int total = 0;
            foreach (List<ItemStack> tab in _tabs)
            {
                foreach (ItemStack stack in tab)
                {
                    if (stack.Info.Code == code)
                    {
                        total += stack.Count;
                    }
                }
            }
            return total;
        }

        /// <summary>
        /// 能不能把 <paramref name="count"/> 件 <paramref name="info"/> 全部放进去(先填同编号还没满的格,再占空格)。
        /// </summary>
        public bool CanAdd(ItemInfo info, int count = 1)
        {
            return count > 0 && info != null && Room(info) >= count;
        }

        private int Room(ItemInfo info)
        {
            List<ItemStack> tab = _tabs[(int)TabOf(info)];
            int limit = StackLimitOf(info);
            long room = (long)(_slotsPerTab - tab.Count) * limit;
            foreach (ItemStack stack in tab)
            {
                if (stack.Info.Code == info.Code)
                {
                    room += Math.Max(0, limit - stack.Count);
                }
            }
            return room > int.MaxValue ? int.MaxValue : (int)room;
        }

        /// <summary>放进 <paramref name="count"/> 件物品;返回实际放进去几件(放不下的不放;没有物品或数量不大于 0 返回 0)。</summary>
        public int Add(ItemInfo info, int count = 1)
        {
            if (info == null || count <= 0)
            {
                return 0;
            }
            List<ItemStack> tab = _tabs[(int)TabOf(info)];
            int limit = StackLimitOf(info);
            int remaining = count;
            foreach (ItemStack stack in tab)
            {
                if (remaining == 0)
                {
                    break;
                }
                if (stack.Info.Code != info.Code || stack.Count >= limit)
                {
                    continue;
                }
                int put = Math.Min(remaining, limit - stack.Count);
                stack.Count += put;
                remaining -= put;
            }
            while (remaining > 0 && tab.Count < _slotsPerTab)
            {
                int put = Math.Min(remaining, limit);
                tab.Add(new ItemStack(info, put));
                remaining -= put;
            }
            return count - remaining;
        }

        /// <summary>整格拿走这一页第 <paramref name="index"/> 格(后面的格子往前补);返回被拿走的格,没有这一格返回 null。</summary>
        public ItemStack TakeAt(InventoryTab tab, int index)
        {
            List<ItemStack> list = _tabs[(int)tab];
            if ((uint)index >= (uint)list.Count)
            {
                return null;
            }
            ItemStack stack = list[index];
            list.RemoveAt(index);
            return stack;
        }

        /// <summary>
        /// 从这一页第 <paramref name="index"/> 格拿走 <paramref name="count"/> 件(卖东西用):格子不存在、件数不大于 0 或超过这一格的件数时一件也不拿并返回 false;
        /// 拿空的格子整格移除(后面的往前补)。
        /// </summary>
        public bool RemoveAt(InventoryTab tab, int index, int count)
        {
            List<ItemStack> list = _tabs[(int)tab];
            if ((uint)index >= (uint)list.Count || count <= 0 || count > list[index].Count)
            {
                return false;
            }
            ItemStack stack = list[index];
            stack.Count -= count;
            if (stack.Count == 0)
            {
                list.RemoveAt(index);
            }
            return true;
        }

        /// <summary>
        /// 把这一页第 <paramref name="index"/> 格换成一件 <paramref name="info"/>(穿装备换下来的那件放回原来的格):
        /// 格子不存在、物品不属于这一页、或者物品不是装备(只有装备一格一件,可以原位换)时返回 false、什么都不动。
        /// </summary>
        public bool ReplaceAt(InventoryTab tab, int index, ItemInfo info)
        {
            List<ItemStack> list = _tabs[(int)tab];
            if ((uint)index >= (uint)list.Count || info == null || !info.IsEquipment || TabOf(info) != tab)
            {
                return false;
            }
            list[index] = new ItemStack(info, 1);
            return true;
        }

        /// <summary>整个背包的副本(格子重新建、物品信息共用):先在副本上试放,再决定要不要真放。</summary>
        public Inventory Clone()
        {
            var copy = new Inventory(_slotsPerTab);
            for (int t = 0; t < TabCount; t++)
            {
                foreach (ItemStack stack in _tabs[t])
                {
                    copy._tabs[t].Add(new ItemStack(stack.Info, stack.Count));
                }
            }
            return copy;
        }

        /// <summary>
        /// 先拿走 <paramref name="take"/>(编号与数量,拿不够当作拿光)、再按次序放进 <paramref name="give"/> 里的物品,能不能全部放下:
        /// 在副本上试,真实的背包不动。任务交付用:收集类先交出材料、再领奖励。
        /// </summary>
        public bool CanSwap(IEnumerable<KeyValuePair<int, int>> take, IEnumerable<KeyValuePair<ItemInfo, int>> give)
        {
            Inventory scratch = Clone();
            if (take != null)
            {
                foreach (KeyValuePair<int, int> t in take)
                {
                    scratch.Remove(t.Key, Math.Min(t.Value, scratch.CountOf(t.Key)));
                }
            }
            if (give != null)
            {
                foreach (KeyValuePair<ItemInfo, int> g in give)
                {
                    if (g.Key != null && g.Value > 0 && scratch.Add(g.Key, g.Value) != g.Value)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>清空整个背包(存档还原用)。</summary>
        public void Clear()
        {
            foreach (List<ItemStack> tab in _tabs)
            {
                tab.Clear();
            }
        }

        /// <summary>
        /// 存档还原用:把 <paramref name="count"/> 件 <paramref name="info"/> 放成新的格子(不并进同编号已有的格子,所以存档里的格子布局原样回来);
        /// 一格放不下的部分接着开新格,这一页满了就停。返回放进去几件(没有物品或数量不大于 0 返回 0)。
        /// </summary>
        public int RestoreStack(ItemInfo info, int count)
        {
            if (info == null || count <= 0)
            {
                return 0;
            }
            List<ItemStack> tab = _tabs[(int)TabOf(info)];
            int limit = StackLimitOf(info);
            int remaining = count;
            while (remaining > 0 && tab.Count < _slotsPerTab)
            {
                int put = Math.Min(remaining, limit);
                tab.Add(new ItemStack(info, put));
                remaining -= put;
            }
            return count - remaining;
        }

        /// <summary>拿掉 <paramref name="count"/> 件某编号的物品(先拿后放进去的格);不够就一件也不拿并返回 false。</summary>
        public bool Remove(int code, int count = 1)
        {
            if (count <= 0 || CountOf(code) < count)
            {
                return false;
            }
            int remaining = count;
            foreach (List<ItemStack> tab in _tabs)
            {
                for (int i = tab.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    ItemStack stack = tab[i];
                    if (stack.Info.Code != code)
                    {
                        continue;
                    }
                    int take = Math.Min(remaining, stack.Count);
                    stack.Count -= take;
                    remaining -= take;
                    if (stack.Count == 0)
                    {
                        tab.RemoveAt(i);
                    }
                }
            }
            return true;
        }
    }
}
