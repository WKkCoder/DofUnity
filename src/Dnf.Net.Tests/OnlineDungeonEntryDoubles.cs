using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 进副本编排测试用的合成帧拼装(服务端到客户端方向:15 字节头 + 体,两个校验值都是对体算的 crc32N)与合成载荷。
/// 会话加密用密钥切片异或的替身(<see cref="SliceXorCipher"/>)与合成密钥,启动包取自既有的合成帧向量;不含任何真实抓包内容。
/// </summary>
internal static class DungeonEntryWire
{
    public const string KeyId = "SYNTH_FRAME_K1";

    /// <summary>向量文件里的合成启动包(类 0、包号 1,内含 <see cref="KeyId"/> 对应的密钥块),同 <c>GameClientSessionTests</c> 的取法。</summary>
    public static byte[] BootstrapFrame()
    {
        string[] row = FrameVectorFile.Rows("frame_frames_vector.txt")
            .Single(r => r[0] == "s2c" && r[5] == "378" && r[11].StartsWith("CHANNELINFO"));
        return FrameVectorFile.Hex(row[13]);
    }

    /// <summary>与会话里同一把密钥、同一套替身的会话密码(每次新建一个,不跨线程共用)。</summary>
    public static SessionCipher Cipher()
    {
        return new SessionCipher(FrameCipherDoubles.Key(KeyId), s => new SliceXorCipher(s));
    }

    /// <summary>小端 32 位整数写入,显式移位。</summary>
    public static void PutU32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    /// <summary>头(类、包号、整帧长度、校验 a、校验 b)加体;两个校验值都取体的 crc32N,低字节相等即可通过解码。</summary>
    public static byte[] Frame(byte cls, ushort id, byte[] body)
    {
        uint crc = Crc32N.Compute(body);
        byte[] frame = new byte[15 + body.Length];
        frame[0] = cls;
        frame[1] = (byte)id;
        frame[2] = (byte)(id >> 8);
        PutU32(frame, 3, (uint)frame.Length);
        PutU32(frame, 7, crc);
        PutU32(frame, 11, crc);
        body.CopyTo(frame, 15);
        return frame;
    }

    /// <summary>会话加密的通知帧(类 0):载荷补零到槽的块长后加密,与真会话的解码路径一致。</summary>
    public static byte[] Notice(ushort id, byte[] payload)
    {
        return Frame(0, id, Cipher().Encrypt(id, payload));
    }

    /// <summary>会话加密的命令回包帧(类 1)。</summary>
    public static byte[] Reply(ushort id, byte[] payload)
    {
        return Frame(1, id, Cipher().Encrypt(id, payload));
    }

    /// <summary>561 信封帧(类 0、不走会话加密):体就是信封载荷。</summary>
    public static byte[] Envelope(byte[] sealedBody)
    {
        return Frame(0, 561, sealedBody);
    }

    /// <summary>"内层帧":解包委托要返回的东西,完整的 15 字节头加载荷(不加密、不补字节)。</summary>
    public static byte[] Inner(byte cls, ushort id, byte[] payload)
    {
        return Frame(cls, id, payload);
    }

    /// <summary>接受阶段的拒绝帧:类 1、包号 1、整帧 21 字节的明文帧(载荷 0x00、0x04 加 4 字节折叠哈希)。</summary>
    public static byte[] Refusal()
    {
        byte[] body = PacketIntegrity.AppendFoldHash(new byte[] { 0, 4 });
        Assert.Equal(6, body.Length);
        return Frame(1, 1, body);
    }

    public static byte[] Join(params byte[][] parts)
    {
        return parts.SelectMany(p => p).ToArray();
    }

    /// <summary>
    /// 直接造一个解码好的帧(给假会话接缝用):走真解码器、空密码替身,载荷原样,长度不补字节。
    /// <see cref="DecodedFrame"/> 的构造函数不公开,只能从字节解码得到。
    /// </summary>
    public static DecodedFrame Decoded(byte cls, ushort id, byte[] payload)
    {
        var decoder = new ServerFrameDecoder { Cipher = new NullPayloadCipher() };
        FrameStatus status = decoder.Decode(Frame(cls, id, payload), out DecodedFrame frame);
        Assert.Equal(FrameStatus.Ok, status);
        return frame;
    }

