using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 进副本薄封装(<see cref="OnlineDungeonRunner"/>)的"战斗接力"测试:调过 <see cref="OnlineDungeonRunner.UseCombat"/> 之后,
/// 进副本成功(拿到起始地图)就接着在同一个会话上跑联网战斗,战斗结束之后才轮到保活;没调用时一字不变。
/// 假会话从选副本流程(命令 15、16)开始回放合成的 27、28、29,29 里是合成的多怪起始地图;怪编号 9100 起,没有任何真实编号。
/// </summary>
public class OnlineDungeonRunnerCombatTests
{
    private const string Combat = "DNF_NET stage=combat";
    private static readonly TimeSpan Roomy = TimeSpan.FromSeconds(5);

    private static Task<OnlineDungeonResult> Run(OnlineDungeonRunner runner, HostTestKit.LogSink log, Action<OnlineDungeonResult> entered = null, CancellationToken cancellationToken = default)
    {
        return GatewayTestSupport.Bounded(runner.RunAsync(901, 3, log.Add, cancellationToken, Roomy, Roomy, Roomy, entered), 30);
    }

    private static int IndexOf(string[] lines, Func<string, bool> match, string what)
    {
        int index = Array.FindIndex(lines, l => match(l));
        Assert.True(index >= 0, "没有找到日志行:" + what + "\n已有:\n" + string.Join("\n", lines));
        return index;
    }

    /// <summary>
    /// 主接力:进副本五步 → 宿主在"进副本有了结局"的回调里把本地击杀排进队列 → 战斗从起始地图跑到回城就绪 → 然后才起保活。
    /// 日志里的先后:进副本完成行、战斗开始行、战斗完成行、保活开始行、保活结束行;服务端视角:先杀完、清图、再结算。
    /// </summary>
    [Fact]
    public async Task Entry_then_combat_then_keep_alive_in_that_order()
    {
        int[] ids = CombatKit.Sequential(3);
        var server = new FakeCombatServer(ids);
        CombatFakeSession session = CombatFakeSession.ForEntry(server, CombatKit.Map29Payload(ids));
        var order = new List<string>();
        var log = new HostTestKit.LogSink();
        var runner = new OnlineDungeonRunner(session, token =>
        {
            order.Add("keepalive");
            return Task.FromResult(7);
        });
        OnlineDungeonCombatRunner combat = runner.UseCombat(CombatKit.Options());
        OnlineDungeonResult seenByHost = null;
        var accepted = new List<bool>();

        // 回调里抛的异常会被封装吞掉,所以断言都放到回调外面
        OnlineDungeonResult result = await Run(runner, log, entered: r =>
        {
            order.Add("entered");
            seenByHost = r;
            foreach (int local in new[] { 2, 0, 1 })
            {
                accepted.Add(combat.PostKill(local));
            }
        });

        Assert.True(result.Entered);
        Assert.Same(result, seenByHost);
        Assert.Equal(3, seenByHost.StartMap.Monsters.Count);
        Assert.Equal(new[] { true, true, true }, accepted.ToArray());
        Assert.Equal(new[] { "entered", "keepalive" }, order);
        Assert.Equal(new[] { "kill:9102", "kill:9100", "kill:9101", "clear", "settle" }, server.Journal);
        Assert.Equal(0, server.PrematureSettlements);
        Assert.True(combat.Ended);
        Assert.Equal(OnlineCombatPhase.Settled, combat.Machine.Phase);

        // 命令的先后:进副本的两条(15、16)、三条击杀、一条结算
        Assert.Equal(
            new[] { (PacketClass.Cmd, (ushort)15), (PacketClass.Cmd, (ushort)16), (PacketClass.Cmd, (ushort)42), (PacketClass.Cmd, (ushort)42), (PacketClass.Cmd, (ushort)42), (PacketClass.Cmd, (ushort)49) },
            session.Sent.Select(s => (s.Class, s.Id)).ToArray());

        string[] lines = log.Lines;
        int dungeonDone = IndexOf(lines, l => l.StartsWith("DNF_NET stage=dungeon done "), "进副本完成");
        int combatBegin = IndexOf(lines, l => l == Combat + " begin monsters=3 duplicateNumbers=0", "战斗开始");
        int combatDone = IndexOf(lines, l => l.StartsWith(Combat + " done outcome=ReturnReady phase=Settled monsters=3 sent=3 confirmed=3 "), "战斗完成");
        int keepStart = IndexOf(lines, l => l == "DNF_NET stage=dungeon keepalive=start", "保活开始");
        int keepEnd = IndexOf(lines, l => l == "DNF_NET heartbeat answered=7", "保活结束");
        Assert.True(dungeonDone < combatBegin && combatBegin < combatDone && combatDone < keepStart && keepStart < keepEnd,
            "先后不对:\n" + string.Join("\n", lines));
    }

