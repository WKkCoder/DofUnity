using System.Text.RegularExpressions;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.PktImplTestKit;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针 fight 子命令的默认关闭诊断选项 <c>--permits-only</c>(登录进城后只打印副本许可表就退出,给集成者选副本用)的测试:
/// 命令行解析(不需要 <c>--dungeon</c>、与 <c>--dungeon</c> 冲突、边界)与输出行的拼法(Net 层纯函数,探针只管打)。
/// 许可表一律用合成的副本号(900 以上)与合成档位(0 到 4 与 255),通知载荷用 <see cref="PktImplTestKit"/> 的独立写入器手工拼,走真实的解码与合并路径;
/// 不读任何真实帧,不含真实编号或数值,不连网。
/// 探针胶水(<c>FightCommand</c>:进城之后打两行、退出码 0)没有自动化覆盖,只有用法路径的冒烟;能测的逻辑都在这里。
/// </summary>
public class OnlineFightPermitsTests
{
    // 许可通知的包号写字面量,不引用产品常量:产品常量被改动时,测试要能发现
    private const ushort PermissionId = 5;

    private static OnlineFightArguments Parse(params string[] args)
    {
        Assert.True(OnlineFightArguments.TryParse(args, out OnlineFightArguments parsed, out OnlineFightArgumentProblem problem), "应当解析成功,实际问题 " + problem);
        Assert.Equal(OnlineFightArgumentProblem.None, problem);
        Assert.NotNull(parsed);
        return parsed;
    }

    private static OnlineFightArgumentProblem Problem(string joined)
    {
        // "|" 分隔各个参数;空串表示没有参数
        string[] args = joined.Length == 0 ? new string[0] : joined.Split('|');
        Assert.False(OnlineFightArguments.TryParse(args, out OnlineFightArguments parsed, out OnlineFightArgumentProblem problem), "应当解析失败:" + joined);
        Assert.Null(parsed);
        return problem;
    }

    private static DecodedFrame Notice(byte[] payload)
    {
        return PktImplTestKit.Frame(Noti, PermissionId, payload);
    }

    /// <summary>把若干份许可通知载荷按顺序喂给真的合并路径(<see cref="DungeonPermissionParser.Collect"/>),得到快照。</summary>
    private static DungeonPermissionSnapshot Snapshot(params byte[][] payloads)
    {
        DungeonPermissionSnapshot snapshot = DungeonPermissionParser.Collect(payloads.Select(Notice).ToArray());
        Assert.NotNull(snapshot);
        return snapshot;
    }

    private static byte[] Table(params (int DungeonId, int Tier)[] entries)
    {
        return Permission(entries);
    }

    /// <summary>读不通的通知:声明 4 条、实际只有 1 条。</summary>
    private static byte[] ShortTable()
    {
        return PermissionWithDeclaredCount(4, new[] { (901, 1) });
    }

    // ======== 命令行:只看许可表,不需要 --dungeon ========

    [Fact]
    public void Permits_only_needs_no_dungeon_and_leaves_every_other_default_alone()
    {
        OnlineFightArguments a = Parse("--permits-only");

        Assert.True(a.PermitsOnly);
        Assert.Null(a.RepoRoot);
        Assert.Equal(0, a.DungeonId);
        Assert.Equal(0, a.Mode);
        Assert.Equal(400, a.PaceMs);
        Assert.Equal(TimeSpan.FromSeconds(25), a.ClearWait);
        Assert.Equal(TimeSpan.FromSeconds(25), a.SettleWait);
        Assert.False(a.ContinueOnRefuse);
        Assert.Equal(0, a.StaySeconds);
    }

    [Fact]
    public void A_repo_root_in_the_first_position_goes_with_permits_only()
    {
        OnlineFightArguments a = Parse("some-root", "--permits-only");

        Assert.Equal("some-root", a.RepoRoot);
        Assert.True(a.PermitsOnly);
    }

