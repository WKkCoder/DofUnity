namespace Dnf.Net.Session
{
    /// <summary>
    /// 反机器人信封里 6 个 8 字节分组算法(IDEA、DES 新、DES 大、CAST、TEAN、RC2)在整块之后对"剩余 1 到 7 个字节"做的整体变换。
    /// <para>
    /// 形式:对 n 个字节的 8n 个数据位做一个由密钥驱动的**位仿射变换**(位置换再逐位取反)。密钥字节依次处理,每个密钥字节走 16 个"块";
    /// 块 i 由该密钥字节的第 <c>BitOf[i]</c> 位决定用 8 种"位旋转"里的哪一种(位为 0 用 <c>MapIfClear[i]</c>,为 1 用 <c>MapIfSet[i]</c>)。
    /// 每个旋转 = 一个 8n 位的置换 + 一个取反掩码:新位 j = 旧位 <c>perm[j]</c> 再异或掩码的第 j 位;8 个旋转只有 4 种不同的置换(两个转轴 × 两个转向),
    /// 每种置换配两种取反掩码。密钥长度为 0 时什么都不做。解密是加密的严格逆:密钥字节倒序、块倒序、每块用逆旋转。
    /// </para>
    /// <para>
    /// 与参考实现的关系:参考实现把这 8n 位放在栈上的"位立方体"(节点图,每个节点四个方向指针)里,每个块是在立方体上做两三次环旋转
    /// (沿指针走一圈搬运节点值,部分旋转顺带取反);数据位进出立方体各有一套固定的位置编排和取反常量。这里不重现节点图,只保留它在数据位上的净效果:
    /// 载入/取回的编排与取反常量已并入各旋转的置换与掩码里。置换表、取反掩码、16 块的取位表是对参考实现做动态追踪
    /// (记录全部节点写入,用 8n+1 次数据探测还原每一步的仿射行)得到的,并对"全部 256 个密钥字节 × n=1..7 × 两个方向 × 多种密钥长度"逐一核对过。
    /// n=4..7 共用同一套取位表,n=1、2、3 的略有不同。
    /// </para>
    /// </summary>
    internal static class AntibotTailBox
    {
        /// <summary>
        /// 对 <paramref name="data"/> 从 <paramref name="offset"/> 起的 <paramref name="count"/>(1 到 7)个字节原地变换,
        /// 密钥取 <paramref name="key"/> 的前 <paramref name="keyLength"/> 字节(不超过 key 的长度;0 为恒等)。
        /// 参数不合法(null、count 不在 1..7、范围越界)时什么都不做。
        /// </summary>
        internal static void Apply(byte[] key, int keyLength, bool encrypt, byte[] data, int offset, int count)
        {
            if (key == null || data == null || count < 1 || count > 7 || offset < 0 || offset > data.Length - count)
            {
                return;
            }

            if (keyLength > key.Length)
            {
                keyLength = key.Length;
            }

            if (keyLength <= 0)
            {
                return;
            }

            BoxShape shape = Shapes[count - 1];
            ulong state = 0;
            for (int i = 0; i < count; i++)
            {
                state |= (ulong)data[offset + i] << (8 * i);
            }

            if (encrypt)
            {
                for (int k = 0; k < keyLength; k++)
                {
                    int keyByte = key[k];
                    for (int b = 0; b < 16; b++)
                    {
                        int map = ((keyByte >> shape.BitOf[b]) & 1) != 0 ? shape.MapIfSet[b] : shape.MapIfClear[b];
                        state = Gather(state, shape.Src[map], shape.Flip[map], shape.Bits);
                    }
                }
            }
            else
            {
                for (int k = keyLength - 1; k >= 0; k--)
                {
                    int keyByte = key[k];
                    for (int b = 15; b >= 0; b--)
                    {
                        int map = ((keyByte >> shape.BitOf[b]) & 1) != 0 ? shape.MapIfSet[b] : shape.MapIfClear[b];
                        state = Gather(state, shape.InvSrc[map], shape.InvFlip[map], shape.Bits);
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                data[offset + i] = (byte)(state >> (8 * i));
            }
        }

        /// <summary>新状态的第 j 位 = 旧状态的第 src[j] 位,再异或取反掩码。</summary>
        private static ulong Gather(ulong state, byte[] src, ulong flip, int bits)
        {
            ulong result = 0;
            for (int j = 0; j < bits; j++)
            {
                result |= ((state >> src[j]) & 1UL) << j;
            }

            return result ^ flip;
        }

        /// <summary>某个 n 的整套参数,并在构造时算出各旋转的逆映射。</summary>
        private sealed class BoxShape
        {
            internal readonly int Bits;
            internal readonly byte[][] Src;
            internal readonly ulong[] Flip;
            internal readonly byte[][] InvSrc;
            internal readonly ulong[] InvFlip;
            internal readonly byte[] BitOf;
            internal readonly byte[] MapIfClear;
            internal readonly byte[] MapIfSet;

            /// <param name="bits">8n。</param>
            /// <param name="perms">不同的置换(新位 j 取自旧位 perms[p][j])。</param>
            /// <param name="permOf">8 个旋转各自用哪种置换。</param>
            /// <param name="flip">8 个旋转各自的取反掩码。</param>
            /// <param name="bitOf">16 个块各由密钥字节的哪一位选择。</param>
            /// <param name="mapIfClear">该位为 0 时块用的旋转号。</param>
            /// <param name="mapIfSet">该位为 1 时块用的旋转号。</param>
            internal BoxShape(int bits, byte[][] perms, byte[] permOf, ulong[] flip, byte[] bitOf, byte[] mapIfClear, byte[] mapIfSet)
            {
                Bits = bits;
                Flip = flip;
                BitOf = bitOf;
                MapIfClear = mapIfClear;
                MapIfSet = mapIfSet;
                int maps = permOf.Length;
                Src = new byte[maps][];
                InvSrc = new byte[maps][];
                InvFlip = new ulong[maps];
                for (int m = 0; m < maps; m++)
                {
                    byte[] src = perms[permOf[m]];
                    Src[m] = src;
                    InvSrc[m] = new byte[bits];
                    for (int j = 0; j < bits; j++)
                    {
                        // 新位 j = 旧位 src[j] ^ f[j]  =>  旧位 src[j] = 新位 j ^ f[j]
                        InvSrc[m][src[j]] = (byte)j;
                        if (((flip[m] >> j) & 1UL) != 0)
                        {
                            InvFlip[m] |= 1UL << src[j];
                        }
                    }
                }
            }
        }

        // 下标 n-1 对应 n 个字节。
        private static readonly BoxShape[] Shapes =
        {
            // n = 1:8 位
            new BoxShape(
                8,
                new byte[][]
                {
                    new byte[] { 0, 1, 4, 7, 5, 2, 3, 6 },
                    new byte[] { 0, 1, 6, 5, 3, 4, 7, 2 },
                    new byte[] { 7, 0, 1, 2, 4, 5, 6, 3 },
                    new byte[] { 1, 6, 3, 0, 4, 5, 2, 7 },
                },
                new byte[] { 0, 1, 2, 3, 0, 1, 3, 2 },
                new ulong[] { 0x3CUL, 0x48UL, 0x48UL, 0xC3UL, 0x48UL, 0x84UL, 0x48UL, 0x84UL },
                new byte[] { 6, 3, 5, 4, 5, 3, 4, 2, 2, 6, 1, 0, 0, 7, 7, 1 },
                new byte[] { 0, 2, 4, 4, 3, 5, 6, 3, 1, 6, 0, 5, 7, 1, 7, 2 },
                new byte[] { 1, 3, 5, 5, 2, 4, 7, 2, 0, 7, 1, 4, 6, 0, 6, 3 }),
            // n = 2:16 位
            new BoxShape(
                16,
                new byte[][]
                {
                    new byte[] { 0, 2, 14, 5, 4, 6, 10, 1, 8, 11, 9, 3, 12, 15, 13, 7 },
                    new byte[] { 0, 3, 1, 11, 4, 7, 5, 15, 8, 10, 6, 13, 12, 14, 2, 9 },
                    new byte[] { 12, 1, 2, 7, 0, 5, 6, 4, 13, 9, 10, 15, 8, 11, 14, 3 },
                    new byte[] { 4, 7, 2, 15, 1, 5, 6, 3, 12, 9, 10, 8, 0, 13, 14, 11 },
                },
                new byte[] { 0, 1, 0, 1, 2, 3, 3, 2 },
                new ulong[] { 0x2080UL, 0x802UL, 0xC80EUL, 0x8EE8UL, 0xC80EUL, 0x7117UL, 0x802UL, 0x2080UL },
                new byte[] { 3, 2, 7, 7, 4, 6, 1, 5, 3, 6, 2, 4, 5, 0, 1, 0 },
                new byte[] { 0, 2, 3, 4, 3, 6, 0, 5, 7, 1, 5, 7, 1, 2, 4, 7 },
                new byte[] { 1, 3, 2, 5, 2, 7, 1, 4, 6, 0, 4, 6, 0, 3, 5, 6 }),
            // n = 3:24 位
            new BoxShape(
                24,
                new byte[][]
                {
                    new byte[] { 0, 14, 1, 5, 4, 6, 12, 2, 8, 11, 10, 3, 9, 15, 13, 17, 16, 23, 7, 18, 20, 21, 22, 19 },
                    new byte[] { 0, 2, 18, 11, 4, 7, 5, 15, 8, 12, 10, 13, 6, 14, 1, 9, 16, 3, 19, 23, 20, 21, 22, 17 },
                    new byte[] { 4, 22, 7, 15, 2, 5, 6, 3, 10, 9, 0, 8, 12, 13, 14, 11, 1, 17, 18, 19, 21, 16, 20, 23 },
                    new byte[] { 10, 1, 16, 7, 0, 5, 6, 4, 13, 9, 8, 15, 12, 11, 14, 3, 21, 17, 18, 19, 22, 20, 2, 23 },
                },
                new byte[] { 0, 1, 0, 1, 2, 3, 2, 3 },
                new ulong[] { 0x2080UL, 0x804UL, 0xC80EUL, 0x8E9AE8UL, 0x804UL, 0x2080UL, 0x716517UL, 0xC80EUL },
                new byte[] { 3, 2, 7, 4, 6, 1, 5, 3, 6, 2, 7, 4, 5, 0, 1, 0 },
                new byte[] { 0, 2, 1, 3, 4, 0, 6, 5, 1, 6, 7, 5, 1, 2, 7, 5 },
                new byte[] { 1, 3, 0, 2, 5, 1, 7, 4, 0, 7, 6, 4, 0, 3, 6, 4 }),
            // n = 4:32 位
            new BoxShape(
                32,
                new byte[][]
                {
                    new byte[] { 4, 6, 10, 3, 2, 5, 13, 1, 8, 11, 9, 0, 12, 14, 27, 17, 7, 23, 18, 16, 20, 21, 22, 19, 15, 25, 31, 26, 28, 29, 30, 24 },
                    new byte[] { 24, 16, 4, 3, 7, 5, 1, 15, 8, 10, 2, 14, 12, 6, 13, 9, 19, 0, 18, 23, 20, 21, 22, 17, 31, 25, 27, 11, 28, 29, 30, 26 },
                    new byte[] { 15, 7, 2, 5, 4, 1, 22, 0, 3, 9, 10, 12, 8, 13, 30, 11, 16, 17, 6, 19, 21, 18, 20, 23, 24, 14, 26, 27, 29, 25, 28, 31 },
                    new byte[] { 7, 18, 2, 8, 4, 3, 6, 5, 12, 9, 10, 15, 14, 25, 11, 0, 16, 17, 21, 19, 22, 20, 1, 23, 24, 29, 26, 27, 30, 28, 13, 31 },
                },
                new byte[] { 0, 1, 0, 1, 2, 3, 3, 2 },
                new ulong[] { 0x4080UL, 0x802UL, 0xA843UL, 0x8D8B8E95UL, 0x7274716AUL, 0xA843UL, 0x4080UL, 0x802UL },
                new byte[] { 3, 2, 2, 7, 4, 7, 4, 6, 1, 5, 3, 6, 5, 0, 1, 0 },
                new byte[] { 0, 2, 4, 5, 6, 1, 3, 7, 0, 4, 6, 1, 1, 2, 5, 6 },
                new byte[] { 1, 3, 5, 4, 7, 0, 2, 6, 1, 5, 7, 0, 0, 3, 4, 7 }),
            // n = 5:40 位
            new BoxShape(
                40,
                new byte[][]
                {
                    new byte[] { 4, 6, 10, 3, 2, 5, 13, 1, 8, 11, 9, 0, 12, 14, 27, 17, 7, 23, 18, 16, 20, 21, 22, 35, 15, 25, 31, 26, 28, 29, 30, 24, 19, 33, 39, 34, 36, 37, 38, 32 },
                    new byte[] { 24, 16, 4, 3, 7, 5, 1, 15, 8, 10, 2, 14, 12, 6, 13, 9, 19, 0, 18, 32, 20, 21, 22, 17, 31, 25, 27, 11, 28, 29, 30, 26, 39, 33, 35, 23, 36, 37, 38, 34 },
                    new byte[] { 15, 7, 2, 5, 4, 1, 22, 0, 3, 9, 10, 12, 8, 13, 30, 11, 16, 17, 6, 19, 38, 18, 20, 23, 24, 14, 26, 27, 29, 25, 28, 31, 32, 21, 34, 35, 37, 33, 36, 39 },
                    new byte[] { 7, 18, 2, 8, 4, 3, 6, 5, 12, 9, 10, 15, 14, 25, 11, 0, 16, 17, 21, 19, 22, 33, 1, 23, 24, 29, 26, 27, 30, 28, 13, 31, 32, 37, 34, 35, 38, 36, 20, 39 },
                },
                new byte[] { 0, 1, 0, 1, 2, 3, 3, 2 },
                new ulong[] { 0x4080UL, 0x802UL, 0xA843UL, 0x8D8D8B8E95UL, 0x727274716AUL, 0xA843UL, 0x4080UL, 0x802UL },
                new byte[] { 3, 2, 2, 7, 4, 7, 4, 6, 1, 5, 3, 6, 5, 0, 1, 0 },
                new byte[] { 0, 2, 4, 5, 6, 1, 3, 7, 0, 4, 6, 1, 1, 2, 5, 6 },
                new byte[] { 1, 3, 5, 4, 7, 0, 2, 6, 1, 5, 7, 0, 0, 3, 4, 7 }),
            // n = 6:48 位
            new BoxShape(
                48,
                new byte[][]
                {
                    new byte[] { 4, 6, 10, 3, 2, 5, 13, 1, 8, 11, 9, 0, 12, 14, 27, 17, 7, 23, 18, 16, 20, 21, 22, 35, 15, 25, 41, 26, 28, 29, 30, 24, 19, 33, 39, 34, 36, 37, 38, 32, 40, 43, 31, 47, 44, 45, 46, 42 },
                    new byte[] { 24, 16, 4, 3, 7, 5, 1, 15, 8, 10, 2, 14, 12, 6, 13, 9, 19, 0, 18, 32, 20, 21, 22, 17, 31, 25, 27, 11, 28, 29, 30, 42, 39, 33, 35, 23, 36, 37, 38, 34, 40, 26, 47, 41, 44, 45, 46, 43 },
                    new byte[] { 15, 7, 2, 5, 4, 1, 22, 0, 3, 9, 10, 12, 8, 13, 30, 11, 16, 17, 6, 19, 38, 18, 20, 23, 24, 14, 26, 27, 40, 25, 28, 31, 32, 21, 34, 35, 37, 33, 36, 39, 45, 41, 42, 43, 29, 46, 44, 47 },
                    new byte[] { 7, 18, 2, 8, 4, 3, 6, 5, 12, 9, 10, 15, 14, 25, 11, 0, 16, 17, 21, 19, 22, 33, 1, 23, 24, 29, 26, 27, 30, 44, 13, 31, 32, 37, 34, 35, 38, 36, 20, 39, 28, 41, 42, 43, 46, 40, 45, 47 },
                },
                new byte[] { 0, 1, 0, 1, 2, 3, 3, 2 },
                new ulong[] { 0x4080UL, 0x802UL, 0xA843UL, 0x8E8D8D8B8E95UL, 0x71727274716AUL, 0xA843UL, 0x4080UL, 0x802UL },
                new byte[] { 3, 2, 2, 7, 4, 7, 4, 6, 1, 5, 3, 6, 5, 0, 1, 0 },
                new byte[] { 0, 2, 4, 5, 6, 1, 3, 7, 0, 4, 6, 1, 1, 2, 5, 6 },
                new byte[] { 1, 3, 5, 4, 7, 0, 2, 6, 1, 5, 7, 0, 0, 3, 4, 7 }),
            // n = 7:56 位
            new BoxShape(
                56,
                new byte[][]
                {
                    new byte[] { 4, 6, 10, 3, 2, 5, 13, 1, 8, 11, 9, 0, 12, 14, 27, 17, 7, 23, 18, 16, 20, 21, 22, 35, 15, 25, 41, 26, 28, 29, 30, 24, 19, 33, 49, 34, 36, 37, 38, 32, 40, 43, 31, 47, 44, 45, 46, 42, 48, 51, 39, 55, 52, 53, 54, 50 },
                    new byte[] { 24, 16, 4, 3, 7, 5, 1, 15, 8, 10, 2, 14, 12, 6, 13, 9, 19, 0, 18, 32, 20, 21, 22, 17, 31, 25, 27, 11, 28, 29, 30, 42, 39, 33, 35, 23, 36, 37, 38, 50, 40, 26, 47, 41, 44, 45, 46, 43, 48, 34, 55, 49, 52, 53, 54, 51 },
                    new byte[] { 15, 7, 2, 5, 4, 1, 22, 0, 3, 9, 10, 12, 8, 13, 30, 11, 16, 17, 6, 19, 38, 18, 20, 23, 24, 14, 26, 27, 40, 25, 28, 31, 32, 21, 34, 35, 48, 33, 36, 39, 45, 41, 42, 43, 29, 46, 44, 47, 53, 49, 50, 51, 37, 54, 52, 55 },
                    new byte[] { 7, 18, 2, 8, 4, 3, 6, 5, 12, 9, 10, 15, 14, 25, 11, 0, 16, 17, 21, 19, 22, 33, 1, 23, 24, 29, 26, 27, 30, 44, 13, 31, 32, 37, 34, 35, 38, 52, 20, 39, 28, 41, 42, 43, 46, 40, 45, 47, 36, 49, 50, 51, 54, 48, 53, 55 },
                },
                new byte[] { 0, 1, 0, 1, 2, 3, 3, 2 },
                new ulong[] { 0x4080UL, 0x802UL, 0xA843UL, 0x8E8E8D8D8B8E95UL, 0x7171727274716AUL, 0xA843UL, 0x4080UL, 0x802UL },
                new byte[] { 3, 2, 2, 7, 4, 7, 4, 6, 1, 5, 3, 6, 5, 0, 1, 0 },
                new byte[] { 0, 2, 4, 5, 6, 1, 3, 7, 0, 4, 6, 1, 1, 2, 5, 6 },
                new byte[] { 1, 3, 5, 4, 7, 0, 2, 6, 1, 5, 7, 0, 0, 3, 4, 7 }),
        };
    }
}
