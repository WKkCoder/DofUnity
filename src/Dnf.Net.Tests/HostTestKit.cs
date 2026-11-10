using System.Diagnostics;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 宿主接线相关测试(进城后半段、进副本薄封装、日志行)共用的小工具:真会话接内存管道、脚本化的"服务端"、合成的登录回包与角色列表通知、
/// 真信封(反机器人算法 16 + 合成序号)封内层帧。所有编号都是合成值(账号号 905、副本号 901 起、无关帧包号 900 以上),不含任何真实数据。
/// </summary>
internal static class HostTestKit
{
    /// <summary>合成的账号号(登录成功回包里的 i32);小端字节 89 03。</summary>
    public const int AccountId = 905;

    /// <summary>
    /// 城镇各窗口用的短窗口(合成脚本的回包是同步灌进管道的,窗口只是"最多等多久",所以不必长;也别太短,免得慢机器上还没读就到点)。
    /// </summary>
    public static OnlineTownWindows TownWindows { get; } = new OnlineTownWindows(
        TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(200));

    public static async Task<(GameClientSession Session, DungeonEntryPipe Pipe)> ReadySession()
    {
        var pipe = new DungeonEntryPipe();
        pipe.Push(DungeonEntryWire.BootstrapFrame());
        var session = new GameClientSession(pipe, s => new SliceXorCipher(s));
        Assert.True(await session.ReceiveBootstrapAsync(TimeSpan.FromSeconds(5)));
        return (session, pipe);
    }

    /// <summary>
    /// 登录成功回包的载荷(与 <c>OnlineTownEntryTests</c> 里同一布局:首字节 1、i32 名长、名字、8 字节、i32 账号号、7 字节尾),
    /// 名字是合成的 ASCII。
    /// </summary>
    public static byte[] LoginSuccess(string name = "n900", int accountId = AccountId)
    {
        byte[] payload = new byte[5 + name.Length + 8 + 4 + 7];
        payload[0] = 1;
        BitConverter.TryWriteBytes(payload.AsSpan(1, 4), name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            payload[5 + i] = (byte)name[i];
        }
        BitConverter.TryWriteBytes(payload.AsSpan(5 + name.Length + 8, 4), accountId);
        return payload;
    }

    /// <summary>三个条目的模式 2 角色列表(明文):条目 i 的等级 61+i、职业 50+i、成长类型字节 0x25+i(低 4 位 5+i)。</summary>
    public static byte[] ListPayload()
    {
        return UserInfoTestData.Packet(UserInfoTestData.Standard(0), UserInfoTestData.Standard(1), UserInfoTestData.Standard(2));
    }

    /// <summary>直接发来的角色列表通知:会话加密、包号 2(压缩包号,载荷经 zlib),会话解码后载荷就是明文。</summary>
    public static byte[] DirectListNotice(byte[] plainList = null)
    {
        return DungeonEntryWire.Notice(2, UserInfoTestData.Zlib(plainList ?? ListPayload()));
    }

    /// <summary>
    /// 一个真信封(561,类 0、不走会话加密)封着一个内层帧:信封载荷 = 反机器人信封(算法 16、合成序号),
    /// 明文是算法包加完整的内层帧(15 字节头 + 载荷)。会话层会拆开它,把内层帧紧跟在信封帧后面交付(载荷原样,不解压)。
    /// </summary>
    public static byte[] RealEnvelope(byte cls, ushort id, byte[] payload)
    {
        byte[] plain = AntibotTestSupport.Concat(AntibotTestSupport.AlgorithmPacket(8), DungeonEntryWire.Inner(cls, id, payload));
        return DungeonEntryWire.Envelope(AntibotTestSupport.ValidEnvelope(16, AntibotTestSupport.SeqA, plain));
    }

    /// <summary>真信封的载荷本身(不含外层 561 帧头):给直接测解包委托、或造假端口的 561 帧用。</summary>
    public static byte[] RealEnvelopePayload(byte cls, ushort id, byte[] payload)
    {
        byte[] plain = AntibotTestSupport.Concat(AntibotTestSupport.AlgorithmPacket(8), DungeonEntryWire.Inner(cls, id, payload));
        return AntibotTestSupport.ValidEnvelope(16, AntibotTestSupport.SeqA, plain);
    }

    public static byte[][] Frames(params byte[][] frames)
    {
        return frames;
    }

