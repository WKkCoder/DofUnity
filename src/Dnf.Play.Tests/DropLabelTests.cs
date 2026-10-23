using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using static Dnf.Play.Tests.ItemDropSessionTests;
using static Dnf.Play.Tests.RoomCombatTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 地上物品与金币的名牌(M30,证据 E-122):盒子的削角、颜色表、名牌图的大小与像素、摆放(水平居中、盒子下沿比图上沿高 8 行、按进深排序)、
/// 文字取法,以及战斗会话里"够得着的那一件画成选中"。文字表与字体都是合成的(方块字体:"ab" 的排版宽 6、墨迹 3 行),期望值是手算。
/// </summary>
public class DropLabelTests
{
    // ------------------------------------------------------------------ 工具

    private static StrLibrary Library(params (string Path, string Text)[] files)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string text) in files)
        {
            map[path] = Encoding.ASCII.GetBytes(text);
        }
        return new StrLibrary(p => map.TryGetValue(p, out byte[] data) ? data : null);
    }

    private static WorldTexts Texts()
    {
        return new WorldTexts(Library(
            ("stackable/stackable.kor.str", "name_a>ab\r\nname_b>abcdef\r\nname_c>\r\nname_pct>5%% off\r\n"),
            ("ui/ui.kor.str", "inventory_main_bottom06>%d gold\r\n")));
    }

    private static OverlayArt Art() => new OverlayArt { Fonts = new BlockFonts() };

    private static ItemInfo Named(string nameKey, int rarity = 0)
    {
        ItemInfo item = StackItem();
        item.NameKey = nameKey;
        item.TextKind = StrLibrary.KindStackable;
        item.Rarity = rarity;
        return item;
    }

    private static FieldDrop LandedItem(int id, ItemInfo item, double x, double y)
    {
        FieldDrop d = FieldDrop.LaunchItem(id, item, x, y, new DropRandom(id));
        d.Advance(5000);
        Assert.True(d.Landed);
        return d;
    }

    private static FieldDrop LandedGold(int id, int amount, double x, double y)
    {
        FieldDrop d = FieldDrop.Launch(id, amount, x, y, new DropRandom(id));
        d.Advance(5000);
        Assert.True(d.Landed);
        return d;
    }

    private static (int R, int G, int B, int A) Px(SpriteFrame f, int x, int y)
    {
        int o = (y * f.Width + x) * 4;
        return (f.Rgba[o], f.Rgba[o + 1], f.Rgba[o + 2], f.Rgba[o + 3]);
    }

    private static (int, int, int, int) Rgba(uint rgb, int a) => ((int)((rgb >> 16) & 0xFF), (int)((rgb >> 8) & 0xFF), (int)(rgb & 0xFF), a);

    // ------------------------------------------------------------------ 颜色与形状

    [Fact]
    public void TheRarityColorsKeepTheMeasuredWhiteAndPurpleAndClampAtBothEnds()
    {
        Assert.Equal(6, DropLabels.RarityColors.Length);
        Assert.Equal(0xFFFFFFu, DropLabels.RarityColor(0));
        Assert.Equal(0xB36BFFu, DropLabels.RarityColor(2));
        Assert.Equal(DropLabels.RarityColors[0], DropLabels.RarityColor(-3));
        Assert.Equal(DropLabels.RarityColors[5], DropLabels.RarityColor(5));
        Assert.Equal(DropLabels.RarityColors[5], DropLabels.RarityColor(99));
        Assert.Equal(DropLabels.RarityColors.Length, DropLabels.RarityColors.Distinct().Count());
    }

    [Fact]
    public void ColorOfIsTheRarityColorForItemsAndWhiteForGold()
    {
        Assert.Equal(0xB36BFFu, DropLabels.ColorOf(LandedItem(1, Named("name_a", rarity: 2), 300, 100)));
        Assert.Equal(DropLabels.GoldColor, DropLabels.ColorOf(LandedGold(2, 10, 300, 100)));
        Assert.Equal(DropLabels.GoldColor, DropLabels.ColorOf(null));
    }

    [Fact]
    public void TheBoxLosesTwoPixelsOnTheOuterRowAndOneOnTheNextRowAtEveryCorner()
    {
        const int w = 20;
        const int h = 23;
        // 左上角:第 0 行少 2 个,第 1 行少 1 个,第 2 行起是满的
        Assert.False(DropLabels.InBox(0, 0, w, h));
        Assert.False(DropLabels.InBox(1, 0, w, h));
        Assert.True(DropLabels.InBox(2, 0, w, h));
        Assert.False(DropLabels.InBox(0, 1, w, h));
        Assert.True(DropLabels.InBox(1, 1, w, h));
        Assert.True(DropLabels.InBox(0, 2, w, h));
        // 其余三个角对称
        Assert.False(DropLabels.InBox(w - 1, 0, w, h));
        Assert.False(DropLabels.InBox(w - 2, 0, w, h));
        Assert.True(DropLabels.InBox(w - 3, 0, w, h));
        Assert.False(DropLabels.InBox(w - 1, 1, w, h));
        Assert.True(DropLabels.InBox(w - 2, 1, w, h));
        Assert.False(DropLabels.InBox(0, h - 1, w, h));
        Assert.False(DropLabels.InBox(1, h - 1, w, h));
        Assert.True(DropLabels.InBox(2, h - 1, w, h));
        Assert.False(DropLabels.InBox(w - 1, h - 1, w, h));
        Assert.True(DropLabels.InBox(w - 1, h - 3, w, h));
        // 盒子外面
        Assert.False(DropLabels.InBox(-1, 5, w, h));
        Assert.False(DropLabels.InBox(w, 5, w, h));
        Assert.False(DropLabels.InBox(5, -1, w, h));
        Assert.False(DropLabels.InBox(5, h, w, h));
        // 一共少 4 × 3 = 12 个像素
        int inside = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                inside += DropLabels.InBox(x, y, w, h) ? 1 : 0;
            }
        }
        Assert.Equal(w * h - 12, inside);
    }

    // ------------------------------------------------------------------ 名牌图

    [Fact]
    public void TheLabelFrameIsTheBoxPlusTheTextAndALayerForTheSelectionRing()
    {
        var labels = new DropLabels(Art());
        SpriteFrame f = labels.Frame("ab", 0xB36BFFu, false);
        // 方块字体下 "ab" 的排版宽 6(文字图宽 8,含两侧各 1 像素描边):盒子宽 6 + 2 × (1 + 4) = 16、高 23,四周再留 1 像素
        Assert.Equal((18, 25), (f.Width, f.Height));
        Assert.Equal((0, 0), (f.OffsetX, f.OffsetY));

        Assert.Equal(Rgba(DropLabels.BorderRgb, DropLabels.BorderAlpha), Px(f, 6, 1));          // 上边框(盒子 (5,0))
        Assert.Equal(Rgba(DropLabels.BorderRgb, DropLabels.BorderAlpha), Px(f, 2, 2));          // 削角后的第一个边框点(盒子 (1,1))
        Assert.Equal((0, 0, 0, 0), Px(f, 1, 1));                                                // 盒子的角(盒子 (0,0))被削掉
        Assert.Equal((0, 0, 0, 0), Px(f, 6, 0));                                                // 没选中:外框那一圈是空的
        Assert.Equal((0, 0, 0, DropLabels.FillAlpha), Px(f, 4, 2));                             // 底:黑色 80%
        Assert.Equal((0, 0, 0, DropLabels.FillAlpha), Px(f, 13, 16));

        // 文字:墨迹 3 行在 12 行的段里居中 → 第一行在盒子上沿下 5 + (12 − 3) / 2 = 9 行;排版原点在左边框内侧 4 像素
        Assert.Equal((0xB3, 0x6B, 0xFF, 255), Px(f, 6, 10));                                    // "a" 的左上角墨迹
        Assert.Equal((0xB3, 0x6B, 0xFF, 255), Px(f, 7, 12));                                    // "a" 的右下角墨迹
        Assert.Equal((0, 0, 0, 255), Px(f, 5, 10));                                             // 描边:黑色不透明
        Assert.Equal((0, 0, 0, DropLabels.FillAlpha), Px(f, 6, 8));                             // 描边之上又是底
    }

    [Fact]
    public void ASelectedLabelGetsAYellowRingAFainterFillAndAnOpaqueBorder()
    {
        var labels = new DropLabels(Art());
        SpriteFrame plain = labels.Frame("ab", 0xFFFFFFu, false);
        SpriteFrame sel = labels.Frame("ab", 0xFFFFFFu, true);
        Assert.Equal((plain.Width, plain.Height), (sel.Width, sel.Height));                     // 对位不变
        Assert.NotSame(plain, sel);

        Assert.Equal(Rgba(DropLabels.SelectRgb, 255), Px(sel, 6, 0));                           // 上外框(盒子 (5,−1))
        Assert.Equal(Rgba(DropLabels.SelectRgb, 255), Px(sel, 1, 1));                           // 削掉的角上有外框(沿着削角走)
        Assert.Equal(Rgba(DropLabels.SelectRgb, 255), Px(sel, 0, 12));                          // 左外框
        Assert.Equal((0, 0, 0, 0), Px(sel, 0, 0));                                              // 外框的最外角不画
        Assert.Equal(Rgba(DropLabels.BorderRgb, 255), Px(sel, 6, 1));                           // 边框不透明
        Assert.Equal((0, 0, 0, DropLabels.SelectedFillAlpha), Px(sel, 13, 16));                 // 底更透明
        Assert.True(DropLabels.SelectedFillAlpha < DropLabels.FillAlpha);
        Assert.Equal((255, 255, 255, 255), Px(sel, 6, 10));                                     // 字照旧
    }

    [Fact]
    public void TheLabelWidthFollowsTheTextAndSameArgumentsGiveTheSameFrame()
    {
        var labels = new DropLabels(Art());
        SpriteFrame a = labels.Frame("ab", 0xFFFFFFu, false);
        SpriteFrame b = labels.Frame("abcdef", 0xFFFFFFu, false);
        Assert.Equal(a.Width + 12, b.Width);                                                    // 多四个窄字,每个前进 3
        Assert.Same(a, labels.Frame("ab", 0xFFFFFFu, false));
        Assert.NotSame(a, labels.Frame("ab", 0xB36BFFu, false));
        Assert.NotSame(a, labels.Frame("ab", 0xFFFFFFu, true));
    }

    [Fact]
    public void WithoutFontsOrTextThereIsNoFrame()
    {
        Assert.Null(new DropLabels(new OverlayArt()).Frame("ab", 0xFFFFFFu, false));
        var labels = new DropLabels(Art());
        Assert.Null(labels.Frame(null, 0xFFFFFFu, false));
        Assert.Null(labels.Frame("", 0xFFFFFFu, false));
        Assert.Throws<ArgumentNullException>(() => new DropLabels(null!));
    }

    // ------------------------------------------------------------------ 文字

    [Fact]
    public void TheTextIsTheItemNameOrTheGoldFormatFilledIn()
    {
        WorldTexts t = Texts();
        Assert.Equal("ab", DropLabels.TextOf(LandedItem(1, Named("name_a"), 300, 100), t));
        Assert.Equal("5% off", DropLabels.TextOf(LandedItem(2, Named("name_pct"), 300, 100), t));   // %% 还原成 %
        Assert.Null(DropLabels.TextOf(LandedItem(3, Named("name_c"), 300, 100), t));                // 空值当作没有
        Assert.Null(DropLabels.TextOf(LandedItem(4, Named("name_zzz"), 300, 100), t));
        Assert.Null(DropLabels.TextOf(LandedItem(5, StackItem(), 300, 100), t));                    // 物品没有名字引用
        Assert.Equal("1234 gold", DropLabels.TextOf(LandedGold(6, 1234, 300, 100), t));
        Assert.Null(DropLabels.TextOf(null, t));
        Assert.Null(DropLabels.TextOf(LandedGold(7, 5, 300, 100), null));
        Assert.Null(DropLabels.TextOf(LandedGold(8, 5, 300, 100), new WorldTexts(Library())));      // 文字表里没有金额格式
    }

    // ------------------------------------------------------------------ 摆放

    [Fact]
    public void ALabelIsCenteredOnTheSpriteAndItsBottomRowIsEightRowsAboveIt()
    {
        var labels = new DropLabels(Art());
        var projection = new TownProjection(50, 200);
        var sprites = new DropSprites();
        FieldDrop d = LandedItem(1, Named("name_a", rarity: 2), 300, 100);
        SceneSprite pile = d.Sprite(sprites, projection);
        var into = new List<SceneSprite>();
        int order = 7;
        Assert.Equal(1, labels.Add(into, ref order, new[] { d }, sprites, projection, null, Texts()));
        SceneSprite label = Assert.Single(into);
        Assert.Equal(8, order);
        Assert.Equal("drop-label", label.AnimationPath);
        int boxW = label.Frame.Width - 2;
        Assert.Equal(pile.ScreenX + pile.Frame.Width / 2, label.ScreenX + 1 + boxW / 2);          // 盒子中点 = 图中点
        Assert.Equal(pile.ScreenY - 8, label.ScreenY + 1 + 22);                                    // 盒子最下一行比图上沿高 8 行
        Assert.Same(labels.Frame("ab", 0xB36BFFu, false), label.Frame);
    }

    [Fact]
    public void TheSelectedOneIsTaggedAndDrawnAsSelected()
    {
        var labels = new DropLabels(Art());
        var projection = new TownProjection(50, 200);
        var sprites = new DropSprites();
        FieldDrop a = LandedItem(1, Named("name_a"), 300, 100);
        FieldDrop b = LandedItem(2, Named("name_b"), 340, 100);
        var into = new List<SceneSprite>();
        int order = 0;
        Assert.Equal(2, labels.Add(into, ref order, new[] { a, b }, sprites, projection, b, Texts()));
        Assert.Equal(2, order);
        SceneSprite first = into.Single(x => x.AnimationPath == "drop-label");
        SceneSprite second = into.Single(x => x.AnimationPath == "drop-label-selected");
        Assert.Same(labels.Frame("ab", 0xFFFFFFu, false), first.Frame);
        Assert.Same(labels.Frame("abcdef", 0xFFFFFFu, true), second.Frame);
    }

    [Fact]
    public void LabelsAreAddedFromTheBackToTheFrontSoTheLowerOnesCoverTheHigherOnes()
    {
        var labels = new DropLabels(Art());
        var projection = new TownProjection(0, 200);
        var sprites = new DropSprites();
        FieldDrop front = LandedItem(1, Named("name_b"), 300, 160);                              // 进深大(靠下):后画
        FieldDrop back = LandedItem(2, Named("name_a"), 300, 90);
        var into = new List<SceneSprite>();
        int order = 0;
        labels.Add(into, ref order, new[] { front, back }, sprites, projection, null, Texts());
        Assert.Equal(2, into.Count);
        Assert.Same(labels.Frame("ab", 0xFFFFFFu, false), into[0].Frame);
        Assert.Same(labels.Frame("abcdef", 0xFFFFFFu, false), into[1].Frame);
        Assert.True(into[0].LayerRank == into[1].LayerRank);
        Assert.True(into[0].SortKey <= into[1].SortKey);
    }

    [Fact]
    public void FlyingDropsDropsWithoutTextOrWithoutASpriteGetNoLabel()
    {
        var labels = new DropLabels(Art());
        var projection = new TownProjection(0, 200);
        var sprites = new DropSprites();
        FieldDrop flying = FieldDrop.LaunchItem(1, Named("name_a"), 300, 100, new DropRandom(1));      // 还在空中
        FieldDrop noName = LandedItem(2, StackItem(), 300, 100);
        ItemInfo noArtItem = Named("name_a");
        noArtItem.FieldImage = null;
        FieldDrop noArt = LandedItem(3, noArtItem, 300, 100);
        FieldDrop ok = LandedGold(4, 20, 300, 100);
        var into = new List<SceneSprite>();
        int order = 0;
        Assert.Equal(1, labels.Add(into, ref order, new[] { flying, noName, noArt, ok }, sprites, projection, null, Texts()));
        Assert.Single(into);
        Assert.Equal(1, order);
    }

    [Fact]
    public void NothingIsAddedWithoutFontsTextsDropsOrAList()
    {
        var projection = new TownProjection(0, 200);
        var sprites = new DropSprites();
        FieldDrop d = LandedItem(1, Named("name_a"), 300, 100);
        var into = new List<SceneSprite>();
        int order = 0;
        Assert.Equal(0, new DropLabels(new OverlayArt()).Add(into, ref order, new[] { d }, sprites, projection, null, Texts()));
        var labels = new DropLabels(Art());
        Assert.Equal(0, labels.Add(into, ref order, new[] { d }, sprites, projection, null, null));
        Assert.Equal(0, labels.Add(into, ref order, new FieldDrop[0], sprites, projection, null, Texts()));
        Assert.Equal(0, labels.Add(into, ref order, null, sprites, projection, null, Texts()));
        Assert.Equal(0, labels.Add(null, ref order, new[] { d }, sprites, projection, null, Texts()));
        Assert.Empty(into);
        Assert.Equal(0, order);
    }

    // ------------------------------------------------------------------ 文字表上的两个取法

    [Fact]
    public void WorldTextsGivesItemNamesAndTheGoldAmount()
    {
        WorldTexts t = Texts();
        Assert.Equal("ab", t.ItemName(Named("name_a")));
        Assert.Null(t.ItemName(null));
        Assert.Equal("0 gold", t.GoldAmount(0));
        Assert.Equal("1234567 gold", t.GoldAmount(1234567));
        Assert.Null(new WorldTexts(Library()).GoldAmount(5));
    }

    // ------------------------------------------------------------------ 战斗会话里

    [Fact]
    public void TheSessionDrawsLabelsOnlyWithFontsAndTextsAndMarksTheOneThePressWouldPick()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        s.UseDropArt(new DropSprites());
        s.Hero.Place(60, 100, false, HeroMotion.Rest, 0);
        FieldDrop item = s.SpawnItem(Named("name_a", rarity: 2), 300, 100);
        FieldDrop gold = s.SpawnGold(20, 330, 100);
        for (int i = 0; i < 300 && s.Drops.Any(d => !d.Landed); i++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.True(item.Landed && gold.Landed);

        // 没给字体与文字表:一块也不画
        Assert.DoesNotContain(s.Build(0), x => x.AnimationPath.StartsWith("drop-label"));
        s.UseDropLabels(new OverlayArt(), Texts());                                                // 有文字表没字体
        Assert.DoesNotContain(s.Build(0), x => x.AnimationPath.StartsWith("drop-label"));
        s.UseDropLabels(Art(), null);                                                              // 有字体没文字表
        Assert.DoesNotContain(s.Build(0), x => x.AnimationPath.StartsWith("drop-label"));

        // 都有:两块,主角够不着,谁也没选中
        s.UseDropLabels(Art(), Texts());
        s.Update(10, 0, 0, false);
        List<SceneSprite> scene = s.Build(0);
        Assert.Equal(2, scene.Count(x => x.AnimationPath == "drop-label"));
        Assert.DoesNotContain(scene, x => x.AnimationPath == "drop-label-selected");
        Assert.Null(s.SelectedDrop);
        Assert.Equal((2, 0), (s.DropLabelsDrawn, s.SelectedLabelFrames));                          // 检查用的计数

        // 走到物品旁:它的名牌变成选中的;金币的不会(金币走到就自动捡)
        s.Hero.Place(item.X, item.Y, false, HeroMotion.Rest, 0);
        s.Update(10, 0, 0, false);
        Assert.Same(item, s.SelectedDrop);
        scene = s.Build(0);
        SceneSprite selected = Assert.Single(scene, x => x.AnimationPath == "drop-label-selected");
        Assert.Same(item, s.SelectedDrop);
        Assert.Equal(1, s.SelectedLabelFrames);
        Assert.Equal(DropLabelsSelectedFrame("ab", DropLabels.ColorOf(item)).Width, selected.Frame.Width);

        // 按攻击键捡走之后名牌随东西一起没了
        s.Update(10, 0, 0, true);
        Assert.DoesNotContain(s.Build(0), x => x.AnimationPath == "drop-label-selected");
    }

    private static SpriteFrame DropLabelsSelectedFrame(string text, uint color) => new DropLabels(Art()).Frame(text, color, true);
}
