using System;
using System.Collections.Generic;

// 证据:E-117(服务端 CMonsterDrop::_generateRandomItem 0x08535ed2 里金币之后的物品一段:类型 1 到 4 各掷一次概率、再掷稀有度、再从池里取;
// CLuckPoint::GetItemRarity 0x08550be4;CItemGenRateTable::chooseItem 0x085349a4)。
namespace Dnf.Game
{
    /// <summary>掷出来的一件物品:来自哪一类(1 到 4,见 <see cref="ItemPool.TypeAccepts"/>)与物品本身。</summary>
    public readonly struct RolledItem
    {
        public RolledItem(int type, ItemInfo info)
        {
            Type = type;
            Info = info;
        }

        /// <summary>掉落类型 1 到 4。</summary>
        public int Type { get; }

        public ItemInfo Info { get; }
    }

    /// <summary>
    /// 一只怪物死后的物品掷骰(服务端 <c>CMonsterDrop::_generateRandomItem</c> 里金币之后的那一段,证据 E-117):
    /// <list type="number">
    /// <item>按副本等级选概率行(没有行就不掉);</item>
    /// <item>类型 1 到 4 依次各来一遍:概率 = <see cref="MonsterDropTable.ItemProbability"/>(万分之几);随机数取 0 到 10000(含),
    /// <b>不大于</b>概率才掉(金币是"小于",物品是"小于等于");</item>
    /// <item>掉了就再掷稀有度:随机数取 1 到 1000000(含),<see cref="MonsterDropTable.ItemRarity"/> 在这一类的六个分界里找档;</item>
    /// <item>用怪物自己的等级从池里取一件(<see cref="ItemPool.Choose"/>);池是空的就没有(骰子照扔)。每一类最多掉一件,所以一次最多四件。</item>
    /// </list>
    /// 服务端对装备还会按"怪物等级 − 物品等级"随机一个品级与强化值(<c>generateEquipGrade</c> 0x08533932、<c>generateUpgrade</c> 0x08533676),
    /// 并消耗幸运点;这里都没有做,掉出来的装备是原样的。
    /// </summary>
    public static class ItemDrop
    {
        /// <summary>
        /// 掷一只怪物的物品,返回每一件(可能是空表)。<paramref name="drops"/>、<paramref name="pool"/> 或 <paramref name="random"/> 为 null、
        /// 种类不在 0 到 3 之间时返回空表(与金币同一个入口规则)。<see cref="DropContext.Rolls"/> 次各掷一遍。
        /// </summary>
        public static List<RolledItem> Roll(MonsterDropTable drops, ItemPool pool, DropContext context, IDropRandom random)
        {
            var items = new List<RolledItem>();
            if (drops == null || pool == null || random == null || context.Role < 0 || context.Role >= MonsterDropTable.RoleCount)
            {
                return items;
            }
            for (int n = 0; n < context.Rolls; n++)
            {
                RollOnce(drops, pool, context, random, items);
            }
            return items;
        }

        private static void RollOnce(MonsterDropTable drops, ItemPool pool, DropContext c, IDropRandom random, List<RolledItem> items)
        {
            int row = drops.FindRow(c.DungeonLevel);
            if (row < 0)
            {
                return;
            }
            for (int type = 1; type < MonsterDropTable.TypeCount; type++)
            {
                int probability = drops.ItemProbability(row, type, c.Role, c.Difficulty, c.Members, c.DropRate);
                int roll = random.NextInclusive(MonsterDropTable.RollMax);
                if (roll > probability)
                {
                    continue;
                }
                int rarityRoll = random.Range(1, MonsterDropTable.RarityRollMax);
                int rarity = drops.ItemRarity(type, rarityRoll);
                ItemInfo item = pool.Choose(type, rarity, c.MonsterLevel, random);
                if (item != null)
                {
                    items.Add(new RolledItem(type, item));
                }
            }
        }
    }
}
