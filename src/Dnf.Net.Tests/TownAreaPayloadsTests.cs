using System;
using System.Collections.Generic;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

public sealed class TownAreaPayloadsTests
{
    [Fact]
    public void BuildSetUserArea_Writes11BytesInClientAndServerReadOrder()
    {
        byte[] payload = TownAreaPayloads.BuildSetUserArea(
            targetVillage: 91,
            targetArea: 92,
            x: 901,
            y: 902,
            srcVillage: 903,
            srcArea: 904);

        Assert.Equal(TownAreaPayloads.SetUserAreaPayloadLength, payload.Length);
        byte[] expected = new NoticePayloadBuilder()
            .U8(91)
            .U8(92)
            .U16(901)
            .U16(902)
            .U8(TownAreaPayloads.DefaultSetAreaFlags)
            .U16(903)
            .U16(904)
            .ToArray();
        Assert.Equal(expected, payload);
    }

    [Fact]
    public void BuildSetUserArea_AllowsExplicitCustomFlagsAndDefaultZeroSources()
    {
        byte[] payload = TownAreaPayloads.BuildSetUserArea(95, 96, 910, 920, flags: 97);

        byte[] expected = new NoticePayloadBuilder()
            .U8(95)
            .U8(96)
            .U16(910)
            .U16(920)
            .U8(97)
            .U16(0)
            .U16(0)
            .ToArray();
        Assert.Equal(expected, payload);
    }

    [Fact]
    public void TryParseSetUserAreaError_ParsesStandard3ByteErrorAndSignedArg()
    {
        byte[] payload = new NoticePayloadBuilder()
            .U8(0)
            .U8(0x13)
            .U8(0xFE)
            .Raw(0xAA, 0xBB)
            .ToArray();

        Assert.True(TownAreaPayloads.TryParseSetUserAreaError(payload, out SetUserAreaErrorNotice notice));
        Assert.NotNull(notice);
        Assert.Equal(0x13, notice.ErrorCode);
        Assert.Equal(0xFE, notice.Arg);
        Assert.Equal(-2, notice.SignedArg);
        Assert.Null(notice.ForbiddenMoveRemainingMinutes);
        Assert.Equal(2, notice.TrailingByteCount);
    }

