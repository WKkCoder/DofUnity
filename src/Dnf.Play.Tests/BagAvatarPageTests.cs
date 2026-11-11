using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using static Dnf.Render.Tests.WardrobeFixture;

namespace Dnf.Play.Tests;

/// <summary>
/// 背包窗口的装扮页(r15-avatarhost,<see cref="BagWindow.Page"/>、<see cref="BagAvatarPage"/>,本项目自己的占位设计,没有原版证据):窗口怎么把页接上、切过去、把按键与点击交给它。
/// 物品与衣柜是合成夹具(<see cref="AvatarHostRig"/>),编号全是合成的 900 起。
/// </summary>
public class BagWindowAvatarPageTests
{
    private static (int X, int Y) Center((int X, int Y, int W, int H) r) => (r.X + r.W / 2, r.Y + r.H / 2);

    // ---------------------------------------------------------------- 接上、切页

    [Fact]
    public void WithoutAvatarsWiredInTheWindowIsJustTheItemsPageAsBefore()
    {
        using var h = new AvatarHostRig();
        var window = new BagWindow();
        Assert.Null(window.Avatars);
        Assert.Equal(BagPage.Items, window.Page);
        Assert.True(window.Show(GameStage.Town));
        Assert.False(window.TogglePage());
        Assert.Equal((BagPage.Items, string.Empty), (window.Page, window.Message));
        (int x, int y) = Center(BagWindow.PageButtonRect);
        Assert.False(window.Click(x, y, h.Progress));                              // 没接装扮页就没有切页按钮
        Assert.Equal(BagPage.Items, window.Page);
    }

    [Fact]
    public void UseAvatarsAddsThePageAndNullTakesItAwayAgainAndBothGoBackToTheItemsPage()
    {
        using var h = new AvatarHostRig();
        BagWindow window = h.OpenWindow();
        Assert.Same(h.Session, window.Avatars.Session);
        Assert.True(window.TogglePage());
        Assert.Equal(BagPage.Avatars, window.Page);

        window.UseAvatars(h.Session);                                              // 重新接上:也回到物品页
        Assert.Equal(BagPage.Items, window.Page);
        Assert.True(window.TogglePage());

        window.UseAvatars(null);
        Assert.Null(window.Avatars);
        Assert.Equal(BagPage.Items, window.Page);
        Assert.False(window.TogglePage());
        Assert.Equal(BagPage.Items, window.Page);
    }

    [Fact]
    public void ThePageOnlyTogglesInAnOpenWindow()
    {
        using var h = new AvatarHostRig();
        var window = new BagWindow();
        window.UseAvatars(h.Session);
        Assert.False(window.TogglePage());                                         // 还没开过
        Assert.True(window.Show(GameStage.Town));
        window.Close();
        Assert.False(window.TogglePage());                                         // 开过又关上了
        Assert.Equal(BagPage.Items, window.Page);
    }

    [Fact]
    public void TheAvatarPageOpensOnlyInTheTownAndSaysWhyInTheDungeon()
    {
        using var h = new AvatarHostRig();
        BagWindow dungeon = h.OpenWindow(GameStage.Dungeon);
        Assert.False(dungeon.TogglePage());
        Assert.Equal((BagPage.Items, "AVATARS: TOWN ONLY"), (dungeon.Page, dungeon.Message));

        BagWindow town = h.OpenWindow(GameStage.Town);
        Assert.True(town.TogglePage());
        Assert.Equal((BagPage.Avatars, string.Empty), (town.Page, town.Message));

        // 同一个窗口换阶段再开:按这一次开的阶段算
        Assert.True(town.Show(GameStage.Dungeon));
        Assert.False(town.TogglePage());
        Assert.Equal("AVATARS: TOWN ONLY", town.Message);
        Assert.True(town.Show(GameStage.Town));
        Assert.True(town.TogglePage());
    }

    [Fact]
    public void TheAvatarPageNeedsAReadyWardrobe()
    {
        using var h = new AvatarHostRig();
        var bare = new AvatarSession(h.Rig.Assets, h.Progress, () => null);
        var window = new BagWindow();
        window.UseAvatars(bare);
        Assert.True(window.Show(GameStage.Town));
        Assert.False(window.TogglePage());
        Assert.Equal((BagPage.Items, "AVATARS UNAVAILABLE"), (window.Page, window.Message));
    }

    [Fact]
    public void OpeningAgainStartsOnTheItemsPageWithAFreshAvatarPage()
    {
        using var h = new AvatarHostRig();
        BagWindow window = h.OpenAvatarWindow();
        window.Move(1, 1);
        Assert.Equal(BagWindow.Columns + 1, window.Avatars.Cursor);
        AvatarHostRig.SelectSlot(window, 4);
        Assert.False(window.Activate(h.Progress));
        Assert.Equal("EMPTY SLOT", window.Message);
        Assert.True(window.Avatars.OnSlots);

        window.Close();
        Assert.True(window.Show(GameStage.Town));
        Assert.Equal(BagPage.Items, window.Page);
        Assert.Equal((0, false, 0, string.Empty), (window.Avatars.Cursor, window.Avatars.OnSlots, window.Avatars.SlotCursor, window.Avatars.Message));
    }

