using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;

namespace Dnf.Client.Tests;

/// <summary>
/// r20fix-e2e C-1:无人值守驱动 <see cref="OnlineAutoDriver"/> 里"守卫条件"的补测——每个守卫的两侧各一条用例,期望值全是手写字面量。
/// 覆盖:不带 <c>-dnfonlineauto-dungeon</c> 时的默认难度、选角光标在重新进入选角页时归零、选频道页"读失败"与"没有频道"各自停手、
/// 选角页不重复发 Enter、空名册与缺名册、选图页忙时只等待、结算失败三条判据各自单独成立、指定副本不在或不可进时停手而不是确认第 0 项。
/// 外壳那几条走真的 <see cref="OnlineShellFlow"/> 加假后端;选图与结算走真的 <see cref="GameFlow"/>、选图屏、联网进副本控制器与结算控制器。全部是合成数据。
/// </summary>
public sealed class OnlineAutoDriverGuardGapTests
{
    // ------------------------------------------------------------------ 公共夹具

    private static DungeonEntry Entry(string name, int dungeonId, int markerY)
    {
        return new DungeonEntry("dungeon/test/" + name + ".dgn", name, new DungeonCard(dungeonId, 100, markerY, 0, null, 0, 1));
    }

    private static DirectoryServer OneServer()
    {
        var channel = new DirectoryChannel(1, "ch1", ChannelCongestion.Smooth, new ChannelTarget(30011, null, 1));
        return new DirectoryServer("srv1", new[] { channel });
    }

    /// <summary>选服务器、选频道真界面的目录来源:固定给一个服务器一个顺畅的频道。</summary>
    private sealed class PickerDirectory : IChannelDirectoryBackend
    {
        public int Calls { get; private set; }

