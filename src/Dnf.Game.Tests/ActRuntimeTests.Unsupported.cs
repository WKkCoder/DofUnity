using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// 没做的行为与读不懂的触发器标签的运行期统计(M54,证据 E-147):运行器执行到没做的行为时向宿主通报一次,
/// 每次(重新)开始时把"读不懂的触发器"的每种标签通报一次。这些只是给场景排优先级用的统计,不改变运行结果。脚本内容全是合成的。
/// </summary>
public sealed partial class ActRuntimeTests
{
    private sealed class CountingHost : IActHost
    {
        public Dictionary<string, int> Behaviors { get; } = new();
        public Dictionary<string, int> Triggers { get; } = new();
        public long NowMs { get; set; }

        public int Random(int n) => 0;

        public void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft)
        {
        }

        public void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into)
        {
        }

        public void Shake(int strength, int durationMs)
        {
        }

        public void SummonMonster(IActEntity master, int index, int level, float x, float y, float z, bool facingLeft, int lifetimeMs)
        {
        }

        public void NoteUnsupportedBehavior(string tag)
        {
            Behaviors.TryGetValue(tag, out int n);
            Behaviors[tag] = n + 1;
        }

        public void NoteUnsupportedTrigger(string tag)
        {
            Triggers.TryGetValue(tag, out int n);
            Triggers[tag] = n + 1;
        }
    }

    [Fact]
    public void AnUnsupportedBehaviorIsReportedOncePerExecutionAndTheRestOfTheGroupStillRuns()
    {
        ActProgram p = P(Cat(
            Trigger(L("[LIMIT]", 2), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[PARTICLE]"), L("[SET SPEED]"), L("[X AXIS]", 7), L("[/SET SPEED]"), L("[SAY SPEECH]"))));
        var host = new CountingHost();
        var e = new Entity();
        var runner = new ActRunner(p, e, host);

        runner.Update(0, 0.01f);
        runner.Update(1, 0.01f);

        Assert.Equal(2, runner.TotalFired);
        Assert.Equal(2, e.Log.Count(l => l == "speed:7,-,-"));
        Assert.Equal(2, host.Behaviors["[PARTICLE]"]);
        Assert.Equal(2, host.Behaviors["[SAY SPEECH]"]);
        Assert.False(host.Behaviors.ContainsKey("[SET SPEED]"));
        Assert.Empty(host.Triggers);
    }

    [Fact]
    public void ATriggerWithAnUnreadableTagIsReportedOnEveryStartNotOnEveryUpdate()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[IF]", "x"), L("[/IF]"), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(10))));
        var host = new CountingHost();
        var runner = new ActRunner(p, new Entity(), host);

        Assert.Equal(1, host.Triggers["[IF]"]);
        runner.Update(0, 0.01f);
        runner.Update(1, 0.01f);
        runner.Update(2, 0.01f);
        Assert.Equal(1, host.Triggers["[IF]"]);

        runner.Begin();
        Assert.Equal(2, host.Triggers["[IF]"]);
        Assert.Single(host.Triggers);
    }

    [Fact]
    public void AHostThatDoesNotCareGetsNoCallsAndNothingBreaks()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[PARTICLE]"))));
        var runner = new ActRunner(p, new Entity(), new Host());
        runner.Update(0, 0.01f);
        Assert.Equal(1, runner.TotalFired);
    }
}
