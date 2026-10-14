using UnityEngine;

namespace Dnf.Client
{
    /// <summary>
    /// DNF 的逻辑屏幕:800×600,原点在左上角、y 向下。Unity 的世界坐标 y 向上,
    /// 这里约定 1 个世界单位 = 1 个逻辑像素,屏幕点 (sx, sy) 对应世界点 (sx, Height - sy)。
    /// </summary>
    public static class PixelScreen
    {
        public const int Width = 800;
        public const int Height = 600;

        /// <summary>屏幕坐标(左上角原点、y 向下)转世界坐标。</summary>
        public static Vector3 ToWorld(int screenX, int screenY, float z = 0f)
        {
            return new Vector3(screenX, Height - screenY, z);
        }

        /// <summary>
        /// 把相机配成"一个世界单位 = 一个逻辑像素"的正交相机:视口恰好覆盖 (0,0) 到 (800,600) 的世界范围。
        /// 渲染到 800×600 的目标时与 <see cref="Dnf.Render.RgbaCanvas"/> 逐像素对应。
        /// </summary>
        public static void Configure(Camera camera, Color background)
        {
            camera.orthographic = true;
            camera.orthographicSize = Height * 0.5f;
            camera.aspect = (float)Width / Height;
            camera.transform.position = new Vector3(Width * 0.5f, Height * 0.5f, -10f);
            camera.transform.rotation = Quaternion.identity;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = background;
            camera.allowMSAA = false;
            camera.allowHDR = false;
            camera.allowDynamicResolution = false;
        }
    }
}
