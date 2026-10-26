using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 地图物件的掉落规则(证据 E-126,服务端静态读码):<c>.dgn</c> 里 [special passive object item] 与 [dungeon object drop] 的读法,
/// 一个 [item] 动作按"随机物品、指定物品、整座副本通用池"的顺序生成,各自的随机数范围(2 到 10000 与 1 到 10000)、
/// 累计权重、有符号字节的个数、等级规则。编号、权重与等级全是合成的。
/// </summary>
public class ObjectItemsTests
{
    private sealed class ScriptedRandom : IDropRandom
    {
        private readonly Queue<int> _values;

        public List<string> Calls { get; } = new();

        public ScriptedRandom(params int[] values)
        {
            _values = new Queue<int>(values);
        }

        public int NextInclusive(int max)
        {
            Calls.Add("N" + max);
            return _values.Dequeue();
        }

        public int Range(int lo, int hi)
        {
            Calls.Add("R" + lo + ":" + hi);
            return _values.Dequeue();
        }
    }

    private static ScriptValue I(int v) => new(ScriptValueKind.Int, v, null);

    private static ScriptLine Line(string tag, params int[] ints) => new(tag, ints.Select(I).ToArray());

    private static ItemInfo Info(int code) => new(code, "stackable/" + code + ".stk", ItemKind.Stackable);

    private static ItemInfo Resolve(int code) => code is >= 100 and < 300 ? Info(code) : null;

