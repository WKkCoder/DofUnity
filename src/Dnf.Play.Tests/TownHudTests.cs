using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using static Dnf.Play.Tests.ExpTests;
using static Dnf.Play.Tests.HudArtTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 城镇里的原版底栏(M30,<see cref="TownHud"/>):和副本里同一条底栏,状态取"站在城镇里的主角"——血蓝满、技能槽亮、没有冷却,
/// 等级、经验条、SP 与物品快捷槽取自等级进度。精灵是合成的纯色块(同 HudArtTests),数值手算。
/// </summary>
public class TownHudTests
{
    private const string SkillIcon = "Character/Test/SkillIcon.img";

    private static ItemInfo Potion(int code, int hp = 100, int mp = 0, int coolMs = 0)
    {
        return new ItemInfo(code, "stackable/test/p" + code + ".stk", ItemKind.Stackable)
        {
            StackableType = StackableTypes.Waste,
            HpRecovery = hp > 0 ? new RecoveryEffect(false, hp, 0) : null,
            MpRecovery = mp > 0 ? new RecoveryEffect(false, mp, 0) : null,
            CoolTimeMs = coolMs,
            StackLimit = 99,
            Price = 10,
            Value = 4,
            Icon = "Item/Test.img",
            IconFrame = 1,
        };
    }

    private static FakeSprites Sprites()
    {
        FakeSprites sprites = Hud();
        sprites.Frames[("sprite/character/test/skillicon.img", 10)] = Coded(28, 28, 50);
        sprites.Frames[("sprite/character/test/skillicon.img", 11)] = Coded(28, 28, 51);
        sprites.Frames[("sprite/item/test.img", 1)] = Coded(28, 28, 70);
        return sprites;
    }

    private static HeroSkills TwoSkills(int levelA = 3, int levelS = 5)
    {
        SkillDef def = SkillWithIcon(5, SkillIcon, 10, 11);
        return new HeroSkills(new[]
        {
            new HeroSkillSlot(SkillKeys.A, 5, levelA, def),
            new HeroSkillSlot(SkillKeys.S, 6, levelS, def),
        });
    }

    private static TownHud Town(FakeSprites sprites = null, HeroSkills skills = null)
    {
        return new TownHud(HudArt.For(sprites ?? Sprites()), skills ?? TwoSkills());
    }

    private static List<SceneSprite> Build(TownHud hud, HeroProgress progress)
    {
        var list = new List<SceneSprite>();
        int order = 0;
        Assert.True(hud.Build(list, ref order, progress));
        return list;
    }

    private static SceneSprite Only(List<SceneSprite> sprites, string tag, int nth = 0)
    {
        return sprites.Where(s => s.AnimationPath == tag).ElementAt(nth);
    }

    // ------------------------------------------------------------------ 各槽的显示状态

    [Fact]
    public void TheItemSlotsFollowTheQuickBarAndAreEmptyWithoutAProgress()
    {
        Assert.Empty(HudStates.ItemSlots(null));

        HeroProgress progress = Progress();
        Assert.Empty(HudStates.ItemSlots(progress));                           // 快捷栏是空的

        progress.AddItem(Potion(10, hp: 100, coolMs: 2000), 3);
        progress.AddItem(Potion(11, hp: 0, mp: 100), 5);
        List<HudItemState> states = HudStates.ItemSlots(progress);
        Assert.Equal(2, states.Count);
        Assert.Equal((0, "sprite/item/test.img", 1, 3, 0.0), (states[0].Slot, states[0].IconKey, states[0].IconFrame, states[0].Count, states[0].CooldownFraction));
        Assert.Equal((1, 5), (states[1].Slot, states[1].Count));
    }

    [Fact]
    public void TheIdleSkillSlotsAreLitWithTheirLevelsAndHaveNoCooldown()
    {
        Assert.Empty(HudStates.IdleSkillSlots(null));
        Assert.Empty(HudStates.IdleSkillSlots(new HeroSkills(new HeroSkillSlot[0])));

        HeroSkills skills = TwoSkills(levelA: 3, levelS: 5);
        List<HudSkillState> states = HudStates.IdleSkillSlots(skills);
        Assert.Equal(2, states.Count);
        Assert.Equal((0, true, 0.0, 3, false), (states[0].Slot, states[0].Usable, states[0].CooldownFraction, states[0].Level, states[0].LabelGray));
        Assert.Equal((1, true, 0.0, 5, false), (states[1].Slot, states[1].Usable, states[1].CooldownFraction, states[1].Level, states[1].LabelGray));
        Assert.Same(skills.Slots[0].Def, states[0].Def);
    }

