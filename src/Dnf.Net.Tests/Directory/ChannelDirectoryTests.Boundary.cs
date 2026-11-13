using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.ChannelDirectory;

namespace Dnf.Net.Tests;

/// <summary>
/// 验收后补的边界测试(杀掉集成者抽查里存活的单点变异):线上数值表、拥挤度分界、组内最大负载聚合、
/// 握手与版本检查的严格性、解密与帧的边界、脚本解析边界、版本串长度上限。
/// <para>
/// 约定:协议数值(包号、字段偏移、阈值)一律用字面数字钉死,不引用产品常量去造期望值(常量被改了测试也跟着变,正是变异存活的原因);
/// 所有等假服务端/假流的地方都带 5 秒取消令牌,回归的客户端表现为断言失败而不是挂起;全部是合成数据(RFC5737 192.0.2.x、900+ 编号、合成标签)。
/// </para>
/// </summary>
public sealed partial class ChannelDirectoryTests
{
    // 摘要钉住预共享值,避免字面量扩散:测试里不重复写初始密钥与问候明文本身,只比对 SHA-256 摘要与长度。
    private const string InitialAesKeySha256 = "b3f38d08002a604cb34ed861ad1783f8aa9dccc2fa6603842beab8d352adc1bc";
    private const string GreetingPlaintextSha256 = "53149289c602b2605c97eb66a8238bf8f74bccfb9f1ae548cf3ace285bef7116";

    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(5);

    private static string Sha256Hex(byte[] data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    // ------------------------------------------------------------------ 助手:脚本化假服务端

    /// <summary>
    /// 脚本化假服务端:按测试给的剧本读请求帧、回应答帧。所有读写都带同一个 5 秒取消令牌;
    /// 客户端回归成"多发/少发"时,剧本里的断言先失败,而不是卡在等待上。
    /// </summary>
    private sealed class ScriptedServer
    {
        private readonly Stream _stream;
        private readonly CancellationToken _ct;

        public ScriptedServer(Stream stream, CancellationToken ct)
        {
            _stream = stream;
            _ct = ct;
        }

        /// <summary>服务端按顺序收到的请求命令号。</summary>
        public List<byte> CommandsSeen { get; } = new List<byte>();

        /// <summary>读一帧请求并核对命令号(读不到帧 = 客户端没按预期发包)。</summary>
        public async Task<ChannelServiceFrame> ReadRequestAsync(byte expectedCommand)
        {
            ChannelServiceFrame frame = await ChannelServiceFrameCodec.ReadFrameAsync(_stream, cancellationToken: _ct);
            Assert.NotNull(frame);
            CommandsSeen.Add(frame.Header.Command);
            Assert.Equal(expectedCommand, frame.Header.Command);
            return frame;
        }

        /// <summary>客户端拒绝后必须收手:这里读到的只能是流结束(帧读取器返回 null)。</summary>
        public async Task ExpectNoFurtherRequestAsync()
        {
            ChannelServiceFrame extra = await ChannelServiceFrameCodec.ReadFrameAsync(_stream, cancellationToken: _ct);
            if (extra != null)
            {
                CommandsSeen.Add(extra.Header.Command);
                Assert.Fail("客户端本该在这里收手,却又发来了命令 0x" + extra.Header.Command.ToString("x2"));
            }
        }

        public async Task SendFrameAsync(byte command, byte[] body, byte flag = 1)
        {
            byte[] packet = ChannelServiceFrameCodec.Build(command, body, flag: flag);
            await _stream.WriteAsync(packet, 0, packet.Length, _ct);
            await _stream.FlushAsync(_ct);
        }

        /// <summary>0x0b → 0x0c:收问候帧(只核对体长 32),回状态 0 + 32 字节密钥材料(前 16 字节是会话密钥)。</summary>
        public async Task NegotiateSessionKeyAsync()
        {
            ChannelServiceFrame hello = await ReadRequestAsync(0x0b);
            Assert.Equal(32, hello.Body.Length);
            byte[] reply = new byte[36];
            Buffer.BlockCopy(SyntheticKey32(), 0, reply, 4, 32);
            await SendFrameAsync(0x0c, reply);
        }

        /// <summary>0x05:收版本检查请求,用会话密钥解开 16 字节版本块,返回截到首个 NUL 的字符串。</summary>
        public async Task<string> ReadVersionRequestAsync()
        {
            ChannelServiceFrame frame = await ReadRequestAsync(0x05);
            Assert.Equal(16, frame.Body.Length);
            Assert.True(ChannelServiceCrypto.TryDecryptAes128Ecb(SyntheticKey16(), frame.Body, out byte[] plain));
            return ChannelInfoBinaryParser.ReadFixedString(plain, 0, 16);
        }

        /// <summary>用会话密钥加密(零填充)后作为应答体发出。</summary>
        public Task SendEncryptedAsync(byte command, byte[] plain, int minimumPaddedLength = 0)
        {
            return SendFrameAsync(command, ChannelServiceCrypto.EncryptAes128EcbZeroPadded(SyntheticKey16(), plain, minimumPaddedLength));
        }

        /// <summary>用会话密钥加密后再 zlib 压缩作为应答体发出(0x09 / 0x03 的载荷形态)。</summary>
        public Task SendCompressedEncryptedAsync(byte command, byte[] plain)
        {
            byte[] cipher = ChannelServiceCrypto.EncryptAes128EcbZeroPadded(SyntheticKey16(), plain);
            return SendFrameAsync(command, ZlibCompress(cipher));
        }
    }

    /// <summary>
    /// 把脚本化假服务端与客户端接在一对阻塞管道上跑一遍,返回客户端结局与服务端收到的命令号序列。
    /// 客户端 5 秒内没有结局 = 断言失败(不是挂起);假服务端剧本里的断言失败会原样冒出来。
    /// </summary>
    private static async Task<(ChannelDirectoryLoadResult Result, byte[] Commands)> RunScenarioAsync(
        Func<ScriptedServer, Task> serverScript,
        string cachedVersion = null,
        ChannelScriptData fallbackScript = null)
    {
        var (clientStream, serverStream) = BlockingDuplexStream.CreatePair();
        using var cts = new CancellationTokenSource(GuardTimeout);
        var server = new ScriptedServer(serverStream, cts.Token);
        Task serverTask = Task.Run(async () =>
        {
            await using var _s = serverStream;
            await serverScript(server);
        });

        ChannelDirectoryLoadResult result;
        try
        {
            await using (clientStream)
            {
                result = await ChannelDirectoryClient.LoadFromStreamAsync(clientStream, cachedVersion, fallbackScript, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 假服务端剧本自己的断言失败更有信息量:先让它冒出来
            if (serverTask.IsFaulted)
            {
                await serverTask;
            }
            Assert.Fail("客户端在 5 秒内没有得到结局(等不到数据)");
            throw;
        }
        await serverTask.WaitAsync(GuardTimeout);
        return (result, server.CommandsSeen.ToArray());
    }

    private static ChannelWireGroup WireGroup(string tag, params (string Name, int Max, int Load, string Host, int Port)[] channels)
    {
        var list = new List<ChannelWireEntry>();
        foreach (var c in channels)
        {
            list.Add(new ChannelWireEntry(c.Name, c.Max, c.Load, c.Host, c.Port));
        }
        return new ChannelWireGroup(tag, list);
    }

    private static byte[] OneChannelList()
    {
        return BuildBinaryChannelList(new[]
        {
            ("srv900", new[] { ("zone900_01", 200, 10, "192.0.2.10", 19001) }),
        });
    }

    /// <summary>
    /// 手工拼 zlib 容器:2 字节头 + 若干"存储块"(不压缩)+ 4 字节大端 Adler-32。
    /// 每块长度可控,所以压缩后的总长度能精确凑到目标值(总长 = 6 + 5 × 块数 + 原文长度)。
    /// </summary>
    private static byte[] ZlibStoredBlocks(byte[] raw, int blockSize)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x01);
        int pos = 0;
        do
        {
            int n = Math.Min(blockSize, raw.Length - pos);
            bool last = pos + n >= raw.Length;
            ms.WriteByte(last ? (byte)1 : (byte)0);
            ms.WriteByte((byte)(n & 0xff));
            ms.WriteByte((byte)((n >> 8) & 0xff));
            ms.WriteByte((byte)(~n & 0xff));
            ms.WriteByte((byte)((~n >> 8) & 0xff));
            ms.Write(raw, pos, n);
            pos += n;
        }
        while (pos < raw.Length);

        uint a = 1, b = 0;
        foreach (byte x in raw)
        {
            a = (a + x) % 65521u;
            b = (b + a) % 65521u;
        }
        uint adler = (b << 16) | a;
        ms.WriteByte((byte)(adler >> 24));
        ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));
        ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ A. 线上数值表(字面数字)

