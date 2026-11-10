namespace Dnf.Game.Tests;

/// <summary>
/// 角色身上的称号图鉴:四个分类的格数上限、放入的检查次序与返回码、直接放(成就奖励)、取出的检查、清空与读档。
/// 规则取自服务端静态读码(putTitle/getTitle/putTitleOnly/checkError,地址在笔记里);称号与图鉴定义全是合成的(<see cref="TitleFixtures"/>)。
/// </summary>
public class TitleBookTests
{
    private static ItemInfo Item(int code) => TitleFixtures.Lookup()(code);

    private static TitleBook NewBook(TitleBookDef def = null) => new(def ?? TitleFixtures.Book());

    // ------------------------------------------------------------------ 分类与格数

    [Fact]
    public void TheCategoryTableMatchesTheServerNumberingAndCapacities()
    {
        Assert.Equal(new[] { 0, 1, 2, 3 }, new[] { (int)TitleCategory.General, (int)TitleCategory.Specific, (int)TitleCategory.Despair, (int)TitleCategory.Event });
        Assert.Equal(new[] { "general", "specific", "despair", "event" }, new[] { TitleCategory.General, TitleCategory.Specific, TitleCategory.Despair, TitleCategory.Event }.Select(TitleCategories.NameOf).ToArray());
        Assert.Equal(new[] { 70, 200, 120, 60 }, new[] { TitleCategory.General, TitleCategory.Specific, TitleCategory.Despair, TitleCategory.Event }.Select(TitleCategories.CapacityOf).ToArray());
        Assert.Equal(4, TitleCategories.Count);
        Assert.Null(TitleCategories.NameOf((TitleCategory)9));
        Assert.Equal(0, TitleCategories.CapacityOf((TitleCategory)9));
        Assert.False(TitleCategories.IsValid((TitleCategory)4));
        Assert.False(TitleCategories.IsValid((TitleCategory)(-1)));
    }

    [Fact]
    public void CategoryKeywordsParseCaseInsensitivelyAndUnknownOnesFail()
    {
        Assert.True(TitleCategories.TryParse("GENERAL", out TitleCategory c));
        Assert.Equal(TitleCategory.General, c);
        Assert.True(TitleCategories.TryParse("Event", out c));
        Assert.Equal(TitleCategory.Event, c);
        Assert.False(TitleCategories.TryParse("bogus", out _));
        Assert.False(TitleCategories.TryParse("", out _));
        Assert.False(TitleCategories.TryParse(null, out _));
    }

    [Fact]
    public void SlotRangesEndExactlyAtTheCapacity()
    {
        Assert.True(TitleCategories.IsValidSlot(TitleCategory.General, 0));
        Assert.True(TitleCategories.IsValidSlot(TitleCategory.General, 69));
        Assert.False(TitleCategories.IsValidSlot(TitleCategory.General, 70));
        Assert.True(TitleCategories.IsValidSlot(TitleCategory.Specific, 199));
        Assert.False(TitleCategories.IsValidSlot(TitleCategory.Specific, 200));
        Assert.True(TitleCategories.IsValidSlot(TitleCategory.Despair, 119));
        Assert.False(TitleCategories.IsValidSlot(TitleCategory.Despair, 120));
        Assert.True(TitleCategories.IsValidSlot(TitleCategory.Event, 59));
        Assert.False(TitleCategories.IsValidSlot(TitleCategory.Event, 60));
        Assert.False(TitleCategories.IsValidSlot(TitleCategory.General, -1));
        Assert.False(TitleCategories.IsValidSlot((TitleCategory)7, 0));
    }

    // ------------------------------------------------------------------ 放入

