using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物的自定义动作(.mob 的 [etc action];M62-b,证据 E-159):脚本 [SET ACTION] [CUSTOM] n 切进列表里的第 n 个动作,
/// 放它自己的动画、跑它自己的触发器。运行模型是推断(客户端的状态机一侧没读到):住在出招阶段里、不吃出招速率、不算一次出招、
/// 循环动画只有脚本或挨打能结束……怪物与 .act 全是合成的(f_cust 开头的几只,见 <see cref="AvatarFixture"/> 的 FamilyMobs 与 CustomScripts)。
/// 主角站在 (300, 100),怪物站在 (340, 100) 朝左;出招后的冷却是 1500 毫秒;每一步 10 毫秒。
/// </summary>
public partial class PassiveObjectTests
{
    [Fact]
    public void AnAttackScriptThatSetsACustomActionPlaysItsOwnAnimationAndTriggersAndFinishesLikeAnAttack()
    {
        using var arena = new ActArena("f_custnow", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(m.HasCustomActions);
        Assert.Equal(2, m.CustomActionCount);
        Assert.Equal(AvatarFixture.CustomAAni, m.AnimationPathOf(m.CustomAni(0)));
        Assert.Equal(AvatarFixture.CustomBAni, m.AnimationPathOf(m.CustomAni(1)));
        Assert.False(m.CustomExists(2));
        Assert.Equal(-1, m.CurrentCustomIndex);
        Assert.Equal(1, arena.Session.CustomActMonsters);

        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.Equal(StrikeAniPath, m.CurrentAnimationPath);
        int toCustom = arena.Until(() => m.InCustomAction);
        Assert.InRange(toCustom, 6, 10);                                 // 出招第 1 帧(80 毫秒)脚本切过来
        Assert.Equal(MonsterPhase.Attack, m.Phase);                      // 仍在出招阶段
        Assert.Equal(0, m.CurrentCustomIndex);
        Assert.Equal(AvatarFixture.CustomAAni, m.CurrentAnimationPath);
        Assert.Equal(0, m.AttackIndex);
        Assert.Equal(1, m.SwingNumber);                                  // 自定义动作不算一次出招
        Assert.Equal(1, m.CustomActionsStarted);
        Assert.Equal((1, 0, 0), (arena.Session.CustomActionsStarted, arena.Session.CustomActionsFinished, arena.Session.CustomActionsRejected));
        Assert.Equal(0, arena.Session.PassivesSpawned);
        arena.Steps(1);
        Assert.Equal(1, arena.Session.PassivesSpawned);                  // A 的 [ON SET ACTION] 在它的第一步跑

        int length = arena.Until(() => m.Phase == MonsterPhase.Wait);
        Assert.InRange(length, 21, 25);                                  // A 共 240 毫秒
        Assert.Equal(1, m.CustomActionsFinished);
        Assert.Equal(-1, m.CurrentCustomIndex);
        Assert.Equal(StayAniPath, m.CurrentAnimationPath);
        Assert.Equal((1, 1, 0), (arena.Session.CustomActionsStarted, arena.Session.CustomActionsFinished, arena.Session.CustomActionsRejected));

        // 由出招起头的一串动作:结束按出招结束算,进出招间隔(1500 毫秒)
        arena.Steps(100);
        Assert.Equal(MonsterPhase.Wait, m.Phase);
        Assert.Equal(1, m.SwingNumber);
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.Equal(2, m.SwingNumber);
        Assert.Equal(StrikeAniPath, m.CurrentAnimationPath);
    }

    [Fact]
    public void ACustomActionStartedFromStandingIgnoresTheAttackDelayAndDoesNotRestartIt()
    {
        using var arena = new ActArena("f_custstay", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);         // 主角在视野里:怪物先自己出第 0 招(没有触发器)
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
        int toCustom = arena.Until(() => m.InCustomAction);
        Assert.InRange(toCustom, 8, 12);                                            // 站立动画第 1 帧(100 毫秒):出招间隔没过,切招会被拒,切自定义动作不拦
        Assert.Equal(1, m.SwingNumber);
        Assert.Equal((1, 0), (m.FamilySetActionApplied, m.FamilySetActionUnhosted));
        Assert.Equal(AvatarFixture.CustomAAni, m.CurrentAnimationPath);
        Assert.Equal(MonsterPhase.Attack, m.Phase);

        // 站立里切进来的自定义动作结束时不进出招间隔、也不动它:第二次出招仍从第一次出完招算 1500 毫秒(站立脚本每次回到待机都重新跑一遍)
        int rest = arena.Until(() => m.SwingNumber == 2, 400);
        Assert.True(rest > 0);
        Assert.InRange(toCustom + rest, 148, 185);
        Assert.True(m.CustomActionsStarted >= 3);                                   // 这 1.5 秒里站立脚本一遍遍把它切进自定义动作
        Assert.Equal(m.CustomActionsStarted - (m.InCustomAction ? 1 : 0), m.CustomActionsFinished);
    }

    [Fact]
    public void AChainOfCustomActionsStartedFromStandingStillLeavesTheAttackDelayAlone()
    {
        // 站立里切进自定义动作 0,它再切到 1:这一串不由出招起头,结束也不进出招间隔(整串结束一次)
        using var arena = new ActArena("f_custstaychain", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
        int toCustom = arena.Until(() => m.CurrentCustomIndex == 0);
        Assert.InRange(toCustom, 8, 12);
        int toSecond = arena.Until(() => m.CurrentCustomIndex == 1);
        Assert.True(toSecond > 0);
        int toFinish = arena.Until(() => m.CustomActionsFinished == 1);
        Assert.True(toFinish > 0);
        int rest = arena.Until(() => m.SwingNumber == 2, 400);
        Assert.True(rest > 0);                                                      // 间隔没被重新算:第二次出招仍在第一次出完招之后 1.5 秒上下
        Assert.InRange(toCustom + toSecond + toFinish + rest, 148, 195);
    }

    [Fact]
    public void ACustomActionStartedFromWalkingIsAcceptedToo()
    {
        using var arena = new ActArena("f_custmove", monsterX: 500, useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Walk) > 0);
        Assert.True(arena.Until(() => m.InCustomAction) > 0);                       // 行走动画第 1 帧
        Assert.Equal(AvatarFixture.CustomAAni, m.CurrentAnimationPath);
        Assert.Equal((1, 0), (m.FamilySetActionApplied, m.FamilySetActionUnhosted));
        Assert.Equal(0, m.SwingNumber);
        Assert.True(arena.Until(() => m.CustomActionsFinished == 1) > 0);
        Assert.NotEqual(MonsterPhase.Attack, m.Phase);                              // 回到待机或接着走,不是出招
    }

    [Fact]
    public void AnEndSwitchToACustomActionWaitsForTheAttackAnimationToFinish()
    {
        using var arena = new ActArena("f_custend", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        arena.Steps(20);                                                            // 200 毫秒:第 1 帧的脚本早已排好队,出招动画(320 毫秒)还没放完
        Assert.False(m.InCustomAction);
        Assert.Equal(StrikeAniPath, m.CurrentAnimationPath);
        Assert.Equal(0, m.CustomActionsStarted);
        int rest = arena.Until(() => m.InCustomAction);
        Assert.InRange(21 + rest, 31, 34);
        Assert.Equal(AvatarFixture.CustomAAni, m.CurrentAnimationPath);
        Assert.Equal(1, m.SwingNumber);
        Assert.Equal(1, m.CustomActionsStarted);
    }

    [Fact]
    public void ASwitchToACustomActionThatDoesNotExistIsRejectedAndTheCurrentActionCarriesOn()
    {
        // 列表是 A、空位、B:序号 3 与 7 越界、序号 1 是空位,三次都被拒、出招照放,第四次切 2(B)才进去
        using var arena = new ActArena("f_custbad", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.Equal(3, m.CustomActionCount);
        Assert.True(m.CustomExists(0));
        Assert.False(m.CustomExists(1));
        Assert.True(m.CustomExists(2));
        Assert.Null(m.CustomAni(1));
        Assert.Null(m.CustomAni(7));
        Assert.Null(m.CustomTriggerState(1, 0));
        Assert.True(arena.Until(() => m.CustomActionsRejected > 0) > 0);
        Assert.Equal(2, m.CustomActionsRejected);                                   // 第 1 帧:序号 3(正好等于个数)与 7(远远越界)
        Assert.False(m.InCustomAction);
        Assert.Equal(StrikeAniPath, m.CurrentAnimationPath);
        Assert.True(arena.Until(() => m.CustomActionsRejected == 3) > 0);           // 第 2 帧:序号 1 是空位
        Assert.False(m.InCustomAction);
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.True(arena.Until(() => m.InCustomAction) > 0);                       // 第 3 帧:序号 2 存在
        Assert.Equal(2, m.CurrentCustomIndex);
        Assert.Equal(AvatarFixture.CustomBAni, m.CurrentAnimationPath);
        Assert.Equal((1, 3), (m.CustomActionsStarted, m.CustomActionsRejected));
        Assert.Equal(3, arena.Session.CustomActionsRejected);

        // 怪物根本没写 [etc action]:切不进去,出招照放
        using var none = new ActArena("f_custnone", useVitals: false);
        MonsterActor n = none.Monster;
        Assert.False(n.HasCustomActions);
        Assert.Equal(0, n.CustomActionCount);
        Assert.Equal(0, none.Session.CustomActMonsters);
        Assert.True(none.Until(() => n.CustomActionsRejected == 1) > 0);
        Assert.Equal(MonsterPhase.Attack, n.Phase);
        Assert.Equal(StrikeAniPath, n.CurrentAnimationPath);
        Assert.True(none.Until(() => n.Phase == MonsterPhase.Wait) > 0);
        Assert.Equal((0, 0), (n.CustomActionsStarted, n.CustomActionsFinished));
    }

    [Fact]
    public void ACustomActionCanChainIntoAnotherOneOrLeaveForAnAttack()
    {
        using var chain = new ActArena("f_custchain", useVitals: false);
        MonsterActor c = chain.Monster;
        Assert.True(chain.Until(() => c.CurrentCustomIndex == 0) > 0);
        Assert.Equal(AvatarFixture.CustomAAni, c.CurrentAnimationPath);
        Assert.True(chain.Until(() => c.CurrentCustomIndex == 1) > 0);              // 自定义动作 0 的第 1 帧切到 1
        Assert.Equal(AvatarFixture.CustomBAni, c.CurrentAnimationPath);
        Assert.Equal(2, c.CustomActionsStarted);
        Assert.Equal(0, c.CustomActionsFinished);                                   // 换到另一个自定义动作不算放完
        Assert.Equal(1, c.SwingNumber);
        chain.Steps(1);
        Assert.Equal(1, chain.Session.PassivesSpawned);                             // 0 没有触发器;1(B)出场放一个
        int length = chain.Until(() => c.Phase == MonsterPhase.Wait);
        Assert.InRange(length, 19, 22);                                             // B 共 200 毫秒
        Assert.Equal(2, c.CustomActionsStarted);
        Assert.Equal(1, c.CustomActionsFinished);                                   // 整串只在最后结束一次

        // 自定义动作里切回出招:算一次新的出招,出招动画从头放
        using var leave = new ActArena("f_custatk", useVitals: false);
        MonsterActor l = leave.Monster;
        Assert.True(leave.Until(() => l.CurrentCustomIndex == 0) > 0);
        Assert.Equal(0, l.AttackIndex);
        Assert.True(leave.Until(() => !l.InCustomAction) > 0);
        Assert.Equal(MonsterPhase.Attack, l.Phase);
        Assert.Equal(1, l.AttackIndex);
        Assert.Equal(2, l.SwingNumber);
        Assert.Equal(StrikeAniPath, l.CurrentAnimationPath);
        Assert.Equal((1, 0), (l.CustomActionsStarted, l.CustomActionsFinished));
        Assert.True(leave.Until(() => l.Phase == MonsterPhase.Wait) > 0);
        Assert.Equal(0, l.CustomActionsFinished);                                   // 结束的是出招,不是自定义动作
    }

    [Fact]
    public void ALoopingCustomActionOnlyEndsWhenTheScriptOrAHitEndsIt()
    {
        using var forever = new ActArena("f_custloop", useVitals: false);
        MonsterActor f = forever.Monster;
        Assert.True(forever.Until(() => f.InCustomAction) > 0);
        Assert.Equal(AvatarFixture.CustomLoopAni, f.CurrentAnimationPath);
        forever.Steps(300);                                                         // 3 秒:循环动画自己不会结束
        Assert.True(f.InCustomAction);
        Assert.Equal(0, f.CustomActionsFinished);
        Assert.False(f.ApplyHit(1, false, 0, false));                               // 挨打是另一条出路
        Assert.Equal(MonsterPhase.Hurt, f.Phase);
        Assert.False(f.InCustomAction);
        Assert.Equal(0, f.CustomActionsFinished);                                   // 被打断的不算放完

        // 脚本 [SET ACTION] [STAND] 结束它
        using var exit = new ActArena("f_custloopexit", useVitals: false);
        MonsterActor e = exit.Monster;
        Assert.True(exit.Until(() => e.InCustomAction) > 0);
        int leave = exit.Until(() => e.Phase == MonsterPhase.Wait);
        Assert.InRange(leave, 8, 12);                                               // 循环动画第 1 帧(100 毫秒)
        Assert.Equal(1, e.CustomActionsFinished);

        // 切招带 [END]:没有排队时循环动画不自己结束;排了队就放满当前一遍(200 毫秒)再接
        using var queued = new ActArena("f_custloopend", useVitals: false);
        MonsterActor q = queued.Monster;
        Assert.True(queued.Until(() => q.InCustomAction) > 0);
        int pass = queued.Until(() => !q.InCustomAction);
        Assert.InRange(pass, 18, 22);
        Assert.Equal(MonsterPhase.Attack, q.Phase);
        Assert.Equal(1, q.AttackIndex);
        Assert.Equal(0, q.CustomActionsFinished);
    }

    [Fact]
    public void ACustomActionWithoutTriggersOrWithoutAnAnimationStillStartsAndEnds()
    {
        using var plain = new ActArena("f_custplain", useVitals: false);
        MonsterActor p = plain.Monster;
        Assert.True(plain.Until(() => p.InCustomAction) > 0);
        Assert.Equal(AvatarFixture.CustomAAni, p.CurrentAnimationPath);
        int length = plain.Until(() => p.Phase == MonsterPhase.Wait);
        Assert.InRange(length, 22, 26);
        Assert.Equal(0, plain.Session.PassivesSpawned);                             // 没有触发器:只放动画
        Assert.Equal(1, p.CustomActionsFinished);

        // 动画读不到:只带程序;进来之后跑完第一步(触发器走一次)就结束
        using var bare = new ActArena("f_custnoani", useVitals: false);
        MonsterActor b = bare.Monster;
        Assert.True(b.CustomExists(0));
        Assert.Null(b.CustomAni(0));
        Assert.True(bare.Until(() => b.InCustomAction) > 0);
        Assert.True(bare.Until(() => b.CustomActionsFinished == 1) > 0);
        Assert.Equal(1, bare.Session.PassivesSpawned);
        Assert.Equal(MonsterPhase.Wait, b.Phase);
    }

    [Fact]
    public void TheAttackBoxesOfACustomActionHurtTheHeroOncePerEntry()
    {
        using var arena = new ActArena("f_custstrike");
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.InCustomAction) > 0);
        Assert.Equal(AvatarFixture.CustomStrikeAni, m.CurrentAnimationPath);
        Assert.Equal(1000, arena.Session.Vitals.Hp);                                // 出招动画的攻击框(160 毫秒)还没到就被切走了
        Assert.False(m.HitHeroThisSwing);
        int toHit = arena.Until(() => m.HitHeroThisSwing);
        Assert.InRange(toHit, 14, 20);                                              // 自定义动作动画第 2 帧(160 毫秒)带攻击框
        Assert.True(arena.Session.Vitals.Hp < 1000);
        Assert.Equal(1, m.SwingNumber);
    }

    [Fact]
    public void TheSwitchBoardOfACustomActionSurvivesLeavingAndEnteringAgain()
    {
        using var arena = new ActArena("f_custonce", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.Equal(true, m.CustomTriggerState(0, 0));
        Assert.Null(m.CustomTriggerState(0, 1));                                    // 没有这个触发器
        Assert.Null(m.CustomTriggerState(1, 0));                                    // 没有这个自定义动作
        Assert.True(arena.Until(() => m.CustomActionsFinished == 1) > 0);
        Assert.Equal(1, arena.Session.PassivesSpawned);
        Assert.Equal(false, m.CustomTriggerState(0, 0));                            // 自己把触发器 0 关了,出了这个动作也留着
        Assert.Equal((1, 0), (m.ActSwitchApplied, m.ActSwitchMissed));
        Assert.True(arena.Until(() => m.CustomActionsFinished == 2, 600) > 0);      // 出招间隔(1500 毫秒)之后第二次出招、再切进自定义动作
        Assert.Equal(2, m.CustomActionsStarted);
        Assert.Equal(1, arena.Session.PassivesSpawned);                             // 触发器关着:这一次没有放
        Assert.Equal(false, m.CustomTriggerState(0, 0));
    }

    [Fact]
    public void AScriptCanSwitchTheBoardsOfCustomActionsFromAnotherActionAndMissesAreCounted()
    {
        // 自定义动作 1(CustGate)的触发器 0 出生时关着;出招脚本先把它打开、再往不存在的 9 写(落空)、然后切进去
        using var open = new ActArena("f_custsw", useVitals: false);
        MonsterActor m = open.Monster;
        Assert.Equal(false, m.CustomTriggerState(1, 0));
        Assert.True(open.Until(() => m.CurrentCustomIndex == 1) > 0);
        Assert.Equal(true, m.CustomTriggerState(1, 0));
        Assert.Equal((1, 1, 0), (m.ActSwitchApplied, m.ActSwitchMissed, m.ActSwitchUnhosted));
        Assert.Equal((1, 1), (open.Session.ActSwitchApplied, open.Session.ActSwitchMissed));
        open.Steps(1);
        Assert.Equal(1, open.Session.PassivesSpawned);                              // 打开了,出场放一个

        // 对照:不打开直接切进去,触发器 0 关着,什么也不放
        using var closed = new ActArena("f_custgate", useVitals: false);
        MonsterActor c = closed.Monster;
        Assert.True(closed.Until(() => c.CustomActionsFinished == 1) > 0);
        Assert.Equal(0, closed.Session.PassivesSpawned);
        Assert.Equal(false, c.CustomTriggerState(0, 0));
    }

    [Fact]
    public void AScriptCanReadTheBoardsOfCustomActions()
    {
        using var arena = new ActArena("f_custread", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.Equal(false, m.CustomTriggerState(0, 0));
        Assert.True(arena.Until(() => m.ActSpeedX != 0) > 0);                       // 第 1 帧:读到"关着"的那条放了 X 速度
        Assert.Equal(-100.0, m.ActSpeedX);
        Assert.Equal(0.0, m.ActSpeedY);                                             // 读"开着"的那条没触发
        Assert.Equal(0.0, m.ActSpeedZ);                                             // 自定义动作 9 不存在:读不到,不触发
        Assert.True(arena.Until(() => m.CustomTriggerState(0, 0) == true) > 0);     // 第 2 帧写开了
        Assert.True(arena.Until(() => m.ActSpeedY != 0) > 0);                       // 第 3 帧再读:开着
        Assert.Equal(51.0, m.ActSpeedY);
        Assert.False(m.InCustomAction);                                             // 只是读写开关板,没有切进去
    }

    [Fact]
    public void ACustomActionIsNotSpedUpByTheAttackSpeed()
    {
        // 攻击速度 2000(出招动画两倍速):出招第 1 帧提前到 40 毫秒,自定义动作还是放它自己的 240 毫秒
        using var arena = new ActArena("f_custnow", useVitals: false, attackSpeed: 2000);
        MonsterActor m = arena.Monster;
        int toCustom = arena.Until(() => m.InCustomAction);
        Assert.InRange(toCustom, 3, 6);
        int length = arena.Until(() => m.Phase == MonsterPhase.Wait);
        Assert.InRange(length, 22, 26);

        // 自定义动作里的 [FRAME] 同样不吃出招速率:第 0 个的第 1 帧(80 毫秒)才切到第 1 个
        using var chain = new ActArena("f_custchain", useVitals: false, attackSpeed: 2000);
        MonsterActor c = chain.Monster;
        Assert.True(chain.Until(() => c.CurrentCustomIndex == 0) > 0);
        int hop = chain.Until(() => c.CurrentCustomIndex == 1);
        Assert.InRange(hop, 7, 10);
    }

    [Fact]
    public void EnteringACustomActionStartsANewHitWindowSoTheAttackBoxesOfBothPartsCanLand()
    {
        // 出招自己的攻击框(第 2 帧)先打一下;出招放完([END])接自定义动作,它的攻击框(第 2 帧)再打一下:每一段各算一次
        using var arena = new ActArena("f_custend2", useVitals: true, hp: 1000000, frozenHero: true);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.HitHeroThisSwing) > 0);
        Assert.False(m.InCustomAction);
        Assert.True(arena.Until(() => m.InCustomAction) > 0);
        Assert.False(m.HitHeroThisSwing);                                           // 新的一段:判定从头算
        Assert.True(arena.Until(() => m.HitHeroThisSwing) > 0);
        Assert.Equal(AvatarFixture.CustomStrikeAni, m.CurrentAnimationPath);
        Assert.Equal(1, m.SwingNumber);
    }

    [Fact]
    public void ChangingTheAttackFileFromACustomActionAppliesAtOnceBecauseNoAttackIsRunning()
    {
        // 自定义动作里换第 0 招的文件:此刻在跑的是自定义动作,不是那一招,当场换上(记成待换是出招正在跑的待遇)
        using var arena = new ActArena("f_custswap", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.Equal(StrikeAniPath, m.AnimationPathOf(m.AttackAni(0)));
        Assert.True(arena.Until(() => m.ActionFilesApplied > 0) > 0);
        Assert.True(m.InCustomAction);
        Assert.Equal((1, 0), (m.ActionFilesApplied, m.ActionFilesDeferred));
        Assert.False(m.HasPendingActionFile);
        Assert.Equal(AvatarFixture.SwapStrikeAni, m.AnimationPathOf(m.AttackAni(0)));
        Assert.Equal(AvatarFixture.CustomAAni, m.CurrentAnimationPath);             // 正在放的自定义动作照旧

        // 出招里换自己再切进自定义动作:那一招的新文件等下一次真正进入出招才换上,进自定义动作不算
        using var own = new ActArena("f_custswap2", useVitals: false);
        MonsterActor o = own.Monster;
        Assert.True(own.Until(() => o.InCustomAction) > 0);
        Assert.Equal((0, 1), (o.ActionFilesApplied, o.ActionFilesDeferred));
        Assert.True(o.HasPendingActionFile);
        Assert.Equal(StrikeAniPath, o.AnimationPathOf(o.AttackAni(0)));
        Assert.True(own.Until(() => o.Phase == MonsterPhase.Wait) > 0);
        Assert.True(own.Until(() => o.Phase == MonsterPhase.Attack) > 0);           // 出招间隔之后的下一击:进入这一招时换上
        Assert.Equal(1, o.ActionFilesApplied);
        Assert.False(o.HasPendingActionFile);
        Assert.Equal(AvatarFixture.SwapStrikeAni, o.CurrentAnimationPath);
    }

    [Fact]
    public void WithoutAnActLibraryOrAfterDetachingThereAreNoCustomActions()
    {
        using var detached = new ActArena("f_custnow", useVitals: false, attach: false);
        Assert.False(detached.Monster.HasCustomActions);
        Assert.Equal(0, detached.Session.CustomActMonsters);
        detached.Steps(60);
        Assert.Equal(0, detached.Monster.CustomActionsStarted);

        using var arena = new ActArena("f_custnow", useVitals: false);
        Assert.True(arena.Monster.HasCustomActions);
        arena.Session.AttachActs(null);
        Assert.False(arena.Monster.HasCustomActions);
        Assert.Equal(0, arena.Monster.CustomActionCount);
        Assert.False(arena.Monster.CustomExists(0));

        // 没写 [etc action] 的怪物也一样没有
        using var plain = new ActArena("f_plain", useVitals: false);
        Assert.False(plain.Monster.HasCustomActions);
    }
}
