using Dnf.Net.Session;

namespace Dnf.NetProbe;

/// <summary>
/// <c>switch</c> 子命令的外壳:只把"读本机配置、拿真会话、接 Ctrl+C、逐行打印"接到 Net 层的 <see cref="OnlineSwitchProbe"/> 上,
/// 命令行解析、目标选择、各步判定、退出码与输出行的拼法都在 Net 层(有单元测试),这里不含任何协议或判定逻辑。
///
/// 用法:
///   dnf-netprobe switch [仓库根] --to-index &lt;K&gt; | --to-port &lt;N&gt; [--client-version &lt;u32&gt;] [--server-group &lt;0..17&gt;]
///
/// 只读验证:一条会话、网关只登录一次 → 读频道目录 → 连第一个频道(设置里的频道端口)并记下角色列表 → 换频道到目标并记下角色列表 → 断开。
/// 不选角、不进城、不发任何命令包,也没有任何循环重试(会话内部台账 E-443 的有限重试是产品行为,原样保留)。
/// <c>--to-index K</c> = 频道目录里"端口有效、并且与第一个频道不是同一个主机加端口"的频道按目录顺序排的第 K 个(从 0 起);
/// 不够 K+1 个就一个频道也不连,退出码 7。<c>--to-port N</c> = 同一主机上的指定端口。二选一,同时给或都不给是用法错误。
///
/// 输出一律脱敏:每行以 <c>PROBE_SWITCH </c> 开头,只含阶段名、结局枚举名、登录失败码、角色列表状态与条数、游戏登录次数、
/// 实际写进登录请求的服务器组与客户端版本号(小整数)、各步耗时毫秒与 <c>to=index:K</c> 或 <c>to=port</c> 这样的序号式记号(异常只带类型名);
/// 不含账号、口令、令牌、主机、IP、端口号、URL、频道名、角色名与任何载荷字节。最后一行是
/// <c>PROBE_SWITCH result=ok|fail first=&lt;结局&gt; second=&lt;结局&gt; attempts1=&lt;n&gt; attempts2=&lt;n&gt; group1=&lt;n&gt; group2=&lt;n&gt; clientVersion=&lt;n&gt; elapsedMs=&lt;n&gt;</c>。
///
/// 退出码:0 两步都成功且两次都收到角色列表;2 本地配置装配失败;3 第一个频道连接失败;4 换频道失败;
/// 5 网关认证失败(立即结束,绝不重试);6 读目录失败;7 目录里选不出目标;64 用法错误(没读配置、没有任何网络访问)。
/// 另有两个规格之外的补充:15 被取消(Ctrl+C)、70 流程自己没料到的异常(只打异常类型名);两者照样打最后一行。
/// </summary>
internal static class SwitchCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var rest = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            rest.Add(args[i]);
        }

        using var cancel = new CancellationTokenSource();
        int pressed = 0;
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // 第一次 Ctrl+C:让流程自己收场(关连接、打最后一行);再按一次就不拦了,进程直接结束
            if (Interlocked.Increment(ref pressed) == 1)
            {
                e.Cancel = true;
                cancel.Cancel();
            }
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return await OnlineSwitchProbe.RunAsync(rest, Directory.GetCurrentDirectory(), new ProductPort(), Console.WriteLine, cancel.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    // 产品里的端口:读本机配置(与 orchestrated 同一个 LocalDevEntrySettings.TryLoad)、拿真会话。
    private sealed class ProductPort : IOnlineSwitchPort
    {
        public bool TryLoadSettings(string repoRoot, out OnlineEntrySettings settings, out string problem)
        {
            return LocalDevEntrySettings.TryLoad(repoRoot, out settings, out problem);
        }

        public IOnlineSwitchSession CreateSession(OnlineEntrySettings settings)
        {
            return new OnlineClientSwitchSession(new OnlineClientSession(settings));
        }
    }
}
