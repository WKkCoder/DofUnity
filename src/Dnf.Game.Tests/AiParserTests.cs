using Dnf.Data;
using Dnf.Game.Ai;

namespace Dnf.Game.Tests;

/// <summary>
/// .ai 脚本解析器(AiParser)的测试。规则依据 E-052(客户端加载函数的读法 + 全库 7,104 个文件的读通率);
/// 脚本内容全是合成的,不含原版内容。
/// </summary>
public sealed class AiParserTests
{
    /// <summary>"[" 开头的字符串当标签,别的字符串当字符串标记,int 当整数,float 当浮点。</summary>
    private static (ScriptToken[] Tokens, PvfStringTable Strings) Script(params object[] items)
    {
        var b = new TokenStreamBuilder();
        foreach (object item in items)
        {
            switch (item)
            {
                case string s when s.StartsWith('['):
                    b.Tag(s);
                    break;
                case string s:
                    b.Str(s);
                    break;
                case int i:
                    b.Int(i);
                    break;
                case float f:
                    b.Float(f);
                    break;
                default:
                    throw new ArgumentException("不支持的标记类型");
            }
        }
        return b.Build();
    }

    private static AiScript Parse(AiConstants constants, params object[] items)
    {
        (ScriptToken[] tokens, PvfStringTable strings) = Script(items);
        return AiParser.Parse(tokens, strings, constants);
    }

    private static AiConstants Constants(params (string Name, float Value)[] pairs)
    {
        var c = new AiConstants();
        foreach ((string name, float value) in pairs)
        {
            c.Add(name, value);
        }
        return c;
    }

    [Fact]
    public void ASimpleThinkWithTwoBranches()
    {
        AiScript s = Parse(null,
            "[ai pattern]", "[think]", "[hp]", "%<=", 30, "[true]", "[return]", 3, "[false]", "[return]", -1, "[/think]", "[/ai pattern]");

        Assert.True(s.Clean);
        Assert.Equal(AiResultKind.Think, s.Root.Kind);
        AiNode node = s.Root.Node!;
        Assert.Equal("[hp]", node.Keyword);
        Assert.Equal("%<=", node.Operator);
        Assert.Equal(10, node.Code);
        Assert.Equal(30f, Assert.Single(node.Params).Value);
        Assert.Equal(AiResultKind.Number, node.True.Kind);
        Assert.Equal(3f, node.True.Number);
        Assert.Equal(-1f, node.False.Number);
        Assert.Equal(1, s.NodeCount);
        Assert.Equal(new[] { "[/ai pattern]" }, s.Trailing);
    }

    [Fact]
    public void BranchesCanComeInEitherOrderAndBeOmitted()
    {
        AiScript s = Parse(null,
            "[think]", "[hp]", ">=", 5, "[false]", "[return]", 7, "[/think]");

        AiNode node = s.Root.Node!;
        Assert.Equal(AiResultKind.Default, node.True.Kind);
        Assert.Equal(-1f, node.True.Number);
        Assert.Equal(7f, node.False.Number);
        Assert.Empty(s.Trailing);
    }

    [Fact]
    public void NestedThinkInABranchConsumesItsOwnCloser()
    {
        AiScript s = Parse(null,
            "[think]", "[hp]", ">=", 5,
            "[true]", "[think]", "[mp]", ">=", 1, "[true]", "[return]", 1, "[false]", "[return]", 2, "[/think]",
            "[false]", "[return]", 9,
            "[/think]");

        AiNode outer = s.Root.Node!;
        Assert.Equal(AiResultKind.Think, outer.True.Kind);
        Assert.Equal("[mp]", outer.True.Node!.Keyword);
        Assert.Equal(2f, outer.True.Node!.False.Number);
        Assert.Equal(9f, outer.False.Number);
        Assert.Equal(2, s.NodeCount);
        Assert.Equal(2, s.MaxDepth);
        Assert.Empty(s.Trailing);
    }

    [Fact]
    public void AThinkRightAfterTheConditionIsTheTrueBranch()
    {
        // 没写 [true] 的嵌套块算"真"分支,读完以后外层再读一个标记当作自己的 [/think]
        AiScript s = Parse(null,
            "[think]", "[void]", "get random()", 0, 100,
            "[think]", "[last result]", "<=", 50, "[true]", "[return]", 1, "[/think]",
            "[/think]", "[/ai pattern]");

        AiNode outer = s.Root.Node!;
        Assert.Equal(AiResultKind.Think, outer.True.Kind);
        Assert.Equal("[last result]", outer.True.Node!.Keyword);
        Assert.Equal(1f, outer.True.Node!.True.Number);
        Assert.Equal(AiResultKind.Default, outer.False.Kind);
        Assert.Equal(new[] { "[/ai pattern]" }, s.Trailing);
    }