    [Fact]
    public void TryParseSetUserAreaError_ParsesForbiddenMove7ByteError()
    {
        byte[] payload = new NoticePayloadBuilder()
            .U8(0)
            .U8(TownAreaPayloads.ForbiddenMoveErrorCode)
            .U8(93)
            .I32(905)
            .ToArray();

        Assert.True(TownAreaPayloads.TryParseSetUserAreaError(payload, out SetUserAreaErrorNotice notice));
        Assert.NotNull(notice);
        Assert.Equal(TownAreaPayloads.ForbiddenMoveErrorCode, notice.ErrorCode);
        Assert.Equal(93, notice.Arg);
        Assert.Equal(93, notice.SignedArg);
        Assert.Equal(905, notice.ForbiddenMoveRemainingMinutes);
        Assert.Equal(0, notice.TrailingByteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TryParseSetUserAreaError_RejectsTruncatedStandardError(int len)
    {
        byte[] full = new NoticePayloadBuilder().U8(0).U8(0x15).U8(91).ToArray();
        byte[] sliced = full.AsSpan(0, len).ToArray();

        Assert.False(TownAreaPayloads.TryParseSetUserAreaError(sliced, out SetUserAreaErrorNotice notice));
        Assert.Null(notice);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void TryParseSetUserAreaError_RejectsTruncatedForbiddenMoveMinutes(int len)
    {
        byte[] full = new NoticePayloadBuilder()
            .U8(0)
            .U8(TownAreaPayloads.ForbiddenMoveErrorCode)
            .U8(91)
            .I32(999)
            .ToArray();
        byte[] sliced = full.AsSpan(0, len).ToArray();

        Assert.False(TownAreaPayloads.TryParseSetUserAreaError(sliced, out SetUserAreaErrorNotice notice));
        Assert.Null(notice);
    }

    [Fact]
    public void TryParseSetUserAreaError_RejectsNullAndNonZeroStatus()
    {
        Assert.False(TownAreaPayloads.TryParseSetUserAreaError(null, out _));

        byte[] nonZeroStatus = new NoticePayloadBuilder().U8(1).U8(0x13).U8(91).ToArray();
        Assert.False(TownAreaPayloads.TryParseSetUserAreaError(nonZeroStatus, out _));
    }

    [Fact]
    public void TryParseUserArea_ReadsAllFieldsAndReportsTrailingBytes()
    {
        byte[] payload = new NoticePayloadBuilder()
            .U16(901)
            .U8(91)
            .U8(92)
            .U16(902)
            .U16(903)
            .U8(1)
            .U8(2)
            .Raw(0xCC)
            .ToArray();

        Assert.True(TownAreaPayloads.TryParseUserArea(payload, out UserAreaNotice notice));
        Assert.NotNull(notice);
        Assert.Equal((ushort)901, notice.UniqueId);
        Assert.Equal((byte)91, notice.Village);
        Assert.Equal((byte)92, notice.Area);
        Assert.Equal((ushort)902, notice.X);
        Assert.Equal((ushort)903, notice.Y);
        Assert.Equal((byte)1, notice.Direction);
        Assert.Equal((byte)2, notice.Visible);
        Assert.Equal(1, notice.TrailingByteCount);
    }

    [Fact]
    public void TryParseUserArea_RejectsNullAndEveryTruncationBefore10Bytes()
    {
        Assert.False(TownAreaPayloads.TryParseUserArea(null, out _));

        byte[] full = new NoticePayloadBuilder()
            .U16(901).U8(91).U8(92).U16(902).U16(903).U8(1).U8(1)
            .ToArray();
        for (int len = 0; len < TownAreaPayloads.UserAreaPayloadLength; len++)
        {
            Assert.False(TownAreaPayloads.TryParseUserArea(full.AsSpan(0, len).ToArray(), out UserAreaNotice notice));
            Assert.Null(notice);
        }
    }

    [Fact]
    public void TryParseAreaUsers_ReadsZeroAndMultiEntriesAsReadOnlyList()
    {
        byte[] emptyPayload = new NoticePayloadBuilder().U8(91).U8(92).U16(0).ToArray();
        Assert.True(TownAreaPayloads.TryParseAreaUsers(emptyPayload, out AreaUsersNotice emptyNotice));
        Assert.Equal((byte)91, emptyNotice.Village);
        Assert.Equal((byte)92, emptyNotice.Area);
        Assert.Empty(emptyNotice.Entries);
        Assert.Equal(0, emptyNotice.TrailingByteCount);

        byte[] twoEntries = new NoticePayloadBuilder()
            .U8(91).U8(92).U16(2)
            .U16(901).U16(902).U16(903).U8(1).U8(1)
            .U16(904).U16(905).U16(906).U8(0).U8(1)
            .Raw(0xDD, 0xEE)
            .ToArray();

        Assert.True(TownAreaPayloads.TryParseAreaUsers(twoEntries, out AreaUsersNotice notice));
        Assert.Equal((byte)91, notice.Village);
        Assert.Equal((byte)92, notice.Area);
        Assert.Equal(2, notice.Entries.Count);
        Assert.Equal((ushort)901, notice.Entries[0].UniqueId);
        Assert.Equal((ushort)902, notice.Entries[0].X);
        Assert.Equal((ushort)903, notice.Entries[0].Y);
        Assert.Equal((byte)1, notice.Entries[0].Direction);
        Assert.Equal((byte)1, notice.Entries[0].Visible);
        Assert.Equal((ushort)904, notice.Entries[1].UniqueId);
        Assert.Equal((ushort)905, notice.Entries[1].X);
        Assert.Equal((ushort)906, notice.Entries[1].Y);
        Assert.Equal((byte)0, notice.Entries[1].Direction);
        Assert.Equal((byte)1, notice.Entries[1].Visible);
        Assert.Equal(2, notice.TrailingByteCount);
        Assert.Throws<NotSupportedException>(() => ((IList<AreaUserEntry>)notice.Entries).Clear());
    }

    [Fact]
    public void TryParseAreaUsers_RejectsNullHeaderTruncationAndOverstatedCount()
    {
        Assert.False(TownAreaPayloads.TryParseAreaUsers(null, out _));

        byte[] oneEntry = new NoticePayloadBuilder()
            .U8(91).U8(92).U16(1)
            .U16(901).U16(902).U16(903).U8(1).U8(1)
            .ToArray();
        for (int len = 0; len < oneEntry.Length; len++)
        {
            Assert.False(TownAreaPayloads.TryParseAreaUsers(oneEntry.AsSpan(0, len).ToArray(), out AreaUsersNotice notice));
            Assert.Null(notice);
        }

        byte[] absurdCount = new NoticePayloadBuilder()
            .U8(91).U8(92).U16(ushort.MaxValue)
            .U16(901).U16(902).U16(903).U8(1).U8(1)
            .ToArray();
        Assert.False(TownAreaPayloads.TryParseAreaUsers(absurdCount, out AreaUsersNotice rejected));
        Assert.Null(rejected);
    }
}
