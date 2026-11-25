using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Play
{
    public sealed partial class HeroProgress
    {
        private string _heroName = CharacterRoster.LocalName;
        private bool _isOnline;
        private ItemCatalog _onlineCatalog;

        /// <summary>主角头顶名牌展示的名称;默认 <see cref="CharacterRoster.LocalName"/>,联网进城时写入选中角色名。</summary>
        public string HeroName
        {
            get => _heroName;
            set => _heroName = string.IsNullOrEmpty(value) ? CharacterRoster.LocalName : value;
        }

        /// <summary>是否是通过 <see cref="CreateOnline"/> 建立的联网主角(绝不读写离线存档)。</summary>
        public bool IsOnline => _isOnline;

        /// <summary>是否因为服务端职业非鬼剑士而显式退回了鬼剑士外观与技能。</summary>
        public bool IsJobFallback { get; private set; }

        /// <summary>
        /// 按联网选中角色在内存中建立主角进度(绝不读写离线 <see cref="HeroSave"/>):
        /// 仅鬼剑士(<see cref="EquipRules.SwordmanJob"/>)套用真实一转与觉醒成长增量;其他职业显式退回鬼剑士基础数据并标记 <see cref="IsJobFallback"/>。
        /// </summary>
        public static HeroProgress CreateOnline(GameData data, MobRewardTable rewards, OnlineHeroProfile profile)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }
            bool fallback = profile.Job != EquipRules.SwordmanJob;
            CharacterDef chr = data.GetCharacter(EquipRules.SwordmanJob);
            if (chr == null)
            {
                return null;
            }
            int growtype = fallback ? DefaultGrowtype : Math.Max(0, profile.GrowType);
            int awakening = fallback ? 0 : Math.Max(0, Math.Min(2, profile.Awakening));
            ExpTable table = data.ExpTable;
            var progress = new HeroProgress(
                table,
                data.SpTable,
                data.GetGrowth(chr),
                rewards ?? MobRewardTable.Placeholder(table),
                data.ExpRates,
                profile.Level < 1 ? 1 : profile.Level,
                exp: -1,
                growtype: growtype,
                awakening: awakening);
            progress._isOnline = true;
            progress._onlineCatalog = new ItemCatalog(data);
            progress.HeroName = profile.Name;
            progress.Job = chr.Job;
            progress.IsJobFallback = fallback;
            progress.AttachSkills(SkillCatalog.Load(data, chr.Job), chr.Tags.Lines, grantTotalPoints: true);
            return progress;
        }

        /// <summary>
        /// 宿主入口(公开签名不引用 <c>Dnf.Game</c>,保证无传递引用的客户端程序集在 Unity 下不产生 CS0012):
        /// 按联网角色档案从数据资源建立不落盘的在线 <see cref="HeroProgress"/>,可选直接应用进城物品同步数据。
        /// </summary>
        public static HeroProgress CreateForOnlineHero(DnfAssets assets, OnlineHeroProfile profile, OnlineItemSyncData items = null)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }
            var data = new GameData(new PvfScriptSource(assets.Pvf));
            HeroProgress progress = CreateOnline(data, MobRewards.Load(assets, data.ExpTable), profile);
            if (progress != null && items != null)
            {
                progress.ApplyOnlineItems(items);
            }
            return progress;
        }

        /// <summary>
        /// 把线上物品同步数据应用到内存中的联网主角(只给 <see cref="IsOnline"/> 为真的实例用,绝不读写 <see cref="HeroSave"/>,
        /// 也不改动 <see cref="GoldGained"/> 与 <see cref="ItemsGained"/>)。
        /// </summary>
        public OnlineItemSyncReport ApplyOnlineItems(OnlineItemSyncData data)
        {
            if (!_isOnline)
            {
                throw new InvalidOperationException("只有联网主角才能应用线上物品同步数据");
            }
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            bool goldSynced = false;
            bool inventorySynced = false;
            bool equipsSynced = false;
            int bagApplied = 0;
            int quickApplied = 0;
            int equipsApplied = 0;
            int unknownItemCount = 0;
            long overflowItemCount = 0;
            int invalidEquipSlotCount = 0;

            if (data.HasGold)
            {
                Gold = data.Gold < 0L ? 0L : data.Gold;
                goldSynced = true;
            }

            if (data.HasBagData)
            {
                Bag.Clear();
                Quick.ClearAll();
                inventorySynced = true;

                IReadOnlyList<OnlineItemSlotEntry> quickEntries = data.QuickEntries;
                for (int i = 0; i < quickEntries.Count; i++)
                {
                    OnlineItemSlotEntry entry = quickEntries[i];
                    if (entry == null)
                    {
                        continue;
                    }
                    ItemInfo info = LookupOnlineItem(entry.ItemCode);
                    if (info == null)
                    {
                        unknownItemCount++;
                        continue;
                    }
                    ItemInfo resolved = WithOnlineEquipInstance(info, entry);
                    int count = resolved.IsEquipment ? 1 : Math.Max(1, entry.Count);
                    int put = Bag.Add(resolved, count);
                    if (put < count)
                    {
                        overflowItemCount += (long)count - put;
                    }
                    int quickSlot = entry.Slot - 3;
                    if (quickSlot >= 0 && quickSlot < QuickSlots.Count && Quick.Set(quickSlot, resolved))
                    {
                        quickApplied++;
                    }
                }

                IReadOnlyList<OnlineItemSlotEntry> bagEntries = data.BagEntries;
                for (int i = 0; i < bagEntries.Count; i++)
                {
                    OnlineItemSlotEntry entry = bagEntries[i];
                    if (entry == null)
                    {
                        continue;
                    }
                    ItemInfo info = LookupOnlineItem(entry.ItemCode);
                    if (info == null)
                    {
                        unknownItemCount++;
                        continue;
                    }
                    ItemInfo resolved = WithOnlineEquipInstance(info, entry);
                    int count = resolved.IsEquipment ? 1 : Math.Max(1, entry.Count);
                    int put = Bag.Add(resolved, count);
                    if (put > 0)
                    {
                        bagApplied++;
                    }
                    if (put < count)
                    {
                        overflowItemCount += (long)count - put;
                    }
                }
            }

            if (data.HasEquipData)
            {
                equipsSynced = true;
                var desired = new ItemInfo[EquipmentSlots.Count];
                IReadOnlyList<OnlineItemSlotEntry> wornEquips = data.WornEquips;
                for (int i = 0; i < wornEquips.Count; i++)
                {
                    OnlineItemSlotEntry entry = wornEquips[i];
                    if (entry == null)
                    {
                        continue;
                    }
                    int part = entry.Slot;
                    if (!EquipmentSlots.IsSlot(part))
                    {
                        invalidEquipSlotCount++;
                        continue;
                    }
                    ItemInfo info = LookupOnlineItem(entry.ItemCode);
                    if (info == null || !info.IsEquipment)
                    {
                        unknownItemCount++;
                        continue;
                    }
                    ItemInfo resolved = WithOnlineEquipInstance(info, entry);
                    ItemInfo existing = Worn.Get(part);
                    if (existing != null
                        && ReferenceEquals(existing.Definition, resolved.Definition)
                        && Equals(existing.Instance, resolved.Instance))
                    {
                        resolved = existing;
                    }
                    desired[part - EquipmentSlots.First] = resolved;
                }
                for (int offset = 0; offset < EquipmentSlots.Count; offset++)
                {
                    int part = EquipmentSlots.First + offset;
                    ItemInfo next = desired[offset];
                    if (next == null)
                    {
                        Worn.Take(part);
                    }
                    else
                    {
                        Worn.Restore(part, next);
                    }
                }
                equipsApplied = Worn.EquippedCount;
            }

            int clampedOverflow = overflowItemCount > int.MaxValue ? int.MaxValue : (int)overflowItemCount;
            return new OnlineItemSyncReport(
                goldSynced,
                inventorySynced,
                equipsSynced,
                bagApplied,
                quickApplied,
                equipsApplied,
                unknownItemCount,
                clampedOverflow,
                invalidEquipSlotCount,
                data.UnmodeledAvatarBagCount,
                data.UnmodeledCargoCount,
                data.UnmodeledPetBagCount,
                data.UnmodeledAccountCargoCount,
                data.UnmodeledExpandEquipCount);
        }

        private ItemInfo LookupOnlineItem(uint itemCode)
        {
            if (_onlineCatalog == null || itemCode > int.MaxValue)
            {
                return null;
            }
            return _onlineCatalog.Get((int)itemCode);
        }

        private static ItemInfo WithOnlineEquipInstance(ItemInfo info, OnlineItemSlotEntry entry)
        {
            if (!info.IsEquipment)
            {
                return info;
            }
            int upgrade = entry.UpgradeLevel < 0 ? 0 : (entry.UpgradeLevel > 255 ? 255 : entry.UpgradeLevel);
            int enchant = entry.EnchantCode <= int.MaxValue ? (int)entry.EnchantCode : 0;
            int grade = Math.Max(0, entry.GradeValue);
            if (upgrade == 0 && !entry.IsAmplified && entry.IsIdentified && enchant == 0 && grade == 0)
            {
                return info;
            }
            return info.WithInstance(new EquipInstance(upgrade, entry.IsAmplified, entry.IsIdentified, enchant, grade));
        }
    }
}
