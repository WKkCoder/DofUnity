using Dnf.Net.ChannelDirectory;
using Dnf.Net.Gateway;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 <c>switch</c> 子命令与两个开关解析的测试共用件:假端口、假会话与合成数据。
/// 账号、口令、主机、频道名、组标签都带 SYNTH- 前缀,端口是远离常见取值的合成数字,
/// 这样"输出里一个都不出现"可以直接按子串断言。全部合成,不含任何真实值。
/// </summary>
internal static class OnlineSwitchTestKit
{
    internal const string Account = "SYNTH-ACCOUNT-ALPHA";
    internal const string Password = "SYNTH-PASSWORD-ALPHA";
    internal const string UrlHost = "synth-url-host.invalid";
    internal const string GatewayHost = "SYNTH-HOST-GATEWAY";
    internal const string HostB = "SYNTH-HOST-BRAVO";
    internal const string HostC = "SYNTH-HOST-CHARLIE";
    internal const string DefaultRoot = "SYNTH-DEFAULT-ROOT";

    /// <summary>设置里的频道端口,也就是第一个频道的端口。</summary>
    internal const int FirstPort = 47301;

    internal const int PortB = 47311;
    internal const int PortC = 47312;
    internal const int PortD = 47313;

    /// <summary><c>--to-port</c> 用的端口。</summary>
    internal const int TargetPort = 47399;

    /// <summary>网关地址里的端口(只出现在合成网关地址里)。</summary>
    internal const int GatewayPort = 47390;

    /// <summary>上面所有合成端口的十进制文本:输出里一个都不许出现。</summary>
    internal static readonly string[] PortTexts =
    {
        FirstPort.ToString(), PortB.ToString(), PortC.ToString(), PortD.ToString(), TargetPort.ToString(), GatewayPort.ToString(),
    };

    /// <summary>合成设置:两个开关缺省 0,可以预置成别的值来证明命令行优先于读到的配置。</summary>
    internal static OnlineEntrySettings Settings(uint clientVersion = 0, ushort serverGroup = 0)
    {
        return new OnlineEntrySettings
        {
            Gateway = new GatewayOptions("http://" + UrlHost + ":" + GatewayPort, GatewayTestSupport.HexLower(GatewayTestSupport.SynthKey(5))),
            Account = Account,
            Password = Password,
            DataVersion = 900,
            ChannelPort = FirstPort,
            ClientVersion = clientVersion,
            ServerGroup = serverGroup,
        };
    }

    internal static OnlineLoginResult LoginOk()
    {
        return new OnlineLoginResult(OnlineStage.GameServerAddress, OnlineFailure.None);
    }

    internal static OnlineLoginResult LoginFailed()
    {
        return new OnlineLoginResult(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed);
    }

    /// <summary>有 <paramref name="count"/> 个条目的模式 2 角色列表(合成载荷经真解析器读出)。</summary>
    internal static UserInfoNotice Notice(int count)
    {
        var specs = Enumerable.Range(0, count).Select(i => UserInfoTestData.Standard(i)).ToArray();
        Assert.True(UserInfoParser.TryParse(UserInfoTestData.Packet(specs), out UserInfoNotice notice));
        Assert.Equal(count, notice.Entries.Count);
        return notice;
    }

    /// <summary>成功且收到角色列表(<paramref name="characters"/> 可以是 0)。</summary>
    internal static OnlineConnectResult Connected(int characters, int attempts = 1, ushort group = 0)
    {
        return new OnlineConnectResult(OnlineStage.GameLogin, OnlineFailure.None, null, null,
            Notice(characters), OnlineCharacterListStatus.Received, null, attempts, group);
    }

    /// <summary>成功但角色列表没收到或畸形(<see cref="OnlineCharacterListStatus.Received"/> 以外的状态)。</summary>
    internal static OnlineConnectResult ConnectedWithoutList(OnlineCharacterListStatus status, int attempts = 1, ushort group = 0)
    {
        return new OnlineConnectResult(OnlineStage.GameLogin, OnlineFailure.None, null, null, null, status, null, attempts, group);
    }

    internal static OnlineConnectResult Failed(OnlineFailure failure, OnlineStage stage, byte? loginCode = null, int attempts = 0, ushort group = 0)
    {
        return new OnlineConnectResult(stage, failure, loginCode, null, null, OnlineCharacterListStatus.NotReceived, null, attempts, group);
    }

    /// <summary>一个目录:一个组里依次是给定的(主机,端口);端口 0 的条目没有"端口有效"标记。</summary>
    internal static ChannelDirectoryLoadResult Directory(params (string Host, int Port)[] channels)
    {
        return ChannelDirectoryLoadResult.Success(DirectorySnapshot("SYNTH-GROUP", channels), 0, true);
    }

    internal static ChannelDirectorySnapshot DirectorySnapshot(string groupTag, params (string Host, int Port)[] channels)
    {
        var entries = channels
            .Select((c, i) => new ChannelWireEntry("SYNTH-CHANNEL-" + (i + 1), 200, 10, c.Host, c.Port))
            .ToArray();
        return ChannelDirectorySnapshot.Build("0", true, null, new[] { new ChannelWireGroup(groupTag, entries) });
    }

