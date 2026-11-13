using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

// [MOTION] 里叠加层([SUB ANI] 一族)与声音([SOUND]、[SOUND PROBABILITY])的结构读取与事件流。
// 条目形状取自客户端加载函数的读法;整数的含义(起始帧、层序键、声音帧、概率)是本机样本统计出来的推断。
// 脚本内容全是合成的(路径与声音标签里的 900、901 起的编号只是区分用,不对应任何真实对象)。
public sealed partial class ActRuntimeTests
{
    /// <summary>一个 [MOTION],主动画之后接 <paramref name="body"/> 里的叠加层与声音登记;不带触发器。</summary>
    private static ActProgram MotionProgram(params ScriptLine[] body)
    {
        var lines = new List<ScriptLine> { L("[MOTION]"), L("[BASE ANI]", S("syn/base900.ani")) };
        lines.AddRange(body);
        lines.Add(L("[/MOTION]"));
        return P(lines.ToArray());
    }

    private static ActMotion MotionOf(params ScriptLine[] body) => MotionProgram(body).Motions[0];

    /// <summary>
    /// 叠加层夹具:文件顺序四条 —— #0 起始帧 0 层序键 5;#1 起始帧 3 层序键 −2;#2([WITH XY])起始帧 1 层序键 0 偏移 (12, −7);#3([WITH XYZ])起始帧 3 层序键 5 偏移 (1, 2, 3)。
    /// 合成顺序(层序键升序、同键文件顺序)是 #1、#2、#0、#3。
    /// </summary>
    private static ScriptLine[] LayerFixture() => new[]
    {
        L("[SUB ANI]", S("syn/l900.ani"), 0, 5, S("syn/l901.ani"), 3, -2),
        L("[/SUB ANI]"),
        L("[SUB ANI WITH XY]", S("syn/l902.ani"), 1, 0, 12, -7),
        L("[/SUB ANI WITH XY]"),
        L("[SUB ANI WITH XYZ]", S("syn/l903.ani"), 3, 5, 1, 2, 3),
        L("[/SUB ANI WITH XYZ]"),
    };

    /// <summary>
    /// 声音夹具:文件顺序五条 —— #0 帧 2、#1 帧 0、#2 帧 2([SOUND],概率 100);#3 帧 2 概率 40、#4 帧 5 概率 100([SOUND PROBABILITY])。
    /// </summary>
    private static ScriptLine[] SoundFixture() => new[]
    {
        L("[SOUND]", S("SYN_SND_900"), 2, S("SYN_SND_901"), 0, S("SYN_SND_902"), 2),
        L("[/SOUND]"),
        L("[SOUND PROBABILITY]", S("SYN_SND_903"), 2, 40, S("SYN_SND_904"), 5, 100),
        L("[/SOUND PROBABILITY]"),
    };

    private static string Seq(IEnumerable<ActMotionEvent> events) => string.Join(" ", events.Select(e => e.ToString()));

    /// <summary>一帧一帧地推(帧号 <paramref name="from"/> 到 <paramref name="to"/>,含两端),把出来的事件合成一串。</summary>
    private static string PlayFrames(ActMotionPlayer player, int from, int to)
    {
        var all = new List<ActMotionEvent>();
        for (int f = from; f <= to; f++)
        {
            player.Advance(f, all);
        }
        return Seq(all);
    }

    private sealed class MotionHost : IActHost
    {
        public List<ActMotionEvent> Events { get; } = new();
        public List<IActEntity> Owners { get; } = new();

        public int Random(int n) => 0;

        public long NowMs => 0;

        public void SpawnPassive(IActEntity owner, int index, int level, float x, float y, float z, bool facingLeft)
        {
        }

        public void Resolve(IActEntity owner, ActSelector selector, int partyIndex, List<IActEntity> into)
        {
        }

        public void Shake(int strength, int durationMs)
        {
        }

