using System.Reflection;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 副本信息通知(类 0、包号 28)的明文载荷解析器(纯函数)。载荷全是手工拼的合成字节(手写字节数组或 <see cref="NoticePayloadBuilder"/>),
/// 副本编号等取值全是合成值(9001 等),不读任何真实帧文件,不含真实编号或名字。
/// 读序依据 clire_NOTES §2.5:u16 副本编号;u8 B1..B4;u8 C1、C2;u8 D;u8 n 与 n 组 {u8 E1, u8 E2};u16 F;u8 G;共 13 + 2n 字节,
/// 其后的尾巴(4 字节校验尾等)解析器不消费,只报告个数。
/// 偏移(从 0 起):[0..1] 副本编号,[2..5] B1..B4,[6..7] C1、C2,[8] D,[9] n,[10 ..] n 组,然后 F(2 字节)、G(1 字节)。
/// </summary>
public class DungeonInfoParserTests
{
    // ---- 合成载荷 ----

    /// <summary>
    /// 标准头部:每个单字节字段取值互不相同,字段错位或对调后读出的值会变;副本编号 9001 = 0x2329、F = 0x1234,高低字节不同,字节序错了值会变。
    /// </summary>
    private static NoticePayloadBuilder StandardHead(int pairCount)
    {
        return new NoticePayloadBuilder()
            .U16(9001)
            .U8(0x11).U8(0x12).U8(0x13).U8(0x14)
            .U8(0x21).U8(0x22)
            .U8(0x31)
            .U8(pairCount);
    }

    /// <summary>标准载荷:n 组由参数按 E1, E2, E1, E2 ... 给出,后面是 F = 0x1234、G = 0x41;没有尾巴。</summary>
    private static byte[] Standard(params int[] pairBytes)
    {
        NoticePayloadBuilder b = StandardHead(pairBytes.Length / 2);
        foreach (int value in pairBytes)
        {
            b.U8(value);
        }
        return b.U16(0x1234).U8(0x41).ToArray();
    }

    private static DungeonInfoNotice ParseOk(byte[] payload)
    {
        Assert.True(DungeonInfoParser.TryParse(payload, out DungeonInfoNotice notice));
        Assert.NotNull(notice);
        return notice;
    }

    /// <summary>被拒:不抛异常、返回 false、notice 为 null。</summary>
    private static void AssertRejected(byte[] payload)
    {
        DungeonInfoNotice notice = null;
        bool ok = true;
        Exception ex = Record.Exception(() => { ok = DungeonInfoParser.TryParse(payload, out notice); });
        Assert.Null(ex);
        Assert.False(ok);
        Assert.Null(notice);
    }

    /// <summary>标准载荷里头部各字段(不含 n 与之后的部分)的期望值。</summary>
    private static void AssertStandardHead(DungeonInfoNotice n)
    {
        Assert.Equal(9001, n.DungeonId);
        Assert.Equal(0x11, n.B1);
        Assert.Equal(0x12, n.B2);
        Assert.Equal(0x13, n.B3);
        Assert.Equal(0x14, n.B4);
        Assert.Equal(0x21, n.C1);
        Assert.Equal(0x22, n.C2);
        Assert.Equal(0x31, n.D);
    }

    // ---- 常量与空输入 ----

    [Fact]
    public void Production_constant_matches_the_documented_id()
    {
        // 包号写成字面量:产品常量被改动时,测试要能发现
        Assert.Equal(28, DungeonInfoParser.PacketId);
    }

    [Fact]
    public void Null_payload_is_rejected_without_throwing()
    {
        AssertRejected(null);
    }

    [Fact]
    public void Empty_payload_is_rejected()
    {
        AssertRejected(new byte[0]);
    }

    // ---- 读序与字节序(手写字节,逐字段标注) ----

