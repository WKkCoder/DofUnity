using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 技能特效预载表里的计数循环(M105,R7-he-08):职业函数的某个分支用 <c>for</c> 循环按序号拼出一串路径,
/// 解析器把写成 <c>"前缀" + i + "后缀"</c> 的循环展开成逐次的路径。脚本文本是合成的(技能编号与文件名都是测试用的),期望的路径表按循环手算。
/// </summary>
public class SkillEffectTableLoopTests
{
    private static string Script(string body)
    {
        return "SKILL_LOOPY <- 21;\nSKILL_PLAIN <- 22;\n"
            + "function sq_LoadSkillEffect_Test(obj, skillIndex)\n{\n"
            + "\tif (skillIndex == SKILL_LOOPY)\n\t{\n" + body + "\t}\n"
            + "\telse if (skillIndex == SKILL_PLAIN)\n\t{\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/plain.ani\");\n\t}\n}\n";
    }

    private static string[] LoopyList(string body)
    {
        return SkillEffectTable.Parse(Script(body), "Test").EffectsOf(21).ToArray();
    }

    [Fact]
    public void ALoopedLoadExpandsToOnePathPerIterationAndNestedLoopsRunOuterFirst()
    {
        // 第一个循环 i = 1 到 3(含):a1 a2 a3;外层 j = 1 到 2、里层 i = 1 到 2:b1-1 b1-2 b2-1 b2-2;循环之后的直写预载排在最后
        string body =
            "\t\tfor(local i = 1 ; i <= 3 ; ++i)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/a\" + i + \".ani\");\n"
            + "\t\tfor(local j = 1 ; j <= 2 ; ++j)\n\t\t\tfor(local i = 1 ; i <= 2 ; ++i)\n\t\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/b\" + j + \"-\" + i + \".ani\");\n"
            + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/last.ani\");\n";
        Assert.Equal(new[] { "fx/a1.ani", "fx/a2.ani", "fx/a3.ani", "fx/b1-1.ani", "fx/b1-2.ani", "fx/b2-1.ani", "fx/b2-2.ani", "fx/last.ani" }, LoopyList(body));
        // 别的分支不受影响
        Assert.Equal(new[] { "fx/plain.ani" }, SkillEffectTable.Parse(Script(body), "Test").EffectsOf(22).ToArray());
    }

    [Fact]
    public void ALoopCanStartFromALocalDeclaredBeforeItOrFromAnAssignmentAndMayUseABraceBody()
    {
        // 起值写在循环外的 local 里(for(; i <= 2; ++i) 从 1 起),再一个 for(i = 5; i <= 6; ++i) 从 5 起;循环体用花括号块
        string body =
            "\t\tlocal i = 1;\n\t\tfor(; i <= 2 ; ++i)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/c\" + i + \".ani\");\n"
            + "\t\tfor(i = 5 ; i <= 6 ; ++i)\n\t\t{\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/d\" + i + \".ani\");\n\t\t}\n";
        Assert.Equal(new[] { "fx/c1.ani", "fx/c2.ani", "fx/d5.ani", "fx/d6.ani" }, LoopyList(body));
    }

    [Fact]
    public void AStrictLessThanBoundStopsOneBeforeTheLimitAndAnEqualBoundRunsOnce()
    {
        // i < 3 从 1 起:1、2;a == b 的循环正好跑一次
        string body =
            "\t\tfor(local i = 1 ; i < 3 ; ++i)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/e\" + i + \".ani\");\n"
            + "\t\tfor(local k = 7 ; k <= 7 ; ++k)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/f\" + k + \".ani\");\n";
        Assert.Equal(new[] { "fx/e1.ani", "fx/e2.ani", "fx/f7.ani" }, LoopyList(body));
    }

