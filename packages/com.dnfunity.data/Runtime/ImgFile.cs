using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Dnf.Data
{
    public static class ImgPixelFormat
    {
        public const int Argb1555 = 0x0E;
        public const int Argb4444 = 0x0F;
        public const int Argb8888 = 0x10;

        /// <summary>链接帧:索引里只有 8 字节(类型 + 目标帧号),像素取自目标帧。</summary>
        public const int Link = 0x11;

        /// <summary>每像素字节数;未知类型返回 0。</summary>
        public static int BytesPerPixel(int type)
        {
            switch (type)
            {
                case Argb1555:
                case Argb4444:
                    return 2;
                case Argb8888:
                    return 4;
                default:
                    return 0;
            }
        }
    }

    /// <summary>IMG 帧索引项。链接帧只有 <see cref="LinkTarget"/> 有意义。</summary>
    public readonly struct ImgFrameInfo
    {
        public int Type { get; }
        public int Compress { get; }
        public int Width { get; }
        public int Height { get; }

        /// <summary>索引里的 size 字段。compress==6 时是压缩后长度;compress==5 时对 16 位格式按 32 位计,不能当存储长度用。</summary>
        public int Size { get; }

        public int X { get; }
        public int Y { get; }
        public int FrameWidth { get; }
        public int FrameHeight { get; }
        public int LinkTarget { get; }

        public bool IsLink => Type == ImgPixelFormat.Link;

        public ImgFrameInfo(int type, int compress, int width, int height, int size, int x, int y, int frameWidth, int frameHeight)
        {
            Type = type;
            Compress = compress;
            Width = width;
            Height = height;
            Size = size;
            X = x;
            Y = y;
            FrameWidth = frameWidth;
            FrameHeight = frameHeight;
            LinkTarget = -1;
        }

        private ImgFrameInfo(int linkTarget)
        {
            Type = ImgPixelFormat.Link;
            Compress = 0;
            Width = 0;
            Height = 0;
            Size = 0;
            X = 0;
            Y = 0;
            FrameWidth = 0;
            FrameHeight = 0;
            LinkTarget = linkTarget;
        }

        public static ImgFrameInfo MakeLink(int target) => new ImgFrameInfo(target);
    }

    public enum ImgDecodeStatus
    {
        Ok,

        /// <summary>
        /// 1×1(或空)帧的 zlib 流被截断。真实数据里有 97 个这样的占位帧(类型 0x0E、compress 6、size 4),
        /// 按全透明像素处理。
        /// </summary>
        TruncatedPlaceholder,
    }

    /// <summary>
    /// IMG v2:32 字节头 = 16 字节魔数("Neople Img File" + NUL)+ i64 indexSize + i32 version(2)+ i32 frameCount。
    /// 索引项:链接帧 8 字节(i32 0x11, i32 target),其他帧 36 字节(i32 type, compress, w, h, size, x, y, frameW, frameH)。
    /// 帧数据从 32 + indexSize 开始,按索引顺序紧挨着。像素格式 0x0E ARGB1555、0x0F ARGB4444、0x10 ARGB8888;
    /// compress 5 = 未压缩,6 = zlib。
    /// </summary>
    public sealed class ImgFile
    {
        private const int MaxLinkHops = 16;
        private static readonly byte[] V2Prefix = System.Text.Encoding.ASCII.GetBytes("Neople Img File");
        private static readonly byte[] LegacyPrefix = System.Text.Encoding.ASCII.GetBytes("Neople Image Fi");

        private readonly byte[] _data;
        private readonly ImgFrameInfo[] _frames;
        private readonly long[] _offsets;

        public int FrameCount => _frames.Length;

        /// <summary>按索引推算出的整个 IMG 应有的字节数。真实数据里与条目大小逐字节相等。</summary>
        public long ExpectedLength { get; }

        public static bool IsV2(ReadOnlySpan<byte> data) => HasPrefix(data, V2Prefix);

        /// <summary>旧版 "Neople Image File"。本库不解析。</summary>
        public static bool IsLegacy(ReadOnlySpan<byte> data) => HasPrefix(data, LegacyPrefix);

        private static bool HasPrefix(ReadOnlySpan<byte> data, byte[] prefix)
        {
            if (data.Length < prefix.Length)
            {
                return false;
            }
            for (int i = 0; i < prefix.Length; i++)
            {
                if (data[i] != prefix[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>帧数据在文件里实际占的字节数(见 <see cref="ImgFrameInfo.Size"/> 的说明)。</summary>
        public static long StoredLength(in ImgFrameInfo f)
        {
            if (f.Compress == 6)
            {
                return f.Size;
            }
            int bpp = ImgPixelFormat.BytesPerPixel(f.Type);
            return (long)f.Width * f.Height * (bpp == 0 ? 4 : bpp);
        }

        /// <summary>解析索引。数据数组由本对象持有,不会被复制,调用方此后不要修改它。</summary>
        public static ImgFile Parse(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            return new ImgFile(data);
        }

        private ImgFile(byte[] data)
        {
            _data = data;
            if (data.Length < 32 || !HasPrefix(data, V2Prefix))
            {
                throw new DnfFormatException("IMG 魔数不对或文件太短");
            }
            long indexSize = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16));
            int version = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24));
            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(28));
            if (version != 2)
            {
                throw new DnfFormatException("IMG 版本不是 2: " + version);
            }
            if (count < 0 || count > data.Length || indexSize < 0 || 32L + indexSize > data.Length)
            {
                throw new DnfFormatException("IMG 头不合理: indexSize=" + indexSize + " frameCount=" + count);
            }

            _frames = new ImgFrameInfo[count];
            int pos = 32;
            long end = 32L + indexSize;
            for (int i = 0; i < count; i++)
            {
                if (pos + 8 > data.Length)
                {
                    throw new DnfFormatException("IMG 索引越界(帧 " + i + ")");
                }
                int type = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));
                if (type == ImgPixelFormat.Link)
                {
                    _frames[i] = ImgFrameInfo.MakeLink(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4)));
                    pos += 8;
                }
                else
                {
                    if (pos + 36 > data.Length)
                    {
                        throw new DnfFormatException("IMG 索引越界(帧 " + i + ")");
                    }
                    ReadOnlySpan<byte> r = data.AsSpan(pos + 4, 32);
                    _frames[i] = new ImgFrameInfo(
                        type,
                        BinaryPrimitives.ReadInt32LittleEndian(r),
                        BinaryPrimitives.ReadInt32LittleEndian(r.Slice(4)),
                        BinaryPrimitives.ReadInt32LittleEndian(r.Slice(8)),
                        BinaryPrimitives.ReadInt32LittleEndian(r.Slice(12)),
                        BinaryPrimitives.ReadInt32LittleEndian(r.Slice(16)),
                        BinaryPrimitives.ReadInt32LittleEndian(r.Slice(20)),
                        BinaryPrimitives.ReadInt32LittleEndian(r.Slice(24)),
                        BinaryPrimitives.ReadInt32LittleEndian(r.Slice(28)));
                    pos += 36;
                }
            }
            if (pos != end)
            {
                throw new DnfFormatException("IMG 索引区长度不符:解析得 " + pos + ",头里是 " + end);
            }

            _offsets = new long[count];
            long d = end;
            for (int i = 0; i < count; i++)
            {
                if (_frames[i].IsLink)
                {
                    _offsets[i] = -1;
                }
                else
                {
                    _offsets[i] = d;
                    d += StoredLength(_frames[i]);
                }
            }
            ExpectedLength = d;
        }

        /// <summary>帧的索引项(链接帧原样返回)。</summary>
        public ImgFrameInfo GetFrame(int index)
        {
            if ((uint)index >= (uint)_frames.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            return _frames[index];
        }

        /// <summary>沿链接帧走到真实帧,最多 16 跳。</summary>
        public int ResolveLink(int index)
        {
            if ((uint)index >= (uint)_frames.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }
            int hops = 0;
            while (_frames[index].IsLink)
            {
                index = _frames[index].LinkTarget;
                hops++;
                if (hops > MaxLinkHops || (uint)index >= (uint)_frames.Length)
                {
                    throw new DnfFormatException("链接帧链条不合理");
                }
            }
            return index;
        }

        /// <summary>解压后的原始像素字节(按存储格式,行优先,自上而下),链接帧会跟到目标帧。</summary>
        public byte[] DecodeRaw(int index)
        {
            return DecodeRaw(index, out _, false);
        }

        /// <param name="verifyChecksum">校验 zlib 尾部的 Adler-32。运行时不需要,全库验证时打开。</param>
        public byte[] DecodeRaw(int index, out ImgDecodeStatus status, bool verifyChecksum)
        {
            status = ImgDecodeStatus.Ok;
            int j = ResolveLink(index);
            ImgFrameInfo f = _frames[j];
            int bpp = ImgPixelFormat.BytesPerPixel(f.Type);
            if (bpp == 0)
            {
                throw new DnfFormatException("未知像素类型 0x" + f.Type.ToString("X") + "(帧 " + j + ")");
            }
            if (f.Width < 0 || f.Height < 0)
            {
                throw new DnfFormatException("帧尺寸为负(帧 " + j + ")");
            }
            long wantLong = (long)f.Width * f.Height * bpp;
            if (wantLong > int.MaxValue)
            {
                throw new DnfFormatException("帧尺寸过大(帧 " + j + ")");
            }
            int want = (int)wantLong;
            long stored = StoredLength(f);
            long offset = _offsets[j];
            if (stored < 0 || offset < 0 || offset + stored > _data.Length)
            {
                throw new DnfFormatException("帧数据超出 IMG(帧 " + j + ")");
            }

            if (f.Compress == 5)
            {
                var raw = new byte[want];
                Buffer.BlockCopy(_data, (int)offset, raw, 0, want);
                return raw;
            }
            if (f.Compress != 6)
            {
                throw new DnfFormatException("未知压缩方式 " + f.Compress + "(帧 " + j + ")");
            }
            byte[] inflated = Inflate((int)offset, (int)stored, want, verifyChecksum, out bool complete);
            if (complete)
            {
                return inflated;
            }
            if ((long)f.Width * f.Height <= 1)
            {
                status = ImgDecodeStatus.TruncatedPlaceholder;
                return new byte[want];
            }
            throw new DnfFormatException("zlib 流被截断或损坏(帧 " + j + ")");
        }

        private byte[] Inflate(int offset, int size, int expected, bool verifyChecksum, out bool complete)
        {
            complete = false;
            var result = new byte[expected];
            if (size < 2)
            {
                return result;
            }
            int cmf = _data[offset];
            int flg = _data[offset + 1];
            if ((cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
            {
                return result;
            }
            try
            {
                using (var input = new MemoryStream(_data, offset + 2, size - 2, false))
                using (var inflater = new DeflateStream(input, CompressionMode.Decompress))
                {
                    int total = 0;
                    while (total < expected)
                    {
                        int n = inflater.Read(result, total, expected - total);
                        if (n <= 0)
                        {
                            break;
                        }
                        total += n;
                    }
                    if (total != expected)
                    {
                        return result;
                    }
                    if (verifyChecksum)
                    {
                        if (inflater.ReadByte() >= 0)
                        {
                            throw new DnfFormatException("zlib 输出比预期长");
                        }
                        if (size < 6)
                        {
                            return result;
                        }
                        uint stored = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset + size - 4));
                        if (stored != Adler32.Compute(result))
                        {
                            throw new DnfFormatException("zlib Adler-32 校验失败");
                        }
                    }
                    complete = true;
                    return result;
                }
            }
            catch (InvalidDataException)
            {
                return result;
            }
        }

        /// <summary>解码为 RGBA8888(R、G、B、A 字节顺序,行优先,自上而下)。链接帧会跟到目标帧。</summary>
        public byte[] DecodeRgba(int index)
        {
            return DecodeRgba(index, out _);
        }

        public byte[] DecodeRgba(int index, out ImgFrameInfo resolved)
        {
            int j = ResolveLink(index);
            resolved = _frames[j];
            byte[] raw = DecodeRaw(j, out _, false);
            return ImgPixels.ToRgba(resolved.Type, raw, resolved.Width * resolved.Height);
        }
    }
}
