using System;

// 证据:本类只是把几处数据与开关收在一起,各自的来源见 AccountCargoScript(账号金库脚本)、GoldLimitTable(每级金币上限)、StorageItemScript(物品脚本里的三个标签)、
// StorageEligibility(资格规则;其中"某一段编号的物品不收"在服务端是写死的一段编号区间,那是原版的物品编号,不进仓库,由宿主从本机填进来)。
namespace Dnf.Game
{
    /// <summary>
    /// 仓库用到的数据与开关。每一项都可以不给:没给账号金库脚本就建不了也升不了账号金库,没给每级上限就不查取钱的携带上限,
    /// 没给物品事实就按"什么都没写"算(此时账号金库一件物品都不收,见 <see cref="RequirePackagable"/>)。
    /// </summary>
    public sealed class StorageSettings
    {
        /// <summary>账号金库脚本(档位表);null 表示没读到。</summary>
        public AccountCargoScript AccountScript { get; set; }

        /// <summary>每级金币携带上限;null 表示不查(取钱不受限)。</summary>
        public GoldLimitTable GoldLimits { get; set; }

        /// <summary>
        /// 两种仓库都不收的一段物品编号(含两端)。服务端在存入时写死了一段编号区间当"不能存";那段编号是原版的物品号,所以不写在代码里。
        /// 默认是空区间(<see cref="ExcludedCodeFrom"/> 大于 <see cref="ExcludedCodeTo"/>)。
        /// </summary>
        public int ExcludedCodeFrom { get; set; } = 1;

        /// <summary>不收的物品编号区间的上端(含)。</summary>
        public int ExcludedCodeTo { get; set; } = 0;

        /// <summary>
        /// 账号金库只收脚本里写了 <c>[packagable]</c> 的物品(服务端 CItem::isPackagable)。默认为真,照服务端。
        /// 本机这份原版数据里没有一件物品带这个标签,所以照服务端的话账号金库实际只能放金币;想让物品格也能用就得显式关掉它(这是本项目的选择,不是服务端的行为)。
        /// </summary>
        public bool RequirePackagable { get; set; } = true;

        /// <summary>按物品取脚本里的事实(<see cref="StorageItemFacts"/>);null 表示都按"什么都没写"。</summary>
        public Func<ItemInfo, StorageItemFacts> Facts { get; set; }

        /// <summary>编号是否落在不收的区间里。</summary>
        public bool IsExcludedCode(int code)
        {
            return ExcludedCodeFrom <= code && code <= ExcludedCodeTo;
        }

        /// <summary>这件物品的事实;物品为空或没有给 <see cref="Facts"/> 时是 <see cref="StorageItemFacts.None"/>。</summary>
        public StorageItemFacts FactsOf(ItemInfo info)
        {
            return info == null || Facts == null ? StorageItemFacts.None : Facts(info);
        }

        /// <summary>
        /// 从数据里装配:账号金库脚本、每级金币上限(读不到就是 null),物品事实按需读物品脚本(<see cref="GameData.ReadScript"/> 自己有缓存)。
        /// 不收的编号区间不在数据里,装配之后由宿主按本机的情况填 <see cref="ExcludedCodeFrom"/> 与 <see cref="ExcludedCodeTo"/>。
        /// </summary>
        public static StorageSettings FromData(GameData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            return new StorageSettings
            {
                AccountScript = AccountCargoScript.Read(data),
                GoldLimits = GoldLimitTable.Read(data),
                Facts = info => StorageItemScript.Read(data, info),
            };
        }
    }
}
