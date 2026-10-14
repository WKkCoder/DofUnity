using System;
using System.Buffers.Binary;

namespace Dnf.Data
{
    /// <summary>
    /// 帧属性 id。结构(负载长度)在全库 187,365 个二进制 .ani 上验证过;
    /// 含义除了 Delay 与 Sound 的取值已核对外,其余都是根据负载形状与取值的推断,没有引擎里的原表。
    /// </summary>
    public static class AniFrameProperty
    {
        /// <summary>图形特效:u16 类型(0 到 6),类型 5 后面多 3 字节。类型 2 占 98.6%,极可能是叠加类混合(推断)。</summary>
        public const ushort GraphicEffect = 11;

        /// <summary>帧延时,i32,单位毫秒(取值 80、100、70、50、60 等,已核对)。</summary>
        public const ushort Delay = 12;

        /// <summary>u32 长度 + 字符串,形如音效键(取值如 HOLD,已核对)。</summary>
        public const ushort Sound = 16;
    }

    public readonly struct AniProperty
    {
        public ushort Id { get; }
        public byte[] Payload { get; }

        public AniProperty(ushort id, byte[] payload)
        {
            Id = id;
            Payload = payload;
        }
    }

    /// <summary>判定框:u16 类型 + 6 个 i32。类型 14 疑为伤害框、15 疑为攻击框(推断);六个数的含义未标注。</summary>
    public readonly struct AniBox
    {
        public ushort Type { get; }
        public int V0 { get; }
        public int V1 { get; }
        public int V2 { get; }
        public int V3 { get; }
        public int V4 { get; }
        public int V5 { get; }

        public AniBox(ushort type, int v0, int v1, int v2, int v3, int v4, int v5)
        {
            Type = type;
            V0 = v0;
            V1 = v1;
            V2 = v2;
            V3 = v3;
            V4 = v4;
            V5 = v5;
        }
    }

    public sealed class AniFrame
    {
        /// <summary>图片表里的下标;没有图时为 -1。</summary>
        public int ImageIndex { get; }

        /// <summary>该图片(IMG)里的帧号;没有图时为 -1。</summary>
        public int FrameIndex { get; }

        public int X { get; }
        public int Y { get; }
        public AniBox[] Boxes { get; }
        public AniProperty[] Properties { get; }

        public AniFrame(int imageIndex, int frameIndex, int x, int y, AniBox[] boxes, AniProperty[] properties)
        {
            ImageIndex = imageIndex;
            FrameIndex = frameIndex;
            X = x;
            Y = y;
            Boxes = boxes;
            Properties = properties;
        }

        /// <summary>读取帧延时(毫秒)。没有该属性时返回 false。</summary>
        public bool TryGetDelayMs(out int milliseconds)
        {
            foreach (AniProperty p in Properties)
            {
                if (p.Id == AniFrameProperty.Delay && p.Payload.Length == 4)
                {
                    milliseconds = BinaryPrimitives.ReadInt32LittleEndian(p.Payload);
                    return true;
                }
            }
            milliseconds = 0;
            return false;
        }
    }

    /// <summary>
    /// 二进制 .ani:
    ///   u16 frameCount, u16 imageCount;
    ///   imageCount * (u32 len, path)   —— NPK 里的名字 = "sprite/" + path.ToLower();
    ///   u16 globalCount, globalCount * (u16 id + 负载);
    ///   frameCount * 帧:
    ///     u16 boxCount, boxCount * (u16 type + 6 * i32);
    ///     u16 imageIdx(0xFFFF 表示无图),有图时再跟 u16 frameIdx;
    ///     i32 x, i32 y, u16 propCount, propCount * (u16 id + 负载)。
    /// 路径里可能带 %02d%02d 之类的占位符,运行时由装备编号替换(替换规则未研究)。
    /// </summary>
    public sealed class AniFile
    {
        public string[] ImagePaths { get; }
        public AniProperty[] GlobalProperties { get; }
        public AniFrame[] Frames { get; }

        private AniFile(string[] paths, AniProperty[] globals, AniFrame[] frames)
        {
            ImagePaths = paths;
            GlobalProperties = globals;
            Frames = frames;
        }

        private static int GlobalPayloadSize(ushort id)
        {
            switch (id)
            {
                case 0: return 1;
                case 1: return 1;
                case 3: return 2;
                case 18: return 15;
                case 28: return 2;
                default: return -1;
            }
        }

        private static int FramePayloadSize(ushort id)
        {
            switch (id)
            {
                case 0: return 1;
                case 1: return 1;
                case 2: return 8;
                case 7: return 8;
                case 8: return 4;
                case 9: return 4;
                case 10: return 1;
                case 12: return 4;
                case 13: return 2;
                case 17: return 0;
                case 23: return 4;
                case 24: return 2;
                case 25: return 0;
                case 26: return 4;
                case 27: return 8;
                default: return -1;
            }
        }

