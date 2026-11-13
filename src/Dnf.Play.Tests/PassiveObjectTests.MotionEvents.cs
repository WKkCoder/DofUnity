using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// SUB ANI 线:.act 的 [MOTION] 里叠加层与声音的事件流在房间会话里的落点(声音进声音队列,叠加层只登记个数)。
/// 怪物与 .act 全是合成的(见 <see cref="AvatarFixture"/> 的 motionfeed、motiontrig、f_custmotion、f_swapmotion;声音标签与叠加动画路径里的 900、910、920 编号只是区分用)。
/// 出招动画是 4 帧、每帧 80 毫秒(自定义动作 3 帧、每帧 80 毫秒);主角站在 (300, 100),怪物在 (340, 100) 朝左;每一步 10 毫秒。
/// motionfeed 的登记:叠加层 #0 帧 1、#1 帧 2;声音 904 帧 0、900 帧 1、902 帧 2(概率 100)、903 帧 2(概率 0)、901 帧 3。
/// </summary>
public partial class PassiveObjectTests
{
    /// <summary>一次出招(或自定义动作)里逐步的记录:那一步的动画帧、取走的合成声音、叠加层开始与结束的累计个数。</summary>
    private sealed class MotionTrace
    {
        public List<int> Frames { get; } = new();
        public List<SoundCue[]> Cues { get; } = new();
        public List<int> LayerStarts { get; } = new();
        public List<int> LayerEnds { get; } = new();

        public int Steps => Frames.Count;

        /// <summary>动画帧第一次不小于 <paramref name="frame"/> 的那一步的序号;没有是 -1。</summary>
        public int FirstStepAtFrame(int frame) => Frames.FindIndex(f => f >= frame);

        /// <summary>声音 <paramref name="key"/> 出现在哪几步。</summary>
        public int[] StepsWith(string key) => Enumerable.Range(0, Cues.Count).Where(i => Cues[i].Any(c => c.Key == key)).ToArray();

        public IEnumerable<SoundCue> AllCues => Cues.SelectMany(c => c);
    }

    /// <summary>
    /// 每步 10 毫秒推进到 <paramref name="running"/> 为假(离开这个动作)为止,逐步记录;最后那一步(已经离开)的动画帧记成 <paramref name="lastFrame"/>。
    /// 只收合成声音(标签以 SYN_SND_ 开头),别的声音(怪物叫声等)不管。
    /// </summary>
    private static MotionTrace TraceWhile(ActArena arena, Func<bool> running, int lastFrame, int max = 300)
    {
        var trace = new MotionTrace();
        var buffer = new List<SoundCue>();
        for (int i = 0; i < max; i++)
        {
            arena.Session.Update(10, 0, 0, false, false);
            bool on = running();
            buffer.Clear();
            arena.Session.DrainSounds(buffer);
            trace.Frames.Add(on ? arena.Monster.CurrentFrame : lastFrame);
            trace.Cues.Add(buffer.Where(c => c.Key.StartsWith("SYN_SND_", StringComparison.Ordinal)).ToArray());
            trace.LayerStarts.Add(arena.Session.ActLayerStarts);
            trace.LayerEnds.Add(arena.Session.ActLayerEnds);
            if (!on)
            {
                break;
            }
        }
        Assert.True(trace.Steps < max, "动作没有在预期的步数里结束");
        return trace;
    }

    private static MotionTrace TraceAttack(ActArena arena)
    {
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        return TraceWhile(arena, () => m.Phase == MonsterPhase.Attack, 3);
    }

    /// <summary>motionfeed 的声音登记:(标签, 帧)。概率 0 的 903 永不响,不在表里。</summary>
    private static readonly (string Key, int Frame)[] FeedSounds = { ("SYN_SND_904", 0), ("SYN_SND_900", 1), ("SYN_SND_902", 2), ("SYN_SND_901", 3) };

    private static void AssertFeedSoundsFollowTheirFrames(MotionTrace t)
    {
        foreach ((string key, int frame) in FeedSounds)
        {
            int first = t.FirstStepAtFrame(frame);
            Assert.True(first >= 0, key);
            Assert.Equal(new[] { first }, t.StepsWith(key));                    // 动画帧第一次到登记帧的那一步出现,只出现一次
        }
        Assert.Empty(t.StepsWith("SYN_SND_903"));                                // 概率 0:永不响
    }

    // ------------------------------------------------------------------ 没有触发器的出招:事件流单独放

