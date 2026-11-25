using System;

namespace Dnf.Net.Session
{
    /// <summary>从客户端程序文件读文件版本失败的原因(只点名问题类别,不带文件里的任何内容)。</summary>
    public enum ClientVersionFileProblem
    {
        /// <summary>没有问题。</summary>
        None = 0,

        /// <summary>不是 PE 可执行文件:缺 MZ 或 PE 标记、头部超出文件、可选头魔数不认识。</summary>
        NotPeImage,

        /// <summary>PE 头里没有资源目录。</summary>
        NoResourceDirectory,

        /// <summary>资源目录的形状不对:越界、映射不到任何节、目录项层级不对。</summary>
        BadResourceDirectory,

        /// <summary>资源里没有编号 1 的版本资源(Windows 的取版本接口只认这一个)。</summary>
        NoVersionResource,

        /// <summary>版本资源的形状不对:越界、过短、键名不是 <c>VS_VERSION_INFO</c>、缺固定文件信息签名。</summary>
        BadVersionResource,
    }

    /// <summary>
    /// 读客户端程序文件自带的版本资源里的"文件版本"四段数(只读、纯函数:输入是整个文件的字节,不碰文件系统)。
    /// 做法与原版客户端取自己版本的方式一致(台账 E-451):取资源类型 16(版本)下编号 1 的那一项的第一个语言,
    /// 其中 <c>VS_VERSIONINFO</c> 头之后的固定文件信息(签名 0xFEEF04BD)里,文件版本高位字是 A、低位字是 B,
    /// 文件版本低位字组的高位字是 C、低位字是 D。
    /// 文件内容当作不可信输入:每次读取都先查边界,目录只走固定三层、不递归,所以任何畸形文件都只会得到一个问题类别,不会抛异常、不会死循环、不会按声明的大小去分配内存。
    /// </summary>
    public static class ClientVersionFile
    {
        /// <summary>版本资源数据的大小上限(正常的只有几百字节到几 KB)。</summary>
        public const int MaxVersionResourceBytes = 1024 * 1024;

        private const int ResourceTypeVersion = 16;
        private const int ResourceIdVersionInfo = 1;
        private const uint FixedFileInfoSignature = 0xFEEF04BDu;
        private const int FixedFileInfoBytes = 52;
        private const int VersionInfoKeyBytes = 32;                   // "VS_VERSION_INFO" 加结尾 0,UTF-16
        private const int FixedFileInfoOffset = 40;                   // 6 字节头 + 32 字节键名 = 38,按 4 字节对齐到 40
        private const string VersionInfoKey = "VS_VERSION_INFO";