    [Fact]
    public void The_option_is_off_unless_it_is_given()
    {
        Assert.False(Parse("--dungeon", "901", "0").PermitsOnly);
        Assert.False(Parse("some-root", "--dungeon", "901", "4", "--pace-ms", "250", "--continue-on-refuse", "--stay", "5").PermitsOnly);
        // 给了多次也只是开着
        Assert.True(Parse("--permits-only", "--permits-only").PermitsOnly);
    }

    [Fact]
    public void The_other_fight_options_are_still_read_and_checked_but_do_nothing_with_permits_only()
    {
        OnlineFightArguments a = Parse("--pace-ms", "250", "--permits-only", "--continue-on-refuse", "--stay", "7");

        Assert.True(a.PermitsOnly);
        Assert.Equal(250, a.PaceMs);
        Assert.True(a.ContinueOnRefuse);
        Assert.Equal(7, a.StaySeconds);
        // 它们的范围检查照旧
        Assert.Equal(OnlineFightArgumentProblem.BadPaceMs, Problem("--permits-only|--pace-ms|60001"));
        Assert.Equal(OnlineFightArgumentProblem.BadStay, Problem("--stay|-1|--permits-only"));
    }

    // ======== 命令行:与 --dungeon 冲突、边界 ========

    [Theory]
    [InlineData("--permits-only|--dungeon|901|0")]                          // 谁先谁后都一样
    [InlineData("--dungeon|901|0|--permits-only")]
    [InlineData("some-root|--permits-only|--dungeon|901|4")]
    [InlineData("--dungeon|0|0|--pace-ms|250|--permits-only")]
    [InlineData("--permits-only|--permits-only|--dungeon|65535|4")]
    [InlineData("--permits-only|--continue-on-refuse|--dungeon|901|0|--stay|3")]
    public void Permits_only_together_with_a_dungeon_is_a_conflict_in_either_order(string joined)
    {
        Assert.Equal(OnlineFightArgumentProblem.Conflict, Problem(joined));
    }

    [Theory]
    [InlineData("--permits-only|--dungeon", OnlineFightArgumentProblem.BadDungeon)]          // 每个参数先得读得通,之后才判冲突
    [InlineData("--permits-only|--dungeon|901", OnlineFightArgumentProblem.BadDungeon)]
    [InlineData("--permits-only|--dungeon|abc|0", OnlineFightArgumentProblem.BadDungeon)]
    [InlineData("--dungeon|901|5|--permits-only", OnlineFightArgumentProblem.BadDungeon)]
    [InlineData("--permits-only|--bogus", OnlineFightArgumentProblem.UnknownArgument)]
    [InlineData("--permits-only|some-root", OnlineFightArgumentProblem.UnknownArgument)]     // 仓库根只能在第一个位置
    [InlineData("--permits-only|--dungeon|901|0|some-root", OnlineFightArgumentProblem.UnknownArgument)]
    [InlineData("--permits-only|--pace-ms", OnlineFightArgumentProblem.BadPaceMs)]
    [InlineData("--permits-only|--clear-wait|0", OnlineFightArgumentProblem.BadClearWait)]
    [InlineData("--permits-only|--settle-wait|x", OnlineFightArgumentProblem.BadSettleWait)]
    public void A_bad_command_line_with_permits_only_is_refused_with_the_name_of_the_first_problem(string joined, OnlineFightArgumentProblem expected)
    {
        Assert.Equal(expected, Problem(joined));
    }

    [Theory]
    [InlineData("")]
    [InlineData("some-root")]
    [InlineData("--continue-on-refuse")]
    [InlineData("--pace-ms|250")]
    [InlineData("--stay|5")]
    [InlineData("some-root|--clear-wait|40|--settle-wait|30")]
    [InlineData("--permits")]
    [InlineData("--permit-only")]
    [InlineData("--PERMITS-ONLY")]
    public void Without_permits_only_the_dungeon_is_still_required_and_near_misses_of_the_option_name_are_not_it(string joined)
    {
        // 没有 --dungeon、也没有这个选项:恰好是"缺 --dungeon";选项名拼错(大小写也算)的是"不认识的参数"
        bool nearMiss = joined.StartsWith("--permit", StringComparison.OrdinalIgnoreCase);

        Assert.Equal(nearMiss ? OnlineFightArgumentProblem.UnknownArgument : OnlineFightArgumentProblem.MissingDungeon, Problem(joined));
    }

