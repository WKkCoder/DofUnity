using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// "每个字符前进量可控"的合成字体集(区域 B 第五轮,覆盖层最大宽度边界用):
/// 每个字符都是 1 像素宽、字号高的实心竖条,前进 1 像素;只有"松紧字符"(<see cref="SlackChar"/>)的前进由测试指定,
/// 于是一段已知文字的量宽(<see cref="GlyphText.Measure"/>,不含描边)可以被精确调成想要的数:
/// 其余字符个数 + 松紧字符的前进 = 量宽。前进与字号无关(缩小字号并不会让文字变窄),所以放不下的文字一定先缩到最小档、再被截断补省略号。
/// 帧宽 = 量宽 + 2(描边两边各 1),帧高 = 请求字号 + 2;<see cref="Requested"/> 记下被问过的字号。
/// </summary>
internal sealed class OnlineSettlementGapB5TuneFonts : IGlyphFonts
{
    public OnlineSettlementGapB5TuneFonts(char slackChar = '\0', int slackAdvance = 1)
    {
        SlackChar = slackChar;
        SlackAdvance = slackAdvance;
    }

    public char SlackChar { get; }

    public int SlackAdvance { get; }

    public List<int> Requested { get; } = new List<int>();

    public IGlyphSource ForSize(int pixelSize)
    {
        Requested.Add(pixelSize);
        return new OnlineSettlementGapB5TuneFont(pixelSize, SlackChar, SlackAdvance);
    }
}

internal sealed class OnlineSettlementGapB5TuneFont : IGlyphSource
{
    private readonly int _px;
    private readonly char _slackChar;
    private readonly int _slackAdvance;

    public OnlineSettlementGapB5TuneFont(int px, char slackChar, int slackAdvance)
    {
        _px = px;
        _slackChar = slackChar;
        _slackAdvance = slackAdvance;
    }

    public int PixelSize => _px;

    public int Ascent => _px;

    public int Descent => 0;

    public int LineHeight => _px;

    public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
    {
        int advance = _slackChar != '\0' && codePoint == _slackChar ? _slackAdvance : 1;
        var alpha = new byte[_px];
        Array.Fill(alpha, (byte)255);
        glyph = new GlyphBitmap(1, _px, 0, _px, advance, alpha);
        return true;
    }
}

/// <summary>
/// 联网结算覆盖层 <see cref="OnlineSettlementOverlay.Build"/> 里三处"文字最大宽度"边界的补测(区域 B 第五轮):
/// 灰显神秘商店条与自动回城倒计时条的文字最大宽 = 面板宽 − 16 = 270 − 16 = 254,左侧战果行的文字最大宽 = 460。
/// 有字体时文字交给 <see cref="ShellPaint.Line"/>:量宽(不含描边)不超过最大宽就原样画(帧宽 = 量宽 + 2、帧高 = 字号 + 2);
/// 超过就先把字号逐档缩小(最多缩 4 档、且不低于 8),仍然放不下再截断补省略号(截到"文字 + 省略号"恰不超过最大宽 → 帧宽 = 最大宽 + 2、帧高 = 缩后字号 + 2)。
/// 用 <see cref="OnlineSettlementGapB5TuneFonts"/> 把同一段文字的量宽调成"最大宽 − 1 / 恰等于 / + 1 / 远超过",
/// 最大宽被改动 ±1 时"恰等于"与"+1"两行至少有一行的帧尺寸会变;远超过的行直接读出最大宽(截断后帧宽 = 最大宽 + 2)。
/// 没有字体时 <c>DrawLine</c> 走点阵 <c>art.Text</c>,根本不带最大宽(见最后一条用例,也是这些常量只在有字体时才有意义的证据)。
/// </summary>
public sealed class OnlineSettlementGapB5OverlayWidthTests
{
    // 神秘商店条的文字:13 个字符,首字符 "神" 只出现一次(用作松紧字符)
    private const string ShopText = "神秘商店 (本阶段未开放)";

    // 倒计时条在剩 30 秒时的文字:15 个字符,首字符 "自" 只出现一次
    private const string CountdownText = "自动返回城镇倒计时: 30 秒";

