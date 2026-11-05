using System.IO.Compression;
using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>
/// 服务端到客户端的整帧解码。期望值来自服务端发包整条链(定稿、压缩、加密、重算校验、发送钩子)在模拟器里跑出的线上字节
/// (frame_frames_vector.txt、frame_reject_vector.txt),压缩数据另用标准库的 zlib 压缩器生成。
/// </summary>
public class FrameServerDecoderTests
{
    private static ServerFrameDecoder NewDecoder()
    {
        ServerFrameDecoder decoder = new ServerFrameDecoder
        {
            Cipher = FrameCipherDoubles.OracleSession("SYNTH_FRAME_K1"),
        };
        // 向量里的类 0 包号 561 是普通的会话加密帧;默认的"不加密集合"在别的测试里单独检查
        decoder.PlainNotiIds.Clear();
        return decoder;
    }

    /// <summary>手工拼的帧不在预言机表里,用密钥切片异或的替身解密。</summary>
    private static ServerFrameDecoder NewXorDecoder()
    {
        ServerFrameDecoder decoder = new ServerFrameDecoder
        {
            Cipher = new SessionCipher(FrameCipherDoubles.Key("SYNTH_FRAME_K1"), s => new SliceXorCipher(s)),
        };
        decoder.PlainNotiIds.Clear();
        return decoder;
    }

    private static IEnumerable<string[]> S2cRows() => FrameVectorFile.Rows("frame_frames_vector.txt").Where(r => r[0] == "s2c");

    private static Dictionary<int, int> BlockSizes()
    {
        Dictionary<int, int> map = new();
        foreach (string[] r in FrameVectorFile.Rows("frame_slot_meta_vector.txt"))
        {
            map[int.Parse(r[0])] = int.Parse(r[3]);
        }
        return map;
    }

    private static SecondLayerReceiver ReceiverWithState(byte state)
    {
        SecondLayerReceiver receiver = new SecondLayerReceiver();
        receiver.Check(ReadOnlySpan<byte>.Empty, (uint)PacketIntegrity.Scatter(state, 0) << 16, false);
        return receiver;
    }

    /// <summary>手工拼一个没有发送钩子的服务端帧:两个校验值相等。</summary>
    private static byte[] MakeFrame(byte cls, ushort id, byte[] body, uint? crcA = null, uint? crcB = null)
    {
        uint crc = Crc32N.Compute(body);
        byte[] f = new byte[15 + body.Length];
        f[0] = cls;
        f[1] = (byte)id;
        f[2] = (byte)(id >> 8);
        BitConverter.GetBytes((uint)f.Length).CopyTo(f, 3);
        BitConverter.GetBytes(crcA ?? crc).CopyTo(f, 7);
        BitConverter.GetBytes(crcB ?? crc).CopyTo(f, 11);
        body.CopyTo(f, 15);
        return f;
    }

    private static byte[] Zlib(byte[] data)
    {
        using MemoryStream ms = new();
        using (ZLibStream z = new(ms, CompressionLevel.Optimal, true))
        {
            z.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }

    private static byte[] Payload(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 + 1)).ToArray();

    // ---- 向量回放 ----

