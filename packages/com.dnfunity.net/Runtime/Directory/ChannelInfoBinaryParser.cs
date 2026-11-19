using System;
using System.Collections.Generic;
using System.Text;

namespace Dnf.Net.ChannelDirectory
{
    /// <summary>二进制频道列表解析状态。</summary>
    public enum ChannelInfoParseStatus
    {
        /// <summary>解析成功且精确消费完毕。</summary>
        Ok = 0,

        /// <summary>输入为 null。</summary>
        NullInput = 1,

        /// <summary>数据中途截断。</summary>
        Truncated = 2,

        /// <summary>组数量为负或超出剩余字节最小所需空间。</summary>
        BadGroupCount = 3,

        /// <summary>某组内频道数为负或超出剩余字节最小所需空间。</summary>
        BadChannelCount = 4,

        /// <summary>末尾存在多余字节(不符合精确长度或 AES 16B 对齐余量)。</summary>
        TrailingBytes = 5,

        /// <summary>AES 块尾部填充区域出现非 0 字节。</summary>
        NonZeroPadding = 6,
    }

    /// <summary>线缆级单个 48 字节频道记录(未做组内最大值/脚本派生)。</summary>
    public sealed class ChannelWireEntry
    {
        /// <summary>构造原始频道记录。</summary>
        public ChannelWireEntry(string name, int maxCapacity, int currentLoad, string host, int port)
        {
            Name = name ?? string.Empty;
            MaxCapacity = maxCapacity;
            CurrentLoad = currentLoad;
            Host = host ?? string.Empty;
            Port = port;
        }

        /// <summary>频道名(<c>+0x00..+0x13</c>,20 字节定长截断至首个 NUL)。</summary>
        public string Name { get; }

        /// <summary>最大容量(<c>+0x14</c>,<c>i32</c>)。</summary>
        public int MaxCapacity { get; }

        /// <summary>当前负载(<c>+0x18</c>,<c>i32</c>)。</summary>
        public int CurrentLoad { get; }

        /// <summary>目标 IP/主机名(<c>+0x1c..+0x2b</c>,16 字节定长截断至首个 NUL)。</summary>
        public string Host { get; }

        /// <summary>目标端口(<c>+0x2c</c>,<c>i32</c>)。</summary>
        public int Port { get; }
    }

    /// <summary>线缆级服务器频道组。</summary>
    public sealed class ChannelWireGroup
    {
        /// <summary>构造原始频道组。</summary>
        public ChannelWireGroup(string tag, IReadOnlyList<ChannelWireEntry> channels)
        {
            Tag = tag ?? string.Empty;
            Channels = channels ?? Array.Empty<ChannelWireEntry>();
        }

        /// <summary>组标签(20 字节定长截断至首个 NUL)。</summary>
        public string Tag { get; }

        /// <summary>组内频道列表。</summary>
        public IReadOnlyList<ChannelWireEntry> Channels { get; }
    }

    /// <summary>
    /// <c>cmd=0x03</c> 解密后频道列表二进制读取器。
    /// <para>
    /// 【观】<c>DNF.exe</c> <c>0x010fa958..0x010fae1b</c>:
    /// <c>u32 groupCount</c> → 重复 <c>groupCount</c> 组(<c>byte[20] groupTag</c> + <c>u32 channelCount</c> + <c>channelCount × 48B</c>),
    /// 每条 48 字节为 <c>byte[20] name</c> + <c>i32 maxCapacity</c> + <c>i32 currentLoad</c> + <c>byte[16] host</c> + <c>i32 port</c>。
    /// </para>
    /// </summary>
    public static class ChannelInfoBinaryParser
    {
        /// <summary>解析完整明文频道列表。</summary>
        public static ChannelInfoParseStatus TryParse(
            byte[] data,
            bool allowAesZeroPadding,
            out List<ChannelWireGroup> groups)
        {
            groups = null;
            if (data == null)
            {
                return ChannelInfoParseStatus.NullInput;
            }
            if (data.Length < 4)
            {
                return ChannelInfoParseStatus.Truncated;
            }

            int pos = 0;
            int groupCount = ReadInt32Le(data, pos);
            pos += 4;

            const int minGroupBytes = ChannelServiceConstants.GroupTagLength + 4;
            if (groupCount < 0 || (long)groupCount * minGroupBytes > data.Length - pos)
            {
                return ChannelInfoParseStatus.BadGroupCount;
            }

            var parsedGroups = new List<ChannelWireGroup>(groupCount);
            for (int g = 0; g < groupCount; g++)
            {
                if (data.Length - pos < minGroupBytes)
                {
                    return ChannelInfoParseStatus.Truncated;
                }
                string groupTag = ReadFixedString(data, pos, ChannelServiceConstants.GroupTagLength);
                pos += ChannelServiceConstants.GroupTagLength;

                int channelCount = ReadInt32Le(data, pos);
                pos += 4;
                if (channelCount < 0 || (long)channelCount * ChannelServiceConstants.ChannelRecordLength > data.Length - pos)
                {
                    return ChannelInfoParseStatus.BadChannelCount;
                }

                var channels = new List<ChannelWireEntry>(channelCount);
                for (int c = 0; c < channelCount; c++)
                {
                    if (data.Length - pos < ChannelServiceConstants.ChannelRecordLength)
                    {
                        return ChannelInfoParseStatus.Truncated;
                    }
                    string name = ReadFixedString(data, pos, ChannelServiceConstants.ChannelNameLength);
                    int maxCapacity = ReadInt32Le(data, pos + 20);
                    int currentLoad = ReadInt32Le(data, pos + 24);
                    string host = ReadFixedString(data, pos + 28, ChannelServiceConstants.ChannelHostLength);
                    int port = ReadInt32Le(data, pos + 44);
                    pos += ChannelServiceConstants.ChannelRecordLength;

                    channels.Add(new ChannelWireEntry(name, maxCapacity, currentLoad, host, port));
                }
                parsedGroups.Add(new ChannelWireGroup(groupTag, channels));
            }

            int remaining = data.Length - pos;
            if (!allowAesZeroPadding)
            {
                if (remaining != 0)
                {
                    return ChannelInfoParseStatus.TrailingBytes;
                }
            }
            else
            {
                int expectedPad = (ChannelServiceConstants.AesBlockSize - (pos % ChannelServiceConstants.AesBlockSize)) % ChannelServiceConstants.AesBlockSize;
                if (remaining != expectedPad)
                {
                    return ChannelInfoParseStatus.TrailingBytes;
                }
                for (int i = pos; i < data.Length; i++)
                {
                    if (data[i] != 0)
                    {
                        return ChannelInfoParseStatus.NonZeroPadding;
                    }
                }
            }

            groups = parsedGroups;
            return ChannelInfoParseStatus.Ok;
        }

        internal static string ReadFixedString(byte[] buffer, int offset, int maxLength)
        {
            int len = 0;
            while (len < maxLength && buffer[offset + len] != 0)
            {
                len++;
            }
            return Encoding.UTF8.GetString(buffer, offset, len);
        }

        internal static int ReadInt32Le(byte[] buffer, int offset)
        {
            return (int)ChannelServiceFrameCodec.ReadUInt32Le(buffer, offset);
        }
    }
}
