using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 创建/删除角色模态面板的逻辑补充测试(r20fix-createcharui,簇 5 的相邻点补测):职业网格上的方向键移动、
/// 输入过滤与长度上限、提交/取消的挂起语义、忙态与失败态的切换,以及画面里随这些状态变化的部件。
/// 版式是自拟的,这里不钉像素位置,只看"画了什么、用什么颜色、带什么标签";全部用内存里的合成字体与合成数据。
/// </summary>
public sealed class CharacterManageModalsLogicTests
{
    private static CharacterCreateModal NewCreate() => new CharacterCreateModal(null, null, new SizedFonts());

    private static CharacterDeleteModal NewDelete(string name = "HeroA") => new CharacterDeleteModal(2, name, "职业0", 25, new SizedFonts());

    private static List<SceneSprite> Build(ICharacterManageModal modal, long timeMs = 0)
    {
        var sprites = new List<SceneSprite>();
        int order = 0;
        modal.BuildInto(sprites, ref order, timeMs);
        return sprites;
    }

    /// <summary>职业称号:(职业, 0, 0) 这个组合才有名字("职业N"),别的组合一律回一个带着三个参数的怪名字,问错了参数一眼能看出来。</summary>
    private sealed class JobTitles : ICharacterTitles
    {
        public List<(int Job, int GrowType, int Awakening)> Asked { get; } = new List<(int, int, int)>();

        public string DefaultName => "默认名";

        public string ClassName(int job, int growType, int awakening)
        {
            Asked.Add((job, growType, awakening));
            return growType == 0 && awakening == 0 ? "职业" + job : "问错了" + job + "/" + growType + "/" + awakening;
        }
    }

    /// <summary>记下每次被要求摆的姿势(名册里的名字、职业名、职业号、动画时间、脚下点);每次回一个 1×1 的精灵。</summary>
    private sealed class RecordingAvatars : ICharacterAvatars
    {
        public List<(string Name, string JobName, int Job, long AgeMs, int FootX, int FootY)> Poses { get; }
            = new List<(string, string, int, long, int, int)>();

        public IReadOnlyList<SceneSprite> Pose(CharacterSlot slot, long ageMs, int footX, int footY)
        {
            Poses.Add((slot.Name, slot.JobName, slot.Online == null ? -1 : slot.Online.Job, ageMs, footX, footY));
            return new[] { new SceneSprite(new SpriteFrame(1, 1, 0, 0, 1, 1, new byte[4]), footX, footY, 0, 0, "fake/avatar", 0) };
        }
    }

    private static int Count(List<SceneSprite> sprites, string tag) => SpriteQuery.Tagged(sprites, tag).Count;

    private static SpriteFrame FrameOf(List<SceneSprite> sprites, string tag) => Assert.Single(SpriteQuery.Tagged(sprites, tag)).Frame;

    // ------------------------------------------------------------------ 创建模态:方向键在 2 列网格上移动

    /// <summary>
    /// 11 个职业排成 2 列:职业 j 在第 j%2 列、第 j/2 行(最后一行只有职业 10)。
    /// 每行是 (起点, 左, 右, 上, 下) 按手算的结果:到边就停在原地;职业 9 往下、职业 10 往右没有对应格子,也停在原地。
    /// </summary>
    [Theory]
    [InlineData(0, 0, 1, 0, 2)]
    [InlineData(1, 0, 1, 1, 3)]
    [InlineData(2, 2, 3, 0, 4)]
    [InlineData(3, 2, 3, 1, 5)]
    [InlineData(4, 4, 5, 2, 6)]
    [InlineData(5, 4, 5, 3, 7)]
    [InlineData(6, 6, 7, 4, 8)]
    [InlineData(7, 6, 7, 5, 9)]
    [InlineData(8, 8, 9, 6, 10)]
    [InlineData(9, 8, 9, 7, 9)]
    [InlineData(10, 10, 10, 8, 10)]
    public void Direction_keys_move_the_selected_job_over_the_two_column_grid(int start, int left, int right, int up, int down)
    {
        (OnlineShellKey Key, int Expected)[] moves =
        {
            (OnlineShellKey.Left, left),
            (OnlineShellKey.Right, right),
            (OnlineShellKey.Up, up),
            (OnlineShellKey.Down, down),
        };
        foreach ((OnlineShellKey key, int expected) in moves)
        {
            CharacterCreateModal modal = NewCreate();
            modal.SelectJob((byte)start);
            Assert.Equal((byte)start, modal.SelectedJob);

            Assert.True(modal.Key(key));

            Assert.Equal((byte)expected, modal.SelectedJob);
        }
    }

