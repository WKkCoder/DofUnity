using System;

namespace Dnf.Net.Frame
{
    /// <summary>
    /// 一个帧头的各字段值(字节位置与字节序由 <see cref="FrameLayout"/> 决定)。
    /// 第一校验字、第二字段的含义随方向不同:服务端到客户端是两个 4 字节校验;客户端到服务端是 4 字节校验字(低字节 = 校验,高 3 字节 = 包完整性第二层)
    /// 与 2 字节序号。这里只当整数存取,不解释。
    /// </summary>
    public struct PacketHeader : IEquatable<PacketHeader>
    {
        /// <summary>包类;服务端接收时不按类拒收,所以保留原始字节。</summary>
        public byte ClassByte;

        /// <summary>包号(2 字节)。</summary>
        public ushort PacketId;

        /// <summary>整帧长度(含头)。</summary>
        public uint TotalLength;

        /// <summary>第一校验字(偏移由布局决定,4 字节)。</summary>
        public uint Check;

        /// <summary>第二字段(宽度由布局决定,0、2 或 4 字节;宽度为 0 时恒为 0)。</summary>
        public uint Second;

        /// <summary>包类的枚举视图。</summary>
        public PacketClass Class
        {
            get => (PacketClass)ClassByte;
            set => ClassByte = (byte)value;
        }

        /// <summary>
        /// 从 <paramref name="data"/> 起点读头;不足 <see cref="FrameLayout.HeaderLength"/> 字节返回 false(不抛异常)。
        /// 只读字段,不检查长度范围(范围由 <see cref="FrameAssembler"/> 检查)。
        /// </summary>
        public static bool TryRead(FrameLayout layout, ReadOnlySpan<byte> data, out PacketHeader header)
        {
            if (layout == null)
            {
                throw new ArgumentNullException(nameof(layout));
            }
            header = default;
            if (data.Length < layout.HeaderLength)
            {
                return false;
            }
            header.ClassByte = data[layout.ClassOffset];
            header.PacketId = (ushort)ReadUInt(data, layout.IdOffset, 2, layout.BigEndian);
            header.TotalLength = ReadUInt(data, layout.LengthOffset, 4, layout.BigEndian);
            header.Check = ReadUInt(data, layout.CheckOffset, 4, layout.BigEndian);
            header.Second = layout.SecondWidth == 0 ? 0u : ReadUInt(data, layout.SecondOffset, layout.SecondWidth, layout.BigEndian);
            return true;
        }

        /// <summary>
        /// 把头写进 <paramref name="dest"/> 起点(只覆盖布局里的字段,别的字节不动);空间不足返回 false。
        /// 第二字段的值超出宽度时只写低位(2 字节宽度写低 16 位)。
        /// </summary>
        public bool TryWrite(FrameLayout layout, Span<byte> dest)
        {
            if (layout == null)
            {
                throw new ArgumentNullException(nameof(layout));
            }
            if (dest.Length < layout.HeaderLength)
            {
                return false;
            }
            dest[layout.ClassOffset] = ClassByte;
            WriteUInt(dest, layout.IdOffset, 2, layout.BigEndian, PacketId);
            WriteUInt(dest, layout.LengthOffset, 4, layout.BigEndian, TotalLength);
            WriteUInt(dest, layout.CheckOffset, 4, layout.BigEndian, Check);
            if (layout.SecondWidth != 0)
            {
                WriteUInt(dest, layout.SecondOffset, layout.SecondWidth, layout.BigEndian, Second);
            }
            return true;
        }

        internal static uint ReadUInt(ReadOnlySpan<byte> data, int offset, int width, bool bigEndian)
        {
            uint v = 0;
            if (bigEndian)
            {
                for (int i = 0; i < width; i++)
                {
                    v = (v << 8) | data[offset + i];
                }
            }
            else
            {
                for (int i = width - 1; i >= 0; i--)
                {
                    v = (v << 8) | data[offset + i];
                }
            }
            return v;
        }

        internal static void WriteUInt(Span<byte> dest, int offset, int width, bool bigEndian, uint value)
        {
            for (int i = 0; i < width; i++)
            {
                int shift = bigEndian ? (width - 1 - i) * 8 : i * 8;
                dest[offset + i] = (byte)(value >> shift);
            }
        }

        /// <inheritdoc />
        public bool Equals(PacketHeader other)
        {
            return ClassByte == other.ClassByte && PacketId == other.PacketId && TotalLength == other.TotalLength
                && Check == other.Check && Second == other.Second;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is PacketHeader other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                int h = ClassByte;
                h = (h * 397) ^ PacketId;
                h = (h * 397) ^ (int)TotalLength;
                h = (h * 397) ^ (int)Check;
                h = (h * 397) ^ (int)Second;
                return h;
            }
        }
    }
}
