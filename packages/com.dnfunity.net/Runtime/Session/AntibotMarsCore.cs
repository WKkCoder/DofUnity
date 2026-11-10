using System;
using System.Buffers.Binary;

namespace Dnf.Net.Session
{
    /// <summary>
    /// MARS（修订版）单块算法：128 位分组，密钥取 key32 的前 16 字节（四个小端字）扩成 40 个子密钥。
    /// 结构为：加首四个子密钥 → 8 轮无密钥前向混合 → 16 轮带密钥核心轮（前 8 轮、后 8 轮的加/异或次序不同）
    /// → 8 轮无密钥后向混合 → 减末四个子密钥。一个实例对应一把密钥。
    /// </summary>
    internal sealed class AntibotMarsCore
    {
        private readonly uint[] _keys = new uint[40];

        internal AntibotMarsCore(byte[] key32)
        {
            ExpandKey(key32);
        }

        /// <summary>原地加密 buf 中 offset 起的 16 字节。</summary>
        internal void EncryptBlock(byte[] buf, int offset)
        {
            uint a = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset)) + _keys[0];
            uint b = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 4)) + _keys[1];
            uint c = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 8)) + _keys[2];
            uint d = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 12)) + _keys[3];

            ForwardMix(ref a, ref b, ref c, ref d);
            CoreRounds(ref a, ref b, ref c, ref d);
            BackwardMix(ref a, ref b, ref c, ref d);

            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), a - _keys[36]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 4), b - _keys[37]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 8), c - _keys[38]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 12), d - _keys[39]);
        }

        /// <summary>原地解密 buf 中 offset 起的 16 字节：把加密的每一段按相反次序逐步撤销。</summary>
        internal void DecryptBlock(byte[] buf, int offset)
        {
            uint a = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset)) + _keys[36];
            uint b = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 4)) + _keys[37];
            uint c = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 8)) + _keys[38];
            uint d = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 12)) + _keys[39];

            UndoBackwardMix(ref a, ref b, ref c, ref d);
            UndoCoreRounds(ref a, ref b, ref c, ref d);
            UndoForwardMix(ref a, ref b, ref c, ref d);

            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), a - _keys[0]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 4), b - _keys[1]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 8), c - _keys[2]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 12), d - _keys[3]);
        }

        /// <summary>
        /// 密钥扩展：15 个字的工作区（前 4 个是密钥，第 5 个是密钥字数 4，其余为 0），
        /// 做 4 遍“线性变换 → 4 轮搅拌 → 取 10 个字”，共得 40 个子密钥；最后修正全部乘法子密钥（下标 5、7、……、35）。
        /// </summary>
        private void ExpandKey(byte[] key32)
        {
            uint[] box = AntibotMarsTables.SBox;
            uint[] t = new uint[15];
            for (int i = 0; i < 4; i++)
            {
                t[i] = BinaryPrimitives.ReadUInt32LittleEndian(key32.AsSpan(4 * i));
            }

            t[4] = 4;

            for (int pass = 0; pass < 4; pass++)
            {
                for (int i = 0; i < 15; i++)
                {
                    t[i] ^= RotateLeft(t[(i + 8) % 15] ^ t[(i + 13) % 15], 3) ^ (uint)(4 * i + pass);
                }

                for (int stir = 0; stir < 4; stir++)
                {
                    for (int i = 0; i < 15; i++)
                    {
                        t[i] = RotateLeft(t[i] + box[t[(i + 14) % 15] & 511], 9);
                    }
                }

                for (int i = 0; i < 10; i++)
                {
                    _keys[10 * pass + i] = t[(4 * i) % 15];
                }
            }

            for (int i = 5; i < 37; i += 2)
            {
                FixMultiplier(i);
            }
        }

        /// <summary>
        /// 乘法子密钥必须是奇数且不能含过长的同值比特串：先把低两位置 1，
        /// 再找出“连续 10 位以上全 0 或全 1”的串，对被它们覆盖的位异或上由前一个子密钥选定旋转量的常数。
        /// </summary>
        private void FixMultiplier(int index)
        {
            uint original = _keys[index];
            uint word = original | 3;

            // 第 l 位为 1 表示 word 的第 l、l+1 两位相同；连续 9 个这样的位 = 10 位同值串，runStart 标出串的最低位。
            uint same = ~(word ^ (word >> 1)) & 0x7FFFFFFF;
            uint four = same & (same >> 1) & (same >> 2);
            uint runStart = four & (four >> 3) & (four >> 6);
            if (runStart != 0)
            {
                uint spread = (runStart << 1) | (runStart << 2);
                spread |= spread << 2;
                uint mask = spread | (spread << 4);
                uint pattern = RotateLeft(AntibotMarsTables.Multipliers[(int)(original & 3)], (int)(_keys[index - 1] & 31)) & 0xFFFFFFFC;
                word ^= mask & pattern;
            }

            _keys[index] = word;
        }

        /// <summary>核心轮的混合函数：由输入字和两个子密钥得到 (左, 中, 右) 三个字。</summary>
        private static void EFunction(uint input, uint addKey, uint mulKey, out uint left, out uint middle, out uint right)
        {
            middle = input + addKey;
            right = RotateLeft(input, 13) * mulKey;
            left = AntibotMarsTables.SBox[middle & 511];
            right = RotateLeft(right, 5);
            middle = RotateLeft(middle, (int)(right & 31));
            left ^= right;
            right = RotateLeft(right, 5);
            left ^= right;
            left = RotateLeft(left, (int)(right & 31));
        }

        /// <summary>前向混合：8 轮，第 0、4 轮把 d 加进 a，第 1、5 轮把 b 加进 a。</summary>
        private static void ForwardMix(ref uint a, ref uint b, ref uint c, ref uint d)
        {
            uint[] box = AntibotMarsTables.SBox;
            for (int i = 0; i < 8; i++)
            {
                b ^= box[a & 0xFF];
                b += box[256 + ((a >> 8) & 0xFF)];
                c += box[(a >> 16) & 0xFF];
                d ^= box[256 + (a >> 24)];
                a = RotateRight(a, 24);
                if (i == 0 || i == 4)
                {
                    a += d;
                }
                else if (i == 1 || i == 5)
                {
                    a += b;
                }

                RotateWords(ref a, ref b, ref c, ref d);
            }
        }

        private static void UndoForwardMix(ref uint a, ref uint b, ref uint c, ref uint d)
        {
            uint[] box = AntibotMarsTables.SBox;
            for (int i = 7; i >= 0; i--)
            {
                UndoRotateWords(ref a, ref b, ref c, ref d);
                if (i == 0 || i == 4)
                {
                    a -= d;
                }
                else if (i == 1 || i == 5)
                {
                    a -= b;
                }

                a = RotateLeft(a, 24);
                d ^= box[256 + (a >> 24)];
                c -= box[(a >> 16) & 0xFF];
                b -= box[256 + ((a >> 8) & 0xFF)];
                b ^= box[a & 0xFF];
            }
        }

        /// <summary>核心轮：16 轮，前 8 轮把 (左, 右) 作用在 b、d 上为“加、异或”，后 8 轮为“d 加、b 异或”。</summary>
        private void CoreRounds(ref uint a, ref uint b, ref uint c, ref uint d)
        {
            for (int i = 0; i < 16; i++)
            {
                EFunction(a, _keys[2 * i + 4], _keys[2 * i + 5], out uint left, out uint middle, out uint right);
                a = RotateLeft(a, 13);
                c += middle;
                if (i < 8)
                {
                    b += left;
                    d ^= right;
                }
                else
                {
                    d += left;
                    b ^= right;
                }

                RotateWords(ref a, ref b, ref c, ref d);
            }
        }

        private void UndoCoreRounds(ref uint a, ref uint b, ref uint c, ref uint d)
        {
            for (int i = 15; i >= 0; i--)
            {
                UndoRotateWords(ref a, ref b, ref c, ref d);
                a = RotateRight(a, 13);
                EFunction(a, _keys[2 * i + 4], _keys[2 * i + 5], out uint left, out uint middle, out uint right);
                c -= middle;
                if (i < 8)
                {
                    b -= left;
                    d ^= right;
                }
                else
                {
                    d -= left;
                    b ^= right;
                }
            }
        }

        /// <summary>后向混合：8 轮，第 2、6 轮从 a 里减去 d，第 3、7 轮从 a 里减去 b。</summary>
        private static void BackwardMix(ref uint a, ref uint b, ref uint c, ref uint d)
        {
            uint[] box = AntibotMarsTables.SBox;
            for (int i = 0; i < 8; i++)
            {
                if (i == 2 || i == 6)
                {
                    a -= d;
                }
                else if (i == 3 || i == 7)
                {
                    a -= b;
                }

                b ^= box[256 + (a & 0xFF)];
                c -= box[a >> 24];
                d -= box[256 + ((a >> 16) & 0xFF)];
                d ^= box[(a >> 8) & 0xFF];
                a = RotateLeft(a, 24);
                RotateWords(ref a, ref b, ref c, ref d);
            }
        }

        private static void UndoBackwardMix(ref uint a, ref uint b, ref uint c, ref uint d)
        {
            uint[] box = AntibotMarsTables.SBox;
            for (int i = 7; i >= 0; i--)
            {
                UndoRotateWords(ref a, ref b, ref c, ref d);
                a = RotateRight(a, 24);
                d ^= box[(a >> 8) & 0xFF];
                d += box[256 + ((a >> 16) & 0xFF)];
                c += box[a >> 24];
                b ^= box[256 + (a & 0xFF)];
                if (i == 2 || i == 6)
                {
                    a += d;
                }
                else if (i == 3 || i == 7)
                {
                    a += b;
                }
            }
        }

        /// <summary>每轮结束时四个字轮换一位：(a, b, c, d) 变成 (b, c, d, a)。</summary>
        private static void RotateWords(ref uint a, ref uint b, ref uint c, ref uint d)
        {
            uint first = a;
            a = b;
            b = c;
            c = d;
            d = first;
        }

        /// <summary>RotateWords 的逆：(a, b, c, d) 变成 (d, a, b, c)。</summary>
        private static void UndoRotateWords(ref uint a, ref uint b, ref uint c, ref uint d)
        {
            uint last = d;
            d = c;
            c = b;
            b = a;
            a = last;
        }

        private static uint RotateLeft(uint value, int bits)
        {
            bits &= 31;
            return bits == 0 ? value : (value << bits) | (value >> (32 - bits));
        }

        private static uint RotateRight(uint value, int bits)
        {
            return RotateLeft(value, 32 - (bits & 31));
        }
    }
}
