using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-126(服务端 CBattle_Field::add_item_curr_map 0x08302bc6 进房间时给每个 [item] 动作生成物品;CObjectDrop::generateRandomItem 0x0853818a、
// CObjectDrop::generateSpecificItem 0x08538234、CDungeonDrop::generateSpecificItem 0x08539c3e;.dgn 的两个标签读自 ImportDungeonScript 0x088e5faa;
// 全部是只读静态读码,取值范围与调用顺序可复现,具体随机数列不可复现)。
namespace Dnf.Game
{
    /// <summary>一对 (物品编号, 权重):权重是万分之几的累计量,按顺序累加后与随机数比较。</summary>
    public readonly struct WeightedItem
    {
        public WeightedItem(int itemId, int weight)
        {
            ItemId = itemId;
            Weight = weight;
        }

        public int ItemId { get; }
        public int Weight { get; }
    }

    /// <summary>
    /// 副本里一项"特殊物件的物品池"(<c>.dgn</c> 的 <c>[special passive object item]</c> 的一项):等级(-1 = 用副本的标准等级)与一串 (物品编号, 权重)。
    /// 地图上 <c>[item]</c> 动作的第一个数就是它在这个标签里的下标。
    /// </summary>
    public sealed class SpecialItemEntry
    {
        public SpecialItemEntry(int level, IReadOnlyList<WeightedItem> items)
        {
            Level = level;
            Items = items ?? new List<WeightedItem>();
        }

        public int Level { get; }
        public IReadOnlyList<WeightedItem> Items { get; }
    }

    /// <summary>
    /// 一座副本里和"物件掉物品"有关的两份数据(证据 E-126):<c>[special passive object item]</c>(每项:序号(服务端丢弃)、等级、个数 N、N 对 (物品编号, 权重))
    /// 与 <c>[dungeon object drop]</c>(一串 (物品编号, 权重),直到值用完)。没有这些标签是空表。
    /// </summary>
    public sealed class DungeonObjectItems
    {
        public static readonly DungeonObjectItems Empty = new DungeonObjectItems(new List<SpecialItemEntry>(), new List<WeightedItem>());

        public DungeonObjectItems(IReadOnlyList<SpecialItemEntry> entries, IReadOnlyList<WeightedItem> dungeonDrop)
        {
            Entries = entries ?? new List<SpecialItemEntry>();
            DungeonDrop = dungeonDrop ?? new List<WeightedItem>();
        }

        public IReadOnlyList<SpecialItemEntry> Entries { get; }

        /// <summary><c>[dungeon object drop]</c>:每个有 <c>[item]</c> 动作的物件额外再掷一次的整座副本通用的池。</summary>
        public IReadOnlyList<WeightedItem> DungeonDrop { get; }

        public static DungeonObjectItems Parse(ScriptLine[] dungeon)
        {
            if (dungeon == null)
            {
                return Empty;
            }
            var entries = new List<SpecialItemEntry>();
            var drop = new List<WeightedItem>();
            foreach (ScriptLine line in dungeon)
            {
                if (line.TagIs("[special passive object item]"))
                {
                    ReadEntries(line.Ints(), entries);
                }
                else if (line.TagIs("[dungeon object drop]"))
                {
                    drop.Clear();                                  // 加载器每次都重新读,后一次覆盖前一次
                    List<int> n = line.Ints();
                    for (int i = 0; i + 1 < n.Count; i += 2)
                    {
                        drop.Add(new WeightedItem(n[i], n[i + 1]));
                    }
                }
            }
            return entries.Count == 0 && drop.Count == 0 ? Empty : new DungeonObjectItems(entries, drop);
        }

        /// <summary>每项:序号、等级、个数 N、N 对;数不够就停(已完整读出的项保留)。</summary>
        private static void ReadEntries(List<int> n, List<SpecialItemEntry> into)
        {
            int i = 0;
            while (i + 2 < n.Count)
            {
                int level = n[i + 1];
                int count = n[i + 2];
                i += 3;
                if (count < 0 || i + 2 * count > n.Count)
                {
                    return;
                }
                var items = new List<WeightedItem>(count);
                for (int k = 0; k < count; k++)
                {
                    items.Add(new WeightedItem(n[i + 2 * k], n[i + 2 * k + 1]));
                }
                i += 2 * count;
                into.Add(new SpecialItemEntry(level, items));
            }
        }
    }

    /// <summary>
    /// 一个 <c>[item]</c> 动作的物品生成(服务端 <c>CBattle_Field::add_item_curr_map</c> 对每一项掉落记录依次做三件事,证据 E-126):
    /// <list type="number">
    /// <item><b>随机物品</b> p3 次:每次按"物件掉落表"(<c>Etc/ItemDropInfo_Object.etc</c>,格式同怪物掉落表)掷一遍
    /// (<c>CMonsterDrop::_generateRandomItem</c>:怪物等级 = 副本等级 = 这一项的等级,种类 0,难度取副本难度,单人,掉落率 100;金币部分服务端不看);</item>
    /// <item><b>指定物品</b> p2 次:随机数取 2 到 10000(含),在这一项自带的 (物品编号, 权重) 里按权重累加,第一个"随机数 &lt; 累计权重"的就是掉的物品;累加完了还没中就不掉;</item>
    /// <item><b>整座副本的通用池</b> 一次:随机数取 1 到 10000(含),在 <c>[dungeon object drop]</c> 里同样累加。</item>
    /// </list>
    /// p2、p3 在服务端是有符号字节(<c>-1</c> 与以下都是零次)。这一项的等级是 <c>.dgn</c> 里写的(不是 -1 时),否则用副本的标准等级(1 到 200),
    /// 超出范围的整项不生成。生成的顺序是 随机、指定、通用。
    /// </summary>
    public static class ObjectItemRoll
    {
        /// <summary>指定物品的随机数下限(<c>CObjectDrop::generateSpecificItem</c> 传 2;<c>CDungeonDrop</c> 传 1,是服务端两处不同的写法)。</summary>
        public const int EntryPickMin = 2;

        public const int DungeonPickMin = 1;
        public const int PickMax = 10000;

        /// <summary>
        /// 掷一个 <c>[item]</c> 动作(<paramref name="entryIndex"/>、<paramref name="specificCount"/>、<paramref name="randomCount"/> 就是动作的前三个数)。
        /// <paramref name="table"/> 与 <paramref name="pool"/> 是物件掉落表及按它建的物品池(为 null 就不出随机物品,但随机数照样不消耗);
        /// <paramref name="resolve"/> 把物品编号换成 <see cref="ItemInfo"/>(找不到的编号被跳过)。
        /// </summary>
        public static List<ItemInfo> Roll(DungeonObjectItems items, int entryIndex, int specificCount, int randomCount, int standardLevel, int difficulty,
            MonsterDropTable table, ItemPool pool, Func<int, ItemInfo> resolve, IDropRandom random)
        {
            var result = new List<ItemInfo>();
            if (items == null || random == null || entryIndex < 0 || entryIndex >= items.Entries.Count)
            {
                return result;
            }
            SpecialItemEntry entry = items.Entries[entryIndex];
            int level = entry.Level != -1 ? entry.Level : standardLevel;
            if (entry.Level == -1 && (standardLevel < 1 || standardLevel > 200))
            {
                return result;                                    // 副本的标准等级不在 1 到 200:整项不生成
            }
            int randoms = unchecked((sbyte)randomCount);
            int specifics = unchecked((sbyte)specificCount);
            if (table != null && pool != null)
            {
                for (int i = 0; i < randoms; i++)
                {
                    var context = new DropContext(level, 0, level, difficulty, 1, 0, 100, 1);
                    foreach (RolledItem rolled in ItemDrop.Roll(table, pool, context, random))
                    {
                        result.Add(rolled.Info);
                    }
                }
            }
            for (int i = 0; i < specifics; i++)
            {
                Add(result, resolve, Pick(entry.Items, EntryPickMin, random));
            }
            Add(result, resolve, Pick(items.DungeonDrop, DungeonPickMin, random));        // 服务端对每一项都调一次,池是空的也先掷数
            return result;
        }

        /// <summary>
        /// 一次按权重的挑选:随机数取 [<paramref name="min"/>, 10000](含);按顺序累加权重,第一个"随机数 &lt; 累计"的物品编号;没中返回 -1。
        /// 服务端先掷数再遍历,所以池是空的也消耗一个随机数。
        /// </summary>
        public static int Pick(IReadOnlyList<WeightedItem> pairs, int min, IDropRandom random)
        {
            int roll = random.Range(min, PickMax);
            int sum = 0;
            foreach (WeightedItem w in pairs)
            {
                sum += w.Weight;
                if (roll < sum)
                {
                    return w.ItemId;
                }
            }
            return -1;
        }

        private static void Add(List<ItemInfo> into, Func<int, ItemInfo> resolve, int itemId)
        {
            if (itemId < 0 || resolve == null)
            {
                return;
            }
            ItemInfo info = resolve(itemId);
            if (info != null)
            {
                into.Add(info);
            }
        }
    }
}
