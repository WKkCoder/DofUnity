using System;
using System.Collections.Generic;
using Dnf.Game;

// 本项目自己的设计(r15-storage):把 Game 层的仓库逻辑(AccountStorage)接到主角进度(HeroProgress)上。服务端的规则与证据在 Game 层各文件的头注释里,这里没有新的原版证据。
// 不改 HeroProgress:背包就是它的 Bag(Inventory 原样用),钱经 HeroPurse 走它的 TrySpendGold / AddGold。
namespace Dnf.Play
{
    /// <summary>把 <see cref="HeroProgress"/> 的金币接成仓库用的钱包。</summary>
    public sealed class HeroPurse : IStoragePurse
    {
        private readonly HeroProgress _hero;

        public HeroPurse(HeroProgress hero)
        {
            _hero = hero ?? throw new ArgumentNullException(nameof(hero));
        }

        public long Gold => _hero.Gold;

        public bool TrySpend(long amount) => _hero.TrySpendGold(amount);

        /// <summary>
        /// 加金币走 <see cref="HeroProgress.AddGold"/>,所以从账号金库取出来的钱也会计进 <see cref="HeroProgress.GoldGained"/>(累计进账);
        /// 要不计进去得给 HeroProgress 加一个只加钱不计累计的入口(见交接文档的请求)。
        /// </summary>
        public void Receive(long amount) => _hero.AddGold(amount);
    }

    /// <summary>读档时发生了什么(宿主写进日志)。</summary>
    public sealed class StorageApplyReport
    {
        public StorageApplyReport(int accountSlots, int personalSlots, int dropped)
        {
            AccountSlots = accountSlots;
            PersonalSlots = personalSlots;
            Dropped = dropped;
        }

        /// <summary>还原的账号金库格数。</summary>
        public int AccountSlots { get; }

        /// <summary>还原的个人仓库格数(各角色合计)。</summary>
        public int PersonalSlots { get; }

        /// <summary>找不到物品(编号不在用户的数据里)、格子没开放、格号重复或数量超出一格上限而丢掉的格数。</summary>
        public int Dropped { get; }
    }

    /// <summary>
    /// 一个角色打开仓库的会话:这个角色(<see cref="HeroKey"/>)的主角进度加上账号级的仓库数据(<see cref="Storage"/>)。
    /// 存取、扩容、开通、升级、存取金币都经这里:背包与金币用主角的,做成之后把快捷栏对齐背包(存走的药水从快捷栏拿掉、取回的药水自动进快捷栏,同拾取与卖出)。
    /// 每个动作的检查次序与失败时"什么都不动"见 <see cref="AccountStorage"/>。
    /// </summary>
    public sealed class StorageSession
    {
        private readonly HeroPurse _purse;

        public StorageSession(HeroProgress hero, string heroKey, AccountStorage storage)
        {
            Hero = hero ?? throw new ArgumentNullException(nameof(hero));
            if (!AccountStorage.IsValidHeroKey(heroKey))
            {
                throw new ArgumentException("角色键不合法", nameof(heroKey));
            }
            HeroKey = heroKey;
            Storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _purse = new HeroPurse(hero);
        }

        public HeroProgress Hero { get; }

        /// <summary>这个角色在仓库存档里的键(宿主起的、不含空白的名字)。</summary>
        public string HeroKey { get; }

        /// <summary>账号级的仓库数据;读档(<see cref="Restore"/>)会换成新的,界面每次从这里重新取。</summary>
        public AccountStorage Storage { get; private set; }

        /// <summary>这个角色的个人仓库。</summary>
        public StorageVault Personal => Storage.PersonalVault(HeroKey);

        /// <summary>某一种仓库的格子(账号金库没开通时容量为 0)。</summary>
        public StorageVault VaultOf(StorageKind kind)
        {
            return kind == StorageKind.Account ? Storage.AccountVault : Personal;
        }

        /// <summary>把背包 <paramref name="tab"/> 页第 <paramref name="bagIndex"/> 格里的 <paramref name="count"/> 件存进仓库(见 <see cref="AccountStorage.Store"/>)。</summary>
        public StorageResult Store(StorageKind kind, InventoryTab tab, int bagIndex, int count, int preferredSlot, out int slot)
        {
            StorageResult result = Storage.Store(kind, HeroKey, Hero.Bag, tab, bagIndex, count, preferredSlot, out slot);
            if (result == StorageResult.Ok)
            {
                SyncQuick();
            }
            return result;
        }

        /// <summary>把仓库第 <paramref name="vaultSlot"/> 格里的 <paramref name="count"/> 件取回背包(见 <see cref="AccountStorage.Take"/>)。</summary>
        public StorageResult Take(StorageKind kind, int vaultSlot, int count)
        {
            ItemInfo info = VaultOf(kind).SlotAt(vaultSlot)?.Info;
            StorageResult result = Storage.Take(kind, HeroKey, vaultSlot, count, Hero.Bag);
            if (result == StorageResult.Ok)
            {
                Hero.Quick.Place(info);
            }
            return result;
        }

        /// <summary>仓库里两格互换。</summary>
        public StorageResult Swap(StorageKind kind, int slotA, int slotB)
        {
            return Storage.Swap(kind, HeroKey, slotA, slotB);
        }

