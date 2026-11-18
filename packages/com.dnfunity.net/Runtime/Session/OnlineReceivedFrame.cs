using System;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 城镇心跳泵收到的非心跳帧只读快照(含类 1 命令回包与类 0 通知;不含 0/0 心跳帧与 561 信封外壳)。
    /// 载荷数组为独立副本(末尾可能带有会话加密补零,实际长度按协议字段读法确定),改动它不会影响会话或别的订阅者。
    /// <see cref="ToString"/> 只含包类、包号、长度与压缩标志,不含任何载荷内容或机密。
    /// </summary>
    public sealed class OnlineReceivedFrame
    {
        private readonly byte[] _payload;

        internal OnlineReceivedFrame(PacketClass packetClass, int packetId, byte[] payload, bool wasCompressed)
        {
            PacketClass = packetClass;
            PacketId = packetId;
            _payload = CloneBytes(payload);
            WasCompressed = wasCompressed;
        }

        /// <summary>包类(例如命令回包为 <see cref="PacketClass.Cmd"/>,服务端通知为 <see cref="PacketClass.Noti"/>)。</summary>
        public PacketClass PacketClass { get; }

        /// <summary>包号。</summary>
        public int PacketId { get; }

        /// <summary>解码(压缩包号已解压)后的载荷副本;每次读取返回独立复制件,末尾可能带有会话加密补零。</summary>
        public byte[] Payload => CloneBytes(_payload);

        /// <summary>载荷字节长度(含末尾可能存在的会话加密补零)。</summary>
        public int PayloadLength => _payload.Length;

        /// <summary>该帧在帧层解码时是否经过了 zlib 解压(例如包号 2、9、0x57)。</summary>
        public bool WasCompressed { get; }

        /// <summary>脱敏摘要:只输出包类、包号、载荷长度与压缩标志,不含载荷内容。</summary>
        public override string ToString()
        {
            string cls = PacketClass == PacketClass.Cmd ? "CMD" : (PacketClass == PacketClass.Noti ? "NOTI" : "类" + (int)PacketClass);
            return cls + "/" + PacketId + "(" + _payload.Length + ")" + (WasCompressed ? ":compressed" : string.Empty);
        }

        private static byte[] CloneBytes(byte[] source)
        {
            if (source == null || source.Length == 0)
            {
                return Array.Empty<byte>();
            }
            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }
    }
}