    private static List<SceneSprite> Build(OverlayArt art, OnlineSettlementSummary summary = null, bool showCountdown = false, long remainingMs = 30000)
    {
        var state = new OnlineSettlementRenderState
        {
            Summary = summary ?? OnlineSettlementGapCKit.BareSummary(),
            AgeMs = 0,
            IsChoiceEnabled = SettlementArt.IsEnabled,
            RemainingCountdownMs = remainingMs,
            ShowCountdown = showCountdown,
        };
        return OnlineSettlementOverlay.Build(art, state, 0);
    }

    private static SceneSprite One(List<SceneSprite> hud, string tag) => Assert.Single(hud, s => s.AnimationPath == tag);

    /// <summary>首字符当松紧字符:其余每个字符前进 1,首字符前进 total − (字数 − 1),整段文字的量宽恰为 total(并自检)。</summary>
    private static OnlineSettlementGapB5TuneFonts FontMeasuring(string text, int total)
    {
        var fonts = new OnlineSettlementGapB5TuneFonts(text[0], total - (text.Length - 1));
        Assert.Equal(total, GlyphText.Measure(fonts.ForSize(12), text));
        return fonts;
    }

    // 保护的行为:灰显神秘商店条的文字最大宽恰为 面板宽 − 16 = 254:
    // 量宽 253、254 都放得下(12 号字原样画,帧宽 = 量宽 + 2、帧高 14),量宽 255 放不下(缩到最小档 8 号再截断,帧宽 254 + 2 = 256、帧高 10),
    // 量宽远超过(262,松紧字符前进 250,其后的 12 个字符各 1)时同样截到 254(保留松紧字符与其后 3 个字符加省略号 = 250 + 3 + 1,帧宽 256)——
    // 所以最大宽不论被放宽还是收紧一像素,都有一行的帧宽或帧高会变。
    // 文字在条内垂直居中:y = 218 + (24 − 帧高) / 2,14 → 223,10 → 225。
    [Theory]
    [InlineData(253, 255, 14, 223)]
    [InlineData(254, 256, 14, 223)]
    [InlineData(255, 256, 10, 225)]
    [InlineData(262, 256, 10, 225)]
    public void SecretShopTextIsShrunkAndCutOnlyWhenWiderThanPanelWidthMinusSixteen(int measured, int frameWidth, int frameHeight, int screenY)
    {
        OnlineSettlementGapB5TuneFonts fonts = FontMeasuring(ShopText, measured);
        List<SceneSprite> hud = Build(new OverlayArt { Fonts = fonts });

        SceneSprite text = One(hud, "settle-secretshop-disabled");
        Assert.Equal((frameWidth, frameHeight), (text.Frame.Width, text.Frame.Height));
        Assert.Equal((522, screenY), (text.ScreenX, text.ScreenY));
        // 放不下时字号逐档缩到 8(12 → 11 → 10 → 9 → 8),放得下时只问过 12 号
        Assert.Equal(measured > 254, fonts.Requested.Contains(8));
    }

    // 保护的行为:自动回城倒计时条的文字最大宽同样是 面板宽 − 16 = 254(与神秘商店条各自独立传参):
    // "自动返回城镇倒计时: 30 秒"(剩 30000 毫秒 → 30 秒)的量宽调成 253 / 254 / 255 / 262(松紧字符前进 248,其后 14 个字符各 1;
    // 262 时留松紧字符与其后 5 个字符加省略号 = 248 + 5 + 1 = 254),帧尺寸与上面同理:(255, 14)、(256, 14)、(256, 10)、(256, 10);
    // y = 246 + (24 − 帧高) / 2 → 251、251、253、253。
    [Theory]
    [InlineData(253, 255, 14, 251)]
    [InlineData(254, 256, 14, 251)]
    [InlineData(255, 256, 10, 253)]
    [InlineData(262, 256, 10, 253)]
    public void CountdownTextIsShrunkAndCutOnlyWhenWiderThanPanelWidthMinusSixteen(int measured, int frameWidth, int frameHeight, int screenY)
    {
        OnlineSettlementGapB5TuneFonts fonts = FontMeasuring(CountdownText, measured);
        List<SceneSprite> hud = Build(new OverlayArt { Fonts = fonts }, showCountdown: true, remainingMs: 30000);

        SceneSprite text = One(hud, "settle-countdown");
        Assert.Equal((frameWidth, frameHeight), (text.Frame.Width, text.Frame.Height));
        Assert.Equal((522, screenY), (text.ScreenX, text.ScreenY));

        // 同一画面里的神秘商店文字(此字体下量宽 13 个字符 = 13)不受影响:12 号、帧宽 15、帧高 14
        SceneSprite shop = One(hud, "settle-secretshop-disabled");
        Assert.Equal((15, 14), (shop.Frame.Width, shop.Frame.Height));
    }

