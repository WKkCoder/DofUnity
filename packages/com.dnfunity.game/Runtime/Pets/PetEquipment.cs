using System;
using System.Collections.Generic;

// 服务端静态读码:部位号 22 宠物、23/24/25 神器(IsArtifactEquipSlot 0x083393fc 只认 0x17..0x19;IsCreatureEquipmentScope 0x0833926c);
// 神器归用户、不归宠物(GetArtifact 0x0833be56),放入要宠物脚本开放该颜色、没有宠物时只能拿下(DenySwapArtifact 0x0833989c),
// 神器只能是不可叠加的物品(CArtifact::EquipArtifact 0x08336f6e);经验加成 CCreature::GetAddExp 0x08337724。
namespace Dnf.Game
{
    /// <summary>宠物格穿脱检查与动作的结果。</summary>
    public enum PetEquipResult
    {
        /// <summary>成功。</summary>
        Ok = 0,

        /// <summary>没有这件物品(空物品、背包格越界、没有这个实例编号、神器格是空的)。</summary>
        NoItem = 1,

        /// <summary>不是装备(可叠加物品)。</summary>
        NotEquipment = 2,

        /// <summary>物品的部位与要放的格子对不上(普通装备放进宠物格、宠物放进神器格、红神器放进蓝格……)。</summary>
        WrongSlot = 3,

        /// <summary>部位号不是神器格(23 到 25)。</summary>
        NotArtifactSlot = 4,

        /// <summary>现在没有装备宠物,神器格只能拿下、不能放入。</summary>
        NoPetEquipped = 5,

        /// <summary>装备着的宠物的脚本没有开放这个颜色的神器格。</summary>
        SlotUnavailable = 6,

        /// <summary>这只宠物已经装备着。</summary>
        AlreadyEquipped = 7,

        /// <summary>背包装备页放不下被换下或脱下的物品。</summary>
        BagFull = 8,
    }

    /// <summary>一件物品穿戴时该交给哪套格子(见 <see cref="PetEquipRules.Route"/>)。</summary>
    public enum WearTarget
    {
        /// <summary>不是能穿的东西(空、可叠加、部位号不在 0 到 25)。</summary>
        None = 0,

        /// <summary>普通装备栏与时装栏那一套(0 到 21 号部位;10 到 21 号是装备栏,时装 0 到 9 号另有它的栏)。</summary>
        Regular = 1,

        /// <summary>宠物格(22 号部位):穿的是宠物实例,不是物品本身。</summary>
        PetSlot = 2,

        /// <summary>神器格(23 到 25 号部位)。</summary>
        ArtifactSlot = 3,
    }

    /// <summary>
    /// 宠物格:22 号放一只宠物实例,23 到 25 号放神器(三格各认一种颜色)。与普通装备栏 <see cref="EquipmentSlots"/>(10 到 21 号)
    /// 与时装栏(0 到 9 号)是互不相干的三套格子:这里的任何操作都不碰它们,反过来也一样。
    /// <see cref="Version"/> 每次有变化加一。
    /// </summary>
    public sealed class PetEquipment
    {
        /// <summary>宠物格的部位号。</summary>
        public const int PetPart = EquipmentParts.Creature;

        /// <summary>第一个神器格的部位号(红)。</summary>
        public const int FirstArtifact = EquipmentParts.ArtifactRed;

        /// <summary>最后一个神器格的部位号(绿)。</summary>
        public const int LastArtifact = EquipmentParts.ArtifactGreen;

        /// <summary>神器格个数。</summary>
        public const int ArtifactCount = LastArtifact - FirstArtifact + 1;

        private readonly ItemInfo[] _artifacts = new ItemInfo[ArtifactCount];

        /// <summary>现在装备着的宠物;没有是 null。</summary>
        public PetInstance Pet { get; private set; }

        /// <summary>变化次数(换宠物、放入或拿下神器各算一次)。</summary>
        public int Version { get; private set; }

