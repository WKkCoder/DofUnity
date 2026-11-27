using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 创建/删除角色模态面板的补充测试(r21-createcharui2):
/// 职业格在网格边缘的移动、服务端失败之后再提交的错误条与错误文字、输入框里"碰巧是占位词"的名字不被翻译。
/// 全部直接驱动模态对象,用内存里的合成字体与合成数据(名字、职业都是编的),不触发任何真实创建或删除。
/// </summary>
public sealed class CharacterManageModalsSettleTests
{
    /// <summary>
    /// 只肯给 12 像素字号的字体集:输入框里的数据字(16 像素)要不到字体,只能走点阵占位;
    /// 点阵占位的"占位词翻成中文"只在文字系统的本地化开关打开时才发生,模态面板自己把它关着。
    /// </summary>
    private sealed class OnlyTwelveFonts : IGlyphFonts
    {
        private readonly SizedFonts _inner = new SizedFonts();

        public IGlyphSource ForSize(int pixelSize) => pixelSize == 12 ? _inner.ForSize(12) : null;
    }

    private static List<SceneSprite> Build(ICharacterManageModal modal)
    {
        var sprites = new List<SceneSprite>();
        int order = 0;
        modal.BuildInto(sprites, ref order, 0);
        return sprites;
    }

    private static List<SceneSprite> Tagged(List<SceneSprite> sprites, string tag) => SpriteQuery.Tagged(sprites, tag);

    // ------------------------------------------------------------------ 职业格:网格边缘的移动

    /// <summary>
    /// 职业格是 2 列、最后一行只有第一列(职业号 0 到 10 共 11 个):一次往下走很多行,落在起始那一列的最后一个存在的格子上;
    /// 第二列的最后一格不存在,从它那一列往下走原地不动。
    /// </summary>
    [Fact]
    public void A_large_downward_move_lands_on_the_last_existing_cell_of_the_starting_column()
    {
        var modal = new CharacterCreateModal();
        modal.SelectJob(4);

        modal.MoveJob(0, 100);

        Assert.Equal(10, modal.SelectedJob);

        modal.SelectJob(5);
        modal.MoveJob(0, 100);
        Assert.Equal(5, modal.SelectedJob);
    }

    /// <summary>往右下斜着走、目标格不存在时(最后一行的第二格):横向有移动就落在最后一个职业上;只往下、没有横向移动就原地不动。</summary>
    [Fact]
    public void A_diagonal_move_onto_the_missing_last_cell_lands_on_the_last_job_but_a_straight_one_stays()
    {
        var modal = new CharacterCreateModal();
        modal.SelectJob(8);

        modal.MoveJob(1, 1);

        Assert.Equal(10, modal.SelectedJob);

        modal.SelectJob(9);
        modal.MoveJob(0, 1);
        Assert.Equal(9, modal.SelectedJob);
    }

    // ------------------------------------------------------------------ 服务端失败之后再提交

    /// <summary>
    /// 服务端失败的红条(<c>create-modal-error</c>)和本地校验不过的橙条(<c>create-modal-validation-error</c>)是两种提示,
    /// 靠"这条错误是不是服务端来的"这个标记区分:服务端失败之后再提交一个不合法的名字,要换成校验的橙条,不能还是服务端的红条。
    /// </summary>
    [Fact]
    public void A_name_error_found_after_a_server_failure_is_drawn_as_a_validation_error_not_a_server_error()
    {
        var modal = new CharacterCreateModal();
        modal.SetFailure(OnlineCharacterManageStatus.DuplicateActiveName);
        Assert.Single(Tagged(Build(modal), "create-modal-error"));

        modal.Submit();

        List<SceneSprite> sprites = Build(modal);
        Assert.Single(Tagged(sprites, "create-modal-validation-error"));
        Assert.Empty(Tagged(sprites, "create-modal-error"));
        Assert.Equal(CharacterManageModalAction.None, modal.TakeAction());
    }

    /// <summary>服务端失败之后原样再提交一次(名字没变、合法):旧的失败文字要在提交的同时清掉,不留到下一次结果回来。</summary>
    [Fact]
    public void Submitting_a_valid_name_again_clears_the_failure_text_of_the_previous_attempt_in_the_create_modal()
    {
        var modal = new CharacterCreateModal();
        modal.Type("HeroNew");
        modal.Submit();
        Assert.Equal(CharacterManageModalAction.Submit, modal.TakeAction());
        modal.SetBusy(true);
        modal.SetFailure(OnlineCharacterManageStatus.DuplicateActiveName);
        Assert.False(string.IsNullOrEmpty(modal.ErrorText));

        modal.Submit();

        Assert.Null(modal.ErrorText);
        Assert.Empty(Tagged(Build(modal), "create-modal-error"));
        Assert.Equal(CharacterManageModalAction.Submit, modal.TakeAction());
    }

    /// <summary>删除模态同理:确认名没变、仍然对得上,再提交一次时旧的失败文字要立刻清掉。</summary>
    [Fact]
    public void Submitting_the_same_matching_confirmation_again_clears_the_failure_text_of_the_previous_attempt_in_the_delete_modal()
    {
        var modal = new CharacterDeleteModal(1, "HeroOld", "SWORDMAN", 12);
        modal.Type("HeroOld");
        modal.Submit();
        Assert.Equal(CharacterManageModalAction.Submit, modal.TakeAction());
        modal.SetBusy(true);
        modal.SetFailure(OnlineCharacterManageStatus.NoReply);
        Assert.False(string.IsNullOrEmpty(modal.ErrorText));

        modal.Submit();

        Assert.Null(modal.ErrorText);
        Assert.Equal(CharacterManageModalAction.Submit, modal.TakeAction());
    }

    // ------------------------------------------------------------------ 输入框里的名字不翻译

    /// <summary>
    /// 玩家输入的名字是"数据文字",原样画:哪怕它碰巧是个占位词("CLASS" 在占位词表里有中文版),也不能被翻成中文。
    /// 用只给 12 像素字号的字体集让输入框的 16 像素字走点阵占位这条路:原样画是 5 个点阵字 31 × 9 像素,
    /// 被翻成两个汉字再用 12 像素字体画,尺寸就对不上了。
    /// </summary>
    [Fact]
    public void A_typed_name_that_is_also_a_placeholder_word_is_drawn_as_typed_in_the_create_modal()
    {
        Assert.True(UiText.HasTranslation("CLASS"));
        var modal = new CharacterCreateModal(fonts: new OnlyTwelveFonts());
        modal.Type("CLASS");

        SceneSprite typed = Assert.Single(Tagged(Build(modal), "create-modal-name-text"));

        Assert.Equal(OverlayArt.TextWidth("CLASS", 1), typed.Frame.Width);
        Assert.Equal(OverlayArt.TextHeight(1), typed.Frame.Height);
    }

    [Fact]
    public void A_typed_confirmation_that_is_also_a_placeholder_word_is_drawn_as_typed_in_the_delete_modal()
    {
        Assert.True(UiText.HasTranslation("CLASS"));
        var modal = new CharacterDeleteModal(1, "CLASS", "SWORDMAN", 12, new OnlyTwelveFonts());
        modal.Type("CLASS");

        SceneSprite typed = Assert.Single(Tagged(Build(modal), "delete-modal-input-text"));

        Assert.Equal(OverlayArt.TextWidth("CLASS", 1), typed.Frame.Width);
        Assert.Equal(OverlayArt.TextHeight(1), typed.Frame.Height);
    }
}
