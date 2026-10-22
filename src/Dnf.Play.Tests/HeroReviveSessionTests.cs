using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 房间战斗会话里的主角死亡、复活窗口、复活与无敌(证据 E-115):窗口在主角躺平的那一刻才开、攻击键就是用币键、复活回满血并起身、
/// 起身与之后一小段时间怪物打不到、没币或计时走完就失败、画面上用倒计时取代占位的 DEAD。
/// 数据全是合成的:场景沿用 <see cref="HeroKnockdownTests"/> 的迷你职业与会出倒地招的迷你怪物(每一击 25 点伤害,主角血量上限 20,一击就死)。
/// </summary>
public class HeroReviveSessionTests
{
    private const int Damage = 25;

    private static HeroKnockdownTests.Arena DyingArena(ReviveCoins coins, DungeonCoinRules rules = null, int attackDelayMs = 100000, string chr = null)
    {
        var arena = new HeroKnockdownTests.Arena(HeroKnockdownTests.Strike(down: true), hpMax: 20, chr: chr, attackDelayMs: attackDelayMs);
        arena.Session.Revive = new HeroRevive(rules ?? DungeonCoinRules.Default, coins);
        return arena;
    }

    private static bool Mentions(IEnumerable<SceneSprite> sprites, string prefix)
    {
        return sprites.Any(s => (s.AnimationPath ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ 窗口

    [Fact]
    public void TheWindowOpensOnlyOnceTheDeadHeroLiesStill()
    {
        using var arena = DyingArena(new ReviveCoins(1));
        RoomCombatSession s = arena.Session;
        HeroRevive revive = s.Revive;
        Assert.Equal((false, false), (revive.Open, s.CanRevive));

        arena.RunToFirstHit();
        Assert.True(s.HeroDead);
        Assert.True(arena.Down.Active);
        Assert.Equal((false, 0), (revive.Open, revive.Deaths));              // 还在飞:服务端要等躺下的死亡通知,窗口没开

        Assert.True(arena.RunUntil(() => revive.Open));
        Assert.False(arena.Down.Active);                                      // 躺平的那一刻开窗
        Assert.Equal((true, 1, 10, true), (revive.Open, revive.Deaths, revive.Seconds, s.CanRevive));
    }

    [Fact]
    public void AHeroWithoutADownMotionOpensTheWindowRightAfterTheKillingBlowAndStandsUpFromTheDamagePose()
    {
        var coins = new ReviveCoins(1);
        using var arena = DyingArena(coins, chr: AvatarFixture.HurtOnlyChrPath);
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        arena.Steps(1);
        Assert.Equal((true, true), (s.HeroDead, s.Revive.Open));
        Assert.Equal(HeroMotion.Hurt, arena.Hero.Motion);                    // 占位的死亡:停在受击姿势上

        s.Update(10, 0, 0, true, false);
        Assert.Equal((1, 0, false), (s.Revived, coins.Count, s.HeroDead));
        Assert.Equal(HeroMotion.Rest, arena.Hero.Motion);                    // 结束受击姿势:站起来了
    }

    // ------------------------------------------------------------------ 复活

    [Fact]
    public void PressingAttackWhileTheWindowIsOpenUsesACoinRestoresTheHeroAndStandsHimUp()
    {
        var coins = new ReviveCoins(2);
        using var arena = DyingArena(coins);
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        Assert.True(arena.RunUntil(() => s.Revive.Open));
        arena.Steps(100);                                                     // 过了一秒
        Assert.Equal(9, s.Revive.Seconds);
        Assert.Equal(0, s.Vitals.Hp);

        s.Update(10, 1, 1, true, true);                                       // 死人只认复活键:方向与跳跃都被丢掉
        Assert.Equal((1, 1, 1, false), (s.Revived, coins.Count, s.Revive.Used, s.Revive.Open));
        Assert.False(s.HeroDead);
        Assert.Equal(s.Vitals.HpMax, s.Vitals.Hp);
        Assert.Equal(s.Vitals.MpMax, s.Vitals.Mp);
        Assert.True(s.HeroInvincible);
        Assert.True(arena.Hero.IsDown);                                       // 先躺着:躺满之后起身,不是一复活就站起来
        Assert.True(arena.Down.Active);

        Assert.True(arena.RunUntil(() => !arena.Hero.IsDown));
        Assert.Equal((HeroMotion.Rest, 0.0), (arena.Hero.Motion, arena.Hero.Z));
        s.Update(10, 1, 0, false, false);
        Assert.Equal(HeroMotion.Move, arena.Hero.Motion);                     // 起身之后恢复接受输入
    }

    [Fact]
    public void AttackKeyPressedBeforeTheWindowOpensDoesNotSpendACoin()
    {
        var coins = new ReviveCoins(2);
        using var arena = DyingArena(coins);
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        Assert.True(arena.Down.Active);
        for (int i = 0; i < 10; i++)
        {
            s.Update(10, 0, 0, true, false);                                  // 倒地飞行里连按攻击键:窗口还没开,不会用币
        }
        Assert.Equal((0, 2, false), (s.Revived, coins.Count, s.Revive.Open));
        Assert.True(arena.Down.Active);
    }

    [Fact]
    public void TheRevivedHeroIsInvincibleForAWhileThenCanBeHitAndKilledAgainWhichOpensAFreshWindow()
    {
        var coins = new ReviveCoins(3);
        using var arena = DyingArena(coins, attackDelayMs: 200);
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        Assert.True(arena.RunUntil(() => s.Revive.Open));
        s.Update(10, 0, 0, true, false);
        Assert.False(s.HeroDead);
        int hits = s.HeroHits;

        arena.Steps(250);                                                     // 2.5 秒:怪物就站在旁边接着出招,但一下也打不中
        Assert.Equal((hits, true, false), (s.HeroHits, s.HeroInvincible, s.HeroDead));

        Assert.True(arena.RunUntil(() => !s.HeroInvincible));
        Assert.True(arena.RunUntil(() => s.HeroHits > hits, 600));            // 无敌过了,怪物又打得到了
        Assert.True(s.HeroDead);
        Assert.True(arena.RunUntil(() => s.Revive.Open));
        Assert.Equal((2, 10, 1), (s.Revive.Deaths, s.Revive.Seconds, s.Revive.Used));   // 新窗口重新数 10 秒,已用次数接着累计
        Assert.False(s.Revive.Failed);
    }

    [Fact]
    public void TryReviveHeroIsTheSameRouteForCallersWithoutKeys()
    {
        var coins = new ReviveCoins(1);
        using var arena = DyingArena(coins);
        RoomCombatSession s = arena.Session;
        Assert.False(s.TryReviveHero());                                      // 活着:没有可复活的
        arena.RunToFirstHit();
        Assert.False(s.TryReviveHero());                                      // 窗口还没开
        Assert.Equal(1, coins.Count);
        Assert.True(arena.RunUntil(() => s.Revive.Open));
        Assert.True(s.TryReviveHero());
        Assert.Equal((0, 1, false), (coins.Count, s.Revived, s.HeroDead));
        Assert.False(s.TryReviveHero());                                      // 窗口关了
    }

    [Fact]
    public void FreeRevivalNeverRunsOutOfCoins()
    {
        var rules = new DungeonCoinRules(0, 1, new Dictionary<int, (int Limit, bool Pay)> { [0] = (0, false) });
        var coins = new ReviveCoins(0);
        using var arena = DyingArena(coins, rules, attackDelayMs: 200);
        RoomCombatSession s = arena.Session;
        for (int round = 1; round <= 2; round++)
        {
            int before = s.HeroHits;
            Assert.True(arena.RunUntil(() => s.HeroHits > before, 800));
            Assert.True(arena.RunUntil(() => s.Revive.Open));
            Assert.Equal(CoinVerdict.Free, s.Revive.Verdict);
            s.Update(10, 0, 0, true, false);
            Assert.Equal((round, 0, 0), (s.Revived, coins.Count, s.Revive.Used));
            Assert.True(arena.RunUntil(() => !s.HeroInvincible, 800));
        }
    }

    // ------------------------------------------------------------------ 失败

    [Fact]
    public void WithoutACoinTheKeyDoesNothingAndTheCountdownEndsInFailure()
    {
        var coins = new ReviveCoins(0);
        using var arena = DyingArena(coins);
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        Assert.True(arena.RunUntil(() => s.Revive.Open));
        Assert.Equal((CoinVerdict.NoCoin, false), (s.Revive.Verdict, s.CanRevive));
        for (int i = 0; i < 20; i++)
        {
            s.Update(10, 0, 0, true, false);
        }
        Assert.Equal((true, 0, true), (s.HeroDead, s.Revived, s.Revive.Open));

        Assert.True(arena.RunUntil(() => s.Revive.Failed, 1100));
        Assert.Equal((false, true, true), (s.Revive.Open, s.Revive.Failed, s.HeroDead));
        s.Update(10, 0, 0, true, false);
        arena.Steps(50);
        Assert.Equal((0, true), (s.Revived, s.HeroDead));                     // 失败之后按键也没用
    }

    [Fact]
    public void ARoomWithoutAReviveObjectKeepsTheOldBehaviourAndNeverFails()
    {
        using var arena = new HeroKnockdownTests.Arena(HeroKnockdownTests.Strike(down: true), hpMax: 20);
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        arena.Steps(1500);
        Assert.Equal((true, false, 0), (s.HeroDead, s.CanRevive, s.Revived));
        Assert.False(s.TryReviveHero());
        Assert.Contains(s.Build(0), x => x.AnimationPath == "hero-dead");     // 老行为:一直躺着、画 DEAD
        Assert.False(Mentions(s.Build(0), "revive"));
    }

    // ------------------------------------------------------------------ 画面

    [Fact]
    public void TheDeathScreenShowsTheCountdownWhileTheWindowIsOpenAndNothingBeforeOrAfter()
    {
        var coins = new ReviveCoins(3);
        using var arena = DyingArena(coins);
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        List<SceneSprite> flying = s.Build(0);
        Assert.False(Mentions(flying, "revive"));                             // 还在飞:窗口没开,什么也不画
        Assert.DoesNotContain(flying, x => x.AnimationPath == "hero-dead");

        Assert.True(arena.RunUntil(() => s.Revive.Open));
        List<SceneSprite> open = s.Build(0);
        Assert.Contains(open, x => x.AnimationPath == "revive-digit");
        Assert.Contains(open, x => x.AnimationPath == "revive-prompt");
        Assert.Contains(open, x => x.AnimationPath == "revive-stock");
        Assert.DoesNotContain(open, x => x.AnimationPath == "hero-dead");     // 取代了占位的 DEAD

        coins.Add(-3);                                                        // 币没了:提示换成"没有币"(不同的文字、不同的图)
        SpriteFrame withCoin = open.First(x => x.AnimationPath == "revive-prompt").Frame;
        SpriteFrame without = s.Build(0).First(x => x.AnimationPath == "revive-prompt").Frame;
        Assert.NotSame(withCoin, without);

        Assert.True(arena.RunUntil(() => s.Revive.Failed, 1100));
        Assert.False(Mentions(s.Build(0), "revive"));                         // 失败之后窗口关了:不画倒计时
    }

    [Fact]
    public void TheCountdownDigitsFollowTheSecondsLeft()
    {
        using var arena = DyingArena(new ReviveCoins(1));
        RoomCombatSession s = arena.Session;
        arena.RunToFirstHit();
        Assert.True(arena.RunUntil(() => s.Revive.Open));
        SpriteFrame ten = s.Build(0).First(x => x.AnimationPath == "revive-digit").Frame;
        arena.Steps(100);
        SpriteFrame nine = s.Build(0).First(x => x.AnimationPath == "revive-digit").Frame;
        Assert.NotSame(ten, nine);
        Assert.True(ten.Width > nine.Width);                                  // 占位版是一整串像素字:两位数比一位数宽
    }

    // ------------------------------------------------------------------ 测试旋钮

    [Fact]
    public void TheMonsterDamageScaleMultipliesWhatTheHeroTakes()
    {
        using var plain = new HeroKnockdownTests.Arena(HeroKnockdownTests.Strike(down: true));
        plain.RunToFirstHit();
        Assert.Equal(1000 - Damage, plain.Session.Vitals.Hp);

        using var scaled = new HeroKnockdownTests.Arena(HeroKnockdownTests.Strike(down: true));
        scaled.Session.MonsterDamageScale = 7;
        scaled.RunToFirstHit();
        Assert.Equal(1000 - Damage * 7, scaled.Session.Vitals.Hp);
        Assert.Equal(Damage * 7, scaled.Session.HeroDamageTaken);
    }
}
