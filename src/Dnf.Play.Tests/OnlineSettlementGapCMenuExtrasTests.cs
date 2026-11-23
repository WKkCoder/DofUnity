using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网结算菜单下方的两条附加条(<see cref="OnlineSettlementOverlay.Build"/> 里的灰显"神秘商店"占位条与"自动回城"倒计时条)的版式与文案:
/// 手算坐标——面板 x = <see cref="SettlementArt.PanelX"/> = 512、宽 270;脚的顶 <see cref="SettlementArt.FooterY"/> = 196(= 面板顶 22 + 头 54 + 四行 120)、
/// 脚高 16;所以神秘商店条的顶 = 196 + 16 + 6 = 218,条高 24,条间留 4 → 倒计时条的顶 = 218 + 24 + 4 = 246;
/// 文字在条内水平从 x + 10 = 522 起、垂直居中((24 − 字高)/ 2)。背景块的第 6 个参数是整体不透明度(神秘商店条 190、倒计时条 200)。
/// 有字体用"随字号与字符变化"的合成字体 <see cref="OnlineSettlementGapCKit.ProbeFonts"/>(字号 12 → 帧高 14,宽读出内容),无字体用点阵字库(1 倍 → 帧高 9)。
/// </summary>
public sealed class OnlineSettlementGapCMenuExtrasTests
{
    private static List<SceneSprite> Build(OverlayArt art, long remainingMs = 42500, bool showCountdown = true)
    {
        var state = new OnlineSettlementRenderState
        {
            Summary = OnlineSettlementGapCKit.BareSummary(),
            AgeMs = 0,
            IsChoiceEnabled = SettlementArt.IsEnabled,
            RemainingCountdownMs = remainingMs,
            ShowCountdown = showCountdown,
        };
        return OnlineSettlementOverlay.Build(art, state, 0);
    }

    private static SceneSprite One(List<SceneSprite> hud, string tag) => Assert.Single(hud, s => s.AnimationPath == tag);

    private static uint[] Colors(SpriteFrame frame) => OnlineSettlementGapCKit.OpaqueColors(frame).OrderBy(c => c).ToArray();

    // 保护的行为:神秘商店占位条紧贴菜单脚的下方(顶 = 脚顶 + 脚高 + 6)、与面板同宽、半透明深色底(整体不透明度 190),
    // 文字在条内左起 10 像素、垂直居中,用 12 号字与灰色;序号上文字紧跟背景之后(文字层高于背景)。有字体的路径。
    [Fact]
    public void SecretShopBarWithFontsSitsSixPixelsBelowTheFooterAndCentersItsTwelvePixelText()
    {
        var fonts = new OnlineSettlementGapCKit.ProbeFonts();
        List<SceneSprite> hud = Build(new OverlayArt { Fonts = fonts }, showCountdown: false);

        // 脚顶 196 + 脚高 16 + 6 = 218
        Assert.Equal(196, SettlementArt.FooterY);
        Assert.Equal(16, SettlementArt.FooterHeight);
        SceneSprite bg = One(hud, "settle-secretshop-bg");
        Assert.Equal((512, 218), (bg.ScreenX, bg.ScreenY));
        Assert.Equal((270, 24), (bg.Frame.Width, bg.Frame.Height));
        Assert.Equal(new[] { 0x161A22u }, Colors(bg.Frame));
        Assert.Equal(190, bg.TintAlpha);
        Assert.Equal(OverlayArt.UiRank, bg.LayerRank);

        // 文字:12 号字 → 帧高 12 + 2 = 14;帧宽 = "神秘商店 (本阶段未开放)" 的前进之和 + 2 = (10 个全角 × 4) + 空格 2 + 两个括号 3 + 3 + 2 = 50
        SceneSprite text = One(hud, "settle-secretshop-disabled");
        Assert.Equal((50, 14), (text.Frame.Width, text.Frame.Height));
        // x = 512 + 10 = 522;y = 218 + (24 − 14) / 2 = 223
        Assert.Equal((522, 223), (text.ScreenX, text.ScreenY));
        Assert.Equal(255, text.TintAlpha);
        Assert.Equal(OverlayArt.UiRank, text.LayerRank);
        Assert.Contains(0x7A8290u, OnlineSettlementGapCKit.OpaqueColors(text.Frame));
        // 文字层高于背景:序号紧接背景之后
        Assert.Equal(bg.SortKey + 1, text.SortKey);
        // 没开倒计时:没有倒计时条
        Assert.DoesNotContain(hud, s => s.AnimationPath == "settle-countdown-bg" || s.AnimationPath == "settle-countdown");
    }

