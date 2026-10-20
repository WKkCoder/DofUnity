using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物的倒地状态机(证据 E-082、E-083,静态读码):按体重缩放的击飞、z 抛物线、落地后颠一下、再躺下、动画放完起身;
/// 空中与倒地中再挨打、弹起类砸地再飞、撞墙反弹、命中停顿冻结、在空中被打死。体重取公式的中性点 10000,
/// 迷你怪物的倒地动画(帧 0、1 各 80 毫秒,帧 2、3 长停留,帧 4 躺着 500 毫秒)与原版地精的倒地动画同形;数值全是合成的。
/// 默认的一击:推力 212、抬升 215(类型 1,中性体重)——随机数取 0 时水平初速 160 像素/秒、高度 49 像素、z 初速 √(2 × 1500 × 49)。
/// </summary>
public class MonsterKnockdownTests
{
    private const int Neutral = 10000;
    private static readonly IWalkArea Free = new RectUnionArea(new List<WorldRect>());
    private static readonly string BoxedDown = AvatarFixture.BlobAnimationDir + "/downboxed.ani";

    private static MonsterActor Boxed(DnfAssets assets, double x = 400, double y = 100, int hp = 1000, int weight = Neutral, string mob = AvatarFixture.BlobBoxedDownMobPath)
    {
        MonsterModel model = MonsterModel.Read(assets, mob);
        var spawn = new RoomMonsterSpawn(1, 0, 0, (int)x, (int)y, 0, 1, 0, "[fixed]", "[normal]");
        return new MonsterActor(0, spawn, model, 3, hp, 222.5, 1500, 0, 0, weight);
    }

    private static bool Hit(MonsterActor m, int damage = 1, bool knockDown = true, int push = 212, int lift = 215, bool pushToLeft = false,
        bool bounce = false, bool ignoreWeight = false, bool reactionDamage = false, int rollH = 0, int rollV = 0)
    {
        return m.ApplyHit(damage, knockDown, push, pushToLeft, default, new KnockdownInput(lift, bounce, ignoreWeight, reactionDamage, rollH, rollV));
    }

    private static void Run(MonsterActor m, double ms, IWalkArea walk = null, double step = 10)
    {
        double left = ms;
        while (left > 0)
        {
            double d = left < step ? left : step;
            m.Update(d, 0, 0, false, walk ?? Free);
            left -= d;
        }
    }

