using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [CHECK STUCK](r14 怪物余项)。客户端只有两处给记录类型 8 求值:受击运行器 0x010a8c30 的 0x010a9460 与攻击成功运行器 0x010aefc0 的 0x010af3ef,
/// 写法相同:读第 0 个参数(越界 -1,0x0107b2e0),"非 0"必须等于事件带来的 stuck 标志(.atk 的 [stuck] 概率掷中,E-057),不等就放弃触发器。
/// 全库 7 处都紧跟在 [ON ATTACKSUCCESS] 后,参数全是 0。这里只用合成脚本,不含真实数据。
/// </summary>
public sealed partial class ActRuntimeTests
{
    private static ActProgram StuckGate(string evt, int? n) => P(Cat(
        Trigger(n.HasValue ? new[] { L(evt), L("[CHECK STUCK]", n.Value), L("[DO BEHAVIOR]"), L("[ME]", 0) } : new[] { L(evt), L("[CHECK STUCK]"), L("[DO BEHAVIOR]"), L("[ME]", 0) }),
        Behavior(Speed(901))));

    private static int StuckFires(string evt, int? n, bool stuck, bool useLegacyOverload = false)
    {
        var owner = new Entity();
        var other = new Entity { Kind = ActEntityKind.Character };
        var runner = new ActRunner(StuckGate(evt, n), owner, new Host());
        if (evt == "[ON ATTACKSUCCESS]")
        {
            if (useLegacyOverload)
            {
                runner.OnAttackSuccess(other);
            }
            else
            {
                runner.OnAttackSuccess(other, stuck);
            }
        }
        else
        {
            if (useLegacyOverload)
            {
                runner.OnDamage(other);
            }
            else
            {
                runner.OnDamage(other, 0, stuck);
            }
        }
        return owner.Log.Count;
    }

    [Fact]
    public void CheckStuckCompilesToItsOwnRecordWithTheFirstIntegerAsTheArgument()
    {
        ActProgram zero = StuckGate("[ON ATTACKSUCCESS]", 0);
        Assert.True(zero.Triggers[0].Supported);
        ActRecord rec = zero.Triggers[0].Records.Single(r => r.Kind == ActRecordKind.CheckStuck);
        Assert.Equal(0, rec.Number);
        Assert.Equal(7, StuckGate("[ON DAMAGE]", 7).Triggers[0].Records.Single(r => r.Kind == ActRecordKind.CheckStuck).Number);
    }

    [Fact]
    public void CheckStuckWithoutAnArgumentReadsMinusOneLikeTheClientsOutOfRangeParameter()
    {
        // 0x0107b2e0 越界返回 -1;-1 非 0,所以没写数字等同于"要求这一击 stuck"
        ActRecord rec = StuckGate("[ON ATTACKSUCCESS]", null).Triggers[0].Records.Single(r => r.Kind == ActRecordKind.CheckStuck);
        Assert.Equal(-1, rec.Number);
        Assert.Equal(0, StuckFires("[ON ATTACKSUCCESS]", null, stuck: false));
        Assert.Equal(1, StuckFires("[ON ATTACKSUCCESS]", null, stuck: true));
    }

    [Fact]
    public void ZeroArgumentPassesOnlyWhenTheHitIsNotStuck()
    {
        foreach (string evt in new[] { "[ON ATTACKSUCCESS]", "[ON DAMAGE]" })
        {
            Assert.Equal(1, StuckFires(evt, 0, stuck: false));
            Assert.Equal(0, StuckFires(evt, 0, stuck: true));
        }
    }

    [Fact]
    public void NonZeroArgumentPassesOnlyWhenTheHitIsStuck()
    {
        foreach (string evt in new[] { "[ON ATTACKSUCCESS]", "[ON DAMAGE]" })
        {
            foreach (int n in new[] { 1, 2, -3 })
            {
                Assert.Equal(0, StuckFires(evt, n, stuck: false));
                Assert.Equal(1, StuckFires(evt, n, stuck: true));
            }
        }
    }

