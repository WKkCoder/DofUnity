using Dnf.Net.Crypto;
using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>
/// 客户端到服务端的整帧编码。期望值是服务端原函数在模拟器里的整帧(frame_frames_vector.txt、frame_pi_vector.txt)与手算的头字段位置。
/// </summary>
public class FrameClientBuilderTests
{
    private sealed class ThrowingCipher : IPayloadCipher
    {
        public byte[] Encrypt(int packetId, byte[] payload) => throw new InvalidOperationException("不应调用");

        public byte[] Decrypt(int packetId, byte[] cipherText) => throw new InvalidOperationException("不应调用");

        public int PaddedLength(int packetId, int length) => throw new InvalidOperationException("不应调用");
    }

    private sealed class FixedCipher : IPayloadCipher
    {
        private readonly Func<byte[], byte[]> _encrypt;
        private readonly Func<int, int> _padded;

        public FixedCipher(Func<byte[], byte[]> encrypt, Func<int, int> padded = null)
        {
            _encrypt = encrypt;
            _padded = padded ?? (n => n);
        }

        public byte[] Encrypt(int packetId, byte[] payload) => _encrypt(payload);

        public byte[] Decrypt(int packetId, byte[] cipherText) => cipherText;

        public int PaddedLength(int packetId, int length) => _padded(length);
    }

    private static Func<byte> Sequence(params byte[] values)
    {
        int next = 0;
        return () => values[next++];
    }

    private static ushort SequenceOf(byte[] wire) => (ushort)(wire[11] | (wire[12] << 8));

    private static IEnumerable<string[]> FrameRows(string kind)
    {
        return FrameVectorFile.Rows("frame_frames_vector.txt").Where(r => r[0] == kind);
    }

    // ---- 向量回放 ----

    [Fact]
    public void Session_frames_match_the_original_wire_bytes()
    {
        int rows = 0;
        foreach (string[] r in FrameRows("c2s"))
        {
            // kind cls pid slot key_id payload_len payload_hex pad_hex plain_hex wire_hex wire_len note
            byte cls = byte.Parse(r[1]);
            ushort pid = ushort.Parse(r[2]);
            byte[] payload = FrameVectorFile.Hex(r[6]);
            byte[] pad = FrameVectorFile.Hex(r[7]);
            byte[] wire = FrameVectorFile.Hex(r[9]);
            Assert.Equal(int.Parse(r[10]), wire.Length);
            Assert.Equal(int.Parse(r[5]), payload.Length);
            Assert.True(pad.Distinct().Count() <= 1);
            ushort seq = SequenceOf(wire);
            ClientFrameBuilder builder = new ClientFrameBuilder(FrameCipherDoubles.OracleSession(r[4]));

            // 补齐字节由构造器按给定取值补上
            Assert.Equal(FrameStatus.Ok, builder.TryBuild((PacketClass)cls, pid, seq, payload, out byte[] frame, pad.Length > 0 ? pad[0] : (byte)0));
            Assert.Equal(wire, frame);

            // 调用方自己补好齐再给,结果相同
            Assert.Equal(FrameStatus.Ok, builder.TryBuild((PacketClass)cls, pid, seq, payload.Concat(pad).ToArray(), out byte[] frame2));
            Assert.Equal(wire, frame2);
            rows++;
        }
        Assert.Equal(116, rows);
    }

    [Fact]
    public void Envelope_frames_skip_cipher_and_pad_and_differ_from_the_original_only_in_the_ignored_check_byte()
    {
        int rows = 0;
        foreach (string[] r in FrameRows("c2s_exempt"))
        {
            byte[] payload = FrameVectorFile.Hex(r[6]);
            byte[] wire = FrameVectorFile.Hex(r[9]);
            ClientFrameBuilder builder = new ClientFrameBuilder(new ThrowingCipher());
            Assert.Equal(FrameStatus.Ok, builder.TryBuild((PacketClass)byte.Parse(r[1]), ushort.Parse(r[2]), SequenceOf(wire), payload, out byte[] frame, 0xA5));
            Assert.Equal(wire.Length, frame.Length);
            Assert.Equal(wire.Take(7), frame.Take(7));
            Assert.Equal(wire.Skip(11), frame.Skip(11));
            // 校验字节服务端不看;这里按普通规则算:对 [序号 + 载荷] 的 crc32N 取低 8 位
            Assert.Equal((byte)Crc32N.Compute(wire.Skip(11).ToArray()), frame[7]);
            Assert.Equal(0, frame[8] | frame[9] | frame[10]);
            rows++;
        }
        Assert.Equal(2, rows);
    }