    [Fact]
    public void WireTable_ProtocolNumbersArePinnedByLiterals()
    {
        // 端口、帧头、记录与定长字段
        Assert.Equal(7001, ChannelServiceConstants.DefaultPort);
        Assert.Equal(11, ChannelServiceConstants.HeaderLength);
        Assert.Equal(48, ChannelServiceConstants.ChannelRecordLength);
        Assert.Equal(20, ChannelServiceConstants.GroupTagLength);
        Assert.Equal(20, ChannelServiceConstants.ChannelNameLength);
        Assert.Equal(16, ChannelServiceConstants.ChannelHostLength);
        Assert.Equal(16, ChannelServiceConstants.AesBlockSize);
        Assert.Equal(16, ChannelServiceConstants.AesKeySize);
        Assert.Equal(36, ChannelServiceConstants.KeyExchangeReplyBodyLength);
        Assert.Equal(256, ChannelServiceConstants.MaxVersionReplyBodyLength);
        // 上限 0x32000 = 204800
        Assert.Equal(0x32000, ChannelServiceConstants.MaxPayloadBytes);
        Assert.Equal(204800, ChannelServiceConstants.MaxPayloadBytes);

        // 命令号
        Assert.Equal((byte)0x01, ChannelServiceConstants.CmdChannelListRequest);
        Assert.Equal((byte)0x03, ChannelServiceConstants.CmdChannelListReply);
        Assert.Equal((byte)0x05, ChannelServiceConstants.CmdVersionCheckRequest);
        Assert.Equal((byte)0x06, ChannelServiceConstants.CmdVersionCheckReply);
        Assert.Equal((byte)0x09, ChannelServiceConstants.CmdScriptRequest);
        Assert.Equal((byte)0x0b, ChannelServiceConstants.CmdKeyExchangeRequest);
        Assert.Equal((byte)0x0c, ChannelServiceConstants.CmdKeyExchangeReply);

        // 帧头第 0 字节、发送标志、默认脚本版本
        Assert.Equal((byte)0x00, ChannelServiceConstants.ProtocolByte0);
        Assert.Equal((byte)0x01, ChannelServiceConstants.DefaultFlag);
        Assert.Equal("0", ChannelServiceConstants.DefaultScriptVersion);

        // 满载阈值默认 95、有效容量系数 0.7
        Assert.Equal(95.0f, ChannelServiceConstants.DefaultFullThresholdPercent);
        Assert.Equal(0.7f, ChannelServiceConstants.EffectiveCapacityFactor);
    }

    [Fact]
    public void WireTable_FrameHeaderLayoutFollowsLiteralOffsets()
    {
        // 帧头 11 字节:[0]=byte0 [1]=命令号 [2..5]=总长(含帧头,小端) [6..9]=保留(小端) [10]=flag
        byte[] body = { 0xa1, 0xa2, 0xa3 };
        byte[] wire = ChannelServiceFrameCodec.Build(0x0b, body, byte0: 0x00, reserved: 0x01020304u, flag: 0x01);
        Assert.Equal(new byte[] { 0x00, 0x0b, 14, 0, 0, 0, 0x04, 0x03, 0x02, 0x01, 0x01, 0xa1, 0xa2, 0xa3 }, wire);

        // 默认参数:byte0 = 0、保留 = 0、flag = 1;空载荷的总长就是 11
        Assert.Equal(new byte[] { 0x00, 0x09, 11, 0, 0, 0, 0, 0, 0, 0, 0x01 }, ChannelServiceFrameCodec.Build(0x09));

        // 读回:总长 14 → 体长 3;总长 11 → 体长 0;总长 10 → 无效(-1)
        Assert.True(ChannelServiceFrameCodec.TryReadHeader(wire, 0, wire.Length, out ChannelServiceFrameHeader header));
        Assert.Equal(3, header.BodyLength);
        Assert.Equal(0x01020304u, header.Reserved);
        Assert.Equal(0, new ChannelServiceFrameHeader(0, 0x01, 11u, 0u, 1).BodyLength);
        Assert.Equal(-1, new ChannelServiceFrameHeader(0, 0x01, 10u, 0u, 1).BodyLength);
    }

    [Fact]
    public void WireTable_ChannelRecordFieldsSitAtLiteralOffsets()
    {
        // 手工按字面偏移拼一份明文:u32 组数 + [组标签 20 字节 + u32 频道数 + 记录 × 48 字节] + 零填充到 16 的倍数
        // 记录内:+0 名称(20)  +20 最大容量(i32)  +24 当前负载(i32)  +28 主机(16)  +44 端口(i32)
        byte[] data = new byte[80];
        data[0] = 1;                                           // 1 组
        Encoding.ASCII.GetBytes("grp900").CopyTo(data, 4);     // 组标签,[4..23]
        data[24] = 1;                                          // 该组 1 个频道,[24..27]
        Encoding.ASCII.GetBytes("zone900_07").CopyTo(data, 28);          // 名称,[28..47]
        BitConverter.TryWriteBytes(data.AsSpan(48), 321);                // 最大容量,[48..51]
        BitConverter.TryWriteBytes(data.AsSpan(52), 123);                // 当前负载,[52..55]
        Encoding.ASCII.GetBytes("192.0.2.77").CopyTo(data, 56);          // 主机,[56..71]
        BitConverter.TryWriteBytes(data.AsSpan(72), 19077);              // 端口,[72..75]
        // [76..79] 零填充

        Assert.Equal(ChannelInfoParseStatus.Ok, ChannelInfoBinaryParser.TryParse(data, true, out List<ChannelWireGroup> groups));
        ChannelWireGroup group = Assert.Single(groups);
        Assert.Equal("grp900", group.Tag);
        ChannelWireEntry entry = Assert.Single(group.Channels);
        Assert.Equal("zone900_07", entry.Name);
        Assert.Equal(321, entry.MaxCapacity);
        Assert.Equal(123, entry.CurrentLoad);
        Assert.Equal("192.0.2.77", entry.Host);
        Assert.Equal(19077, entry.Port);
    }

    [Fact]
    public void WireTable_EntryFlagsKeepLiteralBitValues()
    {
        // 删掉 PVP 标记后其余成员的数值不变:Full=1、HasRecommendLevel=2、HasValidPort=8(1<<2 留空)
        Assert.Equal(0, (int)ChannelEntryFlags.None);
        Assert.Equal(1, (int)ChannelEntryFlags.Full);
        Assert.Equal(2, (int)ChannelEntryFlags.HasRecommendLevel);
        Assert.Equal(8, (int)ChannelEntryFlags.HasValidPort);
        Assert.Equal(0, (int)ChannelCongestionTier.Comfortable);
        Assert.Equal(1, (int)ChannelCongestionTier.Harmony);
        Assert.Equal(2, (int)ChannelCongestionTier.Normal);
        Assert.Equal(3, (int)ChannelCongestionTier.Crowded);
        Assert.Equal(4, (int)ChannelCongestionTier.Full);
    }