    [Fact]
    public void AKnockdownFliesInAnArcHopsOnceLiesAndStandsUp()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets);
        Assert.False(Hit(m));
        Assert.True(m.IsKnockedDown);
        Assert.Equal((MonsterPhase.Hurt, 0, 0.0), (m.Phase, m.DownStage, m.Z));
        Assert.Equal(BoxedDown, m.CurrentAnimationPath);
        Assert.True(m.FacingLeft);                                        // 往右推:攻击方在左边,怪物转向攻击方
        Assert.Equal(Math.Sqrt(2.0 * 1500 * 49), m.VerticalSpeed, 2);
        Assert.Equal(160.0, m.HorizontalSpeed, 6);                        // 背朝攻击方飞出去:往右

        var stages = new List<int>();
        var framesByStage = new Dictionary<int, List<int>> { [0] = new List<int>(), [1] = new List<int>(), [2] = new List<int>() };
        double maxZ0 = 0, maxZ1 = 0;
        int steps = 0;
        while (m.IsKnockedDown && steps < 400)
        {
            Run(m, 10);
            steps++;
            if (!m.IsKnockedDown)
            {
                break;
            }
            int stage = m.DownStage;
            if (stages.Count == 0 || stages[stages.Count - 1] != stage)
            {
                stages.Add(stage);
            }
            framesByStage[stage].Add(m.CurrentFrame);
            if (stage == 0)
            {
                maxZ0 = Math.Max(maxZ0, m.Z);
            }
            else if (stage == 1)
            {
                maxZ1 = Math.Max(maxZ1, m.Z);
            }
        }
        Assert.Equal(new[] { 0, 1, 2 }, stages.ToArray());
        // 阶段 0:动画前两帧各 80 毫秒,之后停在第一个长停留帧(帧 2)直到落地
        Assert.Equal(0, framesByStage[0][0]);
        Assert.Equal(framesByStage[0].OrderBy(f => f).ToArray(), framesByStage[0].ToArray());   // 帧号只增不减
        Assert.Contains(1, framesByStage[0]);
        Assert.Equal(2, framesByStage[0][framesByStage[0].Count - 1]);
        Assert.DoesNotContain(3, framesByStage[0]);
        // 阶段 1:颠起时先是帧 2,过了最高点换成帧 3;阶段 2:躺着的帧 4
        Assert.Equal(2, framesByStage[1][0]);
        Assert.Equal(3, framesByStage[1][framesByStage[1].Count - 1]);
        Assert.All(framesByStage[2], f => Assert.Equal(4, f));
        Assert.InRange(maxZ0, 46.0, 49.1);                                // 高度 49(采样在每 10 毫秒,略低于最高点)
        Assert.InRange(maxZ1, 5.0, 10.0);                                 // 颠起 = 最高点(取整 48)÷ 5 = 9,再 × [0.9, 1.1] 取整
        Assert.True(framesByStage[0].Count >= 50 && framesByStage[0].Count <= 52);   // 2 × 383.4 ÷ 1500 = 0.511 秒
        Assert.InRange(framesByStage[2].Count, 48, 50);                   // 躺 500 毫秒

        Assert.False(m.IsKnockedDown);
        Assert.Equal((MonsterPhase.Wait, 0.0), (m.Phase, m.Z));
        Assert.InRange(m.X - 400, 90, 108);                               // 160 × 0.511 ≈ 82,加颠一下 [64, 96] × 0.21 ≈ 13 到 21
        Assert.InRange(steps * 10, 1180, 1290);
    }

    [Fact]
    public void AHeavierMonsterFliesLowerAndShorterForTheSameHit()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var heights = new List<double>();
        var distances = new List<double>();
        foreach (int weight in new[] { 0, Neutral, 40000 })
        {
            MonsterActor m = Boxed(assets, weight: weight);
            Hit(m, rollH: 500, rollV: 500);
            double maxZ = 0;
            for (int i = 0; i < 400 && m.IsKnockedDown; i++)
            {
                Run(m, 10);
                maxZ = Math.Max(maxZ, m.Z);
            }
            heights.Add(maxZ);
            distances.Add(m.X - 400);
        }
        Assert.True(heights[0] > heights[1] && heights[1] > heights[2], string.Join(",", heights));
        Assert.True(distances[0] > distances[1] && distances[1] > distances[2], string.Join(",", distances));

        // 不受体重影响的击飞(类型 0):轻重一样
        var same = new List<double>();
        foreach (int weight in new[] { 0, 40000 })
        {
            MonsterActor m = Boxed(assets, weight: weight);
            Hit(m, push: 300, lift: 60, ignoreWeight: true, rollH: 500, rollV: 500);
            double maxZ = 0;
            for (int i = 0; i < 400 && m.IsKnockedDown; i++)
            {
                Run(m, 10);
                maxZ = Math.Max(maxZ, m.Z);
            }
            same.Add(maxZ);
        }
        Assert.Equal(same[0], same[1], 9);
        Assert.InRange(same[0], 50, 70);                                  // 高度 60 × [0.9, 1.1]
    }

    [Fact]
    public void ANormalHitOnAnAirborneMonsterJugglesItInsteadOfMakingItFlinch()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets);
        Hit(m);
        Run(m, 200);
        double z0 = m.Z;
        Assert.True(z0 > 20);
        // 普通受击([damage reaction] = 1)、抬升 0、往左推:空中走倒地分支,速度清零后悬一下再下落,动画重新放,转向攻击方
        bool killed = Hit(m, knockDown: false, push: 0, lift: 0, pushToLeft: true, reactionDamage: true);
        Assert.False(killed);
        Assert.True(m.IsKnockedDown);
        Assert.Equal(0, m.DownStage);
        Assert.Equal(BoxedDown, m.CurrentAnimationPath);
        Assert.Equal(z0, m.Z, 9);
        Assert.Equal(0.0, m.VerticalSpeed);
        Assert.Equal(0, m.CurrentFrame);
        Assert.False(m.FacingLeft);
        Run(m, 10);
        Assert.True(m.VerticalSpeed < 0);
        Assert.True(m.Z < z0);
        Run(m, 400);                                                      // 落地、颠一下……最终躺下
        Assert.True(m.DownStage >= 1 || !m.IsKnockedDown);
    }

    [Fact]
    public void AGroundedMonsterHitByANormalAttackStillFlinches()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets);
        Hit(m, knockDown: false, push: 0, lift: 0, reactionDamage: true);
        Assert.False(m.IsKnockedDown);
        Assert.Equal((MonsterPhase.Hurt, 0.0), (m.Phase, m.Z));
        Assert.Equal(AvatarFixture.BlobAnimationDir + "/damage1.ani", m.CurrentAnimationPath);
        // [damage reaction] 不是普通受击、也不是倒地(比如只有伤害):不动
        MonsterActor other = Boxed(assets);
        Hit(other, knockDown: false, reactionDamage: false);
        Assert.False(other.IsKnockedDown);
    }

    [Fact]
    public void ALiftlessKnockdownLiesAtOnceAndGetsUpWhenTheLyingFrameEnds()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets);
        Hit(m, lift: 0);
        Assert.Equal(0.0, m.VerticalSpeed);
        Run(m, 10);                                                       // 在地面上没有向上的速度:一帧之内落地,没有高度可颠,直接躺下
        Assert.Equal((2, 4), (m.DownStage, m.CurrentFrame));
        Assert.Equal(0.0, m.Z);
        Run(m, 480);
        Assert.True(m.IsKnockedDown);
        Run(m, 20);
        Assert.False(m.IsKnockedDown);
        Assert.Equal(MonsterPhase.Wait, m.Phase);
        Assert.True(m.X > 400);                                           // 躺下之前沿推的方向滑了一点:水平速度没有摩擦,只持续了那一帧
    }

    [Fact]
    public void AHitOnALyingMonsterRelaunchesItFromStageOneWithAQuarterOfTheHeight()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets);
        Hit(m, lift: 0);
        Run(m, 200);
        Assert.Equal(2, m.DownStage);
        // 躺着又挨一下普通攻击(抬升 215,类型 1):输出二为 1,阶段 1;旧阶段 ≥ 1 所以高度只有四分之一:55 ÷ 4 = 13 → [11, 14];动画跳到第一个长停留帧
        Hit(m, knockDown: false, lift: 215, reactionDamage: true);
        Assert.Equal((1, 2), (m.DownStage, m.CurrentFrame));
        Assert.Equal(Math.Sqrt(2.0 * 1500 * 11), m.VerticalSpeed, 2);
        Run(m, 800);
        Assert.False(m.IsKnockedDown);                                    // 颠一下,落地后躺下,再起身
    }

    [Fact]
    public void ABounceSlamsIntoTheGroundThenFliesAgainWithTheStoredPushAndLift()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets);
        Hit(m, bounce: true);                                             // 类型 3:受体重影响的弹起
        Assert.Equal(0, m.DownStage);
        Assert.True(m.VerticalSpeed < 0);                                 // 先砸向地面
        Assert.Equal(0.0, m.Z);
        Run(m, 10);                                                       // 已经在地面:这一帧里落地,用存下的推力与抬升再飞一次(类型 1)
        Assert.Equal(0, m.DownStage);
        Assert.Equal(0.0, m.Z);
        Assert.True(m.VerticalSpeed > 300);
        Assert.InRange(m.HorizontalSpeed, 160, 241);
        Assert.Equal(2, m.CurrentFrame);                                  // 直接跳到空中那一帧
        Run(m, 10);
        Assert.True(m.Z > 0);
        Run(m, 2000);
        Assert.False(m.IsKnockedDown);
        Assert.True(m.X > 400);
    }

    [Fact]
    public void HitStopFreezesTheFlightAndTheAnimationClock()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets);
        Hit(m);
        m.Pause(100);
        Run(m, 100);
        Assert.Equal((0.0, 400.0, 0), (m.Z, m.X, m.CurrentFrame));
        Assert.Equal(Math.Sqrt(2.0 * 1500 * 49), m.VerticalSpeed, 2);
        Run(m, 50);
        Assert.True(m.Z > 0);
        Assert.True(m.X > 400);
    }

    [Fact]
    public void TheDamageBoxesTheSpriteAndTheHeadFollowTheHeight()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets);
        Assert.Equal(60, m.HeadHeight());
        Hit(m);
        Run(m, 250);                                                      // 接近最高点
        int z = (int)Math.Floor(m.Z);
        Assert.InRange(z, 45, 49);
        Aabb box = Assert.Single(m.DamageBoxes());
        Assert.Equal((z, z + 60), (box.MinZ, box.MaxZ));                  // 受击框 z ∈ [0, 60] 整体抬高
        Assert.Equal(60 + z, m.HeadHeight());
        // 站在地上的怪物够不着空中的怪物:同一位置、高度 0 的框与它不相交
        Assert.False(new Aabb(box.MinX, box.MaxX, box.MinY, box.MaxY, 0, 40).Overlaps(box));
    }

    [Fact]
    public void AWallBouncesAFlyingMonsterBackAtAFifthOfItsSpeed()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var box = new RectUnionArea(new List<WorldRect> { new WorldRect(0, 0, 420, 300) });
        MonsterActor m = Boxed(assets);
        Hit(m, lift: 400);                                                // 飞得久一点
        double vx = m.HorizontalSpeed;
        Assert.True(vx > 0);
        for (int i = 0; i < 50 && m.HorizontalSpeed > 0; i++)
        {
            Run(m, 10, box);
        }
        Assert.Equal(-Math.Truncate(vx / 5.0), m.HorizontalSpeed, 6);     // 反向、缩到五分之一
        Assert.True(m.X <= 420);
        Assert.True(m.IsKnockedDown);
    }

    [Fact]
    public void AKillingBlowWithoutLiftLiesAtOnceFlashesWhiteFadesOutAndVanishes()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets, hp: 10);
        Assert.True(Hit(m, damage: 50, knockDown: false, push: 30, lift: 0, reactionDamage: true));
        Assert.False(m.IsAlive);
        Assert.True(m.IsKnockedDown);                                     // 已死而且有倒地动作:走倒地流程(E-079 第 3 步),阶段 0 在下一步落地
        Assert.True(m.DeathSilhouette);                                   // 白闪从血量归零就开始
        Assert.Equal(255, m.DeathTintAlpha);
        Run(m, 10);                                                       // 没有抬升:这一帧落地,阶段 1 被强制成 2,已死 → 死亡(E-082)
        Assert.Equal((MonsterPhase.Dying, false), (m.Phase, m.IsKnockedDown));
        Assert.Equal(4, m.CurrentFrame);                                  // 躺着的那一帧
        Assert.True(m.DeathSilhouette);
        Assert.Equal(255, m.DeathTintAlpha);
        Assert.Empty(m.DamageBoxes());
        Run(m, 240);                                                      // 纯白剪影到血量归零后 250 毫秒
        Assert.Equal(255, m.DeathTintAlpha);
        Run(m, 125);                                                      // 375 毫秒:淡出到一半
        Assert.InRange(m.DeathTintAlpha, 126, 129);
        Assert.False(m.IsGone);
        Run(m, 130);
        Assert.True(m.IsGone);                                            // 血量归零后 500 毫秒消失
        Assert.False(m.DeathSilhouette);
    }

    [Fact]
    public void AKillingBlowWithAShortFlightDiesWhenTheMonsterLiesDownButTheClockStartedAtTheKill()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets, hp: 10);
        Assert.True(Hit(m, damage: 50, lift: 130));                       // 缩放后的抬升 247 → 高度约 18 像素:腾空 0.31 秒、颠一下 0.10 秒
        Assert.False(m.IsAlive);
        Assert.True(m.IsKnockedDown);
        Assert.True(m.DeathSilhouette);
        Assert.Empty(m.DamageBoxes());
        Assert.False(Hit(m));                                             // 尸体不会再挨打
        var stages = new List<int>();
        int steps = 0;
        while (m.Phase == MonsterPhase.Hurt && steps++ < 400)
        {
            Run(m, 10);
            if (m.IsKnockedDown && (stages.Count == 0 || stages[stages.Count - 1] != m.DownStage))
            {
                stages.Add(m.DownStage);
            }
        }
        Assert.Equal(new[] { 0, 1 }, stages.ToArray());                   // 腾空、颠一下;躺下的那一刻就死亡,没有躺地阶段
        Assert.Equal(MonsterPhase.Dying, m.Phase);
        Assert.Equal(0.0, m.Z);
        Assert.Equal(4, m.CurrentFrame);
        Assert.InRange(steps * 10, 380, 450);
        Assert.InRange(m.DeathElapsedMs, 380, 450);                       // 死亡的时钟是从血量归零算的,不是从进入死亡算的
        Run(m, 500 - m.DeathElapsedMs);
        Assert.True(m.IsGone);                                            // 血量归零后 500 毫秒
    }

    [Fact]
    public void AKillingBlowWithAHighLiftMakesTheCorpseFlashWhiteAndVanishInTheAir()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets, hp: 10);
        Assert.True(Hit(m, damage: 50));                                  // 默认一击:推力 212、抬升 215(类型 1):腾空 0.51 秒、颠一下 0.22 秒
        Assert.Equal(Math.Sqrt(2.0 * 1500 * 49), m.VerticalSpeed, 2);     // 与活着时飞得一样
        Assert.False(Hit(m));
        Run(m, 250);
        Assert.True(m.Z > 20);
        Assert.True(m.DeathSilhouette);                                   // 腾空的尸体也是白的
        Assert.Equal(255, m.DeathTintAlpha);
        Run(m, 130);                                                      // 380 毫秒:淡出到一半以后,还在飞
        Assert.InRange(m.DeathTintAlpha, 90, 160);
        Assert.True(m.Z > 0 && m.IsKnockedDown);
        Run(m, 130);                                                      // 510 毫秒:消失,没有落地、没有进入死亡
        Assert.True(m.IsGone);
        Assert.False(m.IsKnockedDown && m.Phase == MonsterPhase.Dying);
    }

    [Fact]
    public void AMonsterWithoutADownMotionJustDiesAtOnce()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets, hp: 10, mob: "monster/blob/big.mob");
        Assert.True(Hit(m, damage: 50));
        Assert.Equal((MonsterPhase.Dying, false), (m.Phase, m.IsKnockedDown));
        Assert.True(m.DeathSilhouette);
        Run(m, 510);
        Assert.True(m.IsGone);
    }

    [Fact]
    public void AMonsterKilledWithTheOldSignatureStillDiesWhereItStands()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets, hp: 10);
        Assert.True(m.ApplyHit(50, true, 30, false));                     // 老签名没有倒地输入:直接死亡
        Assert.Equal((MonsterPhase.Dying, false), (m.Phase, m.IsKnockedDown));
        Run(m, 510);
        Assert.True(m.IsGone);
    }

    [Fact]
    public void TheDyingSpriteIsAWhiteSilhouetteThatFadesWithTheTintAlpha()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets, hp: 10);
        var projection = new TownProjection(0, 200);
        SceneSprite alive = m.Sprite(assets, projection);
        Assert.NotNull(alive);
        Assert.Equal(255, alive.TintAlpha);
        Hit(m, damage: 50, knockDown: false, push: 0, lift: 0, reactionDamage: true);
        Run(m, 10);
        SceneSprite white = m.Sprite(assets, projection);
        Assert.NotNull(white);
        Assert.NotSame(alive.Frame, white.Frame);
        for (int i = 0; i < white.Frame.Rgba.Length; i += 4)
        {
            if (white.Frame.Rgba[i + 3] != 0)
            {
                Assert.Equal((255, 255, 255), (white.Frame.Rgba[i], white.Frame.Rgba[i + 1], white.Frame.Rgba[i + 2]));
            }
        }
        Run(m, 365);                                                      // 血量归零后 375 毫秒
        SceneSprite fading = m.Sprite(assets, projection);
        Assert.InRange(fading.TintAlpha, 126, 129);
        Assert.Same(white.Frame, fading.Frame);                           // 同一帧不会反复生成新的剪影(Unity 按帧对象缓存贴图)
    }

    [Fact]
    public void AMonsterWithoutAnyLongFramesStillRunsTheWholeMachineByTime()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets, mob: AvatarFixture.BlobShortDownMobPath);
        Hit(m);
        var stages = new List<int>();
        int steps = 0;
        while (m.IsKnockedDown && steps++ < 400)
        {
            Run(m, 10);
            if (m.IsKnockedDown && (stages.Count == 0 || stages[stages.Count - 1] != m.DownStage))
            {
                stages.Add(m.DownStage);
            }
        }
        Assert.Equal(new[] { 0, 1, 2 }.Take(stages.Count).ToArray(), stages.ToArray());
        Assert.False(m.IsKnockedDown);                                    // 动画(240 毫秒)早在腾空时放完,躺下就是一帧
        Assert.Equal(MonsterPhase.Wait, m.Phase);
        Assert.InRange(steps * 10, 600, 900);
    }

    [Fact]
    public void WithoutAKnockdownInputTheOldPlaceholderPathRunsAndLandsAFlyingMonster()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Boxed(assets);
        Hit(m);
        Run(m, 200);
        Assert.True(m.Z > 0);
        m.ApplyHit(1, false, 30, false);                                  // 老的签名没有倒地输入:占位硬直,落到地上
        Assert.False(m.IsKnockedDown);
        Assert.Equal(0.0, m.Z);
        Assert.Equal(MonsterPhase.Hurt, m.Phase);
    }
}
