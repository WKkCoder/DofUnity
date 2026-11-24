using System.Text.RegularExpressions;
using Dnf.Net.ChannelDirectory;
using Dnf.Net.Gateway;
using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// <c>switch --list</c> 输出的脱敏:每一条路径(成功、各步失败、取消、没料到的异常)走完后,
/// ① 每一行都必须匹配"只含小整数与枚举名"的白名单格式;② 合成的账号、口令、令牌、主机、IP、端口数字、URL、频道名、组标签文字、异常消息一个都不许出现在输出里。
/// 数据全是合成的(SYNTH- 前缀、合成端口、文档用地址段);异常消息里故意塞了这些敏感串,证明只带异常类型名。没有睡眠。
/// </summary>
public sealed class OnlineProbeKnobsGapListRedactionTests
{
    private static readonly string GatewayIp = GatewayTestSupport.Dots(198, 51, 100, 7);
    private static readonly string OtherIp = GatewayTestSupport.Dots(198, 51, 100, 8);
    private static readonly string SynthToken = Convert.ToBase64String(GatewayTestSupport.Pattern(46, 1));

    // 每一行必须匹配其中之一(只有固定记号、枚举名与小整数)
    private static readonly Regex[] Allowed =
    {
        new(@"^PROBE_SWITCH stage=(settings|gateway-login|directory) to=list outcome=[A-Za-z]+( failure=[A-Za-z]+ reached=[A-Za-z]+ gateway=[A-Za-z]+ gatewayError=[A-Za-z]+)?( fault=[A-Za-z]+)?( elapsedMs=\d+)?$"),
        new(@"^PROBE_SWITCH stage=abort outcome=(Cancelled|Unexpected fault=[A-Za-z]+)$"),
        new(@"^PROBE_SWITCH dir entry=\d+ usable=[01] first=[01] candidate=(\d+|-) group=\d{1,2} kind=-?\d+$"),
        new(@"^PROBE_SWITCH dir-summary entries=\d+ usable=\d+ candidates=\d+ firstInDirectory=[01]$"),
        new(@"^PROBE_SWITCH result=(ok|fail) mode=list( failedStage=(settings|gateway-login|directory|summary))? elapsedMs=\d+$"),
    };

    private static string[] SecretsFor(params string[] extra)
    {
        return new[]
        {
            Account, Password, UrlHost, GatewayHost, HostB, HostC, "SYNTH", "http", "198.51.100", GatewayIp, OtherIp, SynthToken,
            "cain", "siroco", "starter", "70000", "47301", "47311", "47312", "47313", "47314", "47399", "47390",
        }.Concat(PortTexts).Concat(extra).ToArray();
    }

    private static async Task<(int Exit, List<string> Lines)> Run(IOnlineSwitchPort port)
    {
        var lines = new List<string>();
        int exit = await GatewayTestSupport.Bounded(OnlineSwitchProbe.RunAsync(new[] { "--list" }, DefaultRoot, port, lines.Add), 20);
        return (exit, lines);
    }

    private static void AssertRedacted(string scenario, List<string> lines, params string[] extraSecrets)
    {
        Assert.NotEmpty(lines);
        foreach (string line in lines)
        {
            Assert.True(Allowed.Any(r => r.IsMatch(line)), scenario + ": line outside the allow-list format: " + line);
        }
        string all = string.Join("\n", lines);
        foreach (string secret in SecretsFor(extraSecrets))
        {
            Assert.False(all.Contains(secret, StringComparison.OrdinalIgnoreCase), scenario + ": output contains a forbidden string of length " + secret.Length);
        }
    }

