using System;
using System.Security.Cryptography;

namespace Dnf.Net.Frame
{
    /// <summary>
    /// 包完整性第二层:校验字(客户端到服务端)或第二个校验值(服务端到客户端)的高 3 字节。
    /// 发送方把"密文的 SuperFastHash 折成 1 字节再异或连接上的状态字节"得到 h,与两个随机字节 r1、r3 按固定位置拼成 24 位,
    /// 新状态等于 r1;接收方从中取回 r1 与 h,用自己的状态重算比较。不符只记一次可疑事件,不拒收;密文为空时恒通过。
    /// </summary>
    public static partial class PacketIntegrity
    {
        /// <summary>连接上状态字节的初值,以及复位后的值。</summary>
        public const byte SecondLayerInitialState = 0x27;

        /// <summary>客户端到服务端:类 1 包号 1 出现时先把状态复位。</summary>
        public static bool ResetsStateClientToServer(PacketClass packetClass, int packetId)
        {
            return packetClass == PacketClass.Cmd && packetId == 1;
        }

        /// <summary>服务端到客户端:类 0 包号 1 出现时先把状态复位。</summary>
        public static bool ResetsStateServerToClient(PacketClass packetClass, int packetId)
        {
            return packetClass == PacketClass.Noti && packetId == 1;
        }

        /// <summary>
        /// SuperFastHash(Paul Hsieh 的公开算法),尾部字节按无符号处理;长度 0 返回 0。
        /// </summary>
        public static uint SuperFastHash(ReadOnlySpan<byte> data)
        {
            int length = data.Length;
            if (length == 0) return 0;
            unchecked
            {
                uint hash = (uint)length;
                int rem = length & 3;
                int i = 0;
                for (int blocks = length >> 2; blocks > 0; blocks--)
                {
                    hash += (uint)(data[i] | (data[i + 1] << 8));
                    uint tmp = ((uint)(data[i + 2] | (data[i + 3] << 8)) << 11) ^ hash;
                    hash = (hash << 16) ^ tmp;
                    hash += hash >> 11;
                    i += 4;
                }
                switch (rem)
                {
                    case 3:
                        hash += (uint)(data[i] | (data[i + 1] << 8));
                        hash ^= hash << 16;
                        hash ^= (uint)data[i + 2] << 18;
                        hash += hash >> 11;
                        break;
                    case 2:
                        hash += (uint)(data[i] | (data[i + 1] << 8));
                        hash ^= hash << 11;
                        hash += hash >> 17;
                        break;
                    case 1:
                        hash += data[i];
                        hash ^= hash << 10;
                        hash += hash >> 1;
                        break;
                }
                hash ^= hash << 3;
                hash += hash >> 5;
                hash ^= hash << 4;
                hash += hash >> 17;
                hash ^= hash << 25;
                hash += hash >> 6;
                return hash;
            }
        }

        /// <summary>SuperFastHash 的 4 个字节与 <paramref name="key"/> 一起异或成 1 个字节。</summary>
        public static byte MakeHash(ReadOnlySpan<byte> data, byte key)
        {
            uint h = SuperFastHash(data);
            return (byte)(h ^ (h >> 8) ^ (h >> 16) ^ (h >> 24) ^ key);
        }

        /// <summary>把随机字节 r1 与 1 字节哈希 h 拼成 16 位值(位置固定,是一个双射)。</summary>
        public static ushort Scatter(byte r1, byte h)
        {
            int x = (r1 >> 6)
                | ((h & 0x03) << 6)
                | ((h & 0x0C) << 12)
                | ((h & 0x30) >> 2)
                | ((h & 0xC0) << 4)
                | ((r1 & 0x03) << 12)
                | ((r1 & 0x0C) << 2)
                | ((r1 & 0x30) << 4);
            return (ushort)x;
        }

        /// <summary><see cref="Scatter"/> 的逆。</summary>
        public static void Unscatter(ushort x, out byte r1, out byte h)
        {
            r1 = (byte)(((x & 0x03) << 6) | ((x >> 12) & 0x03) | (((x >> 4) & 0x03) << 2) | (((x >> 8) & 0x03) << 4));
            h = (byte)(((x >> 6) & 0x03) | (((x >> 2) & 0x03) << 4) | (((x >> 10) & 0x03) << 6) | (((x >> 14) & 0x03) << 2));
        }
    }

    /// <summary>第二层的发送方:一个连接一个对象,保存状态字节。</summary>
    public sealed class SecondLayerSender
    {
        private readonly Func<byte> _random;

        /// <summary>用系统随机数。</summary>
        public SecondLayerSender() : this(SystemRandomByte)
        {
        }

        /// <summary>
        /// 指定随机字节来源(测试里给定序列)。每次封装依次取 r1、r3;密文为空时再取一个字节当作哈希。
        /// </summary>
        public SecondLayerSender(Func<byte> randomByte)
        {
            _random = randomByte ?? throw new ArgumentNullException(nameof(randomByte));
            State = PacketIntegrity.SecondLayerInitialState;
        }

        /// <summary>当前状态字节(上一次封装用到的 r1;从未封装过是初值)。</summary>
        public byte State { get; private set; }

        /// <summary>
        /// 对一个包的密文(帧头之后的全部字节)封装:返回 低字节 | 24 位值 &lt;&lt; 8。
        /// <paramref name="reset"/> 为真时先把状态复位(见 <see cref="PacketIntegrity.ResetsStateClientToServer"/>)。
        /// </summary>
        public uint Seal(ReadOnlySpan<byte> cipherBody, byte lowByte, bool reset)
        {
            if (reset) State = PacketIntegrity.SecondLayerInitialState;
            byte r1 = _random();
            byte r3 = _random();
            byte h = cipherBody.Length > 0 ? PacketIntegrity.MakeHash(cipherBody, State) : _random();
            uint x = PacketIntegrity.Scatter(r1, h);
            State = r1;
            return lowByte | ((uint)r3 << 8) | (x << 16);
        }

        private static byte SystemRandomByte()
        {
            byte[] one = new byte[1];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(one);
            }
            return one[0];
        }
    }

    /// <summary>第二层的接收方:一个连接一个对象,保存状态字节。</summary>
    public sealed class SecondLayerReceiver
    {
        /// <summary>创建,状态为初值。</summary>
        public SecondLayerReceiver()
        {
            State = PacketIntegrity.SecondLayerInitialState;
        }

        /// <summary>当前状态字节(上一个包的 r1)。</summary>
        public byte State { get; private set; }

        /// <summary>不符的次数。</summary>
        public int Mismatches { get; private set; }

        /// <summary>
        /// 检查一个包:<paramref name="word"/> 是 4 字节校验字(高 16 位是拼好的 r1 与哈希)。
        /// 无论是否通过,状态都推进到取回的 r1;不通过时 <see cref="Mismatches"/> 加一。密文为空恒通过。
        /// </summary>
        public bool Check(ReadOnlySpan<byte> cipherBody, uint word, bool reset)
        {
            if (reset) State = PacketIntegrity.SecondLayerInitialState;
            PacketIntegrity.Unscatter((ushort)(word >> 16), out byte r1, out byte expected);
            byte previous = State;
            State = r1;
            if (cipherBody.Length == 0) return true;
            bool ok = PacketIntegrity.MakeHash(cipherBody, previous) == expected;
            if (!ok) Mismatches++;
            return ok;
        }
    }
}
