using Dnf.Data;
using Dnf.Game;

namespace Dnf.Game.Tests;

/// <summary>
/// 出招指令的解析(M47,证据 E-140):.skl 的 [command] 里只有八个括号词,分隔词是字符串池里的 "," 或 "&amp;"。
/// 数据全是合成的,不含任何原版技能的指令。
/// </summary>
public sealed class SkillCommandTests
{
    private static ScriptValue Paren(string text) => new ScriptValue(ScriptValueKind.Paren, 0, text);

    private static ScriptValue Sep(string text) => new ScriptValue(ScriptValueKind.Separator, 0, text);

    private static ScriptLine Line(params ScriptValue[] values) => new ScriptLine("[command]", values);

    [Fact]
    public void KeysAreCollectedInOrderWithOrWithoutParenthesesAndAnyCase()
    {
        SkillCommand c = SkillCommand.From(Line(Paren("(DOWN)"), Sep(","), Paren("right"), Sep(","), Paren(" ( Attack ) "), Sep(","), Paren("(JUMP)"), Paren("(SKILL)"), Paren("(BUFF)")));
        Assert.Equal(new[] { CommandKey.Down, CommandKey.Right, CommandKey.Attack, CommandKey.Jump, CommandKey.Skill, CommandKey.Buff }, c.Keys.ToArray());
        Assert.Equal(6, c.Length);
        Assert.False(c.IsEmpty);
        Assert.Equal(0, c.UnknownCount);
        Assert.Equal(CommandKey.Buff, c.Trigger);
    }

    [Fact]
    public void TheAmpersandSeparatorMarksTheKeyBeforeItAsTogetherWithTheNext()
    {
        SkillCommand c = SkillCommand.From(Line(Paren("(UP)"), Sep("&"), Paren("(SKILL)")));
        Assert.True(c.TogetherWithNext(0));
        Assert.False(c.TogetherWithNext(1));       // 最后一个键没有后一个
        Assert.False(c.TogetherWithNext(-1));
        Assert.False(c.TogetherWithNext(5));
        Assert.True(c.HasTogether);
        Assert.Equal("Up&Skill", c.ToString());

        // 逗号(先后)和读不出字符的分隔词都按先后
        SkillCommand seq = SkillCommand.From(Line(Paren("(LEFT)"), Sep(","), Paren("(RIGHT)"), Sep(null), Paren("(SKILL)")));
        Assert.False(seq.HasTogether);
        Assert.Equal("Left+Right+Skill", seq.ToString());

        // 只有紧挨着的那个分隔词算;"同时"不会漏到后面的键上
        SkillCommand mixed = SkillCommand.From(Line(Paren("(DOWN)"), Sep("&"), Paren("(RIGHT)"), Sep(","), Paren("(SKILL)")));
        Assert.Equal(new[] { true, false, false }, new[] { mixed.TogetherWithNext(0), mixed.TogetherWithNext(1), mixed.TogetherWithNext(2) });
    }

    [Fact]
    public void UnknownWordsAreCountedAndOtherValuesAreIgnored()
    {
        SkillCommand c = SkillCommand.From(Line(
            Paren("(UP)"), Paren("(SPIN)"), new ScriptValue(ScriptValueKind.Int, 7, null), new ScriptValue(ScriptValueKind.String, 0, "SKILL"), Sep(","), Paren("(SKILL)")));
        Assert.Equal(new[] { CommandKey.Up, CommandKey.Skill }, c.Keys.ToArray());
        Assert.Equal(1, c.UnknownCount);

        // 只有不认识的词:没有键,但数得出未知个数
        SkillCommand onlyUnknown = SkillCommand.From(Line(Paren("(SPIN)"), Paren("")));
        Assert.True(onlyUnknown.IsEmpty);
        Assert.Equal(2, onlyUnknown.UnknownCount);
    }

    [Fact]
    public void NoLineOrAnEmptyLineIsTheEmptyCommand()
    {
        Assert.Same(SkillCommand.Empty, SkillCommand.From(null));
        Assert.Same(SkillCommand.Empty, SkillCommand.From(Line()));
        Assert.Same(SkillCommand.Empty, SkillCommand.From(Line(Sep(","))));
        Assert.True(SkillCommand.Empty.IsEmpty);
        Assert.Equal(0, SkillCommand.Empty.Length);
        Assert.False(SkillCommand.Empty.IsDirectionsThenTrigger);
        Assert.Equal("", SkillCommand.Empty.ToString());
    }

