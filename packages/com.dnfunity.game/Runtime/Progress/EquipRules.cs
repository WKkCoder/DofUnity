using System;
using System.Collections.Generic;

// 证据:E-118(服务端静态读码:IsEquipable 0x085003be 是"这个角色能不能把这件物品穿进这个槽"的总判断,里面依次是
// 槽与物品装备类型是否一致(宠物类除外)、CItem::check_job_type 0x0850d728、CItem::check_low_level 0x084e95bc;
// 职业标志位的写法在 ImportEquipmentScript 0x08991aeb 读 [usable job] 的那一段,职业名数组是 CHARACTER_JOB_TAG_NAMES @0x093f4d40,
// getCharacterJob 0x088be57e 把名字换成下标,认不出来的名字(例如 [all])返回 11;默认值来自 STItemScript::Clear 0x0898d718)。
namespace Dnf.Game
{
    /// <summary>穿戴检查的结果(<see cref="EquipRules.Check"/>、<see cref="EquipActions.Equip"/>)。</summary>
    public enum EquipResult
    {
        /// <summary>可以穿。</summary>
        Ok = 0,

        /// <summary>没有这件物品(背包下标越界、空槽)。</summary>
        NoItem = 1,

        /// <summary>不是装备(可叠加物品)。</summary>
        NotEquipment = 2,

        /// <summary>这一类装备没有装备栏格子(时装、宠物类;这一步只做 10 到 21 号部位)。</summary>
        NotWearable = 3,

        /// <summary>物品的装备类型与要放进去的槽不一致。</summary>
        WrongSlot = 4,

        /// <summary>主角的职业穿不了。</summary>
        WrongJob = 5,

        /// <summary>主角的等级不够。</summary>
        LevelTooLow = 6,

        /// <summary>规则允许,但画面上穿不出来(武器没有这个职业的动画图层);只在宿主给了检查函数时出现。</summary>
        NotShowable = 7,
    }

    /// <summary>
    /// 能不能穿的规则。服务端的总判断 <c>IsEquipable</c>(0x085003be)里与这一步有关的三条:
    /// ① 物品的装备类型必须等于要放进的槽(宠物类物品不看槽);② <c>check_job_type</c>:物品的"可用职业"标志里这个职业那一位是 1;
    /// ③ <c>check_low_level</c>:物品的最低等级不大于角色等级(服务端还加一个"溢出等级"——付费特权或服务器的最高装备等级,
    /// 普通角色是 0,这里没做)。
    /// <para>
    /// 可用职业标志的写法(<c>ImportEquipmentScript</c> 读 <c>[usable job]</c>):每个字符串交给 <c>getCharacterJob</c>——
    /// 与 11 个职业名之一相同就置那一位,<b>不是职业名的字符串(例如 [all])置全部 11 位</b>;标志位初始全 0,
    /// 所以一件没写 <c>[usable job]</c> 的装备没有任何职业能穿。
    /// </para>
    /// <b>没做</b>:服务端的"脚步控制率上限"(防滥用)、需要某个技能才能穿(<c>get_need_skill</c>)、专业职业等级(<c>isExpertJobUsable</c>)、
    /// 绑定类型 5 的最高可用等级(<c>GetUsableMaxLevel</c>)。
    /// </summary>
    public static class EquipRules
    {
        /// <summary>职业数(服务端职业标志位数组的长度)。</summary>
        public const int JobCount = 11;

        /// <summary><see cref="JobOf"/> 认不出来的名字(服务端 <c>getCharacterJob</c> 返回 11,按"所有职业"处理)。</summary>
        public const int AllJobs = 11;

        /// <summary>剑魂(默认主角,0 号角色)的职业下标。</summary>
        public const int SwordmanJob = 0;

        private static readonly string[] JobTags =
        {
            "[swordman]", "[fighter]", "[gunner]", "[mage]", "[priest]", "[at gunner]", "[thief]", "[at fighter]", "[at mage]",
            "[demonic swordman]", "[creator mage]",
        };

        /// <summary>职业下标对应的名字;越界返回 null。</summary>
        public static string JobTag(int job)
        {
            return (uint)job < (uint)JobTags.Length ? JobTags[job] : null;
        }

