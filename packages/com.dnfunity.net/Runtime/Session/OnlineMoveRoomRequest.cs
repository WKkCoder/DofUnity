using System;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 一次换房请求:要走到迷宫里的哪一格(换房命令的目标格),以及随命令一起交给服务端的本地角色位置与"分层图"标志。
    /// 不可变;构造时就校验目标格的范围,所以手里有这个对象就说明它拼得出合法的换房载荷(<see cref="OnlineCombatPayloads.BuildMoveRoom"/>)。
    /// <para>
    /// 目标格是迷宫格坐标(与起始地图通知里的 <see cref="StartMapNotice.CellX"/>、<see cref="StartMapNotice.CellY"/> 同一套),不是房间里的像素坐标。
    /// 服务端只放行"当前格上下左右相邻的一格"(<c>CBattle_Field::check_movemap_range 0x8307b2a</c>:斜向与更远的格回错误码 21;已经在目标格回 18),
    /// 当前格取自上一条起始地图通知的 <see cref="StartMapNotice.CellX"/>/<see cref="StartMapNotice.CellY"/>(服务端那条通知就是按它的当前格写的,静态读码)。
    /// 这里不替调用方预判相邻关系:服务端回绝是无害的错误回包(见 <see cref="OnlineDungeonCombat.MoveRoomReplyPacketId"/>),
    /// 状态机留在当前房间,只是会消耗该房间的拒绝额度(<see cref="OnlineDungeonCombatOptions.MaxMoveRefusalsPerRoom"/>)。
    /// </para>
    /// <para>
    /// 一个必须知道的雷:目标格越过当前迷宫的宽或高(X 不小于宽、Y 不小于高)时,服务端的 <c>DisPatcher_MoveMap::check_error 0x81c4a90</c> 返回 -5,
    /// <c>process 0x81c5330</c> 经 <c>LineFunc</c> 把它变成正数返回值,分发器据此断线(静态读码)。迷宫宽高不在这一层手里,所以这里只拦住"绝对不可能合法"的范围
    /// (<see cref="OnlineCombatPayloads.MaxMoveRoomCell"/>),调用方知道迷宫尺寸时应自己再收紧。
    /// </para>
    /// <para>
    /// 位置两项(<see cref="PositionX"/>、<see cref="PositionY"/>)缺省 0、0,但多次换房时<b>应当传真实位置</b>:服务端的
    /// <c>Secu_HackLogCheckByParty::CheckMoveMapFitInPathHackCnt 0x827a96e</c> 排在 <c>CBattle_Field::MoveMap</c> 之前(被拒的换房也会走到),
    /// 每个队伍的第一次换房豁免,第二次起单人或自动组队的队伍要求这个位置落在当前地图的过门路径对象上,对不上记一次反作弊计数(0x1f7;只计数,不拒绝、不断线,
    /// 计数过阈值后的处置没读)。所以缺省值只在第一次换房时可以当作"无害",此后的每次换房都建议由宿主填角色在过门处的真实位置(静态读码,没有真服验证)。
    /// </para>
    /// </summary>
    public sealed class OnlineMoveRoomRequest
    {
        /// <param name="targetX">目标格 X,0 到 <see cref="OnlineCombatPayloads.MaxMoveRoomCell"/>。</param>
        /// <param name="targetY">目标格 Y,0 到 <see cref="OnlineCombatPayloads.MaxMoveRoomCell"/>。</param>
        /// <param name="positionX">本地角色此刻的位置 X(i32);缺省 0,第二次换房起建议传真实值(见类说明)。</param>
        /// <param name="positionY">本地角色此刻的位置 Y(i32);缺省 0,第二次换房起建议传真实值(见类说明)。</param>
        /// <param name="layered">是否按"分层图"处理(线上 1 个字节,为 1 时服务端多走一步 <c>checkLayeredMap</c>);缺省 false。</param>
        /// <exception cref="ArgumentOutOfRangeException">目标格不在 [0, <see cref="OnlineCombatPayloads.MaxMoveRoomCell"/>]。</exception>
        public OnlineMoveRoomRequest(int targetX, int targetY, int positionX = 0, int positionY = 0, bool layered = false)
        {
            if (targetX < 0 || targetX > OnlineCombatPayloads.MaxMoveRoomCell)
            {
                throw new ArgumentOutOfRangeException(nameof(targetX), "目标格 X 必须在 0.." + OnlineCombatPayloads.MaxMoveRoomCell);
            }
            if (targetY < 0 || targetY > OnlineCombatPayloads.MaxMoveRoomCell)
            {
                throw new ArgumentOutOfRangeException(nameof(targetY), "目标格 Y 必须在 0.." + OnlineCombatPayloads.MaxMoveRoomCell);
            }
            TargetX = targetX;
            TargetY = targetY;
            PositionX = positionX;
            PositionY = positionY;
            Layered = layered;
        }

        /// <summary>目标格 X(线上第 0 个字节)。</summary>
        public int TargetX { get; }

        /// <summary>目标格 Y(线上第 1 个字节)。</summary>
        public int TargetY { get; }

        /// <summary>本地角色位置 X(线上 i32)。</summary>
        public int PositionX { get; }

        /// <summary>本地角色位置 Y(线上 i32)。</summary>
        public int PositionY { get; }

        /// <summary>分层图标志(线上 1 个字节,1 = 真)。</summary>
        public bool Layered { get; }
    }
}
