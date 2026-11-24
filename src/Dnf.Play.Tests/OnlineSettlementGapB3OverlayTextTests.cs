using Dnf.Render;
using Kit = Dnf.Play.Tests.OnlineSettlementGapB3Kit;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 联网结算覆盖层"随字体画出来的文字"的逐字补测(区域 B,第三轮):
/// 神秘商店灰显条、自动返回城镇倒计时条、左侧结果各行、顶部提示条的文字,有字体 / 无字体两个版本都读回来逐字比对。
/// 有字体版本用"把码点编码进像素"的合成字体(<see cref="OnlineSettlementGapB3CodecFonts"/>)画,再由
/// <see cref="OnlineSettlementGapB3Kit.Decode"/> 把精灵读回字符串——既有测试只量精灵尺寸与位置,同样长度的错字量不出来;
/// 无字体版本用同一块画布的点阵字库帧缓存(同文字、同倍数、同颜色拿到同一个对象)加逐像素对照。
/// 同时钉住精灵标签、坐标、颜色与整张精灵列表的顺序。全部合成数据,期望值手算(推导写在注释里)。
/// </summary>
public sealed class OnlineSettlementGapB3OverlayTextTests
{
    private const string ShopZh = "神秘商店 (本阶段未开放)";
    private const string ShopEn = "SECRET SHOP (LOCKED)";
    private const string CountdownZhPrefix = "自动返回城镇倒计时: ";
    private const string CountdownZhSuffix = " 秒";
    private const string CountdownEnPrefix = "AUTO RETURN TO TOWN: ";
    private const string CountdownEnSuffix = "S";

    private const uint ShopColor = 0x7A8290u;
    private const uint CountdownNormal = 0xFFE9A0u;
    private const uint CountdownUrgent = 0xFFB060u;
    private const uint ResultWhite = 0xF4F4F4u;
    private const uint ResultNote = 0xB8C6DAu;

    private static List<SceneSprite> Build(
        OverlayArt art,
        OnlineSettlementSummary summary,
        bool countdown = false,
        long remainMs = 0,
        OnlineSettlementNotice notice = null,
        double ageMs = 0,
        ISpriteSource sprites = null)
    {
        return OnlineSettlementOverlay.Build(art, new OnlineSettlementRenderState
        {
            Summary = summary,
            Notice = notice,
            AgeMs = ageMs,
            IsChoiceEnabled = SettlementArt.IsEnabled,
            RemainingCountdownMs = remainMs,
            ShowCountdown = countdown,
            Sprites = sprites,
        }, timeMs: 1000);
    }

    private static SceneSprite Only(List<SceneSprite> hud, string tag)
    {
        return Assert.Single(hud, s => s.AnimationPath == tag);
    }

    /// <summary>无字体时精灵必须正是点阵字库为"这段文字 + 倍数 + 颜色"画的那一帧(帧缓存同一对象),并且逐像素等于一块全新画布画的同一段文字。</summary>
    private static void AssertBitmapText(SpriteFrame actual, OverlayArt art, string text, int scale, uint fill, uint outline = OverlayArt.Black)
    {
        Assert.NotNull(actual);
        Assert.Same(art.Text(text, scale, fill, outline), actual);
        SpriteFrame fresh = new OverlayArt().Text(text, scale, fill, outline);
        Assert.Equal(fresh.Width, actual.Width);
        Assert.Equal(fresh.Height, actual.Height);
        Assert.Equal(fresh.Rgba, actual.Rgba);
    }

    // ================================================================== 神秘商店灰显条

