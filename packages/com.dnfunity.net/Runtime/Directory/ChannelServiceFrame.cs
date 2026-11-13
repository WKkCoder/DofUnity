using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Dnf.Net.ChannelDirectory
{
    /// <summary>
    /// Channel Service(7001)11 字节小端帧头:<c>&lt;BBIIB&gt;</c> = <c>byte0(1) + cmd(1) + totalLen(4) + reserved(4) + flag(1)</c>。
    /// <para>【观】<c>DNF.exe</c> <c>0x010f4c99/0x010f4efc/0x010f3fac/0x010f3ffd/0x010f4030</c>。</para>
    /// </summary>
    public readonly struct ChannelServiceFrameHeader
    {
        /// <summary>构造帧头。</summary>
        public ChannelServiceFrameHeader(byte byte0, byte command, uint totalLength, uint reserved, byte flag)
        {
            Byte0 = byte0;
            Command = command;
            TotalLength = totalLength;
            Reserved = reserved;
            Flag = flag;
        }

        /// <summary>第 0 字节(协议版本,通常为 0x00)。</summary>
        public byte Byte0 { get; }

        /// <summary>第 1 字节命令号。</summary>
        public byte Command { get; }

        /// <summary>第 2..5 字节小端总长度(含 11 字节帧头)。</summary>
        public uint TotalLength { get; }

        /// <summary>第 6..9 字节保留字段。</summary>
        public uint Reserved { get; }

        /// <summary>第 10 字节标志位(通常为 0x01;接收侧要求 &gt;= 1)。</summary>
        public byte Flag { get; }

        /// <summary>帧体长度(<c>TotalLength - 11</c>);帧头无效时返回 -1。</summary>
        public int BodyLength
        {
            get
            {
                if (TotalLength < ChannelServiceConstants.HeaderLength || TotalLength > int.MaxValue)
                {
                    return -1;
                }
                return (int)(TotalLength - ChannelServiceConstants.HeaderLength);
            }
        }
    }

    /// <summary>Channel Service 解码后的一帧(帧头 + 原始载荷字节)。</summary>
    public sealed class ChannelServiceFrame
    {
        /// <summary>构造已解帧对象。</summary>
        public ChannelServiceFrame(ChannelServiceFrameHeader header, byte[] body)
        {
            Header = header;
            Body = body ?? Array.Empty<byte>();
        }

        /// <summary>11 字节帧头。</summary>
        public ChannelServiceFrameHeader Header { get; }

        /// <summary>帧体字节(不含 11 字节帧头)。</summary>
        public byte[] Body { get; }
    }

    /// <summary>Channel Service 11 字节小端帧编解码与异步流读写器。</summary>
    public static class ChannelServiceFrameCodec
    {
        /// <summary>编码单帧(11 字节小端帧头 + 可选载荷)。</summary>
        public static byte[] Build(
            byte command,
            byte[] body = null,
            byte byte0 = ChannelServiceConstants.ProtocolByte0,
            uint reserved = 0,
            byte flag = ChannelServiceConstants.DefaultFlag)
        {
            int bodyLen = body == null ? 0 : body.Length;
            uint totalLen = checked((uint)(ChannelServiceConstants.HeaderLength + bodyLen));
            byte[] frame = new byte[totalLen];
            frame[0] = byte0;
            frame[1] = command;
            WriteUInt32Le(frame, 2, totalLen);
            WriteUInt32Le(frame, 6, reserved);
            frame[10] = flag;
            if (bodyLen > 0)
            {
                Buffer.BlockCopy(body, 0, frame, ChannelServiceConstants.HeaderLength, bodyLen);
            }
            return frame;
        }

        /// <summary>尝试解析 11 字节小端帧头。</summary>
        public static bool TryReadHeader(byte[] buffer, int offset, int count, out ChannelServiceFrameHeader header)
        {
            header = default(ChannelServiceFrameHeader);
            if (buffer == null || offset < 0 || count < ChannelServiceConstants.HeaderLength || offset + count > buffer.Length)
            {
                return false;
            }
            byte byte0 = buffer[offset];
            byte cmd = buffer[offset + 1];
            uint totalLen = ReadUInt32Le(buffer, offset + 2);
            uint reserved = ReadUInt32Le(buffer, offset + 6);
            byte flag = buffer[offset + 10];
            header = new ChannelServiceFrameHeader(byte0, cmd, totalLen, reserved, flag);
            return totalLen >= ChannelServiceConstants.HeaderLength;
        }

        /// <summary>从流中精确读取一帧;先核对体长上限再分配内存。到达 EOF 返回 null。</summary>
        public static async Task<ChannelServiceFrame> ReadFrameAsync(
            Stream stream,
            int maxBodyLength = ChannelServiceConstants.MaxPayloadBytes,
            CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (maxBodyLength < 0) throw new ArgumentOutOfRangeException(nameof(maxBodyLength));

            byte[] hdrBuf = new byte[ChannelServiceConstants.HeaderLength];
            int firstRead = await stream.ReadAsync(hdrBuf, 0, hdrBuf.Length, cancellationToken).ConfigureAwait(false);
            if (firstRead == 0)
            {
                return null;
            }
            if (firstRead < hdrBuf.Length)
            {
                if (!await ReadExactRemainingAsync(stream, hdrBuf, firstRead, hdrBuf.Length - firstRead, cancellationToken).ConfigureAwait(false))
                {
                    throw new EndOfStreamException("ChannelService 帧头截断");
                }
            }

            if (!TryReadHeader(hdrBuf, 0, hdrBuf.Length, out ChannelServiceFrameHeader header))
            {
                throw new InvalidDataException("ChannelService 帧头 totalLength 小于 11");
            }
            int bodyLen = header.BodyLength;
            if (bodyLen < 0 || bodyLen > maxBodyLength)
            {
                throw new InvalidDataException("ChannelService 帧体长度越界: " + bodyLen);
            }

            byte[] body = new byte[bodyLen];
            if (bodyLen > 0)
            {
                if (!await ReadExactRemainingAsync(stream, body, 0, bodyLen, cancellationToken).ConfigureAwait(false))
                {
                    throw new EndOfStreamException("ChannelService 帧体截断");
                }
            }
            return new ChannelServiceFrame(header, body);
        }

        private static async Task<bool> ReadExactRemainingAsync(
            Stream stream,
            byte[] buffer,
            int offset,
            int remaining,
            CancellationToken cancellationToken)
        {
            while (remaining > 0)
            {
                int n = await stream.ReadAsync(buffer, offset, remaining, cancellationToken).ConfigureAwait(false);
                if (n <= 0)
                {
                    return false;
                }
                offset += n;
                remaining -= n;
            }
            return true;
        }

        internal static uint ReadUInt32Le(byte[] buffer, int offset)
        {
            return (uint)buffer[offset]
                | ((uint)buffer[offset + 1] << 8)
                | ((uint)buffer[offset + 2] << 16)
                | ((uint)buffer[offset + 3] << 24);
        }

        internal static void WriteUInt32Le(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }
    }
}
