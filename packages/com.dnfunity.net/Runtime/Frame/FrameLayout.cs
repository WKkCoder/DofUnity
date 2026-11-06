using System;

namespace Dnf.Net.Frame
{
    /// <summary>
    /// 一个方向上的帧头布局:头长、各字段偏移、第二校验字段宽度、校验起点、字节序、整帧长度上下限。
    /// 做成对象而不是常量,是因为两个方向的头不同(服务端到客户端 15 字节,客户端到服务端 13 字节,
    /// 服务端 <c>PacketBuf::put_header</c> 0x0858c94a 把写游标设为 0xF,<c>PacketBuf::clear_ptr</c> 0x0858c93a 把读游标设为 0xD,E-202),
    /// 而字节序又取决于包对象的字节序成员(默认 0 = 小端,<c>put_int</c> 0x0858cad4 / <c>get_int</c> 0x0858d0f0 的两个分支)。
    /// 构造时检查字段落在头里且互不重叠,所以拿到的布局一定能读写。
    /// </summary>
    public sealed class FrameLayout
    {
        /// <summary>整帧长度的绝对上限(服务端包缓冲 95000 字节,<c>PacketBuf::set_packet</c> 0x0858d462 的上限常量)。</summary>
        public const int PacketBufferSize = 95000;

        /// <summary>服务端接收时的整帧长度下限(<c>CUser::Parsing</c> 0x08647890,E-202)。</summary>
        public const int ServerReceiveMin = 13;

        /// <summary>服务端接收时的整帧长度上限(同上,0x578)。</summary>
        public const int ServerReceiveMax = 0x578;

        /// <summary>服务端每条连接的接收缓冲大小(0x1000):一次读取的上限,不是帧长上限;流重组结果与读取切分无关。</summary>
        public const int ServerReceiveBufferSize = 0x1000;

        private static readonly FrameLayout s_serverToClient = new FrameLayout(
            "服务端到客户端", headerLength: 15, classOffset: 0, idOffset: 1, lengthOffset: 3,
            checkOffset: 7, secondOffset: 11, secondWidth: 4, checksumStart: 15,
            bigEndian: false, minFrameLength: 15, maxFrameLength: PacketBufferSize);

        private static readonly FrameLayout s_clientToServer = new FrameLayout(
            "客户端到服务端", headerLength: 13, classOffset: 0, idOffset: 1, lengthOffset: 3,
            checkOffset: 7, secondOffset: 11, secondWidth: 2, checksumStart: 11,
            bigEndian: false, minFrameLength: ServerReceiveMin, maxFrameLength: ServerReceiveMax);

        /// <summary>
        /// 服务端发出的帧:15 字节头(类 1、包号 2、整帧长 4、校验 a 4、校验 b 4),密文自偏移 0xF(E-202;
        /// <c>PacketBuf::finalize</c> 0x0858d548、<c>encFinalize</c> 0x0858df30)。长度上限取服务端包缓冲大小(推断:服务端发不出更大的帧)。
        /// </summary>
        public static FrameLayout ServerToClient => s_serverToClient;

        /// <summary>
        /// 客户端发出的帧(服务端 <c>CUser::Parsing</c> 0x08647890 读的那种):13 字节头(类 1、包号 2、整帧长 4、校验字 4、序号 2),
        /// 密文自偏移 0xD;整帧长 13 到 0x578。校验字低字节覆盖偏移 0xB 起的 2 字节序号与解密后的明文(E-203)。
        /// </summary>
        public static FrameLayout ClientToServer => s_clientToServer;

        /// <summary>布局名字(只用于诊断)。</summary>
        public string Name { get; }

        /// <summary>头长(字节)。</summary>
        public int HeaderLength { get; }

        /// <summary>类字段偏移(1 字节)。</summary>
        public int ClassOffset { get; }

        /// <summary>包号字段偏移(2 字节)。</summary>
        public int IdOffset { get; }

        /// <summary>整帧长度字段偏移(4 字节,含头)。</summary>
        public int LengthOffset { get; }

        /// <summary>第一校验字段偏移(4 字节)。</summary>
        public int CheckOffset { get; }

        /// <summary>第二字段偏移(服务端到客户端是校验 b,客户端到服务端是序号)。</summary>
        public int SecondOffset { get; }

