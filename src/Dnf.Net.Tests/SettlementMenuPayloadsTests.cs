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

    // ======== 验收后补测试:线上数值与命令枚举用字面数字钉死(期望值不引用产品常量) ========
    // 上面既有的用例拿产品常量造包、再拿同一个常量断言,常量被改一处时造包与解析一起变,测试照样通过。
    // 下面的用例里,包号、字段上界、标志位、命令值都是字面数字(读码结论,也是产品常量的现值)。

    /// <summary>线上数值表:每个常量一行,期望值全是字面数字;命令枚举的线上值也按字面钉死。产品里任何一个被改一处,对应那一行就失败。</summary>
    [Fact]
    public void WireNumberTable_EverySettlementConstantEqualsItsLiteral()
    {
        // 包号:1/72 评分卷轴状态、1/73 翻牌权限状态、1/74 翻牌、1/75 结算菜单命令
        Assert.Equal(72, SettlementMenuPayloads.ScoreScrollStatePacketId);
        Assert.Equal(73, SettlementMenuPayloads.CardSelectRightStatePacketId);
        Assert.Equal(74, SettlementMenuPayloads.SelectCardPacketId);
        Assert.Equal(75, SettlementMenuPayloads.EplpCommandPacketId);

        // 字段取值上界与标志位
        Assert.Equal(1, SettlementMenuPayloads.MaxSelectCardMode);
        Assert.Equal(3, SettlementMenuPayloads.MaxEplpCommand);
        Assert.Equal(1, SettlementMenuPayloads.EplpFlagExecute);
        Assert.Equal(2, SettlementMenuPayloads.EplpFlagUiSync);

        // (1, 73) 回包固定的槽位数
        Assert.Equal(4, SettlementMenuPayloads.CardSelectRightSlotCount);

        // 命令枚举的线上值
        Assert.Equal(0, (int)EplpCommand.Retry);
        Assert.Equal(1, (int)EplpCommand.SelectOtherDungeon);
        Assert.Equal(2, (int)EplpCommand.ReturnToTown);
        Assert.Equal(3, (int)EplpCommand.OpenSecretShop);
    }

    /// <summary>
    /// "已知命令"属性(<see cref="EplpReplyNotice.Command"/>)的边界:原始命令值 0 到 3 有强类型,3(最大已知值)必须有值、4(最大值加 1)必须没有。
    /// 原始值都是字面数字;成功与失败两种回包形状各验一遍。
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(255, false)]
    public void TryParseEplpReply_KnownCommandHasValueThroughRaw3AndNotFromRaw4(byte raw, bool known)
    {
        byte[] success = new NoticePayloadBuilder().U8(1).U8(1).U8(raw).ToArray();
        Assert.True(SettlementMenuPayloads.TryParseEplpReply(success, out EplpReplyNotice okNotice));
        Assert.Equal(raw, okNotice.CommandRaw);
        Assert.Equal(known, okNotice.Command.HasValue);

        byte[] failure = new NoticePayloadBuilder().U8(0).U8(8).U8(raw).ToArray();
        Assert.True(SettlementMenuPayloads.TryParseEplpReply(failure, out EplpReplyNotice errNotice));
        Assert.Equal(raw, errNotice.CommandRaw);
        Assert.Equal(known, errNotice.Command.HasValue);

        if (known)
        {
            Assert.Equal(raw, (byte)okNotice.Command.Value);
            Assert.Equal(raw, (byte)errNotice.Command.Value);
        }
    }

    /// <summary>原始命令值 0 到 3 各自对应的枚举名(字面数字对枚举名),成功与失败两种形状都一样。</summary>
    [Fact]
    public void TryParseEplpReply_CommandNamesFollowRawValues0To3()
    {
        (byte Raw, EplpCommand Name)[] table =
        {
            (0, EplpCommand.Retry),
            (1, EplpCommand.SelectOtherDungeon),
            (2, EplpCommand.ReturnToTown),
            (3, EplpCommand.OpenSecretShop),
        };

        foreach ((byte raw, EplpCommand name) in table)
        {
            byte[] success = new NoticePayloadBuilder().U8(1).U8(1).U8(raw).ToArray();
            Assert.True(SettlementMenuPayloads.TryParseEplpReply(success, out EplpReplyNotice okNotice));
            Assert.Equal(name, okNotice.Command);

            byte[] failure = new NoticePayloadBuilder().U8(0).U8(8).U8(raw).ToArray();
            Assert.True(SettlementMenuPayloads.TryParseEplpReply(failure, out EplpReplyNotice errNotice));
            Assert.Equal(name, errNotice.Command);
        }
    }

    /// <summary>
    /// 四槽位列表承诺只读:当作 IList 写下标必须抛 NotSupportedException。
    /// 注意:数组也实现 IList,但数组的 Clear/Add/Insert/Remove 会抛、写下标却会成功,所以必须用写下标来区分"只读包装"与"直接暴露数组"。
    /// </summary>
    [Fact]
    public void TryParseCardSelectRightReply_SlotStatesRejectIndexerWriteThroughIList()
    {
        byte[] payload = new NoticePayloadBuilder().U8(1).U16(1).U16(0).U16(0xFFFF).U16(901).ToArray();
        Assert.Equal(9, payload.Length);
        Assert.True(SettlementMenuPayloads.TryParseCardSelectRightReply(payload, out CardSelectRightReplyNotice notice));

        IList<short> list = (IList<short>)notice.SlotStates;
        Assert.Throws<NotSupportedException>(() => { list[0] = 7; });
        Assert.Throws<NotSupportedException>(() => { list[3] = 7; });
        Assert.Throws<NotSupportedException>(() => { list.Add(7); });
        Assert.Throws<NotSupportedException>(() => { list.Insert(0, 7); });
        Assert.Throws<NotSupportedException>(() => { list.Remove(1); });
        Assert.Throws<NotSupportedException>(() => { list.RemoveAt(0); });
        Assert.Throws<NotSupportedException>(() => { list.Clear(); });

        // 写入全都被拒之后,四个槽位原样不动
        Assert.Equal(4, notice.SlotStates.Count);
        Assert.Equal((short)1, notice.SlotStates[0]);
        Assert.Equal((short)0, notice.SlotStates[1]);
        Assert.Equal((short)-1, notice.SlotStates[2]);
        Assert.Equal((short)901, notice.SlotStates[3]);
    }
}
