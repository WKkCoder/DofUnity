using System;
using System.Collections.Generic;

// 证据:服务端读码(静态,没运行)。随机箱:Dispatcher_UseBoosterItem::dispatch_sig 0x08208d40 读包后查状态必须是 3(城镇)、不在交易、职业可用
// (check_change_job_type 0x0850eba2),交给 CCeraShop::ProcessSpecialItem 0x0832587e → respond_special_item_booster 0x0827ca4a → getBoosterItem 0x0850f75e 抽取;
// 礼包:Dispatcher_CeraPackageOpen 0x081e788a(类型 38)→ respond_special_item_package 0x0827c204 按 [package data] 全给;发放在 _processAdditionalItem 0x083267a2。
// 服务端放不下的会走邮件(插入失败 −1),本地没有邮箱,所以放不下就不开、什么都不动。抽数函数 get_rand_int 0x086b1b87:n 大于 0 时近似均匀地取 0 到 n−1。
namespace Dnf.Game
{
    /// <summary>随机源:返回 0 到 <paramref name="maxExclusive"/> − 1 的整数(<paramref name="maxExclusive"/> 总是大于 0)。测试里换成固定值,正式用 <see cref="ItemOpener.RollFrom"/>。</summary>
    public delegate int RollSource(int maxExclusive);

    /// <summary>开出来的一项:物品与数量。</summary>
    public sealed class OpenGrant
    {
        public OpenGrant(ItemInfo info, int count)
        {
            Info = info ?? throw new ArgumentNullException(nameof(info));
            Count = count;
        }

        public ItemInfo Info { get; }

        public int Count { get; }
    }

    /// <summary>开一件的结果:<see cref="Result"/> 不是 <see cref="UseResult.Ok"/> 时什么都没变(没扣、没发、没起冷却),<see cref="Grants"/> 是空表。</summary>
    public sealed class OpenResult
    {
        private static readonly IReadOnlyList<OpenGrant> None = new OpenGrant[0];

        public OpenResult(UseResult result, IReadOnlyList<OpenGrant> grants = null)
        {
            Result = result;
            Grants = grants ?? None;
        }

        public UseResult Result { get; }

        /// <summary>发出去的东西(按抽到的先后,同一件抽中几次就有几项,没有合并)。</summary>
        public IReadOnlyList<OpenGrant> Grants { get; }

        public bool Ok => Result == UseResult.Ok;
    }

    /// <summary>
    /// 开启类(随机箱 <see cref="ItemUseClass.OpenBox"/>、固定礼包 <see cref="ItemUseClass.OpenPackage"/>)的使用流程:
    /// 检查 → 抽 → 试放 → 扣一件 → 发 → 起冷却。<b>原子</b>:任何一道检查不过,背包、冷却、随机源之外的一切都不动;
    /// 过了才扣一件并发出全部奖励(先在背包副本上试过放得下,所以不会"扣了没给"或"只给一半")。
    /// 冷却与背包是药水用的同一套(<see cref="ItemCooldowns"/> 与 <see cref="Inventory"/>),等级职业检查是同一个函数(<see cref="ConsumableRules.CheckUser"/>)。
    /// </summary>
    public sealed class ItemOpener
    {
        /// <summary>一个块最多抽几次(本地的防呆上限,全库最大是 3;服务端没有这条)。</summary>
        public const int MaxDrawsPerBlock = 100;

        private readonly Func<int, ItemInfo> _lookup;
        private readonly RollSource _roll;

        /// <param name="lookup">物品编号 → 物品信息(找不到返回 null),通常是 <see cref="ItemCatalog.Get"/>。</param>
        /// <param name="roll">随机源。</param>
        public ItemOpener(Func<int, ItemInfo> lookup, RollSource roll)
        {
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            _roll = roll ?? throw new ArgumentNullException(nameof(roll));
        }

        /// <summary>用 <see cref="Random"/> 做随机源。</summary>
        public static RollSource RollFrom(Random random)
        {
            if (random == null)
            {
                throw new ArgumentNullException(nameof(random));
            }
            return max => random.Next(max);
        }

