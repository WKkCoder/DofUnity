using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Tests;

namespace Dnf.Play.Tests;

/// <summary>宠物脚本(.cre)里外观与移动字段的读取:数全是合成的,动画路径按脚本所在目录解析,缺的项落到缺省。</summary>
public class PetLookTests
{
    private const string Cre = "creature/p901/p901.cre";

    private static PetLook Read(Action<TokenWriter> body, string scriptPath = Cre, int petId = 901)
    {
        var fx = new Fixture().Tokens(scriptPath, body);
        var data = new GameData(fx.Build());
        return PetLookScript.Parse(data.ReadScript(scriptPath), petId, scriptPath);
    }

    [Fact]
    public void EveryFieldIsReadAndTheAnimationPathsAreResolvedAgainstTheScriptDirectory()
    {
        PetLook look = Read(w => w.Tag("[basic motion]").Str("Motion/Stay.ani")
            .Tag("[walk motion]").Str("Motion/Walk.ani")
            .Tag("[run motion]").Str("Motion/Run.ani")
            .Tag("[floating height]").Int(37)
            .Tag("[character span]").Int(-41)
            .Tag("[move speed]").Ints(330, 270, 130, 105, 60));

        Assert.Equal(901, look.PetId);
        Assert.Equal(Cre, look.ScriptPath);
        Assert.Equal("creature/p901/motion/stay.ani", look.BasicAni);
        Assert.Equal("creature/p901/motion/walk.ani", look.WalkAni);
        Assert.Equal("creature/p901/motion/run.ani", look.RunAni);
        Assert.Equal(37, look.FloatingHeight);
        Assert.Equal(-41, look.CharacterSpan);
        Assert.Equal(new[] { 330, 270, 130, 105, 60 }, look.MoveSpeed.ToArray());
        Assert.True(look.HasAppearance);
    }

    [Fact]
    public void MissingOrEmptyFieldsFallToTheDefaultsAndAnEmptyMotionStringMeansNoAnimation()
    {
        // 什么都没写
        PetLook none = Read(w => w.Tag("[max level]").Int(5));
        Assert.Equal((null, null, null, 0, null, false), (none.BasicAni, none.WalkAni, none.RunAni, none.FloatingHeight, none.CharacterSpan, none.HasAppearance));
        Assert.Empty(none.MoveSpeed);

        // 写成空字符串的动作等于没写;其余照读
        PetLook blank = Read(w => w.Tag("[basic motion]").Str("Motion/Stay.ani").Tag("[walk motion]").Str("").Tag("[run motion]").Str(""));
        Assert.Equal(("creature/p901/motion/stay.ani", null, null), (blank.BasicAni, blank.WalkAni, blank.RunAni));
        Assert.True(blank.HasAppearance);

        // 一个动画也没有:画不出来
        PetLook empty = Read(w => w.Tag("[basic motion]").Str("").Tag("[floating height]").Int(9));
        Assert.False(empty.HasAppearance);
        Assert.Equal(9, empty.FloatingHeight);
    }

    [Fact]
    public void AMoveSpeedListThatIsNotExactlyFiveNumbersCountsAsNotWritten()
    {
        Assert.Empty(Read(w => w.Tag("[move speed]").Ints(1, 2, 3, 4)).MoveSpeed);
        Assert.Empty(Read(w => w.Tag("[move speed]").Ints(1, 2, 3, 4, 5, 6)).MoveSpeed);
        Assert.Empty(Read(w => w.Tag("[move speed]")).MoveSpeed);
        Assert.Equal(5, Read(w => w.Tag("[move speed]").Ints(1, 2, 3, 4, 5)).MoveSpeed.Count);
    }

    [Fact]
    public void FloatNumbersAreTruncatedTowardZeroAndTheLastOccurrenceOfATagWins()
    {
        PetLook look = Read(w => w.Tag("[floating height]").Float(12.9f).Tag("[character span]").Float(-41.9f)
            .Tag("[basic motion]").Str("a/first.ani").Tag("[basic motion]").Str("b/second.ani")
            .Tag("[floating height]").Float(5.5f));
        Assert.Equal((5, -41, "creature/p901/b/second.ani"), (look.FloatingHeight, look.CharacterSpan, look.BasicAni));
        Assert.Equal(12, Read(w => w.Tag("[floating height]").Float(12.9f)).FloatingHeight);
    }