        /// <summary>
        /// 从整个程序文件的字节里读文件版本。成功返回 true;失败返回 false,<paramref name="problem"/> 是原因类别。
        /// </summary>
        public static bool TryReadFileVersion(byte[] image, out ClientFileVersion version, out ClientVersionFileProblem problem)
        {
            version = default;
            if (image == null || image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
            {
                problem = ClientVersionFileProblem.NotPeImage;
                return false;
            }

            if (!TryU32(image, 0x3C, out uint lfanew))
            {
                problem = ClientVersionFileProblem.NotPeImage;
                return false;
            }
            long pe = lfanew;
            if (!InRange(image, pe, 24) || image[pe] != (byte)'P' || image[pe + 1] != (byte)'E' || image[pe + 2] != 0 || image[pe + 3] != 0)
            {
                problem = ClientVersionFileProblem.NotPeImage;
                return false;
            }

            TryU16(image, pe + 6, out int sectionCount);
            TryU16(image, pe + 20, out int optionalSize);
            long optional = pe + 24;
            if (!TryU16(image, optional, out int magic))
            {
                problem = ClientVersionFileProblem.NotPeImage;
                return false;
            }

            long countOffset;
            long directories;
            if (magic == 0x10B)
            {
                countOffset = optional + 92;
                directories = optional + 96;
            }
            else if (magic == 0x20B)
            {
                countOffset = optional + 108;
                directories = optional + 112;
            }
            else
            {
                problem = ClientVersionFileProblem.NotPeImage;
                return false;
            }

            if (!TryU32(image, countOffset, out uint directoryCount))
            {
                problem = ClientVersionFileProblem.NotPeImage;
                return false;
            }
            long resourceEntry = directories + 2 * 8;                 // 数据目录第 2 项是资源
            if (directoryCount <= 2 || resourceEntry + 8 > optional + optionalSize
                || !TryU32(image, resourceEntry, out uint resourceRva) || !TryU32(image, resourceEntry + 4, out uint resourceSize))
            {
                problem = ClientVersionFileProblem.NoResourceDirectory;
                return false;
            }
            if (resourceRva == 0 || resourceSize == 0)
            {
                problem = ClientVersionFileProblem.NoResourceDirectory;
                return false;
            }

            long sections = optional + optionalSize;
            if (!InRange(image, sections, 40L * sectionCount)
                || !TryRvaToOffset(image, sections, sectionCount, resourceRva, 16, out long root))
            {
                problem = ClientVersionFileProblem.BadResourceDirectory;
                return false;
            }

            // 第一层:资源类型,找编号 16;第二层:该类型下的资源编号,找编号 1;第三层:语言,取第一项
            Lookup typeLookup = FindIdEntry(image, root, ResourceTypeVersion, out uint typeEntry);
            if (typeLookup != Lookup.Found)
            {
                problem = typeLookup == Lookup.NotFound ? ClientVersionFileProblem.NoVersionResource : ClientVersionFileProblem.BadResourceDirectory;
                return false;
            }
            if (!TryDirectory(root, typeEntry, out long nameDirectory))
            {
                problem = ClientVersionFileProblem.BadResourceDirectory;
                return false;
            }

            Lookup nameLookup = FindIdEntry(image, nameDirectory, ResourceIdVersionInfo, out uint nameEntry);
            if (nameLookup != Lookup.Found)
            {
                problem = nameLookup == Lookup.NotFound ? ClientVersionFileProblem.NoVersionResource : ClientVersionFileProblem.BadResourceDirectory;
                return false;
            }
            if (!TryDirectory(root, nameEntry, out long languageDirectory))
            {
                problem = ClientVersionFileProblem.BadResourceDirectory;
                return false;
            }

            if (!TryFirstEntry(image, languageDirectory, out uint leafEntry))
            {
                problem = ClientVersionFileProblem.NoVersionResource;
                return false;
            }
            if ((leafEntry & 0x80000000u) != 0)
            {
                problem = ClientVersionFileProblem.BadResourceDirectory;      // 语言层该是叶子,不该还有下一层
                return false;
            }

            // 数据项:数据的 RVA、大小(RVA 要经节表映射回文件偏移)
            long dataEntry = root + leafEntry;
            if (!TryU32(image, dataEntry, out uint dataRva) || !TryU32(image, dataEntry + 4, out uint dataSize))
            {
                problem = ClientVersionFileProblem.BadResourceDirectory;
                return false;
            }
            if (dataSize < FixedFileInfoOffset + FixedFileInfoBytes || dataSize > MaxVersionResourceBytes
                || !TryRvaToOffset(image, sections, sectionCount, dataRva, dataSize, out long blob))
            {
                problem = ClientVersionFileProblem.BadVersionResource;
                return false;
            }

            return TryParseVersionInfo(image, blob, out version, out problem);
        }

        /// <summary>给人看的一句话,只点名问题类别(不带任何文件内容)。<see cref="ClientVersionFileProblem.None"/> 返回空串。</summary>
        public static string Describe(ClientVersionFileProblem problem)
        {
            switch (problem)
            {
                case ClientVersionFileProblem.NotPeImage:
                    return "这个文件不是 PE 可执行文件";
                case ClientVersionFileProblem.NoResourceDirectory:
                    return "这个文件里没有资源目录";
                case ClientVersionFileProblem.BadResourceDirectory:
                    return "这个文件的资源目录形状不对";
                case ClientVersionFileProblem.NoVersionResource:
                    return "这个文件的资源里没有版本资源";
                case ClientVersionFileProblem.BadVersionResource:
                    return "这个文件的版本资源形状不对";
                default:
                    return string.Empty;
            }
        }

        private enum Lookup
        {
            Found,
            NotFound,
            Bad,
        }

        // blob 处的数据已由调用方保证至少有 92 字节(头、键名、对齐、固定文件信息)都在文件范围内
        private static bool TryParseVersionInfo(byte[] image, long blob, out ClientFileVersion version, out ClientVersionFileProblem problem)
        {
            version = default;
            problem = ClientVersionFileProblem.BadVersionResource;
            if (!TryU16(image, blob + 2, out int valueLength) || valueLength < FixedFileInfoBytes)
            {
                return false;
            }
            if (!InRange(image, blob + 6, VersionInfoKeyBytes))
            {
                return false;
            }
            for (int i = 0; i < VersionInfoKey.Length; i++)
            {
                // UTF-16 小端:低字节是字符,高字节是 0
                if (image[blob + 6 + 2 * i] != (byte)VersionInfoKey[i] || image[blob + 6 + 2 * i + 1] != 0)
                {
                    return false;
                }
            }
            if (image[blob + 6 + 2 * VersionInfoKey.Length] != 0 || image[blob + 6 + 2 * VersionInfoKey.Length + 1] != 0)
            {
                return false;
            }

            long fixedInfo = blob + FixedFileInfoOffset;
            if (!TryU32(image, fixedInfo, out uint signature) || signature != FixedFileInfoSignature
                || !TryU32(image, fixedInfo + 8, out uint fileVersionMs) || !TryU32(image, fixedInfo + 12, out uint fileVersionLs))
            {
                return false;
            }

            version = new ClientFileVersion(
                (ushort)(fileVersionMs >> 16),
                (ushort)(fileVersionMs & 0xFFFF),
                (ushort)(fileVersionLs >> 16),
                (ushort)(fileVersionLs & 0xFFFF));
            problem = ClientVersionFileProblem.None;
            return true;
        }

        // 在 directory 处的资源目录里按"编号"(不是名字)找 id;找到时给出该项的"数据或子目录偏移"字段。名字项排在前面(数量在目录头里),编号项在后。
        private static Lookup FindIdEntry(byte[] image, long directory, int id, out uint offsetField)
        {
            offsetField = 0;
            if (!TryU16(image, directory + 12, out int named) || !TryU16(image, directory + 14, out int ids))
            {
                return Lookup.Bad;
            }
            if (!InRange(image, directory + 16, 8L * (named + ids)))
            {
                return Lookup.Bad;
            }
            for (int i = named; i < named + ids; i++)
            {
                long entry = directory + 16 + 8L * i;
                TryU32(image, entry, out uint nameOrId);
                if ((nameOrId & 0x80000000u) == 0 && nameOrId == (uint)id)
                {
                    TryU32(image, entry + 4, out offsetField);
                    return Lookup.Found;
                }
            }
            return Lookup.NotFound;
        }

        private static bool TryFirstEntry(byte[] image, long directory, out uint offsetField)
        {
            offsetField = 0;
            if (!TryU16(image, directory + 12, out int named) || !TryU16(image, directory + 14, out int ids) || named + ids < 1)
            {
                return false;
            }
            return InRange(image, directory + 16, 8) && TryU32(image, directory + 16 + 4, out offsetField);
        }

        // 目录项的"数据或子目录偏移"字段:高位置 1 表示子目录,低 31 位是相对资源根的偏移
        private static bool TryDirectory(long root, uint offsetField, out long directory)
        {
            directory = 0;
            if ((offsetField & 0x80000000u) == 0)
            {
                return false;
            }
            directory = root + (offsetField & 0x7FFFFFFFu);
            return true;
        }

        private static bool TryRvaToOffset(byte[] image, long sections, int sectionCount, uint rva, long needed, out long offset)
        {
            offset = 0;
            for (int i = 0; i < sectionCount; i++)
            {
                long s = sections + 40L * i;
                TryU32(image, s + 12, out uint virtualAddress);
                TryU32(image, s + 16, out uint rawSize);
                TryU32(image, s + 20, out uint rawPointer);
                if (rva < virtualAddress)
                {
                    continue;
                }
                long inside = (long)rva - virtualAddress;
                if (inside + needed > rawSize)
                {
                    continue;
                }
                long candidate = (long)rawPointer + inside;
                if (!InRange(image, candidate, needed))
                {
                    continue;
                }
                offset = candidate;
                return true;
            }
            return false;
        }

        private static bool InRange(byte[] image, long offset, long length)
        {
            return offset >= 0 && length >= 0 && offset + length <= image.Length;
        }

        private static bool TryU16(byte[] image, long offset, out int value)
        {
            value = 0;
            if (!InRange(image, offset, 2))
            {
                return false;
            }
            value = image[offset] | (image[offset + 1] << 8);
            return true;
        }

        private static bool TryU32(byte[] image, long offset, out uint value)
        {
            value = 0;
            if (!InRange(image, offset, 4))
            {
                return false;
            }
            value = (uint)(image[offset] | (image[offset + 1] << 8) | (image[offset + 2] << 16) | (image[offset + 3] << 24));
            return true;
        }
    }
}
