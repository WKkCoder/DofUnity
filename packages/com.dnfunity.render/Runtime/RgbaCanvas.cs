using System;

namespace Dnf.Render
{
    /// <summary>
    /// CPU 合成画布(RGBA8888)。它是 Unity 渲染路径的对照标准:同一份绘制指令,CPU 合成与 GPU 渲染的结果应当一致。
    /// 混合规则:alpha = 255 的像素直接覆盖(已用原版屏幕输出校验,证据 E-023);alpha = 0 跳过;
    /// 半透明像素按 out = (src*a + dst*(255-a) + 127) / 255 取整,这个取整方式**没有**与原版比较过。
    /// </summary>
    public sealed class RgbaCanvas
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Pixels { get; }

        public RgbaCanvas(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }
            Width = width;
            Height = height;
            Pixels = new byte[checked(width * height * 4)];
        }

        public void Clear(byte r, byte g, byte b, byte a)
        {
            for (int i = 0; i < Pixels.Length; i += 4)
            {
                Pixels[i] = r;
                Pixels[i + 1] = g;
                Pixels[i + 2] = b;
                Pixels[i + 3] = a;
            }
        }

        /// <summary>把像素块的左上角放在 (x, y) 处绘制,超出画布的部分裁掉。</summary>
        public void Draw(SpriteFrame sprite, int x, int y)
        {
            Draw(sprite, x, y, false);
        }

        /// <summary>
        /// 同上;<paramref name="mirrored"/> 为真时像素块水平翻转(左右颠倒),(x, y) 仍然是翻转后像素块的左上角。
        /// </summary>
        public void Draw(SpriteFrame sprite, int x, int y, bool mirrored)
        {
            Draw(sprite, x, y, mirrored, SpriteBlend.Normal, 255);
        }

        /// <summary>
        /// 同上,并指定混合方式与染色 alpha(0 到 255)。
        /// 加法混合 <see cref="SpriteBlend.Additive"/>:对每个 alpha 不为 0 的像素,
        /// dst' = min(255, round(dst + src × src.a/255 × tintAlpha/255)),按通道独立,整个式子只在最后四舍五入一次。
        /// 这条规则在原版截图上对 GatePillarLight 的 1,366 个像素逐像素精确成立、对 GateLight1 的 44,200 个像素 98.94% 精确成立(证据 E-030)。
        /// 普通混合忽略染色 alpha(它在普通混合下怎样生效没有核对过)。画布自己的 alpha 通道在加法混合时保持不变。
        /// </summary>
        public void Draw(SpriteFrame sprite, int x, int y, bool mirrored, SpriteBlend blend, int tintAlpha)
        {
            if (sprite == null)
            {
                throw new ArgumentNullException(nameof(sprite));
            }
            if (blend == SpriteBlend.Additive)
            {
                DrawAdditive(sprite, x, y, mirrored, Math.Max(0, Math.Min(255, tintAlpha)));
                return;
            }
            int x0 = Math.Max(0, -x);
            int y0 = Math.Max(0, -y);
            int x1 = Math.Min(sprite.Width, Width - x);
            int y1 = Math.Min(sprite.Height, Height - y);
            byte[] src = sprite.Rgba;
            for (int sy = y0; sy < y1; sy++)
            {
                int rowStart = sy * sprite.Width * 4;
                int dof = ((y + sy) * Width + x + x0) * 4;
                for (int sx = x0; sx < x1; sx++, dof += 4)
                {
                    int so = rowStart + (mirrored ? sprite.Width - 1 - sx : sx) * 4;
                    int a = src[so + 3];
                    if (a == 0)
                    {
                        continue;
                    }
                    if (a == 255)
                    {
                        Pixels[dof] = src[so];
                        Pixels[dof + 1] = src[so + 1];
                        Pixels[dof + 2] = src[so + 2];
                        Pixels[dof + 3] = 255;
                        continue;
                    }
                    int inv = 255 - a;
                    int da = Pixels[dof + 3];
                    if (da == 255)
                    {
                        // 常见情形:画布是不透明的"屏幕"
                        Pixels[dof] = (byte)((src[so] * a + Pixels[dof] * inv + 127) / 255);
                        Pixels[dof + 1] = (byte)((src[so + 1] * a + Pixels[dof + 1] * inv + 127) / 255);
                        Pixels[dof + 2] = (byte)((src[so + 2] * a + Pixels[dof + 2] * inv + 127) / 255);
                    }
                    else if (da == 0)
                    {
                        Pixels[dof] = src[so];
                        Pixels[dof + 1] = src[so + 1];
                        Pixels[dof + 2] = src[so + 2];
                        Pixels[dof + 3] = (byte)a;
                    }
                    else
                    {
                        // 透明画布上的一般 over(直通 alpha):outA = a + da*(1-a)
                        int outA = a + (da * inv + 127) / 255;
                        long wSrc = (long)a * 255;
                        long wDst = (long)da * inv;
                        long denom = wSrc + wDst;
                        Pixels[dof] = (byte)((src[so] * wSrc + Pixels[dof] * wDst + denom / 2) / denom);
                        Pixels[dof + 1] = (byte)((src[so + 1] * wSrc + Pixels[dof + 1] * wDst + denom / 2) / denom);
                        Pixels[dof + 2] = (byte)((src[so + 2] * wSrc + Pixels[dof + 2] * wDst + denom / 2) / denom);
                        Pixels[dof + 3] = (byte)outA;
                    }
                }
            }
        }

        private void DrawAdditive(SpriteFrame sprite, int x, int y, bool mirrored, int tintAlpha)
        {
            if (tintAlpha == 0)
            {
                return;
            }
            int x0 = Math.Max(0, -x);
            int y0 = Math.Max(0, -y);
            int x1 = Math.Min(sprite.Width, Width - x);
            int y1 = Math.Min(sprite.Height, Height - y);
            byte[] src = sprite.Rgba;
            const int Denominator = 255 * 255;
            for (int sy = y0; sy < y1; sy++)
            {
                int rowStart = sy * sprite.Width * 4;
                int dof = ((y + sy) * Width + x + x0) * 4;
                for (int sx = x0; sx < x1; sx++, dof += 4)
                {
                    int so = rowStart + (mirrored ? sprite.Width - 1 - sx : sx) * 4;
                    int weight = src[so + 3] * tintAlpha;
                    if (weight == 0)
                    {
                        continue;
                    }
                    // round(dst + src × weight / 65025) = floor((2 × (dst × 65025 + src × weight) + 65025) / (2 × 65025));
                    // 65025 是奇数,所以不会出现恰好 .5 的平局,与浮点写法逐位相同。
                    for (int c = 0; c < 3; c++)
                    {
                        int numerator = Pixels[dof + c] * Denominator + src[so + c] * weight;
                        int value = (2 * numerator + Denominator) / (2 * Denominator);
                        Pixels[dof + c] = (byte)(value > 255 ? 255 : value);
                    }
                }
            }
        }
    }
}
