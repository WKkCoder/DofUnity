using System;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

public sealed class SessionLifecyclePayloadsTests
{
    [Fact]
    public void BuildLifecycleCommands_Return0ByteEmptyPayloads()
    {
        Assert.Empty(SessionLifecyclePayloads.BuildGiveUpGame());
        Assert.Empty(SessionLifecyclePayloads.BuildReturnToSelectCharacter());
    }

    [Fact]
    public void TryParseReturnToSelectReply_Parses1ByteSuccessAnd2ByteError()
    {
        Assert.True(SessionLifecyclePayloads.TryParseReturnToSelectReply(new byte[] { 1, 0xFA }, out ReturnToSelectReplyNotice ok));
        Assert.NotNull(ok);
        Assert.True(ok.Succeeded);
        Assert.Equal((byte)0, ok.ErrorCode);
        Assert.Equal(1, ok.TrailingByteCount);

        Assert.True(SessionLifecyclePayloads.TryParseReturnToSelectReply(new byte[] { 0, 0x13, 0xFB }, out ReturnToSelectReplyNotice fail));
        Assert.NotNull(fail);
        Assert.False(fail.Succeeded);
        Assert.Equal((byte)0x13, fail.ErrorCode);
        Assert.Equal(1, fail.TrailingByteCount);
    }

    [Fact]
    public void TryParseReturnToSelectReply_RejectsNullEmptyTruncatedErrorAndUnknownStatus()
    {
        Assert.False(SessionLifecyclePayloads.TryParseReturnToSelectReply(null, out _));
        Assert.False(SessionLifecyclePayloads.TryParseReturnToSelectReply(Array.Empty<byte>(), out _));
        Assert.False(SessionLifecyclePayloads.TryParseReturnToSelectReply(new byte[] { 0 }, out _));
        Assert.False(SessionLifecyclePayloads.TryParseReturnToSelectReply(new byte[] { 2, 0 }, out _));
    }

    [Fact]
    public void TryParseDisconnectReason_ReadsLittleEndianInt32AndDetectsTimeoutReason()
    {
        byte[] heartbeatKick = new NoticePayloadBuilder().I32(SessionLifecyclePayloads.HeartbeatTimeoutReason).Raw(0xCC).ToArray();
        Assert.True(SessionLifecyclePayloads.TryParseDisconnectReason(heartbeatKick, out DisconnectReasonNotice timeoutNotice));
        Assert.Equal(SessionLifecyclePayloads.HeartbeatTimeoutReason, timeoutNotice.Reason);
        Assert.True(timeoutNotice.IsHeartbeatTimeout);
        Assert.Equal(1, timeoutNotice.TrailingByteCount);

        byte[] customNegative = new NoticePayloadBuilder().I32(-901).ToArray();
        Assert.True(SessionLifecyclePayloads.TryParseDisconnectReason(customNegative, out DisconnectReasonNotice customNotice));
        Assert.Equal(-901, customNotice.Reason);
        Assert.False(customNotice.IsHeartbeatTimeout);
        Assert.Equal(0, customNotice.TrailingByteCount);
    }

    [Fact]
    public void TryParseDisconnectReason_RejectsNullAndShortPayloads()
    {
        Assert.False(SessionLifecyclePayloads.TryParseDisconnectReason(null, out _));
        byte[] full = new NoticePayloadBuilder().I32(902).ToArray();
        for (int len = 0; len < 4; len++)
        {
            Assert.False(SessionLifecyclePayloads.TryParseDisconnectReason(full.AsSpan(0, len).ToArray(), out DisconnectReasonNotice notice));
            Assert.Null(notice);
        }
    }
}
