using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Game;
using Dnf.Game.Tests;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 阶段 2a 跨职业状态族配方(0x11 多属性增益、0x0d 零蓄力/常驻霸体投掷、0x13 防御姿态)与职业作用域隔离的合成单元测试。
/// 行为测试全部使用合成技能编号、合成脚本与 <see cref="AvatarFixture"/> 合成夹具,不含任何真实技能名、职业名、素材路径或 PVF 数值。
/// </summary>
public sealed class CrossJobSkillTests
{
    private const int HeroX = 300;
    private const int HeroY = 100;

    // ------------------------------------------------------------------ 合成数据助手

    private static SkillDef SyntheticSkillDef(int id, int[] statics, int[] pvpStatics, int[][] levels, int[][] pvpLevels, params string[] commandParens)
    {
        var lines = new List<ScriptLine>
        {
            new ScriptLine("[type]", new[] { new ScriptValue(ScriptValueKind.String, 0, "[active]") }),
            new ScriptLine("[maximum level]", new[] { new ScriptValue(ScriptValueKind.Int, Math.Max(1, levels == null ? 1 : levels.Length), null) }),
            new ScriptLine("[cool time]", new[] { new ScriptValue(ScriptValueKind.Int, 2400, null) }),
            new ScriptLine("[consume MP]", new[] { new ScriptValue(ScriptValueKind.Int, 18, null) }),
        };
        if (statics != null && statics.Length > 0)
        {
            lines.Add(new ScriptLine("[static data]", statics.Select(v => new ScriptValue(ScriptValueKind.Int, v, null)).ToArray()));
        }
        if (levels != null && levels.Length > 0)
        {
            var flat = new List<ScriptValue> { new ScriptValue(ScriptValueKind.Int, levels[0].Length, null) };
            foreach (int[] row in levels)
            {
                foreach (int v in row)
                {
                    flat.Add(new ScriptValue(ScriptValueKind.Int, v, null));
                }
            }
            lines.Add(new ScriptLine("[level info]", flat.ToArray()));
        }
        if ((pvpStatics != null && pvpStatics.Length > 0) || (pvpLevels != null && pvpLevels.Length > 0))
        {
            lines.Add(new ScriptLine("[pvp]", Array.Empty<ScriptValue>()));
            if (pvpStatics != null && pvpStatics.Length > 0)
            {
                lines.Add(new ScriptLine("[static data]", pvpStatics.Select(v => new ScriptValue(ScriptValueKind.Int, v, null)).ToArray()));
            }
            if (pvpLevels != null && pvpLevels.Length > 0)
            {
                var flat = new List<ScriptValue> { new ScriptValue(ScriptValueKind.Int, pvpLevels[0].Length, null) };
                foreach (int[] row in pvpLevels)
                {
                    foreach (int v in row)
                    {
                        flat.Add(new ScriptValue(ScriptValueKind.Int, v, null));
                    }
                }
                lines.Add(new ScriptLine("[level info]", flat.ToArray()));
            }
            lines.Add(new ScriptLine("[/pvp]", Array.Empty<ScriptValue>()));
        }
        if (commandParens != null && commandParens.Length > 0)
        {
            lines.Add(new ScriptLine("[command]", commandParens.Select(t => new ScriptValue(ScriptValueKind.Paren, 0, t)).ToArray()));
        }
        return new SkillDef(id, "skill/test/s" + id + ".skl", lines.ToArray());
    }

    private static CharacterDef WithThrowPoses(CharacterDef chr, int selector, string chargeAni, string shootAni)
    {
        var lines = new List<ScriptLine>(chr.Tags.Lines)
        {
            new ScriptLine("[throw motion " + selector + "-1]", new[] { new ScriptValue(ScriptValueKind.String, 0, "/" + chargeAni) }),
            new ScriptLine("[throw motion " + selector + "-2]", new[] { new ScriptValue(ScriptValueKind.String, 0, "/" + shootAni) }),
        };
        return new CharacterDef(chr.Job, chr.Path, lines.ToArray());
    }

