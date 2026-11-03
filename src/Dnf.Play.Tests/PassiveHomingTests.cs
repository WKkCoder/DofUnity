using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;

namespace Dnf.Play.Tests;

/// <summary>
/// 被动对象的追踪控制器(M67,证据 E-165):出生时的初始方向、检查间隔、两种转法(无 [max rotation] 一步指过去、有 [max rotation] 每帧转一点)、
/// 追踪锥、时限、直线瞄准、跟着目标平移、朝向与画面旋转。数据全是合成的:身体与目标都是替身,位置不动(除非测试自己挪),
/// 帧长与对象年龄由测试给,所以数全是手算的:速度向量 = 速度 × (cos, sin × 0.29),方向是整度数学角。
/// </summary>
public class PassiveHomingTests
{
    private static ScriptValue V(object v) => v switch
    {
        int i => new ScriptValue(ScriptValueKind.Int, i, null),
        float f => new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(f), null),
        string s => new ScriptValue(ScriptValueKind.String, 0, s),
        _ => throw new ArgumentException("不支持的值类型"),
    };

    private static ScriptLine L(string tag, params object[] values) => new(tag, Array.ConvertAll(values, V));

    private static HomingDef Def(params ScriptLine[] lines)
    {
        var all = new List<ScriptLine> { L("[homing]"), L("[homing use]", 1) };
        all.AddRange(lines);
        all.Add(L("[/homing]"));
        return HomingDef.Read(new TagTable(all.ToArray()));
    }

    private sealed class Body : IHomingBody
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double VelocityX { get; set; }
        public double VelocityY { get; set; }
        public double VelocityZ { get; set; }
        public bool? Left { get; set; }
        public int FacingCalls { get; set; }
        public float Rotation { get; set; }
        public int Moves { get; set; }

        public void SetPlanarVelocity(double vx, double vy)
        {
            VelocityX = vx;
            VelocityY = vy;
        }

        public void SetVerticalVelocity(double vz) => VelocityZ = vz;

        public void MoveTo(double x, double y)
        {
            X = x;
            Y = y;
            Moves++;
        }

        public void SetHomingFacing(bool left)
        {
            Left = left;
            FacingCalls++;
        }

        public void SetRotation(float radians) => Rotation = radians;
    }

    private sealed class Actor : IActEntity
    {
        public ActEntityKind Kind => ActEntityKind.Character;
        public int ObjectIndex => 0;
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public bool FacingLeft => false;
        public bool IsAlive { get; set; } = true;
        public IActEntity Target => null;
        public IActEntity Master => null;
        public int MotionVersion => 0;
        public int Hp => 0;
        public int HpMax => 0;
        public int Level => 0;

        public void SetFacing(bool left)
        {
        }

        public void SetSpeed(bool hasX, float vx, bool hasY, float vy, bool hasZ, float vz)
        {
        }

        public void SetAction(ActActionKind kind, int index, bool now, bool atEnd)
        {
        }

        public void Destroy(bool force)
        {
        }

        public void AttackRect(ActAttackRectMode mode)
        {
        }

        public void SetFrame(int frame)
        {
        }

        public void SetDamageBox(ActDamageBoxMode mode)
        {
        }

        public void SetFloatingHeight(int height)
        {
        }

        public bool Teleport(float x, float y, float z) => false;

        public void SetHp(int hp)
        {
        }
    }

    /// <summary>身体 + 目标 + 控制器 + 对象年龄;<see cref="Step"/> 先让年龄走 dt 毫秒再调控制器(身体默认不动,<paramref name="move"/> 为真时先按速度走一步)。</summary>
    private sealed class Sim
    {
        public readonly Body Body = new();
        public readonly Actor Target = new();
        public readonly PassiveHoming Homing;
        public double Now;

        public Sim(HomingDef def, bool facingLeft = false, bool noTarget = false)
        {
            Homing = new PassiveHoming(def);
            Homing.Start(Body, facingLeft, 0, noTarget ? null : Target);
        }

        public void Step(int dtMs, bool move = false)
        {
            Now += dtMs;
            if (move)
            {
                Body.X += Body.VelocityX * dtMs / 1000.0;
                Body.Y += Body.VelocityY * dtMs / 1000.0;
            }
            Homing.Step(Body, dtMs, Now);
        }

        public void Aim(float x, float y, float z = 0)
        {
            Target.X = x;
            Target.Y = y;
            Target.Z = z;
        }
    }

    // ------------------------------------------------------------------ 方向角

    [Fact]
    public void HeadingIsAWholeDegreeMathAngleThatUndoesTheDepthCompression()
    {
        Assert.Equal(0, PassiveHoming.Heading(0, 0, 100, 0));
        Assert.Equal(45, PassiveHoming.Heading(0, 0, 100, 29));       // 纵深差 29 还原成 100(乘 100 除以 29),所以 (100, 29) 是 45 度
        Assert.Equal(90, PassiveHoming.Heading(0, 0, 0, 29));
        Assert.Equal(135, PassiveHoming.Heading(0, 0, -100, 29));
        Assert.Equal(180, PassiveHoming.Heading(0, 0, -100, 0));
        Assert.Equal(225, PassiveHoming.Heading(0, 0, -100, -29));
        Assert.Equal(270, PassiveHoming.Heading(0, 0, 0, -29));
        Assert.Equal(315, PassiveHoming.Heading(0, 0, 100, -29));
        Assert.Equal(90, PassiveHoming.Heading(50, 10, 50, 39));       // 看的是差
        Assert.Equal(0, PassiveHoming.Heading(7, 7, 7, 7));            // 重合:没有方向,取 0
    }

    [Fact]
    public void HeadingTruncatesTowardZeroSoNegativeAnglesAreNotRoundedDown()
    {
        Assert.Equal(1, PassiveHoming.Heading(0, 0, 100, 1));          // 1.97 度
        Assert.Equal(359, PassiveHoming.Heading(0, 0, 100, -1));       // -1.97 度截成 -1,不是 -2
        Assert.Equal(0, PassiveHoming.Heading(0, 0, 1000, 1));         // 0.2 度
        Assert.Equal(178, PassiveHoming.Heading(0, 0, -100, 1));       // 178.03 度
    }

    // ------------------------------------------------------------------ 出生

    [Fact]
    public void BirthPointsTheObjectAlongTheInitialRotationAndATurnedAroundWhenItFacesLeft()
    {
        HomingDef def = Def(L("[homing velocity]", 200, 0), L("[init rotation]", 30f));
        var right = new Sim(def);
        Assert.True(right.Homing.On);
        Assert.Equal(30f, right.Homing.Angle);
        Assert.Equal(173.2051, right.Body.VelocityX, 3);                // 200 × cos 30
        Assert.Equal(29.0, right.Body.VelocityY, 3);                    // 200 × sin 30 × 0.29

        var left = new Sim(def, facingLeft: true);
        Assert.Equal(210f, left.Homing.Angle);
        Assert.Equal(-173.2051, left.Body.VelocityX, 3);
        Assert.Equal(-29.0, left.Body.VelocityY, 3);
        Assert.Equal(1, right.Homing.Steers);                           // 出生时设了一次方向
    }

    [Fact]
    public void AHomingDirectionFromZeroToThreeReplacesTheObjectsOwnFacingAtBirth()
    {
        var facingLeft = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing direction]", 1)), facingLeft: true);
        Assert.Equal(0f, facingLeft.Homing.Angle);                      // 1 = 右:左朝的对象也不加 180
        Assert.False(facingLeft.Body.Left);                             // 并且把对象的朝向设成右

        var facingRight = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing direction]", 0)));
        Assert.Equal(180f, facingRight.Homing.Angle);                   // 0 = 左:右朝的对象也加 180
        Assert.True(facingRight.Body.Left);

        var two = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing direction]", 2)), facingLeft: true);
        Assert.Equal(0f, two.Homing.Angle);                             // 2 与 3 不是 0:不加 180
        Assert.Null(two.Body.Left);                                     // 也不碰对象的朝向码(这一版只认左右)

        var four = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing direction]", 4)), facingLeft: true);
        Assert.Equal(180f, four.Homing.Angle);                          // 4 不在 0 到 3 里:用对象自己的朝向
        Assert.Null(four.Body.Left);
    }

    [Fact]
    public void TheVelocityVectorUsesTheDepthCompressionAndTheAccelerationNeverTakesTheSpeedBelowZero()
    {
        var sim = new Sim(Def(L("[homing velocity]", 200, 100)));
        sim.Homing.SetHeading(sim.Body, 90f, 100);
        Assert.Equal(0.0, sim.Body.VelocityX, 3);
        Assert.Equal(58.0, sim.Body.VelocityY, 3);                      // 200 × 1 × 0.29
        Assert.Equal(210.0, sim.Homing.Speed, 3);                       // 200 + 100 毫秒 × 100 × 0.001

        var slow = new Sim(Def(L("[homing velocity]", 200, -5000)));
        slow.Homing.SetHeading(slow.Body, 0f, 100);
        Assert.Equal(200.0, slow.Body.VelocityX, 3);                    // 这一步还用旧速度
        Assert.Equal(0.0, slow.Homing.Speed, 3);                        // 200 - 500 夹到 0
        slow.Homing.SetHeading(slow.Body, 0f, 100);
        Assert.Equal(0.0, slow.Body.VelocityX, 3);
    }

    [Fact]
    public void ADegreeBeyondAFullTurnWrapsAndTheFractionIsDropped()
    {
        var sim = new Sim(Def(L("[homing velocity]", 100, 0)));
        sim.Homing.SetHeading(sim.Body, 450.9f, 0);                     // 450 → 90
        Assert.Equal(0.0, sim.Body.VelocityX, 3);
        Assert.Equal(29.0, sim.Body.VelocityY, 3);
        sim.Homing.SetHeading(sim.Body, -90f, 0);                       // -90 → 270
        Assert.Equal(-29.0, sim.Body.VelocityY, 3);
    }

    // ------------------------------------------------------------------ 没有 [max rotation]:检查时一步指过去

    [Fact]
    public void WithoutAMaxRotationTheObjectReaimsOnlyAtEachCheck()
    {
        var sim = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 100), L("[init rotation]", 45f)));
        sim.Aim(100, 0);
        sim.Step(10);                                                   // 年龄 10 > 0:第一步就检查
        Assert.Equal(1, sim.Homing.Checks);
        Assert.Equal(100.0, sim.Body.VelocityX, 3);
        Assert.Equal(0.0, sim.Body.VelocityY, 3);

        sim.Aim(0, 29);
        sim.Step(10);                                                   // 年龄 20:下一次检查在 10 + 100 = 110 之后,目标挪了也不管
        Assert.Equal(1, sim.Homing.Checks);
        Assert.Equal(100.0, sim.Body.VelocityX, 3);

        for (int i = 0; i < 9; i++)
        {
            sim.Step(10);                                               // 年龄 30 ... 110:110 > 110 不成立
        }
        Assert.Equal(1, sim.Homing.Checks);
        sim.Step(10);                                                   // 年龄 120:检查
        Assert.Equal(2, sim.Homing.Checks);
        Assert.Equal(0.0, sim.Body.VelocityX, 3);
        Assert.Equal(29.0, sim.Body.VelocityY, 3);
        Assert.Equal(90f, sim.Homing.Angle);
    }

    [Fact]
    public void TheCheckGapDefaultsToHalfASecond()
    {
        var sim = new Sim(Def(L("[homing velocity]", 100, 0)));
        sim.Aim(100, 0);
        sim.Step(10);                                                   // 年龄 10:第一次检查,下一次要年龄大于 10 + 500
        Assert.Equal(1, sim.Homing.Checks);
        for (int i = 0; i < 50; i++)
        {
            sim.Step(10);                                               // 年龄 20 ... 510:510 > 510 不成立
        }
        Assert.Equal(1, sim.Homing.Checks);
        sim.Step(10);                                                   // 年龄 520
        Assert.Equal(2, sim.Homing.Checks);
    }

    [Fact]
    public void ADeadMissingOrSwitchedOffControllerDoesNotSteer()
    {
        var none = new Sim(Def(L("[homing velocity]", 100, 0)), noTarget: true);
        none.Aim(0, 29);
        none.Step(10);
        Assert.Equal((0, 100.0), (none.Homing.Checks, none.Body.VelocityX));

        var dead = new Sim(Def(L("[homing velocity]", 100, 0)));
        dead.Aim(0, 29);
        dead.Target.IsAlive = false;
        dead.Step(10);
        Assert.Equal((0, 100.0), (dead.Homing.Checks, dead.Body.VelocityX));

        var off = new Sim(Def(L("[homing velocity]", 100, 0)));
        off.Aim(0, 29);
        off.Homing.SetOn(false);
        off.Step(10);
        Assert.Equal((0, 100.0), (off.Homing.Checks, off.Body.VelocityX));
        off.Homing.SetOn(true);
        off.Step(10);
        Assert.Equal(1, off.Homing.Checks);
        Assert.Equal(0.0, off.Body.VelocityX, 3);                       // 重新打开就接着追
    }

    // ------------------------------------------------------------------ 有 [max rotation]:每帧转一点

    [Fact]
    public void WithAMaxRotationTheObjectTurnsTowardTheRecordedTargetByTheRatePerFrame()
    {
        var sim = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 100), L("[max rotation]", 90f)));
        sim.Aim(0, 29);                                                 // 目标在正上方:90 度
        sim.Step(100);                                                  // 每帧最多转 100 毫秒 × 90 度每秒 = 9 度
        Assert.Equal(9f, sim.Homing.Angle);
        Assert.Equal(98.7688, sim.Body.VelocityX, 3);                   // 100 × cos 9
        Assert.Equal(4.5366, sim.Body.VelocityY, 3);                    // 100 × sin 9 × 0.29
        for (int i = 2; i <= 10; i++)
        {
            sim.Step(100);
            Assert.Equal(9f * i, sim.Homing.Angle);
        }
        sim.Step(100);
        Assert.Equal(90f, sim.Homing.Angle);                            // 到了就停在目标方向上
        Assert.Equal(29.0, sim.Body.VelocityY, 3);
    }

    [Fact]
    public void TheObjectTurnsTheShortWayRoundAndWrapsPastZeroAndFullTurn()
    {
        var clockwise = new Sim(Def(L("[homing velocity]", 100, 0), L("[max rotation]", 90f)));
        clockwise.Aim(0, -29);                                          // 270 度:从 0 度往回转比往前转近
        clockwise.Step(100);
        Assert.Equal(351f, clockwise.Homing.Angle);                     // 0 - 9 低于 0 就加 360
        clockwise.Step(100);
        Assert.Equal(342f, clockwise.Homing.Angle);

        var counter = new Sim(Def(L("[homing velocity]", 100, 0), L("[max rotation]", 90f), L("[init rotation]", 355f)));
        counter.Aim(100, 5);                                            // 9 度:从 355 度往前转
        counter.Step(100);
        Assert.Equal(4f, counter.Homing.Angle);                         // 355 + 9 = 364 超过 360 就减 360
        counter.Step(100);
        Assert.Equal(9f, counter.Homing.Angle);                         // 差 5 度不到 9:直接指过去

        var toZero = new Sim(Def(L("[homing velocity]", 100, 0), L("[max rotation]", 90f), L("[init rotation]", 9f)));
        toZero.Aim(0, -29);                                             // 270 度:从 9 度往回转 9 度正好是 0,不是负数就不加 360
        toZero.Step(100);
        Assert.Equal(0f, toZero.Homing.Angle);
    }

    [Fact]
    public void ATargetExactlyBehindIsOutsideTheDefaultCone()
    {
        var behind = new Sim(Def(L("[homing velocity]", 100, 0), L("[max rotation]", 90f)));
        behind.Aim(-100, 0);                                            // 差正好 180 度,[diff rotation] 缺省 180:差大于等于它就不追
        for (int i = 0; i < 5; i++)
        {
            behind.Step(100);
        }
        Assert.Equal(0f, behind.Homing.Angle);
        Assert.Equal(100.0, behind.Body.VelocityX, 3);

        var almost = new Sim(Def(L("[homing velocity]", 100, 0), L("[max rotation]", 90f)));
        almost.Aim(-100, 1);                                            // 178 度:在锥里,转
        almost.Step(100);
        Assert.Equal(9f, almost.Homing.Angle);

        // 锥放到 360 度:差正好 180 度时按逆时针转(差大于 180 才改顺时针),差正好 -180 度时同样逆时针(差小于等于 -180 就走逆时针)
        var wide = new Sim(Def(L("[homing velocity]", 100, 0), L("[max rotation]", 90f), L("[diff rotation]", 360f)));
        wide.Aim(-100, 0);
        wide.Step(100);
        Assert.Equal(9f, wide.Homing.Angle);

        var mirrored = new Sim(Def(L("[homing velocity]", 100, 0), L("[max rotation]", 90f), L("[diff rotation]", 360f), L("[init rotation]", 180f)));
        mirrored.Aim(100, 0);
        mirrored.Step(100);
        Assert.Equal(189f, mirrored.Homing.Angle);
    }

    [Fact]
    public void ATargetOutsideTheDiffRotationConeKeepsTheCurrentWholeDegreeDirection()
    {
        var out90 = new Sim(Def(L("[homing velocity]", 100, 0), L("[max rotation]", 90f), L("[diff rotation]", 60f), L("[init rotation]", 20.5f)));
        out90.Aim(0, 29);                                               // 90 度:差 70 ≥ 60,不转
        int steers = out90.Homing.Steers;
        out90.Step(100);
        Assert.Equal(20f, out90.Homing.Angle);                          // 方向取成整度(20.5 → 20),速度向量照常重设
        Assert.Equal(steers + 1, out90.Homing.Steers);
        Assert.Equal(93.9693, out90.Body.VelocityX, 3);                 // 100 × cos 20

        var inCone = new Sim(Def(L("[homing velocity]", 100, 0), L("[max rotation]", 90f), L("[diff rotation]", 60f)));
        inCone.Aim(100, 29);                                            // 45 度:差 45 < 60,转
        inCone.Step(100);
        Assert.Equal(9f, inCone.Homing.Angle);
    }

    [Fact]
    public void TheObjectSnapsToTheTargetDirectionWhenTheRemainingGapIsSmallerThanTheStep()
    {
        var sim = new Sim(Def(L("[homing velocity]", 100, 0), L("[max rotation]", 90f), L("[init rotation]", 85f)));
        sim.Aim(0, 29);
        sim.Step(100);
        Assert.Equal(90f, sim.Homing.Angle);                            // 差 5 度,一帧能转 9 度:不多转,直接指过去

        var wide = new Sim(Def(L("[homing velocity]", 100, 0), L("[max rotation]", 90f), L("[init rotation]", 85f)));
        wide.Aim(0, 29);
        wide.Step(10);                                                  // 一帧只能转 0.9 度
        Assert.Equal(85.9f, wide.Homing.Angle, 3);
    }

    [Fact]
    public void AnUnsetAngleIsRecoveredFromTheCurrentVelocityAndAFullTurnIsNotWrapped()
    {
        // 初始方向 -10(小于 0 当作没设过):按当前速度反推(速度整数截断后是 (98, -5),反推出 351 度),再往 0 度转 9 度到 360,不超过 360 就不减
        var sim = new Sim(Def(L("[homing velocity]", 100, 0), L("[max rotation]", 90f), L("[init rotation]", -10f)));
        Assert.Equal(-10f, sim.Homing.Angle);
        sim.Aim(100, 0);
        sim.Step(100);
        Assert.Equal(360f, sim.Homing.Angle);
        Assert.Equal(100.0, sim.Body.VelocityX, 3);
        Assert.Equal(0.0, sim.Body.VelocityY, 3);
    }

    // ------------------------------------------------------------------ 加速度

    [Fact]
    public void AccelerationOnlyChangesTheSpeedWhereverTheHeadingIsSet()
    {
        var instant = new Sim(Def(L("[homing velocity]", 100, 50), L("[homing check gap]", 100)));
        instant.Aim(100, 0);
        Assert.Equal(100.0, instant.Homing.Speed, 3);                   // 出生时 dt 是 0
        instant.Step(100);                                              // 年龄 100:检查,用旧速度 100,然后加到 105
        Assert.Equal(100.0, instant.Body.VelocityX, 3);
        Assert.Equal(105.0, instant.Homing.Speed, 3);
        instant.Step(100);                                              // 年龄 200:不检查,速度不变
        Assert.Equal(105.0, instant.Homing.Speed, 3);
        instant.Step(100);                                              // 年龄 300:检查
        Assert.Equal(105.0, instant.Body.VelocityX, 3);
        Assert.Equal(110.0, instant.Homing.Speed, 3);

        var rotating = new Sim(Def(L("[homing velocity]", 100, 50), L("[homing check gap]", 100), L("[max rotation]", 90f)));
        rotating.Aim(100, 0);
        rotating.Step(100);
        rotating.Step(100);
        rotating.Step(100);
        Assert.Equal(115.0, rotating.Homing.Speed, 3);                  // 有 [max rotation] 每帧都定方向:每帧加一次
    }

    // ------------------------------------------------------------------ 时限与直线瞄准

    [Fact]
    public void HomingEndsAfterTheHomingTimeButTheStepThatExpiresItStillRunsItsCheck()
    {
        var sim = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 100), L("[homing time]", 250)));
        sim.Aim(100, 0);
        sim.Step(100);                                                  // 年龄 100:检查
        sim.Step(100);                                                  // 年龄 200:不检查
        Assert.True(sim.Homing.On);
        sim.Aim(0, 29);
        sim.Step(100);                                                  // 年龄 300 > 0 + 250:关掉,但这一步的检查照做
        Assert.False(sim.Homing.On);
        Assert.Equal(2, sim.Homing.Checks);
        Assert.Equal(29.0, sim.Body.VelocityY, 3);

        sim.Aim(-100, 0);
        sim.Step(100);                                                  // 已经关了:速度保持
        Assert.Equal(2, sim.Homing.Checks);
        Assert.Equal(29.0, sim.Body.VelocityY, 3);
    }

    [Fact]
    public void TheHomingTimeIsExclusiveAndCountsFromTheMomentTheControllerStarted()
    {
        var body = new Body();
        var target = new Actor();
        var homing = new PassiveHoming(Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 50), L("[homing time]", 300)));
        homing.Start(body, false, 1000, target);                        // 对象 1000 毫秒大才开始追
        homing.Step(body, 50, 1050);
        homing.Step(body, 250, 1300);                                   // 1300 > 1000 + 300 不成立:还追着
        Assert.True(homing.On);
        homing.Step(body, 50, 1350);                                    // 1350 > 1300:关
        Assert.False(homing.On);
    }

    [Fact]
    public void ATimeOfZeroNeverExpires()
    {
        var sim = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 100)));
        sim.Aim(100, 0);
        for (int i = 0; i < 100; i++)
        {
            sim.Step(100);
        }
        Assert.True(sim.Homing.On);
    }

    [Fact]
    public void StraightHomingAimsOnceAlongTheThreeDimensionalVectorAtTheCurrentSpeedAndThenSwitchesOff()
    {
        var sim = new Sim(Def(L("[homing velocity]", 100, 0), L("[straight homing]")));
        sim.Aim(30, 0, 40);                                             // 长度 50:单位向量 (0.6, 0, 0.8) × 100
        sim.Step(10);
        Assert.Equal(60.0, sim.Body.VelocityX, 3);
        Assert.Equal(0.0, sim.Body.VelocityY, 3);                       // 没有纵深压缩,也不过 sin/cos 表
        Assert.Equal(80.0, sim.Body.VelocityZ, 3);
        Assert.False(sim.Homing.On);
        Assert.Equal(0, sim.Homing.Checks);

        sim.Aim(0, 29, 0);
        sim.Step(10);                                                   // 关了:不再瞄
        Assert.Equal(60.0, sim.Body.VelocityX, 3);

        var onTop = new Sim(Def(L("[homing velocity]", 100, 0), L("[straight homing]")));
        onTop.Aim(0, 0, 0);                                             // 正好压在目标上:没有方向可指(客户端会算出非数,这里偏离原版保持速度)
        onTop.Step(10);
        Assert.Equal(100.0, onTop.Body.VelocityX, 3);
        Assert.False(onTop.Homing.On);
    }

    // ------------------------------------------------------------------ 平移、朝向、瞄准点、画面旋转

    [Fact]
    public void SyncTargetMoveCarriesTheObjectByTheTargetsMovementBetweenChecks()
    {
        HomingDef def = Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 100), L("[max rotation]", 90f), L("[sync target move]"));
        var sim = new Sim(def);
        sim.Aim(100, 0);
        sim.Step(100);                                                  // 第一次检查:没有上一次的位置,不平移
        Assert.Equal(0, sim.Body.Moves);
        sim.Aim(130, 0);
        sim.Step(100);                                                  // 年龄 200:不检查
        sim.Step(100);                                                  // 年龄 300:检查,目标走了 (30, 0)
        Assert.Equal(1, sim.Body.Moves);
        Assert.Equal((30.0, 0.0), (sim.Body.X, sim.Body.Y));

        var yOnly = new Sim(def);
        yOnly.Aim(0, 20);                                               // 上一次位置 X 为 0、Y 不为 0 也算有上一次位置
        yOnly.Step(100);
        yOnly.Aim(0, 15);
        yOnly.Step(100);
        yOnly.Step(100);
        Assert.Equal(1, yOnly.Body.Moves);
        Assert.Equal((0.0, -5.0), (yOnly.Body.X, yOnly.Body.Y));

        var origin = new Sim(def);
        origin.Aim(0, 0);                                               // 上一次位置是 (0, 0):和没有一样,不平移
        origin.Step(100);
        origin.Aim(10, 10);
        origin.Step(100);
        origin.Step(100);
        Assert.Equal(0, origin.Body.Moves);

        var without = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 100), L("[max rotation]", 90f)));
        without.Aim(100, 0);
        without.Step(100);
        without.Aim(130, 0);
        without.Step(100);
        without.Step(100);
        Assert.Equal(0, without.Body.Moves);                            // 没有 [sync target move] 不平移
    }

    [Fact]
    public void TheFacingFollowsTheTargetOnlyWhenNeitherTheSpriteRotationNorAHomingDirectionOwnsIt()
    {
        var sim = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 100)));
        sim.Aim(100, 0);
        sim.Step(100);
        Assert.False(sim.Body.Left);                                    // 对象在目标左边(0 < 100):朝右

        sim.Body.X = 100;
        sim.Step(100);                                                  // 年龄 200:不检查
        sim.Step(100);                                                  // 年龄 300:x 相等 → 朝左(只有严格小于才朝右)
        Assert.True(sim.Body.Left);

        sim.Body.X = 99.9;                                              // 位置先取整数:99 < 100
        sim.Step(100);
        sim.Step(100);
        sim.Step(100);
        Assert.False(sim.Body.Left);

        var turning = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 100), L("[sync animation rotation]", 1)));
        turning.Aim(100, 0);
        turning.Step(100);
        Assert.Equal(0, turning.Body.FacingCalls);

        var directed = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 100), L("[homing direction]", 4)));
        directed.Aim(100, 0);
        directed.Step(100);
        Assert.Equal(0, directed.Body.FacingCalls);                     // 只有 -1 才改朝向
    }

    [Fact]
    public void AHomingDirectionOfZeroOrOneAimsAtAPointBesideTheTargetByTheKeepDistance()
    {
        var plain = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 100), L("[homing keep distance]", 29)));
        plain.Aim(0, 29);
        plain.Step(100);
        Assert.Equal(90f, plain.Homing.Angle);                          // 朝向码是 -1:偏移不用

        var plus = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 100), L("[homing keep distance]", 29), L("[homing direction]", 0)));
        plus.Aim(0, 29);
        plus.Step(100);
        Assert.Equal(73f, plus.Homing.Angle);                           // 瞄准点 (0 + 29, 29)

        var minus = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 100), L("[homing keep distance]", 29), L("[homing direction]", 1)));
        minus.Aim(0, 29);
        minus.Step(100);
        Assert.Equal(106f, minus.Homing.Angle);                         // 瞄准点 (0 - 29, 29)

        var smooth = new Sim(Def(L("[homing velocity]", 100, 0), L("[homing check gap]", 100), L("[homing keep distance]", 29), L("[homing direction]", 0), L("[max rotation]", 3600f)));
        smooth.Aim(0, 29);
        smooth.Step(100);
        Assert.Equal(73f, smooth.Homing.Angle);                         // 转的时候也朝同一个瞄准点(一帧转得够,直接指过去)
    }

    [Fact]
    public void ASpriteRotatingObjectReportsItsRotationInRadiansFromThePlusHalfTurnWholeDegree()
    {
        var sim = new Sim(Def(L("[homing velocity]", 100, 0), L("[init rotation]", 90f), L("[sync animation rotation]", 1)));
        Assert.Equal(1.5f * 3.14f, sim.Body.Rotation, 3);               // (90 + 180) % 360 = 270,270 / 180 × 3.14
        sim.Homing.SetHeading(sim.Body, 0f, 0);
        Assert.Equal(3.14f, sim.Body.Rotation, 3);
        sim.Homing.SetHeading(sim.Body, 350f, 0);
        Assert.Equal(170f / 180f * 3.14f, sim.Body.Rotation, 3);        // (350 + 180) % 360 = 170

        var plain = new Sim(Def(L("[homing velocity]", 100, 0), L("[init rotation]", 90f)));
        Assert.Equal(0f, plain.Body.Rotation);                          // 没有 [sync animation rotation] 不转画面
    }
}
