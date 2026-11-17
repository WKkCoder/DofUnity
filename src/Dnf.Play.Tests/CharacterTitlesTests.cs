using Dnf.Data;

namespace Dnf.Play.Tests;

/// <summary>
/// 职业称号的来源(<see cref="DnfCharacterTitles"/>):职业号 → 角色脚本路径(<see cref="DnfCharacterTitles.ChrPathOf"/>)、
/// 脚本头部 <c>[growtype name]</c> 的下标 = 一转、各 <c>[growtype N]</c> 块里 <c>[awakening name]</c> 属于一转 N−1(一觉、二觉),
/// 文字取自角色文字表。脚本与文字表都是合成的;下面的结构取自在用户数据上核对过的真实脚本的形状,不含任何真实文字。
/// </summary>
public class CharacterTitlesTests
{
    private static ScriptLine Keyed(string tag, params string[] keys)
    {
        return new ScriptLine(tag, keys.Select(k => new ScriptValue(ScriptValueKind.Keyed, 0, k)).ToArray());
    }

    private static ScriptLine Plain(string tag) => new ScriptLine(tag, new ScriptValue[0]);

    /// <summary>合成的角色文字表(GB18030):g0 到 g2 是一转名,g3 是注释行,g4 是空值,a21、a22 是觉醒名。</summary>
    private static StrLibrary Strings()
    {
        byte[] table = ItemTextsTests.Gbk("// header\r\ng0>基础职业\r\ng1>一转甲\r\ng2>一转乙\r\ng3>//注释\r\ng4>\r\na21>一觉甲\r\na22>二觉甲\r\nb31>一觉乙\r\n");
        return new StrLibrary(p => p == "character/character.kor.str" ? table : null);
    }

    /// <summary>一个职业的合成角色脚本:六个一转引用(第 3 个是注释、第 4 个空、第 5 个文字表里没有)、两个块(块 2 有觉醒名)。</summary>
    private static ScriptLine[] Script()
    {
        return new[]
        {
            Plain("[body image path]"),
            Keyed("[growtype name]", "g0", "g1", "g2", "g3", "g4", "missing"),
            Keyed("[growtype name]", "g2", "g2", "g2"),                 // 第二个 [growtype name] 不算
            Plain("[growtype 1]"),
            Plain("[growtype 2]"),
            Keyed("[awakening name]", "a21", "a22"),
            Keyed("[awakening name]", "b31", "b31"),                    // 同一块里的第二个不算
            Plain("[growtype 3]"),
            Keyed("[awakening name]", "b31"),                           // 块 3 的觉醒名属于一转 2
        };
    }

    private static (DnfCharacterTitles Titles, List<string> Reads) Make(Dictionary<string, ScriptLine[]> scripts, StrLibrary strings = null)
    {
        var reads = new List<string>();
        var titles = new DnfCharacterTitles(path =>
        {
            reads.Add(path);
            return scripts.TryGetValue(path, out ScriptLine[] script) ? script : null;
        }, strings ?? Strings());
        return (titles, reads);
    }

    // ------------------------------------------------------------------ 路径

    [Fact]
    public void EveryJobHasAChrPathBuiltFromItsTag()
    {
        string[] expected =
        {
            "character/swordman/swordman.chr", "character/fighter/fighter.chr", "character/gunner/gunner.chr", "character/mage/mage.chr",
            "character/priest/priest.chr", "character/gunner/atgunner.chr", "character/thief/thief.chr", "character/fighter/atfighter.chr",
            "character/mage/atmage.chr", "character/swordman/demonicswordman.chr", "character/mage/creatormage.chr",
        };
        for (int job = 0; job < expected.Length; job++)
        {
            Assert.Equal(expected[job], DnfCharacterTitles.ChrPathOf(job));
        }
    }

    [Fact]
    public void JobsOutsideTheElevenHaveNoPath()
    {
        Assert.Null(DnfCharacterTitles.ChrPathOf(-1));
        Assert.Null(DnfCharacterTitles.ChrPathOf(11));
        Assert.Null(DnfCharacterTitles.ChrPathOf(255));
    }

    // ------------------------------------------------------------------ 一转

    [Fact]
    public void TheGrowthNameIsTheEntryAtTheGrowthIndex()
    {
        var (titles, _) = Make(new() { ["character/swordman/swordman.chr"] = Script() });
        Assert.Equal("基础职业", titles.ClassName(0, 0, 0));
        Assert.Equal("一转甲", titles.ClassName(0, 1, 0));
        Assert.Equal("一转乙", titles.ClassName(0, 2, 0));
    }

