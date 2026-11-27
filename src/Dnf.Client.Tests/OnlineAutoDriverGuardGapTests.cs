using System.Reflection;
using Dnf.Net.Frame;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;

namespace Dnf.Client.Tests;

/// <summary>
/// r20fix-e2e C-1:无人值守驱动 <see cref="OnlineAutoDriver"/> 里"守卫条件"的补测——每个守卫的两侧各一条用例,期望值全是手写字面量。
/// 覆盖:不带 <c>-dnfonlineauto-dungeon</c> 时的默认难度、选角光标在重新进入选角页时归零、选频道页"读失败"与"没有频道"各自停手、
/// 选角页不重复发 Enter、空名册与缺名册、选图页忙时只等待、结算失败三条判据各自单独成立、指定副本不在或不可进时停手而不是确认第 0 项,
/// 外壳各阶段与进图加载画面里驱动对外报的阶段名、登录被取消即失败、频道连接被取消与退回选服务器页之后驱动不重试、进过城镇之后"已确认"旗已清,
/// 以及命令行参数解析的几处边界:副本参数的两个值恰好是最后两个参数、最短清图时间值之后的参数仍要解析、数字参数最长 9 位、数字末尾带 NUL。
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
            : this(picker, manage, !picker, characterSlot, roster)
        {
        }

        /// <summary><paramref name="autoPass"/> 为假且没有真界面(<paramref name="picker"/> 为假)时,选服务器与选频道是停下来等人的占位页。</summary>
        public Shell(bool picker, bool manage, bool autoPass, int characterSlot, params string[] roster)
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
                AutoPassPlaceholders = autoPass,
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

    [Fact]
    public void The_driver_only_waits_on_the_loading_screen_and_names_that_stage()
    {
        Assert.True(OnlineAutoOptions.TryParse(new[] { "-dnfonlineauto" }, true, out OnlineAutoOptions options, out _, out _));
        var world = new OnlineSettlementGapAKit.RecordingTownWorld(new TownPlace(1, 0), new TownPlace(1, 0));
        var flow = new GameFlow(world, (p, _) => new OnlineSettlementGapAKit.OtherRun(), new[] { Entry("d1", 9101, 100) },
            dungeonSplash: path => new SplashScene(path, null, null, null, 0, 0, 1000.0));
        flow.EnterTown("boot");
        flow.EnterSelect("test");
        var driver = new OnlineAutoDriver(options);

        // 驱动在选图页确认之后,流程先停在进图加载画面
        for (int i = 0; i < 12 && flow.Stage == GameStage.DungeonSelect; i++)
        {
            driver.StepGame(flow, null, null, 16.0, 100 + i * 16);
            flow.Poll();
        }
        Assert.Equal(GameStage.Loading, flow.Stage);
        Assert.Equal("select", driver.Stage);

        // 加载画面里驱动只改名字、什么都不做
        for (int i = 0; i < 5; i++)
        {
            driver.StepGame(flow, null, null, 16.0, 400 + i * 16);
        }

        Assert.Equal(GameStage.Loading, flow.Stage);
        Assert.Equal("loading", driver.Stage);
        Assert.False(driver.Finished);
        Assert.Null(driver.ResultLine);
    }

    [Fact]
    public void Options_parsed_from_the_bare_flag_carry_every_default_value()
    {
        Assert.True(OnlineAutoOptions.TryParse(new[] { "-dnfonlineauto" }, true, out OnlineAutoOptions bare, out string ignored, out string error));
        Assert.Null(ignored);
        Assert.Null(error);
        Assert.Equal(0, bare.CharacterSlot);
        Assert.Null(bare.DungeonId);
        Assert.Equal(0, bare.DungeonMode);
        Assert.Equal(0, bare.MinClearTimeSeconds);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, bare.SettlementChoices);
        Assert.Equal("OnlineAutoOptions(char=0,dungeon=auto,mode=0,settleCount=1,minClearSec=0)", bare.ToString());
    }

    [Theory]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-dungeon", "902", "3" }, 902, 3)]
    [InlineData(new[] { "-dnfonlineauto-dungeon", "903", "1", "-dnfonlineauto" }, 903, 1)]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-char", "5", "-dnfonlineauto-dungeon", "904", "0" }, 904, 0)]
    public void The_dungeon_flag_is_accepted_whether_or_not_its_two_values_end_the_argument_list(string[] args, int expectedDungeon, int expectedMode)
    {
        // 子参数守卫"后面至少还剩两个值":两个值恰好是最后两个参数时(守卫的边界,第一、三条)要放行;值后面还跟着别的参数时(第二条)同样放行。
        Assert.True(OnlineAutoOptions.TryParse(args, true, out OnlineAutoOptions options, out string ignored, out string error));

        Assert.Null(ignored);
        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal(expectedDungeon, options.DungeonId);
        Assert.Equal(expectedMode, options.DungeonMode);
    }

    [Theory]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-mincleartime", "7", "-dnfonlineauto-char", "4" }, 7, 4)]
    [InlineData(new[] { "-dnfonlineauto-mincleartime", "7", "-dnfonlineauto" }, 7, 0)]
    public void A_flag_right_after_the_min_clear_time_value_is_still_parsed(string[] args, int expectedSeconds, int expectedSlot)
    {
        // 最短清图时间只带一个值:值之后的下一个参数不能被一并跳过(第一条看后面的选角参数,第二条看后面的总开关)
        Assert.True(OnlineAutoOptions.TryParse(args, true, out OnlineAutoOptions options, out string ignored, out string error));

        Assert.Null(ignored);
        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal(expectedSeconds, options.MinClearTimeSeconds);
        Assert.Equal(expectedSlot, options.CharacterSlot);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("9", 9)]
    [InlineData("000000009", 9)]
    [InlineData("123456789", 123456789)]
    public void A_character_slot_of_up_to_nine_digits_is_accepted_as_written(string slotText, int expectedSlot)
    {
        // 数字参数最长 9 位:9 位整数本身是合法的(最大的一个也要放行),首尾数字 0 与 9 同样放行。
        Assert.True(OnlineAutoOptions.TryParse(new[] { "-dnfonlineauto", "-dnfonlineauto-char", slotText }, true, out OnlineAutoOptions options, out string ignored, out string error));

        Assert.Null(ignored);
        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal(expectedSlot, options.CharacterSlot);
    }

    [Theory]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-char", "1234567890" }, "BadCharSlot")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-char", "0000000009" }, "BadCharSlot")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-char", "5\0" }, "BadCharSlot")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-char", "5\0\0" }, "BadCharSlot")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-dungeon", "901\0", "1" }, "BadDungeon")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-dungeon", "901", "1\0" }, "BadDungeon")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-mincleartime", "5\0" }, "BadMinClearTime")]
    public void A_number_that_is_ten_characters_long_or_ends_in_nul_is_rejected_even_though_the_runtime_parser_would_take_it(string[] args, string expectedError)
    {
        // 前两条:10 个字符(一条是 int 放得下的 10 位数,一条是带前导零的 10 个字符)超过 9 位上限就拒,不交给运行时的整数解析;
        // 其余几条:末尾带 NUL 的数字运行时的整数解析会照单全收("5\0" 解析成 5),产品要自己逐字符检查把它拒掉。
        Assert.False(OnlineAutoOptions.TryParse(args, true, out OnlineAutoOptions options, out string ignored, out string error));

        Assert.Null(options);
        Assert.Null(ignored);
        Assert.Equal(expectedError, error);
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

    [Fact]
    public void Channel_page_that_shows_itself_busy_while_the_flow_is_idle_makes_the_driver_only_wait()
    {
        var shell = new Shell(picker: true, manage: false, characterSlot: 0, "HeroA");
        shell.RunUntilStage(OnlineShellStage.ChannelSelect);
        Assert.Equal(PickerPhase.Ready, shell.Flow.ChannelScreen.Phase);
        shell.Flow.ChannelScreen.SetBusy(ChannelBusy.Connecting);
        Assert.False(shell.Flow.ChannelScreen.CanStart);
        Assert.False(shell.Flow.Busy);

        // 只推进驱动、不轮询流程:画面的忙状态保持原样(流程自己每帧会把它同步回去)
        for (int i = 0; i < 6; i++)
        {
            shell.Driver.StepShell(shell.Flow, 500 + i);
        }

        Assert.False(shell.Driver.Finished);
        Assert.Null(shell.Driver.ResultLine);
        Assert.Equal("channel", shell.Driver.Stage);
        Assert.Equal(0, shell.Backend.ConnectCalls);
        Assert.False(shell.Flow.Busy);

        // 画面不忙了:驱动这才按下它的那一次 Enter,流程起连接
        shell.Flow.ChannelScreen.SetBusy(ChannelBusy.None);
        shell.RunUntil(() => shell.Backend.ConnectCalls > 0, maxTicks: 60);

        Assert.False(shell.Driver.Finished);
        Assert.Equal(1, shell.Backend.ConnectCalls);
    }

    [Fact]
    public void Channel_placeholder_without_a_directory_is_waited_on_and_never_failed_by_the_driver()
    {
        // 没注入目录界面、占位页也不自动放行:选服务器占位页由驱动放行,选频道占位页停在那里等人;驱动自己不判失败
        var shell = new Shell(picker: false, manage: false, autoPass: false, characterSlot: 0, "HeroA");
        shell.RunUntilStage(OnlineShellStage.ChannelSelect);
        Assert.Null(shell.Flow.ChannelScreen);
        Assert.False(shell.Flow.Busy);

        for (int i = 0; i < 8; i++)
        {
            shell.Tick();
        }

        Assert.False(shell.Driver.Finished);
        Assert.Null(shell.Driver.ResultLine);
        Assert.Equal("channel", shell.Driver.Stage);
        Assert.Equal(OnlineShellStage.ChannelSelect, shell.Flow.Stage);
        Assert.Equal(0, shell.Backend.ConnectCalls);
    }

    [Fact]
    public void Server_page_with_a_failed_directory_and_no_notice_fails_the_server_stage()
    {
        var shell = new Shell(picker: true, manage: false, characterSlot: 0, "HeroA");
        shell.RunUntilStage(OnlineShellStage.ServerSelect);
        // 只轮询流程、驱动先不动手:等目录读完、画面就绪
        for (int i = 0; i < 300 && (shell.Flow.Busy || shell.Flow.ServerScreen.Phase != PickerPhase.Ready); i++)
        {
            shell.PollOnly();
        }
        Assert.Equal(PickerPhase.Ready, shell.Flow.ServerScreen.Phase);
        shell.Flow.ServerScreen.SetFailed();
        Assert.Equal(PickerPhase.Failed, shell.Flow.ServerScreen.Phase);
        Assert.Null(shell.Flow.ServerScreen.Notice);
        Assert.Null(shell.Flow.Notice);
        Assert.False(shell.Flow.Busy);

        shell.Driver.StepShell(shell.Flow, 88);

        Assert.True(shell.Driver.Finished);
        Assert.False(shell.Driver.Succeeded);
        Assert.Equal("server", shell.Driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=server elapsedMs=88", shell.Driver.ResultLine);
        Assert.Equal(0, shell.Backend.ConnectCalls);
    }

    // ------------------------------------------------------------------ 阶段名;取消与退回之后驱动不重试

    [Fact]
    public void The_driver_names_the_shell_stage_it_is_working_on_as_soon_as_it_looks_at_it()
    {
        var selectGate = new TaskCompletionSource<OnlineSelectOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = new Shell(picker: true, manage: false, characterSlot: 0, "HeroA");
        shell.Backend.OnSelect = (slot, ct) => selectGate.Task;
        Assert.Equal("init", shell.Driver.Stage);

        // 每一段都是:流程先走到那个阶段(驱动还没看见,名字还是上一段的),驱动看一眼,名字换成这一段的
        shell.Driver.StepShell(shell.Flow, shell.Now);
        Assert.Equal(OnlineShellStage.Login, shell.Flow.Stage);
        Assert.Equal("login", shell.Driver.Stage);

        shell.RunUntilStage(OnlineShellStage.ServerSelect);
        Assert.Equal("login", shell.Driver.Stage);
        shell.Driver.StepShell(shell.Flow, shell.Now);
        Assert.Equal("server", shell.Driver.Stage);

        shell.RunUntilStage(OnlineShellStage.ChannelSelect);
        Assert.Equal("server", shell.Driver.Stage);
        shell.Driver.StepShell(shell.Flow, shell.Now);
        Assert.Equal("channel", shell.Driver.Stage);

        shell.RunUntilStage(OnlineShellStage.CharacterSelect);
        Assert.Equal("channel", shell.Driver.Stage);
        shell.Driver.StepShell(shell.Flow, shell.Now);
        Assert.Equal("charselect", shell.Driver.Stage);

        // 选角请求挂在那里:流程停在加载页
        shell.RunUntilStage(OnlineShellStage.Loading);
        Assert.Equal("charselect", shell.Driver.Stage);
        shell.Driver.StepShell(shell.Flow, shell.Now);
        Assert.Equal("loading", shell.Driver.Stage);
        Assert.False(shell.Driver.Finished);
        Assert.Null(shell.Driver.ResultLine);
        selectGate.SetResult(OnlineSelectOutcome.Ok());
    }

    [Fact]
    public void A_login_the_player_cancels_is_failed_by_the_driver_as_the_login_stage_and_never_submitted_again()
    {
        var loginGate = new TaskCompletionSource<OnlineAuthOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = new Shell(picker: false, manage: false, characterSlot: 0, "HeroA");
        shell.Backend.OnLogin = (user, password, ct) => loginGate.Task;

        // 驱动按下它的那一次 Enter,流程把登录请求发出去、挂在那里;忙的时候驱动只等
        shell.RunUntil(() => shell.Backend.LoginCalls > 0);
        Assert.Equal(1, shell.Backend.LoginCalls);
        Assert.True(shell.Flow.Busy);
        for (int i = 0; i < 5; i++)
        {
            shell.Tick();
        }
        Assert.False(shell.Driver.Finished);
        Assert.Equal(1, shell.Backend.LoginCalls);

        // 玩家按 Esc 取消:登录页回到空闲,提示是"已取消"(不算错误提示)
        shell.Flow.Key(OnlineShellKey.Escape);
        shell.PollOnly();
        Assert.Equal(OnlineShellStage.Login, shell.Flow.Stage);
        Assert.False(shell.Flow.Busy);
        Assert.NotNull(shell.Flow.Notice);
        Assert.Equal(OnlineNoticeKind.Cancelled, shell.Flow.Notice.Kind);
        Assert.False(shell.Flow.Notice.IsError);
        Assert.False(shell.Driver.Finished);

        shell.Driver.StepShell(shell.Flow, 123);

        Assert.True(shell.Driver.Finished);
        Assert.False(shell.Driver.Succeeded);
        Assert.Equal("login", shell.Driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=login elapsedMs=123", shell.Driver.ResultLine);
        Assert.Equal(new[] { "DNF_ONLINE result=fail stage=login elapsedMs=123" }, shell.ResultLines);
        Assert.Equal(1, shell.Backend.LoginCalls);
    }

    [Fact]
    public void A_channel_connect_the_player_cancels_is_never_retried_by_the_driver()
    {
        var connectGate = new TaskCompletionSource<OnlineConnectOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shell = new Shell(picker: true, manage: false, characterSlot: 0, "HeroA");
        shell.Backend.OnConnect = (target, ct) => connectGate.Task;

        // 驱动在频道页按下它的那一次 Enter,连接请求挂在那里
        shell.RunUntil(() => shell.Backend.ConnectCalls > 0);
        Assert.Equal(1, shell.Backend.ConnectCalls);
        Assert.Equal(OnlineShellStage.ChannelSelect, shell.Flow.Stage);
        Assert.True(shell.Flow.Busy);

        // 玩家按 Esc 取消:频道页回到就绪、能开始,提示是"已取消"(不算错误提示)
        shell.Flow.Key(OnlineShellKey.Escape);
        shell.PollOnly();
        Assert.Equal(OnlineShellStage.ChannelSelect, shell.Flow.Stage);
        Assert.False(shell.Flow.Busy);
        Assert.Equal(PickerPhase.Ready, shell.Flow.ChannelScreen.Phase);
        Assert.True(shell.Flow.ChannelScreen.CanStart);
        Assert.Equal(OnlineNoticeKind.Cancelled, shell.Flow.Notice.Kind);
        Assert.False(shell.Flow.Notice.IsError);

        // 之后的每一帧驱动都不再按 Enter:不重连、也不收尾(等人处理)
        for (int i = 0; i < 8; i++)
        {
            shell.Tick();
        }

        Assert.Equal(1, shell.Backend.ConnectCalls);
        Assert.Equal(OnlineShellStage.ChannelSelect, shell.Flow.Stage);
        Assert.False(shell.Driver.Finished);
        Assert.Null(shell.Driver.ResultLine);
        connectGate.SetResult(OnlineConnectOutcome.Ok(ShellTestData.Roster(2)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_server_page_the_player_backs_out_to_is_not_confirmed_a_second_time_by_the_driver(bool realPicker)
    {
        // realPicker 为真:选服务器是真界面;为假:没有目录、占位页也不自动放行(驱动调 ChooseServer 放行占位页)
        var shell = realPicker
            ? new Shell(picker: true, manage: false, characterSlot: 0, "HeroA")
            : new Shell(picker: false, manage: false, autoPass: false, characterSlot: 0, "HeroA");

        // 驱动在选服务器页确认了一次,流程来到选频道页(驱动还没看到它)
        shell.RunUntilStage(OnlineShellStage.ChannelSelect);

        // 玩家在频道页按 Esc 退回选服务器页
        shell.Flow.Key(OnlineShellKey.Escape);
        shell.PollOnly();
        Assert.Equal(OnlineShellStage.ServerSelect, shell.Flow.Stage);
        for (int i = 0; i < 300 && realPicker && (shell.Flow.Busy || shell.Flow.ServerScreen == null || shell.Flow.ServerScreen.Phase != PickerPhase.Ready); i++)
        {
            shell.PollOnly();
        }
        Assert.False(shell.Flow.Busy);

        // 之后的每一帧驱动都不再确认:流程停在选服务器页,驱动不收尾(等人处理)
        for (int i = 0; i < 8; i++)
        {
            shell.Tick();
        }

        Assert.Equal(OnlineShellStage.ServerSelect, shell.Flow.Stage);
        Assert.Equal("server", shell.Driver.Stage);
        Assert.False(shell.Driver.Finished);
        Assert.Null(shell.Driver.ResultLine);
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

    // ------------------------------------------------------------------ 选哪个角色:指定的槽位在名册里就用它,否则退回第一个能进的

    /// <summary>同一个角色的"状态字节被拦住"版本(客户端对它拦住"开始游戏")。</summary>
    private static OnlineCharacterEntry Blocked(OnlineCharacterEntry e)
    {
        return new OnlineCharacterEntry(e.Uid, e.NameBytes.ToArray(), e.X1, e.Job, e.GrowTypeByte, e.Level, e.D, OnlineCharacterRoster.BlockedStatus,
            e.Equips, e.G, e.H, e.I, e.J, e.Premium, e.K, e.A2, e.B2, e.C2, e.D2);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]
    [InlineData(7, 0)]
    public void The_configured_character_slot_is_used_when_the_roster_has_it_and_otherwise_the_first_entry(int configured, int expectedSlot)
    {
        // 三个角色:槽位 0..2 存在,等于人数的 3 与更远的 7 都不存在(不得越界读名册,退回第一个能进的)
        var shell = new Shell(picker: false, manage: false, characterSlot: configured, "HeroA", "HeroB", "HeroC");

        shell.RunUntil(() => shell.Backend.SelectCalls > 0, maxTicks: 200);

        Assert.False(shell.Driver.Finished);
        Assert.Equal(1, shell.Backend.SelectCalls);
        Assert.Equal(expectedSlot, shell.Backend.LastSlot);
    }

    [Fact]
    public void A_blocked_configured_slot_falls_back_to_the_first_enterable_entry_even_when_that_is_slot_zero()
    {
        var shell = new Shell(picker: false, manage: false, characterSlot: 2, "HeroA", "HeroB", "HeroC");
        shell.Backend.ManagedRoster[1] = Blocked(shell.Backend.ManagedRoster[1]);
        shell.Backend.ManagedRoster[2] = Blocked(shell.Backend.ManagedRoster[2]);

        shell.RunUntil(() => shell.Backend.SelectCalls > 0, maxTicks: 200);

        Assert.False(shell.Driver.Finished);
        Assert.Equal(1, shell.Backend.SelectCalls);
        Assert.Equal(0, shell.Backend.LastSlot);
    }

    [Fact]
    public void The_fallback_takes_the_first_enterable_entry_and_not_a_later_one()
    {
        var shell = new Shell(picker: false, manage: false, characterSlot: 1, "HeroA", "HeroB", "HeroC", "HeroD");
        shell.Backend.ManagedRoster[0] = Blocked(shell.Backend.ManagedRoster[0]);
        shell.Backend.ManagedRoster[1] = Blocked(shell.Backend.ManagedRoster[1]);

        shell.RunUntil(() => shell.Backend.SelectCalls > 0, maxTicks: 200);

        Assert.False(shell.Driver.Finished);
        Assert.Equal(1, shell.Backend.SelectCalls);
        Assert.Equal(2, shell.Backend.LastSlot);
    }

    // ------------------------------------------------------------------ 选角光标:目标落到光标后面时往回走

    /// <summary>
    /// 把外壳当前的角色名册换成"指定槽位被拦住"的版本(界面里的名册不变,只是驱动下一帧读到的名册变了——
    /// 与 <see cref="Missing_character_list_fails_the_charselect_stage_without_throwing"/> 同一个手法)。
    /// </summary>
    private static void BlockRosterEntries(Shell shell, params int[] slots)
    {
        var entries = new List<OnlineCharacterEntry>(shell.Flow.Characters.Entries);
        foreach (int slot in slots)
        {
            entries[slot] = Blocked(entries[slot]);
        }
        MethodInfo setter = typeof(OnlineShellFlow).GetProperty(nameof(OnlineShellFlow.Characters)).GetSetMethod(true);
        Assert.NotNull(setter);
        setter.Invoke(shell.Flow, new object[] { new OnlineCharacterList(36, 0, 0, 0u, entries, 0) });
    }

    private static Shell NineCharacterShellWalkedTo(int characterSlot, int steps)
    {
        // 九个角色排在 6 列的网格里:槽位 0..5 是第 0 行,6..8 是第 1 行。驱动只走 steps 步(都是向右、向下),还没按 Enter
        var shell = new Shell(picker: false, manage: false, characterSlot: characterSlot, "HeroA", "HeroB", "HeroC", "HeroD", "HeroE", "HeroF", "HeroG", "HeroH", "HeroI");
        shell.RunUntilStage(OnlineShellStage.CharacterSelect);
        for (int i = 0; i < steps; i++)
        {
            shell.Tick();
        }
        Assert.Equal(0, shell.Backend.SelectCalls);
        Assert.False(shell.Driver.Finished);
        return shell;
    }

    [Fact]
    public void When_the_target_moves_behind_the_cursor_the_driver_walks_back_left_and_up_and_selects_it()
    {
        // 目标是槽位 8(第 2 列第 1 行):向右、向右、向下三步走到那里,还没按 Enter。
        Shell shell = NineCharacterShellWalkedTo(characterSlot: 8, steps: 3);
        BlockRosterEntries(shell, 8);                       // 槽位 8 现在进不去,目标退回第一个能进的:槽位 0

        shell.RunUntil(() => shell.Backend.SelectCalls > 0, maxTicks: 40);

        // 向左、向左、向上三步回到 (0,0),再按 Enter:选中的是槽位 0
        Assert.False(shell.Driver.Finished);
        Assert.Equal(1, shell.Backend.SelectCalls);
        Assert.Equal(0, shell.Backend.LastSlot);
    }

    [Fact]
    public void When_only_the_row_is_ahead_of_the_target_the_driver_walks_up_and_selects_it()
    {
        // 目标是槽位 7(第 1 列第 1 行):向右、向下两步走到那里,还没按 Enter。
        Shell shell = NineCharacterShellWalkedTo(characterSlot: 7, steps: 2);
        BlockRosterEntries(shell, 0, 7);                    // 槽位 7 与 0 进不去,目标退回第一个能进的:槽位 1(同一列、上一行)

        shell.RunUntil(() => shell.Backend.SelectCalls > 0, maxTicks: 40);

        Assert.False(shell.Driver.Finished);
        Assert.Equal(1, shell.Backend.SelectCalls);
        Assert.Equal(1, shell.Backend.LastSlot);
    }

    /// <summary>驱动最后一次走完之后,只推进流程(驱动不再动),等"选角"结果落地;有界。</summary>
    private static void PollUntilSelected(Shell shell)
    {
        for (int i = 0; i < 40 && shell.Backend.SelectCalls == 0; i++)
        {
            shell.PollOnly();
        }
    }

    [Fact]
    public void Walking_back_left_and_up_takes_exactly_one_driver_tick_per_cell_and_one_more_for_enter()
    {
        // 与"目标落到光标后面"同一个局面:停在槽位 8(第 2 列第 1 行),目标退回槽位 0。驱动每个 tick 走一格:向左、向左、向上,第四个 tick 才按 Enter。
        // 只看"最后选中谁"会漏掉驱动对光标位置的记账错误——界面里的光标走到头会夹住,多按几下 Left 也停在原地、照样选中槽位 0——所以这里数 tick。
        Shell shell = NineCharacterShellWalkedTo(characterSlot: 8, steps: 3);
        BlockRosterEntries(shell, 8);

        for (int i = 0; i < 3; i++)
        {
            shell.Tick();
            Assert.Equal(0, shell.Backend.SelectCalls);
        }
        shell.Tick();                                       // 第四个 tick:驱动认为光标已在 (0,0),按 Enter
        PollUntilSelected(shell);

        Assert.False(shell.Driver.Finished);
        Assert.Equal(1, shell.Backend.SelectCalls);
        Assert.Equal(0, shell.Backend.LastSlot);
    }

    [Fact]
    public void Walking_up_takes_exactly_one_driver_tick_for_the_row_and_one_more_for_enter()
    {
        // 停在槽位 7(第 1 列第 1 行),目标退回槽位 1(同一列、上一行):一个 tick 向上,第二个 tick 按 Enter。
        Shell shell = NineCharacterShellWalkedTo(characterSlot: 7, steps: 2);
        BlockRosterEntries(shell, 0, 7);

        shell.Tick();
        Assert.Equal(0, shell.Backend.SelectCalls);
        shell.Tick();
        PollUntilSelected(shell);

        Assert.False(shell.Driver.Finished);
        Assert.Equal(1, shell.Backend.SelectCalls);
        Assert.Equal(1, shell.Backend.LastSlot);
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
    public void A_visit_to_town_clears_the_confirmed_mark_so_the_select_page_opened_there_is_confirmed_again()
    {
        // 门槛挡下确认之后,"已确认"这面旗一直挂着(上一个用例);玩家从那张新选图屏退回城镇:驱动在城镇里把旗清掉并重新请求选图页,
        // 新的选图页上驱动照常确认第二次(门槛仍然挡,所以这一步只看"确认被发出",不再往下走)
        GameFlow flow = ThreeDungeonSelect(out _);
        flow.EntryGate = _ => EntryVerdict.FatigueShort;
        var driver = new OnlineAutoDriver(new OnlineAutoOptions());

        driver.StepGame(flow, null, null, 16.0, 100);
        Assert.Equal(SelectOutcome.Enter, flow.Select.Outcome);
        flow.Poll();
        Assert.Equal(1, flow.EntryBlocks);
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal(SelectOutcome.None, flow.Select.Outcome);

        flow.Select.Back();
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.Town, flow.Stage);

        driver.StepGame(flow, null, null, 16.0, 200);       // 城镇:清旗、请求选图页
        Assert.Equal(GameStage.DungeonSelect, flow.Stage);
        Assert.Equal(SelectOutcome.None, flow.Select.Outcome);
        Assert.Equal("town", driver.Stage);

        driver.StepGame(flow, null, null, 16.0, 216);       // 新的选图页:旗已清,驱动确认
        Assert.Equal(SelectOutcome.Enter, flow.Select.Outcome);
        Assert.Equal("select", driver.Stage);
        Assert.Equal(1, flow.EntryBlocks);
        Assert.False(driver.Finished);
        Assert.Null(driver.ResultLine);
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

    [Fact]
    public void Select_screen_with_every_entry_disabled_fails_at_once_wherever_the_cursor_stands()
    {
        GameFlow flow = ThreeDungeonSelect(out _);
        flow.Select.EntryStatus = _ => new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, 5);
        flow.Select.Move(1);
        Assert.Equal(1, flow.Select.Index);
        var driver = new OnlineAutoDriver(new OnlineAutoOptions());

        driver.StepGame(flow, null, null, 16.0, 654);

        Assert.True(driver.Finished);
        Assert.False(driver.Succeeded);
        Assert.Equal("select", driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=select elapsedMs=654", driver.ResultLine);
        Assert.Equal(1, flow.Select.Index);                           // 没有朝第 0 项走
        Assert.Equal(SelectOutcome.None, flow.Select.Outcome);
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

    [Theory]
    [InlineData(SettlementChoice.Retry)]
    [InlineData(SettlementChoice.SelectDungeon)]
    public void A_return_to_town_after_a_retry_or_select_choice_is_unexpected_and_fails_the_dungeon_stage(SettlementChoice choice)
    {
        using OnlineSettlementGapAKit.DirectRig direct = NewSettlementInDungeon();
        ReachMenuReady(direct);
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(settlementChoices: new[] { choice, SettlementChoice.ReturnToTown }));

        driver.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 450);        // 驱动选了 choice(此前它没走过任何副本非结算帧)
        Assert.False(driver.Finished);
        Assert.Equal(1, driver.CompletedSettlements);

        direct.Flow.EnterTown("forced");                                    // 没有任何进图握手在途,流程却回了城镇
        driver.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 460);

        Assert.True(driver.Finished);
        Assert.False(driver.Succeeded);
        Assert.Equal(1, driver.ExitCode);
        Assert.Equal("dungeon", driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=dungeon elapsedMs=460", driver.ResultLine);
        Assert.Equal(GameStage.Town, direct.Flow.Stage);
    }

    [Fact]
    public void A_settlement_choice_the_controller_refuses_fails_the_settle_stage_and_sends_nothing()
    {
        using OnlineSettlementGapAKit.DirectRig direct = NewSettlementInDungeon();
        ReachMenuReady(direct);
        SetPrivate(direct.Ctrl, "_rejectedChoicesMask", 1 << (int)SettlementChoice.Retry);
        Assert.False(direct.Ctrl.IsChoiceEnabled(SettlementChoice.Retry));
        Assert.Equal(OnlineSettlementPhase.MenuReady, direct.Ctrl.Phase);
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(settlementChoices: new[] { SettlementChoice.Retry, SettlementChoice.ReturnToTown }));

        driver.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 470);

        AssertFailedAtSettle(driver, 470, direct);
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

    [Fact]
    public void A_requested_dungeon_without_a_select_policy_falls_back_to_the_first_enabled_entry_without_throwing()
    {
        // 没有联网进副本控制器(也就没有选图策略)就没法把副本编号解析到条目上:退回"第一个可进的条目",不抛空引用
        GameFlow flow = ThreeDungeonSelect(out DungeonEntry[] entries);
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(dungeonId: 9102));

        for (int i = 0; i < 8 && flow.Stage == GameStage.DungeonSelect; i++)
        {
            driver.StepGame(flow, null, null, 16.0, 700 + i * 16);
            flow.Poll();
        }

        Assert.False(driver.Finished);
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Equal(entries[0].Path, flow.DungeonPath);
    }
}
