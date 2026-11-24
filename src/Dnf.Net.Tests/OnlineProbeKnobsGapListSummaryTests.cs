using System.Globalization;
using Dnf.Net.ChannelDirectory;
using Dnf.Net.Session;
using static Dnf.Net.Tests.OnlineSwitchTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 目录摘要(<see cref="OnlineDirectorySummary"/>)的内容:一份混合的合成目录逐字段断言——端口无效、与第一个频道同主机同端口、
/// 同端口不同主机、没写主机/只有空白的主机、主机大小写不同、组标签解不出、组标签大小写与空白、有槽位规则与没有槽位规则、规则靠"同频道号回退"命中等——
/// 并证明每个 <c>candidate=K</c> 的条目就是 <see cref="OnlineSwitchProbe.SelectByIndex"/> 取 K 选中的那个、<c>first</c> 与候选排除同一判定、
/// 服务器组的单条目纯函数在边界上的取值(1..17 内外)、行格式的每个字段。数据全是合成的(SYNTH- 前缀与合成端口)。
/// </summary>
public sealed class OnlineProbeKnobsGapListSummaryTests
{
    private const int PortE = 47314;
    private const int PortF = 47315;
    private const int PortG = 47316;

    private static ChannelWireEntry W(string name, string host, int port)
    {
        return new ChannelWireEntry(name, 200, 10, host, port);
    }

    /// <summary>
    /// 脚本里的 [server] 槽位规则(合成):组 1 的频道号 1 → 种类 0、频道号 2 → 种类 3;组 3 的频道号 1 → 种类 11;组 14 的频道号 1 → 种类 16。
    /// 种类故意取互不相同的值,这样每个条目的 kind 只能来自它自己命中的那条规则。
    /// </summary>
    private static ChannelScriptData SlotScript()
    {
        var slots = new[]
        {
            new ChannelServerSlotRule(1, 1, 0, string.Empty, "SYNTH-TITLE-A", 0, "[none]"),
            new ChannelServerSlotRule(1, 2, 0, string.Empty, "SYNTH-TITLE-B", 3, "[none]"),
            new ChannelServerSlotRule(3, 1, 0, string.Empty, "SYNTH-TITLE-C", 11, "[none]"),
            new ChannelServerSlotRule(14, 1, 0, string.Empty, "SYNTH-TITLE-D", 16, "[none]"),
        };
        return new ChannelScriptData(string.Empty, 95f, null, slots);
    }

    /// <summary>
    /// 混合目录(13 个条目,目录顺序即下标;第一个频道 = 网关主机 + <see cref="FirstPort"/>)。每个条目的期望见 <see cref="MixedExpectedLines"/> 旁的推导。
    /// </summary>
    private static ChannelDirectorySnapshot MixedDirectory()
    {
        var groups = new[]
        {
            new ChannelWireGroup("cain", new[]
            {
                W("SYNTH-CH-1", GatewayHost, FirstPort),      // 0
                W("SYNTH-CH-2", GatewayHost, 0),              // 1
                W("SYNTH-CH-9", HostC, FirstPort),            // 2
            }),
            new ChannelWireGroup("siroco", new[]
            {
                W("SYNTH-CH-1", HostB, PortB),                // 3
                W("SYNTH-CH-1", string.Empty, PortC),         // 4
                W("SYNTH-CH-1", string.Empty, FirstPort),     // 5
            }),
            new ChannelWireGroup("starter", new[]
            {
                W("SYNTH-CH-1", "   ", PortD),                // 6
                W("SYNTH-CH-1", HostB, 70000),                // 7
            }),
            new ChannelWireGroup("SYNTH-GROUP", new[]
            {
                W("SYNTH-NAMED", HostC, PortE),               // 8
                W("SYNTH-CH-1", HostC, PortF),                // 9
            }),
            new ChannelWireGroup(string.Empty, new[]
            {
                W("SYNTH-CH-2", HostB, -5),                   // 10
            }),
            new ChannelWireGroup(" Cain ", new[]
            {
                W("SYNTH-CH-2", HostC, PortG),                // 11
            }),
            new ChannelWireGroup("cain", new[]
            {
                W("SYNTH-CH-1", GatewayHost.ToLowerInvariant(), FirstPort),   // 12
            }),
        };
        return ChannelDirectorySnapshot.Build("0", true, SlotScript(), groups);
    }

