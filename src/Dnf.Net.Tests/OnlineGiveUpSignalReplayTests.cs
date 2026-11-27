using System.IO.Compression;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Net.Tests.GiveUpSignalKit;

namespace Dnf.Net.Tests;

/// <summary>
/// r22-giveup-signal:把"真服放弃之后实际推来的那一串帧"(0/9、0/24、0/3、0/36、0/2,没有 0/23,其中三个裹在 561 信封里)原样重放,
/// 一层是真会话上的战斗跑器(A),一层是战斗驱动加 <c>--give-up-on-failure</c>(B)。数据全是合成的,不连真服。
/// 修复之前这串帧凑不成回城判据,跑器一路等到超时,驱动把"回城成功"误报成超时。
/// </summary>
public class OnlineGiveUpSignalReplayTests
{
    private const string P = "DNF_NET stage=combat";

    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(3);

    private sealed class Replay
    {
        public OnlineDungeonCombatResult Result;
        public DungeonEntryPipe Pipe;
        public HostTestKit.LogSink Log;
    }

    private static async Task<Replay> RunReplay(byte[][] window, double giveUpTimeoutSeconds = 8)
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = frame => frame.Class == PacketClass.Cmd && frame.Id == GiveUpKit.GiveUpId ? window : null;
        OnlineDungeonCombatOptions options = CombatKit.Options();
        options.GiveUpTimeout = TimeSpan.FromSeconds(giveUpTimeoutSeconds);
        var runner = new OnlineDungeonCombatRunner(session, OnlineDungeonRunner.UnwrapEnvelope, options);
        Assert.True(runner.PostGiveUp());
        var log = new HostTestKit.LogSink();
        OnlineDungeonCombatResult result = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(CombatKit.Sequential(2)), log.Add), 20);
        return new Replay { Result = result, Pipe = pipe, Log = log };
    }

    private static int GiveUpCommands(DungeonEntryPipe pipe)
    {
        return pipe.ClientFrames.Count(f => f.Class == PacketClass.Cmd && f.Id == GiveUpKit.GiveUpId);
    }

    private static string PassIds(OnlineDungeonCombatResult r)
    {
        return string.Join(",", r.Passthrough.Select(c => c.PacketClass.ToString().ToUpperInvariant() + "/" + c.PacketId + ":" + c.Count));
    }

    public static TheoryData<AreaUsersShape, bool> Variants()
    {
        var data = new TheoryData<AreaUsersShape, bool>();
        foreach (AreaUsersShape shape in new[] { AreaUsersShape.Unparseable, AreaUsersShape.Parseable, AreaUsersShape.ParseableTableSized })
        {
            data.Add(shape, false);
            data.Add(shape, true);
        }
        return data;
    }

    public static TheoryData<AreaUsersShape> Shapes()
    {
        return new TheoryData<AreaUsersShape>
        {
            AreaUsersShape.Unparseable,
            AreaUsersShape.Parseable,
            AreaUsersShape.ParseableTableSized,
        };
    }

    // ======== A:真会话上的跑器 ========

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task The_real_window_without_0_23_ends_as_gave_up_with_one_command_and_the_baseline_counts(AreaUsersShape shape)
    {
        Replay r = await RunReplay(RealWire(AreaUsers(shape)));
        OnlineDungeonCombatResult result = r.Result;

        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, result.Outcome);
        Assert.False(result.ReturnReady);
        Assert.Equal(1, GiveUpCommands(r.Pipe));
        Assert.Equal(1, r.Pipe.ClientFrames.Count(f => f.Class == PacketClass.Cmd));      // 放弃之后跑器没有再发任何命令

        // 窗口的增量就是绝对值(放弃之前什么都没来):9 帧、8 帧透传、拆开 2 个信封、2 个开不了、2 份内层副本被跳过
        Assert.Equal(9, result.FramesSeen);
        Assert.Equal(8, result.PassthroughCount);
        Assert.Equal(2, result.EnvelopesOpened);
        Assert.Equal(2, result.EnvelopesUnopened);
        Assert.Equal(2, result.InnerCopiesSkipped);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(0, result.Malformed);
        Assert.Equal(0, result.HeartbeatsAnswered);
        Assert.Equal("NOTI/2:1,NOTI/3:1,NOTI/9:1,NOTI/36:1,NOTI/561:4", PassIds(result));    // 0/24 不在透传表里,0/2 在

        // 没见过 0/23:用户区快照为 null;0/24 读得下来才有区内用户快照
        Assert.Null(result.UserArea);
        Assert.Equal(shape != AreaUsersShape.Unparseable, result.AreaUsers != null);

        // 日志(缺省):只有这四行(开始、放弃已发、回城、收场)
        string[] expected =
        {
            P + " begin monsters=2 duplicateNumbers=0",
            P + " event=GiveUpSent",
            P + " event=ReturnedToTown userArea=0 areaUsers=" + (shape == AreaUsersShape.Unparseable ? "-" : "1"),
            P + " ended outcome=GaveUp phase=Fighting monsters=2 sent=0 confirmed=0 refused=0 blocked=0 outOfOrder=0 duplicate=0 unknownNumber=0 malformed=0 " +
                "passthrough=8 heartbeats=0 envelopes=2/2 innerSkipped=2 frames=9 passIds=NOTI/2:1,NOTI/3:1,NOTI/9:1,NOTI/36:1,NOTI/561:4",
        };
        Assert.Equal(expected, r.Log.Lines);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task When_the_0_2_group_arrives_before_the_0_24_the_run_ends_on_the_0_24_frame(AreaUsersShape shape)
    {
        // 到达顺序无关:0/2 先到(它的信封与内层副本),随后 0/24 一到判据就成立,跑器在那一帧收场——后面的 0/3、0/36 组根本没处理
        Replay r = await RunReplay(RealWire(AreaUsers(shape), reverse2And24: true));
        OnlineDungeonCombatResult result = r.Result;

        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, result.Outcome);
        Assert.Equal(1, GiveUpCommands(r.Pipe));
        Assert.Equal(5, result.FramesSeen);                 // 0/9 的信封与副本、0/2 的信封与副本、普通 0/24
        Assert.Equal(4, result.PassthroughCount);           // 两个信封帧、两份副本(0/9、0/2)
        Assert.Equal(0, result.EnvelopesOpened);
        Assert.Equal(2, result.EnvelopesUnopened);
        Assert.Equal(0, result.InnerCopiesSkipped);         // 压缩内层开不了,副本当普通帧处理,不是被跳过
        Assert.Equal("NOTI/2:1,NOTI/9:1,NOTI/561:2", PassIds(result));
        Assert.Null(result.UserArea);
        Assert.Equal(shape != AreaUsersShape.Unparseable, result.AreaUsers != null);
        Assert.Equal(P + " ended outcome=GaveUp phase=Fighting monsters=2 sent=0 confirmed=0 refused=0 blocked=0 outOfOrder=0 duplicate=0 unknownNumber=0 malformed=0 " +
            "passthrough=4 heartbeats=0 envelopes=0/2 innerSkipped=0 frames=5 passIds=NOTI/2:1,NOTI/9:1,NOTI/561:2", r.Log.Lines.Last());
    }

    [Fact]
    public async Task The_replay_with_a_25_byte_unparseable_0_24_through_the_fake_session_also_gives_up()
    {
        // 25 字节读不下来的 0/24 只能直接喂(真会话会把它补零到块长的整数倍,补完就读得下来了):假会话没有补零
        var session = new CombatFakeSession();
        session.Respond = (cls, id, payload) =>
        {
            if (cls == PacketClass.Cmd && id == GiveUpKit.GiveUpId)
            {
                PushFake(session, Unparseable25());
            }
            return Array.Empty<CombatKit.ServerFrame>();
        };
        OnlineDungeonCombatOptions options = CombatKit.Options();
        options.GiveUpTimeout = TimeSpan.FromSeconds(8);
        var runner = new OnlineDungeonCombatRunner(session, OnlineDungeonRunner.UnwrapEnvelope, options);
        Assert.True(runner.PostGiveUp());

        OnlineDungeonCombatResult result = await GatewayTestSupport.Bounded(runner.RunAsync(CombatKit.Map(CombatKit.Sequential(2)), null), 20);

        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, result.Outcome);
        Assert.Equal(1, session.Commands(GiveUpKit.GiveUpId));
        Assert.Equal(9, result.FramesSeen);
        Assert.Equal(8, result.PassthroughCount);
        Assert.Equal(2, result.EnvelopesOpened);
        Assert.Equal(2, result.EnvelopesUnopened);
        Assert.Equal(2, result.InnerCopiesSkipped);
        Assert.Null(result.UserArea);
        Assert.Null(result.AreaUsers);
        Assert.Equal(0, result.Malformed);
    }

    [Fact]
    public async Task Without_the_0_24_the_other_four_groups_are_not_enough_and_the_run_times_out()
    {
        Replay r = await RunReplay(RealWire(AreaUsers(AreaUsersShape.Parseable), without24: true), giveUpTimeoutSeconds: 1.2);
        OnlineDungeonCombatResult result = r.Result;

        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, result.Outcome);
        Assert.Equal(1, GiveUpCommands(r.Pipe));
        Assert.Equal(8, result.FramesSeen);                 // 0/9、0/3、0/36、0/2 各带一份内层副本
        Assert.Equal(8, result.PassthroughCount);
        Assert.Equal(2, result.EnvelopesOpened);
        Assert.Equal(2, result.EnvelopesUnopened);
        Assert.Equal(2, result.InnerCopiesSkipped);
        Assert.Equal("NOTI/2:1,NOTI/3:1,NOTI/9:1,NOTI/36:1,NOTI/561:4", PassIds(result));
        Assert.Null(result.UserArea);
        Assert.Null(result.AreaUsers);
        Assert.DoesNotContain(r.Log.Lines, l => l.Contains("ReturnedToTown", StringComparison.Ordinal));
        Assert.StartsWith(P + " ended outcome=Timeout ", r.Log.Lines.Last());
    }

    [Fact]
    public async Task Without_the_0_2_the_0_24_and_the_three_others_are_not_enough_and_the_run_times_out()
    {
        Replay r = await RunReplay(RealWire(AreaUsers(AreaUsersShape.Parseable), without2: true), giveUpTimeoutSeconds: 1.2);
        OnlineDungeonCombatResult result = r.Result;

        Assert.Equal(OnlineDungeonCombatOutcome.Timeout, result.Outcome);
        Assert.Equal(1, GiveUpCommands(r.Pipe));
        Assert.Equal(7, result.FramesSeen);
        Assert.Equal(6, result.PassthroughCount);
        Assert.Equal(2, result.EnvelopesOpened);
        Assert.Equal(1, result.EnvelopesUnopened);
        Assert.Equal(2, result.InnerCopiesSkipped);
        Assert.Equal("NOTI/3:1,NOTI/9:1,NOTI/36:1,NOTI/561:3", PassIds(result));
        Assert.DoesNotContain(r.Log.Lines, l => l.Contains("ReturnedToTown", StringComparison.Ordinal));
        Assert.StartsWith(P + " ended outcome=Timeout ", r.Log.Lines.Last());
    }

    // ======== B:战斗驱动加 --give-up-on-failure ========

    private static OnlineFightArguments ParseWithSwitch()
    {
        Assert.True(OnlineFightArguments.TryParse(new[] { "--dungeon", "901", "0", "--give-up-on-failure" }, out OnlineFightArguments parsed, out _));
        return parsed;
    }

    private static GiveUpRig RigAnsweringWith(byte[] payload24, bool reverse2And24 = false, bool without24 = false, bool without2 = false, TimeSpan? wait = null)
    {
        GiveUpRig rig = GiveUpRig.ForNoClearNotice(giveUp: true, giveUpWait: wait, unwrap: OnlineDungeonRunner.UnwrapEnvelope);
        rig.OnGiveUp = () =>
        {
            PushFake(rig.Session, payload24, reverse2And24, without24, without2);
            return Array.Empty<CombatKit.ServerFrame>();
        };
        return rig;
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task The_driver_reports_returned_for_the_real_window_and_the_exit_code_is_the_one_it_has_without_the_switch(AreaUsersShape shape, bool reverse2And24)
    {
        GiveUpRig rig = RigAnsweringWith(shape == AreaUsersShape.Unparseable ? Unparseable25() : AreaUsers(shape), reverse2And24);
        Task<OnlineFightSummary> run = await rig.Drive(OnlineFightOutcome.NoClearNotice);

        OnlineFightSummary s = await rig.Done(run);

        Assert.NotNull(s.GiveUp);
        Assert.Equal(OnlineGiveUpOutcome.Returned, s.GiveUp.Outcome);
        Assert.True(s.GiveUp.ReturnedToTown);
        Assert.True(s.GiveUp.Sent);
        Assert.Equal(OnlineDungeonCombatOutcome.GaveUp, s.RunnerOutcome);
        Assert.Equal(1, rig.Session.Commands(45));
        Assert.Equal(OnlineFightOutcome.NoClearNotice, s.Outcome);
        Assert.Equal(OnlineFightLog.ResultFields(s) + " giveUp=returned", OnlineFightLog.ResultFieldsFor(ParseWithSwitch(), s, null, s.GiveUp));
        Assert.Equal(new[] { OnlineFightLog.GiveUpStep("sent", "NoClearNotice"), OnlineFightLog.GiveUpStep("returned", "NoClearNotice") },
            rig.Log.Lines.Where(l => l.StartsWith(OnlineFightLog.GiveUpPrefix, StringComparison.Ordinal)).ToArray());

        // 同一场失败不开开关:收场同样是 NoClearNotice,退出码一样(放弃只是附加的善后,不改退出码)
        GiveUpRig off = GiveUpRig.ForNoClearNotice(giveUp: false, unwrap: OnlineDungeonRunner.UnwrapEnvelope);
        OnlineFightSummary withoutSwitch = await off.Done(await off.Drive(OnlineFightOutcome.NoClearNotice));
        Assert.Null(withoutSwitch.GiveUp);
        Assert.Equal(OnlineFightOutcome.NoClearNotice, withoutSwitch.Outcome);
        Assert.Equal(OnlineFightExitCodes.ForOutcome(withoutSwitch.Outcome), OnlineFightExitCodes.ForOutcome(s.Outcome));
        Assert.Equal(11, OnlineFightExitCodes.ForOutcome(s.Outcome));
    }

    [Fact]
    public async Task The_driver_still_reports_timeout_when_the_0_24_is_missing_from_the_window()
    {
        GiveUpRig rig = RigAnsweringWith(AreaUsers(AreaUsersShape.Parseable), without24: true, wait: ShortWait);
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(OnlineFightOutcome.NoClearNotice);
        await CombatKit.WaitUntil(() => rig.Machine.PassthroughCount >= 8, "另外四组(0/9、0/3、0/36、0/2)都已经进了状态机");
        await rig.WaitParked();
        Assert.False(run.IsCompleted);
        Assert.False(rig.Machine.ReturnedToTown);

        rig.Clock.Advance(ShortWait);
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineGiveUpOutcome.Timeout, s.GiveUp.Outcome);
        Assert.True(s.GiveUp.Sent);
        Assert.False(s.GiveUp.ReturnedToTown);
        Assert.Equal(1, rig.Session.Commands(45));
        Assert.Equal(OnlineFightOutcome.NoClearNotice, s.Outcome);
        Assert.Equal(11, OnlineFightExitCodes.ForOutcome(s.Outcome));
        Assert.EndsWith(" giveUp=timeout", OnlineFightLog.ResultFieldsFor(ParseWithSwitch(), s, null, s.GiveUp));
    }

    [Fact]
    public async Task The_driver_still_reports_timeout_when_the_0_2_is_missing_from_the_window()
    {
        GiveUpRig rig = RigAnsweringWith(AreaUsers(AreaUsersShape.Parseable), without2: true, wait: ShortWait);
        Task<OnlineFightSummary> run = await rig.DriveToGiveUpWait(OnlineFightOutcome.NoClearNotice);
        await CombatKit.WaitUntil(() => rig.Machine.PassthroughCount >= 6, "0/24 与另外三组都已经进了状态机");
        await rig.WaitParked();
        Assert.False(run.IsCompleted);
        Assert.False(rig.Machine.ReturnedToTown);

        rig.Clock.Advance(ShortWait);
        OnlineFightSummary s = await rig.Done(run);

        Assert.Equal(OnlineGiveUpOutcome.Timeout, s.GiveUp.Outcome);
        Assert.EndsWith(" giveUp=timeout", OnlineFightLog.ResultFieldsFor(ParseWithSwitch(), s, null, s.GiveUp));
        Assert.Equal(1, rig.Session.Commands(45));
    }
}

