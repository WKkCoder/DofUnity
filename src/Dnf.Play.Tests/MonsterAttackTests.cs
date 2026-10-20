using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物打主角(P5.M5,证据 E-086):出招动画里带攻击框的帧才能伤人、同一次出招只打中一次、够得着才出招(几何判定)、
/// 伤害走原版公式(输入是推断)、主角受击硬直与击退、命中停顿、血量归零的死亡、血条与红字。数据全是合成的:
/// 迷你职业站在 (300, 100) 朝右,站立时的受击框是世界 x ∈ [288, 312]、y ∈ [94, 106]、z ∈ [0, 80];
/// "会伤人的迷你怪物"(<see cref="AvatarFixture.BlobStrikerMobPath"/>)朝左站在 (340, 100) 时,出招动画第 2 帧(160 到 240 毫秒)的攻击框是
/// x ∈ [290, 330]、y ∈ [90, 110]、z ∈ [0, 50],与主角的受击框相交。公式常量也是合成的(物理攻击力项除以 1000、减伤系数 100):
/// 怪物攻击力 40、[damage bonus] 25 → 基础伤害 50;主角防御力 100、怪物等级 1 → 防御率 100 / (1 × 100 + 100) = 0.5 → 伤害 25。
/// </summary>
public class MonsterAttackTests
{
    private const int HeroX = 300;
    private const int HeroY = 100;
    private const int MonsterAttack = 40;
    private const int ExpectedDamage = 25;

    private static readonly HitStunTable SyntheticStunTable = new HitStunTable(10, 330, 1000);

    private static DamageConstants Constants()
    {
        return new DamageConstants
        {
            PhysicalDamageRate = 1000f,
            MagicalDamageRate = 1000f,
            PhysicalReduceRate = 100f,
            MagicalReduceRate = 100f,
        };
    }

    private static HeroVitals Vitals(int hpMax = 100, int mpMax = 50, double defense = 100, int hitRecovery = 0)
    {
        return new HeroVitals(1, hpMax, mpMax, defense, 0, hitRecovery);
    }

    /// <summary>一个迷你房间、主角(带受击动作)、一个会伤人的迷你怪物。</summary>
    private sealed class StrikerArena : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public RoomCombatSession Session { get; }
        public MonsterActor Monster { get; }
        public TownHero Hero => Session.Hero;

        /// <param name="frozen">为真时主角的可走区域只是脚下一小块:怪物的每一步都会落在区域外,所以怪物原地不动。</param>
        /// <param name="strike">给了就替换怪物出招用的攻击信息(默认读合成的 .atk:[damage bonus] 25)。</param>
        public StrikerArena(int monsterX = 340, int monsterY = 100, HeroVitals vitals = null, bool useVitals = true, DamageConstants constants = null,
            bool useConstants = true, HitStunTable stunTable = null, HeroStrikeInfo? strike = null, bool frozen = false, string chr = null,
            int attack = MonsterAttack, int level = 1)
        {
            Assets = DnfAssets.Open(_fx.Root);
            var tiles = new List<TileFile>();
            for (int i = 0; i < 5; i++)
            {
                tiles.Add(new TileFile("t" + i, new List<KeyValuePair<string, int>>(), 80, new byte[14 * 6]));
            }
            var map = new RoomMap("map/test/arena.map", "[normal]", tiles, 50, 80, 120, new List<RoomBackground>(), new List<MapAnimation>(),
                new List<int[]> { null, null, null, null }, null);
            var content = new RoomContent(map, null, null, null, null);
            AvatarClass cls = AvatarClass.FromChr(Assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
            AvatarOutfit outfit = AvatarOutfit.CreateDefault(Assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
            var area = frozen ? new List<WorldRect> { new WorldRect(HeroX - 5, HeroY - 5, 10, 10) } : new List<WorldRect>();
            TownHero hero = TownHero.Create(Assets, chr ?? AvatarFixture.ActionChrPath, outfit, area, HeroX, HeroY, false, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));

            MonsterModel model = MonsterModel.Read(Assets, AvatarFixture.BlobStrikerMobPath);
            var spawn = new RoomMonsterSpawn(1, 0, 0, monsterX, monsterY, 0, 1, 0, "[fixed]", "[normal]");
            Monster = new MonsterActor(0, spawn, model, level, 100000, 222.5, 1500, 0, 0);
            Monster.PhysicalAttack = attack;
            Monster.Strike = strike ?? HeroStrikeInfo.Read(new GameData(new PvfScriptSource(Assets.Pvf)), AvatarFixture.BlobStrikerAtk);

            var plain = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
            Session = new RoomCombatSession(Assets, room, new[] { Monster }, plain, 1, default, default, null, 0, -1f, stunTable,
                useVitals ? vitals ?? Vitals() : null, useConstants ? constants ?? Constants() : null);
        }

        /// <summary>每 10 毫秒一步推进 <paramref name="steps"/> 步,主角一直按 (dirX, dirY) 走。</summary>
        public void Steps(int steps, int dirX = 0, int dirY = 0)
        {
            for (int i = 0; i < steps; i++)
            {
                Session.Update(10, dirX, dirY, false, false);
            }
        }

        /// <summary>推进到主角第一次被打中为止(最多 <paramref name="maxSteps"/> 步);返回被打中那一步的下标(从 0 数),没打中返回 -1。</summary>
        public int RunToFirstHit(int maxSteps = 100)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                Session.Update(10, 0, 0, false, false);
                if (Session.HeroHits > 0)
                {
                    return i;
                }
            }
            return -1;
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    private static readonly IReadOnlyList<Aabb> HeroBoxes = new List<Aabb> { new Aabb(288, 312, 94, 106, 0, 80) };