    // 逐条目推导(第一个频道 = GatewayHost:FirstPort;槽位规则:组1:1→0、组1:2→3、组3:1→11、组14:1→16;解不出组或该组没有此频道号时才按"同频道号回退"):
    //  0 cain  CH-1 网关主机:第一端口  → 可用,就是第一个频道;组 1;规则(1,1)→种类 0
    //  1 cain  CH-2 网关主机:端口 0   → 不可用(端口无效);组 1;规则(1,2)→种类 3
    //  2 cain  CH-9 丙主机:第一端口   → 可用;主机不同所以不是第一个频道,候选 0;组 1;组 1 有规则但没有频道号 9 → 无规则,种类 -1
    //  3 siroco CH-1 乙主机:乙端口    → 可用,候选 1;组 3;规则(3,1)→种类 11
    //  4 siroco CH-1 没写主机:丙端口  → 可用(没写主机视为网关主机,但端口不同)候选 2;组 3;种类 11
    //  5 siroco CH-1 没写主机:第一端口 → 可用,没写主机视为网关主机,所以是第一个频道;组 3;种类 11
    //  6 starter CH-1 空白主机:丁端口 → 可用,候选 3;组 14;规则(14,1)→种类 16
    //  7 starter CH-1 乙主机:端口 70000 → 不可用(超过 65535);组 14;种类 16
    //  8 SYNTH-GROUP SYNTH-NAMED 丙主机:戊端口 → 可用,候选 4;组标签解不出→组 0;名字里没有数字→无规则,种类 -1
    //  9 SYNTH-GROUP CH-1 丙主机:己端口 → 可用,候选 5;组 0;组标签解不出,规则靠"同频道号回退"取到第一条频道号 1 的规则(1,1)→种类 0
    // 10 (空标签) CH-2 乙主机:端口 -5 → 不可用;组 0;回退取第一条频道号 2 的规则(1,2)→种类 3
    // 11 " Cain " CH-2 丙主机:庚端口 → 可用,候选 6;标签大小写与空白不影响映射→组 1;规则(1,2)→种类 3
    // 12 cain CH-1 小写的网关主机:第一端口 → 主机比较不分大小写,所以是第一个频道;组 1;种类 0
    // 汇总:13 条;可用 = 0、2、3、4、5、6、8、9、11、12 共 10;候选 = 2、3、4、6、8、9、11 共 7;目录里有第一个频道。
    private static readonly string[] MixedExpectedLines =
    {
        "PROBE_SWITCH dir entry=0 usable=1 first=1 candidate=- group=1 kind=0",
        "PROBE_SWITCH dir entry=1 usable=0 first=0 candidate=- group=1 kind=3",
        "PROBE_SWITCH dir entry=2 usable=1 first=0 candidate=0 group=1 kind=-1",
        "PROBE_SWITCH dir entry=3 usable=1 first=0 candidate=1 group=3 kind=11",
        "PROBE_SWITCH dir entry=4 usable=1 first=0 candidate=2 group=3 kind=11",
        "PROBE_SWITCH dir entry=5 usable=1 first=1 candidate=- group=3 kind=11",
        "PROBE_SWITCH dir entry=6 usable=1 first=0 candidate=3 group=14 kind=16",
        "PROBE_SWITCH dir entry=7 usable=0 first=0 candidate=- group=14 kind=16",
        "PROBE_SWITCH dir entry=8 usable=1 first=0 candidate=4 group=0 kind=-1",
        "PROBE_SWITCH dir entry=9 usable=1 first=0 candidate=5 group=0 kind=0",
        "PROBE_SWITCH dir entry=10 usable=0 first=0 candidate=- group=0 kind=3",
        "PROBE_SWITCH dir entry=11 usable=1 first=0 candidate=6 group=1 kind=3",
        "PROBE_SWITCH dir entry=12 usable=1 first=1 candidate=- group=1 kind=0",
        "PROBE_SWITCH dir-summary entries=13 usable=10 candidates=7 firstInDirectory=1",
    };