    [Fact]
    public void Wire_frames_decode_to_the_original_payload_with_second_layer_state()
    {
        Dictionary<int, int> blocks = BlockSizes();
        int rows = 0, compressedRows = 0, bootstrapRows = 0;
        foreach (string[] r in S2cRows())
        {
            // kind cls pid slot key_id payload_len payload_hex pad_hex plain_hex wire_hex wire_len note pi_prev final_wire_hex pi_state_after
            byte cls = byte.Parse(r[1]);
            ushort pid = ushort.Parse(r[2]);
            byte[] payload = FrameVectorFile.Hex(r[6]);
            byte[] finalized = FrameVectorFile.Hex(r[8]);
            byte[] wire = FrameVectorFile.Hex(r[13]);
            byte piPrev = Convert.ToByte(r[12], 16);
            Assert.Equal(int.Parse(r[10]), FrameVectorFile.Hex(r[9]).Length);

            ServerFrameDecoder decoder = NewDecoder();
            decoder.SecondLayer = ReceiverWithState(piPrev);
            Assert.Equal(FrameStatus.Ok, decoder.Decode(wire, out DecodedFrame decoded));
            Assert.Equal(cls, decoded.Header.ClassByte);
            Assert.Equal(pid, decoded.Header.PacketId);
            Assert.Equal((uint)wire.Length, decoded.Header.TotalLength);
            Assert.True(decoded.SecondLayerChecked);
            Assert.True(decoded.SecondLayerOk);
            Assert.Equal(Convert.ToByte(r[14], 16), decoder.SecondLayer.State);

            bool compressed = pid == 2 || pid == 9 || pid == 0x57;
            Assert.Equal(compressed, decoded.WasCompressed);
            byte[] finalizedBody = finalized.Skip(15).ToArray();
            if (cls == 0 && pid == 1)
            {
                Assert.Equal(DecodedKind.Bootstrap, decoded.Kind);
                Assert.Equal(finalizedBody, decoded.Payload);
                bootstrapRows++;
            }
            else if (compressed)
            {
                Assert.Equal(DecodedKind.Session, decoded.Kind);
                Assert.Equal(PacketIntegrity.AppendFoldHash(payload), decoded.Payload);
                compressedRows++;
            }
            else
            {
                Assert.Equal(DecodedKind.Session, decoded.Kind);
                int block = blocks[pid % 14];
                int padded = finalizedBody.Length == 0 ? 0 : (finalizedBody.Length + block - 1) / block * block;
                Assert.Equal(padded, decoded.Payload.Length);
                Assert.Equal(finalizedBody, decoded.Payload.Take(finalizedBody.Length));
                Assert.All(decoded.Payload.Skip(finalizedBody.Length), b => Assert.Equal((byte)0, b));
            }
            rows++;
        }
        Assert.Equal(240, rows);
        Assert.Equal(36, compressedRows);
        Assert.Equal(13, bootstrapRows);
    }

    [Fact]
    public void Frames_before_the_send_hook_decode_too_and_are_not_second_layer_checked_without_a_receiver()
    {
        int rows = 0;
        foreach (string[] r in S2cRows())
        {
            ServerFrameDecoder decoder = NewDecoder();
            byte[] preHook = FrameVectorFile.Hex(r[9]);
            byte[] postHook = FrameVectorFile.Hex(r[13]);
            Assert.Equal(FrameStatus.Ok, decoder.Decode(preHook, out DecodedFrame a));
            Assert.Equal(FrameStatus.Ok, decoder.Decode(postHook, out DecodedFrame b));
            Assert.False(a.SecondLayerChecked);
            Assert.True(a.SecondLayerOk);
            Assert.Equal(a.Payload, b.Payload);
            // 钩子只改偏移 0xC..0xE
            for (int i = 0; i < preHook.Length; i++)
            {
                if (i < 12 || i > 14)
                {
                    Assert.Equal(preHook[i], postHook[i]);
                }
            }
            rows++;
        }
        Assert.Equal(240, rows);
    }

    [Fact]
    public void Reject_and_unencrypted_frames_from_the_server_chain()
    {
        int reject = 0, plain = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_reject_vector.txt"))
        {
            // kind cls pid payload_len payload_hex plain_hex pi_prev final_hex pi_state_after note
            ServerFrameDecoder decoder = new ServerFrameDecoder();
            byte[] payload = FrameVectorFile.Hex(r[4]);
            if (r[0] == "accept_reject")
            {
                byte[] wire = FrameVectorFile.Hex(r[5]);
                Assert.Equal(21, wire.Length);
                // 没有会话密码也能解:拒绝帧是明文
                Assert.Equal(FrameStatus.Ok, decoder.Decode(wire, out DecodedFrame d));
                Assert.Equal(DecodedKind.Rejected, d.Kind);
                Assert.Equal(PacketIntegrity.AppendFoldHash(payload), d.Payload);
                Assert.Equal(new byte[] { 0, 4 }, d.Payload.Take(2));
                Assert.False(d.WasCompressed);
                reject++;
            }
            else
            {
                Assert.Equal("nonenc_hook", r[0]);
                byte[] wire = FrameVectorFile.Hex(r[7]);
                decoder.SecondLayer = ReceiverWithState(Convert.ToByte(r[6], 16));
                Assert.Equal(FrameStatus.Ok, decoder.Decode(wire, out DecodedFrame d));
                Assert.Equal(DecodedKind.Plain, d.Kind);
                Assert.Equal(PacketIntegrity.AppendFoldHash(payload), d.Payload);
                Assert.True(d.SecondLayerOk);
                Assert.Equal(Convert.ToByte(r[8], 16), decoder.SecondLayer.State);
                plain++;
            }
        }
        Assert.Equal(1, reject);
        Assert.Equal(10, plain);
    }

