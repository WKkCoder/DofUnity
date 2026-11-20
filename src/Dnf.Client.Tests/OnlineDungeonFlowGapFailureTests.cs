using System.Diagnostics;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;
using static Dnf.Client.Tests.OnlineDungeonFlowGapKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网进副本控制器"进入阶段各种失败出口"的缺口补测:连接断开/切帧出错算断线、取消与异常给出不同的提示、
/// 进入成功后立刻被取消就不能起战斗、建房失败时战斗任务已经结束的收尾节奏、以及退出时对后台任务的有限等待。
/// 全部用合成数据与接缝会话;时间只走场景台的逻辑时钟(只有"退出等待"那两条是在量一个真实的阻塞时长,理由写在各自的注释里)。
/// </summary>
public sealed class OnlineDungeonFlowGapFailureTests
{
    private const string P = "DNF_ONLINE stage=dungeon ";

    private static string[] Reasons(GameFlow flow) => flow.History.Select(h => h.Reason).ToArray();

    // ------------------------------------------------------------------------------------------------
    // 进入阶段的失败分类
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护"进入时连接断开或字节流切帧出错 = 断线,而不是普通失败":这两种结局走断线出口(留下离开与失败两行日志、触发断线回调、
    /// 不把选择屏重开成"进入失败");只有被拒、超时、取消等才走普通失败出口。
    /// </summary>
    [Theory]
    [InlineData(true, "ConnectionClosed")]
    [InlineData(false, "FramingFault")]
    public void A_connection_that_closes_or_mis_frames_during_entry_ends_as_a_disconnect_not_an_ordinary_failure(bool peerClosed, string failure)
    {
        using var rig = new Rig();
        rig.Port.SetResponder((cls, id, _) =>
        {
            if (cls == PacketClass.Cmd && id == OnlineDungeonEntry.StartGamePacketId)
            {
                // "开始游戏"发出去之后:对端关了(或入站字节流切帧出错),等 27 时读不到任何帧
                if (peerClosed)
                {
                    rig.Port.CurrentNet.ClosePeer();
                }
                else
                {
                    rig.Port.CurrentNet.FaultFraming();
                }
            }
            return Array.Empty<CombatKit.ServerFrame>();
        });
        rig.Start();
        DungeonSelectScreen screen = rig.Flow.Select;

        rig.Flow.OnlineStartDungeon(rig.Entries[0], 1);
        rig.PumpUntilIdle();

        // 副本 A 档位 3 → 上限 3;请求难度 1 原样进入;失败发生在等 27 的阶段;断线出口:离开(原因 Disconnected、无击杀、1 间房)+ 失败(会话)
        Assert.Equal(
            new[]
            {
                P + "event=select outcome=enter difficulty=1 max=3",
                P + "event=enter areakind=unknown entrance=true difficulty=1",
                P + "event=fail reason=" + failure + " step=WaitLoadingNotice",
                P + "event=leave reason=Disconnected reported=0 dropped=0 rooms=1",
                P + "event=fail reason=Disconnected step=Session",
            },
            rig.Logs());
        Assert.Equal(1, rig.Disconnects);                                        // 断线回调触发一次
        Assert.Equal(1, rig.Port.EnterExclusiveCalls);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);                           // 回调之前已经放回收包权
        Assert.DoesNotContain("online enter failed", Reasons(rig.Flow));         // 没有走"进入失败"重开选择屏
        Assert.Same(screen, rig.Flow.Select);
    }

