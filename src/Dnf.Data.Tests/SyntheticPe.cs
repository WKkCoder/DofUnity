using System.Text;

namespace Dnf.Data.Tests;

/// <summary>
/// 合成 PE 映像构造器(只给测试用,不入产品):按规格造出最小的 PE32 / PE32+ 映像——DOS 头(e_lfanew)、PE 签名、COFF 头、
/// 带数据目录的可选头、节表(至少两个节:.text 与 .rsrc,可选第三个放资源数据的节与若干空节)、
/// 资源节里的三层资源目录(类型:命名或数字;编号;语言;叶子数据项)。所有字节都是合成的,不含任何原版内容。
/// 构造结果带一张"标记表"(文件偏移),测试可以据此原地改字节(成环、过深、条目数夸张……)。
/// </summary>
internal static class SyntheticPe
{
    internal sealed class Leaf
    {
        public Leaf(int language, byte[] data)
        {
            Language = language;
            Data = data;
        }

        public int Language { get; }

        public byte[] Data { get; }
    }

    internal sealed class Item
    {
        public Item(int id, params Leaf[] leaves)
        {
            Id = id;
            Leaves = leaves;
        }

        public int Id { get; }

        public Leaf[] Leaves { get; }
    }

    internal sealed class ResType
    {
        private ResType(string name, int id, Item[] items)
        {
            Name = name;
            Id = id;
            Items = items;
        }

        public static ResType Named(string name, params Item[] items) => new ResType(name, 0, items);

        public static ResType Numbered(int id, params Item[] items) => new ResType(null, id, items);

        /// <summary>命名类型为 null 表示数字类型。</summary>
        public string Name { get; }

        public int Id { get; }

        public Item[] Items { get; }

        public string Key => Name ?? "#" + Id;
    }

    internal sealed class Spec
    {
        public bool Pe32Plus;
        public bool DataInSeparateSection;
        public ResType[] Types = Array.Empty<ResType>();

        /// <summary>可选头里写的数据目录个数(NumberOfRvaAndSizes),可选头的大小也按这个个数算(小于 3 时放不下资源目录项)。</summary>
        public int DirectoryCount = 16;

        public bool OmitResourceDirectory = false;

        /// <summary>在 .text、.rsrc(、数据节)之后再加几个空节(原始大小 0)。</summary>
        public int ExtraSections = 0;

        /// <summary>.text 节的原始数据大小(填充字节;测试"不整文件读进内存"时放很大)。</summary>
        public int TextBytes = 0x200;
    }

    internal sealed class Image
    {
        public byte[] Bytes = Array.Empty<byte>();

        /// <summary>名字 → 文件偏移。键见 <see cref="Build"/> 的注释。</summary>
        public Dictionary<string, int> Marks = new Dictionary<string, int>();

        public int this[string mark] => Marks[mark];

        /// <summary>资源内相对偏移(相对资源根目录)= 文件偏移 - 资源节的文件偏移。</summary>
        public uint Relative(string mark) => (uint)(Marks[mark] - Marks["rsrc"]);

        public void PatchU16(int offset, int value)
        {
            Bytes[offset] = (byte)value;
            Bytes[offset + 1] = (byte)(value >> 8);
        }

        public void PatchU32(int offset, uint value)
        {
            Bytes[offset] = (byte)value;
            Bytes[offset + 1] = (byte)(value >> 8);
            Bytes[offset + 2] = (byte)(value >> 16);
            Bytes[offset + 3] = (byte)(value >> 24);
        }

        public uint ReadU32(int offset) => (uint)(Bytes[offset] | (Bytes[offset + 1] << 8) | (Bytes[offset + 2] << 16) | (Bytes[offset + 3] << 24));

        /// <summary>目标项数据的最后一个字节之后的文件偏移(叶子的数据都在目录结构之后,所以文件截到这里或更长就读得到)。</summary>
        public int DataEnd(string typeKey, int id, int leafIndex) => Marks[$"data-end:{typeKey}/{id}/{leafIndex}"];
    }