    [Fact]
    public void Assembler_and_decoder_handle_a_byte_stream_in_any_chunking()
    {
        List<byte[]> wires = S2cRows().Select(r => FrameVectorFile.Hex(r[13])).ToList();
        byte[] stream = wires.SelectMany(w => w).ToArray();
        ServerFrameDecoder reference = NewDecoder();
        List<byte[]> expected = wires.Select(w =>
        {
            Assert.Equal(FrameStatus.Ok, reference.Decode(w, out DecodedFrame d));
            return d.Payload;
        }).ToList();

        foreach (int chunk in new[] { 1, 2, 7, 15, 16, 100, stream.Length })
        {
            FrameAssembler assembler = new FrameAssembler(FrameLayout.ServerToClient);
            ServerFrameDecoder decoder = NewDecoder();
            List<RawFrame> raws = new();
            for (int offset = 0; offset < stream.Length; offset += chunk)
            {
                Assert.Equal(FrameError.None, assembler.Feed(stream, offset, Math.Min(chunk, stream.Length - offset), raws));
            }
            Assert.Equal(wires.Count, raws.Count);
            Assert.Equal(0, assembler.PendingBytes);
            for (int i = 0; i < raws.Count; i++)
            {
                Assert.Equal(FrameStatus.Ok, decoder.Decode(raws[i], out DecodedFrame d));
                Assert.Equal(expected[i], d.Payload);
            }
        }
    }

    // ---- 校验与长度 ----

    [Fact]
    public void Tampering_with_checksums_or_ciphertext_is_detected_and_the_hook_bytes_are_not_compared()
    {
        string[] row = S2cRows().First(r => r[2] == "30" && r[5] != "0");
        byte[] good = FrameVectorFile.Hex(row[13]);
        Assert.Equal(FrameStatus.Ok, NewDecoder().Decode(good, out _));

        foreach (int index in new[] { 7, 8, 9, 10, 11, 15, good.Length - 1 })
        {
            byte[] bad = (byte[])good.Clone();
            bad[index] ^= 0x01;
            Assert.Equal(FrameStatus.ChecksumMismatch, NewDecoder().Decode(bad, out DecodedFrame d));
            Assert.Null(d);
        }
        // 偏移 12..14 是发送钩子的字节,不与校验值比较
        foreach (int index in new[] { 12, 13, 14 })
        {
            byte[] hooked = (byte[])good.Clone();
            hooked[index] ^= 0xFF;
            Assert.Equal(FrameStatus.Ok, NewDecoder().Decode(hooked, out _));
        }
        // 关闭校验后篡改的帧照常解
        ServerFrameDecoder lenient = NewDecoder();
        lenient.VerifyChecksums = false;
        byte[] flipped = (byte[])good.Clone();
        flipped[7] ^= 0x01;
        Assert.Equal(FrameStatus.Ok, lenient.Decode(flipped, out _));
    }

    [Fact]
    public void Second_layer_mismatch_is_reported_but_never_rejects_the_frame()
    {
        string[] row = S2cRows().First(r => r[2] == "30" && r[5] != "0");
        byte[] good = FrameVectorFile.Hex(row[13]);
        byte piPrev = Convert.ToByte(row[12], 16);

        ServerFrameDecoder wrongState = NewDecoder();
        wrongState.SecondLayer = ReceiverWithState((byte)(piPrev ^ 0x40));
        Assert.Equal(FrameStatus.Ok, wrongState.Decode(good, out DecodedFrame d));
        Assert.True(d.SecondLayerChecked);
        Assert.False(d.SecondLayerOk);
        Assert.Equal(1, wrongState.SecondLayer.Mismatches);

        ServerFrameDecoder rightState = NewDecoder();
        rightState.SecondLayer = ReceiverWithState(piPrev);
        Assert.Equal(FrameStatus.Ok, rightState.Decode(good, out DecodedFrame ok));
        Assert.True(ok.SecondLayerOk);
        Assert.Equal(0, rightState.SecondLayer.Mismatches);
    }

