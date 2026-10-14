using System.IO.Compression;
using Dnf.Data;

namespace Dnf.Cli.Analysis;

/// <summary>最小的 PNG 写入:8 位 RGBA、非隔行、每行滤波类型 0。只用来输出检查用的图片。</summary>
internal static class PngWriter
{
    public static void Write(string path, int width, int height, byte[] rgba)
    {
        if (rgba.Length != checked(width * height * 4))
        {
            throw new ArgumentException("RGBA 长度必须等于 宽 × 高 × 4", nameof(rgba));
        }
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBe(ihdr, 0, (uint)width);
        WriteBe(ihdr, 4, (uint)height);
        ihdr[8] = 8;   // 位深
        ihdr[9] = 6;   // RGBA
        Chunk(fs, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
        {
            var line = new byte[1 + width * 4];
            for (int y = 0; y < height; y++)
            {
                line[0] = 0;
                Buffer.BlockCopy(rgba, y * width * 4, line, 1, width * 4);
                z.Write(line, 0, line.Length);
            }
        }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, (uint)data.Length);
        s.Write(len);
        var body = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++)
        {
            body[i] = (byte)type[i];
        }
        Buffer.BlockCopy(data, 0, body, 4, data.Length);
        s.Write(body);
        var crc = new byte[4];
        WriteBe(crc, 0, Crc32.Compute(body));
        s.Write(crc);
    }

    private static void WriteBe(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }
}
