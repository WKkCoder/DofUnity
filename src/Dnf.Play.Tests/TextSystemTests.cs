using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// M29 文字系统(证据 E-121):占位英文的中文版(<see cref="UiText"/>)、叠加层的字体路径(<see cref="OverlayArt"/>)、
/// 物品文字(<see cref="ItemTexts"/>、<see cref="ItemDetailArt"/>)与背包、商店窗口的接线。
/// 字体是合成的方块字体、字符串表是虚构的短文本,不含任何原版内容。
/// </summary>
public class UiTextTests
{
    [Theory]
    [InlineData("INVENTORY", "背包")]
    [InlineData("COOLING DOWN", "冷却中")]
    [InlineData("PRESS X TO REVIVE", "按 X 键复活")]
    [InlineData("TOWN", "返回城镇")]
    [InlineData("BUY", "买入")]
    public void ExactPhrasesTranslate(string english, string chinese)
    {
        Assert.Equal(chinese, UiText.Translate(english));
        Assert.True(UiText.HasTranslation(english));
    }

    [Theory]
    [InlineData("GOLD 123", "金币 123")]
    [InlineData("NEED LEVEL 7", "需要等级 7")]
    [InlineData("LEVEL 12", "等级 12")]
    [InlineData("LV 9", "等级 9")]
    [InlineData("LV 5  GRADE 2  RARITY 3  PRICE 400", "等级 5  品级 2  稀有度 3  价格 400")]
    [InlineData("BOUGHT 3 FOR 120", "买入 3 件,花费 120 金币")]
    [InlineData("SOLD 2 FOR 80", "卖出 2 件,获得 80 金币")]
    [InlineData("UNIT 10  COST 50", "单价 10  花费 50")]
    [InlineData("UNIT 10  GET 50", "单价 10  获得 50")]
    [InlineData("CLEAR TIME 01:23", "通关用时 01:23")]
    [InlineData("EXP +300", "经验 +300")]
    [InlineData("GOLD +45", "金币 +45")]
    [InlineData("MAX 99", "最多 99")]
    [InlineData("PAGE 2", "第 2 页")]
    [InlineData("COINS 3", "复活币 3")]
    [InlineData("ATK P +120", "物理攻击 +120")]
    [InlineData("ATK M +80", "魔法攻击 +80")]
    public void PatternPhrasesCarryTheirNumbersAcross(string english, string chinese)
    {
        Assert.Equal(chinese, UiText.Translate(english));
    }

    [Fact]
    public void ASubstitutedValueIsNeverSubstitutedAgain()
    {
        Assert.Equal("单价 {1}  花费 x", UiText.Translate("UNIT {1}  COST x"));
    }

    [Fact]
    public void AnExactPhraseBeatsAPatternThatWouldAlsoMatch()
    {
        Assert.Equal("买入几件", UiText.Translate("BUY HOW MANY"));     // 不是 买入价 HOW MANY
        Assert.Equal("卖出几件", UiText.Translate("SELL HOW MANY"));
        Assert.Equal("最大", UiText.Translate("MAX"));                  // 不是 最多 …
        Assert.Equal("等级不足", UiText.Translate("LEVEL TOO LOW"));      // 不是 等级 TOO LOW
    }

    [Fact]
    public void TheLongerInfoLineIsNotEatenByTheShortLvPattern()
    {
        Assert.StartsWith("等级 1  品级", UiText.Translate("LV 1  GRADE 1  RARITY 1  PRICE 1"));
    }

    [Fact]
    public void UnknownTextIsReturnedUnchanged()
    {
        foreach (string text in new[] { "HELLO", "12345", "x", "测试", "GOLD", "GOLD ", "LV ", "EXP +" })
        {
            Assert.Same(text, UiText.Translate(text));
            Assert.False(UiText.HasTranslation(text));
        }
        Assert.Null(UiText.Translate(null));
        Assert.Equal(string.Empty, UiText.Translate(string.Empty));
    }

    [Fact]
    public void TablesAreNotEmptyAndEveryChineseSideIsNonEmpty()
    {
        Assert.True(UiText.ExactCount > 60);
        Assert.True(UiText.PatternCount > 15);
    }

