using System;
using System.Collections.Generic;

namespace Dnf.Render
{
    /// <summary>
    /// 客户端的线性同余随机数(证据 E-074,DNF.exe 0x00E50E60 里"种子取指针"的那一支):种子 s 连推三步 s ← s × 0x41C64E6D + 0x3039(32 位回绕),
    /// 取三步结果的高 16 位拼成 31 位非负整数 ((((s1 &gt;&gt; 16) &amp; 0x7FF) &lt;&lt; 10 ^ ((s2 &gt;&gt; 16) &amp; 0x3FF)) &lt;&lt; 10) ^ ((s3 &gt;&gt; 16) &amp; 0x3FF),
    /// 种子留在第三步的结果上。原版种子指针为空时用 timeGetTime() 累加出的全局种子,这里没有对应物,不实现。
    /// 对拍:模拟器里直接运行客户端这个函数得到的标准答案(12 个种子 × 4 次,<c>GateArrivalTests</c>)。
    /// </summary>
    public static class ClientRandom
    {
        public const uint Multiplier = 0x41C64E6Du;
        public const uint Increment = 0x3039u;

        /// <summary>推进 <paramref name="seed"/> 并返回下一个 31 位非负随机数。</summary>
        public static int Next(ref uint seed)
        {
            uint s1 = unchecked(seed * Multiplier + Increment);
            uint s2 = unchecked(s1 * Multiplier + Increment);
            uint s3 = unchecked(s2 * Multiplier + Increment);
            seed = s3;
            uint a = (s1 >> 16) & 0x7FFu;
            uint b = (s2 >> 16) & 0x3FFu;
            uint c = (s3 >> 16) & 0x3FFu;
            return (int)((((a << 10) ^ b) << 10) ^ c);
        }
    }

    /// <summary>
    /// 出口的识别矩形(证据 E-074):客户端在建舞台时按地图的 [pathgate pos](四个出口的 (x, y))与 [pathgate recognize range](四个出口的 (宽, 高))算出,
    /// 服务端用同一组矩形校验主角过门时报上来的位置;进房间时的落点也落在"进来的那个出口"的矩形里。半开区间 [x, x + 宽) × [y, y + 高)。
    /// 左、右出口:宽高缺省 120 × 106,左矩形的左边贴着出口 x、右矩形的右边贴着出口 x;上、下出口:宽高缺省 140 × 40,以出口为中心;
    /// 纵向都以出口 y 为中心。
    /// </summary>
    public static class GateAreas
    {
        public const int SideDefaultWidth = 120;
        public const int SideDefaultHeight = 106;
        public const int VerticalDefaultWidth = 140;
        public const int VerticalDefaultHeight = 40;

        /// <summary>
        /// 四个出口的矩形(按 <see cref="GateSide"/> 排)。<paramref name="pos"/> 每项是 (x, y) 或 null(脚本没写这个出口:矩形为 null);
        /// <paramref name="range"/> 每项是 (宽, 高) 或 null,不大于 0 的分量取缺省值。
        /// </summary>
        public static WorldRect?[] Compute(IReadOnlyList<int[]> pos, IReadOnlyList<int[]> range)
        {
            var result = new WorldRect?[4];
            if (pos == null)
            {
                return result;
            }
            for (int side = 0; side < 4 && side < pos.Count; side++)
            {
                int[] p = pos[side];
                if (p == null || p.Length < 2)
                {
                    continue;
                }
                int[] r = range != null && side < range.Count ? range[side] : null;
                result[side] = For((GateSide)side, p[0], p[1], r != null && r.Length >= 1 ? r[0] : 0, r != null && r.Length >= 2 ? r[1] : 0);
            }
            return result;
        }

        /// <summary>某一侧出口的矩形:出口位置 (<paramref name="x"/>, <paramref name="y"/>),<paramref name="rangeWidth"/>/<paramref name="rangeHeight"/> 是脚本给的识别范围(不大于 0 取缺省值)。</summary>
        public static WorldRect For(GateSide side, int x, int y, int rangeWidth = 0, int rangeHeight = 0)
        {
            bool sideGate = side == GateSide.Left || side == GateSide.Right;
            int w = rangeWidth > 0 ? rangeWidth : (sideGate ? SideDefaultWidth : VerticalDefaultWidth);
            int h = rangeHeight > 0 ? rangeHeight : (sideGate ? SideDefaultHeight : VerticalDefaultHeight);
            int left = sideGate ? (side == GateSide.Left ? x : x - w) : x - w / 2;
            return new WorldRect(left, y - h / 2, w, h);
        }

        /// <summary>半开区间:(x, y) 在矩形 [X, X + 宽) × [Y, Y + 高) 里。</summary>
        public static bool Contains(WorldRect r, int x, int y)
        {
            return x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height;
        }

        /// <summary>
        /// 占位盒(脚下原点 (x, y)、宽 <paramref name="bodyWidth"/>、深 <paramref name="bodyDepth"/>,左右上下各取一半,边界像素算在内)与矩形有没有重叠。
        /// 上下出口的触发用它:矩形常常比可走带的边缘高出几像素到几十像素(全库一成左右的下出口如此),主角的脚点够不着,盒子压到就算在里面。
        /// </summary>
        public static bool OverlapsBox(WorldRect r, int x, int y, int bodyWidth, int bodyDepth)
        {
            return x + (bodyWidth >> 1) >= r.X && x - (bodyWidth >> 1) < r.X + r.Width
                && y + (bodyDepth >> 1) >= r.Y && y - (bodyDepth >> 1) < r.Y + r.Height;
        }
    }