    [Fact]
    public void Header_and_length_errors()
    {
        ServerFrameDecoder decoder = NewXorDecoder();
        byte[] good = MakeFrame(0, 29, new byte[16]);
        Assert.Equal(FrameStatus.TruncatedHeader, decoder.Decode(new byte[14], out DecodedFrame d));
        Assert.Null(d);
        Assert.Equal(FrameStatus.TruncatedHeader, decoder.Decode(Array.Empty<byte>(), out _));
        Assert.Equal(FrameStatus.LengthMismatch, decoder.Decode(good.Take(good.Length - 1).ToArray(), out _));
        Assert.Equal(FrameStatus.LengthMismatch, decoder.Decode(good.Concat(new byte[] { 0 }).ToArray(), out _));
        // 刚好 15 字节:只有头,空载荷
        Assert.Equal(FrameStatus.Ok, decoder.Decode(MakeFrame(0, 29, Array.Empty<byte>()), out DecodedFrame empty));
        Assert.Empty(empty.Payload);
        Assert.Throws<ArgumentNullException>(() => decoder.Decode((byte[])null, out _));
    }

    // ---- 分流 ----

    [Fact]
    public void Class_zero_packet_one_is_bootstrap_without_a_session_cipher()
    {
        ServerFrameDecoder decoder = new ServerFrameDecoder();
        byte[] body = Payload(40);
        byte[] frame = MakeFrame(0, 1, BootstrapTransform.Encode(body));
        Assert.Equal(FrameStatus.Ok, decoder.Decode(frame, out DecodedFrame d));
        Assert.Equal(DecodedKind.Bootstrap, d.Kind);
        Assert.Equal(body, d.Payload);
        // 类 1 包号 1(长度不是 21)走会话解密,没有密码就是 NoCipher
        byte[] cmd = MakeFrame(1, 1, new byte[16]);
        Assert.Equal(FrameStatus.NoCipher, decoder.Decode(cmd, out DecodedFrame none));
        Assert.Null(none);
        // 类 0 其他包号同样需要会话密码
        Assert.Equal(FrameStatus.NoCipher, decoder.Decode(MakeFrame(0, 2 + 14, new byte[16]), out _));
    }

    [Fact]
    public void Reject_frame_needs_class_one_packet_one_and_exactly_21_bytes()
    {
        byte[] body = PacketIntegrity.AppendFoldHash(new byte[] { 0, 4 });
        Assert.Equal(6, body.Length);
        ServerFrameDecoder decoder = NewXorDecoder();
        Assert.Equal(FrameStatus.Ok, decoder.Decode(MakeFrame(1, 1, body), out DecodedFrame reject));
        Assert.Equal(DecodedKind.Rejected, reject.Kind);
        Assert.Equal(body, reject.Payload);

        // 同样 21 字节但类 0:启动包变换(长度不变)
        Assert.Equal(FrameStatus.Ok, decoder.Decode(MakeFrame(0, 1, body), out DecodedFrame boot));
        Assert.Equal(DecodedKind.Bootstrap, boot.Kind);
        Assert.Equal(BootstrapTransform.Decode(body), boot.Payload);
        // 类 1 包号 2:会话解密,长度 6 不是块长倍数
        Assert.Equal(FrameStatus.CipherFailed, decoder.Decode(MakeFrame(1, 2, body), out _));
        // 类 1 包号 1 但长度 31(16 字节密文):会话解密
        Assert.Equal(FrameStatus.Ok, decoder.Decode(MakeFrame(1, 1, new byte[16]), out DecodedFrame session));
        Assert.Equal(DecodedKind.Session, session.Kind);
        // 拒绝帧没有发送钩子:两个校验值必须完全相等,且等于对明文的 crc32N
        uint crc = Crc32N.Compute(body);
        Assert.Equal(FrameStatus.ChecksumMismatch, decoder.Decode(MakeFrame(1, 1, body, crc, crc ^ 0x100), out _));
        Assert.Equal(FrameStatus.ChecksumMismatch, decoder.Decode(MakeFrame(1, 1, body, crc ^ 1, crc ^ 1), out _));
        // 拒绝帧不参与第二层
        decoder.SecondLayer = new SecondLayerReceiver();
        Assert.Equal(FrameStatus.Ok, decoder.Decode(MakeFrame(1, 1, body), out DecodedFrame again));
        Assert.False(again.SecondLayerChecked);
        Assert.Equal(PacketIntegrity.SecondLayerInitialState, decoder.SecondLayer.State);
    }

