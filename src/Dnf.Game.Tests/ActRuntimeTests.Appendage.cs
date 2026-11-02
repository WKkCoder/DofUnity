using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [APPENDAGE] / [DELETE APPENDAGE](M63,证据 E-161):写法(持续毫秒、每项"属性标签 操作符 数值"、操作符只有 [%] 才算比例)、
/// 对目标集合里每个活着的活动对象各挂一个附加物、删除按来源与"同一项"匹配,以及对象身上的附加物集合(同项顶替、按来源删、到期、按顺序算数值块)。
/// 脚本与数值全是合成的。
/// </summary>
public sealed partial class ActRuntimeTests
{
    private static ScriptLine[] Appendage(int duration, params ScriptLine[] entries)
    {
        var list = new List<ScriptLine> { L("[APPENDAGE]", duration) };
        list.AddRange(entries);
        list.Add(L("[/APPENDAGE]"));
        return list.ToArray();
    }

    private static ScriptLine[] DeleteAppendage(int leading, params ScriptLine[] entries)
    {
        var list = new List<ScriptLine> { L("[DELETE APPENDAGE]", leading) };
        list.AddRange(entries);
        list.Add(L("[/DELETE APPENDAGE]"));
        return list.ToArray();
    }

    private static ScriptLine[] Entry(string stat, string op, int value) => new[] { L(stat), L(op, value) };

    // ------------------------------------------------------------------ 编译

    [Fact]
    public void AppendageCompilesTheDurationAndEveryEntryInOrder()
    {
        ActBehavior b = Compiled(Cat(
            Appendage(5000, Cat(Entry("[move speed]", "[%]", -30), Entry("[equipment physical defense]", "[+]", 50), Entry("[stuck]", "[-]", -7)))));

        Assert.Equal(ActBehaviorKind.Appendage, b.Kind);
        Assert.Equal(5000, b.StatDurationMs);
        Assert.Equal(
            new[]
            {
                new StatChange(StatChangeTypes.MoveSpeed, true, -30),
                new StatChange(StatChangeTypes.EquipmentPhysicalDefense, false, 50),
                new StatChange(StatChangeTypes.StuckResistance, false, -7),
            },
            b.StatChanges.ToArray());
    }

    [Fact]
    public void OnlyThePercentOperatorMakesAPercentAndMinusDoesNotNegate()
    {
        // 客户端加载块只把操作符与 [%] 比一次:[+] 与 [-] 都是点数,数值照写的用(E-161)
        ActBehavior b = Compiled(Cat(Appendage(0, Cat(
            Entry("[dark element]", "[-]", 30),
            Entry("[dark element]", "[+]", 30),
            Entry("[dark element]", "[%]", 30)))));

        StatChange[] c = b.StatChanges.ToArray();
        Assert.Equal(new StatChange(StatChangeTypes.ElementToleranceDark, false, 30), c[0]);
        Assert.Equal(c[0], c[1]);
        Assert.Equal(new StatChange(StatChangeTypes.ElementToleranceDark, true, 30), c[2]);
        Assert.NotEqual(c[0], c[2]);
    }

    [Fact]
    public void AnEntryMayCarryExtraNumbersThatAreReadAndIgnored()
    {
        // 全库有 12 项(防御类的点数写法)多带一个数:客户端加载块不读它,后面的项照常
        ActBehavior b = Compiled(Cat(Appendage(100, Cat(
            new[] { L("[physical absolute defense]"), L("[+]", 30, 99) },
            Entry("[move speed]", "[%]", 10)))));

        Assert.Equal(
            new[] { new StatChange(StatChangeTypes.PhysicalAbsoluteDefense, false, 30), new StatChange(StatChangeTypes.MoveSpeed, true, 10) },
            b.StatChanges.ToArray());
    }

    [Fact]
    public void AnUnknownStatTagBecomesTypeMinusOneAndTheEntriesAfterItSurvive()
    {
        ActBehavior b = Compiled(Cat(Appendage(100, Cat(Entry("[jump speed]", "[%]", 5), Entry("[move speed]", "[%]", 10)))));

        Assert.Equal(
            new[] { new StatChange(StatChangeTypes.Unknown, true, 5), new StatChange(StatChangeTypes.MoveSpeed, true, 10) },
            b.StatChanges.ToArray());
    }

