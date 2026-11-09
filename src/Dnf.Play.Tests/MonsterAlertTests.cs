using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 被打中之后叫同伴(客户端受击回调 0x0089c7b0 → 0x0088f170 的静态读码;靠近本身见 E-148):
/// 大脑这一侧(靠近的返回值、70% 的几率、请求的取走、被叫的同伴怎么靠近)与房间这一侧(按难度的名额、收集与顺序、跳过已在靠近的);
/// 房间会话里整条链路的测试在 <see cref="MonsterAlertSessionTests"/>。数全是合成的:位置、视野、血量、随机数都是测试自己定的,与原版数据无关。
/// </summary>
public class MonsterAlertTests
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

    private static MonsterBrain NewBrain(Func<int> random, int sight = 200, bool stationary = false)
    {
        var traits = new MonsterBrainTraits(true, false, false, false, false, false, false, 40);
        return new MonsterBrain(sight, 50, traits, 2000, stationary, random);
    }

    // ------------------------------------------------------------------ 大脑:靠近成功之后掷 70%

    [Theory]
    [InlineData(0, true)]
    [InlineData(69, true)]
    [InlineData(70, false)]
    [InlineData(99, false)]
    public void AFarHitThatStartsTheApproachRequestsAnAlertOnlyWhenTheRollIsBelowSeventy(int roll, bool expected)
    {
        MonsterBrain b = NewBrain(new Dial(roll).AsFunc());
        b.Update(0, true, 100, 100, 300, 100);                          // 相距 200 ≥ 150
        b.NotifyDamaged();
        Assert.Equal(MonsterMoveMode.Approach, b.Mode);
        Assert.Equal(expected, b.TakeAlertRequest());
    }

    [Fact]
    public void AHitFromCloseByStartsNoApproachAndRequestsNoAlertButExactlyTheFarDistanceDoes()
    {
        MonsterBrain near = NewBrain(new Dial(0).AsFunc());
        near.Update(0, true, 100, 100, 100 + 149, 100);
        near.NotifyDamaged();
        Assert.NotEqual(MonsterMoveMode.Approach, near.Mode);
        Assert.False(near.TakeAlertRequest());

        MonsterBrain exact = NewBrain(new Dial(0).AsFunc());
        exact.Update(0, true, 100, 100, 100 + 150, 100);
        exact.NotifyDamaged();
        Assert.Equal(MonsterMoveMode.Approach, exact.Mode);
        Assert.True(exact.TakeAlertRequest());
    }

    [Fact]
    public void NoAlertIsRequestedBeforeAnyUpdateOrByAStationaryBrain()
    {
        MonsterBrain fresh = NewBrain(new Dial(0).AsFunc());
        fresh.NotifyDamaged();
        Assert.False(fresh.TakeAlertRequest());

        MonsterBrain stay = NewBrain(new Dial(0).AsFunc(), stationary: true);
        stay.Update(0, true, 100, 100, 400, 100);
        stay.NotifyDamaged();
        Assert.False(stay.TakeAlertRequest());
    }

    [Fact]
    public void AHitWhileAlreadyApproachingRequestsNothing()
    {
        MonsterBrain b = NewBrain(new Dial(0).AsFunc());
        b.Update(0, true, 100, 100, 400, 100);
        b.NotifyDamaged();
        Assert.True(b.TakeAlertRequest());
        b.NotifyDamaged();                                              // 已在靠近模式:靠近函数早退返回假,不叫
        Assert.False(b.TakeAlertRequest());
    }

    [Fact]
    public void TheApproachReturnsTrueOnlyWhileTheHeroIsStillAValidTargetOfTheEnlargedSight()
    {
        // 视野 200 → 靠近里放大到 300 → 有效目标的上限 420;基础视野的上限是 280
        MonsterBrain between = NewBrain(new Dial(0).AsFunc(), sight: 200);
        between.Update(0, true, 100, 100, 100 + 400, 100);              // 400:超过 280、没超过 420
        between.NotifyDamaged();
        Assert.Equal(MonsterMoveMode.Approach, between.Mode);
        Assert.True(between.TakeAlertRequest());

        MonsterBrain beyond = NewBrain(new Dial(0).AsFunc(), sight: 200);
        beyond.Update(0, true, 100, 100, 100 + 600, 100);               // 600:连放大后的视野也够不着
        beyond.NotifyDamaged();
        Assert.Equal(MonsterMoveMode.Approach, beyond.Mode);            // 靠近照进
        Assert.False(beyond.TakeAlertRequest());                        // 但返回假,不叫
    }

    [Fact]
    public void ARequestIsTakenOnceAndEachNewHitClearsAnUntakenOne()
    {
        MonsterBrain b = NewBrain(new Dial(0).AsFunc());
        b.Update(0, true, 100, 100, 400, 100);
        b.NotifyDamaged();
        Assert.True(b.TakeAlertRequest());
        Assert.False(b.TakeAlertRequest());                             // 取走即清

        MonsterBrain stale = NewBrain(new Dial(0).AsFunc());
        stale.Update(0, true, 100, 100, 400, 100);
        stale.NotifyDamaged();                                          // 留下一份请求,没人取
        stale.NotifyDamaged();                                          // 又挨一下(已在靠近中):旧请求不再留着
        Assert.False(stale.TakeAlertRequest());
    }

    // ------------------------------------------------------------------ 大脑:被叫的同伴

    [Fact]
    public void ACalledAllyApproachesTheHeroLikeAHitOneButRollsNothingAndCallsNobody()
    {
        MonsterBrain ally = NewBrain(new Dial(0).AsFunc());
        ally.Update(0, true, 100, 100, 400, 100);                       // 相距 300
        Assert.True(ally.AlertApproach());
        Assert.Equal(MonsterMoveMode.Approach, ally.Mode);
        Assert.Equal(300, ally.Sight);                                  // 视野 × 1.5
        Assert.Equal((400, 100), (ally.DestinationX, ally.DestinationY));
        Assert.False(ally.TakeAlertRequest());                          // 被叫的不再接着叫别人
    }

    [Fact]
    public void ACalledAllyThatIsNearTheHeroOrAlreadyApproachingOrStationaryStaysAsItWas()
    {
        MonsterBrain near = NewBrain(new Dial(0).AsFunc());
        near.Update(0, true, 100, 100, 100 + 149, 100);
        Assert.False(near.AlertApproach());
        Assert.NotEqual(MonsterMoveMode.Approach, near.Mode);

        MonsterBrain approaching = NewBrain(new Dial(0).AsFunc());
        approaching.Update(0, true, 100, 100, 400, 100);
        Assert.True(approaching.AlertApproach());
        Assert.False(approaching.AlertApproach());
        Assert.Equal(300, approaching.Sight);                           // 没有叠加 × 1.5

        MonsterBrain stay = NewBrain(new Dial(0).AsFunc(), stationary: true);
        stay.Update(0, true, 100, 100, 400, 100);
        Assert.False(stay.AlertApproach());
        Assert.Equal(MonsterMoveMode.Wander, stay.Mode);

        MonsterBrain unknown = NewBrain(new Dial(0).AsFunc());
        Assert.False(unknown.AlertApproach());                          // 还没有主角位置
        Assert.Equal(MonsterMoveMode.Wander, unknown.Mode);
    }

    [Fact]
    public void ACalledAllyWithTheHeroOutOfItsEnlargedSightStillEntersTheApproach()
    {
        MonsterBrain ally = NewBrain(new Dial(0).AsFunc(), sight: 200);
        ally.Update(0, true, 100, 100, 100 + 600, 100);
        Assert.False(ally.AlertApproach());                             // 返回值是"主角仍是有效目标"
        Assert.Equal(MonsterMoveMode.Approach, ally.Mode);              // 模式照换:叫的一方不看返回值
    }

    // ------------------------------------------------------------------ 房间:收集、名额、顺序

    private const int HeroX = 100;
    private const int HeroY = 100;

    /// <summary>一间合成的房间:主角在 (100, 100),怪物按加入的顺序排(出生序);每只带一个走位大脑(默认随机数 99 = 不叫人)。</summary>
    private sealed class Rig : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();
        private readonly DnfAssets _assets;
        private readonly MonsterModel _model;

        public List<MonsterActor> Monsters { get; } = new List<MonsterActor>();

        public Rig()
        {
            _assets = DnfAssets.Open(_fx.Root);
            _model = MonsterModel.Read(_assets, AvatarFixture.BlobMobPath);
        }

        public MonsterActor Add(int x, int y = HeroY, int hp = 100, Func<int> random = null, int sight = 200, bool stationary = false,
            bool withBrain = true, bool prime = true)
        {
            var spawn = new RoomMonsterSpawn(1, 0, 0, x, y, 0, 1, 0, "[fixed]", "[normal]");
            var m = new MonsterActor(Monsters.Count, spawn, _model, 3, hp, 100, 1500, 400);
            if (withBrain)
            {
                m.SetDefaultAi(NewBrain(random ?? new Dial(99).AsFunc(), sight, stationary));
                if (prime)
                {
                    Prime(m);
                }
            }
            Monsters.Add(m);
            return m;
        }

        /// <summary>让这只怪物的大脑知道主角在哪(第一次 Update)。</summary>
        public static void Prime(MonsterActor m)
        {
            m.Brain.Update(0, true, m.X, m.Y, HeroX, HeroY);
        }

        public void Dispose()
        {
            _assets.Dispose();
            _fx.Dispose();
        }
    }

    /// <summary>呼叫者在 (400, 100)(离主角 300,在视野 200 放大后的 420 之内),随机数 0(一定掷中);它先挨一下,留下请求。</summary>
    private static MonsterActor Caller(Rig rig, int sight = 200)
    {
        MonsterActor caller = rig.Add(400, random: new Dial(0).AsFunc(), sight: sight);
        caller.Brain.NotifyDamaged();
        return caller;
    }

    private static MonsterMoveMode ModeOf(MonsterActor m) => m.Brain.Mode;

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 4)]
    [InlineData(3, 5)]
    [InlineData(4, 5)]
    public void TheNumberOfAlliesCalledFollowsTheDungeonDifficultyAndTheFirstOnesInRoomOrderAreTheOnesCalled(int difficulty, int expected)
    {
        using var rig = new Rig();
        MonsterActor caller = Caller(rig);
        for (int i = 0; i < 6; i++)
        {
            rig.Add(450 + i * 20);                                      // 离呼叫者 50 到 150,全在视野里
        }
        Assert.Equal(expected, MonsterAlert.Broadcast(caller, rig.Monsters, difficulty));
        for (int i = 1; i <= 6; i++)
        {
            Assert.Equal(i <= expected ? MonsterMoveMode.Approach : MonsterMoveMode.Wander, ModeOf(rig.Monsters[i]));
        }
    }

    [Fact]
    public void ADifficultyOutsideZeroToFourCallsFourAllies()
    {
        using var rig = new Rig();
        MonsterActor caller = Caller(rig);
        for (int i = 0; i < 6; i++)
        {
            rig.Add(450 + i * 20);
        }
        Assert.Equal(4, MonsterAlert.Broadcast(caller, rig.Monsters, 9));
    }

    [Fact]
    public void TheCallersEnlargedSightDecidesWhoIsInRange()
    {
        // 呼叫者视野 200,靠近后放大到 300:上限 420(基础视野的上限是 280)
        using var rig = new Rig();
        MonsterActor caller = Caller(rig);
        MonsterActor within = rig.Add(400 + 350);                       // 350:超过 280、没超过 420
        MonsterActor beyond = rig.Add(400 + 450);                       // 450:超过 420
        Assert.Equal(1, MonsterAlert.Broadcast(caller, rig.Monsters, 4));
        Assert.Equal(MonsterMoveMode.Approach, ModeOf(within));
        Assert.Equal(MonsterMoveMode.Wander, ModeOf(beyond));
    }

    [Fact]
    public void AlliesAreCalledInRoomOrderNotByDistance()
    {
        using var rig = new Rig();
        MonsterActor caller = Caller(rig);
        MonsterActor far = rig.Add(400 + 250);
        MonsterActor near = rig.Add(400 + 50);
        Assert.Equal(1, MonsterAlert.Broadcast(caller, rig.Monsters, 0));    // 名额 1:房间里靠前的那个
        Assert.Equal(MonsterMoveMode.Approach, ModeOf(far));
        Assert.Equal(MonsterMoveMode.Wander, ModeOf(near));
    }

    [Fact]
    public void AnAllyAlreadyApproachingIsSkippedAndDoesNotUseUpTheSlot()
    {
        using var rig = new Rig();
        MonsterActor caller = Caller(rig);
        MonsterActor busy = rig.Add(450);
        MonsterActor next = rig.Add(470);
        MonsterActor last = rig.Add(490);
        Assert.True(busy.Brain.AlertApproach());                       // 先让它进靠近模式
        Assert.Equal(1, MonsterAlert.Broadcast(caller, rig.Monsters, 0));
        Assert.Equal(MonsterMoveMode.Approach, ModeOf(next));
        Assert.Equal(MonsterMoveMode.Wander, ModeOf(last));
    }

    [Fact]
    public void ADeadAllyIsNotCalledAndDoesNotUseUpTheSlot()
    {
        using var rig = new Rig();
        MonsterActor caller = Caller(rig);
        MonsterActor dead = rig.Add(450, hp: 1, prime: false);          // 还没有主角位置时挨打:不触发它自己的靠近
        Assert.True(dead.ApplyHit(999, false, 0, false));
        Rig.Prime(dead);
        MonsterActor alive = rig.Add(470);
        MonsterActor spare = rig.Add(490);
        Assert.False(dead.IsAlive);
        Assert.Equal(1, MonsterAlert.Broadcast(caller, rig.Monsters, 0));
        Assert.Equal(MonsterMoveMode.Wander, ModeOf(dead));
        Assert.Equal(MonsterMoveMode.Approach, ModeOf(alive));
        Assert.Equal(MonsterMoveMode.Wander, ModeOf(spare));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStationaryOrBrainlessAllyUsesUpASlotButChangesNothing(bool stationary)
    {
        // 客户端对每个被叫的同伴都调靠近并把名额减一;原地不动的(靠近对它没有效果)与没有大脑的(脚本指挥,我们没有那套运行时)同样占名额(推断)
        using var rig = new Rig();
        MonsterActor caller = Caller(rig);
        MonsterActor first = stationary ? rig.Add(450, stationary: true) : rig.Add(450, withBrain: false);
        MonsterActor second = rig.Add(470);
        Assert.Equal(1, MonsterAlert.Broadcast(caller, rig.Monsters, 0));
        if (first.Brain != null)
        {
            Assert.Equal(MonsterMoveMode.Wander, ModeOf(first));
        }
        Assert.Equal(MonsterMoveMode.Wander, ModeOf(second));            // 唯一的名额被第一个用掉了
    }

    [Fact]
    public void ASlotIsUsedPerCalledAllyEvenWhenItsApproachDoesNotTakeEffect()
    {
        // 第一个同伴离主角只有 50:被叫了但靠近不成(不够远),名额照样用掉
        using var rig = new Rig();
        MonsterActor caller = Caller(rig);
        MonsterActor close = rig.Add(150);                              // 离呼叫者 250 ≤ 280 在视野里;离主角 50 < 150
        MonsterActor other = rig.Add(450);
        Assert.Equal(1, MonsterAlert.Broadcast(caller, rig.Monsters, 0));
        Assert.Equal(MonsterMoveMode.Wander, ModeOf(close));
        Assert.Equal(MonsterMoveMode.Wander, ModeOf(other));
    }

    [Fact]
    public void WithoutARequestNobodyIsCalled()
    {
        using var rig = new Rig();
        MonsterActor quiet = rig.Add(400, random: new Dial(99).AsFunc());     // 掷不中
        quiet.Brain.NotifyDamaged();
        MonsterActor ally = rig.Add(450);
        Assert.Equal(MonsterMoveMode.Approach, ModeOf(quiet));                // 自己照靠近
        Assert.Equal(0, MonsterAlert.Broadcast(quiet, rig.Monsters, 4));
        Assert.Equal(MonsterMoveMode.Wander, ModeOf(ally));
    }

    [Fact]
    public void TheRequestIsConsumedByTheFirstBroadcast()
    {
        using var rig = new Rig();
        MonsterActor caller = Caller(rig);
        MonsterActor first = rig.Add(450);
        MonsterActor second = rig.Add(470);
        Assert.Equal(1, MonsterAlert.Broadcast(caller, rig.Monsters, 0));
        Assert.Equal(0, MonsterAlert.Broadcast(caller, rig.Monsters, 4));     // 请求已经取走;再叫的话 second 本来会被叫到
        Assert.Equal(MonsterMoveMode.Approach, ModeOf(first));
        Assert.Equal(MonsterMoveMode.Wander, ModeOf(second));
    }

    [Fact]
    public void ABroadcastWithNoCallerNoBrainOrNoRoomDoesNothing()
    {
        using var rig = new Rig();
        MonsterActor script = rig.Add(400, withBrain: false);
        MonsterActor ally = rig.Add(450);
        Assert.Equal(0, MonsterAlert.Broadcast(null, rig.Monsters, 4));
        Assert.Equal(0, MonsterAlert.Broadcast(script, rig.Monsters, 4));
        MonsterActor caller = Caller(rig);
        Assert.Equal(0, MonsterAlert.Broadcast(caller, null, 4));
        Assert.Equal(MonsterMoveMode.Wander, ModeOf(ally));
    }

    [Fact]
    public void ACallerWhoseApproachLeavesTheHeroOutOfSightCallsNobody()
    {
        using var rig = new Rig();
        MonsterActor caller = rig.Add(100 + 600, random: new Dial(0).AsFunc());   // 离主角 600 > 420
        caller.Brain.NotifyDamaged();
        MonsterActor ally = rig.Add(100 + 650);
        Assert.Equal(MonsterMoveMode.Approach, ModeOf(caller));
        Assert.Equal(0, MonsterAlert.Broadcast(caller, rig.Monsters, 4));
        Assert.Equal(MonsterMoveMode.Wander, ModeOf(ally));
    }

    [Fact]
    public void AHitThroughTheActorFarFromTheHeroAlertsAllies()
    {
        using var rig = new Rig();
        MonsterActor caller = rig.Add(400, hp: 10, random: new Dial(0).AsFunc());
        MonsterActor first = rig.Add(450);
        MonsterActor second = rig.Add(470);
        Assert.False(caller.ApplyHit(3, false, 0, false));
        Assert.Equal(1, MonsterAlert.Broadcast(caller, rig.Monsters, 0));
        Assert.Equal(MonsterMoveMode.Approach, ModeOf(first));
        Assert.Equal(MonsterMoveMode.Wander, ModeOf(second));
    }

    [Fact]
    public void AKillingBlowStillAlertsBecauseTheCallbackRunsBeforeTheDamageIsApplied()
    {
        using var rig = new Rig();
        MonsterActor caller = rig.Add(400, hp: 1, random: new Dial(0).AsFunc());
        MonsterActor ally = rig.Add(450);
        Assert.True(caller.ApplyHit(999, false, 0, false));
        Assert.False(caller.IsAlive);
        Assert.Equal(1, MonsterAlert.Broadcast(caller, rig.Monsters, 0));
        Assert.Equal(MonsterMoveMode.Approach, ModeOf(ally));
    }
}
