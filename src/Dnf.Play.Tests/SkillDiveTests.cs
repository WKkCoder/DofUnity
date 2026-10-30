using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 腾空悬停后俯冲类技能(M55,证据 E-149)在房间会话里的行为:只在跳跃状态里放(地面上"没放成",不扣蓝、不进冷却),
/// 放出来悬停 200 毫秒后开始俯冲(这一刻放预载特效),落地那一刻按"放出时的离地高度够不够"在脚下放冲击对象(伤害取 [level info] 的另外两列),
/// 命中靠空中攻击动画最后一帧的攻击框(动画定在这一帧,每步都判,同一击同一个怪只打一次;第 1 帧的框从来没播过,不判)。
/// 数据是合成的(<see cref="AvatarFixture.DiveSkill"/>,放在技能槽 A):1 级与 2 级的百分比伤害 150 与 210,冲击对象 (固定值 40、百分比 300) 与 (60、420),
/// 最小离地高度 30,最后一帧的攻击框在身前 +100 到 +140。怪物受击框是 −10 到 +30,所以站在主角身前 +120 的怪物吃得到定住帧的框,站在 +40 的只在第 1 帧的框里。
/// </summary>
public class SkillDiveTests
{
    private const double HeroX = 300;
    private const int ArenaY = 100;