    [Fact]
    public void ATruncatedEntryEndsTheListButTheEntriesBeforeItAreKept()
    {
        // 最后一项只有标签、没有操作符与数值(全库没有这样的写法)
        ActBehavior b = Compiled(Cat(Appendage(100, Cat(Entry("[move speed]", "[%]", 20), new[] { L("[attack speed]") }))));

        Assert.Equal(ActBehaviorKind.Appendage, b.Kind);
        Assert.Equal(new[] { new StatChange(StatChangeTypes.MoveSpeed, true, 20) }, b.StatChanges.ToArray());
    }

    [Fact]
    public void AnEmptyAppendageCompilesToNoEntries()
    {
        ActBehavior b = Compiled(Appendage(100));
        Assert.Equal(ActBehaviorKind.Appendage, b.Kind);
        Assert.Empty(b.StatChanges);
    }

    [Fact]
    public void AMissingCloserDoesNotSwallowTheBehaviorThatFollows()
    {
        // 客户端加载块靠 [/APPENDAGE] 收尾;全库每个 [APPENDAGE] 都有收尾,缺了的写法这里认到下一个行为头为止(防御)
        ActProgram p = P(Me(Cat(
            new[] { L("[APPENDAGE]", 100) },
            Entry("[move speed]", "[%]", 10),
            Speed(5))));

        ActBehaviorGroup g = p.Behaviors[0];
        Assert.Equal(new[] { ActBehaviorKind.Appendage, ActBehaviorKind.SetSpeed }, g.Items.Select(i => i.Kind).ToArray());
        Assert.Equal(new[] { new StatChange(StatChangeTypes.MoveSpeed, true, 10) }, g.Items[0].StatChanges.ToArray());
        Assert.Empty(g.UnsupportedTags);
    }

    [Fact]
    public void ABehaviorHeadInTheOperatorSlotEndsTheListAndStaysForTheNextBehavior()
    {
        ActProgram p = P(Me(Cat(
            new[] { L("[APPENDAGE]", 100), L("[move speed]") },
            Speed(5))));

        ActBehaviorGroup g = p.Behaviors[0];
        Assert.Equal(new[] { ActBehaviorKind.Appendage, ActBehaviorKind.SetSpeed }, g.Items.Select(i => i.Kind).ToArray());
        Assert.Empty(g.Items[0].StatChanges);
    }

    [Fact]
    public void DeleteAppendageCompilesLikeAppendageAndKeepsTheLeadingNumber()
    {
        ActBehavior b = Compiled(Cat(DeleteAppendage(2000, Cat(Entry("[move speed]", "[%]", -30), Entry("[stuck]", "[+]", 4)))));

        Assert.Equal(ActBehaviorKind.DeleteAppendage, b.Kind);
        Assert.Equal(2000, b.StatDurationMs);
        Assert.Equal(
            new[] { new StatChange(StatChangeTypes.MoveSpeed, true, -30), new StatChange(StatChangeTypes.StuckResistance, false, 4) },
            b.StatChanges.ToArray());
    }

    [Fact]
    public void AppendageBehaviorsAreSupportedAndTheBehaviorsAfterThemStillCompile()
    {
        ActProgram p = P(Me(Cat(
            Appendage(100, Entry("[move speed]", "[%]", 10)),
            DeleteAppendage(0, Entry("[move speed]", "[%]", 10)),
            Speed(5))));

        ActBehaviorGroup g = p.Behaviors[0];
        Assert.Equal(new[] { ActBehaviorKind.Appendage, ActBehaviorKind.DeleteAppendage, ActBehaviorKind.SetSpeed }, g.Items.Select(i => i.Kind).ToArray());
        Assert.Empty(g.UnsupportedTags);
    }

    // ------------------------------------------------------------------ 运行

    [Fact]
    public void AppendageHangsOneAppendagePerEntryOnEveryLivingActiveTargetWithTheOwnerAsSource()
    {
        var owner = new Entity();
        var monster = new Entity { Kind = ActEntityKind.Monster };
        var hero = new Entity { Kind = ActEntityKind.Character };
        var passive = new Entity { Kind = ActEntityKind.Passive };
        var dead = new Entity { Kind = ActEntityKind.Monster, IsAlive = false };
        var host = new Host { ListDeadToo = true };
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { monster, hero, passive, dead };

        Run(P(OnEnemies(Appendage(3000, Cat(Entry("[move speed]", "[%]", -30), Entry("[stuck]", "[+]", 5))))), owner, host);

        var expected = new[]
        {
            (new StatChange(StatChangeTypes.MoveSpeed, true, -30), 3000, (IActEntity)owner),
            (new StatChange(StatChangeTypes.StuckResistance, false, 5), 3000, (IActEntity)owner),
        };
        Assert.Equal(expected, monster.StatAdds.ToArray());
        Assert.Equal(expected, hero.StatAdds.ToArray());
        Assert.Empty(passive.StatAdds);
        Assert.Empty(dead.StatAdds);
        Assert.Empty(owner.StatAdds);
    }

