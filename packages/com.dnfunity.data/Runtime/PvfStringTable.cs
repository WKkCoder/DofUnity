using System;
using System.Buffers.Binary;

namespace Dnf.Data
{
    /// <summary>
    /// stringtable.bin:u32 count,随后 count+1 个 u32 偏移(相对第 4 字节),再往后是字符串池,字符串无终止符。
    /// 第 i 个字符串 = data[4 + off[i] .. 4 + off[i+1])。标记流脚本里的标签、路径与键都是这里的下标。
    /// 在真实数据里:第一个偏移恰好等于偏移表长度,最后一个偏移恰好指到文件末尾,偏移单调不减。
    /// </summary>
    public sealed class PvfStringTable
    {
        private readonly byte[] _data;
        private readonly int[] _offsets;
        private readonly string[] _cache;

        public int Count { get; }

        public PvfStringTable(byte[] data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            if (data.Length < 8)
            {
                throw new DnfFormatException("stringtable 太短: " + data.Length);
            }
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (((long)n + 1) * 4 + 4 > data.Length)
            {
                throw new DnfFormatException("stringtable 偏移表超出文件: count=" + n);
            }
            Count = (int)n;
            _offsets = new int[Count + 1];
            long previous = 0;
            for (int i = 0; i <= Count; i++)
            {
                uint off = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + 4 * i));
                if (off < previous || 4L + off > data.Length)
                {
                    throw new DnfFormatException("stringtable 偏移 " + i + " 不合理: " + off);
                }
                _offsets[i] = (int)off;
                previous = off;
            }
            _cache = new string[Count];
        }

        public ReadOnlySpan<byte> GetBytes(int index)
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            int start = 4 + _offsets[index];
            int end = 4 + _offsets[index + 1];
            return new ReadOnlySpan<byte>(_data, start, end - start);
        }

        /// <summary>按 Latin1 解码并缓存。标签名、路径、键都是 ASCII。</summary>
        public string this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }
                string s = _cache[index];
                if (s == null)
                {
                    s = Latin1.Decode(GetBytes(index));
                    _cache[index] = s;
                }
                return s;
            }
        }
    }
}