    /// <summary>进副本的脚本:收到类 1 包号 15 就回放 <paramref name="after15"/>,收到包号 16 就回放 <paramref name="after16"/>,别的不理。</summary>
    public static Func<DungeonEntryClientFrame, IEnumerable<byte[]>> DungeonChain(byte[][] after15, byte[][] after16)
    {
        return frame =>
        {
            if (frame.Class != PacketClass.Cmd)
            {
                return null;
            }
            if (frame.Id == 15)
            {
                return after15;
            }
            if (frame.Id == 16)
            {
                return after16;
            }
            return null;
        };
    }

    /// <summary>
    /// 进城的脚本:登录命令(类 1 包号 1)回 <paramref name="afterLogin"/>,角色列表请求(包号 8)回 <paramref name="afterList"/>,
    /// 选角命令(包号 4,载荷首字节是槽号)回 <paramref name="afterSelect"/>(槽号);其余命令(进副本的 15、16 等)交给 <paramref name="others"/>。
    /// </summary>
    public static Func<DungeonEntryClientFrame, IEnumerable<byte[]>> TownScript(
        byte[][] afterLogin,
        byte[][] afterList,
        Func<int, byte[][]> afterSelect,
        Func<DungeonEntryClientFrame, IEnumerable<byte[]>> others = null)
    {
        return frame =>
        {
            if (frame.Class != PacketClass.Cmd)
            {
                return null;
            }
            switch (frame.Id)
            {
                case 1:
                    return afterLogin;
                case 8:
                    return afterList;
                case 4:
                    return afterSelect(frame.Plain[0]);
                default:
                    return others?.Invoke(frame);
            }
        };
    }

    /// <summary>默认的登录成功回包帧。</summary>
    public static byte[][] LoginOk()
    {
        return Frames(DungeonEntryWire.Reply(1, LoginSuccess()));
    }

    /// <summary>选角回程:进城完成通知(类 0、包号 124;载荷一个字节,解析器只认包号)。</summary>
    public static byte[][] EnterComplete()
    {
        return Frames(DungeonEntryWire.Notice(124, new byte[] { 0 }));
    }

    /// <summary>进城入参:合成的账号与口令(进城后半段不连网关,所以不给网关地址与密钥)。</summary>
    public static OnlineEntrySettings Settings(int maxSlots = 6, string account = "test900")
    {
        return new OnlineEntrySettings { Account = account, Password = "pw900", DataVersion = 900, MaxSlots = maxSlots };
    }

    /// <summary>在已就绪的会话上跑进城后半段,外加一道保险(被测实现被改坏后不返回,20 秒后让测试失败而不是挂住)。</summary>
    public static Task<OnlineEntryResult> EnterTown(
        GameClientSession session,
        IDisposable connection = null,
        int maxSlots = 6,
        OnlineTownWindows windows = null,
        Action<OnlineStage> onStage = null,
        string account = "test900")
    {
        var entry = new OnlineTownEntry(Settings(maxSlots, account));
        return GatewayTestSupport.Bounded(
            entry.EnterOnSessionAsync(session, "TOKEN900", 0, connection ?? new CountingConnection(), onStage, windows ?? TownWindows, CancellationToken.None), 20);
    }

    /// <summary>线程安全的日志收集器(日志回调可能在任意线程上被调用)。</summary>
    public sealed class LogSink
    {
        private readonly object _gate = new object();
        private readonly List<string> _lines = new List<string>();

        public void Add(string line)
        {
            lock (_gate)
            {
                _lines.Add(line);
            }
        }

        public string[] Lines
        {
            get
            {
                lock (_gate)
                {
                    return _lines.ToArray();
                }
            }
        }

        /// <summary>等到收到满足条件的一行(轮询),超时让测试失败。</summary>
        public async Task WaitFor(Func<string, bool> match, string what, int milliseconds = 5000)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!Lines.Any(match))
            {
                if (watch.ElapsedMilliseconds > milliseconds)
                {
                    Assert.Fail("等待日志行超时:" + what + "\n已有:\n" + string.Join("\n", Lines));
                }
                await Task.Delay(10);
            }
        }
    }
}

/// <summary>数着被释放几次的连接替身(进城结果要接管连接的释放)。</summary>
internal sealed class CountingConnection : IDisposable
{
    public int DisposeCount { get; private set; }

    public void Dispose()
    {
        DisposeCount++;
    }
}
