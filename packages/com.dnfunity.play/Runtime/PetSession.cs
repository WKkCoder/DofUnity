using System;
using Dnf.Game;

// 本项目自己的设计:把宠物名册、宠物格、宠物目录和时钟拢成一个会话,给宿主一个入口(喂食、加经验、战斗开始与结束、装备与卸下、存档)。
// 规则本身都在 Dnf.Game 的 Pets 里(依据是服务端静态读码);这里不加规则,只做接线。
namespace Dnf.Play
{
    /// <summary>从存档还原宠物的结果统计。</summary>
    public sealed class PetRestoreReport
    {
        public PetRestoreReport(int pets, int petsDropped, bool petEquipped, int artifacts, int artifactsDropped)
        {
            Pets = pets;
            PetsDropped = petsDropped;
            PetEquipped = petEquipped;
            Artifacts = artifacts;
            ArtifactsDropped = artifactsDropped;
        }

        /// <summary>还原回名册的宠物数。</summary>
        public int Pets { get; }

        /// <summary>丢掉的宠物数:物品编号找不到、不是宠物物品、对应的宠物定义读不到、实例号重复,以及宠物格的行指向的实例号不在名册里或是重复的宠物格行(第二行起丢)。</summary>
        public int PetsDropped { get; }

        /// <summary>宠物格里的宠物是否装备回去了。</summary>
        public bool PetEquipped { get; }

        /// <summary>还原回神器格的神器数。</summary>
        public int Artifacts { get; }

        /// <summary>丢掉的神器数:物品编号找不到,或结构上放不进这一格(数据变了)。</summary>
        public int ArtifactsDropped { get; }
    }

    /// <summary>
    /// 宠物会话:名册(<see cref="Roster"/>)、宠物格(<see cref="Equipment"/>)与目录(<see cref="Catalog"/>),所有会碰饱食的操作都由宿主给当前秒数 <c>now</c>。
    /// 与角色的普通装备栏、时装栏互不相干:这里不读也不写它们,存档里用独立的 <c>pet</c> 与 <c>petslot</c> 两种行。
    /// <para>
    /// 接进宿主的写法:存档时 <c>var save = progress.Snapshot(); pets.Capture(save, now);</c>;读档时 <c>progress.Restore(save, lookup); pets.Restore(save, lookup, now, fighting);</c>。
    /// </para>
    /// </summary>
    public sealed class PetSession
    {
        public PetSession(PetCatalog catalog)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public PetCatalog Catalog { get; }

        public PetRoster Roster { get; } = new PetRoster();

        public PetEquipment Equipment { get; } = new PetEquipment();

        /// <summary>装备着的宠物;没有是 null。</summary>
        public PetInstance Equipped => Equipment.Pet;

        /// <summary>
        /// 按宠物物品新孵出一只宠物加进名册(1 级、经验 0、饱食 100、未装备),实例号取下一个空闲的号;
        /// 物品不是宠物物品、宠物定义或经验表读不到时返回 null、什么都不加。物品本身从背包里拿走是宿主的事。
        /// </summary>
        public PetInstance Adopt(ItemInfo petItem, int now)
        {
            PetDef def = Catalog.DefOf(petItem);
            PetExpTable table = Catalog.ExpTable;
            if (def == null || table == null)
            {
                return null;
            }
            int uid = Roster.NextUid();
            if (uid <= 0)
            {
                return null;
            }
            PetInstance pet = PetInstance.Hatch(uid, petItem.Code, def, table, now);
            return Roster.Add(pet) ? pet : null;
        }

        /// <summary>装备名册里的一只宠物(旧的先卸下);见 <see cref="PetEquipActions.EquipPet"/>。</summary>
        public PetEquipResult Equip(int uid, int now, bool userFighting)
        {
            return PetEquipActions.EquipPet(Roster, Equipment, uid, now, userFighting);
        }

        /// <summary>卸下装备着的宠物;见 <see cref="PetEquipActions.UnequipPet"/>。</summary>
        public PetEquipResult Unequip(int now)
        {
            return PetEquipActions.UnequipPet(Equipment, now);
        }

        /// <summary>把背包装备页的神器放进神器格;见 <see cref="PetEquipActions.EquipArtifact"/>。</summary>
        public PetEquipResult EquipArtifact(Inventory bag, int bagIndex)
        {
            return PetEquipActions.EquipArtifact(bag, Equipment, bagIndex);
        }

        /// <summary>把神器拿下放回背包;见 <see cref="PetEquipActions.UnequipArtifact"/>。</summary>
        public PetEquipResult UnequipArtifact(Inventory bag, int slot)
        {
            return PetEquipActions.UnequipArtifact(bag, Equipment, slot);
        }

        /// <summary>
        /// 把一只宠物从名册里放走(装备着的先卸下);没有这个实例号返回 false。
        /// 服务端读码里没有读到"放生"的规则,这是名册自己的收尾操作(宿主卖掉或丢弃宠物物品时用)。
        /// </summary>
        public bool Release(int uid, int now)
        {
            PetInstance pet = Roster.Find(uid);
            if (pet == null)
            {
                return false;
            }
            if (ReferenceEquals(Equipment.Pet, pet))
            {
                PetEquipActions.UnequipPet(Equipment, now);
            }
            return Roster.Remove(uid);
        }

