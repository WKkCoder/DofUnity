using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 联网进城的"启动包之后"那一段(游戏登录 → 顺带拉角色列表 → 逐槽选角 → 进城快照)在假会话上的测试:
/// 真 <see cref="GameClientSession"/> 接内存管道,脚本化的"服务端"在收到客户端命令时回放合成帧,整段在进程内走完,不联网。
/// 重点是角色回填:登录后拉角色列表那一步的应答帧(直接来的,或封在真信封里由会话层拆开的)以前收了就丢,
/// 现在交给 <see cref="TownEntryParser"/>,快照的等级、职业、成长类型因此被填上;进副本阶段的帧不会再喂。
/// 数据全是合成的:账号号 905、角色列表来自 <see cref="UserInfoTestData"/>(条目 i 的等级 61+i、职业 50+i、成长类型字节 0x25+i)。
/// </summary>
public class OnlineTownEntrySessionTests
{
    private static byte[] TimeSync(int hour, int minute)
    {
        return new NoticePayloadBuilder().U16(hour).U16(minute).ToArray();
    }

    // ======== 角色回填 ========

    /// <summary>
    /// 角色列表应答直接发来(会话加密、压缩的包号 2):快照的等级、职业、成长类型(取低 4 位)是选中槽那一条的值。
    /// 顺带钉住既有行为:客户端只发三类命令(登录 1、列表请求 8 带账号号小端与类型 2、选角 4 带槽号),阶段按序回调,帧数按会话事件计。
    /// </summary>
    [Fact]
    public async Task A_character_list_answer_sent_directly_backfills_level_job_and_growtype()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(HostTestKit.LoginOk(), HostTestKit.Frames(HostTestKit.DirectListNotice()), slot => HostTestKit.EnterComplete());
        var stages = new List<OnlineStage>();

        OnlineEntryResult result = await HostTestKit.EnterTown(session, onStage: stages.Add);

