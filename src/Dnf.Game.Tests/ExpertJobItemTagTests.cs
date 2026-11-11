using Dnf.Data;
using static Dnf.Game.Tests.ExpertJobTestData;

namespace Dnf.Game.Tests;

/// <summary>
/// 物品脚本里与副职业、分解有关的标签,以及穿戴装备的加成。依据:服务端 CItem::isExpertJobUsable 0x084e95ce、GetExpertJobTypeList 0x089bf6ca、
/// CInventory::GetExpertJobSelfDisjointBigWinRate 0x0850cae2 / ...ResultVariation 0x0850cb94 / GetExpertJobAddtionalExp 0x0850cc46(静态读码)。
/// 物品与数值全是合成的。
/// </summary>
public class ExpertJobItemTagTests
{
    private static ExpertJobItemTags Tags(params ScriptLine[] lines) => ExpertJobItemTags.Parse(lines);

    // ------------------------------------------------------------------ 读取

    [Fact]
    public void AnItemWithoutTagsHasTheServerDefaults()
    {
        ExpertJobItemTags none = ExpertJobItemTags.None;
        Assert.False(none.ImpossibleDisjoint);
        Assert.False(none.RandomOption);
        Assert.Equal(ExpertJobKind.None, none.ExpertType);
        Assert.Empty(none.Usable);
        Assert.Equal(-1, none.PortableDisjointIndex);
        Assert.Equal(0f, none.BigWinRate);
        Assert.Equal(0f, none.ResultVariation);
        Assert.Equal(0f, none.AdditionalExpChance);
        Assert.Equal(0, none.AdditionalExpAmount);
        Assert.Empty(none.Errors);

        ExpertJobItemTags parsed = Tags(L("[name]", "x"), L("[grade]", 5));
        Assert.False(parsed.ImpossibleDisjoint);
        Assert.Equal(-1, parsed.PortableDisjointIndex);
        Assert.Equal(-1, Tags().PortableDisjointIndex);
        Assert.Equal(-1, ExpertJobItemTags.Parse(null).PortableDisjointIndex);
    }

    [Fact]
    public void ImpossibleContentsMarksDisjointOnlyWhenOneOfTheValuesIsDisjoint()
    {
        Assert.True(Tags(L("[impossible contents]", "disjoint")).ImpossibleDisjoint);
        Assert.True(Tags(L("[impossible contents]", "trade", "disjoint", "enchant")).ImpossibleDisjoint);
        Assert.True(Tags(L("[impossible contents]", "DISJOINT")).ImpossibleDisjoint);
        Assert.False(Tags(L("[impossible contents]", "trade", "enchant")).ImpossibleDisjoint);
        Assert.False(Tags(L("[impossible contents]", "disjointed")).ImpossibleDisjoint);
        Assert.False(Tags(L("[impossible contents]")).ImpossibleDisjoint);
        Assert.False(Tags(L("[impossible contents]", 7)).ImpossibleDisjoint);     // 数不算
    }

    [Fact]
    public void RandomOptionAndTheExpertTypeAndPortableIndexAreRead()
    {
        Assert.True(Tags(L("[random option]", 1)).RandomOption);
        Assert.Equal(ExpertJobKind.Disjointer, Tags(L("[expert type]", "[disjointer]")).ExpertType);
        Assert.Equal(ExpertJobKind.DollController, Tags(L("[expert type]", "[doll_controller]")).ExpertType);
        Assert.Equal(ExpertJobKind.None, Tags(L("[expert type]", "[unknown]")).ExpertType);
        Assert.Equal(1, Tags(L("[portable disjoint]", 1)).PortableDisjointIndex);
        Assert.Equal(0, Tags(L("[portable disjoint]", 0)).PortableDisjointIndex);
        Assert.Equal(-1, Tags(L("[portable disjoint]")).PortableDisjointIndex);       // 标签没带值:保持缺省
    }

    [Fact]
    public void TheProfessionBonusTagsAreReadAsFloatsAndAnInteger()
    {
        ExpertJobItemTags t = Tags(
            L("[prof disjoint big win rate]", 12.5f),
            L("[prof disjoint result variation]", 7),          // 整数写法的数也算浮点
            L("[prof additional gain exp]", 25.0f, 7));
        Assert.Equal(12.5f, t.BigWinRate);
        Assert.Equal(7f, t.ResultVariation);
        Assert.Equal(25f, t.AdditionalExpChance);
        Assert.Equal(7, t.AdditionalExpAmount);

        ExpertJobItemTags chanceOnly = Tags(L("[prof additional gain exp]", 30.0f));
        Assert.Equal(30f, chanceOnly.AdditionalExpChance);
        Assert.Equal(0, chanceOnly.AdditionalExpAmount);
    }