    /// <summary>
    /// 保护"取消与异常是两种提示":进入被取消(退出时中止)→ 选择屏上显示"失败"提示;进入任务因异常失败 → 显示"断线"提示;
    /// 两者日志的原因不同(Cancelled / 异常类型名),且都走普通失败出口("online enter failed"),不触发断线回调。
    /// </summary>
    [Fact]
    public void A_cancelled_entry_reports_a_failed_notice_while_a_faulted_entry_reports_a_disconnect_notice()
    {
        // ---- 取消:等 27 的时候被 PrepareForQuit 中止
        using (var cancelled = new Rig(o => o.LoadingNoticeTimeout = TimeSpan.FromSeconds(30)))
        {
            cancelled.Start();
            cancelled.Flow.OnlineStartDungeon(cancelled.Entries[0], 0);
            cancelled.WaitForCommand(OnlineDungeonEntry.StartGamePacketId);

            cancelled.Ctrl.PrepareForQuit();
            cancelled.PumpUntilIdle();

            Assert.Equal(
                new[]
                {
                    P + "event=select outcome=enter difficulty=0 max=3",
                    P + "event=enter areakind=unknown entrance=true difficulty=0",
                    P + "event=fail reason=Cancelled step=Enter",
                },
                cancelled.Logs());
            Assert.Equal(DungeonSelectNoticeKind.Failed, cancelled.Flow.Select.Notice.Kind);
            Assert.Equal("online enter failed", cancelled.Flow.History[cancelled.Flow.History.Count - 1].Reason);
            Assert.Equal(0, cancelled.Disconnects);
            Assert.Equal(1, cancelled.Port.LeaveExclusiveCalls);
        }

        // ---- 异常:进入独占收包时就抛
        using (var faulted = new Rig())
        {
            faulted.Port.EnterExclusiveBehavior = _ => Task.FromException(new InvalidOperationException("synthetic"));
            faulted.Start();

            faulted.Flow.OnlineStartDungeon(faulted.Entries[0], 0);
            faulted.PumpUntilIdle();

            Assert.Equal(
                new[]
                {
                    P + "event=select outcome=enter difficulty=0 max=3",
                    P + "event=enter areakind=unknown entrance=true difficulty=0",
                    P + "event=fail reason=InvalidOperationException step=Enter",
                },
                faulted.Logs());
            Assert.Equal(DungeonSelectNoticeKind.Disconnected, faulted.Flow.Select.Notice.Kind);
            Assert.Equal("online enter failed", faulted.Flow.History[faulted.Flow.History.Count - 1].Reason);
            Assert.Equal(0, faulted.Disconnects);
            Assert.Equal(0, faulted.Port.CreateRunnerCalls);                      // 没走到造跑器
        }
    }

    /// <summary>
    /// 保护"进入成功的那一刻若已被取消,就绝不起战斗":在进入成功的日志行里触发退出(令牌随即取消),
    /// 战斗跑器没起 → 战斗状态机不存在 → 控制器不调建房工厂,走"战斗未就绪"的建房失败出口(提示"房间失败"、原因写"online room build failed")。
    /// </summary>
    [Fact]
    public void A_cancel_that_lands_right_after_a_successful_entry_never_starts_combat_and_reports_a_room_failure()
    {
        var holder = new Rig[1];
        using var rig = new Rig(o =>
        {
            o.QuitWaitTimeoutMs = 50;
            o.NetLog = line =>
            {
                if (line.Contains(" done step=Done", StringComparison.Ordinal))
                {
                    holder[0].Ctrl.PrepareForQuit();   // 进入刚成功、还没起战斗:令牌取消
                }
            };
        });
        holder[0] = rig;
        rig.Port.SetResponder((cls, id, _) => EntryReply(cls, id) ?? Array.Empty<CombatKit.ServerFrame>());
        rig.Start();

        rig.Flow.OnlineStartDungeon(rig.Entries[0], 1);
        rig.PumpUntilIdle();

        Assert.Equal(0, rig.BuilderCalls);                                       // 战斗没起,建房工厂一次都没调
        Assert.Equal(
            new[]
            {
                P + "event=select outcome=enter difficulty=1 max=3",
                P + "event=enter areakind=unknown entrance=true difficulty=1",
                P + "event=fail reason=CombatNotReady step=Machine",
            },
            rig.Logs());
        Assert.Equal(DungeonSelectNoticeKind.RoomFailed, rig.Flow.Select.Notice.Kind);
        Assert.Equal("online room build failed", rig.Flow.History[rig.Flow.History.Count - 1].Reason);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(0, rig.Disconnects);
    }

