using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网城镇世界包装(<see cref="OnlineTownWorld"/>)的行为测试:确认放行的四个比较项、单在途、回退后的屏蔽窗口、
/// 安全位置的记录与回退(真主角摆回去、清出口锁存、推一帧)、提示文案与到期、强切关闭时的日志去重、创建会话时的复位。
/// 期望值都是手算的字面量;时间全部由 <c>Poll(nowMs)</c> 喂入,没有任何真实等待。
/// </summary>
public sealed class OnlineTownWorldTests
{
    // 合成图册:城镇 1 有网关区 0(登录点 (233,177))与普通区 1、2、3;城镇 2 有普通区 0、1。
    // 各区可走区域不同,所以区的"落点"互不相同:区 1 → 外包矩形中心 (400,250);区 2 → (400,220);区 3 → (300,200)。
    private static TownAtlas WorldAtlas() => TownKit.Atlas(
        (1, new[]
        {
            new KitArea(0, "[gate]", new[] { 233, 177 }, new[] { 0, 100, 800, 300 }, new[] { new KitExit(402, 118, 38, 74, 1, 2) }),
            new KitArea(1, "[normal]", null, new[] { 0, 100, 800, 300 }, new[] { new KitExit(610, 215, 70, 36, 1, 2) }),
            new KitArea(2, "[normal]", null, new[] { 100, 120, 600, 200 },
                new[] { new KitExit(110, 150, 40, 40, 1, 1), new KitExit(640, 150, 40, 40, 1, 0) }),
            new KitArea(3, "[normal]", null, new[] { 50, 150, 500, 100 }, new[] { new KitExit(60, 160, 30, 30, 1, 2) }),
        }),
        (2, new[]
        {
            new KitArea(0, "[normal]", null, new[] { 0, 100, 800, 300 }, new[] { new KitExit(10, 150, 30, 30, 1, 1) }),
            new KitArea(1, "[normal]", null, new[] { 0, 100, 800, 300 }),
        }));

    private static TownExit Exit12 => TownKit.Exit(610, 215, 70, 36, 1, 2);

    private static TownWorldRig NewRig(bool heroes = false, bool areaSync = true, long timeoutMs = 0) =>
        new TownWorldRig(WorldAtlas(), new TownPlace(1, 1), heroes, areaSync, timeoutMs);

    /// <summary>主角在区域 1(落点 (400,250))里的场景:真的 <see cref="TownHero"/>,会话是假的出口来源,记录每次 <c>Update</c> 与出口清除。</summary>
    private sealed class Scene : IDisposable
    {
        public Scene(bool areaSync = true, long timeoutMs = 0)
        {
            Rig = NewRig(heroes: true, areaSync: areaSync, timeoutMs: timeoutMs);
            Session = (TownKitSession)Rig.World.Create(new TownPlace(1, 1));
            Hero = Session.Hero;
        }

        public TownWorldRig Rig { get; }

        public TownKitSession Session { get; }

        public TownHero Hero { get; }

        /// <summary>主角在出口外自由走动到 (x, y),宿主这一帧照例记录安全位置。</summary>
        public void Idle(double x, double y, bool left)
        {
            Hero.Place(x, y, left, HeroMotion.Rest, 0);
            Rig.World.RecordSafePosition();
        }

        /// <summary>主角走进出口矩形 (640,230) 并锁存了待触发出口;宿主这一帧照例记录安全位置(此时不该更新)。</summary>
        public void PressExit()
        {
            Hero.Place(640, 230, false, HeroMotion.Rest, 0);
            Session.PendingExit = Exit12;
            Rig.World.RecordSafePosition();
        }

        public void AssertBackAt(double x, double y, bool left)
        {
            Assert.Equal(x, Hero.X);
            Assert.Equal(y, Hero.Y);
            Assert.Equal(left, Hero.FacingLeft);
            Assert.Equal(HeroMotion.Rest, Hero.Motion);
            Assert.Null(Session.PendingExit);
            Assert.Equal(1, Session.ClearCalls);
            Assert.Equal((0.0, 0, 0), Assert.Single(Session.Updates));
        }

        public void Dispose() => Rig.Dispose();
    }

    // ───────────────────────── 构造与转发 ─────────────────────────

    [Fact]
    public void Constructor_rejects_missing_inner_world_atlas_or_coordinator()
    {
        TownAtlas atlas = WorldAtlas();
        var transport = new TownKitTransport();
        using var coord = new OnlineTownCommandCoordinator(transport, TownWorldRig.SelfUid);
        var inner = new TownKitWorld(atlas, new TownPlace(1, 1));

        Assert.Throws<ArgumentNullException>(() => new OnlineTownWorld(null, atlas, coord));
        Assert.Throws<ArgumentNullException>(() => new OnlineTownWorld(inner, null, coord));
        Assert.Throws<ArgumentNullException>(() => new OnlineTownWorld(inner, atlas, null));
    }

