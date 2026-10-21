using Dnf.Data;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 场景音乐(证据 E-097):流程在选角界面、城镇区域、结算开始时发出的声音提示,以及它们交给调度器之后音乐怎么换。
/// 标签与会话全是合成的;哪个界面放哪个标签是静态读码的观察(选角)与按名字的推断(结算),测试钉的是代码自己的约定。
/// </summary>
public class SceneMusicTests
{
    private const string Xml = @"<AudioTagDatabase>
  <MUSIC ID=""M_CHARACTER_SELECT"" FILE=""music\t\sel.ogg"" LOOP_DELAY=""0""/>
  <MUSIC ID=""M_TOWN"" FILE=""music\t\town.ogg"" LOOP_DELAY=""0""/>
  <MUSIC ID=""M_GATE"" FILE=""music\t\gate.ogg"" LOOP_DELAY=""0""/>
  <MUSIC ID=""M_DUNGEON"" FILE=""music\t\dg.ogg"" LOOP_DELAY=""0""/>
  <EFFECT ID=""M_RESULT"" FILE=""sounds\t\result_effect.ogg""/>
  <MUSIC ID=""M_RESULT"" FILE=""music\t\result_music.ogg""/>
  <AMBIENT ID=""A_TOWN"" FILE=""sounds\amb\t.ogg"" LOOP_DELAY=""0""/>
  <AMBIENT ID=""A_GATE"" FILE=""sounds\amb\g.ogg"" LOOP_DELAY=""0""/>
</AudioTagDatabase>";

    /// <summary>带地图 [sound] 的城镇区域会话。</summary>
    private sealed class MusicalTown : GameFlowTests.FakeHero, IMapSoundSource
    {
        public IReadOnlyList<string> MapSounds { get; }

        public MusicalTown(string label, params string[] sounds) : base(label)
        {
            MapSounds = sounds;
        }
    }

    /// <summary>自己发声音提示的副本会话。</summary>
    private sealed class SoundyRun : GameFlowTests.FakeHero, IRunSession, ISoundSource
    {
        public SoundCueQueue Sounds { get; } = new SoundCueQueue();

        public SettlementChoice Choice { get; set; }

        public SoundyRun(string label) : base(label)
        {
        }

        public void DrainSounds(List<SoundCue> into) => Sounds.Drain(into);
    }

    private sealed class MusicWorld : ITownWorld
    {
        public List<SoundyRun> Runs { get; } = new List<SoundyRun>();

        public TownPlace Start => new TownPlace(1, 0);

        public int ReturnArea { get; set; } = 2;

        public TownPlace Return => new TownPlace(1, ReturnArea, TownEntry.Return);

        public IHeroSession Create(TownPlace place)
        {
            string label = "town " + place.Town + "/" + place.Area;
            switch (place.Area)
            {
                case 0:
                    return new MusicalTown(label, "M_TOWN", "A_TOWN");
                case 2:
                    return new MusicalTown(label, "M_GATE", "A_GATE");
                case 4:
                    return new MusicalTown(label, "M_TOWN", "A_GATE");              // 与区域 0 同一首音乐、另一种环境声
                case 3:
                    return new MusicalTown(label);                                  // 地图里没有 [sound]
                default:
                    return new GameFlowTests.FakeHero(label);                       // 不带地图声音的会话(老式单张地图)
            }
        }

        public SplashScene Splash(TownPlace? from, TownPlace to) => null;

        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft) => null;

