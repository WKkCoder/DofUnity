using Dnf.Render;
using static Dnf.Play.Tests.OnlineDungeonScreenGapKit;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 测试补强(区域 C,第七轮):<see cref="DungeonSelectOverlay.EnhanceWorldMap"/> 与 <see cref="OnlineDungeonOverlay.Build"/> 里
/// "边界值与守卫"一类的小处。这两个入口是公开的、接受任意精灵列表,所以这里直接造精灵列表、给带字体的素材,把边界卡在两侧:
/// 按标签找精灵时下标 0 也算找到、文字行最大宽度的"刚好放得下/刚好放不下"、区域名与队伍图标重叠时的左移位置、
/// 条目为空或选中越界时的守卫、找不到卡片时的返回路径、提示条宽度的上限。
/// 复用 <see cref="OnlineDungeonScreenGapKit"/>(internal,同一个测试程序集):行号编码色块与随字号变化的探针字体 <see cref="ProbeFonts"/>;
/// 不改全进程共用字体集(素材用实例自己的 <see cref="OverlayArt.Fonts"/>)。
/// ProbeFonts 的排版规则(手算用):窄字前进 3、全角字前进 4、省略号(U+2026)按全角前进 4;帧宽 = 各字前进之和 + 2(描边两边各 1)、帧高 = 字号 + 2;
/// 限宽的处理(<see cref="ShellPaint.Line"/>):测宽超过限宽就把字号一号一号缩小(最多缩 4 号、不低于 8 号),缩到头仍超才从后面去字、补省略号。
/// 期望值都是手算的字面量,注释写推导。
/// </summary>
public class OnlineDungeonScreenGapR7Tests
{
    private static (int X, int Y, int W, int H) Box(SceneSprite s) => (s.ScreenX, s.ScreenY, s.Frame.Width, s.Frame.Height);

    /// <summary>n 个全角字(每个前进 4,共 4n)。</summary>
    private static string Wide(int n) => new string('洛', n);

    /// <summary>n 个窄字(每个前进 3,共 3n)。</summary>
    private static string Narrow(int n) => new string('A', n);

    /// <summary>占位精灵:标签决定它会不会被文字系统替换;层序与不透明度故意给非默认值,用来确认替换时原样继承。</summary>
    private static SceneSprite Stub(string tag, int x, int y, int sortKey, byte colorTag, int tint = 255)
    {
        return OverlayArt.Place(Coded(6, 4, colorTag), x, y, sortKey, tag, tint);
    }

    /// <summary>
    /// 带字体调用世界地图增强入口,只让"文字系统替换"这一段起作用:不挂可进入状态、不挂提示(所以不会画遮罩与提示条),卡片图已淡入完(2000 毫秒)。
    /// 返回的序号必须等于精灵总数(没有追加任何精灵)。
    /// </summary>
    private static void Enhance(
        List<SceneSprite> sprites,
        ProbeFonts fonts,
        IReadOnlyList<DungeonEntry> entries,
        int selected,
        Func<DungeonEntry, string> entryDisplayName,
        string regionDisplayName)
    {
        int order = 99;
        DungeonSelectOverlay.EnhanceWorldMap(sprites, ref order, new OverlayArt { Fonts = fonts }, entries, selected, 2000, 0, null, entryDisplayName, regionDisplayName, null);
        Assert.Equal(sprites.Count, order);
    }

    /// <summary>
    /// 两张占位卡片(取不到卡片图,只用它们的位置与等级):
    ///   0 号 标记 (160,160)、等级 4 → 气泡 (124,83) → 选中大卡 (97,63,168,73)、没选中小卡 (124,83,118,52);
    ///   1 号 标记 (420,220)、等级 1000 → 气泡 (384,143) → 选中大卡 (357,123,168,73)、没选中小卡 (384,143,118,52)。
    /// 等级文字的位数不同(004 与 1000),用来从帧宽看出文字取的是哪张卡。
    /// </summary>
    private static DungeonEntry[] TwoCards()
    {
        return new[]
        {
            FallbackCardEntry("lorien", 9101, 160, 160, level: 4),
            FallbackCardEntry("vilmark", 9102, 420, 220, level: 1000),
        };
    }

    // ------------------------------------------------------------------ 区域名:按标签找精灵、限宽 160、与队伍图标重叠时的位置

