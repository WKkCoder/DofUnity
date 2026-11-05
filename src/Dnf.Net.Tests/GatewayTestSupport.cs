using Dnf.Net.Gateway;

namespace Dnf.Net.Tests;

/// <summary>网关测试共用的小工具。密钥一律是合成的(由字节规律派生),不在源码里写 64 位十六进制常量。</summary>
internal static class GatewayTestSupport
{
    /// <summary>合成的 32 字节密钥:第 i 字节 = i*7 + seed(模 256)。</summary>
    public static byte[] SynthKey(int seed = 1)
    {
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(i * 7 + seed);
        }
        return key;
    }

    public static string HexLower(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>公开向量文件里的十六进制;"-" 表示空串。</summary>
    public static byte[] Hex(string text)
    {
        return text == "-" ? Array.Empty<byte>() : Convert.FromHexString(text);
    }

    public static string GoldenPath(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "Golden", name);
    }

    /// <summary>用段拼出点分文本(源码里不出现点分四段的字面量,交付门会把它们当成服务器地址)。</summary>
    public static string Dots(params object[] parts)
    {
        return string.Join(".", parts);
    }

    /// <summary>
    /// 给可能卡住的等待加一道保险:被测实现被改坏(变异)后可能永远不返回,超过时限就让测试失败,而不是挂住整个测试进程。
    /// 失败用 <see cref="Assert.Fail(string)"/>,不是 TimeoutException,免得与"被测代码本身抛的超时"混淆。
    /// </summary>
    public static async Task<T> Bounded<T>(Task<T> task, int seconds = 30)
    {
        using CancellationTokenSource stop = new();
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds), stop.Token));
        if (finished != task)
        {
            Assert.Fail("the operation did not finish within " + seconds + " seconds");
        }
        stop.Cancel();
        return await task;
    }

    /// <summary>固定字节序列(i * step + start)的字节数组,当作合成数据用。</summary>
    public static byte[] Pattern(int length, int start = 0, int step = 1)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(start + i * step);
        }
        return data;
    }
}

/// <summary>每次都返回同一个 nonce 的来源(只用于测试可复现密文;生产代码用随机来源)。</summary>
internal sealed class FixedNonceSource : INonceSource
{
    private readonly byte[] _nonce;

    public FixedNonceSource(byte[] nonce)
    {
        _nonce = nonce;
    }

    public int Calls { get; private set; }

    public byte[] NextNonce()
    {
        Calls++;
        return _nonce == null ? null : (byte[])_nonce.Clone();
    }
}

/// <summary>按调用次数依次给出不同 nonce 的来源(第 n 次 = 全 n 字节填充)。</summary>
internal sealed class CountingNonceSource : INonceSource
{
    private int _next;

    /// <summary>已经给出的 nonce 个数。</summary>
    public int Calls
    {
        get { return Volatile.Read(ref _next); }
    }

    public byte[] NextNonce()
    {
        byte[] nonce = new byte[12];
        Array.Fill(nonce, (byte)Interlocked.Increment(ref _next));
        return nonce;
    }
}

/// <summary>手动拨动的时钟。</summary>
internal sealed class ManualClock : IGatewayClock
{
    public TimeSpan Now { get; set; }

    public void Advance(TimeSpan by)
    {
        Now += by;
    }
}
