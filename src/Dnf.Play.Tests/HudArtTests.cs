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
    private sealed class FakeSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) => Frames.TryGetValue((imageName, frame), out sprite!);
    }

    /// <summary>像素 (x, y) = (R=y%256, G=x%256, B=tag, A=255):从任何一块裁出来的像素都能反推它在原图里的行与列。</summary>
    private static SpriteFrame Coded(int width, int height, byte tag, int offsetX = 0, int offsetY = 0)
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

    private static FakeSprites Hud(bool bar = true, bool hp = true, bool mp = true, bool strip = true, bool extras = true)
    {
        var f = new FakeSprites();
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

    private static List<SceneSprite> Build(HudArt hud, int level = 1, int hp = 100, int hpMax = 100, int mp = 50, int mpMax = 50, IReadOnlyList<HudSkillState> skills = null)
    {
        var list = new List<SceneSprite>();
        int order = 0;
        hud.Build(list, ref order, level, hp, hpMax, mp, mpMax, skills);
        return list;
    }

    private static ScriptValue Str(string text) => new ScriptValue(ScriptValueKind.String, 0, text);

    private static ScriptValue Num(int n) => new ScriptValue(ScriptValueKind.Int, n, null);

    private static SkillDef SkillWithIcon(int id, string image, int normal, int gray)
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
}
