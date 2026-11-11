using System;

// 证据:个人仓库——容量 8 起:CUser::SetInventory 0x0864d160 与 CUser::setTagCharacInven 0x08690226 在读出来的容量为 0 时 SetCapacity(8);
// 每次扩 16:Dispatcher_UpgradeCargo::dispatch_sig 0x0822a0a4(新容量 = 现有容量 + 0x10,先问 CCargo::isUpgradable 0x0850c698,通过才扣一个道具再 CCargo::UpgradeCargo 0x0850c656);
// isUpgradable 要求现有容量不为 0 且新容量小于 0x99(最多 152),CCargo::IsOperatorable 0x0850d39c 也只认下标不大于 0x97 的格子。
// 账号金库——最多 56 格:CAccountCargo::CheckValidSlot 0x0828a554 与 GetSpecificItemSlot 0x0828a61a 都把格号压在 0x38 以内。
namespace Dnf.Game
{
    /// <summary>仓库的几个固定数值与说明文字。档位表里的数(容量、价钱、金币上限)是原版数据,不在这里。</summary>
    public static class StorageRules
    {
        /// <summary>个人仓库刚开始的容量(服务端 SetCapacity(8))。</summary>
        public const int PersonalInitialCapacity = 8;

        /// <summary>个人仓库用一个扩容道具扩多少格(服务端 0x10)。</summary>
        public const int PersonalExpandStep = 16;

        /// <summary>个人仓库最多多少格(服务端格号上限 0x97,即 152 格)。</summary>
        public const int PersonalMaxSlots = 152;

        /// <summary>账号金库最多多少格(服务端格号上限 0x37,即 56 格;档位表里写得更大也只开放到这里)。</summary>
        public const int AccountMaxSlots = 56;

        /// <summary>这一种仓库最多开放多少格。</summary>
        public static int MaxSlotsOf(StorageKind kind)
        {
            return kind == StorageKind.Account ? AccountMaxSlots : PersonalMaxSlots;
        }

        /// <summary>
        /// 个人仓库现在的容量还能不能再扩一次(服务端 <c>isUpgradable(现有容量 + 16)</c>):现有容量不为 0、扩完不超过 <see cref="PersonalMaxSlots"/>。
        /// 8 起每次 16,第 9 次扩到 152,之后就不能再扩。
        /// </summary>
        public static bool CanExpandPersonal(int capacity)
        {
            return capacity > 0 && capacity + PersonalExpandStep <= PersonalMaxSlots;
        }

        /// <summary>结果的中文说明(给界面弹提示用)。</summary>
        public static string MessageOf(StorageResult result)
        {
            switch (result)
            {
                case StorageResult.Ok:
                    return "完成。";
                case StorageResult.NoItem:
                    return "那一格里没有东西。";
                case StorageResult.BadSlot:
                    return "那个仓库格子还没有开放。";
                case StorageResult.BadQuantity:
                    return "数量不对。";
                case StorageResult.NotAllowed:
                    return "这件物品不能存进这个仓库。";
                case StorageResult.VaultFull:
                    return "仓库放不下了。";
                case StorageResult.BagFull:
                    return "背包放不下了。";
                case StorageResult.NoAccountVault:
                    return "还没有开通账号金库。";
                case StorageResult.AccountVaultExists:
                    return "账号金库已经开通了。";
                case StorageResult.AtMaxCapacity:
                    return "已经扩到最大了。";
                case StorageResult.LevelTooLow:
                    return "等级不够,不能开通账号金库。";
                case StorageResult.NoMaterial:
                    return "所需的材料不够。";
                case StorageResult.NoGold:
                    return "金币不够。";
                case StorageResult.OverVaultGoldLimit:
                    return "存进去会超过账号金库能放的金币上限。";
                case StorageResult.OverHeroGoldLimit:
                    return "取出来会超过角色能携带的金币上限。";
                case StorageResult.BadAmount:
                    return "金额不对。";
                case StorageResult.NoData:
                    return "没有读到需要的数据,这件事现在做不了。";
                case StorageResult.NotExpansionItem:
                    return "这件物品不能用来扩充仓库。";
                default:
                    throw new ArgumentOutOfRangeException(nameof(result));
            }
        }
    }
}