    /// <summary>目录里下标 → 在 --to-index 候选里的序号(手写:候选 0..6 依次是目录下标 2、3、4、6、8、9、11)。</summary>
    private static readonly int[] MixedCandidateEntries = { 2, 3, 4, 6, 8, 9, 11 };

    private static OnlineDirectorySummary Mixed(out ChannelDirectorySnapshot snapshot)
    {
        snapshot = MixedDirectory();
        return OnlineDirectorySummary.Build(snapshot.Channels, GatewayHost, FirstPort);
    }

    // ======== 一、混合目录逐行 ========

    // 保护:混合目录的每个条目的每个字段(逐行字面量,推导见上面的注释)
    [Fact]
    public void The_mixed_directory_gives_exactly_these_lines()
    {
        OnlineDirectorySummary summary = Mixed(out _);

        Assert.Equal(MixedExpectedLines, summary.ToLines());
    }

    // 保护:汇总数字与逐条目属性一致(条目数、可用数、候选数、目录里有无第一个频道)
    [Fact]
    public void The_summary_numbers_match_the_entries()
    {
        OnlineDirectorySummary summary = Mixed(out ChannelDirectorySnapshot snapshot);

        Assert.Equal(13, summary.Entries.Count);
        Assert.Equal(snapshot.Channels.Count, summary.Entries.Count);
        Assert.Equal(10, summary.UsableCount);
        Assert.Equal(7, summary.CandidateCount);
        Assert.True(summary.FirstInDirectory);
        Assert.Equal(summary.Entries.Count(e => e.Usable), summary.UsableCount);
        Assert.Equal(summary.Entries.Count(e => e.CandidateIndex >= 0), summary.CandidateCount);
        Assert.Equal(Enumerable.Range(0, 13), summary.Entries.Select(e => e.Index));
    }

