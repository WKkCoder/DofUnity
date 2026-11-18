using System;
using System.Collections.Generic;
using System.Linq;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

public sealed class ChatPayloadsTests
{
    private static byte[] MakeDistinctBytes(int count, byte start = 0x21)
    {
        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            byte val = (byte)((start + i) % 255);
            if (val == 0)
            {
                val = 1;
            }
            bytes[i] = val;
        }
        return bytes;
    }

    // ======== 线上字面数值钉死表（期望全用字面量，不引用产品常量） ========

    [Fact]
    public void WireNumberTable_EveryChatConstantAndEnumMatchesLiteralSpec()
    {
        Assert.Equal(17, ChatPayloads.SendMessagePacketId);
        Assert.Equal(12, ChatPayloads.ChatMessagePacketId);
        Assert.Equal(65, ChatPayloads.OtherChannelMessagePacketId);
        Assert.Equal(118, ChatPayloads.MegaphoneMessagePacketId);

        Assert.Equal(256, ChatPayloads.TextBufferSize);
        Assert.Equal(255, ChatPayloads.MaxTextBytes);
        Assert.Equal(30, ChatPayloads.TargetNameBufferSize);
        Assert.Equal(29, ChatPayloads.MaxTargetNameBytes);
        Assert.Equal(31, ChatPayloads.OtherChannelSenderBufferSize);
        Assert.Equal(30, ChatPayloads.MaxOtherChannelSenderBytes);
        Assert.Equal(512, ChatPayloads.MegaphoneClientTextBufferSize);
        Assert.Equal(511, ChatPayloads.MaxMegaphoneClientTextBytes);

        Assert.Equal(0, ChatPayloads.OtherChannelStatusOk);
        Assert.Equal(0x4d, ChatPayloads.OtherChannelStatusBlocked);
        Assert.Equal(0x5a, ChatPayloads.OtherChannelStatusGmTarget);
        Assert.Equal(0x15, ChatPayloads.OtherChannelStatusCrossGroupBlocked);
        Assert.Equal(0, ChatPayloads.SendErrorPacketStatusPrefix);

        Assert.Equal((byte)0, (byte)ChatChannel.System);
        Assert.Equal((byte)1, (byte)ChatChannel.Whisper);
        Assert.Equal((byte)2, (byte)ChatChannel.Party);
        Assert.Equal((byte)3, (byte)ChatChannel.Town);
        Assert.Equal((byte)4, (byte)ChatChannel.World);
        Assert.Equal((byte)5, (byte)ChatChannel.PvpRoom);
        Assert.Equal((byte)6, (byte)ChatChannel.Guild);
        Assert.Equal((byte)7, (byte)ChatChannel.WhisperGmOrOther);
        Assert.Equal((byte)8, (byte)ChatChannel.Member);
        Assert.Equal((byte)9, (byte)ChatChannel.PvpTeam);
        Assert.Equal((byte)10, (byte)ChatChannel.MuteNotice);
        Assert.Equal((byte)11, (byte)ChatChannel.MegaphoneWorldSmall);
        Assert.Equal((byte)12, (byte)ChatChannel.MegaphoneWorldLarge);
        Assert.Equal((byte)13, (byte)ChatChannel.MegaphoneLocalSmall);
        Assert.Equal((byte)14, (byte)ChatChannel.MegaphoneGm);
        Assert.Equal((byte)15, (byte)ChatChannel.MegaphoneLocalLarge);
        Assert.Equal((byte)33, (byte)ChatChannel.CrossChannelWhisper);
        Assert.Equal((byte)34, (byte)ChatChannel.AreaNoticeItem);
        Assert.Equal((byte)36, (byte)ChatChannel.CrossGroupWhisperAlt);
        Assert.Equal((byte)37, (byte)ChatChannel.CrossGroupWhisperSecondary);
        Assert.Equal((byte)38, (byte)ChatChannel.CrossGroupWhisperDirect);
        Assert.Equal((byte)40, (byte)ChatChannel.BlueMarble);
    }

    [Fact]
    public void ChannelPredicates_MapKnownPreserveUnknownAndClassifyTargetOrDisconn()
    {
        var expectedKnown = new Dictionary<byte, ChatChannel>
        {
            [0] = ChatChannel.System,
            [1] = ChatChannel.Whisper,
            [2] = ChatChannel.Party,
            [3] = ChatChannel.Town,
            [4] = ChatChannel.World,
            [5] = ChatChannel.PvpRoom,
            [6] = ChatChannel.Guild,
            [7] = ChatChannel.WhisperGmOrOther,
            [8] = ChatChannel.Member,
            [9] = ChatChannel.PvpTeam,
            [10] = ChatChannel.MuteNotice,
            [11] = ChatChannel.MegaphoneWorldSmall,
            [12] = ChatChannel.MegaphoneWorldLarge,
            [13] = ChatChannel.MegaphoneLocalSmall,
            [14] = ChatChannel.MegaphoneGm,
            [15] = ChatChannel.MegaphoneLocalLarge,
            [33] = ChatChannel.CrossChannelWhisper,
            [34] = ChatChannel.AreaNoticeItem,
            [36] = ChatChannel.CrossGroupWhisperAlt,
            [37] = ChatChannel.CrossGroupWhisperSecondary,
            [38] = ChatChannel.CrossGroupWhisperDirect,
            [40] = ChatChannel.BlueMarble,
        };

        for (int b = 0; b <= 255; b++)
        {
            byte raw = (byte)b;
            ChatChannel? mapped = ChatPayloads.TryMapKnownChannel(raw);
            if (expectedKnown.TryGetValue(raw, out ChatChannel known))
            {
                Assert.Equal(known, mapped);
            }
            else
            {
                Assert.Null(mapped);
            }

            bool expectTarget = raw == 1 || raw == 7 || raw == 33 || raw == 36 || raw == 37 || raw == 38;
            Assert.Equal(expectTarget, ChatPayloads.RequiresTargetChannel(raw));

            bool expectDisconn = raw == 0 || raw == 4 || raw == 10 || (raw >= 16 && raw <= 30);
            Assert.Equal(expectDisconn, ChatPayloads.IsServerOnlyDisconnChannel(raw));
        }
    }

    // ======== 1/17 SEND_MESSAGE 构造、默认参数、往返与边界 ========

    [Fact]
    public void BuildSendMessage_TownDefaultArgs_WritesHandComputedLittleEndianBytes()
    {
        byte[] text = { 0x68, 0x65, 0x6c, 0x6c, 0x6f }; // "hello"
        byte[] enumPayload = ChatPayloads.BuildSendMessage(ChatChannel.Town, text);
        byte[] rawPayload = ChatPayloads.BuildSendMessage((byte)3, text);
        Assert.True(ChatPayloads.TryBuildSendMessage(3, text, out byte[] tryPayload));

        byte[] expected =
        {
            0x03,                   // channel = 3 (Town)
            0x00, 0x00,             // itemSpace = 0
            0x00, 0x00, 0x00, 0x00, // itemSlot = 0
            0x05, 0x00, 0x00, 0x00, // textLen = 5
            0x68, 0x65, 0x6c, 0x6c, 0x6f,
        };
        Assert.Equal(expected, enumPayload);
        Assert.Equal(expected, rawPayload);
        Assert.Equal(expected, tryPayload);

        // 非目标频道传空数组 targetNameBytes 应允许
        byte[] withEmptyTarget = ChatPayloads.BuildSendMessage(ChatChannel.Town, text, targetNameBytes: Array.Empty<byte>());
        Assert.Equal(expected, withEmptyTarget);
    }

    [Fact]
    public void BuildSendMessage_NonTargetWithNonZeroItemAndRawUnknown_RoundTripsAndPreservesUnknown()
    {
        byte[] text = { 0x71, 0x72, 0x73, 0x74 };
        byte[] payload = ChatPayloads.BuildSendMessage(
            rawChannel: 99,
            textBytes: text,
            itemSpace: 0x0201,
            itemSlot: 0x06050403u);

        byte[] expected =
        {
            99,                     // unknown raw channel
            0x01, 0x02,             // itemSpace
            0x03, 0x04, 0x05, 0x06, // itemSlot
            0x04, 0x00, 0x00, 0x00, // textLen = 4
            0x71, 0x72, 0x73, 0x74,
        };
        Assert.Equal(expected, payload);

        Assert.True(ChatPayloads.TryParseSendMessage(payload, out SendMessageCommand parsed));
        Assert.NotNull(parsed);
        Assert.Equal((byte)99, parsed.ChannelRaw);
        Assert.Null(parsed.Channel);
        Assert.Equal((ushort)0x0201, parsed.ItemSpace);
        Assert.Equal(0x06050403u, parsed.ItemSlot);
        Assert.Equal(new byte[] { 0x71, 0x72, 0x73, 0x74 }, parsed.TextBytes);
        Assert.Empty(parsed.TargetNameBytes);
        Assert.Same(Array.Empty<byte>(), parsed.CopyTargetNameBytes());
        Assert.Equal((byte)0, parsed.TargetServerGroup);
        Assert.Equal(0, parsed.TrailingByteCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(33)]
    [InlineData(36)]
    [InlineData(37)]
    [InlineData(38)]
    public void BuildSendMessage_AllTargetChannels_WriteTargetSuffixAndRoundTrip(byte channelRaw)
    {
        byte[] text = { 0x41, 0x42, 0x43 };
        byte[] target = { 0x51, 0x52, 0x53, 0x54 };

        byte[] payloadDefaultGroup = ChatPayloads.BuildSendMessage(
            rawChannel: channelRaw,
            textBytes: text,
            targetNameBytes: target);

        byte[] expectedDefault =
        {
            channelRaw,
            0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x03, 0x00, 0x00, 0x00,
            0x41, 0x42, 0x43,
            0x04, 0x00, 0x00, 0x00,
            0x51, 0x52, 0x53, 0x54,
            0x00,
        };
        Assert.Equal(expectedDefault, payloadDefaultGroup);

        byte[] enumPayload = ChatPayloads.BuildSendMessage(
            (ChatChannel)channelRaw,
            text,
            itemSpace: 0x1112,
            itemSlot: 0x21222324u,
            targetNameBytes: target,
            targetServerGroup: 0x35);

        Assert.True(ChatPayloads.TryParseSendMessage(enumPayload, out SendMessageCommand cmd));
        Assert.Equal(channelRaw, cmd.ChannelRaw);
        Assert.Equal((ChatChannel)channelRaw, cmd.Channel);
        Assert.Equal((ushort)0x1112, cmd.ItemSpace);
        Assert.Equal(0x21222324u, cmd.ItemSlot);
        Assert.Equal(new byte[] { 0x41, 0x42, 0x43 }, cmd.TextBytes);
        Assert.Equal(new byte[] { 0x51, 0x52, 0x53, 0x54 }, cmd.TargetNameBytes);
        Assert.Equal((byte)0x35, cmd.TargetServerGroup);
        Assert.Equal(0, cmd.TrailingByteCount);
    }

    [Fact]
    public void BuildSendMessage_BoundaryLengthsAndNulGuards_EnforcedExactly()
    {
        // 正文 1 与 255 合法；0、256、null 拒绝
        byte[] text1 = { 0x41 };
        byte[] text255 = MakeDistinctBytes(255, 0x11);
        Assert.True(ChatPayloads.TryBuildSendMessage(3, text1, out byte[] p1));
        Assert.True(ChatPayloads.TryParseSendMessage(p1, out SendMessageCommand c1));
        Assert.Equal(new byte[] { 0x41 }, c1.TextBytes);

        byte[] p255 = ChatPayloads.BuildSendMessage(ChatChannel.Town, text255);
        Assert.True(ChatPayloads.TryParseSendMessage(p255, out SendMessageCommand c255));
        Assert.Equal(text255, c255.TextBytes);

        Assert.Throws<ArgumentNullException>(() => ChatPayloads.BuildSendMessage(ChatChannel.Town, null));
        Assert.Throws<ArgumentNullException>(() => ChatPayloads.BuildSendMessage((byte)3, null));
        Assert.False(ChatPayloads.TryBuildSendMessage(3, null, out _));

        Assert.Throws<ArgumentException>(() => ChatPayloads.BuildSendMessage(ChatChannel.Town, Array.Empty<byte>()));
        Assert.False(ChatPayloads.TryBuildSendMessage(3, Array.Empty<byte>(), out _));

        byte[] text256 = MakeDistinctBytes(256, 0x11);
        Assert.Throws<ArgumentException>(() => ChatPayloads.BuildSendMessage(ChatChannel.Town, text256));
        Assert.False(ChatPayloads.TryBuildSendMessage(3, text256, out _));

        // 含 NUL：首位、中间、末尾均拒绝
        foreach (byte[] badNul in new[]
        {
            new byte[] { 0x00 },
            new byte[] { 0x00, 0x41 },
            new byte[] { 0x41, 0x00, 0x42 },
            new byte[] { 0x41, 0x42, 0x00 },
        })
        {
            Assert.False(ChatPayloads.TryBuildSendMessage(3, badNul, out _));
            Assert.Throws<ArgumentException>(() => ChatPayloads.BuildSendMessage(ChatChannel.Town, badNul));
        }

        // 目标名 1 与 29 合法；0、30、null、含 NUL 拒绝；非目标频道传非空目标名拒绝
        byte[] target1 = { 0x55 };
        byte[] target29 = MakeDistinctBytes(29, 0x31);
        byte[] target30 = MakeDistinctBytes(30, 0x31);

        Assert.True(ChatPayloads.TryBuildSendMessage(1, text1, out byte[] w1, targetNameBytes: target1, targetServerGroup: 9));
        Assert.True(ChatPayloads.TryParseSendMessage(w1, out SendMessageCommand cw1));
        Assert.Equal(new byte[] { 0x55 }, cw1.TargetNameBytes);

        Assert.True(ChatPayloads.TryBuildSendMessage(1, text1, out byte[] w29, targetNameBytes: target29, targetServerGroup: 9));
        Assert.True(ChatPayloads.TryParseSendMessage(w29, out SendMessageCommand cw29));
        Assert.Equal(target29, cw29.TargetNameBytes);

        Assert.Throws<ArgumentNullException>(() => ChatPayloads.BuildSendMessage(ChatChannel.Whisper, text1, targetNameBytes: null));
        Assert.False(ChatPayloads.TryBuildSendMessage(1, text1, out _, targetNameBytes: null));
        Assert.Throws<ArgumentException>(() => ChatPayloads.BuildSendMessage(ChatChannel.Whisper, text1, targetNameBytes: Array.Empty<byte>()));
        Assert.False(ChatPayloads.TryBuildSendMessage(1, text1, out _, targetNameBytes: Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => ChatPayloads.BuildSendMessage(ChatChannel.Whisper, text1, targetNameBytes: target30));
        Assert.False(ChatPayloads.TryBuildSendMessage(1, text1, out _, targetNameBytes: target30));
        Assert.Throws<ArgumentException>(() => ChatPayloads.BuildSendMessage(ChatChannel.Whisper, text1, targetNameBytes: new byte[] { 0x41, 0x00, 0x42 }));

        Assert.Throws<ArgumentException>(() => ChatPayloads.BuildSendMessage(ChatChannel.Town, text1, targetNameBytes: target1));
        Assert.False(ChatPayloads.TryBuildSendMessage(3, text1, out _, targetNameBytes: target1));
    }

    [Fact]
    public void TryParseSendMessage_RejectsTruncationsOverrunsAndDetectsTrailingAndDefensiveCopies()
    {
        Assert.False(ChatPayloads.TryParseSendMessage(null, out SendMessageCommand nullCmd));
        Assert.Null(nullCmd);

        // 城镇逐字节截断
        byte[] townFull = new NoticePayloadBuilder()
            .U8(3).U16(901).U32(902).I32(4).Raw(0x41, 0x42, 0x43, 0x44)
            .ToArray();
        for (int len = 0; len < townFull.Length; len++)
        {
            Assert.False(ChatPayloads.TryParseSendMessage(townFull.AsSpan(0, len).ToArray(), out SendMessageCommand t));
            Assert.Null(t);
        }

        // 私聊逐字节截断（含目标名长度之后与最后的 serverGroup）
        byte[] whisperFull = new NoticePayloadBuilder()
            .U8(1).U16(901).U32(902)
            .I32(2).Raw(0x41, 0x42)
            .I32(3).Raw(0x51, 0x52, 0x53)
            .U8(88)
            .ToArray();
        for (int len = 0; len < whisperFull.Length; len++)
        {
            Assert.False(ChatPayloads.TryParseSendMessage(whisperFull.AsSpan(0, len).ToArray(), out SendMessageCommand w));
            Assert.Null(w);
        }

        // 空正文 0 字节可解析，256 与负数拒绝，目标 30 与负数拒绝
        byte[] townLen0 = new NoticePayloadBuilder().U8(3).U16(0).U32(0).I32(0).Raw(0xCC, 0xDD).ToArray();
        Assert.True(ChatPayloads.TryParseSendMessage(townLen0, out SendMessageCommand c0));
        Assert.Empty(c0.TextBytes);
        Assert.Same(Array.Empty<byte>(), c0.CopyTextBytes());
        Assert.Equal(2, c0.TrailingByteCount);

        byte[] townLen256 = new NoticePayloadBuilder().U8(3).U16(0).U32(0).I32(256).Raw(MakeDistinctBytes(256)).ToArray();
        Assert.False(ChatPayloads.TryParseSendMessage(townLen256, out _));

        byte[] townLenNeg = new NoticePayloadBuilder().U8(3).U16(0).U32(0).I32(-1).Raw(0x41).ToArray();
        Assert.False(ChatPayloads.TryParseSendMessage(townLenNeg, out _));

        byte[] whisperTarget30 = new NoticePayloadBuilder()
            .U8(1).U16(0).U32(0).I32(1).U8(0x41).I32(30).Raw(MakeDistinctBytes(30)).U8(1).ToArray();
        Assert.False(ChatPayloads.TryParseSendMessage(whisperTarget30, out _));

        // 线上含 NUL：按 PacketBuf::get_str 在 0x00 停止，余下字节留作尾随字节
        byte[] townNul = new NoticePayloadBuilder().U8(3).U16(0).U32(0).I32(4).Raw(0x41, 0x42, 0x00, 0x43).ToArray();
        Assert.True(ChatPayloads.TryParseSendMessage(townNul, out SendMessageCommand nulParsed));
        Assert.Equal(new byte[] { 0x41, 0x42 }, nulParsed.TextBytes);
        Assert.Equal(1, nulParsed.TrailingByteCount);

        // 防御性拷贝隔离输入数组与 Copy* 返回值
        byte[] mutableInput = (byte[])whisperFull.Clone();
        Assert.True(ChatPayloads.TryParseSendMessage(mutableInput, out SendMessageCommand isolated));
        mutableInput[11] = 0x7f;
        mutableInput[17] = 0x7f;
        byte[] textCopy = isolated.CopyTextBytes();
        byte[] targetCopy = isolated.CopyTargetNameBytes();
        textCopy[0] = 0x7e;
        targetCopy[0] = 0x7e;
        Assert.Equal(new byte[] { 0x41, 0x42 }, isolated.TextBytes);
        Assert.Equal(new byte[] { 0x51, 0x52, 0x53 }, isolated.TargetNameBytes);
    }

    // ======== 1/17 错误回包 ========

    [Fact]
    public void TryParseSendMessageError_ParsesValidRejectsInvalidStatusAndTruncated()
    {
        Assert.True(ChatPayloads.TryParseSendMessageError(new byte[] { 0x00, 0xd5 }, out SendMessageErrorNotice lockErr));
        Assert.Equal((byte)0xd5, lockErr.ErrorCode);
        Assert.Equal(0, lockErr.TrailingByteCount);

        Assert.True(ChatPayloads.TryParseSendMessageError(new byte[] { 0x00, 0x11, 0x99, 0x88 }, out SendMessageErrorNotice itemErr));
        Assert.Equal((byte)0x11, itemErr.ErrorCode);
        Assert.Equal(2, itemErr.TrailingByteCount);

        Assert.False(ChatPayloads.TryParseSendMessageError(null, out SendMessageErrorNotice n1));
        Assert.Null(n1);
        Assert.False(ChatPayloads.TryParseSendMessageError(Array.Empty<byte>(), out _));
        Assert.False(ChatPayloads.TryParseSendMessageError(new byte[] { 0x00 }, out _));
        Assert.False(ChatPayloads.TryParseSendMessageError(new byte[] { 0x01, 0x11 }, out _));
    }

    // ======== 0/12 MESSAGE（常规 + 未知频道保留 + 禁言 7 字节） ========

    [Fact]
    public void TryParseChatNotice_ParsesNormalKnownAndUnknownChannelsWithDefensiveCopies()
    {
        byte[] townWire = new NoticePayloadBuilder()
            .U8(3)
            .U16(0x1234)
            .U8(91)
            .I32(4)
            .Raw(0x63, 0x68, 0x61, 0x74)
            .Raw(0xee)
            .ToArray();

        Assert.True(ChatPayloads.TryParseChatNotice(townWire, out ChatMessageNotice town));
        Assert.NotNull(town);
        Assert.Equal((byte)3, town.ChannelRaw);
        Assert.Equal(ChatChannel.Town, town.Channel);
        Assert.Equal((ushort)0x1234, town.SenderUniqueId);
        Assert.Equal((byte)91, town.SenderServerGroup);
        Assert.Equal(new byte[] { 0x63, 0x68, 0x61, 0x74 }, town.TextBytes);
        Assert.False(town.IsMuteNotice);
        Assert.Equal(0, town.RemainingMuteMinutes);
        Assert.Equal(1, town.TrailingByteCount);

        // 篡改源缓冲区与 CopyTextBytes 返回值均不影响 notice
        townWire[8] = 0x01;
        byte[] copy = town.CopyTextBytes();
        copy[0] = 0x02;
        Assert.Equal(new byte[] { 0x63, 0x68, 0x61, 0x74 }, town.TextBytes);

        // 已知大富翁 40 原样保留，不猜测替换成 Town
        byte[] marbleWire = new NoticePayloadBuilder().U8(40).U16(901).U8(92).I32(1).U8(0x41).ToArray();
        Assert.True(ChatPayloads.TryParseChatNotice(marbleWire, out ChatMessageNotice marble));
        Assert.Equal((byte)40, marble.ChannelRaw);
        Assert.Equal(ChatChannel.BlueMarble, marble.Channel);

        // 未知频道 41、42、211 保留原始字节且 Channel == null
        foreach (byte unknownRaw in new byte[] { 41, 42, 211 })
        {
            byte[] unknownWire = new NoticePayloadBuilder().U8(unknownRaw).U16(902).U8(93).I32(2).Raw(0x7a, 0x7b).ToArray();
            Assert.True(ChatPayloads.TryParseChatNotice(unknownWire, out ChatMessageNotice unk));
            Assert.Equal(unknownRaw, unk.ChannelRaw);
            Assert.Null(unk.Channel);
            Assert.Equal(new byte[] { 0x7a, 0x7b }, unk.TextBytes);
        }
    }

    [Fact]
    public void TryParseChatNotice_MuteChannel10_Parses7ByteServerFormatAndRejectsTruncations()
    {
        byte[] muteWire = new NoticePayloadBuilder()
            .U8(10)
            .U16(0x0901)
            .I32(0x11223344)
            .Raw(0xfe, 0xff)
            .ToArray();

        Assert.True(ChatPayloads.TryParseChatNotice(muteWire, out ChatMessageNotice mute));
        Assert.Equal((byte)10, mute.ChannelRaw);
        Assert.Equal(ChatChannel.MuteNotice, mute.Channel);
        Assert.Equal((ushort)0x0901, mute.SenderUniqueId);
        Assert.Equal((byte)0, mute.SenderServerGroup);
        Assert.True(mute.IsMuteNotice);
        Assert.Equal(0x11223344, mute.RemainingMuteMinutes);
        Assert.Empty(mute.TextBytes);
        Assert.Same(Array.Empty<byte>(), mute.CopyTextBytes());
        Assert.Equal(2, mute.TrailingByteCount);

        for (int len = 0; len < 7; len++)
        {
            Assert.False(ChatPayloads.TryParseChatNotice(muteWire.AsSpan(0, len).ToArray(), out ChatMessageNotice m));
            Assert.Null(m);
        }
    }

    [Fact]
    public void TryParseChatNotice_NormalBoundaries_NullTruncatedLengths0To256AndNul()
    {
        Assert.False(ChatPayloads.TryParseChatNotice(null, out _));

        byte[] full = new NoticePayloadBuilder().U8(2).U16(903).U8(94).I32(3).Raw(0x61, 0x62, 0x63).ToArray();
        for (int len = 0; len < full.Length; len++)
        {
            Assert.False(ChatPayloads.TryParseChatNotice(full.AsSpan(0, len).ToArray(), out _));
        }

        // len = 0, 1, 255 合法；256 与 -1 拒绝
        byte[] len0 = new NoticePayloadBuilder().U8(3).U16(1).U8(2).I32(0).ToArray();
        Assert.True(ChatPayloads.TryParseChatNotice(len0, out ChatMessageNotice n0));
        Assert.Empty(n0.TextBytes);
        Assert.Same(Array.Empty<byte>(), n0.CopyTextBytes());

        byte[] maxBytes = MakeDistinctBytes(255, 0x15);
        byte[] len255 = new NoticePayloadBuilder().U8(3).U16(1).U8(2).I32(255).Raw(maxBytes).ToArray();
        Assert.True(ChatPayloads.TryParseChatNotice(len255, out ChatMessageNotice n255));
        Assert.Equal(maxBytes, n255.TextBytes);

        byte[] len256 = new NoticePayloadBuilder().U8(3).U16(1).U8(2).I32(256).Raw(MakeDistinctBytes(256)).ToArray();
        Assert.False(ChatPayloads.TryParseChatNotice(len256, out _));

        byte[] lenNeg = new NoticePayloadBuilder().U8(3).U16(1).U8(2).I32(-1).Raw(0x41).ToArray();
        Assert.False(ChatPayloads.TryParseChatNotice(lenNeg, out _));

        // 含 NUL：停在 0x00，后继未消费字节留在 TrailingByteCount
        byte[] withNul = new NoticePayloadBuilder().U8(3).U16(1).U8(2).I32(4).Raw(0x61, 0x62, 0x00, 0x63).ToArray();
        Assert.True(ChatPayloads.TryParseChatNotice(withNul, out ChatMessageNotice nNul));
        Assert.Equal(new byte[] { 0x61, 0x62 }, nNul.TextBytes);
        Assert.Equal(1, nNul.TrailingByteCount);
    }

    // ======== 0/65 MESSAGE_OTHER_CHANNEL ========

    [Fact]
    public void TryParseOtherChannelNotice_ParsesNormalStatusFlagsUnknownAndBoundaries()
    {
        byte[] normalWire = new NoticePayloadBuilder()
            .U8(1)
            .U8(0)
            .I32(5).Raw(0x73, 0x65, 0x6e, 0x64, 0x72)
            .U8(95)
            .I32(4).Raw(0x74, 0x65, 0x78, 0x74)
            .Raw(0xaa, 0xbb)
            .ToArray();

        Assert.True(ChatPayloads.TryParseOtherChannelNotice(normalWire, out OtherChannelMessageNotice normal));
        Assert.Equal((byte)1, normal.ChannelRaw);
        Assert.Equal(ChatChannel.Whisper, normal.Channel);
        Assert.Equal((byte)0, normal.StatusFlag);
        Assert.True(normal.IsNormalDelivery);
        Assert.Equal(new byte[] { 0x73, 0x65, 0x6e, 0x64, 0x72 }, normal.SenderNameBytes);
        Assert.Equal((byte)95, normal.ServerGroup);
        Assert.Equal(new byte[] { 0x74, 0x65, 0x78, 0x74 }, normal.TextBytes);
        Assert.Equal(2, normal.TrailingByteCount);

        // 防御拷贝
        normalWire[6] = 0x01;
        normalWire[12] = 0x02;
        byte[] senderCopy = normal.CopySenderNameBytes();
        byte[] textCopy = normal.CopyTextBytes();
        senderCopy[0] = 0x03;
        textCopy[0] = 0x04;
        Assert.Equal(new byte[] { 0x73, 0x65, 0x6e, 0x64, 0x72 }, normal.SenderNameBytes);
        Assert.Equal(new byte[] { 0x74, 0x65, 0x78, 0x74 }, normal.TextBytes);

        // 非 0 状态码与未知频道保留
        foreach (byte status in new byte[] { 0x4d, 0x5a, 0x15 })
        {
            byte[] errWire = new NoticePayloadBuilder()
                .U8(209).U8(status).I32(0).U8(7).I32(0).ToArray();
            Assert.True(ChatPayloads.TryParseOtherChannelNotice(errWire, out OtherChannelMessageNotice errNotice));
            Assert.Equal((byte)209, errNotice.ChannelRaw);
            Assert.Null(errNotice.Channel);
            Assert.Equal(status, errNotice.StatusFlag);
            Assert.False(errNotice.IsNormalDelivery);
            Assert.Empty(errNotice.SenderNameBytes);
            Assert.Same(Array.Empty<byte>(), errNotice.CopySenderNameBytes());
            Assert.Empty(errNotice.TextBytes);
        }

        // 逐字节截断 + null（基于未修改完整载荷）
        byte[] cleanOtherTruncTarget = new NoticePayloadBuilder()
            .U8(1).U8(0).I32(5).Raw(0x73, 0x65, 0x6e, 0x64, 0x72).U8(95).I32(4).Raw(0x74, 0x65, 0x78, 0x74).ToArray();
        Assert.False(ChatPayloads.TryParseOtherChannelNotice(null, out _));
        for (int len = 0; len < cleanOtherTruncTarget.Length; len++)
        {
            Assert.False(ChatPayloads.TryParseOtherChannelNotice(cleanOtherTruncTarget.AsSpan(0, len).ToArray(), out _));
        }

        // senderName 上限 30 合法、31 拒绝；text 上限 255 合法、256 拒绝
        byte[] sender30 = MakeDistinctBytes(30, 0x22);
        byte[] text255 = MakeDistinctBytes(255, 0x33);
        byte[] maxOk = new NoticePayloadBuilder()
            .U8(6).U8(0).I32(30).Raw(sender30).U8(3).I32(255).Raw(text255).ToArray();
        Assert.True(ChatPayloads.TryParseOtherChannelNotice(maxOk, out OtherChannelMessageNotice parsedMax));
        Assert.Equal(sender30, parsedMax.SenderNameBytes);
        Assert.Equal(text255, parsedMax.TextBytes);

        byte[] sender31 = new NoticePayloadBuilder()
            .U8(6).U8(0).I32(31).Raw(MakeDistinctBytes(31)).U8(3).I32(1).U8(0x41).ToArray();
        Assert.False(ChatPayloads.TryParseOtherChannelNotice(sender31, out _));

        byte[] text256 = new NoticePayloadBuilder()
            .U8(6).U8(0).I32(1).U8(0x41).U8(3).I32(256).Raw(MakeDistinctBytes(256)).ToArray();
        Assert.False(ChatPayloads.TryParseOtherChannelNotice(text256, out _));

        // 文本含 NUL
        byte[] textNul = new NoticePayloadBuilder()
            .U8(6).U8(0).I32(1).U8(0x41).U8(3).I32(3).Raw(0x71, 0x00, 0x72).ToArray();
        Assert.True(ChatPayloads.TryParseOtherChannelNotice(textNul, out OtherChannelMessageNotice nNul));
        Assert.Equal(new byte[] { 0x71 }, nNul.TextBytes);
        Assert.Equal(1, nNul.TrailingByteCount);
    }

    // ======== 0/118 MEGAPHONE_MSG ========

    [Fact]
    public void TryParseMegaphoneNotice_ParsesValidUnknownAndBoundaries511Vs512()
    {
        byte[] megaWire = new NoticePayloadBuilder()
            .U8(13)
            .U8(9)
            .U16(0x4567)
            .I32(4).Raw(0x73, 0x70, 0x6b, 0x72)
            .I32(3).Raw(0x6d, 0x73, 0x67)
            .Raw(0xc1, 0xc2, 0xc3)
            .ToArray();

        Assert.True(ChatPayloads.TryParseMegaphoneNotice(megaWire, out MegaphoneNotice mega));
        Assert.Equal((byte)13, mega.ChannelRaw);
        Assert.Equal(ChatChannel.MegaphoneLocalSmall, mega.Channel);
        Assert.Equal((byte)9, mega.ChannelNumber);
        Assert.Equal((ushort)0x4567, mega.SenderUniqueId);
        Assert.Equal(new byte[] { 0x73, 0x70, 0x6b, 0x72 }, mega.SenderNameBytes);
        Assert.Equal(new byte[] { 0x6d, 0x73, 0x67 }, mega.TextBytes);
        Assert.Equal(3, mega.TrailingByteCount);

        // 防御拷贝
        megaWire[8] = 0x01;
        megaWire[16] = 0x02;
        mega.CopySenderNameBytes()[0] = 0x03;
        mega.CopyTextBytes()[0] = 0x04;
        Assert.Equal(new byte[] { 0x73, 0x70, 0x6b, 0x72 }, mega.SenderNameBytes);
        Assert.Equal(new byte[] { 0x6d, 0x73, 0x67 }, mega.TextBytes);

        // 未知频道保留
        byte[] unkWire = new NoticePayloadBuilder()
            .U8(188).U8(2).U16(901).I32(0).I32(0).ToArray();
        Assert.True(ChatPayloads.TryParseMegaphoneNotice(unkWire, out MegaphoneNotice unk));
        Assert.Equal((byte)188, unk.ChannelRaw);
        Assert.Null(unk.Channel);
        Assert.Empty(unk.SenderNameBytes);
        Assert.Empty(unk.TextBytes);
        Assert.Same(Array.Empty<byte>(), unk.CopySenderNameBytes());
        Assert.Same(Array.Empty<byte>(), unk.CopyTextBytes());

        // 逐字节截断 + null（基于未修改完整载荷）
        byte[] cleanMegaTruncTarget = new NoticePayloadBuilder()
            .U8(13).U8(9).U16(0x4567).I32(4).Raw(0x73, 0x70, 0x6b, 0x72).I32(3).Raw(0x6d, 0x73, 0x67).ToArray();
        Assert.False(ChatPayloads.TryParseMegaphoneNotice(null, out _));
        for (int len = 0; len < cleanMegaTruncTarget.Length; len++)
        {
            Assert.False(ChatPayloads.TryParseMegaphoneNotice(cleanMegaTruncTarget.AsSpan(0, len).ToArray(), out _));
        }

        // sender 29 合法 / 30 拒绝；text 511 合法 / 512 拒绝
        byte[] sender29 = MakeDistinctBytes(29, 0x21);
        byte[] text511 = MakeDistinctBytes(511, 0x31);
        byte[] maxOk = new NoticePayloadBuilder()
            .U8(15).U8(4).U16(905).I32(29).Raw(sender29).I32(511).Raw(text511).ToArray();
        Assert.True(ChatPayloads.TryParseMegaphoneNotice(maxOk, out MegaphoneNotice maxParsed));
        Assert.Equal(sender29, maxParsed.SenderNameBytes);
        Assert.Equal(text511, maxParsed.TextBytes);

        byte[] sender30 = new NoticePayloadBuilder()
            .U8(15).U8(4).U16(905).I32(30).Raw(MakeDistinctBytes(30)).I32(1).U8(0x41).ToArray();
        Assert.False(ChatPayloads.TryParseMegaphoneNotice(sender30, out _));

        byte[] text512 = new NoticePayloadBuilder()
            .U8(15).U8(4).U16(905).I32(1).U8(0x41).I32(512).Raw(MakeDistinctBytes(512)).ToArray();
        Assert.False(ChatPayloads.TryParseMegaphoneNotice(text512, out _));

        // 含 NUL 截断并保留剩余在 TrailingByteCount
        byte[] textNul = new NoticePayloadBuilder()
            .U8(15).U8(4).U16(905).I32(1).U8(0x41).I32(4).Raw(0x71, 0x72, 0x00, 0x73).ToArray();
        Assert.True(ChatPayloads.TryParseMegaphoneNotice(textNul, out MegaphoneNotice nNul));
        Assert.Equal(new byte[] { 0x71, 0x72 }, nNul.TextBytes);
        Assert.Equal(1, nNul.TrailingByteCount);
    }
}
