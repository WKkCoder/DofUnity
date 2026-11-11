using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using static Dnf.Render.Tests.WardrobeFixture;

namespace Dnf.Play.Tests;

/// <summary>
/// 背包装扮页的画法(r15-avatarhost,<see cref="BagAvatarArt"/>,本项目自己的占位版式)与它的文字:只看精灵的组成、位置与文字帧,不看像素。
/// 同一个 <see cref="OverlayArt"/> 对相同的文字、字号、颜色总返回同一个帧对象,所以"画的是这句话"用 <see cref="Assert.Same(object, object)"/> 比。
/// </summary>
public class BagAvatarArtTests
{
    private sealed class StubSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) => Frames.TryGetValue((imageName, frame), out sprite);
    }

    private static SpriteFrame Opaque(int w, int h, int offsetX = 0, int offsetY = 0, int canvasW = -1, int canvasH = -1)
    {
        byte[] rgba = new byte[w * h * 4];
        Array.Fill(rgba, (byte)255);
        return new SpriteFrame(w, h, offsetX, offsetY, canvasW < 0 ? w : canvasW, canvasH < 0 ? h : canvasH, rgba);
    }

    private static int Count(List<SceneSprite> sprites, string tag) => sprites.Count(s => s.AnimationPath == tag);

    private static SceneSprite One(List<SceneSprite> sprites, string tag) => sprites.Single(s => s.AnimationPath == tag);

    private static List<SceneSprite> Draw(AvatarHostRig h, BagWindow window, OverlayArt art, ISpriteSource sprites = null, ItemTexts texts = null)
    {
        return BagWindowArt.Build(window, h.Progress, sprites, art, default, texts);
    }

    private static bool HasInk(SpriteFrame frame, uint rgb)
    {
        for (int i = 0; i + 3 < frame.Rgba.Length; i += 4)
        {
            if (frame.Rgba[i + 3] != 0 && frame.Rgba[i] == (byte)(rgb >> 16) && frame.Rgba[i + 1] == (byte)(rgb >> 8) && frame.Rgba[i + 2] == (byte)rgb)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>等级 20 的主角、背包装备页里:可穿的帽子(901)与上衣(904)、别的职业的帽子(915)、等级不够的帽子(916)、规则放行但画面上穿不出来的上衣(913),外加一件普通装备(941)。</summary>
    private sealed record Stock(ItemInfo Hat, ItemInfo Coat, ItemInfo Foreign, ItemInfo High, ItemInfo NoImage, ItemInfo Ring);

    private static Stock Fill(AvatarHostRig h)
    {
        var stock = new Stock(
            h.Avatar(0),
            h.Avatar(3),
            h.Make(AvatarHostRig.CodeWrongClassHat, HatOtherJob, 0, job: "[fighter]"),
            h.Make(AvatarHostRig.CodeHighLevelHat, HatOtherJob, 0, level: 50),
            h.Make(AvatarHostRig.CodeCoatNoImage, CoatNoImage, 3),
            h.Gear(AvatarHostRig.CodeGearRing, EquipmentParts.Ring));
        h.Give(stock.Hat, stock.Coat, stock.Foreign, stock.High, stock.NoImage, stock.Ring);
        return stock;
    }

    // ---------------------------------------------------------------- 版面

    [Fact]
    public void TheAvatarPageDrawsTheBarTheGridAndTheCaptionsAndNothingOfTheItemsPage()
    {
        using var h = new AvatarHostRig(level: 20);
        Fill(h);
        BagWindow window = h.OpenAvatarWindow();
        var art = new OverlayArt();
        List<SceneSprite> sprites = Draw(h, window, art);

        // 各块的底色与位置:格子、装扮栏、页签位置上的"页签"与两个标题
        (int cx, int cy, int cw, int ch) = BagWindow.CellRect(0);
        SceneSprite cell = sprites.First(s => s.AnimationPath == "avatar-cell");
        Assert.Same(art.Solid(cw, ch, BagWindowArt.CellColor), cell.Frame);
        Assert.Equal((cx, cy), (cell.ScreenX, cell.ScreenY));
        (int sx, int sy, int sw, int sh) = BagWindow.SlotRect(0);
        SceneSprite slot = sprites.First(s => s.AnimationPath == "avatar-slot");
        Assert.Same(art.Solid(sw, sh, BagWindowArt.SlotColor), slot.Frame);
        Assert.Equal((sx, sy), (slot.ScreenX, slot.ScreenY));
        (int tx, int ty, int tw, int th) = BagWindow.TabRect(0);
        SceneSprite tab = One(sprites, "avatar-tab-selected");
        Assert.Same(art.Solid(tw, th, BagWindowArt.TabSelectedColor), tab.Frame);
        Assert.Equal((tx, ty), (tab.ScreenX, tab.ScreenY));
        SceneSprite tabLabel = One(sprites, "avatar-tab-label");
        Assert.Same(art.Text(BagAvatarArt.CaptionText, 2, OverlayArt.Yellow), tabLabel.Frame);
        Assert.Equal((tx + (tw - tabLabel.Frame.Width) / 2, ty + (th - tabLabel.Frame.Height) / 2), (tabLabel.ScreenX, tabLabel.ScreenY));
        SceneSprite caption = One(sprites, "avatar-caption");
        Assert.Same(art.Text(BagAvatarArt.CaptionText, 2, OverlayArt.White), caption.Frame);
        Assert.Equal((BagWindow.SlotX, BagWindow.CaptionY), (caption.ScreenX, caption.ScreenY));
        // 空着的装扮格写部位缩写(第 0 格穿着的是空的,每格都写)
        Assert.Same(art.Text("HAT", 1, BagWindowArt.DimText), sprites.First(s => s.AnimationPath == "avatar-slot-label").Frame);
        Assert.Same(art.Text("AURA", 1, BagWindowArt.DimText), sprites.Last(s => s.AnimationPath == "avatar-slot-label").Frame);

        foreach (string tag in new[]
        {
            "bag-dim", "bag-frame", "bag-panel", "bag-title-bar", "bag-title", "bag-gold", "bag-page-button-selected", "bag-page-label",
            "avatar-caption", "avatar-tab-selected", "avatar-tab-label", "avatar-cell-selected", "avatar-info", "avatar-message", "avatar-hint",
        })
        {
            Assert.Equal(1, Count(sprites, tag));
        }
        Assert.Equal((10, 10, 3, 56), (Count(sprites, "avatar-slot"), Count(sprites, "avatar-slot-label"), Count(sprites, "avatar-stat"), Count(sprites, "avatar-cell")));
        Assert.Equal((5, 5, 2), (Count(sprites, "bag-icon-block"), Count(sprites, "avatar-rarity"), Count(sprites, "avatar-icon-dim")));       // 五件时装,普通装备不画
        foreach (string tag in new[]
        {
            "bag-slot", "bag-slot-label", "bag-cell", "bag-cell-selected", "bag-tab", "bag-tab-selected", "bag-tab-label", "bag-caption", "bag-stat", "bag-info", "bag-message", "bag-hint",
            "bag-page-button", "avatar-slot-selected", "avatar-slot-icon", "avatar-slot-rarity", "avatar-icon",
        })
        {
            Assert.Equal(0, Count(sprites, tag));
        }
        Assert.Equal(sprites.Select((s, i) => i).ToList(), sprites.Select(s => s.SortKey).ToList());       // 画的次序就是排序键
        Assert.All(sprites, s => Assert.NotNull(s.Frame));
    }

    [Fact]
    public void TheItemsPageIsUnchangedByWiringInTheAvatarsExceptForThePageButton()
    {
        using var h = new AvatarHostRig(level: 20);
        Fill(h);
        List<SceneSprite> with = Draw(h, h.OpenWindow(), new OverlayArt());

        var plain = new BagWindow();
        plain.Show(GameStage.Town);
        List<SceneSprite> without = Draw(h, plain, new OverlayArt());
        Assert.Equal(without.Select(s => s.AnimationPath), with.Where(s => !s.AnimationPath.StartsWith("bag-page-")).Select(s => s.AnimationPath));
        Assert.DoesNotContain(with, s => s.AnimationPath.StartsWith("avatar-"));
        Assert.Equal((1, 1, 0), (Count(with, "bag-page-button"), Count(with, "bag-page-label"), Count(with, "bag-page-button-selected")));
        Assert.Equal(0, Count(without, "bag-page-button") + Count(without, "bag-page-label"));              // 没接装扮页:没有按钮
    }

    [Fact]
    public void TheCursorMarkFollowsTheCursorBetweenTheGridAndTheBar()
    {
        using var h = new AvatarHostRig(level: 20);
        Fill(h);
        BagWindow window = h.OpenAvatarWindow();
        var art = new OverlayArt();
        List<SceneSprite> onGrid = Draw(h, window, art);
        (int cx, int cy, int cw, int ch) = BagWindow.CellRect(0);
        Assert.Equal((cx - 2, cy - 2), (One(onGrid, "avatar-cell-selected").ScreenX, One(onGrid, "avatar-cell-selected").ScreenY));
        Assert.Same(art.Solid(cw + 4, ch + 4, BagWindowArt.CursorColor), One(onGrid, "avatar-cell-selected").Frame);
        Assert.Equal(0, Count(onGrid, "avatar-slot-selected"));

        AvatarHostRig.SelectSlot(window, 7);
        List<SceneSprite> onBar = Draw(h, window, art);
        (int sx, int sy, int sw, int sh) = BagWindow.SlotRect(7);
        Assert.Equal((sx - 2, sy - 2), (One(onBar, "avatar-slot-selected").ScreenX, One(onBar, "avatar-slot-selected").ScreenY));
        Assert.Same(art.Solid(sw + 4, sh + 4, BagWindowArt.CursorColor), One(onBar, "avatar-slot-selected").Frame);
        Assert.Equal(0, Count(onBar, "avatar-cell-selected"));
        Assert.Equal((1, 0), (Count(onBar, "avatar-info"), Count(onBar, "avatar-message")));               // 空装扮格:写 EMPTY SLOT,没有"穿不穿得了"
    }

    [Fact]
    public void OnlyWhatTheRuleLayerSaysCannotBeWornIsDimmedAndTheStatsAreItsNumbers()
    {
        using var h = new AvatarHostRig(level: 20);
        Fill(h);
        BagWindow window = h.OpenAvatarWindow();
        var art = new OverlayArt();
        List<SceneSprite> sprites = Draw(h, window, art);

        // 格 0 帽子、1 上衣可穿;2 职业不符、3 等级不够:暗;4 规则放行(只是画面上穿不出来):不暗,这是规则层的结论
        var dims = sprites.Where(s => s.AnimationPath == "avatar-icon-dim").ToList();
        Assert.Equal(new[] { (BagWindow.CellRect(2).X, BagWindow.CellRect(2).Y), (BagWindow.CellRect(3).X, BagWindow.CellRect(3).Y) }, dims.Select(s => (s.ScreenX, s.ScreenY)));
        Assert.All(dims, s => Assert.Equal(BagWindowArt.UnusableAlpha, s.TintAlpha));
        Assert.All(dims, s => Assert.Same(art.Solid(BagWindow.CellRect(2).W, BagWindow.CellRect(2).H, OverlayArt.Black), s.Frame));
        Assert.Equal((0, 5, 3), (h.Session.Slots.WornCount, window.Avatars.View.Avatars.Count, window.Avatars.View.WearableCount));

        var stats = sprites.Where(s => s.AnimationPath == "avatar-stat").ToList();
        string[] lines = BagAvatarArt.StatLines(0, 5, 3);
        Assert.Equal(new[] { "WORN 0/10", "IN BAG 5", "USABLE 3" }, lines);
        for (int i = 0; i < lines.Length; i++)
        {
            Assert.Same(art.Text(lines[i], 2, OverlayArt.White), stats[i].Frame);
            Assert.Equal((BagWindow.SlotX, BagWindowArt.StatsY + 20 * i), (stats[i].ScreenX, stats[i].ScreenY));
        }
        Assert.Equal(new[] { "WORN 3/10", "IN BAG 12", "USABLE 7" }, BagAvatarArt.StatLines(3, 12, 7));
    }

    [Fact]
    public void WornItemsFillTheirSlotsAndIconsComeFromTheItemScriptsImageCenteredInTheCell()
    {
        using var h = new AvatarHostRig(level: 20);
        Stock stock = Fill(h);
        h.WearOk(stock.Hat.Code);
        stock.Hat.Icon = "Item/Icons.img";
        stock.Hat.IconFrame = 7;
        stock.Coat.Icon = "Item/Icons.img";
        stock.Coat.IconFrame = 8;
        var sprites = new StubSprites();
        sprites.Frames[("sprite/item/icons.img", 7)] = Opaque(20, 20, offsetX: 1, offsetY: -1, canvasW: 24, canvasH: 28);
        sprites.Frames[("sprite/item/icons.img", 8)] = Opaque(16, 16);
        BagWindow window = h.OpenAvatarWindow();

        List<SceneSprite> drawn = Draw(h, window, new OverlayArt(), sprites);
        Assert.Equal((9, 1, 1), (Count(drawn, "avatar-slot-label"), Count(drawn, "avatar-slot-icon"), Count(drawn, "avatar-slot-rarity")));       // 帽子穿在 0 号格
        (int sx, int sy, int sw, int sh) = BagWindow.SlotRect(0);
        SceneSprite icon = One(drawn, "avatar-slot-icon");
        Assert.Equal((sx + (sw - 24) / 2 + 1, sy + (sh - 28) / 2 - 1), (icon.ScreenX, icon.ScreenY));

        // 帽子穿走之后背包里是 上衣、职业不符、等级不够、没外观:上衣有图标,另外三件是色块
        Assert.Equal((1, 3, 4), (Count(drawn, "avatar-icon"), Count(drawn, "bag-icon-block"), Count(drawn, "avatar-rarity")));
        (int cx, int cy, int cw, int ch) = BagWindow.CellRect(0);
        SceneSprite coatIcon = One(drawn, "avatar-icon");
        Assert.Equal((cx + (cw - 16) / 2, cy + (ch - 16) / 2), (coatIcon.ScreenX, coatIcon.ScreenY));

        List<SceneSprite> blocks = Draw(h, window, new OverlayArt(), null);        // 没有图标来源:穿着的和背包里的全是色块
        Assert.Equal((0, 0, 5), (Count(blocks, "avatar-slot-icon"), Count(blocks, "avatar-icon"), Count(blocks, "bag-icon-block")));

        sprites.Frames[("sprite/item/icons.img", 8)] = new SpriteFrame(1, 1, 0, 0, 1, 1, new byte[4]);               // 透明的占位帧:退回色块
        Assert.Equal((0, 4), (Count(Draw(h, window, new OverlayArt(), sprites), "avatar-icon"), Count(Draw(h, window, new OverlayArt(), sprites), "bag-icon-block")));
    }

    [Fact]
    public void TheDrawRebuildsTheViewSoABagChangeShowsWithoutAnyKey()
    {
        using var h = new AvatarHostRig(level: 20);
        Fill(h);
        BagWindow window = h.OpenAvatarWindow();
        Assert.Equal(5, Count(Draw(h, window, new OverlayArt()), "avatar-rarity"));
        h.Give(h.Avatar(5));                                                       // 页面没收到任何按键,背包里多了一件
        Assert.Equal(6, Count(Draw(h, window, new OverlayArt()), "avatar-rarity"));
        h.Progress.Bag.RemoveAt(InventoryTab.Equipment, 0, 1);
        Assert.Equal(5, Count(Draw(h, window, new OverlayArt()), "avatar-rarity"));
    }

    // ---------------------------------------------------------------- 切页按钮

    [Fact]
    public void ThePageButtonIsDrawnOnBothPagesWithItsOwnLabelAndState()
    {
        using var h = new AvatarHostRig();
        var art = new OverlayArt();
        BagWindow window = h.OpenWindow();
        List<SceneSprite> items = Draw(h, window, art);
        (int bx, int by, int bw, int bh) = BagWindow.PageButtonRect;
        SceneSprite button = One(items, "bag-page-button");
        Assert.Equal((bx, by, bw, bh), (button.ScreenX, button.ScreenY, button.Frame.Width, button.Frame.Height));
        SceneSprite label = One(items, "bag-page-label");
        Assert.Same(art.Text("V AVATAR", 2, OverlayArt.White), label.Frame);
        Assert.Equal((bx + (bw - label.Frame.Width) / 2, by + (bh - label.Frame.Height) / 2), (label.ScreenX, label.ScreenY));
        Assert.Equal(0, Count(items, "bag-page-button-selected"));

        Assert.True(window.TogglePage());
        List<SceneSprite> avatars = Draw(h, window, art);
        SceneSprite selected = One(avatars, "bag-page-button-selected");
        Assert.Equal((bx, by, bw, bh), (selected.ScreenX, selected.ScreenY, selected.Frame.Width, selected.Frame.Height));
        Assert.Equal(0, Count(avatars, "bag-page-button"));
        Assert.Same(art.Text("V ITEMS", 2, OverlayArt.Yellow), One(avatars, "bag-page-label").Frame);

        Assert.Equal(("V AVATAR", "V ITEMS"), (BagAvatarArt.PageButtonLabel(BagPage.Items), BagAvatarArt.PageButtonLabel(BagPage.Avatars)));
        Assert.Same(art.Solid(bw, bh, BagWindowArt.TabColor), button.Frame);           // 物品页上是普通页签的底色,装扮页上是选中页签的底色
        Assert.Same(art.Solid(bw, bh, BagWindowArt.TabSelectedColor), selected.Frame);
    }

    // ---------------------------------------------------------------- 说明文字

    [Fact]
    public void TheDetailLinesAreTheRuleLayersAnswersInWords()
    {
        using var h = new AvatarHostRig(level: 20);
        Stock stock = Fill(h);
        stock.Hat.Grade = 10;
        stock.Hat.Rarity = 2;
        stock.Hat.Price = 300;
        BagWindow window = h.OpenAvatarWindow();
        BagAvatarPage page = window.Avatars;

        Assert.Equal("LV 1  GRADE 10  RARITY 2  PRICE 300", BagAvatarArt.InfoText(page, stock.Hat));
        Assert.Equal(string.Empty, BagAvatarArt.InfoText(page, null));             // 格子上没东西:空
        Assert.Equal("CAN WEAR", BagAvatarArt.FitText(page));                      // 格 0 是帽子:规则放行、画面上也穿得出来

        string[] fit = new string[6];
        for (int cell = 0; cell < 6; cell++)
        {
            AvatarHostRig.SelectCell(window, cell);
            fit[cell] = BagAvatarArt.FitText(page);
        }
        Assert.Equal(new[] { "CAN WEAR", "CAN WEAR", "WRONG CLASS", "NEED LEVEL 50", "NO ANIMATION FOR THIS", string.Empty }, fit);       // 第 4 格是规则放行、画面穿不出来的上衣;第 5 格是空的

        AvatarHostRig.SelectSlot(window, 3);
        Assert.Equal(string.Empty, BagAvatarArt.FitText(page));                    // 装扮栏上不说穿不穿得了
        Assert.Equal("EMPTY SLOT", BagAvatarArt.InfoText(page, page.SelectedItem));
    }

    [Fact]
    public void TheMessageLineIsOrangeForTheFitHintAndYellowForAnOperationResult()
    {
        using var h = new AvatarHostRig(level: 20);
        Fill(h);
        BagWindow window = h.OpenAvatarWindow();
        var art = new OverlayArt();
        SceneSprite fit = One(Draw(h, window, art), "avatar-message");             // 没操作过:"CAN WEAR",橙字
        Assert.Same(art.Text("CAN WEAR", 2, OverlayArt.Orange), fit.Frame);
        Assert.True(HasInk(fit.Frame, OverlayArt.Orange));
        Assert.False(HasInk(fit.Frame, OverlayArt.Yellow));
        Assert.Equal((BagWindow.SlotX, BagWindowArt.MessageY), (fit.ScreenX, fit.ScreenY));

        Assert.True(window.Activate(h.Progress));
        SceneSprite message = One(Draw(h, window, art), "avatar-message");         // 操作结果是黄字,盖过说明
        Assert.Same(art.Text("WORN", 2, OverlayArt.Yellow), message.Frame);
        Assert.True(HasInk(message.Frame, OverlayArt.Yellow));

        AvatarHostRig.SelectSlot(window, 5);
        Assert.False(window.Activate(h.Progress));
        Assert.Same(art.Text("EMPTY SLOT", 2, OverlayArt.Yellow), One(Draw(h, window, art), "avatar-message").Frame);
        Assert.Same(art.Text(BagAvatarArt.HintText, 1, BagWindowArt.DimText), One(Draw(h, window, art), "avatar-hint").Frame);
    }

    // ---------------------------------------------------------------- 字体与文字表

    [Fact]
    public void FontsAndTextsAddTheItemNameAndExplainOnTheAvatarPageToo()
    {
        using var h = new AvatarHostRig(level: 20);
        Stock stock = Fill(h);
        stock.Hat.NameKey = "name_1";
        stock.Hat.ExplainKey = "explain_1";
        stock.Hat.TextKind = StrLibrary.KindEquipment;
        var texts = new ItemTexts(ItemTextsTests.Library());
        BagWindow window = h.OpenAvatarWindow();

        List<SceneSprite> with = Draw(h, window, new OverlayArt { Fonts = new BlockFonts() }, null, texts);
        Assert.Equal((1, 1, 0, 0), (Count(with, "avatar-item-name"), Count(with, "avatar-item-explain"), Count(with, "bag-item-name"), Count(with, "bag-item-explain")));
        Assert.Equal(BagWindow.SlotX, One(with, "avatar-item-name").ScreenX);

        Assert.Equal(0, Count(Draw(h, window, new OverlayArt { Fonts = new BlockFonts() }), "avatar-item-name"));       // 没给文字表
        Assert.Equal(0, Count(Draw(h, window, new OverlayArt(), null, texts), "avatar-item-name"));                      // 没装字体

        h.WearOk(stock.Hat.Code);                                                  // 光标在装扮栏上、指着穿着的那件:也写它的名字
        AvatarHostRig.SelectSlot(window, 0);
        Assert.Equal(1, Count(Draw(h, window, new OverlayArt { Fonts = new BlockFonts() }, null, texts), "avatar-item-name"));
    }

    [Fact]
    public void ThePageDrawsWithFontsLocalizedAndEveryFrameIsThere()
    {
        using var h = new AvatarHostRig(level: 20);
        Fill(h);
        var fonts = new BlockFonts();
        BagWindow window = h.OpenAvatarWindow();
        List<SceneSprite> sprites = Draw(h, window, new OverlayArt { Fonts = fonts });
        Assert.All(sprites, s => Assert.NotNull(s.Frame));
        Assert.NotEmpty(fonts.Requested);                                          // 占位英文走了中文字体
        Assert.Equal(5, Count(sprites, "avatar-rarity"));

        var plain = new OverlayArt();
        Assert.Equal(Count(Draw(h, window, plain), "avatar-slot"), Count(sprites, "avatar-slot"));
    }

    [Theory]
    [InlineData("AVATAR", "时装")]
    [InlineData("V AVATAR", "V 时装")]
    [InlineData("V ITEMS", "V 物品")]
    [InlineData("HAT", "帽子")]
    [InlineData("HAIR", "头发")]
    [InlineData("FACE", "脸")]
    [InlineData("COAT", "上衣")]
    [InlineData("PANT", "下装")]
    [InlineData("SHOE", "鞋")]
    [InlineData("BRST", "胸饰")]
    [InlineData("WAIST", "腰饰")]
    [InlineData("SKIN", "皮肤")]
    [InlineData("AURA", "灵气")]
    [InlineData("WORN", "已穿上")]
    [InlineData("TAKEN OFF", "已脱下")]
    [InlineData("CAN WEAR", "可以穿")]
    [InlineData("EMPTY SLOT", "空槽位")]
    [InlineData("BAG FULL", "背包已满")]
    [InlineData("AVATARS UNAVAILABLE", "时装功能不可用")]
    [InlineData("AVATARS: TOWN ONLY", "时装只能在城镇里换")]
    [InlineData("WORN 3/10", "已穿 3/10")]
    [InlineData("IN BAG 12", "背包里 12")]
    [InlineData("USABLE 7", "可穿 7")]
    [InlineData("NEED LEVEL 50", "需要等级 50")]
    [InlineData("ARROWS MOVE   ENTER WEAR OR TAKE OFF   V ITEMS   I CLOSE", "方向键 移动   回车 穿上或脱下   V 切回物品   I 关闭")]
    public void TheAvatarPageTextsHaveTheirChineseVersions(string english, string chinese)
    {
        Assert.Equal(chinese, UiText.Translate(english));
    }

    [Fact]
    public void EveryTextTheAvatarPageProducesHasAChineseVersionAndTheDotFontCanDrawIt()
    {
        var texts = new List<string> { BagAvatarArt.CaptionText, BagAvatarArt.HintText, BagAvatarArt.PageButtonLabel(BagPage.Items), BagAvatarArt.PageButtonLabel(BagPage.Avatars) };
        texts.AddRange(Enumerable.Range(AvatarSlots.First, AvatarSlots.Count).Select(BagAvatarPage.SlotLabel));
        foreach (EquipResult result in Enum.GetValues(typeof(EquipResult)))
        {
            texts.Add(BagAvatarPage.MessageOf(result));
        }
        texts.AddRange(new[] { "EMPTY SLOT", "TAKEN OFF", "BAG FULL", "CAN WEAR", "AVATARS UNAVAILABLE", "AVATARS: TOWN ONLY", "NEED LEVEL 50", "LV 1  GRADE 10  RARITY 2  PRICE 300" });
        texts.AddRange(BagAvatarArt.StatLines(3, 12, 7));

        var missing = new List<string>();
        foreach (string text in texts.Where(t => !string.IsNullOrEmpty(t)))
        {
            if (!UiText.HasTranslation(text))
            {
                missing.Add("没有中文版:" + text);
            }
            foreach (char c in text.Where(c => !OverlayArt.HasGlyph(c)))
            {
                missing.Add("点阵字库没有 '" + c + "':" + text);
            }
        }
        Assert.True(missing.Count == 0, string.Join(" / ", missing));
    }
}