        public Task<ServerDirectoryResult> LoadAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ServerDirectoryResult.Ok(new ServerDirectory(new[] { OneServer() })));
        }
    }

    /// <summary>外壳 + 假后端 + 驱动。<c>picker</c> 为真时选服务器与选频道是真界面(要停下等驱动按 Enter);<c>manage</c> 为真时装上创建/删除角色的后端与素材。</summary>
    private sealed class Shell
    {
        public readonly FakeOnlineBackend Backend = new FakeOnlineBackend();
        public readonly OnlineShellFlow Flow;
        public readonly OnlineAutoDriver Driver;
        public readonly List<string> ResultLines = new List<string>();
        public long Now = 1000;

        public Shell(bool picker, bool manage, int characterSlot, params string[] roster)
        {
            Backend.AllowCharacterWrite = manage;
            var seeded = new (string Name, byte Job, byte Level)[roster.Length];
            for (int i = 0; i < roster.Length; i++)
            {
                seeded[i] = (roster[i], (byte)0, (byte)(10 + i));
            }
            Backend.SeedManagedRoster(seeded);
            var options = new OnlineShellOptions
            {
                Configured = true,
                AutoPassPlaceholders = !picker,
                ChannelDirectory = picker ? new PickerDirectory() : null,
                CharacterSelect = new OnlineCharacterSelectOptions(),
                CharacterManage = manage ? Backend : null,
                Fonts = manage ? new SizedFonts() : null,
                Art = manage ? new CharacterSelectScene(new ShellFakeSprites()) : null,
            };
            Flow = new OnlineShellFlow(Backend, options);
            Flow.Login.Prefill("syn_user", "syn_pass");
            Driver = new OnlineAutoDriver(new OnlineAutoOptions(characterSlot: characterSlot), ResultLines.Add);
        }

        /// <summary>流程与驱动各走一步。</summary>
        public void Tick()
        {
            Now += 16;
            Flow.Poll(Now);
            Driver.StepShell(Flow, Now);
        }

        /// <summary>只推进流程。</summary>
        public void PollOnly()
        {
            Now += 16;
            Flow.Poll(Now);
        }

        /// <summary>推进到流程显示 <paramref name="stage"/> 为止:到点之后、驱动还没看到这个阶段就停下(驱动在之前的阶段里照常走)。</summary>
        public void RunUntilStage(OnlineShellStage stage)
        {
            for (int i = 0; i < 300; i++)
            {
                Now += 16;
                Flow.Poll(Now);
                if (Flow.Stage == stage)
                {
                    break;
                }
                Driver.StepShell(Flow, Now);
                Assert.False(Driver.Finished, "驱动在到达目标阶段之前就收尾了:" + Driver.ResultLine);
            }
            Assert.Equal(stage, Flow.Stage);
        }

        /// <summary>流程与驱动一起走,直到条件成立或驱动收尾。</summary>
        public void RunUntil(Func<bool> condition, int maxTicks = 300)
        {
            for (int i = 0; i < maxTicks && !condition() && !Driver.Finished; i++)
            {
                Tick();
            }
        }
    }

    private static void SetPrivate(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    private static void ClickCenter(OnlineShellFlow flow, HitRect rect)
    {
        flow.Click(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    // ------------------------------------------------------------------ 015:不带 -dnfonlineauto-dungeon 时的默认难度

    [Fact]
    public void Options_parsed_without_the_dungeon_flag_keep_mode_zero_and_no_dungeon_id()
    {
        Assert.True(OnlineAutoOptions.TryParse(new[] { "-dnfonlineauto" }, true, out OnlineAutoOptions bare, out string ignored, out string error));
        Assert.Null(ignored);
        Assert.Null(error);
        Assert.Equal(0, bare.DungeonMode);
        Assert.Null(bare.DungeonId);

        // 带了别的子参数、但没有 -dnfonlineauto-dungeon:模式照样是 0
        Assert.True(OnlineAutoOptions.TryParse(
            new[] { "-dnfonlineauto", "-dnfonlineauto-char", "2", "-dnfonlineauto-settle", "retry", "-dnfonlineauto-mincleartime", "5" },
            true, out OnlineAutoOptions others, out _, out _));
        Assert.Equal(0, others.DungeonMode);
        Assert.Null(others.DungeonId);
        Assert.Equal(2, others.CharacterSlot);
        Assert.Equal(5, others.MinClearTimeSeconds);
    }

    [Fact]
    public void Driver_built_from_the_bare_flag_stays_on_difficulty_zero_although_the_dungeon_allows_three()
    {
        Assert.True(OnlineAutoOptions.TryParse(new[] { "-dnfonlineauto" }, true, out OnlineAutoOptions options, out _, out _));
        var world = new OnlineSettlementGapAKit.RecordingTownWorld(new TownPlace(1, 0), new TownPlace(1, 0));
        var flow = new GameFlow(world, (p, _) => new OnlineSettlementGapAKit.OtherRun(), new[] { Entry("d1", 9101, 100) })
        {
            DifficultyLimit = _ => 3,
        };
        flow.EnterTown("boot");
        flow.EnterSelect("test");
        Assert.Equal(3, flow.Select.MaxDifficulty);
        Assert.Equal(0, flow.Select.Difficulty);

        var driver = new OnlineAutoDriver(options);
        for (int i = 0; i < 12 && flow.Stage == GameStage.DungeonSelect; i++)
        {
            driver.StepGame(flow, null, null, 16.0, 100 + i * 16);
            flow.Poll();
        }

        Assert.False(driver.Finished);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Equal(0, flow.Difficulty);
    }

    // ------------------------------------------------------------------ 033:重新进入选角页时光标重新从第 0 格数起

    [Fact]
    public void Re_entering_character_select_through_the_channel_page_restarts_the_cursor_walk_from_the_first_cell()
    {
        var shell = new Shell(picker: true, manage: false, characterSlot: 3, "HeroA", "HeroB", "HeroC", "HeroD");
        shell.RunUntilStage(OnlineShellStage.CharacterSelect);

        // 驱动在选角页向右走了两格(光标 0 → 2),还没按 Enter
        shell.Driver.StepShell(shell.Flow, shell.Now);
        shell.Driver.StepShell(shell.Flow, shell.Now);
        Assert.Equal(0, shell.Backend.SelectCalls);
        Assert.False(shell.Driver.Finished);

        // 玩家退回频道页、再确认同一个频道:流程换频道、重新造一张选角界面(光标回到第 0 格)
        shell.Flow.Key(OnlineShellKey.Escape);
        shell.PollOnly();
        Assert.Equal(OnlineShellStage.ChannelSelect, shell.Flow.Stage);
        shell.Flow.Key(OnlineShellKey.Enter);
        for (int i = 0; i < 20 && shell.Flow.Stage != OnlineShellStage.CharacterSelect; i++)
        {
            shell.PollOnly();
        }
        Assert.Equal(OnlineShellStage.CharacterSelect, shell.Flow.Stage);
        Assert.Equal(1, shell.Backend.SwitchCalls);
        Assert.Equal(1, shell.Backend.ConnectCalls);

        // 驱动要重新从第 0 格数起、走满三格再进游戏
        shell.RunUntil(() => shell.Flow.Stage == OnlineShellStage.InGame);

        Assert.False(shell.Driver.Finished);
        Assert.Equal(OnlineShellStage.InGame, shell.Flow.Stage);
        Assert.Equal(1, shell.Backend.SelectCalls);
        Assert.Equal(3, shell.Backend.LastSlot);
    }

    // ------------------------------------------------------------------ 036:选频道页"读失败"与"没有频道"各自停手

    public enum ChannelCatalog
    {
        Failed = 0,
        Empty = 1,
    }

    [Theory]
    [InlineData(ChannelCatalog.Failed)]
    [InlineData(ChannelCatalog.Empty)]
    public void Channel_page_with_a_failed_or_an_empty_catalog_and_no_notice_fails_the_channel_stage(ChannelCatalog catalog)
    {
        var shell = new Shell(picker: true, manage: false, characterSlot: 0, "HeroA");
        shell.RunUntilStage(OnlineShellStage.ChannelSelect);
        Assert.Equal(PickerPhase.Ready, shell.Flow.ChannelScreen.Phase);
        Assert.Null(shell.Flow.Notice);
        if (catalog == ChannelCatalog.Failed)
        {
            shell.Flow.ChannelScreen.SetFailed();
            Assert.Equal(PickerPhase.Failed, shell.Flow.ChannelScreen.Phase);
        }
        else
        {
            shell.Flow.ChannelScreen.SetServer(new DirectoryServer("empty_srv", null));
            Assert.Equal(PickerPhase.Empty, shell.Flow.ChannelScreen.Phase);
        }
        Assert.Null(shell.Flow.ChannelScreen.Notice);
        Assert.False(shell.Flow.Busy);

        shell.Driver.StepShell(shell.Flow, 77);

        Assert.True(shell.Driver.Finished);
        Assert.False(shell.Driver.Succeeded);
        Assert.Equal("channel", shell.Driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=channel elapsedMs=77", shell.Driver.ResultLine);
        Assert.Equal(new[] { "DNF_ONLINE result=fail stage=channel elapsedMs=77" }, shell.ResultLines);
        Assert.Equal(0, shell.Backend.ConnectCalls);
    }

    // ------------------------------------------------------------------ 038:选角页只按一次 Enter

    /// <summary>登录、选角名册到齐,停在选角页,删除确认面板已打开并输入了准确的名字(驱动还一次都没走过选角页)。</summary>
    private static Shell OpenDeleteConfirmation(out int deleteCallsBefore)
    {
        var shell = new Shell(picker: false, manage: true, characterSlot: 0, "HeroA", "HeroB");
        shell.Backend.OnDeleteCharacter = (slot, name, ct) =>
            Task.FromResult(OnlineCharacterManageOutcome.Fail(OnlineCharacterManageStatus.DatabaseOrUnknownError, 0x02));
        shell.RunUntilStage(OnlineShellStage.CharacterSelect);
        ClickCenter(shell.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Delete));
        shell.PollOnly();
        Assert.True(shell.Flow.HasCharacterModalOpen);
        Assert.NotNull(shell.Flow.DeleteModal);
        shell.Flow.Type("HeroA");
        Assert.True(shell.Flow.DeleteModal.IsNameMatched);
        deleteCallsBefore = shell.Backend.DeleteCalls;
        return shell;
    }

    [Fact]
    public void Driver_presses_enter_on_the_character_page_exactly_once_even_when_the_flow_stays_idle_there()
    {
        Shell shell = OpenDeleteConfirmation(out int before);
        Assert.Equal(0, before);

        // 驱动的那一次 Enter 落在删除确认面板上(请求失败、面板保持打开、面板里的名字还在);之后每一帧都不得再按
        for (int i = 0; i < 12; i++)
        {
            shell.Tick();
        }

        Assert.Equal(1, shell.Backend.DeleteCalls);
        Assert.Equal(0, shell.Backend.SelectCalls);
        Assert.Equal(OnlineShellStage.CharacterSelect, shell.Flow.Stage);
        Assert.True(shell.Flow.HasCharacterModalOpen);
        Assert.False(shell.Flow.Busy);
        Assert.False(shell.Driver.Finished);
    }

    [Fact]
    public void Driver_holds_back_enter_on_the_character_page_while_the_flow_is_busy_and_presses_it_once_afterwards()
    {
        var gate = new TaskCompletionSource<OnlineCharacterManageOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Shell shell = OpenDeleteConfirmation(out _);
        shell.Backend.OnDeleteCharacter = (slot, name, ct) => gate.Task;

        // 玩家自己按 Enter 提交删除:请求挂在那里,流程忙
        shell.Flow.Key(OnlineShellKey.Enter);
        shell.PollOnly();
        Assert.Equal(1, shell.Backend.DeleteCalls);
        Assert.True(shell.Flow.Busy);

        // 忙的时候驱动什么都不按
        for (int i = 0; i < 5; i++)
        {
            shell.Tick();
        }
        Assert.Equal(1, shell.Backend.DeleteCalls);
        Assert.True(shell.Flow.Busy);

        // 请求以失败收场、面板保持打开:流程不忙了,驱动这才按下它的第一次 Enter(面板里名字还在,于是再提交一次),此后不再按
        gate.SetResult(OnlineCharacterManageOutcome.Fail(OnlineCharacterManageStatus.DatabaseOrUnknownError, 0x02));
        for (int i = 0; i < 12; i++)
        {
            shell.Tick();
        }

        Assert.Equal(2, shell.Backend.DeleteCalls);
        Assert.Equal(0, shell.Backend.SelectCalls);
        Assert.False(shell.Flow.Busy);
        Assert.False(shell.Driver.Finished);
    }

    // ------------------------------------------------------------------ 039:缺名册与空名册各自停手

    [Fact]
    public void Missing_character_list_fails_the_charselect_stage_without_throwing()
    {
        var shell = new Shell(picker: false, manage: false, characterSlot: 0, "HeroA", "HeroB");
        shell.RunUntilStage(OnlineShellStage.CharacterSelect);
        MethodInfo setter = typeof(OnlineShellFlow).GetProperty(nameof(OnlineShellFlow.Characters)).GetSetMethod(true);
        Assert.NotNull(setter);
        setter.Invoke(shell.Flow, new object[] { null });
        Assert.Null(shell.Flow.Characters);
        Assert.Null(shell.Flow.Notice);

        shell.Driver.StepShell(shell.Flow, 9);

        Assert.True(shell.Driver.Finished);
        Assert.False(shell.Driver.Succeeded);
        Assert.Equal("charselect", shell.Driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=charselect elapsedMs=9", shell.Driver.ResultLine);
        Assert.Equal(0, shell.Backend.SelectCalls);
    }

    [Fact]
    public void Empty_character_list_fails_the_charselect_stage()
    {
        var shell = new Shell(picker: false, manage: false, characterSlot: 0);
        shell.RunUntil(() => false, maxTicks: 40);

        Assert.Empty(shell.Flow.Characters.Entries);
        Assert.True(shell.Driver.Finished);
        Assert.False(shell.Driver.Succeeded);
        Assert.Equal("charselect", shell.Driver.Stage);
        Assert.StartsWith("DNF_ONLINE result=fail stage=charselect elapsedMs=", shell.Driver.ResultLine);
        Assert.Equal(0, shell.Backend.SelectCalls);
    }

    // ------------------------------------------------------------------ 045:选图屏忙时驱动只等待

    private static GameFlow ThreeDungeonSelect(out DungeonEntry[] entries)
    {
        entries = new[] { Entry("d1", 9101, 100), Entry("d2", 9102, 200), Entry("d3", 9103, 300) };
        var world = new OnlineSettlementGapAKit.RecordingTownWorld(new TownPlace(1, 0), new TownPlace(1, 0));
        var flow = new GameFlow(world, (p, _) => new OnlineSettlementGapAKit.OtherRun(), entries)
        {
            DifficultyLimit = _ => 2,
        };
        flow.EnterTown("boot");
        flow.EnterSelect("test");
        return flow;
    }

    [Fact]
    public void Busy_select_screen_makes_the_driver_only_wait_even_when_every_entry_is_disabled()
    {
        GameFlow flow = ThreeDungeonSelect(out _);
        flow.Select.EntryStatus = _ => new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, 5);
        flow.Select.Busy = true;
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(dungeonMode: 2));

        for (int i = 0; i < 6; i++)
        {
            driver.StepGame(flow, null, null, 16.0, 100 + i * 16);
            flow.Poll();
        }

        Assert.False(driver.Finished);
        Assert.Null(driver.ResultLine);
        Assert.Equal("select", driver.Stage);
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal(0, flow.Select.Index);
        Assert.Equal(0, flow.Select.Difficulty);
        Assert.Equal(SelectOutcome.None, flow.Select.Outcome);
    }

    [Fact]
    public void Select_screen_that_stops_being_busy_lets_the_driver_move_set_difficulty_and_confirm()
    {
        GameFlow flow = ThreeDungeonSelect(out DungeonEntry[] entries);
        flow.Select.EntryStatus = e => e.Name == "d3" ? DungeonEntryStatus.Allowed : new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, 5);
        flow.Select.Busy = true;
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(dungeonMode: 2));
        for (int i = 0; i < 3; i++)
        {
            driver.StepGame(flow, null, null, 16.0, 100 + i * 16);
        }
        Assert.Equal(0, flow.Select.Index);

        flow.Select.Busy = false;
        for (int i = 0; i < 12 && flow.Stage == GameStage.DungeonSelect; i++)
        {
            driver.StepGame(flow, null, null, 16.0, 200 + i * 16);
            flow.Poll();
        }

        Assert.False(driver.Finished);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Equal(entries[2].Path, flow.DungeonPath);
        Assert.Equal(2, flow.Difficulty);
    }

    [Fact]
    public void A_confirm_blocked_by_the_entry_gate_is_never_retried_by_the_driver()
    {
        // 门槛(疲劳)把确认挡下:流程重新打开一张新的选图屏(没有错误提示、不忙);驱动不得再确认第二次——"不自动重试"
        GameFlow flow = ThreeDungeonSelect(out _);
        flow.EntryGate = _ => EntryVerdict.FatigueShort;
        var driver = new OnlineAutoDriver(new OnlineAutoOptions());

        driver.StepGame(flow, null, null, 16.0, 100);       // 选第 0 项并确认
        Assert.Equal(SelectOutcome.Enter, flow.Select.Outcome);
        int serialBefore = flow.StageSerial;
        flow.Poll();                                        // 门槛挡下:换一张新的选图屏
        Assert.Equal(1, flow.EntryBlocks);
        Assert.Equal(serialBefore + 1, flow.StageSerial);
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal(SelectOutcome.None, flow.Select.Outcome);
        Assert.False(flow.Select.Busy);
        Assert.Null(flow.Select.Notice);

        for (int i = 0; i < 10; i++)
        {
            driver.StepGame(flow, null, null, 16.0, 120 + i * 16);
            flow.Poll();
        }

        Assert.Equal(1, flow.EntryBlocks);
        Assert.Equal(SelectOutcome.None, flow.Select.Outcome);
        Assert.False(driver.Finished);
        Assert.Equal("select", driver.Stage);
    }

    [Fact]
    public void Select_screen_with_every_entry_disabled_and_not_busy_fails_the_select_stage()
    {
        GameFlow flow = ThreeDungeonSelect(out _);
        flow.Select.EntryStatus = _ => new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, 5);
        var driver = new OnlineAutoDriver(new OnlineAutoOptions());

        driver.StepGame(flow, null, null, 16.0, 321);

        Assert.True(driver.Finished);
        Assert.False(driver.Succeeded);
        Assert.Equal("select", driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=select elapsedMs=321", driver.ResultLine);
        Assert.Equal(SelectOutcome.None, flow.Select.Outcome);
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
    }

    // ------------------------------------------------------------------ 051:结算失败的三条判据各自单独成立

    private static OnlineSettlementGapAKit.DirectRig NewSettlementInDungeon()
    {
        var direct = new OnlineSettlementGapAKit.DirectRig();
        direct.Flow.EnterSelect("test");
        direct.Flow.Select.Confirm();
        direct.Flow.Poll();
        Assert.Equal(GameStage.Dungeon, direct.Flow.Stage);
        return direct;
    }

    /// <summary>结算推进到菜单可选(标准成功的 72、73 两步回复之后)。</summary>
    private static void ReachMenuReady(OnlineSettlementGapAKit.DirectRig direct)
    {
        direct.StartSettlement(timedOut: false);
        direct.PollAt(100, 16);
        direct.Transport.Push(PacketClass.Cmd, 72, new byte[] { 1 });
        direct.PollAt(120, 16);
        direct.Transport.Push(PacketClass.Cmd, 73, new byte[] { 1, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF });
        direct.PollAt(140, 16);
        direct.PollAt(160, 16);
        Assert.Equal(OnlineSettlementPhase.MenuReady, direct.Ctrl.Phase);
        Assert.False(direct.Ctrl.Summary.IsTimedOut);
        Assert.Null(direct.Ctrl.CurrentNotice);
    }

    private static void AssertFailedAtSettle(OnlineAutoDriver driver, long elapsedMs, OnlineSettlementGapAKit.DirectRig direct)
    {
        Assert.True(driver.Finished);
        Assert.False(driver.Succeeded);
        Assert.Equal(1, driver.ExitCode);
        Assert.Equal("settle", driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=settle elapsedMs=" + elapsedMs, driver.ResultLine);
        Assert.Equal(0, driver.CompletedSettlements);
        Assert.Empty(direct.Completed);
        Assert.DoesNotContain(direct.Transport.Sent, s => s.PacketId == 75);
    }

    [Fact]
    public void Degraded_menu_alone_fails_the_settle_stage()
    {
        using OnlineSettlementGapAKit.DirectRig direct = NewSettlementInDungeon();
        ReachMenuReady(direct);
        SetPrivate(direct.Ctrl, "_phase", OnlineSettlementPhase.MenuDegraded);
        Assert.Equal(OnlineSettlementPhase.MenuDegraded, direct.Ctrl.Phase);
        Assert.False(direct.Ctrl.Summary.IsTimedOut);
        Assert.Null(direct.Ctrl.CurrentNotice);
        var driver = new OnlineAutoDriver(new OnlineAutoOptions());

        driver.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 410);

        AssertFailedAtSettle(driver, 410, direct);
    }

    [Fact]
    public void Timed_out_summary_alone_fails_the_settle_stage()
    {
        using OnlineSettlementGapAKit.DirectRig direct = NewSettlementInDungeon();
        direct.StartSettlement(timedOut: true);
        direct.PollAt(100, 16);
        Assert.True(direct.Ctrl.Summary.IsTimedOut);
        SetPrivate(direct.Ctrl, "_phase", OnlineSettlementPhase.MenuReady);
        SetPrivate(direct.Ctrl, "_notice", null);
        Assert.Equal(OnlineSettlementPhase.MenuReady, direct.Ctrl.Phase);
        Assert.Null(direct.Ctrl.CurrentNotice);
        Assert.True(direct.Ctrl.Summary.IsTimedOut);
        var driver = new OnlineAutoDriver(new OnlineAutoOptions());

        driver.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 420);

        AssertFailedAtSettle(driver, 420, direct);
    }

    public static TheoryData<OnlineSettlementNoticeKind, bool> SettlementNoticeCases => new TheoryData<OnlineSettlementNoticeKind, bool>
    {
        { OnlineSettlementNoticeKind.None, false },
        { OnlineSettlementNoticeKind.Advancing, false },
        { OnlineSettlementNoticeKind.CardSelectSkipped, false },
        { OnlineSettlementNoticeKind.AdvanceNoReply, true },
        { OnlineSettlementNoticeKind.WaitingReturn, false },
        { OnlineSettlementNoticeKind.SettlementTimeout, true },
        { OnlineSettlementNoticeKind.EplpRejectedNotLeader, true },
        { OnlineSettlementNoticeKind.EplpRejectedSecretShopOpen, true },
        { OnlineSettlementNoticeKind.EplpRejectedInvalidCommand, true },
        { OnlineSettlementNoticeKind.EplpRejectedOther, true },
        { OnlineSettlementNoticeKind.EplpTimeout, true },
        { OnlineSettlementNoticeKind.ServerReturnedToTown, false },
        { OnlineSettlementNoticeKind.SendFailed, true },
        { OnlineSettlementNoticeKind.Disconnected, true },
    };

    [Theory]
    [MemberData(nameof(SettlementNoticeCases))]
    public void Settlement_notice_alone_fails_the_settle_stage_only_for_error_kinds(OnlineSettlementNoticeKind kind, bool failsTheRun)
    {
        using OnlineSettlementGapAKit.DirectRig direct = NewSettlementInDungeon();
        ReachMenuReady(direct);
        SetPrivate(direct.Ctrl, "_notice", new OnlineSettlementNotice(kind));
        Assert.Equal(kind, direct.Ctrl.CurrentNotice.Kind);
        Assert.Equal(OnlineSettlementPhase.MenuReady, direct.Ctrl.Phase);
        Assert.False(direct.Ctrl.Summary.IsTimedOut);
        var driver = new OnlineAutoDriver(new OnlineAutoOptions());

        driver.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 430);

        if (failsTheRun)
        {
            AssertFailedAtSettle(driver, 430, direct);
        }
        else
        {
            Assert.False(driver.Finished);
            Assert.Null(driver.ResultLine);
            Assert.Equal("settle", driver.Stage);
            Assert.Equal(1, driver.CompletedSettlements);
            Assert.Single(direct.Transport.Sent, s => s.PacketId == 75);
        }
    }

    [Fact]
    public void Healthy_menu_ready_settlement_is_chosen_without_failing()
    {
        using OnlineSettlementGapAKit.DirectRig direct = NewSettlementInDungeon();
        ReachMenuReady(direct);
        var driver = new OnlineAutoDriver(new OnlineAutoOptions());

        driver.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 440);

        Assert.False(driver.Finished);
        Assert.Null(driver.ResultLine);
        Assert.Equal("settle", driver.Stage);
        Assert.Equal(1, driver.CompletedSettlements);
        Assert.Single(direct.Transport.Sent, s => s.PacketId == 75);
    }

    // ------------------------------------------------------------------ 057:指定副本不在或不可进时停手,绝不确认第 0 项

    private static OnlineDungeonFlowGapKit.Rig SelectRig()
    {
        // 前两项可进;第三项(名字 c)没有本地数据,选图屏上灰显
        var rig = new OnlineDungeonFlowGapKit.Rig(
            entries: new[]
            {
                Entry("a", OnlineDungeonFlowGapKit.DungeonAId, 100),
                Entry("b", OnlineDungeonFlowGapKit.DungeonBId, 200),
                Entry("c", 9103, 300),
            },
            makePolicy: table => OnlineDungeonSelectPolicy.Create(table, hasLocalData: e => e.Name != "c", hasUltimate: _ => true));
        rig.Start();
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.False(rig.Flow.Select.StatusOf(rig.Entries[0]).Disabled);
        Assert.False(rig.Flow.Select.StatusOf(rig.Entries[1]).Disabled);
        Assert.True(rig.Flow.Select.StatusOf(rig.Entries[2]).Disabled);
        return rig;
    }

    private static void AssertFailedAtSelectWithoutConfirming(OnlineDungeonFlowGapKit.Rig rig, OnlineAutoDriver driver, long elapsedMs)
    {
        Assert.True(driver.Finished);
        Assert.False(driver.Succeeded);
        Assert.Equal("select", driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=select elapsedMs=" + elapsedMs, driver.ResultLine);
        Assert.Equal(GameStage.DungeonSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Flow.Select.Index);
        Assert.Equal(SelectOutcome.None, rig.Flow.Select.Outcome);
        Assert.False(rig.Flow.Select.Busy);
        Assert.False(rig.Flow.Poll());
        Assert.Equal(OnlineDungeonFlowPhase.Idle, rig.Ctrl.Phase);
        Assert.Equal(0, rig.Port.EnterExclusiveCalls);
    }

    [Fact]
    public void Requested_dungeon_missing_from_the_list_fails_the_select_stage_and_does_not_confirm_the_first_entry()
    {
        using OnlineDungeonFlowGapKit.Rig rig = SelectRig();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(dungeonId: 9999));

        driver.StepGame(rig.Flow, rig.Ctrl, null, 16.0, 610);

        AssertFailedAtSelectWithoutConfirming(rig, driver, 610);
    }

    [Fact]
    public void Requested_dungeon_present_but_disabled_fails_the_select_stage_and_does_not_confirm_the_first_entry()
    {
        using OnlineDungeonFlowGapKit.Rig rig = SelectRig();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(dungeonId: 9103));

        driver.StepGame(rig.Flow, rig.Ctrl, null, 16.0, 620);

        AssertFailedAtSelectWithoutConfirming(rig, driver, 620);
    }

    [Fact]
    public void Requested_enabled_dungeon_is_walked_to_and_confirmed_instead_of_the_first_entry()
    {
        using OnlineDungeonFlowGapKit.Rig rig = SelectRig();
        rig.Port.EnterExclusiveBehavior = ct => Task.Delay(Timeout.Infinite, ct);   // 握手停在入口,只看确认的是哪一项
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(dungeonId: OnlineDungeonFlowGapKit.DungeonBId));

        for (int i = 0; i < 8 && rig.Ctrl.Phase == OnlineDungeonFlowPhase.Idle; i++)
        {
            driver.StepGame(rig.Flow, rig.Ctrl, null, 16.0, 630 + i * 16);
            rig.Flow.Poll();
        }

        Assert.False(driver.Finished);
        Assert.Equal(OnlineDungeonFlowPhase.Entering, rig.Ctrl.Phase);
        Assert.Equal("dungeon/test/b.dgn", rig.Flow.DungeonPath);
        Assert.Equal(1, rig.Flow.Select.Index);
    }
}
