using Dnf.Data;
using static Dnf.Game.Tests.ExpertJobTestData;

namespace Dnf.Game.Tests;

/// <summary>
/// 副职业目录的装载:从合成的脚本文件集合读出各种类的 .exj、等级上限表、旧版分解表与收购价倍率;以及按物品脚本读副职业标签的接线。
/// 依据:服务端 initExpertJobScript 0x0896d830 → CExpertJobList::init 0x0896d8ec(character 目录下的 expertjob.lst:编号 + 路径,路径相对 character 目录)、
/// ImportExpertJobEtcScript 0x0896ae21、LoadDisJoint 0x0847300c(静态读码)。
/// </summary>
public class ExpertJobCatalogTests
{
    private static Fixture Build()
    {
        return new Fixture()
            .List(ExpertJobKinds.ListPath, (3, "expertjob/disjointer.exj"), (1, "expertjob/enchanter.exj"), (7, "expertjob/ghost.exj"), (4, "expertjob/missing.exj"))
            .Tokens("character/expertjob/disjointer.exj", w => w
                .Tag("[expertness exp]").Int(100).Int(0).Str("a").Int(250).Int(0).Str("b")
                .Tag("[base const]").Int(100)
                .Tag("[giveup cost]").Ints(500, 600))
            .Tokens("character/expertjob/enchanter.exj", w => w
                .Tag("[expertness exp]").Int(50).Int(0).Str("x"))
            .Tokens("character/expertjob/ghost.exj", w => w
                .Tag("[expertness exp]").Int(9).Int(0).Str("g"))
            .Tokens(ExpertJobKinds.EtcPath, w => w
                .Tag("[expertjob level limit]").Ints(1, 10, 2, 20)
                .Tag("[disjointer big win rate]").Ints(1, 3))
            .Tokens(LegacyDisjointScript.Path, w => w
                .Tag("[cube index]").Str("[no element]").Int(950).Tag("[/cube index]")
                .Tag("[cube creation const]").Int(100).Float(1.0f).Float(1.5f).Float(2.0f).Float(2.5f).Float(3.0f).Float(3.5f))
            .Tokens("equipment/pricetable.tbl", w => w.Tag("[percent]").Ints(800, 900, 700))
            .Tokens("equipment/test/g900.equ", w => w.Tag("[impossible contents]").Str("disjoint").Tag("[random option]").Int(1))
            .Tokens("equipment/test/g901.equ", w => w.Tag("[grade]").Int(5));
    }

    private static GameData Data(Fixture fx = null) => new GameData((fx ?? Build()).Build());

    [Fact]
    public void TheScriptPathsAreTheFolderPlusTheFileName()
    {
        Assert.Equal("character", ExpertJobKinds.Folder);
        Assert.Equal("etc", LegacyDisjointScript.Folder);
        Assert.Equal(ExpertJobKinds.ListPath, string.Join("/", "character", "expertjob.lst"));
        Assert.Equal(ExpertJobKinds.EtcPath, string.Join("/", "character", "expertjob.etc"));
        Assert.Equal(LegacyDisjointScript.Path, string.Join("/", "etc", "disjoint.etc"));
    }

    [Fact]
    public void ReadLoadsTheListedScriptsOfKnownKindsRelativeToTheCharacterFolder()
    {
        ExpertJobCatalog catalog = ExpertJobCatalog.Read(Data());
        Assert.Equal(2, catalog.Kinds.Count);     // 编号 7 不是种类;编号 4 的文件不存在
        Assert.Contains(ExpertJobKind.Disjointer, catalog.Kinds);
        Assert.Contains(ExpertJobKind.Enchanter, catalog.Kinds);

        ExpertJobScript disjointer = catalog.Get(ExpertJobKind.Disjointer);
        Assert.NotNull(disjointer);
        Assert.Equal(ExpertJobKind.Disjointer, disjointer.Kind);
        Assert.Equal(2, disjointer.MaxLevel);
        Assert.Equal(100, disjointer.BaseConst);
        Assert.Equal(new[] { 500, 600 }, disjointer.GiveupCosts.ToArray());
        Assert.Equal(1, catalog.Get(ExpertJobKind.Enchanter).MaxLevel);
        Assert.Null(catalog.Get(ExpertJobKind.DollController));
        Assert.Null(catalog.Get(ExpertJobKind.None));
        Assert.Null(catalog.Get(ExpertJobKind.Alchemist));
    }