    [Fact]
    public void TheKeyParserAcceptsExactlyTheEightWords()
    {
        string[] words = { "UP", "DOWN", "LEFT", "RIGHT", "ATTACK", "JUMP", "SKILL", "BUFF" };
        for (int i = 0; i < words.Length; i++)
        {
            Assert.True(SkillCommand.TryParseKey("(" + words[i] + ")", out CommandKey key));
            Assert.Equal((CommandKey)i, key);                 // 枚举的顺序就是这八个词的顺序
        }
        Assert.False(SkillCommand.TryParseKey(null, out _));
        Assert.False(SkillCommand.TryParseKey("", out _));
        Assert.False(SkillCommand.TryParseKey("(UPPER)", out _));
        Assert.False(SkillCommand.TryParseKey("SKILL2", out _));
    }

    [Fact]
    public void DirectionsAndTheTriggerAreClassified()
    {
        Assert.True(SkillCommand.IsDirection(CommandKey.Up));
        Assert.True(SkillCommand.IsDirection(CommandKey.Down));
        Assert.True(SkillCommand.IsDirection(CommandKey.Left));
        Assert.True(SkillCommand.IsDirection(CommandKey.Right));
        Assert.False(SkillCommand.IsDirection(CommandKey.Attack));
        Assert.False(SkillCommand.IsDirection(CommandKey.Jump));
        Assert.False(SkillCommand.IsDirection(CommandKey.Skill));
        Assert.False(SkillCommand.IsDirection(CommandKey.Buff));

        Assert.True(SkillCommand.Of(CommandKey.Down, CommandKey.Right, CommandKey.Skill).IsDirectionsThenTrigger);
        Assert.True(SkillCommand.Of(CommandKey.Skill).IsDirectionsThenTrigger);
        Assert.False(SkillCommand.Of(CommandKey.Attack, CommandKey.Skill).IsDirectionsThenTrigger);
        Assert.False(SkillCommand.Of(CommandKey.Down, CommandKey.Jump, CommandKey.Skill).IsDirectionsThenTrigger);
    }

    [Fact]
    public void OfAndOfTogetherBuildCommandsDirectly()
    {
        SkillCommand a = SkillCommand.Of(CommandKey.Down, CommandKey.Down, CommandKey.Attack);
        Assert.Equal(3, a.Length);
        Assert.False(a.HasTogether);
        Assert.Equal("Down+Down+Attack", a.ToString());

        SkillCommand b = SkillCommand.OfTogether(new[] { CommandKey.Up, CommandKey.Right, CommandKey.Skill }, 0, 7, -1);   // 越界的下标忽略
        Assert.True(b.TogetherWithNext(0));
        Assert.False(b.TogetherWithNext(1));
        Assert.Equal("Up&Right+Skill", b.ToString());

        Assert.Same(SkillCommand.Empty, SkillCommand.Of());
        Assert.Same(SkillCommand.Empty, SkillCommand.OfTogether(null));

        // 复制:改原数组不影响指令
        var source = new[] { CommandKey.Left, CommandKey.Skill };
        SkillCommand copy = SkillCommand.Of(source);
        source[0] = CommandKey.Right;
        Assert.Equal(CommandKey.Left, copy.Keys[0]);
    }

    [Fact]
    public void ASkillDefReadsItsCommandAndKeepsTheOldFlag()
    {
        var with = new SkillDef(901, "skill/t/c.skl", new[] { new ScriptLine("[type]", new[] { new ScriptValue(ScriptValueKind.String, 0, "[active]") }), Line(Paren("(RIGHT)"), Sep(","), Paren("(SKILL)")) });
        Assert.True(with.HasCommand);
        Assert.Equal("Right+Skill", with.Command.ToString());

        var without = new SkillDef(902, "skill/t/d.skl", new[] { new ScriptLine("[type]", new[] { new ScriptValue(ScriptValueKind.String, 0, "[passive]") }) });
        Assert.False(without.HasCommand);
        Assert.Same(SkillCommand.Empty, without.Command);

        // 标签在但里面没有括号词(旧夹具的写法):HasCommand 仍为真,Command 是空的
        var oldStyle = new SkillDef(903, "skill/t/e.skl", new[] { new ScriptLine("[command]", new[] { new ScriptValue(ScriptValueKind.String, 0, "SKILL") }) });
        Assert.True(oldStyle.HasCommand);
        Assert.True(oldStyle.Command.IsEmpty);
    }
}