    // ------------------------------------------------------------------ 怪物一侧:攻击框、出招判断

    [Fact]
    public void OnlyTheFramesWithAnAttackBoxAreDangerousAndTheBoxMirrorsWithTheFacing()
    {
        using var arena = new StrikerArena();
        MonsterActor m = arena.Monster;
        Assert.Empty(m.AttackBoxesAt(2));                                // 还没出招:没有攻击框
        Assert.Equal(0, m.SwingNumber);

        m.Update(10, HeroX, HeroY, true, null, HeroBoxes);               // 够得着:开始出招,朝左
        Assert.Equal((MonsterPhase.Attack, 1L, true), (m.Phase, m.SwingNumber, m.FacingLeft));
        Assert.Equal(AvatarFixture.BlobStrikeAni, m.CurrentAnimationPath);
        Assert.Empty(m.AttackBoxesAt(0));
        Assert.Empty(m.AttackBoxesAt(1));
        Assert.Empty(m.AttackBoxesAt(3));
        Assert.Empty(m.AttackBoxesAt(-1));
        Assert.Empty(m.AttackBoxesAt(4));                                // 越界

        Aabb box = Assert.Single(m.AttackBoxesAt(2));
        Assert.Equal((290, 330, 90, 110, 0, 50), (box.MinX, box.MaxX, box.MinY, box.MaxY, box.MinZ, box.MaxZ));
    }

    [Fact]
    public void AMonsterFacingRightPutsTheBoxOnItsRightSide()
    {
        using var arena = new StrikerArena(monsterX: 260);               // 主角在它右边:朝右
        MonsterActor m = arena.Monster;
        m.Update(10, HeroX, HeroY, true, null, HeroBoxes);
        Assert.Equal((MonsterPhase.Attack, false), (m.Phase, m.FacingLeft));
        Aabb box = Assert.Single(m.AttackBoxesAt(2));
        Assert.Equal((270, 310, 90, 110, 0, 50), (box.MinX, box.MaxX, box.MinY, box.MaxY, box.MinZ, box.MaxZ));
    }

    [Fact]
    public void ADeadMonsterHasNoAttackBoxes()
    {
        using var arena = new StrikerArena();
        MonsterActor m = arena.Monster;
        m.Update(10, HeroX, HeroY, true, null, HeroBoxes);
        Assert.Single(m.AttackBoxesAt(2));
        Assert.True(m.ApplyHit(1000000, false, 0, true));                // 一击打死
        Assert.False(m.IsAlive);
        Assert.Empty(m.AttackBoxesAt(2));
    }

