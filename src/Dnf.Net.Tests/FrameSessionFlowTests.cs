using Dnf.Net.Frame;

namespace Dnf.Net.Tests;

/// <summary>
/// 会话建立的整条流程:先收启动包(固定种子变换)取出密钥块,再用它解后面的包、组前往服务端的包。
/// 启动包的线上字节来自服务端发包整条链在模拟器里的结果(其载荷由独立的构造代码按字段顺序写成)。
/// </summary>
public class FrameSessionFlowTests
{
    private static List<string[]> StartupRows()
    {
        return FrameVectorFile.Rows("frame_frames_vector.txt").Where(r => r[0] == "s2c" && r[11].StartsWith("CHANNELINFO")).ToList();
    }

    [Fact]
    public void Startup_frames_parse_and_the_key_block_opens_the_rest_of_the_stream()
    {
        List<string[]> startup = StartupRows();
        Assert.Equal(2, startup.Count);
        string[] with378 = startup.Single(r => r[5] == "378");
        string[] with408 = startup.Single(r => r[5] == "408");

        ServerFrameDecoder decoder = new ServerFrameDecoder();
        decoder.PlainNotiIds.Clear();

        // 没有会话密码时,启动包照样能解
        Assert.Equal(FrameStatus.Ok, decoder.Decode(FrameVectorFile.Hex(with378[13]), out DecodedFrame first));
        Assert.Equal(DecodedKind.Bootstrap, first.Kind);
        byte[] payload378 = FrameVectorFile.Hex(with378[6]);
        Assert.Equal(378 + 4, first.Payload.Length);
        Assert.Equal(PacketIntegrity.AppendFoldHash(payload378), first.Payload);
        Assert.Equal(ChannelInfoStatus.Ok, ChannelInfo.TryParse(first.Payload, 0, first.Payload.Length, out ChannelInfo info));
        Assert.Equal(FrameCipherDoubles.Key("SYNTH_FRAME_K1"), info.KeyBlock);
        // 载荷 378 字节,追加了折叠哈希:结尾剩的 4 字节就是它
        Assert.Equal(PacketIntegrity.AppendFoldHash(payload378).Skip(378), info.Trailing);

        // 载荷超过 400 字节:不追加哈希,结尾没有多余字节
        Assert.Equal(FrameStatus.Ok, decoder.Decode(FrameVectorFile.Hex(with408[13]), out DecodedFrame second));
        Assert.Equal(408, second.Payload.Length);
        Assert.Equal(ChannelInfoStatus.Ok, ChannelInfo.TryParse(second.Payload, 0, second.Payload.Length, out ChannelInfo info2));
        Assert.Equal(info.KeyBlock, info2.KeyBlock);
        Assert.Empty(info2.Trailing);
        Assert.Equal(2, info2.Addresses.Count);

        // 用取出的密钥块建会话密码,全部 240 个服务端帧(含启动包本身)都能解出来
        decoder.Cipher = new SessionCipher(info.KeyBlock, FrameCipherDoubles.OracleFactory("SYNTH_FRAME_K1"));
        int decoded = 0;
        foreach (string[] r in FrameVectorFile.Rows("frame_frames_vector.txt").Where(r => r[0] == "s2c"))
        {
            Assert.Equal(FrameStatus.Ok, decoder.Decode(FrameVectorFile.Hex(r[13]), out DecodedFrame d));
            decoded++;
        }
        Assert.Equal(240, decoded);
    }

    [Fact]
    public void Frames_built_with_the_key_block_round_trip_through_the_same_session_cipher()
    {
        byte[] key = FrameCipherDoubles.Key("SYNTH_FRAME_K1");
        SessionCipher cipher = new SessionCipher(key, s => new SliceXorCipher(s));
        SecondLayerSender sender = new SecondLayerSender();
        ClientFrameBuilder builder = new ClientFrameBuilder(cipher, sender) { AppendRequiredFoldHash = true };
        SecondLayerReceiver receiver = new SecondLayerReceiver();

        byte[] fields = Enumerable.Range(0, 40).Select(i => (byte)(i * 5 + 2)).ToArray();
        foreach (ushort id in new ushort[] { 1, 30, 31, 100, 367, 1000 })
        {
            Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, id, (ushort)(id + 1), fields, out byte[] frame));
            Assert.True(PacketHeader.TryRead(FrameLayout.ClientToServer, frame, out PacketHeader h));
            Assert.Equal((uint)frame.Length, h.TotalLength);
            byte[] body = frame.Skip(13).ToArray();

