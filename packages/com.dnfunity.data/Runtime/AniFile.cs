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
        /// <summary>
        /// 图形特效:u16 类型(0 到 6),类型 5 后面多 3 字节。全库帧数:类型 2 280,153(98.6%)、5 2,117、4 895、3 604、1 368、0 82、6 24。
        /// 类型 2 是加法混合,已用城镇光效对原版截图核对(证据 E-030);其余类型的名字来自脚本头文件里的枚举 GRAPHICEFFECT_*(证据 E-037,
        /// 见 <see cref="AniGraphicEffect"/>),渲染公式**未核对**。
        /// </summary>
        public const ushort GraphicEffect = 11;

        /// <summary>
        /// 图像缩放,两个 f32 (x, y)(推断,证据 E-029):全库 89,607 帧带它,最常见的取值是 (-1, 1)(13,653 帧,水平镜像),
        /// 其余是 0.5 到 2.0 的缩放(多为 x 与 y 相同)。镜像用城镇里的两个 NPC 在原版截图上核对过(赛丽亚、露娜)。
        /// </summary>
        public const ushort ImageRate = 7;

        /// <summary>
        /// 旋转,一个 f32,弧度(推断,证据 E-029):取值集中在 ±π/2、π、±π/12、±π/6、±π/4 这类值上。围绕哪个点旋转**未知**。
        /// </summary>
        public const ushort ImageRotate = 8;

        /// <summary>
        /// 染色,4 个字节 R、G、B、A(推断,证据 E-029):最常见的是 (255,255,255,170)(41,750 帧),即白色乘半透明;
        /// 另有 (255,150,0,255) 橙、(255,0,0,255) 红、(0,0,0,255) 黑等。怎样与像素相乘**未验证**。
        /// </summary>
        public const ushort Rgba = 9;

        /// <summary>
        /// 与下一帧插值的开关,u8(推断,证据 E-030):全库 51,562 帧带它(取值 1 占 51,562,取值 0 占 82)。
        /// 带它的帧相邻两帧的染色不同的占 36.3%,不带的只有 2.1%(缩放 11.4% 对 1.6%,旋转 9.4% 对 2.3%);
        /// 城镇光效的实测里,三个光效层的染色 alpha 在同一时刻落在帧 0 与帧 1 之间的同一个线性插值位置上(见 E-030)。
        /// 只核对过"染色 alpha 线性插值";位置、缩放、旋转是否同样插值**未验证**。
        /// </summary>
        public const ushort Interpolation = 10;

        /// <summary>帧延时,i32,单位毫秒(取值 80、100、70、50、60 等,已核对)。</summary>
        public const ushort Delay = 12;

        /// <summary>u32 长度 + 字符串,形如音效键(取值如 HOLD,已核对)。</summary>
        public const ushort Sound = 16;

        /// <summary>
        /// 关键帧标志,i32 标志编号(推断,证据 E-036):全库 7,860 帧带它,取值 1 占 5,374、2 占 1,512、3 占 500;
        /// 魔法师(男)的 20 个可检验的状态脚本里,19 个脚本在 onKeyFrameFlag_* 中比较的编号全部出现在该状态所用动画的这个属性里
        /// (例如 IceCrash 的起手动画两帧带 1、2,收尾动画一帧带 3,正是脚本处理的 1、2、3)。引擎在动画播到这一帧时调用 onKeyFrameFlag 回调。
        /// </summary>
        public const ushort KeyFrameFlag = 23;

        /// <summary>
        /// 循环起点标记(没有负载)(推断,证据 E-039):全库 153,097 个多帧二进制 .ani 里 560 个带它;其中 553 个同时带 <see cref="LoopEnd"/>,
        /// 同一动画里它所在的帧在循环终点之前的占 539/553(97.5%)。对应文本 .ani 的 [LOOP START]。
        /// </summary>
        public const ushort LoopStart = 25;

        /// <summary>
        /// 循环终点标记,i32 重复次数(推断,证据 E-039):取值 99999(141 个)、9999(118)、999(65)、100(50)、3(45)、5、10、0……
        /// 大数表示"一直重复,直到脚本结束这个对象或换动画"。对应文本 .ani 的 [LOOP END]。
        /// 播放语义(播到该帧结束后跳回起点、次数是'再重复几次'还是'共几遍')**未验证**:只有一条脚本侧的旁证(冰封大地的柱子按帧号'超过第 6 帧又回到第 6 帧之前'数圈数,
        /// 它的动画正是帧 3 起点、帧 6 终点)。
        /// </summary>
        public const ushort LoopEnd = 26;
    }

    /// <summary>判定框类型(推断,证据 E-036):14 是受击框,15 是攻击框(攻击框只出现在出招的帧上)。</summary>
    public static class AniBoxType
    {
        public const ushort Damage = 14;
        public const ushort Attack = 15;
    }

    /// <summary>
    /// 图形特效类型的取值。名字来自脚本头文件 dnf_enum_header.nut 里的枚举 GRAPHICEFFECT_*(证据 E-037:
    /// NONE 0、DODGE 1、LINEARDODGE 2、DARK 3、XOR 4、MONOCHROME 5、SPACE_DISTORT 6、MAX 7)。
    /// 只有 <see cref="Additive"/>(即 LINEARDODGE,线性减淡 = 加法混合)用城镇光效对原版截图核对过(证据 E-030);
    /// 其余类型只知道名字,渲染公式**未核对**(DODGE 可能是颜色减淡、DARK 可能是变暗,都只是名字的字面意思)。
    /// </summary>
    public static class AniGraphicEffect
    {
        public const int None = 0;
        public const int Dodge = 1;

        /// <summary>加法混合(LINEARDODGE):dst' = min(255, round(dst + src.rgb × src.a/255 × 染色alpha/255))。</summary>
        public const int Additive = 2;
        public const int LinearDodge = 2;
        public const int Dark = 3;
        public const int Xor = 4;
        public const int Monochrome = 5;
        public const int SpaceDistort = 6;
    }

    /// <summary>
    /// 全局属性 id。结构(负载长度)在全库验证过;含义是推断:id 0 是"循环播放"(证据 E-025:
    /// 名字像 stay、wait、move、walk、run、idle 的多帧动画里 92% 带它,名字像 attack、damage、die、down、cast 的只有 2%)。
    /// </summary>
    public static class AniGlobalProperty
    {
        public const ushort Loop = 0;
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

        /// <summary>图像缩放(<see cref="AniFrameProperty.ImageRate"/>,推断)。没有该属性时返回 false(此时缩放为 1,1)。</summary>
        public bool TryGetScale(out float x, out float y)
        {
            foreach (AniProperty p in Properties)
            {
                if (p.Id == AniFrameProperty.ImageRate && p.Payload.Length == 8)
                {
                    x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(p.Payload));
                    y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(p.Payload, 4, 4)));
                    return true;
                }
            }
            x = 1f;
            y = 1f;
            return false;
        }

        /// <summary>旋转角,弧度(<see cref="AniFrameProperty.ImageRotate"/>,推断)。没有该属性时返回 false。</summary>
        public bool TryGetRotationRadians(out float radians)
        {
            foreach (AniProperty p in Properties)
            {
                if (p.Id == AniFrameProperty.ImageRotate && p.Payload.Length == 4)
                {
                    radians = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(p.Payload));
                    return true;
                }
            }
            radians = 0f;
            return false;
        }

        /// <summary>染色 R、G、B、A(<see cref="AniFrameProperty.Rgba"/>,推断)。没有该属性时返回 false(此时按白色不透明)。</summary>
        public bool TryGetTint(out byte r, out byte g, out byte b, out byte a)
        {
            foreach (AniProperty p in Properties)
            {
                if (p.Id == AniFrameProperty.Rgba && p.Payload.Length == 4)
                {
                    r = p.Payload[0];
                    g = p.Payload[1];
                    b = p.Payload[2];
                    a = p.Payload[3];
                    return true;
                }
            }
            r = g = b = a = 255;
            return false;
        }

        /// <summary>该帧是否带"与下一帧插值"开关(<see cref="AniFrameProperty.Interpolation"/>,推断,证据 E-030)。</summary>
        public bool InterpolatesToNext
        {
            get
            {
                foreach (AniProperty p in Properties)
                {
                    if (p.Id == AniFrameProperty.Interpolation)
                    {
                        return p.Payload.Length > 0 && p.Payload[0] != 0;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// 染色 alpha(0 到 255);没有染色属性时为 255。只用于加法混合的光效(证据 E-030);
        /// 对普通混合的帧,染色 alpha 怎样生效**未验证**。
        /// </summary>
        public int TintAlpha => TryGetTint(out _, out _, out _, out byte a) ? a : 255;

        /// <summary>图形特效类型(<see cref="AniFrameProperty.GraphicEffect"/>,取值 0 到 6,含义推断)。没有该属性时返回 false。</summary>
        public bool TryGetGraphicEffect(out int effectType)
        {
            foreach (AniProperty p in Properties)
            {
                if (p.Id == AniFrameProperty.GraphicEffect && p.Payload.Length >= 2)
                {
                    effectType = BinaryPrimitives.ReadUInt16LittleEndian(p.Payload);
                    return true;
                }
            }
            effectType = 0;
            return false;
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

        /// <summary>帧延时(毫秒);没有该属性时按 0。</summary>
        public int DelayMs => TryGetDelayMs(out int ms) ? ms : 0;

        /// <summary>这一帧是否带循环起点标记(<see cref="AniFrameProperty.LoopStart"/>,推断,证据 E-039)。</summary>
        public bool IsLoopStart
        {
            get
            {
                foreach (AniProperty p in Properties)
                {
                    if (p.Id == AniFrameProperty.LoopStart)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>循环终点标记的重复次数(<see cref="AniFrameProperty.LoopEnd"/>,推断,证据 E-039)。没有该属性时返回 false。</summary>
        public bool TryGetLoopEnd(out int repeat)
        {
            foreach (AniProperty p in Properties)
            {
                if (p.Id == AniFrameProperty.LoopEnd && p.Payload.Length == 4)
                {
                    repeat = BinaryPrimitives.ReadInt32LittleEndian(p.Payload);
                    return true;
                }
            }
            repeat = 0;
            return false;
        }

        /// <summary>关键帧标志编号(<see cref="AniFrameProperty.KeyFrameFlag"/>,推断,证据 E-036)。没有该属性时返回 false。</summary>
        public bool TryGetKeyFrameFlag(out int flag)
        {
            foreach (AniProperty p in Properties)
            {
                if (p.Id == AniFrameProperty.KeyFrameFlag && p.Payload.Length == 4)
                {
                    flag = BinaryPrimitives.ReadInt32LittleEndian(p.Payload);
                    return true;
                }
            }
            flag = 0;
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

        /// <summary>是否循环播放(全局属性 0 存在且负载首字节非 0;推断,见 <see cref="AniGlobalProperty"/>)。</summary>
        public bool Loops
        {
            get
            {
                foreach (AniProperty p in GlobalProperties)
                {
                    if (p.Id == AniGlobalProperty.Loop)
                    {
                        return p.Payload.Length > 0 && p.Payload[0] != 0;
                    }
                }
                return false;
            }
        }

        /// <summary>所有帧的延时之和(毫秒)。</summary>
        public int TotalDurationMs
        {
            get
            {
                long sum = 0;
                foreach (AniFrame f in Frames)
                {
                    sum += f.DelayMs;
                }
                return sum > int.MaxValue ? int.MaxValue : (int)sum;
            }
        }

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