        public void SummonMonster(IActEntity master, int index, int level, float x, float y, float z, bool facingLeft, int lifetimeMs)
        {
        }

        /// <summary>事件记在宿主里,也记进拥有者的日志(与触发器行为的日志合成同一条时间线,看先后)。</summary>
        public void MotionEvent(IActEntity owner, ActMotionEvent e)
        {
            Events.Add(e);
            Owners.Add(owner);
            if (owner is Entity en)
            {
                en.Log.Add("motion:" + e);
            }
        }
    }

    // ------------------------------------------------------------------ 结构读取

    [Fact]
    public void SubAniEntriesReadPathStartFrameAndDepthInFileOrderAcrossBlocks()
    {
        ActMotion m = MotionOf(LayerFixture());

        Assert.Equal(4, m.Layers.Count);
        Assert.Equal(new[] { "syn/l900.ani", "syn/l901.ani", "syn/l902.ani", "syn/l903.ani" }, m.Layers.Select(l => l.Path).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, m.Layers.Select(l => l.Index).ToArray());
        Assert.Equal(new[] { 0, 3, 1, 3 }, m.Layers.Select(l => l.StartFrame).ToArray());
        Assert.Equal(new[] { 5, -2, 0, 5 }, m.Layers.Select(l => l.Depth).ToArray());
        Assert.Equal(new[] { ActOverlayForm.Plain, ActOverlayForm.Plain, ActOverlayForm.WithXY, ActOverlayForm.WithXYZ }, m.Layers.Select(l => l.Form).ToArray());
        Assert.Equal(0, m.MalformedEntries);
        Assert.True(m.HasEvents);
        // 老接口不变:SubAnis 仍是全部叠加动画路径(按块、按条目顺序)
        Assert.Equal(new[] { "syn/l900.ani", "syn/l901.ani", "syn/l902.ani", "syn/l903.ani" }, m.SubAnis.ToArray());
        Assert.Equal("syn/base900.ani", m.BaseAni);
    }

    [Fact]
    public void WithXyAndWithXyzCarryTheirOffsetsAndThePlainFormHasNone()
    {
        ActMotion m = MotionOf(LayerFixture());

        Assert.Equal((0, 0, 0), (m.Layers[0].OffsetX, m.Layers[0].OffsetY, m.Layers[0].OffsetZ));
        Assert.Equal((12, -7, 0), (m.Layers[2].OffsetX, m.Layers[2].OffsetY, m.Layers[2].OffsetZ));
        Assert.Equal((1, 2, 3), (m.Layers[3].OffsetX, m.Layers[3].OffsetY, m.Layers[3].OffsetZ));
    }

    [Fact]
    public void CompositeOrderSortsByDepthThenFileOrderAndRanksFollowIt()
    {
        ActMotion m = MotionOf(LayerFixture());

        Assert.Equal(new[] { 1, 2, 0, 3 }, m.CompositeOrder.Select(l => l.Index).ToArray());
        Assert.Equal(new[] { 2, 0, 1, 3 }, m.Layers.Select(l => l.Rank).ToArray());     // 按文件顺序列出各层的名次
        Assert.Equal(new[] { 0, 1, 2, 3 }, m.CompositeOrder.Select(l => l.Rank).ToArray());
    }

    [Fact]
    public void SoundEntriesReadKeyFramePercentAndKeepOneRunningIndexAcrossBothTags()
    {
        ActMotion m = MotionOf(SoundFixture());

        Assert.Equal(5, m.Sounds.Count);
        Assert.Equal(new[] { "SYN_SND_900", "SYN_SND_901", "SYN_SND_902", "SYN_SND_903", "SYN_SND_904" }, m.Sounds.Select(s => s.Key).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, m.Sounds.Select(s => s.Index).ToArray());
        Assert.Equal(new[] { 2, 0, 2, 2, 5 }, m.Sounds.Select(s => s.Frame).ToArray());
        Assert.Equal(new[] { 100, 100, 100, 40, 100 }, m.Sounds.Select(s => s.Percent).ToArray());
        Assert.Equal(new[] { false, false, false, true, true }, m.Sounds.Select(s => s.Probabilistic).ToArray());
        Assert.Equal(0, m.MalformedEntries);
        Assert.True(m.HasEvents);
    }

