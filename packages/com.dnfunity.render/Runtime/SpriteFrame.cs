using System;

namespace Dnf.Render
{
    /// <summary>
    /// 一个解码好的精灵帧:RGBA8888 像素块,加上 IMG 帧头里的位置信息。
    /// 像素块 (Width × Height) 在帧画布 (CanvasWidth × CanvasHeight) 里的偏移是 (OffsetX, OffsetY),
    /// 与 .ani 的帧偏移相加后才是它相对物体原点的位置(证据 E-024)。
    /// </summary>
    public sealed class SpriteFrame
    {
        public int Width { get; }
        public int Height { get; }
        public int OffsetX { get; }
        public int OffsetY { get; }
        public int CanvasWidth { get; }
        public int CanvasHeight { get; }

        /// <summary>RGBA8888,行优先,不填充,长度 = Width * Height * 4。</summary>
        public byte[] Rgba { get; }

        private int _blank = -1;

        /// <summary>
        /// 所有像素都完全透明。换装的某一层在某些帧上没有东西(例如拿剑的后层),IMG 里用一个 1×1 的透明帧占位,
        /// 它带着一个很远的偏移,画出来什么也看不见,所以合成时可以直接跳过。
        /// </summary>
        public bool IsBlank
        {
            get
            {
                if (_blank < 0)
                {
                    bool blank = true;
                    for (int i = 3; i < Rgba.Length; i += 4)
                    {
                        if (Rgba[i] != 0)
                        {
                            blank = false;
                            break;
                        }
                    }
                    _blank = blank ? 1 : 0;
                }
                return _blank == 1;
            }
        }

        private SpriteFrame _silhouette;

        /// <summary>
        /// 同一帧的纯白剪影:颜色全部取 255、alpha 不变(怪物死亡时的白闪,证据 E-084)。每帧只生成一次并缓存,
        /// 所以 Unity 里按帧对象缓存的贴图不会随画面增长。
        /// </summary>
        public SpriteFrame Silhouette
        {
            get
            {
                if (_silhouette == null)
                {
                    var white = new byte[Rgba.Length];
                    for (int i = 0; i < Rgba.Length; i += 4)
                    {
                        white[i] = 255;
                        white[i + 1] = 255;
                        white[i + 2] = 255;
                        white[i + 3] = Rgba[i + 3];
                    }
                    _silhouette = new SpriteFrame(Width, Height, OffsetX, OffsetY, CanvasWidth, CanvasHeight, white);
                }
                return _silhouette;
            }
        }

        public SpriteFrame(int width, int height, int offsetX, int offsetY, int canvasWidth, int canvasHeight, byte[] rgba)
        {
            if (width < 0 || height < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }
            if (rgba == null || rgba.Length != checked(width * height * 4))
            {
                throw new ArgumentException("RGBA 长度必须等于 宽 × 高 × 4", nameof(rgba));
            }
            Width = width;
            Height = height;
            OffsetX = offsetX;
            OffsetY = offsetY;
            CanvasWidth = canvasWidth;
            CanvasHeight = canvasHeight;
            Rgba = rgba;
        }
    }

    /// <summary>按 IMG 名字与帧号取得精灵帧。名字不区分大小写,形如 "sprite/map/postbox.img"。</summary>
    public interface ISpriteSource
    {
        bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite);
    }
}