    [Fact]
    public void WireTable_DefaultFullThresholdIs95WhenNoScript()
    {
        // 没有脚本时:默认脚本、空脚本、没写 [channel rate full] 的脚本都按 95
        Assert.Equal(95.0f, ChannelScriptData.Default.FullThresholdPercent);
        Assert.Equal(95.0f, ChannelScriptParser.Parse(null).FullThresholdPercent);
        Assert.Equal(95.0f, ChannelScriptParser.Parse(string.Empty).FullThresholdPercent);
        Assert.Equal(95.0f, ChannelScriptParser.Parse("[channel] \"[sample_zone]\" 903 917\n").FullThresholdPercent);

        // 拥挤度计算不传阈值时也按 95:95% 不满载,96% 满载
        ChannelCongestionEvaluation at95 = ChannelCongestionEvaluator.Evaluate(95, 100, 95);
        Assert.False(at95.IsFull);
        ChannelCongestionEvaluation at96 = ChannelCongestionEvaluator.Evaluate(96, 100, 96);
        Assert.True(at96.IsFull);
        Assert.Equal(ChannelCongestionTier.Full, at96.Tier);

        // 快照层:没给脚本(null)时同样按默认 95
        ChannelDirectorySnapshot snap = ChannelDirectorySnapshot.Build("0", true, null, new[]
        {
            WireGroup("srv900",
                ("zone900_01", 100, 95, "192.0.2.10", 19001),
                ("zone900_02", 100, 96, "192.0.2.11", 19002)),
        });
        Assert.Equal(95.0f, snap.ScriptData.FullThresholdPercent);
        Assert.False(snap.Channels[0].Congestion.IsFull);
        Assert.Equal((ChannelEntryFlags)8, snap.Channels[0].Flags);
        Assert.True(snap.Channels[1].Congestion.IsFull);
        Assert.Equal((ChannelEntryFlags)(1 | 8), snap.Channels[1].Flags);
    }

    [Fact]
    public void Crypto_InitialKeyAndGreetingArePinnedBySha256Digest()
    {
        // 摘要钉住预共享值,避免字面量扩散:这里不再写一遍初始密钥与问候明文,只断言长度与 SHA-256 摘要。
        byte[] key = ChannelServiceConstants.CreateInitialAesKey();
        byte[] greeting = ChannelServiceConstants.CreateGreetingPlaintext();
        Assert.Equal(16, key.Length);
        Assert.Equal(32, greeting.Length);
        Assert.Equal(InitialAesKeySha256, Sha256Hex(key));
        Assert.Equal(GreetingPlaintextSha256, Sha256Hex(greeting));

        // 每次调用返回独立副本:调用方改写自己拿到的数组,不会污染后续握手
        key[0] ^= 0xff;
        greeting[0] ^= 0xff;
        Assert.Equal(InitialAesKeySha256, Sha256Hex(ChannelServiceConstants.CreateInitialAesKey()));
        Assert.Equal(GreetingPlaintextSha256, Sha256Hex(ChannelServiceConstants.CreateGreetingPlaintext()));
    }

    [Fact]
    public void Crypto_AesEcb_MatchesFips197AppendixC1Vector()
    {
        // FIPS-197 附录 C.1 公开向量(AES-128):密钥 000102…0f、明文 00112233…ff、密文 69c4e0d8…c55a
        byte[] key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        byte[] plain = Convert.FromHexString("00112233445566778899aabbccddeeff");
        byte[] expected = Convert.FromHexString("69c4e0d86a7b0430d8cdb78070b4c55a");

        // 加密(恰好一个块,不再追加填充块)
        Assert.Equal(expected, ChannelServiceCrypto.EncryptAes128EcbZeroPadded(key, plain));

        // 解密
        Assert.True(ChannelServiceCrypto.TryDecryptAes128Ecb(key, expected, out byte[] decrypted));
        Assert.Equal(plain, decrypted);

        // ECB:相同的明文块得到相同的密文块(CBC 不会这样)
        byte[] twoBlocks = new byte[32];
        Buffer.BlockCopy(plain, 0, twoBlocks, 0, 16);
        Buffer.BlockCopy(plain, 0, twoBlocks, 16, 16);
        byte[] twoCipher = ChannelServiceCrypto.EncryptAes128EcbZeroPadded(key, twoBlocks);
        Assert.Equal(32, twoCipher.Length);
        Assert.Equal(expected, twoCipher[..16]);
        Assert.Equal(expected, twoCipher[16..]);

        // 零填充:17 字节补到 32 字节,解密后前 17 字节是原文、后 15 字节全 0
        byte[] seventeen = new byte[17];
        for (int i = 0; i < 17; i++)
        {
            seventeen[i] = (byte)(0x40 + i);
        }
        byte[] padCipher = ChannelServiceCrypto.EncryptAes128EcbZeroPadded(key, seventeen);
        Assert.Equal(32, padCipher.Length);
        Assert.True(ChannelServiceCrypto.TryDecryptAes128Ecb(key, padCipher, out byte[] padPlain));
        Assert.Equal(seventeen, padPlain[..17]);
        Assert.Equal(new byte[15], padPlain[17..]);
    }

    // ------------------------------------------------------------------ B. 拥挤度分界

    [Theory]
    // 阈值 95:94、95 都不满载(严格大于才满载),96 起满载
    [InlineData(94, 100, 95.0f, false, 134)]
    [InlineData(95, 100, 95.0f, false, 135)]
    [InlineData(96, 100, 95.0f, true, 96)]
    // 阈值 90
    [InlineData(90, 100, 90.0f, false, 128)]
    [InlineData(91, 100, 90.0f, true, 91)]
    // 小数阈值:92.5% 恰好相等不满载,92.6% 满载
    [InlineData(925, 1000, 92.5f, false, 132)]
    [InlineData(926, 1000, 92.5f, true, 92)]
    // 阈值 100:负载恰好等于容量也不满载,超出一点才满载
    [InlineData(100, 100, 100.0f, false, 142)]
    [InlineData(101, 100, 100.0f, true, 101)]
    public void Congestion_FullNeedsRawPercentStrictlyAboveThreshold(int load, int max, float threshold, bool expectFull, int expectedPercent)
    {
        // 组内最大负载取自己的负载;不满载时有效分母 = min((int)(max*0.7f), load) = (int)(max*0.7f),pct 大于 100 → 拥挤
        ChannelCongestionEvaluation e = ChannelCongestionEvaluator.Evaluate(load, max, load, threshold);
        Assert.Equal(expectFull, e.IsFull);
        Assert.Equal(expectedPercent, e.EffectivePercent);
        Assert.Equal(expectFull ? ChannelCongestionTier.Full : ChannelCongestionTier.Crowded, e.Tier);
        Assert.Equal(expectFull ? 0xfff000ffu : 0xff0000ffu, e.ArgbColor);
    }