        Assert.True(result.Entered);
        Assert.Equal(OnlineFailure.None, result.Failure);
        Assert.Equal(0, result.Slot);
        TownEntrySnapshot snapshot = result.Snapshot;
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot.Slot);
        Assert.Equal(61, snapshot.Level);
        Assert.Equal(50, snapshot.Job);
        Assert.Equal(5, snapshot.GrowType);          // 字节 0x25 的低 4 位
        Assert.True(snapshot.EnterComplete);
        Assert.Equal(0, snapshot.FramesMalformed);

        Assert.Equal(new[] { OnlineStage.GameLogin, OnlineStage.SelectSlot }, stages);
        IReadOnlyList<DungeonEntryClientFrame> sent = pipe.ClientFrames;
        Assert.Equal(new ushort[] { 1, 8, 4 }, sent.Select(f => f.Id).ToArray());
        Assert.All(sent, f => Assert.Equal(PacketClass.Cmd, f.Class));
        Assert.Equal(new byte[] { 0x89, 0x03, 2 }, sent[1].Plain.Take(3).ToArray());    // 905 = 0x0389 小端,类型 2
        Assert.Equal(0, sent[2].Plain[0]);
        Assert.Equal(session.Events.Count, result.FramesSeen);
        Assert.Equal(session.SecondLayerMismatches, result.SecondLayerMismatches);
    }

    /// <summary>
    /// 角色列表应答封在真信封里(561、算法 16、合成序号):会话层拆开,内层帧紧跟在信封帧后面交付,载荷是模式 2 的明文;
    /// 快照照样被填上,信封帧自己是未知帧号,只计"看过"。
    /// </summary>
    [Fact]
    public async Task A_character_list_answer_wrapped_in_a_real_envelope_backfills_the_same_values()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.RealEnvelope(0, 2, HostTestKit.ListPayload())),
            slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        TownEntrySnapshot snapshot = result.Snapshot;
        Assert.Equal(61, snapshot.Level);
        Assert.Equal(50, snapshot.Job);
        Assert.Equal(5, snapshot.GrowType);
        Assert.Equal(0, snapshot.FramesMalformed);
        // 登录回包 1 帧 + 列表窗口里的信封帧与内层帧 2 帧 + 选角窗口的进城完成 1 帧;认得并读出的是内层列表与进城完成
        Assert.Equal(4, snapshot.FramesSeen);
        Assert.Equal(2, snapshot.FramesParsed);
    }

    /// <summary>选中哪个槽就取列表里同一位置的条目(槽号即列表位置),前面的槽没有判定也照常往后试。</summary>
    [Theory]
    [InlineData(0, 61, 50, 5)]
    [InlineData(1, 62, 51, 6)]
    [InlineData(2, 63, 52, 7)]
    public async Task The_selected_slot_picks_the_entry_at_the_same_list_position(int enterAt, int level, int job, int growType)
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.DirectListNotice()),
            slot => slot == enterAt ? HostTestKit.EnterComplete() : HostTestKit.Frames());

        OnlineEntryResult result = await HostTestKit.EnterTown(session, maxSlots: 3);

        Assert.True(result.Entered);
        Assert.Equal(enterAt, result.Slot);
        Assert.Equal(enterAt, result.Snapshot.Slot);
        Assert.Equal(level, result.Snapshot.Level);
        Assert.Equal(job, result.Snapshot.Job);
        Assert.Equal(growType, result.Snapshot.GrowType);
        Assert.Equal(Enumerable.Range(0, enterAt + 1).Select(i => (byte)i).ToArray(),
            pipe.ClientFrames.Where(f => f.Id == 4).Select(f => f.Plain[0]).ToArray());
    }

    /// <summary>登录回包窗口里就带着角色列表(服务端随登录成功一并推来的):也算城镇阶段的帧,照样回填。</summary>
    [Fact]
    public async Task A_character_list_that_arrives_together_with_the_login_reply_is_fed_too()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.Frames(DungeonEntryWire.Reply(1, HostTestKit.LoginSuccess()), HostTestKit.DirectListNotice()),
            HostTestKit.Frames(),
            slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        Assert.Equal(61, result.Snapshot.Level);
        Assert.Equal(50, result.Snapshot.Job);
        Assert.Equal(5, result.Snapshot.GrowType);
    }

    /// <summary>
    /// 各窗口的帧各喂一遍、不多不少:登录回包、列表应答、选角窗口的进城完成,以及尾窗口里晚到的时间同步,
    /// 看过 4 帧、读出 3 帧(回包是命令帧,只计看过)。尾窗口的帧由后台任务在选角窗口结束后才灌入(窗口给得宽,无论落在哪个窗口都被喂到)。
    /// </summary>
    [Fact]
    public async Task Frames_of_every_town_window_are_fed_exactly_once()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.DirectListNotice()),
            slot =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(450);
                    pipe.Push(DungeonEntryWire.Notice(300, TimeSync(13, 45)));
                });
                return HostTestKit.EnterComplete();
            });
        var windows = new OnlineTownWindows(
            TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(1000));

        OnlineEntryResult result = await HostTestKit.EnterTown(session, windows: windows);

        Assert.True(result.Entered);
        TownEntrySnapshot snapshot = result.Snapshot;
        Assert.Equal(4, snapshot.FramesSeen);
        Assert.Equal(3, snapshot.FramesParsed);
        Assert.Equal(0, snapshot.FramesMalformed);
        Assert.Equal(13, snapshot.ServerHour);
        Assert.Equal(45, snapshot.ServerMinute);
        Assert.Equal(61, snapshot.Level);
        Assert.True(snapshot.EnterComplete);
    }

    // ======== 回填不到时:空着,不猜 ========

    /// <summary>没有列表应答:进城照常成功,三个角色字段为 null,诊断计数里没有错位。</summary>
    [Fact]
    public async Task Without_a_list_answer_the_entry_still_completes_and_the_character_fields_stay_null()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(HostTestKit.LoginOk(), HostTestKit.Frames(), slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        TownEntrySnapshot snapshot = result.Snapshot;
        Assert.Null(snapshot.Level);
        Assert.Null(snapshot.Job);
        Assert.Null(snapshot.GrowType);
        Assert.True(snapshot.EnterComplete);
        Assert.Equal(2, snapshot.FramesSeen);        // 登录回包 + 进城完成
        Assert.Equal(1, snapshot.FramesParsed);
        Assert.Equal(0, snapshot.FramesMalformed);
    }

    /// <summary>登录回包读不出账号号:不发角色列表请求(与既有行为一致),照常选角,角色字段为 null。</summary>
    [Fact]
    public async Task No_list_request_is_sent_when_the_account_id_cannot_be_read()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        byte[] unreadable = { 1, 99, 0, 0, 0 };         // 成功形状,但名长声明 99,超过上限,读不出账号号
        Assert.False(LoginReplyParse.TryGetAccountId(unreadable, out _));
        pipe.Script = HostTestKit.TownScript(HostTestKit.Frames(DungeonEntryWire.Reply(1, unreadable)), HostTestKit.Frames(), slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        Assert.Equal(new ushort[] { 1, 4 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
        Assert.Null(result.Snapshot.Level);
    }

    /// <summary>
    /// 封进信封的内层列表帧仍是压缩态、会话层也解不开它(zlib 流缺了尾巴:会话层认得出 zlib 头,严格解压不通过,原样交付压缩字节,首字节不是模式 2):
    /// 解析器读不通,按错位计数,字段保持 null,不去猜着解压,也不丢帧。内层帧能解开的情形见 <see cref="InnerFrameInflateTests"/>:快照照样回填。
    /// </summary>
    [Fact]
    public async Task An_inner_list_frame_that_is_still_compressed_is_counted_malformed_and_never_guessed()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        byte[] stream = UserInfoTestData.Zlib(HostTestKit.ListPayload());
        byte[] withoutItsTail = stream.Take(stream.Length - 6).ToArray();
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.RealEnvelope(0, 2, withoutItsTail)),
            slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.True(result.Entered);
        Assert.Null(result.Snapshot.Level);
        Assert.Null(result.Snapshot.Job);
        Assert.Null(result.Snapshot.GrowType);
        Assert.Equal(1, result.Snapshot.FramesMalformed);
        Assert.Equal(1, result.Snapshot.FramesParsed);       // 进城完成
    }

    /// <summary>选中的槽号超出列表条数:字段保持 null,计一次错位,进城本身不受影响。</summary>
    [Fact]
    public async Task A_slot_beyond_the_listed_entries_leaves_the_fields_null()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        byte[] twoEntries = UserInfoTestData.Packet(UserInfoTestData.Standard(0), UserInfoTestData.Standard(1));
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.DirectListNotice(twoEntries)),
            slot => slot == 2 ? HostTestKit.EnterComplete() : HostTestKit.Frames());

        OnlineEntryResult result = await HostTestKit.EnterTown(session, maxSlots: 3);

        Assert.True(result.Entered);
        Assert.Equal(2, result.Snapshot.Slot);
        Assert.Null(result.Snapshot.Level);
        Assert.Equal(1, result.Snapshot.FramesMalformed);
    }

    // ======== 进副本阶段的帧不再喂 ========

    /// <summary>
    /// 进城后接着在同一个会话上进副本(宿主的真实顺序):进副本阶段到的角色列表(等级换成 99)落不进快照,
    /// 快照在进城那一刻定下,看过的帧数也不再增加;进副本本身照常走完。
    /// </summary>
    [Fact]
    public async Task Frames_received_in_the_dungeon_stage_are_never_fed_to_the_town_snapshot()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        UserInfoTestData.EntrySpec changed = UserInfoTestData.Standard(0);
        changed.Level = 99;
        byte[] laterList = HostTestKit.DirectListNotice(UserInfoTestData.Packet(changed));
        pipe.Script = HostTestKit.TownScript(
            HostTestKit.LoginOk(),
            HostTestKit.Frames(HostTestKit.DirectListNotice()),
            slot => HostTestKit.EnterComplete(),
            HostTestKit.DungeonChain(
                HostTestKit.Frames(DungeonEntryWire.Notice(27, DungeonEntryWire.Loading27()), laterList),
                HostTestKit.Frames(DungeonEntryWire.Notice(28, DungeonEntryWire.Info28()), DungeonEntryWire.Notice(29, DungeonEntryWire.Map29()))));

        OnlineEntryResult town = await HostTestKit.EnterTown(session);
        TownEntrySnapshot before = town.Snapshot;
        int seenBefore = before.FramesSeen;
        Assert.Equal(61, before.Level);

        OnlineDungeonResult dungeon = await GatewayTestSupport.Bounded(
            new OnlineDungeonRunner(town.Session).EnterAsync(901, 3, null, CancellationToken.None, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)), 20);

        Assert.True(dungeon.Entered);
        Assert.Same(before, town.Snapshot);
        Assert.Equal(61, town.Snapshot.Level);
        Assert.Equal(seenBefore, town.Snapshot.FramesSeen);
        // 进副本阶段确实收到了那份列表(记录里是一条无关帧),只是没进快照
        Assert.Contains(dungeon.Frames, f => f.PacketId == 2 && f.Note == OnlineDungeonFrameNote.None);
    }

    // ======== 失败路径与连接归属 ========

    /// <summary>登录被拒:带失败码返回,不发列表请求与选角,结果没有快照也没有会话。</summary>
    [Fact]
    public async Task A_rejected_login_stops_before_the_list_request_and_carries_the_code()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        byte[] rejected = DungeonEntryWire.Reply(1, new byte[] { 0, 0x17, 0, 0 });
        Assert.NotEqual(ServerFrameDecoder.RejectFrameLength, rejected.Length);     // 恰好 21 字节会被当成接受阶段的拒绝帧
        pipe.Script = HostTestKit.TownScript(HostTestKit.Frames(rejected), HostTestKit.Frames(HostTestKit.DirectListNotice()), slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.False(result.Entered);
        Assert.Equal(OnlineStage.GameLogin, result.Stage);
        Assert.Equal(OnlineFailure.GameLoginRejected, result.Failure);
        Assert.Equal((byte)0x17, result.LoginFailureCode);
        Assert.Null(result.Snapshot);
        Assert.Null(result.Session);
        Assert.Equal(new ushort[] { 1 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
    }

    /// <summary>登录没有回包:停在游戏登录,没有后续命令。</summary>
    [Fact]
    public async Task A_login_without_a_reply_fails_with_no_reply()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(HostTestKit.Frames(), HostTestKit.Frames(), slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session);

        Assert.Equal(OnlineStage.GameLogin, result.Stage);
        Assert.Equal(OnlineFailure.GameLoginNoReply, result.Failure);
        Assert.Null(result.Snapshot);
        Assert.Equal(new ushort[] { 1 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
    }

    /// <summary>登录请求本地检查不过(账号超长):什么都不发。</summary>
    [Fact]
    public async Task A_bad_login_request_is_refused_locally_and_nothing_is_sent()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(HostTestKit.LoginOk(), HostTestKit.Frames(), slot => HostTestKit.EnterComplete());

        OnlineEntryResult result = await HostTestKit.EnterTown(session, account: new string('a', 30));

        Assert.Equal(OnlineStage.GameLogin, result.Stage);
        Assert.Equal(OnlineFailure.BadLoginRequest, result.Failure);
        Assert.Empty(pipe.ClientFrames);
    }

    /// <summary>每个槽都没有判定:逐个试完,以"没有可用的槽"收场,没有快照;列表应答不会让没进城的结果带上快照。</summary>
    [Fact]
    public async Task When_no_slot_enters_the_result_is_a_failure_without_a_snapshot()
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(HostTestKit.LoginOk(), HostTestKit.Frames(HostTestKit.DirectListNotice()), slot => HostTestKit.Frames());

        OnlineEntryResult result = await HostTestKit.EnterTown(session, maxSlots: 2);

        Assert.False(result.Entered);
        Assert.Equal(OnlineStage.SelectSlot, result.Stage);
        Assert.Equal(OnlineFailure.NoUsableSlot, result.Failure);
        Assert.Equal(-1, result.Slot);
        Assert.Null(result.Snapshot);
        Assert.Equal(new ushort[] { 1, 8, 4, 4 }, pipe.ClientFrames.Select(f => f.Id).ToArray());
    }

    /// <summary>连接的释放归结果对象:成功与失败都一样,返回时没有释放,<see cref="OnlineEntryResult.Dispose"/> 释放一次且只一次。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_result_takes_over_disposing_the_connection(bool success)
    {
        (GameClientSession session, DungeonEntryPipe pipe) = await HostTestKit.ReadySession();
        pipe.Script = HostTestKit.TownScript(
            success ? HostTestKit.LoginOk() : HostTestKit.Frames(),
            HostTestKit.Frames(),
            slot => HostTestKit.EnterComplete());
        var connection = new CountingConnection();

        OnlineEntryResult result = await HostTestKit.EnterTown(session, connection);

        Assert.Equal(success, result.Entered);
        Assert.Equal(0, connection.DisposeCount);
        result.Dispose();
        Assert.Equal(1, connection.DisposeCount);
        result.Dispose();
        Assert.Equal(1, connection.DisposeCount);
    }

    /// <summary>线上用的那组窗口(实测定下的 8、2、10、3 秒)没有被改动。</summary>
    [Fact]
    public void The_production_windows_are_the_field_tested_ones()
    {
        OnlineTownWindows w = OnlineTownWindows.Default;
        Assert.Equal(TimeSpan.FromSeconds(8), w.Login);
        Assert.Equal(TimeSpan.FromSeconds(2), w.CharacterList);
        Assert.Equal(TimeSpan.FromSeconds(10), w.Slot);
        Assert.Equal(TimeSpan.FromSeconds(3), w.Tail);
    }
}