    [Fact]
    public void PuttingWritesTheSlotAndReturnsWhatItDisplaced()
    {
        TitleBook book = NewBook();
        Assert.Equal(TitleBookResult.Ok, book.Put(TitleCategory.General, 5, Item(TitleFixtures.General5), 10, out int replaced));
        Assert.Equal(0, replaced);
        Assert.Equal((TitleFixtures.General5, 1, 1, 1), (book.CodeAt(TitleCategory.General, 5), book.Count(TitleCategory.General), book.TotalCount, book.Version));
        Assert.True(book.Contains(TitleFixtures.General5));
        Assert.True(book.TryFind(TitleFixtures.General5, out TitleCategory c, out int s));
        Assert.Equal((TitleCategory.General, 5), (c, s));

        // 不绑成就的格什么都收:换进另一件,原来的编号交还
        Assert.Equal(TitleBookResult.Ok, book.Put(TitleCategory.General, 5, Item(TitleFixtures.General0), 10, out replaced));
        Assert.Equal(TitleFixtures.General5, replaced);
        Assert.Equal((TitleFixtures.General0, 1, 2), (book.CodeAt(TitleCategory.General, 5), book.TotalCount, book.Version));
        Assert.False(book.Contains(TitleFixtures.General5));
    }

    [Fact]
    public void PuttingChecksTheLockFirstThenTheLevelThenTheDespairLevelThenTheSlot()
    {
        TitleBook book = NewBook();
        // 锁着的页(特殊分类第 0 页):等级不够、物品也对不上,先报 12
        Assert.Equal(TitleBookResult.SectionLocked, book.CheckPut(TitleCategory.Specific, 0, Item(TitleFixtures.General0), 1));
        Assert.Equal(TitleBookResult.SectionLocked, book.CheckPut(TitleCategory.Specific, 1, null, 99));
        // 页没锁:等级不够先于"对不上"
        Assert.Equal(TitleBookResult.LevelTooLow, book.CheckPut(TitleCategory.General, 0, Item(TitleFixtures.General0), 9));
        Assert.Equal(TitleBookResult.LevelTooLow, book.CheckPut(TitleCategory.General, 0, Item(TitleFixtures.General1), 9));
        // 绝望分类的等级线在 70,先于"对不上"
        Assert.Equal(TitleBookResult.DespairLevel, book.CheckPut(TitleCategory.Despair, 0, Item(TitleFixtures.Despair0), 69));
        Assert.Equal(TitleBookResult.DespairLevel, book.CheckPut(TitleCategory.Despair, 0, Item(TitleFixtures.General0), 69));
        Assert.Equal(TitleBookResult.LevelTooLow, book.CheckPut(TitleCategory.Despair, 0, Item(TitleFixtures.Despair0), 9));   // 10 的线先查
        // 最后才是这一格收不收
        Assert.Equal(TitleBookResult.Mismatch, book.CheckPut(TitleCategory.General, 0, Item(TitleFixtures.General1), 10));
        Assert.Equal(TitleBookResult.Mismatch, book.CheckPut(TitleCategory.General, 0, null, 10));
        Assert.Equal(TitleBookResult.Ok, book.CheckPut(TitleCategory.General, 0, Item(TitleFixtures.General0), 10));
    }

    [Fact]
    public void TheLevelLinesAreExactlyTenAndSeventy()
    {
        TitleBook book = NewBook();
        Assert.Equal((10, 70), (TitleBook.MinLevel, TitleBook.DespairMinLevel));
        Assert.Equal(TitleBookResult.LevelTooLow, book.CheckPut(TitleCategory.General, 0, Item(TitleFixtures.General0), 9));
        Assert.Equal(TitleBookResult.Ok, book.CheckPut(TitleCategory.General, 0, Item(TitleFixtures.General0), 10));
        Assert.Equal(TitleBookResult.DespairLevel, book.CheckPut(TitleCategory.Despair, 0, Item(TitleFixtures.Despair0), 69));
        Assert.Equal(TitleBookResult.Ok, book.CheckPut(TitleCategory.Despair, 0, Item(TitleFixtures.Despair0), 70));
        Assert.Equal(TitleBookResult.Ok, book.CheckPut(TitleCategory.General, 0, Item(TitleFixtures.General0), 69));          // 70 的线只管绝望分类
    }