    [Fact]
    public void Rejected_frames_fail_the_checksum_rule_or_the_length_rules()
    {
        int checkMismatch = 0, notBlock = 0, sizeRows = 0;
        foreach (string[] r in FrameRows("c2s_bad"))
        {
            byte[] wire = FrameVectorFile.Hex(r[9]);
            string note = r[11];
            if (note.StartsWith("size 字段"))
            {
                FrameAssembler assembler = new FrameAssembler(FrameLayout.ClientToServer);
                List<RawFrame> frames = new();
                FrameError error = assembler.Feed(wire, frames);
                Assert.Empty(frames);
                Assert.Equal(note.Contains("0x579") ? FrameError.LengthAboveMaximum : FrameError.LengthBelowMinimum, error);
                sizeRows++;
                continue;
            }
            SessionCipher cipher = FrameCipherDoubles.OracleSession(r[4]);
            ushort pid = ushort.Parse(r[2]);
            byte[] body = wire.Skip(13).ToArray();
            byte[] plain = cipher.Decrypt(pid, body);
            if (note.StartsWith("体长不是块长倍数"))
            {
                Assert.Null(plain);
                Assert.NotEqual(0, body.Length % CipherSlots.BlockSizes[pid % 14]);
                notBlock++;
                continue;
            }
            // 校验字节不符:按规则算出的校验字节与帧里的不同
            Assert.NotNull(plain);
            byte computed = ClientFrameBuilder.ComputeCheckByte(FrameLayout.ClientToServer, wire, plain);
            Assert.NotEqual(wire[7], computed);
            checkMismatch++;
        }
        Assert.Equal(10, checkMismatch);
        Assert.Equal(5, notBlock);
        Assert.Equal(2, sizeRows);
    }

    [Fact]
    public void Second_layer_words_match_the_original_for_all_non_empty_rows()
    {
        int rows = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_pi_vector.txt"))
        {
            byte[] data = FrameVectorFile.Hex(r[3]);
            if (r[0] != "pi_send" || data.Length == 0)
            {
                continue;
            }
            bool reset = r[1] == "1";
            byte prev = Convert.ToByte(r[2], 16);
            byte r1 = Convert.ToByte(r[4], 16);
            uint wordAfter = FrameVectorFile.HexU32(r[6]);
            SecondLayerSender sender = new SecondLayerSender(Sequence(prev, 0, 0, r1, (byte)(wordAfter >> 8)));
            sender.Seal(ReadOnlySpan<byte>.Empty, 0, false);
            ClientFrameBuilder builder = new ClientFrameBuilder(new NullPayloadCipher(), sender);
            // 重置位只由"类 1 包号 1"触发
            PacketClass cls = PacketClass.Cmd;
            ushort pid = reset ? (ushort)1 : (ushort)28;
            Assert.Equal(FrameStatus.Ok, builder.TryBuild(cls, pid, 0x1234, data, out byte[] frame));
            uint word = BitConverter.ToUInt32(frame, 7);
            byte expectedLow = (byte)Crc32N.Compute(new byte[] { 0x34, 0x12 }.Concat(data).ToArray());
            Assert.Equal((wordAfter & 0xFFFFFF00u) | expectedLow, word);
            Assert.Equal(r1, sender.State);
            rows++;
        }
        Assert.True(rows > 100);
    }

    // ---- 头字段与边界 ----

    [Fact]
    public void Header_fields_are_little_endian_at_the_documented_offsets()
    {
        ClientFrameBuilder builder = new ClientFrameBuilder(new NullPayloadCipher());
        Assert.Equal(FrameStatus.Ok, builder.TryBuild((PacketClass)0x7E, 0xABCD, 0x1357, new byte[] { 1, 2, 3 }, out byte[] f));
        Assert.Equal(16, f.Length);
        Assert.Equal(0x7E, f[0]);
        Assert.Equal(0xCD, f[1]);
        Assert.Equal(0xAB, f[2]);
        Assert.Equal(new byte[] { 16, 0, 0, 0 }, f.Skip(3).Take(4));
        Assert.Equal(0, f[8] | f[9] | f[10]);
        Assert.Equal(0x57, f[11]);
        Assert.Equal(0x13, f[12]);
        Assert.Equal(new byte[] { 1, 2, 3 }, f.Skip(13));
        // 校验字节 = 对 [序号两字节 + 明文] 的 crc32N 取低 8 位
        Assert.Equal((byte)Crc32N.Compute(new byte[] { 0x57, 0x13, 1, 2, 3 }), f[7]);
    }