    [Fact]
    public void IncompleteEntriesAreDroppedAndCountedWithoutShiftingTheOnesAroundThem()
    {
        ActMotion m = MotionOf(
            L("[SUB ANI]", S("syn/l900.ani"), 0, 1, S("syn/l901.ani"), 7, S("syn/l902.ani"), 2, 3),        // #1 缺层序键:丢掉
            L("[SUB ANI WITH XY]", S("syn/l903.ani"), 0, 0, 5),                                              // 只有三个整数:丢掉
            L("[SOUND]", S("SYN_SND_900"), 4, S("SYN_SND_901"), 6),
            L("[SOUND PROBABILITY]", S("SYN_SND_902"), 1, S("SYN_SND_903"), 2, 50),                          // 第一条缺概率:丢掉
            L("[SOUND]", 9, S("SYN_SND_904"), 8));                                                           // 开头游离的整数:记一次

        Assert.Equal(new[] { "syn/l900.ani", "syn/l902.ani" }, m.Layers.Select(l => l.Path).ToArray());
        Assert.Equal(new[] { 0, 1 }, m.Layers.Select(l => l.Index).ToArray());                              // 序号只数读得全的
        Assert.Equal(new[] { "SYN_SND_900", "SYN_SND_901", "SYN_SND_903", "SYN_SND_904" }, m.Sounds.Select(s => s.Key).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, m.Sounds.Select(s => s.Index).ToArray());
        Assert.Equal(new[] { 4, 6, 2, 8 }, m.Sounds.Select(s => s.Frame).ToArray());
        Assert.Equal(new[] { 100, 100, 50, 100 }, m.Sounds.Select(s => s.Percent).ToArray());
        Assert.Equal(4, m.MalformedEntries);
    }

    private static ScriptValue Hx(int value) => new(ScriptValueKind.Hex, value, null);

    [Fact]
    public void FloatValuesInEntriesTruncateTowardZeroAndHexValuesReadAsIntegers()
    {
        // 整数槽里写了浮点:向零截断(−2.9 是 −2,不是 −3;−0.5 是 0);写成十六进制的整数照读
        ActMotion m = MotionOf(
            L("[SUB ANI]", S("syn/l900.ani"), Fl(2.9f), Fl(-2.9f), S("syn/l901.ani"), Hx(0x10), Hx(0x0A)),
            L("[SUB ANI WITH XY]", S("syn/l902.ani"), 1, 0, Fl(-0.5f), Fl(7.99f)),
            L("[SOUND]", S("SYN_SND_900"), Fl(3.99f), S("SYN_SND_901"), Hx(0x20)),
            L("[SOUND PROBABILITY]", S("SYN_SND_902"), Fl(1.5f), Fl(49.9f)));

        Assert.Equal(new[] { 2, 16, 1 }, m.Layers.Select(l => l.StartFrame).ToArray());
        Assert.Equal(new[] { -2, 10, 0 }, m.Layers.Select(l => l.Depth).ToArray());
        Assert.Equal((0, 7), (m.Layers[2].OffsetX, m.Layers[2].OffsetY));
        Assert.Equal(new[] { 3, 32, 1 }, m.Sounds.Select(s => s.Frame).ToArray());
        Assert.Equal(new[] { 100, 100, 49 }, m.Sounds.Select(s => s.Percent).ToArray());
        Assert.Equal(0, m.MalformedEntries);
    }

