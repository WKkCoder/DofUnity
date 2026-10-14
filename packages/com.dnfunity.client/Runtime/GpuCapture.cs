using System;
using UnityEngine;

namespace Dnf.Client
{
    /// <summary>用一台相机渲染一帧到离屏目标并读回 CPU(RGBA8888,第一行在上)。</summary>
    public static class GpuCapture
    {
        public static byte[] Render(Camera camera, int width, int height)
        {
            if (camera == null)
            {
                throw new ArgumentNullException(nameof(camera));
            }
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                throw new InvalidOperationException("当前没有图形设备(是否带了 -nographics?),无法渲染");
            }
            RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 1);
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            Texture2D readback = null;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                readback = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                var raw = readback.GetRawTextureData<byte>();
                var result = new byte[width * height * 4];
                int stride = width * 4;
                for (int y = 0; y < height; y++)
                {
                    // Unity 的第 0 行在下面
                    NativeToManaged(raw, (height - 1 - y) * stride, result, y * stride, stride);
                }
                return result;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
                UnityObjects.Destroy(readback);
            }
        }

        private static void NativeToManaged(Unity.Collections.NativeArray<byte> src, int srcOffset, byte[] dst, int dstOffset, int count)
        {
            Unity.Collections.NativeArray<byte>.Copy(src, srcOffset, dst, dstOffset, count);
        }
    }
}
