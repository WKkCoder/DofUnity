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

        /// <summary>
        /// 探针转行的白名单(纯函数):Net 层的日志行只放行 <c>stage=dungeon</c>、<c>stage=combat</c>、<c>stage=fight</c> 三类,
        /// 返回去掉 <c>DNF_NET </c> 前缀的 <c>stage=…</c> 文本(探针再加 <c>PROBE_FIGHT </c> 前缀与时间);其余一律返回 null,探针不打。
        /// 进副本的 <c>begin</c> 行回显命令行里的副本编号与模式,同样不放行。白名单放在这里而不是探针里,是为了让它有测试守着。
        /// </summary>
        public static string ProbeText(string netLine)
        {
            const string head = "DNF_NET stage=";
            if (netLine == null || !netLine.StartsWith(head, StringComparison.Ordinal))
            {
                return null;
            }
            string body = netLine.Substring(head.Length);
            bool allowed = body.StartsWith("dungeon ", StringComparison.Ordinal)
                || body.StartsWith("combat ", StringComparison.Ordinal)
                || body.StartsWith("fight ", StringComparison.Ordinal);
            if (!allowed || body.StartsWith("dungeon begin ", StringComparison.Ordinal))
            {
                return null;
            }
            return "stage=" + body;
        }

        /// <summary>
        /// 探针各行的统一前缀。带 <c>t=</c> 的线索行由探针自己加前缀与时间;<c>--permits-only</c> 的两行(见 <see cref="PermitsReport"/>)不带 <c>t=</c>,整行由本类拼好,探针只管打。
        /// </summary>
        public const string ProbePrefix = "PROBE_FIGHT";

        /// <summary>
        /// 副本许可表一行的字段(纯函数,<c>--permits-only</c> 的输出):<c>notices=1 malformed=0 count=3 entries=901:1,905:4,940:2</c>。
        /// <c>notices</c> 是合并进来的许可通知个数,<c>malformed</c> 是读不通、整包丢弃的个数,<c>count</c> 是合并后的条目数(副本号去重之后);
        /// <c>entries</c> 按副本号<b>升序</b>、十进制、<c>副本号:档位</c>、逗号分隔(档位是线上原值,不做换算);没有条目写 <c>-</c>。
        /// 快照为 null(进城窗口里一个许可通知也没有)时:<c>notices=0 malformed=0 count=- entries=-</c>,
        /// 用 <c>count=-</c> 与"收到了个数为 0 的表"(<c>count=0</c>)区分开。
        /// 只含副本号、档位与各计数,没有账号、口令、地址、端口、令牌,也没有载荷字节。
        /// 这是默认关闭的诊断选项的输出,只给集成者在本机终端上挑副本用;默认的 <c>fight</c> 不打副本号。
        /// </summary>
        public static string PermitsFields(DungeonPermissionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "notices=0 malformed=0 count=- entries=-";
            }

            var entries = new DungeonPermissionEntry[snapshot.Entries.Count];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = snapshot.Entries[i];
            }
            Array.Sort(entries, (a, b) => a.DungeonId.CompareTo(b.DungeonId));

            var list = new StringBuilder();
            for (int i = 0; i < entries.Length; i++)
            {
                if (i > 0)
                {
                    list.Append(',');
                }
                list.Append(Number(entries[i].DungeonId)).Append(':').Append(Number(entries[i].Tier));
            }

            return "notices=" + Number(snapshot.NoticeCount)
                + " malformed=" + Number(snapshot.MalformedNoticeCount)
                + " count=" + Number(snapshot.Count)
                + " entries=" + (entries.Length == 0 ? "-" : list.ToString());
        }

        /// <summary>
        /// <c>--permits-only</c> 成功时探针打的两整行(都不带 <c>t=</c>,先后就是打印顺序,最后一行是 <c>PROBE_FIGHT result permits=ok</c>):
        /// <c>PROBE_FIGHT stage=permits &lt;<see cref="PermitsFields"/>&gt;</c> 与 <c>PROBE_FIGHT result permits=ok</c>。
        /// </summary>
        public static string[] PermitsReport(DungeonPermissionSnapshot snapshot)
        {
            return new[]
            {
                ProbePrefix + " stage=permits " + PermitsFields(snapshot),
                ProbePrefix + " result permits=ok",
            };
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
