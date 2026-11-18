using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网城镇叠层的精灵逐个对位置、尺寸与顺序的测试:字体是字宽固定(与字号无关)的合成字体,所以每张文字帧、每条提示条的宽度都能手算。
/// 文字帧宽 = 排版宽 + 2(描边各 1 像素),高 = 行高 16 + 2 = 18;提示条宽 = 56(带图标时的边框)+ 文字帧宽,最多 520(角标)/560(提示)。
/// 精灵全部显式带 <see cref="OverlayArt.Fonts"/>,不读全进程共用的静态字体集,所以与别的测试并行也不受影响。
/// </summary>
public sealed class OnlineTownOverlaySpriteTests
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

    private static OverlayArt Art(int narrow = 7, int wide = 14) => new OverlayArt { Fonts = new FlatFonts(narrow, wide) };

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

    // 角标"联机城镇 · 道具/装备/金币暂未同步":14 个全角字 + 5 个其余字符 = 14×14 + 5×7 = 231;文字帧 233 × 18;条宽 56 + 233 = 289
    [Fact]
    public void The_status_badge_sits_at_twelve_ten_and_every_sprite_has_its_exact_size_and_order()
    {
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(), Profile(0), null, 1000);

        AssertSprites(sprites,
            ("onlinetown-status-bar", 12, 10, 289, 32, 0),
            ("onlinetown-status-accent", 13, 11, 4, 30, 1),
            ("onlinetown-status-icon", 28, 16, 20, 20, 2),
            ("onlinetown-status", 56, 17, 233, 18, 3));
    }

    // 退回角标:30 个全角字 + 5 个其余字符 = 30×14 + 5×7 = 455;文字帧 457;条宽 513(没碰到 520)
    [Fact]
    public void The_fallback_badge_is_wider_and_keeps_the_same_positions()
    {
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(), Profile(2), null, 1000);

        AssertSprites(sprites,
            ("onlinetown-status-bar", 12, 10, 513, 32, 0),
            ("onlinetown-status-accent", 13, 11, 4, 30, 1),
            ("onlinetown-status-icon", 28, 16, 20, 20, 2),
            ("onlinetown-status", 56, 17, 457, 18, 3));
    }

    [Fact]
    public void A_prompt_of_kind_none_draws_only_the_badge()
    {
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(), Profile(0), new OnlineTownPrompt(OnlineTownPromptKind.None), 1000);

        Assert.Equal(4, sprites.Count);
        Assert.DoesNotContain(sprites, s => s.AnimationPath.StartsWith("onlinetown-prompt", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_prompt_only_the_badge_is_drawn()
    {
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(), Profile(0), null, 1000);

        Assert.Equal(4, sprites.Count);
        Assert.All(sprites, s => Assert.StartsWith("onlinetown-status", s.AnimationPath));
    }

    // 提示条:带图标,条宽 = 56 + 文字帧宽,横向居中 x = (800 - 条宽) / 2(整数除),纵向 y = 48;排序键接着角标的 3 往下数
    [Fact]
    public void The_waiting_prompt_is_a_spinner_bar_centered_under_the_badge()
    {
        // "正在切换区域…":6 个全角字 + 省略号(全角)= 7 × 14 = 98;文字帧 100;条宽 156;x = (800 - 156) / 2 = 322
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(), Profile(0), new OnlineTownPrompt(OnlineTownPromptKind.Waiting), 240);

        AssertSprites(sprites,
            ("onlinetown-status-bar", 12, 10, 289, 32, 0),
            ("onlinetown-status-accent", 13, 11, 4, 30, 1),
            ("onlinetown-status-icon", 28, 16, 20, 20, 2),
            ("onlinetown-status", 56, 17, 233, 18, 3),
            ("onlinetown-prompt-bar", 322, 48, 156, 32, 4),
            ("onlinetown-prompt-accent", 323, 49, 4, 30, 5),
            ("onlinetown-prompt-spinner", 338, 54, 20, 20, 6),
            ("onlinetown-prompt", 366, 55, 100, 18, 7));
    }

    [Fact]
    public void The_rejected_prompt_is_centered_by_its_own_width()
    {
        // "无法切换至目标区域，已留在当前区域":17 个全角字 = 238;文字帧 240;条宽 296;x = (800 - 296) / 2 = 252
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(), Profile(0), new OnlineTownPrompt(OnlineTownPromptKind.Rejected), 240);

        AssertSprites(sprites.GetRange(4, 4),
            ("onlinetown-prompt-bar", 252, 48, 296, 32, 4),
            ("onlinetown-prompt-accent", 253, 49, 4, 30, 5),
            ("onlinetown-prompt-icon", 268, 54, 20, 20, 6),
            ("onlinetown-prompt", 296, 55, 240, 18, 7));
    }

    [Fact]
    public void An_odd_prompt_width_is_halved_with_integer_division()
    {
        // "该区域暂未开放，约 5 分钟后可进入":15 个全角字 + 3 个其余字符 = 15×14 + 3×7 = 231;文字帧 233;条宽 289;x = (800 - 289) / 2 = 255
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(), Profile(0),
            new OnlineTownPrompt(OnlineTownPromptKind.ForbiddenMinutes, minutes: 5), 240);

        AssertSprites(sprites.GetRange(4, 4),
            ("onlinetown-prompt-bar", 255, 48, 289, 32, 4),
            ("onlinetown-prompt-accent", 256, 49, 4, 30, 5),
            ("onlinetown-prompt-icon", 271, 54, 20, 20, 6),
            ("onlinetown-prompt", 299, 55, 233, 18, 7));
    }

    [Fact]
    public void The_forbidden_prompt_without_minutes_and_the_unmapped_prompt_have_their_own_widths()
    {
        // "当前暂时无法进入该区域":11 个全角字 = 154;文字帧 156;条宽 212;x = 294
        List<SceneSprite> forbidden = OnlineTownOverlay.Build(Art(), Profile(0),
            new OnlineTownPrompt(OnlineTownPromptKind.ForbiddenMinutes, minutes: 0), 240);
        AssertSprites(forbidden.GetRange(4, 4),
            ("onlinetown-prompt-bar", 294, 48, 212, 32, 4),
            ("onlinetown-prompt-accent", 295, 49, 4, 30, 5),
            ("onlinetown-prompt-icon", 310, 54, 20, 20, 6),
            ("onlinetown-prompt", 338, 55, 156, 18, 7));

        // "目标区域暂未映射，已留在当前区域":16 个全角字 = 224;文字帧 226;条宽 282;x = 259
        List<SceneSprite> unmapped = OnlineTownOverlay.Build(Art(), Profile(0), new OnlineTownPrompt(OnlineTownPromptKind.UnmappedArea), 240);
        AssertSprites(unmapped.GetRange(4, 4),
            ("onlinetown-prompt-bar", 259, 48, 282, 32, 4),
            ("onlinetown-prompt-accent", 260, 49, 4, 30, 5),
            ("onlinetown-prompt-icon", 275, 54, 20, 20, 6),
            ("onlinetown-prompt", 303, 55, 226, 18, 7));
    }

    // ───────────────────────── 色调 ─────────────────────────

    /// <summary>提示条底框里一个确定在框内(左侧、图标之前)的像素(x = 8,y = 16)的 RGBA 四个字节。</summary>
    private static byte[] PromptBarPixel(OnlineTownPromptKind kind)
    {
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(), Profile(0), new OnlineTownPrompt(kind, minutes: 5), 240);
        return Pixel(sprites[4]);
    }

    private static byte[] Pixel(SceneSprite bar)
    {
        int at = (16 * bar.Frame.Width + 8) * 4;
        return new[] { bar.Frame.Rgba[at], bar.Frame.Rgba[at + 1], bar.Frame.Rgba[at + 2], bar.Frame.Rgba[at + 3] };
    }

    // 色调只比较"谁和谁一样、谁和谁不一样"(不碰调色板里的具体色值):等待是一种(蓝)、被拒与剩余分钟是同一种(红)、未映射与角标同一种(信息)
    [Fact]
    public void The_prompt_tone_follows_the_kind_waiting_and_error_and_info_look_different()
    {
        byte[] badge = Pixel(OnlineTownOverlay.Build(Art(), Profile(0), null, 240)[0]);
        byte[] waiting = PromptBarPixel(OnlineTownPromptKind.Waiting);
        byte[] forbidden = PromptBarPixel(OnlineTownPromptKind.ForbiddenMinutes);
        byte[] rejected = PromptBarPixel(OnlineTownPromptKind.Rejected);
        byte[] unmapped = PromptBarPixel(OnlineTownPromptKind.UnmappedArea);

        Assert.Equal(forbidden, rejected);
        Assert.Equal(badge, unmapped);
        Assert.NotEqual(rejected, badge);
        Assert.NotEqual(waiting, badge);
        Assert.NotEqual(waiting, rejected);
    }

    // ───────────────────────── 宽度上限 ─────────────────────────

    [Fact]
    public void A_badge_wider_than_the_limit_is_cut_to_exactly_five_hundred_and_twenty()
    {
        // 每个字都 14 宽(与字号无关):退回角标 35 个字 = 490 > 464(= 520 - 56)→ 截成 33 个字(含省略号)= 462;文字帧 464;条宽 min(520, 56 + 464) = 520
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(14, 14), Profile(2), null, 1000);

        AssertSprites(sprites,
            ("onlinetown-status-bar", 12, 10, 520, 32, 0),
            ("onlinetown-status-accent", 13, 11, 4, 30, 1),
            ("onlinetown-status-icon", 28, 16, 20, 20, 2),
            ("onlinetown-status", 56, 17, 464, 18, 3));
    }

    [Fact]
    public void A_prompt_wider_than_the_limit_is_cut_to_exactly_five_hundred_and_sixty_and_stays_centered()
    {
        // 每个字都 36 宽:提示 17 个字 = 612 > 504(= 560 - 56)→ 截成 14 个字(含省略号)= 504;文字帧 506;条宽 min(560, 56 + 506) = 560;x = (800 - 560) / 2 = 120
        // 同一个字体下角标 35 个字 = 1260 > 464 → 截成 12 个字 = 432;文字帧 434;条宽 490
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(36, 36), Profile(2), new OnlineTownPrompt(OnlineTownPromptKind.Rejected), 240);

        AssertSprites(sprites,
            ("onlinetown-status-bar", 12, 10, 490, 32, 0),
            ("onlinetown-status-accent", 13, 11, 4, 30, 1),
            ("onlinetown-status-icon", 28, 16, 20, 20, 2),
            ("onlinetown-status", 56, 17, 434, 18, 3),
            ("onlinetown-prompt-bar", 120, 48, 560, 32, 4),
            ("onlinetown-prompt-accent", 121, 49, 4, 30, 5),
            ("onlinetown-prompt-icon", 136, 54, 20, 20, 6),
            ("onlinetown-prompt", 164, 55, 506, 18, 7));
    }

    // ───────────────────────── 转圈相位 ─────────────────────────

    /// <summary>某个时刻下等待提示里转圈精灵(标签 <c>onlinetown-prompt-spinner</c>,有且只有一个)的整张帧。</summary>
    private static SpriteFrame SpinnerFrameAt(long timeMs)
    {
        List<SceneSprite> sprites = OnlineTownOverlay.Build(Art(), Profile(0), new OnlineTownPrompt(OnlineTownPromptKind.Waiting), timeMs);
        Assert.NotNull(sprites);
        SceneSprite found = null;
        int count = 0;
        foreach (SceneSprite sprite in sprites)
        {
            if (sprite.AnimationPath == "onlinetown-prompt-spinner")
            {
                found = sprite;
                count++;
            }
        }
        Assert.Equal(1, count);
        return found.Frame;
    }

    // 相位 = (把负时间当 0 后 ÷ 80 取整)再对 10 取余,下面全是手算:
    //   0→0、79→0(还没满 80)、80→1、159→1、160→2、245→3(245 ÷ 80 = 3 余 5)、720→9、799→9、
    //   800→0(整一圈 10 格)、880→1(11 → 1)、1000→2(12 → 2);
    //   负数一律当 0:-5→0、-80→0、-170→0(不能当成 -1、-2 再去模成 9、8)
    // 期望帧用独立构造的 ShellPaint 按"相位字面量"现画(转圈是 20 × 20、等待语气的颜色),逐字节比。
    [Theory]
    [InlineData(0L, 0)]
    [InlineData(79L, 0)]
    [InlineData(80L, 1)]
    [InlineData(159L, 1)]
    [InlineData(160L, 2)]
    [InlineData(245L, 3)]
    [InlineData(720L, 9)]
    [InlineData(799L, 9)]
    [InlineData(800L, 0)]
    [InlineData(880L, 1)]
    [InlineData(1000L, 2)]
    [InlineData(-5L, 0)]
    [InlineData(-80L, 0)]
    [InlineData(-170L, 0)]
    public void The_waiting_spinner_frame_is_exactly_the_hand_computed_phase_of_the_time(long timeMs, int phase)
    {
        SpriteFrame expected = new ShellPaint(Art()).Spinner(20, phase, ShellPaint.ToneColor(ShellTone.Wait));

        SpriteFrame actual = SpinnerFrameAt(timeMs);

        Assert.Equal((20, 20), (actual.Width, actual.Height));
        Assert.Equal(expected.Rgba, actual.Rgba);
    }

    // 十个相位两两不同(头在圈上的位置不一样);同一相位的时刻(同一个 80 毫秒格里、整圈 800 毫秒之后、负数)画出来逐字节相同
    [Fact]
    public void The_ten_spinner_phases_all_look_different_and_the_same_phase_looks_the_same_at_any_time()
    {
        var byPhase = new byte[10][];
        for (int phase = 0; phase < 10; phase++)
        {
            byPhase[phase] = SpinnerFrameAt(phase * 80 + 5).Rgba;
        }
        for (int a = 0; a < 10; a++)
        {
            for (int b = a + 1; b < 10; b++)
            {
                Assert.NotEqual(byPhase[a], byPhase[b]);
            }
        }

        // 相位 0:0、5、79、800、1600(20 格)、1679(还是第 20 格)、-5、-80;相位 1:80、159、880、1680(21 格);相位 3:240、245、319、1040(13 格)
        foreach (long timeMs in new long[] { 0, 5, 79, 800, 1600, 1679, -5, -80 })
        {
            Assert.Equal(byPhase[0], SpinnerFrameAt(timeMs).Rgba);
        }
        foreach (long timeMs in new long[] { 80, 159, 880, 1680 })
        {
            Assert.Equal(byPhase[1], SpinnerFrameAt(timeMs).Rgba);
        }
        foreach (long timeMs in new long[] { 240, 245, 319, 1040 })
        {
            Assert.Equal(byPhase[3], SpinnerFrameAt(timeMs).Rgba);
        }
    }

    // 只有等待提示里的转圈随时间变;底、色条、文字、非等待提示的图标、左上角小条的图标(信息语气只画静态图标)在 0 与 245 毫秒下逐一相同
    [Theory]
    [InlineData(OnlineTownPromptKind.Waiting)]
    [InlineData(OnlineTownPromptKind.Rejected)]
    [InlineData(OnlineTownPromptKind.ForbiddenMinutes)]
    [InlineData(OnlineTownPromptKind.UnmappedArea)]
    public void Only_the_waiting_spinner_changes_with_the_time(OnlineTownPromptKind kind)
    {
        List<SceneSprite> early = OnlineTownOverlay.Build(Art(), Profile(0), new OnlineTownPrompt(kind, minutes: 5), 0);
        List<SceneSprite> late = OnlineTownOverlay.Build(Art(), Profile(0), new OnlineTownPrompt(kind, minutes: 5), 245);

        Assert.Equal(8, early.Count);
        Assert.Equal(early.Count, late.Count);
        int changed = 0;
        for (int i = 0; i < early.Count; i++)
        {
            Assert.Equal(early[i].AnimationPath, late[i].AnimationPath);
            Assert.Equal((early[i].ScreenX, early[i].ScreenY), (late[i].ScreenX, late[i].ScreenY));
            Assert.Equal((early[i].Frame.Width, early[i].Frame.Height), (late[i].Frame.Width, late[i].Frame.Height));
            if (early[i].AnimationPath == "onlinetown-prompt-spinner")
            {
                Assert.NotEqual(early[i].Frame.Rgba, late[i].Frame.Rgba);
                changed++;
            }
            else
            {
                Assert.Equal(early[i].Frame.Rgba, late[i].Frame.Rgba);
            }
        }
        Assert.Equal(kind == OnlineTownPromptKind.Waiting ? 1 : 0, changed);
    }

    // ───────────────────────── 缺参数 ─────────────────────────

    [Fact]
    public void A_missing_art_or_profile_draws_nothing()
    {
        Assert.Null(OnlineTownOverlay.Build(null, Profile(0), new OnlineTownPrompt(OnlineTownPromptKind.Rejected), 0));
        Assert.Null(OnlineTownOverlay.Build(Art(), null, new OnlineTownPrompt(OnlineTownPromptKind.Rejected), 0));
    }

    [Fact]
    public void The_status_badge_text_is_the_exact_literal_in_every_mode()
    {
        Assert.Equal("联机城镇 · 道具/装备/金币暂未同步", OnlineTownOverlay.StatusBadgeText(Profile(0), true));
        Assert.Equal("ONLINE TOWN / ITEMS EQUIPS GOLD UNSYNCED", OnlineTownOverlay.StatusBadgeText(Profile(0), false));
        Assert.Equal("当前仅支持鬼剑士外观与技能，暂按鬼剑士显示 · 道具/装备/金币未同步", OnlineTownOverlay.StatusBadgeText(Profile(2), true));
        Assert.Equal("SWORDMAN FALLBACK / ITEMS EQUIPS GOLD UNSYNCED", OnlineTownOverlay.StatusBadgeText(Profile(2), false));
        Assert.Equal("联机城镇 · 道具/装备/金币暂未同步", OnlineTownOverlay.StatusBadgeText(null, true));
        Assert.Equal("ONLINE TOWN / ITEMS EQUIPS GOLD UNSYNCED", OnlineTownOverlay.StatusBadgeText(null, false));
    }
}