    [Fact]
    public void EveryPlaceholderTheWindowsProduceHasATranslation()
    {
        var missing = new List<string>();
        void Check(string text)
        {
            if (!string.IsNullOrEmpty(text) && !UiText.HasTranslation(text))
            {
                missing.Add(text);
            }
        }

        foreach (InventoryTab tab in Enum.GetValues(typeof(InventoryTab)))
        {
            Check(BagWindow.TabLabel(tab));
        }
        for (int i = 0; i < EquipmentSlots.Count; i++)
        {
            Check(BagWindow.SlotLabel(EquipmentSlots.First + i));
        }
        foreach (EquipResult r in Enum.GetValues(typeof(EquipResult)))
        {
            Check(BagWindow.MessageOf(r));
        }
        foreach (UseResult r in Enum.GetValues(typeof(UseResult)))
        {
            Check(BagWindow.MessageOf(r));
        }
        foreach (ShopResult r in Enum.GetValues(typeof(ShopResult)))
        {
            Check(ShopRules.MessageOf(r));
        }
        foreach (SystemMenuAction a in Enum.GetValues(typeof(SystemMenuAction)))
        {
            Check(SystemMenu.LabelOf(a));
        }
        foreach (CoinVerdict v in Enum.GetValues(typeof(CoinVerdict)))
        {
            Check(ReviveArt.PromptOf(v));
        }
        Check(ShopWindowArt.TitleText(null));
        Check(ShopWindowArt.TitleText(new ItemShopDef(1, "p", 1, "[weapon shop]", null, new ItemShopTab[0])));
        Check(ShopWindowArt.TitleText(new ItemShopDef(1, "p", 1, "[etc shop]", null, new ItemShopTab[0])));
        Check(ShopWindowArt.TabLabel(ShopMode.Buy, 0));
        Check(ShopWindowArt.TabLabel(ShopMode.Sell, 2));
        Check(BagWindowArt.HintText);
        Check("GOLD 10");
        Check("SPACE SHOP");
        Check("EMPTY SLOT");
        Check("CAN EQUIP");
        Check("CAN USE");
        Check("UNEQUIPPED");
        Check("BAG FULL");

        // 任务窗口、提示与 NPC 提示条(M31)
        foreach (QuestActionResult r in Enum.GetValues(typeof(QuestActionResult)))
        {
            Check(QuestWindow.MessageOf(r, "QUEST ACCEPTED"));
            Check(QuestWindow.MessageOf(r, "QUEST COMPLETE"));
            Check(QuestWindow.MessageOf(r, "QUEST ABANDONED"));
        }
        foreach (QuestButton b in Enum.GetValues(typeof(QuestButton)))
        {
            Check(QuestWindowArt.ButtonText(b));
        }
        foreach (string text in new[]
        {
            "QUESTS", "NO QUESTS", "SPACE TALK", "REWARD", "CHOOSE ONE", "PROGRESS 2/5", "EXP 10  GOLD 5", "LV 5", "QUEST", "QUEST READY", "QUEST ITEM", "PRESS ABANDON AGAIN",
            QuestHudArt.TrackerTitle,
        })
        {
            Check(text);
        }
        Check(QuestWindowArt.HintText(QuestWindowMode.Journal, false));
        Check(QuestWindowArt.HintText(QuestWindowMode.Npc, false));
        Check(QuestWindowArt.HintText(QuestWindowMode.Npc, true));
        Check(QuestHudArt.ToastTitle(QuestNoticeKind.Accepted));

        Assert.True(missing.Count == 0, "没有中文版的占位文字:" + string.Join(" / ", missing));
    }
}

public class OverlayArtGlyphTests
{
    private const string FiveWide = "一二三四五";

    private static (OverlayArt Art, BlockFonts Fonts) WithFonts()
    {
        var fonts = new BlockFonts();
        return (new OverlayArt { Fonts = fonts }, fonts);
    }

    [Fact]
    public void WithoutFontsChineseTextStaysOnTheDotFont()
    {
        var art = new OverlayArt();
        SpriteFrame s = art.Text("背包", 2);
        Assert.False(art.HasFonts);
        Assert.Equal(OverlayArt.TextWidth("背包", 2), s.Width);
        Assert.Equal(OverlayArt.TextHeight(2), s.Height);
    }

