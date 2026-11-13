using System;
using System.Collections.Generic;
using System.Globalization;

namespace Dnf.Net.Session
{
    /// <summary>控制台探针 <c>fight</c> 子命令的命令行参数哪里不对(只给问题名,不回显用户输入的内容)。</summary>
    public enum OnlineFightArgumentProblem
    {
        /// <summary>没问题。</summary>
        None = 0,

        /// <summary>没给 <c>--dungeon</c>。</summary>
        MissingDungeon,

        /// <summary><c>--dungeon</c> 后面缺值,或副本编号不是 0 到 65535 的纯数字、模式不是 0 到 4。</summary>
        BadDungeon,

        /// <summary><c>--pace-ms</c> 缺值或不是 0 到 <see cref="OnlineFightArguments.MaxPaceMs"/> 的纯数字。</summary>
        BadPaceMs,

        /// <summary><c>--clear-wait</c> 缺值或不是 1 到 <see cref="OnlineFightArguments.MaxWaitSeconds"/> 的纯数字(秒)。</summary>
        BadClearWait,

        /// <summary><c>--settle-wait</c> 缺值或不是 1 到 <see cref="OnlineFightArguments.MaxWaitSeconds"/> 的纯数字(秒)。</summary>
        BadSettleWait,

        /// <summary><c>--stay</c> 缺值或不是 0 到 <see cref="OnlineFightArguments.MaxWaitSeconds"/> 的纯数字(秒)。</summary>
        BadStay,

        /// <summary>出现了不认识的参数,或仓库根不在第一个位置。</summary>
        UnknownArgument,
    }

    /// <summary>
    /// 探针 <c>fight</c> 子命令的命令行:<c>[仓库根] --dungeon &lt;副本编号&gt; &lt;模式 0–4&gt; [--pace-ms N] [--clear-wait S] [--settle-wait S] [--continue-on-refuse] [--stay S]</c>。
    /// 解析是纯函数,不读环境、不连网;数值一律只认纯数字(不认正负号与空白),越界就是错,不悄悄截断。
    /// </summary>
    public sealed class OnlineFightArguments
    {
        /// <summary>--pace-ms 的上限(毫秒):一分钟。</summary>
        public const int MaxPaceMs = 60000;

        /// <summary>--clear-wait、--settle-wait、--stay 的上限(秒):一小时。</summary>
        public const int MaxWaitSeconds = 3600;

        private OnlineFightArguments()
        {
        }

        /// <summary>仓库根;没给为 null(探针退回当前目录)。</summary>
        public string RepoRoot { get; private set; }

        /// <summary>副本编号(0 到 65535)。</summary>
        public int DungeonId { get; private set; }

        /// <summary>模式(0 到 4)。</summary>
        public int Mode { get; private set; }

        /// <summary>相邻两次排击杀的间隔(毫秒),缺省 <see cref="OnlineFightOptions.DefaultPaceMs"/>。</summary>
        public int PaceMs { get; private set; } = OnlineFightOptions.DefaultPaceMs;

        /// <summary>等清图通知的最长时间,缺省 <see cref="OnlineFightOptions.DefaultClearWait"/>。</summary>
        public TimeSpan ClearWait { get; private set; } = OnlineFightOptions.DefaultClearWait;

        /// <summary>等两段结算通知的最长时间,缺省 <see cref="OnlineFightOptions.DefaultSettleWait"/>。</summary>
        public TimeSpan SettleWait { get; private set; } = OnlineFightOptions.DefaultSettleWait;

        /// <summary>首次被拒后是否继续排击杀,缺省 false。</summary>
        public bool ContinueOnRefuse { get; private set; }

        /// <summary>结局出来之后继续挂着看服务端还有没有动作的秒数,缺省 0(不挂)。</summary>
        public int StaySeconds { get; private set; }

        /// <summary>
        /// 解析 <c>fight</c> 之后的那些参数。仓库根只能出现在第一个位置(不以 <c>--</c> 开头的那个);同一个选项给了多次以最后一次为准。
        /// </summary>
        public static bool TryParse(IReadOnlyList<string> args, out OnlineFightArguments parsed, out OnlineFightArgumentProblem problem)
        {
            parsed = null;
            var result = new OnlineFightArguments();
            bool haveDungeon = false;
            int count = args == null ? 0 : args.Count;
            for (int i = 0; i < count; i++)
            {
                string token = args[i];
                if (token == null)
                {
                    problem = OnlineFightArgumentProblem.UnknownArgument;
                    return false;
                }

                if (i == 0 && !token.StartsWith("--", StringComparison.Ordinal))
                {
                    result.RepoRoot = token;
                    continue;
                }

                switch (token)
                {
                    case "--dungeon":
                    {
                        if (i + 2 >= count)
                        {
                            problem = OnlineFightArgumentProblem.BadDungeon;
                            return false;
                        }
                        int dungeon;
                        int mode;
                        if (!OnlineDungeonRunner.TryParseArguments(args[i + 1], args[i + 2], out dungeon, out mode))
                        {
                            problem = OnlineFightArgumentProblem.BadDungeon;
                            return false;
                        }
                        result.DungeonId = dungeon;
                        result.Mode = mode;
                        haveDungeon = true;
                        i += 2;
                        break;
                    }
                    case "--pace-ms":
                    {
                        int value;
                        if (!TryNext(args, i, 0, MaxPaceMs, out value))
                        {
                            problem = OnlineFightArgumentProblem.BadPaceMs;
                            return false;
                        }
                        result.PaceMs = value;
                        i++;
                        break;
                    }
                    case "--clear-wait":
                    {
                        int value;
                        if (!TryNext(args, i, 1, MaxWaitSeconds, out value))
                        {
                            problem = OnlineFightArgumentProblem.BadClearWait;
                            return false;
                        }
                        result.ClearWait = TimeSpan.FromSeconds(value);
                        i++;
                        break;
                    }
                    case "--settle-wait":
                    {
                        int value;
                        if (!TryNext(args, i, 1, MaxWaitSeconds, out value))
                        {
                            problem = OnlineFightArgumentProblem.BadSettleWait;
                            return false;
                        }
                        result.SettleWait = TimeSpan.FromSeconds(value);
                        i++;
                        break;
                    }
                    case "--stay":
                    {
                        int value;
                        if (!TryNext(args, i, 0, MaxWaitSeconds, out value))
                        {
                            problem = OnlineFightArgumentProblem.BadStay;
                            return false;
                        }
                        result.StaySeconds = value;
                        i++;
                        break;
                    }
                    case "--continue-on-refuse":
                        result.ContinueOnRefuse = true;
                        break;
                    default:
                        problem = OnlineFightArgumentProblem.UnknownArgument;
                        return false;
                }
            }

            if (!haveDungeon)
            {
                problem = OnlineFightArgumentProblem.MissingDungeon;
                return false;
            }

            parsed = result;
            problem = OnlineFightArgumentProblem.None;
            return true;
        }

