using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>读本地开发入参的委托形状(产品里就是 <see cref="LocalDevEntrySettings.TryLoad"/>;测试里换成合成设置)。</summary>
    public delegate bool OnlineSettingsLoader(string repoRoot, out OnlineEntrySettings settings, out string problem);

    /// <summary>
    /// 探针 <c>orchestrated</c> 子命令进城之前的那一段(只管命令行与入参装配,不连网):
    /// 先解析 <c>--client-version</c> 与 <c>--server-group</c>(不合规就只打一行用法提示、返回 64,<b>不去读本地配置</b>、更不会有网络访问),
    /// 再读本地配置,最后用命令行给了的开关覆盖设置(命令行优先于本地配置)。两个开关都不给时,行为与这一段加进来之前完全一样。
    /// </summary>
    public static class OnlineOrchestratedSetup
    {
        /// <summary>用法行(用法错误时打这一行)。</summary>
        public const string UsageLine = "用法: dnf-netprobe orchestrated [仓库根] [--stay N] [--client-version <u32>] [--server-group <0..17>]";

        /// <summary>
        /// 装配入参。<paramref name="args"/> 是 <c>orchestrated</c> 之后的全部参数;仓库根只能是第一个且不以 <c>--</c> 开头的那个(与以前一致),
        /// 没给就用 <paramref name="defaultRepoRoot"/>。返回 0:可以继续进城,<paramref name="settings"/> 已带上覆盖;
        /// 返回 <see cref="OnlineSwitchExitCodes.Usage"/>:命令行不合规,已打用法行,没有调用 <paramref name="load"/>;
        /// 返回 <see cref="OnlineSwitchExitCodes.SettingsFailed"/>:本地配置装配失败,已按原来的写法打了"装配失败: …"。
        /// </summary>
        public static int Prepare(IReadOnlyList<string> args, string defaultRepoRoot, OnlineSettingsLoader load,
            Action<string> writeLine, out OnlineEntrySettings settings)
        {
            if (load == null)
            {
                throw new ArgumentNullException(nameof(load));
            }
            if (writeLine == null)
            {
                throw new ArgumentNullException(nameof(writeLine));
            }

            settings = null;
            if (!OnlineOrchestratedKnobs.TryParse(args, out OnlineOrchestratedKnobs knobs, out _))
            {
                writeLine(UsageLine);
                return OnlineSwitchExitCodes.Usage;
            }

            string repoRoot = args != null && args.Count >= 1 && args[0] != null && !args[0].StartsWith("--", StringComparison.Ordinal)
                ? args[0]
                : defaultRepoRoot;
            if (!load(repoRoot, out OnlineEntrySettings loaded, out string problem))
            {
                writeLine("装配失败: " + problem);
                return OnlineSwitchExitCodes.SettingsFailed;
            }

            knobs.ApplyTo(loaded);
            settings = loaded;
            return 0;
        }
    }
}
