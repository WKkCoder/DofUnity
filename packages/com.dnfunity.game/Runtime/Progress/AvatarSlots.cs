using System.Collections.Generic;

// 证据(r14-avatar,服务端静态读码,E-117 的部位编号为前提):
//   ① 身上栏的格号就是装备部位号:0 到 9 是时装、10 起是普通装备。CInventory::IsEquipAvatar 0x084fb638 只扫身上栏的 0 到 9 格看有没有穿时装;
//      CInventory::ChangeEquip 0x084fc37e 把"时装背包 → 身上"的目标格限制在 9 以内、"普通背包 → 身上"限制在 10 以上,越界返回错误码 0x15;
//   ② 服务端的时装有自己的背包(CheckValidSlot 0x084fb01e 的类型 2,格号上限 104),与普通背包分开;这里的背包仍是同一个 Inventory 的装备页
//      (布局是推断,见 Inventory 的说明),装扮栏与它之间的搬运语义按服务端 CInventory::move_item 0x08500688:被换下来的那件放回被穿上的那一格。
namespace Dnf.Game
{
    /// <summary>
    /// 装扮栏(时装栏):10 个格子,下标就是装备部位号 0 到 9(<see cref="EquipmentParts.HatAvatar"/> 到 <see cref="EquipmentParts.AuroraAvatar"/>:
    /// 帽子、头发、脸、上衣、裤子、鞋、胸饰、腰饰、皮肤、灵气)。与 <see cref="EquipmentSlots"/>(部位 10 到 21)是互不相干的两个栏,
    /// 各管各的 <see cref="Version"/>;穿脱规则见 <see cref="AvatarRules"/>、<see cref="AvatarActions"/>。
    /// 本类只是容器,不检查职业与等级(检查在动作里);为了让"换穿时把换下来的放回背包"永远成立,槽里只许放本部位的装备。
    /// </summary>
    public sealed class AvatarSlots
    {
        /// <summary>第一个槽的部位号(帽子)。</summary>
        public const int First = EquipmentParts.HatAvatar;

        /// <summary>最后一个槽的部位号(灵气)。</summary>
        public const int Last = EquipmentParts.AuroraAvatar;

        /// <summary>槽数。</summary>
        public const int Count = Last - First + 1;

        private readonly ItemInfo[] _items = new ItemInfo[Count];

        /// <summary>槽的变化次数(穿上、脱下、换穿各算一次);宿主据此重算外观。</summary>
        public int Version { get; private set; }

        /// <summary>部位号有没有格子。</summary>
        public static bool IsSlot(int part) => part >= First && part <= Last;

        /// <summary>某个部位上穿的时装;空格或部位没有格子返回 null。</summary>
        public ItemInfo Get(int part)
        {
            return IsSlot(part) ? _items[part - First] : null;
        }

        /// <summary>穿着几件。</summary>
        public int WornCount
        {
            get
            {
                int n = 0;
                foreach (ItemInfo item in _items)
                {
                    if (item != null)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>穿着的全部时装(按部位号从小到大)。</summary>
        public IEnumerable<ItemInfo> Items
        {
            get
            {
                foreach (ItemInfo item in _items)
                {
                    if (item != null)
                    {
                        yield return item;
                    }
                }
            }
        }

        /// <summary>存档还原用:清空全部格子(有东西才算一次变化)。</summary>
        public void Clear()
        {
            bool any = false;
            for (int i = 0; i < _items.Length; i++)
            {
                if (_items[i] != null)
                {
                    _items[i] = null;
                    any = true;
                }
            }
            if (any)
            {
                Version++;
            }
        }

        /// <summary>
        /// 存档还原用:直接放(不查职业与等级,规则由调用的一方先查)。部位没有格子、物品为空、不是装备、或物品的部位号与槽不一致时什么也不做并返回 false。
        /// </summary>
        public bool Restore(int part, ItemInfo item)
        {
            if (!IsSlot(part) || item == null || !item.IsEquipment || item.Part != part)
            {
                return false;
            }
            Set(part, item);
            return true;
        }

        /// <summary>直接放(不检查规则;<see cref="AvatarActions"/> 检查之后才调它)。部位没有格子或内容没变时什么也不做。</summary>
        internal void Set(int part, ItemInfo item)
        {
            if (!IsSlot(part) || ReferenceEquals(_items[part - First], item))
            {
                return;
            }
            _items[part - First] = item;
            Version++;
        }
    }
}
