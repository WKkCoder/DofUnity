using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>载荷尾折叠哈希(第一层包完整性,0x0808c8b5):32 行向量来自本机模拟器跑原函数;长度范围规则与反例另测。</summary>
public sealed class FrameIntegrityTests
{
    [Fact]
    public void FoldHashMatchesTheEmulatorVectors()
    {
        int hashed = 0;
        int skipped = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_primitives_vector.txt"))
        {
            if (r[0] != "md5fold")
            {
                continue;
            }
            byte[] input = FrameVectorFile.Hex(r[1]);
            if (r[2] == "none")
            {
                // 原函数对长度 0 与超过 400 的载荷不产生哈希
                Assert.False(PacketIntegrity.HasFoldHash(input.Length));
                skipped++;
            }
            else
            {
                Assert.True(PacketIntegrity.HasFoldHash(input.Length));
                Assert.Equal(FrameVectorFile.HexU32(r[2]), PacketIntegrity.FoldHash(input));
                hashed++;
            }
        }
        Assert.Equal(28, hashed);
        Assert.Equal(4, skipped);
    }

    [Fact]
    public void LengthRangeBoundaries()
    {
        Assert.False(PacketIntegrity.HasFoldHash(0));
        Assert.True(PacketIntegrity.HasFoldHash(1));
        Assert.True(PacketIntegrity.HasFoldHash(400));
        Assert.False(PacketIntegrity.HasFoldHash(401));
        Assert.False(PacketIntegrity.HasFoldHash(-1));
        Assert.Equal(400, PacketIntegrity.FoldHashMaxPayload);
    }

    [Fact]
    public void FlippingAnyPayloadBitChangesTheHash()
    {
        byte[] data = new byte[40];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 11 + 3);
        }
        uint original = PacketIntegrity.FoldHash(data);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= 0x01;
            Assert.NotEqual(original, PacketIntegrity.FoldHash(data));
            data[i] ^= 0x01;
        }
        Assert.Equal(original, PacketIntegrity.FoldHash(data));
    }
}
