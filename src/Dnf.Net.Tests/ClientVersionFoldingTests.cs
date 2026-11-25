using System.Numerics;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 客户端版本号的折算算式(台账 E-451):四段 16 位文件版本数折成登录请求里的 u32,运算与客户端一样是 32 位、超出时回绕。
/// 期望值要么是手算的字面量,要么来自用大整数按定义逐步算的独立参考实现(不走被测代码的 32 位运算路径);
/// 所有版本数都是随手写的合成假数,不含任何真实客户端的取值。
/// </summary>
public sealed class ClientVersionFoldingTests
{
    // 独立参考:大整数逐步按定义算(不回绕),调用方再按需要对 2^32 取模
    private static BigInteger Reference(int a, int b, int c, int d)
    {
        BigInteger x = new BigInteger(a) * 100 + b;
        x = x * 10 + c / 100;
        x = x * 100 + c % 100;
        x = x * 100 + d;
        return x;
    }

    [Theory]
    [InlineData(1, 2, 345, 6, 10234506u)]        // ((((1×100+2)×10+3)×100+45)×100+6)
    [InlineData(3, 20, 105, 7, 32010507u)]       // 320 → 3201 → 320105 → 32010507
    [InlineData(0, 0, 0, 0, 0u)]
    [InlineData(0, 0, 0, 7, 7u)]
    [InlineData(0, 0, 5, 0, 500u)]               // C 不满 100:⌊C÷100⌋ 为 0,余数就是 C
    [InlineData(0, 0, 100, 0, 10000u)]           // C 正好 100:⌊C÷100⌋ 为 1,余数 0
    [InlineData(0, 0, 999, 0, 99900u)]
    [InlineData(0, 1, 0, 0, 100000u)]
    [InlineData(9, 99, 999, 99, 99999999u)]
    public void Fold_follows_the_documented_arithmetic(int a, int b, int c, int d, uint expected)
    {
        Assert.Equal(expected, ClientVersionFolding.Fold((ushort)a, (ushort)b, (ushort)c, (ushort)d));
        Assert.True(ClientVersionFolding.IsExact((ushort)a, (ushort)b, (ushort)c, (ushort)d));
    }

    [Fact]
    public void Every_segment_changes_the_result_and_no_two_segments_are_interchangeable()
    {
        const ushort a = 3, b = 20, c = 105, d = 7;
        uint baseline = ClientVersionFolding.Fold(a, b, c, d);
        var variants = new Dictionary<string, uint>
        {
            ["a+1"] = ClientVersionFolding.Fold(a + 1, b, c, d),
            ["b+1"] = ClientVersionFolding.Fold(a, b + 1, c, d),
            ["c+1"] = ClientVersionFolding.Fold(a, b, c + 1, d),
            ["c+100"] = ClientVersionFolding.Fold(a, b, c + 100, d),     // 只动 ⌊C÷100⌋ 那一位
            ["d+1"] = ClientVersionFolding.Fold(a, b, c, d + 1),
            ["swap a b"] = ClientVersionFolding.Fold(b, a, c, d),
            ["swap c d"] = ClientVersionFolding.Fold(a, b, d, c),
        };
        foreach (KeyValuePair<string, uint> variant in variants)
        {
            Assert.NotEqual(baseline, variant.Value);
        }
        Assert.Equal(variants.Count, variants.Values.Distinct().Count());

        // 每一段的步长各不相同(没有进位时):D 加 1 增 1,C 加 1 增 100(余数那一位),C 加 100 增 10000(⌊C÷100⌋ 那一位),B 加 1 增 100000,A 加 1 增 10000000
        Assert.Equal(baseline + 1u, variants["d+1"]);
        Assert.Equal(baseline + 100u, variants["c+1"]);
        Assert.Equal(baseline + 10000u, variants["c+100"]);
        Assert.Equal(baseline + 100000u, variants["b+1"]);
        Assert.Equal(baseline + 10000000u, variants["a+1"]);
    }

