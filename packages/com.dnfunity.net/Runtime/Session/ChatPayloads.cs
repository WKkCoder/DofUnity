using System;
using System.Collections.Generic;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 聊天消息频道编号（线上 <c>u8</c>，由服务端 <c>DisPatcher_SendMess::dispatch_sig</c> 0x081f3540、
    /// <c>GameWorld::send_chat_msg</c> 0x086c975e 与客户端发送/颜色映射 0x00962ab0、0x0095c260 互证）。
    /// <para>未知频道原始字节由外层的 <c>ChannelRaw</c> 原样保留，<see cref="ChatPayloads.TryMapKnownChannel"/> 返回 <see langword="null"/>，绝不猜测归类。</para>
    /// </summary>
    public enum ChatChannel : byte
    {
        /// <summary>系统/公告消息（仅服务端内部发包；客户端主动发送会被 <c>DisConnSig</c> 踢线，0x086c975e）。</summary>
        System = 0,

        /// <summary>同线私聊（C→S 走 <c>1/17</c> 并附目标角色名与服务器组；S→C 走 <c>0/65</c>）。</summary>
        Whisper = 1,

        /// <summary>队伍聊天（幽灵状态允许；S→C 走 <c>0/12</c>）。</summary>
        Party = 2,

        /// <summary>城镇/同区普通聊天（<c>Area::send_to_all</c> 0x086c2d2a 广播同区所有在线用户并回显发送者；S→C 走 <c>0/12</c>）。</summary>
        Town = 3,

        /// <summary>全服广播频道（仅服务端内部 <c>GameWorld::send_all</c> 使用；客户端主动发送会被 <c>DisConnSig</c> 踢线，0x086c975e）。</summary>
        World = 4,

        /// <summary>PvP 房间聊天（S→C 走 <c>0/12</c>）。</summary>
        PvpRoom = 5,

        /// <summary>公会聊天（幽灵状态允许；服务端经 <c>CGuildServerProxy</c> 转发，S→C 走 <c>0/65</c>）。</summary>
        Guild = 6,

        /// <summary>跨线/特殊私聊（C→S 附目标角色名与服务器组；S→C 走 <c>0/65</c>）。</summary>
        WhisperGmOrOther = 7,

        /// <summary>成员跨线聊天（幽灵状态允许；经 <c>CMonitorServerProxy</c> 转发，S→C 走 <c>0/65</c>）。</summary>
        Member = 8,

        /// <summary>PvP 队伍聊天（S→C 走 <c>0/12</c>）。</summary>
        PvpTeam = 9,

        /// <summary>禁言提示通知（仅服务端下发 <c>0/12</c> 7 字节剩余分钟数；客户端主动发送会被 <c>DisConnSig</c> 踢线，0x086c975e）。</summary>
        MuteNotice = 10,

        /// <summary>跨服小喇叭（经 <c>CMonitorServerProxy</c> 转发；S→C 走 <c>0/118</c>）。</summary>
        MegaphoneWorldSmall = 11,

        /// <summary>跨服大喇叭（经 <c>CMonitorServerProxy</c> 转发；S→C 走 <c>0/118</c>）。</summary>
        MegaphoneWorldLarge = 12,

        /// <summary>本线喇叭（本进程 <c>GameWorld::send_all</c> 直接广播；S→C 走 <c>0/118</c>）。</summary>
        MegaphoneLocalSmall = 13,

        /// <summary>GM 跨服喇叭（需 GM 权限，经 <c>CMonitorServerProxy</c> 转发；S→C 走 <c>0/118</c>）。</summary>
        MegaphoneGm = 14,

        /// <summary>本线超级喇叭（本进程 <c>GameWorld::send_all</c> 直接广播；S→C 走 <c>0/118</c>）。</summary>
        MegaphoneLocalLarge = 15,

        /// <summary>跨线密语（C→S 附目标角色名与服务器组；S→C 走 <c>0/65</c>）。</summary>
        CrossChannelWhisper = 33,

        /// <summary>城镇公告道具聊天（要求 <c>state == 3</c>，同区广播；S→C 走 <c>0/12</c>）。</summary>
        AreaNoticeItem = 34,

        /// <summary>统合跨服私聊（由客户端在跨服组下从频道 <c>7</c> 重映射，0x009632da；附目标后缀）。</summary>
        CrossGroupWhisperAlt = 36,

        /// <summary>统合跨服密语（由客户端在跨服组下从频道 <c>33</c> 重映射，0x009632ca；附目标后缀）。</summary>
        CrossGroupWhisperSecondary = 37,

        /// <summary>统合跨服定向私聊（由客户端在跨服组下从频道 <c>1</c> 重映射，0x009632de；附目标后缀）。</summary>
        CrossGroupWhisperDirect = 38,

        /// <summary>大富翁玩法频道（0x086c975e 调用 <c>BlueMarble</c> 投递；S→C 走 <c>0/12</c>）。</summary>
        BlueMarble = 40,
    }

    /// <summary>
    /// 聊天系统核心包（<c>1/17 SEND_MESSAGE</c>、<c>1/17</c> CMD 错误回包、<c>0/12 MESSAGE</c>、<c>0/65 MESSAGE_OTHER_CHANNEL</c>、<c>0/118 MEGAPHONE_MSG</c>）
    /// 的纯明文载荷构造与解析器（小端序，不含 13 字节帧头与会话加密）。
    /// <para>
    /// 本进程 vs 外部聊天服务结论：<c>DF_ChattingServerProxy.cpp</c>（0x0846c598）为空编译单元；同线聊天（含城镇频道 <c>3</c>、队伍 <c>2</c>、
    /// 本线喇叭 <c>13/15</c>、本线在线私聊 <c>1</c>）全部由本服 <c>df_game_r</c> 直接处理并投递，跨线与公会消息借用既有监控/公会/社区代理中转。
    /// </para>
    /// <para>
    /// 文本编解码约定：客户端上行先将 UTF-16LE 经 0x011a0190 转为 UTF-8 再写 <c>i32 len + 裸 UTF-8 字节</c>（无末尾 NUL）；
    /// 本层只按防御性拷贝 <c>byte[]</c> 收发，不绑定具体代码页。服务端按 <c>PacketBuf::get_str</c>（0x0858d2bc）读串，
    /// 要求 <c>0 &lt;= len &lt; bufferSize</c> 且遇 <c>0x00</c> 提前停止拷贝（遗留后续字节在流内导致后继字段错位），故构造上行包时拒绝含 <c>0x00</c> 的文本或目标名。
    /// </para>
    /// </summary>
    public static class ChatPayloads
    {
        /// <summary>发送聊天命令包号（类 1，即 <see cref="PacketClass.Cmd"/>，0x11）。</summary>
        public const ushort SendMessagePacketId = 17;

        /// <summary>常规聊天通知包号（类 0，即 <see cref="PacketClass.Noti"/>，0x0C）。</summary>
        public const int ChatMessagePacketId = 12;

        /// <summary>跨频道/私聊/公会聊天通知包号（类 0，即 <see cref="PacketClass.Noti"/>，0x41）。</summary>
        public const int OtherChannelMessagePacketId = 65;

        /// <summary>大小喇叭广播通知包号（类 0，即 <see cref="PacketClass.Noti"/>，0x76）。</summary>
        public const int MegaphoneMessagePacketId = 118;

        /// <summary>常规聊天正文服务端/客户端 <c>get_str</c> 缓冲区容量（0x100 = 256；0x081f3540、0x00733300、0x00743af0）。</summary>
        public const int TextBufferSize = 256;

        /// <summary>常规聊天正文最大字节数（严格小于 <see cref="TextBufferSize"/>）。</summary>
        public const int MaxTextBytes = TextBufferSize - 1;

        /// <summary>私聊目标角色名（<c>1/17</c> 0x081f3540）与喇叭发送者角色名（<c>0/118</c> 0x0073cfe0）缓冲区容量（0x1E = 30）。</summary>
        public const int TargetNameBufferSize = 30;

        /// <summary>私聊目标角色名与喇叭发送者角色名最大字节数（严格小于 <see cref="TargetNameBufferSize"/>）。</summary>
        public const int MaxTargetNameBytes = TargetNameBufferSize - 1;

        /// <summary><c>0/65 MESSAGE_OTHER_CHANNEL</c> 发送者角色名客户端缓冲区容量（0x1F = 31，0x00743af0）。</summary>
        public const int OtherChannelSenderBufferSize = 31;

        /// <summary><c>0/65</c> 发送者角色名最大字节数（严格小于 <see cref="OtherChannelSenderBufferSize"/>）。</summary>
        public const int MaxOtherChannelSenderBytes = OtherChannelSenderBufferSize - 1;

        /// <summary><c>0/118 MEGAPHONE_MSG</c> 客户端读取正文的缓冲区容量（0x200 = 512，0x0073cfe0；上行仍受 255 限制）。</summary>
        public const int MegaphoneClientTextBufferSize = 512;

        /// <summary><c>0/118</c> 客户端读取正文最大字节数（严格小于 <see cref="MegaphoneClientTextBufferSize"/>）。</summary>
        public const int MaxMegaphoneClientTextBytes = MegaphoneClientTextBufferSize - 1;

        /// <summary><c>0/65</c> 正常投递状态码（<c>statusFlag = 0</c>）。</summary>
        public const byte OtherChannelStatusOk = 0;

        /// <summary><c>0/65</c> 被对方拉黑拒收状态码（<c>'M'</c> = 0x4D，0x086c975e）。</summary>
        public const byte OtherChannelStatusBlocked = 0x4d;

        /// <summary><c>0/65</c> 目标为 GM 拒收状态码（<c>'Z'</c> = 0x5A，0x086c975e）。</summary>
        public const byte OtherChannelStatusGmTarget = 0x5a;

        /// <summary><c>0/65</c> 跨服统合 PvP 密语受阻状态码（0x15，0x086c975e）。</summary>
        public const byte OtherChannelStatusCrossGroupBlocked = 0x15;

        /// <summary><c>1/17</c> 错误回包首字节（<c>CUser::SendCmdErrorPacket</c> 0x0867bfae 写 0；客户端 0x0070d280 仅在为 0 时弹窗）。</summary>
        public const byte SendErrorPacketStatusPrefix = 0;

        /// <summary>
        /// 将原始频道字节映射为已读通的 <see cref="ChatChannel"/>；未读通或未知字节返回 <see langword="null"/>，不做猜测。
        /// </summary>
        public static ChatChannel? TryMapKnownChannel(byte rawChannel)
        {
            return Enum.IsDefined(typeof(ChatChannel), rawChannel) ? (ChatChannel?)rawChannel : null;
        }

        /// <summary>
        /// 判断 <c>1/17 SEND_MESSAGE</c> 上行包是否必须附带目标角色名与目标服务器组后缀
        /// （对应 <c>DisPatcher_SendMess::dispatch_sig</c> 0x081f3540 与客户端 0x00962ab0：<c>1, 7, 33, 36, 37, 38</c>）。
        /// </summary>
        public static bool RequiresTargetChannel(byte rawChannel)
        {
            return rawChannel == (byte)ChatChannel.Whisper
                || rawChannel == (byte)ChatChannel.WhisperGmOrOther
                || rawChannel == (byte)ChatChannel.CrossChannelWhisper
                || rawChannel == (byte)ChatChannel.CrossGroupWhisperAlt
                || rawChannel == (byte)ChatChannel.CrossGroupWhisperSecondary
                || rawChannel == (byte)ChatChannel.CrossGroupWhisperDirect;
        }

        /// <summary>
        /// 判断该频道号是否属于服务端专用防作弊拦截列表（<c>channel &lt; 31 &amp;&amp; ((1u &lt;&lt; channel) &amp; 0x7FFF0411u) != 0</c>，
        /// 含频道 <c>0, 4, 10, 16..30</c>；在 <c>GameWorld::send_chat_msg</c> 0x086c975e 中由玩家发送会直接触发 <c>DisConnSig</c> 踢线）。
        /// </summary>
        public static bool IsServerOnlyDisconnChannel(byte rawChannel)
        {
            return rawChannel == 0
                || rawChannel == 4
                || rawChannel == 10
                || (rawChannel >= 16 && rawChannel <= 30);
        }

        /// <summary>
        /// 构造 <c>1/17 SEND_MESSAGE</c> 上行载荷（使用已知 <see cref="ChatChannel"/> 枚举）。
        /// </summary>
        /// <param name="channel">聊天频道。</param>
        /// <param name="textBytes">UTF-8 编码正文字节（长度 1..255，不可为 null 或含 0x00）。</param>
        /// <param name="itemSpace">消耗品背包页类型（普通聊天传 0）。</param>
        /// <param name="itemSlot">消耗品背包槽位（普通聊天传 0）。</param>
        /// <param name="targetNameBytes">私聊目标角色名字节；仅目标后缀频道必填（1..29 字节且不含 0x00），其余频道必须为 null 或空。</param>
        /// <param name="targetServerGroup">私聊目标服务器组字节（普通频道不使用）。</param>
        /// <returns>小端明文载荷字节。</returns>
        public static byte[] BuildSendMessage(
            ChatChannel channel,
            byte[] textBytes,
            ushort itemSpace = 0,
            uint itemSlot = 0,
            byte[] targetNameBytes = null,
            byte targetServerGroup = 0)
        {
            return BuildSendMessage((byte)channel, textBytes, itemSpace, itemSlot, targetNameBytes, targetServerGroup);
        }

        /// <summary>
        /// 构造 <c>1/17 SEND_MESSAGE</c> 上行载荷（允许传入原始 <c>byte</c> 频道值）。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="textBytes"/> 为 null，或目标频道下 <paramref name="targetNameBytes"/> 为 null。</exception>
        /// <exception cref="ArgumentException">正文长度不在 1..255、正文或目标名含 0x00、非目标频道传入了非空目标名、或目标名长度不在 1..29。</exception>
        public static byte[] BuildSendMessage(
            byte rawChannel,
            byte[] textBytes,
            ushort itemSpace = 0,
            uint itemSlot = 0,
            byte[] targetNameBytes = null,
            byte targetServerGroup = 0)
        {
            if (textBytes == null)
            {
                throw new ArgumentNullException(nameof(textBytes));
            }
            bool needsTarget = RequiresTargetChannel(rawChannel);
            if (needsTarget && targetNameBytes == null)
            {
                throw new ArgumentNullException(nameof(targetNameBytes));
            }
            if (!TryBuildSendMessage(rawChannel, textBytes, out byte[] payload, itemSpace, itemSlot, targetNameBytes, targetServerGroup))
            {
                throw new ArgumentException("聊天上行载荷字段不合法（检查正文 1..255、目标名 1..29 及是否含 0x00）。", nameof(textBytes));
            }
            return payload;
        }

        /// <summary>
        /// 尝试构造 <c>1/17 SEND_MESSAGE</c> 上行载荷（不抛异常）。
        /// <para>写序：<c>u8 channel, u16 itemSpace, u32 itemSlot, i32 textLen, byte[textLen]</c>；当 <see cref="RequiresTargetChannel"/> 为真时追加 <c>i32 targetLen, byte[targetLen], u8 targetServerGroup</c>。</para>
        /// </summary>
        public static bool TryBuildSendMessage(
            byte rawChannel,
            byte[] textBytes,
            out byte[] payload,
            ushort itemSpace = 0,
            uint itemSlot = 0,
            byte[] targetNameBytes = null,
            byte targetServerGroup = 0)
        {
            payload = null;
            if (!IsValidOutgoingText(textBytes, MaxTextBytes))
            {
                return false;
            }

            bool needsTarget = RequiresTargetChannel(rawChannel);
            if (needsTarget)
            {
                if (!IsValidOutgoingText(targetNameBytes, MaxTargetNameBytes))
                {
                    return false;
                }
            }
            else if (targetNameBytes != null && targetNameBytes.Length > 0)
            {
                return false;
            }

            var writer = new PacketWriter(0);
            writer.PutByte(rawChannel);
            writer.PutShort(itemSpace);
            writer.PutUInt(itemSlot);
            writer.PutInt(textBytes.Length);
            writer.PutBinary(textBytes);
            if (needsTarget)
            {
                writer.PutInt(targetNameBytes.Length);
                writer.PutBinary(targetNameBytes);
                writer.PutByte(targetServerGroup);
            }

            payload = writer.ToArray();
            return true;
        }

        /// <summary>
        /// 解析 <c>1/17 SEND_MESSAGE</c> 上行明文载荷（互证于客户端 0x00962ab0 与服务端 0x081f3540）。
        /// </summary>
        /// <param name="payload">上行载荷字节（允许 null）。</param>
        /// <param name="command">成功时返回只读命令对象；失败时为 null。</param>
        /// <returns>满足布局与服务端字符串缓冲区上限时返回 true；其余返回 false，不抛异常。</returns>
        public static bool TryParseSendMessage(byte[] payload, out SendMessageCommand command)
        {
            command = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte rawChannel)
                || !reader.TryGetUShort(out ushort itemSpace)
                || !reader.TryGetUInt(out uint itemSlot)
                || !TryReadLengthPrefixedText(reader, TextBufferSize, out byte[] textBytes))
            {
                return false;
            }

            byte[] targetNameBytes = Array.Empty<byte>();
            byte targetServerGroup = 0;
            if (RequiresTargetChannel(rawChannel))
            {
                if (!TryReadLengthPrefixedText(reader, TargetNameBufferSize, out targetNameBytes)
                    || !reader.TryGetByte(out targetServerGroup))
                {
                    return false;
                }
            }

            command = new SendMessageCommand(
                rawChannel,
                itemSpace,
                itemSlot,
                textBytes,
                targetNameBytes,
                targetServerGroup,
                reader.Remaining);
            return true;
        }

        /// <summary>
        /// 解析 <c>(1, 17) SEND_MESSAGE</c> 服务端错误回复（<c>CUser::SendCmdErrorPacket</c> 0x0867bfae：<c>u8 0, u8 errorCode</c>；客户端处理 0x0070d280）。
        /// </summary>
        /// <param name="payload">回复明文载荷（允许 null）。</param>
        /// <param name="notice">成功时返回只读错误记录；失败时为 null。</param>
        /// <returns>首字节为 0 且总长至少 2 字节时返回 true；其余返回 false。</returns>
        public static bool TryParseSendMessageError(byte[] payload, out SendMessageErrorNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte status) || status != SendErrorPacketStatusPrefix)
            {
                return false;
            }
            if (!reader.TryGetByte(out byte errorCode))
            {
                return false;
            }

            notice = new SendMessageErrorNotice(errorCode, reader.Remaining);
            return true;
        }

        /// <summary>
        /// 解析 <c>0/12 (0x0C) MESSAGE</c> 服务端通知载荷（互证于 <c>GameWorld::make_packet_chat_msg</c> 0x086c954c、<c>send_chat_msg</c> 0x086c9970 与客户端 0x00733300）。
        /// <para>
        /// 常规频道布局：<c>u8 channel, u16 senderUniqueId, u8 senderServerGroup, i32 textLen, byte[textLen]</c>（允许未知频道字节并原样保留）。
        /// 禁言频道（<c>channel == 10</c>）布局按服务端实际发包格式（0x086c9970..0x086c9a5b）：7 字节 <c>u8 10, u16 senderUniqueId, i32 remainingMinutes</c>。
        /// </para>
        /// </summary>
        /// <param name="payload">通知明文载荷（允许 null）。</param>
        /// <param name="notice">成功时返回只读聊天通知；失败时为 null。</param>
        /// <returns>符合对应分支线缆布局且文本不超缓冲区上限时返回 true；其余返回 false。</returns>
        public static bool TryParseChatNotice(byte[] payload, out ChatMessageNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte rawChannel)
                || !reader.TryGetUShort(out ushort senderUniqueId))
            {
                return false;
            }

            if (rawChannel == (byte)ChatChannel.MuteNotice)
            {
                if (!reader.TryGetInt(out int remainingMinutes))
                {
                    return false;
                }
                notice = new ChatMessageNotice(
                    rawChannel,
                    senderUniqueId,
                    0,
                    Array.Empty<byte>(),
                    true,
                    remainingMinutes,
                    reader.Remaining);
                return true;
            }

            if (!reader.TryGetByte(out byte senderServerGroup)
                || !TryReadLengthPrefixedText(reader, TextBufferSize, out byte[] textBytes))
            {
                return false;
            }

            notice = new ChatMessageNotice(
                rawChannel,
                senderUniqueId,
                senderServerGroup,
                textBytes,
                false,
                0,
                reader.Remaining);
            return true;
        }

        /// <summary>
        /// 解析 <c>0/65 (0x41) MESSAGE_OTHER_CHANNEL</c> 私聊/公会/成员聊天通知（互证于客户端 0x00743af0 与服务端 0x086c975e、0x084cb1ac 等）。
        /// <para>布局：<c>u8 channel, u8 statusFlag, i32 senderLen + byte[senderLen], u8 serverGroup, i32 textLen + byte[textLen]</c>。</para>
        /// </summary>
        /// <param name="payload">通知明文载荷（允许 null）。</param>
        /// <param name="notice">成功时返回只读通知记录；失败时为 null。</param>
        /// <returns>满足布局且长度未越界时返回 true；其余返回 false。</returns>
        public static bool TryParseOtherChannelNotice(byte[] payload, out OtherChannelMessageNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte rawChannel)
                || !reader.TryGetByte(out byte statusFlag)
                || !TryReadLengthPrefixedText(reader, OtherChannelSenderBufferSize, out byte[] senderNameBytes)
                || !reader.TryGetByte(out byte serverGroup)
                || !TryReadLengthPrefixedText(reader, TextBufferSize, out byte[] textBytes))
            {
                return false;
            }

            notice = new OtherChannelMessageNotice(
                rawChannel,
                statusFlag,
                senderNameBytes,
                serverGroup,
                textBytes,
                reader.Remaining);
            return true;
        }

        /// <summary>
        /// 解析 <c>0/118 (0x76) MEGAPHONE_MSG</c> 喇叭广播通知（互证于客户端 0x0073cfe0 与服务端 0x086c975e、0x084d09aa）。
        /// <para>布局：<c>u8 channel, u8 channelNumber, u16 senderUniqueId, i32 senderLen + byte[senderLen], i32 textLen + byte[textLen]</c>。</para>
        /// </summary>
        /// <param name="payload">通知明文载荷（允许 null）。</param>
        /// <param name="notice">成功时返回只读喇叭记录；失败时为 null。</param>
        /// <returns>满足布局且长度未超客户端缓冲区上限时返回 true；其余返回 false。</returns>
        public static bool TryParseMegaphoneNotice(byte[] payload, out MegaphoneNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte rawChannel)
                || !reader.TryGetByte(out byte channelNumber)
                || !reader.TryGetUShort(out ushort senderUniqueId)
                || !TryReadLengthPrefixedText(reader, TargetNameBufferSize, out byte[] senderNameBytes)
                || !TryReadLengthPrefixedText(reader, MegaphoneClientTextBufferSize, out byte[] textBytes))
            {
                return false;
            }

            notice = new MegaphoneNotice(
                rawChannel,
                channelNumber,
                senderUniqueId,
                senderNameBytes,
                textBytes,
                reader.Remaining);
            return true;
        }

        private static bool IsValidOutgoingText(byte[] bytes, int maxLength)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > maxLength)
            {
                return false;
            }
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryReadLengthPrefixedText(PacketReader reader, int bufferSize, out byte[] bytes)
        {
            bytes = null;
            if (!reader.TryGetInt(out int length))
            {
                return false;
            }
            return reader.TryGetString(bufferSize, length, out bytes);
        }

        internal static byte[] CloneBytes(byte[] source)
        {
            if (source.Length == 0)
            {
                return Array.Empty<byte>();
            }
            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }
    }

    /// <summary><c>1/17 SEND_MESSAGE</c> 上行聊天请求只读视图。</summary>
    public sealed class SendMessageCommand
    {
        private readonly byte[] _textBytes;
        private readonly byte[] _targetNameBytes;

        internal SendMessageCommand(
            byte channelRaw,
            ushort itemSpace,
            uint itemSlot,
            byte[] textBytes,
            byte[] targetNameBytes,
            byte targetServerGroup,
            int trailingByteCount)
        {
            ChannelRaw = channelRaw;
            Channel = ChatPayloads.TryMapKnownChannel(channelRaw);
            ItemSpace = itemSpace;
            ItemSlot = itemSlot;
            _textBytes = ChatPayloads.CloneBytes(textBytes);
            TextBytes = Array.AsReadOnly(_textBytes);
            _targetNameBytes = ChatPayloads.CloneBytes(targetNameBytes);
            TargetNameBytes = Array.AsReadOnly(_targetNameBytes);
            TargetServerGroup = targetServerGroup;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>原始频道字节（含未读通频道的原始值）。</summary>
        public byte ChannelRaw { get; }

        /// <summary>已定义频道枚举；未知频道时为 <see langword="null"/>。</summary>
        public ChatChannel? Channel { get; }

        /// <summary>消耗品背包页类型（普通聊天为 0）。</summary>
        public ushort ItemSpace { get; }

        /// <summary>消耗品背包槽位（普通聊天为 0）。</summary>
        public uint ItemSlot { get; }

        /// <summary>正文字节的只读视图（UTF-8，已防御性拷贝）。</summary>
        public IReadOnlyList<byte> TextBytes { get; }

        /// <summary>目标角色名字节的只读视图（仅目标后缀频道非空，已防御性拷贝）。</summary>
        public IReadOnlyList<byte> TargetNameBytes { get; }

        /// <summary>目标服务器组字节（非目标频道恒为 0）。</summary>
        public byte TargetServerGroup { get; }

        /// <summary>读完后剩余未消费字节数。</summary>
        public int TrailingByteCount { get; }

        /// <summary>返回正文原始字节的独立副本。</summary>
        public byte[] CopyTextBytes() => ChatPayloads.CloneBytes(_textBytes);

        /// <summary>返回目标角色名原始字节的独立副本。</summary>
        public byte[] CopyTargetNameBytes() => ChatPayloads.CloneBytes(_targetNameBytes);
    }

    /// <summary><c>(1, 17) SEND_MESSAGE</c> 服务端错误回包只读记录（0x0867bfae / 0x0070d280）。</summary>
    public sealed class SendMessageErrorNotice
    {
        internal SendMessageErrorNotice(byte errorCode, int trailingByteCount)
        {
            ErrorCode = errorCode;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>错误码（例如道具锁为 0xD5，其余消耗/条件失败为 0x11）。</summary>
        public byte ErrorCode { get; }

        /// <summary>读完后剩余未消费字节数。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary><c>0/12 (0x0C) MESSAGE</c> 聊天或禁言通知只读记录。</summary>
    public sealed class ChatMessageNotice
    {
        private readonly byte[] _textBytes;

        internal ChatMessageNotice(
            byte channelRaw,
            ushort senderUniqueId,
            byte senderServerGroup,
            byte[] textBytes,
            bool isMuteNotice,
            int remainingMuteMinutes,
            int trailingByteCount)
        {
            ChannelRaw = channelRaw;
            Channel = ChatPayloads.TryMapKnownChannel(channelRaw);
            SenderUniqueId = senderUniqueId;
            SenderServerGroup = senderServerGroup;
            _textBytes = ChatPayloads.CloneBytes(textBytes);
            TextBytes = Array.AsReadOnly(_textBytes);
            IsMuteNotice = isMuteNotice;
            RemainingMuteMinutes = remainingMuteMinutes;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>原始频道字节（保留未知频道值，不做猜测）。</summary>
        public byte ChannelRaw { get; }

        /// <summary>已定义频道枚举；未知频道时为 <see langword="null"/>。</summary>
        public ChatChannel? Channel { get; }

        /// <summary>发送者在当前服的唯一编号（系统消息/禁言通知为 0）。</summary>
        public ushort SenderUniqueId { get; }

        /// <summary>发送者服务器组（禁言分支为 0）。</summary>
        public byte SenderServerGroup { get; }

        /// <summary>聊天正文字节的只读视图（已防御性拷贝；禁言分支为空）。</summary>
        public IReadOnlyList<byte> TextBytes { get; }

        /// <summary>是否为禁言分钟通知分支（<c>ChannelRaw == 10</c>）。</summary>
        public bool IsMuteNotice { get; }

        /// <summary>禁言剩余分钟数（仅 <see cref="IsMuteNotice"/> 为真时有效，由毫秒除以 60000 得到）。</summary>
        public int RemainingMuteMinutes { get; }

        /// <summary>读完后剩余未消费字节数。</summary>
        public int TrailingByteCount { get; }

        /// <summary>返回正文原始字节的独立副本。</summary>
        public byte[] CopyTextBytes() => ChatPayloads.CloneBytes(_textBytes);
    }

    /// <summary><c>0/65 (0x41) MESSAGE_OTHER_CHANNEL</c> 跨频道/私聊/公会聊天通知只读记录。</summary>
    public sealed class OtherChannelMessageNotice
    {
        private readonly byte[] _senderNameBytes;
        private readonly byte[] _textBytes;

        internal OtherChannelMessageNotice(
            byte channelRaw,
            byte statusFlag,
            byte[] senderNameBytes,
            byte serverGroup,
            byte[] textBytes,
            int trailingByteCount)
        {
            ChannelRaw = channelRaw;
            Channel = ChatPayloads.TryMapKnownChannel(channelRaw);
            StatusFlag = statusFlag;
            _senderNameBytes = ChatPayloads.CloneBytes(senderNameBytes);
            SenderNameBytes = Array.AsReadOnly(_senderNameBytes);
            ServerGroup = serverGroup;
            _textBytes = ChatPayloads.CloneBytes(textBytes);
            TextBytes = Array.AsReadOnly(_textBytes);
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>原始频道字节（保留未知频道值，不做猜测）。</summary>
        public byte ChannelRaw { get; }

        /// <summary>已定义频道枚举；未知频道时为 <see langword="null"/>。</summary>
        public ChatChannel? Channel { get; }

        /// <summary>状态标志（0 为正常投递，0x4D 拉黑拒收，0x5A GM 拒收，0x15 跨服阻断等）。</summary>
        public byte StatusFlag { get; }

        /// <summary>是否为正常投递（<c>StatusFlag == 0</c>）。</summary>
        public bool IsNormalDelivery => StatusFlag == ChatPayloads.OtherChannelStatusOk;

        /// <summary>发送者角色名字节的只读视图（状态非 0 回错时为目标名）。</summary>
        public IReadOnlyList<byte> SenderNameBytes { get; }

        /// <summary>跨线/目标服务器组字节（同线直投时为 0）。</summary>
        public byte ServerGroup { get; }

        /// <summary>正文字节的只读视图（已防御性拷贝）。</summary>
        public IReadOnlyList<byte> TextBytes { get; }

        /// <summary>读完后剩余未消费字节数。</summary>
        public int TrailingByteCount { get; }

        /// <summary>返回发送者角色名原始字节的独立副本。</summary>
        public byte[] CopySenderNameBytes() => ChatPayloads.CloneBytes(_senderNameBytes);

        /// <summary>返回正文原始字节的独立副本。</summary>
        public byte[] CopyTextBytes() => ChatPayloads.CloneBytes(_textBytes);
    }

    /// <summary><c>0/118 (0x76) MEGAPHONE_MSG</c> 喇叭广播通知只读记录。</summary>
    public sealed class MegaphoneNotice
    {
        private readonly byte[] _senderNameBytes;
        private readonly byte[] _textBytes;

        internal MegaphoneNotice(
            byte channelRaw,
            byte channelNumber,
            ushort senderUniqueId,
            byte[] senderNameBytes,
            byte[] textBytes,
            int trailingByteCount)
        {
            ChannelRaw = channelRaw;
            Channel = ChatPayloads.TryMapKnownChannel(channelRaw);
            ChannelNumber = channelNumber;
            SenderUniqueId = senderUniqueId;
            _senderNameBytes = ChatPayloads.CloneBytes(senderNameBytes);
            SenderNameBytes = Array.AsReadOnly(_senderNameBytes);
            _textBytes = ChatPayloads.CloneBytes(textBytes);
            TextBytes = Array.AsReadOnly(_textBytes);
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>原始频道字节（保留未知频道值，不做猜测）。</summary>
        public byte ChannelRaw { get; }

        /// <summary>已定义频道枚举；未知频道时为 <see langword="null"/>。</summary>
        public ChatChannel? Channel { get; }

        /// <summary>发送者所在频道线号（服务端 <c>CEnvironment::get_channel_no</c>）。</summary>
        public byte ChannelNumber { get; }

        /// <summary>发送者唯一编号。</summary>
        public ushort SenderUniqueId { get; }

        /// <summary>发送者角色名字节的只读视图（已防御性拷贝）。</summary>
        public IReadOnlyList<byte> SenderNameBytes { get; }

        /// <summary>喇叭正文字节的只读视图（已防御性拷贝）。</summary>
        public IReadOnlyList<byte> TextBytes { get; }

        /// <summary>读完后剩余未消费字节数。</summary>
        public int TrailingByteCount { get; }

        /// <summary>返回发送者角色名原始字节的独立副本。</summary>
        public byte[] CopySenderNameBytes() => ChatPayloads.CloneBytes(_senderNameBytes);

        /// <summary>返回喇叭正文原始字节的独立副本。</summary>
        public byte[] CopyTextBytes() => ChatPayloads.CloneBytes(_textBytes);
    }
}