    [Fact]
    public void Selecting_a_job_above_the_highest_is_ignored_and_the_highest_itself_is_accepted()
    {
        CharacterCreateModal modal = NewCreate();
        modal.SelectJob(7);

        modal.SelectJob(11);
        Assert.Equal((byte)7, modal.SelectedJob);
        modal.SelectJob(255);
        Assert.Equal((byte)7, modal.SelectedJob);
        modal.SelectJob(10);
        Assert.Equal((byte)10, modal.SelectedJob);
        modal.SelectJob(0);
        Assert.Equal((byte)0, modal.SelectedJob);
    }

    [Fact]
    public void Every_job_cell_answers_to_its_own_job_at_the_first_and_the_last_pixel_and_nothing_else_does()
    {
        for (int job = 0; job <= 10; job++)
        {
            HitRect cell = CharacterCreateModal.JobRect(job);
            Assert.Equal(job, CharacterCreateModal.HitJob(cell.X, cell.Y));
            Assert.Equal(job, CharacterCreateModal.HitJob(cell.X + cell.Width - 1, cell.Y + cell.Height - 1));
        }
        Assert.Equal(-1, CharacterCreateModal.HitJob(0, 0));
        Assert.Equal(-1, CharacterCreateModal.HitJob(799, 599));
    }

    // ------------------------------------------------------------------ 创建模态:输入过滤与长度上限

    [Fact]
    public void The_name_field_takes_at_most_32_characters_and_a_backspace_frees_room_again()
    {
        CharacterCreateModal modal = NewCreate();

        modal.Type(new string('a', 40));
        Assert.Equal(new string('a', 32), modal.Name);

        modal.Type("b");
        Assert.Equal(new string('a', 32), modal.Name);

        modal.Type("\b");
        Assert.Equal(new string('a', 31), modal.Name);
        modal.Type("c");
        Assert.Equal(new string('a', 31) + "c", modal.Name);

        // 在 31 个字符的时候一次给两个:只放得下第一个
        CharacterCreateModal nearly = NewCreate();
        nearly.Type(new string('a', 31));
        nearly.Type("xy");
        Assert.Equal(new string('a', 31) + "x", nearly.Name);
    }

    [Fact]
    public void Control_characters_and_delete_are_dropped_but_space_and_tilde_are_kept()
    {
        CharacterCreateModal modal = NewCreate();

        modal.Type("a\u001fb\u007fc d~\r\n");

        Assert.Equal("abc d~", modal.Name);
    }

    [Fact]
    public void A_busy_create_modal_ignores_typing_even_a_backspace()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("Hero");
        modal.SetBusy(true);

        modal.Type("x");
        Assert.Equal("Hero", modal.Name);

        modal.Type("\b");
        Assert.Equal("Hero", modal.Name);

