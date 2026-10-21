using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 主角的倒地与起身(证据 E-079、E-082、E-083、E-085、E-105):被倒地类的攻击打中、在空中被打、倒地中再被打、被打死时,主角像怪物一样
/// 飞起来、落地颠一下、躺着、起身(状态机与怪物共用);躺着时累计伤害超过最大血量的 15% 提前起身;死了就躺着不动;
/// [damage reaction] 为 none 的只扣血;没有倒地动作的角色脚本一律走普通受击。
/// 数据全是合成的:迷你职业站在 (300, 100) 朝右,倒地动画四帧(80、10000、10000、400 毫秒,第 4 帧是躺着),体重 60000,
/// 会伤人的迷你怪物站在 (340, 100) 朝左,第 2 帧出招(攻击框 x ∈ [290, 330]、z ∈ [0, 50]),每一击 25 点伤害(公式常量见 <see cref="MonsterAttackTests"/>)。
/// </summary>
public class HeroKnockdownTests
{
    private const int HeroX = 300;
    private const int HeroY = 100;
    private const int Damage = 25;

    /// <summary>怪物站在远处,要先走过来才出招:给主角留出起跳的时间。</summary>
    private const int FarMonsterX = 560;

    private static DamageConstants Constants()
    {
        return new DamageConstants { PhysicalDamageRate = 1000f, MagicalDamageRate = 1000f, PhysicalReduceRate = 100f, MagicalReduceRate = 100f };
    }

    /// <summary>怪物的出招信息:[damage bonus] 25;倒地类(<paramref name="down"/>)、普通受击(1)、none 三种反应;[push aside] 默认 0(主角原地起落)。</summary>
    private static HeroStrikeInfo Strike(bool down = false, bool none = false, int push = 0, int lift = 300, bool bounce = false, int hitValue = -1)
    {
        return new HeroStrikeInfo(AvatarFixture.StrikerDamageBonus, push, down, 0, 0, hitValue, false, -1f, 0, false, 0, false, lift, bounce, false,
            reactionDamage: !down && !none, hitWav: null, reactionNone: none);
    }

    private static KnockdownInput Input(int lift = 300, bool bounce = false)
    {
        return new KnockdownInput(lift, bounce, false, false, 500, 500);
    }

    private sealed class Arena : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public RoomCombatSession Session { get; }
        public MonsterActor Monster { get; }
        public TownHero Hero => Session.Hero;
        public HeroKnockdown Down => Session.HeroDown;