    // 保护的行为:左侧战果行(14 号字)的文字最大宽恰为 460:
    // 奖励物品行 = "奖励物品: " 6 个字符 + 物品名,每个字符前进 1,物品名 453 / 454 / 455 / 514 个字符 → 量宽 459 / 460 / 461 / 520;
    // 459、460 放得下(帧宽 = 量宽 + 2 = 461、462,帧高 14 + 2 = 16),461 放不下(缩到 10 号再截断:459 字 + 省略号 = 460,帧宽 462、帧高 12),
    // 520 同样截到 460(帧宽 462、帧高 12)。战果行共三条(通关用时占位、奖励物品、固定的未同步提示),奖励物品是第二条。
    [Theory]
    [InlineData(453, 461, 16)]
    [InlineData(454, 462, 16)]
    [InlineData(455, 462, 12)]
    [InlineData(514, 462, 12)]
    public void ResultLineIsShrunkAndCutOnlyWhenWiderThan460(int nameLength, int frameWidth, int frameHeight)
    {
        var spec = new OnlineSettlementGapCKit.SummarySpec
        {
            RewardParse = OnlineSettlementParseState.Succeeded,
            Drops = new[] { new OnlineSettlementDropItem(9801, 1, new string('甲', nameLength), isQuestItem: false) },
        };
        var fonts = new OnlineSettlementGapB5TuneFonts();
        List<SceneSprite> hud = Build(new OverlayArt { Fonts = fonts }, spec.Build());

        List<SceneSprite> lines = OnlineSettlementGapCKit.Tagged(hud, "online-settle-reward");
        Assert.Equal(3, lines.Count);
        SceneSprite drop = lines[1];
        Assert.Equal((frameWidth, frameHeight), (drop.Frame.Width, drop.Frame.Height));
        Assert.Equal(24, drop.ScreenX);
        // 第一条(通关用时占位,10 个字符)与最后一条(未同步提示,16 个字符)远小于 460:14 号原样画,帧高 16
        Assert.Equal(16, lines[0].Frame.Height);
        Assert.Equal(16, lines[2].Frame.Height);
        Assert.Equal(12, lines[0].Frame.Width);
        Assert.Equal(18, lines[2].Frame.Width);
        // 行距:每行的 y = 上一行的 y + 上一行的帧高 + 5
        Assert.Equal(24, lines[0].ScreenY);
        Assert.Equal(lines[0].ScreenY + lines[0].Frame.Height + 5, drop.ScreenY);
        Assert.Equal(drop.ScreenY + drop.Frame.Height + 5, lines[2].ScreenY);
    }

    // 保护的行为:没有字体时 DrawLine 不带最大宽(走点阵 art.Text),三处文字再长也不被缩小或截断,字号档位只由请求的字号决定
    // (战果行 14 号 → 点阵 2 倍,帧高 7 × 2 + 2 = 16;神秘商店与倒计时 12 号 → 1 倍,帧高 9)。
    // 一条 100 字符的奖励物品名:点阵宽 = (106 × 6 − 1) × 2 + 2 = 1272,远超 460 也原样画。
    [Fact]
    public void WithoutFontsNoMaximumWidthIsAppliedToAnyLine()
    {
        var spec = new OnlineSettlementGapCKit.SummarySpec
        {
            RewardParse = OnlineSettlementParseState.Succeeded,
            Drops = new[] { new OnlineSettlementDropItem(9802, 1, new string('A', 100), isQuestItem: false) },
        };
        var art = new OverlayArt();
        Assert.False(art.HasFonts);
        List<SceneSprite> hud = Build(art, spec.Build(), showCountdown: true, remainingMs: 30000);

        // "DROP: " + 100 个 A = 106 个字符
        List<SceneSprite> lines = OnlineSettlementGapCKit.Tagged(hud, "online-settle-reward");
        SceneSprite drop = lines[1];
        Assert.Equal((1272, 16), (drop.Frame.Width, drop.Frame.Height));
        Assert.Same(art.Text("DROP: " + new string('A', 100), 2, OverlayArt.White), drop.Frame);

        Assert.Equal(9, One(hud, "settle-secretshop-disabled").Frame.Height);
        Assert.Equal(9, One(hud, "settle-countdown").Frame.Height);
    }
}