    [Fact]
    public void Plain_members_are_forwarded_to_the_inner_world_and_the_coordinator()
    {
        using var rig = NewRig();

        Assert.Same(rig.Inner, rig.World.Inner);
        Assert.Same(rig.Coordinator, rig.World.Coordinator);
        Assert.Equal(new TownPlace(1, 1), rig.World.Start);
        Assert.Equal(new TownPlace(1, 1), rig.World.Return);
        Assert.Null(rig.World.Splash(null, new TownPlace(1, 2)));
        Assert.False(rig.World.TryRoute(new TownPlace(1, 1), out TownExit routed));
        Assert.Equal(default(TownExit), routed);
        Assert.False(rig.World.IsAreaChangePending);
        Assert.Null(rig.World.CurrentPrompt);

        rig.World.Create(new TownPlace(1, 1));
        rig.World.Poll(1000);
        Assert.Null(rig.Through(new TownPlace(1, 1), Exit12));
        Assert.True(rig.World.IsAreaChangePending);
    }

    // ───────────────────────── 无目标出口 ─────────────────────────

    [Fact]
    public void An_exit_without_a_target_is_passed_straight_through_and_never_synced()
    {
        using var rig = NewRig();
        rig.World.Create(new TownPlace(1, 1));
        rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(1, 2);

        TownPlace? result = rig.Through(new TownPlace(1, 1), TownKit.Exit(0, 0, 10, 10, -1, -1));

        Assert.Equal(new TownPlace(1, 2), result);
        Assert.Empty(rig.Transport.Sent);
        Assert.Null(rig.World.CurrentPrompt);
        Assert.False(rig.World.IsAreaChangePending);
        Assert.Empty(rig.Logs);
    }

    [Fact]
    public void An_exit_the_inner_world_does_not_take_is_never_synced()
    {
        using var rig = NewRig();
        rig.World.Create(new TownPlace(1, 1));
        rig.Inner.ThroughBehavior = (_, _, _) => null;

        Assert.Null(rig.Through(new TownPlace(1, 1), Exit12));

        Assert.Empty(rig.Transport.Sent);
        Assert.Null(rig.World.CurrentPrompt);
        Assert.Empty(rig.Logs);
    }

    // ───────────────────────── 确认放行 ─────────────────────────

    [Fact]
    public void A_confirmed_change_is_passed_once_for_the_same_source_and_target_and_then_forgotten()
    {
        using var rig = NewRig();
        rig.World.Create(new TownPlace(1, 1));
        rig.World.Poll(1000);
        Assert.Null(rig.Through(new TownPlace(1, 1), Exit12));
        Assert.Equal(AreaChangeStatus.Confirmed, rig.ConfirmNormal(1, 2, 1016).Status);
        Assert.Null(rig.World.CurrentPrompt);

        TownPlace? passed = rig.Through(new TownPlace(1, 1), Exit12);

        Assert.True(passed.HasValue);
        Assert.Equal(1, passed.Value.Town);
        Assert.Equal(2, passed.Value.Area);
        Assert.Single(rig.Transport.Sent);

        // 放行只一次:同一个出口再来就要重新同步
        Assert.Null(rig.Through(new TownPlace(1, 1), Exit12));
        Assert.Equal(2, rig.Transport.Sent.Count);
    }

    [Theory]
    [InlineData(2, 1, 1, 2)]   // 只有来源村不同
    [InlineData(1, 3, 1, 2)]   // 只有来源区不同
    [InlineData(1, 1, 2, 1)]   // 只有目标村不同
    [InlineData(1, 1, 1, 3)]   // 只有目标区不同
    public void A_confirmed_change_is_not_passed_when_exactly_one_of_the_four_numbers_differs(int fromTown, int fromArea, int toTown, int toArea)
    {
        using var rig = NewRig();
        rig.World.Create(new TownPlace(1, 1));
        rig.World.Poll(1000);
        Assert.Null(rig.Through(new TownPlace(1, 1), Exit12));
        Assert.Equal(AreaChangeStatus.Confirmed, rig.ConfirmNormal(1, 2, 1016).Status);

        TownPlace? variant = rig.Through(new TownPlace(fromTown, fromArea), TownKit.Exit(0, 0, 10, 10, toTown, toArea));

        Assert.Null(variant);
        Assert.Equal(2, rig.Transport.Sent.Count);
        KitSend second = rig.Transport.Sent[1];
        Assert.Equal((byte)toTown, second.DstV);
        Assert.Equal((byte)toArea, second.DstA);
        Assert.Equal((ushort)fromTown, second.SrcV);
        Assert.Equal((ushort)fromArea, second.SrcA);
        Assert.Equal(OnlineTownPromptKind.Waiting, rig.World.CurrentPrompt.Kind);
    }