        /// <summary>这个部位号是宠物格或神器格。</summary>
        public static bool IsSlot(int part) => part >= PetPart && part <= LastArtifact;

        /// <summary>这个部位号是神器格。</summary>
        public static bool IsArtifactSlot(int part) => part >= FirstArtifact && part <= LastArtifact;

        /// <summary>某个神器格上放的东西;空格或部位号不是神器格返回 null。</summary>
        public ItemInfo Artifact(int part)
        {
            return IsArtifactSlot(part) ? _artifacts[part - FirstArtifact] : null;
        }

        /// <summary>放了几件神器。</summary>
        public int ArtifactCountEquipped
        {
            get
            {
                int n = 0;
                foreach (ItemInfo item in _artifacts)
                {
                    if (item != null)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>直接换宠物(不检查规则、不改宠物自己的状态;<see cref="PetEquipActions"/> 与存档还原才调用)。内容没变时什么也不做。</summary>
        internal void SetPet(PetInstance pet)
        {
            if (ReferenceEquals(Pet, pet))
            {
                return;
            }
            Pet = pet;
            Version++;
        }

        /// <summary>直接放神器(不检查规则)。部位号不是神器格或内容没变时什么也不做。</summary>
        internal void SetArtifact(int part, ItemInfo item)
        {
            if (!IsArtifactSlot(part) || ReferenceEquals(_artifacts[part - FirstArtifact], item))
            {
                return;
            }
            _artifacts[part - FirstArtifact] = item;
            Version++;
        }

        /// <summary>
        /// 存档还原用:直接把一只宠物放进宠物格(不检查规则、不改宠物自己的状态,调用方先让它 <see cref="PetInstance.Equip"/>)。宠物为空返回 false。
        /// </summary>
        public bool RestorePet(PetInstance pet)
        {
            if (pet == null)
            {
                return false;
            }
            SetPet(pet);
            return true;
        }

        /// <summary>存档还原用:直接把神器放进神器格(不检查规则,规则由调用的一方先查)。部位号不是神器格或物品为空返回 false。</summary>
        public bool RestoreArtifact(int part, ItemInfo item)
        {
            if (!IsArtifactSlot(part) || item == null)
            {
                return false;
            }
            SetArtifact(part, item);
            return true;
        }

        /// <summary>存档还原用:清空全部(有东西才算一次变化)。宠物自己的状态不动。</summary>
        public void Clear()
        {
            bool any = Pet != null;
            Pet = null;
            for (int i = 0; i < _artifacts.Length; i++)
            {
                if (_artifacts[i] != null)
                {
                    _artifacts[i] = null;
                    any = true;
                }
            }
            if (any)
            {
                Version++;
            }
        }

        /// <summary>
        /// 三个神器格的经验加成(百分数)之和(服务端 <c>CalculateExp</c> 里的 <c>GetAddExp</c> × 3):没有装备宠物时是 0;
        /// 每格:宠物脚本开放这个颜色、格里有神器,并且神器的 <c>[creature minimum level]</c> 是 0 或不大于宠物等级,才加这件神器的
        /// <c>[creature experience amount rate]</c>,否则这一格加 0。每一步累加都落成 float(服务端每步存回 float)。
        /// </summary>
        /// <param name="itemData">按物品取宠物字段(<see cref="PetCatalog.ItemData"/>);返回 null 的物品按"没有加成"算。</param>
        public float ExperienceBonus(Func<ItemInfo, PetItemData> itemData)
        {
            float sum = 0f;
            for (int part = FirstArtifact; part <= LastArtifact; part++)
            {
                sum = sum + SlotBonus(part, itemData);
            }
            return sum;
        }

        private float SlotBonus(int part, Func<ItemInfo, PetItemData> itemData)
        {
            PetInstance pet = Pet;
            if (pet == null || !pet.Def.AllowsArtifactPart(part))
            {
                return 0f;
            }
            ItemInfo item = Artifact(part);
            PetItemData data = item == null || itemData == null ? null : itemData(item);
            if (data == null)
            {
                return 0f;
            }
            return data.MinimumLevel == 0 || data.MinimumLevel <= pet.Level ? data.ExperienceRate : 0f;
        }
    }

    /// <summary>宠物格的规则判断(只判断,不动任何东西)。</summary>
    public static class PetEquipRules
    {
        /// <summary>
        /// 一件物品穿戴时该交给哪套格子:装备且部位是 22 → 宠物格,23 到 25 → 神器格,0 到 21 → 普通装备栏(时装 0 到 9 另有它的栏,仍不归宠物格);
        /// 空、可叠加、部位号不在 0 到 25(数据里认不出的装备类型是 26)→ 无。
        /// 宠物类物品(22 到 25)<b>永远不进普通装备栏</b>;服务端的总判断 <c>IsEquipable</c> 对宠物类物品不比对槽号,
        /// 靠上一层按类别字节分派来保证这一点,这里把这层分派做成一个函数。
        /// </summary>
        public static WearTarget Route(ItemInfo item)
        {
            if (item == null || !item.IsEquipment || item.Part < 0 || item.Part > PetEquipment.LastArtifact)
            {
                return WearTarget.None;
            }
            if (item.Part == PetEquipment.PetPart)
            {
                return WearTarget.PetSlot;
            }
            if (PetEquipment.IsArtifactSlot(item.Part))
            {
                return WearTarget.ArtifactSlot;
            }
            return WearTarget.Regular;
        }

        /// <summary>
        /// 这件物品结构上能不能放进 <paramref name="slot"/> 号神器格(不看宠物):是装备(服务端要求不可叠加)、部位号等于槽号。
        /// 存档还原用它(服务端读档时直接放,不查宠物)。服务端不比对神器颜色与槽号,这里按部位号一一对应(比服务端严,选择而非观察)。
        /// </summary>
        public static PetEquipResult CheckArtifactItem(ItemInfo item, int slot)
        {
            if (!PetEquipment.IsArtifactSlot(slot))
            {
                return PetEquipResult.NotArtifactSlot;
            }
            if (item == null)
            {
                return PetEquipResult.NoItem;
            }
            if (!item.IsEquipment)
            {
                return PetEquipResult.NotEquipment;
            }
            return item.Part == slot ? PetEquipResult.Ok : PetEquipResult.WrongSlot;
        }

        /// <summary>
        /// 把神器放进 <paramref name="slot"/> 号格这一步能不能做(服务端 <c>DenySwapArtifact</c>):在 <see cref="CheckArtifactItem"/> 之外,
        /// 必须有装备着的宠物,并且它的脚本开放这个颜色。服务端的第二处检查传错了槽号(笔误),这里按"物品落在哪格就查哪格"。
        /// </summary>
        public static PetEquipResult CheckArtifactMove(ItemInfo item, int slot, PetInstance equippedPet)
        {
            PetEquipResult basic = CheckArtifactItem(item, slot);
            if (basic != PetEquipResult.Ok)
            {
                return basic;
            }
            if (equippedPet == null)
            {
                return PetEquipResult.NoPetEquipped;
            }
            return equippedPet.Def.AllowsArtifactPart(slot) ? PetEquipResult.Ok : PetEquipResult.SlotUnavailable;
        }
    }

    /// <summary>宠物格上的动作:换宠物、放入与拿下神器(规则见 <see cref="PetEquipRules"/>)。</summary>
    public static class PetEquipActions
    {
        /// <summary>
        /// 装备名册里编号为 <paramref name="uid"/> 的宠物(服务端 <c>EquipCreatureItem</c>:先卸下旧的,再装新的):
        /// 旧宠物进未装备状态,新宠物按用户是否在战斗中进消耗中或回补状态(饿死的宠物在"回补"状态下复活,见 <see cref="PetInstance.SetState"/>)。
        /// 没有这个编号返回 <see cref="PetEquipResult.NoItem"/>;已经装备着返回 <see cref="PetEquipResult.AlreadyEquipped"/>、什么都不动。
        /// </summary>
        public static PetEquipResult EquipPet(PetRoster roster, PetEquipment equipment, int uid, int now, bool userFighting)
        {
            if (roster == null)
            {
                throw new ArgumentNullException(nameof(roster));
            }
            if (equipment == null)
            {
                throw new ArgumentNullException(nameof(equipment));
            }
            PetInstance pet = roster.Find(uid);
            if (pet == null)
            {
                return PetEquipResult.NoItem;
            }
            if (ReferenceEquals(equipment.Pet, pet))
            {
                return PetEquipResult.AlreadyEquipped;
            }
            if (equipment.Pet != null)
            {
                equipment.Pet.Dismantle(now);
            }
            pet.Equip(userFighting, now);
            equipment.SetPet(pet);
            return PetEquipResult.Ok;
        }

        /// <summary>卸下装备着的宠物(进未装备状态,宠物仍留在名册里;神器仍留在神器格——神器归用户);没有宠物返回 <see cref="PetEquipResult.NoItem"/>。</summary>
        public static PetEquipResult UnequipPet(PetEquipment equipment, int now)
        {
            if (equipment == null)
            {
                throw new ArgumentNullException(nameof(equipment));
            }
            PetInstance pet = equipment.Pet;
            if (pet == null)
            {
                return PetEquipResult.NoItem;
            }
            pet.Dismantle(now);
            equipment.SetPet(null);
            return PetEquipResult.Ok;
        }

        /// <summary>
        /// 把背包装备页第 <paramref name="bagIndex"/> 格的神器放进它自己部位号对应的神器格(规则见 <see cref="PetEquipRules.CheckArtifactMove"/>):
        /// 不通过原样返回结果、什么都不动;格里原来有神器就换——旧的放回背包里被放入的那一格(原位),没有就把那一格腾空。
        /// </summary>
        public static PetEquipResult EquipArtifact(Inventory bag, PetEquipment equipment, int bagIndex)
        {
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            if (equipment == null)
            {
                throw new ArgumentNullException(nameof(equipment));
            }
            IReadOnlyList<ItemStack> tab = bag.Tab(InventoryTab.Equipment);
            if ((uint)bagIndex >= (uint)tab.Count)
            {
                return PetEquipResult.NoItem;
            }
            ItemInfo item = tab[bagIndex].Info;
            int slot = item.Part;
            PetEquipResult result = PetEquipRules.CheckArtifactMove(item, slot, equipment.Pet);
            if (result != PetEquipResult.Ok)
            {
                return result;
            }
            ItemInfo old = equipment.Artifact(slot);
            if (old == null)
            {
                bag.TakeAt(InventoryTab.Equipment, bagIndex);
            }
            else
            {
                bag.ReplaceAt(InventoryTab.Equipment, bagIndex, old);
            }
            equipment.SetArtifact(slot, item);
            return PetEquipResult.Ok;
        }

        /// <summary>
        /// 把 <paramref name="slot"/> 号神器格上的神器拿下放回背包(没有宠物时也能拿,服务端 <c>DenySwapArtifact</c> 只拦放入);
        /// 格子是空的返回 <see cref="PetEquipResult.NoItem"/>,背包放不下返回 <see cref="PetEquipResult.BagFull"/>,都什么也不动。
        /// </summary>
        public static PetEquipResult UnequipArtifact(Inventory bag, PetEquipment equipment, int slot)
        {
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            if (equipment == null)
            {
                throw new ArgumentNullException(nameof(equipment));
            }
            if (!PetEquipment.IsArtifactSlot(slot))
            {
                return PetEquipResult.NotArtifactSlot;
            }
            ItemInfo item = equipment.Artifact(slot);
            if (item == null)
            {
                return PetEquipResult.NoItem;
            }
            if (!bag.CanAdd(item))
            {
                return PetEquipResult.BagFull;
            }
            equipment.SetArtifact(slot, null);
            bag.Add(item);
            return PetEquipResult.Ok;
        }
    }
}
