using System.Globalization;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// 客户端"两个对象之间的角度与距离"整数算法(E-169):手算的锚点(横竖线、四个斜向、向零截断、同点、模式 0 的纵深还原),
/// 期望值与客户端函数 0x007fa2c0 / 0x007fb7c0 在模拟器里的输出一致(见 <see cref="ActGeometryGoldenTests"/>)。
/// </summary>
public sealed class ActGeometryTests
{
    [Theory]
    [InlineData(100, 0, 0)]
    [InlineData(0, 100, 90)]
    [InlineData(-100, 0, 180)]
    [InlineData(0, -100, 270)]
    [InlineData(100, 100, 45)]
    [InlineData(-100, 100, 135)]
    [InlineData(-100, -100, 225)]
    [InlineData(100, -100, 315)]
    [InlineData(100, 30, 16)]
    [InlineData(100, -30, 344)]
    [InlineData(-100, -30, 197)]
    [InlineData(-100, 30, 163)]
    [InlineData(0, 0, 0)]
    public void HeadingWithoutDepthRestoreIsADegreeAngleTruncatedTowardZero(int dx, int dy, int expected)
    {
        Assert.Equal(expected, ActGeometry.Heading(10, 20, 10 + dx, 20 + dy, false));
    }

    [Theory]
    [InlineData(100, 100, 73)]
    [InlineData(-100, 100, 106)]
    [InlineData(-100, -100, 254)]
    [InlineData(100, -100, 287)]
    [InlineData(100, 29, 45)]
    [InlineData(100, 0, 0)]
    public void HeadingWithDepthRestoreStretchesTheDepthDifferenceBy100Over29(int dx, int dy, int expected)
    {
        Assert.Equal(expected, ActGeometry.Heading(10, 20, 10 + dx, 20 + dy, true));
    }

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(0, 29, 29)]
    [InlineData(3, 4, 5)]
    [InlineData(1, 1, 1)]
    [InlineData(100, 100, 141)]
    [InlineData(-300, 0, 300)]
    [InlineData(0, -29, 29)]
    public void DistanceWithoutDepthRestoreIsATruncatedEuclideanDistance(int dx, int dy, int expected)
    {
        Assert.Equal(expected, ActGeometry.Distance(10, 20, 10 + dx, 20 + dy, false));
    }

    [Theory]
    [InlineData(0, 29, 100)]
    [InlineData(0, -29, 100)]
    [InlineData(0, 100, 344)]
    [InlineData(3, 4, 13)]
    [InlineData(1, 1, 3)]
    [InlineData(100, 100, 358)]
    [InlineData(300, 0, 300)]
    public void DistanceWithDepthRestoreDividesTheDepthDifferenceBy029BeforeSquaring(int dx, int dy, int expected)
    {
        Assert.Equal(expected, ActGeometry.Distance(10, 20, 10 + dx, 20 + dy, true));
    }

    [Fact]
    public void DistanceDoesNotCareWhichEndIsWhich()
    {
        var rng = new Random(7);
        for (int i = 0; i < 400; i++)
        {
            int x1 = rng.Next(-2000, 2000), y1 = rng.Next(-800, 800), x2 = rng.Next(-2000, 2000), y2 = rng.Next(-800, 800);
            Assert.Equal(ActGeometry.Distance(x1, y1, x2, y2, false), ActGeometry.Distance(x2, y2, x1, y1, false));
            Assert.Equal(ActGeometry.Distance(x1, y1, x2, y2, true), ActGeometry.Distance(x2, y2, x1, y1, true));
        }
    }

    [Fact]
    public void ARightwardWalkAtSpeedNIsNInX()
    {
        ActGeometry.Velocity(0, 600f, out int vx, out int vy);
        Assert.Equal((600, 0), (vx, vy));
        ActGeometry.Velocity(180, 600f, out vx, out vy);
        Assert.Equal((-600, 0), (vx, vy));
    }

    [Fact]
    public void TinyTrigResiduesTruncateToZeroNotOne()
    {
        // 90 度的余弦是 6e-17 量级、180 度的正弦是 1e-16 量级,落到单精度乘速度再向零截断都是 0
        ActGeometry.Velocity(90, 600f, out int vx, out int vy);
        Assert.Equal((0, 600), (vx, vy));
        ActGeometry.Velocity(270, 600f, out vx, out vy);
        Assert.Equal((0, -600), (vx, vy));
    }

    [Fact]
    public void ADiagonalWalkTruncatesEachComponentTowardZero()
    {
        ActGeometry.Velocity(45, 100f, out int vx, out int vy);
        Assert.Equal((70, 70), (vx, vy));
        ActGeometry.Velocity(225, 100f, out vx, out vy);
        Assert.Equal((-70, -70), (vx, vy));
    }
}