            // 服务端的做法:对密文验第二层,解密,再验校验字节
            Assert.True(receiver.Check(body, h.Check, PacketIntegrity.ResetsStateClientToServer(PacketClass.Cmd, id)));
            byte[] plain = cipher.Decrypt(id, body);
            Assert.Equal((byte)h.Check, ClientFrameBuilder.ComputeCheckByte(FrameLayout.ClientToServer, frame, plain));
            byte[] expectedPayload = PacketIntegrity.RequiresClientFoldHash(PacketClass.Cmd, id) ? PacketIntegrity.AppendFoldHash(fields) : fields;
            Assert.Equal(expectedPayload, plain.Take(expectedPayload.Length));
            Assert.All(plain.Skip(expectedPayload.Length), b => Assert.Equal((byte)0, b));
        }
        Assert.Equal(0, receiver.Mismatches);
    }

    private static Func<byte> CountingRandom()
    {
        int n = 0;
        return () => (byte)(n++ * 37 + 11);
    }

    [Fact]
    public void Server_oriented_builder_seals_the_second_layer_and_appends_the_fold_hash()
    {
        // 随机字节序列 11, 48, 85, 122, ...:每帧依次取 r1、r3(密文非空时不再取别的)
        ClientFrameBuilder builder = ClientFrameBuilder.CreateForServer(new NullPayloadCipher(), CountingRandom());
        SecondLayerReceiver receiver = new SecondLayerReceiver();
        byte[] fields = Enumerable.Range(0, 12).Select(i => (byte)(i + 1)).ToArray();

        // 包号 1 复位状态;包号 30 在折叠哈希名单里(见 PacketIntegrity 的 17 个包号),包号 32 不在
        foreach ((ushort id, bool folded) in new[] { ((ushort)1, false), ((ushort)30, true), ((ushort)32, false), ((ushort)30, true) })
        {
            Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, id, 7, fields, out byte[] frame));
            Assert.True(PacketHeader.TryRead(FrameLayout.ClientToServer, frame, out PacketHeader h));
            int expectedLength = fields.Length + (folded ? 4 : 0);
            // 空密码:密文就是载荷(含需要的折叠哈希),整帧 = 13 字节头 + 载荷
            Assert.Equal(13 + expectedLength, frame.Length);
            byte[] body = frame.Skip(13).ToArray();
            Assert.Equal(folded, body.Length == fields.Length + 4);
            // 高 3 字节有内容(随机字节不为 0),接收方按自己的状态核对通过
            Assert.NotEqual(0u, h.Check >> 8);
            Assert.True(receiver.Check(body, h.Check, PacketIntegrity.ResetsStateClientToServer(PacketClass.Cmd, id)));
        }
        Assert.Equal(0, receiver.Mismatches);
        // 4 帧 × 每帧 2 个随机字节,最后一个 r1 是第 7 个数:6*37+11 = 233
        Assert.Equal((byte)233, receiver.State);
    }

    [Fact]
    public void Plain_constructor_leaves_the_second_layer_empty_and_the_fold_hash_off()
    {
        // 反例:不走工厂时,校验字高 3 字节为 0,折叠哈希也不追加;接收方对这种字会判不符
        ClientFrameBuilder builder = new ClientFrameBuilder(new NullPayloadCipher());
        byte[] fields = { 1, 2, 3, 4 };
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 30, 1, fields, out byte[] frame));
        Assert.True(PacketHeader.TryRead(FrameLayout.ClientToServer, frame, out PacketHeader h));
        Assert.Equal(0u, h.Check >> 8);
        Assert.Equal(13 + fields.Length, frame.Length);

        SecondLayerReceiver receiver = new SecondLayerReceiver();
        // 高 3 字节为 0 时取回的 r1、h 都是 0;哈希核对 MakeHash(密文, 0x27) 与 0 相等的概率只有 1/256,这里的固定输入不相等
        Assert.False(receiver.Check(frame.Skip(13).ToArray(), h.Check, false));
        Assert.Equal(1, receiver.Mismatches);
    }

    [Fact]
    public void Tampered_body_fails_the_second_layer_but_state_still_advances()
    {
        // 篡改密文一个字节:接收方不符(服务端只计数、不拒收),状态仍推进到取回的 r1
        ClientFrameBuilder builder = ClientFrameBuilder.CreateForServer(new NullPayloadCipher(), CountingRandom());
        Assert.Equal(FrameStatus.Ok, builder.TryBuild(PacketClass.Cmd, 1, 1, new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 }, out byte[] frame));
        Assert.True(PacketHeader.TryRead(FrameLayout.ClientToServer, frame, out PacketHeader h));
        byte[] body = frame.Skip(13).ToArray();
        body[3] ^= 0x40;
        SecondLayerReceiver receiver = new SecondLayerReceiver();
        Assert.False(receiver.Check(body, h.Check, true));
        Assert.Equal(1, receiver.Mismatches);
        // 第一帧取的 r1 是序列的第一个数 11
        Assert.Equal((byte)11, receiver.State);
    }
}
