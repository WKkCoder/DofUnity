using System;
using System.Collections.Generic;

// 证据(服务端静态读码 + 本机全库数据普查,数值与编号不入库):
// ImportEquipmentScript 0x08991aeb 把 [set item](成员表,vector<int>)、[piece set ability] N 与 [set ability](键固定 0)读进装备对象;
// 服务端没有"数几件套装"的消费者(这部分在客户端,受保护读不到),所以件数口径是推断:
//   · 全库 1195 个套装主件,每个成员表的成员数都等于成员的不同部位数(没有重复编号),所以"穿齐"= 成员表里每一件都穿着;
//   · 成员表只有 1041 张不同的:25 张被 2 到 16 个主件共用(表完全相同,加成块也基本相同——24 张的穿齐与件数档文本完全一致),这些主件之间的成员,
//     [set item master] 指回的主件不统一(有的指向这个、有的指向那个、有的没写)。所以件数不能只数"指回同一个主件"的穿着件,按成员表数;
//     同一张成员表只算一套(取编号最小的主件)——穿着件指向不同的共用主件时不会把同一套加成算两次;
//   · [piece set ability] 的键只有 3、5、8 三档,都不大于所在套装的成员数,按"穿满 N 件"理解;各档累计生效(穿 5 件时 3 件档也在),这是推断;
//   · [set ability] 块里是主要数值属性(物理/魔法攻击、血蓝上限、防御、速度……),按"穿齐"理解,这也是推断。
// 套装徽章(时装部位的套装,importEquipmentPartSetInfo 0x089073d9 / insert_item_parts_set 0x08622cdc)这一步没有时装格,不处理。
namespace Dnf.Game
{
    /// <summary>某一套套装此刻的穿着状态(<see cref="EquipSets.Evaluate"/> 的一项)。</summary>
    public sealed class EquipSetState
    {
        internal EquipSetState(int master, int worn, int total, bool fullActive, IReadOnlyList<int> tiers)
        {
            Master = master;
            Worn = worn;
            Total = total;
            FullActive = fullActive;
            ActiveTiers = tiers;
        }

        /// <summary>套装主件的编号(带 <c>[set item]</c> 成员表的那件)。</summary>
        public int Master { get; }

        /// <summary>穿着的成员件数。</summary>
        public int Worn { get; }

        /// <summary>成员总数(成员表的不同编号个数;主件没有成员表时为 0)。</summary>
        public int Total { get; }

        /// <summary>穿齐了:<c>[set ability]</c> 块的加成生效。</summary>
        public bool FullActive { get; }

        /// <summary>已经达到的"满 N 件"档(<c>[piece set ability] N</c>),从小到大。</summary>
        public IReadOnlyList<int> ActiveTiers { get; }

