using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [CHECK PARTY SYMBOL]、[SET PARTY SYMBOL]、[CHECK PLAYER NUM]、[CHECK PARTYMEMBERS STATE]、[CHECK PARTY SLOT]、[ABS CASTING](M107)。
/// 期望值手算,算式写在注释里;"整体真假"的条件用 CondProgram(条件 + [DO BEHAVIOR] [ME],设速度 4 的日志就是触发了)观察。
/// </summary>
public sealed partial class ActRuntimeTests
{
    /// <summary>带队伍存储的替身宿主:四个槽、队伍符号(出生 -1)、读条通知。</summary>
    private sealed class PartyHost : IActHost
    {
        public Dictionary<ActSelector, List<IActEntity>> Sets { get; } = new();

        public IActEntity[] Slots { get; } = new IActEntity[ActRunner.PartySlotCount];

        /// <summary>槽被占着但角色为空的槽(客户端允许的状态)。</summary>
        public bool[] OccupiedWithoutCharacter { get; } = new bool[ActRunner.PartySlotCount];

        public int Symbol { get; private set; } = -1;

        public int SymbolWrites { get; private set; }

        public List<(IActEntity owner, int first, int second)> AbsCasts { get; } = new();

        public int PartySymbol => Symbol;

        public void SetPartySymbol(int value)
        {
            Symbol = value;
            SymbolWrites++;
        }

        public IActEntity PartyMember(int slot) => Slots[slot];

        public bool PartySlotOccupied(int slot) => Slots[slot] != null || OccupiedWithoutCharacter[slot];

        public void ShowAbsCasting(IActEntity owner, int first, int second) => AbsCasts.Add((owner, first, second));

        public int Random(int n) => 0;

        public long NowMs => 0;

        public void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft)
        {
        }

