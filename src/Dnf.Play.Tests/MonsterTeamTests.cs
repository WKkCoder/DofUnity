using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// [SET TEAM] 在怪物宿主上的落点(客户端怪物类的 setTeam 是基类实现 0x00442c90:只把队号字段写成参数;getTeam 读同一个字段):
/// 队号只存在怪物自己身上,叫同伴按"队号相同"收集,所以改过队号的怪物进出收集范围。数全是合成的。
/// </summary>
public sealed class MonsterTeamTests
{
    private const int HeroX = 100;
    private const int HeroY = 100;

    private static MonsterBrain NewBrain(int roll, int sight = 200)
    {
        var traits = new MonsterBrainTraits(true, false, false, false, false, false, false, 40);
        return new MonsterBrain(sight, 50, traits, 2000, false, () => roll);
    }

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

        /// <summary>造一只怪物并装一个走位大脑(随机数 <paramref name="roll"/>:0 一定掷中叫人,99 一定不叫),让它知道主角在哪。</summary>
        public MonsterActor Add(int x, int roll = 99)
        {
            var spawn = new RoomMonsterSpawn(1, 0, 0, x, HeroY, 0, 1, 0, "[fixed]", "[normal]");
            var m = new MonsterActor(Monsters.Count, spawn, _model, 3, 100, 100, 1500, 400);
            m.SetDefaultAi(NewBrain(roll));
            m.Brain.Update(0, true, m.X, m.Y, HeroX, HeroY);
            Monsters.Add(m);
            return m;
        }

        public void Dispose()
        {
            _assets.Dispose();
            _fx.Dispose();
        }
    }

    [Fact]
    public void AMonsterIsOnTheDefaultMonsterTeamUntilTheScriptChangesIt()
    {
        using var rig = new Rig();
        IActEntity self = rig.Add(400).ActSelf;
        Assert.Null(self.OverrideActTeam);
        Assert.Equal(100, self.Team);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(0)]        // 0 是主角队号:是一个有效的写入值,不能被当成"没写过"
    [InlineData(11)]
    [InlineData(100)]      // 写成与默认相同的值也算写过
    [InlineData(-1)]
    public void SetTeamWritesTheFieldAndTheNewValueIsWhatEveryoneReads(int team)
    {
        using var rig = new Rig();
        IActEntity self = rig.Add(400).ActSelf;
        self.SetActTeam(team);
        Assert.Equal(team, self.OverrideActTeam);
        Assert.Equal(team, self.Team);
    }

    [Fact]
    public void ASecondWriteReplacesTheFirstAndTheTeamBelongsToThatMonsterOnly()
    {
        using var rig = new Rig();
        IActEntity a = rig.Add(400).ActSelf;
        IActEntity b = rig.Add(450).ActSelf;
        a.SetActTeam(7);
        a.SetActTeam(3);
        Assert.Equal(3, a.Team);
        Assert.Equal(100, b.Team);
        Assert.Null(b.OverrideActTeam);
    }

    // ------------------------------------------------------------------ 叫同伴按队号收集

    /// <summary>呼叫者在 (400, 100)(离主角 300,在视野 200 放大后的 420 之内),随机数 0 一定掷中;同伴在 (450, 100)。呼叫者先挨一下留下请求。</summary>
    private static (MonsterActor caller, MonsterActor ally) CallerAndAlly(Rig rig, int? callerTeam, int? allyTeam)
    {
        MonsterActor caller = rig.Add(400, roll: 0);
        MonsterActor ally = rig.Add(450);
        if (callerTeam.HasValue)
        {
            caller.ActSelf.SetActTeam(callerTeam.Value);
        }
        if (allyTeam.HasValue)
        {
            ally.ActSelf.SetActTeam(allyTeam.Value);
        }
        caller.Brain.NotifyDamaged();
        return (caller, ally);
    }

    [Theory]
    [InlineData(null, null, true)]      // 两边都是默认的 100:被叫
    [InlineData(null, 7, false)]        // 同伴改成了别的队:不再算同伴
    [InlineData(7, null, false)]        // 呼叫者改了队,同伴还在 100:同样隔开
    [InlineData(7, 7, true)]            // 两边改成同一队:又是同伴
    [InlineData(0, 0, true)]            // 队号 0 也是有效队号
    [InlineData(0, null, false)]
    [InlineData(null, 100, true)]       // 写成与默认相同的值没有区别
    public void TheAlertGathersOnlyAlliesOnTheCallersTeamAsItIsNow(int? callerTeam, int? allyTeam, bool called)
    {
        using var rig = new Rig();
        (MonsterActor caller, MonsterActor ally) = CallerAndAlly(rig, callerTeam, allyTeam);
        Assert.Equal(MonsterMoveMode.Approach, caller.Brain.Mode);
        Assert.Equal(called ? 1 : 0, MonsterAlert.Broadcast(caller, rig.Monsters, 0));
        Assert.Equal(called ? MonsterMoveMode.Approach : MonsterMoveMode.Wander, ally.Brain.Mode);
    }

    [Fact]
    public void ARoomOfTwoTeamsCallsOnlyTheCallersOwnTeamAndTheOtherTeamDoesNotUseUpTheQuota()
    {
        using var rig = new Rig();
        MonsterActor caller = rig.Add(400, roll: 0);
        MonsterActor other = rig.Add(450);          // 房间顺序里排在前面的是另一队
        MonsterActor mate = rig.Add(470);
        other.ActSelf.SetActTeam(5);
        caller.Brain.NotifyDamaged();
        Assert.Equal(1, MonsterAlert.Broadcast(caller, rig.Monsters, 0));   // 难度 0 名额 1:另一队不占名额,名额给了同队的
        Assert.Equal(MonsterMoveMode.Wander, other.Brain.Mode);
        Assert.Equal(MonsterMoveMode.Approach, mate.Brain.Mode);
    }
}
