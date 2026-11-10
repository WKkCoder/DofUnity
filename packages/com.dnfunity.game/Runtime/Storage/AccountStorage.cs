using System;
using System.Collections.Generic;

// 证据:本类把服务端几处分散的仓库操作收成按账号存的一份数据——
//   开通账号金库 CAccountCargo::CreateAccountCargo 0x0828ad72(次序:已有→0x14、等级不够→0x0e、没有档位数据→0x13、材料不够→0x16、金币不够→0x16,然后先扣材料再扣钱;
//     材料要求"件数不为 0",不够指背包里那一格的数量小于要求;金币要求"价钱不小于 1 才收",钱数等于价钱可以);
//   升级 UpgradeAccountCargo 0x0828b0b2(没有金库→0x15、没有下一档→0x13,其余同开通,没有等级要求);
//   存取金币 DepositMoney 0x0828a12a / WithdrawMoney 0x0828a2f6 / CheckMoneyLimit 0x0828a4ca(金额为负或超出可用的→10;存入后超过当前档金币上限→0x5f,等于可以;
//     取出后角色身上的钱超过等级上限→0x5e,等于可以;当前容量不在档位表里时存入一律不通过);
//   存取物品 CUser::exchange_interspace 0x0865f7b0(个人仓库一支、账号金库一支,次序见 StorageVault 的头注释);
//   个人仓库扩容 Dispatcher_UpgradeCargo 0x0822a0a4(次序:背包格没有物品→0x11、不是扩容道具→0x11、已到上限→0x13,然后扣一件、容量加 16)。
// 与服务端的差别(选择):材料件数按背包里这个编号的总数算(服务端只看一格);两个仓库的操作都是"先全部检查、再动手",所以失败时背包、仓库、钱包都原样。
namespace Dnf.Game
{
    /// <summary>
    /// 一个账号的全部仓库数据:账号金库(开通与否、档位、金币、格子)与每个角色的个人仓库(按角色键存)。
    /// 所有改动都经过这里的方法,每个方法要么整个做完(返回 <see cref="StorageResult.Ok"/>)要么什么也不动。
    /// 背包用 <see cref="Inventory"/>、钱包用 <see cref="IStoragePurse"/>,都由调用方传进来,本类不持有它们。
    /// </summary>
    public sealed class AccountStorage
    {
        /// <summary>角色键的最大长度(防写坏的存档)。</summary>
        public const int MaxHeroKeyLength = 64;

        private readonly Dictionary<string, StorageVault> _personal = new Dictionary<string, StorageVault>(StringComparer.Ordinal);
        private readonly StorageVault _account = new StorageVault(StorageKind.Account, 0);
        private int _accountTier;
        private long _accountGold;

        public AccountStorage(StorageSettings settings = null)
        {
            Settings = settings ?? new StorageSettings();
        }

        public StorageSettings Settings { get; }

        /// <summary>改动次数:每次成功的存取、扩容、开通、升级、存取金币加一,只增不减(界面据此知道要不要重画)。读档不算。</summary>
        public int Version { get; private set; }

        // ---------------- 账号金库

        /// <summary>账号金库开通了吗。</summary>
        public bool HasAccountVault => _accountTier > 0;

        /// <summary>账号金库现在的容量值(档位表里的键;没开通是 0)。开放的格数是它与 <see cref="StorageRules.AccountMaxSlots"/> 里小的那个。</summary>
        public int AccountTierCapacity => _accountTier;

        /// <summary>账号金库的格子(没开通时容量为 0)。</summary>
        public StorageVault AccountVault => _account;

        /// <summary>账号金库里的金币。</summary>
        public long AccountGold => _accountGold;

        /// <summary>现在这一档账号金库最多放多少金币;没开通、没有账号金库脚本、或当前容量不在档位表里是 0。</summary>
        public long AccountGoldLimit
        {
            get
            {
                AccountCargoTier tier = Settings.AccountScript?.Current(_accountTier);
                return tier == null ? 0 : tier.GoldLimit;
            }
        }

