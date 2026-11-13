using System;
using System.Collections.Generic;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

public sealed class SettlementMenuPayloadsTests
{
    [Fact]
    public void BuildZeroByteSettlementCommands_ReturnEmptyPayloads()
    {
        Assert.Empty(SettlementMenuPayloads.BuildScoreScrollState());
        Assert.Empty(SettlementMenuPayloads.BuildCardSelectRightState());
    }

    [Fact]
    public void TryParseScoreScrollReply_AcceptsStatus1AndRejectsInvalidInputs()
    {
        Assert.False(SettlementMenuPayloads.TryParseScoreScrollReply(null, out _));
        Assert.False(SettlementMenuPayloads.TryParseScoreScrollReply(Array.Empty<byte>(), out _));
        Assert.False(SettlementMenuPayloads.TryParseScoreScrollReply(new byte[] { 0 }, out _));
        Assert.False(SettlementMenuPayloads.TryParseScoreScrollReply(new byte[] { 2 }, out _));

        Assert.True(SettlementMenuPayloads.TryParseScoreScrollReply(new byte[] { 1, 0xAA }, out ScoreScrollReplyNotice notice));
        Assert.NotNull(notice);
        Assert.Equal((byte)1, notice.Status);
        Assert.Equal(1, notice.TrailingByteCount);
    }

    [Fact]
    public void TryParseCardSelectRightReply_ParsesNegativeZeroPositiveSlots()
    {
        // -1 = 无玩家槽位, 0 = 无翻牌权, 1 = 有翻牌权
        byte[] payload = new NoticePayloadBuilder()
            .U8(1)
            .U16(0xFFFF)
            .U16(0)
            .U16(1)
            .U16(901)
            .Raw(0xBB, 0xCC)
            .ToArray();

        Assert.True(SettlementMenuPayloads.TryParseCardSelectRightReply(payload, out CardSelectRightReplyNotice notice));
        Assert.NotNull(notice);
        Assert.Equal(4, notice.SlotStates.Count);
        Assert.Equal((short)-1, notice.SlotStates[0]);
        Assert.Equal((short)0, notice.SlotStates[1]);
        Assert.Equal((short)1, notice.SlotStates[2]);
        Assert.Equal((short)901, notice.SlotStates[3]);
        Assert.Equal(2, notice.TrailingByteCount);
        Assert.Throws<NotSupportedException>(() => ((IList<short>)notice.SlotStates).Clear());
    }

    [Fact]
    public void TryParseCardSelectRightReply_RejectsNullBadStatusAndTruncation()
    {
        Assert.False(SettlementMenuPayloads.TryParseCardSelectRightReply(null, out _));

        byte[] badStatus = new NoticePayloadBuilder()
            .U8(0).U16(1).U16(1).U16(1).U16(1).ToArray();
        Assert.False(SettlementMenuPayloads.TryParseCardSelectRightReply(badStatus, out _));

        byte[] full = new NoticePayloadBuilder()
            .U8(1).U16(1).U16(0).U16(0xFFFF).U16(0xFFFF).ToArray();
        for (int len = 0; len < full.Length; len++)
        {
            Assert.False(SettlementMenuPayloads.TryParseCardSelectRightReply(full.AsSpan(0, len).ToArray(), out _));
        }
    }

