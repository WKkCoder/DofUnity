using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dnf.Net.ChannelDirectory;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 频道目录里一个条目的脱敏摘要(<c>switch --list</c> 的一行):序号、端口是否有效、是否就是"第一个频道"、在 <c>--to-index</c> 候选里的序号、
    /// 会写进游戏登录请求的服务器组、槽位规则里的频道种类。全是小整数与 0/1,<b>不含</b>主机、端口、频道名与组标签文字。
    /// </summary>
    public sealed class OnlineDirectoryEntrySummary
    {
        internal OnlineDirectoryEntrySummary(int index, bool usable, bool first, int candidateIndex, ushort serverGroup, int kind)
        {
            Index = index;
            Usable = usable;
            First = first;
            CandidateIndex = candidateIndex;
            ServerGroup = serverGroup;
            Kind = kind;
        }

        /// <summary>在目录里的序号(按目录顺序,从 0 起)。</summary>
        public int Index { get; }

        /// <summary>端口有效(<see cref="ChannelEntryFlags.HasValidPort"/>)。</summary>
        public bool Usable { get; }

        /// <summary>它就是"第一个频道"(网关给的主机加设置里的频道端口;与 <see cref="OnlineSwitchProbe.Candidates"/> 排除用的同一判定)。</summary>
        public bool First { get; }

        /// <summary>在 <c>--to-index</c> 候选里的序号 K(从 0 起,与 <see cref="OnlineSwitchProbe.SelectByIndex"/> 同一份列表);不是候选为 -1。</summary>
        public int CandidateIndex { get; }

        /// <summary>这个条目会被写进游戏登录请求的服务器组(设置里的服务器组为 0 时目录解出的值;解不出为 0)。</summary>
        public ushort ServerGroup { get; }

        /// <summary>槽位规则里的频道种类编号(<see cref="ChannelServerSlotRule.ChannelKind"/>);没有槽位规则为 <see cref="OnlineDirectorySummary.NoKind"/>。</summary>
        public int Kind { get; }

        /// <summary>
        /// 输出行:<c>PROBE_SWITCH dir entry=&lt;i&gt; usable=&lt;0|1&gt; first=&lt;0|1&gt; candidate=&lt;K 或 -&gt; group=&lt;0..17&gt; kind=&lt;整数&gt;</c>。
        /// </summary>
        public string ToLine()
        {
            var sb = new StringBuilder(OnlineSwitchProbe.LinePrefix);
            sb.Append(" dir entry=").Append(Num(Index));
            sb.Append(" usable=").Append(Flag(Usable));
            sb.Append(" first=").Append(Flag(First));
            sb.Append(" candidate=").Append(CandidateIndex >= 0 ? Num(CandidateIndex) : "-");
            sb.Append(" group=").Append(Num(ServerGroup));
            sb.Append(" kind=").Append(Num(Kind));
            return sb.ToString();
        }

        internal static string Num(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        internal static string Flag(bool value)
        {
            return value ? "1" : "0";
        }
    }

    /// <summary>
    /// 频道目录的脱敏摘要(<c>switch --list</c>):逐条目一行加一行汇总。纯函数,不连网;摘要里没有主机、端口、频道名、组标签文字。
    /// <para>
    /// 保证:摘要里 <c>candidate=K</c> 的条目,就是 <see cref="OnlineSwitchProbe.SelectByIndex"/> 取 <c>K</c> 时选中的那个条目
    /// (候选序号是对 <see cref="OnlineSwitchProbe.Candidates"/> 返回的同一份列表按目录顺序逐个对出来的,不另算一遍)。
    /// <c>first</c> 与候选排除用同一个判定(<see cref="OnlineSwitchProbe.IsFirstChannel"/>)。
    /// </para>
    /// <para>
    /// 服务器组的规则是对"单个条目"的纯函数 <see cref="ServerGroupOf"/>,与会话里按端口解服务器组(<c>OnlineClientSession.ResolveDirectoryServerGroup</c>)
    /// 对该条目用的是同一条规则:组标签经 <c>ChannelScriptData.TryMapServerGroupId</c> 解得出、并且落在 1 到 <see cref="LoginRequest.MaxServerGroup"/> 之间才算,否则 0。
    /// 注意:会话是按"端口"在整份目录里找条目的(同一个端口出现在多个条目时先认主机相同的、再取第一个解得出的);
    /// 端口互不相同时与这里逐条目的值一致,端口重复时这里给的是该条目自己的组,不一定是会话连那个端口时实际取到的。
    /// </para>
    /// </summary>
    public sealed class OnlineDirectorySummary
    {
        /// <summary>没有槽位规则时 <c>kind=</c> 给的值。</summary>
        public const int NoKind = -1;

        private OnlineDirectorySummary(IReadOnlyList<OnlineDirectoryEntrySummary> entries, int usableCount, int candidateCount, bool firstInDirectory)
        {
            Entries = entries;
            UsableCount = usableCount;
            CandidateCount = candidateCount;
            FirstInDirectory = firstInDirectory;
        }

        /// <summary>每个目录条目一条,按目录顺序。</summary>
        public IReadOnlyList<OnlineDirectoryEntrySummary> Entries { get; }

        /// <summary>端口有效的条目数。</summary>
        public int UsableCount { get; }

        /// <summary><c>--to-index</c> 候选的个数。</summary>
        public int CandidateCount { get; }

        /// <summary>目录里是否有条目就是"第一个频道"(有的话它被排除在候选之外)。</summary>
        public bool FirstInDirectory { get; }

        /// <summary>
        /// 做摘要。<paramref name="firstHost"/> 是网关给的游戏服主机(list 模式没有连接,所以只用它),<paramref name="firstPort"/> 是设置里的频道端口。
        /// <paramref name="channels"/> 为 null 当作空目录;null 条目照样占一个序号(端口无效、不是候选、组 0、种类 -1)。
        /// </summary>
        public static OnlineDirectorySummary Build(IReadOnlyList<ChannelEntry> channels, string firstHost, int firstPort)
        {
            int count = channels == null ? 0 : channels.Count;
            List<ChannelEntry> candidates = OnlineSwitchProbe.Candidates(channels, firstHost, firstPort);
            var entries = new List<OnlineDirectoryEntrySummary>(count);
            int usableCount = 0;
            bool firstInDirectory = false;
            int nextCandidate = 0;
            for (int i = 0; i < count; i++)
            {
                ChannelEntry entry = channels[i];
                bool usable = entry != null && (entry.Flags & ChannelEntryFlags.HasValidPort) != 0;
                bool first = OnlineSwitchProbe.IsFirstChannel(entry, firstHost, firstPort);

                // 候选按目录顺序排,所以按顺序逐个对:当前这个条目就是下一个候选时,它的序号就是 nextCandidate
                int candidateIndex = -1;
                if (nextCandidate < candidates.Count && ReferenceEquals(candidates[nextCandidate], entry))
                {
                    candidateIndex = nextCandidate;
                    nextCandidate++;
                }

                if (usable)
                {
                    usableCount++;
                }
                if (first)
                {
                    firstInDirectory = true;
                }
                entries.Add(new OnlineDirectoryEntrySummary(i, usable, first, candidateIndex, ServerGroupOf(entry), KindOf(entry)));
            }
            return new OnlineDirectorySummary(entries, usableCount, candidates.Count, firstInDirectory);
        }

        /// <summary>这个条目会被写进游戏登录请求的服务器组(设置里的服务器组为 0 时):组标签解得出且在 1 到 17 之间才算,否则 0。</summary>
        public static ushort ServerGroupOf(ChannelEntry entry)
        {
            if (entry == null || !ChannelScriptData.TryMapServerGroupId(entry.GroupTag, out int groupId))
            {
                return 0;
            }
            return ServerGroupFromId(groupId);
        }

        /// <summary>解出的组编号落在 1 到 <see cref="LoginRequest.MaxServerGroup"/> 之间才算,否则 0(会话里那条规则的范围判断)。</summary>
        public static ushort ServerGroupFromId(int groupId)
        {
            if (groupId < 1 || groupId > LoginRequest.MaxServerGroup)
            {
                return 0;
            }
            return (ushort)groupId;
        }

        /// <summary>条目槽位规则里的频道种类编号;没有槽位规则为 <see cref="NoKind"/>。</summary>
        public static int KindOf(ChannelEntry entry)
        {
            return entry != null && entry.SlotRule != null ? entry.SlotRule.ChannelKind : NoKind;
        }

        /// <summary>汇总行:<c>PROBE_SWITCH dir-summary entries=&lt;N&gt; usable=&lt;U&gt; candidates=&lt;C&gt; firstInDirectory=&lt;0|1&gt;</c>。</summary>
        public string SummaryLine()
        {
            var sb = new StringBuilder(OnlineSwitchProbe.LinePrefix);
            sb.Append(" dir-summary entries=").Append(OnlineDirectoryEntrySummary.Num(Entries.Count));
            sb.Append(" usable=").Append(OnlineDirectoryEntrySummary.Num(UsableCount));
            sb.Append(" candidates=").Append(OnlineDirectoryEntrySummary.Num(CandidateCount));
            sb.Append(" firstInDirectory=").Append(OnlineDirectoryEntrySummary.Flag(FirstInDirectory));
            return sb.ToString();
        }

        /// <summary>全部输出行:逐条目一行(按目录顺序),最后一行汇总。</summary>
        public IReadOnlyList<string> ToLines()
        {
            var lines = new List<string>(Entries.Count + 1);
            for (int i = 0; i < Entries.Count; i++)
            {
                lines.Add(Entries[i].ToLine());
            }
            lines.Add(SummaryLine());
            return lines;
        }
    }
}