    [Fact]
    public void AnAttackWithoutTriggersStillPlaysItsSoundsAtTheRegisteredFramesAndClosesItsLayersWhenTheAttackEnds()
    {
        using var arena = new ActArena("motionfeed", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.False(m.HasActs);                                                 // 没有触发器:不建运行器,出招阶段里没有别的变化
        Assert.False(m.MotionStreamActive);

        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.True(m.MotionStreamActive);
        MotionTrace t = TraceWhile(arena, () => m.Phase == MonsterPhase.Attack, 3);

        AssertFeedSoundsFollowTheirFrames(t);
        Assert.InRange(t.FirstStepAtFrame(1), 6, 8);                             // 帧 1 从 80 毫秒起
        Assert.Equal(0, t.FirstStepAtFrame(0));

        // 叠加层 #0 在帧 1、#1 在帧 2 开始;出招期间没有结束,离开出招阶段那一步一起收口
        int s1 = t.FirstStepAtFrame(1);
        int s2 = t.FirstStepAtFrame(2);
        Assert.Equal(0, t.LayerStarts[s1 - 1]);
        Assert.Equal(1, t.LayerStarts[s1]);
        Assert.Equal(1, t.LayerStarts[s2 - 1]);
        Assert.Equal(2, t.LayerStarts[s2]);
        Assert.All(t.LayerEnds.Take(t.Steps - 1), n => Assert.Equal(0, n));
        Assert.Equal(2, t.LayerEnds[t.Steps - 1]);

        // 声音都是普通音效,发声的是这只怪物(序号 + 1)
        Assert.All(t.AllCues, c =>
        {
            Assert.Equal(SoundCueKind.Effect, c.Kind);
            Assert.Equal(m.Index + 1, c.Owner);
        });
        Assert.Equal((2, 2), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));
        Assert.Equal((5, 4, 1), (arena.Session.ActSoundEvents, arena.Session.ActSoundsQueued, arena.Session.ActSoundsSkipped));
        Assert.Equal(9, m.MotionEventsFed);                                      // 2 个开始 + 5 个声音 + 2 个结束
        Assert.False(m.MotionStreamActive);
    }