    [Fact]
    public void TheRegionNameSpriteIsFoundByTagEvenInTheFirstSlotAndReplacedByFontTextKeepingItsLayerSlot()
    {
        // 保护:按标签 map-region-name 找精灵,下标 0 也算找到(不能要求它排在别的精灵后面):带字体又给了区域显示名时,
        // 下标 0 的占位精灵被换成字体字(14 号、白字),层序、标签、不透明度继承原来的,别的精灵一个不动;
        // 没给显示名(null、空串)或列表里没有这个标签时一个都不换,也不去向字体要字。
        // "AB":3+3 = 6 → 帧宽 8、帧高 14+2 = 16;右端 797 → x = 797-8 = 789(不小于 762,不挪到队伍图标左边),y = 482。
        var fonts = new ProbeFonts();
        SceneSprite region = Stub("map-region-name", 5, 6, 3, 40, tint: 200);
        SceneSprite other = Stub("map-other", 7, 8, 4, 41);
        var sprites = new List<SceneSprite> { region, other };
        Enhance(sprites, fonts, null, -1, null, "AB");

        Assert.Equal(2, sprites.Count);
        Assert.NotSame(region, sprites[0]);
        Assert.Equal((789, 482, 8, 16), Box(sprites[0]));
        Assert.Equal(("map-region-name", 3, 200), (sprites[0].AnimationPath, sprites[0].SortKey, sprites[0].TintAlpha));
        Assert.Contains(0xF4F4F4u, OpaqueColors(sprites[0].Frame));
        Assert.Same(other, sprites[1]);
        Assert.Equal(new[] { 14 }, fonts.Requested.ToArray());

        // 对照:同样的精灵列表,区域显示名为 null 或空串 → 原样不动、字体一次都没被问过
        foreach (string none in new[] { null, string.Empty })
        {
            var quiet = new ProbeFonts();
            var untouched = new List<SceneSprite> { region, other };
            Enhance(untouched, quiet, null, -1, null, none);
            Assert.Same(region, untouched[0]);
            Assert.Same(other, untouched[1]);
            Assert.Empty(quiet.Requested);
        }

        // 对照:列表里没有 map-region-name 标签 → 给了显示名也一个都不换
        SceneSprite second = Stub("map-level-x", 1, 2, 9, 42);
        var noTag = new List<SceneSprite> { other, second };
        Enhance(noTag, new ProbeFonts(), null, -1, null, "AB");
        Assert.Same(other, noTag[0]);
        Assert.Same(second, noTag[1]);
    }

    [Theory]
    [InlineData(39, 1, 581, 161, 16, "14")]             // 159 宽(39 个全角字 156 + 'A' 3):放得下;帧 161×16;右对齐 x = 797-161 = 636 < 762 → 挪到图标左边:745-3-161 = 581
    [InlineData(40, 0, 580, 162, 16, "14")]             // 160 宽(40 个全角字):刚好放得下,不缩不截;帧 162×16;x = 745-3-162 = 580
    [InlineData(38, 3, 581, 161, 12, "14,13,12,11,10")] // 161 宽(38 个全角字 152 + "AAA" 9):放不下,缩到 10 号仍超 → 截断,帧 161×12
    public void ARegionNameExactlyAsWideAsTheOneHundredSixtyLimitFitsWhileOnePixelMoreIsShrunkAndClipped(
        int wide, int narrow, int expectedX, int expectedWidth, int expectedHeight, string expectedSizes)
    {
        // 保护:区域名的限宽是 160(超过才处理):159、160 宽原样排出(14 号、帧高 16),161 宽才一号一号缩到 10 号并从后面截断补省略号。
        //   限宽若被改小一像素,刚好 160 宽的名字就会被缩小截断;改大一像素,161 宽的名字就会原样排出。
        // 名字 = 若干全角字后面跟若干窄字。161 宽的截断推导(10 号):41 个字测宽 161+4(省略号)> 160;去 1 个字(38 全角 + "AA")= 152+6 = 158,+4 = 162 > 160;
        //   再去 1 个(38 全角 + "A")= 155,+4 = 159 <= 160 → 留 "38 个全角字 + A + …",测宽 159 → 帧宽 161、帧高 10+2 = 12。
        // 这些名字都够长,右对齐后的左端小于 762(= 队伍图标 745 + 17),所以都挪到图标左边:x = 745-3-帧宽,y = 482。
        var fonts = new ProbeFonts();
        var sprites = new List<SceneSprite> { Stub("map-first", 0, 0, 0, 51), Stub("map-region-name", 0, 0, 1, 50) };
        Enhance(sprites, fonts, null, -1, null, Wide(wide) + Narrow(narrow));

        Assert.Equal(2, sprites.Count);
        Assert.Equal((expectedX, 482, expectedWidth, expectedHeight), Box(sprites[1]));
        Assert.Equal(expectedSizes, string.Join(",", fonts.Requested));
        Assert.Equal(("map-region-name", 1), (sprites[1].AnimationPath, sprites[1].SortKey));
    }

