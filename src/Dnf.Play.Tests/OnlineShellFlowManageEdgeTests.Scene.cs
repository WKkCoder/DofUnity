using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网外壳"创建/删除角色"流程与画面、输入的接线补充测试(r20fix-createcharui,相邻点补测):
/// 画面只在面板开着的时候画面板、面板开着时底下的选角界面收不到点击、预览头像的时钟跟着宿主每帧推进。
/// 夹具(Rig、CreateRig、日志常量)在 <c>OnlineShellFlowManageEdgeTests.cs</c>。
/// </summary>
public sealed partial class OnlineShellFlowManageEdgeTests
{
    private static int Drawn(OnlineShellFlow flow, string tag) => SpriteQuery.Tagged(flow.Scene.Build(0), tag).Count;

    /// <summary>记下每次被要求摆的姿势(名册里的名字、动画时间);每次回一个 1×1 的精灵。创建面板的预览头像用的名字是空串。</summary>
    private sealed class PoseRecorder : ICharacterAvatars
    {
        public List<(string Name, long AgeMs)> Poses { get; } = new List<(string, long)>();

        public IReadOnlyList<SceneSprite> Pose(CharacterSlot slot, long ageMs, int footX, int footY)
        {
            Poses.Add((slot.Name, ageMs));
            return new[] { new SceneSprite(new SpriteFrame(1, 1, 0, 0, 1, 1, new byte[4]), footX, footY, 0, 0, "fake/avatar", 0) };
        }
    }

    /// <summary>与 <see cref="CreateRig"/> 一样登录进选角,只是选角素材带着给定的头像来源(写开关与接线都是开的)。</summary>
    private static Rig CreateRigWithAvatars(ICharacterAvatars avatars, params (string Name, byte Job, byte Level)[] seed)
    {
        var backend = new FakeOnlineBackend { AllowCharacterWrite = true };
        backend.SeedManagedRoster(seed);
        var lines = new List<string>();
        var options = new OnlineShellOptions
        {
            Log = lines.Add,
            CharacterSelect = new OnlineCharacterSelectOptions(),
            CharacterManage = backend,
            Fonts = new SizedFonts(),
            Art = new CharacterSelectScene(new ShellFakeSprites(), avatars),
        };
        var rig = new Rig(backend, lines, options);
        rig.Flow.Type("player_01");
        rig.Flow.Key(OnlineShellKey.Tab);
        rig.Flow.Type("synthetic-pw");
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Poll();
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        return rig;
    }

    // ------------------------------------------------------------------ 画面只在面板开着时画面板

    [Fact]
    public void The_scene_draws_the_create_modal_exactly_while_it_is_open()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        Assert.Equal(0, Drawn(rig.Flow, "create-modal-panel"));

        rig.ClickCreate();

