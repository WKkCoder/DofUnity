using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Dnf.Data;
using UnityEngine;

namespace DnfUnity.Smoke
{
    /// <summary>
    /// 播放器(不是编辑器)里的冒烟自检:数据层包在目标平台的运行时(Mono 或 IL2CPP)上,
    /// 与运行时相关的几条路径(Span 强转、DeflateStream、SHA-256、CRC-32、PVF 解密)是否行为正确。
    /// 期望值与 Assets/Tests/EditMode/DataLayerSmokeTests.cs 相同,来自 Python 参考实现。
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