    [Fact]
    public void WithoutFontsPlaceholderEnglishIsNotTranslated()
    {
        var art = new OverlayArt();
        SpriteFrame s = art.Text("INVENTORY", 2);
        Assert.Equal(OverlayArt.TextWidth("INVENTORY", 2), s.Width);
    }

    [Fact]
    public void TextTheDotFontCanDrawStaysOnTheDotFontEvenWithFonts()
    {
        (OverlayArt art, BlockFonts fonts) = WithFonts();
        SpriteFrame s = art.Text("HP 100/200", 2);
        Assert.Equal(OverlayArt.TextHeight(2), s.Height);
        Assert.Equal(OverlayArt.TextWidth("HP 100/200", 2), s.Width);
        Assert.Empty(fonts.Requested);
        Assert.Equal(OverlayArt.TextHeight(3), art.Text("12345", 3).Height);
    }

    [Fact]
    public void ChineseTextUsesTheGlyphFont()
    {
        (OverlayArt art, BlockFonts fonts) = WithFonts();
        SpriteFrame s = art.Text("背包", 2);
        // 方块字体:全角字前进 4,行距 4,加描边 2
        Assert.Equal((10, 6), (s.Width, s.Height));
        Assert.Equal(new[] { 16 }, fonts.Requested);
    }

    [Fact]
    public void PlaceholderEnglishIsTranslatedWhenFontsAreInstalled()
    {
        (OverlayArt art, BlockFonts _) = WithFonts();
        SpriteFrame english = art.Text("INVENTORY", 3);
        SpriteFrame chinese = art.Text("背包", 3);
        Assert.Same(chinese, english);
    }

    [Fact]
    public void LocalizeOffKeepsThePlaceholderEnglish()
    {
        (OverlayArt art, BlockFonts fonts) = WithFonts();
        art.Localize = false;
        SpriteFrame s = art.Text("INVENTORY", 2);
        Assert.Equal(OverlayArt.TextHeight(2), s.Height);
        Assert.Empty(fonts.Requested);
    }

    [Fact]
    public void CharactersTheDotFontLacksSendTheTextToTheFont()
    {
        (OverlayArt art, BlockFonts fonts) = WithFonts();
        SpriteFrame lower = art.Text("abc", 2);
        Assert.NotEqual(OverlayArt.TextHeight(2), lower.Height);
        Assert.Single(fonts.Requested);
        SpriteFrame multi = art.Text("A\nB", 2);
        Assert.True(multi.Height > lower.Height);
    }

    [Fact]
    public void ScaleMapsToAFontSizeInThePixelRange()
    {
        (OverlayArt art, BlockFonts fonts) = WithFonts();
        foreach (int scale in new[] { 1, 2, 3, 8, 20 })
        {
            art.Text("中", scale);
        }
        Assert.Equal(new[] { 12, 16, 20, 40, 64 }, fonts.Requested);
        Assert.Equal(12, OverlayArt.GlyphPixelSize(0));
        Assert.Equal(12, OverlayArt.GlyphPixelSize(-3));
        Assert.Equal(64, OverlayArt.GlyphPixelSize(1000));
    }

    [Fact]
    public void TheSameRequestReturnsTheSameObjectAndColorsAreDistinct()
    {
        (OverlayArt art, BlockFonts _) = WithFonts();
        SpriteFrame a = art.Text("中", 2, OverlayArt.Yellow);
        Assert.Same(a, art.Text("中", 2, OverlayArt.Yellow));
        Assert.NotSame(a, art.Text("中", 2, OverlayArt.White));
        Assert.NotSame(a, art.Text("中", 3, OverlayArt.Yellow));
    }

    [Fact]
    public void TheGlyphFrameUsesTheRequestedFillAndOutline()
    {
        (OverlayArt art, BlockFonts _) = WithFonts();
        SpriteFrame s = art.Text("中", 2, 0xFF0000u, 0x0000FFu);
        bool hasFill = false, hasOutline = false;
        for (int i = 0; i < s.Rgba.Length; i += 4)
        {
            if (s.Rgba[i + 3] == 255 && s.Rgba[i] == 255 && s.Rgba[i + 1] == 0 && s.Rgba[i + 2] == 0)
            {
                hasFill = true;
            }
            if (s.Rgba[i + 3] == 255 && s.Rgba[i] == 0 && s.Rgba[i + 1] == 0 && s.Rgba[i + 2] == 255)
            {
                hasOutline = true;
            }
        }
        Assert.True(hasFill && hasOutline);
    }

