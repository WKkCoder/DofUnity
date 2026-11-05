using System.Security.Cryptography;
using Dnf.Net.Crypto;
using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>
/// 会话密码层:包号 % 14 选槽、密钥块切片、ECB 逐块、补齐规则、失败路径。
/// 期望值来自服务端原函数在模拟器里的结果(frame_session_packet_vector.txt 与块级预言机表)、标准库 AES、与手算的偏移和长度。
/// </summary>
public class FrameSessionCipherTests
{
    private static readonly int[] HandOffsets = { 0, 8, 24, 80, 140, 172, 188, 204, 220, 236, 252, 268, 278, 318 };
    private static readonly int[] HandKeyLengths = { 8, 16, 56, 60, 32, 16, 16, 16, 16, 16, 16, 10, 40, 16 };
    private static readonly int[] HandBlockSizes = { 4, 16, 8, 16, 16, 16, 8, 8, 16, 8, 8, 8, 8, 16 };

    private static byte[] SequentialKey()
    {
        byte[] key = new byte[334];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)((i * 7 + 3) & 0xFF);
        }
        return key;
    }

    // ---- 向量回放 ----

    [Fact]
    public void Packet_vectors_replay_with_block_oracle()
    {
        int enc = 0, dec = 0, bad = 0, neg = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_session_packet_vector.txt"))
        {
            // kind slot packet_id key_id block B plain_len in_hex pad_hex ret cap out_len out_hex
            string kind = r[0];
            int id = int.Parse(r[2]);
            SessionCipher cipher = FrameCipherDoubles.OracleSession(r[3]);
            byte[] input = FrameVectorFile.Hex(r[7]);
            byte[] pad = FrameVectorFile.Hex(r[8]);
            byte[] expected = FrameVectorFile.Hex(r[12]);
            switch (kind)
            {
                case "enc":
                    // 原函数把"调用者缓冲区里紧跟载荷的补齐字节"一起加密;这里把补齐字节并进输入,结果必须一致
                    Assert.Equal(expected, cipher.Encrypt(id, input.Concat(pad).ToArray()));
                    if (pad.All(b => b == 0))
                    {
                        Assert.Equal(expected, cipher.Encrypt(id, input));
                    }
                    enc++;
                    break;
                case "dec":
                    Assert.Equal(expected, cipher.Decrypt(id, input));
                    dec++;
                    break;
                case "decbad":
                    Assert.Null(cipher.Decrypt(id, input));
                    bad++;
                    break;
                case "encneg":
                    // 原函数对负长度返回成功且输出长度 0;数组长度不会是负数,对应的是空载荷
                    Assert.Empty(cipher.Encrypt(id, Array.Empty<byte>()));
                    neg++;
                    break;
                case "enccap":
                    // 输出容量不足是服务端缓冲区的语义,本实现按需分配输出,没有对应概念
                    break;
                default:
                    Assert.Fail("未知的向量种类 " + kind);
                    break;
            }
        }
        Assert.Equal(378, enc);
        Assert.Equal(336, dec);
        Assert.Equal(28, bad);
        Assert.Equal(14, neg);
    }

    [Fact]
    public void Slot_one_packet_vectors_match_standard_aes()
    {
        // 槽 1 与标准 AES-128(ECB)一致:同一批向量,换成标准库的块算法也必须得到原函数的结果
        int checkedRows = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_session_packet_vector.txt"))
        {
            if (r[1] != "1" || (r[0] != "enc" && r[0] != "dec"))
            {
                continue;
            }
            SessionCipher cipher = new SessionCipher(FrameCipherDoubles.Key(r[3]), _ => new AesSlotCipher());
            int id = int.Parse(r[2]);
            byte[] input = FrameVectorFile.Hex(r[7]);
            byte[] expected = FrameVectorFile.Hex(r[12]);
            if (r[0] == "enc")
            {
                Assert.Equal(expected, cipher.Encrypt(id, input.Concat(FrameVectorFile.Hex(r[8])).ToArray()));
            }
            else
            {
                Assert.Equal(expected, cipher.Decrypt(id, input));
            }
            checkedRows++;
        }
        Assert.True(checkedRows > 40);
    }

    [Fact]
    public void Slot_one_matches_dotnet_aes_for_all_lengths_and_ids_of_the_slot()
    {
        byte[] key = SequentialKey();
        SessionCipher cipher = new SessionCipher(key, _ => new AesSlotCipher());
        using Aes aes = Aes.Create();
        aes.Key = key.Skip(8).Take(16).ToArray();
        foreach (int id in new[] { 1, 15, 29, 14 * 100 + 1, 65535 })
        {
            for (int len = 0; len <= 70; len++)
            {
                byte[] payload = Enumerable.Range(0, len).Select(i => (byte)(i * 13 + id)).ToArray();
                byte[] padded = new byte[(len + 15) / 16 * 16];
                payload.CopyTo(padded, 0);
                byte[] expected = len == 0 ? Array.Empty<byte>() : aes.EncryptEcb(padded, PaddingMode.None);
                byte[] actual = cipher.Encrypt(id, payload);
                Assert.Equal(expected, actual);
                Assert.Equal(padded, cipher.Decrypt(id, actual));
            }
        }
    }

    [Fact]
    public void Slot_table_matches_measured_key_and_block_sizes()
    {
        List<string[]> rows = FrameVectorFile.Rows("frame_slot_meta_vector.txt");
        Assert.Equal(14, rows.Count);
        foreach (string[] r in rows)
        {
            int slot = int.Parse(r[0]);
            Assert.Equal(int.Parse(r[2]), CipherSlots.KeyLengths[slot]);
            Assert.Equal(int.Parse(r[3]), CipherSlots.BlockSizes[slot]);
            Assert.Equal(HandKeyLengths[slot], int.Parse(r[2]));
            Assert.Equal(HandBlockSizes[slot], int.Parse(r[3]));
        }
    }

    // ---- 选槽与密钥切片 ----

    [Fact]
    public void Each_slot_receives_its_own_key_slice_at_hand_computed_offsets()
    {
        byte[] key = SequentialKey();
        Dictionary<int, SliceXorCipher> created = new();
        SessionCipher cipher = new SessionCipher(key, slot =>
        {
            SliceXorCipher c = new SliceXorCipher(slot);
            created[slot] = c;
            return c;
        });
        for (int slot = 0; slot < 14; slot++)
        {
            byte[] expectedSlice = key.Skip(HandOffsets[slot]).Take(HandKeyLengths[slot]).ToArray();
            int block = HandBlockSizes[slot];
            byte[] plain = Enumerable.Range(0, block).Select(i => (byte)(i + 1)).ToArray();
            byte[] actual = cipher.Encrypt(slot + 14 * 3, plain);
            Assert.Equal(expectedSlice, created[slot].Slice);
            byte[] expected = plain.Select((b, i) => (byte)(b ^ expectedSlice[i % expectedSlice.Length])).ToArray();
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(13, 13)]
    [InlineData(14, 0)]
    [InlineData(15, 1)]
    [InlineData(27, 13)]
    [InlineData(28, 0)]
    [InlineData(65534, 0)]
    [InlineData(65535, 1)]
    public void Slot_is_packet_id_modulo_fourteen(int packetId, int slot)
    {
        Assert.Equal(slot, SessionCipher.SlotOf(packetId));
        Assert.Equal(HandBlockSizes[slot], SessionCipher.BlockSizeOf(packetId));
        int used = -1;
        SessionCipher cipher = new SessionCipher(SequentialKey(), s =>
        {
            used = s;
            return new SliceXorCipher(s);
        });
        Assert.NotNull(cipher.Encrypt(packetId, new byte[] { 1 }));
        Assert.Equal(slot, used);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-14)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Packet_id_outside_sixteen_bits_fails_without_touching_any_slot(int packetId)
    {
        int created = 0;
        SessionCipher cipher = new SessionCipher(SequentialKey(), s =>
        {
            created++;
            return new SliceXorCipher(s);
        });
        Assert.Null(cipher.Encrypt(packetId, new byte[] { 1, 2, 3 }));
        Assert.Null(cipher.Decrypt(packetId, new byte[16]));
        Assert.Equal(0, SessionCipher.BlockSizeOf(packetId));
        Assert.Equal(-1, SessionCipher.SlotOf(packetId));
        Assert.Equal(0, created);
    }

    // ---- 补齐与长度 ----

    [Fact]
    public void Padding_goes_to_next_block_multiple_with_zero_bytes_and_empty_stays_empty()
    {
        SessionCipher cipher = new SessionCipher(SequentialKey(), s => new SliceXorCipher(s));
        foreach (int slot in new[] { 0, 1, 2, 6, 13 })
        {
            int b = HandBlockSizes[slot];
            foreach (int len in new[] { 0, 1, b - 1, b, b + 1, 3 * b, 3 * b + 1 })
            {
                byte[] payload = Enumerable.Repeat((byte)0x5A, len).ToArray();
                byte[] encrypted = cipher.Encrypt(slot, payload);
                int expectedLength = len == 0 ? 0 : (len + b - 1) / b * b;
                Assert.Equal(expectedLength, encrypted.Length);
                byte[] back = cipher.Decrypt(slot, encrypted);
                Assert.Equal(expectedLength, back.Length);
                Assert.Equal(payload, back.Take(len).ToArray());
                Assert.All(back.Skip(len), v => Assert.Equal((byte)0, v));
            }
        }
    }

    [Fact]
    public void PaddedLength_matches_encrypt_output_length_for_every_slot_and_length()
    {
        SessionCipher cipher = new SessionCipher(SequentialKey(), s => new SliceXorCipher(s));
        for (int slot = 0; slot < 14; slot++)
        {
            int b = HandBlockSizes[slot];
            for (int len = 0; len <= 3 * b + 1; len++)
            {
                int expected = len == 0 ? 0 : (len + b - 1) / b * b;
                Assert.Equal(expected, cipher.PaddedLength(slot + 14, len));
                Assert.Equal(expected, cipher.Encrypt(slot + 14, new byte[len]).Length);
            }
        }
        Assert.Equal(-1, cipher.PaddedLength(-1, 4));
        Assert.Equal(-1, cipher.PaddedLength(65536, 4));
        Assert.Equal(-1, cipher.PaddedLength(1, -1));
        Assert.Equal(7, new NullPayloadCipher().PaddedLength(3, 7));
    }

    [Fact]
    public void Each_block_is_independent_and_equal_plain_blocks_give_equal_cipher_blocks()
    {
        // ECB:没有跨块、跨包的状态
        SessionCipher cipher = new SessionCipher(SequentialKey(), _ => new AesSlotCipher());
        byte[] plain = new byte[48];
        byte[] first = cipher.Encrypt(1, plain);
        Assert.Equal(first.Take(16), first.Skip(16).Take(16));
        Assert.Equal(first.Take(16), first.Skip(32));
        Assert.Equal(first, cipher.Encrypt(1, plain));
        byte[] changed = (byte[])plain.Clone();
        changed[17] = 1;
        byte[] second = cipher.Encrypt(1, changed);
        Assert.Equal(first.Take(16), second.Take(16));
        Assert.NotEqual(first.Skip(16).Take(16), second.Skip(16).Take(16));
        Assert.Equal(first.Skip(32), second.Skip(32));
    }

    [Fact]
    public void Decrypt_rejects_lengths_that_are_not_block_multiples_and_accepts_the_boundaries()
    {
        SessionCipher cipher = new SessionCipher(SequentialKey(), s => new SliceXorCipher(s));
        foreach (int slot in new[] { 0, 1, 2, 5, 11 })
        {
            int b = HandBlockSizes[slot];
            Assert.Empty(cipher.Decrypt(slot, Array.Empty<byte>()));
            Assert.Null(cipher.Decrypt(slot, new byte[1]));
            Assert.Null(cipher.Decrypt(slot, new byte[b - 1]));
            Assert.Equal(b, cipher.Decrypt(slot, new byte[b]).Length);
            Assert.Null(cipher.Decrypt(slot, new byte[b + 1]));
            Assert.Equal(2 * b, cipher.Decrypt(slot, new byte[2 * b]).Length);
        }
    }

    [Fact]
    public void Inputs_are_not_modified()
    {
        SessionCipher cipher = new SessionCipher(SequentialKey(), s => new SliceXorCipher(s));
        byte[] payload = { 1, 2, 3, 4, 5 };
        byte[] before = (byte[])payload.Clone();
        cipher.Encrypt(2, payload);
        Assert.Equal(before, payload);
        byte[] ct = new byte[16];
        ct[3] = 9;
        byte[] ctBefore = (byte[])ct.Clone();
        cipher.Decrypt(1, ct);
        Assert.Equal(ctBefore, ct);
    }

    // ---- 构造与工厂 ----

    [Fact]
    public void Key_block_shorter_than_334_is_rejected_and_longer_tail_is_ignored()
    {
        Assert.Throws<ArgumentException>(() => new SessionCipher(new byte[333], s => new SliceXorCipher(s)));
        Assert.Throws<ArgumentNullException>(() => new SessionCipher(null, s => new SliceXorCipher(s)));
        Assert.Throws<ArgumentNullException>(() => new SessionCipher(new byte[334], null));
        byte[] exact = SequentialKey();
        byte[] longer = exact.Concat(new byte[] { 0xEE, 0xEE, 0xEE }).ToArray();
        SessionCipher a = new SessionCipher(exact, s => new SliceXorCipher(s));
        SessionCipher b = new SessionCipher(longer, s => new SliceXorCipher(s));
        byte[] payload = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        Assert.Equal(a.Encrypt(13, payload), b.Encrypt(13, payload));
        // 最后一个槽用到密钥块的最后一字节(下标 333)
        byte[] changedLast = (byte[])exact.Clone();
        changedLast[333] ^= 0xFF;
        SessionCipher c = new SessionCipher(changedLast, s => new SliceXorCipher(s));
        Assert.NotEqual(a.Encrypt(13, payload), c.Encrypt(13, payload));
    }

    [Fact]
    public void Key_block_is_copied_at_construction()
    {
        byte[] key = SequentialKey();
        SessionCipher cipher = new SessionCipher(key, s => new SliceXorCipher(s));
        Array.Clear(key, 0, key.Length);
        byte[] payload = { 1, 2, 3, 4 };
        // 槽对象在第一次用到时才建立:构造之后被清零的原数组不得影响结果
        SessionCipher fresh = new SessionCipher(SequentialKey(), s => new SliceXorCipher(s));
        Assert.Equal(fresh.Encrypt(0, payload), cipher.Encrypt(0, payload));
        Assert.NotEqual(payload, cipher.Encrypt(0, payload));
    }

    [Fact]
    public void Slot_objects_are_created_once_and_only_when_used()
    {
        List<int> created = new();
        SessionCipher cipher = new SessionCipher(SequentialKey(), s =>
        {
            created.Add(s);
            return new SliceXorCipher(s);
        });
        Assert.Empty(created);
        cipher.Encrypt(3, new byte[] { 1 });
        cipher.Encrypt(3 + 14, new byte[] { 1 });
        cipher.Decrypt(3, new byte[16]);
        Assert.Equal(new[] { 3 }, created);
        cipher.Encrypt(4, new byte[] { 1 });
        Assert.Equal(new[] { 3, 4 }, created);
        // 空载荷、空密文不需要算法对象
        cipher.Encrypt(5, Array.Empty<byte>());
        cipher.Decrypt(6, Array.Empty<byte>());
        Assert.Equal(new[] { 3, 4 }, created);
    }

    [Fact]
    public void Factory_result_must_agree_with_slot_table()
    {
        SessionCipher wrongBlock = new SessionCipher(SequentialKey(), _ => new AesSlotCipher());
        // 槽 1 与槽表一致;包号 2 选槽 2,而替身的块长 16 与槽 2 的块长 8 不符
        Assert.NotNull(wrongBlock.Encrypt(1, new byte[] { 1 }));
        Assert.Throws<InvalidOperationException>(() => wrongBlock.Encrypt(2, new byte[] { 1 }));
        // 槽 5 的密钥长 16、块长 16,与替身完全一致,应当成功;槽 6 的密钥长也是 16 但块长是 8:只有块长不符,也必须拒绝
        Assert.NotNull(wrongBlock.Encrypt(5, new byte[] { 1 }));
        Assert.Throws<InvalidOperationException>(() => wrongBlock.Encrypt(6, new byte[] { 1 }));
        SessionCipher none = new SessionCipher(SequentialKey(), _ => null);
        Assert.Throws<InvalidOperationException>(() => none.Encrypt(1, new byte[] { 1 }));
        Assert.Throws<ArgumentNullException>(() => none.Encrypt(1, null));
        Assert.Throws<ArgumentNullException>(() => none.Decrypt(1, null));
    }

    [Fact]
    public void Default_factory_is_not_touched_for_empty_input()
    {
        // 默认工厂用各槽的真实算法;空载荷与空密文不需要算法对象,所以不依赖任何槽的实现
        SessionCipher cipher = new SessionCipher(SequentialKey());
        Assert.Empty(cipher.Encrypt(0 + 14 * 2, Array.Empty<byte>()));
        Assert.Empty(cipher.Decrypt(5, Array.Empty<byte>()));
    }
}
