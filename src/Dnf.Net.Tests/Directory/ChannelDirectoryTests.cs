using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.ChannelDirectory;

namespace Dnf.Net.Tests;

/// <summary>
/// 选服与选频道服务(7001)只读目录读取器的纯合成字节测试。
/// 全部主机、端口、版本、脚本、密钥均采用合成假值(RFC5737 192.0.2.x 与 900+ 编号),不依赖真实抓包或原版文本。
/// 验收后补的边界测试(杀存活变异)在同一个 partial 类的 <c>ChannelDirectoryTests.Boundary.cs</c> 里,共用本文件的私有助手。
/// </summary>
public sealed partial class ChannelDirectoryTests
{
    private static byte[] SyntheticKey16()
    {
        byte[] k = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            k[i] = (byte)(0x90 + i);
        }
        return k;
    }

    private static byte[] SyntheticKey32()
    {
        byte[] k = new byte[32];
        byte[] k16 = SyntheticKey16();
        Buffer.BlockCopy(k16, 0, k, 0, 16);
        for (int i = 16; i < 32; i++)
        {
            k[i] = (byte)(0xa0 + i);
        }
        return k;
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var zs = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zs.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }

    private static void WriteInt32Le(MemoryStream ms, int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BitConverter.TryWriteBytes(buf, value);
        ms.Write(buf);
    }

    private static void WriteFixedText(MemoryStream ms, string text, int fixedLen, Encoding encoding)
    {
        byte[] buf = new byte[fixedLen];
        if (!string.IsNullOrEmpty(text))
        {
            byte[] encoded = (encoding ?? Encoding.ASCII).GetBytes(text);
            int copy = Math.Min(encoded.Length, fixedLen);
            Buffer.BlockCopy(encoded, 0, buf, 0, copy);
        }
        ms.Write(buf, 0, buf.Length);
    }

    private static byte[] BuildBinaryChannelList(
        IReadOnlyList<(string GroupTag, (string Name, int MaxCap, int Load, string Host, int Port)[] Channels)> groups,
        int extraTrailingBytes = 0,
        byte trailingFill = 0,
        Encoding encoding = null)
    {
        using var ms = new MemoryStream();
        WriteInt32Le(ms, groups.Count);
        foreach (var (tag, channels) in groups)
        {
            WriteFixedText(ms, tag, 20, encoding);
            WriteInt32Le(ms, channels.Length);
            foreach (var ch in channels)
            {
                WriteFixedText(ms, ch.Name, 20, encoding);
                WriteInt32Le(ms, ch.MaxCap);
                WriteInt32Le(ms, ch.Load);
                WriteFixedText(ms, ch.Host, 16, encoding);
                WriteInt32Le(ms, ch.Port);
            }
        }
        for (int i = 0; i < extraTrailingBytes; i++)
        {
            ms.WriteByte(trailingFill);
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task FrameCodec_RoundTripsHeaderAndHandlesOneByteFragments()
    {
        byte[] payload = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 };
        byte[] wire = ChannelServiceFrameCodec.Build(
            ChannelServiceConstants.CmdChannelListReply,
            payload,
            byte0: 0x00,
            reserved: 0x90010203u,
            flag: 0x01);

        Assert.Equal(11 + payload.Length, wire.Length);
        Assert.Equal(0x00, wire[0]);
        Assert.Equal(0x03, wire[1]);
        Assert.Equal(16u, BitConverter.ToUInt32(wire, 2));
        Assert.Equal(0x90010203u, BitConverter.ToUInt32(wire, 6));
        Assert.Equal(0x01, wire[10]);

        await using var chunked = new OneByteReadStream(wire);
        ChannelServiceFrame decoded = await ChannelServiceFrameCodec.ReadFrameAsync(chunked);
        Assert.NotNull(decoded);
        Assert.Equal(ChannelServiceConstants.CmdChannelListReply, decoded.Header.Command);
        Assert.Equal(16u, decoded.Header.TotalLength);
        Assert.Equal(0x90010203u, decoded.Header.Reserved);
        Assert.Equal((byte)1, decoded.Header.Flag);
        Assert.Equal(payload, decoded.Body);
    }

    [Fact]
    public async Task FrameCodec_RejectsOutOfRangeLengthsAndTruncation()
    {
        byte[] badTotalLen = ChannelServiceFrameCodec.Build(0x01, Array.Empty<byte>());
        BitConverter.TryWriteBytes(badTotalLen.AsSpan(2), 10u);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChannelServiceFrameCodec.ReadFrameAsync(new MemoryStream(badTotalLen)));

        byte[] oversized = ChannelServiceFrameCodec.Build(0x01, new byte[64]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ChannelServiceFrameCodec.ReadFrameAsync(new MemoryStream(oversized), maxBodyLength: 32));

        byte[] truncatedHeader = new byte[] { 0x00, 0x01, 0x10, 0x00 };
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            ChannelServiceFrameCodec.ReadFrameAsync(new MemoryStream(truncatedHeader)));

        byte[] truncatedBody = ChannelServiceFrameCodec.Build(0x03, new byte[12])[..15];
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            ChannelServiceFrameCodec.ReadFrameAsync(new MemoryStream(truncatedBody)));

        Assert.Null(await ChannelServiceFrameCodec.ReadFrameAsync(new MemoryStream(Array.Empty<byte>())));
    }

    [Fact]
    public void Crypto_AesAndZlib_RoundTripsAndRejectsTampering()
    {
        byte[] key = ChannelServiceConstants.CreateInitialAesKey();
        byte[] greetingCipher = ChannelServiceCrypto.EncryptAes128EcbZeroPadded(
            key,
            ChannelServiceConstants.CreateGreetingPlaintext(),
            32);
        Assert.Equal(32, greetingCipher.Length);
        Assert.True(ChannelServiceCrypto.TryDecryptAes128Ecb(key, greetingCipher, out byte[] greetingDec));
        // 不在测试里重复写预共享的问候明文,只比对摘要(摘要钉住预共享值,避免字面量扩散)
        Assert.Equal(GreetingPlaintextSha256, Sha256Hex(greetingDec));

        byte[] plainPayload = BuildBinaryChannelList(new[]
        {
            ("srv900", new[] { ("zone900_01", 200, 12, "192.0.2.10", 19001) })
        });
        byte[] paddedCipher = ChannelServiceCrypto.EncryptAes128EcbZeroPadded(key, plainPayload);
        byte[] compressed = ZlibCompress(paddedCipher);

        Assert.True(ChannelServiceCrypto.TryInflateZlibAndDecrypt(key, compressed, out byte[] roundtripped, out string err), err);
        Assert.Equal(ChannelInfoParseStatus.Ok, ChannelInfoBinaryParser.TryParse(roundtripped, true, out List<ChannelWireGroup> groups));
        Assert.Single(groups);
        Assert.Equal("192.0.2.10", groups[0].Channels[0].Host);
        Assert.Equal(19001, groups[0].Channels[0].Port);

        byte[] corruptedAdler = (byte[])compressed.Clone();
        corruptedAdler[^1] ^= 0xff;
        Assert.False(ChannelServiceCrypto.TryInflateZlibAndDecrypt(key, corruptedAdler, out _, out _));

        byte[] nonMultipleOf16Compressed = ZlibCompress(new byte[17]);
        Assert.False(ChannelServiceCrypto.TryInflateZlibAndDecrypt(key, nonMultipleOf16Compressed, out _, out string modErr));
        Assert.Contains("16", modErr);
    }

    [Fact]
    public void BinaryParser_ParsesAllFieldsAndZeroItemsAndDetectsAllCorruptions()
    {
        byte[] payload = BuildBinaryChannelList(new[]
        {
            ("srv900", new[]
            {
                ("zone900_01", 200, 10, "192.0.2.10", 19001),
                ("zone900_02", 300, 150, "192.0.2.11", 19002),
            }),
            ("srv901", new[]
            {
                ("arena901_01", 100, 99, "192.0.2.20", 19003),
            }),
        });

        Assert.Equal(ChannelInfoParseStatus.Ok, ChannelInfoBinaryParser.TryParse(payload, allowAesZeroPadding: false, out List<ChannelWireGroup> groups));
        Assert.Equal(2, groups.Count);
        Assert.Equal("srv900", groups[0].Tag);
        Assert.Equal(2, groups[0].Channels.Count);
        Assert.Equal("zone900_01", groups[0].Channels[0].Name);
        Assert.Equal(200, groups[0].Channels[0].MaxCapacity);
        Assert.Equal(10, groups[0].Channels[0].CurrentLoad);
        Assert.Equal("192.0.2.10", groups[0].Channels[0].Host);
        Assert.Equal(19001, groups[0].Channels[0].Port);
        Assert.Equal("arena901_01", groups[1].Channels[0].Name);
        Assert.Equal("192.0.2.20", groups[1].Channels[0].Host);
        Assert.Equal(19003, groups[1].Channels[0].Port);

        // Zero groups & zero channels in a group
        byte[] zeroGroups = BuildBinaryChannelList(Array.Empty<(string, (string, int, int, string, int)[])>());
        Assert.Equal(ChannelInfoParseStatus.Ok, ChannelInfoBinaryParser.TryParse(zeroGroups, false, out List<ChannelWireGroup> emptyList));
        Assert.Empty(emptyList);

        byte[] zeroChannels = BuildBinaryChannelList(new[] { ("srv902", Array.Empty<(string, int, int, string, int)>()) });
        Assert.Equal(ChannelInfoParseStatus.Ok, ChannelInfoBinaryParser.TryParse(zeroChannels, false, out List<ChannelWireGroup> emptyGroup));
        Assert.Single(emptyGroup);
        Assert.Empty(emptyGroup[0].Channels);

        // Null & truncation at every length
        Assert.Equal(ChannelInfoParseStatus.NullInput, ChannelInfoBinaryParser.TryParse(null, false, out _));
        for (int len = 0; len < payload.Length; len++)
        {
            byte[] slice = payload[..len];
            ChannelInfoParseStatus st = ChannelInfoBinaryParser.TryParse(slice, false, out _);
            Assert.NotEqual(ChannelInfoParseStatus.Ok, st);
        }

        // Strict mode rejects even 1 trailing byte; AES padding mode rejects non-zero padding or wrong remainder
        byte[] withOneTrailing = BuildBinaryChannelList(new[] { ("srv900", Array.Empty<(string, int, int, string, int)>()) }, extraTrailingBytes: 1, trailingFill: 0);
        Assert.Equal(ChannelInfoParseStatus.TrailingBytes, ChannelInfoBinaryParser.TryParse(withOneTrailing, false, out _));

        int padNeed = (16 - (payload.Length % 16)) % 16;
        byte[] aesAligned = new byte[payload.Length + padNeed];
        Buffer.BlockCopy(payload, 0, aesAligned, 0, payload.Length);
        Assert.Equal(ChannelInfoParseStatus.Ok, ChannelInfoBinaryParser.TryParse(aesAligned, true, out _));

        if (padNeed > 0)
        {
            byte[] badPadByte = (byte[])aesAligned.Clone();
            badPadByte[^1] = 0x01;
            Assert.Equal(ChannelInfoParseStatus.NonZeroPadding, ChannelInfoBinaryParser.TryParse(badPadByte, true, out _));
        }

        byte[] extraBlock = new byte[aesAligned.Length + 16];
        Buffer.BlockCopy(aesAligned, 0, extraBlock, 0, aesAligned.Length);
        Assert.Equal(ChannelInfoParseStatus.TrailingBytes, ChannelInfoBinaryParser.TryParse(extraBlock, true, out _));

        // Bad counts
        byte[] negGroup = (byte[])zeroGroups.Clone();
        BitConverter.TryWriteBytes(negGroup.AsSpan(0), -1);
        Assert.Equal(ChannelInfoParseStatus.BadGroupCount, ChannelInfoBinaryParser.TryParse(negGroup, false, out _));

        byte[] hugeGroup = (byte[])zeroGroups.Clone();
        BitConverter.TryWriteBytes(hugeGroup.AsSpan(0), 900);
        Assert.Equal(ChannelInfoParseStatus.BadGroupCount, ChannelInfoBinaryParser.TryParse(hugeGroup, false, out _));

        byte[] hugeChan = (byte[])zeroChannels.Clone();
        BitConverter.TryWriteBytes(hugeChan.AsSpan(24), 900);
        Assert.Equal(ChannelInfoParseStatus.BadChannelCount, ChannelInfoBinaryParser.TryParse(hugeChan, false, out _));

        // Field-by-field mutations visibly change parsed result
        byte[] single = BuildBinaryChannelList(new[]
        {
            ("srv900", new[] { ("zone900_01", 200, 10, "192.0.2.10", 19001) })
        });
        Assert.Equal(ChannelInfoParseStatus.Ok, ChannelInfoBinaryParser.TryParse(single, false, out List<ChannelWireGroup> baseline));

        byte[] mutName = (byte[])single.Clone();
        mutName[4 + 24] = (byte)'X';
        ChannelInfoBinaryParser.TryParse(mutName, false, out List<ChannelWireGroup> pName);
        Assert.NotEqual(baseline[0].Channels[0].Name, pName[0].Channels[0].Name);

        byte[] mutCap = (byte[])single.Clone();
        BitConverter.TryWriteBytes(mutCap.AsSpan(4 + 24 + 20), 999);
        ChannelInfoBinaryParser.TryParse(mutCap, false, out List<ChannelWireGroup> pCap);
        Assert.Equal(999, pCap[0].Channels[0].MaxCapacity);

        byte[] mutLoad = (byte[])single.Clone();
        BitConverter.TryWriteBytes(mutLoad.AsSpan(4 + 24 + 24), 88);
        ChannelInfoBinaryParser.TryParse(mutLoad, false, out List<ChannelWireGroup> pLoad);
        Assert.Equal(88, pLoad[0].Channels[0].CurrentLoad);

        byte[] mutHost = (byte[])single.Clone();
        Encoding.ASCII.GetBytes("192.0.2.99").CopyTo(mutHost, 4 + 24 + 28);
        ChannelInfoBinaryParser.TryParse(mutHost, false, out List<ChannelWireGroup> pHost);
        Assert.Equal("192.0.2.99", pHost[0].Channels[0].Host);

        byte[] mutPort = (byte[])single.Clone();
        BitConverter.TryWriteBytes(mutPort.AsSpan(4 + 24 + 44), 19999);
        ChannelInfoBinaryParser.TryParse(mutPort, false, out List<ChannelWireGroup> pPort);
        Assert.Equal(19999, pPort[0].Channels[0].Port);
    }

    [Fact]
    public void CongestionEvaluatorAndScriptParser_MatchClientThresholdRules()
    {
        const string script =
            "[channel rate comfortable] 1\n" +
            "[channel rate harmony] 3\n" +
            "[channel rate normal] 11\n" +
            "[channel rate full] 92\n" +
            "[channel] \"[alpha_zone]\" 901 915\n";

        ChannelScriptData parsed = ChannelScriptParser.Parse(script);
        Assert.Equal(92.0f, parsed.FullThresholdPercent);
        Assert.Single(parsed.RecommendLevels);
        Assert.True(parsed.TryMatchRecommendLevel("[alpha_zone]03", out int minLv, out int maxLv));
        Assert.Equal(901, minLv);
        Assert.Equal(915, maxLv);
        Assert.False(parsed.TryMatchRecommendLevel("[other_zone]01", out _, out _));

        // effectiveMax = min((int)(200 * 0.7f) = 140, groupMaxLoad = 50) = 50
        ChannelCongestionEvaluation t0 = ChannelCongestionEvaluator.Evaluate(1, 200, 50, 95.0f);
        ChannelCongestionEvaluation t1 = ChannelCongestionEvaluator.Evaluate(2, 200, 50, 95.0f);
        ChannelCongestionEvaluation t2 = ChannelCongestionEvaluator.Evaluate(3, 200, 50, 95.0f);
        ChannelCongestionEvaluation t3 = ChannelCongestionEvaluator.Evaluate(13, 200, 50, 95.0f);
        ChannelCongestionEvaluation t4 = ChannelCongestionEvaluator.Evaluate(191, 200, 191, 95.0f);

        Assert.Equal(ChannelCongestionTier.Comfortable, t0.Tier);
        Assert.Equal(0xff00ff00u, t0.ArgbColor);
        Assert.Equal(ChannelCongestionTier.Harmony, t1.Tier);
        Assert.Equal(0xff00b1ffu, t1.ArgbColor);
        Assert.Equal(ChannelCongestionTier.Normal, t2.Tier);
        Assert.Equal(0xff0080ffu, t2.ArgbColor);
        Assert.Equal(ChannelCongestionTier.Crowded, t3.Tier);
        Assert.Equal(0xff0000ffu, t3.ArgbColor);
        Assert.Equal(ChannelCongestionTier.Full, t4.Tier);
        Assert.True(t4.IsFull);
        Assert.Equal(0xfff000ffu, t4.ArgbColor);

        // Group cap above 0.7f uses (int)(maxCapacity * 0.7f) = 140: load=35 -> 25% -> Crowded
        ChannelCongestionEvaluation capped = ChannelCongestionEvaluator.Evaluate(35, 200, 500, 95.0f);
        Assert.Equal(ChannelCongestionTier.Crowded, capped.Tier);
        Assert.Equal(25, capped.EffectivePercent);
    }

    [Fact]
    public async Task Client_FullOutdatedFlow_DownloadsScriptAndChannelSnapshot()
    {
        var (clientStream, serverStream) = BlockingDuplexStream.CreatePair();
        byte[] sessionKey32 = SyntheticKey32();
        byte[] sessionKey16 = SyntheticKey16();
        var commandsSeen = new List<byte>();
        // 脚本明文长度不是 16 的倍数,AES 零填充后尾部带 NUL;客户端必须把尾部 NUL 去掉(见末尾 RawText 断言)
        const string scriptBody = "[channel rate full] 90\n[channel] \"[zone_alpha]\" 905 920\n";

        // 5 秒级超时:假服务端/客户端等不到数据时按失败处理,不会卡死
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task serverTask = Task.Run(async () =>
        {
            await using var _s = serverStream;
            byte[] initialKey = ChannelServiceConstants.CreateInitialAesKey();

            ChannelServiceFrame fHello = await ChannelServiceFrameCodec.ReadFrameAsync(serverStream, cancellationToken: cts.Token);
            Assert.NotNull(fHello);
            commandsSeen.Add(fHello.Header.Command);
            Assert.Equal(ChannelServiceConstants.CmdKeyExchangeRequest, fHello.Header.Command);
            Assert.Equal(32, fHello.Body.Length);
            Assert.True(ChannelServiceCrypto.TryDecryptAes128Ecb(initialKey, fHello.Body, out byte[] helloPlain));
            Assert.Equal(GreetingPlaintextSha256, Sha256Hex(helloPlain));

            byte[] keyExchangeReply = new byte[36];
            Buffer.BlockCopy(sessionKey32, 0, keyExchangeReply, 4, 32);
            await SendRawAsync(serverStream, ChannelServiceConstants.CmdKeyExchangeReply, keyExchangeReply);

            ChannelServiceFrame fVer = await ChannelServiceFrameCodec.ReadFrameAsync(serverStream, cancellationToken: cts.Token);
            Assert.NotNull(fVer);
            commandsSeen.Add(fVer.Header.Command);
            Assert.Equal(ChannelServiceConstants.CmdVersionCheckRequest, fVer.Header.Command);
            Assert.True(ChannelServiceCrypto.TryDecryptAes128Ecb(sessionKey16, fVer.Body, out byte[] verDec));
            Assert.Equal("0", ChannelInfoBinaryParser.ReadFixedString(verDec, 0, 16));

            byte[] verReplyPlain = new byte[20];
            BitConverter.TryWriteBytes(verReplyPlain.AsSpan(0), 0u);
            Encoding.ASCII.GetBytes("v900").CopyTo(verReplyPlain, 4);
            byte[] verReplyCipher = ChannelServiceCrypto.EncryptAes128EcbZeroPadded(sessionKey16, verReplyPlain, 32);
            await SendRawAsync(serverStream, ChannelServiceConstants.CmdVersionCheckReply, verReplyCipher);

            ChannelServiceFrame fScriptReq = await ChannelServiceFrameCodec.ReadFrameAsync(serverStream, cancellationToken: cts.Token);
            Assert.NotNull(fScriptReq);
            commandsSeen.Add(fScriptReq.Header.Command);
            Assert.Equal(ChannelServiceConstants.CmdScriptRequest, fScriptReq.Header.Command);
            Assert.Empty(fScriptReq.Body);

            byte[] scriptCipher = ChannelServiceCrypto.EncryptAes128EcbZeroPadded(sessionKey16, Encoding.UTF8.GetBytes(scriptBody));
            await SendRawAsync(serverStream, ChannelServiceConstants.CmdScriptRequest, ZlibCompress(scriptCipher));

            ChannelServiceFrame fListReq = await ChannelServiceFrameCodec.ReadFrameAsync(serverStream, cancellationToken: cts.Token);
            Assert.NotNull(fListReq);
            commandsSeen.Add(fListReq.Header.Command);
            Assert.Equal(ChannelServiceConstants.CmdChannelListRequest, fListReq.Header.Command);
            Assert.Empty(fListReq.Body);

            byte[] listPlain = BuildBinaryChannelList(new[]
            {
                ("srv_900", new[]
                {
                    ("[zone_alpha]01", 200, 1, "192.0.2.10", 19001),
                    ("[zone_alpha]02", 200, 185, "192.0.2.11", 19002),
                }),
            });
            byte[] listCipher = ChannelServiceCrypto.EncryptAes128EcbZeroPadded(sessionKey16, listPlain);
            await SendRawAsync(serverStream, ChannelServiceConstants.CmdChannelListReply, ZlibCompress(listCipher));
        });

        ChannelDirectoryLoadResult result;
        await using (clientStream)
        {
            result = await ChannelDirectoryClient.LoadFromStreamAsync(clientStream, cancellationToken: cts.Token);
        }
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded, result.ErrorDetail);
        Assert.Equal(new byte[] { 0x0b, 0x05, 0x09, 0x01 }, commandsSeen.ToArray());
        Assert.Equal("v900", result.Snapshot.ScriptVersion);
        Assert.False(result.Snapshot.ScriptUpToDate);
        Assert.Equal(90.0f, result.Snapshot.ScriptData.FullThresholdPercent);
        // 脚本原文里不能带 AES 零填充留下的尾部 NUL
        Assert.Equal(scriptBody, result.Snapshot.ScriptData.RawText);
        Assert.Equal(2, result.Snapshot.Channels.Count);

        ChannelEntry ch1 = result.Snapshot.Channels[0];
        Assert.Equal(1, ch1.ChannelNo);
        Assert.Equal("srv_900", ch1.GroupTag);
        Assert.Equal("[zone_alpha]01", ch1.Name);
        Assert.Equal("[zone_alpha]", ch1.DisplayPrefix);
        Assert.Equal("192.0.2.10", ch1.Host);
        Assert.Equal(19001, ch1.Port);
        Assert.Equal(ChannelCongestionTier.Comfortable, ch1.Status);
        Assert.Equal(905, ch1.RecommendedMinLevel);
        Assert.Equal(920, ch1.RecommendedMaxLevel);
        Assert.True(ch1.Flags.HasFlag(ChannelEntryFlags.HasRecommendLevel));
        Assert.True(ch1.Flags.HasFlag(ChannelEntryFlags.HasValidPort));
        Assert.False(ch1.Flags.HasFlag(ChannelEntryFlags.Full));
        // 标志位按字面数值钉死:HasRecommendLevel=2 | HasValidPort=8,没有多余的位
        Assert.Equal((ChannelEntryFlags)(2 | 8), ch1.Flags);

        ChannelEntry ch2 = result.Snapshot.Channels[1];
        Assert.Equal(2, ch2.ChannelNo);
        Assert.Equal(ChannelCongestionTier.Full, ch2.Status);
        Assert.True(ch2.Flags.HasFlag(ChannelEntryFlags.Full));
        // Full=1 | HasRecommendLevel=2 | HasValidPort=8
        Assert.Equal((ChannelEntryFlags)(1 | 2 | 8), ch2.Flags);
    }

    [Fact]
    public async Task Client_UpToDateVersion_SkipsScriptRequestAndAppliesFallbackScript()
    {
        var (clientStream, serverStream) = BlockingDuplexStream.CreatePair();
        byte[] sessionKey32 = SyntheticKey32();
        byte[] sessionKey16 = SyntheticKey16();
        var commandsSeen = new List<byte>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task serverTask = Task.Run(async () =>
        {
            await using var _s = serverStream;
            ChannelServiceFrame fHello = await ChannelServiceFrameCodec.ReadFrameAsync(serverStream, cancellationToken: cts.Token);
            commandsSeen.Add(fHello.Header.Command);
            byte[] keyReply = new byte[36];
            Buffer.BlockCopy(sessionKey32, 0, keyReply, 4, 32);
            await SendRawAsync(serverStream, ChannelServiceConstants.CmdKeyExchangeReply, keyReply);

            ChannelServiceFrame fVer = await ChannelServiceFrameCodec.ReadFrameAsync(serverStream, cancellationToken: cts.Token);
            commandsSeen.Add(fVer.Header.Command);
            byte[] verOk = new byte[16];
            BitConverter.TryWriteBytes(verOk.AsSpan(0), 1u);
            await SendRawAsync(serverStream, ChannelServiceConstants.CmdVersionCheckReply,
                ChannelServiceCrypto.EncryptAes128EcbZeroPadded(sessionKey16, verOk, 16));

            ChannelServiceFrame fList = await ChannelServiceFrameCodec.ReadFrameAsync(serverStream, cancellationToken: cts.Token);
            commandsSeen.Add(fList.Header.Command);
            byte[] listPlain = BuildBinaryChannelList(new[]
            {
                ("srv_901", new[] { ("[beta_zone]04", 100, 20, "192.0.2.30", 19004) }),
            });
            byte[] listCipher = ChannelServiceCrypto.EncryptAes128EcbZeroPadded(sessionKey16, listPlain);
            await SendRawAsync(serverStream, ChannelServiceConstants.CmdChannelListReply, ZlibCompress(listCipher));
        });

        ChannelScriptData fallback = ChannelScriptParser.Parse("[channel rate full] 80\n[channel] \"[beta_zone]\" 910 930\n");
        ChannelDirectoryLoadResult result;
        await using (clientStream)
        {
            result = await ChannelDirectoryClient.LoadFromStreamAsync(clientStream, cachedVersion: "v901", fallbackScript: fallback, cancellationToken: cts.Token);
        }
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded, result.ErrorDetail);
        Assert.Equal(new byte[] { 0x0b, 0x05, 0x01 }, commandsSeen.ToArray());
        Assert.True(result.Snapshot.ScriptUpToDate);
        Assert.Equal("v901", result.Snapshot.ScriptVersion);
        Assert.Equal(910, result.Snapshot.Channels[0].RecommendedMinLevel);
        Assert.Equal(930, result.Snapshot.Channels[0].RecommendedMaxLevel);
    }

    [Fact]
    public async Task Client_RejectsBadHandshakeStatusBadVersionFlagAndInvalidArguments()
    {
        // Bad endpoint arguments never touch network
        ChannelDirectoryLoadResult badHost = await ChannelDirectoryClient.LoadAsync(string.Empty, 7001);
        Assert.Equal(ChannelDirectoryLoadOutcome.InvalidArgument, badHost.Outcome);
        ChannelDirectoryLoadResult badPort = await ChannelDirectoryClient.LoadAsync("192.0.2.1", 0);
        Assert.Equal(ChannelDirectoryLoadOutcome.InvalidArgument, badPort.Outcome);
        ChannelDirectoryLoadResult badTimeout = await ChannelDirectoryClient.LoadAsync("192.0.2.1", 7001, timeout: TimeSpan.Zero);
        Assert.Equal(ChannelDirectoryLoadOutcome.InvalidArgument, badTimeout.Outcome);

        // Bad cached version (contains NUL or >= 16 bytes) rejected before wire send
        {
            var (c, _) = BlockingDuplexStream.CreatePair();
            // 带 5 秒取消令牌:若回归成"放行 16 字节版本串",客户端会发问候帧再等回包,这里要按失败收场而不是挂起
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            ChannelDirectoryLoadResult badVerLen = await ChannelDirectoryClient.LoadFromStreamAsync(c, cachedVersion: new string('9', 16), cancellationToken: cts.Token);
            Assert.Equal(ChannelDirectoryLoadOutcome.InvalidArgument, badVerLen.Outcome);
        }

        // Flag == 0 on key exchange reply -> FrameError
        {
            var (c, s) = BlockingDuplexStream.CreatePair();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task srv = Task.Run(async () =>
            {
                await using var _s = s;
                await ChannelServiceFrameCodec.ReadFrameAsync(s, cancellationToken: cts.Token);
                byte[] pkt = ChannelServiceFrameCodec.Build(ChannelServiceConstants.CmdKeyExchangeReply, new byte[36], flag: 0);
                await s.WriteAsync(pkt, 0, pkt.Length);
                await s.FlushAsync();
            });
            ChannelDirectoryLoadResult res;
            await using (c)
            {
                res = await ChannelDirectoryClient.LoadFromStreamAsync(c, cancellationToken: cts.Token);
            }
            await srv.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ChannelDirectoryLoadOutcome.FrameError, res.Outcome);
        }

        // Corrupted compressed channel list -> DecompressOrDecryptFailed
        {
            var (c, s) = BlockingDuplexStream.CreatePair();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            byte[] key32 = SyntheticKey32();
            byte[] key16 = SyntheticKey16();
            Task srv = Task.Run(async () =>
            {
                await using var _s = s;
                await ChannelServiceFrameCodec.ReadFrameAsync(s, cancellationToken: cts.Token);
                byte[] kReply = new byte[36];
                Buffer.BlockCopy(key32, 0, kReply, 4, 32);
                await SendRawAsync(s, ChannelServiceConstants.CmdKeyExchangeReply, kReply);

                await ChannelServiceFrameCodec.ReadFrameAsync(s, cancellationToken: cts.Token);
                byte[] verOk = new byte[16];
                BitConverter.TryWriteBytes(verOk.AsSpan(0), 1u);
                await SendRawAsync(s, ChannelServiceConstants.CmdVersionCheckReply,
                    ChannelServiceCrypto.EncryptAes128EcbZeroPadded(key16, verOk, 16));

                await ChannelServiceFrameCodec.ReadFrameAsync(s, cancellationToken: cts.Token);
                await SendRawAsync(s, ChannelServiceConstants.CmdChannelListReply, new byte[] { 0x78, 0x9c, 0x00, 0x01, 0x02, 0x03, 0x04 });
            });
            ChannelDirectoryLoadResult res;
            await using (c)
            {
                res = await ChannelDirectoryClient.LoadFromStreamAsync(c, "v902", cancellationToken: cts.Token);
            }
            await srv.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ChannelDirectoryLoadOutcome.DecompressOrDecryptFailed, res.Outcome);
        }

        // Non-zero handshake status -> KeyExchangeRejected
        {
            var (c, s) = BlockingDuplexStream.CreatePair();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task srv = Task.Run(async () =>
            {
                await using var _s = s;
                await ChannelServiceFrameCodec.ReadFrameAsync(s, cancellationToken: cts.Token);
                byte[] reply = new byte[36];
                BitConverter.TryWriteBytes(reply.AsSpan(0), 7u);
                await SendRawAsync(s, ChannelServiceConstants.CmdKeyExchangeReply, reply);
            });
            ChannelDirectoryLoadResult res;
            await using (c)
            {
                res = await ChannelDirectoryClient.LoadFromStreamAsync(c, cancellationToken: cts.Token);
            }
            await srv.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ChannelDirectoryLoadOutcome.KeyExchangeRejected, res.Outcome);
            Assert.Equal(7u, res.HandshakeStatus);
        }

        // Wrong 0x0c length -> BadKeyExchangeLength
        {
            var (c, s) = BlockingDuplexStream.CreatePair();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task srv = Task.Run(async () =>
            {
                await using var _s = s;
                await ChannelServiceFrameCodec.ReadFrameAsync(s, cancellationToken: cts.Token);
                await SendRawAsync(s, ChannelServiceConstants.CmdKeyExchangeReply, new byte[35]);
            });
            ChannelDirectoryLoadResult res;
            await using (c)
            {
                res = await ChannelDirectoryClient.LoadFromStreamAsync(c, cancellationToken: cts.Token);
            }
            await srv.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ChannelDirectoryLoadOutcome.BadKeyExchangeLength, res.Outcome);
        }

        // upToDate == 2 -> VersionRejected
        {
            var (c, s) = BlockingDuplexStream.CreatePair();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            byte[] key32 = SyntheticKey32();
            byte[] key16 = SyntheticKey16();
            Task srv = Task.Run(async () =>
            {
                await using var _s = s;
                await ChannelServiceFrameCodec.ReadFrameAsync(s, cancellationToken: cts.Token);
                byte[] reply = new byte[36];
                Buffer.BlockCopy(key32, 0, reply, 4, 32);
                await SendRawAsync(s, ChannelServiceConstants.CmdKeyExchangeReply, reply);
                await ChannelServiceFrameCodec.ReadFrameAsync(s, cancellationToken: cts.Token);
                byte[] badVer = new byte[16];
                BitConverter.TryWriteBytes(badVer.AsSpan(0), 2u);
                await SendRawAsync(s, ChannelServiceConstants.CmdVersionCheckReply,
                    ChannelServiceCrypto.EncryptAes128EcbZeroPadded(key16, badVer, 16));
            });
            ChannelDirectoryLoadResult res;
            await using (c)
            {
                res = await ChannelDirectoryClient.LoadFromStreamAsync(c, cancellationToken: cts.Token);
            }
            await srv.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ChannelDirectoryLoadOutcome.VersionRejected, res.Outcome);
        }
    }

    [Fact]
    public void BinaryParser_DecodesUtf8ChineseGroupTagsAndChannelNames()
    {
        byte[] payload = BuildBinaryChannelList(new[]
        {
            ("测试大区", new[]
            {
                ("测试频道11", 200, 12, "192.0.2.15", 19011),
            }),
        }, encoding: Encoding.UTF8);

        ChannelInfoParseStatus status = ChannelInfoBinaryParser.TryParse(payload, allowAesZeroPadding: false, out List<ChannelWireGroup> groups);

        Assert.Equal(ChannelInfoParseStatus.Ok, status);
        ChannelWireGroup group = Assert.Single(groups);
        Assert.Equal("测试大区", group.Tag);
        Assert.DoesNotContain('?', group.Tag);
        ChannelWireEntry entry = Assert.Single(group.Channels);
        Assert.Equal("测试频道11", entry.Name);
        Assert.DoesNotContain('?', entry.Name);
        Assert.Equal("192.0.2.15", entry.Host);
        Assert.Equal(19011, entry.Port);
    }

    [Fact]
    public void ChannelScriptParser_ParsesServerBlocksBackticksGroupIdsAndMergeFallback()
    {
        const string serverScript =
            "[dungeon]\n" +
            "[server] 3\n" +
            "  11 <4::cname_syn_11> 0 `[zone_alpha]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  52 <4::cname_syn_52> 13 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "[/server]\n" +
            "[server] 8  21 `测试直写名` 1 `[zone_beta]` 0 0 0 0 0 0 `` `` `` 0 0  [/server]\n";

        ChannelScriptData primary = ChannelScriptParser.Parse(serverScript);

        Assert.False(primary.HasExplicitFullThreshold);
        Assert.Empty(primary.RecommendLevels);
        Assert.Equal(3, primary.ServerSlots.Count);

        // 14 canonical server group IDs (English + Simplified/Traditional Chinese aliases)
        (string Tag, int ExpectedId)[] cases =
        {
            ("cain", 1), ("卡恩", 1),
            ("diregie", 2), ("狄瑞吉", 2),
            ("siroco", 3), ("希洛克", 3),
            ("prey", 4), ("pray", 4), ("普雷", 4),
            ("casillas", 5), ("cassillas", 5), ("卡西利亚斯", 5), ("卡西利亞斯", 5),
            ("hilder", 6), ("赫尔德", 6), ("赫爾德", 6),
            ("ruke", 7), ("luke", 7), ("卢克", 7),
            ("seria", 8), ("cerria", 8), ("赛丽亚", 8), ("賽麗亞", 8),
            ("anton", 9), ("安徒恩", 9),
            ("roxy", 10), ("lukse", 10), ("卢克西", 10),
            ("bakal", 11), ("巴卡尔", 11),
            ("ozma", 12), ("奥兹玛", 12),
            ("khazan", 13), ("kazan", 13), ("卡赞", 13),
            ("starter_server", 14), ("start", 14), ("新手", 14),
        };
        foreach ((string tag, int expectedId) in cases)
        {
            Assert.True(ChannelScriptData.TryMapServerGroupId(tag, out int actualId), tag);
            Assert.Equal(expectedId, actualId);
        }
        Assert.False(ChannelScriptData.TryMapServerGroupId(null, out _));
        Assert.False(ChannelScriptData.TryMapServerGroupId("unknown_group_999", out _));

        ChannelServerSlotRule siroco11 = primary.FindServerSlot("siroco", 11);
        Assert.NotNull(siroco11);
        Assert.Equal(3, siroco11.ServerGroupId);
        Assert.Equal(11, siroco11.ChannelNo);
        Assert.Equal(4, siroco11.TitleStringKind);
        Assert.Equal("cname_syn_11", siroco11.TitleStringKey);
        Assert.Equal("[zone_alpha]", siroco11.AreaTag);

        ChannelServerSlotRule siroco11Zh = primary.FindServerSlot("希洛克", 11);
        Assert.Same(siroco11, siroco11Zh);

        ChannelServerSlotRule seria21 = primary.FindServerSlot("赛丽亚", 21);
        Assert.NotNull(seria21);
        Assert.Equal(8, seria21.ServerGroupId);
        Assert.Equal(-1, seria21.TitleStringKind);
        Assert.Equal("测试直写名", seria21.LiteralTitle);
        Assert.Equal("[zone_beta]", seria21.AreaTag);

        ChannelScriptData fallback = ChannelScriptParser.Parse("[channel rate full] 88.5\n[channel] `[zone_alpha]` 41 85\n");
        ChannelScriptData merged = primary.MergeFallback(fallback);
        Assert.Equal(serverScript, merged.RawText);
        Assert.True(merged.HasExplicitFullThreshold);
        Assert.Equal(88.5f, merged.FullThresholdPercent);
        Assert.Single(merged.RecommendLevels);
        Assert.Equal(3, merged.ServerSlots.Count);
    }

    [Fact]
    public void SnapshotBuild_PopulatesParsedChannelNoSlotRuleAndAreaRecommendLevel()
    {
        ChannelScriptData script = ChannelScriptParser.Parse(
            "[server] 3\n" +
            "  11 <4::cname_syn_11> 0 `[zone_alpha]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  52 <4::cname_syn_52> 13 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "[/server]\n" +
            "[channel] `[zone_alpha]` 41 85\n");

        ChannelDirectorySnapshot snapshot = ChannelDirectorySnapshot.Build(
            "v900",
            false,
            script,
            new[]
            {
                WireGroup("siroco", ("#ch.11", 200, 10, "192.0.2.10", 19011), ("#ch.52", 200, 5, "192.0.2.10", 19052)),
            });

        Assert.Equal(2, snapshot.Channels.Count);
        ChannelEntry ch11 = snapshot.Channels[0];
        Assert.Equal(1, ch11.ChannelNo);
        Assert.Equal(11, ch11.ParsedChannelNo);
        Assert.Equal("#ch.", ch11.DisplayPrefix);
        Assert.NotNull(ch11.SlotRule);
        Assert.Equal("cname_syn_11", ch11.SlotRule.TitleStringKey);
        Assert.True(ch11.Flags.HasFlag(ChannelEntryFlags.HasRecommendLevel));
        Assert.Equal(41, ch11.RecommendedMinLevel);
        Assert.Equal(85, ch11.RecommendedMaxLevel);

        ChannelEntry ch52 = snapshot.Channels[1];
        Assert.Equal(2, ch52.ChannelNo);
        Assert.Equal(52, ch52.ParsedChannelNo);
        Assert.Equal("#ch.", ch52.DisplayPrefix);
        Assert.NotNull(ch52.SlotRule);
        Assert.Equal("[none]", ch52.SlotRule.AreaTag);
        Assert.False(ch52.Flags.HasFlag(ChannelEntryFlags.HasRecommendLevel));
        Assert.Null(ch52.RecommendedMinLevel);
        Assert.Null(ch52.RecommendedMaxLevel);
    }

    [Fact]
    public async Task Client_OutdatedServerBlockScript_MergesFallbackRecommendLevelsAndFullRate()
    {
        const string serverOnlyScript =
            "[server] 3\n" +
            "  11 <4::cname_syn_11> 0 `[zone_alpha]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "[/server]\n";
        ChannelScriptData fallback = ChannelScriptParser.Parse(
            "[channel rate full] 88.0\n" +
            "[channel] `[zone_alpha]` 41 85\n");

        (ChannelDirectoryLoadResult res, byte[] cmds) = await RunScenarioAsync(async srv =>
        {
            await srv.NegotiateSessionKeyAsync();
            await srv.ReadVersionRequestAsync();
            byte[] ver = new byte[20];
            Encoding.ASCII.GetBytes("v910").CopyTo(ver, 4);
            await srv.SendEncryptedAsync(0x06, ver, 32);
            await srv.ReadRequestAsync(0x09);
            await srv.SendCompressedEncryptedAsync(0x09, Encoding.UTF8.GetBytes(serverOnlyScript));
            await srv.ReadRequestAsync(0x01);
            byte[] list = BuildBinaryChannelList(new[]
            {
                ("siroco", new[] { ("#ch.11", 200, 10, "192.0.2.10", 19011) }),
            });
            await srv.SendCompressedEncryptedAsync(0x03, list);
        }, fallbackScript: fallback);

        Assert.True(res.Succeeded, res.ErrorDetail);
        Assert.Equal(new byte[] { 0x0b, 0x05, 0x09, 0x01 }, cmds);
        Assert.Equal(88.0f, res.Snapshot.ScriptData.FullThresholdPercent);
        ChannelEntry ch = Assert.Single(res.Snapshot.Channels);
        Assert.Equal(11, ch.ParsedChannelNo);
        Assert.NotNull(ch.SlotRule);
        Assert.Equal("cname_syn_11", ch.SlotRule.TitleStringKey);
        Assert.Equal(41, ch.RecommendedMinLevel);
        Assert.Equal(85, ch.RecommendedMaxLevel);
    }

    [Theory]
    [InlineData(0, false, ChannelSelectPlacement.MainGrid)]
    [InlineData(11, false, ChannelSelectPlacement.SpecialTower)]
    [InlineData(3, false, ChannelSelectPlacement.SpecialTrade)]
    [InlineData(4, true, ChannelSelectPlacement.PvpArena)]
    [InlineData(5, true, ChannelSelectPlacement.PvpArena)]
    [InlineData(6, true, ChannelSelectPlacement.PvpArena)]
    [InlineData(7, true, ChannelSelectPlacement.PvpArena)]
    [InlineData(8, true, ChannelSelectPlacement.PvpArena)]
    [InlineData(9, true, ChannelSelectPlacement.PvpArena)]
    [InlineData(10, true, ChannelSelectPlacement.PvpArena)]
    [InlineData(13, true, ChannelSelectPlacement.PvpArena)]
    [InlineData(14, true, ChannelSelectPlacement.PvpArena)]
    [InlineData(15, true, ChannelSelectPlacement.PvpArena)]
    [InlineData(16, true, ChannelSelectPlacement.PvpArena)]
    [InlineData(1, false, ChannelSelectPlacement.Hidden)]
    [InlineData(2, false, ChannelSelectPlacement.Hidden)]
    [InlineData(12, false, ChannelSelectPlacement.Hidden)]
    [InlineData(17, false, ChannelSelectPlacement.Hidden)]
    [InlineData(18, false, ChannelSelectPlacement.Hidden)]
    [InlineData(-1, false, ChannelSelectPlacement.Hidden)]
    public void ChannelServerSlotRule_ClassifyChannelKind_MatchesClientBucketTable(
        int channelKind,
        bool expectedPvp,
        ChannelSelectPlacement expectedPlacement)
    {
        Assert.Equal(expectedPvp, ChannelServerSlotRule.IsPvpChannelKind(channelKind));
        Assert.Equal(expectedPlacement, ChannelServerSlotRule.ClassifyChannelKind(channelKind));
        var rule = new ChannelServerSlotRule(3, 11, -1, string.Empty, "syn", channelKind, "[none]");
        Assert.Equal(expectedPlacement, rule.SelectPlacement);
    }

    [Fact]
    public void ChannelScriptData_HasServerSlotsForGroup_And_SnapshotPreservesPvpMaxLoad()
    {
        const string scriptText =
            "[server] 3\n" +
            "  11 <4::cname_syn_11> 0 `[zone_alpha]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  52 <4::cname_syn_52> 13 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "  53 <4::cname_syn_53> 8 `[none]` 0 0 0 0 0 0 `` `` `` 0 0\n" +
            "[/server]\n";
        ChannelScriptData script = ChannelScriptParser.Parse(scriptText);

        Assert.True(script.HasServerSlotsForGroup("siroco"));
        Assert.True(script.HasServerSlotsForGroup("希洛克服务器"));
        Assert.False(script.HasServerSlotsForGroup("cain"));
        Assert.False(script.HasServerSlotsForGroup("unknown_custom_group"));
        Assert.False(ChannelScriptData.Default.HasServerSlotsForGroup("siroco"));

        ChannelDirectorySnapshot snapshot = ChannelDirectorySnapshot.Build(
            "v920",
            false,
            script,
            new[]
            {
                WireGroup("siroco",
                    ("#ch.11", 200, 12, "192.0.2.10", 19011),
                    ("#ch.52", 200, 180, "192.0.2.10", 19052),
                    ("#ch.53", 200, 90, "192.0.2.10", 19053)),
            });

        ChannelGroup siroco = Assert.Single(snapshot.Groups);
        Assert.Equal(3, siroco.Channels.Count);
        Assert.Equal(180, siroco.MaxCurrentLoad);
        Assert.Equal(ChannelSelectPlacement.MainGrid, siroco.Channels[0].SlotRule.SelectPlacement);
        Assert.Equal(ChannelSelectPlacement.PvpArena, siroco.Channels[1].SlotRule.SelectPlacement);
        Assert.Equal(ChannelSelectPlacement.PvpArena, siroco.Channels[2].SlotRule.SelectPlacement);
    }

    private static async Task SendRawAsync(Stream stream, byte cmd, byte[] body)
    {
        byte[] packet = ChannelServiceFrameCodec.Build(cmd, body);
        await stream.WriteAsync(packet, 0, packet.Length);
        await stream.FlushAsync();
    }

    private sealed class OneByteReadStream : MemoryStream
    {
        public OneByteReadStream(byte[] data) : base(data, writable: false) { }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length > 1)
            {
                buffer = buffer[..1];
            }
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return base.ReadAsync(buffer, offset, Math.Min(count, 1), cancellationToken);
        }
    }

    private sealed class BlockingDuplexStream : Stream
    {
        private readonly BlockingPipe _rx;
        private readonly BlockingPipe _tx;

        private BlockingDuplexStream(BlockingPipe rx, BlockingPipe tx)
        {
            _rx = rx;
            _tx = tx;
        }

        public static (BlockingDuplexStream A, BlockingDuplexStream B) CreatePair()
        {
            var aToB = new BlockingPipe();
            var bToA = new BlockingPipe();
            return (new BlockingDuplexStream(bToA, aToB), new BlockingDuplexStream(aToB, bToA));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => _rx.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _rx.ReadAsync(buffer, offset, count, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _tx.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _tx.Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tx.Complete();
            }
            base.Dispose(disposing);
        }

        private sealed class BlockingPipe
        {
            private readonly object _gate = new object();
            private readonly Queue<byte> _queue = new Queue<byte>();
            private bool _completed;

            public void Write(byte[] buffer, int offset, int count)
            {
                lock (_gate)
                {
                    for (int i = 0; i < count; i++)
                    {
                        _queue.Enqueue(buffer[offset + i]);
                    }
                    Monitor.PulseAll(_gate);
                }
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                if (count == 0) return 0;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_completed)
                    {
                        Monitor.Wait(_gate, 50);
                    }
                    int read = 0;
                    while (read < count && _queue.Count > 0)
                    {
                        buffer[offset + read++] = _queue.Dequeue();
                    }
                    return read;
                }
            }

            public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                return Task.Run(() =>
                {
                    if (count == 0) return 0;
                    lock (_gate)
                    {
                        while (_queue.Count == 0 && !_completed && !ct.IsCancellationRequested)
                        {
                            Monitor.Wait(_gate, 50);
                        }
                        if (_queue.Count == 0 && ct.IsCancellationRequested)
                        {
                            ct.ThrowIfCancellationRequested();
                        }
                        int read = 0;
                        while (read < count && _queue.Count > 0)
                        {
                            buffer[offset + read++] = _queue.Dequeue();
                        }
                        return read;
                    }
                }, ct);
            }

            public void Complete()
            {
                lock (_gate)
                {
                    _completed = true;
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }
}
