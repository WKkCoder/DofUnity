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