    [Fact]
    public void Reads_every_field_in_client_order_little_endian()
    {
        byte[] payload =
        {
            0x29, 0x23,             // 副本编号 9001(合成值)= 0x2329,小端
            0x11, 0x12, 0x13, 0x14, // B1..B4
            0x21, 0x22,             // C1、C2
            0x31,                   // D
            0x00,                   // n = 0
            0x34, 0x12,             // F = 0x1234,小端
            0x41,                   // G
        };
        Assert.Equal(13, payload.Length);

        DungeonInfoNotice n = ParseOk(payload);
        AssertStandardHead(n);
        Assert.Empty(n.Pairs);
        Assert.Equal(0x1234, n.F);
        Assert.Equal(0x41, n.G);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void Pairs_are_read_in_wire_order_and_each_pair_is_E1_then_E2()
    {
        byte[] payload = Standard(0x51, 0x52, 0x53, 0x54, 0x55, 0x56);
        Assert.Equal(19, payload.Length); // 13 + 2 * 3

        DungeonInfoNotice n = ParseOk(payload);
        AssertStandardHead(n);
        Assert.Equal(3, n.Pairs.Count);
        Assert.Equal(0x51, n.Pairs[0].E1);
        Assert.Equal(0x52, n.Pairs[0].E2);
        Assert.Equal(0x53, n.Pairs[1].E1);
        Assert.Equal(0x54, n.Pairs[1].E2);
        Assert.Equal(0x55, n.Pairs[2].E1);
        Assert.Equal(0x56, n.Pairs[2].E2);

        // F 与 G 紧接在最后一组之后
        Assert.Equal(0x1234, n.F);
        Assert.Equal(0x41, n.G);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public void Pair_count_boundaries_read_exactly_n_pairs(int count)
    {
        int[] pairBytes = new int[count * 2];
        for (int i = 0; i < count; i++)
        {
            pairBytes[2 * i] = i;             // E1 = i
            pairBytes[(2 * i) + 1] = 255 - i; // E2 = 255 - i
        }

        byte[] payload = Standard(pairBytes);
        Assert.Equal(13 + (2 * count), payload.Length);

        DungeonInfoNotice n = ParseOk(payload);
        AssertStandardHead(n);
        Assert.Equal(count, n.Pairs.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(i, n.Pairs[i].E1);
            Assert.Equal(255 - i, n.Pairs[i].E2);
        }
        Assert.Equal(0x1234, n.F);
        Assert.Equal(0x41, n.G);
        Assert.Equal(0, n.TrailingByteCount);
    }

    [Fact]
    public void Pair_count_sweep_succeeds_exactly_when_the_pairs_fit()
    {
        // 固定 3 组数据 + F + G 的载荷(19 字节),把 n(下标 9)逐值换成 0..255:
        // n <= 3 时 13 + 2n <= 19,读得下,剩下的字节算尾巴;n > 3 时读不下
        byte[] baseline = Standard(0x51, 0x52, 0x53, 0x54, 0x55, 0x56);
        Assert.Equal(19, baseline.Length);
        for (int n = 0; n <= 255; n++)
        {
            byte[] payload = (byte[])baseline.Clone();
            payload[9] = (byte)n;
            if (n <= 3)
            {
                DungeonInfoNotice notice = ParseOk(payload);
                Assert.Equal(n, notice.Pairs.Count);
                Assert.Equal(19 - (13 + (2 * n)), notice.TrailingByteCount);
            }
            else
            {
                AssertRejected(payload);
            }
        }
    }

    // ---- 尾巴:不消费、不核对,只报告个数 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(11)]
    public void Trailing_bytes_are_counted_but_never_consumed_or_checked(int tailLength)
    {
        // 尾巴刻意拼成"又一组 E1、E2 再加 F、G"的形状:被错吞就会改变 Pairs 的个数或 F、G
        byte[] tail = new byte[tailLength];
        for (int i = 0; i < tailLength; i++)
        {
            tail[i] = (byte)(0xA0 + i);
        }
        byte[] payload = Standard(0x51, 0x52).Concat(tail).ToArray();

        DungeonInfoNotice n = ParseOk(payload);
        AssertStandardHead(n);
        DungeonInfoPair only = Assert.Single(n.Pairs);
        Assert.Equal(0x51, only.E1);
        Assert.Equal(0x52, only.E2);
        Assert.Equal(0x1234, n.F);
        Assert.Equal(0x41, n.G);
        Assert.Equal(tailLength, n.TrailingByteCount);
    }

    [Fact]
    public void Trailing_hash_and_padding_shaped_tail_is_tolerated()
    {
        // 尾巴形如"4 字节校验 + 会话加密补的零":读完 G 就停,不要求恰好读完,也不核对
        byte[] tail = new byte[] { 0xA1, 0xB2, 0xC3, 0xD4 }.Concat(new byte[7]).ToArray();
        DungeonInfoNotice n = ParseOk(Standard().Concat(tail).ToArray());
        Assert.Equal(0x1234, n.F);
        Assert.Equal(0x41, n.G);
        Assert.Equal(11, n.TrailingByteCount);
    }

    // ---- 短包与个数超出载荷:一律拒绝,不抛 ----

    [Fact]
    public void Every_strict_prefix_of_a_valid_payload_is_rejected()
    {
        // n = 2:前缀落在头部各字节、n 之后、成对字节之间、F 与 G 之前
        byte[] full = Standard(0x51, 0x52, 0x53, 0x54);
        Assert.Equal(17, full.Length); // 13 + 2 * 2
        ParseOk(full);
        for (int len = 0; len < full.Length; len++)
        {
            AssertRejected(full.Take(len).ToArray());
        }
    }

    [Fact]
    public void Pair_count_larger_than_the_available_data_is_rejected()
    {
        // 声明 3 组,实际只给 2 组 + F + G(共 17 字节,需要 19):第三组吃掉 F,F 又读不满
        byte[] declared3 = StandardHead(3).U8(0x51).U8(0x52).U8(0x53).U8(0x54).U16(0x1234).U8(0x41).ToArray();
        Assert.Equal(17, declared3.Length);
        AssertRejected(declared3);

        // 声明 255 组,只给 20 字节数据
        byte[] declared255 = StandardHead(255).Raw(new byte[20]).ToArray();
        AssertRejected(declared255);
    }

    // ---- 取值不做范围检查 ----

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    public void Field_values_are_not_range_checked(int fill)
    {
        // 除 n(保持 0)外每个字节都取同一个边界值:副本编号取 0 与 65535 也照收(解析器没有副本表)
        byte f = (byte)fill;
        byte[] payload = { f, f, f, f, f, f, f, f, f, 0x00, f, f, f };
        Assert.Equal(13, payload.Length);

        DungeonInfoNotice n = ParseOk(payload);
        Assert.Equal(fill * 0x101, n.DungeonId);
        Assert.Equal(fill, n.B1);
        Assert.Equal(fill, n.B2);
        Assert.Equal(fill, n.B3);
        Assert.Equal(fill, n.B4);
        Assert.Equal(fill, n.C1);
        Assert.Equal(fill, n.C2);
        Assert.Equal(fill, n.D);
        Assert.Empty(n.Pairs);
        Assert.Equal(fill * 0x101, n.F);
        Assert.Equal(fill, n.G);
        Assert.Equal(0, n.TrailingByteCount);
    }

    // ---- 容错:任意载荷都不抛,成败与长度规则一致 ----

    [Fact]
    public void Arbitrary_payloads_never_throw_and_follow_the_length_rule()
    {
        // 确定性伪随机字节(固定种子):0..64 字节 × 每个长度 40 份;偶数份把 n(下标 9)压到 0..7,让成功分支有足够样本
        uint seed = 0x13579BDF;
        int successes = 0;
        for (int len = 0; len <= 64; len++)
        {
            for (int round = 0; round < 40; round++)
            {
                byte[] body = new byte[len];
                for (int i = 0; i < len; i++)
                {
                    seed = (seed * 1664525u) + 1013904223u;
                    body[i] = (byte)(seed >> 24);
                }
                if (len > 9 && round % 2 == 0)
                {
                    body[9] = (byte)(body[9] % 8);
                }

                // 长度规则(与产品读取代码无关的独立推导):至少读到 n,且总长 13 + 2n 不超过载荷
                bool expectOk = len >= 10 && len >= 13 + (2 * body[9]);

                DungeonInfoNotice n = null;
                bool ok = false;
                Exception ex = Record.Exception(() => { ok = DungeonInfoParser.TryParse(body, out n); });
                Assert.Null(ex);
                Assert.Equal(expectOk, ok);
                if (!expectOk)
                {
                    Assert.Null(n);
                    continue;
                }

                successes++;
                int pairs = body[9];
                Assert.NotNull(n);
                Assert.Equal(body[0] | (body[1] << 8), n.DungeonId);
                Assert.Equal(body[2], n.B1);
                Assert.Equal(body[3], n.B2);
                Assert.Equal(body[4], n.B3);
                Assert.Equal(body[5], n.B4);
                Assert.Equal(body[6], n.C1);
                Assert.Equal(body[7], n.C2);
                Assert.Equal(body[8], n.D);
                Assert.Equal(pairs, n.Pairs.Count);
                for (int i = 0; i < pairs; i++)
                {
                    Assert.Equal(body[10 + (2 * i)], n.Pairs[i].E1);
                    Assert.Equal(body[11 + (2 * i)], n.Pairs[i].E2);
                }
                int fAt = 10 + (2 * pairs);
                Assert.Equal(body[fAt] | (body[fAt + 1] << 8), n.F);
                Assert.Equal(body[fAt + 2], n.G);
                Assert.Equal(len - (fAt + 3), n.TrailingByteCount);
            }
        }

        // 成功分支确实被大量覆盖(防止这个用例退化成只测失败)
        Assert.True(successes > 100);
    }

    // ---- 只读与不可变 ----

    [Fact]
    public void Notice_types_are_read_only_and_cannot_be_constructed_outside_the_parser()
    {
        foreach (Type type in new[] { typeof(DungeonInfoNotice), typeof(DungeonInfoPair) })
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                Assert.Null(property.GetSetMethod(true));
            }
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.True(field.IsInitOnly);
            }
        }

