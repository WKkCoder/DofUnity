using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 施法时间怎么落到一段动画的第 0 帧上、以及每段是否起滑(M105,R7-he-01、R7-he-03、R7-he-09、R7-he-10,客户端状态 0x1f、0x26、0x17、0x2c 的静态读码)。
/// 数据全是合成的(<see cref="AvatarFixture"/>);期望值都按夹具的帧时长手算:
/// 动作表(<c>action.chr</c> 的 [etc motion])第 1 项 5 帧 (100,100,100,100,200)、第 2 项 4 帧各 100、第 3 项 3 帧各 100、第 4 项 6 帧 (10000,50,50,50,50,50)、第 5 项 1 帧 500;
/// 波系技能 <see cref="AvatarFixture.WaveSkill"/> 的 [casting time] 是 300、[static data] 是 (250, 160, −320);
/// 多段技能 <see cref="AvatarFixture.ChainSkill"/> 没有 [casting time]、[static data] 是 (200, −400, 100, −100, 2)。
/// </summary>
public class SkillCastModeTests
{
    private static SkillRecipes OneStage(int skillId, int motion, SkillCastMode mode, int holdStaticIndex = -1, int castHoldFrames = 0)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(skillId,
                new[]
                {
                    new SkillStageSpec(motionIndex: motion, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0,
                        castHoldFrames: castHoldFrames, castMode: mode, holdStaticIndex: holdStaticIndex),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false),
        });
    }

    private static HeroSkills Load(GameData data, int skillId, SkillRecipes recipes, int level = 2)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(skillId, level));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, skillId) }, 0, 0);   // 技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, recipes);
    }

    private static TownHero NewHero(DnfAssets assets)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), 100, 50, false, TownHero.DungeonRestTag);
    }

    private static int HoldOf(GameData data, DnfAssets assets, int skillId, int motion, SkillCastMode mode, int holdStaticIndex = -1)
    {
        SkillRecipes recipes = OneStage(skillId, motion, mode, holdStaticIndex);
        HeroSkillSlot slot = Load(data, skillId, recipes).SlotOf(SkillKeys.A);
        return HeroSkills.CastHoldMs(slot, slot.Recipe.Stages[0], NewHero(assets));
    }

    /// <summary>放一次技能,第 0 帧一直停到 <paramref name="holdMs"/> 毫秒:前一毫秒还在第 0 帧、到点进第 1 帧。</summary>
    private static void AssertFirstFrameLasts(GameData data, DnfAssets assets, int skillId, int motion, SkillCastMode mode, int holdStaticIndex, int holdMs)
    {
        HeroSkills skills = Load(data, skillId, OneStage(skillId, motion, mode, holdStaticIndex));
        TownHero hero = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero));
        Assert.Equal(HeroMotion.Skill, hero.Motion);
        hero.Update(holdMs - 1, 0, 0);
        Assert.Equal(0, hero.CurrentFrame);
        hero.Update(1, 0, 0);
        Assert.Equal(1, hero.CurrentFrame);
    }

    // ------------------------------------------------------------------ 伸缩(状态 0x1f 的姿势段)

    [Fact]
    public void StretchHoldsTheFirstFrameForTheCastingTimeMinusTheOtherFramesNeverBelowZero()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 施法时间 300:减去第 1 帧起各帧时长之和(动作 4:5×50 = 250;动作 3:2×100 = 200;动作 5 只有一帧:0)
        Assert.Equal(50, HoldOf(data, assets, AvatarFixture.WaveSkill, 4, SkillCastMode.Stretch));
        Assert.Equal(100, HoldOf(data, assets, AvatarFixture.WaveSkill, 3, SkillCastMode.Stretch));
        Assert.Equal(300, HoldOf(data, assets, AvatarFixture.WaveSkill, 5, SkillCastMode.Stretch));
        // 边界:动作 2 的后三帧正好 300 → 0;动作 1 的后四帧 500 超过施法时间 → 压到 0,不出负数
        Assert.Equal(0, HoldOf(data, assets, AvatarFixture.WaveSkill, 2, SkillCastMode.Stretch));
        Assert.Equal(0, HoldOf(data, assets, AvatarFixture.WaveSkill, 1, SkillCastMode.Stretch));

        // 真放一次:动作 4 的第 0 帧停 50 毫秒
        AssertFirstFrameLasts(data, assets, AvatarFixture.WaveSkill, 4, SkillCastMode.Stretch, -1, 50);

        // 停 0 毫秒时第 0 帧不占时间:放出来就在第 1 帧
        HeroSkills zero = Load(data, AvatarFixture.WaveSkill, OneStage(AvatarFixture.WaveSkill, 2, SkillCastMode.Stretch));
        TownHero hero = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, zero.Cast(zero.SlotOf(SkillKeys.A), hero));
        Assert.Equal(1, hero.CurrentFrame);
    }

    // ------------------------------------------------------------------ 暂停(状态 0x26)

    [Fact]
    public void PauseHoldsTheFirstFrameForTheCastingTimePlusItsOwnLength()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 施法时间 300 加第 0 帧自己的时长:动作 4 是 10000、动作 3 与 2 是 100、动作 5(一帧)是 500
        Assert.Equal(10300, HoldOf(data, assets, AvatarFixture.WaveSkill, 4, SkillCastMode.Pause));
        Assert.Equal(400, HoldOf(data, assets, AvatarFixture.WaveSkill, 3, SkillCastMode.Pause));
        Assert.Equal(400, HoldOf(data, assets, AvatarFixture.WaveSkill, 2, SkillCastMode.Pause));
        Assert.Equal(800, HoldOf(data, assets, AvatarFixture.WaveSkill, 5, SkillCastMode.Pause));

        // 与伸缩不同:同一个动作 3,伸缩是 100、暂停是 400
        Assert.Equal(100, HoldOf(data, assets, AvatarFixture.WaveSkill, 3, SkillCastMode.Stretch));

        AssertFirstFrameLasts(data, assets, AvatarFixture.WaveSkill, 3, SkillCastMode.Pause, -1, 400);
    }

    [Fact]
    public void StretchAndPauseLeaveTheAnimationAloneWhenTheSkillHasNoCastingTime()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 多段技能没有 [casting time]:两种写法都不改动画(-1),动作 2 的第 0 帧照自己的 100 毫秒走
        Assert.Equal(-1, HoldOf(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.Stretch));
        Assert.Equal(-1, HoldOf(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.Pause));
        AssertFirstFrameLasts(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.Stretch, -1, 100);
        AssertFirstFrameLasts(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.Pause, -1, 100);
    }

    [Fact]
    public void ThePlaceholderModeIsUnchangedAndIgnoresTheStaticIndex()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 老写法:施法时间 300 − 第 1 到 3 帧(3×50)= 150;不是"静态停留"的段,写了下标也不看
        HeroSkillSlot slot = Load(data, AvatarFixture.WaveSkill, OneStage(AvatarFixture.WaveSkill, 4, SkillCastMode.Placeholder, holdStaticIndex: 0, castHoldFrames: 3)).SlotOf(SkillKeys.A);
        Assert.Equal(150, HeroSkills.CastHoldMs(slot, slot.Recipe.Stages[0], NewHero(assets)));
        // 没写蓄力帧数也没写模式:不改动画
        HeroSkillSlot none = Load(data, AvatarFixture.WaveSkill, OneStage(AvatarFixture.WaveSkill, 4, SkillCastMode.Placeholder, holdStaticIndex: 0)).SlotOf(SkillKeys.A);
        Assert.Equal(-1, HeroSkills.CastHoldMs(none, none.Recipe.Stages[0], NewHero(assets)));
    }

    // ------------------------------------------------------------------ 静态停留(状态 0x17)

    [Fact]
    public void StaticHoldTakesTheFirstFrameStayFromTheStaticDataEntryAndClampsNegativesToZero()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 多段技能 [static data] = (200, −400, 100, −100, 2),动作 2:不看施法时间,停留 = 那一项
        Assert.Equal(200, HoldOf(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.StaticHold, 0));
        Assert.Equal(100, HoldOf(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.StaticHold, 2));
        Assert.Equal(2, HoldOf(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.StaticHold, 4));
        // 负数压到 0
        Assert.Equal(0, HoldOf(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.StaticHold, 1));
        Assert.Equal(0, HoldOf(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.StaticHold, 3));
        // 边界:下标越过数据末尾(只有 5 个数)取不到,按 0
        Assert.Equal(0, HoldOf(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.StaticHold, 5));
        // 另一个技能:波系技能 [static data] = (250, 160, −320)
        Assert.Equal(250, HoldOf(data, assets, AvatarFixture.WaveSkill, 2, SkillCastMode.StaticHold, 0));
        Assert.Equal(0, HoldOf(data, assets, AvatarFixture.WaveSkill, 2, SkillCastMode.StaticHold, 2));

        // 真放一次:第 0 帧停 200 毫秒(不是动作自己的 100)
        AssertFirstFrameLasts(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.StaticHold, 0, 200);
        // 停 2 毫秒:第一毫秒还在第 0 帧、第二毫秒进第 1 帧
        AssertFirstFrameLasts(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.StaticHold, 4, 2);
    }

    [Fact]
    public void StaticHoldWithoutAnIndexOrWithAnUnknownSkillDefinitionLeavesTheAnimationAlone()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 没写下标(默认 -1):不改动画,第 0 帧照自己的 100 毫秒走
        Assert.Equal(-1, HoldOf(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.StaticHold));
        AssertFirstFrameLasts(data, assets, AvatarFixture.ChainSkill, 2, SkillCastMode.StaticHold, -1, 100);
    }

    // ------------------------------------------------------------------ 每段是否起滑(状态 0x2c)

    private static SkillRecipes TwoStagesSliding(bool firstSlides, bool secondSlides)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill,
                new[]
                {
                    new SkillStageSpec(motionIndex: 2, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0, slides: firstSlides),
                    new SkillStageSpec(motionIndex: 3, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0, slides: secondSlides),
                },
                slideSpeedIndex: 0, slideAccelIndex: 2, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                autoChain: true),
        });
    }

    [Fact]
    public void AStageThatDoesNotSlideStartsAtZeroSpeedAndStaysThereWhileTheNextStageSlides()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkills skills = Load(data, AvatarFixture.ChainSkill, TwoStagesSliding(firstSlides: false, secondSlides: true));
        TownHero hero = NewHero(assets);
        hero.SkillFollowUp = h => skills.FollowUp(h);

        // 起滑速度取 [static data] 第 0 个数 200、加速度取第 2 个数 100;第一段不滑:速度与加速度都是 0,速度一直是 0
        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero));
        Assert.Equal((0, 0.0), (hero.SkillStage, hero.SkillSlideSpeed));
        hero.Update(100, 0, 0);
        Assert.Equal(0.0, hero.SkillSlideSpeed);
        hero.Update(250, 0, 0);
        Assert.Equal((0, 0.0), (hero.SkillStage, hero.SkillSlideSpeed));

        // 第一段 400 毫秒播完接第二段:这一段滑,速度从 200 起(不为 0、不超过 200)
        hero.Update(60, 0, 0);
        Assert.Equal((1, HeroMotion.Skill), (hero.SkillStage, hero.Motion));
        Assert.InRange(hero.SkillSlideSpeed, 1.0, 200.0);
    }

    [Fact]
    public void WhenTheFirstStageSlidesItStartsAtTheStaticSpeedAndASecondStageThatDoesNotStopsAtZero()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkills skills = Load(data, AvatarFixture.ChainSkill, TwoStagesSliding(firstSlides: true, secondSlides: false));
        TownHero hero = NewHero(assets);
        hero.SkillFollowUp = h => skills.FollowUp(h);

        // 默认(每段都滑)的第一段:放出来速度就是 [static data] 第 0 个数 200
        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero));
        Assert.Equal(200.0, hero.SkillSlideSpeed);

        // 第二段标了不滑:接上之后速度是 0
        hero.Update(410, 0, 0);
        Assert.Equal(1, hero.SkillStage);
        Assert.Equal(0.0, hero.SkillSlideSpeed);
    }

    [Theory]
    [InlineData(SkillCastMode.Stretch)]
    [InlineData(SkillCastMode.Pause)]
    [InlineData(SkillCastMode.StaticHold)]
    public void TheWholeTimeHoldBelongsToThePlaceholderModeOnlyAndRefusesTheOtherCastModes(SkillCastMode mode)
    {
        // 合并两条线时补的守卫:"整段施法时间做蓄力"(状态 0x1b 一族)与另三种施法方式互斥,同给时构造抛异常,免得其中一个被静默忽略
        Assert.Throws<ArgumentException>(() => new SkillStageSpec(0, -1, -1, -1, effectIndex: -1, effectFrame: 0, castHoldWholeTime: true, castMode: mode));
        // 反例:占位模式配整段施法时间照常构造,两个字段原样保留
        var ok = new SkillStageSpec(0, -1, -1, -1, effectIndex: -1, effectFrame: 0, castHoldWholeTime: true);
        Assert.Equal((true, SkillCastMode.Placeholder), (ok.CastHoldWholeTime, ok.CastMode));
    }

    [Fact]
    public void ThePerStageSlideFlagFromTheConstructorAndTheCopyMethodAreTheSameSwitchAndCopiesKeepTheCastMode()
    {
        // 起滑开关只有一个(状态 0x25/0x27 那条线的 WithoutSlide() 与本线构造参数 slides 同指一个属性):两种写法等价,复制不丢施法方式与静态停留下标
        var viaCtor = new SkillStageSpec(0, -1, -1, -1, effectIndex: -1, effectFrame: 0, castMode: SkillCastMode.StaticHold, slides: false, holdStaticIndex: 3);
        var viaCopy = new SkillStageSpec(0, -1, -1, -1, effectIndex: -1, effectFrame: 0, castMode: SkillCastMode.StaticHold, holdStaticIndex: 3).WithoutSlide();
        Assert.Equal((false, SkillCastMode.StaticHold, 3), (viaCtor.Slides, viaCtor.CastMode, viaCtor.HoldStaticIndex));
        Assert.Equal((false, SkillCastMode.StaticHold, 3), (viaCopy.Slides, viaCopy.CastMode, viaCopy.HoldStaticIndex));
        // 反例:默认起滑;别的复制方法不碰起滑
        var plain = new SkillStageSpec(0, -1, -1, -1, effectIndex: -1, effectFrame: 0, castMode: SkillCastMode.Pause);
        Assert.True(plain.Slides);
        Assert.True(plain.WithAutoAdvance().Slides);
        Assert.Equal(SkillCastMode.Pause, plain.WithAutoAdvance().CastMode);
    }
}