        /// <summary>
        /// 这件物品开一次会发什么(编号与数量),只看内容、不碰背包:随机箱按块抽,礼包按脚本里的次序全给。
        /// 不是开启类、没有对应的数据、数据有坏、或有本地发不了的块种类(商城币、宠物)时返回 null;返回空表表示这一次什么也抽不出(数据退化:总权重 0、抽数 0、抽到的项数量不为正)。
        /// </summary>
        public static List<PackageEntry> Draw(ItemInfo box, RollSource roll)
        {
            if (roll == null)
            {
                throw new ArgumentNullException(nameof(roll));
            }
            ItemContents contents = box?.Contents;
            if (contents == null || contents.Malformed)
            {
                return null;
            }
            switch (ItemUseTable.ClassOf(box))
            {
                case ItemUseClass.OpenPackage:
                    if (!contents.HasPackage)
                    {
                        return null;
                    }
                    var fixedItems = new List<PackageEntry>(contents.Package.Count);
                    foreach (PackageEntry entry in contents.Package)
                    {
                        if (entry.Count > 0)
                        {
                            fixedItems.Add(entry);             // 数量不为正的项什么也不给(全库没有这样的项)
                        }
                    }
                    return fixedItems;
                case ItemUseClass.OpenBox:
                    if (!contents.HasBooster || !contents.AllBlocksGrantable)
                    {
                        return null;
                    }
                    var picked = new List<PackageEntry>();
                    foreach (BoosterBlock block in contents.Boosters)
                    {
                        int draws = Math.Min(block.Draws, MaxDrawsPerBlock);
                        for (int d = 0; d < draws && block.TotalWeight > 0; d++)
                        {
                            int index = block.IndexAt(roll((int)Math.Min(block.TotalWeight, int.MaxValue)));
                            if (index >= 0 && block.Entries[index].Count > 0)
                            {
                                picked.Add(new PackageEntry(block.Entries[index].Code, block.Entries[index].Count));
                            }
                        }
                    }
                    return picked;
                default:
                    return null;
            }
        }

        /// <summary>
        /// 开一件 <paramref name="box"/>。检查次序(每一道不过都原样返回,什么也不动):是开启类(<see cref="UseResult.NotConsumable"/>)→ 这里能开
        /// (<see cref="UseResult.Unavailable"/>:开启类只在城镇,服务端要求用户状态是 3)→ 等级、职业(<see cref="UseResult.LevelTooLow"/>、<see cref="UseResult.WrongJob"/>)→
        /// 背包里有(<see cref="UseResult.NotInBag"/>)→ 不在冷却(<see cref="UseResult.Cooling"/>)→ 内容本地发得了(<see cref="UseResult.NotYet"/>)→
        /// 抽得出东西(<see cref="UseResult.NoEffect"/>)→ 抽到的编号都认得(<see cref="UseResult.NotYet"/>)→ 先拿走这一件后放得下全部奖励(<see cref="UseResult.BagFull"/>)。
        /// 全过了:扣一件、起冷却、把奖励交给 <paramref name="give"/>(没给就直接放进 <paramref name="bag"/>),返回 <see cref="UseResult.Ok"/>。
        /// </summary>
        /// <param name="bag">主角的背包(取走箱子、放进奖励都在这里)。</param>
        /// <param name="cooldowns">冷却表(与药水共用;成功时给这件物品起冷却)。</param>
        /// <param name="box">要开的箱子或礼包。</param>
        /// <param name="job">主角的职业下标(<see cref="EquipRules"/>)。</param>
        /// <param name="level">主角的等级。</param>
        /// <param name="place">现在在哪(城镇或副本,单个值)。</param>
        /// <param name="give">放奖励的办法,通常是 <c>HeroProgress.AddItem</c>(顺带记拾取累计、药水进快捷栏);必须能把试过放得下的全部放下。</param>
        public OpenResult Open(Inventory bag, ItemCooldowns cooldowns, ItemInfo box, int job, int level, ItemPlaces place, Action<ItemInfo, int> give = null)
        {
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            if (cooldowns == null)
            {
                throw new ArgumentNullException(nameof(cooldowns));
            }
            if (!ItemUseTable.IsOpening(box))
            {
                return new OpenResult(UseResult.NotConsumable);
            }
            if (!ItemUseTable.AllowedAt(box, place))
            {
                return new OpenResult(UseResult.Unavailable);
            }
            UseResult user = ConsumableRules.CheckUser(box, job, level);
            if (user != UseResult.Ok)
            {
                return new OpenResult(user);
            }
            if (bag.CountOf(box.Code) < 1)
            {
                return new OpenResult(UseResult.NotInBag);
            }
            if (cooldowns.IsCooling(box))
            {
                return new OpenResult(UseResult.Cooling);
            }
            List<PackageEntry> plan = Draw(box, _roll);
            if (plan == null)
            {
                return new OpenResult(UseResult.NotYet);
            }
            if (plan.Count == 0)
            {
                return new OpenResult(UseResult.NoEffect);
            }
            var grants = new List<OpenGrant>(plan.Count);
            var give2 = new List<KeyValuePair<ItemInfo, int>>(plan.Count);
            foreach (PackageEntry entry in plan)
            {
                ItemInfo info = _lookup(entry.Code);
                if (info == null)
                {
                    return new OpenResult(UseResult.NotYet);
                }
                grants.Add(new OpenGrant(info, entry.Count));
                give2.Add(new KeyValuePair<ItemInfo, int>(info, entry.Count));
            }
            var take = new List<KeyValuePair<int, int>> { new KeyValuePair<int, int>(box.Code, 1) };
            if (!bag.CanSwap(take, give2))
            {
                return new OpenResult(UseResult.BagFull);
            }
            bag.Remove(box.Code, 1);
            cooldowns.Start(box);
            if (give == null)
            {
                give = (info, count) => bag.Add(info, count);
            }
            foreach (OpenGrant grant in grants)
            {
                give(grant.Info, grant.Count);
            }
            return new OpenResult(UseResult.Ok, grants);
        }
    }
}