    // 保护:摘要的行里没有主机、端口、频道名、组标签文字(混合目录里这些都是 SYNTH- 前缀或合成端口)
    [Fact]
    public void The_summary_lines_carry_no_host_port_name_or_tag_text()
    {
        OnlineDirectorySummary summary = Mixed(out _);
        string all = string.Join("\n", summary.ToLines());

        Assert.DoesNotContain("SYNTH", all, StringComparison.OrdinalIgnoreCase);
        foreach (string port in PortTexts.Concat(new[] { PortE.ToString(), PortF.ToString(), PortG.ToString(), "70000" }))
        {
            Assert.DoesNotContain(port, all);
        }
        Assert.DoesNotContain("cain", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("siroco", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("starter", all, StringComparison.OrdinalIgnoreCase);
    }

    // ======== 二、候选序号与 --to-index 一致 ========

    // 保护:每个 candidate=K 的条目,就是 SelectByIndex 取 K 选中的那个条目(同一个对象);K 到候选个数时选不出(null)
    [Fact]
    public void Each_candidate_index_names_the_entry_that_select_by_index_picks()
    {
        OnlineDirectorySummary summary = Mixed(out ChannelDirectorySnapshot snapshot);

        for (int k = 0; k < MixedCandidateEntries.Length; k++)
        {
            ChannelEntry picked = OnlineSwitchProbe.SelectByIndex(snapshot.Channels, GatewayHost, FirstPort, k);
            Assert.Same(snapshot.Channels[MixedCandidateEntries[k]], picked);          // 手写的期望下标
            OnlineDirectoryEntrySummary line = Assert.Single(summary.Entries, e => e.CandidateIndex == k);
            Assert.Same(picked, snapshot.Channels[line.Index]);                        // 摘要里 candidate=k 的就是它
        }
        Assert.Null(OnlineSwitchProbe.SelectByIndex(snapshot.Channels, GatewayHost, FirstPort, MixedCandidateEntries.Length));
    }

    // 保护:候选个数与 Candidates 返回的列表一致;"是候选"恰好等于"可用且不是第一个频道"(first 与候选排除同一判定)
    [Fact]
    public void First_and_usable_decide_candidacy_exactly_like_the_candidates_list()
    {
        OnlineDirectorySummary summary = Mixed(out ChannelDirectorySnapshot snapshot);
        List<ChannelEntry> candidates = OnlineSwitchProbe.Candidates(snapshot.Channels, GatewayHost, FirstPort);

        Assert.Equal(candidates.Count, summary.CandidateCount);
        foreach (OnlineDirectoryEntrySummary e in summary.Entries)
        {
            ChannelEntry entry = snapshot.Channels[e.Index];
            Assert.Equal(e.Usable && !e.First, e.CandidateIndex >= 0);
            Assert.Equal(candidates.Contains(entry), e.CandidateIndex >= 0);
            Assert.Equal(OnlineSwitchProbe.IsFirstChannel(entry, GatewayHost, FirstPort), e.First);
        }
    }

    // 保护:IsFirstChannel 的判定:端口等于设置端口且主机相同(没写主机/空白 = 网关主机;不分大小写;IP 字面量按地址比,两侧去空白);null 条目不是
    [Fact]
    public void Is_first_channel_needs_the_same_port_and_the_same_host()
    {
        string gatewayIp = GatewayTestSupport.Dots(198, 51, 100, 7);
        string otherIp = GatewayTestSupport.Dots(198, 51, 100, 8);
        ChannelDirectorySnapshot snapshot = ChannelDirectorySnapshot.Build("0", true, null, new[]
        {
            new ChannelWireGroup("g", new[]
            {
                W("SYNTH-A", gatewayIp, FirstPort),                    // 0:同主机同端口
                W("SYNTH-B", " " + gatewayIp + " ", FirstPort),        // 1:IP 字面量带空白,仍是同一地址
                W("SYNTH-C", otherIp, FirstPort),                      // 2:端口相同,主机不同
                W("SYNTH-D", gatewayIp, PortB),                        // 3:主机相同,端口不同
                W("SYNTH-E", string.Empty, FirstPort),                 // 4:没写主机 = 网关主机
                W("SYNTH-F", string.Empty, PortB),                     // 5:没写主机,端口不同
                W("SYNTH-G", GatewayHost, FirstPort),                  // 6:主机名与 IP 字面量不是同一个
            }),
        });
        var expected = new[] { true, true, false, false, true, false, false };

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], OnlineSwitchProbe.IsFirstChannel(snapshot.Channels[i], gatewayIp, FirstPort));
        }
        Assert.False(OnlineSwitchProbe.IsFirstChannel(null, gatewayIp, FirstPort));
    }

    // 保护:网关主机缺失(null/空白)时,只有"没写主机"的条目才算与它同主机;写了主机的条目不是第一个频道
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_gateway_host_only_entries_with_no_host_can_be_the_first_channel(string firstHost)
    {
        ChannelDirectorySnapshot snapshot = DirectorySnapshot("SYNTH-GROUP", (GatewayHost, FirstPort), (string.Empty, FirstPort), ("  ", FirstPort));

        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(snapshot.Channels, firstHost, FirstPort);

        Assert.Equal(new[]
        {
            "PROBE_SWITCH dir entry=0 usable=1 first=0 candidate=0 group=0 kind=-1",
            "PROBE_SWITCH dir entry=1 usable=1 first=1 candidate=- group=0 kind=-1",
            "PROBE_SWITCH dir entry=2 usable=1 first=1 candidate=- group=0 kind=-1",
            "PROBE_SWITCH dir-summary entries=3 usable=3 candidates=1 firstInDirectory=1",
        }, summary.ToLines());
    }

    // ======== 三、边角输入 ========

    // 保护:目录里没有第一个频道时 firstInDirectory=0、全部可用条目都是候选
    [Fact]
    public void A_directory_without_the_first_channel_reports_zero_for_first_in_directory()
    {
        ChannelDirectorySnapshot snapshot = DirectorySnapshot("SYNTH-GROUP", (HostB, PortB), (HostC, PortC));

        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(snapshot.Channels, GatewayHost, FirstPort);

        Assert.Equal(new[]
        {
            "PROBE_SWITCH dir entry=0 usable=1 first=0 candidate=0 group=0 kind=-1",
            "PROBE_SWITCH dir entry=1 usable=1 first=0 candidate=1 group=0 kind=-1",
            "PROBE_SWITCH dir-summary entries=2 usable=2 candidates=2 firstInDirectory=0",
        }, summary.ToLines());
        Assert.False(summary.FirstInDirectory);
    }

    // 保护:只有第一个频道的目录:firstInDirectory=1、候选 0 个
    [Fact]
    public void A_directory_with_only_the_first_channel_has_no_candidates()
    {
        ChannelDirectorySnapshot snapshot = DirectorySnapshot("SYNTH-GROUP", (GatewayHost, FirstPort));

        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(snapshot.Channels, GatewayHost, FirstPort);

        Assert.Equal(new[]
        {
            "PROBE_SWITCH dir entry=0 usable=1 first=1 candidate=- group=0 kind=-1",
            "PROBE_SWITCH dir-summary entries=1 usable=1 candidates=0 firstInDirectory=1",
        }, summary.ToLines());
    }

    // 保护:first 是纯判定、不要求可用:设置里的频道端口为 0 时,端口 0 的条目 first=1 但 usable=0,仍不是候选;
    // 候选要求"可用且不是第一个频道";firstInDirectory 只看 first,不看可用
    [Fact]
    public void An_unusable_entry_can_still_be_the_first_channel_but_is_never_a_candidate()
    {
        ChannelDirectorySnapshot snapshot = DirectorySnapshot("SYNTH-GROUP", (GatewayHost, 0), (HostB, PortB));

        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(snapshot.Channels, GatewayHost, 0);

        Assert.Equal(new[]
        {
            "PROBE_SWITCH dir entry=0 usable=0 first=1 candidate=- group=0 kind=-1",
            "PROBE_SWITCH dir entry=1 usable=1 first=0 candidate=0 group=0 kind=-1",
            "PROBE_SWITCH dir-summary entries=2 usable=1 candidates=1 firstInDirectory=1",
        }, summary.ToLines());
    }

    // 保护:null 目录当空目录;目录里的 null 条目照样占一个序号(不可用、不是第一个、不是候选、组 0、种类 -1),不抛异常,也不打乱后面条目的候选序号
    [Fact]
    public void A_null_directory_is_empty_and_null_entries_take_an_index_but_nothing_else()
    {
        OnlineDirectorySummary empty = OnlineDirectorySummary.Build(null, GatewayHost, FirstPort);
        ChannelDirectorySnapshot snapshot = DirectorySnapshot("SYNTH-GROUP", (HostB, PortB), (HostC, PortC));
        var withNulls = new List<ChannelEntry> { null, snapshot.Channels[0], null, snapshot.Channels[1], null };

        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(withNulls, GatewayHost, FirstPort);

        Assert.Equal(new[] { "PROBE_SWITCH dir-summary entries=0 usable=0 candidates=0 firstInDirectory=0" }, empty.ToLines());
        Assert.Equal(new[]
        {
            "PROBE_SWITCH dir entry=0 usable=0 first=0 candidate=- group=0 kind=-1",
            "PROBE_SWITCH dir entry=1 usable=1 first=0 candidate=0 group=0 kind=-1",
            "PROBE_SWITCH dir entry=2 usable=0 first=0 candidate=- group=0 kind=-1",
            "PROBE_SWITCH dir entry=3 usable=1 first=0 candidate=1 group=0 kind=-1",
            "PROBE_SWITCH dir entry=4 usable=0 first=0 candidate=- group=0 kind=-1",
            "PROBE_SWITCH dir-summary entries=5 usable=2 candidates=2 firstInDirectory=0",
        }, summary.ToLines());
        Assert.Same(snapshot.Channels[1], OnlineSwitchProbe.SelectByIndex(withNulls, GatewayHost, FirstPort, 1));
    }

    // 保护:同一个条目对象在列表里出现两次时,候选序号按候选列表的顺序各给一个(0 与 1),与 SelectByIndex 一致
    [Fact]
    public void The_same_entry_listed_twice_gets_two_candidate_indexes_in_list_order()
    {
        ChannelDirectorySnapshot snapshot = DirectorySnapshot("SYNTH-GROUP", (HostB, PortB));
        ChannelEntry entry = snapshot.Channels[0];
        var twice = new List<ChannelEntry> { entry, entry };

        OnlineDirectorySummary summary = OnlineDirectorySummary.Build(twice, GatewayHost, FirstPort);

        Assert.Equal(new[]
        {
            "PROBE_SWITCH dir entry=0 usable=1 first=0 candidate=0 group=0 kind=-1",
            "PROBE_SWITCH dir entry=1 usable=1 first=0 candidate=1 group=0 kind=-1",
            "PROBE_SWITCH dir-summary entries=2 usable=2 candidates=2 firstInDirectory=0",
        }, summary.ToLines());
        Assert.Same(entry, OnlineSwitchProbe.SelectByIndex(twice, GatewayHost, FirstPort, 1));
    }

    // ======== 四、服务器组的单条目纯函数与边界 ========

    private static ChannelEntry EntryWithTag(string tag)
    {
        return DirectorySnapshot(tag, (GatewayHost, FirstPort)).Channels[0];
    }

    // 保护:组标签 → 服务器组:解得出的(大小写、首尾空白、server 后缀噪声不影响)给 1..14;解不出的(空、空白、合成名)给 0
    [Theory]
    [InlineData("cain", 1)]
    [InlineData("Cain", 1)]
    [InlineData(" siroco ", 3)]
    [InlineData("SIROCO", 3)]
    [InlineData("prey", 4)]
    [InlineData("khazan", 13)]
    [InlineData("starter", 14)]
    [InlineData("start", 14)]
    [InlineData("first", 14)]
    [InlineData("Cain Server", 1)]
    [InlineData("cain-server", 1)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("SYNTH-GROUP", 0)]
    [InlineData("SYNTH-UNKNOWN-TAG", 0)]
    public void The_server_group_of_an_entry_comes_from_its_group_tag(string tag, int expected)
    {
        Assert.Equal((ushort)expected, OnlineDirectorySummary.ServerGroupOf(EntryWithTag(tag)));
    }

    // 保护:没有条目(null)时服务器组为 0
    [Fact]
    public void A_missing_entry_has_server_group_zero()
    {
        Assert.Equal((ushort)0, OnlineDirectorySummary.ServerGroupOf(null));
    }

    // 保护:解出的组编号落在 1..LoginRequest.MaxServerGroup(17)内才算,边界:0 与负数、18 及以上(含会让 ushort 回绕的 65537)都给 0;1、14、17 原样
    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(14, 14)]
    [InlineData(16, 16)]
    [InlineData(17, 17)]
    [InlineData(18, 0)]
    [InlineData(255, 0)]
    [InlineData(65535, 0)]
    [InlineData(65536, 0)]
    [InlineData(65537, 0)]
    [InlineData(131073, 0)]
    [InlineData(int.MaxValue, 0)]
    public void A_group_id_counts_only_between_one_and_the_login_request_maximum(int groupId, int expected)
    {
        Assert.Equal(17, LoginRequest.MaxServerGroup);
        Assert.Equal((ushort)expected, OnlineDirectorySummary.ServerGroupFromId(groupId));
    }

    // ======== 五、种类与行格式 ========

    // 保护:kind 是槽位规则里的频道种类;没有槽位规则(名字没数字、频道号对不上)给 NoKind=-1;null 条目也是 -1
    [Fact]
    public void The_kind_is_the_slot_rule_channel_kind_or_minus_one()
    {
        ChannelDirectorySnapshot snapshot = MixedDirectory();

        Assert.Equal(-1, OnlineDirectorySummary.NoKind);
        Assert.Equal(0, OnlineDirectorySummary.KindOf(snapshot.Channels[0]));      // 规则(1,1) 种类 0(0 不是"没有")
        Assert.Equal(3, OnlineDirectorySummary.KindOf(snapshot.Channels[1]));
        Assert.Equal(-1, OnlineDirectorySummary.KindOf(snapshot.Channels[2]));     // 组 1 没有频道号 9
        Assert.Equal(11, OnlineDirectorySummary.KindOf(snapshot.Channels[3]));
        Assert.Equal(16, OnlineDirectorySummary.KindOf(snapshot.Channels[6]));
        Assert.Equal(-1, OnlineDirectorySummary.KindOf(snapshot.Channels[8]));     // 名字里没有数字
        Assert.Equal(-1, OnlineDirectorySummary.KindOf(null));
    }

    // 保护:一行里每个字段的位置与取值(usable 与 first 互相独立、candidate 0 不是 "-"、负的候选序号一律 "-"、大数原样)
    [Theory]
    [InlineData(0, false, false, -1, 0, -1, "PROBE_SWITCH dir entry=0 usable=0 first=0 candidate=- group=0 kind=-1")]
    [InlineData(12, true, true, 5, 17, 99, "PROBE_SWITCH dir entry=12 usable=1 first=1 candidate=5 group=17 kind=99")]
    [InlineData(3, true, false, 0, 1, 0, "PROBE_SWITCH dir entry=3 usable=1 first=0 candidate=0 group=1 kind=0")]
    [InlineData(4, false, true, -5, 14, 16, "PROBE_SWITCH dir entry=4 usable=0 first=1 candidate=- group=14 kind=16")]
    [InlineData(100, true, false, 12, 3, 11, "PROBE_SWITCH dir entry=100 usable=1 first=0 candidate=12 group=3 kind=11")]
    public void An_entry_line_has_the_fields_in_this_order(int index, bool usable, bool first, int candidate, int group, int kind, string expected)
    {
        var entry = new OnlineDirectoryEntrySummary(index, usable, first, candidate, (ushort)group, kind);

        Assert.Equal(expected, entry.ToLine());
        Assert.Equal(index, entry.Index);
        Assert.Equal(usable, entry.Usable);
        Assert.Equal(first, entry.First);
        Assert.Equal(candidate, entry.CandidateIndex);
        Assert.Equal((ushort)group, entry.ServerGroup);
        Assert.Equal(kind, entry.Kind);
    }

    // 保护:行里的数字不受当前区域文化影响(某些文化的负号不是 ASCII 的 '-',kind=-1 与汇总都必须是 ASCII)
    [Fact]
    public void The_numbers_are_formatted_with_the_invariant_culture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            // 前提:这个文化的负号不是 ASCII '-'(否则这个测试什么也证明不了)
            Assert.NotEqual("-1", (-1).ToString());

            OnlineDirectorySummary summary = OnlineDirectorySummary.Build(DirectorySnapshot("SYNTH-GROUP", (HostB, PortB)).Channels, GatewayHost, FirstPort);

            Assert.Equal(new[]
            {
                "PROBE_SWITCH dir entry=0 usable=1 first=0 candidate=0 group=0 kind=-1",
                "PROBE_SWITCH dir-summary entries=1 usable=1 candidates=1 firstInDirectory=0",
            }, summary.ToLines());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // 保护:ToLines 是逐条目一行、最后一行汇总;SummaryLine 单独取得到同一行
    [Fact]
    public void To_lines_ends_with_the_summary_line()
    {
        OnlineDirectorySummary summary = Mixed(out _);

        IReadOnlyList<string> lines = summary.ToLines();

        Assert.Equal(summary.Entries.Count + 1, lines.Count);
        Assert.Equal(summary.SummaryLine(), lines[^1]);
        Assert.Equal("PROBE_SWITCH dir-summary entries=13 usable=10 candidates=7 firstInDirectory=1", summary.SummaryLine());
        for (int i = 0; i < summary.Entries.Count; i++)
        {
            Assert.Equal(summary.Entries[i].ToLine(), lines[i]);
        }
    }
}
