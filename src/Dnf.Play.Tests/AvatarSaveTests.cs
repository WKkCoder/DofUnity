using Dnf.Game;
using Dnf.Render;
using static Dnf.Render.Tests.WardrobeFixture;

namespace Dnf.Play.Tests;

/// <summary>
/// 装扮栏的存档(r15-avatarhost,本项目自己的设计,没有原版证据):<c>avatar 部位号 物品编号</c> 行的写读、从会话抓取(<see cref="AvatarSession.Capture"/>)、
/// 读档还原(<see cref="AvatarSession.Restore"/>,穿不回去的放回背包)与存档员的接法(<see cref="HeroSaver.Capture"/>)。规则层(r14-avatar)定的存档形状:
/// 存 <see cref="AvatarSlots"/> 里每个部位穿着的物品编号,读回用 <see cref="AvatarSlots.Restore"/>。编号全是合成的。
/// </summary>
public class AvatarSaveTests
{
    private const string Header = "DNFUNITY-SAVE 1\n";

    private static HeroSave Parse(string text)
    {
        Assert.True(HeroSave.TryParse(text, out HeroSave save, out string error), error);
        return save;
    }

    private static HeroSave WithRows(params (int Part, int Code)[] rows)
    {
        var save = new HeroSave { Level = 7, Gold = 123 };
        foreach ((int part, int code) in rows)
        {
            save.Avatars.Add(new SavedAvatar(part, code));
        }
        return save;
    }

    /// <summary>另一个主角进度与会话(同一套素材与底座),读档的"新一局"。</summary>
    private static (HeroProgress Progress, AvatarSession Session) FreshRun(AvatarHostRig h, int level = AvatarHostRig.DefaultLevel)
    {
        HeroProgress progress = ExpTests.Progress(level: level);
        return (progress, new AvatarSession(h.Rig.Assets, progress, () => h.Rig.Base));
    }

    // ---------------------------------------------------------------- 文本格式

    [Fact]
    public void AvatarRowsAreWrittenOneByOneInTheOrderTheyAreHeld()
    {
        HeroSave save = WithRows((0, 901), (3, 904));
        string plain = WithRows().ToText();
        string text = save.ToText();
        Assert.Contains("avatar 0 901\navatar 3 904\n", text);
        Assert.Equal(plain, text.Replace("avatar 0 901\navatar 3 904\n", string.Empty));       // 只多了这两行
        Assert.DoesNotContain("avatar", plain);
        Assert.Empty(new HeroSave().Avatars);
    }

    [Fact]
    public void AvatarRowsRoundTripThroughTheText()
    {
        HeroSave save = WithRows((0, 901), (3, 904), (9, 2147483647));
        save.Bag.Add(new SavedStack(905, 1));
        save.Worn.Add(new SavedWorn(EquipmentSlots.First, 930));
        HeroSave back = Parse(save.ToText());
        Assert.Equal(save.ToText(), back.ToText());
        Assert.Equal(new[] { (0, 901), (3, 904), (9, 2147483647) }, back.Avatars.Select(a => (a.Part, a.Code)));
        Assert.Equal(new[] { (905, 1) }, back.Bag.Select(s => (s.Code, s.Count)));
        Assert.Equal(new[] { (EquipmentSlots.First, 930) }, back.Worn.Select(w => (w.Part, w.Code)));
    }

    [Fact]
    public void AnOldSaveWithoutAvatarRowsStillReadsAndHasNoAvatars()
    {
        HeroSave old = Parse(Header + "level 2\ngold 5\n");
        Assert.Empty(old.Avatars);
        Assert.Equal(2, old.Level);
        Assert.DoesNotContain("avatar", old.ToText());
    }

