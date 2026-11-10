using Dnf.Game;
using Dnf.Render;
using static Dnf.Play.Tests.ExpTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 道具使用补全在 Play 层的接线(<see cref="BagWindow"/> 按派发表分类:回复药走会话、开启类在城镇开、传送只记请求、登记类拒绝):
/// 背包里按回车开箱的结果与提示、失败什么都不动、与药水共用的背包与冷却、快捷栏资格、药水路径永远不会扣掉箱子。
/// 编号、数量全是合成的(9000xx 是箱子,9010xx 是奖励),期望值是手算。
/// </summary>
public class ItemUseTests
{
    // ------------------------------------------------------------------ 工具

    private static readonly int BoosterType = StackableTypes.FromTag("[booster]");
    private static readonly int PackageType = StackableTypes.FromTag("[usable cera package]");
    private static readonly int TeleportType = StackableTypes.FromTag("[teleport potion]");

    private static ItemInfo Stack(int code, int type, int limit = 100, int coolMs = 0, int coolGroup = -1, int minLevel = 0)
    {
        return new ItemInfo(code, "stackable/test/s" + code + ".stk", ItemKind.Stackable)
        {
            StackableType = type,
            StackLimit = limit,
            CoolTimeMs = coolMs,
            CoolGroup = coolGroup,
            MinimumLevel = minLevel,
        };
    }

    private static ItemInfo Material(int code) => Stack(code, StackableTypes.Etc);

    private static ItemInfo Tonic(int code, int hp = 50)
    {
        ItemInfo item = Stack(code, StackableTypes.Waste, 99);
        item.HpRecovery = new RecoveryEffect(false, hp, 0);
        return item;
    }

    private static ItemInfo Package(int code, int coolMs = 0, int coolGroup = -1, params (int Code, int Count)[] entries)
    {
        ItemInfo pack = Stack(code, PackageType, 1000, coolMs, coolGroup);
        pack.Contents = new ItemContents(null, entries.Select(e => new PackageEntry(e.Code, e.Count)).ToList(), false);
        return pack;
    }

    private static ItemInfo Box(int code, int coolMs, params BoosterBlock[] blocks)
    {
        ItemInfo box = Stack(code, BoosterType, 1000, coolMs);
        box.Contents = new ItemContents(blocks, null, false);
        return box;
    }

    private static BoosterBlock Block(int draws, params (int Code, int Weight, int Count)[] entries)
    {
        return new BoosterBlock(BoosterKind.Etc, draws, entries.Select(e => new BoosterEntry(e.Code, e.Weight, e.Count)));
    }

    private static Func<int, ItemInfo> Lookup(params ItemInfo[] items)
    {
        Dictionary<int, ItemInfo> map = items.ToDictionary(i => i.Code);
        return code => map.TryGetValue(code, out ItemInfo info) ? info : null;
    }

    /// <summary>开着背包窗口、接好开箱器(随机源固定给 <paramref name="roll"/>),切到 <paramref name="tab"/> 页。</summary>
    private static BagWindow Opened(Func<int, ItemInfo> lookup, GameStage stage = GameStage.Town, int roll = 0, InventoryTab tab = InventoryTab.Consumable)
    {
        var bag = new BagWindow { Opener = new ItemOpener(lookup, max => Math.Min(roll, max - 1)) };
        Assert.True(bag.Show(stage));
        for (int i = 0; i < Inventory.TabCount && bag.Tab != tab; i++)
        {
            bag.NextTab(1);
        }
        Assert.Equal(tab, bag.Tab);
        return bag;
    }

    private static string Snapshot(HeroProgress p)
    {
        var parts = new List<string>();
        for (int t = 0; t < Inventory.TabCount; t++)
        {
            foreach (ItemStack stack in p.Bag.Tab((InventoryTab)t))
            {
                parts.Add(t + ":" + stack.Info.Code + "x" + stack.Count);
            }
        }
        return string.Join(",", parts) + "|cd" + p.CooldownsActive + "|quick" + p.QuickFilled + "|gained" + p.ItemsGained;
    }

    // ------------------------------------------------------------------ 开箱:成功