    [Fact]
    public void AnAppendageOnTheOwnerItselfIsAllowed()
    {
        var owner = new Entity();
        Run(P(Me(Appendage(0, Entry("[attack speed]", "[%]", 25)))), owner);

        var added = Assert.Single(owner.StatAdds);
        Assert.Equal((new StatChange(StatChangeTypes.AttackSpeed, true, 25), 0), (added.change, added.durationMs));
        Assert.Same(owner, added.source);
    }

    [Fact]
    public void DeleteAppendageRemovesEachEntryFromEveryActiveTargetNamingTheOwnerAsTheSource()
    {
        var owner = new Entity();
        var monster = new Entity { Kind = ActEntityKind.Monster };
        var passive = new Entity { Kind = ActEntityKind.Passive };
        var deadMonster = new Entity { Kind = ActEntityKind.Monster, IsAlive = false };
        var host = new Host { ListDeadToo = true };
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { monster, passive, deadMonster };

        Run(P(OnEnemies(DeleteAppendage(0, Cat(Entry("[move speed]", "[%]", -30), Entry("[equipment physical defense]", "[%]", 20))))), owner, host);

        var expected = new[]
        {
            (new StatChange(StatChangeTypes.MoveSpeed, true, -30), (IActEntity)owner),
            (new StatChange(StatChangeTypes.EquipmentPhysicalDefense, true, 20), (IActEntity)owner),
        };
        Assert.Equal(expected, monster.StatRemoves.ToArray());
        Assert.Equal(expected, deadMonster.StatRemoves.ToArray());       // 删除不要求目标活着
        Assert.Empty(passive.StatRemoves);
    }

    // ------------------------------------------------------------------ 对象身上的附加物集合

    private static StatAppendageSet NewSet() => new StatAppendageSet();

    [Fact]
    public void AddingTheSameChangeReplacesTheOldOneWhateverItsSource()
    {
        var a = new Entity();
        var b = new Entity();
        StatAppendageSet set = NewSet();
        var slow = new StatChange(StatChangeTypes.MoveSpeed, true, -30);

        Assert.Equal(0, set.Add(a, slow, 1000));
        Assert.Equal(1, set.Add(b, slow, 5000));
        StatAppendage only = Assert.Single(set.Items);
        Assert.Same(b, only.Source);
        Assert.Equal(5000, only.DurationMs);

        // 数值不同、比例标志不同、类型不同都是另一项:并存
        Assert.Equal(0, set.Add(a, new StatChange(StatChangeTypes.MoveSpeed, true, -40), 1000));
        Assert.Equal(0, set.Add(a, new StatChange(StatChangeTypes.MoveSpeed, false, -30), 1000));
        Assert.Equal(0, set.Add(a, new StatChange(StatChangeTypes.AttackSpeed, true, -30), 1000));
        Assert.Equal(4, set.Count);
    }

    [Fact]
    public void RemovingNeedsTheSameSourceAndTheSameChange()
    {
        var a = new Entity();
        var b = new Entity();
        StatAppendageSet set = NewSet();
        var slow = new StatChange(StatChangeTypes.MoveSpeed, true, -30);
        var armor = new StatChange(StatChangeTypes.EquipmentPhysicalDefense, true, 20);
        set.Add(a, slow, 0);
        set.Add(b, armor, 0);

        Assert.Equal(0, set.Remove(b, slow));                                                        // 来源不对
        Assert.Equal(0, set.Remove(a, new StatChange(StatChangeTypes.MoveSpeed, true, -31)));        // 不是同一项
        Assert.Equal(2, set.Count);
        Assert.Equal(1, set.Remove(a, slow));
        Assert.Equal(StatChangeTypes.EquipmentPhysicalDefense, Assert.Single(set.Items).Change.Type);
    }

