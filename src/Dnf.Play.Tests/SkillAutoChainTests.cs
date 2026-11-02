using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 两段自动接续类技能(M60,证据 E-154,客户端状态 0x20):第一段动画播完不用再按键就接第二段,身体只在第一段里打人,
/// 第二段一开始放一个主角的被动对象(伤害取技能的另外两列);接段不扣蓝、不进冷却,中途被打断或主角死了就不接。
/// 数据全是合成的(见 <see cref="AvatarFixture.ChainSkill"/>):两段动作 400 与 300 毫秒,第一段第 2 帧(200 毫秒起)有攻击框;
/// 对象用 <see cref="AvatarFixture.WaveObjectId"/>(四帧各 50 毫秒,第 0、1 帧有攻击框,x 从 +20 起宽 120,击倒);
/// [level info] 四列的 2 级是 (180, 300, 40, 60):第一段取第 0 列(没有固定值列),对象取第 1 列(百分比)与第 3 列(固定值);冷却 3000、耗蓝 20。
/// </summary>
public class SkillAutoChainTests
{
    private static SkillRecipes Recipes(bool auto = true, int gateForStage1 = 0, int chainFromFrame = 1, bool withObject = true, bool stageEffects = true)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill,
                new[]
                {
                    new SkillStageSpec(motionIndex: 2, attackInfoIndex: 1, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: stageEffects ? 0 : -1, effectFrame: 0),
                    new SkillStageSpec(motionIndex: 3, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: stageEffects ? 1 : -1, effectFrame: 0,
                        requiresSkill: gateForStage1),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: chainFromFrame, slideFollowsHeldDirection: false,
                objects: withObject
                    ? new[] { new SkillObjectSpec(AvatarFixture.WaveObjectId, stage: 1, frame: 0, damageRateColumn: 1, damageFixedColumn: 3) }
                    : null,
                autoChain: auto),
        });
    }

    private static HeroSkills Load(GameData data, int level = 2, SkillRecipes recipes = null)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ChainSkill, level));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.ChainSkill) }, 0, 0);   // 技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, recipes ?? Recipes());
    }

    private static TownHero NewHero(DnfAssets assets, double x = 100, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), x, 50, facingLeft, TownHero.DungeonRestTag);
    }

    private static RoomCombatTests.Arena NewArena(SkillRecipes recipes = null, HeroVitals vitals = null, (int X, int Y)[] monsters = null)
    {
        var arena = new RoomCombatTests.Arena(monsters ?? new[] { (340, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, vitals: vitals,
            skillsFactory: data => Load(data, 2, recipes));
        arena.Session.AttachActs(new ActLibrary(arena.Assets, new GameData(new PvfScriptSource(arena.Assets.Pvf))));
        return arena;
    }

    private static void Run(RoomCombatSession s, int ms, params (int At, SkillKeys Keys)[] presses)
    {
        for (int t = 0; t < ms; t += 10)
        {
            SkillKeys keys = SkillKeys.None;
            foreach ((int at, SkillKeys k) in presses)
            {
                if (at == t)
                {
                    keys |= k;
                }
            }
            s.Update(10, 0, 0, false, false, keys);
        }
    }

    // ------------------------------------------------------------------ 配方与表

    [Fact]
    public void AnAutoChainRecipeNeedsTwoStagesAndCannotBeAHopADiveOrAJumpAttack()
    {
        var one = new[] { new SkillStageSpec(2, 1, 0, -1, 0, 0) };
        var two = new[] { new SkillStageSpec(2, 1, 0, -1, 0, 0), new SkillStageSpec(3, -1, -1, -1, 1, 0) };
        Assert.False(new SkillRecipe(1, two, -1, -1, -1, -1, 1).AutoChain);                            // 默认不是
        Assert.True(new SkillRecipe(1, two, -1, -1, -1, -1, 1, autoChain: true).AutoChain);
        Assert.Throws<ArgumentException>(() => new SkillRecipe(1, one, -1, -1, -1, -1, 1, autoChain: true));         // 只有一段:没有可接的
        Assert.Throws<ArgumentException>(() => new SkillRecipe(1, two, -1, -1, -1, -1, 1, autoChain: true,
            hop: new SkillHopSpec(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 2, 4, 3, 5, 4)));
        Assert.Throws<ArgumentException>(() => new SkillRecipe(1, two, -1, -1, -1, -1, 1, autoChain: true, dive: new SkillDiveSpec(200, 470, -900, 0, 3)));
        Assert.Throws<ArgumentException>(() => new SkillRecipe(1, two, -1, -1, -1, -1, 1, autoChain: true,
            jumpAttack: new SkillJumpAttackSpec(23, 24, 2, new[] { 4 }, new[] { 5 })));
    }

    [Fact]
    public void TheDefaultTableHasAnAutoChainRecipeWithABodyStrikeFirstAndAHeroObjectOnTheSecondStage()
    {
        SkillRecipe auto = null;
        foreach (SkillRecipe r in SkillRecipes.Default.All)
        {
            if (r.AutoChain && r.Objects.Count > 0)
            {
                Assert.Null(auto);                                           // 带对象的自动接续配方只有一个(没有对象的姿势→挥击类见 SkillPoseStrikeTests)
                auto = r;
            }
        }
        Assert.NotNull(auto);
        Assert.True(HeroSkills.IsImplemented(auto.SkillId));
        Assert.Equal(2, auto.Stages.Count);
        // 第一段:身体有攻击信息与百分比列、没有固定值列;第二段:身体不打人,放预载特效
        Assert.True(auto.Stages[0].HasOwnStrike && auto.Stages[0].DamageRateColumn >= 0 && auto.Stages[0].DamageFixedColumn == -1);
        Assert.False(auto.Stages[1].HasOwnStrike);
        Assert.Equal(new[] { auto.Stages[0].MotionIndex + 1 }, new[] { auto.Stages[1].MotionIndex });
        Assert.True(auto.Stages[0].EffectIndices.Count == 2 && auto.Stages[1].EffectIndices.Count == 1);
        // 对象在第二段一开始放,伤害取两个不同的列;不滑行;不看方向键
        SkillObjectSpec o = Assert.Single(auto.Objects);
        Assert.Equal((1, 0, false), (o.Stage, o.Frame, o.OnLanding));
        Assert.True(o.DamageRateColumn >= 0 && o.DamageFixedColumn >= 0 && o.DamageRateColumn != o.DamageFixedColumn);
        Assert.True(auto.SlideSpeedIndex < 0 && auto.SlideAccelIndex < 0 && !auto.SlideFollowsHeldDirection);
        Assert.Null(auto.Hop);
        Assert.Null(auto.Dive);
        Assert.Null(auto.JumpAttack);
    }

    // ------------------------------------------------------------------ 槽

    [Fact]
    public void ASlotReadsTheBodyStrikeFromTheFirstStageAndTheObjectsDamageFromTheOtherColumns()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkillSlot slot = Load(new GameData(new PvfScriptSource(assets.Pvf))).SlotOf(SkillKeys.A);

        Assert.True(slot.Recipe.AutoChain);
        Assert.Equal(2, slot.StageStrikes.Count);
        HeroStrikeInfo body = slot.StageStrikes[0];
        Assert.Equal((true, 180, 0), (body.IsSkillDamage, body.SkillRatePercent, body.SkillFixed));      // 第 0 列的百分比、没有固定值
        Assert.True(slot.StageHasOwnStrike(0));
        Assert.False(slot.StageHasOwnStrike(1));
        HeroStrikeInfo obj = Assert.Single(slot.ObjectStrikes);
        Assert.Equal((true, 300, 60, true), (obj.IsSkillDamage, obj.SkillRatePercent, obj.SkillFixed, obj.KnockDown));   // 第 1 列与第 3 列;对象的攻击信息带击倒
        Assert.Equal(new[] { AvatarFixture.ChainEffect1, AvatarFixture.ChainEffect2 }, slot.StageEffects.ToArray());
    }

    // ------------------------------------------------------------------ HeroSkills.FollowUp

    [Fact]
    public void TheFollowUpChainsOnlyAnAutoChainSkillInItsMainPhaseAndOnlyToARealNextStage()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkills skills = Load(data);
        HeroSkillSlot slot = skills.SlotOf(SkillKeys.A);
        TownHero hero = NewHero(assets);
        var vitals = new HeroVitals(1, 500, 100, 0, 0, 0);

        // 没在放技能 / 没有角色:不接
        Assert.False(skills.FollowUp(hero));
        Assert.False(skills.FollowUp(null));

        Assert.Equal(SkillCastResult.Cast, skills.Cast(slot, hero, vitals));
        Assert.Equal((80, 3000.0), (vitals.Mp, slot.CooldownLeftMs));
        long swings = hero.SwingNumber;

        // 接上第二段:换动作、新的一击、段号加 1;不扣蓝、不重置冷却
        skills.Advance(500);
        Assert.True(skills.FollowUp(hero));
        Assert.Equal((1, "chainb.ani", 0, swings + 1), (hero.SkillStage, hero.SkillMotionName, hero.CurrentFrame, hero.SwingNumber));
        Assert.Equal((80, 2500.0), (vitals.Mp, slot.CooldownLeftMs));

        // 已经是最后一段:没有下一段
        Assert.False(skills.FollowUp(hero));
        Assert.Equal(1, hero.SkillStage);
    }

    [Fact]
    public void ARecipeThatIsNotAutoChainNeverFollowsUpByItself()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)), 2, Recipes(auto: false));
        TownHero hero = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero));
        hero.SkillFollowUp = skills.FollowUp;

        hero.Update(400, 0, 0);                                      // 第一段播完:不是自动接续,技能结束
        Assert.Equal((HeroMotion.Rest, 0), (hero.Motion, hero.SkillStage));
        Assert.False(skills.FollowUp(hero));
    }

    [Fact]
    public void TheNextStageNeedsItsRequiredSkillLearnedAndOtherwiseTheSkillEndsAfterTheFirstStage()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 第二段要求的技能没学(9999):播完就结束
        HeroSkills locked = Load(data, 2, Recipes(gateForStage1: 9999));
        TownHero a = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, locked.Cast(locked.SlotOf(SkillKeys.A), a));
        a.SkillFollowUp = locked.FollowUp;
        a.Update(400, 0, 0);
        Assert.Equal((HeroMotion.Rest, 0), (a.Motion, a.SkillStage));

        // 要求的技能学过了(这里要求它自己,2 级):接上
        HeroSkills open = Load(data, 2, Recipes(gateForStage1: AvatarFixture.ChainSkill));
        TownHero b = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, open.Cast(open.SlotOf(SkillKeys.A), b));
        b.SkillFollowUp = open.FollowUp;
        b.Update(400, 0, 0);
        Assert.Equal((HeroMotion.Skill, 1), (b.Motion, b.SkillStage));
    }

    [Fact]
    public void PressingTheSkillKeyAgainDoesNotChainAnAutoChainSkillEvenPastTheChainFrame()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)));          // 接段帧 1,动画已过第 2 帧
        TownHero hero = NewHero(assets);
        Assert.NotNull(skills.TryCast(SkillKeys.A, hero));
        hero.Update(250, 0, 0);
        Assert.Equal(2, hero.CurrentFrame);
        Assert.Null(skills.TryChain(SkillKeys.A, hero));
        Assert.Equal(0, hero.SkillStage);

        // 对照:同一个配方不标自动接续,同一刻按键就接上了
        HeroSkills manual = Load(new GameData(new PvfScriptSource(assets.Pvf)), 2, Recipes(auto: false));
        TownHero other = NewHero(assets);
        Assert.NotNull(manual.TryCast(SkillKeys.A, other));
        other.Update(250, 0, 0);
        Assert.NotNull(manual.TryChain(SkillKeys.A, other));
        Assert.Equal(1, other.SkillStage);
    }

    // ------------------------------------------------------------------ 房间里

    [Fact]
    public void InTheRoomTheFirstStageHitsThenTheSecondStartsByItselfAndDropsTheObjectWhichHitsOnce()
    {
        // 主角 (300, 100) 朝右;怪物在 (340, 100):第一段第 2 帧(200 毫秒起)的身体攻击框够得着,对象(放在主角脚下,框 x 从 320 到 440)也够得着
        using var arena = NewArena();
        RoomCombatSession s = arena.Session;
        MonsterActor m = s.Monsters[0];
        HeroSkillSlot slot = s.Skills.SlotOf(SkillKeys.A);

        Run(s, 10, (0, SkillKeys.A));
        Assert.Equal(1, s.SkillCasts);
        Run(s, 210);                                                 // 220 毫秒:第一段身体的框打中一次,还没接第二段
        Assert.Equal((1, 0, 0, 0, 0), (s.Hits, s.SkillChains, s.Hero.SkillStage, s.HeroObjectsSpawned, s.HeroObjectHits));
        double body = PlaceholderDamage.SkillBaseHit * 180 / 100.0;
        Assert.InRange(s.TotalDamage, (long)Math.Floor(body), (long)Math.Floor(body) + 1);
        Assert.False(m.IsKnockedDown);

        // 没按任何键:第一段 400 毫秒播完那一步自己接上第二段,同一步里放出对象(接段不算新的施放)
        Run(s, 170);                                                 // 390 毫秒
        Assert.Equal((0, 0), (s.SkillChains, s.Hero.SkillStage));
        Run(s, 10);                                                  // 400 毫秒
        Assert.Equal((1, 1, 1, 1), (s.SkillChains, s.Hero.SkillStage, s.HeroObjectsSpawned, s.SkillCasts));
        Assert.Equal(HeroMotion.Skill, s.Hero.Motion);
        Assert.Single(s.HeroObjects);

        // 对象打中一次(第二段里主角的身体不打人),击倒;伤害取对象的两列(300% + 固定 60)
        long before = s.TotalDamage;
        Run(s, 100);
        Assert.Equal((2, 1), (s.Hits, s.HeroObjectHits));
        double objectHit = PlaceholderDamage.SkillBaseHit * 300 / 100.0 + 60;
        Assert.InRange(s.TotalDamage - before, (long)Math.Floor(objectHit), (long)Math.Floor(objectHit) + 1);
        Assert.True(m.IsKnockedDown);

        // 第二段 300 毫秒播完:回到站立,没有再接、没有再放对象
        Run(s, 400);
        Assert.Equal((HeroMotion.Rest, 0, 1, 1, 1), (s.Hero.Motion, s.Hero.SkillStage, s.SkillChains, s.HeroObjectsSpawned, s.SkillCasts));
        Assert.Equal(2, s.Hits);
        Assert.InRange(slot.CooldownLeftMs, 2000.0, 2300.0);         // 3000 毫秒冷却从起手算起,已过 ~910 毫秒:接段没有把它重置回满
    }

    [Fact]
    public void AKeyPressedWhileTheSkillIsRunningDoesNothingAndTheStagesStillRunOnTheirOwn()
    {
        using var arena = NewArena(vitals: new HeroVitals(1, 1000, 500, 0, 0, 0), monsters: new[] { (900, 100) });
        RoomCombatSession s = arena.Session;

        Run(s, 400, (0, SkillKeys.A), (250, SkillKeys.A));          // 第 2 帧之后又按了一下:不接段(自动接续不看按键),也不是新的施放(冷却中)
        Assert.Equal((1, 1, 1, 480), (s.SkillCasts, s.SkillChains, s.Hero.SkillStage, s.Vitals.Mp));          // 400 毫秒:自己接上了,魔法值只在起手扣过一次
        Run(s, 100, (50, SkillKeys.A));                              // 第二段里再按:同样没反应
        Assert.Equal((1, 1, 1, 480), (s.SkillCasts, s.SkillChains, s.Hero.SkillStage, s.Vitals.Mp));
        Run(s, 300);
        Assert.Equal((HeroMotion.Rest, 1, 1, 480), (s.Hero.Motion, s.SkillChains, s.SkillCasts, s.Vitals.Mp));
    }

    [Fact]
    public void AnInterruptedFirstStageDoesNotChainAndASecondStageBeginsOnlyOnce()
    {
        using var arena = NewArena(monsters: new[] { (900, 100) });
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, 100);
        Assert.True(s.Hero.BeginHurt(100, 0, 0, true));              // 第一段播到一半被打:技能中断
        Run(s, 600);
        Assert.Equal((0, 0, HeroMotion.Rest), (s.SkillChains, s.HeroObjectsSpawned, s.Hero.Motion));
    }

    [Fact]
    public void ADeadHeroDoesNotChainOnToTheSecondStage()
    {
        using var arena = NewArena(vitals: new HeroVitals(1, 1000, 500, 0, 0, 0), monsters: new[] { (900, 100) });
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, 100);
        Assert.Equal(HeroMotion.Skill, s.Hero.Motion);
        s.Vitals.Damage(s.Vitals.Hp);
        Assert.True(s.HeroDead);
        Run(s, 600);
        Assert.Equal((0, 0), (s.SkillChains, s.HeroObjectsSpawned));
    }

    [Fact]
    public void TheStageEffectsAreSplitAcrossTheStagesAndTheSecondStageEffectAppearsWhenItStarts()
    {
        using var arena = NewArena(monsters: new[] { (900, 100) });
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Assert.Equal(new[] { AvatarFixture.ChainEffect1 }, s.Effects.Select(e => e.Path).ToArray());      // 第一段一开始放第一个
        Run(s, 280);                                                 // 290 毫秒:第二段的特效还没有
        Assert.DoesNotContain(s.Effects, e => e.Path == AvatarFixture.ChainEffect2);
        Run(s, 110);                                                 // 400 毫秒:接上第二段,第二段的特效放出来(第一段的 350 毫秒特效已播完)
        Assert.Equal(1, s.Hero.SkillStage);
        Assert.Contains(s.Effects, e => e.Path == AvatarFixture.ChainEffect2);
        Run(s, 50);
        Assert.Single(s.Effects, e => e.Path == AvatarFixture.ChainEffect2);                              // 同一段不重复放
    }

    [Fact]
    public void WithoutAnObjectTheSecondStageStillRunsButNothingIsDropped()
    {
        using var arena = NewArena(Recipes(withObject: false), monsters: new[] { (340, 100) });
        RoomCombatSession s = arena.Session;
        Run(s, 10, (0, SkillKeys.A));
        Run(s, 450);
        Assert.Equal((1, 0, 1), (s.SkillChains, s.HeroObjectsSpawned, s.Hits));        // 只有第一段身体那一下
        Assert.Equal(1, s.Hero.SkillStage);
        Run(s, 300);
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);
    }
}
