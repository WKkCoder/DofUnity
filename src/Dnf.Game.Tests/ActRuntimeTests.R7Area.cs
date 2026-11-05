using Dnf.Data;
using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// [AREA]、[IS IN AREA]、[IS DAMAGE ENABLE](M107)。
/// 期望值手算,算式写在注释里。位置按整数比较(客户端取整数坐标;小数部分向零截断,与既有条件一致)。
/// </summary>
public sealed partial class ActRuntimeTests
{
    private static Entity At(float x, float y, float z = 0, bool left = false) => new Entity { X = x, Y = y, Z = z, FacingLeft = left };

    private static ScriptLine[] InArea(string coordinate, int[] pos, int[] range)
    {
        var lines = new List<ScriptLine> { L("[IS IN AREA]") };
        if (pos != null)
        {
            lines.Add(L("[POS]", pos.Cast<object>().ToArray()));
        }
        if (range != null)
        {
            lines.Add(L("[RANGE]", range.Cast<object>().ToArray()));
        }
        if (coordinate != null)
        {
            lines.Add(L("[COORDINATE]", coordinate));
        }
        lines.Add(L("[/IS IN AREA]"));
        return lines.ToArray();
    }

    // ------------------------------------------------------------------ [AREA]

    [Fact]
    public void AreaCompilesExactlyFourIntegersOrStaysUndone()
    {
        ActRecord r = Filter(L("[AREA]", 1, 2, 3, 4)).Triggers[0].Records.First(x => x.Kind == ActRecordKind.Area);
        Assert.Equal(new[] { 1, 2, 3, 4 }, r.Ints);
        // 反例:少写、不写、多写(全库有六个整数的写法,客户端加载分支只读四个、多的怎么处理没读通)都读不懂
        foreach (ScriptLine[] bad in new[]
        {
            Lines(L("[AREA]", 1, 2, 3)), Lines(L("[AREA]")), Lines(L("[AREA]", 1, 2, 3, 4, 5, 6)),
        })
        {
            ActProgram p = Filter(bad);
            Assert.Contains(ActRecordKind.Unsupported, p.Triggers[0].Records.Select(x => x.Kind));
            Assert.Contains("[AREA]", p.Triggers[0].UnsupportedTags);
        }
    }

    [Fact]
    public void AreaKeepsTargetsInsideTheBoxAroundTheOwnerOffsetInclusiveOfEveryEdge()
    {
        // 拥有者 (100, 50),[AREA] 10 -5 20 8:
        //   中心 = (100+10, 50-5) = (110, 45);X 范围 [110-20, 110+20] = [90, 130];Y 范围 [45-8, 45+8] = [37, 53]
        Entity[] items =
        {
            At(110, 45),       // 中心
            At(90, 45),        // X 下边界
            At(130, 45),       // X 上边界
            At(110, 37),       // Y 下边界
            At(110, 53),       // Y 上边界
            At(89, 45),        // X 下边界外 1
            At(131, 45),       // X 上边界外 1
            At(110, 36),       // Y 下边界外 1
            At(110, 54),       // Y 上边界外 1
            At(90, 37),        // 左下角
            At(130, 53),       // 右上角
            At(110, 45, 999, true),      // 高度与朝向不参与
            At(130.9f, 45),    // 取整数:(int)130.9 = 130,在边界上
        };
        bool[] expected = { true, true, true, true, true, false, false, false, false, true, true, true, true };
        Assert.Equal(expected, Kept(Filter(L("[AREA]", 10, -5, 20, 8)), At(100, 50), items));
    }

    [Fact]
    public void AreaBoxFollowsTheOwner()
    {
        // 同一个 [AREA] 10 -5 20 8,拥有者移到 (200, 80):中心 (210, 75),X [190, 230],Y [67, 83]
        ActProgram p = Filter(L("[AREA]", 10, -5, 20, 8));
        Assert.Equal(new[] { false, true, true, false }, Kept(p, At(200, 80), At(110, 45), At(210, 75), At(190, 67), At(189, 67)));
    }