        /// <summary>开通账号金库:所需的等级、材料(<see cref="AccountCargoScript.RequiredItem"/> 件数见最小一档)与金币从背包与钱包里扣。</summary>
        public StorageResult CreateAccountVault(int heroLevel, Inventory bag, IStoragePurse purse)
        {
            RequireHeroState(bag, purse);
            AccountCargoScript script = Settings.AccountScript;
            if (script == null)
            {
                return StorageResult.NoData;
            }
            if (HasAccountVault)
            {
                return StorageResult.AccountVaultExists;
            }
            if (heroLevel < script.RequiredLevel)
            {
                return StorageResult.LevelTooLow;
            }
            AccountCargoTier tier = script.Create;
            if (tier == null || tier.Capacity < 1)
            {
                return StorageResult.NoData;
            }
            StorageResult paid = Pay(tier, script.RequiredItem, bag, purse);
            if (paid != StorageResult.Ok)
            {
                return paid;
            }
            _accountTier = tier.Capacity;
            _account.GrowTo(tier.Capacity);
            Version++;
            return StorageResult.Ok;
        }

        /// <summary>账号金库升到下一档:容量与金币上限换成下一档的,价钱与材料从背包与钱包里扣。</summary>
        public StorageResult UpgradeAccountVault(Inventory bag, IStoragePurse purse)
        {
            RequireHeroState(bag, purse);
            if (!HasAccountVault)
            {
                return StorageResult.NoAccountVault;
            }
            AccountCargoScript script = Settings.AccountScript;
            if (script == null)
            {
                return StorageResult.NoData;
            }
            AccountCargoTier next = script.Next(_accountTier);
            if (next == null)
            {
                return StorageResult.AtMaxCapacity;
            }
            StorageResult paid = Pay(next, script.RequiredItem, bag, purse);
            if (paid != StorageResult.Ok)
            {
                return paid;
            }
            _accountTier = next.Capacity;
            _account.GrowTo(next.Capacity);
            Version++;
            return StorageResult.Ok;
        }

        // 交一档的价钱:先查材料够不够,再扣金币(钱包自己判断够不够,不够就什么也没扣),最后扣材料——这样哪一步失败背包与钱包都原样。
        // 材料件数不大于 0 不收材料,价钱不大于 0 不收金币。
        private static StorageResult Pay(AccountCargoTier tier, int requiredItem, Inventory bag, IStoragePurse purse)
        {
            int material = tier.MaterialCount;
            long price = tier.PriceGold;
            if (material > 0 && bag.CountOf(requiredItem) < material)               // 脚本没写材料编号(0)时背包里数不出这种东西,按没有材料算
            {
                return StorageResult.NoMaterial;
            }
            if (price > 0 && !purse.TrySpend(price))
            {
                return StorageResult.NoGold;
            }
            if (material > 0)
            {
                bag.Remove(requiredItem, material);
            }
            return StorageResult.Ok;
        }

        /// <summary>把 <paramref name="amount"/> 金币从钱包存进账号金库。金额为 0 什么也不动(算成功);存完不能超过这一档的金币上限(等于可以)。</summary>
        public StorageResult DepositGold(IStoragePurse purse, long amount)
        {
            if (purse == null)
            {
                throw new ArgumentNullException(nameof(purse));
            }
            if (!HasAccountVault)
            {
                return StorageResult.NoAccountVault;
            }
            if (amount < 0)
            {
                return StorageResult.BadAmount;
            }
            if (amount > purse.Gold)
            {
                return StorageResult.NoGold;
            }
            AccountCargoTier tier = Settings.AccountScript?.Current(_accountTier);
            if (tier == null || amount > tier.GoldLimit - _accountGold)            // 等价于 存后 > 上限;写成减法是怕存档里的金币写得极大时加法溢出
            {
                return StorageResult.OverVaultGoldLimit;
            }
            if (amount == 0)
            {
                return StorageResult.Ok;
            }
            if (!purse.TrySpend(amount))
            {
                return StorageResult.NoGold;
            }
            _accountGold += amount;
            Version++;
            return StorageResult.Ok;
        }

        /// <summary>
        /// 从账号金库取 <paramref name="amount"/> 金币到钱包。金额为 0 什么也不动(算成功);金库里要有这么多;
        /// 取完角色身上的钱不能超过 <paramref name="heroLevel"/> 级的携带上限(等于可以;没有上限表就不查)。
        /// </summary>
        public StorageResult WithdrawGold(IStoragePurse purse, long amount, int heroLevel)
        {
            if (purse == null)
            {
                throw new ArgumentNullException(nameof(purse));
            }
            if (!HasAccountVault)
            {
                return StorageResult.NoAccountVault;
            }
            if (amount < 0)
            {
                return StorageResult.BadAmount;
            }
            if (amount > _accountGold)
            {
                return StorageResult.NoGold;
            }
            GoldLimitTable limits = Settings.GoldLimits;
            if (limits != null && amount > limits.LimitOf(heroLevel) - purse.Gold)   // 等价于 取后 > 上限;减法同上,防溢出
            {
                return StorageResult.OverHeroGoldLimit;
            }
            if (amount == 0)
            {
                return StorageResult.Ok;
            }
            _accountGold -= amount;
            purse.Receive(amount);
            Version++;
            return StorageResult.Ok;
        }

