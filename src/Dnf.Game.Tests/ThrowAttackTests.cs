using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 怪物的 [throw attack] 块与投掷状态机、.ptl 粒子定义的读法(证据 E-103)。数值与路径全是合成的,与原版数据无关;
/// 检验的是我们照客户端函数写下的规则(子标签名、缺省值、发射条件的比较方向、次数的重置与递减顺序)。
/// </summary>
public class ThrowAttackTests
{
    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptValue S(string text) => new ScriptValue(ScriptValueKind.String, 0, text);

    private static ScriptLine Line(string tag, params ScriptValue[] values) => new ScriptLine(tag, values);

    private static ThrowAttackRecord Block(params ScriptValue[] values) => ThrowAttackRecord.ParseBlock(values);

    // ------------------------------------------------------------------ [throw attack] 块

    [Fact]
    public void EveryKeyOfABlockIsReadFromTheStringAndValuePairs()
    {
        ThrowAttackRecord r = Block(
            S("[attack index]"), I(2),
            S("[passive object number]"), I(3),
            S("[passive object filename]"), S("Shot/A.ptl"), S("Shot/B.ptl"), S("Shot/C.ptl"),
            S("[object type]"), I(4),
            S("[passive object index]"), I(77),
            S("[power]"), I(5),
            S("[throw frame]"), I(6),
            S("[passive object start x cood]"), I(10), I(20), I(30),
            S("[passive object start y cood]"), I(-1), I(-2), I(-3),
            S("[passive object start z cood]"), I(40), I(50), I(60),
            S("[attack number]"), I(7),
            S("[throw sound]"), S("THROW_X"),
            S("[start kind]"), I(1),
            S("[direction]"), I(0), I(1), I(0));
        Assert.Equal((2, 3, 4, 77, 5, 6, 7, 1), (r.AttackIndex, r.PassiveObjectNumber, r.ObjectType, r.PassiveObjectIndex, r.Power, r.ThrowFrame, r.AttackNumber, r.StartKind));
        Assert.Equal(new[] { "Shot/A.ptl", "Shot/B.ptl", "Shot/C.ptl" }, r.ParticleFiles);
        Assert.Equal(new[] { 10, 20, 30 }, r.StartX);
        Assert.Equal(new[] { -1, -2, -3 }, r.StartY);
        Assert.Equal(new[] { 40, 50, 60 }, r.StartZ);
        Assert.Equal("THROW_X", r.ThrowSound);
        Assert.Equal((20, -2, 50), r.StartOffset(1));
        Assert.Equal(new[] { false, true, false }, new[] { r.IsReversed(0), r.IsReversed(1), r.IsReversed(2) });
        Assert.Equal("Shot/B.ptl", r.ParticleFile(1));
        Assert.Null(r.ParticleFile(3));
        Assert.Equal(3, r.ShotCount);
    }

    [Fact]
    public void AMissingKeyKeepsTheClientDefault()
    {
        ThrowAttackRecord r = Block();
        Assert.Equal((-1, -1, 0, 0, -1, -1, -1, 0), (r.AttackIndex, r.PassiveObjectNumber, r.ObjectType, r.PassiveObjectIndex, r.Power, r.ThrowFrame, r.AttackNumber, r.StartKind));
        Assert.Empty(r.ParticleFiles);
        Assert.Empty(r.StartX);
        Assert.Empty(r.Directions);
        Assert.Null(r.ThrowSound);
        Assert.Equal(0, r.ShotCount);                          // [passive object number] 缺省 -1:放不出东西
        Assert.Equal((0, 0, 0), r.StartOffset(0));
    }

    [Fact]
    public void AnEmptySoundIsNoSoundAndAShortOffsetTablePadsWithZero()
    {
        ThrowAttackRecord r = Block(S("[throw sound]"), S(""), S("[passive object number]"), I(2), S("[passive object start x cood]"), I(15));
        Assert.Null(r.ThrowSound);
        Assert.Equal((15, 0, 0), r.StartOffset(0));
        Assert.Equal((0, 0, 0), r.StartOffset(1));
        Assert.False(r.IsReversed(5));
    }

    [Fact]
    public void UnknownKeysAreSkippedWithTheirValuesAndValuesBeforeAnyKeyAreIgnored()
    {
        ThrowAttackRecord r = Block(I(99), S("[no such key]"), I(1), I(2), S("[throw frame]"), I(4), S("[also unknown]"), S("text"), S("[attack index]"), I(1));
        Assert.Equal((4, 1), (r.ThrowFrame, r.AttackIndex));
    }

