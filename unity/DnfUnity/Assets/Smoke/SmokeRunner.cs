using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Dnf.Client;
using Dnf.Data;
using Dnf.Render;
using UnityEngine;

namespace DnfUnity.Smoke
{
    /// <summary>
    /// 播放器(不是编辑器)里的冒烟自检:数据层包在目标平台的运行时(Mono 或 IL2CPP)上,
    /// 与运行时相关的几条路径(Span 强转、DeflateStream、SHA-256、CRC-32、PVF 解密)是否行为正确。
    /// 期望值与 Assets/Tests/EditMode/DataLayerSmokeTests.cs 相同,来自 Python 参考实现。
    /// 有图形设备时(播放器不带 -nographics)还会做 GPU 与 CPU 合成器的对照(Dnf.Client.RenderSelfTest);
    /// 设置了环境变量 DNF_DIR 时再对真实城镇场景做一次。没有图形设备时这些检查被跳过(日志里有 SKIP 行)。
    /// 结果写进日志的一行:DNF_SMOKE_RESULT pass=N fail=M backend=... platform=...
    /// 批处理模式下(桌面播放器 -batchmode)自检完成后用退出码退出:0 通过,1 失败。
    /// </summary>
    public sealed class SmokeRunner : MonoBehaviour
    {
        private int _pass;
        private int _fail;

        private void Start()
        {
            try
            {
                RunAll();
            }
            catch (Exception e)
            {
                _fail++;
                Debug.LogError("DNF_SMOKE_EXCEPTION " + e);
            }

#if ENABLE_IL2CPP
            const string backend = "il2cpp";
#else
            const string backend = "mono";
#endif
            Debug.Log("DNF_SMOKE_RESULT pass=" + _pass + " fail=" + _fail +
                      " backend=" + backend + " platform=" + Application.platform +
                      " unity=" + Application.unityVersion);

            if (Application.isBatchMode)
            {
                Application.Quit(_fail == 0 ? 0 : 1);
            }
        }

        private void Check(string name, bool ok, string detail = "")
        {
            if (ok)
            {
                _pass++;
            }
            else
            {
                _fail++;
            }
            Debug.Log("DNF_SMOKE " + (ok ? "PASS " : "FAIL ") + name + (ok ? "" : " " + detail));
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        private static byte[] Ascending(int count)
        {
            var data = new byte[count];
            for (int i = 0; i < count; i++)
            {
                data[i] = (byte)i;
            }
            return data;
        }

        private void RunAll()
        {
            // CRC-32:期望值来自 Python 的 zlib.crc32(bytes(range(32)), seed)
            Check("crc32 seed 0", Crc32.Compute(Ascending(32), 0u) == 0x91267E8Au);
            Check("crc32 seed 0xDEADBEEF", Crc32.Compute(Ascending(32), 0xDEADBEEFu) == 0xCE7EA316u);
            Check("crc32 seed 334998", Crc32.Compute(Ascending(32), 334998u) == 0x40C677E0u);

            // PVF 解密
            byte[] data = Ascending(32);
            PvfCipher.DecryptInPlace(data, 0x19036113u);
            Check("pvf cipher crc 0x19036113",
                Hex(data) == "c09b6e0ad08b7e1ae0bb4e2af0ab5e3a80db2e4a90cb3e5aa0fb0e6ab0eb1e7a");

            // 像素格式转换
            byte[] p4444 = { 0x00, 0x00, 0x23, 0xF1, 0x21, 0x84, 0x34, 0x12, 0xFF, 0xFF };
            Check("img argb4444",
                Hex(ImgPixels.ToRgba(ImgPixelFormat.Argb4444, p4444, 5)) == "00000000112233ff4422118822334411ffffffff");
            // 1555:5 位通道按 round(v*255/31) 展开(与原版屏幕输出一致,证据 E-023);位复制在 v=3、7、24、28 上会差 1
            byte[] p1555 = { 0xF8, 0x8C, 0x03, 0xF3 };
            Check("img argb1555 rounding like the original client",
                Hex(ImgPixels.ToRgba(ImgPixelFormat.Argb1555, p1555, 2)) == "193ac5ffe6c519ff");
            byte[] p8888 = { 1, 2, 3, 4, 255, 0, 128, 255, 9, 8, 7, 6 };
            Check("img argb8888",
                Hex(ImgPixels.ToRgba(ImgPixelFormat.Argb8888, p8888, 3)) == "030201048000ffff07080906");

            // zlib 帧:DeflateStream + Adler-32
            const int w = 17, h = 9;
            var raw = new byte[w * h * 4];
            var rng = new System.Random(5);
            for (int i = 0; i < raw.Length; i++)
            {
                raw[i] = (byte)(rng.Next(4) * 60);
            }
            byte[] stored;
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var deflate = new DeflateStream(ms, CompressionMode.Compress, true))
                {
                    deflate.Write(raw, 0, raw.Length);
                }
                var adler = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32.Compute(raw));
                ms.Write(adler, 0, 4);
                stored = ms.ToArray();
            }
            using (var img = new MemoryStream())
            {
                img.Write(Encoding.ASCII.GetBytes("Neople Img File"), 0, 15);
                img.WriteByte(0);
                WriteInt64(img, 36);
                WriteInt32(img, 2);
                WriteInt32(img, 1);
                WriteInt32(img, ImgPixelFormat.Argb8888);
                WriteInt32(img, 6);
                WriteInt32(img, w);
                WriteInt32(img, h);
                WriteInt32(img, stored.Length);
                WriteInt32(img, 0);
                WriteInt32(img, 0);
                WriteInt32(img, w);
                WriteInt32(img, h);
                img.Write(stored, 0, stored.Length);

                ImgFile file = ImgFile.Parse(img.ToArray());
                byte[] decoded = file.DecodeRaw(0, out ImgDecodeStatus status, true);
                Check("img zlib frame status", status == ImgDecodeStatus.Ok, status.ToString());
                Check("img zlib frame bytes", decoded != null && decoded.Length == raw.Length &&
                                              Hex(decoded) == Hex(raw));
            }

