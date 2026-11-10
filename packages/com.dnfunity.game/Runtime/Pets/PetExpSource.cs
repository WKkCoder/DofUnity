// 服务端静态读码:CCreatureMgr::GainExp 0x08339b06 的调用方——副本通关 CParty::prepare_dungeon_clear 0x085aa39a、
// 战争房间击杀 WarRoom::HandleMonsterKill 0x086be1fe、物品喂养 CCreatureMgr::UseItem 0x0833a5bc、调试命令。
// 前两处都是把"用户对象上累计的一个整数"原样交给宠物管理器,并且只在装备的不是选择成长型宠物时才调用。
namespace Dnf.Game
{
    /// <summary>
    /// 宠物经验的来源。服务端的做法:经验量不是宠物自己算的,而是别处(通关、击杀)累加到用户身上的一个整数,到点交给宠物的加经验函数。
    /// 这个接口只表达"这一次要给宠物多少基础经验(还没算神器加成)";怎么累加、什么时候清零由实现的一方决定。
    /// <para>
    /// 物品喂养(选择成长型的另一条经验路径)这一版没有实现,所以接口里没有它。
    /// </para>
    /// </summary>
    public interface IPetExpSource
    {
        /// <summary>这一次要结算给宠物的基础经验量;不大于 0 时宠物那边拒绝(<see cref="PetGainOutcome.InvalidAmount"/>)。</summary>
        int PetExpAmount { get; }
    }
}
