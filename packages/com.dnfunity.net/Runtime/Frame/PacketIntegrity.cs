using System;
using System.Security.Cryptography;

namespace Dnf.Net.Frame
{
    /// <summary>
    /// 包完整性("PI")两层校验里的第一层:载荷尾 4 字节折叠哈希(<c>_NS_MakeHash_</c> 0x0808c8b5)。
    /// 算法:对载荷算 MD5,把 16 个字节按固定的字节置换异或折成 4 个字节(第 0 字节 = d13^d10^d8^d0,第 1 字节 = d9^d12^d5^d1,
    /// 第 2 字节 = d6^d4^d14^d2,第 3 字节 = d15^d7^d11^d3),按小端拼成整数,再异或常量 0xBF1A7881。
    /// 只在载荷长度 1 到 400 时追加(服务端 <c>PacketBuf::finalize</c> 0x0858d548);长度 0 或超过 400 不追加。
    /// 帧里没有明文长度字段,所以接收方只能"按字段读完之后,拿游标位置算哈希再比较后面 4 字节"(见 <see cref="PacketReader.CheckFoldHash"/>)。
    /// </summary>
    public static partial class PacketIntegrity
    {
        /// <summary>追加折叠哈希的载荷长度上限(含)。</summary>
        public const int FoldHashMaxPayload = 400;

        /// <summary>折叠后再异或的常量。</summary>
        public const uint FoldHashXor = 0xBF1A7881u;

        /// <summary>
        /// 客户端到服务端的帧里,服务端会检查载荷尾折叠哈希的类 1 包号(只有这 17 个;类 0 的全部包号与其余类 1 包号不检查)。
        /// 不符时服务端只累加可疑事件计数,不拒收、不断开;每个包号前 101 次不符才计数。
        /// </summary>
        public static readonly int[] ClientFoldHashPacketIds =
        {
            30, 31, 41, 42, 49, 145, 264, 283, 284, 286, 294, 295, 319, 320, 334, 338, 367,
        };

        /// <summary>某个客户端到服务端的包是否需要在字段之后附折叠哈希。</summary>
        public static bool RequiresClientFoldHash(PacketClass packetClass, int packetId)
        {
            return packetClass == PacketClass.Cmd && Array.IndexOf(ClientFoldHashPacketIds, packetId) >= 0;
        }

        /// <summary>某个载荷长度是否会被服务端追加折叠哈希。</summary>
        public static bool HasFoldHash(int payloadLength)
        {
            return payloadLength >= 1 && payloadLength <= FoldHashMaxPayload;
        }

        /// <summary>折叠哈希(不检查长度范围,由调用方用 <see cref="HasFoldHash"/> 判断)。</summary>
        public static uint FoldHash(ReadOnlySpan<byte> payload)
        {
            byte[] d;
            using (MD5 md5 = MD5.Create())
            {
                d = md5.ComputeHash(payload.ToArray());
            }
            uint b0 = (uint)(d[13] ^ d[10] ^ d[8] ^ d[0]);
            uint b1 = (uint)(d[9] ^ d[12] ^ d[5] ^ d[1]);
            uint b2 = (uint)(d[6] ^ d[4] ^ d[14] ^ d[2]);
            uint b3 = (uint)(d[15] ^ d[7] ^ d[11] ^ d[3]);
            return ((b3 << 24) | (b2 << 16) | (b1 << 8) | b0) ^ FoldHashXor;
        }

        /// <summary>
        /// 返回载荷的副本;长度在 1 到 400 时尾部追加 4 字节折叠哈希(小端),否则不追加。
        /// </summary>
        public static byte[] AppendFoldHash(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (!HasFoldHash(payload.Length)) return (byte[])payload.Clone();
            uint hash = FoldHash(payload);
            byte[] result = new byte[payload.Length + 4];
            Buffer.BlockCopy(payload, 0, result, 0, payload.Length);
            result[payload.Length] = (byte)hash;
            result[payload.Length + 1] = (byte)(hash >> 8);
            result[payload.Length + 2] = (byte)(hash >> 16);
            result[payload.Length + 3] = (byte)(hash >> 24);
            return result;
        }
    }
}
