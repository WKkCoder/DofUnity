using Dnf.Game;

namespace Dnf.Play.Tests;

/// <summary>
/// 装备个体状态的存档字段(本项目自己的设计,没有原版证据):<c>equipstate</c> 行的文本写出与读回、范围检查、旧存档与不认得的键。
/// 编号与数值全是合成的。
/// </summary>
public class EnhanceSaveTests
{
    private const string Header = "DNFUNITY-SAVE 1\n";

    private static HeroSave Parse(string text)
    {
        Assert.True(HeroSave.TryParse(text, out HeroSave save, out string error), error);
        return save;
    }

    private static HeroSave WithStates()
    {
        var save = new HeroSave { Level = 7, Gold = 123 };
        save.EquipStates.Add(new SavedEquipState(0, 0, 900, 3, 0, 0, 0));
        save.EquipStates.Add(new SavedEquipState(0, 2, 900, 12, 3, 931, 77));
        save.EquipStates.Add(new SavedEquipState(1, 10, 901, 0, 0, 931, 0));
        save.EquipStates.Add(new SavedEquipState(1, 21, 902, 255, 7, 2147483647, 2147483647));
        return save;
    }

    [Fact]
    public void StatesAreWrittenOnePerLineInOrder()
    {
        string text = WithStates().ToText();
        Assert.Contains("equipstate 0 0 900 3 0 0 0\n", text);
        Assert.Contains("equipstate 0 2 900 12 3 931 77\n", text);
        Assert.Contains("equipstate 1 10 901 0 0 931 0\n", text);
        Assert.Contains("equipstate 1 21 902 255 7 2147483647 2147483647\n", text);
        Assert.True(text.IndexOf("equipstate 0 0 900", StringComparison.Ordinal) < text.IndexOf("equipstate 0 2 900", StringComparison.Ordinal));
        Assert.True(text.IndexOf("equipstate 0 2 900", StringComparison.Ordinal) < text.IndexOf("equipstate 1 10 901", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTextRoundTripsFieldForField()
    {
        string text = WithStates().ToText();
        HeroSave back = Parse(text);
        Assert.Equal(4, back.EquipStates.Count);
        Assert.Equal(new SavedEquipState(0, 0, 900, 3, 0, 0, 0), back.EquipStates[0]);
        Assert.Equal(new SavedEquipState(0, 2, 900, 12, 3, 931, 77), back.EquipStates[1]);
        Assert.Equal(new SavedEquipState(1, 10, 901, 0, 0, 931, 0), back.EquipStates[2]);
        Assert.Equal(new SavedEquipState(1, 21, 902, 255, 7, int.MaxValue, int.MaxValue), back.EquipStates[3]);
        Assert.Equal((7, 123L), (back.Level, back.Gold));
        Assert.Equal(text, back.ToText());
    }

    [Fact]
    public void ASaveWithoutStatesWritesNoRowsAndStillReads()
    {
        string text = new HeroSave().ToText();
        Assert.DoesNotContain("equipstate", text, StringComparison.Ordinal);
        Assert.Empty(Parse(text).EquipStates);
        // 旧存档(只有头一行与几个字段)照样读得懂
        HeroSave old = Parse(Header + "level 3\n");
        Assert.Equal(3, old.Level);
        Assert.Empty(old.EquipStates);
    }

    [Fact]
    public void StateRowsDoNotDisturbTheOtherSections()
    {
        HeroSave save = WithStates();
        save.Bag.Add(new SavedStack(5, 1));
        save.Worn.Add(new SavedWorn(10, 5));
        save.Quick.Add(new SavedQuick(0, 100));
        save.Pets.Add(new SavedPet(1, 950, 30, 80));
        save.PetSlots.Add(new SavedPetSlot(22, 1));
        string text = save.ToText();
        HeroSave back = Parse(text);
        Assert.Single(back.Bag);
        Assert.Single(back.Worn);
        Assert.Single(back.Quick);
        Assert.Single(back.Pets);
        Assert.Single(back.PetSlots);
        Assert.Equal(4, back.EquipStates.Count);
        Assert.Equal(text, back.ToText());
    }

    [Fact]
    public void UnknownNeighbourKeysAreStillIgnored()
    {
        HeroSave back = Parse(Header + "equipstatex 1 2 3\nequipstates 1\nequipstate 0 0 900 3 0 0 0\n");
        Assert.Single(back.EquipStates);
    }

    [Theory]
    [InlineData("equipstate 0 0 900 0 0 0 0", 0, 0, 900, 0, 0, 0, 0)]                                         // 最小的状态行
    [InlineData("equipstate 0 55 1 255 7 2147483647 2147483647", 0, 55, 1, 255, 7, int.MaxValue, int.MaxValue)]
    [InlineData("equipstate 0 2147483647 2147483647 31 1 0 0", 0, int.MaxValue, int.MaxValue, 31, 1, 0, 0)]    // 背包序号可以很大(对不上的在还原时丢)
    [InlineData("equipstate 1 10 5 1 2 3 4", 1, 10, 5, 1, 2, 3, 4)]
    [InlineData("equipstate 1 21 5 1 4 3 4", 1, 21, 5, 1, 4, 3, 4)]
    public void StateRowsAtTheBoundariesAreAccepted(string line, int place, int index, int code, int upgrade, int flags, int enchant, int grade)
    {
        HeroSave back = Parse(Header + line + "\n");
        Assert.Equal(new SavedEquipState(place, index, code, upgrade, flags, enchant, grade), Assert.Single(back.EquipStates));
    }

    [Theory]
    [InlineData("equipstate 0 0 900 3 0 0")]               // 少一个值
    [InlineData("equipstate 0 0 900 3 0 0 0 0")]           // 多一个值
    [InlineData("equipstate")]                              // 一个值也没有
    [InlineData("equipstate x 0 900 3 0 0 0")]              // 不是整数
    [InlineData("equipstate 0 0 900 3.5 0 0 0")]            // 小数
    [InlineData("equipstate 2 0 900 3 0 0 0")]              // 位置只有 0 与 1
    [InlineData("equipstate 2 10 900 3 0 0 0")]             // 位置 2 配上合法的部位号也不行(不能靠序号范围挡住)
    [InlineData("equipstate -1 0 900 3 0 0 0")]
    [InlineData("equipstate 0 -1 900 3 0 0 0")]             // 背包序号不能是负的
    [InlineData("equipstate 1 9 900 3 0 0 0")]              // 身上的部位号只有 10 到 21
    [InlineData("equipstate 1 22 900 3 0 0 0")]
    [InlineData("equipstate 1 0 900 3 0 0 0")]
    [InlineData("equipstate 0 0 0 3 0 0 0")]                // 物品编号从 1 起
    [InlineData("equipstate 0 0 -5 3 0 0 0")]
    [InlineData("equipstate 0 0 900 -1 0 0 0")]             // 强化等级是一个字节
    [InlineData("equipstate 0 0 900 256 0 0 0")]
    [InlineData("equipstate 0 0 900 3 -1 0 0")]            // 标志只有三位
    [InlineData("equipstate 0 0 900 3 8 0 0")]
    [InlineData("equipstate 0 0 900 3 0 -1 0")]            // 附魔编号不能是负的
    [InlineData("equipstate 0 0 900 3 0 0 -1")]            // 品级值不能是负的
    [InlineData("equipstate 0 0 900 3 0 2147483648 0")]    // 超出整数
    [InlineData("equipstate 0 2147483648 900 3 0 0 0")]
    public void ABadStateRowMakesTheWholeSaveUnreadableWithALineNumber(string line)
    {
        // 前面垫一行合法的内容,错误应报在第 3 行(头一行是第 1 行)。
        Assert.False(HeroSave.TryParse(Header + "level 2\n" + line + "\n", out HeroSave save, out string error));
        Assert.Null(save);
        Assert.StartsWith("第 3 行", error);
        Assert.Contains("equipstate", error);
    }

    [Fact]
    public void TheFlagBitsMapToTheStateAndBack()
    {
        Assert.Equal(1, SavedEquipState.FlagAmplified);
        Assert.Equal(2, SavedEquipState.FlagUnidentified);
        Assert.Equal(4, SavedEquipState.FlagDamaged);
        var all = new EquipInstance(9, amplified: true, identified: false, enchantCode: 931, gradeValue: 5, damaged: true);
        SavedEquipState saved = SavedEquipState.From(EnhancePlace.Worn, 12, 900, all);
        Assert.Equal(new SavedEquipState(1, 12, 900, 9, 7, 931, 5), saved);
        Assert.Equal(all, saved.ToInstance());
        SavedEquipState plain = SavedEquipState.From(EnhancePlace.Bag, 1, 900, new EquipInstance(2));
        Assert.Equal(new SavedEquipState(0, 1, 900, 2, 0, 0, 0), plain);
        Assert.Equal(new EquipInstance(2), plain.ToInstance());
        // 只有增幅标志:已鉴定
        Assert.Equal(new EquipInstance(0, amplified: true), new SavedEquipState(0, 0, 900, 0, 1, 0, 0).ToInstance());
    }
}
