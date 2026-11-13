using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.NetProbe;

/// <summary>
/// <c>fight</c> 子命令:无头跑一遍"进城 → 进副本 → 杀光起始地图的怪 → 清图 → 结算 → 可回城",
/// 全程只用产品 Net 层的编排(<see cref="OnlineTownEntry"/>、<see cref="OnlineDungeonRunner"/>、<see cref="OnlineFightDriver"/>),探针自己不拼任何协议字节。
/// 用来在真服上回答头号问题:服务端认不认这个击杀载荷(确认还是拒绝)。
///
/// 用法:
///   dnf-netprobe fight [仓库根] --dungeon &lt;副本编号 0–65535&gt; &lt;模式 0–4&gt; [--pace-ms N] [--clear-wait S] [--settle-wait S] [--continue-on-refuse] [--stay S]
///
/// 输出一律脱敏:每行以 <c>PROBE_FIGHT </c> 开头,只含阶段名、枚举名、本地序号、包类/包号/载荷长度与各种计数;
/// 不含账号、口令、地址、端口、令牌、载荷字节,也不含服务端怪编号与角色数值(进副本的参数、选角槽号都不打)。
/// 线索行带 <c>t=</c>(自命令开始的秒数);最后一行要么是 <c>PROBE_FIGHT result …</c>(战斗跑完,不论成败),
/// 要么是 <c>PROBE_FIGHT abort …</c>(没走到战斗:装配、进城、认证、进副本失败或用法不对),两者互斥。
///
/// 安全:认证类失败(网关登录被拒、游戏登录被拒)立即以专用退出码退出,绝不自动重试、绝不循环重连;整条链是线性的,只连一次。
/// </summary>
internal static class FightCommand
{
    private const string UsageText = "用法: dnf-netprobe fight [仓库根] --dungeon <副本编号 0-65535> <模式 0-4> [--pace-ms N] [--clear-wait S] [--settle-wait S] [--continue-on-refuse] [--stay S]";

    // --stay 里最多列出多少个收到的帧(再多的只记"+N")。
    private const int MaxStayFrames = 48;

    public static async Task<int> RunAsync(string[] args)
    {
        var rest = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            rest.Add(args[i]);
        }
        if (!OnlineFightArguments.TryParse(rest, out OnlineFightArguments parsed, out OnlineFightArgumentProblem usageProblem))
        {
            Console.WriteLine("PROBE_FIGHT abort at=usage reason=" + usageProblem + " exit=" + OnlineFightExitCodes.Usage);
            Console.WriteLine(UsageText);
            return OnlineFightExitCodes.Usage;
        }