    [Fact]
    public void ExpertJobOnlyReadsNameAndLevelPairsInOrder()
    {
        ExpertJobItemTags t = Tags(L("[expertjob only]", "disjointer", 5, "all", 9, "enchanter", 1, "alchemist", 2, "doll_controller", 3));
        Assert.Empty(t.Errors);
        Assert.Equal(5, t.Usable.Count);
        Assert.Equal(3, t.Usable[0].Type);
        Assert.Equal(5, t.Usable[0].Level);
        Assert.Equal(5, t.Usable[1].Type);        // "all" → 5
        Assert.Equal(9, t.Usable[1].Level);
        Assert.Equal(1, t.Usable[2].Type);
        Assert.Equal(2, t.Usable[3].Type);
        Assert.Equal(4, t.Usable[4].Type);
    }

    [Fact]
    public void AnUnknownExpertJobNameIsAnErrorAndStopsReadingTheTag()
    {
        // 服务端遇到不认得的名字整份物品脚本装载失败;这里记一条错误,保留之前读到的对,后面的不读。
        ExpertJobItemTags t = Tags(L("[expertjob only]", "disjointer", 5, "smith", 3, "enchanter", 1));
        Assert.Single(t.Errors);
        Assert.Contains("smith", t.Errors[0]);
        Assert.Single(t.Usable);
        Assert.Equal(3, t.Usable[0].Type);
    }

    [Fact]
    public void ARepeatedExpertJobOnlyTagReplacesTheEarlierOne()
    {
        ExpertJobItemTags t = Tags(L("[expertjob only]", "disjointer", 5), L("[expertjob only]", "alchemist", 2));
        Assert.Single(t.Usable);
        Assert.Equal(2, t.Usable[0].Type);
    }

    [Fact]
    public void AMalformedExpertJobOnlyPairStopsTheTagWithoutAnError()
    {
        ExpertJobItemTags t = Tags(L("[expertjob only]", "disjointer", 5, 7, "all"));
        Assert.Single(t.Usable);                       // 第二对的名字位置是个数 → 停
        Assert.Empty(t.Errors);
        Assert.Empty(Tags(L("[expertjob only]", "disjointer")).Usable);   // 只有名字没有等级
    }

    // ------------------------------------------------------------------ 谁能用

    [Fact]
    public void AnItemWithoutRestrictionsIsUsableByEveryone()
    {
        Assert.True(ExpertJobItemTags.None.IsUsable(0, 0));
        Assert.True(ExpertJobItemTags.None.IsUsable(3, 99));
    }

    [Fact]
    public void ARestrictedItemNeedsTheNamedJobAndAtLeastTheNamedLevel()
    {
        ExpertJobItemTags t = Tags(L("[expertjob only]", "disjointer", 5));
        Assert.True(t.IsUsable(3, 5));
        Assert.True(t.IsUsable(3, 6));
        Assert.False(t.IsUsable(3, 4));
        Assert.False(t.IsUsable(2, 99));     // 别的副职业
        Assert.False(t.IsUsable(0, 99));     // 没有副职业
    }

    [Fact]
    public void TheAllEntryAdmitsAnyJobButNotNoJob()
    {
        ExpertJobItemTags t = Tags(L("[expertjob only]", "all", 9));
        Assert.True(t.IsUsable(3, 9));
        Assert.True(t.IsUsable(1, 10));
        Assert.False(t.IsUsable(2, 8));
        Assert.False(t.IsUsable(0, 99));
    }

    [Fact]
    public void TheFirstMatchingEntryDecidesEvenIfALaterOneWouldAllowIt()
    {
        // 分解师 4 级:第一对就是 "disjointer 5" → 等级不够,不会再往后看 "all 1"。
        ExpertJobItemTags specificFirst = Tags(L("[expertjob only]", "disjointer", 5, "all", 1));
        Assert.False(specificFirst.IsUsable(3, 4));
        Assert.True(specificFirst.IsUsable(2, 1));       // 别的职业:跳过第一对,"all 1" 放行

        // 反过来 "all 1" 在前:分解师 2 级就放行,即使后面有 "disjointer 50"。
        ExpertJobItemTags allFirst = Tags(L("[expertjob only]", "all", 1, "disjointer", 50));
        Assert.True(allFirst.IsUsable(3, 2));
    }

    // ------------------------------------------------------------------ 穿戴加成