    /// <summary>
    /// 保护的行为:有字体时,菜单下方的神秘商店灰显条画 "神秘商店 (本阶段未开放)"(逐字,由编码字体读回),字色 0x7A8290(描边黑),
    /// 字号 12(精灵高 = 12 + 2 = 14),摆在条底(512, 218)右移 10、垂直居中 → (522, 218 + (24 - 14) / 2 = 223);底色块 270×24、色 0x161A22、不透明度 190。
    /// </summary>
    [Fact]
    public void The_locked_secret_shop_bar_draws_its_chinese_text_verbatim_with_fonts()
    {
        OverlayArt art = Kit.FontArt(out _);

        List<SceneSprite> hud = Build(art, Kit.Summary());

        SceneSprite text = Only(hud, "settle-secretshop-disabled");
        Assert.Equal(ShopZh, Kit.Decode(text.Frame, ShopColor));
        Assert.Equal(new HashSet<uint> { ShopColor, 0x000000u }, Kit.OpaqueColors(text.Frame));
        Assert.Equal(14, text.Frame.Height);
        Assert.Equal(522, text.ScreenX);
        Assert.Equal(223, text.ScreenY);
        Assert.Equal(255, text.TintAlpha);
        Assert.Equal(OverlayArt.UiRank, text.LayerRank);

        SceneSprite bar = Only(hud, "settle-secretshop-bg");
        Assert.Equal(270, bar.Frame.Width);
        Assert.Equal(24, bar.Frame.Height);
        Assert.Equal(0xFF161A22u, Kit.FirstPixelArgb(bar.Frame));
        Assert.Equal(512, bar.ScreenX);
        Assert.Equal(218, bar.ScreenY);
        Assert.Equal(190, bar.TintAlpha);
    }

    /// <summary>
    /// 保护的行为:没有字体时,灰显条画点阵字库的 "SECRET SHOP (LOCKED)"(逐字、1 倍、色 0x7A8290),
    /// 精灵高 = 7 + 2 = 9 → y = 218 + (24 - 9) / 2 = 225,x = 522;底色块同有字体版本。
    /// </summary>
    [Fact]
    public void The_locked_secret_shop_bar_draws_its_english_text_verbatim_without_fonts()
    {
        var art = new OverlayArt();

        List<SceneSprite> hud = Build(art, Kit.Summary());

        SceneSprite text = Only(hud, "settle-secretshop-disabled");
        AssertBitmapText(text.Frame, art, ShopEn, 1, ShopColor);
        Assert.Equal(9, text.Frame.Height);
        Assert.Equal(522, text.ScreenX);
        Assert.Equal(225, text.ScreenY);

        SceneSprite bar = Only(hud, "settle-secretshop-bg");
        Assert.Equal(512, bar.ScreenX);
        Assert.Equal(218, bar.ScreenY);
        Assert.Equal(190, bar.TintAlpha);
        Assert.Equal(0xFF161A22u, Kit.FirstPixelArgb(bar.Frame));
    }

    // ================================================================== 自动返回倒计时条

    /// <summary>
    /// 保护的行为:有字体时倒计时条画 "自动返回城镇倒计时: N 秒"(逐字),N 是剩余毫秒向上取整的秒数(负数先夹到 0);
    /// 剩余不超过 10 秒用橙色 0xFFB060、否则 0xFFE9A0;字号 12(高 14),摆在 (522, 246 + (24 - 14) / 2 = 251);底色块 270×24、色 0x101826、不透明度 200、在 (512, 246)。
    /// </summary>
    [Theory]
    [InlineData(-5000, 0, true)]   // 负数夹到 0 → (0 + 999) / 1000 = 0
    [InlineData(0, 0, true)]       // (0 + 999) / 1000 = 0
    [InlineData(1, 1, true)]       // (1 + 999) / 1000 = 1
    [InlineData(999, 1, true)]     // (999 + 999) / 1000 = 1
    [InlineData(1000, 1, true)]    // (1000 + 999) / 1000 = 1(整秒不多算)
    [InlineData(1001, 2, true)]    // (1001 + 999) / 1000 = 2
    [InlineData(10000, 10, true)]  // 10 秒仍是橙色
    [InlineData(10001, 11, false)] // 11 秒起恢复浅黄
    [InlineData(42500, 43, false)] // (42500 + 999) / 1000 = 43
    [InlineData(60000, 60, false)] // 整 60 秒
    public void The_countdown_bar_draws_its_chinese_text_verbatim_with_rounded_up_seconds_and_the_urgent_color(int remainMs, int seconds, bool urgent)
    {
        OverlayArt art = Kit.FontArt(out _);
        uint color = urgent ? CountdownUrgent : CountdownNormal;

        List<SceneSprite> hud = Build(art, Kit.Summary(), countdown: true, remainMs: remainMs);

        SceneSprite text = Only(hud, "settle-countdown");
        string expected = CountdownZhPrefix + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + CountdownZhSuffix;
        Assert.Equal(expected, Kit.Decode(text.Frame, color));
        Assert.Equal(new HashSet<uint> { color, 0x000000u }, Kit.OpaqueColors(text.Frame));
        Assert.Equal(14, text.Frame.Height);
        Assert.Equal(522, text.ScreenX);
        Assert.Equal(251, text.ScreenY);
        Assert.Equal(255, text.TintAlpha);

        SceneSprite bar = Only(hud, "settle-countdown-bg");
        Assert.Equal(270, bar.Frame.Width);
        Assert.Equal(24, bar.Frame.Height);
        Assert.Equal(0xFF101826u, Kit.FirstPixelArgb(bar.Frame));
        Assert.Equal(512, bar.ScreenX);
        Assert.Equal(246, bar.ScreenY);
        Assert.Equal(200, bar.TintAlpha);
    }

