using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;
using static Dnf.Play.Tests.ExpTests;
using static Dnf.Play.Tests.HudArtTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 技能快捷槽按技能簿排(M35,证据 E-127):六格快捷栏对应六个键 A S D F G H,槽的等级是学到的等级,空格没有槽;城镇底栏随技能簿重排。
/// 数据是 <see cref="AvatarFixture"/> 的合成角色:初始技能表 [skill] 是 "181 2、5 1、46 3、169 1、174 1"(181 与 174 是被动)。
/// </summary>
public class HeroSkillBookTests
{
    private static (GameData Data, CharacterDef Chr, SkillBook Book) Load(DnfAssets assets)
    {
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        return (data, chr, book);
    }

    private static TownHero NewHero(DnfAssets assets)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), 100, 50, false, TownHero.DungeonRestTag);
    }

    [Fact]
    public void TheGivenSkillsFillTheFirstThreeKeysLikeTheOldInitialTable()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        (GameData data, CharacterDef chr, SkillBook book) = Load(assets);

        Assert.Equal(5, book.LearnedCount);                                  // 两个被动也在技能簿里,只是不上栏
        Assert.Equal(new[] { AvatarFixture.HardAttackSkill, AvatarFixture.UpperSlashSkill, AvatarFixture.BackStepSkill, 0, 0, 0 },
            Enumerable.Range(0, SkillBook.QuickSlots).Select(book.QuickAt).ToArray());

        HeroSkills fromBook = HeroSkills.Create(data, chr, 0, book);
        HeroSkills legacy = HeroSkills.Create(data, chr, 0);
        Assert.Equal(legacy.Slots.Select(s => s.Key).ToArray(), fromBook.Slots.Select(s => s.Key).ToArray());
        Assert.Equal(legacy.Slots.Select(s => s.SkillId).ToArray(), fromBook.Slots.Select(s => s.SkillId).ToArray());
        Assert.Equal(legacy.Slots.Select(s => s.Level).ToArray(), fromBook.Slots.Select(s => s.Level).ToArray());
        Assert.Equal(legacy.Slots.Select(s => s.CooldownMs).ToArray(), fromBook.Slots.Select(s => s.CooldownMs).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, fromBook.Slots.Select(s => s.Position).ToArray());
    }

    [Fact]
    public void SlotsFollowTheQuickPositionsAndEmptyCellsHaveNoSlot()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        (GameData data, CharacterDef chr, SkillBook book) = Load(assets);
        Assert.True(book.AssignQuick(4, AvatarFixture.UpperSlashSkill));     // 上挑挪到第 5 格(键 G),第 2 格腾空
        Assert.True(book.AssignQuick(5, AvatarFixture.BackStepSkill));       // 后跳挪到第 6 格(键 H)

        HeroSkills skills = HeroSkills.Create(data, chr, 0, book);
        Assert.Equal(new[] { SkillKeys.A, SkillKeys.G, SkillKeys.H }, skills.Slots.Select(s => s.Key).ToArray());
        Assert.Equal(new[] { 0, 4, 5 }, skills.Slots.Select(s => s.Position).ToArray());
        Assert.Null(skills.SlotOf(SkillKeys.S));
        Assert.Null(skills.SlotOf(SkillKeys.D));
        Assert.Equal(AvatarFixture.UpperSlashSkill, skills.SlotOf(SkillKeys.G).SkillId);
        Assert.Equal(AvatarFixture.UpperSlashLevel, skills.SlotOf(SkillKeys.G).Level);        // 等级取技能簿里的

        // 底栏的槽位置也按快捷栏位置给,不按在列表里的次序
        List<HudSkillState> states = HudStates.IdleSkillSlots(skills);
        Assert.Equal(new[] { 0, 4, 5 }, states.Select(s => s.Slot).ToArray());
    }

    [Fact]
    public void ALearnedLevelDrivesTheCooldownAndTheKeysFGHCastLikeAnyOther()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        (GameData data, CharacterDef chr, SkillBook book) = Load(assets);
        Assert.True(book.AssignQuick(5, AvatarFixture.UpperSlashSkill));
        HeroSkills skills = HeroSkills.Create(data, chr, 0, book);
        TownHero hero = NewHero(assets);

        HeroSkillSlot upper = skills.SlotOf(SkillKeys.H);
        Assert.Equal(1778, upper.CooldownMs);                                // 3 级:同 HeroSkillsTests 里按等级插值的结果
        Assert.Same(upper, skills.TryCast(SkillKeys.H, hero));
        Assert.Equal((HeroMotion.Skill, AvatarFixture.UpperSlashSkill), (hero.Motion, hero.SkillId));
        Assert.Equal(1778.0, upper.CooldownLeftMs);
        Assert.Null(skills.TryCast(SkillKeys.F | SkillKeys.G, hero));         // 没绑技能的键没有反应
        Assert.Equal(SkillCastResult.NotBound, skills.LastCastResult);
    }

    [Fact]
    public void AnEmptyBarOrNoBookGivesNoSlots()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        (GameData data, CharacterDef chr, SkillBook book) = Load(assets);
        for (int i = 0; i < SkillBook.QuickSlots; i++)
        {
            book.ClearQuick(i);
        }
        Assert.Empty(HeroSkills.Create(data, chr, 0, book).Slots);
        Assert.Empty(HeroSkills.Create(data, chr, 0, null).Slots);
        Assert.Empty(HeroSkills.Create(data, null, 0, book).Slots);
        Assert.Empty(HeroSkills.Create(null, chr, 0, book).Slots);
    }

    [Fact]
    public void ABookWithAnEmptyCatalogCannotPlaceAnythingOnTheBar()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        (GameData data, CharacterDef chr, _) = Load(assets);
        // 另造一本用空目录的簿子:快捷栏放不进任何技能(目录里没有),所以没有槽
        var empty = new SkillBook(SkillCatalog.Empty);
        Assert.False(empty.AssignQuick(0, AvatarFixture.HardAttackSkill));
        Assert.Empty(HeroSkills.Create(data, chr, 0, empty).Slots);
    }

    [Fact]
    public void TheTownBarRearrangesItsSkillSlotsWhenTheBookChanges()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        CharacterDef chr = data.GetCharacter(0);
        HeroProgress progress = Progress(level: 5);
        progress.AttachSkills(SkillCatalog.Load(data, 0), chr.Tags.Lines, true);
        var town = new TownHud(HudArt.For(Hud()), HeroSkills.Create(data, chr, 0), data, chr);
        var list = new List<SceneSprite>();
        int order = 0;

        Assert.True(town.Build(list, ref order, progress));
        Assert.Equal(new[] { 0, 1, 2 }, town.Skills.Slots.Select(s => s.Position).ToArray());
        Assert.Equal(AvatarFixture.UpperSlashLevel, town.Skills.SlotOf(SkillKeys.S).Level);

        // 技能簿里把上挑挪到第 6 格:下一次画就重排(同一本簿子,版本变了)
        Assert.True(progress.Skills.AssignQuick(5, AvatarFixture.UpperSlashSkill));
        list.Clear();
        Assert.True(town.Build(list, ref order, progress));
        Assert.Equal(new[] { 0, 2, 5 }, town.Skills.Slots.Select(s => s.Position).ToArray());
        Assert.Equal(AvatarFixture.UpperSlashSkill, town.Skills.SlotOf(SkillKeys.H).SkillId);

        // 没接技能数据的进度:保持原来的初始技能表,不重排
        HeroProgress bare = Progress(level: 5);
        TownHud legacy = new TownHud(HudArt.For(Hud()), HeroSkills.Create(data, chr, 0), data, chr);
        list.Clear();
        Assert.True(legacy.Build(list, ref order, bare));
        Assert.Equal(new[] { 0, 1, 2 }, legacy.Skills.Slots.Select(s => s.Position).ToArray());
    }
}
