using System.Text;
using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// .atk 加载器(AttackInfoLoader)的测试。依据 E-047:原版加载函数的读法 + 对全库 6,394 个 .atk 的统计 + 脚本枚举头文件。
/// 测试数据全是合成的标记流,不含原版内容;全库层面的核对由 `dnf atk-check --dump` 与 `tools/py/atk_ref.py --dump` 的逐行对拍完成。
/// </summary>
public sealed class AttackInfoLoaderTests
{
    /// <summary>手工拼标记流 + 字符串池的小构造器。</summary>
    private sealed class Atk
    {
        private readonly List<string> _pool = new();
        private readonly List<ScriptToken> _tokens = new();

        private int Intern(string s)
        {
            int i = _pool.IndexOf(s);
            if (i < 0)
            {
                _pool.Add(s);
                i = _pool.Count - 1;
            }
            return i;
        }

        private Atk Add(TokenType type, int value)
        {
            _tokens.Add(new ScriptToken((byte)type, value));
            return this;
        }

        public Atk Tag(string name) => Add(TokenType.Tag, Intern(name));

        public Atk Int(int v) => Add(TokenType.Int, v);

        public Atk Ints(params int[] values)
        {
            foreach (int v in values)
            {
                Int(v);
            }
            return this;
        }

        public Atk Hex(int v) => Add(TokenType.UInt, v);

        public Atk Float(float v) => Add(TokenType.Float, BitConverter.SingleToInt32Bits(v));

        public Atk Str(string s) => Add(TokenType.String, Intern(s));

        /// <summary>"种类 + 键"对(类型 9 加 10),关键字位置也接受。</summary>
        public Atk Pair(string key) => Add(TokenType.StringRefKind, 1).Add(TokenType.StringRefKey, Intern(key));

        public (AttackInfoData Data, AttackInfoLoadReport Report) Load()
        {
            var s = new MemoryStream();
            void U32(uint v) => s.Write(BitConverter.GetBytes(v));
            U32((uint)_pool.Count);
            uint offset = (uint)((_pool.Count + 1) * 4);
            foreach (string item in _pool)
            {
                U32(offset);
                offset += (uint)item.Length;
            }
            U32(offset);
            foreach (string item in _pool)
            {
                s.Write(Encoding.Latin1.GetBytes(item));
            }
            var table = new PvfStringTable(s.ToArray());
            AttackInfoData data = AttackInfoLoader.Load(new ScriptCursor(_tokens.ToArray(), table), out AttackInfoLoadReport report);
            return (data, report);
        }
    }

