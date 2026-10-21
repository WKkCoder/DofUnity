using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 怪物 [attack kind] 记录与出招决策的规则(证据 E-100、E-101):12 个数一条记录、射程判断、抽签、抽签间隔。
/// 数值全是合成的,与原版数据无关;检验的是我们照客户端函数写下的规则(位置、比较方向、取整)。
/// </summary>
public class AttackKindRecordTests
{
    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static ScriptValue F(float value) => new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(value), null);

    /// <summary>正面 100、背面 20、两侧死区 0、纵向两个方向各 30、死区 0 的记录。</summary>
    private static AttackKindRecord Box(int front = 100, int back = 20, int leftDead = 0, int rightDead = 0, int yMinus = 30, int yPlus = 30,
        int yMinusDead = 0, int yPlusDead = 0)
    {
        return new AttackKindRecord(10, 5, 1, true, front, leftDead, back, rightDead, yMinus, yMinusDead, yPlus, yPlusDead);
    }

    [Fact]
    public void TwelveNumbersFormOneRecordInTheLoaderOrderAndATailThatDoesNotFillARecordIsDropped()
    {
        var tokens = new double[]
        {
            11, 22, 33, 1, 41, 42, 43, 44, 45, 46, 47, 48,
            0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8,
            9, 9, 9, 9, 9,
        };
        AttackKindRecord[] records = AttackKindRecord.Parse(tokens);
        Assert.Equal(2, records.Length);
        AttackKindRecord a = records[0];
        Assert.Equal((11f, 22f, 33f, true), (a.ChanceInRange, a.ChanceAnywhere, a.ChanceObject, a.InitiallyReady));
        // t4 正面、t5 左死区、t6 背面、t7 右死区、t8 −y 射程、t9 −y 死区、t10 +y 射程、t11 +y 死区
        Assert.Equal((41, 42, 43, 44), (a.FrontReach, a.LeftDead, a.BackReach, a.RightDead));
        Assert.Equal((45, 46, 47, 48), (a.YMinusReach, a.YMinusDead, a.YPlusReach, a.YPlusDead));
        Assert.Equal(0f, a.ChanceVsDown);                     // [attack kind] 里没有它:缺省 0
        Assert.False(records[1].InitiallyReady);
        Assert.Equal((1, 2, 3, 4), (records[1].FrontReach, records[1].LeftDead, records[1].BackReach, records[1].RightDead));
        Assert.Empty(AttackKindRecord.Parse(new double[11]));
        Assert.Empty(AttackKindRecord.Parse((IReadOnlyList<double>)null));
    }

    [Fact]
    public void TheTagReaderAcceptsIntegerAndFloatNumbersAndNoTagMeansNoRecords()
    {
        var values = new List<ScriptValue> { F(12.5f), I(3), F(0.25f), I(1) };
        for (int i = 4; i < 12; i++)
        {
            values.Add(I(i));
        }
        var tags = new TagTable(new[] { new ScriptLine("[attack kind]", values.ToArray()), new ScriptLine("[/attack kind]", System.Array.Empty<ScriptValue>()) });
        AttackKindRecord[] records = AttackKindRecord.Parse(tags);
        Assert.Single(records);
        Assert.Equal((12.5f, 3f, 0.25f, true), (records[0].ChanceInRange, records[0].ChanceAnywhere, records[0].ChanceObject, records[0].InitiallyReady));
        Assert.Equal(4, records[0].FrontReach);
        Assert.Equal(11, records[0].YPlusDead);

        Assert.Empty(AttackKindRecord.Parse(new TagTable(new[] { new ScriptLine("[name]", new[] { I(1) }) })));
        Assert.Empty(AttackKindRecord.Parse((TagTable)null));
    }

    [Fact]
    public void ARecordHasUsableExtentsOnlyWhenBothAxesHaveAReachAndOtherwiseIsNeverInRange()
    {
        Assert.True(Box().HasExtents);
        Assert.True(Box(front: 0, back: 5).HasExtents);                       // 正面与背面有一个就行
        Assert.True(Box(yMinus: 0, yPlus: 7).HasExtents);
        Assert.False(Box(front: 0, back: 0).HasExtents);
        Assert.False(Box(yMinus: 0, yPlus: 0).HasExtents);
        Assert.False(MonsterAttackRules.IsInRange(Box(front: 0, back: 0), 500, 300, true, 500, 300, 40, 20));
    }

    [Fact]
    public void FacingLeftTheFrontReachesTowardsMinusXAndTheBackTowardsPlusXWithTheTargetWidenedByAQuarterOfItsWidth()
    {
        AttackKindRecord r = Box(front: 100, back: 20);
        // 目标宽 40,四分之一是 10:正面边界 x = 500 − 100 − 10 = 390(含),背面边界 x = 500 + 20 + 10 = 530(含)
        Assert.True(MonsterAttackRules.IsInRange(r, 500, 300, true, 390, 300, 40, 20));
        Assert.False(MonsterAttackRules.IsInRange(r, 500, 300, true, 389, 300, 40, 20));
        Assert.True(MonsterAttackRules.IsInRange(r, 500, 300, true, 530, 300, 40, 20));
        Assert.False(MonsterAttackRules.IsInRange(r, 500, 300, true, 531, 300, 40, 20));
    }

    [Fact]
    public void FacingRightTheRangeIsTheMirrorImage()
    {
        AttackKindRecord r = Box(front: 100, back: 20);
        Assert.True(MonsterAttackRules.IsInRange(r, 500, 300, false, 610, 300, 40, 20));
        Assert.False(MonsterAttackRules.IsInRange(r, 500, 300, false, 611, 300, 40, 20));
        Assert.True(MonsterAttackRules.IsInRange(r, 500, 300, false, 470, 300, 40, 20));
        Assert.False(MonsterAttackRules.IsInRange(r, 500, 300, false, 469, 300, 40, 20));
    }

    [Fact]
    public void TheMarginPushesTheWholeWindowTowardsTheFacingSide()
    {
        AttackKindRecord r = Box(front: 100, back: 20);
        // 朝左、偏移 5:正面多出 5 像素(边界 385),背面少 5 像素(边界 525)
        Assert.True(MonsterAttackRules.IsInRange(r, 500, 300, true, 385, 300, 40, 20, margin: 5));
        Assert.False(MonsterAttackRules.IsInRange(r, 500, 300, true, 384, 300, 40, 20, margin: 5));
        Assert.True(MonsterAttackRules.IsInRange(r, 500, 300, true, 525, 300, 40, 20, margin: 5));
        Assert.False(MonsterAttackRules.IsInRange(r, 500, 300, true, 526, 300, 40, 20, margin: 5));
        // 朝右:镜像
        Assert.True(MonsterAttackRules.IsInRange(r, 500, 300, false, 615, 300, 40, 20, margin: 5));
        Assert.False(MonsterAttackRules.IsInRange(r, 500, 300, false, 616, 300, 40, 20, margin: 5));
        Assert.True(MonsterAttackRules.IsInRange(r, 500, 300, false, 475, 300, 40, 20, margin: 5));
        Assert.False(MonsterAttackRules.IsInRange(r, 500, 300, false, 474, 300, 40, 20, margin: 5));
    }

    [Fact]
    public void TheQuarterWidthTruncatesTowardsZero()
    {
        AttackKindRecord r = Box(front: 100, back: 0, yMinus: 30, yPlus: 30);
        // 宽 7:四分之一取 1(不是 2);朝左正面边界 x = 500 − 100 − 1 = 399
        Assert.True(MonsterAttackRules.IsInRange(r, 500, 300, true, 399, 300, 7, 20));
        Assert.False(MonsterAttackRules.IsInRange(r, 500, 300, true, 398, 300, 7, 20));
    }

    [Fact]
    public void TheLeftAndRightDeadZonesAreAbsoluteAndExcludeBothEnds()
    {
        AttackKindRecord r = Box(front: 100, back: 100, leftDead: 30, rightDead: 40);
        // 死区是开区间 (470, 540):不随朝向镜像
        foreach (bool left in new[] { true, false })
        {
            Assert.False(MonsterAttackRules.IsInRange(r, 500, 300, left, 471, 300, 40, 20));
            Assert.False(MonsterAttackRules.IsInRange(r, 500, 300, left, 539, 300, 40, 20));
            Assert.True(MonsterAttackRules.IsInRange(r, 500, 300, left, 470, 300, 40, 20));
            Assert.True(MonsterAttackRules.IsInRange(r, 500, 300, left, 540, 300, 40, 20));
        }
    }

    [Fact]
    public void TheVerticalReachIsWidenedByAQuarterOfTheTargetDepthAndHasItsOwnDeadZone()
    {
        AttackKindRecord r = Box(yMinus: 30, yPlus: 40);
        // 深 20,四分之一是 5:−y 一侧 y ≥ 300 − 30 − 5 = 265,+y 一侧 y ≤ 300 + 40 + 5 = 345
        Assert.True(MonsterAttackRules.IsInRange(r, 500, 300, true, 450, 265, 40, 20));
        Assert.False(MonsterAttackRules.IsInRange(r, 500, 300, true, 450, 264, 40, 20));
        Assert.True(MonsterAttackRules.IsInRange(r, 500, 300, true, 450, 345, 40, 20));
        Assert.False(MonsterAttackRules.IsInRange(r, 500, 300, true, 450, 346, 40, 20));

        AttackKindRecord dead = Box(yMinus: 30, yPlus: 30, yMinusDead: 10, yPlusDead: 12);
        Assert.False(MonsterAttackRules.IsInRange(dead, 500, 300, true, 450, 291, 40, 20));       // (290, 312) 里
        Assert.False(MonsterAttackRules.IsInRange(dead, 500, 300, true, 450, 311, 40, 20));
        Assert.True(MonsterAttackRules.IsInRange(dead, 500, 300, true, 450, 290, 40, 20));
        Assert.True(MonsterAttackRules.IsInRange(dead, 500, 300, true, 450, 312, 40, 20));
    }

    [Fact]
    public void ARollHitsWhenTheRandomNumberModuloTenThousandIsBelowThePercentTimesAHundred()
    {
        Assert.True(MonsterAttackRules.Roll(1499, 15f));
        Assert.False(MonsterAttackRules.Roll(1500, 15f));
        Assert.True(MonsterAttackRules.Roll(10000 + 5, 0.1f));                      // 先对 10000 取余
        Assert.False(MonsterAttackRules.Roll(10000 + 10, 0.1f));
        Assert.False(MonsterAttackRules.Roll(0, 0f));                               // 0% 永不中
        Assert.True(MonsterAttackRules.Roll(9999, 100f));                           // 100% 必中
        Assert.True(MonsterAttackRules.Roll(0, 1f));
    }

    [Fact]
    public void TheAttackVoiceRollHitsWhenTheRandomNumberModuloOneHundredIsBelowFifteen()
    {
        Assert.Equal(15, MonsterAttackRules.AttackVoicePercent);
        Assert.True(MonsterAttackRules.RollAttackVoice(14));
        Assert.False(MonsterAttackRules.RollAttackVoice(15));
        Assert.True(MonsterAttackRules.RollAttackVoice(100 + 3));                    // 先对 100 取余
        Assert.False(MonsterAttackRules.RollAttackVoice(100 + 99));
        Assert.False(MonsterAttackRules.RollAttackVoice(0, 0));                     // 0% 永不中
        Assert.True(MonsterAttackRules.RollAttackVoice(99, 100));                   // 100% 必中
        // 长期比例约 15%(固定种子,不是统计显著性检验)
        var random = new Random(7);
        int hits = 0;
        for (int i = 0; i < 20000; i++)
        {
            if (MonsterAttackRules.RollAttackVoice(random.Next()))
            {
                hits++;
            }
        }
        Assert.InRange(hits, 2700, 3300);
    }

    [Fact]
    public void TheNextDecisionIsOneDelayPlusARandomJitterOfTenPercentEitherSideAndNoJitterWhenTheSpanIsZero()
    {
        // 延迟 1000:范围 (int)(1000 × 0.2) = 200,抖动 = 随机 % 200 − 100
        Assert.Equal(5000 - 100 + 1000, MonsterAttackRules.NextDecisionMs(5000, 1000, 0));
        Assert.Equal(5000 + 99 + 1000, MonsterAttackRules.NextDecisionMs(5000, 1000, 199));
        Assert.Equal(5000 - 100 + 1000, MonsterAttackRules.NextDecisionMs(5000, 1000, 200));
        Assert.Equal(5000 + 0 + 1000, MonsterAttackRules.NextDecisionMs(5000, 1000, 100 + 200 * 7));
        // 延迟太短(范围取整为 0):不抖动,也不会除零
        Assert.Equal(5003, MonsterAttackRules.NextDecisionMs(5000, 3, 12345));
        Assert.Equal((3000, 1.3f, 0.2f, 10000), (MonsterAttackRules.SpawnLockoutMs, MonsterAttackRules.HostileChanceScale, MonsterAttackRules.JitterFraction, MonsterAttackRules.RollModulus));
    }
}