    [Fact]
    public void HostsThatNeverReportStuckLandInTheNotStuckBranch()
    {
        // 旧入口(没有 stuck 参数)等同于"没有 stuck 命中":参数 0 通过,参数非 0 不通过
        foreach (string evt in new[] { "[ON ATTACKSUCCESS]", "[ON DAMAGE]" })
        {
            Assert.Equal(1, StuckFires(evt, 0, stuck: false, useLegacyOverload: true));
            Assert.Equal(0, StuckFires(evt, 1, stuck: false, useLegacyOverload: true));
        }
    }

    [Fact]
    public void TheStuckFlagIsOnlyValidDuringThatOneWakeUp()
    {
        // 先带 stuck 唤醒一次(参数 0 的触发器放弃),下一次不带 stuck 的唤醒要正常触发:标志不能残留
        var owner = new Entity();
        var other = new Entity { Kind = ActEntityKind.Character };
        var runner = new ActRunner(StuckGate("[ON ATTACKSUCCESS]", 0), owner, new Host());
        runner.OnAttackSuccess(other, stuck: true);
        Assert.Empty(owner.Log);
        runner.OnAttackSuccess(other);
        Assert.Single(owner.Log);

        // 反过来:参数 1 的触发器先 stuck 触发一次,随后的普通命中不能因为残留标志再触发
        var owner2 = new Entity();
        var runner2 = new ActRunner(StuckGate("[ON DAMAGE]", 1), owner2, new Host());
        runner2.OnDamage(other, 0, stuck: true);
        Assert.Single(owner2.Log);
        runner2.OnDamage(other, 0);
        Assert.Single(owner2.Log);
    }

    /// <summary>
    /// 三个触发器、三个行为组,各自的日志条数可以区分:
    /// 攻击成功里要 stuck 的(组 0);受击里要 stuck 且是水元素的(组 1);受击里要 stuck 且是火元素的(组 2)。
    /// "不带元素的唤醒"(旧的一参数 OnDamage)让元素条件一律通过,所以它能把任何残留的 stuck 标志暴露成多触发的日志。
    /// </summary>
    private static ActProgram StuckLeakProbe() => P(Cat(
        Trigger(L("[ON ATTACKSUCCESS]"), L("[CHECK STUCK]", 1), L("[DO BEHAVIOR]"), L("[ME]", 0)),
        Trigger(L("[ON DAMAGE]"), L("[CHECK STUCK]", 1), L("[IS WATER DAMAGE]"), L("[DO BEHAVIOR]"), L("[ME]", 1)),
        Trigger(L("[ON DAMAGE]"), L("[CHECK STUCK]", 1), L("[IS FIRE DAMAGE]"), L("[DO BEHAVIOR]"), L("[ME]", 2)),
        Behavior(Speed(901)),
        Behavior(Speed(902)),
        Behavior(Speed(903))));

    [Fact]
    public void AStuckHitFromEitherEventDoesNotLeakIntoALaterHitThatCarriesNoFlag()
    {
        // 旧的一参数 OnDamage 不带 stuck 标志,它读到的必须是"没有 stuck":哪怕上一次唤醒(任一事件)带着 stuck
        var other = new Entity { Kind = ActEntityKind.Character };

        var afterDamage = new Entity();
        var viaDamage = new ActRunner(StuckLeakProbe(), afterDamage, new Host());
        viaDamage.OnDamage(other, 2, stuck: true);          // 水且 stuck:只有组 1 的触发器触发
        Assert.Single(afterDamage.Log);
        viaDamage.OnDamage(other);                          // 旧入口:没有标志,三个触发器里要 stuck 的都不能再触发(组 2 本来没触发过)
        Assert.Single(afterDamage.Log);

        var afterAttack = new Entity();
        var viaAttack = new ActRunner(StuckLeakProbe(), afterAttack, new Host());
        viaAttack.OnAttackSuccess(other, stuck: true);      // 组 0 触发
        Assert.Single(afterAttack.Log);
        viaAttack.OnDamage(other);                          // 跨事件:攻击成功带过 stuck,后面的受击旧入口也不能借用(组 1、2 都没触发过)
        Assert.Single(afterAttack.Log);
    }

