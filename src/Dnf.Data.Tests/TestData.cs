using System.IO.Compression;
using System.Text;
using Dnf.Data;

namespace Dnf.Data.Tests;

/// <summary>合成数据构造器。测试里的"原版格式"数据都由这里按规格自己造出来,不依赖任何原版文件。</summary>
internal static class TestData
{
    public sealed record PvfFileSpec(string Path, byte[] Content, uint Crc);

    private static void U32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void I32(Stream s, int v) => U32(s, (uint)v);

    private static void U16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        s.Write(b);
    }

    private static byte[] Latin1Bytes(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            b[i] = (byte)s[i];
        }
        return b;
    }

    // ---------------------------------------------------------------- PVF

    public static (byte[] Pvf, int TrailingBytes) BuildPvf(
        IReadOnlyList<PvfFileSpec> files,
        uint treeCrc = 0x19036113u,
        uint version = 66282u,
        int extraTreeBytes = 1)
    {
        var data = new MemoryStream();
        var tree = new MemoryStream();
        uint fileNo = 0;
        foreach (PvfFileSpec f in files)
        {
            uint rel = (uint)data.Length;
            int padded = (f.Content.Length + 3) & ~3;
            var enc = new byte[padded];
            Array.Copy(f.Content, enc, f.Content.Length);
            for (int i = f.Content.Length; i < padded; i++)
            {
                enc[i] = 0xEE; // 对齐填充,解密后会被截掉
            }
            PvfCipher.EncryptInPlace(enc, f.Crc);
            data.Write(enc);

            U32(tree, fileNo++);
            U32(tree, (uint)f.Path.Length);
            tree.Write(Latin1Bytes(f.Path));
            U32(tree, (uint)f.Content.Length);
            U32(tree, f.Crc);
            U32(tree, rel);
        }
        int parsedLength = (int)tree.Length;
        tree.Write(new byte[extraTreeBytes]);
        while (tree.Length % 4 != 0)
        {
            tree.WriteByte(0);
        }
        byte[] treeBytes = tree.ToArray();
        int trailing = treeBytes.Length - parsedLength;
        PvfCipher.EncryptInPlace(treeBytes, treeCrc);

        var pvf = new MemoryStream();
        U32(pvf, 36);
        pvf.Write(Latin1Bytes(new string('G', 36)));
        U32(pvf, version);
        U32(pvf, (uint)treeBytes.Length);
        U32(pvf, treeCrc);
        U32(pvf, (uint)files.Count);
        pvf.Write(treeBytes);
        pvf.Write(data.ToArray());
        return (pvf.ToArray(), trailing);
    }

    // ---------------------------------------------------------------- stringtable / 标记流脚本

    public static byte[] BuildStringTable(IReadOnlyList<byte[]> strings)
    {
        var s = new MemoryStream();
        U32(s, (uint)strings.Count);
        uint off = (uint)((strings.Count + 1) * 4);
        foreach (byte[] item in strings)
        {
            U32(s, off);
            off += (uint)item.Length;
        }
        U32(s, off);
        foreach (byte[] item in strings)
        {
            s.Write(item);
        }
        return s.ToArray();
    }

    public static byte[] BuildTokenScript(IEnumerable<(byte Type, int Value)> tokens)
    {
        var s = new MemoryStream();
        s.WriteByte(0xB0);
        s.WriteByte(0xD0);
        foreach ((byte type, int value) in tokens)
        {
            s.WriteByte(type);
            I32(s, value);
        }
        return s.ToArray();
    }

    // ---------------------------------------------------------------- NPK

    private static byte[] NpkKey()
    {
        string text = ("puchikon@neople dungeon and fighter " + string.Concat(Enumerable.Repeat("DNF", 100))).Substring(0, 255) + "\0";
        return Latin1Bytes(text);
    }

    public static byte[] BuildNpk(IReadOnlyList<(string Name, byte[] Data)> entries)
    {
        byte[] key = NpkKey();
        var s = new MemoryStream();
        s.Write(Encoding.ASCII.GetBytes("NeoplePack_Bill\0"));
        U32(s, (uint)entries.Count);
        uint offset = (uint)(20 + 264 * entries.Count);
        foreach ((string name, byte[] data) in entries)
        {
            U32(s, offset);
            U32(s, (uint)data.Length);
            var raw = new byte[256];
            byte[] nameBytes = Latin1Bytes(name);
            Array.Copy(nameBytes, raw, nameBytes.Length);
            for (int i = 0; i < 256; i++)
            {
                raw[i] ^= key[i];
            }
            s.Write(raw);
            offset += (uint)data.Length;
        }
        foreach ((string _, byte[] data) in entries)
        {
            s.Write(data);
        }
        return s.ToArray();
    }

    // ---------------------------------------------------------------- IMG v2

    public sealed class ImgFrameSpec
    {
        public int Type;
        public int Compress;
        public int Width;
        public int Height;
        public int SizeField;
        public int X;
        public int Y;
        public int FrameWidth;
        public int FrameHeight;
        public byte[] Stored = Array.Empty<byte>();
        public int LinkTarget = -1;

        public static ImgFrameSpec Link(int target) => new ImgFrameSpec { Type = ImgPixelFormat.Link, LinkTarget = target };

        /// <summary>未压缩帧。16 位格式的 size 字段按真实 IMG 的习惯记成 32 位大小(是实际的两倍)。</summary>
        public static ImgFrameSpec Raw(int type, int w, int h, byte[] pixels)
        {
            return new ImgFrameSpec
            {
                Type = type, Compress = 5, Width = w, Height = h, SizeField = w * h * 4, X = 3, Y = -4,
                FrameWidth = w + 10, FrameHeight = h + 10, Stored = pixels,
            };
        }

        public static ImgFrameSpec Zlib(int type, int w, int h, byte[] pixels)
        {
            byte[] z = ZlibCompress(pixels);
            return new ImgFrameSpec
            {
                Type = type, Compress = 6, Width = w, Height = h, SizeField = z.Length, X = 1, Y = 2,
                FrameWidth = w, FrameHeight = h, Stored = z,
            };
        }
    }

    public static byte[] ZlibCompress(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, true))
        {
            z.Write(data);
        }
        return ms.ToArray();
    }

    public static byte[] BuildImg(IReadOnlyList<ImgFrameSpec> frames)
    {
        var index = new MemoryStream();
        foreach (ImgFrameSpec f in frames)
        {
            I32(index, f.Type);
            if (f.Type == ImgPixelFormat.Link)
            {
                I32(index, f.LinkTarget);
            }
            else
            {
                I32(index, f.Compress);
                I32(index, f.Width);
                I32(index, f.Height);
                I32(index, f.SizeField);
                I32(index, f.X);
                I32(index, f.Y);
                I32(index, f.FrameWidth);
                I32(index, f.FrameHeight);
            }
        }
        var s = new MemoryStream();
        s.Write(Encoding.ASCII.GetBytes("Neople Img File\0"));
        s.Write(BitConverter.GetBytes((long)index.Length));
        I32(s, 2);
        I32(s, frames.Count);
        s.Write(index.ToArray());
        foreach (ImgFrameSpec f in frames)
        {
            if (f.Type != ImgPixelFormat.Link)
            {
                s.Write(f.Stored);
            }
        }
        return s.ToArray();
    }

    // ---------------------------------------------------------------- ANI

    public sealed class AniFrameSpec
    {
        public int ImageIndex = -1;
        public int FrameIndex;
        public int X;
        public int Y;
        public List<(ushort Type, int[] Values)> Boxes = new();
        public List<(ushort Id, byte[] Payload)> Props = new();
    }

    public static byte[] BuildAni(string[] paths, IReadOnlyList<(ushort Id, byte[] Payload)> globals, IReadOnlyList<AniFrameSpec> frames)
    {
        var s = new MemoryStream();
        U16(s, (ushort)frames.Count);
        U16(s, (ushort)paths.Length);
        foreach (string p in paths)
        {
            U32(s, (uint)p.Length);
            s.Write(Latin1Bytes(p));
        }
        U16(s, (ushort)globals.Count);
        foreach ((ushort id, byte[] payload) in globals)
        {
            U16(s, id);
            s.Write(payload);
        }
        foreach (AniFrameSpec f in frames)
        {
            U16(s, (ushort)f.Boxes.Count);
            foreach ((ushort type, int[] values) in f.Boxes)
            {
                U16(s, type);
                foreach (int v in values)
                {
                    I32(s, v);
                }
            }
            if (f.ImageIndex < 0)
            {
                U16(s, 0xFFFF);
            }
            else
            {
                U16(s, (ushort)f.ImageIndex);
                U16(s, (ushort)f.FrameIndex);
            }
            I32(s, f.X);
            I32(s, f.Y);
            U16(s, (ushort)f.Props.Count);
            foreach ((ushort id, byte[] payload) in f.Props)
            {
                U16(s, id);
                s.Write(payload);
            }
        }
        return s.ToArray();
    }

    public static byte[] Bytes(params int[] values) => values.Select(v => (byte)v).ToArray();

    public static byte[] LE32(int v) => BitConverter.GetBytes(v);

    public static byte[] Str(string s)
    {
        var b = new List<byte>(BitConverter.GetBytes((uint)s.Length));
        b.AddRange(Latin1Bytes(s));
        return b.ToArray();
    }
}
