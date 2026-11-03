using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [SET USE HOMING] n(M67,证据 E-165;客户端行为码 0xdf):值非 0 开、0 关目标对象身上的追踪,由对象自己落实(没有追踪的对象默认什么都不做);
/// 以前它在"没做的行为"里,现在不再通报。脚本内容全是合成的。
/// </summary>
public sealed partial class ActRuntimeTests
{
    [Fact]
    public void SetUseHomingPassesTheValueAsAFlagToEveryTarget()
    {
        var on = new Entity();
        Run(P(Me(L("[SET USE HOMING]", 1))), on);
        Assert.Equal(new[] { "homing:True" }, on.Log.ToArray());

        var off = new Entity();
        Run(P(Me(L("[SET USE HOMING]", 0))), off);
        Assert.Equal(new[] { "homing:False" }, off.Log.ToArray());

        var other = new Entity();
        Run(P(Me(L("[SET USE HOMING]", 2))), other);
        Assert.Equal(new[] { "homing:True" }, other.Log.ToArray());      // 只看是不是 0

        var owner = new Entity();
        var a = new Entity { Kind = ActEntityKind.Passive };
        var b = new Entity { Kind = ActEntityKind.Passive };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b };
        Run(P(OnEnemies(L("[SET USE HOMING]", 0))), owner, host);
        Assert.Equal(new[] { "homing:False" }, a.Log.ToArray());
        Assert.Equal(new[] { "homing:False" }, b.Log.ToArray());
        Assert.Empty(owner.Log);
    }

    [Fact]
    public void SetUseHomingIsASupportedBehaviorAndItsNeighboursStillRun()
    {
        ActProgram p = P(Cat(
            Trigger(L("[LIMIT]", 1), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[SET USE HOMING]", 1), L("[SET SPEED]"), L("[X AXIS]", 9), L("[/SET SPEED]"))));
        var host = new CountingHost();
        var e = new Entity();
        new ActRunner(p, e, host).Update(0, 0.01f);

        Assert.Equal(new[] { "homing:True", "speed:9,-,-" }, e.Log.ToArray());
        Assert.Empty(host.Behaviors);                    // 不再记成"没做的行为"
    }

    [Fact]
    public void AnEntityWithoutHomingIgnoresTheSwitchByDefault()
    {
        IActEntity plain = new PlainEntity();
        plain.SetUseHoming(true);                        // 接口的默认实现:什么都不做,不抛异常
        plain.SetUseHoming(false);
    }

    private sealed class PlainEntity : IActEntity
    {
        public ActEntityKind Kind => ActEntityKind.Monster;
        public int ObjectIndex => 0;
        public float X => 0;
        public float Y => 0;
        public float Z => 0;
        public bool FacingLeft => false;
        public bool IsAlive => true;
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
}
