using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网副本房间的出生点计划(<see cref="RoomSpawnPlan.Fill"/>,r15-dungeonhost):按对应表里的怪数,用房间地图自己的 [monster] 当模板循环凑出出生点表。
/// 纯函数,数据全是合成的出生点(怪物编号只当标签,不查任何目录)。
/// </summary>
public class RoomSpawnPlanTests
{
    private static RoomMonsterSpawn S(int code, int x) => new RoomMonsterSpawn(code, 0, 0, x, 100, 0, 1, 0, "[fixed]", "[normal]");

    private static int[] Codes(IReadOnlyList<RoomMonsterSpawn> plan) => plan.Select(s => s.Code).ToArray();

    [Fact]
    public void As_many_templates_as_records_gives_the_templates_in_order()
    {
        var templates = new[] { S(11, 100), S(12, 200), S(13, 300) };

        IReadOnlyList<RoomMonsterSpawn> plan = RoomSpawnPlan.Fill(3, templates);

        Assert.Equal(new[] { 11, 12, 13 }, Codes(plan));
        Assert.Equal(new[] { 100, 200, 300 }, plan.Select(s => s.X).ToArray());
    }

    [Fact]
    public void Fewer_templates_than_records_are_cycled_in_order()
    {
        var templates = new[] { S(11, 100), S(12, 200) };

        IReadOnlyList<RoomMonsterSpawn> plan = RoomSpawnPlan.Fill(5, templates);

        Assert.Equal(new[] { 11, 12, 11, 12, 11 }, Codes(plan));
        Assert.Equal(5, plan.Count);
    }

    [Fact]
    public void More_templates_than_records_only_the_first_ones_are_used()
    {
        // 房间里的怪物只能比对应表少或相等:多出来的模板不能变成没有服务端编号的怪
        var templates = new[] { S(11, 100), S(12, 200), S(13, 300), S(14, 400) };

        IReadOnlyList<RoomMonsterSpawn> plan = RoomSpawnPlan.Fill(2, templates);

        Assert.Equal(new[] { 11, 12 }, Codes(plan));
    }

    [Fact]
    public void Without_templates_the_plan_is_empty_whatever_the_count()
    {
        Assert.Empty(RoomSpawnPlan.Fill(4, null));
        Assert.Empty(RoomSpawnPlan.Fill(4, new List<RoomMonsterSpawn>()));
    }

    [Fact]
    public void Zero_records_give_an_empty_plan()
    {
        Assert.Empty(RoomSpawnPlan.Fill(0, new[] { S(11, 100) }));
    }

    [Fact]
    public void A_negative_count_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomSpawnPlan.Fill(-1, new[] { S(11, 100) }));
    }

    [Fact]
    public void The_plan_is_a_fresh_list_and_the_templates_are_left_alone()
    {
        var templates = new List<RoomMonsterSpawn> { S(11, 100), S(12, 200) };

        IReadOnlyList<RoomMonsterSpawn> first = RoomSpawnPlan.Fill(2, templates);
        IReadOnlyList<RoomMonsterSpawn> second = RoomSpawnPlan.Fill(2, templates);

        Assert.NotSame(first, second);
        Assert.NotSame(templates, first);
        Assert.Equal(Codes(first), Codes(second));      // 同样的输入永远得到同样的计划
        Assert.Equal(new[] { 11, 12 }, templates.Select(s => s.Code).ToArray());
        Assert.Equal(2, templates.Count);
    }
}
