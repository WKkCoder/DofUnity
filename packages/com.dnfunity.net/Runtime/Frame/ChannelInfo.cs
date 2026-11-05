using System;
using System.Collections.Generic;

namespace Dnf.Net.Frame
{
    /// <summary>解析 CHANNELINFO 载荷的结果。</summary>
    public enum ChannelInfoStatus
    {
        /// <summary>成功。</summary>
        Ok = 0,

        /// <summary>数据在某个字段中间结束。</summary>
        Truncated = 1,

        /// <summary>频道名长度为负或超出剩余字节。</summary>
        BadNameLength = 2,

        /// <summary>地址个数为负,或按每个至少 4 字节算已超出剩余字节。</summary>
        BadAddressCount = 3,

        /// <summary>某个地址元素的长度为负。</summary>
        BadAddressLength = 4,
    }

    /// <summary>CHANNELINFO 里的一个地址元素:服务端写的长度整数(完整 strlen)与实际写出的字节(至多 16 个)。</summary>
    public sealed class ChannelAddress
    {
        /// <summary>服务端写的长度整数。</summary>
        public int DeclaredLength { get; }

        /// <summary>实际随后的字节;个数是 <see cref="DeclaredLength"/> 与 16 的较小者。</summary>
        public byte[] Bytes { get; }

        /// <summary>构造。</summary>
        public ChannelAddress(int declaredLength, byte[] bytes)
        {
            DeclaredLength = declaredLength;
            Bytes = bytes;
        }
    }

    /// <summary>
    /// 连接建立后服务端主动发的第一个包(类 0、包号 1,CHANNELINFO)解开固定种子变换后的载荷(E-201,<c>CUser::InitOnAccept</c> 0x086493c8)。
    /// 载荷顺序:334 字节会话密钥块(<c>Cipher::GetOriginalKey</c> 0x080975de 原样拷出)、频道名(整数长度 + 字节)、日期种子(整数)、随机数(整数)、
    /// 三个环境字节、当前秒数(整数)、地址个数(整数)、每个地址(整数长度 + 至多 16 字节)、个数不为 0 时再两个整数、两个常量整数(命令表与通知表的结束下标)。
    /// 载荷不超过 400 字节时末尾还有 4 字节折叠哈希(见 <see cref="PacketIntegrity"/>),这里不核对,当作 <see cref="Trailing"/> 交给调用者。
    /// </summary>
    public sealed class ChannelInfo
    {
        /// <summary>会话密钥块的长度(与 <c>CipherSlots.TotalKeyLength</c> 相同;E-262)。</summary>
        public const int KeyBlockLength = 334;

        /// <summary>地址元素最多写出的字节数(服务端 <c>put_str</c> 的上限参数 0x10)。</summary>
        public const int AddressMaxBytes = 16;

        /// <summary>会话密钥块(334 字节)。</summary>
        public byte[] KeyBlock { get; private set; }

        /// <summary>频道名字节(没有结尾 NUL;编码未知,调用者自己解释)。</summary>
        public byte[] ChannelName { get; private set; }

        /// <summary>日期种子。</summary>
        public int DateSeed { get; private set; }

        /// <summary>随机数(服务端写进用户对象的那个值)。</summary>
        public int RandomValue { get; private set; }

        /// <summary>三个环境字节(语义未知)。</summary>
        public byte[] EnvironmentBytes { get; private set; }

        /// <summary>服务端当前时间(秒)。</summary>
        public int ServerTime { get; private set; }

        /// <summary>地址列表;可以为空。</summary>
        public IReadOnlyList<ChannelAddress> Addresses { get; private set; }

        /// <summary>地址列表非空时随后的两个整数(语义未知);列表为空时没有,两个值都是 0 且 <see cref="HasAddressTail"/> 为假。</summary>
        public int[] AddressTail { get; private set; }

        /// <summary>是否读到了地址列表之后的两个整数。</summary>
        public bool HasAddressTail { get; private set; }

        /// <summary>第一个结尾常量(命令包名表的结束下标)。</summary>
        public int CommandTableEnd { get; private set; }