    [Fact]
    public void AValueThatIsNeitherANumberNorAStringEndsTheEntryAndIsDroppedAndCounted()
    {
        // 整数槽里遇到括号词、"种类+键":这一条凑不够整数,丢掉记一次;那个游离的值本身不是字符串,不能起一条新的,再记一次;
        // 之后的字符串重新起头,不被吞。叠加层这块 2 次(条目 + 括号词),声音那块 2 次(条目 + 键值),共 4 次
        ActMotion m = MotionOf(
            L("[SUB ANI]", S("syn/l900.ani"), 0, new ScriptValue(ScriptValueKind.Paren, 0, "(UP)"), S("syn/l901.ani"), 1, 2),
            L("[SOUND]", S("SYN_SND_900"), new ScriptValue(ScriptValueKind.Keyed, 0, "KEY"), S("SYN_SND_901"), 4));

        Assert.Equal(new[] { "syn/l901.ani" }, m.Layers.Select(l => l.Path).ToArray());
        Assert.Equal(new[] { "SYN_SND_901" }, m.Sounds.Select(s => s.Key).ToArray());
        Assert.Equal(4, m.MalformedEntries);
        Assert.Equal((1, 2), (m.Layers[0].StartFrame, m.Layers[0].Depth));
        Assert.Equal(0, m.Layers[0].Index);                                                              // 序号只数读得全的
        Assert.Equal(0, m.Sounds[0].Index);
    }

    [Fact]
    public void AMotionWithoutOverlaysOrSoundsHasNoEventsAndTheProgramSaysSo()
    {
        ActProgram plain = MotionProgram();

        Assert.False(plain.Motions[0].HasEvents);
        Assert.False(plain.HasMotionEvents);
        Assert.Empty(plain.Motions[0].Layers);
        Assert.Empty(plain.Motions[0].Sounds);
        Assert.True(MotionProgram(SoundFixture()).HasMotionEvents);
        Assert.True(MotionProgram(LayerFixture()).HasMotionEvents);
        Assert.False(ActProgram.Empty.HasMotionEvents);
    }

    // ------------------------------------------------------------------ 叠加层起止

    [Fact]
    public void EachLayerStartsOnItsOwnStartFrameAndAllEndTogetherWhenTheMotionEndsInCompositeOrder()
    {
        var player = new ActMotionPlayer(MotionOf(LayerFixture()));
        var events = new List<ActMotionEvent>();

        Assert.Equal(1, player.Advance(0, events));
        Assert.Equal("LayerStart@0:layer#0", Seq(events));
        events.Clear();
        Assert.Equal(1, player.Advance(1, events));
        Assert.Equal("LayerStart@1:layer#2", Seq(events));
        events.Clear();
        Assert.Equal(0, player.Advance(2, events));
        Assert.Equal(2, player.Advance(3, events));
        Assert.Equal("LayerStart@3:layer#1 LayerStart@3:layer#3", Seq(events));          // 同一帧起的两层按合成顺序:#1(键 −2)先于 #3(键 5)
        events.Clear();
        Assert.Equal(0, player.Advance(4, events));
        Assert.Equal(4, player.ActiveLayers);

        Assert.Equal(4, player.End(events));
        Assert.Equal("LayerEnd@4:layer#1 LayerEnd@4:layer#2 LayerEnd@4:layer#0 LayerEnd@4:layer#3", Seq(events));      // 合成名次 0、1、2、3
        Assert.Equal(0, player.ActiveLayers);
    }

    [Fact]
    public void LayersStartingOnTheSameFrameComeOutInCompositeOrderNotFileOrder()
    {
        // 三层都在第 0 帧起,层序键依文件顺序是 5、−2、0:合成顺序是 #1、#2、#0
        ActMotion m = MotionOf(
            L("[SUB ANI]", S("syn/l900.ani"), 0, 5, S("syn/l901.ani"), 0, -2, S("syn/l902.ani"), 0, 0));
        var player = new ActMotionPlayer(m);

        Assert.Equal("LayerStart@0:layer#1 LayerStart@0:layer#2 LayerStart@0:layer#0", PlayFrames(player, 0, 0));
    }