    [Fact]
    public void ALoopThatRunsZeroTimesAddsNothingAndKeepsTheNeighboursInOrder()
    {
        // 边界:起值大于止值,一次也不跑;前后的直写预载不受影响;i < 1 从 1 起也是零次
        string body =
            "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/before.ani\");\n"
            + "\t\tfor(local i = 4 ; i <= 3 ; ++i)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/never\" + i + \".ani\");\n"
            + "\t\tfor(local j = 1 ; j < 1 ; ++j)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/never\" + j + \".ani\");\n"
            + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/after.ani\");\n";
        Assert.Equal(new[] { "fx/before.ani", "fx/after.ani" }, LoopyList(body));
    }

    [Fact]
    public void ALoopOfExactlyTheLimitExpandsButOneMoreIsLeftAlone()
    {
        // 边界:最多展开 64 次。1 到 64 共 64 次:全展开;1 到 65 共 65 次:整个循环原样留着,里面的调用不是字面量路径,读不到
        string at = "\t\tfor(local i = 1 ; i <= 64 ; ++i)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/g\" + i + \".ani\");\n";
        string[] list = LoopyList(at);
        Assert.Equal(64, list.Length);
        Assert.Equal("fx/g1.ani", list[0]);
        Assert.Equal("fx/g64.ani", list[63]);

        string over = "\t\tfor(local i = 1 ; i <= 65 ; ++i)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/g\" + i + \".ani\");\n";
        Assert.Empty(LoopyList(over));
    }

    [Fact]
    public void LoopsWithANonLiteralBoundAMismatchedVariableOrNoKnownStartAreNotExpanded()
    {
        // 反例:止值不是整数字面量、条件与自增不是同一个变量、声明的变量与条件里的不是同一个、起值没处可取:都不展开,里面的预载读不到;旁边的直写预载照常
        string body =
            "\t\tfor(local i = 1 ; i <= count ; ++i)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/h\" + i + \".ani\");\n"
            + "\t\tfor(local j = 1 ; k <= 3 ; ++j)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/h\" + j + \".ani\");\n"
            + "\t\tfor(; m <= 3 ; ++m)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/h\" + m + \".ani\");\n"
            + "\t\tfor(local n = 1 ; p <= 3 ; ++p)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/h\" + p + \".ani\");\n"
            + "\t\tfor(local q = 1 ; q <= 3 ; ++r)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/h\" + q + \".ani\");\n"
            + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/kept.ani\");\n";
        Assert.Equal(new[] { "fx/kept.ani" }, LoopyList(body));
    }

    [Fact]
    public void ALoopWithoutAStartTakesTheLastLocalDeclaredForItsOwnVariableAndIgnoresOtherVariables()
    {
        // for(; i <= 7; ++i) 的起值取它前面最后一次 local i = 数:先 local i = 1、再 local z = 8、再 local i = 6 → 从 6 起,只有 6、7;
        // 别的变量(z = 8)的声明不算;前面那个 i = 1 被后一个盖掉
        string body =
            "\t\tlocal i = 1;\n\t\tlocal z = 8;\n\t\tlocal i = 6;\n"
            + "\t\tfor(; i <= 7 ; ++i)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/s\" + i + \".ani\");\n";
        Assert.Equal(new[] { "fx/s6.ani", "fx/s7.ani" }, LoopyList(body));

        // 只声明了别的变量(只有 local z = 8):起值取不到,不展开
        string other = "\t\tlocal z = 8;\n\t\tfor(; i <= 9 ; ++i)\n\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/t\" + i + \".ani\");\n";
        Assert.Empty(LoopyList(other));
    }

    [Fact]
    public void AScriptWithoutLoopsIsReadExactlyAsBefore()
    {
        // 没有循环的脚本:展开不改动任何东西
        string body = "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/one.ani\");\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"fx/two.ani\");\n";
        Assert.Equal(new[] { "fx/one.ani", "fx/two.ani" }, LoopyList(body));
        Assert.Equal(body, SkillEffectTable.ExpandLoops(body));
    }
}
