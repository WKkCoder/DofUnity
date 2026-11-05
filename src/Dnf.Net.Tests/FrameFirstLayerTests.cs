using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>
/// 包完整性第一层(载荷尾折叠哈希)在客户端到服务端方向的适用范围与边界。
/// 期望值来自服务端原检查函数在模拟器里的扫描与逐行结果(frame_pi1_vector.txt:事件数大于 0 表示服务端判定不符)。
/// </summary>
public class FrameFirstLayerTests
{
    private static IEnumerable<string[]> Rows(Func<string, bool> scenario)
    {
        return FrameVectorFile.Rows("frame_pi1_vector.txt").Where(r => scenario(r[0]));
    }

    [Fact]
    public void Required_packet_ids_equal_the_original_function_scan_result()
    {
        // scan_hit:对类 0..3、包号 0..1023 全范围扫描(错误的尾)后,只列出触发了事件的行
        List<string[]> hits = Rows(s => s == "scan_hit").ToList();
        Assert.Equal(17, hits.Count);
        Assert.All(hits, r => Assert.Equal("1", r[2]));
        Assert.All(hits, r => Assert.Equal("1", r[6]));
        int[] scanned = hits.Select(r => int.Parse(r[3])).OrderBy(v => v).ToArray();
        Assert.Equal(scanned, PacketIntegrity.ClientFoldHashPacketIds.OrderBy(v => v).ToArray());
        Assert.Equal(17, PacketIntegrity.ClientFoldHashPacketIds.Distinct().Count());
        foreach (int id in scanned)
        {
            Assert.True(PacketIntegrity.RequiresClientFoldHash(PacketClass.Cmd, id));
            Assert.False(PacketIntegrity.RequiresClientFoldHash(PacketClass.Noti, id));
            Assert.False(PacketIntegrity.RequiresClientFoldHash((PacketClass)2, id));
        }
    }

    [Fact]
    public void Other_classes_and_neighbouring_ids_are_not_required()
    {
        int rows = 0;
        foreach (string[] r in Rows(s => s == "other_class" || s == "neighbor_id"))
        {
            Assert.Equal("0", r[6]);
            Assert.False(PacketIntegrity.RequiresClientFoldHash((PacketClass)byte.Parse(r[2]), int.Parse(r[3])));
            rows++;
        }
        Assert.Equal(68 + 24, rows);
        // 0 号包号与最大包号不在列表里
        Assert.False(PacketIntegrity.RequiresClientFoldHash(PacketClass.Cmd, 0));
        Assert.False(PacketIntegrity.RequiresClientFoldHash(PacketClass.Cmd, 65535));
    }

    [Fact]
    public void Tail_rows_are_accepted_exactly_when_the_tail_is_the_little_endian_fold_hash()
    {
        int accepted = 0, rejected = 0;
        foreach (string[] r in Rows(s => s.StartsWith("tail_pid_")))
        {
            byte[] fields = FrameVectorFile.Hex(r[4]);
            byte[] tail = FrameVectorFile.Hex(r[5]);
            byte[] expected = PacketIntegrity.AppendFoldHash(fields).Skip(fields.Length).ToArray();
            Assert.Equal(4, expected.Length);
            bool tailIsRight = tail.SequenceEqual(expected);
            Assert.Equal(tailIsRight, r[6] == "0");
            if (tailIsRight)
            {
                accepted++;
            }
            else
            {
                rejected++;
            }
        }
        Assert.Equal(17 * 6, accepted);
        Assert.Equal(17 * 18, rejected);
    }

    [Fact]
    public void Length_bounds_follow_one_to_four_hundred()
    {
        int rows = 0, right = 0;
        foreach (string[] r in Rows(s => s == "len_bounds"))
        {
            byte[] fields = FrameVectorFile.Hex(r[4]);
            int length = fields.Length;
            byte[] expectedTail = PacketIntegrity.AppendFoldHash(fields).Skip(length).ToArray();
            bool tailIsRight = expectedTail.Length == 4 && FrameVectorFile.Hex(r[5]).SequenceEqual(expectedTail);
            // 前 8 行的尾是错的:服务端只在字段区间长 1..400 时才比较;最后一行长 400、尾正确
            Assert.Equal(!tailIsRight && PacketIntegrity.HasFoldHash(length), r[6] == "1");
            if (tailIsRight)
            {
                Assert.Equal(400, length);
                right++;
            }
            rows++;
        }
        Assert.Equal(9, rows);
        Assert.Equal(1, right);
    }

    [Fact]
    public void Builder_appends_the_hash_only_for_required_class_one_packets_and_only_when_enabled()
    {
        byte[] fields = { 1, 2, 3, 4, 5, 6, 7, 8 };
        byte[] hashed = PacketIntegrity.AppendFoldHash(fields);
        Assert.Equal(12, hashed.Length);

        ClientFrameBuilder builder = new ClientFrameBuilder(new NullPayloadCipher());
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 30, 1, fields, out byte[] byDefault));
        Assert.Equal(fields, byDefault.Skip(13));

        builder.AppendRequiredFoldHash = true;
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 30, 1, fields, out byte[] required));
        Assert.Equal(hashed, required.Skip(13));
        Assert.Equal(25, required.Length);
        // 类 0 或不在列表里的包号不追加
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Noti, 30, 1, fields, out byte[] noti));
        Assert.Equal(fields, noti.Skip(13));
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 32, 1, fields, out byte[] other));
        Assert.Equal(fields, other.Skip(13));
        // 空载荷与超过 400 字节的载荷不追加
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 30, 1, Array.Empty<byte>(), out byte[] empty));
        Assert.Equal(13, empty.Length);
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 30, 1, new byte[400], out byte[] max));
        Assert.Equal(13 + 404, max.Length);
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 30, 1, new byte[401], out byte[] over));
        Assert.Equal(13 + 401, over.Length);
    }

    [Fact]
    public void AppendFoldHash_boundaries_and_copy_semantics()
    {
        Assert.Empty(PacketIntegrity.AppendFoldHash(Array.Empty<byte>()));
        Assert.Equal(5, PacketIntegrity.AppendFoldHash(new byte[1]).Length);
        Assert.Equal(404, PacketIntegrity.AppendFoldHash(new byte[400]).Length);
        Assert.Equal(401, PacketIntegrity.AppendFoldHash(new byte[401]).Length);
        byte[] source = { 9, 9 };
        byte[] copy = PacketIntegrity.AppendFoldHash(new byte[0]);
        Assert.NotSame(source, PacketIntegrity.AppendFoldHash(source));
        Assert.NotSame(copy, PacketIntegrity.AppendFoldHash(copy));
        // 小端:尾 4 字节还原成整数等于折叠哈希
        byte[] hashed = PacketIntegrity.AppendFoldHash(source);
        Assert.Equal(PacketIntegrity.FoldHash(source), BitConverter.ToUInt32(hashed, 2));
        Assert.Throws<ArgumentNullException>(() => PacketIntegrity.AppendFoldHash(null));
    }
}
