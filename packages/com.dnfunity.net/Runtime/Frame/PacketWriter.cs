using System;

namespace Dnf.Net.Frame
{
    /// <summary>
    /// 载荷写入器:照服务端 <c>PacketBuf</c> 的 put 原语写字节(0x0858c978 起,E-202、E-206)。
    /// 编码事实(都读自反编译,并由本机模拟器对原函数的向量逐项核对):
    /// 字节、短整数、整数按字节序成员写 1、2、4 字节(默认 0 = 小端);字符串原语只写字节,既不写长度前缀、也不写结尾 NUL,
    /// 写到遇到 NUL 或达到最大字节数为止;二进制块原语只写字节,不写长度前缀,空块失败。
    /// 长度前缀由调用者另外用整数原语写(比如频道名先写整数长度再写字节)。
    /// 每个原语先检查"游标 + 本次所需字节 &gt; 上限"(<c>is_ptr_end</c> 0x0858cec4,上限 0x172b4),超了整个原语失败、不写任何字节、不动游标;
    /// 字符串原语检查的是"最大字节数"而不是实际写入的字节数。
    /// </summary>
    public sealed class PacketWriter
    {
        /// <summary>写游标上限:游标加本次所需字节超过它就失败(0x0858cec4 里的立即数)。</summary>
        public const int DefaultLimit = 0x172b4;

        private readonly bool _bigEndian;
        private readonly int _limit;
        private readonly int _start;
        private byte[] _buffer;
        private int _position;

        /// <summary>
        /// 创建写入器。<paramref name="startPosition"/> 是游标起点(服务端发包头占 0xF 字节,写游标从 0xF 起,<c>put_header</c> 0x0858c94a;
        /// 上限按绝对游标比较,所以起点会占用额度);<see cref="ToArray"/> 只返回起点之后写的字节。
        /// </summary>
        public PacketWriter(int startPosition, bool bigEndian = false, int limit = DefaultLimit)
        {
            if (startPosition < 0 || startPosition > limit)
            {
                throw new ArgumentOutOfRangeException(nameof(startPosition));
            }
            _start = startPosition;
            _position = startPosition;
            _bigEndian = bigEndian;
            _limit = limit;
            _buffer = new byte[Math.Max(64, startPosition + 64)];
        }

        /// <summary>按布局的头长作起点创建(头由帧层另写)。</summary>
        public static PacketWriter ForLayout(FrameLayout layout, int limit = DefaultLimit)
        {
            if (layout == null)
            {
                throw new ArgumentNullException(nameof(layout));
            }
            return new PacketWriter(layout.HeaderLength, layout.BigEndian, limit);
        }

        /// <summary>当前游标(绝对位置)。</summary>
        public int Position => _position;

        /// <summary>起点之后已写入的字节数。</summary>
        public int Length => _position - _start;

        /// <summary>写 1 个字节(取 <paramref name="value"/> 的低 8 位)。</summary>
        public bool PutByte(int value)
        {
            if (!Reserve(1))
            {
                return false;
            }
            _buffer[_position++] = (byte)value;
            return true;
        }

        /// <summary>写 2 字节整数(取低 16 位)。</summary>
        public bool PutShort(int value)
        {
            if (!Reserve(2))
            {
                return false;
            }
            PutBytes((uint)value, 2);
            return true;
        }

        /// <summary>写 4 字节整数。</summary>
        public bool PutInt(int value)
        {
            return PutUInt((uint)value);
        }

        /// <summary>写 4 字节无符号整数。</summary>
        public bool PutUInt(uint value)
        {
            if (!Reserve(4))
            {
                return false;
            }
            PutBytes(value, 4);
            return true;
        }

        /// <summary>
        /// 写字符串字节:最多 <paramref name="maxLength"/> 个,遇到 0 字节停止(不含该 0)。没有长度前缀,没有结尾 NUL。
        /// 空间检查按 <paramref name="maxLength"/> 而不是实际长度(0x0858cc68)。
        /// </summary>
        public bool PutString(ReadOnlySpan<byte> text, int maxLength)
        {
            if (maxLength < 0 || !Reserve(maxLength))
            {
                return false;
            }
            for (int i = 0; i < maxLength && i < text.Length && text[i] != 0; i++)
            {
                _buffer[_position++] = text[i];
            }
            return true;
        }

        /// <summary>写二进制块:原样写入,没有长度前缀;空块失败(0x0858ce16 记日志后返回假)。</summary>
        public bool PutBinary(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0 || !Reserve(data.Length))
            {
                return false;
            }
            data.CopyTo(new Span<byte>(_buffer, _position, data.Length));
            _position += data.Length;
            return true;
        }

        /// <summary>起点之后写入的字节(副本)。</summary>
        public byte[] ToArray()
        {
            byte[] result = new byte[_position - _start];
            Buffer.BlockCopy(_buffer, _start, result, 0, result.Length);
            return result;
        }

        // 照 is_ptr_end:上限以绝对游标计;失败什么都不写
        private bool Reserve(int count)
        {
            if (count < 0 || _position + count > _limit)
            {
                return false;
            }
            int needed = _position + count;
            if (needed > _buffer.Length)
            {
                int size = _buffer.Length;
                while (size < needed)
                {
                    size *= 2;
                }
                Array.Resize(ref _buffer, size);
            }
            return true;
        }

        private void PutBytes(uint value, int width)
        {
            for (int i = 0; i < width; i++)
            {
                int shift = _bigEndian ? (width - 1 - i) * 8 : i * 8;
                _buffer[_position++] = (byte)(value >> shift);
            }
        }
    }
}