    [Fact]
    public void AMonsterWalksUntilItsSwingCanReachTheHeroThenSwingsAndRepeatsAfterTheAttackDelay()
    {
        using var arena = new StrikerArena(monsterX: 450);
        MonsterActor m = arena.Monster;
        m.Update(10, HeroX, HeroY, true, null, HeroBoxes);
        Assert.Equal(MonsterPhase.Walk, m.Phase);
        for (int i = 0; i < 200 && m.Phase != MonsterPhase.Attack; i++)
        {
            m.Update(10, HeroX, HeroY, true, null, HeroBoxes);
        }
        // 攻击框朝左伸到 x - 50,碰到主角受击框的右沿 312 时(取整后 x ≤ 361)出招,走到够得着为止,不走进主角身体里
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.InRange(m.X, 359.0, 362.0);
        Assert.Equal(1, m.SwingNumber);

        // 出招动画(四帧 × 80 毫秒)放完回到待机,冷却 1500 毫秒后再出一次
        for (int i = 0; i < 230; i++)
        {
            m.Update(10, HeroX, HeroY, true, null, HeroBoxes);
        }
        Assert.Equal(2, m.SwingNumber);
    }

    [Fact]
    public void WithoutTheHeroBoxesAMonsterKeepsTheOldFixedRangeRule()
    {
        using var arena = new StrikerArena(monsterX: 450);
        MonsterActor m = arena.Monster;
        for (int i = 0; i < 200 && m.Phase != MonsterPhase.Attack; i++)
        {
            m.Update(10, HeroX, HeroY, true, null);
        }
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.InRange(m.X, 367.0, 370.5);                                // 横向 70 像素内出招(旧行为)
    }

    // ------------------------------------------------------------------ 会话:命中、伤害、受击反应

    [Fact]
    public void TheSwingHitsTheHeroExactlyOnceAtTheFrameWithTheAttackBox()
    {
        using var arena = new StrikerArena();
        RoomCombatSession s = arena.Session;
        int hitStep = arena.RunToFirstHit();
        Assert.Equal(16, hitStep);                                       // 第 0 步开始出招,第 16 步到 160 毫秒 = 第 2 帧
        Assert.Equal(1, s.HeroHits);
        Assert.Equal(1L, arena.Monster.SwingNumber);

        arena.Steps(30);                                                 // 帧 2 与帧 3 里攻击框还在(帧 2 停 80 毫秒):同一次出招不再打
        Assert.Equal(1, s.HeroHits);
        Assert.True(arena.Monster.HitHeroThisSwing);
    }

    [Fact]
    public void TheHitCostsHpThroughTheOriginalFormulaAndShowsARedNumberOnTheHero()
    {
        using var arena = new StrikerArena();
        RoomCombatSession s = arena.Session;
        Assert.Equal(100, s.Vitals.Hp);
        arena.RunToFirstHit();

        Assert.Equal((100 - ExpectedDamage, (long)ExpectedDamage), (s.Vitals.Hp, s.HeroDamageTaken));
        HitPopup popup = Assert.Single(s.Popups);
        Assert.Equal((ExpectedDamage.ToString(), true, 80), (popup.Text, popup.OnHero, popup.Z));     // 头顶 = 受击框顶
        Assert.Equal((300.0, 100.0), (popup.WorldX, popup.WorldY));
        Assert.False(s.HeroDead);
    }

    [Fact]
    public void ABetterDefenseTakesLessAndTheDamageIsAtLeastOne()
    {
        using var weak = new StrikerArena(vitals: Vitals(defense: 0));
        weak.RunToFirstHit();
        Assert.Equal(50, weak.Session.HeroDamageTaken);                  // 没有防御:基础伤害原样(40 × 125%)

        using var strong = new StrikerArena(vitals: Vitals(defense: 300));
        strong.RunToFirstHit();
        Assert.Equal(12, strong.Session.HeroDamageTaken);                // 防御率 300 / (100 + 300) = 0.75 → 12.5 向零取整

        using var wall = new StrikerArena(vitals: Vitals(defense: 1e9));
        wall.RunToFirstHit();
        Assert.Equal(1, wall.Session.HeroDamageTaken);                   // 防御率顶到上限,伤害至少是 1
    }