    [Theory]
    [InlineData(0, 11, 762, 35)]     // 11 个 'A':测宽 33 → 帧宽 35;右对齐 x = 797-35 = 762,不小于 762 → 不挪
    [InlineData(7, 2, 706, 36)]      // 7 个全角字 + "AA":测宽 28+6 = 34 → 帧宽 36;x = 797-36 = 761 < 762 → 挪到图标左边:745-3-36 = 706
    [InlineData(0, 2, 789, 8)]       // "AA":测宽 6 → 帧宽 8;x = 797-8 = 789,不挪
    public void TheRegionNameStaysRightAlignedUnlessItWouldTouchThePartyIconAndThenMovesToItsLeft(int wide, int narrow, int expectedX, int expectedWidth)
    {
        // 保护:区域名右对齐到 797;右对齐后的左端 x 小于 762(= 队伍图标 745 + 17)就会盖住图标,此时改放在图标左边 x = 745-3-帧宽,
        // 恰好 762 不算盖住。两侧各取一例(刚好 762 不挪、761 就挪),并核对挪动用的是"减 3"(与队伍图标之间留 3 像素)。
        var fonts = new ProbeFonts();
        var sprites = new List<SceneSprite> { Stub("map-region-name", 0, 0, 0, 52) };
        Enhance(sprites, fonts, null, -1, null, Wide(wide) + Narrow(narrow));

        Assert.Equal((expectedX, 482, expectedWidth, 16), Box(Assert.Single(sprites)));
    }

    // ------------------------------------------------------------------ 选中卡片的等级文字:按标签找精灵、守卫

    [Fact]
    public void TheLevelTextSpriteIsFoundByTagEvenInTheFirstSlotAndShowsTheSelectedCardsLevel()
    {
        // 保护:按标签 map-level 找精灵,下标 0 也算找到:带字体、选中项有卡片时,下标 0 的占位精灵被换成选中卡的等级字(12 号、白字),
        // 位置、层序、标签、不透明度继承原来的;选中项不同文字就不同(靠位数区分:等级 4 → "Lv. 004~005",等级 1000 → "Lv. 1000~1001")。
        // "Lv. 004~005":11 个字符,空格前进 2、其余 10 个前进 3 → 32 → 帧宽 34、帧高 12+2 = 14;
        // "Lv. 1000~1001":13 个字符,12*3+2 = 38 → 帧宽 40、帧高 14。
        DungeonEntry[] entries = TwoCards();
        foreach ((int selected, int expectedWidth) in new[] { (0, 34), (1, 40) })
        {
            var fonts = new ProbeFonts();
            SceneSprite level = Stub("map-level", 103, 89, 5, 60, tint: 180);
            SceneSprite other = Stub("map-other", 1, 2, 6, 61);
            var sprites = new List<SceneSprite> { level, other };
            Enhance(sprites, fonts, entries, selected, null, null);

            Assert.Equal(2, sprites.Count);
            Assert.Equal((103, 89, expectedWidth, 14), Box(sprites[0]));
            Assert.Equal(("map-level", 5, 180), (sprites[0].AnimationPath, sprites[0].SortKey, sprites[0].TintAlpha));
            Assert.Contains(0xF4F4F4u, OpaqueColors(sprites[0].Frame));
            Assert.Same(other, sprites[1]);
            Assert.Equal(new[] { 12 }, fonts.Requested.ToArray());
        }
    }