    /// <summary>
    /// 保护的行为:没有字体时倒计时条画点阵字库的 "AUTO RETURN TO TOWN: NS"(逐字、1 倍),秒数与颜色规则同有字体版本;
    /// 精灵高 9 → y = 246 + (24 - 9) / 2 = 253,x = 522。
    /// </summary>
    [Theory]
    [InlineData(-5000, 0, true)]
    [InlineData(0, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(1000, 1, true)]
    [InlineData(1001, 2, true)]
    [InlineData(10000, 10, true)]
    [InlineData(10001, 11, false)]
    [InlineData(42500, 43, false)]
    [InlineData(60000, 60, false)]
    public void The_countdown_bar_draws_its_english_text_verbatim_with_rounded_up_seconds_and_the_urgent_color(int remainMs, int seconds, bool urgent)
    {
        var art = new OverlayArt();
        uint color = urgent ? CountdownUrgent : CountdownNormal;

        List<SceneSprite> hud = Build(art, Kit.Summary(), countdown: true, remainMs: remainMs);

        SceneSprite text = Only(hud, "settle-countdown");
        string expected = CountdownEnPrefix + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + CountdownEnSuffix;
        AssertBitmapText(text.Frame, art, expected, 1, color);
        Assert.Equal(9, text.Frame.Height);
        Assert.Equal(522, text.ScreenX);
        Assert.Equal(253, text.ScreenY);

        SceneSprite bar = Only(hud, "settle-countdown-bg");
        Assert.Equal(512, bar.ScreenX);
        Assert.Equal(246, bar.ScreenY);
        Assert.Equal(200, bar.TintAlpha);
        Assert.Equal(0xFF101826u, Kit.FirstPixelArgb(bar.Frame));
    }

    /// <summary>
    /// 保护的行为:不显示倒计时(<c>ShowCountdown</c> 为假)时,倒计时的底块与文字都不画——哪怕剩余时间有值;灰显商店条照画。
    /// </summary>
    [Fact]
    public void Without_the_countdown_flag_neither_countdown_sprite_is_drawn_in_either_language()
    {
        foreach (bool fonts in new[] { true, false })
        {
            OverlayArt art = fonts ? Kit.FontArt(out _) : new OverlayArt();

            List<SceneSprite> hud = Build(art, Kit.Summary(), countdown: false, remainMs: 42500);

            Assert.DoesNotContain(hud, s => s.AnimationPath == "settle-countdown-bg");
            Assert.DoesNotContain(hud, s => s.AnimationPath == "settle-countdown");
            Only(hud, "settle-secretshop-bg");
            Only(hud, "settle-secretshop-disabled");
        }
    }

    // ================================================================== 左侧结果各行

    /// <summary>
    /// 保护的行为:有字体、评分字母没能画成图(图包里没有)时,结果面板按行画文字精灵(标签 online-settle-reward):
    /// "评分 S"、"通关用时 --:--"、"掉落物品: --"、"提示: 物品/金币暂未同步至本地" 逐字读回;
    /// 字号 14(高 16),x = 24,y 从 24 起每行加 (16 + 5) = 21 → 24、45、66、87;前面各行白色 0xF4F4F4,最后一行(说明)0xB8C6DA。
    /// </summary>
    [Fact]
    public void Result_lines_are_drawn_verbatim_with_fonts_when_the_rank_letter_is_not_a_sprite()
    {
        OverlayArt art = Kit.FontArt(out _);

        List<SceneSprite> hud = Build(art, Kit.Summary(hasRank: true, rankLetter: "S"));

        List<SceneSprite> lines = hud.Where(s => s.AnimationPath == "online-settle-reward").ToList();
        string[] expected = { "评分 S", "通关用时 --:--", "掉落物品: --", "提示: 物品/金币暂未同步至本地" };
        int[] expectedY = { 24, 45, 66, 87 };
        Assert.Equal(expected.Length, lines.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            uint color = i == expected.Length - 1 ? ResultNote : ResultWhite;
            Assert.Equal(expected[i], Kit.Decode(lines[i].Frame, color));
            Assert.Equal(new HashSet<uint> { color, 0x000000u }, Kit.OpaqueColors(lines[i].Frame));
            Assert.Equal(16, lines[i].Frame.Height);
            Assert.Equal(24, lines[i].ScreenX);
            Assert.Equal(expectedY[i], lines[i].ScreenY);
        }
        Assert.DoesNotContain(hud, s => s.AnimationPath == ResultArt.Tag);
    }

    /// <summary>
    /// 保护的行为:无字体、评分字母没画成图时,结果各行画点阵字库的英文(2 倍):
    /// "RANK S"、"CLEAR TIME --:--"、"DROPS: --"、"NOTE: ITEMS/GOLD UNSYNCED";精灵高 7 × 2 + 2 = 16,y 同样是 24、45、66、87,颜色规则同有字体版本。
    /// </summary>
    [Fact]
    public void Result_lines_are_drawn_verbatim_without_fonts_when_the_rank_letter_is_not_a_sprite()
    {
        var art = new OverlayArt();

        List<SceneSprite> hud = Build(art, Kit.Summary(hasRank: true, rankLetter: "S"));

        List<SceneSprite> lines = hud.Where(s => s.AnimationPath == "online-settle-reward").ToList();
        string[] expected = { "RANK S", "CLEAR TIME --:--", "DROPS: --", "NOTE: ITEMS/GOLD UNSYNCED" };
        int[] expectedY = { 24, 45, 66, 87 };
        Assert.Equal(expected.Length, lines.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            uint color = i == expected.Length - 1 ? ResultNote : ResultWhite;
            AssertBitmapText(lines[i].Frame, art, expected[i], 2, color);
            Assert.Equal(16, lines[i].Frame.Height);
            Assert.Equal(24, lines[i].ScreenX);
            Assert.Equal(expectedY[i], lines[i].ScreenY);
        }
        Assert.DoesNotContain(hud, s => s.AnimationPath == ResultArt.Tag);
    }

    /// <summary>
    /// 保护的行为:评分字母画成了图(图包里有这一帧)时,字母精灵(标签 rank)摆在 (24 + 帧偏移 x, 20 + 帧偏移 y) = (27, 25),
    /// 文字行不再重复 "评分"/"RANK" 行,而是从 y = 220 起排:220、241、262;有字体 / 无字体两个版本的文字逐字。
    /// </summary>
    [Fact]
    public void A_drawn_rank_letter_replaces_the_rank_text_line_and_moves_the_text_block_down()
    {
        var sprites = new Kit.LetterSprites();
        var rgba = new byte[48 * 48 * 4];
        rgba[3] = 255;
        sprites.Add(ResultArt.RankLetterImage, 2, new SpriteFrame(48, 48, 3, 5, 48, 48, rgba));

        // 有字体
        OverlayArt fontArt = Kit.FontArt(out _);
        List<SceneSprite> withFonts = Build(fontArt, Kit.Summary(hasRank: true, rankLetter: "S", rankFrame: 2), sprites: sprites);
        SceneSprite letter = Only(withFonts, ResultArt.Tag);
        Assert.Equal(27, letter.ScreenX);
        Assert.Equal(25, letter.ScreenY);
        List<SceneSprite> zhLines = withFonts.Where(s => s.AnimationPath == "online-settle-reward").ToList();
        string[] zh = { "通关用时 --:--", "掉落物品: --", "提示: 物品/金币暂未同步至本地" };
        int[] ys = { 220, 241, 262 };
        Assert.Equal(zh.Length, zhLines.Count);
        for (int i = 0; i < zh.Length; i++)
        {
            Assert.Equal(zh[i], Kit.Decode(zhLines[i].Frame, i == zh.Length - 1 ? ResultNote : ResultWhite));
            Assert.Equal(24, zhLines[i].ScreenX);
            Assert.Equal(ys[i], zhLines[i].ScreenY);
        }
        // 字母精灵先于文字行(排序值更小)
        Assert.True(letter.SortKey < zhLines[0].SortKey);

        // 无字体
        var plainArt = new OverlayArt();
        List<SceneSprite> withoutFonts = Build(plainArt, Kit.Summary(hasRank: true, rankLetter: "S", rankFrame: 2), sprites: sprites);
        Assert.Equal(27, Only(withoutFonts, ResultArt.Tag).ScreenX);
        List<SceneSprite> enLines = withoutFonts.Where(s => s.AnimationPath == "online-settle-reward").ToList();
        string[] en = { "CLEAR TIME --:--", "DROPS: --", "NOTE: ITEMS/GOLD UNSYNCED" };
        Assert.Equal(en.Length, enLines.Count);
        for (int i = 0; i < en.Length; i++)
        {
            AssertBitmapText(enLines[i].Frame, plainArt, en[i], 2, i == en.Length - 1 ? ResultNote : ResultWhite);
            Assert.Equal(ys[i], enLines[i].ScreenY);
        }
    }

    // ================================================================== 顶部提示条

    /// <summary>
    /// 保护的行为:有字体时,提示条把提示文字(这里用自定义文案,与模型里的固定文案解耦)逐字画出,字色按语气取:等待 0xC6DCFF、说明 0xFFE9A0、出错 0xFFC4A6,描边 0x0A1224;
    /// 文案 10 个字 → 文字精灵宽 3 × 10 + 2 = 32 → 提示条宽 56 + 32 = 88 → x = (800 - 88) / 2 = 356;文字在 (356 + 44, 48 + (32 - 16) / 2) = (400, 56);
    /// 条与色条在 (356, 48)、(357, 49);图标 (372, 54)(等待语气是转圈,标签 -spinner)。
    /// </summary>
    [Theory]
    [InlineData(OnlineSettlementNoticeKind.Advancing, 0xC6DCFFu, "onlinesettle-notice-spinner")]
    [InlineData(OnlineSettlementNoticeKind.CardSelectSkipped, 0xFFE9A0u, "onlinesettle-notice-icon")]
    [InlineData(OnlineSettlementNoticeKind.EplpRejectedNotLeader, 0xFFC4A6u, "onlinesettle-notice-icon")]
    public void The_notice_bar_draws_the_message_verbatim_with_the_tone_color_when_fonts_exist(
        OnlineSettlementNoticeKind kind, uint color, string iconTag)
    {
        OverlayArt art = Kit.FontArt(out _);
        const string Message = "合成提示ABC123";
        Assert.Equal(10, Message.Length);

        List<SceneSprite> hud = Build(art, Kit.Summary(), notice: new OnlineSettlementNotice(kind, null, Message));

        SceneSprite text = Only(hud, "onlinesettle-notice");
        Assert.Equal(Message, Kit.Decode(text.Frame, color));
        Assert.Equal(new HashSet<uint> { color, 0x0A1224u }, Kit.OpaqueColors(text.Frame));
        Assert.Equal(400, text.ScreenX);
        Assert.Equal(56, text.ScreenY);

        SceneSprite bar = Only(hud, "onlinesettle-notice-bar");
        Assert.Equal(88, bar.Frame.Width);
        Assert.Equal(32, bar.Frame.Height);
        Assert.Equal(356, bar.ScreenX);
        Assert.Equal(48, bar.ScreenY);
        SceneSprite accent = Only(hud, "onlinesettle-notice-accent");
        Assert.Equal(357, accent.ScreenX);
        Assert.Equal(49, accent.ScreenY);
        SceneSprite icon = Only(hud, iconTag);
        Assert.Equal(372, icon.ScreenX);
        Assert.Equal(54, icon.ScreenY);
        Assert.Equal(20, icon.Frame.Width);
    }

    /// <summary>
    /// 保护的行为:无字体时提示条画点阵字库的大写英文(1 倍,字色按语气、描边 0x0A1224),自定义文案 14 个字符 →
    /// 文字精灵宽 6 × 14 + 1 = 85 → 条宽 56 + 85 = 141 → x = (800 - 141) / 2 = 329;文字在 (329 + 44, 48 + (32 - 9) / 2) = (373, 59)。
    /// </summary>
    [Theory]
    [InlineData(OnlineSettlementNoticeKind.Advancing, 0xC6DCFFu)]
    [InlineData(OnlineSettlementNoticeKind.CardSelectSkipped, 0xFFE9A0u)]
    [InlineData(OnlineSettlementNoticeKind.EplpRejectedNotLeader, 0xFFC4A6u)]
    public void The_notice_bar_draws_the_message_with_the_bitmap_font_when_no_fonts_exist(OnlineSettlementNoticeKind kind, uint color)
    {
        var art = new OverlayArt();
        const string Message = "NOTICE ABC 123";
        Assert.Equal(14, Message.Length);

        List<SceneSprite> hud = Build(art, Kit.Summary(), notice: new OnlineSettlementNotice(kind, null, Message));

        SceneSprite text = Only(hud, "onlinesettle-notice");
        AssertBitmapText(text.Frame, art, Message, 1, color, 0x0A1224u);
        Assert.Equal(373, text.ScreenX);
        Assert.Equal(59, text.ScreenY);
        SceneSprite bar = Only(hud, "onlinesettle-notice-bar");
        Assert.Equal(141, bar.Frame.Width);
        Assert.Equal(329, bar.ScreenX);
        Assert.Equal(48, bar.ScreenY);
    }

    /// <summary>
    /// 保护的行为:没有自定义文案时,提示条画提示种类自己的固定文案(有字体取中文、无字体取英文)——这里用"连接已断开"逐字核对两个版本;
    /// 有字体 12 个字 → 宽 3 × 12 + 2 = 38 → 条宽 94 → x = (800 - 94) / 2 = 353,文字 x = 397;无字体 23 个字符 → 6 × 23 + 1 = 139 → 条宽 195 → x = 302,文字 x = 346。
    /// </summary>
    [Fact]
    public void The_notice_bar_falls_back_to_the_fixed_message_of_the_notice_kind_in_both_languages()
    {
        var notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.Disconnected);

        OverlayArt fontArt = Kit.FontArt(out _);
        SceneSprite zh = Only(Build(fontArt, Kit.Summary(), notice: notice), "onlinesettle-notice");
        Assert.Equal("与频道服务器的连接已断开", Kit.Decode(zh.Frame, 0xFFC4A6u));
        Assert.Equal(397, zh.ScreenX);

        var plainArt = new OverlayArt();
        SceneSprite en = Only(Build(plainArt, Kit.Summary(), notice: notice), "onlinesettle-notice");
        AssertBitmapText(en.Frame, plainArt, "CHANNEL CONNECTION LOST", 1, 0xFFC4A6u, 0x0A1224u);
        Assert.Equal(346, en.ScreenX);
    }

