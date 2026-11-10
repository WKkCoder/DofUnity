using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 呼叫同伴的规则(客户端 0x0088f170、收集 0x0088efb0 的静态读码):几率门、按难度的名额、收集条件、挑人的顺序与名额。
/// 数全是合成的;期望值来自反汇编里读到的比较方向与表项,不是从实现里抄的。
/// </summary>
public class MonsterAlertRulesTests
{
    private const int Team = 100;

    private static MonsterAlertCandidate Caller(int x = 0, int y = 0) => new MonsterAlertCandidate(Team, x, y, true, true, isCaller: true);

    private static MonsterAlertCandidate Ally(int x, int y = 0, int team = Team, bool alive = true, bool approaching = false) =>
        new MonsterAlertCandidate(team, x, y, alive, approaching);

    // ------------------------------------------------------------------ 几率门

    [Theory]
    [InlineData(0, true)]
    [InlineData(69, true)]
    [InlineData(70, false)]
    [InlineData(99, false)]
    [InlineData(100, true)]                 // 对 100 取余:100 → 0
    [InlineData(169, true)]
    [InlineData(170, false)]
    [InlineData(2147483647, true)]          // 余 47
    [InlineData(2147483599, false)]         // 余 99
    [InlineData(-1, true)]                  // 有符号取余得 -1,比较是有符号的(JGE),所以也过
    public void TheChanceGateIsTheRemainderModuloOneHundredBelowSeventy(int random, bool expected)
    {
        Assert.Equal(expected, MonsterAlertRules.Roll(random));
    }