    [Fact]
    public void TheLevelTextIsLeftAloneWithoutThrowingWhenThereAreNoEntriesTheSelectionIsOutOfRangeOrTheSelectedEntryHasNoCard()
    {
        // 保护:替换等级字的守卫是"条目列表不为空 且 选中序号在 [0, 数量) 内 且 选中项有卡片",缺哪一项都原样不动、不抛异常、也不去向字体要字:
        //   条目为 null;条目为空列表而选中 0;选中 == 数量(2);选中 -1;选中远超数量(5);选中项没有卡片(占位列表的普通条目)。
        DungeonEntry[] two = TwoCards();
        var cases = new (string Name, IReadOnlyList<DungeonEntry> Entries, int Selected)[]
        {
            ("条目为 null", null, 0),
            ("空列表选 0", Array.Empty<DungeonEntry>(), 0),
            ("选中等于数量", two, 2),
            ("选中 -1", two, -1),
            ("选中远超数量", two, 5),
            ("选中项没有卡片", new[] { PlainEntry("AAA") }, 0),
        };
        foreach ((string name, IReadOnlyList<DungeonEntry> entries, int selected) in cases)
        {
            var fonts = new ProbeFonts();
            SceneSprite level = Stub("map-level", 103, 89, 5, 60);
            var sprites = new List<SceneSprite> { level };
            Enhance(sprites, fonts, entries, selected, null, null);

            Assert.True(ReferenceEquals(level, sprites[0]), name);
            Assert.Single(sprites);
            Assert.Empty(fonts.Requested);
        }
    }

    // ------------------------------------------------------------------ 卡片名:限宽 = 卡片宽 - 10、找不到卡片的返回路径

    [Theory]
    [InlineData(2, 50, 27, 0, "14,12", 160, 16, 91, 110, 14, 162)]
    [InlineData(0, 53, 25, 3, "14,13,12,11,10,12,11,10,9,8", 159, 12, 93, 109, 10, 164)]
    public void ACardNameExactlyAsWideAsItsCardMinusTenFitsWhileOnePixelMoreIsShrunkAndClipped(
        int zeroWide, int zeroNarrow, int oneWide, int oneNarrow, string expectedSizes,
        int zeroWidth, int zeroHeight, int zeroY, int oneWidth, int oneHeight, int oneY)
    {
        // 保护:卡片名的限宽 = 卡片宽 - 10(选中的大卡 168 → 158、没选中的小卡 118 → 108),超过才处理:
        //   刚好放得下的名字原样排出(选中卡 14 号、小卡 12 号,不缩不截),多 1 像素就一号一号缩小(最多缩 4 号)并从后面截断补省略号;
        //   限宽若被改小一像素,刚好放得下的名字就会被缩小截断;改大一像素,多 1 像素的名字就会原样排出。
        // 名字落位:x = 卡左 + 6;y = 卡顶 + (卡高 - 帧高)/2(整数除)。名字精灵的落点分别放在 0 号大卡 (97,63,168,73) 与 1 号小卡 (384,143,118,52) 里(选中 0 号)。
        // 名字 = 若干全角字后面跟若干窄字(截断从后面去字,所以先去掉的是窄字)。
        //
        // 情形一(刚好放得下):
        //   0 号(选中,限宽 158,14 号):2 个全角字 + 50 个 'A' = 8+150 = 158 → 帧 160×16;x = 97+6 = 103,y = 63 + (73-16)/2 = 63+28 = 91;
        //   1 号(小卡,限宽 108,12 号):27 个全角字 = 108 → 帧 110×14;x = 384+6 = 390,y = 143 + (52-14)/2 = 143+19 = 162。
        // 情形二(多 1 像素):
        //   0 号:53 个 'A' = 159 > 158 → 缩到 10 号(14→13→12→11→10)仍超 → 去字后 + 省略号(4)<= 158,即 <= 154:51 个 'A' = 153(52 个 = 156 超)
        //         → "51 个 A + …" 测宽 157 → 帧 159×12;x = 103,y = 63 + (73-12)/2 = 63+30 = 93;
        //   1 号:25 个全角字 + "AAA" = 100+9 = 109 > 108 → 缩到 8 号(12→11→10→9→8)仍超 → 去字后 + 4 <= 108,即 <= 104:25 个全角字 + 'A' = 103(再加一个 'A' = 106 超)
        //         → "25 个全角字 + A + …" 测宽 107 → 帧 109×10;x = 390,y = 143 + (52-10)/2 = 143+21 = 164。
        DungeonEntry[] entries = TwoCards();
        string zeroName = Wide(zeroWide) + Narrow(zeroNarrow);
        string oneName = Wide(oneWide) + Narrow(oneNarrow);
        var fonts = new ProbeFonts();
        SceneSprite nameZero = Stub("map-card-name", 103, 91, 11, 70, tint: 220);
        SceneSprite nameOne = Stub("map-card-name", 390, 162, 12, 71, tint: 100);
        var sprites = new List<SceneSprite> { nameZero, nameOne };
        Enhance(sprites, fonts, entries, 0, e => e.Card.DungeonId == 9101 ? zeroName : oneName, null);

        Assert.Equal(2, sprites.Count);
        Assert.Equal((103, zeroY, zeroWidth, zeroHeight), Box(sprites[0]));
        Assert.Equal((390, oneY, oneWidth, oneHeight), Box(sprites[1]));
        Assert.Equal(("map-card-name", 11, 220), (sprites[0].AnimationPath, sprites[0].SortKey, sprites[0].TintAlpha));
        Assert.Equal(("map-card-name", 12, 100), (sprites[1].AnimationPath, sprites[1].SortKey, sprites[1].TintAlpha));
        Assert.Contains(0xF4F4F4u, OpaqueColors(sprites[0].Frame));
        Assert.Contains(0xF4F4F4u, OpaqueColors(sprites[1].Frame));
        Assert.Equal(expectedSizes, string.Join(",", fonts.Requested));
    }

