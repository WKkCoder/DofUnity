using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 被动对象的追踪(M67,证据 E-165):出生时按创建者取追踪对象([ME] 创建者、[ENEMY] 创建者盯着的目标、没有就取主角、[MONSTER] 编号取场上的怪物)、
/// 每帧先按旧速度走一步再转向、[SET USE HOMING] 开关、[homing use] 0 的对象不追、追踪的动画旋转。数据全是合成的(见 <see cref="AvatarFixture.ActHomeInstantId"/> 起的几个对象):
/// 追踪速度 200、检查间隔 100 毫秒;转向用的数在 <see cref="PassiveHomingTests"/> 里逐条验过,这里只看对象和宿主接起来的样子。
/// </summary>
public partial class PassiveObjectTests
{
    /// <summary>一个会动的"目标":种类、编号、位置与死活都能随时改。</summary>
    private sealed class Mark : IActEntity
    {
        public ActEntityKind Kind { get; set; } = ActEntityKind.Character;
        public int ObjectIndex { get; set; }
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

    private static Mark Hero(float x, float y, float z = 0) => new Mark { X = x, Y = y, Z = z };

    private static Mark Monster(int code, float x, float y, bool alive = true) => new Mark { Kind = ActEntityKind.Monster, ObjectIndex = code, X = x, Y = y, IsAlive = alive };

    // ------------------------------------------------------------------ 取追踪对象

    [Fact]
    public void AnEnemyHomingObjectTakesTheTargetOfItsCreator()
    {
        using var rig = new Rig();
        Mark hero = Hero(300, 100);
        rig.Owner.ActTarget = hero;
        rig.Host.Characters.Add(Hero(0, 0));                           // 宿主的主角在别处:创建者盯着的目标优先

        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeInstantId, facingLeft: false, x: 100, y: 50);
        Assert.NotNull(o.Homing);
        Assert.True(o.Homing.On);
        Assert.Same(hero, o.Homing.Target);
    }

    [Fact]
    public void AnEnemyHomingObjectFallsBackToTheFirstLiveHeroWhenTheCreatorHasNoLiveTarget()
    {
        using var rig = new Rig();
        Mark far = Hero(900, 100);
        Mark near = Hero(300, 100);
        Mark dead = Hero(5, 5);
        dead.IsAlive = false;
        rig.Host.Characters.Add(dead);
        rig.Host.Characters.Add(far);
        rig.Host.Characters.Add(near);

        PassiveObject noTarget = rig.Spawn(AvatarFixture.ActHomeInstantId);
        Assert.Same(far, noTarget.Homing.Target);                      // 死的跳过,取第一个活着的

        rig.Owner.ActTarget = dead;                                    // 创建者盯着的目标死了:同样走兜底
        PassiveObject deadTarget = rig.Spawn(AvatarFixture.ActHomeInstantId);
        Assert.Same(far, deadTarget.Homing.Target);
    }

