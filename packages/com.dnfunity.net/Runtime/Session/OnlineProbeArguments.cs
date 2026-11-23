using System;
using System.Collections.Generic;
using System.Globalization;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 控制台探针 <c>orchestrated</c>(新增两个开关部分)与 <c>switch</c> 子命令的命令行哪里不对(只给问题名,不回显用户输入的内容)。
    /// 出现任何一种问题,探针都只打一行用法提示并以退出码 64 结束,不读本地配置、不发起任何网络访问。
    /// </summary>
    public enum OnlineProbeArgumentProblem
    {
        /// <summary>没问题。</summary>
        None = 0,

        /// <summary><c>--client-version</c> 缺值,或不是 0 到 4294967295 的纯十进制数字(含负号、空白、十六进制、超出 u32)。</summary>
        BadClientVersion,

        /// <summary><c>--server-group</c> 缺值,或不是 0 到 17 的纯十进制数字。</summary>
        BadServerGroup,

        /// <summary><c>--to-index</c> 缺值,或不是非负的纯十进制数字(最多 9 位)。</summary>
        BadToIndex,

        /// <summary><c>--to-port</c> 缺值,或不是 1 到 65535 的纯十进制数字。</summary>
        BadToPort,

        /// <summary><c>--to-index</c> 与 <c>--to-port</c> 一个都没给(二选一,必须给一个)。</summary>
        MissingTarget,

        /// <summary><c>--to-index</c> 与 <c>--to-port</c> 同时给了(二选一)。</summary>
        ConflictingTargets,

        /// <summary>同一个选项出现了不止一次(哪怕两次的值相同)。</summary>
        DuplicateOption,

        /// <summary>出现了不认识的参数,或仓库根不在第一个位置。</summary>
        UnknownArgument,
    }

    /// <summary>
    /// 探针里 <c>--client-version</c>、<c>--server-group</c> 两个开关的取值解析(纯函数,不读环境、不连网):
    /// 只认纯十进制数字(不认正负号、空白、十六进制),越界就是错,不悄悄截断。
    /// </summary>
    public static class OnlineProbeKnobParser
    {
        // u32 最大 4294967295,十进制 10 位;更长的一律不收(也避免前导零堆出超长文本)
        private const int MaxClientVersionDigits = 10;

        // 其余整数选项的文本长度上限,与 fight 子命令的数值参数同一约定
        private const int MaxIntDigits = 9;

        /// <summary>客户端版本号:0 到 4294967295。</summary>
        public static bool TryParseClientVersion(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || text.Length > MaxClientVersionDigits)
            {
                return false;
            }
            return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>服务器组:0 到 <see cref="LoginRequest.MaxServerGroup"/>(17)。</summary>
        public static bool TryParseServerGroup(string text, out ushort value)
        {
            value = 0;
            if (!TryParseInt(text, out int parsed) || parsed > LoginRequest.MaxServerGroup)
            {
                return false;
            }
            value = (ushort)parsed;
            return true;
        }

        /// <summary>目录序号(<c>--to-index</c>):从 0 起的非负整数。</summary>
        public static bool TryParseToIndex(string text, out int value)
        {
            return TryParseInt(text, out value);
        }

        /// <summary>频道端口(<c>--to-port</c>):1 到 65535。</summary>
        public static bool TryParseToPort(string text, out int value)
        {
            if (TryParseInt(text, out value) && value >= 1 && value <= 65535)
            {
                return true;
            }
            value = 0;
            return false;
        }

        private static bool TryParseInt(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || text.Length > MaxIntDigits)
            {
                return false;
            }
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>
    /// 探针 <c>orchestrated</c> 子命令新增的两个开关 <c>--client-version &lt;u32&gt;</c> 与 <c>--server-group &lt;0..17&gt;</c>。
    /// 只认这两个选项:仓库根、<c>--stay N</c> 与历史上的各种选项一概不管(它们仍由探针自己按原来的松散写法读),所以不给这两个选项时与以前完全一样。
    /// 同一个选项出现多次、缺值、值不合规都是用法错误。
    /// </summary>
    public sealed class OnlineOrchestratedKnobs
    {
        private OnlineOrchestratedKnobs()
        {
        }

        /// <summary>给了 <c>--client-version</c> 时的值;没给为 null(不覆盖设置里的值)。</summary>
        public uint? ClientVersion { get; private set; }

        /// <summary>给了 <c>--server-group</c> 时的值;没给为 null(不覆盖设置里的值)。</summary>
        public ushort? ServerGroup { get; private set; }

        /// <summary>解析 <c>orchestrated</c> 之后的全部参数。</summary>
        public static bool TryParse(IReadOnlyList<string> args, out OnlineOrchestratedKnobs parsed, out OnlineProbeArgumentProblem problem)
        {
            parsed = null;
            var result = new OnlineOrchestratedKnobs();
            int count = args == null ? 0 : args.Count;
            for (int i = 0; i < count; i++)
            {
                string token = args[i];
                if (token == "--client-version")
                {
                    if (result.ClientVersion.HasValue)
                    {
                        problem = OnlineProbeArgumentProblem.DuplicateOption;
                        return false;
                    }
                    if (i + 1 >= count || !OnlineProbeKnobParser.TryParseClientVersion(args[i + 1], out uint version))
                    {
                        problem = OnlineProbeArgumentProblem.BadClientVersion;
                        return false;
                    }
                    result.ClientVersion = version;
                    i++;
                }
                else if (token == "--server-group")
                {
                    if (result.ServerGroup.HasValue)
                    {
                        problem = OnlineProbeArgumentProblem.DuplicateOption;
                        return false;
                    }
                    if (i + 1 >= count || !OnlineProbeKnobParser.TryParseServerGroup(args[i + 1], out ushort group))
                    {
                        problem = OnlineProbeArgumentProblem.BadServerGroup;
                        return false;
                    }
                    result.ServerGroup = group;
                    i++;
                }
            }
            parsed = result;
            problem = OnlineProbeArgumentProblem.None;
            return true;
        }

        /// <summary>把给了的开关写进设置(没给的不动)。在读完本地配置之后调用,所以命令行优先于本地配置。</summary>
        public void ApplyTo(OnlineEntrySettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            if (ClientVersion.HasValue)
            {
                settings.ClientVersion = ClientVersion.Value;
            }
            if (ServerGroup.HasValue)
            {
                settings.ServerGroup = ServerGroup.Value;
            }
        }
    }

    /// <summary>
    /// 探针 <c>switch</c> 子命令的命令行:
    /// <c>[仓库根] --to-index &lt;K&gt; | --to-port &lt;N&gt; [--client-version &lt;u32&gt;] [--server-group &lt;0..17&gt;]</c>。
    /// <c>--to-index</c> 与 <c>--to-port</c> 二选一(同时给或都不给都是错);任何选项重复、缺值、值不合规、出现不认识的参数、仓库根不在第一个位置,都是用法错误。
    /// 解析是纯函数,不读环境、不连网;问题只给问题名,不回显用户输入。
    /// </summary>
    public sealed class OnlineSwitchArguments
    {
        private OnlineSwitchArguments()
        {
        }

        /// <summary>仓库根;没给为 null(探针退回当前目录)。</summary>
        public string RepoRoot { get; private set; }

        /// <summary>目标按"目录序号"选时的序号 K(从 0 起);按端口选时为 -1。</summary>
        public int ToIndex { get; private set; } = -1;

        /// <summary>目标按"同一主机上的端口"选时的端口;按序号选时为 0。</summary>
        public int ToPort { get; private set; }

        /// <summary>目标是按序号选(<c>--to-index</c>)还是按端口选(<c>--to-port</c>)。</summary>
        public bool TargetIsIndex => ToIndex >= 0;

        /// <summary>给了 <c>--client-version</c> 时的值;没给为 null。</summary>
        public uint? ClientVersion { get; private set; }

        /// <summary>给了 <c>--server-group</c> 时的值;没给为 null。</summary>
        public ushort? ServerGroup { get; private set; }

        /// <summary>解析 <c>switch</c> 之后的全部参数。</summary>
        public static bool TryParse(IReadOnlyList<string> args, out OnlineSwitchArguments parsed, out OnlineProbeArgumentProblem problem)
        {
            parsed = null;
            var result = new OnlineSwitchArguments();
            bool haveIndex = false;
            bool havePort = false;
            bool haveVersion = false;
            bool haveGroup = false;
            int count = args == null ? 0 : args.Count;
            for (int i = 0; i < count; i++)
            {
                string token = args[i];
                if (token == null)
                {
                    problem = OnlineProbeArgumentProblem.UnknownArgument;
                    return false;
                }
                if (i == 0 && !token.StartsWith("--", StringComparison.Ordinal))
                {
                    result.RepoRoot = token;
                    continue;
                }

                switch (token)
                {
                    case "--to-index":
                    {
                        if (haveIndex)
                        {
                            problem = OnlineProbeArgumentProblem.DuplicateOption;
                            return false;
                        }
                        if (i + 1 >= count || !OnlineProbeKnobParser.TryParseToIndex(args[i + 1], out int index))
                        {
                            problem = OnlineProbeArgumentProblem.BadToIndex;
                            return false;
                        }
                        result.ToIndex = index;
                        haveIndex = true;
                        i++;
                        break;
                    }
                    case "--to-port":
                    {
                        if (havePort)
                        {
                            problem = OnlineProbeArgumentProblem.DuplicateOption;
                            return false;
                        }
                        if (i + 1 >= count || !OnlineProbeKnobParser.TryParseToPort(args[i + 1], out int targetPort))
                        {
                            problem = OnlineProbeArgumentProblem.BadToPort;
                            return false;
                        }
                        result.ToPort = targetPort;
                        havePort = true;
                        i++;
                        break;
                    }
                    case "--client-version":
                    {
                        if (haveVersion)
                        {
                            problem = OnlineProbeArgumentProblem.DuplicateOption;
                            return false;
                        }
                        if (i + 1 >= count || !OnlineProbeKnobParser.TryParseClientVersion(args[i + 1], out uint version))
                        {
                            problem = OnlineProbeArgumentProblem.BadClientVersion;
                            return false;
                        }
                        result.ClientVersion = version;
                        haveVersion = true;
                        i++;
                        break;
                    }
                    case "--server-group":
                    {
                        if (haveGroup)
                        {
                            problem = OnlineProbeArgumentProblem.DuplicateOption;
                            return false;
                        }
                        if (i + 1 >= count || !OnlineProbeKnobParser.TryParseServerGroup(args[i + 1], out ushort group))
                        {
                            problem = OnlineProbeArgumentProblem.BadServerGroup;
                            return false;
                        }
                        result.ServerGroup = group;
                        haveGroup = true;
                        i++;
                        break;
                    }
                    default:
                        problem = OnlineProbeArgumentProblem.UnknownArgument;
                        return false;
                }
            }

            // 目标二选一:每个参数都读得通之后才判(不分先后)
            if (haveIndex && havePort)
            {
                problem = OnlineProbeArgumentProblem.ConflictingTargets;
                return false;
            }
            if (!haveIndex && !havePort)
            {
                problem = OnlineProbeArgumentProblem.MissingTarget;
                return false;
            }

            parsed = result;
            problem = OnlineProbeArgumentProblem.None;
            return true;
        }
    }
}