    [Fact]
    public void AreaWithZeroHalfExtentsIsASinglePointAndNegativeExtentsAreEmpty()
    {
        // 半宽半高 0:只有中心点本身。拥有者 (7, 9),偏移 (0, 0)
        Assert.Equal(new[] { true, false, false, false, false }, Kept(Filter(L("[AREA]", 0, 0, 0, 0)), At(7, 9), At(7, 9), At(8, 9), At(6, 9), At(7, 10), At(7, 8)));
        // 半宽 -1:X 范围 [0-(-1), 0+(-1)] = [1, -1],空框,连中心点也不在里面
        Assert.Equal(new[] { false, false }, Kept(Filter(L("[AREA]", 0, 0, -1, 5)), At(0, 0), At(0, 0), At(1, 0)));
    }

    [Fact]
    public void AreaOnAnEmptySetPasses()
    {
        // 边界:空集合照样通过 → [CHECKED NO] [=] 0 触发;对照:一个在框外的对象被剔除后同样触发,在框内的则不触发
        ScriptLine[] cond = Lines(L("[AREA]", 0, 0, 5, 5));
        Assert.True(EmptyAfterAll(cond, At(0, 0), new Host()));
        Assert.True(EmptyAfterAll(cond, At(0, 0), new Host(), At(6, 0)));
        Assert.False(EmptyAfterAll(cond, At(0, 0), new Host(), At(5, 0)));
    }

    // ------------------------------------------------------------------ [IS IN AREA]

    [Fact]
    public void IsInAreaCompilesSubTagsInAnyOrderWithDefaults()
    {
        ActRecord full = Filter(InArea("RELATIVE", new[] { 10, 20, 30 }, new[] { 5, 6, 7 })).Triggers[0].Records.First(x => x.Kind == ActRecordKind.IsInArea);
        Assert.Equal(new[] { 10, 20, 30, 5, 6, 7 }, full.Ints);
        Assert.Equal(CoordRelative, full.Number);

        // 次序随意:[RANGE] 在前、[COORDINATE] 在中间
        ActRecord shuffled = Filter(L("[IS IN AREA]"), L("[RANGE]", 5, 6, 7), L("[COORDINATE]", "RELATIVE"), L("[POS]", 10, 20, 30), L("[/IS IN AREA]"))
            .Triggers[0].Records.First(x => x.Kind == ActRecordKind.IsInArea);
        Assert.Equal(full.Ints, shuffled.Ints);
        Assert.Equal(full.Number, shuffled.Number);

        // 缺省:没写的中心 0、半宽 0、绝对;后写的盖前写的
        ActRecord onlyRange = Filter(InArea(null, null, new[] { 5, 6, 7 })).Triggers[0].Records.First(x => x.Kind == ActRecordKind.IsInArea);
        Assert.Equal(new[] { 0, 0, 0, 5, 6, 7 }, onlyRange.Ints);
        Assert.Equal(CoordAbsolute, onlyRange.Number);
        ActRecord later = Filter(L("[IS IN AREA]"), L("[POS]", 0, 0, 0), L("[POS]", 10, 20, 30), L("[/IS IN AREA]")).Triggers[0].Records.First(x => x.Kind == ActRecordKind.IsInArea);
        Assert.Equal(new[] { 10, 20, 30, 0, 0, 0 }, later.Ints);
    }

    // 坐标写法编号在测试里独立写一遍(0 绝对、1 相对),不引用实现里的常量
    private const int CoordAbsolute = 0;

    private const int CoordRelative = 1;