    [Fact]
    public void WrappedNeedsFontsAndWrapsToTheWidth()
    {
        Assert.Null(new OverlayArt().Wrapped(FiveWide, 1, 0xFFFFFFu, 8));
        (OverlayArt art, BlockFonts _) = WithFonts();
        Assert.Null(art.Wrapped(string.Empty, 1, 0xFFFFFFu, 8));
        SpriteFrame s = art.Wrapped(FiveWide, 1, 0xFFFFFFu, 8);
        Assert.Equal((10, 18), (s.Width, s.Height));                      // 三行:4*3 + 2*2 + 2
        SpriteFrame cut = art.Wrapped(FiveWide, 1, 0xFFFFFFu, 8, 2);
        Assert.Equal(12, cut.Height);                                      // 两行:4*2 + 2 + 2
        Assert.Same(s, art.Wrapped(FiveWide, 1, 0xFFFFFFu, 8));
    }

    [Fact]
    public void WrappedDoesNotTranslateDataText()
    {
        (OverlayArt art, BlockFonts _) = WithFonts();
        SpriteFrame a = art.Wrapped("INVENTORY", 1, 0xFFFFFFu, 200);
        SpriteFrame b = art.Text("INVENTORY", 1, 0xFFFFFFu);
        Assert.NotSame(a, b);
        Assert.NotEqual(a.Width, b.Width);                                 // b 被翻成两个汉字(宽 10),a 是九个窄字
    }

    [Fact]
    public void GlyphLineHeightIsTheLineHeightPlusTheGap()
    {
        Assert.Equal(0, new OverlayArt().GlyphLineHeight(1));
        (OverlayArt art, BlockFonts _) = WithFonts();
        Assert.Equal(4 + GlyphText.DefaultLineGap, art.GlyphLineHeight(1));
    }

    [Fact]
    public void AFontSetThatReturnsNoFontFallsBackToTheDotFont()
    {
        var art = new OverlayArt { Fonts = new NoFonts() };
        SpriteFrame s = art.Text("背包", 2);
        Assert.Equal(OverlayArt.TextHeight(2), s.Height);
        Assert.Null(art.Wrapped("背包", 1, 0xFFFFFFu, 10));
        Assert.Equal(0, art.GlyphLineHeight(1));
    }

    private sealed class NoFonts : IGlyphFonts
    {
        public IGlyphSource ForSize(int pixelSize) => null;
    }
}

public class ItemTextsTests
{
    internal static byte[] Gbk(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(54936).GetBytes(text);
    }