        public void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into)
        {
            if (Sets.TryGetValue(selector, out List<IActEntity> list))
            {
                into.AddRange(list.Where(e => e.IsAlive));
            }
        }

        public void Shake(int strength, int durationMs)
        {
        }

        public void SummonMonster(IActEntity master, int index, int level, float x, float y, float z, bool facingLeft, int lifetimeMs)
        {
        }
    }

    private static bool FiresOn(ActProgram p, IActHost host)
    {
        var owner = new Entity();
        new ActRunner(p, owner, host).Update(0, 0.016f);
        return owner.Log.Contains("speed:4,-,-");
    }

    private static Entity Member(bool alive = true) => new Entity { Kind = ActEntityKind.Character, IsAlive = alive };

    private static PartyHost PartyOf(params Entity[] slots)
    {
        var host = new PartyHost();
        for (int i = 0; i < slots.Length; i++)
        {
            host.Slots[i] = slots[i];
        }
        return host;
    }

    // ------------------------------------------------------------------ [CHECK PARTY SYMBOL]、[SET PARTY SYMBOL]

    [Fact]
    public void CheckPartySymbolCompilesExactlyTwoIntegersOrStaysUndone()
    {
        ActRecord r = CondProgram(L("[CHECK PARTY SYMBOL]", 1, 5)).Triggers[0].Records.First(x => x.Kind == ActRecordKind.CheckPartySymbol);
        Assert.Equal(1, r.A);
        Assert.Equal(5, r.B);
        foreach (ScriptLine[] bad in new[] { Lines(L("[CHECK PARTY SYMBOL]", 1)), Lines(L("[CHECK PARTY SYMBOL]")), Lines(L("[CHECK PARTY SYMBOL]", 1, 5, 7)) })
        {
            ActProgram p = CondProgram(bad);
            Assert.Contains("[CHECK PARTY SYMBOL]", p.Triggers[0].UnsupportedTags);
        }
    }

    [Fact]
    public void PartySymbolStartsAtMinusOne()
    {
        // 队伍符号出生值 -1(队伍构造函数调重置写 -1,观察)
        Assert.True(FiresOn(CondProgram(L("[CHECK PARTY SYMBOL]", 1, -1)), new PartyHost()));
        Assert.False(FiresOn(CondProgram(L("[CHECK PARTY SYMBOL]", 1, 0)), new PartyHost()));
        Assert.False(FiresOn(CondProgram(L("[CHECK PARTY SYMBOL]", 0, -1)), new PartyHost()));
    }

    [Fact]
    public void CheckPartySymbolModesAgainstTheStoredValue()
    {
        // 队伍符号 5。模式 1 要相等、模式 0 要不等,别的模式恒假:
        //   (1,5) 5==5 真;(1,4) 5==4 假;(0,5) 5!=5 假;(0,4) 5!=4 真;(2,5)、(-1,4) 假
        foreach ((int mode, int value, bool expected) in new[]
        {
            (1, 5, true), (1, 4, false), (0, 5, false), (0, 4, true), (2, 5, false), (-1, 4, false),
        })
        {
            var host = new PartyHost();
            host.SetPartySymbol(5);
            Assert.Equal(expected, FiresOn(CondProgram(L("[CHECK PARTY SYMBOL]", mode, value)), host));
        }
    }

    [Fact]
    public void CheckPartySymbolIsWholeTruthNotAPerObjectFilter()
    {
        // [WHICH] [ALL ENEMY] 选出两个对象,队伍条件为真时两个都留(不逐个过滤),为假时整条触发器放弃(两个都不被选中)
        ActProgram p = Filter(L("[CHECK PARTY SYMBOL]", 1, 5));
        var yes = new PartyHost();
        yes.SetPartySymbol(5);
        Entity a = new Entity(), b = new Entity();
        yes.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, b };
        new ActRunner(p, new Entity(), yes).Update(0, 0.016f);
        Assert.Equal(new[] { true, true }, new[] { a.Log.Count > 0, b.Log.Count > 0 });

        var no = new PartyHost();
        Entity c = new Entity(), d = new Entity();
        no.Sets[ActSelector.AllEnemy] = new List<IActEntity> { c, d };
        new ActRunner(p, new Entity(), no).Update(0, 0.016f);
        Assert.Equal(new[] { false, false }, new[] { c.Log.Count > 0, d.Log.Count > 0 });
    }

    [Fact]
    public void SetPartySymbolWritesOnceRegardlessOfHowManyTargets()
    {
        var host = new PartyHost();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { new Entity(), new Entity(), new Entity() };
        new ActRunner(OnAllEnemies(L("[SET PARTY SYMBOL]", 7)), new Entity(), host).Update(0, 0.016f);
        // 三个目标,队伍符号只写一次(没有目标循环)
        Assert.Equal(7, host.Symbol);
        Assert.Equal(1, host.SymbolWrites);
    }

    [Fact]
    public void SetPartySymbolThenCheckRoundTripsInTriggerOrder()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 0, 0), L("[CHECK PARTY SYMBOL]", 1, 6), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(L("[SET PARTY SYMBOL]", 6)),
            Behavior(Speed(4))));
        var owner = new Entity();
        var host = new PartyHost();
        new ActRunner(p, owner, host).Update(0, 0.016f);
        Assert.Equal(6, host.Symbol);
        Assert.Contains("speed:4,-,-", owner.Log);
    }

    [Fact]
    public void AHostWithoutPartyStorageReadsMinusOneAndDropsWrites()
    {
        // 默认接口实现:没有存储的宿主恒读 -1、写入丢弃。先写 5 再读:读 5 不成立,读 -1 成立
        ActProgram write5ThenRead(int expected) => P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Trigger(L("[FRAME]", 0, 0), L("[CHECK PARTY SYMBOL]", 1, expected), L("[DO BEHAVIOR]"), L("[ME]", 1)),
            Behavior(L("[SET PARTY SYMBOL]", 5)),
            Behavior(Speed(4))));
        Assert.False(Fires(write5ThenRead(5), new Entity()));
        Assert.True(Fires(write5ThenRead(-1), new Entity()));
    }

    // ------------------------------------------------------------------ [CHECK PLAYER NUM]

    [Fact]
    public void CheckPlayerNumCompilesItsSubTags()
    {
        ActRecord r = Assert.Single(CondProgram(L("[CHECK PLAYER NUM]"), L("[COUNT]", 2), L("[INCLUDE DEAD]", 1), L("[/CHECK PLAYER NUM]"))
            .Triggers[0].Records, x => x.Kind == ActRecordKind.CheckPlayerNum);
        Assert.Equal(new[] { 2, 1 }, r.Ints);

        // 缺省:没写 [INCLUDE DEAD] 是不含死者(0),没写 [COUNT] 是 0 人
        ActRecord onlyCount = CondProgram(L("[CHECK PLAYER NUM]"), L("[COUNT]", 3), L("[/CHECK PLAYER NUM]"))
            .Triggers[0].Records.First(x => x.Kind == ActRecordKind.CheckPlayerNum);
        Assert.Equal(new[] { 3, 0 }, onlyCount.Ints);
        ActRecord onlyDead = CondProgram(L("[CHECK PLAYER NUM]"), L("[INCLUDE DEAD]", 1), L("[/CHECK PLAYER NUM]"))
            .Triggers[0].Records.First(x => x.Kind == ActRecordKind.CheckPlayerNum);
        Assert.Equal(new[] { 0, 1 }, onlyDead.Ints);
    }

    [Fact]
    public void CheckPlayerNumSubTagOrderIsFreeAndTheLastWriteWins()
    {
        ActRecord a = CondProgram(L("[CHECK PLAYER NUM]"), L("[INCLUDE DEAD]", 1), L("[COUNT]", 2), L("[/CHECK PLAYER NUM]"))
            .Triggers[0].Records.First(x => x.Kind == ActRecordKind.CheckPlayerNum);
        ActRecord b = CondProgram(L("[CHECK PLAYER NUM]"), L("[COUNT]", 2), L("[INCLUDE DEAD]", 1), L("[/CHECK PLAYER NUM]"))
            .Triggers[0].Records.First(x => x.Kind == ActRecordKind.CheckPlayerNum);
        Assert.Equal(b.Ints, a.Ints);
        ActRecord last = CondProgram(L("[CHECK PLAYER NUM]"), L("[COUNT]", 1), L("[COUNT]", 3), L("[/CHECK PLAYER NUM]"))
            .Triggers[0].Records.First(x => x.Kind == ActRecordKind.CheckPlayerNum);
        Assert.Equal(new[] { 3, 0 }, last.Ints);
    }

    [Fact]
    public void CheckPlayerNumWithUnreadableSubTagsStaysUndone()
    {
        foreach (ScriptLine[] bad in new[]
        {
            Lines(L("[CHECK PLAYER NUM]"), L("[COUNT]"), L("[/CHECK PLAYER NUM]")),                         // [COUNT] 没写整数
            Lines(L("[CHECK PLAYER NUM]"), L("[INCLUDE DEAD]", 2), L("[/CHECK PLAYER NUM]")),            // 只认 0 与 1
            Lines(L("[CHECK PLAYER NUM]"), L("[COUNT]", 1), L("[LEVEL]", 1), L("[/CHECK PLAYER NUM]")),  // 不认得的子标签
        })
        {
            Assert.Contains("[CHECK PLAYER NUM]", CondProgram(bad).Triggers[0].UnsupportedTags);
        }
    }

    [Fact]
    public void CheckPlayerNumCountsTheSingleHeroOfTheDefaultHost()
    {
        // 默认宿主:主角占槽 0,一个人。主角活着:人数 1
        var alive = new Host { PlayableCharacter = Member() };
        ActProgram count(int n, int dead = 0) => CondProgram(L("[CHECK PLAYER NUM]"), L("[COUNT]", n), L("[INCLUDE DEAD]", dead), L("[/CHECK PLAYER NUM]"));
        Assert.True(Fires(count(1), new Entity(), alive));          // 1 == 1
        Assert.False(Fires(count(2), new Entity(), alive));         // 1 != 2(反例)
        Assert.False(Fires(count(0), new Entity(), alive));         // 1 != 0(边界)

        // 主角死了:不含死者 → 0 人;含死者 → 1 人
        var dead = new Host { PlayableCharacter = Member(false) };
        Assert.True(Fires(count(0, 0), new Entity(), dead));        // 0 == 0
        Assert.False(Fires(count(1, 0), new Entity(), dead));       // 0 != 1
        Assert.True(Fires(count(1, 1), new Entity(), dead));        // 1 == 1
        Assert.False(Fires(count(0, 1), new Entity(), dead));       // 1 != 0

        // 取不到主角:一个人也没有
        Assert.True(Fires(count(0), new Entity(), new Host()));
        Assert.False(Fires(count(1), new Entity(), new Host()));
    }

    [Fact]
    public void CheckPlayerNumCountsEveryOccupiedSlotOfAFourSlotParty()
    {
        // 槽:[活, 死, 活, 空]。不含死者 2 人(活的两个),含死者 3 人(空槽不算)
        PartyHost host = PartyOf(Member(), Member(false), Member());
        ActProgram count(int n, int dead) => CondProgram(L("[CHECK PLAYER NUM]"), L("[COUNT]", n), L("[INCLUDE DEAD]", dead), L("[/CHECK PLAYER NUM]"));
        Assert.True(FiresOn(count(2, 0), host));                // 2 == 2
        Assert.False(FiresOn(count(3, 0), host));               // 2 != 3
        Assert.True(FiresOn(count(3, 1), host));                // 3 == 3
        Assert.False(FiresOn(count(2, 1), host));               // 3 != 2
        Assert.False(FiresOn(count(4, 1), PartyOf(Member(), Member(false), Member())));        // 3 != 4

        // 满员四个人全活:4 人(上界)
        Assert.True(FiresOn(count(4, 0), PartyOf(Member(), Member(), Member(), Member())));
        // 一个人也没有:0 人
        Assert.True(FiresOn(count(0, 1), new PartyHost()));
    }

    [Fact]
    public void CheckPlayerNumBlockWithoutAnySubTagIsFalseEvenForAnEmptyParty()
    {
        // 子标签一个也没有:客户端报错并判假。对照:同一个空队伍,[COUNT] 0 为真
        ActProgram bare = CondProgram(L("[CHECK PLAYER NUM]"), L("[/CHECK PLAYER NUM]"));
        ActRecord r = bare.Triggers[0].Records.First(x => x.Kind == ActRecordKind.CheckPlayerNum);
        Assert.Empty(r.Ints);
        Assert.False(FiresOn(bare, new PartyHost()));
        Assert.True(FiresOn(CondProgram(L("[CHECK PLAYER NUM]"), L("[COUNT]", 0), L("[/CHECK PLAYER NUM]")), new PartyHost()));
    }

    // ------------------------------------------------------------------ [CHECK PARTYMEMBERS STATE]

    [Fact]
    public void CheckPartyMembersStateCompilesExactlyOneSubTag()
    {
        foreach ((string sub, int code) in new[] { ("[ALL ALIVE]", 0x4e), ("[ALL DEATH]", 0x4f), ("[EVEN ONE DEATH]", 0x50) })
        {
            ActRecord r = CondProgram(L("[CHECK PARTYMEMBERS STATE]"), L(sub)).Triggers[0].Records.First(x => x.Kind == ActRecordKind.CheckPartyMembersState);
            Assert.Equal(code, r.Number);
        }
        foreach (ScriptLine[] bad in new[]
        {
            Lines(L("[CHECK PARTYMEMBERS STATE]")),                                        // 没有子标签
            Lines(L("[CHECK PARTYMEMBERS STATE]"), L("[ALL ALIVE]"), L("[ALL DEATH]")),    // 两个子标签
            Lines(L("[CHECK PARTYMEMBERS STATE]"), L("[NO ONE]")),                         // 不认得
        })
        {
            Assert.Contains("[CHECK PARTYMEMBERS STATE]", CondProgram(bad).Triggers[0].UnsupportedTags);
        }
    }

    private static bool PartyState(string sub, PartyHost host) => FiresOn(CondProgram(L("[CHECK PARTYMEMBERS STATE]"), L(sub)), host);

    [Fact]
    public void AllAliveNeedsEveryOccupiedSlotToHoldALivingCharacter()
    {
        Assert.True(PartyState("[ALL ALIVE]", PartyOf(Member(), Member(), Member())));          // 三个都活
        Assert.False(PartyState("[ALL ALIVE]", PartyOf(Member(), Member(false), Member())));    // 一个死 → 假
        Assert.False(PartyState("[ALL ALIVE]", PartyOf(Member(false), Member(false))));         // 全死
        Assert.True(PartyState("[ALL ALIVE]", new PartyHost()));                                // 没人:循环不进,真(边界)
        // 槽占着而角色为空:按"不活着"算 → 假
        var holes = PartyOf(Member());
        holes.OccupiedWithoutCharacter[1] = true;
        Assert.False(PartyState("[ALL ALIVE]", holes));
    }

    [Fact]
    public void AllDeathFailsOnAnyLivingCharacterAndSkipsEmptyCharacterSlots()
    {
        Assert.False(PartyState("[ALL DEATH]", PartyOf(Member(), Member(), Member())));         // 全活 → 假
        Assert.False(PartyState("[ALL DEATH]", PartyOf(Member(false), Member(), Member(false))));   // 夹着一个活的 → 假
        Assert.True(PartyState("[ALL DEATH]", PartyOf(Member(false), Member(false))));          // 全死 → 真
        Assert.True(PartyState("[ALL DEATH]", new PartyHost()));                                // 没人:真(边界)
        // 槽占着而角色为空的跳过(不当作"活着"),其余全死 → 真
        var holes = PartyOf(Member(false));
        holes.OccupiedWithoutCharacter[1] = true;
        Assert.True(PartyState("[ALL DEATH]", holes));
        // 只有一个角色为空的槽:真
        var onlyHole = new PartyHost();
        onlyHole.OccupiedWithoutCharacter[0] = true;
        Assert.True(PartyState("[ALL DEATH]", onlyHole));
    }

    [Fact]
    public void EvenOneDeathIsTrueWhenAnyOccupiedSlotHoldsNoLivingCharacter()
    {
        // 方向与旧卡片相反(旧卡片把 0x50 记成"全活才真"):有人死了才真
        Assert.True(PartyState("[EVEN ONE DEATH]", PartyOf(Member(), Member(false), Member())));    // 一个死 → 真
        Assert.True(PartyState("[EVEN ONE DEATH]", PartyOf(Member(false), Member(false))));         // 全死 → 真
        Assert.False(PartyState("[EVEN ONE DEATH]", PartyOf(Member(), Member(), Member())));        // 全活 → 假(反例)
        Assert.False(PartyState("[EVEN ONE DEATH]", new PartyHost()));                              // 没人 → 假(边界)
        // 槽占着而角色为空:按"没有活着的角色"算 → 真
        var holes = PartyOf(Member());
        holes.OccupiedWithoutCharacter[1] = true;
        Assert.True(PartyState("[EVEN ONE DEATH]", holes));
    }

    [Fact]
    public void PartyMembersStateOfTheDefaultHostSeesOnlyTheHeroInSlotZero()
    {
        ActProgram allAlive = CondProgram(L("[CHECK PARTYMEMBERS STATE]"), L("[ALL ALIVE]"));
        ActProgram oneDead = CondProgram(L("[CHECK PARTYMEMBERS STATE]"), L("[EVEN ONE DEATH]"));
        var alive = new Host { PlayableCharacter = Member() };
        Assert.True(Fires(allAlive, new Entity(), alive));
        Assert.False(Fires(oneDead, new Entity(), alive));
        var dead = new Host { PlayableCharacter = Member(false) };
        Assert.False(Fires(allAlive, new Entity(), dead));
        Assert.True(Fires(oneDead, new Entity(), dead));
    }

    // ------------------------------------------------------------------ [CHECK PARTY SLOT]

    [Fact]
    public void CheckPartySlotCompilesExactlyTwoIntegersOrStaysUndone()
    {
        ActTrigger t = Assert.Single(CondProgram(L("[CHECK PARTY SLOT]", 2, 1)).Triggers);
        Assert.True(t.Supported);
        ActRecord r = t.Records.First(x => x.Kind == ActRecordKind.CheckPartySlot);
        Assert.Equal(2, r.A);
        Assert.Equal(1, r.B);
        foreach (ScriptLine[] bad in new[] { Lines(L("[CHECK PARTY SLOT]", 1)), Lines(L("[CHECK PARTY SLOT]")), Lines(L("[CHECK PARTY SLOT]", 1, 0, 3)) })
        {
            ActTrigger u = Assert.Single(CondProgram(bad).Triggers);
            Assert.False(u.Supported);
            Assert.Contains("[CHECK PARTY SLOT]", u.UnsupportedTags);
        }
    }

    [Fact]
    public void CheckPartySlotIsTrueWhenTheSlotIsTakenIffTheFlagIsOne()
    {
        // 队伍:槽 0 有主角,槽 1 空,槽 2 有人,槽 3 空。结果 = (槽被占着) == (标志 == 1)。
        PartyHost host = PartyOf(Member(), null, Member());
        bool Result(int slot, int flag) => FiresOn(CondProgram(L("[CHECK PARTY SLOT]", slot, flag)), host);

        Assert.True(Result(0, 1));       // 占着、标志 1:true == true
        Assert.False(Result(0, 0));      // 占着、标志 0:true == false(反例:标志 0 要槽没人)
        Assert.False(Result(1, 1));      // 空着、标志 1:false == true
        Assert.True(Result(1, 0));       // 空着、标志 0:false == false
        Assert.True(Result(2, 1));       // 第二个有人的槽
        Assert.False(Result(2, 0));
        Assert.True(Result(3, 0));       // 边界:最后一个槽,空着
        Assert.False(Result(3, 1));
        Assert.True(Result(4, 0));       // 越界的槽(客户端对 4 以上给空表项):当没人
        Assert.False(Result(4, 1));
        Assert.True(Result(-1, 0));      // 槽 -1 是"没写"的读数:当没人
        Assert.False(Result(-1, 1));
        Assert.False(Result(0, 2));      // 标志只有 1 算"要有人",别的值都算"要没人":占着的槽 true == false
        Assert.True(Result(1, 2));       // 空着的槽 false == false
        Assert.True(Result(0, -1));      // 标志 -1("没写"):客户端不查槽,当没人:false == false(照"槽占着"算会是 false,所以这条是反例)
        Assert.False(Result(0, 1000));   // 大标志照样只看 == 1
    }

    [Fact]
    public void CheckPartySlotCountsATakenSlotWhateverTheCharacterIsDoing()
    {
        // 角色死了也算占着(客户端只看槽表项是否非空,不看角色)
        PartyHost dead = PartyOf(Member(alive: false));
        Assert.True(FiresOn(CondProgram(L("[CHECK PARTY SLOT]", 0, 1)), dead));
        Assert.False(FiresOn(CondProgram(L("[CHECK PARTY SLOT]", 0, 0)), dead));

        // 槽表项非空、角色空:也算占着
        PartyHost taken = PartyOf();
        taken.OccupiedWithoutCharacter[1] = true;
        Assert.True(FiresOn(CondProgram(L("[CHECK PARTY SLOT]", 1, 1)), taken));
        Assert.False(FiresOn(CondProgram(L("[CHECK PARTY SLOT]", 0, 1)), taken));
    }

    [Fact]
    public void CheckPartySlotOnTheDefaultSingleHeroHostSeesSlotZeroTakenOnlyWhileTheHeroIsThere()
    {
        // 全库唯一一处写的是 "槽 0 标志 0"(槽 0 没人才触发);默认宿主里槽 0 = 主角
        ActProgram p = CondProgram(L("[CHECK PARTY SLOT]", 0, 0));
        Assert.False(Fires(p, new Entity(), new Host { PlayableCharacter = Member() }));
        Assert.True(Fires(p, new Entity(), new Host { PlayableCharacter = null }));
    }

    // ------------------------------------------------------------------ [ABS CASTING]

    [Fact]
    public void AbsCastingCompilesUpToTwoIntegersAndReadsMissingOnesAsMinusOne()
    {
        ActBehavior ok = OnlyBehavior(P(Behavior(L("[ABS CASTING]", 3, 4))));
        Assert.Equal(ActBehaviorKind.AbsCasting, ok.Kind);
        Assert.Equal(3, ok.AbsCastFirst);
        Assert.Equal(4, ok.AbsCastSecond);

        // 少写:客户端参数读取越界给 -1(全库有不带整数与只带一个整数的写法)
        ActBehavior none = OnlyBehavior(P(Behavior(L("[ABS CASTING]"))));
        Assert.Equal(ActBehaviorKind.AbsCasting, none.Kind);
        Assert.Equal(-1, none.AbsCastFirst);
        Assert.Equal(-1, none.AbsCastSecond);
        ActBehavior one = OnlyBehavior(P(Behavior(L("[ABS CASTING]", 3))));
        Assert.Equal(ActBehaviorKind.AbsCasting, one.Kind);
        Assert.Equal(3, one.AbsCastFirst);
        Assert.Equal(-1, one.AbsCastSecond);

        // 多写:客户端加载块读几个没读通,不替它猜
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(P(Behavior(L("[ABS CASTING]", 3, 4, 5)))).Kind);
    }

    [Fact]
    public void AbsCastingReachesTheHostWithoutChangingTheOtherBehaviors()
    {
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Cat(new[] { L("[ABS CASTING]", 3, 4) }, Speed(4)))));
        var owner = new Entity();
        var host = new PartyHost();
        new ActRunner(p, owner, host).Update(0, 0.016f);
        Assert.Equal(new[] { (3, 4) }, host.AbsCasts.Select(c => (c.first, c.second)));
        Assert.Same(owner, host.AbsCasts[0].owner);
        Assert.Contains("speed:4,-,-", owner.Log);          // 同组的设速度照常执行
    }

    [Fact]
    public void AbsCastingWithANegativeIntegerRaisesNoGauge()
    {
        // 客户端对负数不起读条条;同组其它行为不受影响
        foreach ((int a, int b) in new[] { (-1, 4), (3, -1), (-1, -1) })
        {
            ActProgram p = P(Cat(
                Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
                Behavior(Cat(new[] { L("[ABS CASTING]", a, b) }, Speed(4)))));
            var owner = new Entity();
            var host = new PartyHost();
            new ActRunner(p, owner, host).Update(0, 0.016f);
            Assert.Empty(host.AbsCasts);
            Assert.Contains("speed:4,-,-", owner.Log);
        }
        // 少写的整数按 -1 读:同样不起读条条,同组其它行为照常
        foreach (ScriptLine abs in new[] { L("[ABS CASTING]"), L("[ABS CASTING]", 3) })
        {
            ActProgram p = P(Cat(
                Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
                Behavior(Cat(new[] { abs }, Speed(4)))));
            var owner = new Entity();
            var host = new PartyHost();
            new ActRunner(p, owner, host).Update(0, 0.016f);
            Assert.Empty(host.AbsCasts);
            Assert.Contains("speed:4,-,-", owner.Log);
        }
        // 边界:0 不是负数,照常起
        var zero = new PartyHost();
        new ActRunner(P(Cat(Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)), Behavior(L("[ABS CASTING]", 0, 0)))), new Entity(), zero).Update(0, 0.016f);
        Assert.Single(zero.AbsCasts);
    }
}
