using Dnf.Data;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// 称号的外观数据(显示名、自定义动画路径、限时天数、图标——只给数据,不含渲染)、头顶气泡表、限时的换算、以及称号目录(从装备清单里挑出称号目录下的物品并按需读出)。
/// 称号装备脚本的"只认深度 0 的标签"是对全库装备脚本的块规则(<c>ScriptBlocks</c>);数据全是合成的(称号 901 起)。
/// </summary>
public class TitleAppearanceTests
{
    private const string Path901 = "equipment/character/common/title/synth901.equ";

    private static TitleAppearance Parse(TokenStreamBuilder b, int code = 901, string path = Path901)
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return TitleAppearanceScript.Parse(b.Bytes(), strings, code, path);
    }

    // ------------------------------------------------------------------ 外观

    [Fact]
    public void AppearanceReadsTheNamePartAnimationPeriodAndIcon()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[name]").Keyed(1, "t_name_901");
        b.Tag("[name2]").Keyed(1, "t_name2_901");
        b.Tag("[equipment type]").Str("[title name]");
        b.Tag("[usable job]").Str("[all]");
        b.Tag("[custom animation]").Str("animation/synth901.ani");
        b.Tag("[usable period]").Int(30);
        b.Tag("[icon]").Str("icon/t.img").Int(4);
        TitleAppearance a = Parse(b);
        Assert.Equal((901, Path901), (a.Code, a.Path));
        Assert.Equal(("t_name_901", 1, "t_name2_901"), (a.NameKey, a.TextKind, a.Name2Key));
        Assert.Equal(("[title name]", true), (a.PartTag, a.IsTitle));
        Assert.Equal(("animation/synth901.ani", true, "equipment/character/common/title/animation/synth901.ani"), (a.AnimationPath, a.HasAnimation, a.ResolvedAnimationPath));
        Assert.Equal((30, true), (a.UsablePeriodDays, a.IsTimeLimited));
        Assert.Equal(("icon/t.img", 4), (a.IconPath, a.IconFrame));
    }

    [Fact]
    public void OnlyDepthZeroTagsCountAndTheSameTagsInsideABlockAreIgnored()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[custom animation]").Str("outer.ani");
        b.Tag("[usable period]").Int(7);
        b.Tag("[name2]").Keyed(1, "outer_name2");
        b.Tag("[if]").Tag("[custom animation]").Str("block.ani").Tag("[usable period]").Int(99).Tag("[name2]").Keyed(1, "block_name2").Tag("[/if]");
        TitleAppearance a = Parse(b);
        Assert.Equal(("outer.ani", 7, "outer_name2"), (a.AnimationPath, a.UsablePeriodDays, a.Name2Key));

        var b2 = new TokenStreamBuilder();                                                               // 块里的值不能顶替外层缺的字段
        b2.Tag("[if]").Tag("[custom animation]").Str("block.ani").Tag("[usable period]").Int(99).Tag("[/if]");
        TitleAppearance only = Parse(b2);
        Assert.Equal((null, 0, false, false), (only.AnimationPath, only.UsablePeriodDays, only.HasAnimation, only.IsTimeLimited));
    }

    [Fact]
    public void AMissingOrEmptyFieldIsAbsentNotAnError()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[custom animation]").Str("");
        TitleAppearance a = Parse(b);
        Assert.False(a.HasAnimation);
        Assert.Null(a.AnimationPath);                                                                    // 写了空串当没写,不留 ""
        Assert.Null(a.ResolvedAnimationPath);
        Assert.False(a.IsTimeLimited);
        Assert.Equal((null, null, -1, false), (a.NameKey, a.IconPath, a.IconFrame, a.IsTitle));
        Assert.Equal(0, a.UsablePeriodDays);

        var b2 = new TokenStreamBuilder();
        b2.Tag("[usable period]");                                                                       // 没有数
        Assert.Equal(0, Parse(b2).UsablePeriodDays);

        var b3 = new TokenStreamBuilder();
        b3.Tag("[icon]").Str("icon/t.img");                                                              // 只有图包没有帧号:帧号仍是 -1(不是 0)
        TitleAppearance icon = Parse(b3);
        Assert.Equal(("icon/t.img", -1), (icon.IconPath, icon.IconFrame));
    }

    [Fact]
    public void ANonTitleEquipmentTypeIsNotATitle()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[equipment type]").Str("[weapon]");
        TitleAppearance a = Parse(b);
        Assert.False(a.IsTitle);
        Assert.Equal("[weapon]", a.PartTag);
        Assert.False(new TitleAppearance(1, "x").IsTitle);
    }

    [Fact]
    public void AppearanceRejectsANullStringTable()
    {
        Assert.Throws<ArgumentNullException>(() => TitleAppearanceScript.Parse(new byte[] { 0xB0, 0xD0 }, null, 1, "x"));
    }

    // ------------------------------------------------------------------ 气泡

    [Fact]
    public void BalloonRecordsAreSixValuesEachAndAShortTailIsDropped()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[title balloon info]").Ints(901, -1).Str("a_stay.ani").Str("a_move.ani").Ints(3, -4)
            .Ints(902, 901).Str("b_stay.ani").Str("b_move.ani").Ints(0, 0)
            .Ints(5, 6, 7);
        List<TitleBalloon> list = TitleBalloonScript.Parse(TitleFixtures.Lines(b));
        Assert.Equal(2, list.Count);
        Assert.Equal((901, -1, "a_stay.ani", "a_move.ani", 3, -4), (list[0].TitleCode, list[0].SecondCode, list[0].StayAnimation, list[0].MoveAnimation, list[0].OffsetX, list[0].OffsetY));
        Assert.Equal((902, 901, "b_stay.ani"), (list[1].TitleCode, list[1].SecondCode, list[1].StayAnimation));
    }

    [Fact]
    public void ATailOneValueShortOfARecordIsDroppedWithoutReadingPastTheEnd()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[title balloon info]").Ints(901, -1).Str("s.ani").Str("m.ani").Ints(1, 2)
            .Ints(902, -1).Str("t.ani").Str("n.ani").Int(3);                                              // 第二条只有 5 个值,差最后一个
        List<TitleBalloon> list = TitleBalloonScript.Parse(TitleFixtures.Lines(b));
        Assert.Equal(new[] { 901 }, list.Select(x => x.TitleCode).ToArray());

        var exact = new TokenStreamBuilder();
        exact.Tag("[title balloon info]").Ints(901, -1).Str("s.ani").Str("m.ani").Ints(1, 2);              // 恰好 6 个值算一条
        Assert.Single(TitleBalloonScript.Parse(TitleFixtures.Lines(exact)));
    }

    [Fact]
    public void ABalloonRecordWithAWrongShapeEndsThatLinesRecords()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[title balloon info]").Ints(901, -1).Str("s.ani").Str("m.ani").Ints(1, 2)
            .Int(902).Str("not a number").Str("x.ani").Str("y.ani").Ints(0, 0)                          // 第二个值应是数
            .Ints(903, -1).Str("s3.ani").Str("m3.ani").Ints(0, 0);                                      // 前一条坏了,后面不再读
        b.Tag("[title balloon info]").Ints(904, -1).Str("s4.ani").Str("m4.ani").Ints(0, 0);           // 另一行重新开始
        List<TitleBalloon> list = TitleBalloonScript.Parse(TitleFixtures.Lines(b));
        Assert.Equal(new[] { 901, 904 }, list.Select(x => x.TitleCode).ToArray());
        Assert.Empty(TitleBalloonScript.Parse(null));
    }

    // ------------------------------------------------------------------ 限时换算

    [Fact]
    public void ExpiryIsDaysTimesADayPlusTheBaseAndExpiresAtTheExactSecond()
    {
        Assert.Equal(0x44A54A80L, TitleExpiry.BaseSeconds);
        Assert.Equal(86400L, TitleExpiry.SecondsPerDay);
        Assert.Equal(TitleExpiry.BaseSeconds, TitleExpiry.ExpirySeconds(0));
        Assert.Equal(TitleExpiry.BaseSeconds + 86400L * 5, TitleExpiry.ExpirySeconds(5));
        long expiry = TitleExpiry.ExpirySeconds(5);
        Assert.False(TitleExpiry.IsExpired(5, expiry - 1));
        Assert.True(TitleExpiry.IsExpired(5, expiry));                                                   // 到期秒数 ≤ 当前秒数
        Assert.True(TitleExpiry.IsExpired(5, expiry + 1));
    }

    [Fact]
    public void TheExpiryDayForAPeriodIsTheDaysAlreadyPassedPlusThePeriod()
    {
        Assert.Equal(30, TitleExpiry.ExpiryDayFor(TitleExpiry.BaseSeconds, 30));
        Assert.Equal(10, TitleExpiry.ExpiryDayFor(TitleExpiry.BaseSeconds + 3 * 86400L + 5, 7));
        Assert.Equal(3 + 7, TitleExpiry.ExpiryDayFor(TitleExpiry.BaseSeconds + 4 * 86400L - 1, 7));       // 差一秒满四天,按三整天算
        long now = TitleExpiry.BaseSeconds + 100 * 86400L + 12345;
        Assert.False(TitleExpiry.IsExpired(TitleExpiry.ExpiryDayFor(now, 7), now));                      // 刚取得的物品不会马上过期
        Assert.True(TitleExpiry.IsExpired(TitleExpiry.ExpiryDayFor(now, 7), now + 8 * 86400L));
    }

    // ------------------------------------------------------------------ 目录

    private static Fixture CatalogFixture()
    {
        return new Fixture()
            .List(ItemCatalog.EquipmentListPath,
                (901, "character/common/title/synth901.equ"), (902, "character/common/title/synth902.equ"), (903, "character/common/title/synthmissing.equ"), (950, "character/swordman/synthw950.equ"))
            .Tokens(Path901, w => w.Tag("[equipment type]").Str("[title name]").Tag("[custom animation]").Str("a.ani").Tag("[usable period]").Int(7))
            .Tokens("equipment/character/common/title/synth902.equ", w => w.Tag("[equipment type]").Str("[title name]"))
            .Tokens("equipment/character/swordman/synthw950.equ", w => w.Tag("[equipment type]").Str("[weapon]"))
            .Tokens(TitleCatalog.BookPath, w => w.Tag("[title collection info]").Str("general").Int(1).Ints(0, 0, 9001, 1, 901).Tag("[/title collection info]"))
            .Tokens(TitleCatalog.BalloonPath, w => w.Tag("[title balloon info]").Ints(901, -1).Str("s.ani").Str("m.ani").Ints(1, 2));
    }

    [Fact]
    public void TheCatalogListsOnlyItemsUnderTheTitleDirectoryAndReadsThemOnDemand()
    {
        var catalog = new TitleCatalog(new GameData(CatalogFixture().Build()));
        Assert.Equal(new[] { 901, 902, 903 }, catalog.Codes());                                          // 武器 950 不在称号目录下
        TitleAppearance a = catalog.AppearanceOf(901);
        Assert.NotNull(a);
        Assert.Equal((true, "a.ani", 7), (a.IsTitle, a.AnimationPath, a.UsablePeriodDays));
        Assert.Same(a, catalog.AppearanceOf(901));                                                       // 读过的缓存
        Assert.True(catalog.AppearanceOf(902).IsTitle);
        Assert.Null(catalog.AppearanceOf(903));                                                          // 列了但脚本不在
        Assert.Null(catalog.AppearanceOf(950));                                                          // 不是称号目录下的物品
        Assert.Null(catalog.AppearanceOf(777));
    }

    [Fact]
    public void TheCatalogLoadsTheBookAndTheBalloons()
    {
        var catalog = new TitleCatalog(new GameData(CatalogFixture().Build()));
        TitleBookDef book = catalog.Book();
        Assert.Equal(9001, book.SlotAt(TitleCategory.General, 0).AchievementId);
        Assert.Same(book, catalog.Book());
        Assert.Single(catalog.Balloons());
        Assert.True(catalog.TryGetBalloon(901, out TitleBalloon balloon));
        Assert.Equal(("s.ani", "m.ani", 1, 2), (balloon.StayAnimation, balloon.MoveAnimation, balloon.OffsetX, balloon.OffsetY));
        Assert.False(catalog.TryGetBalloon(902, out _));
    }

    [Fact]
    public void ATitleScriptThatIsNotATokenStreamReadsAsNullInsteadOfThrowing()
    {
        MemoryScriptSource source = CatalogFixture().Build();
        source.AddBytes("equipment/character/common/title/synth902.equ", new byte[] { 1, 2, 3, 4, 5, 6, 7 });   // 没有 B0 D0
        source.AddBytes(TitleCatalog.BookPath, new byte[] { 0xB0, 0xD0, 1, 2, 3 });                          // 正文长度不是 5 的倍数
        var catalog = new TitleCatalog(new GameData(source));
        Assert.Null(catalog.AppearanceOf(902));
        Assert.NotNull(catalog.AppearanceOf(901));                                                           // 别的称号不受影响
        Assert.Equal(0, catalog.Book().DefinedSlotCount(TitleCategory.General));
    }

    [Fact]
    public void AMissingDataSetGivesEmptyAnswersNotExceptions()
    {
        var catalog = new TitleCatalog(new GameData(new MemoryScriptSource()));
        Assert.Empty(catalog.Codes());
        Assert.Null(catalog.AppearanceOf(901));
        Assert.Equal(0, catalog.Book().DefinedSlotCount(TitleCategory.General));
        Assert.Empty(catalog.Balloons());
        Assert.False(catalog.TryGetBalloon(901, out _));
        Assert.Throws<ArgumentNullException>(() => new TitleCatalog(null));
    }
}
