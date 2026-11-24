using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网结算画面顶部非阻塞提示条(<see cref="OnlineSettlementOverlay.Build"/> 里标签 <c>onlinesettle-notice</c> 一族的精灵)的宽度与摆放:
/// 条宽 = 色条图标的留白 56 + 文字帧宽,不超过 <see cref="OnlineSettlementOverlay.MaxNoticeWidth"/>(560);
/// 条水平居中 x = (800 − 宽) / 2,顶 y = <see cref="OnlineSettlementOverlay.NoticeY"/>(48),条高 32;
/// 图标左起 x + 16、垂直在条内居中((32 − 20) / 2 = 6);文字左起 x + 44、垂直居中((32 − 字高) / 2)。
/// 有字体用合成字体 <see cref="OnlineSettlementGapCKit.ProbeFonts"/>(窄字前进 3、全角 4、帧宽 = 前进之和 + 2,14 号字帧高 16);无字体用点阵字库。
/// </summary>
public sealed class OnlineSettlementGapCNoticeTests
{
    private const string Tag = "onlinesettle-notice";

    private static List<SceneSprite> Build(OverlayArt art, OnlineSettlementNotice notice, long timeMs = 0)
    {
        var state = new OnlineSettlementRenderState
        {
            Summary = OnlineSettlementGapCKit.BareSummary(),
            Notice = notice,
            AgeMs = 0,
            IsChoiceEnabled = SettlementArt.IsEnabled,
        };
        return OnlineSettlementOverlay.Build(art, state, timeMs);
    }

    private static SceneSprite One(List<SceneSprite> hud, string tag) => Assert.Single(hud, s => s.AnimationPath == tag);

    private static bool IsNoticeSprite(SceneSprite s) => s.AnimationPath.StartsWith(Tag, StringComparison.Ordinal);

    // 保护的行为:画面几个版式常量的数值(提示条最宽 560、顶 y = 48、屏幕 800 × 600)。
    [Fact]
    public void NoticeLayoutConstantsAreFixed()
    {
        Assert.Equal(560, OnlineSettlementOverlay.MaxNoticeWidth);
        Assert.Equal(48, OnlineSettlementOverlay.NoticeY);
        Assert.Equal(800, OnlineSettlementOverlay.ScreenWidth);
        Assert.Equal(600, OnlineSettlementOverlay.ScreenHeight);
    }

    // 保护的行为:短提示按文字量出条宽并水平居中;条、色条、图标、文字四个精灵的位置、尺寸、先后次序与错误语气的图标。有字体路径。
    [Fact]
    public void ShortNoticeWithFontsIsSizedByItsTextAndCenteredAtNoticeY()
    {
        var notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.SendFailed, null, "ABCDE");
        List<SceneSprite> hud = Build(new OverlayArt { Fonts = new OnlineSettlementGapCKit.ProbeFonts() }, notice);

        // 文字 5 个窄字 → 前进 15 → 帧宽 17;条宽 = 56 + 17 = 73;x = (800 − 73) / 2 = 363
        SceneSprite bar = One(hud, Tag + "-bar");
        Assert.Equal((363, 48), (bar.ScreenX, bar.ScreenY));
        Assert.Equal((73, 32), (bar.Frame.Width, bar.Frame.Height));
        // 左边的色条:x + 1、y + 1,宽 4、高 32 − 2 = 30
        SceneSprite accent = One(hud, Tag + "-accent");
        Assert.Equal((364, 49), (accent.ScreenX, accent.ScreenY));
        Assert.Equal((4, 30), (accent.Frame.Width, accent.Frame.Height));
        // 图标 20 × 20:x + 16 = 379,y + (32 − 20) / 2 = 54(错误语气画图标,不是转圈)
        SceneSprite icon = One(hud, Tag + "-icon");
        Assert.Equal((379, 54), (icon.ScreenX, icon.ScreenY));
        Assert.Equal((20, 20), (icon.Frame.Width, icon.Frame.Height));
        Assert.DoesNotContain(hud, s => s.AnimationPath == Tag + "-spinner");
        // 文字:14 号字帧高 16;x + 44 = 407;y + (32 − 16) / 2 = 56
        SceneSprite text = One(hud, Tag);
        Assert.Equal((17, 16), (text.Frame.Width, text.Frame.Height));
        Assert.Equal((407, 56), (text.ScreenX, text.ScreenY));
        Assert.Contains(0xFFC4A6u, OnlineSettlementGapCKit.OpaqueColors(text.Frame));                 // 错误语气的字色
        // 四个精灵排在整张序号的最后,依次连续;全体序号连续
        Assert.Equal(new[] { Tag + "-bar", Tag + "-accent", Tag + "-icon", Tag }, hud.Skip(hud.Count - 4).Select(s => s.AnimationPath));
        Assert.Equal(Enumerable.Range(0, hud.Count), hud.Select(s => s.SortKey));
        Assert.All(hud.Skip(hud.Count - 4), s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        Assert.All(hud.Skip(hud.Count - 4), s => Assert.Equal(255, s.TintAlpha));
    }

