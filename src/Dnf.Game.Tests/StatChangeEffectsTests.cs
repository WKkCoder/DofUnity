using Dnf.Game.Act;

namespace Dnf.Game.Tests;

/// <summary>
/// 属性改变的类型表与写进数值块的规则(M63,证据 E-161:客户端 0x0125aa00 的字典与 0x00984260 的分支)。数值全是合成的。
/// </summary>
public sealed class StatChangeEffectsTests
{
    [Theory]
    [InlineData("[physical attack]", 0)]
    [InlineData("[magical attack]", 1)]
    [InlineData("[magical defense]", 2)]
    [InlineData("[physical defense]", 3)]
    [InlineData("[equipment physical attack]", 4)]
    [InlineData("[equipment physical defense]", 5)]
    [InlineData("[physical attack bonus]", 6)]
    [InlineData("[magical attack bonus]", 7)]
    [InlineData("[jump power]", 8)]
    [InlineData("[attack speed]", 10)]
    [InlineData("[move speed]", 11)]
    [InlineData("[cast speed]", 12)]
    [InlineData("[hp max]", 13)]
    [InlineData("[hp regen rate]", 14)]
    [InlineData("[physical critical hit rate]", 15)]
    [InlineData("[magical critical hit rate]", 16)]
    [InlineData("[physical back attack critical rate]", 17)]
    [InlineData("[magical back attack critical rate]", 18)]
    [InlineData("[fire element]", 20)]
    [InlineData("[water element]", 21)]
    [InlineData("[dark element]", 22)]
    [InlineData("[light element]", 23)]
    [InlineData("[all element]", 24)]
    [InlineData("[all active status resistance]", 26)]
    [InlineData("[stuck]", 27)]
    [InlineData("[mp regen rate]", 29)]
    [InlineData("[hit recovery]", 30)]
    [InlineData("[equipment magical attack]", 31)]
    [InlineData("[equipment magical defense]", 32)]
    [InlineData("[good hit]", 33)]
    [InlineData("[fire element attack]", 42)]
    [InlineData("[water element attack]", 43)]
    [InlineData("[dark element attack]", 44)]
    [InlineData("[light element attack]", 45)]
    [InlineData("[all element attack]", 46)]
    [InlineData("[mp max]", 48)]
    [InlineData("[separate attack]", 53)]
    [InlineData("[antievil]", 55)]
    [InlineData("[physical absolute damage]", 56)]
    [InlineData("[magical absolute damage]", 60)]
    [InlineData("[physical absolute defense]", 61)]
    [InlineData("[magical absolute defense]", 62)]
    [InlineData("[village moving speed]", 63)]
    public void EveryStatTagMapsToTheClientTypeNumber(string tag, int type)
    {
        Assert.Equal(type, StatChangeTypes.FromTag(tag));
    }

