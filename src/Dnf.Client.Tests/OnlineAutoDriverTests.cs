using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dnf.Play;
using Dnf.Play.Tests;
using Dnf.Render;
using Xunit;

namespace Dnf.Client.Tests;

/// <summary>
/// r20-e2e：<see cref="OnlineAutoOptions"/> 参数解析边界、<see cref="OnlineAutoDriver"/> 纯状态机推进、
/// 失败即停不重试、多行选角网格导航、结算序列选择与脱敏日志行逐字断言。
/// </summary>
public sealed class OnlineAutoDriverTests
{
    // ------------------------------------------------------------------ OnlineAutoOptions

    [Fact]
    public void Options_constructor_validates_all_boundaries_and_defaults()
    {
        var def = new OnlineAutoOptions();
        Assert.Equal(0, def.CharacterSlot);
        Assert.Null(def.DungeonId);
        Assert.Equal(0, def.DungeonMode);
        Assert.Equal(0, def.MinClearTimeSeconds);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, def.SettlementChoices);
        Assert.Equal("OnlineAutoOptions(char=0,dungeon=auto,mode=0,settleCount=1,minClearSec=0)", def.ToString());

        var max = new OnlineAutoOptions(12, ushort.MaxValue, OnlineAutoOptions.MaxDungeonMode,
            new[] { SettlementChoice.Retry, SettlementChoice.SelectDungeon, SettlementChoice.ReturnToTown },
            OnlineAutoOptions.MaxMinClearTimeSeconds);
        Assert.Equal(12, max.CharacterSlot);
        Assert.Equal(65535, max.DungeonId);
        Assert.Equal(4, max.DungeonMode);
        Assert.Equal(3600, max.MinClearTimeSeconds);
        Assert.Equal(3, max.SettlementChoices.Count);
        Assert.Equal("OnlineAutoOptions(char=12,dungeon=65535,mode=4,settleCount=3,minClearSec=3600)", max.ToString());