    /// <summary>目录里有可映射的组标签、合成频道名、合成主机与端口:任何一个出现在输出里都算泄露。</summary>
    private static ChannelDirectoryLoadResult RichDirectory()
    {
        ChannelDirectorySnapshot snapshot = ChannelDirectorySnapshot.Build("0", true, null, new[]
        {
            new ChannelWireGroup("cain", new[]
            {
                new ChannelWireEntry("SYNTH-CHANNEL-LEAK-1", 200, 10, GatewayHost, FirstPort),
                new ChannelWireEntry("SYNTH-CHANNEL-LEAK-2", 200, 10, HostB, PortB),
            }),
            new ChannelWireGroup("SYNTH-GROUP-LEAK", new[]
            {
                new ChannelWireEntry("SYNTH-CHANNEL-LEAK-3", 200, 10, HostC, PortC),
                new ChannelWireEntry("SYNTH-CHANNEL-LEAK-4", 200, 10, HostC, 70000),
            }),
        });
        return ChannelDirectoryLoadResult.Success(snapshot, 0, true);
    }

    // 保护:各条路径的输出都只有白名单格式的行、没有任何合成敏感串;并确认每个场景确实走到了预期的退出码(防止场景没触发目标路径)
    [Fact]
    public async Task No_path_leaks_account_password_token_host_port_url_channel_name_tag_or_exception_text()
    {
        var scenarios = new List<(string Name, int ExpectedExit, IOnlineSwitchPort Port)>();

        var ok = new FakePort();
        ok.Session.OnDirectory = RichDirectory;
        scenarios.Add(("success", 0, ok));
        scenarios.Add(("success-standard-directory", 0, new FakePort()));

        var empty = new FakePort();
        empty.Session.OnDirectory = () => Directory();
        scenarios.Add(("success-empty-directory", 0, empty));

        scenarios.Add(("settings-failed", 2, new FakePort { LoadSucceeds = false }));
        scenarios.Add(("create-throws", 2, new FakePort { ThrowOnCreate = true }));

        var rejected = new FakePort();
        rejected.Session.OnLogin = () => new OnlineLoginResult(OnlineStage.GatewayLogin, OnlineFailure.GatewayLoginFailed,
            GatewayResult.ServerError("SYNTH-ERROR-KEY " + Account + " " + Password, GatewayErrorCode.WrongCredentials));
        scenarios.Add(("gateway-server-error", 5, rejected));

        var loginThrows = new FakePort();
        loginThrows.Session.OnLogin = () => throw new IOException("SYNTH-LOGIN " + Account + " " + Password + " " + GatewayHost + " " + SynthToken + " 47390");
        scenarios.Add(("gateway-throws", 5, loginThrows));

        var directoryFails = new FakePort();
        directoryFails.Session.OnDirectory = () => ChannelDirectoryLoadResult.Fail(ChannelDirectoryLoadOutcome.FrameError, "SYNTH-DETAIL " + GatewayHost + " 47301");
        scenarios.Add(("directory-failed", 6, directoryFails));

        var directoryThrows = new FakePort();
        directoryThrows.Session.FaultAsTask = true;
        directoryThrows.Session.OnDirectory = () => throw new InvalidOperationException("SYNTH-DIRECTORY " + HostB + " " + PortB);
        scenarios.Add(("directory-throws", 6, directoryThrows));

        var cancelled = new FakePort();
        cancelled.Session.OnLogin = () => throw new OperationCanceledException("SYNTH-CANCEL " + Account);
        scenarios.Add(("cancelled", 15, cancelled));

        scenarios.Add(("unexpected", 70, new ThrowingHostPort()));

        foreach ((string name, int expectedExit, IOnlineSwitchPort port) in scenarios)
        {
            (int exit, List<string> lines) = await Run(port);

            Assert.True(expectedExit == exit, name + ": exit " + exit + " instead of " + expectedExit);
            AssertRedacted(name, lines, "LEAK");
        }
    }