    [Fact]
    public void CommentEmptyMissingAndOutOfRangeNamesAreNull()
    {
        var (titles, _) = Make(new() { ["character/swordman/swordman.chr"] = Script() });
        Assert.Null(titles.ClassName(0, 3, 0));                  // 值是 // 开头的注释
        Assert.Null(titles.ClassName(0, 4, 0));                  // 值是空的
        Assert.Null(titles.ClassName(0, 5, 0));                  // 文字表里没有这个键
        Assert.Null(titles.ClassName(0, 6, 0));                  // 超出脚本给的个数
        Assert.Null(titles.ClassName(0, 15, 0));
        Assert.Null(titles.ClassName(0, -1, 0));
        Assert.Null(titles.ClassName(0, 0, -1));
    }

    [Fact]
    public void OnlyTheFirstGrowthNameLineCounts()
    {
        var (titles, _) = Make(new() { ["character/swordman/swordman.chr"] = Script() });
        Assert.Equal("一转甲", titles.ClassName(0, 1, 0));       // 第二个 [growtype name] 里写的全是 g2,不能顶替
    }

    // ------------------------------------------------------------------ 觉醒

    [Fact]
    public void AwakeningNamesBelongToTheGrowthOneBelowTheBlockNumber()
    {
        var (titles, _) = Make(new() { ["character/swordman/swordman.chr"] = Script() });
        Assert.Equal("一觉甲", titles.ClassName(0, 1, 1));       // [growtype 2] 块 → 一转 1
        Assert.Equal("二觉甲", titles.ClassName(0, 1, 2));
        Assert.Equal("一觉乙", titles.ClassName(0, 2, 1));       // [growtype 3] 块 → 一转 2
    }

    [Fact]
    public void AnAwakeningWithoutANameDoesNotBorrowTheGrowthName()
    {
        var (titles, _) = Make(new() { ["character/swordman/swordman.chr"] = Script() });
        Assert.Null(titles.ClassName(0, 1, 3));                  // 一转 1 只有两个觉醒名
        Assert.Null(titles.ClassName(0, 2, 2));                  // 一转 2 只有一个
        Assert.Null(titles.ClassName(0, 5, 1));                  // 一转 5 没有觉醒块
    }

    [Fact]
    public void ABaseClassWithAnAwakeningStageShowsTheBaseName()
    {
        // 觉醒阶段 > 0 却没转职(服务端不会这样给):退回基础职业名,不抛、不乱编
        var (titles, _) = Make(new() { ["character/swordman/swordman.chr"] = Script() });
        Assert.Equal("基础职业", titles.ClassName(0, 0, 2));
    }

    [Fact]
    public void BlockNumbersAreMatchedByTheirTag()
    {
        // 块号只认 "[growtype 数字]";[growtype name] 与别的写法不是块
        ScriptLine[] script =
        {
            Keyed("[growtype name]", "g0", "g1", "g2"),
            Plain("[growtype name]"), Plain("[growtype]"), Plain("[growtype x]"), Plain("[growtype -2]"),
            Keyed("[awakening name]", "a21"),                    // 前面没有合法的块号 ≥ 2:不属于任何一转
            Plain("[GROWTYPE 2]"),
            Keyed("[AWAKENING NAME]", "a21", "a22"),             // 标签不分大小写
        };
        var (titles, _) = Make(new() { ["character/fighter/fighter.chr"] = script });
        Assert.Equal("一觉甲", titles.ClassName(1, 1, 1));
        Assert.Equal("二觉甲", titles.ClassName(1, 1, 2));
        Assert.Equal("基础职业", titles.ClassName(1, 0, 1));    // 一转 0 没有觉醒名(也不会被前面没有块号的那行 [awakening name] 占去)
    }

    /// <summary>另一张合成的角色文字表(GB18030):b0、b1 是基础职业与一转名,k11、k12 写在 [growtype 1] 块里,k21、k22 写在 [growtype 2] 块里。</summary>
    private static StrLibrary BlockStrings()
    {
        byte[] table = ItemTextsTests.Gbk("// header\r\nb0>基础职业丙\r\nb1>一转丁\r\nk11>壹块觉醒甲\r\nk12>壹块觉醒乙\r\nk21>贰块觉醒甲\r\nk22>贰块觉醒乙\r\n");
        return new StrLibrary(p => p == "character/character.kor.str" ? table : null);
    }

