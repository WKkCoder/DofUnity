using System;
using System.Buffers.Binary;

namespace Dnf.Net.Session
{
    /// <summary>
    /// NSEA 块算法（自定义，128 位分组）：一个实例对应“一次调用 + 一把密钥”，块与块之间按 CBC 方式用链值串起来。
    /// </summary>
    /// <remarks>
    /// 密钥调度：只用 key32 的前 16 字节。把 [0x00, 0x10, 16 字节密钥, 其余补零] 摆成 0x800 字节的“洗牌流”，
    /// 先后用前 0x400 字节、后 0x400 字节分别去洗两张初始 S 盒（256 项，每项四个字节都等于下标），
    /// 洗牌时第 i 步把第 i 个字节与位置 (流字节 + (i%4)*256) 处的字节对调。
    /// 两张 S 盒再按小端字读成两张 256 项的字表 A、B。
    ///
    /// 块变换：先把四个字与链值异或，拆成两对 S0=(w0,w1)、S1=(w2,w3)。
    /// 记 T(P)=(A[P.0 的低字节], B[P.1 的低字节])，shift(P)=把一对字当 64 位整体循环左移 8 位，则
    ///   S2 = S0 ^ T(S1)；S(n+2) = shift(S(n)) ^ T(S(n+1))，n=1..15；
    ///   密文 = shift(S16) 与 S17 拼成的四个字，同时成为下一块的链值。
    /// 解密把每一步反过来：S17 取自密文后两字，S16 是密文前两字做 shift 的逆，再逐步回推到 S0，最后与链值异或。
    /// </remarks>
    internal sealed class AntibotNseaCore
    {
        private const int KeyBytes = 0x10;

        private readonly uint[] _tableA;
        private readonly uint[] _tableB;
        private readonly uint[] _chain = new uint[4];

        internal AntibotNseaCore(byte[] key32)
        {
            byte[] stream = new byte[0x800];
            stream[1] = KeyBytes;
            Buffer.BlockCopy(key32, 0, stream, 2, KeyBytes);
            _tableA = ShuffledTable(stream, 0);
            _tableB = ShuffledTable(stream, 0x400);
        }

        /// <summary>原地加密 buf 中 offset 起的 16 字节，并把链值更新为这块密文。</summary>
        internal void EncryptBlock(byte[] buf, int offset)
        {
            uint a0 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset)) ^ _chain[0];
            uint a1 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 4)) ^ _chain[1];
            uint b0 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 8)) ^ _chain[2];
            uint b1 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 12)) ^ _chain[3];

            // 此刻 (a, b) = (S0, S1)；先得到 S2，之后每轮由前两项推出下一项。
            uint c0 = a0 ^ _tableA[b0 & 0xFF];
            uint c1 = a1 ^ _tableB[b1 & 0xFF];
            for (int n = 1; n <= 15; n++)
            {
                uint d0 = ((b0 << 8) | (b1 >> 24)) ^ _tableA[c0 & 0xFF];
                uint d1 = ((b1 << 8) | (b0 >> 24)) ^ _tableB[c1 & 0xFF];
                b0 = c0;
                b1 = c1;
                c0 = d0;
                c1 = d1;
            }

            // 此刻 (b, c) = (S16, S17)。
            _chain[0] = (b0 << 8) | (b1 >> 24);
            _chain[1] = (b1 << 8) | (b0 >> 24);
            _chain[2] = c0;
            _chain[3] = c1;
            WriteBlock(buf, offset, _chain[0], _chain[1], _chain[2], _chain[3]);
        }

        /// <summary>原地解密 buf 中 offset 起的 16 字节，并把链值更新为这块密文。</summary>
        internal void DecryptBlock(byte[] buf, int offset)
        {
            uint k0 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset));
            uint k1 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 4));
            uint k2 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 8));
            uint k3 = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 12));

            // (x, y) = (S16, S17)；每轮由后一项和再后一项回推前一项。
            uint x0 = (k0 >> 8) | (k1 << 24);
            uint x1 = (k1 >> 8) | (k0 << 24);
            uint y0 = k2;
            uint y1 = k3;
            for (int n = 15; n >= 1; n--)
            {
                uint t0 = y0 ^ _tableA[x0 & 0xFF];
                uint t1 = y1 ^ _tableB[x1 & 0xFF];
                y0 = x0;
                y1 = x1;
                x0 = (t0 >> 8) | (t1 << 24);
                x1 = (t1 >> 8) | (t0 << 24);
            }

            // 此刻 (x, y) = (S1, S2)；S0 = S2 ^ T(S1)。
            uint s00 = y0 ^ _tableA[x0 & 0xFF];
            uint s01 = y1 ^ _tableB[x1 & 0xFF];
            WriteBlock(buf, offset, s00 ^ _chain[0], s01 ^ _chain[1], x0 ^ _chain[2], x1 ^ _chain[3]);

            _chain[0] = k0;
            _chain[1] = k1;
            _chain[2] = k2;
            _chain[3] = k3;
        }

        private static void WriteBlock(byte[] buf, int offset, uint w0, uint w1, uint w2, uint w3)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), w0);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 4), w1);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 8), w2);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 12), w3);
        }

        /// <summary>用洗牌流 stream 从 start 起的 0x400 个字节洗一张初始 S 盒，并按小端字读成 256 项的字表。</summary>
        private static uint[] ShuffledTable(byte[] stream, int start)
        {
            byte[] box = new byte[0x400];
            for (int i = 0; i < 0x400; i++)
            {
                box[i] = (byte)(i >> 2);
            }

            for (int i = 0; i < 0x400; i++)
            {
                int target = stream[start + i] + (i & 3) * 0x100;
                byte held = box[i];
                box[i] = box[target];
                box[target] = held;
            }

            uint[] table = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                table[i] = BinaryPrimitives.ReadUInt32LittleEndian(box.AsSpan(4 * i));
            }

            return table;
        }
    }
}