    [Fact]
    public void Check_byte_covers_sequence_and_padding_but_not_class_or_id()
    {
        SessionCipher cipher = new SessionCipher(FrameCipherDoubles.Key("SYNTH_FRAME_K1"), s => new SliceXorCipher(s));
        ClientFrameBuilder builder = new ClientFrameBuilder(cipher);
        byte[] payload = { 1, 2, 3, 4, 5 };
        builder.TryBuild(PacketClass.Cmd, 28, 7, payload, out byte[] a);
        builder.TryBuild(PacketClass.Noti, 28, 7, payload, out byte[] classChanged);
        builder.TryBuild(PacketClass.Cmd, 28 + 14, 7, payload, out byte[] idChangedSameSlot);
        Assert.Equal(a[7], classChanged[7]);
        Assert.Equal(a[7], idChangedSameSlot[7]);
        Assert.Equal(a.Skip(13), idChangedSameSlot.Skip(13));

        // 序号与补齐字节会改变校验字节(挑一组确定不撞的取值)
        List<byte> checks = new();
        for (ushort seq = 0; seq < 40; seq++)
        {
            builder.TryBuild(PacketClass.Cmd, 28, seq, payload, out byte[] f);
            checks.Add(f[7]);
        }
        Assert.True(checks.Distinct().Count() > 20);
        checks.Clear();
        for (int pad = 0; pad < 40; pad++)
        {
            builder.TryBuild(PacketClass.Cmd, 28, 7, payload, out byte[] f, (byte)pad);
            checks.Add(f[7]);
        }
        Assert.True(checks.Distinct().Count() > 20);
    }