    [Fact]
    public void TheShotCountIsClampedAndOnlyThrowAttackLinesAreReadInOrder()
    {
        Assert.Equal(ThrowAttackRecord.MaxShots, Block(S("[passive object number]"), I(100000)).ShotCount);
        Assert.Equal(0, Block(S("[passive object number]"), I(-5)).ShotCount);
        var tags = new TagTable(new[]
        {
            Line("[attack kind]", I(1)),
            Line("[throw attack]", S("[attack index]"), I(0)),
            Line("[/throw attack]"),
            Line("[waiting motion]", S("a.ani")),
            Line("[throw attack]", S("[attack index]"), I(1)),
            Line("[/throw attack]"),
        });
        IReadOnlyList<ThrowAttackRecord> records = ThrowAttackRecord.Parse(tags);
        Assert.Equal(new[] { 0, 1 }, records.Select(x => x.AttackIndex).ToArray());
        Assert.Empty(ThrowAttackRecord.Parse(new TagTable(new[] { Line("[attack kind]", I(1)) })));
    }

    // ------------------------------------------------------------------ 投掷状态机

    private static ThrowAttackRecord Simple(int attack = 0, int frame = 2, int number = 1, int index = 5, int attackNumber = 1, string sound = null)
    {
        return new ThrowAttackRecord(attack, number, null, 0, index, 0, frame, null, null, null, attackNumber, sound);
    }

    [Fact]
    public void ItThrowsOnlyAfterTheAnimationFramePassesTheThrowFrame()
    {
        var runner = new ThrowAttackRunner(new[] { Simple(frame: 2) });
        runner.EnterAttack();
        Assert.False(runner.Tick(0, 0).Any);
        Assert.False(runner.Tick(0, 2).Any);                    // 等于发射帧还不行:要"超过"
        ThrowTick t = runner.Tick(0, 3);
        Assert.Equal(new[] { new ThrowShot(0, 0, false) }, t.Shots.ToArray());
        Assert.True(runner.Thrown(0));
        Assert.False(runner.Tick(0, 4).Any);                    // 同一次出招只发射一次
    }

    [Fact]
    public void ARecordBelongsToOneAttackAndNeedsAPassiveObjectIndex()
    {
        var runner = new ThrowAttackRunner(new[] { Simple(attack: 1), Simple(attack: 0, index: 0) });
        runner.EnterAttack();
        Assert.False(runner.Tick(0, 9).Any);                    // 第 0 条属于第 1 招;第 1 条的被动对象编号是 0,不发射
        Assert.Single(runner.Tick(1, 9).Shots);
    }

    [Fact]
    public void ReEnteringTheAttackClearsTheThrownFlagsButTheRemainingCountOnlyResetsWhileStanding()
    {
        var runner = new ThrowAttackRunner(new[] { Simple(attackNumber: 3) });
        runner.EnterAttack();
        ThrowTick first = runner.Tick(0, 9);
        Assert.True(first.RepeatAttack);                        // 剩 3 次:放完还剩 2,要再来一遍
        Assert.Equal(2, runner.Remaining(0));
        runner.EnterAttack();
        Assert.False(runner.Thrown(0));
        Assert.True(runner.Tick(0, 9).RepeatAttack);            // 剩 2:放完还剩 1,还要再来一遍
        Assert.Equal(1, runner.Remaining(0));
        runner.EnterAttack();
        ThrowTick last = runner.Tick(0, 9);
        Assert.Single(last.Shots);
        Assert.False(last.RepeatAttack);                        // 剩 1:这是最后一次
        Assert.Equal(0, runner.Remaining(0));
        runner.EnterAttack();
        Assert.False(runner.Tick(0, 9).Any);                    // 剩余次数为 0:不再发射
        runner.Idle();
        Assert.Equal(3, runner.Remaining(0));                   // 站立一帧就恢复
        runner.EnterAttack();
        Assert.Single(runner.Tick(0, 9).Shots);
    }

    [Fact]
    public void TheDefaultAttackNumberThrowsOnceWithoutRepeating()
    {
        var runner = new ThrowAttackRunner(new[] { Simple(attackNumber: -1) });
        Assert.Equal(-1, runner.Remaining(0));
        runner.EnterAttack();
        ThrowTick t = runner.Tick(0, 9);
        Assert.Single(t.Shots);
        Assert.False(t.RepeatAttack);
        Assert.Equal(-2, runner.Remaining(0));
        runner.Idle();
        Assert.Equal(-1, runner.Remaining(0));
    }

    [Fact]
    public void AZeroAttackNumberNeverThrows()
    {
        var runner = new ThrowAttackRunner(new[] { Simple(attackNumber: 0) });
        runner.EnterAttack();
        Assert.False(runner.Tick(0, 9).Any);
    }

