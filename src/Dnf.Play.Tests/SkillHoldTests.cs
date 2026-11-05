using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// "整段施法时间做蓄力"的发射类技能(<see cref="SkillStageSpec.CastHoldWholeTime"/>):蓄力不再减动作的前几帧,第 0 帧就停整段 [casting time]。
/// 数据是合成的(<see cref="AvatarFixture.WaveSkill"/>:施法动作六帧,第 0 帧蓄力占位,第 1 到 5 帧各 50 毫秒;[casting time] 300 毫秒;
/// 放一个对象,四帧各 50 毫秒,第 0、1 帧有攻击框)。整段蓄力 300 毫秒,之后五帧 250 毫秒,共 550 毫秒;第 4 帧从 300 + 150 = 450 毫秒起。
/// </summary>
public sealed class SkillHoldTests
{
    private static SkillRecipes Recipes(bool wholeTime = true, int castHoldFrames = 0, int objectFrame = 4, int forwardX = 0)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.WaveSkill,
                new[]
                {
                    new SkillStageSpec(motionIndex: 4, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0,
                        castHoldFrames: castHoldFrames, castHoldWholeTime: wholeTime),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1, slideFollowsHeldDirection: false,
                objects: new[] { new SkillObjectSpec(AvatarFixture.WaveObjectId, 0, objectFrame, damageRateColumn: 1, damageFixedColumn: 0, forwardX: forwardX) }),
        });
    }

    private static HeroSkills Load(GameData data, int skillId, SkillRecipes recipes, int level = 2)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(skillId, level));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, skillId) }, 0, 0);
        return HeroSkills.Create(data, chr, 0, book, recipes);
    }

    private static TownHero NewHero(DnfAssets assets)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), 100, 50, false, TownHero.DungeonRestTag);
    }

    private static void Run(RoomCombatSession s, int ms, SkillKeys firstStepKeys = SkillKeys.None)
    {
        for (int t = 0; t < ms; t += 10)
        {
            s.Update(10, 0, 0, false, false, t == 0 ? firstStepKeys : SkillKeys.None);
        }
    }

    [Fact]
    public void AWholeTimeHoldWaitsTheFullCastingTimeAndNothingIsTakenOffForTheFirstFrames()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        HeroSkills skills = Load(data, AvatarFixture.WaveSkill, Recipes());
        TownHero hero = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero));
        Assert.Equal((HeroMotion.Skill, 0), (hero.Motion, hero.CurrentFrame));
        hero.Update(299, 0, 0);
        Assert.Equal(0, hero.CurrentFrame);                          // 蓄力 = [casting time] 300 毫秒,不减任何帧
        hero.Update(1, 0, 0);
        Assert.Equal(1, hero.CurrentFrame);
        hero.Update(249, 0, 0);
        Assert.Equal(HeroMotion.Skill, hero.Motion);                 // 第 1 到 5 帧共 250 毫秒:整段 550 毫秒
        hero.Update(1, 0, 0);
        Assert.Equal(HeroMotion.Rest, hero.Motion);

        // 对照:同一技能按"减前三帧"的蓄力只有 300 − 150 = 150 毫秒,第 1 帧 150 毫秒就到了
        HeroSkills subtract = Load(data, AvatarFixture.WaveSkill, Recipes(wholeTime: false, castHoldFrames: 3));
        TownHero other = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, subtract.Cast(subtract.SlotOf(SkillKeys.A), other));
        other.Update(150, 0, 0);
        Assert.Equal(1, other.CurrentFrame);
    }

    [Fact]
    public void ASkillWithoutACastingTimeHasAZeroLengthWholeTimeHoldNotNoHold()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        // 多段技能的 .skl 没有 [casting time]:整段蓄力 = 0 毫秒,第 0 帧不占时间,一放出来就在第一个有时长的帧上
        var recipes = new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill,
                new[] { new SkillStageSpec(motionIndex: 4, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0, castHoldWholeTime: true) },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1),
        });
        HeroSkills skills = Load(data, AvatarFixture.ChainSkill, recipes);
        TownHero hero = NewHero(assets);

        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero));
        Assert.Equal(1, hero.CurrentFrame);
    }

    [Fact]
    public void AStageHoldsEitherTheWholeTimeOrAFrameCountNeverBothAndDefaultsToNeither()
    {
        // 又要整段、又要减帧:状态里只有一种算法,拒绝
        Assert.Throws<ArgumentException>(() => new SkillStageSpec(0, -1, -1, -1, effectIndex: -1, effectFrame: 0, castHoldFrames: 1, castHoldWholeTime: true));
        Assert.Throws<ArgumentException>(() => new SkillStageSpec(0, -1, -1, -1, effectIndex: -1, effectFrame: 0, castHoldFrames: 7, castHoldWholeTime: true));

        // 边界:整段与 0 帧一起是合法的;减帧不带整段、什么都不带也合法
        var whole = new SkillStageSpec(0, -1, -1, -1, effectIndex: -1, effectFrame: 0, castHoldFrames: 0, castHoldWholeTime: true);
        Assert.Equal((true, 0), (whole.CastHoldWholeTime, whole.CastHoldFrames));
        var frames = new SkillStageSpec(0, -1, -1, -1, effectIndex: -1, effectFrame: 0, castHoldFrames: 3);
        Assert.Equal((false, 3), (frames.CastHoldWholeTime, frames.CastHoldFrames));
        var neither = new SkillStageSpec(0, -1, -1, -1, effectIndex: -1, effectFrame: 0);
        Assert.Equal((false, 0), (neither.CastHoldWholeTime, neither.CastHoldFrames));
    }

    [Fact]
    public void TheObjectOfAWholeTimeHoldSkillAppearsWhenTheAnimationReachesItsFrameAfterTheFullHold()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, AvatarFixture.WaveSkill, Recipes(objectFrame: 4)));
        arena.Session.AttachActs(new ActLibrary(arena.Assets, new GameData(new PvfScriptSource(arena.Assets.Pvf))));
        RoomCombatSession s = arena.Session;

        Run(s, 440, SkillKeys.A);                                    // 主角自己的时间 440 毫秒:第 4 帧(450 毫秒起)还没到
        Assert.Equal((1, 0), (s.SkillCasts, s.HeroObjectsSpawned));
        Run(s, 20);                                                  // 460 毫秒:到了
        Assert.Equal(1, s.HeroObjectsSpawned);
        Run(s, 200);
        Assert.Equal(1, s.HeroObjectsSpawned);                       // 一次施放只放一次

        // 对象放在第 5 帧(500 毫秒起):450 毫秒时还没有,510 毫秒有了
        using var later = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, AvatarFixture.WaveSkill, Recipes(objectFrame: 5)));
        later.Session.AttachActs(new ActLibrary(later.Assets, new GameData(new PvfScriptSource(later.Assets.Pvf))));
        Run(later.Session, 490, SkillKeys.A);
        Assert.Equal(0, later.Session.HeroObjectsSpawned);
        Run(later.Session, 30);
        Assert.Equal(1, later.Session.HeroObjectsSpawned);
    }

    [Fact]
    public void TheDefaultTableHasOneWholeTimeHoldRecipeWithOneObjectAndNoBodyStrike()
    {
        // 状态 0x1b 的一个技能(静态读码):整段施法时间蓄力、不减帧;动作取 [etc motion] 第 1 项,结束帧 17;主角身体不命中;
        // 动画到第 4 帧放一个对象,伤害百分比取 [level info] 第 1 列、固定值取第 0 列,向前偏 80;技能号与对象编号不在这里断言
        SkillRecipe hold = Assert.Single(SkillRecipes.Default.All, r => r.Stages.Any(st => st.CastHoldWholeTime));
        Assert.True(HeroSkills.IsImplemented(hold.SkillId));
        Assert.Single(hold.Stages);

        SkillStageSpec st = hold.Stages[0];
        Assert.Equal((1, 17, true, 0, false), (st.MotionIndex, st.EndFrame, st.CastHoldWholeTime, st.CastHoldFrames, st.HasOwnStrike));
        Assert.False(hold.SlideFollowsHeldDirection);

        SkillObjectSpec o = Assert.Single(hold.Objects);
        Assert.Equal((0, 4, 1, 0, 80, 0, 0), (o.Stage, o.Frame, o.DamageRateColumn, o.DamageFixedColumn, o.ForwardX, o.OffsetY, o.OffsetZ));
    }
}
