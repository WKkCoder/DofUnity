using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 伤害数字的"标志"整数:客户端生成函数(0x007f9610,第六个参数)交给构造函数(0x007f9160)的位集合,决定用哪一套数字图和哪一条动画(证据 E-107)。
    /// 构造函数的取法(观察,读反汇编):位 0 → 黄色一套;否则位 1 → 红橙色;否则位 3 → 青色;否则位 2 → 按数的正负取绿色或灰白;都没有 → 灰白。
    /// 位 4 → 暴击(第 1 条动画,另外在数字上挂 CriticalHit.ani,这里没做);否则位 5 → 第 2 条动画(缩放固定 1.2,不弹出);都没有 → 第 0 条动画。
    /// 命中处理的主调用点(0x00e63255 一带)的标志是:施加者是本机主角 → 位 0;受击者是本机主角 → 位 1;暴击 → 位 4;其余调用点(状态伤害一类,推断)带位 5。
    /// 位的含义里"施加者/受击者是本机主角"是观察到的比较;位 2、3、5 对应哪类事件是推断(它们的调用点没有逐个追)。
    /// </summary>
    public static class DamageNumberFlags
    {
        /// <summary>位 0:施加者是本机主角(主角打出去的伤害,黄色)。</summary>
        public const int AttackerIsHero = 1;

        /// <summary>位 1:受击者是本机主角(主角挨的伤害,红橙色)。</summary>
        public const int VictimIsHero = 2;

        /// <summary>位 2:按数的正负取绿色(正)或灰白(负)(推断:恢复类)。</summary>
        public const int BySign = 4;

        /// <summary>位 3:青色(推断:魔法值类)。</summary>
        public const int Cyan = 8;

        /// <summary>位 4:暴击。</summary>
        public const int Critical = 0x10;

        /// <summary>位 5:缩放固定为 1.2 的那一条动画(推断:状态伤害一类)。</summary>
        public const int Steady = 0x20;

        // 数字图 combo_big.img 里各套的 "0" 所在帧(每套十帧,0 到 9 顺序排)
        public const int GreyFirstFrame = 44;
        public const int RedFirstFrame = 54;
        public const int GreenFirstFrame = 64;
        public const int CyanFirstFrame = 74;
        public const int YellowFirstFrame = 143;

        /// <summary>这组标志选的数字图里 "0" 的帧号。</summary>
        public static int FirstFrame(int flags, int value)
        {
            if ((flags & AttackerIsHero) != 0)
            {
                return YellowFirstFrame;
            }
            if ((flags & VictimIsHero) != 0)
            {
                return RedFirstFrame;
            }
            if ((flags & Cyan) != 0)
            {
                return CyanFirstFrame;
            }
            if ((flags & BySign) != 0)
            {
                return value < 0 ? GreyFirstFrame : GreenFirstFrame;
            }
            return GreyFirstFrame;
        }

        /// <summary>这组标志选的动画模板:1 = 暴击,2 = 固定缩放,0 = 普通。</summary>
        public static int Template(int flags)
        {
            if ((flags & Critical) != 0)
            {
                return 1;
            }
            return (flags >> 4) & 2;
        }
    }

    /// <summary>伤害数字在某一时刻的姿态。</summary>
    public readonly struct DamageNumberPose
    {
        /// <summary>整体缩放(以数字的原点为中心:水平居中、竖直在顶边)。</summary>
        public double Scale { get; }

        /// <summary>竖直偏移(像素,向上为负)。</summary>
        public double YOffset { get; }

        /// <summary>整体不透明度,0 到 255。</summary>
        public int Alpha { get; }

        public DamageNumberPose(double scale, double yOffset, int alpha)
        {
            Scale = scale;
            YOffset = yOffset;
            Alpha = alpha;
        }
    }

    /// <summary>
    /// 伤害数字的动画:五个关键帧的小动画,客户端生成函数(0x007f9610)里一次建好三条模板(证据 E-107)。
    /// 每个关键帧有停留毫秒、缩放、竖直偏移、染色 alpha 与"向下一帧插值"开关,与 .ani 的帧属性是同一套机制(<see cref="AniTimeline"/>、证据 E-030):
    /// 带开关的帧在停留时间内从自己的值线性过渡到下一帧的值,不带开关的帧停住。
    /// 模板 0:(200 毫秒,缩放 2.0 → 1.0)、(400,停住)、(200,偏移 0 → -39、alpha 255 → 128)、(200,偏移 -39 → -69、alpha 128 → 0)、末帧。
    /// 模板 1(暴击)的停留是模板 0 的 1.5 倍(300、600、300、300);模板 2 缩放固定 1.2,停留同模板 0。
    /// 线性插值与各帧的数值经基线核对(同一个数字的两次测量:缩放 1.76 与 1.25 相隔 100 毫秒,偏移 -9 在 640 毫秒左右)。
    /// </summary>
    public static class DamageNumberAnimation
    {
        private readonly struct Key
        {
            public Key(int ms, double scale, int y, int alpha, bool interpolate)
            {
                Ms = ms;
                Scale = scale;
                Y = y;
                Alpha = alpha;
                Interpolate = interpolate;
            }

            public int Ms { get; }
            public double Scale { get; }
            public int Y { get; }
            public int Alpha { get; }
            public bool Interpolate { get; }
        }

        // 偏移 -39、-69 与 alpha 0x80 → 0 来自第 3、4 帧的写入;缩放 2.0、1.0、1.2 取自常量 0x01562188、0x015620fc、0x0156a55c
        private static readonly Key[][] Templates =
        {
            new[]
            {
                new Key(200, 2.0, 0, 255, true), new Key(400, 1.0, 0, 255, false), new Key(200, 1.0, 0, 255, true),
                new Key(200, 1.0, -39, 128, true), new Key(0, 1.0, -69, 0, false),
            },
            new[]
            {
                new Key(300, 2.0, 0, 255, true), new Key(600, 1.0, 0, 255, false), new Key(300, 1.0, 0, 255, true),
                new Key(300, 1.0, -39, 128, true), new Key(0, 1.0, -69, 0, false),
            },
            new[]
            {
                new Key(200, 1.2, 0, 255, true), new Key(400, 1.2, 0, 255, false), new Key(200, 1.2, 0, 255, true),
                new Key(200, 1.2, -39, 128, true), new Key(0, 1.2, -69, 0, false),
            },
        };

        /// <summary>这条模板从弹出到消失的总毫秒数。</summary>
        public static int DurationMs(int template)
        {
            int total = 0;
            foreach (Key k in Templates[Clamp(template)])
            {
                total += k.Ms;
            }
            return total;
        }

        /// <summary>
        /// 时刻 <paramref name="ageMs"/>(从弹出算)的姿态;已经播完(到了末帧)返回 false,调用方该把这个数字摘掉。
        /// </summary>
        public static bool TryPose(int template, double ageMs, out DamageNumberPose pose)
        {
            Key[] keys = Templates[Clamp(template)];
            if (ageMs < 0)
            {
                ageMs = 0;
            }
            double start = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                Key k = keys[i];
                double end = start + k.Ms;
                if (ageMs < end)
                {
                    if (k.Interpolate && i + 1 < keys.Length)
                    {
                        Key next = keys[i + 1];
                        double p = (ageMs - start) / k.Ms;
                        pose = new DamageNumberPose(
                            k.Scale + (next.Scale - k.Scale) * p,
                            k.Y + (next.Y - k.Y) * p,
                            (int)Math.Round(k.Alpha + (next.Alpha - k.Alpha) * p, MidpointRounding.AwayFromZero));
                    }
                    else
                    {
                        pose = new DamageNumberPose(k.Scale, k.Y, k.Alpha);
                    }
                    return true;
                }
                start = end;
            }
            pose = default;
            return false;
        }

        private static int Clamp(int template)
        {
            return template < 0 ? 0 : (template >= Templates.Length ? Templates.Length - 1 : template);
        }
    }

    /// <summary>
    /// 伤害数字的画法(证据 E-107):数字是图包 <c>sprite/common/etc/combo_big.img</c> 里的位图字,每套十帧;
    /// 客户端把一串数字按"相邻两个数字叠 6 个像素(数字 1 叠 5 个)"依次画进一张贴图,再把这张贴图当一帧交给动画;
    /// 贴图的位置:对象的原点在命中点横坐标减去推进宽度之半,贴图左边在原点再往左推进宽度之半(整数右移),缩放以原点为中心(水平居中、竖直在顶边),双线性过滤。
    /// 叠加层精灵没有缩放,所以这里把贴图按缩放比例预先双线性放大成新的帧(缩放量化到 1/32,按内容缓存,GPU 端按帧对象缓存纹理)。
    /// </summary>
    public sealed class DamageNumberArt
    {
        public const string Image = "sprite/common/etc/combo_big.img";

        /// <summary>最多画几位:客户端把绝对值夹在 9999999。</summary>
        public const int MaxValue = 9999999;

        /// <summary>数字叠加的图层大类:盖住场景里的全部精灵、在界面(血条、底栏)之下;聊天框一类的界面在数字之上(基线上聊天文字盖着数字)。</summary>
        public const int Rank = OverlayArt.DimRank;

        private const int ScaleSteps = 32;
        private const int CacheLimit = 512;

        private sealed class Composed
        {
            public Composed(SpriteFrame padded, int advance)
            {
                Padded = padded;
                Advance = advance;
            }

            /// <summary>四周各留一个像素透明边的贴图(缩放时边缘能淡出,与客户端的大贴图一样)。</summary>
            public SpriteFrame Padded { get; }

            /// <summary>各个数字的推进宽度之和(客户端记下的"总宽",不含最后一个数字多出来的那几个像素)。</summary>
            public int Advance { get; }
        }

        private readonly ISpriteSource _sprites;
        private readonly Dictionary<string, Composed> _composed = new Dictionary<string, Composed>();
        private readonly Dictionary<string, SpriteFrame> _scaled = new Dictionary<string, SpriteFrame>();

        private DamageNumberArt(ISpriteSource sprites)
        {
            _sprites = sprites;
        }

        /// <summary>造数字画法;图包里没有黄色那一套的 "0" 就返回 null(调用方退回占位文字)。</summary>
        public static DamageNumberArt For(ISpriteSource sprites)
        {
            if (sprites == null)
            {
                return null;
            }
            return sprites.TryGetFrame(Image, DamageNumberFlags.YellowFirstFrame, out SpriteFrame zero) && zero != null
                ? new DamageNumberArt(sprites)
                : null;
        }

        /// <summary>要画的数字串:绝对值夹在 0 到 9999999 之间的十进制(与客户端一致)。</summary>
        public static string Digits(int value)
        {
            long abs = value < 0 ? -(long)value : value;
            if (abs > MaxValue)
            {
                abs = MaxValue;
            }
            return abs.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>一串数字的推进宽度之和;图包里缺帧返回 -1。</summary>
        public int AdvanceWidth(int value, int firstFrame)
        {
            Composed c = Compose(value, firstFrame);
            return c == null ? -1 : c.Advance;
        }

        /// <summary>
        /// 这一帧画哪一个精灵;动画播完、图包缺帧返回 null。
        /// 数字对象的世界位置是 (<see cref="HitPopup.WorldX"/> 减推进宽度之半, <see cref="HitPopup.WorldY"/>, <see cref="HitPopup.Z"/>),
        /// 按城镇的加法模型(<see cref="TownProjection"/>)投到屏幕上;动画偏移再加在竖直方向。
        /// </summary>
        public SceneSprite Place(HitPopup popup, TownProjection projection, int order)
        {
            if (popup == null || !DamageNumberAnimation.TryPose(popup.Template, popup.AgeMs, out DamageNumberPose pose))
            {
                return null;
            }
            Composed c = Compose(popup.Value, popup.FirstFrame);
            if (c == null)
            {
                return null;
            }
            double s = Math.Round(pose.Scale * ScaleSteps) / ScaleSteps;
            SpriteFrame frame = Scaled(c, popup.Value, popup.FirstFrame, s);
            int half = c.Advance >> 1;
            int originY = (int)Math.Floor(popup.WorldY) + projection.GroundY - popup.Z;
            double originX = (int)Math.Floor(popup.WorldX) - half - projection.CameraX;
            // 贴图左边 = 原点 - 半宽 × 缩放(以原点为中心缩放);再减去一个像素的透明边(也按缩放)
            double left = originX - half * s - s;
            double top = originY + pose.YOffset - s;
            return new SceneSprite(frame, (int)Math.Floor(left + 0.5), (int)Math.Floor(top + 0.5), order, -3, "damage", 0, false, SpriteBlend.Normal, pose.Alpha, Rank);
        }

        private Composed Compose(int value, int firstFrame)
        {
            string digits = Digits(value);
            string key = firstFrame.ToString(CultureInfo.InvariantCulture) + ":" + digits;
            if (_composed.TryGetValue(key, out Composed cached))
            {
                return cached;
            }
            var frames = new SpriteFrame[digits.Length];
            var xs = new int[digits.Length];
            int advance = 0, inkWidth = 0, inkHeight = 0;
            for (int i = 0; i < digits.Length; i++)
            {
                if (!_sprites.TryGetFrame(Image, firstFrame + (digits[i] - '0'), out SpriteFrame f) || f == null)
                {
                    return null;
                }
                frames[i] = f;
                xs[i] = advance;
                inkWidth = Math.Max(inkWidth, advance + f.Width);
                inkHeight = Math.Max(inkHeight, f.Height);
                advance += f.Width - (digits[i] == '1' ? 5 : 6);
            }
            if (_composed.Count >= CacheLimit)
            {
                _composed.Clear();
                _scaled.Clear();
            }
            int pw = inkWidth + 2, ph = inkHeight + 2;
            var rgba = new byte[checked(pw * ph * 4)];
            for (int i = 0; i < frames.Length; i++)
            {
                Over(rgba, pw, frames[i], xs[i] + 1, 1);
            }
            var composed = new Composed(new SpriteFrame(pw, ph, 0, 0, pw, ph, rgba), advance);
            _composed[key] = composed;
            return composed;
        }

        private SpriteFrame Scaled(Composed c, int value, int firstFrame, double scale)
        {
            if (Math.Abs(scale - 1.0) < 1e-9)
            {
                return c.Padded;
            }
            string key = firstFrame.ToString(CultureInfo.InvariantCulture) + ":" + Digits(value) + ":" + ((int)Math.Round(scale * ScaleSteps)).ToString(CultureInfo.InvariantCulture);
            if (_scaled.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            if (_scaled.Count >= CacheLimit * 4)
            {
                _scaled.Clear();
            }
            int dw = Math.Max(1, (int)Math.Floor(c.Padded.Width * scale + 0.5));
            int dh = Math.Max(1, (int)Math.Floor(c.Padded.Height * scale + 0.5));
            SpriteFrame result = Bilinear(c.Padded, dw, dh);
            _scaled[key] = result;
            return result;
        }

        /// <summary>把 src 按"先画的在下"叠到 dst(RGBA,非预乘,宽 dstWidth)的 (x, y) 处。</summary>
        private static void Over(byte[] dst, int dstWidth, SpriteFrame src, int x, int y)
        {
            for (int row = 0; row < src.Height; row++)
            {
                for (int col = 0; col < src.Width; col++)
                {
                    int so = (row * src.Width + col) * 4;
                    int sa = src.Rgba[so + 3];
                    if (sa == 0)
                    {
                        continue;
                    }
                    int doff = ((y + row) * dstWidth + x + col) * 4;
                    int da = dst[doff + 3];
                    if (sa == 255 || da == 0)
                    {
                        dst[doff] = src.Rgba[so];
                        dst[doff + 1] = src.Rgba[so + 1];
                        dst[doff + 2] = src.Rgba[so + 2];
                        dst[doff + 3] = (byte)sa;
                        continue;
                    }
                    // 一般的"源盖在目标上":a = sa + da (1 - sa/255);颜色按各自的贡献加权
                    double sw = sa / 255.0;
                    double dw = da / 255.0 * (1.0 - sw);
                    double outA = sw + dw;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        dst[doff + ch] = (byte)Math.Min(255, (int)Math.Round((src.Rgba[so + ch] * sw + dst[doff + ch] * dw) / outA));
                    }
                    dst[doff + 3] = (byte)Math.Min(255, (int)Math.Round(outA * 255.0));
                }
            }
        }

        /// <summary>双线性放缩(像素中心对齐,边缘夹住),RGBA 四个通道分别插值,最后四舍五入。</summary>
        public static SpriteFrame Bilinear(SpriteFrame src, int dw, int dh)
        {
            var rgba = new byte[checked(dw * dh * 4)];
            double sx = (double)src.Width / dw, sy = (double)src.Height / dh;
            for (int y = 0; y < dh; y++)
            {
                double v = (y + 0.5) * sy - 0.5;
                int y0 = (int)Math.Floor(v);
                double fy = v - y0;
                int y1 = Math.Min(src.Height - 1, y0 + 1);
                y0 = Math.Max(0, y0);
                for (int x = 0; x < dw; x++)
                {
                    double u = (x + 0.5) * sx - 0.5;
                    int x0 = (int)Math.Floor(u);
                    double fx = u - x0;
                    int x1 = Math.Min(src.Width - 1, x0 + 1);
                    x0 = Math.Max(0, x0);
                    int o = (y * dw + x) * 4;
                    for (int ch = 0; ch < 4; ch++)
                    {
                        double top = src.Rgba[(y0 * src.Width + x0) * 4 + ch] * (1 - fx) + src.Rgba[(y0 * src.Width + x1) * 4 + ch] * fx;
                        double bottom = src.Rgba[(y1 * src.Width + x0) * 4 + ch] * (1 - fx) + src.Rgba[(y1 * src.Width + x1) * 4 + ch] * fx;
                        rgba[o + ch] = (byte)Math.Min(255, (int)Math.Floor(top * (1 - fy) + bottom * fy + 0.5));
                    }
                }
            }
            return new SpriteFrame(dw, dh, 0, 0, dw, dh, rgba);
        }
    }
}
