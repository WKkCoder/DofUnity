using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 装了 [attack kind] 记录的怪物怎样出招(证据 E-100、E-101):出生后 3 秒内不出招、到点才抽签、按 [attack delay] 加抖动的节奏重复、
/// 记录里有射程范围的招在射程里停下并出招、挑了第几招就放第几招的动画与攻击信息。动画与数据全是合成的。
/// 移动与视野仍是占位,所以这里不检验它们与原版的关系。
/// </summary>
public class MonsterAiTests
{
    private const double WalkSpeed = 222.5;
    private static readonly IWalkArea Free = new RectUnionArea(new List<WorldRect>());

    private static MonsterActor Make(DnfAssets assets, string mob, double x, double y, int attackDelay = 1500, int attackSpeed = 400)
    {
        MonsterModel model = MonsterModel.Read(assets, mob);
        var spawn = new RoomMonsterSpawn(1, 0, 0, (int)x, (int)y, 0, 1, 0, "[fixed]", "[normal]");
        return new MonsterActor(0, spawn, model, 3, 100, WalkSpeed, attackDelay, attackSpeed);
    }

    /// <summary>没有射程范围(全 0)的基础攻击,另两个概率是 0(数值是合成的)。</summary>
    private static AttackKindRecord Basic()
    {
        return new AttackKindRecord(15f, 0f, 1f, true, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>正面 200、背面 20、纵向各 30;射程里 100% 出招。</summary>
    private static AttackKindRecord Ranged(bool ready = true)
    {
        return new AttackKindRecord(100f, 0f, 0f, ready, 200, 0, 20, 0, 30, 0, 30, 0);
    }

    private static List<MonsterPhase> Run(MonsterActor m, int steps, double heroX, double heroY, double step = 10)
    {
        var phases = new List<MonsterPhase>();
        for (int k = 0; k < steps; k++)
        {
            m.Update(step, heroX, heroY, true, Free);
            phases.Add(m.Phase);
        }
        return phases;
    }

    private static List<int> AttackStarts(List<MonsterPhase> phases)
    {
        var starts = new List<int>();
        for (int i = 0; i < phases.Count; i++)
        {
            if (phases[i] == MonsterPhase.Attack && (i == 0 || phases[i - 1] != MonsterPhase.Attack))
            {
                starts.Add(i);
            }
        }
        return starts;
    }

    [Fact]
    public void ABasicAttackWaitsForTheSpawnLockoutThenStrikesAtTheFirstDecisionAndRepeatsAboutOneDelayAfterThat()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Make(assets, AvatarFixture.BlobMobPath, 360, 100);
        m.SetAttackKinds(new[] { Basic() }, 12345u);
        Assert.NotNull(m.AttackChooser);
        // 主角在 60 像素外(固定射程内):旧占位在第 0 步就出招;装了记录后要等出生满 3 秒(AI 时钟 3000 毫秒)
        List<MonsterPhase> phases = Run(m, 700, 300, 100);
        List<int> starts = AttackStarts(phases);
        Assert.True(starts.Count >= 2);
        Assert.Equal(299, starts[0]);                                   // 第 300 步结束时时钟 = 3000 毫秒:第一次决策,基础攻击够得着(推断的几何判断)就出招
        Assert.Equal(0, m.AttackIndex);
        Assert.Equal(360.0, m.X);
        // [attack delay] 1500 的抖动范围 ±150:第一次决策后下一次在 4350 到 4650 毫秒之间(出招动画 600 毫秒在 3600 毫秒结束,不会挡住)
        Assert.InRange(starts[1], 434, 464);
        Assert.Equal(MonsterPhase.Wait, phases[starts[0] - 1]);
    }

    [Fact]
    public void WithoutRecordsTheOldPlaceholderStillStrikesAtOnceAndWithRecordsTheStrikeNeverComesEarly()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor legacy = Make(assets, AvatarFixture.BlobMobPath, 360, 100);
        Assert.Null(legacy.AttackChooser);
        Assert.Equal(MonsterPhase.Attack, Run(legacy, 1, 300, 100)[0]);

        MonsterActor aware = Make(assets, AvatarFixture.BlobMobPath, 360, 100);
        aware.SetAttackKinds(new[] { Basic() }, 7u);
        Assert.DoesNotContain(MonsterPhase.Attack, Run(aware, 299, 300, 100));      // 时钟 2990 毫秒以内:一次出招也没有
        Assert.Equal(2990L, aware.AiClockMs);
    }