    [Fact]
    public void ReturnStrAndImportBecomeNamedAndImportResults()
    {
        AiScript s = Parse(null,
            "[think]", "[hp]", ">=", 5, "[true]", "[return str]", "combo", "[false]", "[import ai]", "other.ai", "[/think]");

        AiNode node = s.Root.Node!;
        Assert.Equal(AiResultKind.Name, node.True.Kind);
        Assert.Equal("combo", node.True.Name);
        Assert.Equal(AiResultKind.Import, node.False.Kind);
        Assert.Equal("other.ai", node.False.Name);
        Assert.True(s.Clean);
    }

    [Fact]
    public void ImportRightAfterTheConditionIsTheTrueBranchAndEndsTheNode()
    {
        AiScript s = Parse(null, "[think]", "[hp]", ">=", 5, "[import ai]", "x.ai", "[false]", "[return]", 1);

        AiNode node = s.Root.Node!;
        Assert.Equal(AiResultKind.Import, node.True.Kind);
        Assert.Equal(AiResultKind.Default, node.False.Kind);
        Assert.Equal("[false]", s.Trailing[0]);
    }

    [Fact]
    public void WholeFileCanBeAReturnAnImportOrANamedReturn()
    {
        Assert.Equal(7f, Parse(null, "[ai pattern]", "[return]", 7, "[/ai pattern]").Root.Number);
        AiScript named = Parse(null, "[return str]", "attack");
        Assert.Equal(AiResultKind.Name, named.Root.Kind);
        Assert.Equal("attack", named.Root.Name);
        AiScript import = Parse(null, "[import ai]", "x.ai");
        Assert.Equal(AiResultKind.Import, import.Root.Kind);
        Assert.Equal("x.ai", import.Root.Name);
        Assert.Equal(AiResultKind.Default, Parse(null, "[ai pattern]", "[/ai pattern]").Root.Kind);
    }

    [Fact]
    public void ReturnAcceptsAFloatOrAName()
    {
        AiScript s = Parse(null,
            "[think]", "[hp]", ">=", 5, "[true]", "[return]", 2.5f, "[false]", "[return]", "tame", "[/think]");

        Assert.Equal(2.5f, s.Root.Node!.True.Number);
        Assert.Equal(AiResultKind.Name, s.Root.Node!.False.Kind);
        Assert.Equal("tame", s.Root.Node!.False.Name);
    }

    [Theory]
    [InlineData("is target in attack area()", 0x0e, 4)]
    [InlineData("is target in attack area_box()", 0x0f, 6)]
    [InlineData("is target in attack area_surround()", 0x10, 8)]
    [InlineData("is target in attack area_absolute()", 0x11, 6)]
    [InlineData("enemy number of in attack area()", 0x12, 4)]
    [InlineData("is the skill in cooltime()", 0x13, 1)]
    [InlineData("number of in attack area()", 0x14, 7)]
    [InlineData("party member number()", 0x15, 0)]
    [InlineData("is attack action enable()", 0x16, 2)]
    [InlineData("is custom action enable()", 0x17, 2)]
    [InlineData("damage check()", 0x18, 1)]
    [InlineData("is target object in area()", 0x19, 7)]
    [InlineData("is the load skill in cooltime()", 0x1a, 1)]
    [InlineData("is free state()", 0x1b, 0)]
    [InlineData("check random()", 0, 2)]
    [InlineData("check bonus random()", 1, 2)]
    [InlineData("get random()", 6, 2)]
    public void VoidFunctionsReadTheirOwnNumberOfParameters(string function, int code, int count)
    {
        var items = new List<object> { "[think]", "[void]", function };
        for (int i = 0; i < count; i++)
        {
            items.Add(10 + i);
        }
        items.AddRange(new object[] { "[true]", "[return]", 1, "[/think]", "[/ai pattern]" });
        AiScript s = Parse(null, items.ToArray());

        Assert.True(s.Clean);
        AiNode node = s.Root.Node!;
        Assert.Equal(code, node.Code);
        Assert.Equal(count, node.Params.Length);
        Assert.Equal(1f, node.True.Number);
        Assert.Equal(new[] { "[/ai pattern]" }, s.Trailing);
    }