    /// <summary>保护的行为:没有提示(null)或提示种类为"无"时,不画任何提示条精灵。</summary>
    [Fact]
    public void No_notice_or_a_none_kind_draws_no_notice_sprites()
    {
        foreach (bool fonts in new[] { true, false })
        {
            foreach (OnlineSettlementNotice notice in new[] { null, new OnlineSettlementNotice(OnlineSettlementNoticeKind.None, null, "不会出现") })
            {
                OverlayArt art = fonts ? Kit.FontArt(out _) : new OverlayArt();
                List<SceneSprite> hud = Build(art, Kit.Summary(), notice: notice);
                Assert.DoesNotContain(hud, s => s.AnimationPath.StartsWith("onlinesettle-notice", StringComparison.Ordinal));
            }
        }
    }

    // ================================================================== 整张精灵列表

    /// <summary>
    /// 保护的行为:整张列表的标签、先后顺序与排序值(逐个递增、与下标相同)——两层黑幕(后层 BackDimRank、前层 DimRank,不透明度满 56,整屏 800×600)、
    /// 后备结算面板(底、标题、四行文字,三个热键提示)、灰显商店条(底 + 字)、倒计时条(底 + 字)、结果四行、提示条(条、色条、图标、字);
    /// 并且黑幕在 AgeMs 为 0 时一层都不画。
    /// </summary>
    [Fact]
    public void The_whole_sprite_list_keeps_its_tags_order_and_sort_keys()
    {
        OverlayArt art = Kit.FontArt(out _);
        var notice = new OnlineSettlementNotice(OnlineSettlementNoticeKind.EplpRejectedNotLeader, 8);

        List<SceneSprite> hud = Build(
            art,
            Kit.Summary(hasRank: true, rankLetter: "S"),
            countdown: true,
            remainMs: 30000,
            notice: notice,
            ageMs: DungeonSession.SettlementDimMs);

        string[] expectedTags =
        {
            "settle-dim-back", "settle-dim",
            "settle-fallback", "settle-title", "settle-label", "settle-hint", "settle-label", "settle-label", "settle-hint", "settle-label", "settle-hint",
            "settle-secretshop-bg", "settle-secretshop-disabled",
            "settle-countdown-bg", "settle-countdown",
            "online-settle-reward", "online-settle-reward", "online-settle-reward", "online-settle-reward",
            "onlinesettle-notice-bar", "onlinesettle-notice-accent", "onlinesettle-notice-icon", "onlinesettle-notice",
        };
        Assert.Equal(expectedTags, hud.Select(s => s.AnimationPath).ToArray());
        Assert.Equal(Enumerable.Range(0, expectedTags.Length).ToArray(), hud.Select(s => s.SortKey).ToArray());

        SceneSprite back = hud[0];
        SceneSprite front = hud[1];
        Assert.Equal(OverlayArt.BackDimRank, back.LayerRank);
        Assert.Equal(OverlayArt.DimRank, front.LayerRank);
        Assert.Equal(56, back.TintAlpha);
        Assert.Equal(56, front.TintAlpha);
        Assert.Equal(800, back.Frame.Width);
        Assert.Equal(600, back.Frame.Height);
        Assert.Same(back.Frame, front.Frame);

        // 没有结算停留时间:一层黑幕都不画
        List<SceneSprite> undimmed = Build(art, Kit.Summary(hasRank: true, rankLetter: "S"), ageMs: 0);
        Assert.DoesNotContain(undimmed, s => s.AnimationPath == "settle-dim-back" || s.AnimationPath == "settle-dim");
    }

    /// <summary>保护的行为:画布、状态或摘要缺任何一个都不出列表(返回 null,不抛异常)。</summary>
    [Fact]
    public void Build_returns_null_when_the_canvas_the_state_or_the_summary_is_missing()
    {
        OverlayArt art = Kit.FontArt(out _);
        var state = new OnlineSettlementRenderState { Summary = Kit.Summary() };

        Assert.Null(OnlineSettlementOverlay.Build(null, state, 0));
        Assert.Null(OnlineSettlementOverlay.Build(art, null, 0));
        Assert.Null(OnlineSettlementOverlay.Build(art, new OnlineSettlementRenderState(), 0));
        Assert.NotNull(OnlineSettlementOverlay.Build(art, state, 0));
    }
}
