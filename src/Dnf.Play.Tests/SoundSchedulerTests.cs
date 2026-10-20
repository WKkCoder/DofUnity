using Dnf.Data;

namespace Dnf.Play.Tests;

/// <summary>
/// 声音调度器(证据 E-089、E-090):提示 → 命令。不碰声卡,标签与文件全是合成的。
/// 循环间隔、延迟、"换房间沿用相同标签"这些规则是推断(没有录音可对照),测试钉的是调度器自己的约定。
/// </summary>
public class SoundSchedulerTests
{
    private const string Xml = @"<AudioTagDatabase>
  <EFFECT ID=""E1"" FILE=""sounds\t\e1.ogg""/>
  <EFFECT ID=""E2"" FILE=""sounds\t\e2.ogg""/>
  <RANDOM ID=""R_E""><ITEM TAG=""E1"" PROB=""50""/><ITEM TAG=""E2"" PROB=""50""/></RANDOM>
  <GROUP ID=""G_DELAY""><ITEM TAG=""E1"" DELAY=""0""/><ITEM TAG=""E2"" DELAY=""0.5""/></GROUP>
  <EFFECT ID=""V1"" FILE=""sounds\t\v1.ogg""/>
  <EFFECT ID=""V2"" FILE=""sounds\t\v2.ogg""/>
  <MUSIC ID=""M1"" FILE=""music\m1.ogg"" LOOP_DELAY=""0""/>
  <MUSIC ID=""M2"" FILE=""music\m2.ogg"" LOOP_DELAY=""0""/>
  <AMBIENT ID=""A1"" FILE=""sounds\amb\a1.ogg"" LOOP_DELAY=""0""/>
  <AMBIENT ID=""A2"" FILE=""sounds\amb\a2.ogg"" LOOP_DELAY=""0""/>
  <EFFECT ID=""P1"" FILE=""sounds\t\p1.ogg"" LOOP_DELAY=""10"" LOOP_DELAY_RANGE=""0""/>
  <EFFECT ID=""ONCE"" FILE=""sounds\t\once.ogg""/>
  <MUSIC ID=""M_ONCE"" FILE=""music\once.ogg""/>
</AudioTagDatabase>";

    private double _random;

    private SoundScheduler Make() => new SoundScheduler(AudioCatalog.ParseText(Xml), () => _random);

    private static SoundCue Effect(string key) => new SoundCue(key, SoundCueKind.Effect);

    private static SoundCue Voice(string key, int owner = 0) => new SoundCue(key, SoundCueKind.Voice, owner);

    private static SoundCue Room(string key) => new SoundCue(key, SoundCueKind.RoomSound);

    private static SoundCue Enter(string map) => new SoundCue(map, SoundCueKind.RoomEnter);

    private static List<SoundCommand> Step(SoundScheduler s, double ms, params SoundCue[] cues)
    {
        var output = new List<SoundCommand>();
        s.Update(ms, cues, output);
        return output;
    }

    private static string[] Text(IEnumerable<SoundCommand> commands) => commands.Select(c => c.ToString()).ToArray();

    // ------------------------------------------------------------------ 音效

    [Fact]
    public void AnEffectPlaysTheFileOfTheTagAndRandomTagsFollowTheInjectedRandom()
    {
        SoundScheduler s = Make();
        List<SoundCommand> a = Step(s, 0, Effect("E1"));
        Assert.Single(a);
        Assert.Equal((SoundCommandKind.Play, SoundChannel.Effect, "E1", "sounds/t/e1.ogg", false), (a[0].Kind, a[0].Channel, a[0].Id, a[0].File, a[0].Loop));

        _random = 0.1;
        Assert.Equal("sounds/t/e1.ogg", Step(s, 100, Effect("R_E"))[0].File);
        _random = 0.9;
        Assert.Equal("sounds/t/e2.ogg", Step(s, 100, Effect("R_E"))[0].File);
    }

    [Fact]
    public void TheSameEffectWithinTheCoalesceWindowPlaysOnlyOnce()
    {
        SoundScheduler s = Make();
        Assert.Single(Step(s, 0, Effect("E1"), Effect("E1")));                           // 同一步里两次:一次
        Assert.Empty(Step(s, 10, Effect("E1")));                                         // 10 毫秒后:还在窗口里
        Assert.Single(Step(s, SoundScheduler.CoalesceMs, Effect("E1")));                 // 窗口过了:放
        Assert.Single(Step(s, 0, Effect("E2")));                                         // 别的标签不受影响
        Assert.Equal(2, s.CoalescedCount);
        Assert.Equal(3, s.PlayCount);
    }