    /// <summary>27 的合成载荷:48 字节(槽 13 块长 16 的整数倍,会话加密不补字节),内容不透明,编排只收原始字节。</summary>
    public static byte[] Loading27()
    {
        return GatewayTestSupport.Pattern(48, 0xA0);
    }

    /// <summary>28 的合成载荷,按 <c>DungeonInfoParser</c> 读序:u16 副本号、B1..B4、C1、C2、D、n=2、两组 E、u16 F、G,共 17 字节。</summary>
    public static byte[] Info28(int dungeonId = 901)
    {
        return new NoticePayloadBuilder()
            .U16(dungeonId).U8(1).U8(2).U8(3).U8(4).U8(5).U8(6).U8(7).U8(2)
            .U8(0x10).U8(0x20).U8(0x11).U8(0x21)
            .U16(903).U8(9)
            .ToArray();
    }

    /// <summary>读不下来的 28:个数声明 200 组,后面一个字节都没有(补零后也读不下来)。</summary>
    public static byte[] BrokenInfo28()
    {
        return new NoticePayloadBuilder().U16(901).U8(1).U8(2).U8(3).U8(4).U8(5).U8(6).U8(7).U8(200).ToArray();
    }

    /// <summary>29 的合成载荷(地图类型 0 的短形):格 (3,4)、flagA 0、种子、全局 1、2、类型 0、K=0、玩家序号 0,共 12 字节。</summary>
    public static byte[] Map29(int seed = 90001)
    {
        return new NoticePayloadBuilder()
            .U8(3).U8(4).U8(0).I32(seed).U8(1).U8(2).U8(0)
            .U8(0).U8(0)
            .ToArray();
    }

    /// <summary>读不下来的 29:地图类型 1 且声明 200 条怪记录,后面没有数据。</summary>
    public static byte[] BrokenMap29()
    {
        return new NoticePayloadBuilder().U8(3).U8(4).U8(0).I32(90001).U8(1).U8(2).U8(1).U16(905).U8(200).ToArray();
    }
}

/// <summary>客户端写出的一帧,已按会话密码解回明文(含补的 0x00)。</summary>
internal sealed class DungeonEntryClientFrame
{
    public DungeonEntryClientFrame(int index, PacketClass packetClass, ushort id, int wireLength, byte[] plain)
    {
        Index = index;
        Class = packetClass;
        Id = id;
        WireLength = wireLength;
        Plain = plain;
    }

    /// <summary>从 0 起的第几帧。</summary>
    public int Index { get; }

    public PacketClass Class { get; }

    public ushort Id { get; }

    /// <summary>整帧字节数(含 13 字节头)。</summary>
    public int WireLength { get; }

    /// <summary>解密后的明文,会话加密补齐的 0x00 仍在末尾。</summary>
    public byte[] Plain { get; }

    public static DungeonEntryClientFrame Parse(int index, byte[] wire)
    {
        Assert.True(PacketHeader.TryRead(FrameLayout.ClientToServer, wire, out PacketHeader header));
        Assert.Equal((uint)wire.Length, header.TotalLength);
        byte[] body = wire.Skip(FrameLayout.ClientToServer.HeaderLength).ToArray();
        byte[] plain = DungeonEntryWire.Cipher().Decrypt(header.PacketId, body);
        Assert.NotNull(plain);
        return new DungeonEntryClientFrame(index, header.Class, header.PacketId, wire.Length, plain);
    }
}