    [Fact]
    public void IsInAreaWithUnreadableShapesStaysUndone()
    {
        foreach (ScriptLine[] bad in new[]
        {
            Lines(L("[IS IN AREA]"), L("[/IS IN AREA]")),                                                   // 一个子标签也没有
            Lines(L("[IS IN AREA]"), L("[POS]", 1, 2), L("[/IS IN AREA]")),                                 // [POS] 少一个整数
            Lines(L("[IS IN AREA]"), L("[RANGE]", 1, 2, 3, 4), L("[/IS IN AREA]")),                         // [RANGE] 多一个整数
            Lines(L("[IS IN AREA]"), L("[X POS]", 1), L("[/IS IN AREA]")),                                  // 单轴写法这一版不认
            Lines(L("[IS IN AREA]"), L("[POS]", 1, 2, 3), L("[COORDINATE]", "relative"), L("[/IS IN AREA]")),  // 大小写不同:客户端查表是否区分大小写没读通
            Lines(L("[IS IN AREA]"), L("[POS]", 1, 2, 3), L("[COORDINATE]"), L("[/IS IN AREA]")),           // 没写字符串
        })
        {
            ActProgram p = Filter(bad);
            Assert.Contains("[IS IN AREA]", p.Triggers[0].UnsupportedTags);
        }
    }

    [Fact]
    public void IsInAreaAbsoluteBoxIsInclusiveOnEveryAxisAndIgnoresTheOwner()
    {
        // 中心 (10, 20, 30),半宽 (5, 6, 7):X [5, 15],Y [14, 26],Z [23, 37]
        ActProgram p = Filter(InArea(null, new[] { 10, 20, 30 }, new[] { 5, 6, 7 }));
        Entity[] items =
        {
            At(10, 20, 30),    // 中心
            At(5, 14, 23),     // 下角
            At(15, 26, 37),    // 上角
            At(4, 20, 30),     // X 低 1
            At(16, 20, 30),    // X 高 1
            At(10, 13, 30),    // Y 低 1
            At(10, 27, 30),    // Y 高 1
            At(10, 20, 22),    // Z 低 1
            At(10, 20, 38),    // Z 高 1
        };
        bool[] expected = { true, true, true, false, false, false, false, false, false };
        Assert.Equal(expected, Kept(p, At(1000, 1000, 1000), items));
        // 写 "ABSOLUTE" 与不写等价
        Entity[] again = { At(10, 20, 30), At(4, 20, 30) };
        Assert.Equal(new[] { true, false }, Kept(Filter(InArea("ABSOLUTE", new[] { 10, 20, 30 }, new[] { 5, 6, 7 })), At(1000, 1000, 1000), again));
    }

    [Fact]
    public void IsInAreaRelativeAddsTheOwnerCoordinates()
    {
        // 拥有者 (100, 200, 0):中心 = (10+100, 20+200, 30+0) = (110, 220, 30);X [105, 115],Y [214, 226],Z [23, 37]
        ActProgram p = Filter(InArea("RELATIVE", new[] { 10, 20, 30 }, new[] { 5, 6, 7 }));
        Entity[] items =
        {
            At(110, 220, 30),   // 中心
            At(105, 214, 23),   // 下角
            At(115, 226, 37),   // 上角
            At(104, 220, 30),   // X 低 1
            At(116, 220, 30),   // X 高 1
            At(10, 20, 30),     // 绝对写法的中心,在相对盒子外
        };
        Assert.Equal(new[] { true, true, true, false, false, false }, Kept(p, At(100, 200, 0), items));

        // 拥有者在空中 Z=10:中心 Z = 30+10 = 40,Z [33, 47]:Z=30 在外,Z=40 在内
        Assert.Equal(new[] { false, true }, Kept(p, At(100, 200, 10), At(110, 220, 30), At(110, 220, 40)));
    }

    [Fact]
    public void IsInAreaDefaultsToAPointAtTheOriginWhenOnlyOneOfPosAndRangeIsWritten()
    {
        // 只写 [RANGE] 5 6 7:中心 (0,0,0),X [-5,5] Y [-6,6] Z [-7,7]
        Assert.Equal(new[] { true, true, false, false }, Kept(Filter(InArea(null, null, new[] { 5, 6, 7 })), At(50, 50, 50), At(0, 0, 0), At(5, 6, 7), At(6, 0, 0), At(0, 0, -8)));
        // 只写 [POS] 10 20 30:半宽 0,只有 (10,20,30) 这一点
        Assert.Equal(new[] { true, false }, Kept(Filter(InArea(null, new[] { 10, 20, 30 }, null)), At(0, 0), At(10, 20, 30), At(11, 20, 30)));
    }