    private static TownHero NewHero(DnfAssets assets, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), HeroX, HeroY, facingLeft, TownHero.DungeonRestTag);
    }

    private static SkillObjectSpec SynthObj(int objectId = AvatarFixture.WaveObjectId, int stage = 0, int rateCol = 0)
    {
        return new SkillObjectSpec(objectId, stage, 0, damageRateColumn: rateCol, damageFixedColumn: rateCol);
    }

    private static HeroSkills LoadJobSkills(GameData data, int job, int skillId, int level, SkillRecipes recipes, bool quickSlot = true,
        bool addThrowPoses = false, string chargeAni = AvatarFixture.ChainAAni, string shootAni = AvatarFixture.ChainBAni, int poseSelector = 1)
    {
        CharacterDef baseChr = data.GetCharacter(0);
        CharacterDef chr = new CharacterDef(job, baseChr.Path, baseChr.Tags.Lines);
        if (addThrowPoses)
        {
            chr = WithThrowPoses(chr, poseSelector, chargeAni, shootAni);
        }
        SkillCatalog cat = SkillCatalog.Load(data, 0);
        var book = new SkillBook(cat);
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.Where(kv => kv.Key != skillId).ToList();
        levels.Add(new KeyValuePair<int, int>(skillId, level));
        var quick = quickSlot ? new List<KeyValuePair<int, int>> { new KeyValuePair<int, int>(0, skillId) } : new List<KeyValuePair<int, int>>();
        book.Restore(levels, quick, 0, 0);
        return HeroSkills.Create(data, chr, job, book, recipes);
    }

    // ------------------------------------------------------------------ 1. BuffStatIds / BuffStatEntry / BuffRecipe.WithExtraStats

    [Fact]
    public void BuffStatIdsMatchServerAppendageEnumeration()
    {
        Assert.Equal(0, BuffStatIds.PhysicalAttack);
        Assert.Equal(1, BuffStatIds.MagicalAttack);
        Assert.Equal(4, BuffStatIds.EquipmentPhysicalAttack);
        Assert.Equal(8, BuffStatIds.JumpPower);
        Assert.Equal(10, BuffStatIds.AttackSpeed);
        Assert.Equal(12, BuffStatIds.CastSpeed);
        Assert.Equal(15, BuffStatIds.PhysicalCriticalHitRate);
        Assert.Equal(30, BuffStatIds.HitRecovery);
        Assert.Equal(33, BuffStatIds.GoodHit);
        Assert.Equal((int)BuffStat.MagicalAttack, BuffStatIds.MagicalAttack);
        Assert.Equal((int)BuffStat.JumpPower, BuffStatIds.JumpPower);
    }

    [Fact]
    public void BuffStatEntryValidatesStatTypeSourceAndScaleBoundaries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BuffStatEntry(-1, 0, -1));
        Assert.Throws<ArgumentException>(() => new BuffStatEntry(0, -1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BuffStatEntry(0, 0, -1, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BuffStatEntry(0, 0, -1, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BuffStatEntry(0, 0, -1, double.NegativeInfinity));

        var staticZero = new BuffStatEntry(0, amountStaticIndex: 0, amountColumn: -1);
        Assert.Equal((0, 0, -1, 1.0), (staticZero.StatType, staticZero.AmountStaticIndex, staticZero.AmountColumn, staticZero.AmountScale));

        var columnZero = new BuffStatEntry(BuffStatIds.GoodHit, amountStaticIndex: -1, amountColumn: 0, amountScale: -0.1);
        Assert.Equal((33, -1, 0, -0.1), (columnZero.StatType, columnZero.AmountStaticIndex, columnZero.AmountColumn, columnZero.AmountScale));

        var bothNonNeg = new BuffStatEntry(BuffStatIds.AttackSpeed, amountStaticIndex: 0, amountColumn: 0, amountScale: 0.0);
        Assert.Equal((10, 0, 0, 0.0), (bothNonNeg.StatType, bothNonNeg.AmountStaticIndex, bothNonNeg.AmountColumn, bothNonNeg.AmountScale));
    }

    [Fact]
    public void BuffStatEntryEvaluatesStaticOrLevelWithScaleAndIgnoresPvpOverrides()
    {
        SkillDef def = SyntheticSkillDef(
            id: 951,
            statics: new[] { 120, 340, 560 },
            pvpStatics: new[] { 9120, 9340, 9560 },
            levels: new[]
            {
                new[] { 15000, 42, 75 },
                new[] { 22000, 88, 130 },
            },
            pvpLevels: new[]
            {
                new[] { 999, 888, 777 },
                new[] { 666, 555, 444 },
            });

        // 读不到定义一律为 0
        var fromStatic0 = new BuffStatEntry(BuffStatIds.AttackSpeed, amountStaticIndex: 0, amountColumn: 1, amountScale: -1.0);
        Assert.Equal(0.0, fromStatic0.Evaluate(null, 1));

        // AmountStaticIndex == 0 优先于 AmountColumn,且取 PVE [static data][0] = 120(不是 PVP 的 9120)
        Assert.Equal(-120.0, fromStatic0.Evaluate(def, 1), 6);
        Assert.Equal(-120.0, fromStatic0.Evaluate(def, 2), 6);

        var fromStatic2 = new BuffStatEntry(BuffStatIds.HitRecovery, amountStaticIndex: 2, amountColumn: -1, amountScale: -0.001);
        Assert.Equal(-0.56, fromStatic2.Evaluate(def, 1), 6);

        // AmountStaticIndex < 0 时走 [level info],且取 PVE(不是 PVP)
        var fromCol0 = new BuffStatEntry(BuffStatIds.CastSpeed, amountStaticIndex: -1, amountColumn: 0, amountScale: 1.0);
        Assert.Equal(15000.0, fromCol0.Evaluate(def, 1), 6);
        Assert.Equal(22000.0, fromCol0.Evaluate(def, 2), 6);

        var fromCol2Pos = new BuffStatEntry(BuffStatIds.PhysicalCriticalHitRate, amountStaticIndex: -1, amountColumn: 2, amountScale: 0.1);
        Assert.Equal(7.5, fromCol2Pos.Evaluate(def, 1), 6);
        Assert.Equal(13.0, fromCol2Pos.Evaluate(def, 2), 6);

        var fromCol2Neg = new BuffStatEntry(BuffStatIds.GoodHit, amountStaticIndex: -1, amountColumn: 2, amountScale: -0.1);
        Assert.Equal(-7.5, fromCol2Neg.Evaluate(def, 1), 6);
        Assert.Equal(-13.0, fromCol2Neg.Evaluate(def, 2), 6);
    }

    [Fact]
    public void BuffRecipeWithExtraStatsClonesImmutablyAndValidatesArguments()
    {
        var original = new BuffRecipe(952, (BuffStat)BuffStatIds.PhysicalAttack, durationColumn: 0, amountStaticIndex: -1, amountColumn: 1, amountScale: 0.5, guardSkillId: 914);
        Assert.Empty(original.ExtraStats);

        Assert.Throws<ArgumentNullException>(() => original.WithExtraStats(null));
        var e1 = new BuffStatEntry(BuffStatIds.AttackSpeed, amountStaticIndex: 0, amountColumn: -1, amountScale: -1.0);
        var e2 = new BuffStatEntry(BuffStatIds.HitRecovery, amountStaticIndex: 0, amountColumn: -1, amountScale: -0.001);
        Assert.Throws<ArgumentException>(() => original.WithExtraStats(e1, null));

        BuffRecipe emptyClone = original.WithExtraStats();
        Assert.NotSame(original, emptyClone);
        Assert.Empty(emptyClone.ExtraStats);

        BuffStatEntry[] input = { e1, e2 };
        BuffRecipe withExtras = original.WithExtraStats(input);
        input[0] = new BuffStatEntry(BuffStatIds.GoodHit, -1, 2, -0.1);          // 事后改动入参数组不影响副本

        Assert.NotSame(original, withExtras);
        Assert.Empty(original.ExtraStats);                                       // 原对象不受影响
        Assert.Equal((952, (BuffStat)BuffStatIds.PhysicalAttack, 0, -1, 1, 0.5, 914),
            (withExtras.SkillId, withExtras.Stat, withExtras.DurationColumn, withExtras.AmountStaticIndex, withExtras.AmountColumn, withExtras.AmountScale, withExtras.GuardSkillId));
        Assert.Equal(2, withExtras.ExtraStats.Count);
        Assert.Same(e1, withExtras.ExtraStats[0]);
        Assert.Same(e2, withExtras.ExtraStats[1]);
    }

    // ------------------------------------------------------------------ 2. Job 作用域条目、JobSkillKey 与 SkillRecipes.WithJobScoped

    [Fact]
    public void JobScopedEntryConstructorsValidateJobAndRecipe()
    {
        var buff = new BuffRecipe(953, BuffStat.MagicalAttack, 0, -1, 1);
        var guard = new GuardRecipe(954, 0, 0, 0, 1, 2, 3);
        var thr = ThrowRecipe.WithZeroCharge(955, 0, true, 1, new[] { SynthObj() });

        Assert.Throws<ArgumentOutOfRangeException>(() => new JobBuffEntry(-1, buff));
        Assert.Throws<ArgumentNullException>(() => new JobBuffEntry(0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobGuardEntry(-1, guard));
        Assert.Throws<ArgumentNullException>(() => new JobGuardEntry(0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobThrowEntry(-1, thr));
        Assert.Throws<ArgumentNullException>(() => new JobThrowEntry(0, null));

        var jb0 = new JobBuffEntry(0, buff);
        var jb5 = new JobBuffEntry(5, buff);
        var jg0 = new JobGuardEntry(0, guard);
        var jg9 = new JobGuardEntry(9, guard);
        var jt0 = new JobThrowEntry(0, thr);
        var jt4 = new JobThrowEntry(4, thr);
        Assert.Equal((0, 5), (jb0.Job, jb5.Job));
        Assert.Same(buff, jb5.Recipe);
        Assert.Equal((0, 9), (jg0.Job, jg9.Job));
        Assert.Same(guard, jg9.Recipe);
        Assert.Equal((0, 4), (jt0.Job, jt4.Job));
        Assert.Same(thr, jt4.Recipe);
    }

    [Fact]
    public void JobSkillKeyEqualityAndHashCodeDistinguishJobAndSkill()
    {
        var a = new JobSkillKey(1, 956);
        var same = new JobSkillKey(1, 956);
        var diffJob = new JobSkillKey(2, 956);
        var diffSkill = new JobSkillKey(1, 957);
        var swapped = new JobSkillKey(956, 1);

        Assert.Equal((1, 956), (a.Job, a.SkillId));
        Assert.True(a.Equals(same));
        Assert.False(a.Equals(diffJob));
        Assert.False(a.Equals(diffSkill));
        Assert.True(a.Equals((object)same));
        Assert.False(a.Equals((object)diffJob));
        Assert.False(a.Equals((object)diffSkill));
        Assert.False(a.Equals(null));
        Assert.False(a.Equals("not-a-key"));
        Assert.Equal(a.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(a.GetHashCode(), swapped.GetHashCode());
    }

    [Fact]
    public void WithJobScopedIsImmutableChainsAndOverwritesOnlyMatchingJobAndSkill()
    {
        var empty = new SkillRecipes(Array.Empty<SkillRecipe>());
        Assert.Equal((0, 0, 0), (empty.JobBuffCount, empty.JobGuardCount, empty.JobThrowCount));
        Assert.Empty(empty.AllJobBuffs);
        Assert.Empty(empty.AllJobGuards);
        Assert.Empty(empty.AllJobThrows);

        SkillRecipes noop = empty.WithJobScoped();
        Assert.Equal((0, 0, 0), (noop.JobBuffCount, noop.JobGuardCount, noop.JobThrowCount));

        Assert.Equal("jobBuffs", Assert.Throws<ArgumentException>(() => empty.WithJobScoped(jobBuffs: new JobBuffEntry[] { null })).ParamName);
        Assert.Equal("jobGuards", Assert.Throws<ArgumentException>(() => empty.WithJobScoped(jobGuards: new JobGuardEntry[] { null })).ParamName);
        Assert.Equal("jobThrows", Assert.Throws<ArgumentException>(() => empty.WithJobScoped(jobThrows: new JobThrowEntry[] { null })).ParamName);

        var b11a = new BuffRecipe(961, BuffStat.MagicalAttack, 0, -1, 1);
        var b11b = new BuffRecipe(961, BuffStat.JumpPower, 1, 0, -1);
        var b12 = new BuffRecipe(962, BuffStat.MagicalAttack, 0, -1, 1);
        var b21 = new BuffRecipe(961, BuffStat.SuperArmorChance, 1, -1, 0, 0.001);
        var b33 = new BuffRecipe(963, (BuffStat)BuffStatIds.PhysicalAttack, 0, -1, 1);

        var g11a = new GuardRecipe(961, 0, 0, 0, 1, 2, 3);
        var g11b = new GuardRecipe(961, 2, 1, 0, 1, 2, 3);
        var g12 = new GuardRecipe(962, 0, 0, 0, 1, 2, 3);
        var g21 = new GuardRecipe(961, 1, 0, 0, 1, 2, 3);
        var g33 = new GuardRecipe(963, 3, 0, 0, 1, 2, 3);

        var t11a = ThrowRecipe.WithZeroCharge(961, 0, false, 1, new[] { SynthObj() });
        var t11b = ThrowRecipe.WithZeroCharge(961, 1, true, 2, new[] { SynthObj() });
        var t12 = ThrowRecipe.WithZeroCharge(962, 0, true, 1, new[] { SynthObj() });
        var t21 = ThrowRecipe.WithZeroCharge(961, 0, false, 2, new[] { SynthObj() });
        var t33 = ThrowRecipe.WithZeroCharge(963, 0, true, 1, new[] { SynthObj() });

        // 同一次调用里重复传入同一 (job, skillId): 后者覆盖前者且不重复计数
        SkillRecipes dedup = empty.WithJobScoped(
            jobBuffs: new[] { new JobBuffEntry(1, b11a), new JobBuffEntry(1, b11b) },
            jobGuards: new[] { new JobGuardEntry(1, g11a), new JobGuardEntry(1, g11b) },
            jobThrows: new[] { new JobThrowEntry(1, t11a), new JobThrowEntry(1, t11b) });
        Assert.Equal((1, 1, 1), (dedup.JobBuffCount, dedup.JobGuardCount, dedup.JobThrowCount));
        Assert.Same(b11b, dedup.FindClassBuff(1, 961));
        Assert.Same(b11b, Assert.Single(dedup.AllJobBuffs).Recipe);
        Assert.Same(g11b, dedup.FindGuard(1, 961));
        Assert.Same(g11b, Assert.Single(dedup.AllJobGuards).Recipe);
        Assert.Same(t11b, dedup.FindThrow(1, 961));
        Assert.Same(t11b, Assert.Single(dedup.AllJobThrows).Recipe);

        // 链式调用: 第二次仅覆盖 (1, 961) 并新增 (3, 963),保留 (1, 962) 与 (2, 961),且不改动第一次的实例
        SkillRecipes stage1 = empty.WithJobScoped(
            jobBuffs: new[] { new JobBuffEntry(1, b11a), new JobBuffEntry(1, b12), new JobBuffEntry(2, b21) },
            jobGuards: new[] { new JobGuardEntry(1, g11a), new JobGuardEntry(1, g12), new JobGuardEntry(2, g21) },
            jobThrows: new[] { new JobThrowEntry(1, t11a), new JobThrowEntry(1, t12), new JobThrowEntry(2, t21) });
        SkillRecipes stage2 = stage1.WithJobScoped(
            jobBuffs: new[] { new JobBuffEntry(1, b11b), new JobBuffEntry(3, b33) },
            jobGuards: new[] { new JobGuardEntry(1, g11b), new JobGuardEntry(3, g33) },
            jobThrows: new[] { new JobThrowEntry(1, t11b), new JobThrowEntry(3, t33) });

        Assert.Equal((0, 0, 0), (empty.JobBuffCount, empty.JobGuardCount, empty.JobThrowCount));
        Assert.Equal((3, 3, 3), (stage1.JobBuffCount, stage1.JobGuardCount, stage1.JobThrowCount));
        Assert.Same(b11a, stage1.FindClassBuff(1, 961));
        Assert.Same(g11a, stage1.FindGuard(1, 961));
        Assert.Same(t11a, stage1.FindThrow(1, 961));

        Assert.Equal((4, 4, 4), (stage2.JobBuffCount, stage2.JobGuardCount, stage2.JobThrowCount));
        Assert.Same(b11b, stage2.FindClassBuff(1, 961));
        Assert.Same(b12, stage2.FindClassBuff(1, 962));
        Assert.Same(b21, stage2.FindClassBuff(2, 961));
        Assert.Same(b33, stage2.FindClassBuff(3, 963));
        Assert.Same(g11b, stage2.FindGuard(1, 961));
        Assert.Same(g12, stage2.FindGuard(1, 962));
        Assert.Same(g21, stage2.FindGuard(2, 961));
        Assert.Same(g33, stage2.FindGuard(3, 963));
        Assert.Same(t11b, stage2.FindThrow(1, 961));
        Assert.Same(t12, stage2.FindThrow(1, 962));
        Assert.Same(t21, stage2.FindThrow(2, 961));
        Assert.Same(t33, stage2.FindThrow(3, 963));
    }

    [Fact]
    public void JobScopedLookupsIsolateCollisionsAndPreserveSharedBaseBuffs()
    {
        var job0Multi = new SkillRecipe(971, new[] { new SkillStageSpec(0, 0, 0, 1, -1, 0) }, -1, -1, -1, -1, 0);
        var sharedBuff = new BuffRecipe(972, BuffStat.JumpPower, 0, 0, -1);
        var job0LegacyGuard = new GuardRecipe(973, 0, 0, 0, 1, 2, 3);
        var job0LegacyClassBuff = new BuffRecipe(974, BuffStat.AutoGuardChance, 1, -1, 0, 0.01, guardSkillId: 973);
        var job0LegacyThrow = new ThrowRecipe(975, 0, 1, -1, 1, new[] { SynthObj() });

        var job2BuffOnSharedId = new BuffRecipe(972, BuffStat.MagicalAttack, 0, -1, 1);
        var job2Buff = new BuffRecipe(974, (BuffStat)BuffStatIds.PhysicalAttack, 0, -1, 1);
        var job2Guard = new GuardRecipe(973, 1, 0, 0, 1, 2, 3);
        var job2Throw = ThrowRecipe.WithZeroCharge(975, 0, true, 1, new[] { SynthObj() });

        var baseOnly = new SkillRecipes(
            new[] { job0Multi },
            buffs: new[] { sharedBuff },
            guards: new[] { job0LegacyGuard },
            classBuffs: new[] { job0LegacyClassBuff },
            throws: new[] { job0LegacyThrow });

        // 尚未调用 WithJobScoped 时: 负职业一律 null, 首职业回退旧表, 非首职业共享基类增益但不继承首职业局部表
        Assert.Null(baseOnly.Find(-1, 971));
        Assert.Null(baseOnly.FindBuff(-1, 972));
        Assert.Null(baseOnly.FindClassBuff(-1, 974));
        Assert.Null(baseOnly.FindGuard(-1, 973));
        Assert.Null(baseOnly.FindThrow(-1, 975));

        Assert.Same(job0Multi, baseOnly.Find(0, 971));
        Assert.Null(baseOnly.Find(1, 971));
        Assert.Same(sharedBuff, baseOnly.FindBuff(0, 972));
        Assert.Same(sharedBuff, baseOnly.FindBuff(2, 972));
        Assert.Same(sharedBuff, baseOnly.FindBuff(10, 972));
        Assert.Null(baseOnly.FindClassBuff(0, 972));
        Assert.Same(job0LegacyClassBuff, baseOnly.FindBuff(0, 974));
        Assert.Same(job0LegacyClassBuff, baseOnly.FindClassBuff(0, 974));
        Assert.Null(baseOnly.FindBuff(1, 974));
        Assert.Null(baseOnly.FindClassBuff(1, 974));
        Assert.Same(job0LegacyGuard, baseOnly.FindGuard(0, 973));
        Assert.Null(baseOnly.FindGuard(1, 973));
        Assert.Same(job0LegacyThrow, baseOnly.FindThrow(0, 975));
        Assert.Null(baseOnly.FindThrow(1, 975));

        // 挂上职业 2 作用域条目: 共享增益优先于同号职业增益,其余按职业严格隔离
        SkillRecipes scoped = baseOnly.WithJobScoped(
            jobBuffs: new[] { new JobBuffEntry(2, job2BuffOnSharedId), new JobBuffEntry(2, job2Buff) },
            jobGuards: new[] { new JobGuardEntry(2, job2Guard) },
            jobThrows: new[] { new JobThrowEntry(2, job2Throw) });

        Assert.Null(scoped.FindBuff(-1, 972));
        Assert.Null(scoped.FindClassBuff(-1, 974));
        Assert.Null(scoped.FindGuard(-1, 973));
        Assert.Null(scoped.FindThrow(-1, 975));

        Assert.Same(sharedBuff, scoped.FindBuff(2, 972));               // FindBuff 优先命中基类共享增益
        Assert.Same(job2BuffOnSharedId, scoped.FindClassBuff(2, 972));  // FindClassBuff 查本职业表
        Assert.Same(job2Buff, scoped.FindBuff(2, 974));
        Assert.Same(job2Buff, scoped.FindClassBuff(2, 974));
        Assert.Same(job0LegacyClassBuff, scoped.FindClassBuff(0, 974));
        Assert.Null(scoped.FindBuff(3, 974));

        Assert.Same(job2Guard, scoped.FindGuard(2, 973));
        Assert.Same(job0LegacyGuard, scoped.FindGuard(0, 973));
        Assert.Null(scoped.FindGuard(3, 973));

        Assert.Same(job2Throw, scoped.FindThrow(2, 975));
        Assert.Same(job0LegacyThrow, scoped.FindThrow(0, 975));
        Assert.Null(scoped.FindThrow(3, 975));

        // 首职业也可在 WithJobScoped 里覆盖旧表
        var job0OverrideBuff = new BuffRecipe(974, BuffStat.MagicalAttack, 0, -1, 1);
        var job0OverrideGuard = new GuardRecipe(973, 4, 0, 0, 1, 2, 3);
        var job0OverrideThrow = ThrowRecipe.WithZeroCharge(975, 0, false, 2, new[] { SynthObj() });
        SkillRecipes over0 = scoped.WithJobScoped(
            jobBuffs: new[] { new JobBuffEntry(0, job0OverrideBuff) },
            jobGuards: new[] { new JobGuardEntry(0, job0OverrideGuard) },
            jobThrows: new[] { new JobThrowEntry(0, job0OverrideThrow) });
        Assert.Same(job0OverrideBuff, over0.FindClassBuff(0, 974));
        Assert.Same(job0OverrideGuard, over0.FindGuard(0, 973));
        Assert.Same(job0OverrideThrow, over0.FindThrow(0, 975));
        Assert.Same(job2Guard, over0.FindGuard(2, 973));
    }

    // ------------------------------------------------------------------ 3. 状态 0x0d 零蓄力兜底、无条件霸体与 ThrowRun / RoomCombat

    [Fact]
    public void ThrowRecipeWithZeroChargeValidatesArgumentsAndSetsFallbackFlags()
    {
        SkillObjectSpec[] objs = { SynthObj() };

        // 普通构造: 阶段 0 下标必须非负,且默认不走零蓄力兜底、不无条件霸体
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrowRecipe(981, -1, 0, -1, 1, objs));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrowRecipe(981, 0, -1, -1, 1, objs));
        var normalZeroIdx = new ThrowRecipe(981, chargeStaticIndex: 0, shootStaticIndex: 0, armorStaticIndex: -1, poseSelector: 1, objs);
        Assert.Equal((0, 0, -1, false, false, 1),
            (normalZeroIdx.ChargeStaticIndex, normalZeroIdx.ShootStaticIndex, normalZeroIdx.ArmorStaticIndex,
             normalZeroIdx.UsesZeroChargeFallback, normalZeroIdx.AlwaysArmorInCharge, normalZeroIdx.PoseSelector));
        Assert.Equal("[throw motion 1-1]", normalZeroIdx.ChargePoseTag);
        Assert.Equal("[throw motion 1-2]", normalZeroIdx.ShootPoseTag);

        // WithZeroCharge 仍校验技能号、阶段 1 下标、姿势选择号与对象表
        Assert.Throws<ArgumentOutOfRangeException>(() => ThrowRecipe.WithZeroCharge(0, 0, true, 1, objs));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThrowRecipe.WithZeroCharge(982, -1, true, 1, objs));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThrowRecipe.WithZeroCharge(982, 0, true, 0, objs));
        Assert.Throws<ArgumentNullException>(() => ThrowRecipe.WithZeroCharge(982, 0, true, 1, null));
        Assert.Throws<ArgumentException>(() => ThrowRecipe.WithZeroCharge(982, 0, true, 1, Array.Empty<SkillObjectSpec>()));

        ThrowRecipe armored = ThrowRecipe.WithZeroCharge(982, shootStaticIndex: 0, alwaysArmorInCharge: true, poseSelector: 2, objs);
        Assert.Equal((982, -1, 0, -1, true, true, 2),
            (armored.SkillId, armored.ChargeStaticIndex, armored.ShootStaticIndex, armored.ArmorStaticIndex,
             armored.UsesZeroChargeFallback, armored.AlwaysArmorInCharge, armored.PoseSelector));
        Assert.Equal("[throw motion 2-1]", armored.ChargePoseTag);
        Assert.Equal("[throw motion 2-2]", armored.ShootPoseTag);

        ThrowRecipe unarmored = ThrowRecipe.WithZeroCharge(983, shootStaticIndex: 1, alwaysArmorInCharge: false, poseSelector: 1, objs);
        Assert.Equal((983, -1, 1, -1, true, false, 1),
            (unarmored.SkillId, unarmored.ChargeStaticIndex, unarmored.ShootStaticIndex, unarmored.ArmorStaticIndex,
             unarmored.UsesZeroChargeFallback, unarmored.AlwaysArmorInCharge, unarmored.PoseSelector));
    }

    [Fact]
    public void HeroSkillSlotThrowArmorAndNewThrowRunCoverZeroChargeAlwaysArmorAndPvpIsolation()
    {
        SkillDef def = SyntheticSkillDef(
            id: 984,
            statics: new[] { 180, 0, 1, 2 },
            pvpStatics: new[] { 999, 1, 0, 0 },
            levels: new[] { new[] { 100 } },
            pvpLevels: new[] { new[] { 500 } });
        SkillObjectSpec[] objs = { SynthObj() };

        // 非投掷槽或缺少定义: 无霸体且建不出阶段机(即使 AlwaysArmorInCharge 为真,缺 Def 也必须 false/null)
        var plain = new HeroSkillSlot(SkillKeys.A, 984, 1, def);
        Assert.False(plain.ThrowArmor);
        Assert.Null(plain.NewThrowRun());

        ThrowRecipe zeroArmored = ThrowRecipe.WithZeroCharge(984, shootStaticIndex: 0, alwaysArmorInCharge: true, poseSelector: 1, objs);
        var noDef = new HeroSkillSlot(SkillKeys.A, 984, 1, null) { Throw = zeroArmored };
        Assert.False(noDef.ThrowArmor);
        Assert.Null(noDef.NewThrowRun());

        // 零蓄力 + 无条件霸体: ChargeMs 走 70ms 兜底, ShootMs 取 PVE static[0]=180(不取 PVP 999), 阶段 0 有霸体、阶段 1 关闭
        var slotZeroArmored = new HeroSkillSlot(SkillKeys.A, 984, 1, def) { Throw = zeroArmored };
        Assert.True(slotZeroArmored.ThrowArmor);
        ThrowRun run = slotZeroArmored.NewThrowRun();
        Assert.NotNull(run);
        Assert.Equal((70, 180, true, 0, true), (run.ChargeMs, run.ShootMs, run.ArmorInCharge, run.Phase, run.ArmorActive));
        run.Advance(69);
        Assert.Equal((0, true, false), (run.Phase, run.ArmorActive, run.Finished));
        run.Advance(1);
        Assert.Equal((1, false, false), (run.Phase, run.ArmorActive, run.Finished));
        run.Advance(180);
        Assert.Equal((2, false, true), (run.Phase, run.ArmorActive, run.Finished));

        // 零蓄力 + 无霸体
        var slotZeroPlain = new HeroSkillSlot(SkillKeys.A, 984, 1, def)
        {
            Throw = ThrowRecipe.WithZeroCharge(984, shootStaticIndex: 0, alwaysArmorInCharge: false, poseSelector: 1, objs),
        };
        Assert.False(slotZeroPlain.ThrowArmor);
        ThrowRun runPlain = slotZeroPlain.NewThrowRun();
        Assert.Equal((70, 180, false, false), (runPlain.ChargeMs, runPlain.ShootMs, runPlain.ArmorInCharge, runPlain.ArmorActive));

        // 普通投掷: ChargeStaticIndex == 0 取 PVE static[0]=180(绝不误走 70ms 兜底、不取 PVP 999); ArmorStaticIndex 边界 0/1/2/3 验证 PVE 非 0 开关
        var slotStaticArmor0 = new HeroSkillSlot(SkillKeys.A, 984, 1, def)
        {
            Throw = new ThrowRecipe(984, chargeStaticIndex: 0, shootStaticIndex: 3, armorStaticIndex: 0, poseSelector: 1, objs),
        };
        Assert.True(slotStaticArmor0.ThrowArmor);
        ThrowRun runStatic0 = slotStaticArmor0.NewThrowRun();
        Assert.Equal((180, 2, true), (runStatic0.ChargeMs, runStatic0.ShootMs, runStatic0.ArmorInCharge));

        var slotStaticArmorOff = new HeroSkillSlot(SkillKeys.A, 984, 1, def)
        {
            Throw = new ThrowRecipe(984, chargeStaticIndex: 0, shootStaticIndex: 2, armorStaticIndex: 1, poseSelector: 1, objs),
        };
        Assert.False(slotStaticArmorOff.ThrowArmor);             // PVE static[1]==0, PVP static[1]==1 -> 必须按 PVE 判 false

        var slotStaticArmorOne = new HeroSkillSlot(SkillKeys.A, 984, 1, def)
        {
            Throw = new ThrowRecipe(984, chargeStaticIndex: 0, shootStaticIndex: 2, armorStaticIndex: 2, poseSelector: 1, objs),
        };
        Assert.True(slotStaticArmorOne.ThrowArmor);              // PVE static[2]==1, PVP static[2]==0 -> 必须按 PVE 判 true

        var slotStaticArmorTwo = new HeroSkillSlot(SkillKeys.A, 984, 1, def)
        {
            Throw = new ThrowRecipe(984, chargeStaticIndex: 0, shootStaticIndex: 2, armorStaticIndex: 3, poseSelector: 1, objs),
        };
        Assert.True(slotStaticArmorTwo.ThrowArmor);              // PVE static[3]==2 (!= 0 不仅限于 == 1)
    }

    [Fact]
    public void RoomCombatRunsCrossJobZeroChargeAlwaysArmorThrowAndAbsorbsPhaseZeroHit()
    {
        // 用 WaveSkill 当作职业 2 的零蓄力常驻霸体 0x0d 投掷: 阶段 0=70ms 兜底, 阶段 1=static[0]=250ms, 无条件霸体, 放 1 个对象
        SkillRecipes recipes = new SkillRecipes(Array.Empty<SkillRecipe>()).WithJobScoped(
            jobThrows: new[]
            {
                new JobThrowEntry(2, ThrowRecipe.WithZeroCharge(AvatarFixture.WaveSkill, shootStaticIndex: 0, alwaysArmorInCharge: true, poseSelector: 1,
                    objects: new[] { SynthObj(AvatarFixture.WaveObjectId, stage: 0, rateCol: 2) })),
            });

        // 1) 空场地完整周期: 70ms 切阶段 1(共 7 步 10ms), 再过 250ms(25 步)收尾回站立
        using (var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => LoadJobSkills(data, job: 2, AvatarFixture.WaveSkill, level: 2, recipes, quickSlot: true, addThrowPoses: true)))
        {
            arena.Session.AttachActs(new ActLibrary(arena.Assets, new GameData(new PvfScriptSource(arena.Assets.Pvf))));
            RoomCombatSession s = arena.Session;
            Assert.Equal(2, s.Skills.Job);
            s.Update(10, 0, 0, false, false, SkillKeys.A);
            Assert.Equal((1, 1, 0, true, 1), (s.SkillCasts, s.ThrowCasts, s.ThrowPhase, s.ThrowActive, s.HeroObjectsSpawned));

            for (int i = 0; i < 5; i++) s.Update(10, 0, 0, false, false, SkillKeys.None);     // 累计 60ms: 仍在阶段 0
            Assert.Equal((0, true), (s.ThrowPhase, s.ThrowActive));

            s.Update(10, 0, 0, false, false, SkillKeys.None);                                  // 累计 70ms: 切阶段 1
            Assert.Equal((1, true), (s.ThrowPhase, s.ThrowActive));

            for (int i = 0; i < 24; i++) s.Update(10, 0, 0, false, false, SkillKeys.None);     // 阶段 1 走 240ms: 仍在阶段 1
            Assert.Equal((1, true), (s.ThrowPhase, s.ThrowActive));

            s.Update(10, 0, 0, false, false, SkillKeys.None);                                  // 阶段 1 满 250ms: 结束回站立
            Assert.Equal((-1, false, HeroMotion.Rest, 0), (s.ThrowPhase, s.ThrowActive, s.Hero.Motion, s.ThrowCancels));
        }

        // 2) 阶段 0(70ms 窗口内)挨打: AlwaysArmorInCharge 霸体挡下受击反应,阶段机不打断并走完
        using (var fx = new AvatarFixture())
        using (DnfAssets assets = DnfAssets.Open(fx.Root))
        {
            var vitals = new HeroVitals(1, 1000, 50, 100, 0, 0);
            var tiles = Enumerable.Range(0, 5).Select(i => new TileFile("t" + i, new List<KeyValuePair<string, int>>(), 80, new byte[14 * 6])).ToList();
            var map = new RoomMap("map/test/arena.map", "[normal]", tiles, 50, 80, 120, new List<RoomBackground>(), new List<MapAnimation>(),
                new List<int[]> { null, null, null, null }, null);
            var content = new RoomContent(map, null, null, null, null);
            TownHero hero = NewHero(assets, facingLeft: true);
            var room = new RoomSession(assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));
            MonsterModel model = MonsterModel.Read(assets, AvatarFixture.BlobStrikerMobPath);
            var monster = new MonsterActor(0, new RoomMonsterSpawn(1, 0, 0, 340, HeroY, 0, 1, 0, "[fixed]", "[normal]"), model, 1, 100000, 222.5, 100000, 0, 0)
            {
                PhysicalAttack = 40,
                Strike = new HeroStrikeInfo(AvatarFixture.StrikerDamageBonus, 0, false, 0, 0, -1, false, -1f, 0, false, 0, false, 300, false, false,
                    reactionDamage: true, hitWav: null, reactionNone: false, ignoreSuperArmor: false),
            };
            var plain = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
            var constants = new DamageConstants { PhysicalDamageRate = 1000f, MagicalDamageRate = 1000f, PhysicalReduceRate = 100f, MagicalReduceRate = 100f };
            HeroSkills skills = LoadJobSkills(new GameData(new PvfScriptSource(assets.Pvf)), job: 2, AvatarFixture.WaveSkill, level: 1, recipes, quickSlot: true,
                addThrowPoses: true, chargeAni: AvatarFixture.GuardAni, shootAni: AvatarFixture.GuardAni);
            var session = new RoomCombatSession(assets, room, new[] { monster }, plain, 1, default, default, skills, 0, -1f,
                new HitStunTable(10, 330, 1000), vitals, constants);
            session.AttachActs(new ActLibrary(assets, new GameData(new PvfScriptSource(assets.Pvf))));

            // 先推进到怪物快出手(13 步=130ms, 首击落在第 16 步前后),再放出 70ms 阶段 0 的零蓄力常驻霸体投掷
            for (int i = 0; i < 13; i++) session.Update(10, 0, 0, false, false, SkillKeys.None);
            Assert.Equal(0, session.HeroHits);

            session.Update(10, 0, 0, false, false, SkillKeys.A);
            Assert.Equal((1, 0, true), (session.ThrowCasts, session.ThrowPhase, session.ThrowActive));

            for (int i = 0; i < 6 && session.HeroHits == 0; i++) session.Update(10, 0, 0, false, false, SkillKeys.None);
            Assert.Equal((1, 1, 0, 0, HeroMotion.Skill), (session.HeroHits, session.SuperArmorAbsorbed, hero.HurtCount, session.ThrowPhase, hero.Motion));
            Assert.Equal(975, vitals.Hp);

            for (int i = 0; i < 50 && session.ThrowActive; i++) session.Update(10, 0, 0, false, false, SkillKeys.None);
            Assert.Equal((false, 0, HeroMotion.Rest), (session.ThrowActive, session.ThrowCancels, hero.Motion));
        }
    }

    // ------------------------------------------------------------------ 4. HeroSkills 职业作用域、BuffExtraAmounts、IsImplemented、Cast 与碰撞隔离

    [Fact]
    public void HeroSkillsConstructorsAndNullCreatesPreserveJob()
    {
        var oneArg = new HeroSkills(Array.Empty<HeroSkillSlot>());
        var twoArg = new HeroSkills(Array.Empty<HeroSkillSlot>(), Array.Empty<HeroSkillSlot>());
        var threeArg = new HeroSkills(Array.Empty<HeroSkillSlot>(), null, job: 4);
        HeroSkills nullInit = HeroSkills.Create(null, null, job: 6);
        HeroSkills nullBook = HeroSkills.Create(null, null, job: 7, book: null);

        Assert.Equal((0, 0, 4, 6, 7), (oneArg.Job, twoArg.Job, threeArg.Job, nullInit.Job, nullBook.Job));
    }

    [Fact]
    public void HeroSkillsIsImplementedEnforcesJobScopeAndSharedBaseSkills()
    {
        // 非法职业号与非正技能号一律 false(即使传基类共享技能)
        Assert.False(HeroSkills.IsImplemented(-1, SkillIds.BackStep));
        Assert.False(HeroSkills.IsImplemented(-1, SkillIds.QuickStand));
        Assert.False(HeroSkills.IsImplemented(-1, SkillRecipes.Default.AllBuffs.First().SkillId));
        Assert.False(HeroSkills.IsImplemented(0, 0));
        Assert.False(HeroSkills.IsImplemented(0, -3));
        Assert.False(HeroSkills.IsImplemented(2, 0));

        // 5 个基类共享项对全部非负职业(0..10)都生效
        int[] sharedBuffIds = SkillRecipes.Default.AllBuffs.Select(b => b.SkillId).ToArray();
        Assert.Equal(3, sharedBuffIds.Length);
        for (int job = 0; job <= 10; job++)
        {
            Assert.True(HeroSkills.IsImplemented(job, SkillIds.BackStep));
            Assert.True(HeroSkills.IsImplemented(job, SkillIds.QuickStand));
            foreach (int buffId in sharedBuffIds)
            {
                Assert.True(HeroSkills.IsImplemented(job, buffId));
            }
        }

        // 首职业手写技(HardAttack/UpperSlash)仅对职业 0 生效
        Assert.True(HeroSkills.IsImplemented(SkillIds.HardAttack));
        Assert.True(HeroSkills.IsImplemented(0, SkillIds.HardAttack));
        Assert.True(HeroSkills.IsImplemented(SkillIds.UpperSlash));
        Assert.True(HeroSkills.IsImplemented(0, SkillIds.UpperSlash));
        for (int job = 1; job <= 10; job++)
        {
            Assert.False(HeroSkills.IsImplemented(job, SkillIds.HardAttack));
            Assert.False(HeroSkills.IsImplemented(job, SkillIds.UpperSlash));
        }

        // 首职业专属配方/旧专属增益/旧防御姿态/旧投掷不泄漏给没有登记该号的其它职业(例如职业 2、职业 3)
        SkillRecipe job0Recipe = SkillRecipes.Default.All.First();
        BuffRecipe job0ClassBuff = Assert.Single(SkillRecipes.Default.AllClassBuffs);
        GuardRecipe job0Guard = Assert.Single(SkillRecipes.Default.AllGuards);
        ThrowRecipe job0Throw = Assert.Single(SkillRecipes.Default.AllThrows);

        Assert.True(HeroSkills.IsImplemented(job0Recipe.SkillId));
        Assert.True(HeroSkills.IsImplemented(0, job0Recipe.SkillId));
        Assert.True(HeroSkills.IsImplemented(0, job0ClassBuff.SkillId));
        Assert.True(HeroSkills.IsImplemented(0, job0Guard.SkillId));          // 兼测 skillId == 1 的边界(kill skillId <= 0 -> <= 1)
        Assert.True(HeroSkills.IsImplemented(0, job0Throw.SkillId));
        Assert.False(HeroSkills.IsImplemented(99999));

        foreach (int otherJob in new[] { 2, 3, 5, 6, 8, 10 })
        {
            Assert.False(HeroSkills.IsImplemented(otherJob, job0Recipe.SkillId));
            Assert.False(HeroSkills.IsImplemented(otherJob, job0ClassBuff.SkillId));
            Assert.False(HeroSkills.IsImplemented(otherJob, job0Guard.SkillId));
            Assert.False(HeroSkills.IsImplemented(otherJob, job0Throw.SkillId));
        }
    }

    [Fact]
    public void HeroSkillSlotBuffExtraAmountsEvaluatesAllExtraStatsByLevel()
    {
        SkillDef def = SyntheticSkillDef(
            id: 985,
            statics: new[] { 250 },
            pvpStatics: new[] { 9250 },
            levels: new[]
            {
                new[] { 18000, 35, 64 },
                new[] { 24000, 70, 128 },
            },
            pvpLevels: new[]
            {
                new[] { 1000, 1, 2 },
                new[] { 2000, 3, 4 },
            });

        var noExtras = new BuffRecipe(985, (BuffStat)BuffStatIds.PhysicalAttack, 0, -1, 1);
        var oneExtra = noExtras.WithExtraStats(new BuffStatEntry(BuffStatIds.GoodHit, -1, 2, -0.1));
        var twoExtras = noExtras.WithExtraStats(
            new BuffStatEntry(BuffStatIds.AttackSpeed, 0, -1, -1.0),
            new BuffStatEntry(BuffStatIds.HitRecovery, 0, -1, -0.001));

        // 边界: Buff == null / Def == null / ExtraStats 为空 -> 皆返回空表
        Assert.Empty(new HeroSkillSlot(SkillKeys.A, 985, 1, def).BuffExtraAmounts);
        Assert.Empty(new HeroSkillSlot(SkillKeys.A, 985, 1, null) { Buff = oneExtra }.BuffExtraAmounts);
        Assert.Empty(new HeroSkillSlot(SkillKeys.A, 985, 1, def) { Buff = noExtras }.BuffExtraAmounts);

        // 1 条额外属性(击杀 ExtraStats.Count == 0 -> == 1)
        var slotOneL1 = new HeroSkillSlot(SkillKeys.A, 985, 1, def) { Buff = oneExtra };
        KeyValuePair<int, double> kv1 = Assert.Single(slotOneL1.BuffExtraAmounts);
        Assert.Equal(BuffStatIds.GoodHit, kv1.Key);
        Assert.Equal(-6.4, kv1.Value, 6);

        var slotOneL2 = new HeroSkillSlot(SkillKeys.A, 985, 2, def) { Buff = oneExtra };
        Assert.Equal((24000.0, 70.0), (slotOneL2.BuffDurationMs, slotOneL2.BuffAmount));
        Assert.Equal(-12.8, Assert.Single(slotOneL2.BuffExtraAmounts).Value, 6);

        // 2 条额外属性(取 [static data])
        var slotTwo = new HeroSkillSlot(SkillKeys.A, 985, 1, def) { Buff = twoExtras };
        IReadOnlyList<KeyValuePair<int, double>> pairs = slotTwo.BuffExtraAmounts;
        Assert.Equal(2, pairs.Count);
        Assert.Equal(BuffStatIds.AttackSpeed, pairs[0].Key);
        Assert.Equal(-250.0, pairs[0].Value, 6);
        Assert.Equal(BuffStatIds.HitRecovery, pairs[1].Key);
        Assert.Equal(-0.25, pairs[1].Value, 6);
    }

    [Fact]
    public void NonZeroJobIsolatesSwordmanHandcraftedSkillsAndAllowsScopedBuffGuardAndBackStep()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        CharacterDef baseChr = data.GetCharacter(0);

        // 1) 三参 Create(data, chr, job): 职业 0 的 HardAttack 带攻击信息与 2 个特效; 职业 2 读带真实指令的合成目录时不挂 HardAttack 专属命中与特效
        HeroSkills initJob0 = HeroSkills.Create(data, baseChr, EquipRules.SwordmanJob);
        HeroSkillSlot haJob0 = initJob0.SlotOfSkill(SkillIds.HardAttack);
        Assert.Equal(0, initJob0.Job);
        Assert.NotNull(haJob0);
        Assert.True(haJob0.Strike.HasValue && haJob0.Strike.Value.IsSkillDamage);
        Assert.Equal(2, haJob0.EffectPaths.Count);
        Assert.Equal(HeroSkills.HardAttackEffectForwardX, haJob0.EffectForwardX);

        // 构造含真实括号指令的合成目录: HardAttack(SKILL), UpperSlash(UP,SKILL), BackStep(DOWN,JUMP), QuickStand(UP,JUMP)
        SkillDef haCmdDef = SyntheticSkillDef(SkillIds.HardAttack, new[] { 120 }, new[] { 999 }, new[] { new[] { 200, 40 } }, new[] { new[] { 9, 9 } }, "(SKILL)");
        SkillDef usCmdDef = SyntheticSkillDef(SkillIds.UpperSlash, new[] { 110 }, new[] { 999 }, new[] { new[] { 150, 30 } }, new[] { new[] { 9, 9 } }, "(UP)", "(SKILL)");
        SkillDef bsCmdDef = SyntheticSkillDef(SkillIds.BackStep, new[] { 270 }, new[] { 999 }, new[] { new[] { 10 } }, new[] { new[] { 9 } }, "(DOWN)", "(JUMP)");
        SkillDef qsCmdDef = SyntheticSkillDef(SkillIds.QuickStand, new[] { 150 }, new[] { 999 }, new[] { new[] { 10 } }, new[] { new[] { 9 } }, "(UP)", "(JUMP)");
        var cmdCatalog = new SkillCatalog(2, new[] { haCmdDef, usCmdDef, bsCmdDef, qsCmdDef });

        SkillBook MakeCmdBook(bool quickHardAttack)
        {
            var b = new SkillBook(cmdCatalog);
            b.Restore(
                new[]
                {
                    new KeyValuePair<int, int>(SkillIds.HardAttack, 1),
                    new KeyValuePair<int, int>(SkillIds.UpperSlash, 1),
                    new KeyValuePair<int, int>(SkillIds.BackStep, 1),
                    new KeyValuePair<int, int>(SkillIds.QuickStand, 1),
                },
                quickHardAttack ? new[] { new KeyValuePair<int, int>(0, SkillIds.HardAttack) } : Array.Empty<KeyValuePair<int, int>>(),
                0, 0);
            return b;
        }

        CharacterDef chrJob2 = new CharacterDef(2, baseChr.Path, baseChr.Tags.Lines);
        SkillRecipes emptyRecipes = new SkillRecipes(Array.Empty<SkillRecipe>());
        HeroSkills job2Unscoped = HeroSkills.Create(data, chrJob2, job: 2, MakeCmdBook(quickHardAttack: true), emptyRecipes);
        Assert.Equal(2, job2Unscoped.Job);
        HeroSkillSlot haJob2 = job2Unscoped.SlotOf(SkillKeys.A);
        Assert.NotNull(haJob2);
        Assert.Null(haJob2.Strike);
        Assert.Empty(haJob2.EffectPaths);
        Assert.Equal(0, haJob2.EffectForwardX);
        Assert.DoesNotContain(job2Unscoped.CommandSlots, s => s.SkillId == SkillIds.UpperSlash);
        Assert.DoesNotContain(job2Unscoped.CommandSlots, s => s.SkillId == SkillIds.HardAttack);
        Assert.Contains(job2Unscoped.CommandSlots, s => s.SkillId == SkillIds.BackStep);
        Assert.Contains(job2Unscoped.CommandSlots, s => s.SkillId == SkillIds.QuickStand);

        // 职业 2 对无配方的 HardAttack / UpperSlash: Cast 返回 NotImplemented, 指令也不触发(且 QuickStand 不进指令释放)
        TownHero hero = NewHero(assets);
        Assert.Equal(SkillCastResult.NotImplemented, job2Unscoped.Cast(haJob2, hero));
        var upperOnlyJob2 = LoadJobSkills(data, job: 2, SkillIds.UpperSlash, level: 1, emptyRecipes, quickSlot: true);
        Assert.Equal(SkillCastResult.NotImplemented, upperOnlyJob2.Cast(upperOnlyJob2.SlotOf(SkillKeys.A), hero));

        var cmdInput = new CommandInput();
        cmdInput.Update(10, 0, 0, attackPressed: false, jumpPressed: false, skillPressed: true, buffPressed: false);
        Assert.Null(job2Unscoped.TryCastCommand(cmdInput, hero));

        var qsInput = new CommandInput();
        qsInput.Update(10, 0, -1, attackPressed: false, jumpPressed: false, skillPressed: false, buffPressed: false);
        qsInput.Update(10, 0, -1, attackPressed: false, jumpPressed: true, skillPressed: false, buffPressed: false);
        Assert.Null(job2Unscoped.TryCastCommand(qsInput, hero));

        // 职业 2 仍共享 BackStep(基类能力): 既可 Cast 也可按指令放,且取 PVE static[0]=270(不取 PVP 999)
        HeroSkillSlot backStepJob2 = job2Unscoped.SlotOfSkill(SkillIds.BackStep);
        Assert.NotNull(backStepJob2);
        Assert.Equal(SkillCastResult.Cast, job2Unscoped.Cast(backStepJob2, hero));
        Assert.True(hero.IsBackStep);

        job2Unscoped.Advance(3000);
        TownHero heroBsCmd = NewHero(assets);
        var bsInput = new CommandInput();
        bsInput.Update(10, 0, 1, attackPressed: false, jumpPressed: false, skillPressed: false, buffPressed: false);
        bsInput.Update(10, 0, 1, attackPressed: false, jumpPressed: true, skillPressed: false, buffPressed: false);
        HeroSkillSlot bsCast = job2Unscoped.TryCastCommand(bsInput, heroBsCmd);
        Assert.NotNull(bsCast);
        Assert.Equal(SkillIds.BackStep, bsCast.SkillId);
        Assert.True(heroBsCmd.IsBackStep);

        // BackStep 缺 Def 时按 0 传力仍能放(不抛异常,1ms 重力即落地回站立,绝不误传 1); 与 PVE static[0]=270(而非 PVP 999) 对照起跳高度
        TownHero hero2 = NewHero(assets);
        var noDefBackStep = new HeroSkills(new[] { new HeroSkillSlot(SkillKeys.A, SkillIds.BackStep, 1, null) }, null, job: 2);
        Assert.Equal(SkillCastResult.Cast, noDefBackStep.Cast(noDefBackStep.SlotOf(SkillKeys.A), hero2));
        Assert.True(hero2.IsBackStep);
        hero2.Update(1, 0, 0, false, false);
        Assert.Equal((0.0, HeroMotion.Rest), (hero2.Z, hero2.Motion));

        TownHero heroPvpPower = NewHero(assets);
        Assert.True(heroPvpPower.BeginBackStep(SkillIds.BackStep, 999));
        heroBsCmd.Update(100, 0, 0, false, false);
        heroPvpPower.Update(100, 0, 0, false, false);
        Assert.True(heroBsCmd.Z > 0.0);
        Assert.True(heroBsCmd.Z < heroPvpPower.Z);

        // 职业 0 的 HardAttack 与 UpperSlash 回归不退化(且两招都在 CommandSlots/可 Cast)
        HeroSkills job0Book = HeroSkills.Create(data, baseChr, job: 0, MakeCmdBook(quickHardAttack: true), emptyRecipes);
        Assert.Contains(job0Book.CommandSlots, s => s.SkillId == SkillIds.UpperSlash);
        TownHero hero0a = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, job0Book.Cast(job0Book.SlotOf(SkillKeys.A), hero0a));
        Assert.Equal((HeroMotion.Skill, SkillIds.HardAttack), (hero0a.Motion, hero0a.SkillId));
        TownHero hero0b = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, job0Book.Cast(job0Book.SlotOfSkill(SkillIds.UpperSlash), hero0b));
        Assert.Equal((HeroMotion.Skill, SkillIds.UpperSlash), (hero0b.Motion, hero0b.SkillId));

        // 2) 当职业 2 给与 HardAttack / UpperSlash 同号的技能挂上本职业 0x11 增益配方时:
        //    必须走 Buff 施法分支,绝不被首职业 HardAttack/UpperSlash 劫持
        var buffOnHaId = new BuffRecipe(SkillIds.HardAttack, BuffStat.MagicalAttack, durationColumn: 0, amountStaticIndex: -1, amountColumn: 1)
            .WithExtraStats(new BuffStatEntry(BuffStatIds.CastSpeed, -1, 1, 0.5));
        var buffOnUsId = new BuffRecipe(SkillIds.UpperSlash, BuffStat.JumpPower, durationColumn: 0, amountStaticIndex: -1, amountColumn: 0);
        SkillRecipes scopedOverride = emptyRecipes.WithJobScoped(jobBuffs: new[]
        {
            new JobBuffEntry(2, buffOnHaId),
            new JobBuffEntry(2, buffOnUsId),
        });

        HeroSkills job2WithBuffs = HeroSkills.Create(data, chrJob2, job: 2, MakeCmdBook(quickHardAttack: true), scopedOverride);
        HeroSkillSlot haBuffSlot = job2WithBuffs.SlotOf(SkillKeys.A);
        Assert.Same(buffOnHaId, haBuffSlot.Buff);
        Assert.Null(haBuffSlot.Strike);
        Assert.Empty(haBuffSlot.EffectPaths);
        Assert.Equal((200.0, 40.0), (haBuffSlot.BuffDurationMs, haBuffSlot.BuffAmount));
        Assert.Equal(20.0, Assert.Single(haBuffSlot.BuffExtraAmounts).Value, 6);
        // UpperSlash 没上快捷栏但挂了职业 2 增益配方 -> 进入 CommandSlots
        HeroSkillSlot usBuffSlot = Assert.Single(job2WithBuffs.CommandSlots, s => s.SkillId == SkillIds.UpperSlash);
        Assert.Same(buffOnUsId, usBuffSlot.Buff);

        TownHero heroBuff1 = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, job2WithBuffs.Cast(haBuffSlot, heroBuff1));
        Assert.Equal((HeroMotion.Skill, SkillIds.HardAttack), (heroBuff1.Motion, heroBuff1.SkillId));
        Assert.Equal(AvatarFixture.BuffAni, heroBuff1.CurrentAnimationPath);     // 播的是 [buff motion],绝不是 hardattack.ani

        TownHero heroBuff2 = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, job2WithBuffs.Cast(usBuffSlot, heroBuff2));
        Assert.Equal((HeroMotion.Skill, SkillIds.UpperSlash), (heroBuff2.Motion, heroBuff2.SkillId));
        Assert.Equal(AvatarFixture.BuffAni, heroBuff2.CurrentAnimationPath);     // 播的是 [buff motion],绝不是 UpperSlash

        // 指令释放同样走职业 2 自己的 Buff 槽(先走完冷却)
        job2WithBuffs.Advance(3000);
        TownHero heroBuffCmd = NewHero(assets);
        var haCmdInput = new CommandInput();
        haCmdInput.Update(10, 0, 0, attackPressed: false, jumpPressed: false, skillPressed: true, buffPressed: false);
        HeroSkillSlot castByCmd = job2WithBuffs.TryCastCommand(haCmdInput, heroBuffCmd);
        Assert.NotNull(castByCmd);
        Assert.Equal(SkillIds.HardAttack, castByCmd.SkillId);
        Assert.Equal(AvatarFixture.BuffAni, heroBuffCmd.CurrentAnimationPath);

        // 3) 三参 Create(data, chr, job) 给配方技能挂上的 skillLevelOf 委托取 initial[i + 1](精确边界)
        int defaultRecipeSkillId = SkillRecipes.Default.All.First().SkillId;
        var memFx = new Fixture();
        memFx.Tokens("skill/skilllist.lst", w => w.Int(0).Str("test.lst"));
        memFx.Tokens("skill/test.lst", w => w.Int(defaultRecipeSkillId).Str("test/r.skl"));
        memFx.Tokens("skill/test/r.skl", w => w
            .Tag("[type]").Str("[active]")
            .Tag("[command]").Str("SKILL")
            .Tag("[maximum level]").Int(10)
            .Tag("[static data]").Ints(100, 200, 300, 400)
            .Tag("[level info]").Ints(6, 10, 20, 30, 40, 50, 60));
        GameData memData = new GameData(memFx.Build());
        var memChr = new CharacterDef(0, "character/test/mini.chr", new[]
        {
            new ScriptLine("[skill]", new[]
            {
                new ScriptValue(ScriptValueKind.Int, defaultRecipeSkillId, null),
                new ScriptValue(ScriptValueKind.Int, 3, null),
            }),
        });
        HeroSkills initRecipeSkills = HeroSkills.Create(memData, memChr, EquipRules.SwordmanJob);
        HeroSkillSlot initRecipeSlot = Assert.Single(initRecipeSkills.Slots);
        Assert.Equal((defaultRecipeSkillId, 3), (initRecipeSlot.SkillId, initRecipeSlot.Level));
        Assert.NotNull(initRecipeSlot.PassiveSkillLevelOf);
        Assert.Equal(3, initRecipeSlot.PassiveSkillLevelOf(0));

        // 4) 首职业 HardAttack 落在 [etc motion] 下标 0 时仍可正常释放(击杀 etc >= 0 -> > 0 / >= 1)
        string tmpDir = Path.Combine(Path.GetTempPath(), "dnf-crossjob-etc0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmpDir, "ImagePacks2"));
        try
        {
            byte[] tinyAni = new AniBuilder().Frame(100).ToBytes();
            byte[] strTable = TestData.BuildStringTable(new[]
            {
                System.Text.Encoding.ASCII.GetBytes("[rest motion]"),
                System.Text.Encoding.ASCII.GetBytes("[move motion]"),
                System.Text.Encoding.ASCII.GetBytes("[waiting motion]"),
                System.Text.Encoding.ASCII.GetBytes("[etc motion]"),
                System.Text.Encoding.ASCII.GetBytes("animation/hardattack.ani"),
            });
            byte[] chrTokens = TestData.BuildTokenScript(new (byte, int)[]
            {
                (5, 0), (7, 4),
                (5, 1), (7, 4),
                (5, 2), (7, 4),
                (5, 3), (7, 4),
            });
            (byte[] pvfBytes, int _) = TestData.BuildPvf(new[]
            {
                new TestData.PvfFileSpec("stringtable.bin", strTable, 0),
                new TestData.PvfFileSpec("character/test/etc0.chr", chrTokens, 0),
                new TestData.PvfFileSpec("character/test/animation/hardattack.ani", tinyAni, 0),
            }, validChecksums: true);
            File.WriteAllBytes(Path.Combine(tmpDir, "Script.pvf"), pvfBytes);
            using DnfAssets etc0Assets = DnfAssets.Open(tmpDir);
            AvatarClass cls0 = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
            AvatarOutfit outfit0 = AvatarOutfit.CreateDefault(assets, cls0, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
            TownHero heroEtc0 = TownHero.Create(etc0Assets, "character/test/etc0.chr", outfit0, new List<WorldRect>(), HeroX, HeroY, false, TownHero.DungeonRestTag);
            Assert.Equal(0, heroEtc0.EtcMotionIndexOf(HeroSkills.HardAttackMotionFile));
            var haOnlyJob0 = new HeroSkills(new[] { new HeroSkillSlot(SkillKeys.A, SkillIds.HardAttack, 1, haCmdDef) }, null, EquipRules.SwordmanJob);
            Assert.Equal(SkillCastResult.Cast, haOnlyJob0.Cast(haOnlyJob0.SlotOf(SkillKeys.A), heroEtc0));
            Assert.Equal((HeroMotion.Skill, SkillIds.HardAttack), (heroEtc0.Motion, heroEtc0.SkillId));
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }

        // 5) 职业 2 登记 0x13 防御姿态: 进入 Guard 姿态并按职业作用域生效
        var job2Guard = new GuardRecipe(AvatarFixture.GuardSkill, AvatarFixture.GuardEtcIndex, 0, 0, 1, 2, 3);
        SkillRecipes scopedGuard = emptyRecipes.WithJobScoped(jobGuards: new[] { new JobGuardEntry(2, job2Guard) });
        HeroSkills job2WithGuard = LoadJobSkills(data, job: 2, AvatarFixture.GuardSkill, level: 2, scopedGuard, quickSlot: false);
        HeroSkillSlot guardSlot = Assert.Single(job2WithGuard.CommandSlots, s => s.SkillId == AvatarFixture.GuardSkill);
        Assert.Same(job2Guard, guardSlot.Guard);
        Assert.Equal((AvatarFixture.GuardMinHoldMs, AvatarFixture.GuardSpeed2, AvatarFixture.GuardRecoilMs2),
            (guardSlot.GuardMinHoldMs, guardSlot.GuardRecoilSpeed, guardSlot.GuardRecoilMs));
        TownHero heroGuard = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, job2WithGuard.Cast(guardSlot, heroGuard));
        Assert.Equal((HeroMotion.Skill, true, AvatarFixture.GuardSkill), (heroGuard.Motion, heroGuard.Guarding, heroGuard.SkillId));

        // 4) 职业 2 多属性 0x11 增益在 RoomCombatSession 里的完整周期(施法结束挂主增益、扣蓝、进冷却、到点失效)
        var multiBuff = new BuffRecipe(AvatarFixture.MagicBuffSkill, BuffStat.MagicalAttack, durationColumn: 0, amountStaticIndex: -1, amountColumn: 1)
            .WithExtraStats(new BuffStatEntry(BuffStatIds.CastSpeed, -1, 1, 0.25));
        SkillRecipes scopedMultiBuff = emptyRecipes.WithJobScoped(jobBuffs: new[] { new JobBuffEntry(2, multiBuff) });
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: d => LoadJobSkills(d, job: 2, AvatarFixture.MagicBuffSkill, level: 2, scopedMultiBuff, quickSlot: true));
        RoomCombatSession session = arena.Session;
        HeroSkillSlot magicSlot = session.Skills.SlotOf(SkillKeys.A);
        Assert.Equal(15.0, Assert.Single(magicSlot.BuffExtraAmounts).Value, 6);
        session.Update(10, 0, 0, false, false, SkillKeys.A);
        Assert.Equal((1, 0, HeroMotion.Skill), (session.BuffCasts, session.Buffs.Count, session.Hero.Motion));
        // 推完 [buff motion] 施法动作 -> 挂上增益
        for (int i = 0; i < 40 && session.Hero.Motion == HeroMotion.Skill; i++) session.Update(10, 0, 0, false, false, SkillKeys.None);
        Assert.Equal((1, (double)AvatarFixture.MagicBuffAmount2, (double)AvatarFixture.MagicBuffMs2),
            (session.Buffs.Count, session.Buffs.Total(BuffStat.MagicalAttack), session.Buffs.Find(AvatarFixture.MagicBuffSkill).DurationMs));
        // 走到持续时间到期 -> 失效
        session.Update(AvatarFixture.MagicBuffMs2 + 10, 0, 0, false, false, SkillKeys.None);
        Assert.Equal((0, 1), (session.Buffs.Count, session.Buffs.Expired));
    }

    // ------------------------------------------------------------------ 5. SkillRecipes.Default 阶段 2a 跨职业表引擎常量与既有表零回归

    [Fact]
    public void DefaultTablePreservesLegacyCountsAndRegistersAllStage2aCrossJobEngineConstants()
    {
        SkillRecipes def = SkillRecipes.Default;

        // 既有首职业表与基类表计数零回归
        Assert.Equal(3, def.BuffCount);
        Assert.Equal(1, def.ClassBuffCount);
        Assert.Equal(1, def.GuardCount);
        Assert.Equal(1, def.ThrowCount);

        // 阶段 2a 新增跨职业表计数
        Assert.Equal(8, def.JobBuffCount);
        Assert.Equal(1, def.JobGuardCount);
        Assert.Equal(2, def.JobThrowCount);
        Assert.Equal(8, def.AllJobBuffs.Count);
        Assert.Single(def.AllJobGuards);
        Assert.Equal(2, def.AllJobThrows.Count);

        // 逐条核对静态读码定案的引擎常量(不含真实技能名、职业名、路径或 PVF 数据值)
        BuffRecipe b0_38 = def.FindClassBuff(0, 38);
        BuffRecipe b9_38 = def.FindClassBuff(9, 38);
        Assert.NotNull(b0_38);
        Assert.Same(b0_38, b9_38);
        Assert.Equal((38, (BuffStat)BuffStatIds.EquipmentPhysicalAttack, 0, -1, 2, 1.0, 0),
            (b0_38.SkillId, b0_38.Stat, b0_38.DurationColumn, b0_38.AmountStaticIndex, b0_38.AmountColumn, b0_38.AmountScale, b0_38.GuardSkillId));
        BuffStatEntry b38Ex = Assert.Single(b0_38.ExtraStats);
        Assert.Equal((BuffStatIds.PhysicalCriticalHitRate, -1, 3, 0.1), (b38Ex.StatType, b38Ex.AmountStaticIndex, b38Ex.AmountColumn, b38Ex.AmountScale));

        BuffRecipe b9_3 = def.FindClassBuff(9, 3);
        Assert.NotNull(b9_3);
        Assert.Equal((3, BuffStat.AutoGuardChance, 1, -1, 0, 0.01, 1),
            (b9_3.SkillId, b9_3.Stat, b9_3.DurationColumn, b9_3.AmountStaticIndex, b9_3.AmountColumn, b9_3.AmountScale, b9_3.GuardSkillId));
        Assert.Empty(b9_3.ExtraStats);

        GuardRecipe g9_1 = def.FindGuard(9, 1);
        Assert.NotNull(g9_1);
        Assert.Same(g9_1, def.FindGuard(9, b9_3.GuardSkillId));
        Assert.Equal((1, 0, 0, 0, 1, 2, 3),
            (g9_1.SkillId, g9_1.MotionIndex, g9_1.MinHoldStaticIndex, g9_1.RecoilSpeedColumn, g9_1.RecoilMsColumn, g9_1.PhysicalColumn, g9_1.MagicalColumn));

        BuffRecipe b1_39 = def.FindClassBuff(1, 39);
        BuffRecipe b7_39 = def.FindClassBuff(7, 39);
        Assert.NotNull(b1_39);
        Assert.Same(b1_39, b7_39);
        Assert.Equal((39, (BuffStat)BuffStatIds.PhysicalCriticalHitRate, 1, -1, 0, 0.1, 0),
            (b1_39.SkillId, b1_39.Stat, b1_39.DurationColumn, b1_39.AmountStaticIndex, b1_39.AmountColumn, b1_39.AmountScale, b1_39.GuardSkillId));
        BuffStatEntry b39Ex = Assert.Single(b1_39.ExtraStats);
        Assert.Equal((BuffStatIds.GoodHit, -1, 2, -0.1), (b39Ex.StatType, b39Ex.AmountStaticIndex, b39Ex.AmountColumn, b39Ex.AmountScale));

        BuffRecipe b1_37 = def.FindClassBuff(1, 37);
        Assert.NotNull(b1_37);
        Assert.Null(def.FindClassBuff(7, 37));
        Assert.Equal((37, (BuffStat)BuffStatIds.PhysicalAttack, 0, -1, 1, 1.0, 0),
            (b1_37.SkillId, b1_37.Stat, b1_37.DurationColumn, b1_37.AmountStaticIndex, b1_37.AmountColumn, b1_37.AmountScale, b1_37.GuardSkillId));
        Assert.Equal(2, b1_37.ExtraStats.Count);
        Assert.Equal((BuffStatIds.AttackSpeed, 0, -1, -1.0),
            (b1_37.ExtraStats[0].StatType, b1_37.ExtraStats[0].AmountStaticIndex, b1_37.ExtraStats[0].AmountColumn, b1_37.ExtraStats[0].AmountScale));
        Assert.Equal((BuffStatIds.HitRecovery, 0, -1, -0.001),
            (b1_37.ExtraStats[1].StatType, b1_37.ExtraStats[1].AmountStaticIndex, b1_37.ExtraStats[1].AmountColumn, b1_37.ExtraStats[1].AmountScale));

        BuffRecipe b4_47 = def.FindClassBuff(4, 47);
        Assert.NotNull(b4_47);
        Assert.Equal((47, (BuffStat)BuffStatIds.PhysicalAttack, 0, -1, 1, 1.0, 0),
            (b4_47.SkillId, b4_47.Stat, b4_47.DurationColumn, b4_47.AmountStaticIndex, b4_47.AmountColumn, b4_47.AmountScale, b4_47.GuardSkillId));
        BuffStatEntry b47Ex = Assert.Single(b4_47.ExtraStats);
        Assert.Equal((BuffStatIds.GoodHit, -1, 2, -0.1), (b47Ex.StatType, b47Ex.AmountStaticIndex, b47Ex.AmountColumn, b47Ex.AmountScale));

        BuffRecipe b4_48 = def.FindClassBuff(4, 48);
        Assert.NotNull(b4_48);
        Assert.Equal((48, BuffStat.MagicalAttack, 0, -1, 1, 1.0, 0),
            (b4_48.SkillId, b4_48.Stat, b4_48.DurationColumn, b4_48.AmountStaticIndex, b4_48.AmountColumn, b4_48.AmountScale, b4_48.GuardSkillId));
        BuffStatEntry b48Ex = Assert.Single(b4_48.ExtraStats);
        Assert.Equal((BuffStatIds.CastSpeed, -1, 2, 1.0), (b48Ex.StatType, b48Ex.AmountStaticIndex, b48Ex.AmountColumn, b48Ex.AmountScale));

        ThrowRecipe t0_32 = def.FindThrow(0, 32);
        ThrowRecipe t9_32 = def.FindThrow(9, 32);
        Assert.NotNull(t0_32);
        Assert.Same(t0_32, t9_32);
        Assert.Equal((32, -1, 0, -1, true, true, 1),
            (t0_32.SkillId, t0_32.ChargeStaticIndex, t0_32.ShootStaticIndex, t0_32.ArmorStaticIndex,
             t0_32.UsesZeroChargeFallback, t0_32.AlwaysArmorInCharge, t0_32.PoseSelector));
        SkillObjectSpec obj32 = Assert.Single(t0_32.Objects);
        Assert.Equal((20018, 0, 0, 0, 0), (obj32.ObjectId, obj32.Stage, obj32.Frame, obj32.DamageRateColumn, obj32.DamageFixedColumn));

        // 全部登记项经由 Find* 与 IsImplemented 双向一致
        foreach (JobBuffEntry e in def.AllJobBuffs)
        {
            Assert.Same(e.Recipe, def.FindBuff(e.Job, e.Recipe.SkillId));
            Assert.Same(e.Recipe, def.FindClassBuff(e.Job, e.Recipe.SkillId));
            Assert.True(HeroSkills.IsImplemented(e.Job, e.Recipe.SkillId));
        }
        foreach (JobGuardEntry e in def.AllJobGuards)
        {
            Assert.Same(e.Recipe, def.FindGuard(e.Job, e.Recipe.SkillId));
            Assert.True(HeroSkills.IsImplemented(e.Job, e.Recipe.SkillId));
        }
        foreach (JobThrowEntry e in def.AllJobThrows)
        {
            Assert.Same(e.Recipe, def.FindThrow(e.Job, e.Recipe.SkillId));
            Assert.True(HeroSkills.IsImplemented(e.Job, e.Recipe.SkillId));
        }
    }
}
