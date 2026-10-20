using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 房间里的怪物(证据 E-069):追击、出招、受击硬直与击退、死亡与消失、受击框。动画与数据全是合成的(迷你怪物:受击动画是一帧停 10000 毫秒,
/// 与原版的受击动画同形),数值检验的是我们自己写下的规则与量到的取值,不是原版数据。
/// </summary>
public class MonsterActorTests
{
    private const string Stay = AvatarFixture.BlobAnimationDir + "/stay.ani";
    private const string Move = AvatarFixture.BlobAnimationDir + "/move.ani";
    private const string Damage1 = AvatarFixture.BlobAnimationDir + "/damage1.ani";
    private const string Damage2 = AvatarFixture.BlobAnimationDir + "/damage2.ani";
    private const string Down = AvatarFixture.BlobAnimationDir + "/down.ani";
    private const string Attack1 = AvatarFixture.BlobAnimationDir + "/attack1.ani";

    private static readonly IWalkArea Free = new RectUnionArea(new List<WorldRect>());

    /// <summary>测试里怪物的水平行走速度(像素/秒,与主角同速),竖直取 90。</summary>
    private const double WalkSpeed = 222.5;

    private static MonsterActor Blob(DnfAssets assets, double x, double y, int hp = 100, int attackDelay = 1500, int attackSpeed = 0,
        string mob = AvatarFixture.BlobMobPath, bool boss = false)
    {
        MonsterModel model = MonsterModel.Read(assets, mob);
        var spawn = new RoomMonsterSpawn(1, 0, 0, (int)x, (int)y, 0, 1, 0, "[fixed]", boss ? "[boss]" : "[normal]");
        return new MonsterActor(0, spawn, model, 3, hp, WalkSpeed, attackDelay, attackSpeed);
    }

    private static void Run(MonsterActor m, double ms, double heroX, double heroY, bool heroPresent = true, IWalkArea walk = null, double step = 10)
    {
        double left = ms;
        while (left > 0)
        {
            double d = left < step ? left : step;
            m.Update(d, heroX, heroY, heroPresent, walk ?? Free);
            left -= d;
        }
    }

    [Fact]
    public void ANewMonsterStandsAtItsSpawnPointWithFullHealthFacingLeft()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 340, 100, hp: 35, boss: true);
        Assert.Equal((340.0, 100.0), (m.X, m.Y));
        Assert.Equal((35, 35, 3, 0), (m.Hp, m.HpMax, m.Level, m.Index));
        Assert.True(m.IsAlive);
        Assert.True(m.IsBoss);
        Assert.True(m.FacingLeft);                              // 进房间时朝左(原版进图画面,证据 E-069)
        Assert.Equal(MonsterPhase.Wait, m.Phase);
        Assert.Equal(Stay, m.CurrentAnimationPath);
        Assert.Equal(222.5, m.SpeedX, 9);
        Assert.Equal(90.0, m.SpeedY, 9);
        Assert.Equal(1500, m.AttackDelayMs);
        Assert.Equal(1.0, m.AttackRate);
        // 血量最少 1;没有 [attack delay](0)时用缺省
        Assert.Equal(1, Blob(assets, 0, 0, hp: -5).HpMax);
        Assert.Equal(MonsterTuning.DefaultAttackDelayMs, Blob(assets, 0, 0, attackDelay: 0).AttackDelayMs);
    }

    [Fact]
    public void AMonsterOutOfSightOrWithoutAHeroJustStands()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor far = Blob(assets, 340, 100);
        Run(far, 500, 340 + MonsterTuning.SightX + 5, 100);             // 水平方向在视野外
        Assert.Equal((MonsterPhase.Wait, 340.0, 100.0), (far.Phase, far.X, far.Y));
        Run(far, 500, 340, 100 + MonsterTuning.SightY + 5);              // 竖直方向在视野外
        Assert.Equal((MonsterPhase.Wait, 340.0, 100.0), (far.Phase, far.X, far.Y));

        MonsterActor alone = Blob(assets, 340, 100);
        Run(alone, 500, 300, 100, heroPresent: false);                   // 没有主角:待机
        Assert.Equal((MonsterPhase.Wait, 340.0, 100.0), (alone.Phase, alone.X, alone.Y));
    }

    [Fact]
    public void AMonsterInSightWalksTowardsTheHeroAtTheScaledMoveSpeedAndFacesIt()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 600, 100);                         // 主角在 x = 300:朝左走
        Run(m, 500, 300, 160);
        Assert.Equal(MonsterPhase.Walk, m.Phase);
        Assert.True(m.FacingLeft);
        Assert.Equal(Move, m.CurrentAnimationPath);
        Assert.Equal(600 - 222.5 * 0.5, m.X, 6);                         // 0.5 秒走 111.25 像素
        Assert.Equal(100 + 90.0 * 0.5, m.Y, 6);                          // 同时朝下走 45 像素(主角在下面 60 像素)……
        // 再走 1 秒:水平离主角 70 像素以内、竖直离 16 像素以内就停下出招(这两个距离是近似,未量),不会越过主角
        Run(m, 1000, 300, 160);
        Assert.NotEqual(MonsterPhase.Walk, m.Phase);
        Assert.InRange(m.X, 367, 371);
        Assert.InRange(m.Y, 144, 160);

        MonsterActor right = Blob(assets, 100, 100);                     // 主角在右边:朝右走并转身
        Run(right, 200, 400, 100);
        Assert.False(right.FacingLeft);
        Assert.Equal(100 + 222.5 * 0.2, right.X, 6);
    }

    [Fact]
    public void AMonsterNeverLeavesTheWalkableArea()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var box = new RectUnionArea(new List<WorldRect> { new WorldRect(0, 0, 400, 300) });
        MonsterActor m = Blob(assets, 350, 100);
        Run(m, 3000, 600, 100, walk: box);                               // 主角在区域外(右边):怪物走到边界就停
        Assert.InRange(m.X, 350, 400);
        Assert.True(box.Contains(m.X, m.Y));
        Assert.True(m.X > 390);
    }

    [Fact]
    public void AMonsterStopsAndStrikesWhenNearThenRestsForItsCooldownBeforeStrikingAgain()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        // [attack speed] 400:出招动画 80 × 3 = 240 毫秒按 0.4 倍速播放,墙钟 600 毫秒;[attack delay] 1500
        MonsterActor m = Blob(assets, 360, 100, attackDelay: 1500, attackSpeed: 400);
        Assert.Equal(0.4, m.AttackRate, 9);
        var phases = new List<MonsterPhase>();
        var frames = new List<int>();
        for (int k = 0; k < 260; k++)
        {
            m.Update(10, 300, 100, true, Free);
            phases.Add(m.Phase);
            frames.Add(m.CurrentFrame);
        }
        Assert.Equal(360.0, m.X);                                         // 够近:不再走
        Assert.Equal(MonsterPhase.Attack, phases[0]);
        Assert.Equal(MonsterPhase.Attack, phases[59]);
        Assert.Equal(MonsterPhase.Wait, phases[60]);                      // 600 毫秒后收招
        Assert.Equal(MonsterPhase.Wait, phases[209]);                     // 冷却 1500 毫秒
        Assert.Equal(MonsterPhase.Attack, phases[210]);                   // 再次出招
        // 出招动画按 0.4 倍速:第 0 帧(80 毫秒)占墙钟 200 毫秒,第 1 帧在 200 到 400 毫秒,第 2 帧在 400 毫秒之后
        Assert.Equal(0, frames[19]);
        Assert.Equal(1, frames[20]);
        Assert.Equal(1, frames[39]);
        Assert.Equal(2, frames[40]);
        Assert.Equal(Attack1, m.CurrentAnimationPath);
    }

    [Fact]
    public void AMonsterWithoutAnAttackMotionNeverStrikesAndJustWaitsWhenNear()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 340, 100, mob: "monster/blob/big.mob");
        Run(m, 500, 300, 100);
        Assert.Equal(MonsterPhase.Wait, m.Phase);                         // 靠近了(40 像素)但没有攻击动作
        Assert.Equal(Stay, m.CurrentAnimationPath);
        // 没有行走动画时走路也不出错(回退到待机动画)
        MonsterActor walker = Blob(assets, 600, 100, mob: "monster/blob/big.mob");
        Run(walker, 100, 300, 100);
        Assert.Equal(MonsterPhase.Walk, walker.Phase);
        Assert.Equal(Stay, walker.CurrentAnimationPath);
    }

    [Fact]
    public void AHitTakesHealthAlternatesTheHurtAnimationsAndHoldsForTheMeasuredSpan()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 200, 100, hp: 40);

        Assert.False(m.ApplyHit(10, false, 0, true));
        Assert.Equal(30, m.Hp);
        Assert.Equal(MonsterPhase.Hurt, m.Phase);
        Assert.Equal(Damage1, m.CurrentAnimationPath);
        // 受击动画只有一帧停 10000 毫秒:硬直取量到的时长(命中停顿之外的部分),不能跟着动画停 10 秒
        Assert.Equal(MonsterTuning.HurtHoldMs, m.HurtSpanMs);
        double hold = MonsterTuning.HurtHoldMs;

        Run(m, hold - 1, 5000, 100, heroPresent: false);
        Assert.Equal(MonsterPhase.Hurt, m.Phase);
        Run(m, 1, 5000, 100, heroPresent: false);
        Assert.Equal(MonsterPhase.Wait, m.Phase);

        // 受击动作一、二交替;连续挨打时硬直重新计时
        m.ApplyHit(1, false, 0, true);
        Assert.Equal(Damage2, m.CurrentAnimationPath);
        Run(m, 300, 5000, 100, heroPresent: false);
        m.ApplyHit(1, false, 0, true);
        Assert.Equal(Damage1, m.CurrentAnimationPath);
        Assert.Equal(0L, m.PhaseMs);
        Run(m, hold - 1, 5000, 100, heroPresent: false);
        Assert.Equal(MonsterPhase.Hurt, m.Phase);                         // 距离第二下还差 1 毫秒:仍在硬直

        // 被击倒:放倒地动画,在地上停得更久
        m.ApplyHit(1, true, 0, true);
        Assert.Equal(Down, m.CurrentAnimationPath);
        Assert.Equal(MonsterTuning.KnockDownHoldMs, m.HurtSpanMs);
        Run(m, 899, 5000, 100, heroPresent: false);
        Assert.Equal(MonsterPhase.Hurt, m.Phase);
        Run(m, 1, 5000, 100, heroPresent: false);
        Assert.Equal(MonsterPhase.Wait, m.Phase);
        Assert.Equal(40 - 10 - 1 - 1 - 1, m.Hp);
    }

    [Fact]
    public void AHurtAnimationWithARealDurationSetsTheSpanButNotBelowTheFloor()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        // 只有待机动画(两帧 × 100 毫秒 = 200 毫秒)的怪物挨打:用待机动画当受击动画,硬直取下限 280 毫秒
        MonsterActor m = Blob(assets, 200, 100, mob: "monster/blob/big.mob");
        m.ApplyHit(1, false, 0, true);
        Assert.Equal(Stay, m.CurrentAnimationPath);
        Assert.Equal(MonsterTuning.MinHurtMs, m.HurtSpanMs);
        // 没有倒地动画时,"被击倒"按普通受击处理
        m.ApplyHit(1, true, 0, true);
        Assert.Equal(MonsterTuning.MinHurtMs, m.HurtSpanMs);
    }

    [Fact]
    public void ThePushEasesOutOverTheEaseTimeAndStopsAtTheTotalDistance()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 200, 100);
        m.ApplyHit(1, false, 30, true);                                   // 向左推 30 像素
        Run(m, 150, 5000, 100, heroPresent: false);                       // t = 0.5:1 − 0.25 = 75% = 22.5
        Assert.Equal(200 - 22.5, m.X, 6);
        Run(m, 150, 5000, 100, heroPresent: false);                       // t = 1:全部 30 像素
        Assert.Equal(170.0, m.X, 6);
        Run(m, 10, 5000, 100, heroPresent: false);                        // 硬直结束前不再多推
        Assert.Equal(170.0, m.X, 6);

        MonsterActor right = Blob(assets, 200, 100);
        right.ApplyHit(1, false, 12, false);                              // 向右推
        Run(right, 400, 5000, 100, heroPresent: false);
        Assert.Equal(212.0, right.X, 6);
        Assert.Equal(100.0, right.Y);
    }

    // ------------------------------------------------------------------ 受击硬直公式(证据 E-080):表、倍率与受击恢复全是合成数值

    private static readonly HitStunTable SyntheticTable = new HitStunTable(10, 330, 1000);

    private static MonsterActor WithRecovery(DnfAssets assets, double x, double y, int recovery, int hp = 100)
    {
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, (int)x, (int)y, 0, 1, 0, "[fixed]", "[normal]");
        return new MonsterActor(0, spawn, model, 3, hp, WalkSpeed, 1500, 0, recovery);
    }

    [Fact]
    public void TheFormulaHurtLastsTheComputedTimeFromTheMomentOfTheHit()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = WithRecovery(assets, 200, 100, recovery: 500);
        Assert.Equal(500, m.HitRecovery);

        Assert.False(m.ApplyHit(10, false, 999, true, new HitStunInput(SyntheticTable, 1.0f)));
        Assert.Equal(MonsterPhase.Hurt, m.Phase);
        Assert.True(m.HurtFromFormula);
        Assert.Equal(170.0, m.HurtSpanMs);                      // 10 + (330 − 10) × (1000 − 500) / 1000
        Run(m, 169, 5000, 100, heroPresent: false);
        Assert.Equal(MonsterPhase.Hurt, m.Phase);
        Run(m, 1, 5000, 100, heroPresent: false);
        Assert.Equal(MonsterPhase.Wait, m.Phase);

        // 受击恢复高的怪物硬直短:恢复到顶只剩下限;再高也一样
        MonsterActor tough = WithRecovery(assets, 200, 100, recovery: 5000);
        tough.ApplyHit(1, false, 0, true, new HitStunInput(SyntheticTable, 1.0f));
        Assert.Equal(10.0, tough.HurtSpanMs);

        // 倍率乘在整个硬直上
        MonsterActor slow = WithRecovery(assets, 200, 100, recovery: 500);
        slow.ApplyHit(1, false, 0, true, new HitStunInput(SyntheticTable, 0.5f));
        Assert.Equal(85.0, slow.HurtSpanMs);
    }

    [Fact]
    public void TheHitStopFreezesTheAnimationButNotTheFormulaHurtClock()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = WithRecovery(assets, 200, 100, recovery: 500);     // 硬直 170
        m.ApplyHit(1, false, 0, true, new HitStunInput(SyntheticTable, 1.0f));
        m.Pause(100);

        Run(m, 100, 5000, 100, heroPresent: false);                          // 停顿的 100 毫秒:动画时间不走,硬直秒表走
        Assert.Equal(0L, m.PhaseMs);
        Assert.Equal(MonsterPhase.Hurt, m.Phase);
        Run(m, 69, 5000, 100, heroPresent: false);
        Assert.Equal(MonsterPhase.Hurt, m.Phase);                             // 总共 169 毫秒
        Run(m, 1, 5000, 100, heroPresent: false);
        Assert.Equal(MonsterPhase.Wait, m.Phase);                             // 170 毫秒:停顿算在硬直里,不是停顿之后再 170

        // 停顿比硬直还长:停顿里状态不变,停顿一结束(下一步)就恢复
        MonsterActor frozen = WithRecovery(assets, 200, 100, recovery: 500);
        frozen.ApplyHit(1, false, 0, true, new HitStunInput(SyntheticTable, 1.0f));
        frozen.Pause(300);
        Run(frozen, 300, 5000, 100, heroPresent: false);
        Assert.Equal(MonsterPhase.Hurt, frozen.Phase);
        Run(frozen, 10, 5000, 100, heroPresent: false);
        Assert.Equal(MonsterPhase.Wait, frozen.Phase);

        // 占位硬直(没给表)照旧:停顿之后才开始计
        MonsterActor placeholder = WithRecovery(assets, 200, 100, recovery: 500);
        placeholder.ApplyHit(1, false, 0, true);
        Assert.False(placeholder.HurtFromFormula);
        placeholder.Pause(100);
        Run(placeholder, 100 + MonsterTuning.HurtHoldMs - 1, 5000, 100, heroPresent: false);
        Assert.Equal(MonsterPhase.Hurt, placeholder.Phase);
        Run(placeholder, 1, 5000, 100, heroPresent: false);
        Assert.Equal(MonsterPhase.Wait, placeholder.Phase);
    }

    [Fact]
    public void TheFormulaKnockbackIgnoresPushAsideAndEasesOutOverItsOwnDuration()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = WithRecovery(assets, 200, 100, recovery: 0);        // 硬直 330,比 150 长:默认击退整整 150 毫秒
        m.ApplyHit(1, false, 999, true, new HitStunInput(SyntheticTable, 1.0f));      // [push aside] = 999 在受击里不用
        HitStunResult expected = HitStun.Damage(SyntheticTable, 0, 0, 1.0f, 0, 0, false);
        Assert.InRange(expected.KnockPixels, 30.0, 30.02);

        Run(m, 75, 5000, 100, heroPresent: false);                            // 约一半时间:位移比例约 1 − 0.25
        Assert.InRange(m.X, 177.48, 177.52);
        Run(m, 200, 5000, 100, heroPresent: false);
        Assert.Equal(200 - expected.KnockPixels, m.X, 6);
        Assert.Equal(100.0, m.Y);

        // 往右打就往右退;命中模式是灼烧(不击退)时不动
        MonsterActor right = WithRecovery(assets, 200, 100, recovery: 0);
        right.ApplyHit(1, false, 0, false, new HitStunInput(SyntheticTable, 1.0f));
        Run(right, 300, 5000, 100, heroPresent: false);
        Assert.Equal(200 + expected.KnockPixels, right.X, 6);

        MonsterActor burn = WithRecovery(assets, 200, 100, recovery: 0);
        burn.ApplyHit(1, false, 30, true, new HitStunInput(SyntheticTable, 1.0f, noKnock: true));
        Run(burn, 300, 5000, 100, heroPresent: false);
        Assert.Equal(200.0, burn.X);
    }

    [Fact]
    public void AKnockDownAndAKillKeepThePlaceholderPaths()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = WithRecovery(assets, 200, 100, recovery: 0, hp: 40);
        m.ApplyHit(1, true, 30, true, new HitStunInput(SyntheticTable, 1.0f));      // 倒地:停留与击退仍是占位
        Assert.Equal(Down, m.CurrentAnimationPath);
        Assert.False(m.HurtFromFormula);
        Assert.Equal(MonsterTuning.KnockDownHoldMs, m.HurtSpanMs);
        Run(m, MonsterTuning.PushEaseMs, 5000, 100, heroPresent: false);
        Assert.Equal(170.0, m.X, 6);                                                // [push aside] 30 照旧推满

        // 没有倒地动画时"被击倒"退成普通受击(E-079 第 7 步):按公式算
        var big = new MonsterActor(0, new RoomMonsterSpawn(1, 0, 0, 200, 100, 0, 1, 0, "[fixed]", "[normal]"),
            MonsterModel.Read(assets, "monster/blob/big.mob"), 3, 100, WalkSpeed, 1500, 0, 500);
        big.ApplyHit(1, true, 0, true, new HitStunInput(SyntheticTable, 1.0f));
        Assert.True(big.HurtFromFormula);
        Assert.Equal(170.0, big.HurtSpanMs);

        // 一击打死:进死亡,不走硬直
        MonsterActor dying = WithRecovery(assets, 200, 100, recovery: 0, hp: 5);
        Assert.True(dying.ApplyHit(50, false, 0, true, new HitStunInput(SyntheticTable, 1.0f)));
        Assert.Equal(MonsterPhase.Dying, dying.Phase);
        Assert.False(dying.HurtFromFormula);
    }

    [Fact]
    public void AForcedStunTimeReplacesTheComputedOne()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = WithRecovery(assets, 200, 100, recovery: 500);
        m.ApplyHit(1, false, 0, true, new HitStunInput(SyntheticTable, 1.0f, forceStunMs: 77));
        Assert.Equal(77.0, m.HurtSpanMs);
    }

    [Fact]
    public void AMonsterCannotBePushedOutOfTheWalkableArea()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var box = new RectUnionArea(new List<WorldRect> { new WorldRect(100, 0, 300, 300) });
        MonsterActor m = Blob(assets, 110, 100);
        m.ApplyHit(1, false, 60, true);                                   // 向左推 60,但左边只剩 10 像素
        Run(m, 400, 5000, 100, heroPresent: false, walk: box);
        Assert.True(box.Contains(m.X, m.Y));
        Assert.InRange(m.X, 100, 110);
    }

    [Fact]
    public void AKillPlaysTheFallingAnimationFromTheFirstFrameAndTheMonsterVanishesAfterTheDeathTime()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 200, 100, hp: 20);
        Assert.False(m.ApplyHit(19, false, 0, true));
        Assert.True(m.ApplyHit(5, false, 0, true));                       // 血量夹在 0,返回这一下打死了
        Assert.Equal((0, false, MonsterPhase.Dying), (m.Hp, m.IsAlive, m.Phase));
        Assert.Equal(Down, m.CurrentAnimationPath);
        Assert.Equal(0, m.CurrentFrame);
        Assert.Empty(m.DamageBoxes());                                    // 死了就没有受击框

        Run(m, 100, 5000, 100, heroPresent: false);
        Assert.Equal(1, m.CurrentFrame);                                  // 倒地:第 0 帧 80 毫秒,之后停在第 1 帧(停留帧)
        Run(m, 399, 5000, 100, heroPresent: false);                       // 共 499 毫秒:还在
        Assert.Equal(MonsterPhase.Dying, m.Phase);
        Assert.NotNull(m.Sprite(assets, new TownProjection(0, 200)));
        Run(m, 1, 5000, 100, heroPresent: false);
        Assert.Equal((MonsterPhase.Gone, true), (m.Phase, m.IsGone));
        Assert.Null(m.Sprite(assets, new TownProjection(0, 200)));

        // 打死之后再挨打、再更新都没有效果
        Assert.False(m.ApplyHit(50, false, 0, true));
        m.Update(100, 200, 100, true, Free);
        Assert.Equal((0, MonsterPhase.Gone), (m.Hp, m.Phase));
    }

    [Fact]
    public void TheDamageBoxIsMirroredWithTheFacingAndOnlyExistsWhileAlive()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 200, 100);                          // 朝左;受击框 (-10, -6, 0, 40, 12, 60)
        Aabb left = Assert.Single(m.DamageBoxes());
        Assert.Equal(new Aabb(200 - 30, 200 + 10, 94, 106, 0, 60), left);

        Run(m, 20, 400, 100);                                             // 主角在右边:转向右
        Assert.False(m.FacingLeft);
        Aabb right = Assert.Single(m.DamageBoxes());
        int wx = (int)Math.Floor(m.X);
        Assert.Equal(new Aabb(wx - 10, wx + 30, 94, 106, 0, 60), right);

        Assert.Equal(60, m.HeadHeight());                                 // 待机第 0 帧受击框的顶:z + 高
    }

    [Fact]
    public void TheSpriteFollowsTheFacingTheCurrentFrameAndTheWorldPosition()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var projection = new TownProjection(10, 200);
        MonsterActor m = Blob(assets, 200, 100);
        SceneSprite left = m.Sprite(assets, projection);
        Assert.NotNull(left);
        Assert.True(left.Mirrored);                                       // 原图头朝右:朝左要翻转
        Assert.Equal(MonsterSprites.ObjectIdBase + m.Index, left.NpcId);
        Assert.Equal(Stay, left.AnimationPath);
        Assert.Equal(100, left.SortKey);
        Assert.Equal(200 - 10 - (-10 - 3 + 6), left.ScreenX);

        Run(m, 20, 400, 100);
        SceneSprite right = m.Sprite(assets, projection);
        Assert.False(right.Mirrored);
        Assert.Equal((int)Math.Floor(m.X) - 10 - 10 - 3, right.ScreenX);
    }
    [Fact]
    public void AMonsterThatIsPausedHoldsEveryClockUntilThePauseRunsOut()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 340, 100);
        Run(m, 100, 100, 100);                                            // 主角在远处左边:追过去,先走一小段
        double x0 = m.X;
        long phase0 = m.PhaseMs;
        Assert.Equal(MonsterPhase.Walk, m.Phase);
        m.Pause(60);
        Assert.Equal(60.0, m.PauseMs, 9);
        Run(m, 50, 100, 100);                                             // 停顿里:位置、阶段计时都不动
        Assert.Equal((x0, phase0), (m.X, m.PhaseMs));
        Assert.Equal(10.0, m.PauseMs, 9);
        m.Update(30, 100, 100, true, Free);                               // 这一步里停顿结束:剩下的 20 毫秒照常走
        Assert.Equal(0.0, m.PauseMs, 9);
        Assert.Equal(phase0 + 20, m.PhaseMs);
        Assert.True(m.X < x0);                                            // 继续往主角走(主角在左边)
    }

    [Fact]
    public void PausesDoNotStackAndTheLongerRemainderWins()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 340, 100);
        m.Pause(100);
        m.Pause(40);                                                      // 更短的不改
        Assert.Equal(100.0, m.PauseMs, 9);
        m.Update(35, 700, 100, true, Free);
        Assert.Equal(65.0, m.PauseMs, 9);
        m.Pause(80);                                                      // 剩余 65 < 80:换成 80,不是 145
        Assert.Equal(80.0, m.PauseMs, 9);
        m.Pause(0);
        m.Pause(-5);
        Assert.Equal(80.0, m.PauseMs, 9);
    }

    [Fact]
    public void AHitMonsterIsPausedBeforeItsHurtReactionRuns()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Blob(assets, 340, 100);
        m.ApplyHit(10, false, 30, false);
        m.Pause(90);
        Run(m, 90, 300, 100);
        Assert.Equal((MonsterPhase.Hurt, 0L), (m.Phase, m.PhaseMs));      // 受击动画停在第一帧,硬直计时还没开始
        Run(m, 100, 300, 100);
        Assert.Equal(100L, m.PhaseMs);
        Assert.True(m.X > 340);                                           // 击退在停顿结束后才开始推
    }
}
