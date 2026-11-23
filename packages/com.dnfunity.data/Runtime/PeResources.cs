using System;
using System.IO;
using System.Text;

// 证据:PE 映像(DOS 头的 e_lfanew、"PE\0\0" 签名、COFF 头、可选头里的数据目录、节表)与三层资源目录(类型 → 编号 → 语言 → 数据项)
// 是公开的文件格式规范;这里只读资源,不执行、不修改任何东西。客户端内嵌文字表放在命名类型 "FILE" 之下,见台账 E-442 一带。
namespace Dnf.Data
{
    /// <summary>
    /// 从 PE 映像(<c>DNF.exe</c> 这类可执行文件)的资源目录里按"类型名 + 数字编号"取一段资源字节。
    /// <para>
    /// 只在<b>可随机读取的流</b>上读:读 DOS 头、PE 头、可选头(PE32 与 PE32+ 都认)、节表与资源目录的几个小结构,再 <c>Seek</c> 到数据那一小段读出来;
    /// 绝不把整个文件读进内存(真实的可执行文件有几十 MB)。
    /// </para>
    /// <para>
    /// 所有读取都做边界检查:头或目录被截断、偏移越界、目录成环或比三层更深、条目数声明夸张(超过 <see cref="MaxDirectoryEntries"/>)、
    /// 资源大小声明夸张(超过 <see cref="MaxResourceBytes"/>)、流本身读写出错,一律返回 false,<b>不抛异常</b>;只有参数为 null 才抛 <see cref="ArgumentNullException"/>。
    /// </para>
    /// <para>
    /// 实现的选择:RVA 换文件偏移只看节在文件里的原始数据范围(<c>SizeOfRawData</c>),不看 <c>VirtualSize</c>;不校验校验和、对齐与节名;
    /// 资源目录里的"结构"(目录、名字串、数据项)必须落在数据目录声明的大小之内,而叶子指向的"数据本身"只要落在某个节的原始数据里即可(不必在资源节内)。
    /// </para>
    /// </summary>
    public static class PeResources
    {
        /// <summary>单段资源的大小上限(64 MiB):声明得比这大的一律当坏数据。</summary>
        public const int MaxResourceBytes = 64 * 1024 * 1024;

        /// <summary>一个资源目录里(命名项 + 编号项)的条目数上限:声明得比这多的一律当坏数据。</summary>
        public const int MaxDirectoryEntries = 4096;

        /// <summary>节的个数上限(Windows 加载器也只认到 96 个)。</summary>
        public const int MaxSections = 96;

        private const uint HighBit = 0x80000000u;
        private const uint LowMask = 0x7FFFFFFFu;
        private const int DirectoryHeaderSize = 16;
        private const int DirectoryEntrySize = 8;
        private const int DataEntrySize = 16;
        private const int SectionHeaderSize = 40;
        private const int Pe32Magic = 0x10b;
        private const int Pe32PlusMagic = 0x20b;

        /// <summary>
        /// 读命名类型 <paramref name="typeName"/>(第一层按名字匹配,不区分大小写的序数比较)之下编号 <paramref name="resourceId"/>(第二层按数字编号匹配)的资源,
        /// 取第三层的第一个语言项。找到并读出返回 true;其余任何情形返回 false(<paramref name="data"/> 为 null)。
        /// 返回后流的位置不确定。资源大小为 0 时返回 true 与空数组。
        /// </summary>
        public static bool TryReadNamedResource(Stream image, string typeName, int resourceId, out byte[] data)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }
            if (typeName == null)
            {
                throw new ArgumentNullException(nameof(typeName));
            }
            data = null;
            try
            {
                if (!image.CanSeek || !image.CanRead)
                {
                    return false;
                }
                return Read(image, typeName, resourceId, out data);
            }
            catch (IOException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            data = null;
            return false;
        }

