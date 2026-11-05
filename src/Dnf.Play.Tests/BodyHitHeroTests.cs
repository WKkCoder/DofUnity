using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 身体命中一族(客户端状态 0x3c、0x3e、0x3f,证据 E-162、E-163)里属于"技能簿一侧"的规则,用合成配方在技能簿加主角上验:横轴起滑、只第 0 段起滑、
/// 按键接段(帧门槛、认的键、跨段、要求的技能)、段伤害倍率、自动接续的上界。数据全是合成的(见 <see cref="AvatarFixture.ChainSkill"/>):
/// 动作 2 是 400 毫秒(四帧各 100)、动作 3 是 300 毫秒(三帧各 100);[static data] 是 (200, −400, 100, −100, 2);[level info] 四列 1 级 (150, 250, 30, 50);冷却 3000。
/// 主角站在 x = 100,滑行位移按匀变速公式 v0·t + a·t²/2(到速度为 0 为止)手算。
/// </summary>
public class BodyHitHeroTests
{
    private static SkillStageSpec Strike(int motion, int attackInfo, int requires = 0)
    {
        return new SkillStageSpec(motionIndex: motion, attackInfoIndex: attackInfo, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0, requiresSkill: requires);
    }

    private static SkillStageSpec Plain(int motion, int requires = 0)
    {
        return new SkillStageSpec(motionIndex: motion, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0, requiresSkill: requires);
    }

    private static SkillRecipes One(SkillRecipe recipe)
    {
        return new SkillRecipes(new[] { recipe });
    }