    [Fact]
    public void Plain_noti_ids_default_to_0x231_for_class_zero_only_and_are_configurable()
    {
        byte[] body = Payload(20);
        ServerFrameDecoder decoder = new ServerFrameDecoder { Cipher = new SessionCipher(FrameCipherDoubles.Key("SYNTH_FRAME_K1"), s => new SliceXorCipher(s)) };
        Assert.Contains(0x231, decoder.PlainNotiIds);
        Assert.Single(decoder.PlainNotiIds);
        Assert.Equal(FrameStatus.Ok, decoder.Decode(MakeFrame(0, 0x231, body), out DecodedFrame d));
        Assert.Equal(DecodedKind.Plain, d.Kind);
        Assert.Equal(body, d.Payload);
        // 类 1 的同号包不在集合里:20 字节不是块长倍数 -> 会话解密失败
        Assert.Equal(FrameStatus.CipherFailed, decoder.Decode(MakeFrame(1, 0x231, body), out _));
        Assert.Equal(FrameStatus.CipherFailed, decoder.Decode(MakeFrame(0, 0x232, body), out _));
        decoder.PlainNotiIds.Add(0x232);
        Assert.Equal(FrameStatus.Ok, decoder.Decode(MakeFrame(0, 0x232, body), out DecodedFrame d2));
        Assert.Equal(DecodedKind.Plain, d2.Kind);
        decoder.PlainNotiIds.Remove(0x231);
        Assert.Equal(FrameStatus.CipherFailed, decoder.Decode(MakeFrame(0, 0x231, body), out _));
        // 明文帧的校验照常检查
        decoder.PlainNotiIds.Add(0x231);
        uint crc = Crc32N.Compute(body);
        Assert.Equal(FrameStatus.ChecksumMismatch, decoder.Decode(MakeFrame(0, 0x231, body, crc ^ 1, crc ^ 1), out _));
    }

    [Fact]
    public void Session_failures_are_reported()
    {
        ServerFrameDecoder decoder = NewXorDecoder();
        Assert.Equal(FrameStatus.CipherFailed, decoder.Decode(MakeFrame(0, 29, new byte[15]), out _));
        Assert.Equal(FrameStatus.CipherFailed, decoder.Decode(MakeFrame(0, 29, new byte[17]), out _));
        decoder.Cipher = null;
        Assert.Equal(FrameStatus.NoCipher, decoder.Decode(MakeFrame(0, 29, new byte[16]), out _));
    }

    // ---- 压缩 ----

    [Theory]
    [InlineData(0, 2, true)]
    [InlineData(0, 9, true)]
    [InlineData(0, 0x57, true)]
    [InlineData(1, 2, true)]
    [InlineData(1, 0x57, true)]
    [InlineData(0, 1, false)]
    [InlineData(0, 3, false)]
    [InlineData(0, 8, false)]
    [InlineData(0, 10, false)]
    [InlineData(0, 0x56, false)]
    [InlineData(0, 0x58, false)]
    [InlineData(0, 0x57 + 0x100, false)]
    public void Compressed_ids_are_two_nine_and_0x57_for_both_classes(int cls, int id, bool compressed)
    {
        Assert.Equal(compressed, ServerFrameDecoder.IsCompressedId(id));
        byte[] original = Payload(300);
        byte[] zipped = Zlib(original);
        ServerFrameDecoder decoder = new ServerFrameDecoder { Cipher = new NullPayloadCipher() };
        FrameStatus status = decoder.Decode(MakeFrame((byte)cls, (ushort)id, zipped), out DecodedFrame d);
        if (compressed)
        {
            Assert.Equal(FrameStatus.Ok, status);
            Assert.True(d.WasCompressed);
            Assert.Equal(original, d.Payload);
        }
        else if (cls == 0 && id == 1)
        {
            Assert.Equal(DecodedKind.Bootstrap, d.Kind);
        }
        else
        {
            Assert.Equal(FrameStatus.Ok, status);
            Assert.False(d.WasCompressed);
            Assert.Equal(zipped, d.Payload);
        }
    }

