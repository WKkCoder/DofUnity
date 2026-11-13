using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.PktImplTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 清图通知(类 0、包号 0x1f)的明文载荷解析器(纯函数)。读码结论:服务端两个发送点都只写包头就 finalize,载荷长度为 0;
/// 客户端处理器不调用任何读原语(settlenoti_NOTES §2.1、§3.1)。所以解析器没有字段可读:任何非 null 载荷都成功,
/// 全部字节都算尾巴,只在 <see cref="DungeonClearNotice.TrailingByteCount"/> 报告个数。载荷全是合成字节,不含任何真实数据。
/// </summary>
public class DungeonClearParserTests
{
    // 包号写字面量,不引用产品常量:产品常量被改动时,测试要能发现。
    private const ushort ClearId = 0x1f;

    private static DungeonClearNotice ParseOk(byte[] payload)
    {
        Assert.True(DungeonClearParser.TryParse(payload, out DungeonClearNotice notice));
        Assert.NotNull(notice);
        return notice;
    }

    [Fact]
    public void Production_constant_matches_the_documented_id()
    {
        Assert.Equal(0x1f, DungeonClearParser.PacketId);
        Assert.Equal(31, DungeonClearParser.PacketId);
    }

    [Fact]
    public void An_empty_payload_is_the_normal_shape_with_no_trailing_bytes()
    {
        DungeonClearNotice n = ParseOk(new byte[0]);

        Assert.Equal(0, n.TrailingByteCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(255)]
    [InlineData(1000)]
    public void Any_bytes_are_trailing_bytes_and_are_counted_not_consumed(int length)
    {
        byte[] payload = new byte[length];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(0x31 + (i * 11));
        }

        DungeonClearNotice n = ParseOk(payload);

        Assert.Equal(length, n.TrailingByteCount);
    }

    [Fact]
    public void A_server_style_fold_hash_tail_and_encryption_padding_are_just_trailing_bytes()
    {
        // 空载荷长度 0:服务端不追加折叠哈希(只有 1 到 0x190 才追加);会话加密补零仍可能存在
        byte[] none = PacketIntegrity.AppendFoldHash(new byte[0]);
        Assert.Empty(none);
        Assert.Equal(0, ParseOk(none).TrailingByteCount);
        Assert.Equal(6, ParseOk(none.Concat(new byte[6]).ToArray()).TrailingByteCount);

        // 万一载荷带 4 字节折叠哈希,同样只是尾巴
        byte[] hashed = PacketIntegrity.AppendFoldHash(new byte[] { 0x01, 0x02, 0x03 });
        Assert.Equal(7, hashed.Length);
        Assert.Equal(7, ParseOk(hashed).TrailingByteCount);
    }

    [Fact]
    public void Null_is_rejected_without_throwing_and_the_notice_is_null()
    {
        DungeonClearNotice notice = null;
        bool ok = true;
        Exception ex = Record.Exception(() => { ok = DungeonClearParser.TryParse(null, out notice); });

        Assert.Null(ex);
        Assert.False(ok);
        Assert.Null(notice);
    }

    [Fact]
    public void Parsing_does_not_modify_the_input_and_the_result_does_not_alias_it()
    {
        byte[] payload = { 0x10, 0x20, 0x30 };
        byte[] before = (byte[])payload.Clone();

        DungeonClearNotice first = ParseOk(payload);
        DungeonClearNotice second = ParseOk(payload);
        payload[0] = 0xEE;

        Assert.Equal(new byte[] { 0xEE, 0x20, 0x30 }, payload);
        Assert.Equal(before.Length, first.TrailingByteCount);
        Assert.NotSame(first, second);
        Assert.Equal(first.TrailingByteCount, second.TrailingByteCount);
    }

    [Fact]
    public void The_record_has_only_the_trailing_count_member_and_is_read_only()
    {
        PropertyInfo[] props = typeof(DungeonClearNotice).GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.Equal(new[] { "TrailingByteCount" }, props.Select(p => p.Name).ToArray());
        Assert.Null(props[0].GetSetMethod(true));
        Assert.Empty(typeof(DungeonClearNotice).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        foreach (FieldInfo field in typeof(DungeonClearNotice).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Assert.True(field.IsInitOnly);
            Assert.False(field.IsPublic);
            Assert.Equal(typeof(int), field.FieldType);
        }
    }

    [Fact]
    public void A_decoded_frame_with_an_empty_payload_parses_and_carries_the_documented_header()
    {
        // 走真实解码路径:类 0、包号 0x1f、载荷长度 0
        DecodedFrame frame = PktImplTestKit.Frame(Noti, ClearId);

        Assert.Equal(PacketClass.Noti, frame.Header.Class);
        Assert.Equal(ClearId, frame.Header.PacketId);
        Assert.Equal(DungeonClearParser.PacketId, frame.Header.PacketId);
        Assert.True(DungeonClearParser.TryParse(frame.Payload, out DungeonClearNotice n));
        Assert.Equal(frame.Payload.Length, n.TrailingByteCount);
    }
}
