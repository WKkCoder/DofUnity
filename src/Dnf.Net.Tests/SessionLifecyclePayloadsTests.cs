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

    // ======== 验收后补测试:线上数值用字面数字钉死(期望值不引用产品常量) ========
    // 上面既有的用例拿产品常量造包、再拿同一个常量断言,常量被改一处时造包与解析一起变,测试照样通过。
    // 下面的用例里,包号与踢线原因值都是字面数字(读码结论,也是产品常量的现值)。

    /// <summary>线上数值表:每个常量一行,期望值全是字面数字;产品里任何一个被改一处,对应那一行就失败。</summary>
    [Fact]
    public void WireNumberTable_EveryLifecycleConstantEqualsItsLiteral()
    {
        Assert.Equal(7, SessionLifecyclePayloads.ReturnToSelectCharacterPacketId);   // 1/7 返回选角
        Assert.Equal(45, SessionLifecyclePayloads.GiveUpGamePacketId);               // 1/45 放弃副本
        Assert.Equal(199, SessionLifecyclePayloads.DisconnectReasonPacketId);        // 0/199 断线原因通知
        Assert.Equal(8, SessionLifecyclePayloads.HeartbeatTimeoutReason);            // 心跳超时踢线原因值
    }

    /// <summary>踢线原因值字面 8(线上小端 08 00 00 00)才是心跳超时;它的邻居与低字节也是 8 的更大值都不是。</summary>
    [Fact]
    public void TryParseDisconnectReason_LiteralReason8IsHeartbeatTimeoutAndNeighboursAreNot()
    {
        byte[] eight = new NoticePayloadBuilder().I32(8).ToArray();
        Assert.Equal(new byte[] { 8, 0, 0, 0 }, eight);
        Assert.True(SessionLifecyclePayloads.TryParseDisconnectReason(eight, out DisconnectReasonNotice timeout));
        Assert.Equal(8, timeout.Reason);
        Assert.True(timeout.IsHeartbeatTimeout);
        Assert.Equal(0, timeout.TrailingByteCount);

        foreach (int other in new[] { 0, 7, 9, 0x108, -8 })
        {
            byte[] payload = new NoticePayloadBuilder().I32(other).ToArray();
            Assert.True(SessionLifecyclePayloads.TryParseDisconnectReason(payload, out DisconnectReasonNotice notice));
            Assert.Equal(other, notice.Reason);
            Assert.False(notice.IsHeartbeatTimeout);
        }
    }
}