        /// <summary>第二字段宽度(0、2 或 4 字节)。</summary>
        public int SecondWidth { get; }

        /// <summary>校验覆盖范围的起点(到整帧末尾)。</summary>
        public int ChecksumStart { get; }

        /// <summary>多字节字段是否大端。两个方向都读到小端;留成参数是因为"字节序成员从不被改成 1"只是未找到写入点的推断。</summary>
        public bool BigEndian { get; }

        /// <summary>整帧长度下限(含)。</summary>
        public int MinFrameLength { get; }

        /// <summary>整帧长度上限(含)。</summary>
        public int MaxFrameLength { get; }

        /// <summary>密文起点:头后面第一个字节。</summary>
        public int CipherOffset => HeaderLength;

        /// <summary>按字段自定义一个布局;参数不自洽时抛 <see cref="ArgumentException"/>。</summary>
        public FrameLayout(
            string name, int headerLength, int classOffset, int idOffset, int lengthOffset,
            int checkOffset, int secondOffset, int secondWidth, int checksumStart,
            bool bigEndian, int minFrameLength, int maxFrameLength)
        {
            if (secondWidth != 0 && secondWidth != 2 && secondWidth != 4)
            {
                throw new ArgumentException("第二字段宽度只能是 0、2、4", nameof(secondWidth));
            }
            // 字段区间 [起, 止):类 1 字节、包号 2、长度 4、校验 4、第二字段 secondWidth
            int[][] spans =
            {
                new[] { classOffset, classOffset + 1 },
                new[] { idOffset, idOffset + 2 },
                new[] { lengthOffset, lengthOffset + 4 },
                new[] { checkOffset, checkOffset + 4 },
                new[] { secondOffset, secondOffset + secondWidth },
            };
            for (int i = 0; i < spans.Length; i++)
            {
                if (spans[i][0] < 0 || spans[i][1] > headerLength)
                {
                    throw new ArgumentException("字段超出帧头", nameof(headerLength));
                }
                for (int j = i + 1; j < spans.Length; j++)
                {
                    bool apart = spans[i][1] <= spans[j][0] || spans[j][1] <= spans[i][0];
                    // 宽度为 0 的第二字段不占位,不参与重叠判断
                    if (!apart && spans[i][0] != spans[i][1] && spans[j][0] != spans[j][1])
                    {
                        throw new ArgumentException("帧头字段重叠");
                    }
                }
            }
            if (checksumStart < 0 || checksumStart > headerLength)
            {
                throw new ArgumentException("校验起点必须落在头内或正好是头尾", nameof(checksumStart));
            }
            if (minFrameLength < headerLength)
            {
                throw new ArgumentException("整帧长度下限不能小于头长", nameof(minFrameLength));
            }
            if (maxFrameLength < minFrameLength)
            {
                throw new ArgumentException("整帧长度上限不能小于下限", nameof(maxFrameLength));
            }
            Name = name ?? string.Empty;
            HeaderLength = headerLength;
            ClassOffset = classOffset;
            IdOffset = idOffset;
            LengthOffset = lengthOffset;
            CheckOffset = checkOffset;
            SecondOffset = secondOffset;
            SecondWidth = secondWidth;
            ChecksumStart = checksumStart;
            BigEndian = bigEndian;
            MinFrameLength = minFrameLength;
            MaxFrameLength = maxFrameLength;
        }

        /// <summary>返回字节序相反的同一布局(测试"字节序可配置"与另一种假设用)。</summary>
        public FrameLayout WithByteOrder(bool bigEndian)
        {
            return new FrameLayout(
                Name, HeaderLength, ClassOffset, IdOffset, LengthOffset, CheckOffset, SecondOffset,
                SecondWidth, ChecksumStart, bigEndian, MinFrameLength, MaxFrameLength);
        }

        /// <summary>返回改了整帧长度上下限的同一布局。</summary>
        public FrameLayout WithLengthLimits(int minFrameLength, int maxFrameLength)
        {
            return new FrameLayout(
                Name, HeaderLength, ClassOffset, IdOffset, LengthOffset, CheckOffset, SecondOffset,
                SecondWidth, ChecksumStart, BigEndian, minFrameLength, maxFrameLength);
        }
    }
}