/// <summary>重放夹具自己的自检:拼出来的帧长与载荷形状必须与它声称的一致,否则上面的重放就失去意义。</summary>
public class GiveUpSignalKitTests
{
    [Fact]
    public void The_stored_zlib_streams_inflate_back_to_the_plain_bytes_and_are_eleven_bytes_longer()
    {
        foreach (byte[] plain in new[] { Plain9(), Plain2(), new byte[0], GatewayTestSupport.Pattern(300, 5, 7) })
        {
            byte[] stream = ZlibStored(plain);
            Assert.Equal(plain.Length + 11, stream.Length);
            using var input = new MemoryStream(stream);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            Assert.Equal(plain, output.ToArray());
        }
    }

    [Fact]
    public void The_inner_frames_have_the_totals_of_the_window_and_the_three_variants_of_0_24_have_the_promised_shapes()
    {
        Assert.Equal(Total9, DungeonEntryWire.Inner(0, 9, ZlibStored(Plain9())).Length);
        Assert.Equal(Total3, DungeonEntryWire.Inner(0, 3, Payload3()).Length);
        Assert.Equal(Total36, DungeonEntryWire.Inner(0, 36, Payload36()).Length);
        Assert.Equal(Total2, DungeonEntryWire.Inner(0, 2, ZlibStored(Plain2())).Length);

        Assert.False(TownAreaPayloads.TryParseAreaUsers(AreaUsers(AreaUsersShape.Unparseable), out _));
        Assert.False(TownAreaPayloads.TryParseAreaUsers(Unparseable25(), out _));
        Assert.True(TownAreaPayloads.TryParseAreaUsers(AreaUsers(AreaUsersShape.Parseable), out AreaUsersNotice one));
        Assert.Single(one.Entries);
        Assert.True(TownAreaPayloads.TryParseAreaUsers(AreaUsers(AreaUsersShape.ParseableTableSized), out AreaUsersNotice sized));
        Assert.Single(sized.Entries);
        Assert.Equal(Total24, DungeonEntryWire.Notice(24, AreaUsers(AreaUsersShape.ParseableTableSized)).Length);
        Assert.Equal(25, Unparseable25().Length);
    }
}
