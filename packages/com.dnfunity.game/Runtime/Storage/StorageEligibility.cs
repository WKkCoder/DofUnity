using System;

// 证据:存入个人仓库的资格——CUser::exchange_interspace 0x0865f7b0 在"背包 → 个人仓库"分支里依次拒绝:物品读不到;CItem::IsImpossibleCharacCargo(虚表偏移 0x40,0x085143be);
//   编号在写死的一段区间里;类别字节为 4(任务类)。其余都能存(类别字节为 1 的装备走单格,别的类别走叠加)。
// 存入账号金库的资格——同一函数 0x0866005f 起先拒绝 IsTradeLimitAttachTypeItem 0x081201dc(可叠加物品且绑定类型为 7),再过 CAccountCargo::CheckInsertCondition 0x08289a4a:
//   物品读不到;不是 CItem::isPackagable;类别字节为 4 到 8;编号在写死的那段区间里;绑定类型为 1 或 2;(绑定类型 3 还要看实例的一个标志,新造出来的物品标志为 1,所以通过);
//   实例级的检查(装备锁、交易限制、有期限的物品过期、GM 免检)本项目的物品没有实例状态,不建模。
// 类别字节的来源:CEquipItem::make_item 0x0851098a(时装 8、宠物 5、神器 6、其余 1)与 CStackableItem::make_item 0x0850f8b8(任务 4、材料 3、专家职业材料 10、宠物类 7、徽章 9、其余 2)。
// 绑定类型的编号:ImportEquipmentScript 0x08991aeb 与 ImportStackableScript 0x089aeec8 里"字符串 → 数"的比较链。
namespace Dnf.Game
{
    /// <summary>存进仓库的资格判断(纯逻辑,只看物品本身与数据,不看仓库满不满)。</summary>
    public static class StorageEligibility
    {
        // ---- 类别字节(服务端 Inven_Item 偏移 1)
        public const int CategoryEquipment = 1;
        public const int CategoryConsumable = 2;
        public const int CategoryMaterial = 3;
        public const int CategoryQuest = 4;
        public const int CategoryCreature = 5;
        public const int CategoryArtifact = 6;
        public const int CategoryCreatureStackable = 7;
        public const int CategoryAvatar = 8;
        public const int CategoryAvatarEmblem = 9;
        public const int CategoryMaterialExpertJob = 10;

        // ---- 绑定类型编号(服务端 CItem::GetAttachType)
        public const int AttachFree = 0;
        public const int AttachTrade = 1;
        public const int AttachTradeDelete = 2;
        public const int AttachSealing = 3;
        public const int AttachSealingTrade = 4;
        public const int AttachAccount = 5;
        public const int AttachTradeLimit = 7;

        /// <summary>脚本没写 <c>[attach type]</c> 或写了不认识的字符串。</summary>
        public const int AttachUnknown = -1;

        // 可叠加类型里与类别字节对应的几个编号(与 StackableTypes 的编号一致)
        private const int StackableMaterialExpertJob = 2;
        private const int StackableCreature = 9;
        private const int StackableFeed = 10;
        private const int StackableAvatarEmblem = 20;

        /// <summary>物品的类别字节(服务端 make_item 写进 Inven_Item 的那个)。物品为空按装备算。</summary>
        public static int CategoryOf(ItemInfo info)
        {
            if (info == null || info.IsEquipment)
            {
                int part = info == null ? EquipmentParts.Unknown : info.Part;
                if (part == EquipmentParts.Creature)
                {
                    return CategoryCreature;
                }
                if (part >= EquipmentParts.ArtifactRed && part <= EquipmentParts.ArtifactGreen)
                {
                    return CategoryArtifact;
                }
                return EquipmentParts.IsAvatar(part) ? CategoryAvatar : CategoryEquipment;
            }
            switch (info.StackableType)
            {
                case StackableTypes.Material:
                    return CategoryMaterial;
                case StackableMaterialExpertJob:
                    return CategoryMaterialExpertJob;
                case StackableTypes.Quest:
                    return CategoryQuest;
                case StackableCreature:
                case StackableFeed:
                    return CategoryCreatureStackable;
                case StackableAvatarEmblem:
                    return CategoryAvatarEmblem;
                default:
                    return CategoryConsumable;
            }
        }

        /// <summary>物品的绑定类型编号(<c>[attach type]</c> 的字符串换成服务端的数);不认识返回 <see cref="AttachUnknown"/>。</summary>
        public static int AttachTypeOf(ItemInfo info)
        {
            string tag = info?.AttachType;
            if (tag == null)
            {
                return AttachUnknown;
            }
            if (string.Equals(tag, "[free]", StringComparison.OrdinalIgnoreCase))
            {
                return AttachFree;
            }
            if (string.Equals(tag, "[trade]", StringComparison.OrdinalIgnoreCase))
            {
                return AttachTrade;
            }
            if (string.Equals(tag, "[trade delete]", StringComparison.OrdinalIgnoreCase))
            {
                return AttachTradeDelete;
            }
            if (string.Equals(tag, "[sealing]", StringComparison.OrdinalIgnoreCase))
            {
                return AttachSealing;
            }
            if (string.Equals(tag, "[sealing trade]", StringComparison.OrdinalIgnoreCase))
            {
                return AttachSealingTrade;
            }
            if (string.Equals(tag, "[account]", StringComparison.OrdinalIgnoreCase))
            {
                return AttachAccount;
            }
            if (string.Equals(tag, "[trade limit]", StringComparison.OrdinalIgnoreCase))
            {
                return AttachTradeLimit;
            }
            return AttachUnknown;
        }

        /// <summary>
        /// 这件物品能不能存进 <paramref name="kind"/> 这种仓库:能返回 <see cref="StorageResult.Ok"/>,不能返回 <see cref="StorageResult.NotAllowed"/>(物品为空返回 <see cref="StorageResult.NoItem"/>)。
        /// <paramref name="settings"/> 为 null 时用默认设置(没有物品事实、没有排除区间、账号金库要求 <c>[packagable]</c>)。
        /// </summary>
        public static StorageResult Check(StorageKind kind, ItemInfo info, StorageSettings settings)
        {
            if (info == null)
            {
                return StorageResult.NoItem;
            }
            StorageSettings s = settings ?? new StorageSettings();
            StorageItemFacts facts = s.FactsOf(info);
            int category = CategoryOf(info);
            if (kind == StorageKind.Personal)
            {
                bool refused = facts.ImpossibleCharacCargo || s.IsExcludedCode(info.Code) || category == CategoryQuest;
                return refused ? StorageResult.NotAllowed : StorageResult.Ok;
            }
            if (s.RequirePackagable && !facts.Packagable)
            {
                return StorageResult.NotAllowed;
            }
            if (category >= CategoryQuest && category <= CategoryAvatar)
            {
                return StorageResult.NotAllowed;
            }
            if (s.IsExcludedCode(info.Code))
            {
                return StorageResult.NotAllowed;
            }
            int attach = AttachTypeOf(info);
            if (attach == AttachTrade || attach == AttachTradeDelete)
            {
                return StorageResult.NotAllowed;
            }
            if (info.IsStackable && attach == AttachTradeLimit)
            {
                return StorageResult.NotAllowed;
            }
            return StorageResult.Ok;
        }
    }
}
