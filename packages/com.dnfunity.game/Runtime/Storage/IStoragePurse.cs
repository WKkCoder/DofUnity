// 证据:服务端存取金币与开通、升级账号金库时对角色身上金币的操作——CInventory::use_money(扣钱,不够返回失败)、CInventory::gain_money(加钱)、
// CUserCharacInfo::getCurCharacMoney(读现有金币)。本接口只是把"读、扣、加"三件事抽出来,让仓库逻辑不依赖具体的主角类(主角类由 Play 层接上)。
namespace Dnf.Game
{
    /// <summary>角色身上的钱包:仓库存取金币、交开通与升级的价钱时通过它读、扣、加。</summary>
    public interface IStoragePurse
    {
        /// <summary>现有金币。</summary>
        long Gold { get; }

        /// <summary>扣掉 <paramref name="amount"/>;不够或金额不大于 0 返回 false、什么也不动。</summary>
        bool TrySpend(long amount);

        /// <summary>加上 <paramref name="amount"/>(调用方保证不为负)。</summary>
        void Receive(long amount);
    }
}
