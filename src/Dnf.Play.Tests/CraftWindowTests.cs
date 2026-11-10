using Dnf.Game;
using static Dnf.Play.Tests.CraftPlayKit;

namespace Dnf.Play.Tests;

/// <summary>
/// 制作窗口的数据模型(本项目自己的设计,不含界面):列表次序、选行与份数、预览、学习与制作两个动作及其计数、关闭后的行为。
/// 数据用 <see cref="CraftPlayKit"/> 的合成配方:900 = 矿×3 + 草×1 → 药水×2;940 = 金币 50 + 矿×1 → 药水×1;
/// 941 = 矿×1 → 药水×1(成功率 50,失败给草×1);942 = 草×1 → 药水×1。
/// </summary>
public class CraftWindowTests
{
    private static CraftWindow Opened(CraftSession session, Inventory bag, ICraftPurse purse = null)
    {
        var window = new CraftWindow();
        window.Use(session);
        Assert.True(window.Show(bag, purse));
        return window;
    }

    private static int[] Codes(CraftWindow window) => window.Rows.Select(r => r.Code).ToArray();

    // ------------------------------------------------------------------ 打开与列表

    [Fact]
    public void WithoutASessionOrABagItDoesNotOpen()
    {
        var window = new CraftWindow();
        Assert.False(window.Show(new Inventory(), null));
        Assert.False(window.Open);
        Assert.Equal(0, window.Opens);
        Assert.Throws<ArgumentNullException>(() => window.Use(null));

        CraftSession session = NewSession();
        window.Use(session);
        Assert.Same(session, window.Session);
        Assert.False(window.Show(null, null));
        Assert.False(window.Open);
        Assert.True(window.Show(new Inventory(), null));
        Assert.True(window.Open);
        Assert.Equal(1, window.Opens);
    }

