using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// 消息族五个行为:[SEND DO BEHAVIOR]、[SEND MESSAGE](含 [ATTACK_COMMAND] 等五个子关键字)、[CHANGE AI]、[CAMOUFLAGE]、[APPEND HIT]。
/// 规则依据客户端执行块 0x71 / 0x79 / 0xec / 0xae / 0x82 与加载块 0x0133ebb0 / 0x0133714c 的静态读码;脚本与数值全是合成的,
/// 期望值都是手算的(每条断言旁边写了推导),不是拿被测代码回算。
/// </summary>
public sealed partial class ActRuntimeTests
{
    /// <summary>带全部消息族收件口的合成对象:收到什么都记下来;<see cref="Runner"/> 是它"当前动作"的运行器(没有就收不下 [SEND DO BEHAVIOR])。</summary>
    private sealed class MsgEntity : Entity, IActGroupReceiver, IActAiPeer, IActCamouflageTarget
    {
        public ActRunner Runner { get; set; }

        public List<int> Posted { get; } = new();

        public bool PostActGroup(int group)
        {
            Posted.Add(group);
            if (Runner == null)
            {
                return false;
            }
            Runner.EnqueueRemoteGroup(group);
            return true;
        }

        public bool HasAiSlave { get; set; } = true;

        public bool HasAiHost { get; set; } = true;

        public bool IsAiCharacter { get; set; }

        public bool IsAiControlledCharacter { get; set; }

        public List<ActAiMessage> Messages { get; } = new();

        public void ReceiveAiMessage(ActAiMessage message) => Messages.Add(message);

        public List<(int role, string path, string from)> AiChanges { get; } = new();

        public void ChangeAi(int role, string path, string fromProgram) => AiChanges.Add((role, path, fromProgram));

        public List<(bool on, int ms)> Camouflages { get; } = new();

        public void SetCamouflage(bool on, int durationMs) => Camouflages.Add((on, durationMs));
    }

    /// <summary>除了基本宿主能力,还收 [APPEND HIT] 的世界;记下每次强行追加的一击。</summary>
    private sealed class MsgHost : IActHost, IActMessageHost
    {
        private readonly Host _inner = new();

        public Host Inner => _inner;

        public long NowMs => _inner.NowMs;

        public int Random(int n) => _inner.Random(n);

        public void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft)
        {
        }

