using System;

// 证据:E-133。[TELEPORT] 的落点检查(客户端执行块 0x010b71a4 起,静态读码,非 REA 自动化):先问"这个点在地图里、能不能站"(虚槽 0xe0、0xdc),
// 都行就直接摆(虚槽 0x98);不行就用虚槽 0xe4 在半径 200 像素内找一个最近的合法点(实参 200、-1、3)再问一遍;还不行就不动。
// 虚槽 0xe4 的搜索细节(步长、方向偏好)没有读到:这里按 10 像素一格取最近的(推断)。
namespace Dnf.Play
{
    /// <summary>瞬移的落点解析:目标点能站就用它,否则找附近最近的能站的点。</summary>
    public static class Placement
    {
        /// <summary>找落点的半径(像素),客户端调用时给的 200。</summary>
        public const int SearchRadius = 200;

        /// <summary>搜索的格距(像素)。推断值。</summary>
        public const int SearchStep = 10;

        /// <summary>
        /// 解析落点:<paramref name="canStand"/> 对 (x, y) 为真就原样返回;否则在 <see cref="SearchRadius"/> 之内、按 <see cref="SearchStep"/> 的格子找离 (x, y) 最近的能站的点
        /// (同样近时先找到的赢,自上而下、自左而右)。一个也没有返回 false。
        /// </summary>
        public static bool TryResolve(Func<double, double, bool> canStand, double x, double y, out double resolvedX, out double resolvedY)
        {
            if (canStand == null)
            {
                throw new ArgumentNullException(nameof(canStand));
            }
            resolvedX = x;
            resolvedY = y;
            if (canStand(x, y))
            {
                return true;
            }
            long best = long.MaxValue;
            bool found = false;
            long limit = (long)SearchRadius * SearchRadius;
            for (int dy = -SearchRadius; dy <= SearchRadius; dy += SearchStep)
            {
                for (int dx = -SearchRadius; dx <= SearchRadius; dx += SearchStep)
                {
                    long d2 = (long)dx * dx + (long)dy * dy;
                    if (d2 > limit || d2 >= best)
                    {
                        continue;
                    }
                    if (canStand(x + dx, y + dy))
                    {
                        best = d2;
                        resolvedX = x + dx;
                        resolvedY = y + dy;
                        found = true;
                    }
                }
            }
            return found;
        }
    }
}
