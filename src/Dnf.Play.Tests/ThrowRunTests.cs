namespace Dnf.Play.Tests;

/// <summary>
/// 投掷类技能(客户端状态 0x0d 一族)的纯逻辑:阶段机 <see cref="ThrowRun"/>(两个阶段的计时、霸体、70 毫秒兜底、"永不"、施法速度折算)、
/// 配方 <see cref="ThrowRecipe"/> 的合法性与配方表的查找。时长、编号都是合成的小数,期望值全是手算(每条用例旁边写了算式)。
/// </summary>
public sealed class ThrowRunTests
{
    // ------------------------------------------------------------------ 阶段机

    [Fact]
    public void TheChargePhaseEndsExactlyWhenItsTimeIsReached()
    {
        var run = new ThrowRun(chargeRawMs: 100, shootRawMs: 200, armorInCharge: false);
        Assert.Equal((100, 200, 0), (run.ChargeMs, run.ShootMs, run.Phase));

        run.Advance(99);
        Assert.Equal((0, 99.0), (run.Phase, run.PhaseMs));       // 99 < 100:还在阶段 0
        run.Advance(1);
        Assert.Equal((1, 0.0), (run.Phase, run.PhaseMs));        // 99 + 1 = 100 ≥ 100:换阶段 1,计时清零
        Assert.False(run.Finished);
    }

    [Fact]
    public void TheShootPhaseEndsExactlyWhenItsTimeIsReachedAndTheRunStaysFinished()
    {
        var run = new ThrowRun(100, 200, false);
        run.Advance(100);
        run.Advance(199);
        Assert.Equal((1, 199.0, false), (run.Phase, run.PhaseMs, run.Finished));    // 阶段 1 走了 199 < 200
        run.Advance(1);
        Assert.Equal((2, true), (run.Phase, run.Finished));                         // 199 + 1 = 200 ≥ 200:结束

        run.Advance(5000);
        Assert.Equal((2, 0.0), (run.Phase, run.PhaseMs));                           // 结束之后不再动
    }

    [Fact]
    public void EachPhaseRestartsItsTimerSoLeftoverTimeIsNotCarriedOver()
    {
        var run = new ThrowRun(100, 200, false);
        run.Advance(250);                                         // 阶段 0 只需要 100,多出来的 150 不带进阶段 1
        Assert.Equal((1, 0.0), (run.Phase, run.PhaseMs));
        run.Advance(199);                                         // 若带进去:150 + 199 = 349 ≥ 200 就结束了;不带:199 < 200
        Assert.False(run.Finished);
        run.Advance(1);
        Assert.True(run.Finished);
    }

    [Fact]
    public void AtMostOnePhaseChangesPerStepEvenWhenTheStepCoversBothTimes()
    {
        var run = new ThrowRun(100, 50, false);
        run.Advance(1000);                                        // 一步覆盖两个阶段的总和,也只换一次(客户端每帧只判一次)
        Assert.Equal((1, 0.0, false), (run.Phase, run.PhaseMs, run.Finished));
        run.Advance(49);
        Assert.False(run.Finished);                               // 阶段 1 从 0 起重新数:49 < 50
        run.Advance(1);
        Assert.True(run.Finished);
    }

    [Fact]
    public void ANonPositiveOrNotANumberStepDoesNothing()
    {
        var run = new ThrowRun(100, 200, true);
        run.Advance(0);
        run.Advance(-30);
        run.Advance(double.NaN);
        Assert.Equal((0, 0.0), (run.Phase, run.PhaseMs));
        run.Advance(0.5);
        Assert.Equal(0.5, run.PhaseMs);                           // 对照:正数的小步照常累加
    }