        /// <summary>第二个结尾常量(通知包名表的结束下标)。</summary>
        public int NotiTableEnd { get; private set; }

        /// <summary>两个结尾常量之后剩下的字节(通常是 4 字节折叠哈希,或补齐)。</summary>
        public byte[] Trailing { get; private set; }

        /// <summary>
        /// 从 <paramref name="payload"/> 的 [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="count"/>) 解析。
        /// 任何截断或不合理的长度都返回对应状态(不抛异常);失败时 <paramref name="info"/> 为 null。
        /// </summary>
        public static ChannelInfoStatus TryParse(byte[] payload, int offset, int count, out ChannelInfo info)
        {
            info = null;
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }
            if (offset < 0 || count < 0 || offset > payload.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            byte[] body = new byte[count];
            Buffer.BlockCopy(payload, offset, body, 0, count);
            PacketReader r = new PacketReader(body);
            ChannelInfo c = new ChannelInfo();

            if (!r.TryGetBinary(KeyBlockLength, out byte[] key))
            {
                return ChannelInfoStatus.Truncated;
            }
            c.KeyBlock = key;

            if (!r.TryGetInt(out int nameLength))
            {
                return ChannelInfoStatus.Truncated;
            }
            if (nameLength < 0 || nameLength > r.Remaining)
            {
                return ChannelInfoStatus.BadNameLength;
            }
            c.ChannelName = ReadRaw(r, nameLength);

            int seed, rnd, time, n;
            if (!r.TryGetInt(out seed) || !r.TryGetInt(out rnd))
            {
                return ChannelInfoStatus.Truncated;
            }
            c.DateSeed = seed;
            c.RandomValue = rnd;

            byte[] env = new byte[3];
            for (int i = 0; i < env.Length; i++)
            {
                if (!r.TryGetByte(out env[i]))
                {
                    return ChannelInfoStatus.Truncated;
                }
            }
            c.EnvironmentBytes = env;

            if (!r.TryGetInt(out time) || !r.TryGetInt(out n))
            {
                return ChannelInfoStatus.Truncated;
            }
            c.ServerTime = time;
            // 每个地址元素至少 4 字节(长度整数),个数大得离谱就是坏数据,别按它去分配
            if (n < 0 || (long)n * 4 > r.Remaining)
            {
                return ChannelInfoStatus.BadAddressCount;
            }
            List<ChannelAddress> list = new List<ChannelAddress>(n);
            for (int i = 0; i < n; i++)
            {
                if (!r.TryGetInt(out int declared))
                {
                    return ChannelInfoStatus.Truncated;
                }
                if (declared < 0)
                {
                    return ChannelInfoStatus.BadAddressLength;
                }
                int take = Math.Min(declared, AddressMaxBytes);
                if (take > r.Remaining)
                {
                    return ChannelInfoStatus.Truncated;
                }
                list.Add(new ChannelAddress(declared, ReadRaw(r, take)));
            }
            c.Addresses = list;

            c.AddressTail = new int[2];
            if (n != 0)
            {
                if (!r.TryGetInt(out int t0) || !r.TryGetInt(out int t1))
                {
                    return ChannelInfoStatus.Truncated;
                }
                c.AddressTail[0] = t0;
                c.AddressTail[1] = t1;
                c.HasAddressTail = true;
            }

            if (!r.TryGetInt(out int e0) || !r.TryGetInt(out int e1))
            {
                return ChannelInfoStatus.Truncated;
            }
            c.CommandTableEnd = e0;
            c.NotiTableEnd = e1;
            c.Trailing = ReadRaw(r, r.Remaining);
            info = c;
            return ChannelInfoStatus.Ok;
        }

        // 读 count 个原始字节(count 为 0 返回空数组;调用前已确认够读)
        private static byte[] ReadRaw(PacketReader r, int count)
        {
            if (count == 0)
            {
                return new byte[0];
            }
            r.TryGetBinary(count, out byte[] bytes);
            return bytes;
        }
    }
}