    [Fact]
    public void ACardNameSpriteOutsideEveryCardRectangleIsLeftAloneWithoutAskingForAName()
    {
        // 保护:卡片名精灵的落点不在任何卡片矩形里(找不到对应的卡片)时,原样不动、不抛异常、也不去问显示名;
        // 没选中的卡只认小卡矩形(落在它的大卡矩形里不算);选中序号为 -1、等于数量(都当作"没有选中项")时同样安全;
        // 同一个精灵列表里能找到卡片的精灵照常被替换(证明扫描确实一路走完了所有条目)。
        // 选中 0 号时:0 号大卡 (97,63,168,73) → x∈[97,265)、y∈[63,136);1 号小卡 (384,143,118,52) → x∈[384,502)、y∈[143,195);
        //   (700,20):哪张卡都不在;(370,130):在 1 号"选中大卡" (357,123,168,73) 里,但不在 1 号小卡里,也不在 0 号大卡里。
        //   (130,90) 在 0 号大卡里 → 14 号:"XY"(窄字 3+3 = 6 → 帧宽 8、帧高 16),x = 97+6 = 103,y = 63 + (73-16)/2 = 63+28 = 91。
        // 选中 -1 / 2(越界)时:0 号小卡 (124,83,118,52) → x∈[124,242)、y∈[83,135);1 号小卡同上;两个杂散点同样哪张都不在;
        //   (130,90) 落在 0 号小卡里 → 12 号:帧 8×14,x = 124+6 = 130,y = 83 + (52-14)/2 = 83+19 = 102。
        DungeonEntry[] entries = TwoCards();
        var cases = new[]
        {
            (Selected: 0, ExpectedX: 103, ExpectedY: 91, Size: 14),
            (Selected: -1, ExpectedX: 130, ExpectedY: 102, Size: 12),
            (Selected: 2, ExpectedX: 130, ExpectedY: 102, Size: 12),
        };
        foreach (var c in cases)
        {
            int asked = 0;
            var fonts = new ProbeFonts();
            SceneSprite strayFar = Stub("map-card-name", 700, 20, 1, 80);
            SceneSprite strayNear = Stub("map-card-name", 370, 130, 2, 81);
            SceneSprite hit = Stub("map-card-name", 130, 90, 3, 82);
            var sprites = new List<SceneSprite> { strayFar, strayNear, hit };
            Enhance(sprites, fonts, entries, c.Selected, _ =>
            {
                asked++;
                return "XY";
            }, null);

            string label = "选中 " + c.Selected;
            Assert.True(ReferenceEquals(strayFar, sprites[0]), label);
            Assert.True(ReferenceEquals(strayNear, sprites[1]), label);
            Assert.Equal((c.ExpectedX, c.ExpectedY, 8, c.Size + 2), Box(sprites[2]));
            Assert.Equal(("map-card-name", 3), (sprites[2].AnimationPath, sprites[2].SortKey));
            Assert.Equal(1, asked);
            Assert.Equal(new[] { c.Size }, fonts.Requested.ToArray());
        }
    }

