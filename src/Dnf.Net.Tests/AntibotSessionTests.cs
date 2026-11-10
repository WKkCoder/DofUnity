using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.AntibotTestSupport;

namespace Dnf.Net.Tests;

/// <summary>
/// 会话层对 561 号信封的接线:信封拆得开,就把内层完整帧作为一个解码结果紧跟在信封帧之后;拆不开或内层不足一个帧头,信封帧照常保留,
/// 只在事件里留一条说明。信封一律按规格手工拼(算法 16 的密文区,与具体块算法无关),帧用测试里的合成帧头与校验。
/// </summary>
public class AntibotSessionTests
{
    private static byte[] Bootstrap()
    {
        string[] row = FrameVectorFile.Rows("frame_frames_vector.txt")
            .Single(r => r[0] == "s2c" && r[5] == "378" && r[11].StartsWith("CHANNELINFO"));
        return FrameVectorFile.Hex(row[13]);
    }

    /// <summary>服务端到客户端的明文帧:15 字节帧头(类、包号、总长、两个校验)加载荷。</summary>
    private static byte[] ServerFrame(byte packetClass, ushort packetId, byte[] body)
    {
        uint crc = Crc32N.Compute(body);
        byte[] frame = new byte[15 + body.Length];
        frame[0] = packetClass;
        frame[1] = (byte)packetId;
        frame[2] = (byte)(packetId >> 8);
        WriteLittleEndian32(frame, 3, (uint)frame.Length);
        WriteLittleEndian32(frame, 7, crc);
        WriteLittleEndian32(frame, 11, crc);
        body.CopyTo(frame, 15);
        return frame;
    }

    private static byte[] EnvelopeFrame(byte[] inner)
    {
        return ServerFrame(0, 561, ValidEnvelope(16, SeqA, Concat(AlgorithmPacket(4), inner)));
    }

    private static async Task<GameClientSession> OpenAsync(params byte[][] frames)
    {
        MemoryStream stream = new();
        byte[] bootstrap = Bootstrap();
        stream.Write(bootstrap, 0, bootstrap.Length);
        foreach (byte[] frame in frames)
        {
            stream.Write(frame, 0, frame.Length);
        }

        stream.Position = 0;
        GameClientSession session = new(stream, s => new SliceXorCipher(s));
        Assert.True(await session.ReceiveBootstrapAsync(TimeSpan.FromSeconds(5)));
        return session;
    }

    [Fact]
    public async Task AnUnwrappableEnvelopeAddsTheInnerFrameRightAfterIt()
    {
        byte[] innerBody = Synth(11, 33);
        byte[] innerFrame = ServerFrame(1, 0x1234, innerBody);
        byte[] envelope = ValidEnvelope(16, SeqA, Concat(AlgorithmPacket(4), innerFrame));
        GameClientSession session = await OpenAsync(ServerFrame(0, 561, envelope));

        List<DecodedFrame> frames = await session.ReceiveAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, frames.Count);
        DecodedFrame outer = frames[0];
        Assert.Equal(DecodedKind.Plain, outer.Kind);
        Assert.Equal(PacketClass.Noti, outer.Header.Class);
        Assert.Equal(561, outer.Header.PacketId);
        Assert.Equal(envelope, outer.Payload);

        DecodedFrame inner = frames[1];
        Assert.Equal(DecodedKind.Plain, inner.Kind);
        Assert.Equal(PacketClass.Cmd, inner.Header.Class);
        Assert.Equal(0x1234, inner.Header.PacketId);
        Assert.Equal((uint)innerFrame.Length, inner.Header.TotalLength);
        Assert.Equal(innerBody, inner.Payload);
        Assert.False(inner.WasCompressed);

