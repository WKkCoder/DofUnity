// 服务端静态读码:副职业与分解相关的操作失败时抛出的 ENUM_ERROR 编号(下面每个名字后面括号里写的是服务端编号与出处)。
// 名字是这一线按用法起的;同一个编号在不同场合含义不同的,在注释里都列出。
namespace Dnf.Game
{
    /// <summary>副职业、分解与分解机操作的结果。<c>(int)</c> 转出来就是服务端的错误号;<see cref="Ok"/> 为 0。</summary>
    public enum ExpertJobError
    {
        /// <summary>成功。</summary>
        Ok = 0,

        /// <summary>
        /// 服务端错误号 1:放弃副职业的次数已用完(<c>OnGiveup</c> 0x0849e60c:已放弃次数不小于 [giveup cost] 项数);
        /// 便携分解器:序号没有在 [portable disjoint info] 里登记(<c>_check_disjointable_item_state</c> 0x081f8ba1)。
        /// </summary>
        LimitReached = 1,

        /// <summary>错误号 3:没有角色(<c>Dispatcher_GiveupExpertJob</c> 路径)。</summary>
        NoCharacter = 3,

        /// <summary>错误号 4:背包放不下分解产物(<c>_check_disjoint_need_slot</c> 0x081f8fe3 与逐件试放)。</summary>
        NoSpace = 4,

        /// <summary>错误号 8:放弃时没有副职业、没有脚本,或 [clear quests] 里还有没完成的任务。</summary>
        NotAvailable = 8,

        /// <summary>错误号 10:机主的金币装不下这次的收费(<c>CUser::CheckMoney</c> 0x0866af1c;摆摊收费的路径,这一版不走)。</summary>
        GoldLimit = 10,

        /// <summary>错误号 0xd:便携分解器的可分解等级不够(<c>_check_disjointable_item_state</c>)。</summary>
        LevelTooHigh = 0x0d,

        /// <summary>错误号 0xe:角色等级没到机器升档要求(<c>OnUpgradeDisjointMachine</c> 0x085d37e8)。</summary>
        LevelTooLow = 0x0e,

        /// <summary>错误号 0x11:这件物品不能分解(宠物、不是装备类、传入的物品不存在、带 [trade delete]、分解结果表里没有这一行)。</summary>
        InvalidItem = 0x11,

        /// <summary>错误号 0x13:不允许(物品脚本 [impossible contents] 有 "disjoint";机主不是分解师;机器升档已到顶或经验等级不够)。</summary>
        NotPermitted = 0x13,

        /// <summary>错误号 0x15:金币不够(放弃费用;没有便携分解器物品时也是这个号)。</summary>
        NotEnoughGold = 0x15,

        /// <summary>错误号 0x16:这次操作不成立(角色等级没到就职要求;升档费用为 0 或金币不够;修理修不出耐久;便携分解器扣不掉)。</summary>
        Refused = 0x16,

        /// <summary>错误号 0xae:物品还没鉴定,而这次没有分解机(<c>_checkDisjointableItem</c> 0x081fa0f6)。</summary>
        Unidentified = 0xae,

        /// <summary>错误号 0xbd:分解机耐久用完(<c>OnStartMission</c> 0x085d2a16 → <c>check_disjoint_endurance</c> 0x085d1f24)。</summary>
        MachineBroken = 0xbd,

        /// <summary>错误号 0xd3:未鉴定的物品在分解结果表里没有对应行。</summary>
        UnidentifiedNoResult = 0xd3,

        /// <summary>错误号 0xd5:物品被锁(<c>CUser::CheckItemLock</c>;锁由宿主决定,这一线只留出口)。</summary>
        ItemLocked = 0xd5,

        /// <summary>
        /// <b>不是服务端的错误号</b>(这一线自己加的):分解算出的产物在物品数据里找不到(数据缺失),整次分解不做。
        /// 服务端的物品都在内存表里,没有这种情况。
        /// </summary>
        UnknownProduct = 1000,
    }
}