        // 外部只能经解析器拿到记录
        Assert.Empty(typeof(DungeonInfoNotice).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.True(typeof(DungeonInfoPair).IsValueType);

        // 实例字段只许简单值与只读列表接口:不留载荷数组或可写集合的引用
        Type[] allowed = { typeof(ushort), typeof(byte), typeof(int), typeof(IReadOnlyList<DungeonInfoPair>) };
        foreach (FieldInfo field in typeof(DungeonInfoNotice).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Assert.Contains(field.FieldType, allowed);
        }
    }

    [Fact]
    public void Pairs_list_rejects_every_write_even_through_IList()
    {
        DungeonInfoNotice n = ParseOk(Standard(0x51, 0x52));

        // 数组即使经 IList 也允许按下标写,所以结果不能是数组
        Assert.False(n.Pairs is Array);
        IList<DungeonInfoPair> list = Assert.IsAssignableFrom<IList<DungeonInfoPair>>(n.Pairs);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => { list.Add(default); });
        Assert.Throws<NotSupportedException>(() => { list.Insert(0, default); });
        Assert.Throws<NotSupportedException>(() => { list[0] = default; });
        Assert.Throws<NotSupportedException>(() => { list.RemoveAt(0); });
        Assert.Throws<NotSupportedException>(() => { list.Clear(); });

