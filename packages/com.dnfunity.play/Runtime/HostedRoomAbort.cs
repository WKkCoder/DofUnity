namespace Dnf.Play
{
    /// <summary>
    /// 联网多房间副本中途放弃(<see cref="HostedRoomRun.Abort"/>)的原因(r18-hostmulti)。放弃 = 这一局当作失败:<see cref="HostedRoomRun.Failed"/> 变真,
    /// 流程(<see cref="GameFlow"/>)据此回城镇,与 <c>-dnfnetdungeon</c> 进副本失败时同一条路;原因只有状态名,日志与笔记里不带任何编号。
    /// </summary>
    public enum HostedAbort
    {
        /// <summary>没有放弃。</summary>
        None = 0,

        /// <summary>服务端拒绝换房的次数用完了(联网层每个房间的额度,缺省 3 次)。</summary>
        MoveRefused = 1,

        /// <summary>换房命令发出(或排上队)之后,等不到新房间的起始地图通知:超过宿主的等待上限,或联网层的握手超时把战斗收了尾。</summary>
        MoveTimeout = 2,

        /// <summary>本地怪杀光了、换房请求也排了,但服务端的怪表一直没被确认清空(击杀被拒、或这一间房里有本地杀不死的怪):换房被联网层的"当前房间没清"闸门一直拦着。</summary>
        RoomNotCleared = 3,

        /// <summary>新房间造出来了,但里面本地怪的数目与服务端新通知里的怪表对不上(缺了几只):清图永远等不到,不能往下打。</summary>
        RoomMismatch = 4,

        /// <summary>新房间造不出来:房间工厂没造出房间、抛了异常,或房间地图里没有通向下一间的出口位置。</summary>
        RoomBuild = 5,

        /// <summary>本地迷宫数据与服务端的房间序列对不上:新通知的格子不是计划的目标格、或本地没有任何迷宫能解释服务端给的格子与出口。</summary>
        CellMismatch = 6,

        /// <summary>联网战斗在没有结算的情况下结束了(对端断开、帧层出错、整体超时等),本地怪的击杀再也交不出去。</summary>
        NetEnded = 7,

        /// <summary>进了新房间之后,装载完成命令发出了却等不到装载完成通知(联网层握手超时)。</summary>
        LoadTimeout = 8,
    }
}
