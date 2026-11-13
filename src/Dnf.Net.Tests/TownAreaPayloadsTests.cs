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

    // ======== 验收后补测试:线上数值用字面数字钉死(期望值不引用产品常量) ========
    // 上面既有的用例拿产品常量造包、再拿同一个常量断言,常量被改一处时造包与解析一起变,测试照样通过。
    // 下面的用例里,包号、长度、偏移、标志字节、错误码都是字面数字(读码结论,也是产品常量的现值)。

    /// <summary>手工拼 1 条 8 字节的区内用户条目:u16 uniqueId、u16 x、u16 y、u8 direction、u8 visible;长度当场核对。</summary>
    private static byte[] HandMadeEntry(int uniqueId, int x, int y, int direction, int visible)
    {
        byte[] entry = new NoticePayloadBuilder().U16(uniqueId).U16(x).U16(y).U8(direction).U8(visible).ToArray();
        Assert.Equal(8, entry.Length);
        return entry;
    }

    /// <summary>手工拼 0/24 载荷:头 4 字节(u8 village、u8 area、u16 count)后接 n 条 8 字节条目;总长当场核对为 4 + 8n。</summary>
    private static byte[] HandMadeAreaUsers(int village, int area, params byte[][] entries)
    {
        NoticePayloadBuilder builder = new NoticePayloadBuilder().U8(village).U8(area).U16(entries.Length);
        foreach (byte[] entry in entries)
        {
            builder.Raw(entry);
        }
        byte[] payload = builder.ToArray();
        Assert.Equal(4 + 8 * entries.Length, payload.Length);
        return payload;
    }

    /// <summary>线上数值表:每个常量一行,期望值全是字面数字;产品里任何一个常量被改一处,对应那一行就失败。</summary>
    [Fact]
    public void WireNumberTable_EveryTownAreaConstantEqualsItsLiteral()
    {
        // 包号:1/38 换区命令;0/23 单人进出区通知;0/24 区内用户通知
        Assert.Equal(38, TownAreaPayloads.SetUserAreaPacketId);
        Assert.Equal(23, TownAreaPayloads.UserAreaPacketId);
        Assert.Equal(24, TownAreaPayloads.AreaUsersPacketId);

        // 长度与偏移
        Assert.Equal(11, TownAreaPayloads.SetUserAreaPayloadLength);
        Assert.Equal(10, TownAreaPayloads.UserAreaPayloadLength);
        Assert.Equal(4, TownAreaPayloads.AreaUsersHeaderLength);
        Assert.Equal(8, TownAreaPayloads.AreaUserEntryLength);

        // 取值:换区默认标志字节、禁止移动错误码
        Assert.Equal(5, TownAreaPayloads.DefaultSetAreaFlags);
        Assert.Equal(0x44, TownAreaPayloads.ForbiddenMoveErrorCode);
    }

    /// <summary>不传 flags 时,载荷第 7 个字节(下标 6)必须是字面 5;整条 11 字节再按字面逐字节核一遍。</summary>
    [Fact]
    public void BuildSetUserArea_WithoutFlags_WritesLiteral5AtIndex6()
    {
        byte[] payload = TownAreaPayloads.BuildSetUserArea(91, 92, 901, 902);

        Assert.Equal(11, payload.Length);
        Assert.Equal(5, payload[6]);
        Assert.Equal(
            new byte[]
            {
                91, 92,       // 目标城镇、目标区域
                0x85, 0x03,   // x = 901,小端
                0x86, 0x03,   // y = 902,小端
                5,            // flags 缺省值
                0, 0,         // 源城镇缺省 0
                0, 0,         // 源区域缺省 0
            },
            payload);
    }

    /// <summary>错误码恰好是字面 0x44 时才带 4 字节分钟数;这里整条用字面 0x44 手拼,不引用产品常量。</summary>
    [Fact]
    public void TryParseSetUserAreaError_LiteralCode0x44CarriesMinutes()
    {
        byte[] payload = new NoticePayloadBuilder().U8(0).U8(0x44).U8(93).I32(905).Raw(0xAA).ToArray();
        Assert.Equal(8, payload.Length);

        Assert.True(TownAreaPayloads.TryParseSetUserAreaError(payload, out SetUserAreaErrorNotice notice));
        Assert.NotNull(notice);
        Assert.Equal(0x44, notice.ErrorCode);
        Assert.Equal(93, notice.Arg);
        Assert.Equal(905, notice.ForbiddenMoveRemainingMinutes);
        Assert.Equal(1, notice.TrailingByteCount);

        // 恰好 7 字节(无尾字节)同样成功
        Assert.True(TownAreaPayloads.TryParseSetUserAreaError(payload.AsSpan(0, 7).ToArray(), out notice));
        Assert.Equal(905, notice.ForbiddenMoveRemainingMinutes);
        Assert.Equal(0, notice.TrailingByteCount);
    }

    /// <summary>字面 0x44 后面不够 4 字节分钟数(总长 3 到 6)一律失败;错误码常量被改成别的值时,3 字节那条会被当成普通错误而成功,这里就会失败。</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void TryParseSetUserAreaError_LiteralCode0x44RejectsTruncatedMinutes(int len)
    {
        byte[] full = new NoticePayloadBuilder().U8(0).U8(0x44).U8(93).I32(905).ToArray();
        Assert.Equal(7, full.Length);

        Assert.False(TownAreaPayloads.TryParseSetUserAreaError(full.AsSpan(0, len).ToArray(), out SetUserAreaErrorNotice notice));
        Assert.Null(notice);
    }

    /// <summary>0x44 的两个邻居(0x43、0x45)不走分钟分支:后面跟着的 4 字节原样留在尾部,恰好 3 字节也能解析成功。</summary>
    [Theory]
    [InlineData(0x43)]
    [InlineData(0x45)]
    public void TryParseSetUserAreaError_CodesNextTo0x44DoNotTakeTheMinutesBranch(byte errorCode)
    {
        byte[] withTail = new NoticePayloadBuilder().U8(0).U8(errorCode).U8(93).I32(905).ToArray();
        Assert.True(TownAreaPayloads.TryParseSetUserAreaError(withTail, out SetUserAreaErrorNotice notice));
        Assert.NotNull(notice);
        Assert.Equal(errorCode, notice.ErrorCode);
        Assert.Equal(93, notice.Arg);
        Assert.Null(notice.ForbiddenMoveRemainingMinutes);
        Assert.Equal(4, notice.TrailingByteCount);

        byte[] exactly3 = new NoticePayloadBuilder().U8(0).U8(errorCode).U8(93).ToArray();
        Assert.True(TownAreaPayloads.TryParseSetUserAreaError(exactly3, out notice));
        Assert.NotNull(notice);
        Assert.Null(notice.ForbiddenMoveRemainingMinutes);
        Assert.Equal(0, notice.TrailingByteCount);
    }

    /// <summary>手工拼 0、1、3 条字面 8 字节条目(头 4 字节 + n×8),条数与每条每个字段都对,恰好读满、没有尾字节。</summary>
    [Fact]
    public void TryParseAreaUsers_HandMadeZeroOneAndThreeEightByteEntries()
    {
        // 0 条:恰好 4 字节
        byte[] zero = HandMadeAreaUsers(91, 92);
        Assert.Equal(4, zero.Length);
        Assert.True(TownAreaPayloads.TryParseAreaUsers(zero, out AreaUsersNotice zeroNotice));
        Assert.Equal((byte)91, zeroNotice.Village);
        Assert.Equal((byte)92, zeroNotice.Area);
        Assert.Empty(zeroNotice.Entries);
        Assert.Equal(0, zeroNotice.TrailingByteCount);

        // 1 条:恰好 12 字节
        byte[] one = HandMadeAreaUsers(93, 94, HandMadeEntry(901, 902, 903, 1, 0));
        Assert.Equal(12, one.Length);
        Assert.True(TownAreaPayloads.TryParseAreaUsers(one, out AreaUsersNotice oneNotice));
        Assert.Equal((byte)93, oneNotice.Village);
        Assert.Equal((byte)94, oneNotice.Area);
        AreaUserEntry only = Assert.Single(oneNotice.Entries);
        Assert.Equal((ushort)901, only.UniqueId);
        Assert.Equal((ushort)902, only.X);
        Assert.Equal((ushort)903, only.Y);
        Assert.Equal((byte)1, only.Direction);
        Assert.Equal((byte)0, only.Visible);
        Assert.Equal(0, oneNotice.TrailingByteCount);

        // 3 条:恰好 28 字节,每条的各字段取互不相同的值
        byte[] three = HandMadeAreaUsers(
            95,
            96,
            HandMadeEntry(911, 912, 913, 2, 1),
            HandMadeEntry(921, 922, 923, 3, 0),
            HandMadeEntry(931, 932, 933, 0, 1));
        Assert.Equal(28, three.Length);
        Assert.True(TownAreaPayloads.TryParseAreaUsers(three, out AreaUsersNotice threeNotice));
        Assert.Equal((byte)95, threeNotice.Village);
        Assert.Equal((byte)96, threeNotice.Area);
        Assert.Equal(3, threeNotice.Entries.Count);
        Assert.Equal((ushort)911, threeNotice.Entries[0].UniqueId);
        Assert.Equal((ushort)912, threeNotice.Entries[0].X);
        Assert.Equal((ushort)913, threeNotice.Entries[0].Y);
        Assert.Equal((byte)2, threeNotice.Entries[0].Direction);
        Assert.Equal((byte)1, threeNotice.Entries[0].Visible);
        Assert.Equal((ushort)921, threeNotice.Entries[1].UniqueId);
        Assert.Equal((ushort)922, threeNotice.Entries[1].X);
        Assert.Equal((ushort)923, threeNotice.Entries[1].Y);
        Assert.Equal((byte)3, threeNotice.Entries[1].Direction);
        Assert.Equal((byte)0, threeNotice.Entries[1].Visible);
        Assert.Equal((ushort)931, threeNotice.Entries[2].UniqueId);
        Assert.Equal((ushort)932, threeNotice.Entries[2].X);
        Assert.Equal((ushort)933, threeNotice.Entries[2].Y);
        Assert.Equal((byte)0, threeNotice.Entries[2].Direction);
        Assert.Equal((byte)1, threeNotice.Entries[2].Visible);
        Assert.Equal(0, threeNotice.TrailingByteCount);
    }

    /// <summary>
    /// 恰好长度的边界(按产品现在的判据):少 1 字节失败(0 条时头不完整,n 条时最后一条缺 1 字节),
    /// 多 1 字节成功且多出的那 1 字节记为尾字节、条目不变。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void TryParseAreaUsers_OneByteShortFailsAndOneByteOverLeavesOneTrailingByte(int n)
    {
        var entries = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            entries[i] = HandMadeEntry(901 + i, 902 + i, 903 + i, i, 1);
        }
        byte[] exact = HandMadeAreaUsers(91, 92, entries);
        Assert.Equal(4 + 8 * n, exact.Length);

        Assert.True(TownAreaPayloads.TryParseAreaUsers(exact, out AreaUsersNotice exactNotice));
        Assert.Equal(n, exactNotice.Entries.Count);
        Assert.Equal(0, exactNotice.TrailingByteCount);

        byte[] shortByOne = exact.AsSpan(0, exact.Length - 1).ToArray();
        Assert.False(TownAreaPayloads.TryParseAreaUsers(shortByOne, out AreaUsersNotice shortNotice));
        Assert.Null(shortNotice);

        byte[] overByOne = new byte[exact.Length + 1];
        exact.CopyTo(overByOne, 0);
        overByOne[exact.Length] = 0xEE;
        Assert.True(TownAreaPayloads.TryParseAreaUsers(overByOne, out AreaUsersNotice overNotice));
        Assert.Equal(n, overNotice.Entries.Count);
        Assert.Equal(1, overNotice.TrailingByteCount);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal((ushort)(901 + i), overNotice.Entries[i].UniqueId);
            Assert.Equal((ushort)(902 + i), overNotice.Entries[i].X);
            Assert.Equal((ushort)(903 + i), overNotice.Entries[i].Y);
            Assert.Equal((byte)i, overNotice.Entries[i].Direction);
            Assert.Equal((byte)1, overNotice.Entries[i].Visible);
        }
    }

    /// <summary>声明条数比载荷里装得下的多一条,必须整个失败(不得返回读了一半的结果)。</summary>
    [Fact]
    public void TryParseAreaUsers_RejectsCountOneMoreThanTheBytesHold()
    {
        byte[] oneEntryOnly = new NoticePayloadBuilder()
            .U8(91).U8(92).U16(2)
            .Raw(HandMadeEntry(901, 902, 903, 1, 1))
            .ToArray();
        Assert.Equal(12, oneEntryOnly.Length);

        Assert.False(TownAreaPayloads.TryParseAreaUsers(oneEntryOnly, out AreaUsersNotice notice));
        Assert.Null(notice);
    }

    /// <summary>
    /// 区内用户列表承诺只读:当作 IList 写下标必须抛 NotSupportedException。
    /// 注意:数组也实现 IList,但数组的 Clear/Add/Insert/Remove 会抛、写下标却会成功,所以必须用写下标来区分"只读包装"与"直接暴露数组"。
    /// </summary>
    [Fact]
    public void TryParseAreaUsers_EntriesRejectIndexerWriteThroughIList()
    {
        byte[] payload = HandMadeAreaUsers(
            91,
            92,
            HandMadeEntry(901, 902, 903, 1, 1),
            HandMadeEntry(911, 912, 913, 2, 0));
        Assert.True(TownAreaPayloads.TryParseAreaUsers(payload, out AreaUsersNotice notice));
        Assert.Equal(2, notice.Entries.Count);

        IList<AreaUserEntry> list = (IList<AreaUserEntry>)notice.Entries;
        AreaUserEntry first = notice.Entries[0];
        AreaUserEntry second = notice.Entries[1];

        Assert.Throws<NotSupportedException>(() => { list[0] = second; });
        Assert.Throws<NotSupportedException>(() => { list[1] = first; });
        Assert.Throws<NotSupportedException>(() => { list.Add(first); });
        Assert.Throws<NotSupportedException>(() => { list.Insert(0, first); });
        Assert.Throws<NotSupportedException>(() => { list.Remove(first); });
        Assert.Throws<NotSupportedException>(() => { list.RemoveAt(0); });
        Assert.Throws<NotSupportedException>(() => { list.Clear(); });

        // 写入全都被拒之后,列表原样不动
        Assert.Equal(2, notice.Entries.Count);
        Assert.Same(first, notice.Entries[0]);
        Assert.Same(second, notice.Entries[1]);
    }

    /// <summary>0/23 的长度边界(字面数字):9 字节失败,恰好 10 字节成功且无尾字节,11 字节成功且尾字节 1。</summary>
    [Fact]
    public void TryParseUserArea_LiteralBoundaryAt10Bytes()
    {
        byte[] exact = new NoticePayloadBuilder()
            .U16(901).U8(91).U8(92).U16(902).U16(903).U8(1).U8(2)
            .ToArray();
        Assert.Equal(10, exact.Length);

        Assert.False(TownAreaPayloads.TryParseUserArea(exact.AsSpan(0, 9).ToArray(), out UserAreaNotice shortNotice));
        Assert.Null(shortNotice);

        Assert.True(TownAreaPayloads.TryParseUserArea(exact, out UserAreaNotice exactNotice));
        Assert.Equal(0, exactNotice.TrailingByteCount);
        Assert.Equal((byte)2, exactNotice.Visible);

        byte[] over = new NoticePayloadBuilder().Raw(exact).Raw(0xCC).ToArray();
        Assert.True(TownAreaPayloads.TryParseUserArea(over, out UserAreaNotice overNotice));
        Assert.Equal(1, overNotice.TrailingByteCount);
        Assert.Equal((ushort)901, overNotice.UniqueId);
        Assert.Equal((byte)2, overNotice.Visible);
    }
}