    // 保护:真会话(内存假网关)上成功与网关拒绝两条路径同样不泄露令牌、口令、地址、端口数字;行都在白名单格式内
    [Fact]
    public async Task The_real_session_paths_leak_no_token_password_address_or_port_either()
    {
        var okRig = NewRig();
        (int okExit, List<string> okLines) = await Run(new RigPort(okRig));

        var rejectedRig = NewRig();
        rejectedRig.FakeGateway.ForceReplyPlaintext = null;
        rejectedRig.Settings.Password = "SYNTH-WRONG-PASSWORD";
        (int rejectedExit, List<string> rejectedLines) = await Run(new RigPort(rejectedRig));

        Assert.Equal(0, okExit);
        Assert.Equal(5, rejectedExit);
        AssertRedacted("real-success", okLines, "LEAK", "SYNTH-WRONG-PASSWORD");
        AssertRedacted("real-rejected", rejectedLines, "LEAK", "SYNTH-WRONG-PASSWORD");
        Assert.Contains(okLines, l => l.StartsWith("PROBE_SWITCH dir entry="));
    }

    // 保护:成功路径输出里的行种类恰好是:3 个阶段行、N 个条目行、1 个汇总行、1 个结果行(没有多余的行种类)
    [Fact]
    public async Task The_success_output_has_exactly_the_expected_kinds_of_lines()
    {
        var port = new FakePort();
        port.Session.OnDirectory = RichDirectory;

        (int exit, List<string> lines) = await Run(port);

        Assert.Equal(0, exit);
        Assert.Equal(3, lines.Count(l => l.Contains(" stage=")));
        Assert.Equal(4, lines.Count(l => l.StartsWith("PROBE_SWITCH dir entry=")));
        Assert.Equal(1, lines.Count(l => l.StartsWith("PROBE_SWITCH dir-summary ")));
        Assert.Equal(1, lines.Count(l => l.StartsWith("PROBE_SWITCH result=")));
        Assert.Equal(9, lines.Count);
    }

    // ---- 辅助:读游戏服主机时抛异常的会话/端口(模拟流程自己出了没料到的错,消息里塞敏感串) ----
    private sealed class ThrowingHostPort : IOnlineSwitchPort
    {
        public bool TryLoadSettings(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            settings = Settings();
            problem = null;
            return true;
        }

        public IOnlineSwitchSession CreateSession(OnlineEntrySettings settings)
        {
            return new ThrowingHostSession();
        }
    }

    private sealed class ThrowingHostSession : IOnlineSwitchSession
    {
        public uint ClientVersion => 0;
        public string GameServerHost => throw new InvalidOperationException("SYNTH-HOST-READ " + GatewayHost + " " + Account + " 47301");

        public Task<OnlineLoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken) => Task.FromResult(LoginOk());
        public Task<ChannelDirectoryLoadResult> LoadChannelDirectoryAsync(CancellationToken cancellationToken) => Task.FromResult(StandardDirectory());
        public Task<OnlineConnectResult> ConnectAsync(int channelPort, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OnlineConnectResult> SwitchChannelAsync(int channelPort, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OnlineConnectResult> SwitchChannelAsync(string host, int channelPort, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose()
        {
        }
    }

    // ---- 真会话环境 ----
    private sealed class RigPort : IOnlineSwitchPort
    {
        private readonly OnlineFlowRig _rig;

        internal RigPort(OnlineFlowRig rig)
        {
            _rig = rig;
        }

        public bool TryLoadSettings(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            settings = _rig.Settings;
            problem = null;
            return true;
        }

        public IOnlineSwitchSession CreateSession(OnlineEntrySettings settings)
        {
            return new OnlineClientSwitchSession(new OnlineClientSession(settings, null, null, _rig.Hooks));
        }
    }

    private static OnlineFlowRig NewRig()
    {
        var rig = new OnlineFlowRig();
        rig.FakeGateway.ForceReplyPlaintext = "0|" + SynthToken;
        rig.FakeGateway.GameServerIp = GatewayIp;
        rig.Hooks.SwitchLoginRetryDelay = TimeSpan.Zero;
        rig.Settings.ChannelPort = FirstPort;
        ChannelDirectorySnapshot snapshot = DirectorySnapshot("siroco", (GatewayIp, FirstPort), (OtherIp, PortB), (GatewayIp, PortC));
        rig.Hooks.DirectoryLoader = (_, _, _) => Task.FromResult(ChannelDirectoryLoadResult.Success(snapshot, 0, true));
        return rig;
    }
}
