using Dnf.Data;
using Dnf.Game.Act;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// M107 怪物线 A 的接线:怪物、被动对象、主角在脚本里各有自己的符号槽;房间会话有一个队伍符号;怪物的受击盒开关让 [IS DAMAGE ENABLE] 与
/// [IS DAMAGEBOX ENABLE] 读到真实状态;单人房间里队伍只有主角一个人。脚本全是合成的:用 <see cref="ActProgram.Compile"/> 直接编出来,
/// 让 <see cref="ActRunner"/> 以怪物(或主角)的脚本身份、房间的 <see cref="IActHost"/> 运行一帧;观察办法是行为 [SET SYMBOL] 9 有没有落到拥有者的符号槽上。
/// </summary>
public partial class PassiveObjectTests
{
    private static ScriptLine R7Line(string tag, params object[] values) =>
        new ScriptLine(tag, Array.ConvertAll(values, v => v is int i
            ? new ScriptValue(ScriptValueKind.Int, i, null)
            : new ScriptValue(ScriptValueKind.String, 0, (string)v)));

    /// <summary>
    /// 一个触发器:[FRAME] 0 0、<paramref name="conditions"/>、[DO BEHAVIOR] 落到(过滤后的)工作集;行为组 0 = <paramref name="behavior"/>。
    /// 不用 [ME]:[ME] 不看工作集,逐对象过滤型条件(怪物的受击盒开关)就看不出来了。
    /// </summary>
    private static ActProgram R7Program(ScriptLine[] conditions, params ScriptLine[] behavior)
    {
        var lines = new List<ScriptLine> { R7Line("[TRIGGER]"), R7Line("[FRAME]", 0, 0) };
        lines.AddRange(conditions);
        lines.Add(R7Line("[DO BEHAVIOR]"));
        lines.Add(R7Line("[CHECKUP OBJECT]", 0));
        lines.Add(R7Line("[/TRIGGER]"));
        lines.Add(R7Line("[BEHAVIOR]"));
        lines.AddRange(behavior);
        lines.Add(R7Line("[/BEHAVIOR]"));
        return ActProgram.Compile("r7.act", ActParser.Parse(lines.ToArray()));
    }

    /// <summary>"满足条件就把拥有者的符号改成 9"。</summary>
    private static ActProgram R7WriteNineIf(params ScriptLine[] conditions) => R7Program(conditions, R7Line("[SET SYMBOL]", 9));

    private static void R7Run(ActProgram program, IActEntity owner, IActHost host) => new ActRunner(program, owner, host).Update(0, 0.016f);

    // ------------------------------------------------------------------ 符号槽

    [Fact]
    public void AMonsterScriptIdentityOwnsTwoSymbolSlotsThatStartAtMinusOneAndDoNotTouchEachOther()
    {
        using var arena = new ActArena("plain", useVitals: false);
        IActEntity self = arena.Monster.ActSelf;
        Assert.Equal(-1, self.ActSymbol);
        Assert.Equal(-1, self.ActSymbolOption);

        self.SetActSymbol(7);
        Assert.Equal(7, self.ActSymbol);
        Assert.Equal(-1, self.ActSymbolOption);             // 写符号不动选项槽
        self.SetActSymbolOption(3);
        Assert.Equal(7, self.ActSymbol);
        Assert.Equal(3, self.ActSymbolOption);              // 写选项槽不动符号
        self.SetActSymbol(-5);
        Assert.Equal(-5, self.ActSymbol);                   // 任意整数都存,包括负数
        self.SetActSymbol(0);
        Assert.Equal(0, self.ActSymbol);                    // 0 与出生值 -1 是两回事

        MonsterActor other = Plain(arena.Assets);
        Assert.Equal(-1, other.ActSelf.ActSymbol);          // 每只怪物各用各的槽
        Assert.Equal(-1, other.ActSelf.ActSymbolOption);
    }

    [Fact]
    public void ScriptSetSymbolOnAMonsterLandsInItsSlotAndCheckSymbolReadsItBackAsAFilter()
    {
        using var arena = new ActArena("plain", useVitals: false);
        IActEntity self = arena.Monster.ActSelf;
        IActHost host = arena.Session.ActHost;

        R7Run(R7Program(new ScriptLine[0], R7Line("[SET SYMBOL]", 5), R7Line("[SET SYMBOL OPTION]", 2)), self, host);
        Assert.Equal(5, self.ActSymbol);
        Assert.Equal(2, self.ActSymbolOption);

        R7Run(R7WriteNineIf(R7Line("[CHECK SYMBOL]", 1, 4)), self, host);
        Assert.Equal(5, self.ActSymbol);                    // 反例:符号是 5 不是 4,不改
        R7Run(R7WriteNineIf(R7Line("[CHECK SYMBOL]", 0, 5)), self, host);
        Assert.Equal(5, self.ActSymbol);                    // 反例:要求"不等于 5",不改
        R7Run(R7WriteNineIf(R7Line("[CHECK SYMBOL]", 1, 5)), self, host);
        Assert.Equal(9, self.ActSymbol);                    // 等于 5:写了 9
        R7Run(R7WriteNineIf(R7Line("[CHECK SYMBOL]", 0, 5)), self, host);
        Assert.Equal(9, self.ActSymbol);                    // 现在是 9:"不等于 5"成立,写 9 还是 9(幂等)
        Assert.Equal(2, self.ActSymbolOption);              // 选项槽一直没被碰
    }