    internal static StrLibrary Library()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["equipment/equipment.kor.str"] = Gbk("// t\r\nname_1>测试剑\r\nexplain_1>第一行\\n第二行\r\nbasic_1>基础说明\r\nflavor_1>背景故事\r\nname_2>无说明装备\r\npct_1>命中时有10%%的几率;满100%%暴击;单个%不动\r\n"),
            ["stackable/stackable.kor.str"] = Gbk("name_9>测试药水\r\nexplain_9>恢复一些生命\r\nname_1>同键的消耗品\r\n"),
            ["itemshop/itemshop.kor.str"] = Gbk("tab_a>第一页\r\n"),
        };
        return new StrLibrary(p => files.TryGetValue(p, out byte[] d) ? d : null);
    }

    internal static ItemInfo Sword()
    {
        return new ItemInfo(1, "equipment/t/a.equ", ItemKind.Equipment)
        {
            Part = EquipmentParts.Weapon, Rarity = 3, NameKey = "name_1", ExplainKey = "explain_1", BasicExplainKey = "basic_1", FlavorKey = "flavor_1", TextKind = StrLibrary.KindEquipment,
        };
    }

    [Fact]
    public void NameExplainAndFlavorAreResolvedThroughTheItemsTextKind()
    {
        var texts = new ItemTexts(Library());
        ItemInfo sword = Sword();
        Assert.Equal("测试剑", texts.NameOf(sword));
        Assert.Equal("第一行\n第二行", texts.ExplainOf(sword));
        Assert.Equal("背景故事", texts.FlavorOf(sword));
    }

    [Fact]
    public void DoublePercentSignsComeBackAsOne()
    {
        var texts = new ItemTexts(Library());
        ItemInfo sword = Sword();
        sword.ExplainKey = "pct_1";
        Assert.Equal("命中时有10%的几率;满100%暴击;单个%不动", texts.ExplainOf(sword));
        Assert.Equal("没有百分号", ItemTexts.Display("没有百分号"));
        Assert.Equal("50%", ItemTexts.Display("50%%"));
        Assert.Equal("%", ItemTexts.Display("%%"));
        Assert.Equal("%%%", ItemTexts.Display("%%%%%"));
        Assert.Null(ItemTexts.Display(null));
        Assert.Null(ItemTexts.Display(string.Empty));
    }

    [Fact]
    public void ExplainFallsBackToTheBasicExplain()
    {
        var texts = new ItemTexts(Library());
        ItemInfo sword = Sword();
        sword.ExplainKey = null;
        Assert.Equal("基础说明", texts.ExplainOf(sword));
        sword.BasicExplainKey = null;
        Assert.Null(texts.ExplainOf(sword));
        sword.ExplainKey = "no_such_key";
        sword.BasicExplainKey = "basic_1";
        Assert.Equal("基础说明", texts.ExplainOf(sword));      // 第一个键查不到,用第二个
    }

    [Fact]
    public void WithoutATextKindTheItemKindDecidesTheFile()
    {
        var texts = new ItemTexts(Library());
        var gear = new ItemInfo(2, "equipment/t/b.equ", ItemKind.Equipment) { NameKey = "name_1", TextKind = -1 };
        var potion = new ItemInfo(3, "stackable/t/p.stk", ItemKind.Stackable) { NameKey = "name_1", TextKind = -1 };
        Assert.Equal("测试剑", texts.NameOf(gear));
        Assert.Equal("同键的消耗品", texts.NameOf(potion));         // 同一个键在不同文件里是不同的字
    }

    [Fact]
    public void StackableTextsComeFromTheStackableFile()
    {
        var texts = new ItemTexts(Library());
        var potion = new ItemInfo(3, "stackable/t/p.stk", ItemKind.Stackable) { NameKey = "name_9", ExplainKey = "explain_9", TextKind = StrLibrary.KindStackable };
        Assert.Equal("测试药水", texts.NameOf(potion));
        Assert.Equal("恢复一些生命", texts.ExplainOf(potion));
        Assert.Null(texts.FlavorOf(potion));
    }

    [Fact]
    public void MissingPiecesResolveToNull()
    {
        var texts = new ItemTexts(Library());
        Assert.Null(texts.NameOf(null));
        Assert.Null(texts.NameOf(new ItemInfo(4, "equipment/t/c.equ", ItemKind.Equipment)));                 // 没有键
        Assert.Null(texts.NameOf(new ItemInfo(4, "equipment/t/c.equ", ItemKind.Equipment) { NameKey = "nope" }));
        Assert.Null(texts.ExplainOf(new ItemInfo(2, "equipment/t/b.equ", ItemKind.Equipment) { NameKey = "name_2" }));
        Assert.Null(new ItemTexts(new StrLibrary(p => null)).NameOf(Sword()));                               // 文件不存在
        Assert.Throws<ArgumentNullException>(() => new ItemTexts(null));
    }

    [Fact]
    public void ShopTabNamesComeFromTheShopTextFile()
    {
        var texts = new ItemTexts(Library());
        Assert.Equal("第一页", texts.ShopTabName("tab_a"));
        Assert.Null(texts.ShopTabName("tab_b"));
        Assert.Null(texts.ShopTabName(null));
        Assert.Null(texts.ShopTabName(string.Empty));
    }
}

public class ItemDetailArtTests
{
    private static OverlayArt ArtWithFonts() => new OverlayArt { Fonts = new BlockFonts() };

