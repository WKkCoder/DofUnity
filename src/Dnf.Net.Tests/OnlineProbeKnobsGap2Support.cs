using System.Text.RegularExpressions;
using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// <c>OnlineProbeKnobsGap2*</c> 一组测试共用的小件:跑一遍 <c>switch</c> 并把输出行里的耗时归一成 N。
/// 内容是从既有测试里的私有辅助(跑一遍、把行尾 <c>elapsedMs=数字</c> 换成 <c>elapsedMs=N</c>)复制出来的,不依赖那些私有类。
/// </summary>
internal static class OnlineProbeKnobsGap2Support
{
    // 探针里每一行带耗时的输出,耗时都在行尾(<c>elapsedMs=数字</c>);数值是真实秒表,不可预期,统一换成 N 再逐字比较。
    // 只换行尾的这一处:行里别处的 elapsedMs 字样(比如被改坏的令牌 elapsedMt=…)不会被换掉,逐字比较时就会对不上。
    private static readonly Regex ElapsedAtEnd = new(@"elapsedMs=\d+$");

    internal static string Norm(string line)
    {
        return ElapsedAtEnd.Replace(line, "elapsedMs=N");
    }

    internal static string[] Norm(IEnumerable<string> lines)
    {
        return lines.Select(Norm).ToArray();
    }

    /// <summary>跑一遍 <c>switch</c>(命令行按空格拆);返回退出码与归一后的全部输出行。</summary>
    internal static async Task<(int Exit, string[] Lines)> Run(IOnlineSwitchPort port, string commandLine, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        string[] args = commandLine.Length == 0 ? Array.Empty<string>() : commandLine.Split(' ');
        int exit = await GatewayTestSupport.Bounded(OnlineSwitchProbe.RunAsync(args, DefaultRoot, port, lines.Add, cancellationToken), 10);
        return (exit, Norm(lines));
    }
}