    [Fact]
    public void TheLastWrittenMoveSpeedWinsAndASpanThatIsNotANumberCountsAsNotWritten()
    {
        PetLook twice = Read(w => w.Tag("[move speed]").Ints(1, 2, 3, 4, 5).Tag("[move speed]").Ints(6, 7, 8, 9, 10));
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, twice.MoveSpeed.ToArray());
        Assert.Empty(Read(w => w.Tag("[move speed]").Ints(1, 2, 3, 4, 5).Tag("[move speed]").Ints(1, 2, 3, 4)).MoveSpeed);   // 后写的不是五个数:当没写

        PetLook text = Read(w => w.Tag("[character span]").Str("far").Tag("[floating height]").Str("high"));
        Assert.Equal((null, 0), (text.CharacterSpan, text.FloatingHeight));
    }

    [Fact]
    public void ARelativeAnimationPathStepsOutOfTheScriptDirectoryWhenItSaysSo()
    {
        PetLook look = Read(w => w.Tag("[basic motion]").Str("../shared/stay.ani").Tag("[walk motion]").Str("walk.ani"));
        Assert.Equal("creature/shared/stay.ani", look.BasicAni);
        Assert.Equal("creature/p901/walk.ani", look.WalkAni);
    }

    [Fact]
    public void ANullScriptGivesALookWithNothingInIt()
    {
        PetLook look = PetLookScript.Parse(null, 77, "creature/x/x.cre");
        Assert.Equal((77, "creature/x/x.cre", false, 0, null), (look.PetId, look.ScriptPath, look.HasAppearance, look.FloatingHeight, look.CharacterSpan));
        Assert.Empty(look.MoveSpeed);
        Assert.Null(look.AniOf(PetMotion.Stand));
        Assert.Null(look.AniOf(PetMotion.Run));
    }

    // ------------------------------------------------------------ 动作 → 动画的退回次序

    private static PetLook Anis(string basic, string walk, string run) => new PetLook(1, "s.cre", basic, walk, run, 0, null, null);

    [Fact]
    public void RunFallsBackToWalkAndWalkFallsBackToStandAndNothingWritesNull()
    {
        PetLook all = Anis("stand", "walk", "run");
        Assert.Equal(("stand", "walk", "run"), (all.AniOf(PetMotion.Stand), all.AniOf(PetMotion.Walk), all.AniOf(PetMotion.Run)));

        PetLook noRun = Anis("stand", "walk", null);
        Assert.Equal(("stand", "walk", "walk"), (noRun.AniOf(PetMotion.Stand), noRun.AniOf(PetMotion.Walk), noRun.AniOf(PetMotion.Run)));

        PetLook noWalk = Anis("stand", null, "run");
        Assert.Equal(("stand", "stand", "run"), (noWalk.AniOf(PetMotion.Stand), noWalk.AniOf(PetMotion.Walk), noWalk.AniOf(PetMotion.Run)));

        PetLook onlyStand = Anis("stand", null, null);
        Assert.Equal(("stand", "stand", "stand"), (onlyStand.AniOf(PetMotion.Stand), onlyStand.AniOf(PetMotion.Walk), onlyStand.AniOf(PetMotion.Run)));

        // 站立动画缺:站着时画不出来,但有行走动画时追的时候照样有图
        PetLook noStand = Anis(null, "walk", null);
        Assert.Equal((null, "walk", "walk"), (noStand.AniOf(PetMotion.Stand), noStand.AniOf(PetMotion.Walk), noStand.AniOf(PetMotion.Run)));
        Assert.True(noStand.HasAppearance);
        Assert.False(Anis(null, null, null).HasAppearance);
    }

    [Fact]
    public void AnEmptyAnimationPathHandedStraightToTheLookCountsAsNotWrittenJustLikeOneMissingFromTheScript()
    {
        // 脚本解析那一路早就把空串当没写;这里是直接造外观时给空串:三个路径各自都要归成 null,动作退回与"有没有外观"跟着走
        PetLook none = Anis("", "", "");
        Assert.Equal((null, null, null), (none.BasicAni, none.WalkAni, none.RunAni));
        Assert.False(none.HasAppearance);
        Assert.Equal((null, null, null), (none.AniOf(PetMotion.Stand), none.AniOf(PetMotion.Walk), none.AniOf(PetMotion.Run)));

        PetLook emptyStand = Anis("", "walk", null);                // 站立空:站着画不出来,追的时候有行走图
        Assert.Equal((null, true, null, "walk"), (emptyStand.BasicAni, emptyStand.HasAppearance, emptyStand.AniOf(PetMotion.Stand), emptyStand.AniOf(PetMotion.Run)));

        PetLook emptyWalk = Anis("stand", "", "run");               // 行走空:行走退回站立,奔跑照旧
        Assert.Equal((null, "stand", "run"), (emptyWalk.WalkAni, emptyWalk.AniOf(PetMotion.Walk), emptyWalk.AniOf(PetMotion.Run)));

        PetLook emptyRun = Anis("stand", "walk", "");               // 奔跑空:奔跑退回行走
        Assert.Equal((null, "walk"), (emptyRun.RunAni, emptyRun.AniOf(PetMotion.Run)));
    }

    [Fact]
    public void TheLookKeepsItsOwnCopyOfTheMoveSpeeds()
    {
        int[] source = { 1, 2, 3, 4, 5 };
        var look = new PetLook(1, "s.cre", null, null, null, 0, null, source);
        source[0] = 99;
        Assert.Equal(1, look.MoveSpeed[0]);
        Assert.Empty(new PetLook(1, "s.cre", null, null, null, 0, null, null).MoveSpeed);
        Assert.Equal(string.Empty, new PetLook(1, null, null, null, null, 0, null, null).ScriptPath);
    }

    // ------------------------------------------------------------ 目录

    [Fact]
    public void TheCatalogReadsEachPetOnceKeyedByIdAndGivesNothingForNoDefinition()
    {
        var data = new GameData(PetTownKit.Scripts().Build());
        var catalog = new PetLookCatalog(data);
        var def = new PetDef(PetTownKit.PetFull, Cre);

        PetLook first = catalog.Get(def);
        Assert.Equal(PetTownKit.FullHeight, first.FloatingHeight);
        Assert.Equal(PetTownKit.FullSpan, first.CharacterSpan);
        Assert.Equal(PetTownKit.FullSpeed, first.MoveSpeed.ToArray());
        Assert.Equal("creature/p901/motion/stay.ani", first.BasicAni);
        Assert.Equal("creature/p901/motion/walk.ani", first.WalkAni);
        Assert.Null(first.RunAni);                                   // 901 的奔跑动作写成了空字符串
        Assert.Same(first, catalog.Get(def));                        // 缓存
        Assert.Same(first, catalog.Get(new PetDef(PetTownKit.PetFull, "ignored/other.cre")));   // 以编号为键

        Assert.Null(catalog.Get(null));
    }

    [Fact]
    public void ABrokenOrMissingScriptStillGivesALookThatCanFollowButHasNoPicture()
    {
        var data = new GameData(PetTownKit.Scripts().Build());
        var catalog = new PetLookCatalog(data);

        PetLook missing = catalog.Get(new PetDef(555, "creature/nowhere/none.cre"));
        Assert.NotNull(missing);
        Assert.Equal((555, "creature/nowhere/none.cre", false, 0, null), (missing.PetId, missing.ScriptPath, missing.HasAppearance, missing.FloatingHeight, missing.CharacterSpan));

        PetLook noPath = catalog.Get(new PetDef(556, ""));
        Assert.NotNull(noPath);
        Assert.False(noPath.HasAppearance);

        // 没写任何外观的 902、只写了站立动画的 903
        Assert.False(catalog.Get(new PetDef(PetTownKit.PetGrow, "creature/p902/p902.cre")).HasAppearance);
        PetLook plain = catalog.Get(new PetDef(PetTownKit.PetPlain, "creature/p903/p903.cre"));
        Assert.Equal(("creature/p903/motion/stay.ani", null, null, null), (plain.BasicAni, plain.WalkAni, plain.RunAni, plain.CharacterSpan));
        Assert.Throws<ArgumentNullException>(() => new PetLookCatalog(null));
    }
}