    [Fact]
    public void EveryAttackStartsItsEventStreamOverAndTheSequenceRepeats()
    {
        using var arena = new ActArena("motionfeed", useVitals: false);
        MotionTrace first = TraceAttack(arena);
        MotionTrace second = TraceAttack(arena);                                 // 冷却 1500 毫秒后的第二次出招

        AssertFeedSoundsFollowTheirFrames(second);
        Assert.Equal(first.Cues.SelectMany(c => c).Select(c => c.Key), second.Cues.SelectMany(c => c).Select(c => c.Key));
        Assert.Equal(first.Steps, second.Steps);
        Assert.Equal((4, 4), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));
        Assert.Equal((10, 8, 2), (arena.Session.ActSoundEvents, arena.Session.ActSoundsQueued, arena.Session.ActSoundsSkipped));
    }

    [Fact]
    public void BeingHitMidAttackClosesTheStartedLayersAtOnceAndTheRestOfTheSoundsNeverPlay()
    {
        using var arena = new ActArena("motionfeed", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.True(arena.Until(() => m.CurrentFrame >= 2) > 0);                 // 两个叠加层都开始了,帧 3 的声音还没响
        Assert.Equal(2, arena.Session.ActLayerStarts);
        Assert.Equal(0, arena.Session.ActLayerEnds);

        m.ApplyHit(1, false, 0, false);                                          // 受击:出招中断
        Assert.NotEqual(MonsterPhase.Attack, m.Phase);
        Assert.Equal(2, arena.Session.ActLayerEnds);
        Assert.False(m.MotionStreamActive);

        // 受击没有出招冷却:怪物很快又出招。这一次出招开始之前,没有任何事件冒出来
        var all = new List<SoundCue>();
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack, 200) > 0);
        arena.Session.DrainSounds(all);
        Assert.DoesNotContain(all, c => c.Key == "SYN_SND_901");
        Assert.Equal((2, 2), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));

        // 新的一次出招从头来:声音按登记帧各响一次,叠加层再开始、再收口
        MotionTrace again = TraceWhile(arena, () => m.Phase == MonsterPhase.Attack, 3);
        AssertFeedSoundsFollowTheirFrames(again);
        Assert.Equal((4, 4), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));
    }

    [Fact]
    public void ADetachedOrUnattachedSessionPlaysNothingAndTheMonsterDoesNotRunAStream()
    {
        using var detached = new ActArena("motionfeed", useVitals: false, attach: false);
        Assert.True(detached.Until(() => detached.Monster.Phase == MonsterPhase.Attack) > 0);
        Assert.False(detached.Monster.MotionStreamActive);
        MotionTrace t = TraceWhile(detached, () => detached.Monster.Phase == MonsterPhase.Attack, 3);
        Assert.Empty(t.AllCues);
        Assert.Equal((0, 0, 0), (detached.Session.ActLayerStarts, detached.Session.ActLayerEnds, detached.Session.ActSoundEvents));

        using var attached = new ActArena("motionfeed", useVitals: false);
        attached.Session.AttachActs(null);                                       // 撤掉
        Assert.True(attached.Until(() => attached.Monster.Phase == MonsterPhase.Attack) > 0);
        Assert.False(attached.Monster.MotionStreamActive);
        MotionTrace u = TraceWhile(attached, () => attached.Monster.Phase == MonsterPhase.Attack, 3);
        Assert.Empty(u.AllCues);
        Assert.Equal((0, 0, 0), (attached.Session.ActLayerStarts, attached.Session.ActLayerEnds, attached.Session.ActSoundEvents));
    }

    [Fact]
    public void TheEventStreamOnlyReadsAndDoesNotChangeWhatTheMonsterDoes()
    {
        // 同一个出招动画:对照那只没有事件登记的怪物(plain),逐步比较阶段、位置与动画帧,以及主角挨的打
        using var feed = new ActArena("motionfeed", useVitals: true);
        using var control = new ActArena("plain", useVitals: true);
        for (int i = 0; i < 600; i++)
        {
            feed.Session.Update(10, 0, 0, false, false);
            control.Session.Update(10, 0, 0, false, false);
            Assert.Equal((control.Monster.Phase, control.Monster.X, control.Monster.Y, control.Monster.Z, control.Monster.CurrentFrame),
                (feed.Monster.Phase, feed.Monster.X, feed.Monster.Y, feed.Monster.Z, feed.Monster.CurrentFrame));
        }
        Assert.Equal(control.Session.HeroHits, feed.Session.HeroHits);
        Assert.Equal(control.Session.HeroDamageTaken, feed.Session.HeroDamageTaken);
        Assert.Equal(control.Monster.SwingNumber, feed.Monster.SwingNumber);
        Assert.True(feed.Monster.SwingNumber >= 2);                              // 这 6 秒里出了不止一次招,事件流确实跑过
        Assert.True(feed.Session.ActSoundEvents >= 10);
    }

    // ------------------------------------------------------------------ 有触发器的出招:事件流由运行器出

    [Fact]
    public void AnAttackWithTriggersGetsItsEventsFromTheRunnerAndNotFromTheSeparateStream()
    {
        using var arena = new ActArena("motiontrig", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(m.HasActs);
        Assert.True(arena.Until(() => m.Phase == MonsterPhase.Attack) > 0);
        Assert.False(m.MotionStreamActive);                                      // 不重复:运行器自己出
        MotionTrace t = TraceWhile(arena, () => m.Phase == MonsterPhase.Attack, 3);

        int sound = t.FirstStepAtFrame(1);
        Assert.Equal(new[] { sound }, t.StepsWith("SYN_SND_910"));
        Assert.InRange(sound, 6, 8);
        Assert.Equal(1, t.LayerStarts[0]);                                       // 叠加层帧 0 一开始就起
        Assert.Equal((1, 1), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));
        Assert.Equal((1, 1, 0), (arena.Session.ActSoundEvents, arena.Session.ActSoundsQueued, arena.Session.ActSoundsSkipped));
        Assert.Equal(0, t.LayerEnds[t.Steps - 2]);
        Assert.Equal(1, t.LayerEnds[t.Steps - 1]);                               // 离开出招阶段那一步收口
        Assert.Equal(0, m.MotionEventsFed);
        Assert.Equal(m.Index + 1, t.AllCues.Single().Owner);
    }

    // ------------------------------------------------------------------ 自定义动作

    [Fact]
    public void ACustomActionWithoutTriggersPlaysItsOwnEventStreamFromItsOwnFrames()
    {
        using var arena = new ActArena("f_custmotion", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => m.InCustomAction) > 0);                    // 出招第 1 帧脚本切进自定义动作 0
        Assert.True(m.MotionStreamActive);
        Assert.Equal(0, arena.Session.ActLayerStarts);                           // 切过来的这一步还没出事件(出招那一招没有登记)
        MotionTrace t = TraceWhile(arena, () => m.InCustomAction, 2);

        int sound = t.FirstStepAtFrame(1);
        Assert.Equal(new[] { sound }, t.StepsWith("SYN_SND_920"));               // 自定义动作自己的帧 1(80 毫秒起)
        Assert.InRange(sound, 6, 8);
        Assert.Equal(1, t.LayerStarts[0]);
        Assert.Equal(0, t.LayerEnds[t.Steps - 2]);
        Assert.Equal((1, 1), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));
        Assert.Equal(1, arena.Session.ActSoundsQueued);
        Assert.False(m.MotionStreamActive);
    }

    // ------------------------------------------------------------------ [CHANGE ACTION FILE] 换进来的招

    [Fact]
    public void AnAttackSwappedInByAScriptStartsItsEventStreamWhenItIsEntered()
    {
        using var arena = new ActArena("f_swapmotion", useVitals: false);
        MonsterActor m = arena.Monster;
        Assert.True(m.HasActs);                                                  // 第 0 招有触发器;第 1 招原来既没触发器也没事件
        Assert.True(arena.Until(() => m.ActionFilesApplied > 0 && m.AttackIndex == 1 && m.Phase == MonsterPhase.Attack) > 0);
        Assert.True(m.MotionStreamActive);                                       // 换进来的程序没有触发器、有事件:单独放
        MotionTrace t = TraceWhile(arena, () => m.Phase == MonsterPhase.Attack, 3);

        AssertFeedSoundsFollowTheirFrames(t);
        Assert.Equal((2, 2), (arena.Session.ActLayerStarts, arena.Session.ActLayerEnds));
    }
}
