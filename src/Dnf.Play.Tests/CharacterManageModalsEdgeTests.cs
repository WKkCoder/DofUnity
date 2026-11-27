using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 创建/删除角色模态面板与选角画面在键盘、输入、状态条、命中框上的补充测试(r20fix-createcharui,簇 5)。
/// 面板的版式是自拟的(不对拍原版),所以这里只钉不变量(按钮成对等大、对齐、不越出卡片与画布、不互相重叠)与"点在矩形内/外"的定义,
/// 不钉具体像素偏移。全部用内存里的合成字体与合成数据。
/// </summary>
public sealed class CharacterManageModalsEdgeTests
{
    private const string TooShortError = "角色名至少需要 4 个 UTF-8 字节";
    private const string DeleteMismatchError = "输入的确认名与目标角色名不一致";

    private static CharacterCreateModal NewCreate() => new CharacterCreateModal(null, null, new SizedFonts());

    private static CharacterDeleteModal NewDelete(string name = "HeroA") => new CharacterDeleteModal(2, name, "职业0", 25, new SizedFonts());

    private static List<SceneSprite> Build(ICharacterManageModal modal)
    {
        var sprites = new List<SceneSprite>();
        int order = 0;
        modal.BuildInto(sprites, ref order, 0);
        return sprites;
    }

    private static SpriteFrame FrameOf(List<SceneSprite> sprites, string tag)
    {
        return Assert.Single(SpriteQuery.Tagged(sprites, tag)).Frame;
    }

    // ------------------------------------------------------------------ 5a:创建模态的 Tab / Shift+Tab 回绕

    [Fact]
    public void Shift_tab_walks_the_eleven_jobs_backwards_and_wraps_from_the_first_to_the_last()
    {
        CharacterCreateModal modal = NewCreate();
        Assert.Equal((byte)0, modal.SelectedJob);

        Assert.True(modal.Key(OnlineShellKey.ShiftTab));
        Assert.Equal((byte)10, modal.SelectedJob);
        for (int expected = 9; expected >= 0; expected--)
        {
            Assert.True(modal.Key(OnlineShellKey.ShiftTab));
            Assert.Equal((byte)expected, modal.SelectedJob);
        }
        Assert.True(modal.Key(OnlineShellKey.ShiftTab));
        Assert.Equal((byte)10, modal.SelectedJob);
    }

    [Fact]
    public void Tab_walks_the_eleven_jobs_forwards_and_wraps_from_the_last_to_the_first()
    {
        CharacterCreateModal modal = NewCreate();
        for (int expected = 1; expected <= 10; expected++)
        {
            Assert.True(modal.Key(OnlineShellKey.Tab));
            Assert.Equal((byte)expected, modal.SelectedJob);
        }
        Assert.True(modal.Key(OnlineShellKey.Tab));
        Assert.Equal((byte)0, modal.SelectedJob);
        Assert.True(modal.Key(OnlineShellKey.Tab));
        Assert.Equal((byte)1, modal.SelectedJob);
    }

    [Fact]
    public void Tab_and_shift_tab_do_nothing_while_the_modal_is_busy_but_the_key_is_still_swallowed()
    {
        CharacterCreateModal modal = NewCreate();
        modal.SelectJob(4);
        modal.SetBusy(true);

        Assert.True(modal.Key(OnlineShellKey.Tab));
        Assert.Equal((byte)4, modal.SelectedJob);
        Assert.True(modal.Key(OnlineShellKey.ShiftTab));
        Assert.Equal((byte)4, modal.SelectedJob);
        Assert.True(modal.Key(OnlineShellKey.Left));
        Assert.Equal((byte)4, modal.SelectedJob);
    }

    /// <summary>模态吃掉所有按键(返回 true):没有哪个键能漏给背景的选角画面。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_key_is_swallowed_by_the_modal(bool create)
    {
        foreach (OnlineShellKey key in Enum.GetValues<OnlineShellKey>())
        {
            ICharacterManageModal modal = create ? NewCreate() : NewDelete();
            Assert.True(modal.Key(key), key.ToString());
        }
    }