    [Fact]
    public void AZeroChargeTimeFallsBackToSeventyMillisecondsButOtherSmallTimesDoNot()
    {
        var zero = new ThrowRun(0, 100, false);
        Assert.Equal(70, ThrowRun.ZeroChargeFallbackMs);
        Assert.Equal(70, zero.ChargeMs);
        zero.Advance(69);
        Assert.Equal(0, zero.Phase);                              // 70 毫秒兜底:69 还没到
        zero.Advance(1);
        Assert.Equal(1, zero.Phase);                              // 69 + 1 = 70

        var one = new ThrowRun(1, 100, false);
        Assert.Equal(1, one.ChargeMs);                            // 对照:1 毫秒不是 0,不兜底
        one.Advance(1);
        Assert.Equal(1, one.Phase);

        // 折算后才变成 0 也兜底:1 × 1000 / 2000 = 0(整数除法截断)
        Assert.Equal(70, new ThrowRun(1, 100, false, castSpeedPermille: 2000).ChargeMs);
        // 阶段 1 的 0 不兜底:一进阶段 1 下一步就结束
        var instant = new ThrowRun(10, 0, false);
        instant.Advance(10);
        Assert.Equal((1, 0), (instant.Phase, instant.ShootMs));
        instant.Advance(1);
        Assert.True(instant.Finished);
    }

    [Fact]
    public void ANegativeTimeMeansTheStateNeverChangesOnItsOwn()
    {
        var neverCharges = new ThrowRun(-1, 100, false);
        Assert.Equal(-1, neverCharges.ChargeMs);                  // 负数原样保留(客户端用 -1 表示"永不")
        neverCharges.Advance(1_000_000_000);
        Assert.Equal(0, neverCharges.Phase);

        var neverEnds = new ThrowRun(100, -1, false);
        Assert.Equal(-1, neverEnds.ShootMs);
        neverEnds.Advance(100);
        neverEnds.Advance(1_000_000_000);
        Assert.Equal((1, false), (neverEnds.Phase, neverEnds.Finished));

        // 即使施法速度不是基准,-1 也不变成别的数(1000 / 1250 向零取整是 0,不能让它变成 0)
        Assert.Equal((-1, -1), (new ThrowRun(-1, -1, false, 1250).ChargeMs, new ThrowRun(-1, -1, false, 1250).ShootMs));
    }

    [Fact]
    public void TheCastSpeedScalesBothTimesAsTimesThousandOverSpeedTruncated()
    {
        // 速度 1250:450 × 1000 / 1250 = 360(整除),700 × 1000 / 1250 = 560(整除)
        var fast = new ThrowRun(450, 700, false, castSpeedPermille: 1250);
        Assert.Equal((360, 560), (fast.ChargeMs, fast.ShootMs));

        // 速度 1300:450000 / 1300 = 346.15 → 346,700000 / 1300 = 538.46 → 538(向零取整,不四舍五入)
        var odd = new ThrowRun(450, 700, false, castSpeedPermille: 1300);
        Assert.Equal((346, 538), (odd.ChargeMs, odd.ShootMs));

        // 基准与不合法的速度(0、负数)都按 1000:时长原样
        Assert.Equal(1000, ThrowRun.DefaultCastSpeed);
        Assert.Equal((450, 700), (new ThrowRun(450, 700, false).ChargeMs, new ThrowRun(450, 700, false).ShootMs));
        foreach (int bad in new[] { 0, -5 })
        {
            var run = new ThrowRun(450, 700, false, bad);
            Assert.Equal((450, 700), (run.ChargeMs, run.ShootMs));
        }
    }

    [Fact]
    public void ArmorIsOnlyActiveInPhaseZeroOfAnArmoredSkill()
    {
        var armored = new ThrowRun(100, 100, armorInCharge: true);
        Assert.True(armored.ArmorInCharge);
        Assert.True(armored.ArmorActive);                         // 阶段 0
        armored.Advance(100);
        Assert.Equal(1, armored.Phase);
        Assert.False(armored.ArmorActive);                        // 阶段 1:关掉
        armored.Advance(100);
        Assert.True(armored.Finished);
        Assert.False(armored.ArmorActive);                        // 结束之后也没有

        var plain = new ThrowRun(100, 100, armorInCharge: false);
        Assert.False(plain.ArmorInCharge);
        Assert.False(plain.ArmorActive);                          // 没有霸体的技能阶段 0 里也没有
    }

