using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物投射物(M15,证据 E-103):出招动画的当前帧超过 [throw frame] 才发射、起点偏移随朝向镜像、[attack number] 决定一次出招里扔几次、
/// 被打断就不扔、发射声音;投射物按 .ptl 沿 x 轴直线飞、寿命到了消失、攻击框与主角的受击框相交就打中主角。数据全是合成的:
/// 投掷手站在 (360, 100) 朝左,发射点偏移 (30, 0, 40) → 投射物出现在 (330, 100, 40),以 200 像素/秒向左飞,寿命 1000 毫秒;
/// 迷你职业站在 (300, 100),站立时的受击框是世界 x ∈ [288, 312]、y ∈ [94, 106]、z ∈ [0, 80];投射物的攻击框是以它为中心的 16 × 16 × 16(z 从它的高度起)。
/// </summary>
public class MonsterThrowTests
{
    private const int HeroX = 300;
    private const int HeroY = 100;
    private static readonly IWalkArea Free = new RectUnionArea(new List<WorldRect>());

    private static GameData DataOf(DnfAssets assets) => new GameData(new PvfScriptSource(assets.Pvf));

    private static IReadOnlyList<ThrowAttackRecord> FixtureRecords(DnfAssets assets)
    {
        return ThrowAttackRecord.Parse(new TagTable(DataOf(assets).ReadScript(AvatarFixture.BlobThrowerMobPath)));
    }

