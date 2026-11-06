using System;
using System.Collections.Generic;

namespace Dnf.Net.Frame
{
    /// <summary>流重组失败的原因。出错后的 <see cref="FrameAssembler"/> 不再接受数据(服务端遇到同样情况会关连接)。</summary>
    public enum FrameError
    {
        /// <summary>没有错误。</summary>
        None = 0,

        /// <summary>头里的整帧长度小于布局下限(服务端接收是 13)。</summary>
        LengthBelowMinimum = 1,

        /// <summary>头里的整帧长度大于布局上限(服务端接收是 0x578)。</summary>
        LengthAboveMaximum = 2,
    }

    /// <summary>流里切出的一个完整帧:已解析的头与整帧字节(含头)。</summary>
    public readonly struct RawFrame
    {
        /// <summary>帧头。</summary>
        public PacketHeader Header { get; }

        /// <summary>整帧字节(含头),长度等于 <see cref="PacketHeader.TotalLength"/>;是调用方自己的副本。</summary>
        public byte[] Bytes { get; }

        /// <summary>构造。</summary>
        public RawFrame(PacketHeader header, byte[] bytes)
        {
            Header = header;
            Bytes = bytes;
        }
    }

    /// <summary>
    /// TCP 流重组:把任意切分的字节流切成完整帧(半包、粘包、一次一个字节、一次多个包结果相同)。
    /// 规则照服务端收包函数 <c>CUser::Parsing</c>(0x08647890,E-202):
    /// 累计字节不足布局下限就继续等;够了先读头,整帧长度越界就是错误;已收字节不足整帧长度就等更多;
    /// 一个帧收齐后交出,剩余字节不足下限时停止解析(留到下一次)。
    /// 为什么不限制缓冲:服务端每条连接的接收缓冲只有 0x1000(<see cref="FrameLayout.ServerReceiveBufferSize"/>),
    /// 那是它"一次读多少"的上限,帧是否完整只取决于头里的长度;这里在读头时就拒绝超长帧,所以待处理字节永远小于一个最大帧加一次喂入。
    /// 校验、解密、解压都不在这里:它只管切帧。
    /// </summary>
    public sealed class FrameAssembler
    {
        private readonly FrameLayout _layout;
        private byte[] _buffer = new byte[256];
        private int _count;

        /// <summary>创建某个方向的重组器。</summary>
        public FrameAssembler(FrameLayout layout)
        {
            _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        }

        /// <summary>使用的布局。</summary>
        public FrameLayout Layout => _layout;

        /// <summary>第一个错误(粘住,直到 <see cref="Reset"/>)。</summary>
        public FrameError Error { get; private set; }

        /// <summary>出错的那个帧头里写的整帧长度(没出错时为 0)。</summary>
        public uint OffendingLength { get; private set; }

        /// <summary>已收到但还没凑成完整帧的字节数。</summary>
        public int PendingBytes => _count;

        /// <summary>清掉所有状态(新连接)。</summary>
        public void Reset()
        {
            _count = 0;
            Error = FrameError.None;
            OffendingLength = 0;
        }

        /// <summary>
        /// 喂入新收到的字节,把这次凑齐的完整帧依次追加到 <paramref name="output"/>。
        /// 返回当前错误状态:<see cref="FrameError.None"/> 表示正常(可能 0 个帧);出错时,出错之前已凑齐的帧仍会交出。
        /// 出错后再喂入的数据被丢弃,并继续返回同一个错误。
        /// </summary>
        public FrameError Feed(byte[] data, int offset, int count, List<RawFrame> output)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            return Feed(new ReadOnlySpan<byte>(data, offset, count), output);
        }

        /// <summary>同上,参数是只读片段。</summary>
        public FrameError Feed(ReadOnlySpan<byte> data, List<RawFrame> output)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }
            if (Error != FrameError.None)
            {
                return Error;
            }
            Append(data);
            int pos = 0;
            // 服务端:剩余不足下限就不解析;下限不小于头长,所以够了一定读得出头
            while (_count - pos >= _layout.MinFrameLength)
            {
                PacketHeader.TryRead(_layout, new ReadOnlySpan<byte>(_buffer, pos, _count - pos), out PacketHeader header);
                uint length = header.TotalLength;
                if (length < (uint)_layout.MinFrameLength)
                {
                    Fault(FrameError.LengthBelowMinimum, length);
                    break;
                }
                if (length > (uint)_layout.MaxFrameLength)
                {
                    Fault(FrameError.LengthAboveMaximum, length);
                    break;
                }
                if (_count - pos < (int)length)
                {
                    break;
                }
                byte[] frame = new byte[(int)length];
                Buffer.BlockCopy(_buffer, pos, frame, 0, (int)length);
                output.Add(new RawFrame(header, frame));
                pos += (int)length;
            }
            if (Error != FrameError.None)
            {
                // 出错帧之前的字节已交出;其余不再保留(连接已不可用)
                _count = 0;
            }
            else if (pos > 0)
            {
                Buffer.BlockCopy(_buffer, pos, _buffer, 0, _count - pos);
                _count -= pos;
            }
            return Error;
        }

        private void Fault(FrameError error, uint length)
        {
            Error = error;
            OffendingLength = length;
        }

        private void Append(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
            {
                return;
            }
            int needed = _count + data.Length;
            if (needed > _buffer.Length)
            {
                int size = _buffer.Length;
                while (size < needed)
                {
                    size *= 2;
                }
                Array.Resize(ref _buffer, size);
            }
            data.CopyTo(new Span<byte>(_buffer, _count, data.Length));
            _count = needed;
        }
    }
}
