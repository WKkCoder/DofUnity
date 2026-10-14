using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Dnf.Data
{
    /// <summary>
    /// PVF 目录项。<see cref="Crc"/> 同时是解密密钥与校验值:解密后(含对齐填充)的整段明文做 CRC-32,
    /// 种子取 <see cref="FileNumber"/>,结果等于 <see cref="Crc"/>。
    /// </summary>
    public readonly struct PvfEntry
    {
        public string Path { get; }
        public uint FileNumber { get; }
        public uint Length { get; }
        public uint Crc { get; }
        public uint Offset { get; }

        public PvfEntry(string path, uint fileNumber, uint length, uint crc, uint offset)
        {
            Path = path;
            FileNumber = fileNumber;
            Length = length;
            Crc = crc;
            Offset = offset;
        }

        public override string ToString() => Path;
    }

    /// <summary>
    /// 只读的 PVF(Script.pvf)包。
    /// 头:u32 guidLen, guid, u32 version, u32 dirTreeLen, u32 dirTreeCrc, u32 nFiles。
    /// 目录项:u32 fileNo, u32 pathLen, path, u32 length, u32 crc, u32 relOffset。
    /// 文件数据起点 = 头长 + dirTreeLen + relOffset,读取长度向上对齐到 4 字节,解密后截到 length。
    /// 线程安全:读取时对底层流加锁。
    /// </summary>
    public sealed class PvfArchive : IDisposable
    {
        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly object _gate = new object();
        private readonly long _dataBase;
        private readonly PvfEntry[] _entries;
        private Dictionary<string, int> _index;

        public byte[] GuidBytes { get; }
        public uint Version { get; }
        public uint DirectoryCrc { get; }

        /// <summary>目录树解析完后多出的字节数。真实数据里是 1 个零字节:把目录树补齐到 4 字节倍数的填充,校验和覆盖它。</summary>
        public int DirectoryTrailingBytes { get; }

        /// <summary>
        /// 目录树的校验是否通过:长度是 4 的倍数,且解密后整段的 CRC-32(种子 = 文件数)等于头里的 dirTreeCrc。
        /// 与客户端的做法一致;不通过时库仍然会解析目录,由调用方决定是否拒绝。
        /// </summary>
        public bool DirectoryChecksumValid { get; }

        public IReadOnlyList<PvfEntry> Entries => _entries;

        public int Count => _entries.Length;

        public static PvfArchive Open(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            try
            {
                return new PvfArchive(fs, false);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        public PvfArchive(Stream stream, bool leaveOpen = false)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (!stream.CanSeek || !stream.CanRead)
            {
                throw new ArgumentException("需要可读且可随机访问的流", nameof(stream));
            }
            _leaveOpen = leaveOpen;
            long fileLength = stream.Length;

            var lenBuf = new byte[4];
            if (ReadAt(0, lenBuf, 0, 4) != 4)
            {
                throw new DnfFormatException("PVF 头被截断");
            }
            uint guidLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
            if (guidLen > 4096)
            {
                throw new DnfFormatException("PVF guidLen 不合理: " + guidLen);
            }
            var head = new byte[guidLen + 16];
            if (ReadAt(4, head, 0, head.Length) != head.Length)
            {
                throw new DnfFormatException("PVF 头被截断");
            }
            GuidBytes = new byte[guidLen];
            Buffer.BlockCopy(head, 0, GuidBytes, 0, (int)guidLen);
            ReadOnlySpan<byte> h = head.AsSpan((int)guidLen);
            Version = BinaryPrimitives.ReadUInt32LittleEndian(h);
            uint treeLen = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(4));
            DirectoryCrc = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(8));
            uint nFiles = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(12));

            long headerLength = 4L + guidLen + 16;
            if (headerLength + treeLen > fileLength)
            {
                throw new DnfFormatException("PVF 目录树超出文件: dirTreeLen=" + treeLen + " 文件长度=" + fileLength);
            }
            if (treeLen > int.MaxValue || (long)nFiles * 20 > treeLen)
            {
                throw new DnfFormatException("PVF 文件数与目录树长度不匹配: nFiles=" + nFiles + " dirTreeLen=" + treeLen);
            }

            var tree = new byte[treeLen];
            if (ReadAt(headerLength, tree, 0, tree.Length) != tree.Length)
            {
                throw new DnfFormatException("PVF 目录树被截断");
            }
            PvfCipher.DecryptInPlace(tree, DirectoryCrc);
            DirectoryChecksumValid = (treeLen & 3u) == 0 && Crc32.Compute(tree, nFiles) == DirectoryCrc;
            _dataBase = headerLength + treeLen;
            _entries = ParseDirectory(tree, (int)nFiles, out int leftover);
            DirectoryTrailingBytes = leftover;
        }

        private static PvfEntry[] ParseDirectory(byte[] tree, int count, out int leftover)
        {
            var entries = new PvfEntry[count];
            int pos = 0;
            for (int i = 0; i < count; i++)
            {
                if (pos + 8 > tree.Length)
                {
                    throw new DnfFormatException("PVF 目录项 " + i + " 被截断");
                }
                uint fileNo = BinaryPrimitives.ReadUInt32LittleEndian(tree.AsSpan(pos));
                uint pathLen = BinaryPrimitives.ReadUInt32LittleEndian(tree.AsSpan(pos + 4));
                pos += 8;
                if (pathLen > 4096 || (long)pos + pathLen + 12 > tree.Length)
                {
                    throw new DnfFormatException("PVF 目录项 " + i + " 的路径长度不合理: " + pathLen);
                }
                string path = Latin1.Decode(tree.AsSpan(pos, (int)pathLen));
                pos += (int)pathLen;
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(tree.AsSpan(pos));
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(tree.AsSpan(pos + 4));
                uint rel = BinaryPrimitives.ReadUInt32LittleEndian(tree.AsSpan(pos + 8));
                pos += 12;
                entries[i] = new PvfEntry(path, fileNo, length, crc, rel);
            }
            leftover = tree.Length - pos;
            return entries;
        }

        /// <summary>按路径查找,忽略大小写。路径用 '/' 分隔。若有重复路径,取目录里靠后的那个(真实数据里没有重复)。</summary>
        public bool TryGetEntry(string path, out PvfEntry entry)
        {
            var index = _index;
            if (index == null)
            {
                lock (_gate)
                {
                    index = _index ?? (_index = BuildIndex());
                }
            }
            if (path != null && index.TryGetValue(path, out int i))
            {
                entry = _entries[i];
                return true;
            }
            entry = default;
            return false;
        }

        private Dictionary<string, int> BuildIndex()
        {
            var d = new Dictionary<string, int>(_entries.Length, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _entries.Length; i++)
            {
                d[_entries[i].Path] = i;
            }
            return d;
        }

        /// <summary>读取并解密一个文件的全部内容,不做校验(快)。</summary>
        public byte[] Read(PvfEntry entry)
        {
            return ReadCore(entry, false, out _);
        }

        /// <summary>
        /// 读取并解密一个文件的全部内容,同时按客户端的规则校验:解密后(含对齐填充)的整段明文做 CRC-32,
        /// 种子取目录项的 fileNo,应当等于目录项的 crc。校验不通过时仍返回内容,<paramref name="checksumValid"/> 为 false。
        /// </summary>
        public byte[] Read(PvfEntry entry, out bool checksumValid)
        {
            return ReadCore(entry, true, out checksumValid);
        }

        /// <summary>读取、解密并校验;校验不通过抛 <see cref="DnfFormatException"/>(客户端遇到这种文件也会拒绝加载)。</summary>
        public byte[] ReadVerified(PvfEntry entry)
        {
            byte[] data = ReadCore(entry, true, out bool ok);
            if (!ok)
            {
                throw new DnfFormatException("PVF 文件校验失败: " + entry.Path);
            }
            return data;
        }

        private byte[] ReadCore(PvfEntry entry, bool verify, out bool checksumValid)
        {
            if (entry.Length > int.MaxValue - 4)
            {
                throw new DnfFormatException("PVF 文件过大: " + entry.Path);
            }
            int length = (int)entry.Length;
            int padded = (length + 3) & ~3;
            var buffer = new byte[padded];
            int got = ReadAt(_dataBase + entry.Offset, buffer, 0, padded);
            if (got < length)
            {
                throw new DnfFormatException("PVF 文件数据被截断: " + entry.Path + " 需要 " + length + " 实得 " + got);
            }
            PvfCipher.DecryptInPlace(buffer, entry.Crc);
            // 对齐填充读不满(数据区恰好在文件末尾被截)时,校验按"整段"算必然不过,如实报告。
            checksumValid = !verify || (got == padded && Crc32.Compute(buffer, entry.FileNumber) == entry.Crc);
            if (padded == length)
            {
                return buffer;
            }
            var exact = new byte[length];
            Buffer.BlockCopy(buffer, 0, exact, 0, length);
            return exact;
        }

        public byte[] Read(string path)
        {
            if (!TryGetEntry(path, out PvfEntry e))
            {
                throw new FileNotFoundException("PVF 里没有这个文件", path);
            }
            return Read(e);
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
