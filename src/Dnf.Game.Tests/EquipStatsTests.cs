using Dnf.Data;

namespace Dnf.Game.Tests;

/// <summary>
/// 装备属性与武器外观的读入、属性集的运算、装备栏的汇总。数据全是合成的:物品编号 901 起,数值是随手写的小整数,与原版数据无关。
/// 读法的整库证据(独立 Python 实现逐件对拍)在 equip-check 里,这里只钉住规则本身与汇总的账。
/// </summary>
public class EquipStatsTests
{
    // ------------------------------------------------------------------ 夹具

    private static EquipStats Read(TokenStreamBuilder b)
    {
        (ScriptToken[] tokens, PvfStringTable strings) = b.Build();
        return EquipStats.Read(tokens, strings);
    }

    private static EquipLook ReadLook(TokenStreamBuilder b)
    {
        (ScriptToken[] tokens, PvfStringTable strings) = b.Build();
        return EquipLook.Read(tokens, strings);
    }

    private static ItemInfo Parse(TokenStreamBuilder b, ItemKind kind = ItemKind.Equipment, int code = 901)
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return ItemScript.Parse(b.Bytes(), strings, kind, code, "equipment/test/e" + code + ".equ");
    }

    private static (long, long, long, long, long, long, long, long, long) Fields(StatBlock s)
    {
        return (s.HpMax, s.MpMax, s.PhysicalAttack, s.PhysicalDefense, s.MagicalAttack, s.MagicalDefense, s.InventoryLimit, s.MpRegenSpeed, s.HitRecovery);
    }

    private static readonly (long, long, long, long, long, long, long, long, long) Zeros = (0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static ItemInfo Gear(int code, int part, EquipStats stats = null, string job = "[swordman]", int level = 1)
    {
        var item = new ItemInfo(code, "equipment/test/g" + code + ".equ", ItemKind.Equipment) { Part = part, MinimumLevel = level, Stats = stats };
        item.UsableJobs.Add(job);
        return item;
    }

    private static EquipStats RandomStats(Random random)
    {
        var builder = new EquipStats.Builder();
        foreach (EquipStat stat in Enum.GetValues<EquipStat>())
        {
            if (random.Next(3) != 0)
            {
                continue;
            }
            if (random.Next(2) == 0)
            {
                builder.Set(stat, random.Next(-50, 500));
            }
            else
            {
                builder.Set(stat, random.Next(0, 300), random.Next(0, 300));
            }
        }
        return builder.Build();
    }

    /// <summary>另一种求和(按标签字符串分桶,不用有序合并),给"汇总等于各件之和"做参照。</summary>
    private static Dictionary<string, (double First, double Second, bool HasSecond)> Reference(IEnumerable<ItemInfo> items)
    {
        var sum = new Dictionary<string, (double First, double Second, bool HasSecond)>();
        foreach (ItemInfo item in items)
        {
            foreach (EquipStatEntry e in item.Stats.Entries)
            {
                string tag = EquipStats.TagOf(e.Stat);
                (double f, double s, bool h) = sum.TryGetValue(tag, out var old) ? old : (0.0, 0.0, false);
                sum[tag] = (f + e.First, s + e.Second, h || e.HasSecond);
            }
        }
        return sum;
    }

    private static void AssertMatchesReference(EquipStats total, Dictionary<string, (double First, double Second, bool HasSecond)> reference)
    {
        Assert.Equal(reference.Count, total.Count);
        foreach (EquipStatEntry e in total.Entries)
        {
            Assert.True(reference.TryGetValue(EquipStats.TagOf(e.Stat), out var expected), EquipStats.TagOf(e.Stat));
            Assert.Equal(expected.First, e.First);
            Assert.Equal(expected.Second, e.Second);
            Assert.Equal(expected.HasSecond, e.HasSecond);
        }
    }

    // ------------------------------------------------------------------ 标签表

    [Fact]
    public void TheTagTableHasOneDistinctLowercaseTagPerMemberInMemberOrder()
    {
        EquipStat[] members = Enum.GetValues<EquipStat>();
        Assert.Equal(EquipStats.StatCount, members.Length);
        var seen = new HashSet<string>();
        for (int i = 0; i < members.Length; i++)
        {
            Assert.Equal(i, (int)members[i]);
            string tag = EquipStats.TagOf(members[i]);
            Assert.NotNull(tag);
            Assert.Matches(@"^\[[a-z ]+\]$", tag);
            Assert.True(seen.Add(tag), tag);
            Assert.True(EquipStats.TryFromTag(tag, out EquipStat back));
            Assert.Equal(members[i], back);
        }
        Assert.Null(EquipStats.TagOf((EquipStat)EquipStats.StatCount));
        Assert.Null(EquipStats.TagOf((EquipStat)(-1)));
    }

    [Fact]
    public void TheFirstNineMembersAreTheCharacterStatBlockFieldsInOrder()
    {
        string[] expected =
        {
            "[hp max]", "[mp max]", "[physical attack]", "[physical defense]", "[magical attack]", "[magical defense]", "[inventory limit]",
            "[mp regen speed]", "[hit recovery]",
        };
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], EquipStats.TagOf((EquipStat)i));
        }
    }

    [Fact]
    public void EveryMembersTagIsItsNameSplitIntoLowercaseWords()
    {
        // 期望独立于标签表:成员名按驼峰拆词、小写、空格相连、加方括号;唯一的例外是数据里把 activestatus 写成一个词
        foreach (EquipStat member in Enum.GetValues<EquipStat>())
        {
            string words = string.Join(" ", System.Text.RegularExpressions.Regex.Split(member.ToString(), "(?<=[a-z])(?=[A-Z])")).ToLowerInvariant();
            if (member == EquipStat.AllActiveStatusResistance)
            {
                words = "all activestatus resistance";
            }
            Assert.Equal("[" + words + "]", EquipStats.TagOf(member));
        }
    }

    [Fact]
    public void TagLookupIgnoresCaseAndRejectsUnknownTags()
    {
        Assert.True(EquipStats.TryFromTag("[HP Max]", out EquipStat stat));
        Assert.Equal(EquipStat.HpMax, stat);
        Assert.False(EquipStats.TryFromTag("[grade]", out _));
        Assert.False(EquipStats.TryFromTag("[/hp max]", out _));
        Assert.False(EquipStats.TryFromTag(string.Empty, out _));
        Assert.False(EquipStats.TryFromTag(null, out _));
    }

    // ------------------------------------------------------------------ 读属性

    [Fact]
    public void ANumberAfterAKnownTagIsRead()
    {
        EquipStats s = Read(new TokenStreamBuilder().Tag("[hp max]").Int(12).Tag("[move speed]").Int(-3));
        Assert.Equal(2, s.Count);
        Assert.False(s.IsEmpty);
        Assert.Equal((12.0, -3.0, 0.0), (s[EquipStat.HpMax], s[EquipStat.MoveSpeed], s[EquipStat.MpMax]));
        Assert.True(s.Has(EquipStat.HpMax));
        Assert.False(s.Has(EquipStat.MpMax));
        Assert.False(s.HasSecond(EquipStat.HpMax));
    }

    [Fact]
    public void FloatsAndUnsignedNumbersAreReadByValue()
    {
        EquipStats s = Read(new TokenStreamBuilder().Tag("[attack speed]").Float(1.5f).Tag("[cast speed]").UInt(4000000000u));
        Assert.Equal(1.5, s[EquipStat.AttackSpeed]);
        Assert.Equal(4000000000.0, s[EquipStat.CastSpeed]);
    }

    [Fact]
    public void OnlyTheFirstTwoNumbersAreKept()
    {
        EquipStats s = Read(new TokenStreamBuilder().Tag("[equipment physical attack]").Int(30).Int(20).Int(10).Tag("[hp max]").Int(1).Int(2).Int(3));
        Assert.Equal((30.0, 20.0, true), (s[EquipStat.EquipmentPhysicalAttack], s.SecondOf(EquipStat.EquipmentPhysicalAttack), s.HasSecond(EquipStat.EquipmentPhysicalAttack)));
        Assert.Equal((1.0, 2.0), (s[EquipStat.HpMax], s.SecondOf(EquipStat.HpMax)));
    }

    [Fact]
    public void NonNumericValuesAreSkippedAndDoNotCountAsNumbers()
    {
        EquipStats s = Read(new TokenStreamBuilder().Tag("[hp max]").Str("abc").Int(7).Str("def"));
        Assert.Equal((7.0, false), (s[EquipStat.HpMax], s.HasSecond(EquipStat.HpMax)));
    }

    [Fact]
    public void ValuesBelongToTheTagRightBeforeThemEvenWhenThatTagIsUnknown()
    {
        EquipStats s = Read(new TokenStreamBuilder().Tag("[hp max]").Int(5).Tag("[grade]").Int(9).Tag("[mp max]").Int(6));
        Assert.Equal((5.0, false, 6.0), (s[EquipStat.HpMax], s.HasSecond(EquipStat.HpMax), s[EquipStat.MpMax]));
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void TagNamesAreMatchedIgnoringCase()
    {
        EquipStats s = Read(new TokenStreamBuilder().Tag("[HP Max]").Int(5).Tag("[Equipment Physical Defense]").Int(2).Int(1));
        Assert.Equal((5.0, 2.0, 1.0), (s[EquipStat.HpMax], s[EquipStat.EquipmentPhysicalDefense], s.SecondOf(EquipStat.EquipmentPhysicalDefense)));
    }

    [Fact]
    public void ALaterTagOverridesTheEarlierOneAndDropsItsSecondNumber()
    {
        EquipStats s = Read(new TokenStreamBuilder()
            .Tag("[equipment physical defense]").Int(4).Int(3)
            .Tag("[move speed]").Int(5)
            .Tag("[equipment physical defense]").Int(7)
            .Tag("[move speed]").Int(9));
        Assert.Equal((7.0, false, 9.0), (s[EquipStat.EquipmentPhysicalDefense], s.HasSecond(EquipStat.EquipmentPhysicalDefense), s[EquipStat.MoveSpeed]));
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void ALineWithoutNumbersDoesNotOverrideAnEarlierOne()
    {
        EquipStats s = Read(new TokenStreamBuilder().Tag("[move speed]").Int(5).Tag("[move speed]").Str("x").Tag("[attack speed]"));
        Assert.Equal(5.0, s[EquipStat.MoveSpeed]);
        Assert.False(s.Has(EquipStat.AttackSpeed));
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void ScriptsWithoutKnownStatsGiveTheSharedEmptyInstance()
    {
        Assert.Same(EquipStats.Empty, Read(new TokenStreamBuilder()));
        Assert.Same(EquipStats.Empty, Read(new TokenStreamBuilder().Tag("[grade]").Int(3).Tag("[name]").Str("x")));
        Assert.True(EquipStats.Empty.IsEmpty);
        Assert.Equal(0, EquipStats.Empty.Count);
        Assert.Equal(0.0, EquipStats.Empty[EquipStat.HpMax]);
    }

    [Fact]
    public void ReadRejectsNullArguments()
    {
        (ScriptToken[] tokens, PvfStringTable strings) = new TokenStreamBuilder().Build();
        Assert.Throws<ArgumentNullException>(() => EquipStats.Read(null, strings));
        Assert.Throws<ArgumentNullException>(() => EquipStats.Read(tokens, null));
        Assert.Throws<ArgumentNullException>(() => EquipLook.Read(null, strings));
        Assert.Throws<ArgumentNullException>(() => EquipLook.Read(tokens, null));
    }

    // ------------------------------------------------------------------ 块规则

    [Fact]
    public void TagsInsideABlockAreNotTheItemsOwnAndNeverOverrideThem()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[set item]").Tag("[hp max]").Int(77).Tag("[/set item]");           // 块在前
        b.Tag("[hp max]").Int(5);
        b.Tag("[level section ability]");
        b.Tag("[hp max]").Int(99).Tag("[mp max]").Int(88);
        b.Tag("[/level section ability]");                                         // 块在后
        b.Tag("[physical attack]").Int(7);
        EquipStats s = Read(b);
        Assert.Equal((5.0, 0.0, 7.0), (s[EquipStat.HpMax], s[EquipStat.MpMax], s[EquipStat.PhysicalAttack]));
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void ATagWithoutACloserIsNotABlock()
    {
        // [animation job] 一类只有开头没有收尾的标签是普通标签:它后面的属性仍在深度 0
        EquipStats s = Read(new TokenStreamBuilder().Tag("[animation job]").Str("[swordman]").Tag("[hp max]").Int(4));
        Assert.Equal(4.0, s[EquipStat.HpMax]);
    }

    [Fact]
    public void NestedBlocksReturnToDepthZeroOnlyWhenTheOuterOneCloses()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[a]").Tag("[b]").Tag("[hp max]").Int(1).Tag("[/b]").Tag("[mp max]").Int(2).Tag("[/a]").Tag("[physical attack]").Int(3);
        EquipStats s = Read(b);
        Assert.Equal(1, s.Count);
        Assert.Equal(3.0, s[EquipStat.PhysicalAttack]);
    }

    [Fact]
    public void ACloserPopsEveryBlockOpenedAfterItsOpener()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[a]").Tag("[b]").Tag("[hp max]").Int(1).Tag("[/a]").Tag("[mp max]").Int(2).Tag("[/b]").Tag("[physical attack]").Int(3);
        EquipStats s = Read(b);
        Assert.Equal((0.0, 2.0, 3.0), (s[EquipStat.HpMax], s[EquipStat.MpMax], s[EquipStat.PhysicalAttack]));   // [/a] 把 [b] 一起弹掉;后面的 [/b] 是孤立的,忽略
    }

    [Fact]
    public void ACloserWithoutAnOpenerIsIgnored()
    {
        EquipStats s = Read(new TokenStreamBuilder().Tag("[/x]").Tag("[hp max]").Int(3));
        Assert.Equal(3.0, s[EquipStat.HpMax]);
    }

    [Fact]
    public void AStrayCloserInsideAnOpenBlockDoesNotEndTheBlock()
    {
        // [/zzz] 没有对应的开头:忽略,不能把还开着的 [a] 弹掉,所以 [hp max] 3 仍在块里
        var b = new TokenStreamBuilder();
        b.Tag("[a]").Tag("[/zzz]").Tag("[hp max]").Int(3).Tag("[/a]").Tag("[mp max]").Int(4);
        EquipStats s = Read(b);
        Assert.Equal((0.0, 4.0), (s[EquipStat.HpMax], s[EquipStat.MpMax]));
    }

    [Fact]
    public void ABlockThatIsNeverClosedSwallowsTheRestOfTheScript()
    {
        // 收尾标签在开头标签之前:那个收尾是孤立的,开头标签之后就一直在块里
        EquipStats s = Read(new TokenStreamBuilder().Tag("[hp max]").Int(2).Tag("[/a]").Tag("[a]").Tag("[mp max]").Int(3));
        Assert.Equal(1, s.Count);
        Assert.Equal(2.0, s[EquipStat.HpMax]);
    }

    [Fact]
    public void BlockNamesAreMatchedIgnoringCase()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[Level Section Ability]").Tag("[hp max]").Int(9).Tag("[/level section ability]").Tag("[mp max]").Int(1);
        EquipStats s = Read(b);
        Assert.Equal((0.0, 1.0), (s[EquipStat.HpMax], s[EquipStat.MpMax]));
    }

    [Fact]
    public void ACloserPopsOnlyDownToTheLastOpenBlockOfTheSameName()
    {
        // [x] [x] [/x]:第一个收尾只弹最近的那个 [x],外层的 [x] 还开着,所以 [hp max] 3 仍在块里;第二个收尾才回到深度 0
        var b = new TokenStreamBuilder();
        b.Tag("[x]").Tag("[x]").Tag("[/x]").Tag("[hp max]").Int(3).Tag("[/x]").Tag("[mp max]").Int(4);
        EquipStats s = Read(b);
        Assert.Equal((0.0, 4.0), (s[EquipStat.HpMax], s[EquipStat.MpMax]));
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void ATagWhoseStringIndexIsOutsideThePoolIsNoKnownTagAndLeavesTheBlockStackAlone()
    {
        (ScriptToken[] head, PvfStringTable strings) = new TokenStreamBuilder().Tag("[hp max]").Int(5).Build();
        ScriptToken[] tokens = head.Concat(new[]
        {
            new ScriptToken((byte)TokenType.Tag, strings.Count),                    // 恰好越界一格
            new ScriptToken((byte)TokenType.Int, 7),
            new ScriptToken((byte)TokenType.Tag, -1),                               // 负的下标
            new ScriptToken((byte)TokenType.Int, 8),
        }).ToArray();
        EquipStats s = EquipStats.Read(tokens, strings);
        Assert.Equal((5.0, 1), (s[EquipStat.HpMax], s.Count));                      // 池外的标签不是属性标签,它后面的数也不算到前一个标签头上
        Assert.Equal(new[] { 0, -1, 0, -1, 0, -1 }, ScriptBlocks.Depths(tokens, strings));
        Assert.Empty(EquipLook.Read(tokens, strings).Segments);
    }

    [Fact]
    public void DepthsMarkEachTagWithItsBlockDepthAndOtherTokensWithMinusOne()
    {
        var b = new TokenStreamBuilder();
        // 0:[a] 1:1 2:[b] 3:[c] 4:2 5:[/b] 6:[d] 7:[/a] 8:[e];[c] [d] [e] 没有收尾,是普通标签
        b.Tag("[a]").Int(1).Tag("[b]").Tag("[c]").Int(2).Tag("[/b]").Tag("[d]").Tag("[/a]").Tag("[e]");
        (ScriptToken[] tokens, PvfStringTable strings) = b.Build();
        Assert.Equal(new[] { 0, -1, 1, 2, -1, 1, 1, 0, 0 }, ScriptBlocks.Depths(tokens, strings));
    }

    // ------------------------------------------------------------------ 属性集的运算

    [Fact]
    public void EqualityComparesEveryNumberTheSecondNumberFlagTheItemsAndTheCount()
    {
        EquipStats a = EquipStats.Empty.With(EquipStat.HpMax, 5).With(EquipStat.EquipmentPhysicalAttack, 3, 4);
        Assert.True(a.Equals(EquipStats.Empty.With(EquipStat.HpMax, 5).With(EquipStat.EquipmentPhysicalAttack, 3, 4)));
        Assert.False(a.Equals(EquipStats.Empty.With(EquipStat.HpMax, 6).With(EquipStat.EquipmentPhysicalAttack, 3, 4)));        // 第一个数不同
        Assert.False(a.Equals(EquipStats.Empty.With(EquipStat.HpMax, 5).With(EquipStat.EquipmentPhysicalAttack, 3, 9)));        // 只有第二个数不同
        Assert.False(a.Equals(EquipStats.Empty.With(EquipStat.HpMax, 5).With(EquipStat.EquipmentMagicalAttack, 3, 4)));         // 项不同
        Assert.False(a.Equals(EquipStats.Empty.With(EquipStat.HpMax, 5)));                                                      // 项数不同
        Assert.False(EquipStats.Empty.With(EquipStat.HpMax, 5).Equals(a));
        Assert.False(a.Equals(null));
        // 第二个数写了 0 与根本没写第二个数不一样(有没有第二个数的标记不同)
        Assert.False(EquipStats.Empty.With(EquipStat.EquipmentPhysicalAttack, 3, 0).Equals(EquipStats.Empty.With(EquipStat.EquipmentPhysicalAttack, 3)));
    }

    [Fact]
    public void WithAddsAndReplacesOneItemKeepsTheMemberOrderAndDoesNotTouchTheOriginal()
    {
        EquipStats s = EquipStats.Empty.With(EquipStat.MoveSpeed, 4).With(EquipStat.HpMax, 10).With(EquipStat.EquipmentPhysicalAttack, 7, 3);
        Assert.Equal(new[] { EquipStat.HpMax, EquipStat.EquipmentPhysicalAttack, EquipStat.MoveSpeed }, s.Entries.Select(e => e.Stat));
        EquipStats t = s.With(EquipStat.EquipmentPhysicalAttack, 9);
        Assert.Equal((9.0, false), (t[EquipStat.EquipmentPhysicalAttack], t.HasSecond(EquipStat.EquipmentPhysicalAttack)));
        Assert.Equal((7.0, 3.0, true), (s[EquipStat.EquipmentPhysicalAttack], s.SecondOf(EquipStat.EquipmentPhysicalAttack), s.HasSecond(EquipStat.EquipmentPhysicalAttack)));
        Assert.Equal(3, t.Count);
    }

    [Fact]
    public void PlusAddsBothNumbersItemByItemAndLeavesTheOperandsAlone()
    {
        EquipStats a = EquipStats.Empty.With(EquipStat.HpMax, 10).With(EquipStat.EquipmentPhysicalAttack, 7, 3);
        EquipStats b = EquipStats.Empty.With(EquipStat.HpMax, 5).With(EquipStat.MoveSpeed, 2).With(EquipStat.EquipmentPhysicalAttack, 1, 2);
        EquipStats c = a.Plus(b);
        Assert.Equal(3, c.Count);
        Assert.Equal((15.0, 2.0, 8.0, 5.0, true), (c[EquipStat.HpMax], c[EquipStat.MoveSpeed], c[EquipStat.EquipmentPhysicalAttack],
            c.SecondOf(EquipStat.EquipmentPhysicalAttack), c.HasSecond(EquipStat.EquipmentPhysicalAttack)));
        Assert.Equal((2, 10.0), (a.Count, a[EquipStat.HpMax]));
        Assert.Equal((3, 5.0), (b.Count, b[EquipStat.HpMax]));
    }

    [Fact]
    public void PlusMarksASecondNumberWhenEitherSideHasOne()
    {
        EquipStats one = EquipStats.Empty.With(EquipStat.EquipmentPhysicalDefense, 3);
        EquipStats two = EquipStats.Empty.With(EquipStat.EquipmentPhysicalDefense, 4, 2);
        foreach (EquipStats c in new[] { one.Plus(two), two.Plus(one) })
        {
            Assert.Equal((7.0, 2.0, true), (c[EquipStat.EquipmentPhysicalDefense], c.SecondOf(EquipStat.EquipmentPhysicalDefense), c.HasSecond(EquipStat.EquipmentPhysicalDefense)));
        }
        Assert.False(one.Plus(one).HasSecond(EquipStat.EquipmentPhysicalDefense));
    }

    [Fact]
    public void PlusWithNothingGivesBackTheSameInstance()
    {
        EquipStats a = EquipStats.Empty.With(EquipStat.HpMax, 10);
        Assert.Same(a, a.Plus(null));
        Assert.Same(a, a.Plus(EquipStats.Empty));
        Assert.Same(a, EquipStats.Empty.Plus(a));
        Assert.Same(EquipStats.Empty, EquipStats.Empty.Plus(EquipStats.Empty));
    }

    [Fact]
    public void SumAddsAWholeSequenceAndNothingSumsToEmpty()
    {
        EquipStats a = EquipStats.Empty.With(EquipStat.HpMax, 10);
        EquipStats b = EquipStats.Empty.With(EquipStat.HpMax, 5).With(EquipStat.MpMax, 1);
        EquipStats c = EquipStats.Empty.With(EquipStat.MpMax, 2);
        EquipStats total = EquipStats.Sum(new[] { a, b, null, c });
        Assert.Equal((15.0, 3.0, 2), (total[EquipStat.HpMax], total[EquipStat.MpMax], total.Count));
        Assert.Same(EquipStats.Empty, EquipStats.Sum(new EquipStats[0]));
        Assert.Throws<ArgumentNullException>(() => EquipStats.Sum(null));
    }

    [Fact]
    public void EqualityIsByContent()
    {
        EquipStats a = EquipStats.Empty.With(EquipStat.HpMax, 10).With(EquipStat.MoveSpeed, 2, 1);
        EquipStats b = EquipStats.Empty.With(EquipStat.MoveSpeed, 2, 1).With(EquipStat.HpMax, 10);
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a.Equals(a.With(EquipStat.HpMax, 11)));
        Assert.False(a.Equals(a.With(EquipStat.MoveSpeed, 2)));                    // 少了第二个数
        Assert.False(a.Equals(null));
        Assert.False(a.Equals(EquipStats.Empty));
        Assert.True(EquipStats.Empty.Equals(EquipStats.Empty));
    }

    [Fact]
    public void TheBuilderOverwritesAnItemAndDropsItsSecondNumber()
    {
        EquipStats s = new EquipStats.Builder().Set(EquipStat.HpMax, 1, 2).Set(EquipStat.HpMax, 3).Set(EquipStat.MpMax, 4).Build();
        Assert.Equal((3.0, false, 4.0, 2), (s[EquipStat.HpMax], s.HasSecond(EquipStat.HpMax), s[EquipStat.MpMax], s.Count));
        Assert.Same(EquipStats.Empty, new EquipStats.Builder().Build());
    }

    [Fact]
    public void ToStringListsTheItemsInOrderAndADashWhenThereAreNone()
    {
        Assert.Equal("-", EquipStats.Empty.ToString());
        Assert.Equal("[hp max]=10;[equipment physical attack]=7,3", EquipStats.Empty.With(EquipStat.EquipmentPhysicalAttack, 7, 3).With(EquipStat.HpMax, 10).ToString());
    }

    // ------------------------------------------------------------------ 折成角色属性块

    [Fact]
    public void TheNineSameNameTagsFoldIntoTheBlockInTenths()
    {
        EquipStats s = EquipStats.Empty
            .With(EquipStat.HpMax, 11).With(EquipStat.MpMax, 12).With(EquipStat.PhysicalAttack, 13).With(EquipStat.PhysicalDefense, 14)
            .With(EquipStat.MagicalAttack, 15).With(EquipStat.MagicalDefense, 16).With(EquipStat.InventoryLimit, 17).With(EquipStat.MpRegenSpeed, 18)
            .With(EquipStat.HitRecovery, 19);
        Assert.Equal((110L, 120L, 130L, 140L, 150L, 160L, 170L, 180L, 190L), Fields(s.ToStatBlock()));
    }

    [Fact]
    public void TheEquipmentDefensePairAddsToDefenseWithItsFirstNumberOnly()
    {
        EquipStats s = EquipStats.Empty.With(EquipStat.PhysicalDefense, 4).With(EquipStat.EquipmentPhysicalDefense, 6, 5).With(EquipStat.EquipmentMagicalDefense, 3, 9);
        StatBlock b = s.ToStatBlock();
        Assert.Equal((100L, 30L), (b.PhysicalDefense, b.MagicalDefense));            // 4×10 + 6×10;0 + 3×10;第二个数(5、9)不参与
    }

    [Fact]
    public void ItemsOutsideTheNineDoNotTouchTheBlock()
    {
        EquipStats s = EquipStats.Empty.With(EquipStat.EquipmentPhysicalAttack, 50, 40).With(EquipStat.EquipmentMagicalAttack, 30, 20).With(EquipStat.AttackSpeed, 5)
            .With(EquipStat.FireResistance, 3).With(EquipStat.PhysicalCriticalHit, 2).With(EquipStat.MpMaxRate, 1.5).With(EquipStat.SeparateAttack, 8, 4);
        Assert.Equal(Zeros, Fields(s.ToStatBlock()));
        Assert.Equal(Zeros, Fields(EquipStats.Empty.ToStatBlock()));
    }

    [Fact]
    public void FoldingRoundsAwayFromZeroLikeTheCharacterScriptsReader()
    {
        Assert.Equal(13L, EquipStats.Empty.With(EquipStat.PhysicalAttack, 1.25).ToStatBlock().PhysicalAttack);
        Assert.Equal(-13L, EquipStats.Empty.With(EquipStat.PhysicalAttack, -1.25).ToStatBlock().PhysicalAttack);
        Assert.Equal(0L, EquipStats.Empty.With(EquipStat.HpMax, 0.04).ToStatBlock().HpMax);
    }

    [Fact]
    public void FoldingIsLinearForWholeNumbers()
    {
        var random = new Random(901);
        for (int n = 0; n < 300; n++)
        {
            EquipStats a = RandomStats(random);
            EquipStats b = RandomStats(random);
            Assert.Equal(Fields(a.ToStatBlock() + b.ToStatBlock()), Fields(a.Plus(b).ToStatBlock()));
        }
    }

    // ------------------------------------------------------------------ 武器外观

    [Fact]
    public void ALookSegmentGathersItsJobsVariationLayersAndScripts()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[animation job]").Str("[swordman]");
        b.Tag("[variation]").Int(3).Int(7);
        b.Tag("[layer variation]").Int(10).Str("lyr_a");
        b.Tag("[equipment ani script]").Str("path/a.ani");
        b.Tag("[layer variation]").Int(20).Str("lyr_b");
        b.Tag("[equipment ani script]").Str("path/b.ani");
        b.Tag("[animation job]").Str("[fighter]");
        b.Tag("[variation]").Int(1).Int(2);
        b.Tag("[sub type]").Int(5);
        EquipLook look = ReadLook(b);
        Assert.Equal(2, look.Segments.Count);
        LookSegment sword = look.Segments[0];
        Assert.Equal(("[swordman]", true, 3, 7), (sword.Job, sword.HasVariation, sword.VariationA, sword.VariationB));
        Assert.Equal(new[] { (10, "lyr_a"), (20, "lyr_b") }, sword.Layers.Select(l => (l.Order, l.Name)));
        Assert.Equal(new[] { "path/a.ani", "path/b.ani" }, sword.AniScripts);
        LookSegment fighter = look.Segments[1];
        Assert.Equal(("[fighter]", true, 1, 2), (fighter.Job, fighter.HasVariation, fighter.VariationA, fighter.VariationB));
        Assert.Empty(fighter.Layers);
        Assert.Empty(fighter.AniScripts);
        Assert.Equal((true, 5, 0), (look.HasSubType, look.SubType, look.NestedTagCount));
    }

    [Fact]
    public void ForJobFindsTheFirstSegmentOfTheJobIgnoringCaseAndNullWhenThereIsNone()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[animation job]").Str("[swordman]").Tag("[variation]").Int(1).Int(1);
        b.Tag("[animation job]").Str("[fighter]").Tag("[variation]").Int(2).Int(2);
        b.Tag("[animation job]").Str("[swordman]").Tag("[variation]").Int(3).Int(3);
        EquipLook look = ReadLook(b);
        Assert.Equal(3, look.Segments.Count);
        Assert.Same(look.Segments[0], look.ForJob("[SwordMan]"));
        Assert.Same(look.Segments[1], look.ForJob("[fighter]"));
        Assert.Null(look.ForJob("[priest]"));
        Assert.Null(look.ForJob(null));
    }

    [Fact]
    public void OnlyTheFirstSubTypeCountsAndNoneMeansNone()
    {
        EquipLook two = ReadLook(new TokenStreamBuilder().Tag("[sub type]").Int(5).Tag("[sub type]").Int(9));
        Assert.Equal((true, 5), (two.HasSubType, two.SubType));
        EquipLook none = ReadLook(new TokenStreamBuilder().Tag("[animation job]").Str("[swordman]"));
        Assert.Equal((false, 0), (none.HasSubType, none.SubType));
        EquipLook empty = ReadLook(new TokenStreamBuilder());
        Assert.Equal((0, false, 0), (empty.Segments.Count, empty.HasSubType, empty.NestedTagCount));
    }

    [Fact]
    public void LooksWrittenBeforeTheFirstJobGoToAnUnnamedSegment()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[layer variation]").Int(1).Str("lyr_x");
        b.Tag("[animation job]").Str("[swordman]");
        b.Tag("[layer variation]").Int(2).Str("lyr_y");
        EquipLook look = ReadLook(b);
        Assert.Equal(2, look.Segments.Count);
        Assert.Null(look.Segments[0].Job);
        Assert.Equal(new[] { "lyr_x" }, look.Segments[0].Layers.Select(l => l.Name));
        Assert.Equal(new[] { "lyr_y" }, look.Segments[1].Layers.Select(l => l.Name));
        Assert.Null(look.ForJob(null));                                             // 职业为空的段不算"某个职业"的
    }

    [Fact]
    public void LookTagsInsideABlockAreCountedButNotRead()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[animation job]").Str("[swordman]").Tag("[variation]").Int(1).Int(2);
        b.Tag("[level section ani]");
        b.Tag("[animation job]").Str("[swordman]").Tag("[variation]").Int(8).Int(9).Tag("[layer variation]").Int(5).Str("lyr_alt");
        b.Tag("[/level section ani]");
        b.Tag("[layer variation]").Int(1).Str("lyr_base");
        EquipLook look = ReadLook(b);
        Assert.Single(look.Segments);
        Assert.Equal((1, 2), (look.Segments[0].VariationA, look.Segments[0].VariationB));
        Assert.Equal(new[] { "lyr_base" }, look.Segments[0].Layers.Select(l => l.Name));
        Assert.Equal(3, look.NestedTagCount);                                       // 块里的 [animation job]、[variation]、[layer variation]
    }

    [Fact]
    public void IncompleteLookLinesAreIgnoredAndALaterVariationOverridesAnEarlierOne()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[animation job]").Str("[swordman]");
        b.Tag("[variation]").Int(1).Int(2);
        b.Tag("[variation]").Int(5);                                                // 只有一个整数:不算
        b.Tag("[layer variation]").Int(1);                                          // 没有层名
        b.Tag("[layer variation]").Str("lyr_only_name");                            // 没有顺序键
        b.Tag("[equipment ani script]").Int(4);                                     // 没有路径
        b.Tag("[layer variation]").Int(2).Str("lyr_ok");
        b.Tag("[variation]").Int(7).Int(8);
        LookSegment seg = ReadLook(b).Segments[0];
        Assert.Equal((true, 7, 8), (seg.HasVariation, seg.VariationA, seg.VariationB));
        Assert.Equal(new[] { (2, "lyr_ok") }, seg.Layers.Select(l => (l.Order, l.Name)));
        Assert.Empty(seg.AniScripts);
    }

    [Fact]
    public void LookIntegersMayBeUnsignedIntegerTokensToo()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[animation job]").Str("[swordman]");
        b.Tag("[variation]").UInt(3).UInt(7);
        b.Tag("[layer variation]").UInt(10).Str("lyr_a");
        b.Tag("[sub type]").UInt(5);
        EquipLook look = ReadLook(b);
        LookSegment seg = look.Segments[0];
        Assert.Equal((true, 3, 7), (seg.HasVariation, seg.VariationA, seg.VariationB));
        Assert.Equal(new[] { (10, "lyr_a") }, seg.Layers.Select(l => (l.Order, l.Name)));
        Assert.Equal((true, 5), (look.HasSubType, look.SubType));
    }

    [Fact]
    public void AVariationLineWithOnlyOneIntegerIsNotAVariationAndChangesNothing()
    {
        LookSegment alone = ReadLook(new TokenStreamBuilder().Tag("[animation job]").Str("[swordman]").Tag("[variation]").Int(5)).Segments[0];
        Assert.Equal((false, 0, 0), (alone.HasVariation, alone.VariationA, alone.VariationB));
        LookSegment after = ReadLook(new TokenStreamBuilder().Tag("[animation job]").Str("[swordman]")
            .Tag("[variation]").Int(1).Int(2).Tag("[variation]").Int(5)).Segments[0];
        Assert.Equal((true, 1, 2), (after.HasVariation, after.VariationA, after.VariationB));        // 后来那行只有一个整数:不覆盖
    }

    [Fact]
    public void ASegmentWithoutAVariationSaysSo()
    {
        LookSegment seg = ReadLook(new TokenStreamBuilder().Tag("[animation job]").Str("[swordman]")).Segments[0];
        Assert.Equal((false, 0, 0), (seg.HasVariation, seg.VariationA, seg.VariationB));
    }

    // ------------------------------------------------------------------ 接进物品脚本的读入

    [Fact]
    public void ParsingEquipmentReadsItsStatsAndOnlyWeaponsGetALook()
    {
        TokenStreamBuilder Script(string type)
        {
            var b = new TokenStreamBuilder();
            b.Tag("[equipment type]").Str(type).Int(1);
            b.Tag("[minimum level]").Int(5);
            b.Tag("[physical attack]").Int(12);
            b.Tag("[equipment physical attack]").Int(30).Int(20);
            b.Tag("[animation job]").Str("[swordman]");
            b.Tag("[layer variation]").Int(1).Str("lyr_a");
            return b;
        }

        ItemInfo weapon = Parse(Script("[weapon]"));
        Assert.Equal((EquipmentParts.Weapon, 5), (weapon.Part, weapon.MinimumLevel));        // 头部字段不受影响
        Assert.Equal((12.0, 30.0, 20.0, 2), (weapon.Stats[EquipStat.PhysicalAttack], weapon.Stats[EquipStat.EquipmentPhysicalAttack],
            weapon.Stats.SecondOf(EquipStat.EquipmentPhysicalAttack), weapon.Stats.Count));
        Assert.NotNull(weapon.Look);
        Assert.Equal(new[] { "lyr_a" }, weapon.Look.ForJob("[swordman]").Layers.Select(l => l.Name));

        ItemInfo coat = Parse(Script("[coat]"));
        Assert.Equal(EquipmentParts.Coat, coat.Part);
        Assert.Equal(weapon.Stats, coat.Stats);                                              // 属性与部位无关
        Assert.Null(coat.Look);
    }

    [Fact]
    public void ParsedStatsIgnoreTheBlocksOfSetsAndLevelSections()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[equipment type]").Str("[coat]").Int(1);
        b.Tag("[physical attack]").Int(4);
        b.Tag("[level section ability]").Tag("[physical attack]").Int(40).Tag("[hp max]").Int(400).Tag("[/level section ability]");
        b.Tag("[piece set ability]").Tag("[magical attack]").Int(50).Tag("[/piece set ability]");
        ItemInfo item = Parse(b);
        Assert.Equal(1, item.Stats.Count);
        Assert.Equal(4.0, item.Stats[EquipStat.PhysicalAttack]);
    }

    [Fact]
    public void StackableItemsHaveNeitherStatsNorALookEvenWhenTheirScriptHasStatTags()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[stackable type]").Str("[consumable]");
        b.Tag("[hp max]").Int(100);
        b.Tag("[animation job]").Str("[swordman]");
        ItemInfo item = Parse(b, ItemKind.Stackable);
        Assert.Same(EquipStats.Empty, item.Stats);
        Assert.Null(item.Look);
    }

    [Fact]
    public void ANewItemHasEmptyStatsAndGivingNullStatsMeansEmpty()
    {
        var item = new ItemInfo(902, "equipment/test/x.equ", ItemKind.Equipment);
        Assert.Same(EquipStats.Empty, item.Stats);
        Assert.Null(item.Look);
        item.Stats = EquipStats.Empty.With(EquipStat.HpMax, 1);
        item.Stats = null;
        Assert.Same(EquipStats.Empty, item.Stats);
    }

    // ------------------------------------------------------------------ 装备栏的汇总

    [Fact]
    public void NothingWornTotalsNothing()
    {
        var slots = new EquipmentSlots();
        Assert.Same(EquipStats.Empty, slots.TotalStats());
        Assert.Equal(Zeros, Fields(slots.TotalStatBlock()));
    }

    [Fact]
    public void EachSlotCountsOnceAndOnlyItself()
    {
        for (int part = EquipmentSlots.First; part <= EquipmentSlots.Last; part++)
        {
            var slots = new EquipmentSlots();
            Assert.True(slots.Restore(part, Gear(950 + part, part, EquipStats.Empty.With(EquipStat.HpMax, 100 + part).With(EquipStat.PhysicalAttack, part))));
            EquipStats total = slots.TotalStats();
            Assert.Equal((1 + 1, 100.0 + part, (double)part), (total.Count, total[EquipStat.HpMax], total[EquipStat.PhysicalAttack]));
            Assert.Equal((10L * (100 + part), 10L * part), (slots.TotalStatBlock().HpMax, slots.TotalStatBlock().PhysicalAttack));
        }
    }

    [Fact]
    public void EveryTwelveSlotsAreAllAddedWithoutLosingOrRepeatingAny()
    {
        var slots = new EquipmentSlots();
        double expected = 0;
        for (int part = EquipmentSlots.First; part <= EquipmentSlots.Last; part++)
        {
            int weight = 1 << (part - EquipmentSlots.First);                                  // 每个槽一位:少一个或重复一个都对不上
            expected += weight;
            slots.Restore(part, Gear(960 + part, part, EquipStats.Empty.With(EquipStat.HpMax, weight)));
        }
        Assert.Equal(4095.0, expected);
        Assert.Equal(expected, slots.TotalStats()[EquipStat.HpMax]);
        Assert.Equal(40950L, slots.TotalStatBlock().HpMax);
    }

    [Fact]
    public void WearingAddsAndTakingOffDeducts()
    {
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        ItemInfo weapon = Gear(901, EquipmentParts.Weapon, EquipStats.Empty.With(EquipStat.HpMax, 10).With(EquipStat.PhysicalAttack, 3));
        ItemInfo coat = Gear(902, EquipmentParts.Coat, EquipStats.Empty.With(EquipStat.HpMax, 5).With(EquipStat.PhysicalDefense, 2));
        Assert.Equal(1, bag.Add(weapon, 1));
        Assert.Equal(1, bag.Add(coat, 1));

        Assert.Equal(EquipResult.Ok, EquipActions.Equip(bag, slots, 0, EquipRules.SwordmanJob, 10));
        Assert.Equal((10.0, 3.0), (slots.TotalStats()[EquipStat.HpMax], slots.TotalStats()[EquipStat.PhysicalAttack]));
        Assert.Equal(EquipResult.Ok, EquipActions.Equip(bag, slots, 0, EquipRules.SwordmanJob, 10));
        Assert.Equal((15.0, 3.0, 2.0), (slots.TotalStats()[EquipStat.HpMax], slots.TotalStats()[EquipStat.PhysicalAttack], slots.TotalStats()[EquipStat.PhysicalDefense]));
        Assert.Equal((150L, 30L, 20L), (slots.TotalStatBlock().HpMax, slots.TotalStatBlock().PhysicalAttack, slots.TotalStatBlock().PhysicalDefense));

        Assert.True(EquipActions.Unequip(bag, slots, EquipmentParts.Weapon));
        Assert.Equal((5.0, 0.0, 2.0), (slots.TotalStats()[EquipStat.HpMax], slots.TotalStats()[EquipStat.PhysicalAttack], slots.TotalStats()[EquipStat.PhysicalDefense]));
        Assert.False(slots.TotalStats().Has(EquipStat.PhysicalAttack));                      // 脱下的那件带来的项整个没了,不是留一个 0
        Assert.True(EquipActions.Unequip(bag, slots, EquipmentParts.Coat));
        Assert.Same(EquipStats.Empty, slots.TotalStats());
        Assert.Equal(Zeros, Fields(slots.TotalStatBlock()));
    }

    [Fact]
    public void ReplacingAnItemInTheSameSlotCountsOnlyTheNewOne()
    {
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        ItemInfo a = Gear(901, EquipmentParts.Weapon, EquipStats.Empty.With(EquipStat.HpMax, 10));
        ItemInfo b = Gear(902, EquipmentParts.Weapon, EquipStats.Empty.With(EquipStat.HpMax, 7).With(EquipStat.MoveSpeed, 2));
        bag.Add(a, 1);
        bag.Add(b, 1);
        Assert.Equal(EquipResult.Ok, EquipActions.Equip(bag, slots, 0, EquipRules.SwordmanJob, 10));      // 穿 a
        Assert.Equal((10.0, false), (slots.TotalStats()[EquipStat.HpMax], slots.TotalStats().Has(EquipStat.MoveSpeed)));
        Assert.Equal(EquipResult.Ok, EquipActions.Equip(bag, slots, 0, EquipRules.SwordmanJob, 10));      // 背包装备页现在只有 b:换上,a 回背包
        Assert.Same(b, slots.Get(EquipmentParts.Weapon));
        Assert.Equal((7.0, 2.0, 2), (slots.TotalStats()[EquipStat.HpMax], slots.TotalStats()[EquipStat.MoveSpeed], slots.TotalStats().Count));   // 不是 17
        Assert.Equal(70L, slots.TotalStatBlock().HpMax);
        Assert.Equal(EquipResult.Ok, EquipActions.Equip(bag, slots, 0, EquipRules.SwordmanJob, 10));      // 换回 a
        Assert.Equal((10.0, false, 1), (slots.TotalStats()[EquipStat.HpMax], slots.TotalStats().Has(EquipStat.MoveSpeed), slots.TotalStats().Count));
    }

    [Fact]
    public void RefusedEquipsAndImpossibleSlotsChangeNothing()
    {
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        ItemInfo worn = Gear(901, EquipmentParts.Weapon, EquipStats.Empty.With(EquipStat.HpMax, 10));
        slots.Restore(EquipmentParts.Weapon, worn);
        EquipStats before = slots.TotalStats();
        int version = slots.Version;

        bag.Add(Gear(902, EquipmentParts.Coat, EquipStats.Empty.With(EquipStat.HpMax, 50), job: "[priest]"), 1);       // 职业不符
        Assert.Equal(EquipResult.WrongJob, EquipActions.Equip(bag, slots, 0, EquipRules.SwordmanJob, 10));
        bag.Add(Gear(903, EquipmentParts.Shoes, EquipStats.Empty.With(EquipStat.HpMax, 60), level: 99), 1);           // 等级不够
        Assert.Equal(EquipResult.LevelTooLow, EquipActions.Equip(bag, slots, 1, EquipRules.SwordmanJob, 10));
        Assert.Equal(EquipResult.NoItem, EquipActions.Equip(bag, slots, 5, EquipRules.SwordmanJob, 10));              // 那一格没有东西
        Assert.False(EquipActions.Unequip(bag, slots, EquipmentParts.Ring));                                           // 那一槽是空的
        Assert.False(EquipActions.Unequip(bag, slots, EquipmentSlots.First - 1));                                      // 没有这个槽
        Assert.False(slots.Restore(EquipmentSlots.First - 1, Gear(904, EquipmentParts.Weapon, EquipStats.Empty.With(EquipStat.HpMax, 70))));
        Assert.False(slots.Restore(EquipmentSlots.Last + 1, Gear(905, EquipmentParts.Weapon, EquipStats.Empty.With(EquipStat.HpMax, 80))));
        Assert.False(slots.Restore(EquipmentParts.Coat, null));

        Assert.True(before.Equals(slots.TotalStats()));
        Assert.Equal(10.0, slots.TotalStats()[EquipStat.HpMax]);
        Assert.Equal(version, slots.Version);
    }

    [Fact]
    public void ThePartAnItemSaysItBelongsToDecidesTheSlotItGoesInto()
    {
        // 槽位错位:戒指的属性不能算在武器槽上,更不能因为别的槽有同样的东西就被抵消
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        bag.Add(Gear(901, EquipmentParts.Ring, EquipStats.Empty.With(EquipStat.MpMax, 9)), 1);
        Assert.Equal(EquipResult.Ok, EquipActions.Equip(bag, slots, 0, EquipRules.SwordmanJob, 10));
        Assert.Same(slots.Get(EquipmentParts.Ring), slots.Items.Single());
        Assert.Null(slots.Get(EquipmentParts.Weapon));
        Assert.Null(slots.Get(EquipmentParts.Support));
        Assert.Equal(9.0, slots.TotalStats()[EquipStat.MpMax]);
        Assert.False(EquipActions.Unequip(bag, slots, EquipmentParts.Weapon));                  // 武器槽是空的:脱不下来,汇总不变
        Assert.Equal(9.0, slots.TotalStats()[EquipStat.MpMax]);
    }

    [Fact]
    public void TotalsAreAlwaysTheSumOfThePartsWhateverIsWorn()
    {
        var random = new Random(902);
        for (int n = 0; n < 300; n++)
        {
            var slots = new EquipmentSlots();
            var worn = new List<ItemInfo>();
            for (int part = EquipmentSlots.First; part <= EquipmentSlots.Last; part++)
            {
                if (random.Next(4) == 0)
                {
                    continue;
                }
                ItemInfo item = Gear(1000 + n * 20 + part, part, RandomStats(random));
                slots.Restore(part, item);
                worn.Add(item);
            }
            AssertMatchesReference(slots.TotalStats(), Reference(worn));
            StatBlock blocks = default;
            foreach (ItemInfo item in worn)
            {
                blocks += item.Stats.ToStatBlock();
            }
            Assert.Equal(Fields(blocks), Fields(slots.TotalStatBlock()));
        }
    }

    [Fact]
    public void AnyOrderOfWearingAndTakingOffKeepsTheTotalEqualToWhatIsWorn()
    {
        // 模型对拍:随机穿、脱、换装,模型只记"哪个槽现在是哪件",每一步之后汇总都要等于模型里各件之和
        var random = new Random(903);
        var bag = new Inventory();
        var slots = new EquipmentSlots();
        var model = new Dictionary<int, ItemInfo>();
        int code = 2000;
        for (int step = 0; step < 600; step++)
        {
            while (bag.Tab(InventoryTab.Equipment).Count > 0)                             // 背包每步清空:只留这一步放进去的那件
            {
                Assert.True(bag.RemoveAt(InventoryTab.Equipment, 0, 1));
            }
            if (random.Next(5) < 3)
            {
                int part = EquipmentSlots.First + random.Next(EquipmentSlots.Count);
                bool badJob = random.Next(6) == 0;
                bool badLevel = random.Next(6) == 0;
                ItemInfo item = Gear(code++, part, RandomStats(random), badJob ? "[priest]" : "[swordman]", badLevel ? 99 : 1);
                Assert.Equal(1, bag.Add(item, 1));
                EquipResult result = EquipActions.Equip(bag, slots, 0, EquipRules.SwordmanJob, 10);
                Assert.Equal(badJob ? EquipResult.WrongJob : badLevel ? EquipResult.LevelTooLow : EquipResult.Ok, result);
                if (result == EquipResult.Ok)
                {
                    model[part] = item;
                }
            }
            else
            {
                int part = EquipmentSlots.First + random.Next(EquipmentSlots.Count);
                bool done = EquipActions.Unequip(bag, slots, part);
                Assert.Equal(model.ContainsKey(part), done);
                model.Remove(part);
            }
            AssertMatchesReference(slots.TotalStats(), Reference(model.Values));
        }
        Assert.True(model.Count > 0 || slots.EquippedCount == 0);
        Assert.Equal(model.Count, slots.EquippedCount);
    }
}