    private static int Build(List<SceneSprite> into, OverlayArt art, ItemTexts texts, ItemInfo item, int x = 10, int y = 20, int width = 40, int bottom = 200)
    {
        int order = 0;
        return ItemDetailArt.Build(into, ref order, art, texts, item, x, y, width, bottom, "t");
    }

    [Fact]
    public void NothingIsAddedWithoutFontsTextsItemOrAName()
    {
        var texts = new ItemTexts(ItemTextsTests.Library());
        var list = new List<SceneSprite>();
        Assert.Equal(0, Build(list, new OverlayArt(), texts, ItemTextsTests.Sword()));
        Assert.Equal(0, Build(list, ArtWithFonts(), null, ItemTextsTests.Sword()));
        Assert.Equal(0, Build(list, ArtWithFonts(), texts, null));
        Assert.Equal(0, Build(list, ArtWithFonts(), texts, new ItemInfo(9, "equipment/t/z.equ", ItemKind.Equipment) { NameKey = "nope" }));
        Assert.Equal(0, Build(list, ArtWithFonts(), texts, ItemTextsTests.Sword(), width: 0));
        Assert.Empty(list);
    }

    [Fact]
    public void NameAndExplainAreStackedWithAGap()
    {
        var texts = new ItemTexts(ItemTextsTests.Library());
        var list = new List<SceneSprite>();
        Assert.Equal(2, Build(list, ArtWithFonts(), texts, ItemTextsTests.Sword()));
        SceneSprite name = list.Single(s => s.AnimationPath == "t-name");
        SceneSprite explain = list.Single(s => s.AnimationPath == "t-explain");
        Assert.Equal((10, 20), (name.ScreenX, name.ScreenY));
        Assert.Equal(10, explain.ScreenX);
        Assert.Equal(20 + name.Frame.Height + ItemDetailArt.BlockGap, explain.ScreenY);
        Assert.Equal(OverlayArt.UiRank, name.LayerRank);
    }

    [Fact]
    public void TheNameIsColoredByRarity()
    {
        var texts = new ItemTexts(ItemTextsTests.Library());
        var list = new List<SceneSprite>();
        ItemInfo sword = ItemTextsTests.Sword();
        sword.Rarity = 5;
        Build(list, ArtWithFonts(), texts, sword);
        SpriteFrame name = list.Single(s => s.AnimationPath == "t-name").Frame;
        uint color = BagWindowArt.RarityColor(5);
        bool found = false;
        for (int i = 0; i < name.Rgba.Length; i += 4)
        {
            if (name.Rgba[i + 3] == 255 && name.Rgba[i] == ((color >> 16) & 0xFF) && name.Rgba[i + 1] == ((color >> 8) & 0xFF) && name.Rgba[i + 2] == (color & 0xFF))
            {
                found = true;
            }
        }
        Assert.True(found);
    }

    [Fact]
    public void WithoutAnExplainOnlyTheNameIsDrawn()
    {
        var texts = new ItemTexts(ItemTextsTests.Library());
        var list = new List<SceneSprite>();
        var gear = new ItemInfo(2, "equipment/t/b.equ", ItemKind.Equipment) { NameKey = "name_2", TextKind = StrLibrary.KindEquipment };
        Assert.Equal(1, Build(list, ArtWithFonts(), texts, gear));
        Assert.Equal("t-name", Assert.Single(list).AnimationPath);
    }

    [Fact]
    public void AnExplainThatDoesNotFitIsCutToTheAvailableLines()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["equipment/equipment.kor.str"] = ItemTextsTests.Gbk("n>名\r\ne>" + string.Concat(Enumerable.Repeat("很长的说明", 40)) + "\r\n"),
        };
        var texts = new ItemTexts(new StrLibrary(p => files.TryGetValue(p, out byte[] d) ? d : null));
        var item = new ItemInfo(1, "equipment/t/a.equ", ItemKind.Equipment) { NameKey = "n", ExplainKey = "e", TextKind = StrLibrary.KindEquipment };
        var list = new List<SceneSprite>();
        OverlayArt art = ArtWithFonts();
        // 名字一行(高 6 + 2 描边),间隔 6;说明从 y = 20 + 6 + 6 开始,到 bottom = 62 还剩 30 像素,每行 6:五行
        Build(list, art, texts, item, bottom: 62);
        SpriteFrame explain = list.Single(s => s.AnimationPath == "t-explain").Frame;
        int expectedLines = (62 - (20 + list.Single(s => s.AnimationPath == "t-name").Frame.Height + ItemDetailArt.BlockGap)) / art.GlyphLineHeight(1);
        Assert.Equal(expectedLines * 4 + (expectedLines - 1) * GlyphText.DefaultLineGap + 2, explain.Height);
    }

    [Fact]
    public void NoRoomForAnExplainLeavesJustTheName()
    {
        var texts = new ItemTexts(ItemTextsTests.Library());
        var list = new List<SceneSprite>();
        Assert.Equal(1, Build(list, ArtWithFonts(), texts, ItemTextsTests.Sword(), bottom: 30));
    }
}

