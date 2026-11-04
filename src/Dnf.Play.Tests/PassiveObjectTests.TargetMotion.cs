using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物出招脚本里朝目标的移动与转向(M70,证据 E-169),带着真的 .act 程序在迷你房间里跑:
/// [MOVE TO TARGET] 朝主角冲、计时到了停;[IS DIRECTION TO MOVE] 让出招里逐帧面朝目标;[CHANGE DIRECTION BY TARGET] 关掉"朝向跟着目标走"总开关。
/// 怪物与 .act 都是合成的。
/// </summary>
public partial class PassiveObjectTests
{
    /// <summary>主角一直往右走,直到站到怪物右边 6 像素以外。出招结束了就不再走(<paramref name="stopWhenAttackEnds"/>);走到了返回 true。</summary>
    private static bool WalkHeroPastMonster(ActArena arena, bool stopWhenAttackEnds = true, int maxSteps = 80)
    {
        RoomCombatSession s = arena.Session;
        MonsterActor m = arena.Monster;
        for (int i = 0; i < maxSteps; i++)
        {
            if (stopWhenAttackEnds && m.Phase != MonsterPhase.Attack)
            {
                return false;
            }
            s.Update(10, 1, 0, false, false);
            if (s.Hero.X > m.X + 6)
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void AnAttackThatMovesToTheTargetWalksToTheHeroAndStopsWhenItsTimerIsUp()
    {
        using var arena = new ActArena("xmoveto", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.Equal(340.0, m.X);
        Assert.True(arena.Until(() => m.ActSpeedX != 0) > 0);
        Assert.Equal(-(double)AvatarFixture.MoveToSpeed, m.ActSpeedX);       // 主角在左边:朝左冲
        Assert.Equal(0.0, m.ActSpeedY);                                      // 同一条纵深线
        Assert.Equal(MonsterPhase.Attack, m.Phase);

        int steps = arena.Until(() => m.ActSpeedX == 0);                     // 距离 40 ÷ 速度 400 = 100 毫秒后清零
        Assert.InRange(steps, 8, 13);
        Assert.Equal(MonsterPhase.Attack, m.Phase);                          // 出招还没放完
        Assert.InRange(m.X, 296.0, 304.0);                                   // 正好冲到主角站的地方(300)
        double stopped = m.X;
        arena.Steps(5);
        Assert.Equal(stopped, m.X);
        Assert.Equal(0.0, m.ActSpeedY);
    }

    [Fact]
    public void WithoutTheScriptTheMonsterStaysWhereItAttacksFrom()
    {
        using var arena = new ActArena("plain", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        arena.Steps(12);
        Assert.Equal(340.0, m.X);
    }

    [Fact]
    public void AnAttackThatTurnsTowardTheTargetFollowsTheHeroAcrossTheMonster()
    {
        using var arena = new ActArena("xtrack", monsterX: 312, useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.False(m.AttackFacing);                                        // 出生时没开
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        arena.Steps(2);
        Assert.True(m.AttackFacing);                                         // 脚本在出招开始时打开了
        Assert.True(m.FacingLeft);                                           // 主角在左边
        Assert.True(WalkHeroPastMonster(arena));
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.False(m.FacingLeft);                                          // 主角走到右边,出招里的怪物跟着转过去
    }

    [Fact]
    public void WithoutTheScriptAnAttackKeepsTheFacingItStartedWith()
    {
        using var arena = new ActArena("plain", monsterX: 312, useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.True(m.FacingLeft);
        Assert.True(WalkHeroPastMonster(arena));
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.True(m.FacingLeft);                                           // 对照:没打开逐帧面朝,出招里不转
    }

    [Fact]
    public void TheMasterFacingSwitchSwitchedOffByTheScriptBlocksTheTrackingToo()
    {
        using var arena = new ActArena("xtrackoff", monsterX: 312, useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        arena.Steps(2);
        Assert.False(m.FacesTarget);
        Assert.True(m.AttackFacing);
        Assert.True(WalkHeroPastMonster(arena));
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.True(m.FacingLeft);                                           // 总开关关着,逐帧面朝开着也不转
    }

    [Fact]
    public void TheScriptSwitchingTheFacingOffStopsTheMonstersOwnTurningAfterTheAttackToo()
    {
        using var arena = new ActArena("xface0", monsterX: 312, useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        arena.Steps(2);
        Assert.False(m.FacesTarget);
        Assert.True(arena.Until(() => m.Phase != MonsterPhase.Attack) > 0);
        Assert.True(m.FacingLeft);
        Assert.True(WalkHeroPastMonster(arena, stopWhenAttackEnds: false, maxSteps: 40));
        Assert.True(m.FacingLeft);                                           // 主角到了右边,收招后的怪物也不再转身去面对
    }

    [Fact]
    public void WithoutTheSwitchTheMonsterTurnsToFaceTheHeroAfterTheAttack()
    {
        using var arena = new ActArena("plain", monsterX: 312, useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.True(arena.Until(() => m.Phase != MonsterPhase.Attack) > 0);
        Assert.True(m.FacingLeft);
        Assert.True(WalkHeroPastMonster(arena, stopWhenAttackEnds: false, maxSteps: 40));
        arena.Steps(2);
        Assert.False(m.FacingLeft);                                          // 对照:总开关开着,站立或走路时跟着主角转
    }

    [Fact]
    public void TheAttackFacingSwitchDoesNotApplyInsideACustomAction()
    {
        // 自定义动作(住在出招阶段里)不追目标:出招起手转向主角(右),后来主角走到左边,循环的自定义动作里仍朝右
        using var arena = new ActArena("f_custloop", monsterX: 260, useVitals: false);
        MonsterActor m = arena.Monster;
        m.AttackFacing = true;
        Assert.True(arena.Until(() => m.InCustomAction) > 0);
        Assert.False(m.FacingLeft);
        for (int i = 0; i < 80 && arena.Session.Hero.X > m.X - 8; i++)
        {
            arena.Session.Update(10, -1, 0, false, false);
        }
        Assert.True(arena.Session.Hero.X < m.X - 8);                   // 主角到了怪物左边
        Assert.True(m.InCustomAction);
        Assert.False(m.FacingLeft);
    }
}
