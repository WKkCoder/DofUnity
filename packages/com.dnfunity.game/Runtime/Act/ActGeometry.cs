using System;

// 证据:客户端"两个对象之间的角度"函数 0x007fa2c0 与"距离"函数 0x007fb7c0 的静态读码(E-169,非 REA 自动化);
// 调用方是 [MOVE TO TARGET] 的执行块 0x010c744a(行为码 0xf6)。
namespace Dnf.Game.Act
{
    /// <summary>
    /// 客户端取"从一个对象指向另一个对象的角度与距离"的整数算法。位置取整数(客户端对象的坐标就是整数),
    /// 纵深(Y)在画面里被压扁:模式 0 先把纵深差还原再算,模式 1 不还原。
    /// 同一套算法被动对象追踪(<c>PassiveHoming.Heading</c>,模式 0 的角度)已经在用,这里补上模式 1 的角度与取整的距离。
    /// </summary>
    public static class ActGeometry
    {
        /// <summary>纵深压缩的分子(客户端全局 0x01a39e60,只读了文件里的初值 29)。</summary>
        public const int DepthScale = 29;

        /// <summary>
        /// 从 (fromX, fromY) 指向 (toX, toY) 的整度方向角 [0, 360)(客户端 0x007fa2c0):0 朝 +X、纵深增大(画面往下)是 90,世界坐标不翻转。
        /// <paramref name="restoreDepth"/> 为真是客户端的模式 0:纵深差先乘 100 除以 29;[MOVE TO TARGET] 用模式 1(不还原)。
        /// atan2 的结果先落到单精度,再乘 180/π(常量是负的,取整后再用 360 减,等价于向零截断的度数对 360 取余)。
        /// </summary>
        public static int Heading(int fromX, int fromY, int toX, int toY, bool restoreDepth)
        {
            float dx = toX - fromX;
            float dy = toY - fromY;
            if (restoreDepth)
            {
                dy = dy * 100f / DepthScale;
            }
            float radians = (float)Math.Atan2(dy, dx);
            int negated = (int)((double)radians * -57.29577951308232);
            return (360 - negated) % 360;
        }

        /// <summary>
        /// 朝一个整数度方向、按速度 <paramref name="speed"/> 走的速度向量(客户端 [MOVE TO TARGET] 块 0x010c744a 的写法):
        /// 角度先转单精度、乘 π/180 进双精度,余弦(CRT 0x013db30f)与正弦(CRT 0x013d848b)落回单精度再各乘速度(单精度),向零截断成整数。
        /// 余弦、正弦与客户端 CRT 在 0 到 359 度每一度上的单精度结果逐位一致(金标准 act_geometry_golden.txt)。
        /// </summary>
        public static void Velocity(int angleDegrees, float speed, out int vx, out int vy)
        {
            double radians = (double)(float)angleDegrees * 0.017453292519943295;
            vx = (int)((float)Math.Cos(radians) * speed);
            vy = (int)((float)Math.Sin(radians) * speed);
        }

        /// <summary>
        /// 两点的整数距离(客户端 0x007fb7c0):纵深差先除以比例(模式 0 是 29 × 0.01 的单精度积,模式 1 是 1)再截成整数,
        /// 与横向差各自平方相加,开方后向零截断。平方和是 32 位整数,差超过约 4.6 万像素会溢出变负,开方得 NaN,
        /// 客户端把它转整数得到 x86 的"不确定整数"0x80000000,这里照搬(游戏里碰不到,为的是与原版逐位一致)。
        /// </summary>
        public static int Distance(int fromX, int fromY, int toX, int toY, bool restoreDepth)
        {
            float ratio = restoreDepth ? (float)(DepthScale * (double)0.01f) : 1f;
            int dx = fromX - toX;
            int dy = (int)((float)(fromY - toY) / ratio);
            int squared = unchecked(dy * dy + dx * dx);
            double root = Math.Sqrt((double)squared);
            return double.IsNaN(root) ? int.MinValue : (int)root;
        }
    }
}
