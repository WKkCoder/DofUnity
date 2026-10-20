using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>数据定义解析(SkillDef、CharacterDef)与引擎小部件(计时事件、动画播放器、计时器)的测试。</summary>
public sealed class DefsAndCoreTests
{
    private static ScriptLine Line(string tag, params object[] values)
    {
        var list = new List<ScriptValue>();
        foreach (object v in values)
        {
            switch (v)
            {
                case int i:
                    list.Add(new ScriptValue(ScriptValueKind.Int, i, null));
                    break;
                case string s:
                    list.Add(new ScriptValue(ScriptValueKind.String, 0, s));
                    break;
                default:
                    throw new ArgumentException("不支持的值类型");
            }
        }
        return new ScriptLine(tag, list.ToArray());
    }

    [Fact]
    public void SkillDefSeparatesPvpTablesFromTheNormalOnes()
    {
        var def = new SkillDef(15, "skill/x/foo.skl", new[]
        {
            Line("[type]", "[active]"),
            Line("[executable states]", 0, 8, 14),
            Line("[maximum level]", 30),
            Line("[static data]", 300, 150),
            Line("[level info]", 2, 10, 20, 30, 40),
            Line("[pvp]"),
            Line("[static data]", 7),
            Line("[level info]", 1, 5, 6),
            Line("[/pvp]"),
        });
        Assert.Equal("[active]", def.Type);
        Assert.Equal(new[] { 0, 8, 14 }, def.ExecutableStates);
        Assert.Equal(30, def.MaximumLevel);
        Assert.Equal(300, def.GetStatic(0, pvp: false));
        Assert.Equal(150, def.GetStatic(1, pvp: false));
        Assert.Equal(7, def.GetStatic(0, pvp: true));
        Assert.Equal(0, def.GetStatic(5, pvp: false));
        Assert.Equal(0, def.GetStatic(-1, pvp: false));
        // 两列、两级:第 1 级 (10, 20),第 2 级 (30, 40)
        Assert.Equal(20, def.GetLevel(1, 1, pvp: false));
        Assert.Equal(30, def.GetLevel(2, 0, pvp: false));
        Assert.Equal(40, def.GetLevel(99, 1, pvp: false)); // 超出取最后一级
        Assert.Equal(10, def.GetLevel(0, 0, pvp: false)); // 低于 1 取第一级
        Assert.Equal(0, def.GetLevel(1, 2, pvp: false)); // 列越界
        Assert.Equal(6, def.GetLevel(3, 0, pvp: true)); // 决斗场表:一列两级 (5)(6),超出取最后一级
        Assert.True(def.LevelInfo.IsConsistent);
    }

    [Fact]
    public void SkillDefReadsTheTwoIconFramesAndIgnoresAMalformedIconLine()
    {
        // [icon] "图像" 帧 "图像" 帧:能用的图标与灰图标(数是合成的)
        var def = new SkillDef(5, "skill/x/icon.skl", new[] { Line("[icon]", "Character/X/Effect/SkillIcon.img", 10, "Character/X/Effect/SkillIcon.img", 11) });
        Assert.Equal(("Character/X/Effect/SkillIcon.img", 10, "Character/X/Effect/SkillIcon.img", 11), (def.IconImage, def.IconFrame, def.IconDisabledImage, def.IconDisabledFrame));

        // 两个图像可以不同,各自带各自的帧
        var split = new SkillDef(6, "skill/x/split.skl", new[] { Line("[icon]", "A/One.img", 3, "A/Two.img", 4) });
        Assert.Equal(("A/One.img", 3, "A/Two.img", 4), (split.IconImage, split.IconFrame, split.IconDisabledImage, split.IconDisabledFrame));

        // 没有 [icon] 或形状不对(少了后一对、类型错位):当作没有
        foreach (SkillDef none in new[]
        {
            new SkillDef(7, "skill/x/none.skl", new[] { Line("[type]", "[active]") }),
            new SkillDef(8, "skill/x/short.skl", new[] { Line("[icon]", "A/One.img", 3) }),
            new SkillDef(9, "skill/x/swapped.skl", new[] { Line("[icon]", 3, "A/One.img", 4, "A/Two.img") }),
        })
        {
            Assert.Null(none.IconImage);
            Assert.Null(none.IconDisabledImage);
            Assert.Equal((-1, -1), (none.IconFrame, none.IconDisabledFrame));
        }
    }