    [Fact]
    public void LayersWithEqualDepthKeepFileOrder()
    {
        ActMotion m = MotionOf(
            L("[SUB ANI]", S("syn/l900.ani"), 0, 1, S("syn/l901.ani"), 0, 1, S("syn/l902.ani"), 0, 1));
        var player = new ActMotionPlayer(m);

        Assert.Equal("LayerStart@0:layer#0 LayerStart@0:layer#1 LayerStart@0:layer#2", PlayFrames(player, 0, 0));
    }

    [Fact]
    public void ALayerIsAlignedToTheMainFrameNotToTheCallThatFirstSawIt()
    {
        // 第一次更新就到第 4 帧:起始帧 0、1、3 的层补起,事件上的帧号是登记的起始帧(补帧按登记的帧报)
        var player = new ActMotionPlayer(MotionOf(LayerFixture()));
        var events = new List<ActMotionEvent>();

        player.Advance(4, events);

        Assert.Equal("LayerStart@0:layer#0 LayerStart@1:layer#2 LayerStart@3:layer#1 LayerStart@3:layer#3", Seq(events));
    }

    [Fact]
    public void ALayerWhoseStartFrameIsNeverReachedNeverStarts()
    {
        var player = new ActMotionPlayer(MotionOf(L("[SUB ANI]", S("syn/l900.ani"), 9, 0)));
        var events = new List<ActMotionEvent>();

        player.Advance(3, events);
        Assert.Empty(events);
        Assert.Equal(0, player.ActiveLayers);
        player.End(events);
        Assert.Empty(events);          // 没起过的层也没有止
    }

    [Fact]
    public void ANegativeStartFrameStartsOnTheFirstFrameAndNegativeFramesAreIgnored()
    {
        var player = new ActMotionPlayer(MotionOf(L("[SUB ANI]", S("syn/l900.ani"), -3, 0)));
        var events = new List<ActMotionEvent>();

        Assert.Equal(0, player.Advance(-1, events));       // 还没有动画帧
        Assert.Equal(-1, player.LastFrame);
        Assert.Equal(1, player.Advance(0, events));
        Assert.Equal("LayerStart@0:layer#0", Seq(events));
    }

    // ------------------------------------------------------------------ 声音

    [Fact]
    public void SoundsFireOnTheirFrameInFileOrderAndEachOnlyOnce()
    {
        var player = new ActMotionPlayer(MotionOf(SoundFixture()));
        var events = new List<ActMotionEvent>();

        player.Advance(0, events);
        Assert.Equal("Sound@0:sound#1", Seq(events));
        events.Clear();
        player.Advance(1, events);
        Assert.Empty(events);
        player.Advance(2, events);
        Assert.Equal("Sound@2:sound#0 Sound@2:sound#2 Sound@2:sound#3", Seq(events));        // 同一帧三条按文件顺序
        events.Clear();
        player.Advance(2, events);                                                              // 同一帧再来一次不重复
        Assert.Empty(events);
        player.Advance(3, events);
        player.Advance(4, events);
        Assert.Empty(events);
        player.Advance(5, events);
        Assert.Equal("Sound@5:sound#4", Seq(events));
        Assert.Equal("SYN_SND_904", events[0].Key);
        Assert.Equal(100, events[0].Sound.Percent);
    }

    [Fact]
    public void AProbabilitySoundIsReportedWithItsPercentAndNeverRolledByThePlayer()
    {
        var player = new ActMotionPlayer(MotionOf(L("[SOUND PROBABILITY]", S("SYN_SND_900"), 1, 0, S("SYN_SND_901"), 1, 40)));
        var events = new List<ActMotionEvent>();

        player.Advance(1, events);

        Assert.Equal("Sound@1:sound#0 Sound@1:sound#1", Seq(events));                           // 概率 0 的也照出:掷骰是宿主的事
        Assert.Equal(new[] { 0, 40 }, events.Select(e => e.Sound.Percent).ToArray());
    }