    [Fact]
    public void TheSoundIsReportedOncePerTriggeredRecordAndAnEmptyOneIsSilent()
    {
        var runner = new ThrowAttackRunner(new[] { Simple(number: 3, sound: "WHOOSH"), Simple(sound: null), Simple(frame: 8, sound: "LATE") });
        runner.EnterAttack();
        ThrowTick t = runner.Tick(0, 4);
        Assert.Equal(new[] { "WHOOSH" }, t.Sounds.ToArray());
        Assert.Equal(4, t.Shots.Count);                          // 第 0 条三个、第 1 条一个;第 2 条还没到发射帧
        Assert.Equal(new[] { 0, 0, 0, 1 }, t.Shots.Select(s => s.Record).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 0 }, t.Shots.Select(s => s.Slot).ToArray());
        ThrowTick later = runner.Tick(0, 9);
        Assert.Equal(new[] { "LATE" }, later.Sounds.ToArray());
        Assert.Single(later.Shots);
    }

    [Fact]
    public void TheReversedFlagComesFromTheDirectionTable()
    {
        var record = new ThrowAttackRecord(0, 2, null, 0, 5, 0, 1, null, null, null, 1, null, 0, new[] { 0, 1 });
        var runner = new ThrowAttackRunner(new[] { record });
        runner.EnterAttack();
        Assert.Equal(new[] { false, true }, runner.Tick(0, 2).Shots.Select(s => s.Reversed).ToArray());
    }

    // ------------------------------------------------------------------ .ptl

    [Fact]
    public void AStraightShotOnTheXAxisIsRecognized()
    {
        ParticleDef p = ParticleDef.Parse("monster/test/particle/shot.ptl", new[]
        {
            Line("[x axis]"),
            Line("[move type]", S("[straight line]")),
            Line("[move variable 1]", I(300), I(300)),
            Line("[life time]", I(900)),
            Line("[landing type]", S("[vanish]")),
        });
        Assert.Equal((ParticleMoveType.StraightLine, 300, 300), (p.X.Type, p.X.Var1A, p.X.Var1B));
        Assert.Equal(300.0, p.X.StraightSpeed);
        Assert.Equal((ParticleMoveType.Stop, ParticleMoveType.Stop), (p.Y.Type, p.Z.Type));
        Assert.Equal((900, ParticleLanding.Vanish, 0), (p.LifeTimeMs, p.Landing, p.Gravity));
        Assert.True(p.IsPlainHorizontalShot);
    }

    [Fact]
    public void AxisTagsScopeTheFollowingMovementTags()
    {
        ParticleDef p = ParticleDef.Parse("p.ptl", new[]
        {
            Line("[x axis]"),
            Line("[move type]", S("[straight line]")),
            Line("[move variable 1]", I(100), I(100)),
            Line("[z axis]"),
            Line("[move type]", S("[sin]")),
            Line("[move variable 1]", I(5), I(6)),
            Line("[move variable 2]", I(7), I(8)),
            Line("[changing type]", S("[ping pong]")),
            Line("[changing term]", I(250)),
            Line("[gravity]", I(12)),
            Line("[landing type]", S("[slide]")),
        });
        Assert.Equal((ParticleMoveType.StraightLine, 100), (p.X.Type, p.X.Var1A));
        Assert.Equal((ParticleMoveType.Sin, 5, 6, 7, 8, ParticleChanging.PingPong, 250), (p.Z.Type, p.Z.Var1A, p.Z.Var1B, p.Z.Var2A, p.Z.Var2B, p.Z.Changing, p.Z.ChangingTerm));
        Assert.Equal(0, p.Z.StraightSpeed);                      // 只有直线运动有"直线速度"
        Assert.Equal((12, ParticleLanding.Slide, -1), (p.Gravity, p.Landing, p.LifeTimeMs));
        Assert.False(p.IsPlainHorizontalShot);                   // z 轴在做正弦运动
    }

    [Fact]
    public void WithoutAnAxisTagMovementTagsBelongToTheXAxisAndObjectBlocksAreSkipped()
    {
        ParticleDef p = ParticleDef.Parse("p.ptl", new[]
        {
            Line("[move type]", S("[straight line]")),
            Line("[move variable 1]", I(50), I(60)),
            Line("[object]"),
            Line("[y axis]"),
            Line("[move type]", S("[cos]")),
            Line("[life time]", I(5)),
            Line("[/object]"),
            Line("[life time]", I(400)),
            Line("[landing type]", S("[unknown spelling]")),
        });
        Assert.Equal((ParticleMoveType.StraightLine, 50, 60), (p.X.Type, p.X.Var1A, p.X.Var1B));
        Assert.Equal(ParticleMoveType.Stop, p.Y.Type);           // 子对象里的 y 轴不算
        Assert.Equal(400, p.LifeTimeMs);                         // 子对象里的 [life time] 不算
        Assert.Equal(ParticleLanding.Pierce, p.Landing);         // 认不得的写法:保持缺省
    }

    [Fact]
    public void AnUnknownMoveTypeStringKeepsTheAxisStopped()
    {
        ParticleDef p = ParticleDef.Parse("p.ptl", new[] { Line("[x axis]"), Line("[move type]", S("[teleport]")), Line("[move variable 1]", I(9), I(9)) });
        Assert.Equal(ParticleMoveType.Stop, p.X.Type);
        Assert.Equal(0, p.X.StraightSpeed);
    }
}
