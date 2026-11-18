using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网城镇界面叠层测试:明标物品/装备/金币未同步、非鬼剑士显式提示、切区等待转圈与 0x44 剩余分钟数提示。
/// </summary>
public sealed class OnlineTownOverlayTests
{
    private static OnlineHeroProfile Profile(byte job)
    {
        var entry = new OnlineCharacterEntry(9201, System.Text.Encoding.UTF8.GetBytes("测试"), 0, job, 0, 20, 0, 0, Array.Empty<OnlineCharacterEquip>(),
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        return OnlineHeroProfile.FromEntry(entry, 0);
    }

    [Fact]
    public void Null_art_or_profile_returns_null_without_throwing()
    {
        Assert.Null(OnlineTownOverlay.Build(null, Profile(0), null, 0));
        Assert.Null(OnlineTownOverlay.Build(new OverlayArt(), null, null, 0));
    }

    [Fact]
    public void Overlay_explicitly_marks_unsynced_items_and_fallback_job_in_both_font_modes()
    {
        var fonts = new RecordingFonts();
        var fontArt = new OverlayArt { Fonts = fonts };
        var blockArt = new OverlayArt();

        IReadOnlyList<SceneSprite> normal = OnlineTownOverlay.Build(fontArt, Profile(0), null, 1000);
        Assert.NotNull(normal);
        Assert.Contains(normal, s => s.AnimationPath == "onlinetown-status");
        Assert.True(fonts.Asked("未同步"));
        Assert.False(fonts.Asked("暂按鬼剑士显示"));

        var fallbackFonts = new RecordingFonts();
        IReadOnlyList<SceneSprite> fallback = OnlineTownOverlay.Build(new OverlayArt { Fonts = fallbackFonts }, Profile(2), null, 1000);
        Assert.NotNull(fallback);
        Assert.True(fallbackFonts.Asked("暂按鬼剑士显示"));

        IReadOnlyList<SceneSprite> ascii = OnlineTownOverlay.Build(blockArt, Profile(2), null, 1000);
        Assert.NotNull(ascii);
        Assert.Contains(ascii, s => s.AnimationPath == "onlinetown-status-icon");
        Assert.Contains("FALLBACK", OnlineTownOverlay.StatusBadgeText(Profile(2), hasFonts: false));
        Assert.Contains("UNSYNCED", OnlineTownOverlay.StatusBadgeText(Profile(0), hasFonts: false));
    }

    [Fact]
    public void Area_prompts_render_spinner_when_waiting_and_minutes_when_forbidden()
    {
        var fonts = new RecordingFonts();
        var art = new OverlayArt { Fonts = fonts };

        List<SceneSprite> wait = OnlineTownOverlay.Build(art, Profile(0), new OnlineTownPrompt(OnlineTownPromptKind.Waiting), 240);
        Assert.Contains(wait, s => s.AnimationPath == "onlinetown-prompt-spinner");
        Assert.True(fonts.Asked("正在切换区域"));

        List<SceneSprite> forbidden = OnlineTownOverlay.Build(art, Profile(0), new OnlineTownPrompt(OnlineTownPromptKind.ForbiddenMinutes, minutes: 12), 240);
        Assert.Contains(forbidden, s => s.AnimationPath == "onlinetown-prompt-icon");
        Assert.True(fonts.Asked("12 分钟"));

        List<SceneSprite> rejected = OnlineTownOverlay.Build(art, Profile(0), new OnlineTownPrompt(OnlineTownPromptKind.Rejected), 240);
        Assert.Contains(rejected, s => s.AnimationPath == "onlinetown-prompt");
    }
}
