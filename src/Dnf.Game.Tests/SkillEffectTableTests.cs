using Dnf.Game;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// 技能特效预载表的解析(证据 E-129)。脚本文本是合成的:与原版 sqr/common.nut 同形(常量行、职业函数、按技能编号分支、依次预载特效),
/// 技能编号与文件名都是测试用的。
/// </summary>
public class SkillEffectTableTests
{
    private const string Script =
        "SKILL_ALPHA\t<- 11;\r\nSKILL_BETA <- 12;\r\nSKILL_GAMMA <- 13;\r\n"
        + "function sq_LoadSkillEffect_Demon(obj, skillIndex)\r\n{\r\n"
        + "\tif (skillIndex == SKILL_ALPHA)\r\n\t{\r\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/AlphaDemon.ani\");\r\n\t}\r\n}\r\n"
        + "function sq_LoadSkillEffect_Swordman(obj, skillIndex)\r\n{\r\n"
        + "\tif (skillIndex == SKILL_ALPHA)\r\n\t{\r\n"
        + "\t\t// 注释里写的 sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Commented.ani\") 不算\r\n"
        + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Alpha1.ani\");\r\n"
        + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Alpha2.ani\");\r\n\t}\r\n"
        + "\telse if (skillIndex == SKILL_BETA || skillIndex == 14)\r\n\t{\r\n"
        + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Shared.ani\");\r\n\t}\r\n"
        + "\telse if (skillIndex == SKILL_GAMMA)\r\n\t{\r\n\t}\r\n"
        + "\telse if (skillIndex == SKILL_UNKNOWN)\r\n\t{\r\n\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Lost.ani\");\r\n\t}\r\n"
        + "\telse if (skillIndex == 15)\r\n\t{\r\n\t\tif (obj.isSomething())\r\n\t\t{\r\n"
        + "\t\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/Nested.ani\");\r\n\t\t}\r\n"
        + "\t\tobj.sq_LoadSkillEffectAni(skillIndex, \"Effect/Animation/AfterNested.ani\");\r\n\t}\r\n}\r\n";

    [Fact]
    public void LoadsOneJobFunctionAsAnOrderedPathListPerSkill()
    {
        SkillEffectTable table = SkillEffectTable.Parse(Script, "Swordman");

        // 按预载顺序;只取这个职业的函数,别的职业的同一技能不混进来;注释里的调用不算
        Assert.Equal(new[] { "Effect/Animation/Alpha1.ani", "Effect/Animation/Alpha2.ani" }, table.EffectsOf(11).ToArray());
        Assert.Equal(new[] { "Effect/Animation/AlphaDemon.ani" }, SkillEffectTable.Parse(Script, "Demon").EffectsOf(11).ToArray());
    }

    [Fact]
    public void SeveralSkillsInOneConditionShareTheBranchAndNumbersWorkLikeConstants()
    {
        SkillEffectTable table = SkillEffectTable.Parse(Script, "Swordman");

        Assert.Equal(new[] { "Effect/Animation/Shared.ani" }, table.EffectsOf(12).ToArray());   // 常量 SKILL_BETA
        Assert.Equal(new[] { "Effect/Animation/Shared.ani" }, table.EffectsOf(14).ToArray());   // 直接写的数字
    }

    [Fact]
    public void ABranchWithoutLoadsOrWithAnUnknownConstantGivesNothingAndNestedBlocksStayInTheirSkill()
    {
        SkillEffectTable table = SkillEffectTable.Parse(Script, "Swordman");

        Assert.Empty(table.EffectsOf(13));                    // 空分支
        Assert.Empty(table.EffectsOf(99));                    // 没有这个技能
        // 常量没定义的分支整支不算(预载挂不到任何编号上),也不能串到别的技能上
        Assert.DoesNotContain(table.EffectsOf(11).Concat(table.EffectsOf(12)).Concat(table.EffectsOf(15)), p => p.Contains("Lost"));
        // 嵌套的条件里的预载仍属于最近的带编号的外层分支,顺序保持
        Assert.Equal(new[] { "Effect/Animation/Nested.ani", "Effect/Animation/AfterNested.ani" }, table.EffectsOf(15).ToArray());
        Assert.Equal(4, table.SkillCount);                    // 11、12、14、15
    }

    [Fact]
    public void AMissingJobFunctionOrEmptyTextGivesAnEmptyTable()
    {
        Assert.Equal(0, SkillEffectTable.Parse(Script, "Nobody").SkillCount);
        Assert.Equal(0, SkillEffectTable.Parse(string.Empty, "Swordman").SkillCount);
        Assert.Equal(0, SkillEffectTable.Parse(Script, string.Empty).SkillCount);
        Assert.Equal(0, SkillEffectTable.Parse("function sq_LoadSkillEffect_Swordman(obj, skillIndex) { ", "Swordman").SkillCount);   // 括号不配对
        Assert.Empty(SkillEffectTable.Empty.EffectsOf(11));
    }

    [Fact]
    public void LoadReadsTheScriptFromTheDataSourceAndToleratesNonAsciiComments()
    {
        var source = new MemoryScriptSource();
        // 注释里放一些高位字节(原版脚本的注释是本地代码页的文字)
        byte[] head = { 0x2F, 0x2F, 0xB1, 0xE2, 0xBA, 0xBB, 0x0D, 0x0A };
        byte[] body = System.Text.Encoding.ASCII.GetBytes(Script);
        var all = new byte[head.Length + body.Length];
        head.CopyTo(all, 0);
        body.CopyTo(all, head.Length);
        source.AddBytes(SkillEffectTable.ScriptPath, all);
        var data = new GameData(source);

        SkillEffectTable table = SkillEffectTable.Load(data, SkillEffectTable.SwordmanJob);
        Assert.Equal(2, table.EffectsOf(11).Count);

        // 读不到脚本、没有数据时是空表
        Assert.Equal(0, SkillEffectTable.Load(new GameData(new MemoryScriptSource()), SkillEffectTable.SwordmanJob).SkillCount);
        Assert.Equal(0, SkillEffectTable.Load(null, SkillEffectTable.SwordmanJob).SkillCount);
    }
}