    // ------------------------------------------------------------------ 5c:创建模态的退格清错误

    [Fact]
    public void Backspace_that_removes_a_character_clears_the_validation_error()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("ab");
        modal.Submit();
        Assert.Equal(TooShortError, modal.ErrorText);

        modal.Type("\b");

        Assert.Equal("a", modal.Name);
        Assert.Null(modal.ErrorText);
    }

    [Fact]
    public void An_ordinary_character_also_clears_the_validation_error()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("ab");
        modal.Submit();
        Assert.Equal(TooShortError, modal.ErrorText);

        modal.Type("c");

        Assert.Equal("abc", modal.Name);
        Assert.Null(modal.ErrorText);
    }

    [Fact]
    public void Backspace_on_an_empty_name_changes_nothing_and_keeps_the_error()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Submit();
        Assert.Equal(TooShortError, modal.ErrorText);

        modal.Type("\b");
        modal.Type("\b\b");

        Assert.Equal(string.Empty, modal.Name);
        Assert.Equal(TooShortError, modal.ErrorText);
    }

    [Fact]
    public void Backspace_clears_a_server_failure_too_and_the_scene_goes_back_to_the_status_bar()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("HeroA");
        modal.SetFailure(OnlineCharacterManageStatus.DuplicateActiveName);
        Assert.Equal("该角色名已被使用", modal.ErrorText);
        List<SceneSprite> failing = Build(modal);
        Assert.Single(SpriteQuery.Tagged(failing, "create-modal-error"));
        Assert.Empty(SpriteQuery.Tagged(failing, "create-modal-status"));

        modal.Type("\b");

        Assert.Equal("Hero", modal.Name);
        Assert.Null(modal.ErrorText);
        List<SceneSprite> after = Build(modal);
        Assert.Empty(SpriteQuery.Tagged(after, "create-modal-error"));
        Assert.Empty(SpriteQuery.Tagged(after, "create-modal-validation-error"));
        Assert.Single(SpriteQuery.Tagged(after, "create-modal-status"));
    }

    // ------------------------------------------------------------------ 5d:删除模态的退格

    [Fact]
    public void Backspace_on_an_empty_confirmation_name_neither_throws_nor_changes_anything()
    {
        CharacterDeleteModal modal = NewDelete();
        modal.Submit();
        Assert.Equal(DeleteMismatchError, modal.ErrorText);

        modal.Type("\b");
        modal.Type("\b\b\b");

        Assert.Equal(string.Empty, modal.ConfirmName);
        Assert.Equal(DeleteMismatchError, modal.ErrorText);
        Assert.False(modal.IsNameMatched);
    }

    [Fact]
    public void Backspace_removes_exactly_one_character_of_a_non_empty_confirmation_name_and_clears_the_error()
    {
        CharacterDeleteModal modal = NewDelete();
        modal.Type("HeroAB");
        modal.Submit();
        Assert.Equal(DeleteMismatchError, modal.ErrorText);

        modal.Type("\b");

        Assert.Equal("HeroA", modal.ConfirmName);
        Assert.Null(modal.ErrorText);
        Assert.True(modal.IsNameMatched);
    }

    // ------------------------------------------------------------------ 5e:删除模态的目标等级下限

    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(-3, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(60, 60)]
    public void The_target_level_is_never_below_one(int given, int expected)
    {
        var modal = new CharacterDeleteModal(1, "HeroA", "职业0", given, null);

        Assert.Equal(expected, modal.TargetLevel);
    }

    // ------------------------------------------------------------------ 5f:创建模态状态条的三种状态

    private const uint Info = 0xFFD25E;
    private const uint Success = 0x4FD17F;
    private const uint Error = 0xFF7A45;

    [Fact]
    public void The_status_bar_is_informational_while_the_name_is_empty()
    {
        List<SceneSprite> sprites = Build(NewCreate());

        Assert.True(ShellFrames.HasColor(FrameOf(sprites, "create-modal-status-accent"), Info));
        Assert.False(ShellFrames.HasColor(FrameOf(sprites, "create-modal-status-accent"), Error));
        // "当前字节数：0/18（4-18字节，不含空格与特殊符号）":20 个全角字 × 14 + 8 个半角字 × 7 + 2
        Assert.Equal(338, FrameOf(sprites, "create-modal-status").Width);
    }

    [Fact]
    public void The_status_bar_is_an_error_while_the_name_is_non_empty_and_invalid()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("a");

        List<SceneSprite> sprites = Build(modal);

        Assert.True(ShellFrames.HasColor(FrameOf(sprites, "create-modal-status-accent"), Error));
        Assert.False(ShellFrames.HasColor(FrameOf(sprites, "create-modal-status-accent"), Info));
        // "角色名至少需要 4 个 UTF-8 字节 (1/18)":10 个全角字 × 14 + 17 个半角字 × 7 + 2
        Assert.Equal(261, FrameOf(sprites, "create-modal-status").Width);
    }

    [Fact]
    public void The_status_bar_is_a_success_while_the_name_is_valid_and_shows_the_byte_count()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("Hero");

        List<SceneSprite> sprites = Build(modal);

        Assert.True(ShellFrames.HasColor(FrameOf(sprites, "create-modal-status-accent"), Success));
        Assert.False(ShellFrames.HasColor(FrameOf(sprites, "create-modal-status-accent"), Info));
        Assert.Equal(338, FrameOf(sprites, "create-modal-status").Width);

        // 11 字节:两位数的计数多一个半角字
        modal.Type("Hero900");
        Assert.Equal(11, modal.NameByteCount);
        Assert.Equal(345, FrameOf(Build(modal), "create-modal-status").Width);
    }

    [Fact]
    public void The_status_bar_text_says_what_the_state_is_for_each_of_the_three_states()
    {
        var emptyFonts = new RecordingFonts();
        Build(new CharacterCreateModal(null, null, emptyFonts));
        Assert.True(emptyFonts.Asked("当前字节数"));
        Assert.False(emptyFonts.Asked("至少需要"));

        var invalidFonts = new RecordingFonts();
        var invalid = new CharacterCreateModal(null, null, invalidFonts);
        invalid.Type("a");
        Build(invalid);
        Assert.True(invalidFonts.Asked("至少需要"));
        Assert.False(invalidFonts.Asked("当前字节数"));

        var validFonts = new RecordingFonts();
        var valid = new CharacterCreateModal(null, null, validFonts);
        valid.Type("Hero");
        Build(valid);
        Assert.True(validFonts.Asked("当前字节数"));
        Assert.False(validFonts.Asked("至少需要"));
    }

    // ------------------------------------------------------------------ 5g:按钮命中框

    /// <summary>
    /// 点在矩形里(含最左上与最右下像素)触发动作,左、右、上、下各偏一像素不触发——
    /// "在内/在外"由矩形这个定义本身给出,不断言常量数值。
    /// </summary>
    private static void AssertEdges(Func<ICharacterManageModal> make, Func<ICharacterManageModal, CharacterManageModalAction> take,
        HitRect rect, CharacterManageModalAction expected)
    {
        (int X, int Y)[] inside =
        {
            (rect.X, rect.Y),
            (rect.X + rect.Width - 1, rect.Y + rect.Height - 1),
            (rect.X + rect.Width - 1, rect.Y),
            (rect.X, rect.Y + rect.Height - 1),
        };
        foreach ((int x, int y) in inside)
        {
            ICharacterManageModal modal = make();
            modal.Click(x, y);
            Assert.Equal(expected, take(modal));
        }

        (int X, int Y)[] outside =
        {
            (rect.X - 1, rect.Y + rect.Height / 2),
            (rect.X + rect.Width, rect.Y + rect.Height / 2),
            (rect.X + rect.Width / 2, rect.Y - 1),
            (rect.X + rect.Width / 2, rect.Y + rect.Height),
        };
        foreach ((int x, int y) in outside)
        {
            ICharacterManageModal modal = make();
            modal.Click(x, y);
            Assert.Equal(CharacterManageModalAction.None, take(modal));
        }
    }

    private static ICharacterManageModal ReadyCreate()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("Hero900");
        return modal;
    }

    private static ICharacterManageModal ReadyDelete()
    {
        CharacterDeleteModal modal = NewDelete("HeroA");
        modal.Type("HeroA");
        return modal;
    }

    [Fact]
    public void Create_modal_confirm_button_triggers_inside_its_rectangle_and_not_one_pixel_outside()
    {
        AssertEdges(ReadyCreate, m => ((CharacterCreateModal)m).TakeAction(), CharacterCreateModal.ConfirmButtonRect, CharacterManageModalAction.Submit);
    }

    [Fact]
    public void Create_modal_cancel_button_triggers_inside_its_rectangle_and_not_one_pixel_outside()
    {
        AssertEdges(ReadyCreate, m => ((CharacterCreateModal)m).TakeAction(), CharacterCreateModal.CancelButtonRect, CharacterManageModalAction.Cancel);
    }

    [Fact]
    public void Delete_modal_confirm_button_triggers_inside_its_rectangle_and_not_one_pixel_outside()
    {
        AssertEdges(ReadyDelete, m => ((CharacterDeleteModal)m).TakeAction(), CharacterDeleteModal.ConfirmButtonRect, CharacterManageModalAction.Submit);
    }

    [Fact]
    public void Delete_modal_cancel_button_triggers_inside_its_rectangle_and_not_one_pixel_outside()
    {
        AssertEdges(ReadyDelete, m => ((CharacterDeleteModal)m).TakeAction(), CharacterDeleteModal.CancelButtonRect, CharacterManageModalAction.Cancel);
    }

    [Fact]
    public void While_busy_the_create_modal_ignores_confirm_clicks_but_still_cancels()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("Hero900");
        modal.SetBusy(true);

        ClickCenter(modal, CharacterCreateModal.ConfirmButtonRect);
        Assert.Equal(CharacterManageModalAction.None, modal.TakeAction());
        modal.Click(CharacterCreateModal.JobRect(5).X + 1, CharacterCreateModal.JobRect(5).Y + 1);
        Assert.Equal((byte)0, modal.SelectedJob);
        ClickCenter(modal, CharacterCreateModal.CancelButtonRect);
        Assert.Equal(CharacterManageModalAction.Cancel, modal.TakeAction());
    }

    private static void ClickCenter(ICharacterManageModal modal, HitRect rect)
    {
        modal.Click(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    /// <summary>
    /// 创建面板的版式不变量(面板是自拟的,不钉具体像素):确认与取消是等大的一对、同一行、与名字输入框同高同顶;
    /// 左到右依次是输入框、确认、取消,互不重叠;整排在卡片里、在 800×600 画布里;卡片左边距(到输入框)等于右边距(到取消钮)。
    /// </summary>
    [Fact]
    public void Create_modal_buttons_are_a_matched_pair_aligned_with_the_name_box_and_inside_the_card()
    {
        HitRect box = CharacterCreateModal.NameBoxRect;
        HitRect confirm = CharacterCreateModal.ConfirmButtonRect;
        HitRect cancel = CharacterCreateModal.CancelButtonRect;
        int cardLeft = CharacterCreateModal.CardX;
        int cardRight = CharacterCreateModal.CardX + CharacterCreateModal.CardWidth;
        int cardTop = CharacterCreateModal.CardY;
        int cardBottom = CharacterCreateModal.CardY + CharacterCreateModal.CardHeight;

        Assert.Equal(confirm.Width, cancel.Width);
        Assert.Equal(confirm.Height, cancel.Height);
        Assert.Equal(confirm.Y, cancel.Y);
        Assert.Equal(box.Y, confirm.Y);
        Assert.Equal(box.Height, confirm.Height);

        Assert.True(box.X + box.Width <= confirm.X);
        Assert.True(confirm.X + confirm.Width <= cancel.X);

        Assert.True(box.X >= cardLeft);
        Assert.True(cancel.X + cancel.Width <= cardRight);
        Assert.True(confirm.Y >= cardTop);
        Assert.True(confirm.Y + confirm.Height <= cardBottom);
        Assert.True(cardLeft >= 0 && cardRight <= OnlineShellArt.ScreenWidth);
        Assert.True(cardTop >= 0 && cardBottom <= OnlineShellArt.ScreenHeight);

        Assert.Equal(box.X - cardLeft, cardRight - (cancel.X + cancel.Width));
    }

    /// <summary>
    /// 删除面板的版式不变量:确认与取消是等大的一对、同一行;确认的左缘与取消的右缘正好是确认名输入框的左、右缘;
    /// 两个按钮不重叠、在卡片里、在画布里;卡片左边距等于右边距。
    /// </summary>
    [Fact]
    public void Delete_modal_buttons_are_a_matched_pair_spanning_the_confirmation_box_inside_the_card()
    {
        HitRect box = CharacterDeleteModal.ConfirmBoxRect;
        HitRect confirm = CharacterDeleteModal.ConfirmButtonRect;
        HitRect cancel = CharacterDeleteModal.CancelButtonRect;
        int cardLeft = CharacterDeleteModal.CardX;
        int cardRight = CharacterDeleteModal.CardX + CharacterDeleteModal.CardWidth;
        int cardTop = CharacterDeleteModal.CardY;
        int cardBottom = CharacterDeleteModal.CardY + CharacterDeleteModal.CardHeight;

        Assert.Equal(confirm.Width, cancel.Width);
        Assert.Equal(confirm.Height, cancel.Height);
        Assert.Equal(confirm.Y, cancel.Y);

        Assert.Equal(box.X, confirm.X);
        Assert.Equal(box.X + box.Width, cancel.X + cancel.Width);
        Assert.True(confirm.X + confirm.Width <= cancel.X);
        Assert.True(box.Y + box.Height <= confirm.Y);

        Assert.True(confirm.X >= cardLeft);
        Assert.True(cancel.X + cancel.Width <= cardRight);
        Assert.True(confirm.Y >= cardTop);
        Assert.True(confirm.Y + confirm.Height <= cardBottom);
        Assert.True(cardLeft >= 0 && cardRight <= OnlineShellArt.ScreenWidth);
        Assert.True(cardTop >= 0 && cardBottom <= OnlineShellArt.ScreenHeight);

        Assert.Equal(box.X - cardLeft, cardRight - (cancel.X + cancel.Width));
    }

    // ------------------------------------------------------------------ 5h:选角画面的创建/删除启用位

    [Fact]
    public void The_online_select_screen_reads_back_each_enabled_flag_independently()
    {
        CharacterSelectScreen screen = CharSelectKit.Online(2);
        Assert.False(screen.CreateEnabled);
        Assert.False(screen.DeleteEnabled);

        screen.DeleteEnabled = true;
        Assert.True(screen.DeleteEnabled);
        Assert.False(screen.CreateEnabled);

        screen.CreateEnabled = true;
        Assert.True(screen.CreateEnabled);
        Assert.True(screen.DeleteEnabled);

        screen.DeleteEnabled = false;
        Assert.False(screen.DeleteEnabled);
        Assert.True(screen.CreateEnabled);

        screen.CreateEnabled = false;
        Assert.False(screen.CreateEnabled);
        Assert.False(screen.DeleteEnabled);
    }

    [Fact]
    public void The_offline_select_screen_reports_both_flags_false_and_ignores_setting_them()
    {
        var screen = new CharacterSelectScreen(RosterKit.Roster(2));
        Assert.False(screen.IsOnline);
        Assert.False(screen.CreateEnabled);
        Assert.False(screen.DeleteEnabled);

        screen.CreateEnabled = true;
        screen.DeleteEnabled = true;

        Assert.False(screen.CreateEnabled);
        Assert.False(screen.DeleteEnabled);
    }
}