    [Fact]
    public void SkillDefWithoutPvpSectionFallsBackToTheNormalDataInPvpMode()
    {
        var def = new SkillDef(1, "skill/x/a.skl", new[]
        {
            Line("[static data]", 9),
            Line("[level info]", 1, 4, 5),
        });
        Assert.Equal(9, def.GetStatic(0, pvp: true));
        Assert.Equal(5, def.GetLevel(2, 0, pvp: true));
        Assert.Empty(def.ExecutableStates);
    }

    [Fact]
    public void CharacterDefResolvesPathsRelativeToTheCharacterFile()
    {
        var def = new CharacterDef(8, "character/mage/atmage.chr", new[]
        {
            Line("[job]", "[at mage]"),
            Line("[waiting motion]", "animation/stand.ani"),
            Line("[etc motion]", "animation/a.ani", "animation/b.ani"),
            Line("[etc attack info]", "attackinfo/x.atk"),
        });
        Assert.Equal("[at mage]", def.JobTag);
        Assert.Equal("character/mage/animation/b.ani", def.CustomAnimationPath(1));
        Assert.Null(def.CustomAnimationPath(2));
        Assert.Null(def.CustomAnimationPath(-1));
        Assert.Equal("character/mage/attackinfo/x.atk", def.CustomAttackInfoPath(0));
        Assert.Equal("character/mage/animation/stand.ani", def.MotionPath("[waiting motion]"));
        Assert.Null(def.MotionPath("[nothing]"));
    }

    [Fact]
    public void TagTableIndexesTheFirstOccurrenceCaseInsensitively()
    {
        var t = new TagTable(new[] { Line("[A]", 1, 2), Line("[a]", 3), Line("[name]", "x") });
        Assert.Equal(new[] { 1, 2 }, t.Ints("[a]"));
        Assert.Equal(1, t.IntOr("[A]", 9));
        Assert.Equal(9, t.IntOr("[none]", 9));
        Assert.Equal("x", t.FirstString("[NAME]"));
        Assert.Empty(t.Strings("[none]"));
    }

    [Fact]
    public void ListParsingPairsNumbersWithPaths()
    {
        Dictionary<int, string> map = TagTable.ParseList(new[] { Line(null, 5, "a.ani", 6, "b.ani", 5, "dup.ani") });
        Assert.Equal("a.ani", map[5]);
        Assert.Equal("b.ani", map[6]);
        Assert.Equal(2, map.Count);
    }

    // ------------------------------------------------------------------ 计时事件

    [Fact]
    public void TimeEventsFireInOrderWithCountsStartingAtOne()
    {
        var set = new TimeEventSet();
        set.Set(0, 100, 3, false);
        var fired = new List<(int Index, int Count)>();
        for (int t = 0; t < 400; t++)
        {
            set.Advance(1, (i, n) =>
            {
                fired.Add((i, n));
                return false;
            });
        }
        Assert.Equal(new[] { (0, 1), (0, 2), (0, 3) }, fired);
        Assert.False(set.Exists(0));
    }

    [Fact]
    public void TimeEventFireImmediatelyAndStopRequest()
    {
        var set = new TimeEventSet();
        set.Set(1, 50, 9999, true);
        int calls = 0;
        set.Advance(1, (i, n) =>
        {
            calls++;
            return true; // 要求中止
        });
        Assert.Equal(1, calls);
        Assert.False(set.Exists(1));
    }