    /// <summary>
    /// 保护"建房失败时战斗任务若已经结束,同一次轮询就直接去放权":服务端在起始地图之后立刻关了连接,战斗一开始就收场;
    /// 建房失败的那一次轮询之后,控制器应已进入"放开独占收包"阶段(而不是还要多等一圈的"先停战斗"阶段)。
    /// </summary>
    [Fact]
    public void A_room_build_failure_after_the_combat_already_ended_goes_straight_to_releasing_the_exclusive_receive()
    {
        using var rig = new Rig();
        rig.Port.SetResponder((cls, id, _) =>
        {
            CombatKit.ServerFrame[] reply = EntryReply(cls, id);
            if (reply != null && id == OnlineDungeonEntry.SelectDungeonPacketId)
            {
                rig.Port.CurrentNet.ClosePeer();   // 28 与 29 照常回,随后对端关闭:战斗第一圈就读到"断了"
            }
            return reply ?? Array.Empty<CombatKit.ServerFrame>();
        });
        rig.Start();

        rig.Flow.OnlineStartDungeon(rig.Entries[0], 0);
        rig.Poll();   // 进入阶段的结果出来:建房失败(缺省工厂返回 NoRoom)

        Assert.Equal(OnlineDungeonFlowPhase.ReleasingExclusive, rig.Ctrl.Phase);
        Assert.Equal(1, rig.BuilderCalls);

        rig.PumpUntilIdle();

        Assert.Equal(
            new[]
            {
                P + "event=select outcome=enter difficulty=0 max=3",
                P + "event=enter areakind=unknown entrance=true difficulty=0",
                P + "event=room status=NoRoom multi=off spawned=0 expected=0 rooms=1",
                P + "event=fail reason=RoomBuildFailed step=NoRoom",
            },
            rig.Logs());
        Assert.Equal(DungeonSelectNoticeKind.RoomFailed, rig.Flow.Select.Notice.Kind);
        Assert.Equal("online room build failed", rig.Flow.History[rig.Flow.History.Count - 1].Reason);
        Assert.Equal(1, rig.Port.LeaveExclusiveCalls);
        Assert.Equal(0, rig.Disconnects);
    }

    // ------------------------------------------------------------------------------------------------
    // 退出时对后台任务的有限等待
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护"退出会等后台任务收敛":进入任务在取消之后还要收尾一小会儿(这里用一段真实的短延时模拟"收尾慢",不是拿它排顺序);
    /// 退出(上限 5 秒)必须等到它收尾完才返回,而不是取消后立刻回来。
    /// </summary>
    [Fact]
    public void Quitting_waits_for_a_pending_entry_task_that_is_slow_to_wind_down()
    {
        using var windDownDone = new ManualResetEventSlim(false);
        using var rig = new Rig(o => o.QuitWaitTimeoutMs = 5000);
        rig.Port.EnterExclusiveBehavior = async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(100);        // 取消之后的收尾
                windDownDone.Set();
                throw;
            }
        };
        rig.Start();
        rig.Flow.OnlineStartDungeon(rig.Entries[0], 0);   // 进入任务挂在"进入独占收包"上

        rig.Ctrl.PrepareForQuit();

        Assert.True(windDownDone.IsSet);   // 退出返回时,后台任务已经收尾完
    }

    /// <summary>
    /// 保护"1 毫秒的退出等待也是真的在等":有任务挂着且不响应取消时,每次退出都至少阻塞约 1 毫秒(等待上限用完才返回)。
    /// 单次 1 毫秒量不出来,所以连着退出 100 次、量总阻塞时长:有等待时 ≥ 约 100 毫秒,没有等待时只有几毫秒(阈值取 40 毫秒,两头都留足余量)。
    /// </summary>
    [Fact]
    public void A_one_millisecond_quit_wait_still_blocks_while_a_task_ignores_the_cancellation()
    {
        var neverEnds = new TaskCompletionSource<bool>();
        using var rig = new Rig(o => o.QuitWaitTimeoutMs = 1);
        rig.Port.EnterExclusiveBehavior = _ => neverEnds.Task;   // 不响应取消,一直挂着
        rig.Start();
        rig.Flow.OnlineStartDungeon(rig.Entries[0], 0);
        rig.Ctrl.PrepareForQuit();   // 预热:第一次含取消与 JIT

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            rig.Ctrl.PrepareForQuit();
        }
        watch.Stop();
        neverEnds.SetResult(true);   // 放它走,让场景台能干净收尾

        Assert.True(watch.ElapsedMilliseconds >= 40, "100 次 1 毫秒的退出等待应当累计阻塞 ≥ 40 毫秒,实际 " + watch.ElapsedMilliseconds + " 毫秒");
    }
}