        public bool TryRoute(TownPlace from, out TownExit exit)
        {
            exit = default;
            return false;
        }
    }

    private static GameFlow NewFlow(MusicWorld world)
    {
        return new GameFlow(
            world,
            (path, run) =>
            {
                var session = new SoundyRun("run " + run);
                world.Runs.Add(session);
                return session;
            },
            new[] { new DungeonEntry("dungeon/test/a.dgn", "AAA") },
            null,
            null,
            () => new CharacterSelectScreen(CharacterRoster.Local()));
    }

    private static List<SoundCue> Drain(GameFlow flow)
    {
        var cues = new List<SoundCue>();
        flow.DrainSounds(cues);
        return cues;
    }

    private static string[] Text(IEnumerable<SoundCue> cues) => cues.Select(c => c.Kind + ":" + c.Key).ToArray();

    private static string[] Text(IEnumerable<SoundCommand> commands) => commands.Select(c => c.ToString()).ToArray();

    // ------------------------------------------------------------------ 流程发出的提示

    [Fact]
    public void AFlowThatHasNotStartedHasNoSounds()
    {
        GameFlow flow = NewFlow(new MusicWorld());
        Assert.Empty(Drain(flow));
        Assert.Throws<ArgumentNullException>(() => flow.DrainSounds(null!));
    }

    [Fact]
    public void TheCharacterSelectScreenQueuesItsMusicBehindASceneMarkerExactlyOnce()
    {
        GameFlow flow = NewFlow(new MusicWorld());
        flow.EnterCharacterSelect();

        Assert.Equal(new[] { "RoomEnter:scene:character-select", "RoomSound:M_CHARACTER_SELECT" }, Text(Drain(flow)));
        Assert.Empty(Drain(flow));                                                  // 取走之后不重复
        flow.CharacterSelect.Move(1, 0);                                            // 界面里的操作不产生声音提示(按键声不在这一步)
        Assert.Empty(Drain(flow));
    }

    [Fact]
    public void ATownAreaQueuesItsMapSoundsInTheOrderTheMapListsThemBehindTheMapPath()
    {
        GameFlow flow = NewFlow(new MusicWorld());
        flow.EnterTown("start");

        Assert.Equal(new[] { "RoomEnter:fake/town 1/0", "RoomSound:M_TOWN", "RoomSound:A_TOWN" }, Text(Drain(flow)));
        Assert.Empty(Drain(flow));

        flow.EnterTown("back");                                                      // 第二次进城走"回城落脚点":别的区域,别的声音
        Assert.Equal(new[] { "RoomEnter:fake/town 1/2", "RoomSound:M_GATE", "RoomSound:A_GATE" }, Text(Drain(flow)));
    }

    [Fact]
    public void AMapWithoutSoundsOnlyMarksTheSceneAndASessionWithoutMapSoundsIsSilent()
    {
        var world = new StepWorld();
        GameFlow flow = new GameFlow(world, (path, run) => new SoundyRun("run"), new[] { new DungeonEntry("dungeon/test/a.dgn", "AAA") });
        flow.EnterTown();                                                            // 区域 3:会话带地图声音但列表是空的:只有场景标记(上一块区域的环境声据此停掉)
        Assert.Equal(new[] { "RoomEnter:fake/quiet" }, Text(Drain(flow)));

        world.Area = 9;                                                              // 区域 9:会话根本不带地图声音(老式单张地图):什么也不发
        flow.EnterTown();
        Assert.Empty(Drain(flow));
    }

    [Fact]
    public void OnlyATownToTownSwitchKeepsTheOldSoundsAndTheFirstSwitchNeverCounts()
    {
        GameFlow flow = NewFlow(new MusicWorld());
        Assert.False(flow.LastSwitchStaysInTown);                                    // 还没切换过
        flow.EnterTown("start");
        Assert.False(flow.LastSwitchStaysInTown);                                    // 第一次切换的"上一阶段"就是它自己:不算
        flow.EnterTown("again");
        Assert.True(flow.LastSwitchStaysInTown);                                     // 城镇 → 城镇
        flow.EnterSelect();
        Assert.False(flow.LastSwitchStaysInTown);
        flow.EnterTown("back");
        Assert.False(flow.LastSwitchStaysInTown);                                    // 选择界面 → 城镇
        flow.EnterCharacterSelect();
        Assert.False(flow.LastSwitchStaysInTown);
    }

    private sealed class StepWorld : ITownWorld
    {
        public int Area { get; set; } = 3;

        public TownPlace Start => new TownPlace(1, Area);

        public TownPlace Return => new TownPlace(1, Area);

        public IHeroSession Create(TownPlace place) => place.Area == 3 ? new MusicalTown("quiet") : new GameFlowTests.FakeHero("legacy");

        public SplashScene Splash(TownPlace? from, TownPlace to) => null;

        public TownPlace? Through(TownPlace from, TownExit exit, bool facingLeft) => null;

        public bool TryRoute(TownPlace from, out TownExit exit)
        {
            exit = default;
            return false;
        }
    }

    [Fact]
    public void CuesLeftOverFromAStageThatHasBeenLeftAreDroppedAndTheNewStageStartsClean()
    {
        GameFlow flow = NewFlow(new MusicWorld());
        flow.EnterCharacterSelect();                                                 // 选角的提示没人取
        flow.CharacterSelect.Confirm();
        Assert.True(flow.Poll());                                                    // 开始游戏:进城

        Assert.Equal(new[] { "RoomEnter:fake/town 1/0", "RoomSound:M_TOWN", "RoomSound:A_TOWN" }, Text(Drain(flow)));   // 没有选角音乐的残留
    }

    [Fact]
    public void WhileInADungeonTheFlowForwardsTheSessionsOwnCuesAndAfterLeavingItDoesNot()
    {
        var world = new MusicWorld();
        GameFlow flow = NewFlow(world);
        flow.EnterTown();
        Drain(flow);

        flow.EnterSelect();
        Assert.Empty(Drain(flow));                                                   // 副本选择界面没有声音
        flow.StartDungeon("dungeon/test/a.dgn");
        Assert.Equal(GameStage.Dungeon, flow.Stage);
        Assert.Empty(Drain(flow));                                                   // 阶段自己没有提示;会话还没发

        SoundyRun run = world.Runs[0];
        run.Sounds.Add(SceneMusic.ResultSound, SoundCueKind.Ui);
        Assert.Equal(new[] { "Ui:M_RESULT" }, Text(Drain(flow)));
        Assert.Empty(Drain(flow));

        run.Choice = SettlementChoice.ReturnToTown;
        Assert.True(flow.Poll());                                                    // 回城
        Assert.Equal(GameStage.Town, flow.Stage);
        run.Sounds.Add("OLD_TAG", SoundCueKind.Effect);                              // 旧会话再发什么也不会被取到
        Assert.Equal(new[] { "RoomEnter:fake/town 1/2", "RoomSound:M_GATE", "RoomSound:A_GATE" }, Text(Drain(flow)));
    }

    // ------------------------------------------------------------------ 交给调度器之后

    [Fact]
    public void TheSchedulerPlaysTheSelectMusicThenTheTownMusicThenTheDungeonMusicAndTheResultSoundAsAUiSoundWhenTheHostStopsTheOldSoundsOnEachSwitch()
    {
        var world = new MusicWorld();
        var scheduler = new SoundScheduler(AudioCatalog.ParseText(Xml), () => 0.5);
        GameFlow flow = NewFlow(world);
        var output = new List<SoundCommand>();

        void Step()
        {
            output.Clear();
            scheduler.Update(16, Drain(flow), output);
        }

        void StopOldSounds()                                                         // 宿主换阶段时做的事
        {
            output.Clear();
            scheduler.StopAll(output);
        }

        flow.EnterCharacterSelect();
        Step();
        Assert.Equal(new[] { "Play#1 Music M_CHARACTER_SELECT loop" }, Text(output));
        Assert.Equal("M_CHARACTER_SELECT", scheduler.MusicId);

        flow.CharacterSelect.Confirm();
        Assert.True(flow.Poll());                                                    // 登录进城
        StopOldSounds();
        Assert.Equal(new[] { "Stop#1 Music" }, Text(output));
        Step();
        Assert.Equal(new[] { "Play#2 Music M_TOWN loop", "Play#3 Room A_TOWN loop" }, Text(output));

        flow.EnterSelect();
        StopOldSounds();
        Assert.Equal(new[] { "Stop#2 Music", "Stop#3 Room" }, Text(output).OrderBy(t => t).ToArray());
        Step();
        Assert.Empty(output);                                                        // 副本选择界面安静

        flow.StartDungeon("dungeon/test/a.dgn");
        StopOldSounds();
        SoundyRun run = world.Runs[0];
        run.Sounds.Add("dungeon/test/room", SoundCueKind.RoomEnter);                 // 副本房间自己的音乐
        run.Sounds.Add("M_DUNGEON", SoundCueKind.RoomSound);
        Step();
        Assert.Equal(new[] { "Play#4 Music M_DUNGEON loop" }, Text(output));

        // 结算开始:不换阶段,只发一声 M_RESULT(界面音)。audio.xml 里它写了两次(Effect 在前、Music 在后),先出现的赢:放的是 Effect 那条,
        // 走界面通道,不换背景音乐、不循环
        run.Sounds.Add(SceneMusic.ResultSound, SoundCueKind.Ui);
        Step();
        Assert.Equal(new[] { "Play#5 Ui M_RESULT" }, Text(output));
        Assert.Equal("M_DUNGEON", scheduler.MusicId);
    }

    [Fact]
    public void MovingBetweenTownAreasThatShareTheSameMusicKeepsItPlayingAndOnlyChangesTheAmbientSounds()
    {
        // 宿主在"城镇→城镇"的切换里不 StopAll(见 LastSwitchStaysInTown),调度器按"同一个标签沿用"处理:音乐不重放,只换环境声
        var world = new MusicWorld { ReturnArea = 4 };
        var scheduler = new SoundScheduler(AudioCatalog.ParseText(Xml), () => 0.5);
        GameFlow flow = NewFlow(world);
        var output = new List<SoundCommand>();

        flow.EnterTown("start");
        scheduler.Update(16, Drain(flow), output);
        Assert.Equal(new[] { "Play#1 Music M_TOWN loop", "Play#2 Room A_TOWN loop" }, Text(output));

        flow.EnterTown("next area");
        Assert.True(flow.LastSwitchStaysInTown);
        output.Clear();
        scheduler.Update(16, Drain(flow), output);
        Assert.Equal(new[] { "Stop#2 Room", "Play#3 Room A_GATE loop" }, Text(output));
        Assert.Equal("M_TOWN", scheduler.MusicId);

        world.ReturnArea = 2;                                                        // 换到另一首音乐的区域:音乐换掉
        flow.EnterTown("gate");
        output.Clear();
        scheduler.Update(16, Drain(flow), output);
        Assert.Equal("M_GATE", scheduler.MusicId);
        Assert.Contains("Play#4 Music M_GATE loop", Text(output));
        Assert.Contains("Stop#1 Music", Text(output));
    }

    [Fact]
    public void TheSceneTagsAreTheAudioIdsTheClientUses()
    {
        Assert.Equal("M_CHARACTER_SELECT", SceneMusic.CharacterSelect);
        Assert.Equal("M_RESULT", SceneMusic.ResultSound);
        Assert.StartsWith("scene:", SceneMusic.CharacterSelectScene);
    }
}
