using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 异常状态的共享公式(M43,证据 E-136):数值表的读法、等级插值、持续时间(位掩码、怪物减半、负数归零)、附加参数的个数与常量项、
/// 持续伤害(毒、病、灼伤、出血)的取整细节、同步与可摇脱的种类。
/// 数值全是合成的,与原版表无关;与原版机器码的逐位对拍见 <see cref="ActiveStatusGoldenTests"/>
/// (金标准文件 Golden/active_status_golden.txt 由 tools/py/active_status_oracle.py 生成,期望值是服务端函数在模拟器里的运行结果)。
/// </summary>
public sealed class ActiveStatusTests
{
    private const int Slow = 0, Freeze = 1, Poison = 2, Stun = 3, Curse = 4, Blind = 5, Lightning = 6, Stone = 7, Sleep = 8, Burn = 9,
        WeaponBreak = 10, Bleeding = 11, Haste = 12, Bless = 13, Element = 14, Confuse = 15, Hold = 16, ArmorBreak = 17;

    private static ScriptValue[] Nums(params double[] values)
    {
        var list = new ScriptValue[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            double v = values[i];
            list[i] = v == Math.Floor(v)
                ? new ScriptValue(ScriptValueKind.Int, (int)v, null)
                : new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits((float)v), null);
        }
        return list;
    }

    private static double[] Seq(int count, double start, double step)
    {
        var a = new double[count];
        for (int i = 0; i < count; i++)
        {
            a[i] = start + step * i;
        }
        return a;
    }

    /// <summary>每种状态的一级值与满级值取"种类号 + 基数"(满级值再加 100),附加参数每项一对;最高等级 11,所以等级 k 的插值进度是 (k − 1) / 10。</summary>
    private static ActiveStatusTable Table(int maxLevel = 11, float userToUser = 1f, float tower = 1f)
    {
        var power1 = new float[ActiveStatusTable.KindCount * 2];
        var power2 = new float[ActiveStatusTable.KindCount * 2];
        var etc = new float[ActiveStatusTable.KindCount * ActiveStatusTable.EtcSlots * 2];
        var seconds = new int[ActiveStatusTable.KindCount];
        for (int k = 0; k < ActiveStatusTable.KindCount; k++)
        {
            seconds[k] = 3 + k;
            power1[k * 2] = 1f + k;
            power1[k * 2 + 1] = 101f + k;
            power2[k * 2] = 2f + k;
            power2[k * 2 + 1] = 52f + k;
            for (int s = 0; s < ActiveStatusTable.EtcSlots; s++)
            {
                etc[(k * ActiveStatusTable.EtcSlots + s) * 2] = 10f * (s + 1);
                etc[(k * ActiveStatusTable.EtcSlots + s) * 2 + 1] = 10f * (s + 1) + 1000f;
            }
        }
        return ActiveStatusTable.Synthetic(maxLevel, seconds, power1, power2, etc, userToUser, tower);
    }

    // ---- 表的读法 ----

    [Fact]
    public void ParseReadsEveryTagCaseInsensitively()
    {
        var lines = new[]
        {
            new ScriptLine(null, Nums(9)),
            new ScriptLine("[active status max level]", Nums(20)),
            new ScriptLine("[/active status max level]", Array.Empty<ScriptValue>()),
            new ScriptLine("[ACTIVE STATUS DURATION]", Nums(Seq(18, 10, 10))),
            new ScriptLine("[Active Status Power 1]", Nums(Seq(36, 1, 1))),
            new ScriptLine("[active status power 2]", Nums(Seq(36, 0.5, 0.5))),
            new ScriptLine("[active status etc parameters]", Nums(Seq(144, 1000, 1))),
            new ScriptLine("[active status level rate for prob]", Nums(0.25)),
            new ScriptLine("[active status level rate for power]", Nums(0.5)),
            new ScriptLine("[active status level absolute rate]", Nums(0.75)),
            new ScriptLine("[lightning time per number]", Nums(400)),
            new ScriptLine("[active status user to user damage rate]", Nums(0.4)),
            new ScriptLine("[dead tower active status user to user damage rate]", Nums(0.2)),
            new ScriptLine("[something else]", Nums(1, 2, 3)),
        };
        ActiveStatusTable t = ActiveStatusTable.Parse(lines);
        Assert.Equal(20, t.MaxLevel);
        Assert.Equal(10, t.DurationSeconds(0));
        Assert.Equal(180, t.DurationSeconds(17));
        Assert.Equal(1f, t.Power1Start(0));
        Assert.Equal(2f, t.Power1End(0));
        Assert.Equal(35f, t.Power1Start(17));
        Assert.Equal(36f, t.Power1End(17));
        Assert.Equal(0.5f, t.Power2Start(0));
        Assert.Equal(1f, t.Power2End(0));
        Assert.Equal(17.5f, t.Power2Start(17));
        Assert.Equal(1000f, t.EtcStart(0, 0));
        Assert.Equal(1001f, t.EtcEnd(0, 0));
        Assert.Equal(1002f, t.EtcStart(0, 1));
        Assert.Equal(1008f, t.EtcStart(1, 0));                      // 每种状态四项、每项一对,共 8 个数
        Assert.Equal(1000f + 143, t.EtcEnd(17, 3));
        Assert.Equal(0.25f, t.LevelRateForProb);
        Assert.Equal(0.5f, t.LevelRateForPower);
        Assert.Equal(0.75f, t.LevelAbsoluteRate);
        Assert.Equal(400, t.LightningTimePerNumber);
        Assert.Equal(0.4f, t.UserToUserDamageRate);
        Assert.Equal(0.2f, t.DeathTowerUserToUserDamageRate);
    }

    [Fact]
    public void MissingAndShortTagsLeaveZeros()
    {
        Assert.Same(ActiveStatusTable.Empty, ActiveStatusTable.Parse(null));
        Assert.Equal(0, ActiveStatusTable.Empty.MaxLevel);
        Assert.Equal(0f, ActiveStatusTable.Empty.Power1End(3));
        Assert.Equal(0f, ActiveStatusTable.Empty.UserToUserDamageRate);

        var lines = new[]
        {
            new ScriptLine("[active status duration]", Nums(5, 6)),
            new ScriptLine("[active status power 1]", Nums(1.5, 2.5, 3.5)),
        };
        ActiveStatusTable t = ActiveStatusTable.Parse(lines);
        Assert.Equal(5, t.DurationSeconds(0));
        Assert.Equal(6, t.DurationSeconds(1));
        Assert.Equal(0, t.DurationSeconds(2));
        Assert.Equal(3.5f, t.Power1Start(1));
        Assert.Equal(0f, t.Power1End(1));
        Assert.Equal(0f, t.Power1Start(2));
        Assert.Equal(0f, t.EtcStart(0, 0));
    }

    [Fact]
    public void OutOfRangeLookupsAreZero()
    {
        ActiveStatusTable t = Table();
        Assert.Equal(0, t.DurationSeconds(-1));
        Assert.Equal(0, t.DurationSeconds(18));
        Assert.Equal(0f, t.Power1Start(18));
        Assert.Equal(0f, t.Power2End(-1));
        Assert.Equal(0f, t.EtcStart(0, 4));
        Assert.Equal(0f, t.EtcEnd(0, -1));
        Assert.Equal(0f, t.EtcEnd(18, 0));
    }

    // ---- 等级插值 ----

    [Fact]
    public void PowerInterpolatesBetweenLevelOneAndMaxLevel()
    {
        ActiveStatusTable t = Table();                               // 一级 4(种类 3),满级 104,最高等级 11:每升一级加 10
        Assert.Equal(4f, ActiveStatusFormulas.Power1(t, Stun, 1));
        Assert.Equal(14f, ActiveStatusFormulas.Power1(t, Stun, 2));
        Assert.Equal(54f, ActiveStatusFormulas.Power1(t, Stun, 6));
        Assert.Equal(104f, ActiveStatusFormulas.Power1(t, Stun, 11));
        Assert.Equal(104f, ActiveStatusFormulas.Power1(t, Stun, 99));   // 超过最高等级取满级值
        Assert.Equal(4f, ActiveStatusFormulas.Power1(t, Stun, 0));      // 低于一级取一级值
        Assert.Equal(4f, ActiveStatusFormulas.Power1(t, Stun, -5));
        Assert.Equal(30f, ActiveStatusFormulas.Power2(t, Stun, 6));      // 威力 2:一级 5、满级 55,等级 6 走到一半
    }

    [Fact]
    public void NoMaxLevelMeansTheTopValue()
    {
        ActiveStatusTable t = Table(maxLevel: 0);                    // 没读到最高等级:进度的分母是 -1,任何等级都"不小于它"
        Assert.Equal(104f, ActiveStatusFormulas.Power1(t, Stun, 1));
        Assert.Equal(104f, ActiveStatusFormulas.Power1(t, Stun, 50));
    }

    [Fact]
    public void EtcParametersInterpolateEachSlotOnItsOwn()
    {
        ActiveStatusTable t = Table();
        Assert.Equal(10f, ActiveStatusFormulas.EtcParameter(t, Curse, 1, 0));
        Assert.Equal(510f, ActiveStatusFormulas.EtcParameter(t, Curse, 6, 0));
        Assert.Equal(20f + 500f, ActiveStatusFormulas.EtcParameter(t, Curse, 6, 1));
        Assert.Equal(1040f, ActiveStatusFormulas.EtcParameter(t, Curse, 11, 3));
    }

    [Fact]
    public void UniformVelocityKeepsTheOriginalOrderOfOperations()
    {
        Assert.Equal(7f, ActiveStatusFormulas.UniformVelocitySingle(7f, 9f, 0, 10));          // i 小于 1:a
        Assert.Equal(9f, ActiveStatusFormulas.UniformVelocitySingle(7f, 9f, 10, 10));         // i 不小于 n:b
        Assert.Equal(9f, ActiveStatusFormulas.UniformVelocitySingle(7f, 9f, 12, 10));
        Assert.Equal(8f, ActiveStatusFormulas.UniformVelocitySingle(7f, 9f, 5, 10));
        Assert.Equal(7.5f, ActiveStatusFormulas.UniformVelocitySingle(7f, 9f, 3, 12));
        Assert.Equal(2f, ActiveStatusFormulas.UniformVelocitySingle(1f, 2f, 1, 0));           // n 为 0:先比 i < n,不成立就是 b,不做除法
        Assert.Equal(1f, ActiveStatusFormulas.UniformVelocitySingle(1f, 2f, -1, 0));          // -1 < 0 成立、再 i < 1 成立:a,同样不做除法
    }

    // ---- 持续时间 ----

    [Fact]
    public void DurationComesFromThePowerFromTheTableOrIsZero()
    {
        ActiveStatusTable t = Table();
        // 威力型(冰冻、眩晕、失明、石化、睡眠、武器破坏、混乱、束缚、防具破坏):1000 × 威力 1
        Assert.Equal(2000, ActiveStatusFormulas.BaseDurationMs(t, Freeze, 1));        // 威力 1 的一级值 2
        Assert.Equal(52000, ActiveStatusFormulas.BaseDurationMs(t, Freeze, 6));
        Assert.Equal(102000, ActiveStatusFormulas.BaseDurationMs(t, Freeze, 11));
        // 表型(减速、中毒、诅咒、感电、灼伤、出血、加速、祝福):默认秒数 × 1000,与等级无关
        Assert.Equal(3000, ActiveStatusFormulas.BaseDurationMs(t, Slow, 1));
        Assert.Equal(3000, ActiveStatusFormulas.BaseDurationMs(t, Slow, 11));
        Assert.Equal(5000, ActiveStatusFormulas.BaseDurationMs(t, Poison, 4));
        // 元素与越界:0
        Assert.Equal(0, ActiveStatusFormulas.BaseDurationMs(t, Element, 5));
        Assert.Equal(0, ActiveStatusFormulas.BaseDurationMs(t, 18, 5));
        Assert.Equal(0, ActiveStatusFormulas.BaseDurationMs(t, -1, 5));
        Assert.Equal(0, ActiveStatusFormulas.BaseDurationMs(t, 99, 5));
    }

    [Fact]
    public void TheTwoDurationMasksAndTheRestPartitionTheKinds()
    {
        int power = ActiveStatusFormulas.DurationFromPowerMask, table = ActiveStatusFormulas.DurationFromTableMask;
        Assert.Equal(0, power & table);
        var neither = new List<int>();
        for (int k = 0; k < ActiveStatusTable.KindCount; k++)
        {
            if (((power | table) >> k & 1) == 0)
            {
                neither.Add(k);
            }
        }
        Assert.Equal(new[] { Element }, neither);
        foreach (int k in new[] { Freeze, Stun, Blind, Stone, Sleep, WeaponBreak, Confuse, Hold, ArmorBreak })
        {
            Assert.True((power >> k & 1) == 1, "威力型 " + k);
        }
        foreach (int k in new[] { Slow, Poison, Curse, Lightning, Burn, Bleeding, Haste, Bless })
        {
            Assert.True((table >> k & 1) == 1, "表型 " + k);
        }
    }

    [Fact]
    public void MonsterHalvingNeedsEveryBitOfTheMonsterObjectType()
    {
        ActiveStatusTable t = Table();                               // 中毒默认 5 秒
        const int Mob = ActiveStatusFormulas.MonsterObjectType;
        Assert.Equal(5000, ActiveStatusFormulas.DurationMs(t, Poison, 1, 0, 1));
        Assert.Equal(2500, ActiveStatusFormulas.DurationMs(t, Poison, 1, Mob, 1));
        Assert.Equal(2500, ActiveStatusFormulas.DurationMs(t, Poison, 1, Mob | 0x4000, 1));  // 位多出来不要紧
        Assert.Equal(5000, ActiveStatusFormulas.DurationMs(t, Poison, 1, 0x201, 1));         // 缺一位就不算
        Assert.Equal(5000, ActiveStatusFormulas.DurationMs(t, Poison, 1, 0x011, 1));
        Assert.Equal(5000, ActiveStatusFormulas.DurationMs(t, Poison, 1, 0x210, 1));
        Assert.Equal(2500, ActiveStatusFormulas.DurationMs(t, Poison, 1, Mob, 7));           // 第 6 个参数只要不是 0,减的都是一半
        Assert.Equal(2500, ActiveStatusFormulas.DurationMs(t, Poison, 1, Mob, -3));
    }

    [Fact]
    public void HalvingTruncatesTowardsZero()
    {
        // 威力 1 取 0.0035:3.5 毫秒落成整数 3;减去 trunc(3 × 0.5) = 1 得 2
        ActiveStatusTable t = ActiveStatusTable.Synthetic(11, new int[18], new float[] { 0f, 0f, 0.0035f, 0.0035f }, null);
        Assert.Equal(3, ActiveStatusFormulas.DurationMs(t, Freeze, 1, 0, 1));
        Assert.Equal(2, ActiveStatusFormulas.DurationMs(t, Freeze, 1, ActiveStatusFormulas.MonsterObjectType, 1));
    }

    [Fact]
    public void ZeroRatioWipesOutTheMonsterDuration()
    {
        // 第 6 个参数为 0:机器码里的除法给出非数,取整成"整数不确定值",非负的持续时间回绕成负数,最后归零
        ActiveStatusTable t = Table();
        Assert.Equal(0, ActiveStatusFormulas.DurationMs(t, Poison, 1, ActiveStatusFormulas.MonsterObjectType, 0));
        Assert.Equal(5000, ActiveStatusFormulas.DurationMs(t, Poison, 1, 0, 0));                // 不是怪物就不碰这一项
    }

    [Fact]
    public void NegativeDurationsBecomeZero()
    {
        ActiveStatusTable t = ActiveStatusTable.Synthetic(11, new int[18], new float[] { 0f, 0f, -2f, -2f }, null);
        Assert.Equal(0, ActiveStatusFormulas.DurationMs(t, Freeze, 1, 0, 1));
        Assert.Equal(0, ActiveStatusFormulas.DurationMs(t, Freeze, 1, ActiveStatusFormulas.MonsterObjectType, 1));
        var seconds = new int[18];
        seconds[Slow] = -4;
        t = ActiveStatusTable.Synthetic(11, seconds, null, null, null);
        Assert.Equal(0, ActiveStatusFormulas.DurationMs(t, Slow, 1, 0, 1));
    }

    // ---- 附加参数 ----

    [Fact]
    public void EtcParameterCountsFollowTheStatusKind()
    {
        ActiveStatusTable t = Table();
        var expected = new Dictionary<int, int>
        {
            [Slow] = 2, [Freeze] = 0, [Poison] = 1, [Stun] = 0, [Curse] = 4, [Blind] = 2, [Lightning] = 1, [Stone] = 1, [Sleep] = 0,
            [Burn] = 4, [WeaponBreak] = 1, [Bleeding] = 1, [Haste] = 2, [Bless] = 4, [Element] = 0, [Confuse] = 1, [Hold] = 0, [ArmorBreak] = 1,
        };
        foreach (KeyValuePair<int, int> pair in expected)
        {
            Assert.Equal(pair.Value, ActiveStatusFormulas.MakeEtcParameters(t, pair.Key, 3).Length);
        }
        Assert.Empty(ActiveStatusFormulas.MakeEtcParameters(t, 18, 3));
        Assert.Empty(ActiveStatusFormulas.MakeEtcParameters(t, -1, 3));
    }

    [Fact]
    public void EtcParametersAreTheInterpolatedSlotsTruncated()
    {
        ActiveStatusTable t = Table();
        int[] curse = ActiveStatusFormulas.MakeEtcParameters(t, Curse, 6);
        Assert.Equal(new[] { 510, 520, 530, 540 }, curse);
        int[] poison = ActiveStatusFormulas.MakeEtcParameters(t, Poison, 1);
        Assert.Equal(new[] { 10 }, poison);
        // 向零取整:一级 0.9、满级 2.9,等级 6 是 1.9 → 1
        ActiveStatusTable frac = ActiveStatusTable.Synthetic(11, null, null, null, new float[] { 0.9f, 2.9f });
        Assert.Equal(new[] { 1, 2 }, new[] { ActiveStatusFormulas.MakeEtcParameters(frac, Slow, 6)[0], ActiveStatusFormulas.MakeEtcParameters(frac, Slow, 11)[0] });
    }

    [Fact]
    public void BurnAndConfuseCarryConstantSlots()
    {
        ActiveStatusTable t = Table();
        int[] burn = ActiveStatusFormulas.MakeEtcParameters(t, Burn, 11);
        Assert.Equal(new[] { 1010, 1020, 1, 3000 }, burn);           // 前两项按等级插值,后两项是写死的 1 与 3000
        Assert.Equal(new[] { 10, 20, 1, 3000 }, ActiveStatusFormulas.MakeEtcParameters(t, Burn, 1));
        Assert.Equal(new[] { 1 }, ActiveStatusFormulas.MakeEtcParameters(t, Confuse, 1));
        Assert.Equal(new[] { 1 }, ActiveStatusFormulas.MakeEtcParameters(t, Confuse, 11));
    }

    // ---- 同步、伤害下标、摇脱 ----

    [Fact]
    public void SyncKindsAreThePersistentDamageOnes()
    {
        var sync = new List<int>();
        for (int k = -2; k < 40; k++)
        {
            if (ActiveStatusFormulas.IsSync(k))
            {
                sync.Add(k);
            }
        }
        Assert.Equal(new[] { Poison, Lightning, Stone, Burn, Bleeding }, sync);
        Assert.Equal(0, ActiveStatusFormulas.DamageIndex(Poison));
        Assert.Equal(1, ActiveStatusFormulas.DamageIndex(Lightning));
        Assert.Equal(2, ActiveStatusFormulas.DamageIndex(Stone));
        Assert.Equal(3, ActiveStatusFormulas.DamageIndex(Burn));
        Assert.Equal(4, ActiveStatusFormulas.DamageIndex(Bleeding));
        for (int k = -2; k < 40; k++)
        {
            Assert.Equal(ActiveStatusFormulas.IsSync(k), ActiveStatusFormulas.DamageIndex(k) >= 0);
        }
    }

    [Fact]
    public void OnlyFreezeStunAndStoneCanBeShakenOff()
    {
        var shakable = new List<int>();
        for (int k = -1; k < 20; k++)
        {
            if (ActiveStatusFormulas.IsShakable(k))
            {
                shakable.Add(k);
            }
        }
        Assert.Equal(new[] { Freeze, Stun, Stone }, shakable);
    }

    // ---- 持续伤害 ----

    [Fact]
    public void BleedingSpreadsTheTotalOverHalfSecondTicks()
    {
        Assert.Equal(100, ActiveStatusFormulas.BleedingDamagePerTick(new ActiveStatusDamageInput(1000, 5000)));    // 10 跳
        Assert.Equal(250, ActiveStatusFormulas.BleedingDamagePerTick(new ActiveStatusDamageInput(1000, 2000)));    // 4 跳
        Assert.Equal(1000, ActiveStatusFormulas.BleedingDamagePerTick(new ActiveStatusDamageInput(1000, 499)));    // 不足一跳按一跳
        Assert.Equal(1000, ActiveStatusFormulas.BleedingDamagePerTick(new ActiveStatusDamageInput(1000, 0)));
        Assert.Equal(1000, ActiveStatusFormulas.BleedingDamagePerTick(new ActiveStatusDamageInput(1000, -900)));
        Assert.Equal(2, ActiveStatusFormulas.BleedingDamagePerTick(new ActiveStatusDamageInput(10, 2000)));        // 2.5 向零取整
        Assert.Equal(-2, ActiveStatusFormulas.BleedingDamagePerTick(new ActiveStatusDamageInput(-10, 2000)));
        Assert.Equal(500, ActiveStatusFormulas.BleedingTermMs);
    }

    [Fact]
    public void BleedingServerLimitIsOneAndAHalfTimesTheTotal()
    {
        Assert.Equal(1500, ActiveStatusFormulas.BleedingDamageServerLimit(new ActiveStatusDamageInput(1000, 5000)));
        Assert.Equal(4, ActiveStatusFormulas.BleedingDamageServerLimit(new ActiveStatusDamageInput(3, 0)));       // 4.5 向零取整
        Assert.Equal(0, ActiveStatusFormulas.BleedingDamageServerLimit(new ActiveStatusDamageInput(0, 0)));
    }

    [Fact]
    public void LargeBleedingTotalsLoseLowBitsLikeTheOriginalFloats()
    {
        // 16777217 = 2^24 + 1 不能用 float 表示:服务端的"允许伤害"先把它落成 float(16777216),再乘 1.5 → 25165824
        Assert.Equal(25165824, ActiveStatusFormulas.BleedingDamageServerLimit(new ActiveStatusDamageInput(16777217, 0)));
        // 客户端每跳的商落成 float:2^24 + 1 除以 1 跳 → 16777216
        Assert.Equal(16777216, ActiveStatusFormulas.BleedingDamagePerTick(new ActiveStatusDamageInput(16777217, 0)));
    }

    [Fact]
    public void PoisonRampsUpToTheBaseDamage()
    {
        ActiveStatusTable t = Table();
        var d = new ActiveStatusDamageInput(100, 1000);
        Assert.Equal(0, ActiveStatusFormulas.PoisonDamage(t, d, 0));
        Assert.Equal(25, ActiveStatusFormulas.PoisonDamage(t, d, 250));
        Assert.Equal(50, ActiveStatusFormulas.PoisonDamage(t, d, 500));
        Assert.Equal(100, ActiveStatusFormulas.PoisonDamage(t, d, 1000));
        Assert.Equal(100, ActiveStatusFormulas.PoisonDamage(t, d, 5000));
        Assert.Equal(0, ActiveStatusFormulas.PoisonDamage(t, d, -7));
        Assert.Equal(33, ActiveStatusFormulas.PoisonDamage(t, d, 333));                       // 33.3 向零取整
    }

    [Fact]
    public void PoisonAndDiseaseAreNothingWhenTheGuardIsFull()
    {
        ActiveStatusTable t = Table();
        Assert.Equal(0, ActiveStatusFormulas.PoisonDamage(t, new ActiveStatusDamageInput(100, 1000, guard: 100), 1000));
        Assert.Equal(0, ActiveStatusFormulas.DiseaseDamage(t, new ActiveStatusDamageInput(100, 1000, guard: 250), 1000));
        Assert.Equal(100, ActiveStatusFormulas.PoisonDamage(t, new ActiveStatusDamageInput(100, 1000, guard: 99), 1000));
        Assert.Equal(100, ActiveStatusFormulas.DiseaseDamage(t, new ActiveStatusDamageInput(100, 1000, guard: -5), 1000));
        // 灼伤不看这一项
        Assert.Equal(100, ActiveStatusFormulas.BurnDamage(t, new ActiveStatusDamageInput(100, 1000, guard: 200), 1000));
    }

    [Fact]
    public void UserToUserRateAppliesOnlyBetweenPlayers()
    {
        ActiveStatusTable t = Table(userToUser: 0.5f, tower: 0.25f);
        Assert.Equal(100, ActiveStatusFormulas.PoisonDamage(t, new ActiveStatusDamageInput(100, 1000), 1000));
        Assert.Equal(50, ActiveStatusFormulas.PoisonDamage(t, new ActiveStatusDamageInput(100, 1000, userToUser: true), 1000));
        Assert.Equal(25, ActiveStatusFormulas.PoisonDamage(t, new ActiveStatusDamageInput(100, 1000, objectType: 10, userToUser: true), 1000));
        Assert.Equal(100, ActiveStatusFormulas.PoisonDamage(t, new ActiveStatusDamageInput(100, 1000, objectType: 10), 1000));   // 不是玩家打玩家:不乘
        Assert.Equal(50, ActiveStatusFormulas.BurnDamage(t, new ActiveStatusDamageInput(100, 1000, userToUser: true), 1000));
        Assert.Equal(25, ActiveStatusFormulas.BurnDamage(t, new ActiveStatusDamageInput(100, 1000, objectType: 10, userToUser: true), 1000));
        Assert.Equal(50, ActiveStatusFormulas.DiseaseDamage(t, new ActiveStatusDamageInput(100, 1000, userToUser: true), 1000));
        Assert.Equal(0.5f, ActiveStatusFormulas.UserToUserRate(t, false));
        Assert.Equal(0.25f, ActiveStatusFormulas.UserToUserRate(t, true));
    }

    [Fact]
    public void DiseaseTruncatesBeforeTheUserToUserRateAndPoisonAfter()
    {
        // 插值得 1.9(Base 19、Span 10、step 1),玩家对玩家系数 0.6:
        // 病先取整成 1 再乘系数得 0.6 → 0;毒与灼伤先乘系数得 1.14 → 1
        ActiveStatusTable t = Table(userToUser: 0.6f);
        var d = new ActiveStatusDamageInput(19, 10, userToUser: true);
        Assert.Equal(0, ActiveStatusFormulas.DiseaseDamage(t, d, 1));
        Assert.Equal(1, ActiveStatusFormulas.PoisonDamage(t, d, 1));
        Assert.Equal(1, ActiveStatusFormulas.BurnDamage(t, d, 1));
        // 不是玩家打玩家:三者都只取整得 1
        var plain = new ActiveStatusDamageInput(19, 10);
        Assert.Equal(1, ActiveStatusFormulas.DiseaseDamage(t, plain, 1));
        Assert.Equal(1, ActiveStatusFormulas.PoisonDamage(t, plain, 1));
    }

    [Fact]
    public void BurnRampsUpToTheBaseDamage()
    {
        ActiveStatusTable t = Table();
        var d = new ActiveStatusDamageInput(80, 2000);
        Assert.Equal(0, ActiveStatusFormulas.BurnDamage(t, d, 0));
        Assert.Equal(40, ActiveStatusFormulas.BurnDamage(t, d, 1000));
        Assert.Equal(80, ActiveStatusFormulas.BurnDamage(t, d, 2000));
        Assert.Equal(80, ActiveStatusFormulas.BurnDamage(t, d, 9999));
        Assert.Equal(0, ActiveStatusFormulas.BurnDamage(t, d, -1));
    }
}