    [Fact]
    public void SettingTheSameIndexAgainReplacesTheOldEvent()
    {
        var set = new TimeEventSet();
        set.Set(2, 100, 1, false);
        set.Advance(90, (i, n) => false);
        set.Set(2, 100, 1, false); // 重新计时
        int calls = 0;
        set.Advance(50, (i, n) =>
        {
            calls++;
            return false;
        });
        Assert.Equal(0, calls);
        set.Advance(50, (i, n) =>
        {
            calls++;
            return false;
        });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void TimeEventCallbackMayRemoveOrReplaceEvents()
    {
        var set = new TimeEventSet();
        set.Set(0, 10, 100, false);
        set.Set(1, 10, 100, false);
        var order = new List<string>();
        set.Advance(10, (i, n) =>
        {
            order.Add(i + ":" + n);
            if (i == 0)
            {
                set.Remove(1);
                set.Set(5, 10, 1, true);
            }
            return false;
        });
        Assert.Equal(new[] { "0:1" }, order);
        Assert.False(set.Exists(1));
        Assert.True(set.Exists(5));
    }

    // ------------------------------------------------------------------ 动画播放器

    private static AniFile Ani(AniBuilder b) => AniFile.Parse(b.ToBytes());

    [Fact]
    public void AnimatorReportsFlagsAtFrameEntryAndEndForNonLoopingClips()
    {
        var a = new Animator();
        a.Set(Ani(new AniBuilder().Frame(50, 1).Frame(50, 2).Frame(30)), "x.ani");
        var log = new List<string>();
        for (int t = 0; t < 200; t++)
        {
            a.Advance(1, f => log.Add("flag" + f + "@" + t), () => log.Add("end@" + t));
        }
        Assert.Equal(new[] { "flag1@0", "flag2@49", "end@129" }, log);
        Assert.True(a.Ended);
    }

    [Fact]
    public void AnimatorLoopsAndNeverEnds()
    {
        var a = new Animator();
        a.Set(Ani(new AniBuilder().Loop().Frame(30, 7).Frame(30)), "loop.ani");
        int flags = 0;
        int ends = 0;
        for (int t = 0; t < 305; t++)
        {
            a.Advance(1, f => flags++, () => ends++);
        }
        Assert.Equal(0, ends);
        Assert.InRange(flags, 5, 6); // 每 60 ms 一次
        Assert.False(a.Ended);
    }

    [Fact]
    public void AnimatorStopsAdvancingWhenACallbackSetsAnotherClip()
    {
        var a = new Animator();
        a.Set(Ani(new AniBuilder().Frame(10, 1).Frame(10, 2)), "first.ani");
        var seen = new List<int>();
        a.Advance(100, f =>
        {
            seen.Add(f);
            a.Set(Ani(new AniBuilder().Frame(10, 9)), "second.ani");
        }, () => seen.Add(-1));
        // 回调里换了动画:第一次推进到此为止,新动画的第 0 帧标志在下一次推进时报告
        Assert.Equal(new[] { 1 }, seen);
        Assert.Equal("second.ani", a.Path);
        a.Advance(1, f => seen.Add(f), () => seen.Add(-1));
        Assert.Equal(new[] { 1, 9 }, seen);
    }

    [Fact]
    public void AnimatorWithNoClipDoesNothing()
    {
        var a = new Animator();
        a.Set(null, null);
        a.Advance(100, f => throw new InvalidOperationException(), () => throw new InvalidOperationException());
        Assert.True(a.Ended);
        Assert.Null(a.CurrentFrame);
    }

    [Fact]
    public void AnimatorZeroDelayFramesPassInstantly()
    {
        var a = new Animator();
        a.Set(Ani(new AniBuilder().Frame(0, 1).Frame(0, 2).Frame(10, 3)), "z.ani");
        var flags = new List<int>();
        a.Advance(1, f => flags.Add(f), null);
        Assert.Equal(new[] { 1, 2, 3 }, flags);
    }

    // ------------------------------------------------------------------ 几何

    [Fact]
    public void BoxesOverlapOnlyWhenAllThreeAxesOverlap()
    {
        var a = new Aabb(0, 10, 0, 10, 0, 10);
        Assert.True(a.Overlaps(new Aabb(5, 15, 5, 15, 5, 15)));
        Assert.False(a.Overlaps(new Aabb(10, 20, 0, 10, 0, 10))); // 只贴边不算重叠
        Assert.False(a.Overlaps(new Aabb(0, 10, 20, 30, 0, 10)));
        Assert.False(a.Overlaps(new Aabb(0, 10, 0, 10, 11, 20)));
    }

    [Fact]
    public void BoxMirroringKeepsTheNearEdgeInFrontOfTheObject()
    {
        var box = new AniBox(AniBoxType.Attack, 10, -5, 0, 50, 10, 50);
        Aabb right = Combat.ToWorld(box, 100, 100, 0, Direction.Right);
        Aabb left = Combat.ToWorld(box, 100, 100, 0, Direction.Left);
        Assert.Equal((110, 160), (right.MinX, right.MaxX));
        Assert.Equal((40, 90), (left.MinX, left.MaxX));
        Assert.Equal((95, 105), (right.MinY, right.MaxY));
        Assert.Equal((0, 50), (right.MinZ, right.MaxZ));
    }
}
