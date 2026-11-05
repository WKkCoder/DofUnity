using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [CHECK SYMBOL]、[SET SYMBOL]、[SET SYMBOL OPTION](M107)。
/// 期望值全部手算,算式写在各测试的注释里;实现之前这几个标签让整条触发器读不懂(永不触发),所以"留下 / 触发了"本身就是新旧差别。
/// </summary>
public sealed partial class ActRuntimeTests
{
    /// <summary>带符号与受击开关存储的替身对象。出生符号 -1 与客户端重置值一致(推断);受击开关缺省"不知道"(null)。</summary>
    private sealed class SymEntity : Entity, IActEntity
    {
        public int Symbol { get; set; } = -1;

        public int SymbolOption { get; set; } = -1;

        public int SymbolWrites { get; private set; }

        public bool? DamageEnabled { get; set; }

        public int ActSymbol => Symbol;

        public void SetActSymbol(int value)
        {
            Symbol = value;
            SymbolWrites++;
        }

        public int ActSymbolOption => SymbolOption;

        public void SetActSymbolOption(int value) => SymbolOption = value;

        public bool? ActDamageEnabled => DamageEnabled;
    }

    private static SymEntity[] Syms(params int[] symbols) => symbols.Select(s => new SymEntity { Symbol = s }).ToArray();

    /// <summary>
    /// [WHICH] [ALL ENEMY] 之后接一串条件,再用 [CHECKED NO] [=] 0 看工作集是不是空了:空了才触发,行为落在拥有者自己身上(拥有者的日志非空 = 触发了)。
    /// 只看"哪个对象被行为选中"看不出空集合与整条条件为假的差别,所以要数工作集。
    /// </summary>
    private static bool EmptyAfterAll(ScriptLine[] conditions, Entity owner, Host host, params Entity[] items)
    {
        ActProgram p = P(Cat(
            Trigger(Cat(
                new[] { L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]") },
                conditions,
                new[] { L("[CHECKED NO]"), L("[=]", 0), L("[DO BEHAVIOR]"), L("[ME]", 0) })),
            Behavior(Speed(1))));
        host.Sets[ActSelector.AllEnemy] = items.Cast<IActEntity>().ToList();
        new ActRunner(p, owner, host).Update(0, 0.016f);
        return owner.Log.Count > 0;
    }

    private static ScriptLine[] Lines(params ScriptLine[] lines) => lines;

    /// <summary>[WHICH] [ALL ENEMY] 选出全部敌人,[DO BEHAVIOR] 对它们执行行为组 0。</summary>
    private static ActProgram OnAllEnemies(params ScriptLine[] behavior) => P(Cat(
        Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
        Behavior(behavior)));

    // ------------------------------------------------------------------ [CHECK SYMBOL]

    [Fact]
    public void CheckSymbolCompilesExactlyTwoIntegersOrStaysUndone()
    {
        ActProgram ok = Filter(L("[CHECK SYMBOL]", 1, 5));
        ActRecord r = ok.Triggers[0].Records.First(x => x.Kind == ActRecordKind.CheckSymbol);
        Assert.Equal(1, r.A);
        Assert.Equal(5, r.B);
        Assert.Empty(ok.Triggers[0].UnsupportedTags);

        // 反例:少写、不写、多写都读不懂(不静默猜)
        foreach (ScriptLine[] bad in new[] { Lines(L("[CHECK SYMBOL]", 1)), Lines(L("[CHECK SYMBOL]")), Lines(L("[CHECK SYMBOL]", 1, 5, 7)) })
        {
            ActProgram p = Filter(bad);
            Assert.Contains(ActRecordKind.Unsupported, p.Triggers[0].Records.Select(x => x.Kind));
            Assert.Contains("[CHECK SYMBOL]", p.Triggers[0].UnsupportedTags);
        }
    }

    [Fact]
    public void CheckSymbolModeOneKeepsEqualAndModeZeroKeepsDifferent()
    {
        // 符号 5、6、-1 三个对象,比较值 5:
        //   模式 1(要相等):5==5 留,6==5 否,-1==5 否 → 真、假、假
        //   模式 0(要不等):5!=5 否,6!=5 留,-1!=5 留 → 假、真、真
        Assert.Equal(new[] { true, false, false }, Kept(Filter(L("[CHECK SYMBOL]", 1, 5)), new Entity(), Syms(5, 6, -1)));
        Assert.Equal(new[] { false, true, true }, Kept(Filter(L("[CHECK SYMBOL]", 0, 5)), new Entity(), Syms(5, 6, -1)));
    }

    [Fact]
    public void CheckSymbolWithAnyOtherModeKeepsNothing()
    {
        // 反例:模式只有 1 与 0 有意义;2、-1、7 一律剔除,哪怕符号恰好等于比较值(符号 5、比较值 5)
        foreach (int mode in new[] { 2, -1, 7 })
        {
            Assert.Equal(new[] { false, false }, Kept(Filter(L("[CHECK SYMBOL]", mode, 5)), new Entity(), Syms(5, 6)));
        }
    }

    [Fact]
    public void CheckSymbolOnAnEmptySetPassesWhileAMismatchEmptiesTheSet()
    {
        ScriptLine[] cond = Lines(L("[CHECK SYMBOL]", 1, 5));
        // 边界:空集合照样通过(客户端逐对象循环一次也不进)→ [CHECKED NO] [=] 0 触发
        Assert.True(EmptyAfterAll(cond, new Entity(), new Host()));
        // 一个不匹配的对象被剔除后集合变空 → 触发;一个匹配的留下 → 集合非空 → 不触发
        Assert.True(EmptyAfterAll(cond, new Entity(), new Host(), Syms(6)));
        Assert.False(EmptyAfterAll(cond, new Entity(), new Host(), Syms(5)));
    }