    // 保护的行为:很长的提示被夹到最宽 560:条宽恰为 560、居中 x = (800 − 560) / 2 = 120,左右两边留白对称,文字被截断并缩小字号。有字体路径。
    [Fact]
    public void LongNoticeWithFontsIsClampedToMaxNoticeWidthAndCentered()
    {
        var notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.SendFailed, null, new string('A', 300));
        List<SceneSprite> hud = Build(new OverlayArt { Fonts = new OnlineSettlementGapCKit.ProbeFonts() }, notice);

        SceneSprite bar = One(hud, Tag + "-bar");
        Assert.Equal(560, bar.Frame.Width);
        Assert.True(bar.Frame.Width <= OnlineSettlementOverlay.MaxNoticeWidth);
        Assert.Equal((120, 48), (bar.ScreenX, bar.ScreenY));
        // 左右留白相等:左 120、右 800 − (120 + 560) = 120
        Assert.Equal(bar.ScreenX, 800 - (bar.ScreenX + bar.Frame.Width));

        // 文字区宽 = 560 − 56 = 504:字号缩到 10(最多缩 4),放不下再截断补省略号:166 个窄字 × 3 + 省略号 4 = 502,帧宽 504;帧高 10 + 2 = 12
        SceneSprite text = One(hud, Tag);
        Assert.Equal((504, 12), (text.Frame.Width, text.Frame.Height));
        // x = 120 + 44 = 164;y = 48 + (32 − 12) / 2 = 58
        Assert.Equal((164, 58), (text.ScreenX, text.ScreenY));
    }

    // 保护的行为:条宽随文字量逐像素增长,到 560 封顶,居中用整数除法(向下取整):
    // 窄字前进 3,字号 14 的一行字(不缩小时)帧宽 = 3 × 字数 + 2,文字区宽上限 = 560 − 56 = 504。
    //   165 字:帧宽 497 → 条宽 553,x = (800 − 553) / 2 = 123;   167 字:帧宽 503 → 条宽 559,x = 120;
    //   168 字:前进 504 恰好放得下 → 帧宽 506 → 条宽 min(560, 562) = 560,x = 120(恰到上限);
    //   169 字:前进 507 放不下 → 字号缩到 10 再截断 → 166 字 + 省略号,帧宽 504 → 条宽 560,x = 120。
    [Theory]
    [InlineData(165, 553, 123, 497, 16)]
    [InlineData(167, 559, 120, 503, 16)]
    [InlineData(168, 560, 120, 506, 16)]
    [InlineData(169, 560, 120, 504, 12)]
    public void NoticeBarWidthGrowsWithTheTextAndStopsAtTheMaximum(int letters, int barWidth, int barX, int textWidth, int textHeight)
    {
        var notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.SendFailed, null, new string('A', letters));
        List<SceneSprite> hud = Build(new OverlayArt { Fonts = new OnlineSettlementGapCKit.ProbeFonts() }, notice);

        SceneSprite bar = One(hud, Tag + "-bar");
        Assert.Equal(barWidth, bar.Frame.Width);
        Assert.Equal(barX, bar.ScreenX);
        SceneSprite text = One(hud, Tag);
        Assert.Equal((textWidth, textHeight), (text.Frame.Width, text.Frame.Height));
        Assert.Equal((barX + 44, 48 + (32 - textHeight) / 2), (text.ScreenX, text.ScreenY));
    }

    // 保护的行为:无字体时提示用点阵字库画;等待语气(正在推进)的图标位置画转圈而不是图标;条宽 = 56 + 文字帧宽。
    [Fact]
    public void NoticeWithoutFontsUsesBlockTextAndTheWaitToneShowsASpinner()
    {
        var art = new OverlayArt();
        Assert.False(art.HasFonts);
        var notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.Advancing);
        Assert.Equal("ADVANCING SETTLEMENT...", notice.Message(hasFonts: false));
        List<SceneSprite> hud = Build(art, notice, timeMs: 240);

        // 23 个字:点阵 1 倍帧宽 = 23 × 6 − 1 + 2 = 139、帧高 9;条宽 = 56 + 139 = 195;x = (800 − 195) / 2 = 302
        SceneSprite bar = One(hud, Tag + "-bar");
        Assert.Equal((302, 48), (bar.ScreenX, bar.ScreenY));
        Assert.Equal((195, 32), (bar.Frame.Width, bar.Frame.Height));
        // 转圈:x + 16 = 318,y + 6 = 54;没有图标
        SceneSprite spinner = One(hud, Tag + "-spinner");
        Assert.Equal((318, 54), (spinner.ScreenX, spinner.ScreenY));
        Assert.DoesNotContain(hud, s => s.AnimationPath == Tag + "-icon");
        // 转圈的相位 = 240 / 80 % 10 = 3
        Assert.Equal(new ShellPaint(art).Spinner(20, 3, ShellPaint.ToneColor(ShellTone.Wait)).Rgba, spinner.Frame.Rgba);
        // 文字:x + 44 = 346;y + (32 − 9) / 2 = 59;就是同文字、1 倍、等待语气字色的点阵帧
        SceneSprite text = One(hud, Tag);
        Assert.Same(art.Text("ADVANCING SETTLEMENT...", 1, OnlineShellArt.ToneTextColor(ShellTone.Wait), ShellPaint.Navy), text.Frame);
        Assert.Equal((139, 9), (text.Frame.Width, text.Frame.Height));
        Assert.Equal((346, 59), (text.ScreenX, text.ScreenY));
        Assert.Equal(Enumerable.Range(0, hud.Count), hud.Select(s => s.SortKey));
    }

    // 保护的行为:没有提示、提示种类为"无"、或文字为空(宽度量出来是 0)时,不画任何提示条精灵,也不抛异常;
    // 有字体与无字体两条路径一样。有文字(宽度大于 0)时才画(上面几条已覆盖)。
    [Fact]
    public void NoNoticeOrAnEmptyNoticeDrawsNothing()
    {
        var emptyMessageKind = (OnlineSettlementNoticeKind)99;      // 未定义的种类:没有文案(空串)
        Assert.Equal(string.Empty, new OnlineSettlementNotice(emptyMessageKind).Message(hasFonts: true));

        foreach (OverlayArt art in new[] { new OverlayArt(), new OverlayArt { Fonts = new OnlineSettlementGapCKit.ProbeFonts() } })
        {
            Assert.DoesNotContain(Build(art, null), IsNoticeSprite);
            Assert.DoesNotContain(Build(art, new OnlineSettlementNotice(OnlineSettlementNoticeKind.None)), IsNoticeSprite);
            // 种类为"无"时,即便带了自定义文案也不画
            Assert.DoesNotContain(Build(art, new OnlineSettlementNotice(OnlineSettlementNoticeKind.None, null, "ABC")), IsNoticeSprite);
            // 种类有效但文案为空:宽度 0,不画
            Assert.DoesNotContain(Build(art, new OnlineSettlementNotice(emptyMessageKind)), IsNoticeSprite);
            // 对照:同一画面有文字时确实画出提示条(排除上面的"没有"是因为整张画面都没出来)
            Assert.Contains(Build(art, new OnlineSettlementNotice(OnlineSettlementNoticeKind.SendFailed, null, "AB")), IsNoticeSprite);
        }
    }
}
