using System;

namespace Dnf.Net.Frame
{
    /// <summary>折叠哈希核对结果。</summary>
    public enum FoldHashStatus
    {
        /// <summary>游标之后不足 4 字节,或已读区间长度不在 1 到 400(这种长度服务端不追加哈希)。</summary>
        Missing = 0,

        /// <summary>尾 4 字节等于已读区间的折叠哈希。</summary>
        Match = 1,

        /// <summary>尾 4 字节与已读区间的折叠哈希不等。</summary>
        Mismatch = 2,
    }

    /// <summary>
    /// 载荷读取器:照服务端 <c>PacketBuf</c> 的 get 原语读字节(0x0858cf22 起,E-202、E-206)。
    /// 每个原语先检查"游标 + 所需字节 &gt; 长度"(<c>is_len_end</c> 0x0858cefc),不够就失败、不动游标。
    /// 整数按字节序成员读(默认小端)。<see cref="TryGetString"/> 是 <c>get_str</c>(0x0858d2bc)的原样复刻,有两个容易踩的规则:
    /// 缓冲大小必须严格大于读取长度(给结尾 NUL 留位置);读到 0 字节就提前停下,游标停在该 0 之后,剩余的字节留在流里(不会被吞掉)。
    /// 与原函数不同的一处:读取长度为负数时原函数什么也不读却返回成功,这里按失败处理(线上不会出现负长度,不属于编码规则)。
    /// </summary>
    public sealed class PacketReader
    {
        private readonly byte[] _buffer;
        private readonly bool _bigEndian;

        /// <summary>
        /// 读取 <paramref name="buffer"/> 里 [0, <paramref name="length"/>) 的内容,游标从 <paramref name="position"/> 起。
        /// 服务端读取收到的帧时游标从 0xD 起(<c>clear_ptr</c> 0x0858c93a),所以起点与长度都按绝对位置给。
        /// </summary>
        public PacketReader(byte[] buffer, int position, int length, bool bigEndian = false)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (length < 0 || length > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }
            if (position < 0 || position > length)
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }
            _buffer = buffer;
            Position = position;
            Length = length;
            _bigEndian = bigEndian;
        }

        /// <summary>读整个数组,游标从 0 起。</summary>
        public PacketReader(byte[] buffer)
            : this(buffer, 0, buffer == null ? 0 : buffer.Length)
        {
        }

        /// <summary>当前游标(绝对位置)。</summary>
        public int Position { get; private set; }

        /// <summary>可读内容的结束位置(不含)。</summary>
        public int Length { get; }

        /// <summary>还能读多少字节。</summary>
        public int Remaining => Length - Position;

        /// <summary>读 1 个字节。</summary>
        public bool TryGetByte(out byte value)
        {
            value = 0;
            if (!Has(1))
            {
                return false;
            }
            value = _buffer[Position++];
            return true;
        }

        /// <summary>读 2 字节有符号整数。</summary>
        public bool TryGetShort(out short value)
        {
            bool ok = TryGetUShort(out ushort raw);
            value = (short)raw;
            return ok;
        }

        /// <summary>读 2 字节无符号整数。</summary>
        public bool TryGetUShort(out ushort value)
        {
            value = 0;
            if (!Has(2))
            {
                return false;
            }
            value = (ushort)PacketHeader.ReadUInt(_buffer, Position, 2, _bigEndian);
            Position += 2;
            return true;
        }

        /// <summary>读 4 字节有符号整数。</summary>
        public bool TryGetInt(out int value)
        {
            bool ok = TryGetUInt(out uint raw);
            value = (int)raw;
            return ok;
        }

        /// <summary>读 4 字节无符号整数。</summary>
        public bool TryGetUInt(out uint value)
        {
            value = 0;
            if (!Has(4))
            {
                return false;
            }
            value = PacketHeader.ReadUInt(_buffer, Position, 4, _bigEndian);
            Position += 4;
            return true;
        }

        /// <summary>读二进制块:原样取 <paramref name="count"/> 字节;<paramref name="count"/> 不大于 0 失败(原函数对 0 失败,0x0858d3b2)。</summary>
        public bool TryGetBinary(int count, out byte[] value)
        {
            value = null;
            if (count <= 0 || !Has(count))
            {
                return false;
            }
            value = new byte[count];
            Buffer.BlockCopy(_buffer, Position, value, 0, count);
            Position += count;
            return true;
        }

        /// <summary>
        /// <c>get_str</c> 的复刻:最多取 <paramref name="length"/> 字节,遇到 0 字节提前结束。
        /// <paramref name="bufferSize"/> 是调用者缓冲的大小,必须严格大于 <paramref name="length"/>,否则失败。
        /// <paramref name="length"/> 为 0 时成功且不读任何字节,结果是空数组。
        /// 成功时游标前进的字节数 = 读到的字节数,若遇到 0 则再加这个 0 本身(不会跳过它后面的字节)。
        /// </summary>
        public bool TryGetString(int bufferSize, int length, out byte[] value)
        {
            value = null;
            if (length < 0 || bufferSize < 0 || length >= bufferSize || !Has(length))
            {
                return false;
            }
            int start = Position;
            int n = 0;
            while (n < length)
            {
                byte b = _buffer[Position++];
                if (b == 0)
                {
                    break;
                }
                n++;
            }
            value = new byte[n];
            Buffer.BlockCopy(_buffer, start, value, 0, n);
            return true;
        }

        /// <summary>
        /// 核对游标之后的 4 字节是否等于 [<paramref name="hashedFrom"/>, 游标) 的折叠哈希(服务端对 17 个命令包号做的第一层包完整性检查,
        /// <c>_NS_PI_Check_CMD</c> 0x08274778)。不移动游标。
        /// </summary>
        public FoldHashStatus CheckFoldHash(int hashedFrom)
        {
            int hashed = Position - hashedFrom;
            if (hashedFrom < 0 || hashed < 1 || hashed > PacketIntegrity.FoldHashMaxPayload || !Has(4))
            {
                return FoldHashStatus.Missing;
            }
            uint expected = PacketIntegrity.FoldHash(new ReadOnlySpan<byte>(_buffer, hashedFrom, hashed));
            uint actual = PacketHeader.ReadUInt(_buffer, Position, 4, _bigEndian);
            return expected == actual ? FoldHashStatus.Match : FoldHashStatus.Mismatch;
        }

        private bool Has(int count)
        {
            return count >= 0 && Position + count <= Length;
        }
    }
}
