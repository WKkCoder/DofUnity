using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 换房命令(类 1、包号 48,43 字节)与装载完成命令(类 1、包号 40,8 字节)的载荷拼装、换房请求对象、换房失败回包的读法。
/// 逐字段断言用独立的读法(<see cref="MoveRoomView"/>,按服务端读序的顺序游标)与手写的偏移表两路核对:产品代码把任何一个字段挪一个字节,
/// 两路都会失败。取值都是合成的(901 起),不含任何真实数值。
/// </summary>
public class OnlineMoveRoomPayloadsTests
{
    private static byte[] Le32(int value)
    {
        return new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
    }

    private static byte[] Le16(int value)
    {
        return new[] { (byte)value, (byte)(value >> 8) };
    }

    /// <summary>每个字段都取互不相同、两字节以上宽度的字段取"各字节都不同"的合成值,这样任何一处错位都会读出别的值。</summary>
    private static byte[] FullyPopulated()
    {
        return OnlineCombatPayloads.BuildMoveRoom(
            targetX: 5,
            targetY: 7,
            positionX: 901 + (0x0A << 16),
            positionY: -(902 + (0x0B << 16)),
            layered: true,
            trapBits: 7 * 131,
            seatItems: new ushort[] { 911, 912, 913, 914 },
            seatPlayTimes: new uint[] { 921, 922 + (0x0C << 16), 923, 0xF0000001u },
            unused: 931,
            tail: 941 + (0x0D << 20));
    }

    // ======== 换房载荷:逐字段 ========

    /// <summary>43 字节,每个字段在文档写明的偏移上,服务端读序的游标读下来每一项都对、正好读满 43 字节。</summary>
    [Fact]
    public void The_move_payload_is_43_bytes_with_every_field_in_server_read_order()
    {
        byte[] payload = FullyPopulated();

        Assert.Equal(43, payload.Length);
        Assert.Equal(OnlineCombatPayloads.MoveRoomLength, payload.Length);

        MoveRoomView v = MoveRoomView.Parse(payload);
        Assert.Equal(43, v.Consumed);
        Assert.Equal(5, v.TargetX);
        Assert.Equal(7, v.TargetY);
        Assert.Equal(901 + (0x0A << 16), v.PositionX);
        Assert.Equal(-(902 + (0x0B << 16)), v.PositionY);
        Assert.Equal(1, v.Layered);
        Assert.Equal(7 * 131, v.TrapBits);
        Assert.Equal(new ushort[] { 911, 912, 913, 914 }, v.Seats);
        Assert.Equal(new uint[] { 921, 922 + (0x0Cu << 16), 923, 0xF0000001u }, v.PlayTimes);
        Assert.Equal(931, v.Unused);
        Assert.Equal(941 + (0x0D << 20), v.Tail);
    }