    [Theory]
    [InlineData(0, ChannelCongestionTier.Comfortable, 0xff00ff00u)]
    [InlineData(1, ChannelCongestionTier.Comfortable, 0xff00ff00u)]
    [InlineData(2, ChannelCongestionTier.Comfortable, 0xff00ff00u)]
    [InlineData(3, ChannelCongestionTier.Harmony, 0xff00b1ffu)]
    [InlineData(4, ChannelCongestionTier.Harmony, 0xff00b1ffu)]
    [InlineData(5, ChannelCongestionTier.Normal, 0xff0080ffu)]
    [InlineData(24, ChannelCongestionTier.Normal, 0xff0080ffu)]
    [InlineData(25, ChannelCongestionTier.Crowded, 0xff0000ffu)]
    [InlineData(26, ChannelCongestionTier.Crowded, 0xff0000ffu)]
    [InlineData(100, ChannelCongestionTier.Crowded, 0xff0000ffu)]
    public void Congestion_PercentBandsSplitAtLiteralBoundaries(int pct, ChannelCongestionTier expectedTier, uint expectedColor)
    {
        // 容量 1000、组内最大负载 100:有效分母 = min((int)(1000*0.7f) = 700, 100) = 100,于是 pct 恰等于负载;
        // 原始占比 = 负载/1000 ≤ 10%,远低于满载阈值
        ChannelCongestionEvaluation e = ChannelCongestionEvaluator.Evaluate(pct, 1000, 100, 95.0f);
        Assert.False(e.IsFull);
        Assert.Equal(pct, e.EffectivePercent);
        Assert.Equal(expectedTier, e.Tier);
        Assert.Equal(expectedColor, e.ArgbColor);
    }

    [Fact]
    public void Congestion_DegenerateInputsFallBackWithoutDividingByZero()
    {
        // 负载 ≤ 0:顺畅,0%
        foreach (int load in new[] { 0, -3 })
        {
            ChannelCongestionEvaluation e = ChannelCongestionEvaluator.Evaluate(load, 200, 50, 95.0f);
            Assert.Equal(ChannelCongestionTier.Comfortable, e.Tier);
            Assert.False(e.IsFull);
            Assert.Equal(0, e.EffectivePercent);
            Assert.Equal(0xff00ff00u, e.ArgbColor);
        }

        // 容量 ≤ 0 而负载 > 0:满载,100%
        foreach (int max in new[] { 0, -1 })
        {
            ChannelCongestionEvaluation e = ChannelCongestionEvaluator.Evaluate(5, max, 50, 95.0f);
            Assert.Equal(ChannelCongestionTier.Full, e.Tier);
            Assert.True(e.IsFull);
            Assert.Equal(100, e.EffectivePercent);
            Assert.Equal(0xfff000ffu, e.ArgbColor);
        }

        // 组内最大负载为 0 或负数:不封顶,有效分母 = (int)(200*0.7f) = 140 → 35*100/140 = 25 → 拥挤
        foreach (int groupMax in new[] { 0, -1 })
        {
            ChannelCongestionEvaluation e = ChannelCongestionEvaluator.Evaluate(35, 200, groupMax, 95.0f);
            Assert.False(e.IsFull);
            Assert.Equal(25, e.EffectivePercent);
            Assert.Equal(ChannelCongestionTier.Crowded, e.Tier);
        }

        // 组内最大负载刚好低于 140:有效分母取组内最大值(139):35*100/139 = 25.18 → 25
        ChannelCongestionEvaluation belowCap = ChannelCongestionEvaluator.Evaluate(35, 200, 139, 95.0f);
        Assert.Equal(25, belowCap.EffectivePercent);
        // 组内最大值 70:35*100/70 = 50
        Assert.Equal(50, ChannelCongestionEvaluator.Evaluate(35, 200, 70, 95.0f).EffectivePercent);

        // 有效分母算出 0(容量 1 → (int)(1*0.7f) = 0)且没有判满载(阈值给到 1000):拥挤,100%
        ChannelCongestionEvaluation zeroDenominator = ChannelCongestionEvaluator.Evaluate(1, 1, 1, 1000.0f);
        Assert.Equal(ChannelCongestionTier.Crowded, zeroDenominator.Tier);
        Assert.False(zeroDenominator.IsFull);
        Assert.Equal(100, zeroDenominator.EffectivePercent);
        Assert.Equal(0xff0000ffu, zeroDenominator.ArgbColor);

        // 有效容量系数 0.7:容量 1000 → 700;35*100/700 = 5(普通),34*100/700 = 4(良好)
        Assert.Equal(ChannelCongestionTier.Normal, ChannelCongestionEvaluator.Evaluate(35, 1000, 2000, 95.0f).Tier);
        Assert.Equal(ChannelCongestionTier.Harmony, ChannelCongestionEvaluator.Evaluate(34, 1000, 2000, 95.0f).Tier);
    }

    // ------------------------------------------------------------------ C. 组内最大负载聚合(快照层)

    [Fact]
    public void Snapshot_GroupMaxLoadCapsEffectiveCapacityPerGroup()
    {
        // 组 A:容量都是 1000,最大负载 100 排在中间 → 有效分母 min(700, 100) = 100,pct 恰等于负载
        ChannelWireGroup groupA = WireGroup("srv_a",
            ("a01", 1000, 1, "192.0.2.1", 19001),
            ("a02", 1000, 100, "192.0.2.2", 19002),
            ("a03", 1000, 3, "192.0.2.3", 19003),
            ("a04", 1000, 5, "192.0.2.4", 19004),
            ("a05", 1000, 24, "192.0.2.5", 19005),
            ("a06", 1000, 25, "192.0.2.6", 19006));
        // 组 B:最大负载 50 → 有效分母 50:7 → 14%(普通),50 → 100%(拥挤);组与组之间互不影响
        ChannelWireGroup groupB = WireGroup("srv_b",
            ("b01", 1000, 7, "192.0.2.7", 19007),
            ("b02", 1000, 50, "192.0.2.8", 19008));
        // 组 C:最大负载 69 略低于 (int)(100*0.7f) = 70 → 有效分母 69:10 → 14%,69 → 100%(若误按 70 算则是 98%)
        ChannelWireGroup groupC = WireGroup("srv_c",
            ("c01", 100, 10, "192.0.2.9", 19009),
            ("c02", 100, 69, "192.0.2.10", 19010));
        // 组 D:最大负载 80 高于 70 → 有效分母取容量一侧的 70:5 → 7%(普通),80 → 114%(拥挤)
        ChannelWireGroup groupD = WireGroup("srv_d",
            ("d01", 100, 5, "192.0.2.11", 19011),
            ("d02", 100, 80, "192.0.2.12", 19012));

        ChannelDirectorySnapshot snap = ChannelDirectorySnapshot.Build("0", true, ChannelScriptData.Default, new[] { groupA, groupB, groupC, groupD });

        Assert.Equal(4, snap.Groups.Count);
        Assert.Equal(100, snap.Groups[0].MaxCurrentLoad);
        Assert.Equal(50, snap.Groups[1].MaxCurrentLoad);
        Assert.Equal(69, snap.Groups[2].MaxCurrentLoad);
        Assert.Equal(80, snap.Groups[3].MaxCurrentLoad);

        (int Pct, ChannelCongestionTier Tier)[] expected =
        {
            (1, ChannelCongestionTier.Comfortable),
            (100, ChannelCongestionTier.Crowded),
            (3, ChannelCongestionTier.Harmony),
            (5, ChannelCongestionTier.Normal),
            (24, ChannelCongestionTier.Normal),
            (25, ChannelCongestionTier.Crowded),
            (14, ChannelCongestionTier.Normal),
            (100, ChannelCongestionTier.Crowded),
            (14, ChannelCongestionTier.Normal),
            (100, ChannelCongestionTier.Crowded),
            (7, ChannelCongestionTier.Normal),
            (114, ChannelCongestionTier.Crowded),
        };
        Assert.Equal(expected.Length, snap.Channels.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            ChannelEntry entry = snap.Channels[i];
            Assert.Equal(i + 1, entry.ChannelNo);
            Assert.False(entry.Congestion.IsFull);
            Assert.Equal(expected[i].Pct, entry.Congestion.EffectivePercent);
            Assert.Equal(expected[i].Tier, entry.Status);
        }
        // 展平列表与分组列表是同一批条目
        Assert.Same(snap.Groups[0].Channels[1], snap.Channels[1]);
        Assert.Same(snap.Groups[3].Channels[1], snap.Channels[11]);
    }

