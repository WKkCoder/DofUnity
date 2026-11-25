using System.Text;

namespace Dnf.Net.Tests;

/// <summary>
/// 合成的最小 PE 文件构造器:只含"读版本资源"要走的东西(DOS 头、PE 头、可选头的数据目录、节表、三层资源目录、版本资源数据),
/// 全部是现场拼出来的合成字节,不含任何真实程序的内容,版本数也全是随手写的假数。
/// 布局故意让"节的内存地址"与"节在文件里的位置"不相等(默认资源节的 RVA 与原始指针差很多),并且有两个节,
/// 这样把 RVA 当文件偏移用、或只看第一个节的读法都会读错。
/// </summary>
internal static class SyntheticPeImage
{
    /// <summary>资源目录树的一个节点:目录(有若干项)或叶子(有数据)。</summary>
    internal sealed class Node
    {
        public readonly List<(bool Named, uint Id, Node Child)> Entries = new();

        public byte[] Data;

        public static Node Dir(params (bool Named, uint Id, Node Child)[] entries)
        {
            var node = new Node();
            node.Entries.AddRange(entries);
            return node;
        }

        public static Node Leaf(byte[] data)
        {
            return new Node { Data = data };
        }
    }

    internal sealed class Options
    {
        /// <summary>PE32+(64 位)可选头;默认 PE32。</summary>
        public bool Pe32Plus;

        /// <summary>资源节的内存地址(RVA)。</summary>
        public uint ResourceRva = 0x3000;

        /// <summary>资源节在文件里的位置;与 RVA 刻意不同。</summary>
        public uint ResourceRawPointer = 0x600;

        /// <summary>数据目录里登记的资源目录 RVA 与大小是否填(填 0 表示没有资源目录)。</summary>
        public bool DeclareResourceDirectory = true;

        /// <summary>可选头里的数据目录项数;默认 16。</summary>
        public uint DirectoryCount = 16;
    }

    public static (bool Named, uint Id, Node Child) Id(uint id, Node child) => (false, id, child);

    public static (bool Named, uint Id, Node Child) Named(Node child) => (true, 0, child);

    /// <summary>
    /// 一份常见的版本资源块(VS_VERSIONINFO):头、键名、对齐、固定文件信息、一点尾部填充。
    /// 产品版本故意取与文件版本不同的假数,读错字段(读成产品版本)就会被发现。
    /// </summary>
    public static byte[] VersionBlob(ushort a, ushort b, ushort c, ushort d, string key = "VS_VERSION_INFO",
        uint signature = 0xFEEF04BDu, ushort valueLength = 52, int trailing = 40)
    {
        var bytes = new byte[40 + 52 + trailing];
        W16(bytes, 0, (uint)bytes.Length);
        W16(bytes, 2, valueLength);
        W16(bytes, 4, 0);
        byte[] keyBytes = Encoding.Unicode.GetBytes(key + "\0");
        Array.Copy(keyBytes, 0, bytes, 6, Math.Min(keyBytes.Length, 32));
        int f = 40;
        W32(bytes, f, signature);
        W32(bytes, f + 4, 0x00010000u);
        W32(bytes, f + 8, ((uint)a << 16) | b);               // 文件版本高 32 位:A 在高字,B 在低字
        W32(bytes, f + 12, ((uint)c << 16) | d);              // 文件版本低 32 位:C 在高字,D 在低字
        W32(bytes, f + 16, 0x00090008u);                      // 产品版本:与文件版本不同的假数
        W32(bytes, f + 20, 0x000B000Au);
        return bytes;
    }

