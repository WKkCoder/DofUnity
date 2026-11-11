// 证据:服务端 CUser::exchange_interspace 0x0865f7b0 的错误出口(存入个人仓库 0x17 不能放、0x11 数量或背包不对、4 放不下;
// 存入账号金库 0x13 没建立、0x17 与 0x11 资格不符、4 放不下)、CAccountCargo::CreateAccountCargo 0x0828ad72(0x14 已有、0x0e 等级不够、0x13 没有档位数据、0x16 材料或金币不够)、
// UpgradeAccountCargo 0x0828b0b2(0x15 没有、0x13 已到最高档、0x16)、DepositMoney 0x0828a12a / WithdrawMoney 0x0828a2f6(10 金额不对或钱不够、0x5f 超金库上限、0x5e 超角色携带上限)、
// Dispatcher_UpgradeCargo 0x0822a0a4(扩个人仓库:0x11 没有道具、0x13 已到上限)。
namespace Dnf.Game
{
    /// <summary>一次仓库操作的结果(本项目自己的枚举;服务端对应的错误码见各项说明)。除 <see cref="Ok"/> 外,操作都没有改动任何东西。</summary>
    public enum StorageResult
    {
        Ok = 0,

        /// <summary>要动的那一格没有东西(背包格或仓库格是空的、物品读不到)。</summary>
        NoItem,

        /// <summary>仓库格号不在现在开放的格子里(服务端 IsOperatorable / CheckValidSlot 不通过)。</summary>
        BadSlot,

        /// <summary>件数不合法(不大于 0、超过这一格的件数、装备不是 1 件;一格放不下的新叠也算)。</summary>
        BadQuantity,

        /// <summary>这件东西不能存进这个仓库(服务端 0x17 / 账号金库的 CheckInsertCondition 不通过)。</summary>
        NotAllowed,

        /// <summary>仓库放不下(没有空格、或同编号那一叠再加就超过叠加上限;服务端 4)。</summary>
        VaultFull,

        /// <summary>背包放不下(服务端 tryInsertItemIntoInventory 返回 -1,0x11)。</summary>
        BagFull,

        /// <summary>账号金库还没建立(服务端 IsExistAccountCargo 为假)。</summary>
        NoAccountVault,

        /// <summary>账号金库已经建立,不能再建(服务端 0x14)。</summary>
        AccountVaultExists,

        /// <summary>已经是最高一档、不能再扩(账号金库 GetNextUpgradeInfo 为空 0x13;个人仓库 isUpgradable 为假 0x13)。</summary>
        AtMaxCapacity,

        /// <summary>角色等级不够建账号金库(服务端 0x0e)。</summary>
        LevelTooLow,

        /// <summary>材料不够(账号金库建立或升级所需的物品;服务端 0x16)。</summary>
        NoMaterial,

        /// <summary>金币不够(建立或升级的价钱、存入的金额、取出时金库里的钱;服务端 0x16 / 10)。</summary>
        NoGold,

        /// <summary>存进去之后超过这一档账号金库能放的金币上限(服务端 CheckMoneyLimit 不通过,0x5f)。</summary>
        OverVaultGoldLimit,

        /// <summary>取出之后角色身上的钱超过这个等级的携带上限(服务端 CUser::CheckMoney 不通过,0x5e)。</summary>
        OverHeroGoldLimit,

        /// <summary>金额是负数(服务端 10)。</summary>
        BadAmount,

        /// <summary>没有读到需要的数据表(账号金库脚本),这件事做不了。</summary>
        NoData,

        /// <summary>这件东西不是个人仓库的扩容道具(服务端 verifyStackableItemActionPlace 不通过)。</summary>
        NotExpansionItem,
    }
}