    private const uint HighBit = 0x80000000u;
    private const int PeOffset = 0x80;

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    /// <summary>
    /// 造一个映像。标记表的键(值都是文件偏移):
    /// <c>pe</c>、<c>coff</c>、<c>optional</c>、<c>directory-count</c>(NumberOfRvaAndSizes)、<c>resource-directory</c>(数据目录下标 2:RVA 在 +0、大小在 +4)、
    /// <c>section-table</c>、<c>section:0</c>…(每节 40 字节的头)、<c>rsrc</c>(资源根目录 = 资源节原始数据起点)、<c>data-section</c>(数据节原始数据起点,仅分节时有);
    /// 目录项(8 字节:+0 名字或编号、+4 目标)<c>type-entry:类型键</c>、<c>item-entry:类型键/编号</c>、<c>leaf-entry:类型键/编号/语言序号</c>;
    /// 目录(16 字节头)<c>type-dir:类型键</c>、<c>item-dir:类型键/编号</c>;数据项(16 字节)<c>data-entry:类型键/编号/语言序号</c>;
    /// 数据本身 <c>data:…</c> 与结尾 <c>data-end:…</c>。类型键:命名类型是名字,数字类型是 "#编号"。
    /// 命名类型排在数字类型之前(规格要求);同层条目按给定顺序。
    /// </summary>
    public static Image Build(Spec spec)
    {
        ResType[] types = spec.Types.Where(t => t.Name != null).Concat(spec.Types.Where(t => t.Name == null)).ToArray();
        int directoryStart = spec.Pe32Plus ? 112 : 96;
        int optionalSize = directoryStart + 8 * spec.DirectoryCount;
        int sectionCount = 2 + (spec.DataInSeparateSection ? 1 : 0) + spec.ExtraSections;
        int headersEnd = PeOffset + 24 + optionalSize + sectionCount * 40;
        int textRaw = AlignUp(headersEnd, 0x200);

        // ---- 资源 blob 的布局(相对偏移):根目录、各类型目录、各编号目录、名字串、数据项、(同节时)数据本身
        var offsets = new Dictionary<string, int>();
        int pos = 16 + 8 * types.Length;
        foreach (ResType t in types)
        {
            offsets["type-dir:" + t.Key] = pos;
            pos += 16 + 8 * t.Items.Length;
        }
        foreach (ResType t in types)
        {
            foreach (Item item in t.Items)
            {
                offsets[$"item-dir:{t.Key}/{item.Id}"] = pos;
                pos += 16 + 8 * item.Leaves.Length;
            }
        }
        foreach (ResType t in types.Where(t => t.Name != null))
        {
            offsets["name:" + t.Key] = pos;
            pos += 2 + 2 * t.Name.Length;
        }
        foreach (ResType t in types)
        {
            foreach (Item item in t.Items)
            {
                for (int k = 0; k < item.Leaves.Length; k++)
                {
                    offsets[$"data-entry:{t.Key}/{item.Id}/{k}"] = pos;
                    pos += 16;
                }
            }
        }
        int structureEnd = pos;
        int dataTotal = 0;
        var dataPositions = new Dictionary<string, int>();   // 在"数据区"里的偏移(数据区 = 同节时紧跟结构、分节时是单独的节)
        foreach (ResType t in types)
        {
            foreach (Item item in t.Items)
            {
                for (int k = 0; k < item.Leaves.Length; k++)
                {
                    dataPositions[$"{t.Key}/{item.Id}/{k}"] = dataTotal;
                    dataTotal += item.Leaves[k].Data.Length;
                }
            }
        }
        int rsrcLength = spec.DataInSeparateSection ? structureEnd : structureEnd + dataTotal;

        // ---- 节布局:虚拟地址与文件偏移故意不同(RVA ≠ 文件偏移),数据节的 RVA 与资源节也不同
        int textVa = 0x1000;
        int rsrcVa = AlignUp(textVa + spec.TextBytes, 0x1000) + 0x1000;
        int rsrcRaw = textRaw + spec.TextBytes;
        int dataVa = AlignUp(rsrcVa + rsrcLength, 0x1000) + 0x2000;
        int dataRaw = rsrcRaw + rsrcLength;
        int fileLength = spec.DataInSeparateSection ? dataRaw + dataTotal : rsrcRaw + rsrcLength;

        var bytes = new byte[fileLength];
        var image = new Image { Bytes = bytes };
        Dictionary<string, int> marks = image.Marks;

        void U16(int at, int v)
        {
            bytes[at] = (byte)v;
            bytes[at + 1] = (byte)(v >> 8);
        }

        void U32(int at, uint v)
        {
            bytes[at] = (byte)v;
            bytes[at + 1] = (byte)(v >> 8);
            bytes[at + 2] = (byte)(v >> 16);
            bytes[at + 3] = (byte)(v >> 24);
        }

        // ---- DOS 头与 PE 头
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        U32(0x3C, PeOffset);
        bytes[PeOffset] = (byte)'P';
        bytes[PeOffset + 1] = (byte)'E';
        int coff = PeOffset + 4;
        int optional = coff + 20;
        U16(coff, spec.Pe32Plus ? 0x8664 : 0x014C);
        U16(coff + 2, sectionCount);
        U16(coff + 16, optionalSize);
        U16(coff + 18, 0x0102);
        U16(optional, spec.Pe32Plus ? 0x20b : 0x10b);
        U32(optional + directoryStart - 4, (uint)spec.DirectoryCount);
        int resourceDirectory = optional + directoryStart + 2 * 8;
        marks["pe"] = PeOffset;
        marks["coff"] = coff;
        marks["optional"] = optional;
        marks["directory-count"] = optional + directoryStart - 4;
        marks["resource-directory"] = resourceDirectory;
        if (!spec.OmitResourceDirectory && spec.DirectoryCount >= 3)
        {
            U32(resourceDirectory, (uint)rsrcVa);
            U32(resourceDirectory + 4, (uint)rsrcLength);
        }

        // ---- 节表
        int table = optional + optionalSize;
        marks["section-table"] = table;
        void Section(int index, string name, int virtualSize, int va, int rawSize, int rawPointer)
        {
            int at = table + index * 40;
            marks["section:" + index] = at;
            byte[] n = Encoding.ASCII.GetBytes(name);
            Array.Copy(n, 0, bytes, at, Math.Min(8, n.Length));
            U32(at + 8, (uint)virtualSize);
            U32(at + 12, (uint)va);
            U32(at + 16, (uint)rawSize);
            U32(at + 20, (uint)rawPointer);
            U32(at + 36, 0x40000040u);
        }

        Section(0, ".text", spec.TextBytes, textVa, spec.TextBytes, textRaw);
        for (int i = 0; i < spec.TextBytes && textRaw + i < bytes.Length; i++)
        {
            bytes[textRaw + i] = 0xCC;
        }
        Section(1, ".rsrc", rsrcLength, rsrcVa, rsrcLength, rsrcRaw);
        int next = 2;
        if (spec.DataInSeparateSection)
        {
            Section(next++, ".rdat", dataTotal, dataVa, dataTotal, dataRaw);
            marks["data-section"] = dataRaw;
        }
        for (int e = 0; e < spec.ExtraSections; e++)
        {
            Section(next++, ".x" + e, 0, dataVa + 0x10000 + e * 0x1000, 0, fileLength);
        }
        marks["rsrc"] = rsrcRaw;

        // ---- 资源目录
        void WriteDirectoryHeader(int relative, int named, int ids)
        {
            int at = rsrcRaw + relative;
            U16(at + 12, named);
            U16(at + 14, ids);
        }

        int namedTypes = types.Count(t => t.Name != null);
        WriteDirectoryHeader(0, namedTypes, types.Length - namedTypes);
        marks["root"] = rsrcRaw;
        for (int i = 0; i < types.Length; i++)
        {
            ResType t = types[i];
            int entry = rsrcRaw + 16 + 8 * i;
            marks["type-entry:" + t.Key] = entry;
            if (t.Name != null)
            {
                U32(entry, HighBit | (uint)offsets["name:" + t.Key]);
                int name = rsrcRaw + offsets["name:" + t.Key];
                U16(name, t.Name.Length);
                byte[] chars = Encoding.Unicode.GetBytes(t.Name);
                Array.Copy(chars, 0, bytes, name + 2, chars.Length);
            }
            else
            {
                U32(entry, (uint)t.Id);
            }
            int typeDir = offsets["type-dir:" + t.Key];
            U32(entry + 4, HighBit | (uint)typeDir);
            marks["type-dir:" + t.Key] = rsrcRaw + typeDir;
            WriteDirectoryHeader(typeDir, 0, t.Items.Length);
            for (int j = 0; j < t.Items.Length; j++)
            {
                Item item = t.Items[j];
                string itemKey = $"{t.Key}/{item.Id}";
                int itemEntry = rsrcRaw + typeDir + 16 + 8 * j;
                marks["item-entry:" + itemKey] = itemEntry;
                U32(itemEntry, (uint)item.Id);
                int itemDir = offsets["item-dir:" + itemKey];
                U32(itemEntry + 4, HighBit | (uint)itemDir);
                marks["item-dir:" + itemKey] = rsrcRaw + itemDir;
                WriteDirectoryHeader(itemDir, 0, item.Leaves.Length);
                for (int k = 0; k < item.Leaves.Length; k++)
                {
                    string leafKey = $"{itemKey}/{k}";
                    int leafEntry = rsrcRaw + itemDir + 16 + 8 * k;
                    marks["leaf-entry:" + leafKey] = leafEntry;
                    U32(leafEntry, (uint)item.Leaves[k].Language);
                    int dataEntry = offsets["data-entry:" + leafKey];
                    U32(leafEntry + 4, (uint)dataEntry);
                    marks["data-entry:" + leafKey] = rsrcRaw + dataEntry;

                    byte[] data = item.Leaves[k].Data;
                    int inArea = dataPositions[leafKey];
                    int dataFile = spec.DataInSeparateSection ? dataRaw + inArea : rsrcRaw + structureEnd + inArea;
                    uint dataRvaValue = spec.DataInSeparateSection ? (uint)(dataVa + inArea) : (uint)(rsrcVa + structureEnd + inArea);
                    U32(rsrcRaw + dataEntry, dataRvaValue);
                    U32(rsrcRaw + dataEntry + 4, (uint)data.Length);
                    U32(rsrcRaw + dataEntry + 8, 0x4E4);
                    Array.Copy(data, 0, bytes, dataFile, data.Length);
                    marks["data:" + leafKey] = dataFile;
                    marks["data-end:" + leafKey] = dataFile + data.Length;
                }
            }
        }
        return image;
    }

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>标准夹具:命名类型 FIL/FILF/FILES(干扰项,名字长度与目标相同或不同)、FILE(编号 41、157、43;157 有两个语言,第一个语言号故意比第二个大)、数字类型 3 与 10(同样有编号 157)。</summary>
    public static Spec Standard(bool pe32Plus = false, bool separate = false)
    {
        return new Spec
        {
            Pe32Plus = pe32Plus,
            DataInSeparateSection = separate,
            Types = new[]
            {
                ResType.Named("FIL", new Item(157, new Leaf(0x0409, Ascii("decoy:FIL/157")))),
                ResType.Named("FILF", new Item(157, new Leaf(0x0409, Ascii("decoy:FILF/157")))),
                ResType.Named("FILES", new Item(157, new Leaf(0x0409, Ascii("decoy:FILES/157")))),
                ResType.Named(
                    "FILE",
                    new Item(41, new Leaf(0x0409, Ascii("FILE/41/a")), new Leaf(0x0804, Ascii("FILE/41/b"))),
                    new Item(157, new Leaf(0x0804, Ascii("FILE/157/first")), new Leaf(0x0409, Ascii("FILE/157/second"))),
                    new Item(43, new Leaf(0x0409, Ascii("FILE/43/a")))),
                ResType.Numbered(3, new Item(157, new Leaf(0x0409, Ascii("decoy:#3/157")))),
                ResType.Numbered(10, new Item(157, new Leaf(0x0409, Ascii("decoy:#10/157")))),
            },
        };
    }
}
