using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>
/// 启动包的固定种子变换:逐字节 ROR8(x ^ 0xB5, 6)。期望值来自服务端原函数在模拟器里的结果(frame_bootstrap_vector.txt)与手算的位运算。
/// </summary>
public class FrameBootstrapTests
{
    [Fact]
    public void Vectors_replay_for_both_directions_including_nonzero_seed()
    {
        int zeroSeed = 0, otherSeed = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_bootstrap_vector.txt"))
        {
            if (r[0] == "seedrand")
            {
                continue;
            }
            // kind seed_in seed_out len input_hex output_hex
            uint seedIn = uint.Parse(r[1]);
            uint seedOut = uint.Parse(r[2]);
            byte[] input = FrameVectorFile.Hex(r[4]);
            byte[] expected = FrameVectorFile.Hex(r[5]);
            Assert.Equal(int.Parse(r[3]), input.Length);
            byte[] data = (byte[])input.Clone();
            uint seed = seedIn;
            if (r[0] == "enc")
            {
                BootstrapTransform.Encode(data, ref seed);
            }
            else
            {
                Assert.Equal("dec", r[0]);
                BootstrapTransform.Decode(data, ref seed);
            }
            Assert.Equal(expected, data);
            Assert.Equal(seedOut, seed);
            if (seedIn == 0)
            {
                zeroSeed++;
            }
            else
            {
                otherSeed++;
            }
        }
        Assert.Equal(42, zeroSeed);
        Assert.Equal(64, otherSeed);
    }

    [Fact]
    public void SeedRand_vectors()
    {
        int rows = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_bootstrap_vector.txt"))
        {
            if (r[0] != "seedrand")
            {
                continue;
            }
            uint seed = uint.Parse(r[1]);
            uint result = BootstrapTransform.SeedRand(ref seed);
            Assert.Equal(uint.Parse(r[2]), result);
            Assert.Equal(uint.Parse(r[3]), seed);
            rows++;
        }
        Assert.Equal(11, rows);
    }

    [Theory]
    [InlineData(0x00, 0xD6)] // 0x00 ^ 0xB5 = 1011_0101,循环右移 6 位(即循环左移 2 位)= 1101_0110
    [InlineData(0xB5, 0x00)] // 异或后为 0
    [InlineData(0xFF, 0x29)] // 0xFF ^ 0xB5 = 0100_1010,循环左移 2 位 = 0010_1001
    [InlineData(0x01, 0xD2)] // 0x01 ^ 0xB5 = 1011_0100,循环左移 2 位 = 1101_0010
    [InlineData(0x80, 0xD4)] // 0x80 ^ 0xB5 = 0011_0101,循环左移 2 位 = 1101_0100
    public void Single_byte_hand_computed(int plain, int cipher)
    {
        Assert.Equal((byte)cipher, BootstrapTransform.EncodeByte((byte)plain));
        Assert.Equal((byte)plain, BootstrapTransform.DecodeByte((byte)cipher));
    }

    [Fact]
    public void Wire_path_is_a_permutation_of_all_256_values_and_decode_inverts_encode()
    {
        HashSet<byte> seen = new();
        for (int v = 0; v < 256; v++)
        {
            byte e = BootstrapTransform.EncodeByte((byte)v);
            seen.Add(e);
            Assert.Equal((byte)v, BootstrapTransform.DecodeByte(e));
        }
        Assert.Equal(256, seen.Count);
    }

    [Fact]
    public void Array_and_span_forms_agree_and_preserve_length()
    {
        byte[] data = Enumerable.Range(0, 300).Select(i => (byte)(i * 31 + 7)).ToArray();
        byte[] encoded = BootstrapTransform.Encode(data);
        Assert.Equal(data.Length, encoded.Length);
        Assert.Equal(data.Select(BootstrapTransform.EncodeByte), encoded);
        byte[] inPlace = (byte[])data.Clone();
        BootstrapTransform.Encode(inPlace.AsSpan());
        Assert.Equal(encoded, inPlace);
        Assert.Equal(data, BootstrapTransform.Decode(encoded));
        BootstrapTransform.Decode(inPlace.AsSpan());
        Assert.Equal(data, inPlace);
        // 数组形式不改输入
        Assert.Equal(Enumerable.Range(0, 300).Select(i => (byte)(i * 31 + 7)), data);
        Assert.Empty(BootstrapTransform.Encode(Array.Empty<byte>()));
        Assert.Empty(BootstrapTransform.Decode(Array.Empty<byte>()));
        Assert.Throws<ArgumentNullException>(() => BootstrapTransform.Encode((byte[])null));
        Assert.Throws<ArgumentNullException>(() => BootstrapTransform.Decode((byte[])null));
    }

    [Fact]
    public void Seed_zero_stays_zero_and_nonzero_seed_advances_once_per_call_regardless_of_length()
    {
        uint zero = 0;
        BootstrapTransform.Encode(new byte[100], ref zero);
        Assert.Equal(0u, zero);
        BootstrapTransform.Decode(new byte[0], ref zero);
        Assert.Equal(0u, zero);

        uint a = 12345, b = 12345;
        BootstrapTransform.Encode(new byte[1], ref a);
        BootstrapTransform.Encode(new byte[500], ref b);
        Assert.Equal(a, b);
        Assert.NotEqual(12345u, a);
        uint c = 12345;
        BootstrapTransform.Encode(new byte[0], ref c);
        Assert.Equal(a, c);
    }

    [Fact]
    public void Derive_for_zero_seed_is_fixed_key_and_rotation()
    {
        uint seed = 0;
        BootstrapTransform.Derive(ref seed, out byte key, out int rotation);
        Assert.Equal((byte)0xB5, key);
        Assert.Equal(6, rotation);
        Assert.Equal(0x04453EB5u, BootstrapTransform.FixedState);
        // 状态字 0x04453EB5:最低字节 0xB5,第 2 个字节 0x3E,与 7 相与得 6
        Assert.Equal(0xB5, BootstrapTransform.FixedKey);
        Assert.Equal(6, BootstrapTransform.FixedRotation);
    }
}
