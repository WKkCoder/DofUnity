using System.Buffers.Binary;
using System.IO.Compression;

namespace Dnf.Cli.Analysis;

/// <summary>解出的截图:每个像素是 0x00RRGGBB(丢掉 alpha)。</summary>
internal sealed class Raster
{
    public int Width { get; }
    public int Height { get; }
    public uint[] Pixels { get; }

    public Raster(int width, int height, uint[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }
}

/// <summary>
/// 最小 PNG 解码器,只服务于"拿截图去对精灵":8 位 RGB 或 RGBA、不隔行。
/// 不依赖第三方图像库,所以 Cli 不用额外的包。别的格式一律报错,不静默地读歪。
/// </summary>
internal static class PngReader
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static Raster Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 33 || !data.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new UserError("不是 PNG 文件: " + path);
        }

        int pos = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        using var idat = new MemoryStream();
        bool seenHeader = false;
        while (pos + 8 <= data.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            int bodyStart = pos + 8;
            if (len > int.MaxValue || bodyStart + (long)len + 4 > data.Length)
            {
                throw new UserError("PNG 块长度越界: " + type);
            }
            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart));
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 4));
                    bitDepth = data[bodyStart + 8];
                    colorType = data[bodyStart + 9];
                    interlace = data[bodyStart + 12];
                    seenHeader = true;
                    break;
                case "IDAT":
                    idat.Write(data, bodyStart, (int)len);
                    break;
            }
            pos = bodyStart + (int)len + 4;
            if (type == "IEND")
            {
                break;
            }
        }

        if (!seenHeader)
        {
            throw new UserError("PNG 缺少 IHDR");
        }
        if (bitDepth != 8 || (colorType != 2 && colorType != 6) || interlace != 0)
        {
            throw new UserError("只支持 8 位 RGB/RGBA、不隔行的 PNG;这张是位深 " + bitDepth + "、颜色类型 " + colorType + "、隔行 " + interlace);
        }

        int bpp = colorType == 6 ? 4 : 3;
        int stride = width * bpp;
        byte[] raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int n = z.Read(raw, read, raw.Length - read);
                if (n <= 0)
                {
                    throw new UserError("PNG 像素数据被截断");
                }
                read += n;
            }
        }

        byte[] cur = new byte[stride];
        byte[] prev = new byte[stride];
        var pixels = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (stride + 1);
            int filter = raw[rowStart];
            ReadOnlySpan<byte> src = raw.AsSpan(rowStart + 1, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0;
                int b = prev[i];
                int c = i >= bpp ? prev[i - bpp] : 0;
                int x = src[i];
                cur[i] = filter switch
                {
                    0 => (byte)x,
                    1 => (byte)(x + a),
                    2 => (byte)(x + b),
                    3 => (byte)(x + ((a + b) >> 1)),
                    4 => (byte)(x + Paeth(a, b, c)),
                    _ => throw new UserError("PNG 未知滤波类型 " + filter),
                };
            }
            for (int px = 0; px < width; px++)
            {
                int o = px * bpp;
                pixels[y * width + px] = ((uint)cur[o] << 16) | ((uint)cur[o + 1] << 8) | cur[o + 2];
            }
            (cur, prev) = (prev, cur);
        }
        return new Raster(width, height, pixels);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }
        return pb <= pc ? b : c;
    }
}