        /// <param name="attackDelayMs">怪物两次出招之间的冷却;默认很长,只打一次。</param>
        public Arena(HeroStrikeInfo strike, int hpMax = 1000, string chr = null, int attackDelayMs = 100000, int monsterX = 340)
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
            TownHero hero = TownHero.Create(Assets, chr ?? AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), HeroX, HeroY, false, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));

            MonsterModel model = MonsterModel.Read(Assets, AvatarFixture.BlobStrikerMobPath);
            var spawn = new RoomMonsterSpawn(1, 0, 0, monsterX, HeroY, 0, 1, 0, "[fixed]", "[normal]");
            Monster = new MonsterActor(0, spawn, model, 1, 100000, 222.5, attackDelayMs, 0, 0);
            Monster.PhysicalAttack = 40;
            Monster.Strike = strike;

            var plain = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
            Session = new RoomCombatSession(Assets, room, new[] { Monster }, plain, 1, default, default, null, 0, -1f, new HitStunTable(10, 330, 1000),
                new HeroVitals(1, hpMax, 50, 100, 0, 0), Constants());
        }

        public void Steps(int steps, int dirX = 0, int dirY = 0, bool attack = false, bool jump = false)
        {
            for (int i = 0; i < steps; i++)
            {
                Session.Update(10, dirX, dirY, attack, jump);
            }
        }

        /// <summary>推进到主角第一次被打中为止;返回被打中那一步的下标(从 0 数),没打中返回 -1。</summary>
        public int RunToFirstHit(int maxSteps = 300)
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

        /// <summary>推进到条件成立为止(最多 <paramref name="maxSteps"/> 步);成立返回 true。</summary>
        public bool RunUntil(Func<bool> condition, int maxSteps = 600)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                if (condition())
                {
                    return true;
                }
                Session.Update(10, 0, 0, false, false);
            }
            return condition();
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    // ------------------------------------------------------------------ 会话:倒地类攻击

    [Fact]
    public void ADownAttackKnocksTheHeroDownThroughTheThreeStagesAndHeGetsBackUp()
    {
        using var arena = new Arena(Strike(down: true));
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        Assert.True(arena.Down.Available);
        Assert.True(arena.RunToFirstHit() >= 0);

        // 被打中的那一步:进入倒地,转向攻击方(怪物在右边),扣血,原地起飞
        Assert.Equal((HeroMotion.Down, 1, 1, 0), (hero.Motion, s.HeroHits, arena.Down.Begun, arena.Down.Stage));
        Assert.False(hero.FacingLeft);
        Assert.Equal(1000 - Damage, s.Vitals.Hp);
        Assert.Equal((1, 0), (hero.DownCount, hero.HurtCount));

        // 一路走完:阶段 0(腾空)→ 1(颠一下)→ 2(躺着)→ 起身;按什么键都没用,没有水平推力所以位置不变
        var stages = new List<int>();
        double maxZ = 0;
        int lyingFrames = 0;
        for (int i = 0; i < 600 && hero.IsDown; i++)
        {
            if (stages.Count == 0 || stages[stages.Count - 1] != arena.Down.Stage)
            {
                stages.Add(arena.Down.Stage);
            }
            maxZ = Math.Max(maxZ, hero.Z);
            if (arena.Down.Stage == 2)
            {
                Assert.Equal((3, 0.0), (hero.CurrentFrame, hero.Z));          // 躺着:躺地帧、贴地
                lyingFrames++;
            }
            Assert.Equal(HeroX, hero.X, 9);
            bool pressing = i < 40;                                       // 前 400 毫秒一直按着方向键、攻击键与跳跃键:没有任何效果
            s.Update(10, pressing ? -1 : 0, pressing ? 1 : 0, pressing, pressing);
        }
        Assert.Equal(new[] { 0, 1, 2 }, stages);
        Assert.True(maxZ > 0);
        Assert.True(lyingFrames > 0);
        Assert.False(hero.IsDown);
        Assert.Equal(HeroMotion.Rest, hero.Motion);
        Assert.Equal(0.0, hero.Z);
        Assert.Equal((1, 0, 1, false), (arena.Down.GetUps, arena.Down.EarlyEnds, arena.Down.Begun, arena.Down.Armed));

        // 起身之后能照常走
        s.Update(10, 1, 0, false, false);
        Assert.Equal(HeroMotion.Move, hero.Motion);
    }

    [Fact]
    public void TheHeroFacesTheAttackerAndIsPushedAwayByTheDownPush()
    {
        using var arena = new Arena(Strike(down: true, push: 300, lift: 300));
        arena.RunToFirstHit();
        TownHero hero = arena.Hero;
        Assert.False(hero.FacingLeft);                                    // 怪物在右边:转向右边(攻击方),被往左推
        arena.Steps(5);
        Assert.True(arena.Down.HorizontalSpeed < 0);
        Assert.True(hero.X < HeroX);
        arena.RunUntil(() => !hero.IsDown);
        Assert.True(hero.X < HeroX - 5);

        // 怪物在左边:转向左边,被往右推
        using var left = new Arena(Strike(down: true, push: 300, lift: 300), monsterX: 260);
        Assert.True(left.RunToFirstHit(600) >= 0);
        Assert.True(left.Hero.FacingLeft);
        left.Steps(5);
        Assert.True(left.Down.HorizontalSpeed > 0);
        Assert.True(left.Hero.X > HeroX);
    }

    [Fact]
    public void AnOrdinaryHitOnAGroundedHeroIsJustTheHurtMotion()
    {
        using var arena = new Arena(Strike());
        arena.RunToFirstHit();
        Assert.Equal((HeroMotion.Hurt, 0, 1), (arena.Hero.Motion, arena.Down.Begun, arena.Hero.HurtCount));
        Assert.Equal(0, arena.Hero.DownCount);
    }

    [Fact]
    public void ANoneReactionOnlyCostsHpAndTheHeroDoesNotReactAtAll()
    {
        using var arena = new Arena(Strike(none: true), hpMax: 100);
        arena.RunToFirstHit();
        RoomCombatSession s = arena.Session;
        Assert.Equal((1, 100 - Damage), (s.HeroHits, s.Vitals.Hp));
        Assert.Equal((HeroMotion.Rest, 0, 0, 0), (arena.Hero.Motion, arena.Hero.HurtCount, arena.Hero.DownCount, arena.Down.Begun));
        Assert.Contains(s.Popups, p => p.Text == Damage.ToString());       // 红字照常飘
    }

    [Fact]
    public void AHeroWithoutADownMotionIsOnlyHurtEvenByADownAttack()
    {
        using var arena = new Arena(Strike(down: true), chr: AvatarFixture.HurtOnlyChrPath);
        Assert.False(arena.Down.Available);
        arena.RunToFirstHit();
        Assert.Equal((HeroMotion.Hurt, 0, 0), (arena.Hero.Motion, arena.Down.Begun, arena.Hero.DownCount));
        Assert.False(arena.Down.Active);
    }

    // ------------------------------------------------------------------ 会话:空中被打、倒地中被打

    /// <summary>
    /// 找一个让怪物那一击打中"已经离地"的主角的起跳时机:先量出不跳时第一次被打中的步数,再把起跳提前 0 到 60 步试;
    /// 找到返回(竞技场,起跳提前了几步);第一次被打中的那一步开始前主角的 z 已经 &gt; 0。
    /// </summary>
    private static Arena ArenaWhereTheHitLandsInTheAir(HeroStrikeInfo strike)
    {
        int first;
        using (var probe = new Arena(strike, monsterX: FarMonsterX))
        {
            first = probe.RunToFirstHit();
        }
        Assert.True(first >= 0);
        for (int early = 0; early <= 60; early++)
        {
            var arena = new Arena(strike, monsterX: FarMonsterX);
            bool air = false;
            for (int i = 0; i < first + 60; i++)
            {
                double zBefore = arena.Hero.Z;
                int hitsBefore = arena.Session.HeroHits;
                arena.Session.Update(10, 0, 0, false, i == first - early);
                if (arena.Session.HeroHits > hitsBefore)
                {
                    air = zBefore > 0;
                    break;
                }
            }
            if (air)
            {
                return arena;
            }
            arena.Dispose();
        }
        throw new InvalidOperationException("找不到让这一击打中腾空主角的起跳时机");
    }

    [Fact]
    public void AnOrdinaryHitOnAnAirborneHeroKnocksHimDownAndNoLandingSoundPlays()
    {
        using Arena arena = ArenaWhereTheHitLandsInTheAir(Strike());
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        // 普通受击(决策函数的 1)打中离地的主角:走倒地分支,不是受击硬直
        Assert.Equal((HeroMotion.Down, 1, 0, 1), (hero.Motion, arena.Down.Begun, hero.HurtCount, hero.DownCount));
        Assert.False(hero.IsAirborne);

        var cues = new List<SoundCue>();
        s.DrainSounds(cues);
        arena.RunUntil(() => !hero.IsDown);
        s.DrainSounds(cues);
        Assert.False(hero.IsDown);
        Assert.DoesNotContain(cues, c => c.Key == SwordmanVoice.JumpEnd);    // 倒地不是"落地":没有落地声
    }

    [Fact]
    public void AHitWhileDownKnocksTheHeroDownAgainWithoutLeavingTheDownState()
    {
        // 怪物每隔 100 毫秒再出一招;第一击倒地,之后换成普通受击的一击:打在倒地中(腾空或躺着)的主角身上也走倒地分支
        using var arena = new Arena(Strike(down: true), attackDelayMs: 100);
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        arena.Monster.Strike = Strike();
        Assert.True(arena.RunUntil(() => s.HeroHits >= 2, 200));
        Assert.True(arena.Hero.IsDown);
        Assert.Equal((2, 0, 1), (arena.Down.Begun, arena.Hero.HurtCount, arena.Hero.DownCount));    // 没起过身,也没进过受击硬直
        Assert.True(arena.Down.Active);
    }

    [Fact]
    public void TheHeroFlightIsFrozenByTheHitStopLikeTheMonsters()
    {
        using var arena = new Arena(Strike(down: true, hitValue: 200));
        arena.RunToFirstHit();
        RoomCombatSession s = arena.Session;
        Assert.True(s.HeroPauseMs > 0);
        double z = arena.Hero.Z, ms = arena.Hero.DownAnimationMs;
        arena.Steps(5);                                                   // 停顿还没结束:高度与动画时钟都不动
        Assert.Equal((z, ms), (arena.Hero.Z, arena.Hero.DownAnimationMs));
        arena.Steps(30);
        Assert.True(arena.Hero.Z > z);
    }

    [Fact]
    public void TheHurtBoxesFollowTheDownPoseTheHeightAndTheLyingFrame()
    {
        using var arena = new Arena(Strike(down: true));
        arena.RunToFirstHit();
        TownHero hero = arena.Hero;
        arena.Steps(20);                                                  // 腾空:站立受击框抬到离地高度上
        Assert.True(hero.Z > 0);
        List<Aabb> flying = HeroHurtBoxes.At(hero);
        Assert.Single(flying);
        Assert.Equal((int)Math.Round(hero.Z), flying[0].MinZ);
        Assert.Equal((int)Math.Round(hero.Z) + 80, flying[0].MaxZ);

        Assert.True(arena.RunUntil(() => arena.Down.Stage == 2));         // 躺着:贴地的扁框
        List<Aabb> lying = HeroHurtBoxes.At(hero);
        Assert.Single(lying);
        Assert.Equal((0, 20), (lying[0].MinZ, lying[0].MaxZ));
        Assert.Equal((HeroX - 30, HeroX + 30), (lying[0].MinX, lying[0].MaxX));
    }

    // ------------------------------------------------------------------ 会话:死亡

    [Fact]
    public void AHeroKilledByADownAttackFallsLiesDownAndStaysThere()
    {
        using var arena = new Arena(Strike(down: true), hpMax: 20);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        arena.RunToFirstHit();
        Assert.True(s.HeroDead);
        Assert.Equal((HeroMotion.Down, 1), (hero.Motion, arena.Down.Begun));

        Assert.True(arena.RunUntil(() => !arena.Down.Active));            // 腾空、颠一下、躺下:躺下的那一刻转入死亡
        Assert.Equal((HeroMotion.Down, 0.0, 3), (hero.Motion, hero.Z, hero.CurrentFrame));
        double x = hero.X;
        for (int i = 0; i < 300; i++)
        {
            s.Update(10, 1, 1, i % 7 == 0, i % 11 == 0);                  // 死后不接受输入,也不会起身
        }
        Assert.Equal((HeroMotion.Down, 3, 0, 1), (hero.Motion, hero.CurrentFrame, arena.Down.GetUps, s.HeroHits));
        Assert.InRange(hero.X, x - 1e-9, x + 1e-9);
        Assert.Equal(MonsterPhase.Wait, arena.Monster.Phase);             // 怪物看不见死人
    }

    [Fact]
    public void AnOrdinaryOrNoneReactionKillingBlowAlsoKnocksTheHeroDown()
    {
        using (var ordinary = new Arena(Strike(), hpMax: 20))
        {
            ordinary.RunToFirstHit();
            Assert.True(ordinary.Session.HeroDead);
            Assert.Equal((HeroMotion.Down, 1), (ordinary.Hero.Motion, ordinary.Down.Begun));      // 已死 + 有倒地动作 → 倒地(决策函数第 3 步)
        }
        using (var none = new Arena(Strike(none: true, push: 0, lift: 0), hpMax: 20))
        {
            none.RunToFirstHit();
            Assert.True(none.Session.HeroDead);
            Assert.Equal((HeroMotion.Down, 1), (none.Hero.Motion, none.Down.Begun));
            none.Steps(5);
            Assert.True(none.Hero.Z > 0);
            Assert.True(none.Down.HorizontalSpeed < 0);                                           // 没有请求时用引擎默认的推力 400 与抬升 400
            none.RunUntil(() => !none.Down.Active);
            Assert.True(none.Hero.X < HeroX);
        }
    }

    [Fact]
    public void ADeadHeroWithoutADownMotionStaysInTheDamagePoseAsBefore()
    {
        using var arena = new Arena(Strike(down: true), hpMax: 20, chr: AvatarFixture.HurtOnlyChrPath);
        arena.RunToFirstHit();
        Assert.True(arena.Session.HeroDead);
        Assert.Equal((HeroMotion.Hurt, 0), (arena.Hero.Motion, arena.Down.Begun));
    }

    // ------------------------------------------------------------------ HeroKnockdown:提前起身(E-085)

    private static TownHero NewHero(DnfAssets assets, string chr = AvatarFixture.ActionChrPath)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, chr, outfit, new List<WorldRect>(), HeroX, HeroY, false, TownHero.DungeonRestTag);
    }

    private static void StepUntil(HeroKnockdown knock, Func<bool> condition, int maxSteps = 600)
    {
        for (int i = 0; i < maxSteps && !condition(); i++)
        {
            knock.Step(10);
        }
        Assert.True(condition());
    }

    [Fact]
    public void TheEarlyGetUpRuleOnlyStartsAfterTheHeroLiesDownAndNeedsMoreThanFifteenPercent()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        var knock = new HeroKnockdown(hero);
        Assert.True(knock.Begin(Input(), 0, false, dead: false));

        knock.NoteDamage(1000, 100);                                      // 还没躺下(没装填):不累计
        Assert.Equal((false, 0.0, false), (knock.Armed, knock.Accumulated, knock.EarlyEndPending));

        StepUntil(knock, () => knock.Stage == 2);
        Assert.Equal((true, 0.0), (knock.Armed, knock.Accumulated));       // 躺下的那一刻装填,累计清零
        knock.NoteDamage(15, 100);                                        // 刚好 15%:不算(必须大于)
        Assert.Equal((false, 15.0), (knock.EarlyEndPending, knock.Accumulated));
        knock.NoteDamage(1, 100);                                         // 累计 16% > 15%
        Assert.True(knock.EarlyEndPending);

        knock.Step(10);                                                   // 躺着时下一步就起身,不用等动画放完
        Assert.Equal((false, 1, 0, false), (hero.IsDown, knock.EarlyEnds, knock.GetUps, knock.Armed));
        Assert.Equal((HeroMotion.Rest, 0.0), (hero.Motion, hero.Z));
        Assert.Equal((0.0, false), (knock.Accumulated, knock.EarlyEndPending));
    }

    [Fact]
    public void ABigHitWhileLyingKnocksHimUpAgainButHeStandsUpAtTheNextLandingWithoutLyingDown()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        var knock = new HeroKnockdown(hero);
        knock.Begin(Input(), 0, false, dead: false);
        StepUntil(knock, () => knock.Stage == 2);

        knock.NoteDamage(30, 100);                                        // 躺着挨了大伤害:触发,同时又被击飞(倒地中再请求,阶段 1)
        knock.Begin(Input(), 0, false, dead: false);
        Assert.Equal((true, 1, 2), (knock.EarlyEndPending, knock.Stage, knock.Begun));
        Assert.True(hero.Z >= 0);

        var stages = new List<int>();
        for (int i = 0; i < 600 && knock.Active; i++)
        {
            stages.Add(knock.Stage);
            knock.Step(10);
        }
        Assert.DoesNotContain(2, stages);                                 // 落地就起身:不再躺下
        Assert.Equal((false, 1, 0), (hero.IsDown, knock.EarlyEnds, knock.GetUps));
    }

    [Fact]
    public void TheDeadNeverGetUpEvenWhenTheEarlyGetUpIsPending()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        var knock = new HeroKnockdown(hero);
        knock.Begin(Input(), 0, false, dead: false);
        StepUntil(knock, () => knock.Stage == 2);
        knock.NoteDamage(60, 100);
        Assert.True(knock.EarlyEndPending);
        knock.Begin(Input(), 0, false, dead: true);                       // 这一击把他打死了:提前起身不再起作用
        StepUntil(knock, () => !knock.Active);
        Assert.Equal((true, 0, 0, HeroMotion.Down), (hero.IsDown, knock.EarlyEnds, knock.GetUps, hero.Motion));
        Assert.Equal((0.0, 3), (hero.Z, hero.CurrentFrame));
    }

    [Fact]
    public void ABeginWithoutADownMotionDoesNothing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets, AvatarFixture.HurtOnlyChrPath);
        var knock = new HeroKnockdown(hero);
        Assert.False(knock.Available);
        Assert.False(knock.Begin(Input(), 0, false, dead: false));
        knock.Step(10);
        Assert.Equal((HeroMotion.Rest, 0, false), (hero.Motion, knock.Begun, knock.Active));
    }

    [Fact]
    public void AnAirborneHeroKeepsHisHeightAndStartsFallingFromIt()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        TownHero hero = NewHero(assets);
        hero.Update(10, 0, 0, false, true);
        for (int i = 0; i < 100 && hero.Z <= 0; i++)
        {
            hero.Update(10, 0, 0, false, false);
        }
        double z = hero.Z;
        Assert.True(z > 0);
        var knock = new HeroKnockdown(hero);
        knock.Begin(Input(lift: 0), 0, true, dead: false);                // 没有抬升:从当前高度往下掉
        Assert.Equal((HeroMotion.Down, z), (hero.Motion, hero.Z));
        knock.Step(20);
        Assert.True(hero.Z < z);
        StepUntil(knock, () => knock.Stage == 2);
        Assert.Equal(0.0, hero.Z);
    }
}
