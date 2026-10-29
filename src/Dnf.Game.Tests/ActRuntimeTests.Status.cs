using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [ACTIVE STATUS](M51,证据 E-144):参数读法与 .atk 的状态槽一致(种类、概率、等级、持续时间、按种类定的附加整数),
/// 对目标集合里每个活动对象(主角与怪物)调一次"挂状态",被动对象与死去的目标不挂,源头是行为的拥有者。脚本内容全是合成的,数是测试用的。
/// </summary>
public sealed partial class ActRuntimeTests
{
    private static ScriptValue Fl(float v) => new(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(v), null);

    private static ScriptLine[] Status(string kind, params ScriptValue[] values) => new[] { L("[ACTIVE STATUS]"), new ScriptLine(kind, values) };

    // ------------------------------------------------------------------ 编译

    [Fact]
    public void ActiveStatusReadsTheKindProbabilityLevelDurationAndThePerKindExtraIntegers()
    {
        // 附加整数个数按种类走(与 .atk 同一张表):灼伤四个、减速两个、眩晕没有
        ActBehavior burn = Compiled(Status("[burn]", Fl(80.5f), I(3), I(5000), I(11), I(12), I(13), I(14)));
        Assert.Equal(ActBehaviorKind.ActiveStatus, burn.Kind);
        Assert.Equal((ActiveStatusKind.Burn, 80.5f, 3, 5000), (burn.Status.Kind, burn.Status.Probability, burn.Status.Level, burn.Status.Duration));
        Assert.Equal(new[] { 11, 12, 13, 14 }, burn.Status.Extra);
        Assert.Equal(0, burn.StatusSpill);

        ActBehavior slow = Compiled(Status("[slow]", Fl(100f), I(1), I(2000), I(30), I(40)));
        Assert.Equal(ActiveStatusKind.Slow, slow.Status.Kind);
        Assert.Equal(new[] { 30, 40 }, slow.Status.Extra);

        ActBehavior stun = Compiled(Status("[stun]", Fl(1000f), I(2), I(1500)));
        Assert.Equal((ActiveStatusKind.Stun, 1000f, 2, 1500), (stun.Status.Kind, stun.Status.Probability, stun.Status.Level, stun.Status.Duration));
        Assert.Empty(stun.Status.Extra);
    }

    [Fact]
    public void ActiveStatusKindMayAlsoBeWrittenAsAQuotedString()
    {
        // 客户端读的是"类型记号或字符串"(与 .atk 状态槽的读法一样),真实数据里有一处把种类写成带引号的字符串
        ActBehavior b = Compiled(L("[ACTIVE STATUS]", S("[poison]"), Fl(100f), I(1), I(3000), I(55)));
        Assert.Equal(ActBehaviorKind.ActiveStatus, b.Kind);
        Assert.Equal(ActiveStatusKind.Poison, b.Status.Kind);
        Assert.Equal(new[] { 55 }, b.Status.Extra);
        Assert.Equal(0, b.StatusSpill);
    }

    [Fact]
    public void ActiveStatusProbabilityMayBeWrittenAsAnIntegerAndMissingNumbersAreZero()
    {
        ActBehavior asInt = Compiled(Status("[stun]", I(60), I(2), I(1500)));
        Assert.Equal(60f, asInt.Status.Probability);

        ActBehavior shortLine = Compiled(Status("[stun]", Fl(50f)));
        Assert.Equal(ActBehaviorKind.ActiveStatus, shortLine.Kind);
        Assert.Equal((50f, 0, 0), (shortLine.Status.Probability, shortLine.Status.Level, shortLine.Status.Duration));
        Assert.Equal((0, 2), (shortLine.StatusSpill, shortLine.StatusMissing));          // 少写了等级与持续时间:记在 StatusMissing 里

        // 附加整数不够:缺的当 0,个数仍按种类定
        ActBehavior fewExtras = Compiled(Status("[burn]", Fl(100f), I(1), I(3000), I(7)));
        Assert.Equal(new[] { 7, 0, 0, 0 }, fewExtras.Status.Extra);
        Assert.Equal((0, 3), (fewExtras.StatusSpill, fewExtras.StatusMissing));
    }