    // 同上,无字体(点阵字库)的路径:文字是 1 倍点阵的 "SECRET SHOP (LOCKED)"(帧高 9、宽 (20 × 6 − 1) + 2 = 121),y = 218 + (24 − 9) / 2 = 225。
    [Fact]
    public void SecretShopBarWithoutFontsSitsSixPixelsBelowTheFooterAndCentersItsBlockText()
    {
        var art = new OverlayArt();
        Assert.False(art.HasFonts);
        List<SceneSprite> hud = Build(art, showCountdown: false);

        SceneSprite bg = One(hud, "settle-secretshop-bg");
        Assert.Equal((512, 218), (bg.ScreenX, bg.ScreenY));
        Assert.Equal((270, 24), (bg.Frame.Width, bg.Frame.Height));
        Assert.Equal(190, bg.TintAlpha);

        SceneSprite text = One(hud, "settle-secretshop-disabled");
        Assert.Same(art.Text("SECRET SHOP (LOCKED)", 1, 0x7A8290u), text.Frame);
        Assert.Equal((121, 9), (text.Frame.Width, text.Frame.Height));
        Assert.Equal((522, 225), (text.ScreenX, text.ScreenY));
        Assert.Equal(255, text.TintAlpha);
        Assert.Equal(bg.SortKey + 1, text.SortKey);
    }

    // 保护的行为:倒计时条在神秘商店条之后(间隔 4,顶 = 218 + 24 + 4 = 246)、与面板同宽、不透明度 200 的深蓝底;
    // 文字左起 522、垂直居中(有字体 12 号字帧高 14 → y = 246 + 5 = 251)、序号紧跟背景;四个附加精灵的序号依次连续。
    [Fact]
    public void CountdownBarWithFontsSitsBelowTheShopBarWithItsOwnOpacityAndCenteredText()
    {
        List<SceneSprite> hud = Build(new OverlayArt { Fonts = new OnlineSettlementGapCKit.ProbeFonts() }, remainingMs: 42500);

        SceneSprite shopBg = One(hud, "settle-secretshop-bg");
        SceneSprite shopText = One(hud, "settle-secretshop-disabled");
        SceneSprite bg = One(hud, "settle-countdown-bg");
        Assert.Equal((512, 246), (bg.ScreenX, bg.ScreenY));
        Assert.Equal((270, 24), (bg.Frame.Width, bg.Frame.Height));
        Assert.Equal(new[] { 0x101826u }, Colors(bg.Frame));
        Assert.Equal(200, bg.TintAlpha);
        Assert.Equal(OverlayArt.UiRank, bg.LayerRank);

        SceneSprite text = One(hud, "settle-countdown");
        // 42500 毫秒 → 向上取整 43 秒;文字 "自动返回城镇倒计时: 43 秒" 的帧宽 = 9 个全角 × 4 + 冒号 3 + 空格 2 + (数字 4 → 6、3 → 5)+ 空格 2 + 全角 4 + 2 = 49 + 11 = 60
        Assert.Equal((60, 14), (text.Frame.Width, text.Frame.Height));
        Assert.Equal((522, 251), (text.ScreenX, text.ScreenY));
        Assert.Equal(255, text.TintAlpha);
        Assert.Equal(bg.SortKey + 1, text.SortKey);
        // 四个附加精灵的序号依次连续:商店底、商店字、倒计时底、倒计时字
        Assert.Equal(new[] { shopBg.SortKey, shopBg.SortKey + 1, shopBg.SortKey + 2, shopBg.SortKey + 3 },
            new[] { shopBg.SortKey, shopText.SortKey, bg.SortKey, text.SortKey });
    }