        // ---------------- 个人仓库

        /// <summary>角色键合法吗:不空、不超过 <see cref="MaxHeroKeyLength"/> 个字符、不含空白、不以 # 开头(存档是按空白分词、# 开头当注释的文本)。</summary>
        public static bool IsValidHeroKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MaxHeroKeyLength || key[0] == '#')
            {
                return false;
            }
            for (int i = 0; i < key.Length; i++)
            {
                if (char.IsWhiteSpace(key[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>取某个角色的个人仓库;还没有就按初始容量(<see cref="StorageRules.PersonalInitialCapacity"/>)新建一个。</summary>
        public StorageVault PersonalVault(string heroKey)
        {
            if (!IsValidHeroKey(heroKey))
            {
                throw new ArgumentException("角色键不合法", nameof(heroKey));
            }
            if (!_personal.TryGetValue(heroKey, out StorageVault vault))
            {
                vault = new StorageVault(StorageKind.Personal, StorageRules.PersonalInitialCapacity);
                _personal[heroKey] = vault;
            }
            return vault;
        }

        /// <summary>已经有个人仓库的角色键(按字典序)。</summary>
        public IReadOnlyList<string> PersonalKeys
        {
            get
            {
                var keys = new List<string>(_personal.Keys);
                keys.Sort(StringComparer.Ordinal);
                return keys;
            }
        }

        /// <summary>
        /// 用背包 <paramref name="tab"/> 页第 <paramref name="bagIndex"/> 格里的一件扩容道具(<see cref="StorageItemFacts.UpgradesCargo"/>)扩这个角色的个人仓库:
        /// 容量加 <see cref="StorageRules.PersonalExpandStep"/>,扣掉一件道具;容量已经不能再扩返回 <see cref="StorageResult.AtMaxCapacity"/>。
        /// </summary>
        public StorageResult ExpandPersonal(string heroKey, Inventory bag, InventoryTab tab, int bagIndex)
        {
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            StorageVault vault = PersonalVault(heroKey);
            IReadOnlyList<ItemStack> cells = bag.Tab(tab);
            if ((uint)bagIndex >= (uint)cells.Count)
            {
                return StorageResult.NoItem;
            }
            ItemInfo info = cells[bagIndex].Info;
            if (!info.IsStackable || !Settings.FactsOf(info).UpgradesCargo)
            {
                return StorageResult.NotExpansionItem;
            }
            if (!StorageRules.CanExpandPersonal(vault.Capacity))
            {
                return StorageResult.AtMaxCapacity;
            }
            bag.RemoveAt(tab, bagIndex, 1);
            vault.GrowTo(vault.Capacity + StorageRules.PersonalExpandStep);
            Version++;
            return StorageResult.Ok;
        }

        // ---------------- 存取物品

        /// <summary>
        /// 把背包 <paramref name="tab"/> 页第 <paramref name="bagIndex"/> 格里的 <paramref name="count"/> 件存进仓库(<paramref name="kind"/> 是哪一种;个人仓库按 <paramref name="heroKey"/>)。
        /// <paramref name="preferredSlot"/> 是想放的格(不指定给 −1);<paramref name="slot"/> 返回实际放进的格号(失败 −1)。
        /// 次序:仓库有没有、背包格有没有物品 → 资格(<see cref="StorageEligibility"/>)→ 件数不超过这一格有的 → 仓库放得下(<see cref="StorageVault.PlanPut"/>)→ 动手。
        /// 失败时背包与仓库都原样;成功时背包少 <paramref name="count"/> 件、仓库多 <paramref name="count"/> 件,总数不变。
        /// </summary>
        public StorageResult Store(StorageKind kind, string heroKey, Inventory bag, InventoryTab tab, int bagIndex, int count, int preferredSlot, out int slot)
        {
            slot = -1;
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            StorageVault vault = Resolve(kind, heroKey, out StorageResult resolved);
            if (resolved != StorageResult.Ok)
            {
                return resolved;
            }
            IReadOnlyList<ItemStack> cells = bag.Tab(tab);
            if ((uint)bagIndex >= (uint)cells.Count)
            {
                return StorageResult.NoItem;
            }
            ItemStack cell = cells[bagIndex];
            ItemInfo info = cell.Info;
            StorageResult allowed = StorageEligibility.Check(kind, info, Settings);
            if (allowed != StorageResult.Ok)
            {
                return allowed;
            }
            if (count < 1 || count > cell.Count)
            {
                return StorageResult.BadQuantity;
            }
            StoragePlacement plan = vault.PlanPut(info, count, preferredSlot);
            if (!plan.IsOk)
            {
                return plan.Result;
            }
            bag.RemoveAt(tab, bagIndex, count);
            slot = vault.CommitPut(info, count, plan);
            Version++;
            return StorageResult.Ok;
        }

        /// <summary>
        /// 把仓库第 <paramref name="vaultSlot"/> 格里的 <paramref name="count"/> 件取回背包。
        /// 次序:仓库有没有 → 格子与件数(<see cref="StorageVault.PlanTake"/>)→ 背包放得下(<see cref="Inventory.CanAdd"/>,放不下返回 <see cref="StorageResult.BagFull"/>)→ 动手。
        /// 失败时背包与仓库都原样;成功时仓库少 <paramref name="count"/> 件、背包多 <paramref name="count"/> 件,总数不变。
        /// </summary>
        public StorageResult Take(StorageKind kind, string heroKey, int vaultSlot, int count, Inventory bag)
        {
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            StorageVault vault = Resolve(kind, heroKey, out StorageResult resolved);
            if (resolved != StorageResult.Ok)
            {
                return resolved;
            }
            StorageResult planned = vault.PlanTake(vaultSlot, count);
            if (planned != StorageResult.Ok)
            {
                return planned;
            }
            ItemInfo info = vault.SlotAt(vaultSlot).Info;
            if (!bag.CanAdd(info, count))
            {
                return StorageResult.BagFull;
            }
            vault.Take(vaultSlot, count);
            bag.Add(info, count);
            Version++;
            return StorageResult.Ok;
        }

        /// <summary>仓库里两格互换(见 <see cref="StorageVault.Swap"/>)。</summary>
        public StorageResult Swap(StorageKind kind, string heroKey, int slotA, int slotB)
        {
            StorageVault vault = Resolve(kind, heroKey, out StorageResult resolved);
            if (resolved != StorageResult.Ok)
            {
                return resolved;
            }
            StorageResult result = vault.Swap(slotA, slotB);
            if (result == StorageResult.Ok)
            {
                Version++;
            }
            return result;
        }

        private StorageVault Resolve(StorageKind kind, string heroKey, out StorageResult result)
        {
            result = StorageResult.Ok;
            if (kind == StorageKind.Account)
            {
                if (!HasAccountVault)
                {
                    result = StorageResult.NoAccountVault;
                    return null;
                }
                return _account;
            }
            return PersonalVault(heroKey);
        }

        private static void RequireHeroState(Inventory bag, IStoragePurse purse)
        {
            if (bag == null)
            {
                throw new ArgumentNullException(nameof(bag));
            }
            if (purse == null)
            {
                throw new ArgumentNullException(nameof(purse));
            }
        }

        // ---------------- 读档

        /// <summary>
        /// 读档用:直接把账号金库设成 <paramref name="tierCapacity"/>(档位表里的键)与 <paramref name="gold"/>,不收费用、不查档位表(数据换了也不丢存档里的东西)。
        /// <paramref name="tierCapacity"/> 不大于 0 表示没开通(金币也清零)。金币为负按 0。格子里的东西由调用方再逐格 <see cref="StorageVault.RestoreSlot"/>。
        /// </summary>
        public void RestoreAccount(int tierCapacity, long gold)
        {
            _accountTier = tierCapacity > 0 ? tierCapacity : 0;
            _accountGold = _accountTier > 0 && gold > 0 ? gold : 0;
            _account.GrowTo(_accountTier);
        }

        /// <summary>读档用:取一个角色的个人仓库,把容量设成 <paramref name="capacity"/>(只会变大;初始 8 格已经有了),返回它让调用方逐格放回物品。</summary>
        public StorageVault RestorePersonal(string heroKey, int capacity)
        {
            StorageVault vault = PersonalVault(heroKey);
            vault.GrowTo(capacity);
            return vault;
        }
    }
}