    [Fact]
    public void The_conflict_is_reported_by_name_only_and_never_echoes_what_the_user_typed()
    {
        // 探针打的是 "reason=" + 枚举名:问题只有名字,命令行里的仓库根、副本号都不会出现在里面
        OnlineFightArgumentProblem problem = Problem("secret-root|--permits-only|--dungeon|901|2");

        Assert.Equal(OnlineFightArgumentProblem.Conflict, problem);
        string line = "PROBE_FIGHT abort at=usage reason=" + problem + " exit=" + OnlineFightExitCodes.Usage;
        Assert.Equal("PROBE_FIGHT abort at=usage reason=Conflict exit=64", line);
        Assert.DoesNotContain("secret", line);
        Assert.DoesNotContain("901", line);
    }

    // ======== 输出行:许可表的拼法 ========

    [Fact]
    public void The_entries_are_listed_in_ascending_dungeon_order_whatever_order_they_arrived_in()
    {
        // 线上顺序 940、901、905、912(第一次出现的先后),输出必须按副本号升序
        DungeonPermissionSnapshot s = Snapshot(Table((940, 2), (901, 1), (905, 4), (912, 3)));

        Assert.Equal(new[] { 940, 901, 905, 912 }, s.Entries.Select(e => (int)e.DungeonId).ToArray());
        Assert.Equal("notices=1 malformed=0 count=4 entries=901:1,905:4,912:3,940:2", OnlineFightLog.PermitsFields(s));
    }

    [Fact]
    public void The_order_is_numeric_and_not_the_order_of_the_digits_as_text()
    {
        DungeonPermissionSnapshot s = Snapshot(Table((1000, 1), (901, 2), (65535, 3), (9000, 4), (902, 0)));

        Assert.Equal("notices=1 malformed=0 count=5 entries=901:2,902:0,1000:1,9000:4,65535:3", OnlineFightLog.PermitsFields(s));
    }

    [Fact]
    public void Dungeon_numbers_and_tiers_are_plain_decimal_whatever_their_width()
    {
        Assert.Equal("notices=1 malformed=0 count=3 entries=901:0,902:255,903:128", OnlineFightLog.PermitsFields(Snapshot(Table((903, 128), (902, 255), (901, 0)))));
        Assert.Equal("notices=1 malformed=0 count=1 entries=900:1", OnlineFightLog.PermitsFields(Snapshot(Table((900, 1)))));
        Assert.Equal("notices=1 malformed=0 count=2 entries=0:4,65535:1", OnlineFightLog.PermitsFields(Snapshot(Table((65535, 1), (0, 4)))));
    }

    [Fact]
    public void A_later_notice_overrides_an_earlier_tier_and_the_counts_follow_the_agreed_meaning()
    {
        // 三份读得通的通知(合计 6 条、去重后 4 个副本)、两份读不通的:notices、malformed、count 三个数各不相同,互相串了就看得出来
        DungeonPermissionSnapshot s = Snapshot(
            Table((905, 1), (901, 2), (912, 3)),
            ShortTable(),
            Table((901, 4), (930, 1)),
            Table((912, 0)),
            new byte[] { 0x05, 0x00, 0x01 });

        Assert.Equal(3, s.NoticeCount);
        Assert.Equal(2, s.MalformedNoticeCount);
        Assert.Equal(4, s.Count);
        Assert.Equal("notices=3 malformed=2 count=4 entries=901:4,905:1,912:0,930:1", OnlineFightLog.PermitsFields(s));
    }

    [Fact]
    public void Notices_that_cannot_be_read_are_counted_and_an_all_unreadable_table_has_zero_entries()
    {
        Assert.Equal("notices=0 malformed=2 count=0 entries=-", OnlineFightLog.PermitsFields(Snapshot(ShortTable(), new byte[] { 0x05, 0x00, 0x01 })));
        Assert.Equal("notices=1 malformed=1 count=1 entries=901:1", OnlineFightLog.PermitsFields(Snapshot(ShortTable(), Table((901, 1)))));
    }