/// <summary>
/// 与客户端原版函数的逐位对拍(证据 E-169):Golden/act_geometry_golden.txt 里是全合成的坐标,期望值是客户端的角度函数 0x007fa2c0、
/// 距离函数 0x007fb7c0 与 CRT 正余弦 0x013db30f / 0x013d848b 在模拟器里跑出来的(tools/py/geometry_oracle.py)。文件不含任何原版数据;换了机器、没有模拟器也能回归。
/// </summary>
public sealed class ActGeometryGoldenTests
{
    private static string[][] Rows(string kind)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Golden", "act_geometry_golden.txt");
        return File.ReadAllLines(path)
            .Where(l => l.StartsWith(kind + "\t", StringComparison.Ordinal))
            .Select(l => l.Split('\t'))
            .ToArray();
    }

    private static int N(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    [Fact]
    public void AnglesMatchTheClientFunction()
    {
        string[][] rows = Rows("angle");
        Assert.True(rows.Length >= 800);
        var bad = new List<string>();
        foreach (string[] r in rows)
        {
            int got = ActGeometry.Heading(N(r[1]), N(r[2]), N(r[3]), N(r[4]), N(r[5]) == 0);
            if (got != N(r[6]))
            {
                bad.Add(string.Join(",", r.Skip(1)) + " got " + got);
            }
        }
        Assert.True(bad.Count == 0, bad.Count + " 组角度对不上,头几组:" + string.Join(" | ", bad.Take(5)));
    }

    [Fact]
    public void DistancesMatchTheClientFunction()
    {
        string[][] rows = Rows("dist");
        Assert.True(rows.Length >= 800);
        var bad = new List<string>();
        foreach (string[] r in rows)
        {
            int got = ActGeometry.Distance(N(r[1]), N(r[2]), N(r[3]), N(r[4]), N(r[5]) == 0);
            if (got != N(r[6]))
            {
                bad.Add(string.Join(",", r.Skip(1)) + " got " + got);
            }
        }
        Assert.True(bad.Count == 0, bad.Count + " 组距离对不上,头几组:" + string.Join(" | ", bad.Take(5)));
    }

    /// <summary>每一整数度:.NET 的余弦与正弦落到单精度后与客户端 CRT 的单精度结果逐位相同,所以"乘速度再截断"的整数速度向量不会差一。</summary>
    [Fact]
    public void CosineAndSineMatchTheClientAtEveryWholeDegreeInSinglePrecision()
    {
        string[][] rows = Rows("trig");
        Assert.Equal(360, rows.Length);
        var bad = new List<string>();
        foreach (string[] r in rows)
        {
            int degrees = N(r[1]);
            double radians = (double)(float)degrees * 0.017453292519943295;
            float clientCos = (float)BitConverter.Int64BitsToDouble(long.Parse(r[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            float clientSin = (float)BitConverter.Int64BitsToDouble(long.Parse(r[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            if (BitConverter.SingleToInt32Bits((float)Math.Cos(radians)) != BitConverter.SingleToInt32Bits(clientCos)
                || BitConverter.SingleToInt32Bits((float)Math.Sin(radians)) != BitConverter.SingleToInt32Bits(clientSin))
            {
                bad.Add(degrees.ToString(CultureInfo.InvariantCulture));
            }
        }
        Assert.True(bad.Count == 0, "单精度对不上的度数:" + string.Join(",", bad.Take(20)));
    }

    /// <summary>速度向量在 0 到 359 度、几种速度上与"用客户端的余弦正弦手算"一致。</summary>
    [Fact]
    public void VelocityVectorsAgreeWithTheClientTrigAtEveryDegree()
    {
        string[][] rows = Rows("trig");
        foreach (float speed in new[] { 1f, 37f, 100f, 600f, 1500f })
        {
            foreach (string[] r in rows)
            {
                int degrees = N(r[1]);
                float clientCos = (float)BitConverter.Int64BitsToDouble(long.Parse(r[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                float clientSin = (float)BitConverter.Int64BitsToDouble(long.Parse(r[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                ActGeometry.Velocity(degrees, speed, out int vx, out int vy);
                Assert.Equal((int)(clientCos * speed), vx);
                Assert.Equal((int)(clientSin * speed), vy);
            }
        }
    }
}
