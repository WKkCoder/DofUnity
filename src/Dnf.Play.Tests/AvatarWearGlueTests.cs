using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using static Dnf.Render.Tests.WardrobeFixture;

namespace Dnf.Play.Tests;

/// <summary>
/// 装扮"穿脱 → 外观"的接线(r14-avatar):Dnf.Game 的穿脱规则(<see cref="AvatarActions"/>)与 Dnf.Render 的衣柜(<see cref="AvatarWardrobe"/>)互不引用,
/// 由宿主层把两边接起来:canShow 钩子 = 用 <see cref="ItemInfo.Path"/> 读出 <see cref="AvatarItem"/> 再问衣柜;穿上或脱下成功之后同步衣柜。
/// 这里用合成素材把这套接法完整跑一遍,钉住几个约定:ItemInfo.Path 就是 AvatarItem.Read 要的 PVF 路径;钩子在规则之后才问;
/// 穿不出来时背包、装扮栏、衣柜都不动;换穿时旧件回原来的格子、外观换成新件;脱下之后画面回到基础外观。
/// </summary>
public class AvatarWearGlueTests
{
    private static ItemInfo Info(int code, Equ equ, int part)
    {
        var item = new ItemInfo(code, equ.FullPath, ItemKind.Equipment) { Part = part, MinimumLevel = 1 };
        item.UsableJobs.Add("[swordman]");
        return item;
    }

    private static bool InAny(IEnumerable<Box> boxes, (int X, int Y) p) => boxes.Any(b => b.Contains(p.X, p.Y));

    /// <summary>宿主层要写的那几行:穿 = 规则(带"衣柜穿得出来吗"的钩子)通过后把同一件读成 AvatarItem 交给衣柜;脱 = 先脱栏、再脱衣柜。</summary>
    private sealed class Host : IDisposable
    {
        public WardrobeRig Rig { get; } = new();
        public Inventory Bag { get; } = new();
        public AvatarSlots Slots { get; } = new();

        public void Add(params ItemInfo[] items)
        {
            foreach (ItemInfo item in items)
            {
                Assert.Equal(1, Bag.Add(item));
            }
        }

        public int[] BagCodes() => Bag.Tab(InventoryTab.Equipment).Select(s => s.Info.Code).ToArray();

        private AvatarItem Read(ItemInfo info) => AvatarItem.Read(Rig.Assets, Rig.Class, info.Path);

        public EquipResult Wear(int bagIndex)
        {
            ItemInfo item = Bag.Tab(InventoryTab.Equipment)[bagIndex].Info;
            EquipResult result = AvatarActions.Wear(Bag, Slots, bagIndex, EquipRules.SwordmanJob, 99, info => Rig.Wardrobe.CanShow(Read(info)));
            if (result == EquipResult.Ok)
            {
                Assert.True(Rig.Wardrobe.Wear(Read(item)));
            }
            return result;
        }

        public bool TakeOff(int part)
        {
            if (!AvatarActions.TakeOff(Bag, Slots, part))
            {
                return false;
            }
            Assert.True(Rig.Wardrobe.TakeOff(part));
            return true;
        }

        public void Dispose() => Rig.Dispose();
    }

    [Fact]
    public void WearingAHatChangesOnlyTheHatRegionAndTakingItOffBringsTheBaseLookBack()
    {
        using var host = new Host();
        ItemInfo hat = Info(900, Hat, 0);
        host.Add(hat);
        RgbaCanvas baseLook = host.Rig.Shot(host.Rig.Base);

        Assert.Equal(EquipResult.Ok, host.Wear(0));
        Assert.Same(hat, host.Slots.Get(0));
        Assert.Empty(host.BagCodes());
        Assert.Equal(1, host.Rig.Wardrobe.WornCount);
        List<(int X, int Y)> diff = WardrobeRig.Diff(baseLook, host.Rig.Shot());
        Assert.NotEmpty(diff);
        var allowed = new[] { CapDefaultArt.Box, HatArt.Box };
        Assert.All(diff, p => Assert.True(InAny(allowed, p), "意外的变化像素 " + p));

        Assert.True(host.TakeOff(0));
        Assert.Null(host.Slots.Get(0));
        Assert.Equal(new[] { 900 }, host.BagCodes());
        Assert.Equal(0, host.Rig.Wardrobe.WornCount);
        Assert.Same(host.Rig.Base, host.Rig.Wardrobe.Current);
        Assert.Empty(WardrobeRig.Diff(baseLook, host.Rig.Shot()));
    }

    [Fact]
    public void ACoatThatCannotBeDrawnIsRefusedAndNothingMoves()
    {
        using var host = new Host();
        ItemInfo coat = Info(900, CoatNoImage, 3);
        host.Add(coat);
        int version = host.Rig.Wardrobe.Version;

        Assert.Equal(EquipResult.NotShowable, host.Wear(0));
        Assert.Equal(new[] { 900 }, host.BagCodes());
        Assert.Null(host.Slots.Get(3));
        Assert.Equal(0, host.Slots.Version);
        Assert.Equal(0, host.Rig.Wardrobe.WornCount);
        Assert.Equal(version, host.Rig.Wardrobe.Version);
    }

    [Fact]
    public void TheRuleChainStillComesFirstWhenTheHookIsWired()
    {
        using var host = new Host();
        ItemInfo wrongJob = Info(900, CoatOne, 3);
        wrongJob.UsableJobs.Clear();
        wrongJob.UsableJobs.Add("[fighter]");                              // 画面上画得出来,但剑魂穿不了
        ItemInfo tooHigh = Info(901, CoatTwo, 3);
        tooHigh.MinimumLevel = 100;
        host.Add(wrongJob, tooHigh);

        Assert.Equal(EquipResult.WrongJob, host.Wear(0));
        Assert.Equal(EquipResult.LevelTooLow, host.Wear(1));
        Assert.Equal(new[] { 900, 901 }, host.BagCodes());
        Assert.Equal(0, host.Slots.WornCount);
        Assert.Equal(0, host.Rig.Wardrobe.WornCount);
    }

    [Fact]
    public void SwappingACoatPutsTheOldOneBackInTheSameCellAndShowsTheNewOne()
    {
        using var host = new Host();
        ItemInfo one = Info(900, CoatOne, 3);
        ItemInfo two = Info(901, CoatTwo, 3);
        ItemInfo filler = Info(902, Pants, 4);
        host.Add(one, filler, two);

        Assert.Equal(EquipResult.Ok, host.Wear(0));
        Assert.Equal(new[] { 902, 901 }, host.BagCodes());
        Assert.True(WardrobeRig.HasColor(host.Rig.Shot(), CoatOneFrontArt.Color));

        Assert.Equal(EquipResult.Ok, host.Wear(1));                       // 背包里 [902, 901]:第 1 格是 901(CoatTwo)
        Assert.Same(two, host.Slots.Get(3));
        Assert.Equal(new[] { 902, 900 }, host.BagCodes());                // 900 回到了 901 原来的格子
        RgbaCanvas shot = host.Rig.Shot();
        Assert.True(WardrobeRig.HasColor(shot, CoatTwoArt.Color));
        Assert.False(WardrobeRig.HasColor(shot, CoatOneFrontArt.Color));
        Assert.False(WardrobeRig.HasColor(shot, CoatOneBackArt.Color));
        Assert.Equal(1, host.Rig.Wardrobe.WornCount);
    }
}
