using Dnf.Net.ChannelDirectory;
using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 目录摘要(<see cref="OnlineDirectorySummary"/>)里每个条目的"候选序号"这个公开属性(<see cref="OnlineDirectoryEntrySummary.CandidateIndex"/>)的取值:
/// 候选条目是它在 <c>--to-index</c> 候选里的序号(0 起),<b>不是候选的条目(第一个频道、端口无效的条目、空条目、位于候选之前、之间或之后的条目)精确等于 -1</b>。
/// 输出行里非候选写成 <c>candidate=-</c>,只看行的话 -1 与别的负数分不开,所以这里直接断言属性本身。
/// 数据全是合成的(SYNTH- 前缀与合成端口);不连网、不读本机配置。
/// </summary>
public sealed class OnlineProbeKnobsGap2SummaryTests
{
    private static List<ChannelEntry> Snapshot(params (string Host, int Port)[] channels)
    {
        return new List<ChannelEntry>(DirectorySnapshot("SYNTH-GROUP", channels).Channels);
    }

    // 保护:混合目录里每个条目的 CandidateIndex 属性——非候选一律精确为 -1,候选依次为 0、1、2……(按目录顺序,中间夹着非候选也不跳号、不串号)
    // 推导(第一个频道 = GatewayHost:FirstPort):
    //   0 第一个频道(同主机同端口)      → 不是候选 → -1
    //   1 乙主机:乙端口                  → 候选 0
    //   2 网关主机:端口 0(端口无效)     → 不是候选 → -1
    //   3 丙主机:丙端口                  → 候选 1
    //   4 空条目(null)                  → 不是候选 → -1
    //   5 没写主机:第一端口(= 第一个频道)→ 不是候选 → -1
    //   6 丙主机:丁端口                  → 候选 2
    //   7 乙主机:端口 70000(超过 65535) → 不是候选 → -1
    //   8 丙主机:第一端口(主机不同)     → 候选 3
    [Fact]
    public void Non_candidate_entries_have_candidate_index_exactly_minus_one_and_candidates_count_up_from_zero()
    {
        List<ChannelEntry> entries = Snapshot(
            (GatewayHost, FirstPort),     // 0
            (HostB, PortB),               // 1
            (GatewayHost, 0),             // 2
            (HostC, PortC),               // 3
            (HostB, PortD),               // 4:下面换成 null
            (string.Empty, FirstPort),    // 5
            (HostC, PortD),               // 6
            (HostB, 70000),               // 7
            (HostC, FirstPort));          // 8
        entries[4] = null;

        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(entries, GatewayHost, FirstPort);

        Assert.Equal(new[] { -1, 0, -1, 1, -1, -1, 2, -1, 3 }, summary.Entries.Select(e => e.CandidateIndex).ToArray());
        Assert.Equal(4, summary.CandidateCount);
        Assert.Equal(Enumerable.Range(0, 9), summary.Entries.Select(e => e.Index));
    }

    // 保护:第一个频道的条目不是候选,它的 CandidateIndex 属性精确为 -1(排在最前、排在中间、排在最后都一样),而且它不改变前后候选的序号
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void The_first_channel_is_minus_one_wherever_it_sits_and_does_not_shift_the_candidate_numbering(int firstAt)
    {
        var channels = new List<(string Host, int Port)> { (HostB, PortB), (HostC, PortC) };
        channels.Insert(firstAt, (GatewayHost, FirstPort));     // 候选依次是 乙、丙;第一个频道插在 firstAt 位置
        List<ChannelEntry> entries = Snapshot(channels.ToArray());

        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(entries, GatewayHost, FirstPort);

        // 推导:三个条目里只有插入的那个是第一个频道;另两个按目录顺序是候选 0、候选 1
        int[] expected = { 0, 1 };
        var all = new List<int>(expected);
        all.Insert(firstAt, -1);
        Assert.Equal(all.ToArray(), summary.Entries.Select(e => e.CandidateIndex).ToArray());
        Assert.True(summary.Entries[firstAt].First);
        Assert.Equal(-1, summary.Entries[firstAt].CandidateIndex);
    }

    // 保护:端口无效的条目(0、负数、超过 65535)不是候选,CandidateIndex 精确为 -1;它们夹在候选之间不影响候选序号
    [Fact]
    public void Entries_with_an_invalid_port_are_minus_one_between_and_after_candidates()
    {
        List<ChannelEntry> entries = Snapshot(
            (HostB, 0),             // 0:端口无效 → -1
            (HostB, PortB),         // 1:候选 0
            (HostB, -3),            // 2:端口无效 → -1
            (HostB, 65536),         // 3:端口无效 → -1
            (HostC, PortC),         // 4:候选 1
            (HostC, 0));            // 5:端口无效(排在最后) → -1

        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(entries, GatewayHost, FirstPort);

        Assert.Equal(new[] { -1, 0, -1, -1, 1, -1 }, summary.Entries.Select(e => e.CandidateIndex).ToArray());
        Assert.Equal(new[] { false, true, false, false, true, false }, summary.Entries.Select(e => e.Usable).ToArray());
    }

