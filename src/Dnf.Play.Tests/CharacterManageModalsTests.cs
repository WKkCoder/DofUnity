using Dnf.Render;

namespace Dnf.Play.Tests;

public sealed class CharacterManageModalsTests
{
    private sealed class FakeTitles : ICharacterTitles
    {
        public string DefaultName => "默认名";
        public string ClassName(int job, int growType, int awakening) => "职业" + job;
    }

    private sealed class FakeAvatars : ICharacterAvatars
    {
        public readonly List<(int Job, long AgeMs, int FootX, int FootY)> Calls = new();

        public IReadOnlyList<SceneSprite> Pose(CharacterSlot slot, long ageMs, int footX, int footY)
        {
            int job = slot?.Online?.Job ?? -1;
            Calls.Add((job, ageMs, footX, footY));
            var frame = new SpriteFrame(16, 24, 0, 0, 16, 24, new byte[16 * 24 * 4]);
            return new[] { OverlayArt.Place(frame, footX - 8, footY - 24, 0, "fake-avatar") };
        }
    }

    private static void ClickCenter(ICharacterManageModal modal, HitRect rect)
    {
        modal.Click(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    // ------------------------------------------------------------------ CharacterSelectScreen 开关与按钮

    [Fact]
    public void CharacterSelect_create_and_delete_default_to_disabled_and_enable_when_turned_on()
    {
        CharacterSelectScreen screen = CharSelectKit.Online(2);
        Assert.False(screen.CreateEnabled);
        Assert.False(screen.DeleteEnabled);
        Assert.Equal(CharacterSelectButton.None, screen.TakeManageAction());

        // 禁用时点击仍然弹暂未开放
        CharSelectKit.ClickButton(screen, CharacterSelectButton.Create);
        Assert.Equal(CharacterSelectPrompt.NotAvailable, screen.Prompt);
        Assert.Equal(CharacterSelectButton.None, screen.TakeManageAction());

        // 启用后按钮可触发动作，且视图不再画灰按钮
        screen.CreateEnabled = true;
        screen.DeleteEnabled = true;
        List<SceneSprite> sprites = screen.Build(0);
        Assert.Equal(2, SpriteQuery.Tagged(sprites, "select-char-button-disabled").Count == 0 ? 2 : 0);

        CharSelectKit.ClickButton(screen, CharacterSelectButton.Create);
        Assert.Equal(CharacterSelectButton.Create, screen.TakeManageAction());
        Assert.Equal(CharacterSelectButton.None, screen.TakeManageAction());

        CharSelectKit.ClickButton(screen, CharacterSelectButton.Delete);
        Assert.Equal(CharacterSelectButton.Delete, screen.TakeManageAction());
    }

    [Fact]
    public void Empty_roster_with_create_enabled_does_not_show_not_available_on_delete_click()
    {
        CharacterSelectScreen screen = CharSelectKit.Online(0);
        screen.CreateEnabled = true;
        screen.DeleteEnabled = false;

        CharSelectKit.ClickButton(screen, CharacterSelectButton.Delete);
        Assert.Equal(CharacterSelectPrompt.None, screen.Prompt);
        Assert.Equal(CharacterSelectButton.None, screen.TakeManageAction());
    }

    // ------------------------------------------------------------------ Create Modal

    [Fact]
    public void Create_modal_selects_all_11_jobs_uses_titles_and_renders_preview()
    {
        var avatars = new FakeAvatars();
        var fonts = new SizedFonts();
        var modal = new CharacterCreateModal(new FakeTitles(), avatars, fonts);

        for (byte job = 0; job <= 10; job++)
        {
            ClickCenter(modal, CharacterCreateModal.JobRect(job));
            Assert.Equal(job, modal.SelectedJob);
            Assert.Equal("职业" + job, modal.JobLabel(job));
        }

        // 无称号来源时回退到英文占位
        var fallbackModal = new CharacterCreateModal(null, avatars, null);
        Assert.Equal("SWORDMAN", fallbackModal.JobLabel(0));

        var sprites = new List<SceneSprite>();
        int order = 0;
        modal.Advance(240);
        modal.BuildInto(sprites, ref order, 240);

        Assert.Single(avatars.Calls);
        Assert.Equal((10, 240L, CharacterCreateModal.PreviewFootX, CharacterCreateModal.PreviewFootY), avatars.Calls[0]);
        Assert.Single(SpriteQuery.Tagged(sprites, "create-modal-avatar"));
        Assert.Single(SpriteQuery.Tagged(sprites, "create-modal-job-selected"));
    }

    [Fact]
    public void Create_modal_handles_cjk_backspace_control_chars_and_live_byte_validation()
    {
        var modal = new CharacterCreateModal(new FakeTitles(), null, new SizedFonts());
        modal.Type("剑\x01\n\r\t士\x7F甲");
        // 注意：\t 被控制字符过滤丢弃，得到 "剑士甲" (9 UTF-8 bytes)
        Assert.Equal("剑士甲", modal.Name);
        Assert.Equal(9, modal.NameByteCount);
        Assert.Equal(OnlineCharacterNameError.Valid, modal.NameError);

        modal.Type("\b");
        Assert.Equal("剑士", modal.Name);
        Assert.Equal(6, modal.NameByteCount);
        Assert.Equal(OnlineCharacterNameError.Valid, modal.NameError);

        modal.Type("\b");
        Assert.Equal("剑", modal.Name);
        Assert.Equal(3, modal.NameByteCount);
        Assert.Equal(OnlineCharacterNameError.TooShort, modal.NameError);

        // 过短时点击确认不发射 Submit，显示本地校验错误
        ClickCenter(modal, CharacterCreateModal.ConfirmButtonRect);
        Assert.Equal(CharacterManageModalAction.None, modal.TakeAction());
        Assert.False(string.IsNullOrEmpty(modal.ErrorText));

        // 补足到合法长度，再提交
        modal.Type("乙");
        Assert.Equal(6, modal.NameByteCount);
        Assert.Null(modal.ErrorText);
        ClickCenter(modal, CharacterCreateModal.ConfirmButtonRect);
        Assert.Equal(CharacterManageModalAction.Submit, modal.TakeAction());
    }

    [Fact]
    public void Create_modal_blocks_overlong_and_forbidden_names_and_maps_all_status_messages()
    {
        var modal = new CharacterCreateModal(null, null, new SizedFonts());
        modal.Type("ab c");
        Assert.Equal(OnlineCharacterNameError.ForbiddenAsciiCharacter, modal.NameError);
        modal.Submit();
        Assert.Equal(CharacterManageModalAction.None, modal.TakeAction());
        Assert.False(string.IsNullOrEmpty(modal.ErrorText));

        var longModal = new CharacterCreateModal(null, null, new SizedFonts());
        longModal.Type(new string('a', 19));
        Assert.Equal(OnlineCharacterNameError.TooLong, longModal.NameError);
        longModal.Submit();
        Assert.Equal(CharacterManageModalAction.None, longModal.TakeAction());

        // 验证全部枚举值都有中英文非空提示，且 UnknownError 保留十六进制错误码
        foreach (OnlineCharacterManageStatus status in Enum.GetValues<OnlineCharacterManageStatus>())
        {
            if (status == OnlineCharacterManageStatus.Succeeded)
            {
                continue;
            }
            string zh = OnlineCharacterManageText.Message(status, true, 0xAB);
            string en = OnlineCharacterManageText.Message(status, false, 0xAB);
            Assert.False(string.IsNullOrWhiteSpace(zh), status.ToString());
            Assert.False(string.IsNullOrWhiteSpace(en), status.ToString());
            if (status == OnlineCharacterManageStatus.UnknownError)
            {
                Assert.Contains("0XAB", zh);
                Assert.Contains("0XAB", en);
            }
        }
    }

    // ------------------------------------------------------------------ Delete Modal

    [Fact]
    public void Delete_modal_requires_exact_confirmation_name_and_supports_cancel_during_busy()
    {
        var modal = new CharacterDeleteModal(2, "剑士甲", "职业0", 25, new SizedFonts());
        modal.Type("剑\x07士\b甲");
        Assert.Equal("剑甲", modal.ConfirmName);
        Assert.False(modal.IsNameMatched);

        // 不匹配时不能提交
        ClickCenter(modal, CharacterDeleteModal.ConfirmButtonRect);
        Assert.Equal(CharacterManageModalAction.None, modal.TakeAction());
        Assert.False(string.IsNullOrEmpty(modal.ErrorText));

        // 逐字输入精确匹配后可提交
        modal.Type("\b士甲");
        Assert.Equal("剑士甲", modal.ConfirmName);
        Assert.True(modal.IsNameMatched);
        ClickCenter(modal, CharacterDeleteModal.ConfirmButtonRect);
        Assert.Equal(CharacterManageModalAction.Submit, modal.TakeAction());

        // 忙状态下禁止再次提交与改字，但取消/Esc 仍有效
        modal.SetBusy(true);
        modal.Type("extra");
        Assert.Equal("剑士甲", modal.ConfirmName);
        ClickCenter(modal, CharacterDeleteModal.ConfirmButtonRect);
        Assert.Equal(CharacterManageModalAction.None, modal.TakeAction());
        ClickCenter(modal, CharacterDeleteModal.CancelButtonRect);
        Assert.Equal(CharacterManageModalAction.Cancel, modal.TakeAction());

        // 渲染树含目标信息与忙碌条
        var sprites = new List<SceneSprite>();
        int order = 0;
        modal.BuildInto(sprites, ref order, 160);
        Assert.Single(SpriteQuery.Tagged(sprites, "delete-modal-target"));
        Assert.Single(SpriteQuery.Tagged(sprites, "delete-modal-busy-spinner"));
    }

    [Theory]
    [InlineData("create", true)]
    [InlineData("create", false)]
    [InlineData("delete", true)]
    [InlineData("delete", false)]
    public void CharCreateShot_builder_renders_offline_modals_with_or_without_fonts(string mode, bool useFonts)
    {
        IGlyphFonts fonts = useFonts ? new SizedFonts() : null;
        List<SceneSprite> sprites = Dnf.Cli.Analysis.CharCreateShot.BuildSprites(null, fonts, mode, 4, "Hero900", 120);
        Assert.NotEmpty(sprites);
        if (mode == "create")
        {
            Assert.Single(SpriteQuery.Tagged(sprites, "create-modal-panel"));
            Assert.Single(SpriteQuery.Tagged(sprites, "create-modal-job-selected"));
        }
        else
        {
            Assert.Single(SpriteQuery.Tagged(sprites, "delete-modal-panel"));
            Assert.Single(SpriteQuery.Tagged(sprites, "delete-modal-target"));
        }
    }
}