    /// <summary>默认目录:第一个频道本身、候选 0 号、端口无效的条目、候选 1 号、候选 2 号(主机与端口各有一处与第一个频道相同)。</summary>
    internal static ChannelDirectoryLoadResult StandardDirectory()
    {
        return Directory(
            (GatewayHost, FirstPort),   // 与第一个频道同主机同端口:被排除
            (HostB, PortB),             // 候选 0
            (GatewayHost, 0),           // 端口无效:被排除
            (HostC, PortC),             // 候选 1
            (HostC, PortD));            // 候选 2
    }

    /// <summary>
    /// 可编排的假会话:记录调用顺序,结果由委托给出;委托里抛异常即"这个调用抛异常"
    /// (<see cref="FaultAsTask"/> 为真时抛成"已出错的任务",否则同步抛)。
    /// </summary>
    internal sealed class FakeSession : IOnlineSwitchSession
    {
        internal List<string> Calls { get; } = new();
        internal string LoginAccount { get; private set; }
        internal string LoginPassword { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool FaultAsTask { get; set; }

        public uint ClientVersion { get; set; }
        public string GameServerHost { get; set; } = GatewayHost;

        internal Func<OnlineLoginResult> OnLogin { get; set; } = LoginOk;
        internal Func<ChannelDirectoryLoadResult> OnDirectory { get; set; } = StandardDirectory;
        internal Func<int, OnlineConnectResult> OnConnect { get; set; } = _ => Connected(3, 1, 3);
        internal Func<string, int, OnlineConnectResult> OnSwitch { get; set; } = (_, _) => Connected(2, 1, 3);
        internal Action OnDisconnect { get; set; } = () => { };

        internal int Count(string call) => Calls.Count(c => c == call);

        public Task<OnlineLoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken)
        {
            Calls.Add("login");
            LoginAccount = account;
            LoginPassword = password;
            return Run(() => OnLogin());
        }

        public Task<ChannelDirectoryLoadResult> LoadChannelDirectoryAsync(CancellationToken cancellationToken)
        {
            Calls.Add("directory");
            return Run(() => OnDirectory());
        }

        public Task<OnlineConnectResult> ConnectAsync(int channelPort, CancellationToken cancellationToken)
        {
            Calls.Add("connect:" + channelPort);
            return Run(() => OnConnect(channelPort));
        }

        public Task<OnlineConnectResult> SwitchChannelAsync(int channelPort, CancellationToken cancellationToken)
        {
            Calls.Add("switch-port:" + channelPort);
            return Run(() => OnSwitch(null, channelPort));
        }

        public Task<OnlineConnectResult> SwitchChannelAsync(string host, int channelPort, CancellationToken cancellationToken)
        {
            Calls.Add("switch-host:" + host + ":" + channelPort);
            return Run(() => OnSwitch(host, channelPort));
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            Calls.Add("disconnect");
            return Run(() =>
            {
                OnDisconnect();
                return true;
            });
        }

        public void Dispose()
        {
            Disposed = true;
        }

        private Task<T> Run<T>(Func<T> body)
        {
            try
            {
                return Task.FromResult(body());
            }
            catch (Exception e) when (FaultAsTask)
            {
                return Task.FromException<T>(e);
            }
        }
    }

    /// <summary>可编排的假端口:返回给定设置与假会话,记录被调用的次数与收到的参数。</summary>
    internal sealed class FakePort : IOnlineSwitchPort
    {
        internal OnlineEntrySettings Settings { get; set; } = OnlineSwitchTestKit.Settings();
        internal bool LoadSucceeds { get; set; } = true;
        internal bool ThrowOnCreate { get; set; }
        internal FakeSession Session { get; } = new();
        internal int LoadCalls { get; private set; }
        internal int CreateCalls { get; private set; }
        internal string RepoRootSeen { get; private set; }
        internal OnlineEntrySettings SettingsSeenByCreate { get; private set; }

        public bool TryLoadSettings(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            LoadCalls++;
            RepoRootSeen = repoRoot;
            settings = LoadSucceeds ? Settings : null;
            problem = LoadSucceeds ? null : "SYNTH-PROBLEM-DETAIL " + Account + " " + GatewayHost;
            return LoadSucceeds;
        }

        public IOnlineSwitchSession CreateSession(OnlineEntrySettings settings)
        {
            CreateCalls++;
            SettingsSeenByCreate = settings;
            if (ThrowOnCreate)
            {
                throw new IOException("SYNTH-CREATE-MESSAGE " + GatewayHost);
            }
            Session.ClientVersion = settings.ClientVersion;
            return Session;
        }
    }

    /// <summary>"一旦被碰就失败"的端口:用来证明命令行不合规时流程在碰端口之前就结束(没读配置、没拿会话、更不会联网)。</summary>
    internal sealed class FailIfCalledPort : IOnlineSwitchPort
    {
        internal int Calls { get; private set; }

        public bool TryLoadSettings(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            Calls++;
            throw new InvalidOperationException("SYNTH: the port must not be touched when the command line is invalid");
        }

        public IOnlineSwitchSession CreateSession(OnlineEntrySettings settings)
        {
            Calls++;
            throw new InvalidOperationException("SYNTH: the port must not be touched when the command line is invalid");
        }
    }
}