        /// <summary>用背包里的一件扩容道具扩个人仓库(见 <see cref="AccountStorage.ExpandPersonal"/>)。</summary>
        public StorageResult ExpandPersonal(InventoryTab tab, int bagIndex)
        {
            StorageResult result = Storage.ExpandPersonal(HeroKey, Hero.Bag, tab, bagIndex);
            if (result == StorageResult.Ok)
            {
                SyncQuick();
            }
            return result;
        }

        /// <summary>开通账号金库(用主角现在的等级、背包与金币)。</summary>
        public StorageResult CreateAccountVault()
        {
            StorageResult result = Storage.CreateAccountVault(Hero.Level, Hero.Bag, _purse);
            if (result == StorageResult.Ok)
            {
                SyncQuick();
            }
            return result;
        }

        /// <summary>账号金库升一档。</summary>
        public StorageResult UpgradeAccountVault()
        {
            StorageResult result = Storage.UpgradeAccountVault(Hero.Bag, _purse);
            if (result == StorageResult.Ok)
            {
                SyncQuick();
            }
            return result;
        }

        /// <summary>把主角身上的金币存进账号金库。</summary>
        public StorageResult DepositGold(long amount)
        {
            return Storage.DepositGold(_purse, amount);
        }

        /// <summary>从账号金库取金币到主角身上(按主角现在的等级查携带上限)。</summary>
        public StorageResult WithdrawGold(long amount)
        {
            return Storage.WithdrawGold(_purse, amount, Hero.Level);
        }

        // 背包里已经没有的药水从快捷栏拿掉(同 HeroProgress 卖东西、用药之后的做法)。
        private void SyncQuick()
        {
            QuickSlots quick = Hero.Quick;
            for (int i = 0; i < QuickSlots.Count; i++)
            {
                ItemInfo item = quick[i];
                if (item != null && Hero.Bag.CountOf(item.Code) <= 0)
                {
                    quick.Clear(i);
                }
            }
        }

        /// <summary>
        /// 现在的仓库数据拍成存档快照:账号金库(没开通就没有)与每个角色的个人仓库(还是初始容量又没有东西的不写,所以只是看过一眼不会让存档变样)。
        /// 只有数字与物品编号;格子从小到大,角色键按字典序,所以内容没变文本也不变。
        /// </summary>
        public StorageSave Snapshot()
        {
            var save = new StorageSave
            {
                AccountCapacity = Storage.AccountTierCapacity,
                AccountGold = Storage.AccountGold,
            };
            AddSlots(save.AccountSlots, Storage.AccountVault);
            foreach (string key in Storage.PersonalKeys)
            {
                StorageVault vault = Storage.PersonalVault(key);
                if (vault.Capacity == StorageRules.PersonalInitialCapacity && vault.UsedSlots == 0)
                {
                    continue;
                }
                var saved = new SavedPersonalVault(key, vault.Capacity);
                AddSlots(saved.Slots, vault);
                save.Personal.Add(saved);
            }
            return save;
        }

        private static void AddSlots(List<SavedVaultSlot> into, StorageVault vault)
        {
            for (int slot = 0; slot < vault.Capacity; slot++)
            {
                ItemStack stack = vault.SlotAt(slot);
                if (stack != null)
                {
                    into.Add(new SavedVaultSlot(slot, stack.Info.Code, stack.Count));
                }
            }
        }

        /// <summary>
        /// 把存档还原成仓库数据(整个换成新的):账号金库按存档里的容量值与金币(不收费用、不对档位表,数据换了也不丢东西),个人仓库按键放回;
        /// <paramref name="lookup"/> 按物品编号取物品(没有返回 null)。编号找不到、格子没开放(容量不够)、格号重复、数量超出一格上限的格丢掉,数目在返回值里。
        /// </summary>
        public StorageApplyReport Restore(StorageSave save, Func<int, ItemInfo> lookup)
        {
            if (save == null)
            {
                throw new ArgumentNullException(nameof(save));
            }
            if (lookup == null)
            {
                throw new ArgumentNullException(nameof(lookup));
            }
            var fresh = new AccountStorage(Storage.Settings);
            int accountSlots = 0;
            int personalSlots = 0;
            int dropped = 0;
            fresh.RestoreAccount(save.AccountCapacity, save.AccountGold);
            foreach (SavedVaultSlot saved in save.AccountSlots)
            {
                if (RestoreSlot(fresh.AccountVault, saved, lookup))
                {
                    accountSlots++;
                }
                else
                {
                    dropped++;
                }
            }
            foreach (SavedPersonalVault saved in save.Personal)
            {
                if (!AccountStorage.IsValidHeroKey(saved.Key))
                {
                    dropped += saved.Slots.Count;
                    continue;
                }
                StorageVault vault = fresh.RestorePersonal(saved.Key, saved.Capacity);
                foreach (SavedVaultSlot slot in saved.Slots)
                {
                    if (RestoreSlot(vault, slot, lookup))
                    {
                        personalSlots++;
                    }
                    else
                    {
                        dropped++;
                    }
                }
            }
            Storage = fresh;
            return new StorageApplyReport(accountSlots, personalSlots, dropped);
        }

        private static bool RestoreSlot(StorageVault vault, SavedVaultSlot saved, Func<int, ItemInfo> lookup)
        {
            return vault.RestoreSlot(saved.Slot, lookup(saved.Code), saved.Count);        // 找不到物品时 RestoreSlot 自己拒绝
        }
    }
}