    // 上面那组里"只有目标村不同"的那一行,目标区号也跟着变了;这里让目标区号相同(都是 1)、只有目标村不同(1 与 2),单独钉住目标村这一项
    [Fact]
    public void A_confirmed_change_is_not_passed_to_another_town_with_the_same_target_area_number()
    {
        using var rig = NewRig();
        rig.World.Create(new TownPlace(1, 1));
        rig.World.Poll(1000);
        Assert.Null(rig.Through(new TownPlace(1, 0), TownKit.Exit(0, 0, 10, 10, 1, 1)));
        Assert.Equal(AreaChangeStatus.Confirmed, rig.ConfirmNormal(1, 1, 1016).Status);

        TownPlace? variant = rig.Through(new TownPlace(1, 0), TownKit.Exit(0, 0, 10, 10, 2, 1));

        Assert.Null(variant);
        Assert.Equal(2, rig.Transport.Sent.Count);
        KitSend second = rig.Transport.Sent[1];
        Assert.Equal((byte)2, second.DstV);
        Assert.Equal((byte)1, second.DstA);
        Assert.Equal((ushort)1, second.SrcV);
        Assert.Equal((ushort)0, second.SrcA);
        Assert.Equal(OnlineTownPromptKind.Waiting, rig.World.CurrentPrompt.Kind);
    }

    [Fact]
    public void A_rollback_forgets_a_confirmation_the_host_never_collected()
    {
        using var rig = NewRig();
        rig.World.Create(new TownPlace(1, 1));
        rig.World.Poll(1000);
        Assert.Null(rig.Through(new TownPlace(1, 1), Exit12));
        Assert.Equal(AreaChangeStatus.Confirmed, rig.ConfirmNormal(1, 2, 1016).Status);

        // 宿主没取走确认,先来了一个映射不上的出口:回退,同时那份确认作废
        rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(1, 77);
        Assert.Null(rig.Through(new TownPlace(1, 1), TownKit.Exit(0, 0, 10, 10, 1, 77)));
        rig.Inner.ThroughBehavior = null;
        rig.World.Poll(1500);

        Assert.Null(rig.Through(new TownPlace(1, 1), Exit12));
        Assert.Equal(2, rig.Transport.Sent.Count);
    }

    [Fact]
    public void A_rejection_never_leaves_a_confirmation_that_would_let_the_next_identical_exit_through()
    {
        using var rig = NewRig();
        rig.World.Create(new TownPlace(1, 1));
        rig.World.Poll(1000);
        Assert.Null(rig.Through(new TownPlace(1, 1), Exit12));
        Assert.Equal(AreaChangeStatus.Confirmed, rig.ConfirmNormal(1, 2, 1016).Status);
        Assert.True(rig.Through(new TownPlace(1, 1), Exit12).HasValue);   // 确认被取走,来源 1/1、目标 1/2 还留在记忆里

        // 同一个出口再来一次,这回被服务端拒绝
        Assert.Null(rig.Through(new TownPlace(1, 1), Exit12));
        rig.Transport.Push(KitEvents.Error(0x11));
        Assert.Equal(AreaChangeStatus.RolledBack, rig.World.Poll(1100).Status);
        rig.World.Poll(1600);

        Assert.Null(rig.Through(new TownPlace(1, 1), Exit12));
        Assert.Equal(3, rig.Transport.Sent.Count);
    }

    // ───────────────────────── 单在途 ─────────────────────────