/// <summary>金标准:全合成输入下,C# 重算与服务端原版函数(模拟器里的机器码)的输出逐条一致。</summary>
public sealed class ActiveStatusGoldenTests
{
    [Fact]
    public void GoldenMatchesTheOriginalFunctions()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Golden", "active_status_golden.txt");
        Assert.True(File.Exists(path), "缺少金标准文件 " + path);
        ActiveStatusCaseReplay.Result r = ActiveStatusCaseReplay.Replay(File.ReadLines(path));
        Assert.True(r.Cases >= 600, "用例太少:" + r.Cases);
        Assert.True(r.Bad == 0, "不一致 " + r.Bad + " 条:\n" + string.Join("\n", r.Samples));
        foreach (string kind in new[] { "UV", "POW", "ETC", "DUR", "BLEED", "SYNC", "POISON", "DISEASE", "BURN" })
        {
            Assert.True(r.PerKind.TryGetValue(kind, out int n) && n > 0, "没有 " + kind + " 用例");
        }
    }

    [Fact]
    public void ReplayCatchesAWrongExpectation()
    {
        // 回放本身要能报错:把一条合成的毒伤害期望值改错,应当被抓到
        var lines = new[]
        {
            "TABLE\t1\t11\t" + string.Join(",", new int[18]) + "\t" + string.Join(",", new int[36]) + "\t" + string.Join(",", new int[36]) + "\t" + string.Join(",", new int[72])
                + "\t1065353216\t1065353216",
            "POISON\t1\t100\t1000\t0\t0\t0\t1000\t100",
            "POISON\t1\t100\t1000\t0\t0\t0\t1000\t99",
        };
        ActiveStatusCaseReplay.Result r = ActiveStatusCaseReplay.Replay(lines);
        Assert.Equal(2, r.Cases);
        Assert.Equal(1, r.Bad);
    }
}