/// <summary>
/// 内存里的双向管道,接在真 <see cref="GameClientSession"/> 下面当网络流:服务端方向的字节由 <see cref="Push"/> 灌入(读端没数据时挂起,
/// 数据到了或 <see cref="CloseServer"/> 才返回,与真套接字的读语义一致);客户端写出的每一帧被解码记录,并交给 <see cref="Script"/>,
/// 由脚本决定"服务端"当场回放什么(同步回放,所以没有时序竞争)。每次 <c>WriteAsync</c> 恰好是一整帧(会话一帧一写)。
/// </summary>
internal sealed class DungeonEntryPipe : Stream
{
    private readonly object _gate = new object();
    private readonly List<byte> _inbound = new List<byte>();
    private readonly List<DungeonEntryClientFrame> _client = new List<DungeonEntryClientFrame>();
    private TaskCompletionSource<int> _pending;
    private byte[] _pendingBuffer;
    private int _pendingOffset;
    private int _pendingCount;
    private bool _closed;

    /// <summary>收到客户端一帧时调用,返回要立即回放给客户端的服务端字节块(可为 null 或空);脚本里也可以直接调 Push 与 CloseServer。</summary>
    public Func<DungeonEntryClientFrame, IEnumerable<byte[]>> Script { get; set; }

    /// <summary>非 null 时,客户端写帧会抛这个异常(模拟连接已坏)。</summary>
    public Exception WriteFailure { get; set; }

    /// <summary>目前为止客户端写出的所有帧(快照)。</summary>
    public IReadOnlyList<DungeonEntryClientFrame> ClientFrames
    {
        get
        {
            lock (_gate)
            {
                return _client.ToArray();
            }
        }
    }

    /// <summary>服务端往客户端发一段字节(读端挂着就直接交给它)。</summary>
    public void Push(byte[] bytes)
    {
        TaskCompletionSource<int> wake = null;
        int woken = 0;
        lock (_gate)
        {
            if (_closed)
            {
                throw new InvalidOperationException("管道的服务端已经关闭");
            }
            _inbound.AddRange(bytes);
            if (_pending != null)
            {
                int n = Math.Min(_pendingCount, _inbound.Count);
                _inbound.CopyTo(0, _pendingBuffer, _pendingOffset, n);
                _inbound.RemoveRange(0, n);
                wake = _pending;
                woken = n;
                _pending = null;
            }
        }
        wake?.SetResult(woken);
    }

