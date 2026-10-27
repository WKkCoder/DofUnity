using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 多段技能配方(M37,证据 E-129):槽按配方读各段的攻击信息与伤害列、起手按方向键定滑行、同一个键在动画播过指定帧后接下一段、
/// 分段特效按帧放并跟着主角、接段不再扣蓝不再进冷却。数据全是合成的(见 <see cref="AvatarFixture.ChainSkill"/>):
/// 两段动作 400 与 300 毫秒,第一段第 2 帧(200 毫秒起)、第二段第 1 帧(100 毫秒起)有攻击框;[static data] 是 (200, −400, 100, −100);
/// [level info] 四列,2 级是 (180, 300, 40, 60):第一段取第 0 列与第 2 列,第二段取第 1 列与第 3 列;冷却 3000、耗蓝 20。
/// </summary>
public class SkillChainTests
{
    private static SkillRecipes Recipes(int gateForStage1 = 0, int chainFromFrame = 2, bool followHeld = true, bool threeStages = false)
    {
        var stages = new List<SkillStageSpec>
        {
            new SkillStageSpec(motionIndex: 2, attackInfoIndex: 1, damageRateColumn: 0, damageFixedColumn: 2, effectIndex: 0, effectFrame: 1),
            new SkillStageSpec(motionIndex: 3, attackInfoIndex: 2, damageRateColumn: 1, damageFixedColumn: 3, effectIndex: 1, effectFrame: 0, requiresSkill: gateForStage1),
        };
        if (threeStages)
        {
            // 第三段重用第一段的动作与攻击信息(夹具里只有两个动作),伤害列取第 1、3 列,不放特效
            stages.Add(new SkillStageSpec(motionIndex: 2, attackInfoIndex: 1, damageRateColumn: 1, damageFixedColumn: 3, effectIndex: -1, effectFrame: 0));
        }
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill, stages,
                slideSpeedIndex: 0, slideAccelIndex: 1, forwardSpeedIndex: 2, forwardAccelIndex: 3, chainFromFrame: chainFromFrame, slideFollowsHeldDirection: followHeld),
        });
    }

    private static HeroSkills Load(GameData data, int chainLevel = 2, SkillRecipes recipes = null)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ChainSkill, chainLevel));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.ChainSkill) }, 0, 0);   // 多段技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, recipes ?? Recipes());
    }

    private static TownHero NewHero(DnfAssets assets, double x = 100, double y = 50, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), x, y, facingLeft, TownHero.DungeonRestTag);
    }

    private static HeroSkills Factory(GameData data)
    {
        return Load(data);
    }

    // ------------------------------------------------------------------ 槽

    [Fact]
    public void ASlotReadsEachStageFromItsOwnAttackInfoAndDamageColumnsAndTheEffectsFromThePreloadScript()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkillSlot slot = Load(new GameData(new PvfScriptSource(assets.Pvf)), 2).SlotOf(SkillKeys.A);

        Assert.Equal((AvatarFixture.ChainSkill, 2, 3000, 20), (slot.SkillId, slot.Level, slot.CooldownMs, slot.ConsumeMp));
        Assert.NotNull(slot.Recipe);
        Assert.Equal(2, slot.StageStrikes.Count);
        HeroStrikeInfo a = slot.StageStrikes[0];
        HeroStrikeInfo b = slot.StageStrikes[1];
        Assert.Equal((true, 180, 40, AvatarFixture.ChainAPushAside, false), (a.IsSkillDamage, a.SkillRatePercent, a.SkillFixed, a.PushAside, a.KnockDown));
        Assert.Equal((true, 300, 60, AvatarFixture.ChainBPushAside, true), (b.IsSkillDamage, b.SkillRatePercent, b.SkillFixed, b.PushAside, b.KnockDown));
        Assert.Equal(180, slot.StrikeAt(0).Value.SkillRatePercent);
        Assert.Equal(300, slot.StrikeAt(1).Value.SkillRatePercent);
        Assert.Equal(300, slot.StrikeAt(7).Value.SkillRatePercent);          // 段号越界取最后一段
        Assert.Equal(new[] { AvatarFixture.ChainEffect1, AvatarFixture.ChainEffect2 }, slot.StageEffects.ToArray());   // 预载脚本里的顺序与相对路径解析
        Assert.Null(slot.Strike);                                            // 配方技能没有单一的攻击信息

        // 1 级取 [level info] 的第一行
        HeroSkillSlot first = Load(new GameData(new PvfScriptSource(assets.Pvf)), 1).SlotOf(SkillKeys.A);
        Assert.Equal((150, 30), (first.StageStrikes[0].SkillRatePercent, first.StageStrikes[0].SkillFixed));
        Assert.Equal((250, 50), (first.StageStrikes[1].SkillRatePercent, first.StageStrikes[1].SkillFixed));
    }

    [Fact]
    public void WithoutARecipeTheSkillIsNotImplementedAndTheDefaultTableIsWellFormed()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        // 默认配方表里没有合成技能:槽有,但放不出来
        HeroSkills skills = Load(data, 2, SkillRecipes.Default);
        HeroSkillSlot slot = skills.SlotOf(SkillKeys.A);
        Assert.Null(slot.Recipe);
        Assert.False(HeroSkills.IsImplemented(AvatarFixture.ChainSkill));
        TownHero hero = NewHero(assets);
        Assert.Equal(SkillCastResult.NotImplemented, skills.Cast(slot, hero));
        Assert.Equal(HeroMotion.Rest, hero.Motion);

        // 默认表的结构:每个配方至少一段,下标与列号非负,接段帧不小于 1,每一段的特效帧不小于 0;同一个技能只有一份
        Assert.True(SkillRecipes.Default.Count >= 1);
        foreach (SkillRecipe r in SkillRecipes.Default.All)
        {
            Assert.Same(r, SkillRecipes.Default.Find(r.SkillId));
            Assert.True(HeroSkills.IsImplemented(r.SkillId));
            Assert.True(r.Stages.Count >= 1 && r.ChainFromFrame >= 1);
            Assert.All(r.Stages, st =>
            {
                Assert.True(st.MotionIndex >= 0 && st.AttackInfoIndex >= 0 && st.DamageRateColumn >= 0 && st.DamageFixedColumn >= 0);
                Assert.True(st.EffectFrame >= 0 && st.EffectIndex >= -1);
            });
        }
        Assert.Throws<ArgumentException>(() => new SkillRecipe(1, new SkillStageSpec[0], 0, 0, 0, 0, 1));
    }

    // ------------------------------------------------------------------ 起滑

    [Theory]
    [InlineData(0, 0, false, 200.0, -400.0)]     // 没按方向键:基础的两个数
    [InlineData(1, 0, false, 300.0, -500.0)]     // 朝右、按右:加上"顺着按"的加成
    [InlineData(-1, 0, false, 0.0, 0.0)]         // 朝右、按左:不滑
    [InlineData(0, -1, false, 0.0, 0.0)]         // 只按上或下:不滑
    [InlineData(0, 1, false, 0.0, 0.0)]
    [InlineData(1, 1, false, 300.0, -500.0)]     // 斜着按:只看左右(推断)
    [InlineData(-1, -1, false, 0.0, 0.0)]
    [InlineData(-1, 0, true, 300.0, -500.0)]     // 朝左、按左:顺着
    [InlineData(1, 0, true, 0.0, 0.0)]
    [InlineData(0, 0, true, 200.0, -400.0)]
    public void TheSlideOfTheOpeningStageDependsOnTheHeldDirection(int heldX, int heldY, bool facingLeft, double speed, double accel)
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)));
        TownHero hero = NewHero(assets, facingLeft: facingLeft);

        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero, null, heldX, heldY));
        Assert.Equal((HeroMotion.Skill, AvatarFixture.ChainSkill, 0, speed), (hero.Motion, hero.SkillId, hero.SkillStage, hero.SkillSlideSpeed));

        // 第一段 400 毫秒滑完:位移就是这组速度与加速度在这段时间里的距离,朝哪边滑看朝向
        hero.Update(400, 0, 0);
        double distance = TownHero.SlideDistance(speed, accel, 0, 400);
        Assert.Equal(100 + (facingLeft ? -distance : distance), hero.X, 6);
        Assert.Equal(speed == 0.0, distance == 0.0);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, false)]     // 朝右、按右
    [InlineData(-1, 0, false)]    // 朝右、按左:照样滑
    [InlineData(0, 1, false)]
    [InlineData(-1, -1, false)]
    [InlineData(-1, 0, true)]     // 朝左、按左
    [InlineData(1, 0, true)]      // 朝左、按右:照样滑
    [InlineData(0, -1, true)]
    public void ARecipeThatDoesNotReadTheHeldDirectionAlwaysSlidesForwardByTheBaseNumbers(int heldX, int heldY, bool facingLeft)
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)), 2, Recipes(followHeld: false));
        TownHero hero = NewHero(assets, facingLeft: facingLeft);

        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero, null, heldX, heldY));
        Assert.Equal(200.0, hero.SkillSlideSpeed);                        // 基础的两个数(200、-400),没有"顺着按"的加成,也没有"按别的方向不滑"
        hero.Update(400, 0, 0);
        Assert.Equal(100 + (facingLeft ? -1 : 1) * TownHero.SlideDistance(200.0, -400.0, 0, 400), hero.X, 6);

        // 接段同样不看方向键
        HeroSkills again = Load(new GameData(new PvfScriptSource(assets.Pvf)), 2, Recipes(followHeld: false));
        TownHero other = NewHero(assets, facingLeft: facingLeft);
        Assert.NotNull(again.TryCast(SkillKeys.A, other, null, heldX, heldY));
        other.Update(200, 0, 0);
        Assert.NotNull(again.TryChain(SkillKeys.A, other, -heldX, heldY));
        Assert.Equal(200.0, other.SkillSlideSpeed);
    }

    // ------------------------------------------------------------------ 接段

    [Fact]
    public void AThreeStageRecipeChainsThroughEveryStageAndEachStageNeedsItsOwnChainFrame()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkills skills = Load(data, 2, Recipes(threeStages: true));
        HeroSkillSlot slot = skills.SlotOf(SkillKeys.A);
        Assert.Equal(3, slot.Recipe.Stages.Count);
        Assert.Equal(3, slot.StageStrikes.Count);
        Assert.Equal(300, slot.StageStrikes[2].SkillRatePercent);          // 第三段取第 1、3 列(2 级:300、60)
        Assert.Equal(60, slot.StageStrikes[2].SkillFixed);

        TownHero hero = NewHero(assets);
        var vitals = new HeroVitals(1, 500, 100, 0, 0, 0);
        Assert.NotNull(skills.TryCast(SkillKeys.A, hero, vitals));
        Assert.Equal((0, 80), (hero.SkillStage, vitals.Mp));

        // 第 0 段:没到接段帧(第 2 帧)按键没有反应;到了接上第 1 段,换段后帧号从头数,所以马上再按接不上第 2 段
        hero.Update(100, 0, 0);
        Assert.Null(skills.TryChain(SkillKeys.A, hero));
        hero.Update(100, 0, 0);
        Assert.NotNull(skills.TryChain(SkillKeys.A, hero));
        Assert.Equal((1, 0), (hero.SkillStage, hero.CurrentFrame));
        Assert.Null(skills.TryChain(SkillKeys.A, hero));
        Assert.Equal(1, hero.SkillStage);

        // 第 1 段播到第 2 帧(200 毫秒)后接上第 2 段(最后一段):不再扣蓝,最后一段之后没有下一段
        hero.Update(200, 0, 0);
        Assert.Equal(2, hero.CurrentFrame);
        Assert.NotNull(skills.TryChain(SkillKeys.A, hero));
        Assert.Equal((2, 80), (hero.SkillStage, vitals.Mp));
        hero.Update(300, 0, 0);
        Assert.Null(skills.TryChain(SkillKeys.A, hero));
        hero.Update(200, 0, 0);
        Assert.Equal((HeroMotion.Rest, 0), (hero.Motion, hero.SkillStage));
    }

    [Fact]
    public void PressingTheSameKeyAgainAfterTheChainFrameStartsTheNextStageWithoutPayingAgain()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)));
        HeroSkillSlot slot = skills.SlotOf(SkillKeys.A);
        TownHero hero = NewHero(assets);
        var vitals = new HeroVitals(1, 500, 100, 0, 0, 0);

        Assert.Same(slot, skills.TryCast(SkillKeys.A, hero, vitals));
        Assert.Equal((80, 3000.0), (vitals.Mp, slot.CooldownLeftMs));

        // 动画还没播到第 2 帧(接段帧):按键没有反应,也不会当成新的起手(技能进行中)
        hero.Update(150, 0, 0);
        Assert.Equal(1, hero.CurrentFrame);
        Assert.Null(skills.TryChain(SkillKeys.A, hero));
        Assert.Null(skills.TryCast(SkillKeys.A, hero, vitals));
        Assert.Equal(SkillCastResult.Cooldown, skills.LastCastResult);
        Assert.Equal((0, 80), (hero.SkillStage, vitals.Mp));

        // 第 2 帧:别的键、没按键都不接;同一个键接上第二段(按着朝向一侧的方向键:速度加成)
        hero.Update(50, 0, 0);
        Assert.Equal(2, hero.CurrentFrame);
        Assert.Null(skills.TryChain(SkillKeys.S, hero));
        Assert.Null(skills.TryChain(SkillKeys.None, hero));
        long swings = hero.SwingNumber;
        skills.Advance(500);                                         // 冷却走了一部分:接段不能把它重置回满
        Assert.Same(slot, skills.TryChain(SkillKeys.A, hero, 1, 0));
        Assert.Equal((1, "chainb.ani", 0), (hero.SkillStage, hero.SkillMotionName, hero.CurrentFrame));
        Assert.Equal((swings + 1, 300.0), (hero.SwingNumber, hero.SkillSlideSpeed));
        Assert.Equal((80, 2500.0), (vitals.Mp, slot.CooldownLeftMs));    // 不再扣蓝、不重新进冷却

        // 最后一段之后没有下一段;放完回到站立
        hero.Update(200, 0, 0);
        Assert.Null(skills.TryChain(SkillKeys.A, hero));
        hero.Update(100, 0, 0);
        Assert.Equal((HeroMotion.Rest, 0), (hero.Motion, hero.SkillStage));
        Assert.Null(skills.TryChain(SkillKeys.A, hero));            // 不在放技能时更没有"接段"
    }

    [Fact]
    public void TheSecondStageSlidesByTheDirectionHeldWhenItIsEntered()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        foreach ((int heldX, double speed) in new[] { (0, 200.0), (1, 300.0), (-1, 0.0) })
        {
            HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)));
            TownHero hero = NewHero(assets);
            Assert.NotNull(skills.TryCast(SkillKeys.A, hero, null, 1, 0));    // 第一段按着右键起手:速度 300
            Assert.Equal(300.0, hero.SkillSlideSpeed);
            hero.Update(200, 0, 0);
            Assert.NotNull(skills.TryChain(SkillKeys.A, hero, heldX, 0));
            Assert.Equal(speed, hero.SkillSlideSpeed);                          // 第二段的速度按接段那一刻按着的方向重算
        }
    }

    [Fact]
    public void ThePreconditionOfAStageGatesTheChainUntilThatSkillIsLearned()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 要求一个没学的技能:到了接段帧也接不上
        HeroSkills locked = Load(data, 2, Recipes(gateForStage1: 999));
        TownHero hero = NewHero(assets);
        Assert.NotNull(locked.TryCast(SkillKeys.A, hero));
        hero.Update(250, 0, 0);
        Assert.Null(locked.TryChain(SkillKeys.A, hero));
        Assert.Equal(0, hero.SkillStage);

        // 要求一个学过的技能(上挑,技能簿里 3 级):接得上
        HeroSkills open = Load(data, 2, Recipes(gateForStage1: AvatarFixture.UpperSlashSkill));
        TownHero other = NewHero(assets);
        Assert.NotNull(open.TryCast(SkillKeys.A, other));
        other.Update(250, 0, 0);
        Assert.NotNull(open.TryChain(SkillKeys.A, other));
        Assert.Equal(1, other.SkillStage);

        // 没有接技能等级查询的技能表(旧的创建方式)一律当没学
        HeroSkills detached = new HeroSkills(Load(data, 2, Recipes(gateForStage1: AvatarFixture.UpperSlashSkill)).Slots);
        TownHero third = NewHero(assets);
        Assert.NotNull(detached.TryCast(SkillKeys.A, third));
        third.Update(250, 0, 0);
        Assert.Null(detached.TryChain(SkillKeys.A, third));
    }

    // ------------------------------------------------------------------ 房间里

    private static void Run(RoomCombatSession s, int ms, int dirX = 0, params (int At, SkillKeys Keys)[] presses)
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
            s.Update(10, dirX, 0, false, false, keys);
        }
    }

    [Fact]
    public void InTheRoomEachStageHitsOnceWithItsOwnStrikeAndDamageAndTheKnockdownComesOnlyWithTheSecond()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, skillsFactory: Factory);
        RoomCombatSession s = arena.Session;
        MonsterActor m = s.Monsters[0];

        // 第一段:攻击框在动画第 2 帧(200 毫秒起),只打一次;没有击倒
        long hitAt = -1;
        for (int t = 0; t < 300; t += 10)
        {
            s.Update(10, 0, 0, false, false, t == 0 ? SkillKeys.A : SkillKeys.None);
            if (s.Hits == 1 && hitAt < 0)
            {
                hitAt = s.Hero.MotionMs;
            }
        }
        Assert.InRange(hitAt, 200, 209);
        Assert.Equal((1, 1, 0, 0), (s.SkillCasts, s.Hits, s.SkillChains, s.Hero.SkillStage));
        double first = PlaceholderDamage.SkillBaseHit * 180 / 100.0 + 40;
        Assert.InRange(s.TotalDamage, (long)Math.Floor(first), (long)Math.Floor(first) + 1);
        Assert.False(m.IsKnockedDown);

        // 第一段播到 310 毫秒时再按一次:接第二段(接段不算新的施放),第二段第 1 帧(100 毫秒起)打一次,击倒
        long total = s.TotalDamage;
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Assert.Equal((1, 1, 1), (s.SkillCasts, s.SkillChains, s.Hero.SkillStage));
        long secondHitAt = -1;
        for (int t = 0; t < 300; t += 10)
        {
            s.Update(10, 0, 0, false, false);
            if (s.Hits == 2 && secondHitAt < 0)
            {
                secondHitAt = s.Hero.MotionMs;
            }
        }
        Assert.InRange(secondHitAt, 100, 129);
        Assert.Equal(2, s.Hits);
        double second = PlaceholderDamage.SkillBaseHit * 300 / 100.0 + 60;
        Assert.InRange(s.TotalDamage - total, (long)Math.Floor(second), (long)Math.Floor(second) + 1);
        Assert.True(m.IsKnockedDown);
        Assert.Equal((HeroMotion.Rest, 0), (s.Hero.Motion, s.Hero.SkillStage));
    }

    [Fact]
    public void AKeyPressedBeforeTheChainFrameIsLostAndTheChainKeyDoesNotCountAsACast()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (700, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, skillsFactory: Factory);
        RoomCombatSession s = arena.Session;
        Run(s, 200, 0, (0, SkillKeys.A), (100, SkillKeys.A));          // 100 毫秒是第 1 帧:接段帧是第 2 帧,这一下没有反应
        Assert.Equal((1, 0, 0), (s.SkillCasts, s.SkillChains, s.Hero.SkillStage));
        Run(s, 50, 0, (0, SkillKeys.A));                               // 这一下落在 200 毫秒(第 2 帧起点):接上
        Assert.Equal((1, 1, 1), (s.SkillCasts, s.SkillChains, s.Hero.SkillStage));
        Run(s, 50, 0, (0, SkillKeys.A));                               // 已经是最后一段:再按没有反应
        Assert.Equal((1, 1, 1), (s.SkillCasts, s.SkillChains, s.Hero.SkillStage));
    }

    [Fact]
    public void TheStageEffectsAppearAtTheirFramesFollowTheHeroAndDoNotRepeat()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, chr: AvatarFixture.ActionChrPath, monsterSpeed: 0, skillsFactory: Factory);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        Assert.Empty(s.Effects);

        // 按着右键起手:第一段的特效在动画第 1 帧(100 毫秒起)才放,之前没有
        s.Update(10, 1, 0, false, false, SkillKeys.A);
        Assert.Equal(HeroMotion.Skill, hero.Motion);
        Run(s, 80, 1);
        Assert.Empty(s.Effects);
        Run(s, 20, 1);
        Assert.Equal(new[] { AvatarFixture.ChainEffect1 }, s.Effects.Select(e => e.Path).ToArray());
        AnimatedEffect effect = s.Effects[0];
        Assert.Equal(hero.FacingLeft, effect.FacingLeft);

        // 滑行中特效跟着主角走(落在主角的位置上),同一段不会再放第二个
        double x0 = hero.X;
        Run(s, 60, 1);
        Assert.True(hero.X > x0);
        Assert.Equal((hero.X, hero.Y), (effect.X, effect.Y));
        Assert.Single(s.Effects);

        // 动画过了 200 毫秒接第二段:第二段的特效一开始就放,第一段的那个还在放(350 毫秒)
        Run(s, 40, 1);
        Run(s, 10, 0, (0, SkillKeys.A));
        Assert.Equal(1, hero.SkillStage);
        Assert.Equal(new[] { AvatarFixture.ChainEffect1, AvatarFixture.ChainEffect2 }, s.Effects.Select(e => e.Path).ToArray());
        Run(s, 400, 0);
        Assert.Empty(s.Effects);                                        // 都播完去掉,第二段结束后不再放新的
        Assert.Equal(HeroMotion.Rest, hero.Motion);
    }

    [Fact]
    public void TheChainKeyIsBufferedDuringAHitPauseLikeAnyOtherSkillKey()
    {
        // 命中停顿里按下的技能键存起来,停顿一结束就生效(接段同样走这条路):第一段打中怪物后主角被定住一小会儿
        var strikes = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, strikes, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, weaponStun: 150, skillsFactory: Factory);
        RoomCombatSession s = arena.Session;
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        for (int i = 0; i < 100 && s.Hits < 1; i++)
        {
            s.Update(10, 0, 0, false, false);
        }
        Assert.Equal(1, s.Hits);
        Assert.True(s.HeroPauseMs > 0);
        // 停顿中按键:存着,停顿结束(帧已过接段帧)的那一步就接上第二段
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Assert.Equal(0, s.SkillChains);
        for (int i = 0; i < 40 && s.SkillChains == 0; i++)
        {
            s.Update(10, 0, 0, false, false);
        }
        Assert.Equal((1, 1), (s.SkillChains, s.Hero.SkillStage));
    }
}
