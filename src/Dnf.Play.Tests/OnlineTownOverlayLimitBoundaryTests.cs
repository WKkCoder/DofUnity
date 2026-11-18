using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网城镇叠层"宽度上限差 1 像素"的边界测试:补 <c>OnlineTownOverlaySpriteTests</c> 里被"按字截断的量化"吞掉的那一像素。
/// 角标条最宽 520、提示条最宽 560;条左边的色条与图标加右边的留白共 56,所以文字的最大排版宽是 464(角标)与 504(提示);
/// 文字帧宽 = 排版宽 + 2(描边各 1 像素),条宽 = min(上限, 56 + 文字帧宽)。
/// 合成字体 (narrow, wide) 是按整数方程挑的:让某一条真实文案(全角字 a 个、其余字符 b 个)的未截断排版宽 a×wide + b×narrow
/// 恰好等于 464 / 504("刚好放得下",不截断、条宽被 min 压到上限),或恰好等于 465 / 505("超一像素",必须截断):
/// 文字放得下时条宽是上限本身,所以上限偏 +1 条宽就变;超一像素时偏 +1 就变成放得下;偏 −1 则让"刚好放得下"的那条被截断。
/// 精灵全部显式带 <see cref="OverlayArt.Fonts"/>,不读全进程共用的静态字体集,与别的测试并行互不影响。期望值全部手算。
/// </summary>
public sealed class OnlineTownOverlayLimitBoundaryTests
{
    /// <summary>每个字的前进宽度固定:全角字 <c>wide</c>、其余 <c>narrow</c>(不随字号变);字形高 10,行高 16,基线 12。</summary>
    private sealed class FlatFonts : IGlyphFonts
    {
        private readonly int _narrow;
        private readonly int _wide;

        public FlatFonts(int narrow, int wide)
        {
            _narrow = narrow;
            _wide = wide;
        }

        public IGlyphSource ForSize(int pixelSize) => new Source(pixelSize, _narrow, _wide);

        private sealed class Source : IGlyphSource
        {
            private readonly int _narrow;
            private readonly int _wide;

            public Source(int px, int narrow, int wide)
            {
                PixelSize = px;
                _narrow = narrow;
                _wide = wide;
            }

            public int PixelSize { get; }

            public int Ascent => 12;

            public int Descent => 4;

            public int LineHeight => 16;

            public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
            {
                int advance = GlyphText.IsWide(codePoint) ? _wide : _narrow;
                if (codePoint == ' ')
                {
                    glyph = GlyphBitmap.Blank(advance);
                    return true;
                }
                int w = Math.Max(1, advance - 1);
                var alpha = new byte[w * 10];
                Array.Fill(alpha, (byte)255);
                glyph = new GlyphBitmap(w, 10, 0, 12, advance, alpha);
                return true;
            }
        }
    }

    private static OnlineHeroProfile Profile(int job) =>
        new OnlineHeroProfile(0, 9201, 9202, job, 0, 0, 0, 20, "测试角色1", "测试职业", 0, null, default, null);

    private static OverlayArt Art(int narrow, int wide) => new OverlayArt { Fonts = new FlatFonts(narrow, wide) };