    // 保护:空条目(null)占一个序号、CandidateIndex 精确为 -1,不影响前后候选的序号;整份目录全是 null 时每个都是 -1
    [Fact]
    public void Null_entries_are_minus_one_and_do_not_shift_the_candidate_numbering()
    {
        List<ChannelEntry> some = Snapshot((HostB, PortB), (HostC, PortC));
        var withNulls = new List<ChannelEntry> { null, some[0], null, null, some[1], null };

        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(withNulls, GatewayHost, FirstPort);

        Assert.Equal(new[] { -1, 0, -1, -1, 1, -1 }, summary.Entries.Select(e => e.CandidateIndex).ToArray());

        OnlineDirectorySummary onlyNulls = OnlineDirectorySummary.Build(new ChannelEntry[] { null, null, null }, GatewayHost, FirstPort);

        Assert.Equal(new[] { -1, -1, -1 }, onlyNulls.Entries.Select(e => e.CandidateIndex).ToArray());
        Assert.Equal(0, onlyNulls.CandidateCount);
    }

    // 保护:一个候选都没有的目录(只有第一个频道、只有无效端口、空目录、null 目录)里,没有任何条目的 CandidateIndex 不是 -1
    [Fact]
    public void A_directory_without_any_candidate_has_only_minus_one()
    {
        OnlineDirectorySummary onlyFirst = OnlineDirectorySummary.Build(Snapshot((GatewayHost, FirstPort)), GatewayHost, FirstPort);
        OnlineDirectorySummary onlyInvalid = OnlineDirectorySummary.Build(Snapshot((HostB, 0), (HostC, -1)), GatewayHost, FirstPort);
        OnlineDirectorySummary empty = OnlineDirectorySummary.Build(new List<ChannelEntry>(), GatewayHost, FirstPort);
        OnlineDirectorySummary nothing = OnlineDirectorySummary.Build(null, GatewayHost, FirstPort);

        Assert.Equal(new[] { -1 }, onlyFirst.Entries.Select(e => e.CandidateIndex).ToArray());
        Assert.Equal(new[] { -1, -1 }, onlyInvalid.Entries.Select(e => e.CandidateIndex).ToArray());
        Assert.Empty(empty.Entries);
        Assert.Empty(nothing.Entries);
        Assert.Equal(0, onlyFirst.CandidateCount + onlyInvalid.CandidateCount + empty.CandidateCount + nothing.CandidateCount);
    }

    // 保护:候选条目的 CandidateIndex 就是它在 Candidates 列表里的下标(0 起);按这个序号去 SelectByIndex 取回的正是它自己,
    // 非候选(-1)不能当序号去取(SelectByIndex 对负序号给 null)
    [Fact]
    public void A_candidates_index_is_its_position_in_the_candidate_list_and_minus_one_selects_nothing()
    {
        List<ChannelEntry> entries = Snapshot(
            (HostB, 0),             // 0:无效端口 → -1
            (GatewayHost, FirstPort), // 1:第一个频道 → -1
            (HostB, PortB),         // 2:候选 0
            (HostC, PortC),         // 3:候选 1
            (HostC, PortD));        // 4:候选 2

        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(entries, GatewayHost, FirstPort);
        List<ChannelEntry> candidates = OnlineSwitchProbe.Candidates(entries, GatewayHost, FirstPort);

        Assert.Equal(new[] { -1, -1, 0, 1, 2 }, summary.Entries.Select(e => e.CandidateIndex).ToArray());
        foreach (OnlineDirectoryEntrySummary candidate in summary.Entries.Where(x => x.CandidateIndex != -1))
        {
            Assert.Same(entries[candidate.Index], candidates[candidate.CandidateIndex]);
            Assert.Same(entries[candidate.Index], OnlineSwitchProbe.SelectByIndex(entries, GatewayHost, FirstPort, candidate.CandidateIndex));
        }
        foreach (OnlineDirectoryEntrySummary other in summary.Entries.Where(x => x.CandidateIndex == -1))
        {
            Assert.Null(OnlineSwitchProbe.SelectByIndex(entries, GatewayHost, FirstPort, other.CandidateIndex));
        }
    }

    // 保护:同一个条目对象在列表里出现两次时,两个位置的 CandidateIndex 各是 0 与 1;夹在中间的第一个频道仍是 -1
    [Fact]
    public void The_same_entry_listed_twice_has_two_candidate_indexes_with_minus_one_for_what_is_between()
    {
        List<ChannelEntry> pair = Snapshot((HostB, PortB), (GatewayHost, FirstPort));
        var listed = new List<ChannelEntry> { pair[0], pair[1], pair[0] };

        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(listed, GatewayHost, FirstPort);

        Assert.Equal(new[] { 0, -1, 1 }, summary.Entries.Select(e => e.CandidateIndex).ToArray());
    }

    // 保护:公开属性与输出行同源——CandidateIndex 为 -1 的行写 "candidate=-",否则写数字;两者逐条目一一对应
    [Fact]
    public void The_property_and_the_line_agree_for_every_entry()
    {
        List<ChannelEntry> entries = Snapshot((GatewayHost, FirstPort), (HostB, PortB), (HostB, 0), (HostC, PortC));
        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(entries, GatewayHost, FirstPort);

        // 期望(手算):第一个频道 -1;候选 0;无效端口 -1;候选 1
        int[] expected = { -1, 0, -1, 1 };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], summary.Entries[i].CandidateIndex);
            string token = expected[i] == -1 ? "candidate=- " : "candidate=" + expected[i] + " ";
            Assert.Contains(token, summary.Entries[i].ToLine());
        }
    }
}