    [Fact]
    public void While_a_request_is_in_flight_every_through_is_swallowed_without_any_side_effect()
    {
        using var s = new Scene();
        s.Rig.World.Poll(1000);
        s.PressExit();
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
        Assert.Single(s.Rig.Transport.Sent);
        OnlineTownPrompt waiting = s.Rig.World.CurrentPrompt;
        Assert.Equal(OnlineTownPromptKind.Waiting, waiting.Kind);

        // 在途时来一个映射不上的出口:不发送、不换提示、不回退
        s.Rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(1, 77);
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), TownKit.Exit(0, 0, 10, 10, 1, 77)));
        // 在途时来一个映射得上的出口:同样不发送
        s.Rig.Inner.ThroughBehavior = null;
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), TownKit.Exit(0, 0, 10, 10, 1, 3)));

        Assert.Single(s.Rig.Transport.Sent);
        Assert.Same(waiting, s.Rig.World.CurrentPrompt);
        Assert.Equal(0, s.Session.ClearCalls);
        Assert.Empty(s.Session.Updates);
        Assert.Equal(640.0, s.Hero.X);
        Assert.Equal(230.0, s.Hero.Y);
        Assert.Empty(s.Rig.Logs);
        Assert.True(s.Rig.World.IsAreaChangePending);
    }

    [Fact]
    public void The_waiting_prompt_has_no_minutes_and_never_expires_and_goes_away_with_the_result()
    {
        using var s = new Scene();
        s.Rig.World.Poll(1000);
        s.PressExit();
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));

        Assert.Equal(OnlineTownPromptKind.Waiting, s.Rig.World.CurrentPrompt.Kind);
        Assert.Equal(0, s.Rig.World.CurrentPrompt.Minutes);
        Assert.Equal(0L, s.Rig.World.CurrentPrompt.ExpiresAtMs);
        Assert.Equal(AreaChangeStatus.None, s.Rig.World.Poll(1000 + 3999).Status);
        Assert.Equal(OnlineTownPromptKind.Waiting, s.Rig.World.CurrentPrompt.Kind);

        s.Rig.ConfirmNormal(1, 2, 5000);
        Assert.Null(s.Rig.World.CurrentPrompt);
        Assert.Equal(new[] { "DNF_ONLINE stage=town event=areachange outcome=ok" }, s.Rig.Logs);
        Assert.Equal(0, s.Session.ClearCalls);
        Assert.Empty(s.Session.Updates);
    }

    // ───────────────────────── 回退后的屏蔽窗口 ─────────────────────────

    [Fact]
    public void After_a_rollback_exits_are_ignored_for_exactly_four_hundred_milliseconds_and_clear_the_pending_exit_each_time()
    {
        using var s = new Scene();
        s.Rig.World.Poll(1000);
        s.PressExit();
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
        s.Rig.Transport.Push(KitEvents.Error(0x11));
        Assert.Equal(AreaChangeStatus.RolledBack, s.Rig.World.Poll(1016).Status);   // 屏蔽到 1016 + 400 = 1416
        Assert.Equal(1, s.Session.ClearCalls);

        // 1415:还在窗口内。主角又被推回出口里,Through 来了:不重发,但每次都清掉锁存的出口
        s.Session.PendingExit = Exit12;
        s.Rig.World.Poll(1415);
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
        Assert.Single(s.Rig.Transport.Sent);
        Assert.Equal(2, s.Session.ClearCalls);
        Assert.Null(s.Session.PendingExit);

        // 1416:窗口刚好结束,出口重新生效
        s.Rig.World.Poll(1416);
        s.Session.PendingExit = Exit12;
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
        Assert.Equal(2, s.Rig.Transport.Sent.Count);
        Assert.Equal(2, s.Session.ClearCalls);
    }

    [Fact]
    public void Creating_a_new_session_ends_the_bounce_window_at_once()
    {
        using var s = new Scene();
        s.Rig.World.Poll(1000);
        s.PressExit();
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
        s.Rig.Transport.Push(KitEvents.Error(0x11));
        Assert.Equal(AreaChangeStatus.RolledBack, s.Rig.World.Poll(1016).Status);   // 屏蔽到 1416

        // 窗口还没过,宿主就换了新的会话:新会话里的出口不该被前一次回退屏蔽
        s.Rig.World.Poll(1100);
        s.Rig.World.Create(new TownPlace(1, 2));
        Assert.Null(s.Rig.Through(new TownPlace(1, 2), TownKit.Exit(110, 150, 40, 40, 1, 1)));

        Assert.Equal(2, s.Rig.Transport.Sent.Count);
        Assert.Equal((byte)1, s.Rig.Transport.Sent[1].DstA);
        Assert.Equal(OnlineTownPromptKind.Waiting, s.Rig.World.CurrentPrompt.Kind);
    }

    // ───────────────────────── 映射不上的出口 ─────────────────────────

    [Theory]
    [InlineData(1, 77, 1, 1)]   // 目标映射不上:区域 77 图册里没有
    [InlineData(1, 2, 7, 7)]    // 来源映射不上:7/7 图册里没有
    [InlineData(1, 300, 1, 1)]  // 目标区号超过一个字节
    public void An_unmapped_area_sends_nothing_prompts_for_four_seconds_and_puts_the_hero_back_at_its_last_safe_position(
        int toTown, int toArea, int fromTown, int fromArea)
    {
        using var s = new Scene();
        s.Rig.World.Poll(1000);
        s.Idle(300, 260, true);   // 最后一个安全位置:(300,260) 朝左
        s.PressExit();
        s.Rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(toTown, toArea);

        Assert.Null(s.Rig.Through(new TownPlace(fromTown, fromArea), Exit12));

        Assert.Empty(s.Rig.Transport.Sent);
        OnlineTownPrompt prompt = s.Rig.World.CurrentPrompt;
        Assert.Equal(OnlineTownPromptKind.UnmappedArea, prompt.Kind);
        Assert.Equal(0, prompt.Minutes);
        Assert.Equal(5000L, prompt.ExpiresAtMs);   // 1000 + 4000
        Assert.Equal(new[] { "DNF_ONLINE stage=town event=areachange outcome=unmapped" }, s.Rig.Logs);
        s.AssertBackAt(300, 260, true);
        Assert.False(s.Rig.World.IsAreaChangePending);
    }

    [Fact]
    public void An_unmapped_area_prompt_uses_the_clock_of_the_last_poll()
    {
        using var s = new Scene();
        s.Rig.World.Poll(2500);
        s.Rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(1, 77);

        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));

        Assert.Equal(6500L, s.Rig.World.CurrentPrompt.ExpiresAtMs);
        Assert.Equal(OnlineTownPromptKind.UnmappedArea, s.Rig.World.CurrentPrompt.Kind);
    }

    [Fact]
    public void An_unmapped_area_is_also_rejected_cleanly_before_any_session_exists()
    {
        using var rig = NewRig();
        rig.World.Poll(100);
        rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(1, 77);

        Assert.Null(rig.Through(new TownPlace(1, 1), Exit12));

        Assert.Empty(rig.Transport.Sent);
        Assert.Equal(OnlineTownPromptKind.UnmappedArea, rig.World.CurrentPrompt.Kind);
        Assert.Equal(4100L, rig.World.CurrentPrompt.ExpiresAtMs);
    }

    [Fact]
    public void An_unmapped_area_starts_the_bounce_window_too()
    {
        using var s = new Scene();
        s.Rig.World.Poll(1000);
        s.Rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(1, 77);
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));   // 屏蔽到 1400
        OnlineTownPrompt first = s.Rig.World.CurrentPrompt;

        s.Rig.World.Poll(1399);
        s.Session.PendingExit = Exit12;
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
        Assert.Same(first, s.Rig.World.CurrentPrompt);
        Assert.Single(s.Rig.Logs);
        Assert.Equal(2, s.Session.ClearCalls);

        s.Rig.World.Poll(1400);
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
        Assert.NotSame(first, s.Rig.World.CurrentPrompt);
        Assert.Equal(2, s.Rig.Logs.Count);
        Assert.Equal(5400L, s.Rig.World.CurrentPrompt.ExpiresAtMs);
    }

    // ───────────────────────── 各种结局:提示、日志、回退 ─────────────────────────

    private const string ForbiddenCode68 = "DNF_ONLINE stage=town event=areachange outcome=forbidden code=68";

    [Theory]
    //     结局          结果状态                          提示种类(0 = 无)  分钟  日志
    [InlineData("rejected", AreaChangeStatus.RolledBack, 3, 0, "DNF_ONLINE stage=town event=areachange outcome=rejected code=17")]
    [InlineData("forbidden9", AreaChangeStatus.Forbidden, 2, 9, ForbiddenCode68 + " minutes=9")]
    [InlineData("forbidden1", AreaChangeStatus.Forbidden, 2, 1, ForbiddenCode68 + " minutes=1")]
    [InlineData("forbidden0", AreaChangeStatus.Forbidden, 2, 0, ForbiddenCode68)]
    [InlineData("kick", AreaChangeStatus.SendFailed, 3, 0, "DNF_ONLINE stage=town event=areachange outcome=send_failed")]
    [InlineData("disconnect", AreaChangeStatus.SendFailed, 3, 0, "DNF_ONLINE stage=town event=areachange outcome=send_failed")]
    [InlineData("faulted", AreaChangeStatus.SendFailed, 3, 0, "DNF_ONLINE stage=town event=areachange outcome=send_failed")]
    public void A_failed_change_prompts_for_four_seconds_logs_the_outcome_and_puts_the_hero_back(
        string ending, AreaChangeStatus status, int promptKind, int minutes, string log)
    {
        using var s = new Scene();
        s.Rig.World.Poll(1000);
        s.Idle(300, 260, true);
        s.PressExit();
        if (ending == "faulted")
        {
            s.Rig.Transport.OnSend = _ => Task.FromException(new IOException("测试故障"));
        }
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
        switch (ending)
        {
            case "rejected":
                s.Rig.Transport.Push(KitEvents.Error(0x11));
                break;
            case "forbidden9":
                s.Rig.Transport.Push(KitEvents.Error(0x44, 9));
                break;
            case "forbidden1":
                s.Rig.Transport.Push(KitEvents.Error(0x44, 1));
                break;
            case "forbidden0":
                s.Rig.Transport.Push(KitEvents.Error(0x44, 0));
                break;
            case "kick":
                s.Rig.Transport.Push(KitEvents.Kicked(2));
                break;
            case "disconnect":
                s.Rig.Transport.RaiseDisconnected();
                break;
        }

        AreaChangeResult result = s.Rig.World.Poll(1016);

        Assert.Equal(status, result.Status);
        OnlineTownPrompt prompt = s.Rig.World.CurrentPrompt;
        Assert.Equal((OnlineTownPromptKind)promptKind, prompt.Kind);
        Assert.Equal(minutes, prompt.Minutes);
        Assert.Equal(5016L, prompt.ExpiresAtMs);   // 1016 + 4000
        Assert.Equal(new[] { log }, s.Rig.Logs);
        s.AssertBackAt(300, 260, true);
        Assert.False(s.Rig.World.IsAreaChangePending);

        // 到期:3999 毫秒后还在,4000 毫秒后消失
        s.Rig.World.Poll(5015);
        Assert.Same(prompt, s.Rig.World.CurrentPrompt);
        s.Rig.World.Poll(5016);
        Assert.Null(s.Rig.World.CurrentPrompt);
    }

    [Fact]
    public void A_timed_out_change_clears_the_prompt_logs_it_and_puts_the_hero_back_after_exactly_five_seconds()
    {
        using var s = new Scene();
        s.Rig.World.Poll(1000);
        s.Idle(300, 260, true);
        s.PressExit();
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));

        Assert.Equal(AreaChangeStatus.None, s.Rig.World.Poll(5999).Status);
        Assert.Equal(OnlineTownPromptKind.Waiting, s.Rig.World.CurrentPrompt.Kind);
        Assert.Equal(0, s.Session.ClearCalls);

        AreaChangeResult result = s.Rig.World.Poll(6000);

        Assert.Equal(AreaChangeStatus.Timeout, result.Status);
        Assert.Null(s.Rig.World.CurrentPrompt);
        Assert.Equal(new[] { "DNF_ONLINE stage=town event=areachange outcome=timeout" }, s.Rig.Logs);
        s.AssertBackAt(300, 260, true);
    }

    [Fact]
    public void A_confirmed_change_does_not_touch_the_hero_or_the_pending_exit()
    {
        using var s = new Scene();
        s.Rig.World.Poll(1000);
        s.PressExit();
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));

        Assert.Equal(AreaChangeStatus.Confirmed, s.Rig.ConfirmNormal(1, 2, 1016).Status);

        Assert.Equal(640.0, s.Hero.X);
        Assert.Equal(230.0, s.Hero.Y);
        Assert.Equal(0, s.Session.ClearCalls);
        Assert.Empty(s.Session.Updates);
        Assert.NotNull(s.Session.PendingExit);
    }

    // ───────────────────────── 提示到期(含负时钟与"0 = 永不到期") ─────────────────────────

    [Fact]
    public void A_prompt_expiring_at_a_positive_time_goes_away_exactly_then_even_on_a_negative_clock_start()
    {
        using var s = new Scene();
        s.Rig.World.Poll(-3999);
        s.Rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(1, 77);
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
        OnlineTownPrompt prompt = s.Rig.World.CurrentPrompt;
        Assert.Equal(1L, prompt.ExpiresAtMs);   // -3999 + 4000

        s.Rig.World.Poll(0);
        Assert.Same(prompt, s.Rig.World.CurrentPrompt);
        s.Rig.World.Poll(1);
        Assert.Null(s.Rig.World.CurrentPrompt);
    }

    [Fact]
    public void A_prompt_whose_expiry_is_zero_never_expires()
    {
        using var s = new Scene();
        s.Rig.World.Poll(-4000);
        s.Rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(1, 77);
        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));
        OnlineTownPrompt prompt = s.Rig.World.CurrentPrompt;
        Assert.Equal(0L, prompt.ExpiresAtMs);

        s.Rig.World.Poll(100000);

        Assert.Same(prompt, s.Rig.World.CurrentPrompt);
    }

    [Theory]
    [InlineData(OnlineTownPromptKind.None, 0, "", "")]
    [InlineData(OnlineTownPromptKind.Waiting, 0, "正在切换区域…", "CHANGING AREA...")]
    [InlineData(OnlineTownPromptKind.ForbiddenMinutes, 5, "该区域暂未开放，约 5 分钟后可进入", "AREA LOCKED (5 MIN)")]
    [InlineData(OnlineTownPromptKind.ForbiddenMinutes, 1, "该区域暂未开放，约 1 分钟后可进入", "AREA LOCKED (1 MIN)")]
    [InlineData(OnlineTownPromptKind.ForbiddenMinutes, 0, "当前暂时无法进入该区域", "AREA TEMPORARILY LOCKED")]
    [InlineData(OnlineTownPromptKind.Rejected, 0, "无法切换至目标区域，已留在当前区域", "AREA MOVE REJECTED")]
    [InlineData(OnlineTownPromptKind.UnmappedArea, 0, "目标区域暂未映射，已留在当前区域", "UNMAPPED AREA")]
    public void Prompt_messages_are_the_exact_texts_in_both_font_modes(OnlineTownPromptKind kind, int minutes, string withFonts, string withoutFonts)
    {
        var prompt = new OnlineTownPrompt(kind, minutes, 123);

        Assert.Equal(withFonts, prompt.Message(true));
        Assert.Equal(withoutFonts, prompt.Message(false));
        Assert.Equal(kind, prompt.Kind);
        Assert.Equal(minutes, prompt.Minutes);
        Assert.Equal(123L, prompt.ExpiresAtMs);
    }

    [Fact]
    public void A_prompt_defaults_to_no_minutes_and_no_expiry()
    {
        var prompt = new OnlineTownPrompt(OnlineTownPromptKind.Rejected);

        Assert.Equal(0, prompt.Minutes);
        Assert.Equal(0L, prompt.ExpiresAtMs);
    }

    // ───────────────────────── 安全位置的记录 ─────────────────────────

    [Fact]
    public void The_safe_position_follows_every_idle_frame_but_not_a_hero_pressing_an_exit_or_a_request_in_flight()
    {
        using var s = new Scene();
        s.Rig.World.Poll(1000);
        s.Idle(300, 260, true);
        s.Idle(310, 255, false);   // 空闲时每一帧都更新
        s.PressExit();             // 走进出口锁存了待触发出口:这一帧不更新

        Assert.Null(s.Rig.Through(new TownPlace(1, 1), Exit12));

        // 在途时:出口锁存被清掉、主角被挪到别处,宿主照例每帧记录,但在途时不更新
        s.Session.PendingExit = null;
        s.Hero.Place(500, 270, true, HeroMotion.Rest, 0);
        s.Rig.World.RecordSafePosition();
        s.Session.PendingExit = Exit12;

        s.Rig.Transport.Push(KitEvents.Error(0x11));
        s.Rig.World.Poll(1016);

        s.AssertBackAt(310, 255, false);
    }

    [Fact]
    public void Without_any_recorded_position_the_hero_goes_back_to_the_arrival_point_of_the_source_area()
    {
        using var rig = NewRig(heroes: true);
        // 会话一建好主角就压着出口:创建时不记录安全位置,没有已知的安全位置
        TownKitSession created = null;
        rig.Inner.Factory = place =>
        {
            TownArrival arrival = rig.Atlas.Arrive(place);
            created = new TownKitSession("kit/pressed", rig.NewHero(arrival.X, arrival.Y, arrival.FacingLeft));
            created.PendingExit = Exit12;
            return created;
        };
        rig.World.Create(new TownPlace(1, 2));
        rig.World.Poll(1000);
        rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(1, 77);

        // 来源是区域 3,落点 = 可走区域外包矩形中心 (50+500/2, 150+100/2) = (300, 200)
        Assert.Null(rig.Through(new TownPlace(1, 3), Exit12));

        Assert.Equal(300.0, created.Hero.X);
        Assert.Equal(200.0, created.Hero.Y);
        Assert.False(created.Hero.FacingLeft);
        Assert.Equal(HeroMotion.Rest, created.Hero.Motion);
    }

    [Fact]
    public void A_new_session_forgets_the_safe_position_of_the_previous_area()
    {
        using var s = new Scene();
        s.Rig.World.Poll(1000);
        s.Idle(300, 260, true);   // 旧区里的安全位置

        // 新会话一建好主角就压着出口,创建时不记录:旧的安全位置不该留下
        s.Rig.Inner.Factory = place =>
        {
            TownArrival arrival = s.Rig.Atlas.Arrive(place);
            var session = new TownKitSession("kit/new", s.Rig.NewHero(arrival.X, arrival.Y, arrival.FacingLeft));
            session.PendingExit = Exit12;
            return session;
        };
        var fresh = (TownKitSession)s.Rig.World.Create(new TownPlace(1, 2));
        s.Rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(1, 77);

        Assert.Null(s.Rig.Through(new TownPlace(1, 2), Exit12));

        // 区域 2 的落点:可走区域 (100,120,600,200) 的中心 (400, 220)
        Assert.Equal(400.0, fresh.Hero.X);
        Assert.Equal(220.0, fresh.Hero.Y);
    }

    [Fact]
    public void The_safe_position_is_read_from_an_exit_source_when_the_session_has_no_hero_yet()
    {
        using var rig = NewRig(heroes: true);
        // 建会话时没有主角,只有出口来源的位置 (330,270) 朝左 → 这就是安全位置
        rig.Inner.Factory = place => new TownKitSession("kit/nohero", null, 330, 270, true);
        var session = (TownKitSession)rig.World.Create(new TownPlace(1, 1));
        rig.World.Poll(1000);
        session.Hero = rig.NewHero(640, 230);   // 回退时才有主角
        rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(1, 77);

        Assert.Null(rig.Through(new TownPlace(1, 1), Exit12));

        Assert.Equal(330.0, session.Hero.X);
        Assert.Equal(270.0, session.Hero.Y);
        Assert.True(session.Hero.FacingLeft);
    }

    [Fact]
    public void The_safe_position_is_the_arrival_of_the_active_place_when_the_session_is_only_a_session()
    {
        using var rig = NewRig(heroes: true);
        var plain = new PlainKitSession("kit/plain");
        rig.Inner.Factory = place => plain;
        rig.World.Create(new TownPlace(1, 2));   // 活动地点是区域 2:落点 (400, 220)
        rig.World.Poll(1000);
        plain.Hero = rig.NewHero(640, 230);
        rig.Inner.ThroughBehavior = (_, _, _) => new TownPlace(1, 77);

        // 回退时给的来源是区域 3(落点 (300,200)),但已经记录的安全位置是区域 2 的落点
        Assert.Null(rig.Through(new TownPlace(1, 3), Exit12));

        Assert.Equal(400.0, plain.Hero.X);
        Assert.Equal(220.0, plain.Hero.Y);
        Assert.Equal((0.0, 0, 0), Assert.Single(plain.Updates));
    }

    [Fact]
    public void Recording_without_a_session_does_nothing()
    {
        using var rig = NewRig();

        rig.World.RecordSafePosition();
        rig.World.Poll(1000);

        Assert.Null(rig.World.CurrentPrompt);
    }

    // ───────────────────────── 强切关闭 ─────────────────────────

    private const string DisabledLine = "DNF_ONLINE stage=town event=areachange outcome=disabled";

    [Fact]
    public void With_area_sync_off_the_inner_target_is_passed_without_any_command_and_the_same_exit_logs_once()
    {
        using var rig = NewRig(areaSync: false);
        rig.World.Create(new TownPlace(1, 1));
        TownExit exit = Exit12;

        TownPlace? first = rig.Through(new TownPlace(1, 1), exit);
        TownPlace? second = rig.Through(new TownPlace(1, 1), exit);

        Assert.Equal(2, first.Value.Area);
        Assert.Equal(2, second.Value.Area);
        Assert.Empty(rig.Transport.Sent);
        Assert.Null(rig.World.CurrentPrompt);
        Assert.Equal(new[] { DisabledLine }, rig.Logs);
    }

    [Theory]
    [InlineData("from.Town")]
    [InlineData("from.Area")]
    [InlineData("exit.Town")]
    [InlineData("exit.Area")]
    [InlineData("rect.X")]
    [InlineData("rect.Y")]
    public void With_area_sync_off_a_different_exit_logs_again_and_then_dedupes_again(string changed)
    {
        using var rig = NewRig(areaSync: false);
        rig.World.Create(new TownPlace(1, 1));
        Assert.True(rig.Through(new TownPlace(1, 1), TownKit.Exit(610, 215, 70, 36, 1, 2)).HasValue);
        Assert.Single(rig.Logs);

        TownPlace from = changed == "from.Town" ? new TownPlace(2, 1) : changed == "from.Area" ? new TownPlace(1, 3) : new TownPlace(1, 1);
        TownExit exit = changed switch
        {
            "exit.Town" => TownKit.Exit(610, 215, 70, 36, 2, 2),
            "exit.Area" => TownKit.Exit(610, 215, 70, 36, 1, 3),
            "rect.X" => TownKit.Exit(611, 215, 70, 36, 1, 2),
            "rect.Y" => TownKit.Exit(610, 216, 70, 36, 1, 2),
            _ => TownKit.Exit(610, 215, 70, 36, 1, 2),
        };
        Assert.True(rig.Through(from, exit).HasValue);
        Assert.Equal(2, rig.Logs.Count);

        Assert.True(rig.Through(from, exit).HasValue);
        Assert.Equal(2, rig.Logs.Count);
        Assert.Equal(new[] { DisabledLine, DisabledLine }, rig.Logs);
    }

    [Fact]
    public void With_area_sync_off_creating_a_session_makes_the_same_exit_log_again()
    {
        using var rig = NewRig(areaSync: false);
        rig.World.Create(new TownPlace(1, 1));
        Assert.True(rig.Through(new TownPlace(1, 1), Exit12).HasValue);
        Assert.Single(rig.Logs);

        rig.World.Create(new TownPlace(1, 1));
        Assert.True(rig.Through(new TownPlace(1, 1), Exit12).HasValue);

        Assert.Equal(new[] { DisabledLine, DisabledLine }, rig.Logs);
    }

    [Fact]
    public void Area_sync_can_be_switched_on_again_and_sends_from_then_on()
    {
        using var rig = NewRig(areaSync: false);
        rig.World.Create(new TownPlace(1, 1));
        Assert.True(rig.Through(new TownPlace(1, 1), Exit12).HasValue);
        rig.Coordinator.AreaSyncEnabled = true;
        rig.World.Poll(1000);

        Assert.Null(rig.Through(new TownPlace(1, 1), Exit12));

        Assert.Single(rig.Transport.Sent);
    }

    [Fact]
    public void Without_a_log_sink_the_world_still_works()
    {
        var atlas = WorldAtlas();
        var transport = new TownKitTransport();
        using var coord = new OnlineTownCommandCoordinator(transport, TownWorldRig.SelfUid, false);
        var world = new OnlineTownWorld(new TownKitWorld(atlas, new TownPlace(1, 1)), atlas, coord);
        world.Create(new TownPlace(1, 1));

        Assert.True(world.Through(new TownPlace(1, 1), Exit12, false).HasValue);
    }
}