        private ref struct Reader
        {
            private readonly ReadOnlySpan<byte> _d;

            public int Pos;

            public Reader(ReadOnlySpan<byte> data)
            {
                _d = data;
                Pos = 0;
            }

            public int Length => _d.Length;

            public void Need(int n, string what)
            {
                if (n < 0 || (long)Pos + n > _d.Length)
                {
                    throw new DnfFormatException("ANI 数据被截断:" + what + " @" + Pos);
                }
            }

            public ushort U16(string what)
            {
                Need(2, what);
                ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_d.Slice(Pos));
                Pos += 2;
                return v;
            }

            public int I32(string what)
            {
                Need(4, what);
                int v = BinaryPrimitives.ReadInt32LittleEndian(_d.Slice(Pos));
                Pos += 4;
                return v;
            }

            public uint U32(string what)
            {
                Need(4, what);
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(_d.Slice(Pos));
                Pos += 4;
                return v;
            }

            public ReadOnlySpan<byte> Bytes(int n, string what)
            {
                Need(n, what);
                ReadOnlySpan<byte> s = _d.Slice(Pos, n);
                Pos += n;
                return s;
            }

            public ushort PeekU16(int at, string what)
            {
                Need(at + 2 - Pos, what);
                return BinaryPrimitives.ReadUInt16LittleEndian(_d.Slice(at));
            }
        }

        /// <summary>解析一个二进制 .ani。任何与已知结构不符的地方都抛 <see cref="DnfFormatException"/>。</summary>
        public static AniFile Parse(ReadOnlySpan<byte> data)
        {
            if (data.Length < 8)
            {
                throw new DnfFormatException("ANI 太短: " + data.Length);
            }
            var r = new Reader(data);
            int frameCount = r.U16("frameCount");
            int imageCount = r.U16("imageCount");

            var paths = new string[imageCount];
            for (int i = 0; i < imageCount; i++)
            {
                uint len = r.U32("路径长度");
                if (len > 400)
                {
                    throw new DnfFormatException("ANI 图片路径长度不合理: " + len);
                }
                paths[i] = Latin1.Decode(r.Bytes((int)len, "图片路径"));
            }

            int globalCount = r.U16("globalCount");
            var globals = new AniProperty[globalCount];
            for (int i = 0; i < globalCount; i++)
            {
                ushort id = r.U16("全局属性 id");
                int size = GlobalPayloadSize(id);
                if (size < 0)
                {
                    throw new DnfFormatException("ANI 未知全局属性 id " + id);
                }
                globals[i] = new AniProperty(id, r.Bytes(size, "全局属性负载").ToArray());
            }

            var frames = new AniFrame[frameCount];
            for (int f = 0; f < frameCount; f++)
            {
                int boxCount = r.U16("boxCount");
                if (boxCount > 64)
                {
                    throw new DnfFormatException("ANI 判定框数量不合理: " + boxCount);
                }
                var boxes = new AniBox[boxCount];
                for (int b = 0; b < boxCount; b++)
                {
                    ushort type = r.U16("框类型");
                    boxes[b] = new AniBox(type, r.I32("框"), r.I32("框"), r.I32("框"), r.I32("框"), r.I32("框"), r.I32("框"));
                }

                int imageIdx = r.U16("imageIdx");
                int frameIdx = -1;
                if (imageIdx != 0xFFFF)
                {
                    frameIdx = r.U16("frameIdx");
                    if (imageIdx >= Math.Max(imageCount, 1))
                    {
                        throw new DnfFormatException("ANI imageIdx 越界: " + imageIdx);
                    }
                }
                else
                {
                    imageIdx = -1;
                }

                int x = r.I32("x");
                int y = r.I32("y");
                int propCount = r.U16("propCount");
                if (propCount > 40)
                {
                    throw new DnfFormatException("ANI 帧属性数量不合理: " + propCount);
                }
                var props = new AniProperty[propCount];
                for (int p = 0; p < propCount; p++)
                {
                    ushort id = r.U16("帧属性 id");
                    int size;
                    if (id == AniFrameProperty.GraphicEffect)
                    {
                        ushort effectType = r.PeekU16(r.Pos, "特效类型");
                        size = 2 + (effectType == 5 ? 3 : 0);
                    }
                    else if (id == AniFrameProperty.Sound)
                    {
                        uint strLen = BinaryPrimitives.ReadUInt32LittleEndian(r.Bytes(4, "字符串长度"));
                        if (strLen > 300)
                        {
                            throw new DnfFormatException("ANI 属性字符串长度不合理: " + strLen);
                        }
                        r.Pos -= 4;
                        size = 4 + (int)strLen;
                    }
                    else
                    {
                        size = FramePayloadSize(id);
                        if (size < 0)
                        {
                            throw new DnfFormatException("ANI 未知帧属性 id " + id);
                        }
                    }
                    props[p] = new AniProperty(id, r.Bytes(size, "帧属性负载").ToArray());
                }
                frames[f] = new AniFrame(imageIdx, frameIdx, x, y, boxes, props);
            }

            if (r.Pos != r.Length)
            {
                throw new DnfFormatException("ANI 末尾多出 " + (r.Length - r.Pos) + " 字节");
            }
            return new AniFile(paths, globals, frames);
        }
    }
}