    [Fact]
    public void NamedParametersAreLookedUpInTheConstantsTable()
    {
        AiConstants constants = Constants(("all", 100), ("monster", 2));
        AiScript s = Parse(constants,
            "[think]", "[void]", "number of in attack area()", 100, 100, 50, 50, "all", "monster", 1,
            "[true]", "[return]", 1, "[/think]");

        Assert.True(s.Clean);
        AiParam[] p = s.Root.Node!.Params;
        Assert.Equal(7, p.Length);
        Assert.True(p[4].IsName);
        Assert.Equal("all", p[4].Name);
        Assert.Equal(100f, p[4].Value);
        Assert.Equal(2f, p[5].Value);
        Assert.False(p[6].IsName);
    }

    [Fact]
    public void AnUnknownNameIsAnErrorWhenATableIsGivenAndIgnoredWithoutOne()
    {
        object[] script = { "[think]", "[target state]", "levitate", "[true]", "[return]", 1, "[/think]" };

        AiScript withTable = Parse(Constants(("sit", 1)), script);
        Assert.Equal("invalid parameter", withTable.Errors[0].Split('(')[0]);

        AiScript without = Parse(null, script);
        Assert.True(without.Clean);
        Assert.True(float.IsNaN(without.Root.Node!.Params[0].Value));
    }

    [Fact]
    public void KeywordsThatSkipTheOperatorUseTheEqualsCode()
    {
        AiScript s = Parse(null, "[think]", "[target state]", 5, "[true]", "[return]", 1, "[/think]");
        AiNode node = s.Root.Node!;
        Assert.Null(node.Operator);
        Assert.Equal(AiVocabulary.CodeEquals, node.Code);
        Assert.Equal(0x0b, node.KindId);
        Assert.Equal(5f, node.Params[0].Value);
    }

    [Fact]
    public void MyAttackReadsOneParameterThenBecomesAnEqualsTest()
    {
        AiScript s = Parse(null, "[think]", "[my attack]", 7, "[true]", "[return]", 1, "[/think]");
        Assert.Equal(AiVocabulary.CodeEquals, s.Root.Node!.Code);
        Assert.Equal(7f, Assert.Single(s.Root.Node!.Params).Value);
        Assert.True(s.Clean);
    }

    [Fact]
    public void CheckKeyCooltimeReadsAStringKey()
    {
        AiScript s = Parse(null, "[think]", "[check key cooltime]", "slash", "[true]", "[return]", 1, "[/think]");
        AiNode node = s.Root.Node!;
        Assert.Equal(AiVocabulary.CodeKeyName, node.Code);
        AiParam key = Assert.Single(node.Params);
        Assert.Equal("slash", key.Name);
        Assert.True(s.Clean);
    }

    [Fact]
    public void StatChangeIsAPrefixForTheNextKeyword()
    {
        AiScript s = Parse(null, "[think]", "[stat change]", "[hp]", ">=", 5, "[true]", "[return]", 1, "[/think]");
        AiNode node = s.Root.Node!;
        Assert.True(node.StatChangePrefix);
        Assert.Equal("[hp]", node.Keyword);
        Assert.Equal(2, node.KindId);
    }

    [Fact]
    public void KeywordsWithAnOperatorReadOneBeforeTheirParameters()
    {
        AiScript s = Parse(null, "[think]", "[my level]", "is in range()", 10, 20, "[true]", "[return]", 1, "[/think]");
        AiNode node = s.Root.Node!;
        Assert.Equal(5, node.Code);
        Assert.Equal(2, node.Params.Length);
        Assert.Equal(20f, node.Params[1].Value);
    }

    [Fact]
    public void AnExtraCloserEndsTheNodeAndTheRestIsNeverRead()
    {
        // 真分支里多了一个 [/think]:外层结点在一路分支后就当作结束,后面的 [false] 原版也读不到。
        AiScript s = Parse(null,
            "[think]", "[hp]", ">=", 5,
            "[true]", "[think]", "[mp]", ">=", 1, "[true]", "[return]", 1, "[/think]",
            "[/think]",
            "[false]", "[return]", 2, "[/think]", "[/ai pattern]");

        Assert.Equal(AiResultKind.Default, s.Root.Node!.False.Kind);
        Assert.Equal("[false]", s.Trailing[0]);
        Assert.Equal(5, s.TotalTokens - s.ConsumedTokens);
    }

