using Dnf.Game;
using Dnf.Render;
using static Dnf.Play.Tests.ExpTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 背包窗口(I 键;<see cref="BagWindow"/>、<see cref="BagWindowArt"/>,本项目自己的占位设计,证据 E-118)与它在流程里的开关(<see cref="GameFlow.ToggleBag"/>)。
/// 物品、等级进度都是合成的;窗口只管状态与点击判定,画法只看精灵的组成。
/// </summary>
public class BagWindowTests
{
    private static ItemInfo Gear(int code, int part = EquipmentParts.Weapon, int level = 1, params string[] jobs)
    {
        var item = new ItemInfo(code, "equipment/test/g" + code + ".equ", ItemKind.Equipment) { Part = part, MinimumLevel = level, Grade = 10, Rarity = 2, Price = 300 };
        item.UsableJobs.AddRange(jobs.Length == 0 ? new[] { "[swordman]" } : jobs);
        return item;
    }

    private static ItemInfo Potion(int code)
    {
        return new ItemInfo(code, "stackable/p" + code + ".stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste, StackLimit = 99 };
    }

    private static BagWindow Opened(GameStage stage = GameStage.Town)
    {
        var bag = new BagWindow();
        Assert.True(bag.Show(stage));
        return bag;
    }

    private sealed class FakeRun : GameFlowTests.FakeHero, IRunSession
    {
        public FakeRun(string path, int run) : base("run " + path + " #" + run)
        {
        }

        public SettlementChoice Choice { get; set; }

        public bool Failed { get; set; }
    }

    private static readonly DungeonEntry A = new DungeonEntry("dungeon/test/a.dgn", "AAA");

    private static GameFlow NewFlow()
    {
        return new GameFlow(() => new GameFlowTests.FakeHero("town"), (path, run) => new FakeRun(path, run), new[] { A });
    }

    // ------------------------------------------------------------------ 开关与光标

    [Fact]
    public void ShowOnlyOpensInTownAndDungeonAndStartsOnTheFirstEquipmentCell()
    {
        foreach (GameStage stage in new[] { GameStage.Loading, GameStage.DungeonSelect, GameStage.CharacterSelect })
        {
            var closed = new BagWindow();
            Assert.False(closed.Show(stage));
            Assert.False(closed.Open);
        }
        foreach (GameStage stage in new[] { GameStage.Town, GameStage.Dungeon })
        {
            BagWindow bag = Opened(stage);
            Assert.Equal((true, InventoryTab.Equipment, 0, false, 0, string.Empty), (bag.Open, bag.Tab, bag.Cursor, bag.OnEquipment, bag.EquipCursor, bag.Message));
        }
    }

    [Fact]
    public void ShowAgainResetsThePageAndTheCursors()
    {
        BagWindow bag = Opened();
        bag.NextTab(1);
        bag.Move(1, 1);
        bag.Close();
        Assert.False(bag.Open);
        Assert.True(bag.Show(GameStage.Dungeon));
        Assert.Equal((InventoryTab.Equipment, 0, false), (bag.Tab, bag.Cursor, bag.OnEquipment));
    }

    [Fact]
    public void AClosedWindowIgnoresEveryInput()
    {
        var bag = new BagWindow();
        HeroProgress progress = Progress();
        progress.AddItem(Gear(1));
        bag.Move(1, 1);
        bag.NextTab(1);
        Assert.False(bag.Activate(progress));
        Assert.False(bag.Click(BagWindow.CellRect(0).X + 1, BagWindow.CellRect(0).Y + 1, progress));
        Assert.False(bag.TryEquipAny(progress, out int tried));
        Assert.Equal((0, 0, InventoryTab.Equipment, 0), (tried, bag.Cursor, bag.Tab, progress.EquippedCount));
    }

    [Fact]
    public void TheCursorStaysInsideTheGrid()
    {
        BagWindow bag = Opened();
        bag.Move(0, -1);
        Assert.Equal(0, bag.Cursor);
        bag.Move(1, 0);
        bag.Move(0, 1);
        Assert.Equal(BagWindow.Columns + 1, bag.Cursor);
        for (int i = 0; i < 20; i++)
        {
            bag.Move(1, 1);
        }
        Assert.Equal(Inventory.SlotsPerTab - 1, bag.Cursor);                  // 右下角夹住
        Assert.False(bag.OnEquipment);
        Assert.Equal(7, BagWindow.Rows);
    }

    [Fact]
    public void LeftFromTheFirstColumnEntersTheEquipmentPanelAndRightFromItsLastColumnComesBack()
    {
        BagWindow bag = Opened();
        bag.Move(0, 1);                                                       // 第二行第一列
        Assert.Equal(BagWindow.Columns, bag.Cursor);
        bag.Move(-1, 0);
        Assert.True(bag.OnEquipment);
        Assert.Equal(1 * BagWindow.SlotColumns + BagWindow.SlotColumns - 1, bag.EquipCursor);   // 同一行的最右一格
        bag.Move(1, 0);
        Assert.False(bag.OnEquipment);
        Assert.Equal(BagWindow.Columns, bag.Cursor);                          // 回到同一行第一列
    }

    [Fact]
    public void TheEquipmentCursorMovesInsideItsThreeColumnPanelAndClamps()
    {
        BagWindow bag = Opened();
        bag.Move(-1, 0);                                                      // 第一行最右一格
        Assert.Equal(2, bag.EquipCursor);
        bag.Move(-1, 0);
        bag.Move(-1, 0);
        bag.Move(-1, 0);                                                      // 左边夹住
        Assert.Equal(0, bag.EquipCursor);
        for (int i = 0; i < 10; i++)
        {
            bag.Move(0, 1);
        }
        Assert.Equal(9, bag.EquipCursor);                                     // 四行三列,最下一行第一格
        bag.Move(0, -1);
        Assert.Equal(6, bag.EquipCursor);
        Assert.True(bag.OnEquipment);
        Assert.Equal(12, EquipmentSlots.Count);
    }

    [Fact]
    public void LeftFromALowGridRowLandsOnTheLastEquipmentRow()
    {
        BagWindow bag = Opened();
        for (int i = 0; i < 6; i++)
        {
            bag.Move(0, 1);                                                   // 第七行
        }
        bag.Move(-1, 0);
        Assert.Equal(EquipmentSlots.Count - 1, bag.EquipCursor);              // 装备栏只有四行,落在最后一格
    }

    [Fact]
    public void NextTabCyclesBothWaysAndResetsTheCursor()
    {
        BagWindow bag = Opened();
        bag.Move(1, 1);
        bag.NextTab(1);
        Assert.Equal((InventoryTab.Consumable, 0), (bag.Tab, bag.Cursor));
        bag.NextTab(1);
        bag.NextTab(1);
        Assert.Equal(InventoryTab.Quest, bag.Tab);
        bag.NextTab(1);
        Assert.Equal(InventoryTab.Equipment, bag.Tab);
        bag.NextTab(-1);
        Assert.Equal(InventoryTab.Quest, bag.Tab);
        bag.Move(-1, 0);
        bag.Move(-1, 0);
        bag.NextTab(-1);
        Assert.Equal((InventoryTab.Material, false), (bag.Tab, bag.OnEquipment));
    }

    [Fact]
    public void EveryLabelAndMessageCanBeDrawnWithThePixelFont()
    {
        for (int tab = 0; tab < Inventory.TabCount; tab++)
        {
            string label = BagWindow.TabLabel((InventoryTab)tab);
            Assert.NotEmpty(label);
            Assert.All(label, c => Assert.True(OverlayArt.HasGlyph(c), "字库里没有 '" + c + "'"));
        }
        for (int part = EquipmentSlots.First; part <= EquipmentSlots.Last; part++)
        {
            string label = BagWindow.SlotLabel(part);
            Assert.NotEmpty(label);
            Assert.All(label, c => Assert.True(OverlayArt.HasGlyph(c), "字库里没有 '" + c + "'"));
        }
        Assert.Equal(string.Empty, BagWindow.SlotLabel(9));
        Assert.Equal(string.Empty, BagWindow.SlotLabel(22));
        foreach (EquipResult r in Enum.GetValues(typeof(EquipResult)))
        {
            Assert.All(BagWindow.MessageOf(r), c => Assert.True(OverlayArt.HasGlyph(c), "字库里没有 '" + c + "'"));
        }
        Assert.Equal(string.Empty, BagWindow.MessageOf(EquipResult.NoItem));
        Assert.Equal("EQUIPPED", BagWindow.MessageOf(EquipResult.Ok));
        Assert.All(BagWindowArt.HintText, c => Assert.True(OverlayArt.HasGlyph(c), "字库里没有 '" + c + "'"));
        Assert.Equal(12, Enumerable.Range(EquipmentSlots.First, EquipmentSlots.Count).Select(BagWindow.SlotLabel).Distinct().Count());   // 十二个缩写各不相同
    }

    [Fact]
    public void ThePanelsDoNotOverlapAndStayInsideTheWindow()
    {
        int right = BagWindow.WindowX + BagWindow.WindowWidth, bottom = BagWindow.WindowY + BagWindow.WindowHeight;
        for (int i = 0; i < Inventory.SlotsPerTab; i++)
        {
            (int x, int y, int w, int h) = BagWindow.CellRect(i);
            Assert.True(x >= BagWindow.GridX && x + w <= right && y + h <= bottom);
            Assert.Equal(i, BagWindow.HitCell(x, y));
            Assert.Equal(i, BagWindow.HitCell(x + w - 1, y + h - 1));
            Assert.Equal(-1, BagWindow.HitCell(x + w, y));                    // 格子之间的缝不算
        }
        for (int i = 0; i < EquipmentSlots.Count; i++)
        {
            (int x, int y, int w, int h) = BagWindow.SlotRect(i);
            Assert.True(x + w <= BagWindow.GridX - 4);                        // 装备栏在格子的左边
            Assert.Equal(i, BagWindow.HitSlot(x, y));
            Assert.Equal(i, BagWindow.HitSlot(x + w - 1, y + h - 1));
        }
        for (int i = 0; i < Inventory.TabCount; i++)
        {
            (int x, int y, int w, int h) = BagWindow.TabRect(i);
            Assert.True(x + w <= right && y + h <= BagWindow.GridY);
            Assert.Equal(i, BagWindow.HitTab(x + w / 2, y + h / 2));
        }
        Assert.Equal(-1, BagWindow.HitCell(0, 0));
        Assert.Equal(-1, BagWindow.HitSlot(0, 0));
        Assert.Equal(-1, BagWindow.HitTab(0, 0));
    }

    // ------------------------------------------------------------------ 穿与脱

    [Fact]
    public void ActivateOnAWearableItemEquipsItAndSaysSo()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        ItemInfo sword = Gear(1);
        progress.AddItem(sword);
        Assert.True(bag.Activate(progress));
        Assert.Equal(("EQUIPPED", EquipResult.Ok, 1), (bag.Message, bag.LastResult, bag.Changes));
        Assert.Same(sword, progress.Worn.Get(EquipmentParts.Weapon));
        Assert.Empty(progress.Bag.Tab(InventoryTab.Equipment));
    }

