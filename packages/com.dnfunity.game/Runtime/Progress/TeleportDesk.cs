using System;

// 证据:服务端读码(静态,没运行)。传送道具:Dispatcher_Teleport::check_error 0x081d056c 查用户状态在 3 到 4(城镇一类,不含副本)、格内物品是传送道具、
// 去向(城镇编号一个字节)至少是 1 且 CUser::CheckMoveTown 0x08678526 允许(PVP 频道与三个特定去向不检查;其余要求去向村庄存在、角色等级够、村庄要求的任务已通关)、队伍的传送状态允许,
// 然后走 CUser::use_stackable 0x0865e0ae 扣一件、再调 CAlchemist::OnTeleportCharacter 把角色传走(process 0x081d07bc)。去向由玩家在界面上选,城镇的地图与移动属于城镇线,这里不做。
namespace Dnf.Game
{
    /// <summary>一次传送请求:用了哪件传送道具。去向还没定,等城镇线选好后在 <see cref="TeleportDesk.Commit"/> 里给。</summary>
    public sealed class TeleportRequest
    {
        public TeleportRequest(ItemInfo item)
        {
            Item = item ?? throw new ArgumentNullException(nameof(item));
        }

        public ItemInfo Item { get; }
    }

    /// <summary>
    /// 传送道具的桩(<see cref="ItemUseClass.Teleport"/>):使用只被<b>记下来</b>,去向待城镇线接上;记下来时不扣物品、不起冷却(东西没有真的用掉)。
    /// 城镇线选好去向、把角色传走之后调 <see cref="Commit"/>(扣一件、起冷却、清掉请求),放弃就调 <see cref="Cancel"/>。
    /// 开启类共用的检查(地点、等级、职业、背包、冷却)与药水、开箱是同一套;服务端传送也是先查冷却再 <c>use_stackable</c> 扣一件。
    /// </summary>
    public sealed class TeleportDesk
    {
        /// <summary>等着城镇线处理的请求;没有为 null。再记一次会换掉它。</summary>
        public TeleportRequest Pending { get; private set; }

        /// <summary>累计记下了几次请求。</summary>
        public int Recorded { get; private set; }

        /// <summary>累计提交了几次(真的扣了物品)。</summary>
        public int Committed { get; private set; }

        /// <summary>
        /// 记一次传送请求。检查次序:是传送类(<see cref="UseResult.NotConsumable"/>)→ 这里能用(<see cref="UseResult.Unavailable"/>,只在城镇)→
        /// 等级、职业 → 背包里有 → 不在冷却;全过了返回 <see cref="UseResult.Pending"/>,其余原样返回、什么也不记。背包与冷却都不动。
        /// </summary>
        public UseResult Request(Inventory bag, ItemCooldowns cooldowns, ItemInfo item, int job, int level, ItemPlaces place)
        {
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            if (cooldowns == null)
            {
                throw new ArgumentNullException(nameof(cooldowns));
            }
            if (ItemUseTable.ClassOf(item) != ItemUseClass.Teleport)
            {
                return UseResult.NotConsumable;
            }
            if (!ItemUseTable.AllowedAt(item, place))
            {
                return UseResult.Unavailable;
            }
            UseResult user = ConsumableRules.CheckUser(item, job, level);
            if (user != UseResult.Ok)
            {
                return user;
            }
            if (bag.CountOf(item.Code) < 1)
            {
                return UseResult.NotInBag;
            }
            if (cooldowns.IsCooling(item))
            {
                return UseResult.Cooling;
            }
            Pending = new TeleportRequest(item);
            Recorded++;
            return UseResult.Pending;
        }

        /// <summary>
        /// 城镇线把角色传走之后调:扣一件、起冷却、清掉请求。没有等着的请求、去向不是正数(服务端要求至少 1)、或背包里已经没有这件(请求作废)返回 false。
        /// 去向去的是哪座城镇、行不行得通由城镇线自己查(服务端是 <c>CheckMoveTown</c>)。
        /// </summary>
        public bool Commit(Inventory bag, ItemCooldowns cooldowns, int destination)
        {
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            if (cooldowns == null)
            {
                throw new ArgumentNullException(nameof(cooldowns));
            }
            if (Pending == null || destination < 1)
            {
                return false;
            }
            ItemInfo item = Pending.Item;
            if (!bag.Remove(item.Code, 1))
            {
                Pending = null;
                return false;
            }
            cooldowns.Start(item);
            Pending = null;
            Committed++;
            return true;
        }

        /// <summary>放弃等着的请求(什么也没扣)。</summary>
        public void Cancel()
        {
            Pending = null;
        }
    }
}