    // ------------------------------------------------------------------ 难度名额

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 4)]
    [InlineData(3, 5)]
    [InlineData(4, 5)]
    public void TheAllyLimitIsTheFiveEntryTableByDifficulty(int difficulty, int expected)
    {
        Assert.Equal(expected, MonsterAlertRules.AllyLimit(difficulty));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    public void ADifficultyOutsideZeroToFourUsesTheMiddleTierLikeTheClientGetter(int difficulty)
    {
        Assert.Equal(4, MonsterAlertRules.AllyLimit(difficulty));       // 读取函数越界返回 2 → 表里第 2 项 = 4
    }

    // ------------------------------------------------------------------ 收集条件

    [Fact]
    public void TheCallerItselfIsNeverCollectedEvenWhenEverythingElseQualifies()
    {
        MonsterAlertCandidate caller = Caller();
        Assert.False(MonsterAlertRules.IsCollected(caller, 200, caller));
        Assert.True(MonsterAlertRules.IsCollected(caller, 200, Ally(0)));       // 同一个位置的别的怪物照收
    }

    [Fact]
    public void OnlySameTeamMonstersAreCollected()
    {
        Assert.True(MonsterAlertRules.IsCollected(Caller(), 200, Ally(50, team: 100)));
        Assert.False(MonsterAlertRules.IsCollected(Caller(), 200, Ally(50, team: 101)));
        Assert.False(MonsterAlertRules.IsCollected(Caller(), 200, Ally(50, team: 0)));
    }

    [Fact]
    public void DeadMonstersAreNotCollected()
    {
        Assert.False(MonsterAlertRules.IsCollected(Caller(), 200, Ally(50, alive: false)));
    }

    [Fact]
    public void TheDistanceTestUsesTheCallersSightTimesOnePointFourAndIncludesTheLimit()
    {
        // 视野 100 → 上限 140
        Assert.True(MonsterAlertRules.IsCollected(Caller(), 100, Ally(140)));
        Assert.False(MonsterAlertRules.IsCollected(Caller(), 100, Ally(141)));
        Assert.True(MonsterAlertRules.IsCollected(Caller(), 100, Ally(-140)));
        // 放大后的视野 150 → 上限 210
        Assert.True(MonsterAlertRules.IsCollected(Caller(), 150, Ally(210)));
        Assert.False(MonsterAlertRules.IsCollected(Caller(), 150, Ally(211)));
    }

    [Fact]
    public void TheDistanceIsMeasuredFromTheCallerNotFromTheOrigin()
    {
        MonsterAlertCandidate caller = Caller(1000, 300);
        Assert.True(MonsterAlertRules.IsCollected(caller, 100, Ally(1140, 300)));
        Assert.False(MonsterAlertRules.IsCollected(caller, 100, Ally(140, 300)));       // 离原点近、离呼叫者远
        Assert.True(MonsterAlertRules.IsCollected(caller, 100, Ally(900, 300)));
    }

    [Fact]
    public void TheVerticalGapCountsThreePointFourFiveTimesAsMuchInTheDistance()
    {
        // 视野 100 → 上限 140:纵差 40 折成 137(取整)在内,纵差 41 折成 141 在外
        Assert.True(MonsterAlertRules.IsCollected(Caller(0, 100), 100, Ally(0, 140)));
        Assert.False(MonsterAlertRules.IsCollected(Caller(0, 100), 100, Ally(0, 141)));
    }

    [Fact]
    public void ASightOfZeroCollectsEveryQualifyingMonsterNoMatterHowFar()
    {
        Assert.True(MonsterAlertRules.IsCollected(Caller(), 0, Ally(100000, 5000)));
    }

    // ------------------------------------------------------------------ 挑人

    [Fact]
    public void PickingFollowsTheRoomOrderAndStopsWhenTheLimitIsFilled()
    {
        var room = new List<MonsterAlertCandidate> { Ally(10), Ally(20), Ally(30), Ally(40), Ally(50), Ally(60) };
        Assert.Equal(new[] { 0, 1, 2 }, MonsterAlertRules.Pick(Caller(), 200, room, 3));
        Assert.Equal(new[] { 0 }, MonsterAlertRules.Pick(Caller(), 200, room, 1));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, MonsterAlertRules.Pick(Caller(), 200, room, 9));        // 名额多于候选:全叫
    }

    [Fact]
    public void ThePickedOnesAreTheFirstQualifyingNotTheClosest()
    {
        // 房间顺序是 远、近、中;名额 2 → 取房间里靠前的两个(下标 0、1),不按距离排
        var room = new List<MonsterAlertCandidate> { Ally(250), Ally(10), Ally(100) };
        Assert.Equal(new[] { 0, 1 }, MonsterAlertRules.Pick(Caller(), 200, room, 2));
    }

    [Fact]
    public void TheCallerInTheRoomListIsSkippedAndDoesNotUseUpASlot()
    {
        var room = new List<MonsterAlertCandidate> { Caller(), Ally(10), Ally(20) };
        Assert.Equal(new[] { 1 }, MonsterAlertRules.Pick(Caller(), 200, room, 1));
        Assert.Equal(new[] { 1, 2 }, MonsterAlertRules.Pick(Caller(), 200, room, 2));
    }

    [Fact]
    public void AnAllyAlreadyApproachingIsSkippedWithoutUsingUpASlot()
    {
        var room = new List<MonsterAlertCandidate> { Ally(10, approaching: true), Ally(20), Ally(30) };
        Assert.Equal(new[] { 1 }, MonsterAlertRules.Pick(Caller(), 200, room, 1));              // 名额 1:跳过下标 0,叫下标 1
        var two = new List<MonsterAlertCandidate> { Ally(10, approaching: true), Ally(20, approaching: true), Ally(30), Ally(40), Ally(50) };
        Assert.Equal(new[] { 2, 3 }, MonsterAlertRules.Pick(Caller(), 200, two, 2));
    }

    [Fact]
    public void AllApproachingAlliesMeansNobodyIsCalled()
    {
        var room = new List<MonsterAlertCandidate> { Ally(10, approaching: true), Ally(20, approaching: true) };
        Assert.Empty(MonsterAlertRules.Pick(Caller(), 200, room, 5));
    }

    [Fact]
    public void MonstersThatFailTheCollectionTestDoNotUseUpSlots()
    {
        var room = new List<MonsterAlertCandidate>
        {
            Ally(10, alive: false),           // 死了
            Ally(20, team: 7),                // 别的队
            Ally(900),                        // 太远(视野 200 → 上限 280)
            Ally(30),
            Ally(40),
        };
        Assert.Equal(new[] { 3, 4 }, MonsterAlertRules.Pick(Caller(), 200, room, 2));
    }

    [Fact]
    public void ASlotIsUsedPerCalledAllyWhateverBecomesOfIt()
    {
        // 名额按"叫了几个"数,与被叫的靠近成没成无关:规则这一层只知道叫了谁
        var room = new List<MonsterAlertCandidate> { Ally(10), Ally(20), Ally(30) };
        Assert.Equal(2, MonsterAlertRules.Pick(Caller(), 200, room, 2).Count);
    }

    [Fact]
    public void ALimitOfZeroOrLessPicksNobodyAndAMissingRoomIsEmpty()
    {
        var room = new List<MonsterAlertCandidate> { Ally(10) };
        Assert.Empty(MonsterAlertRules.Pick(Caller(), 200, room, 0));
        Assert.Empty(MonsterAlertRules.Pick(Caller(), 200, room, -3));
        Assert.Empty(MonsterAlertRules.Pick(Caller(), 200, null!, 3));
    }

    [Fact]
    public void ABiggerSightReachesFartherAlliesInTheSameRoom()
    {
        var room = new List<MonsterAlertCandidate> { Ally(100), Ally(300) };
        Assert.Equal(new[] { 0 }, MonsterAlertRules.Pick(Caller(), 100, room, 5));      // 上限 140
        Assert.Equal(new[] { 0 }, MonsterAlertRules.Pick(Caller(), 200, room, 5));      // 上限 280
        Assert.Equal(new[] { 0, 1 }, MonsterAlertRules.Pick(Caller(), 220, room, 5));   // 上限 308
    }

    // ------------------------------------------------------------------ r14:收集里的排除标志(槽 +0x670)

    private static MonsterAlertCandidate Flagged(int x, int y = 0) =>
        new MonsterAlertCandidate(Team, x, y, true, false, flagged: true);

    [Fact]
    public void AFlaggedMonsterIsNotCollectedEvenWhenEverythingElseQualifies()
    {
        Assert.False(MonsterAlertRules.IsCollected(Caller(), 200, Flagged(10)));
        Assert.True(MonsterAlertRules.IsCollected(Caller(), 200, Ally(10)));
    }

    [Fact]
    public void AFlaggedMonsterDoesNotUseUpASlot()
    {
        // 名额 2:房间里 Flagged 排在最前面,被它占去名额就只能挑到一个
        var room = new List<MonsterAlertCandidate> { Caller(), Flagged(10), Ally(20), Ally(30), Ally(40) };
        Assert.Equal(new[] { 2, 3 }, MonsterAlertRules.Pick(Caller(), 200, room, 2));
    }

    [Fact]
    public void TheFlagDefaultsToFalseSoExistingHostsAreUnaffected()
    {
        Assert.False(new MonsterAlertCandidate(Team, 0, 0, true, false).Flagged);
        Assert.False(new MonsterAlertCandidate(Team, 0, 0, true, false, true).Flagged);     // 第六个参数是"自己"
        Assert.True(new MonsterAlertCandidate(Team, 0, 0, true, false, false, true).Flagged);
    }

    // ------------------------------------------------------------------ r14:受击回调里的攻击者门(0x0089cea4)

    [Theory]
    [InlineData(0x211)]     // 怪物本体
    [InlineData(0x111)]     // AI 角色类
    [InlineData(0x11)]
    [InlineData(0x1211)]
    [InlineData(0x1111)]
    public void AttackersWithBothAiCharacterBitsNeverOpenTheAlert(int kind)
    {
        Assert.False(MonsterAlertRules.AttackerOpensTheAlert(true, kind));
    }

    [Theory]
    [InlineData(0x21)]
    [InlineData(0x121)]
    [InlineData(0x221)]
    [InlineData(0x421)]
    [InlineData(0x821)]
    [InlineData(0x1)]       // 只有第 0 位
    [InlineData(0x10)]      // 只有第 4 位:两位要同时在才算 AI 角色一族
    public void AttackersMissingEitherBitOpenTheAlertWhenTheyHaveAnOwner(int kind)
    {
        Assert.True(MonsterAlertRules.AttackerOpensTheAlert(true, kind));
    }

    [Theory]
    [InlineData(0x21)]
    [InlineData(0x211)]
    [InlineData(0)]
    public void WithoutAResponsibleActorTheAlertNeverOpens(int kind)
    {
        Assert.False(MonsterAlertRules.AttackerOpensTheAlert(false, kind));
    }

    [Fact]
    public void TheKindMaskIsTheTwoLowBitsOfTheMonsterKindAndTheAiCharacterKind()
    {
        Assert.Equal(0x11, MonsterAlertRules.AiCharacterKindMask);
        Assert.Equal(0x11, 0x211 & MonsterAlertRules.AiCharacterKindMask);
        Assert.Equal(0x11, 0x111 & MonsterAlertRules.AiCharacterKindMask);
    }
}
