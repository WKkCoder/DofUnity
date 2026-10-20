using System;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 城镇里走向出口的方向决定(自动机用,证据 E-095):沿出口"往外"的那根轴一直推(这样站进矩形、被区域边缘挡住时仍在推,触发出口),
    /// 另一根轴对准出口矩形的中心(死区 <see cref="AlignTolerance"/> 像素,免得在中心附近来回抖)。
    /// 贴着可走区域的墙走时,每根轴单独检测碰撞,所以一根轴被挡住另一根仍然会走,绕过门洞一类的凹口不需要寻路。
    /// </summary>
    public static class TownBot
    {
        public const double AlignTolerance = 4.0;

        public static void Direction(double x, double y, WorldRect exit, int outwardX, int outwardY, out int dirX, out int dirY)
        {
            double cx = exit.X + exit.Width * 0.5;
            double cy = exit.Y + exit.Height * 0.5;
            if (outwardX != 0)
            {
                dirX = outwardX;
                dirY = Align(cy - y);
            }
            else if (outwardY != 0)
            {
                dirX = Align(cx - x);
                dirY = outwardY;
            }
            else
            {
                dirX = Align(cx - x);
                dirY = Align(cy - y);
            }
        }

        private static int Align(double delta)
        {
            return delta > AlignTolerance ? 1 : delta < -AlignTolerance ? -1 : 0;
        }
    }
}