    [Theory]
    [InlineData(0, 91)]
    [InlineData(1, 92)]
    public void BuildSelectCard_WritesValid2ByteModes(byte mode, byte cardIndex)
    {
        byte[] payload = SettlementMenuPayloads.BuildSelectCard(mode, cardIndex);

        Assert.Equal(new byte[] { mode, cardIndex }, payload);
        Assert.True(SettlementMenuPayloads.TryBuildSelectCard(mode, cardIndex, out byte[] tryPayload));
        Assert.Equal(payload, tryPayload);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public void BuildSelectCard_RejectsOutOfRangeMode(byte mode)
    {
        Assert.False(SettlementMenuPayloads.TryBuildSelectCard(mode, 91, out byte[] payload));
        Assert.Null(payload);
        Assert.Throws<ArgumentOutOfRangeException>(() => SettlementMenuPayloads.BuildSelectCard(mode, 91));
    }

    [Theory]
    [InlineData(SettlementMenuPayloads.EplpFlagExecute, EplpCommand.Retry, 1, 0)]
    [InlineData(SettlementMenuPayloads.EplpFlagExecute, EplpCommand.SelectOtherDungeon, 1, 1)]
    [InlineData(SettlementMenuPayloads.EplpFlagExecute, EplpCommand.ReturnToTown, 1, 2)]
    [InlineData(SettlementMenuPayloads.EplpFlagUiSync, EplpCommand.OpenSecretShop, 2, 3)]
    public void BuildEplpCommand_WritesFlagThenCommand(byte flag, EplpCommand command, byte expectedFlag, byte expectedCmd)
    {
        byte[] payload = SettlementMenuPayloads.BuildEplpCommand(flag, command);

        Assert.Equal(new byte[] { expectedFlag, expectedCmd }, payload);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(255)]
    public void BuildEplpCommand_RejectsInvalidCommand(byte invalidCmd)
    {
        Assert.False(SettlementMenuPayloads.TryBuildEplpCommand(SettlementMenuPayloads.EplpFlagExecute, invalidCmd, out byte[] payload));
        Assert.Null(payload);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SettlementMenuPayloads.BuildEplpCommand(SettlementMenuPayloads.EplpFlagExecute, (EplpCommand)invalidCmd));
    }

    [Fact]
    public void TryParseEplpReply_DistinguishesSuccessBroadcastFromErrorFieldOrder()
    {
        byte[] success = new NoticePayloadBuilder().U8(1).U8(SettlementMenuPayloads.EplpFlagExecute).U8(2).Raw(0xEE).ToArray();
        Assert.True(SettlementMenuPayloads.TryParseEplpReply(success, out EplpReplyNotice okNotice));
        Assert.True(okNotice.Succeeded);
        Assert.Equal((byte)1, okNotice.Flag);
        Assert.Equal((byte)0, okNotice.ErrorCode);
        Assert.Equal((byte)2, okNotice.CommandRaw);
        Assert.Equal(EplpCommand.ReturnToTown, okNotice.Command);
        Assert.Equal(1, okNotice.TrailingByteCount);

        // _SendEPLPError (0x081fed4c): [u8 0, u8 errorCode, u8 command]
        byte[] err = new NoticePayloadBuilder().U8(0).U8(0x85).U8(0).ToArray();
        Assert.True(SettlementMenuPayloads.TryParseEplpReply(err, out EplpReplyNotice errNotice));
        Assert.False(errNotice.Succeeded);
        Assert.Equal((byte)0, errNotice.Flag);
        Assert.Equal((byte)0x85, errNotice.ErrorCode);
        Assert.Equal((byte)0, errNotice.CommandRaw);
        Assert.Equal(EplpCommand.Retry, errNotice.Command);
        Assert.Equal(0, errNotice.TrailingByteCount);
    }

    [Fact]
    public void TryParseEplpReply_HandlesOutOfRangeCommandEnumAndRejectsBadStatusOrShortBuffers()
    {
        byte[] unknownCmdInErr = new NoticePayloadBuilder().U8(0).U8(1).U8(99).ToArray();
        Assert.True(SettlementMenuPayloads.TryParseEplpReply(unknownCmdInErr, out EplpReplyNotice notice));
        Assert.False(notice.Succeeded);
        Assert.Equal((byte)1, notice.ErrorCode);
        Assert.Equal((byte)99, notice.CommandRaw);
        Assert.Null(notice.Command);

        Assert.False(SettlementMenuPayloads.TryParseEplpReply(null, out _));
        Assert.False(SettlementMenuPayloads.TryParseEplpReply(new byte[] { 2, 1, 0 }, out _));
        for (int len = 0; len < 3; len++)
        {
            Assert.False(SettlementMenuPayloads.TryParseEplpReply(new byte[len], out _));
        }
    }
}