    [Fact]
    public void UnknownTagsAreCountedAndPlayNothing()
    {
        SoundScheduler s = Make();
        Assert.Empty(Step(s, 0, Effect("NOT_THERE"), Voice("NOPE"), new SoundCue("NOPE2", SoundCueKind.Ui)));
        Assert.Equal(3, s.UnknownCount);
        Assert.Equal(0, s.ActiveCount);
    }

    [Fact]
    public void UiCuesUseTheirOwnChannel()
    {
        SoundScheduler s = Make();
        List<SoundCommand> a = Step(s, 0, new SoundCue("E1", SoundCueKind.Ui));
        Assert.Equal(SoundChannel.Ui, a[0].Channel);
    }

    [Fact]
    public void AGroupStartsItsFirstItemAtOnceAndTheDelayedItemWhenDue()
    {
        SoundScheduler s = Make();
        List<SoundCommand> a = Step(s, 0, Effect("G_DELAY"));
        Assert.Equal(new[] { "sounds/t/e1.ogg" }, a.Select(c => c.File).ToArray());
        Assert.Equal(2, s.ActiveCount);                                                  // 排队的那一个也算在途
        Assert.Empty(Step(s, 499));
        List<SoundCommand> b = Step(s, 1);
        Assert.Equal(new[] { "sounds/t/e2.ogg" }, b.Select(c => c.File).ToArray());
    }

    // ------------------------------------------------------------------ 语音

    [Fact]
    public void ANewVoiceInterruptsTheSameOwnersOldOneButNotOtherOwners()
    {
        SoundScheduler s = Make();
        List<SoundCommand> a = Step(s, 0, Voice("V1"));
        int h1 = a[0].Handle;
        List<SoundCommand> b = Step(s, 100, Voice("V2"));
        Assert.Equal(new[] { "Stop#" + h1 + " Voice", "Play#" + b[1].Handle + " Voice V2" }, Text(b));

        List<SoundCommand> c = Step(s, 100, Voice("V1", owner: 3));                      // 别的主人:不打断
        Assert.Equal(new[] { SoundCommandKind.Play }, c.Select(x => x.Kind).ToArray());

        s.NotifyFinished(b[1].Handle);                                                   // 主人 0 的语音放完了:下一句不用停谁
        List<SoundCommand> d = Step(s, 100, Voice("V1"));
        Assert.Equal(new[] { SoundCommandKind.Play }, d.Select(x => x.Kind).ToArray());
    }

    [Fact]
    public void AVoiceThatAlreadyFinishedIsNotStoppedAgain()
    {
        SoundScheduler s = Make();
        int h = Step(s, 0, Voice("V1"))[0].Handle;
        s.NotifyFinished(h);
        s.NotifyFinished(h);                                                             // 重复通知无害
        List<SoundCommand> next = Step(s, 50, Voice("V2"));
        Assert.Single(next);
        Assert.Equal(SoundCommandKind.Play, next[0].Kind);
    }

    // ------------------------------------------------------------------ 房间声音

    [Fact]
    public void EnteringARoomStartsItsMusicAndLoopingAmbientAndWaitsOutPeriodicEffects()
    {
        SoundScheduler s = Make();
        List<SoundCommand> a = Step(s, 0, Enter("map/r1"), Room("M1"), Room("A1"), Room("P1"));
        Assert.Equal(new[] { "Play#1 Music M1 loop", "Play#2 Room A1 loop" }, Text(a));
        Assert.Equal("M1", s.MusicId);
        Assert.Equal(2, s.RoomSoundCount);                                               // A1 与 P1(周期音效在等第一个间隔)

        Assert.Empty(Step(s, 9999));
        List<SoundCommand> p = Step(s, 1);                                               // 10 秒到了
        Assert.Equal(new[] { "Play#3 Room P1" }, Text(p));
        Assert.Empty(Step(s, 60000));                                                    // 没放完之前不会重复
        s.NotifyFinished(p[0].Handle);                                                   // 放完:隔 10 秒再放
        Assert.Empty(Step(s, 9999));
        Assert.Equal(new[] { "Play#4 Room P1" }, Text(Step(s, 1)));
    }