        private static bool Read(Stream image, string typeName, int resourceId, out byte[] data)
        {
            data = null;
            var source = new Source(image, image.Length);

            // DOS 头:"MZ",偏移 0x3C 是 PE 头的位置 e_lfanew。
            var dos = new byte[0x40];
            if (!source.Read(0, dos, dos.Length) || dos[0] != (byte)'M' || dos[1] != (byte)'Z')
            {
                return false;
            }
            long peOffset = U32(dos, 0x3C);

            // PE 签名(4)+ COFF 头(20):节数在 COFF+2,可选头大小在 COFF+16。
            var nt = new byte[24];
            if (!source.Read(peOffset, nt, nt.Length)
                || nt[0] != (byte)'P' || nt[1] != (byte)'E' || nt[2] != 0 || nt[3] != 0)
            {
                return false;
            }
            int sectionCount = U16(nt, 4 + 2);
            int optionalSize = U16(nt, 4 + 16);
            long optionalStart = peOffset + nt.Length;
            if (sectionCount < 1 || sectionCount > MaxSections || optionalSize < 2)
            {
                return false;
            }

            // 可选头:魔数决定数据目录从偏移 96(PE32)还是 112(PE32+)开始;数据目录的个数紧挨在它前面(u32);下标 2 是资源目录 {RVA, 大小}。
            var magicBytes = new byte[2];
            if (!source.Read(optionalStart, magicBytes, 2))
            {
                return false;
            }
            int magic = U16(magicBytes, 0);
            int directoryStart;
            if (magic == Pe32Magic)
            {
                directoryStart = 96;
            }
            else if (magic == Pe32PlusMagic)
            {
                directoryStart = 112;
            }
            else
            {
                return false;
            }
            int needed = directoryStart + 3 * 8;
            if (optionalSize < needed)
            {
                return false;
            }
            var optional = new byte[needed];
            if (!source.Read(optionalStart, optional, needed))
            {
                return false;
            }
            uint directoryCount = U32(optional, directoryStart - 4);
            uint resourceRva = U32(optional, directoryStart + 2 * 8);
            uint resourceSize = U32(optional, directoryStart + 2 * 8 + 4);
            if (directoryCount < 3 || resourceRva == 0 || resourceSize < DirectoryHeaderSize)
            {
                return false;
            }

            // 节表:紧跟在可选头之后,每节 40 字节:虚拟地址在 +12,原始数据大小在 +16,原始数据的文件位置在 +20。
            var table = new byte[sectionCount * SectionHeaderSize];
            if (!source.Read(optionalStart + optionalSize, table, table.Length))
            {
                return false;
            }
            var sections = new Section[sectionCount];
            for (int i = 0; i < sectionCount; i++)
            {
                int at = i * SectionHeaderSize;
                sections[i] = new Section(U32(table, at + 12), U32(table, at + 16), U32(table, at + 20));
            }

            var walker = new Walker(source, sections, resourceRva, resourceSize);
            return walker.TryFind(typeName, resourceId, out data);
        }

        private static ushort U16(byte[] b, int at)
        {
            return (ushort)(b[at] | (b[at + 1] << 8));
        }