        Assert.Equal(1, Drawn(rig.Flow, "create-modal-dim"));
        Assert.Equal(1, Drawn(rig.Flow, "create-modal-panel"));
        Assert.Equal(0, Drawn(rig.Flow, "delete-modal-panel"));

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Equal(0, Drawn(rig.Flow, "create-modal-dim"));
        Assert.Equal(0, Drawn(rig.Flow, "create-modal-panel"));
    }

    [Fact]
    public void The_scene_draws_the_delete_modal_exactly_while_it_is_open()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        Assert.Equal(0, Drawn(rig.Flow, "delete-modal-panel"));

        rig.ClickDelete();

        Assert.Equal(1, Drawn(rig.Flow, "delete-modal-dim"));
        Assert.Equal(1, Drawn(rig.Flow, "delete-modal-panel"));
        Assert.Equal(0, Drawn(rig.Flow, "create-modal-panel"));

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();

        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Equal(0, Drawn(rig.Flow, "delete-modal-dim"));
        Assert.Equal(0, Drawn(rig.Flow, "delete-modal-panel"));
    }

    [Fact]
    public void Closing_one_modal_and_opening_the_other_swaps_what_the_scene_draws()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));

        rig.ClickCreate();
        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        rig.ClickDelete();

        Assert.Equal(0, Drawn(rig.Flow, "create-modal-panel"));
        Assert.Equal(1, Drawn(rig.Flow, "delete-modal-panel"));

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        rig.ClickCreate();

        Assert.Equal(1, Drawn(rig.Flow, "create-modal-panel"));
        Assert.Equal(0, Drawn(rig.Flow, "delete-modal-panel"));
    }

    // ------------------------------------------------------------------ 面板开着时,底下的选角界面收不到点击

    /// <summary>
    /// 面板开着,点到底下 1 号角色格上的一下被面板吞掉:关掉面板之后回车进游戏进的还是 0 号,不是 1 号。
    /// (点的位置不在面板的确认/取消钮上,面板自己不会因此关闭或提交。)
    /// </summary>
    [Fact]
    public void A_click_on_a_character_cell_under_an_open_create_modal_does_not_move_the_selection()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        rig.ClickCreate();
        CharacterSelectArt.SlotOrigin(1, out int cellX, out int cellY);
        int x = cellX + CharacterSelectArt.SlotWidth / 2;
        int y = cellY + CharacterSelectArt.SlotHeight / 2;
        Assert.False(CharacterCreateModal.ConfirmButtonRect.Contains(x, y));
        Assert.False(CharacterCreateModal.CancelButtonRect.Contains(x, y));
        Assert.Equal(1, CharacterSelectArt.HitSlot(x, y));

        rig.Flow.Click(x, y);
        rig.Poll();
        Assert.NotNull(rig.Flow.CreateModal);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        Assert.False(rig.Flow.HasCharacterModalOpen);

        Assert.Equal(0, rig.EnterGameAndReadSlot());
    }

    [Fact]
    public void A_click_on_a_character_cell_under_an_open_delete_modal_does_not_move_the_selection()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        rig.ClickDelete();
        CharacterSelectArt.SlotOrigin(1, out int cellX, out int cellY);
        int x = cellX + CharacterSelectArt.SlotWidth / 2;
        int y = cellY + CharacterSelectArt.SlotHeight / 2;
        Assert.False(CharacterDeleteModal.ConfirmButtonRect.Contains(x, y));
        Assert.False(CharacterDeleteModal.CancelButtonRect.Contains(x, y));
        Assert.Equal(1, CharacterSelectArt.HitSlot(x, y));

        rig.Flow.Click(x, y);
        rig.Poll();
        Assert.NotNull(rig.Flow.DeleteModal);
        Assert.Equal((byte)0, rig.Flow.DeleteModal.TargetSlot);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Poll();
        Assert.False(rig.Flow.HasCharacterModalOpen);

        Assert.Equal(0, rig.EnterGameAndReadSlot());
    }

    /// <summary>面板开着时点到底下"游戏开始"钮也被吞掉:关掉面板、再轮询几次,既没有进游戏也没有选角请求。</summary>
    [Fact]
    public void A_click_on_the_start_button_under_an_open_modal_does_not_start_the_game_later()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        rig.ClickCreate();
        HitRect start = CharacterSelectArt.ButtonRect(CharacterSelectButton.Start);
        int x = start.X + start.Width / 2;
        int y = start.Y + start.Height / 2;
        Assert.False(CharacterCreateModal.ConfirmButtonRect.Contains(x, y));
        Assert.False(CharacterCreateModal.CancelButtonRect.Contains(x, y));
        Assert.Equal(CharacterSelectButton.Start, CharacterSelectArt.HitButton(x, y));

        rig.Flow.Click(x, y);
        rig.Pump(2);
        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Pump(4);

        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.SelectCalls);
        Assert.False(rig.Flow.HasCharacterModalOpen);
    }

    // ------------------------------------------------------------------ 预览头像的时钟

    /// <summary>
    /// 创建面板开着时,宿主每帧传给 Flow.Advance 的帧间隔累加成面板自己的时钟,预览头像按它摆姿势,
    /// 不用 Build 传进来的外部时间(这里故意传一个和面板打开时刻毫不相干的大数)。
    /// </summary>
    [Fact]
    public void The_open_create_modal_runs_its_preview_clock_on_the_frame_intervals_the_host_passes_in()
    {
        var avatars = new PoseRecorder();
        Rig rig = CreateRigWithAvatars(avatars, ("HeroA", 0, 10));
        rig.ClickCreate();

        rig.Flow.Advance(250);
        rig.Flow.Advance(150);
        avatars.Poses.Clear();
        rig.Flow.Scene.Build(99999);

        (string Name, long AgeMs) preview = Assert.Single(avatars.Poses, pose => pose.Name == string.Empty);
        Assert.Equal(400, preview.AgeMs);
    }
}
