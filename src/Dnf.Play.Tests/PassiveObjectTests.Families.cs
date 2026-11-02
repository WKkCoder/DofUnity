using Dnf.Game.Act;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物非出招动作族的 .act 触发器宿主(M53,证据 E-146):站立、行走、受击、倒地、过程、最后一幕各有自己的 .act,触发器与出招的一样,
/// 但只在这一族是当前动作时跑(过程动作出生就跑、最后一幕血量归零才跑),挨打与打中主角的事件发给当前动作与过程动作,别的族的 [TRIGGER] 开关写进它们自己的开关数组。
/// 怪物与 .act 全是合成的:f_ 开头的几只(见 <see cref="AvatarFixture"/> 的 FamilyMobs)。放出来的 ActForeverId 对象不会消失,会话里的被动对象数就是触发次数。
/// 主角站在 (300, 100),怪物站在 (340, 100) 朝左;出招后的冷却是 1500 毫秒,冷却里怪物站着。
/// </summary>
public partial class PassiveObjectTests
{
    [Fact]
    public void AStandingScriptRunsOnlyWhileTheMonsterStandsAndStartsOverEachTime()
    {
        using var arena = new ActArena("f_stay", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(m.HasFamilyActs);
        Assert.False(m.HasActs);                                    // 出招本身没有触发器
        Assert.Null(m.FamilyRunner(MonsterActFamily.Stay));
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.Null(m.ActiveFamily);                                // 出招不属于任何一族
        Assert.Null(m.FamilyRunner(MonsterActFamily.Stay));

        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
        Assert.Equal(MonsterActFamily.Stay, m.ActiveFamily);
        Assert.NotNull(m.FamilyRunner(MonsterActFamily.Stay));
        Assert.Equal(0, arena.Session.PassivesSpawned);
        int steps = arena.Until(() => arena.Session.PassivesSpawned > 0);
        Assert.InRange(steps, 8, 12);                               // 站立动画帧 1 从 100 毫秒起
        arena.Steps(50);
        Assert.Equal(1, arena.Session.PassivesSpawned);             // [LIMIT] 1:这一次站立只放一个
        Assert.Equal(MonsterPhase.Wait, m.Phase);

        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);        // 冷却完又出招:站立的运行器撤掉
        Assert.Null(m.FamilyRunner(MonsterActFamily.Stay));
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 1) > 0);    // 再站一次:运行器重新建,[LIMIT] 的次数从零数
        Assert.Equal(2, arena.Session.PassivesSpawned);
        Assert.Equal(2, m.FamilyRunsStarted);
    }

    [Fact]
    public void AWalkingScriptSetsItsSpeedForTheWalkOnlyAndTheSpeedEndsWithIt()
    {
        using var fam = new ActArena("f_move", monsterX: 500, useVitals: false);
        using var control = new ActArena("f_plain", monsterX: 500, useVitals: false);
        Assert.True(fam.Monster.HasFamilyActs);
        Assert.False(control.Monster.HasFamilyActs);
        fam.Steps(30);
        control.Steps(30);
        Assert.Equal(MonsterPhase.Walk, fam.Monster.Phase);
        Assert.Equal(MonsterActFamily.Move, fam.Monster.ActiveFamily);
        Assert.Equal(-100.0, fam.Monster.ActSpeedX);                // 朝左取反
        double extra = control.Monster.X - fam.Monster.X;           // 比对照多走的:100 像素/秒 × 0.3 秒
        Assert.InRange(extra, 25.0, 31.0);

        Assert.True(fam.Until(() => fam.Monster.Phase == MonsterPhase.Attack, 600) > 0);
        Assert.Equal(0.0, fam.Monster.ActSpeedX);                   // 出招一开始(没有出招脚本)脚本给的速度就清了
        Assert.Null(fam.Monster.FamilyRunner(MonsterActFamily.Move));
    }

    [Fact]
    public void TheProcScriptRunsFromBirthAndStaysAliveWhileTheMonsterFights()
    {
        using var arena = new ActArena("f_procboot", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.NotNull(m.FamilyRunner(MonsterActFamily.Proc));      // 装上就开始跑
        arena.Steps(2);
        Assert.Equal(1, arena.Session.PassivesSpawned);             // 出生那一步 [FRAME] 0 0 放了一个
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.Null(m.ActiveFamily);
        Assert.NotNull(m.FamilyRunner(MonsterActFamily.Proc));      // 出招时过程动作照跑
        arena.Steps(200);
        Assert.Equal(1, arena.Session.PassivesSpawned);             // 单次位挡住重复(没有动画的动作帧号固定 0)
        Assert.Equal(1, m.FamilyRunsStarted);
    }

    [Fact]
    public void BeingHitWakesTheProcScriptWhateverTheMonsterIsDoingAndDeathStopsIt()
    {
        using var attacking = new ActArena("f_procdmg", useVitals: false);
        MonsterActor a = attacking.Monster;
        attacking.Steps(3);
        Assert.Equal(MonsterPhase.Attack, a.Phase);
        Assert.Equal(0, attacking.Session.PassivesSpawned);
        a.ApplyHit(1, false, 0, false);                             // 出招时挨一下:过程动作的 [ON DAMAGE] 触发
        Assert.Equal(1, attacking.Session.PassivesSpawned);

        using var standing = new ActArena("f_procdmg", useVitals: false);
        MonsterActor s = standing.Monster;
        Assert.True(standing.Until(() => s.Phase == MonsterPhase.Wait && s.AttackIndex == 0 && s.SwingNumber > 0) > 0);
        s.ApplyHit(1, false, 0, false);                             // 站着时挨一下
        Assert.Equal(1, standing.Session.PassivesSpawned);

        s.ApplyHit(1000, false, 0, false);                          // 打死:过程动作退场
        standing.Steps(2);
        Assert.Null(s.FamilyRunner(MonsterActFamily.Proc));
    }

    [Fact]
    public void TheHurtAndKnockdownScriptsRunWhenThatAnimationPlaysAndTheNextHitStartsThemOver()
    {
        using var arena = new ActArena("f_dmg", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(m.HasFamilyActs);
        arena.Steps(3);
        m.ApplyHit(1, false, 0, false);                             // 普通受击:第一次放受击动作一
        arena.Steps(2);
        Assert.Equal(MonsterPhase.Hurt, m.Phase);
        Assert.Equal(MonsterActFamily.Damage1, m.ActiveFamily);
        Assert.Equal(1, arena.Session.PassivesSpawned);             // [ON SET ACTION]

        m.ApplyHit(1, true, 0, false);                              // 倒地攻击(占位路径,放倒地动作)
        arena.Steps(2);
        Assert.Equal(MonsterActFamily.Down, m.ActiveFamily);
        Assert.Equal(2, arena.Session.PassivesSpawned);
        Assert.Null(m.FamilyRunner(MonsterActFamily.Damage1));
    }

    [Fact]
    public void TheLastScriptRunsFromZeroHpAndKeepsGoingAfterTheBodyIsGone()
    {
        using var arena = new ActArena("f_last", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.False(m.LastActRunning);
        arena.Steps(3);
        Assert.Equal(0, arena.Session.PassivesSpawned);
        Assert.True(m.ApplyHit(1000, false, 0, false));
        arena.Steps(2);
        Assert.True(m.LastActRunning);
        Assert.Equal(1, arena.Session.PassivesSpawned);             // 血量归零那一步:[ON SET ACTION] 放了一个(拥有者算活着,最后一幕里脚本照样能放东西)
        Assert.NotEqual(MonsterPhase.Gone, m.Phase);

        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Gone) > 0);       // 身体 500 毫秒后消失,最后一幕(1200 毫秒)还在跑
        Assert.True(m.LastActRunning);
        Assert.Equal(1, arena.Session.PassivesSpawned);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 1) > 0);  // 帧 2(800 毫秒)的触发器在消失之后才到
        Assert.Equal(MonsterPhase.Gone, m.Phase);
        Assert.Equal(2, arena.Session.PassivesSpawned);
        arena.Steps(100);
        Assert.False(m.LastActRunning);                             // 动画播完再留一小会儿就收
        Assert.Equal(2, arena.Session.PassivesSpawned);
        Assert.Null(m.FamilyRunner(MonsterActFamily.Proc));
    }

    [Fact]
    public void AScriptSwitchesAnotherFamilysTriggerAndTheOtherFamilyHonoursIt()
    {
        // 站立的 [ON SET ACTION] 把过程动作触发器 0(出生时关着)打开:之后挨一下,过程动作才放东西
        using var arena = new ActArena("f_procsw", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.Equal(false, m.FamilyTriggerState(MonsterActFamily.Proc, 0));
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
        arena.Steps(2);
        Assert.Equal(true, m.FamilyTriggerState(MonsterActFamily.Proc, 0));
        Assert.Equal((1, 0, 0), (m.ActSwitchApplied, m.ActSwitchMissed, m.ActSwitchUnhosted));
        Assert.Equal(0, arena.Session.PassivesSpawned);
        m.ApplyHit(1, false, 0, false);
        Assert.Equal(1, arena.Session.PassivesSpawned);
        Assert.Null(m.FamilyTriggerState(MonsterActFamily.Move, 0));  // 没装程序的族:没有开关数组

        // 出招里的 [TRIGGER] [STAY] 0 [OFF]:站立动作的触发器 0 在站立之前就关了,之后站着也不放东西
        using var off = new ActArena("f_xstay", useVitals: false);
        MonsterActor o = off.Monster;
        Assert.Equal(true, o.FamilyTriggerState(MonsterActFamily.Stay, 0));
        Assert.True(off.Until(() => o.FamilyTriggerState(MonsterActFamily.Stay, 0) == false) > 0);
        Assert.Equal(1, o.ActSwitchApplied);
        Assert.True(off.Until(() => o.Phase == MonsterPhase.Wait) > 0);
        Assert.NotNull(o.FamilyRunner(MonsterActFamily.Stay));
        off.Steps(60);
        Assert.Equal(0, off.Session.PassivesSpawned);
        Assert.Equal(false, o.FamilyTriggerState(MonsterActFamily.Stay, 0));
    }

    [Fact]
    public void SetActionFromAStandingScriptStartsTheAttackButNotWhileTheAttackDelayRuns()
    {
        // 出生时没有冷却:站立 100 毫秒后脚本切到第 1 招(主角在视野外,怪物自己不出招)
        using var arena = new ActArena("f_stayset", monsterX: 900, useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.Equal(1, m.AttackIndex);
        Assert.Equal((1, 0), (m.FamilySetActionApplied, m.FamilySetActionUnhosted));

        // 在视野里:怪物先自己出第 0 招;出完进了间隔,站立脚本 100 毫秒后切招,间隔没过不接(记没落实);间隔过了怪物自己再出招
        using var near = new ActArena("f_stayset", useVitals: false);
        MonsterActor n = near.Monster;
        Assert.True(near.Until(() => n.Phase == MonsterPhase.Attack) > 0);
        Assert.Equal(0, n.AttackIndex);
        Assert.True(near.Until(() => n.Phase == MonsterPhase.Wait) > 0);
        near.Steps(30);                                             // 500 毫秒:脚本早已触发过
        Assert.Equal(MonsterPhase.Wait, n.Phase);
        Assert.Equal((0, 1), (n.FamilySetActionApplied, n.FamilySetActionUnhosted));
        Assert.True(near.Until(() => n.Phase == MonsterPhase.Attack) > 0);
        Assert.Equal(0, n.AttackIndex);                             // 间隔过了是怪物自己出的招,不是脚本切的

        using var unwired = new ActArena("f_stayunh", useVitals: false);
        MonsterActor u = unwired.Monster;
        Assert.True(unwired.Until(() => u.FamilySetActionUnhosted > 0) > 0);       // [SET ACTION] [CUSTOM]:怪物还没有自定义动作
        Assert.Equal((0, 1), (u.FamilySetActionApplied, u.FamilySetActionUnhosted));
        Assert.Equal(MonsterPhase.Wait, u.Phase);

        // [TRIGGER] 指向还没有宿主的族([DEFAULT])只记数;[CUSTOM] 有宿主了(M62-b,E-159),这只怪物没有自定义动作,落空
        using var xunh = new ActArena("f_xunh", useVitals: false);
        MonsterActor x = xunh.Monster;
        Assert.True(xunh.Until(() => x.ActSwitchUnhosted > 0, 100) > 0);
        xunh.Steps(5);
        Assert.Equal((0, 1, 1), (x.ActSwitchApplied, x.ActSwitchMissed, x.ActSwitchUnhosted));
    }

    [Fact]
    public void AHopInAWalkingScriptThatStopsRisingInTheAirComesBackDown()
    {
        using var arena = new ActArena("f_hop", monsterX: 500, useVitals: false);
        MonsterActor m = arena.Monster;
        double top = 0;
        for (int i = 0; i < 45; i++)                                // 0.45 秒(一步 10 毫秒):上升 100 毫秒,脚本把 Z 速度设成 0 之后再落约 270 毫秒
        {
            arena.Steps(1);
            top = System.Math.Max(top, m.Z);
        }
        Assert.InRange(top, 20.0, 80.0);
        Assert.Equal(0.0, m.Z);                                     // 速度设成 0 不是"悬在半空":受重力落回地面
        Assert.Equal(MonsterPhase.Walk, m.Phase);                   // 落地时还在走

        // 在半空里动作被换掉(挨了一下进受击):不悬停的怪物也受重力落回地面,不留在主角够不着的高度
        using var cut = new ActArena("f_hop", monsterX: 500, useVitals: false);
        MonsterActor c = cut.Monster;
        cut.Steps(3);
        Assert.True(c.Z > 10.0);
        Assert.False(c.ApplyHit(1, false, 0, false));               // 没打死
        Assert.Equal(MonsterPhase.Hurt, c.Phase);
        Assert.True(cut.Until(() => c.Z == 0.0, 200) > 0);
    }

    [Fact]
    public void WithoutALibraryOrWithoutTriggersNoFamilyScriptRuns()
    {
        using var detached = new ActArena("f_stay", useVitals: false, attach: false);
        Assert.False(detached.Monster.HasFamilyActs);
        detached.Steps(150);
        Assert.Equal(0, detached.Session.PassivesSpawned);

        using var plain = new ActArena("f_plain", useVitals: false);
        Assert.False(plain.Monster.HasFamilyActs);                  // 各族的 .act 只有动画、没有触发器
        plain.Steps(100);
        Assert.Equal(0, plain.Session.PassivesSpawned);

        using var attached = new ActArena("f_stay", useVitals: false);
        Assert.True(attached.Monster.HasFamilyActs);
        attached.Session.AttachActs(null);                          // 撤掉
        Assert.False(attached.Monster.HasFamilyActs);
        Assert.Null(attached.Monster.ActiveFamily);
        attached.Steps(150);
        Assert.Equal(0, attached.Session.PassivesSpawned);
    }
}