    [Fact]
    public void ThePassiveObjectAndTheHeroEachKeepTheirOwnSymbols()
    {
        using var arena = new ActArena("plain", useVitals: false);
        arena.Session.ActHost.SpawnPassive(arena.Monster.ActSelf, AvatarFixture.ActHomeInstantId, 0, 100f, 40f, 0f, false);
        PassiveObject spawned = arena.Session.Passives.Single(p => p.Template.Index == AvatarFixture.ActHomeInstantId);
        IActEntity passive = spawned;
        IActEntity hero = arena.Session.HeroEntity;

        Assert.Equal(-1, passive.ActSymbol);
        Assert.Equal(-1, passive.ActSymbolOption);
        Assert.Equal(-1, hero.ActSymbol);
        Assert.Equal(-1, hero.ActSymbolOption);

        passive.SetActSymbol(6);
        passive.SetActSymbolOption(8);
        Assert.Equal(6, passive.ActSymbol);
        Assert.Equal(8, passive.ActSymbolOption);
        Assert.Equal(-1, hero.ActSymbol);                   // 被动对象写的不会写到主角身上
        Assert.Equal(-1, arena.Monster.ActSelf.ActSymbol);  // 也不会写到怪物身上

        hero.SetActSymbol(2);
        Assert.Equal(2, hero.ActSymbol);
        Assert.Equal(6, passive.ActSymbol);                 // 主角写的不会写到被动对象身上
        Assert.Equal(-1, hero.ActSymbolOption);
    }

    // ------------------------------------------------------------------ 队伍符号

    [Fact]
    public void ThePartySymbolBelongsToTheRoomSessionStartsAtMinusOneAndIsWrittenByTheScript()
    {
        using var first = new ActArena("plain", useVitals: false);
        using var second = new ActArena("plain", useVitals: false);
        IActHost host = first.Session.ActHost;
        Assert.Equal(-1, host.PartySymbol);
        Assert.Equal(-1, second.Session.ActHost.PartySymbol);

        R7Run(R7Program(new ScriptLine[0], R7Line("[SET PARTY SYMBOL]", 4)), first.Monster.ActSelf, host);
        Assert.Equal(4, host.PartySymbol);
        Assert.Equal(-1, second.Session.ActHost.PartySymbol);       // 另一个房间会话不受影响:队伍符号是按房间会话存的

        IActEntity self = first.Monster.ActSelf;
        R7Run(R7WriteNineIf(R7Line("[CHECK PARTY SYMBOL]", 1, 3)), self, host);
        Assert.Equal(-1, self.ActSymbol);                   // 反例:队伍符号是 4 不是 3
        R7Run(R7WriteNineIf(R7Line("[CHECK PARTY SYMBOL]", 0, 4)), self, host);
        Assert.Equal(-1, self.ActSymbol);                   // 反例:"不等于 4"不成立
        R7Run(R7WriteNineIf(R7Line("[CHECK PARTY SYMBOL]", 1, 4)), self, host);
        Assert.Equal(9, self.ActSymbol);                    // 等于 4:写了 9
    }

    [Fact]
    public void ACheckPartySymbolGateInTheFreshRoomOnlyOpensForTheInitialMinusOne()
    {
        using var arena = new ActArena("plain", useVitals: false);
        IActEntity self = arena.Monster.ActSelf;
        IActHost host = arena.Session.ActHost;

        R7Run(R7WriteNineIf(R7Line("[CHECK PARTY SYMBOL]", 1, 0)), self, host);
        Assert.Equal(-1, self.ActSymbol);                   // 反例:新房间里队伍符号是 -1,不是 0
        R7Run(R7WriteNineIf(R7Line("[CHECK PARTY SYMBOL]", 1, -1)), self, host);
        Assert.Equal(9, self.ActSymbol);                    // 边界:正是 -1
    }

    // ------------------------------------------------------------------ 受击盒