    [Fact]
    public void EachVisitToTheAvatarPageStartsFreshAndSeesTheBagAsItIsNow()
    {
        using var h = new AvatarHostRig();
        BagWindow window = h.OpenAvatarWindow();
        window.Move(1, 1);
        Assert.Equal(BagWindow.Columns + 1, window.Avatars.Cursor);
        Assert.True(window.TogglePage());                                          // 回物品页
        Assert.Equal(BagPage.Items, window.Page);
        h.Give(h.Avatar(0));                                                       // 在物品页期间背包里多了一件时装

        Assert.True(window.TogglePage());
        Assert.Equal((0, false), (window.Avatars.Cursor, window.Avatars.OnSlots));
        Assert.Single(window.Avatars.View.Avatars);
    }

    // ---------------------------------------------------------------- 按键交给显示着的那一页

    [Fact]
    public void KeysGoToThePageThatIsShownAndTheTabsStayWhereTheyWere()
    {
        using var h = new AvatarHostRig();
        BagWindow window = h.OpenWindow();
        window.Move(1, 0);
        Assert.Equal((1, 0), (window.Cursor, window.Avatars.Cursor));
        window.NextTab(1);
        Assert.Equal((InventoryTab.Consumable, 0), (window.Tab, window.Cursor));
        window.Move(1, 0);                                                         // 物品页的光标不在第一格:切去装扮页再回来要原样
        Assert.Equal(1, window.Cursor);

        Assert.True(window.TogglePage());
        window.Move(1, 1);
        window.NextTab(1);                                                         // 装扮页没有页签:一次一次单独按,不能靠"来回抵消"蒙混过去
        Assert.Equal((1, BagWindow.Columns + 1, InventoryTab.Consumable), (window.Cursor, window.Avatars.Cursor, window.Tab));
        window.NextTab(-1);
        Assert.Equal((1, BagWindow.Columns + 1, InventoryTab.Consumable), (window.Cursor, window.Avatars.Cursor, window.Tab));

        Assert.True(window.TogglePage());
        Assert.Equal((InventoryTab.Consumable, 1), (window.Tab, window.Cursor));
        window.Move(1, 0);
        window.NextTab(1);
        Assert.Equal((InventoryTab.Material, 0), (window.Tab, window.Cursor));
        Assert.Equal(BagWindow.Columns + 1, window.Avatars.Cursor);                // 物品页的操作没碰装扮页
    }

    [Fact]
    public void ActivateOnTheAvatarPageWearsAndTakesOffAndTheWindowCountsTheChanges()
    {
        using var h = new AvatarHostRig(level: 20);
        ItemInfo hat = h.Avatar(0);
        ItemInfo foreign = h.Make(AvatarHostRig.CodeWrongClassHat, HatOtherJob, 0, job: "[fighter]");
        h.Give(hat, foreign);
        BagWindow window = h.OpenAvatarWindow();
        Assert.False(window.Activate(null));                                       // 没有进度:什么也不做

        Assert.True(window.Activate(h.Progress));                                  // 格 0 是帽子
        Assert.Equal((1, "WORN", EquipResult.Ok), (window.Changes, window.Message, window.Avatars.LastResult));
        Assert.Equal(901, h.Session.Slots.Get(0).Code);
        Assert.Equal(EquipResult.NoItem, window.LastResult);                       // 物品页自己的"最近结果"不被装扮页写

        Assert.False(window.Activate(h.Progress));                                 // 现在格 0 是别的职业的帽子
        Assert.Equal((1, "WRONG CLASS", EquipResult.WrongJob), (window.Changes, window.Message, window.Avatars.LastResult));
        Assert.Equal(1, h.Session.Slots.WornCount);

        AvatarHostRig.SelectSlot(window, 0);
        Assert.True(window.Activate(h.Progress));
        Assert.Equal((2, "TAKEN OFF"), (window.Changes, window.Message));
        Assert.Equal(0, h.Session.Slots.WornCount);

        Assert.True(window.TogglePage());                                          // 物品页的提示是它自己的
        Assert.Equal(string.Empty, window.Message);
    }

    [Fact]
    public void TheItemsPageStillEquipsNormalGearAndRefusesAvatarsWithoutTouchingTheBar()
    {
        using var h = new AvatarHostRig();
        ItemInfo hat = h.Avatar(0);
        ItemInfo gear = h.Gear(AvatarHostRig.CodeGearCoat, EquipmentParts.Coat);
        h.Give(hat, gear);
        BagWindow window = h.OpenWindow();                                         // 物品页格 0 是时装帽子,格 1 是普通上衣
        Assert.False(window.Activate(h.Progress));
        Assert.Equal((EquipResult.NotWearable, "NO SLOT FOR THIS"), (window.LastResult, window.Message));
        Assert.Equal((0, 0), (h.Session.Slots.WornCount, h.Progress.EquippedCount));

        window.Move(1, 0);
        Assert.True(window.Activate(h.Progress));
        Assert.Equal((EquipResult.Ok, 1), (window.LastResult, window.Changes));
        Assert.Equal((0, 1), (h.Session.Slots.WornCount, h.Progress.EquippedCount));
    }

    [Fact]
    public void TheUnattendedProbeAlwaysTriesOnTheItemsPageEvenWhenTheAvatarPageIsShown()
    {
        using var h = new AvatarHostRig();
        ItemInfo hat = h.Avatar(0);
        ItemInfo gear = h.Gear(AvatarHostRig.CodeGearCoat, EquipmentParts.Coat);
        h.Give(hat, gear);
        BagWindow window = h.OpenAvatarWindow();
        Assert.False(window.TryEquipAny(null, out int none));                      // 没有进度:什么也不做
        Assert.Equal((0, BagPage.Avatars), (none, window.Page));

        // 装扮页开着,探针也回到物品页:格 0 的时装装备栏不收,格 1 的上衣穿上;装扮栏不动
        Assert.True(window.TryEquipAny(h.Progress, out int tried));
        Assert.Equal((BagPage.Items, 2, 1, 0), (window.Page, tried, h.Progress.EquippedCount, h.Session.Slots.WornCount));
    }