    [Fact]
    public void ObjectsBeginWithSymbolMinusOne()
    {
        // 出生值 -1(推断):[CHECK SYMBOL] 1 -1 命中新对象,[CHECK SYMBOL] 0 -1 不命中
        Assert.Equal(new[] { true }, Kept(Filter(L("[CHECK SYMBOL]", 1, -1)), new Entity(), new SymEntity()));
        Assert.Equal(new[] { false }, Kept(Filter(L("[CHECK SYMBOL]", 0, -1)), new Entity(), new SymEntity()));
    }

    [Fact]
    public void ObjectsWithoutSymbolStorageAlwaysReadMinusOne()
    {
        // 接口默认实现:没有存储的对象恒读 -1、写入丢弃
        IActEntity plain = new Entity();
        plain.SetActSymbol(9);
        plain.SetActSymbolOption(9);
        Assert.Equal(-1, plain.ActSymbol);
        Assert.Equal(-1, plain.ActSymbolOption);
    }

    // ------------------------------------------------------------------ [SET SYMBOL]、[SET SYMBOL OPTION]

    [Fact]
    public void SetSymbolFamilyCompilesExactlyOneIntegerOrStaysUndone()
    {
        foreach ((string tag, ActBehaviorKind kind) in new[]
        {
            ("[SET SYMBOL]", ActBehaviorKind.SetSymbol),
            ("[SET SYMBOL OPTION]", ActBehaviorKind.SetSymbolOption),
            ("[SET PARTY SYMBOL]", ActBehaviorKind.SetPartySymbol),
        })
        {
            ActBehavior ok = OnlyBehavior(P(Behavior(L(tag, 3))));
            Assert.Equal(kind, ok.Kind);
            Assert.Equal(3, ok.SymbolValue);
            // 反例:不写、写两个都读不懂
            Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(P(Behavior(L(tag)))).Kind);
            Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(P(Behavior(L(tag, 3, 4)))).Kind);
        }
        // 负值照样是合法的符号
        Assert.Equal(-1, OnlyBehavior(P(Behavior(L("[SET SYMBOL]", -1)))).SymbolValue);
    }

    [Fact]
    public void SetSymbolWritesEveryTargetWithoutFilteringAliveOrKind()
    {
        var live = new SymEntity { Kind = ActEntityKind.Monster };
        var dead = new SymEntity { Kind = ActEntityKind.Monster, IsAlive = false };
        var passive = new SymEntity { Kind = ActEntityKind.Passive };
        var hero = new SymEntity { Kind = ActEntityKind.Character };
        var host = new Host { ListDeadToo = true };
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { live, dead, passive, hero };
        new ActRunner(OnAllEnemies(L("[SET SYMBOL]", 9)), new Entity(), host).Update(0, 0.016f);
        // 四个目标各写一次,一个不落(死了的、被动对象、主角都写;客户端写入块不筛存活与种类)
        Assert.Equal(new[] { 9, 9, 9, 9 }, new[] { live.Symbol, dead.Symbol, passive.Symbol, hero.Symbol });
        Assert.Equal(new[] { 1, 1, 1, 1 }, new[] { live.SymbolWrites, dead.SymbolWrites, passive.SymbolWrites, hero.SymbolWrites });
        // 第二个整数不动
        Assert.Equal(-1, live.SymbolOption);
    }

    [Fact]
    public void SetSymbolOptionWritesOnlyTheSecondInteger()
    {
        var a = new SymEntity { Symbol = 3 };
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a };
        new ActRunner(OnAllEnemies(L("[SET SYMBOL OPTION]", 4)), new Entity(), host).Update(0, 0.016f);
        Assert.Equal(4, a.SymbolOption);
        Assert.Equal(3, a.Symbol);          // 符号不被碰
        Assert.Equal(0, a.SymbolWrites);

        // 反过来 [SET SYMBOL] 不碰第二个整数
        var b = new SymEntity { SymbolOption = 8 };
        var host2 = new Host();
        host2.Sets[ActSelector.AllEnemy] = new List<IActEntity> { b };
        new ActRunner(OnAllEnemies(L("[SET SYMBOL]", 2)), new Entity(), host2).Update(0, 0.016f);
        Assert.Equal(2, b.Symbol);
        Assert.Equal(8, b.SymbolOption);
    }

    [Fact]
    public void SetSymbolThenCheckSymbolRoundTripsInTriggerOrder()
    {
        // 触发器 0 先写符号 3,同一帧里触发器 1 读到 3 才触发(触发器按序号依次求值;触发器 1 用 [CHECKUP OBJECT] 看过滤后的工作集,[ME] 不看工作集)
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 0, 0), L("[CHECK SYMBOL]", 1, 3), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 1)),
            Behavior(L("[SET SYMBOL]", 3)),
            Behavior(Speed(1))));
        var owner = new SymEntity();
        new ActRunner(p, owner, new Host()).Update(0, 0.016f);
        Assert.Equal(3, owner.Symbol);
        Assert.NotEmpty(owner.Log);          // 触发器 1 触发了

        // 反例:读的是 4,不是写进去的 3 → 触发器 1 不触发
        ActProgram q = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 0, 0), L("[CHECK SYMBOL]", 1, 4), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 1)),
            Behavior(L("[SET SYMBOL]", 3)),
            Behavior(Speed(1))));
        var owner2 = new SymEntity();
        new ActRunner(q, owner2, new Host()).Update(0, 0.016f);
        Assert.Equal(3, owner2.Symbol);
        Assert.Empty(owner2.Log);
    }
}