    // ------------------------------------------------------------------ 城镇底栏

    [Fact]
    public void NothingIsDrawnWithoutAProgressOrAList()
    {
        TownHud hud = Town();
        var list = new List<SceneSprite>();
        int order = 0;
        Assert.False(hud.Build(list, ref order, null));
        Assert.Empty(list);
        Assert.Equal(0, order);
        Assert.False(hud.Build(null, ref order, Progress()));
        Assert.Null(TownHud.TryCreate(null));
    }

    [Fact]
    public void TheTownBarHasBothFullOrbsAndTheSameFixedPartsAsTheDungeonBar()
    {
        List<SceneSprite> list = Build(Town(), Progress());

        Assert.Equal(1, list.Count(s => s.AnimationPath == "hud-bar"));
        Assert.Equal((0, 509), (Only(list, "hud-bar").ScreenX, Only(list, "hud-bar").ScreenY));
        SceneSprite hp = Only(list, "hud-hp");
        SceneSprite mp = Only(list, "hud-mp");
        Assert.Equal(56, hp.Frame.Height);                                     // 满的:整个球的液体都露出来
        Assert.Equal(56, mp.Frame.Height);
        Assert.Equal(HudArt.SlotCount, list.Count(s => s.AnimationPath == "hud-key"));
        Assert.Equal(HudArt.ItemSlotCount, list.Count(s => s.AnimationPath == "hud-item-key"));
        Assert.Equal(5, list.Count(s => s.AnimationPath == "hud-menu-icon"));
        Assert.Contains(list, s => s.AnimationPath == "hud-menu");
        Assert.Contains(list, s => s.AnimationPath == "hud-shop");
        Assert.All(list, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        for (int i = 1; i < list.Count; i++)
        {
            Assert.True(list[i].SortKey > list[i - 1].SortKey);
        }
    }

    [Fact]
    public void TheTownBarDrawsExactlyWhatTheDungeonBarDrawsForAFullHeroAtTheSameProgress()
    {
        FakeSprites sprites = Sprites();
        HeroProgress progress = Progress(level: 3, exp: 450);
        progress.AddItem(Potion(10, hp: 100), 4);
        HeroVitals vitals = HeroVitals.FromStats(progress.Level, progress.Stats);
        HeroSkills skills = TwoSkills();

        List<SceneSprite> town = Build(Town(sprites, skills), progress);

        var dungeon = new List<SceneSprite>();
        int order = 0;
        HudArt.For(sprites).Build(dungeon, ref order, vitals.Level, vitals.HpMax, vitals.HpMax, vitals.MpMax, vitals.MpMax,
            new[] { new HudSkillState(0, skills.Slots[0].Def, true, 0.0, 3, false), new HudSkillState(1, skills.Slots[1].Def, true, 0.0, 5, false) },
            new HudExp(progress.ExpInLevel, progress.LevelSpan, false), progress.SkillPoints, HudStates.ItemSlots(progress));

        Assert.Equal(dungeon.Count, town.Count);
        for (int i = 0; i < town.Count; i++)
        {
            Assert.Equal((dungeon[i].AnimationPath, dungeon[i].ScreenX, dungeon[i].ScreenY, dungeon[i].SortKey),
                (town[i].AnimationPath, town[i].ScreenX, town[i].ScreenY, town[i].SortKey));
            Assert.Same(dungeon[i].Frame, town[i].Frame);                      // 同一个源共用同一份派生帧
        }
    }

    [Fact]
    public void TheLevelPlateTheExpBarAndTheSpDigitsComeFromTheProgress()
    {
        // 经验表是 100 × L 的等差区间:2 级门槛 100、3 级 300;2 级里已经有 50 点 → 50 / 200 = 0.25,625 × 0.25 = 156 列
        HeroProgress progress = Progress(level: 2, exp: 150);
        List<SceneSprite> list = Build(Town(), progress);
        Assert.Equal(2, list.Count(s => s.AnimationPath == "hud-level"));      // "Lv" 牌 + 一位数字
        Assert.Equal(42, Only(list, "hud-level", 1).Frame.Rgba[2]);             // 标记色 40 + 数字 2
        SceneSprite exp = Only(list, "hud-exp");
        Assert.Equal(156, exp.Frame.Width);
        Assert.Equal(progress.SkillPoints.ToString().Length, list.Count(s => s.AnimationPath == "hud-sp"));
    }

    [Fact]
    public void TheMaxLevelBarGetsTheCapOverlay()
    {
        HeroProgress progress = Progress(level: Leveling.MaxLevel);
        List<SceneSprite> list = Build(Town(), progress);
        Assert.Contains(list, s => s.AnimationPath == "hud-exp-cap");
    }

    [Fact]
    public void ALevelUpBetweenTwoBuildsChangesTheLevelPlateAndTheOrbsFollowTheNewMaximum()
    {
        TownHud hud = Town();
        HeroProgress low = Progress(level: 1);
        HeroProgress high = Progress(level: 5);
        Assert.Equal(41, Only(Build(hud, low), "hud-level", 1).Frame.Rgba[2]);   // 数字 1
        Assert.Equal(45, Only(Build(hud, high), "hud-level", 1).Frame.Rgba[2]);  // 数字 5:按新的等级重算(缓存按等级换)
        Assert.Equal(41, Only(Build(hud, low), "hud-level", 1).Frame.Rgba[2]);   // 换回来也对
    }

    [Fact]
    public void TheSkillSlotsShowTheirLitIconsAtTheTwoFirstSlotsAndTheThirdIsLeftEmpty()
    {
        List<SceneSprite> list = Build(Town(), Progress());
        Assert.Equal(2, list.Count(s => s.AnimationPath == "hud-skill"));
        Assert.Equal((538, 558), (Only(list, "hud-skill", 0).ScreenX, Only(list, "hud-skill", 0).ScreenY));
        Assert.Equal((538 + 30, 558), (Only(list, "hud-skill", 1).ScreenX, Only(list, "hud-skill", 1).ScreenY));
        Assert.Equal(50, Only(list, "hud-skill", 0).Frame.Rgba[2]);              // 亮的那张,不是灰的
        Assert.Equal(50, Only(list, "hud-skill", 1).Frame.Rgba[2]);
        Assert.DoesNotContain(list, s => s.AnimationPath == "hud-skill-cooldown");
        Assert.Equal(4, list.Count(s => s.AnimationPath == "hud-skill-level"));  // 两个槽各一个前缀与一个数字
    }

    [Fact]
    public void TheItemSlotsShowTheQuickBarPotionsWithTheirBagCounts()
    {
        HeroProgress progress = Progress();
        progress.AddItem(Potion(10, hp: 100), 12);
        progress.AddItem(Potion(11, hp: 0, mp: 100), 3);
        List<SceneSprite> list = Build(Town(), progress);

        Assert.Equal(2, list.Count(s => s.AnimationPath == "hud-item"));
        Assert.Equal((85, 558), (Only(list, "hud-item", 0).ScreenX, Only(list, "hud-item", 0).ScreenY));
        Assert.Equal((85 + 30, 558), (Only(list, "hud-item", 1).ScreenX, Only(list, "hud-item", 1).ScreenY));
        Assert.Equal(70, Only(list, "hud-item", 0).Frame.Rgba[2]);
        Assert.Equal(2, list.Count(s => s.AnimationPath == "hud-item-count"));
        Assert.DoesNotContain(list, s => s.AnimationPath == "hud-item-cooldown");

        // 药水用掉一次:冷却遮罩出现在城镇底栏里(冷却照墙钟走,城镇里也在转)
        HeroProgress cooling = Progress();
        cooling.AddItem(Potion(10, hp: 100, coolMs: 2000), 3);
        Assert.Equal(UseResult.Ok, cooling.UseQuickSlot(0, HeroVitals.FromStats(cooling.Level, cooling.Stats)));
        List<SceneSprite> after = Build(Town(), cooling);
        Assert.Contains(after, s => s.AnimationPath == "hud-item-cooldown");
        cooling.Cooldowns.Advance(2000);                                       // 冷却走完:遮罩消失
        Assert.DoesNotContain(Build(Town(), cooling), s => s.AnimationPath == "hud-item-cooldown");
    }

    [Fact]
    public void ABarWithoutSkillsStillDrawsTheRestAndTheCoreFramesAreRequiredToCreateOne()
    {
        TownHud bare = new TownHud(HudArt.For(Sprites()), null);
        Assert.Empty(bare.Skills.Slots);
        List<SceneSprite> list = Build(bare, Progress());
        Assert.DoesNotContain(list, s => s.AnimationPath == "hud-skill");
        Assert.Contains(list, s => s.AnimationPath == "hud-bar");

        Assert.Throws<ArgumentNullException>(() => new TownHud(null, TwoSkills()));
        Assert.Null(HudArt.For(Hud(bar: false)));                              // 没有底栏图:造不出 HudArt,TryCreate 也就返回 null
    }
}
