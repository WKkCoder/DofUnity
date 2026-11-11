namespace Dnf.Game.Tests;

/// <summary>
/// 集成补强(主线验收时补):成就的完成判定要三个计数全为零。
/// 既有测试只把第一个计数减到零,没有"首尾两个计数归零、中间还剩"的情形;
/// 本文件把中间计数还剩时不算完成、补上后才完成这条边界钉死。
/// 数据全是合成的(成就编号 9003,计数 2、1、3)。
/// </summary>
public class AchievementCompletionBoundaryTests
{
    [Fact]
    public void ZeroingTheOuterCountersLeavesTheMiddleOneIncomplete()
    {
        AchievementTracker tracker = new(TitleFixtures.Achievements());   // 9003:计数 2、1、3,最低等级 1
        Assert.Equal(AchievementTriggerCode.Ok, tracker.Trigger(9003, 2, 0, 3, 1, out bool done));
        Assert.False(done);
        Assert.False(tracker.IsCleared(9003));
        Assert.Equal((0, 1, 0), (tracker.ProgressOf(9003).Remaining0, tracker.ProgressOf(9003).Remaining1, tracker.ProgressOf(9003).Remaining2));
        Assert.False(tracker.ProgressOf(9003).IsComplete);

        Assert.Equal(AchievementTriggerCode.Ok, tracker.Trigger(9003, 0, 1, 0, 1, out bool completed));
        Assert.True(completed);
        Assert.True(tracker.IsCleared(9003));
        Assert.True(tracker.ProgressOf(9003).IsComplete);
    }
}
