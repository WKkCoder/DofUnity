using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

// 证据:NPK 与 IMG 容器的布局见 E-002;与 Python 参考实现的全库对拍见 E-008。
namespace Dnf.Data
{
    public readonly struct NpkEntry
    {
        public string Name { get; }
        public uint Offset { get; }
        public uint Size { get; }

        public NpkEntry(string name, uint offset, uint size)
        {
            Name = name;
            Offset = offset;
            Size = size;
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// 只读的 NPK 包:"NeoplePack_Bill\0"(16 字节)+ u32 count + count * 264 字节目录项。
    /// 目录项:u32 offset, u32 size, 256 字节名称。名称与固定密钥逐字节异或,
    /// 密钥 = ("puchikon@neople dungeon and fighter " + "DNF" * 100) 的前 255 个字符再加一个 NUL。
    /// 线程安全:读取时对底层流加锁。
    /// </summary>
    public sealed class NpkArchive : IDisposable
    {
        private const int HeaderLength = 20;
        private const int EntryLength = 264;
        private const int NameLength = 256;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NeoplePack_Bill\0");
        private static readonly byte[] NameKey = BuildNameKey();

        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly object _gate = new object();
        private readonly NpkEntry[] _entries;

        public IReadOnlyList<NpkEntry> Entries => _entries;

        public int Count => _entries.Length;

        public static NpkArchive Open(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            try
            {
                return new NpkArchive(fs, false);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        public NpkArchive(Stream stream, bool leaveOpen = false)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (!stream.CanSeek || !stream.CanRead)
            {
                throw new ArgumentException("需要可读且可随机访问的流", nameof(stream));
            }
            _leaveOpen = leaveOpen;
            long fileLength = stream.Length;

            var head = new byte[HeaderLength];
            if (ReadAt(0, head, 0, HeaderLength) != HeaderLength)
            {
                throw new DnfFormatException("NPK 头被截断");
            }
            for (int i = 0; i < Magic.Length; i++)
            {
                if (head[i] != Magic[i])
                {
                    throw new DnfFormatException("不是 NPK:魔数不对");
                }
            }
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(16));
            long tocLength = (long)count * EntryLength;
            if (tocLength + HeaderLength > fileLength || tocLength > int.MaxValue)
            {
                throw new DnfFormatException("NPK 目录表超出文件: count=" + count);
            }
            var toc = new byte[(int)tocLength];
            if (ReadAt(HeaderLength, toc, 0, toc.Length) != toc.Length)
            {
                throw new DnfFormatException("NPK 目录表被截断");
            }
            _entries = new NpkEntry[count];
            var name = new byte[NameLength];
            for (int i = 0; i < _entries.Length; i++)
            {
                int at = i * EntryLength;
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(at));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(toc.AsSpan(at + 4));
                int length = 0;
                bool ended = false;
                for (int k = 0; k < NameLength; k++)
                {
                    byte b = (byte)(toc[at + 8 + k] ^ NameKey[k]);
                    name[k] = b;
                    if (!ended && b == 0)
                    {
                        length = k;
                        ended = true;
                    }
                }
                if (!ended)
                {
                    length = NameLength;
                }
                _entries[i] = new NpkEntry(Latin1.Decode(name.AsSpan(0, length)), offset, size);
            }
        }

        private static byte[] BuildNameKey()
        {
            var sb = new StringBuilder("puchikon@neople dungeon and fighter ");
            for (int i = 0; i < 100; i++)
            {
                sb.Append("DNF");
            }
            var key = new byte[NameLength];
            for (int i = 0; i < NameLength - 1; i++)
            {
                key[i] = (byte)sb[i];
            }
            key[NameLength - 1] = 0;
            return key;
        }

        /// <summary>读取一个条目的原始字节(通常是一个 IMG)。</summary>
        public byte[] Read(NpkEntry entry)
        {
            if (entry.Size > int.MaxValue)
            {
                throw new DnfFormatException("NPK 条目过大: " + entry.Name);
            }
            var buffer = new byte[entry.Size];
            int got = ReadAt(entry.Offset, buffer, 0, buffer.Length);
            if (got != buffer.Length)
            {
                throw new DnfFormatException("NPK 条目数据被截断: " + entry.Name + " 需要 " + buffer.Length + " 实得 " + got);
            }
            return buffer;
        }

        private int ReadAt(long position, byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                _stream.Seek(position, SeekOrigin.Begin);
                int total = 0;
                while (total < count)
                {
                    int n = _stream.Read(buffer, offset + total, count - total);
                    if (n <= 0)
                    {
                        break;
                    }
                    total += n;
                }
                return total;
            }
        }

        public void Dispose()
        {
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }
}