        /// <summary>这一档(满 <paramref name="pieces"/> 件)已经生效。</summary>
        public bool HasTier(int pieces)
        {
            foreach (int tier in ActiveTiers)
            {
                if (tier == pieces)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>套装的件数判定与加成:按穿着的装备数成员件数,穿齐或满 N 件才给对应的加成。</summary>
    public static class EquipSets
    {
        /// <summary>
        /// 一件装备所属套装的主件编号:写了 <c>[set item master]</c> 就用它,自己带成员表(主件)就是自己,都没有则为 0。
        /// </summary>
        public static int MasterOf(ItemInfo item)
        {
            EnhanceData data = item?.Enhance;
            if (data == null)
            {
                return 0;
            }
            if (data.SetMaster != 0)
            {
                return data.SetMaster;
            }
            return data.SetMembers.Count > 0 ? item.Code : 0;
        }

        /// <summary>
        /// 逐套算穿着状态(按主件编号从小到大)。穿着件指向的主件(<see cref="MasterOf"/>)是候选;<paramref name="lookup"/> 按物品编号取定义
        /// (取主件的成员表与加成块),取不到时若主件本身穿着就用穿着的那件。
        /// 成员件数 = <b>所有</b>穿着的装备里编号落在成员表内的不同编号数(不看这件自己指向哪个主件);同一张成员表被几个主件共用时只算一套(编号最小的)。
        /// 主件没有成员表时按"指向这个主件的穿着件数"算,且没法判断穿齐(只能有"满 N 件"档)。没有任何成员穿着的套装不出现在结果里。
        /// </summary>
        public static IReadOnlyList<EquipSetState> Evaluate(IEnumerable<ItemInfo> worn, Func<int, ItemInfo> lookup)
        {
            var candidates = new SortedDictionary<int, HashSet<int>>();
            var wornByCode = new Dictionary<int, ItemInfo>();
            if (worn != null)
            {
                foreach (ItemInfo item in worn)
                {
                    if (item == null)
                    {
                        continue;
                    }
                    wornByCode[item.Code] = item;
                    int master = MasterOf(item);
                    if (master == 0)
                    {
                        continue;
                    }
                    if (!candidates.TryGetValue(master, out HashSet<int> pointing))
                    {
                        pointing = new HashSet<int>();
                        candidates[master] = pointing;
                    }
                    pointing.Add(item.Code);
                }
            }
            var states = new List<EquipSetState>();
            var seenLists = new List<HashSet<int>>();
            foreach (KeyValuePair<int, HashSet<int>> candidate in candidates)
            {
                ItemInfo masterItem = lookup?.Invoke(candidate.Key);
                if (masterItem == null)
                {
                    wornByCode.TryGetValue(candidate.Key, out masterItem);
                }
                EnhanceData data = masterItem?.Enhance;
                if (data == null || !data.IsSetMaster)
                {
                    continue;
                }
                var members = new HashSet<int>(data.SetMembers);
                int total = members.Count;
                int count;
                if (total == 0)
                {
                    count = candidate.Value.Count;
                }
                else
                {
                    if (seenLists.Exists(list => list.SetEquals(members)))
                    {
                        continue;
                    }
                    seenLists.Add(members);
                    count = 0;
                    foreach (int code in wornByCode.Keys)
                    {
                        if (members.Contains(code))
                        {
                            count++;
                        }
                    }
                }
                if (count == 0)
                {
                    continue;
                }
                bool full = data.SetFullBonus != null && total > 0 && count >= total;
                var tiers = new List<int>();
                foreach (SetPieceBonus tier in data.SetPieceBonuses)
                {
                    if (tier.Pieces >= 1 && count >= tier.Pieces && !tiers.Contains(tier.Pieces))
                    {
                        tiers.Add(tier.Pieces);
                    }
                }
                states.Add(new EquipSetState(candidate.Key, count, total, full, tiers));
            }
            return states;
        }

        /// <summary>
        /// 套装带来的属性项:每个已生效的块(穿齐的 <c>[set ability]</c>、达到的每一档 <c>[piece set ability] N</c>)一项,
        /// 汇总时各自折算再相加(与每件装备同一口径)。没有套装生效返回空表。
        /// </summary>
        public static IReadOnlyList<EquipStats> Terms(IEnumerable<ItemInfo> worn, Func<int, ItemInfo> lookup)
        {
            var terms = new List<EquipStats>();
            foreach (EquipSetState state in Evaluate(worn, lookup))
            {
                if (!state.FullActive && state.ActiveTiers.Count == 0)
                {
                    continue;
                }
                ItemInfo masterItem = lookup?.Invoke(state.Master);
                if (masterItem == null && worn != null)
                {
                    foreach (ItemInfo item in worn)
                    {
                        if (item != null && item.Code == state.Master)
                        {
                            masterItem = item;
                            break;
                        }
                    }
                }
                EnhanceData data = masterItem?.Enhance;
                if (data == null)
                {
                    continue;
                }
                if (state.FullActive && !data.SetFullBonus.IsEmpty)
                {
                    terms.Add(data.SetFullBonus);
                }
                foreach (SetPieceBonus tier in data.SetPieceBonuses)
                {
                    if (state.HasTier(tier.Pieces) && !tier.Stats.IsEmpty)
                    {
                        terms.Add(tier.Stats);
                    }
                }
            }
            return terms;
        }
    }
}