    [Fact]
    public void Snapshot_GroupWithZeroMaxLoad_IsComfortableAndKeepsZero()
    {
        // 退化分支:全 0 负载、负负载(组内最大值保持 0)、没有频道的空组
        ChannelWireGroup zeroLoads = WireGroup("srv_z",
            ("z01", 100, 0, "192.0.2.1", 19001),
            ("z02", 100, 0, "192.0.2.2", 19002));
        ChannelWireGroup negativeLoads = WireGroup("srv_n",
            ("n01", 100, -5, "192.0.2.3", 19003),
            ("n02", 100, -1, "192.0.2.4", 19004));
        ChannelWireGroup emptyGroup = WireGroup("srv_e");

        ChannelDirectorySnapshot snap = ChannelDirectorySnapshot.Build("0", true, ChannelScriptData.Default, new[] { zeroLoads, negativeLoads, emptyGroup });

        Assert.Equal(3, snap.Groups.Count);
        Assert.Equal(0, snap.Groups[0].MaxCurrentLoad);
        Assert.Equal(0, snap.Groups[1].MaxCurrentLoad);
        Assert.Equal(0, snap.Groups[2].MaxCurrentLoad);
        Assert.Empty(snap.Groups[2].Channels);
        Assert.Equal(4, snap.Channels.Count);
        foreach (ChannelEntry entry in snap.Channels)
        {
            Assert.Equal(ChannelCongestionTier.Comfortable, entry.Status);
            Assert.False(entry.Congestion.IsFull);
            Assert.Equal(0, entry.Congestion.EffectivePercent);
            Assert.Equal(0xff00ff00u, entry.Congestion.ArgbColor);
            Assert.Equal((ChannelEntryFlags)8, entry.Flags);
        }
        Assert.Equal(new[] { 1, 2, 3, 4 }, new[] { snap.Channels[0].ChannelNo, snap.Channels[1].ChannelNo, snap.Channels[2].ChannelNo, snap.Channels[3].ChannelNo });

        // 没有任何分组
        ChannelDirectorySnapshot none = ChannelDirectorySnapshot.Build("0", true, null, null);
        Assert.Empty(none.Groups);
        Assert.Empty(none.Channels);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(19001, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(int.MaxValue, false)]
    public void Snapshot_EntryPortFlag_FollowsLiteralRange(int port, bool expectValid)
    {
        ChannelDirectorySnapshot snap = ChannelDirectorySnapshot.Build("0", true, null, new[]
        {
            WireGroup("srv900", ("zone900_01", 100, 1, "192.0.2.10", port)),
        });
        ChannelEntry entry = Assert.Single(snap.Channels);
        Assert.Equal(port, entry.Port);
        // 标志位按字面数值:有效端口 = 8,否则一位都不带(负载 1/100 不满载、没有推荐规则)
        Assert.Equal((ChannelEntryFlags)(expectValid ? 8 : 0), entry.Flags);
    }

    // ------------------------------------------------------------------ D. 握手与版本检查的严格性

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(37)]
    [InlineData(48)]
    [InlineData(68)]
    public async Task Client_KeyReplyBodyNot36Bytes_IsBadKeyExchangeLength(int bodyLength)
    {
        // 只有恰好 36 字节才合法:多于 36(37、48、68)与少于 36 都要判长度错,且客户端不得继续往下发包
        var (result, commands) = await RunScenarioAsync(async server =>
        {
            await server.ReadRequestAsync(0x0b);
            await server.SendFrameAsync(0x0c, new byte[bodyLength]);
            await server.ExpectNoFurtherRequestAsync();
        });
        Assert.Equal(ChannelDirectoryLoadOutcome.BadKeyExchangeLength, result.Outcome);
        Assert.Equal(new byte[] { 0x0b }, commands);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(7u)]
    [InlineData(0x80000000u)]
    [InlineData(0xffffffffu)]
    public async Task Client_AnyNonZeroHandshakeStatus_IsKeyExchangeRejected(uint status)
    {
        // 只有状态 0 通过;1、2 也必须拒绝,并且把状态码带回结果
        var (result, commands) = await RunScenarioAsync(async server =>
        {
            await server.ReadRequestAsync(0x0b);
            byte[] reply = new byte[36];
            BitConverter.TryWriteBytes(reply.AsSpan(0), status);
            await server.SendFrameAsync(0x0c, reply);
            await server.ExpectNoFurtherRequestAsync();
        });
        Assert.Equal(ChannelDirectoryLoadOutcome.KeyExchangeRejected, result.Outcome);
        Assert.Equal(status, result.HandshakeStatus);
        Assert.Equal(new byte[] { 0x0b }, commands);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(0xffffffffu)]
    public async Task Client_VersionFlagAboveOne_IsRejectedEvenWhenPlaintextIsLongEnough(uint flag)
    {
        // 明文 32 字节(足够长,带"新版本号"):若客户端把 2 当成"需要更新",就会继续发 0x09 拉脚本——必须不发
        var (result, commands) = await RunScenarioAsync(async server =>
        {
            await server.NegotiateSessionKeyAsync();
            await server.ReadVersionRequestAsync();
            byte[] plain = new byte[32];
            BitConverter.TryWriteBytes(plain.AsSpan(0), flag);
            Encoding.ASCII.GetBytes("v903").CopyTo(plain, 4);
            await server.SendEncryptedAsync(0x06, plain);
            await server.ExpectNoFurtherRequestAsync();
        });
        Assert.Equal(ChannelDirectoryLoadOutcome.VersionRejected, result.Outcome);
        Assert.Null(result.VersionUpToDate);
        Assert.Equal(new byte[] { 0x0b, 0x05 }, commands);
    }

    [Fact]
    public async Task Client_OutdatedFlagWithoutNewVersionBytes_IsVersionRejectedWithoutThrowing()
    {
        // 明文只有 16 字节:状态 0(需要更新)却缺 16 字节新版本号。后 12 字节全填非零,
        // 这样越界去读新版本号时不会碰巧遇到 NUL 提前收场——客户端必须在读之前就判 VersionRejected,不得抛越界异常。
        var (result, commands) = await RunScenarioAsync(async server =>
        {
            await server.NegotiateSessionKeyAsync();
            await server.ReadVersionRequestAsync();
            byte[] plain = new byte[16];
            for (int i = 4; i < 16; i++)
            {
                plain[i] = (byte)('a' + i);
            }
            await server.SendEncryptedAsync(0x06, plain);
            await server.ExpectNoFurtherRequestAsync();
        });
        Assert.Equal(ChannelDirectoryLoadOutcome.VersionRejected, result.Outcome);
        Assert.False(result.VersionUpToDate);
        Assert.Equal(new byte[] { 0x0b, 0x05 }, commands);
    }

