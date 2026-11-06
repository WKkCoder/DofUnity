using System.Buffers.Binary;
using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 进城后的心跳保活(E-306):会话在内存流上先收启动包,流里再排一条心跳通知(类 0、包号 0)与一条无关通知;
/// 保活泵只给心跳回一帧类 1、包号 0、载荷 8 字节全零,对端关流后泵自己停。回出的帧用会话密码解回来核对。
/// </summary>
public class OnlineHeartbeatTests
{
    private static string[] StartupRow()
    {
        return FrameVectorFile.Rows("frame_frames_vector.txt")
            .Single(r => r[0] == "s2c" && r[5] == "378" && r[11].StartsWith("CHANNELINFO"));
    }

    private static byte[] ServerFrame(ushort packetId, byte[] cipherBody)
    {
        uint crc = Crc32N.Compute(cipherBody);
        var frame = new byte[15 + cipherBody.Length];
        frame[0] = (byte)PacketClass.Noti;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(3), (uint)frame.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(11), crc);
        cipherBody.CopyTo(frame, 15);
        return frame;
    }

    [Fact]
    public async Task HeartbeatIsAnsweredOnceWithZeroChecksAndOtherFramesAreIgnored()
    {
        byte[] bootstrap = FrameVectorFile.Hex(StartupRow()[13]);
        var cipher = new SessionCipher(FrameCipherDoubles.Key("SYNTH_FRAME_K1"), s => new SliceXorCipher(s));
        byte[] heartbeat = ServerFrame(0, cipher.Encrypt(0, new byte[] { 0 }));
        byte[] other = ServerFrame(28, cipher.Encrypt(28, new byte[] { 0 }));
        byte[] third = ServerFrame(29, cipher.Encrypt(29, new byte[] { 0 }));

        using var stream = new MemoryStream();
        stream.Write(bootstrap, 0, bootstrap.Length);
        stream.Write(heartbeat, 0, heartbeat.Length);
        stream.Write(other, 0, other.Length);
        stream.Write(third, 0, third.Length);
        stream.Position = 0;

        var session = new GameClientSession(stream, s => new SliceXorCipher(s));
        Assert.True(await session.ReceiveBootstrapAsync(TimeSpan.FromSeconds(5)));

        int answered = await OnlineHeartbeat.RunAsync(session, CancellationToken.None);
        Assert.Equal(1, answered);
        Assert.True(session.PeerClosed);

        byte[] wire = stream.ToArray().Skip(bootstrap.Length + heartbeat.Length + other.Length + third.Length).ToArray();
        Assert.True(PacketHeader.TryRead(FrameLayout.ClientToServer, wire, out PacketHeader header));
        Assert.Equal(PacketClass.Cmd, header.Class);
        Assert.Equal(0, header.PacketId);
        var back = new SessionCipher(session.Channel.KeyBlock, s => new SliceXorCipher(s));
        byte[] plain = back.Decrypt(0, wire.Skip(FrameLayout.ClientToServer.HeaderLength).ToArray());
        Assert.Equal(new byte[8], plain.Take(8).ToArray());
        Assert.All(plain.Skip(8), b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public async Task NoHeartbeatNoReplyAndClosedStreamEndsThePump()
    {
        byte[] bootstrap = FrameVectorFile.Hex(StartupRow()[13]);
        using var stream = new MemoryStream();
        stream.Write(bootstrap, 0, bootstrap.Length);
        stream.Position = 0;

        var session = new GameClientSession(stream, s => new SliceXorCipher(s));
        Assert.True(await session.ReceiveBootstrapAsync(TimeSpan.FromSeconds(5)));

        int answered = await OnlineHeartbeat.RunAsync(session, CancellationToken.None);
        Assert.Equal(0, answered);
        Assert.Equal(stream.Length, bootstrap.Length);   // 一字节回包都没写
    }

    [Fact]
    public void HeartbeatShapeIsFixed()
    {
        Assert.Equal(8, OnlineHeartbeat.ReplyPayload.Length);
        Assert.All(OnlineHeartbeat.ReplyPayload, b => Assert.Equal((byte)0, b));
        Assert.Equal(0, OnlineHeartbeat.HeartbeatPacketId);
        Assert.Equal(0, OnlineHeartbeat.ReplyPacketId);
    }
}
