using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Dnf.Data;
using NUnit.Framework;

namespace DnfUnity.Tests
{
    /// <summary>
    /// 在 Unity 自带的运行时(编辑器里是 Mono)上跑数据层库的冒烟测试。
    /// 完整的合成数据单元测试在 src/Dnf.Data.Tests(xunit,.NET),这里只验证"包能被 Unity 编译,
    /// 并且与运行时有关的几条路径(Span 强转、DeflateStream、SHA-256)行为一致"。
    /// 期望值取自 tools/py/dnfdata.py 的 Python 参考实现。
    /// </summary>
    public class DataLayerSmokeTests
    {
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

        [TestCase(0u, "44960a4654861a5664b62a6674a63a7604d64a0614c65a1624f66a2634e67a36")]
        [TestCase(0x19036113u, "c09b6e0ad08b7e1ae0bb4e2af0ab5e3a80db2e4a90cb3e5aa0fb0e6ab0eb1e7a")]
        [TestCase(0xDEADBEEFu, "bf2070f9af3060e99f0050d98f1040c9ff6030b9ef7020a9df401099cf500089")]
        public void PvfCipher_MatchesPythonReference(uint crc, string expectedHex)
        {
            byte[] data = Ascending(32);
            PvfCipher.DecryptInPlace(data, crc);
            Assert.AreEqual(expectedHex, Hex(data));
        }

        // 期望值来自 Python 的 zlib.crc32(bytes(range(32)), seed)。
        [TestCase(0u, 0x91267E8Au)]
        [TestCase(0xDEADBEEFu, 0xCE7EA316u)]
        [TestCase(334998u, 0x40C677E0u)]
        public void Crc32_MatchesZlibReference(uint seed, uint expected)
        {
            Assert.AreEqual(expected, Crc32.Compute(Ascending(32), seed));
        }

        [Test]
        public void ImgPixels_MatchPythonReference()
        {
            byte[] p4444 = { 0x00, 0x00, 0x23, 0xF1, 0x21, 0x84, 0x34, 0x12, 0xFF, 0xFF };
            Assert.AreEqual("00000000112233ff4422118822334411ffffffff",
                Hex(ImgPixels.ToRgba(ImgPixelFormat.Argb4444, p4444, 5)));

            byte[] p1555 = { 0x00, 0x00, 0x00, 0x80, 0xE0, 0x83, 0xFF, 0x7F, 0xFF, 0xFF, 0x1F, 0x3C };
            Assert.AreEqual("00000000000000ff00ff00ffffffff00ffffffff7b00ff00",
                Hex(ImgPixels.ToRgba(ImgPixelFormat.Argb1555, p1555, 6)));

            byte[] p8888 = { 1, 2, 3, 4, 255, 0, 128, 255, 9, 8, 7, 6 };
            Assert.AreEqual("030201048000ffff07080906",
                Hex(ImgPixels.ToRgba(ImgPixelFormat.Argb8888, p8888, 3)));
        }

        /// <summary>造一个只含一帧 zlib 压缩 ARGB8888 的 IMG v2,检查 DeflateStream 路径与 Adler-32 校验。</summary>
        [Test]
        public void ImgZlibFrame_DecodesOnUnityRuntime()
        {
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
                Assert.AreEqual(img.Length, file.ExpectedLength);
                byte[] decoded = file.DecodeRaw(0, out ImgDecodeStatus status, true);
                Assert.AreEqual(ImgDecodeStatus.Ok, status);
                CollectionAssert.AreEqual(raw, decoded);
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

        /// <summary>
        /// 真实数据集成测试:只有设置了环境变量 DNF_DIR 与 DNF_EXPECT_PVF_AGGREGATE 才运行。
        /// 读出 Script.pvf 的全部文件,把"每个文件明文的十六进制 SHA-256 按目录顺序拼接后再哈希"与期望值比较,
        /// 同时按原版客户端的规则校验目录树与每个文件的 CRC-32(证据台账 E-011),要求全部通过。
        /// </summary>
        [Test]
        public void RealPvf_AggregateHashMatches()
        {
            string dir = Environment.GetEnvironmentVariable("DNF_DIR");
            string expected = Environment.GetEnvironmentVariable("DNF_EXPECT_PVF_AGGREGATE");
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(expected))
            {
                Assert.Ignore("未设置 DNF_DIR 与 DNF_EXPECT_PVF_AGGREGATE,跳过真实数据测试");
            }
            string path = Path.Combine(dir, "Script.pvf");
            Assume.That(File.Exists(path), "找不到 " + path);

            using (PvfArchive pvf = PvfArchive.Open(path))
            using (SHA256 aggregate = SHA256.Create())
            using (SHA256 each = SHA256.Create())
            {
                var input = new MemoryStream();
                int checksumFailures = 0;
                for (int i = 0; i < pvf.Count; i++)
                {
                    byte[] data = pvf.Read(pvf.Entries[i], out bool checksumOk);
                    if (!checksumOk)
                    {
                        checksumFailures++;
                    }
                    byte[] hash = each.ComputeHash(data);
                    byte[] hex = Encoding.ASCII.GetBytes(Hex(hash));
                    input.Write(hex, 0, hex.Length);
                }
                input.Position = 0;
                Assert.AreEqual(expected.ToLowerInvariant(), Hex(aggregate.ComputeHash(input)));
                Assert.AreEqual(334998, pvf.Count);
                Assert.IsTrue(pvf.DirectoryChecksumValid, "目录树校验应当通过");
                Assert.AreEqual(0, checksumFailures, "文件校验失败的个数");
            }
        }
    }
}