    [Fact]
    public void EnterOnAPackageInTownGivesEveryEntryThroughTheSharedBagAndShowsUsed()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo pack = Package(900002, 0, -1, (901001, 2), (901002, 1));
        ItemInfo tonic = Tonic(901002);
        p.AddItem(pack, 2);
        BagWindow bag = Opened(Lookup(pack, Material(901001), tonic));

        Assert.True(bag.Activate(p));

        Assert.Equal(("USED", UseResult.Ok, 1, 1), (bag.Message, bag.LastUse, bag.Uses, bag.Opens));
        Assert.Equal(new[] { (901001, 2), (901002, 1) }, bag.LastOpen.Grants.Select(g => (g.Info.Code, g.Count)).ToArray());
        Assert.Equal((1, 2, 1), (p.Bag.CountOf(900002), p.Bag.CountOf(901001), p.Bag.CountOf(901002)));
        Assert.Equal(0, p.Quick.IndexOf(901002));                        // 开出来的药水和拾到的一样自动进快捷栏
        Assert.Equal(2 + 2 + 1, p.ItemsGained);                          // 放箱子 2 件 + 开出来 3 件,同一条拾取累计
    }

    [Fact]
    public void ARandomBoxGivesTheEntryTheRollSelected()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo box = Box(900001, 0, Block(1, (901001, 3, 1), (901002, 7, 4)));
        p.AddItem(box, 1);
        Func<int, ItemInfo> lookup = Lookup(box, Material(901001), Material(901002));

        BagWindow low = Opened(lookup, GameStage.Town, 2);                // 累计 3、10:抽到 2 → 第一条
        Assert.True(low.Activate(p));
        Assert.Equal((1, 0), (p.Bag.CountOf(901001), p.Bag.CountOf(901002)));

        p.AddItem(box, 1);
        BagWindow high = Opened(lookup, GameStage.Town, 3);               // 抽到 3 → 第二条
        Assert.True(high.Activate(p));
        Assert.Equal((1, 4), (p.Bag.CountOf(901001), p.Bag.CountOf(901002)));
        Assert.Equal(0, p.Bag.CountOf(900001));
    }

    [Fact]
    public void OpeningNeverAsksTheSessionCallback()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo pack = Package(900002, 0, -1, (901001, 1));
        p.AddItem(pack, 1);
        BagWindow bag = Opened(Lookup(pack, Material(901001)));
        bag.UseItem = item => throw new InvalidOperationException("开箱不该问会话");

        Assert.True(bag.Activate(p));
        Assert.Equal(1, p.Bag.CountOf(901001));
    }

    [Fact]
    public void ClickingTheSelectedBoxCellAgainOpensItLikeEnter()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo pack = Package(900002, 0, -1, (901001, 1));
        p.AddItem(pack, 1);
        BagWindow bag = Opened(Lookup(pack, Material(901001)));
        (int x, int y, int w, int h) = BagWindow.CellRect(0);

        Assert.True(bag.Click(x + w / 2, y + h / 2, p));                  // 第一次点:选中(光标本来就在这一格,所以这一下就算"再点")
        Assert.Equal(1, p.Bag.CountOf(901001));
        Assert.Equal(0, p.Bag.CountOf(900002));
    }

    // ------------------------------------------------------------------ 开箱:失败不动

    [Fact]
    public void TheDungeonRefusesBoxesAndLeavesEverythingAlone()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo pack = Package(900002, 1000, -1, (901001, 1));
        p.AddItem(pack, 2);
        string before = Snapshot(p);
        BagWindow bag = Opened(Lookup(pack, Material(901001)), GameStage.Dungeon);

        Assert.False(bag.Activate(p));

        Assert.Equal(("CANNOT USE HERE", UseResult.Unavailable, 0, 0), (bag.Message, bag.LastUse, bag.Uses, bag.Opens));
        Assert.Equal((UseResult.Unavailable, 0), (bag.LastOpen.Result, bag.LastOpen.Grants.Count));
        Assert.Equal(before, Snapshot(p));
    }

    [Fact]
    public void ARefusedOpenReplacesTheLastOpenSoNoOldGrantsLinger()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo pack = Package(900002, 0, -1, (901001, 1));
        p.AddItem(pack, 2);
        BagWindow bag = Opened(Lookup(pack, Material(901001)));
        Assert.Null(bag.LastOpen);

        Assert.True(bag.Activate(p));
        Assert.Equal((true, 1), (bag.LastOpen.Ok, bag.LastOpen.Grants.Count));
        pack.MinimumLevel = 99;
        Assert.False(bag.Activate(p));
        Assert.Equal((UseResult.LevelTooLow, 0), (bag.LastOpen.Result, bag.LastOpen.Grants.Count));
        bag.Opener = null;
        Assert.False(bag.Activate(p));
        Assert.Equal((UseResult.NotYet, 0), (bag.LastOpen.Result, bag.LastOpen.Grants.Count));
        Assert.Equal(1, bag.Opens);
    }

    [Fact]
    public void ALowLevelOrAWrongClassGetsTheSameMessagesAsPotionsAndNothingMoves()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo pack = Package(900002, 0, -1, (901001, 1));
        p.AddItem(pack, 1);
        BagWindow bag = Opened(Lookup(pack, Material(901001)));
        string before = Snapshot(p);

        pack.MinimumLevel = 60;
        Assert.False(bag.Activate(p));
        Assert.Equal(("LEVEL TOO LOW", UseResult.LevelTooLow), (bag.Message, bag.LastUse));
        pack.MinimumLevel = 0;
        pack.UsableJobs.Add("[mage]");
        Assert.False(bag.Activate(p));
        Assert.Equal(("WRONG CLASS", UseResult.WrongJob), (bag.Message, bag.LastUse));
        Assert.Equal(before, Snapshot(p));
        Assert.Equal((0, 0), (bag.Uses, bag.Opens));
    }

    [Fact]
    public void AFullBagRefusesWithBagFullAndKeepsTheBox()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo pack = Package(900002, 0, -1, (901001, 1));
        p.AddItem(pack, 1);
        for (int i = 0; i < Inventory.SlotsPerTab; i++)
        {
            Assert.True(p.AddItem(Material(910000 + i)));                 // 材料页 56 格各占一格
        }
        string before = Snapshot(p);
        BagWindow bag = Opened(Lookup(pack, Material(901001)));

        Assert.False(bag.Activate(p));

        Assert.Equal(("BAG FULL", UseResult.BagFull, 0, 0), (bag.Message, bag.LastUse, bag.Uses, bag.Opens));
        Assert.Equal(before, Snapshot(p));
        Assert.Equal(1, p.Bag.CountOf(900002));
    }

    [Fact]
    public void ABoxWithoutAnOpenerIsNotYetAndNothingMoves()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo pack = Package(900002, 0, -1, (901001, 1));
        p.AddItem(pack, 1);
        BagWindow bag = Opened(Lookup(pack, Material(901001)));
        bag.Opener = null;
        string before = Snapshot(p);

        Assert.False(bag.Activate(p));

        Assert.Equal(("CANNOT USE YET", UseResult.NotYet), (bag.Message, bag.LastUse));
        Assert.Equal(before, Snapshot(p));
    }

    [Fact]
    public void ContentThisVersionCannotGrantIsNotYetAndNothingMoves()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo cera = Stack(900010, BoosterType);
        cera.Contents = new ItemContents(new[] { new BoosterBlock(BoosterKind.Cera, 1, new[] { new BoosterEntry(1, 1, 100) }) }, null, false);
        p.AddItem(cera, 1);
        string before = Snapshot(p);
        BagWindow bag = Opened(Lookup(cera));

        Assert.False(bag.Activate(p));

        Assert.Equal(("CANNOT USE YET", UseResult.NotYet), (bag.Message, bag.LastUse));
        Assert.Equal(before, Snapshot(p));
    }

    [Theory]
    [InlineData("[dye]")]
    [InlineData("[disguise]")]
    [InlineData("[booster selection]")]
    [InlineData("[multi upgradable legacy]")]
    [InlineData("[upgrade limit cube]")]
    [InlineData("[only effect]")]
    [InlineData("[upgradable legacy]")]
    [InlineData("[creature expitem]")]
    [InlineData("[recipe]")]
    [InlineData("[cera package]")]
    [InlineData("[cera booster]")]
    [InlineData("[booster random]")]
    [InlineData("[expert town potion]")]
    [InlineData("[material]")]
    [InlineData("[quest]")]
    [InlineData("[unlimited waste]")]
    public void RegisteredOrUnreadClassesAreRefusedWithCannotUseYetAndNothingMoves(string tag)
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo item = Stack(900100, StackableTypes.FromTag(tag));
        Assert.NotEqual(StackableTypes.Unknown, item.StackableType);
        Assert.True(ItemUseTable.StatusOf(item) == ItemUseStatus.Registered || ItemUseTable.StatusOf(item) == ItemUseStatus.Unread);
        p.AddItem(item, 3);
        string before = Snapshot(p);
        BagWindow bag = Opened(Lookup(item), GameStage.Town, 0, Inventory.TabOf(item));
        BagWindow inDungeon = Opened(Lookup(item), GameStage.Dungeon, 0, Inventory.TabOf(item));

        Assert.False(bag.Activate(p));
        Assert.False(inDungeon.Activate(p));

        Assert.Equal(("CANNOT USE YET", UseResult.NotYet, 0), (bag.Message, bag.LastUse, bag.Uses));
        Assert.Equal(UseResult.NotYet, inDungeon.LastUse);
        Assert.Equal(before, Snapshot(p));
    }

    // ------------------------------------------------------------------ 冷却与背包共用

    [Fact]
    public void AnOpenedBoxStartsTheSharedCooldownAndTheSecondOneWaitsForIt()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo pack = Package(900002, 1000, 9, (901001, 1));
        ItemInfo tonic = Tonic(901002);
        tonic.CoolGroup = 9;                                              // 与箱子同一个冷却组:共用冷却
        tonic.CoolTimeMs = 500;
        p.AddItem(pack, 2);
        BagWindow bag = Opened(Lookup(pack, Material(901001)));

        Assert.True(bag.Activate(p));
        Assert.True(p.Cooldowns.IsCooling(pack));
        Assert.True(p.Cooldowns.IsCooling(tonic));
        Assert.False(bag.Activate(p));
        Assert.Equal(("COOLING DOWN", UseResult.Cooling), (bag.Message, bag.LastUse));
        Assert.Equal((1, 1), (p.Bag.CountOf(900002), p.Bag.CountOf(901001)));

        p.AdvanceCooldowns(1000);                                         // 宿主按墙钟推进冷却(城镇里也走)
        Assert.True(bag.Activate(p));
        Assert.Equal((0, 2), (p.Bag.CountOf(900002), p.Bag.CountOf(901001)));
        Assert.Equal(2, bag.Opens);
    }

    [Fact]
    public void APotionCoolingStopsAnOpenOfTheSameGroup()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo pack = Package(900002, 1000, 9, (901001, 1));
        ItemInfo tonic = Tonic(901002);
        tonic.CoolGroup = 9;
        tonic.CoolTimeMs = 5000;
        p.AddItem(pack, 1);
        p.Cooldowns.Start(tonic);                                         // 刚喝过同组的药
        string before = Snapshot(p);
        BagWindow bag = Opened(Lookup(pack, Material(901001)));

        Assert.False(bag.Activate(p));

        Assert.Equal(UseResult.Cooling, bag.LastUse);
        Assert.Equal(before, Snapshot(p));
    }

    // ------------------------------------------------------------------ 药水路径与快捷栏

    [Fact]
    public void TheRecoveryPathNeverConsumesABoxNoMatterHowItIsReached()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo box = Box(900001, 0, Block(1, (901001, 1, 1)));
        box.HpRecovery = new RecoveryEffect(false, 50, 0);                // 即使脚本里带了回复字段
        ItemInfo pack = Package(900002, 0, -1, (901001, 1));
        pack.MpRecovery = new RecoveryEffect(false, 50, 0);
        p.AddItem(box, 2);
        p.AddItem(pack, 2);
        HeroVitals vitals = HeroVitals.FromStats(p.Level, p.Stats);
        vitals.Damage(vitals.Hp / 2);
        string before = Snapshot(p);

        Assert.Equal(UseResult.NotConsumable, p.UseConsumable(box, vitals));
        Assert.Equal(UseResult.NotConsumable, p.UseConsumable(pack, vitals));
        Assert.Equal(0, p.QuickFilled);                                   // 拾到时不进快捷栏
        Assert.True(p.Quick.Set(0, box));                                 // 存档还原那条路径硬放进去也一样
        Assert.Equal(UseResult.NotConsumable, p.UseQuickSlot(0, vitals));
        Assert.Equal(before.Replace("quick0", "quick1"), Snapshot(p));
        Assert.Equal(2, p.Bag.CountOf(900001));
    }

    [Fact]
    public void OnlyPotionsAreAutoPlacedIntoTheQuickSlots()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo scroll = Stack(900300, TeleportType);
        scroll.HpRecovery = new RecoveryEffect(false, 50, 0);
        p.AddItem(Box(900001, 0, Block(1, (901001, 1, 1))), 1);
        p.AddItem(Package(900002, 0, -1, (901001, 1)), 1);
        p.AddItem(scroll, 1);
        Assert.Equal(0, p.QuickFilled);
        Assert.True(p.AddItem(Tonic(900050)));
        Assert.Equal(1, p.QuickFilled);
        Assert.Equal(0, p.Quick.IndexOf(900050));
    }

    [Fact]
    public void PotionsInTheDungeonStillGoThroughTheSessionAndBoxesNever()
    {
        GameFlow flow = NewFlow();
        flow.StartDungeon(A.Path, "start");
        var run = (ItemRun)flow.Run;
        HeroProgress p = Progress(level: 50);
        ItemInfo tonic = Tonic(900050);
        ItemInfo pack = Package(900002, 0, -1, (901001, 1));
        p.AddItem(tonic, 2);
        p.AddItem(pack, 1);
        flow.Bag.Opener = new ItemOpener(Lookup(pack, Material(901001)), max => 0);
        Assert.True(flow.ToggleBag());
        flow.Bag.NextTab(1);

        Assert.True(flow.Bag.Activate(p));                                // 光标在第一格:药水,交给会话
        Assert.Equal(new[] { 900050 }, run.Items);
        Assert.Equal(("USED", UseResult.Ok), (flow.Bag.Message, flow.Bag.LastUse));
        flow.Bag.Move(1, 0);                                              // 第二格:礼包,副本里不能开,也不该问会话
        Assert.False(flow.Bag.Activate(p));
        Assert.Equal(new[] { 900050 }, run.Items);
        Assert.Equal(("CANNOT USE HERE", UseResult.Unavailable), (flow.Bag.Message, flow.Bag.LastUse));
        Assert.Equal(1, p.Bag.CountOf(900002));
    }

    [Fact]
    public void InTownTheBoxOpensWhileThePotionStillSaysCannotUseHere()
    {
        GameFlow flow = NewFlow();
        flow.EnterTown("start");
        HeroProgress p = Progress(level: 50);
        ItemInfo tonic = Tonic(900050);
        ItemInfo pack = Package(900002, 0, -1, (901001, 1));
        p.AddItem(tonic, 2);
        p.AddItem(pack, 1);
        flow.Bag.Opener = new ItemOpener(Lookup(pack, Material(901001)), max => 0);
        Assert.True(flow.ToggleBag());
        flow.Bag.NextTab(1);

        Assert.False(flow.Bag.Activate(p));                               // 药水:城镇里没有生命值可回复(原行为不变)
        Assert.Equal(("CANNOT USE HERE", UseResult.Unavailable), (flow.Bag.Message, flow.Bag.LastUse));
        Assert.Equal(2, p.Bag.CountOf(900050));
        flow.Bag.Move(1, 0);
        Assert.True(flow.Bag.Activate(p));                                // 礼包:开
        Assert.Equal(("USED", UseResult.Ok), (flow.Bag.Message, flow.Bag.LastUse));
        Assert.Equal((0, 1), (p.Bag.CountOf(900002), p.Bag.CountOf(901001)));
    }

    // ------------------------------------------------------------------ 传送桩

    [Fact]
    public void ATeleportItemInTownOnlyRecordsARequestAndTakesNothing()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo scroll = Stack(900300, TeleportType, 100, 3000);
        p.AddItem(scroll, 2);
        string before = Snapshot(p);
        BagWindow bag = Opened(Lookup(scroll));

        Assert.False(bag.Activate(p));

        Assert.Equal(("CANNOT USE YET", UseResult.Pending, 0), (bag.Message, bag.LastUse, bag.Uses));
        Assert.Same(scroll, bag.Teleports.Pending.Item);
        Assert.Equal(before, Snapshot(p));
        Assert.True(bag.Teleports.Commit(p.Bag, p.Cooldowns, 2));          // 城镇线接上之后:扣一件、起冷却
        Assert.Equal(1, p.Bag.CountOf(900300));
        Assert.True(p.Cooldowns.IsCooling(scroll));
    }

    [Fact]
    public void ATeleportItemInTheDungeonIsRefusedAndRecordsNothing()
    {
        HeroProgress p = Progress(level: 50);
        ItemInfo scroll = Stack(900300, TeleportType);
        p.AddItem(scroll, 1);
        string before = Snapshot(p);
        BagWindow bag = Opened(Lookup(scroll), GameStage.Dungeon);

        Assert.False(bag.Activate(p));

        Assert.Equal(("CANNOT USE HERE", UseResult.Unavailable), (bag.Message, bag.LastUse));
        Assert.Null(bag.Teleports.Pending);
        Assert.Equal(before, Snapshot(p));
    }

    // ------------------------------------------------------------------ 窗口状态与提示

    [Fact]
    public void ShowRemembersTheStageItWasOpenedIn()
    {
        var bag = new BagWindow();
        Assert.Equal(GameStage.Town, bag.Stage);
        Assert.True(bag.Show(GameStage.Dungeon));
        Assert.Equal(GameStage.Dungeon, bag.Stage);
        bag.Close();
        Assert.False(bag.Show(GameStage.Loading));                         // 打不开的阶段不改记录
        Assert.Equal(GameStage.Dungeon, bag.Stage);
        Assert.True(bag.Show(GameStage.Town));
        Assert.Equal(GameStage.Town, bag.Stage);
    }

    [Theory]
    [InlineData(UseResult.BagFull, "BAG FULL")]
    [InlineData(UseResult.NotYet, "CANNOT USE YET")]
    [InlineData(UseResult.Pending, "CANNOT USE YET")]
    public void TheNewUseResultsHaveMessagesTheFontAndTheTranslationAlreadyHave(UseResult result, string expected)
    {
        string message = BagWindow.MessageOf(result);
        Assert.Equal(expected, message);
        Assert.All(message, c => Assert.True(OverlayArt.HasGlyph(c), "字库里没有 '" + c + "'"));
        Assert.True(UiText.HasTranslation(message));
    }

    [Fact]
    public void AnEquipmentPieceStillGoesThroughTheWearPathNotTheUsePath()
    {
        HeroProgress p = Progress(level: 50);
        var coat = new ItemInfo(900400, "equipment/test/e900400.equ", ItemKind.Equipment) { Part = EquipmentParts.Coat, Grade = 1 };
        coat.UsableJobs.Add("[swordman]");
        p.AddItem(coat);
        BagWindow bag = Opened(Lookup(coat), GameStage.Town, 0, InventoryTab.Equipment);

        Assert.True(bag.Activate(p));
        Assert.Equal(("EQUIPPED", EquipResult.Ok), (bag.Message, bag.LastResult));
        Assert.Equal((0, 0), (bag.Uses, bag.Opens));
        Assert.Null(bag.LastOpen);
    }

    // ------------------------------------------------------------------ 流程夹具

    private sealed class ItemRun : GameFlowTests.FakeHero, IRunSession, IItemSession
    {
        public ItemRun(string label) : base(label)
        {
        }

        public SettlementChoice Choice { get; set; }

        public bool Failed { get; set; }

        public List<int> Items { get; } = new List<int>();

        public UseResult TryUseQuickSlot(int slot) => UseResult.Ok;

        public UseResult TryUseItem(ItemInfo item)
        {
            Items.Add(item.Code);
            return UseResult.Ok;
        }
    }

    private static readonly DungeonEntry A = new DungeonEntry("dungeon/test/a.dgn", "AAA");

    private static GameFlow NewFlow()
    {
        return new GameFlow(() => new GameFlowTests.FakeHero("town"), (path, run) => new ItemRun("run " + path + " #" + run), new[] { A });
    }
}