    [Fact]
    public void IsInAreaWithAnUnknownCoordinateWordIsFalseEvenForAnEmptySet()
    {
        ScriptLine[] bad = InArea("WORLD", new[] { 10, 20, 30 }, new[] { 5, 6, 7 });
        // 对象就在中心也留不下;空集合在写法合格时是通过的(下面第二条),写法不认得时整条条件为假、不看工作集
        Assert.Equal(new[] { false }, Kept(Filter(bad), At(0, 0), At(10, 20, 30)));
        Assert.False(EmptyAfterAll(bad, At(0, 0), new Host()));
        Assert.True(EmptyAfterAll(InArea("ABSOLUTE", new[] { 10, 20, 30 }, new[] { 5, 6, 7 }), At(0, 0), new Host()));
    }

    // ------------------------------------------------------------------ [IS DAMAGE ENABLE]

    [Fact]
    public void IsDamageEnableCompilesWithoutParameters()
    {
        ActProgram p = Filter(L("[IS DAMAGE ENABLE]"));
        Assert.Contains(ActRecordKind.IsDamageEnable, p.Triggers[0].Records.Select(x => x.Kind));
        Assert.Empty(p.Triggers[0].UnsupportedTags);
    }

    private static SymEntity Damage(bool? enabled, ActEntityKind kind = ActEntityKind.Monster) => new SymEntity { Kind = kind, DamageEnabled = enabled };

    [Fact]
    public void IsDamageEnableKeepsOnlyObjectsWhoseDamageSwitchIsOn()
    {
        Assert.Equal(new[] { true, false, true }, Kept(Filter(L("[IS DAMAGE ENABLE]")), new Entity(), Damage(true), Damage(false), Damage(true, ActEntityKind.Character)));
    }

    [Fact]
    public void IsDamageEnableDoesNotEvaluateWhenAnyTargetsSwitchIsUnknown()
    {
        // 开关状态没读通的对象(null)在工作集里:整条触发器放弃,连开着的那个也不被选中(与没实现这条条件之前一样)
        Assert.Equal(new[] { false, false }, Kept(Filter(L("[IS DAMAGE ENABLE]")), new Entity(), Damage(true), Damage(null)));
        // 对照:去掉不知道的那个,开着的就被选中
        Assert.Equal(new[] { true }, Kept(Filter(L("[IS DAMAGE ENABLE]")), new Entity(), Damage(true)));
        // 不实现受击开关的普通替身对象同样是"不知道"
        Assert.Equal(new[] { false }, Kept(Filter(L("[IS DAMAGE ENABLE]")), new Entity(), new Entity()));
    }

    [Fact]
    public void IsDamageEnableKeepsPassiveObjectsWhileIsDamageboxEnableDropsThem()
    {
        // [IS DAMAGE ENABLE] 把拥有者与对象都转成碰撞体(被动对象也转得过去),不按种类剔;
        // [IS DAMAGEBOX ENABLE] 转的是怪物类,被动对象转不过去被剔(既有行为)
        SymEntity passive = Damage(true, ActEntityKind.Passive);
        passive.IsActDamageBoxEnabled = true;
        Assert.Equal(new[] { true }, Kept(Filter(L("[IS DAMAGE ENABLE]")), new Entity(), passive));
        SymEntity passive2 = Damage(true, ActEntityKind.Passive);
        passive2.IsActDamageBoxEnabled = true;
        Assert.Equal(new[] { false }, Kept(Filter(L("[IS DAMAGEBOX ENABLE]", 1)), new Entity(), passive2));
    }

    [Fact]
    public void IsDamageEnableOnAnEmptySetPasses()
    {
        ScriptLine[] cond = Lines(L("[IS DAMAGE ENABLE]"));
        Assert.True(EmptyAfterAll(cond, new Entity(), new Host()));
        Assert.True(EmptyAfterAll(cond, new Entity(), new Host(), Damage(false)));
        Assert.False(EmptyAfterAll(cond, new Entity(), new Host(), Damage(true)));
    }
}
