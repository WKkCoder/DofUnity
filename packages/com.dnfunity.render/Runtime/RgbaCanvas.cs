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
            if (sprite == null)
            {
                throw new ArgumentNullException(nameof(sprite));
            }
            int x0 = Math.Max(0, -x);
            int y0 = Math.Max(0, -y);
            int x1 = Math.Min(sprite.Width, Width - x);
            int y1 = Math.Min(sprite.Height, Height - y);
            byte[] src = sprite.Rgba;
            for (int sy = y0; sy < y1; sy++)
            {
                int so = (sy * sprite.Width + x0) * 4;
                int dof = ((y + sy) * Width + x + x0) * 4;
                for (int sx = x0; sx < x1; sx++, so += 4, dof += 4)
                {
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
    }
}