    // ---------------------------------------------------------------- 点击

    [Fact]
    public void ThePageButtonSwitchesPagesFromEitherPageAndSaysWhyItCannotInTheDungeon()
    {
        using var h = new AvatarHostRig();
        (int bx, int by, int bw, int bh) = BagWindow.PageButtonRect;
        Assert.Equal((330, 38, 132, 28), (bx, by, bw, bh));                        // 摆位是版面定下来的数(标题条右侧),不跟着常数一起变,下面的边界才有意义
        BagWindow window = h.OpenWindow();
        Assert.True(window.Click(bx, by, h.Progress));                             // 左上角那一点也在按钮里
        Assert.Equal(BagPage.Avatars, window.Page);
        Assert.True(window.Click(bx + bw - 1, by + bh - 1, h.Progress));           // 右下角也是
        Assert.Equal(BagPage.Items, window.Page);
        Assert.False(window.Click(bx + bw, by, h.Progress));                       // 出了按钮的边:不是窗口里的控件
        Assert.False(window.Click(bx - 1, by, h.Progress));
        Assert.Equal(BagPage.Items, window.Page);

        BagWindow dungeon = h.OpenWindow(GameStage.Dungeon);
        Assert.True(dungeon.Click(bx + 1, by + 1, h.Progress));                    // 点中了控件,只是切不过去
        Assert.Equal((BagPage.Items, "AVATARS: TOWN ONLY"), (dungeon.Page, dungeon.Message));
    }

    [Fact]
    public void ClicksOnTheAvatarPageSelectThenActivateAndNeverReachTheItemsPageControls()
    {
        using var h = new AvatarHostRig();
        ItemInfo hat = h.Avatar(0);
        h.Give(hat);
        BagWindow window = h.OpenAvatarWindow();
        BagAvatarPage page = window.Avatars;

        (int cx1, int cy1) = Center(BagWindow.CellRect(1));
        (int cx0, int cy0) = Center(BagWindow.CellRect(0));
        Assert.True(window.Click(cx1, cy1, h.Progress));
        Assert.Equal((1, false), (page.Cursor, page.OnSlots));
        Assert.True(window.Click(cx0, cy0, h.Progress));                           // 先选中
        Assert.Equal((0, 0), (page.Cursor, window.Changes));
        Assert.Equal(0, h.Session.Slots.WornCount);
        Assert.True(window.Click(cx0, cy0, h.Progress));                           // 再点一下等于回车
        Assert.Equal((1, 1, "WORN"), (window.Changes, h.Session.Slots.WornCount, window.Message));

        (int s4x, int s4y) = Center(BagWindow.SlotRect(4));
        Assert.True(window.Click(s4x, s4y, h.Progress));                           // 点装扮栏里别的格:只是选中它(空格也一样),不动穿着的
        Assert.Equal((true, 4), (page.OnSlots, page.SlotCursor));
        Assert.Equal((1, 1), (window.Changes, h.Session.Slots.WornCount));
        (int sx, int sy) = Center(BagWindow.SlotRect(0));
        Assert.True(window.Click(sx, sy, h.Progress));
        Assert.Equal((true, 0), (page.OnSlots, page.SlotCursor));
        Assert.Equal(1, h.Session.Slots.WornCount);
        Assert.True(window.Click(sx, sy, h.Progress));
        Assert.Equal((2, 0, "TAKEN OFF"), (window.Changes, h.Session.Slots.WornCount, window.Message));

        // 物品页的页签点不到;装备栏的 10、11 号格装扮页没有;窗口外面不是控件
        (int tx, int ty) = Center(BagWindow.TabRect(1));
        Assert.False(window.Click(tx, ty, h.Progress));
        Assert.Equal(InventoryTab.Equipment, window.Tab);
        (int ex, int ey) = Center(BagWindow.SlotRect(AvatarSlots.Count));
        Assert.False(window.Click(ex, ey, h.Progress));
        Assert.Equal(0, page.SlotCursor);
        Assert.False(window.Click(0, 0, h.Progress));
        Assert.Equal(BagPage.Avatars, window.Page);
    }

    [Fact]
    public void AClosedWindowIgnoresTheAvatarInputToo()
    {
        using var h = new AvatarHostRig();
        BagWindow window = h.OpenAvatarWindow();
        h.Give(h.Avatar(0));
        window.Close();
        window.Move(1, 1);
        Assert.False(window.Activate(h.Progress));
        (int cx, int cy) = Center(BagWindow.CellRect(0));
        Assert.False(window.Click(cx, cy, h.Progress));
        Assert.Equal((0, 0), (window.Avatars.Cursor, h.Session.Slots.WornCount));
    }
}

/// <summary>装扮页本身(<see cref="BagAvatarPage"/>):光标、穿脱、点击,以及"窗口里的结果与直接问规则层一致"。</summary>
public class BagAvatarPageTests
{
    private static (int X, int Y) Center((int X, int Y, int W, int H) r) => (r.X + r.W / 2, r.Y + r.H / 2);

    // ---------------------------------------------------------------- 光标

