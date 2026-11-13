using System;
using System.Globalization;
using System.Text;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 无头战斗驱动的日志行拼法(纯函数,调用方只管打)。每一行都以 <see cref="Prefix"/> 开头,只含阶段名、枚举名、本地序号与各种计数,
    /// <b>不含任何载荷内容</b>(包括服务端怪编号),也不含异常消息;数字一律按固定文化输出。
    /// 控制台探针的 <c>fight</c> 子命令把这些行与战斗跑器的行(<see cref="OnlineCombatLog"/>)原样转成 <c>PROBE_FIGHT …</c> 行再打;
    /// 末尾的汇总字段(<see cref="ResultFields"/>)就是探针最终那行 <c>PROBE_FIGHT result …</c> 的内容,字段名与顺序是对外约定,不要随手改。
    /// </summary>
    public static class OnlineFightLog
    {
        /// <summary>驱动各行的统一前缀。</summary>
        public const string Prefix = "DNF_NET stage=fight";

        /// <summary>驱动开始:怪数、间隔与两段等待(毫秒)、被拒后是否继续。</summary>
        internal static string Begin(int monsters, int paceMs, TimeSpan clearWait, TimeSpan settleWait, bool continueOnRefuse)
        {
            return Prefix + " begin monsters=" + Number(monsters)
                + " paceMs=" + Number(paceMs)
                + " clearWaitMs=" + Number((long)clearWait.TotalMilliseconds)
                + " settleWaitMs=" + Number((long)settleWait.TotalMilliseconds)
                + " continueOnRefuse=" + Flag(continueOnRefuse);
        }

        /// <summary>进入一个阶段:kills(排击杀)、clearWait(等清图通知)、settleWait(等结算通知);带此刻已排的击杀数。</summary>
        internal static string Phase(string name, int posted)
        {
            return Prefix + " phase=" + name + " posted=" + Number(posted);
        }

        /// <summary>第一次发现有击杀被拒:是哪一只(本地序号)、驱动是停下还是继续。</summary>
        internal static string Refused(int local, bool stop)
        {
            return Prefix + " refused local=" + Number(local) + " action=" + (stop ? "stop" : "continue");
        }

        /// <summary>收场的一行:汇总字段加驱动与跑器的补充计数。</summary>
        internal static string End(OnlineFightSummary r)
        {
            var sb = new StringBuilder(Prefix);
            sb.Append(" end ").Append(ResultFields(r));
            sb.Append(" posted=").Append(Number(r.Posted));
            sb.Append(" pending=").Append(Number(r.Pending));
            sb.Append(" heartbeats=").Append(Number(r.HeartbeatsAnswered));
            sb.Append(" runner=").Append(r.RunnerOutcome.HasValue ? r.RunnerOutcome.Value.ToString() : "-");
            if (r.FaultType != null)
            {
                sb.Append(" exception=").Append(r.FaultType);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 汇总字段(对外约定,顺序固定):<c>outcome=AllDone monsters=7 sent=7 confirmed=7 refused=0 blocked=0 clearNotice=1 settlement=complete returnReady=1</c>。
        /// 结局是枚举名;<c>clearNotice</c> 与 <c>returnReady</c> 是 0 或 1;<c>settlement</c> 是小写的 none、partial、complete。
        /// </summary>
        public static string ResultFields(OnlineFightSummary r)
        {
            return "outcome=" + r.Outcome
                + " monsters=" + Number(r.Monsters)
                + " sent=" + Number(r.Sent)
                + " confirmed=" + Number(r.Confirmed)
                + " refused=" + Number(r.Refused)
                + " blocked=" + Number(r.Blocked)
                + " clearNotice=" + Flag(r.ClearNoticeSeen)
                + " settlement=" + r.Settlement.ToString().ToLowerInvariant()
                + " returnReady=" + Flag(r.ReturnReady);
        }

        /// <summary>
        /// 头号问题(击杀载荷在服务端那里的待遇)的判读字段:<c>kill=accepted pending=0 firstRefused=-</c>。
        /// <c>kill</c> 是小写的 none、accepted、partial、refused、silent(含义见 <see cref="OnlineFightKillVerdict"/>);<c>pending</c> 是收场时仍在途的击杀数;
        /// <c>firstRefused</c> 是第一只被拒的怪的本地序号,没有写 <c>-</c>。
        /// </summary>
        public static string VerdictFields(OnlineFightSummary r)
        {
            return "kill=" + r.KillVerdict.ToString().ToLowerInvariant()
                + " pending=" + Number(r.Pending)
                + " firstRefused=" + (r.FirstRefusedLocal >= 0 ? Number(r.FirstRefusedLocal) : "-");
        }

        private static string Flag(bool value)
        {
            return value ? "1" : "0";
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Number(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
