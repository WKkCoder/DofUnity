using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 客户端会话在内存流上的整条接缝:先收启动包取出密钥块,再发登录帧、收会话帧。
/// 启动包与会话帧的线上字节来自服务端发包整条链在模拟器里的结果(同 <see cref="FrameSessionFlowTests"/> 的向量),
/// 发出的登录帧用同样的会话密码解回来核对;事件记录里只许有方向、包号与长度,不许有载荷内容。
/// </summary>
public class GameClientSessionTests
{
    private static string[] StartupRow()
    {
        return FrameVectorFile.Rows("frame_frames_vector.txt")
            .Single(r => r[0] == "s2c" && r[5] == "378" && r[11].StartsWith("CHANNELINFO"));
    }

    [Fact]
    public async Task BootstrapInitializesTheSessionAndTheLoginFrameDecryptsBack()
    {
        byte[] bootstrap = FrameVectorFile.Hex(StartupRow()[13]);
        using var stream = new MemoryStream();
        stream.Write(bootstrap, 0, bootstrap.Length);
        stream.Position = 0;

        var session = new GameClientSession(stream, s => new SliceXorCipher(s));
        Assert.False(session.IsReady);
        Assert.True(await session.ReceiveBootstrapAsync(TimeSpan.FromSeconds(5)));
        Assert.True(session.IsReady);
        Assert.False(session.Rejected);
        Assert.Equal(FrameCipherDoubles.Key("SYNTH_FRAME_K1"), session.Channel.KeyBlock);

        byte[] body = new LoginRequest
        {
            PatchTime = 60,
            Token = Encoding.ASCII.GetBytes(new string('A', 344)),
            UserName = Encoding.ASCII.GetBytes("tester"),
            MouseCipher = new byte[8],
            DataVersion = 1,
            ServerGroup = 0,
        }.Build();
        Assert.Equal(FrameStatus.Ok, await session.SendAsync(PacketClass.Cmd, LoginRequest.PacketId, body));

        byte[] wire = stream.ToArray().Skip(bootstrap.Length).ToArray();
        Assert.True(PacketHeader.TryRead(FrameLayout.ClientToServer, wire, out PacketHeader header));
        Assert.Equal(PacketClass.Cmd, header.Class);
        Assert.Equal(LoginRequest.PacketId, header.PacketId);
        Assert.Equal((uint)wire.Length, header.TotalLength);

        var cipher = new SessionCipher(session.Channel.KeyBlock, s => new SliceXorCipher(s));
        byte[] plain = cipher.Decrypt(LoginRequest.PacketId, wire.Skip(FrameLayout.ClientToServer.HeaderLength).ToArray());
        byte[] expected = body;                                            // 登录包不在追加折叠哈希的 17 个包号里,原样加密
        Assert.Equal(expected, plain.Take(expected.Length).ToArray());
        Assert.All(plain.Skip(expected.Length), b => Assert.Equal((byte)0, b));

        Assert.Equal(2, session.Events.Count);
        Assert.False(session.Events[0].Outbound);
        Assert.True(session.Events[1].Outbound);
        Assert.DoesNotContain("AAAA", session.Events[1].ToString());       // 记录里没有令牌内容
    }

    [Fact]
    public async Task SessionFramesArrivingWithTheBootstrapDecodeAfterIt()
    {
        string[] startup = StartupRow();
        string[] sessionRow = FrameVectorFile.Rows("frame_frames_vector.txt")
            .First(r => r[0] == "s2c" && !r[11].StartsWith("CHANNELINFO") && r[6].Length >= 64);   // 跳过空载荷的小帧
        byte[] bootstrap = FrameVectorFile.Hex(startup[13]);
        byte[] sessionFrame = FrameVectorFile.Hex(sessionRow[13]);
        using var stream = new MemoryStream();
        stream.Write(bootstrap, 0, bootstrap.Length);
        stream.Write(sessionFrame, 0, sessionFrame.Length);
        stream.Position = 0;

        var session = new GameClientSession(stream, FrameCipherDoubles.OracleFactory("SYNTH_FRAME_K1"));
        Assert.True(await session.ReceiveBootstrapAsync(TimeSpan.FromSeconds(5)));
        List<DecodedFrame> frames = await session.ReceiveAsync(TimeSpan.FromSeconds(5), stopAfterFrames: 1);
        DecodedFrame frame = Assert.Single(frames);
        byte[] expectedPayload = FrameVectorFile.Hex(sessionRow[6]);
        Assert.Equal(expectedPayload, frame.Payload.Take(expectedPayload.Length).ToArray());
    }

    [Fact]
    public async Task AnEmptyStreamIsNotABootstrap()
    {
        using var stream = new MemoryStream();
        var session = new GameClientSession(stream, s => new SliceXorCipher(s));
        Assert.False(await session.ReceiveBootstrapAsync(TimeSpan.FromMilliseconds(200)));
        Assert.False(session.IsReady);
        Assert.True(session.PeerClosed);
    }

    [Fact]
    public async Task SendingBeforeTheBootstrapThrows()
    {
        using var stream = new MemoryStream();
        var session = new GameClientSession(stream, s => new SliceXorCipher(s));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.SendAsync(PacketClass.Cmd, LoginRequest.PacketId, new byte[] { 1 }));
    }
}