    [Fact]
    public void The_largest_exact_value_and_the_first_wrapped_one_sit_on_either_side_of_the_32_bit_limit()
    {
        // 4294967295 = ((((429×100+49)×10+6)×100+72)×100+95);再大 1 就回绕成 0
        Assert.Equal(uint.MaxValue, ClientVersionFolding.Fold(429, 49, 672, 95));
        Assert.True(ClientVersionFolding.IsExact(429, 49, 672, 95));
        Assert.Equal(0u, ClientVersionFolding.Fold(429, 49, 672, 96));
        Assert.False(ClientVersionFolding.IsExact(429, 49, 672, 96));
        Assert.Equal(1u, ClientVersionFolding.Fold(429, 49, 672, 97));
        Assert.False(ClientVersionFolding.IsExact(429, 49, 672, 97));
    }

    [Fact]
    public void Fold_and_is_exact_agree_with_an_independent_big_integer_reference_over_many_values()
    {
        var random = new Random(20260401);
        BigInteger modulus = BigInteger.Pow(2, 32);
        for (int i = 0; i < 30000; i++)
        {
            // 一半取现实里会有的小数,一半取整个 16 位范围(含一定会回绕的)
            bool small = (i & 1) == 0;
            int a = random.Next(small ? 50 : 65536);
            int b = random.Next(small ? 300 : 65536);
            int c = random.Next(small ? 1500 : 65536);
            int d = random.Next(small ? 100 : 65536);
            BigInteger exact = Reference(a, b, c, d);

            Assert.Equal((uint)(exact % modulus), ClientVersionFolding.Fold((ushort)a, (ushort)b, (ushort)c, (ushort)d));
            Assert.Equal(exact < modulus, ClientVersionFolding.IsExact((ushort)a, (ushort)b, (ushort)c, (ushort)d));
        }
    }

    [Fact]
    public void Every_value_in_a_dense_band_around_the_32_bit_limit_agrees_with_the_reference()
    {
        // 随机取值几乎碰不到 32 位上限附近(那里 A 约 429、B 约 49、C 约 600):这一带逐个核对,
        // C 取 580 到 640 每个整数(⌊C÷100⌋ 在 5 与 6 之间换挡的地方),D 取小数、刚好够与刚好超出的几个值
        BigInteger modulus = BigInteger.Pow(2, 32);
        int checkedCount = 0;
        foreach (int a in new[] { 428, 429, 430 })
        {
            foreach (int b in new[] { 48, 49, 50, 99 })
            {
                for (int c = 580; c <= 640; c++)
                {
                    foreach (int d in new[] { 0, 1, 95, 96, 99, 7295, 7296, 8000, 65535 })
                    {
                        BigInteger exact = Reference(a, b, c, d);
                        Assert.Equal((uint)(exact % modulus), ClientVersionFolding.Fold((ushort)a, (ushort)b, (ushort)c, (ushort)d));
                        Assert.Equal(exact < modulus, ClientVersionFolding.IsExact((ushort)a, (ushort)b, (ushort)c, (ushort)d));
                        checkedCount++;
                    }
                }
            }
        }
        Assert.Equal(3 * 4 * 61 * 9, checkedCount);
    }

    [Fact]
    public void The_extreme_values_wrap_instead_of_throwing()
    {
        // 全 65535:折出的数远超 32 位;只要求按 32 位回绕、不抛异常(项目开了溢出检查的构建也一样)
        uint folded = ClientVersionFolding.Fold(ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue);
        Assert.Equal((uint)(Reference(65535, 65535, 65535, 65535) % BigInteger.Pow(2, 32)), folded);
        Assert.False(ClientVersionFolding.IsExact(ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue));
    }

    [Fact]
    public void The_file_version_value_object_labels_folds_and_reports_exactness_consistently()
    {
        var version = new ClientFileVersion(3, 20, 105, 7);
        Assert.Equal((ushort)3, version.A);
        Assert.Equal((ushort)20, version.B);
        Assert.Equal((ushort)105, version.C);
        Assert.Equal((ushort)7, version.D);
        Assert.Equal("3.20.105.7", version.ToString());
        Assert.Equal(32010507u, version.Fold());
        Assert.True(version.FoldsExactly());

        var wrapping = new ClientFileVersion(429, 49, 672, 96);
        Assert.Equal("429.49.672.96", wrapping.ToString());
        Assert.Equal(0u, wrapping.Fold());
        Assert.False(wrapping.FoldsExactly());

        Assert.Equal("0.0.0.0", default(ClientFileVersion).ToString());
        Assert.Equal("65535.65535.65535.65535", new ClientFileVersion(ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue).ToString());
    }
}