        /// <summary>职业名(<c>[usable job]</c> 里的字符串)对应的下标;不是 11 个职业名之一返回 <see cref="AllJobs"/>。</summary>
        public static int JobOf(string tag)
        {
            if (tag != null)
            {
                for (int i = 0; i < JobTags.Length; i++)
                {
                    if (string.Equals(JobTags[i], tag, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }
            return AllJobs;
        }

        /// <summary>这件装备的可用职业标志里,职业 <paramref name="job"/> 那一位是不是 1(没写 <c>[usable job]</c> 就是谁也穿不了)。</summary>
        public static bool UsableBy(ItemInfo item, int job)
        {
            if (item == null || (uint)job >= JobCount)
            {
                return false;
            }
            foreach (string tag in item.UsableJobs)
            {
                int j = JobOf(tag);
                if (j == AllJobs || j == job)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 职业 <paramref name="job"/>、等级 <paramref name="level"/> 的角色能不能把 <paramref name="item"/> 穿进 <paramref name="slot"/> 号部位。
        /// 按服务端 <c>IsEquipable</c> 的次序检查:槽对不对(宠物类不看)、职业、等级。<see cref="EquipResult.NotWearable"/> 是这一步自己加的:
        /// 只有 <see cref="EquipmentSlots"/> 里的 12 个部位有格子。
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
            if (!EquipmentSlots.IsSlot(slot))
            {
                return EquipResult.NotWearable;
            }
            if (!item.IsCreature && item.Part != slot)
            {
                return EquipResult.WrongSlot;
            }
            if (!UsableBy(item, job))
            {
                return EquipResult.WrongJob;
            }
            if (item.MinimumLevel > level)
            {
                return EquipResult.LevelTooLow;
            }
            return EquipResult.Ok;
        }

        /// <summary>这件物品按自己的装备类型放进它那个槽,能不能穿(槽取自物品自己的类型,所以不会是 <see cref="EquipResult.WrongSlot"/>)。</summary>
        public static EquipResult Check(ItemInfo item, int job, int level)
        {
            return item == null ? EquipResult.NoItem : Check(item, item.Part, job, level);
        }
    }

    /// <summary>
    /// 装备栏:12 个格子,下标是装备部位 10 到 21(<see cref="EquipmentParts.Weapon"/> 到 <see cref="EquipmentParts.MagicStone"/>,
    /// 服务端把这一段叫"可强化"部位,<c>IsEnchantalbe</c> 0x08514db0)。时装(0 到 9)与宠物类(22 到 25)这一步没有格子。
    /// <see cref="Version"/> 每次有变化加一,宿主与战斗会话据此重算攻击面板、换武器外观。
    /// </summary>
    public sealed class EquipmentSlots
    {
        /// <summary>第一个槽的部位号(武器)。</summary>
        public const int First = EquipmentParts.Weapon;

        /// <summary>最后一个槽的部位号(魔法石)。</summary>
        public const int Last = EquipmentParts.MagicStone;

        /// <summary>槽数。</summary>
        public const int Count = Last - First + 1;

        private readonly ItemInfo[] _items = new ItemInfo[Count];
        private IEquipOverlay _overlay;

        /// <summary>槽的变化次数(穿上、脱下、换穿各算一次;换附加来源与 <see cref="Refresh"/> 也算一次)。</summary>
        public int Version { get; private set; }

        /// <summary>
        /// 叠在基础属性之上的附加来源(强化、附魔、套装,见 <see cref="IEquipOverlay"/>);默认没有,汇总就只是各件装备的基础属性之和。
        /// 换了来源会让 <see cref="Version"/> 加一。
        /// </summary>
        public IEquipOverlay Overlay
        {
            get => _overlay;
            set
            {
                if (!ReferenceEquals(_overlay, value))
                {
                    _overlay = value;
                    Version++;
                }
            }
        }

        /// <summary>附加来源的依据变了(例如套装表重新载入)时让版本加一,宿主据此重算。</summary>
        public void Refresh()
        {
            Version++;
        }

        /// <summary>部位号有没有格子。</summary>
        public static bool IsSlot(int part) => part >= First && part <= Last;

        /// <summary>某个部位上穿的东西;空格或部位没有格子返回 null。</summary>
        public ItemInfo Get(int part)
        {
            return IsSlot(part) ? _items[part - First] : null;
        }

        /// <summary>穿着几件。</summary>
        public int EquippedCount
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

        /// <summary>穿着的全部装备(按部位号从小到大)。</summary>
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

        /// <summary>
        /// 穿着的全部装备的基础属性逐项相加(每次现算,所以脱下、换穿之后自然只剩还穿着的那几件)。
        /// 每件的属性是 <see cref="ItemInfo.Stats"/>(深度 0 的数值标签);汇总 = 各件之和,没有穿装备是 <see cref="EquipStats.Empty"/>。
        /// 装了 <see cref="Overlay"/> 时再加上它给的各项(强化、附魔、套装),没装就完全是原来的口径。
        /// </summary>
        public EquipStats TotalStats()
        {
            EquipStats total = EquipStats.Empty;
            foreach (ItemInfo item in _items)
            {
                if (item != null)
                {
                    total = total.Plus(item.Stats);
                }
            }
            if (_overlay != null)
            {
                foreach (EquipStats term in _overlay.Terms(this))
                {
                    total = total.Plus(term);
                }
            }
            return total;
        }

        /// <summary>
        /// 穿着的全部装备折成角色属性块后相加(<see cref="EquipStats.ToStatBlock"/> 按件折、再相加:
        /// 折算是整数运算,汇总恒等于各件分项之和,不受浮点取整的影响)。没有穿装备是全 0。
        /// 装了 <see cref="Overlay"/> 时,它给的每一项也像一件装备那样先折算再相加。
        /// </summary>
        public StatBlock TotalStatBlock()
        {
            StatBlock total = default;
            foreach (ItemInfo item in _items)
            {
                if (item != null)
                {
                    total = total + item.Stats.ToStatBlock();
                }
            }
            if (_overlay != null)
            {
                foreach (EquipStats term in _overlay.Terms(this))
                {
                    total = total + term.ToStatBlock();
                }
            }
            return total;
        }

        /// <summary>
        /// 把某个部位上穿着的东西直接拿掉(强化失败装备被销毁时用,不放回背包);返回被拿掉的那件,部位没有格子或格子是空的返回 null,
        /// 拿掉算一次变化(<see cref="Version"/> 加一)。
        /// </summary>
        public ItemInfo Take(int part)
        {
            if (!IsSlot(part) || _items[part - First] == null)
            {
                return null;
            }
            ItemInfo old = _items[part - First];
            _items[part - First] = null;
            Version++;
            return old;
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

        /// <summary>存档还原用:直接放(不检查规则,规则由调用的一方先查)。部位没有格子或物品为空时什么也不做并返回 false。</summary>
        public bool Restore(int part, ItemInfo item)
        {
            if (!IsSlot(part) || item == null)
            {
                return false;
            }
            Set(part, item);
            return true;
        }

        /// <summary>直接放(不检查规则;<see cref="EquipActions"/> 检查之后才调它)。部位没有格子或内容没变时什么也不做。</summary>
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

    /// <summary>穿与脱:在背包与装备栏之间搬东西(规则见 <see cref="EquipRules"/>)。</summary>
    public static class EquipActions
    {
        /// <summary>
        /// 把装备页第 <paramref name="bagIndex"/> 格的装备穿上:规则不通过原样返回结果、什么都不动;
        /// 槽里原来有东西就换:原来的那件放回背包里被穿上的那一格(原位),没有就把那一格腾空。
        /// <paramref name="canShow"/> 不为空时再问一次"画面上穿不穿得出来"(武器要有这个职业的动画图层),不通过返回 <see cref="EquipResult.NotShowable"/>。
        /// </summary>
        public static EquipResult Equip(Inventory bag, EquipmentSlots slots, int bagIndex, int job, int level, Func<ItemInfo, bool> canShow = null)
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
            EquipResult result = EquipRules.Check(item, job, level);
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
        /// 把 <paramref name="part"/> 号部位上的装备脱下放回背包;槽是空的、或背包装备页没有空格(<see cref="Inventory.CanAdd"/>)返回 false、什么都不动。
        /// </summary>
        public static bool Unequip(Inventory bag, EquipmentSlots slots, int part)
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
