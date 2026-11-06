using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 登录成功包的布局解析与逐槽选角判定(纯函数,据实测 E-357/E-358/E-359)。
/// 账号号只在进程内用,测试里用合成字节,不含任何真实账号数据。
/// </summary>
public class OnlineTownEntryTests
{
    private static byte[] SuccessPayload(string name, int accountId)
    {
        byte[] payload = new byte[5 + name.Length + 8 + 4 + 7];
        payload[0] = 1;
        BitConverter.TryWriteBytes(payload.AsSpan(1, 4), name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            payload[5 + i] = (byte)name[i];
        }
        BitConverter.TryWriteBytes(payload.AsSpan(5 + name.Length + 8, 4), accountId);
        return payload;
    }

    [Fact]
    public void LoginReplyParsesAccountIdAfterNameAndFlags()
    {
        Assert.True(LoginReplyParse.TryGetAccountId(SuccessPayload("ab", 0x11223344), out int id));
        Assert.Equal(0x11223344, id);
        Assert.True(LoginReplyParse.TryGetAccountId(SuccessPayload("", 7), out int id2));
        Assert.Equal(7, id2);
        Assert.True(LoginReplyParse.TryGetAccountId(SuccessPayload(new string('n', LoginReplyParse.MaxNameLength), -3), out int id3));
        Assert.Equal(-3, id3);
    }

    [Fact]
    public void LoginReplyRejectsBadLayouts()
    {
        Assert.False(LoginReplyParse.TryGetAccountId(null, out _));
        Assert.False(LoginReplyParse.TryGetAccountId(new byte[4], out _));
        byte[] wrongMark = SuccessPayload("ab", 7);
        wrongMark[0] = 0;
        Assert.False(LoginReplyParse.TryGetAccountId(wrongMark, out _));

        byte[] tooLong = SuccessPayload("ab", 7);
        BitConverter.TryWriteBytes(tooLong.AsSpan(1, 4), LoginReplyParse.MaxNameLength + 1);
        Assert.False(LoginReplyParse.TryGetAccountId(tooLong, out _));

        byte[] negative = SuccessPayload("ab", 7);
        BitConverter.TryWriteBytes(negative.AsSpan(1, 4), -1);
        Assert.False(LoginReplyParse.TryGetAccountId(negative, out _));

        byte[] shortPayload = SuccessPayload("abcd", 7).AsSpan(0, 5 + 4 + 8).ToArray();
        Assert.False(LoginReplyParse.TryGetAccountId(shortPayload, out _));
    }

    [Fact]
    public void LoginReplyRejectsNameLengthAboveCapEvenWhenBytesFit()
    {
        // 名长超过上限时,即使后面的字节足够读出账号号,也不许猜
        int overCap = LoginReplyParse.MaxNameLength + 1;
        byte[] payload = new byte[5 + overCap + 8 + 4];
        payload[0] = 1;
        BitConverter.TryWriteBytes(payload.AsSpan(1, 4), overCap);
        BitConverter.TryWriteBytes(payload.AsSpan(5 + overCap + 8, 4), 4242);
        Assert.False(LoginReplyParse.TryGetAccountId(payload, out _));
    }

    [Fact]
    public void LoginReplyFailureCodeShape()
    {
        Assert.Equal((byte)0x17, LoginReplyParse.FailureCode(new byte[] { 0, 0x17, 1 }));
        Assert.Null(LoginReplyParse.FailureCode(new byte[] { 1, 0x17 }));
        Assert.Null(LoginReplyParse.FailureCode(new byte[] { 0 }));
        Assert.Null(LoginReplyParse.FailureCode(null));
    }

    private static SlotFrameHint Hint(PacketClass cls, int id, params byte[] payload)
    {
        return new SlotFrameHint(cls, id, payload);
    }

    [Fact]
    public void SlotJudgeEnteredByCompleteNotice()
    {
        SlotOutcome outcome = SlotJudge.Judge(new[]
        {
            Hint(PacketClass.Noti, 12, 2, 0),
            Hint(PacketClass.Noti, SlotJudge.EnterCompletePacketId),
        });
        Assert.Equal(SlotOutcomeKind.Entered, outcome.Kind);
    }

    [Fact]
    public void SlotJudgeEnteredBySuccessReply()
    {
        SlotOutcome outcome = SlotJudge.Judge(new[] { Hint(PacketClass.Cmd, SlotJudge.SelectPacketId, 1, 9, 9) });
        Assert.Equal(SlotOutcomeKind.Entered, outcome.Kind);
    }

    [Fact]
    public void SlotJudgeEnteredByLargePayloadDespiteFailureReply()
    {
        // 失败回包可能和别的大数据包同窗:大包形态优先(实测失败只有错误小包,成功必有大数据下发)
        byte[] big = new byte[SlotJudge.LargePayloadThreshold];
        SlotOutcome outcome = SlotJudge.Judge(new[]
        {
            Hint(PacketClass.Cmd, SlotJudge.SelectPacketId, 0, 4),
            new SlotFrameHint(PacketClass.Noti, 355, big),
        });
        Assert.Equal(SlotOutcomeKind.Entered, outcome.Kind);
    }

    [Fact]
    public void SlotJudgeFailedCarriesTheCode()
    {
        SlotOutcome outcome = SlotJudge.Judge(new[]
        {
            Hint(PacketClass.Noti, 183, 0, 0),
            Hint(PacketClass.Cmd, SlotJudge.SelectPacketId, 0, 0xfe),
        });
        Assert.Equal(SlotOutcomeKind.Failed, outcome.Kind);
        Assert.Equal((byte)0xfe, outcome.FailureCode);
    }

    [Fact]
    public void SlotJudgeNoVerdictCases()
    {
        Assert.Equal(SlotOutcomeKind.NoVerdict, SlotJudge.Judge(new SlotFrameHint[0]).Kind);
        Assert.Equal(SlotOutcomeKind.NoVerdict, SlotJudge.Judge((IReadOnlyList<SlotFrameHint>)null).Kind);
        // 静默失败:只有零星小通知
        Assert.Equal(SlotOutcomeKind.NoVerdict, SlotJudge.Judge(new[]
        {
            Hint(PacketClass.Noti, 183, 1, 2, 3),
            Hint(PacketClass.Noti, 272, 0),
        }).Kind);
        // 选角回包首字节既不是成功也不是失败形状
        Assert.Equal(SlotOutcomeKind.NoVerdict, SlotJudge.Judge(new[]
        {
            Hint(PacketClass.Cmd, SlotJudge.SelectPacketId, 7, 0),
        }).Kind);
        // 失败形状但缺码字节
        Assert.Equal(SlotOutcomeKind.NoVerdict, SlotJudge.Judge(new[]
        {
            Hint(PacketClass.Cmd, SlotJudge.SelectPacketId, 0),
        }).Kind);
        // 恰好差一个字节没到大包下限
        byte[] almost = new byte[SlotJudge.LargePayloadThreshold - 1];
        Assert.Equal(SlotOutcomeKind.NoVerdict, SlotJudge.Judge(new[]
        {
            new SlotFrameHint(PacketClass.Noti, 355, almost),
        }).Kind);
    }
}
