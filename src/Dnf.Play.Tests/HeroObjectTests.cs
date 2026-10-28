using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 主角技能放出的被动对象与施法蓄力(M42,证据 E-135)。数据全是合成的(见 <see cref="AvatarFixture.WaveSkill"/>):
/// 施法动作六帧(第 0 帧蓄力占位,第 1 到 5 帧各 50 毫秒,第 4 帧带一个"身体"攻击框),[casting time] 300 毫秒 → 蓄力 150 毫秒;
/// 动画播到第 3 帧(250 毫秒起)放一个老式被动对象,对象四帧各 50 毫秒、第 0、1 帧有攻击框;2 级伤害 (百分比 333、固定值 12);消耗魔法 15、冷却 2500。
/// </summary>
public class HeroObjectTests
{
    private static SkillRecipes Recipes(int castHoldFrames = 3, int objectFrame = 3, int forwardX = 0, int offsetY = 0, int effectFrame = 1, bool withObject = true)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.WaveSkill,
                new[] { new SkillStageSpec(motionIndex: 4, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: 0, effectFrame: effectFrame, castHoldFrames: castHoldFrames) },
                slideSpeedIndex: 1, slideAccelIndex: 2, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1, slideFollowsHeldDirection: false,
                objects: withObject
                    ? new[] { new SkillObjectSpec(AvatarFixture.WaveObjectId, 0, objectFrame, damageRateColumn: 2, damageFixedColumn: 0, forwardX: forwardX, offsetY: offsetY) }
                    : null),
        });
    }

    /// <summary>多特效、等特效关键帧标志、定结束帧的配方(证据 E-137):蓄力同波系配方;对象放在第 <paramref name="objectFrame"/> 帧以后(再等特效标志)。</summary>
    private static SkillRecipes EffectRecipes(int[] moreEffects = null, int endFrame = -1, int afterEffect = -1, int objectFrame = 0, int effectFrame = 0)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.WaveSkill,
                new[]
                {
                    new SkillStageSpec(motionIndex: 4, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: 0, effectFrame: effectFrame, castHoldFrames: 3,
                        moreEffects: moreEffects, endFrame: endFrame),
                },
                slideSpeedIndex: 1, slideAccelIndex: 2, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1, slideFollowsHeldDirection: false,
                objects: new[]
                {
                    new SkillObjectSpec(AvatarFixture.WaveObjectId, 0, objectFrame, damageRateColumn: 2, damageFixedColumn: 0, afterEffect: afterEffect,
                        afterEffectFlag: AvatarFixture.WaveEffect2Flag),
                }),
        });
    }

    private static HeroSkills Load(GameData data, int level = 2, SkillRecipes recipes = null)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.WaveSkill, level));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.WaveSkill) }, 0, 0);   // 波系技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, recipes ?? Recipes());
    }

    private static HeroSkills Factory(GameData data)
    {
        return Load(data);
    }

    private static TownHero NewHero(DnfAssets assets, double x = 100, double y = 50, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), x, y, facingLeft, TownHero.DungeonRestTag);
    }

    private static void Attach(RoomCombatTests.Arena arena)
    {
        arena.Session.AttachActs(new ActLibrary(arena.Assets, new GameData(new PvfScriptSource(arena.Assets.Pvf))));
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

    // ------------------------------------------------------------------ 槽与蓄力

    [Fact]
    public void ASlotReadsTheObjectsStrikeFromTheObjectFileAndTheSkillLevelColumns()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkillSlot slot = Load(data, 2).SlotOf(SkillKeys.A);

        Assert.Equal((AvatarFixture.WaveSkill, 2, 2500, 15), (slot.SkillId, slot.Level, slot.CooldownMs, slot.ConsumeMp));
        Assert.Single(slot.ObjectStrikes);
        HeroStrikeInfo o = slot.ObjectStrikes[0];
        Assert.Equal((true, 333, 12, AvatarFixture.WavePushAside, true), (o.IsSkillDamage, o.SkillRatePercent, o.SkillFixed, o.PushAside, o.KnockDown));
        Assert.Equal(AvatarFixture.WaveHitSound, o.HitWav);

        // 1 级取 [level info] 的第一行(百分比第 2 列、固定值第 0 列)
        HeroStrikeInfo first = Load(data, 1).SlotOf(SkillKeys.A).ObjectStrikes[0];
        Assert.Equal((222, 11), (first.SkillRatePercent, first.SkillFixed));

        // 这一段主角自己的动画不命中:没有自己的攻击信息(默认值),StageHasOwnStrike 为假
        Assert.False(slot.StageHasOwnStrike(0));
        Assert.False(slot.StageHasOwnStrike(7));                     // 段号越界取最后一段
        Assert.Equal(0, slot.StageStrikes[0].SkillRatePercent);
        Assert.Equal(new[] { AvatarFixture.WaveEffect, AvatarFixture.WaveEffect2 }, slot.StageEffects.ToArray());   // 预载表里的特效(按登记顺序)

        // 没有放对象的配方:对象表为空
        Assert.Empty(Load(data, 2, Recipes(withObject: false)).SlotOf(SkillKeys.A).ObjectStrikes);
    }

    [Fact]
    public void TheCastHoldIsTheCastingTimeMinusTheNextFramesAndTheFirstFrameWaitsThatLong()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkills skills = Load(data);
        TownHero hero = NewHero(assets);

        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero));
        Assert.Equal((HeroMotion.Skill, "wavecast.ani", 0), (hero.Motion, hero.SkillMotionName, hero.CurrentFrame));
        hero.Update(149, 0, 0);
        Assert.Equal(0, hero.CurrentFrame);                          // 蓄力 = 300 − (50 + 50 + 50) = 150 毫秒
        hero.Update(1, 0, 0);
        Assert.Equal(1, hero.CurrentFrame);
        hero.Update(250, 0, 0);
        Assert.Equal(HeroMotion.Rest, hero.Motion);                  // 整段 150 + 250 = 400 毫秒

        // 蓄力的帧数是配方里的常量:只减前 2 帧就是 300 − 100 = 200 毫秒;减 5 帧是 50 毫秒
        foreach ((int frames, int hold) in new[] { (2, 200), (5, 50), (0, -1) })
        {
            TownHero other = NewHero(assets);
            HeroSkills s = Load(data, 2, Recipes(castHoldFrames: frames));
            Assert.Equal(SkillCastResult.Cast, s.Cast(s.SlotOf(SkillKeys.A), other));
            if (hold >= 0)
            {
                other.Update(hold - 1, 0, 0);
                Assert.Equal(0, other.CurrentFrame);
                other.Update(1, 0, 0);
                Assert.Equal(1, other.CurrentFrame);
            }
            else
            {
                other.Update(9000, 0, 0);                            // 没有蓄力:第 0 帧原样停 10 秒
                Assert.Equal((HeroMotion.Skill, 0), (other.Motion, other.CurrentFrame));
            }
        }
    }

    [Fact]
    public void ASkillWithoutACastingTimeHasNoHoldAndStartsOnTheFirstTimedFrame()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        // 多段技能的 .skl 没有 [casting time]:施法时间按 0 算,蓄力是 max(0, 0 − 150) = 0,第 0 帧不占时间
        var recipes = new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill,
                new[] { new SkillStageSpec(motionIndex: 4, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0, castHoldFrames: 3) },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1),
        });
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ChainSkill, 2));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.ChainSkill) }, 0, 0);
        HeroSkills skills = HeroSkills.Create(data, chr, 0, book, recipes);
        TownHero hero = NewHero(assets);

        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero));
        Assert.Equal(1, hero.CurrentFrame);
    }

    [Fact]
    public void TheDefaultTableHasAWaveRecipeWithAHoldAndAnObjectAndNoBodyStrike()
    {
        // 默认配方表里的波系配方(证据 E-135):施法蓄力减前三帧、主角身体不命中、放一个对象;文件与编号不在这里断言
        SkillRecipe wave = null;
        foreach (SkillRecipe r in SkillRecipes.Default.All)
        {
            if (r.Objects.Count > 0 && r.Stages[0].CastHoldFrames > 0)
            {
                wave = r;
            }
        }
        Assert.NotNull(wave);
        Assert.Single(wave.Stages);
        Assert.Equal((3, false), (wave.Stages[0].CastHoldFrames, wave.Stages[0].HasOwnStrike));
        Assert.True(HeroSkills.IsImplemented(wave.SkillId));
        Assert.False(wave.SlideFollowsHeldDirection);
        Assert.All(wave.Objects, o => Assert.True(o.Frame >= 1 && o.DamageRateColumn != o.DamageFixedColumn));
    }

    // ------------------------------------------------------------------ 多特效、特效标志与结束帧(M44,证据 E-137)

    [Fact]
    public void AStagesEffectListIsEveryIndexOnceWithTheFirstOneFirst()
    {
        var one = new SkillStageSpec(0, -1, -1, -1, effectIndex: 2, effectFrame: 0);
        Assert.Equal(new[] { 2 }, one.EffectIndices.ToArray());
        var none = new SkillStageSpec(0, -1, -1, -1, effectIndex: -1, effectFrame: 0);
        Assert.Empty(none.EffectIndices);
        var many = new SkillStageSpec(0, -1, -1, -1, effectIndex: 0, effectFrame: 0, moreEffects: new[] { 1, 2, 3 }, endFrame: 10);
        Assert.Equal(new[] { 0, 1, 2, 3 }, many.EffectIndices.ToArray());
        Assert.Equal(10, many.EndFrame);
        Assert.Equal(-1, one.EndFrame);
        var onlyMore = new SkillStageSpec(0, -1, -1, -1, effectIndex: -1, effectFrame: 0, moreEffects: new[] { 4 });
        Assert.Equal(new[] { 4 }, onlyMore.EffectIndices.ToArray());     // 第一个下标是 -1 时只放"额外"的
    }

    [Fact]
    public void TheStageEndFrameEndsTheSkillStateAtThatFramesStartInsteadOfTheAnimationsEnd()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        // 施法动作:第 0 帧蓄力 150 毫秒,第 1 到 5 帧各 50 毫秒(第 3 帧从 250 毫秒起,整段 400 毫秒)
        foreach ((int endFrame, int endsAt) in new[] { (3, 250), (1, 150), (5, 350), (6, 400), (99, 400), (-1, 400) })
        {
            HeroSkills skills = Load(data, 2, EffectRecipes(endFrame: endFrame));
            TownHero hero = NewHero(assets);
            Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero));
            hero.Update(endsAt - 1, 0, 0);
            Assert.Equal(HeroMotion.Skill, hero.Motion);
            hero.Update(1, 0, 0);
            Assert.Equal(HeroMotion.Rest, hero.Motion);                  // 帧号太大(动画没有这么多帧)等于不设结束帧
        }
    }

    [Fact]
    public void EveryEffectOfAStageIsPlacedWhenTheStageStartsAndAnEffectIndexOutsideThePreloadTableIsSkipped()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, 2, EffectRecipes(moreEffects: new[] { 1, 5 })));
        Attach(arena);
        RoomCombatSession s = arena.Session;

        Assert.Empty(s.Effects);
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Assert.Equal(new[] { AvatarFixture.WaveEffect, AvatarFixture.WaveEffect2 }, s.Effects.Select(e => e.Path).ToArray());   // 下标 5 预载表里没有:不放
        Run(s, 60);
        Assert.Equal(2, s.Effects.Count);                                // 同一次挥击不重复放
    }

    [Fact]
    public void AnObjectThatWaitsForAnEffectsKeyFrameFlagAppearsOnlyWhenThatEffectReachesTheFlaggedFrame()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, 2, EffectRecipes(moreEffects: new[] { 1 }, afterEffect: 1)));
        Attach(arena);
        RoomCombatSession s = arena.Session;

        s.Update(10, 0, 0, false, false, SkillKeys.A);                   // 特效这一步放出来,播放时间从 0 起
        Assert.Equal((2, 0), (s.Effects.Count, s.HeroObjectsSpawned));
        Run(s, 90);                                                      // 特效已播 90 毫秒:标志帧(第 2 帧)从 100 毫秒起,还没到
        Assert.Equal(0, s.HeroObjectsSpawned);
        Run(s, 10);                                                      // 这一步开头特效已播 100 毫秒:到了
        Assert.Equal(1, s.HeroObjectsSpawned);
        Run(s, 100);
        Assert.Equal(1, s.HeroObjectsSpawned);                           // 一次挥击只放一次
    }

    [Fact]
    public void AnObjectDoesNotWaitWhenItsEffectIsNotPartOfTheStageOrWasNeverPlaced()
    {
        // 没有 afterEffect:第一步就放(对照)
        using (var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, 2, EffectRecipes(moreEffects: new[] { 1 }, afterEffect: -1))))
        {
            Attach(arena);
            arena.Session.Update(10, 0, 0, false, false, SkillKeys.A);
            Assert.Equal(1, arena.Session.HeroObjectsSpawned);
        }
        // 等的特效这一段并不放(只放第 0 个):不拦着
        using (var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, 2, EffectRecipes(afterEffect: 1))))
        {
            Attach(arena);
            arena.Session.Update(10, 0, 0, false, false, SkillKeys.A);
            Assert.Equal((1, 1), (arena.Session.Effects.Count, arena.Session.HeroObjectsSpawned));
        }
        // 这一段放,但预载表里没有这个特效(没放出来):退回只看帧号
        using (var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, 2, EffectRecipes(moreEffects: new[] { 5 }, afterEffect: 5))))
        {
            Attach(arena);
            arena.Session.Update(10, 0, 0, false, false, SkillKeys.A);
            Assert.Equal((1, 1), (arena.Session.Effects.Count, arena.Session.HeroObjectsSpawned));
        }
    }

    [Fact]
    public void TheFrameGateStillAppliesBeforeTheEffectFlagIsLookedAt()
    {
        // 对象要第 3 帧(250 毫秒起)以后、并且特效到了标志才放:标志(100 毫秒)早到了,放的时间由帧号定
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, 2, EffectRecipes(moreEffects: new[] { 1 }, afterEffect: 1, objectFrame: 3)));
        Attach(arena);
        RoomCombatSession s = arena.Session;
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Run(s, 230);                                                     // 240 毫秒
        Assert.Equal(0, s.HeroObjectsSpawned);
        Run(s, 20);                                                      // 260 毫秒:第 3 帧已过
        Assert.Equal(1, s.HeroObjectsSpawned);
    }

    [Fact]
    public void TheDefaultTableHasAMultiEffectRecipeWithAnEndFrameAndAnObjectThatWaitsForAnEffectFlag()
    {
        SkillRecipe multi = null;
        foreach (SkillRecipe r in SkillRecipes.Default.All)
        {
            if (r.Stages.Count == 1 && r.Stages[0].EffectIndices.Count >= 3)
            {
                multi = r;
            }
        }
        Assert.NotNull(multi);
        SkillStageSpec st = multi.Stages[0];
        Assert.False(st.HasOwnStrike);                                   // 动作里没有攻击框:身体不打人
        Assert.True(st.EndFrame > 0);
        Assert.Equal(st.EffectIndices.Distinct().Count(), st.EffectIndices.Count);
        Assert.Single(multi.Objects);
        SkillObjectSpec o = multi.Objects[0];
        Assert.Contains(o.AfterEffect, st.EffectIndices);
        Assert.True(o.Frame >= 1 && o.ForwardX > 0 && o.OffsetZ > 0 && o.DamageRateColumn != o.DamageFixedColumn);
        Assert.True(HeroSkills.IsImplemented(multi.SkillId));
        Assert.False(multi.SlideFollowsHeldDirection);
        Assert.True(multi.SlideSpeedIndex < 0);                          // 进入块把速度清零:不滑行
    }

    // ------------------------------------------------------------------ 房间里

    [Fact]
    public void TheObjectAppearsAtItsFrameHitsEachMonsterOnceAndTheBodyAnimationDoesNotHit()
    {
        // 主角 (300, 100) 朝右;怪物在 (340, 100)。施法动作第 4 帧(300 毫秒起)有"身体"攻击框:它不该命中(没有自己的攻击信息)
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, skillsFactory: Factory);
        Attach(arena);
        RoomCombatSession s = arena.Session;
        MonsterActor m = s.Monsters[0];

        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Assert.Equal((1, 0, 0), (s.SkillCasts, s.HeroObjectsSpawned, s.Hits));
        Run(s, 230);                                                  // 240 毫秒:第 2 帧,对象还没放
        Assert.Equal((0, 0), (s.HeroObjectsSpawned, s.Hits));
        Run(s, 20);                                                   // 260 毫秒:第 3 帧起点(250)已过,对象放出,这一步里不走
        Assert.Equal(1, s.HeroObjectsSpawned);
        Assert.Single(s.HeroObjects);
        Run(s, 20);
        Assert.Equal((1, 1, 1), (s.Hits, s.HeroObjectHits, s.HeroObjects[0].MonstersHit));

        // 伤害按技能 2 级的两列(333% + 12);对象的攻击信息带击倒
        double expected = PlaceholderDamage.SkillBaseHit * 333 / 100.0 + 12;
        Assert.InRange(s.TotalDamage, (long)Math.Floor(expected), (long)Math.Floor(expected) + 1);
        Assert.True(m.IsKnockedDown);

        // 对象的第 1 帧也有攻击框、身体动画第 4 帧也有:都不再打(同一个对象对同一个怪物只打一次;身体不命中)
        Run(s, 300);
        Assert.Equal((1, 1, 1), (s.Hits, s.HeroObjectHits, s.SkillCasts));
        Assert.Empty(s.HeroObjects);                                   // 对象 200 毫秒播完消失
        Assert.Equal(1, s.HeroObjectsSpawned);
    }

    [Fact]
    public void AnObjectHitDoesNotFreezeTheHeroButTheMonsterStillPauses()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, weaponStun: 150, skillsFactory: Factory);
        Attach(arena);
        RoomCombatSession s = arena.Session;
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        for (int i = 0; i < 100 && s.HeroObjectHits == 0; i++)
        {
            s.Update(10, 0, 0, false, false);
        }
        Assert.Equal(1, s.HeroObjectHits);
        Assert.Equal(0.0, s.HeroPauseMs);                              // 主角没被定住(身体挥击命中才定住主角)
        Assert.True(s.Monsters[0].PauseMs > 0);                        // 怪物按命中停顿定住
    }

    [Fact]
    public void ObjectsHitEveryMonsterInReachOnceAndMissTheOnesOutsideTheirBoxes()
    {
        // 对象放在 (300, 100):攻击框 x 为 320 到 440、y 为 80 到 120、高 0 到 100;怪物受击框 x 从 −10 到 +30 宽、y 从 −6 起深 12
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100), (400, 105), (900, 100), (340, 160), (200, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: Factory);
        Attach(arena);
        RoomCombatSession s = arena.Session;
        Run(s, 400, (0, SkillKeys.A));
        Assert.Equal((2, 2, 1), (s.HeroObjectHits, s.Hits, s.HeroObjectsSpawned));
        Assert.Equal(new[] { true, true, false, false, false }, s.Monsters.Select(m => m.IsKnockedDown).ToArray());
    }

    [Fact]
    public void AFacingLeftHeroPlacesTheObjectToTheLeftAndTheOffsetsMoveIt()
    {
        var recipes = Recipes(forwardX: 60, offsetY: 5);
        using var arena = new RoomCombatTests.Arena(new[] { (180, 105) }, facingLeft: true, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, 2, recipes));
        Attach(arena);
        RoomCombatSession s = arena.Session;
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        for (int i = 0; i < 40 && s.HeroObjectsSpawned == 0; i++)
        {
            s.Update(10, 0, 0, false, false);
        }
        HeroObject o = Assert.Single(s.HeroObjects);
        Assert.True(o.FacingLeft);
        Assert.Equal(s.Hero.X - 60, o.X, 6);                           // 朝左:前方是 x 变小的方向
        Assert.Equal(s.Hero.Y + 5, o.Y, 6);
        Assert.Equal(AvatarFixture.WaveSkill, o.SkillId);
        // 攻击框镜像到左边:怪物 (180, 105) 在对象 (≈240, 105) 左侧 40..120 像素内,被打中
        Run(s, 60);
        Assert.Equal(1, s.HeroObjectHits);
    }

    [Fact]
    public void OneObjectPerSwingAndEveryNewCastPlacesAnother()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, skillsFactory: Factory);
        Attach(arena);
        RoomCombatSession s = arena.Session;
        Run(s, 700, (0, SkillKeys.A));
        Assert.Equal((1, 1), (s.SkillCasts, s.HeroObjectsSpawned));    // 一次挥击只放一个(动画过了第 3 帧之后每一步都不重放)
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);
        s.Skills.Advance(3000);                                        // 冷却(2500 毫秒)走完再放一次
        Run(s, 700, (0, SkillKeys.A));
        Assert.Equal((2, 2), (s.SkillCasts, s.HeroObjectsSpawned));
    }

    [Fact]
    public void WithoutAnActLibraryOrWithAMissingObjectNothingIsPlacedAndTheBodyStillDoesNotHit()
    {
        // 没接 .act 程序库:拿不到对象模板,不放;主角身体也不命中
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, skillsFactory: Factory);
        RoomCombatSession s = arena.Session;
        Run(s, 500, (0, SkillKeys.A));
        Assert.Equal((1, 0, 0, 0), (s.SkillCasts, s.HeroObjectsSpawned, s.Hits, s.HeroObjects.Count));

        // 对象编号读不到 .obj:同样不放(也不报错)
        var recipes = new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.WaveSkill,
                new[] { new SkillStageSpec(motionIndex: 4, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0, castHoldFrames: 3) },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1,
                objects: new[] { new SkillObjectSpec(7777777, 0, 3, damageRateColumn: 2, damageFixedColumn: 0) }),
        });
        using var arena2 = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, skillsFactory: data => Load(data, 2, recipes));
        Attach(arena2);
        Run(arena2.Session, 500, (0, SkillKeys.A));
        Assert.Equal((1, 0, 0), (arena2.Session.SkillCasts, arena2.Session.HeroObjectsSpawned, arena2.Session.Hits));
    }

    [Fact]
    public void ObjectsKillMonstersAndCountTheKillLikeAnyHeroHit()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100), (380, 100) }, hp: 1, frozen: true, chr: AvatarFixture.ActionChrPath, skillsFactory: Factory);
        Attach(arena);
        RoomCombatSession s = arena.Session;
        Run(s, 400, (0, SkillKeys.A));
        Assert.Equal((2, 2, 2), (s.HeroObjectHits, s.Hits, s.Kills));
        Assert.True(s.TotalDamage > 0);
    }

    [Fact]
    public void ADeadMonsterAndAHitMonsterThatLeftTheBoxesAreNotHitAgainByLaterFrames()
    {
        // 怪物 hp 极小:第一次命中就死,对象第 1 帧的攻击框不会再打到它(死了不算)
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 1, frozen: true, chr: AvatarFixture.ActionChrPath, skillsFactory: Factory);
        Attach(arena);
        RoomCombatSession s = arena.Session;
        Run(s, 600, (0, SkillKeys.A));
        Assert.Equal((1, 1, 1), (s.HeroObjectHits, s.Hits, s.Kills));
    }

    // ------------------------------------------------------------------ 特效

    [Fact]
    public void TheCastEffectAppearsWhenTheHoldIsReleasedAndFollowsTheHero()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, chr: AvatarFixture.ActionChrPath, monsterSpeed: 0, skillsFactory: Factory);
        Attach(arena);
        RoomCombatSession s = arena.Session;
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Run(s, 130);                                                   // 140 毫秒:蓄力中,没有特效
        Assert.Empty(s.Effects);
        Run(s, 20);                                                    // 160 毫秒:放行(150 毫秒),特效挂上
        Assert.Equal(new[] { AvatarFixture.WaveEffect }, s.Effects.Select(e => e.Path).ToArray());
        AnimatedEffect effect = s.Effects[0];
        Run(s, 40);
        Assert.Equal((s.Hero.X, s.Hero.Y), (effect.X, effect.Y));      // 滑行中特效跟着主角走
        Assert.Single(s.Effects);
    }

    [Fact]
    public void TheObjectIsDrawnWhileItLivesAndItsBoxesAreMirroredByFacing()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, skillsFactory: Factory);
        Attach(arena);
        RoomCombatSession s = arena.Session;
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        for (int i = 0; i < 40 && s.HeroObjectsSpawned == 0; i++)
        {
            s.Update(10, 0, 0, false, false);
        }
        HeroObject o = Assert.Single(s.HeroObjects);
        Assert.Equal(AvatarFixture.WaveObjAni, o.Template.Basic.AniPath);
        Assert.NotNull(o.Sprite(arena.Assets, s.Room.Projection));
        Assert.Contains(s.Build(0), sp => sp.AnimationPath == AvatarFixture.WaveObjAni);   // 场景的精灵表里有对象这一张
        Run(s, 250);
        Assert.Empty(s.HeroObjects);
        Assert.DoesNotContain(s.Build(0), sp => sp.AnimationPath == AvatarFixture.WaveObjAni);   // 消失后没有了
    }

    // ------------------------------------------------------------------ 对象本身

    [Fact]
    public void AnObjectEndsWhenItsNonLoopingAnimationEndsAndGivesEachFramesBoxesOnce()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        PassiveTemplate template = new ActLibrary(assets, data).Passive(AvatarFixture.WaveObjectId);
        Assert.NotNull(template);
        var o = new HeroObject(template, new HeroStrikeInfo(0, 0, false), AvatarFixture.WaveSkill, 100, 50, 0, false);

        // 第 0 帧的框一开始就给(跳过了的帧一起补上),同一帧不给第二次;对象 x 从 +20 起宽 120:世界 x 为 120 到 240
        List<Aabb> first = o.TakeAttackBoxes();
        Assert.Single(first);
        Assert.Equal((120, 240), (first[0].MinX, first[0].MaxX));
        Assert.Empty(o.TakeAttackBoxes());
        o.Update(30);
        Assert.Empty(o.TakeAttackBoxes());                             // 还在第 0 帧
        o.Update(30);                                                  // 60 毫秒:第 1 帧
        Assert.Single(o.TakeAttackBoxes());
        o.Update(200);                                                 // 一步跨过第 2、3 帧(没有框)并播完
        Assert.True(o.Finished);
        Assert.Empty(o.TakeAttackBoxes());
        Assert.Equal(3, o.CurrentFrame);

        // 一大步跨过有框的几帧:跳过的帧的框一起给
        var big = new HeroObject(template, new HeroStrikeInfo(0, 0, false), AvatarFixture.WaveSkill, 100, 50, 0, true);
        big.Update(120);                                               // 直接到第 2 帧:第 0、1 帧的框都还没取过
        List<Aabb> both = big.TakeAttackBoxes();
        Assert.Equal(2, both.Count);
        Assert.True(both.All(b => b.MaxX <= 100 - 20 + 1 && b.MinX >= 100 - 140 - 1));   // 朝左镜像
        Assert.Empty(big.TakeAttackBoxes());
    }

    [Fact]
    public void AnObjectRemembersWhomItHit()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        PassiveTemplate template = new ActLibrary(assets, data).Passive(AvatarFixture.WaveObjectId);
        var o = new HeroObject(template, new HeroStrikeInfo(0, 0, false), 1, 0, 0, 0, false);
        Assert.False(o.CanHit((MonsterActor)null));
        Assert.False(o.CanHit((BreakableObject)null));
        Assert.Equal(0, o.MonstersHit);
        Assert.Throws<ArgumentNullException>(() => new HeroObject(null, new HeroStrikeInfo(0, 0, false), 1, 0, 0, 0, false));
    }
}