    [Fact]
    public void ANewPageStartsOnTheFirstCellWithNothingSaid()
    {
        using var h = new AvatarHostRig();
        h.Give(h.Avatar(3), h.Gear(AvatarHostRig.CodeGearCoat, EquipmentParts.Coat), h.Avatar(0));
        var page = new BagAvatarPage(h.Session);
        Assert.Same(h.Session, page.Session);
        Assert.Equal((0, false, 0, string.Empty, EquipResult.NoItem, 0), (page.Cursor, page.OnSlots, page.SlotCursor, page.Message, page.LastResult, page.Changes));
        Assert.Equal(new[] { 904, 901 }, page.View.Avatars.Select(e => e.Item.Code));          // 建页时就有视图,普通装备不在里面
        Assert.Equal(-1, page.SelectedPart);
        Assert.Equal(904, page.SelectedItem.Code);
        Assert.Equal(0, page.SelectedEntry.Value.BagIndex);
        Assert.Throws<ArgumentNullException>(() => new BagAvatarPage(null));
    }

    [Fact]
    public void TheCursorStaysInsideTheGridLikeTheItemsPage()
    {
        using var h = new AvatarHostRig();
        BagAvatarPage page = h.OpenAvatarWindow().Avatars;
        page.Move(0, -1);
        Assert.Equal(0, page.Cursor);
        page.Move(1, 0);
        page.Move(0, 1);
        Assert.Equal(BagWindow.Columns + 1, page.Cursor);
        page.Move(-1, 0);                                                                     // 不在最左一列时往左只是格子里挪一格,不进装扮栏
        Assert.Equal((BagWindow.Columns, false), (page.Cursor, page.OnSlots));
        for (int i = 0; i < 20; i++)
        {
            page.Move(1, 1);
        }
        Assert.Equal((Inventory.SlotsPerTab - 1, false), (page.Cursor, page.OnSlots));        // 右下角夹住
        page.Move(0, 5);
        page.Move(5, 0);
        Assert.Equal(Inventory.SlotsPerTab - 1, page.Cursor);
    }

    [Theory]
    [InlineData(0, 2, 0)]
    [InlineData(1, 5, 8)]
    [InlineData(2, 8, 16)]
    [InlineData(3, 9, 24)]
    [InlineData(4, 9, 24)]
    [InlineData(5, 9, 24)]
    [InlineData(6, 9, 24)]
    public void LeftFromTheFirstColumnEntersTheAvatarBarAtTheSameRowAndRightComesBackToTheBarsRow(int row, int slot, int back)
    {
        using var h = new AvatarHostRig();
        BagAvatarPage page = h.OpenAvatarWindow().Avatars;
        for (int i = 0; i < row; i++)
        {
            page.Move(0, 1);
        }
        Assert.Equal(row * BagWindow.Columns, page.Cursor);
        page.Move(-1, 0);
        Assert.Equal((true, slot, slot), (page.OnSlots, page.SlotCursor, page.SelectedPart));    // 装扮栏三列四行,最后一行只有一格
        Assert.Null(page.SelectedEntry);
        page.Move(1, 0);
        Assert.Equal((false, back, -1), (page.OnSlots, page.Cursor, page.SelectedPart));
    }

    [Fact]
    public void TheAvatarBarCursorMovesInsideItsThreeColumnsAndClampsAtTheEdges()
    {
        using var h = new AvatarHostRig();
        BagAvatarPage page = h.OpenAvatarWindow().Avatars;
        page.Move(-1, 0);
        var seen = new List<int> { page.SlotCursor };
        // 起点 2;左左左(第三下夹住)、下四次(第四下夹住)、上、右、下(最后一行只有第 9 格,夹到它)、左(夹住)、上四次(第四下夹住)
        foreach ((int dx, int dy) in new[] { (-1, 0), (-1, 0), (-1, 0), (0, 1), (0, 1), (0, 1), (0, 1), (0, -1), (1, 0), (0, 1), (-1, 0), (0, -1), (0, -1), (0, -1), (0, -1) })
        {
            page.Move(dx, dy);
            Assert.True(page.OnSlots);
            seen.Add(page.SlotCursor);
        }
        Assert.Equal(new[] { 2, 1, 0, 0, 3, 6, 9, 9, 6, 7, 9, 9, 6, 3, 0, 0 }, seen);
    }

    [Fact]
    public void ResetGoesBackToTheOpeningStateAndRebuildsTheView()
    {
        using var h = new AvatarHostRig();
        BagWindow window = h.OpenAvatarWindow();
        BagAvatarPage page = window.Avatars;
        AvatarHostRig.SelectSlot(window, 6);
        Assert.False(page.Activate());
        Assert.Equal("EMPTY SLOT", page.Message);
        h.Give(h.Avatar(0));
        Assert.Empty(page.View.Avatars);                                           // 视图是快照:背包变了,页还没重建
        page.Reset();
        Assert.Equal((0, false, 0, string.Empty), (page.Cursor, page.OnSlots, page.SlotCursor, page.Message));
        Assert.Single(page.View.Avatars);
        Assert.Single(page.Refresh().Avatars);
    }

    [Fact]
    public void TheSelectionFollowsTheCursor()
    {
        using var h = new AvatarHostRig();
        ItemInfo hat = h.Avatar(0);
        ItemInfo coat = h.Avatar(3);
        h.Give(hat, coat);
        BagWindow window = h.OpenAvatarWindow();
        BagAvatarPage page = window.Avatars;
        h.WearOk(coat.Code);
        page.Refresh();

        Assert.Equal(hat, page.SelectedItem);
        Assert.Equal((0, 901), (page.SelectedEntry.Value.BagIndex, page.SelectedEntry.Value.Item.Code));
        page.Move(1, 0);                                                           // 第二格是空的
        Assert.Null(page.SelectedEntry);
        Assert.Null(page.SelectedItem);
        AvatarHostRig.SelectSlot(window, 3);
        Assert.Equal(coat, page.SelectedItem);                                     // 装扮栏上选中的是穿着的那件
        Assert.Equal(3, page.SelectedPart);
        Assert.Null(page.SelectedEntry);
        AvatarHostRig.SelectSlot(window, 5);
        Assert.Null(page.SelectedItem);
    }

