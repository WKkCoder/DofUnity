using System;
using System.Collections.Generic;

// 证据(r14-avatar,服务端静态读码;普通装备那一套见 EquipRules 的 E-118):
//   服务端搬东西的总函数 CInventory::move_item 0x08500688 里有四个"把一件物品放进身上栏"的分支,每个分支先过 CheckEquipable 0x085002e0
//   (两端物品的类别字节是 1 或 8 或空格、且不是两个空格),再调 IsEquipable 0x085003be(槽与物品装备类型一致、check_job_type、check_low_level):
//     普通背包 → 身上   调用点 0x085010b5,对象是要穿上的那件;
//     身上 ⇄ 普通背包   调用点 0x085008e7,对象是从背包换进身上的那件;
//     身上 ⇄ 时装背包   调用点 0x08500cc4,对象是从时装背包换进身上的那件;
//     时装背包 → 身上   调用点 0x085014a1,对象却是身上目标格里原有的那一件(汇编逐条核对过:取的是身上栏格 +0x1e 处的物品编号),
//                       要穿上的时装本身只过 CheckEquipable。
//   观察:前三支检查"进身上的那件",只有"穿时装"这一支查的是"出身上的那件"。推断:这一支是服务端的漏检,三支的意图一致;原版客户端有没有自己先查没有读。
//   这里按意图办:要穿上的时装与普通装备走同一条规则链(槽、职业、等级),不照搬漏检。
//   数据一侧:全库的时装类装备(部位 0 到 9)都写了 [usable job](灵气里写 [all] 的只占少数,其余写明职业),等级限制是 [minimum level](没写按 0,
//   皮肤类有大半是 0,其余部位几乎都大于 0);并且有剑魂动画块的时装全库里没有一件是剑魂不可用的,所以职业限制是数据里真实存在的限制。
namespace Dnf.Game
{
    /// <summary>
    /// 时装能不能穿的规则:与 <see cref="EquipRules"/> 同一条链,只是槽换成装扮栏的 0 到 9 号:槽对不对、职业、等级。
    /// 结果沿用 <see cref="EquipResult"/>(宿主的提示可以共用):<see cref="EquipResult.NotWearable"/> 在这里是"槽不在 0 到 9"(普通装备那边是"不在 10 到 21"),
    /// 所以武器等普通装备按自己的部位去穿装扮栏得到的是 <see cref="EquipResult.NotWearable"/>,硬塞进某个时装槽得到的是 <see cref="EquipResult.WrongSlot"/>。
    /// 与 <see cref="EquipRules.Check(ItemInfo, int, int, int)"/> 的一处不同:宠物类物品不享受"不看槽"的待遇(服务端的这个例外只对宠物栏有意义)。
    /// <b>没做</b>(同 <see cref="EquipRules"/>):付费特权的溢出等级、脚步控制率、需要技能、专业职业等级;时装独有的限期(<c>[usable period]</c>)、临时编号、
    /// 套装与属性加成都不在这一步。
    /// </summary>
    public static class AvatarRules
    {
        /// <summary>
        /// 职业 <paramref name="job"/>、等级 <paramref name="level"/> 的角色能不能把 <paramref name="item"/> 穿进装扮栏的 <paramref name="slot"/> 号槽。
        /// 次序:没有物品、不是装备、槽不在 0 到 9、物品的装备类型不等于槽、职业、等级(与 <see cref="EquipRules.Check(ItemInfo, int, int, int)"/> 同序)。
        /// </summary>
        public static EquipResult Check(ItemInfo item, int slot, int job, int level)
        {
            if (item == null)
            {
                return EquipResult.NoItem;
            }
            if (!item.IsEquipment)
            {
                return EquipResult.NotEquipment;
            }
            if (!AvatarSlots.IsSlot(slot))
            {
                return EquipResult.NotWearable;
            }
            if (item.Part != slot)
            {
                return EquipResult.WrongSlot;
            }
            if (!EquipRules.UsableBy(item, job))
            {
                return EquipResult.WrongJob;
            }
            if (item.MinimumLevel > level)
            {
                return EquipResult.LevelTooLow;
            }
            return EquipResult.Ok;
        }

        /// <summary>这件物品按自己的装备类型放进它那个槽,能不能穿;不是时装(部位不在 0 到 9)返回 <see cref="EquipResult.NotWearable"/>。</summary>
        public static EquipResult Check(ItemInfo item, int job, int level)
        {
            return item == null ? EquipResult.NoItem : Check(item, item.Part, job, level);
        }
    }

    /// <summary>
    /// 穿与脱:在背包的装备页与装扮栏之间搬时装(规则见 <see cref="AvatarRules"/>)。语义与 <see cref="EquipActions"/> 一致:
    /// 换穿时换下来的那件放回被穿上的那一格(服务端 <c>move_item</c> 的交换)。装扮栏与 <see cref="EquipmentSlots"/> 互不相干:
    /// 这里不碰普通装备栏,<see cref="EquipActions"/> 也碰不到装扮栏(它对时装返回 <see cref="EquipResult.NotWearable"/>)。
    /// </summary>
    public static class AvatarActions
    {
        /// <summary>
        /// 把装备页第 <paramref name="bagIndex"/> 格的时装穿上:规则不通过原样返回结果、什么都不动;
        /// 槽里原来有时装就换:原来的那件放回背包里被穿上的那一格(原位),没有就把那一格腾空。
        /// <paramref name="canShow"/> 不为空时再问一次"画面上穿不穿得出来"(例如这个职业有没有这件时装的图层),不通过返回 <see cref="EquipResult.NotShowable"/>。
        /// </summary>
        public static EquipResult Wear(Inventory bag, AvatarSlots slots, int bagIndex, int job, int level, Func<ItemInfo, bool> canShow = null)
        {
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            if (slots == null)
            {
                throw new ArgumentNullException(nameof(slots));
            }
            IReadOnlyList<ItemStack> tab = bag.Tab(InventoryTab.Equipment);
            if ((uint)bagIndex >= (uint)tab.Count)
            {
                return EquipResult.NoItem;
            }
            ItemInfo item = tab[bagIndex].Info;
            EquipResult result = AvatarRules.Check(item, job, level);
            if (result != EquipResult.Ok)
            {
                return result;
            }
            if (canShow != null && !canShow(item))
            {
                return EquipResult.NotShowable;
            }
            ItemInfo old = slots.Get(item.Part);
            if (old == null)
            {
                bag.TakeAt(InventoryTab.Equipment, bagIndex);
            }
            else
            {
                bag.ReplaceAt(InventoryTab.Equipment, bagIndex, old);
            }
            slots.Set(item.Part, item);
            return EquipResult.Ok;
        }

        /// <summary>
        /// 把 <paramref name="part"/> 号槽上的时装脱下放回背包;槽是空的、或背包装备页没有空格(<see cref="Inventory.CanAdd"/>)返回 false、什么都不动。
        /// </summary>
        public static bool TakeOff(Inventory bag, AvatarSlots slots, int part)
        {
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            if (slots == null)
            {
                throw new ArgumentNullException(nameof(slots));
            }
            ItemInfo item = slots.Get(part);
            if (item == null || !bag.CanAdd(item))
            {
                return false;
            }
            slots.Set(part, null);
            bag.Add(item);
            return true;
        }
    }
}