    private static HeroSkills Load(GameData data, SkillRecipes recipes, int level = 1)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ChainSkill, level));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.ChainSkill) }, 0, 0);   // 技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, recipes);
    }

    private static TownHero NewHero(DnfAssets assets, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), 100, 50, facingLeft, TownHero.DungeonRestTag);
    }

    // ------------------------------------------------------------------ 起滑

    /// <summary>单段配方:基础 (S0 = 200, S1 = −400),顺着按的速度 S2 = 100;<paramref name="bodyHit"/> 为假是旧规则(顺着按是加成、只按上下不滑)。</summary>
    private static SkillRecipes HeldSlide(bool bodyHit, bool followsHeld = true)
    {
        return One(new SkillRecipe(AvatarFixture.ChainSkill, new[] { Strike(2, 1) },
            slideSpeedIndex: 0, slideAccelIndex: 1, forwardSpeedIndex: 2, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: followsHeld,
            bodyHit: bodyHit ? new SkillBodyHitSpec(horizontalHeldSlide: true) : null));
    }

    private static (double Speed, double X) CastAndSlide(HeroSkills skills, DnfAssets assets, int heldX, int heldY, bool facingLeft)
    {
        TownHero hero = NewHero(assets, facingLeft);
        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero, null, heldX, heldY));
        double speed = hero.SkillSlideSpeed;
        hero.Update(400, 0, 0);
        return (speed, hero.X);
    }

    [Theory]
    // 按住的横轴、纵轴、朝左?、起滑速度、400 毫秒内滑的距离(手算:加速度 −400 像素/秒²)
    [InlineData(0, 0, false, 200.0, 48.0)]      // 基础:200 × 0.4 − 200 × 0.4² = 80 − 32 = 48(0.5 秒才停,400 毫秒还在滑)
    [InlineData(1, 0, false, 100.0, 12.5)]      // 顺着按:速度换成 100,0.25 秒停,距离 100² / (2 × 400) = 12.5
    [InlineData(-1, 0, false, 0.0, 0.0)]        // 反着按:不滑
    [InlineData(0, 1, false, 200.0, 48.0)]      // 只按上下:横轴没按,取基础(旧规则不滑)
    [InlineData(0, -1, true, 200.0, 48.0)]
    [InlineData(1, 1, false, 100.0, 12.5)]      // 斜着按:只看横轴
    [InlineData(-1, -1, false, 0.0, 0.0)]
    [InlineData(-1, 0, true, 100.0, 12.5)]      // 朝左按左:顺着
    [InlineData(1, 0, true, 0.0, 0.0)]
    [InlineData(0, 0, true, 200.0, 48.0)]
    public void TheBodyHitSlideReadsOnlyTheHorizontalHeldDirection(int heldX, int heldY, bool facingLeft, double speed, double distance)
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)), HeldSlide(bodyHit: true));
        (double s, double x) = CastAndSlide(skills, assets, heldX, heldY, facingLeft);
        Assert.Equal(speed, s);
        Assert.Equal(100 + (facingLeft ? -distance : distance), x, 6);
    }

    [Fact]
    public void TheOldRuleStillAddsTheForwardSpeedAndStopsOnAVerticalKeyWhenTheRecipeHasNoBodyHitRules()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        Assert.Equal(0.0, CastAndSlide(Load(data, HeldSlide(bodyHit: false)), assets, 0, 1, false).Speed);       // 旧规则:只按上下不滑
        Assert.Equal(300.0, CastAndSlide(Load(data, HeldSlide(bodyHit: false)), assets, 1, 0, false).Speed);     // 旧规则:顺着按是 200 + 100 的加成,不是换成 100
        Assert.Equal(200.0, CastAndSlide(Load(data, HeldSlide(bodyHit: false)), assets, 0, 0, false).Speed);
    }

    [Fact]
    public void ARecipeThatDoesNotReadTheHeldDirectionSlidesTheSameWhateverIsHeldEvenWithTheBodyHitRule()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        Assert.Equal(200.0, CastAndSlide(Load(data, HeldSlide(bodyHit: true, followsHeld: false)), assets, -1, 0, false).Speed);    // 反着按也滑基础的
        Assert.Equal(200.0, CastAndSlide(Load(data, HeldSlide(bodyHit: true, followsHeld: false)), assets, 1, 0, false).Speed);     // 顺着按也不换
    }

    /// <summary>两段配方,第 0 段按技能键接第 1 段(第 0 帧起);两段都配了起滑数。</summary>
    private static SkillRecipes TwoSlidingStages(bool firstStageOnly)
    {
        return One(new SkillRecipe(AvatarFixture.ChainSkill, new[] { Strike(2, 1), Strike(3, 2) },
            slideSpeedIndex: 0, slideAccelIndex: 1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue,
            bodyHit: new SkillBodyHitSpec(slideFirstStageOnly: firstStageOnly, chains: new[] { new SkillKeyChain(0, 0, 1, 0, SkillChainKeys.SkillKey) })));
    }

    [Fact]
    public void OnlyTheFirstStageSlidesWhenTheSpecSaysSoAndEveryStageSlidesOtherwise()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        foreach ((bool firstOnly, double secondStageSpeed) in new[] { (true, 0.0), (false, 200.0) })
        {
            HeroSkills skills = Load(data, TwoSlidingStages(firstOnly));
            TownHero hero = NewHero(assets);
            Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero));
            Assert.Equal(200.0, hero.SkillSlideSpeed);                                      // 第 0 段总是按配方起滑
            hero.Update(100, 0, 0);
            Assert.NotNull(skills.TryChainBody(SkillKeys.CommandSkill, false, hero));
            Assert.Equal((1, secondStageSpeed), (hero.SkillStage, hero.SkillSlideSpeed));   // 只第 0 段起滑:第 1 段的滑行速度清零;否则第 1 段重新按配方起滑
        }
    }

    // ------------------------------------------------------------------ 按键接段

    private static SkillRecipes Chained(SkillBodyHitSpec body, int stage1Requires = 0)
    {
        return One(new SkillRecipe(AvatarFixture.ChainSkill, new[] { Strike(2, 1), Strike(3, 2, stage1Requires) },
            slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, bodyHit: body));
    }

    [Fact]
    public void AKeyChainFiresOnlyFromTheThresholdFrameAndOnlyForItsKeysAndDoesNotChargeAgain()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)),
            Chained(new SkillBodyHitSpec(chains: new[] { new SkillKeyChain(0, 0, 1, 2, SkillChainKeys.SkillKey) })));
        TownHero hero = NewHero(assets);

        Assert.Null(skills.TryChainBody(SkillKeys.CommandSkill, false, hero));              // 还没放技能:没有可接的
        HeroSkillSlot slot = skills.SlotOf(SkillKeys.A);
        Assert.Equal(SkillCastResult.Cast, skills.Cast(slot, hero));
        Assert.Equal((double)slot.CooldownMs, slot.CooldownLeftMs);                                   // 起手进了冷却
        hero.Update(190, 0, 0);                                                              // 第 1 帧(100 到 200 毫秒)
        Assert.Equal(1, hero.CurrentFrame);
        Assert.Null(skills.TryChainBody(SkillKeys.CommandSkill, false, hero));              // 差一帧:不接
        hero.Update(10, 0, 0);                                                               // 第 2 帧起(200 毫秒)
        Assert.Equal(2, hero.CurrentFrame);
        Assert.Null(skills.TryChainBody(SkillKeys.A, false, hero));                          // 自己的快捷键:这条规则不认
        Assert.Null(skills.TryChainBody(SkillKeys.None, true, hero));                        // 攻击键:不认
        Assert.Null(skills.TryChainBody(SkillKeys.None, false, hero));
        Assert.Equal(0, hero.SkillStage);

        skills.Advance(200);                                                                 // 冷却走掉 200 毫秒;接段不重新进冷却
        double left = slot.CooldownLeftMs;
        Assert.Same(slot, skills.TryChainBody(SkillKeys.CommandSkill, false, hero));
        Assert.Equal((1, HeroMotion.Skill), (hero.SkillStage, hero.Motion));
        Assert.Equal(left, slot.CooldownLeftMs);
        Assert.True(hero.MotionMs < 10);                                                     // 新一段从头计时
        Assert.Null(skills.TryChainBody(SkillKeys.CommandSkill, false, hero));              // 第 1 段不在规则的范围里,不再接
    }

    [Fact]
    public void ABodyHitChainTableReplacesTheOldOwnKeyChain()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 旧规则:没有身体命中规则的两段配方,播到第 2 帧后再按自己的键就接下一段
        var plain = One(new SkillRecipe(AvatarFixture.ChainSkill, new[] { Strike(2, 1), Strike(3, 2) },
            slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 2));
        HeroSkills oldSkills = Load(data, plain);
        TownHero a = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, oldSkills.Cast(oldSkills.SlotOf(SkillKeys.A), a));
        a.Update(200, 0, 0);
        Assert.NotNull(oldSkills.TryChain(SkillKeys.A, a));
        Assert.Equal(1, a.SkillStage);

        // 同样的 chainFromFrame,但配了按键接段表:旧路径让位,按自己的键不再接
        var bodyHit = One(new SkillRecipe(AvatarFixture.ChainSkill, new[] { Strike(2, 1), Strike(3, 2) },
            slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 2,
            bodyHit: new SkillBodyHitSpec(chains: new[] { new SkillKeyChain(0, 0, 1, 2, SkillChainKeys.SkillKey) })));
        HeroSkills newSkills = Load(data, bodyHit);
        TownHero b = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, newSkills.Cast(newSkills.SlotOf(SkillKeys.A), b));
        b.Update(200, 0, 0);
        Assert.Null(newSkills.TryChain(SkillKeys.A, b));
        Assert.Equal(0, b.SkillStage);
    }

    [Fact]
    public void AChainMayAcceptOwnKeyOrAttackKeyAndCanSkipStagesToItsTarget()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        // 三段配方:第 0 段播完自动接第 1 段(上界 0),第 0 到第 1 段按自己的键或攻击键从第 0 帧起接第 2 段
        SkillRecipes three = One(new SkillRecipe(AvatarFixture.ChainSkill, new[] { Strike(2, 1), Strike(3, 2), Strike(2, 1) },
            slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, autoChain: true,
            bodyHit: new SkillBodyHitSpec(autoChainLastFromStage: 0, chains: new[] { new SkillKeyChain(0, 1, 2, 0, SkillChainKeys.Own | SkillChainKeys.AttackKey) })));

        // 从第 0 段按攻击键:跳过第 1 段,直接落在第 2 段
        HeroSkills skills = Load(data, three);
        TownHero hero = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero));
        Assert.Equal(0, hero.CurrentFrame);                                                   // 第 0 帧起就能接(边界)
        Assert.Null(skills.TryChainBody(SkillKeys.CommandSkill, false, hero));              // 技能键不认
        Assert.NotNull(skills.TryChainBody(SkillKeys.None, true, hero));
        Assert.Equal((HeroMotion.Skill, 2), (hero.Motion, hero.SkillStage));
        Assert.Null(skills.TryChainBody(SkillKeys.None, true, hero));                        // 已在第 2 段:规则范围之外

        // 从第 1 段按自己的键:也落在第 2 段
        HeroSkills second = Load(data, three);
        TownHero h2 = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, second.Cast(second.SlotOf(SkillKeys.A), h2));
        h2.SkillFollowUp = second.FollowUp;
        h2.Update(400, 0, 0);                                                                // 第 0 段播完,自动接第 1 段
        Assert.Equal(1, h2.SkillStage);
        Assert.NotNull(second.TryChainBody(SkillKeys.A, false, h2));
        Assert.Equal(2, h2.SkillStage);
    }

    [Fact]
    public void AChainIntoAStageWhoseRequiredSkillIsNotLearnedDoesNothing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        var chains = new[] { new SkillKeyChain(0, 0, 1, 0, SkillChainKeys.SkillKey) };

        HeroSkills locked = Load(data, Chained(new SkillBodyHitSpec(chains: chains), stage1Requires: 9999));       // 9999 没学
        TownHero a = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, locked.Cast(locked.SlotOf(SkillKeys.A), a));
        Assert.Null(locked.TryChainBody(SkillKeys.CommandSkill, false, a));
        Assert.Equal(0, a.SkillStage);

        HeroSkills open = Load(data, Chained(new SkillBodyHitSpec(chains: chains), stage1Requires: AvatarFixture.ChainSkill));   // 要求它自己,学过
        TownHero b = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, open.Cast(open.SlotOf(SkillKeys.A), b));
        Assert.NotNull(open.TryChainBody(SkillKeys.CommandSkill, false, b));
        Assert.Equal(1, b.SkillStage);
    }

    // ------------------------------------------------------------------ 段伤害倍率

    private static HeroStrikeInfo StrikeOf(HeroSkillSlot slot, int stage)
    {
        return slot.StrikeAt(stage).Value;
    }

    [Fact]
    public void AStageScaleMultipliesThePercentOfThatStageOnly()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        SkillRecipes Make(params SkillStageScale[] scales)
        {
            return One(new SkillRecipe(AvatarFixture.ChainSkill, new[] { Strike(2, 1), Strike(3, 2) },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue,
                bodyHit: new SkillBodyHitSpec(scales: scales)));
        }

        // 1 级百分比 150;第 1 段乘 S0 = 200 / 100:150% × 2 = 300%,第 0 段不变
        HeroSkillSlot scaled = Load(data, Make(new SkillStageScale(1, 0))).SlotOf(SkillKeys.A);
        Assert.Equal((150, 300), (StrikeOf(scaled, 0).SkillRatePercent, StrikeOf(scaled, 1).SkillRatePercent));
        // 乘 S2 = 100:恰为 100% 时与没有倍率一致(边界)
        HeroSkillSlot identity = Load(data, Make(new SkillStageScale(1, 2))).SlotOf(SkillKeys.A);
        Assert.Equal((150, 150), (StrikeOf(identity, 0).SkillRatePercent, StrikeOf(identity, 1).SkillRatePercent));
        // 没有倍率的配方:两段都是列值
        HeroSkillSlot none = Load(data, Make()).SlotOf(SkillKeys.A);
        Assert.Equal((150, 150), (StrikeOf(none, 0).SkillRatePercent, StrikeOf(none, 1).SkillRatePercent));
        // 倍率标在第 0 段:第 0 段变、第 1 段不变(2 级时列值 180 × 2 = 360)
        HeroSkillSlot first = Load(data, Make(new SkillStageScale(0, 0)), level: 2).SlotOf(SkillKeys.A);
        Assert.Equal((360, 180), (StrikeOf(first, 0).SkillRatePercent, StrikeOf(first, 1).SkillRatePercent));
    }

    [Fact]
    public void AStageScaleLeavesAFixedOnlyStageAlone()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        // 第 1 段只取固定值列(第 2 列,1 级是 30),没有百分比列:倍率不作用在固定值上
        var recipes = One(new SkillRecipe(AvatarFixture.ChainSkill,
            new[] { Strike(2, 1), new SkillStageSpec(motionIndex: 3, attackInfoIndex: 2, damageRateColumn: -1, damageFixedColumn: 2, effectIndex: -1, effectFrame: 0) },
            slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue,
            bodyHit: new SkillBodyHitSpec(scales: new[] { new SkillStageScale(1, 0) })));
        HeroSkillSlot slot = Load(data, recipes).SlotOf(SkillKeys.A);
        Assert.Equal((0, 30), (StrikeOf(slot, 1).SkillRatePercent, StrikeOf(slot, 1).SkillFixed));
        Assert.Equal(150, StrikeOf(slot, 0).SkillRatePercent);
    }

    // ------------------------------------------------------------------ 自动接续的上界

    private static SkillRecipes ThreeStagesAuto(int lastFrom)
    {
        return One(new SkillRecipe(AvatarFixture.ChainSkill, new[] { Strike(2, 1), Strike(3, 2), Strike(2, 1) },
            slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, autoChain: true,
            bodyHit: new SkillBodyHitSpec(autoChainLastFromStage: lastFrom)));
    }

    [Fact]
    public void AutoChainStopsAfterTheLastAllowedStageAndRunsToTheEndWithoutABound()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 上界 0:第 0 段播完接第 1 段,第 1 段播完就结束(第 2 段要靠按键才到)
        HeroSkills bounded = Load(data, ThreeStagesAuto(0));
        TownHero a = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, bounded.Cast(bounded.SlotOf(SkillKeys.A), a));
        a.SkillFollowUp = bounded.FollowUp;
        a.Update(400, 0, 0);
        Assert.Equal((HeroMotion.Skill, 1), (a.Motion, a.SkillStage));
        a.Update(300, 0, 0);
        Assert.Equal(HeroMotion.Rest, a.Motion);

        // 没有上界(-1):接到最后一段,最后一段播完才结束
        HeroSkills open = Load(data, ThreeStagesAuto(-1));
        TownHero b = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, open.Cast(open.SlotOf(SkillKeys.A), b));
        b.SkillFollowUp = open.FollowUp;
        b.Update(400, 0, 0);
        b.Update(300, 0, 0);
        Assert.Equal((HeroMotion.Skill, 2), (b.Motion, b.SkillStage));
        b.Update(400, 0, 0);
        Assert.Equal(HeroMotion.Rest, b.Motion);

        // 上界 1:恰好够到第 2 段(边界)
        HeroSkills upTo1 = Load(data, ThreeStagesAuto(1));
        TownHero c = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, upTo1.Cast(upTo1.SlotOf(SkillKeys.A), c));
        c.SkillFollowUp = upTo1.FollowUp;
        c.Update(400, 0, 0);
        c.Update(300, 0, 0);
        Assert.Equal((HeroMotion.Skill, 2), (c.Motion, c.SkillStage));
    }
}