    // ---------------------------------------------------------------- 穿与脱

    [Fact]
    public void WearingGoesThroughTheSessionAndRebuildsTheView()
    {
        using var h = new AvatarHostRig();
        ItemInfo hat = h.Avatar(0);
        ItemInfo coat = h.Avatar(3);
        h.Give(hat, coat);
        BagAvatarPage page = h.OpenAvatarWindow().Avatars;
        Assert.Equal(new[] { 901, 904 }, page.View.Avatars.Select(e => e.Item.Code));

        Assert.True(page.Activate());
        Assert.Equal((1, "WORN", EquipResult.Ok), (page.Changes, page.Message, page.LastResult));
        Assert.Equal(901, h.Session.Slots.Get(0).Code);
        Assert.Equal(new[] { 904 }, page.View.Avatars.Select(e => e.Item.Code));   // 帽子离开了背包,视图已经重建
        Assert.Equal(904, page.SelectedItem.Code);
        Assert.Equal(new[] { 904 }, h.Codes());
        Assert.Equal(1, h.Session.Wardrobe.WornCount);
    }

    [Theory]
    [InlineData(AvatarHostRig.CodeWrongClassHat, EquipResult.WrongJob, "WRONG CLASS")]
    [InlineData(AvatarHostRig.CodeHighLevelHat, EquipResult.LevelTooLow, "LEVEL TOO LOW")]
    [InlineData(AvatarHostRig.CodeHatOtherJob, EquipResult.NotShowable, "NO ANIMATION FOR THIS")]
    public void AFailedWearChangesNothingAndShowsTheRuleLayersMessage(int code, EquipResult expected, string message)
    {
        using var h = new AvatarHostRig(level: 20);
        h.Make(AvatarHostRig.CodeWrongClassHat, HatOtherJob, 0, job: "[fighter]");
        h.Make(AvatarHostRig.CodeHighLevelHat, HatOtherJob, 0, level: 50);
        h.Make(AvatarHostRig.CodeHatOtherJob, HatOtherJob, 0);
        h.Give(h.Lookup(code));
        BagAvatarPage page = h.OpenAvatarWindow().Avatars;
        Assert.False(page.Activate());
        Assert.Equal((expected, message, 0), (page.LastResult, page.Message, page.Changes));
        Assert.Equal(new[] { code }, h.Codes());
        Assert.Equal((0, 0), (h.Session.Slots.WornCount, h.Session.Wardrobe.WornCount));
        // 格子上的结果就是规则层的结果,没有第二套判断(规则放行、只是画面上穿不出来的那一件,规则层的结果是 Ok)
        Assert.Equal(expected == EquipResult.NotShowable ? EquipResult.Ok : expected, page.SelectedEntry.Value.Result);
    }

    [Fact]
    public void AnEmptyCellDoesNothing()
    {
        using var h = new AvatarHostRig();
        h.Give(h.Avatar(0));
        BagAvatarPage page = h.OpenAvatarWindow().Avatars;
        page.Move(1, 0);
        Assert.False(page.Activate());
        Assert.Equal((string.Empty, EquipResult.NoItem, 0), (page.Message, page.LastResult, page.Changes));
        Assert.Equal(0, h.Session.Slots.WornCount);
    }

    [Fact]
    public void SwappingPutsTheOldItemInTheCellTheNewOneCameFrom()
    {
        using var h = new AvatarHostRig();
        ItemInfo one = h.Avatar(3);
        ItemInfo two = h.Make(AvatarHostRig.CodeCoatTwo, CoatTwo, 3);
        h.Give(one, two);
        BagAvatarPage page = h.OpenAvatarWindow().Avatars;
        Assert.True(page.Activate());                                              // 穿 904:背包剩 910
        Assert.Equal(new[] { 910 }, h.Codes());
        Assert.True(page.Activate());                                              // 格 0 现在是 910:穿上换下 904,904 回到 910 原来的格
        Assert.Equal(new[] { 904 }, h.Codes());
        Assert.Equal(910, h.Session.Slots.Get(3).Code);
        Assert.Equal(new[] { 904 }, page.View.Avatars.Select(e => e.Item.Code));
        Assert.Equal((2, "WORN"), (page.Changes, page.Message));
        string[] paths = AvatarHostRig.Paths(h.Session.Wardrobe.Current);                       // 外观里是上衣二,没有上衣一的图层
        Assert.Contains(CoatTwoArt.Path, paths);
        Assert.DoesNotContain(CoatOneFrontArt.Path, paths);
        Assert.DoesNotContain(CoatOneBackArt.Path, paths);
    }

