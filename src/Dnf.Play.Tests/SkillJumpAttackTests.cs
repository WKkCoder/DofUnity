using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 空中攻击强化类技能(M56,证据 E-150)在房间会话里的行为:指令是单独一个攻击键,所以不靠技能键,而是在空中按攻击键、普通的空中攻击要开始的那一刻接管;
/// 接管后扣蓝、进冷却、两种动作交替,伤害换成技能的百分比、其余沿用空中攻击的攻击信息,动画播到特效帧放一次预载特效。
/// 数据是合成的(<see cref="AvatarFixture.JumpSkill"/>,不放进快捷栏,除非测试另说):1 级与 2 级的百分比 220 与 250,耗蓝 9,冷却 4000 毫秒(比一跳长,所以连跳两次时每次之间要等冷却);
/// 第一种动作(四帧 × 50 毫秒)的攻击框在第 2 帧、身前 +20 到 +60,第二种动作(三帧 × 60 毫秒)的框在第 1 帧、身前 +100 到 +140;怪物受击框是 −10 到 +30,
/// 所以站在身前 +40 的怪只被第一种打中,站在 +120 的只被第二种打中。
/// </summary>
public class SkillJumpAttackTests
{
    private const double HeroX = 300;
    private const int ArenaY = 100;
    internal const int JumpSkillId = AvatarFixture.JumpSkill;
    private const int Skill = JumpSkillId;
    private const int MonsterHp = 100000;