    /// <summary>服务端关闭连接:已灌入的字节读完之后,读端读到 0(流结束)。</summary>
    public void CloseServer()
    {
        TaskCompletionSource<int> wake = null;
        lock (_gate)
        {
            _closed = true;
            if (_pending != null)
            {
                wake = _pending;
                _pending = null;
            }
        }
        wake?.SetResult(0);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("会话只用异步读");
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_inbound.Count > 0)
            {
                int n = Math.Min(count, _inbound.Count);
                _inbound.CopyTo(0, buffer, offset, n);
                _inbound.RemoveRange(0, n);
                return Task.FromResult(n);
            }
            if (_closed)
            {
                return Task.FromResult(0);
            }
            _pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingBuffer = buffer;
            _pendingOffset = offset;
            _pendingCount = count;
            return _pending.Task;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (WriteFailure != null)
        {
            throw WriteFailure;
        }
        byte[] wire = new byte[count];
        Array.Copy(buffer, offset, wire, 0, count);
        DungeonEntryClientFrame frame;
        lock (_gate)
        {
            frame = DungeonEntryClientFrame.Parse(_client.Count, wire);
            _client.Add(frame);
        }
        IEnumerable<byte[]> replies = Script?.Invoke(frame);
        if (replies != null)
        {
            foreach (byte[] bytes in replies)
            {
                Push(bytes);
            }
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 会话接缝的假实现:收帧从预先排好的队里出(队空就等满窗口,与真会话"到点空手返回"一致),
/// 每次发送可以指定帧层状态或抛异常,并在发送成功后往收件队里追加帧。用来造真会话造不出的情形(帧层编码失败、发送时流已断)。
/// </summary>
internal sealed class DungeonEntryFakePort : IOnlineDungeonSession
{
    private readonly Queue<DecodedFrame> _inbox = new Queue<DecodedFrame>();

    public bool Ready { get; set; } = true;

    public bool Closed { get; set; }

    public FrameError Fault { get; set; }

    /// <summary>为 true 时一次收帧把队里的帧全交出去(无视 stopAfterFrames),用来检验一批帧里的处理。</summary>
    public bool ReturnWholeInbox { get; set; }

    /// <summary>每次发送(含失败的)的记录。</summary>
    public List<(PacketClass Class, ushort Id, byte[] Payload)> Sent { get; } = new List<(PacketClass, ushort, byte[])>();

    /// <summary>收帧被调用的次数。</summary>
    public int ReceiveCalls { get; private set; }

    /// <summary>第 n 次发送(从 0 起)返回的帧层状态。</summary>
    public Func<int, FrameStatus> StatusOfSend { get; set; } = index => FrameStatus.Ok;

    /// <summary>第 n 次发送要抛的异常(null 为不抛)。</summary>
    public Func<int, Exception> FailureOfSend { get; set; } = index => null;

    /// <summary>第 n 次发送成功后追加到收件队里的帧。</summary>
    public Func<int, ushort, DecodedFrame[]> OnSent { get; set; }

    public bool IsReady => Ready;

    public bool PeerClosed => Closed;

    public FrameError LastFrameError => Fault;

    public void Enqueue(params DecodedFrame[] frames)
    {
        foreach (DecodedFrame frame in frames)
        {
            _inbox.Enqueue(frame);
        }
    }

    public Task<FrameStatus> SendAsync(PacketClass packetClass, ushort packetId, byte[] payload, CancellationToken cancellationToken)
    {
        int index = Sent.Count;
        Sent.Add((packetClass, packetId, (byte[])payload.Clone()));
        Exception failure = FailureOfSend(index);
        if (failure != null)
        {
            throw failure;
        }
        FrameStatus status = StatusOfSend(index);
        if (status == FrameStatus.Ok && OnSent != null)
        {
            DecodedFrame[] more = OnSent(index, packetId);
            if (more != null)
            {
                Enqueue(more);
            }
        }
        return Task.FromResult(status);
    }

    public async Task<List<DecodedFrame>> ReceiveAsync(TimeSpan window, int stopAfterFrames, CancellationToken cancellationToken)
    {
        ReceiveCalls++;
        var result = new List<DecodedFrame>();
        while (_inbox.Count > 0 && (ReturnWholeInbox || stopAfterFrames <= 0 || result.Count < stopAfterFrames))
        {
            result.Add(_inbox.Dequeue());
        }
        if (result.Count > 0 || Closed || Fault != FrameError.None || window <= TimeSpan.Zero)
        {
            return result;
        }
        await Task.Delay(window, cancellationToken);
        return result;
    }
}

/// <summary>
/// 固定映射的假解包委托:<see cref="Seal"/> 造出看不出内容的 33 字节"信封载荷"并登记它对应的内层帧,
/// <see cref="Unwrap"/> 按载荷查表(查不到返回 null),同时记下被问到的每个载荷。没有任何解密。
/// </summary>
internal sealed class DungeonEntryEnvelopeBook
{
    private readonly Dictionary<string, byte[]> _inner = new Dictionary<string, byte[]>();

    /// <summary>委托被调用时拿到的信封载荷,按调用顺序。</summary>
    public List<byte[]> Seen { get; } = new List<byte[]>();

    /// <summary>登记并返回一个不透明的信封载荷;<paramref name="tag"/> 不同,载荷就不同。</summary>
    public byte[] Seal(byte[] inner, int tag)
    {
        byte[] opaque = GatewayTestSupport.Pattern(33, tag * 5, 7);
        string key = Convert.ToHexString(opaque);
        Assert.False(_inner.ContainsKey(key), "同一个 tag 不能封两次");
        _inner[key] = inner;
        return opaque;
    }

    public byte[] Unwrap(byte[] payload)
    {
        Seen.Add(payload);
        return _inner.TryGetValue(Convert.ToHexString(payload), out byte[] inner) ? inner : null;
    }
}