    [Theory]
    [InlineData("avatar", "需要 2 个值")]
    [InlineData("avatar 3", "需要 2 个值")]
    [InlineData("avatar 3 904 1", "需要 2 个值")]
    [InlineData("avatar x 904", "不是整数")]
    [InlineData("avatar 3 y", "不是整数")]
    [InlineData("avatar 3 9.5", "不是整数")]
    [InlineData("avatar -1 904", "超出范围")]
    [InlineData("avatar 10 904", "超出范围")]                // 部位 10 起是普通装备
    [InlineData("avatar 99999999999 904", "超出范围")]
    [InlineData("avatar 3 0", "超出范围")]                   // 编号从 1 起
    [InlineData("avatar 3 -5", "超出范围")]
    [InlineData("avatar 3 2147483648", "超出范围")]
    public void ABadAvatarRowMakesTheWholeSaveUnreadableWithALineNumberAndTheRightReason(string line, string reason)
    {
        // 前面垫一行合法的内容,错误应报在第 3 行(头一行是第 1 行);原因分三类:个数不对、不是整数、超出范围,报错的人要看到真正的那一类
        Assert.False(HeroSave.TryParse(Header + "level 2\n" + line + "\n", out HeroSave save, out string error));
        Assert.Null(save);
        Assert.StartsWith("第 3 行", error);
        Assert.Contains("avatar", error);
        Assert.Contains(reason, error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void TheFirstAndLastAvatarPartsAreAccepted(int part)
    {
        HeroSave save = Parse(Header + "avatar " + part + " 5\n");
        Assert.Equal(new[] { (part, 5) }, save.Avatars.Select(a => (a.Part, a.Code)));
    }

    [Theory]
    [InlineData(1)]                              // 编号从 1 起(0 不是物品)
    [InlineData(2147483647)]                     // 一直到 int 的上界
    public void TheSmallestAndLargestItemCodesAreAccepted(int code)
    {
        HeroSave save = Parse(Header + "avatar 4 " + code + "\n");
        Assert.Equal(new[] { (4, code) }, save.Avatars.Select(a => (a.Part, a.Code)));
        Assert.Equal(save.ToText(), Parse(save.ToText()).ToText());
    }

    // ---------------------------------------------------------------- 从会话抓取

    [Fact]
    public void CaptureWritesEveryWornPartInAscendingOrderAndClearsWhatWasThere()
    {
        using var h = new AvatarHostRig();
        foreach (int part in new[] { 5, 0, 3 })                                    // 故意不按部位号的次序穿
        {
            ItemInfo item = h.Avatar(part);
            h.Give(item);
            h.WearOk(item.Code);
        }
        HeroSave save = WithRows((9, 999), (8, 998));                              // 存档里原有的装扮字段会被清掉
        h.Session.Capture(save);
        Assert.Equal(new[] { (0, 901), (3, 904), (5, 906) }, save.Avatars.Select(a => (a.Part, a.Code)));
        Assert.Equal(7, save.Level);                                               // 别的字段不动
        Assert.Equal(123L, save.Gold);

        Assert.True(h.Session.TakeOff(3));
        h.Session.Capture(save);
        Assert.Equal(new[] { (0, 901), (5, 906) }, save.Avatars.Select(a => (a.Part, a.Code)));
        Assert.True(h.Session.TakeOff(0));
        Assert.True(h.Session.TakeOff(5));
        h.Session.Capture(save);
        Assert.Empty(save.Avatars);
    }

    [Fact]
    public void CaptureWritesTheLastPartOfTheBarTooEvenThoughNothingCanShowIt()
    {
        using var h = new AvatarHostRig();
        ItemInfo hat = h.Avatar(0);
        ItemInfo aurora = h.Make(AvatarHostRig.CodeAurora, Aurora, AvatarSlots.Last);
        h.Give(hat);
        h.WearOk(hat.Code);
        Assert.False(h.Session.CanShow(aurora));                                   // 灵气本版画不出来,正路穿不上
        Assert.True(h.Session.Slots.Restore(AvatarSlots.Last, aurora));            // 栏本身能放(读档还原的口子);抓取不挑能不能画

        var save = new HeroSave();
        h.Session.Capture(save);
        Assert.Equal(new[] { (0, 901), (AvatarSlots.Last, AvatarHostRig.CodeAurora) }, save.Avatars.Select(a => (a.Part, a.Code)));
    }

    // ---------------------------------------------------------------- 读档还原

    [Fact]
    public void RestoreWearsTheSavedItemsAgainAndTheLookIsTheSame()
    {
        using var h = new AvatarHostRig();
        foreach (int part in new[] { 0, 3, 5 })
        {
            ItemInfo item = h.Avatar(part);
            h.Give(item);
            h.WearOk(item.Code);
        }
        TownHero before = h.NewHero();
        h.Session.Sync(before);
        var save = new HeroSave();
        h.Session.Capture(save);
        HeroSave loaded = Parse(save.ToText());                                    // 经过文本,像真的存读一样

        (HeroProgress progress, AvatarSession fresh) = FreshRun(h);
        AvatarRestoreReport report = fresh.Restore(loaded, h.Lookup);
        Assert.Equal((3, 0, 0), (report.Worn, report.Moved, report.Dropped));
        Assert.Equal(new[] { (0, 901), (3, 904), (5, 906) }, AvatarHostRig.WornCodes(fresh));
        Assert.Equal(3, fresh.Wardrobe.WornCount);
        Assert.Empty(progress.Bag.Tab(InventoryTab.Equipment));                    // 穿着的没有多出一份在背包里

        TownHero after = h.NewHero();
        Assert.True(fresh.Sync(after));
        Assert.Equal(AvatarHostRig.Paths(before.Outfit), AvatarHostRig.Paths(after.Outfit));
    }

    [Fact]
    public void RestoreDropsWhatItCannotFind()
    {
        using var h = new AvatarHostRig();
        (HeroProgress progress, AvatarSession fresh) = FreshRun(h);
        h.Avatar(0);
        AvatarRestoreReport report = fresh.Restore(WithRows((0, 901), (3, 999)), h.Lookup);       // 999 不在目录里
        Assert.Equal((1, 0, 1), (report.Worn, report.Moved, report.Dropped));
        Assert.Equal(901, fresh.Slots.Get(0).Code);
        Assert.Null(fresh.Slots.Get(3));
        Assert.Empty(progress.Bag.Tab(InventoryTab.Equipment));
    }

    [Fact]
    public void RestoreMovesWhatTheRulesNoLongerAllowBackToTheBag()
    {
        using var h = new AvatarHostRig();
        (HeroProgress progress, AvatarSession fresh) = FreshRun(h, level: 20);
        h.Make(901, Hat, 0, level: 50);                                            // 等级不够了(存档之后数据变了、或存档被改过)
        h.Make(904, CoatOne, 3, job: "[fighter]");                                 // 职业不符
        h.Make(906, Shoes, 5);
        AvatarRestoreReport report = fresh.Restore(WithRows((0, 901), (3, 904), (5, 906)), h.Lookup);
        Assert.Equal((1, 2, 0), (report.Worn, report.Moved, report.Dropped));
        Assert.Equal(906, fresh.Slots.Get(5).Code);
        Assert.Null(fresh.Slots.Get(0));
        Assert.Null(fresh.Slots.Get(3));
        Assert.Equal(new[] { 901, 904 }, progress.Bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code));
        Assert.Equal(1, fresh.Wardrobe.WornCount);
    }

    [Fact]
    public void RestoreMovesWhatTheWardrobeCannotDrawBackToTheBag()
    {
        using var h = new AvatarHostRig();
        (HeroProgress progress, AvatarSession fresh) = FreshRun(h);
        h.Make(AvatarHostRig.CodeCoatNoImage, CoatNoImage, 3);                     // 图片不在图包里
        h.Make(901, Hat, 0);
        AvatarRestoreReport report = fresh.Restore(WithRows((3, AvatarHostRig.CodeCoatNoImage), (0, 901)), h.Lookup);
        Assert.Equal((1, 1, 0), (report.Worn, report.Moved, report.Dropped));
        Assert.Null(fresh.Slots.Get(3));
        Assert.Equal(new[] { AvatarHostRig.CodeCoatNoImage }, progress.Bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code));
        Assert.Equal(1, fresh.Wardrobe.WornCount);
    }