        /// <summary>给装备着的宠物喂一次饲料(哪件消耗品、背包里扣不扣由宿主判断);没有宠物返回 <see cref="PetFeedResult.NoPet"/>。</summary>
        public PetFeedResult Feed(int now)
        {
            PetInstance pet = Equipment.Pet;
            return pet == null ? PetFeedResult.NoPet : pet.Feed(now);
        }

        /// <summary>
        /// 给装备着的宠物加经验:基础量 <paramref name="amount"/>,神器加成由神器格与目录里的物品字段算出(<see cref="PetEquipment.ExperienceBonus"/>)。
        /// 没有宠物返回 <see cref="PetGainOutcome.NoPet"/>。
        /// </summary>
        public PetGainResult GainExp(int amount, int now)
        {
            PetInstance pet = Equipment.Pet;
            if (pet == null)
            {
                return new PetGainResult(PetGainOutcome.NoPet, false, 0);
            }
            return pet.GainExp(amount, now, Equipment.ExperienceBonus(Catalog.ItemData));
        }

        /// <summary>取经验来源给的这一次基础经验量,再 <see cref="GainExp(int, int)"/>。</summary>
        public PetGainResult GainExp(IPetExpSource source, int now)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            return GainExp(source.PetExpAmount, now);
        }

        /// <summary>战斗开始或结束(装备着的宠物据此在消耗中与回补之间切换);没有宠物返回默认值。</summary>
        public PetStateChange SetFighting(bool fighting, int now)
        {
            PetInstance pet = Equipment.Pet;
            return pet == null ? default : pet.SetFighting(fighting, now);
        }

        /// <summary>
        /// 把名册与宠物格写进存档(先清掉 <paramref name="save"/> 里原有的宠物字段):每只宠物存实例号、物品编号、累计经验、整数饱食
        /// (此刻结算后向零取整);宠物格存装备着的宠物的实例号与每个神器格的物品编号。
        /// </summary>
        public void Capture(HeroSave save, int now)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            save.Pets.Clear();
            save.PetSlots.Clear();
            foreach (PetInstance pet in Roster.Pets)
            {
                save.Pets.Add(new SavedPet(pet.Uid, pet.ItemCode, pet.Exp, pet.Stomach(now)));
            }
            if (Equipment.Pet != null)
            {
                save.PetSlots.Add(new SavedPetSlot(PetEquipment.PetPart, Equipment.Pet.Uid));
            }
            for (int part = PetEquipment.FirstArtifact; part <= PetEquipment.LastArtifact; part++)
            {
                ItemInfo artifact = Equipment.Artifact(part);
                if (artifact != null)
                {
                    save.PetSlots.Add(new SavedPetSlot(part, artifact.Code));
                }
            }
        }

        /// <summary>
        /// 把存档里的宠物还原到这个会话上(整个替换名册与宠物格);<paramref name="lookup"/> 按物品编号取物品。
        /// 每只宠物照服务端读档的做法:经验与整数饱食照存、等级由经验重算、未装备、计时从 <paramref name="now"/> 起(离线时间不算);
        /// 宠物格里记的宠物随后走一遍装备(用户在战斗里进消耗中,否则进回补——饿死的宠物因此在读档时复活成饱食 1)。
        /// 神器只做结构检查(装备、部位号对得上)就放回去,不要求此刻有宠物(服务端读档就是直接放)。
        /// </summary>
        public PetRestoreReport Restore(HeroSave save, Func<int, ItemInfo> lookup, int now, bool userFighting)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            if (lookup == null)
            {
                throw new ArgumentNullException(nameof(lookup));
            }
            Equipment.Clear();
            Roster.Clear();
            int pets = 0;
            int petsDropped = 0;
            int artifacts = 0;
            int artifactsDropped = 0;
            PetExpTable table = Catalog.ExpTable;
            foreach (SavedPet saved in save.Pets)
            {
                PetDef def = table == null ? null : Catalog.DefOf(lookup(saved.ItemCode));
                if (def != null && Roster.Add(PetInstance.Load(saved.Uid, saved.ItemCode, def, table, saved.Exp, saved.Stomach, now)))
                {
                    pets++;
                }
                else
                {
                    petsDropped++;
                }
            }
            bool equipped = false;
            foreach (SavedPetSlot slot in save.PetSlots)
            {
                if (slot.Part == PetEquipment.PetPart)
                {
                    PetInstance pet = Roster.Find(slot.Value);
                    if (pet != null && Equipment.Pet == null)
                    {
                        pet.Equip(userFighting, now);
                        Equipment.RestorePet(pet);
                        equipped = true;
                    }
                    else
                    {
                        petsDropped++;
                    }
                }
                else
                {
                    ItemInfo artifact = lookup(slot.Value);
                    if (PetEquipRules.CheckArtifactItem(artifact, slot.Part) == PetEquipResult.Ok && Equipment.Artifact(slot.Part) == null)
                    {
                        Equipment.RestoreArtifact(slot.Part, artifact);
                        artifacts++;
                    }
                    else
                    {
                        artifactsDropped++;
                    }
                }
            }
            return new PetRestoreReport(pets, petsDropped, equipped, artifacts, artifactsDropped);
        }
    }
}
