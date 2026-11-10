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

    // ======== 进城会话事件汇总行 ========

    private static SessionEvent In(PacketClass cls, int id, string note)
    {
        return new SessionEvent(false, cls, id, 40, 20, note);
    }

    /// <summary>
    /// 汇总行逐项数:信封 = 拆开的 + 没拆开的 + 内层不成帧的;内层包号去重升序;角色列表应答(类 0、包号 2)分直接来的与信封内层带的;
    /// 发出去的事件、别的类的包号 2 不计。
    /// </summary>
    [Fact]
    public void The_session_digest_counts_envelopes_inner_ids_and_list_notices()
    {
        var events = new List<SessionEvent>
        {
            new SessionEvent(true, PacketClass.Cmd, 1, 40, 20, string.Empty),                  // 发出去的:不计
            new SessionEvent(true, PacketClass.Noti, 2, 40, 20, string.Empty),                 // 发出去的包号 2:不计
            In(PacketClass.Cmd, 1, "Session"),
            In(PacketClass.Noti, 561, "Plain"),                                                // 信封帧本身:只算收到
            In(PacketClass.Noti, 2, "信封内层"),                                               // 列表应答,封在信封里
            In(PacketClass.Noti, 561, "Plain"),
            In(PacketClass.Noti, 124, "信封内层"),
            In(PacketClass.Noti, 561, "Plain"),
            In(PacketClass.Noti, 124, "信封内层"),                                             // 重复的内层包号只列一次
            In(PacketClass.Noti, 561, "Plain"),
            In(PacketClass.Noti, 561, "信封拆包失败:CrcMismatch"),                            // 没拆开
            In(PacketClass.Noti, 561, "Plain"),
            In(PacketClass.Noti, 561, "信封内层(帧头不足,未展开)"),                            // 拆开了,内层不成帧
            In(PacketClass.Noti, 2, "Plain,压缩"),                                            // 直接来的列表应答
            In(PacketClass.Cmd, 2, "Session"),                                                 // 类 1 的包号 2:不是列表应答
        };

        Assert.Equal(
            "DNF_NET session events=15 received=13 envelopes=5 opened=3 unwrapFailed=1 unwrapReasons=CrcMismatch:1 innerShort=1 innerIds=2,124 id2Direct=1 id2Inner=1",
            OnlineNetLog.SessionDigest(events));
    }

    [Fact]
    public void The_session_digest_of_nothing_is_stable()
    {
        Assert.Equal(
            "DNF_NET session events=0 received=0 envelopes=0 opened=0 unwrapFailed=0 unwrapReasons=- innerShort=0 innerIds=- id2Direct=0 id2Inner=0",
            OnlineNetLog.SessionDigest(new List<SessionEvent>()));
        Assert.Equal("DNF_NET session none", OnlineNetLog.SessionDigest(null));
    }

    /// <summary>
    /// 拆包失败按原因分:原因是 <see cref="AntibotEnvelopeStatus"/> 的枚举名,升序排、各记次数;
    /// 备注里不是枚举名的文字(没有冒号、自由文本、数字写法、未定义的数值)一律记 Unknown,绝不把别的文字写进日志。
    /// </summary>
    [Fact]
    public void The_session_digest_breaks_unwrap_failures_down_by_status_name_only()
    {
        var events = new List<SessionEvent>
        {
            In(PacketClass.Noti, 561, "信封拆包失败:TooShort"),
            In(PacketClass.Noti, 561, "信封拆包失败:CrcMismatch"),
            In(PacketClass.Noti, 561, "信封拆包失败:LengthPrefixMismatch"),
            In(PacketClass.Noti, 561, "信封拆包失败:CrcMismatch"),
            In(PacketClass.Noti, 561, "信封拆包失败"),                                  // 没有冒号
            In(PacketClass.Noti, 561, "信封拆包失败:SECRET-905 载荷内容"),              // 自由文本
            In(PacketClass.Noti, 561, "信封拆包失败:5"),                                // 数字写法(5 恰是个已定义的值,但不是它的名字)
            In(PacketClass.Noti, 561, "信封拆包失败:99"),                               // 未定义的数值
            In(PacketClass.Noti, 561, "信封拆包失败:crcmismatch"),                      // 大小写不符
        };

        string line = OnlineNetLog.SessionDigest(events);

        Assert.Equal(
            "DNF_NET session events=9 received=9 envelopes=9 opened=0 unwrapFailed=9 unwrapReasons=CrcMismatch:2,LengthPrefixMismatch:1,TooShort:1,Unknown:5 innerShort=0 innerIds=- id2Direct=0 id2Inner=0",
            line);
        Assert.DoesNotContain("SECRET", line);
    }

    /// <summary>进副本那一行与进城那一行同一份汇总,只是行首换成 DNF_NET stage=dungeon;没有事件表时写 none。</summary>
    [Fact]
    public void The_dungeon_session_digest_is_the_same_summary_under_the_dungeon_prefix()
    {
        var events = new List<SessionEvent>
        {
            In(PacketClass.Noti, 561, "Plain"),
            In(PacketClass.Noti, 27, "信封内层"),
            In(PacketClass.Noti, 561, "信封拆包失败:TooShort"),
        };

        string town = OnlineNetLog.SessionDigest(events);
        string dungeon = OnlineNetLog.DungeonSessionDigest(events);

        Assert.StartsWith("DNF_NET session ", town);
        Assert.Equal(Prefix + " session " + town.Substring("DNF_NET session ".Length), dungeon);
        Assert.Contains(" opened=1 unwrapFailed=1 unwrapReasons=TooShort:1 ", dungeon);
        Assert.Equal(Prefix + " session none", OnlineNetLog.DungeonSessionDigest(null));
    }

    /// <summary>内层包号太多时只列前 32 个(升序),其余记个数,行长有界。</summary>
    [Fact]
    public void The_session_digest_caps_the_inner_id_list()
    {
        var events = new List<SessionEvent>();
        for (int id = 900; id > 900 - 40; id--)                     // 40 个不同的包号,故意倒序给
        {
            events.Add(In(PacketClass.Noti, id, "信封内层"));
        }

        string line = OnlineNetLog.SessionDigest(events);

        string ids = line.Split(' ').Single(kv => kv.StartsWith("innerIds=")).Substring("innerIds=".Length);
        string[] parts = ids.Split(',');
        Assert.Equal(OnlineNetLog.MaxDigestIds + 1, parts.Length);
        Assert.Equal("861", parts[0]);
        Assert.Equal("892", parts[OnlineNetLog.MaxDigestIds - 1]);
        Assert.Equal("+8", parts[OnlineNetLog.MaxDigestIds]);
        Assert.Contains(" opened=40 ", line);
    }

    /// <summary>事件列表里夹着空条目也不抛(只当它不存在)。</summary>
    [Fact]
    public void The_session_digest_skips_null_entries()
    {
        var events = new List<SessionEvent> { null, In(PacketClass.Noti, 2, "Plain,压缩") };

        Assert.Equal(
            "DNF_NET session events=2 received=1 envelopes=0 opened=0 unwrapFailed=0 unwrapReasons=- innerShort=0 innerIds=- id2Direct=1 id2Inner=0",
            OnlineNetLog.SessionDigest(events));
    }

    /// <summary>
    /// 汇总行靠会话写进事件的备注字样认信封内层与拆包失败:这里用真会话钉住这份约定。
    /// 一个能拆开的信封封着包号 2、一个拆不开的信封、一个直接来的包号 2。
    /// </summary>
    [Fact]
    public async Task The_session_digest_reads_the_notes_a_real_session_writes()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Push(HostTestKit.RealEnvelope(0, 2, HostTestKit.ListPayload()));
        pipe.Push(DungeonEntryWire.Envelope(GatewayTestSupport.Pattern(40, 0x55)));           // 合成的垃圾载荷:拆不开
        pipe.Push(HostTestKit.DirectListNotice());
        pipe.CloseServer();

        await GatewayTestSupport.Bounded(session.ReceiveAsync(TimeSpan.FromSeconds(1)), 10);

        // 第一个事件是启动包,之后:信封帧、信封内层、信封帧、拆包失败、直接来的包号 2;
        // 合成垃圾载荷的头四个字节(0x55 起的递增字节)既不等于其后的字节数也不等于全长,拆包状态是 LengthPrefixMismatch
        Assert.Equal(
            "DNF_NET session events=6 received=6 envelopes=2 opened=1 unwrapFailed=1 unwrapReasons=LengthPrefixMismatch:1 innerShort=0 innerIds=2 id2Direct=1 id2Inner=1",
            OnlineNetLog.SessionDigest(session.Events));
    }

    /// <summary>
    /// 内层自称总长 10 字节(自洽,信封解包认),但不够一个 15 字节的帧头:会话只记事件("信封内层(帧头不足,未展开)")不交付内层帧;
    /// 汇总行把它记在 innerShort,不进 opened、不列包号。
    /// </summary>
    [Fact]
    public async Task The_session_digest_counts_an_inner_frame_too_short_to_open()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        byte[] inner = new byte[10];
        inner[1] = 0x84;                                            // 包号 900(小端 0x0384)
        inner[2] = 0x03;
        DungeonEntryWire.PutU32(inner, 3, 10);
        byte[] plain = AntibotTestSupport.Concat(AntibotTestSupport.AlgorithmPacket(8), inner);
        pipe.Push(DungeonEntryWire.Envelope(AntibotTestSupport.ValidEnvelope(16, AntibotTestSupport.SeqA, plain)));
        pipe.CloseServer();

        await GatewayTestSupport.Bounded(session.ReceiveAsync(TimeSpan.FromSeconds(1)), 10);

        Assert.Equal(
            "DNF_NET session events=3 received=3 envelopes=1 opened=0 unwrapFailed=0 unwrapReasons=- innerShort=1 innerIds=- id2Direct=0 id2Inner=0",
            OnlineNetLog.SessionDigest(session.Events));
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
