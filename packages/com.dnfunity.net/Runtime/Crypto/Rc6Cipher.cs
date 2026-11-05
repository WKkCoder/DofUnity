using System;

namespace Dnf.Net.Crypto
{
    /// <summary>
    /// 槽 3:RC6-32/20 的变体。块长 16 字节,切片 60 字节,但只有前 32 字节参与(本机模拟器逐字节翻转实测):
    /// 原版 Initialize(0x0809bc84)要求长度不小于 60,把前 32 字节拷成密钥、再拷 44 字节进扩展密钥表所在的区域,
    /// 然后以 32 字节的密钥长度调 rc6_key_setup(0x080b5b48)——扩展密钥表随即被整张重写,所以后 28 字节(以及越过切片的读取)对结果没有影响。
    /// 与标准 RC6 的唯一差别:扩展密钥表 S 是 44 个 **字节**,不是 44 个 32 位字(本机模拟器实测,E-263 里没有这一条):
    /// S[0] = 0x63、S[i] = S[i-1] - 0x47(正是 P32、Q32 魔数的低字节);混合阶段 S[i] = 低 8 位(ROL(S[i] + A + B, 3)),A 取这个字节值;
    /// 块函数(rc6_block_encrypt 0x080b5d6e)里加到字上的 S 项也是这些字节。其余(魔数之外的结构、20 轮、小端字、v = 3·max(44, c))与标准一致。
    /// 所以标准 RC6 的公开向量在这里不成立;本类以模拟器向量为准。
    /// 对象在 SetKey 之后只读,同一个对象可以被多个线程同时加解密(SetKey 本身不能与加解密并发)。
    /// </summary>
    public sealed class Rc6Cipher : BlockCipher
    {
        private const int Rounds = 20;
        private const int TableSize = 2 * Rounds + 4;      // 44 项
        private const int UsedKeyBytes = 32;               // rc6_key_setup 实际读的密钥字节数
        // 公开算法常量:RC6 的 P32 = 0xB7E15163、Q32 = 0x9E3779B9;原版只留低字节(0x63 与 0xB9,后者以减 0x47 的形式出现)。
        private const byte SeedLow = 0x63;
        private const byte StepSubtract = 0x47;

        private readonly byte[] _s = new byte[TableSize];
        private bool _keyed;

        public override int Slot => 3;

        public override int BlockSize => 16;

        public override int KeyLength => 60;

        public override void SetKey(byte[] key, int offset)
        {
            SlotKit0To6.CheckKey(key, offset, KeyLength);
            int c = UsedKeyBytes / 4;
            uint[] l = new uint[c];
            for (int i = 0; i < c; i++)
            {
                l[i] = SlotKit0To6.LoadLe32(key, offset + 4 * i);
            }
            _s[0] = SeedLow;
            for (int i = 1; i < TableSize; i++)
            {
                _s[i] = (byte)(_s[i - 1] - StepSubtract);
            }
            uint a = 0;
            uint b = 0;
            int ii = 0;
            int jj = 0;
            int steps = 3 * Math.Max(TableSize, c);
            for (int n = 0; n < steps; n++)
            {
                _s[ii] = (byte)SlotKit0To6.RotL((uint)_s[ii] + a + b, 3);
                a = _s[ii];
                l[jj] = SlotKit0To6.RotL(l[jj] + a + b, (int)(a + b));
                b = l[jj];
                ii = (ii + 1) % TableSize;
                jj = (jj + 1) % c;
            }
            _keyed = true;
        }

        public override void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            SlotKit0To6.EnsureKeyed(_keyed);
            SlotKit0To6.CheckBlock(input, inOffset, BlockSize, nameof(input), nameof(inOffset));
            SlotKit0To6.CheckBlock(output, outOffset, BlockSize, nameof(output), nameof(outOffset));
            uint a = SlotKit0To6.LoadLe32(input, inOffset);
            uint b = SlotKit0To6.LoadLe32(input, inOffset + 4);
            uint c = SlotKit0To6.LoadLe32(input, inOffset + 8);
            uint d = SlotKit0To6.LoadLe32(input, inOffset + 12);
            b += _s[0];
            d += _s[1];
            for (int i = 2; i <= 2 * Rounds; i += 2)
            {
                uint t = SlotKit0To6.RotL(b * (2u * b + 1u), 5);
                uint u = SlotKit0To6.RotL(d * (2u * d + 1u), 5);
                a = SlotKit0To6.RotL(a ^ t, (int)u) + _s[i];
                c = SlotKit0To6.RotL(c ^ u, (int)t) + _s[i + 1];
                uint first = a;
                a = b;
                b = c;
                c = d;
                d = first;
            }
            a += _s[TableSize - 2];
            c += _s[TableSize - 1];
            SlotKit0To6.StoreLe32(output, outOffset, a);
            SlotKit0To6.StoreLe32(output, outOffset + 4, b);
            SlotKit0To6.StoreLe32(output, outOffset + 8, c);
            SlotKit0To6.StoreLe32(output, outOffset + 12, d);
        }

        public override void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            SlotKit0To6.EnsureKeyed(_keyed);
            SlotKit0To6.CheckBlock(input, inOffset, BlockSize, nameof(input), nameof(inOffset));
            SlotKit0To6.CheckBlock(output, outOffset, BlockSize, nameof(output), nameof(outOffset));
            uint a = SlotKit0To6.LoadLe32(input, inOffset);
            uint b = SlotKit0To6.LoadLe32(input, inOffset + 4);
            uint c = SlotKit0To6.LoadLe32(input, inOffset + 8);
            uint d = SlotKit0To6.LoadLe32(input, inOffset + 12);
            c -= _s[TableSize - 1];
            a -= _s[TableSize - 2];
            for (int i = 2 * Rounds; i >= 2; i -= 2)
            {
                uint last = d;
                d = c;
                c = b;
                b = a;
                a = last;
                uint u = SlotKit0To6.RotL(d * (2u * d + 1u), 5);
                uint t = SlotKit0To6.RotL(b * (2u * b + 1u), 5);
                c = SlotKit0To6.RotR(c - _s[i + 1], (int)t) ^ u;
                a = SlotKit0To6.RotR(a - _s[i], (int)u) ^ t;
            }
            d -= _s[1];
            b -= _s[0];
            SlotKit0To6.StoreLe32(output, outOffset, a);
            SlotKit0To6.StoreLe32(output, outOffset + 4, b);
            SlotKit0To6.StoreLe32(output, outOffset + 8, c);
            SlotKit0To6.StoreLe32(output, outOffset + 12, d);
        }
    }
}