    // 同上,无字体路径:倒计时文字是 1 倍点阵 "AUTO RETURN TO TOWN: 43S"(帧高 9),y = 246 + (24 − 9) / 2 = 253。
    [Fact]
    public void CountdownBarWithoutFontsSitsBelowTheShopBarWithCenteredBlockText()
    {
        var art = new OverlayArt();
        List<SceneSprite> hud = Build(art, remainingMs: 42500);

        SceneSprite bg = One(hud, "settle-countdown-bg");
        Assert.Equal((512, 246), (bg.ScreenX, bg.ScreenY));
        Assert.Equal(200, bg.TintAlpha);

        SceneSprite text = One(hud, "settle-countdown");
        Assert.Same(art.Text("AUTO RETURN TO TOWN: 43S", 1, 0xFFE9A0u), text.Frame);
        Assert.Equal((522, 253), (text.ScreenX, text.ScreenY));
        Assert.Equal(bg.SortKey + 1, text.SortKey);
    }

    // 保护的行为:倒计时秒数 = 剩余毫秒向上取整到秒(1 毫秒也算 1 秒,整秒不多算,负数当 0),
    // 剩 10 秒及以内字色转橙(0xFFB060),否则是浅黄(0xFFE9A0)。有字体路径:秒数从帧宽读出——
    // 帧宽 = 49 + 各位数字的 (2 + 数字值);0 → 51,1 → 52,2 → 53,3 → 54,10 → 54(3 + 2),11 → 55,60 → 59(8 + 2)。
    [Theory]
    [InlineData(1L, 1, 52)]
    [InlineData(999L, 1, 52)]
    [InlineData(1000L, 1, 52)]
    [InlineData(1001L, 2, 53)]
    [InlineData(1999L, 2, 53)]
    [InlineData(2000L, 2, 53)]
    [InlineData(2001L, 3, 54)]
    [InlineData(0L, 0, 51)]
    [InlineData(-1L, 0, 51)]
    [InlineData(-5000L, 0, 51)]
    [InlineData(10000L, 10, 54)]
    [InlineData(10001L, 11, 55)]
    [InlineData(59999L, 60, 59)]
    [InlineData(60000L, 60, 59)]
    public void CountdownSecondsRoundUpWithFonts(long remainingMs, int expectedSeconds, int expectedFrameWidth)
    {
        List<SceneSprite> hud = Build(new OverlayArt { Fonts = new OnlineSettlementGapCKit.ProbeFonts() }, remainingMs);
        SceneSprite text = One(hud, "settle-countdown");

        Assert.Equal(expectedFrameWidth, text.Frame.Width);
        Assert.Equal(14, text.Frame.Height);
        HashSet<uint> colors = OnlineSettlementGapCKit.OpaqueColors(text.Frame);
        uint expectedColor = expectedSeconds <= 10 ? 0xFFB060u : 0xFFE9A0u;
        uint otherColor = expectedSeconds <= 10 ? 0xFFE9A0u : 0xFFB060u;
        Assert.Contains(expectedColor, colors);
        Assert.DoesNotContain(otherColor, colors);
    }

    // 同上,无字体路径:文字帧就是 "AUTO RETURN TO TOWN: n S" 对应颜色的点阵帧(同参数同对象),n 即向上取整后的秒数。
    [Theory]
    [InlineData(1L, 1)]
    [InlineData(999L, 1)]
    [InlineData(1000L, 1)]
    [InlineData(1001L, 2)]
    [InlineData(1999L, 2)]
    [InlineData(2000L, 2)]
    [InlineData(2001L, 3)]
    [InlineData(0L, 0)]
    [InlineData(-1L, 0)]
    [InlineData(-5000L, 0)]
    [InlineData(10000L, 10)]
    [InlineData(10001L, 11)]
    [InlineData(59999L, 60)]
    [InlineData(60000L, 60)]
    public void CountdownSecondsRoundUpWithoutFonts(long remainingMs, int expectedSeconds)
    {
        var art = new OverlayArt();
        List<SceneSprite> hud = Build(art, remainingMs);
        SceneSprite text = One(hud, "settle-countdown");

        uint color = expectedSeconds <= 10 ? 0xFFB060u : 0xFFE9A0u;
        Assert.Same(art.Text("AUTO RETURN TO TOWN: " + expectedSeconds + "S", 1, color), text.Frame);
    }
}
