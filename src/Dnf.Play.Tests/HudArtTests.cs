using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 原版底栏(<see cref="HudArt"/>,证据 E-088)。精灵是合成的纯色块与"行号编码"的渐变,验证的是布局规则:
/// 位置、层次、液体露出的行数、等级数字的排法、技能图标亮灰的选择、热键字母的裁切、缓存与共用。
/// </summary>
public class HudArtTests
{
    internal sealed class FakeSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) => Frames.TryGetValue((imageName, frame), out sprite!);
    }

    /// <summary>像素 (x, y) = (R=y%256, G=x%256, B=tag, A=255):从任何一块裁出来的像素都能反推它在原图里的行与列。</summary>
    internal static SpriteFrame Coded(int width, int height, byte tag, int offsetX = 0, int offsetY = 0)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                rgba[o] = (byte)(y % 256);
                rgba[o + 1] = (byte)(x % 256);
                rgba[o + 2] = tag;
                rgba[o + 3] = 255;
            }
        }
        return new SpriteFrame(width, height, offsetX, offsetY, width, height, rgba);
    }

    internal static FakeSprites Hud(bool bar = true, bool hp = true, bool mp = true, bool strip = true, bool extras = true, bool exp = true, bool sp = true)
    {
        var f = new FakeSprites();
        if (sp)
        {
            // SP 数字:帧 0 是负号,帧 1 到 10 是数字 0 到 9(12×11,"1" 是 8 宽、偏移 2,与真实的 sp.img 一样),标记色 60 + 数字
            f.Frames[(HudArt.SpDigitImage, 0)] = Coded(8, 4, 59, 2, 4);
            for (int d = 0; d < 10; d++)
            {
                f.Frames[(HudArt.SpDigitImage, HudArt.SpDigitFirstFrame + d)] = d == 1 ? Coded(8, 11, (byte)(60 + d), 2, 0) : Coded(12, 11, (byte)(60 + d));
            }
        }
        if (exp)
        {
            // 经验条(625×4,自带偏移 0)与满级覆盖层(同尺寸),标记色 5 与 6
            f.Frames[(HudArt.HudImage, HudArt.ExpBarFrame)] = Coded(625, 4, 5);
            f.Frames[(HudArt.HudImage, HudArt.ExpCapFrame)] = Coded(625, 4, 6);
        }
        if (bar)
        {
            f.Frames[(HudArt.HudImage, HudArt.BarFrame)] = Coded(800, 91, 1, 0, 1);
        }
        if (hp)
        {
            f.Frames[(HudArt.HudImage, HudArt.HpFillFrame)] = Coded(56, 56, 2);
        }
        if (mp)
        {
            f.Frames[(HudArt.HudImage, HudArt.MpFillFrame)] = Coded(56, 56, 3);
        }
        if (strip)
        {
            f.Frames[(HudArt.HudImage, HudArt.KeyLabelStripFrame)] = Coded(448, 7, 4, 107, 461);
        }
        if (extras)
        {
            for (int i = 0; i < HudArt.MenuIconFrames.Length; i++)
            {
                f.Frames[(HudArt.HudImage, HudArt.MenuIconFrames[i])] = Coded(24, 26, (byte)(10 + i));
            }
            f.Frames[(HudArt.HudImage, HudArt.MenuButtonFrame)] = Coded(51, 26, 20);
            f.Frames[(HudArt.HudImage, HudArt.ShopButtonFrame)] = Coded(50, 26, 21);
            f.Frames[(HudArt.HudImage, HudArt.InfoButtonFrame)] = Coded(14, 26, 22);
            f.Frames[(HudArt.LevelImage, 10)] = Coded(18, 11, 30);
            for (int d = 0; d < 10; d++)
            {
                // 数字 "1" 窄一点,自带偏移 2(与真实的 level02.img 一样);其余 12 宽
                f.Frames[(HudArt.LevelImage, d)] = d == 1 ? Coded(8, 11, (byte)(40 + d), 2, 0) : Coded(12, 11, (byte)(40 + d));
            }
        }
        return f;
    }

    private static SceneSprite Only(List<SceneSprite> sprites, string tag, int nth = 0)
    {
        return sprites.Where(s => s.AnimationPath == tag).ElementAt(nth);
    }

    private static List<SceneSprite> Build(HudArt hud, int level = 1, int hp = 100, int hpMax = 100, int mp = 50, int mpMax = 50, IReadOnlyList<HudSkillState> skills = null,
        HudExp? exp = null, int? skillPoints = null)
    {
        var list = new List<SceneSprite>();
        int order = 0;
        hud.Build(list, ref order, level, hp, hpMax, mp, mpMax, skills, exp, skillPoints);
        return list;
    }

    private static ScriptValue Str(string text) => new ScriptValue(ScriptValueKind.String, 0, text);

    private static ScriptValue Num(int n) => new ScriptValue(ScriptValueKind.Int, n, null);

    internal static SkillDef SkillWithIcon(int id, string image, int normal, int gray)
    {
        return new SkillDef(id, "skill/test/s" + id + ".skl", new[] { new ScriptLine("[icon]", new[] { Str(image), Num(normal), Str(image), Num(gray) }) });
    }

    // ------------------------------------------------------------------ 创建与共用

    [Fact]
    public void CreationNeedsTheBarBothLiquidsAndTheKeyLabelStrip()
    {
        Assert.NotNull(HudArt.TryCreate(Hud()));
        Assert.Null(HudArt.TryCreate(null!));
        Assert.Null(HudArt.TryCreate(Hud(bar: false)));
        Assert.Null(HudArt.TryCreate(Hud(hp: false)));
        Assert.Null(HudArt.TryCreate(Hud(mp: false)));
        Assert.Null(HudArt.TryCreate(Hud(strip: false)));
        Assert.Null(HudArt.For(null!));
        Assert.Null(HudArt.For(Hud(bar: false)));

        // 条太窄(装不下最后一个热键字母)也不行
        FakeSprites narrow = Hud();
        narrow.Frames[(HudArt.HudImage, HudArt.KeyLabelStripFrame)] = Coded(300, 7, 4);
        Assert.Null(HudArt.TryCreate(narrow));
    }

    [Fact]
    public void TheSameSpriteSourceSharesOneHudObjectSoTheDerivedFramesAreNotRebuiltPerRoom()
    {
        FakeSprites sprites = Hud();
        HudArt a = HudArt.For(sprites)!;
        Assert.Same(a, HudArt.For(sprites));
        Assert.NotSame(a, HudArt.For(Hud()));
        // 同样的行数每次拿到的是同一个液体帧对象(GPU 端按帧对象缓存贴图)
        SpriteFrame first = Only(Build(a, hp: 30, hpMax: 100), "hud-hp").Frame;
        SpriteFrame again = Only(Build(a, hp: 30, hpMax: 100), "hud-hp").Frame;
        Assert.Same(first, again);
        Assert.NotSame(first, Only(Build(a, hp: 31, hpMax: 100), "hud-hp").Frame);
    }

    // ------------------------------------------------------------------ 液体

    [Fact]
    public void OrbRowsRoundUpKeepAtLeastOneRowWhileAliveAndClampToTheFullOrb()
    {
        Assert.Equal(0, HudArt.OrbRows(0, 100));
        Assert.Equal(0, HudArt.OrbRows(-5, 100));
        Assert.Equal(0, HudArt.OrbRows(5, 0));
        Assert.Equal(1, HudArt.OrbRows(1, 1000));                       // 还有一点就至少一行
        Assert.Equal(28, HudArt.OrbRows(50, 100));
        Assert.Equal(29, HudArt.OrbRows(51, 100));                      // 向上取整:28.56 → 29
        Assert.Equal(56, HudArt.OrbRows(99, 100));                      // 55.44 → 56
        Assert.Equal(56, HudArt.OrbRows(100, 100));
        Assert.Equal(56, HudArt.OrbRows(250, 100));
    }

    [Fact]
    public void TheBarIsDrawnFirstThenTheLiquidsWithTheSurfaceMovingDownAsTheValueDrops()
    {
        HudArt hud = HudArt.TryCreate(Hud())!;
        List<SceneSprite> sprites = Build(hud, hp: 50, hpMax: 100, mp: 50, mpMax: 50);

        Assert.Equal("hud-bar", sprites[0].AnimationPath);
        // 底栏的像素块在 (0,509)(帧自带偏移 y=1,所以摆放原点是 508)
        Assert.Equal((0, 509), (sprites[0].ScreenX, sprites[0].ScreenY));

        // 血球一半:露出下面 28 行,液面在 524 + 28;裁出来的帧第一行是原图第 28 行
        SceneSprite hp = Only(sprites, "hud-hp");
        Assert.Equal((13, 524 + 28), (hp.ScreenX, hp.ScreenY));
        Assert.Equal((56, 28), (hp.Frame.Width, hp.Frame.Height));
        Assert.Equal(28, hp.Frame.Rgba[0]);
        Assert.Equal(55, hp.Frame.Rgba[(27 * 56) * 4]);                  // 最后一行 = 原图第 55 行

        // 魔法球满:用原图,不裁
        SceneSprite mp = Only(sprites, "hud-mp");
        Assert.Equal((732, 524), (mp.ScreenX, mp.ScreenY));
        Assert.Equal(56, mp.Frame.Height);
        Assert.Equal(0, mp.Frame.Rgba[0]);

        // 先底栏、后液体、层次递增、全在界面层
        Assert.True(sprites.IndexOf(hp) > 0 && sprites.IndexOf(mp) > sprites.IndexOf(hp));
        Assert.All(sprites, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        for (int i = 1; i < sprites.Count; i++)
        {
            Assert.True(sprites[i].SortKey > sprites[i - 1].SortKey);
        }
    }

    [Fact]
    public void AnEmptyOrbShowsNoLiquidAtAllAndANearlyEmptyOneShowsOneRowAtTheBottom()
    {
        HudArt hud = HudArt.TryCreate(Hud())!;
        List<SceneSprite> empty = Build(hud, hp: 0, hpMax: 100, mp: 0, mpMax: 0);
        Assert.DoesNotContain(empty, s => s.AnimationPath == "hud-hp" || s.AnimationPath == "hud-mp");

        SceneSprite one = Only(Build(hud, hp: 1, hpMax: 10000), "hud-hp");
        Assert.Equal((13, 524 + 55), (one.ScreenX, one.ScreenY));
        Assert.Equal(1, one.Frame.Height);
        Assert.Equal(55, one.Frame.Rgba[0]);                             // 取的是最下面一行
    }

    // ------------------------------------------------------------------ 经验条(证据 E-111)

    [Fact]
    public void TheExpBarShowsTheFirstColumnsOfFrameFourAtTheMeasuredPositionAndIsAbsentWhenNotGiven()
    {
        HudArt hud = HudArt.TryCreate(Hud())!;
        Assert.DoesNotContain(Build(hud), s => s.AnimationPath == "hud-exp" || s.AnimationPath == "hud-exp-cap");   // 没给经验:不画

        // 一半:625 × 0.5 = 312.5 截断成 312 列;摆在 (88, 591),原图的第 0 到 311 列
        SceneSprite half = Only(Build(hud, exp: new HudExp(500, 1000, false)), "hud-exp");
        Assert.Equal((88, 591), (half.ScreenX, half.ScreenY));
        Assert.Equal((312, 4), (half.Frame.Width, half.Frame.Height));
        Assert.Equal(5, half.Frame.Rgba[2]);                             // 标记色:经验条这一帧
        Assert.Equal(0, half.Frame.Rgba[1]);                             // 第一列是原图第 0 列
        Assert.Equal(311 % 256, half.Frame.Rgba[(311) * 4 + 1]);         // 最后一列是原图第 311 列
        Assert.Equal(3, half.Frame.Rgba[(3 * 312) * 4]);                 // 最后一行是原图第 3 行
    }

    [Fact]
    public void AFullExpBarUsesTheWholeFrameAndAnEmptyOneDrawsNothing()
    {
        HudArt hud = HudArt.TryCreate(Hud())!;
        SceneSprite full = Only(Build(hud, exp: new HudExp(1000, 1000, false)), "hud-exp");
        Assert.Equal((88, 591), (full.ScreenX, full.ScreenY));
        Assert.Equal(625, full.Frame.Width);
        Assert.Equal(625, Only(Build(hud, exp: new HudExp(5000, 1000, false)), "hud-exp").Frame.Width);   // 超过分母也是满条
        Assert.DoesNotContain(Build(hud, exp: new HudExp(0, 1000, false)), s => s.AnimationPath == "hud-exp");
        Assert.DoesNotContain(Build(hud, exp: new HudExp(1, 1000, false)), s => s.AnimationPath == "hud-exp");   // 0.625 列截断成 0:什么也不露
    }

    [Fact]
    public void TheExpBarDrawsAfterTheOrbsAndBeforeTheLevelPlateInTheUiLayerWithRisingSortKeys()
    {
        HudArt hud = HudArt.TryCreate(Hud())!;
        List<SceneSprite> sprites = Build(hud, level: 12, exp: new HudExp(500, 1000, false));
        SceneSprite exp = Only(sprites, "hud-exp");
        Assert.True(sprites.IndexOf(Only(sprites, "hud-mp")) < sprites.IndexOf(exp));
        Assert.True(sprites.IndexOf(exp) < sprites.IndexOf(Only(sprites, "hud-level", 0)));
        Assert.All(sprites, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        for (int i = 1; i < sprites.Count; i++)
        {
            Assert.True(sprites[i].SortKey > sprites[i - 1].SortKey);
        }
    }

    [Fact]
    public void TheMaxLevelOverlayIsDrawnOnTopOfTheBarEvenWhenTheBarIsEmpty()
    {
        HudArt hud = HudArt.TryCreate(Hud())!;
        List<SceneSprite> capped = Build(hud, exp: new HudExp(500, 1000, true));
        SceneSprite bar = Only(capped, "hud-exp");
        SceneSprite cap = Only(capped, "hud-exp-cap");
        Assert.Equal((88, 591), (cap.ScreenX, cap.ScreenY));
        Assert.Equal((625, 4, 6), (cap.Frame.Width, cap.Frame.Height, (int)cap.Frame.Rgba[2]));   // 整张覆盖层,标记色 6
        Assert.True(cap.SortKey > bar.SortKey);                          // 盖在条上面
        List<SceneSprite> empty = Build(hud, exp: new HudExp(0, 1000, true));
        Assert.DoesNotContain(empty, s => s.AnimationPath == "hud-exp");
        Assert.Contains(empty, s => s.AnimationPath == "hud-exp-cap");   // 覆盖层与条的宽度无关
    }

    [Fact]
    public void TheSameExpWidthSharesOneCroppedFrameAndMissingExpFramesAreSkipped()
    {
        HudArt hud = HudArt.TryCreate(Hud())!;
        SpriteFrame first = Only(Build(hud, exp: new HudExp(500, 1000, false)), "hud-exp").Frame;
        SpriteFrame again = Only(Build(hud, exp: new HudExp(501, 1000, false)), "hud-exp").Frame;   // 625 × 0.501 = 313.1 → 313,不同宽度
        SpriteFrame same = Only(Build(hud, exp: new HudExp(500, 1000, false)), "hud-exp").Frame;
        Assert.Same(first, same);                                        // 同样的宽度拿到同一个帧对象(GPU 端按帧对象缓存贴图)
        Assert.NotSame(first, again);

        HudArt bare = HudArt.TryCreate(Hud(exp: false))!;
        List<SceneSprite> sprites = Build(bare, exp: new HudExp(500, 1000, true));
        Assert.DoesNotContain(sprites, s => s.AnimationPath == "hud-exp" || s.AnimationPath == "hud-exp-cap");
        Assert.Contains(sprites, s => s.AnimationPath == "hud-bar");     // 缺经验条的图不影响其余部分
    }

    // ------------------------------------------------------------------ 等级牌

    [Fact]
    public void TheLevelPlateIsTheLvLabelFollowedByDigitsTwelvePixelsApartEachKeepingItsOwnOffset()
    {
        HudArt hud = HudArt.TryCreate(Hud())!;

        List<SceneSprite> two = Build(hud, level: 12);
        Assert.Equal((19, 585), (Only(two, "hud-level", 0).ScreenX, Only(two, "hud-level", 0).ScreenY));
        // "1" 帧自带偏移 2:原点 37 → 像素块 39;"2" 原点 49
        Assert.Equal((39, 585), (Only(two, "hud-level", 1).ScreenX, Only(two, "hud-level", 1).ScreenY));
        Assert.Equal((49, 585), (Only(two, "hud-level", 2).ScreenX, Only(two, "hud-level", 2).ScreenY));
        Assert.Equal(3, two.Count(s => s.AnimationPath == "hud-level"));
        Assert.Equal(41, Only(two, "hud-level", 1).Frame.Rgba[2]);       // 标记色:数字 1
        Assert.Equal(42, Only(two, "hud-level", 2).Frame.Rgba[2]);       // 数字 2

        Assert.Equal(2, Build(hud, level: 7).Count(s => s.AnimationPath == "hud-level"));
        Assert.Equal(4, Build(hud, level: 100).Count(s => s.AnimationPath == "hud-level"));
        List<SceneSprite> zero = Build(hud, level: -3);                  // 负数当 0
        Assert.Equal(40, Only(zero, "hud-level", 1).Frame.Rgba[2]);
    }

    // ------------------------------------------------------------------ 菜单与按钮

    [Fact]
    public void TheMenuIconsAndTheThreeButtonsSitAtTheMeasuredPositions()
    {
        HudArt hud = HudArt.TryCreate(Hud())!;
        List<SceneSprite> sprites = Build(hud);

        for (int i = 0; i < 5; i++)
        {
            SceneSprite icon = Only(sprites, "hud-menu-icon", i);
            Assert.Equal((283 + 26 * i, 560), (icon.ScreenX, icon.ScreenY));
            Assert.Equal(10 + i, icon.Frame.Rgba[2]);                    // 五个不同的帧,按顺序
        }
        Assert.Equal((413, 560), (Only(sprites, "hud-menu").ScreenX, Only(sprites, "hud-menu").ScreenY));
        Assert.Equal((466, 560), (Only(sprites, "hud-shop").ScreenX, Only(sprites, "hud-shop").ScreenY));
        Assert.Equal((519, 559), (Only(sprites, "hud-info").ScreenX, Only(sprites, "hud-info").ScreenY));
    }

    [Fact]
    public void MissingExtrasAreSimplySkippedWhileTheCoreStillDraws()
    {
        HudArt hud = HudArt.TryCreate(Hud(extras: false))!;
        List<SceneSprite> sprites = Build(hud, level: 5);
        Assert.Contains(sprites, s => s.AnimationPath == "hud-bar");
        Assert.Contains(sprites, s => s.AnimationPath == "hud-hp");
        Assert.DoesNotContain(sprites, s => s.AnimationPath == "hud-menu" || s.AnimationPath == "hud-level" || s.AnimationPath == "hud-menu-icon");
        Assert.Equal(HudArt.SlotCount, sprites.Count(s => s.AnimationPath == "hud-key"));
    }

    // ------------------------------------------------------------------ 技能槽与热键字母

    [Fact]
    public void ASkillSlotShowsTheLitIconWhenUsableAndTheGrayOneWhenNotAndNothingWithoutAnIcon()
    {
        FakeSprites sprites = Hud();
        // .skl 里写的是 "Character/Test/SkillIcon.img",图包里的名字是 sprite/ + 小写
        sprites.Frames[("sprite/character/test/skillicon.img", 10)] = Coded(28, 28, 50);
        sprites.Frames[("sprite/character/test/skillicon.img", 11)] = Coded(28, 28, 51);
        HudArt hud = HudArt.TryCreate(sprites)!;
        SkillDef def = SkillWithIcon(5, "Character/Test/SkillIcon.img", 10, 11);

        SceneSprite lit = Only(Build(hud, skills: new[] { new HudSkillState(1, def, true) }), "hud-skill");
        Assert.Equal((538 + 30, 558), (lit.ScreenX, lit.ScreenY));       // S 槽:第二格
        Assert.Equal(50, lit.Frame.Rgba[2]);

        SceneSprite gray = Only(Build(hud, skills: new[] { new HudSkillState(2, def, false) }), "hud-skill");
        Assert.Equal((538 + 60, 558), (gray.ScreenX, gray.ScreenY));
        Assert.Equal(51, gray.Frame.Rgba[2]);

        // 没有图标的技能、没有技能定义、槽号越界:不画
        var noIcon = new SkillDef(6, "skill/test/none.skl", new[] { new ScriptLine("[type]", new[] { Str("[active]") }) });
        List<SceneSprite> none = Build(hud, skills: new[]
        {
            new HudSkillState(0, noIcon, true), new HudSkillState(1, null!, true), new HudSkillState(-1, def, true), new HudSkillState(HudArt.SlotCount, def, true),
        });
        Assert.DoesNotContain(none, s => s.AnimationPath == "hud-skill");

        // 图包里缺亮图标:退而用灰的(反过来也是)
        FakeSprites onlyGray = Hud();
        onlyGray.Frames[("sprite/character/test/skillicon.img", 11)] = Coded(28, 28, 51);
        HudArt grayHud = HudArt.TryCreate(onlyGray)!;
        Assert.Equal(51, Only(Build(grayHud, skills: new[] { new HudSkillState(0, def, true) }), "hud-skill").Frame.Rgba[2]);
    }

    [Fact]
    public void AGrayIconWithADifferentOffsetIsAlignedToTheSameOriginAsTheLitOne()
    {
        FakeSprites sprites = Hud();
        sprites.Frames[("sprite/character/test/skillicon.img", 0)] = Coded(28, 28, 50, 0, 0);
        sprites.Frames[("sprite/character/test/skillicon.img", 1)] = Coded(24, 24, 51, 2, 3);
        HudArt hud = HudArt.TryCreate(sprites)!;
        SkillDef def = SkillWithIcon(5, "Character/Test/SkillIcon.img", 0, 1);

        SceneSprite gray = Only(Build(hud, skills: new[] { new HudSkillState(0, def, false) }), "hud-skill");
        Assert.Equal((538 + 2, 558 + 3), (gray.ScreenX, gray.ScreenY));  // 原点 (538,558) + 灰图自己的偏移
    }

    [Fact]
    public void TheSixKeyLettersAreCutFromTheStripAndAlwaysDrawnOnTopOfTheSkillIcons()
    {
        FakeSprites sprites = Hud();
        sprites.Frames[("sprite/character/test/skillicon.img", 0)] = Coded(28, 28, 50);
        HudArt hud = HudArt.TryCreate(sprites)!;
        SkillDef def = SkillWithIcon(5, "Character/Test/SkillIcon.img", 0, 0);
        List<SceneSprite> list = Build(hud, skills: new[] { new HudSkillState(0, def, true) });

        for (int i = 0; i < HudArt.SlotCount; i++)
        {
            SceneSprite key = Only(list, "hud-key", i);
            Assert.Equal((560 + 30 * i, 579), (key.ScreenX, key.ScreenY));
            Assert.Equal((HudArt.KeyLabelWidth, HudArt.KeyLabelHeight), (key.Frame.Width, key.Frame.Height));
            Assert.Equal(HudArt.KeyLabelColumns[i] % 256, key.Frame.Rgba[1]);   // 第一列的列号 = 在条里的起始列
            Assert.Equal(4, key.Frame.Rgba[2]);
        }
        Assert.True(list.IndexOf(Only(list, "hud-key", 0)) > list.IndexOf(Only(list, "hud-skill")));
    }

    // ------------------------------------------------------------------ 冷却遮罩、技能等级小字与 SP 数字(证据 E-112)

    private static (HudArt Hud, SkillDef Def) WithIcon()
    {
        FakeSprites sprites = Hud();
        sprites.Frames[("sprite/character/test/skillicon.img", 10)] = Coded(28, 28, 50);
        sprites.Frames[("sprite/character/test/skillicon.img", 11)] = Coded(28, 28, 51);
        return (HudArt.TryCreate(sprites)!, SkillWithIcon(5, "Character/Test/SkillIcon.img", 10, 11));
    }

    private static (int R, int G, int B, int A) Px(SpriteFrame f, int x, int y)
    {
        int o = (y * f.Width + x) * 4;
        return (f.Rgba[o], f.Rgba[o + 1], f.Rgba[o + 2], f.Rgba[o + 3]);
    }

    private static int CountColor(SpriteFrame f, uint rgb)
    {
        int n = 0;
        for (int y = 0; y < f.Height; y++)
        {
            for (int x = 0; x < f.Width; x++)
            {
                (int r, int g, int b, int a) = Px(f, x, y);
                if (a == 255 && ((uint)r << 16 | (uint)g << 8 | (uint)b) == rgb)
                {
                    n++;
                }
            }
        }
        return n;
    }

    [Fact]
    public void CooldownRowsAreTwentyEightTimesTheRemainingFractionTruncatedAndClamped()
    {
        Assert.Equal(0, HudArt.CooldownRows(0.0));
        Assert.Equal(0, HudArt.CooldownRows(-0.5));
        Assert.Equal(0, HudArt.CooldownRows(double.NaN));
        Assert.Equal(28, HudArt.CooldownRows(1.0));
        Assert.Equal(28, HudArt.CooldownRows(3.0));
        Assert.Equal(0, HudArt.CooldownRows(0.03));                      // 28 × 0.03 = 0.84:向零取整,一行都盖不住
        Assert.Equal(2, HudArt.CooldownRows(0.08));
        Assert.Equal(14, HudArt.CooldownRows(0.5));
        Assert.Equal(21, HudArt.CooldownRows(0.75));
        Assert.Equal(25, HudArt.CooldownRows(0.9));
        Assert.Equal(27, HudArt.CooldownRows(0.999));
    }

    [Fact]
    public void TheLitRowsOfTheCooldownReproduceTheEightBaselineSamples()
    {
        // 基线 S08 里一次冷却过程取了 8 个采样点(图标亮着的行数 = 28 减去盖住的行数);
        // 这里把它们折成"已过的比例":总长取合成的 600,已过 15、125、145、165、185、215、235、255,对应亮 1、6、7、8、9、11、11、12 行
        int[] elapsed = { 15, 125, 145, 165, 185, 215, 235, 255 };
        int[] lit = { 1, 6, 7, 8, 9, 11, 11, 12 };
        for (int i = 0; i < elapsed.Length; i++)
        {
            double left = (600.0 - elapsed[i]) / 600.0;
            Assert.Equal(lit[i], HudArt.SkillIconSize - HudArt.CooldownRows(left));
        }
    }

    [Fact]
    public void TheCooldownShadeCoversTheTopRowsOfTheIconAndSitsAboveTheLabelAndBelowTheKeyLetter()
    {
        (HudArt hud, SkillDef def) = WithIcon();
        List<SceneSprite> list = Build(hud, skills: new[] { new HudSkillState(1, def, false, 0.5, 2, false) });
        SceneSprite shade = Only(list, "hud-skill-cooldown");
        Assert.Equal((538 + 30, 558), (shade.ScreenX, shade.ScreenY));
        Assert.Equal((28, 14), (shade.Frame.Width, shade.Frame.Height));
        Assert.Equal(128, shade.TintAlpha);                              // (8,8,8) 盖 128/255:被盖的像素 = 灰图标 / 2 + 4
        Assert.Equal((8, 8, 8, 255), Px(shade.Frame, 0, 0));
        Assert.Equal((8, 8, 8, 255), Px(shade.Frame, 27, 13));

        int iShade = list.IndexOf(shade);
        Assert.True(iShade > list.IndexOf(Only(list, "hud-skill")));
        Assert.True(iShade > list.IndexOf(Only(list, "hud-skill-level", 1)));
        Assert.True(iShade < list.IndexOf(Only(list, "hud-key", 0)));

        // 同样的行数拿到同一个帧对象;没在冷却或不到一行就没有遮罩;满冷却盖满整个图标
        Assert.Same(shade.Frame, Only(Build(hud, skills: new[] { new HudSkillState(2, def, false, 0.5) }), "hud-skill-cooldown").Frame);
        Assert.DoesNotContain(Build(hud, skills: new[] { new HudSkillState(1, def, false, 0.0) }), s => s.AnimationPath == "hud-skill-cooldown");
        Assert.DoesNotContain(Build(hud, skills: new[] { new HudSkillState(1, def, false, 0.03) }), s => s.AnimationPath == "hud-skill-cooldown");
        Assert.Equal(28, Only(Build(hud, skills: new[] { new HudSkillState(1, def, false, 1.0) }), "hud-skill-cooldown").Frame.Height);
    }

    [Fact]
    public void TheSkillLevelLabelIsLvDotAndTheNumberInGoldWithABlackOutlineAtTheMeasuredPlace()
    {
        (HudArt hud, SkillDef def) = WithIcon();
        List<SceneSprite> list = Build(hud, skills: new[] { new HudSkillState(1, def, true, 0.0, 1, false) });
        SceneSprite prefix = Only(list, "hud-skill-level", 0);
        SceneSprite number = Only(list, "hud-skill-level", 1);

        // "Lv." 的填充(9 宽 5 高)从图标内 (10, 1) 起:帧比填充多一圈描边,所以帧放在 (9, 0)
        Assert.Equal((538 + 30 + 9, 558), (prefix.ScreenX, prefix.ScreenY));
        Assert.Equal((11, 7), (prefix.Frame.Width, prefix.Frame.Height));
        Assert.Equal((255, 206, 49, 255), Px(prefix.Frame, 1, 1));       // "L" 竖笔的最上一点
        Assert.Equal((255, 206, 49, 255), Px(prefix.Frame, 3, 5));       // "L" 的脚
        Assert.Equal((255, 206, 49, 255), Px(prefix.Frame, 9, 5));       // 句点
        Assert.Equal((0, 0, 0, 255), Px(prefix.Frame, 0, 0));            // 描边(8 邻域,含斜角)
        Assert.Equal((0, 0, 0, 255), Px(prefix.Frame, 2, 0));
        Assert.Equal(13, CountColor(prefix.Frame, HudArt.LabelGoldRgb)); // "L" 7 点 + "v" 5 点 + "." 1 点

        // 数字 "1"(2 宽)的填充右端落在图标内 x = 26,描边到 27
        Assert.Equal((538 + 30 + 24, 558), (number.ScreenX, number.ScreenY));
        Assert.Equal((4, 7), (number.Frame.Width, number.Frame.Height));
        Assert.Equal(6, CountColor(number.Frame, HudArt.LabelGoldRgb));
        Assert.Equal((255, 206, 49, 255), Px(number.Frame, 2, 5));       // "1" 的竖笔(填充的右列)落在帧的第 2 列,第 3 列是右边的描边
        Assert.Equal((0, 0, 0, 255), Px(number.Frame, 3, 5));

        // 两位数:数字串的右端还是图标内 x = 26
        SceneSprite twelve = Only(Build(hud, skills: new[] { new HudSkillState(0, def, true, 0.0, 12, false) }), "hud-skill-level", 1);
        Assert.Equal(8, twelve.Frame.Width);
        Assert.Equal(538 + 20, twelve.ScreenX);

        // 同样的字拿到同一个帧对象;等级 0 或负数不画
        Assert.Same(prefix.Frame, Only(Build(hud, skills: new[] { new HudSkillState(2, def, true, 0.0, 7, false) }), "hud-skill-level", 0).Frame);
        Assert.DoesNotContain(Build(hud, skills: new[] { new HudSkillState(1, def, true, 0.0, 0, false) }), s => s.AnimationPath == "hud-skill-level");
        Assert.DoesNotContain(Build(hud, skills: new[] { new HudSkillState(1, def, true, 0.0, -3, false) }), s => s.AnimationPath == "hud-skill-level");
    }

    [Fact]
    public void AGrayLabelUsesTheMeasuredGrayFillAndDarkOutline()
    {
        (HudArt hud, SkillDef def) = WithIcon();
        SceneSprite gray = Only(Build(hud, skills: new[] { new HudSkillState(0, def, false, 0.0, 1, true) }), "hud-skill-level", 0);
        Assert.Equal((132, 132, 132, 255), Px(gray.Frame, 1, 1));
        Assert.Equal((16, 16, 16, 255), Px(gray.Frame, 0, 0));
        Assert.Equal(0, CountColor(gray.Frame, HudArt.LabelGoldRgb));
        Assert.Equal(13, CountColor(gray.Frame, HudArt.LabelGrayRgb));
        Assert.NotSame(gray.Frame, Only(Build(hud, skills: new[] { new HudSkillState(0, def, true, 0.0, 1, false) }), "hud-skill-level", 0).Frame);
    }

    [Fact]
    public void TheSpNumberIsCenteredInTheBoxAtTenPixelsPerDigitAndEachDigitKeepsItsOwnOffset()
    {
        HudArt hud = HudArt.TryCreate(Hud())!;
        // 基线样本:一个两位数,两个数字块落在 x = 755、765,y = 585(这里的数是合成的 47)
        List<SceneSprite> two = Build(hud, skillPoints: 47);
        Assert.Equal(2, two.Count(s => s.AnimationPath == "hud-sp"));
        SceneSprite first = Only(two, "hud-sp", 0);
        SceneSprite second = Only(two, "hud-sp", 1);
        Assert.Equal((755, 585), (first.ScreenX, first.ScreenY));
        Assert.Equal((765, 585), (second.ScreenX, second.ScreenY));
        Assert.Equal(60 + 4, first.Frame.Rgba[2]);                       // 数字 d 取 sp.img 的第 d + 1 帧
        Assert.Equal(60 + 7, second.Frame.Rgba[2]);

        Assert.Equal(760, Only(Build(hud, skillPoints: 5), "hud-sp").ScreenX);                   // 一位数:765 − 5
        Assert.Equal(762, Only(Build(hud, skillPoints: 1), "hud-sp").ScreenX);                   // "1" 窄,自带偏移 2
        List<SceneSprite> three = Build(hud, skillPoints: 120);
        Assert.Equal(new[] { 752, 760, 770 }, three.Where(s => s.AnimationPath == "hud-sp").Select(s => s.ScreenX).ToArray());   // 三位数 "120":765 − 15 = 750 起,"1" 加偏移 2

        // 负数当 0 画;没给就不画;缺图也不画(不报错)
        Assert.Equal(60 + 0, Only(Build(hud, skillPoints: -4), "hud-sp").Frame.Rgba[2]);
        Assert.DoesNotContain(Build(hud), s => s.AnimationPath == "hud-sp");
        HudArt noDigits = HudArt.TryCreate(Hud(sp: false))!;
        Assert.DoesNotContain(Build(noDigits, skillPoints: 47), s => s.AnimationPath == "hud-sp");

        // 层次:在等级牌之后、菜单图标之前
        List<SceneSprite> all = Build(hud, skillPoints: 47);
        Assert.True(all.IndexOf(Only(all, "hud-sp", 0)) > all.IndexOf(Only(all, "hud-level", 0)));
        Assert.True(all.IndexOf(Only(all, "hud-sp", 1)) < all.IndexOf(Only(all, "hud-menu-icon", 0)));
    }
}
