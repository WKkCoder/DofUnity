using Dnf.Data;
using Dnf.Game.Ai;

namespace Dnf.Game.Tests;

/// <summary>
/// .ai 求值器(AiEvaluator)的测试。规则依据 E-052、E-053、E-128(客户端求值循环、比较运算符与区域判断的静态读码,方向对着样本脚本核过);
/// 脚本与宿主都是合成的,不含原版内容。
/// </summary>
public sealed class AiEvaluatorTests
{
    private sealed class Host : IAiHost
    {
        private int _next;

        public int[] Rands { get; set; } = { 0 };

        public float SelfX { get; set; } = 100f;

        public float SelfY { get; set; } = 200f;

        public bool SelfFacingLeft { get; set; }

        public bool HasTarget { get; set; } = true;

        public float TargetX { get; set; }

        public float TargetY { get; set; }

        public HashSet<int> Cooling { get; } = new();

        public HashSet<(int, int)> Enabled { get; } = new();

        public bool Free { get; set; } = true;

        public int Counted { get; set; }

        public (float XMin, float XMax, float YMin, float YMax, int Team, int Type, int Id)? LastCount { get; private set; }

        public bool OtherResult { get; set; }

        public List<string> OtherCalls { get; } = new();

        public int Rand() => Rands[_next++ % Rands.Length];

        public bool IsAttackCooling(int index) => Cooling.Contains(index);

        public bool IsAttackEnabled(int action, int trigger) => Enabled.Contains((action, trigger));

        public bool IsCustomEnabled(int action, int trigger) => false;

        public bool IsFreeState() => Free;

        public bool DamageCheck(int mode) => mode == 1;

        public int CountInArea(float xMin, float xMax, float yMin, float yMax, int teamMode, int typeFilter, int idFilter)
        {
            LastCount = (xMin, xMax, yMin, yMax, teamMode, typeFilter, idFilter);
            return Counted;
        }

        public bool Other(AiNode node, AiScratch scratch)
        {
            OtherCalls.Add(node.Keyword);
            return OtherResult;
        }
    }

    private static AiConstants Constants()
    {
        var c = new AiConstants();
        c.Add("all", 100f);
        c.Add("monster", 2f);
        c.Add("summon", 5f);
        c.Add("enemy", -1f);
        return c;
    }

