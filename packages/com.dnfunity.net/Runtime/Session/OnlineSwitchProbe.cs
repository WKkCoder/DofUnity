using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.ChannelDirectory;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 探针 <c>switch</c> 子命令的进程退出码(对外约定)。网关层的任何失败都立即结束,探针从不自动重试、也不循环重连。
    /// </summary>
    public static class OnlineSwitchExitCodes
    {
        /// <summary>两步都成功,且两次都收到了角色列表。</summary>
        public const int Success = 0;

        /// <summary>读本地装配(仓库根下的配置)失败;与 orchestrated 子命令同码。</summary>
        public const int SettingsFailed = 2;

        /// <summary>第一个频道没连成(连不上、没收到启动包、游戏登录被拒或没回应、登录请求本地检查不过、没拿到角色列表)。</summary>
        public const int FirstConnectFailed = 3;

        /// <summary>换频道没成(同上各种原因,针对换过去的那一步)。</summary>
        public const int SwitchFailed = 4;

        /// <summary>网关认证失败(登录被拒、被限流、没有可用的游戏服地址、网络或协议出错)。立即结束,不重试。</summary>
        public const int GatewayAuthFailed = 5;

        /// <summary>读频道目录失败。</summary>
        public const int DirectoryFailed = 6;

        /// <summary>目录里选不出目标(<c>--to-index K</c> 时可选的频道不够 K+1 个)。此时没有连接任何频道。</summary>
        public const int NoTarget = 7;

        /// <summary>被取消(Ctrl+C);与 fight 子命令同码。规格之外的补充。</summary>
        public const int Cancelled = 15;

        /// <summary>命令行用法不对;没有读本地配置、没有任何网络访问。</summary>
        public const int Usage = 64;

        /// <summary>流程自己没料到的异常(只打异常类型名);与 fight 子命令同码。规格之外的补充。</summary>
        public const int Unexpected = 70;
    }

    /// <summary><c>switch</c> 子命令里一步(连第一个频道、换频道)的结局枚举名(输出行里的 first=、second=、outcome=)。</summary>
    public enum OnlineSwitchStepOutcome
    {
        /// <summary>这一步没有执行(前面的步骤已经失败或结束)。</summary>
        NotRun = 0,

        /// <summary>连上、游戏登录通过,并且收到了角色列表(可以是空列表)。</summary>
        Ok,

        /// <summary>连不上频道。</summary>
        ConnectFailed,

        /// <summary>连上了但没收到启动包。</summary>
        BootstrapFailed,

        /// <summary>连接在接受阶段被服务端拒绝。</summary>
        Rejected,

        /// <summary>游戏登录回包给的是失败码(码在输出行的 loginFailureCode 里)。</summary>
        GameLoginRejected,

        /// <summary>游戏登录没等到回包。</summary>
        GameLoginNoReply,

        /// <summary>登录请求本地检查不过(字段不合规),没有发出。</summary>
        BadLoginRequest,

        /// <summary>游戏登录通过,但没有收到角色列表。</summary>
        CharacterListNotReceived,

        /// <summary>游戏登录通过,但角色列表读不通(畸形)。</summary>
        CharacterListMalformed,

        /// <summary>这一步里会话调用抛了没料到的异常(输出行里只带异常类型名)。</summary>
        Faulted,

        /// <summary>其它失败(产品层给出了这里没单列的失败原因)。</summary>
        OtherFailure,
    }

    /// <summary>
    /// <c>switch</c> 子命令用到的会话的最小接口:登录、读目录、连频道、换频道、断开,仅此而已。
    /// 没有选角、没有独占收包、没有发命令包的口子,所以 <c>switch</c> 的流程在结构上就不可能去选角或发命令。
    /// 产品里由 <see cref="OnlineClientSwitchSession"/> 包一个真会话,测试里换成假的。
    /// </summary>
    public interface IOnlineSwitchSession : IDisposable
    {
        /// <summary>会话实际持有的客户端版本号(下一次登录请求会写它)。</summary>
        uint ClientVersion { get; }

        /// <summary>网关给的游戏服主机文本(只在进程内用来判断"是不是同一个主机",绝不打印);还没登录为 null。</summary>
        string GameServerHost { get; }

        /// <summary>网关登录并取游戏服地址;整个子命令只调用一次。</summary>
        Task<OnlineLoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken);

        /// <summary>读频道目录(单次只读,不重试)。</summary>
        Task<ChannelDirectoryLoadResult> LoadChannelDirectoryAsync(CancellationToken cancellationToken);

        /// <summary>连指定端口的频道(主机取网关给的地址)、收启动包并游戏登录、取角色列表。</summary>
        Task<OnlineConnectResult> ConnectAsync(int channelPort, CancellationToken cancellationToken);

        /// <summary>关旧连接,用同一令牌连同一主机上的另一个端口。</summary>
        Task<OnlineConnectResult> SwitchChannelAsync(int channelPort, CancellationToken cancellationToken);

        /// <summary>关旧连接,用同一令牌连给定主机(IP 字面量)与端口。</summary>
        Task<OnlineConnectResult> SwitchChannelAsync(string host, int channelPort, CancellationToken cancellationToken);

        /// <summary>关闭当前频道连接。</summary>
        Task DisconnectAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// <c>switch</c> 子命令的"外部接口":读本地配置与拿会话。产品里装配真的(读本机配置、真会话),测试里装配假的;
    /// 命令行不合规时,流程在碰到这里的任何一个方法之前就以退出码 64 结束。
    /// </summary>
    public interface IOnlineSwitchPort
    {
        /// <summary>装配联网入参(产品:<see cref="LocalDevEntrySettings.TryLoad"/>);失败的原因文字不会被打印。</summary>
        bool TryLoadSettings(string repoRoot, out OnlineEntrySettings settings, out string problem);

        /// <summary>按入参拿一个新会话(产品:真会话)。</summary>
        IOnlineSwitchSession CreateSession(OnlineEntrySettings settings);
    }

    /// <summary>把真的 <see cref="OnlineClientSession"/> 接到 <see cref="IOnlineSwitchSession"/> 上;只转发,不加任何行为。</summary>
    public sealed class OnlineClientSwitchSession : IOnlineSwitchSession
    {
        private readonly OnlineClientSession _session;

        /// <summary>包一个会话;释放时一并释放它。</summary>
        public OnlineClientSwitchSession(OnlineClientSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <inheritdoc />
        public uint ClientVersion => _session.ClientVersion;

        /// <inheritdoc />
        public string GameServerHost => _session.GameServerHost;

        /// <inheritdoc />
        public Task<OnlineLoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken)
        {
            return _session.LoginAsync(account, password, cancellationToken);
        }

        /// <inheritdoc />
        public Task<ChannelDirectoryLoadResult> LoadChannelDirectoryAsync(CancellationToken cancellationToken)
        {
            return _session.LoadChannelDirectoryAsync(cancellationToken: cancellationToken);
        }

        /// <inheritdoc />
        public Task<OnlineConnectResult> ConnectAsync(int channelPort, CancellationToken cancellationToken)
        {
            return _session.ConnectAsync(channelPort, cancellationToken);
        }

        /// <inheritdoc />
        public Task<OnlineConnectResult> SwitchChannelAsync(int channelPort, CancellationToken cancellationToken)
        {
            return _session.SwitchChannelAsync(channelPort, cancellationToken);
        }

        /// <inheritdoc />
        public Task<OnlineConnectResult> SwitchChannelAsync(string host, int channelPort, CancellationToken cancellationToken)
        {
            return _session.SwitchChannelAsync(host, channelPort, cancellationToken);
        }

        /// <inheritdoc />
        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            return _session.DisconnectAsync(cancellationToken);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _session.Dispose();
        }
    }

    /// <summary>
    /// 探针 <c>switch</c> 子命令(只读、一条会话、网关只登录一次、不选角、不进城、不发任何命令包):
    /// 网关登录 → 读频道目录 → 连第一个频道(设置里的频道端口)并记下角色列表 → 换频道到目标并记下角色列表 → 断开。
    /// 这是原版"选角界面点选择频道"的那条路。会话内部台账 E-443 的有限重试(结算窗口内游戏登录被拒码 1 才重试,最多 6 次)是产品行为,这里原样保留,
    /// 探针自己没有任何重试或循环。
    /// <para>
    /// 输出只有以 <c>PROBE_SWITCH</c> 开头的脱敏行:阶段名、结局枚举名、登录失败码、角色列表状态与条数、游戏登录次数、实际写进登录请求的服务器组与客户端版本号、
    /// 各步耗时毫秒、<c>to=index:K</c> 或 <c>to=port</c> 这样的序号式记号(异常只带类型名)。绝不输出账号、口令、令牌、主机、IP、端口号、URL、频道名、角色名与任何载荷字节
    /// (目录里的主机与端口只在进程内用来选目标与连接)。最后一行总是 <c>PROBE_SWITCH result=…</c>(用法错误除外:只有一行用法提示)。
    /// </para>
    /// <para>
    /// 给了 <c>--list</c> 时是另一个只读流程(<see cref="OnlineSwitchListRun"/>):读本地设置 → 网关登录(只一次)→ 读频道目录 → 逐条目打脱敏摘要 → 结束,
    /// 一个频道也不连(不调用 <see cref="IOnlineSwitchSession.ConnectAsync"/> 与 <see cref="IOnlineSwitchSession.SwitchChannelAsync(int, CancellationToken)"/>)、
    /// 不做游戏登录、不选角、不发命令包;用途是在真服换频道验证之前先看到"哪个序号会被 <c>--to-index</c> 选中、各频道会写什么服务器组"。
    /// 不给 <c>--list</c> 的行为与加这个模式之前一字不差。
    /// </para>
    /// </summary>
    public static class OnlineSwitchProbe
    {
        /// <summary>每一行输出的开头。</summary>
        public const string LinePrefix = "PROBE_SWITCH";

        /// <summary>命令行语法(用法行里用)。</summary>
        public const string Syntax = "dnf-netprobe switch [仓库根] --to-index <K> | --to-port <N> [--client-version <u32>] [--server-group <0..17>]";

        /// <summary>只读目录摘要的命令行语法(命令行里出现了 <c>--list</c> 的用法行里,接在 <see cref="Syntax"/> 之后)。</summary>
        public const string ListSyntax = "dnf-netprobe switch [仓库根] --list";

        /// <summary>
        /// 跑一遍;返回进程退出码(见 <see cref="OnlineSwitchExitCodes"/>)。<paramref name="args"/> 是 <c>switch</c> 之后的全部参数;
        /// <paramref name="defaultRepoRoot"/> 在没给仓库根时使用;<paramref name="writeLine"/> 收每一行输出。
        /// 命令行不合规时在碰 <paramref name="port"/> 之前就返回 64。会话调用抛的异常(取消除外)在各步里收成 Faulted 结局;
        /// 取消返回 <see cref="OnlineSwitchExitCodes.Cancelled"/>,流程自己没料到的异常返回 <see cref="OnlineSwitchExitCodes.Unexpected"/>,两者都照样打最后一行。
        /// 给了 <c>--list</c> 时走只读的目录摘要流程(网关登录 → 读目录 → 打摘要,不连任何频道、不做游戏登录,见 <see cref="OnlineDirectorySummary"/>),
        /// 退出码只有 0、2、5、6、15、64、70。
        /// </summary>
        public static async Task<int> RunAsync(IReadOnlyList<string> args, string defaultRepoRoot, IOnlineSwitchPort port,
            Action<string> writeLine, CancellationToken cancellationToken = default)
        {
            if (port == null)
            {
                throw new ArgumentNullException(nameof(port));
            }
            if (writeLine == null)
            {
                throw new ArgumentNullException(nameof(writeLine));
            }

            if (!OnlineSwitchArguments.TryParse(args, out OnlineSwitchArguments parsed, out OnlineProbeArgumentProblem usageProblem))
            {
                // 命令行里出现了 --list 时,用法行里把目录摘要的写法也列出来;其余情形的用法行与以前一字不差
                string syntax = OnlineSwitchArguments.MentionsList(args) ? Syntax + " | " + ListSyntax : Syntax;
                writeLine(LinePrefix + " usage problem=" + usageProblem + " exit=" + Num(OnlineSwitchExitCodes.Usage) + " syntax: " + syntax);
                return OnlineSwitchExitCodes.Usage;
            }

            if (parsed.ListMode)
            {
                return await OnlineSwitchListRun.RunAsync(parsed, defaultRepoRoot, port, writeLine, cancellationToken).ConfigureAwait(false);
            }

            var run = new Run(parsed, writeLine, cancellationToken);
            try
            {
                return await run.ExecuteAsync(defaultRepoRoot, port).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                writeLine(LinePrefix + " stage=abort outcome=Cancelled");
                return run.Finish(OnlineSwitchExitCodes.Cancelled);
            }
            catch (Exception e)
            {
                writeLine(LinePrefix + " stage=abort outcome=Unexpected fault=" + e.GetBaseException().GetType().Name);
                return run.Finish(OnlineSwitchExitCodes.Unexpected);
            }
        }

        /// <summary>一步的结局枚举名:连上且收到角色列表才算 <see cref="OnlineSwitchStepOutcome.Ok"/>。<paramref name="result"/> 为 null 表示这一步抛了异常。</summary>
        public static OnlineSwitchStepOutcome OutcomeOf(OnlineConnectResult result)
        {
            if (result == null)
            {
                return OnlineSwitchStepOutcome.Faulted;
            }
            if (result.IsSuccess)
            {
                switch (result.CharacterListStatus)
                {
                    case OnlineCharacterListStatus.Received:
                        return OnlineSwitchStepOutcome.Ok;
                    case OnlineCharacterListStatus.Malformed:
                        return OnlineSwitchStepOutcome.CharacterListMalformed;
                    default:
                        return OnlineSwitchStepOutcome.CharacterListNotReceived;
                }
            }
            switch (result.Failure)
            {
                case OnlineFailure.ConnectFailed:
                    return OnlineSwitchStepOutcome.ConnectFailed;
                case OnlineFailure.BootstrapFailed:
                    return OnlineSwitchStepOutcome.BootstrapFailed;
                case OnlineFailure.Rejected:
                    return OnlineSwitchStepOutcome.Rejected;
                case OnlineFailure.GameLoginRejected:
                    return OnlineSwitchStepOutcome.GameLoginRejected;
                case OnlineFailure.GameLoginNoReply:
                    return OnlineSwitchStepOutcome.GameLoginNoReply;
                case OnlineFailure.BadLoginRequest:
                    return OnlineSwitchStepOutcome.BadLoginRequest;
                default:
                    return OnlineSwitchStepOutcome.OtherFailure;
            }
        }

        /// <summary>
        /// <c>--to-index K</c> 的候选:目录里端口有效(<see cref="ChannelEntryFlags.HasValidPort"/>)、并且与第一个频道不是同一个主机加端口的频道,按目录顺序。
        /// 第一个频道 = 网关给的主机(<paramref name="firstHost"/>)加设置里的频道端口(<paramref name="firstPort"/>);目录里没写主机的条目视为在网关给的那个主机上。
        /// </summary>
        public static List<ChannelEntry> Candidates(IReadOnlyList<ChannelEntry> channels, string firstHost, int firstPort)
        {
            var result = new List<ChannelEntry>();
            if (channels == null)
            {
                return result;
            }
            for (int i = 0; i < channels.Count; i++)
            {
                ChannelEntry entry = channels[i];
                if (entry == null || (entry.Flags & ChannelEntryFlags.HasValidPort) == 0)
                {
                    continue;
                }
                if (IsFirstChannel(entry, firstHost, firstPort))
                {
                    continue;
                }
                result.Add(entry);
            }
            return result;
        }

        /// <summary>
        /// 这个条目是不是"第一个频道":端口等于设置里的频道端口(<paramref name="firstPort"/>)、并且与网关给的主机(<paramref name="firstHost"/>)是同一个主机
        /// (目录里没写主机的条目视为在网关给的那个主机上)。<see cref="Candidates"/> 排除第一个频道与目录摘要里的 <c>first=</c> 用的是同一个判定。
        /// </summary>
        public static bool IsFirstChannel(ChannelEntry entry, string firstHost, int firstPort)
        {
            return entry != null && entry.Port == firstPort && SameHost(entry.Host, firstHost);
        }

        /// <summary>候选里按目录顺序的第 <paramref name="index"/> 个(从 0 起);不够时为 null。</summary>
        public static ChannelEntry SelectByIndex(IReadOnlyList<ChannelEntry> channels, string firstHost, int firstPort, int index)
        {
            if (index < 0)
            {
                return null;
            }
            List<ChannelEntry> candidates = Candidates(channels, firstHost, firstPort);
            return index < candidates.Count ? candidates[index] : null;
        }

        private static bool SameHost(string entryHost, string firstHost)
        {
            if (string.IsNullOrWhiteSpace(entryHost))
            {
                return true;                   // 目录没写主机:只能是网关给的那个主机(连接时也是这么取的)
            }
            if (string.IsNullOrWhiteSpace(firstHost))
            {
                return false;
            }
            string a = entryHost.Trim();
            string b = firstHost.Trim();
            if (IPAddress.TryParse(a, out IPAddress left) && IPAddress.TryParse(b, out IPAddress right))
            {
                return left.Equals(right);
            }
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string Num(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        // 一步的汇总(最后一行用)
        private sealed class StepReport
        {
            public OnlineSwitchStepOutcome Outcome = OnlineSwitchStepOutcome.NotRun;
            public int Attempts;
            public ushort Group;
        }

        private sealed class Run
        {
            private readonly OnlineSwitchArguments _args;
            private readonly Action<string> _write;
            private readonly CancellationToken _cancellationToken;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly string _to;
            private readonly StepReport _first = new StepReport();
            private readonly StepReport _second = new StepReport();
            private uint _clientVersion;

            public Run(OnlineSwitchArguments args, Action<string> write, CancellationToken cancellationToken)
            {
                _args = args;
                _write = write;
                _cancellationToken = cancellationToken;
                _to = args.TargetIsIndex ? "index:" + Num(args.ToIndex) : "port";
                _clientVersion = args.ClientVersion ?? 0;
            }

            public async Task<int> ExecuteAsync(string defaultRepoRoot, IOnlineSwitchPort port)
            {
                // ---- 装配:先读本地配置,再用命令行的两个开关覆盖
                OnlineEntrySettings settings = null;
                IOnlineSwitchSession session = null;
                string fault = null;
                try
                {
                    string repoRoot = _args.RepoRoot ?? defaultRepoRoot;
                    if (port.TryLoadSettings(repoRoot, out settings, out _) && settings != null)
                    {
                        if (_args.ClientVersion.HasValue)
                        {
                            settings.ClientVersion = _args.ClientVersion.Value;
                        }
                        if (_args.ServerGroup.HasValue)
                        {
                            settings.ServerGroup = _args.ServerGroup.Value;
                        }
                        session = port.CreateSession(settings);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    fault = e.GetBaseException().GetType().Name;
                    session = null;
                }
                if (session == null)
                {
                    _write(LinePrefix + " stage=settings to=" + _to + " outcome=SettingsFailed" + (fault == null ? string.Empty : " fault=" + fault));
                    return Finish(OnlineSwitchExitCodes.SettingsFailed);
                }

                using (session)
                {
                    _clientVersion = session.ClientVersion;
                    _write(LinePrefix + " stage=settings to=" + _to + " outcome=Ok clientVersion=" + Num(_clientVersion)
                        + " serverGroup=" + Num(settings.ServerGroup));

                    // ---- 网关登录:整个子命令只此一次,任何网关层失败都立即结束
                    long mark = _clock.ElapsedMilliseconds;
                    (OnlineLoginResult login, string loginFault) = await GuardAsync(
                        () => session.LoginAsync(settings.Account, settings.Password, _cancellationToken)).ConfigureAwait(false);
                    if (login == null || !login.IsSuccess)
                    {
                        string detail = login == null
                            ? " outcome=Faulted fault=" + loginFault
                            : " outcome=Failed failure=" + login.Failure + " reached=" + login.Stage + " gateway=" + login.GatewayOutcome
                                + " gatewayError=" + login.GatewayErrorCode;
                        _write(LinePrefix + " stage=gateway-login to=" + _to + detail + " elapsedMs=" + Ms(mark));
                        return Finish(OnlineSwitchExitCodes.GatewayAuthFailed);
                    }
                    _write(LinePrefix + " stage=gateway-login to=" + _to + " outcome=Ok elapsedMs=" + Ms(mark));

                    // ---- 读目录
                    mark = _clock.ElapsedMilliseconds;
                    (ChannelDirectoryLoadResult directory, string directoryFault) = await GuardAsync(
                        () => session.LoadChannelDirectoryAsync(_cancellationToken)).ConfigureAwait(false);
                    if (directory == null || !directory.Succeeded || directory.Snapshot == null)
                    {
                        string detail = directory == null
                            ? " outcome=Faulted fault=" + directoryFault
                            : " outcome=" + (directory.Succeeded ? "NoSnapshot" : directory.Outcome.ToString());
                        _write(LinePrefix + " stage=directory to=" + _to + detail + " elapsedMs=" + Ms(mark));
                        return Finish(OnlineSwitchExitCodes.DirectoryFailed);
                    }
                    _write(LinePrefix + " stage=directory to=" + _to + " outcome=Ok elapsedMs=" + Ms(mark));

                    // ---- 选目标(按序号时在连任何频道之前就选好;选不出就一个频道也不连)
                    ChannelEntry target = null;
                    if (_args.TargetIsIndex)
                    {
                        target = SelectByIndex(directory.Snapshot.Channels, session.GameServerHost, settings.ChannelPort, _args.ToIndex);
                        if (target == null)
                        {
                            _write(LinePrefix + " stage=target to=" + _to + " outcome=NoTarget");
                            return Finish(OnlineSwitchExitCodes.NoTarget);
                        }
                    }

                    // ---- 连第一个频道(设置里的频道端口),记下角色列表
                    mark = _clock.ElapsedMilliseconds;
                    (OnlineConnectResult connected, string connectFault) = await GuardAsync(
                        () => session.ConnectAsync(settings.ChannelPort, _cancellationToken)).ConfigureAwait(false);
                    Record(_first, connected);
                    _write(StepLine("connect", connected, _first, session.ClientVersion, Ms(mark), connectFault));
                    if (_first.Outcome != OnlineSwitchStepOutcome.Ok)
                    {
                        return Finish(OnlineSwitchExitCodes.FirstConnectFailed);
                    }

                    // ---- 换频道:关旧连接、同一令牌连目标、重新游戏登录、再取角色列表(会话内部的有限重试是产品行为)
                    mark = _clock.ElapsedMilliseconds;
                    (OnlineConnectResult switched, string switchFault) = await GuardAsync(
                        () => SwitchAsync(session, target)).ConfigureAwait(false);
                    Record(_second, switched);
                    _write(StepLine("switch", switched, _second, session.ClientVersion, Ms(mark), switchFault));
                    if (_second.Outcome != OnlineSwitchStepOutcome.Ok)
                    {
                        return Finish(OnlineSwitchExitCodes.SwitchFailed);
                    }

                    // ---- 断开结束(不选角、不进城、不发命令);断开本身出异常不改变"两步都成功"的结论
                    mark = _clock.ElapsedMilliseconds;
                    string disconnectFault = null;
                    try
                    {
                        await session.DisconnectAsync(_cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        disconnectFault = e.GetBaseException().GetType().Name;
                    }
                    _write(LinePrefix + " stage=disconnect to=" + _to + " outcome=" + (disconnectFault == null ? "Ok" : "Faulted fault=" + disconnectFault)
                        + " elapsedMs=" + Ms(mark));
                    return Finish(OnlineSwitchExitCodes.Success);
                }
            }

            /// <summary>打最后一行并原样返回退出码。</summary>
            public int Finish(int exitCode)
            {
                _write(LinePrefix + " result=" + (exitCode == OnlineSwitchExitCodes.Success ? "ok" : "fail")
                    + " first=" + _first.Outcome + " second=" + _second.Outcome
                    + " attempts1=" + Num(_first.Attempts) + " attempts2=" + Num(_second.Attempts)
                    + " group1=" + Num(_first.Group) + " group2=" + Num(_second.Group)
                    + " clientVersion=" + Num(_clientVersion) + " elapsedMs=" + Num(_clock.ElapsedMilliseconds));
                return exitCode;
            }

            // 目标按序号选时,目录条目自带主机的就连它的主机与端口;没写主机的就是网关给的那个主机上的端口。按端口选时是同一主机上的指定端口。
            private Task<OnlineConnectResult> SwitchAsync(IOnlineSwitchSession session, ChannelEntry target)
            {
                if (target == null)
                {
                    return session.SwitchChannelAsync(_args.ToPort, _cancellationToken);
                }
                return string.IsNullOrWhiteSpace(target.Host)
                    ? session.SwitchChannelAsync(target.Port, _cancellationToken)
                    : session.SwitchChannelAsync(target.Host.Trim(), target.Port, _cancellationToken);
            }

            // 会话调用抛异常(取消除外)时收成"没有结果 + 异常类型名",由调用方当作这一步失败
            private static async Task<(T Value, string Fault)> GuardAsync<T>(Func<Task<T>> call) where T : class
            {
                try
                {
                    return (await call().ConfigureAwait(false), null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    return (null, e.GetBaseException().GetType().Name);
                }
            }

            private static void Record(StepReport report, OnlineConnectResult result)
            {
                report.Outcome = OutcomeOf(result);
                report.Attempts = result == null ? 0 : result.GameLoginAttempts;
                report.Group = result == null ? (ushort)0 : result.EffectiveServerGroup;
            }

            private string StepLine(string stage, OnlineConnectResult result, StepReport report, uint clientVersion, string elapsed, string fault)
            {
                var sb = new StringBuilder(LinePrefix);
                sb.Append(" stage=").Append(stage).Append(" to=").Append(_to).Append(" outcome=").Append(report.Outcome);
                if (result == null)
                {
                    sb.Append(" failure=- reached=- loginFailureCode=- characterList=NotReceived characters=-");
                    if (fault != null)
                    {
                        sb.Append(" fault=").Append(fault);
                    }
                }
                else
                {
                    sb.Append(" failure=").Append(result.Failure).Append(" reached=").Append(result.Stage);
                    sb.Append(" loginFailureCode=").Append(result.LoginFailureCode.HasValue ? Num(result.LoginFailureCode.Value) : "-");
                    sb.Append(" characterList=").Append(result.CharacterListStatus);
                    sb.Append(" characters=").Append(result.CharacterListStatus == OnlineCharacterListStatus.Received && result.CharacterList != null
                        ? Num(result.CharacterList.Entries.Count)
                        : "-");
                }
                sb.Append(" attempts=").Append(Num(report.Attempts));
                sb.Append(" serverGroup=").Append(Num(report.Group));
                sb.Append(" clientVersion=").Append(Num(clientVersion));
                sb.Append(" elapsedMs=").Append(elapsed);
                return sb.ToString();
            }

            private string Ms(long mark)
            {
                return Num(_clock.ElapsedMilliseconds - mark);
            }
        }
    }
}
