using System;
using System.Collections.Generic;

namespace Dnf.Game
{
    /// <summary>
    /// 叠在"穿着装备基础属性"之上的附加属性来源(强化、附魔、套装……)。<see cref="EquipmentSlots.TotalStats"/> 与
    /// <see cref="EquipmentSlots.TotalStatBlock"/> 在加完每件装备之后再加上它给的各项:每一项和每件装备一样,先各自折成角色属性再相加,
    /// 所以"汇总恒等于各分项之和"的口径不变,<see cref="EquipStats"/> 的折算本身一点没改。
    /// </summary>
    public interface IEquipOverlay
    {
        /// <summary>此刻附加的属性项(与槽的 <see cref="EquipmentSlots.Version"/> 对应,实现可以按版本缓存)。没有附加返回空表。</summary>
        IReadOnlyList<EquipStats> Terms(EquipmentSlots slots);
    }

    /// <summary>
    /// 一件穿着的装备凭自己的个体状态(强化等级、品级值……)带来的额外属性。
    /// 强化等级与品级值换算成角色属性,服务端不负责(它只存数);换算在客户端生成装备属性集的函数里,那是读得到的客户端代码(不是受保护区):
    /// 默认实现是 <see cref="EquipInstanceBonus"/>(强化表按部位分路、品级值按 etc 里的浮动数据浮动;依据与没查明的部分见 <see cref="EquipUpgradeEffect"/>、
    /// <see cref="EquipGradeVariation"/> 的文件头),宿主也可以换成自己的。
    /// </summary>
    public interface IEquipInstanceBonus
    {
        /// <summary>这件穿着的装备的额外属性;没有返回 null 或空集。</summary>
        EquipStats BonusOf(ItemInfo worn);
    }

    /// <summary>
    /// 默认的附加来源:附魔(穿着装备上附的卡,取卡的 <c>[enchant]</c> 块)、套装(见 <see cref="EquipSets"/>)与可选的个体状态加成。
    /// 按槽的版本缓存;依据变了(换了加成来源、套装表重新载入)调用 <see cref="Invalidate"/>。
    /// </summary>
    public sealed class EquipBonusOverlay : IEquipOverlay
    {
        private readonly Func<int, ItemInfo> _lookup;
        private IEquipInstanceBonus _instanceBonus;
        private bool _enchantEnabled = true;
        private bool _setsEnabled = true;
        private EquipmentSlots _attached;
        private EquipmentSlots _cacheSlots;
        private int _cacheVersion = -1;
        private IReadOnlyList<EquipStats> _cache;

        /// <param name="lookup">按物品编号取定义(附魔卡、套装主件);取不到的编号当作没有数据。</param>
        public EquipBonusOverlay(Func<int, ItemInfo> lookup)
        {
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        }

        /// <summary>个体状态加成的来源(强化、品级);不设为没有。</summary>
        public IEquipInstanceBonus InstanceBonus
        {
            get => _instanceBonus;
            set
            {
                _instanceBonus = value;
                Invalidate();
            }
        }

        /// <summary>附魔加成是否计入(默认计入)。</summary>
        public bool EnchantEnabled
        {
            get => _enchantEnabled;
            set
            {
                _enchantEnabled = value;
                Invalidate();
            }
        }

        /// <summary>套装加成是否计入(默认计入)。</summary>
        public bool SetsEnabled
        {
            get => _setsEnabled;
            set
            {
                _setsEnabled = value;
                Invalidate();
            }
        }

        /// <summary>装到一个装备栏上:此后该栏的汇总里带上这里的各项。同一个对象一次只装在一个栏上(再装到别的栏上,原来的会被摘下)。</summary>
        public void Attach(EquipmentSlots slots)
        {
            if (slots == null)
            {
                throw new ArgumentNullException(nameof(slots));
            }
            if (_attached != null && !ReferenceEquals(_attached, slots) && ReferenceEquals(_attached.Overlay, this))
            {
                _attached.Overlay = null;
            }
            _attached = slots;
            slots.Overlay = this;
        }

        /// <summary>丢掉缓存并让装着它的栏(<see cref="Attach"/>)升一次版本,宿主据此重算攻击面板。</summary>
        public void Invalidate()
        {
            _cache = null;
            _cacheSlots = null;
            _cacheVersion = -1;
            if (_attached != null && ReferenceEquals(_attached.Overlay, this))
            {
                _attached.Refresh();
            }
        }

        public IReadOnlyList<EquipStats> Terms(EquipmentSlots slots)
        {
            if (slots == null)
            {
                throw new ArgumentNullException(nameof(slots));
            }
            if (_cache != null && ReferenceEquals(_cacheSlots, slots) && _cacheVersion == slots.Version)
            {
                return _cache;
            }
            var terms = new List<EquipStats>();
            var worn = new List<ItemInfo>(slots.Items);
            foreach (ItemInfo item in worn)
            {
                if (_enchantEnabled)
                {
                    EquipStats enchant = EnchantOf(item);
                    if (enchant != null && !enchant.IsEmpty)
                    {
                        terms.Add(enchant);
                    }
                }
                if (_instanceBonus != null)
                {
                    EquipStats extra = _instanceBonus.BonusOf(item);
                    if (extra != null && !extra.IsEmpty)
                    {
                        terms.Add(extra);
                    }
                }
            }
            if (_setsEnabled)
            {
                terms.AddRange(EquipSets.Terms(worn, _lookup));
            }
            _cache = terms;
            _cacheSlots = slots;
            _cacheVersion = slots.Version;
            return terms;
        }

        /// <summary>一件穿着的装备身上附魔卡给的属性(没有附魔、卡取不到或卡没有 <c>[enchant]</c> 块返回 null)。</summary>
        public EquipStats EnchantOf(ItemInfo item)
        {
            EquipInstance state = item?.Instance;
            if (state == null || state.EnchantCode == 0)
            {
                return null;
            }
            return _lookup(state.EnchantCode)?.Enhance?.EnchantStats;
        }
    }
}