    private static MonsterActor Make(DnfAssets assets, double x, double y, IReadOnlyList<ThrowAttackRecord> records = null)
    {
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobThrowerMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, (int)x, (int)y, 0, 1, 0, "[fixed]", "[normal]");
        var m = new MonsterActor(0, spawn, model, 1, 100, 222.5, 1500, 0);
        m.SetThrows(MonsterThrows.Load(assets, DataOf(assets), AvatarFixture.BlobThrowerMobPath, records ?? FixtureRecords(assets)));
        return m;
    }

    /// <summary>迷你怪物的记录改几项:发射帧、一次出招扔几次、起点偏移、是否反向、被动对象编号。</summary>
    private static ThrowAttackRecord Record(int frame = 1, int attackNumber = 1, int startX = AvatarFixture.ShotStartX, int startZ = AvatarFixture.ShotStartZ,
        bool reversed = false, int objectIndex = AvatarFixture.ShotObjectId, string particle = "Particle/Shot.ptl")
    {
        return new ThrowAttackRecord(0, 1, new[] { particle }, 0, objectIndex, 0, frame, new[] { startX }, new[] { 0 }, new[] { startZ }, attackNumber,
            AvatarFixture.ShotThrowSound, 0, new[] { reversed ? 1 : 0 });
    }

    /// <summary>推进到第一次发射为止(最多 <paramref name="maxSteps"/> 步);返回发射的投射物与声音。</summary>
    private static (List<MonsterProjectile> Shots, List<string> Sounds, int Steps) RunToThrow(MonsterActor m, double heroX = HeroX, int maxSteps = 200)
    {
        var shots = new List<MonsterProjectile>();
        var sounds = new List<string>();
        int steps = 0;
        while (steps < maxSteps && !m.TakeThrown(shots, sounds))
        {
            m.Update(10, heroX, HeroY, true, Free);
            steps++;
        }
        return (shots, sounds, steps);
    }

    // ------------------------------------------------------------------ 怪物一侧:什么时候扔、扔在哪

    [Fact]
    public void TheShotLeavesWhenTheAnimationPassesTheThrowFrameAndNotBefore()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Make(assets, 360, HeroY);
        (List<MonsterProjectile> shots, List<string> sounds, int _) = RunToThrow(m);
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.Equal(160L, m.PhaseMs);                                   // 发射帧 1:动画到第 2 帧(第 160 毫秒起)才发射,第 1 帧的 150 毫秒还没有
        MonsterProjectile shot = Assert.Single(shots);
        Assert.Same(m, shot.Owner);
        Assert.Equal((330.0, 100.0, 40.0), (shot.X, shot.Y, shot.Z));    // 朝左:x 偏移 30 往左镜像
        Assert.True(shot.FacingLeft);
        Assert.Equal(new[] { AvatarFixture.ShotThrowSound }, sounds);
        Assert.Equal(0.0, shot.AgeMs);
    }

    [Fact]
    public void AnAttackThrowsOnlyOnceAndTheMonsterLeavesTheAttackNormally()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Make(assets, 360, HeroY);
        RunToThrow(m);
        var again = new List<MonsterProjectile>();
        var soundsAgain = new List<string>();
        while (m.Phase == MonsterPhase.Attack)
        {
            m.Update(10, HeroX, HeroY, true, Free);
            Assert.False(m.TakeThrown(again, soundsAgain));
        }
        Assert.Equal(MonsterPhase.Wait, m.Phase);
        Assert.Equal(1L, m.SwingNumber);
        Assert.Empty(again);
    }

    [Fact]
    public void AMonsterOnTheLeftOfTheHeroThrowsToTheRightAndTheOffsetMirrors()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Make(assets, 240, HeroY);
        (List<MonsterProjectile> shots, _, _) = RunToThrow(m);
        MonsterProjectile shot = Assert.Single(shots);
        Assert.False(shot.FacingLeft);
        Assert.Equal((270.0, 100.0, 40.0), (shot.X, shot.Y, shot.Z));
    }

    [Fact]
    public void AReversedDirectionThrowsTheOtherWayAndStartsOnTheOtherSide()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Make(assets, 360, HeroY, new[] { Record(reversed: true) });
        (List<MonsterProjectile> shots, _, _) = RunToThrow(m);
        MonsterProjectile shot = Assert.Single(shots);
        Assert.False(shot.FacingLeft);                                   // 怪物朝左,[direction] 非 0 → 投射物朝右,起点偏移也按朝右放
        Assert.Equal(390.0, shot.X);
    }

    [Fact]
    public void AnAttackNumberOfThreeThrowsThreeTimesInARowAndTheCountComesBackWhileStanding()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Make(assets, 360, HeroY, new[] { Record(attackNumber: 3) });
        var afterThrow = new List<(long Swing, long PhaseMs)>();
        var shots = new List<MonsterProjectile>();
        var sounds = new List<string>();
        for (int step = 0; step < 400; step++)                          // 4 秒:第一轮连扔三次(约 0.5 秒),等 1.5 秒冷却,第二轮再扔三次
        {
            m.Update(10, HeroX, HeroY, true, Free);
            if (m.TakeThrown(shots, sounds))
            {
                afterThrow.Add((m.SwingNumber, m.PhaseMs));
            }
        }
        // 前两次扔完剩余次数还在 2 以上:同一步里重新出招(SwingNumber 加 1、阶段计时归零);第三次是最后一次,出招照常放完;
        // 第四到第六次是下一轮(站立时剩余次数恢复到 3)
        Assert.Equal(new[] { (2L, 0L), (3L, 0L), (3L, 160L), (5L, 0L), (6L, 0L), (6L, 160L) }, afterThrow);
        Assert.Equal(6, shots.Count);
        Assert.Equal(6, sounds.Count);
    }

    [Fact]
    public void AMonsterHurtBeforeTheThrowFrameDoesNotThrow()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Make(assets, 360, HeroY);
        var shots = new List<MonsterProjectile>();
        var sounds = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            m.Update(10, HeroX, HeroY, true, Free);
        }
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.True(m.PhaseMs < 160);
        m.ApplyHit(5, false, 0, false);
        Assert.Equal(MonsterPhase.Hurt, m.Phase);
        while (m.Phase == MonsterPhase.Hurt)
        {
            m.Update(10, HeroX, HeroY, true, Free);
            Assert.False(m.TakeThrown(shots, sounds));
        }
        Assert.Empty(shots);
    }

    [Fact]
    public void ARecordWhoseObjectCannotBeLoadedStillPlaysTheSoundAndCountsTheThrow()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Make(assets, 360, HeroY, new[] { Record(objectIndex: 4242) });
        Assert.Null(m.Throws.TemplateFor(0, 0));
        (List<MonsterProjectile> shots, List<string> sounds, _) = RunToThrow(m);
        Assert.Empty(shots);                                             // 被动对象编号不在列表里:没有东西可放
        Assert.Equal(new[] { AvatarFixture.ShotThrowSound }, sounds);
        Assert.True(m.Throws.Runner.Thrown(0));
        Assert.Equal(0, m.Throws.Runner.Remaining(0));
    }

    [Fact]
    public void AMonsterWithoutThrowsNeverReportsAnyAndTheFixtureBlockLoadsIntoTemplates()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobThrowerMobPath);
        var plain = new MonsterActor(0, new RoomMonsterSpawn(1, 0, 0, 360, HeroY, 0, 1, 0, "[fixed]", "[normal]"), model, 3, 100, 222.5, 1500, 0);
        var shots = new List<MonsterProjectile>();
        var sounds = new List<string>();
        for (int i = 0; i < 100; i++)
        {
            plain.Update(10, HeroX, HeroY, true, Free);
            Assert.False(plain.TakeThrown(shots, sounds));
        }
        Assert.Null(MonsterThrows.Load(assets, DataOf(assets), AvatarFixture.BlobThrowerMobPath, Array.Empty<ThrowAttackRecord>()));

        MonsterThrows throws = MonsterThrows.Load(assets, DataOf(assets), AvatarFixture.BlobThrowerMobPath, FixtureRecords(assets));
        ProjectileTemplate t = throws.TemplateFor(0, 0);
        Assert.NotNull(t);
        Assert.Equal(AvatarFixture.ShotAni, t.AniPath);
        Assert.Equal(AvatarFixture.ShotParticle, t.Particle.Path);
        Assert.Equal((ParticleMoveType.StraightLine, AvatarFixture.ShotSpeed), (t.Particle.X.Type, t.Particle.X.Var1A));
        Assert.Equal(AvatarFixture.ShotLifeMs, t.LifeMs);
        Assert.Equal(AvatarFixture.ShotDamageBonus, t.Strike.DamageBonus);
        Assert.Null(throws.TemplateFor(0, 1));                           // 只有一个投射物
        Assert.Null(throws.TemplateFor(5, 0));
    }

    // ------------------------------------------------------------------ 投射物一侧:飞、寿命、攻击框

    private static ProjectileTemplate Template(DnfAssets assets)
    {
        return MonsterThrows.Load(assets, DataOf(assets), AvatarFixture.BlobThrowerMobPath, FixtureRecords(assets)).TemplateFor(0, 0);
    }

    private static MonsterProjectile Shot(DnfAssets assets, double x, bool left, ProjectileTemplate template = null)
    {
        MonsterActor owner = Make(assets, 360, HeroY);
        return new MonsterProjectile(owner, template ?? Template(assets), x, 100, 40, left);
    }

    [Fact]
    public void AShotFliesAlongItsFacingAtTheParticleSpeedAndVanishesWhenItsLifeEnds()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterProjectile left = Shot(assets, 400, true);
        MonsterProjectile right = Shot(assets, 400, false);
        for (int i = 0; i < 50; i++)
        {
            left.Update(10);
            right.Update(10);
        }
        Assert.Equal(300.0, left.X, 6);                                  // 200 像素/秒 × 0.5 秒
        Assert.Equal(500.0, right.X, 6);
        Assert.Equal((100.0, 40.0), (left.Y, left.Z));                   // y、z 轴不动
        Assert.False(left.Finished);
        for (int i = 0; i < 49; i++)
        {
            left.Update(10);
        }
        Assert.False(left.Finished);                                     // 990 毫秒
        left.Update(10);
        Assert.True(left.Finished);                                      // 寿命 1000 毫秒
        Assert.Empty(left.AttackBoxes());
        left.Update(10);
        Assert.Equal(1000.0, left.AgeMs);                                // 消失之后不再走
    }

    [Fact]
    public void TheAttackBoxFollowsThePositionAndHeight()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterProjectile shot = Shot(assets, 400, true);
        Aabb box = Assert.Single(shot.AttackBoxes());
        Assert.Equal((392, 408, 92, 108, 40, 56), (box.MinX, box.MaxX, box.MinY, box.MaxY, box.MinZ, box.MaxZ));
        for (int i = 0; i < 10; i++)
        {
            shot.Update(10);
        }
        box = Assert.Single(shot.AttackBoxes());
        Assert.Equal((372, 388), (box.MinX, box.MaxX));                  // 飞了 20 像素
    }

    [Fact]
    public void AShotWithoutAParticleStaysPutAndLivesForTheDefaultTime()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        ProjectileTemplate real = Template(assets);
        var still = new ProjectileTemplate(real.Ani, real.AniPath, real.Strike, null);
        Assert.Equal(ProjectileTemplate.DefaultLifeMs, still.LifeMs);
        MonsterProjectile shot = Shot(assets, 400, true, still);
        for (int i = 0; i < 299; i++)
        {
            shot.Update(10);
        }
        Assert.Equal((400.0, false), (shot.X, shot.Finished));
        shot.Update(10);
        Assert.True(shot.Finished);
    }

    [Fact]
    public void TheParticleFileIsFoundNextToTheMobFirstAndThenUnderThePvfRoot()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        GameData data = DataOf(assets);
        ProjectileTemplate Load(string file) => MonsterThrows.Load(assets, data, AvatarFixture.BlobThrowerMobPath, new[] { Record(particle: file) }).TemplateFor(0, 0);

        Assert.Equal((double)AvatarFixture.ShotSpeed, Load("Particle/Shot.ptl").Particle.X.StraightSpeed);          // 相对 .mob 所在目录
        Assert.Equal((double)AvatarFixture.AbsShotSpeed, Load("Common/Particle/Shot.ptl").Particle.X.StraightSpeed);  // 从 PVF 根目录起的完整路径
        ProjectileTemplate missing = Load("Nowhere/Missing.ptl");
        Assert.NotNull(missing);                                         // 读不到 .ptl 不影响发射:当作没有粒子定义,原地不动
        Assert.Null(missing.Particle);
        Assert.Null(Load(string.Empty).Particle);                        // 写成空串同样没有粒子定义
    }

    [Fact]
    public void TheShorterOfTheParticleLifeAndTheObjectTimeLimitWinsAndAnAnimationThatEndsCanEndTheShot()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        ProjectileTemplate real = Template(assets);
        var timed = new ProjectileTemplate(real.Ani, real.AniPath, real.Strike, real.Particle, objectLifeMs: 400);
        Assert.Equal(400, timed.LifeMs);
        var longer = new ProjectileTemplate(real.Ani, real.AniPath, real.Strike, real.Particle, objectLifeMs: 5000);
        Assert.Equal(AvatarFixture.ShotLifeMs, longer.LifeMs);

        // 动画播完就消失(.obj 的 [on end of animation]):用一个不循环的 320 毫秒动画,没有粒子也没有时限时寿命就是动画时长
        AniFile oneShot = assets.LoadAni(AvatarFixture.BlobThrowAni);
        var template = new ProjectileTemplate(oneShot, AvatarFixture.BlobThrowAni, default, null, -1, true);
        Assert.Equal(320, template.LifeMs);
        MonsterProjectile shot = Shot(assets, 400, true, template);
        for (int i = 0; i < 31; i++)
        {
            shot.Update(10);
        }
        Assert.False(shot.Finished);
        shot.Update(10);
        Assert.True(shot.Finished);
    }

    // ------------------------------------------------------------------ 会话:打中主角、飞过、画出来

    private static readonly HitStunTable SyntheticStunTable = new HitStunTable(10, 330, 1000);

    private static DamageConstants Constants()
    {
        return new DamageConstants { PhysicalDamageRate = 1000f, MagicalDamageRate = 1000f, PhysicalReduceRate = 100f, MagicalReduceRate = 100f };
    }

    private sealed class Arena : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public RoomCombatSession Session { get; }
        public MonsterActor Monster { get; }

        /// <param name="records">投掷记录(缺省读合成 .mob 里的那一个)。</param>
        /// <param name="ranged">给了就装成怪物的 [attack kind](出生 3 秒后才抽签,射程里 100% 出招);缺省走"够近就出招"的占位(70 像素内)。</param>
        public Arena(int monsterX = 340, IReadOnlyList<ThrowAttackRecord> records = null, bool ranged = false, bool useVitals = true, int hp = 100)
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
            TownHero hero = TownHero.Create(Assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), HeroX, HeroY, false, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));

            Monster = Make(Assets, monsterX, HeroY, records);
            Monster.PhysicalAttack = 40;
            if (ranged)
            {
                Monster.SetAttackKinds(new[] { new AttackKindRecord(100f, 0f, 0f, true, 400, 0, 20, 0, 30, 0, 30, 0) }, 1u);
            }
            var plain = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
            Session = new RoomCombatSession(Assets, room, new[] { Monster }, plain, 1, default, default, null, 0, -1f, SyntheticStunTable,
                useVitals ? new HeroVitals(1, hp, 50, 100, 0, 0) : null, useVitals ? Constants() : null);
            Session.DrainSounds(new List<SoundCue>());
        }

        public void Steps(int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                Session.Update(10, 0, 0, false, false);
            }
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    [Fact]
    public void AShotThatReachesTheHeroHurtsHimOnceWithItsOwnAttackInfoAndVanishes()
    {
        using var arena = new Arena(340);
        var cues = new List<SoundCue>();
        arena.Steps(25);                                                  // 出招 160 毫秒时发射,出生点 (310, 100, 40) 的攻击框 x ∈ [302, 318] 已与主角相交
        arena.Session.DrainSounds(cues);
        Assert.Equal(1, arena.Session.HeroHits);
        // 公式常量是合成的(与 MonsterAttackTests 相同):怪物攻击力 40、投射物的 [damage bonus] 25 → 基础伤害 50;主角防御力 100、怪物等级 1 → 防御率 0.5 → 伤害 25
        Assert.Equal(25, arena.Session.HeroDamageTaken);
        Assert.Empty(arena.Session.Projectiles);                          // 打中就消失
        Assert.Contains(cues, c => c.Kind == SoundCueKind.Effect && c.Key == AvatarFixture.ShotThrowSound);
        arena.Steps(100);
        Assert.Equal(1, arena.Session.HeroHits);                          // 同一个投射物只打一次
    }

    [Fact]
    public void TheShotUsesItsOwnAttackInfoNotTheMonstersMeleeOne()
    {
        long Damage(HeroStrikeInfo? melee)
        {
            using var arena = new Arena(340);
            if (melee.HasValue)
            {
                arena.Monster.Strike = melee.Value;
            }
            arena.Steps(25);
            return arena.Session.HeroDamageTaken;
        }
        // 投射物的 [damage bonus] 是 25;怪物自己的攻击信息改成 400 也不影响这一击
        Assert.Equal(25, Damage(null));
        Assert.Equal(25, Damage(new HeroStrikeInfo(400, 0, false)));
    }

    [Fact]
    public void AReversedShotFliesAwayFromTheHeroAndExpiresWithoutHurtingAnyone()
    {
        using var arena = new Arena(340, new[] { Record(reversed: true) });
        arena.Steps(25);
        Assert.Single(arena.Session.Projectiles);
        MonsterProjectile shot = arena.Session.Projectiles[0];
        Assert.False(shot.FacingLeft);
        Assert.True(shot.X > 340);
        arena.Steps(110);                                                 // 寿命 1000 毫秒
        Assert.Empty(arena.Session.Projectiles);
        Assert.Equal(0, arena.Session.HeroHits);
    }

    [Fact]
    public void AShotAboveTheHeroHurtBoxFliesOverHim()
    {
        using var arena = new Arena(340, new[] { Record(startZ: 100) });
        arena.Steps(25);
        Assert.Single(arena.Session.Projectiles);
        arena.Steps(120);
        Assert.Equal(0, arena.Session.HeroHits);                          // 攻击框 z ∈ [100, 116],主角的受击框 z ∈ [0, 80]
        Assert.Empty(arena.Session.Projectiles);
    }

    [Fact]
    public void AShotFromFarAwayFliesForAWhileBeforeItHits()
    {
        using var arena = new Arena(500, ranged: true);
        int spawnStep = -1;
        int hitStep = -1;
        for (int step = 0; step < 700 && hitStep < 0; step++)
        {
            arena.Session.Update(10, 0, 0, false, false);
            if (spawnStep < 0 && arena.Session.Projectiles.Count > 0)
            {
                spawnStep = step;
                MonsterProjectile shot = arena.Session.Projectiles[0];
                Assert.Equal((470.0, 100.0, 40.0), (shot.X, shot.Y, shot.Z));
            }
            if (arena.Session.HeroHits > 0)
            {
                hitStep = step;
            }
        }
        Assert.True(spawnStep > 300, "出生 3 秒后才出招");
        Assert.True(hitStep > spawnStep);
        // 攻击框 x ∈ [x − 8, x + 8],x 从 470 起每 10 毫秒左移 2 像素;x − 8 < 312(受击框右边)即 x < 320:第 75 或 76 步
        Assert.InRange(hitStep - spawnStep, 75, 76);
        Assert.Empty(arena.Session.Projectiles);
    }

    [Fact]
    public void ADeadHeroIsNotHurtAgainAndAnUnarmedArenaIgnoresShots()
    {
        using var arena = new Arena(340, hp: 1);
        arena.Steps(25);
        Assert.True(arena.Session.HeroDead);
        Assert.Equal(1, arena.Session.HeroHits);
        arena.Steps(200);
        Assert.Equal(1, arena.Session.HeroHits);

        using var noVitals = new Arena(340, useVitals: false);            // 没有主角生命值:怪物照样出招、投射物照样飞,但打不到主角
        noVitals.Steps(25);
        Assert.Single(noVitals.Session.Projectiles);
        noVitals.Steps(100);
        Assert.Equal(0, noVitals.Session.HeroHits);
    }

    [Fact]
    public void AFlyingShotIsDrawnAtItsPositionWithItsAnimation()
    {
        using var arena = new Arena(500, ranged: true);
        for (int i = 0; i < 400 && arena.Session.Projectiles.Count == 0; i++)
        {
            arena.Session.Update(10, 0, 0, false, false);
        }
        Assert.Single(arena.Session.Projectiles);
        SceneSprite shot = arena.Session.Build(0).Single(s => s.AnimationPath == AvatarFixture.ShotAni);
        double x0 = arena.Session.Projectiles[0].X;
        arena.Steps(20);
        SceneSprite later = arena.Session.Build(0).Single(s => s.AnimationPath == AvatarFixture.ShotAni);
        Assert.True(later.ScreenX < shot.ScreenX);                        // 向左飞:屏幕位置跟着挪
        Assert.True(arena.Session.Projectiles[0].X < x0);
    }
}