    private static AiScript Parse(params object[] items)
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
        (ScriptToken[] tokens, PvfStringTable strings) = b.Build();
        AiScript script = AiParser.Parse(tokens, strings, Constants());
        Assert.True(script.Clean, string.Join(";", script.Errors));
        return script;
    }

    private static float Run(AiScript script, Host host, AiScratch scratch, Dictionary<string, AiScript> files = null, string path = "monster/x/ai/action.ai")
    {
        var evaluator = new AiEvaluator(p => files != null && files.TryGetValue(p, out AiScript s) ? s : null);
        AiResult result = evaluator.Evaluate(path, script, host, scratch);
        return result.Number;
    }

    private static AiScript IfHp(string op, int threshold, int whenTrue, int whenFalse)
    {
        return Parse("[think]", "[hp]", op, threshold, "[true]", "[return]", whenTrue, "[false]", "[return]", whenFalse, "[/think]");
    }

    [Fact]
    public void APercentComparisonDividesTheValueByItsMaximum()
    {
        var scratch = new AiScratch();
        scratch.Set(AiSlot.Hp, 30f, 100f);
        Assert.Equal(2f, Run(IfHp("%>=", 50, 1, 2), new Host(), scratch));
        scratch.Set(AiSlot.Hp, 60f, 100f);
        Assert.Equal(1f, Run(IfHp("%>=", 50, 1, 2), new Host(), scratch));
        scratch.Set(AiSlot.Hp, 50f, 100f);
        Assert.Equal(1f, Run(IfHp("%>=", 50, 1, 2), new Host(), scratch));   // 边界相等也算
        Assert.Equal(2f, Run(IfHp("%>", 50, 1, 2), new Host(), scratch));
        Assert.Equal(1f, Run(IfHp("%<=", 50, 1, 2), new Host(), scratch));
        Assert.Equal(2f, Run(IfHp("%<", 50, 1, 2), new Host(), scratch));
        Assert.Equal(1f, Run(IfHp("%==", 50, 1, 2), new Host(), scratch));
    }

    [Fact]
    public void PlainComparisonsUseTheValueNotThePercent()
    {
        var scratch = new AiScratch();
        scratch.Set(AiSlot.Hp, 30f, 100f);
        Assert.Equal(1f, Run(IfHp(">=", 30, 1, 2), new Host(), scratch));
        Assert.Equal(2f, Run(IfHp(">", 30, 1, 2), new Host(), scratch));
        Assert.Equal(1f, Run(IfHp("<=", 30, 1, 2), new Host(), scratch));
        Assert.Equal(2f, Run(IfHp("<", 30, 1, 2), new Host(), scratch));
        Assert.Equal(1f, Run(IfHp("==", 30, 1, 2), new Host(), scratch));
        Assert.Equal(2f, Run(IfHp("==", 31, 1, 2), new Host(), scratch));
    }

    [Fact]
    public void AClearedScratchReadsMinusOneWithMaximumZero()
    {
        var scratch = new AiScratch();
        Assert.Equal(-1f, scratch.Value(AiSlot.Hp));
        Assert.Equal(0f, scratch.Max(AiSlot.Hp));
        // 没写过的血量:数值比较 -1 >= 0 为假;百分比里 -1 ÷ 0 是负无穷,"大于等于"为假、"小于等于"为真
        Assert.Equal(2f, Run(IfHp(">=", 0, 1, 2), new Host(), scratch));
        Assert.Equal(2f, Run(IfHp("%>=", 0, 1, 2), new Host(), scratch));
        Assert.Equal(1f, Run(IfHp("%<=", 0, 1, 2), new Host(), scratch));
    }

    [Fact]
    public void IsInRangeIsInclusiveAtBothEnds()
    {
        AiScript script = Parse("[think]", "[my level]", "is in range()", 10, 20, "[true]", "[return]", 1, "[false]", "[return]", 0, "[/think]");
        var scratch = new AiScratch();
        scratch.Set(AiSlot.MyLevel, 10f);
        Assert.Equal(1f, Run(script, new Host(), scratch));
        scratch.Set(AiSlot.MyLevel, 20f);
        Assert.Equal(1f, Run(script, new Host(), scratch));
        scratch.Set(AiSlot.MyLevel, 21f);
        Assert.Equal(0f, Run(script, new Host(), scratch));
        scratch.Set(AiSlot.MyLevel, 9f);
        Assert.Equal(0f, Run(script, new Host(), scratch));
    }

    [Fact]
    public void UnwrittenBranchesAndEmptyScriptsGiveMinusOne()
    {
        AiScript script = Parse("[think]", "[hp]", ">=", 5, "[true]", "[return]", 7, "[/think]");
        var scratch = new AiScratch();
        scratch.Set(AiSlot.Hp, 1f, 10f);
        Assert.Equal(-1f, Run(script, new Host(), scratch));
        scratch.Set(AiSlot.Hp, 6f, 10f);
        Assert.Equal(7f, Run(script, new Host(), scratch));
        Assert.Equal(-1f, new AiEvaluator(null).Evaluate("a.ai", null, new Host(), scratch).Number);
    }

    [Fact]
    public void CheckRandomTakesTheRemainderOfTheClientRandom()
    {
        AiScript script = Parse("[think]", "[void]", "check random()", 40, 100, "[true]", "[return]", 1, "[false]", "[return]", 0, "[/think]");
        Assert.Equal(1f, Run(script, new Host { Rands = new[] { 237 } }, new AiScratch()));   // 37 < 40
        Assert.Equal(0f, Run(script, new Host { Rands = new[] { 240 } }, new AiScratch()));   // 40 < 40 不成立
        Assert.Equal(0f, Run(script, new Host { Rands = new[] { 199 } }, new AiScratch()));   // 99 < 40 不成立
    }

    [Fact]
    public void GetRandomWritesTheLastResultAndIsAlwaysTrue()
    {
        AiScript script = Parse(
            "[think]", "[void]", "get random()", 10, 100,
            "[true]", "[think]", "[last result]", "<=", 50, "[true]", "[return]", 1, "[false]", "[return]", 2, "[/think]",
            "[false]", "[return]", 9,
            "[/think]");
        var scratch = new AiScratch();
        Assert.Equal(1f, Run(script, new Host { Rands = new[] { 330 } }, scratch));   // 10 + 30 = 40 <= 50
        Assert.Equal(40f, scratch.Value(AiSlot.LastResult));
        Assert.Equal(2f, Run(script, new Host { Rands = new[] { 355 } }, new AiScratch()));   // 10 + 55 = 65 > 50
    }

    [Fact]
    public void CheckBonusRandomAddsTheSlotValueToTheThreshold()
    {
        AiScript script = Parse("[think]", "[hp]", "check bonus random()", 10, 100, "[true]", "[return]", 1, "[false]", "[return]", 0, "[/think]");
        var scratch = new AiScratch();
        scratch.Set(AiSlot.Hp, 25f, 100f);
        Assert.Equal(1f, Run(script, new Host { Rands = new[] { 30 } }, scratch));    // 30 < 25 + 10
        Assert.Equal(0f, Run(script, new Host { Rands = new[] { 35 } }, scratch));    // 35 < 35 不成立
    }

    [Fact]
    public void TheSkillCooltimeFunctionIsTrueWhileTheSkillIsCooling()
    {
        AiScript script = Parse("[think]", "[void]", "is the skill in cooltime()", 2, "[true]", "[return]", -1, "[false]", "[return]", 2, "[/think]");
        var host = new Host();
        Assert.Equal(2f, Run(script, host, new AiScratch()));
        host.Cooling.Add(2);
        Assert.Equal(-1f, Run(script, host, new AiScratch()));
    }

    [Fact]
    public void AttackActionEnableAsksTheHostForActionAndTrigger()
    {
        AiScript script = Parse("[think]", "[void]", "is attack action enable()", 3, 0, "[true]", "[return]", 1, "[false]", "[return]", 0, "[/think]");
        var host = new Host();
        Assert.Equal(0f, Run(script, host, new AiScratch()));
        host.Enabled.Add((3, 0));
        Assert.Equal(1f, Run(script, host, new AiScratch()));
        host.Enabled.Clear();
        host.Enabled.Add((3, 1));
        Assert.Equal(0f, Run(script, host, new AiScratch()));
    }

    [Fact]
    public void FreeStateAndDamageCheckComeFromTheHost()
    {
        AiScript free = Parse("[think]", "[void]", "is free state()", "[true]", "[return]", 1, "[false]", "[return]", 0, "[/think]");
        Assert.Equal(1f, Run(free, new Host { Free = true }, new AiScratch()));
        Assert.Equal(0f, Run(free, new Host { Free = false }, new AiScratch()));
        AiScript check = Parse("[think]", "[void]", "damage check()", 1, "[true]", "[return]", 5, "[false]", "[return]", 6, "[/think]");
        Assert.Equal(5f, Run(check, new Host(), new AiScratch()));
    }

    [Fact]
    public void AnAreaTestUsesTheFacingTruncatesToIntegersAndNeedsATarget()
    {
        // 朝右:X 区间 [x - 身后, x + 身前] = [80, 150];Y 区间 [y - 20.., y + ..] = [190, 230];位置先取整(100.9 -> 100、200.9 -> 200)
        AiScript script = Parse("[think]", "[void]", "is target in attack area()", 20, 50, 10, 30, "[true]", "[return]", 1, "[false]", "[return]", 0, "[/think]");
        var host = new Host { SelfX = 100.9f, SelfY = 200.9f, TargetX = 150.9f, TargetY = 230.5f };
        Assert.Equal(1f, Run(script, host, new AiScratch()));
        host.TargetX = 151f;
        Assert.Equal(0f, Run(script, host, new AiScratch()));
        host.TargetX = 79.9f;                     // 取整成 79,不在 [80, 150]
        Assert.Equal(0f, Run(script, host, new AiScratch()));
        host.TargetX = 80f;
        host.TargetY = 189f;                      // 低于 Y 下界 190
        Assert.Equal(0f, Run(script, host, new AiScratch()));
        // 朝左:前后对调,X 区间 [x - 身前, x + 身后] = [50, 120]
        host.SelfFacingLeft = true;
        host.TargetX = 50f;
        host.TargetY = 200f;
        Assert.Equal(1f, Run(script, host, new AiScratch()));
        host.TargetX = 121f;
        Assert.Equal(0f, Run(script, host, new AiScratch()));
        host.HasTarget = false;
        host.TargetX = 100f;
        Assert.Equal(0f, Run(script, host, new AiScratch()));
    }

    [Fact]
    public void CountingFunctionsWriteTheLastResultAndPassTheirFilters()
    {
        AiScript script = Parse(
            "[think]", "[void]", "number of in attack area()", 30, 60, 5, 15, "all", "summon", 777,
            "[true]", "[think]", "[last result]", ">=", 3, "[true]", "[return]", 1, "[false]", "[return]", 2, "[/think]",
            "[/think]");
        var host = new Host { SelfFacingLeft = true, Counted = 4 };
        var scratch = new AiScratch();
        Assert.Equal(1f, Run(script, host, scratch));
        Assert.Equal(4f, scratch.Value(AiSlot.LastResult));
        // 朝左:x 区间 [100 - 60, 100 + 30],y 区间 [200 - 5, 200 + 15];队伍 100(不限)、种类 5(召唤物)、编号 777
        Assert.Equal((40f, 130f, 195f, 215f, 100, 5, 777), host.LastCount);
        host.Counted = 2;
        Assert.Equal(2f, Run(script, host, new AiScratch()));

        AiScript enemies = Parse("[think]", "[void]", "enemy number of in attack area()", 30, 60, 5, 15, "[true]", "[think]", "[last result]", ">=", 1, "[true]", "[return]", 1, "[/think]", "[/think]");
        host.Counted = 1;
        Assert.Equal(1f, Run(enemies, host, new AiScratch()));
        Assert.Equal(-1, host.LastCount!.Value.Team);
        Assert.Equal(AiObjectKind.All, host.LastCount!.Value.Type);
        Assert.Equal(-1, host.LastCount!.Value.Id);
    }

    [Fact]
    public void KeywordsWithTheirOwnCodesAreLeftToTheHost()
    {
        AiScript script = Parse("[think]", "[set target monster]", 5, 6, "[true]", "[return]", 1, "[false]", "[return]", 0, "[/think]");
        var host = new Host { OtherResult = true };
        Assert.Equal(1f, Run(script, host, new AiScratch()));
        Assert.Equal(new[] { "[set target monster]" }, host.OtherCalls);
        host.OtherResult = false;
        Assert.Equal(0f, Run(script, host, new AiScratch()));
    }

    [Fact]
    public void AnImportSwitchesToTheSiblingScriptWithoutLookingAtAnyCondition()
    {
        AiScript main = Parse("[think]", "[hp]", ">=", 0, "[true]", "[import ai]", "Other.ai", "[false]", "[return]", 5, "[/think]");
        AiScript other = Parse("[think]", "[hp]", "%>=", 50, "[true]", "[return]", 11, "[false]", "[return]", 12, "[/think]");
        var files = new Dictionary<string, AiScript> { ["monster/x/ai/other.ai"] = other };
        var scratch = new AiScratch();
        scratch.Set(AiSlot.Hp, 80f, 100f);
        Assert.Equal(11f, Run(main, new Host(), scratch, files));
        scratch.Set(AiSlot.Hp, 10f, 100f);
        Assert.Equal(12f, Run(main, new Host(), scratch, files));
    }

    [Fact]
    public void ARootImportAndMissingOrLoopingImportsAreSafe()
    {
        AiScript root = Parse("[import ai]", "Loop.ai");
        AiScript loop = Parse("[import ai]", "Loop.ai");
        var files = new Dictionary<string, AiScript> { ["monster/x/ai/loop.ai"] = loop };
        Assert.Equal(-1f, Run(root, new Host(), new AiScratch(), files));            // 自己导入自己:到层数上限就算默认值
        Assert.Equal(-1f, Run(root, new Host(), new AiScratch(), new Dictionary<string, AiScript>()));   // 文件缺失
        AiScript up = Parse("[import ai]", "../Shared.ai");
        var shared = Parse("[return]", 4);
        files = new Dictionary<string, AiScript> { ["monster/x/shared.ai"] = shared };
        Assert.Equal(4f, Run(up, new Host(), new AiScratch(), files));                // 相对脚本所在目录,"../" 折叠
    }

    [Fact]
    public void NamedReturnsComeBackAsNames()
    {
        AiScript script = Parse("[think]", "[hp]", ">=", 0, "[true]", "[return str]", "stand", "[/think]");
        var scratch = new AiScratch();
        scratch.Set(AiSlot.Hp, 1f, 1f);
        AiResult r = new AiEvaluator(null).Evaluate("a.ai", script, new Host(), scratch);
        Assert.Equal(AiResultKind.Name, r.Kind);
        Assert.Equal("stand", r.Name);
    }
}