    private static Func<int, ExpertJobItemTags> Worn(params (int part, ExpertJobItemTags tags)[] slots)
    {
        var map = slots.ToDictionary(s => s.part, s => s.tags);
        return part => map.TryGetValue(part, out ExpertJobItemTags t) ? t : null;
    }

    [Fact]
    public void WornBonusesAreSummedOverTheTwelveEquipmentSlotsOnly()
    {
        ExpertJobItemTags a = Tags(L("[prof disjoint big win rate]", 12.5f), L("[prof disjoint result variation]", 10f), L("[prof additional gain exp]", 25f, 7));
        ExpertJobItemTags b = Tags(L("[prof disjoint big win rate]", 7.5f), L("[prof disjoint result variation]", 5f), L("[prof additional gain exp]", 10f, 3));
        ExpertJobItemTags outside = Tags(L("[prof disjoint big win rate]", 100f), L("[prof disjoint result variation]", 100f), L("[prof additional gain exp]", 100f, 100));
        Func<int, ExpertJobItemTags> worn = Worn((10, a), (21, b), (9, outside), (22, outside), (0, outside));

        Assert.Equal(20f, ExpertJobGear.SelfBigWinRate(worn, 3, 5));
        Assert.Equal(15f, ExpertJobGear.SelfResultVariation(worn, 3, 5));
        ExpertJobGear.AdditionalExp(worn, 3, 5, out float chance, out int amount);
        Assert.Equal(35f, chance);
        Assert.Equal(10, amount);
        Assert.Equal(10, ExpertJobGear.FirstSlot);
        Assert.Equal(21, ExpertJobGear.LastSlot);
    }

    [Fact]
    public void AWornPieceTheJobCannotUseAddsNothing()
    {
        ExpertJobItemTags restricted = Tags(L("[expertjob only]", "disjointer", 9), L("[prof disjoint big win rate]", 40f), L("[prof additional gain exp]", 50f, 9));
        ExpertJobItemTags open = Tags(L("[prof disjoint big win rate]", 2f), L("[prof additional gain exp]", 5f, 1));
        Func<int, ExpertJobItemTags> worn = Worn((10, restricted), (11, open));

        Assert.Equal(2f, ExpertJobGear.SelfBigWinRate(worn, 3, 8));            // 等级 8 不够 9
        Assert.Equal(42f, ExpertJobGear.SelfBigWinRate(worn, 3, 9));
        Assert.Equal(2f, ExpertJobGear.SelfBigWinRate(Worn((10, restricted), (11, open)), 2, 99));   // 别的副职业
        ExpertJobGear.AdditionalExp(worn, 3, 8, out float chance, out int amount);
        Assert.Equal(5f, chance);
        Assert.Equal(1, amount);
    }

    [Fact]
    public void WithoutAJobOrWithoutWornItemsEveryBonusIsZero()
    {
        ExpertJobItemTags a = Tags(L("[prof disjoint big win rate]", 12.5f), L("[prof disjoint result variation]", 10f), L("[prof additional gain exp]", 25f, 7));
        Func<int, ExpertJobItemTags> worn = Worn((10, a));
        Assert.Equal(0f, ExpertJobGear.SelfBigWinRate(worn, 0, 5));
        Assert.Equal(0f, ExpertJobGear.SelfResultVariation(worn, 0, 5));
        ExpertJobGear.AdditionalExp(worn, 0, 5, out float chance, out int amount);
        Assert.Equal(0f, chance);
        Assert.Equal(0, amount);

        Assert.Equal(0f, ExpertJobGear.SelfBigWinRate(null, 3, 5));
        Assert.Equal(0f, ExpertJobGear.SelfResultVariation(null, 3, 5));
        ExpertJobGear.AdditionalExp(null, 3, 5, out chance, out amount);
        Assert.Equal(0f, chance);
        Assert.Equal(0, amount);
        Assert.Equal(0f, ExpertJobGear.SelfBigWinRate(Worn(), 3, 5));
    }

    [Fact]
    public void WornBonusesAccumulateInSinglePrecision()
    {
        // 0.1f + 0.2f 在单精度里加出的值,和 float 变量逐件累加一致。
        ExpertJobItemTags a = Tags(L("[prof disjoint big win rate]", 0.1f));
        ExpertJobItemTags b = Tags(L("[prof disjoint big win rate]", 0.2f));
        float expected = 0f;
        expected += 0.1f;
        expected += 0.2f;
        Assert.Equal(expected, ExpertJobGear.SelfBigWinRate(Worn((10, a), (11, b)), 3, 1));
    }
}