    /// <summary>
    /// 进房间时主角的落点(证据 E-074,对应客户端 0x007D6880 与 0x007FA3F0):
    ///   1. 在"进来的那个出口"的识别矩形里取一个随机点,主角的占位盒(宽 w、深 d)要整个落在矩形里:
    ///      x = 矩形.x + w / 2 + (矩形.宽 − w + 1 &gt; 0 ? 随机数 % (矩形.宽 − w + 1) : 0),y 同理(先取 x 再取 y;差值不为正时不取随机数);
    ///   2. 这个点上站不下(<c>isFree</c> 为假)就由近到远一圈圈找(<see cref="NearestFree"/>)。
    /// 随机数用的是 <see cref="ClientRandom"/>,种子是"主角对象的编号 × 地图编号"这一个整数,而且客户端是把它放在调用函数的参数槽里推进的,
    /// 每次进房间都从同一个初值开始,所以同一个主角进同一个房间的落点是固定的;主角对象编号的来源没有查明(<see cref="DefaultHeroObjectId"/>)。
    /// </summary>
    public static class GateArrival
    {
        /// <summary>找最近可站点的最大半径与步长(客户端传的是 200 与 3,模式 -1)。</summary>
        public const int NearestFreeRadius = 200;

        public const int NearestFreeStep = 3;

        /// <summary>
        /// 主角对象的编号。原版是主角对象(0x48 号虚函数)的返回值,运行时由对象管理器分配,来源没有查明;单机复刻里取一个固定的值。
        /// 取的是 2^22 以内能同时满足原版基线四处量到的落点(s2 起点 (876, 342)、s3 左出口进来 x ≈ 89、s2 右出口进来 x ≈ 1034、b2 上出口进来 ≈ (944, 156),
        /// 容差各 ±6、±8、±4)的最小一个(证据 E-074:这一范围内共 4 个候选,个数与碰运气的期望相当,所以只是拟合出的一个点,不是还原的值)。
        /// </summary>
        public const int DefaultHeroObjectId = 1752953;

        /// <summary>落点的随机种子:主角对象编号 × 地图编号,32 位回绕。</summary>
        public static uint SeedFor(int heroObjectId, int mapId)
        {
            return unchecked((uint)(heroObjectId * mapId));
        }

        /// <summary>
        /// 在矩形 <paramref name="area"/> 里取一个随机点;<paramref name="bodyWidth"/>/<paramref name="bodyDepth"/> 是主角占位盒的宽与深(取自 .chr 的 [width])。
        /// <paramref name="seed"/> 被推进(每取一次随机数推进一次)。
        /// </summary>
        public static void RandomPoint(WorldRect area, int bodyWidth, int bodyDepth, ref uint seed, out int x, out int y)
        {
            int offX = 0, offY = 0;
            int spanX = area.Width - bodyWidth + 1;
            if (spanX > 0)
            {
                offX = ClientRandom.Next(ref seed) % spanX;
            }
            int spanY = area.Height - bodyDepth + 1;
            if (spanY > 0)
            {
                offY = ClientRandom.Next(ref seed) % spanY;
            }
            x = area.X + bodyWidth / 2 + offX;
            y = area.Y + bodyDepth / 2 + offY;
        }

        /// <summary>
        /// 找离 (<paramref name="x"/>, <paramref name="y"/>) 最近的可站点(客户端 0x007FA3F0 的模式 -1):先看原点;不行就按环半径 R = 步长、2 × 步长、……直到 <paramref name="maxRadius"/>,
        /// 每一环是以原点为中心、半边长 R 的正方形边框,由 t = 0、步长、……、R 依次检查八个点:
        /// (x + R, y + t)、(x + R, y − t)、(x − R, y + t)、(x − R, y − t),t &lt; R 时再检查 (x + t, y + R)、(x − t, y + R)、(x + t, y − R)、(x − t, y − R)
        /// (t = 0 时带负号的那几个与前一个重合,不重复检查)。第一个可站的点就是结果;步长大于半径时只看原点。找不到返回 false。
        /// </summary>
        public static bool NearestFree(Func<int, int, bool> isFree, int x, int y, int maxRadius, int step, out int outX, out int outY)
        {
            if (isFree == null)
            {
                throw new ArgumentNullException(nameof(isFree));
            }
            if (step <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(step));
            }
            if (isFree(x, y))
            {
                outX = x;
                outY = y;
                return true;
            }
            for (int radius = step; radius <= maxRadius; radius += step)
            {
                for (int t = 0; t <= radius; t += step)
                {
                    if (Probe(isFree, x + radius, y + t, out outX, out outY)
                        || (t != 0 && Probe(isFree, x + radius, y - t, out outX, out outY))
                        || Probe(isFree, x - radius, y + t, out outX, out outY)
                        || (t != 0 && Probe(isFree, x - radius, y - t, out outX, out outY)))
                    {
                        return true;
                    }
                    if (t == radius)
                    {
                        continue;
                    }
                    if (Probe(isFree, x + t, y + radius, out outX, out outY)
                        || (t != 0 && Probe(isFree, x - t, y + radius, out outX, out outY))
                        || Probe(isFree, x + t, y - radius, out outX, out outY)
                        || (t != 0 && Probe(isFree, x - t, y - radius, out outX, out outY)))
                    {
                        return true;
                    }
                }
            }
            outX = x;
            outY = y;
            return false;
        }

        private static bool Probe(Func<int, int, bool> isFree, int px, int py, out int outX, out int outY)
        {
            outX = px;
            outY = py;
            return isFree(px, py);
        }
    }
}