    private static DungeonObjectItems Items(int level, int[] pairs, params WeightedItem[] dungeon)
    {
        var list = new List<WeightedItem>();
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            list.Add(new WeightedItem(pairs[i], pairs[i + 1]));
        }
        return new DungeonObjectItems(new[] { new SpecialItemEntry(level, list) }, dungeon);
    }

    // ------------------------------------------------------------------ .dgn 的两个标签

    [Fact]
    public void ParseReadsEveryEntryAsOrdinalLevelCountAndPairsPlusTheDungeonWidePool()
    {
        DungeonObjectItems items = DungeonObjectItems.Parse(new[]
        {
            Line("[name]"),
            Line("[special passive object item]", 0, -1, 2, 101, 6000, 102, 4000, /**/ 1, 30, 0, /**/ 2, 55, 1, 103, 10000),
            Line("[dungeon object drop]", 201, 3000, 202, 7000),
        });
        Assert.Equal(3, items.Entries.Count);
        Assert.Equal(new[] { -1, 30, 55 }, items.Entries.Select(e => e.Level).ToArray());
        Assert.Equal(new[] { 2, 0, 1 }, items.Entries.Select(e => e.Items.Count).ToArray());
        Assert.Equal((101, 6000), (items.Entries[0].Items[0].ItemId, items.Entries[0].Items[0].Weight));
        Assert.Equal((102, 4000), (items.Entries[0].Items[1].ItemId, items.Entries[0].Items[1].Weight));
        Assert.Equal(103, items.Entries[2].Items[0].ItemId);
        Assert.Equal(new[] { 201, 202 }, items.DungeonDrop.Select(w => w.ItemId).ToArray());
        Assert.Equal(new[] { 3000, 7000 }, items.DungeonDrop.Select(w => w.Weight).ToArray());
    }

    [Fact]
    public void NoTagsGiveTheSharedEmptyTable()
    {
        Assert.Same(DungeonObjectItems.Empty, DungeonObjectItems.Parse(null));
        Assert.Same(DungeonObjectItems.Empty, DungeonObjectItems.Parse(new[] { Line("[name]") }));
        Assert.Empty(DungeonObjectItems.Empty.Entries);
        Assert.Empty(DungeonObjectItems.Empty.DungeonDrop);
    }

    [Fact]
    public void ADamagedEntryKeepsEverythingBeforeIt()
    {
        // 第二项声明了 3 对却只给了 1 对:停,第一项保留
        DungeonObjectItems items = DungeonObjectItems.Parse(new[]
        {
            Line("[special passive object item]", 0, 10, 1, 101, 500, /**/ 1, 20, 3, 102, 500),
        });
        Assert.Single(items.Entries);
        Assert.Equal(10, items.Entries[0].Level);
        // 个数是负数
        Assert.Empty(DungeonObjectItems.Parse(new[] { Line("[special passive object item]", 0, 10, -2, 1, 1) }).Entries);
        // 一个有两个标签的副本:后一个 [dungeon object drop] 覆盖前一个,[special passive object item] 累加
        DungeonObjectItems twice = DungeonObjectItems.Parse(new[]
        {
            Line("[dungeon object drop]", 201, 1),
            Line("[special passive object item]", 0, 10, 0),
            Line("[dungeon object drop]", 202, 2),
            Line("[special passive object item]", 1, 20, 0),
        });
        Assert.Equal(new[] { 202 }, twice.DungeonDrop.Select(w => w.ItemId).ToArray());
        Assert.Equal(2, twice.Entries.Count);
    }

    // ------------------------------------------------------------------ 一次挑选

    [Theory]
    [InlineData(2, 101)]
    [InlineData(2999, 101)]
    [InlineData(3000, 102)]               // 比较是"随机数 < 累计",正好等于累计的落到下一个
    [InlineData(6999, 102)]
    [InlineData(7000, -1)]
    [InlineData(10000, -1)]
    public void PickTakesTheFirstPairWhoseCumulativeWeightExceedsTheRoll(int roll, int expected)
    {
        var pairs = new[] { new WeightedItem(101, 3000), new WeightedItem(102, 4000) };
        Assert.Equal(expected, ObjectItemRoll.Pick(pairs, ObjectItemRoll.EntryPickMin, new ScriptedRandom(roll)));
    }

    [Fact]
    public void PickAsksForTheRangeTheServerUsesAndAnEmptyPoolStillConsumesARoll()
    {
        var random = new ScriptedRandom(5, 5, 5);
        Assert.Equal(-1, ObjectItemRoll.Pick(new List<WeightedItem>(), ObjectItemRoll.EntryPickMin, random));
        Assert.Equal(-1, ObjectItemRoll.Pick(new List<WeightedItem>(), ObjectItemRoll.DungeonPickMin, random));
        Assert.Equal(101, ObjectItemRoll.Pick(new[] { new WeightedItem(101, 99999) }, 1, random));
        Assert.Equal(new[] { "R2:10000", "R1:10000", "R1:10000" }, random.Calls);
        Assert.Equal((2, 1, 10000), (ObjectItemRoll.EntryPickMin, ObjectItemRoll.DungeonPickMin, ObjectItemRoll.PickMax));
    }

    // ------------------------------------------------------------------ 一个 [item] 动作

    [Fact]
    public void SpecificItemsComeFromTheEntryAndTheDungeonPoolIsRolledOnceAfterThem()
    {
        DungeonObjectItems items = Items(30, new[] { 101, 5000, 102, 5000 }, new WeightedItem(201, 10000));
        // 指定 3 次:100 → 101(100 < 5000);5000 → 102(不小于第一个累计 5000,小于第二个累计 10000);10000 → 没中(累计到 10000 为止,不小于);通用池再掷一次
        var random = new ScriptedRandom(100, 5000, 10000, 7);
        List<ItemInfo> got = ObjectItemRoll.Roll(items, 0, 3, 0, 20, 0, null, null, Resolve, random);
        Assert.Equal(new[] { 101, 102, 201 }, got.Select(i => i.Code).ToArray());
        Assert.Equal(new[] { "R2:10000", "R2:10000", "R2:10000", "R1:10000" }, random.Calls);
    }

    [Fact]
    public void RandomItemsAreRolledFirstThenTheSpecificOnesThenTheDungeonPool()
    {
        // 物件掉落表:每次"随机物品"掷一遍(类型 1 的概率 10000,其余 0);池里只有一件
        MonsterDropTable table = ObjectTable();
        var pool = new ItemPool(new[] { RandomStack(150) }, table);
        DungeonObjectItems items = Items(10, new[] { 101, 10001 }, new WeightedItem(201, 10001));
        // 每次掷一遍:类型 1:概率骰(0 → 掉)、稀有度骰(1)、取物品骰(5);类型 2、3、4 的概率骰各 1(> 0,不掉)
        var random = new ScriptedRandom(0, 1, 5, 1, 1, 1, /* 第二次 */ 0, 1, 5, 1, 1, 1, /* 指定 */ 100, /* 通用 */ 100);
        List<ItemInfo> got = ObjectItemRoll.Roll(items, 0, 1, 2, 20, 0, table, pool, Resolve, random);
        Assert.Equal(new[] { 150, 150, 101, 201 }, got.Select(i => i.Code).ToArray());
        Assert.Equal(new[]
        {
            "N10000", "R1:1000000", "N10", "N10000", "N10000", "N10000",
            "N10000", "R1:1000000", "N10", "N10000", "N10000", "N10000",
            "R2:10000", "R1:10000",
        }, random.Calls);
    }

    [Fact]
    public void WithoutTheObjectTableNoRandomItemsAreRolledAndNoRandomNumbersAreSpentOnThem()
    {
        DungeonObjectItems items = Items(10, new[] { 101, 10001 });
        var random = new ScriptedRandom(100, 100);
        List<ItemInfo> got = ObjectItemRoll.Roll(items, 0, 1, 5, 20, 0, null, null, Resolve, random);
        Assert.Equal(new[] { 101 }, got.Select(i => i.Code).ToArray());
        Assert.Equal(new[] { "R2:10000", "R1:10000" }, random.Calls);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-128, 0)]
    [InlineData(255, 0)]                  // 服务端把这两个数当有符号字节:255 就是 -1
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(256 + 2, 2)]              // 超过一个字节的部分被截掉
    public void CountsAreSignedBytes(int count, int expectedSpecific)
    {
        DungeonObjectItems items = Items(10, new[] { 101, 10001 });
        var random = new ScriptedRandom(Enumerable.Repeat(100, 20).ToArray());
        List<ItemInfo> got = ObjectItemRoll.Roll(items, 0, count, 0, 20, 0, null, null, Resolve, random);
        Assert.Equal(expectedSpecific, got.Count);
        Assert.Equal(expectedSpecific + 1, random.Calls.Count);          // 加上通用池的那一次
    }

    [Fact]
    public void TheEntryLevelWinsOverTheStandardLevelAndAMinusOneUsesTheStandardLevelWhichMustBeInRange()
    {
        // 合成的物件掉落表只有等级 1 到 50 的行,池里只有一件等级 10 的物品
        MonsterDropTable table = ObjectTable();
        var pool = new ItemPool(new[] { RandomStack(150) }, table);

        // 项自带等级 10:标准等级是 99(表里没有这一行)也照样掷出来 —— 说明用的是项自己的等级
        Assert.Equal(150, Assert.Single(ObjectItemRoll.Roll(Items(10, new int[0]), 0, 0, 1, 99, 0, table, pool, Resolve, RandomsForOneRoll())).Code);
        // 项的等级 -1:改用标准等级 10,掷得出来
        Assert.Equal(150, Assert.Single(ObjectItemRoll.Roll(Items(-1, new int[0]), 0, 0, 1, 10, 0, table, pool, Resolve, RandomsForOneRoll())).Code);
        // 标准等级 60 在 1 到 200 里但表里没有这一行:随机物品什么都不掷,只剩通用池的那一次
        var noRow = new ScriptedRandom(100);
        Assert.Empty(ObjectItemRoll.Roll(Items(-1, new int[0]), 0, 0, 1, 60, 0, table, pool, Resolve, noRow));
        Assert.Equal(new[] { "R1:10000" }, noRow.Calls);

        // 项的等级是 -1、标准等级又不在 1 到 200 里:整项不生成(连通用池也不掷)
        foreach (int standard in new[] { 0, -5, 201, 1000 })
        {
            var untouched = new ScriptedRandom();
            Assert.Empty(ObjectItemRoll.Roll(Items(-1, new[] { 101, 10001 }, new WeightedItem(201, 10001)), 0, 1, 1, standard, 0, table, pool, Resolve, untouched));
            Assert.Empty(untouched.Calls);
        }
        // 项自带了等级:标准等级不在范围也无所谓
        Assert.NotEmpty(ObjectItemRoll.Roll(Items(10, new[] { 101, 10001 }), 0, 1, 0, 0, 0, null, null, Resolve, new ScriptedRandom(100, 100)));
    }

    [Fact]
    public void AMissingEntryNullInputsAndUnknownItemIdsGiveNothing()
    {
        var untouched = new ScriptedRandom();
        DungeonObjectItems items = Items(10, new[] { 101, 10001 }, new WeightedItem(201, 10001));
        Assert.Empty(ObjectItemRoll.Roll(items, 1, 1, 0, 20, 0, null, null, Resolve, untouched));          // 没有这一项
        Assert.Empty(ObjectItemRoll.Roll(items, -1, 1, 0, 20, 0, null, null, Resolve, untouched));
        Assert.Empty(ObjectItemRoll.Roll(null, 0, 1, 0, 20, 0, null, null, Resolve, untouched));
        Assert.Empty(ObjectItemRoll.Roll(items, 0, 1, 0, 20, 0, null, null, Resolve, null));
        Assert.Empty(untouched.Calls);

        // 编号解析不出来的被跳过,别的照出
        DungeonObjectItems unknown = Items(10, new[] { 9999, 10001 }, new WeightedItem(201, 10001));
        List<ItemInfo> got = ObjectItemRoll.Roll(unknown, 0, 2, 0, 20, 0, null, null, Resolve, new ScriptedRandom(100, 100, 100));
        Assert.Equal(new[] { 201 }, got.Select(i => i.Code).ToArray());
        Assert.Empty(ObjectItemRoll.Roll(items, 0, 1, 0, 20, 0, null, null, null, new ScriptedRandom(100, 100)));      // 没有解析函数
    }

    // ------------------------------------------------------------------ 合成的物件掉落表

    private static ScriptedRandom RandomsForOneRoll() => new(0, 1, 5, 1, 1, 1, 100);

    private static ItemInfo RandomStack(int code)
    {
        return new ItemInfo(code, "stackable/" + code + ".stk", ItemKind.Stackable)
        {
            Grade = 10,
            Rarity = 0,
            CreationRate = 10,
            StackableType = StackableTypes.Material,
        };
    }

    /// <summary>与怪物掉落表同一个格式(类型 1 概率 10000、其余 0;每个等级的物品窗口向下 0、向上 1;等级行 1 到 50)。</summary>
    private static MonsterDropTable ObjectTable()
    {
        int[] basis =
        {
            100000, 300000, 600000, 900000, 990000, 1000000,
            10, 20, 30, 40, 50, 60,
            200000, 400000, 600000, 800000, 900000, 950000,
            1, 2, 3, 4, 5, 6,
        };
        var b = new TokenStreamBuilder();
        b.Tag("[drop prob count]").Int(1);
        b.Tag("[drop prob]").Ints(1, 50).Ints(0, 10000, 0, 0, 0);
        b.Tag("[basis of rarity dicision]").Ints(basis);
        b.Tag("[party member drop bonusrate]").Floats(Enumerable.Repeat(1f, 20).ToArray());
        b.Tag("[dungeon difficulty drop bonusrate]").Floats(Enumerable.Repeat(1f, 25).ToArray());
        b.Tag("[monster type drop bonusrate]").Floats(Enumerable.Repeat(1f, 20).ToArray());
        b.Tag("[item drop ref table]");
        for (int level = 1; level <= 200; level++)
        {
            b.Ints(level, 0, 1);
        }
        MonsterDropTable table = MonsterDropTable.Load(b.Cursor(), out string error);
        Assert.Null(error);
        Assert.NotNull(table);
        return table;
    }
}
