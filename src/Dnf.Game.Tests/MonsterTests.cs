using System.Globalization;
using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 怪物定义(.mob)、基础属性表(.tbl)与按等级算数值(MonsterAbility)的测试(证据 E-062)。
/// 数据全是合成的(哨兵数值),不含任何原版数据;与原版函数的逐位对拍见 <see cref="MonsterGoldenTests"/>。
/// </summary>
public sealed class MonsterTests
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

    private static MonsterDef Def(int id, params ScriptLine[] lines)
    {
        return new MonsterDef(id, "monster/test/dir/foo.mob", lines);
    }

    /// <summary>每一列都填一个可辨认的哨兵 1000 + 列序(从 0 数),第 1 列(等级)取 level。</summary>
    private static MonsterBaseRow SentinelRow(int level)
    {
        var cols = new int[MonsterBaseRow.Columns];
        for (int k = 0; k < cols.Length; k++)
        {
            cols[k] = 1000 + k;
        }
        cols[0] = level;
        return new MonsterBaseRow(cols);
    }

    private static MonsterBaseRow ZeroRow(int level, int hpBase = 0)
    {
        var cols = new int[MonsterBaseRow.Columns];
        cols[0] = level;
        cols[5] = hpBase;
        return new MonsterBaseRow(cols);
    }

    private static MonsterBaseTable Table(params MonsterBaseRow[] rows)
    {
        return new MonsterBaseTable(rows);
    }

    private static MonsterBaseTables Tables(MonsterBaseTable common = null, MonsterBaseTable named = null, MonsterBaseTable boss = null,
        MonsterBaseTable summon = null, MonsterBaseTable warRoom = null)
    {
        MonsterBaseTable empty = Table();
        return new MonsterBaseTables(new[] { common ?? empty, named ?? empty, boss ?? empty, summon ?? empty, warRoom ?? empty });
    }

    // ---------------- MonsterDef ----------------

    [Fact]
    public void DefaultsMatchTheOriginalClearFunction()
    {
        MonsterDef def = Def(1);
        Assert.Equal(new IntPair(0, 0), def.Level);
        Assert.Equal(new IntPair(100, 100), def.JumpSpeed);
        Assert.Equal(new IntPair(40, 20), def.Width);
        Assert.Equal(200, def.Sight);
        Assert.Equal(50, def.Warlike);
        Assert.Equal(1000, def.AttackDelay);
        Assert.Equal(1, def.LifeMax);
        Assert.Equal(0, def.AbilityTable);
        Assert.Empty(def.AbilityCategories);
        Assert.Equal("foo", def.BaseName);
    }

    [Fact]
    public void PairTagsReadTwoIntegersAndASingleIntegerMeansMinEqualsMax()
    {
        MonsterDef def = Def(
            2,
            Line("[level]", 3, 9),
            Line("[HP regen speed]", 4),
            Line("[fire resistance]", -5, 7),
            Line("[stun resistance]", 11, 12),
            Line("[light attack]", 1, 2));
        Assert.Equal(new IntPair(3, 9), def.Level);
        Assert.Equal(new IntPair(4, 4), def.HpRegenSpeed);
        Assert.Equal(new IntPair(-5, 7), def.ElementResistance[0]);
        Assert.Equal(new IntPair(0, 0), def.ElementResistance[1]);
        int stun = Array.IndexOf(MonsterDef.StatusResistanceTags, "[stun resistance]");
        Assert.Equal(3, stun);
        Assert.Equal(new IntPair(11, 12), def.StatusResistance[stun]);
        Assert.Equal(new IntPair(1, 2), def.ElementAttack[3]);
        Assert.Equal(17, def.StatusResistance.Length);
    }

    [Fact]
    public void AbilityCategoriesKeepOrderAndSkipUnknownNames()
    {
        MonsterDef def = Def(
            3,
            Line("[ability category]", "[HP MAX]", "*", 120, "[NOT A CATEGORY]", "+", 5, "[ATTACK_SPEED]", "+", 250, "[STUCK]", "*", 80));
        Assert.Equal(3, def.AbilityCategories.Length);
        Assert.Equal(MonsterAbility.CategoryHpMax, def.AbilityCategories[0].Type);
        Assert.True(def.AbilityCategories[0].Multiply);
        Assert.Equal(120, def.AbilityCategories[0].Value);
        Assert.Equal(0, def.AbilityCategories[1].Type);
        Assert.False(def.AbilityCategories[1].Multiply);
        Assert.Equal(250, def.AbilityCategories[1].Value);
        Assert.Equal(12, def.AbilityCategories[2].Type);
    }

    [Fact]
    public void AssetPathsResolveRelativeToTheMobDirectory()
    {
        MonsterDef def = Def(
            4,
            Line("[waiting action]", "action/wait.act"),
            Line("[attack action]", "a1.act", "../shared/a2.act"),
            Line("[/attack action]"),
            Line("[attack info]", "atk/one.atk", ""),
            Line("[/attack info]"),
            Line("[ai pattern]"),
            Line("[easy]", "ai/e1.ai", "ai/e2.ai"),
            Line("[hero]", "ai/h.ai"),
            Line("[/ai pattern]"));
        Assert.Equal("monster/test/dir/action/wait.act", def.WaitingActionPath);
        Assert.Equal("monster/test/dir/a1.act", def.AttackActionPath(0));
        Assert.Equal("monster/test/shared/a2.act", def.AttackActionPath(1));
        Assert.Null(def.AttackActionPath(2));
        Assert.Equal("monster/test/dir/atk/one.atk", def.AttackInfoPath(0));
        Assert.Null(def.AttackInfoPath(1)); // 空串表示该位没有攻击信息
        Assert.Equal(new[] { "monster/test/dir/ai/e1.ai", "monster/test/dir/ai/e2.ai" }, def.AiPaths("easy"));
        Assert.Equal(new[] { "monster/test/dir/ai/h.ai" }, def.AiPaths("hero"));
        Assert.Empty(def.AiPaths("hard"));
        Assert.Null(def.MoveActionPath);
    }

    [Fact]
    public void StringsOutsideBlocksAreNotCountedAsBlockItems()
    {
        MonsterDef def = Def(5, Line("[attack action]", "a1.act"), Line("[/attack action]"), Line("[waiting action]", "w.act"));
        Assert.Equal(new[] { "a1.act" }, def.AttackActions);
    }

    // ---------------- 基础属性表 ----------------

    [Fact]
    public void BaseTableParsingCutsEveryTwentyFourIntegersAndDropsATail()
    {
        var values = new List<ScriptValue>();
        for (int i = 0; i < 24 * 2 + 5; i++)
        {
            values.Add(new ScriptValue(ScriptValueKind.Int, i, null));
        }
        MonsterBaseTable table = MonsterBaseTable.Parse(new[] { new ScriptLine(null, values.ToArray()) });
        Assert.Equal(2, table.Count);
        Assert.Equal(0, table.Rows[0].Level);
        Assert.Equal(24, table.Rows[1].Level);
        Assert.Equal(1, table.IndexOfLevel(24));
        Assert.Equal(-1, table.IndexOfLevel(7));
    }

    [Fact]
    public void FirstRowWithAMatchingLevelWins()
    {
        MonsterBaseTable table = Table(ZeroRow(5, 111), ZeroRow(6, 222), ZeroRow(5, 333));
        Assert.Equal(0, table.IndexOfLevel(5));
        Assert.Equal(1, table.IndexOfLevel(6));
    }

    // ---------------- MonsterAbility ----------------

    [Fact]
    public void LerpFollowsTheOriginalIntegerArithmetic()
    {
        Assert.Equal(20, MonsterAbility.Lerp(10, 20, 9, 5)); // cur 不小于 total → 最大值
        Assert.Equal(20, MonsterAbility.Lerp(10, 20, 5, 5));
        Assert.Equal(10, MonsterAbility.Lerp(10, 20, 0, 5)); // cur 不大于 0 → 最小值
        Assert.Equal(10, MonsterAbility.Lerp(10, 20, -3, 5));
        Assert.Equal(14, MonsterAbility.Lerp(10, 20, 2, 5)); // 10 + 10 × 2 / 5
        Assert.Equal(7, MonsterAbility.Lerp(10, 0, 1, 3)); // (0 - 10) × 1 / 3 向零取整得 -3,再加 10
        Assert.Equal(15, MonsterAbility.Lerp(15, 15, 0, 0)); // total 为 0 时先判 cur >= total
        Assert.Equal(70503270, MonsterAbility.Lerp(0, 1_000_000_000, 5, 10)); // 乘法在 32 位里回绕,与原版一致
    }

    [Fact]
    public void NamedMonsterIdRangesAreInclusive()
    {
        foreach (int id in new[] { 50000, 50500, 51000, 56000, 58000, 60000 })
        {
            Assert.True(MonsterAbility.IsNamedMonster(id), id.ToString(CultureInfo.InvariantCulture));
        }
        foreach (int id in new[] { 0, 49999, 51001, 55999, 60001 })
        {
            Assert.False(MonsterAbility.IsNamedMonster(id), id.ToString(CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void TableSelectionLaterRulesOverrideEarlierOnes()
    {
        MonsterDef plain = Def(1001);
        MonsterDef summon = Def(1001, Line("[ability table]", 1));
        MonsterDef named = Def(50000);
        Assert.Equal(MonsterBaseTables.Common, MonsterAbility.SelectTable(plain, 0, MonsterKind.Normal, false, false));
        Assert.Equal(MonsterBaseTables.Summon, MonsterAbility.SelectTable(summon, 0, MonsterKind.Normal, false, false));
        Assert.Equal(MonsterBaseTables.Summon, MonsterAbility.SelectTable(plain, 0, MonsterKind.Normal, true, false));
        Assert.Equal(MonsterBaseTables.Boss, MonsterAbility.SelectTable(plain, 0, MonsterKind.Boss, false, false));
        Assert.Equal(MonsterBaseTables.Boss, MonsterAbility.SelectTable(summon, 0, MonsterKind.Boss, false, false)); // 首领盖过召唤
        Assert.Equal(MonsterBaseTables.Named, MonsterAbility.SelectTable(named, 0, MonsterKind.Normal, false, false));
        Assert.Equal(MonsterBaseTables.Named, MonsterAbility.SelectTable(named, 0, MonsterKind.Boss, false, false)); // 有名盖过首领
        Assert.Equal(MonsterBaseTables.WarRoom, MonsterAbility.SelectTable(named, 2000, MonsterKind.Boss, false, false)); // 战争房盖过一切
        Assert.Equal(MonsterBaseTables.WarRoom, MonsterAbility.SelectTable(plain, 2002, MonsterKind.Normal, false, false));
        Assert.Equal(MonsterBaseTables.Common, MonsterAbility.SelectTable(plain, 1999, MonsterKind.Normal, false, false));
        Assert.Equal(MonsterBaseTables.Common, MonsterAbility.SelectTable(plain, 2003, MonsterKind.Normal, false, false));
        Assert.Equal(MonsterBaseTables.WarRoom, MonsterAbility.SelectTable(plain, 0, MonsterKind.Normal, false, true));
    }

    [Fact]
    public void FileColumnsLandInTheFieldsTheServerFunctionWritesThemTo()
    {
        MonsterBaseTables tables = Tables(common: Table(SentinelRow(7)));
        MonsterDef def = Def(1001, Line("[level]", 7, 7));
        MonsterStats s = MonsterAbility.BasicByLevel(def, tables, null, 7, 0, MonsterKind.Normal);

        Assert.Equal(7, s.Level);
        Assert.Equal(1005, s.Raw[MonsterStats.IxHpBase]); // 第 6 列
        Assert.Equal(1006, s.Raw[MonsterStats.IxMpBase]); // 第 7 列
        Assert.Equal(1001, s.PhysicalAttack); // 第 2 列
        Assert.Equal(1002, s.PhysicalDefense);
        Assert.Equal(1003, s.MagicalAttack);
        Assert.Equal(1004, s.MagicalDefense);
        Assert.Equal(1007, s.EquipPhysicalAttack); // 第 8 至 11 列
        Assert.Equal(1008, s.EquipPhysicalDefense);
        Assert.Equal(1009, s.EquipMagicalAttack);
        Assert.Equal(1010, s.EquipMagicalDefense);
        Assert.Equal(10.11f, s.F(0x6A)); // 第 12 列除以 100 加进浮点字段
        Assert.Equal(101, s.StuckResistance); // 第 13 列除以 10 取整加进硬直抗性
        for (int e = 0; e < 4; e++)
        {
            Assert.Equal(1013 + e, s.ElementResistance(e)); // 第 14 至 17 列
            Assert.Equal(1017 + e, s.ElementAttack(e)); // 第 18 至 21 列
        }
        Assert.Equal(1021, s.HitRecovery); // 第 22 列
        Assert.Equal(1022, s.Rigidity); // 第 23 列
        for (int i = 0; i < 17; i++)
        {
            if (i != 0xD)
            {
                Assert.Equal(1023, s.StatusResistance(i)); // 第 24 列加到除硬直抗性外的 16 项
            }
        }
        Assert.Equal(1, s.Raw[MonsterStats.IxFlag]); // [life max] 缺省 1
    }

    [Fact]
    public void LevelInterpolatesPairsAndClampsOutsideTheRange()
    {
        MonsterBaseTables tables = Tables(common: Table(ZeroRow(5), ZeroRow(15), ZeroRow(25)));
        MonsterDef def = Def(1001, Line("[level]", 10, 20), Line("[weight]", 100, 200), Line("[move speed]", 300, 100));
        Assert.Equal(150, MonsterAbility.BasicByLevel(def, tables, null, 15, 0, MonsterKind.Normal).Weight);
        Assert.Equal(200, MonsterAbility.BasicByLevel(def, tables, null, 25, 0, MonsterKind.Normal).Weight);
        Assert.Equal(100, MonsterAbility.BasicByLevel(def, tables, null, 5, 0, MonsterKind.Normal).Weight);
        Assert.Equal(200, MonsterAbility.BasicByLevel(def, tables, null, 15, 0, MonsterKind.Normal).MoveSpeed); // 300 → 100 的一半
    }

    [Fact]
    public void RegenRateIsStoredAsFloatBeforeItMultipliesTheMaximum()
    {
        MonsterBaseTables tables = Tables(common: Table(ZeroRow(7, 1005)));
        MonsterDef def = Def(1001, Line("[level]", 7, 7), Line("[HP regen speed]", 300, 300));
        MonsterStats s = MonsterAbility.BasicByLevel(def, tables, null, 7, 0, MonsterKind.Normal);
        Assert.Equal(1005, s.HpMax);
        Assert.Equal(301, s.HpRegen); // 1005 × float(0.3) = 301.50000119…,截断
        Assert.Equal(0, s.MpRegen);
    }

    [Fact]
    public void MissingRowLeavesEverythingAtItsDefaultExceptTheLevel()
    {
        MonsterBaseTables tables = Tables(common: Table(ZeroRow(5, 100)));
        MonsterDef def = Def(1001, Line("[level]", 1, 9), Line("[weight]", 7, 8));
        MonsterStats s = MonsterAbility.BasicByLevel(def, tables, null, 6, 0, MonsterKind.Normal);
        Assert.Equal(6, s.Level);
        Assert.Equal(0, s.Raw[MonsterStats.IxHpBase]);
        Assert.Equal(0, s.Weight);
        Assert.Equal(1000, s.MoveSpeed);
        Assert.Equal(1.0f, s.F(MonsterStats.IxHpRate));
    }

    [Fact]
    public void WarRoomTableIsKeyedByTheDungeonParameterButTheLevelStaysTheCallersLevel()
    {
        MonsterBaseTables tables = Tables(common: Table(ZeroRow(7, 10)), warRoom: Table(ZeroRow(2001, 20), ZeroRow(2002, 30)));
        MonsterDef def = Def(1001, Line("[level]", 7, 7));
        MonsterStats s = MonsterAbility.BasicByLevel(def, tables, null, 7, 2002, MonsterKind.Normal);
        Assert.Equal(7, s.Level);
        Assert.Equal(30, s.Raw[MonsterStats.IxHpBase]);
    }

    [Fact]
    public void OwnTableReplacesTheRowAtTheSameIndexAndFallsBackWhenItIsShorter()
    {
        MonsterBaseTables tables = Tables(common: Table(ZeroRow(1, 100), ZeroRow(2, 200), ZeroRow(3, 300)));
        MonsterBaseTable own = Table(ZeroRow(1, 555), ZeroRow(2, 666));
        MonsterDef def = Def(1001, Line("[level]", 1, 3));
        Assert.Equal(555, MonsterAbility.BasicByLevel(def, tables, own, 1, 0, MonsterKind.Normal).Raw[MonsterStats.IxHpBase]);
        Assert.Equal(666, MonsterAbility.BasicByLevel(def, tables, own, 2, 0, MonsterKind.Normal).Raw[MonsterStats.IxHpBase]);
        // 行号 2 超出自带表:原版会读到数组之外(未定义行为),这里沿用公共表的行
        Assert.Equal(300, MonsterAbility.BasicByLevel(def, tables, own, 3, 0, MonsterKind.Normal).Raw[MonsterStats.IxHpBase]);
    }

    [Fact]
    public void LifeMaxWritesTheLowByteOfTheFlagSlot()
    {
        MonsterBaseTables tables = Tables(common: Table(ZeroRow(1)));
        Assert.Equal(5, MonsterAbility.BasicByLevel(Def(1, Line("[level]", 1, 1), Line("[life max]", 5)), tables, null, 1, 0, MonsterKind.Normal).Raw[MonsterStats.IxFlag]);
        Assert.Equal(0x2C, MonsterAbility.BasicByLevel(Def(1, Line("[level]", 1, 1), Line("[life max]", 300)), tables, null, 1, 0, MonsterKind.Normal).Raw[MonsterStats.IxFlag]);
    }

    private static MonsterStats StatsWithHp(int hp)
    {
        var s = new MonsterStats();
        s.Raw[MonsterStats.IxHpBase] = hp;
        return s;
    }

    [Fact]
    public void CategoryMultiplyAndAddFollowTheOriginalRounding()
    {
        MonsterStats a = StatsWithHp(1005);
        MonsterAbility.ApplyCategories(a, new[] { new AbilityCategoryEntry(MonsterAbility.CategoryHpMax, true, 150) }, false);
        Assert.Equal(1507, a.Raw[MonsterStats.IxHpBase]); // 1507.5 截断

        MonsterStats b = StatsWithHp(100);
        MonsterAbility.ApplyCategories(b, new[] { new AbilityCategoryEntry(MonsterAbility.CategoryHpMax, true, 53) }, false);
        Assert.Equal(52, b.Raw[MonsterStats.IxHpBase]); // 原版把 100 的 53% 算成 52(double 朴素算法会得到 53)

        MonsterStats c = StatsWithHp(100);
        MonsterAbility.ApplyCategories(
            c,
            new[]
            {
                new AbilityCategoryEntry(MonsterAbility.CategoryHpMax, false, 250), // + 250 / 100 = +2
                new AbilityCategoryEntry(MonsterAbility.CategoryHpMax, false, -250), // -250 / 100 = -2(向零取整)
                new AbilityCategoryEntry(MonsterAbility.CategoryHpMax, false, 99), // 99 / 100 = 0
            },
            false);
        Assert.Equal(100, c.Raw[MonsterStats.IxHpBase]);
    }

    [Fact]
    public void BossHpCategoryAppliesOnlyToBossesAndTheNormalOneOnlyToOthersWhenBothArePresent()
    {
        AbilityCategoryEntry[] both =
        {
            new AbilityCategoryEntry(MonsterAbility.CategoryHpMax, true, 200),
            new AbilityCategoryEntry(MonsterAbility.CategoryHpMaxBoss, true, 300),
        };
        MonsterStats boss = StatsWithHp(100);
        MonsterAbility.ApplyCategories(boss, both, true);
        Assert.Equal(300, boss.Raw[MonsterStats.IxHpBase]);
        MonsterStats normal = StatsWithHp(100);
        MonsterAbility.ApplyCategories(normal, both, false);
        Assert.Equal(200, normal.Raw[MonsterStats.IxHpBase]);

        // 只有普通的一项时,首领也用它
        AbilityCategoryEntry[] onlyNormal = { new AbilityCategoryEntry(MonsterAbility.CategoryHpMax, true, 200) };
        MonsterStats boss2 = StatsWithHp(100);
        MonsterAbility.ApplyCategories(boss2, onlyNormal, true);
        Assert.Equal(200, boss2.Raw[MonsterStats.IxHpBase]);

        // 只有首领的一项时,普通怪不改生命
        AbilityCategoryEntry[] onlyBoss = { new AbilityCategoryEntry(MonsterAbility.CategoryHpMaxBoss, true, 300) };
        MonsterStats normal2 = StatsWithHp(100);
        MonsterAbility.ApplyCategories(normal2, onlyBoss, false);
        Assert.Equal(100, normal2.Raw[MonsterStats.IxHpBase]);
    }

    [Fact]
    public void CategoriesTouchTheFieldsTheServerJumpTableNames()
    {
        var s = new MonsterStats();
        for (int i = 0; i < MonsterAbility.CategoryField.Length; i++)
        {
            Assert.InRange(MonsterAbility.CategoryField[i], 1, MonsterStats.Size - 1);
        }
        // 类别 12([STUCK])改的是硬直抗性槽,类别 11([HIT_RECOVERY])改受击恢复
        s.Raw[MonsterStats.IxStuckResistance] = 40;
        s.Raw[MonsterStats.IxHitRecovery] = 1000;
        MonsterAbility.ApplyCategories(
            s,
            new[]
            {
                new AbilityCategoryEntry(12, true, 50),
                new AbilityCategoryEntry(11, false, 500),
                new AbilityCategoryEntry(99, true, 50), // 越界的类别编号被忽略
            },
            false);
        Assert.Equal(20, s.StuckResistance);
        Assert.Equal(1005, s.HitRecovery);
    }

    [Fact]
    public void CategoryNamesRoundTripThroughTheirIndices()
    {
        for (int i = 0; i < MonsterAbility.CategoryNames.Length; i++)
        {
            Assert.Equal(i, MonsterAbility.CategoryIndex(MonsterAbility.CategoryNames[i]));
        }
        Assert.Equal(-1, MonsterAbility.CategoryIndex("[hp max]")); // 区分大小写,与服务端字符串比较一致
        Assert.Equal(-1, MonsterAbility.CategoryIndex(null));
    }

    [Fact]
    public void ClearDefaultsMatchTheServerFunction()
    {
        var s = new MonsterStats();
        Assert.Equal(1.0f, s.F(MonsterStats.IxHpRate));
        Assert.Equal(1.0f, s.F(MonsterStats.IxMpRate));
        Assert.Equal(1000, s.MoveSpeed);
        Assert.Equal(1000, s.AttackSpeed);
        Assert.Equal(1000, s.CastSpeed);
        Assert.Equal(100, s.JumpSpeed);
        Assert.Equal(0, s.Weight);
        for (int e = 0; e < 4; e++)
        {
            Assert.Equal(1.0f, s.F(0x12 + e));
            Assert.Equal(1.0f, s.F(0x1A + e));
        }
        Assert.Equal(MonsterStats.Size, s.Raw.Length);
    }

    [Fact]
    public void HpMaxMultipliesTheBaseByTheFloatRateWithoutRoundingTheProductEarly()
    {
        var s = new MonsterStats();
        s.Raw[MonsterStats.IxHpBase] = 1000;
        s.SetF(MonsterStats.IxHpRate, 1.5f);
        Assert.Equal(1500, s.HpMax);
        s.Raw[MonsterStats.IxHpBase] = 3;
        s.SetF(MonsterStats.IxHpRate, 0.1f);
        Assert.Equal(0, s.HpMax);
        s.Raw[MonsterStats.IxHpBase] = int.MaxValue;
        s.SetF(MonsterStats.IxHpRate, 2.0f);
        Assert.Equal(int.MinValue, s.HpMax); // 越界得到整数不确定值
    }

    // ---------------- GameData ----------------

    [Fact]
    public void GameDataLoadsMonstersTablesAndOwnTablesFromThePvfLayout()
    {
        var fx = new Fixture();
        fx.List("monster/monster.lst", (101, "test/foo.mob"), (102, "test/missing.mob"));
        fx.Tokens("monster/test/foo.mob", w => w.Tag("[level]").Ints(2, 4).Tag("[base table]").Str("tables/own.tbl"));
        fx.Tokens("monster/tables/own.tbl", w =>
        {
            for (int row = 0; row < 2; row++)
            {
                w.Int(row + 1);
                for (int k = 1; k < 24; k++)
                {
                    w.Int(k == 5 ? 900 + row : 0);
                }
            }
        });
        fx.Tokens("monster/commonmonsterbaseparameter.tbl", w =>
        {
            for (int row = 0; row < 3; row++)
            {
                w.Int(row + 1);
                for (int k = 1; k < 24; k++)
                {
                    w.Int(0);
                }
            }
        });
        GameData data = new GameData(fx.Build());

        Assert.Equal(new[] { 101, 102 }, data.MonsterIds());
        Assert.Equal("monster/test/foo.mob", data.GetMonsterPath(101));
        Assert.Null(data.GetMonsterPath(999));
        MonsterDef def = data.GetMonster(101);
        Assert.NotNull(def);
        Assert.Equal(new IntPair(2, 4), def.Level);
        Assert.Same(def, data.GetMonster(101)); // 缓存
        Assert.Null(data.GetMonster(102)); // 列表里有、文件不存在
        Assert.Null(data.GetMonster(999));

        MonsterBaseTables tables = data.MonsterBaseTables;
        Assert.Equal(3, tables[MonsterBaseTables.Common].Count);
        Assert.Equal(0, tables[MonsterBaseTables.Named].Count);

        MonsterBaseTable own = data.GetMonsterOwnTable(def);
        Assert.NotNull(own);
        Assert.Equal(2, own.Count);
        Assert.Equal(900, own.Rows[0].HpBase);
        Assert.Equal(901, own.Rows[1].HpBase);
        Assert.Null(data.GetMonsterOwnTable(null));
    }
}