            // SHA-256(清单哈希用到)
            using (var sha = SHA256.Create())
            {
                string hash = Hex(sha.ComputeHash(Encoding.ASCII.GetBytes("abc")));
                Check("sha256 abc",
                    hash == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
            }

            RunGpuChecks();
        }

        private void RunGpuChecks()
        {
            Check("gamma color space", QualitySettings.activeColorSpace == ColorSpace.Gamma, QualitySettings.activeColorSpace.ToString());
            if (!RenderSelfTest.GraphicsAvailable)
            {
                Debug.Log("DNF_SMOKE SKIP gpu checks: no graphics device (nographics)");
                return;
            }
            Debug.Log("DNF_SMOKE gpu device=" + SystemInfo.graphicsDeviceType + " name=" + SystemInfo.graphicsDeviceName);

            var bg = new Color32(40, 40, 40, 255);
            var none = new List<SceneSprite>();
            RenderSelfTest.Diff d = RenderSelfTest.Compare(RenderSelfTest.RenderGpu(none, bg), RenderSelfTest.RenderCpu(none, bg));
            Check("gpu empty stage is the background", d.AllExact, d.ToString());

            List<SceneSprite> opaque = RenderSelfTest.RandomSprites(1, 40, false);
            d = RenderSelfTest.Compare(RenderSelfTest.RenderGpu(opaque, bg), RenderSelfTest.RenderCpu(opaque, bg));
            Check("gpu opaque sprites equal cpu byte for byte", d.AllExact, d.ToString());

            List<SceneSprite> edges = RenderSelfTest.EdgeSprites();
            d = RenderSelfTest.Compare(RenderSelfTest.RenderGpu(edges, bg), RenderSelfTest.RenderCpu(edges, bg));
            Check("gpu placement is pixel exact at the screen edges", d.AllExact, d.ToString());

            List<SceneSprite> sweep = RenderSelfTest.AlphaSweep(100, 100);
            d = RenderSelfTest.Compare(RenderSelfTest.RenderGpu(sweep, bg), RenderSelfTest.RenderCpu(sweep, bg));
            Debug.Log("DNF_SMOKE gpu alpha sweep: " + d);
            Check("gpu semi-transparent single layer within 1 of cpu", d.MaxChannelDiff <= 1, d.ToString());

            List<SceneSprite> overlap = RenderSelfTest.RandomSprites(11, 30, true);
            d = RenderSelfTest.Compare(RenderSelfTest.RenderGpu(overlap, bg), RenderSelfTest.RenderCpu(overlap, bg));
            Debug.Log("DNF_SMOKE gpu overlapping semi-transparent: " + d);
            Check("gpu semi-transparent overlap within 3 of cpu", d.MaxChannelDiff <= 3, d.ToString());

            string dir = Environment.GetEnvironmentVariable("DNF_DIR");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Script.pvf")))
            {
                Debug.Log("DNF_SMOKE SKIP real town: DNF_DIR is not set");
                return;
            }
            using (DnfAssets assets = DnfAssets.Open(dir))
            using (var town = new TownView(assets, "map/elvengard/(r)gate.map", TownProjection.FromE024, bg))
            {
                List<SceneSprite> sprites = town.ShowAt(0);
                byte[] gpu = town.Stage.Capture();
                d = RenderSelfTest.Compare(gpu, RenderSelfTest.RenderCpu(sprites, bg));
                Debug.Log("DNF_SMOKE real town: npcs=" + town.NpcCount + " sprites=" + sprites.Count + " " + d);
                Check("gpu real town equals cpu", town.NpcCount == 5 && d.MaxChannelDiff <= 1, d.ToString());
            }
        }

        private static void WriteInt32(Stream s, int v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, v);
            s.Write(b, 0, 4);
        }

        private static void WriteInt64(Stream s, long v)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(b, v);
            s.Write(b, 0, 8);
        }
    }
}
