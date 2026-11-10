using System;
using System.Buffers.Binary;

namespace Dnf.Net.Session
{
    /// <summary>
    /// NSEA 自定义 128 位分组；<paramref name="key32"/> 前 16 字节。
    /// 密钥调度对齐 <c>CNsea::initSBoxes(key, i=0x10, j=0)</c>（<c>initTempKey</c> 长度参数为 i，种子取自 j）；
    /// 会话入口 <c>initIVE(0)</c>；块间延续 IVE 与链字节 <c>this+0xc</c>。
    /// 余量 <c>len%16</c> 走 Seattos。
    /// </summary>
    internal static class AntibotNsea
    {
        internal static void Transform(byte[] key32, bool encrypt, byte[] buf)
        {
            if (buf.Length == 0)
            {
                return;
            }

            AntibotNseaCore.BeginSession(key32);
            int off = 0;
            int aligned = buf.Length & ~0xF;
            while (off < aligned)
            {
                if (encrypt)
                {
                    AntibotNseaCore.EncryptBlock(buf, off);
                }
                else
                {
                    AntibotNseaCore.DecryptBlock(buf, off);
                }

                off += 16;
            }

            int rest = buf.Length - off;
            if (rest > 0)
            {
                ApplySeattosSuffix(key32, encrypt, buf, off, rest);
            }
        }

        private static void ApplySeattosSuffix(byte[] key32, bool encrypt, byte[] buf, int offset, int count)
        {
            byte[] tail = new byte[count];
            Buffer.BlockCopy(buf, offset, tail, 0, count);
            AntibotSeattos.Transform(key32, encrypt, tail);
            Buffer.BlockCopy(tail, 0, buf, offset, count);
        }
    }

    internal static class AntibotNseaCore
    {
        private static byte[] _sboxA;
        private static byte[] _sboxB;
        private static readonly uint[] Ive = new uint[4];
        private static uint _chainByte;

        internal static void BeginSession(byte[] key32)
        {
            byte[] key16 = new byte[16];
            Buffer.BlockCopy(key32, 0, key16, 0, 16);
            AntibotNseaTables.Build(key16, out _sboxA, out _sboxB);
            Ive[0] = Ive[1] = Ive[2] = Ive[3] = 0;
            _chainByte = 0;
        }

        internal static void EncryptBlock(byte[] buf, int offset)
        {
            uint[] w = ReadBlockWords(buf, offset);
            AntibotNseaKernel.EncryptBlock(_sboxA, _sboxB, Ive, ref _chainByte, w);
            WriteBlockWords(buf, offset, w);
        }

        internal static void DecryptBlock(byte[] buf, int offset)
        {
            uint[] w = ReadBlockWords(buf, offset);
            AntibotNseaKernel.DecryptBlock(_sboxA, _sboxB, Ive, ref _chainByte, w);
            WriteBlockWords(buf, offset, w);
        }

        private static uint[] ReadBlockWords(byte[] buf, int offset)
        {
            return new uint[]
            {
                BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset)),
                BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 12)),
            };
        }

        private static void WriteBlockWords(byte[] buf, int offset, uint[] w)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset), w[0]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 4), w[1]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 8), w[2]);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(offset + 12), w[3]);
        }
    }

    internal static class AntibotNseaTables
    {
        private const uint LcgMul = 0x343FD;
        private const uint LcgAdd = 0x269EC3;

        /// <summary>TenNsea 调用 initSBoxes 时 i=0x10、j=0。</summary>
        internal static void Build(byte[] key16, out byte[] sboxA, out byte[] sboxB)
        {
            const int keyParamI = 0x10;
            const int keyParamJ = 0;

            byte[] buf = new byte[0x800];
            byte[] stream0 = ExpandTempKey(key16, keyParamI, keyParamJ & 0xFFFF);
            byte[] stream1 = ExpandTempKey(key16, keyParamI, (keyParamJ >> 16) & 0xFFFF);
            Buffer.BlockCopy(stream0, 0, buf, 0, 0x400);
            Buffer.BlockCopy(stream1, 0, buf, 0x400, 0x400);

            byte[] phaseA = CreateInitialSbox();
            byte[] phaseB = CreateInitialSbox();
            PermuteSbox(phaseA, buf, 0);
            PermuteSbox(phaseB, buf, 0x400);

            buf[0] = 0;
            buf[1] = (byte)keyParamI;
            Buffer.BlockCopy(key16, 0, buf, 2, 16);
            for (int i = 2 + keyParamI; i < 0x7FF; i++)
            {
                buf[i] = 0;
            }

            sboxA = CreateInitialSbox();
            sboxB = CreateInitialSbox();
            PermuteSbox(sboxA, buf, 0);
            PermuteSbox(sboxB, buf, 0x400);
        }

        private static byte[] ExpandTempKey(byte[] key, int keyLen, int seedWord)
        {
            int seed = seedWord & 0xFFFF;
            for (int i = 0; i < keyLen; i++)
            {
                seed = ((seed << 1) & 0xFFFF) ^ key[i];
            }

            byte[] outBytes = new byte[0x400];
            uint state = (uint)seed;
            for (int i = 0; i < 0x400; i++)
            {
                state = state * LcgMul + LcgAdd;
                outBytes[i] = (byte)((state >> 16) & 0xFF);
            }

            return outBytes;
        }

        private static byte[] CreateInitialSbox()
        {
            byte[] box = new byte[0x400];
            for (int i = 0; i < 256; i++)
            {
                int b = i * 4;
                box[b] = (byte)i;
                box[b + 1] = (byte)i;
                box[b + 2] = (byte)i;
                box[b + 3] = (byte)i;
            }

            return box;
        }

        private static void PermuteSbox(byte[] box, byte[] stream, int start)
        {
            int streamOff = start;
            for (int edi = 0; edi < 0x400; edi++)
            {
                int sb = stream[streamOff++];
                int j = (sb + (edi & 3) * 0x100 + start) & 0x3FF;
                byte tmp = box[edi];
                box[edi] = box[j];
                box[j] = tmp;
            }
        }
    }
}