    /// <summary>同一份载荷再按手写的偏移表逐字节核对一遍(两种读法互相印证):偏移 → 字节。</summary>
    [Fact]
    public void The_move_payload_bytes_sit_at_the_documented_offsets()
    {
        byte[] payload = FullyPopulated();

        Assert.Equal(5, payload[0]);                                                              // 目标格 X
        Assert.Equal(7, payload[1]);                                                              // 目标格 Y
        Assert.Equal(Le32(901 + (0x0A << 16)), payload.Skip(2).Take(4).ToArray());                // 位置 X
        Assert.Equal(Le32(-(902 + (0x0B << 16))), payload.Skip(6).Take(4).ToArray());             // 位置 Y(负数:符号字节也要对)
        Assert.Equal(1, payload[10]);                                                             // 分层标志
        Assert.Equal(Le16(7 * 131), payload.Skip(11).Take(2).ToArray());                          // trapBits
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(Le16(911 + i), payload.Skip(13 + 2 * i).Take(2).ToArray());             // 座位项
        }
        Assert.Equal(Le32(921), payload.Skip(21).Take(4).ToArray());                              // 座位 0 的游玩时间
        Assert.Equal(Le32(922 + (0x0C << 16)), payload.Skip(25).Take(4).ToArray());
        Assert.Equal(Le32(923), payload.Skip(29).Take(4).ToArray());
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0xF0 }, payload.Skip(33).Take(4).ToArray());  // 超过 int.MaxValue 的无符号值按位原样
        Assert.Equal(Le16(931), payload.Skip(37).Take(2).ToArray());                              // 未用的 u16
        Assert.Equal(Le32(941 + (0x0D << 20)), payload.Skip(39).Take(4).ToArray());               // 末尾 i32
    }

    /// <summary>每个字段单独变一下,只有它自己的那几个字节跟着变(防止两个字段共用或挤占同一处字节)。</summary>
    [Fact]
    public void Changing_one_field_changes_only_its_own_bytes()
    {
        byte[] baseline = OnlineCombatPayloads.BuildMoveRoom(4, 4);

        void OnlyBytesChanged(byte[] changed, params int[] offsets)
        {
            Assert.Equal(43, changed.Length);
            var diff = new List<int>();
            for (int i = 0; i < 43; i++)
            {
                if (changed[i] != baseline[i])
                {
                    diff.Add(i);
                }
            }
            Assert.Equal(offsets, diff.ToArray());
        }

        OnlyBytesChanged(OnlineCombatPayloads.BuildMoveRoom(5, 4), 0);
        OnlyBytesChanged(OnlineCombatPayloads.BuildMoveRoom(4, 5), 1);
        OnlyBytesChanged(OnlineCombatPayloads.BuildMoveRoom(4, 4, positionX: 0x01020304), 2, 3, 4, 5);
        OnlyBytesChanged(OnlineCombatPayloads.BuildMoveRoom(4, 4, positionY: 0x01020304), 6, 7, 8, 9);
        OnlyBytesChanged(OnlineCombatPayloads.BuildMoveRoom(4, 4, layered: true), 10);
        OnlyBytesChanged(OnlineCombatPayloads.BuildMoveRoom(4, 4, trapBits: 7 * 0x1234), 11, 12);
        OnlyBytesChanged(OnlineCombatPayloads.BuildMoveRoom(4, 4, seatItems: new ushort[] { 0x0102, 0xFFFF, 0xFFFF, 0xFFFF }), 13, 14);
        OnlyBytesChanged(OnlineCombatPayloads.BuildMoveRoom(4, 4, seatItems: new ushort[] { 0xFFFF, 0xFFFF, 0xFFFF, 0x0102 }), 19, 20);
        OnlyBytesChanged(OnlineCombatPayloads.BuildMoveRoom(4, 4, seatPlayTimes: new uint[] { 0x01020304, 0, 0, 0 }), 21, 22, 23, 24);
        OnlyBytesChanged(OnlineCombatPayloads.BuildMoveRoom(4, 4, seatPlayTimes: new uint[] { 0, 0, 0, 0x01020304 }), 33, 34, 35, 36);
        OnlyBytesChanged(OnlineCombatPayloads.BuildMoveRoom(4, 4, unused: 0x0102), 37, 38);
        OnlyBytesChanged(OnlineCombatPayloads.BuildMoveRoom(4, 4, tail: 0x01020304), 39, 40, 41, 42);
    }

    /// <summary>缺省取值:座位整体跳过(0xFFFF)、其余全零;读序游标读出来也一致。</summary>
    [Fact]
    public void The_defaults_skip_every_seat_and_zero_the_rest()
    {
        byte[] payload = OnlineCombatPayloads.BuildMoveRoom(3, 2);

        MoveRoomView v = MoveRoomView.Parse(payload);
        Assert.Equal(3, v.TargetX);
        Assert.Equal(2, v.TargetY);
        Assert.Equal(0, v.PositionX);
        Assert.Equal(0, v.PositionY);
        Assert.Equal(0, v.Layered);
        Assert.Equal(0, v.TrapBits);
        Assert.Equal(new ushort[] { 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF }, v.Seats);
        Assert.Equal(new uint[] { 0, 0, 0, 0 }, v.PlayTimes);
        Assert.Equal(0, v.Unused);
        Assert.Equal(0, v.Tail);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, payload.Skip(13).Take(8).ToArray());
    }

    /// <summary>目标格的边界:0 与 0x31 能拼,出界的在拼装时就拒绝(服务端对它们回错误码或直接断线)。</summary>
    [Fact]
    public void The_target_cell_is_checked_against_the_server_range()
    {
        Assert.Equal(0, MoveRoomView.Parse(OnlineCombatPayloads.BuildMoveRoom(0, 0)).TargetX);
        MoveRoomView edge = MoveRoomView.Parse(OnlineCombatPayloads.BuildMoveRoom(0x31, 0x31));
        Assert.Equal(0x31, edge.TargetX);
        Assert.Equal(0x31, edge.TargetY);

        Assert.Throws<ArgumentOutOfRangeException>(() => OnlineCombatPayloads.BuildMoveRoom(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OnlineCombatPayloads.BuildMoveRoom(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => OnlineCombatPayloads.BuildMoveRoom(0x32, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OnlineCombatPayloads.BuildMoveRoom(0, 0x32));
    }

    /// <summary>trapBits 必须是 7 的倍数(服务端记计数、累计过阈值处罚并断线),拼装时就拒绝其余取值;座位数组长度必须是 4。</summary>
    [Fact]
    public void Values_the_server_would_punish_are_refused_when_building()
    {
        Assert.NotEqual(0, 901 % 7);                                  // 901 不是 7 的倍数:取值选得有意义
        Assert.Throws<ArgumentOutOfRangeException>(() => OnlineCombatPayloads.BuildMoveRoom(1, 1, trapBits: 901));
        Assert.Throws<ArgumentOutOfRangeException>(() => OnlineCombatPayloads.BuildMoveRoom(1, 1, trapBits: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => OnlineCombatPayloads.BuildMoveRoom(1, 1, trapBits: -7));
        Assert.Throws<ArgumentOutOfRangeException>(() => OnlineCombatPayloads.BuildMoveRoom(1, 1, trapBits: 7 * 9363));    // 65541,超过 u16
        Assert.Throws<ArgumentOutOfRangeException>(() => OnlineCombatPayloads.BuildMoveRoom(1, 1, trapBits: 0xFFFF));      // 65535 不是 7 的倍数
        Assert.Equal(Le16(7 * 9362), OnlineCombatPayloads.BuildMoveRoom(1, 1, trapBits: 7 * 9362).Skip(11).Take(2).ToArray());     // 65534 是最大的倍数
        Assert.Equal(new byte[] { 0, 0 }, OnlineCombatPayloads.BuildMoveRoom(1, 1, trapBits: 0).Skip(11).Take(2).ToArray());

        Assert.Throws<ArgumentException>(() => OnlineCombatPayloads.BuildMoveRoom(1, 1, seatItems: new ushort[3]));
        Assert.Throws<ArgumentException>(() => OnlineCombatPayloads.BuildMoveRoom(1, 1, seatItems: new ushort[5]));
        Assert.Throws<ArgumentException>(() => OnlineCombatPayloads.BuildMoveRoom(1, 1, seatPlayTimes: new uint[3]));
        Assert.Throws<ArgumentException>(() => OnlineCombatPayloads.BuildMoveRoom(1, 1, seatPlayTimes: new uint[5]));
    }

    /// <summary>每次拼都给新数组(调用方改它不会影响下一次)。</summary>
    [Fact]
    public void Each_build_returns_a_fresh_array()
    {
        byte[] a = OnlineCombatPayloads.BuildMoveRoom(2, 2);
        byte[] b = OnlineCombatPayloads.BuildMoveRoom(2, 2);

        Assert.NotSame(a, b);
        a[0] = 99;
        Assert.Equal(2, b[0]);
        Assert.Equal(2, OnlineCombatPayloads.BuildMoveRoom(2, 2)[0]);
    }

    // ======== 换房请求对象 ========

    /// <summary>换房请求不可变、构造时校验范围;状态机缺省的载荷工厂把它的五项原样放进载荷。</summary>
    [Fact]
    public async Task The_request_object_carries_its_five_fields_into_the_default_payload()
    {
        var request = new OnlineMoveRoomRequest(6, 2, 901, -902, true);
        Assert.Equal(6, request.TargetX);
        Assert.Equal(2, request.TargetY);
        Assert.Equal(901, request.PositionX);
        Assert.Equal(-902, request.PositionY);
        Assert.True(request.Layered);

        var rig = new RoomRig(1);
        await rig.KillAndConfirmAll();
        OnlineCombatSendResult sent = await rig.Machine.RequestMoveRoomAsync(request);

        Assert.True(sent.Sent);
        (PacketClass cls, ushort id, byte[] payload) = rig.Port.Sent.Last();
        Assert.Equal(PacketClass.Cmd, cls);
        Assert.Equal(48, id);
        MoveRoomView v = MoveRoomView.Parse(payload);
        Assert.Equal(6, v.TargetX);
        Assert.Equal(2, v.TargetY);
        Assert.Equal(901, v.PositionX);
        Assert.Equal(-902, v.PositionY);
        Assert.Equal(1, v.Layered);
        Assert.Equal(0, v.TrapBits);
        Assert.Equal(new ushort[] { 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF }, v.Seats);
    }

    [Fact]
    public void The_request_object_refuses_cells_the_server_range_never_allows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineMoveRoomRequest(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineMoveRoomRequest(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineMoveRoomRequest(0x32, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineMoveRoomRequest(0, 0x32));
        var edge = new OnlineMoveRoomRequest(0x31, 0x31);
        Assert.Equal(0x31, edge.TargetX);
        Assert.False(edge.Layered);
        Assert.Equal(0, edge.PositionX);
        Assert.Equal(0, edge.PositionY);
    }

    // ======== 装载完成载荷 ========

    [Fact]
    public void The_loaded_report_is_two_little_endian_i32s()
    {
        byte[] zero = OnlineCombatPayloads.BuildRoomLoaded();
        Assert.Equal(8, zero.Length);
        Assert.Equal(OnlineCombatPayloads.RoomLoadedLength, zero.Length);
        Assert.Equal(new byte[8], zero);

        byte[] custom = OnlineCombatPayloads.BuildRoomLoaded(901 + (0x0A << 16), -902);
        RoomLoadedView v = RoomLoadedView.Parse(custom);
        Assert.Equal(901 + (0x0A << 16), v.First);
        Assert.Equal(-902, v.Second);
        Assert.Equal(Le32(901 + (0x0A << 16)), custom.Take(4).ToArray());
        Assert.Equal(Le32(-902), custom.Skip(4).ToArray());
        Assert.NotSame(OnlineCombatPayloads.BuildRoomLoaded(), OnlineCombatPayloads.BuildRoomLoaded());
    }

    // ======== 换房失败回包的读法 ========

    /// <summary>错误码取第二个字节,后面的折叠摘要与补零不读;码值本层不解释(含没读通的码)。</summary>
    [Fact]
    public void The_refusal_reader_takes_the_second_byte_and_ignores_the_tail()
    {
        Assert.True(OnlineCombatPayloads.TryReadMoveRoomRefusal(new byte[] { 0, 20 }, out byte code));
        Assert.Equal(20, code);

        Assert.True(OnlineCombatPayloads.TryReadMoveRoomRefusal(new byte[] { 0, 201, 9, 9, 9, 9, 0, 0 }, out code));
        Assert.Equal(201, code);                                      // 没读通的码一视同仁

        Assert.True(OnlineCombatPayloads.TryReadMoveRoomRefusal(new byte[] { 0, 0 }, out code));
        Assert.Equal(0, code);
    }

    /// <summary>成功形状(首字节非 0)、太短、null 都不是失败回包。</summary>
    [Fact]
    public void The_refusal_reader_rejects_shapes_that_are_not_refusals()
    {
        Assert.False(OnlineCombatPayloads.TryReadMoveRoomRefusal(new byte[] { 1, 20 }, out byte code));
        Assert.Equal(0, code);
        Assert.False(OnlineCombatPayloads.TryReadMoveRoomRefusal(new byte[] { 0 }, out code));
        Assert.False(OnlineCombatPayloads.TryReadMoveRoomRefusal(new byte[0], out code));
        Assert.False(OnlineCombatPayloads.TryReadMoveRoomRefusal(null, out code));
        Assert.Equal(0, code);
    }

    /// <summary>独立读法自己的把关:不是 43 字节它就让测试失败(服务端读不满断线、多了就不是这条命令)。</summary>
    [Fact]
    public void The_independent_move_reader_refuses_a_wrong_length()
    {
        Assert.ThrowsAny<Exception>(() => MoveRoomView.Parse(new byte[42]));
        Assert.ThrowsAny<Exception>(() => MoveRoomView.Parse(new byte[44]));
        Assert.ThrowsAny<Exception>(() => RoomLoadedView.Parse(new byte[7]));
        Assert.Equal(43, MoveRoomView.Parse(new byte[43]).Consumed);
    }
}