        // 取 args[index+1] 当 [min, max] 内的纯数字。
        private static bool TryNext(IReadOnlyList<string> args, int index, int min, int max, out int value)
        {
            value = 0;
            if (index + 1 >= args.Count)
            {
                return false;
            }
            string text = args[index + 1];
            if (string.IsNullOrEmpty(text) || text.Length > 9)
            {
                return false;
            }
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }
            return value >= min && value <= max;
        }
    }

    /// <summary>
    /// 探针 <c>fight</c> 子命令的进程退出码(对外约定):0 成功;非 0 里,装配、进城、认证、进副本各有专用码,战斗收场的非成功结局各占一个码。
    /// 认证类失败(登录失败码)一律是 <see cref="AuthFailed"/>,探针从不自动重试、也不循环重连。
    /// </summary>
    public static class OnlineFightExitCodes
    {
        /// <summary>杀光、清图、结算收齐,可回城(<see cref="OnlineFightOutcome.AllDone"/>)。</summary>
        public const int Success = 0;

        /// <summary>读本地装配(仓库根下的 local.env)失败;与 orchestrated 子命令同码。</summary>
        public const int SettingsFailed = 2;

        /// <summary>进城失败,不属于认证类(连不上、没有可用槽位、引导或游戏登录没有回应等);与 orchestrated 子命令同码。</summary>
        public const int TownEntryFailed = 3;

        /// <summary>认证类失败:网关登录被拒(带登录失败码)、游戏登录被拒、登录请求本地编码不过。立即退出,不重试。</summary>
        public const int AuthFailed = 5;

        /// <summary>进副本五步没走完。</summary>
        public const int DungeonEntryFailed = 6;

        /// <summary><see cref="OnlineFightOutcome.RefusedStop"/>。</summary>
        public const int RefusedStop = 10;

        /// <summary><see cref="OnlineFightOutcome.NoClearNotice"/>。</summary>
        public const int NoClearNotice = 11;

        /// <summary><see cref="OnlineFightOutcome.SettleIncomplete"/>。</summary>
        public const int SettleIncomplete = 12;

        /// <summary><see cref="OnlineFightOutcome.SessionClosed"/>。</summary>
        public const int SessionClosed = 13;

        /// <summary><see cref="OnlineFightOutcome.Faulted"/>。</summary>
        public const int Faulted = 14;

        /// <summary><see cref="OnlineFightOutcome.Cancelled"/>。</summary>
        public const int Cancelled = 15;

        /// <summary>命令行用法不对。</summary>
        public const int Usage = 64;

        /// <summary>探针自己没料到的异常(只打异常类型名)。</summary>
        public const int Unexpected = 70;

        /// <summary>战斗结局对应的退出码。</summary>
        public static int ForOutcome(OnlineFightOutcome outcome)
        {
            switch (outcome)
            {
                case OnlineFightOutcome.AllDone:
                    return Success;
                case OnlineFightOutcome.RefusedStop:
                    return RefusedStop;
                case OnlineFightOutcome.NoClearNotice:
                    return NoClearNotice;
                case OnlineFightOutcome.SettleIncomplete:
                    return SettleIncomplete;
                case OnlineFightOutcome.SessionClosed:
                    return SessionClosed;
                case OnlineFightOutcome.Cancelled:
                    return Cancelled;
                default:
                    return Faulted;
            }
        }

        /// <summary>进城失败的原因对应的退出码:认证类给 <see cref="AuthFailed"/>,其余给 <see cref="TownEntryFailed"/>。</summary>
        public static int ForTownFailure(OnlineFailure failure)
        {
            switch (failure)
            {
                case OnlineFailure.GatewayLoginFailed:
                case OnlineFailure.GameLoginRejected:
                case OnlineFailure.BadLoginRequest:
                    return AuthFailed;
                default:
                    return TownEntryFailed;
            }
        }
    }
}
