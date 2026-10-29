using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物默认 AI 的模式机补全(证据 E-148):绕背冲刺、追击与迟疑、敌对 10 秒超时与再接战、被远处打中后的靠近(终点类别、视野 ×1.5、霸体、到位转换)、
/// 被挡住后的反向与追击,以及接进怪物演员之后的边走边出招。数全是合成的,随机数用可变的固定值或脚本序列替身。
/// </summary>
public class MonsterBrainModesTests
{
    /// <summary>可以中途改值的固定随机数。</summary>
    private sealed class Dial
    {
        public int Value;

        public Dial(int value)
        {
            Value = value;
        }

        public Func<int> AsFunc() => () => Value;
    }

    private static MonsterBrainTraits Traits(bool melee = true, bool ranged = false, bool passive = false, bool carefully = false,
        bool tough = false, bool manace = false, bool fullSuperArmor = false, int bodyWidth = 40)
    {
        return new MonsterBrainTraits(melee, ranged, passive, carefully, tough, manace, fullSuperArmor, bodyWidth);
    }

    private static MonsterBrain NewBrain(Func<int> random, MonsterBrainTraits traits, int sight = 200, int warlike = 50, int roomWidth = 2000)
    {
        return new MonsterBrain(sight, warlike, traits, roomWidth, false, random);
    }

    /// <summary>主角在 (300, 100)、怪物在 (100, 100)(相距 200 在视野 280 之内),随机数 10(&lt; 50:发现之后绕背),发现并过了迟滞:进了绕背冲刺。</summary>
    private static MonsterBrain FlankingBrain(Dial dial, int warlike = 50, MonsterBrainTraits? traits = null)
    {
        MonsterBrain b = NewBrain(dial.AsFunc(), traits ?? Traits(), 200, warlike);
        b.Update(0, true, 100, 100, 300, 100);
        b.Update(MonsterBrain.NoticeDelayMs, true, 100, 100, 300, 100);
        return b;
    }

    // ------------------------------------------------------------------ 绕背冲刺

    [Fact]
    public void ADiscoveryThatRollsBelowFiftyStartsAFlankRunToTheFarSideOfTheTarget()
    {
        var dial = new Dial(10);
        MonsterBrain b = FlankingBrain(dial);
        Assert.Equal(MonsterMoveMode.Flank, b.Mode);
        Assert.Equal(300 + 100, b.DestinationX);                        // 目标 x + 视野 × 0.5
        Assert.Equal(MonsterMoveRules.Right, b.DirX);
        Assert.Equal(MonsterMoveRules.None, b.DirY);
        Assert.True(b.IsEngaged);
        Assert.False(b.IsHostile);                                      // 绕背不算敌对:出招概率加成与好战度加成都没有
    }