    [Fact]
    public void A_missing_snapshot_and_an_empty_table_are_told_apart()
    {
        // 进城窗口里一个许可通知也没有:没有快照,count 与 entries 都写 -
        Assert.Equal("notices=0 malformed=0 count=- entries=-", OnlineFightLog.PermitsFields(null));
        // 收到了个数为 0 的表:有快照、条目数是 0
        Assert.Equal("notices=1 malformed=0 count=0 entries=-", OnlineFightLog.PermitsFields(Snapshot(Table())));
        Assert.Equal("notices=2 malformed=0 count=0 entries=-", OnlineFightLog.PermitsFields(Snapshot(Table(), Table())));
    }

    [Fact]
    public void A_large_table_is_listed_in_full_and_in_order()
    {
        // 300 个副本号(900 起,倒着给),一个也不能少、不截断
        var entries = new (int DungeonId, int Tier)[300];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = (1199 - i, i % 5);
        }
        string fields = OnlineFightLog.PermitsFields(Snapshot(Table(entries)));

        string list = fields.Substring(fields.IndexOf("entries=", StringComparison.Ordinal) + "entries=".Length);
        string[] items = list.Split(',');
        Assert.StartsWith("notices=1 malformed=0 count=300 entries=900:", fields);
        Assert.Equal(300, items.Length);
        Assert.Equal(Enumerable.Range(900, 300).ToArray(), items.Select(x => int.Parse(x.Split(':')[0])).ToArray());
    }

    // ======== 输出行:整行与最后一行 ========

    [Fact]
    public void The_report_is_the_permits_line_and_then_the_result_line_as_the_last_line()
    {
        DungeonPermissionSnapshot s = Snapshot(Table((940, 2), (901, 1), (905, 4), (912, 3)));

        string[] report = OnlineFightLog.PermitsReport(s);

        Assert.Equal(
            new[]
            {
                "PROBE_FIGHT stage=permits notices=1 malformed=0 count=4 entries=901:1,905:4,912:3,940:2",
                "PROBE_FIGHT result permits=ok",
            },
            report);
        Assert.Equal("PROBE_FIGHT", OnlineFightLog.ProbePrefix);
        Assert.Equal("PROBE_FIGHT result permits=ok", report[report.Length - 1]);
    }

    [Fact]
    public void The_report_of_a_missing_snapshot_is_still_two_lines_and_still_ends_with_the_result_line()
    {
        string[] report = OnlineFightLog.PermitsReport(null);

        Assert.Equal(
            new[]
            {
                "PROBE_FIGHT stage=permits notices=0 malformed=0 count=- entries=-",
                "PROBE_FIGHT result permits=ok",
            },
            report);
    }

    [Fact]
    public void The_permits_line_only_ever_carries_dungeon_numbers_tiers_and_counts()
    {
        // 不带 t=,不带别的任何东西:整行必须符合这一个形状(没有账号、口令、地址、端口、令牌、载荷字节的位置)
        var shape = new Regex(@"^PROBE_FIGHT stage=permits notices=\d+ malformed=\d+ count=(\d+|-) entries=(-|\d+:\d+(,\d+:\d+)*)$");
        var resultShape = new Regex(@"^PROBE_FIGHT result permits=ok$");
        DungeonPermissionSnapshot[] snapshots =
        {
            null,
            Snapshot(Table()),
            Snapshot(ShortTable()),
            Snapshot(Table((940, 2), (901, 1), (905, 4), (912, 3))),
            Snapshot(Table((903, 128), (902, 255), (901, 0)), ShortTable(), Table((901, 4))),
        };
        foreach (DungeonPermissionSnapshot s in snapshots)
        {
            string[] report = OnlineFightLog.PermitsReport(s);
            Assert.Equal(2, report.Length);
            Assert.Matches(shape, report[0]);
            Assert.Matches(resultShape, report[1]);
            Assert.DoesNotContain(" t=", report[0]);
            Assert.DoesNotContain(" t=", report[1]);
        }
    }
}