    // ------------------------------------------------------------------ 配方

    private static SkillObjectSpec Object(int id = 9211, int rateColumn = 0, int offsetY = 0)
    {
        return new SkillObjectSpec(id, stage: 0, frame: 0, damageRateColumn: rateColumn, damageFixedColumn: rateColumn, offsetY: offsetY);
    }

    [Fact]
    public void ARecipeKeepsItsConstantsBuildsTheTwoPoseTagsAndCopiesTheObjectList()
    {
        var objects = new List<SkillObjectSpec> { Object(9211, 3, 4), Object(9212, 1, 0) };
        var recipe = new ThrowRecipe(901, chargeStaticIndex: 2, shootStaticIndex: 5, armorStaticIndex: 7, poseSelector: 2, objects: objects);
        Assert.Equal((901, 2, 5, 7, 2), (recipe.SkillId, recipe.ChargeStaticIndex, recipe.ShootStaticIndex, recipe.ArmorStaticIndex, recipe.PoseSelector));
        Assert.Equal("[throw motion 2-1]", recipe.ChargePoseTag);
        Assert.Equal("[throw motion 2-2]", recipe.ShootPoseTag);
        Assert.Equal(new[] { 9211, 9212 }, recipe.Objects.Select(o => o.ObjectId).ToArray());
        Assert.Equal(new[] { 3, 1 }, recipe.Objects.Select(o => o.DamageRateColumn).ToArray());
        Assert.Equal(4, recipe.Objects[0].OffsetY);

        objects.Add(Object(9213));                                // 建好之后改原来的列表,配方里的不变
        Assert.Equal(2, recipe.Objects.Count);

        // 选择号 1 的标签(对照,边界)与"没有霸体开关"(下标 -1)都合法
        var first = new ThrowRecipe(902, 0, 0, -1, 1, new[] { Object() });
        Assert.Equal(("[throw motion 1-1]", "[throw motion 1-2]", -1), (first.ChargePoseTag, first.ShootPoseTag, first.ArmorStaticIndex));
    }