    /// <summary>没调 UseCombat:起始地图里有怪也不发任何战斗命令、不打战斗行,进副本之后直接保活,与原来一字不变。</summary>
    [Fact]
    public async Task Without_UseCombat_nothing_is_fought_even_when_the_map_has_monsters()
    {
        int[] ids = CombatKit.Sequential(3);
        var server = new FakeCombatServer(ids);
        CombatFakeSession session = CombatFakeSession.ForEntry(server, CombatKit.Map29Payload(ids));
        var log = new HostTestKit.LogSink();
        int keepAlive = 0;
        var runner = new OnlineDungeonRunner(session, token =>
        {
            keepAlive++;
            return Task.FromResult(0);
        });

        OnlineDungeonResult result = await Run(runner, log);

        Assert.True(result.Entered);
        Assert.Equal(3, result.StartMap.Monsters.Count);
        Assert.Equal(1, keepAlive);
        Assert.Equal(new ushort[] { 15, 16 }, session.Sent.Select(s => s.Id).ToArray());
        Assert.DoesNotContain(log.Lines, l => l.StartsWith(Combat));
        Assert.Empty(server.Journal);
    }

    /// <summary>进副本没成功(拿不到起始地图):不跑战斗,战斗跑器没有状态机,保活照原来的规则接。</summary>
    [Fact]
    public async Task Combat_is_not_started_when_the_entry_fails()
    {
        int[] ids = CombatKit.Sequential(2);
        var server = new FakeCombatServer(ids);
        var session = new CombatFakeSession
        {
            Respond = (cls, id, payload) =>
                cls == PacketClass.Cmd && id == 15 ? new[] { new CombatKit.ServerFrame(CombatKit.Noti, 27, DungeonEntryWire.Loading27()) }
                : cls == PacketClass.Cmd && id == 16 ? new[] { new CombatKit.ServerFrame(CombatKit.Noti, 28, DungeonEntryWire.Info28()) }     // 没有 29
                : server.Handle(cls, id, payload),
        };
        var log = new HostTestKit.LogSink();
        int keepAlive = 0;
        var runner = new OnlineDungeonRunner(session, token =>
        {
            keepAlive++;
            return Task.FromResult(0);
        });
        OnlineDungeonCombatRunner combat = runner.UseCombat(CombatKit.Options());

        OnlineDungeonResult result = await GatewayTestSupport.Bounded(
            runner.RunAsync(901, 3, log.Add, default, Roomy, Roomy, TimeSpan.FromMilliseconds(300)), 30);

        Assert.False(result.Entered);
        Assert.Null(combat.Machine);
        Assert.False(combat.Ended);
        Assert.DoesNotContain(log.Lines, l => l.StartsWith(Combat));
        Assert.Equal(1, keepAlive);                                  // 超时之后连接还活着:保活照接(既有规则)
        Assert.Equal(0, session.Commands(42) + session.Commands(49));
    }

    /// <summary>起始地图是没有怪的短形:战斗照跑(空表),闸门不因没有怪而放行;整段上限一到就收场,然后才保活。</summary>
    [Fact]
    public async Task An_empty_start_map_runs_combat_until_the_overall_timeout_and_never_settles()
    {
        var server = new FakeCombatServer(new int[0]);
        CombatFakeSession session = CombatFakeSession.ForEntry(server, DungeonEntryWire.Map29());
        var log = new HostTestKit.LogSink();
        int keepAlive = 0;
        var runner = new OnlineDungeonRunner(session, token =>
        {
            keepAlive++;
            return Task.FromResult(0);
        });
        OnlineDungeonCombatRunner combat = runner.UseCombat(CombatKit.Options(overallSeconds: 0.3));

        OnlineDungeonResult result = await Run(runner, log);

        Assert.True(result.Entered);
        Assert.Empty(result.StartMap.Monsters);
        Assert.Equal(0, session.Commands(49));
        Assert.Equal(0, server.PrematureSettlements);
        Assert.Equal(1, keepAlive);
        string[] lines = log.Lines;
        Assert.Equal(Combat + " begin monsters=0 duplicateNumbers=0", lines[IndexOf(lines, l => l.StartsWith(Combat + " begin "), "战斗开始")]);
        IndexOf(lines, l => l.StartsWith(Combat + " ended outcome=Timeout phase=Fighting monsters=0 "), "战斗以超时收场");
    }

