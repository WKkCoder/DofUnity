using System.Text.RegularExpressions;

namespace Dnf.Net.Tests;

/// <summary>
/// 探针胶水(<c>src/Dnf.NetProbe/FightCommand.cs</c>)的结构守卫。探针是个没有测试工程的可执行程序,胶水本身没法单元测试(E-455 的先例:只有用法路径的冒烟);
/// 所以放弃游戏的策略、编排、拼行全放在 Net 层(有行为测试),胶水只剩"把开关传下去、在对的位置调一次、结果行换成 Net 层的选择"。
/// 这里用读源码的办法守住这几处位置与顺序:放弃只在开关里、在战斗与菜单之后、在挂着与结果行之前;
/// 装配、认证、进城、进副本的失败(都是 <c>return Abort(...)</c>)全在它之前;退出码的表达式不碰放弃的结果。
/// 找不到仓库根(没有 <c>DnfUnity.slnx</c>)或源文件就失败,不悄悄跳过。
/// </summary>
public class OnlineGiveUpGlueTests
{
    private const string OldUsageText = "用法: dnf-netprobe fight [仓库根] --dungeon <副本编号 0-65535> <模式 0-4> [--pace-ms N] [--clear-wait S] [--settle-wait S] [--continue-on-refuse] [--stay S] [--min-clear-seconds S] [--after-settle [--menu-choice town]]";

