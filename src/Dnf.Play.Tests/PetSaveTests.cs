namespace Dnf.Play.Tests;

/// <summary>
/// 宠物的存档字段(本项目自己的设计,没有原版证据):<c>pet</c> 与 <c>petslot</c> 两种行的文本写出与读回、范围检查、旧存档与不认得的键。
/// 编号与数值全是合成的。
/// </summary>
public class PetSaveTests
{
    private const string Header = "DNFUNITY-SAVE 1\n";

    private static HeroSave Parse(string text)
    {
        Assert.True(HeroSave.TryParse(text, out HeroSave save, out string error), error);
        return save;
    }

    private static HeroSave WithPets()
    {
        var save = new HeroSave { Level = 7, Gold = 123 };
        save.Pets.Add(new SavedPet(1, 950, 30, 80));
        save.Pets.Add(new SavedPet(2, 951, 0, 100));
        save.PetSlots.Add(new SavedPetSlot(22, 1));
        save.PetSlots.Add(new SavedPetSlot(23, 960));
        save.PetSlots.Add(new SavedPetSlot(25, 962));
        return save;
    }

    [Fact]
    public void PetsAndSlotsAreWrittenOnePerLineInOrder()
    {
        string text = WithPets().ToText();
        Assert.Contains("pet 1 950 30 80\n", text);
        Assert.Contains("pet 2 951 0 100\n", text);
        Assert.Contains("petslot 22 1\n", text);
        Assert.Contains("petslot 23 960\n", text);
        Assert.Contains("petslot 25 962\n", text);
        Assert.True(text.IndexOf("pet 1 950", StringComparison.Ordinal) < text.IndexOf("pet 2 951", StringComparison.Ordinal));
        Assert.True(text.IndexOf("pet 2 951", StringComparison.Ordinal) < text.IndexOf("petslot 22", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTextRoundTripsFieldForField()
    {
        string text = WithPets().ToText();
        HeroSave back = Parse(text);
        Assert.Equal(2, back.Pets.Count);
        Assert.Equal(new SavedPet(1, 950, 30, 80), back.Pets[0]);
        Assert.Equal(new SavedPet(2, 951, 0, 100), back.Pets[1]);
        Assert.Equal(3, back.PetSlots.Count);
        Assert.Equal(new SavedPetSlot(22, 1), back.PetSlots[0]);
        Assert.Equal(new SavedPetSlot(23, 960), back.PetSlots[1]);
        Assert.Equal(new SavedPetSlot(25, 962), back.PetSlots[2]);
        Assert.Equal(7, back.Level);
        Assert.Equal(123L, back.Gold);
        Assert.Equal(text, back.ToText());
    }

    [Fact]
    public void ASaveWithoutPetLinesHasNoPetsAndWritesNone()
    {
        string text = new HeroSave().ToText();
        Assert.DoesNotContain("pet", text, StringComparison.Ordinal);
        HeroSave back = Parse(text);
        Assert.Empty(back.Pets);
        Assert.Empty(back.PetSlots);
        // 旧存档(只有头一行)照样读得懂。
        HeroSave old = Parse(Header + "level 3\n");
        Assert.Equal(3, old.Level);
        Assert.Empty(old.Pets);
    }

    [Fact]
    public void PetLinesDoNotDisturbTheOtherSections()
    {
        HeroSave save = WithPets();
        save.Bag.Add(new SavedStack(5, 1));
        save.Worn.Add(new SavedWorn(10, 5));
        save.Quick.Add(new SavedQuick(0, 100));
        string text = save.ToText();
        HeroSave back = Parse(text);
        Assert.Single(back.Bag);
        Assert.Single(back.Worn);
        Assert.Equal(10, back.Worn[0].Part);
        Assert.Single(back.Quick);
        Assert.Equal(2, back.Pets.Count);
        Assert.Equal(3, back.PetSlots.Count);
        Assert.Equal(text, back.ToText());
    }

    [Fact]
    public void UnknownNeighbourKeysAreStillIgnored()
    {
        HeroSave back = Parse(Header + "petx 1 2 3\npets 1\npet 1 950 30 80\n");
        Assert.Single(back.Pets);
        Assert.Empty(back.PetSlots);
    }

    [Theory]
    [InlineData("pet 1 950 30 80", 1, 950, 30, 80)]
    [InlineData("pet 1 1 0 0", 1, 1, 0, 0)]
    [InlineData("pet 2147483647 2147483647 2147483647 100", int.MaxValue, int.MaxValue, int.MaxValue, 100)]
    public void PetRowsAtTheBoundariesAreAccepted(string line, int uid, int item, int exp, int stomach)
    {
        HeroSave back = Parse(Header + line + "\n");
        Assert.Equal(new SavedPet(uid, item, exp, stomach), Assert.Single(back.Pets));
    }

    [Theory]
    [InlineData("petslot 22 1", 22, 1)]
    [InlineData("petslot 23 960", 23, 960)]
    [InlineData("petslot 25 2147483647", 25, int.MaxValue)]
    public void PetSlotRowsAtTheBoundariesAreAccepted(string line, int part, int value)
    {
        HeroSave back = Parse(Header + line + "\n");
        Assert.Equal(new SavedPetSlot(part, value), Assert.Single(back.PetSlots));
    }

    [Theory]
    [InlineData("pet 1 950 30")]              // 少一个值
    [InlineData("pet 1 950 30 80 7")]         // 多一个值
    [InlineData("pet")]                        // 一个值也没有
    [InlineData("pet x 950 30 80")]           // 不是整数
    [InlineData("pet 1 950 3.5 80")]          // 小数
    [InlineData("pet 0 950 30 80")]           // 实例号从 1 起
    [InlineData("pet -1 950 30 80")]
    [InlineData("pet 1 0 30 80")]             // 物品编号从 1 起
    [InlineData("pet 1 950 -1 80")]           // 经验不能是负的
    [InlineData("pet 1 950 30 101")]          // 饱食最多 100
    [InlineData("pet 1 950 30 -1")]           // 饱食不能是负的
    [InlineData("pet 1 950 2147483648 80")]   // 超出整数
    [InlineData("pet 2147483648 950 30 80")]
    [InlineData("petslot 22")]
    [InlineData("petslot 22 1 5")]
    [InlineData("petslot x 1")]
    [InlineData("petslot 22 x")]
    [InlineData("petslot 21 1")]              // 部位号只有 22 到 25
    [InlineData("petslot 26 1")]
    [InlineData("petslot 0 1")]
    [InlineData("petslot 22 0")]              // 值从 1 起
    [InlineData("petslot 23 -5")]
    public void ABadPetRowMakesTheWholeSaveUnreadableWithALineNumber(string line)
    {
        // 前面垫一行合法的内容,错误应报在第 3 行(头一行是第 1 行)。
        Assert.False(HeroSave.TryParse(Header + "level 2\n" + line + "\n", out HeroSave save, out string error));
        Assert.Null(save);
        Assert.StartsWith("第 3 行", error);
        Assert.Contains(line.Split(' ')[0], error);
    }
}
