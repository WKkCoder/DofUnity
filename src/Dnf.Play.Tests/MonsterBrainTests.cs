using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物默认 AI 的走位大脑(证据 E-114、E-148):游荡、发现、迟滞、敌对、节拍、丢失目标、挨打不改变发现节奏、原地不动、按类别决定用不用大脑,
/// 以及接进怪物演员之后的行为(不到迟滞不走、走起来的步速是 [move speed] × 斜率、视野之外永远没有目标)。
/// 数全是合成的:视野、好战度、速度、位置都是测试自己定的,与原版数据无关;随机数用固定值或脚本序列替身。
/// </summary>
public class MonsterBrainTests
{
    private static Func<int> Fixed(int value) => () => value;

    /// <summary>按顺序给出随机数,用完了一直给最后一个。</summary>
    private static Func<int> Script(params int[] values)
    {
        int next = 0;
        return () => values[Math.Min(next++, values.Length - 1)];
    }

    private static MonsterBrain NewBrain(Func<int> random, int sight = 200, int warlike = 50, bool ranged = false, bool stationary = false, int roomWidth = 2000)
    {
        return new MonsterBrain(sight, warlike, ranged, roomWidth, stationary, random);
    }

    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptValue S(string text) => new ScriptValue(ScriptValueKind.String, 0, text);

    private static MonsterDef Def(int sight = -1, int warlike = -1, params string[] categories)
    {
        var lines = new List<ScriptLine>();
        if (sight >= 0)
        {
            lines.Add(new ScriptLine("[sight]", new[] { I(sight) }));
        }
        if (warlike >= 0)
        {
            lines.Add(new ScriptLine("[warlike]", new[] { I(warlike) }));
        }
        if (categories.Length > 0)
        {
            var values = new ScriptValue[categories.Length];
            for (int i = 0; i < categories.Length; i++)
            {
                values[i] = S(categories[i]);
            }
            lines.Add(new ScriptLine("[category]", values));
            lines.Add(new ScriptLine("[/category]", Array.Empty<ScriptValue>()));
        }
        return new MonsterDef(1, "monster/test.mob", lines.ToArray());
    }

    // ------------------------------------------------------------------ 发现与迟滞

    [Fact]
    public void AHeroBeyondTheSightTimesOnePointFourIsNeverFoundAndTheMonsterKeepsWandering()
    {
        MonsterBrain b = NewBrain(Fixed(7), sight: 200);               // 上限 280
        for (long t = 0; t < 60000; t += 33)
        {
            b.Update(t, true, 100, 100, 100 + 281, 100);
            Assert.False(b.HasTarget);
            Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        }
    }

