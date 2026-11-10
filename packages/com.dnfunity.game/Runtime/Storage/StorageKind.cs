// 证据:服务端有两种仓库——个人仓库 CCargo(按角色存,fixed 数组 152 格、用 CUserCharacInfo::getCurCharacCargo 取)与账号金库 CAccountCargo(按账号存,56 格、带金币)。
// 两者在 CUser::exchange_interspace 0x0865f7b0 里是不同的"空间":个人仓库是 2、账号金库是 0x0c。
namespace Dnf.Game
{
    /// <summary>仓库的两种。</summary>
    public enum StorageKind
    {
        /// <summary>个人仓库:每个角色一个,格子 8 起、每次扩 16,最多 152,不放金币。</summary>
        Personal = 0,

        /// <summary>账号金库:整个账号一个,先要建立;容量与金币上限按档位走,最多 56 格,可以放金币。</summary>
        Account = 1,
    }
}
