using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 目标怪物血条的长度与填充(证据 E-106):总长 = (int)((float)最大生命 / (float)max(层数, 1) × (float)每层像素),
/// 填充用单精度整数插值,掉血后的缩短与白闪的透明度用同一个插值。数值全是合成的。
/// </summary>
public class HpGaugeTests
{
    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptValue F(float value) => new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(value), null);

    private static ScriptValue S(string text) => new ScriptValue(ScriptValueKind.String, 0, text);

    private static ScriptLine Line(string tag, params ScriptValue[] values) => new ScriptLine(tag, values);

    private static MonsterBaseRow Row(int level, int hpBase)
    {
        var cols = new int[MonsterBaseRow.Columns];
        cols[0] = level;
        cols[5] = hpBase;
        return new MonsterBaseRow(cols);
    }

    [Fact]
    public void ScaleIsTheFactorTimes160TruncatedInSinglePrecision()
    {
        Assert.Equal(160, HpGauge.Scale(1f));
        Assert.Equal((int)(0.8f * 160f), HpGauge.Scale(0.8f));
        Assert.Equal(40, HpGauge.Scale(0.25f));
        Assert.Equal(0, HpGauge.Scale(0f));
    }

    [Fact]
    public void LengthTruncatesAndTreatsAZeroLayerCountAsOne()
    {
        Assert.Equal(240, HpGauge.Length(60, 40, 160));          // 60 / 40 × 160
        Assert.Equal(373, HpGauge.Length(7, 3, 160));            // 2.3333 × 160 = 373.33 向零取整
        Assert.Equal(60 * 300, HpGauge.Length(60, 0, 300));      // 层数 0 当 1
        Assert.Equal(60 * 300, HpGauge.Length(60, 1, 300));
        Assert.Equal(0, HpGauge.Length(0, 50, 160));
    }

    [Fact]
    public void FillIsTheSinglePrecisionTruncatingInterpolation()
    {
        Assert.Equal(120, HpGauge.Fill(240, 30, 60));
        Assert.Equal(240, HpGauge.Fill(240, 60, 60));            // 满血取满长
        Assert.Equal(240, HpGauge.Fill(240, 75, 60));            // 超过上限也取满长
        Assert.Equal(0, HpGauge.Fill(240, 0, 60));
        Assert.Equal(0, HpGauge.Fill(100, 1, 1000));             // 还有血但不足一个像素:填充是 0(不补到 1)
        Assert.Equal(3, HpGauge.Fill(100, 1, 30));               // 3.33 向零取整
        Assert.Equal(0, HpGauge.Fill(240, -5, 60));
    }

    [Fact]
    public void LerpReturnsTheStartBelowOneAndTheEndAtOrAboveTheSpan()
    {
        Assert.Equal(255, HpGauge.Lerp(255, 0, 0, 500));
        Assert.Equal(255, HpGauge.Lerp(255, 0, -3, 500));
        Assert.Equal(127, HpGauge.Lerp(255, 0, 250, 500));       // 127.5 向零取整
        Assert.Equal(0, HpGauge.Lerp(255, 0, 500, 500));
        Assert.Equal(0, HpGauge.Lerp(255, 0, 900, 500));
        Assert.Equal(7, HpGauge.Lerp(7, 7, 3, 9));
    }

    [Fact]
    public void FlashAlphaFallsLinearlyFromOpaqueToClearOver500Ms()
    {
        Assert.Equal(255, HpGauge.FlashAlpha(0));
        Assert.Equal(127, HpGauge.FlashAlpha(250));
        Assert.Equal(0, HpGauge.FlashAlpha(499));                // 255 - 254.49 = 0.51 → 0
        Assert.Equal(0, HpGauge.FlashAlpha(500));
        Assert.Equal(500, HpGauge.FlashMs);
    }

    [Fact]
    public void ShrinkRunsAtOnePixelPerFiveMillisecondsCappedAtSixSeconds()
    {
        Assert.Equal(500, HpGauge.ShrinkSpanMs(100));
        Assert.Equal(6000, HpGauge.ShrinkSpanMs(1200));
        Assert.Equal(6000, HpGauge.ShrinkSpanMs(100000));
        Assert.Equal(0, HpGauge.ShrinkSpanMs(0));

        Assert.Equal(0, HpGauge.Shrunk(100, 0));
        Assert.Equal(0, HpGauge.Shrunk(100, -20));
        Assert.Equal(50, HpGauge.Shrunk(100, 250));
        Assert.Equal(20, HpGauge.Shrunk(100, 100));
        Assert.Equal(100, HpGauge.Shrunk(100, 500));
        Assert.Equal(100, HpGauge.Shrunk(100, 9000));
        Assert.Equal(0, HpGauge.Shrunk(0, 100));
    }

    [Fact]
    public void FactorsAreReadFromTheAdjustBlockAsIntegersOrFloats()
    {
        HpGaugeFactors f = HpGaugeFactors.Parse(new[]
        {
            Line("[normal]", I(9)),                              // 块外的同名标签不算
            Line("[adjust hp gauge]"),
            Line("[boss]", F(0.5f)),
            Line("[named]", F(0.25f)),
            Line("[normal]", I(2)),
            Line("[/adjust hp gauge]"),
            Line("[boss]", F(7f)),
        });
        Assert.Equal((2f, 0.25f, 0.5f), (f.Normal, f.Named, f.Boss));
    }

    [Fact]
    public void MissingFileOrEntriesDefaultToOne()
    {
        Assert.Same(HpGaugeFactors.Default, HpGaugeFactors.Parse(null));
        HpGaugeFactors none = HpGaugeFactors.Parse(new[] { Line("[something]", I(5)) });
        Assert.Equal((1f, 1f, 1f), (none.Normal, none.Named, none.Boss));
        HpGaugeFactors partial = HpGaugeFactors.Parse(new[] { Line("[adjust hp gauge]"), Line("[boss]", F(0.5f)), Line("[/adjust hp gauge]") });
        Assert.Equal((1f, 1f, 0.5f), (partial.Normal, partial.Named, partial.Boss));
        // 块没有关上:到文件尾都算块内
        HpGaugeFactors open = HpGaugeFactors.Parse(new[] { Line("[adjust hp gauge]"), Line("[named]", I(3)) });
        Assert.Equal(3f, open.Named);
    }

    [Fact]
    public void TheFactorFollowsTheMonsterKind()
    {
        var f = new HpGaugeFactors(1f, 2f, 3f);
        Assert.Equal(1f, f.For(MonsterKind.Normal));
        Assert.Equal(2f, f.For(MonsterKind.CommonChampion));
        Assert.Equal(2f, f.For(MonsterKind.SuperChampion));
        Assert.Equal(3f, f.For(MonsterKind.Boss));
        Assert.Equal(1f, f.For(MonsterKind.Other));
    }

    [Fact]
    public void LengthForUsesTheHpBaseOfTheLevelRowAndClampsTheRowIndex()
    {
        var table = new MonsterBaseTable(new[] { Row(1, 20), Row(2, 40), Row(3, 80) });
        var factors = new HpGaugeFactors(1f, 0.5f, 0.25f);
        // 等级 2:层数 40;普通系数 1 → 每层 160 像素 → 60 / 40 × 160 = 240
        Assert.Equal(240, HpGauge.LengthFor(MonsterKind.Normal, 60, 2, table, factors));
        // 精英系数 0.5 → 每层 80 像素 → 60 / 40 × 80 = 120;首领系数 0.25 → 每层 40 → 60
        Assert.Equal(120, HpGauge.LengthFor(MonsterKind.CommonChampion, 60, 2, table, factors));
        Assert.Equal(60, HpGauge.LengthFor(MonsterKind.Boss, 60, 2, table, factors));
        // 等级超过行数取最后一行,等级小于 1 取第一行(层数 80 → 60 / 80 × 160 = 120;层数 20 → 480)
        Assert.Equal(120, HpGauge.LengthFor(MonsterKind.Normal, 60, 9, table, factors));
        Assert.Equal(480, HpGauge.LengthFor(MonsterKind.Normal, 60, 0, table, factors));
        // 没有系数表时系数取 1
        Assert.Equal(240, HpGauge.LengthFor(MonsterKind.Boss, 60, 2, table, null));
    }

    [Fact]
    public void WithoutAnyTableRowTheDefaultScaleAndLayersApply()
    {
        Assert.Equal(180, HpGauge.LengthFor(MonsterKind.Normal, 60, 2, null, null));            // 60 / 100 × 300
        Assert.Equal(180, HpGauge.LengthFor(MonsterKind.Boss, 60, 2, new MonsterBaseTable(new MonsterBaseRow[0]), HpGaugeFactors.Default));
    }

    [Fact]
    public void AMonsterDefExposesItsFaceImageAndFrame()
    {
        var def = new MonsterDef(1, "monster/test/face.mob", new[]
        {
            Line("[name]", S("face")),
            Line("[face image]", S("Monster/Test/Faces.img"), I(7)),
        });
        Assert.Equal("Monster/Test/Faces.img", def.FaceImage);
        Assert.Equal(7, def.FaceIndex);

        var bare = new MonsterDef(2, "monster/test/bare.mob", new[] { Line("[name]", S("bare")) });
        Assert.Null(bare.FaceImage);
        Assert.Equal(-1, bare.FaceIndex);
    }
}