    [Fact]
    public void SkippedFramesAreCaughtUpInOrderAndReportTheRegisteredFrame()
    {
        var player = new ActMotionPlayer(MotionOf(SoundFixture()));
        var events = new List<ActMotionEvent>();

        player.Advance(5, events);       // 第一次更新就到第 5 帧:0 到 5 帧里登记的声音按帧、再按文件顺序补齐

        Assert.Equal("Sound@0:sound#1 Sound@2:sound#0 Sound@2:sound#2 Sound@2:sound#3 Sound@5:sound#4", Seq(events));
        Assert.Equal(5, player.LastFrame);
    }

    [Fact]
    public void ASoundOnOrBeyondTheFrameCountNeverFiresAndASoundOnFrameZeroFiresOnTheFirstUpdate()
    {
        var player = new ActMotionPlayer(MotionOf(L("[SOUND]", S("SYN_SND_900"), 0, S("SYN_SND_901"), 7)));

        Assert.Equal("Sound@0:sound#0", PlayFrames(player, 0, 3));        // 主动画只有 4 帧(0 到 3):第 7 帧的永远不响
    }

    [Fact]
    public void ALoopingAnimationRearmsSoundsOnEveryPassButDoesNotRestartLayers()
    {
        var player = new ActMotionPlayer(MotionOf(
            L("[SUB ANI]", S("syn/l900.ani"), 0, 0, S("syn/l901.ani"), 2, 0),
            L("[SOUND]", S("SYN_SND_900"), 0, S("SYN_SND_901"), 2)));

        // 第一圈 0、1、2;第二圈 0、1、2(帧号变小就是新的一圈)
        Assert.Equal(
            "LayerStart@0:layer#0 Sound@0:sound#0 LayerStart@2:layer#1 Sound@2:sound#1 Sound@0:sound#0 Sound@2:sound#1",
            PlayFrames(player, 0, 2) + " " + PlayFrames(player, 0, 2));
        Assert.Equal(2, player.ActiveLayers);
    }

    [Fact]
    public void ARepeatedFrameDoesNotCountAsANewPassButGoingBackOneFrameDoes()
    {
        var player = new ActMotionPlayer(MotionOf(L("[SOUND]", S("SYN_SND_900"), 1)));
        var events = new List<ActMotionEvent>();

        player.Advance(1, events);
        player.Advance(1, events);
        Assert.Single(events);
        player.Advance(0, events);           // 回退一帧:新的一圈
        Assert.Single(events);
        player.Advance(1, events);
        Assert.Equal("Sound@1:sound#0 Sound@1:sound#0", Seq(events));
    }

    // ------------------------------------------------------------------ 结束与重来

    [Fact]
    public void EndOnlyClosesOnceAndTheStreamStaysQuietUntilBeginAndThenStartsOver()
    {
        var player = new ActMotionPlayer(MotionOf(
            L("[SUB ANI]", S("syn/l900.ani"), 0, 0),
            L("[SOUND]", S("SYN_SND_900"), 1)));
        var events = new List<ActMotionEvent>();

        player.Advance(1, events);
        Assert.Equal("LayerStart@0:layer#0 Sound@1:sound#0", Seq(events));
        events.Clear();
        Assert.Equal(1, player.End(events));
        Assert.Equal("LayerEnd@1:layer#0", Seq(events));
        Assert.True(player.Ended);
        events.Clear();
        Assert.Equal(0, player.End(events));              // 只收一次
        Assert.Equal(0, player.Advance(2, events));       // 收了之后什么都不出
        Assert.Empty(events);

        player.Begin();                                   // 重新开始:全部回到没起、没响
        Assert.False(player.Ended);
        Assert.Equal(-1, player.LastFrame);
        Assert.Equal("LayerStart@0:layer#0 Sound@1:sound#0", PlayFrames(player, 0, 1));
    }

