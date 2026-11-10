using System.Globalization;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 联网宿主日志行的拼法(<see cref="OnlineNetLog"/>)。行的样子是给人与脚本看的约定(主 agent 真服实测时按它找问题),
/// 所以这里逐字断言;同时钉住"只有阶段名、原因名、包类包号、长度与计数"这条脱敏纪律。全是合成值。
/// </summary>
public class OnlineNetLogTests
{
    private const string Prefix = "DNF_NET stage=dungeon";

    private static DungeonInfoNotice Info()
    {
        Assert.True(DungeonInfoParser.TryParse(DungeonEntryWire.Info28(), out DungeonInfoNotice info));
        return info;
    }

    private static StartMapNotice Map()
    {
        Assert.True(StartMapParser.TryParse(DungeonEntryWire.Map29(), out StartMapNotice map));
        return map;
    }

    private static OnlineDungeonFrame F(OnlineDungeonStage stage, PacketClass cls, int id, int length, bool inner, OnlineDungeonFrameNote note)
    {
        return new OnlineDungeonFrame(stage, cls, id, length, inner, note);
    }

    // ======== 进城快照行 ========

    [Fact]
    public void The_snapshot_line_shows_every_field_the_backfill_can_fill()
    {
        var snapshot = new TownEntrySnapshot(1, true, 62, 51, 6, 13, 45, 4, 3, 1);

        Assert.Equal("DNF_NET snapshot slot=1 level=62 job=51 growType=6 enterComplete=1 seen=4 parsed=3 malformed=1", OnlineNetLog.Snapshot(snapshot));
    }

    [Fact]
    public void Missing_snapshot_values_are_dashes_and_a_missing_snapshot_is_none()
    {
        Assert.Equal("DNF_NET snapshot slot=- level=- job=- growType=- enterComplete=0 seen=0 parsed=0 malformed=0", OnlineNetLog.Snapshot(TownEntrySnapshot.Empty));
        Assert.Equal("DNF_NET snapshot none", OnlineNetLog.Snapshot(null));
    }

    /// <summary>快照行里没有时间同步的时分、也没有任何角色名一类的内容(快照本来就没有这些字段):只有槽、等级、职业、成长类型与计数。</summary>
    [Fact]
    public void The_snapshot_line_has_only_the_documented_keys()
    {
        string line = OnlineNetLog.Snapshot(new TownEntrySnapshot(0, true, 61, 50, 5, 13, 45, 4, 3, 0));

        string[] keys = line.Split(' ').Skip(2).Select(kv => kv.Split('=')[0]).ToArray();
        Assert.Equal(new[] { "slot", "level", "job", "growType", "enterComplete", "seen", "parsed", "malformed" }, keys);
    }

    // ======== 进副本各行 ========

    [Fact]
    public void The_begin_stage_and_frame_lines_have_their_fixed_shape()
    {
        Assert.Equal(Prefix + " begin dungeon=901 mode=3", OnlineNetLog.DungeonBegin(901, 3));
        Assert.Equal(Prefix + " step=WaitStartMap", OnlineNetLog.DungeonStep(OnlineDungeonStage.WaitStartMap));
        Assert.Equal(
            Prefix + " frame n=2 step=WaitStartMap NOTI/29(12)@561:Accepted",
            OnlineNetLog.DungeonFrame(2, F(OnlineDungeonStage.WaitStartMap, PacketClass.Noti, 29, 12, true, OnlineDungeonFrameNote.Accepted)));
        Assert.Equal(
            Prefix + " frame n=0 step=WaitLoadingNotice CMD/15(8):Acknowledged",
            OnlineNetLog.DungeonFrame(0, F(OnlineDungeonStage.WaitLoadingNotice, PacketClass.Cmd, 15, 8, false, OnlineDungeonFrameNote.Acknowledged)));
        Assert.Equal(Prefix + " frames omitted=5", OnlineNetLog.DungeonFramesOmitted(5));
    }

