using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物的霸体(M50,证据 E-143):.mob 的 [superarmor on attack] 第一个数非 0 的怪物,出招期间(出招状态,STATE_ATTACK = 8)带档位 1 的霸体——
/// 挨了不无视霸体的一击不产生受击反应(也不倒地、不被击退),伤害照扣;攻击信息写了 [ignore super armor] 的一击照样打断它;
/// 出招之外没有霸体;被打死的一击不受保护;[damage reaction] 是 none 的一击对谁都没有反应(决策函数第 1 步)。
/// 数据全是合成的(会伤人的迷你怪物、迷你职业),检验的是规则本身,不是原版数值。
/// </summary>
public sealed class MonsterSuperArmorTests
{
    private static readonly IWalkArea Free = new RectUnionArea(new List<WorldRect>());
    private const double WalkSpeed = 222.5;

    private static MonsterActor Blob(DnfAssets assets, double x, double y, int hp = 100, int attackDelay = 1500, int attackSpeed = 0)
    {
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, (int)x, (int)y, 0, 1, 0, "[fixed]", "[normal]");
        return new MonsterActor(0, spawn, model, 3, hp, WalkSpeed, attackDelay, attackSpeed);
    }

    private static void Run(MonsterActor m, double ms, double heroX, double heroY, bool heroPresent = true)
    {
        for (double left = ms; left > 0; left -= 10)
        {
            m.Update(10, heroX, heroY, heroPresent, Free);
        }
    }

    // ------------------------------------------------------------------ 怪物本身

    [Fact]
    public void OnlyAMonsterWithTheTagAndInTheAttackStateIsArmored()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100);
        Assert.False(m.SuperArmorOnAttack);
        m.Update(10, 300, 100, true, Free);
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.False(m.IsSuperArmored);                                  // 没带标签:出招也没有霸体

        MonsterActor armored = Blob(assets, 360, 100);
        armored.SuperArmorOnAttack = true;
        Assert.Equal(MonsterPhase.Wait, armored.Phase);
        Assert.False(armored.IsSuperArmored);                            // 带标签、还没出招
        armored.Update(10, 300, 100, true, Free);
        Assert.Equal(MonsterPhase.Attack, armored.Phase);
        Assert.True(armored.IsSuperArmored);                             // 进入出招状态:打开

        Run(armored, 400, 300, 100);                                     // 出招动画 3 × 80 毫秒放完,回待机、冷却 1500 毫秒
        Assert.Equal(MonsterPhase.Wait, armored.Phase);
        Assert.False(armored.IsSuperArmored);                            // 离开出招状态:关闭

        // 被打死了就不再有霸体(死亡流程里 Phase 不是出招)
        MonsterActor dying = Blob(assets, 360, 100, hp: 5);
        dying.SuperArmorOnAttack = true;
        dying.Update(10, 300, 100, true, Free);
        Assert.True(dying.ApplyHit(10, false, 0, true, default, default, noReaction: true));
        Assert.False(dying.IsSuperArmored);
    }

    [Fact]
    public void AHitWithoutReactionOnlyCostsHealthAndTheAttackKeepsGoing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100, hp: 100, attackSpeed: 400);       // 出招动画按 0.4 倍速,墙钟 600 毫秒
        m.SuperArmorOnAttack = true;
        m.Update(10, 300, 100, true, Free);
        Run(m, 100, 300, 100);
        double x0 = m.X;
        int frame = m.CurrentFrame;
        string ani = m.CurrentAnimationPath;

        Assert.False(m.ApplyHit(10, true, 80, true, default, default, noReaction: true));    // 倒地攻击也一样:没有反应
        Assert.Equal(90, m.Hp);
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.Equal(ani, m.CurrentAnimationPath);
        Assert.Equal(frame, m.CurrentFrame);
        Assert.False(m.IsKnockedDown);
        Run(m, 100, 300, 100);
        Assert.Equal(x0, m.X);                                           // 没有击退
        Assert.Equal(MonsterPhase.Attack, m.Phase);                      // 出招照常走(还没放完)
        Run(m, 400, 300, 100);
        Assert.Equal(MonsterPhase.Wait, m.Phase);                        // 放完了自己收招
    }

    [Fact]
    public void AHitWithoutReactionLeavesAnUnfinishedPushGoing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 200, 100, hp: 100);
        m.ApplyHit(1, false, 40, true);                                  // 普通一击:向左推 40 像素
        Run(m, 30, 5000, 100, heroPresent: false);
        Assert.True(m.X < 200);
        m.ApplyHit(1, false, 99, false, default, default, noReaction: true);      // 没有反应的一击:不起新的推力,也不取消旧的
        Assert.Equal(98, m.Hp);
        Run(m, 500, 5000, 100, heroPresent: false);
        Assert.InRange(m.X, 159.0, 161.0);                               // 总位移还是 40 像素、方向没变
    }

    [Fact]
    public void AKillingBlowStillKillsWithoutReaction()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 360, 100, hp: 5);
        m.SuperArmorOnAttack = true;
        m.Update(10, 300, 100, true, Free);
        Assert.True(m.IsSuperArmored);
        Assert.True(m.ApplyHit(10, false, 0, true, default, default, noReaction: true));
        Assert.Equal(0, m.Hp);
        Assert.Equal(MonsterPhase.Dying, m.Phase);                       // 死亡的那一击先于霸体处理(决策函数第 3 步在第 4 步之前)
    }

    // ------------------------------------------------------------------ 房间会话

    private static HeroStrikeInfo HeroStrike(bool none = false, bool ignore = false)
    {
        return new HeroStrikeInfo(0, 0, false, 0, 0, -1, false, -1f, 0, false, 0, false, 300, false, false,
            reactionDamage: !none, hitWav: null, reactionNone: none, ignoreSuperArmor: ignore);
    }

    private sealed class Arena : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public RoomCombatSession Session { get; }
        public MonsterActor Monster { get; }
        public TownHero Hero => Session.Hero;

        /// <param name="strike">主角三段普攻共用的攻击信息。</param>
        /// <param name="armored">怪物带不带 [superarmor on attack]。</param>
        /// <param name="monsterHp">怪物血量(默认很大,打不死)。</param>
        /// <param name="monsterX">怪物站的位置;主角在 (300, 100) 朝右。</param>
        /// <param name="attackSpeed">怪物的 [attack speed](千分比,0 表示 1 倍);小一点出招更慢,主角的攻击框打得中的时候它还在出招。</param>
        /// <param name="attackDelayMs">怪物两次出招之间的冷却。</param>
        public Arena(HeroStrikeInfo strike, bool armored, int monsterHp = 100000, int monsterX = 340, int attackSpeed = 400, int attackDelayMs = 100000)
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
            TownHero hero = TownHero.Create(Assets, AvatarFixture.ChrPath, outfit, new List<WorldRect>(), 300, 100, false, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));

            MonsterModel model = MonsterModel.Read(Assets, AvatarFixture.BlobStrikerMobPath);
            var spawn = new RoomMonsterSpawn(1, 0, 0, monsterX, 100, 0, 1, 0, "[fixed]", "[normal]");
            Monster = new MonsterActor(0, spawn, model, 1, monsterHp, WalkSpeed, attackDelayMs, attackSpeed);
            Monster.SuperArmorOnAttack = armored;

            var strikes = new[] { strike, strike, strike };
            Session = new RoomCombatSession(Assets, room, new[] { Monster }, strikes, 1);
        }

        /// <summary>按一下攻击键(一步):第一段普攻开始。</summary>
        public void Attack()
        {
            Session.Update(10, 0, 0, true, false);
        }

        /// <summary>每 10 毫秒一步推进 <paramref name="ms"/> 毫秒。</summary>
        public void Run(int ms)
        {
            for (int t = 0; t < ms; t += 10)
            {
                Session.Update(10, 0, 0, false, false);
            }
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

    [Fact]
    public void AnArmoredMonsterTakesAHitWhileAttackingWithoutFlinching()
    {
        using var arena = new Arena(HeroStrike(), armored: true);
        RoomCombatSession s = arena.Session;
        arena.Attack();
        Assert.True(arena.RunUntil(() => s.Hits > 0));

        MonsterActor m = arena.Monster;
        Assert.Equal(1, s.Hits);
        Assert.True(m.Hp < 100000);                                      // 伤害照扣
        Assert.Equal(MonsterPhase.Attack, m.Phase);                      // 出招没被打断
        Assert.False(m.IsKnockedDown);
        Assert.Equal(1, s.MonsterArmorAbsorbed);
        Assert.Equal(0, s.SuperArmorAbsorbed);                           // 主角那一侧的霸体计数不混

        arena.Run(1000);                                                 // 出招自己放完(0.4 倍速,800 毫秒)
        Assert.Equal(MonsterPhase.Wait, m.Phase);
    }

    [Fact]
    public void AnUnarmoredMonsterIsInterruptedByTheSameHit()
    {
        using var arena = new Arena(HeroStrike(), armored: false);
        RoomCombatSession s = arena.Session;
        arena.Attack();
        Assert.True(arena.RunUntil(() => s.Hits > 0));

        Assert.Equal(MonsterPhase.Hurt, arena.Monster.Phase);            // 打断了出招
        Assert.Equal(0, s.MonsterArmorAbsorbed);
    }

    [Fact]
    public void AStrikeThatIgnoresSuperArmorStillInterruptsAnArmoredMonster()
    {
        using var arena = new Arena(HeroStrike(ignore: true), armored: true);
        RoomCombatSession s = arena.Session;
        arena.Attack();
        Assert.True(arena.RunUntil(() => s.Hits > 0));

        Assert.Equal(MonsterPhase.Hurt, arena.Monster.Phase);            // [ignore super armor]:霸体挡不住
        Assert.Equal(0, s.MonsterArmorAbsorbed);
    }

    [Fact]
    public void ANoneReactionStrikeNeverMakesAnyMonsterFlinchAndIsNotCountedAsArmor()
    {
        // 出招中、不带霸体的怪物:none 的一击没有反应(决策函数第 1 步),也不算霸体吞的
        using var attacking = new Arena(HeroStrike(none: true), armored: false);
        attacking.Attack();
        Assert.True(attacking.RunUntil(() => attacking.Session.Hits > 0));
        Assert.True(attacking.Monster.Hp < 100000);
        Assert.Equal(MonsterPhase.Attack, attacking.Monster.Phase);
        Assert.Equal(0, attacking.Session.MonsterArmorAbsorbed);

        // 待机的怪物:一样没有反应
        using var waiting = new Arena(HeroStrike(none: true), armored: false);
        waiting.Run(1000);                                               // 怪物出招放完,进入冷却
        Assert.Equal(MonsterPhase.Wait, waiting.Monster.Phase);
        waiting.Attack();
        Assert.True(waiting.RunUntil(() => waiting.Session.Hits > 0));
        Assert.True(waiting.Monster.Hp < 100000);
        Assert.Equal(MonsterPhase.Wait, waiting.Monster.Phase);
        Assert.False(waiting.Monster.IsKnockedDown);
    }

    [Fact]
    public void TheArmorEndsWithTheAttackSoALaterHitFlinches()
    {
        using var arena = new Arena(HeroStrike(), armored: true);
        RoomCombatSession s = arena.Session;
        arena.Attack();
        Assert.True(arena.RunUntil(() => s.Hits > 0));
        Assert.Equal((1, MonsterPhase.Attack), (s.MonsterArmorAbsorbed, arena.Monster.Phase));

        Assert.True(arena.RunUntil(() => arena.Monster.Phase == MonsterPhase.Wait && !arena.Hero.IsAttacking));   // 怪物收招,主角也放完了
        int hits = s.Hits;
        arena.Attack();
        Assert.True(arena.RunUntil(() => s.Hits > hits));
        Assert.Equal(MonsterPhase.Hurt, arena.Monster.Phase);            // 没在出招:没有霸体,正常受击
        Assert.Equal(1, s.MonsterArmorAbsorbed);                         // 计数停在第一次
    }

    [Fact]
    public void AKillingBlowOnAnArmoredAttackingMonsterIsNotAbsorbed()
    {
        using var arena = new Arena(HeroStrike(), armored: true, monsterHp: 5);
        RoomCombatSession s = arena.Session;
        arena.Attack();
        Assert.True(arena.RunUntil(() => s.Hits > 0));

        Assert.False(arena.Monster.IsAlive);
        Assert.Equal(0, s.MonsterArmorAbsorbed);                         // 致命的一击先于霸体(决策函数第 3 步在第 4 步之前),不算吞掉
        Assert.NotEqual(MonsterPhase.Attack, arena.Monster.Phase);       // 走死亡流程
    }
}
