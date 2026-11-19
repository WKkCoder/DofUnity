namespace Dnf.Play.Tests;

/// <summary>
/// 游戏内系统菜单"回选角"(<see cref="OnlineShellFlow.ReturnToCharacterSelect"/>):回到选频道并立刻重连当前频道取回最新角色列表。
/// 要钉住的:两步日志(进游戏阶段的 ok、选频道阶段的 enter)、重连的目标是玩家正在用的频道(不是默认频道)、
/// 请求处理完就清掉(不会在以后再次进游戏时凭空再弹一次)、没进游戏时调用无效且不被记住、与"换角色"同时到时回选角优先。
/// 重连用手动完成的任务卡住,这样"已经回到选频道、重连在途"这一刻才看得见(假后端默认立刻成功,同一次轮询里就走到选角了)。
/// </summary>
public sealed class OnlineShellReturnSelectTests
{
    private const string ReturnOk = "DNF_ONLINE stage=ingame event=ok reason=return_select";
    private const string ReturnEnter = "DNF_ONLINE stage=channel event=enter reason=return_select";

    private static TaskCompletionSource<OnlineConnectOutcome> Gate() =>
        new TaskCompletionSource<OnlineConnectOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>手动放行占位阶段:服务器与频道都由测试来选,所以"玩家正在用的频道"能和默认频道不同。</summary>
    private static ShellRig ReachInGameOn(ChannelTarget channel)
    {
        var rig = new ShellRig(o =>
        {
            o.AutoPassPlaceholders = false;
            o.DefaultChannelPort = 30055;
        });
        rig.Fill();
        rig.Submit();                                 // 登录成功 → 选服务器
        rig.Flow.ChooseServer();
        rig.Poll();
        rig.Flow.ChooseChannel(channel);
        rig.Pump(3);                                  // 连上 → 选角
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);                                  // 进游戏
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
        return rig;
    }

    [Fact]
    public void Returning_to_character_select_logs_the_ingame_step_and_then_the_channel_step()
    {
        ShellRig rig = ReachInGameOn(new ChannelTarget(31007));
        TaskCompletionSource<OnlineConnectOutcome> gate = Gate();
        rig.Backend.OnSwitch = (t, ct) => gate.Task;
        int serial = rig.Flow.StageSerial;

        rig.Flow.ReturnToCharacterSelect();
        rig.Poll();

        Assert.Equal(new[] { ReturnOk, ReturnEnter }, rig.Lines.TakeLast(2));
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.Equal(serial + 1, rig.Flow.StageSerial);
        Assert.True(rig.Flow.Busy);
    }

    [Fact]
    public void Returning_to_character_select_reconnects_the_channel_the_player_was_on_not_the_default_one()
    {
        var channel = new ChannelTarget(31007);
        ShellRig rig = ReachInGameOn(channel);
        OnlineCharacterList fresh = ShellTestData.Roster(3);
        TaskCompletionSource<OnlineConnectOutcome> gate = Gate();
        rig.Backend.OnSwitch = (t, ct) => gate.Task;
        Assert.Equal(new[] { "login", "connect", "select" }, rig.Backend.Calls);

        rig.Flow.ReturnToCharacterSelect();
        rig.Poll();

        Assert.Equal(new[] { "login", "connect", "select", "switch" }, rig.Backend.Calls);
        Assert.Equal(channel, rig.Backend.LastTarget);
        Assert.NotEqual(new ChannelTarget(30055), rig.Backend.LastTarget);
        Assert.Equal(0, rig.Backend.DisconnectCalls);

        gate.SetResult(OnlineConnectOutcome.Ok(fresh));
        rig.Pump(2);

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Same(fresh, rig.Flow.Characters);
    }

    [Fact]
    public void Without_a_chosen_channel_the_return_reconnects_the_default_channel()
    {
        var rig = new ShellRig(o => o.DefaultChannelPort = 30066);
        rig.ReachInGame();
        TaskCompletionSource<OnlineConnectOutcome> gate = Gate();
        rig.Backend.OnSwitch = (t, ct) => gate.Task;

        rig.Flow.ReturnToCharacterSelect();
        rig.Poll();

        Assert.Equal(new ChannelTarget(30066), rig.Backend.LastTarget);
        Assert.Equal(OnlineShellStage.ChannelSelect, rig.Flow.Stage);
        Assert.True(rig.Flow.Busy);
    }

    [Fact]
    public void A_handled_return_request_is_forgotten_and_does_not_fire_again_when_the_player_re_enters_the_game()
    {
        ShellRig rig = ReachInGameOn(new ChannelTarget(31007));

        rig.Flow.ReturnToCharacterSelect();
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);

        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);

        int serial = rig.Flow.StageSerial;
        rig.Pump(5);
        rig.Poll(5000);

        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
        Assert.Equal(serial, rig.Flow.StageSerial);
        Assert.Single(rig.Lines, l => l == ReturnOk);
        Assert.Single(rig.Lines, l => l == ReturnEnter);
    }

    [Fact]
    public void A_return_request_before_the_game_starts_does_nothing_and_is_not_remembered_for_later()
    {
        var rig = new ShellRig();
        rig.ReachCharacterSelect();
        int serial = rig.Flow.StageSerial;
        int calls = rig.Backend.Calls.Count;

        rig.Flow.ReturnToCharacterSelect();
        rig.Pump(3);

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(serial, rig.Flow.StageSerial);
        Assert.Equal(calls, rig.Backend.Calls.Count);
        Assert.DoesNotContain(rig.Lines, l => l.Contains("return_select"));

        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);
        rig.Pump(5);

        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
        Assert.DoesNotContain(rig.Lines, l => l.Contains("return_select"));
    }

    [Fact]
    public void When_both_requests_arrive_together_returning_to_select_wins_over_swapping_the_character()
    {
        var channel = new ChannelTarget(31007);
        ShellRig rig = ReachInGameOn(channel);
        TaskCompletionSource<OnlineConnectOutcome> gate = Gate();
        rig.Backend.OnSwitch = (t, ct) => gate.Task;

        rig.Flow.SwapCharacter();
        rig.Flow.ReturnToCharacterSelect();
        rig.Poll();

        Assert.Equal(new[] { ReturnOk, ReturnEnter }, rig.Lines.TakeLast(2));
        Assert.DoesNotContain(rig.Lines, l => l.Contains("swap_character"));
        Assert.Equal(channel, rig.Backend.LastTarget);
        Assert.True(rig.Flow.Busy);
    }

    [Fact]
    public void Swapping_the_character_alone_still_waits_at_the_channel_stage_and_connects_nothing()
    {
        ShellRig rig = ReachInGameOn(new ChannelTarget(31007));
        int calls = rig.Backend.Calls.Count;

        rig.Flow.SwapCharacter();
        rig.Poll();

        Assert.Equal(new[] { "DNF_ONLINE stage=ingame event=ok reason=swap_character", "DNF_ONLINE stage=channel event=enter reason=swap_character" },
            rig.Lines.TakeLast(2));
        Assert.Equal(calls, rig.Backend.Calls.Count);
        Assert.False(rig.Flow.Busy);
    }
}
