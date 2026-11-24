using Dnf.Render;
using Kit = Dnf.Play.Tests.OnlineSettlementGapB3Kit;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网结算画面"自动返回城镇"倒计时条文案的取整与夹取(<see cref="OnlineSettlementOverlay.Build"/> 里
/// <c>remainMs = RemainingCountdownMs &lt; 0 ? 0 : RemainingCountdownMs; remainSec = (remainMs + 999) / 1000;</c> 与 <c>remainSec &lt;= 10</c> 的字色阈值)。
/// 有字体版本用"把码点编码进像素"的合成字体(<see cref="OnlineSettlementGapB3CodecFonts"/>),由 <see cref="OnlineSettlementGapB3Kit.Decode"/> 把文字精灵读回字符串
/// (连数字写错、符号写错都读得出);无字体版本用同一块画布点阵字库的帧缓存(同文字、同倍数、同颜色拿到同一个对象)。
/// 剩余毫秒为负时文案是 "0 秒" / "0S"(不是负数、不抛异常):注意 C# 整数除法向零取整,(−1 + 999) / 1000 与 (−1000 + 999) / 1000 都是 0,
/// 所以不夹取时也看不出差异的负数范围是 [−1998, −1];只有 ≤ −1999 时不夹取才会算出 −1 之类的负秒数(−1999 + 999 = −1000,除以 1000 得 −1)。
/// 因此用例里特意放了 −1999、−2000 与 long.MinValue,保证"夹取"本身(而非除法的巧合)被钉住。
/// </summary>
public sealed class OnlineSettlementGapB6CountdownTests
{
    private const string ZhPrefix = "自动返回城镇倒计时: ";
    private const string ZhSuffix = " 秒";
    private const string EnPrefix = "AUTO RETURN TO TOWN: ";
    private const string EnSuffix = "S";

    /// <summary>剩 10 秒及以内的橙色。</summary>
    private const uint Urgent = 0xFFB060u;

    /// <summary>剩 11 秒及以上的浅黄。</summary>
    private const uint Normal = 0xFFE9A0u;

    private static List<SceneSprite> Build(OverlayArt art, long remainingMs)
    {
        var state = new OnlineSettlementRenderState
        {
            Summary = OnlineSettlementGapCKit.BareSummary(),
            AgeMs = 0,
            IsChoiceEnabled = SettlementArt.IsEnabled,
            RemainingCountdownMs = remainingMs,
            ShowCountdown = true,
        };
        return OnlineSettlementOverlay.Build(art, state, 0);
    }

    private static SceneSprite One(List<SceneSprite> hud, string tag) => Assert.Single(hud, s => s.AnimationPath == tag);

    // 保护的行为(有字体):倒计时文案 "自动返回城镇倒计时: N 秒" 里的 N = 剩余毫秒向上取整到秒——
    // 负数(−1、−1000、很大的负数)一律是 0 而不是负数,0 毫秒是 0,1 毫秒就是 1,整秒不多算(1000 → 1),1001 → 2;
    // 剩 10 秒及以内(含 0)字色是橙色 0xFFB060,11 秒起是浅黄 0xFFE9A0(10000 与 10001 毫秒分居两侧);
    // 文字摆在倒计时条内 (522, 251)(条在 (512, 246),字高 14 → 246 + (24 − 14) / 2 = 251),条本身照常画出(不因负数而缺失)。
    // 手算:先把负数夹到 0,再 (ms + 999) / 1000(整数除法):
    //   ≤ −1 → 0 → 999 / 1000 = 0;0 → 0;1 → 1000 / 1000 = 1;999 → 1998 / 1000 = 1;1000 → 1999 / 1000 = 1;1001 → 2000 / 1000 = 2;
    //   1999 → 2998 / 1000 = 2;2000 → 2999 / 1000 = 2;9001 → 10000 / 1000 = 10;10000 → 10999 / 1000 = 10;10001 → 11000 / 1000 = 11;
    //   11000 → 11999 / 1000 = 11;11001 → 12000 / 1000 = 12;59999 → 60998 / 1000 = 60;60000 → 60999 / 1000 = 60。
    [Theory]
    [InlineData(long.MinValue, "0", true)]
    [InlineData(-86400000L, "0", true)]
    [InlineData(-60000L, "0", true)]
    [InlineData(-2001L, "0", true)]
    [InlineData(-2000L, "0", true)]
    [InlineData(-1999L, "0", true)]
    [InlineData(-1001L, "0", true)]
    [InlineData(-1000L, "0", true)]
    [InlineData(-999L, "0", true)]
    [InlineData(-1L, "0", true)]
    [InlineData(0L, "0", true)]
    [InlineData(1L, "1", true)]
    [InlineData(999L, "1", true)]
    [InlineData(1000L, "1", true)]
    [InlineData(1001L, "2", true)]
    [InlineData(1999L, "2", true)]
    [InlineData(2000L, "2", true)]
    [InlineData(9001L, "10", true)]
    [InlineData(10000L, "10", true)]
    [InlineData(10001L, "11", false)]
    [InlineData(11000L, "11", false)]
    [InlineData(11001L, "12", false)]
    [InlineData(59999L, "60", false)]
    [InlineData(60000L, "60", false)]
    public void CountdownTextWithFontsRoundsUpToWholeSecondsAndClampsNegativesToZero(long remainingMs, string seconds, bool urgent)
    {
        OverlayArt art = Kit.FontArt(out _);
        uint color = urgent ? Urgent : Normal;
        uint otherColor = urgent ? Normal : Urgent;

        List<SceneSprite> hud = Build(art, remainingMs);

        SceneSprite text = One(hud, "settle-countdown");
        Assert.Equal(ZhPrefix + seconds + ZhSuffix, Kit.Decode(text.Frame, color));
        Assert.Equal(new HashSet<uint> { color, OverlayArt.Black }, Kit.OpaqueColors(text.Frame));
        Assert.DoesNotContain(otherColor, Kit.OpaqueColors(text.Frame));
        Assert.Equal(14, text.Frame.Height);
        Assert.Equal((522, 251), (text.ScreenX, text.ScreenY));

        SceneSprite bar = One(hud, "settle-countdown-bg");
        Assert.Equal((512, 246), (bar.ScreenX, bar.ScreenY));
        Assert.Equal((270, 24), (bar.Frame.Width, bar.Frame.Height));
        Assert.Equal(200, bar.TintAlpha);
        Assert.Equal(bar.SortKey + 1, text.SortKey);
    }

    // 保护的行为(无字体):同一套取整与夹取,文案 "AUTO RETURN TO TOWN: NS"(点阵字库 1 倍),
    // 文字帧就是同画布为这段文字与对应字色画的那一帧(同一个对象);10 秒及以内橙色、11 秒起浅黄;
    // 文字摆在 (522, 253)(字高 9 → 246 + (24 − 9) / 2 = 253)。期望秒数与上面的有字体用例逐行相同。
    [Theory]
    [InlineData(long.MinValue, "0", true)]
    [InlineData(-86400000L, "0", true)]
    [InlineData(-60000L, "0", true)]
    [InlineData(-2001L, "0", true)]
    [InlineData(-2000L, "0", true)]
    [InlineData(-1999L, "0", true)]
    [InlineData(-1001L, "0", true)]
    [InlineData(-1000L, "0", true)]
    [InlineData(-999L, "0", true)]
    [InlineData(-1L, "0", true)]
    [InlineData(0L, "0", true)]
    [InlineData(1L, "1", true)]
    [InlineData(999L, "1", true)]
    [InlineData(1000L, "1", true)]
    [InlineData(1001L, "2", true)]
    [InlineData(1999L, "2", true)]
    [InlineData(2000L, "2", true)]
    [InlineData(9001L, "10", true)]
    [InlineData(10000L, "10", true)]
    [InlineData(10001L, "11", false)]
    [InlineData(11000L, "11", false)]
    [InlineData(11001L, "12", false)]
    [InlineData(59999L, "60", false)]
    [InlineData(60000L, "60", false)]
    public void CountdownTextWithoutFontsRoundsUpToWholeSecondsAndClampsNegativesToZero(long remainingMs, string seconds, bool urgent)
    {
        var art = new OverlayArt();
        Assert.False(art.HasFonts);
        uint color = urgent ? Urgent : Normal;

        List<SceneSprite> hud = Build(art, remainingMs);

        SceneSprite text = One(hud, "settle-countdown");
        Assert.Same(art.Text(EnPrefix + seconds + EnSuffix, 1, color), text.Frame);
        Assert.Equal(9, text.Frame.Height);
        Assert.Equal((522, 253), (text.ScreenX, text.ScreenY));

        SceneSprite bar = One(hud, "settle-countdown-bg");
        Assert.Equal((512, 246), (bar.ScreenX, bar.ScreenY));
        Assert.Equal(200, bar.TintAlpha);
        Assert.Equal(bar.SortKey + 1, text.SortKey);
    }

    // 保护的行为:字色阈值恰在 10 与 11 秒之间——同一块画布上,剩 10000 毫秒(10 秒)与 9001 毫秒(10 秒)都是橙色,
    // 10001 毫秒(11 秒)与 11000 毫秒(11 秒)都是浅黄;两种颜色的文字精灵互不相同。有字体与无字体两条路径各核对一遍。
    [Fact]
    public void CountdownColorSwitchesBetweenTenAndElevenRemainingSeconds()
    {
        Assert.NotEqual(Urgent, Normal);

        // 有字体:从读回的填色看字色(精灵里只有"填色 + 黑描边"两种不透明颜色)
        foreach ((long remainingMs, uint expected) in new[] { (9001L, Urgent), (10000L, Urgent), (10001L, Normal), (11000L, Normal) })
        {
            OverlayArt art = Kit.FontArt(out _);
            SceneSprite text = One(Build(art, remainingMs), "settle-countdown");
            Assert.Equal(new HashSet<uint> { expected, OverlayArt.Black }, Kit.OpaqueColors(text.Frame));
        }

        // 无字体:同画布同文字,10 秒用橙色那一帧、11 秒用浅黄那一帧
        var plain = new OverlayArt();
        SpriteFrame tenSeconds = One(Build(plain, 10000), "settle-countdown").Frame;
        SpriteFrame elevenSeconds = One(Build(plain, 10001), "settle-countdown").Frame;
        Assert.Same(plain.Text("AUTO RETURN TO TOWN: 10S", 1, Urgent), tenSeconds);
        Assert.Same(plain.Text("AUTO RETURN TO TOWN: 11S", 1, Normal), elevenSeconds);
        Assert.NotSame(tenSeconds, elevenSeconds);
    }
}
