namespace Dnf.Render
{
    /// <summary>
    /// 城镇画面里"世界坐标 → 屏幕坐标"的加法模型(证据 E-024):
    ///   像素块左上角 x = 世界 x − 相机 x + 帧偏移 x + IMG 帧头 x
    ///   像素块左上角 y = 世界 y − 世界 z + 地面常数 + 帧偏移 y + IMG 帧头 y
    /// 相机 x 与地面常数在 E-024 里是用一个 NPC 拟合出来的(24 与 200),另一个 NPC 做了独立检验;
    /// 它们怎么由相机逻辑、地图宽度与主角位置算出来,还没有研究。"− 世界 z"没有 z 非零的样本,只是惯例假设。
    /// </summary>
    public readonly struct TownProjection
    {
        public int CameraX { get; }
        public int GroundY { get; }

        public TownProjection(int cameraX, int groundY)
        {
            CameraX = cameraX;
            GroundY = groundY;
        }

        /// <summary>E-024 在那张城镇截图上拟合出的常数。</summary>
        public static TownProjection FromE024 => new TownProjection(24, 200);

        public void TopLeft(int worldX, int worldY, int worldZ, int aniX, int aniY, int imgX, int imgY, out int screenX, out int screenY)
        {
            screenX = worldX - CameraX + aniX + imgX;
            screenY = worldY - worldZ + GroundY + aniY + imgY;
        }
    }
}
