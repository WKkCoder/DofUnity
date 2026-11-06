using System;

namespace Dnf.Net.Crypto
{
    /// <summary>
    /// 槽 5:类名叫 Tea,实际是对服务端 CTEA 类(0x080c339c 起)的包装,轮函数是 XTEA:
    /// 32 个循环,每个循环先 v0 += ((v1&lt;&lt;4 ^ v1&gt;&gt;5) + v1) ^ (sum + k[sum&amp;3]),再 sum += 0x9e3779b9,
    /// 最后 v1 += ((v0&lt;&lt;4 ^ v0&gt;&gt;5) + v0) ^ (sum + k[(sum&gt;&gt;11)&amp;3])(0x080c3724 EncryptBlock)。
    /// 字节与 32 位字之间按大端转换(IMethod::BytesToWord 0x080b459e),密钥 16 字节 = 四个大端字。
    /// 包装层的"块长 16"只是长度必须是 16 的倍数;底层以 8 字节为单位,Initialize 传的模式是 0,
    /// CTEA::Encrypt 在模式 0 下逐 8 字节块独立加密(无链状态),所以一个 16 字节块 = 两个互不相干的 8 字节 XTEA 块(E-204、E-263)。
    /// 标准 TEA 的轮函数与此不同:零密钥零明文的标准 TEA 结果与原版不符,XTEA 的已知答案才与原版一致。
    /// 对象在 SetKey 之后只读,同一个对象可以被多个线程同时加解密(SetKey 本身不能与加解密并发)。
    /// </summary>
    public sealed class TeaCipher : BlockCipher
    {
        // 公开算法常量:XTEA 的 delta 与循环数(Needham/Wheeler,1997)。
        private const uint Delta = 0x9E3779B9u;
        private const int Cycles = 32;

        private readonly uint[] _k = new uint[4];
        private bool _keyed;

        public override int Slot => 5;

        public override int BlockSize => 16;

        public override int KeyLength => 16;

        public override void SetKey(byte[] key, int offset)
        {
            SlotKit0To6.CheckKey(key, offset, KeyLength);
            for (int i = 0; i < 4; i++)
            {
                _k[i] = SlotKit0To6.LoadBe32(key, offset + 4 * i);
            }
            _keyed = true;
        }

        public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            SlotKit0To6.EnsureKeyed(_keyed);
            SlotKit0To6.CheckBlock(input, inOffset, BlockSize, nameof(input), nameof(inOffset));
            SlotKit0To6.CheckBlock(output, outOffset, BlockSize, nameof(output), nameof(outOffset));
            // 先把两个半块都读进局部变量,再写出:输入输出是同一数组同一位置也安全。
            uint a0 = SlotKit0To6.LoadBe32(input, inOffset);
            uint a1 = SlotKit0To6.LoadBe32(input, inOffset + 4);
            uint b0 = SlotKit0To6.LoadBe32(input, inOffset + 8);
            uint b1 = SlotKit0To6.LoadBe32(input, inOffset + 12);
            EncryptHalf(ref a0, ref a1);
            EncryptHalf(ref b0, ref b1);
            SlotKit0To6.StoreBe32(output, outOffset, a0);
            SlotKit0To6.StoreBe32(output, outOffset + 4, a1);
            SlotKit0To6.StoreBe32(output, outOffset + 8, b0);
            SlotKit0To6.StoreBe32(output, outOffset + 12, b1);
        }

        public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            SlotKit0To6.EnsureKeyed(_keyed);
            SlotKit0To6.CheckBlock(input, inOffset, BlockSize, nameof(input), nameof(inOffset));
            SlotKit0To6.CheckBlock(output, outOffset, BlockSize, nameof(output), nameof(outOffset));
            uint a0 = SlotKit0To6.LoadBe32(input, inOffset);
            uint a1 = SlotKit0To6.LoadBe32(input, inOffset + 4);
            uint b0 = SlotKit0To6.LoadBe32(input, inOffset + 8);
            uint b1 = SlotKit0To6.LoadBe32(input, inOffset + 12);
            DecryptHalf(ref a0, ref a1);
            DecryptHalf(ref b0, ref b1);
            SlotKit0To6.StoreBe32(output, outOffset, a0);
            SlotKit0To6.StoreBe32(output, outOffset + 4, a1);
            SlotKit0To6.StoreBe32(output, outOffset + 8, b0);
            SlotKit0To6.StoreBe32(output, outOffset + 12, b1);
        }

        private void EncryptHalf(ref uint v0, ref uint v1)
        {
            uint a = v0;
            uint b = v1;
            uint sum = 0;
            for (int i = 0; i < Cycles; i++)
            {
                a += (((b << 4) ^ (b >> 5)) + b) ^ (sum + _k[sum & 3]);
                sum += Delta;
                b += (((a << 4) ^ (a >> 5)) + a) ^ (sum + _k[(sum >> 11) & 3]);
            }
            v0 = a;
            v1 = b;
        }

        private void DecryptHalf(ref uint v0, ref uint v1)
        {
            uint a = v0;
            uint b = v1;
            uint sum = unchecked(Delta * (uint)Cycles);
            for (int i = 0; i < Cycles; i++)
            {
                b -= (((a << 4) ^ (a >> 5)) + a) ^ (sum + _k[(sum >> 11) & 3]);
                sum -= Delta;
                a -= (((b << 4) ^ (b >> 5)) + b) ^ (sum + _k[sum & 3]);
            }
            v0 = a;
            v1 = b;
        }
    }
}