    [Fact]
    public void ATargetInSightIsFoundAtOnceButHostilityStartsOnlyAfterTheNoticeDelay()
    {
        MonsterBrain b = NewBrain(Fixed(99));
        b.Update(0, true, 100, 100, 300, 100);                          // 距离 200 ≤ 280
        Assert.True(b.HasTarget);
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        b.Update(MonsterBrain.NoticeDelayMs - 1, true, 100, 100, 300, 100);
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        b.Update(MonsterBrain.NoticeDelayMs, true, 100, 100, 300, 100);
        Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);
        Assert.True(b.IsHostile);
    }

    [Fact]
    public void ARangedMonsterBecomesHostileInTheRangedMode()
    {
        MonsterBrain b = NewBrain(Fixed(99), ranged: true);
        b.Update(0, true, 100, 100, 300, 100);
        b.Update(MonsterBrain.NoticeDelayMs, true, 100, 100, 300, 100);
        Assert.Equal(MonsterMoveMode.HostileRanged, b.Mode);
    }

    [Fact]
    public void AMonsterWithSightZeroSeesTheHeroAnywhere()
    {
        MonsterBrain b = NewBrain(Fixed(99), sight: 0);
        b.Update(0, true, 100, 100, 90000, 100);
        Assert.True(b.HasTarget);
    }

    [Fact]
    public void WithoutAHeroThereIsNoTargetAndTheMonsterStillWanders()
    {
        MonsterBrain b = NewBrain(Fixed(0));
        b.Update(0, false, 100, 100, 100, 100);
        Assert.False(b.HasTarget);
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        Assert.True(b.Moving);                                          // 随机数 0:游荡的起步掷骰通过,朝一个方向走
    }

    // ------------------------------------------------------------------ 敌对:朝所在的格子走,方向保持到下一次节拍

    [Fact]
    public void MeleeHostilityWalksTowardTheHeroCellAndKeepsItsDirectionUntilTheNextTick()
    {
        MonsterBrain b = NewBrain(Fixed(99));
        b.Update(0, true, 100, 100, 300, 100);
        b.Update(700, true, 100, 100, 300, 100);
        Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);
        Assert.Equal(MonsterMoveRules.Right, b.DirX);
        Assert.Equal(MonsterMoveRules.None, b.DirY);
        Assert.Equal(1, b.StepX);
        Assert.True(b.Moving);
        Assert.Equal(700 + 1000 + 99 % 300, b.NextTickMs);              // 走着的间隔:基数 1000 + 随机数 % 300

        // 主角跳到另一边:下一次节拍之前方向不变,到点才折回来
        b.Update(1000, true, 100, 100, 0, 100);
        Assert.Equal(1, b.StepX);
        b.Update(b.NextTickMs - 1, true, 100, 100, 0, 100);
        Assert.Equal(1, b.StepX);
        b.Update(b.NextTickMs, true, 100, 100, 0, 100);
        Assert.Equal(-1, b.StepX);
    }

    [Fact]
    public void StandingInTheHeroCellIdlesWithTheShorterStandingInterval()
    {
        MonsterBrain b = NewBrain(Fixed(99));
        b.Update(0, true, 100, 100, 105, 100);                          // 同一格(100 / 16 = 105 / 16,纵向同一行)
        b.Update(700, true, 100, 100, 105, 100);
        Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);
        Assert.False(b.Moving);
        Assert.Equal(700 + 800 + 99 % 200, b.NextTickMs);               // 站着的间隔:基数 800 + 随机数 % 200
    }

    [Fact]
    public void AMeleeMonsterWithLowWarlikeSometimesWalksBackwards()
    {
        // 好战度 0:掷骰点数 ≥ 50 时两个方向取反(点数 99 → 取反;点数 0 → 不取反)
        MonsterBrain direct = NewBrain(Fixed(0), warlike: 0);
        direct.Update(0, true, 100, 100, 300, 100);
        direct.Update(700, true, 100, 100, 300, 100);
        Assert.Equal(MonsterMoveRules.Right, direct.DirX);              // 点数 0 → 不取反
        MonsterBrain back = NewBrain(Fixed(99), warlike: 0);
        back.Update(0, true, 100, 100, 300, 100);
        back.Update(700, true, 100, 100, 300, 100);
        Assert.Equal(MonsterMoveRules.Left, back.DirX);                 // 点数 99 → 取反:背对主角走
    }

    [Fact]
    public void ARangedMonsterBacksOffWhenTheHeroIsCloseAndApproachesWhenItIsFar()
    {
        // 视野 200:近界 100、远界 180
        MonsterBrain close = NewBrain(Fixed(99), sight: 200, ranged: true);
        close.Update(0, true, 500, 100, 410, 100);
        close.Update(700, true, 500, 100, 410, 100);
        Assert.Equal(MonsterMoveMode.HostileRanged, close.Mode);
        Assert.Equal(MonsterMoveRules.Right, close.DirX);               // 主角在左边 90 处:朝右后退

        MonsterBrain far = NewBrain(Fixed(99), sight: 200, ranged: true);
        far.Update(0, true, 500, 100, 300, 100);
        far.Update(700, true, 500, 100, 300, 100);
        Assert.Equal(MonsterMoveRules.Left, far.DirX);                  // 主角在左边 200 处:朝左靠近
    }

    // ------------------------------------------------------------------ 丢失目标

    [Fact]
    public void ALostTargetReturnsToWanderingAtTheNextTickAndIsSearchedAgainOnlyAfterTheFindInterval()
    {
        MonsterBrain b = NewBrain(Fixed(99));
        b.Update(0, true, 100, 100, 300, 100);
        b.Update(700, true, 100, 100, 300, 100);
        Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);
        long tick = b.NextTickMs;                                       // 1799

        b.Update(800, true, 100, 100, 100 + 281, 100);                  // 主角跑出了视野
        Assert.False(b.HasTarget);
        Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);             // 模式要到下一次节拍才回游荡
        b.Update(tick, true, 100, 100, 100 + 281, 100);
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);

        // 重新找目标:从"设置目标"(丢失)的那一刻起至少隔 1 秒
        b.Update(800 + MonsterMoveRules.FindIntervalMs - 1, true, 100, 100, 300, 100);
        Assert.False(b.HasTarget);
        b.Update(800 + MonsterMoveRules.FindIntervalMs, true, 100, 100, 300, 100);
        Assert.True(b.HasTarget);
    }

    [Fact]
    public void ASearchThatFindsNothingStillWaitsTheFullIntervalBeforeTheNextOne()
    {
        MonsterBrain b = NewBrain(Fixed(99));
        b.Update(0, true, 100, 100, 100 + 400, 100);                    // 第一次找:太远,没有
        b.Update(500, true, 100, 100, 300, 100);                        // 主角走进视野,但还没到下一次找
        Assert.False(b.HasTarget);
        b.Update(999, true, 100, 100, 300, 100);
        Assert.False(b.HasTarget);
        b.Update(1000, true, 100, 100, 300, 100);
        Assert.True(b.HasTarget);
    }

    // ------------------------------------------------------------------ 受击

    [Fact]
    public void DamageDoesNotShortenTheFindIntervalNorTheNoticeDelay()
    {
        // 客户端有个"受击后立刻找目标"的开关([targeting on damage]),没有任何怪物数据写它:挨打不改变发现与迟滞的节奏
        MonsterBrain b = NewBrain(Fixed(99));
        b.Update(0, true, 100, 100, 100 + 400, 100);                    // 没找到
        b.Update(100, true, 100, 100, 200, 100);                        // 主角进了视野(相距 100,不算"远处"),但间隔没到
        Assert.False(b.HasTarget);
        b.NotifyDamaged();
        b.Update(150, true, 100, 100, 200, 100);
        Assert.False(b.HasTarget);
        b.Update(MonsterMoveRules.FindIntervalMs, true, 100, 100, 200, 100);
        Assert.True(b.HasTarget);
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        b.Update(MonsterMoveRules.FindIntervalMs + MonsterBrain.NoticeDelayMs - 1, true, 100, 100, 200, 100);
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        b.Update(MonsterMoveRules.FindIntervalMs + MonsterBrain.NoticeDelayMs, true, 100, 100, 200, 100);
        Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);
    }

    [Fact]
    public void DamageWithTheHeroOutOfSightGivesNoTargetAndTheNextDiscoveryStillWaits()
    {
        MonsterBrain b = NewBrain(Fixed(99));
        b.NotifyDamaged();
        b.Update(10, true, 100, 100, 100 + 400, 100);                   // 远程技能打的:主角在视野之外,没有目标
        Assert.False(b.HasTarget);
        b.Update(1010, true, 100, 100, 300, 100);                       // 一秒后发现:迟滞重新计
        Assert.True(b.HasTarget);
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        b.Update(1010 + MonsterBrain.NoticeDelayMs, true, 100, 100, 300, 100);
        Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);
    }

    [Fact]
    public void AHitFromCloseByDoesNotChangeTheModeAndDoesNotSkipTheNoticeDelay()
    {
        MonsterBrain b = NewBrain(Fixed(99));
        b.Update(0, true, 100, 100, 200, 100);                          // 相距 100:找到目标,迟滞开始
        Assert.True(b.HasTarget);
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        b.NotifyDamaged();
        b.Update(100, true, 100, 100, 200, 100);
        Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        b.Update(MonsterBrain.NoticeDelayMs, true, 100, 100, 200, 100);
        Assert.Equal(MonsterMoveMode.HostileMelee, b.Mode);
    }

    // ------------------------------------------------------------------ 游荡的方向选择

    [Fact]
    public void WanderingStartsWithAFortyPercentRollThenPicksADiagonal()
    {
        // 掷骰 39(< 40)起步;横向 1 & 1 = 右;纵向 (1 & 1) + 2 = 下;间隔随机数 0;横向停下掷骰 0(概率 0,不取消);纵向停下掷骰 99(≥ 50,不取消)
        MonsterBrain go = NewBrain(Script(39, 1, 1, 0, 0, 99));
        go.Update(0, false, 500, 100, 0, 0);
        Assert.Equal((MonsterMoveRules.Right, MonsterMoveRules.Down), (go.DirX, go.DirY));
        Assert.Equal(500, go.NextTickMs);                               // 走着的游荡间隔:基数 500 + 0

        // 掷骰 40 → 不起步:站着,间隔取站着的那一组(300 + 0)
        MonsterBrain stand = NewBrain(Script(40, 0, 0, 0));
        stand.Update(0, false, 500, 100, 0, 0);
        Assert.False(stand.Moving);
        Assert.Equal(300, stand.NextTickMs);
    }

    [Fact]
    public void AboutHalfOfTheWanderingVerticalMovesAreCancelled()
    {
        // 纵向停下掷骰 49(< 50)→ 纵向被取消,横向照走
        MonsterBrain b = NewBrain(Script(0, 0, 0, 0, 0, 49));
        b.Update(0, false, 500, 100, 0, 0);
        Assert.Equal((MonsterMoveRules.Left, MonsterMoveRules.None), (b.DirX, b.DirY));
    }

    [Fact]
    public void WanderingTurnsAwayFromTheRoomEdgesWithoutRollingForTheHorizontalDirection()
    {
        // 离左边不到 80:横向固定朝右,不吃随机数;纵向的随机数 0 → 上(2 + 0)
        MonsterBrain left = NewBrain(Script(0, 0, 0, 0, 99), roomWidth: 1000);
        left.Update(0, false, 10, 100, 0, 0);
        Assert.Equal((MonsterMoveRules.Right, MonsterMoveRules.Up), (left.DirX, left.DirY));

        MonsterBrain right = NewBrain(Script(0, 0, 0, 0, 99), roomWidth: 1000);
        right.Update(0, false, 990, 100, 0, 0);
        Assert.Equal((MonsterMoveRules.Left, MonsterMoveRules.Up), (right.DirX, right.DirY));
    }

    // ------------------------------------------------------------------ 原地不动

    [Fact]
    public void AStationaryBrainNeverFindsTheHeroNorWalks()
    {
        MonsterBrain b = NewBrain(Fixed(0), stationary: true);
        for (long t = 0; t < 10000; t += 50)
        {
            b.Update(t, true, 100, 100, 110, 100);
            Assert.False(b.HasTarget);
            Assert.False(b.Moving);
            Assert.Equal(MonsterMoveMode.Wander, b.Mode);
        }
        b.NotifyDamaged();
        b.Update(10050, true, 100, 100, 110, 100);
        Assert.False(b.HasTarget);
        Assert.False(b.Moving);
    }

    // ------------------------------------------------------------------ 按类别决定用不用大脑

    [Fact]
    public void AMeleeCategoryGetsABrainWithTheMobSightAndBecomesHostileInTheMeleeMode()
    {
        MonsterBrain melee = MonsterBrain.For(Def(300, 10, "[melee combat]", "[close-carefully]"), 1000, 1)!;
        Assert.NotNull(melee);
        Assert.Equal(300, melee.Sight);
        Assert.False(melee.IsStationary);
        melee.Update(0, true, 100, 100, 100 + 420, 100);                // 上限 = 300 × 1.4 = 420
        Assert.True(melee.HasTarget);
        melee.Update(MonsterBrain.NoticeDelayMs, true, 100, 100, 100 + 420, 100);
        Assert.True(melee.Mode == MonsterMoveMode.HostileMelee || melee.Mode == MonsterMoveMode.Flank);   // 近战怪物发现之后一半几率先绕背

        MonsterBrain both = MonsterBrain.For(Def(250, -1, "[range combat]", "[melee combat]"), 1000, 1)!;
        both.Update(0, true, 100, 100, 300, 100);
        both.Update(MonsterBrain.NoticeDelayMs, true, 100, 100, 300, 100);
        Assert.Equal(MonsterMoveMode.HostileMelee, both.Mode);          // 有近战就走近战
    }

    [Fact]
    public void TheSightDefaultsToTwoHundredWhenTheMobDoesNotSayAndStayMonstersAreStationary()
    {
        Assert.Equal(MonsterMoveRules.DefaultSight, MonsterBrain.For(Def(-1, -1, "[melee combat]"), 1000, 1)!.Sight);
        Assert.True(MonsterBrain.For(Def(-1, -1, "[stay]"), 1000, 1)!.IsStationary);
    }

    [Fact]
    public void MonstersWithoutTheCombatCategoriesOrWithOnesideMoveKeepThePlaceholderAndRangedOnesGetABrain()
    {
        Assert.Null(MonsterBrain.For(null!, 1000, 1));
        Assert.Null(MonsterBrain.For(Def(), 1000, 1));                                       // 没有类别(脚本指挥)
        Assert.Null(MonsterBrain.For(Def(-1, -1, "[fixture]"), 1000, 1));                    // 只是摆设类别
        Assert.NotNull(MonsterBrain.For(Def(250, -1, "[range combat]"), 1000, 1));           // 只有远程:模式 10 读通了,有大脑
        Assert.Null(MonsterBrain.For(Def(-1, -1, "[melee combat]", "[oneside move]"), 1000, 1));
    }

    [Fact]
    public void TheSameSeedGivesTheSameWalkAndDifferentSeedsDiffer()
    {
        static string Trace(uint seed)
        {
            MonsterBrain b = MonsterBrain.For(Def(200, 50, "[melee combat]"), 2000, seed)!;
            var sb = new System.Text.StringBuilder();
            for (long t = 0; t < 30000; t += 100)
            {
                b.Update(t, false, 1000, 100, 0, 0);
                sb.Append(b.DirX).Append(b.DirY).Append(',');
            }
            return sb.ToString();
        }

        Assert.Equal(Trace(7), Trace(7));
        Assert.NotEqual(Trace(7), Trace(8));
    }

    // ------------------------------------------------------------------ 接进怪物演员

    private static MonsterActor Monster(RoomCombatTests.Arena arena, int index = 0) => arena.Session.Monsters[index];

    [Fact]
    public void AMonsterWithABrainStaysPutBeforeTheNoticeDelayThenWalksToTheHeroAtTheGivenSpeed()
    {
        // 主角站在 (300, 100);怪物在 (100, 100) 朝左,步速 100 像素/秒,随机数固定为 99:游荡不起步,敌对时方向不取反
        using var arena = new RoomCombatTests.Arena(new[] { (100, 100) }, monsterSpeed: 100);
        MonsterActor m = Monster(arena);
        m.SetDefaultAi(NewBrain(Fixed(99), sight: 300));
        Assert.True(m.FacingLeft);

        arena.Steps(60);                                                // 600 毫秒:还在迟滞里
        Assert.Equal(100, m.X, 3);
        Assert.Equal(MonsterMoveMode.Wander, m.Brain.Mode);

        arena.Steps(100);                                               // 到 1600 毫秒:700 毫秒进敌对,之后走了约 900 毫秒 → 约 90 像素
        Assert.Equal(MonsterMoveMode.HostileMelee, m.Brain.Mode);
        Assert.InRange(m.X, 100 + 85, 100 + 95);
        Assert.False(m.FacingLeft);                                     // 朝主角走,面朝右边
        Assert.Equal(MonsterPhase.Walk, m.Phase);
    }

    [Fact]
    public void AMonsterWithABrainNeverHasATargetBeyondItsSightTimesOnePointFour()
    {
        // 视野 100 → 上限 140;怪物在 (700, 100),主角在 (300, 100)(相距 400),速度慢,随机游荡
        using var arena = new RoomCombatTests.Arena(new[] { (700, 100) }, monsterSpeed: 30);
        MonsterActor m = Monster(arena);
        m.SetDefaultAi(NewBrain(() => 3 + (int)((m.AiClockMs / 7) % 90), sight: 100));
        for (int i = 0; i < 800; i++)
        {
            arena.Steps(1);
            double dx = arena.Hero.X - m.X;
            double dy = (arena.Hero.Y - m.Y) / MonsterMoveRules.DepthScale;
            if (m.Brain.HasTarget)
            {
                Assert.True(Math.Sqrt(dx * dx + dy * dy) <= 140 + 6, "有目标时必须在视野上限之内(留出取整与一步移动的余量)");
            }
        }
    }

    [Fact]
    public void AStationaryBrainKeepsTheMonsterWhereItIsEvenWithTheHeroInSight()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (500, 100) }, monsterSpeed: 100);
        MonsterActor m = Monster(arena);
        m.SetDefaultAi(NewBrain(Fixed(0), sight: 400, stationary: true));
        arena.Steps(300);
        Assert.Equal(500, m.X, 3);
        Assert.Equal(100, m.Y, 3);
        Assert.Equal(MonsterPhase.Wait, m.Phase);
    }

    [Fact]
    public void AHitDoesNotMakeAMonsterWithoutATargetTargetTheHeroEarly()
    {
        // 视野 30 → 上限 42。主角在 (300, 100) 朝右,怪物在 (360, 100)(相距 60:开头那次找目标找不到,下一次要等 1 秒);
        // 主角往右走到相距不到 40,然后挥击打中怪物。挨打不改变发现的节奏:最早在 1000 毫秒找到、1700 毫秒才敌对
        using var arena = new RoomCombatTests.Arena(new[] { (360, 100) }, monsterSpeed: 0, hp: 5000);
        MonsterActor m = Monster(arena);
        m.SetDefaultAi(NewBrain(Fixed(99), sight: 30));
        for (int i = 0; i < 60 && arena.Hero.X < 322; i++)
        {
            arena.Steps(1, dirX: 1);
        }
        Assert.True(arena.Hero.X >= 322);
        Assert.True(m.AiClockMs < 900);
        Assert.False(m.Brain.HasTarget);                                // 还在 1 秒的找目标间隔里,没有目标
        arena.Session.Update(10, 0, 0, true);
        for (int i = 0; i < 200 && arena.Session.Hits < 1; i++)
        {
            arena.Session.Update(10, 0, 0, false);
        }
        Assert.Equal(1, arena.Session.Hits);
        Assert.True(m.AiClockMs < 1000);
        Assert.False(m.Brain.HasTarget);                                // 挨了打也没有马上找
        for (int i = 0; i < 300 && !m.Brain.IsHostile; i++)
        {
            arena.Session.Update(10, 0, 0, false);
        }
        Assert.True(m.Brain.IsHostile);
        Assert.True(m.AiClockMs >= 1700, "发现的节奏没变:1000 毫秒找到、再过 700 毫秒敌对,实际 " + m.AiClockMs);
    }

    [Fact]
    public void AStayMonsterNeverRollsForAnAttackWhileAnOrdinaryOneStrikesAfterTheLockout()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var free = new RectUnionArea(new List<WorldRect>());
        var basic = new AttackKindRecord(15f, 0f, 1f, true, 0, 0, 0, 0, 0, 0, 0, 0);       // 没有射程范围的基础攻击(合成数)

        MonsterActor Solo(bool stay)
        {
            MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobMobPath);
            var spawn = new RoomMonsterSpawn(1, 0, 0, 360, 100, 0, 1, 0, "[fixed]", "[normal]");
            var m = new MonsterActor(0, spawn, model, 3, 100, 100, 1500, 400);
            m.SetAttackKinds(new[] { basic }, 12345u);
            m.SetDefaultAi(NewBrain(Fixed(99), sight: 10, stationary: stay));
            return m;
        }

        MonsterActor ordinary = Solo(false), stay = Solo(true);
        bool ordinaryAttacked = false, stayAttacked = false;
        for (int i = 0; i < 700; i++)                                   // 7 秒;主角站在 60 像素外
        {
            ordinary.Update(10, 300, 100, true, free);
            stay.Update(10, 300, 100, true, free);
            ordinaryAttacked |= ordinary.Phase == MonsterPhase.Attack;
            stayAttacked |= stay.Phase == MonsterPhase.Attack;
        }
        Assert.True(ordinaryAttacked);
        Assert.False(stayAttacked);
    }

    // ------------------------------------------------------------------ 关着的门体怪物也走不进去

    /// <summary>x 小于给定值的一整片都算障碍(门体的桩)。</summary>
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
    public void AWanderingMonsterIsStoppedByTheRoomBlockerJustLikeTheHero()
    {
        // 随机数 0:游荡一直朝左走(纵向被取消);视野 10 → 看不到主角;步速 100 像素/秒,走 3 秒本来会走到 x = −170。
        // 被障碍挡住一秒以上之后会转反向模式退开(MonsterBrainModesTests),但任何时候都不会穿过障碍
        using var arena = new RoomCombatTests.Arena(new[] { (130, 100) }, monsterSpeed: 100);
        arena.Hero.Blocker = new LeftWall(100);
        MonsterActor m = Monster(arena);
        m.SetDefaultAi(NewBrain(Fixed(0), sight: 10));
        double minX = double.MaxValue;
        for (int i = 0; i < 300; i++)
        {
            arena.Steps(1);
            minX = Math.Min(minX, m.X);
        }
        Assert.InRange(minX, 99.0, 101.0);                              // 走到障碍边上为止
        Assert.False(m.Brain.HasTarget);
    }

    [Fact]
    public void AMonsterThatStartsInsideTheBlockerCanStillWalkOutOfIt()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (50, 100) }, monsterSpeed: 100);
        arena.Hero.Blocker = new LeftWall(100);
        MonsterActor m = Monster(arena);
        m.SetDefaultAi(NewBrain(Fixed(0), sight: 10));                  // 离左边不到 80 像素:游荡的防撞边让它朝右走,正好穿过障碍的范围
        arena.Steps(40);
        Assert.True(m.X > 85, "已经压在障碍里的怪物不受它挡,照样走:x = " + m.X);       // 出了障碍的范围(x ≥ 100)再想走回去才会被挡
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTurnSwitchGovernsABrainMonstersFacingWhileWalking(bool facesTarget)
    {
        // 主角站在 (300, 100);怪物在 (100, 100) 朝左:走向主角的路上面朝主角(右)只在总开关开着时发生(M70,E-169)
        using var arena = new RoomCombatTests.Arena(new[] { (100, 100) }, monsterSpeed: 100);
        MonsterActor m = Monster(arena);
        m.FacesTarget = facesTarget;
        m.SetDefaultAi(NewBrain(Fixed(99), sight: 300));
        arena.Steps(160);                                               // 走了约 90 像素
        Assert.Equal(MonsterMoveMode.HostileMelee, m.Brain.Mode);
        Assert.Equal(MonsterPhase.Walk, m.Phase);
        Assert.True(m.X > 100 + 50, "walks to the right: " + m.X);
        Assert.Equal(facesTarget, !m.FacingLeft);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTurnSwitchGovernsAStandingEngagedBrainMonstersFacing(bool facesTarget)
    {
        // 盯着目标又站着的模式(追击里 80% 的掷骰在随机数 99 下不起步):站着时面朝主角,只在总开关开着时发生(M70,E-169)。
        // 走向主角(右)的路上被判卡住:反向模式往左退 501 毫秒,然后转追击站定
        using var arena = new RoomCombatTests.Arena(new[] { (100, 100) }, monsterSpeed: 100);
        MonsterActor m = Monster(arena);
        m.FacesTarget = facesTarget;
        m.SetDefaultAi(NewBrain(Fixed(99), sight: 400));
        arena.Steps(160);
        Assert.Equal(MonsterMoveMode.HostileMelee, m.Brain.Mode);
        m.Brain.NotifyStuck();
        arena.Steps(100);                                               // 反向走完(501 毫秒),转了追击
        Assert.Equal(MonsterMoveMode.Chase, m.Brain.Mode);
        Assert.True(m.Brain.IsEngaged);
        Assert.False(m.Brain.Moving);
        Assert.True(m.X < 190, "backed off to the left: " + m.X);
        Assert.Equal(facesTarget, !m.FacingLeft);
    }
}