    [Fact]
    public void ADiscoveryThatRollsFiftyOrMoreGoesStraightIntoMeleeHostility()
    {
        MonsterBrain b = FlankingBrain(new Dial(50));
        Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);
    }

    [Fact]
    public void RangedMonstersNeverFlankEvenWhenTheRollWouldAllowIt()
    {
        MonsterBrain ranged = NewBrain(new Dial(10).AsFunc(), Traits(melee: false, ranged: true));
        ranged.Update(0, true, 100, 100, 300, 100);
        ranged.Update(MonsterBrain.NoticeDelayMs, true, 100, 100, 300, 100);
        Assert.Equal(MonsterMoveMode.HostileRanged, ranged.Mode);

        // 近战与远程两个类别都有的怪物也不绕背,走近战
        MonsterBrain both = NewBrain(new Dial(10).AsFunc(), Traits(melee: true, ranged: true));
        both.Update(0, true, 100, 100, 300, 100);
        both.Update(MonsterBrain.NoticeDelayMs, true, 100, 100, 300, 100);
        Assert.Equal(MonsterMoveMode.HostileMelee, both.Mode);
    }

    [Fact]
    public void ARunnerThatPassesTheDestinationTurnsHostileAndOneThatHasNotYetKeepsRunning()
    {
        var dial = new Dial(10);
        MonsterBrain b = FlankingBrain(dial);
        dial.Value = 99;
        b.Update(1100, true, 399, 100, 300, 100);                       // 终点 400:还差 1 像素
        Assert.Equal(MonsterMoveMode.Flank, b.Mode);
        b.Update(1500, true, 400, 100, 300, 100);                       // 到了
        Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);
        Assert.True(b.IsHostile);
    }

    [Fact]
    public void ARunnerThatLosesTheTargetGoesBackToWandering()
    {
        var dial = new Dial(10);
        MonsterBrain b = FlankingBrain(dial);
        dial.Value = 99;
        b.Update(1100, true, 300, 100, 100 + 900, 100);                 // 主角跑出了视野
        Assert.False(b.HasTarget);
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
    }

    [Fact]
    public void BeingHitDuringTheRunTurnsItIntoAChaseAndAFarHitThenStartsTheApproach()
    {
        var dial = new Dial(10);
        MonsterBrain near = FlankingBrain(dial);
        Assert.Equal(MonsterMoveMode.Flank, near.Mode);
        dial.Value = 99;
        near.Update(800, true, 250, 100, 300, 100);                     // 冲到了离主角 50 像素的地方
        near.NotifyDamaged();
        Assert.Equal(MonsterMoveMode.Chase, near.Mode);                 // 近处挨打:只是转追击

        dial.Value = 10;
        MonsterBrain far = FlankingBrain(dial);
        dial.Value = 99;
        far.NotifyDamaged();                                            // 相距 200 ≥ 150:先转追击,再进靠近
        Assert.Equal(MonsterMoveMode.Approach, far.Mode);
        Assert.Equal(300, far.Sight);
    }

    [Fact]
    public void HittingTheHeroDuringTheRunTurnsItIntoAChaseButNotWhileHostile()
    {
        var dial = new Dial(10);
        MonsterBrain hitHero = FlankingBrain(dial);
        Assert.Equal(MonsterMoveMode.Flank, hitHero.Mode);
        dial.Value = 99;
        hitHero.NotifyHitHero();
        Assert.Equal(MonsterMoveMode.Chase, hitHero.Mode);

        dial.Value = 99;
        MonsterBrain hostile = FlankingBrain(dial);
        Assert.Equal(MonsterMoveMode.HostileMelee, hostile.Mode);
        hostile.NotifyHitHero();
        Assert.Equal(MonsterMoveMode.HostileMelee, hostile.Mode);       // 不在冲刺或靠近里,打中主角不改模式
    }

    // ------------------------------------------------------------------ 追击与迟疑

    [Fact]
    public void AChaseWalksTowardTheHeroEightyPercentOfTheTimeAndStandsOtherwise()
    {
        var dial = new Dial(10);
        MonsterBrain b = FlankingBrain(dial, warlike: 100);
        dial.Value = 79;
        b.NotifyHitHero();                                              // 冲刺里打中了主角 → 追击,马上选一次方向:掷骰 79 < 80 → 走
        Assert.Equal(MonsterMoveMode.Chase, b.Mode);
        Assert.Equal(MonsterMoveRules.Right, b.DirX);                   // 朝主角那一格走
        Assert.Equal(1000 + 79 % 1000, b.NextTickMs - 700);             // 走着的间隔:1000 + 随机数 % 1000

        dial.Value = 10;
        MonsterBrain b2 = FlankingBrain(dial, warlike: 100);
        dial.Value = 80;
        b2.NotifyHitHero();
        Assert.Equal(MonsterMoveMode.Chase, b2.Mode);
        Assert.False(b2.Moving);                                        // 掷骰 80:站着
    }

    [Fact]
    public void AChaseHasNoWarlikeBonusSoAFiftyWarlikeMonsterSometimesWalksBackwardsButNotWhenHostile()
    {
        // 随机数 60:追击的掷骰(< 80)通过;好战度 50 + 加成 0 ≤ 60 → 取反
        var dial = new Dial(10);
        MonsterBrain chase = FlankingBrain(dial, warlike: 50);
        dial.Value = 60;
        chase.NotifyHitHero();
        Assert.Equal(MonsterMoveMode.Chase, chase.Mode);
        Assert.Equal(MonsterMoveRules.Left, chase.DirX);                // 主角在右边,却朝左走

        // 同样的随机数在敌对模式里:50 + 50 > 60 → 不取反
        MonsterBrain hostile = NewBrain(new Dial(60).AsFunc(), Traits(), 200, 50);
        hostile.Update(0, true, 100, 100, 300, 100);
        hostile.Update(MonsterBrain.NoticeDelayMs, true, 100, 100, 300, 100);
        Assert.Equal(MonsterMoveMode.HostileMelee, hostile.Mode);
        Assert.Equal(MonsterMoveRules.Right, hostile.DirX);
    }

    [Fact]
    public void AChaseWhoseTargetIsGoneGoesBackToWandering()
    {
        var dial = new Dial(10);
        MonsterBrain b = FlankingBrain(dial);
        dial.Value = 99;
        b.NotifyHitHero();
        Assert.Equal(MonsterMoveMode.Chase, b.Mode);
        b.Update(1100, true, 100, 100, 100 + 900, 100);
        Assert.False(b.HasTarget);
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
    }

    // ------------------------------------------------------------------ 敌对的 10 秒超时与再接战

    [Fact]
    public void HostilityEndsAfterTenSecondsAndTheMonsterDoesNotEngageAgainWhileTheTargetStaysInSight()
    {
        MonsterBrain b = NewBrain(new Dial(99).AsFunc(), Traits());
        b.Update(0, true, 100, 100, 300, 100);
        b.Update(MonsterBrain.NoticeDelayMs, true, 100, 100, 300, 100);
        Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);
        long start = b.ModeStartMs;

        for (long t = start; t <= start + 9900; t += 100)
        {
            b.Update(t, true, 100, 100, 300, 100);
            Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);         // 进入满 10001 毫秒之前一直是敌对
        }
        for (long t = start + 10000; t <= start + 10600; t += 100)
        {
            b.Update(t, true, 100, 100, 300, 100);
        }
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        Assert.True(b.HasTarget);                                       // 目标还在视野里,但不会自动再接战
        for (long t = start + 10700; t <= start + 40000; t += 100)
        {
            b.Update(t, true, 100, 100, 300, 100);
            Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        }
    }

    [Fact]
    public void ALostAndFoundAgainTargetStartsAFreshEngagementAfterTheOldOneTimedOut()
    {
        MonsterBrain b = NewBrain(new Dial(99).AsFunc(), Traits());
        b.Update(0, true, 100, 100, 300, 100);
        b.Update(MonsterBrain.NoticeDelayMs, true, 100, 100, 300, 100);
        long start = b.ModeStartMs;
        long t = start;
        for (; t <= start + 11000; t += 100)
        {
            b.Update(t, true, 100, 100, 300, 100);
        }
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);

        b.Update(t, true, 100, 100, 100 + 900, 100);                    // 主角离开视野:目标没了
        Assert.False(b.HasTarget);
        long lost = t;
        for (t = lost + 100; t <= lost + MonsterMoveRules.FindIntervalMs - 100; t += 100)
        {
            b.Update(t, true, 100, 100, 300, 100);                      // 回来了,但找目标要隔一秒
            Assert.False(b.HasTarget);
        }
        for (; t <= lost + MonsterMoveRules.FindIntervalMs + MonsterBrain.NoticeDelayMs + 400; t += 100)
        {
            b.Update(t, true, 100, 100, 300, 100);
        }
        Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);
    }

    [Fact]
    public void TheModeUpdateRunsOnlyEveryThreeHundredThirtyThreeMilliseconds()
    {
        MonsterBrain b = NewBrain(new Dial(99).AsFunc(), Traits());
        b.Update(0, true, 100, 100, 300, 100);
        b.Update(MonsterBrain.NoticeDelayMs, true, 100, 100, 300, 100);
        long start = b.ModeStartMs;
        long seenWander = -1;
        for (long t = start; t <= start + 12000 && seenWander < 0; t++)
        {
            b.Update(t, true, 100, 100, 300, 100);
            if (b.Mode == MonsterMoveMode.Wander)
            {
                seenWander = t;
            }
        }
        Assert.InRange(seenWander - start, MonsterMoveRules.HostileTimeoutMs, MonsterMoveRules.HostileTimeoutMs + MonsterMoveRules.ModeUpdateIntervalMs + 1);
    }

    // ------------------------------------------------------------------ 被远处打中之后靠近

    [Fact]
    public void AHitFromBeyondArmsLengthStartsTheApproachWithTheLongerSightAndAHitFromCloseByDoesNot()
    {
        MonsterBrain near = NewBrain(new Dial(99).AsFunc(), Traits());
        near.Update(0, true, 100, 100, 100 + 149, 100);
        near.NotifyDamaged();
        Assert.NotEqual(MonsterMoveMode.Approach, near.Mode);
        Assert.Equal(200, near.Sight);

        MonsterBrain far = NewBrain(new Dial(99).AsFunc(), Traits(), sight: 200);
        far.Update(0, true, 100, 100, 400, 100);                        // 相距 300:在视野 280 之外,没有目标
        Assert.False(far.HasTarget);
        far.NotifyDamaged();
        Assert.Equal(MonsterMoveMode.Approach, far.Mode);
        Assert.Equal(300, far.Sight);                                   // 视野 × 1.5
        Assert.Equal((400, 100), (far.DestinationX, far.DestinationY)); // 没有强悍、凶猛、谨慎、被动类别:奔目标所在
        Assert.Equal(MonsterMoveRules.Right, far.DirX);

        MonsterBrain exact = NewBrain(new Dial(99).AsFunc(), Traits());
        exact.Update(0, true, 100, 100, 100 + 150, 100);
        exact.NotifyDamaged();
        Assert.Equal(MonsterMoveMode.Approach, exact.Mode);             // 正好 150 算"远"
    }

    [Fact]
    public void ADamageNotificationBeforeAnyUpdateOrWhileAlreadyApproachingDoesNothing()
    {
        MonsterBrain fresh = NewBrain(new Dial(99).AsFunc(), Traits());
        fresh.NotifyDamaged();
        Assert.Equal(MonsterMoveMode.Wander, fresh.Mode);

        MonsterBrain b = NewBrain(new Dial(99).AsFunc(), Traits(), sight: 200);
        b.Update(0, true, 100, 100, 400, 100);
        b.NotifyDamaged();
        Assert.Equal(300, b.Sight);
        b.NotifyDamaged();
        Assert.Equal(300, b.Sight);                                     // 没有叠加 ×1.5
    }

    [Fact]
    public void TheSightGoesBackWhenTheApproachEndsAndAnUnreachedApproachGivesUpAfterFiveSecondsWithoutATarget()
    {
        MonsterBrain b = NewBrain(new Dial(99).AsFunc(), Traits(), sight: 200);
        b.Update(0, true, 100, 100, 700, 100);                          // 相距 600:连 420 的放大视野也看不到
        b.NotifyDamaged();
        Assert.Equal(MonsterMoveMode.Approach, b.Mode);
        Assert.Equal(300, b.Sight);
        for (long t = 400; t <= 4800; t += 400)
        {
            b.Update(t, true, 100, 100, 700, 100);
            Assert.Equal(MonsterMoveMode.Approach, b.Mode);             // 没目标但还没满 5 秒
        }
        for (long t = 5200; t <= 6000; t += 400)
        {
            b.Update(t, true, 100, 100, 700, 100);
        }
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        Assert.Equal(200, b.Sight);
    }

    [Fact]
    public void ApproachDestinationsFollowTheCategoryOfTheMonster()
    {
        // 强悍:直奔目标;凶猛:停在目标靠自己这一侧横向差的五分之一处,纵向偏 50;被动与谨慎:另一侧、横向差一半处
        MonsterBrain tough = NewBrain(new Dial(99).AsFunc(), Traits(tough: true));
        tough.Update(0, true, 100, 100, 400, 100);
        tough.NotifyDamaged();
        Assert.Equal((400, 100), (tough.DestinationX, tough.DestinationY));

        MonsterBrain manace = NewBrain(new Dial(99).AsFunc(), Traits(manace: true));
        manace.Update(0, true, 100, 100, 400, 100);
        manace.NotifyDamaged();
        Assert.Equal((400 - 60, 100 + 50), (manace.DestinationX, manace.DestinationY));

        // 谨慎:随机数 0 → 偏左、偏上,幅度 0 % span = 0;横向差 |400 − 100 + 10| / 2 = 155
        MonsterBrain careful = NewBrain(new Dial(0).AsFunc(), Traits(carefully: true));
        careful.Update(0, true, 100, 100, 400, 100);
        careful.NotifyDamaged();
        Assert.Equal((400 - 155, 100), (careful.DestinationX, careful.DestinationY));

        // 终点横坐标被夹在房间之内
        MonsterBrain edge = NewBrain(new Dial(1).AsFunc(), Traits(passive: true), roomWidth: 500);
        edge.Update(0, true, 100, 100, 400, 100);
        edge.NotifyDamaged();                                           // 随机数 1 → 偏右:400 + 155 = 555 → 夹到 500
        Assert.Equal(500, edge.DestinationX);
    }

    [Fact]
    public void ToughAndManaceMonstersAreSuperArmoredOnlyWhileApproaching()
    {
        foreach (MonsterBrainTraits traits in new[] { Traits(tough: true), Traits(manace: true) })
        {
            MonsterBrain b = NewBrain(new Dial(99).AsFunc(), traits);
            b.Update(0, true, 100, 100, 400, 100);
            Assert.False(b.ApproachSuperArmor);
            b.NotifyDamaged();
            Assert.Equal(MonsterMoveMode.Approach, b.Mode);
            Assert.True(b.ApproachSuperArmor);
        }

        MonsterBrain plain = NewBrain(new Dial(99).AsFunc(), Traits());
        plain.Update(0, true, 100, 100, 400, 100);
        plain.NotifyDamaged();
        Assert.False(plain.ApproachSuperArmor);

        MonsterBrain already = NewBrain(new Dial(99).AsFunc(), Traits(tough: true, fullSuperArmor: true));
        already.Update(0, true, 100, 100, 400, 100);
        already.NotifyDamaged();
        Assert.False(already.ApproachSuperArmor);                       // 本来就一直霸体的不另开
    }

    [Fact]
    public void AMeleeApproachArrivesTurnsHostileAndTheDestinationFollowsTheHeroInTheMeantime()
    {
        MonsterBrain b = NewBrain(new Dial(99).AsFunc(), Traits(), sight: 200);
        b.Update(0, true, 100, 100, 400, 100);
        b.NotifyDamaged();
        Assert.Equal(MonsterMoveMode.Approach, b.Mode);
        Assert.Equal(300, b.Sight);

        // 1000 毫秒:找到目标(放大的视野里);终点跟到主角当前的位置
        b.Update(1000, true, 200, 100, 460, 100);
        Assert.True(b.HasTarget);
        Assert.Equal(460, b.DestinationX);

        // 到了终点(横向差小于 宽度/2 + 10 = 30)之后的下一拍:不绕背的话转近战敌对,视野还原
        b.Update(2200, true, 450, 100, 460, 100);
        Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);
        Assert.Equal(200, b.Sight);
    }

    [Fact]
    public void ApproachingPassiveAndCarefulMonstersGoBackToWanderingWhenTheyArrive()
    {
        foreach (MonsterBrainTraits traits in new[] { Traits(passive: true), Traits(carefully: true) })
        {
            MonsterBrain b = NewBrain(new Dial(99).AsFunc(), traits, sight: 200);
            b.Update(0, true, 100, 100, 400, 100);
            b.NotifyDamaged();
            Assert.Equal(MonsterMoveMode.Approach, b.Mode);
            // 靠近途中近战怪物的终点每次模式更新都改成目标当前的位置(0x0088d270 模式 12),所以到位是指到了主角身边
            b.Update(2200, true, 380, 100, 400, 100);
            Assert.Equal(MonsterMoveMode.Wander, b.Mode);
            Assert.Equal(200, b.Sight);
        }
    }

    [Fact]
    public void ARangedApproachBecomesHesitantAndHesitationWithoutATargetBecomesIdle()
    {
        MonsterBrain b = NewBrain(new Dial(99).AsFunc(), Traits(melee: false, ranged: true), sight: 200);
        b.Update(0, true, 100, 100, 400, 100);
        b.NotifyDamaged();
        Assert.Equal(MonsterMoveMode.Approach, b.Mode);
        b.Update(1000, true, 100, 100, 400, 100);                       // 找到目标(放大的视野里)→ 远程怪物转迟疑
        Assert.Equal(MonsterMoveMode.Hesitant, b.Mode);
        Assert.Equal(200, b.Sight);

        b.Update(1400, false, 100, 100, 400, 100);                      // 主角没了:迟疑里没目标 → 站桩
        Assert.Equal(MonsterMoveMode.Idle, b.Mode);
        Assert.False(b.Moving);
        Assert.False(b.IsEngaged);
    }

    // ------------------------------------------------------------------ 远程怪物的走位

    [Fact]
    public void ARangedMonsterInTheMiddleDistanceStaysOnItsColumnButAlwaysWalksVertically()
    {
        // 视野 200:近界 100、远界 180;相距 140 在中间:横向不动,纵向总是朝目标的 y 走(同一行时往下)
        MonsterBrain b = NewBrain(new Dial(99).AsFunc(), Traits(melee: false, ranged: true), sight: 200);
        b.Update(0, true, 540, 100, 400, 100);
        b.Update(MonsterBrain.NoticeDelayMs, true, 540, 100, 400, 100);
        Assert.Equal(MonsterMoveMode.HostileRanged, b.Mode);
        Assert.Equal((0, 1), (b.StepX, b.StepY));
        Assert.True(b.Moving);
    }

    // ------------------------------------------------------------------ 被挡住:反向与追击

    [Fact]
    public void AStuckSignalTurnsTheWalkAroundForHalfASecondThenChases()
    {
        MonsterBrain b = NewBrain(new Dial(0).AsFunc(), Traits(), sight: 10);
        b.Update(0, true, 500, 100, 900, 100);                          // 看不到主角;随机数 0:游荡朝左走、纵向被取消
        Assert.Equal((MonsterMoveRules.Left, MonsterMoveRules.None), (b.DirX, b.DirY));

        b.NotifyStuck();
        Assert.Equal(MonsterMoveMode.Reverse, b.Mode);
        Assert.Equal(MonsterMoveRules.Right, b.DirX);                   // 掉头

        b.Update(400, true, 500, 100, 900, 100);                        // 反向不满 501 毫秒
        Assert.Equal(MonsterMoveMode.Reverse, b.Mode);
        b.Update(800, true, 500, 100, 900, 100);                        // 模式更新满足 501 毫秒:转追击
        Assert.Equal(MonsterMoveMode.Chase, b.Mode);

        b.Update(1200, true, 500, 100, 900, 100);                       // 追击里没有目标 → 回游荡
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
    }

    [Fact]
    public void AStationaryBrainIgnoresEveryNotification()
    {
        var b = new MonsterBrain(200, 50, Traits(), 2000, true, new Dial(10).AsFunc());
        b.Update(0, true, 100, 100, 400, 100);
        b.NotifyDamaged();
        b.NotifyHitHero();
        b.NotifyStuck();
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        Assert.False(b.Moving);
    }

    // ------------------------------------------------------------------ 按 .mob 类别选择细节

    private static MonsterDef Def(int sight, int warlike, params string[] categories)
    {
        var lines = new List<ScriptLine>
        {
            new ScriptLine("[sight]", new[] { new ScriptValue(ScriptValueKind.Int, sight, null) }),
            new ScriptLine("[warlike]", new[] { new ScriptValue(ScriptValueKind.Int, warlike, null) }),
        };
        var values = new ScriptValue[categories.Length];
        for (int i = 0; i < categories.Length; i++)
        {
            values[i] = new ScriptValue(ScriptValueKind.String, 0, categories[i]);
        }
        lines.Add(new ScriptLine("[category]", values));
        lines.Add(new ScriptLine("[/category]", Array.Empty<ScriptValue>()));
        return new MonsterDef(1, "monster/test.mob", lines.ToArray());
    }

    [Fact]
    public void RangedOnlyMonstersGetABrainAndTheCategoryDecidesTheApproachStyle()
    {
        MonsterBrain ranged = MonsterBrain.For(Def(250, 50, "[range combat]"), 2000, 1);
        Assert.NotNull(ranged);
        Assert.False(ranged.IsStationary);
        ranged.Update(0, true, 100, 100, 100 + 349, 100);
        Assert.True(ranged.HasTarget);                                  // 250 × 1.4 = 350
        ranged.Update(MonsterBrain.NoticeDelayMs, true, 100, 100, 100 + 349, 100);
        Assert.Equal(MonsterMoveMode.HostileRanged, ranged.Mode);       // 远程怪物不绕背

        MonsterBrain tough = MonsterBrain.For(Def(200, 50, "[melee combat]", "[close-tough]"), 2000, 1);
        tough.Update(0, true, 100, 100, 400, 100);
        tough.NotifyDamaged();
        Assert.Equal(MonsterMoveMode.Approach, tough.Mode);
        Assert.True(tough.ApproachSuperArmor);

        MonsterBrain full = MonsterBrain.For(Def(200, 50, "[melee combat]", "[close-tough]", "[full-superarmor]"), 2000, 1);
        full.Update(0, true, 100, 100, 400, 100);
        full.NotifyDamaged();
        Assert.False(full.ApproachSuperArmor);
    }

    // ------------------------------------------------------------------ 接进怪物演员

    /// <summary>没有射程范围的基础攻击(合成数)。</summary>
    private static readonly AttackKindRecord Basic = new AttackKindRecord(15f, 0f, 1f, true, 0, 0, 0, 0, 0, 0, 0, 0);

    private static MonsterActor Solo(DnfAssets assets, MonsterBrain brain, int x = 360)
    {
        MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
        var spawn = new RoomMonsterSpawn(1, 0, 0, x, 100, 0, 1, 0, "[fixed]", "[normal]");
        var m = new MonsterActor(0, spawn, model, 3, 100, 100, 1500, 400);
        m.SetAttackKinds(new[] { Basic }, 12345u);
        m.SetDefaultAi(brain);
        return m;
    }

    [Fact]
    public void AWalkingMonsterStartsItsAttackWithoutStoppingBecauseWalkingIsTheClientStandState()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var free = new RectUnionArea(new List<WorldRect>());
        MonsterActor m = Solo(assets, NewBrain(new Dial(0).AsFunc(), Traits(), sight: 10));   // 随机数 0:游荡一直在走(横向,碰边掉头)
        bool startedWhileWalking = false, startedWhileStanding = false;
        for (int i = 0; i < 1000; i++)                                  // 10 秒;主角始终贴在怪物左边 60 像素
        {
            MonsterPhase before = m.Phase;
            m.Update(10, m.X - 60, m.Y, true, free);
            if (m.Phase == MonsterPhase.Attack && before != MonsterPhase.Attack)
            {
                startedWhileWalking |= before == MonsterPhase.Walk;
                startedWhileStanding |= before == MonsterPhase.Wait;
            }
        }
        Assert.True(startedWhileWalking);
        Assert.False(startedWhileStanding);                             // 一直在走:每次出招都是从走着开始的
    }

    /// <summary>x 小于给定值的一整片都算障碍。</summary>
    private sealed class LeftWall : IHeroBlocker
    {
        private readonly double _x;

        public LeftWall(double x)
        {
            _x = x;
        }

        public object BlockedBy(double x, double y) => x < _x ? this : null!;
    }

    [Fact]
    public void AMonsterPushingAgainstTheBlockerForASecondTurnsAroundThroughTheReverseMode()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (130, 100) }, monsterSpeed: 100);
        arena.Hero.Blocker = new LeftWall(100);
        MonsterActor m = arena.Session.Monsters[0];
        m.SetDefaultAi(NewBrain(new Dial(0).AsFunc(), Traits(), sight: 10));
        bool sawReverse = false;
        double minX = double.MaxValue;
        for (int i = 0; i < 400; i++)
        {
            arena.Steps(1);
            sawReverse |= m.Brain.Mode == MonsterMoveMode.Reverse;
            minX = Math.Min(minX, m.X);
        }
        Assert.True(sawReverse, "被障碍挡住一秒之后应该进反向模式");
        Assert.True(minX >= 99.0, "反向之后也不会穿过障碍:最小 x = " + minX);
    }

    [Fact]
    public void AMonsterWithABrainThatStartsOutsideTheWalkAreaWalksInAndOneWithoutAnyDriveStaysPut()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var area = new RectUnionArea(new List<WorldRect> { new WorldRect(0, 0, 300, 400) });
        MonsterActor brainy = Solo(assets, NewBrain(new Dial(99).AsFunc(), Traits(), sight: 400), x: 450);   // 出生点在可走区之外(首领从屏幕外登场)
        MonsterActor plain = Solo(assets, null!, x: 450);
        for (int i = 0; i < 500; i++)
        {
            brainy.Update(10, 100, 100, true, area);
            plain.Update(10, 100, 100, true, area);
        }
        Assert.True(brainy.X <= 300, "走位大脑驱动的怪物应该走进可走区:x = " + brainy.X);
        Assert.Equal(450, plain.X, 3);                                  // 没有脚本也没有大脑的旧式怪物保持原样
    }

    [Fact]
    public void ABrainInTheApproachModeMakesTheActorSuperArmoredAndItLastsOnlyForThatMode()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var free = new RectUnionArea(new List<WorldRect>());
        MonsterActor m = Solo(assets, NewBrain(new Dial(99).AsFunc(), Traits(tough: true), sight: 200), x: 100);
        m.Update(10, 400, 100, true, free);
        Assert.False(m.IsSuperArmored);
        m.Brain.NotifyDamaged();                                        // 主角在 300 像素外的一击
        Assert.Equal(MonsterMoveMode.Approach, m.Brain.Mode);
        Assert.True(m.IsSuperArmored);

        m.Update(10, 400 + 900, 100, true, free);                       // 走出放大后的视野(420)
        for (int i = 0; i < 700 && m.Brain.Mode == MonsterMoveMode.Approach; i++)
        {
            m.Update(10, 400 + 900, 100, true, free);
        }
        Assert.NotEqual(MonsterMoveMode.Approach, m.Brain.Mode);
        Assert.False(m.IsSuperArmored);
    }

    [Fact]
    public void AHitOnTheHeroWhileFlankingTurnsTheBrainToTheChase()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var free = new RectUnionArea(new List<WorldRect>());
        MonsterActor m = Solo(assets, NewBrain(new Dial(10).AsFunc(), Traits(), sight: 200), x: 100);
        for (int i = 0; i < 400 && m.Brain.Mode != MonsterMoveMode.Flank; i++)
        {
            m.Update(10, 300, 100, true, free);                         // 主角在 200 像素外、视野之内:发现之后随机数 10 走绕背
        }
        Assert.Equal(MonsterMoveMode.Flank, m.Brain.Mode);
        m.NotifyAttackSuccess(null!);                                   // 绕背途中打中了主角(命中通知)
        Assert.Equal(MonsterMoveMode.Chase, m.Brain.Mode);
    }
}