    [Fact]
    public void ASlotOutsideTheCapacityOrTheDefinitionIsAMismatch()
    {
        TitleBook book = NewBook();
        Assert.Equal(TitleBookResult.Mismatch, book.CheckPut(TitleCategory.General, 70, Item(TitleFixtures.General0), 10));
        Assert.Equal(TitleBookResult.Mismatch, book.CheckPut(TitleCategory.General, -1, Item(TitleFixtures.General0), 10));
        Assert.Equal(TitleBookResult.Mismatch, book.CheckPut(TitleCategory.General, 6, Item(TitleFixtures.General0), 10));      // 在上限里、定义外
        Assert.Equal(TitleBookResult.Mismatch, book.CheckPut((TitleCategory)9, 0, Item(TitleFixtures.General0), 10));
    }

    [Fact]
    public void AnItemThatIsNotEquipmentCannotGoIntoTheBookEvenWhereTheSlotTakesAnything()
    {
        TitleBook book = NewBook();
        Assert.Equal(TitleBookResult.Mismatch, book.CheckPut(TitleCategory.Event, 0, Item(TitleFixtures.StackableCode), 10));
        Assert.Equal(TitleBookResult.Ok, book.CheckPut(TitleCategory.Event, 0, Item(TitleFixtures.WeaponCode), 10));            // 规则层不看部位(宿主层的 TitleSession 才收紧)
    }

    [Fact]
    public void ARefusedPutChangesNothingAndReportsNothingDisplaced()
    {
        TitleBook book = NewBook();
        book.Put(TitleCategory.General, 5, Item(TitleFixtures.General5), 10, out _);
        int version = book.Version;
        Assert.Equal(TitleBookResult.LevelTooLow, book.Put(TitleCategory.General, 5, Item(TitleFixtures.General0), 9, out int replaced));
        Assert.Equal(0, replaced);
        Assert.Equal((TitleFixtures.General5, version), (book.CodeAt(TitleCategory.General, 5), book.Version));
    }

    // ------------------------------------------------------------------ 直接放(成就奖励)

    [Fact]
    public void PutOnlyWritesTheFirstSlotThatListsTheItem()
    {
        TitleBook book = NewBook();
        Assert.True(book.PutOnly(TitleFixtures.General2, TitleFixtures.Lookup(), out TitleCategory c, out int s));
        Assert.Equal((TitleCategory.General, 2, TitleFixtures.General2), (c, s, book.CodeAt(TitleCategory.General, 2)));
        Assert.True(book.PutOnly(TitleFixtures.Specific1, TitleFixtures.Lookup(), out c, out s));
        Assert.Equal((TitleCategory.Specific, 1), (c, s));
    }

    [Fact]
    public void PutOnlyOverwritesAnOccupiedSlotWithoutGivingAnythingBack()
    {
        TitleBook book = NewBook();
        Func<int, ItemInfo> lookup = TitleFixtures.Lookup();
        Assert.True(book.PutOnly(TitleFixtures.General2, lookup, out _, out _));
        Assert.True(book.PutOnly(TitleFixtures.General2Alt, lookup, out TitleCategory c, out int s));                  // 同一格另一个候选
        Assert.Equal((TitleCategory.General, 2, TitleFixtures.General2Alt), (c, s, book.CodeAt(TitleCategory.General, 2)));
        Assert.Equal(1, book.TotalCount);
    }

    [Fact]
    public void PutOnlyIgnoresTheLevelAndThePageLock()
    {
        TitleBook book = NewBook();
        Assert.True(book.PutOnly(TitleFixtures.Specific0, TitleFixtures.Lookup(), out TitleCategory c, out int s));    // 特殊分类第 0 页是锁着的
        Assert.Equal((TitleCategory.Specific, 0, TitleFixtures.Specific0), (c, s, book.CodeAt(TitleCategory.Specific, 0)));
        Assert.True(book.PutOnly(TitleFixtures.Despair0, TitleFixtures.Lookup(), out _, out _));                       // 不看绝望分类的等级线
    }

