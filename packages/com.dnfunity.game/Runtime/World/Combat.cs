using System;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>世界坐标里的轴对齐盒子:x、y、z 三个方向上的 [min, max]。</summary>
    public readonly struct Aabb
    {
        public readonly int MinX;
        public readonly int MaxX;
        public readonly int MinY;
        public readonly int MaxY;
        public readonly int MinZ;
        public readonly int MaxZ;

        public Aabb(int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
        {
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
            MinZ = minZ;
            MaxZ = maxZ;
        }

        public bool Overlaps(Aabb o)
        {
            return MinX < o.MaxX && o.MinX < MaxX && MinY < o.MaxY && o.MinY < MaxY && MinZ < o.MaxZ && o.MinZ < MaxZ;
        }

        /// <summary>
        /// 两个盒子的相交盒(客户端的"命中信息",证据 E-058):各轴下界取较大的、上界取较小的。
        /// 只在两个盒子相交时有意义(调用方先用 <see cref="Overlaps"/> 判);不相交时某些轴的下界会大于上界。
        /// </summary>
        public static Aabb Intersection(Aabb a, Aabb b)
        {
            return new Aabb(
                Math.Max(a.MinX, b.MinX), Math.Min(a.MaxX, b.MaxX),
                Math.Max(a.MinY, b.MinY), Math.Min(a.MaxY, b.MaxY),
                Math.Max(a.MinZ, b.MinZ), Math.Min(a.MaxZ, b.MaxZ));
        }

        /// <summary>X 方向的中心:客户端取 (int)(尺寸 × 0.5 + 下界),单精度算、向零取整(证据 E-107,读 0x0109de20)。</summary>
        public int CenterX => (int)((MaxX - MinX) * 0.5f + MinX);

        /// <summary>Y 方向的中心,算法同 <see cref="CenterX"/>(读 0x0109de80)。</summary>
        public int CenterY => (int)((MaxY - MinY) * 0.5f + MinY);

        /// <summary>Z 方向的中心,算法同 <see cref="CenterX"/>(读 0x0109dee0)。</summary>
        public int CenterZ => (int)((MaxZ - MinZ) * 0.5f + MinZ);

        public override string ToString()
        {
            return "[" + MinX + ".." + MaxX + ", " + MinY + ".." + MaxY + ", " + MinZ + ".." + MaxZ + "]";
        }
    }

    /// <summary>
    /// 判定框的几何(推断,证据 E-036):.ani 里每个框是类型加 6 个整数 (x, y, z, 宽, 深, 高),相对对象位置;
    /// 朝向左时 x 方向镜像。依据:角色的受击框形如 (-1,-5,-1, 38,10,68)(宽 38、深 10、高 68),攻击框形如 (32,-10,7, 57,20,60);
    /// 六个数的取向没有引擎里的原表,只是与这些数值形状一致的解释。
    /// </summary>
    public static class Combat
    {
        public static Aabb ToWorld(AniBox box, int x, int y, int z, int direction)
        {
            int x0 = box.V0;
            int w = box.V3;
            int minX;
            if (direction == Direction.Left)
            {
                minX = x - (x0 + w);
            }
            else
            {
                minX = x + x0;
            }
            return new Aabb(minX, minX + w, y + box.V1, y + box.V1 + box.V4, z + box.V2, z + box.V2 + box.V5);
        }
    }
}
