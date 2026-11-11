using System;

// 证据(服务端静态读码,没有运行;地址是 df_game_r.orig 的虚拟地址):
// 宝珠附魔 expert_job::CExpertJobMgr::OnEnchantByBead 0x0849ed1a 的次序:宝珠格或装备格取不到物品 → 0x11;租赁物品 → 0x11;两件的定义查不到 → 0x11;
//   宝珠不是可叠加物品 → 0x11;宝珠换出卡片(CStackableItem::GetMonsterCardId 0x0849f530,[monster card id])定义查不到 → 0x11;
//   世界兼容 EnableWorld 0x0849d768(这里不建模,由调用方给判断)不过 → 0x13;
//   卡片的 getUsableItemType(装备部位) 0x0850d780 不过 → 0x17;删宝珠一个(失败 → 0x11);
//   装备个体上偏移 0xd 的整数(附魔编号)改成卡片的物品编号——旧的附魔被直接覆盖,没有任何折算或退还;成功返回 0,不收金币(金币与技能归副职业"附魔师" _DoEnchant 0x0849cb2c)。
// 能被附魔的部位:CEquipItem::IsEnchantalbe 0x08514db0 = 部位 10 到 21。
namespace Dnf.Game
{
    /// <summary>附魔流程的错误码(<c>OnEnchantByBead</c> 的返回值)。</summary>
    public static class EnchantCodes
    {
        public const int Ok = 0;

        /// <summary>格子空、定义查不到、宝珠不是可叠加物品、宝珠换不出卡片,或者删不掉宝珠。</summary>
        public const int Invalid = 0x11;

        /// <summary>世界兼容不过(<c>EnableWorld</c>)。</summary>
        public const int World = 0x13;

        /// <summary>这张卡不能附在这个部位。</summary>
        public const int Part = 0x17;
    }

    /// <summary>一次附魔的结果。</summary>
    public sealed class EnchantResult
    {
        internal EnchantResult(int code, ItemInfo equipment, ItemInfo card, int previousCode)
        {
            Code = code;
            Equipment = equipment;
            Card = card;
            PreviousCardCode = previousCode;
        }

        /// <summary>0 成功,否则 <see cref="EnchantCodes"/>。</summary>
        public int Code { get; }

        public bool Success => Code == EnchantCodes.Ok;

        /// <summary>附魔之后的装备(失败时是原来的那件)。</summary>
        public ItemInfo Equipment { get; }

        /// <summary>附上的卡片(宝珠换出的那张);失败时可能为 null。</summary>
        public ItemInfo Card { get; }

        /// <summary>被覆盖掉的旧附魔卡编号(0 表示原来没有附魔)。</summary>
        public int PreviousCardCode { get; }

        /// <summary>要从背包里扣一个宝珠(只有成功才为真)。</summary>
        public bool ConsumesBead => Success;
    }

    /// <summary>装备附魔的规则:宝珠换出卡片、检查部位、覆盖旧卡。不碰背包(调用方按结果去扣宝珠、换装备)。</summary>
    public static class EnchantRules
    {
        /// <summary>这个部位能不能附魔(服务端 <c>IsEnchantalbe</c>:部位 10 到 21)。</summary>
        public static bool IsEnchantable(int part) => part >= UpgradeTable.FirstPart && part <= UpgradeTable.LastPart;

        /// <summary>这张卡能不能附在这个部位(卡的部位表里有它,同时部位本身可附魔)。</summary>
        public static bool CardAllows(ItemInfo card, int part)
        {
            if (card?.Enhance == null || !IsEnchantable(part))
            {
                return false;
            }
            foreach (int allowed in card.Enhance.EnchantParts)
            {
                if (allowed == part)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 宝珠附魔(服务端流程):宝珠换出卡片再附上。<paramref name="enableWorld"/> 是世界兼容判断(参数:装备、宝珠),不给就当通过。
        /// </summary>
        public static EnchantResult ApplyBead(ItemInfo equipment, ItemInfo bead, Func<int, ItemInfo> lookup, Func<ItemInfo, ItemInfo, bool> enableWorld = null)
        {
            if (lookup == null)
            {
                throw new ArgumentNullException(nameof(lookup));
            }
            int previous = equipment?.Instance != null ? equipment.Instance.EnchantCode : 0;
            if (equipment == null || bead == null || !equipment.IsEquipment || !bead.IsStackable)
            {
                return new EnchantResult(EnchantCodes.Invalid, equipment, null, previous);
            }
            int cardCode = bead.Enhance != null ? bead.Enhance.MonsterCardId : 0;
            ItemInfo card = cardCode != 0 ? lookup(cardCode) : null;
            if (card == null)
            {
                return new EnchantResult(EnchantCodes.Invalid, equipment, null, previous);
            }
            if (enableWorld != null && !enableWorld(equipment, bead))
            {
                return new EnchantResult(EnchantCodes.World, equipment, card, previous);
            }
            return Attach(equipment, card);
        }

        /// <summary>
        /// 直接把一张卡附到装备上(检查部位,覆盖旧卡)。服务端的"拿卡直接附"在副职业附魔师的流程里(<c>_DoEnchant</c>,不在这条线),
        /// 这里只提供同一套部位与覆盖规则,供那一线接入。
        /// </summary>
        public static EnchantResult ApplyCard(ItemInfo equipment, ItemInfo card)
        {
            int previous = equipment?.Instance != null ? equipment.Instance.EnchantCode : 0;
            if (equipment == null || card == null || !equipment.IsEquipment)
            {
                return new EnchantResult(EnchantCodes.Invalid, equipment, card, previous);
            }
            return Attach(equipment, card);
        }

        private static EnchantResult Attach(ItemInfo equipment, ItemInfo card)
        {
            EquipInstance old = equipment.Instance;
            int previous = old != null ? old.EnchantCode : 0;
            if (!CardAllows(card, equipment.Part))
            {
                return new EnchantResult(EnchantCodes.Part, equipment, card, previous);
            }
            EquipInstance next = equipment.StateOrDefault;
            next.EnchantCode = card.Code;
            return new EnchantResult(EnchantCodes.Ok, equipment.WithInstance(next), card, previous);
        }
    }
}