    [Fact]
    public void AnObjectWithNobodyToChaseKeepsFlyingStraightAtItsBirthHeading()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeInstantId, facingLeft: false, x: 100, y: 50);
        Assert.Null(o.Homing.Target);
        Advance(o, 100);
        Assert.Equal(120.0, o.X, 6);                                   // 速度 200 × 0.1 秒
        Assert.Equal(50.0, o.Y, 6);
        Assert.Equal(0, o.Homing.Checks);
    }

    [Fact]
    public void AMeHomingObjectChasesItsDirectCreatorAndTheCreatorCanBeAnyObject()
    {
        using var rig = new Rig();
        PassiveObject byDefault = rig.Spawn(AvatarFixture.ActHomeMeId);
        Assert.Same(rig.Owner.ActSelf, byDefault.Homing.Target);        // 创建者就是放它的怪物

        Mark parent = Hero(50, 50);
        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeMeId, facingLeft: false, x: 100, y: 50, master: parent);
        Assert.Same(parent, o.Homing.Target);                          // 被别的对象放出来的:追放它的那个
        Advance(o, 10);                                                // 先走到 102,再朝 (50, 50) 定方向:180 度
        Assert.Equal(102.0, o.X, 6);
        Assert.True(o.FacingLeft);
        Advance(o, 10);
        Assert.Equal(100.0, o.X, 6);                                   // 往回飞 200 × 0.01
    }

    [Fact]
    public void AMonsterHomingObjectChasesTheFirstLiveMonsterOfThatCode()
    {
        using var rig = new Rig();
        Mark other = Monster(5, 10, 10);                               // 别的编号
        Mark fallen = Monster(AvatarFixture.HomeMonsterCode, 20, 20, alive: false);
        Mark decoy = Hero(30, 30);                                     // 编号碰巧一样,但不是怪物:不认
        decoy.ObjectIndex = AvatarFixture.HomeMonsterCode;
        Mark wanted = Monster(AvatarFixture.HomeMonsterCode, 300, 50);
        Mark second = Monster(AvatarFixture.HomeMonsterCode, 400, 50);
        rig.Host.Monsters.AddRange(new[] { other, fallen, decoy, wanted, second });

        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeMonsterId, facingLeft: false, x: 100, y: 50);
        Assert.Same(wanted, o.Homing.Target);                          // 编号对、活着、种类是怪物的第一个

        using var none = new Rig();
        none.Host.Monsters.Add(Monster(5, 300, 50));
        Mark sameNumber = Hero(300, 50);
        sameNumber.ObjectIndex = AvatarFixture.HomeMonsterCode;
        none.Host.Monsters.Add(sameNumber);
        Assert.Null(none.Spawn(AvatarFixture.ActHomeMonsterId).Homing.Target);        // 场上没有这个编号的怪物:不追
    }

    [Fact]
    public void ACharacterHomingObjectChasesTheFirstLiveHeroWhateverTheCreatorTargets()
    {
        using var rig = new Rig();
        Mark creatorsTarget = Hero(900, 100);
        Mark dead = Hero(5, 5);
        dead.IsAlive = false;
        Mark alive = Hero(300, 100);
        rig.Owner.ActTarget = creatorsTarget;
        rig.Host.Characters.AddRange(new[] { dead, alive });
        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeCharId);
        Assert.Same(alive, o.Homing.Target);
    }

    // ------------------------------------------------------------------ 追的样子

    [Fact]
    public void TheObjectWalksOnItsOldVelocityFirstAndThenAimsAtTheTarget()
    {
        using var rig = new Rig();
        rig.Owner.ActTarget = Hero(300, 100);
        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeInstantId, facingLeft: false, x: 100, y: 50);
        Advance(o, 10);
        Assert.Equal(102.0, o.X, 6);                                   // 出生方向 0 度、速度 200:先走 2 像素
        Assert.Equal(50.0, o.Y, 6);
        Assert.Equal(1, o.Homing.Checks);
        Assert.Equal(41f, o.Homing.Angle);                             // 然后才从 (102, 50) 指向 (300, 100):41 度

        Advance(o, 10);
        Assert.Equal(102.0 + 1.509419, o.X, 3);                        // 200 × cos 41 × 0.01
        Assert.Equal(50.0 + 0.380514, o.Y, 3);                         // 200 × sin 41 × 0.29 × 0.01
        Assert.False(o.FacingLeft);                                    // 对象在目标左边:朝右
    }

    [Fact]
    public void TheObjectFacesTheTargetSideWhenNothingElseOwnsTheFacing()
    {
        using var rig = new Rig();
        rig.Owner.ActTarget = Hero(0, 50);
        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeInstantId, facingLeft: false, x: 100, y: 50);
        Assert.False(o.FacingLeft);
        Advance(o, 10);
        Assert.True(o.FacingLeft);                                     // 目标在左边:转过去

        rig.Owner.ActTarget = Hero(500, 50);
        PassiveObject chaser = rig.Spawn(AvatarFixture.ActHomeInstantId, facingLeft: true, x: 100, y: 50);
        Assert.True(chaser.FacingLeft);
        Advance(chaser, 10);
        Assert.False(chaser.FacingLeft);                               // 目标在右边:转回来
    }

    [Fact]
    public void ASmoothObjectTurnsTheMaxRotationRateTowardTheTargetEachFrame()
    {
        using var rig = new Rig();
        rig.Owner.ActTarget = Hero(300, 100);
        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeSmoothId, facingLeft: false, x: 100, y: 50);
        Advance(o, 110);                                               // 11 帧,每帧最多转 10 毫秒 × 90 度每秒 = 0.9 度
        Assert.Equal(9.9, o.Homing.Angle, 2);
        Assert.Equal(1, o.Homing.Checks);
    }

    [Fact]
    public void AStraightObjectAimsOnceWithoutTheDepthCompressionAndThenFliesOn()
    {
        using var rig = new Rig();
        rig.Owner.ActTarget = Hero(131, 90);
        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeStraightId, facingLeft: false, x: 100, y: 50);
        Advance(o, 10);                                                // 先按出生速度 100 走到 101,再朝 (131, 90) 瞄:向量 (30, 40) 长 50
        Assert.Equal(101.0, o.X, 6);
        Assert.False(o.Homing.On);
        Advance(o, 10);
        Assert.Equal(101.6, o.X, 6);                                   // 速度 (60, 80):没有 Y 的 0.29 压缩
        Assert.Equal(50.8, o.Y, 6);
        Advance(o, 100);
        Assert.Equal(101.6 + 6.0, o.X, 6);
        Assert.Equal(50.8 + 8.0, o.Y, 6);
    }

    [Fact]
    public void AStraightObjectAimsUpAtATargetAboveItAndRisesLikeAThrownObject()
    {
        using var rig = new Rig();
        rig.Owner.ActTarget = Hero(101, 50, 40);
        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeStraightId, facingLeft: false, x: 100, y: 50);
        Advance(o, 10);                                                // 走到 (101, 50),再瞄 (0, 0, 40):垂直向上 100
        Assert.Equal(0.0, o.Z, 6);
        Advance(o, 10);
        Assert.Equal(1.0, o.Z, 6);
        Assert.Equal(101.0, o.X, 6);
    }

    [Fact]
    public void ASyncTargetMoveObjectIsCarriedByTheTargetsMovementAtEachCheck()
    {
        using var rig = new Rig();
        Mark hero = Hero(300, 100);
        rig.Owner.ActTarget = hero;
        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeCarryId, facingLeft: false, x: 100, y: 50);
        Advance(o, 10);                                                // 第一次检查:记下目标位置,不平移
        Assert.Equal(1, o.Homing.Checks);
        hero.X = 320;
        hero.Y = 90;
        Advance(o, 90);                                                // 年龄 100:还没到下一次检查(110 之后);转向仍朝记下的 (300, 100)
        Assert.Equal(1, o.Homing.Checks);
        Advance(o, 20);                                                // 年龄 110 不检查,120 检查:目标走了 (+20, -10)
        Assert.Equal(2, o.Homing.Checks);
        Assert.Equal(143.0, o.X, 6);                                   // 自己先飞到约 123.9,取整成 123 再加 20
        Assert.Equal(40.0, o.Y, 6);                                    // 自己飞到约 50.4,取整成 50 再减 10
    }

    [Fact]
    public void ADeadTargetStopsTheSteeringAndTheObjectKeepsItsLastVelocity()
    {
        using var rig = new Rig();
        Mark hero = Hero(300, 100);
        rig.Owner.ActTarget = hero;
        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeInstantId, facingLeft: false, x: 100, y: 50);
        Advance(o, 10);
        hero.IsAlive = false;
        hero.X = 0;
        Advance(o, 200);
        Assert.Equal(102.0 + 20 * 1.509419, o.X, 3);
        Assert.Equal(50.0 + 20 * 0.380514, o.Y, 3);
        Assert.Equal(1, o.Homing.Checks);
    }

    // ------------------------------------------------------------------ 开关与不追的对象

    [Fact]
    public void AnObjectWhoseHomingUseIsZeroNeverHomesAndTheSwitchCannotTurnItOn()
    {
        using var rig = new Rig();
        rig.Owner.ActTarget = Hero(300, 100);
        PassiveObject idle = rig.Spawn(AvatarFixture.ActHomeIdleId, facingLeft: false, x: 100, y: 50);
        Assert.Null(idle.Homing);
        Advance(idle, 100);
        Assert.Equal(100.0, idle.X, 6);                                // 没有 [SET SPEED]:不动

        PassiveObject switched = rig.Spawn(AvatarFixture.ActHomeOnId, facingLeft: false, x: 100, y: 50);
        Assert.Null(switched.Homing);                                  // [SET USE HOMING] 1 也没有控制器可开
        Advance(switched, 100);
        Assert.Equal(100.0, switched.X, 6);
        Assert.Equal(50.0, switched.Y, 6);
    }

    [Fact]
    public void ASwitchOffInTheScriptStopsTheObjectFromReaimingAndTheInterfaceTurnsItBackOn()
    {
        using var rig = new Rig();
        rig.Owner.ActTarget = Hero(300, 100);
        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeOffId, facingLeft: false, x: 100, y: 50);
        Assert.True(o.Homing.On);
        Advance(o, 100);                                               // 第一帧的 [ON SET ACTION] 就把追踪关了
        Assert.False(o.Homing.On);
        Assert.Equal(0, o.Homing.Checks);
        Assert.Equal(120.0, o.X, 6);                                   // 一直走出生方向
        Assert.Equal(50.0, o.Y, 6);

        ((IActEntity)o).SetUseHoming(true);
        Advance(o, 10);
        Assert.True(o.Homing.On);
        Assert.Equal(1, o.Homing.Checks);
        Assert.Equal(44f, o.Homing.Angle);                             // 重新开了:先走到 (122, 50),再指向 (300, 100)
    }

    [Fact]
    public void TheBodyInterfaceMovesTurnsAndRotatesTheObject()
    {
        using var rig = new Rig();
        PassiveObject o = rig.Spawn(AvatarFixture.ActHomeIdleId, facingLeft: false, x: 100, y: 50, z: 0);
        IHomingBody body = o;
        Assert.Equal((100.0, 50.0, 0.0), (body.X, body.Y, body.Z));
        body.SetPlanarVelocity(100, 50);
        Assert.Equal((100.0, 50.0), (body.VelocityX, body.VelocityY));
        Advance(o, 10);
        Assert.Equal(101.0, o.X, 6);
        Assert.Equal(50.5, o.Y, 6);
        body.MoveTo(5, 6);
        Assert.Equal((5.0, 6.0), (o.X, o.Y));
        Assert.Equal(0.0, o.Z);
        body.SetHomingFacing(true);
        Assert.True(o.FacingLeft);
        body.SetHomingFacing(false);
        Assert.False(o.FacingLeft);
        body.SetRotation(1.25f);
        Assert.Equal(1.25f, o.RotationRadians);
    }

    // ------------------------------------------------------------------ 画面旋转

    [Fact]
    public void ASpriteRotatingObjectKeepsItsRotationAndItsFacingStaysAsBorn()
    {
        using var rig = new Rig();
        rig.Owner.ActTarget = Hero(300, 100);
        PassiveObject right = rig.Spawn(AvatarFixture.ActHomeTurnId, facingLeft: false, x: 100, y: 50);
        Assert.Equal(1.5 * 3.14, right.RotationRadians, 3);            // 初始 90 度,((90 + 180) % 360) / 180 × 3.14
        PassiveObject left = rig.Spawn(AvatarFixture.ActHomeTurnId, facingLeft: true, x: 100, y: 50);
        Assert.Equal(0.5 * 3.14, left.RotationRadians, 3);             // 朝左再加 180 度:270,((270 + 180) % 360) / 180 × 3.14
        Assert.True(left.FacingLeft);

        Advance(left, 100);
        Assert.True(left.FacingLeft);                                  // 带画面旋转的对象追踪时不改朝向
        Assert.NotEqual(0.5 * 3.14, left.RotationRadians, 3);          // 转着去追,旋转量跟着变

        PassiveObject plain = rig.Spawn(AvatarFixture.ActHomeInstantId, facingLeft: false, x: 100, y: 50);
        Assert.Equal(0f, plain.RotationRadians);
    }

    // ------------------------------------------------------------------ 房间里

    [Fact]
    public void InARoomSessionAHomingObjectChasesTheHeroAndComesCloser()
    {
        using var arena = new ActArena("shoot", useVitals: false);
        IActHost host = arena.Session.ActHost;
        host.SpawnPassive(arena.Monster.ActSelf, AvatarFixture.ActHomeInstantId, 0, 100f, 40f, 0f, false);
        PassiveObject o = null;
        foreach (PassiveObject p in arena.Session.Passives)
        {
            if (p.Template.Index == AvatarFixture.ActHomeInstantId)
            {
                o = p;
            }
        }
        Assert.NotNull(o);
        Assert.NotNull(o.Homing.Target);
        Assert.Same(arena.Session.HeroEntity, o.Homing.Target);        // 房间会话把主角当"主角"类给出
        Assert.Equal((float)HeroX, o.Homing.Target.X);
        Assert.Equal((float)HeroY, o.Homing.Target.Y);
        arena.Steps(50);
        Assert.True(o.X > 100.0);
        Assert.True(o.Y > 40.0);                                       // 离主角站的那条线(y = 100)近了
        Assert.True(o.Y < HeroY + 1.0);
        Assert.True(o.Homing.Checks >= 1);
    }
}