    [Fact]
    public void PutOnlyFailsForAnUnknownItemAndReportsTheDefaultSlot()
    {
        TitleBook book = NewBook();
        Assert.False(book.PutOnly(999, TitleFixtures.Lookup(), out TitleCategory c, out int s));
        Assert.Equal((TitleCategory.General, 0), (c, s));
        Assert.False(book.PutOnly(TitleFixtures.General0, _ => null, out c, out s));                                   // 物品库里找不到
        Assert.Equal((TitleCategory.General, 0), (c, s));
        Assert.False(book.PutOnly(0, TitleFixtures.Lookup(), out _, out _));
        Assert.False(book.PutOnly(TitleFixtures.General0, null, out _, out _));
        Assert.Equal((0, 0), (book.TotalCount, book.Version));
    }

    [Fact]
    public void PutOnlyReportsTheFoundSlotEvenWhenTheItemCannotBeStored()
    {
        // 某一格列出了一个可叠加物品的编号:找得到格、写不进去;调用方拿到的是这一格(不是默认的第 0 格)
        var b = new TokenStreamBuilder();
        b.Tag("[title collection info]").Str("general").Int(1).Ints(0, 0, 9001, 1, 950);
        b.Tag("[title collection info]").Str("specific").Int(2).Ints(0, 0, 9002, 1, 951).Ints(1, 0, 9003, 1, TitleFixtures.StackableCode);
        TitleBook book = NewBook(TitleBookScript.Parse(TitleFixtures.Lines(b)));
        Assert.False(book.PutOnly(TitleFixtures.StackableCode, TitleFixtures.Lookup(), out TitleCategory c, out int s));
        Assert.Equal((TitleCategory.Specific, 1), (c, s));
        Assert.Equal(0, book.TotalCount);
    }

    // ------------------------------------------------------------------ 取出

    [Fact]
    public void TakingChecksTheRangeThenTheCodeThenWhetherTheSlotMayGoBackToTheBag()
    {
        TitleBook book = NewBook();
        book.Restore(TitleCategory.General, 0, TitleFixtures.General0);                                                // 绑了成就的格
        book.Restore(TitleCategory.General, 5, TitleFixtures.General5);                                                // 不绑成就的格
        Assert.Equal(TitleBookResult.NoData, book.CheckTake(TitleCategory.General, 70, TitleFixtures.General0, true));
        Assert.Equal(TitleBookResult.NoData, book.CheckTake(TitleCategory.General, -1, TitleFixtures.General0, false));
        Assert.Equal(TitleBookResult.NoData, book.CheckTake((TitleCategory)9, 0, TitleFixtures.General0, true));
        Assert.Equal(TitleBookResult.Mismatch, book.CheckTake(TitleCategory.General, 1, TitleFixtures.General1, true));     // 空格
        Assert.Equal(TitleBookResult.Mismatch, book.CheckTake(TitleCategory.General, 0, TitleFixtures.General1, false));    // 编号对不上
        Assert.Equal(TitleBookResult.Mismatch, book.CheckTake(TitleCategory.General, 0, 0, false));
        Assert.Equal(TitleBookResult.NotMovable, book.CheckTake(TitleCategory.General, 0, TitleFixtures.General0, true));   // 绑成就:不能回背包
        Assert.Equal(TitleBookResult.Ok, book.CheckTake(TitleCategory.General, 0, TitleFixtures.General0, false));          // 但能直接穿
        Assert.Equal(TitleBookResult.Ok, book.CheckTake(TitleCategory.General, 5, TitleFixtures.General5, true));
    }