    /// <summary>战斗期间取消:安静地结束,返回进副本的结果,不接保活、不打保活结束行。</summary>
    [Fact]
    public async Task Cancelling_during_combat_ends_quietly_without_keep_alive()
    {
        int[] ids = CombatKit.Sequential(2);
        var server = new FakeCombatServer(ids) { WithholdClear = true };
        CombatFakeSession session = CombatFakeSession.ForEntry(server, CombatKit.Map29Payload(ids));
        var log = new HostTestKit.LogSink();
        int keepAlive = 0;
        var runner = new OnlineDungeonRunner(session, token =>
        {
            keepAlive++;
            return Task.FromResult(0);
        });
        OnlineDungeonCombatRunner combat = runner.UseCombat(CombatKit.Options(overallSeconds: null));
        using var cts = new CancellationTokenSource();

        Task<OnlineDungeonResult> run = Run(runner, log, cancellationToken: cts.Token);
        await log.WaitFor(l => l.StartsWith(Combat + " begin "), "战斗开始");
        cts.Cancel();
        OnlineDungeonResult result = await run;

        Assert.NotNull(result);
        Assert.True(result.Entered);
        Assert.Equal(0, keepAlive);
        Assert.True(combat.Ended);
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("DNF_NET stage=dungeon keepalive"));
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("DNF_NET heartbeat"));
        IndexOf(log.Lines, l => l.StartsWith(Combat + " ended outcome=Cancelled "), "战斗以取消收场");
    }

    /// <summary>战斗期间连接断了:战斗以断线收场,之后的保活按既有规则跳过并写明原因。</summary>
    [Fact]
    public async Task A_connection_lost_during_combat_skips_the_keep_alive_with_the_reason()
    {
        int[] ids = CombatKit.Sequential(2);
        var server = new FakeCombatServer(ids) { WithholdClear = true };
        CombatFakeSession session = CombatFakeSession.ForEntry(server, CombatKit.Map29Payload(ids));
        var log = new HostTestKit.LogSink();
        int keepAlive = 0;
        var runner = new OnlineDungeonRunner(session, token =>
        {
            keepAlive++;
            return Task.FromResult(0);
        });
        runner.UseCombat(CombatKit.Options(overallSeconds: null));

        Task<OnlineDungeonResult> run = Run(runner, log);
        await log.WaitFor(l => l.StartsWith(Combat + " begin "), "战斗开始");
        session.Closed = true;
        OnlineDungeonResult result = await run;

        Assert.True(result.Entered);
        Assert.Equal(0, keepAlive);
        string[] lines = log.Lines;
        int ended = IndexOf(lines, l => l.StartsWith(Combat + " ended outcome=ConnectionClosed "), "战斗以断线收场");
        int skip = IndexOf(lines, l => l == "DNF_NET stage=dungeon keepalive=skip reason=ConnectionClosed", "保活跳过");
        Assert.True(ended < skip);
    }

    /// <summary>UseCombat 重复调用以最后一次为准:只有最后一个跑器跑起来,前一个始终没有状态机。</summary>
    [Fact]
    public async Task The_last_call_to_UseCombat_wins()
    {
        int[] ids = CombatKit.Sequential(1);
        var server = new FakeCombatServer(ids);
        CombatFakeSession session = CombatFakeSession.ForEntry(server, CombatKit.Map29Payload(ids));
        var runner = new OnlineDungeonRunner(session, token => Task.FromResult(0));
        OnlineDungeonCombatRunner first = runner.UseCombat(CombatKit.Options());
        OnlineDungeonCombatRunner second = runner.UseCombat(CombatKit.Options());
        Assert.NotSame(first, second);

        await Run(runner, new HostTestKit.LogSink(), entered: r => second.PostKill(0));

        Assert.Null(first.Machine);
        Assert.NotNull(second.Machine);
        Assert.Equal(OnlineCombatPhase.Settled, second.Machine.Phase);
    }

    [Fact]
    public void UseCombat_validates_the_options_when_the_runner_is_made()
    {
        var runner = new OnlineDungeonRunner(new CombatFakeSession(), token => Task.FromResult(0));

        Assert.Throws<ArgumentOutOfRangeException>(() => runner.UseCombat(new OnlineDungeonCombatOptions { PollInterval = TimeSpan.Zero }));
        Assert.NotNull(runner.UseCombat());                           // null 选项:全用缺省
    }
}