    [Fact]
    public void EndBeforeAnyFrameReportsNothingAndEndsAreStampedWithTheLastFrameSeen()
    {
        var player = new ActMotionPlayer(MotionOf(L("[SUB ANI]", S("syn/l900.ani"), 0, 0)));
        var events = new List<ActMotionEvent>();

        Assert.Equal(0, player.End(events));
        player.Begin();
        player.Advance(2, events);
        events.Clear();
        player.End(events);
        Assert.Equal("LayerEnd@2:layer#0", Seq(events));
    }

    // ------------------------------------------------------------------ 运行器接线

    [Fact]
    public void ARunnerReportsMotionEventsToTheHostEvenWhenTheProgramHasNoTriggers()
    {
        ActProgram p = MotionProgram(Cat(LayerFixture(), SoundFixture()));
        Assert.Empty(p.Triggers);
        var e = new Entity();
        var host = new MotionHost();
        var runner = new ActRunner(p, e, host);

        Assert.True(runner.HasMotionEvents);
        for (int f = 0; f <= 5; f++)
        {
            runner.Update(f, 0.016f);
        }

        Assert.Equal(
            "LayerStart@0:layer#0 Sound@0:sound#1 LayerStart@1:layer#2 Sound@2:sound#0 Sound@2:sound#2 Sound@2:sound#3 "
            + "LayerStart@3:layer#1 LayerStart@3:layer#3 Sound@5:sound#4",
            Seq(host.Events));
        Assert.Equal(9, runner.MotionEventsSent);
        Assert.All(host.Owners, o => Assert.Same(e, o));
    }

    [Fact]
    public void ARunnerWithoutOverlaysOrSoundsNeverCreatesAnEventStream()
    {
        var host = new MotionHost();
        var runner = new ActRunner(MotionProgram(), new Entity(), host);

        runner.Update(0, 0.016f);
        runner.Update(1, 0.016f);
        runner.EndMotion();

        Assert.False(runner.HasMotionEvents);
        Assert.Empty(host.Events);
        Assert.Equal(0, runner.MotionEventsSent);
    }

    [Fact]
    public void OnlyTheFirstMotionBlockFeedsTheEventStreamAndALaterOneIsReadButNotPlayed()
    {
        ScriptLine[] plain = { L("[MOTION]"), L("[BASE ANI]", S("syn/base900.ani")), L("[/MOTION]") };
        ScriptLine[] withSound = { L("[MOTION]"), L("[BASE ANI]", S("syn/base901.ani")), L("[SOUND]", S("SYN_SND_900"), 0), L("[/SOUND]"), L("[/MOTION]") };

        // 第一个 [MOTION] 没登记、第二个才有:结构照读,事件流只认第一个(主动画所在的那个),所以一个事件也没有
        ActProgram late = P(Cat(plain, withSound));
        Assert.Equal(2, late.Motions.Count);
        Assert.True(late.Motions[1].HasEvents);
        Assert.False(late.HasMotionEvents);
        var lateHost = new MotionHost();
        var lateRunner = new ActRunner(late, new Entity(), lateHost);
        lateRunner.Update(0, 0.016f);
        Assert.False(lateRunner.HasMotionEvents);
        Assert.Empty(lateHost.Events);

        // 反过来:第一个有登记,事件照出(第二个没有登记,不添事件)
        ActProgram early = P(Cat(withSound, plain));
        Assert.True(early.HasMotionEvents);
        var earlyHost = new MotionHost();
        new ActRunner(early, new Entity(), earlyHost).Update(0, 0.016f);
        Assert.Equal("Sound@0:sound#0", Seq(earlyHost.Events));
    }