public class WindowTextTests
{
    private static ItemInfo Gear(int code, int rarity = 2)
    {
        var item = new ItemInfo(code, "equipment/t/g" + code + ".equ", ItemKind.Equipment)
        {
            Part = EquipmentParts.Weapon, MinimumLevel = 1, Grade = 10, Rarity = rarity, Price = 300,
            NameKey = "name_1", ExplainKey = "explain_1", TextKind = StrLibrary.KindEquipment,
        };
        item.UsableJobs.Add("[swordman]");
        return item;
    }

    private static int Count(List<SceneSprite> sprites, string tag) => sprites.Count(s => s.AnimationPath == tag);

    private static HeroProgress ProgressWith(ItemInfo item)
    {
        HeroProgress p = ExpTests.Progress();
        p.AddItem(item);
        return p;
    }

    private static OverlayArt ArtWithFonts() => new OverlayArt { Fonts = new BlockFonts() };

    [Fact]
    public void TheBagShowsTheCursorItemsNameAndExplainWhenFontsAndTextsAreThere()
    {
        var bag = new BagWindow();
        Assert.True(bag.Show(GameStage.Town));
        HeroProgress p = ProgressWith(Gear(1));
        var texts = new ItemTexts(ItemTextsTests.Library());

        List<SceneSprite> with = BagWindowArt.Build(bag, p, null, ArtWithFonts(), default, texts);
        Assert.Equal(1, Count(with, "bag-item-name"));
        Assert.Equal(1, Count(with, "bag-item-explain"));
        SceneSprite name = with.Single(s => s.AnimationPath == "bag-item-name");
        Assert.Equal(BagWindow.SlotX, name.ScreenX);
        Assert.Equal(BagWindowArt.StatsY + 40 + 28, name.ScreenY);
    }

    [Fact]
    public void TheBagKeepsThePlaceholderLookWithoutTextsOrWithoutFonts()
    {
        var bag = new BagWindow();
        bag.Show(GameStage.Town);
        HeroProgress p = ProgressWith(Gear(1));
        var texts = new ItemTexts(ItemTextsTests.Library());

        Assert.Equal(0, Count(BagWindowArt.Build(bag, p, null, ArtWithFonts()), "bag-item-name"));                  // 没给文字表
        Assert.Equal(0, Count(BagWindowArt.Build(bag, p, null, new OverlayArt(), default, texts), "bag-item-name"));   // 没装字体
        // 与旧签名的结果逐项一致
        List<SceneSprite> old = BagWindowArt.Build(bag, p, null, new OverlayArt());
        List<SceneSprite> same = BagWindowArt.Build(bag, p, null, new OverlayArt(), default, null);
        Assert.Equal(old.Select(s => s.AnimationPath), same.Select(s => s.AnimationPath));
    }

    [Fact]
    public void TheBagShowsNothingForAnEmptyCursorCell()
    {
        var bag = new BagWindow();
        bag.Show(GameStage.Town);
        var texts = new ItemTexts(ItemTextsTests.Library());
        List<SceneSprite> sprites = BagWindowArt.Build(bag, ExpTests.Progress(), null, ArtWithFonts(), default, texts);
        Assert.Equal(0, Count(sprites, "bag-item-name"));
    }