    /// <param name="skillId">配方挂在哪个技能上(默认是指令为单独攻击键的合成技能;换成别的技能就是"指令不是攻击键"的情形)。</param>
    /// <param name="motionA">第一种动作的 [etc motion] 下标(给个读不到的下标就是"动作读不到")。</param>
    private static SkillRecipes Recipes(int skillId = Skill, int motionA = AvatarFixture.JumpSkillAEtcIndex)
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(skillId,
                new[] { new SkillStageSpec(motionIndex: -1, attackInfoIndex: -1, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0) },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                jumpAttack: new SkillJumpAttackSpec(motionA, AvatarFixture.JumpSkillBEtcIndex, effectFrame: 2, effectsA: new[] { 4, 6 }, effectsB: new[] { 5, 7 })),
        });
    }

    private static HeroSkills Load(GameData data, int level = 2, bool quick = false, bool learned = true, int skillId = Skill, int motionA = AvatarFixture.JumpSkillAEtcIndex)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        if (learned)
        {
            levels.Add(new KeyValuePair<int, int>(skillId, level));
        }
        var slots = quick && learned ? new[] { new KeyValuePair<int, int>(0, skillId) } : Array.Empty<KeyValuePair<int, int>>();
        book.Restore(levels, slots, 0, 0);                                      // 默认不放进快捷栏:只靠指令(攻击键)放
        return HeroSkills.Create(data, chr, 0, book, Recipes(skillId, motionA));
    }

    private static RoomCombatTests.Arena NewArena(int[] monsterXs, HeroVitals vitals = null, bool quick = false, bool commands = true, HeroStrikeInfo jump = default,
        int level = 2, bool learned = true, int seed = 1, int skillId = Skill, int motionA = AvatarFixture.JumpSkillAEtcIndex, Func<GameData, HeroSkills> skillsLoad = null)
    {
        var monsters = new List<(int X, int Y)>();
        foreach (int x in monsterXs)
        {
            monsters.Add((x, ArenaY));
        }
        Func<GameData, HeroSkills> factory = skillsLoad ?? (data => Load(data, level, quick, learned, skillId, motionA));
        var arena = new RoomCombatTests.Arena(monsters, hp: MonsterHp, chr: AvatarFixture.ActionChrPath, monsterSpeed: 0, vitals: vitals, jump: jump, seed: seed,
            skillsFactory: factory);
        arena.Session.CommandsEnabled = commands;
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

    /// <summary>这一步按下攻击键。</summary>
    private static void Attack(RoomCombatSession s)
    {
        s.Update(10, 0, 0, true, false, SkillKeys.None);
    }

    /// <summary>推进到主角落地回到站立(最多 4 秒)。</summary>
    private static void RunToRest(RoomCombatSession s)
    {
        for (int i = 0; i < 400 && s.Hero.Motion != HeroMotion.Rest; i++)
        {
            Step(s);
        }
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);
    }

    /// <summary>站着等到技能冷却好(最多 10 秒)。</summary>
    private static void WaitReady(RoomCombatSession s, HeroSkillSlot slot)
    {
        for (int i = 0; i < 1000 && !slot.Ready; i++)
        {
            Step(s);
        }
        Assert.True(slot.Ready);
    }

    // ------------------------------------------------------------------ 槽与配方表

    [Fact]
    public void ASlotCarriesTheRecipeTheLevelDamageAndTheNinePreloadedEffects()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 只靠指令放的槽(不在快捷栏):按技能号找得到,按键找不到
        HeroSkills skills = Load(data, 2);
        Assert.Null(skills.SlotOf(SkillKeys.A));
        HeroSkillSlot slot = skills.SlotOfSkill(Skill);
        Assert.NotNull(slot);
        Assert.Equal((Skill, 2, AvatarFixture.JumpSkillCoolMs, AvatarFixture.JumpSkillMp), (slot.SkillId, slot.Level, slot.CooldownMs, slot.ConsumeMp));
        SkillJumpAttackSpec spec = slot.Recipe.JumpAttack;
        Assert.NotNull(spec);
        Assert.Equal((AvatarFixture.JumpSkillAEtcIndex, AvatarFixture.JumpSkillBEtcIndex, 2), (spec.MotionIndexA, spec.MotionIndexB, spec.EffectFrame));
        Assert.Equal(new[] { 4, 6 }, spec.EffectsA.ToArray());
        Assert.Equal(new[] { 5, 7 }, spec.EffectsB.ToArray());

        // 预载了九个特效,下标 4 到 7 是四个不同的文件
        Assert.Equal(9, slot.StageEffects.Count);
        Assert.Equal(new[] { AvatarFixture.ChainEffect1, AvatarFixture.ChainEffect2, AvatarFixture.WaveEffect, AvatarFixture.WaveEffect2 },
            slot.StageEffects.Skip(4).Take(4).ToArray());

        // 只有一个等级表的列:第 0 列是百分比,没有固定值列
        Assert.Equal((AvatarFixture.JumpSkillRate2, 0), (slot.Def.GetLevel(2, 0, false), slot.Def.GetLevel(2, -1, false)));
        Assert.Equal(AvatarFixture.JumpSkillRate1, Load(data, 1).SlotOfSkill(Skill).Def.GetLevel(1, 0, false));

        // 放进快捷栏的槽也找得到,按键与按技能号都行
        HeroSkills quick = Load(data, 2, quick: true);
        Assert.Same(quick.SlotOf(SkillKeys.A), quick.SlotOfSkill(Skill));
        Assert.Same(quick.SlotOfSkill(Skill), quick.AttackKeyJumpSkill());
        Assert.Same(skills.SlotOfSkill(Skill), skills.AttackKeyJumpSkill());
        Assert.Null(Load(data, 2, learned: false).AttackKeyJumpSkill());

        // 配方挂在指令不是单独攻击键的技能上:攻击键不接管它
        Assert.Null(Load(data, 2, quick: true, skillId: AvatarFixture.DiveSkill).AttackKeyJumpSkill());
    }

    [Fact]
    public void TheDefaultTableHasTheJumpAttackRecipeWithTheEngineConstantsAndItExcludesHopAndDive()
    {
        SkillRecipe recipe = SkillRecipes.Default.All.Single(r => r.JumpAttack != null);
        SkillJumpAttackSpec spec = recipe.JumpAttack;
        Assert.Equal((23, 24, 2), (spec.MotionIndexA, spec.MotionIndexB, spec.EffectFrame));
        Assert.Equal(new[] { 4, 6 }, spec.EffectsA.ToArray());
        Assert.Equal(new[] { 5, 7 }, spec.EffectsB.ToArray());
        Assert.Null(recipe.Hop);
        Assert.Null(recipe.Dive);
        SkillStageSpec stage = Assert.Single(recipe.Stages);
        Assert.Equal((0, -1), (stage.DamageRateColumn, stage.DamageFixedColumn));
        Assert.False(stage.HasOwnStrike);
        Assert.True(HeroSkills.IsImplemented(recipe.SkillId));

        // 一个配方不能同时是别的特别类型
        Assert.Throws<ArgumentException>(() => new SkillRecipe(1, recipe.Stages, -1, -1, -1, -1, int.MaxValue,
            hop: new SkillHopSpec(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 2, 4, 3, 5, 4), jumpAttack: spec));
        Assert.Throws<ArgumentException>(() => new SkillRecipe(1, recipe.Stages, -1, -1, -1, -1, int.MaxValue,
            dive: new SkillDiveSpec(200, 470, -900, 0, 3), jumpAttack: spec));
    }

    // ------------------------------------------------------------------ 起手(攻击键接管)

    [Fact]
    public void TheAttackKeyInTheAirTakesOverSpendsManaAndTheTwoMotionsAlternateAcrossJumps()
    {
        using var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, 500, 0, 0, 0));
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        HeroSkillSlot slot = s.Skills.SlotOfSkill(Skill);
        Assert.Equal(-1, s.Skills.JumpAttackMode);

        // 地面上按攻击键:普通攻击,什么都不扣
        Attack(s);
        Assert.Equal((HeroMotion.Attack, 0, 0, 500, -1), (hero.Motion, s.SkillCasts, s.JumpSkillCasts, s.Vitals.Mp, s.Skills.JumpAttackMode));
        RunToRest(s);

        // 每一跳放一次:第一种、第二种、第一种、第二种;每次扣 9 点魔法值,冷却开始计;交替模式记着上一次放的种类
        string[] motions = { AvatarFixture.JumpSkillAAni, AvatarFixture.JumpSkillBAni, AvatarFixture.JumpSkillAAni, AvatarFixture.JumpSkillBAni };
        int mp = 500;
        for (int round = 0; round < 4; round++)
        {
            WaitReady(s, slot);
            JumpUp(s, 30);
            Attack(s);
            mp -= AvatarFixture.JumpSkillMp;
            Assert.Equal((HeroMotion.JumpAttack, Skill, motions[round], mp), (hero.Motion, hero.JumpAttackSkillId, hero.CurrentAnimationPath, s.Vitals.Mp));
            Assert.Equal((round + 1, round + 1, round % 2), (s.SkillCasts, s.JumpSkillCasts, s.Skills.JumpAttackMode));
            Assert.True(slot.CooldownLeftMs > AvatarFixture.JumpSkillCoolMs - 100 && slot.CooldownLeftMs <= AvatarFixture.JumpSkillCoolMs);

            // 这一跳不能再放:挥完之后空中再按攻击键没有任何反应,魔法值不动
            for (int i = 0; i < 30 && hero.Motion == HeroMotion.JumpAttack; i++)
            {
                Step(s);
            }
            Assert.Equal(HeroMotion.Jump, hero.Motion);
            Attack(s);
            Assert.Equal((HeroMotion.Jump, round + 1, mp), (hero.Motion, s.SkillCasts, s.Vitals.Mp));
            RunToRest(s);
        }
    }

    [Fact]
    public void WithoutTheSkillTheCommandSwitchEnoughManaOrAReadableMotionTheAttackKeyInTheAirIsThePlainJumpAttack()
    {
        // 魔法值差 1 点:普通的空中攻击,什么都不扣、模式不变、不进冷却
        using (var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, AvatarFixture.JumpSkillMp - 1, 0, 0, 0)))
        {
            RoomCombatSession s = arena.Session;
            JumpUp(s, 30);
            Attack(s);
            Assert.Equal((HeroMotion.JumpAttack, 0, AvatarFixture.JumpAttackAni), (s.Hero.Motion, s.Hero.JumpAttackSkillId, s.Hero.CurrentAnimationPath));
            Assert.Equal((0, 0, AvatarFixture.JumpSkillMp - 1, -1), (s.SkillCasts, s.JumpSkillCasts, s.Vitals.Mp, s.Skills.JumpAttackMode));
            Assert.True(s.Skills.SlotOfSkill(Skill).Ready);
        }

        // 刚好够:接管,魔法值扣到 0
        using (var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, AvatarFixture.JumpSkillMp, 0, 0, 0)))
        {
            RoomCombatSession s = arena.Session;
            JumpUp(s, 30);
            Attack(s);
            Assert.Equal((HeroMotion.JumpAttack, Skill, 1, 0), (s.Hero.Motion, s.Hero.JumpAttackSkillId, s.JumpSkillCasts, s.Vitals.Mp));
        }

        // 没打开出招指令(自动机与扫描默认):普通的空中攻击
        using (var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, 500, 0, 0, 0), commands: false))
        {
            RoomCombatSession s = arena.Session;
            JumpUp(s, 30);
            Attack(s);
            Assert.Equal((HeroMotion.JumpAttack, 0, 0, 500), (s.Hero.Motion, s.Hero.JumpAttackSkillId, s.SkillCasts, s.Vitals.Mp));
        }

        // 没学过:普通的空中攻击
        using (var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, 500, 0, 0, 0), learned: false))
        {
            RoomCombatSession s = arena.Session;
            Assert.Null(s.Skills.SlotOfSkill(Skill));
            JumpUp(s, 30);
            Attack(s);
            Assert.Equal((HeroMotion.JumpAttack, 0, 0, 500), (s.Hero.Motion, s.Hero.JumpAttackSkillId, s.SkillCasts, s.Vitals.Mp));
        }

        // 要播的动作读不到:不接管,不扣蓝、不进冷却、不翻交替模式(第一种动作的下标越界)
        using (var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, 500, 0, 0, 0), motionA: 99))
        {
            RoomCombatSession s = arena.Session;
            JumpUp(s, 30);
            Attack(s);
            Assert.Equal((HeroMotion.JumpAttack, 0, AvatarFixture.JumpAttackAni), (s.Hero.Motion, s.Hero.JumpAttackSkillId, s.Hero.CurrentAnimationPath));
            Assert.Equal((0, 500, -1, true), (s.SkillCasts, s.Vitals.Mp, s.Skills.JumpAttackMode, s.Skills.SlotOfSkill(Skill).Ready));
        }

        // 没有生命魔法值面板(占位伤害、不计耗蓝):照样接管,只是没有可扣的
        using (var arena = NewArena(new[] { 900 }))
        {
            RoomCombatSession s = arena.Session;
            Assert.Null(s.Vitals);
            JumpUp(s, 30);
            Attack(s);
            Assert.Equal((HeroMotion.JumpAttack, Skill, 1), (s.Hero.Motion, s.Hero.JumpAttackSkillId, s.SkillCasts));
        }
    }

    [Fact]
    public void TheCooldownBlocksTheTakeoverAndTheQuickKeyUntilItIsOver()
    {
        using var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, 500, 0, 0, 0), quick: true);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        HeroSkillSlot slot = s.Skills.SlotOf(SkillKeys.A);

        JumpUp(s, 30);
        Attack(s);
        Assert.Equal((Skill, 491, 0), (hero.JumpAttackSkillId, s.Vitals.Mp, s.Skills.JumpAttackMode));
        Assert.False(slot.Ready);
        RunToRest(s);

        // 冷却还没好的下一跳:攻击键是普通的空中攻击(不扣蓝、不翻模式),快捷键放不出来(冷却中)
        JumpUp(s, 30);
        Assert.Equal(SkillCastResult.Cooldown, s.Skills.Cast(slot, hero, s.Vitals));
        Attack(s);
        Assert.Equal((HeroMotion.JumpAttack, 0, AvatarFixture.JumpAttackAni), (hero.Motion, hero.JumpAttackSkillId, hero.CurrentAnimationPath));
        Assert.Equal((1, 491, 0), (s.SkillCasts, s.Vitals.Mp, s.Skills.JumpAttackMode));
        RunToRest(s);

        // 冷却好了再跳:又接管,放第二种
        WaitReady(s, slot);
        JumpUp(s, 30);
        Attack(s);
        Assert.Equal((AvatarFixture.JumpSkillBAni, 2, 482, 1), (hero.CurrentAnimationPath, s.SkillCasts, s.Vitals.Mp, s.Skills.JumpAttackMode));
    }

    [Fact]
    public void ASkillWhoseCommandIsNotTheAttackKeyIsOnlyCastFromItsQuickSlot()
    {
        // 配方挂在指令是"技能键"的技能上(放在快捷栏 A 格):空中按攻击键不接管,按快捷键才放
        using var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, 500, 0, 0, 0), quick: true, skillId: AvatarFixture.DiveSkill);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        Assert.Null(s.Skills.AttackKeyJumpSkill());

        JumpUp(s, 30);
        Attack(s);
        Assert.Equal((HeroMotion.JumpAttack, 0, 0, 500), (hero.Motion, hero.JumpAttackSkillId, s.SkillCasts, s.Vitals.Mp));
        RunToRest(s);

        JumpUp(s, 30);
        Step(s, SkillKeys.A);
        Assert.Equal((HeroMotion.JumpAttack, AvatarFixture.DiveSkill, AvatarFixture.JumpSkillAAni, 1), (hero.Motion, hero.JumpAttackSkillId, hero.CurrentAnimationPath, s.SkillCasts));
    }

    [Fact]
    public void TheQuickKeyCastsTheSkillOnlyInTheAirWithTheSameAlternationAndTheAttackKeyStillTakesOverFromTheQuickSlot()
    {
        using var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, 500, 0, 0, 0), quick: true);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        HeroSkillSlot slot = s.Skills.SlotOf(SkillKeys.A);

        // 地面上:放不出来,不扣蓝、不进冷却、不翻交替模式
        Assert.Equal(SkillCastResult.Busy, s.Skills.Cast(slot, hero, s.Vitals));
        Step(s, SkillKeys.A);
        Assert.Equal((0, HeroMotion.Rest, 500, -1), (s.SkillCasts, hero.Motion, s.Vitals.Mp, s.Skills.JumpAttackMode));
        Assert.True(slot.Ready);

        // 第一跳用攻击键:技能在快捷栏里也由攻击键接管,放第一种;同一跳再按快捷键放不出来(冷却中)
        JumpUp(s, 30);
        Attack(s);
        Assert.Equal((AvatarFixture.JumpSkillAAni, 1, 1, 491, 0), (hero.CurrentAnimationPath, s.SkillCasts, s.JumpSkillCasts, s.Vitals.Mp, s.Skills.JumpAttackMode));
        Assert.False(slot.Ready);
        for (int i = 0; i < 30 && hero.Motion == HeroMotion.JumpAttack; i++)
        {
            Step(s);
        }
        Assert.Equal(HeroMotion.Jump, hero.Motion);
        Assert.Equal(SkillCastResult.Cooldown, s.Skills.Cast(slot, hero, s.Vitals));
        Assert.Equal((1, 491), (s.SkillCasts, s.Vitals.Mp));
        RunToRest(s);

        // 第二跳(冷却好了)用快捷键:放第二种(交替模式是共用的),扣蓝、进冷却;快捷键放的不计 JumpSkillCasts
        WaitReady(s, slot);
        JumpUp(s, 30);
        Step(s, SkillKeys.A);
        Assert.Equal((HeroMotion.JumpAttack, Skill, AvatarFixture.JumpSkillBAni), (hero.Motion, hero.JumpAttackSkillId, hero.CurrentAnimationPath));
        Assert.Equal((2, 1, 482, 1), (s.SkillCasts, s.JumpSkillCasts, s.Vitals.Mp, s.Skills.JumpAttackMode));
        Assert.False(slot.Ready);

        // 同一跳挥完之后再按攻击键:这一跳的空中攻击用掉了,什么也不发生
        for (int i = 0; i < 30 && hero.Motion == HeroMotion.JumpAttack; i++)
        {
            Step(s);
        }
        Assert.Equal(HeroMotion.Jump, hero.Motion);
        Attack(s);
        Assert.Equal((HeroMotion.Jump, 2, 482), (hero.Motion, s.SkillCasts, s.Vitals.Mp));
        RunToRest(s);

        // 第三跳(冷却好了)再用快捷键:模式从第二种翻回第一种
        WaitReady(s, slot);
        JumpUp(s, 30);
        Step(s, SkillKeys.A);
        Assert.Equal((AvatarFixture.JumpSkillAAni, 3, 473, 0), (hero.CurrentAnimationPath, s.SkillCasts, s.Vitals.Mp, s.Skills.JumpAttackMode));
    }

    [Fact]
    public void TheQuickKeyIsBusyWhenTheJumpAlreadyUsedItsAirAttackEvenWithTheCooldownOver()
    {
        // 魔法值够、冷却好,但这一跳已经用掉了空中攻击(普通的一击):快捷键也放不出来(Busy),不扣蓝、不进冷却
        using var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, 500, 0, 0, 0), quick: true, commands: false);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        HeroSkillSlot slot = s.Skills.SlotOf(SkillKeys.A);
        JumpUp(s, 30);
        Attack(s);                                                                    // 出招指令关着:普通的空中攻击
        Assert.Equal((HeroMotion.JumpAttack, 0), (hero.Motion, hero.JumpAttackSkillId));
        for (int i = 0; i < 30 && hero.Motion == HeroMotion.JumpAttack; i++)
        {
            Step(s);
        }
        Assert.Equal(HeroMotion.Jump, hero.Motion);
        Assert.Equal(SkillCastResult.Busy, s.Skills.Cast(slot, hero, s.Vitals));
        Assert.Equal((0, 500, true, -1), (s.SkillCasts, s.Vitals.Mp, slot.Ready, s.Skills.JumpAttackMode));
    }

    [Fact]
    public void TheAlternationModeTravelsWithTheCooldownsToTheNextRoom()
    {
        using var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, 500, 0, 0, 0));
        RoomCombatSession s = arena.Session;
        JumpUp(s, 30);
        Attack(s);
        Assert.Equal(0, s.Skills.JumpAttackMode);

        HeroSkills next = Load(new GameData(new PvfScriptSource(arena.Assets.Pvf)));
        Assert.Equal(-1, next.JumpAttackMode);
        next.CopyCooldownsFrom(s.Skills);
        Assert.Equal(0, next.JumpAttackMode);
        Assert.False(next.SlotOfSkill(Skill).Ready);                                  // 冷却也带过去
        next.CopyCooldownsFrom(next);                                                // 拷自己什么也不做
        Assert.Equal(0, next.JumpAttackMode);
    }

    // ------------------------------------------------------------------ 命中与伤害

    [Fact]
    public void TheSwingUsesTheLevelRateWithTheJumpAttackInfoAndTheTwoMotionsReachDifferentMonsters()
    {
        // 空中攻击自己的攻击信息:加成 17、不击退、不击倒(不击退让怪物原地不动,两次挥击看得出是各打各的)
        var jump = new HeroStrikeInfo(17, 0, false);
        using var arena = NewArena(new[] { (int)HeroX + 40, (int)HeroX + 120 }, jump: jump);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        MonsterActor near = s.Monsters[0];
        MonsterActor far = s.Monsters[1];

        // 第一跳:第一种动作,框在身前 +20 到 +60,只打得到 +40 的怪;打到的那一步,这一击的攻击信息是技能的百分比、空中攻击的其余字段
        JumpUp(s, 30);
        Attack(s);
        HeroStrikeInfo hitStrike = default;
        for (int i = 0; i < 30 && hero.Motion == HeroMotion.JumpAttack; i++)
        {
            int before = s.Hits;
            Step(s);
            if (s.Hits > before)
            {
                hitStrike = s.CurrentHeroStrike;
            }
        }
        Assert.Equal((1, true), (s.Hits, near.Hp < MonsterHp));
        Assert.Equal(MonsterHp, far.Hp);
        Assert.Equal((true, AvatarFixture.JumpSkillRate2, 0, 17), (hitStrike.IsSkillDamage, hitStrike.SkillRatePercent, hitStrike.SkillFixed, hitStrike.DamageBonus));
        RunToRest(s);

        // 第二跳(冷却好了):第二种动作,框在身前 +100 到 +140,只打得到 +120 的怪
        WaitReady(s, s.Skills.SlotOfSkill(Skill));
        int nearHp = near.Hp;
        JumpUp(s, 30);
        Attack(s);
        Assert.Equal(AvatarFixture.JumpSkillBAni, hero.CurrentAnimationPath);
        for (int i = 0; i < 30 && hero.Motion == HeroMotion.JumpAttack; i++)
        {
            Step(s);
        }
        Assert.Equal((2, true, nearHp), (s.Hits, far.Hp < MonsterHp, near.Hp));
    }

    [Fact]
    public void ThePlainJumpAttackKeepsItsOwnStrikeAndTheSkillKeepsTheJumpAttacksKnockbackAndKnockdown()
    {
        // 空中攻击的攻击信息:击退 55、击倒;技能这一击只换伤害,击退与击倒沿用(怪物放得远,不会真的打到)
        var jump = new HeroStrikeInfo(17, 55, true);
        using var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, 500, 0, 0, 0), jump: jump);
        RoomCombatSession s = arena.Session;

        JumpUp(s, 30);
        Attack(s);
        HeroStrikeInfo skill = s.CurrentHeroStrike;
        Assert.Equal((true, AvatarFixture.JumpSkillRate2, 0, 17, 55, true), (skill.IsSkillDamage, skill.SkillRatePercent, skill.SkillFixed, skill.DamageBonus, skill.PushAside, skill.KnockDown));
        RunToRest(s);

        // 没魔法值了:普通的空中攻击,攻击信息没有技能伤害
        using var poor = NewArena(new[] { 900 }, new HeroVitals(1, 1000, 0, 0, 0, 0), jump: jump);
        JumpUp(poor.Session, 30);
        Attack(poor.Session);
        HeroStrikeInfo plain = poor.Session.CurrentHeroStrike;
        Assert.Equal((HeroMotion.JumpAttack, 0, false, 17, 55, true),
            (poor.Session.Hero.Motion, poor.Session.Hero.JumpAttackSkillId, plain.IsSkillDamage, plain.DamageBonus, plain.PushAside, plain.KnockDown));
    }

    private static SkillRecipes RecipesWithPassive()
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(Skill,
                new[] { new SkillStageSpec(motionIndex: -1, attackInfoIndex: -1, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0) },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                jumpAttack: new SkillJumpAttackSpec(AvatarFixture.JumpSkillAEtcIndex, AvatarFixture.JumpSkillBEtcIndex, effectFrame: 2, effectsA: new[] { 4, 6 }, effectsB: new[] { 5, 7 }),
                passivePowerLinks: new[] { new SkillPassivePowerLink(902, 0) }),
        });
    }

    internal static HeroSkills LoadWithPassive(GameData data, int passiveLevel, int skillLevel = 2)
    {
        CharacterDef chr = data.GetCharacter(0);
        var defs = SkillCatalog.Load(data, 0).Skills.ToList();
        defs.Add(new SkillDef(902, "skill/test/passive902.skl", new[]
        {
            new ScriptLine("[type]", new[] { new ScriptValue(ScriptValueKind.String, 0, "[passive]") }),
            new ScriptLine("[executable states]", new[] { new ScriptValue(ScriptValueKind.Int, SkillPassivePowerRate.JumpAttackNativeState, null) }),
            new ScriptLine("[static data]", new[] { new ScriptValue(ScriptValueKind.Int, 80, null) }),
        }));
        var catalog = new SkillCatalog(0, defs);
        var book = new SkillBook(catalog);
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(Skill, skillLevel));
        if (passiveLevel > 0)
        {
            levels.Add(new KeyValuePair<int, int>(902, passiveLevel));
        }
        book.Restore(levels, Array.Empty<KeyValuePair<int, int>>(), 0, 0);
        return HeroSkills.Create(data, chr, 0, book, RecipesWithPassive());
    }

    [Fact]
    public void JumpStageStrikeScalesWithPassiveAndJumpStrikeUsesStageStrikes()
    {
        const int PassiveBonus = 80;
        using var fx = new AvatarFixture();
        GameData data = new GameData(new PvfScriptSource(DnfAssets.Open(fx.Root).Pvf));
        HeroSkills withPassive = LoadWithPassive(data, 1, 2);
        HeroSkills withoutPassive = LoadWithPassive(data, 0, 2);
        HeroSkillSlot slotOn = withPassive.SlotOfSkill(Skill);
        HeroSkillSlot slotOff = withoutPassive.SlotOfSkill(Skill);
        int baseRate = AvatarFixture.JumpSkillRate2;
        int baseFixed = 0;
        int expectRate = (int)Math.Round(baseRate * (1f + PassiveBonus * SkillPassivePowerRate.StaticScale), MidpointRounding.AwayFromZero);
        HeroStrikeInfo plain = slotOff.StrikeAt(0).Value;
        HeroStrikeInfo scaled = slotOn.StrikeAtWithPassiveNativeState(0, SkillPassivePowerRate.JumpAttackNativeState).Value;
        Assert.Equal(baseRate, plain.SkillRatePercent);
        Assert.NotEqual(plain.SkillRatePercent, scaled.SkillRatePercent);
        Assert.Equal(expectRate, scaled.SkillRatePercent);
        Assert.Equal(baseFixed, plain.SkillFixed);
        Assert.Equal(baseFixed, scaled.SkillFixed);

        using var arena = NewArena(new[] { (int)HeroX + 40 }, skillsLoad: data => LoadWithPassive(data, 1, 2));
        RoomCombatSession s = arena.Session;
        JumpUp(s, 30);
        Attack(s);
        HeroStrikeInfo hitStrike = default;
        for (int i = 0; i < 30 && s.Hero.Motion == HeroMotion.JumpAttack; i++)
        {
            int before = s.Hits;
            Step(s);
            if (s.Hits > before)
            {
                hitStrike = s.CurrentHeroStrike;
            }
        }
        Assert.Equal(1, s.Hits);
        Assert.True(hitStrike.IsSkillDamage);
        Assert.Equal(expectRate, hitStrike.SkillRatePercent);
    }

    [Fact]
    public void TheDamageFollowsTheSkillLevelsPercentage()
    {
        long Total(int level)
        {
            using var arena = NewArena(new[] { (int)HeroX + 40 }, level: level);
            RoomCombatSession s = arena.Session;
            JumpUp(s, 30);
            Attack(s);
            for (int i = 0; i < 30 && s.Hero.Motion == HeroMotion.JumpAttack; i++)
            {
                Step(s);
            }
            Assert.Equal(1, s.Hits);
            return s.TotalDamage;
        }

        long level1 = Total(1);
        long level2 = Total(2);
        Assert.True(level1 > 0);
        Assert.True(level2 > level1);
    }

    // ------------------------------------------------------------------ 特效

    [Fact]
    public void TheEffectsComeOnceAtTheEffectFrameAndWhichOnesDependOnTheMotion()
    {
        using var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, 500, 0, 0, 0));
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;

        // 第一种:第 2 帧从 100 毫秒起;放出这一步算 10 毫秒,之后每步 10 毫秒。90 毫秒时还没有,100 毫秒这一步放第 4、6 个预载特效
        JumpUp(s, 30);
        int baseline = s.Effects.Count;
        Attack(s);
        for (int i = 0; i < 8; i++)
        {
            Step(s);
        }
        Assert.Equal((1, 0, baseline), (hero.CurrentFrame, s.JumpSkillEffects, s.Effects.Count));
        Step(s);
        Assert.Equal((2, 1, baseline + 2), (hero.CurrentFrame, s.JumpSkillEffects, s.Effects.Count));
        Assert.Equal(new[] { AvatarFixture.ChainEffect1, AvatarFixture.WaveEffect }, s.Effects.Skip(baseline).Select(e => e.Path).ToArray());

        // 特效挂在主角身上跟着走(主角在下落,特效的高度每步跟着变)
        Step(s);
        Assert.All(s.Effects.Skip(baseline).ToArray(), e => Assert.Equal(hero.Z, e.Z));

        // 之后这一击里不再放
        for (int i = 0; i < 30 && hero.Motion == HeroMotion.JumpAttack; i++)
        {
            Step(s);
        }
        Assert.Equal((1, 1), (s.JumpSkillEffects, s.JumpSkillCasts));
        RunToRest(s);

        // 第二种(冷却好了的下一跳):第 2 帧从 120 毫秒起;放第 5、7 个预载特效
        WaitReady(s, s.Skills.SlotOfSkill(Skill));
        JumpUp(s, 30);
        baseline = s.Effects.Count;
        Attack(s);
        for (int i = 0; i < 10; i++)
        {
            Step(s);
        }
        Assert.Equal((AvatarFixture.JumpSkillBAni, 1, 1, baseline), (hero.CurrentAnimationPath, hero.CurrentFrame, s.JumpSkillEffects, s.Effects.Count));
        Step(s);
        Assert.Equal((2, 2, baseline + 2), (hero.CurrentFrame, s.JumpSkillEffects, s.Effects.Count));
        Assert.Equal(new[] { AvatarFixture.ChainEffect2, AvatarFixture.WaveEffect2 }, s.Effects.Skip(baseline).Select(e => e.Path).ToArray());
    }

    [Fact]
    public void ALandingBeforeTheEffectFrameInterruptsTheSwingWithoutAnyEffect()
    {
        using var arena = NewArena(new[] { 900 }, new HeroVitals(1, 1000, 500, 0, 0, 0));
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;

        // 推进到下落、离地不到 25 像素时按攻击键:不到 100 毫秒就落地,第一种动作没播到特效帧
        JumpUp(s, 30);
        double previous = hero.Z;
        for (int i = 0; i < 300; i++)
        {
            Step(s);
            if (hero.Z < previous && hero.Z < 25 && hero.Z > 10)
            {
                break;
            }
            previous = hero.Z;
        }
        Assert.InRange(hero.Z, 10.0, 25.0);
        int baseline = s.Effects.Count;
        Attack(s);
        Assert.Equal((HeroMotion.JumpAttack, Skill), (hero.Motion, hero.JumpAttackSkillId));
        for (int i = 0; i < 12 && hero.Z > 0; i++)
        {
            Step(s);
        }
        Assert.Equal((0.0, 0, 0), (hero.Z, hero.JumpAttackSkillId, s.JumpSkillEffects));
        Assert.True(hero.AttackAnimationMs < 100 || hero.Motion != HeroMotion.JumpAttack);
        Assert.Equal((1, baseline), (s.JumpSkillCasts, s.Effects.Count));        // 已经扣了蓝(放出来了),但这一击的特效没放
        RunToRest(s);
        Assert.Equal(0, s.JumpSkillEffects);
    }
}
