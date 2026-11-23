using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Net.ChannelDirectory;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 探针 <c>switch --list</c> 的流程(只读的"目录摘要"):读本地设置 → 网关登录(整个流程只一次,失败立即结束、不重试)→ 读频道目录 → 逐条目打脱敏摘要 → 结束并释放会话。
    /// <b>不连任何频道</b>(从不调用 <see cref="IOnlineSwitchSession.ConnectAsync"/> 与 <c>SwitchChannelAsync</c>)、不做游戏登录、不选角、不发任何命令包;
    /// 只有两个网络步骤:网关登录与读目录。用途:真服换频道验证之前,先看到哪个序号会被 <c>--to-index</c> 选中、各频道会写什么服务器组。
    /// <para>
    /// 输出全部以 <c>PROBE_SWITCH </c> 开头,只含小整数与枚举名:不含账号、口令、令牌、主机、IP、端口号、URL、频道名、组标签文字、角色名与任何载荷字节
    /// (异常只带类型名)。退出码:0 成功;2 本地设置装配失败(没有创建会话);5 网关登录失败;6 读目录失败;15 被取消;70 流程自己没料到的异常
    /// (命令行用法错误的 64 在更前面就返回了,没有读设置、没有联网)。最后一行总是 <c>PROBE_SWITCH result=ok|fail mode=list</c>,失败时带 <c>failedStage=</c>。
    /// </para>
    /// </summary>
    internal sealed class OnlineSwitchListRun
    {
        /// <summary>阶段行里 <c>to=</c> 的值(没有换频道目标,用一个固定记号)。</summary>
        internal const string ToToken = "list";

        /// <summary>阶段名:读本地设置并拿会话。</summary>
        internal const string StageSettings = "settings";

        /// <summary>阶段名:网关登录。</summary>
        internal const string StageGatewayLogin = "gateway-login";

        /// <summary>阶段名:读频道目录。</summary>
        internal const string StageDirectory = "directory";

        /// <summary>阶段名:拼摘要并逐行输出(只在进程内算,不联网;出错就是流程自己的缺陷)。</summary>
        internal const string StageSummary = "summary";

        /// <summary>会话调用没抛异常却给了 null 结果时,<c>fault=</c> 的值(固定记号,不含任何运行时文本)。</summary>
        internal const string NullResultFault = "NullResult";

        private readonly Action<string> _write;
        private readonly CancellationToken _cancellationToken;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private string _stage = StageSettings;

        private OnlineSwitchListRun(Action<string> write, CancellationToken cancellationToken)
        {
            _write = write;
            _cancellationToken = cancellationToken;
        }

        /// <summary>跑一遍,返回退出码;取消与没料到的异常都收成退出码并照样打最后一行。</summary>
        internal static async Task<int> RunAsync(OnlineSwitchArguments args, string defaultRepoRoot, IOnlineSwitchPort port,
            Action<string> writeLine, CancellationToken cancellationToken)
        {
            var run = new OnlineSwitchListRun(writeLine, cancellationToken);
            try
            {
                return await run.ExecuteAsync(args.RepoRoot ?? defaultRepoRoot, port).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                writeLine(OnlineSwitchProbe.LinePrefix + " stage=abort outcome=Cancelled");
                return run.Finish(OnlineSwitchExitCodes.Cancelled);
            }
            catch (Exception e)
            {
                writeLine(OnlineSwitchProbe.LinePrefix + " stage=abort outcome=Unexpected fault=" + e.GetBaseException().GetType().Name);
                return run.Finish(OnlineSwitchExitCodes.Unexpected);
            }
        }

        private async Task<int> ExecuteAsync(string repoRoot, IOnlineSwitchPort port)
        {
            // ---- 装配:读本地设置并拿会话(命令行没有任何开关要覆盖,设置原样用)
            OnlineEntrySettings settings = null;
            IOnlineSwitchSession session = null;
            string fault = null;
            try
            {
                if (port.TryLoadSettings(repoRoot, out settings, out _) && settings != null)
                {
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
                _write(Stage(StageSettings) + " outcome=SettingsFailed" + (fault == null ? string.Empty : " fault=" + fault));
                return Finish(OnlineSwitchExitCodes.SettingsFailed);
            }

            using (session)
            {
                _write(Stage(StageSettings) + " outcome=Ok");

                // ---- 网关登录:整个流程只此一次,任何网关层失败都立即结束
                _stage = StageGatewayLogin;
                long mark = _clock.ElapsedMilliseconds;
                (OnlineLoginResult login, string loginFault) = await GuardAsync(
                    () => session.LoginAsync(settings.Account, settings.Password, _cancellationToken)).ConfigureAwait(false);
                if (login == null || !login.IsSuccess)
                {
                    string detail = login == null
                        ? " outcome=Faulted fault=" + (loginFault ?? NullResultFault)
                        : " outcome=Failed failure=" + login.Failure + " reached=" + login.Stage + " gateway=" + login.GatewayOutcome
                            + " gatewayError=" + login.GatewayErrorCode;
                    _write(Stage(StageGatewayLogin) + detail + " elapsedMs=" + Ms(mark));
                    return Finish(OnlineSwitchExitCodes.GatewayAuthFailed);
                }
                _write(Stage(StageGatewayLogin) + " outcome=Ok elapsedMs=" + Ms(mark));

                // ---- 读目录:单次只读,不重试
                _stage = StageDirectory;
                mark = _clock.ElapsedMilliseconds;
                (ChannelDirectoryLoadResult directory, string directoryFault) = await GuardAsync(
                    () => session.LoadChannelDirectoryAsync(_cancellationToken)).ConfigureAwait(false);
                if (directory == null || !directory.Succeeded || directory.Snapshot == null)
                {
                    string detail = directory == null
                        ? " outcome=Faulted fault=" + (directoryFault ?? NullResultFault)
                        : " outcome=" + (directory.Succeeded ? "NoSnapshot" : directory.Outcome.ToString());
                    _write(Stage(StageDirectory) + detail + " elapsedMs=" + Ms(mark));
                    return Finish(OnlineSwitchExitCodes.DirectoryFailed);
                }
                _write(Stage(StageDirectory) + " outcome=Ok elapsedMs=" + Ms(mark));

                // ---- 摘要:第一个频道 = 网关给的主机加设置里的频道端口(没有连接,所以只用网关给的主机)
                _stage = StageSummary;
                OnlineDirectorySummary summary = OnlineDirectorySummary.Build(directory.Snapshot.Channels, session.GameServerHost, settings.ChannelPort);
                foreach (string line in summary.ToLines())
                {
                    _write(line);
                }
                return Finish(OnlineSwitchExitCodes.Success);
            }
        }

        /// <summary>打最后一行并原样返回退出码:<c>result=ok mode=list elapsedMs=…</c>;失败时在 mode 之后带 <c>failedStage=</c>(出错时正在做的阶段)。</summary>
        private int Finish(int exitCode)
        {
            bool ok = exitCode == OnlineSwitchExitCodes.Success;
            _write(OnlineSwitchProbe.LinePrefix + " result=" + (ok ? "ok" : "fail") + " mode=" + ToToken
                + (ok ? string.Empty : " failedStage=" + _stage)
                + " elapsedMs=" + Num(_clock.ElapsedMilliseconds));
            return exitCode;
        }

        private static string Stage(string stage)
        {
            return OnlineSwitchProbe.LinePrefix + " stage=" + stage + " to=" + ToToken;
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

        private string Ms(long mark)
        {
            return Num(_clock.ElapsedMilliseconds - mark);
        }

        private static string Num(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