    [Fact]
    public void ActivateExplainsWhyAnItemCannotBeWorn()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress(level: 5);
        progress.AddItem(Gear(1, EquipmentParts.Weapon, 1, "[mage]"));
        progress.AddItem(Gear(2, EquipmentParts.Weapon, 30));
        progress.AddItem(Gear(3, EquipmentParts.HatAvatar));
        Assert.False(bag.Activate(progress));
        Assert.Equal(("WRONG CLASS", EquipResult.WrongJob), (bag.Message, bag.LastResult));
        bag.Move(1, 0);
        Assert.False(bag.Activate(progress));
        Assert.Equal(("LEVEL TOO LOW", EquipResult.LevelTooLow), (bag.Message, bag.LastResult));
        bag.Move(1, 0);
        Assert.False(bag.Activate(progress));
        Assert.Equal(("NO SLOT FOR THIS", EquipResult.NotWearable), (bag.Message, bag.LastResult));
        Assert.Equal((0, 0, 3), (bag.Changes, progress.EquippedCount, progress.Bag.Tab(InventoryTab.Equipment).Count));
    }

    [Fact]
    public void AStackableItemOrAnEmptyCellIsNotWornAndChangesNothing()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        progress.AddItem(Potion(1), 3);
        bag.NextTab(1);
        Assert.False(bag.Activate(progress));                                 // 消耗品页第一格是药水
        Assert.Equal("CANNOT USE YET", bag.Message);
        bag.Move(1, 0);
        Assert.False(bag.Activate(progress));                                 // 空格子
        Assert.Equal(string.Empty, bag.Message);
        Assert.Equal((0, 0, 3), (bag.Changes, progress.EquippedCount, progress.Bag.CountOf(1)));
    }

    [Fact]
    public void TheShowCheckCanRefuseAWeaponThatHasNoAnimation()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        progress.AddItem(Gear(1));
        int asked = 0;
        bag.CanShow = item => { asked++; return false; };
        Assert.False(bag.Activate(progress));
        Assert.Equal(("NO ANIMATION FOR THIS", EquipResult.NotShowable, 1), (bag.Message, bag.LastResult, asked));
        Assert.Equal(0, progress.EquippedCount);
        bag.CanShow = item => true;
        Assert.True(bag.Activate(progress));
        Assert.Equal(1, progress.EquippedCount);
    }

    [Fact]
    public void ActivateOnTheEquipmentPanelTakesTheItemOffAgain()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        progress.AddItem(Gear(1));
        bag.Activate(progress);
        bag.Move(-1, 0);
        bag.Move(-1, 0);
        bag.Move(-1, 0);                                                      // 装备栏第一格:武器
        Assert.Equal((true, 0), (bag.OnEquipment, bag.EquipCursor));
        Assert.True(bag.Activate(progress));
        Assert.Equal(("UNEQUIPPED", 2), (bag.Message, bag.Changes));
        Assert.Equal((0, 1), (progress.EquippedCount, progress.Bag.Tab(InventoryTab.Equipment).Count));
        Assert.False(bag.Activate(progress));
        Assert.Equal("EMPTY SLOT", bag.Message);
    }

    [Fact]
    public void UnequippingIntoAFullEquipmentPageSaysTheBagIsFull()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        progress.AddItem(Gear(1));
        bag.Activate(progress);
        for (int i = 0; i < Inventory.SlotsPerTab; i++)
        {
            Assert.True(progress.AddItem(Gear(100 + i, EquipmentParts.Coat)));
        }
        bag.Move(-1, 0);
        bag.Move(-1, 0);
        bag.Move(-1, 0);
        Assert.False(bag.Activate(progress));
        Assert.Equal(("BAG FULL", 1, 1), (bag.Message, bag.Changes, progress.EquippedCount));
    }

    [Fact]
    public void TryEquipAnyWalksTheEquipmentPageUntilSomethingFits()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress(level: 5);
        progress.AddItem(Gear(1, EquipmentParts.Weapon, 1, "[mage]"));
        progress.AddItem(Gear(2, EquipmentParts.Weapon, 30));
        progress.AddItem(Gear(3, EquipmentParts.Coat, 5));
        progress.AddItem(Gear(4, EquipmentParts.Pants, 5));
        bag.NextTab(1);                                                       // 从别的页开始也行:先切回装备页
        Assert.True(bag.TryEquipAny(progress, out int tried));
        Assert.Equal((3, InventoryTab.Equipment, 1, 3), (tried, bag.Tab, progress.EquippedCount, progress.Bag.Tab(InventoryTab.Equipment).Count));
        Assert.Equal(3, progress.Worn.Get(EquipmentParts.Coat).Code);
    }

    [Fact]
    public void TryEquipAnyReportsHowManyItWasRefusedWhenNothingFits()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        Assert.False(bag.TryEquipAny(progress, out int none));
        Assert.Equal(0, none);
        progress.AddItem(Gear(1, EquipmentParts.Weapon, 1, "[mage]"));
        progress.AddItem(Gear(2, EquipmentParts.Weapon, 30));
        Assert.False(bag.TryEquipAny(progress, out int tried));
        Assert.Equal((2, 0), (tried, progress.EquippedCount));
        Assert.False(bag.TryEquipAny(null, out int nothing));
        Assert.Equal(0, nothing);
    }

    // ------------------------------------------------------------------ 鼠标

    private static (int X, int Y) CenterOfCell(int i)
    {
        (int x, int y, int w, int h) = BagWindow.CellRect(i);
        return (x + w / 2, y + h / 2);
    }

    private static (int X, int Y) CenterOfSlot(int i)
    {
        (int x, int y, int w, int h) = BagWindow.SlotRect(i);
        return (x + w / 2, y + h / 2);
    }

    [Fact]
    public void ClickingATabSwitchesPages()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        (int x, int y, int w, int h) = BagWindow.TabRect(2);
        Assert.True(bag.Click(x + w / 2, y + h / 2, progress));
        Assert.Equal((InventoryTab.Material, 0), (bag.Tab, bag.Cursor));
    }

    [Fact]
    public void ClickingACellSelectsItAndClickingTheSelectedOneAgainEquips()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        progress.AddItem(Gear(1));
        progress.AddItem(Gear(2, EquipmentParts.Coat));
        (int x, int y) = CenterOfCell(1);
        Assert.True(bag.Click(x, y, progress));
        Assert.Equal((1, 0, 0), (bag.Cursor, bag.Changes, progress.EquippedCount));       // 第一下只是选中
        Assert.True(bag.Click(x, y, progress));
        Assert.Equal((1, 1), (bag.Changes, progress.EquippedCount));
        Assert.Equal(EquipmentParts.Coat, progress.Worn.Get(EquipmentParts.Coat).Part);
    }

    [Fact]
    public void ClickingAnEquipmentSlotSelectsItAndClickingTheSelectedOneAgainTakesItOff()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        progress.AddItem(Gear(1));
        bag.Activate(progress);
        (int x, int y) = CenterOfSlot(0);
        Assert.True(bag.Click(x, y, progress));
        Assert.Equal((true, 0, 1), (bag.OnEquipment, bag.EquipCursor, progress.EquippedCount));
        Assert.True(bag.Click(x, y, progress));
        Assert.Equal((0, 2), (progress.EquippedCount, bag.Changes));
    }

    [Fact]
    public void ClickingOutsideAnyControlIsNotHandledAndClickingACellAfterASlotLeavesThePanel()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        Assert.False(bag.Click(0, 0, progress));
        bag.Move(-1, 0);
        Assert.True(bag.OnEquipment);
        (int x, int y) = CenterOfCell(3);
        Assert.True(bag.Click(x, y, progress));
        Assert.Equal((false, 3), (bag.OnEquipment, bag.Cursor));
    }

    // ------------------------------------------------------------------ 流程

    [Fact]
    public void TheBagOpensInTownAndDungeonOnlyAndTogglesShut()
    {
        GameFlow flow = NewFlow();
        Assert.False(flow.ToggleBag());                                       // 还没开始
        flow.EnterTown("start");
        Assert.True(flow.ToggleBag());
        Assert.True(flow.Bag.Open);
        Assert.True(flow.ToggleBag());
        Assert.False(flow.Bag.Open);
        flow.EnterSelect();
        Assert.False(flow.ToggleBag());
        Assert.False(flow.Bag.Open);
        flow.StartDungeon(A.Path, "start");
        Assert.True(flow.ToggleBag());
        Assert.True(flow.Bag.Open);
    }

    [Fact]
    public void TheBagAndTheMenuNeverAreOpenTogetherAndEscClosesTheBagFirst()
    {
        GameFlow flow = NewFlow();
        flow.EnterTown("start");
        Assert.True(flow.ToggleMenu());
        Assert.False(flow.ToggleBag());                                       // 菜单开着:背包不开
        Assert.False(flow.Bag.Open);
        flow.ToggleMenu();
        Assert.True(flow.ToggleBag());
        Assert.True(flow.ToggleMenu());                                       // Esc:先关背包
        Assert.False(flow.Bag.Open);
        Assert.False(flow.Menu.Open);
        Assert.True(flow.ToggleMenu());
        Assert.True(flow.Menu.Open);
    }

    [Fact]
    public void AnOpenBagHoldsBackTheStageResultsAndASwitchOfStageClosesIt()
    {
        GameFlow flow = NewFlow();
        flow.StartDungeon(A.Path, "start");
        var run = (FakeRun)flow.Run;
        flow.ToggleBag();
        run.Failed = true;
        Assert.False(flow.Poll());                                            // 背包开着:游戏暂停,失败先不处理
        Assert.Equal((GameStage.Dungeon, 0), (flow.Stage, flow.Failures));
        flow.ToggleBag();
        Assert.True(flow.Poll());
        Assert.Equal((GameStage.Town, 1), (flow.Stage, flow.Failures));

        flow.ToggleBag();
        Assert.True(flow.Bag.Open);
        flow.EnterSelect();
        Assert.False(flow.Bag.Open);                                          // 换阶段时背包关上
    }

    // ------------------------------------------------------------------ 画法

    private sealed class StubSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public List<(string, int)> Asked { get; } = new List<(string, int)>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
        {
            Asked.Add((imageName, frame));
            return Frames.TryGetValue((imageName, frame), out sprite);
        }
    }

    private static SpriteFrame Opaque(int w, int h, int offsetX = 0, int offsetY = 0, int canvasW = -1, int canvasH = -1)
    {
        byte[] rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i++)
        {
            rgba[i] = 255;
        }
        return new SpriteFrame(w, h, offsetX, offsetY, canvasW < 0 ? w : canvasW, canvasH < 0 ? h : canvasH, rgba);
    }

    private static List<SceneSprite> Draw(BagWindow bag, HeroProgress progress, ISpriteSource sprites = null, EquipmentStats worn = default)
    {
        return BagWindowArt.Build(bag, progress, sprites, new OverlayArt(), worn);
    }

    private static int Count(List<SceneSprite> sprites, string tag)
    {
        return sprites.Count(s => s.AnimationPath == tag);
    }

    [Fact]
    public void AClosedWindowDrawsNothingAndAnOpenOneNeedsAProgress()
    {
        var bag = new BagWindow();
        Assert.Empty(Draw(bag, Progress()));
        Assert.Empty(Draw(bag, null));                                        // 没开:不看进度
        bag.Show(GameStage.Town);
        Assert.Throws<ArgumentNullException>(() => Draw(bag, null));
    }

    [Fact]
    public void AnEmptyOpenWindowDrawsTheChromeTwelveSlotsFourTabsAndFiftySixCells()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        List<SceneSprite> sprites = Draw(bag, progress);
        foreach (string tag in new[] { "bag-dim", "bag-frame", "bag-panel", "bag-title-bar", "bag-title", "bag-gold", "bag-caption", "bag-hint" })
        {
            Assert.Equal(1, Count(sprites, tag));
        }
        Assert.Equal(EquipmentSlots.Count, Count(sprites, "bag-slot"));
        Assert.Equal(EquipmentSlots.Count, Count(sprites, "bag-slot-label"));           // 空格上写部位缩写
        Assert.Equal((Inventory.TabCount - 1, 1, Inventory.TabCount), (Count(sprites, "bag-tab"), Count(sprites, "bag-tab-selected"), Count(sprites, "bag-tab-label")));
        Assert.Equal(Inventory.SlotsPerTab, Count(sprites, "bag-cell"));
        Assert.Equal(1, Count(sprites, "bag-cell-selected"));
        Assert.Equal(0, Count(sprites, "bag-slot-selected"));
        Assert.Equal(3, Count(sprites, "bag-stat"));
        Assert.Equal(0, Count(sprites, "bag-info"));                                    // 光标在空格子上:没有物品说明
        Assert.Equal(0, Count(sprites, "bag-icon-block") + Count(sprites, "bag-icon") + Count(sprites, "bag-count"));
        Assert.Equal(sprites.Select((s, i) => i).ToList(), sprites.Select(s => s.SortKey).ToList());      // 画的次序就是排序键
    }

    [Fact]
    public void TheCursorHighlightFollowsWhetherItIsOnTheGridOrTheEquipmentPanel()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        bag.Move(-1, 0);
        List<SceneSprite> sprites = Draw(bag, progress);
        Assert.Equal((0, 1), (Count(sprites, "bag-cell-selected"), Count(sprites, "bag-slot-selected")));
        (int x, int y, _, _) = BagWindow.SlotRect(bag.EquipCursor);
        SceneSprite selected = sprites.First(s => s.AnimationPath == "bag-slot-selected");
        Assert.Equal((x - 2, y - 2), (selected.ScreenX, selected.ScreenY));
    }

    [Fact]
    public void ItemsWithoutIconsGetRarityColoredBlocksAndAStackShowsItsCount()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        progress.AddItem(Gear(1));
        progress.AddItem(Gear(2, EquipmentParts.Coat));
        List<SceneSprite> sprites = Draw(bag, progress);
        Assert.Equal((2, 2, 0), (Count(sprites, "bag-icon-block"), Count(sprites, "bag-rarity"), Count(sprites, "bag-count")));
        bag.NextTab(1);
        progress.AddItem(Potion(9), 12);
        sprites = Draw(bag, progress);
        Assert.Equal((1, 1, 1), (Count(sprites, "bag-icon-block"), Count(sprites, "bag-rarity"), Count(sprites, "bag-count")));
        SceneSprite count = sprites.First(s => s.AnimationPath == "bag-count");
        (int cx, int cy, int cw, int ch) = BagWindow.CellRect(0);
        Assert.True(count.ScreenX >= cx && count.ScreenX + count.Frame.Width <= cx + cw);
        Assert.True(count.ScreenY >= cy && count.ScreenY + count.Frame.Height <= cy + ch);
    }

    [Fact]
    public void AnIconComesFromTheItemScriptsImageAndFrameAndIsCenteredInItsCell()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        ItemInfo sword = Gear(1);
        sword.Icon = "Item/Icons.img";
        sword.IconFrame = 7;
        progress.AddItem(sword);
        var sprites = new StubSprites();
        sprites.Frames[("sprite/item/icons.img", 7)] = Opaque(20, 20, offsetX: 1, offsetY: -1, canvasW: 24, canvasH: 28);
        List<SceneSprite> drawn = Draw(bag, progress, sprites);
        Assert.Contains(("sprite/item/icons.img", 7), sprites.Asked);
        SceneSprite icon = drawn.First(s => s.AnimationPath == "bag-icon");
        (int cx, int cy, int cw, int ch) = BagWindow.CellRect(0);
        Assert.Equal((cx + (cw - 24) / 2 + 1, cy + (ch - 28) / 2 - 1), (icon.ScreenX, icon.ScreenY));
        Assert.Equal(0, Count(drawn, "bag-icon-block"));

        sprites.Frames[("sprite/item/icons.img", 7)] = new SpriteFrame(1, 1, 0, 0, 1, 1, new byte[4]);            // 透明的占位帧:退回色块
        Assert.Equal((0, 1), (Count(Draw(bag, progress, sprites), "bag-icon"), Count(Draw(bag, progress, sprites), "bag-icon-block")));
    }

    [Fact]
    public void EquipmentTheHeroCannotWearIsDimmedAndWearableOnesAreNot()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress(level: 5);
        progress.AddItem(Gear(1));
        progress.AddItem(Gear(2, EquipmentParts.Weapon, 1, "[mage]"));
        progress.AddItem(Gear(3, EquipmentParts.Weapon, 50));
        progress.AddItem(Gear(4, EquipmentParts.Creature));
        List<SceneSprite> sprites = Draw(bag, progress);
        Assert.Equal(3, Count(sprites, "bag-icon-dim"));
        Assert.All(sprites.Where(s => s.AnimationPath == "bag-icon-dim"), s => Assert.Equal(BagWindowArt.UnusableAlpha, s.TintAlpha));
        (int cx, int cy, _, _) = BagWindow.CellRect(0);
        Assert.DoesNotContain(sprites, s => s.AnimationPath == "bag-icon-dim" && s.ScreenX == cx && s.ScreenY == cy);
    }

    [Fact]
    public void AWornItemFillsItsSlotInsteadOfTheLabel()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        progress.AddItem(Gear(1));
        bag.Activate(progress);
        List<SceneSprite> sprites = Draw(bag, progress);
        Assert.Equal(EquipmentSlots.Count - 1, Count(sprites, "bag-slot-label"));
        Assert.Equal((1, 1), (Count(sprites, "bag-icon-block"), Count(sprites, "bag-slot-rarity")));
    }

    [Fact]
    public void TheStatLinesAndTheDetailLinesFollowTheSelection()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress(level: 4);
        ItemInfo sword = Gear(1, EquipmentParts.Weapon, 9);
        progress.AddItem(sword);
        Assert.Equal("LV 9  GRADE 10  RARITY 2  PRICE 300", BagWindowArt.InfoText(bag, sword));
        Assert.Equal("NEED LEVEL 9", BagWindowArt.FitText(bag, sword, progress));
        Assert.Equal("CAN EQUIP", BagWindowArt.FitText(bag, Gear(2), progress));
        Assert.Equal("WRONG CLASS", BagWindowArt.FitText(bag, Gear(3, EquipmentParts.Weapon, 1, "[mage]"), progress));
        Assert.Equal(string.Empty, BagWindowArt.FitText(bag, Potion(1), progress));
        Assert.Same(sword, BagWindowArt.SelectedItem(bag, progress));
        Assert.Equal(string.Empty, BagWindowArt.InfoText(bag, null));
        bag.Move(1, 0);
        Assert.Null(BagWindowArt.SelectedItem(bag, progress));
        bag.Move(-1, 0);
        bag.Move(-1, 0);
        Assert.True(bag.OnEquipment);
        Assert.Equal("EMPTY SLOT", BagWindowArt.InfoText(bag, BagWindowArt.SelectedItem(bag, progress)));
        Assert.Equal(string.Empty, BagWindowArt.FitText(bag, sword, progress));      // 装备栏上不说穿不穿得了

        List<SceneSprite> sprites = Draw(bag, progress, null, new EquipmentStats(31, 12));
        Assert.Equal(3, Count(sprites, "bag-stat"));
        Assert.Equal(1, Count(sprites, "bag-info"));
        Assert.Equal(0, Count(sprites, "bag-message"));                              // 装备栏上没有"穿不穿得了"的提示,也没有操作结果
    }

    [Fact]
    public void AnOperationResultReplacesTheFitLine()
    {
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        progress.AddItem(Gear(1, EquipmentParts.Weapon, 1, "[mage]"));
        List<SceneSprite> before = Draw(bag, progress);
        Assert.Equal(1, Count(before, "bag-message"));                               // 没操作过:显示"穿不穿得了",橙字
        SceneSprite fit = before.First(s => s.AnimationPath == "bag-message");
        Assert.True(HasInk(fit.Frame, OverlayArt.Orange));
        Assert.False(HasInk(fit.Frame, OverlayArt.Yellow));
        bag.Activate(progress);
        List<SceneSprite> sprites = Draw(bag, progress);
        Assert.Equal(1, Count(sprites, "bag-message"));
        SceneSprite message = sprites.First(s => s.AnimationPath == "bag-message");
        Assert.True(HasInk(message.Frame, OverlayArt.Yellow));                       // 操作结果是黄字
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

    [Fact]
    public void TheRarityColorsClampToTheTableAndEveryTextCanBeDrawn()
    {
        Assert.Equal(BagWindowArt.RarityColors[0], BagWindowArt.RarityColor(-3));
        Assert.Equal(BagWindowArt.RarityColors[2], BagWindowArt.RarityColor(2));
        Assert.Equal(BagWindowArt.RarityColors[BagWindowArt.RarityColors.Length - 1], BagWindowArt.RarityColor(99));
        BagWindow bag = Opened();
        HeroProgress progress = Progress();
        progress.AddGold(123456);
        progress.AddItem(Gear(1));
        progress.AddItem(Potion(2), 99);
        bag.NextTab(1);
        // 全部精灵都能画出来(字库覆盖,文字帧不为空)
        List<SceneSprite> sprites = Draw(bag, progress, null, new EquipmentStats(1234, 5678));
        Assert.All(sprites, s => Assert.NotNull(s.Frame));
        Assert.Contains(sprites, s => s.AnimationPath == "bag-gold");
    }
}
