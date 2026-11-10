using System;
using System.Collections.Generic;

// 证据(r14-avatar):服务端时装有独立的背包(CheckValidSlot 0x084fb01e 类型 2),与普通背包的物品类型分开;"是不是时装"的判断是装备类型 0 到 9
// (IsAvatarItem 0x08514d46,E-117)。客户端的时装背包界面没有读到,下面的分类(按部位分十类、每类的穿戴状态)是本项目自己的数据模型,不是原版界面的复刻。
namespace Dnf.Game
{
    /// <summary>
    /// 装备页里的一件时装:在装备页的第几格(<see cref="BagIndex"/>,直接交给 <see cref="AvatarActions.Wear"/>)、物品、
    /// 按给定的职业与等级现在穿得上穿不上(<see cref="Result"/>,同 <see cref="AvatarRules.Check(ItemInfo, int, int)"/>)。
    /// </summary>
    public readonly struct AvatarBagEntry
    {
        public AvatarBagEntry(int bagIndex, ItemInfo item, EquipResult result)
        {
            BagIndex = bagIndex;
            Item = item;
            Result = result;
        }

        /// <summary>在 <see cref="InventoryTab.Equipment"/> 页里的格下标。</summary>
        public int BagIndex { get; }

        public ItemInfo Item { get; }

        /// <summary>时装部位(装备类型 0 到 9)。</summary>
        public int Part => Item.Part;

        /// <summary>规则检查的结果;<see cref="EquipResult.Ok"/> 才穿得上。</summary>
        public EquipResult Result { get; }

        public bool CanWear => Result == EquipResult.Ok;
    }

    /// <summary>时装背包的一类:一个部位的"身上穿着的那件"与"装备页里同部位的全部时装"。</summary>
    public sealed class AvatarCategory
    {
        internal AvatarCategory(int part, ItemInfo worn, IReadOnlyList<AvatarBagEntry> entries)
        {
            Part = part;
            Worn = worn;
            Entries = entries;
        }

        /// <summary>部位号 0 到 9。</summary>
        public int Part { get; }

        /// <summary>部位的标签(例如 [coat avatar],见 <see cref="EquipmentParts.TagOf"/>)。</summary>
        public string Tag => EquipmentParts.TagOf(Part);

        /// <summary>装扮栏里这个部位穿着的那件;没穿为 null。</summary>
        public ItemInfo Worn { get; }

        /// <summary>装备页里这个部位的时装(按格的先后)。</summary>
        public IReadOnlyList<AvatarBagEntry> Entries { get; }

        public int Count => Entries.Count;
    }

    /// <summary>
    /// 背包里时装类物品的识别与单独视图(窗口数据,不含美术):把装备页(<see cref="InventoryTab.Equipment"/>)里的格子分成时装与普通装备两堆,
    /// 时装再按部位 0 到 9 分成十类。它是建立时的一份快照:背包或装扮栏变了要重新 <see cref="Build"/>。
    /// 时装物品的识别就是 <see cref="ItemInfo.IsAvatar"/>(装备类型 0 到 9);不改 <see cref="Inventory"/> 的存放方式(时装与普通装备仍同在装备页),只出视图。
    /// </summary>
    public sealed class AvatarBagView
    {
        private readonly AvatarCategory[] _categories;

        private AvatarBagView(AvatarCategory[] categories, IReadOnlyList<AvatarBagEntry> avatars, IReadOnlyList<int> gearIndexes)
        {
            _categories = categories;
            Avatars = avatars;
            GearIndexes = gearIndexes;
        }

        /// <summary>装备页里的全部时装(按格的先后)。</summary>
        public IReadOnlyList<AvatarBagEntry> Avatars { get; }

        /// <summary>装备页里不是时装的装备(武器、防具、饰品、宠物类)所在的格下标(按格的先后)。</summary>
        public IReadOnlyList<int> GearIndexes { get; }

        /// <summary>十个类(下标就是部位号 0 到 9)。</summary>
        public IReadOnlyList<AvatarCategory> Categories => _categories;

        /// <summary>现在穿得上的时装件数。</summary>
        public int WearableCount
        {
            get
            {
                int n = 0;
                foreach (AvatarBagEntry entry in Avatars)
                {
                    if (entry.CanWear)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>某个部位的类;部位不在 0 到 9 返回 null。</summary>
        public AvatarCategory Category(int part)
        {
            return AvatarSlots.IsSlot(part) ? _categories[part - AvatarSlots.First] : null;
        }

        /// <summary>
        /// 为 <paramref name="bag"/> 的装备页建一份视图;<paramref name="worn"/> 是装扮栏(各类的"穿着"来自它),
        /// <paramref name="job"/> 与 <paramref name="level"/> 决定每件时装现在穿得上穿不上。
        /// </summary>
        public static AvatarBagView Build(Inventory bag, AvatarSlots worn, int job, int level)
        {
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            if (worn == null)
            {
                throw new ArgumentNullException(nameof(worn));
            }
            IReadOnlyList<ItemStack> tab = bag.Tab(InventoryTab.Equipment);
            var avatars = new List<AvatarBagEntry>();
            var gear = new List<int>();
            var byPart = new List<AvatarBagEntry>[AvatarSlots.Count];
            for (int i = 0; i < byPart.Length; i++)
            {
                byPart[i] = new List<AvatarBagEntry>();
            }
            for (int index = 0; index < tab.Count; index++)
            {
                ItemInfo item = tab[index].Info;
                if (item.IsAvatar)
                {
                    var entry = new AvatarBagEntry(index, item, AvatarRules.Check(item, job, level));
                    avatars.Add(entry);
                    byPart[item.Part - AvatarSlots.First].Add(entry);
                }
                else
                {
                    gear.Add(index);
                }
            }
            var categories = new AvatarCategory[AvatarSlots.Count];
            for (int i = 0; i < categories.Length; i++)
            {
                int part = AvatarSlots.First + i;
                categories[i] = new AvatarCategory(part, worn.Get(part), byPart[i]);
            }
            return new AvatarBagView(categories, avatars, gear);
        }
    }
}