    [Fact]
    public void TheFirstGrowthBlockHasNoAwakeningNamesEvenIfTheScriptWritesSome()
    {
        // [growtype 1] 是基础职业的成长表,没有觉醒名;[growtype N](N ≥ 2)才属于一转 N − 1
        ScriptLine[] script =
        {
            Keyed("[growtype name]", "b0", "b1"),
            Plain("[growtype 1]"),
            Keyed("[awakening name]", "k11", "k12"),                // 写在块 1 里:不属于任何一转,更不是基础职业的觉醒名
            Plain("[growtype 2]"),
            Keyed("[awakening name]", "k21", "k22"),                // 块 2 → 一转 1
        };
        var titles = new DnfCharacterTitles(path => script, BlockStrings());

        Assert.Equal("基础职业丙", titles.ClassName(0, 0, 0));
        Assert.Equal("基础职业丙", titles.ClassName(0, 0, 1));      // 基础职业没有觉醒名:回落到一转名表第 0 项,不是块 1 里的 k11
        Assert.Equal("基础职业丙", titles.ClassName(0, 0, 2));      // 也不是 k12
        Assert.Equal("贰块觉醒甲", titles.ClassName(0, 1, 1));      // 一转 1 的一觉:块 2 的第一个名字
        Assert.Equal("贰块觉醒乙", titles.ClassName(0, 1, 2));      // 二觉:第二个
        Assert.Null(titles.ClassName(0, 1, 3));                     // 只有两个觉醒名
    }

    // ------------------------------------------------------------------ 职业各读各的

    [Fact]
    public void EachJobReadsItsOwnScriptOnceAndCachesIt()
    {
        var scripts = new Dictionary<string, ScriptLine[]>
        {
            ["character/swordman/swordman.chr"] = Script(),
            ["character/gunner/atgunner.chr"] = new[] { Keyed("[growtype name]", "g2", "g1") },
        };
        var (titles, reads) = Make(scripts);

        Assert.Equal("基础职业", titles.ClassName(0, 0, 0));
        Assert.Equal("一转乙", titles.ClassName(5, 0, 0));       // 职业 5 是 [at gunner],读 atgunner.chr
        Assert.Equal("一转甲", titles.ClassName(5, 1, 0));
        Assert.Equal("一转甲", titles.ClassName(0, 1, 0));
        Assert.Equal(new[] { "character/swordman/swordman.chr", "character/gunner/atgunner.chr" }, reads);
    }

    [Fact]
    public void AMissingScriptOrAJobOutOfRangeGivesNothingAndIsNotReadAgain()
    {
        var (titles, reads) = Make(new());
        Assert.Null(titles.ClassName(3, 1, 0));
        Assert.Null(titles.ClassName(3, 0, 0));
        Assert.Single(reads);
        Assert.Null(titles.ClassName(11, 0, 0));                 // 职业号越界:没有路径,也不去读
        Assert.Null(titles.ClassName(-5, 0, 0));
        Assert.Single(reads);
    }

    [Fact]
    public void UnreadableDataIsTreatedAsNotFound()
    {
        var calls = 0;
        var titles = new DnfCharacterTitles(path =>
        {
            calls++;
            throw new DnfFormatException("数据损坏");
        }, Strings());
        Assert.Null(titles.ClassName(0, 0, 0));
        Assert.Null(titles.ClassName(0, 1, 0));
        Assert.Equal(1, calls);

        var io = new DnfCharacterTitles(path => throw new IOException("读不了"), Strings());
        Assert.Null(io.ClassName(0, 0, 0));
        var invalid = new DnfCharacterTitles(path => throw new InvalidOperationException("状态不对"), Strings());
        Assert.Null(invalid.ClassName(0, 0, 0));
    }

    [Fact]
    public void ANullLineOrATaglessLineInTheScriptIsSkipped()
    {
        ScriptLine[] script = { null, new ScriptLine(null, new ScriptValue[0]), Keyed("[growtype name]", "g0", "g1") };
        var (titles, _) = Make(new() { ["character/swordman/swordman.chr"] = script });
        Assert.Equal("一转甲", titles.ClassName(0, 1, 0));
    }

    [Fact]
    public void TextTableMissingGivesNoNames()
    {
        var (titles, _) = Make(new() { ["character/swordman/swordman.chr"] = Script() }, new StrLibrary(p => null));
        Assert.Null(titles.ClassName(0, 0, 0));
        Assert.Null(titles.ClassName(0, 1, 1));
    }

    // ------------------------------------------------------------------ 默认名与构造

    [Fact]
    public void TheDefaultNamesAreTextForTheRealSourceAndEnglishForThePlaceholder()
    {
        var (titles, _) = Make(new());
        Assert.Equal("无名", titles.DefaultName);
        Assert.Equal("NONAME", PlaceholderTitles.Instance.DefaultName);
        Assert.Null(PlaceholderTitles.Instance.ClassName(0, 1, 1));
    }

    [Fact]
    public void ConstructorArgumentsAreRequired()
    {
        Assert.Throws<ArgumentNullException>(() => new DnfCharacterTitles(null, Strings()));
        Assert.Throws<ArgumentNullException>(() => new DnfCharacterTitles(p => null, null));
    }
}