    /// <summary>逐个对:标签、左上角 x/y、帧宽、帧高、排序键。</summary>
    private static void AssertSprites(List<SceneSprite> actual, params (string Tag, int X, int Y, int W, int H, int Order)[] expected)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            SceneSprite s = actual[i];
            Assert.Equal(expected[i].Tag, s.AnimationPath);
            Assert.Equal((expected[i].X, expected[i].Y), (s.ScreenX, s.ScreenY));
            Assert.Equal((expected[i].W, expected[i].H), (s.Frame.Width, s.Frame.Height));
            Assert.Equal(expected[i].Order, s.SortKey);
        }
    }

    // ───────────────────────── 角标(上限 520,文字最宽 464) ─────────────────────────

    // 角标"联机城镇 · 道具/装备/金币暂未同步":14 个全角字 + 5 个其余字符(空格、间隔点、空格、两个斜杠)。
    // 字体 (narrow 6, wide 31):14×31 + 5×6 = 434 + 30 = 464 = 520 − 56 —— 刚好放得下,不截断;文字帧 466 × 18;条宽 min(520, 56 + 466 = 522) = 520。
    // 上限 +1(521):还是放得下,但条宽 = min(521, 522) = 521;上限 −1(519):最宽 463 < 464,会被截断,条宽也不是 520。
    [Fact]
    public void A_badge_whose_text_is_exactly_the_limit_wide_is_not_cut_and_the_bar_is_exactly_five_hundred_and_twenty()
    {
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(6, 31), Profile(0), null, 1000);

        AssertSprites(sprites,
            ("onlinetown-status-bar", 12, 10, 520, 32, 0),
            ("onlinetown-status-accent", 13, 11, 4, 30, 1),
            ("onlinetown-status-icon", 28, 16, 20, 20, 2),
            ("onlinetown-status", 56, 17, 466, 18, 3));
    }

    // 同一条角标文案,字体 (narrow 9, wide 30):14×30 + 5×9 = 420 + 45 = 465 = 464 + 1 —— 超一像素,必须截断。
    // 省略号(全角)宽 30;去字直到 剩下的字 + 30 ≤ 464:去掉"步"剩 435 → 435 + 30 = 465 > 464;再去掉"同"剩 405 → 405 + 30 = 435 ≤ 464。
    // 所以留 17 个字("联机城镇 · 道具/装备/金币暂未")+ 省略号,排版宽 435;文字帧 437;条宽 56 + 437 = 493(没碰到 520)。
    // 上限 +1(521):最宽 465,465 放得下 → 不截断,文字帧 467、条宽 521。
    [Fact]
    public void A_badge_whose_text_is_one_pixel_over_the_limit_is_cut_with_an_ellipsis()
    {
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(9, 30), Profile(0), null, 1000);

        AssertSprites(sprites,
            ("onlinetown-status-bar", 12, 10, 493, 32, 0),
            ("onlinetown-status-accent", 13, 11, 4, 30, 1),
            ("onlinetown-status-icon", 28, 16, 20, 20, 2),
            ("onlinetown-status", 56, 17, 437, 18, 3));
    }

    // 退回角标"当前仅支持鬼剑士外观与技能,暂按鬼剑士显示 · 道具/装备/金币未同步":30 个全角字 + 5 个其余字符(整数方程 30w + 5n = 465 的一组解 w = 14、n = 9)。
    // 字体 (narrow 9, wide 14):30×14 + 5×9 = 420 + 45 = 465 = 464 + 1 —— 超一像素,必须截断。
    // 省略号宽 14;去字直到 剩下的字 + 14 ≤ 464:去掉"步"剩 451 → 465 > 464;再去掉"同"剩 437 → 437 + 14 = 451 ≤ 464。
    // 所以留 33 个字 + 省略号,排版宽 451;文字帧 453;条宽 56 + 453 = 509。上限 +1(521):465 放得下 → 文字帧 467、条宽 521。
    [Fact]
    public void The_fallback_badge_one_pixel_over_the_limit_is_cut_two_characters_short_of_the_full_text()
    {
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(9, 14), Profile(2), null, 1000);

        AssertSprites(sprites,
            ("onlinetown-status-bar", 12, 10, 509, 32, 0),
            ("onlinetown-status-accent", 13, 11, 4, 30, 1),
            ("onlinetown-status-icon", 28, 16, 20, 20, 2),
            ("onlinetown-status", 56, 17, 453, 18, 3));
    }

    // ───────────────────────── 提示条(上限 560,文字最宽 504) ─────────────────────────

    // 提示"该区域暂未开放,约 12 分钟后可进入"(剩余分钟数 12):15 个全角字 + 4 个其余字符(空格、1、2、空格)。
    // 字体 (narrow 6, wide 32):15×32 + 4×6 = 480 + 24 = 504 = 560 − 56 —— 刚好放得下,不截断;文字帧 506;条宽 min(560, 56 + 506 = 562) = 560;x = (800 − 560) / 2 = 120。
    // 上限 +1(561):条宽 561、x = (800 − 561) / 2 = 119(整数除);上限 −1(559):最宽 503 < 504,会被截断。
    // 同一个字体下角标(14 个全角字 + 5 个其余字符 = 478 > 464)也被截断,不在这条里断言(前 4 个精灵是角标)。
    [Fact]
    public void A_prompt_whose_text_is_exactly_the_limit_wide_is_not_cut_and_the_bar_is_exactly_five_hundred_and_sixty()
    {
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(6, 32), Profile(0),
            new OnlineTownPrompt(OnlineTownPromptKind.ForbiddenMinutes, minutes: 12), 240);

        Assert.Equal(8, sprites.Count);
        AssertSprites(sprites.GetRange(4, 4),
            ("onlinetown-prompt-bar", 120, 48, 560, 32, 4),
            ("onlinetown-prompt-accent", 121, 49, 4, 30, 5),
            ("onlinetown-prompt-icon", 136, 54, 20, 20, 6),
            ("onlinetown-prompt", 164, 55, 506, 18, 7));
    }

    // 同一条提示文案,字体 (narrow 10, wide 31):15×31 + 4×10 = 465 + 40 = 505 = 504 + 1 —— 超一像素,必须截断。
    // 省略号宽 31;去字直到 剩下的字 + 31 ≤ 504:去掉"入"剩 474 → 474 + 31 = 505 > 504;再去掉"进"剩 443 → 443 + 31 = 474 ≤ 504。
    // 所以留 17 个字("该区域暂未开放,约 12 分钟后可")+ 省略号,排版宽 474;文字帧 476;条宽 56 + 476 = 532;x = (800 − 532) / 2 = 134。
    // 上限 +1(561):最宽 505,505 放得下 → 不截断,文字帧 507、条宽 561、x = 119。
    [Fact]
    public void A_prompt_whose_text_is_one_pixel_over_the_limit_is_cut_and_stays_centered_by_its_own_width()
    {
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(10, 31), Profile(0),
            new OnlineTownPrompt(OnlineTownPromptKind.ForbiddenMinutes, minutes: 12), 240);

        Assert.Equal(8, sprites.Count);
        AssertSprites(sprites.GetRange(4, 4),
            ("onlinetown-prompt-bar", 134, 48, 532, 32, 4),
            ("onlinetown-prompt-accent", 135, 49, 4, 30, 5),
            ("onlinetown-prompt-icon", 150, 54, 20, 20, 6),
            ("onlinetown-prompt", 178, 55, 476, 18, 7));
    }

    // 等待提示"正在切换区域…":7 个全角字(含全角省略号),字体 wide 72:7×72 = 504 —— 刚好放得下,转圈条宽 560、x = 120(narrow 与它无关,取 36)。
    [Fact]
    public void The_waiting_prompt_with_seven_wide_characters_exactly_fills_the_prompt_limit()
    {
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(36, 72), Profile(0), new OnlineTownPrompt(OnlineTownPromptKind.Waiting), 240);

        Assert.Equal(8, sprites.Count);
        AssertSprites(sprites.GetRange(4, 4),
            ("onlinetown-prompt-bar", 120, 48, 560, 32, 4),
            ("onlinetown-prompt-accent", 121, 49, 4, 30, 5),
            ("onlinetown-prompt-spinner", 136, 54, 20, 20, 6),
            ("onlinetown-prompt", 164, 55, 506, 18, 7));
    }
}