        // 写都失败了,内容没变
        DungeonInfoPair only = Assert.Single(n.Pairs);
        Assert.Equal(0x51, only.E1);
        Assert.Equal(0x52, only.E2);
    }

    [Fact]
    public void Result_does_not_alias_the_input_buffer()
    {
        byte[] payload = Standard(0x51, 0x52);
        DungeonInfoNotice n = ParseOk(payload);

        // 解析之后把输入整个改写:结果不能跟着变
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = 0xEE;
        }

        AssertStandardHead(n);
        DungeonInfoPair only = Assert.Single(n.Pairs);
        Assert.Equal(0x51, only.E1);
        Assert.Equal(0x52, only.E2);
        Assert.Equal(0x1234, n.F);
        Assert.Equal(0x41, n.G);
    }

    [Fact]
    public void Parsing_does_not_modify_the_input_and_is_repeatable()
    {
        byte[] payload = Standard(0x51, 0x52, 0x53, 0x54).Concat(new byte[] { 1, 2, 3 }).ToArray();
        byte[] before = (byte[])payload.Clone();

        DungeonInfoNotice first = ParseOk(payload);
        DungeonInfoNotice second = ParseOk(payload);

        Assert.Equal(before, payload);
        Assert.NotSame(first, second);
        Assert.Equal(first.DungeonId, second.DungeonId);
        Assert.Equal(first.B1, second.B1);
        Assert.Equal(first.B4, second.B4);
        Assert.Equal(first.C2, second.C2);
        Assert.Equal(first.D, second.D);
        Assert.Equal(first.Pairs.Count, second.Pairs.Count);
        Assert.Equal(first.Pairs[1].E2, second.Pairs[1].E2);
        Assert.Equal(first.F, second.F);
        Assert.Equal(first.G, second.G);
        Assert.Equal(first.TrailingByteCount, second.TrailingByteCount);
    }
}