    [Fact]
    public void Zero_padding_after_the_zlib_stream_is_accepted_and_other_trailing_bytes_are_not()
    {
        byte[] original = Payload(100);
        byte[] zipped = Zlib(original);
        ServerFrameDecoder decoder = new ServerFrameDecoder { Cipher = new NullPayloadCipher() };
        for (int pad = 0; pad <= 17; pad++)
        {
            byte[] body = zipped.Concat(new byte[pad]).ToArray();
            Assert.Equal(FrameStatus.Ok, decoder.Decode(MakeFrame(0, 2, body), out DecodedFrame d));
            Assert.Equal(original, d.Payload);
        }
        byte[] garbage = zipped.Concat(new byte[] { 0, 0, 7 }).ToArray();
        Assert.Equal(FrameStatus.InflateFailed, decoder.Decode(MakeFrame(0, 2, garbage), out _));
    }

    [Fact]
    public void Corrupt_zlib_data_fails_without_throwing()
    {
        byte[] original = Payload(200);
        byte[] zipped = Zlib(original);
        ServerFrameDecoder decoder = new ServerFrameDecoder { Cipher = new NullPayloadCipher() };

        // 头:压缩方法不是 8、头两字节不能被 31 整除、带预设字典
        foreach (Action<byte[]> corrupt in new Action<byte[]>[]
        {
            b => b[0] = 0x79,
            b => b[1] ^= 0x01,
            b => { b[1] = (byte)(b[1] | 0x20); },
            b => b[b.Length - 1] ^= 0x01,   // Adler-32 最后一字节
            b => b[b.Length - 4] ^= 0x80,   // Adler-32 第一字节
            b => b[5] ^= 0xFF,              // deflate 数据中间
        })
        {
            byte[] bad = (byte[])zipped.Clone();
            corrupt(bad);
            Assert.Equal(FrameStatus.InflateFailed, decoder.Decode(MakeFrame(0, 9, bad), out DecodedFrame d));
            Assert.Null(d);
        }
        // 截断(去掉 Adler-32 的一部分)
        Assert.Equal(FrameStatus.InflateFailed, decoder.Decode(MakeFrame(0, 9, zipped.Take(zipped.Length - 1).ToArray()), out _));
        Assert.Equal(FrameStatus.InflateFailed, decoder.Decode(MakeFrame(0, 9, zipped.Take(zipped.Length / 2).ToArray()), out _));
        // 空载荷、太短
        Assert.Equal(FrameStatus.InflateFailed, decoder.Decode(MakeFrame(0, 9, Array.Empty<byte>()), out _));
        Assert.Equal(FrameStatus.InflateFailed, decoder.Decode(MakeFrame(0, 9, new byte[6]), out _));
    }

    [Fact]
    public void Inflated_size_limit_is_inclusive()
    {
        byte[] original = new byte[5000];
        byte[] zipped = Zlib(original);
        ServerFrameDecoder decoder = new ServerFrameDecoder { Cipher = new NullPayloadCipher() };
        decoder.MaxInflatedLength = 5000;
        Assert.Equal(FrameStatus.Ok, decoder.Decode(MakeFrame(0, 87, zipped), out DecodedFrame d));
        Assert.Equal(5000, d.Payload.Length);
        decoder.MaxInflatedLength = 4999;
        Assert.Equal(FrameStatus.PayloadTooLarge, decoder.Decode(MakeFrame(0, 87, zipped), out DecodedFrame none));
        Assert.Null(none);
        // 默认上限是单包缓冲 95000
        Assert.Equal(95000, new ServerFrameDecoder().MaxInflatedLength);
        byte[] atLimit = Zlib(new byte[95000]);
        ServerFrameDecoder normal = new ServerFrameDecoder { Cipher = new NullPayloadCipher() };
        Assert.Equal(FrameStatus.Ok, normal.Decode(MakeFrame(0, 2, atLimit), out DecodedFrame big));
        Assert.Equal(95000, big.Payload.Length);
        Assert.Equal(FrameStatus.PayloadTooLarge, normal.Decode(MakeFrame(0, 2, Zlib(new byte[95001])), out _));
    }

