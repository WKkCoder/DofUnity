using System.Globalization;
using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 副本难度对怪物数值的加成(证据 E-125):三种取系数的办法、取整细节、异常状态抗性的跳项、表与副本自带加成的解析。
/// 数值全是合成的(系数与槽值都是随手定的),与原版数据无关;与原版机器码的逐位对拍见 <see cref="GoldenMatchesTheOriginalFunction"/>
/// (金标准文件 Golden/mob_difficulty_golden.txt 由 tools/py/mob_difficulty_oracle.py 生成,期望值是服务端函数在模拟器里的运行结果)。
/// </summary>
public sealed class MonsterDifficultyTests
{
    private const int G = MonsterDifficultyTable.Groups;
    private const int D = MonsterDifficultyTable.Difficulties;

    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptValue F(float value) => new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(value), null);

    /// <summary>乘的组全 1、加的组全 0 的全局块:Apply 什么也不该改。</summary>
    private static float[] NeutralGlobal()
    {
        var g = new float[G * D];
        for (int k = 0; k < G; k++)
        {
            bool multiplicative = k == MonsterDifficultyTable.GroupHp || k == MonsterDifficultyTable.GroupAttackSpeed
                || k == MonsterDifficultyTable.GroupEquipAttack || k == MonsterDifficultyTable.GroupEquipDefense;
            for (int d = 0; d < D; d++)
            {
                g[k * D + d] = multiplicative ? 1f : 0f;
            }
        }
        return g;
    }

    private static float[] NeutralRow()
    {
        var row = new float[G];
        for (int k = 0; k < G; k++)
        {
            row[k] = 1f;
        }
        return row;
    }

    private static List<float[]>[] NoRows()
    {
        var rows = new List<float[]>[D];
        for (int d = 0; d < D; d++)
        {
            rows[d] = new List<float[]>();
        }
        return rows;
    }

    private static MonsterStats Stats()
    {
        var s = new MonsterStats();
        Array.Clear(s.Raw, 0, MonsterStats.Size);
        return s;
    }

    private static int[] Snapshot(MonsterStats s) => (int[])s.Raw.Clone();

    // ---------------- 全局系数 ----------------

    [Fact]
    public void NeutralTableChangesNothing()
    {
        var table = new MonsterDifficultyTable(NeutralGlobal(), NoRows());
        MonsterStats s = Stats();
        var rng = new Random(5);
        for (int i = 0; i < MonsterStats.Size; i++)
        {
            s.Raw[i] = rng.Next(-100000, 100000);
        }
        int[] before = Snapshot(s);
        for (int d = 0; d < D; d++)
        {
            table.Apply(s, d, 7);
        }
        Assert.Equal(before, s.Raw);
    }

    [Fact]
    public void HpBaseIsTheTruncatedProductOfTheGlobalCoefficient()
    {
        float[] g = NeutralGlobal();
        g[MonsterDifficultyTable.GroupHp * D + 2] = 1.5f;
        var table = new MonsterDifficultyTable(g, NoRows());
        MonsterStats s = Stats();
        s.Raw[MonsterStats.IxHpBase] = 1001;
        table.Apply(s, 2, 1);
        Assert.Equal(1501, s.Raw[MonsterStats.IxHpBase]);             // 1501.5 向零取整
        s = Stats();
        s.Raw[MonsterStats.IxHpBase] = -1001;
        table.Apply(s, 2, 1);
        Assert.Equal(-1501, s.Raw[MonsterStats.IxHpBase]);            // 向零,不是向下
        s = Stats();
        s.Raw[MonsterStats.IxHpBase] = 1001;
        table.Apply(s, 1, 1);
        Assert.Equal(1001, s.Raw[MonsterStats.IxHpBase]);             // 别的难度的系数不受影响
    }

    [Fact]
    public void AdditiveGroupsAddTheTruncatedCoefficientToTheirSlots()
    {
        float[] g = NeutralGlobal();
        g[MonsterDifficultyTable.GroupStuck * D + 3] = 40.9f;
        g[MonsterDifficultyTable.GroupHitRecovery * D + 3] = -7.9f;
        var table = new MonsterDifficultyTable(g, NoRows());
        MonsterStats s = Stats();
        s.Raw[MonsterStats.IxStuckResistance] = 100;
        s.Raw[MonsterStats.IxHitRecovery] = 100;
        table.Apply(s, 3, 1);
        Assert.Equal(140, s.Raw[MonsterStats.IxStuckResistance]);
        Assert.Equal(93, s.Raw[MonsterStats.IxHitRecovery]);          // -7.9 截断成 -7
    }

    [Fact]
    public void StatusResistanceSkipsItemsTwelveAndThirteen()
    {
        float[] g = NeutralGlobal();
        g[MonsterDifficultyTable.GroupStatus * D + 1] = 7f;
        var table = new MonsterDifficultyTable(g, NoRows());
        MonsterStats s = Stats();
        for (int i = 0; i < MonsterDifficultyTable.StatusCount; i++)
        {
            s.Raw[MonsterStats.IxStatusResistance + i] = 100;
        }
        table.Apply(s, 1, 1);
        for (int i = 0; i < MonsterDifficultyTable.StatusCount; i++)
        {
            int want = i == 12 || i == 13 ? 100 : 107;
            Assert.True(want == s.Raw[MonsterStats.IxStatusResistance + i], "第 " + i + " 项");
        }
        Assert.Equal(MonsterStats.IxStuckResistance, MonsterStats.IxStatusResistance + 13);   // 第 13 项就是硬直抗性本身
    }

    [Fact]
    public void StatusSlotMappingMatchesTheOriginalSwitch()
    {
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(i, MonsterDifficultyTable.StatusSlot(i));
        }
        Assert.Equal(-1, MonsterDifficultyTable.StatusSlot(12));
        Assert.Equal(-1, MonsterDifficultyTable.StatusSlot(13));
        Assert.Equal(15, MonsterDifficultyTable.StatusSlot(14));
        Assert.Equal(16, MonsterDifficultyTable.StatusSlot(15));
        Assert.Equal(17, MonsterDifficultyTable.StatusSlot(16));
        Assert.Equal(-1, MonsterDifficultyTable.StatusSlot(17));
        Assert.Equal(-1, MonsterDifficultyTable.StatusSlot(-1));
    }

    [Fact]
    public void MoveSpeedOnlyChangesWhenItIsNotZero()
    {
        float[] g = NeutralGlobal();
        g[MonsterDifficultyTable.GroupMoveSpeed * D + 4] = 50f;
        var table = new MonsterDifficultyTable(g, NoRows());
        MonsterStats s = Stats();
        s.Raw[MonsterStats.IxMoveSpeed] = 0;
        table.Apply(s, 4, 1);
        Assert.Equal(0, s.Raw[MonsterStats.IxMoveSpeed]);
        s.Raw[MonsterStats.IxMoveSpeed] = 1000;
        table.Apply(s, 4, 1);
        Assert.Equal(1050, s.Raw[MonsterStats.IxMoveSpeed]);
    }

    [Fact]
    public void EquipmentSlotsUseTheirOwnGroupsAndAttackSpeedIsMultiplicative()
    {
        float[] g = NeutralGlobal();
        g[MonsterDifficultyTable.GroupEquipAttack * D + 2] = 2f;
        g[MonsterDifficultyTable.GroupEquipDefense * D + 2] = 3f;
        g[MonsterDifficultyTable.GroupAttackSpeed * D + 2] = 0.5f;
        var table = new MonsterDifficultyTable(g, NoRows());
        MonsterStats s = Stats();
        s.Raw[MonsterStats.IxEquipPhysicalAttack] = 10;
        s.Raw[MonsterStats.IxEquipMagicalAttack] = 11;
        s.Raw[MonsterStats.IxEquipPhysicalDefense] = 12;
        s.Raw[MonsterStats.IxEquipMagicalDefense] = 13;
        s.Raw[MonsterStats.IxAttackSpeed] = 1000;
        table.Apply(s, 2, 1);
        Assert.Equal(20, s.Raw[MonsterStats.IxEquipPhysicalAttack]);
        Assert.Equal(22, s.Raw[MonsterStats.IxEquipMagicalAttack]);
        Assert.Equal(36, s.Raw[MonsterStats.IxEquipPhysicalDefense]);
        Assert.Equal(39, s.Raw[MonsterStats.IxEquipMagicalDefense]);
        Assert.Equal(500, s.Raw[MonsterStats.IxAttackSpeed]);
    }

    [Fact]
    public void AdditionWrapsLikeA32BitInteger()
    {
        float[] g = NeutralGlobal();
        g[MonsterDifficultyTable.GroupStuck * D + 1] = 10f;
        var table = new MonsterDifficultyTable(g, NoRows());
        MonsterStats s = Stats();
        s.Raw[MonsterStats.IxStuckResistance] = int.MaxValue - 3;
        table.Apply(s, 1, 1);
        Assert.Equal(unchecked(int.MaxValue - 3 + 10), s.Raw[MonsterStats.IxStuckResistance]);
    }

    [Fact]
    public void ProductBeyondInt32GivesTheIntegerIndefiniteValue()
    {
        float[] g = NeutralGlobal();
        g[MonsterDifficultyTable.GroupHp * D + 1] = 1.0e10f;
        var table = new MonsterDifficultyTable(g, NoRows());
        MonsterStats s = Stats();
        s.Raw[MonsterStats.IxHpBase] = 1000;
        table.Apply(s, 1, 1);
        Assert.Equal(int.MinValue, s.Raw[MonsterStats.IxHpBase]);
    }

    // ---------------- 按等级的行 ----------------

    [Fact]
    public void LevelRowScalesTheGlobalCoefficientThroughAFloatTemporary()
    {
        // 乘:截断(行系数 × float(旧值 × 全局))——中间结果先存进 float,所以 2^24 + 1 会先丢掉最低位
        float[] g = NeutralGlobal();
        float[] row = NeutralRow();
        List<float[]>[] rows = NoRows();
        rows[0].Add(row);
        var table = new MonsterDifficultyTable(g, rows);
        MonsterStats s = Stats();
        s.Raw[MonsterStats.IxHpBase] = 16777217;
        table.Apply(s, 0, 1);
        Assert.Equal(16777216, s.Raw[MonsterStats.IxHpBase]);
        // 没有行(等级超出)时是全局算法:截断(旧值 × 全局),没有 float 中间量
        s = Stats();
        s.Raw[MonsterStats.IxHpBase] = 16777217;
        table.Apply(s, 0, 2);
        Assert.Equal(16777217, s.Raw[MonsterStats.IxHpBase]);
    }

    [Fact]
    public void LevelRowIsChosenByPositionNotByTheLevelNumberWrittenInTheFile()
    {
        float[] g = NeutralGlobal();
        List<float[]>[] rows = NoRows();
        for (int n = 1; n <= 3; n++)
        {
            float[] row = NeutralRow();
            row[MonsterDifficultyTable.GroupHp] = n * 10f;
            rows[1].Add(row);
        }
        var table = new MonsterDifficultyTable(g, rows);
        foreach ((int level, int want) in new[] { (1, 100), (2, 200), (3, 300) })
        {
            MonsterStats s = Stats();
            s.Raw[MonsterStats.IxHpBase] = 10;
            table.Apply(s, 1, level);
            Assert.Equal(want, s.Raw[MonsterStats.IxHpBase]);
        }
        Assert.Equal(3, table.RowCount(1));
        Assert.Equal(0, table.RowCount(0));
        Assert.Null(table.Row(1, 0));
        Assert.Null(table.Row(1, 4));
        Assert.Same(table.Row(1, 2), table.Row(1, 2));
    }

    [Fact]
    public void LevelBelowOneOrBeyondTheRowsUsesTheGlobalCoefficientsOnly()
    {
        float[] g = NeutralGlobal();
        g[MonsterDifficultyTable.GroupHp * D] = 2f;
        List<float[]>[] rows = NoRows();
        float[] row = NeutralRow();
        row[MonsterDifficultyTable.GroupHp] = 100f;
        rows[0].Add(row);
        var table = new MonsterDifficultyTable(g, rows);
        foreach (int level in new[] { 0, -3, 2, 99 })
        {
            MonsterStats s = Stats();
            s.Raw[MonsterStats.IxHpBase] = 50;
            table.Apply(s, 0, level);
            Assert.True(100 == s.Raw[MonsterStats.IxHpBase], "等级 " + level);        // 全局 2 倍,没有行系数
        }
        MonsterStats hit = Stats();
        hit.Raw[MonsterStats.IxHpBase] = 50;
        table.Apply(hit, 0, 1);
        Assert.Equal(10000, hit.Raw[MonsterStats.IxHpBase]);                        // 行系数 100 × 全局 2 × 50
    }

    [Fact]
    public void AdditiveGroupsWithARowMultiplyTheRowCoefficientByTheGlobalOne()
    {
        float[] g = NeutralGlobal();
        g[MonsterDifficultyTable.GroupStuck * D + 2] = 30f;
        List<float[]>[] rows = NoRows();
        float[] row = NeutralRow();
        row[MonsterDifficultyTable.GroupStuck] = 1.5f;
        rows[2].Add(row);
        var table = new MonsterDifficultyTable(g, rows);
        MonsterStats s = Stats();
        s.Raw[MonsterStats.IxStuckResistance] = 1;
        table.Apply(s, 2, 1);
        Assert.Equal(46, s.Raw[MonsterStats.IxStuckResistance]);                    // 1 + 截断(1.5 × 30)
    }

    // ---------------- 副本自带加成 ----------------

    [Fact]
    public void DungeonBonusReplacesTheLevelRowAndMultipliesTheGlobalCoefficient()
    {
        float[] g = NeutralGlobal();
        g[MonsterDifficultyTable.GroupHp * D + 3] = 2f;
        List<float[]>[] rows = NoRows();
        float[] row = NeutralRow();
        row[MonsterDifficultyTable.GroupHp] = 100f;
        rows[3].Add(row);
        var table = new MonsterDifficultyTable(g, rows);

        float[] own = new float[G * D];
        for (int i = 0; i < own.Length; i++)
        {
            own[i] = 1f;
        }
        own[MonsterDifficultyTable.GroupHp * D + 3] = 3f;
        var bonus = new DungeonDifficultyBonus(own);

        MonsterStats s = Stats();
        s.Raw[MonsterStats.IxHpBase] = 10;
        table.Apply(s, 3, 1, bonus);
        Assert.Equal(60, s.Raw[MonsterStats.IxHpBase]);                            // 自带 3 × 全局 2 × 10;等级行的 100 不参与
        // 别的难度:自带系数是 1,全局是 1(中性),结果不变
        s = Stats();
        s.Raw[MonsterStats.IxHpBase] = 10;
        table.Apply(s, 2, 1, bonus);
        Assert.Equal(10, s.Raw[MonsterStats.IxHpBase]);
    }

    [Fact]
    public void DungeonBonusAdditiveGroupsMultiplyToo()
    {
        float[] g = NeutralGlobal();
        g[MonsterDifficultyTable.GroupHitRecovery * D + 1] = 200f;
        var table = new MonsterDifficultyTable(g, NoRows());
        float[] own = new float[G * D];
        own[MonsterDifficultyTable.GroupHitRecovery * D + 1] = 0.5f;
        MonsterStats s = Stats();
        s.Raw[MonsterStats.IxHitRecovery] = 7;
        table.Apply(s, 1, 1, new DungeonDifficultyBonus(own));
        Assert.Equal(107, s.Raw[MonsterStats.IxHitRecovery]);                      // 7 + 截断(0.5 × 200)
    }

    // ---------------- 没有表、难度越界 ----------------

    [Fact]
    public void UnloadedTableAndOutOfRangeDifficultyDoNothing()
    {
        float[] g = NeutralGlobal();
        g[MonsterDifficultyTable.GroupHp * D] = 9f;
        var loaded = new MonsterDifficultyTable(g, NoRows());
        var empty = MonsterDifficultyTable.Empty;
        Assert.False(empty.IsLoaded);
        Assert.True(loaded.IsLoaded);
        foreach (MonsterDifficultyTable table in new[] { empty, loaded })
        {
            foreach (int difficulty in new[] { -1, D, 99 })
            {
                MonsterStats s = Stats();
                s.Raw[MonsterStats.IxHpBase] = 123;
                table.Apply(s, difficulty, 5);
                Assert.Equal(123, s.Raw[MonsterStats.IxHpBase]);
            }
        }
        MonsterStats untouched = Stats();
        untouched.Raw[MonsterStats.IxHpBase] = 123;
        empty.Apply(untouched, 0, 5);
        Assert.Equal(123, untouched.Raw[MonsterStats.IxHpBase]);
        Assert.Throws<ArgumentNullException>(() => loaded.Apply(null, 0, 1));
    }

    [Fact]
    public void ApplyOnlyTouchesTheDocumentedSlots()
    {
        float[] g = new float[G * D];
        for (int i = 0; i < g.Length; i++)
        {
            g[i] = 3f;
        }
        var table = new MonsterDifficultyTable(g, NoRows());
        MonsterStats s = Stats();
        for (int i = 0; i < MonsterStats.Size; i++)
        {
            s.Raw[i] = 1000 + i;
        }
        int[] before = Snapshot(s);
        table.Apply(s, 2, 1);
        var touched = new HashSet<int> { 1, 0x2B, 0x4C, 0x48, 0x46, 0x56, 0x58, 0x5A, 0x5C };
        for (int i = 0; i < MonsterDifficultyTable.StatusCount; i++)
        {
            touched.Add(MonsterStats.IxStatusResistance + i);
        }
        touched.Remove(MonsterStats.IxStatusResistance + 12);       // 第 12 项被跳过
        for (int i = 0; i < MonsterStats.Size; i++)
        {
            if (touched.Contains(i))
            {
                Assert.True(before[i] != s.Raw[i], "槽 0x" + i.ToString("X", CultureInfo.InvariantCulture) + " 应当变化");
            }
            else
            {
                Assert.True(before[i] == s.Raw[i], "槽 0x" + i.ToString("X", CultureInfo.InvariantCulture) + " 不该变化");
            }
        }
    }

    // ---------------- 解析 ----------------

    private static ScriptValue[] Numbers(params float[] values)
    {
        var list = new List<ScriptValue>();
        foreach (float v in values)
        {
            list.Add(v == (int)v ? I((int)v) : F(v));      // 整数写成整数标记,小数写成浮点标记:两种都要认
        }
        return list.ToArray();
    }

    private static float[] Sequence(int count, float start, float step)
    {
        var r = new float[count];
        for (int i = 0; i < count; i++)
        {
            r[i] = start + step * i;
        }
        return r;
    }

    [Fact]
    public void ParseReadsTheHeadBlockAndTheFiveSections()
    {
        float[] head = Sequence(G * D + 104, 0.5f, 0.25f);          // 后面 104 个数是别的块(死亡之塔、血系统),只读前 65 个
        var rowsEasy = new List<float>();
        for (int level = 1; level <= 2; level++)
        {
            rowsEasy.Add(level);
            rowsEasy.AddRange(Sequence(G, 10 * level, 0.5f));
        }
        rowsEasy.Add(3);                                           // 末尾不满一行的数丢掉
        rowsEasy.AddRange(Sequence(5, 99, 1));
        var lines = new[]
        {
            new ScriptLine(null, Numbers(head)),
            new ScriptLine("[dungeon party balance]", Numbers(1, 2, 3)),
            new ScriptLine("[easy]", Numbers(rowsEasy.ToArray())),
            new ScriptLine("[/easy]", Array.Empty<ScriptValue>()),
            new ScriptLine("[HERO]", Numbers(1, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7)),     // 标签不分大小写
            new ScriptLine("[named]", Numbers(1, 5)),                                         // 无关的节被忽略
        };
        MonsterDifficultyTable table = MonsterDifficultyTable.Parse(lines);
        Assert.True(table.IsLoaded);
        Assert.Equal(0.5f, table.Global(0, 0));
        Assert.Equal(0.5f + 0.25f * 7, table.Global(1, 2));
        Assert.Equal(0.5f + 0.25f * (G * D - 1), table.Global(G - 1, D - 1));
        Assert.Equal(2, table.RowCount(0));
        Assert.Equal(0, table.RowCount(1));
        Assert.Equal(1, table.RowCount(4));
        Assert.Equal(10f, table.Row(0, 1)[0]);                      // 行首的等级号被丢掉
        Assert.Equal(20f + 0.5f * 12, table.Row(0, 2)[12]);
        Assert.Equal(7f, table.Row(4, 1)[5]);
    }

    [Fact]
    public void ParseWithAShortHeadOrNothingGivesAnEmptyTable()
    {
        Assert.False(MonsterDifficultyTable.Parse(null).IsLoaded);
        Assert.False(MonsterDifficultyTable.Parse(Array.Empty<ScriptLine>()).IsLoaded);
        var shortHead = new[] { new ScriptLine(null, Numbers(Sequence(G * D - 1, 1, 1))), new ScriptLine("[easy]", Numbers(Sequence(14, 1, 1))) };
        Assert.False(MonsterDifficultyTable.Parse(shortHead).IsLoaded);
    }

    [Fact]
    public void DungeonBonusParseNeedsAllSixtyFiveNumbers()
    {
        var full = new[]
        {
            new ScriptLine("[basis level]", Numbers(5)),
            new ScriptLine("[monster difficulty bonus]", Numbers(Sequence(G * D, 1, 0.5f))),
            new ScriptLine("[/monster difficulty bonus]", Array.Empty<ScriptValue>()),
        };
        DungeonDifficultyBonus bonus = DungeonDifficultyBonus.Parse(full);
        Assert.NotNull(bonus);
        Assert.Equal(1f, bonus.Value(0, 0));
        Assert.Equal(1f + 0.5f * (2 * D + 3), bonus.Value(2, 3));
        Assert.Null(DungeonDifficultyBonus.Parse(new[] { new ScriptLine("[monster difficulty bonus]", Numbers(Sequence(G * D - 1, 1, 1))) }));
        Assert.Null(DungeonDifficultyBonus.Parse(new[] { new ScriptLine("[basis level]", Numbers(5)) }));
        Assert.Null(DungeonDifficultyBonus.Parse(null));
        Assert.Throws<ArgumentException>(() => new DungeonDifficultyBonus(new float[3]));
    }

    // ---------------- 接进怪物数值 ----------------

    [Fact]
    public void ComputeWithTheDifficultyLayerEqualsComputeThenApply()
    {
        var def = new MonsterDef(1, "monster/test/dir/foo.mob", new[]
        {
            new ScriptLine("[level]", new[] { I(5), I(5) }),
        });
        var cols = new int[MonsterBaseRow.Columns];
        for (int k = 0; k < cols.Length; k++)
        {
            cols[k] = 100 + k;
        }
        cols[0] = 5;
        cols[5] = 2000;                                            // 生命值基数
        var common = new MonsterBaseTable(new[] { new MonsterBaseRow(cols) });
        MonsterBaseTable empty = new MonsterBaseTable(Array.Empty<MonsterBaseRow>());
        var tables = new MonsterBaseTables(new[] { common, empty, empty, empty, empty });

        float[] g = NeutralGlobal();
        g[MonsterDifficultyTable.GroupHp * D + 2] = 2.5f;
        g[MonsterDifficultyTable.GroupStuck * D + 2] = 80f;
        var table = new MonsterDifficultyTable(g, NoRows());

        MonsterStats plain = MonsterAbility.Compute(def, tables, null, 5, 0, MonsterKind.Normal);
        MonsterStats layered = MonsterAbility.Compute(def, tables, null, 5, 0, MonsterKind.Normal, table, 2);
        MonsterStats expected = plain.Clone();
        table.Apply(expected, 2, 5);
        Assert.Equal(expected.Raw, layered.Raw);
        Assert.NotEqual(plain.Raw[MonsterStats.IxHpBase], layered.Raw[MonsterStats.IxHpBase]);
        Assert.Equal(plain.StuckResistance + 80, layered.StuckResistance);
        // 难度 0 的系数是中性的:与不带难度层的结果一样
        MonsterStats normal = MonsterAbility.Compute(def, tables, null, 5, 0, MonsterKind.Normal, table, 0);
        Assert.Equal(plain.Raw, normal.Raw);
        // 没有难度表:什么也不加
        MonsterStats none = MonsterAbility.Compute(def, tables, null, 5, 0, MonsterKind.Normal, null, 3);
        Assert.Equal(plain.Raw, none.Raw);
    }

    // ---------------- 与原版机器码对拍 ----------------

    private static int[] ParseInts(string csv)
    {
        string[] parts = csv.Split(',');
        var r = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            r[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
        }
        return r;
    }

    private static float[] ParseFloats(string csv)
    {
        int[] bits = ParseInts(csv);
        var r = new float[bits.Length];
        for (int i = 0; i < bits.Length; i++)
        {
            r[i] = BitConverter.Int32BitsToSingle(bits[i]);
        }
        return r;
    }

    /// <summary>金标准文件里动过的 25 个槽(与 tools/py/mob_difficulty_oracle.py 的 TOUCHED 同序)。</summary>
    private static readonly int[] Touched =
    {
        1, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x46, 0x48, 0x4C, 0x56, 0x58, 0x5A, 0x5C,
    };

    [Fact]
    public void GoldenMatchesTheOriginalFunction()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Golden", "mob_difficulty_golden.txt");
        Assert.True(File.Exists(path), "缺少金标准文件 " + path);

        var globals = new Dictionary<int, float[]>();
        var rowSets = new Dictionary<int, List<float[]>[]>();
        var failures = new List<string>();
        int cases = 0;
        int withOverride = 0;
        int withRow = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            string[] f = line.Split('\t');
            if (f[0] == "TABLE")
            {
                int id = int.Parse(f[1], CultureInfo.InvariantCulture);
                globals[id] = ParseFloats(f[2]);
                rowSets[id] = NoRows();
            }
            else if (f[0] == "ROWS")
            {
                int id = int.Parse(f[1], CultureInfo.InvariantCulture);
                int d = int.Parse(f[2], CultureInfo.InvariantCulture);
                int n = int.Parse(f[3], CultureInfo.InvariantCulture);
                if (n > 0)
                {
                    float[] flat = ParseFloats(f[4]);
                    for (int r = 0; r < n; r++)
                    {
                        var row = new float[G];
                        Array.Copy(flat, r * G, row, 0, G);
                        rowSets[id][d].Add(row);
                    }
                }
            }
            else if (f[0] == "CASE")
            {
                int id = int.Parse(f[1], CultureInfo.InvariantCulture);
                int d = int.Parse(f[2], CultureInfo.InvariantCulture);
                int level = int.Parse(f[3], CultureInfo.InvariantCulture);
                DungeonDifficultyBonus bonus = f[4] == "-" ? null : new DungeonDifficultyBonus(ParseFloats(f[4]));
                int[] input = ParseInts(f[5]);
                int[] expected = ParseInts(f[6]);
                var table = new MonsterDifficultyTable(globals[id], rowSets[id]);

                MonsterStats s = Stats();
                for (int i = 0; i < MonsterStats.Size; i++)
                {
                    s.Raw[i] = 0x5A5A5A5A + i;                        // 没动的槽用哨兵,确认它们原样
                }
                for (int i = 0; i < Touched.Length; i++)
                {
                    s.Raw[Touched[i]] = input[i];
                }
                table.Apply(s, d, level, bonus);
                cases++;
                if (bonus != null)
                {
                    withOverride++;
                }
                else if (level <= table.RowCount(d))
                {
                    withRow++;
                }
                var touched = new HashSet<int>(Touched);
                for (int i = 0; i < MonsterStats.Size; i++)
                {
                    int want = touched.Contains(i) ? expected[Array.IndexOf(Touched, i)] : 0x5A5A5A5A + i;
                    if (s.Raw[i] != want)
                    {
                        failures.Add("表 " + id + " 难度 " + d + " 等级 " + level + (bonus != null ? " 自带" : "") + " 槽 0x" + i.ToString("X", CultureInfo.InvariantCulture)
                            + " 得到 " + s.Raw[i] + " 期望 " + want);
                        break;
                    }
                }
            }
        }
        Assert.True(failures.Count == 0, failures.Count + " 个用例不一致,例如:" + string.Join("; ", failures.Take(5)));
        Assert.True(cases >= 400, "金标准用例太少:" + cases);
        Assert.True(withOverride > cases / 5, "带副本自带加成的用例太少");
        Assert.True(withRow > cases / 5, "命中等级行的用例太少");
    }
}
