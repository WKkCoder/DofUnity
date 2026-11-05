using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>
/// 包完整性第二层:SuperFastHash、折成 1 字节的哈希、位散布、发送方与接收方的状态机。
/// 期望值来自服务端原函数在模拟器里的结果(frame_primitives_vector.txt、frame_pi_vector.txt)与手算的位位置。
/// </summary>
public class FrameSecondLayerTests
{
    private static Func<byte> Sequence(params byte[] values)
    {
        int next = 0;
        return () => values[next++];
    }

    private static SecondLayerReceiver PrimedReceiver(byte state)
    {
        SecondLayerReceiver receiver = new SecondLayerReceiver();
        Assert.True(receiver.Check(ReadOnlySpan<byte>.Empty, (uint)PacketIntegrity.Scatter(state, 0) << 16, false));
        Assert.Equal(state, receiver.State);
        Assert.Equal(0, receiver.Mismatches);
        return receiver;
    }

    // ---- 哈希原语 ----

    [Fact]
    public void SuperFastHash_vectors()
    {
        int rows = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_primitives_vector.txt"))
        {
            if (r[0] != "sfh")
            {
                continue;
            }
            Assert.Equal(FrameVectorFile.HexU32(r[2]), PacketIntegrity.SuperFastHash(FrameVectorFile.Hex(r[1])));
            rows++;
        }
        Assert.Equal(32, rows);
        Assert.Equal(0u, PacketIntegrity.SuperFastHash(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void MakeHash_vectors()
    {
        int rows = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_primitives_vector.txt"))
        {
            if (r[0] != "makehash")
            {
                continue;
            }
            byte key = Convert.ToByte(r[3], 16);
            byte expected = Convert.ToByte(r[2], 16);
            Assert.Equal(expected, PacketIntegrity.MakeHash(FrameVectorFile.Hex(r[1]), key));
            rows++;
        }
        Assert.Equal(96, rows);
    }

    // ---- 位散布 ----

    [Theory]
    [InlineData(0x00, 0x00, 0x0000)]
    [InlineData(0xFF, 0x00, 0x3333)]
    [InlineData(0x00, 0xFF, 0xCCCC)]
    [InlineData(0xFF, 0xFF, 0xFFFF)]
    // r1 的 8 个位各落在哪里(手算:r1 的位 0,1 到 12,13;位 2,3 到 4,5;位 4,5 到 8,9;位 6,7 到 0,1)
    [InlineData(0x01, 0x00, 0x1000)]
    [InlineData(0x02, 0x00, 0x2000)]
    [InlineData(0x04, 0x00, 0x0010)]
    [InlineData(0x08, 0x00, 0x0020)]
    [InlineData(0x10, 0x00, 0x0100)]
    [InlineData(0x20, 0x00, 0x0200)]
    [InlineData(0x40, 0x00, 0x0001)]
    [InlineData(0x80, 0x00, 0x0002)]
    // h 的 8 个位各落在哪里(位 0,1 到 6,7;位 2,3 到 14,15;位 4,5 到 2,3;位 6,7 到 10,11)
    [InlineData(0x00, 0x01, 0x0040)]
    [InlineData(0x00, 0x02, 0x0080)]
    [InlineData(0x00, 0x04, 0x4000)]
    [InlineData(0x00, 0x08, 0x8000)]
    [InlineData(0x00, 0x10, 0x0004)]
    [InlineData(0x00, 0x20, 0x0008)]
    [InlineData(0x00, 0x40, 0x0400)]
    [InlineData(0x00, 0x80, 0x0800)]
    public void Scatter_hand_computed_bit_positions(int r1, int h, int expected)
    {
        Assert.Equal((ushort)expected, PacketIntegrity.Scatter((byte)r1, (byte)h));
        PacketIntegrity.Unscatter((ushort)expected, out byte back1, out byte backH);
        Assert.Equal((byte)r1, back1);
        Assert.Equal((byte)h, backH);
    }

    [Fact]
    public void Scatter_is_a_bijection_over_all_pairs()
    {
        HashSet<ushort> seen = new();
        for (int r1 = 0; r1 < 256; r1++)
        {
            for (int h = 0; h < 256; h++)
            {
                ushort x = PacketIntegrity.Scatter((byte)r1, (byte)h);
                seen.Add(x);
                PacketIntegrity.Unscatter(x, out byte rr, out byte hh);
                Assert.Equal((byte)r1, rr);
                Assert.Equal((byte)h, hh);
            }
        }
        Assert.Equal(65536, seen.Count);
    }

    // ---- 发送方与接收方,与原函数逐行对照 ----

    [Fact]
    public void Sender_vectors_reproduce_the_original_words()
    {
        int rows = 0, nonEmpty = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_pi_vector.txt"))
        {
            if (r[0] != "pi_send")
            {
                continue;
            }
            // pi_send reset prev_state data_hex state_after word_low word_after ok_recv
            bool reset = r[1] == "1";
            byte prev = Convert.ToByte(r[2], 16);
            byte[] data = FrameVectorFile.Hex(r[3]);
            byte r1 = Convert.ToByte(r[4], 16);
            byte low = Convert.ToByte(r[5], 16);
            uint wordAfter = FrameVectorFile.HexU32(r[6]);
            byte r3 = (byte)(wordAfter >> 8);
            PacketIntegrity.Unscatter((ushort)(wordAfter >> 16), out byte r1InWord, out byte hInWord);
            Assert.Equal(r1, r1InWord);
            // 载荷为空时哈希来自随机字节,只能取字里的值;非空时哈希必须由实现自己算出
            SecondLayerSender run = new SecondLayerSender(Sequence(prev, 0, 0, r1, r3, hInWord));
            run.Seal(ReadOnlySpan<byte>.Empty, 0, false);
            uint word = run.Seal(data, low, reset);
            Assert.Equal(wordAfter, word);
            Assert.Equal(r1, run.State);
            Assert.Equal(low, (byte)word);
            if (data.Length > 0)
            {
                nonEmpty++;
            }
            // 原接收函数认可这个字,所以实现的接收方也必须认可
            SecondLayerReceiver receiver = PrimedReceiver(prev);
            Assert.Equal(r[7] == "1", receiver.Check(data, word, reset));
            rows++;
        }
        Assert.Equal(174, rows);
        Assert.True(nonEmpty > 100);
    }

    [Fact]
    public void Receiver_vectors_reproduce_the_original_verdicts_and_states()
    {
        int rows = 0, rejected = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_pi_vector.txt"))
        {
            if (r[0] != "pi_recv")
            {
                continue;
            }
            // pi_recv reset prev_state data_hex word ok_orig state_after
            bool reset = r[1] == "1";
            byte prev = Convert.ToByte(r[2], 16);
            byte[] data = FrameVectorFile.Hex(r[3]);
            uint word = FrameVectorFile.HexU32(r[4]);
            bool expectedOk = r[5] == "1";
            byte expectedState = Convert.ToByte(r[6], 16);
            SecondLayerReceiver receiver = PrimedReceiver(prev);
            Assert.Equal(expectedOk, receiver.Check(data, word, reset));
            Assert.Equal(expectedState, receiver.State);
            Assert.Equal(expectedOk ? 0 : 1, receiver.Mismatches);
            if (!expectedOk)
            {
                rejected++;
            }
            rows++;
        }
        Assert.Equal(740, rows);
        Assert.True(rejected > 100);
    }

    // ---- 状态机与边界 ----

    [Fact]
    public void Initial_state_is_0x27_and_reset_rules_are_per_direction()
    {
        Assert.Equal((byte)0x27, PacketIntegrity.SecondLayerInitialState);
        Assert.Equal((byte)0x27, new SecondLayerSender(() => 0).State);
        Assert.Equal((byte)0x27, new SecondLayerReceiver().State);

        Assert.True(PacketIntegrity.ResetsStateClientToServer(PacketClass.Cmd, 1));
        Assert.False(PacketIntegrity.ResetsStateClientToServer(PacketClass.Noti, 1));
        Assert.False(PacketIntegrity.ResetsStateClientToServer(PacketClass.Cmd, 2));
        Assert.False(PacketIntegrity.ResetsStateClientToServer(PacketClass.Cmd, 0));
        Assert.True(PacketIntegrity.ResetsStateServerToClient(PacketClass.Noti, 1));
        Assert.False(PacketIntegrity.ResetsStateServerToClient(PacketClass.Cmd, 1));
        Assert.False(PacketIntegrity.ResetsStateServerToClient(PacketClass.Noti, 2));
        Assert.False(PacketIntegrity.ResetsStateServerToClient(PacketClass.Noti, 0));
    }

    [Fact]
    public void Reset_makes_the_hash_use_state_0x27_regardless_of_previous_state()
    {
        byte[] body = { 1, 2, 3, 4, 5, 6, 7 };
        SecondLayerSender reference = new SecondLayerSender(Sequence(0x11, 0x22));
        uint expected = reference.Seal(body, 0xAB, false);

        SecondLayerSender dirty = new SecondLayerSender(Sequence(0x99, 0, 0, 0x11, 0x22));
        dirty.Seal(ReadOnlySpan<byte>.Empty, 0, false);
        Assert.Equal((byte)0x99, dirty.State);
        Assert.Equal(expected, dirty.Seal(body, 0xAB, true));

        SecondLayerSender notReset = new SecondLayerSender(Sequence(0x99, 0, 0, 0x11, 0x22));
        notReset.Seal(ReadOnlySpan<byte>.Empty, 0, false);
        Assert.NotEqual(expected, notReset.Seal(body, 0xAB, false));

        // 接收方同理
        SecondLayerReceiver fresh = new SecondLayerReceiver();
        SecondLayerReceiver stale = PrimedReceiver(0x99);
        Assert.True(fresh.Check(body, expected, false));
        Assert.False(stale.Check(body, expected, false));
        SecondLayerReceiver staleButReset = PrimedReceiver(0x99);
        Assert.True(staleButReset.Check(body, expected, true));
    }

    [Fact]
    public void Receiver_state_follows_r1_even_when_the_check_fails_and_mismatches_are_counted()
    {
        byte[] body = { 9, 8, 7 };
        uint word = new SecondLayerSender(Sequence(0x5C, 0x10)).Seal(body, 1, false);
        // 把字里的哈希位翻一位:不通过,但状态仍取字里的 r1
        PacketIntegrity.Unscatter((ushort)(word >> 16), out byte r1, out byte h);
        uint bad = (word & 0xFFFF) | ((uint)PacketIntegrity.Scatter(r1, (byte)(h ^ 1)) << 16);
        SecondLayerReceiver receiver = new SecondLayerReceiver();
        Assert.False(receiver.Check(body, bad, false));
        Assert.Equal((byte)0x5C, receiver.State);
        Assert.Equal(1, receiver.Mismatches);
        Assert.False(receiver.Check(body, bad, false));
        Assert.Equal(2, receiver.Mismatches);
    }

    [Fact]
    public void Empty_body_always_passes_and_still_advances_the_state()
    {
        SecondLayerReceiver receiver = new SecondLayerReceiver();
        Assert.True(receiver.Check(ReadOnlySpan<byte>.Empty, 0xFFFFFFFFu, false));
        PacketIntegrity.Unscatter(0xFFFF, out byte r1, out _);
        Assert.Equal(r1, receiver.State);
        Assert.Equal(0, receiver.Mismatches);
    }

    [Fact]
    public void Low_byte_and_random_byte_positions_in_the_word()
    {
        // 字节序:低字节 = 调用方给的校验字节,第 1 字节 = r3,第 2、3 字节 = 拼好的 16 位值(小端)
        byte[] body = { 1 };
        uint word = new SecondLayerSender(Sequence(0xA1, 0xB2)).Seal(body, 0xC3, false);
        Assert.Equal((byte)0xC3, (byte)word);
        Assert.Equal((byte)0xB2, (byte)(word >> 8));
        byte h = PacketIntegrity.MakeHash(body, 0x27);
        Assert.Equal(PacketIntegrity.Scatter(0xA1, h), (ushort)(word >> 16));
    }

    [Fact]
    public void System_random_sender_produces_words_the_receiver_accepts()
    {
        SecondLayerSender sender = new SecondLayerSender();
        SecondLayerReceiver receiver = new SecondLayerReceiver();
        for (int i = 0; i < 50; i++)
        {
            byte[] body = Enumerable.Range(0, i).Select(v => (byte)(v * 3 + i)).ToArray();
            uint word = sender.Seal(body, (byte)i, i == 0);
            Assert.Equal((byte)i, (byte)word);
            Assert.True(receiver.Check(body, word, i == 0));
            Assert.Equal(sender.State, receiver.State);
        }
        Assert.Equal(0, receiver.Mismatches);
        Assert.Throws<ArgumentNullException>(() => new SecondLayerSender(null));
    }
}
