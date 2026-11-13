using Dnf.Game.Act;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// SUB ANI 线·收口:动作有触发器时事件流由运行器出,开始过的叠加层要在这段动作结束的每一条路上各收一次口(各出一个"结束"):
/// 被动对象消失、被动对象换动作、站立动作被打断、最后一幕放完。声音与叠加层的帧都按各自动画的主层帧对齐。
/// 夹具全是合成的:对象 900(爆炸动画 3 帧、每帧 100 毫秒;叠加层帧 0,声音 930 帧 1、931 帧 2)、对象 901(帧 1 换动作,见 <see cref="AvatarFixture.ActMotionSwapId"/>)、
/// 怪物 f_staymotion(站立动画循环,2 帧、每帧 100 毫秒;叠加层帧 0,声音 940 帧 1)、怪物 f_lastmotion(最后一幕 3 帧、每帧 400 毫秒;叠加层帧 0,声音 950 帧 2)。
/// 每一步 10 毫秒;主角站在 (300, 100),怪物缺省在 (340, 100)。
/// </summary>
public partial class PassiveObjectTests
{
    /// <summary>旁路接下的事件(拥有者加事件),按到达先后。</summary>
    private sealed class MotionTap
    {
        public MotionTap(RoomCombatSession session)
        {
            session.ActMotionObserver = (owner, e) => All.Add((owner, e));
        }

        public List<(IActEntity Owner, ActMotionEvent Event)> All { get; } = new();

        /// <summary>每条事件写成"事件码@帧:叠加动画路径或声音标签",空格隔开。</summary>
        public string Tape() => string.Join(" ", All.Select(x => x.Event.Code + "@" + x.Event.Frame + ":" + x.Event.Key));

        public int Count(ActMotionEventCode code) => All.Count(x => x.Event.Code == code);
    }

    /// <summary>每步 10 毫秒推进到被动对象 <paramref name="o"/> 从会话表里拿掉为止,逐步记下它的动画帧与累计事件数;返回用了几步。</summary>
    private static int WalkUntilRetired(ActArena arena, PassiveObject o, MotionTap tap, List<int> frames, List<int> counts)
    {
        while (arena.Session.Passives.Contains(o))
        {
            arena.Steps(1);
            frames.Add(o.CurrentFrame);
            counts.Add(tap.All.Count);
            Assert.True(frames.Count < 200, "对象没有在预期的步数里消失");
        }
        return frames.Count;
    }

    private static SoundCue[] SynCues(ActArena arena)
    {
        var all = new List<SoundCue>();
        arena.Session.DrainSounds(all);
        return all.Where(c => c.Key.StartsWith("SYN_SND_", StringComparison.Ordinal)).ToArray();
    }

    // ------------------------------------------------------------------ 被动对象:消失时收口

    [Fact]
    public void ASpawnedObjectWithTriggersReportsItsEventsOnItsOwnFramesAndClosesTheLayerWhenItDisappears()
    {
        using var arena = new ActArena("spawn900", useVitals: false);
        MonsterActor m = arena.Monster;
        var tap = new MotionTap(arena.Session);

        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);
        PassiveObject o = Assert.Single(arena.Session.Passives);
        Assert.Equal(AvatarFixture.ActMotionObjId, o.Template.Index);
        Assert.InRange(tap.All.Count, 0, 1);                                     // 放出来的这一步里对象最多推进过一次(看会话先推怪物还是先推对象),至多只有叠加层的起头

        var frames = new List<int>();
        var counts = new List<int>();
        int steps = WalkUntilRetired(arena, o, tap, frames, counts);

        // 事件序列:叠加层帧 0 起,声音按登记帧各响一次,对象消失时叠加层收口(盖的是最后看到的帧 2)
        Assert.Equal("LayerStart@0:syn/l930.ani Sound@1:SYN_SND_930 Sound@2:SYN_SND_931 LayerEnd@2:syn/l930.ani", tap.Tape());
        Assert.InRange(steps, 28, 31);                                           // 爆炸动画 300 毫秒(放出来的那一步若已推进过,少走一步)

        // 逐步对齐:第一次推进就起叠加层;每个声音出现在动画帧第一次到登记帧的那一步;收口在对象被拿掉的那一步
        int s1 = frames.FindIndex(f => f >= 1);
        int s2 = frames.FindIndex(f => f >= 2);
        Assert.Equal(1, counts[0]);
        Assert.Equal(1, counts[s1 - 1]);
        Assert.Equal(2, counts[s1]);
        Assert.Equal(2, counts[s2 - 1]);
        Assert.Equal(3, counts[s2]);
        Assert.Equal(3, counts[steps - 2]);
        Assert.Equal(4, counts[steps - 1]);

