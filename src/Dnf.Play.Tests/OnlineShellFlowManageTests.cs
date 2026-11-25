using Dnf.Render;

namespace Dnf.Play.Tests;

public sealed class OnlineShellFlowManageTests
{
    private static void ClickCenter(OnlineShellFlow flow, HitRect rect)
    {
        flow.Click(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    private static ShellRigWithBackend CreateManagedRig(
        bool allowWrite = true,
        bool wireManage = true,
        params (string Name, byte Job, byte Level)[] seed)
    {
        var backend = new FakeOnlineBackend { AllowCharacterWrite = allowWrite };
        backend.SeedManagedRoster(seed);
        var lines = new List<string>();
        var options = new OnlineShellOptions
        {
            Log = lines.Add,
            CharacterSelect = new OnlineCharacterSelectOptions(),
            CharacterManage = wireManage ? backend : null,
            Fonts = new SizedFonts(),
            Art = new CharacterSelectScene(new ShellFakeSprites()),
        };
        var rig = new ShellRigWithBackend(backend, lines, options);
        rig.Fill();
        rig.Submit();
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        return rig;
    }

    private sealed class ShellRigWithBackend
    {
        public readonly FakeOnlineBackend Backend;
        public readonly List<string> Lines;
        public readonly OnlineShellFlow Flow;
        public long Now = 1000;

        public ShellRigWithBackend(FakeOnlineBackend backend, List<string> lines, OnlineShellOptions options)
        {
            Backend = backend;
            Lines = lines;
            Flow = new OnlineShellFlow(backend, options);
        }

        public void Poll(long advanceMs = 0)
        {
            Now += advanceMs;
            Flow.Poll(Now);
        }

        public void Pump(int times = 4)
        {
            for (int i = 0; i < times; i++) Poll();
        }

        public void Fill(string user = "player_01", string password = "synthetic-pw")
        {
            Flow.Type(user);
            Flow.Key(OnlineShellKey.Tab);
            Flow.Type(password);
        }

        public void Submit()
        {
            Flow.Key(OnlineShellKey.Enter);
            Poll();
        }
    }

    // ------------------------------------------------------------------ 模态输入路由与隔离

    [Fact]
    public void Create_modal_captures_text_keys_and_clicks_without_affecting_background()
    {
        ShellRigWithBackend rig = CreateManagedRig(true, true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        Assert.Equal(0, rig.Flow.Characters.Entries[0].Job);

        // 点击"创立角色"按钮打开创角面板
        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Create));
        rig.Poll();
        Assert.True(rig.Flow.HasCharacterModalOpen);
        Assert.NotNull(rig.Flow.CreateModal);

        // 中文 + 退格 + 控制字符（\n 与 \x01 被过滤）
        rig.Flow.Type("新\x01角\b色\n甲");
        Assert.Equal("新色甲", rig.Flow.CreateModal.Name);

        // 方向键切职业（不移动背景角色槽位），Space 不触发进城选角
        rig.Flow.Key(OnlineShellKey.Right);
        Assert.Equal((byte)1, rig.Flow.CreateModal.SelectedJob);
        rig.Flow.Key(OnlineShellKey.Space);
        rig.Poll();
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.SelectCalls);

        // 点击背景槽位 / 频道 / 退出按钮全部被模态拦截
        CharacterSelectArt.SlotOrigin(1, out int sx, out int sy);
        rig.Flow.Click(sx + 10, sy + 10);
        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Channel));
        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Quit));
        rig.Poll();
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.False(rig.Flow.QuitRequested);
        Assert.True(rig.Flow.HasCharacterModalOpen);

        // Esc 关闭空闲面板但不退出选角界面
        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.CreateCalls);
    }

    // ------------------------------------------------------------------ 创建与删除端到端成功路径与日志脱敏

    [Fact]
    public void Create_success_refreshes_roster_selects_new_character_and_never_logs_name()
    {
        ShellRigWithBackend rig = CreateManagedRig(true, true, ("HeroA", 0, 10));

        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Create));
        rig.Poll();
        rig.Flow.Key(OnlineShellKey.Right);
        rig.Flow.Key(OnlineShellKey.Down); // job 3
        rig.Flow.Type("新角色甲");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Equal(2, rig.Flow.Characters.Entries.Count);
        Assert.Equal((byte)3, rig.Flow.Characters.Entries[1].Job);
        Assert.Equal(1, rig.Backend.CreateCalls);
        Assert.Equal("新角色甲", rig.Backend.LastCreateName);

        // 选中新创建的第 2 个角色并进入游戏，验证 SelectedSlot 已对准新角色
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.InGame, rig.Flow.Stage);
        Assert.Equal(1, rig.Backend.LastSlot);

        // DNF_ONLINE 日志不得泄露任何角色名
        Assert.All(rig.Lines, line => Assert.DoesNotContain("新角色甲", line));
        Assert.Contains(rig.Lines, line => line == "DNF_ONLINE stage=charselect event=ok reason=create_char");
    }

    [Fact]
    public void Delete_success_refreshes_roster_and_clamps_selection_to_empty()
    {
        ShellRigWithBackend rig = CreateManagedRig(true, true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        // 选中第 2 个角色并删除
        rig.Flow.Key(OnlineShellKey.Right);
        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Delete));
        rig.Poll();
        Assert.NotNull(rig.Flow.DeleteModal);
        Assert.Equal((byte)1, rig.Flow.DeleteModal.TargetSlot);

        // 输入错误确认名不发请求
        rig.Flow.Type("Wrong");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        Assert.Equal(0, rig.Backend.DeleteCalls);
        Assert.NotNull(rig.Flow.DeleteModal);

        // 退格并输入准确名字
        for (int i = 0; i < 5; i++) rig.Flow.Type("\b");
        rig.Flow.Type("HeroB");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);
        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Single(rig.Flow.Characters.Entries);

        // 再删最后一个角色 -> 名册空、显示 NoCharacters 提示、不能再点 Delete
        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Delete));
        rig.Poll();
        rig.Flow.Type("HeroA");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);
        Assert.Empty(rig.Flow.Characters.Entries);
        Assert.Equal(OnlineNoticeKind.NoCharacters, rig.Flow.Notice.Kind);
        Assert.All(rig.Lines, line => Assert.DoesNotContain("HeroA", line));
        Assert.All(rig.Lines, line => Assert.DoesNotContain("HeroB", line));
    }

    // ------------------------------------------------------------------ 失败、取消、超时、断线

    [Fact]
    public void Failure_in_modal_keeps_modal_open_with_error_message()
    {
        ShellRigWithBackend rig = CreateManagedRig(true, true, ("HeroA", 0, 10));
        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Create));
        rig.Poll();
        rig.Flow.Type("HeroA"); // 与已有角色重名
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);

        Assert.True(rig.Flow.HasCharacterModalOpen);
        Assert.False(string.IsNullOrEmpty(rig.Flow.CreateModal.ErrorText));
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void Esc_during_in_flight_create_cancels_op_without_leaving_charselect()
    {
        ShellRigWithBackend rig = CreateManagedRig(true, true, ("HeroA", 0, 10));
        var tcs = new TaskCompletionSource<OnlineCharacterManageOutcome>();
        CancellationToken observedCt = default;
        rig.Backend.OnCreateCharacter = (job, name, ct) =>
        {
            observedCt = ct;
            return tcs.Task;
        };

        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Create));
        rig.Poll();
        rig.Flow.Type("HeroNew");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        Assert.True(rig.Flow.Busy);
        Assert.True(rig.Flow.CreateModal.Busy);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        Assert.True(observedCt.IsCancellationRequested);
        Assert.False(rig.Flow.Busy);
        Assert.False(rig.Flow.CreateModal.Busy);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
    }

    [Fact]
    public void Timeout_and_disconnect_during_manage_are_handled_cleanly()
    {
        ShellRigWithBackend rig = CreateManagedRig(true, true, ("HeroA", 0, 10));
        var tcs = new TaskCompletionSource<OnlineCharacterManageOutcome>();
        rig.Backend.OnCreateCharacter = (_, _, _) => tcs.Task;

        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Create));
        rig.Poll();
        rig.Flow.Type("HeroNew");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();

        // 推进超时时间（默认 60000ms）
        rig.Poll(60000);
        Assert.False(rig.Flow.Busy);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.False(string.IsNullOrEmpty(rig.Flow.CreateModal.ErrorText));

        // 断线 -> 退回登录并清理模态
        rig.Backend.RaiseDisconnected();
        rig.Poll();
        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Equal(OnlineShellStage.Login, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.Disconnected, rig.Flow.Notice.Kind);
    }

    [Fact]
    public void When_CharacterManage_is_not_wired_or_disabled_buttons_stay_locked_and_Type_ignored_on_charselect()
    {
        ShellRigWithBackend rig = CreateManagedRig(allowWrite: false, wireManage: false, ("HeroA", 0, 10));
        rig.Flow.Type("ignored");
        ClickCenter(rig.Flow, CharacterSelectArt.ButtonRect(CharacterSelectButton.Create));
        rig.Poll();
        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Equal(0, rig.Backend.CreateCalls);
    }
}