    [Fact]
    public void TakingOffNeedsAWornItemAndARoomInTheBag()
    {
        using var h = new AvatarHostRig();
        ItemInfo hat = h.Avatar(0);
        h.Give(hat);
        BagWindow window = h.OpenAvatarWindow();
        BagAvatarPage page = window.Avatars;

        AvatarHostRig.SelectSlot(window, 0);
        Assert.False(page.Activate());
        Assert.Equal(("EMPTY SLOT", 0), (page.Message, page.Changes));

        h.WearOk(hat.Code);
        h.FillBag();
        AvatarHostRig.SelectSlot(window, 0);
        Assert.False(page.Activate());                                             // 装备页没有空格
        Assert.Equal(("BAG FULL", 0), (page.Message, page.Changes));
        Assert.Equal(901, h.Session.Slots.Get(0).Code);
        Assert.Equal(1, h.Session.Wardrobe.WornCount);

        h.Progress.Bag.RemoveAt(InventoryTab.Equipment, 0, 1);                     // 腾出一格
        AvatarHostRig.SelectSlot(window, 0);
        Assert.True(page.Activate());
        Assert.Equal(("TAKEN OFF", 1), (page.Message, page.Changes));
        Assert.Null(h.Session.Slots.Get(0));
        Assert.Equal(0, h.Session.Wardrobe.WornCount);
        Assert.Equal(901, h.Codes()[^1]);                                          // 背包的格子是挨着排的,脱下的接在最后
        Assert.Equal(new[] { 901 }, page.View.Avatars.Select(e => e.Item.Code));   // 页面手里的视图当场就是新的:脱下的帽子已经在可穿的列表里
    }

    [Fact]
    public void ActivateRebuildsTheViewFirstSoAStaleCellNeverWearsTheWrongItem()
    {
        using var h = new AvatarHostRig();
        ItemInfo coat = h.Avatar(3);
        ItemInfo ring = h.Gear(AvatarHostRig.CodeGearRing, EquipmentParts.Ring);
        ItemInfo hat = h.Avatar(0);
        h.Give(coat, ring, hat);                                                   // 装备页:上衣、戒指、帽子
        BagAvatarPage page = h.OpenAvatarWindow().Avatars;
        Assert.Equal(new[] { 0, 2 }, page.View.Avatars.Select(e => e.BagIndex));

        Assert.True(h.Progress.Bag.RemoveAt(InventoryTab.Equipment, 0, 1));        // 页面不知道的时候,第 0 格的上衣被拿走了:装备页变成 戒指、帽子
        Assert.Equal(new[] { 0, 2 }, page.View.Avatars.Select(e => e.BagIndex));   // 页面手里的视图是旧的
        Assert.True(page.Activate());
        Assert.Equal(901, h.Session.Slots.Get(0).Code);                            // 穿的是现在的格 0 对应的时装(帽子,下标 1),不是旧视图里的下标
        Assert.Equal(new[] { 941 }, h.Codes());
        Assert.Equal(0, h.Progress.EquippedCount);
    }

    // ---------------------------------------------------------------- 点击(页面自己)

    [Fact]
    public void ClickingTheSelectedCellAgainActivatesButSwitchingBetweenTheCellsAndTheBarOnlySelects()
    {
        using var h = new AvatarHostRig();
        ItemInfo hat = h.Avatar(0);
        h.Give(hat);
        BagAvatarPage page = h.OpenAvatarWindow().Avatars;
        (int cx, int cy) = Center(BagWindow.CellRect(0));
        (int sx, int sy) = Center(BagWindow.SlotRect(0));

        // 格 → 装扮栏 → 格 → 装扮栏:每一下都只是换选中的地方(光标本来就在格 0,也不算"再点一下")
        Assert.True(page.Click(sx, sy));
        Assert.True(page.Click(cx, cy));
        Assert.True(page.Click(sx, sy));
        Assert.True(page.Click(cx, cy));
        Assert.Equal((0, 0, false), (page.Changes, h.Session.Slots.WornCount, page.OnSlots));

        Assert.True(page.Click(cx, cy));                                           // 再点一下已选中的格:穿上
        Assert.Equal((1, 1), (page.Changes, h.Session.Slots.WornCount));

        Assert.True(page.Click(cx, cy));                                           // 格 0 现在是空的:再点什么也不发生
        Assert.Equal((1, 1), (page.Changes, h.Session.Slots.WornCount));
        Assert.True(page.Click(sx, sy));
        Assert.True(page.Click(cx, cy));
        Assert.True(page.Click(sx, sy));                                           // 格 → 装扮栏只选中,不脱
        Assert.Equal((1, 1, true), (page.Changes, h.Session.Slots.WornCount, page.OnSlots));
        Assert.True(page.Click(sx, sy));                                           // 再点一下已选中的装扮格:脱下
        Assert.Equal((2, 0), (page.Changes, h.Session.Slots.WornCount));
        Assert.False(page.Click(5, 5));
    }

    // ---------------------------------------------------------------- 文字

    [Fact]
    public void TheMessagesAreTheRuleLayersResultsInWordsAndTheLabelsAreOneToTen()
    {
        Assert.Equal("WORN", BagAvatarPage.MessageOf(EquipResult.Ok));
        Assert.Equal(string.Empty, BagAvatarPage.MessageOf(EquipResult.NoItem));
        foreach (EquipResult result in Enum.GetValues(typeof(EquipResult)))
        {
            if (result != EquipResult.Ok)
            {
                Assert.Equal(BagWindow.MessageOf(result), BagAvatarPage.MessageOf(result));
            }
        }
        Assert.Equal(new[] { "HAT", "HAIR", "FACE", "COAT", "PANT", "SHOE", "BRST", "WAIST", "SKIN", "AURA" },
            Enumerable.Range(AvatarSlots.First, AvatarSlots.Count).Select(BagAvatarPage.SlotLabel));
        Assert.Equal(new[] { string.Empty, string.Empty, string.Empty }, new[] { AvatarSlots.First - 1, AvatarSlots.Last + 1, EquipmentSlots.First + 5 }.Select(BagAvatarPage.SlotLabel));
    }

    // ---------------------------------------------------------------- 窗口里的结果与直接问规则层一致(验收)