        private static uint U32(byte[] b, int at)
        {
            return (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24));
        }

        private readonly struct Section
        {
            public Section(uint rva, uint rawSize, uint rawPointer)
            {
                Rva = rva;
                RawSize = rawSize;
                RawPointer = rawPointer;
            }

            public uint Rva { get; }

            public uint RawSize { get; }

            public uint RawPointer { get; }
        }

        /// <summary>流上的带边界检查的读:超出流长度、读不满都算失败。</summary>
        private sealed class Source
        {
            private readonly Stream _stream;
            private readonly long _length;

            public Source(Stream stream, long length)
            {
                _stream = stream;
                _length = length;
            }

            public bool Contains(long offset, long count)
            {
                return offset >= 0 && count >= 0 && offset <= _length - count;
            }

            public bool Read(long offset, byte[] buffer, int count)
            {
                if (!Contains(offset, count))
                {
                    return false;
                }
                if (count == 0)
                {
                    return true;
                }
                _stream.Seek(offset, SeekOrigin.Begin);
                int done = 0;
                while (done < count)
                {
                    int n = _stream.Read(buffer, done, count - done);
                    if (n <= 0)
                    {
                        return false;
                    }
                    done += n;
                }
                return true;
            }
        }

        /// <summary>资源目录的三层遍历。层数是写死的三层(类型 → 编号 → 语言),所以不可能无限循环;成环与过深另外显式拒绝。</summary>
        private sealed class Walker
        {
            private readonly Source _source;
            private readonly Section[] _sections;
            private readonly long _rva;
            private readonly long _size;

            public Walker(Source source, Section[] sections, long rva, long size)
            {
                _source = source;
                _sections = sections;
                _rva = rva;
                _size = size;
            }

            public bool TryFind(string typeName, int resourceId, out byte[] data)
            {
                data = null;

                // 第一层:按名字(不区分大小写的序数比较)找类型;类型项必须指向子目录。
                if (!ReadDirectory(0, out byte[] types, out int typeCount))
                {
                    return false;
                }
                long typeDir = -1;
                for (int i = 0; i < typeCount; i++)
                {
                    uint nameField = U32(types, i * DirectoryEntrySize);
                    if ((nameField & HighBit) == 0)
                    {
                        continue;
                    }
                    int match = NameEquals(nameField & LowMask, typeName);
                    if (match < 0)
                    {
                        return false;
                    }
                    if (match == 0)
                    {
                        continue;
                    }
                    uint target = U32(types, i * DirectoryEntrySize + 4);
                    if ((target & HighBit) == 0)
                    {
                        return false;
                    }
                    typeDir = target & LowMask;
                    break;
                }
                if (typeDir < 0 || typeDir == 0)
                {
                    // 类型目录指回根目录:成环。
                    return false;
                }

                // 第二层:按数字编号找资源(命名项跳过);编号项必须指向子目录,且不能指回已经走过的目录。
                if (!ReadDirectory(typeDir, out byte[] ids, out int idCount))
                {
                    return false;
                }
                long idDir = -1;
                for (int i = 0; i < idCount; i++)
                {
                    uint field = U32(ids, i * DirectoryEntrySize);
                    if ((field & HighBit) != 0 || field != (uint)resourceId)
                    {
                        continue;
                    }
                    uint target = U32(ids, i * DirectoryEntrySize + 4);
                    if ((target & HighBit) == 0)
                    {
                        return false;
                    }
                    idDir = target & LowMask;
                    break;
                }
                if (idDir < 0 || idDir == 0 || idDir == typeDir)
                {
                    return false;
                }

                // 第三层:语言目录,取第一项;它必须是数据项(再指向子目录就是比三层更深,不认)。
                if (!ReadDirectory(idDir, out byte[] languages, out int languageCount) || languageCount == 0)
                {
                    return false;
                }
                uint leaf = U32(languages, 4);
                if ((leaf & HighBit) != 0)
                {
                    return false;
                }

                // 数据项 {数据的 RVA(是 RVA,不是资源内偏移)、大小、代码页、保留}。
                var entry = new byte[DataEntrySize];
                if (!ReadStructure(leaf, entry, entry.Length))
                {
                    return false;
                }
                uint dataRva = U32(entry, 0);
                uint dataSize = U32(entry, 4);
                if (dataSize > MaxResourceBytes)
                {
                    return false;
                }
                if (dataSize == 0)
                {
                    data = new byte[0];
                    return true;
                }
                if (!TryMap(dataRva, dataSize, out long offset) || !_source.Contains(offset, dataSize))
                {
                    return false;
                }
                var bytes = new byte[dataSize];
                if (!_source.Read(offset, bytes, bytes.Length))
                {
                    return false;
                }
                data = bytes;
                return true;
            }

            /// <summary>读资源目录内相对偏移 <paramref name="relative"/> 处的一个结构(必须落在数据目录声明的大小之内)。</summary>
            private bool ReadStructure(long relative, byte[] buffer, int count)
            {
                if (relative < 0 || relative + count > _size)
                {
                    return false;
                }
                return TryMap(_rva + relative, count, out long offset) && _source.Read(offset, buffer, count);
            }

            /// <summary>读一个目录:16 字节头(命名项数在 +12、编号项数在 +14),后面是每项 8 字节的条目表。</summary>
            private bool ReadDirectory(long relative, out byte[] entries, out int count)
            {
                entries = null;
                count = 0;
                var header = new byte[DirectoryHeaderSize];
                if (!ReadStructure(relative, header, header.Length))
                {
                    return false;
                }
                int total = U16(header, 12) + U16(header, 14);
                if (total > MaxDirectoryEntries)
                {
                    return false;
                }
                var table = new byte[total * DirectoryEntrySize];
                if (total > 0 && !ReadStructure(relative + DirectoryHeaderSize, table, table.Length))
                {
                    return false;
                }
                entries = table;
                count = total;
                return true;
            }

            /// <summary>名字串(u16 字符数 + UTF-16 字符)与 <paramref name="expected"/> 做不区分大小写的序数比较:1 相等,0 不等,-1 读不出来。</summary>
            private int NameEquals(long relative, string expected)
            {
                var lengthBytes = new byte[2];
                if (!ReadStructure(relative, lengthBytes, 2))
                {
                    return -1;
                }
                int length = U16(lengthBytes, 0);
                if (length != expected.Length)
                {
                    return 0;
                }
                var chars = new byte[length * 2];
                if (length > 0 && !ReadStructure(relative + 2, chars, chars.Length))
                {
                    return -1;
                }
                string name = Encoding.Unicode.GetString(chars);
                return string.Equals(name, expected, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            }

            /// <summary>RVA 换文件偏移:找原始数据范围完整包含 [rva, rva+count) 的第一个节。</summary>
            private bool TryMap(long rva, long count, out long offset)
            {
                for (int i = 0; i < _sections.Length; i++)
                {
                    long start = _sections[i].Rva;
                    long end = start + _sections[i].RawSize;
                    if (rva >= start && rva + count <= end)
                    {
                        offset = _sections[i].RawPointer + (rva - start);
                        return true;
                    }
                }
                offset = 0;
                return false;
            }
        }
    }
}
