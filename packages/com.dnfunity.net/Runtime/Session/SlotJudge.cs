using System;
using System.Collections.Generic;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>登录成功包(类 CMD、包号 1,首字节为成功标记)的布局解析(E-357、E-358)。</summary>
    public static class LoginReplyParse
    {
        /// <summary>账号名字段长度上限(服务端按 0x14 截断)。</summary>
        public const int MaxNameLength = 0x14;

        /// <summary>
        /// 从成功包载荷里取出账号号:首字节成功标记、其后 u32 名字长度、名字、8 个标志字节、u32 账号号。
        /// 布局不合(长度越界)返回 false,不猜。
        /// </summary>
        public static bool TryGetAccountId(byte[] payload, out int accountId)
        {
            accountId = 0;
            if (payload == null || payload.Length < 5 || payload[0] != 1)
            {
                return false;
            }
            int nameLength = BitConverter.ToInt32(payload, 1);
            if (nameLength < 0 || nameLength > MaxNameLength)
            {
                return false;
            }
            int offset = 5 + nameLength + 8;
            if (offset + 4 > payload.Length)
            {
                return false;
            }
            accountId = BitConverter.ToInt32(payload, offset);
            return true;
        }

        /// <summary>登录是否失败以及失败码:载荷 [0]=0、[1]=码;不是这个形状返回 null。</summary>
        public static byte? FailureCode(byte[] payload)
        {
            if (payload == null || payload.Length < 2 || payload[0] != 0)
            {
                return null;
            }
            return payload[1];
        }
    }

    /// <summary>逐槽选角时,单个槽的回包判定结果。</summary>
    public enum SlotOutcomeKind
    {
        /// <summary>收到了进城完成通知(空载荷的类 0、包号 124)或选角成功回包:这个槽进了城。</summary>
        Entered,

        /// <summary>服务端回了选角失败(成功字节为 0,次字节是失败码):换下一个槽。</summary>
        Failed,

        /// <summary>没有决定性回包(静默失败,或回包被封进信封看不见):按包流形态也判不出来。</summary>
        NoVerdict,
    }

    /// <summary>单个槽的判定结果(失败时带服务端的失败码)。</summary>
    public readonly struct SlotOutcome
    {
        /// <summary>判定种类。</summary>
        public SlotOutcomeKind Kind { get; }

        /// <summary>失败码(仅 <see cref="SlotOutcomeKind.Failed"/> 时有意义)。</summary>
        public byte FailureCode { get; }

        /// <summary>构造判定结果。</summary>
        public SlotOutcome(SlotOutcomeKind kind, byte failureCode)
        {
            Kind = kind;
            FailureCode = failureCode;
        }
    }

    /// <summary>一帧的判定摘要:类、包号、载荷长与前两个字节(足够做选角判定,不带完整载荷)。</summary>
    public readonly struct SlotFrameHint
    {
        /// <summary>从一帧的头部与载荷取摘要(载荷为空时两个字节记 -1)。</summary>
        public SlotFrameHint(PacketClass packetClass, int packetId, byte[] payload)
        {
            PacketClass = packetClass;
            PacketId = packetId;
            PayloadLength = payload == null ? 0 : payload.Length;
            FirstByte = payload != null && payload.Length >= 1 ? payload[0] : -1;
            SecondByte = payload != null && payload.Length >= 2 ? payload[1] : -1;
        }

        /// <summary>包类。</summary>
        public PacketClass PacketClass { get; }

        /// <summary>包号。</summary>
        public int PacketId { get; }

        /// <summary>载荷字节数。</summary>
        public int PayloadLength { get; }

        /// <summary>载荷首字节(没有为 -1)。</summary>
        public int FirstByte { get; }

        /// <summary>载荷次字节(没有为 -1)。</summary>
        public int SecondByte { get; }
    }

    /// <summary>选角窗口的判定(据实测的包流形态,E-359)。</summary>
    public static class SlotJudge
    {
        /// <summary>进城完成通知的包号(类 0,空载荷)。</summary>
        public const int EnterCompletePacketId = 124;

        /// <summary>选角命令的包号(类 CMD)。</summary>
        public const int SelectPacketId = 4;

        /// <summary>
        /// 选角成功后会下发的单个大数据包的载荷下限:实测约三万字节的进度位图远超任何失败小包,
        /// 成功/失败回包本身可能被封进 561 信封看不见,就用这个形态兜底。
        /// </summary>
        public const int LargePayloadThreshold = 3000;

        /// <summary>判定一个槽的回包窗口:先进城完成与成功回包,其次大包形态,再次失败码,最后无判定。</summary>
        public static SlotOutcome Judge(IReadOnlyList<DecodedFrame> frames)
        {
            if (frames == null)
            {
                return new SlotOutcome(SlotOutcomeKind.NoVerdict, 0);
            }
            var hints = new List<SlotFrameHint>(frames.Count);
            foreach (DecodedFrame frame in frames)
            {
                hints.Add(new SlotFrameHint(frame.Header.Class, frame.Header.PacketId, frame.Payload));
            }
            return Judge(hints);
        }

        /// <summary>按帧摘要判定(与 <see cref="Judge(IReadOnlyList{DecodedFrame})"/> 同一规则,便于不解码地测试)。</summary>
        public static SlotOutcome Judge(IReadOnlyList<SlotFrameHint> frames)
        {
            if (frames == null)
            {
                return new SlotOutcome(SlotOutcomeKind.NoVerdict, 0);
            }
            byte? failure = null;
            bool large = false;
            foreach (SlotFrameHint frame in frames)
            {
                if (frame.PacketClass == PacketClass.Noti && frame.PacketId == EnterCompletePacketId)
                {
                    return new SlotOutcome(SlotOutcomeKind.Entered, 0);
                }
                if (frame.PacketClass == PacketClass.Cmd && frame.PacketId == SelectPacketId && frame.PayloadLength >= 1)
                {
                    if (frame.FirstByte == 1)
                    {
                        return new SlotOutcome(SlotOutcomeKind.Entered, 0);
                    }
                    if (frame.FirstByte == 0 && frame.PayloadLength >= 2)
                    {
                        failure = (byte)frame.SecondByte;
                    }
                }
                if (frame.PayloadLength >= LargePayloadThreshold)
                {
                    large = true;
                }
            }
            if (large)
            {
                return new SlotOutcome(SlotOutcomeKind.Entered, 0);
            }
            if (failure.HasValue)
            {
                return new SlotOutcome(SlotOutcomeKind.Failed, failure.Value);
            }
            return new SlotOutcome(SlotOutcomeKind.NoVerdict, 0);
        }
    }
}