    [Fact]
    public void AMagicalStrikeUsesTheMagicalAttackAndDefense()
    {
        var info = new HeroStrikeInfo(25, 0, false, magical: true);
        using var arena = new StrikerArena(strike: info, vitals: new HeroVitals(1, 100, 50, 300, 100, 0));
        arena.Monster.PhysicalAttack = 4000;                             // 物理攻击力不参与
        arena.Monster.MagicalAttack = MonsterAttack;
        arena.RunToFirstHit();
        Assert.Equal(25, arena.Session.HeroDamageTaken);                 // 魔法防御 100 → 防御率 0.5,与物理防御(300)无关
    }

    [Fact]
    public void WithoutFormulaConstantsTheDamageFallsBackToTheAttackTimesTheBonus()
    {
        using var arena = new StrikerArena(useConstants: false);
        arena.RunToFirstHit();
        Assert.Equal(50, arena.Session.HeroDamageTaken);                 // 40 × (100 + 25) / 100
    }

    [Fact]
    public void TheHeroFlinchesForTheFormulaStunTimeAndIsKnockedBackAwayFromTheMonster()
    {
        using var arena = new StrikerArena(stunTable: SyntheticStunTable, vitals: Vitals(hitRecovery: 500));
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();

        float rate = HitStun.Rate(-1f, -1f, false, false);
        HitStunResult expected = HitStun.Damage(SyntheticStunTable, 500, 0, rate, 0, 0, false);
        Assert.True(expected.StunMs > 0);
        Assert.True(expected.KnockPixels > 0);
        TownHero hero = s.Hero;
        Assert.Equal((HeroMotion.Hurt, 1), (hero.Motion, hero.HurtCount));
        Assert.Equal((double)expected.StunMs, hero.HurtSpanMs, 9);
        Assert.Equal(AvatarFixture.Damage1Ani, hero.CurrentAnimationPath);
        Assert.False(hero.FacingLeft);                                   // 受击不转向

        // 怪物在主角右边朝左出招:主角被往左推(远离怪物)expected.KnockPixels 像素
        arena.Steps((int)Math.Ceiling(expected.StunMs / 10.0) + 5);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        Assert.Equal(HeroX - expected.KnockPixels, hero.X, 3);
    }

    [Fact]
    public void WithoutAStunTableTheHeroUsesTheFallbackStunAndKnockback()
    {
        using var arena = new StrikerArena();
        arena.RunToFirstHit();
        TownHero hero = arena.Hero;
        Assert.Equal((double)HeroHurtTuning.FallbackStunMs, hero.HurtSpanMs, 9);
        arena.Steps(HeroHurtTuning.FallbackStunMs / 10 + 1);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        Assert.Equal(HeroX - (double)HeroHurtTuning.FallbackKnockPixels, hero.X, 3);
    }

    [Fact]
    public void ASecondHitWhileFlinchingSwitchesToTheOtherDamageMotion()
    {
        using var arena = new StrikerArena();
        arena.RunToFirstHit();
        Assert.Equal(AvatarFixture.Damage1Ani, arena.Hero.CurrentAnimationPath);
        // 第二击:让怪物的下一次出招也打中(怪物走回来再出招,冷却 1500 毫秒;主角被推开 30 像素后怪物会追上去)
        for (int i = 0; i < 400 && arena.Session.HeroHits < 2; i++)
        {
            arena.Steps(1);
        }
        Assert.Equal(2, arena.Session.HeroHits);
        Assert.Equal(2, arena.Hero.HurtCount);
        Assert.Equal(AvatarFixture.Damage2Ani, arena.Hero.CurrentAnimationPath);
    }

    [Fact]
    public void TheHitStopFreezesBothSidesButNotTheStunClock()
    {
        var info = new HeroStrikeInfo(25, 0, false, hitValue: 100);
        using var arena = new StrikerArena(strike: info);
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        Assert.Equal(100.0, s.HeroPauseMs, 9);
        Assert.Equal(100.0, arena.Monster.PauseMs, 9);

        TownHero hero = s.Hero;
        double x = hero.X;
        long monsterPhase = arena.Monster.PhaseMs;
        Assert.Equal(0.0, hero.HurtClockMs, 9);
        arena.Steps(10);                                                 // 停顿的 100 毫秒:主角不动、怪物不动
        Assert.Equal(0.0, s.HeroPauseMs, 9);
        Assert.Equal(x, hero.X, 9);
        Assert.Equal(monsterPhase, arena.Monster.PhaseMs);
        Assert.Equal(100.0, hero.HurtClockMs, 6);                        // 硬直的秒表照走
        Assert.Equal(HeroMotion.Hurt, hero.Motion);
    }

