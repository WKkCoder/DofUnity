using System;

namespace Dnf.Net.Frame
{
    /// <summary>
    /// 会话启动包(类 0、包号 1,即带会话密钥块的那个包)用的"固定种子变换"。
    /// 逐字节独立:加密 y = ROR8(x ^ K, R),解密 x = ROL8(y, R) ^ K,长度不变,没有跨字节状态。
    /// 种子变量为 0(线上唯一用到的路径)时 K 恒为 0xB5、R 恒为 6,整个变换就是一张固定的 256 项置换表;
    /// 种子非 0 时先用 <see cref="SeedRand"/> 推进种子再取 K、R(线上不会走到,保留是为了与服务端函数逐值对齐)。
    /// </summary>
    public static class BootstrapTransform
    {
        /// <summary>种子变量为 0 时的状态字。</summary>
        public const uint FixedState = 0x04453EB5u;

        /// <summary>种子为 0 时的异或字节(状态字最低字节)。</summary>
        public const byte FixedKey = 0xB5;

        /// <summary>种子为 0 时的循环右移位数(状态字第 2 个字节与 7 相与)。</summary>
        public const int FixedRotation = 6;

        /// <summary>线性同余伪随机数:三步 x = x * 1103515245 + 12345,依次取 11、10、10 位拼成 31 位结果,并推进种子。</summary>
        public static uint SeedRand(ref uint seed)
        {
            unchecked
            {
                uint x = seed;
                x = x * 1103515245u + 12345u;
                uint a = (x >> 16) & 0x7FFu;
                x = x * 1103515245u + 12345u;
                uint b = (x >> 16) & 0x3FFu;
                x = x * 1103515245u + 12345u;
                uint c = (x >> 16) & 0x3FFu;
                seed = x;
                return (((a << 10) ^ b) << 10) ^ c;
            }
        }

        /// <summary>按种子变量求 (K, R);种子为 0 时种子保持不变,否则被推进一步。</summary>
        public static void Derive(ref uint seed, out byte key, out int rotation)
        {
            uint state = seed == 0 ? FixedState : SeedRand(ref seed);
            key = (byte)(state & 0xFF);
            rotation = (int)((state >> 8) & 7);
        }

        /// <summary>加密一个字节(种子为 0 的线上路径)。</summary>
        public static byte EncodeByte(byte value) => Ror8((byte)(value ^ FixedKey), FixedRotation);

        /// <summary>解密一个字节(种子为 0 的线上路径)。</summary>
        public static byte DecodeByte(byte value) => (byte)(Rol8(value, FixedRotation) ^ FixedKey);

        /// <summary>原位加密,种子为 0。</summary>
        public static void Encode(Span<byte> data)
        {
            uint seed = 0;
            Encode(data, ref seed);
        }

        /// <summary>原位解密,种子为 0。</summary>
        public static void Decode(Span<byte> data)
        {
            uint seed = 0;
            Decode(data, ref seed);
        }

        /// <summary>原位加密;<paramref name="seed"/> 为 0 时不被修改,否则推进一步(每次调用只推进一次,与数据长度无关)。</summary>
        public static void Encode(Span<byte> data, ref uint seed)
        {
            Derive(ref seed, out byte key, out int rotation);
            for (int i = 0; i < data.Length; i++)
                data[i] = Ror8((byte)(data[i] ^ key), rotation);
        }

        /// <summary>原位解密;种子规则同 <see cref="Encode(Span{byte}, ref uint)"/>。</summary>
        public static void Decode(Span<byte> data, ref uint seed)
        {
            Derive(ref seed, out byte key, out int rotation);
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)(Rol8(data[i], rotation) ^ key);
        }

        /// <summary>返回加密后的新数组(种子为 0)。</summary>
        public static byte[] Encode(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            byte[] copy = (byte[])data.Clone();
            Encode(copy.AsSpan());
            return copy;
        }

        /// <summary>返回解密后的新数组(种子为 0)。</summary>
        public static byte[] Decode(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            byte[] copy = (byte[])data.Clone();
            Decode(copy.AsSpan());
            return copy;
        }

        private static byte Ror8(byte value, int count)
        {
            count &= 7;
            return (byte)((value >> count) | (value << (8 - count)));
        }

        private static byte Rol8(byte value, int count)
        {
            count &= 7;
            return (byte)((value << count) | (value >> (8 - count)));
        }
    }
}
