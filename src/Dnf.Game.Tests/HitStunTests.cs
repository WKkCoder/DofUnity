using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 受击(DAMAGE)硬直与默认击退(证据 E-080,静态读码)。表与倍率的数值全是合成的,只检验公式的形状与取整方式,与原版数据无关。
/// </summary>
public class HitStunTests
{
    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptLine Line(string tag, params int[] values)
    {
        var v = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            v[i] = I(values[i]);
        }
        return new ScriptLine(tag, v);
    }

    private static TagTable Etc(params ScriptLine[] lines) => new TagTable(lines);

    // 合成的表:恢复为 0 时硬直 330,恢复到 1000 及以上时只剩 10
    private static readonly HitStunTable Table = new HitStunTable(10, 330, 1000);

    [Fact]
    public void TheThreeTagsAreReadAndTheVariantPicksOneOfTheTwoValues()
    {
        TagTable tags = Etc(
            Line("[hit recovery max]", 1000, 1500),
            Line("[hit delay time max]", 330, 440),
            Line("[hit delay time min]", 10, 20),
            Line("[stand up invincible time]", 5, 6));

        HitStunTable first = HitStunTable.FromEtcParameter(tags);
        Assert.Equal((10, 330, 1000), (first.DelayMin, first.DelayMax, first.RecoveryMax));

        HitStunTable second = HitStunTable.FromEtcParameter(tags, 1);
        Assert.Equal((20, 440, 1500), (second.DelayMin, second.DelayMax, second.RecoveryMax));

        // 只有一个值时,要第二个也得到它
        HitStunTable single = HitStunTable.FromEtcParameter(Etc(Line("[hit recovery max]", 7), Line("[hit delay time max]", 8), Line("[hit delay time min]", 9)), 1);
        Assert.Equal((9, 8, 7), (single.DelayMin, single.DelayMax, single.RecoveryMax));
    }

    [Fact]
    public void AMissingTagOrMissingTableGivesNull()
    {
        Assert.Null(HitStunTable.FromEtcParameter(null));
        Assert.Null(HitStunTable.FromEtcParameter(Etc(Line("[hit delay time max]", 1, 1), Line("[hit delay time min]", 0, 0))));
        Assert.Null(HitStunTable.FromEtcParameter(Etc(Line("[hit recovery max]", 1, 1), Line("[hit delay time min]", 0, 0))));
        Assert.Null(HitStunTable.FromEtcParameter(Etc(Line("[hit recovery max]", 1, 1), Line("[hit delay time max]", 1, 1))));
        Assert.Null(HitStunTable.FromEtcParameter(Etc(Line("[hit recovery max]"), Line("[hit delay time max]", 1, 1), Line("[hit delay time min]", 0, 0))));
    }

    [Fact]
    public void StunFallsLinearlyFromTheMaximumToTheMinimumAsRecoveryRises()
    {
        Assert.Equal(330, Table.StunMs(0, 0, 1.0f));
        Assert.Equal(170, Table.StunMs(500, 0, 1.0f));      // 10 + 320 × 500 / 1000
        Assert.Equal(10, Table.StunMs(1000, 0, 1.0f));
        Assert.Equal(10, Table.StunMs(5000, 0, 1.0f));      // 恢复再高也只降到下限
        // 攻击方所属者的同类属性从受击方的里减掉
        Assert.Equal(234, Table.StunMs(500, 200, 1.0f));    // R = 300:10 + 320 × 700 / 1000
        // 攻击方的比受击方的还高(R 为负)时硬直比"恢复为 0"还长,不封顶
        Assert.Equal(10 + 320 * 1500 / 1000, Table.StunMs(0, 500, 1.0f));
    }

    [Fact]
    public void TheRateMultipliesTheWholeSumAndTheResultTruncatesTowardZero()
    {
        Assert.Equal(85, Table.StunMs(500, 0, 0.5f));
        Assert.Equal(340, Table.StunMs(500, 0, 2.0f));
        Assert.Equal(0, Table.StunMs(500, 0, 0.0f));
        Assert.Equal(117, Table.StunMs(500, 200, 0.5f));    // 234 × 0.5 = 117
        Assert.Equal(2, new HitStunTable(1, 1, 100).StunMs(0, 0, 2.5f));   // 1 × 2.5 = 2.5 → 2
    }

    [Fact]
    public void AZeroOrOneRecoveryCapDoesNotDivideByZero()
    {
        var table = new HitStunTable(10, 330, 0);
        Assert.Equal(10, table.StunMs(0, 0, 1.0f));          // (330 − 10) × (0 − 0) / 1 = 0
        Assert.Equal(10, table.StunMs(50, 0, 1.0f));         // 负数夹到 0
        var one = new HitStunTable(10, 330, 1);
        Assert.Equal(330, one.StunMs(0, 0, 1.0f));
        Assert.Equal(10, one.StunMs(1, 0, 1.0f));
    }

    [Fact]
    public void TheRateKeepsTheClientsTwoTruncationsSoOddValuesLoseALittle()
    {
        // 倍率先变成百分数 int(100 × L),再 × 0.01 × 100 取整,最后 × 0.01(单精度):能精确表示的值原样回来
        Assert.Equal(1.0f, HitStun.RateFromScale(1.0f));
        Assert.Equal(0.5f, HitStun.RateFromScale(0.5f));
        Assert.Equal(0.25f, HitStun.RateFromScale(0.25f));
        Assert.Equal(2.0f, HitStun.RateFromScale(2.0f));
        // 0.86 与 1.2 在单精度里略小于它们的十进制值:百分数照 100 × L 向零取整后,第二次取整再丢掉 1
        Assert.Equal(0.85f, HitStun.RateFromScale(0.86f), 6);
        Assert.Equal(1.19f, HitStun.RateFromScale(1.2f), 6);
        Assert.Equal(0.29f, HitStun.RateFromScale(0.3f), 6);
    }

    [Fact]
    public void AnUnsetScaleMeansOneAndMagicalAttacksTakeSeventyPercent()
    {
        Assert.Equal(1.0f, HitStun.EffectiveScale(-1.0f, false));
        Assert.Equal(1.0f, HitStun.EffectiveScale(-0.99999994f, false));    // 相对误差 2⁻²³ 以内也算没写
        Assert.Equal(0.5f, HitStun.EffectiveScale(0.5f, false));
        Assert.Equal(0.0f, HitStun.EffectiveScale(0.0f, false));            // 0 不是"没写":硬直倍率就是 0
        Assert.Equal(1.0f * 0.7f, HitStun.EffectiveScale(-1.0f, true));
        Assert.Equal(0.5f * 0.7f, HitStun.EffectiveScale(0.5f, true));
    }

    [Fact]
    public void TheFileScaleWinsThenTheWeaponAndTheFlagMultipliesThem()
    {
        Assert.Equal(1.5f, HitStun.DelayScale(1.5f, 0.5f, false));          // 文件写了:用文件的
        Assert.Equal(0.5f, HitStun.DelayScale(-1.0f, 0.5f, false));         // 没写:取武器表
        Assert.Equal(0.75f, HitStun.DelayScale(1.5f, 0.5f, true));          // 标志:武器 × 文件
        Assert.Equal(-1.0f, HitStun.DelayScale(-1.0f, -1.0f, false));       // 都没有:仍是"没写"
        Assert.Equal(0.5f, HitStun.DelayScale(-1.0f, 0.5f, true));          // 文件没写时标志不起作用(不会变成负的)
        Assert.Equal(1.5f, HitStun.DelayScale(1.5f, -1.0f, true));          // 没有武器表时标志也不起作用
        // 一路算下来:没写、没有武器表 → 倍率 1
        Assert.Equal(1.0f, HitStun.Rate(-1.0f, -1.0f, false, false));
        Assert.Equal(0.5f, HitStun.Rate(-1.0f, 0.5f, false, false));
        Assert.Equal(2.0f, HitStun.Rate(2.0f, 0.5f, false, false));
        Assert.Equal(1.0f, HitStun.Rate(2.0f, 0.5f, true, false));
    }

    [Fact]
    public void TheDefaultKnockbackIsAboutThirtyPixelsInAboutOneHundredFiftyMilliseconds()
    {
        HitStunResult r = HitStun.Damage(Table, 500, 0, 1.0f, 0, 0, false);     // 硬直 170,比 150 长
        Assert.Equal(170, r.StunMs);
        Assert.Equal(400, r.KnockSpeed);
        Assert.Equal(-2666, r.KnockDecel);                                        // 400 × (−1000) / 150 向零取整
        Assert.InRange(r.KnockMs, 150.0, 150.1);
        Assert.InRange(r.KnockPixels, 30.0, 30.02);                                // v0 × 持续 / 2000
    }

    [Fact]
    public void AShortStunCutsTheKnockbackShort()
    {
        HitStunResult r = HitStun.Damage(Table, 1000, 0, 1.0f, 0, 0, false);    // 硬直 10
        Assert.Equal(10, r.StunMs);
        Assert.Equal(-40000, r.KnockDecel);
        Assert.Equal(10.0, r.KnockMs, 6);
        Assert.Equal(2.0, r.KnockPixels, 6);

        HitStunResult none = HitStun.Damage(new HitStunTable(0, 0, 100), 0, 0, 1.0f, 0, 0, false);   // 硬直 0:持续 0,没有减速度也没有位移
        Assert.Equal(0, none.StunMs);
        Assert.Equal(0, none.KnockDecel);
        Assert.Equal(0.0, none.KnockPixels);
        Assert.Equal(0.0, none.KnockMs);
    }

    [Fact]
    public void KnockBackTypesOneAndTwoUseTheirOwnSpeedAndAThreeHundredThirtyThreeStun()
    {
        HitStunResult strong = HitStun.Damage(Table, 0, 0, 0.5f, 77, 1, false);   // 类型 1:不看表与强制硬直
        Assert.Equal(166, strong.StunMs);                                          // int(0.5 × 333)
        Assert.Equal(800, strong.KnockSpeed);
        Assert.Equal(unchecked(800 * -1000) / 166, strong.KnockDecel);

        HitStunResult weak = HitStun.Damage(Table, 0, 0, 1.0f, 0, 2, false);
        Assert.Equal(333, weak.StunMs);
        Assert.Equal(400, weak.KnockSpeed);
        Assert.InRange(weak.KnockPixels, 66.0, 67.0);                              // 400 × 333 / 2000 = 66.6

        // 类型 −1:不后退(初速 0),持续 30 但没有位移
        HitStunResult still = HitStun.Damage(Table, 0, 0, 1.0f, 0, -1, false);
        Assert.Equal(330, still.StunMs);
        Assert.Equal(0, still.KnockSpeed);
        Assert.Equal(0.0, still.KnockPixels);
    }

    [Fact]
    public void ABurnHitDoesNotKnockBackAndAForcedStunSkipsTheFormulaAndTheCap()
    {
        HitStunResult burn = HitStun.Damage(Table, 500, 0, 1.0f, 0, 0, true);
        Assert.Equal(170, burn.StunMs);
        Assert.Equal(0, burn.KnockSpeed);
        Assert.Equal(0.0, burn.KnockPixels);

        // 强制硬直:直接用它,击退持续也不被它缩短(取最小只在走公式时做)
        HitStunResult forced = HitStun.Damage(Table, 500, 0, 1.0f, 40, 0, false);
        Assert.Equal(40, forced.StunMs);
        Assert.Equal(400, forced.KnockSpeed);
        Assert.Equal(-2666, forced.KnockDecel);

        // 受击方不能被击退(客户端 this+0x3568 为 0):初速与减速度都是 0
        HitStunResult fixedTarget = HitStun.Damage(Table, 500, 0, 1.0f, 0, 0, false, knockable: false);
        Assert.Equal((170, 0, 0), (fixedTarget.StunMs, fixedTarget.KnockSpeed, fixedTarget.KnockDecel));
    }

    [Fact]
    public void DamageNeedsATable()
    {
        Assert.Throws<ArgumentNullException>(() => HitStun.Damage(null, 0, 0, 1.0f, 0, 0, false));
    }
}