    [Fact]
    public void AMisspelledCloserStillEndsTheNode()
    {
        AiScript s = Parse(null, "[think]", "[hp]", ">=", 5, "[true]", "[return]", 1, "[/thkink]", "[/ai pattern]");
        Assert.True(s.Clean);
        Assert.Equal(1f, s.Root.Node!.True.Number);
        Assert.Equal(new[] { "[/ai pattern]" }, s.Trailing);
    }

    [Fact]
    public void UnknownKeywordAndOperatorAreErrors()
    {
        AiScript unknownKeyword = Parse(null, "[think]", "[no such keyword]", 1, "[true]", "[return]", 1, "[/think]");
        Assert.Equal("invalid condition type", unknownKeyword.Errors[0].Split('(')[0]);
        Assert.Equal(AiResultKind.Default, unknownKeyword.Root.Kind);

        AiScript unknownOperator = Parse(null, "[think]", "[hp]", "!=", 1, "[true]", "[return]", 1, "[/think]");
        Assert.Equal("invalid operator", unknownOperator.Errors[0].Split('(')[0]);
    }

    [Fact]
    public void AParameterThatIsNotANumberOrANameIsAnError()
    {
        AiScript s = Parse(null, "[think]", "[hp]", ">=", "[return]", "[true]", "[return]", 1, "[/think]");
        Assert.NotEmpty(s.Errors);
        Assert.Equal("invalid parameter", s.Errors[0].Split('(')[0]);
    }

    [Fact]
    public void IntegerAndFloatTokensAreBothNumbers()
    {
        AiScript s = Parse(null,
            "[think]", "[void]", "is target in attack area()", 100, 100.5f, 30, 30.25f, "[true]", "[return]", 1, "[/think]");
        AiParam[] p = s.Root.Node!.Params;
        Assert.Equal(new[] { 100f, 100.5f, 30f, 30.25f }, p.Select(x => x.Value).ToArray());
    }

    [Fact]
    public void ConstantsTableReadsPairsAndTheFirstDefinitionWins()
    {
        var b = new TokenStreamBuilder().Str("fire").Int(0).Str("water").Int(1).Str("fire").Int(9).Str("half").Float(0.5f);
        var (_, strings) = b.Build();
        AiConstants c = AiConstants.Parse(b.Bytes(), strings);

        Assert.Equal(3, c.Count);
        Assert.True(c.TryGet("fire", out float fire));
        Assert.Equal(0f, fire);
        Assert.True(c.TryGet("half", out float half));
        Assert.Equal(0.5f, half);
        Assert.False(c.TryGet("earth", out _));
    }

    [Fact]
    public void ConstantsTableStopsAtTheFirstBadPair()
    {
        var b = new TokenStreamBuilder().Str("a").Int(1).Str("b").Tag("[oops]").Str("c").Int(3);
        var (_, strings) = b.Build();
        AiConstants c = AiConstants.Parse(b.Bytes(), strings);

        Assert.Equal(1, c.Count);
        Assert.True(c.TryGet("a", out _));
        Assert.False(c.TryGet("c", out _));
    }

    [Fact]
    public void VocabularyTablesAreConsistent()
    {
        Assert.Equal(AiVocabulary.CtlThink, AiVocabulary.ControlIndex("[think]"));
        Assert.Equal(AiVocabulary.CtlReturn, AiVocabulary.ControlIndex("[return]"));
        Assert.Equal(AiVocabulary.CtlTrue, AiVocabulary.ControlIndex("[true]"));
        Assert.Equal(AiVocabulary.CtlFalse, AiVocabulary.ControlIndex("[false]"));
        Assert.Equal(-1, AiVocabulary.ControlIndex("[hp]"));

        // 表 A 的 56 个关键字类型号连续,名字与类型号互相对得上
        for (int kind = 0; kind <= 0x3f; kind++)
        {
            string name = AiVocabulary.KindName(kind);
            Assert.NotNull(name);
            Assert.Equal(kind, AiVocabulary.KindOf(name));
        }
        Assert.Null(AiVocabulary.KindName(0x40));
        Assert.Equal(0x3f, AiVocabulary.KindOf("[set target position]"));

        // 运算符 0–13 与函数 0x0e–0x1b 的名字互相对得上
        for (int code = 0; code <= 0x1b; code++)
        {
            string name = AiVocabulary.OperatorName(code);
            Assert.NotNull(name);
            Assert.Equal(code, AiVocabulary.OperatorCode(name));
        }
    }
}
