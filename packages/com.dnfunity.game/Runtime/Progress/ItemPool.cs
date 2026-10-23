using System;
using System.Collections.Generic;

// 证据:E-117(服务端 CItemGenRateTable::generateTable 0x0853466c 建池、_itemTypeVerify 0x08534906 分类型、chooseItem 0x085349a4 按权重取)。
namespace Dnf.Game
{
    /// <summary>
    /// 怪物掉落的物品池。服务端在加载 <c>CMonsterDrop</c> 时对每个(类型 1 到 4、稀有度 0 到 5、等级 1 到 200)预先建一张
    /// "累计权重 → 物品"的有序表(<c>std::map</c>):等级 L 的表收的是 <c>[grade]</c> 落在 [L − 向下, L + 向上 − 1](窗口来自
    /// <c>[item drop ref table]</c>,见 <see cref="MonsterDropTable.ItemWindow"/>,再截到 200 以内)的物品,条件是
    /// <c>[creation rate]</c> 不为 0、稀有度相同、类型相符(<see cref="TypeAccepts"/>)、不是特殊怪物才掉的(<c>[special monster drop]</c>);
    /// 每收一件把它的 <c>[creation rate]</c> 加进累计权重,以累计值为键存下。取物品时在 [0, 总权重](含两端)里掷一个整数,
    /// 取第一个键不小于它的(<c>lower_bound</c>)。这里按需建表并缓存,物品按编号升序放(服务端按哈希表的遍历顺序,只影响同一个数落在哪件上,不影响各件的概率)。
    /// </summary>
    public sealed class ItemPool
    {
        /// <summary>物品等级(<c>[grade]</c>)分桶的上界:服务端的数组有 201 个桶(0 到 200)。</summary>
        public const int GradeMax = 200;

        private readonly MonsterDropTable _table;
        private readonly SortedDictionary<int, List<ItemInfo>> _byGrade = new SortedDictionary<int, List<ItemInfo>>();
        private readonly Dictionary<int, Slot> _slots = new Dictionary<int, Slot>();

        /// <summary>一张(类型、稀有度、等级)的表:<see cref="Keys"/> 是累计权重(严格递增),<see cref="Items"/> 与它一一对应。</summary>
        private sealed class Slot
        {
            public int[] Keys;
            public ItemInfo[] Items;
        }

        /// <param name="items">候选物品(通常是 <see cref="ItemCatalog.Droppable"/>);<c>[creation rate]</c> 不大于 0 或特殊怪物掉落的会被排除。</param>
        /// <param name="table">给窗口(<c>[item drop ref table]</c>)用的怪物掉落表。</param>
        public ItemPool(IEnumerable<ItemInfo> items, MonsterDropTable table)
        {
            _table = table ?? throw new ArgumentNullException(nameof(table));
            if (items == null)
            {
                return;
            }
            foreach (ItemInfo item in items)
            {
                if (item == null || item.CreationRate <= 0 || item.SpecialMonsterDrop || item.Grade < 0 || item.Grade > GradeMax)
                {
                    continue;
                }
                if (!_byGrade.TryGetValue(item.Grade, out List<ItemInfo> bucket))
                {
                    bucket = new List<ItemInfo>();
                    _byGrade[item.Grade] = bucket;
                }
                bucket.Add(item);
                Items++;
            }
            foreach (List<ItemInfo> bucket in _byGrade.Values)
            {
                bucket.Sort((a, b) => a.Code.CompareTo(b.Code));
            }
        }

        /// <summary>进了池的物品件数(排除之后)。</summary>
        public int Items { get; }

        /// <summary>
        /// 服务端 <c>_itemTypeVerify</c>:装备(不可叠加)属于类型 2,宠物与宠物装备另外还属于类型 4;
        /// 可叠加物品属于类型 1,其中种类为"设计图"(<see cref="StackableTypes.Recipe"/>)的另外还属于类型 3。
        /// </summary>
        public static bool TypeAccepts(int type, ItemInfo item)
        {
            if (item == null)
            {
                return false;
            }
            if (item.IsEquipment)
            {
                return type == 2 || (type == 4 && item.IsCreature);
            }
            return type == 1 || (type == 3 && item.StackableType == StackableTypes.Recipe);
        }

        private Slot GetSlot(int type, int rarity, int level)
        {
            int key = (type * MonsterDropTable.RarityCount + rarity) * (GradeMax + 1) + level;
            if (_slots.TryGetValue(key, out Slot cached))
            {
                return cached;
            }
            var keys = new List<int>();
            var chosen = new List<ItemInfo>();
            _table.ItemWindow(level, out int before, out int after);
            long total = 0;
            for (int offset = -before; offset < after && level + offset <= GradeMax; offset++)
            {
                int grade = level + offset;
                if (grade < 0 || !_byGrade.TryGetValue(grade, out List<ItemInfo> bucket))
                {
                    continue;
                }
                foreach (ItemInfo item in bucket)
                {
                    if (item.Rarity != rarity || !TypeAccepts(type, item))
                    {
                        continue;
                    }
                    total += item.CreationRate;
                    if (total > int.MaxValue)
                    {
                        total = int.MaxValue;   // 服务端用 32 位累计,真实数据远小于它;饱和之后不再有新键(键必须严格递增)
                        continue;
                    }
                    keys.Add((int)total);
                    chosen.Add(item);
                }
            }
            var slot = new Slot { Keys = keys.ToArray(), Items = chosen.ToArray() };
            _slots[key] = slot;
            return slot;
        }

        private static bool InRange(int type, int rarity, int level)
        {
            return type >= 1 && type < MonsterDropTable.TypeCount && rarity >= 0 && rarity < MonsterDropTable.RarityCount
                && level >= 1 && level <= GradeMax;
        }

        /// <summary>这张表(类型、稀有度、怪物等级)里有多少件候选物品;参数越界为 0。</summary>
        public int CandidateCount(int type, int rarity, int level)
        {
            return InRange(type, rarity, level) ? GetSlot(type, rarity, level).Items.Length : 0;
        }

        /// <summary>这张表的总权重(最后一个累计值);空表或参数越界为 0。</summary>
        public int TotalWeight(int type, int rarity, int level)
        {
            if (!InRange(type, rarity, level))
            {
                return 0;
            }
            Slot slot = GetSlot(type, rarity, level);
            return slot.Keys.Length == 0 ? 0 : slot.Keys[slot.Keys.Length - 1];
        }

        /// <summary>
        /// 服务端 <c>chooseItem</c>:表是空的(或参数越界)返回 null(服务端返回 −1);否则在 [0, 总权重] 里掷一个整数
        /// (<c>CMTRand::randInt</c> 含上界),取第一个累计权重不小于它的物品。
        /// 服务端对类型 2 还先试一遍"随机选项物品"那一路(<c>CRandomOptionItemHandle::choose_item</c>),没有命中才走这里;那一路没有实现。
        /// </summary>
        public ItemInfo Choose(int type, int rarity, int level, IDropRandom random)
        {
            if (random == null || !InRange(type, rarity, level))
            {
                return null;
            }
            Slot slot = GetSlot(type, rarity, level);
            if (slot.Keys.Length == 0)
            {
                return null;
            }
            int roll = random.NextInclusive(slot.Keys[slot.Keys.Length - 1]);
            int index = Array.BinarySearch(slot.Keys, roll);
            if (index < 0)
            {
                index = ~index;      // 第一个比 roll 大的
            }
            if (index >= slot.Keys.Length)
            {
                index = slot.Keys.Length - 1;
            }
            return slot.Items[index];
        }
    }
}
