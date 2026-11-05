using System;

namespace Dnf.Net.Crypto
{
    /// <summary>
    /// 槽 0:4 字节块的"移位 + 异或"(E-204)。块长 4 字节,密钥切片 8 字节。
    /// 原版 Initialize(0x0809c4fe)把切片前 4 字节(取低 5 位)存成移位量、后 4 字节存成异或键;
    /// 但加解密里 ROR/ROL 的返回值被丢弃(0x0809c5cb 之后 EAX 直接被覆盖;解密路径 0x0809c69e 同理),
    /// 所以真正起作用的只有"整块与异或键按位异或":切片前 4 字节对密文没有任何影响(本机模拟器逐字节翻转实测)。
    /// 本类逐位复刻这个实际行为,不实现那个被丢弃的移位。块是小端 32 位整数,异或不依赖字节序,所以等价于逐字节异或切片的后 4 字节。
    /// 对象在 SetKey 之后只读,同一个对象可以被多个线程同时加解密(SetKey 本身不能与加解密并发)。
    /// </summary>
    public sealed class ShiftCipher : BlockCipher
    {
        private uint _xorKey;
        private bool _keyed;

        public override int Slot => 0;

        public override int BlockSize => 4;

        public override int KeyLength => 8;

        public override void SetKey(byte[] key, int offset)
        {
            SlotKit0To6.CheckKey(key, offset, KeyLength);
            _xorKey = SlotKit0To6.LoadLe32(key, offset + 4);
            _keyed = true;
        }

        public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            Transform(input, inOffset, output, outOffset);
        }

        public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            // 解密路径同样是先异或、移位结果被丢弃,所以与加密完全相同(异或是自反的)。
            Transform(input, inOffset, output, outOffset);
        }

        private void Transform(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            SlotKit0To6.EnsureKeyed(_keyed);
            SlotKit0To6.CheckBlock(input, inOffset, BlockSize, nameof(input), nameof(inOffset));
            SlotKit0To6.CheckBlock(output, outOffset, BlockSize, nameof(output), nameof(outOffset));
            uint v = SlotKit0To6.LoadLe32(input, inOffset) ^ _xorKey;
            SlotKit0To6.StoreLe32(output, outOffset, v);
        }
    }

    /// <summary>槽 0 到 6 共用的小工具:参数检查、字节序读写、循环移位(只在本程序集内用)。</summary>
    internal static class SlotKit0To6
    {
        /// <summary>key 为 null → ArgumentNullException;切片 [offset, offset + keyLength) 超出数组 → ArgumentOutOfRangeException。</summary>
        internal static void CheckKey(byte[] key, int offset, int keyLength)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            if (offset < 0 || offset > key.Length - keyLength)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "密钥切片超出 key 数组");
            }
        }

        /// <summary>数组为 null → ArgumentNullException;[offset, offset + blockSize) 超出数组 → ArgumentOutOfRangeException。</summary>
        internal static void CheckBlock(byte[] data, int offset, int blockSize, string dataName, string offsetName)
        {
            if (data == null)
            {
                throw new ArgumentNullException(dataName);
            }
            if (offset < 0 || offset > data.Length - blockSize)
            {
                throw new ArgumentOutOfRangeException(offsetName, "数据块超出数组");
            }
        }

        /// <summary>还没设过密钥就加解密 → InvalidOperationException。</summary>
        internal static void EnsureKeyed(bool keyed)
        {
            if (!keyed)
            {
                throw new InvalidOperationException("尚未调用 SetKey");
            }
        }

        internal static uint LoadLe32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        internal static void StoreLe32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        internal static uint LoadBe32(byte[] b, int o)
        {
            return (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
        }

        internal static void StoreBe32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        /// <summary>32 位循环左移(移位量取低 5 位)。</summary>
        internal static uint RotL(uint x, int n)
        {
            n &= 31;
            return (x << n) | (x >> ((32 - n) & 31));
        }

        /// <summary>32 位循环右移(移位量取低 5 位)。</summary>
        internal static uint RotR(uint x, int n)
        {
            n &= 31;
            return (x >> n) | (x << ((32 - n) & 31));
        }
    }
}