    [Fact]
    public void TheProbeFiresEveryStuckTriggerWhenTheOneArgumentEntryReallyCarriesTheFlag()
    {
        // 探针自己要能"看见"标志:同一份脚本、同一个旧入口,只要这一击确实 stuck(用带标志的入口),三组里元素合得上的就触发
        var other = new Entity { Kind = ActEntityKind.Character };
        var owner = new Entity();
        var runner = new ActRunner(StuckLeakProbe(), owner, new Host());
        runner.OnDamage(other, 0, stuck: true);             // 没有元素(0):水与火的元素条件都不过,不触发
        Assert.Empty(owner.Log);
        runner.OnDamage(other, 1, stuck: true);             // 火:组 2 触发
        Assert.Single(owner.Log);
        runner.OnDamage(other, 2, stuck: true);             // 水:组 1 触发
        Assert.Equal(2, owner.Log.Count);
    }

    [Fact]
    public void CheckStuckOutsideTheTwoHitEventsNeverPasses()
    {
        // 客户端只有受击与攻击成功两个运行器认类型 8;逐帧触发器与 [ON SET ACTION] 里的 [CHECK STUCK] 不通过(推断:其余运行器没有这个分支)
        foreach (int n in new[] { 0, 1 })
        {
            var polled = new Entity();
            new ActRunner(P(Cat(
                Trigger(L("[FRAME]", 0, 0), L("[CHECK STUCK]", n), L("[DO BEHAVIOR]"), L("[ME]", 0)),
                Behavior(Speed(901)))), polled, new Host()).Update(0, 0.016f);
            Assert.Empty(polled.Log);

            var onSet = new Entity();
            var runner = new ActRunner(StuckGate("[ON SET ACTION]", n), onSet, new Host());
            runner.OnSetAction();
            Assert.Empty(onSet.Log);
        }
    }

    [Fact]
    public void ADifferentHitEventDoesNotBorrowTheOtherEventsStuckGate()
    {
        // [ON DAMAGE] 里的 [CHECK STUCK] 只被 OnDamage 唤醒;OnAttackSuccess 唤醒它不动
        var owner = new Entity();
        var other = new Entity { Kind = ActEntityKind.Character };
        var runner = new ActRunner(StuckGate("[ON DAMAGE]", 0), owner, new Host());
        runner.OnAttackSuccess(other, stuck: false);
        Assert.Empty(owner.Log);
        runner.OnDamage(other, 0, stuck: false);
        Assert.Single(owner.Log);
    }

    [Fact]
    public void ConditionsAfterTheGateAreStillEvaluatedWhenTheGatePasses()
    {
        // [CHECK STUCK] 0 通过后,后面的元素条件照常判定:通过 stuck 门但元素不符仍然不触发
        ActProgram p = P(Cat(
            Trigger(L("[ON DAMAGE]"), L("[CHECK STUCK]", 0), L("[IS FIRE DAMAGE]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(901))));
        var owner = new Entity();
        var other = new Entity { Kind = ActEntityKind.Character };
        var runner = new ActRunner(p, owner, new Host());
        runner.OnDamage(other, 2, stuck: false);      // 水:元素条件不过
        Assert.Empty(owner.Log);
        runner.OnDamage(other, 1, stuck: false);      // 火且没 stuck:全过
        Assert.Single(owner.Log);
        runner.OnDamage(other, 1, stuck: true);       // 火但 stuck:门不过
        Assert.Single(owner.Log);
    }
}