    [Fact]
    public void ReadAlsoLoadsTheEtcTheLegacyTableAndThePriceRates()
    {
        ExpertJobCatalog catalog = ExpertJobCatalog.Read(Data());
        Assert.True(catalog.Etc.TryGetLevelLimit(2, out int limit));
        Assert.Equal(20, limit);
        Assert.Equal(3, catalog.Etc.BigWinRate(1));
        Assert.Equal(950, catalog.Legacy.MainItem);
        Assert.Equal(100, catalog.Legacy.BaseConst);
        Assert.Equal(800, catalog.PriceRates.Normal);
        Assert.Equal(900, catalog.PriceRates.Middle);
        Assert.Equal(700, catalog.PriceRates.KiriProtectable);
        Assert.Empty(catalog.Errors());
    }

    [Fact]
    public void ReadOnAnEmptyDataSetGivesAnEmptyCatalogWithNeutralPrices()
    {
        ExpertJobCatalog catalog = ExpertJobCatalog.Read(Data(new Fixture()));
        Assert.Empty(catalog.Kinds);
        Assert.Equal(0, catalog.Etc.LevelLimitCount);
        Assert.Equal(0, catalog.Legacy.MainItem);
        Assert.Equal(1000, catalog.PriceRates.Normal);
        Assert.Equal(1000, catalog.PriceRates.KiriProtectable);
        Assert.Empty(catalog.Errors());
        Assert.Throws<ArgumentNullException>(() => ExpertJobCatalog.Read(null));
    }

    [Fact]
    public void ErrorsListsEveryLoadErrorWithItsSource()
    {
        Fixture fx = new Fixture()
            .List(ExpertJobKinds.ListPath, (3, "expertjob/disjointer.exj"))
            .Tokens("character/expertjob/disjointer.exj", w => w.Tag("[giveup cost]").Ints(1, 2, 3, 4, 5, 6, 7, 8, 9, 10))
            .Tokens(ExpertJobKinds.EtcPath, w => w.Tag("[expertjob level limit]").Ints(1, 10, 1, 20));
        ExpertJobCatalog catalog = ExpertJobCatalog.Read(Data(fx));
        List<string> errors = catalog.Errors();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.StartsWith("expertjob.etc:", StringComparison.Ordinal) && e.Contains("[expertjob level limit]"));
        Assert.Contains(errors, e => e.StartsWith("Disjointer:", StringComparison.Ordinal) && e.Contains("[giveup cost]"));
    }

    [Fact]
    public void TheConstructorKeepsOnlyRealKindsAndTheLastScriptOfAKind()
    {
        ExpertJobScript first = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[] { L("[base const]", 1) });
        ExpertJobScript second = ExpertJobScript.Parse(ExpertJobKind.Disjointer, new[] { L("[base const]", 2) });
        ExpertJobScript none = ExpertJobScript.Parse(ExpertJobKind.None, new[] { L("[base const]", 3) });
        var catalog = new ExpertJobCatalog(null, new[] { first, null, none, second }, null, null);
        Assert.Single(catalog.Kinds);
        Assert.Equal(2, catalog.Get(ExpertJobKind.Disjointer).BaseConst);
        Assert.Equal(0, catalog.Etc.LevelLimitCount);
        Assert.Equal(1000, catalog.PriceRates.Normal);
        Assert.Equal(0, catalog.Legacy.MainItem);

        var empty = new ExpertJobCatalog(null, null, null, null);
        Assert.Empty(empty.Kinds);
    }

    // ------------------------------------------------------------------ 物品标签的接线

    [Fact]
    public void TheTagSourceReadsTheItemScriptAndCachesTheResult()
    {
        var source = new ExpertJobTagSource(Data());
        ItemInfo impossible = Gear(900);
        ItemInfo plain = Gear(901);
        ExpertJobItemTags first = source.Get(impossible);
        Assert.True(first.ImpossibleDisjoint);
        Assert.True(first.RandomOption);
        Assert.Same(first, source.Get(impossible));
        Assert.False(source.Get(plain).ImpossibleDisjoint);
        Assert.NotSame(source.Get(impossible), source.Get(plain));
    }

    [Fact]
    public void TheTagSourceGivesNoTagsForMissingItemsPathsOrScripts()
    {
        var source = new ExpertJobTagSource(Data());
        Assert.Same(ExpertJobItemTags.None, source.Get(null));
        Assert.Same(ExpertJobItemTags.None, source.Get(Gear(777)));                                      // 脚本读不到
        Assert.Same(ExpertJobItemTags.None, source.Get(new ItemInfo(778, null, ItemKind.Equipment)));   // 没有路径
        Assert.Throws<ArgumentNullException>(() => new ExpertJobTagSource(null));
    }
}