        Assert.Equal(3, session.Events.Count);
        SessionEvent unwrapped = session.Events[2];
        Assert.False(unwrapped.Outbound);
        Assert.Equal(PacketClass.Cmd, unwrapped.PacketClass);
        Assert.Equal(0x1234, unwrapped.PacketId);
        Assert.Equal(innerFrame.Length, unwrapped.FrameLength);
        Assert.Equal(innerBody.Length, unwrapped.PayloadLength);
        Assert.Equal("信封内层", unwrapped.Note);
    }

    [Fact]
    public async Task EnvelopesAreExpandedInArrivalOrder()
    {
        byte[] firstBody = Synth(1, 20);
        byte[] secondBody = Synth(2, 41);
        GameClientSession session = await OpenAsync(
            EnvelopeFrame(ServerFrame(1, 0x0101, firstBody)),
            EnvelopeFrame(ServerFrame(0, 0x0202, secondBody)));

        List<DecodedFrame> frames = await session.ReceiveAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 561, 0x0101, 561, 0x0202 }, frames.Select(f => (int)f.Header.PacketId).ToArray());
        Assert.Equal(firstBody, frames[1].Payload);
        Assert.Equal(secondBody, frames[3].Payload);
    }

    [Fact]
    public async Task TheInnerFrameComesBackTogetherWithItsEnvelopeEvenWhenOneFrameWasAskedFor()
    {
        GameClientSession session = await OpenAsync(
            EnvelopeFrame(ServerFrame(1, 0x0101, Synth(1, 20))),
            EnvelopeFrame(ServerFrame(1, 0x0202, Synth(2, 20))));

        List<DecodedFrame> first = await session.ReceiveAsync(TimeSpan.FromSeconds(5), stopAfterFrames: 1);
        Assert.Equal(new[] { 561, 0x0101 }, first.Select(f => (int)f.Header.PacketId).ToArray());

        List<DecodedFrame> second = await session.ReceiveAsync(TimeSpan.FromSeconds(5), stopAfterFrames: 1);
        Assert.Equal(new[] { 561, 0x0202 }, second.Select(f => (int)f.Header.PacketId).ToArray());
    }

    [Fact]
    public async Task AnEnvelopeThatDoesNotUnwrapIsKeptAloneAndTheFailureIsRecorded()
    {
        byte[] plain = Concat(AlgorithmPacket(4), ServerFrame(1, 0x1234, Synth(3, 30)));
        byte[] envelope = new Spec { CipherAlgo = 16, AlgoField = 16, Plain = plain, Crc = Crc32(plain) ^ 1 }.ToBytes();
        GameClientSession session = await OpenAsync(ServerFrame(0, 561, envelope));

        List<DecodedFrame> frames = await session.ReceiveAsync(TimeSpan.FromSeconds(5));

        DecodedFrame only = Assert.Single(frames);
        Assert.Equal(561, only.Header.PacketId);
        Assert.Equal(envelope, only.Payload);
        Assert.Equal(3, session.Events.Count);
        Assert.False(session.Events[2].Outbound);
        Assert.Equal(PacketClass.Noti, session.Events[2].PacketClass);
        Assert.Equal(561, session.Events[2].PacketId);
        Assert.Equal(15 + envelope.Length, session.Events[2].FrameLength);
        Assert.Contains(AntibotEnvelopeStatus.CrcMismatch.ToString(), session.Events[2].Note);
    }

    [Fact]
    public async Task AnInnerPacketShorterThanAFrameHeaderIsOnlyReportedInTheEvent()
    {
        byte[] inner = InnerPacket(1, 0x0BCD, 3, 9);                      // 10 字节:类、包号、总长齐全,但不够 15 字节的帧头
        Assert.Equal(10, inner.Length);
        GameClientSession session = await OpenAsync(EnvelopeFrame(inner));

        List<DecodedFrame> frames = await session.ReceiveAsync(TimeSpan.FromSeconds(5));

        DecodedFrame only = Assert.Single(frames);
        Assert.Equal(561, only.Header.PacketId);
        Assert.Equal(3, session.Events.Count);
        SessionEvent note = session.Events[2];
        Assert.Equal(PacketClass.Cmd, note.PacketClass);
        Assert.Equal(0x0BCD, note.PacketId);
        Assert.Equal(10, note.FrameLength);
        Assert.Contains("未展开", note.Note);
    }

    [Fact]
    public async Task OnlyOneLevelIsUnwrapped()
    {
        byte[] nested = ServerFrame(0, 561, Synth(5, 24));                // 内层自己也是 561 号包:作为普通结果交出,不再往里拆
        GameClientSession session = await OpenAsync(EnvelopeFrame(nested));

        List<DecodedFrame> frames = await session.ReceiveAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, frames.Count);
        Assert.Equal(561, frames[1].Header.PacketId);
        Assert.Equal(3, session.Events.Count);
    }
}