    [Fact]
    public void AMonsterWhoseScriptSwitchedTheDamageBoxOffReadsAsDamageDisabledAndOnAgainAfterTheSwitchBack()
    {
        using var arena = new ActArena("dmgoff", useVitals: false);
        MonsterActor m = arena.Monster;
        IActEntity self = m.ActSelf;
        IActHost host = arena.Session.ActHost;
        Assert.False(m.DamageBoxOff);
        Assert.True(self.ActDamageEnabled);                 // 出招前受击盒开着

        R7Run(R7WriteNineIf(R7Line("[IS DAMAGE ENABLE]")), self, host);
        Assert.Equal(9, self.ActSymbol);                    // 开着:条件成立
        self.SetActSymbol(-1);

        Assert.True(arena.Until(() => m.DamageBoxOff) > 0);
        Assert.False(self.ActDamageEnabled);
        Assert.False(self.IsActDamageBoxEnabled);
        R7Run(R7WriteNineIf(R7Line("[IS DAMAGE ENABLE]")), self, host);
        Assert.Equal(-1, self.ActSymbol);                   // 反例:脚本把受击盒关了,[IS DAMAGE ENABLE] 不成立
        R7Run(R7WriteNineIf(R7Line("[IS DAMAGEBOX ENABLE]", 1)), self, host);
        Assert.Equal(-1, self.ActSymbol);                   // [IS DAMAGEBOX ENABLE] 1("开着才通过")同样读到关
        R7Run(R7WriteNineIf(R7Line("[IS DAMAGEBOX ENABLE]", 0)), self, host);
        Assert.Equal(9, self.ActSymbol);                    // [IS DAMAGEBOX ENABLE] 0("关着才通过")成立
        self.SetActSymbol(-1);

        Assert.True(arena.Until(() => !m.DamageBoxOff) > 0);
        Assert.True(self.ActDamageEnabled);
        Assert.True(self.IsActDamageBoxEnabled);
        R7Run(R7WriteNineIf(R7Line("[IS DAMAGE ENABLE]")), self, host);
        Assert.Equal(9, self.ActSymbol);                    // 帧 3 重新打开:又成立
    }

    [Fact]
    public void TheHeroHasNoKnownDamageBoxFlagSoIsDamageEnableNeverFiresForIt()
    {
        using var arena = new ActArena("plain", useVitals: false);
        IActEntity hero = arena.Session.HeroEntity;
        Assert.Null(hero.ActDamageEnabled);                 // 客户端里这个标志的初值与开关者没查到:未知,不猜

        R7Run(R7WriteNineIf(R7Line("[IS DAMAGE ENABLE]")), hero, arena.Session.ActHost);
        Assert.Equal(-1, hero.ActSymbol);                   // 未知就整条触发器不触发,不改变任何行为
    }

    // ------------------------------------------------------------------ 单人房间的队伍

    [Fact]
    public void InASinglePlayerRoomThePartyIsJustTheHeroSoOnlyCountOneSlotZeroAndAllAliveHold()
    {
        using var arena = new ActArena("plain", useVitals: false);
        IActEntity self = arena.Monster.ActSelf;
        IActHost host = arena.Session.ActHost;

        bool Fires(params ScriptLine[] conditions)
        {
            self.SetActSymbol(-1);
            R7Run(R7WriteNineIf(conditions), self, host);
            return self.ActSymbol == 9;
        }

        ScriptLine[] Count(int n) => new[] { R7Line("[CHECK PLAYER NUM]"), R7Line("[COUNT]", n), R7Line("[/CHECK PLAYER NUM]") };
        ScriptLine[] CountWithDead(int n) => new[] { R7Line("[CHECK PLAYER NUM]"), R7Line("[COUNT]", n), R7Line("[INCLUDE DEAD]", 1), R7Line("[/CHECK PLAYER NUM]") };

        Assert.True(Fires(Count(1)));                       // 主角一个人活着:人数 1
        Assert.False(Fires(Count(0)));                      // 反例:不是 0
        Assert.False(Fires(Count(2)));                      // 反例:不是 2
        Assert.True(Fires(CountWithDead(1)));               // 把死人算进去,还是 1(只有主角)
        Assert.False(Fires(CountWithDead(2)));

        Assert.True(Fires(R7Line("[CHECK PARTY SLOT]", 0, 1)));          // 槽 0 有主角
        Assert.False(Fires(R7Line("[CHECK PARTY SLOT]", 0, 0)));         // 反例:标志 0 要槽 0 没人
        Assert.True(Fires(R7Line("[CHECK PARTY SLOT]", 1, 0)));          // 槽 1 没人:标志 0 成立
        Assert.False(Fires(R7Line("[CHECK PARTY SLOT]", 1, 1)));         // 反例:标志 1 要槽 1 有人

        Assert.True(Fires(R7Line("[CHECK PARTYMEMBERS STATE]"), R7Line("[ALL ALIVE]")));
        Assert.False(Fires(R7Line("[CHECK PARTYMEMBERS STATE]"), R7Line("[ALL DEATH]")));         // 反例:活着的人在,不是"全死"
        Assert.False(Fires(R7Line("[CHECK PARTYMEMBERS STATE]"), R7Line("[EVEN ONE DEATH]")));    // 反例:没有人死
    }
}