        public void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into) => _inner.Resolve(owner, selector, partyIndex, into);

        public void Shake(int strength, int durationMs)
        {
        }

        public void SummonMonster(IActEntity master, int index, int level, float x, float y, float z, bool facingLeft, int lifetimeMs)
        {
        }

        public List<(IActEntity owner, IActEntity target, float x, float y, float z)> Hits { get; } = new();

        public void AppendHit(IActEntity owner, IActEntity target, float x, float y, float z) => Hits.Add((owner, target, x, y, z));
    }

    private static ScriptLine[] SendMessageLines(string keyword, params object[] ints) => new[] { L("[SEND MESSAGE]"), L(keyword, ints) };

    /// <summary>触发器选 [ALL ENEMY] 后依次跑第 0 组;后面可以再带几组(组号从 1 起),给收件方用。</summary>
    private static ActProgram OnEnemiesWithGroups(ScriptLine[] first, params ScriptLine[][] more)
    {
        var parts = new List<ScriptLine[]>
        {
            Trigger(L("[FRAME]", 0, 0), L("[WHICH]"), L("[ALL ENEMY]"), L("[DO BEHAVIOR]"), L("[CHECKUP OBJECT]", 0)),
            Behavior(first),
        };
        foreach (ScriptLine[] g in more)
        {
            parts.Add(Behavior(g));
        }
        return P(Cat(parts.ToArray()));
    }

    /// <summary>收件方的程序:一个永远不触发的触发器(让运行器有东西可更新)加三组(组 0、1、2 各设一个不同的 X 速度 1、2、3)。</summary>
    private static ActProgram ReceiverProgram() => P(Cat(
        Trigger(L("[ENABLE]"), L("[OFF]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
        Behavior(Speed(1)),
        Behavior(Speed(2)),
        Behavior(Speed(3))));

    private static MsgEntity ReceiverWithRunner(ActProgram program = null)
    {
        var e = new MsgEntity();
        e.Runner = new ActRunner(program ?? ReceiverProgram(), e, new Host());
        return e;
    }

    // ------------------------------------------------------------------ [SEND DO BEHAVIOR]

    [Fact]
    public void SendDoBehaviorReadsTheValueAfterAnyMarkerAndIgnoresTheMarker()
    {
        foreach (string marker in new[] { "[ME]", "[CHECKUP OBJECT]", "[LAST ATTACKER]" })
        {
            ActBehavior b = OnlyBehavior(P(Behavior(L("[SEND DO BEHAVIOR]"), L(marker, 3))));
            Assert.Equal(ActBehaviorKind.SendDoBehavior, b.Kind);
            Assert.Equal(3, b.Msg.GroupValue.Eval(_ => 0));       // 选择器标记写什么,组号都是它后面的那个数
        }

        // 边界:值直接写在标签行上、没有选择器标记也读得进
        Assert.Equal(5, OnlyBehavior(P(Behavior(L("[SEND DO BEHAVIOR]", 5)))).Msg.GroupValue.Eval(_ => 0));
        // 边界:选择器标记后面跟随机区间,区间的 [RANDOM] 不会被当成选择器吃掉
        ActBehavior ranged = OnlyBehavior(P(Behavior(L("[SEND DO BEHAVIOR]"), L("[RANDOM]", 4, 6))));
        Assert.Equal(ActBehaviorKind.SendDoBehavior, ranged.Kind);
        Assert.Equal(5, ranged.Msg.GroupValue.Eval(n => 1));      // 区间 [4, 6]:4 + 1 = 5

        // 反例:选择器后面没有值、或什么都没写 → 不做(客户端读到 -1 组号什么也不会执行)
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(P(Behavior(L("[SEND DO BEHAVIOR]"), L("[ME]")))).Kind);
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(P(Behavior(L("[SEND DO BEHAVIOR]")))).Kind);
    }

    [Fact]
    public void SendDoBehaviorPostsToEachTargetAndRunsTheGroupOnThatTargetAlone()
    {
        // 发送方第 0 组:[SEND DO BEHAVIOR] [ME] 1 → 每个目标的当前动作执行自己的第 1 组(X 速度 2)
        ActProgram sender = OnEnemiesWithGroups(new[] { L("[SEND DO BEHAVIOR]"), L("[ME]", 1) });
        MsgEntity t1 = ReceiverWithRunner();
        MsgEntity noAction = new MsgEntity();          // 没有当前动作:收不下,但不能挡住后面的目标
        MsgEntity t2 = ReceiverWithRunner();
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { t1, noAction, t2 };
        new ActRunner(sender, new Entity(), host).Update(0, 0.016f);

        Assert.Equal(new[] { 1 }, t1.Posted.ToArray());
        Assert.Equal(new[] { 1 }, noAction.Posted.ToArray());     // 问过了,没有动作 → 跳过
        Assert.Equal(new[] { 1 }, t2.Posted.ToArray());           // 前面的目标没动作,这一个照发
        // 异步:发出去的这一次更新里什么都没执行,消息在各自运行器的信箱里
        Assert.Empty(t1.Log);
        Assert.Empty(t2.Log);
        Assert.Equal(1, t1.Runner.PendingRemote);
        Assert.Equal(1, t2.Runner.PendingRemote);

        // 收件运行器自己的下一次更新才执行;只更新 t1 时 t2 一点都没动(列表只含收件人自己)
        t1.Runner.Update(0, 0.016f);
        Assert.Equal(new[] { "speed:2,-,-" }, t1.Log.ToArray());
        Assert.Empty(t2.Log);
        Assert.Equal(0, t1.Runner.PendingRemote);
        t2.Runner.Update(0, 0.016f);
        Assert.Equal(new[] { "speed:2,-,-" }, t2.Log.ToArray());
    }

    [Fact]
    public void SendDoBehaviorEvaluatesTheGroupNumberOnceBeforeTheTargetLoop()
    {
        // [RANDOM] 1 3 = 下界 + 随机数 % (上界 - 下界)(不含上界):队列 [1, 0] 下第一次求值 = 1 + 1 % 2 = 2;
        // 若每个目标各求一次,第二个目标会是 1 + 0 % 2 = 1
        ActProgram sender = OnEnemiesWithGroups(new[] { L("[SEND DO BEHAVIOR]"), L("[ME]"), L("[RANDOM]", 1, 3) });
        MsgEntity t1 = ReceiverWithRunner();
        MsgEntity t2 = ReceiverWithRunner();
        var host = new Host();
        host.Randoms.Enqueue(1);
        host.Randoms.Enqueue(0);
        host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { t1, t2 };
        new ActRunner(sender, new Entity(), host).Update(0, 0.016f);

        Assert.Equal(new[] { 2 }, t1.Posted.ToArray());
        Assert.Equal(new[] { 2 }, t2.Posted.ToArray());           // 同一个组号
        Assert.Single(host.RandomBounds);                         // 随机数只要了一次(范围 3 - 1 = 2)
        Assert.Equal(2, host.RandomBounds[0]);
    }

    [Fact]
    public void SendDoBehaviorMessagesOutsideTheGroupRangeRunNothing()
    {
        // 收件方有 3 组(0、1、2):组 2 是最后一组(边界,执行);组 3、-1 不存在(反例,什么都不执行也不报错)
        foreach ((int group, string expected) in new[] { (2, "speed:3,-,-"), (3, null), (-1, null), (100, null) })
        {
            ActProgram sender = OnEnemiesWithGroups(new[] { L("[SEND DO BEHAVIOR]"), L("[ME]", group) });
            MsgEntity t = ReceiverWithRunner();
            var host = new Host();
            host.Sets[ActSelector.AllEnemy] = new List<IActEntity> { t };
            new ActRunner(sender, new Entity(), host).Update(0, 0.016f);
            t.Runner.Update(0, 0.016f);
            Assert.Equal(expected == null ? Array.Empty<string>() : new[] { expected }, t.Log.ToArray());
            Assert.Equal(0, t.Runner.PendingRemote);
        }
    }

    [Fact]
    public void SendDoBehaviorMessagesDieWithTheActionInstance()
    {
        MsgEntity t = ReceiverWithRunner();
        Assert.True(t.PostActGroup(1));
        Assert.Equal(1, t.Runner.PendingRemote);
        t.Runner.Begin();                                         // 收件的动作实例重新开始 = 不是原来那个动作了
        Assert.Equal(0, t.Runner.PendingRemote);
        t.Runner.Update(0, 0.016f);
        Assert.Empty(t.Log);
    }

    [Fact]
    public void QueuedMessagesRunInArrivalOrderAndAnActionChangeHoldsTheRestUntilTheActionRestarts()
    {
        // 收件方:组 0 拨帧(算"动作变了"),组 1、组 2 设 X 速度 2、3
        ActProgram p = P(Cat(
            Trigger(L("[ENABLE]"), L("[OFF]"), L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[SET FRAME]", 3)),
            Behavior(Speed(2)),
            Behavior(Speed(3))));
        MsgEntity t = ReceiverWithRunner(p);

        t.Runner.EnqueueRemoteGroup(2);
        t.Runner.EnqueueRemoteGroup(1);
        t.Runner.Update(0, 0.016f);
        Assert.Equal(new[] { "speed:3,-,-", "speed:2,-,-" }, t.Log.ToArray());      // 按到达顺序:先 2 后 1

        t.Log.Clear();
        t.Runner.EnqueueRemoteGroup(0);
        t.Runner.EnqueueRemoteGroup(1);
        t.Runner.Update(0, 0.016f);
        Assert.Equal(new[] { "frame:3" }, t.Log.ToArray());                         // 组 0 拨了帧:同一次更新里后面的消息不再执行
        Assert.Equal(1, t.Runner.PendingRemote);                                    // 没执行的留在信箱里,不丢

        t.Runner.Begin();                                                           // 动作重来:留着的作废
        Assert.Equal(0, t.Runner.PendingRemote);
        t.Runner.Update(0, 0.016f);
        Assert.Equal(new[] { "frame:3" }, t.Log.ToArray());
    }

    [Fact]
    public void SendDoBehaviorToSelfRunsOnTheNextUpdateNotInTheSameOne()
    {
        // 第 0 组:设速度 + 给自己再发第 0 组。同一次更新里不会原地再跑一遍(信箱在更新开头清一次),所以不会死循环
        ActProgram p = P(Cat(
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(L("[SEND DO BEHAVIOR]"), L("[ME]", 0), L("[SET SPEED]"), L("[X AXIS]", 4), L("[/SET SPEED]"))));
        var self = new MsgEntity();
        self.Runner = new ActRunner(p, self, new Host());

        self.Runner.Update(0, 0.016f);
        Assert.Equal(new[] { "speed:4,-,-" }, self.Log.ToArray());     // 触发器跑了一次
        Assert.Equal(1, self.Runner.PendingRemote);                     // 发给自己的那一条还在信箱里

        self.Runner.Update(0, 0.016f);                                  // 触发器的单次位还没复位(同一帧),这一次只会跑信箱里的那条
        Assert.Equal(new[] { "speed:4,-,-", "speed:4,-,-" }, self.Log.ToArray());
        Assert.Equal(1, self.Runner.PendingRemote);                     // 这一组又发了一条给自己,留到下一次
    }

    // ------------------------------------------------------------------ [SEND MESSAGE]

    [Fact]
    public void SendMessageReadsEachFamilyWithItsOwnNumberOfIntegers()
    {
        // (子关键字, 族, 文件里写的整数, 期望记下的整数):加载块里逐族的读数个数 1 / 4 / 3 / 0 / 1
        var table = new (string keyword, ActMessageFamily family, int[] written, int[] expected)[]
        {
            ("[ATTACK_COMMAND]", ActMessageFamily.Attack, new[] { 4 }, new[] { 4 }),
            ("[MOVE_COMMAND]", ActMessageFamily.Move, new[] { 1, 2, 3, 4 }, new[] { 1, 2, 3, 4 }),
            ("[ETC_COMMAND]", ActMessageFamily.Etc, new[] { 6, 1, 5000 }, new[] { 6, 1, 5000 }),
            ("[RISE_COMMAND]", ActMessageFamily.Rise, Array.Empty<int>(), Array.Empty<int>()),
            ("[DIE_COMMAND]", ActMessageFamily.Die, new[] { 7 }, new[] { 7 }),
        };
        foreach ((string keyword, ActMessageFamily family, int[] written, int[] expected) in table)
        {
            ActProgram p = P(Behavior(SendMessageLines(keyword, written.Cast<object>().ToArray())));
            ActBehavior b = OnlyBehavior(p);          // 只有一条行为:[ETC_COMMAND] 不会再单独冒出来
            Assert.Equal(ActBehaviorKind.SendMessage, b.Kind);
            Assert.Equal(family, b.Msg.Family);
            Assert.Equal(expected, b.Msg.Params);
            Assert.Empty(p.Behaviors[0].UnsupportedTags);
        }
    }

    [Fact]
    public void SendMessageIgnoresExtraNumbersAndDoesNotTurnThemIntoParameters()
    {
        // 全库有 [ATTACK_COMMAND] 写了三个数的写法:只读 1 个,多出来的两个不读
        ActBehavior attack = OnlyBehavior(P(Behavior(SendMessageLines("[ATTACK_COMMAND]", 9, 8, 7))));
        Assert.Equal(new[] { 9 }, attack.Msg.Params);
        // 反例:[RISE_COMMAND] 没有参数,后面跟的数不是它的
        ActBehavior rise = OnlyBehavior(P(Behavior(SendMessageLines("[RISE_COMMAND]", 5))));
        Assert.Empty(rise.Msg.Params);
        // 边界:整数不够时缺的按 0 读(MOVE 要 4 个,只写 2 个)
        ActBehavior move = OnlyBehavior(P(Behavior(SendMessageLines("[MOVE_COMMAND]", 3, 4))));
        Assert.Equal(new[] { 3, 4, 0, 0 }, move.Msg.Params);
    }

    [Fact]
    public void SendMessageWithoutAKnownKeywordIsASilentRecordAndKeepsTheNextBehavior()
    {
        // 没有子关键字:记录没有参数,运行时什么都不发,下一条行为不被吃掉
        ActBehaviorGroup bare = Assert.Single(P(Behavior(L("[SEND MESSAGE]"), L("[SET FRAME]", 3))).Behaviors);
        Assert.Equal(new[] { ActBehaviorKind.SendMessage, ActBehaviorKind.SetFrame }, bare.Items.Select(i => i.Kind).ToArray());
        Assert.Equal(ActMessageFamily.None, bare.Items[0].Msg.Family);
        // 不认识的子关键字:被扫描函数吃掉,后面的数不读
        ActBehaviorGroup unknown = Assert.Single(P(Behavior(L("[SEND MESSAGE]"), L("[FOO_COMMAND]", 1, 2), L("[SET FRAME]", 3))).Behaviors);
        Assert.Equal(new[] { ActBehaviorKind.SendMessage, ActBehaviorKind.SetFrame }, unknown.Items.Select(i => i.Kind).ToArray());
        Assert.Equal(ActMessageFamily.None, unknown.Items[0].Msg.Family);
    }

    private static (MsgEntity owner, MsgEntity[] targets) RunMessage(ScriptLine[] message, Action<MsgEntity> ownerSetup, params MsgEntity[] targets)
    {
        var owner = new MsgEntity();
        ownerSetup?.Invoke(owner);
        ActProgram p = OnEnemiesWithGroups(message);
        var host = new Host();
        host.Sets[ActSelector.AllEnemy] = targets.Cast<IActEntity>().ToList();
        new ActRunner(p, owner, host).Update(0, 0.016f);
        return (owner, targets);
    }

    [Fact]
    public void SendMessageReachesActiveObjectsWithAnAiSlaveOnly()
    {
        var withSlave = new MsgEntity { Kind = ActEntityKind.Monster };
        var noSlave = new MsgEntity { Kind = ActEntityKind.Monster, HasAiSlave = false };
        var passive = new MsgEntity { Kind = ActEntityKind.Passive };      // 被动对象 cast 到主动对象失败
        var hero = new MsgEntity { Kind = ActEntityKind.Character };
        RunMessage(SendMessageLines("[ATTACK_COMMAND]", 3), null, withSlave, noSlave, passive, hero);

        ActAiMessage m = Assert.Single(withSlave.Messages);
        Assert.Equal(ActMessageFamily.Attack, m.Family);
        Assert.Equal(new[] { 3 }, m.Values.ToArray());
        Assert.Null(m.CommandTarget);
        Assert.Empty(noSlave.Messages);          // 反例:没有 AI 从属
        Assert.Empty(passive.Messages);          // 反例:被动对象
        Assert.Single(hero.Messages);            // 边界:角色也是主动对象,有 AI 从属就收
    }

    [Fact]
    public void SendMessageCarriesEachFamilysIntegersInFileOrder()
    {
        var move = new MsgEntity();
        RunMessage(SendMessageLines("[MOVE_COMMAND]", 10, 20, 30, 40), null, move);
        Assert.Equal(new[] { 10, 20, 30, 40 }, Assert.Single(move.Messages).Values.ToArray());

        var rise = new MsgEntity();
        RunMessage(SendMessageLines("[RISE_COMMAND]"), null, rise);
        ActAiMessage r = Assert.Single(rise.Messages);
        Assert.Equal(ActMessageFamily.Rise, r.Family);
        Assert.Empty(r.Values);

        var die = new MsgEntity();
        RunMessage(SendMessageLines("[DIE_COMMAND]", 0), null, die);
        Assert.Equal(new[] { 0 }, Assert.Single(die.Messages).Values.ToArray());

        var none = new MsgEntity();
        RunMessage(new[] { L("[SEND MESSAGE]") }, null, none);      // 没有子关键字:不发
        Assert.Empty(none.Messages);
    }

    [Fact]
    public void EtcCommandNeedsAnAiCharacterOwnerAndCarriesCommandThenParameter()
    {
        var hero = new Entity { Kind = ActEntityKind.Character };
        var target = new MsgEntity();
        // 文件里 "6 1 5000":命令号 6、第二个数 1(带目标)、参数 5000;载荷的整数是 [命令号, 参数] = [6, 5000],带发送者当前的目标
        RunMessage(SendMessageLines("[ETC_COMMAND]", 6, 1, 5000), o => { o.IsAiCharacter = true; o.Target = hero; }, target);
        ActAiMessage m = Assert.Single(target.Messages);
        Assert.Equal(ActMessageFamily.Etc, m.Family);
        Assert.Equal(new[] { 6, 5000 }, m.Values.ToArray());
        Assert.Same(hero, m.CommandTarget);

        // 反例:拥有者不是 AICharacter → 整个目标跳过(五族里只有 ETC 查这个)
        var other = new MsgEntity();
        RunMessage(SendMessageLines("[ETC_COMMAND]", 6, 1, 5000), o => o.IsAiCharacter = false, other);
        Assert.Empty(other.Messages);
        var other2 = new MsgEntity();
        RunMessage(SendMessageLines("[ATTACK_COMMAND]", 2), o => o.IsAiCharacter = false, other2);
        Assert.Single(other2.Messages);          // 同一个拥有者发 ATTACK 照常发出

        // 目标自己要有 AI 从属
        var noSlave = new MsgEntity { HasAiSlave = false };
        RunMessage(SendMessageLines("[ETC_COMMAND]", 6, 1, 5000), o => o.IsAiCharacter = true, noSlave);
        Assert.Empty(noSlave.Messages);
    }

    [Fact]
    public void EtcCommandAttachesTheSendersTargetOnlyWhenTheSecondNumberIsOne()
    {
        var hero = new Entity { Kind = ActEntityKind.Character };
        foreach ((int flag, bool attached) in new[] { (0, false), (1, true), (2, false), (-1, false) })
        {
            var target = new MsgEntity();
            RunMessage(SendMessageLines("[ETC_COMMAND]", 6, flag, 5000), o => { o.IsAiCharacter = true; o.Target = hero; }, target);
            ActAiMessage m = Assert.Single(target.Messages);
            Assert.Equal(attached ? hero : null, m.CommandTarget);
            Assert.Equal(new[] { 6, 5000 }, m.Values.ToArray());       // 命令号与参数不随开关变
        }

        // 边界:开关是 1 但发送者没有目标 → 没有可带的,载荷里是空
        var lonely = new MsgEntity();
        RunMessage(SendMessageLines("[ETC_COMMAND]", 6, 1, 5000), o => o.IsAiCharacter = true, lonely);
        Assert.Null(Assert.Single(lonely.Messages).CommandTarget);
    }

    // ------------------------------------------------------------------ [CHANGE AI]

    [Fact]
    public void ChangeAiDiscardsTheDifficultyKeywordAndReadsRoleThenPath()
    {
        foreach (string keyword in new[] { "[NORMAL]", "[EXPERT]", "[MASTER]", "[KING]", "[HERO]", "[EASY]", "[MEDIUM]", "[HARD]", "[ULTIMATE]" })
        {
            ActBehavior b = OnlyBehavior(P(Behavior(L("[CHANGE AI]"), L(keyword, 2, "synth_a.ai"))));
            Assert.Equal(ActBehaviorKind.ChangeAi, b.Kind);
            Assert.Equal((2, "synth_a.ai"), (b.Msg.AiRole, b.Msg.AiPath));      // 关键字写什么都一样
        }

        // 边界:没有关键字,槽号与路径直接写在标签行上
        ActBehavior bare = OnlyBehavior(P(Behavior(L("[CHANGE AI]", 1, "synth_b.ai"))));
        Assert.Equal((1, "synth_b.ai"), (bare.Msg.AiRole, bare.Msg.AiPath));
        // 路径没写:读成空串,行为还在
        ActBehavior noPath = OnlyBehavior(P(Behavior(L("[CHANGE AI]"), L("[NORMAL]", 2))));
        Assert.Equal((2, string.Empty), (noPath.Msg.AiRole, noPath.Msg.AiPath));
    }

    [Fact]
    public void ChangeAiOnlyReachesAiCharactersAndAiControlledCharacters()
    {
        var aiMonster = new MsgEntity { Kind = ActEntityKind.Monster, IsAiCharacter = true };
        var plainMonster = new MsgEntity { Kind = ActEntityKind.Monster };                              // 反例:普通怪物
        var passive = new MsgEntity { Kind = ActEntityKind.Passive, IsAiCharacter = true };             // 反例:被动对象
        var aiHero = new MsgEntity { Kind = ActEntityKind.Character, IsAiControlledCharacter = true };  // 边界:带 AI 控制的角色
        var plainHero = new MsgEntity { Kind = ActEntityKind.Character };
        var noHost = new MsgEntity { Kind = ActEntityKind.Monster, IsAiCharacter = true, HasAiHost = false };
        var monsterClaimingHeroFlag = new MsgEntity { Kind = ActEntityKind.Monster, IsAiControlledCharacter = true };
        RunMessage(new[] { L("[CHANGE AI]"), L("[NORMAL]", 2, "synth_a.ai") }, null, aiMonster, plainMonster, passive, aiHero, plainHero, noHost, monsterClaimingHeroFlag);

        Assert.Equal(new[] { (2, "synth_a.ai", "test.act") }, aiMonster.AiChanges.ToArray());
        Assert.Equal(new[] { (2, "synth_a.ai", "test.act") }, aiHero.AiChanges.ToArray());
        Assert.Empty(plainMonster.AiChanges);
        Assert.Empty(passive.AiChanges);
        Assert.Empty(plainHero.AiChanges);
        Assert.Empty(noHost.AiChanges);                  // 宿主为空:这个目标跳过
        Assert.Empty(monsterClaimingHeroFlag.AiChanges); // 种类决定看哪个类标志,怪物只认 AICharacter
    }

    [Fact]
    public void ChangeAiRecordsInAGroupTakeEffectInFileOrder()
    {
        var t = new MsgEntity { Kind = ActEntityKind.Monster, IsAiCharacter = true };
        // 五个难度写五条,不筛选:全部按顺序生效,同一槽后者覆盖前者(由宿主按到达顺序覆盖);不同槽互不影响
        RunMessage(Cat(
            new[] { L("[CHANGE AI]"), L("[NORMAL]", 1, "synth_n.ai") },
            new[] { L("[CHANGE AI]"), L("[HERO]", 1, "synth_h.ai") },
            new[] { L("[CHANGE AI]"), L("[NORMAL]", 2, "synth_o.ai") }), null, t);
        Assert.Equal(new[] { (1, "synth_n.ai", "test.act"), (1, "synth_h.ai", "test.act"), (2, "synth_o.ai", "test.act") }, t.AiChanges.ToArray());
    }

    // ------------------------------------------------------------------ [CAMOUFLAGE]

    [Fact]
    public void CamouflageCompilesTwoIntegers()
    {
        ActBehavior b = OnlyBehavior(P(Behavior(L("[CAMOUFLAGE]", 1, 3000))));
        Assert.Equal(ActBehaviorKind.Camouflage, b.Kind);
        Assert.Equal((1, 3000), (b.Msg.CamouflageFlag, b.Msg.CamouflageMs));
        // 缺数时按 0 读
        ActBehavior bare = OnlyBehavior(P(Behavior(L("[CAMOUFLAGE]"))));
        Assert.Equal((0, 0), (bare.Msg.CamouflageFlag, bare.Msg.CamouflageMs));
    }

    [Fact]
    public void CamouflageIsOnOnlyWhenTheFirstNumberIsExactlyOne()
    {
        // 客户端:第一个数 == 1 才开(带第二个数),其余任何值(0、2、-1)都走关的分支
        foreach ((int flag, bool on, int expectedMs) in new[] { (1, true, 3000), (0, false, 0), (2, false, 0), (-1, false, 0) })
        {
            var t = new MsgEntity();
            RunMessage(new[] { L("[CAMOUFLAGE]", flag, 3000) }, null, t);
            Assert.Equal(new[] { (on, expectedMs) }, t.Camouflages.ToArray());
        }

        // 边界:开着、时长 0(原样交给宿主,宿主决定 0 毫秒意味着什么)
        var zero = new MsgEntity();
        RunMessage(new[] { L("[CAMOUFLAGE]", 1, 0) }, null, zero);
        Assert.Equal(new[] { (true, 0) }, zero.Camouflages.ToArray());
    }

    [Fact]
    public void CamouflageSkipsPassiveObjects()
    {
        var monster = new MsgEntity { Kind = ActEntityKind.Monster };
        var passive = new MsgEntity { Kind = ActEntityKind.Passive };
        var hero = new MsgEntity { Kind = ActEntityKind.Character };
        RunMessage(new[] { L("[CAMOUFLAGE]", 1, 500) }, null, monster, passive, hero);
        Assert.Single(monster.Camouflages);
        Assert.Empty(passive.Camouflages);       // 被动对象 cast 到主动对象失败
        Assert.Single(hero.Camouflages);
    }

    // ------------------------------------------------------------------ [APPEND HIT]

    [Fact]
    public void AppendHitCompilesThreeValueExpressions()
    {
        ActBehavior b = OnlyBehavior(P(Behavior(L("[APPEND HIT]", 10, -1, 5))));
        Assert.Equal(ActBehaviorKind.AppendHit, b.Kind);
        Assert.Equal((10, -1, 5), (b.Msg.HitForward.Eval(_ => 0), b.Msg.HitDepth.Eval(_ => 0), b.Msg.HitHeight.Eval(_ => 0)));
        // 随机区间也读得进;后两个缺了按 0
        ActBehavior ranged = OnlyBehavior(P(Behavior(L("[APPEND HIT]"), L("[RANDOM]", 0, 9))));
        Assert.Equal((4, 0, 0), (ranged.Msg.HitForward.Eval(n => 4), ranged.Msg.HitDepth.Eval(_ => 0), ranged.Msg.HitHeight.Eval(_ => 0)));
        // 一个值也没有:不做
        Assert.Equal(ActBehaviorKind.Unsupported, OnlyBehavior(P(Behavior(L("[APPEND HIT]")))).Kind);
    }

    private static (MsgHost host, Entity owner) RunAppendHit(ScriptLine[] hit, Entity owner, params Entity[] targets)
    {
        var host = new MsgHost();
        host.Inner.Sets[ActSelector.AllEnemy] = targets.Cast<IActEntity>().ToList();
        new ActRunner(OnEnemiesWithGroups(hit), owner, host).Update(0, 0.016f);
        return (host, owner);
    }

    [Fact]
    public void AppendHitLandsAheadOfTheOwnerAndMirrorsWithFacing()
    {
        var hero = new Entity { Kind = ActEntityKind.Character };
        // 拥有者 (100, 50, 0) 面朝右,偏移 (10, -1, 5):x = 100 + 10 = 110,y = 50 - 1 = 49,z = 0 + 5 = 5
        var (right, ownerR) = RunAppendHit(new[] { L("[APPEND HIT]", 10, -1, 5) }, new Entity { X = 100, Y = 50, Z = 0, FacingLeft = false }, hero);
        var hitR = Assert.Single(right.Hits);
        Assert.Same(ownerR, hitR.owner);
        Assert.Same(hero, hitR.target);
        Assert.Equal((110f, 49f, 5f), (hitR.x, hitR.y, hitR.z));

        // 面朝左:前移取反,x = 100 - 10 = 90;纵深与高度不随朝向
        var (left, _) = RunAppendHit(new[] { L("[APPEND HIT]", 10, -1, 5) }, new Entity { X = 100, Y = 50, Z = 0, FacingLeft = true }, hero);
        var hitL = Assert.Single(left.Hits);
        Assert.Equal((90f, 49f, 5f), (hitL.x, hitL.y, hitL.z));

        // 边界:全 0 偏移落在拥有者脚下;位置取整数(槽返回整数):100.9 → 100,50.9 → 50,3.9 → 3
        var (zero, _) = RunAppendHit(new[] { L("[APPEND HIT]", 0, 0, 0) }, new Entity { X = 100.9f, Y = 50.9f, Z = 3.9f }, hero);
        var hitZ = Assert.Single(zero.Hits);
        Assert.Equal((100f, 50f, 3f), (hitZ.x, hitZ.y, hitZ.z));
    }

    [Fact]
    public void AppendHitEvaluatesEachValueOnceAndHitsEveryAliveTarget()
    {
        var a = new Entity { Kind = ActEntityKind.Character };
        var dead = new Entity { Kind = ActEntityKind.Monster, IsAlive = false };
        var b = new Entity { Kind = ActEntityKind.Monster };
        var host = new MsgHost();
        host.Inner.ListDeadToo = true;           // 让宿主把死的也给出来,测运行器自己的过滤
        host.Inner.Sets[ActSelector.AllEnemy] = new List<IActEntity> { a, dead, b };
        host.Inner.Randoms.Enqueue(4);
        host.Inner.Randoms.Enqueue(7);
        var owner = new Entity { X = 200, Y = 10, Z = 0 };
        // [RANDOM] 0 9 取值 = 0 + 随机数 % 9:前移 4、纵深 7;高度 [RANDOM] 2 2 区间为空,直接取 2、不要随机数;每个值只求一次,所以两个存活目标落点相同
        new ActRunner(OnEnemiesWithGroups(new[] { L("[APPEND HIT]"), L("[RANDOM]", 0, 9), L("[RANDOM]", 0, 9), L("[RANDOM]", 2, 2) }), owner, host).Update(0, 0.016f);

        Assert.Equal(new IActEntity[] { a, b }, host.Hits.Select(h => h.target).ToArray());      // 死目标不打;顺序同目标集合
        Assert.All(host.Hits, h => Assert.Equal((204f, 17f, 2f), (h.x, h.y, h.z)));            // x = 200 + 4,y = 10 + 7,z = 0 + 2
        Assert.Equal(new[] { 9, 9 }, host.Inner.RandomBounds.ToArray());                         // 前两个值各要一次随机数(范围 9),第三个不要
    }

    [Fact]
    public void AppendHitWithoutAWorldHostIsReportedAsAnUndoneBehavior()
    {
        ActProgram p = P(Me(L("[APPEND HIT]", 1, 2, 3)));
        var host = new CountingHost();
        new ActRunner(p, new Entity(), host).Update(0, 0.016f);
        Assert.Equal(1, host.Behaviors["[APPEND HIT]"]);
    }
}