    [Fact]
    public void ARecipeRejectsAnythingTheStateCouldNotRunAndAcceptsTheBoundaries()
    {
        SkillObjectSpec ok = Object();
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(0, 1, 3, 4, 1, new[] { ok }));            // 技能号必须为正
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(-1, 1, 3, 4, 1, new[] { ok }));
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, -1, 3, 4, 1, new[] { ok }));         // 两个时长下标都要给
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, 1, -1, 4, 1, new[] { ok }));
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, 1, 3, 4, 0, new[] { ok }));          // 选择号从 1 数
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, 1, 3, 4, 1, null));
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, 1, 3, 4, 1, new SkillObjectSpec[0]));   // 至少一个对象
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, 1, 3, 4, 1, new SkillObjectSpec[] { null }));

        // 对象规格:一律在阶段 0 进来那一刻放,百分比与固定值取同一列
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, 1, 3, 4, 1, new[] { Object(id: 0) }));
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, 1, 3, 4, 1, new[] { new SkillObjectSpec(9211, stage: 1, frame: 0, damageRateColumn: 0, damageFixedColumn: 0) }));
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, 1, 3, 4, 1, new[] { new SkillObjectSpec(9211, stage: 0, frame: 1, damageRateColumn: 0, damageFixedColumn: 0) }));
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, 1, 3, 4, 1, new[] { new SkillObjectSpec(9211, 0, 0, 0, 0, onLanding: true) }));
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, 1, 3, 4, 1, new[] { new SkillObjectSpec(9211, 0, 0, 0, 0, afterEffect: 0) }));
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, 1, 3, 4, 1, new[] { new SkillObjectSpec(9211, 0, 0, -1, -1) }));         // 没有百分比列
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, 1, 3, 4, 1, new[] { new SkillObjectSpec(9211, 0, 0, 0, -1) }));          // 没有固定值列(客户端两个数都有)
        Assert.ThrowsAny<ArgumentException>(() => new ThrowRecipe(901, 1, 3, 4, 1, new[] { new SkillObjectSpec(9211, 0, 0, 0, 1) }));           // 两列不是同一列

        // 边界:技能号 1、下标 0、选择号 1、没有霸体开关、百分比列与固定值列都是 0 都合法
        Assert.Equal(1, new ThrowRecipe(1, 0, 0, -1, 1, new[] { ok }).SkillId);
    }

    [Fact]
    public void ARecipeTableFindsThrowRecipesBySkillAndTheOlderConstructorsHaveNone()
    {
        var a = new ThrowRecipe(901, 1, 3, 4, 1, new[] { Object() });
        var b = new ThrowRecipe(902, 0, 1, -1, 2, new[] { Object(9212) });
        var table = new SkillRecipes(new SkillRecipe[0], null, null, null, new[] { a, b });
        Assert.Equal(2, table.ThrowCount);
        Assert.Same(a, table.FindThrow(901));
        Assert.Same(b, table.FindThrow(902));
        Assert.Null(table.FindThrow(903));                                          // 没登记的技能号
        Assert.Equal(new[] { 901, 902 }, table.AllThrows.Select(t => t.SkillId).OrderBy(i => i).ToArray());
        Assert.Null(table.Find(901));                                               // 投掷类不是配方技能,也不是增益与防御姿态类
        Assert.Null(table.FindBuff(901));
        Assert.Null(table.FindGuard(901));

        Assert.Equal(0, new SkillRecipes(new SkillRecipe[0]).ThrowCount);
        Assert.Null(new SkillRecipes(new SkillRecipe[0]).FindThrow(901));
        Assert.Equal(0, new SkillRecipes(new SkillRecipe[0], null, null).ThrowCount);
        Assert.Equal(0, new SkillRecipes(new SkillRecipe[0], null, null, null, null).ThrowCount);   // 投掷表给 null 也行
    }

    [Fact]
    public void TheDefaultTableHasOneThrowRecipeBuiltFromTheReadEngineConstants()
    {
        // 状态 0x0d 的一个技能(静态读码):阶段 0 时长取 [static data] 第 1 个数、阶段 1 取第 3 个、霸体开关取第 4 个,姿势选择号 1;
        // 三个对象都在阶段 0 进来时放:第二个在纵深方向偏 50,前两个取 [level info] 第 0 列、第三个取第 2 列(百分比与固定值同一列)。技能号与对象编号不在这里断言
        ThrowRecipe real = Assert.Single(SkillRecipes.Default.AllThrows);
        Assert.Equal(1, SkillRecipes.Default.ThrowCount);
        Assert.Same(real, SkillRecipes.Default.FindThrow(real.SkillId));
        Assert.True(HeroSkills.IsImplemented(real.SkillId));
        Assert.Null(SkillRecipes.Default.Find(real.SkillId));
        Assert.Null(SkillRecipes.Default.FindThrow(real.SkillId + 1000));

        Assert.Equal((1, 3, 4, 1), (real.ChargeStaticIndex, real.ShootStaticIndex, real.ArmorStaticIndex, real.PoseSelector));
        Assert.Equal(3, real.Objects.Count);
        Assert.Equal(new[] { 0, 0, 2 }, real.Objects.Select(o => o.DamageRateColumn).ToArray());
        Assert.Equal(new[] { 0, 0, 2 }, real.Objects.Select(o => o.DamageFixedColumn).ToArray());
        Assert.Equal(new[] { 0, 50, 0 }, real.Objects.Select(o => o.OffsetY).ToArray());
        Assert.All(real.Objects, o => Assert.Equal((0, 0, 0, 0), (o.ForwardX, o.OffsetZ, o.Stage, o.Frame)));
        Assert.Equal(3, real.Objects.Select(o => o.ObjectId).Distinct().Count());
    }
}