    [Fact]
    public void ANoHitInfoMeansNoHitStop()
    {
        using var arena = new StrikerArena();
        arena.RunToFirstHit();
        Assert.Equal(0.0, arena.Session.HeroPauseMs, 9);
        Assert.Equal(0.0, arena.Monster.PauseMs, 9);
    }

    // ------------------------------------------------------------------ 打不中的情形

    [Fact]
    public void AHeroWhoWalksOutOfTheBoxBeforeTheStrikeFrameIsNotHit()
    {
        using var arena = new StrikerArena(monsterX: 361);               // 刚好够得着(攻击框 [311, 351] 碰到受击框 [288, 312])
        arena.Steps(1);
        Assert.Equal(MonsterPhase.Attack, arena.Monster.Phase);
        arena.Steps(39, dirX: -1);                                       // 主角往左跑开
        Assert.Equal(0, arena.Session.HeroHits);
        Assert.Equal(100, arena.Session.Vitals.Hp);
        Assert.Equal(1, arena.Monster.SwingNumber);
        Assert.Empty(arena.Session.Popups);
    }

    [Fact]
    public void AMonsterThatCannotGetCloseNeverSwings()
    {
        using var arena = new StrikerArena(monsterX: 340, monsterY: 160, frozen: true);
        arena.Steps(300);
        Assert.Equal(0, arena.Monster.SwingNumber);
        Assert.Equal(0, arena.Session.HeroHits);
        Assert.Equal(MonsterPhase.Walk, arena.Monster.Phase);
    }

    [Fact]
    public void AMonsterOffTheHeroDepthLineWalksCloserInDepthBeforeSwinging()
    {
        using var arena = new StrikerArena(monsterX: 340, monsterY: 150);
        Assert.Equal(-1, arena.RunToFirstHit(5));                        // 一开始够不着(深度方向差 50)
        Assert.Equal(MonsterPhase.Walk, arena.Monster.Phase);
        int hit = arena.RunToFirstHit(200);
        Assert.True(hit > 0);                                            // 走近以后打中了
        Assert.True(arena.Monster.Y < 116);                              // 攻击框的深度范围 [Y − 10, Y + 10] 碰到了受击框 [94, 106]
    }

    // ------------------------------------------------------------------ 没有生命值、没有受击动作、死亡

    [Fact]
    public void WithoutVitalsTheHeroIsNeverHurtAndMonstersKeepTheFixedRangeBehavior()
    {
        using var arena = new StrikerArena(useVitals: false);
        arena.Steps(100);
        Assert.Null(arena.Session.Vitals);
        Assert.Equal(0, arena.Session.HeroHits);
        Assert.False(arena.Session.HeroDead);
        Assert.Equal(HeroMotion.Rest, arena.Hero.Motion);
        Assert.True(arena.Monster.SwingNumber >= 1);                     // 怪物照样走近出招,只是打不到(旧行为)
        Assert.DoesNotContain(arena.Session.Build(0), x => x.AnimationPath == "hero-hp");
    }

    [Fact]
    public void AHeroWithoutDamageMotionsStillLosesHpAndIsNotStuck()
    {
        using var arena = new StrikerArena(chr: AvatarFixture.ChrPath);
        arena.RunToFirstHit();
        Assert.False(arena.Hero.CanBeHurt);
        Assert.Equal(100 - ExpectedDamage, arena.Session.Vitals.Hp);
        Assert.Equal(HeroMotion.Rest, arena.Hero.Motion);                // 没有受击动作:不进硬直
        arena.Steps(5, dirX: 1);
        Assert.True(arena.Hero.X > HeroX);                               // 照常能走
    }

