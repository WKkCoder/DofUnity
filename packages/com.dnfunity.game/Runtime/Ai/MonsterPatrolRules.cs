namespace Dnf.Game
{
    /// <summary>
    /// 怪物巡逻模式(客户端旧式模式机的模式 27 与 28;r14 读码,见 docs/monster-ai.md 的模式机一节)的规则。全是纯函数,方向编码同 <see cref="MonsterMoveRules"/>。
    /// <para>
    /// 已读定的部分:模式更新里怎么进入巡逻(0x0088d645)、巡逻中有目标就转追击(0x0088d65d)、点巡逻的"到了"判断(0x008813b0)、
    /// 点巡逻的选方向(0x0089220f)、逐步判停(0x00897c66 与 0x00897c92)、节拍与停下概率(与游荡相同,见 <see cref="MonsterMoveRules.TempoOf"/>)。
    /// </para>
    /// <para>
    /// 没读定的部分(没有实现,也不要从这里推):巡逻类型与点坐标这几个字段(怪物对象 +0x3bf4、+0x3bf8、+0x3bfc)是谁写的——
    /// 普通代码段里整段文本扫描,对它们只有读取、比较与相减,没有写入指令,相邻的 +0x3be8、+0x3bec、+0x3bf0 也无引用;
    /// 写入者没有读到(没有读受保护区),字段含义只能从读取处推:类型 1 与 2、x、y。这些字段在地图或 .mob 里对应哪个条目,没有核对;
    /// 路线巡逻(模式 28)怎么走:路点表(+0x3c28)与备用表(+0x3c1c)的内容来源没读到,
    /// 到点判断(0x0088e3f0 与 0x0088e550)含当前 x 与路点 x、进入模式时记下的起点 x(+0x3c00)的比较,没有逐条读定,所以只有"进入与追击"两条规则。
    /// </para>
    /// </summary>
    public static class MonsterPatrolRules
    {
        /// <summary>巡逻类型字段 = 1:点巡逻(模式 27),朝字段里的一个点走,到了转站桩(15)。</summary>
        public const int TypePoint = 1;

        /// <summary>巡逻类型字段 = 2:路线巡逻(模式 28)。</summary>
        public const int TypeRoute = 2;

        /// <summary>
        /// 模式更新(0x0088d270 的 0x0088d645 块)里,当前模式是游荡(13)、站桩(15)或 17 时,按巡逻类型字段转巡逻:1 转 27,2 转 28,别的值(含没有)不转。
        /// 返回要转入的模式号;不转返回 0。客户端对这三个模式用同一个块(跳转表 0x0088d744 的同一项)。
        /// </summary>
        public static int ModeFor(int currentMode, int patrolType)
        {
            if (currentMode != (int)MonsterMoveMode.Wander && currentMode != (int)MonsterMoveMode.Idle && currentMode != 17)
            {
                return 0;
            }
            switch (patrolType)
            {
                case TypePoint:
                    return (int)MonsterMoveMode.PatrolPoint;
                case TypeRoute:
                    return (int)MonsterMoveMode.PatrolRoute;
                default:
                    return 0;
            }
        }

        /// <summary>模式更新里当前模式是 27 或 28(0x0088d65d 块):有目标就转追击(4),没有不动。</summary>
        public static bool TurnsToChase(int currentMode, bool hasTarget)
        {
            return hasTarget && (currentMode == (int)MonsterMoveMode.PatrolPoint || currentMode == (int)MonsterMoveMode.PatrolRoute);
        }

        /// <summary>
        /// 点巡逻"到了"(0x008813b0):巡逻类型是 1,而且整数坐标与目标点 x、y 都完全相等(没有容差)。别的类型一律不算到。
        /// </summary>
        public static bool Arrived(int patrolType, int x, int y, int pointX, int pointY)
        {
            return patrolType == TypePoint && x == pointX && y == pointY;
        }

        /// <summary>
        /// 点巡逻选方向(模式 27,0x0089220f):<c>横向 = x - 点x ≤ 0 取右、否则左;纵向 = y - 点y ≤ 0 取下、否则上</c>。
        /// 没有"不动":一条轴与点重合时(差为 0)客户端也给出方向(右、下),靠逐步判停(<see cref="StopsOnAxis"/>)把这条轴停住。
        /// 先问 <see cref="Arrived"/>,到了就转站桩(15),不选方向。
        /// </summary>
        public static void PointDirections(int x, int y, int pointX, int pointY, out int dirX, out int dirY)
        {
            dirX = x - pointX <= 0 ? MonsterMoveRules.Right : MonsterMoveRules.Left;
            dirY = y - pointY <= 0 ? MonsterMoveRules.Down : MonsterMoveRules.Up;
        }

        /// <summary>
        /// 模式 27 逐步判停(怪物基类的每步更新,槽 +0x1f0 里 0x00897c66 与 0x00897c92):没有目标时,某条轴的整数坐标与目标点相等,
        /// 客户端就调槽 +0x5f0(0x00832cb0),第一个参数是轴号(0 横向、1 纵向),其余五个参数全是 0——它把这条轴的逐轴记录写成这些值再重发方向,
        /// 这里按"让那条轴停住"理解(推断:没有逐项读它写的每个字段)。两条轴都相等、且类型是 1,另外转站桩(15,见 <see cref="Arrived"/>)。
        /// </summary>
        public static bool StopsOnAxis(int position, int point)
        {
            return position == point;
        }
    }
}
