using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>按时间表替主角按键(证据 E-069):解析、实时读取与固定步长推进。不依赖游戏数据。</summary>
public class CombatScriptTests
{
    private sealed class Recorder : IActionSession
    {
        public readonly List<(double Delta, int DirX, int DirY, bool Attack)> Calls = new();

        public string MapPath => "x";
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Calls.Add((deltaMs, dirX, dirY, false));
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed)
        {
            Calls.Add((deltaMs, dirX, dirY, attackPressed));
        }

        public List<SceneSprite> Build(long timeMs)
        {
            return new List<SceneSprite>();
        }
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
    [InlineData("1:jump")]
    [InlineData("1:left+fly")]
    public void MalformedItemsAreRejected(string text)
    {
        Assert.Throws<FormatException>(() => CombatScript.Parse(text));
    }
}