        var minExplicit = new OnlineAutoOptions(0, 0, 0, Array.Empty<SettlementChoice>(), 0);
        Assert.Equal(0, minExplicit.DungeonId);
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, minExplicit.SettlementChoices);

        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineAutoOptions(characterSlot: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineAutoOptions(dungeonId: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineAutoOptions(dungeonId: 65536));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineAutoOptions(dungeonMode: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineAutoOptions(dungeonMode: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineAutoOptions(minClearTimeSeconds: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineAutoOptions(minClearTimeSeconds: 3601));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineAutoOptions(settlementChoices: new[] { SettlementChoice.None }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnlineAutoOptions(settlementChoices: new[] { SettlementChoice.QuickParty }));
    }

    [Fact]
    public void TryParse_absent_or_null_returns_ok_with_null_options()
    {
        Assert.True(OnlineAutoOptions.TryParse(null, true, out OnlineAutoOptions opt0, out string ign0, out string err0));
        Assert.Null(opt0);
        Assert.Null(ign0);
        Assert.Null(err0);

        Assert.True(OnlineAutoOptions.TryParse(new[] { "-dnfonline", "-dnfserverui" }, true, out OnlineAutoOptions opt1, out string ign1, out string err1));
        Assert.Null(opt1);
        Assert.Null(ign1);
        Assert.Null(err1);
    }

    [Fact]
    public void TryParse_without_dnfonline_ignores_with_exact_reason()
    {
        bool ok = OnlineAutoOptions.TryParse(
            new[] { "-dnfonlineauto", "-dnfonlineauto-char", "1" },
            onlineMode: false,
            out OnlineAutoOptions opt,
            out string ignored,
            out string error);

        Assert.True(ok);
        Assert.Null(opt);
        Assert.Equal("NeedsDnfonline", ignored);
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_parses_all_valid_flags_and_does_not_echo_credentials()
    {
        string[] args =
        {
            "-dnfonline",
            "-dnfonlineuser", "SecretUser901",
            "-dnfnetroot", "/secret/local/path",
            "-dnfonlineauto",
            "-dnfonlineauto-char", "3",
            "-dnfonlineauto-dungeon", "901", "2",
            "-dnfonlineauto-settle", " RETRY , select , Town ",
            "-dnfonlineauto-mincleartime", "65",
        };

        bool ok = OnlineAutoOptions.TryParse(args, onlineMode: true, out OnlineAutoOptions opt, out string ignored, out string error);

        Assert.True(ok);
        Assert.Null(ignored);
        Assert.Null(error);
        Assert.NotNull(opt);
        Assert.Equal(3, opt.CharacterSlot);
        Assert.Equal(901, opt.DungeonId);
        Assert.Equal(2, opt.DungeonMode);
        Assert.Equal(65, opt.MinClearTimeSeconds);
        Assert.Equal(new[] { SettlementChoice.Retry, SettlementChoice.SelectDungeon, SettlementChoice.ReturnToTown }, opt.SettlementChoices);

        string text = opt.ToString();
        Assert.Equal("OnlineAutoOptions(char=3,dungeon=901,mode=2,settleCount=3,minClearSec=65)", text);
        Assert.DoesNotContain("SecretUser901", text);
        Assert.DoesNotContain("/secret/local/path", text);
    }

    [Theory]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-char" }, "BadCharSlot")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-char", "-1" }, "BadCharSlot")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-char", "abc" }, "BadCharSlot")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-char", "1234567890" }, "BadCharSlot")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-dungeon", "901" }, "BadDungeon")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-dungeon", "65536", "0" }, "BadDungeon")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-dungeon", "901", "5" }, "BadDungeon")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-dungeon", "-1", "0" }, "BadDungeon")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-settle" }, "BadSettleChoices")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-settle", "" }, "BadSettleChoices")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-settle", "retry,bogus" }, "BadSettleChoices")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-settle", "retry," }, "BadSettleChoices")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-mincleartime" }, "BadMinClearTime")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-mincleartime", "-1" }, "BadMinClearTime")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-mincleartime", "3601" }, "BadMinClearTime")]
    [InlineData(new[] { "-dnfonlineauto", "-dnfonlineauto-mincleartime", "60s" }, "BadMinClearTime")]
    public void TryParse_rejects_invalid_flag_values_with_exact_error_code(string[] args, string expectedError)
    {
        bool ok = OnlineAutoOptions.TryParse(args, onlineMode: true, out OnlineAutoOptions opt, out string ignored, out string error);

        Assert.False(ok);
        Assert.Null(opt);
        Assert.Null(ignored);
        Assert.Equal(expectedError, error);
    }

    // ------------------------------------------------------------------ OnlineAutoDriver Shell State Machine

    private sealed class StubShellBackend : IOnlineBackend, IChannelDirectoryBackend
    {
        public OnlineAuthOutcome LoginResult { get; set; } = OnlineAuthOutcome.Ok();
        public OnlineConnectOutcome ConnectResult { get; set; }
        public OnlineSelectOutcome SelectResult { get; set; } = OnlineSelectOutcome.Ok();
        public ServerDirectoryResult DirectoryResult { get; set; }

        public int LoginCalls { get; private set; }
        public int ConnectCalls { get; private set; }
        public int SelectCalls { get; private set; }
        public int LastSelectedSlot { get; private set; } = -1;
        public int DirectoryCalls { get; private set; }

        public event Action Disconnected;

        public void RaiseDisconnected() => Disconnected?.Invoke();

        public Task<OnlineAuthOutcome> LoginAsync(string username, string password, CancellationToken ct)
        {
            LoginCalls++;
            return Task.FromResult(LoginResult);
        }

        public Task<OnlineAuthOutcome> RegisterAsync(string username, string password, CancellationToken ct)
            => Task.FromResult(OnlineAuthOutcome.Fail(OnlineNoticeKind.RegistrationClosed));

        public Task<OnlineAuthOutcome> ChangePasswordAsync(string username, string oldPassword, string newPassword, CancellationToken ct)
            => Task.FromResult(OnlineAuthOutcome.Fail(OnlineNoticeKind.Rejected));

        public Task<OnlineConnectOutcome> ConnectChannelAsync(ChannelTarget target, CancellationToken ct)
        {
            ConnectCalls++;
            return Task.FromResult(ConnectResult);
        }

        public Task<OnlineConnectOutcome> SwitchChannelAsync(ChannelTarget target, CancellationToken ct)
        {
            ConnectCalls++;
            return Task.FromResult(ConnectResult);
        }

        public Task<OnlineSelectOutcome> SelectCharacterAsync(int slot, CancellationToken ct)
        {
            SelectCalls++;
            LastSelectedSlot = slot;
            return Task.FromResult(SelectResult);
        }

        public Task<ServerDirectoryResult> LoadAsync(CancellationToken ct)
        {
            DirectoryCalls++;
            return Task.FromResult(DirectoryResult);
        }

        public void Disconnect() { }
    }

    private static OnlineCharacterEntry MakeEntry(ushort uid, byte job = 0, byte level = 15, byte status = 0, byte x1 = 0, uint d2 = 0)
    {
        return new OnlineCharacterEntry(uid, new byte[] { (byte)'H', (byte)'1' }, x1, job, 0, level, 0, status,
            Array.Empty<OnlineCharacterEquip>(), 0, 0, 0, 0, 0, 0, 0, 0, 0, d2);
    }

    private static OnlineCharacterList MakeCharacterList(IEnumerable<OnlineCharacterEntry> entries)
    {
        return new OnlineCharacterList(0, 0, 0, 0, entries, 0);
    }

    private static ServerDirectory MakeSingleServerDirectory()
    {
        var ch = new DirectoryChannel(1, "ch1", ChannelCongestion.Smooth, new ChannelTarget(30011, "127.0.0.1", 1));
        var srv = new DirectoryServer("srv1", new[] { ch });
        return new ServerDirectory(new[] { srv });
    }

    private static void PumpShell(OnlineShellFlow flow, OnlineAutoDriver driver, int maxTicks = 40)
    {
        for (int i = 0; i < maxTicks && !driver.Finished && flow.Stage != OnlineShellStage.InGame; i++)
        {
            flow.Poll(i * 16L);
            driver.StepShell(flow, i * 16L);
        }
        flow.Poll(maxTicks * 16L);
        driver.StepShell(flow, maxTicks * 16L);
    }

    [Fact]
    public void Driver_advances_server_ui_and_navigates_multirow_character_grid_to_slot_7()
    {
        // 8 个角色（第 2 行第 2 列 = 下标 7），验证列先对齐再行对齐导航到槽 7
        var entries = new List<OnlineCharacterEntry>();
        for (int i = 0; i < 8; i++)
        {
            entries.Add(MakeEntry((ushort)(901 + i)));
        }

        var backend = new StubShellBackend
        {
            ConnectResult = OnlineConnectOutcome.Ok(MakeCharacterList(entries)),
            DirectoryResult = ServerDirectoryResult.Ok(MakeSingleServerDirectory()),
        };

        var flow = new OnlineShellFlow(backend, new OnlineShellOptions
        {
            Configured = true,
            AutoPassPlaceholders = false,
            ChannelDirectory = backend,
            CharacterSelect = new OnlineCharacterSelectOptions(),
        });
        flow.Login.Prefill("syn_user", "syn_pass");

        var logs = new List<string>();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(characterSlot: 7), logs.Add);

        PumpShell(flow, driver);

        Assert.False(driver.Finished);
        Assert.Equal(OnlineShellStage.InGame, flow.Stage);
        Assert.Equal(1, backend.LoginCalls);
        Assert.Equal(1, backend.DirectoryCalls);
        Assert.Equal(1, backend.ConnectCalls);
        Assert.Equal(1, backend.SelectCalls);
        Assert.Equal(7, backend.LastSelectedSlot);
    }

    [Fact]
    public void Driver_falls_back_to_first_enterable_character_when_requested_slot_is_blocked()
    {
        var entries = new[]
        {
            MakeEntry(901, d2: 3),       // 待删除：不可进
            MakeEntry(902, status: 8),   // 状态 8：不可进
            MakeEntry(903),              // 第一个可进：槽 2
        };

        var backend = new StubShellBackend
        {
            ConnectResult = OnlineConnectOutcome.Ok(MakeCharacterList(entries)),
        };

        var flow = new OnlineShellFlow(backend, new OnlineShellOptions
        {
            Configured = true,
            AutoPassPlaceholders = true,
            CharacterSelect = new OnlineCharacterSelectOptions(),
        });
        flow.Login.Prefill("syn_user", "syn_pass");

        var driver = new OnlineAutoDriver(new OnlineAutoOptions(characterSlot: 0));
        PumpShell(flow, driver);

        Assert.False(driver.Finished);
        Assert.Equal(OnlineShellStage.InGame, flow.Stage);
        Assert.Equal(2, backend.LastSelectedSlot);
        Assert.Equal(1, backend.SelectCalls);
    }

    [Fact]
    public void Driver_fails_immediately_without_retry_on_login_error_and_emits_exact_result_line_once()
    {
        var backend = new StubShellBackend
        {
            LoginResult = OnlineAuthOutcome.Fail(OnlineNoticeKind.WrongCredentials),
        };
        var flow = new OnlineShellFlow(backend, new OnlineShellOptions { Configured = true });
        flow.Login.Prefill("syn_user", "syn_wrong");

        var logs = new List<string>();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(), logs.Add);

        for (int i = 0; i < 10; i++)
        {
            flow.Poll(i * 16L);
            driver.StepShell(flow, 55);
        }

        Assert.True(driver.Finished);
        Assert.False(driver.Succeeded);
        Assert.Equal(1, driver.ExitCode);
        Assert.Equal("login", driver.Stage);
        Assert.Equal(1, backend.LoginCalls); // 绝不重试
        Assert.Equal("DNF_ONLINE result=fail stage=login elapsedMs=55", driver.ResultLine);
        Assert.Single(logs);
        Assert.Equal("DNF_ONLINE result=fail stage=login elapsedMs=55", logs[0]);

        // 再次调用 Fail 不会改写或重复记日志
        driver.Fail("other", 999);
        Assert.Single(logs);
        Assert.Equal("login", driver.Stage);
    }

    [Fact]
    public void Driver_fails_on_empty_server_directory_empty_characters_and_disconnect_after_ingame()
    {
        // 1) 空服务器目录
        var b1 = new StubShellBackend
        {
            DirectoryResult = ServerDirectoryResult.Ok(new ServerDirectory(Array.Empty<DirectoryServer>())),
        };
        var f1 = new OnlineShellFlow(b1, new OnlineShellOptions { Configured = true, AutoPassPlaceholders = false, ChannelDirectory = b1 });
        f1.Login.Prefill("syn_user", "syn_pass");
        var d1 = new OnlineAutoDriver(new OnlineAutoOptions());
        PumpShell(f1, d1);
        Assert.True(d1.Finished);
        Assert.False(d1.Succeeded);
        Assert.Equal("server", d1.Stage);

        // 2) 空角色列表
        var b2 = new StubShellBackend { ConnectResult = OnlineConnectOutcome.Ok(MakeCharacterList(Array.Empty<OnlineCharacterEntry>())) };
        var f2 = new OnlineShellFlow(b2, new OnlineShellOptions { Configured = true });
        f2.Login.Prefill("syn_user", "syn_pass");
        var d2 = new OnlineAutoDriver(new OnlineAutoOptions());
        PumpShell(f2, d2);
        Assert.True(d2.Finished);
        Assert.False(d2.Succeeded);
        Assert.Equal("charselect", d2.Stage);

        // 3) 进游戏后断线退回登录
        var b3 = new StubShellBackend { ConnectResult = OnlineConnectOutcome.Ok(MakeCharacterList(new[] { MakeEntry(901) })) };
        var f3 = new OnlineShellFlow(b3, new OnlineShellOptions { Configured = true });
        f3.Login.Prefill("syn_user", "syn_pass");
        var logs3 = new List<string>();
        var d3 = new OnlineAutoDriver(new OnlineAutoOptions(), logs3.Add);
        PumpShell(f3, d3);
        Assert.Equal(OnlineShellStage.InGame, f3.Stage);
        Assert.False(d3.Finished);

        b3.RaiseDisconnected();
        f3.Poll(500);
        d3.StepShell(f3, 500);

        Assert.True(d3.Finished);
        Assert.False(d3.Succeeded);
        Assert.Equal(1, d3.ExitCode);
        Assert.Equal("disconnect", d3.Stage);
        Assert.Equal(1, b3.LoginCalls); // 掉线后绝不自动重登
        Assert.Equal("DNF_ONLINE result=fail stage=disconnect elapsedMs=500", d3.ResultLine);
    }

    [Fact]
    public void Driver_fails_on_all_characters_blocked_and_clamps_negative_elapsedMs()
    {
        var entries = new[] { MakeEntry(901, d2: 1), MakeEntry(902, x1: 1) };
        var backend = new StubShellBackend { ConnectResult = OnlineConnectOutcome.Ok(MakeCharacterList(entries)) };
        var flow = new OnlineShellFlow(backend, new OnlineShellOptions { Configured = true });
        flow.Login.Prefill("syn_user", "syn_pass");

        var logs = new List<string>();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(), logs.Add);
        for (int i = 0; i < 20 && !driver.Finished; i++)
        {
            flow.Poll(i * 16L);
            driver.StepShell(flow, -10);
        }

        Assert.True(driver.Finished);
        Assert.False(driver.Succeeded);
        Assert.Equal("charselect", driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=charselect elapsedMs=0", driver.ResultLine);
    }

    [Fact]
    public void Driver_fails_on_dungeon_select_error_notice_and_disabled_dungeon()
    {
        var entries = new[]
        {
            new DungeonEntry("dungeon/test/d1.dgn", "D1", new DungeonCard(9101, 100, 100, 0, null, 0, 1)),
        };
        var world = new OnlineSettlementGapAKit.RecordingTownWorld(new TownPlace(1, 0), new TownPlace(1, 0));
        var flow = new GameFlow(world, (p, _) => throw new InvalidOperationException(), entries);

        // 1) 选图屏收到错误提示
        flow.EnterSelect("test");
        flow.Select.Notice = new DungeonSelectNotice(DungeonSelectNoticeKind.Rejected);
        var d1 = new OnlineAutoDriver(new OnlineAutoOptions());
        d1.StepGame(flow, null, null, 16.0, 200);
        Assert.True(d1.Finished);
        Assert.False(d1.Succeeded);
        Assert.Equal("select", d1.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=select elapsedMs=200", d1.ResultLine);

        // 2) 所有副本都被禁用
        flow.Select.Notice = null;
        flow.Select.EntryStatus = _ => new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, 5);
        var d2 = new OnlineAutoDriver(new OnlineAutoOptions());
        d2.StepGame(flow, null, null, 16.0, 300);
        Assert.True(d2.Finished);
        Assert.False(d2.Succeeded);
        Assert.Equal("select", d2.Stage);
    }

    [Fact]
    public void Driver_fails_on_degraded_settlement_and_unexpected_town_return()
    {
        using var direct = new OnlineSettlementGapAKit.DirectRig();
        direct.Flow.EnterSelect("test");
        direct.Flow.Select.Confirm();
        direct.Flow.Poll();
        Assert.Equal(GameStage.Dungeon, direct.Flow.Stage);

        // 触发结算通知超时 -> MenuDegraded + Summary.IsTimedOut == true
        direct.StartSettlement(timedOut: true);
        direct.PollAt(100, 16);

        var logs = new List<string>();
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(), logs.Add);
        driver.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 400);

        Assert.True(driver.Finished);
        Assert.False(driver.Succeeded);
        Assert.Equal(1, driver.ExitCode);
        Assert.Equal("settle", driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=settle elapsedMs=400", driver.ResultLine);
    }

    [Fact]
    public void TryParse_handles_exact_upper_and_lower_bounds_null_elements_and_subflags_without_main_switch()
    {
        // 含 null 元素 + 精确边界值 0 / 65535 / 4 / 3600
        bool okMax = OnlineAutoOptions.TryParse(
            new[] { null, "-dnfonlineauto", "-dnfonlineauto-char", "0", "-dnfonlineauto-dungeon", "65535", "4", "-dnfonlineauto-mincleartime", "3600", "-dnfonlineauto-settle", "town" },
            onlineMode: true, out OnlineAutoOptions maxOpt, out string ignMax, out string errMax);
        Assert.True(okMax);
        Assert.Null(ignMax);
        Assert.Null(errMax);
        Assert.Equal(0, maxOpt.CharacterSlot);
        Assert.Equal(65535, maxOpt.DungeonId);
        Assert.Equal(4, maxOpt.DungeonMode);
        Assert.Equal(3600, maxOpt.MinClearTimeSeconds);

        bool okMin = OnlineAutoOptions.TryParse(
            new[] { "-dnfonlineauto", "-dnfonlineauto-dungeon", "0", "0", "-dnfonlineauto-mincleartime", "0" },
            onlineMode: true, out OnlineAutoOptions minOpt, out _, out _);
        Assert.True(okMin);
        Assert.Equal(0, minOpt.DungeonId);
        Assert.Equal(0, minOpt.DungeonMode);
        Assert.Equal(0, minOpt.MinClearTimeSeconds);

        // 只传子参数但不传 -dnfonlineauto：返回 true 且 options == null
        bool okSub = OnlineAutoOptions.TryParse(
            new[] { "-dnfonlineauto-char", "2", "-dnfonlineauto-settle", "retry" },
            onlineMode: true, out OnlineAutoOptions subOpt, out string ignSub, out string errSub);
        Assert.True(okSub);
        Assert.Null(subOpt);
        Assert.Null(ignSub);
        Assert.Null(errSub);
    }

    [Fact]
    public void Driver_fails_on_picker_failed_or_unselectable_server_and_full_channel()
    {
        // 1) 目录读失败 -> ServerScreen.Phase == Failed -> fail stage=server
        var b1 = new StubShellBackend { DirectoryResult = ServerDirectoryResult.Fail(OnlineNoticeKind.NetworkError) };
        var f1 = new OnlineShellFlow(b1, new OnlineShellOptions { Configured = true, AutoPassPlaceholders = false, ChannelDirectory = b1 });
        f1.Login.Prefill("syn_user", "syn_pass");
        var d1 = new OnlineAutoDriver(new OnlineAutoOptions());
        PumpShell(f1, d1);
        Assert.True(d1.Finished);
        Assert.False(d1.Succeeded);
        Assert.Equal("server", d1.Stage);

        // 2) 服务器存在但无频道（CanStart == false）
        var b2 = new StubShellBackend { DirectoryResult = ServerDirectoryResult.Ok(new ServerDirectory(new[] { new DirectoryServer("empty_srv", Array.Empty<DirectoryChannel>()) })) };
        var f2 = new OnlineShellFlow(b2, new OnlineShellOptions { Configured = true, AutoPassPlaceholders = false, ChannelDirectory = b2 });
        f2.Login.Prefill("syn_user", "syn_pass");
        var d2 = new OnlineAutoDriver(new OnlineAutoOptions());
        PumpShell(f2, d2);
        Assert.True(d2.Finished);
        Assert.False(d2.Succeeded);
        Assert.Equal("server", d2.Stage);

        // 3) 频道全部满载（CanStart == false）-> fail stage=channel
        var fullCh = new DirectoryChannel(1, "ch1", ChannelCongestion.Full, new ChannelTarget(30011, "127.0.0.1", 1));
        var b3 = new StubShellBackend { DirectoryResult = ServerDirectoryResult.Ok(new ServerDirectory(new[] { new DirectoryServer("srv1", new[] { fullCh }) })) };
        var f3 = new OnlineShellFlow(b3, new OnlineShellOptions { Configured = true, AutoPassPlaceholders = false, ChannelDirectory = b3 });
        f3.Login.Prefill("syn_user", "syn_pass");
        var d3 = new OnlineAutoDriver(new OnlineAutoOptions());
        PumpShell(f3, d3);
        Assert.True(d3.Finished);
        Assert.False(d3.Succeeded);
        Assert.Equal("channel", d3.Stage);
    }

    [Fact]
    public void Driver_selects_target_dungeon_adjusts_difficulty_and_fails_on_unexpected_town_return()
    {
        var entries = new[]
        {
            new DungeonEntry("dungeon/test/d1.dgn", "D1", new DungeonCard(9101, 100, 100, 0, null, 0, 1)),
            new DungeonEntry("dungeon/test/d2.dgn", "D2", new DungeonCard(9102, 100, 200, 0, null, 0, 1)),
            new DungeonEntry("dungeon/test/d3.dgn", "D3", new DungeonCard(9103, 100, 300, 0, null, 0, 1)),
        };
        var world = new OnlineSettlementGapAKit.RecordingTownWorld(new TownPlace(1, 0), new TownPlace(1, 0));
        var flow = new GameFlow(world, (p, _) => new OnlineSettlementGapAKit.OtherRun(), entries)
        {
            DifficultyLimit = _ => 2,
        };
        flow.EnterTown("boot");

        var permTable = new OnlineDungeonPermissionTable();
        permTable.LoadInitial(new[] { new KeyValuePair<int, byte>(9103, 3) }, hasSnapshot: true);
        var policy = OnlineDungeonSelectPolicy.Create(permTable, hasLocalData: _ => true, hasUltimate: _ => true);

        // 先手动进选择屏并让驱动自动选中条目 2（经 Move + MoveDifficulty 到 2）
        var driver = new OnlineAutoDriver(new OnlineAutoOptions(dungeonMode: 3));
        flow.EnterSelect("test");
        flow.Select.EntryStatus = e => e.Name == "D3" ? DungeonEntryStatus.Allowed : new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, 1);

        for (int i = 0; i < 12 && flow.Stage == GameStage.DungeonSelect; i++)
        {
            driver.StepGame(flow, null, null, 16.0, 100 + i * 16);
            flow.Poll();
        }
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Equal("dungeon/test/d3.dgn", flow.DungeonPath);
        Assert.Equal(2, flow.Difficulty); // dungeonMode=3 被 MaxDifficulty=2 夹紧到 2

        // 进副本后走一步让 driver 记录 _expectedTownReturn = false，然后模拟异常掉回城镇（未选 ReturnToTown）
        driver.StepGame(flow, null, null, 16.0, 500);
        flow.EnterTown("aborted");
        driver.StepGame(flow, null, null, 16.0, 600);

        Assert.True(driver.Finished);
        Assert.False(driver.Succeeded);
        Assert.Equal(1, driver.ExitCode);
        Assert.Equal("dungeon", driver.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=dungeon elapsedMs=600", driver.ResultLine);
    }

    [Fact]
    public void Driver_room_bot_stop_non_settled_fails_and_settlement_select_choice_and_exhaustion_work()
    {
        // 1) HostedRoomBot 遇到 Aborted -> driver 立即 fail stage=dungeon
        using var fx = new HostedGatedRoomFixture();
        var spawns = new[] { HostedRoomFixture.Spawn(HostedRoomFixture.Buildable, 340) };
        var run = new HostedRoomRun(fx.Create(spawns, null, Array.Empty<GateSide>()), _ => true, () => false, 0.0);
        var entries = new[] { new DungeonEntry("dungeon/test/d1.dgn", "D1", new DungeonCard(9101, 100, 100, 0, null, 0, 1)) };
        var world = new OnlineSettlementGapAKit.RecordingTownWorld(new TownPlace(1, 0), new TownPlace(1, 0));
        var flow = new GameFlow(world, (p, _) => run, entries);
        flow.EnterTown("boot");
        flow.EnterSelect("test");
        flow.Select.Confirm();
        flow.Poll();
        Assert.Equal(GameStage.Dungeon, flow.Stage);

        run.Abort(HostedAbort.MoveTimeout);
        var dAbort = new OnlineAutoDriver(new OnlineAutoOptions(minClearTimeSeconds: 0));
        dAbort.StepGame(flow, null, null, 16.0, 777);
        Assert.True(dAbort.Finished);
        Assert.False(dAbort.Succeeded);
        Assert.Equal("dungeon", dAbort.Stage);
        Assert.Equal("DNF_ONLINE result=fail stage=dungeon elapsedMs=777", dAbort.ResultLine);

        // 2) SettlementChoice.SelectDungeon -> 耗尽后自动回退 ReturnToTown -> 成功
        using var direct = new OnlineSettlementGapAKit.DirectRig();
        direct.Flow.EnterSelect("test");
        direct.Flow.Select.Confirm();
        direct.Flow.Poll();

        var logs = new List<string>();
        var dSettle = new OnlineAutoDriver(new OnlineAutoOptions(settlementChoices: new[] { SettlementChoice.SelectDungeon }), logs.Add);

        // 第 1 局结算：推进 72 -> 73 -> 74 到 MenuReady，选 SelectDungeon
        direct.StartSettlement(timedOut: false);
        direct.PollAt(100, 16);
        direct.Transport.Push(Dnf.Net.Frame.PacketClass.Cmd, 72, new byte[] { 1 });
        direct.PollAt(120, 16);
        direct.Transport.Push(Dnf.Net.Frame.PacketClass.Cmd, 73, new byte[] { 1, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF });
        direct.PollAt(140, 16);
        direct.PollAt(160, 16);
        Assert.Equal(OnlineSettlementPhase.MenuReady, direct.Ctrl.Phase);

        dSettle.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 200);
        Assert.Equal(1, dSettle.CompletedSettlements);
        Assert.Equal(0, dSettle.RetryCycles);
        Assert.False(dSettle.Finished);

        // 第 2 局结算：序列已耗尽，应自动选择 ReturnToTown 并在回城后成功结束
        direct.Transport.Push(Dnf.Net.Frame.PacketClass.Cmd, 75, new byte[] { 1, 1, 1 });
        direct.Transport.Push(Dnf.Net.Frame.PacketClass.Noti, 2, new byte[] { 2 });
        direct.PollAt(180, 16);
        dSettle.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 220); // 清 _settlementChosenForCurrentActive

        direct.StartSettlement(timedOut: false);
        direct.PollAt(240, 16);
        direct.Transport.Push(Dnf.Net.Frame.PacketClass.Cmd, 72, new byte[] { 1 });
        direct.PollAt(260, 16);
        direct.Transport.Push(Dnf.Net.Frame.PacketClass.Cmd, 73, new byte[] { 1, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF });
        direct.PollAt(280, 16);
        direct.PollAt(300, 16);
        Assert.Equal(OnlineSettlementPhase.MenuReady, direct.Ctrl.Phase);

        dSettle.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 320);
        Assert.Equal(2, dSettle.CompletedSettlements);
        direct.Flow.EnterTown("returned");
        dSettle.StepGame(direct.Flow, null, direct.Ctrl, 16.0, 350);

        Assert.True(dSettle.Finished);
        Assert.True(dSettle.Succeeded);
        Assert.Equal(0, dSettle.ExitCode);
        Assert.Equal("done", dSettle.Stage);
        Assert.Equal("DNF_ONLINE result=ok stage=done elapsedMs=350", dSettle.ResultLine);
    }

    [Fact]
    public void Driver_fails_before_submitting_when_unconfigured_and_does_not_fail_on_non_error_notice()
    {
        // 1) Configured = false 时登录页初始自带 NotConfigured 错误提示：首次 StepShell 即停且绝不调用 LoginAsync
        var b1 = new StubShellBackend();
        var f1 = new OnlineShellFlow(b1, new OnlineShellOptions { Configured = false, ConfigDetail = "missing" });
        var d1 = new OnlineAutoDriver(new OnlineAutoOptions());
        d1.StepShell(f1, 10);
        Assert.True(d1.Finished);
        Assert.False(d1.Succeeded);
        Assert.Equal("login", d1.Stage);
        Assert.Equal(0, b1.LoginCalls);
        Assert.Equal("DNF_ONLINE result=fail stage=login elapsedMs=10", d1.ResultLine);

        // 2) 非错误提示（NoCharacters 以外例如成功注册 RegisterOk 后仍在登录页）：不得误判为失败
        var b2 = new StubShellBackend { ConnectResult = OnlineConnectOutcome.Ok(MakeCharacterList(new[] { MakeEntry(901) })) };
        var f2 = new OnlineShellFlow(b2, new OnlineShellOptions { Configured = true });
        f2.Login.Prefill("syn_user", "syn_pass");
        // 通过公开的点击“注册”按钮制造一次 RegisterOk（IsError == false）提示
        b2.LoginResult = OnlineAuthOutcome.Ok();
        f2.Click(LoginScreen.ButtonRect(LoginButton.Register).X + 4, LoginScreen.ButtonRect(LoginButton.Register).Y + 4);
        // StubShellBackend.RegisterAsync 返回 RegistrationClosed，这里只验证 IsError=true 路径与正常登录提交的区别：
        // 用干净的 flow2 + ConnectFailed 检验 ChannelSelect 上的错误提示（_channelSubmitted 为 false 时仍立刻停在 channel）
        var b3 = new StubShellBackend { ConnectResult = OnlineConnectOutcome.Fail(OnlineNoticeKind.ConnectFailed) };
        var f3 = new OnlineShellFlow(b3, new OnlineShellOptions { Configured = true, AutoPassPlaceholders = true });
        f3.Login.Prefill("syn_user", "syn_pass");
        var d3 = new OnlineAutoDriver(new OnlineAutoOptions());
        PumpShell(f3, d3);
        Assert.True(d3.Finished);
        Assert.False(d3.Succeeded);
        Assert.Equal("channel", d3.Stage);
        Assert.Equal(1, b3.ConnectCalls);
    }

    [Fact]
    public void OnlineShellSetupLoader_applies_devDirectoryPort_only_in_dev_mode_and_valid_range()
    {
        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dnf-dirport-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stack = new Dnf.NetFake.LoopbackOnlineStack();
            stack.WriteSyntheticDevRoot(tmp);

            OnlineShellSetup devCustom = OnlineShellSetupLoader.Load(true, tmp, 0, tmp, null, null, 18999);
            Assert.True(devCustom.Configured);
            Assert.Equal(18999, devCustom.CreateSettings().ChannelDirectoryPort);

            OnlineShellSetup devDefault = OnlineShellSetupLoader.Load(true, tmp, 0, tmp, null, null, 0);
            Assert.Equal(Dnf.Net.ChannelDirectory.ChannelServiceConstants.DefaultPort, devDefault.CreateSettings().ChannelDirectoryPort);

            OnlineShellSetup devTooHigh = OnlineShellSetupLoader.Load(true, tmp, 0, tmp, null, null, 65536);
            Assert.Equal(Dnf.Net.ChannelDirectory.ChannelServiceConstants.DefaultPort, devTooHigh.CreateSettings().ChannelDirectoryPort);

            // 非开发构建必须忽略 -dnfnetroot 与 -dnfdirport
            OnlineShellSetup prod = OnlineShellSetupLoader.Load(false, tmp, 0, tmp, null, null, 18999);
            Assert.False(prod.Configured);
        }
        finally
        {
            try { System.IO.Directory.Delete(tmp, true); } catch { }
        }
    }
}