    [Fact]
    public void ChangingRoomsKeepsSharedSoundsStopsTheOldOnesAndStartsTheNewOnes()
    {
        SoundScheduler s = Make();
        Step(s, 0, Enter("map/r1"), Room("M1"), Room("A1"));
        List<SoundCommand> a = Step(s, 100, Enter("map/r2"), Room("M1"), Room("A2"));
        Assert.Equal(new[] { "Stop#2 Room", "Play#3 Room A2 loop" }, Text(a));           // 音乐沿用(M1 没有命令),A1 停,A2 开始
        Assert.Equal("M1", s.MusicId);

        List<SoundCommand> b = Step(s, 100, Enter("map/r3"), Room("M2"));
        Assert.Equal(new[] { "Stop#1 Music", "Stop#3 Room", "Play#4 Music M2 loop" }, Text(b).OrderBy(t => t == "Play#4 Music M2 loop" ? 1 : 0).ToArray());
        Assert.Equal("M2", s.MusicId);
        Assert.Equal(0, s.RoomSoundCount);
    }

    [Fact]
    public void ARoomWithoutMusicKeepsTheCurrentMusic()
    {
        SoundScheduler s = Make();
        Step(s, 0, Enter("map/r1"), Room("M1"));
        List<SoundCommand> a = Step(s, 100, Enter("map/r2"), Room("A1"));
        Assert.Equal(new[] { "Play#2 Room A1 loop" }, Text(a));
        Assert.Equal("M1", s.MusicId);
        Assert.Equal(new[] { "Stop#2 Room" }, Text(Step(s, 100, Enter("map/r3"))));      // 一个声音也没有的房间:环境声停掉,音乐还在
        Assert.Equal("M1", s.MusicId);
    }

    [Fact]
    public void AnEmptyRoomStopsTheOldAmbient()
    {
        SoundScheduler s = Make();
        Step(s, 0, Enter("map/r1"), Room("A1"));
        List<SoundCommand> a = Step(s, 100, Enter("map/r2"));
        Assert.Equal(new[] { "Stop#1 Room" }, Text(a));
        Assert.Equal(0, s.RoomSoundCount);
    }

    [Fact]
    public void ARoomSoundWithoutLoopDelayPlaysOnceAndIsNotRepeatedInTheSameRoomEvenWhenListedAgain()
    {
        SoundScheduler s = Make();
        List<SoundCommand> a = Step(s, 0, Enter("map/r1"), Room("ONCE"));
        Assert.Equal(new[] { "Play#1 Room ONCE" }, Text(a));
        s.NotifyFinished(1);
        Assert.Empty(Step(s, 120000));
        Assert.Empty(Step(s, 100, Enter("map/r1b"), Room("ONCE")));                      // 同一个标签换房间也不重放
    }

    [Fact]
    public void MusicWithoutLoopDelayPlaysOnceWithoutTheLoopFlag()
    {
        SoundScheduler s = Make();
        List<SoundCommand> a = Step(s, 0, Enter("map/r1"), Room("M_ONCE"));
        Assert.Equal(new[] { "Play#1 Music M_ONCE" }, Text(a));
        Assert.False(a[0].Loop);
    }

    [Fact]
    public void RoomSoundsWithoutAnEntryMarkerAreAddedOneByOneAndUnknownOnesAreCounted()
    {
        SoundScheduler s = Make();
        Assert.Equal(new[] { "Play#1 Room A1 loop" }, Text(Step(s, 0, Room("A1"), Room("A1"), Room("NOT_THERE"))));
        Assert.Equal(1, s.UnknownCount);
        Assert.Equal(1, s.RoomSoundCount);
    }

    [Fact]
    public void StopAllStopsWhatIsPlayingForgetsTheRoomAndSkipsWhatNeverStarted()
    {
        SoundScheduler s = Make();
        Step(s, 0, Enter("map/r1"), Room("M1"), Room("A1"), Effect("G_DELAY"));          // G_DELAY 的第二项还在排队
        var output = new List<SoundCommand>();
        s.StopAll(output);
        Assert.Equal(3, output.Count);                                                   // 音乐、环境声、G_DELAY 的第一项;排队的那项没开始过,不发 Stop
        Assert.All(output, c => Assert.Equal(SoundCommandKind.Stop, c.Kind));
        Assert.Equal((0, 0, null), (s.ActiveCount, s.RoomSoundCount, s.MusicId));
        Assert.Empty(Step(s, 1000));                                                     // 排队的也被取消了
    }

    [Fact]
    public void BadArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new SoundScheduler(null!));
        SoundScheduler s = Make();
        Assert.Throws<ArgumentNullException>(() => s.Update(1, new List<SoundCue>(), null!));
        Assert.Throws<ArgumentNullException>(() => s.StopAll(null!));
        s.Update(-5, null, new List<SoundCommand>());                                    // 负的时间步当 0
        Assert.Equal(0, s.NowMs);
    }
}
