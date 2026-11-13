using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dnf.Net.ChannelDirectory;
using Dnf.Net.Gateway;
using Dnf.Net.Session;

namespace Dnf.NetProbe;

/// <summary>
/// Channel Service(7001)只读联网探针草稿(未联网运行;严格脱敏)。
/// <para>
/// 预注册约束:
/// 1) 每次运行最多执行 1 次网关只读登录,用于获取权威服务端地址;
/// 2) 任何认证失败、限流(429)、获取地址失败或频道协议异常立即以非零退出码终止,绝不自动重试;
/// 3) 输出仅限状态分类、状态码、字节计数、组/频道数量与档位分布;严禁输出账号、口令、IP、端口、URL、令牌、频道原名或原始载荷字节。
/// </para>
/// </summary>
internal static class ChannelProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        string repoRoot = FindRepoRootArg(args) ?? Directory.GetCurrentDirectory();
        int port = IntArg(args, "--channel-port", ChannelServiceConstants.DefaultPort);
        string cachedVersion = StrArg(args, "--version", ChannelServiceConstants.DefaultScriptVersion);

        if (!LocalDevEntrySettings.TryLoad(repoRoot, out OnlineEntrySettings settings, out string problem))
        {
            Console.WriteLine("配置装配失败: " + problem);
            return 2;
        }

        // 网关传输必须关系统代理(UseProxy = false):本机有系统代理时,健康检查会被代理截走而误报成退出码 3;
        // 与 live 子命令的写法一致。传输由本方法自己释放(公开构造的 GatewayClient 不拥有传输)。
        using var transport = new HttpGatewayTransport(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }, settings.Gateway.Timeout);
        using var gateway = new GatewayClient(settings.Gateway, transport);

        GatewayResult health = await gateway.HealthAsync();
        Console.WriteLine("网关健康检查: " + health.Outcome + " (HTTP " + health.HttpStatus + ")");
        if (!health.IsSuccess)
        {
            return 3;
        }

        // 预算上限: 单次只读登录,失败立刻非零退出,绝不重试
        GatewayResult login = await gateway.LoginAsync(settings.Account, settings.Password);
        Console.WriteLine("网关登录: " + login.Outcome + " (HTTP " + login.HttpStatus + "), 令牌长度=" + (login.Token == null ? 0 : login.Token.Length));
        if (!login.IsSuccess || string.IsNullOrEmpty(login.Token))
        {
            return 4;
        }

        GatewayResult serverIp = await gateway.GetGameServerIpAsync();
        Console.WriteLine("服务端地址查询: " + serverIp.Outcome + " (HTTP " + serverIp.HttpStatus + "), 有返回值=" + (!string.IsNullOrWhiteSpace(serverIp.Text)));
        if (!serverIp.IsSuccess || string.IsNullOrWhiteSpace(serverIp.Text))
        {
            return 5;
        }

        ChannelDirectoryLoadResult result = await ChannelDirectoryClient.LoadAsync(
            serverIp.Text.Trim(),
            port,
            cachedVersion,
            timeout: TimeSpan.FromSeconds(10));

        Console.WriteLine(
            "频道目录结局: " + result.Outcome
            + ", 握手码=" + (result.HandshakeStatus.HasValue ? result.HandshakeStatus.Value.ToString() : "-")
            + ", 脚本最新=" + (result.VersionUpToDate.HasValue ? result.VersionUpToDate.Value.ToString() : "-")
            + ", 解析状态=" + (result.ParseStatus.HasValue ? result.ParseStatus.Value.ToString() : "-"));

        if (!result.Succeeded || result.Snapshot == null)
        {
            if (!string.IsNullOrEmpty(result.ErrorDetail))
            {
                Console.WriteLine("失败摘要: " + result.ErrorDetail);
            }
            return 6;
        }

        ChannelDirectorySnapshot snap = result.Snapshot;
        int fullCount = snap.Channels.Count(c => c.Status == ChannelCongestionTier.Full);
        int crowdedCount = snap.Channels.Count(c => c.Status == ChannelCongestionTier.Crowded);
        int normalCount = snap.Channels.Count(c => c.Status == ChannelCongestionTier.Normal);
        int harmonyCount = snap.Channels.Count(c => c.Status == ChannelCongestionTier.Harmony);
        int comfortableCount = snap.Channels.Count(c => c.Status == ChannelCongestionTier.Comfortable);
        int validPortCount = snap.Channels.Count(c => c.Flags.HasFlag(ChannelEntryFlags.HasValidPort));

        Console.WriteLine(
            $"快照汇总: 组数={snap.Groups.Count}, 频道总数={snap.Channels.Count}, 推荐规则={snap.ScriptData.RecommendLevels.Count}, 有效端口数={validPortCount}");
        Console.WriteLine(
            $"拥挤档位分布: 顺畅={comfortableCount}, 良好={harmonyCount}, 普通={normalCount}, 拥挤={crowdedCount}, 满载={fullCount}");
        return 0;
    }

    private static string FindRepoRootArg(string[] args)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--channel-port" || args[i] == "--version")
            {
                i++;
                continue;
            }
            if (!args[i].StartsWith("--"))
            {
                return args[i];
            }
        }
        return null;
    }

    private static int IntArg(string[] args, string flag, int fallback)
    {
        for (int i = 1; i + 1 < args.Length; i++)
        {
            if (args[i] == flag && int.TryParse(args[i + 1], out int v))
            {
                return v;
            }
        }
        return fallback;
    }

    private static string StrArg(string[] args, string flag, string fallback)
    {
        for (int i = 1; i + 1 < args.Length; i++)
        {
            if (args[i] == flag)
            {
                return args[i + 1];
            }
        }
        return fallback;
    }
}