    [Fact]
    public void LimitedAppendagesExpireAndUnlimitedOnesStay()
    {
        var a = new Entity();
        StatAppendageSet set = NewSet();
        set.Add(a, new StatChange(StatChangeTypes.MoveSpeed, true, -30), 1000);
        set.Add(a, new StatChange(StatChangeTypes.AttackSpeed, true, 10), 0);
        set.Add(a, new StatChange(StatChangeTypes.HpMax, true, 5), -1);

        Assert.Equal(0, set.Advance(600));
        Assert.Equal(3, set.Count);
        Assert.Equal(0, set.Advance(399));                // 还剩 1 毫秒
        Assert.Equal(3, set.Count);
        Assert.Equal(1, set.Advance(1));                  // 刚好用完就到期
        Assert.Equal(2, set.Count);
        Assert.Equal(0, set.Advance(1_000_000));          // 没有时限的(0 与负数)一直在
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void TheVersionOnlyMovesWhenTheContentChanges()
    {
        var a = new Entity();
        StatAppendageSet set = NewSet();
        int v0 = set.Version;
        set.Clear();
        set.Remove(a, new StatChange(StatChangeTypes.MoveSpeed, true, 1));
        set.Advance(10);
        Assert.Equal(v0, set.Version);

        var slow = new StatChange(StatChangeTypes.MoveSpeed, true, -30);
        set.Add(a, slow, 100);
        int v1 = set.Version;
        Assert.NotEqual(v0, v1);
        set.Advance(50);                                  // 没到期
        Assert.Equal(v1, set.Version);
        set.Advance(60);                                  // 到期
        Assert.NotEqual(v1, set.Version);

        set.Add(a, slow, 0);
        int v2 = set.Version;
        set.Remove(a, slow);
        Assert.NotEqual(v2, set.Version);
        set.Add(a, slow, 0);
        int v3 = set.Version;
        set.Clear();
        Assert.NotEqual(v3, set.Version);
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void ApplyWritesEveryAppendageIntoACopyAndLeavesTheBasisAlone()
    {
        var a = new Entity();
        var basis = new MonsterStats();
        basis.Raw[MonsterStats.IxEquipPhysicalDefense] = 100;
        basis.Raw[MonsterStats.IxHpBase] = 1000;
        int[] before = (int[])basis.Raw.Clone();

        StatAppendageSet set = NewSet();
        set.Add(a, new StatChange(StatChangeTypes.EquipmentPhysicalDefense, false, 50), 0);
        set.Add(a, new StatChange(StatChangeTypes.EquipmentPhysicalDefense, true, 20), 0);
        set.Add(a, new StatChange(StatChangeTypes.HpMax, true, 10), 0);
        MonsterStats effective = set.Apply(basis);

        Assert.Equal(before, basis.Raw);
        Assert.Equal(150, effective.Raw[MonsterStats.IxEquipPhysicalDefense]);
        Assert.Equal(1.2f, effective.F(MonsterStats.IxEquipPhysicalDefense + 1), 4);
        Assert.Equal(1100, effective.HpMax);

        set.Remove(a, new StatChange(StatChangeTypes.HpMax, true, 10));
        Assert.Equal(1000, set.Apply(basis).HpMax);
    }

    [Fact]
    public void SpeedRatesAddUpAndAreFlooredAtZero()
    {
        var a = new Entity();
        StatAppendageSet set = NewSet();
        Assert.Equal(1.0f, set.SpeedRate(StatChangeTypes.MoveSpeed));

        set.Add(a, new StatChange(StatChangeTypes.MoveSpeed, true, -30), 0);
        set.Add(a, new StatChange(StatChangeTypes.MoveSpeed, false, 500), 0);
        set.Add(a, new StatChange(StatChangeTypes.AttackSpeed, true, 50), 0);
        Assert.Equal(1.2f, set.SpeedRate(StatChangeTypes.MoveSpeed), 4);         // 1 − 0.3 + 0.5
        Assert.Equal(1.5f, set.SpeedRate(StatChangeTypes.AttackSpeed), 4);
        Assert.Equal(1.0f, set.SpeedRate(StatChangeTypes.CastSpeed));

        set.Add(a, new StatChange(StatChangeTypes.MoveSpeed, true, -500), 0);
        Assert.Equal(0.0f, set.SpeedRate(StatChangeTypes.MoveSpeed));            // 夹到 0
        Assert.Equal(0.0f, set.Apply(new MonsterStats()).F(MonsterStats.IxMoveSpeed + 1));
    }
}