        // 拥有者是对象本身,声音的"主人"取到放它出来的怪物(序号 + 1)
        Assert.All(tap.All, x =>
        {
            Assert.Same(o, x.Owner);
            Assert.Same(m, MonsterActor.From(x.Owner));
        });
        SoundCue[] cues = SynCues(arena);
        Assert.Equal(new[] { "SYN_SND_930", "SYN_SND_931" }, cues.Select(c => c.Key).ToArray());
        Assert.All(cues, c =>
        {
            Assert.Equal(SoundCueKind.Effect, c.Kind);
            Assert.Equal(m.Index + 1, c.Owner);
        });
        Assert.Equal((1, 1), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));
        Assert.Equal((2, 2, 0), (arena.Session.ActSoundEvents, arena.Session.ActSoundsQueued, arena.Session.ActSoundsSkipped));

        // 消失之后安静,收口只一次
        arena.Steps(30);
        Assert.Equal(4, tap.All.Count);
    }

    [Fact]
    public void EachSpawnedObjectGetsAFreshEventStreamAndTheSequenceRepeats()
    {
        using var arena = new ActArena("spawn900", useVitals: false);
        var tap = new MotionTap(arena.Session);
        var scratchFrames = new List<int>();
        var scratchCounts = new List<int>();

        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);
        WalkUntilRetired(arena, Assert.Single(arena.Session.Passives), tap, scratchFrames, scratchCounts);
        string first = tap.Tape();

        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 1, 400) > 0);      // 冷却 1500 毫秒后的第二次出招
        scratchFrames.Clear();
        scratchCounts.Clear();
        WalkUntilRetired(arena, Assert.Single(arena.Session.Passives), tap, scratchFrames, scratchCounts);

        Assert.Equal(first + " " + first, tap.Tape());
        Assert.Equal((2, 2), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));
    }

    // ------------------------------------------------------------------ 被动对象:换动作时收口

    [Fact]
    public void AnObjectThatSwitchesActionClosesTheOldActionsLayerAtTheSwitchAndStartsTheNewOneFromItsFirstFrame()
    {
        using var arena = new ActArena("spawn901", useVitals: false);
        MonsterActor m = arena.Monster;
        var tap = new MotionTap(arena.Session);

        Assert.True(arena.Until(() => arena.Session.PassivesSpawned > 0) > 0);
        PassiveObject o = Assert.Single(arena.Session.Passives);
        Assert.Equal(AvatarFixture.ActMotionSwapId, o.Template.Index);

        var frames = new List<int>();
        var counts = new List<int>();
        int steps = WalkUntilRetired(arena, o, tap, frames, counts);

        // 基础动作:叠加层帧 0 起;帧 1 触发器换动作,换的那一刻先把它的叠加层收口(盖帧 1);帧 2 的声音 935 没响到就不再响。
        // 自定义动作:一出来就从自己的帧 0 重新起叠加层,声音 936 在它自己的帧 1,播完对象消失时再收口(盖帧 2)
        Assert.Equal(
            "LayerStart@0:syn/l935.ani LayerEnd@1:syn/l935.ani LayerStart@0:syn/l936.ani Sound@1:SYN_SND_936 LayerEnd@2:syn/l936.ani",
            tap.Tape());

        int swap = counts.FindIndex(c => c >= 2);
        Assert.InRange(swap, 7, 10);                                             // 基础动作的帧 1 从 100 毫秒起
        Assert.Equal(1, counts[swap - 1]);
        Assert.Equal(0, frames[swap]);                                           // 切过去的那一步,新动作的时钟是 0
        Assert.Equal(3, counts[swap + 1]);                                       // 新动作下一步(它第一次推进)就起叠加层
        Assert.Equal(4, counts[frames.FindIndex(swap + 1, f => f >= 1)]);        // 自定义动作自己的帧 1:声音 936
        Assert.Equal(4, counts[steps - 2]);
        Assert.Equal(5, counts[steps - 1]);                                      // 对象消失那一步收口

        SoundCue[] cues = SynCues(arena);
        Assert.Equal(new[] { "SYN_SND_936" }, cues.Select(c => c.Key).ToArray());
        Assert.Equal(m.Index + 1, cues.Single().Owner);
        Assert.Equal((2, 2), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));
        Assert.Equal((1, 1, 0), (arena.Session.ActSoundEvents, arena.Session.ActSoundsQueued, arena.Session.ActSoundsSkipped));
    }

    // ------------------------------------------------------------------ 站立动作:每圈声音重来、叠加层只起一次,被打断时收口

    [Fact]
    public void AStandingActPlaysItsSoundOnEveryLapAndStartsItsLayerOnceAndTheHitThatStopsTheFamilyClosesIt()
    {
        // 怪物在 x=900:主角在视野外,怪物一直站着;站立动画循环,每圈 200 毫秒
        using var arena = new ActArena("f_staymotion", monsterX: 900, useVitals: false);
        MonsterActor m = arena.Monster;
        var tap = new MotionTap(arena.Session);
        var frames = new List<int>();
        var counts = new List<int>();
        for (int i = 0; i < 100; i++)
        {
            arena.Steps(1);
            Assert.Equal(MonsterPhase.Wait, m.Phase);
            frames.Add(m.CurrentFrame);
            counts.Add(tap.All.Count);
        }

        // 第一步起叠加层(帧 0);之后事件只有声音,每一次动画帧从别的帧走到帧 1 的那一步响一次,别的步一个事件也没有
        Assert.Equal(1, counts[0]);
        Assert.Equal(ActMotionEventCode.LayerStart, tap.All[0].Event.Code);
        Assert.Equal(0, tap.All[0].Event.Frame);
        int laps = 0;
        for (int i = 1; i < frames.Count; i++)
        {
            bool lap = frames[i] == 1 && frames[i - 1] != 1;
            laps += lap ? 1 : 0;
            Assert.Equal(lap ? 1 : 0, counts[i] - counts[i - 1]);
        }
        Assert.True(laps >= 4, "100 步里站立动画应当转了不止 4 圈");
        Assert.Equal(laps, tap.Count(ActMotionEventCode.Sound));
        Assert.All(tap.All.Skip(1), x =>
        {
            Assert.Equal(ActMotionEventCode.Sound, x.Event.Code);
            Assert.Equal(1, x.Event.Frame);
            Assert.Equal("SYN_SND_940", x.Event.Key);
            Assert.Same(m, MonsterActor.From(x.Owner));
        });
        Assert.Equal((1, 0), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));       // 动画转圈不让叠加层重来,也不收口
        Assert.Equal(laps, SynCues(arena).Length);

        // 挨一下:当前动作换成受击(这一族没有触发器程序),站立的运行器停掉,叠加层在下一步收口(盖它最后看到的帧)
        int lastFrame = frames[^1];
        m.ApplyHit(1, false, 0, false);
        Assert.NotEqual(MonsterPhase.Wait, m.Phase);
        arena.Steps(1);
        Assert.Equal((1, 1), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));
        Assert.Equal(ActMotionEventCode.LayerEnd, tap.All[^1].Event.Code);
        Assert.Equal(lastFrame, tap.All[^1].Event.Frame);
        Assert.Equal("syn/l940.ani", tap.All[^1].Event.Key);

        // 受击结束又站回来:新的站立运行器从头来,叠加层再起
        Assert.True(arena.Until(() => arena.Session.ActLayerStarts == 2, 2000) > 0);
        Assert.Equal(MonsterPhase.Wait, m.Phase);
        Assert.Equal(ActMotionEventCode.LayerStart, tap.All[^1].Event.Code);
        Assert.Equal(0, tap.All[^1].Event.Frame);
        Assert.Equal(1, arena.Session.ActLayerEnds);
    }

    [Fact]
    public void DyingWhileStandingClosesTheStandingLayerOnTheSameStepTheHealthRunsOutAndNothingMoreIsReported()
    {
        using var arena = new ActArena("f_staymotion", monsterX: 900, useVitals: false);
        MonsterActor m = arena.Monster;
        var tap = new MotionTap(arena.Session);
        arena.Steps(25);                                                         // 站 250 毫秒:叠加层起了,帧 1(100 毫秒、300 毫秒起)的声音响过一次
        Assert.Equal((1, 0), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));
        Assert.Equal(1, tap.Count(ActMotionEventCode.Sound));

        int before = tap.All.Count;
        Assert.True(m.ApplyHit(1000, false, 0, false));
        arena.Steps(1);

        Assert.Equal((1, 1), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));
        Assert.Equal(before + 1, tap.All.Count);
        Assert.Equal(ActMotionEventCode.LayerEnd, tap.All[^1].Event.Code);
        Assert.Equal("syn/l940.ani", tap.All[^1].Event.Key);
        Assert.Same(m, MonsterActor.From(tap.All[^1].Owner));

        arena.Steps(150);                                                        // 这只怪物没有最后一幕程序:身体消失之后也不再有事件
        Assert.Equal(MonsterPhase.Gone, m.Phase);
        Assert.Equal(before + 1, tap.All.Count);
    }

    // ------------------------------------------------------------------ 出招阶段里连招:切走的那一招收口

    [Fact]
    public void ChainingToTheNextAttackClosesTheLayersOfTheOneThatWasCutOffAndTheNextOneStartsFromItsFirstFrame()
    {
        using var arena = new ActArena("motionchain", useVitals: false);
        MonsterActor m = arena.Monster;
        var tap = new MotionTap(arena.Session);

        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.Equal(0, m.AttackIndex);
        Assert.True(arena.Until(() => m.AttackIndex == 1) > 0);
        Assert.Equal(MonsterPhase.Attack, m.Phase);                              // 同一个出招阶段里切的招
        Assert.Equal(1, arena.Session.ActLayerEnds);                             // 第 0 招的叠加层在切招的那一步就收了口,不等出招阶段结束
        Assert.True(arena.Until(() => m.Phase != MonsterPhase.Attack) > 0);

        // 第 0 招:叠加层帧 0 起,帧 1 触发器切第 1 招,切的那一刻收口(盖帧 1),帧 2 的声音 945 没响到;
        // 第 1 招(也有触发器):从自己的帧 0 重新起叠加层,声音 910 在它的帧 1,离开出招阶段时收口(盖最后看到的帧 3)
        Assert.Equal(
            "LayerStart@0:syn/l945.ani LayerEnd@1:syn/l945.ani LayerStart@0:syn/l910.ani Sound@1:SYN_SND_910 LayerEnd@3:syn/l910.ani",
            tap.Tape());
        Assert.All(tap.All, x => Assert.Same(m, MonsterActor.From(x.Owner)));
        SoundCue cue = Assert.Single(SynCues(arena));
        Assert.Equal(("SYN_SND_910", m.Index + 1), (cue.Key, cue.Owner));
        Assert.Equal((2, 2), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));
    }

    // ------------------------------------------------------------------ 最后一幕:放完收口,声音可以晚于身体消失

    [Fact]
    public void TheLastActPlaysItsEventsAfterTheBodyIsGoneAndClosesItsLayerWhenTheLastSceneEnds()
    {
        using var arena = new ActArena("f_lastmotion", useVitals: false);
        MonsterActor m = arena.Monster;
        var tap = new MotionTap(arena.Session);
        arena.Steps(3);
        Assert.Empty(tap.All);                                                   // 活着时最后一幕不放,没有事件

        Assert.True(m.ApplyHit(1000, false, 0, false));
        var phases = new List<MonsterPhase>();
        var counts = new List<int>();
        var running = new List<bool>();
        bool started = false;
        for (int i = 0; i < 400 && !(started && !m.LastActRunning); i++)
        {
            arena.Steps(1);
            started |= m.LastActRunning;
            phases.Add(m.Phase);
            counts.Add(tap.All.Count);
            running.Add(m.LastActRunning);
        }
        Assert.True(started);
        Assert.False(m.LastActRunning);

        // 叠加层帧 0 在最后一幕开始那一步起;声音 950 在最后一幕自己的帧 2(800 毫秒),那时身体(500 毫秒)早消失了;放完收口盖帧 2
        Assert.Equal("LayerStart@0:syn/l950.ani Sound@2:SYN_SND_950 LayerEnd@2:syn/l950.ani", tap.Tape());
        int start = counts.FindIndex(c => c >= 1);
        int sound = counts.FindIndex(c => c >= 2);
        int end = counts.FindIndex(c => c >= 3);
        Assert.InRange(start, 0, 2);
        Assert.Equal(MonsterPhase.Gone, phases[sound]);
        Assert.NotEqual(MonsterPhase.Gone, phases[start]);
        Assert.InRange(sound - start, 78, 82);                                   // 帧 2 从最后一幕的 800 毫秒起
        Assert.Equal(running.Count - 1, end);                                    // 收口就在最后一幕放完(运行器撤掉)的那一步
        Assert.False(running[end]);
        Assert.True(running[end - 1]);
        Assert.InRange(end - start, 138, 142);                                   // 1200 毫秒动画再留 200 毫秒
        Assert.All(tap.All, x => Assert.Same(m, MonsterActor.From(x.Owner)));
        SoundCue cue = Assert.Single(SynCues(arena));
        Assert.Equal(("SYN_SND_950", SoundCueKind.Effect, m.Index + 1), (cue.Key, cue.Kind, cue.Owner));
        Assert.Equal((1, 1), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));

        arena.Steps(50);
        Assert.Equal(3, tap.All.Count);                                          // 收口只一次
    }
}
