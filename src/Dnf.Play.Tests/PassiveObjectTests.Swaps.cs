using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 怪物脚本的 [CHANGE ACTION FILE](M57,证据 E-151):运行中把某一族动作(待机、行走、受击、倒地)或某一招的 .act 换成另一个文件,
/// 连带换动画与触发器程序。客户端的做法(静态读码):被换的动作此刻正在跑就让它跑完,新文件等下一次进入这一族才轮到;没在跑的马上换。
/// 怪物与 .act 全是合成的(f_swap 开头的几只,见 <see cref="AvatarFixture"/> 的 FamilyMobs 与 ActScripts 里"M57"一段):换进来的文件叫 B 系列,
/// 动画路径与原来的不同(<see cref="AvatarFixture.SwapStayAni"/> 等),从 CurrentAnimationPath 认得出;放出来的 ActForeverId 对象不会消失,会话里的被动对象数就是触发次数。
/// 主角站在 (300, 100),怪物站在 (340, 100) 朝左;出招后的冷却是 1500 毫秒,冷却里怪物站着。
/// </summary>
public partial class PassiveObjectTests
{
    private const string StayAniPath = "monster/blob/animation_blob/stay.ani";
    private const string StrikeAniPath = "monster/blob/animation_blob/strike.ani";

    [Fact]
    public void AnAttackScriptThatSwapsWholeFamiliesChangesThemAtOnceBecauseNoneOfThemIsRunning()
    {
        using var arena = new ActArena("f_swapfam", useVitals: false);
        MonsterActor m = arena.Monster;
        AniFile modelStay = m.Model.Waiting;
        Assert.False(m.HasFamilyActs);                                              // 原来各族的 .act 只有动画
        Assert.Equal(StayAniPath, m.AnimationPathOf(m.FamilyAni(MonsterActFamily.Stay)));

        Assert.True(arena.Until(() => m.ActionFilesApplied > 0) > 0);               // 出招一开始([ON SET ACTION])就一口气换五个
        arena.Steps(1);
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.Equal(5, m.ActionFilesApplied);
        Assert.Equal(0, m.ActionFilesDeferred);
        Assert.Equal(5, arena.Session.ActionFilesApplied);
        Assert.False(m.HasPendingActionFile);
        Assert.Equal(AvatarFixture.SwapStayAni, m.AnimationPathOf(m.FamilyAni(MonsterActFamily.Stay)));
        Assert.Equal(AvatarFixture.SwapMoveAni, m.AnimationPathOf(m.FamilyAni(MonsterActFamily.Move)));
        Assert.Equal(AvatarFixture.SwapDamage1Ani, m.AnimationPathOf(m.FamilyAni(MonsterActFamily.Damage1)));
        Assert.Equal(AvatarFixture.SwapDamage2Ani, m.AnimationPathOf(m.FamilyAni(MonsterActFamily.Damage2)));
        Assert.Equal(AvatarFixture.SwapDownAni, m.AnimationPathOf(m.FamilyAni(MonsterActFamily.Down)));
        Assert.True(m.HasFamilyActs);                                               // 待机与受击一号的新文件带触发器:程序表现建出来
        Assert.Same(modelStay, m.Model.Waiting);                                    // 换的只是这一只自己的一份,同种怪物共用的模型不动
        Assert.NotSame(modelStay, m.FamilyAni(MonsterActFamily.Stay));
        Assert.Equal(StrikeAniPath, m.CurrentAnimationPath);                        // 正在放的出招照旧

        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
        Assert.Equal(AvatarFixture.SwapStayAni, m.CurrentAnimationPath);            // 出完招回到待机:放的是新待机
        Assert.Equal(MonsterActFamily.Stay, m.ActiveFamily);
        Assert.Equal(0, arena.Session.PassivesSpawned);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);      // 新待机文件的触发器在跑
        Assert.Equal(1, arena.Session.PassivesSpawned);