    /// <summary>规则层加衣柜的直接接法(r14-avatar 的接线契约),不经过窗口、页面和会话:拿来与窗口走出来的结果逐项比。</summary>
    private sealed class DirectWiring
    {
        private readonly AvatarHostRig _h;
        private readonly AvatarWardrobe _wardrobe;

        public DirectWiring(AvatarHostRig h)
        {
            _h = h;
            Bag = h.Progress.Bag.Clone();
            Slots = new AvatarSlots();
            _wardrobe = new AvatarWardrobe(h.Rig.Assets, h.Rig.Base);
        }

        public Inventory Bag { get; }

        public AvatarSlots Slots { get; }

        public AvatarOutfit Look => _wardrobe.Current;

        public EquipResult Wear(int bagIndex)
        {
            AvatarItem shown = null;
            EquipResult result = AvatarActions.Wear(Bag, Slots, bagIndex, _h.Progress.Job, _h.Progress.Level, info =>
            {
                shown = AvatarItem.Read(_h.Rig.Assets, _h.Rig.Class, info.Path);
                return shown != null && _wardrobe.CanShow(shown);
            });
            if (result == EquipResult.Ok)
            {
                _wardrobe.Wear(shown);
            }
            return result;
        }

        public bool TakeOff(int part)
        {
            if (!AvatarActions.TakeOff(Bag, Slots, part))
            {
                return false;
            }
            _wardrobe.TakeOff(part);
            return true;
        }
    }

