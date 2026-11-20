using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// r19-particle 阶段 3:.act [PARTICLE]（行为码 0xaa，加载 0x013419f8、执行 0x010c5f91）编译与运行器合成单测。
/// 全部数据为手写合成脚本（编号 900+），不含任何原版资源路径。
/// </summary>
public sealed partial class ActRuntimeTests
{
    private sealed class ParticleRecordingHost : IActHost
    {
        public Dictionary<ActSelector, List<IActEntity>> Sets { get; } = new();
        public List<ActParticleRequest> Requests { get; } = new();
        public Dictionary<string, int> UnsupportedBehaviors { get; } = new();

        public int Random(int n) => 0;

        public long NowMs => 0;

        public void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft)
        {
        }

        public void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into)
        {
            if (Sets.TryGetValue(selector, out List<IActEntity> list))
            {
                into.AddRange(list);
            }
        }

        public void Shake(int strength, int durationMs)
        {
        }

        public void SummonMonster(IActEntity master, int index, int level, float x, float y, float z, bool facingLeft, int lifetimeMs)
        {
        }

        public void SpawnParticle(ActParticleRequest request) => Requests.Add(request);

        public void NoteUnsupportedBehavior(string tag)
        {
            UnsupportedBehaviors.TryGetValue(tag, out int n);
            UnsupportedBehaviors[tag] = n + 1;
        }
    }

    private static ActProgram CompileWithPath(string programPath, params ScriptLine[] lines)
    {
        return ActProgram.Compile(programPath, ActParser.Parse(lines));
    }

    [Fact]
    public void ParticleCompilesZeroToThreeOffsetsAndRejectsMalformedShapes()
    {
        // "s i i i"（全库 1,493 处）：保留原路径与三个有符号偏移
        ActProgram full = CompileWithPath("monster/synth_901/action/atk.act", Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[PARTICLE]", "fx/burst.ptl", 12, -34, 56))));
        ActBehavior bFull = full.Behaviors[0].Items[0];
        Assert.Equal(ActBehaviorKind.Particle, bFull.Kind);
        Assert.Equal("[PARTICLE]", bFull.Tag);
        Assert.Equal("fx/burst.ptl", bFull.ParticlePath);
        Assert.Equal((12, -34, 56), (bFull.ParticleOffsetX, bFull.ParticleOffsetY, bFull.ParticleOffsetZ));
        Assert.Empty(full.Behaviors[0].UnsupportedTags);

        // "s"（全库 2 处）、"s i"、"s i i"：缺省尾部偏移补 0
        ActProgram zero = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[PARTICLE]", "fx/zero.ptl"))));
        ActBehavior bZero = zero.Behaviors[0].Items[0];
        Assert.Equal((ActBehaviorKind.Particle, "fx/zero.ptl", 0, 0, 0),
            (bZero.Kind, bZero.ParticlePath, bZero.ParticleOffsetX, bZero.ParticleOffsetY, bZero.ParticleOffsetZ));

        ActProgram one = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[PARTICLE]", "fx/one.ptl", 17))));
        ActBehavior bOne = one.Behaviors[0].Items[0];
        Assert.Equal((ActBehaviorKind.Particle, 17, 0, 0), (bOne.Kind, bOne.ParticleOffsetX, bOne.ParticleOffsetY, bOne.ParticleOffsetZ));

        ActProgram two = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[PARTICLE]", "fx/two.ptl", 17, -23))));
        ActBehavior bTwo = two.Behaviors[0].Items[0];
        Assert.Equal((ActBehaviorKind.Particle, 17, -23, 0), (bTwo.Kind, bTwo.ParticleOffsetX, bTwo.ParticleOffsetY, bTwo.ParticleOffsetZ));

        // 畸形写法：无参数、空串、非字符串首参、多于 3 个数、偏移后尾随字符串 -> 一律保持 Unsupported，不静默猜
        ScriptLine[] malformedCases =
        {
            L("[PARTICLE]"),
            L("[PARTICLE]", ""),
            L("[PARTICLE]", 10, 20, 30),
            L("[PARTICLE]", "fx/too_many.ptl", 1, 2, 3, 4),
            L("[PARTICLE]", "fx/trail.ptl", 1, 2, "extra"),
        };
        foreach (ScriptLine badLine in malformedCases)
        {
            ActProgram bad = P(Cat(
                Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
                Behavior(badLine)));
            Assert.Equal(ActBehaviorKind.Unsupported, bad.Behaviors[0].Items[0].Kind);
            Assert.Contains("[PARTICLE]", bad.Behaviors[0].UnsupportedTags);

            var host = new ParticleRecordingHost();
            new ActRunner(bad, new Entity(), host).Update(0, 0.016f);
            Assert.Empty(host.Requests);
            Assert.Equal(1, host.UnsupportedBehaviors["[PARTICLE]"]);
        }
    }

    [Fact]
    public void ParticleRequestUsesUnmirroredOffsetsAndInheritsOwnerFacingAndActsOnceOnOwner()
    {
        ActProgram p = CompileWithPath("monster/synth_902/action/slash.act", Cat(
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(
                L("[PARTICLE]", "part/spark.ptl", 15, -25, 40),
                L("[SET SPEED]"), L("[X AXIS]", 9), L("[/SET SPEED]"))));

        // 1) 拥有者朝右 (FacingLeft=false)，工作集有 3 个敌人 -> 仍只对拥有者发 1 条请求，坐标 = owner + (15, -25, 40)
        var ownerRight = new Entity { X = 100f, Y = 200f, Z = 30f, FacingLeft = false };
        var hostRight = new ParticleRecordingHost();
        hostRight.Sets[ActSelector.AllEnemy] = new List<IActEntity>
        {
            new Entity { X = 500f, Y = 600f, Z = 0f },
            new Entity { X = 700f, Y = 800f, Z = 0f },
            new Entity { X = 900f, Y = 1000f, Z = 0f },
        };
        var runnerRight = new ActRunner(p, ownerRight, hostRight);
        runnerRight.Update(0, 0.016f);

        Assert.Equal(1, runnerRight.ParticlesRequested);
        ActParticleRequest r1 = Assert.Single(hostRight.Requests);
        Assert.Same(ownerRight, r1.Owner);
        Assert.Equal("part/spark.ptl", r1.Path);
        Assert.Equal("monster/synth_902/action/slash.act", r1.FromProgram);
        Assert.Equal("monster/synth_902/action/part/spark.ptl", r1.ResolvedPath);
        Assert.Equal((15, -25, 40), (r1.OffsetX, r1.OffsetY, r1.OffsetZ));
        Assert.Equal((115f, 175f, 70f), (r1.WorldX, r1.WorldY, r1.WorldZ));
        Assert.False(r1.FacingLeft);

        // 2) 拥有者朝左 (FacingLeft=true)：偏移 (15, -25, 40) 不镜像 -> WorldX 仍为 100 + 15 = 115（而非 85），但 FacingLeft = true（供粒子运动镜像）
        var ownerLeft = new Entity { X = 100f, Y = 200f, Z = 30f, FacingLeft = true };
        var hostLeft = new ParticleRecordingHost();
        hostLeft.Sets[ActSelector.AllEnemy] = new List<IActEntity> { new Entity { X = 500f, Y = 600f, Z = 0f } };
        var runnerLeft = new ActRunner(p, ownerLeft, hostLeft);
        runnerLeft.Update(0, 0.016f);

        Assert.Equal(1, runnerLeft.ParticlesRequested);
        ActParticleRequest r2 = Assert.Single(hostLeft.Requests);
        Assert.Same(ownerLeft, r2.Owner);
        Assert.Equal((115f, 175f, 70f), (r2.WorldX, r2.WorldY, r2.WorldZ));
        Assert.True(r2.FacingLeft);
    }

    [Fact]
    public void ParticleRequestNullGuardsNullFromProgramResolutionAndDefaultHostNoOp()
    {
        var owner = new Entity { X = 10f, Y = 20f, Z = 5f, FacingLeft = true };
        Assert.Throws<ArgumentNullException>(() => new ActParticleRequest(null!, "a.ptl", null, 0, 0, 0, 0, 0, 0, false));
        Assert.Throws<ArgumentNullException>(() => new ActParticleRequest(owner, null!, null, 0, 0, 0, 0, 0, 0, false));

        var noProg = new ActParticleRequest(owner, "fx\\top.ptl", null, 1, 2, 3, 11f, 22f, 8f, true);
        Assert.Equal("fx/top.ptl", noProg.ResolvedPath);
        Assert.Null(noProg.FromProgram);

        // 宿主未覆写 IActHost.SpawnParticle 时走接口默认空实现不报错，计数器仍累加
        ActProgram p = P(Cat(
            Trigger(L("[LIMIT]", 2), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[PARTICLE]", "fx/noop.ptl", 3, 4, 5))));
        var runner = new ActRunner(p, owner, new Host());
        runner.Update(0, 0.016f);
        runner.Update(1, 0.016f);
        Assert.Equal(2, runner.ParticlesRequested);
    }
}
