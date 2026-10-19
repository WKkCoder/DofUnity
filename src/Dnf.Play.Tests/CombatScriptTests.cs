using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>按时间表替主角按键(证据 E-069、E-072):解析、实时读取与固定步长推进。不依赖游戏数据。</summary>
public class CombatScriptTests
{
    private sealed class Recorder : IActionSession
    {
        public readonly List<(double Delta, int DirX, int DirY, bool Attack, bool Jump)> Calls = new();

        public string MapPath => "x";
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Calls.Add((deltaMs, dirX, dirY, false, false));
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed)
        {
            Calls.Add((deltaMs, dirX, dirY, attackPressed, jumpPressed));
        }

        public List<SceneSprite> Build(long timeMs)
        {
            return new List<SceneSprite>();
        }
    }

    private sealed class SkillRecorder : ISkillSession
    {
        public readonly List<(bool Attack, bool Jump, SkillKeys Skills)> Calls = new();

        public string MapPath => "x";
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Calls.Add((false, false, (SkillKeys)(-1)));
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed)
        {
            Calls.Add((attackPressed, jumpPressed, (SkillKeys)(-1)));          // 带技能键的会话应该走六个参数的那一个
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed, SkillKeys skillsPressed)
        {
            Calls.Add((attackPressed, jumpPressed, skillsPressed));
        }

        public List<SceneSprite> Build(long timeMs)
        {
            return new List<SceneSprite>();
        }
    }

    [Fact]
    public void SkillItemsFireOncePerKeyAndCombineWithTheOtherActions()
    {
        CombatScript s = CombatScript.Parse("1:a,2:right+s+d+attack,3:none,4:jump+a");
        Assert.False(s.Poll(999, out _, out _, out _, out SkillKeys skills));
        Assert.Equal(SkillKeys.None, skills);
        s.Poll(1000, out _, out _, out _, out skills);
        Assert.Equal(SkillKeys.A, skills);
        s.Poll(1001, out _, out _, out _, out skills);                        // 同一项不会再触发
        Assert.Equal(SkillKeys.None, skills);

        Assert.True(s.Poll(2000, out int dx, out _, out _, out skills));       // 同一项可以同时有技能键、方向与攻击键
        Assert.Equal((1, SkillKeys.S | SkillKeys.D), (dx, skills));
        s.Poll(2500, out _, out _, out _, out skills);
        Assert.Equal(SkillKeys.None, skills);

        // 不带技能键参数的重载把技能键也算作已读
        s.Poll(4000, out _, out _, out bool jump);
        Assert.True(jump);
        s.Poll(4001, out _, out _, out _, out skills);
        Assert.Equal(SkillKeys.None, skills);

        Assert.Throws<FormatException>(() => CombatScript.Parse("1:q"));
    }

    [Fact]
    public void StepGivesTheSkillKeysToASessionThatTakesThemAndStillWorksForOneThatDoesNot()
    {
        var rec = new SkillRecorder();
        CombatScript s = CombatScript.Parse("0.05:s,0.1:a+d");
        for (int i = 0; i < 8; i++)
        {
            s.Step(rec);
        }
        // 步 3(50.0 毫秒)按 S;步 6(100.0 毫秒)按 A 与 D;其余没有技能键
        Assert.Equal(new[] { SkillKeys.None, SkillKeys.None, SkillKeys.None, SkillKeys.S, SkillKeys.None, SkillKeys.None, SkillKeys.A | SkillKeys.D, SkillKeys.None },
            rec.Calls.Select(c => c.Skills).ToArray());

        var plain = new Recorder();
        CombatScript t = CombatScript.Parse("0:s");
        t.Step(plain);                                                         // 只认五个参数的会话照常工作(技能键被丢掉)
        Assert.Single(plain.Calls);
    }

    [Fact]
    public void AnEmptyOrBlankScriptIsEmptyAndPressesNothing()
    {
        Assert.True(CombatScript.Parse(null).IsEmpty);
        Assert.True(CombatScript.Parse("").IsEmpty);
        Assert.True(CombatScript.Parse("   ").IsEmpty);
        CombatScript s = CombatScript.Parse("");
        Assert.False(s.Poll(5000, out int dx, out int dy));
        Assert.Equal((0, 0), (dx, dy));
    }

    [Fact]
    public void ItemsAreSortedByTimeAndDirectionsHoldUntilTheNextItem()
    {
        CombatScript s = CombatScript.Parse("3:none,0.5:left+up,1:right");     // 故意乱序
        Assert.False(s.IsEmpty);
        s.Poll(0.49 * 1000, out int dx, out int dy);
        Assert.Equal((0, 0), (dx, dy));
        s.Poll(0.5 * 1000, out dx, out dy);                                    // 到点(含边界)
        Assert.Equal((-1, -1), (dx, dy));
        s.Poll(900, out dx, out dy);
        Assert.Equal((-1, -1), (dx, dy));
        s.Poll(1000, out dx, out dy);
        Assert.Equal((1, 0), (dx, dy));                                        // 下一项没写 up:方向整个换掉
        s.Poll(2999, out dx, out dy);
        Assert.Equal((1, 0), (dx, dy));
        s.Poll(3000, out dx, out dy);
        Assert.Equal((0, 0), (dx, dy));
    }

    [Fact]
    public void AnAttackItemFiresExactlyOnceWhenItsTimeIsReached()
    {
        CombatScript s = CombatScript.Parse("1:attack,2:down+attack,2.5:none");
        Assert.False(s.Poll(999, out _, out _));
        Assert.True(s.Poll(1000, out int dx, out int dy));
        Assert.Equal((0, 0), (dx, dy));
        Assert.False(s.Poll(1001, out _, out _));                              // 同一项不会再触发
        Assert.False(s.Poll(1500, out _, out _));
        Assert.True(s.Poll(2000, out dx, out dy));
        Assert.Equal((0, 1), (dx, dy));
        Assert.False(s.Poll(2000, out _, out _));

        // 一次读取跨过两项攻击:合并成一个边沿(两项都标记为已触发)
        CombatScript t = CombatScript.Parse("1:attack,1.1:attack");
        Assert.True(t.Poll(5000, out _, out _));
        Assert.False(t.Poll(6000, out _, out _));
    }

    [Fact]
    public void AJumpItemFiresExactlyOnceAndIsIndependentOfTheAttackKey()
    {
        CombatScript s = CombatScript.Parse("1:jump,2:right+jump+attack,3:none");
        Assert.False(s.Poll(999, out _, out _, out bool jump));
        Assert.False(jump);
        Assert.False(s.Poll(1000, out int dx, out int dy, out jump));        // 只有 jump:攻击键不触发
        Assert.True(jump);
        Assert.Equal((0, 0), (dx, dy));
        s.Poll(1001, out _, out _, out jump);                                 // 同一项不会再触发
        Assert.False(jump);
        Assert.True(s.Poll(2000, out dx, out dy, out jump));                  // 同一项可以同时有 jump 与 attack
        Assert.True(jump);
        Assert.Equal((1, 0), (dx, dy));
        s.Poll(2000, out _, out _, out jump);
        Assert.False(jump);

        // 三参数的重载忽略跳跃键(也把它算作已读)
        CombatScript t = CombatScript.Parse("1:jump");
        Assert.False(t.Poll(1000, out _, out _));
        t.Poll(2000, out _, out _, out jump);
        Assert.False(jump);
    }

    [Fact]
    public void ATapReleaseTapPatternCanBeWrittenAsATimeTable()
    {
        // 双击右:0 秒按下、0.1 秒松开、0.2 秒再按下并保持
        CombatScript s = CombatScript.Parse("0:right,0.1:none,0.2:right");
        s.Poll(50, out int dx, out _);
        Assert.Equal(1, dx);
        s.Poll(150, out dx, out _);
        Assert.Equal(0, dx);
        s.Poll(250, out dx, out _);
        Assert.Equal(1, dx);
    }

    [Fact]
    public void StepAdvancesTheSessionInFixedStepsAndTheClockByTheSameAmount()
    {
        var rec = new Recorder();
        CombatScript s = CombatScript.Parse("0:right,0.05:right+attack,0.1:none");
        for (int i = 0; i < 8; i++)
        {
            s.Step(rec);
        }
        Assert.Equal(8, rec.Calls.Count);
        Assert.All(rec.Calls, c => Assert.Equal(1000.0 / 60.0, c.Delta));
        Assert.Equal(8 * (1000.0 / 60.0), s.TimeMs, 9);
        // 步 0..2(时刻 0、16.7、33.3 毫秒)向右;步 3(50.0 毫秒)起攻击键触发一次;步 6(100.0 毫秒)起停下
        Assert.Equal((1, 0), (rec.Calls[0].DirX, rec.Calls[0].DirY));
        Assert.Equal(new[] { false, false, false, true, false, false, false, false }, rec.Calls.Select(c => c.Attack).ToArray());
        Assert.Equal((1, 0), (rec.Calls[5].DirX, rec.Calls[5].DirY));
        Assert.Equal((0, 0), (rec.Calls[7].DirX, rec.Calls[7].DirY));
        Assert.All(rec.Calls, c => Assert.False(c.Jump));
    }

    [Fact]
    public void StepPassesTheJumpEdgeToTheSession()
    {
        var rec = new Recorder();
        CombatScript s = CombatScript.Parse("0.05:jump");
        for (int i = 0; i < 6; i++)
        {
            s.Step(rec);
        }
        Assert.Equal(new[] { false, false, false, true, false, false }, rec.Calls.Select(c => c.Jump).ToArray());
        Assert.All(rec.Calls, c => Assert.False(c.Attack));
    }

    [Fact]
    public void RunUntilStopsAtTheFirstStepNotEarlierThanTheTarget()
    {
        var rec = new Recorder();
        CombatScript s = CombatScript.Parse("");
        s.RunUntil(rec, 100);
        // 100 / 16.667 = 6 步整:累计 6 × (1000/60) 毫秒(浮点上可能略小于 100,多走一步也算对,但时刻不能早于目标)
        Assert.True(s.TimeMs >= 100);
        Assert.True(s.TimeMs < 100 + 1000.0 / 60.0 + 1e-9);
        int calls = rec.Calls.Count;
        s.RunUntil(rec, 50);                                                   // 已经过了:不再推进
        Assert.Equal(calls, rec.Calls.Count);
        Assert.Throws<ArgumentNullException>(() => s.Step(null));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("a:left")]
    [InlineData("1:left:right")]
    [InlineData("1:dance")]
    [InlineData("1:left+fly")]
    public void MalformedItemsAreRejected(string text)
    {
        Assert.Throws<FormatException>(() => CombatScript.Parse(text));
    }
}