        var clock = Stopwatch.StartNew();
        string at = "settings";
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;            // 让流程自己收场(关连接),不被进程直接杀掉
            cancel.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return await RunCoreAsync(parsed, clock, cancel.Token, step => at = step);
        }
        catch (OperationCanceledException)
        {
            return Abort(clock, at, "cancelled", OnlineFightExitCodes.Cancelled, null);
        }
        catch (Exception e)
        {
            // 只打异常类型名:异常消息里可能带地址之类的内容
            return Abort(clock, at, "exception", OnlineFightExitCodes.Unexpected, "type=" + e.GetBaseException().GetType().Name);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static async Task<int> RunCoreAsync(OnlineFightArguments parsed, Stopwatch clock, CancellationToken token, Action<string> setAt)
    {
        string repoRoot = parsed.RepoRoot ?? Directory.GetCurrentDirectory();

        // ---- 装配
        setAt("settings");
        if (!LocalDevEntrySettings.TryLoad(repoRoot, out OnlineEntrySettings settings, out string problem))
        {
            return Abort(clock, "settings", "settings-failed", OnlineFightExitCodes.SettingsFailed, "detail=" + problem);
        }
        Say(clock, "stage=settings ok=1");

        // ---- 进城(产品编排,与 Unity 宿主同路;一次就是一次,失败不重试)
        setAt("town");
        var entry = new OnlineTownEntry(settings);
        OnlineEntryResult town = await entry.RunAsync(stage => Say(clock, "stage=town step=" + stage), token);
        using (town)
        {
            string loginCode = town.LoginFailureCode.HasValue ? town.LoginFailureCode.Value.ToString(CultureInfo.InvariantCulture) : "-";
            Say(clock, "stage=town done entered=" + Flag(town.Entered) + " reached=" + town.Stage + " failure=" + town.Failure
                + " loginFailureCode=" + loginCode
                + " frames=" + town.FramesSeen.ToString(CultureInfo.InvariantCulture)
                + " secondLayerMismatches=" + town.SecondLayerMismatches.ToString(CultureInfo.InvariantCulture));
            if (!town.Entered)
            {
                int code = OnlineFightExitCodes.ForTownFailure(town.Failure);
                string reason = code == OnlineFightExitCodes.AuthFailed ? "auth-failed" : "town-entry-failed";
                return Abort(clock, "town", reason, code, "failure=" + town.Failure + " reached=" + town.Stage + " loginFailureCode=" + loginCode + (code == OnlineFightExitCodes.AuthFailed ? " retry=never" : string.Empty));
            }

            // ---- 进副本五步(与 TownDemo 同样的用法与参数顺序:副本编号、模式)
            setAt("dungeon");
            var dungeonRunner = new OnlineDungeonRunner(town.Session);
            OnlineDungeonResult dungeon = await dungeonRunner.EnterAsync(parsed.DungeonId, parsed.Mode, line => SayNet(clock, line), token);
            if (!dungeon.Entered || dungeon.StartMap == null)
            {
                string why = dungeon.Entered ? "no-start-map" : "entry-failed";
                return Abort(clock, "dungeon", why, OnlineFightExitCodes.DungeonEntryFailed, "step=" + dungeon.Stage + " failure=" + dungeon.Failure);
            }
            StartMapNotice map = dungeon.StartMap;
            int monsters = map.Monsters.Count;
            Say(clock, "stage=dungeon entered=1 monsters=" + monsters.ToString(CultureInfo.InvariantCulture));

            // ---- 战斗:跑器的两个上限只当后备(驱动自己的两段等待先到),所以放宽到驱动的等待之后
            setAt("fight");
            TimeSpan backstop = TimeSpan.FromMilliseconds((double)monsters * parsed.PaceMs) + parsed.ClearWait + parsed.SettleWait + TimeSpan.FromSeconds(60);
            OnlineDungeonCombatRunner runner = dungeonRunner.UseCombat(new OnlineDungeonCombatOptions
            {
                SettlementTimeout = parsed.SettleWait + TimeSpan.FromSeconds(10),
                OverallTimeout = backstop,
            });
            var driver = new OnlineFightDriver(runner, map, new OnlineFightOptions
            {
                PaceMs = parsed.PaceMs,
                ContinueOnRefuse = parsed.ContinueOnRefuse,
                ClearWait = parsed.ClearWait,
                SettleWait = parsed.SettleWait,
            });
            OnlineFightSummary summary = await driver.RunAsync(line => SayNet(clock, line), token);
            Say(clock, "stage=verdict " + OnlineFightLog.VerdictFields(summary));

            // ---- 挂着看服务端之后还有没有动作
            if (parsed.StaySeconds > 0 && summary.Outcome != OnlineFightOutcome.Cancelled)
            {
                await StayAsync(clock, town, parsed.StaySeconds);
            }

            Console.WriteLine("PROBE_FIGHT result " + OnlineFightLog.ResultFields(summary));
            return OnlineFightExitCodes.ForOutcome(summary.Outcome);
        }
    }

    // 战斗结局出来之后继续挂 S 秒:心跳泵独占接收(沿用 orchestrated 的 --stay 办法),只报挂着期间新增的帧的类/号/长度序列与计数。
    // 事件表只在泵停了之后才读(泵运行时它在被写)。
    private static async Task StayAsync(Stopwatch clock, OnlineEntryResult town, int seconds)
    {
        GameClientSession session = town.Session;
        if (session.PeerClosed)
        {
            Say(clock, "stage=stay skipped=1 reason=PeerClosed");
            return;
        }
        int before = session.Events.Count;
        Say(clock, "stage=stay begin seconds=" + seconds.ToString(CultureInfo.InvariantCulture));
        var watch = Stopwatch.StartNew();
        string pumpEnd = "running";
        bool readable = false;
        using (var stop = new CancellationTokenSource())
        {
            Task<int> pump = OnlineHeartbeat.RunAsync(session, stop.Token);
            Task winner = await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(seconds)));
            if (winner != pump)
            {
                stop.Cancel();
                Task finished = await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(8)));
                if (finished != pump)
                {
                    town.Dispose();     // 关流让泵收尾
                    await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(5)));
                }
            }
            if (pump.IsCompleted)
            {
                readable = true;
                try
                {
                    int answered = await pump;
                    pumpEnd = "ended answered=" + answered.ToString(CultureInfo.InvariantCulture);
                }
                catch (OperationCanceledException)
                {
                    pumpEnd = "stopped";
                }
                catch (Exception e)
                {
                    pumpEnd = "faulted type=" + e.GetBaseException().GetType().Name;
                }
            }
        }

        if (!readable)
        {
            Say(clock, "stage=stay end elapsedSeconds=" + watch.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture) + " pump=stuck");
            return;
        }

        int inbound = 0;
        int outbound = 0;
        int heartbeats = 0;
        var frames = new StringBuilder();
        int listed = 0;
        int count = session.Events.Count;
        for (int i = before; i < count; i++)
        {
            SessionEvent e = session.Events[i];
            if (e == null)
            {
                continue;
            }
            if (e.Outbound)
            {
                outbound++;
                continue;
            }
            inbound++;
            if (e.PacketClass == PacketClass.Noti && e.PacketId == OnlineHeartbeat.HeartbeatPacketId)
            {
                heartbeats++;
            }
            if (listed < MaxStayFrames)
            {
                if (listed > 0)
                {
                    frames.Append(' ');
                }
                frames.Append(e.PacketClass == PacketClass.Cmd ? "CMD" : (e.PacketClass == PacketClass.Noti ? "NOTI" : "C" + ((int)e.PacketClass).ToString(CultureInfo.InvariantCulture)));
                frames.Append('/').Append(e.PacketId.ToString(CultureInfo.InvariantCulture));
                frames.Append('(').Append(e.PayloadLength.ToString(CultureInfo.InvariantCulture)).Append(')');
                listed++;
            }
        }
        if (inbound > listed)
        {
            frames.Append(" +").Append((inbound - listed).ToString(CultureInfo.InvariantCulture));
        }
        Say(clock, "stage=stay end elapsedSeconds=" + watch.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)
            + " inbound=" + inbound.ToString(CultureInfo.InvariantCulture)
            + " outbound=" + outbound.ToString(CultureInfo.InvariantCulture)
            + " heartbeatsSeen=" + heartbeats.ToString(CultureInfo.InvariantCulture)
            + " peerClosed=" + Flag(session.PeerClosed)
            + " pump=" + pumpEnd
            + " frames=" + (frames.Length == 0 ? "-" : frames.ToString()));
    }

    // 把 Net 层的日志行转成探针的线索行:只收 stage=dungeon、stage=combat、stage=fight 三类,其余一律不打;
    // 进副本的 begin 行带副本编号与模式(命令行参数的回显),不打。
    private static void SayNet(Stopwatch clock, string line)
    {
        const string head = "DNF_NET stage=";
        if (line == null || !line.StartsWith(head, StringComparison.Ordinal))
        {
            return;
        }
        string body = line.Substring(head.Length);
        if (!(body.StartsWith("dungeon ", StringComparison.Ordinal)
            || body.StartsWith("combat ", StringComparison.Ordinal)
            || body.StartsWith("fight ", StringComparison.Ordinal)))
        {
            return;
        }
        if (body.StartsWith("dungeon begin ", StringComparison.Ordinal))
        {
            return;
        }
        Say(clock, "stage=" + body);
    }

    private static int Abort(Stopwatch clock, string at, string reason, int exitCode, string detail)
    {
        string line = "PROBE_FIGHT abort at=" + at + " reason=" + reason + (detail == null ? string.Empty : " " + detail) + " exit=" + exitCode.ToString(CultureInfo.InvariantCulture);
        Console.WriteLine(line + " t=" + Seconds(clock));
        return exitCode;
    }

    private static void Say(Stopwatch clock, string text)
    {
        Console.WriteLine("PROBE_FIGHT " + text + " t=" + Seconds(clock));
    }

    private static string Seconds(Stopwatch clock)
    {
        return clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
    }

    private static string Flag(bool value)
    {
        return value ? "1" : "0";
    }
}