    [Fact]
    public void The_done_line_counts_frames_and_marks_which_notices_arrived()
    {
        OnlineDungeonFrame[] frames =
        {
            F(OnlineDungeonStage.WaitLoadingNotice, PacketClass.Noti, 561, 33, false, OnlineDungeonFrameNote.EnvelopeOpened),
            F(OnlineDungeonStage.WaitLoadingNotice, PacketClass.Noti, 27, 48, true, OnlineDungeonFrameNote.Accepted),
            F(OnlineDungeonStage.WaitDungeonInfo, PacketClass.Noti, 27, 48, false, OnlineDungeonFrameNote.Duplicate),
            F(OnlineDungeonStage.WaitDungeonInfo, PacketClass.Noti, 29, 12, false, OnlineDungeonFrameNote.OutOfOrder),
            F(OnlineDungeonStage.WaitDungeonInfo, PacketClass.Noti, 561, 33, false, OnlineDungeonFrameNote.EnvelopeUnopened),
            F(OnlineDungeonStage.WaitDungeonInfo, PacketClass.Noti, 561, 33, false, OnlineDungeonFrameNote.EnvelopeFaulted),
        };
        var result = new OnlineDungeonResult(OnlineDungeonStage.Done, OnlineDungeonFailure.None, null, null, null, new byte[48], Info(), Map(), frames);

        Assert.Equal(
            Prefix + " done step=Done frames=6 unopened=2 outOfOrder=1 duplicate=1 loading27=48 info28=1 map29=1",
            OnlineNetLog.DungeonDone(result));
    }

    [Fact]
    public void The_failed_line_carries_the_step_the_reason_and_the_send_or_rejection_details()
    {
        var timeout = new OnlineDungeonResult(OnlineDungeonStage.WaitStartMap, OnlineDungeonFailure.Timeout, null, null, null,
            new byte[48], Info(), null, new OnlineDungeonFrame[0]);
        Assert.Equal(
            Prefix + " failed step=WaitStartMap failure=Timeout send=- rejected=- code=- frames=0 unopened=0 outOfOrder=0 duplicate=0 loading27=48 info28=1 map29=0",
            OnlineNetLog.DungeonFailed(timeout));

        var rejected = new OnlineDungeonResult(OnlineDungeonStage.WaitDungeonInfo, OnlineDungeonFailure.Rejected, null, 16, 9,
            new byte[48], null, null, new[] { F(OnlineDungeonStage.WaitDungeonInfo, PacketClass.Cmd, 16, 8, false, OnlineDungeonFrameNote.Rejected) });
        Assert.Equal(
            Prefix + " failed step=WaitDungeonInfo failure=Rejected send=- rejected=16 code=9 frames=1 unopened=0 outOfOrder=0 duplicate=0 loading27=48 info28=0 map29=0",
            OnlineNetLog.DungeonFailed(rejected));

        var sendFailed = new OnlineDungeonResult(OnlineDungeonStage.SendSelectDungeon, OnlineDungeonFailure.SendFailed, FrameStatus.FrameTooLarge, null, null,
            new byte[48], null, null, new OnlineDungeonFrame[0]);
        Assert.Equal(
            Prefix + " failed step=SendSelectDungeon failure=SendFailed send=FrameTooLarge rejected=- code=- frames=0 unopened=0 outOfOrder=0 duplicate=0 loading27=48 info28=0 map29=0",
            OnlineNetLog.DungeonFailed(sendFailed));
    }

    [Fact]
    public void Exception_keepalive_and_heartbeat_lines_name_only_the_type()
    {
        Assert.Equal(Prefix + " failed exception=InvalidOperationException", OnlineNetLog.DungeonException(new InvalidOperationException("SECRET-905")));
        Assert.Equal(Prefix + " keepalive=start", OnlineNetLog.DungeonKeepAlive("start", null));
        Assert.Equal(Prefix + " keepalive=skip reason=ConnectionClosed", OnlineNetLog.DungeonKeepAlive("skip", "ConnectionClosed"));
        Assert.Equal("DNF_NET heartbeat answered=3", OnlineNetLog.HeartbeatAnswered(3));
        Assert.Equal("DNF_NET heartbeat ended=IOException", OnlineNetLog.HeartbeatEnded(new IOException("SECRET-905")));
        // 包在聚合异常里时报最里面那个(与宿主原来的保活日志同一取法)
        Assert.Equal("DNF_NET heartbeat ended=IOException", OnlineNetLog.HeartbeatEnded(new AggregateException(new IOException("SECRET-905"))));
        Assert.DoesNotContain("SECRET", OnlineNetLog.DungeonException(new InvalidOperationException("SECRET-905")));
    }

    /// <summary>数字一律按固定文化写:换成负号不是 ASCII 减号的文化,负的副本号照样写成 -1。</summary>
    [Fact]
    public void Numbers_are_written_with_the_invariant_culture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            Assert.Equal(Prefix + " begin dungeon=-1 mode=-1", OnlineNetLog.DungeonBegin(-1, -1));
            Assert.Equal("DNF_NET heartbeat answered=1234567", OnlineNetLog.HeartbeatAnswered(1234567));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
