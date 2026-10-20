using System;
using System.Collections.Generic;

namespace Dnf.Render
{
    /// <summary>
    /// 把一个精灵帧按固定比例缩小或放大,得到另一个精灵帧(证据 E-094:副本选择界面里没被选中的卡片是选中卡片的 0.7 倍)。
    /// 采样方式是客户端缩放贴图时用的双线性:目标像素 (x, y) 的中心 (x + 0.5, y + 0.5) 除以比例、减 0.5 得到源图坐标,
    /// 取周围四个像素按距离加权,四个通道(含 alpha)各自线性插值,不做预乘,边缘取最近的像素。
    /// 对基线里同一张卡片的图用这个模型拟合,平均每通道误差约 2 到 3(满分 255),比最近邻、盒式、双三次都小;徽章在缩放后与基线逐像素一致。
    /// 结果只由输入决定,所以 CPU 合成与 GPU 显示拿到的是同样的像素(对拍不受影响)。
    /// </summary>
    public static class SpriteScale
    {
        /// <summary>
        /// 缩放 <paramref name="frame"/>。像素块的宽高取 <c>ceil(原 × 比例)</c>,画布与偏移按比例四舍五入。
        /// 比例为 1 时原样返回同一个对象。比例必须为正。
        /// </summary>
        public static SpriteFrame Bilinear(SpriteFrame frame, double scale)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }
            if (!(scale > 0.0))
            {
                throw new ArgumentOutOfRangeException(nameof(scale), "比例必须为正");
            }
            if (scale == 1.0 || frame.Width == 0 || frame.Height == 0)
            {
                return frame;
            }
            int sw = frame.Width;
            int sh = frame.Height;
            int dw = Math.Max(1, (int)Math.Ceiling(sw * scale - 1e-9));
            int dh = Math.Max(1, (int)Math.Ceiling(sh * scale - 1e-9));
            byte[] src = frame.Rgba;
            var dst = new byte[dw * dh * 4];
            for (int y = 0; y < dh; y++)
            {
                double v = Clamp((y + 0.5) / scale - 0.5, 0.0, sh - 1);
                int y0 = (int)Math.Floor(v);
                int y1 = Math.Min(y0 + 1, sh - 1);
                double fy = v - y0;
                for (int x = 0; x < dw; x++)
                {
                    double u = Clamp((x + 0.5) / scale - 0.5, 0.0, sw - 1);
                    int x0 = (int)Math.Floor(u);
                    int x1 = Math.Min(x0 + 1, sw - 1);
                    double fx = u - x0;
                    int o = (y * dw + x) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        double top = src[(y0 * sw + x0) * 4 + c] * (1 - fx) + src[(y0 * sw + x1) * 4 + c] * fx;
                        double bottom = src[(y1 * sw + x0) * 4 + c] * (1 - fx) + src[(y1 * sw + x1) * 4 + c] * fx;
                        double value = top * (1 - fy) + bottom * fy;
                        dst[o + c] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(value, MidpointRounding.AwayFromZero)));
                    }
                }
            }
            return new SpriteFrame(
                dw,
                dh,
                (int)Math.Round(frame.OffsetX * scale, MidpointRounding.AwayFromZero),
                (int)Math.Round(frame.OffsetY * scale, MidpointRounding.AwayFromZero),
                Math.Max(dw, (int)Math.Round(frame.CanvasWidth * scale, MidpointRounding.AwayFromZero)),
                Math.Max(dh, (int)Math.Round(frame.CanvasHeight * scale, MidpointRounding.AwayFromZero)),
                dst);
        }

        private static double Clamp(double value, double low, double high)
        {
            return value < low ? low : (value > high ? high : value);
        }
    }

    /// <summary>
    /// <see cref="SpriteScale.Bilinear"/> 的缓存:同一个帧对象与同一个比例总是返回同一个缩放结果对象
    /// (Unity 端按帧对象缓存纹理,所以每帧反复取不会让贴图随画面增长)。
    /// </summary>
    public sealed class ScaledSpriteCache
    {
        private readonly Dictionary<SpriteFrame, Dictionary<double, SpriteFrame>> _cache =
            new Dictionary<SpriteFrame, Dictionary<double, SpriteFrame>>();

        public SpriteFrame Get(SpriteFrame frame, double scale)
        {
            if (frame == null)
            {
                return null;
            }
            if (scale == 1.0)
            {
                return frame;
            }
            if (!_cache.TryGetValue(frame, out Dictionary<double, SpriteFrame> byScale))
            {
                byScale = new Dictionary<double, SpriteFrame>();
                _cache.Add(frame, byScale);
            }
            if (!byScale.TryGetValue(scale, out SpriteFrame scaled))
            {
                scaled = SpriteScale.Bilinear(frame, scale);
                byScale.Add(scale, scaled);
            }
            return scaled;
        }
    }
}