    [Fact]
    public void ActiveStatusCountsLeftoverIntegersAndRejectsUnknownKinds()
    {
        // 多写了两个整数:读法与 .atk 对不上的迹象,记在 StatusSpill 里供审计,状态本身照常读出
        ActBehavior spill = Compiled(Status("[stun]", Fl(100f), I(1), I(1000), I(9), I(9)));
        Assert.Equal(ActBehaviorKind.ActiveStatus, spill.Kind);
        Assert.Equal((2, 0), (spill.StatusSpill, spill.StatusMissing));

        // 种类认不出来或没写:读不懂,按空操作处理(客户端的种类分支只到 17)
        Assert.Equal(ActBehaviorKind.Unsupported, Compiled(Status("[not a status]", Fl(100f), I(1), I(1000))).Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, Compiled(L("[ACTIVE STATUS]")).Kind);

        // 后面紧跟着别的行为:状态行不吞它
        ActProgram two = P(Me(Cat(Status("[freeze]", Fl(100f), I(1), I(1000)), new[] { L("[SET HP]", 9) })));
        Assert.Equal(new[] { ActBehaviorKind.ActiveStatus, ActBehaviorKind.SetHp }, two.Behaviors[0].Items.Select(i => i.Kind).ToArray());
    }

    [Fact]
    public void ActiveStatusKindsCoverEveryStatusNameInTheAttackInfoOrder()
    {
        var kinds = new[]
        {
            "[slow]", "[freeze]", "[poison]", "[stun]", "[curse]", "[blind]", "[lightning]", "[stone]", "[sleep]",
            "[burn]", "[weapon break]", "[bleeding]", "[haste]", "[bless]", "[element]", "[confuse]", "[hold]", "[armor break]",
        };
        for (int i = 0; i < kinds.Length; i++)
        {
            // 够多的整数让每个种类的附加整数读满;多出来的会记成 StatusSpill
            ActBehavior b = Compiled(Status(kinds[i], Fl(100f), I(1), I(1000), I(1), I(2), I(3), I(4), I(5), I(6)));
            Assert.Equal(ActBehaviorKind.ActiveStatus, b.Kind);
            Assert.Equal((ActiveStatusKind)i, b.Status.Kind);
            Assert.Equal(6, b.Status.Extra.Length + b.StatusSpill);
            Assert.Equal(0, b.StatusMissing);
        }
    }

    // ------------------------------------------------------------------ 执行

    [Fact]
    public void ActiveStatusAppliesOnceToEveryLivingActiveObjectWithTheOwnerAsTheSource()
    {
        var owner = new Entity();
        var hero = new Entity { Kind = ActEntityKind.Character };
        var other = new Entity { Kind = ActEntityKind.Monster };
        var dead = new Entity { Kind = ActEntityKind.Character, IsAlive = false };
        var passive = new Entity { Kind = ActEntityKind.Passive };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { hero, other, dead, passive };

        Run(P(OnEnemies(Status("[stun]", Fl(100f), I(2), I(1500)))), owner, host);

        Assert.Equal(new[] { "status:Stun" }, hero.Log.ToArray());
        Assert.Equal(new[] { "status:Stun" }, other.Log.ToArray());
        Assert.Empty(dead.Log);                     // 死了的不挂
        Assert.Empty(passive.Log);                  // 被动对象不是"活动对象"(客户端 0x010bbec6 块的类型位检查 0x11),不挂
        Assert.Same(owner, hero.Statuses[0].source);
        Assert.Equal((ActiveStatusKind.Stun, 100f, 2, 1500), (hero.Statuses[0].entry.Kind, hero.Statuses[0].entry.Probability, hero.Statuses[0].entry.Level, hero.Statuses[0].entry.Duration));
    }

    [Fact]
    public void ActiveStatusOnTheOwnerIsASelfBuffAndAnEmptySelectorDoesNothing()
    {
        var owner = new Entity();
        Run(P(Me(Status("[haste]", Fl(100f), I(1), I(3000), I(10), I(20)))), owner);
        Assert.Equal(new[] { "status:Haste" }, owner.Log.ToArray());
        Assert.Same(owner, owner.Statuses[0].source);

        var lonely = new Entity();
        Run(P(OnEnemies(Status("[stun]", Fl(100f), I(1), I(1000)))), lonely);   // 目标集合是空的
        Assert.Empty(lonely.Log);
    }
}