    [Fact]
    public void MotionEventsAndTriggersRunOnTheSameFrameAndTheMotionEventsComeFirst()
    {
        ActProgram p = P(Cat(
            new[] { L("[MOTION]"), L("[BASE ANI]", S("syn/base900.ani")), L("[SOUND]", S("SYN_SND_900"), 1), L("[/MOTION]") },
            Trigger(L("[FRAME]", 1, 1), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(5))));
        var e = new Entity();
        var runner = new ActRunner(p, e, new MotionHost());

        runner.Update(0, 0.016f);
        Assert.Empty(e.Log);
        runner.Update(1, 0.016f);

        Assert.Equal(new[] { "motion:Sound@1:sound#0", "speed:5,-,-" }, e.Log.ToArray());
        Assert.Equal(1, runner.TotalFired);
    }

    [Fact]
    public void ARunnerCatchesUpSkippedFramesForTheEventStreamToo()
    {
        var host = new MotionHost();
        var runner = new ActRunner(MotionProgram(SoundFixture()), new Entity(), host);

        runner.Update(3, 0.05f);          // 一口气跳到第 3 帧
        Assert.Equal("Sound@0:sound#1 Sound@2:sound#0 Sound@2:sound#2 Sound@2:sound#3", Seq(host.Events));
        runner.Update(6, 0.05f);
        Assert.Equal("Sound@0:sound#1 Sound@2:sound#0 Sound@2:sound#2 Sound@2:sound#3 Sound@5:sound#4", Seq(host.Events));
    }

    [Fact]
    public void EndMotionClosesTheStartedLayersOnceAndALaterUpdateStaysQuiet()
    {
        var host = new MotionHost();
        var runner = new ActRunner(MotionProgram(LayerFixture()), new Entity(), host);

        runner.Update(1, 0.016f);
        host.Events.Clear();
        runner.EndMotion();
        Assert.Equal("LayerEnd@1:layer#2 LayerEnd@1:layer#0", Seq(host.Events));        // 起过的只有 #0 与 #2;按合成名次 #2(键 0)先于 #0(键 5)
        host.Events.Clear();
        runner.EndMotion();
        runner.Update(4, 0.016f);
        Assert.Empty(host.Events);
    }

    [Fact]
    public void BeginClosesWhatWasStartedAndTheNewRunStartsFromTheFirstFrame()
    {
        var host = new MotionHost();
        var runner = new ActRunner(MotionProgram(L("[SUB ANI]", S("syn/l900.ani"), 0, 0), L("[SOUND]", S("SYN_SND_900"), 0)), new Entity(), host);

        runner.Update(0, 0.016f);
        Assert.Equal("LayerStart@0:layer#0 Sound@0:sound#0", Seq(host.Events));
        host.Events.Clear();

        runner.Begin();         // 动作重来:上一轮挂着的叠加层先收尾
        Assert.Equal("LayerEnd@0:layer#0", Seq(host.Events));
        host.Events.Clear();
        runner.Update(0, 0.016f);
        Assert.Equal("LayerStart@0:layer#0 Sound@0:sound#0", Seq(host.Events));
    }

    [Fact]
    public void ThePlainHostDefaultIgnoresMotionEventsSoExistingHostsNeedNoChange()
    {
        // 老宿主(没有实现 MotionEvent)照常跑:事件被默认实现吞掉,运行器的触发器不受影响
        ActProgram p = P(Cat(
            new[] { L("[MOTION]"), L("[BASE ANI]", S("syn/base900.ani")), L("[SOUND]", S("SYN_SND_900"), 0), L("[/MOTION]") },
            Trigger(L("[FRAME]", 0, 0), L("[DO BEHAVIOR]"), L("[ME]", 0)),
            Behavior(Speed(9))));
        var e = new Entity();
        var runner = new ActRunner(p, e, new Host());

        runner.Update(0, 0.016f);

        Assert.Equal(new[] { "speed:9,-,-" }, e.Log.ToArray());
        Assert.Equal(1, runner.MotionEventsSent);
    }
}
