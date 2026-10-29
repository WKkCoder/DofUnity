using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物 .act 里的 [ACTIVE STATUS] 落到主角身上(M51,证据 E-144):脚本按帧对攻击目标挂一个异常状态,走与 .atk 状态槽同一套规则
/// (<see cref="Dnf.Game.ActiveStatusSet.Apply"/>:概率掷骰、持续时间、失去控制),掷中失去控制类状态就把主角留在受击姿势里;
/// 没有状态表(没有这套系统)或没有生命条(无伤害模式)时不挂;怪物与被动对象这一版不接状态。出招 .act 见
/// <see cref="Dnf.Render.Tests.AvatarFixture.ActMobPath"/>:status(概率 100)与 statusmiss(概率 0),都是合成的。
/// </summary>
public partial class PassiveObjectTests
{
    private static ActiveStatusTable ActStatusTable()
    {
        var seconds = new int[ActiveStatusTable.KindCount];
        for (int k = 0; k < seconds.Length; k++)
        {
            seconds[k] = 3 + k;
        }
        return ActiveStatusTable.Synthetic(11, seconds, new float[ActiveStatusTable.KindCount * 2], new float[ActiveStatusTable.KindCount * 2], null, 1f, 1f, 400);
    }

    [Fact]
    public void AnAttackScriptsActiveStatusLandsOnTheHeroAtTheScriptedFrameAndHoldsItHelpless()
    {
        using var arena = new ActArena("status", frozenHero: true);
        RoomCombatSession s = arena.Session;
        s.UseStatuses(ActStatusTable());
        TownHero hero = s.Hero;
        Assert.Equal((0, 0), (s.ActStatusRequests, s.StatusApplied));

        int steps = arena.Until(() => s.ActStatusRequests > 0);
        Assert.InRange(steps, 8, 13);                                       // 帧 1 从 80 毫秒起
        Assert.Equal((1, 1, 0), (s.ActStatusRequests, s.StatusApplied, s.StatusMissed));
        Assert.True(s.HeroStatus.Has(ActiveStatusKind.Stun));
        Assert.True(s.HeroStatus.ControlLocked);
        Assert.Equal(HeroMotion.Hurt, hero.Motion);                         // 当场留在受击姿势里
        Assert.Equal(1000, s.Vitals.Hp);                                    // 只有状态,没有伤害
        for (int i = 0; i < 30; i++)                                        // 方向键、攻击键、跳跃键都没有用
        {
            s.Update(10, 1, -1, i % 3 == 0, i % 5 == 0);
            Assert.Equal(HeroMotion.Hurt, hero.Motion);
            Assert.True(s.HeroStatus.ControlLocked);
            Assert.False(hero.IsAttacking);
        }
    }

    [Fact]
    public void AScriptedStatusRollsItsProbabilityLikeAnAttackInfoSlot()
    {
        using var arena = new ActArena("statusmiss", frozenHero: true);
        RoomCombatSession s = arena.Session;
        s.UseStatuses(ActStatusTable());

        Assert.True(arena.Until(() => s.ActStatusRequests > 0) > 0);
        Assert.Equal((1, 0, 1), (s.ActStatusRequests, s.StatusApplied, s.StatusMissed));   // 概率 0:掷了没中
        Assert.False(s.HeroStatus.ControlLocked);
        Assert.NotEqual(HeroMotion.Hurt, s.Hero.Motion);
        Assert.Empty(s.HeroStatus.Active);
    }

    [Fact]
    public void AScriptedStatusIsIgnoredWithoutAStatusSystemOrWithoutVitalsAndMonstersTakeNone()
    {
        using var noTable = new ActArena("status", frozenHero: true);       // 没装状态表
        noTable.Steps(80);
        Assert.Null(noTable.Session.HeroStatus);
        Assert.Equal(0, noTable.Session.ActStatusRequests);
        Assert.NotEqual(HeroMotion.Hurt, noTable.Session.Hero.Motion);

        using var noVitals = new ActArena("status", useVitals: false, frozenHero: true);   // 无伤害模式:没有生命条
        noVitals.Session.UseStatuses(ActStatusTable());
        noVitals.Steps(80);
        Assert.Equal((0, 0), (noVitals.Session.ActStatusRequests, noVitals.Session.StatusApplied));
        Assert.NotEqual(HeroMotion.Hurt, noVitals.Session.Hero.Motion);

        // 怪物自己(与被动对象)这一版没有状态系统:收到挂状态的请求什么都不做
        IActEntity self = noVitals.Monster.ActSelf;
        var entry = ActiveStatusEntry.Create(ActiveStatusKind.Stun, 100f, 0, 2000);
        self.ApplyStatus(entry, null);
        Assert.True(self.IsAlive);
    }
}