    private static void AssertSame(string step, AvatarHostRig h, DirectWiring direct)
    {
        string bagA = string.Join(",", direct.Bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code));
        string bagB = string.Join(",", h.Codes());
        Assert.True(bagA == bagB, step + ":背包 直接=" + bagA + " 窗口=" + bagB);
        string wornA = string.Join(",", Enumerable.Range(AvatarSlots.First, AvatarSlots.Count).Select(p => direct.Slots.Get(p)?.Code ?? 0));
        string wornB = string.Join(",", Enumerable.Range(AvatarSlots.First, AvatarSlots.Count).Select(p => h.Session.Slots.Get(p)?.Code ?? 0));
        Assert.True(wornA == wornB, step + ":装扮栏 直接=" + wornA + " 窗口=" + wornB);
        string lookA = AvatarHostRig.Names(direct.Look);
        string lookB = AvatarHostRig.Names(h.Session.Wardrobe.Current);
        Assert.True(lookA == lookB, step + ":外观 直接=" + lookA + " 窗口=" + lookB);
    }

    [Fact]
    public void WhatTheWindowDoesIsExactlyWhatTheRuleLayerAnswersWhenAskedDirectly()
    {
        using var h = new AvatarHostRig(level: 20);
        ItemInfo hat = h.Avatar(0);
        ItemInfo coatOne = h.Avatar(3);
        ItemInfo coatTwo = h.Make(AvatarHostRig.CodeCoatTwo, CoatTwo, 3);
        ItemInfo noAnimationHat = h.Make(AvatarHostRig.CodeHatOtherJob, HatOtherJob, 0);
        ItemInfo noImageCoat = h.Make(AvatarHostRig.CodeCoatNoImage, CoatNoImage, 3);
        ItemInfo foreignHat = h.Make(AvatarHostRig.CodeWrongClassHat, HatOtherJob, 0, job: "[fighter]");
        ItemInfo highHat = h.Make(AvatarHostRig.CodeHighLevelHat, HatOtherJob, 0, level: 50);
        ItemInfo ring = h.Gear(AvatarHostRig.CodeGearRing, EquipmentParts.Ring);
        h.Give(hat, coatOne, coatTwo, noAnimationHat, noImageCoat, foreignHat, highHat, ring);

        BagWindow window = h.OpenAvatarWindow();
        var direct = new DirectWiring(h);
        AssertSame("开始", h, direct);

        var results = new List<EquipResult>();
        void Wear(int code)
        {
            AvatarBagEntry[] entries = window.Avatars.Refresh().Avatars.ToArray();
            int cell = Array.FindIndex(entries, e => e.Item.Code == code);
            Assert.True(cell >= 0, "时装格里没有 " + code);
            AvatarHostRig.SelectCell(window, cell);
            int bagIndex = h.IndexOf(code);
            EquipResult want = direct.Wear(bagIndex);
            bool changed = window.Activate(h.Progress);
            Assert.Equal(want, window.Avatars.LastResult);
            Assert.Equal(want == EquipResult.Ok, changed);
            Assert.Equal(BagAvatarPage.MessageOf(want), window.Message);
            AssertSame("穿 " + code, h, direct);
            results.Add(want);
        }
        void Off(int part)
        {
            bool had = direct.Slots.Get(part) != null;
            AvatarHostRig.SelectSlot(window, part);
            bool want = direct.TakeOff(part);
            bool done = window.Activate(h.Progress);
            Assert.Equal(want, done);
            Assert.Equal(want ? "TAKEN OFF" : had ? "BAG FULL" : "EMPTY SLOT", window.Message);
            AssertSame("脱 " + part, h, direct);
        }

        Wear(901);                                    // 帽子:穿上
        Wear(904);                                    // 上衣一:穿上
        Wear(AvatarHostRig.CodeCoatTwo);              // 上衣二:换穿,上衣一回到上衣二的格
        Wear(AvatarHostRig.CodeHatOtherJob);          // 画面上没有这个职业的动画:不穿
        Wear(AvatarHostRig.CodeCoatNoImage);          // 图片不在图包里:不穿
        Wear(AvatarHostRig.CodeWrongClassHat);        // 职业不符
        Wear(AvatarHostRig.CodeHighLevelHat);         // 等级不够
        Off(3);                                       // 脱上衣
        Off(3);                                       // 没有可脱的
        Wear(904);                                    // 空位上穿
        Off(0);                                       // 脱帽子
        Off(9);                                       // 灵气:空的

        // 背包塞满:换穿还可以(旧件回新件的格),脱下就不行了
        for (int i = 0; ; i++)
        {
            ItemInfo filler = h.Gear(AvatarHostRig.CodeFillerFirst + i, EquipmentParts.Ring);
            if (!h.Progress.Bag.CanAdd(filler))
            {
                break;
            }
            Assert.True(h.Progress.AddItem(filler));
            Assert.Equal(1, direct.Bag.Add(filler));
        }
        AssertSame("塞满", h, direct);
        Wear(AvatarHostRig.CodeCoatTwo);
        Off(3);
        Off(0);

        Assert.Equal(new[]
        {
            EquipResult.Ok, EquipResult.Ok, EquipResult.Ok, EquipResult.NotShowable, EquipResult.NotShowable, EquipResult.WrongJob, EquipResult.LevelTooLow,
            EquipResult.Ok, EquipResult.Ok,
        }, results);
    }

    // ---------------------------------------------------------------- 时装与普通装备互不相干(窗口不能把它们混起来判断)

    [Fact]
    public void AvatarsAndNormalGearOfTheSamePartAreWornTogetherAndLeftAloneByEachOther()
    {
        using var h = new AvatarHostRig();
        ItemInfo gearCoat = h.Gear(AvatarHostRig.CodeGearCoat, EquipmentParts.Coat);
        ItemInfo avatarCoat = h.Avatar(3);
        h.Give(gearCoat, avatarCoat);
        BagWindow window = h.OpenWindow();
        Assert.True(window.Activate(h.Progress));                                  // 物品页格 0:普通上衣
        Assert.Equal(1, h.Progress.EquippedCount);

        Assert.True(window.TogglePage());                                          // 装扮页只有那件时装
        Assert.Equal(new[] { 904 }, window.Avatars.Refresh().Avatars.Select(e => e.Item.Code));
        Assert.True(window.Activate(h.Progress));                                  // 普通上衣已经穿着,不影响时装上衣
        Assert.Equal(EquipResult.Ok, window.Avatars.LastResult);
        Assert.Equal((1, 1), (h.Progress.EquippedCount, h.Session.Slots.WornCount));

        Assert.True(window.TogglePage());                                          // 回物品页,脱普通上衣:时装上衣还穿着
        window.Move(-1, 0);
        Assert.True(window.OnEquipment);
        Assert.Equal(EquipmentParts.Coat, EquipmentSlots.First + window.EquipCursor);          // 第一行进装备栏落在最右一格,正好是上衣
        Assert.True(window.Activate(h.Progress));
        Assert.Equal(("UNEQUIPPED", 0, 1), (window.Message, h.Progress.EquippedCount, h.Session.Slots.WornCount));

        Assert.True(window.TogglePage());                                          // 回装扮页,脱时装上衣:不涉及普通装备
        AvatarHostRig.SelectSlot(window, 3);
        Assert.True(window.Activate(h.Progress));
        Assert.Equal(("TAKEN OFF", 0, 0), (window.Message, h.Progress.EquippedCount, h.Session.Slots.WornCount));
        Assert.Equal(new[] { 940, 904 }, h.Codes());                               // 两件都回了背包,各在各的格
    }

    [Fact]
    public void TheAvatarGridListsOnlyAvatarsInBagOrderAndTheItemsPageStillHasEverything()
    {
        using var h = new AvatarHostRig();
        ItemInfo ring = h.Gear(AvatarHostRig.CodeGearRing, EquipmentParts.Ring);
        ItemInfo coat = h.Avatar(3);
        ItemInfo gearCoat = h.Gear(AvatarHostRig.CodeGearCoat, EquipmentParts.Coat);
        ItemInfo hat = h.Avatar(0);
        h.Give(ring, coat, gearCoat, hat);                                         // 装备页:戒指、时装上衣、普通上衣、时装帽子
        BagWindow window = h.OpenAvatarWindow();
        AvatarBagView view = window.Avatars.Refresh();
        Assert.Equal(new[] { (1, 904), (3, 901) }, view.Avatars.Select(e => (e.BagIndex, e.Item.Code)));
        Assert.Equal(new[] { 0, 2 }, view.GearIndexes);

        Assert.True(window.Activate(h.Progress));                                  // 格 0:时装上衣(装备页第 1 格)
        Assert.Equal(904, h.Session.Slots.Get(3).Code);
        Assert.Equal(new[] { 941, 940, 901 }, h.Codes());
        Assert.Equal(new[] { 901 }, window.Avatars.View.Avatars.Select(e => e.Item.Code));     // 时装只剩帽子;普通装备不在这一页

        window.Move(0, 1);                                                         // 第二行第一格没有东西
        Assert.False(window.Activate(h.Progress));
        Assert.Equal(1, h.Session.Slots.WornCount);
        Assert.Equal(3, h.Progress.Bag.Tab(InventoryTab.Equipment).Count);

        Assert.True(window.TogglePage());                                          // 物品页:装备页的格子一格不少
        Assert.Equal(3, h.Progress.Bag.Tab(InventoryTab.Equipment).Count);
        Assert.Equal(EquipResult.NotWearable, h.Progress.Equip(2));                // 帽子是时装,物品页穿不了
    }
}