    [Theory]
    [InlineData("[jump speed]")]
    [InlineData("[MOVE SPEED]")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownTagsMapToMinusOne(string tag)
    {
        Assert.Equal(StatChangeTypes.Unknown, StatChangeTypes.FromTag(tag));
        Assert.Equal(-1, StatChangeTypes.Unknown);
    }

    [Fact]
    public void OnlyAttackMoveAndCastSpeedAreSpeedTypes()
    {
        Assert.True(StatChangeTypes.IsSpeed(StatChangeTypes.AttackSpeed));
        Assert.True(StatChangeTypes.IsSpeed(StatChangeTypes.MoveSpeed));
        Assert.True(StatChangeTypes.IsSpeed(StatChangeTypes.CastSpeed));
        Assert.False(StatChangeTypes.IsSpeed(StatChangeTypes.VillageMoveSpeed));
        Assert.False(StatChangeTypes.IsSpeed(StatChangeTypes.JumpPower));
        Assert.False(StatChangeTypes.IsSpeed(StatChangeTypes.Unknown));
    }

    [Fact]
    public void TwoChangesAreTheSameOnlyWhenTypeFlagAndValueAllMatch()
    {
        var a = new StatChange(11, true, -30);
        Assert.True(a.Equals(new StatChange(11, true, -30)));
        Assert.False(a.Equals(new StatChange(12, true, -30)));
        Assert.False(a.Equals(new StatChange(11, false, -30)));
        Assert.False(a.Equals(new StatChange(11, true, 30)));
        Assert.Equal(a.GetHashCode(), new StatChange(11, true, -30).GetHashCode());
    }

    // ------------------------------------------------------------------ 成对的整数基数 + 浮点倍率

    [Theory]
    [InlineData(StatChangeTypes.PhysicalAttack, MonsterStats.IxPhysicalAttack)]
    [InlineData(StatChangeTypes.MagicalAttack, MonsterStats.IxMagicalAttack)]
    [InlineData(StatChangeTypes.PhysicalDefense, MonsterStats.IxPhysicalDefense)]
    [InlineData(StatChangeTypes.MagicalDefense, MonsterStats.IxMagicalDefense)]
    [InlineData(StatChangeTypes.EquipmentPhysicalAttack, MonsterStats.IxEquipPhysicalAttack)]
    [InlineData(StatChangeTypes.EquipmentPhysicalDefense, MonsterStats.IxEquipPhysicalDefense)]
    [InlineData(StatChangeTypes.EquipmentMagicalAttack, MonsterStats.IxEquipMagicalAttack)]
    [InlineData(StatChangeTypes.EquipmentMagicalDefense, MonsterStats.IxEquipMagicalDefense)]
    [InlineData(StatChangeTypes.HpMax, MonsterStats.IxHpBase)]
    [InlineData(StatChangeTypes.MpMax, MonsterStats.IxMpBase)]
    [InlineData(StatChangeTypes.HpRegenRate, MonsterStats.IxHpRegen)]
    [InlineData(StatChangeTypes.MpRegenRate, MonsterStats.IxMpRegen)]
    [InlineData(StatChangeTypes.JumpPower, MonsterStats.IxJumpPower)]
    public void PlainPairsTakeAFlatChangeInTheIntegerAndAPercentInTheNextFloat(int type, int baseIndex)
    {
        var flat = new MonsterStats();
        flat.Raw[baseIndex] = 100;
        float rateBefore = flat.F(baseIndex + 1);
        int[] others = (int[])flat.Raw.Clone();
        Assert.True(StatChangeEffects.Apply(flat, new StatChange(type, false, 25)));
        Assert.Equal(125, flat.Raw[baseIndex]);
        Assert.Equal(rateBefore, flat.F(baseIndex + 1));
        for (int i = 0; i < others.Length; i++)
        {
            if (i != baseIndex)
            {
                Assert.Equal(others[i], flat.Raw[i]);
            }
        }

        var pct = new MonsterStats();
        pct.Raw[baseIndex] = 100;
        Assert.True(StatChangeEffects.Apply(pct, new StatChange(type, true, 50)));
        Assert.Equal(100, pct.Raw[baseIndex]);
        Assert.Equal(rateBefore + 0.5f, pct.F(baseIndex + 1), 4);
    }

    [Fact]
    public void PercentChangesAddUpInTheRateInsteadOfMultiplying()
    {
        var stats = new MonsterStats();
        StatChangeEffects.Apply(stats, new StatChange(StatChangeTypes.PhysicalAttack, true, 50));
        StatChangeEffects.Apply(stats, new StatChange(StatChangeTypes.PhysicalAttack, true, 50));
        Assert.Equal(2.0f, stats.F(MonsterStats.IxPhysicalAttack + 1), 4);          // 1 + 0.5 + 0.5,不是 1.5 × 1.5
    }

    [Fact]
    public void ThePlainDefensePairIsClampedAtZeroButTheEquipmentPairIsNot()
    {
        var plain = new MonsterStats();
        plain.Raw[MonsterStats.IxPhysicalDefense] = 5;
        StatChangeEffects.Apply(plain, new StatChange(StatChangeTypes.PhysicalDefense, false, -50));
        Assert.Equal(0, plain.Raw[MonsterStats.IxPhysicalDefense]);
        StatChangeEffects.Apply(plain, new StatChange(StatChangeTypes.PhysicalDefense, true, -500));
        Assert.Equal(0.0f, plain.F(MonsterStats.IxPhysicalDefense + 1));              // 1 − 5 夹到 0

        var magic = new MonsterStats();
        StatChangeEffects.Apply(magic, new StatChange(StatChangeTypes.MagicalDefense, false, -3));
        Assert.Equal(0, magic.Raw[MonsterStats.IxMagicalDefense]);

        var equip = new MonsterStats();
        equip.Raw[MonsterStats.IxEquipPhysicalDefense] = 5;
        StatChangeEffects.Apply(equip, new StatChange(StatChangeTypes.EquipmentPhysicalDefense, false, -50));
        Assert.Equal(-45, equip.Raw[MonsterStats.IxEquipPhysicalDefense]);
        StatChangeEffects.Apply(equip, new StatChange(StatChangeTypes.EquipmentPhysicalDefense, true, -500));
        Assert.Equal(-4.0f, equip.F(MonsterStats.IxEquipPhysicalDefense + 1), 4);
    }

    // ------------------------------------------------------------------ 速度

    [Fact]
    public void SpeedChangesGoIntoTheRateAndLeaveTheIntegerBaseAlone()
    {
        var pct = new MonsterStats();
        int baseBefore = pct.Raw[MonsterStats.IxMoveSpeed];
        Assert.True(StatChangeEffects.Apply(pct, new StatChange(StatChangeTypes.MoveSpeed, true, -30)));
        Assert.Equal(0.7f, pct.F(MonsterStats.IxMoveSpeed + 1), 4);
        Assert.Equal(baseBefore, pct.Raw[MonsterStats.IxMoveSpeed]);

        var flat = new MonsterStats();
        Assert.True(StatChangeEffects.Apply(flat, new StatChange(StatChangeTypes.AttackSpeed, false, 500)));
        Assert.Equal(1.5f, flat.F(MonsterStats.IxAttackSpeed + 1), 4);               // 点数的单位是千分之一
        Assert.Equal(baseBefore, flat.Raw[MonsterStats.IxAttackSpeed]);

        var cast = new MonsterStats();
        Assert.True(StatChangeEffects.Apply(cast, new StatChange(StatChangeTypes.CastSpeed, true, 20)));
        Assert.Equal(1.2f, cast.F(MonsterStats.IxCastSpeed + 1), 4);
    }

    [Fact]
    public void SpeedDeltaUsesTheClientUnits()
    {
        Assert.Equal(-0.3f, StatChangeEffects.SpeedDelta(new StatChange(StatChangeTypes.MoveSpeed, true, -30)), 4);
        Assert.Equal(2.0f, StatChangeEffects.SpeedDelta(new StatChange(StatChangeTypes.MoveSpeed, false, 2000)), 4);
    }

    // ------------------------------------------------------------------ 元素

    [Fact]
    public void ElementToleranceAndAttackHitTheirOwnSlotAndTheAllTypesHitAllFour()
    {
        for (int k = 0; k < 4; k++)
        {
            var res = new MonsterStats();
            Assert.True(StatChangeEffects.Apply(res, new StatChange(StatChangeTypes.ElementToleranceFire + k, false, 10 + k)));
            Assert.Equal(10 + k, res.Raw[MonsterStats.IxElementResistance + k]);
            for (int j = 0; j < 4; j++)
            {
                if (j != k)
                {
                    Assert.Equal(0, res.Raw[MonsterStats.IxElementResistance + j]);
                }
            }

            var atk = new MonsterStats();
            Assert.True(StatChangeEffects.Apply(atk, new StatChange(StatChangeTypes.ElementAttackFire + k, true, 20)));
            Assert.Equal(1.2f, atk.F(MonsterStats.IxElementAttack + 4 + k), 4);
            Assert.Equal(1.0f, atk.F(MonsterStats.IxElementAttack + 4 + (k + 1) % 4), 4);
        }

        var all = new MonsterStats();
        Assert.True(StatChangeEffects.Apply(all, new StatChange(StatChangeTypes.ElementToleranceAll, false, 7)));
        Assert.Equal(new[] { 7, 7, 7, 7 }, new[] { all.Raw[MonsterStats.IxElementResistance], all.Raw[MonsterStats.IxElementResistance + 1], all.Raw[MonsterStats.IxElementResistance + 2], all.Raw[MonsterStats.IxElementResistance + 3] });

        var allAttack = new MonsterStats();
        Assert.True(StatChangeEffects.Apply(allAttack, new StatChange(StatChangeTypes.ElementAttackAll, true, 10)));
        for (int k = 0; k < 4; k++)
        {
            Assert.Equal(1.1f, allAttack.F(MonsterStats.IxElementAttack + 4 + k), 4);
        }
    }

    // ------------------------------------------------------------------ 特殊几项

    [Fact]
    public void StuckAddsFlatToTheIntegerAndPercentRelativeToTheCurrentRate()
    {
        var flat = new MonsterStats();
        int before = flat.Raw[MonsterStats.IxStuckResistance];
        Assert.True(StatChangeEffects.Apply(flat, new StatChange(StatChangeTypes.StuckResistance, false, 40)));
        Assert.Equal(before + 40, flat.Raw[MonsterStats.IxStuckResistance]);

        var pct = new MonsterStats();
        float r0 = pct.F(MonsterStats.IxStuckResistanceRate);
        Assert.True(StatChangeEffects.Apply(pct, new StatChange(StatChangeTypes.StuckResistance, true, 50)));
        float r1 = pct.F(MonsterStats.IxStuckResistanceRate);
        Assert.Equal(r0 + r0 * 0.5f, r1, 4);
        StatChangeEffects.Apply(pct, new StatChange(StatChangeTypes.StuckResistance, true, 50));
        Assert.Equal(r1 + r1 * 0.5f, pct.F(MonsterStats.IxStuckResistanceRate), 4);    // 每次都在现值上加,不是在 1.0 上加
    }

    [Fact]
    public void HitRecoveryAddsTheFlatNumberToTheFloatAndPercentRelativeToIt()
    {
        var flat = new MonsterStats();
        float before = flat.F(MonsterStats.IxHitStagger);
        Assert.True(StatChangeEffects.Apply(flat, new StatChange(StatChangeTypes.HitStagger, false, 3)));
        Assert.Equal(before + 3.0f, flat.F(MonsterStats.IxHitStagger), 4);

        var pct = new MonsterStats();
        Assert.True(StatChangeEffects.Apply(pct, new StatChange(StatChangeTypes.HitStagger, true, 20)));
        Assert.Equal(before + before * 0.2f, pct.F(MonsterStats.IxHitStagger), 4);
    }

    [Fact]
    public void AbsoluteDefenseWritesTheTwoFloatCellsTheDamageFillReadsAsPenetration()
    {
        var phys = new MonsterStats();
        float p0 = phys.F(MonsterStats.IxPhysicalAbsoluteDefense);
        Assert.True(StatChangeEffects.Apply(phys, new StatChange(StatChangeTypes.PhysicalAbsoluteDefense, false, 30)));
        Assert.Equal(p0 + 30.0f, phys.F(MonsterStats.IxPhysicalAbsoluteDefense), 4);
        Assert.Equal(0.0f, phys.F(MonsterStats.IxMagicalAbsoluteDefense));

        var magic = new MonsterStats();
        float m0 = magic.F(MonsterStats.IxMagicalAbsoluteDefense);
        Assert.True(StatChangeEffects.Apply(magic, new StatChange(StatChangeTypes.MagicalAbsoluteDefense, true, 20)));
        Assert.Equal(m0 + 0.2f, magic.F(MonsterStats.IxMagicalAbsoluteDefense), 4);
    }

    [Fact]
    public void AttackBonusOnlyAcceptsThePercentWriting()
    {
        var pct = new MonsterStats();
        Assert.True(StatChangeEffects.Apply(pct, new StatChange(StatChangeTypes.PhysicalAttackBonus, true, 15)));
        Assert.Equal(15, pct.Raw[MonsterStats.IxPhysicalAttackBonus]);
        Assert.True(StatChangeEffects.Apply(pct, new StatChange(StatChangeTypes.MagicalAttackBonus, true, 9)));
        Assert.Equal(9, pct.Raw[MonsterStats.IxMagicalAttackBonus]);

        var flat = new MonsterStats();
        int[] before = (int[])flat.Raw.Clone();
        Assert.False(StatChangeEffects.Apply(flat, new StatChange(StatChangeTypes.PhysicalAttackBonus, false, 15)));
        Assert.Equal(before, flat.Raw);
    }

    [Theory]
    [InlineData(StatChangeTypes.PhysicalCriticalHitRate)]
    [InlineData(StatChangeTypes.MagicalCriticalHitRate)]
    [InlineData(StatChangeTypes.PhysicalBackAttackCritical)]
    [InlineData(StatChangeTypes.MagicalBackAttackCritical)]
    [InlineData(StatChangeTypes.ActiveStatusToleranceAll)]
    [InlineData(StatChangeTypes.GoodHit)]
    [InlineData(StatChangeTypes.SeparateAttack)]
    [InlineData(StatChangeTypes.AntiEvil)]
    [InlineData(StatChangeTypes.PhysicalAbsoluteDamage)]
    [InlineData(StatChangeTypes.MagicalAbsoluteDamage)]
    [InlineData(StatChangeTypes.VillageMoveSpeed)]
    [InlineData(StatChangeTypes.Unknown)]
    public void TypesWithNoConsumerAreReportedAndLeaveTheBlockUntouched(int type)
    {
        var stats = new MonsterStats();
        int[] before = (int[])stats.Raw.Clone();
        Assert.False(StatChangeEffects.Apply(stats, new StatChange(type, true, 25)));
        Assert.False(StatChangeEffects.Apply(stats, new StatChange(type, false, 25)));
        Assert.Equal(before, stats.Raw);
    }

    [Theory]
    [InlineData("[move speed]", StatChangeTypes.MoveSpeed)]
    [InlineData("[hp max]", StatChangeTypes.HpMax)]
    [InlineData("[equipment physical defense]", StatChangeTypes.EquipmentPhysicalDefense)]
    [InlineData("[village moving speed]", StatChangeTypes.VillageMoveSpeed)]
    public void TagOfIsTheInverseOfFromTag(string tag, int type)
    {
        Assert.Equal(tag, StatChangeTypes.TagOf(type));
        Assert.Equal(type, StatChangeTypes.FromTag(StatChangeTypes.TagOf(type)));
    }

    [Fact]
    public void TagOfNamesTheNumberWhenNoTagHasIt()
    {
        Assert.Equal("#99", StatChangeTypes.TagOf(99));
        Assert.Equal("#-1", StatChangeTypes.TagOf(StatChangeTypes.Unknown));
    }

    [Fact]
    public void EveryTagInTheTableRoundTripsThroughItsTypeNumber()
    {
        int seen = 0;
        for (int type = 0; type < 64; type++)
        {
            string tag = StatChangeTypes.TagOf(type);
            if (tag[0] == '[')
            {
                Assert.Equal(type, StatChangeTypes.FromTag(tag));
                seen++;
            }
        }
        Assert.Equal(43, seen);                                         // 客户端字典 0x0125aa00 里的属性标签个数
    }

    [Fact]
    public void OnlyTheTypesTheMonsterSideReadsAreSaidToBeRead()
    {
        int[] reads =
        {
            StatChangeTypes.PhysicalAttack, StatChangeTypes.MagicalAttack, StatChangeTypes.EquipmentPhysicalAttack, StatChangeTypes.EquipmentMagicalAttack,
            StatChangeTypes.EquipmentPhysicalDefense, StatChangeTypes.EquipmentMagicalDefense, StatChangeTypes.HpMax, StatChangeTypes.AttackSpeed, StatChangeTypes.MoveSpeed,
            StatChangeTypes.ElementToleranceFire, StatChangeTypes.ElementToleranceWater, StatChangeTypes.ElementToleranceDark, StatChangeTypes.ElementToleranceLight,
            StatChangeTypes.ElementToleranceAll, StatChangeTypes.PhysicalAbsoluteDefense, StatChangeTypes.MagicalAbsoluteDefense,
        };
        for (int type = -1; type < 70; type++)
        {
            Assert.Equal(System.Array.IndexOf(reads, type) >= 0, StatChangeEffects.MonsterReads(type));
        }
        // 普通防御一对客户端的伤害公式不读(读装备防御一对),所以不算有读者
        Assert.False(StatChangeEffects.MonsterReads(StatChangeTypes.PhysicalDefense));
        Assert.False(StatChangeEffects.MonsterReads(StatChangeTypes.MagicalDefense));
    }

    [Fact]
    public void OnlyMoveAndAttackSpeedAreReadOnTheHero()
    {
        for (int type = -1; type < 70; type++)
        {
            Assert.Equal(type == StatChangeTypes.MoveSpeed || type == StatChangeTypes.AttackSpeed, StatChangeEffects.HeroReads(type));
        }
    }

    [Fact]
    public void ApplyRejectsANullBlock()
    {
        Assert.Throws<ArgumentNullException>(() => StatChangeEffects.Apply(null, new StatChange(0, false, 1)));
        Assert.Throws<ArgumentNullException>(() => new StatAppendageSet().Apply(null));
    }
}