    /// <summary>
    /// 常见的资源目录树:类型层有一个按名字的项排在最前,然后是编号 3、16(版本)、24 三个类型;版本类型下默认只有编号 1 一项,
    /// 它下面是语言层,每个语言一个叶子(第一个叶子就是会被读的)。
    /// </summary>
    public static Node TypicalTree(byte[] firstLanguageBlob, params byte[][] moreLanguageBlobs)
    {
        Node filler() => Node.Dir(Id(1, Node.Dir(Id(0x409, Node.Leaf(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })))));
        var languages = new List<(bool Named, uint Id, Node Child)> { Id(0x412, Node.Leaf(firstLanguageBlob)) };
        uint language = 0x413;
        foreach (byte[] blob in moreLanguageBlobs)
        {
            languages.Add(Id(language++, Node.Leaf(blob)));
        }
        Node version = Node.Dir(Id(1, Node.Dir(languages.ToArray())));
        return Node.Dir(Named(filler()), Id(3, filler()), Id(16, version), Id(24, filler()));
    }

    /// <summary>用常见的树拼出一份完整的 PE32 文件。</summary>
    public static byte[] Build(ushort a, ushort b, ushort c, ushort d, Options options = null)
    {
        return Build(TypicalTree(VersionBlob(a, b, c, d)), options);
    }

    /// <summary>把任意资源目录树拼成完整的 PE 文件。</summary>
    public static byte[] Build(Node root, Options options = null)
    {
        options ??= new Options();
        byte[] rsrc = BuildResourceSection(root, options.ResourceRva);

        int optionalSize = options.Pe32Plus ? 240 : 224;
        uint fileEnd = options.ResourceRawPointer + (uint)rsrc.Length;
        var image = new byte[Math.Max(0x400u, fileEnd)];

        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        W32(image, 0x3C, 0x80);

        int pe = 0x80;
        image[pe] = (byte)'P';
        image[pe + 1] = (byte)'E';
        W16(image, pe + 4, options.Pe32Plus ? 0x8664u : 0x14Cu);          // 机器类型
        W16(image, pe + 6, 2);                                            // 两个节:.text 与 .rsrc
        W16(image, pe + 20, (uint)optionalSize);
        W16(image, pe + 22, 0x0102);

        int optional = pe + 24;
        W16(image, optional, options.Pe32Plus ? 0x20Bu : 0x10Bu);
        int countOffset = optional + (options.Pe32Plus ? 108 : 92);
        int directories = optional + (options.Pe32Plus ? 112 : 96);
        W32(image, countOffset, options.DirectoryCount);
        if (options.DeclareResourceDirectory && options.DirectoryCount > 2)
        {
            W32(image, directories + 2 * 8, options.ResourceRva);
            W32(image, directories + 2 * 8 + 4, (uint)rsrc.Length);
        }

        int sections = optional + optionalSize;
        WriteSection(image, sections, ".text", 0x200, 0x1000, 0x200, 0x200);
        WriteSection(image, sections + 40, ".rsrc", (uint)rsrc.Length, options.ResourceRva, (uint)rsrc.Length, options.ResourceRawPointer);

        Array.Copy(rsrc, 0, image, options.ResourceRawPointer, rsrc.Length);
        return image;
    }

    private static void WriteSection(byte[] image, int at, string name, uint virtualSize, uint virtualAddress, uint rawSize, uint rawPointer)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        Array.Copy(nameBytes, 0, image, at, Math.Min(8, nameBytes.Length));
        W32(image, at + 8, virtualSize);
        W32(image, at + 12, virtualAddress);
        W32(image, at + 16, rawSize);
        W32(image, at + 20, rawPointer);
    }

    // 资源节内容:先是所有目录(深度优先排),再是所有数据项(16 字节),最后是所有数据块(4 字节对齐)
    private static byte[] BuildResourceSection(Node root, uint rsrcRva)
    {
        var directories = new List<Node>();
        var leaves = new List<Node>();
        void collect(Node node)
        {
            if (node.Data != null)
            {
                leaves.Add(node);
                return;
            }
            directories.Add(node);
            foreach (var entry in node.Entries)
            {
                collect(entry.Child);
            }
        }
        collect(root);

        var directoryOffset = new Dictionary<Node, int>();
        int cursor = 0;
        foreach (Node directory in directories)
        {
            directoryOffset[directory] = cursor;
            cursor += 16 + 8 * directory.Entries.Count;
        }
        var entryOffset = new Dictionary<Node, int>();
        foreach (Node leaf in leaves)
        {
            entryOffset[leaf] = cursor;
            cursor += 16;
        }
        var blobOffset = new Dictionary<Node, int>();
        foreach (Node leaf in leaves)
        {
            cursor = (cursor + 3) & ~3;
            blobOffset[leaf] = cursor;
            cursor += leaf.Data.Length;
        }

        var bytes = new byte[(cursor + 3) & ~3];
        foreach (Node directory in directories)
        {
            int at = directoryOffset[directory];
            int named = directory.Entries.Count(e => e.Named);
            W16(bytes, at + 12, (uint)named);
            W16(bytes, at + 14, (uint)(directory.Entries.Count - named));
            int entry = at + 16;
            foreach ((bool isNamed, uint id, Node child) in directory.Entries)
            {
                W32(bytes, entry, isNamed ? 0x80000000u : id);
                W32(bytes, entry + 4, child.Data != null ? (uint)entryOffset[child] : 0x80000000u | (uint)directoryOffset[child]);
                entry += 8;
            }
        }
        foreach (Node leaf in leaves)
        {
            int at = entryOffset[leaf];
            W32(bytes, at, rsrcRva + (uint)blobOffset[leaf]);          // 数据的 RVA
            W32(bytes, at + 4, (uint)leaf.Data.Length);
            Array.Copy(leaf.Data, 0, bytes, blobOffset[leaf], leaf.Data.Length);
        }
        return bytes;
    }

    private static void W16(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private static void W32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }
}