    [Fact]
    public void ARangedRecordStopsTheApproachOnceInRangeThenStrikesAfterTheLockout()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Make(assets, AvatarFixture.BlobMobPath, 600, 100);
        m.SetAttackKinds(new[] { Ranged() }, 99u);
        // 主角在 x = 300、宽 40(四分之一 10):朝左的正面射程 200,监测点 x ≤ 510 时进入射程
        List<MonsterPhase> phases = Run(m, 100, 300, 100);
        Assert.Equal(MonsterPhase.Wait, m.Phase);                       // 走到射程里就停(旧占位要走到 70 像素以内)
        Assert.InRange(m.X, 508.0, 511.0);
        Assert.DoesNotContain(MonsterPhase.Attack, phases);              // 出生才 1 秒:还在 3 秒的禁区里
        List<MonsterPhase> later = Run(m, 250, 300, 100);
        Assert.Contains(MonsterPhase.Attack, later);                     // 3 秒之后第一次决策:射程里 100% 出招
        Assert.True(m.AiClockMs >= 3000);
    }

    [Fact]
    public void TheSameSeedPlaysTheSameFightAndADifferentSeedCanDiffer()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        // 射程里只有 25%:每次决策掷骰,序列由种子决定
        var record = new AttackKindRecord(25f, 0f, 0f, true, 200, 0, 20, 0, 30, 0, 30, 0);
        List<MonsterPhase> One(uint seed)
        {
            MonsterActor m = Make(assets, AvatarFixture.BlobMobPath, 360, 100, attackDelay: 1000);
            m.SetAttackKinds(new[] { record }, seed);
            return Run(m, 2000, 300, 100);
        }
        Assert.Equal(One(5u), One(5u));
        bool anyDifferent = false;
        for (uint seed = 6; seed < 14 && !anyDifferent; seed++)
        {
            anyDifferent = !One(5u).SequenceEqual(One(seed));
        }
        Assert.True(anyDifferent);
    }

    [Fact]
    public void TheChosenAttackPlaysItsOwnAnimationAndUsesItsOwnStrikeInfo()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Make(assets, AvatarFixture.BlobDuoMobPath, 360, 100);
        // 第 0 招没准备好,第 1 招在射程里 100%:只能选第 1 招,放它的动画(Strike.ani)与攻击信息
        m.SetAttackKinds(new[] { Ranged(ready: false), Ranged() }, 3u);
        m.Strike = new HeroStrikeInfo(11, 0, false);
        m.SetStrikes(new[] { new HeroStrikeInfo(11, 0, false), new HeroStrikeInfo(77, 0, false) });
        Assert.Equal(11, m.Strike.DamageBonus);                         // 还没出招:当前招是第 0 招
        List<MonsterPhase> phases = Run(m, 330, 300, 100);
        Assert.Contains(MonsterPhase.Attack, phases);
        Assert.Equal(1, m.AttackIndex);
        Assert.Equal(77, m.Strike.DamageBonus);
        // 出招阶段播的是第 1 个攻击动作
        MonsterActor probe = Make(assets, AvatarFixture.BlobDuoMobPath, 360, 100);
        probe.SetAttackKinds(new[] { Ranged(ready: false), Ranged() }, 3u);
        for (int k = 0; k < 400 && probe.Phase != MonsterPhase.Attack; k++)
        {
            probe.Update(10, 300, 100, true, Free);
        }
        Assert.Equal(MonsterPhase.Attack, probe.Phase);
        Assert.Equal(AvatarFixture.BlobStrikeAni, probe.CurrentAnimationPath);
    }

    [Fact]
    public void ARecordWithoutARangeOnASpecialAttackNeverFiresFromTheGeometricFallback()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor m = Make(assets, AvatarFixture.BlobDuoMobPath, 360, 100);
        // 第 0 招没准备好;第 1 招没有范围、不论远近的概率 0:几何判断只对第 0 招用,所以永远不会被选中
        m.SetAttackKinds(new[] { Ranged(ready: false), Basic() }, 3u);
        Assert.DoesNotContain(MonsterPhase.Attack, Run(m, 800, 300, 100));
        Assert.InRange(m.X, 305.0, 309.0);                                // 没有一招够得着:占位的走位一直贴近到最小间隔,不会穿过主角
    }

    [Fact]
    public void RecordsOfAMonsterWithoutAnyAttackAnimationLeaveThePlaceholderInChargeAndTheClockSkipsHitStop()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        MonsterActor none = Make(assets, "monster/blob/big.mob", 340, 100);
        none.SetAttackKinds(new[] { Ranged() }, 1u);
        Assert.Null(none.AttackChooser);                                  // 一招动画都没有:不装决策器
        Run(none, 100, 300, 100);
        Assert.Equal(MonsterPhase.Wait, none.Phase);

        MonsterActor m = Make(assets, AvatarFixture.BlobMobPath, 360, 100);
        m.Pause(500);
        m.Update(300, 300, 100, true, Free);                              // 全在停顿里:怪物的时钟不走
        Assert.Equal(0L, m.AiClockMs);
        m.Update(400, 300, 100, true, Free);                              // 停顿还剩 200,之后走 200
        Assert.Equal(200L, m.AiClockMs);
        Assert.Equal((40, 20, 0), (m.TargetWidth, m.TargetDepth, m.AttackIndex));
    }
}