    [Fact]
    public void Compressed_session_frame_decrypts_before_inflating()
    {
        // 会话加密(替身:密钥切片异或)之后的压缩包:先解密,再解压
        byte[] key = FrameCipherDoubles.Key("SYNTH_FRAME_K1");
        SessionCipher cipher = new SessionCipher(key, s => new SliceXorCipher(s));
        byte[] original = Payload(250);
        byte[] zipped = Zlib(original);
        byte[] encrypted = cipher.Encrypt(2, zipped);
        ServerFrameDecoder decoder = new ServerFrameDecoder { Cipher = cipher };
        Assert.Equal(FrameStatus.Ok, decoder.Decode(MakeFrame(0, 2, encrypted), out DecodedFrame d));
        Assert.Equal(original, d.Payload);
        Assert.Equal(DecodedKind.Session, d.Kind);
        // 密文与明文不同,不先解密就解压必然失败
        Assert.NotEqual(zipped, encrypted.Take(zipped.Length));
    }

    [Fact]
    public void Plain_compressed_frame_skips_the_checksum_comparison()
    {
        // 明文帧的第一校验值是对压缩前的数据算的,无法只靠帧体比对,所以压缩的明文帧不比较校验
        byte[] original = Payload(60);
        byte[] zipped = Zlib(original);
        ServerFrameDecoder decoder = new ServerFrameDecoder { Cipher = new NullPayloadCipher() };
        decoder.PlainNotiIds.Add(2);
        uint crcOfOriginal = Crc32N.Compute(original);
        Assert.Equal(FrameStatus.Ok, decoder.Decode(MakeFrame(0, 2, zipped, crcOfOriginal, Crc32N.Compute(zipped)), out DecodedFrame d));
        Assert.Equal(DecodedKind.Plain, d.Kind);
        Assert.True(d.WasCompressed);
        Assert.Equal(original, d.Payload);
    }

    [Fact]
    public void Layout_property_and_raw_frame_overload()
    {
        ServerFrameDecoder decoder = NewXorDecoder();
        Assert.Same(FrameLayout.ServerToClient, decoder.Layout);
        byte[] bytes = MakeFrame(0, 29, new byte[16]);
        Assert.True(PacketHeader.TryRead(FrameLayout.ServerToClient, bytes, out PacketHeader h));
        Assert.Equal(FrameStatus.Ok, decoder.Decode(new RawFrame(h, bytes), out DecodedFrame d));
        Assert.Equal(h, d.Header);
    }

    [Theory]
    [InlineData(255, 300)]   // 手算:Adler-32 的低半部 a = 1 + 300 * 255 = 76501 > 65521,必须取模后才与 .NET 的 zlib 写出的校验一致
    [InlineData(255, 257)]   // a = 1 + 257 * 255 = 65536,刚过模数 65521 一点点
    [InlineData(255, 256)]   // a = 1 + 256 * 255 = 65281 < 65521,不取模;作为边界对照
    [InlineData(200, 5000)]
    public void Inflate_checks_adler32_with_modulus_65521_when_low_half_overflows(int fill, int length)
    {
        byte[] original = Enumerable.Repeat((byte)fill, length).ToArray();
        ServerFrameDecoder decoder = new ServerFrameDecoder { Cipher = new NullPayloadCipher() };
        Assert.Equal(FrameStatus.Ok, decoder.Decode(MakeFrame(0, 2, Zlib(original)), out DecodedFrame d));
        Assert.True(d.WasCompressed);
        Assert.Equal(original, d.Payload);
    }
}