    // ------------------------------------------------------------------ E. 解密与帧的边界

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(33)]
    public void Crypto_TryDecrypt_LengthNotMultipleOf16_ReturnsFalseWithoutThrowing(int length)
    {
        byte[] key = SyntheticKey16();
        bool ok = true;
        byte[] plain = new byte[1];
        Exception thrown = Record.Exception(() => ok = ChannelServiceCrypto.TryDecryptAes128Ecb(key, new byte[length], out plain));
        Assert.Null(thrown);
        Assert.False(ok);
        Assert.Null(plain);
    }

    [Fact]
    public void Crypto_TryDecrypt_EmptyNullAndBlockMultiples()
    {
        byte[] key = SyntheticKey16();
        Assert.False(ChannelServiceCrypto.TryDecryptAes128Ecb(key, null, out byte[] fromNull));
        Assert.Null(fromNull);
        Assert.False(ChannelServiceCrypto.TryDecryptAes128Ecb(key, Array.Empty<byte>(), out byte[] fromEmpty));
        Assert.Null(fromEmpty);
        // 16 与 32 字节(对照组)能解
        Assert.True(ChannelServiceCrypto.TryDecryptAes128Ecb(key, new byte[16], out byte[] one));
        Assert.Equal(16, one.Length);
        Assert.True(ChannelServiceCrypto.TryDecryptAes128Ecb(key, new byte[32], out byte[] two));
        Assert.Equal(32, two.Length);
    }

    [Theory]
    [InlineData(0, ChannelDirectoryLoadOutcome.DecompressOrDecryptFailed)]
    [InlineData(1, ChannelDirectoryLoadOutcome.DecompressOrDecryptFailed)]
    [InlineData(17, ChannelDirectoryLoadOutcome.DecompressOrDecryptFailed)]
    [InlineData(31, ChannelDirectoryLoadOutcome.DecompressOrDecryptFailed)]
    [InlineData(257, ChannelDirectoryLoadOutcome.FrameError)]
    [InlineData(272, ChannelDirectoryLoadOutcome.FrameError)]
    public async Task Client_VersionReplyBodyWithBadLength_FailsCleanlyWithoutLeakingExceptions(int bodyLength, ChannelDirectoryLoadOutcome expected)
    {
        // 版本检查回复体:0 字节、不是 16 的倍数 → 解密失败;超过 256 字节 → 帧体越界;都不得有异常漏出 LoadFromStreamAsync
        var (result, commands) = await RunScenarioAsync(async server =>
        {
            await server.NegotiateSessionKeyAsync();
            await server.ReadVersionRequestAsync();
            await server.SendFrameAsync(0x06, new byte[bodyLength]);
            await server.ExpectNoFurtherRequestAsync();
        });
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(new byte[] { 0x0b, 0x05 }, commands);
    }

    [Fact]
    public async Task Client_VersionReplyBodyOfExactly256Bytes_PassesTheLengthGates()
    {
        // 256 字节(16 的倍数、恰好等于上限)能走到状态值判定:这里给非法状态 2,看到 VersionRejected 就说明体长关已通过
        var (result, commands) = await RunScenarioAsync(async server =>
        {
            await server.NegotiateSessionKeyAsync();
            await server.ReadVersionRequestAsync();
            byte[] plain = new byte[256];
            plain[0] = 2;
            await server.SendEncryptedAsync(0x06, plain);
            await server.ExpectNoFurtherRequestAsync();
        });
        Assert.Equal(ChannelDirectoryLoadOutcome.VersionRejected, result.Outcome);
        Assert.Equal(new byte[] { 0x0b, 0x05 }, commands);
    }

    [Fact]
    public void Crypto_CompressedBodyLimit_IsInclusiveAtLiteral0x32000()
    {
        byte[] key = SyntheticKey16();

        // 压缩后恰好 0x32000(204800)字节:总长 = 6 + 5 × 18 块 + 原文 204704;原文是 16 的倍数
        byte[] plainAtLimit = new byte[204704];
        for (int i = 0; i < plainAtLimit.Length; i++)
        {
            plainAtLimit[i] = (byte)(i * 7 + 3);
        }
        byte[] bodyAtLimit = ZlibStoredBlocks(ChannelServiceCrypto.EncryptAes128EcbZeroPadded(key, plainAtLimit), 12000);
        Assert.Equal(204800, bodyAtLimit.Length);

        // 压缩后 0x32001(204801)字节:总长 = 6 + 5 × 15 块 + 原文 204720
        byte[] plainOverLimit = new byte[204720];
        for (int i = 0; i < plainOverLimit.Length; i++)
        {
            plainOverLimit[i] = (byte)(i * 5 + 1);
        }
        byte[] bodyOverLimit = ZlibStoredBlocks(ChannelServiceCrypto.EncryptAes128EcbZeroPadded(key, plainOverLimit), 14000);
        Assert.Equal(204801, bodyOverLimit.Length);

        // 恰好等于上限:能进入解压并整条解开(显式上限与默认上限都一样)
        Assert.True(ChannelServiceCrypto.TryInflateZlibAndDecrypt(key, bodyAtLimit, out byte[] okExplicit, out string errExplicit, 0x32000), errExplicit);
        Assert.Equal(plainAtLimit, okExplicit);
        Assert.True(ChannelServiceCrypto.TryInflateZlibAndDecrypt(key, bodyAtLimit, out byte[] okDefault, out string errDefault), errDefault);
        Assert.Equal(plainAtLimit, okDefault);

        // 上限 + 1:判长度无效。同一份数据在放宽上限后能解开,说明它被拒只是因为长度
        Assert.False(ChannelServiceCrypto.TryInflateZlibAndDecrypt(key, bodyOverLimit, out byte[] rejectedExplicit, out string overErrExplicit, 0x32000));
        Assert.Null(rejectedExplicit);
        Assert.False(string.IsNullOrEmpty(overErrExplicit));
        Assert.False(ChannelServiceCrypto.TryInflateZlibAndDecrypt(key, bodyOverLimit, out byte[] rejectedDefault, out string overErrDefault));
        Assert.Null(rejectedDefault);
        Assert.Equal(overErrExplicit, overErrDefault);
        Assert.True(ChannelServiceCrypto.TryInflateZlibAndDecrypt(key, bodyOverLimit, out byte[] okRelaxed, out string errRelaxed, 0x40000), errRelaxed);
        Assert.Equal(plainOverLimit, okRelaxed);

        // 空与 null 也是长度无效,不抛
        Assert.False(ChannelServiceCrypto.TryInflateZlibAndDecrypt(key, null, out _, out string nullErr));
        Assert.Equal(overErrDefault, nullErr);
        Assert.False(ChannelServiceCrypto.TryInflateZlibAndDecrypt(key, Array.Empty<byte>(), out _, out string emptyErr));
        Assert.Equal(overErrDefault, emptyErr);
    }

    [Fact]
    public async Task FrameCodec_BodyLengthLimit_IsInclusive()
    {
        // 显式上限 32:体长 32 可读,33 抛 InvalidDataException
        byte[] at32 = ChannelServiceFrameCodec.Build(0x03, new byte[32]);
        ChannelServiceFrame f32 = await ChannelServiceFrameCodec.ReadFrameAsync(new MemoryStream(at32), maxBodyLength: 32);
        Assert.Equal(32, f32.Body.Length);
        byte[] at33 = ChannelServiceFrameCodec.Build(0x03, new byte[33]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChannelServiceFrameCodec.ReadFrameAsync(new MemoryStream(at33), maxBodyLength: 32));

        // 上限 0:空体可读,1 字节体越界
        ChannelServiceFrame empty = await ChannelServiceFrameCodec.ReadFrameAsync(new MemoryStream(ChannelServiceFrameCodec.Build(0x01)), maxBodyLength: 0);
        Assert.Empty(empty.Body);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChannelServiceFrameCodec.ReadFrameAsync(new MemoryStream(ChannelServiceFrameCodec.Build(0x01, new byte[1])), maxBodyLength: 0));

        // 默认上限 0x32000:体长恰好 204800 可读,204801 越界
        ChannelServiceFrame big = await ChannelServiceFrameCodec.ReadFrameAsync(new MemoryStream(ChannelServiceFrameCodec.Build(0x03, new byte[0x32000])));
        Assert.Equal(204800, big.Body.Length);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChannelServiceFrameCodec.ReadFrameAsync(new MemoryStream(ChannelServiceFrameCodec.Build(0x03, new byte[0x32001]))));

        // 先核对上限再读体:流里只有帧头(声明的体长越界)时判越界,而不是等不到体字节的截断错
        byte[] headerOnly = ChannelServiceFrameCodec.Build(0x03, new byte[33])[..11];
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChannelServiceFrameCodec.ReadFrameAsync(new MemoryStream(headerOnly), maxBodyLength: 32));
    }

    [Theory]
    [InlineData(0, 0, 4, 16)]
    [InlineData(1, 0, 28, 32)]
    [InlineData(1, 1, 76, 80)]
    [InlineData(1, 2, 124, 128)]
    [InlineData(2, 1, 148, 160)]
    public void BinaryParser_AesPaddingMustMatchTheAlignmentExactly(int groupCount, int channelsPerGroup, int expectedUnpaddedLength, int expectedAlignedLength)
    {
        // 有效数据后必须恰好补 (16 - 长度 % 16) % 16 个零:少一个、多一个、整块多补都不是成功
        var groups = new List<(string, (string, int, int, string, int)[])>();
        for (int g = 0; g < groupCount; g++)
        {
            var channels = new (string, int, int, string, int)[channelsPerGroup];
            for (int c = 0; c < channelsPerGroup; c++)
            {
                channels[c] = ("zone90" + g + "_0" + c, 100, 7, "192.0.2.5", 19000 + c);
            }
            groups.Add(("srv90" + g, channels));
        }
        byte[] exact = BuildBinaryChannelList(groups);
        Assert.Equal(expectedUnpaddedLength, exact.Length);

        for (int length = exact.Length; length <= expectedAlignedLength + 17; length++)
        {
            byte[] data = new byte[length];
            Buffer.BlockCopy(exact, 0, data, 0, exact.Length);
            ChannelInfoParseStatus status = ChannelInfoBinaryParser.TryParse(data, true, out List<ChannelWireGroup> parsed);
            if (length == expectedAlignedLength)
            {
                Assert.Equal(ChannelInfoParseStatus.Ok, status);
                Assert.Equal(groupCount, parsed.Count);
            }
            else
            {
                Assert.Equal(ChannelInfoParseStatus.TrailingBytes, status);
                Assert.Null(parsed);
            }
        }
    }

    [Fact]
    public void BinaryParser_FixedWidthStringsKeepEveryByteWhenNotNulTerminated()
    {
        // 组标签 20、频道名 20、主机 16 字节恰好写满、没有 NUL 结尾:必须完整读出
        const string tag20 = "grp900abcdefghijklmn";
        const string name20 = "zone900abcdefghijklm";
        const string host16 = "h900abcdefghijkl";
        Assert.Equal(20, tag20.Length);
        Assert.Equal(20, name20.Length);
        Assert.Equal(16, host16.Length);

        byte[] payload = BuildBinaryChannelList(new[]
        {
            (tag20, new[] { (name20, 100, 7, host16, 19001) }),
        });
        Assert.Equal(76, payload.Length);
        Assert.Equal(ChannelInfoParseStatus.Ok, ChannelInfoBinaryParser.TryParse(payload, false, out List<ChannelWireGroup> groups));
        ChannelWireGroup group = Assert.Single(groups);
        Assert.Equal(tag20, group.Tag);
        ChannelWireEntry entry = Assert.Single(group.Channels);
        Assert.Equal(name20, entry.Name);
        Assert.Equal(host16, entry.Host);

        // 少一个字符(19/19/15):后面有 NUL,读出的就是这 19/19/15 个字符
        byte[] shorter = BuildBinaryChannelList(new[]
        {
            (tag20[..19], new[] { (name20[..19], 100, 7, host16[..15], 19001) }),
        });
        Assert.Equal(ChannelInfoParseStatus.Ok, ChannelInfoBinaryParser.TryParse(shorter, false, out List<ChannelWireGroup> shortGroups));
        Assert.Equal(tag20[..19], shortGroups[0].Tag);
        Assert.Equal(name20[..19], shortGroups[0].Channels[0].Name);
        Assert.Equal(host16[..15], shortGroups[0].Channels[0].Host);

        // 直接读定长串:带偏移;写满不越界;首个 NUL 处截断
        byte[] buffer = new byte[40];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)('A' + (i % 26));
        }
        Assert.Equal("KLMNOPQRSTUVWXYZABCD", ChannelInfoBinaryParser.ReadFixedString(buffer, 10, 20));
        buffer[13] = 0;
        Assert.Equal("KLM", ChannelInfoBinaryParser.ReadFixedString(buffer, 10, 20));
        Assert.Equal(string.Empty, ChannelInfoBinaryParser.ReadFixedString(new byte[16], 0, 16));
    }

    [Fact]
    public async Task Client_ScriptTextHasNoTrailingNulPadding()
    {
        // 脚本明文长度不是 16 的倍数 → AES 零填充 → 解密后尾部带 NUL;RawText 必须是去掉尾部 NUL 的原文
        const string scriptBody = "[channel rate full] 88\n[channel] \"[sample_zone]\" 903 917\n[channel rate full] 88";
        Assert.NotEqual(0, Encoding.UTF8.GetByteCount(scriptBody) % 16);

        var (result, commands) = await RunScenarioAsync(async server =>
        {
            await server.NegotiateSessionKeyAsync();
            Assert.Equal("0", await server.ReadVersionRequestAsync());
            byte[] verPlain = new byte[32];                     // 状态 0(需要更新)+ 16 字节新版本号
            Encoding.ASCII.GetBytes("v905").CopyTo(verPlain, 4);
            await server.SendEncryptedAsync(0x06, verPlain);
            await server.ReadRequestAsync(0x09);
            await server.SendCompressedEncryptedAsync(0x09, Encoding.UTF8.GetBytes(scriptBody));
            await server.ReadRequestAsync(0x01);
            await server.SendCompressedEncryptedAsync(0x03, OneChannelList());
        });

        Assert.True(result.Succeeded, result.ErrorDetail);
        Assert.Equal(new byte[] { 0x0b, 0x05, 0x09, 0x01 }, commands);
        string raw = result.Snapshot.ScriptData.RawText;
        Assert.Equal(scriptBody, raw);
        Assert.Equal(-1, raw.IndexOf('\0'));
        Assert.Equal(88.0f, result.Snapshot.ScriptData.FullThresholdPercent);
        Assert.Equal("v905", result.Snapshot.ScriptVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(70000)]
    [InlineData(int.MaxValue)]
    public async Task Client_PortOutOfRange_IsInvalidArgumentWithoutTouchingNetwork(int port)
    {
        using var cts = new CancellationTokenSource(GuardTimeout);
        ChannelDirectoryLoadResult result = await ChannelDirectoryClient.LoadAsync("192.0.2.1", port, cancellationToken: cts.Token);
        Assert.Equal(ChannelDirectoryLoadOutcome.InvalidArgument, result.Outcome);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public async Task Client_PortInRange_IsNotRejectedAsInvalidArgument(int port)
    {
        // 65535 是合法端口。环回地址上没人监听:连接被拒(或沙箱不让连)都落成 IoError,
        // 这里只要求"不是参数无效";带 3 秒连接超时与 5 秒取消令牌,不会挂起。不连任何真实服务。
        using var cts = new CancellationTokenSource(GuardTimeout);
        ChannelDirectoryLoadResult result = await ChannelDirectoryClient.LoadAsync("127.0.0.1", port, timeout: TimeSpan.FromSeconds(3), cancellationToken: cts.Token);
        Assert.NotEqual(ChannelDirectoryLoadOutcome.InvalidArgument, result.Outcome);
    }

    // ------------------------------------------------------------------ F. 脚本解析边界

    [Theory]
    [InlineData("[channel rate full] 0")]
    [InlineData("[channel rate full] 0.0")]
    [InlineData("[channel rate full] -5")]
    [InlineData("[channel rate full] abc")]
    [InlineData("[channel rate full]")]
    public void ScriptParser_NonPositiveOrUnreadableFullRate_KeepsDefault95(string line)
    {
        Assert.Equal(95.0f, ChannelScriptParser.Parse(line + "\n").FullThresholdPercent);
        // 前面已经给过有效值时,后面的 0 / 非法值也不覆盖它
        Assert.Equal(90.0f, ChannelScriptParser.Parse("[channel rate full] 90\n" + line + "\n").FullThresholdPercent);
    }

    [Fact]
    public void ScriptParser_PositiveFullRateIsAdopted()
    {
        Assert.Equal(80.0f, ChannelScriptParser.Parse("[channel rate full] 80\n").FullThresholdPercent);
        Assert.Equal(0.5f, ChannelScriptParser.Parse("[channel rate full] 0.5\n").FullThresholdPercent);
        Assert.Equal(70.0f, ChannelScriptParser.Parse("[CHANNEL RATE FULL] 70\r\n").FullThresholdPercent);
        // 只取第一个记号;多次出现时后面的有效值覆盖前面的
        Assert.Equal(60.0f, ChannelScriptParser.Parse("[channel rate full] 90\n[channel rate full] 60 70\n").FullThresholdPercent);
    }

    [Fact]
    public void ScriptParser_CommentAndUnknownLines_AreSkipped()
    {
        // 本解析器只认 [channel rate full] 与 [channel] "标签" 最低 最高 两种行,其余行一律跳过(含 # 与 // 开头的行)。
        // 说明:产品里对 # 与 // 开头行的显式跳过,在当前实现里是冗余守卫(后面两种标签的判断本来就要求行首是 '[',
        // 以 # 或 // 开头的行不可能命中),所以"去掉 # 判断"的变异(G08)在可观察行为上与原版完全相同,无法用断言杀死;
        // 本用例只钉住对外行为:这些行既不改阈值也不产生规则。
        const string script =
            "# [channel rate full] 50\r\n" +
            "#[channel] \"[hash_zone]\" 1 2\n" +
            "// [channel rate full] 40\n" +
            "//[channel] \"[slash_zone]\" 3 4\n" +
            "   # 缩进的井号注释\n" +
            "[unknown tag] 77\n" +
            "[channel rate comfortable] 1\n" +
            "[channel rate full] 88\n" +
            "[channel] \"[real_zone]\" 5 6\n";
        ChannelScriptData parsed = ChannelScriptParser.Parse(script);
        Assert.Equal(88.0f, parsed.FullThresholdPercent);
        ChannelRecommendLevelRule only = Assert.Single(parsed.RecommendLevels);
        Assert.Equal("[real_zone]", only.Tag);
        Assert.Equal(5, only.MinLevel);
        Assert.Equal(6, only.MaxLevel);
        Assert.Equal(script, parsed.RawText);
    }

    [Fact]
    public void ScriptParser_RecommendLineWithTooFewTokens_IsSkippedWithoutThrowing()
    {
        const string script =
            "[channel]\n" +                              // 0 个记号
            "[channel] \"[solo_zone]\"\n" +                // 1 个记号
            "[channel] \"[pair_zone]\" 5\n" +              // 2 个记号(缺最高等级)
            "[channel] \"[ok_zone]\" 7 9\n" +              // 3 个记号:唯一有效的一行
            "[channel] \"[low_nan_zone]\" x 9\n" +         // 最低等级不是整数
            "[channel] \"[high_nan_zone]\" 7 y\n";         // 最高等级不是整数
        ChannelScriptData parsed = null;
        Exception thrown = Record.Exception(() => parsed = ChannelScriptParser.Parse(script));
        Assert.Null(thrown);
        ChannelRecommendLevelRule only = Assert.Single(parsed.RecommendLevels);
        Assert.Equal("[ok_zone]", only.Tag);
        Assert.Equal(7, only.MinLevel);
        Assert.Equal(9, only.MaxLevel);
    }

    [Fact]
    public void ScriptData_RecommendTag_MatchesBySubstringAfterDigitStripping()
    {
        ChannelScriptData data = ChannelScriptParser.Parse("[channel] \"[tag]\" 11 22\n[channel] \"[other]\" 33 44\n");

        // 去数字后完全相等
        Assert.True(data.TryMatchRecommendLevel("[tag]", out int min, out int max));
        Assert.Equal((11, 22), (min, max));
        Assert.True(data.TryMatchRecommendLevel("[tag]07", out min, out max));
        Assert.Equal((11, 22), (min, max));

        // 名字"包含"标签(不是完全相等)也匹配:前缀、后缀、数字夹在中间
        Assert.True(data.TryMatchRecommendLevel("xx[tag]yy", out min, out max));
        Assert.Equal((11, 22), (min, max));
        Assert.True(data.TryMatchRecommendLevel("xx[tag]yy12", out min, out max));
        Assert.Equal((11, 22), (min, max));
        Assert.True(data.TryMatchRecommendLevel("9x[tag]3y", out min, out max));
        Assert.Equal((11, 22), (min, max));

        // 大小写不敏感
        Assert.True(data.TryMatchRecommendLevel("XX[TAG]YY", out min, out max));
        Assert.Equal((11, 22), (min, max));

        // 第二条规则也能被命中;不含任何标签的不匹配,并清零输出
        Assert.True(data.TryMatchRecommendLevel("zz[other]", out min, out max));
        Assert.Equal((33, 44), (min, max));
        Assert.False(data.TryMatchRecommendLevel("xx[tg]yy", out min, out max));
        Assert.Equal((0, 0), (min, max));
        Assert.False(data.TryMatchRecommendLevel(string.Empty, out _, out _));
        Assert.False(data.TryMatchRecommendLevel(null, out _, out _));

        // 多条规则都命中时,取出现顺序里的第一条
        ChannelScriptData ordered = ChannelScriptParser.Parse("[channel] \"[tag]\" 1 2\n[channel] \"xx[tag]\" 3 4\n");
        Assert.True(ordered.TryMatchRecommendLevel("xx[tag]yy", out min, out max));
        Assert.Equal((1, 2), (min, max));
    }

    // ------------------------------------------------------------------ G. 版本串长度上限

    [Fact]
    public async Task Client_CachedVersionByteLength_15IsAcceptedAnd16IsRejected()
    {
        string v16 = "v900123456789abc";
        Assert.Equal(16, v16.Length);
        string v15 = v16[..15];

        // 16 字节:参数无效,客户端什么都不发(服务端读到的只能是流结束)
        var (rejected, seen16) = await RunScenarioAsync(server => server.ExpectNoFurtherRequestAsync(), cachedVersion: v16);
        Assert.Equal(ChannelDirectoryLoadOutcome.InvalidArgument, rejected.Outcome);
        Assert.Empty(seen16);

        // 含 NUL:同样参数无效
        var (withNul, seenNul) = await RunScenarioAsync(server => server.ExpectNoFurtherRequestAsync(), cachedVersion: "v9\0x");
        Assert.Equal(ChannelDirectoryLoadOutcome.InvalidArgument, withNul.Outcome);
        Assert.Empty(seenNul);

        // 15 字节:通过校验,整块版本串(15 个字符 + 1 个 NUL)原样到达服务端
        string versionSeenByServer = null;
        var (accepted, seen15) = await RunScenarioAsync(async server =>
        {
            await server.NegotiateSessionKeyAsync();
            versionSeenByServer = await server.ReadVersionRequestAsync();
            byte[] upToDate = new byte[16];
            upToDate[0] = 1;
            await server.SendEncryptedAsync(0x06, upToDate, 16);
            await server.ReadRequestAsync(0x01);
            await server.SendCompressedEncryptedAsync(0x03, OneChannelList());
        }, cachedVersion: v15);
        Assert.True(accepted.Succeeded, accepted.ErrorDetail);
        Assert.Equal(new byte[] { 0x0b, 0x05, 0x01 }, seen15);
        Assert.Equal(v15, versionSeenByServer);
        Assert.Equal(v15, accepted.Snapshot.ScriptVersion);
    }
}