    private static ShopBook Book(string tabNameKey)
    {
        var items = new Dictionary<int, ItemInfo> { [1] = Gear(1), [2] = Gear(2) };
        var shops = new Dictionary<int, ItemShopDef>
        {
            [3] = new ItemShopDef(3, "itemshop/a.shp", 11, "[etc shop]", null, new[] { new ItemShopTab(new[] { 1, 2 }, tabNameKey) }),
        };
        return new ShopBook(i => shops.TryGetValue(i, out ItemShopDef s) ? s : null, c => items.TryGetValue(c, out ItemInfo it) ? it : null,
            new ItemPriceRates(500, 0, 100), () => shops.Keys.OrderBy(k => k).ToList());
    }

    private static ShopWindow OpenShop(string tabNameKey = "tab_a")
    {
        var w = new ShopWindow();
        w.Use(Book(tabNameKey));
        Assert.True(w.Show(GameStage.Town, 3));
        return w;
    }

    [Fact]
    public void TheShopStretchesItsDetailBoxToHoldTheItemTexts()
    {
        ShopWindow w = OpenShop();
        HeroProgress p = ExpTests.Progress();
        var texts = new ItemTexts(ItemTextsTests.Library());

        List<SceneSprite> with = ShopWindowArt.Build(w, p, null, ArtWithFonts(), texts);
        Assert.Equal(1, Count(with, "shop-item-name"));
        Assert.Equal(1, Count(with, "shop-item-explain"));
        Assert.Equal(ShopWindowArt.TextBoxHeight, with.Single(s => s.AnimationPath == "shop-detail").Frame.Height);

        List<SceneSprite> without = ShopWindowArt.Build(w, p, null, ArtWithFonts());
        Assert.Equal(0, Count(without, "shop-item-name"));
        Assert.Equal(190, without.Single(s => s.AnimationPath == "shop-detail").Frame.Height);
    }

    [Fact]
    public void TheShopTextsStayInsideTheDetailBox()
    {
        ShopWindow w = OpenShop();
        var texts = new ItemTexts(ItemTextsTests.Library());
        List<SceneSprite> sprites = ShopWindowArt.Build(w, ExpTests.Progress(), null, ArtWithFonts(), texts);
        SceneSprite box = sprites.Single(s => s.AnimationPath == "shop-detail");
        foreach (string tag in new[] { "shop-item-name", "shop-item-explain" })
        {
            SceneSprite s = sprites.Single(x => x.AnimationPath == tag);
            Assert.True(s.ScreenX >= box.ScreenX && s.ScreenX + s.Frame.Width <= box.ScreenX + box.Frame.Width, tag + " 横向超出");
            Assert.True(s.ScreenY >= box.ScreenY && s.ScreenY + s.Frame.Height <= box.ScreenY + box.Frame.Height, tag + " 纵向超出");
        }
    }

    [Fact]
    public void BuyTabsUseTheShopsOwnPageNamesWhenTheTextTableHasThem()
    {
        ShopWindow w = OpenShop("tab_a");
        var texts = new ItemTexts(ItemTextsTests.Library());
        OverlayArt art = ArtWithFonts();
        Assert.Equal("第一页", ShopWindowArt.TabText(w, 0, art, texts));
        Assert.Equal("PAGE 1", ShopWindowArt.TabText(w, 0, new OverlayArt(), texts));          // 没字体:占位
        Assert.Equal("PAGE 1", ShopWindowArt.TabText(w, 0, art, null));                         // 没文字表:占位
        Assert.Equal("PAGE 1", ShopWindowArt.TabText(OpenShop("tab_missing"), 0, art, texts));  // 表里没有这个键:占位
        Assert.Equal("PAGE 1", ShopWindowArt.TabText(OpenShop(null), 0, art, texts));           // 脚本没写页名:占位
        Assert.Equal("PAGE 9", ShopWindowArt.TabText(w, 8, art, texts));                        // 越界下标
    }

    [Fact]
    public void SellTabsKeepTheBagLabels()
    {
        ShopWindow w = OpenShop();
        w.ToggleMode();
        var texts = new ItemTexts(ItemTextsTests.Library());
        Assert.Equal(BagWindow.TabLabel(InventoryTab.Equipment), ShopWindowArt.TabText(w, 0, ArtWithFonts(), texts));
    }
}