    [Fact]
    public void RestoreMovesAnItemWhosePartDiffersFromTheRowBackToTheBag()
    {
        using var h = new AvatarHostRig();
        (HeroProgress progress, AvatarSession fresh) = FreshRun(h);
        h.Avatar(3);                                                               // 904 是上衣(部位 3),存档里写在部位 5
        AvatarRestoreReport report = fresh.Restore(WithRows((5, 904)), h.Lookup);
        Assert.Equal((0, 1, 0), (report.Worn, report.Moved, report.Dropped));
        Assert.Equal(0, fresh.Slots.WornCount);
        Assert.Equal(new[] { 904 }, progress.Bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code));
    }

    [Fact]
    public void RestoreWearsTheFirstRowOfAPartAndMovesTheSecondBackToTheBag()
    {
        using var h = new AvatarHostRig();
        (HeroProgress progress, AvatarSession fresh) = FreshRun(h);
        h.Avatar(3);
        h.Make(AvatarHostRig.CodeCoatTwo, CoatTwo, 3);
        AvatarRestoreReport report = fresh.Restore(WithRows((3, 904), (3, AvatarHostRig.CodeCoatTwo)), h.Lookup);
        Assert.Equal((1, 1, 0), (report.Worn, report.Moved, report.Dropped));
        Assert.Equal(904, fresh.Slots.Get(3).Code);
        Assert.Equal(new[] { AvatarHostRig.CodeCoatTwo }, progress.Bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code));
    }

    [Fact]
    public void RestoreDropsWhatItCannotWearWhenTheBagHasNoRoom()
    {
        using var h = new AvatarHostRig();
        (HeroProgress progress, AvatarSession fresh) = FreshRun(h, level: 1);
        ItemInfo hat = h.Make(901, Hat, 0, level: 50);                             // 穿不回去,只能回背包
        h.FillBag(progress);
        Assert.False(progress.Bag.CanAdd(hat));
        int cells = progress.Bag.Tab(InventoryTab.Equipment).Count;

        AvatarRestoreReport report = fresh.Restore(WithRows((0, hat.Code)), h.Lookup);
        Assert.Equal((0, 0, 1), (report.Worn, report.Moved, report.Dropped));
        Assert.Equal(cells, progress.Bag.Tab(InventoryTab.Equipment).Count);
        Assert.Equal(0, fresh.Slots.WornCount);
    }

    [Fact]
    public void RestoreReplacesWhateverTheSessionWasWearingAndRestoringNothingClearsIt()
    {
        using var h = new AvatarHostRig();
        ItemInfo hat = h.Avatar(0);
        h.Give(hat);
        h.WearOk(hat.Code);
        TownHero hero = h.NewHero();
        h.Session.Sync(hero);
        h.Avatar(3);

        AvatarRestoreReport report = h.Session.Restore(WithRows((3, 904)), h.Lookup);
        Assert.Equal((1, 0, 0), (report.Worn, report.Moved, report.Dropped));
        Assert.Null(h.Session.Slots.Get(0));                                       // 原来穿的帽子换掉了,不会回背包(整个替换)
        Assert.Equal(904, h.Session.Slots.Get(3).Code);
        Assert.Equal(1, h.Session.Wardrobe.WornCount);
        Assert.True(h.Session.Sync(hero));
        Assert.DoesNotContain(HatArt.Path, AvatarHostRig.Paths(hero.Outfit));
        Assert.Contains(CoatOneFrontArt.Path, AvatarHostRig.Paths(hero.Outfit));

        report = h.Session.Restore(WithRows(), h.Lookup);
        Assert.Equal((0, 0, 0), (report.Worn, report.Moved, report.Dropped));
        Assert.Equal(0, h.Session.Slots.WornCount);
        Assert.Equal(0, h.Session.Wardrobe.WornCount);
        Assert.True(h.Session.Sync(hero));
        Assert.Same(h.Rig.Base, hero.Outfit);
    }

    [Fact]
    public void RestoreWithoutAWardrobeMovesEverythingBackToTheBag()
    {
        using var h = new AvatarHostRig();
        HeroProgress progress = ExpTests.Progress(level: AvatarHostRig.DefaultLevel);
        var bare = new AvatarSession(h.Rig.Assets, progress, () => null);
        h.Avatar(0);
        h.Avatar(3);
        AvatarRestoreReport report = bare.Restore(WithRows((0, 901), (3, 904)), h.Lookup);
        Assert.Equal((0, 2, 0), (report.Worn, report.Moved, report.Dropped));
        Assert.Equal(0, bare.Slots.WornCount);
        Assert.Equal(2, progress.Bag.Tab(InventoryTab.Equipment).Count);
    }

    [Fact]
    public void RestoreFromDataWithoutAvatarRowsClearsTheBarAndNeverOpensTheItemCatalog()
    {
        using var h = new AvatarHostRig();
        ItemInfo hat = h.Avatar(0);
        h.Give(hat);
        h.WearOk(hat.Code);
        AvatarRestoreReport report = h.Session.RestoreFromData(new HeroSave());
        Assert.Equal((0, 0, 0), (report.Worn, report.Moved, report.Dropped));
        Assert.Equal(0, h.Session.Slots.WornCount);
        Assert.Equal(0, h.Session.Wardrobe.WornCount);
    }

    // ---------------------------------------------------------------- 存档员

    [Fact]
    public void TheSaverWritesTheAvatarRowsAndAFreshRunReadsThemBack()
    {
        using var h = new AvatarHostRig();
        var store = new MemoryHeroSaveStore();
        var saver = new HeroSaver(store, h.Progress) { Capture = h.Session.Capture };
        ItemInfo hat = h.Avatar(0);
        ItemInfo coat = h.Avatar(3);
        ItemInfo spare = h.Make(AvatarHostRig.CodeCoatTwo, CoatTwo, 3);
        h.Give(hat, coat, spare);
        h.WearOk(hat.Code);
        h.WearOk(coat.Code);
        Assert.True(saver.Flush(0));
        Assert.Contains("avatar 0 901\navatar 3 904\n", store.Text);

        // 新的一局:先还原背包等,再还原装扮
        HeroSave loaded = store.Load(out string note);
        Assert.Null(note);
        (HeroProgress progress, AvatarSession fresh) = FreshRun(h);
        progress.Restore(loaded, h.Lookup);
        AvatarRestoreReport report = fresh.Restore(loaded, h.Lookup);
        Assert.Equal((2, 0, 0), (report.Worn, report.Moved, report.Dropped));
        Assert.Equal(h.Codes(), progress.Bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code));
        Assert.Equal(h.WornCodes(), AvatarHostRig.WornCodes(fresh));
        Assert.Equal(AvatarHostRig.Names(h.Session.Wardrobe.Current), AvatarHostRig.Names(fresh.Wardrobe.Current));

        // 读回来的状态与存档一致,存档员不会因此再写一遍
        var saver2 = new HeroSaver(store, progress, loaded) { Capture = fresh.Capture };
        Assert.False(saver2.Flush(1));
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void ACaptureThatChangedOnlyTheAvatarBarStillMakesTheSaverWrite()
    {
        using var h = new AvatarHostRig();
        var store = new MemoryHeroSaveStore();
        var saver = new HeroSaver(store, h.Progress) { Capture = h.Session.Capture };
        Assert.True(saver.Flush(0));
        Assert.False(saver.Flush(1));
        Assert.DoesNotContain("avatar", store.Text);

        // 只动装扮栏(背包、金币、经验都没变):抓取在与上次存的内容比较之前,所以这一次也写
        Assert.True(h.Session.Slots.Restore(0, h.Avatar(0)));
        Assert.True(saver.Flush(2));
        Assert.Contains("avatar 0 901\n", store.Text);
        Assert.Equal(2, store.Writes);

        Assert.True(h.Session.Slots.Restore(3, h.Avatar(3)));
        Assert.True(saver.Tick(20));
        Assert.Contains("avatar 0 901\navatar 3 904\n", store.Text);
    }

    [Fact]
    public void WithoutTheCaptureHookTheAvatarRowsAreNotWritten()
    {
        using var h = new AvatarHostRig();
        var store = new MemoryHeroSaveStore();
        var saver = new HeroSaver(store, h.Progress);
        Assert.Null(saver.Capture);
        h.Give(h.Avatar(0));
        h.WearOk(901);
        Assert.True(saver.Flush(0));
        Assert.DoesNotContain("avatar", store.Text);                               // 这就是宿主要在造存档员的同时接上钩子的原因
    }

    [Fact]
    public void TheCaptureHookIsMulticastSoSeveralStatesCanFeedTheSameSave()
    {
        using var h = new AvatarHostRig();
        var store = new MemoryHeroSaveStore();
        var saver = new HeroSaver(store, h.Progress);
        int calls = 0;
        saver.Capture += save => calls++;
        saver.Capture += h.Session.Capture;
        h.Give(h.Avatar(0));
        h.WearOk(901);
        Assert.True(saver.Flush(0));
        Assert.Equal(1, calls);
        Assert.Contains("avatar 0 901\n", store.Text);
        Assert.False(saver.Flush(1));
        Assert.Equal(2, calls);                                                    // 内容没变也照样先抓一遍再比较
    }
}