    private static string RawSource()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "DnfUnity.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.True(dir != null, "找不到仓库根(应含 DnfUnity.slnx)");
        string path = Path.Combine(dir, "src", "Dnf.NetProbe", "FightCommand.cs");
        Assert.True(File.Exists(path), "找不到 " + path);
        return File.ReadAllText(path);
    }

    // 空白折成一个空格:格式调整不影响这里的片段匹配
    private static string Normalized()
    {
        return Regex.Replace(RawSource(), @"\s+", " ");
    }

    private static string RunCore(string normalized)
    {
        int start = normalized.IndexOf("private static async Task<int> RunCoreAsync(", StringComparison.Ordinal);
        int end = normalized.IndexOf("private static async Task StayAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "找不到 RunCoreAsync 的范围");
        return normalized.Substring(start, end - start);
    }

    private static int At(string text, string fragment)
    {
        int index = text.IndexOf(fragment, StringComparison.Ordinal);
        Assert.True(index >= 0, "源码里找不到片段:" + fragment);
        Assert.Equal(index, text.LastIndexOf(fragment, StringComparison.Ordinal));      // 而且只出现一次
        return index;
    }

    private static int Count(string text, string fragment)
    {
        int count = 0;
        int from = 0;
        while ((from = text.IndexOf(fragment, from, StringComparison.Ordinal)) >= 0)
        {
            count++;
            from += fragment.Length;
        }
        return count;
    }

    [Fact]
    public void The_usage_text_only_gains_the_new_switch_at_the_end()
    {
        string source = RawSource();
        Match m = Regex.Match(source, "private const string UsageText = \"(?<text>[^\"]*)\";");
        Assert.True(m.Success, "找不到 UsageText");

        Assert.Equal(OldUsageText + " [--give-up-on-failure] [--frame-log]", m.Groups["text"].Value);
        // 冲突时多打的那一行许可表用法文字没动
        Assert.Contains("private const string PermitsUsageText = \"只看许可表: dnf-netprobe fight [仓库根] --permits-only(不要 --dungeon;登录进城后打印一行许可表就退出,不进副本)\";", source);
    }

    [Fact]
    public void The_give_up_stage_sits_inside_the_switch_after_the_fight_and_the_menu_and_before_the_stay_and_the_result()
    {
        string core = RunCore(Normalized());

        int settings = At(core, "stage=settings ok=1");
        int town = At(core, "new OnlineTownEntry(settings)");
        int permits = At(core, "if (parsed.PermitsOnly) {");
        int enter = At(core, "dungeonRunner.EnterAsync(");
        int fight = At(core, "driver.RunAsync(");
        int verdict = At(core, "stage=verdict ");
        int menu = At(core, "menuSummary = await afterSettle.RunAsync(");
        int gate = At(core, "if (parsed.GiveUpOnFailure) {");
        int finish = At(core, "OnlineGiveUpOnFailure.FinishAsync(parsed.GiveUpOnFailure, giveUpDriver, summary, menuSummary,");
        int stay = At(core, "await StayAsync(clock, town, parsed.StaySeconds);");
        int result = At(core, "OnlineFightLog.ResultFieldsFor(parsed, summary, menuSummary, giveUp)");

        Assert.True(settings < town && town < permits && permits < enter && enter < fight && fight < verdict && verdict < menu && menu < gate && gate < finish && finish < stay && stay < result,
            "顺序应为 装配 < 进城 < 许可表 < 进副本 < 战斗 < 判读 < 菜单 < 放弃 < 挂着 < 结果行");

        // 调用就在开关的花括号里:开关开头到调用之间的花括号成对(没有提前把开关的块收掉),调用语句一结束块就收掉
        const string gateText = "if (parsed.GiveUpOnFailure) {";
        string between = core.Substring(gate + gateText.Length, finish - gate - gateText.Length);
        Assert.Equal(Count(between, "{"), Count(between, "}"));
        int callEnd = core.IndexOf("token);", finish, StringComparison.Ordinal);
        Assert.True(callEnd > finish);
        Assert.StartsWith(" }", core.Substring(callEnd + "token);".Length), StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_auth_town_and_dungeon_entry_failures_all_leave_before_the_give_up_stage()
    {
        string core = RunCore(Normalized());
        int gate = At(core, "if (parsed.GiveUpOnFailure) {");

        // 三处 return Abort:装配失败(2)、进城/认证失败(3、5)、进副本失败(6)
        Assert.Equal(3, Count(core, "return Abort("));
        Assert.Equal(1, Count(core, "return Abort(clock, \"settings\", \"settings-failed\", OnlineFightExitCodes.SettingsFailed,"));
        Assert.Equal(1, Count(core, "return Abort(clock, \"town\", reason, code,"));
        Assert.Equal(1, Count(core, "return Abort(clock, \"dungeon\", why, OnlineFightExitCodes.DungeonEntryFailed,"));
        int from = 0;
        while ((from = core.IndexOf("return Abort(", from, StringComparison.Ordinal)) >= 0)
        {
            Assert.True(from < gate, "所有提前退出都必须在放弃那一段之前");
            from += 1;
        }
        // 放弃那一段之后不再有任何 Abort(这个方法里)
        Assert.DoesNotContain("Abort(", core.Substring(gate));
    }

    [Fact]
    public void The_exit_code_expression_never_mentions_the_give_up()
    {
        string core = RunCore(Normalized());
        int result = At(core, "Console.WriteLine(\"PROBE_FIGHT result \" + resultFields);");
        string tail = core.Substring(result);

        Assert.Contains("return OnlineFightExitCodes.ForOutcome(summary.Outcome);", tail);
        Assert.Contains("return menuSummary != null ? OnlineAfterSettleExitCodes.ForOutcome(menuSummary.Outcome) : OnlineFightExitCodes.Success;", tail);
        Assert.DoesNotContain("giveUp", tail, StringComparison.OrdinalIgnoreCase);
        // 结果行本身与以前同形:一行 PROBE_FIGHT result + 字段
        Assert.Contains("string resultFields = OnlineFightLog.ResultFieldsFor(parsed, summary, menuSummary, giveUp);", core);
    }

    [Fact]
    public void The_result_fields_come_only_from_the_net_layer_selection()
    {
        string source = Normalized();

        Assert.Equal(1, Count(source, "OnlineFightLog.ResultFieldsFor("));
        Assert.Equal(0, Count(source, "OnlineFightLog.ResultFields("));
        Assert.Equal(0, Count(source, "OnlineFightLog.ResultFieldsWithAfterSettle("));
        Assert.Equal(0, Count(source, "OnlineFightLog.ResultFieldsWithGiveUp("));
        Assert.Equal(1, Count(source, "Console.WriteLine(\"PROBE_FIGHT result \""));
    }

    [Fact]
    public void The_switch_is_read_in_exactly_three_places_and_the_wait_is_the_runner_give_up_timeout()
    {
        string core = RunCore(Normalized());

        Assert.Equal(3, Count(core, "parsed.GiveUpOnFailure"));
        Assert.Contains("GiveUpOnFailure = parsed.GiveUpOnFailure,", core);
        Assert.Contains("GiveUpWait = combatOptions.GiveUpTimeout,", core);
        Assert.Contains("GiveUpWaitTimeout = combatOptions.GiveUpTimeout,", core);
        Assert.Contains("OnlineDungeonCombatRunner runner = dungeonRunner.UseCombat(combatOptions);", core);
        // 放弃用的会话层驱动只在开关里造一个(另一个 new OnlineAfterSettleDriver 是 --after-settle 的菜单驱动)
        Assert.Equal(2, Count(core, "new OnlineAfterSettleDriver("));
        Assert.True(core.IndexOf("new OnlineAfterSettleDriver(", StringComparison.Ordinal) < core.IndexOf("if (parsed.GiveUpOnFailure) {", StringComparison.Ordinal));
        Assert.True(core.LastIndexOf("new OnlineAfterSettleDriver(", StringComparison.Ordinal) > core.IndexOf("if (parsed.GiveUpOnFailure) {", StringComparison.Ordinal));
        // 菜单驱动仍然只在 --after-settle 且战斗走到 ReturnReady 时才造
        Assert.Contains("if (parsed.AfterSettle && summary.Outcome == OnlineFightOutcome.AllDone && summary.ReturnReady) {", core);
    }

    [Fact]
    public void The_stay_is_still_skipped_after_a_user_abort_and_also_after_an_aborted_give_up()
    {
        string core = RunCore(Normalized());

        Assert.Contains(
            "if (parsed.StaySeconds > 0 && summary.Outcome != OnlineFightOutcome.Cancelled && (menuSummary == null || menuSummary.Outcome != OnlineAfterSettleOutcome.Cancelled) && (giveUp == null || giveUp.Outcome != OnlineGiveUpOutcome.Cancelled)) {",
            core);
    }

    [Fact]
    public void The_net_lines_reach_the_console_only_through_the_whitelist_and_the_give_up_lines_are_passed_that_way()
    {
        string source = Normalized();

        Assert.Contains("string text = OnlineFightLog.ProbeText(line);", source);
        Assert.Contains("line => SayNet(clock, line), token);", source);
        // 放弃步骤的行是 Net 层给的字符串,探针没有自己拼
        Assert.DoesNotContain("stage=giveup step=", RawSourceWithoutComments());
    }

    // 去掉 // 与 /// 注释后的源码:检查"探针没有自己拼放弃的日志行"时不被说明文字干扰
    private static string RawSourceWithoutComments()
    {
        string[] lines = RawSource().Split('\n');
        var kept = new List<string>();
        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }
}