    [Fact]
    public void EmptyFileGivesTheOriginalDefaults()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk().Load();
        Assert.True(r.Clean);
        Assert.Equal(0f, d.Damage);
        Assert.Equal(AttackKind.Physic, d.AttackType);
        Assert.Equal(DamageReaction.Damage, d.Reaction);
        Assert.Equal(AttackDirection.Horizon, d.Direction);
        Assert.Equal(HitKind.Unspecified, d.HitType);
        Assert.False(d.HitBlood);
        Assert.Equal(-1, d.HitValue);
        Assert.Equal(-1f, d.HitScale);
        Assert.Equal(HitDirection.Auto, d.HitSide);
        Assert.Equal(1f, d.HumanActiveStatusRate);
        Assert.Equal(-1, d.MaxTargetHitNumber);
        Assert.True(d.HitNumberIncreasing);
        Assert.True(d.AttackEnemy);
        Assert.False(d.AttackFriend);
        Assert.False(d.AttackMyself);
        Assert.Equal(100f, d.HumanDamageRate);
        Assert.Equal(100f, d.FortDamageRate);
        Assert.Equal(100f, d.MonsterDamageRate);
        Assert.Empty(d.Elements);
        Assert.Empty(d.CatchObjects);
        Assert.All(d.Statuses, s => Assert.Equal(ActiveStatusKind.None, s.Kind));
        Assert.Equal(string.Empty, d.HitWav);
    }

    [Fact]
    public void ReadsNumbersKeywordsAndTheHitInfoGroup()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk()
            .Tag("[damage]").Float(1.5f)
            .Tag("[damage bonus]").Int(20)
            .Tag("[absolute damage]").Int(7)
            .Tag("[attack type]").Tag("[magic]")
            .Tag("[weapon damage apply]").Int(1)
            .Tag("[attack enemy]").Int(0)
            .Tag("[attack friend]").Int(1)
            .Tag("[attack myself]").Int(1)
            .Tag("[damage reaction]").Tag("[down]")
            .Tag("[push aside]").Int(200)
            .Tag("[lift up]").Int(50)
            .Tag("[critical hit]").Float(0.25f)
            .Tag("[stuck]").Float(3f)
            .Tag("[stuck prob increase]").Int(1)
            .Tag("[weakness]").Float(1f).Float(2f)
            .Tag("[bounce]").Int(4)
            .Tag("[ignore defense]").Int(1)
            .Tag("[hit wav]").Str("SOME_WAV")
            .Tag("[attack direction]").Tag("[hit down]")
            .Tag("[hit info]").Tag("[mana burn]").Tag("[blood]").Int(50).Int(1)
            .Tag("[is use weapon hit delay rate]").Int(1)
            .Tag("[force hit stun time]").Int(300)
            .Tag("[hit direction]").Tag("[inner]")
            .Tag("[ignore super armor]").Int(1)
            .Tag("[ignore weight]").Int(2)
            .Tag("[HP drain]").Int(1)
            .Tag("[MP drain]").Int(1)
            .Tag("[is hit number increasing]").Int(0)
            .Tag("[max target hit number]").Int(5)
            .Tag("[ignore diehard]").Int(1)
            .Tag("[hit check by screen pos]").Int(1)
            .Tag("[human damage rate]").Float(50f)
            .Tag("[fort damage rate]").Float(60f)
            .Tag("[monster damage rate]").Float(70f)
            .Load();
        Assert.True(r.Clean);
        Assert.Equal(1.5f, d.Damage);
        Assert.Equal(20, d.DamageBonus);
        Assert.Equal(7, d.AbsoluteDamage);
        Assert.Equal(AttackKind.Magic, d.AttackType);
        Assert.True(d.WeaponDamageApply);
        Assert.False(d.AttackEnemy);
        Assert.True(d.AttackFriend);
        Assert.True(d.AttackMyself);
        Assert.Equal(DamageReaction.Down, d.Reaction);
        Assert.Equal(200, d.PushAside);
        Assert.Equal(50, d.LiftUp);
        Assert.Equal(0.25f, d.CriticalHit);
        Assert.Equal(3f, d.Stuck);
        Assert.True(d.StuckProbIncrease);
        Assert.Equal(1f, d.WeaknessFirst);
        Assert.Equal(2f, d.WeaknessSecond);
        Assert.Equal(4, d.Bounce);
        Assert.True(d.IgnoreDefense);
        Assert.Equal("SOME_WAV", d.HitWav);
        Assert.Equal(AttackDirection.Down, d.Direction);
        Assert.Equal(HitKind.ManaBurn, d.HitType);
        Assert.True(d.HitBlood);
        Assert.Equal(50, d.HitValue);
        Assert.Equal(1f, d.HitScale);
        Assert.True(d.UseWeaponHitDelayRate);
        Assert.Equal(300, d.ForceHitStunTime);
        Assert.Equal(HitDirection.Inner, d.HitSide);
        Assert.Equal(1, d.IgnoreSuperArmor);
        Assert.Equal(2, d.IgnoreWeight);
        Assert.True(d.HpDrain);
        Assert.True(d.MpDrain);
        Assert.False(d.HitNumberIncreasing);
        Assert.Equal(5, d.MaxTargetHitNumber);
        Assert.True(d.IgnoreDiehard);
        Assert.True(d.HitCheckByScreenPos);
        Assert.Equal(50f, d.HumanDamageRate);
        Assert.Equal(60f, d.FortDamageRate);
        Assert.Equal(70f, d.MonsterDamageRate);
    }

    [Theory]
    [InlineData("[physic]", AttackKind.Physic)]
    [InlineData("[magic]", AttackKind.Magic)]
    [InlineData("[ignore defense]", AttackKind.IgnoreDefense)]
    public void AttackTypeKeywordsMatchTheScriptEnumeration(string keyword, AttackKind expected)
    {
        (AttackInfoData d, _) = new Atk().Tag("[attack type]").Tag(keyword).Load();
        Assert.Equal(expected, d.AttackType);
    }

    [Fact]
    public void HitInfoKindsAndHitSidesTakeTheirFixedNumbers()
    {
        string[] kinds = { "[blow]", "[cut]", "[burn]", "[etc]", "[mana burn]" };
        for (int i = 0; i < kinds.Length; i++)
        {
            (AttackInfoData d, _) = new Atk().Tag("[hit info]").Tag(kinds[i]).Tag("[no blood]").Int(0).Int(0).Load();
            Assert.Equal((HitKind)i, d.HitType);
            Assert.False(d.HitBlood);
        }
        string[] sides = { "[auto]", "[front]", "[back]", "[outer]", "[inner]" };
        for (int i = 0; i < sides.Length; i++)
        {
            (AttackInfoData d, _) = new Atk().Tag("[hit direction]").Tag(sides[i]).Load();
            Assert.Equal((HitDirection)i, d.HitSide);
        }
        string[] directions = { "[hit lift up]", "[hit horizon]", "[hit down]" };
        for (int i = 0; i < directions.Length; i++)
        {
            (AttackInfoData d, _) = new Atk().Tag("[attack direction]").Tag(directions[i]).Load();
            Assert.Equal((AttackDirection)i, d.Direction);
        }
        string[] reactions = { "[none]", "[damage]", "[down]", "[damage except human]" };
        for (int i = 0; i < reactions.Length; i++)
        {
            (AttackInfoData d, _) = new Atk().Tag("[damage reaction]").Tag(reactions[i]).Load();
            Assert.Equal((DamageReaction)i, d.Reaction);
        }
    }

    [Fact]
    public void HumanActiveStatusRateIsAPercentageStoredAsAFraction()
    {
        (AttackInfoData d, _) = new Atk().Tag("[human active status rate]").Int(50).Load();
        Assert.Equal(0.5f, d.HumanActiveStatusRate);
    }

    [Fact]
    public void IntegerSlotsTruncateFloatsTowardsZeroAndFloatSlotsAcceptIntegers()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk()
            .Tag("[damage bonus]").Float(-97.5f)
            .Tag("[lift up]").Float(9.99f)
            .Tag("[damage]").Int(7)
            .Tag("[bounce]").Hex(0x10)
            .Load();
        Assert.True(r.Clean);
        Assert.Equal(-97, d.DamageBonus);
        Assert.Equal(9, d.LiftUp);
        Assert.Equal(7f, d.Damage);
        Assert.Equal(16, d.Bounce);
    }

    [Fact]
    public void KnockBackTypeThreeReadsOneMoreNumberAndTheOthersDoNot()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk().Tag("[knuck back]").Int(3).Float(-97.9f).Tag("[lift up]").Int(5).Load();
        Assert.True(r.Clean);
        Assert.Equal(3, d.KnockBackType);
        Assert.Equal(-97, d.KnockBackPixels);
        Assert.Equal(5, d.LiftUp);

        (AttackInfoData e, AttackInfoLoadReport s) = new Atk().Tag("[knuck back]").Int(-1).Tag("[lift up]").Int(6).Load();
        Assert.True(s.Clean);
        Assert.Equal(-1, e.KnockBackType);
        Assert.Equal(0, e.KnockBackPixels);
        Assert.Equal(6, e.LiftUp);
    }

    [Fact]
    public void ActiveStatusReadsTypeProbabilityLevelDurationAndTheTypeSpecificExtras()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk()
            .Tag("[active status]").Tag("[stun]").Int(10).Int(0).Int(3000)
            .Tag("[active status]").Tag("[burn]").Float(100f).Int(3).Int(5000).Ints(1, 2, 3, 4)
            .Tag("[active status]").Tag("[poison]").Int(50).Int(1).Int(2000).Int(77)
            .Tag("[lift up]").Int(8)
            .Load();
        Assert.True(r.Clean);
        Assert.Equal(ActiveStatusKind.Stun, d.Statuses[0].Kind);
        Assert.Equal(10f, d.Statuses[0].Probability);
        Assert.Equal(0, d.Statuses[0].Level);
        Assert.Equal(3000, d.Statuses[0].Duration);
        Assert.Empty(d.Statuses[0].Extra);
        Assert.Equal(ActiveStatusKind.Burn, d.Statuses[1].Kind);
        Assert.Equal(100f, d.Statuses[1].Probability);
        Assert.Equal(3, d.Statuses[1].Level);
        Assert.Equal(5000, d.Statuses[1].Duration);
        Assert.Equal(new[] { 1, 2, 3, 4 }, d.Statuses[1].Extra);
        Assert.Equal(ActiveStatusKind.Poison, d.Statuses[2].Kind);
        Assert.Equal(new[] { 77 }, d.Statuses[2].Extra);
        Assert.Equal(8, d.LiftUp);
    }

    [Theory]
    [InlineData("[slow]", 0, 2)]
    [InlineData("[freeze]", 1, 0)]
    [InlineData("[poison]", 2, 1)]
    [InlineData("[stun]", 3, 0)]
    [InlineData("[curse]", 4, 4)]
    [InlineData("[blind]", 5, 2)]
    [InlineData("[lightning]", 6, 1)]
    [InlineData("[stone]", 7, 1)]
    [InlineData("[sleep]", 8, 0)]
    [InlineData("[burn]", 9, 4)]
    [InlineData("[weapon break]", 10, 1)]
    [InlineData("[bleeding]", 11, 1)]
    [InlineData("[haste]", 12, 2)]
    [InlineData("[bless]", 13, 4)]
    [InlineData("[element]", 14, 0)]
    [InlineData("[confuse]", 15, 1)]
    [InlineData("[hold]", 16, 0)]
    [InlineData("[armor break]", 17, 1)]
    public void ActiveStatusTypeNumbersAndExtraCountsFollowTheLoader(string name, int number, int extras)
    {
        var script = new Atk().Tag("[active status]").Tag(name).Int(1).Int(2).Int(3);
        for (int i = 0; i < extras; i++)
        {
            script.Int(100 + i);
        }
        (AttackInfoData d, AttackInfoLoadReport r) = script.Tag("[lift up]").Int(9).Load();
        Assert.True(r.Clean);
        Assert.Equal((ActiveStatusKind)number, d.Statuses[0].Kind);
        Assert.Equal(extras, d.Statuses[0].Extra.Length);
        Assert.Equal(9, d.LiftUp);
    }

    [Fact]
    public void ActiveStatusBeyondThreeSlotsIsIgnoredAndCounted()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk()
            .Tag("[active status]").Tag("[stun]").Int(1).Int(0).Int(10)
            .Tag("[active status]").Tag("[stun]").Int(2).Int(0).Int(20)
            .Tag("[active status]").Tag("[stun]").Int(3).Int(0).Int(30)
            .Tag("[active status]")
            .Load();
        Assert.Equal(1, r.StatusOverflow);
        Assert.Equal(3f, d.Statuses[2].Probability);
    }

    [Fact]
    public void AFourthActiveStatusStillConsumesItsValuesSoTheTagsAfterItStayInSync()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk()
            .Tag("[active status]").Tag("[stun]").Int(1).Int(0).Int(10)
            .Tag("[active status]").Tag("[stun]").Int(2).Int(0).Int(20)
            .Tag("[active status]").Tag("[stun]").Int(3).Int(0).Int(30)
            .Tag("[active status]").Tag("[bleeding]").Int(4).Int(0).Int(40).Int(77)
            .Tag("[lift up]").Int(9)
            .Load();
        Assert.Equal((1, 9, 0, false), (r.StatusOverflow, d.LiftUp, r.UnknownTags, r.StoppedAtStrayToken));
        Assert.Equal((3f, ActiveStatusKind.Stun, 30), (d.Statuses[2].Probability, d.Statuses[2].Kind, d.Statuses[2].Duration));   // 第四个没有覆盖第三个
    }

    [Fact]
    public void UnknownStatusNameGivesNoneButStillConsumesTheThreeNumbers()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk().Tag("[active status]").Tag("[stunn]").Int(1).Int(2).Int(3).Tag("[lift up]").Int(4).Load();
        Assert.Equal(ActiveStatusKind.None, d.Statuses[0].Kind);
        Assert.Equal(3, d.Statuses[0].Duration);
        Assert.Equal(4, d.LiftUp);
        Assert.Equal(0, r.UnknownKeywords); // 状态名不认识不算"关键字不认识":原版直接给 18(没有),不报告
    }

    [Fact]
    public void ElementsAccumulateAndNoneClearsThem()
    {
        (AttackInfoData d, _) = new Atk()
            .Tag("[elemental property]").Tag("[fire element]")
            .Tag("[elemental property]").Tag("[water element]")
            .Load();
        Assert.Equal(new[] { ElementKind.Fire, ElementKind.Water }, d.Elements.ToArray());

        (AttackInfoData e, _) = new Atk()
            .Tag("[elemental property]").Tag("[dark element]")
            .Tag("[elemental property]").Tag("[no element]")
            .Load();
        Assert.Empty(e.Elements);

        (AttackInfoData f, _) = new Atk()
            .Tag("[elemental property]").Tag("[no element]")
            .Tag("[elemental property]").Tag("[light element]")
            .Load();
        Assert.Equal(new[] { ElementKind.Light }, f.Elements.ToArray());

        (AttackInfoData g, _) = new Atk().Tag("[elemental property]").Tag("[fire elemnt]").Load(); // 拼错:当作"无"
        Assert.Empty(g.Elements);
    }

    [Fact]
    public void UnknownKeywordKeepsTheDefaultAndIsCountedAndLoadingContinues()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk()
            .Tag("[damage reaction]").Tag("[damge]")
            .Tag("[attack direction]").Tag("[hit horizontal]")
            .Tag("[lift up]").Int(3)
            .Load();
        Assert.Equal(2, r.UnknownKeywords);
        Assert.Equal(DamageReaction.Damage, d.Reaction);
        Assert.Equal(AttackDirection.Horizon, d.Direction);
        Assert.Equal(3, d.LiftUp);
        Assert.False(r.Clean);
    }

    [Fact]
    public void StringAndPairTokensAreAcceptedWhereAKeywordIsExpected()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk().Tag("[attack type]").Str("[magic]").Tag("[damage reaction]").Pair("[down]").Load();
        Assert.True(r.Clean);
        Assert.Equal(AttackKind.Magic, d.AttackType);
        Assert.Equal(DamageReaction.Down, d.Reaction);
    }

    [Fact]
    public void NumbersBeforeAKeywordAreDiscarded()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk().Tag("[attack type]").Int(5).Int(6).Tag("[magic]").Load();
        Assert.Equal(AttackKind.Magic, d.AttackType);
        Assert.Equal(2, r.DiscardedTokens);
    }

    [Fact]
    public void StrayValueBeforeATagStopsLoadingSoTheRestOfTheFileIsLost()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk().Tag("[damage bonus]").Int(5).Int(777).Tag("[lift up]").Int(9).Load();
        Assert.Equal(5, d.DamageBonus);
        Assert.Equal(0, d.LiftUp);
        Assert.True(r.StoppedAtStrayToken);
        Assert.Equal(1, r.DiscardedTokens);
    }

    [Fact]
    public void StrayValuesAtTheEndOfTheFileAreHarmless()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk().Tag("[damage bonus]").Int(5).Int(777).Load();
        Assert.Equal(5, d.DamageBonus);
        Assert.False(r.StoppedAtStrayToken);
        Assert.Equal(1, r.DiscardedTokens);
    }

    [Fact]
    public void MissingValueSwallowsTheNextTagAndItsValueBecomesStray()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk().Tag("[damage bonus]").Tag("[lift up]").Int(9).Load();
        Assert.Equal(0, d.DamageBonus);
        Assert.Equal(0, d.LiftUp);
        Assert.Equal(1, r.BadValues);
    }

    [Fact]
    public void UnknownTagIsReportedAndItsValuesBecomeStrayTokens()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk().Tag("[bogus]").Int(1).Tag("[damage]").Float(2f).Load();
        Assert.Equal(1, r.UnknownTags);
        Assert.Equal("[bogus]", r.FirstUnknownTag);
        Assert.True(r.StoppedAtStrayToken);
        Assert.Equal(0f, d.Damage);
    }

    [Fact]
    public void TagsAreMatchedCaseSensitively()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk().Tag("[DAMAGE BONUS]").Load();
        Assert.Equal(1, r.UnknownTags);
        Assert.Equal(0, d.DamageBonus);
    }

    [Fact]
    public void ModuleSectionsOtherThanDungeonAreSkippedAndDungeonTagsAreNoOps()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk()
            .Tag("[dungeon]")
            .Tag("[damage]").Float(1f)
            .Tag("[pvp]").Tag("[damage]").Float(5f).Tag("[lift up]").Int(3).Tag("[/pvp]")
            .Tag("[push aside]").Int(7)
            .Tag("[warroom]").Tag("[push aside]").Int(99).Tag("[/warroom]")
            .Tag("[/dungeon]")
            .Load();
        Assert.True(r.Clean);
        Assert.Equal(1f, d.Damage);
        Assert.Equal(0, d.LiftUp);
        Assert.Equal(7, d.PushAside);
        Assert.Equal(2, r.SkippedModuleSections);
    }

    [Fact]
    public void ModuleSectionWithoutAnEndTagEndsTheFile()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk().Tag("[damage]").Float(1f).Tag("[pvp]").Tag("[damage]").Float(5f).Load();
        Assert.True(r.UnterminatedModuleSection);
        Assert.Equal(1f, d.Damage);
    }

    [Fact]
    public void CatchObjectsAreReadAsTriplesUntilANonNumberWhichIsConsumed()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk()
            .Tag("[catch object]").Int(1).Int(2).Float(3.5f).Int(4).Int(5).Int(6).Tag("[/catch object]")
            .Tag("[lift up]").Int(8)
            .Load();
        Assert.True(r.Clean);
        Assert.Equal(2, d.CatchObjects.Count);
        Assert.Equal(1, d.CatchObjects[0].First);
        Assert.Equal(2, d.CatchObjects[0].Second);
        Assert.Equal(3.5f, d.CatchObjects[0].Third);
        Assert.Equal(6f, d.CatchObjects[1].Third);
        Assert.Equal(8, d.LiftUp);
    }

    [Fact]
    public void HitWavAcceptsAStringButNotANumber()
    {
        (AttackInfoData d, AttackInfoLoadReport r) = new Atk().Tag("[hit wav]").Int(5).Load();
        Assert.Equal(string.Empty, d.HitWav);
        Assert.Equal(1, r.BadValues);
    }

    [Fact]
    public void TruncateToIntFollowsTheFistpRules()
    {
        Assert.Equal(-97, ScriptCursor.TruncateToInt(-97.9f));
        Assert.Equal(97, ScriptCursor.TruncateToInt(97.9f));
        Assert.Equal(int.MinValue, ScriptCursor.TruncateToInt(float.NaN));
        Assert.Equal(int.MinValue, ScriptCursor.TruncateToInt(3e10f));
        Assert.Equal(int.MinValue, ScriptCursor.TruncateToInt(-3e10f));
    }

    [Fact]
    public void DescribeIsStableAndUsesFloatBitPatterns()
    {
        (AttackInfoData d, _) = new Atk().Tag("[damage]").Float(1f).Load();
        string line = d.Describe();
        Assert.Contains("damage=3f800000 ", line);
        Assert.Contains("fort_damage_rate=42c80000", line);
        Assert.Contains("attack_enemy=1", line);
        Assert.StartsWith("absolute_damage=0 attack_direction=1", line);
    }

    [Fact]
    public void GameDataLoadsTheTypedAttackInfoThroughThePvfSource()
    {
        var fx = new Fixture();
        fx.Tokens("monster/x/hit.atk", w => w.Tag("[damage bonus]").Int(30).Tag("[attack type]").Tag("[magic]").Tag("[elemental property]").Tag("[water element]"));
        var data = new GameData(fx.Build());
        AttackInfoDef def = data.GetAttackInfo("monster/x/hit.atk");
        Assert.NotNull(def);
        Assert.NotNull(def.Data);
        Assert.True(def.Report.Clean);
        Assert.Equal(30, def.Data.DamageBonus);
        Assert.Equal(AttackKind.Magic, def.Data.AttackType);
        Assert.Equal(new[] { ElementKind.Water }, def.Data.Elements.ToArray());
        Assert.Null(data.GetAttackInfo("monster/x/missing.atk"));
    }
}