    [Fact]
    public void LearnedRecipesComeFirstThenLearnableOnesEachInAscendingOrder()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Cheap);
        session.Book.Learn(Std);
        Inventory bag = BagWith(session, (Risky, 1), (Gold50, 1), (Std, 1), (Ore, 1));   // 900 已学:不重复列
        CraftWindow window = Opened(session, bag);

        Assert.Equal(new[] { Std, Cheap, Gold50, Risky }, Codes(window));
        Assert.Equal(new[] { CraftRowState.Learned, CraftRowState.Learned, CraftRowState.Learnable, CraftRowState.Learnable },
            window.Rows.Select(r => r.State).ToArray());
        Assert.All(window.Rows, r => Assert.Equal(r.Code, r.Item.Code));
        Assert.Equal("handwork", window.Rows[0].Category);
        Assert.Null(window.Rows[1].Category);
        Assert.Equal(0, window.Selected);
        Assert.Equal(Std, window.SelectedRow.Code);
        Assert.Equal(1, window.Count);
        Assert.NotNull(window.Preview);
    }

    [Fact]
    public void AnEmptyListHasNoSelectionNoPreviewAndEveryActionIsRefused()
    {
        CraftWindow window = Opened(NewSession(), new Inventory());
        Assert.Empty(window.Rows);
        Assert.Equal(-1, window.Selected);
        Assert.Null(window.SelectedRow);
        Assert.Null(window.Preview);
        window.Move(1);
        window.Select(0);
        window.SetCount(5);
        Assert.False(window.Activate());
        Assert.False(window.Learn());
        Assert.False(window.Craft());
        Assert.Equal(-1, window.Selected);
        Assert.Null(window.LastReport);
        Assert.Equal(0, window.Learns);
        Assert.Equal(0, window.Crafts);
    }

    [Fact]
    public void ReopeningResetsSelectionCountAndReportButNotTheTotals()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Cheap);
        Inventory bag = BagWith(session, (Herb, 3));
        CraftWindow window = Opened(session, bag);
        window.SetCount(2);
        Assert.True(window.Activate());
        Assert.NotNull(window.LastReport);
        Assert.Equal(1, window.Crafts);

        window.Close();
        Assert.False(window.Open);
        Assert.True(window.Show(bag, null));
        Assert.Equal(2, window.Opens);
        Assert.Equal(1, window.Count);
        Assert.Null(window.LastReport);
        Assert.Equal(0, window.Selected);
        Assert.Equal(1, window.Crafts);
    }

    [Fact]
    public void ReopeningAnEmptyListStartsFromOneCopyToo()
    {
        // 列表为空时没有"换行",份数不会被换行重置;重开窗口自己得把份数放回 1。
        CraftWindow window = Opened(NewSession(), new Inventory());
        window.SetCount(7);
        Assert.Equal(7, window.Count);

        window.Close();
        Assert.True(window.Show(new Inventory(), null));
        Assert.Empty(window.Rows);
        Assert.Equal(1, window.Count);
        Assert.Equal(2, window.Opens);
    }

    // ------------------------------------------------------------------ 选行与份数

    [Fact]
    public void MovingClampsAtBothEndsAndAChangedRowResetsTheCount()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Std);
        session.Book.Learn(Cheap);
        session.Book.Learn(Risky);
        CraftWindow window = Opened(session, BagWith(session, (Ore, 50), (Herb, 50)));
        Assert.Equal(new[] { Std, Risky, Cheap }, Codes(window));   // 编号从小到大:900、941、942

        window.Move(-1);
        Assert.Equal(0, window.Selected);
        window.SetCount(7);
        window.Move(1);
        Assert.Equal(1, window.Selected);
        Assert.Equal(1, window.Count);
        window.Move(5);
        Assert.Equal(2, window.Selected);
        window.Move(0);
        Assert.Equal(2, window.Selected);

        window.SetCount(4);
        window.Select(2);   // 选的还是这一行:份数不变
        Assert.Equal(4, window.Count);
        window.Select(0);
        Assert.Equal(1, window.Count);
        window.Select(9);   // 超出范围:什么也不做
        window.Select(-1);
        Assert.Equal(0, window.Selected);

        window.Move(2);
        Assert.Equal(2, window.Selected);
        window.Move(-5);   // 往上走过头:停在第一行,不是原地不动
        Assert.Equal(0, window.Selected);
    }

    [Fact]
    public void TheCountIsClampedAndAddCountMovesIt()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Cheap);
        CraftWindow window = Opened(session, BagWith(session, (Herb, 5)));

        window.SetCount(0);
        Assert.Equal(1, window.Count);
        window.SetCount(-9);
        Assert.Equal(1, window.Count);
        window.SetCount(70000);
        Assert.Equal(CraftBench.MaxCount, window.Count);
        window.SetCount(3);
        window.AddCount(2);
        Assert.Equal(5, window.Count);
        window.AddCount(-10);
        Assert.Equal(1, window.Count);
        window.SetCount(CraftBench.MaxCount);
        window.AddCount(int.MaxValue);
        Assert.Equal(CraftBench.MaxCount, window.Count);
        window.AddCount(int.MinValue);
        Assert.Equal(1, window.Count);
    }

    [Fact]
    public void TheMaxCountIsWhatTheMaterialsAllow()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Std);
        Inventory bag = BagWith(session, (Ore, 7), (Herb, 2));   // 900 要矿×3 + 草×1:矿够 2 份,草够 2 份
        CraftWindow window = Opened(session, bag);
        window.SetMaxCount();
        Assert.Equal(2, window.Count);
        Assert.Equal(2, window.Preview.MaxByMaterials);
        Assert.True(window.Preview.CanCraft);

        window.SetCount(3);
        Assert.Equal(CraftResult.MissingMaterials, window.Preview.Result);

        // 一份也做不了:还是 1。
        bag.Remove(Ore, 7);
        window.Refresh();
        window.SetMaxCount();
        Assert.Equal(1, window.Count);
        Assert.Equal(0, window.Preview.MaxByMaterials);
    }

    [Fact]
    public void ThePreviewOfALearnableRowShowsTheMaterialsButCannotCraft()
    {
        CraftSession session = NewSession();
        Inventory bag = BagWith(session, (Gold50, 1), (Ore, 2));
        CraftWindow window = Opened(session, bag);
        Assert.Equal(CraftRowState.Learnable, window.SelectedRow.State);
        Assert.Equal(CraftResult.NotLearned, window.Preview.Result);
        Assert.False(window.Preview.CanCraft);
        Assert.Equal(2, window.Preview.Lines.Count);
        Assert.True(window.Preview.Lines[0].IsGold);
        Assert.Equal(50, window.Preview.Lines[0].Need);
    }

    // ------------------------------------------------------------------ 学习

    [Fact]
    public void ActivatingALearnableRowLearnsItAndKeepsItSelected()
    {
        CraftSession session = NewSession();
        Inventory bag = BagWith(session, (Risky, 2), (Cheap, 1));
        CraftWindow window = Opened(session, bag);
        Assert.Equal(new[] { Risky, Cheap }, Codes(window));   // 都还没学会:编号从小到大
        window.Select(1);   // 942

        Assert.True(window.Activate());
        Assert.Equal(LearnResult.Ok, window.LastLearn);
        Assert.Equal(1, window.Learns);
        Assert.True(session.Book.IsLearned(Cheap));
        Assert.Equal(0, bag.CountOf(Cheap));
        Assert.Equal(2, bag.CountOf(Risky));
        // 942 现在是已学的行,排到前面;选中还是 942。
        Assert.Equal(new[] { Cheap, Risky }, Codes(window));
        Assert.Equal(0, window.Selected);
        Assert.Equal(Cheap, window.SelectedRow.Code);
        Assert.Equal(CraftRowState.Learned, window.SelectedRow.State);
        Assert.Equal(CraftRowState.Learnable, window.Rows[1].State);
    }

    [Fact]
    public void TheLastRecipeItemIsConsumedAndTheRowStaysAsLearned()
    {
        CraftSession session = NewSession();
        Inventory bag = BagWith(session, (Cheap, 1));
        CraftWindow window = Opened(session, bag);
        Assert.True(window.Learn());
        Assert.Equal(0, bag.CountOf(Cheap));
        Assert.Equal(new[] { Cheap }, Codes(window));
        Assert.Equal(CraftRowState.Learned, window.SelectedRow.State);
        Assert.False(window.Learn());   // 已经是已学的行
        Assert.Equal(1, window.Learns);
    }

    [Fact]
    public void LearningWorksOnlyOnLearnableRowsAndCraftingOnlyOnLearnedOnes()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Std);
        Inventory bag = BagWith(session, (Cheap, 1), (Ore, 3), (Herb, 1));
        CraftWindow window = Opened(session, bag);
        Assert.Equal(new[] { Std, Cheap }, Codes(window));

        string before = Dump(bag);
        Assert.False(window.Learn());   // 选中的 900 已学:连会话都不问
        Assert.Equal(0, window.Learns);
        Assert.Equal(LearnResult.Ok, window.LastLearn);   // 没学过的默认值,没被这一次动过
        window.Select(1);
        Assert.False(window.Craft());   // 942 还没学
        Assert.Null(window.LastReport);
        Assert.Equal(before, Dump(bag));
    }

    // ------------------------------------------------------------------ 制作

    [Fact]
    public void ActivatingALearnedRowCraftsAndTheListAndPreviewFollow()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Std);
        Inventory bag = BagWith(session, (Ore, 7), (Herb, 2));
        CraftWindow window = Opened(session, bag);
        window.SetCount(2);

        Assert.True(window.Activate());
        Assert.True(window.LastReport.Ok);
        Assert.Equal(2, window.LastReport.Count);
        Assert.Equal(1, window.Crafts);
        Assert.Equal(4, bag.CountOf(Potion));
        Assert.Equal(1, bag.CountOf(Ore));
        Assert.Equal(0, bag.CountOf(Herb));
        Assert.Equal(2, window.Count);   // 份数保留
        // 预览已按剩下的材料刷新:矿只剩 1,不够再做 2 份。
        Assert.Equal(CraftResult.MissingMaterials, window.Preview.Result);
        Assert.Equal(0, window.Preview.MaxByMaterials);
    }

    [Fact]
    public void ARefusedCraftChangesNothingAndIsNotCounted()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Std);
        Inventory bag = BagWith(session, (Ore, 2), (Herb, 5));   // 矿差 1
        CraftWindow window = Opened(session, bag);
        string before = Dump(bag);

        Assert.False(window.Activate());
        Assert.Equal(CraftResult.MissingMaterials, window.LastReport.Result);
        Assert.Equal(0, window.Crafts);
        Assert.Equal(before, Dump(bag));
    }

    [Fact]
    public void ARollFailureStillCountsAsACraftBecauseTheMaterialsAreGone()
    {
        CraftSession session = NewSession(new QueueRoll(80));   // 80 ≥ 50:失败
        session.Book.Learn(Risky);
        Inventory bag = BagWith(session, (Ore, 1));
        CraftWindow window = Opened(session, bag);

        Assert.True(window.Activate());
        Assert.True(window.LastReport.Ok);
        Assert.False(window.LastReport.Success);
        Assert.Equal(1, window.Crafts);
        Assert.Equal(0, bag.CountOf(Ore));
        Assert.Equal(0, bag.CountOf(Potion));
        Assert.Equal(1, bag.CountOf(Herb));
    }

    [Fact]
    public void GoldIsReadFromThePurseTheHostGaveAndANullPurseMeansNone()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Gold50);
        Inventory bag = BagWith(session, (Ore, 2));

        CraftWindow without = Opened(session, bag, null);
        Assert.False(without.Preview.CanCraft);
        Assert.Equal(CraftResult.MissingMaterials, without.Preview.Result);
        Assert.False(without.Activate());
        Assert.Equal(2, bag.CountOf(Ore));

        HeroProgress hero = ExpTests.Progress();
        hero.AddGold(80);
        CraftWindow with = Opened(session, bag, new HeroCraftPurse(hero));
        Assert.True(with.Preview.CanCraft);
        Assert.True(with.Activate());
        Assert.Equal(30, hero.Gold);
        Assert.Equal(1, bag.CountOf(Ore));
        Assert.Equal(1, bag.CountOf(Potion));
        Assert.False(with.Preview.CanCraft);   // 金币只剩 30:预览跟着变
    }

    // ------------------------------------------------------------------ 刷新与关闭

    [Fact]
    public void RefreshPicksUpChangesMadeOutsideTheWindowAndKeepsTheSelection()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Std);
        session.Book.Learn(Cheap);
        Inventory bag = new Inventory();
        CraftWindow window = Opened(session, bag);
        window.Select(1);
        window.SetCount(3);
        Assert.False(window.Preview.CanCraft);

        Fill(session, bag, (Herb, 4));
        window.Refresh();
        Assert.Equal(1, window.Selected);
        Assert.Equal(Cheap, window.SelectedRow.Code);
        Assert.Equal(3, window.Count);
        Assert.True(window.Preview.CanCraft);
        Assert.Equal(4, window.Preview.MaxByMaterials);

        // 外面又拿来一件设计图:列表里多一个"可学习"的行,选中还是 942。
        Fill(session, bag, (Gold50, 1));
        window.Refresh();
        Assert.Equal(new[] { Std, Cheap, Gold50 }, Codes(window));
        Assert.Equal(Cheap, window.SelectedRow.Code);
    }

    [Fact]
    public void ARowThatDisappearsMovesTheSelectionToANeighbourAndResetsTheCount()
    {
        CraftSession session = NewSession();
        Inventory bag = BagWith(session, (Cheap, 1), (Gold50, 1), (Risky, 1));
        CraftWindow window = Opened(session, bag);
        Assert.Equal(new[] { Gold50, Risky, Cheap }, Codes(window));
        window.Select(2);
        window.SetCount(3);

        bag.Remove(Cheap, 1);   // 选中的那件设计图被外面拿走
        window.Refresh();
        Assert.Equal(new[] { Gold50, Risky }, Codes(window));
        Assert.Equal(1, window.Selected);
        Assert.Equal(Risky, window.SelectedRow.Code);
        Assert.Equal(1, window.Count);
    }

    [Fact]
    public void ACloseWindowIgnoresEverything()
    {
        CraftSession session = NewSession();
        session.Book.Learn(Cheap);
        Inventory bag = BagWith(session, (Herb, 5));
        CraftWindow window = Opened(session, bag);
        window.Close();
        bag.Remove(Herb, 5);   // 关着的时候背包变了:窗口不去看
        string before = Dump(bag);

        window.Move(1);
        window.Select(0);
        window.SetCount(4);
        window.AddCount(1);
        window.SetMaxCount();
        window.Refresh();
        Assert.False(window.Activate());
        Assert.False(window.Learn());
        Assert.False(window.Craft());
        Assert.Equal(1, window.Count);
        Assert.Equal(0, window.Crafts);
        Assert.True(window.Preview.CanCraft);   // 还是关窗时的预览
        Assert.Equal(before, Dump(bag));
    }

    // ------------------------------------------------------------------ 闸门不放行

    [Fact]
    public void ARefusedLearnIsNotCountedAndKeepsTheRecipeItem()
    {
        var gate = new CountingGate { AllowLearn = false };
        CraftSession session = NewSession(null, gate);
        Inventory bag = BagWith(session, (Cheap, 1));
        CraftWindow window = Opened(session, bag);

        Assert.False(window.Activate());
        Assert.Equal(LearnResult.NotAllowed, window.LastLearn);
        Assert.Equal(0, window.Learns);
        Assert.Equal(1, bag.CountOf(Cheap));
        Assert.False(session.Book.IsLearned(Cheap));
        Assert.Equal(CraftRowState.Learnable, window.SelectedRow.State);

        gate.AllowLearn = true;
        Assert.True(window.Activate());
        Assert.Equal(1, window.Learns);
    }

    [Fact]
    public void ARefusedCraftIsReportedAndNotCounted()
    {
        var gate = new CountingGate { AllowCraft = false };
        CraftSession session = NewSession(null, gate);
        session.Book.Learn(Cheap);
        Inventory bag = BagWith(session, (Herb, 2));
        CraftWindow window = Opened(session, bag);
        string before = Dump(bag);

        Assert.False(window.Preview.CanCraft);
        Assert.Equal(CraftResult.NotAllowed, window.Preview.Result);
        Assert.False(window.Activate());
        Assert.Equal(CraftResult.NotAllowed, window.LastReport.Result);
        Assert.Equal(0, window.Crafts);
        Assert.Equal(before, Dump(bag));
        Assert.Empty(gate.Crafted);
    }

    [Fact]
    public void ABookCodeWithoutARecipeDefinitionIsLeftOutOfTheList()
    {
        CraftSession session = NewSession();
        session.Book.Learn(999);   // 数据里没有这个配方(数据变了)
        session.Book.Learn(Cheap);
        CraftWindow window = Opened(session, new Inventory());
        Assert.Equal(new[] { Cheap }, Codes(window));
        Assert.Equal(Cheap, window.SelectedRow.Code);
    }
}