    private static SkillRecipes Recipes(bool withObject = true)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.DiveSkill,
                new[] { new SkillStageSpec(motionIndex: -1, attackInfoIndex: 5, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0) },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                objects: withObject
                    ? new[] { new SkillObjectSpec(AvatarFixture.WaveObjectId, 0, 0, damageRateColumn: 2, damageFixedColumn: 1, onLanding: true) }
                    : null,
                dive: new SkillDiveSpec(hoverMs: 200, speedX: 470, speedZ: -900, effectIndex: 0, impactHeightIndex: 3)),
        });
    }

    private static HeroSkills Load(GameData data, int level = 2, bool withObject = true)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.DiveSkill, level));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.DiveSkill) }, 0, 0);     // 俯冲技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, Recipes(withObject));
    }

    private static RoomCombatTests.Arena NewArena(int monsterX, HeroVitals vitals = null, bool acts = true, bool withObject = true)
    {
        var arena = new RoomCombatTests.Arena(new[] { (monsterX, ArenaY) }, hp: 100000, chr: AvatarFixture.ActionChrPath, monsterSpeed: 0, vitals: vitals,
            skillsFactory: data => Load(data, 2, withObject));
        if (acts)
        {
            arena.Session.AttachActs(new ActLibrary(arena.Assets, new GameData(new PvfScriptSource(arena.Assets.Pvf))));
        }
        return arena;
    }

    /// <summary>按下跳跃键,推进到主角腾空并且离地至少 <paramref name="minZ"/> 像素。</summary>
    private static void JumpUp(RoomCombatSession s, double minZ)
    {
        s.Update(10, 0, 0, false, true, SkillKeys.None);
        for (int i = 0; i < 300 && !(s.Hero.Motion == HeroMotion.Jump && s.Hero.Z >= minZ); i++)
        {
            s.Update(10, 0, 0, false, false, SkillKeys.None);
        }
        Assert.True(s.Hero.Z >= minZ);
    }

    private static void Step(RoomCombatSession s, SkillKeys pressed = SkillKeys.None)
    {
        s.Update(10, 0, 0, false, false, pressed);
    }

    // ------------------------------------------------------------------ 槽与配方表

    [Fact]
    public void ASlotReadsTheDiveStrikeTheLandingObjectStrikeAndTheEffect()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkillSlot slot = Load(data, 2).SlotOf(SkillKeys.A);

        Assert.Equal((AvatarFixture.DiveSkill, 2, 2200, 11), (slot.SkillId, slot.Level, slot.CooldownMs, slot.ConsumeMp));
        Assert.NotNull(slot.Recipe?.Dive);
        Assert.Equal((200, 470, -900, 0, 3), (slot.Recipe.Dive.HoverMs, slot.Recipe.Dive.SpeedX, slot.Recipe.Dive.SpeedZ, slot.Recipe.Dive.EffectIndex, slot.Recipe.Dive.ImpactHeightIndex));

        // 俯冲本身:[level info] 第 0 列的百分比,没有固定值,攻击信息取 [etc attack info] 第 5 项(击退、不击倒)
        HeroStrikeInfo dive = slot.StageStrikes[0];
        Assert.Equal((true, AvatarFixture.DiveRate2, 0, AvatarFixture.DivePushAside, false), (dive.IsSkillDamage, dive.SkillRatePercent, dive.SkillFixed, dive.PushAside, dive.KnockDown));
        // 冲击对象:固定值取第 1 列、百分比取第 2 列(与俯冲本身不同的两列)
        Assert.Single(slot.ObjectStrikes);
        HeroStrikeInfo impact = slot.ObjectStrikes[0];
        Assert.Equal((true, AvatarFixture.DiveImpactRate2, AvatarFixture.DiveImpactFixed2, AvatarFixture.WavePushAside, true),
            (impact.IsSkillDamage, impact.SkillRatePercent, impact.SkillFixed, impact.PushAside, impact.KnockDown));
        Assert.True(slot.Recipe.Objects[0].OnLanding);
        Assert.Equal(new[] { AvatarFixture.ChainEffect1 }, slot.StageEffects.ToArray());

        HeroSkillSlot first = Load(data, 1).SlotOf(SkillKeys.A);
        Assert.Equal((AvatarFixture.DiveRate1, AvatarFixture.DiveImpactRate1, AvatarFixture.DiveImpactFixed1),
            (first.StageStrikes[0].SkillRatePercent, first.ObjectStrikes[0].SkillRatePercent, first.ObjectStrikes[0].SkillFixed));
    }

    [Fact]
    public void TheDefaultTableHasTheDiveRecipeWithTheEngineConstants()
    {
        SkillRecipe dive = SkillRecipes.Default.Find(16);
        Assert.NotNull(dive?.Dive);
        Assert.Equal((200, 470, -900), (dive.Dive.HoverMs, dive.Dive.SpeedX, dive.Dive.SpeedZ));
        Assert.Null(dive.Hop);
        Assert.Single(dive.Stages);
        Assert.True(dive.Stages[0].HasOwnStrike);
        SkillObjectSpec impact = Assert.Single(dive.Objects);
        Assert.True(impact.OnLanding);
        Assert.True(HeroSkills.IsImplemented(16));
        // 一个配方不能既是蓄力跳跃又是悬停俯冲
        Assert.Throws<ArgumentException>(() => new SkillRecipe(1, new[] { dive.Stages[0] }, -1, -1, -1, -1, int.MaxValue,
            hop: new SkillHopSpec(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 2, 4, 3, 5, 4), dive: new SkillDiveSpec(200, 470, -900, 0, 3)));
    }

    // ------------------------------------------------------------------ 起手

    [Fact]
    public void OnTheGroundTheDiveIsBusyAndCostsNothingButInTheAirItDivesSpendsManaAndStartsTheCooldown()
    {
        using var arena = NewArena(900, new HeroVitals(1, 1000, 500, 0, 0, 0));
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        HeroSkillSlot slot = s.Skills.SlotOf(SkillKeys.A);

        // 站在地上:放不出来,不扣蓝、不进冷却
        Assert.Equal(SkillCastResult.Busy, s.Skills.Cast(slot, hero, s.Vitals));
        Assert.Equal((500, 0.0), (s.Vitals.Mp, slot.CooldownLeftMs));
        Step(s, SkillKeys.A);
        Assert.Equal((0, HeroMotion.Rest, 500), (s.SkillCasts, hero.Motion, s.Vitals.Mp));

        // 腾空时:放出来,动画定在最后一帧,扣 11 点魔法值、冷却 2200 毫秒
        JumpUp(s, 70);
        Step(s, SkillKeys.A);
        Assert.Equal((1, HeroMotion.Skill, true), (s.SkillCasts, hero.Motion, hero.DiveAirborne));
        Assert.Equal((500 - 11, hero.FrameHeld), (s.Vitals.Mp, true));
        Assert.True(slot.CooldownLeftMs > 2100 && slot.CooldownLeftMs <= 2200);
        Assert.Equal(AvatarFixture.JumpAttackAni, hero.CurrentAnimationPath);

        // 再按一次:冷却中(也不接段),什么都不发生
        Step(s, SkillKeys.A);
        Assert.Equal((1, 0), (s.SkillCasts, s.SkillChains));
    }

    [Fact]
    public void TheCommandSingleSkillKeyDivesInTheAirAndDoesNothingOnTheGround()
    {
        using var arena = NewArena(900, new HeroVitals(1, 1000, 500, 0, 0, 0));
        RoomCombatSession s = arena.Session;
        s.CommandsEnabled = true;
        TownHero hero = s.Hero;
        HeroSkillSlot dive = s.Skills.SlotOf(SkillKeys.A);

        // 地面上按单键技能指令:俯冲排在候选里,但放不出来(没放成不扣蓝、不进冷却),别的技能这里没有单键指令,什么也没放
        Step(s, SkillKeys.CommandSkill);
        Assert.Equal((0, HeroMotion.Rest, 0.0, 500), (s.SkillCasts, hero.Motion, dive.CooldownLeftMs, s.Vitals.Mp));

        // 腾空时同一个键:放的是俯冲
        JumpUp(s, 70);
        Step(s, SkillKeys.CommandSkill);
        Assert.Equal((HeroMotion.Skill, AvatarFixture.DiveSkill, true, 1), (hero.Motion, hero.SkillId, hero.DiveAirborne, s.SkillCasts));
        Assert.True(dive.CooldownLeftMs > 0);
        Assert.Equal(500 - 11, s.Vitals.Mp);
    }

    // ------------------------------------------------------------------ 特效、落地、冲击对象

    [Fact]
    public void TheEffectComesAtTheDiveStartAndTheImpactObjectAtLandingAtTheFeetWithTheLandingDamage()
    {
        using var arena = NewArena(900);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;

        JumpUp(s, 90);
        Step(s, SkillKeys.A);
        int effectsAtCast = s.Effects.Count;
        Assert.Equal((0, 0, 0, 0), (s.DiveStarts, s.DiveLandings, s.DiveImpacts, s.HeroObjectsSpawned));

        // 悬停 190 毫秒:没有特效、没有对象(第 1 步算 10 毫秒,已经走了 1 步)
        for (int i = 0; i < 18; i++)
        {
            Step(s);
        }
        Assert.Equal((0, effectsAtCast, false), (s.DiveStarts, s.Effects.Count, hero.IsDiving));

        // 悬停结束的那一步:开始俯冲,放预载表第 0 个特效(挂在主角身上),此时没有对象
        Step(s);
        Assert.Equal((1, effectsAtCast + 1, true), (s.DiveStarts, s.Effects.Count, hero.IsDiving));
        Assert.Equal((0, 0), (s.DiveImpacts, s.HeroObjectsSpawned));

        // 落地那一步:放一个冲击对象,在主角脚下(没有前方偏移),朝向与主角一致,带冲击的伤害;主角落地进入坐姿
        int steps = 0;
        while (s.DiveLandings == 0 && steps++ < 300)
        {
            Step(s);
        }
        Assert.Equal((1, 1, 1), (s.DiveLandings, s.DiveImpacts, s.HeroObjectsSpawned));
        Assert.Equal((0.0, AvatarFixture.SitAni), (hero.Z, hero.CurrentAnimationPath));
        HeroObject impact = Assert.Single(s.HeroObjects);
        Assert.Equal((hero.X, hero.Y), (impact.X, impact.Y));
        Assert.Equal(0.0, impact.Z);
        Assert.Equal(AvatarFixture.DiveImpactRate2, impact.Strike.SkillRatePercent);
        Assert.Equal(AvatarFixture.DiveImpactFixed2, impact.Strike.SkillFixed);

        // 之后不会再放(计数只在落地那一刻动一次)
        for (int i = 0; i < 60; i++)
        {
            Step(s);
        }
        Assert.Equal((1, 1, 1, 1), (s.DiveStarts, s.DiveLandings, s.DiveImpacts, s.HeroObjectsSpawned));
    }

    [Fact]
    public void ALowCastLandsWhileHoveringWithoutTheEffectOrTheImpactAndAMissingObjectTemplateIsHarmless()
    {
        // 刚离地就放:高度不到 30,悬停期间就落地了:没有俯冲(没有特效),没有冲击对象;落地的计数照样加
        using (var arena = NewArena(900))
        {
            RoomCombatSession s = arena.Session;
            JumpUp(s, 1);
            Assert.True(s.Hero.Z < AvatarFixture.DiveImpactMinHeight);
            Step(s, SkillKeys.A);
            int effects = s.Effects.Count;
            for (int i = 0; i < 100 && s.DiveLandings == 0; i++)
            {
                Step(s);
            }
            Assert.Equal((0, 1, 0, 0), (s.DiveStarts, s.DiveLandings, s.DiveImpacts, s.HeroObjectsSpawned));
            Assert.Equal((effects, 0.0), (s.Effects.Count, s.Hero.Z));
        }

        // 没接上 .act 程序库(拿不到对象模板):照常俯冲落地,只是不放对象,也不计放出
        using (var arena = NewArena(900, acts: false))
        {
            RoomCombatSession s = arena.Session;
            JumpUp(s, 90);
            Step(s, SkillKeys.A);
            for (int i = 0; i < 300 && s.DiveLandings == 0; i++)
            {
                Step(s);
            }
            Assert.Equal((1, 1, 0, 0), (s.DiveStarts, s.DiveLandings, s.DiveImpacts, s.HeroObjectsSpawned));
        }
    }

    // ------------------------------------------------------------------ 命中

    [Fact]
    public void TheHeldFramesBoxHitsAMonsterAheadOnceWithTheDivePercentAndTheFirstFramesBoxIsNeverUsed()
    {
        // 身前 +120 的怪物在定住帧的框里(框 z 从主角离地高度起,高 50;怪物 z 0 到 60,所以主角降到 60 以下才够得着)
        using (var arena = NewArena((int)HeroX + 120, acts: false))
        {
            RoomCombatSession s = arena.Session;
            TownHero hero = s.Hero;
            MonsterActor m = s.Monsters[0];
            JumpUp(s, 80);
            Step(s, SkillKeys.A);
            Assert.Equal((AvatarFixture.DiveRate2, AvatarFixture.DivePushAside), (s.CurrentHeroStrike.SkillRatePercent, s.CurrentHeroStrike.PushAside));
            Assert.Equal(0, s.Hits);                                                  // 刚放出来离地高于 60:框够不着怪物(第一次判定没有命中)

            int hitStep = -1;
            double hitZ = -1;
            bool hitAirborne = false;
            for (int i = 0; i < 300 && hero.Motion == HeroMotion.Skill; i++)
            {
                Step(s);
                if (s.Hits == 1 && hitStep < 0)
                {
                    hitStep = i;
                    hitZ = hero.Z;
                    hitAirborne = hero.DiveAirborne;
                }
            }
            Assert.True(hitStep > 0);
            Assert.True(hitAirborne && hitZ <= 60);                                   // 在空中降到够得着的高度时才打到:定住的帧每步都判,不是只判一次
            Assert.Equal(1, s.Hits);                                                  // 之后一直在框里,同一击只算一次
            Assert.True(m.Hp < 100000);
        }

        // 身前 +40 的怪物只在第 1 帧的框里(+20 到 +60):动画直接定在最后一帧,第 1 帧没播过,整个俯冲都打不到。
        // 放出时主角离地要低于 60(框的高度够得着怪物),否则就算第 1 帧被误判,第一次判定也因为够不着而漏过去,测不出来
        using (var arena = NewArena((int)HeroX + 40, acts: false))
        {
            RoomCombatSession s = arena.Session;
            JumpUp(s, 40);
            Assert.True(s.Hero.Z < 60);
            Step(s, SkillKeys.A);
            for (int i = 0; i < 300 && s.Hero.Motion == HeroMotion.Skill; i++)
            {
                Step(s);
            }
            Assert.Equal((0, 1), (s.Hits, s.DiveLandings));
        }
    }
}