        modal.SetBusy(false);
        modal.Type("x");
        Assert.Equal("Herox", modal.Name);
    }

    // ------------------------------------------------------------------ 创建模态:提交、取消与挂起

    [Fact]
    public void A_pending_cancel_is_not_replaced_by_a_later_submit_and_each_action_is_taken_once()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("Hero900");

        modal.Cancel();
        modal.Submit();

        Assert.Equal(CharacterManageModalAction.Cancel, modal.TakeAction());
        Assert.Equal(CharacterManageModalAction.None, modal.TakeAction());
    }

    [Fact]
    public void A_pending_submit_is_replaced_by_a_later_cancel_and_a_second_submit_changes_nothing()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("Hero900");

        modal.Submit();
        modal.Submit();
        Assert.Equal(CharacterManageModalAction.Submit, modal.TakeAction());
        Assert.Equal(CharacterManageModalAction.None, modal.TakeAction());

        modal.Submit();
        modal.Cancel();
        Assert.Equal(CharacterManageModalAction.Cancel, modal.TakeAction());
    }

    [Fact]
    public void Submit_while_busy_neither_queues_an_action_nor_reports_a_name_error()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("a");
        modal.SetBusy(true);

        modal.Submit();

        Assert.Equal(CharacterManageModalAction.None, modal.TakeAction());
        Assert.Null(modal.ErrorText);
    }

    [Fact]
    public void A_name_error_blocks_the_submit_and_a_good_name_clears_the_error_and_queues_it()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("abc");
        modal.Submit();
        Assert.Equal(CharacterManageModalAction.None, modal.TakeAction());
        Assert.Equal("角色名至少需要 4 个 UTF-8 字节", modal.ErrorText);

        modal.Type("d");
        modal.Submit();

        Assert.Null(modal.ErrorText);
        Assert.Equal(CharacterManageModalAction.Submit, modal.TakeAction());
    }

    [Fact]
    public void Busy_and_failure_states_hand_over_cleanly_in_both_directions()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("Hero900");

        modal.SetBusy(true);
        Assert.True(modal.Busy);
        modal.SetFailure(OnlineCharacterManageStatus.DuplicateActiveName);
        Assert.False(modal.Busy);
        Assert.Equal("该角色名已被使用", modal.ErrorText);

        // 关掉忙态不清失败提示;重新进入忙态才清
        modal.SetBusy(false);
        Assert.Equal("该角色名已被使用", modal.ErrorText);
        modal.SetBusy(true);
        Assert.Null(modal.ErrorText);
        Assert.True(modal.Busy);
    }

    [Fact]
    public void Every_click_is_swallowed_by_the_create_modal_wherever_it_lands()
    {
        CharacterCreateModal modal = NewCreate();
        Assert.True(modal.Click(0, 0));
        Assert.True(modal.Click(CharacterCreateModal.ConfirmButtonRect.X + 1, CharacterCreateModal.ConfirmButtonRect.Y + 1));
        Assert.True(modal.Click(CharacterCreateModal.CancelButtonRect.X + 1, CharacterCreateModal.CancelButtonRect.Y + 1));
        Assert.True(modal.Click(CharacterCreateModal.JobRect(3).X + 1, CharacterCreateModal.JobRect(3).Y + 1));
        modal.SetBusy(true);
        Assert.True(modal.Click(CharacterCreateModal.ConfirmButtonRect.X + 1, CharacterCreateModal.ConfirmButtonRect.Y + 1));
        Assert.True(modal.Click(CharacterCreateModal.JobRect(5).X + 1, CharacterCreateModal.JobRect(5).Y + 1));
        Assert.True(modal.Click(0, 0));
    }

    // ------------------------------------------------------------------ 创建模态:画面随状态变化的部件

    /// <summary>
    /// 名字框的边线一像素宽,框最上面一行正中的像素就是边线本身(圆角的抗锯齿碰不到正中):
    /// 常态是金色(绿分量高),非法名字换成橙红(绿分量低得多)。框是渐变加抗锯齿画的,没有一个像素恰好等于样式里写的那个色值,
    /// 所以不比色值,比"同一个像素的绿分量低了一大截"与"两个常态框逐字节相同"。
    /// </summary>
    private static int EdgeGreen(SpriteFrame box) => box.Rgba[(box.Width / 2) * 4 + 1];

    [Fact]
    public void The_name_box_turns_red_only_for_a_non_empty_name_that_breaks_the_rules()
    {
        SpriteFrame emptyBox = FrameOf(Build(NewCreate()), "create-modal-name-box");

        CharacterCreateModal bad = NewCreate();
        bad.Type("a");
        SpriteFrame badBox = FrameOf(Build(bad), "create-modal-name-box");

        CharacterCreateModal good = NewCreate();
        good.Type("Hero");
        SpriteFrame goodBox = FrameOf(Build(good), "create-modal-name-box");

        Assert.Equal(emptyBox.Rgba, goodBox.Rgba);
        Assert.True(EdgeGreen(badBox) + 50 < EdgeGreen(emptyBox),
            "the invalid box edge green " + EdgeGreen(badBox) + " is not far below the normal " + EdgeGreen(emptyBox));
    }

    [Fact]
    public void The_placeholder_is_shown_only_while_the_name_is_empty_and_the_typed_text_only_after()
    {
        List<SceneSprite> empty = Build(NewCreate());
        Assert.Equal(1, Count(empty, "create-modal-name-placeholder"));
        Assert.Equal(0, Count(empty, "create-modal-name-text"));

        CharacterCreateModal typed = NewCreate();
        typed.Type("Hero");
        List<SceneSprite> after = Build(typed);
        Assert.Equal(0, Count(after, "create-modal-name-placeholder"));
        Assert.Equal(1, Count(after, "create-modal-name-text"));
    }

    [Fact]
    public void The_confirm_button_is_greyed_out_only_while_busy()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("Hero900");

        List<SceneSprite> idle = Build(modal);
        Assert.Equal(1, Count(idle, "create-modal-confirm"));
        Assert.Equal(0, Count(idle, "create-modal-confirm-disabled"));

        modal.SetBusy(true);
        List<SceneSprite> busy = Build(modal);
        Assert.Equal(0, Count(busy, "create-modal-confirm"));
        Assert.Equal(1, Count(busy, "create-modal-confirm-disabled"));
    }

    [Fact]
    public void The_notice_bar_shows_exactly_one_of_busy_error_or_status_in_that_order_of_precedence()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("Hero900");

        List<SceneSprite> status = Build(modal);
        Assert.Equal(1, Count(status, "create-modal-status"));
        Assert.Equal(0, Count(status, "create-modal-busy"));
        Assert.Equal(0, Count(status, "create-modal-error"));
        Assert.Equal(0, Count(status, "create-modal-validation-error"));

        modal.SetBusy(true);
        List<SceneSprite> busy = Build(modal);
        Assert.Equal(0, Count(busy, "create-modal-status"));
        Assert.Equal(1, Count(busy, "create-modal-busy"));
        Assert.Equal(0, Count(busy, "create-modal-error"));

        modal.SetFailure(OnlineCharacterManageStatus.NoReply);
        List<SceneSprite> failed = Build(modal);
        Assert.Equal(0, Count(failed, "create-modal-status"));
        Assert.Equal(0, Count(failed, "create-modal-busy"));
        Assert.Equal(1, Count(failed, "create-modal-error"));
        Assert.Equal(0, Count(failed, "create-modal-validation-error"));
    }

    [Fact]
    public void A_local_name_error_is_drawn_as_a_validation_error_not_a_server_error()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("abc");
        modal.Submit();

        List<SceneSprite> sprites = Build(modal);

        Assert.Equal(1, Count(sprites, "create-modal-validation-error"));
        Assert.Equal(0, Count(sprites, "create-modal-error"));
        Assert.Equal(0, Count(sprites, "create-modal-status"));
    }

    [Fact]
    public void Exactly_the_selected_job_cell_is_highlighted_and_follows_the_selection()
    {
        CharacterCreateModal modal = NewCreate();
        modal.SelectJob(6);

        List<SceneSprite> sprites = Build(modal);

        Assert.Equal(1, Count(sprites, "create-modal-job-selected"));
        Assert.Equal(10, Count(sprites, "create-modal-job"));
        Assert.Equal(11, Count(sprites, "create-modal-job-text"));
        HitRect selectedCell = CharacterCreateModal.JobRect(6);
        SceneSprite highlighted = Assert.Single(SpriteQuery.Tagged(sprites, "create-modal-job-selected"));
        Assert.Equal(selectedCell.X, highlighted.ScreenX);
        Assert.Equal(selectedCell.Y, highlighted.ScreenY);
    }

    // ------------------------------------------------------------------ 删除模态

    [Theory]
    [InlineData("", "", false)]                                       // 目标名与输入都是空:空名字不是合法确认名
    [InlineData("HeroA", "HeroA", true)]
    [InlineData("HeroA", "heroa", false)]                             // 逐字、区分大小写
    [InlineData("HeroA", "HeroAB", false)]
    [InlineData("HeroA", "Hero", false)]
    [InlineData("a", "a", true)]                                      // 1 字节:下限
    [InlineData("Hero900Hero900Hero900Hero900H", "Hero900Hero900Hero900Hero900H", true)]    // 29 字节:上限
    [InlineData("Hero900Hero900Hero900Hero900He", "Hero900Hero900Hero900Hero900He", false)]  // 30 字节:上限多一
    public void The_confirmation_name_must_equal_the_target_name_and_be_a_valid_confirmation_name(string target, string typed, bool expected)
    {
        CharacterDeleteModal modal = NewDelete(target);

        modal.Type(typed);

        Assert.Equal(typed, modal.ConfirmName);
        Assert.Equal(expected, modal.IsNameMatched);
    }

    [Fact]
    public void The_delete_confirmation_field_takes_at_most_64_characters_and_drops_control_characters()
    {
        CharacterDeleteModal modal = NewDelete();

        modal.Type(new string('a', 70));
        Assert.Equal(new string('a', 64), modal.ConfirmName);
        modal.Type("b");
        Assert.Equal(new string('a', 64), modal.ConfirmName);
        modal.Type("\b");
        Assert.Equal(new string('a', 63), modal.ConfirmName);

        CharacterDeleteModal other = NewDelete();
        other.Type("a\u001fb\u007fc d~\r\n");
        Assert.Equal("abc d~", other.ConfirmName);
    }

    [Fact]
    public void A_busy_delete_modal_ignores_typing_even_a_backspace()
    {
        CharacterDeleteModal modal = NewDelete();
        modal.Type("Hero");
        modal.SetBusy(true);

        modal.Type("A");
        Assert.Equal("Hero", modal.ConfirmName);

        modal.Type("\b");
        Assert.Equal("Hero", modal.ConfirmName);

        modal.SetBusy(false);
        modal.Type("A");
        Assert.Equal("HeroA", modal.ConfirmName);
    }

    [Fact]
    public void A_pending_delete_cancel_is_not_replaced_by_a_later_submit_and_each_action_is_taken_once()
    {
        CharacterDeleteModal modal = NewDelete();
        modal.Type("HeroA");

        modal.Cancel();
        modal.Submit();

        Assert.Equal(CharacterManageModalAction.Cancel, modal.TakeAction());
        Assert.Equal(CharacterManageModalAction.None, modal.TakeAction());
    }

    [Fact]
    public void A_pending_delete_submit_is_replaced_by_a_later_cancel_and_a_second_submit_changes_nothing()
    {
        CharacterDeleteModal modal = NewDelete();
        modal.Type("HeroA");

        modal.Submit();
        modal.Submit();
        Assert.Equal(CharacterManageModalAction.Submit, modal.TakeAction());
        Assert.Equal(CharacterManageModalAction.None, modal.TakeAction());

        modal.Submit();
        modal.Cancel();
        Assert.Equal(CharacterManageModalAction.Cancel, modal.TakeAction());
    }

    [Fact]
    public void Submit_while_busy_neither_queues_an_action_nor_reports_a_mismatch()
    {
        CharacterDeleteModal modal = NewDelete();
        modal.Type("Hero");
        modal.SetBusy(true);

        modal.Submit();

        Assert.Equal(CharacterManageModalAction.None, modal.TakeAction());
        Assert.Null(modal.ErrorText);
    }

    [Fact]
    public void Delete_busy_and_failure_states_hand_over_cleanly_in_both_directions()
    {
        CharacterDeleteModal modal = NewDelete();
        modal.Type("HeroA");

        modal.SetBusy(true);
        Assert.True(modal.Busy);
        modal.SetFailure(OnlineCharacterManageStatus.NoReply);
        Assert.False(modal.Busy);
        Assert.Equal("等待服务器响应超时", modal.ErrorText);

        modal.SetBusy(false);
        Assert.Equal("等待服务器响应超时", modal.ErrorText);
        modal.SetBusy(true);
        Assert.Null(modal.ErrorText);
        Assert.True(modal.Busy);
    }

    [Fact]
    public void Every_click_is_swallowed_by_the_delete_modal_wherever_it_lands()
    {
        CharacterDeleteModal modal = NewDelete();
        Assert.True(modal.Click(0, 0));
        Assert.True(modal.Click(CharacterDeleteModal.ConfirmButtonRect.X + 1, CharacterDeleteModal.ConfirmButtonRect.Y + 1));
        Assert.True(modal.Click(CharacterDeleteModal.CancelButtonRect.X + 1, CharacterDeleteModal.CancelButtonRect.Y + 1));
        modal.SetBusy(true);
        Assert.True(modal.Click(CharacterDeleteModal.ConfirmButtonRect.X + 1, CharacterDeleteModal.ConfirmButtonRect.Y + 1));
        Assert.True(modal.Click(0, 0));
    }

    [Fact]
    public void The_delete_modal_shows_the_placeholder_until_something_is_typed_and_greys_the_button_while_busy()
    {
        CharacterDeleteModal modal = NewDelete();

        List<SceneSprite> empty = Build(modal);
        Assert.Equal(1, Count(empty, "delete-modal-input-placeholder"));
        Assert.Equal(0, Count(empty, "delete-modal-input-text"));
        Assert.Equal(1, Count(empty, "delete-modal-confirm"));
        Assert.Equal(0, Count(empty, "delete-modal-confirm-disabled"));

        modal.Type("HeroA");
        List<SceneSprite> typed = Build(modal);
        Assert.Equal(0, Count(typed, "delete-modal-input-placeholder"));
        Assert.Equal(1, Count(typed, "delete-modal-input-text"));

        modal.SetBusy(true);
        List<SceneSprite> busy = Build(modal);
        Assert.Equal(0, Count(busy, "delete-modal-confirm"));
        Assert.Equal(1, Count(busy, "delete-modal-confirm-disabled"));
        Assert.Equal(1, Count(busy, "delete-modal-busy"));
        Assert.Equal(0, Count(busy, "delete-modal-error"));
    }

    [Fact]
    public void The_delete_modal_draws_a_failure_in_the_notice_bar_and_nothing_when_all_is_quiet()
    {
        CharacterDeleteModal modal = NewDelete();
        List<SceneSprite> quiet = Build(modal);
        Assert.Equal(0, Count(quiet, "delete-modal-error"));
        Assert.Equal(0, Count(quiet, "delete-modal-busy"));

        modal.SetFailure(OnlineCharacterManageStatus.GuildMasterCannotDelete);
        List<SceneSprite> failed = Build(modal);
        Assert.Equal(1, Count(failed, "delete-modal-error"));
        Assert.Equal(0, Count(failed, "delete-modal-busy"));
    }

    // ------------------------------------------------------------------ 文案表:未知错误带两位大写十六进制

    [Theory]
    [InlineData(null, "服务器返回未知错误", "UNKNOWN SERVER ERROR")]
    [InlineData((byte)0x0A, "服务器返回未知错误（0X0A）", "UNKNOWN SERVER ERROR 0X0A")]
    [InlineData((byte)0xAB, "服务器返回未知错误（0XAB）", "UNKNOWN SERVER ERROR 0XAB")]
    [InlineData((byte)0x00, "服务器返回未知错误（0X00）", "UNKNOWN SERVER ERROR 0X00")]
    public void The_unknown_error_message_carries_the_raw_code_as_two_upper_case_hex_digits_when_there_is_one(
        byte? raw, string expectedCjk, string expectedAscii)
    {
        Assert.Equal(expectedCjk, OnlineCharacterManageText.Message(OnlineCharacterManageStatus.UnknownError, true, raw));
        Assert.Equal(expectedAscii, OnlineCharacterManageText.Message(OnlineCharacterManageStatus.UnknownError, false, raw));
    }

    [Fact]
    public void A_raw_code_is_ignored_by_every_other_message_and_success_has_no_message()
    {
        Assert.Equal("该角色名已被使用", OnlineCharacterManageText.Message(OnlineCharacterManageStatus.DuplicateActiveName, true, 0x12));
        Assert.Equal("CHARACTER NAME ALREADY EXISTS", OnlineCharacterManageText.Message(OnlineCharacterManageStatus.DuplicateActiveName, false, 0x12));
        Assert.Equal(string.Empty, OnlineCharacterManageText.Message(OnlineCharacterManageStatus.Succeeded, true));
        Assert.Equal(string.Empty, OnlineCharacterManageText.NameError(OnlineCharacterNameError.Valid, true));
    }

    // ------------------------------------------------------------------ 第二批:退格、孤立代理项、点第 0 格、未知键、失败提示的清除

    [Fact]
    public void A_backspace_removes_a_single_character_name_and_does_nothing_on_an_empty_one()
    {
        CharacterCreateModal modal = NewCreate();
        modal.Type("a");

        modal.Type("\b");
        Assert.Equal(string.Empty, modal.Name);

        modal.Type("\b");
        Assert.Equal(string.Empty, modal.Name);

        modal.Type("b\b\bc");   // b 打上、退掉,第二个退格落在空名字上,再打 c
        Assert.Equal("c", modal.Name);
    }

    [Fact]
    public void A_backspace_removes_a_single_character_confirmation_and_does_nothing_on_an_empty_one()
    {
        CharacterDeleteModal modal = NewDelete();
        modal.Type("a");

        modal.Type("\b");
        Assert.Equal(string.Empty, modal.ConfirmName);

        modal.Type("\b");
        Assert.Equal(string.Empty, modal.ConfirmName);

        modal.Type("b\b\bc");
        Assert.Equal("c", modal.ConfirmName);
    }

    /// <summary>孤立的代理项(没有配对的半个字符)编不出 UTF-8:字段里照收,但字节数按 0 算(不是 -1 也不是 1),名字检查报编码无效。</summary>
    [Fact]
    public void A_lone_surrogate_stays_in_the_field_but_counts_zero_bytes_and_is_an_invalid_encoding()
    {
        CharacterCreateModal modal = NewCreate();

        modal.Type("\uD800");

        Assert.Equal("\uD800", modal.Name);
        Assert.Equal(0, modal.NameByteCount);
        Assert.Equal(OnlineCharacterNameError.InvalidUtf8, modal.NameError);

        modal.Type("\b");
        Assert.Equal(0, modal.NameByteCount);

        modal.Type("é");      // 2 字节
        Assert.Equal(2, modal.NameByteCount);
        modal.Type("汉");     // 3 字节
        Assert.Equal(5, modal.NameByteCount);
    }

    [Fact]
    public void Clicking_a_job_cell_selects_that_job_from_any_previous_selection_including_the_first()
    {
        CharacterCreateModal modal = NewCreate();
        modal.SelectJob(5);

        HitRect first = CharacterCreateModal.JobRect(0);
        Assert.True(modal.Click(first.X + 2, first.Y + 2));
        Assert.Equal((byte)0, modal.SelectedJob);

        for (int job = 10; job >= 0; job--)
        {
            HitRect cell = CharacterCreateModal.JobRect(job);
            Assert.True(modal.Click(cell.X + cell.Width / 2, cell.Y + cell.Height / 2));
            Assert.Equal((byte)job, modal.SelectedJob);
        }

        // 忙的时候点职业格不换职业
        modal.SetBusy(true);
        HitRect other = CharacterCreateModal.JobRect(7);
        Assert.True(modal.Click(other.X + 2, other.Y + 2));
        Assert.Equal((byte)0, modal.SelectedJob);
    }

    [Fact]
    public void A_key_the_modals_do_not_know_is_swallowed_and_changes_nothing()
    {
        CharacterCreateModal create = NewCreate();
        create.SelectJob(3);
        create.Type("Hero");

        Assert.True(create.Key((OnlineShellKey)999));
        Assert.True(create.Key(OnlineShellKey.Space));

        Assert.Equal((byte)3, create.SelectedJob);
        Assert.Equal("Hero", create.Name);
        Assert.Equal(CharacterManageModalAction.None, create.TakeAction());

        CharacterDeleteModal delete = NewDelete();
        delete.Type("Hero");

        Assert.True(delete.Key((OnlineShellKey)999));
        Assert.True(delete.Key(OnlineShellKey.Left));

        Assert.Equal("Hero", delete.ConfirmName);
        Assert.Equal(CharacterManageModalAction.None, delete.TakeAction());
        Assert.Null(delete.ErrorText);
    }

    [Fact]
    public void Typing_a_character_clears_the_delete_failure_but_a_dropped_character_does_not()
    {
        CharacterDeleteModal modal = NewDelete();
        modal.SetFailure(OnlineCharacterManageStatus.NoReply);
        Assert.Equal("等待服务器响应超时", modal.ErrorText);

        modal.Type("\u001f");     // 控制字符被丢掉,什么都没变
        Assert.Equal("等待服务器响应超时", modal.ErrorText);
        modal.Type("\b");         // 空输入上的退格也什么都没变
        Assert.Equal("等待服务器响应超时", modal.ErrorText);

        modal.Type("H");
        Assert.Null(modal.ErrorText);
        Assert.Equal("H", modal.ConfirmName);
    }

    [Fact]
    public void Typing_a_character_clears_the_create_failure_but_a_dropped_character_does_not()
    {
        CharacterCreateModal modal = NewCreate();
        modal.SetFailure(OnlineCharacterManageStatus.DuplicateActiveName);
        Assert.Equal("该角色名已被使用", modal.ErrorText);

        modal.Type("\u001f");
        Assert.Equal("该角色名已被使用", modal.ErrorText);
        modal.Type("\b");
        Assert.Equal("该角色名已被使用", modal.ErrorText);

        modal.Type("H");
        Assert.Null(modal.ErrorText);
        Assert.Equal("H", modal.Name);
    }

    // ------------------------------------------------------------------ 第二批:职业称号与预览头像怎么问外部来源

    [Fact]
    public void The_job_label_asks_the_titles_for_the_base_class_of_exactly_that_job()
    {
        var titles = new JobTitles();
        var modal = new CharacterCreateModal(titles, null, new SizedFonts());

        Assert.Equal("职业0", modal.JobLabel(0));
        Assert.Equal("职业7", modal.JobLabel(7));
        Assert.Equal("职业10", modal.JobLabel(10));

        Assert.Equal(new[] { (0, 0, 0), (7, 0, 0), (10, 0, 0) }, titles.Asked);
    }

    [Fact]
    public void Every_job_cell_and_the_preview_show_the_title_of_their_own_job()
    {
        var titles = new JobTitles();
        var modal = new CharacterCreateModal(titles, null, new SizedFonts());
        modal.SelectJob(4);

        List<SceneSprite> sprites = Build(modal);

        Assert.Equal(11, Count(sprites, "create-modal-job-text"));
        Assert.Equal(1, Count(sprites, "create-modal-preview-job"));
        Assert.DoesNotContain(titles.Asked, asked => asked.GrowType != 0 || asked.Awakening != 0);
        for (int job = 0; job <= 10; job++)
        {
            Assert.Contains((job, 0, 0), titles.Asked);
        }
    }

    /// <summary>预览头像的时钟:面板自己的时钟没走(0)就用宿主给的时间;走过之后只认自己的时钟,负的帧间隔不让它倒退。</summary>
    [Fact]
    public void The_preview_avatar_is_posed_for_the_selected_job_on_the_modals_own_clock()
    {
        var avatars = new RecordingAvatars();
        var modal = new CharacterCreateModal(new JobTitles(), avatars, new SizedFonts());
        modal.SelectJob(4);

        Build(modal, 777);
        modal.Advance(250);
        Build(modal, 9999);
        modal.Advance(100);
        Build(modal, 12345);
        modal.Advance(-50);
        modal.Advance(0);
        Build(modal, 5);

        Assert.Equal(new long[] { 777, 250, 350, 350 }, avatars.Poses.Select(p => p.AgeMs).ToArray());
        Assert.All(avatars.Poses, pose =>
        {
            Assert.Equal(string.Empty, pose.Name);
            Assert.Equal("职业4", pose.JobName);
            Assert.Equal(4, pose.Job);
            Assert.Equal(CharacterCreateModal.PreviewFootX, pose.FootX);
            Assert.Equal(CharacterCreateModal.PreviewFootY, pose.FootY);
        });
    }

    [Fact]
    public void The_preview_avatar_follows_the_selected_job_and_is_drawn_as_a_modal_sprite()
    {
        var avatars = new RecordingAvatars();
        var modal = new CharacterCreateModal(new JobTitles(), avatars, new SizedFonts());

        modal.SelectJob(9);
        List<SceneSprite> sprites = Build(modal);

        Assert.Equal(1, Count(sprites, "create-modal-avatar"));
        SceneSprite avatar = Assert.Single(SpriteQuery.Tagged(sprites, "create-modal-avatar"));
        Assert.Equal(CharacterCreateModal.PreviewFootX, avatar.ScreenX);
        Assert.Equal(CharacterCreateModal.PreviewFootY, avatar.ScreenY);
        Assert.Equal(("职业9", 9), (avatars.Poses[0].JobName, avatars.Poses[0].Job));
    }
}
