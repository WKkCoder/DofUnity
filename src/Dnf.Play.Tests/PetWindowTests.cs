using Dnf.Game;

namespace Dnf.Play.Tests;

/// <summary>
/// 城镇宠物窗口:名册数据与出战/收起/喂食的操作入口,全部走状态层接口(PetSession),这里只验窗口自己的那一层。
/// 验收口径:喂食后的饱食变化经状态层读回;喂的是出战的那只、不是光标处的那只(喂错宠物的口子);饲料只在喂进去时才扣。
/// 宠物与物品编号、饱食数值全是合成的。
/// </summary>
public class PetWindowTests
{
    private static int Stomach(PetTownKit.Rig rig, int uid) => rig.Session.Roster.Find(uid).Stomach(rig.Clock.NowSeconds);

    private static int RowY(int slot) => PetWindow.RowsY + slot * (PetWindow.RowHeight + PetWindow.RowGap);

    private static int ButtonY(int slot) => PetWindow.ButtonY + slot * (PetWindow.ButtonHeight + PetWindow.ButtonGap);

    /// <summary>1 号(普通型、饱食 40)出战,2 号(普通型、饱食 55)在名册里。</summary>
    private static PetTownKit.Rig TwoPets(bool fighting = false)
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(1, fighting, (1, PetTownKit.ItemFull, 0, 40), (2, PetTownKit.ItemPlain, 0, 55));
        return rig;
    }

    // ------------------------------------------------------------ 名册数据

    [Fact]
    public void RowsListEveryPetWithItsLevelExperienceSatietyAndWhichOneIsOut()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(2, false, (1, PetTownKit.ItemFull, 35, 40), (2, PetTownKit.ItemPlain, 0, 55), (3, PetTownKit.ItemGrow, 0, 10));

        List<PetWindowRow> rows = rig.Window.Rows();
        Assert.Equal(3, rows.Count);
        PetWindowRow a = rows[0];
        Assert.Equal((1, PetTownKit.ItemFull, PetTownKit.PetFull, PetTownKit.FullName), (a.Uid, a.ItemCode, a.PetId, a.Name));
        Assert.Equal((3, 5, 35, 40), (a.Level, a.MaxLevel, a.Exp, a.Stomach));               // 经验 35:经验表 10/30/60/100 → 3 级
        Assert.Equal((false, false, true), (a.Starved, a.Equipped, a.Feedable));

        PetWindowRow b = rows[1];
        Assert.Equal((2, PetTownKit.PetPlain, 1, 55), (b.Uid, b.PetId, b.Level, b.Stomach));
        Assert.Equal((false, true, true), (b.Starved, b.Equipped, b.Feedable));              // 只有出战的那只带"出战"标记

        PetWindowRow c = rows[2];
        Assert.Equal((3, PetTownKit.PetGrow, 100), (c.Uid, c.PetId, c.Stomach));             // 选择成长型:饱食恒满、不吃饲料
        Assert.Equal((false, false, false), (c.Starved, c.Equipped, c.Feedable));
    }

    [Fact]
    public void RowsAreSettledAgainstTheInjectedClockEveryTimeTheyAreAsked()
    {
        var rig = TwoPets();
        Assert.Equal((40, 55), (rig.Window.Rows()[0].Stomach, rig.Window.Rows()[1].Stomach));

        rig.Clock.Advance(3600);                                    // 出战的在回补、没出战的在收着:都是每 360 秒 +1,一小时 +10
        List<PetWindowRow> later = rig.Window.Rows();
        Assert.Equal((50, 65), (later[0].Stomach, later[1].Stomach));

        // 消耗中:每 60 秒 −1
        var fighting = TwoPets(fighting: true);
        fighting.Clock.Advance(600);
        Assert.Equal(30, fighting.Window.Rows()[0].Stomach);
    }

    [Fact]
    public void ARowIsStarvedOnlyOnceTheSatietyDropsBelowOne()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(1, true, (1, PetTownKit.ItemFull, 0, 1));       // 消耗中,饱食 1
        Assert.False(rig.Window.Rows()[0].Starved);                 // 恰好 1 不算
        rig.Clock.Advance(1);                                       // 1 − 1/60 < 1
        PetWindowRow row = rig.Window.Rows()[0];
        Assert.Equal((true, 0), (row.Starved, row.Stomach));        // 整数饱食向零取整是 0
    }

    [Fact]
    public void AnEmptyRosterHasNoRowsNoSelectionAndNothingToDo()
    {
        var rig = new PetTownKit.Rig();
        Assert.Empty(rig.Window.Rows());
        Assert.Null(rig.Window.Selected);
        rig.Window.Show();
        Assert.Equal((0, 0), (rig.Window.Cursor, rig.Window.ScrollRow));
        rig.Window.MoveCursor(5);
        Assert.Equal((0, 0), (rig.Window.Cursor, rig.Window.ScrollRow));
        Assert.False(rig.Window.SelectUid(1));

        Assert.Equal(PetWindowResult.NoPet, rig.Window.Equip());
        Assert.Equal(PetWindowResult.NoPet, rig.Window.ToggleEquip());
        Assert.Equal(PetWindowResult.NoPet, rig.Window.Unequip());
        Assert.Equal(PetWindowResult.NoPet, rig.Window.Feed(PetTownKit.Bag((PetTownKit.FeedItem, 1))));
    }

    // ------------------------------------------------------------ 打开、光标、滚动

    [Fact]
    public void OpeningPutsTheCursorOnTheEquippedPetOrTheFirstOne()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(2, false, (1, PetTownKit.ItemFull, 0, 100), (2, PetTownKit.ItemPlain, 0, 100));
        Assert.False(rig.Window.Open);
        rig.Window.Show();
        Assert.True(rig.Window.Open);
        Assert.Equal(2, rig.Window.Selected.Uid);

        rig.Window.Hide();
        Assert.False(rig.Window.Open);
        rig.Session.Unequip(0);
        rig.Window.Show();                                          // 没有出战的:第一只
        Assert.Equal(1, rig.Window.Selected.Uid);

        rig.Window.Toggle();
        Assert.False(rig.Window.Open);
        rig.Session.Equip(2, 0, false);
        rig.Window.Toggle();
        Assert.Equal((true, 2), (rig.Window.Open, rig.Window.Selected.Uid));
    }

    [Fact]
    public void TheCursorIsClampedNotWrappedAndTheListScrollsToKeepItInView()
    {
        var rig = new PetTownKit.Rig();
        var pets = Enumerable.Range(1, 8).Select(uid => (uid, PetTownKit.ItemPlain, 0, 100)).ToArray();
        rig.Restore(8, false, pets);                                // 8 只,出战的是最后一只,一屏只看 6 行
        rig.Window.Show();
        Assert.Equal((7, 2), (rig.Window.Cursor, rig.Window.ScrollRow));   // 光标出了屏,列表滚到它刚好在最下一行

        rig.Window.MoveCursor(1);
        Assert.Equal((7, 2), (rig.Window.Cursor, rig.Window.ScrollRow));   // 到底不循环
        rig.Window.MoveCursor(-1);
        Assert.Equal((6, 2), (rig.Window.Cursor, rig.Window.ScrollRow));
        rig.Window.MoveCursor(-4);                                  // 到第 3 行(序号 2):还在屏内,不滚
        Assert.Equal((2, 2), (rig.Window.Cursor, rig.Window.ScrollRow));
        rig.Window.MoveCursor(-1);                                  // 上出屏:往回滚
        Assert.Equal((1, 1), (rig.Window.Cursor, rig.Window.ScrollRow));
        rig.Window.MoveCursor(-100);
        Assert.Equal((0, 0), (rig.Window.Cursor, rig.Window.ScrollRow));
        rig.Window.MoveCursor(int.MinValue);
        Assert.Equal((0, 0), (rig.Window.Cursor, rig.Window.ScrollRow));
        rig.Window.MoveCursor(int.MaxValue);
        Assert.Equal((7, 2), (rig.Window.Cursor, rig.Window.ScrollRow));

        Assert.True(rig.Window.SelectUid(1));
        Assert.Equal((0, 0), (rig.Window.Cursor, rig.Window.ScrollRow));
        Assert.True(rig.Window.SelectUid(7));
        Assert.Equal((6, 1), (rig.Window.Cursor, rig.Window.ScrollRow));
        Assert.False(rig.Window.SelectUid(99));                     // 没有这只:光标不动
        Assert.Equal(7, rig.Window.Selected.Uid);
    }

    [Fact]
    public void WhenTheRosterShrinksTheCursorReadsBackAsTheLastRow()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(0, false, (1, PetTownKit.ItemFull, 0, 100), (2, PetTownKit.ItemPlain, 0, 100), (3, PetTownKit.ItemPlain, 0, 100));
        rig.Window.Show();
        rig.Window.MoveCursor(2);
        Assert.Equal(3, rig.Window.Selected.Uid);

        Assert.True(rig.Session.Release(3, 0));
        Assert.Equal((1, 2), (rig.Window.Cursor, rig.Window.Selected.Uid));
        Assert.True(rig.Session.Release(2, 0));
        Assert.True(rig.Session.Release(1, 0));
        Assert.Equal(0, rig.Window.Cursor);
        Assert.Null(rig.Window.Selected);
    }

    // ------------------------------------------------------------ 出战、收起

    [Fact]
    public void EquippingTheCursorPetGoesThroughTheStateLayerAndSwapsTheOldOneOut()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(0, false, (1, PetTownKit.ItemFull, 0, 100), (2, PetTownKit.ItemPlain, 0, 100));
        rig.Window.Show();

        Assert.Equal(PetWindowResult.Equipped, rig.Window.Equip());
        Assert.Equal(1, rig.Session.Equipped.Uid);
        Assert.Equal(PetWindowResult.AlreadyEquipped, rig.Window.Equip());
        Assert.Equal(PetWindowResult.AlreadyEquipped, rig.Window.LastResult);

        rig.Window.MoveCursor(1);
        Assert.Equal(PetWindowResult.Equipped, rig.Window.Equip());                 // 换:旧的先收起
        Assert.Equal(2, rig.Session.Equipped.Uid);
        Assert.Equal(PetState.Stored, rig.Session.Roster.Find(1).State);
        Assert.Equal(PetState.Charging, rig.Session.Roster.Find(2).State);
    }

    [Fact]
    public void ToggleEquipEquipsAnUnequippedCursorPetAndStowsTheEquippedOne()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(0, false, (1, PetTownKit.ItemFull, 0, 100), (2, PetTownKit.ItemPlain, 0, 100));
        rig.Window.Show();

        Assert.Equal(PetWindowResult.Equipped, rig.Window.ToggleEquip());
        Assert.Equal(1, rig.Session.Equipped.Uid);
        Assert.Equal(PetWindowResult.Unequipped, rig.Window.ToggleEquip());         // 光标还在出战的那只上:收起
        Assert.Null(rig.Session.Equipped);

        rig.Window.MoveCursor(1);
        Assert.Equal(PetWindowResult.Equipped, rig.Window.ToggleEquip());
        rig.Window.MoveCursor(-1);                                                  // 光标移到没出战的那只:再切换是换过去,不是收起
        Assert.Equal(PetWindowResult.Equipped, rig.Window.ToggleEquip());
        Assert.Equal(1, rig.Session.Equipped.Uid);
    }

    [Fact]
    public void UnequipStowsTheEquippedPetWhereverTheCursorIsAndNothingEquippedIsNoPet()
    {
        var rig = TwoPets();
        rig.Window.Show();
        rig.Window.MoveCursor(1);                                                   // 光标不在出战的那只上
        Assert.Equal(PetWindowResult.Unequipped, rig.Window.Unequip());
        Assert.Null(rig.Session.Equipped);
        Assert.Equal(PetState.Stored, rig.Session.Roster.Find(1).State);
        Assert.Equal(PetWindowResult.NoPet, rig.Window.Unequip());
        Assert.Equal(PetWindowResult.NoPet, rig.Window.LastResult);
    }

    [Fact]
    public void EquippingWhileTheUserIsFightingStartsTheDrainAndOtherwiseTheCharge()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(0, false, (1, PetTownKit.ItemFull, 0, 100), (2, PetTownKit.ItemPlain, 0, 100));
        rig.Window.Show();
        Assert.False(rig.Window.UserFighting);
        rig.Window.Equip();
        Assert.Equal(PetState.Charging, rig.Session.Equipped.State);

        rig.Window.UserFighting = true;
        rig.Window.MoveCursor(1);
        rig.Window.Equip();
        Assert.Equal(PetState.Draining, rig.Session.Equipped.State);
    }

    // ------------------------------------------------------------ 喂食

    [Fact]
    public void FeedingGoesToTheEquippedPetNotTheOneUnderTheCursor()
    {
        var rig = TwoPets();                                        // 1 号出战(饱食 40),2 号没出战(饱食 55)
        Inventory bag = PetTownKit.Bag((PetTownKit.FeedItem, 3));
        rig.Window.Show();
        Assert.Equal(1, rig.Window.Selected.Uid);
        Assert.True(rig.Window.SelectUid(2));                       // 光标移到没出战的 2 号上再按喂食
        Assert.Equal(2, rig.Window.Selected.Uid);

        Assert.Equal(PetWindowResult.Fed, rig.Window.Feed(bag));
        Assert.Equal(PetWindowResult.Fed, rig.Window.LastResult);
        Assert.Equal(70, Stomach(rig, 1));                          // 出战的那只:40 + 30(状态层读回)
        Assert.Equal(55, Stomach(rig, 2));                          // 光标处的那只一点没动
        Assert.Equal(2, bag.CountOf(PetTownKit.FeedItem));          // 喂进去了,扣一件

        List<PetWindowRow> rows = rig.Window.Rows();
        Assert.Equal((70, 55), (rows[0].Stomach, rows[1].Stomach));
    }

    [Fact]
    public void FeedingUsesTheInjectedClockForTheSatietyItAddsOnTo()
    {
        var rig = TwoPets(fighting: true);                          // 消耗中:每 60 秒 −1
        Inventory bag = PetTownKit.Bag((PetTownKit.FeedItem, 1));
        rig.Clock.Advance(600);                                     // 40 → 30
        Assert.Equal(PetWindowResult.Fed, rig.Window.Feed(bag));
        Assert.Equal(60, Stomach(rig, 1));                          // 30 + 30:先按此刻结算再加,不是 40 + 30
        rig.Clock.Advance(60);
        Assert.Equal(59, Stomach(rig, 1));
    }

    [Fact]
    public void FeedingAStarvedPetRevivesItAndStillCostsOneFeed()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(1, true, (1, PetTownKit.ItemFull, 0, 0));       // 消耗中、饱食 0:饿死
        PetInstance pet = rig.Session.Equipped;
        Assert.True(pet.IsStarved(0));
        Inventory bag = PetTownKit.Bag((PetTownKit.FeedItem, 2));

        Assert.Equal(PetWindowResult.Revived, rig.Window.Feed(bag));
        Assert.Equal(PetWindowResult.Revived, rig.Window.LastResult);
        Assert.False(pet.IsStarved(0));
        Assert.Equal(30, Stomach(rig, 1));
        Assert.Equal(1, bag.CountOf(PetTownKit.FeedItem));
        Assert.False(rig.Window.Rows()[0].Starved);
    }

    [Fact]
    public void FeedingAFullPetIsStillAFeedThatCapsAtOneHundred()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(1, false, (1, PetTownKit.ItemFull, 0, 90));
        Inventory bag = PetTownKit.Bag((PetTownKit.FeedItem, 2));
        Assert.Equal(PetWindowResult.Fed, rig.Window.Feed(bag));
        Assert.Equal(100, Stomach(rig, 1));                         // 90 + 30 夹到 100
        Assert.Equal(PetWindowResult.Fed, rig.Window.Feed(bag));    // 状态层不判"已满":照喂照扣
        Assert.Equal(100, Stomach(rig, 1));
        Assert.Equal(0, bag.CountOf(PetTownKit.FeedItem));
    }

    [Fact]
    public void WhenNothingGetsFedNothingIsTakenFromTheBagAndNoSatietyMoves()
    {
        // 没有出战的宠物
        var none = new PetTownKit.Rig();
        none.Restore(0, false, (1, PetTownKit.ItemFull, 0, 40));
        Inventory bag = PetTownKit.Bag((PetTownKit.FeedItem, 2));
        Assert.Equal(PetWindowResult.NoPet, none.Window.Feed(bag));
        Assert.Equal((40, 2), (Stomach(none, 1), bag.CountOf(PetTownKit.FeedItem)));

        // 出战的是选择成长型:不吃
        var grow = new PetTownKit.Rig();
        grow.Restore(1, false, (1, PetTownKit.ItemGrow, 0, 0));
        Assert.Equal(PetWindowResult.NotFeedable, grow.Window.Feed(bag));
        Assert.Equal(PetWindowResult.NotFeedable, grow.Window.LastResult);
        Assert.Equal(2, bag.CountOf(PetTownKit.FeedItem));

        // 背包里没有饲料:空包、只有别的可叠加物品、包是 null
        var rig = TwoPets();
        Assert.Equal(PetWindowResult.NoFeedItem, rig.Window.Feed(new Inventory()));
        Inventory other = PetTownKit.Bag((PetTownKit.NotFeedItem, 5));
        Assert.Equal(PetWindowResult.NoFeedItem, rig.Window.Feed(other));
        Assert.Equal(PetWindowResult.NoFeedItem, rig.Window.Feed(null));
        Assert.Equal(5, other.CountOf(PetTownKit.NotFeedItem));
        Assert.Equal(40, Stomach(rig, 1));
        Assert.Equal(PetWindowResult.NoFeedItem, rig.Window.LastResult);
    }

    [Fact]
    public void TheFirstFeedStackInTheBagIsUsedFirstAndTheDefaultTestIgnoresOtherItems()
    {
        var rig = TwoPets();
        Inventory bag = PetTownKit.Bag((PetTownKit.NotFeedItem, 5), (PetTownKit.FeedItemB, 2), (PetTownKit.FeedItem, 4), (PetTownKit.ItemFull, 1));
        Assert.Equal(PetTownKit.FeedItemB, rig.Window.FeedItemIn(bag).Code);        // 按放入的先后,第一件饲料
        Assert.Equal(6, rig.Window.FeedCount(bag));                                 // 两种饲料共 2 + 4,别的不算
        Assert.True(rig.Window.CanFeed(bag));

        rig.Window.Feed(bag);
        rig.Window.Feed(bag);
        Assert.Equal((0, 4, 5), (bag.CountOf(PetTownKit.FeedItemB), bag.CountOf(PetTownKit.FeedItem), bag.CountOf(PetTownKit.NotFeedItem)));
        Assert.Equal(PetTownKit.FeedItem, rig.Window.FeedItemIn(bag).Code);         // 第一种用完了,轮到下一种
        rig.Window.Feed(bag);
        Assert.Equal(3, bag.CountOf(PetTownKit.FeedItem));
        Assert.Equal(1, bag.CountOf(PetTownKit.ItemFull));                          // 宠物物品不动
    }

    [Fact]
    public void ACustomFeedTestNarrowsWhatCountsAsFeed()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(1, false, (1, PetTownKit.ItemFull, 0, 40));
        var window = new PetWindow(rig.Session, rig.Clock, item => item.Code == PetTownKit.FeedItem);   // 宿主收紧成某一个编号
        Inventory bag = PetTownKit.Bag((PetTownKit.FeedItemB, 3), (PetTownKit.FeedItem, 1));

        Assert.Equal(PetTownKit.FeedItem, window.FeedItemIn(bag).Code);
        Assert.Equal(1, window.FeedCount(bag));
        Assert.Equal(PetWindowResult.Fed, window.Feed(bag));
        Assert.Equal((0, 3), (bag.CountOf(PetTownKit.FeedItem), bag.CountOf(PetTownKit.FeedItemB)));
        Assert.Equal(PetWindowResult.NoFeedItem, window.Feed(bag));                 // 只剩不算饲料的 981
        Assert.Equal(70, Stomach(rig, 1));
    }

    [Fact]
    public void CanFeedNeedsAnEquippedFeedablePetAndAFeedInTheBag()
    {
        Inventory feed = PetTownKit.Bag((PetTownKit.FeedItem, 1));
        var rig = TwoPets();
        Assert.True(rig.Window.CanFeed(feed));
        Assert.False(rig.Window.CanFeed(new Inventory()));
        Assert.False(rig.Window.CanFeed(null));

        rig.Session.Unequip(0);
        Assert.False(rig.Window.CanFeed(feed));

        var grow = new PetTownKit.Rig();
        grow.Restore(1, false, (1, PetTownKit.ItemGrow, 0, 0));
        Assert.False(grow.Window.CanFeed(feed));
        Assert.Equal(0, grow.Window.FeedCount(null));
    }

    [Fact]
    public void TheDefaultFeedTestWantsAStackableOfTheFeedType()
    {
        Assert.True(PetWindow.IsFeedItem(PetTownKit.Items[PetTownKit.FeedItem]));
        Assert.False(PetWindow.IsFeedItem(PetTownKit.Items[PetTownKit.NotFeedItem]));   // 可叠加但类型不是
        Assert.False(PetWindow.IsFeedItem(PetTownKit.Items[PetTownKit.ItemFull]));      // 不是可叠加物品
        Assert.False(PetWindow.IsFeedItem(null));
        var equipmentOfFeedType = new ItemInfo(990, "equipment/test/x.equ", ItemKind.Equipment) { StackableType = PetTownKit.FeedType };
        Assert.False(PetWindow.IsFeedItem(equipmentOfFeedType));                        // 类型字段对装备不起作用
    }

    [Fact]
    public void FeedIsFoundCountedAndSpentInEveryTabOfTheBag()
    {
        // 宿主传的判断什么都可以认;这里让它认四个页签里各一种:装备页(宠物物品 950)、消耗品页(饲料 980)、材料页、任务页(后两种是合成的可叠加物品)
        var material = new ItemInfo(994, "stackable/test/m994.stk", ItemKind.Stackable) { StackableType = StackableTypes.Material };
        var quest = new ItemInfo(995, "stackable/test/q995.stk", ItemKind.Stackable) { StackableType = StackableTypes.Quest };
        ItemInfo equipment = PetTownKit.Items[PetTownKit.ItemFull];
        ItemInfo consumable = PetTownKit.Items[PetTownKit.FeedItem];
        Assert.Equal(new[] { InventoryTab.Equipment, InventoryTab.Consumable, InventoryTab.Material, InventoryTab.Quest },
            new[] { Inventory.TabOf(equipment), Inventory.TabOf(consumable), Inventory.TabOf(material), Inventory.TabOf(quest) });

        var rig = new PetTownKit.Rig();
        rig.Restore(1, false, (1, PetTownKit.ItemFull, 0, 10));
        foreach ((ItemInfo item, int count) in new[] { (equipment, 1), (consumable, 2), (material, 3), (quest, 4) })
        {
            // 背包里只有这一种、判断也只认这一种:不管它在哪一页都要找得到、数得到、喂得下去、扣得掉
            var bag = new Inventory();
            Assert.Equal(count, bag.Add(item, count));
            var window = new PetWindow(rig.Session, rig.Clock, candidate => candidate.Code == item.Code);
            Assert.Equal(item.Code, window.FeedItemIn(bag).Code);
            Assert.Equal(count, window.FeedCount(bag));
            Assert.True(window.CanFeed(bag));
            Assert.Equal(PetWindowResult.Fed, window.Feed(bag));
            Assert.Equal(count - 1, bag.CountOf(item.Code));
        }
    }

    [Fact]
    public void AnOverscrolledCursorStaysOnTheLastRowWhenTheRosterGrowsLater()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(0, false, (1, PetTownKit.ItemFull, 0, 100), (2, PetTownKit.ItemPlain, 0, 100), (3, PetTownKit.ItemPlain, 0, 100));
        rig.Window.Show();
        rig.Window.MoveCursor(100);                                 // 往下推过头:停在最后一行,不是记着一个 100
        Assert.Equal(3, rig.Window.Selected.Uid);

        PetInstance fourth = rig.Adopt(PetTownKit.ItemPlain);       // 名册后来变长:光标仍在原来的那一行,不会跟着跳到新来的那只上
        Assert.Equal(4, fourth.Uid);
        Assert.Equal((2, 3), (rig.Window.Cursor, rig.Window.Selected.Uid));
    }

    [Fact]
    public void EquippingAndUnequippingSettleTheSatietyAtTheClockTimeNotAtTimeZero()
    {
        var rig = TwoPets(fighting: true);                          // 1 号出战、消耗中(饱食 40),2 号收着(饱食 55)
        rig.Window.UserFighting = true;
        rig.Window.Show();
        rig.Clock.Advance(600);                                     // 十分钟:1 号 −10,2 号 +1.67
        Assert.Equal((30, 56), (Stomach(rig, 1), Stomach(rig, 2)));

        rig.Window.MoveCursor(1);
        Assert.Equal(PetWindowResult.Equipped, rig.Window.Equip()); // 换成 2 号:1 号在这一刻收起、2 号在这一刻开始消耗
        Assert.Equal((30, 56), (Stomach(rig, 1), Stomach(rig, 2))); // 换的那一刻谁也没有凭空变

        rig.Clock.Advance(600);                                     // 2 号消耗十分钟:56.67 → 46.67;1 号收着 +1.67
        Assert.Equal((31, 46), (Stomach(rig, 1), Stomach(rig, 2)));

        Assert.Equal(PetWindowResult.Unequipped, rig.Window.Unequip());   // 收起 2 号:先结算到这一刻
        Assert.Equal(46, Stomach(rig, 2));
        rig.Clock.Advance(3600);                                    // 都收着一小时:各 +10
        Assert.Equal((41, 56), (Stomach(rig, 1), Stomach(rig, 2)));
    }

    [Fact]
    public void TheFeedableCheckLooksAtTheEquippedPetNotAtTheOneUnderTheCursor()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(1, false, (1, PetTownKit.ItemFull, 0, 40), (2, PetTownKit.ItemGrow, 0, 0));   // 出战的是普通型,2 号是选择成长型
        Inventory bag = PetTownKit.Bag((PetTownKit.FeedItem, 2));
        rig.Window.Show();
        Assert.True(rig.Window.SelectUid(2));                       // 光标移到不吃饲料的那只上

        Assert.True(rig.Window.CanFeed(bag));                       // 能不能喂、喂不喂得下去,都看出战的那只
        Assert.Equal(PetWindowResult.Fed, rig.Window.Feed(bag));
        Assert.Equal((70, 1), (Stomach(rig, 1), bag.CountOf(PetTownKit.FeedItem)));
    }

    [Fact]
    public void ANonFeedablePetIsRefusedBeforeTheBagIsLookedAt()
    {
        var grow = new PetTownKit.Rig();
        grow.Restore(1, false, (1, PetTownKit.ItemGrow, 0, 0));
        Assert.Equal(PetWindowResult.NotFeedable, grow.Window.Feed(new Inventory()));   // 包里没有饲料,也先说"这只不吃"
        Assert.Equal(PetWindowResult.NotFeedable, grow.Window.Feed(null));
        Assert.Equal(PetWindowResult.NotFeedable, grow.Window.LastResult);
    }

    [Fact]
    public void FeedingGoesToTheEquippedPetEvenWhenItIsNeitherFirstInTheRosterNorUnderTheCursor()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(2, false, (1, PetTownKit.ItemFull, 0, 40), (2, PetTownKit.ItemPlain, 0, 50), (3, PetTownKit.ItemFull, 0, 60));   // 出战的是中间的 2 号
        Inventory bag = PetTownKit.Bag((PetTownKit.FeedItem, 3));
        rig.Window.Show();
        Assert.True(rig.Window.SelectUid(3));                       // 光标在 3 号上;名册第一只是 1 号

        Assert.Equal(PetWindowResult.Fed, rig.Window.Feed(bag));
        Assert.Equal((40, 80, 60), (Stomach(rig, 1), Stomach(rig, 2), Stomach(rig, 3)));    // 只有出战的 2 号加了 30
        Assert.Equal(2, bag.CountOf(PetTownKit.FeedItem));

        // 名册第一只是不吃饲料的选择成长型、出战的是后面的普通型:能喂、喂得下去
        var growFirst = new PetTownKit.Rig();
        growFirst.Restore(2, false, (1, PetTownKit.ItemGrow, 0, 0), (2, PetTownKit.ItemFull, 0, 40));
        Assert.True(growFirst.Window.CanFeed(bag));
        Assert.Equal(PetWindowResult.Fed, growFirst.Window.Feed(bag));
        Assert.Equal((100, 70), (Stomach(growFirst, 1), Stomach(growFirst, 2)));
    }

    [Fact]
    public void FeedingSettlesTheSatietyAtTheClockTimeFirstSoTheCapAndTheStarvedCheckSeeTheCurrentValue()
    {
        // 消耗中、饱食 90,十分钟后是 80:喂 30 是 110,夹到 100(若按 0 时刻的 90 先喂、再扣十分钟,结果会是 90)
        var rig = new PetTownKit.Rig();
        rig.Restore(1, true, (1, PetTownKit.ItemFull, 0, 90));
        Inventory bag = PetTownKit.Bag((PetTownKit.FeedItem, 4));
        rig.Clock.Advance(600);
        Assert.Equal(PetWindowResult.Fed, rig.Window.Feed(bag));
        Assert.Equal(100, Stomach(rig, 1));

        // 消耗中、饱食 5,十分钟后饿死(降到 0):喂下去是复活,不是普通的"吃下"
        var starving = new PetTownKit.Rig();
        starving.Restore(1, true, (1, PetTownKit.ItemFull, 0, 5));
        starving.Clock.Advance(600);
        Assert.True(starving.Session.Equipped.IsStarved(starving.Clock.NowSeconds));
        Assert.Equal(PetWindowResult.Revived, starving.Window.Feed(bag));
        Assert.Equal(30, Stomach(starving, 1));
    }

    // ------------------------------------------------------------ 点击

    [Fact]
    public void TheWindowRectangleIncludesItsTopLeftEdgeAndExcludesItsBottomRightEdge()
    {
        int x = PetWindow.WindowX;
        int y = PetWindow.WindowY;
        int r = x + PetWindow.WindowWidth;
        int b = y + PetWindow.WindowHeight;
        Assert.True(PetWindow.InWindow(x, y));
        Assert.True(PetWindow.InWindow(r - 1, b - 1));
        Assert.False(PetWindow.InWindow(x - 1, y));
        Assert.False(PetWindow.InWindow(x, y - 1));
        Assert.False(PetWindow.InWindow(r, b - 1));
        Assert.False(PetWindow.InWindow(r - 1, b));
    }

    [Fact]
    public void RowsAreHitOnlyInsideTheirRectanglesAndNotInTheGapsOrEmptyRows()
    {
        var rig = new PetTownKit.Rig();
        rig.Restore(0, false, Enumerable.Range(1, 8).Select(uid => (uid, PetTownKit.ItemPlain, 0, 100)).ToArray());
        PetWindow w = rig.Window;
        int x = PetWindow.RowsX;

        Assert.Equal(0, w.RowAt(x, RowY(0)));
        Assert.Equal(0, w.RowAt(x + PetWindow.RowWidth - 1, RowY(0) + PetWindow.RowHeight - 1));
        Assert.Equal(-1, w.RowAt(x + PetWindow.RowWidth, RowY(0)));                  // 右边缘不含
        Assert.Equal(-1, w.RowAt(x - 1, RowY(0)));
        Assert.Equal(-1, w.RowAt(x, PetWindow.RowsY - 1));
        Assert.Equal(-1, w.RowAt(x, RowY(0) + PetWindow.RowHeight));                 // 行距里
        Assert.Equal(-1, w.RowAt(x, RowY(1) - 1));
        Assert.Equal(1, w.RowAt(x, RowY(1)));
        Assert.Equal(5, w.RowAt(x, RowY(5)));
        Assert.Equal(-1, w.RowAt(x, RowY(6)));                                       // 一屏只有 6 行

        w.MoveCursor(100);                                                           // 滚到最后:屏上第 0 行是名册第 2 只
        Assert.Equal(2, w.RowAt(x, RowY(0)));
        Assert.Equal(7, w.RowAt(x, RowY(5)));

        var small = TwoPets();
        Assert.Equal(1, small.Window.RowAt(x, RowY(1)));
        Assert.Equal(-1, small.Window.RowAt(x, RowY(2)));                            // 名册只有两只:第三行是空的
    }

    [Fact]
    public void ButtonsAreHitOnlyInsideTheirRectanglesInTheOrderEquipFeedClose()
    {
        int x = PetWindow.ButtonX;
        Assert.Equal(PetWindowButton.Equip, PetWindow.ButtonAt(x, ButtonY(0)));
        Assert.Equal(PetWindowButton.Equip, PetWindow.ButtonAt(x + PetWindow.ButtonWidth - 1, ButtonY(0) + PetWindow.ButtonHeight - 1));
        Assert.Equal(PetWindowButton.None, PetWindow.ButtonAt(x + PetWindow.ButtonWidth, ButtonY(0)));
        Assert.Equal(PetWindowButton.None, PetWindow.ButtonAt(x - 1, ButtonY(0)));
        Assert.Equal(PetWindowButton.None, PetWindow.ButtonAt(x, PetWindow.ButtonY - 1));
        Assert.Equal(PetWindowButton.None, PetWindow.ButtonAt(x, ButtonY(0) + PetWindow.ButtonHeight));   // 按钮之间的缝
        Assert.Equal(PetWindowButton.Feed, PetWindow.ButtonAt(x, ButtonY(1)));
        Assert.Equal(PetWindowButton.Feed, PetWindow.ButtonAt(x, ButtonY(2) - 1 - PetWindow.ButtonGap));
        Assert.Equal(PetWindowButton.Close, PetWindow.ButtonAt(x, ButtonY(2)));
        Assert.Equal(PetWindowButton.None, PetWindow.ButtonAt(x, ButtonY(3)));
    }

    [Fact]
    public void ClicksOnRowsSelectAndClicksOnButtonsOperateAndOnlyAnOpenWindowTakesClicks()
    {
        var rig = TwoPets();
        PetWindow w = rig.Window;
        Inventory bag = PetTownKit.Bag((PetTownKit.FeedItem, 3));

        // 窗口没开:点击不被接住,什么也没发生
        Assert.False(w.Click(PetWindow.ButtonX, ButtonY(1), bag));
        Assert.Equal((40, 3), (Stomach(rig, 1), bag.CountOf(PetTownKit.FeedItem)));

        w.Show();
        Assert.False(w.Click(PetWindow.WindowX - 1, PetWindow.WindowY, bag));        // 窗口外
        Assert.True(w.Click(PetWindow.WindowX + 1, PetWindow.WindowY + PetWindow.WindowHeight - 2, bag));   // 窗口里的空白处:接住但不做事
        Assert.Equal((3, 40), (bag.CountOf(PetTownKit.FeedItem), Stomach(rig, 1)));

        // 点第二行:光标过去,不改出战
        Assert.True(w.Click(PetWindow.RowsX + 3, RowY(1) + 3, bag));
        Assert.Equal((2, 1), (w.Selected.Uid, rig.Session.Equipped.Uid));

        // 点"出战/收起":光标在没出战的 2 号上 → 换成 2 号
        Assert.True(w.Click(PetWindow.ButtonX + 3, ButtonY(0) + 3, bag));
        Assert.Equal(2, rig.Session.Equipped.Uid);
        Assert.Equal(PetWindowResult.Equipped, w.LastResult);
        // 再点一次:光标在出战的 2 号上 → 收起
        Assert.True(w.Click(PetWindow.ButtonX + 3, ButtonY(0) + 3, bag));
        Assert.Null(rig.Session.Equipped);
        Assert.Equal(PetWindowResult.Unequipped, w.LastResult);

        // 点"喂食":喂出战的那只(先让 1 号出战,光标留在 2 号上)
        rig.Session.Equip(1, 0, false);
        Assert.True(w.Click(PetWindow.ButtonX + 3, ButtonY(1) + 3, bag));
        Assert.Equal((70, 55, 2), (Stomach(rig, 1), Stomach(rig, 2), bag.CountOf(PetTownKit.FeedItem)));
        Assert.Equal(PetWindowResult.Fed, w.LastResult);

        // 点"关闭"
        Assert.True(w.Click(PetWindow.ButtonX + 3, ButtonY(2) + 3, bag));
        Assert.False(w.Open);
    }

    [Fact]
    public void TheConstructorNeedsASessionAndAClock()
    {
        var rig = new PetTownKit.Rig();
        Assert.Throws<ArgumentNullException>(() => new PetWindow(null, rig.Clock));
        Assert.Throws<ArgumentNullException>(() => new PetWindow(rig.Session, null));
    }
}