    // ------------------------------------------------------------------ 进出副本提示条:宽度 = 文字宽 + 56,上限 560

    [Theory]
    [InlineData(3, 0, 67, 366, 11, 16)]      // "AAA":测宽 9 → 帧 11×16 → 条宽 56+11 = 67,x = (800-67)/2 = 366(整数除)
    [InlineData(3, 123, 559, 120, 503, 16)]  // 测宽 9+492 = 501 → 帧 503×16 → 条宽 56+503 = 559(不到上限),x = (800-559)/2 = 120
    [InlineData(2, 124, 560, 120, 504, 16)]  // 测宽 6+496 = 502 → 帧 504×16 → 条宽 56+504 = 560,刚好等于上限
    [InlineData(1, 125, 560, 120, 505, 16)]  // 测宽 3+500 = 503 ≤ 504(文字限宽 560-56)放得下 → 帧 505×16 → 56+505 = 561 被上限夹到 560
    [InlineData(0, 126, 560, 120, 506, 16)]  // 测宽 504 ≤ 504 刚好放得下 → 帧 506×16 → 56+506 = 562 夹到 560
    [InlineData(3, 124, 559, 120, 503, 12)]  // 测宽 9+496 = 505 > 504 放不下 → 缩到 10 号仍超 → 截断到 去字后 + 4 <= 504,即 <= 500:"AAA"+122 个全角字 = 497(123 个 = 501 超)→ +"…" = 501 → 帧 503×12 → 条宽 559
    [InlineData(0, 200, 560, 120, 506, 12)]  // 测宽 800 远超:缩到 10 号 → 截断到 <= 500:125 个全角字 = 500 → +"…" 504 → 帧 506×12 → 56+506 = 562 夹到 560
    public void TheOnlineDungeonNoticeBarIsAsWideAsItsTextPlusFiftySixButNeverWiderThanFiveHundredSixty(
        int narrow, int wide, int expectedBarWidth, int expectedX, int expectedTextWidth, int expectedTextHeight)
    {
        // 保护:进出联网副本的提示条宽 = 文字帧宽 + 56(左边色条与图标、右边留白),上限 560;文字限宽是 560-56 = 504(测宽超过才缩/截);
        // 条在水平方向居中:x = (800-条宽)/2;精灵依次是 条底、色条、图标(信息语气)、文字,y 从 48 起:
        //   色条 (x+1,49) 4×30,图标 (x+16, 48+(32-20)/2 = 54) 20×20,文字 (x+44, 48+(32-文字高)/2)。
        // 文字 = 若干窄字后面跟若干全角字(截断从后面去字,所以先去掉的是全角字)。
        // 上限若被改大一像素,测宽 503、504、505 与超长的这几例条宽都会变成 561,x 也跟着少 1。
        var fonts = new ProbeFonts();
        string text = Narrow(narrow) + Wide(wide);
        List<SceneSprite> sprites = OnlineDungeonOverlay.Build(new OverlayArt { Fonts = fonts }, new DungeonSelectNotice(DungeonSelectNoticeKind.Info, text), 0);

        Assert.NotNull(sprites);
        Assert.Equal(
            new[] { "onlinedungeon-notice-bar", "onlinedungeon-notice-accent", "onlinedungeon-notice-icon", "onlinedungeon-notice" },
            Tags(sprites));
        int textY = 48 + (32 - expectedTextHeight) / 2;
        Assert.Equal((expectedX, 48, expectedBarWidth, 32), Box(sprites[0]));
        Assert.Equal((expectedX + 1, 49, 4, 30), Box(sprites[1]));
        Assert.Equal((expectedX + 16, 54, 20, 20), Box(sprites[2]));
        Assert.Equal((expectedX + 44, textY, expectedTextWidth, expectedTextHeight), Box(sprites[3]));
        Assert.Equal(new[] { 0, 1, 2, 3 }, sprites.Select(s => s.SortKey));
    }
}