    [Fact]
    public void WhenHpReachesZeroTheHeroStaysInTheDamagePoseAndNothingMoreHappens()
    {
        using var arena = new StrikerArena(vitals: Vitals(hpMax: 20));
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        Assert.True(s.HeroDead);
        Assert.Equal(0, s.Vitals.Hp);
        TownHero hero = s.Hero;
        Assert.Equal(HeroMotion.Hurt, hero.Motion);
        Assert.Equal(HeroHurtTuning.DeadHoldMs, hero.HurtSpanMs);

        // 死后不接受输入:方向、攻击、跳跃都没有效果(先等击退走完)
        arena.Steps(30);
        double x = hero.X;
        for (int i = 0; i < 100; i++)
        {
            s.Update(10, 1, 1, i % 7 == 0, i % 11 == 0);
        }
        Assert.Equal(HeroMotion.Hurt, hero.Motion);
        Assert.InRange(hero.X, x - 1e-9, x + 1e-9);
        Assert.Equal(1, s.HeroHits);                                     // 怪物看不见死人,不再出招
        Assert.Equal(MonsterPhase.Wait, arena.Monster.Phase);
        Assert.Equal(1, arena.Monster.SwingNumber);
        Assert.Contains(s.Build(0), x2 => x2.AnimationPath == "hero-dead");
    }

    // ------------------------------------------------------------------ 叠加层

    [Fact]
    public void TheOverlayShowsTheHeroBarsAndTheRedNumber()
    {
        using var arena = new StrikerArena();
        RoomCombatSession s = arena.Session;
        List<SceneSprite> before = s.Build(0);
        Assert.Equal(new[] { "hero-hp", "hero-hp-label", "hero-mp", "hero-mp-label" },
            before.Where(x => x.LayerRank == OverlayArt.UiRank).Select(x => x.AnimationPath).ToArray());

        arena.RunToFirstHit();
        List<SceneSprite> overlay = s.Build(0).Where(x => x.LayerRank == OverlayArt.UiRank).ToList();
        Assert.Equal(new[] { "hero-hp", "hero-hp-label", "hero-mp", "hero-mp-label", "damage" }, overlay.Select(x => x.AnimationPath).ToArray());
        Assert.All(overlay, x => Assert.Equal(-3, x.NpcId));

        // 生命条:填充部分亮红、其余暗红;满值 100 → 75
        SceneSprite hp = overlay[0];
        Assert.Equal((RoomCombatSession.HeroBarX, RoomCombatSession.HeroBarY), (hp.ScreenX, hp.ScreenY));
        Assert.Equal((RoomCombatSession.HeroBarWidth, RoomCombatSession.HeroBarHeight), (hp.Frame.Width, hp.Frame.Height));
        int filled = (int)Math.Round((RoomCombatSession.HeroBarWidth - 2) * 75.0 / 100.0);
        Assert.Equal(0xE2, hp.Frame.Rgba[(1 * hp.Frame.Width + filled) * 4]);
        Assert.Equal(0x3A, hp.Frame.Rgba[(1 * hp.Frame.Width + filled + 1) * 4]);

        // 魔法条还是满的(没有耗蓝):整条亮蓝
        SceneSprite mp = overlay[2];
        Assert.Equal(0x3C, mp.Frame.Rgba[(1 * mp.Frame.Width + mp.Frame.Width - 2) * 4]);

        // 红字:有红色的像素,没有怪物受伤用的黄色
        SceneSprite damage = overlay[4];
        Assert.True(HasPixel(damage.Frame, 0xFF, 0x50, 0x50));
        Assert.False(HasPixel(damage.Frame, 0xFF, 0xE2, 0x3C));
    }

    [Fact]
    public void WithNoManaPoolTheManaBarIsLeftOut()
    {
        using var arena = new StrikerArena(vitals: Vitals(mpMax: 0));
        Assert.Equal(new[] { "hero-hp", "hero-hp-label" },
            arena.Session.Build(0).Where(x => x.LayerRank == OverlayArt.UiRank).Select(x => x.AnimationPath).ToArray());
    }

    private static bool HasPixel(SpriteFrame frame, byte r, byte g, byte b)
    {
        for (int i = 0; i + 3 < frame.Rgba.Length; i += 4)
        {
            if (frame.Rgba[i + 3] != 0 && frame.Rgba[i] == r && frame.Rgba[i + 1] == g && frame.Rgba[i + 2] == b)
            {
                return true;
            }
        }
        return false;
    }
}