    [Fact]
    public void RemovingClearsOneSlotAndReturnsItsCode()
    {
        TitleBook book = NewBook();
        book.Restore(TitleCategory.General, 0, TitleFixtures.General0);
        int version = book.Version;
        Assert.Equal(TitleFixtures.General0, book.Remove(TitleCategory.General, 0));
        Assert.Equal((0, version + 1), (book.TotalCount, book.Version));
        Assert.Equal(0, book.Remove(TitleCategory.General, 0));                                                        // 空格:什么也不变
        Assert.Equal(version + 1, book.Version);
        Assert.Equal(0, book.Remove(TitleCategory.General, 70));
        Assert.Equal(0, book.Remove((TitleCategory)9, 0));
    }

    // ------------------------------------------------------------------ 查看与读档

    [Fact]
    public void EntriesAreOrderedByCategoryThenSlotAndTryFindReturnsTheFirstHolder()
    {
        TitleBook book = NewBook();
        book.Restore(TitleCategory.Specific, 1, 907);
        book.Restore(TitleCategory.General, 4, 904);
        book.Restore(TitleCategory.General, 0, 901);
        book.Restore(TitleCategory.Event, 0, 970);
        book.Restore(TitleCategory.Event, 3, 901);
        Assert.Equal(new[] { (0, 0, 901), (0, 4, 904), (1, 1, 907), (3, 0, 970), (3, 3, 901) },
            book.Entries().Select(e => ((int)e.Category, e.Slot, e.Code)).ToArray());
        Assert.Equal((5, 2, 1, 0, 2), (book.TotalCount, book.Count(TitleCategory.General), book.Count(TitleCategory.Specific), book.Count(TitleCategory.Despair), book.Count(TitleCategory.Event)));
        Assert.True(book.TryFind(901, out TitleCategory c, out int s));
        Assert.Equal((TitleCategory.General, 0), (c, s));
        Assert.False(book.TryFind(0, out _, out _));
        Assert.False(book.TryFind(555, out _, out s));
        Assert.Equal(-1, s);
        Assert.Equal(0, book.Count((TitleCategory)9));
    }

    [Fact]
    public void RestoreWritesWithoutTheRulesButKeepsToTheCapacityAndPositiveCodes()
    {
        TitleBook book = NewBook();
        Assert.True(book.Restore(TitleCategory.General, 69, 777));                                                      // 定义外、上限内:写得进去
        Assert.False(book.Restore(TitleCategory.General, 70, 777));
        Assert.False(book.Restore(TitleCategory.General, 0, 0));
        Assert.False(book.Restore(TitleCategory.General, 0, -5));
        Assert.False(book.Restore((TitleCategory)9, 0, 5));
        Assert.Equal((1, 777), (book.TotalCount, book.CodeAt(TitleCategory.General, 69)));
        int version = book.Version;
        Assert.True(book.Restore(TitleCategory.General, 69, 777));                                                      // 写同一个值不算变化
        Assert.Equal(version, book.Version);
    }

    [Fact]
    public void ClearEmptiesTheBookAndCountsAsAChangeOnlyWhenThereWasSomething()
    {
        TitleBook book = NewBook();
        book.Clear();
        Assert.Equal(0, book.Version);
        book.Restore(TitleCategory.Specific, 0, 906);
        book.Restore(TitleCategory.Event, 2, 970);
        int version = book.Version;
        book.Clear();
        Assert.Equal((0, version + 1), (book.TotalCount, book.Version));
    }

    [Fact]
    public void ReadsOutsideTheCapacityAnswerZero()
    {
        TitleBook book = NewBook();
        Assert.Equal(0, book.CodeAt(TitleCategory.General, 70));
        Assert.Equal(0, book.CodeAt(TitleCategory.General, -1));
        Assert.Equal(0, book.CodeAt((TitleCategory)9, 0));
    }

    [Fact]
    public void ConstructionRejectsANullDefinition()
    {
        Assert.Throws<ArgumentNullException>(() => new TitleBook(null));
    }
}