        int spawned = arena.Session.PassivesSpawned;
        m.ApplyHit(1, false, 0, false);                                             // 第一次受击放受击动作一,第二次放二
        Assert.Equal(AvatarFixture.SwapDamage1Ani, m.CurrentAnimationPath);
        arena.Steps(2);
        Assert.Equal(spawned + 1, arena.Session.PassivesSpawned);                   // 受击一号的新文件的 [ON SET ACTION]
        m.ApplyHit(1, false, 0, false);
        Assert.Equal(AvatarFixture.SwapDamage2Ani, m.CurrentAnimationPath);
        m.ApplyHit(1, true, 0, false);                                              // 倒地攻击(占位路径,放倒地动作)
        Assert.Equal(AvatarFixture.SwapDownAni, m.CurrentAnimationPath);
    }

    [Fact]
    public void AFamilyThatIsRunningKeepsItsOldFileAndTheNewOneTakesOverOnTheNextEntryIntoIt()
    {
        using var arena = new ActArena("f_swapstay", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);           // 先出一招(这一招不换东西),再站着
        string original = m.CurrentAnimationPath;
        Assert.Equal(StayAniPath, original);

        Assert.True(arena.Until(() => m.ActionFilesDeferred > 0) > 0);              // 站立动画第 1 帧:待机脚本把待机换成 B
        Assert.Equal(1, m.ActionFilesDeferred);
        Assert.Equal(0, m.ActionFilesApplied);
        Assert.True(m.HasPendingActionFile);
        Assert.Equal(1, arena.Session.ActionFilesDeferred);
        Assert.Equal(original, m.CurrentAnimationPath);                             // 正在放的待机不换
        Assert.Equal(original, m.AnimationPathOf(m.FamilyAni(MonsterActFamily.Stay)));
        arena.Steps(50);
        Assert.Equal(MonsterPhase.Wait, m.Phase);
        Assert.Equal(original, m.CurrentAnimationPath);                             // 一直站着,还是旧的
        Assert.Equal(0, arena.Session.PassivesSpawned);                             // 新文件的脚本还没轮到

        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);         // 冷却完出招
        Assert.True(m.HasPendingActionFile);                                        // 进入出招不算进入待机
        Assert.Equal(0, m.ActionFilesApplied);
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);           // 出完招回待机:这一次进入才换上
        Assert.False(m.HasPendingActionFile);
        Assert.Equal(1, m.ActionFilesApplied);
        Assert.Equal(AvatarFixture.SwapStayAni, m.CurrentAnimationPath);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);      // 新待机文件的触发器接着跑,旧的那一条(换文件)不再有
        arena.Steps(100);
        Assert.Equal(1, arena.Session.PassivesSpawned);
        Assert.Equal(1, m.ActionFilesDeferred);
    }

    [Fact]
    public void AnExplicitSetActionToTheSameFamilyEntersItAgainSoTheNewFileTakesOverAtOnce()
    {
        using var arena = new ActArena("f_swapstayset", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
        Assert.Equal(StayAniPath, m.CurrentAnimationPath);

        long swings = m.SwingNumber;
        Assert.True(arena.Until(() => m.ActionFilesApplied > 0, 30) > 0);           // 同一个行为组:先换(待机在跑,记成待换),再 [SET ACTION] [STAND]:当场换上,不等下一次出招
        Assert.Equal(swings, m.SwingNumber);
        Assert.Equal(1, m.ActionFilesDeferred);
        Assert.Equal(1, m.ActionFilesApplied);
        Assert.False(m.HasPendingActionFile);
        Assert.Equal(MonsterPhase.Wait, m.Phase);
        Assert.Equal(AvatarFixture.SwapStayAni, m.CurrentAnimationPath);
        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);      // 重新进入待机:换上的新程序从头跑
        Assert.Equal(1, arena.Session.PassivesSpawned);
    }

    [Fact]
    public void AnExplicitSetActionToWalkAgainEntersTheWalkSoTheNewMoveFileTakesOverAtOnce()
    {
        using var arena = new ActArena("f_swapmoveset", monsterX: 500, useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Walk) > 0);
        Assert.Equal(MonsterActFamily.Move, m.ActiveFamily);

        Assert.True(arena.Until(() => m.ActionFilesApplied > 0, 100) > 0);          // 同一个行为组:先换(行走在跑,记成待换),再 [SET ACTION] [MOVE]
        Assert.Equal(1, m.ActionFilesDeferred);
        Assert.False(m.HasPendingActionFile);
        Assert.Equal(MonsterPhase.Walk, m.Phase);
        Assert.Equal(AvatarFixture.SwapMoveAni, m.CurrentAnimationPath);
        arena.Steps(2);
        Assert.Null(m.FamilyRunner(MonsterActFamily.Move));                         // 新行走文件没有触发器:旧的运行器撤掉,不再重新开始
    }

    [Fact]
    public void ARunningAttackKeepsItsFileTheNextSwingUsesTheNewOneAndItsReachDecidesWhetherThereIsAThirdSwing()
    {
        // 出招靠"攻击框够不够得着主角"决定,这要主角有受击框(有血量);主角钉在原地,每一击都打在同一处
        using var arena = new ActArena("f_swapself", useVitals: true, hp: 1000000, frozenHero: true);
        MonsterActor m = arena.Monster;
        Assert.Equal(StrikeAniPath, m.AnimationPathOf(m.AttackAni(0)));

        Assert.True(arena.Until(() => m.ActionFilesDeferred > 0) > 0);              // 第一击一开始就把自己这一招换了:正在跑,记成待换
        Assert.Equal(MonsterPhase.Attack, m.Phase);
        Assert.Equal(0, m.ActionFilesApplied);
        Assert.True(m.HasPendingActionFile);
        Assert.Equal(StrikeAniPath, m.CurrentAnimationPath);
        Assert.Equal(0, arena.Session.PassivesSpawned);                             // 新文件的脚本还没跑
        long first = m.SwingNumber;

        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
        Assert.Equal(StrikeAniPath, m.AnimationPathOf(m.AttackAni(0)));             // 出完了,下一次出招之前还是旧的
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);         // 第二击:进入这一招的时刻换上
        Assert.Equal(first + 1, m.SwingNumber);
        Assert.Equal(1, m.ActionFilesApplied);
        Assert.False(m.HasPendingActionFile);
        Assert.Equal(AvatarFixture.SwapStrikeAni, m.CurrentAnimationPath);
        arena.Steps(2);
        Assert.Equal(1, arena.Session.PassivesSpawned);                             // 新文件的 [ON SET ACTION]

        // 新动画的攻击框在反面很远处,够不着主角:不再出招。旧动画的攻击框缓存不作废的话还会出第三击
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
        arena.Steps(500);
        Assert.Equal(first + 1, m.SwingNumber);
        Assert.Equal(1, m.ActionFilesDeferred);
    }

    [Fact]
    public void AnAttackSlotThatIsNotRunningChangesAtOnceAndTheSlotRunsTheNewProgramWhenItIsEntered()
    {
        using var arena = new ActArena("f_swapslot", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(m.HasActs);
        Assert.Equal(StrikeAniPath, m.AnimationPathOf(m.AttackAni(1)));
        Assert.Equal(2, m.AttackCount);

        Assert.True(arena.Until(() => m.ActionFilesApplied > 0) > 0);               // 第 0 招换第 1 招,再切过去
        Assert.Equal(0, m.ActionFilesDeferred);
        Assert.Equal(1, m.AttackIndex);
        Assert.Equal(AvatarFixture.SwapStrikeAni, m.AnimationPathOf(m.AttackAni(1)));
        Assert.Equal(AvatarFixture.SwapStrikeAni, m.CurrentAnimationPath);
        arena.Steps(2);
        Assert.Equal(1, arena.Session.PassivesSpawned);                             // 第 1 招原来没有程序,现在跑的是换进来的
    }

    [Fact]
    public void AFamilyScriptCanGiveAMonsterItsFirstAttackProgram()
    {
        using var arena = new ActArena("f_swapfromstay", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.False(m.HasActs);                                                    // 出招 .act 没有触发器:原来一招程序都没有
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
        Assert.True(arena.Until(() => m.ActionFilesApplied > 0) > 0);               // 待机第 1 帧:第 0 招没在跑,当场换上
        Assert.Equal(0, m.ActionFilesDeferred);
        Assert.True(m.HasActs);
        Assert.Equal(AvatarFixture.SwapStrikeAni, m.AnimationPathOf(m.AttackAni(0)));
        Assert.Equal(0, arena.Session.PassivesSpawned);

        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);         // 下一次出招跑的是换进来的程序
        arena.Steps(2);
        Assert.Equal(1, arena.Session.PassivesSpawned);
    }

    [Fact]
    public void RecordsThatCannotBeAppliedAreCountedAndLeaveTheMonsterAlone()
    {
        using var arena = new ActArena("f_swapbad", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.ActionFilesRejected > 0) > 0);
        arena.Steps(3);
        Assert.Equal(2, m.ActionFilesRejected);                                     // 受击序号 2(要小于 2)、出招序号 5(要小于列表个数 1)
        Assert.Equal(1, m.ActionFilesUnhosted);                                     // 冲刺族:这一版怪物没有对应的动作
        Assert.Equal(1, arena.Session.ActionFilesMissing);                          // 文件不存在
        Assert.Equal(0, m.ActionFilesApplied);
        Assert.Equal(0, m.ActionFilesDeferred);
        Assert.False(m.HasPendingActionFile);
        Assert.Equal(StayAniPath, m.AnimationPathOf(m.FamilyAni(MonsterActFamily.Stay)));
        Assert.Equal(2, arena.Session.ActionFilesRejected);
        Assert.Equal(1, arena.Session.ActionFilesUnhosted);

        PassiveAction stay = arena.Session.Acts.SwapAction(AvatarFixture.BlobActionDir + "/stayb.act");
        Assert.False(m.ApplyActionFile(ActFileSlot.Stay, 0, null, null));          // 没有动作:拒绝
        Assert.Equal(3, m.ActionFilesRejected);
        Assert.False(m.ApplyActionFile(ActFileSlot.Custom, 0, stay, null));        // 自定义列表、后退、跳跃、跳击也只记数
        Assert.False(m.ApplyActionFile(ActFileSlot.MoveBack, 0, stay, null));
        Assert.False(m.ApplyActionFile(ActFileSlot.Jump, 0, stay, null));
        Assert.False(m.ApplyActionFile(ActFileSlot.JumpAttack, 0, stay, null));
        Assert.False(m.ApplyActionFile(ActFileSlot.DashAttack, 0, stay, null));
        Assert.Equal(6, m.ActionFilesUnhosted);
        Assert.Equal(StayAniPath, m.AnimationPathOf(m.FamilyAni(MonsterActFamily.Stay)));
    }

    [Fact]
    public void ThePathIsReadRelativeToTheDirectoryOfTheScriptThatWritesIt()
    {
        using var arena = new ActArena("f_swapsub", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.ActionFilesApplied > 0) > 0);               // 路径是 sub/StayC.act,相对 monster/blob/action/
        Assert.Equal(0, arena.Session.ActionFilesMissing);
        Assert.Equal(AvatarFixture.SwapStayAni, m.AnimationPathOf(m.FamilyAni(MonsterActFamily.Stay)));   // 它自己的 [BASE ANI] 又相对 sub 目录
    }

    [Fact]
    public void ALaterSwapOfARunningFamilyReplacesTheOneWaitingAndSwapsDoNotChangeOtherMonsters()
    {
        using var arena = new ActArena("f_plain", useVitals: false);
        using var other = new ActArena("f_plain", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
        PassiveAction b = arena.Session.Acts.SwapAction(AvatarFixture.BlobActionDir + "/stayb.act");
        PassiveAction c = arena.Session.Acts.SwapAction(AvatarFixture.BlobActionDir + "/moveb.act");     // 换成一个路径不同的动画,看得出换上的是哪一个
        Assert.True(m.ApplyActionFile(ActFileSlot.Stay, 0, b, null));               // 待机正在跑:记成待换
        Assert.True(m.ApplyActionFile(ActFileSlot.Stay, 0, c, null));               // 再换一次:后写的顶掉先记的
        Assert.Equal(2, m.ActionFilesDeferred);
        Assert.Equal(0, m.ActionFilesApplied);
        Assert.Equal(StayAniPath, m.CurrentAnimationPath);

        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
        Assert.Equal(1, m.ActionFilesApplied);                                      // 只换上一个
        Assert.False(m.HasPendingActionFile);
        Assert.Equal(AvatarFixture.SwapMoveAni, m.CurrentAnimationPath);
        Assert.Equal(StayAniPath, other.Monster.AnimationPathOf(other.Monster.FamilyAni(MonsterActFamily.Stay)));
        Assert.Equal(0, other.Monster.ActionFilesApplied);
    }

    [Fact]
    public void ASwappedDownFileGivesTheNextKnockdownItsOwnShape()
    {
        // 倒地 B 躺着那一帧停 1500 毫秒(原来 500):换过的怪物躺得更久。倒地状态机的形状按动画推出来,所以要在下一次倒地时重建
        int Lying(string mob, bool swap)
        {
            using var arena = new ActArena(mob, useVitals: false);
            MonsterActor m = arena.Monster;
            if (swap)
            {
                Assert.True(arena.Until(() => m.ActionFilesApplied >= 5) > 0);
            }
            Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
            Assert.False(m.ApplyHit(1, true, 212, false, default, new KnockdownInput(215, false, false, false, 0, 0)));
            Assert.True(m.IsKnockedDown);
            int steps = arena.Until(() => !m.IsKnockedDown, 1500);
            Assert.True(steps > 0);
            return steps;
        }

        int original = Lying("f_plain", swap: false);
        int swapped = Lying("f_swapfam", swap: true);
        Assert.InRange(swapped - original, 90, 110);                                // 多躺 1000 毫秒
    }

    [Fact]
    public void ASwapDuringAKnockdownTakesEffectForTheNextOne()
    {
        using var arena = new ActArena("f_plain", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
        Assert.False(m.ApplyHit(1, true, 212, false, default, new KnockdownInput(215, false, false, false, 0, 0)));
        Assert.True(m.IsKnockedDown);
        PassiveAction down = arena.Session.Acts.SwapAction(AvatarFixture.BlobActionDir + "/downb.act");
        Assert.True(m.ApplyActionFile(ActFileSlot.Down, 0, down, null));            // 倒地正在放:记成待换(被打时才重新进入)
        Assert.Equal(1, m.ActionFilesDeferred);
        int steps = arena.Until(() => !m.IsKnockedDown, 1500);
        Assert.InRange(steps, 1, 400);                                              // 这一次按旧的躺 500 毫秒
        Assert.True(m.HasPendingActionFile);

        Assert.False(m.ApplyHit(1, true, 212, false, default, new KnockdownInput(215, false, false, false, 0, 0)));   // 再挨一下:重新进入倒地,换上
        Assert.False(m.HasPendingActionFile);
        Assert.Equal(1, m.ActionFilesApplied);
        Assert.Equal(AvatarFixture.SwapDownAni, m.CurrentAnimationPath);
        int again = arena.Until(() => !m.IsKnockedDown, 1500);
        Assert.True(again > steps + 80);                                            // 这一次躺 1500 毫秒
    }

    [Fact]
    public void ASwapDuringAKnockdownDoesNotRestartTheFlightThatIsInTheAir()
    {
        // 倒地中再挨一下是"再次开始倒地":上一次的类型、阶段接着算(弹起类的第二下把动画时钟跳到长停留帧)。换倒地文件挂了待换,这一下才换上,
        // 但正在飞的状态机不能被换新的重置
        (bool Facing, int Stage, int Frame) Second(bool swap)
        {
            using var arena = new ActArena("f_plain", useVitals: false);
            MonsterActor m = arena.Monster;
            Assert.True(arena.Until(() => m.Phase == MonsterPhase.Wait) > 0);
            var bounce = new KnockdownInput(215, true, false, false, 0, 0);
            m.ApplyHit(1, true, 212, false, default, bounce);
            Assert.True(m.IsKnockedDown);                                             // 刚被砸到地面上(弹起类先砸地),这一刻再挨一下才算接续飞行
            if (swap)
            {
                PassiveAction down = arena.Session.Acts.SwapAction(AvatarFixture.BlobActionDir + "/downb.act");
                Assert.True(m.ApplyActionFile(ActFileSlot.Down, 0, down, null));
                Assert.Equal(1, m.ActionFilesDeferred);
            }
            m.ApplyHit(1, true, 212, true, default, bounce);                          // 攻击方换到另一侧再打一下
            Assert.True(m.IsKnockedDown);
            Assert.Equal(swap ? 1 : 0, m.ActionFilesApplied);
            return (m.FacingLeft, m.DownStage, m.CurrentFrame);
        }

        (bool Facing, int Stage, int Frame) control = Second(swap: false);
        Assert.True(control.Facing);                                                  // 接续飞行不转向攻击方
        Assert.Equal(1, control.Frame);                                               // 动画时钟跳到了长停留帧
        Assert.Equal(control, Second(swap: true));
    }

    [Fact]
    public void PreloadingAProgramAlsoReadsTheFilesItWillSwapInAndTheyAreCached()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var library = new ActLibrary(assets, DataOf(assets));
        ActProgram swapper = library.Program(AvatarFixture.BlobActionDir + "/actswapfam.act");
        Assert.NotNull(swapper);
        Assert.Equal(1, library.ProgramCount);
        library.Preload(swapper);
        Assert.Equal(7, library.ProgramCount);                                      // 加上五个换进来的文件,以及它们放出来的那个被动对象的动作脚本
        Assert.Equal(1, library.TemplateCount);                                     // 被动对象也顺着换进来的脚本预读了

        PassiveAction stay = library.SwapAction(AvatarFixture.BlobActionDir + "/stayb.act");
        Assert.NotNull(stay);
        Assert.Same(stay, library.SwapAction(AvatarFixture.BlobActionDir + "/stayb.act"));
        Assert.NotNull(stay.Ani);
        Assert.Equal(AvatarFixture.SwapStayAni, stay.AniPath);
        Assert.True(stay.HasTriggers);
        Assert.Null(library.SwapAction(AvatarFixture.BlobActionDir + "/missing.act"));
        Assert.Null(library.SwapAction(""));
        Assert.Null(library.SwapAction(null));
    }
}