    [Fact]
    public void Total_length_upper_bound_is_0x578_inclusive()
    {
        ClientFrameBuilder builder = new ClientFrameBuilder(new NullPayloadCipher());
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 5, 1, new byte[0x578 - 13], out byte[] max));
        Assert.Equal(0x578, max.Length);
        Assert.Equal(0x78, max[3]);
        Assert.Equal(0x05, max[4]);
        Assert.Equal(FrameStatus.FrameTooLarge, builder.TryBuild(PacketClass.Cmd, 5, 1, new byte[0x578 - 13 + 1], out byte[] tooBig));
        Assert.Null(tooBig);
        // 帧长按加密补齐后的长度算
        SessionCipher cipher = new SessionCipher(FrameCipherDoubles.Key("SYNTH_FRAME_K1"), s => new SliceXorCipher(s));
        ClientFrameBuilder session = new ClientFrameBuilder(cipher);
        // 槽 1 块长 16:(0x578 - 13) = 1387 = 86 * 16 + 11,补齐后 1392 > 1387
        Assert.Equal(FrameStatus.FrameTooLarge, session.TryBuild(PacketClass.Cmd, 15, 1, new byte[1387], out _));
        Assert.Equal(FrameStatus.Ok, session.TryBuild(PacketClass.Cmd, 15, 1, new byte[1376], out byte[] ok));
        Assert.Equal(13 + 1376, ok.Length);
        Assert.Equal(FrameStatus.FrameTooLarge, session.TryBuild(PacketClass.Cmd, 15, 1, new byte[1377], out _));
    }

    [Fact]
    public void Empty_payload_gives_a_header_only_frame()
    {
        SessionCipher cipher = new SessionCipher(FrameCipherDoubles.Key("SYNTH_FRAME_K1"), s => new SliceXorCipher(s));
        ClientFrameBuilder builder = new ClientFrameBuilder(cipher);
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 40, 3, Array.Empty<byte>(), out byte[] f, 0xA5));
        Assert.Equal(13, f.Length);
        Assert.Equal((byte)Crc32N.Compute(new byte[] { 3, 0 }), f[7]);
    }

    [Fact]
    public void Cipher_failures_are_reported_not_thrown()
    {
        ClientFrameBuilder failing = new ClientFrameBuilder(new FixedCipher(_ => null));
        Assert.Equal(FrameStatus.EncryptFailed, failing.TryBuild(PacketClass.Cmd, 1, 1, new byte[] { 1 }, out byte[] frame));
        Assert.Null(frame);
        ClientFrameBuilder shrinking = new ClientFrameBuilder(new FixedCipher(p => new byte[p.Length - 1]));
        Assert.Equal(FrameStatus.EncryptFailed, shrinking.TryBuild(PacketClass.Cmd, 1, 1, new byte[] { 1, 2 }, out _));
        // 补齐长度小于载荷长度(接口约定的失败值 -1),或加密结果与补齐长度不符
        ClientFrameBuilder noSlot = new ClientFrameBuilder(new FixedCipher(p => p, _ => -1));
        Assert.Equal(FrameStatus.EncryptFailed, noSlot.TryBuild(PacketClass.Cmd, 1, 1, new byte[] { 1 }, out _));
        ClientFrameBuilder mismatch = new ClientFrameBuilder(new FixedCipher(p => new byte[32], n => 16));
        Assert.Equal(FrameStatus.EncryptFailed, mismatch.TryBuild(PacketClass.Cmd, 1, 1, new byte[] { 1 }, out _, 0x11));
        Assert.Throws<ArgumentNullException>(() => new ClientFrameBuilder(null));
        Assert.Throws<ArgumentNullException>(() => failing.TryBuild(PacketClass.Cmd, 1, 1, null, out _));
    }

    [Fact]
    public void Plain_id_set_is_configurable()
    {
        SessionCipher cipher = new SessionCipher(FrameCipherDoubles.Key("SYNTH_FRAME_K1"), s => new SliceXorCipher(s));
        ClientFrameBuilder builder = new ClientFrameBuilder(cipher);
        byte[] payload = { 1, 2, 3 };
        // 默认只有 0x25C 不加密
        builder.TryBuild(PacketClass.Cmd, 0x25C, 1, payload, out byte[] plain);
        Assert.Equal(payload, plain.Skip(13));
        builder.TryBuild(PacketClass.Cmd, 0x25B, 1, payload, out byte[] encrypted);
        Assert.NotEqual(payload, encrypted.Skip(13).Take(3));
        // 去掉后 0x25C 也加密
        builder.PlainPacketIds.Remove(0x25C);
        builder.TryBuild(PacketClass.Cmd, 0x25C, 1, payload, out byte[] nowEncrypted);
        Assert.NotEqual(payload, nowEncrypted.Skip(13).Take(3));
        // 加上别的包号则不加密
        builder.PlainPacketIds.Add(77);
        builder.TryBuild(PacketClass.Cmd, 77, 1, payload, out byte[] custom);
        Assert.Equal(payload, custom.Skip(13));
    }

    [Fact]
    public void Second_layer_reset_applies_only_to_class_one_packet_one()
    {
        byte[] payload = { 5, 6, 7, 8 };
        // 先让发送方状态变成 0x99;再发一帧,用 r1 = 0x11,r3 = 0x22
        SecondLayerSender Make(out ClientFrameBuilder builder)
        {
            SecondLayerSender s = new SecondLayerSender(Sequence(0x99, 0, 0, 0x11, 0x22));
            s.Seal(ReadOnlySpan<byte>.Empty, 0, false);
            builder = new ClientFrameBuilder(new NullPayloadCipher(), s);
            return s;
        }

        Make(out ClientFrameBuilder reset);
        reset.TryBuild(PacketClass.Cmd, 1, 1, payload, out byte[] viaReset);
        byte hashFromInitial = PacketIntegrity.MakeHash(payload, 0x27);
        Assert.Equal(PacketIntegrity.Scatter(0x11, hashFromInitial), (ushort)(BitConverter.ToUInt32(viaReset, 7) >> 16));

        foreach ((PacketClass cls, ushort id) in new[] { (PacketClass.Noti, (ushort)1), (PacketClass.Cmd, (ushort)2) })
        {
            Make(out ClientFrameBuilder other);
            other.TryBuild(cls, id, 1, payload, out byte[] frame);
            byte hashFromDirty = PacketIntegrity.MakeHash(payload, 0x99);
            Assert.Equal(PacketIntegrity.Scatter(0x11, hashFromDirty), (ushort)(BitConverter.ToUInt32(frame, 7) >> 16));
        }
    }

    [Fact]
    public void Custom_layout_big_endian_header_round_trips_through_the_header_reader()
    {
        FrameLayout layout = FrameLayout.ClientToServer.WithByteOrder(true);
        ClientFrameBuilder builder = new ClientFrameBuilder(new NullPayloadCipher(), null, layout);
        Assert.Same(layout, builder.Layout);
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 0x0102, 0x0A0B, new byte[] { 9 }, out byte[] f));
        Assert.Equal(1, f[1]);
        Assert.Equal(2, f[2]);
        Assert.Equal(new byte[] { 0, 0, 0, 14 }, f.Skip(3).Take(4));
        Assert.Equal(0x0A, f[11]);
        Assert.Equal(0x0B, f[12]);
        Assert.True(PacketHeader.TryRead(layout, f, out PacketHeader h));
        Assert.Equal((ushort)0x0102, h.PacketId);
        Assert.Equal(14u, h.TotalLength);
        Assert.Equal(0x0A0Bu, h.Second);
        // 校验字节的范围取头里从 ChecksumStart 起的字节(此处序号是大端 0A 0B)
        Assert.Equal((byte)Crc32N.Compute(new byte[] { 0x0A, 0x0B, 9 }), (byte)h.Check);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData((ushort)0x00FF)]
    [InlineData((ushort)0x0100)]
    [InlineData((ushort)0x1357)]
    [InlineData((ushort)0xFFFF)]
    public void Sequence_argument_lands_in_header_bytes_11_and_12_little_endian(ushort sequence)
    {
        // 字节 11 = 序号低字节、字节 12 = 序号高字节(E-203:校验字之后的 2 字节序号);三种构造方式都不能丢序号
        byte[] payload = { 1, 2, 3, 4, 5 };
        SessionCipher cipher = new SessionCipher(FrameCipherDoubles.Key("SYNTH_FRAME_K1"), s => new SliceXorCipher(s));
        ClientFrameBuilder[] builders =
        {
            new ClientFrameBuilder(new NullPayloadCipher()),
            new ClientFrameBuilder(cipher),
            ClientFrameBuilder.CreateForServer(cipher, () => 0x5A),
        };
        foreach (ClientFrameBuilder builder in builders)
        {
            Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 28, sequence, payload, out byte[] f));
            Assert.Equal((byte)(sequence & 0xFF), f[11]);
            Assert.Equal((byte)(sequence >> 8), f[12]);
            Assert.Equal(sequence, SequenceOf(f));
            Assert.True(PacketHeader.TryRead(FrameLayout.ClientToServer, f, out PacketHeader h));
            Assert.Equal((uint)sequence, h.Second);
        }
    }

    [Fact]
    public void Different_sequences_give_frames_that_differ_exactly_in_sequence_and_check_byte()
    {
        // 反例:序号不同的两帧,密文区(自偏移 13 起)相同,字节 11..12 不同;校验字节覆盖序号,所以不同序号的校验字节不全相同
        ClientFrameBuilder builder = new ClientFrameBuilder(new NullPayloadCipher());
        byte[] payload = { 9, 8, 7 };
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 28, 0x0001, payload, out byte[] a));
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 28, 0x0002, payload, out byte[] b));
        Assert.Equal(a.Skip(13), b.Skip(13));
        Assert.NotEqual(a.Skip(11).Take(2), b.Skip(11).Take(2));
        Assert.Equal(a.Take(7), b.Take(7));
        Assert.Equal(a.Skip(8).Take(3), b.Skip(8).Take(3));
        // 手算:校验字节 = crc32N([序号低, 序号高, 载荷…]) 的低 8 位
        Assert.Equal((byte)Crc32N.Compute(new byte[] { 1, 0, 9, 8, 7 }), a[7]);
        Assert.Equal((byte)Crc32N.Compute(new byte[] { 2, 0, 9, 8, 7 }), b[7]);
    }

    [Fact]
    public void ComputeCheckByte_uses_header_tail_and_plain_text()
    {
        byte[] header = new byte[13];
        header[11] = 0x34;
        header[12] = 0x12;
        header[7] = 0xFF; // 校验字本身在范围之外
        byte[] plain = { 0xAA, 0xBB };
        byte expected = (byte)Crc32N.Compute(new byte[] { 0x34, 0x12, 0xAA, 0xBB });
        Assert.Equal(expected, ClientFrameBuilder.ComputeCheckByte(FrameLayout.ClientToServer, header, plain));
        header[7] = 0;
        Assert.Equal(expected, ClientFrameBuilder.ComputeCheckByte(FrameLayout.ClientToServer, header, plain));
        Assert.Throws<ArgumentNullException>(() => ClientFrameBuilder.ComputeCheckByte(null, header, plain));
    }
}
