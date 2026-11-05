using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 脚本的 [APPEND HIT] 在房间会话里:拥有者强行对主角打一击——不看攻击框碰没碰到、伤害按拥有者当前的攻击信息结算;
/// 目标不是主角、主角已死或正在无敌、拥有者不是怪物这条线上的对象时不打(只计数)。
/// 场景沿用 <see cref="HeroKnockdownTests"/> 的迷你主角与会出招的迷你怪物:这只怪物每一击 25 点伤害(已被那边的测试固定),主角血量上限 1000。
/// </summary>
public sealed class AppendHitTests
{
    private const int Damage = 25;

    private static GameData DataOf(DnfAssets assets) => new GameData(new PvfScriptSource(assets.Pvf));

    private static HeroKnockdownTests.Arena Open(int hpMax = 1000, bool down = false, int attackDelayMs = 100000)
    {
        var arena = new HeroKnockdownTests.Arena(HeroKnockdownTests.Strike(down: down), hpMax: hpMax, attackDelayMs: attackDelayMs);
        arena.Session.AttachActs(new ActLibrary(arena.Assets, DataOf(arena.Assets)));
        return arena;
    }

    private static IActMessageHost World(RoomCombatSession s) => (IActMessageHost)s.ActHost;

    private static ScriptLine ML(string tag, params object[] values) => new ScriptLine(tag, values
        .Select(v => v is string s ? new ScriptValue(ScriptValueKind.String, 0, s) : new ScriptValue(ScriptValueKind.Int, Convert.ToInt32(v), null)).ToArray());

    [Fact]
    public void AnAppendedHitFromAMonsterHurtsTheHeroEvenFarOutsideAnyAttackBox()
    {
        using var arena = Open();
        RoomCombatSession s = arena.Session;
        World(s).AppendHit(arena.Monster.ActSelf, s.HeroEntity, 5000f, 100f, 0f);          // 落点离主角几千像素:没有几何这回事

        Assert.Equal(1000 - Damage, s.Vitals.Hp);
        Assert.Equal((1, 0, 1, (long)Damage), (s.AppendHitsLanded, s.AppendHitsSkipped, s.HeroHits, s.HeroDamageTaken));
        Assert.Contains(s.Popups, p => p.Text == Damage.ToString());
        World(s).AppendHit(arena.Monster.ActSelf, s.HeroEntity, 5000f, 100f, 0f);          // 没有"这一招已经打中过"的登记:再打一次再扣一次
        Assert.Equal(1000 - 2 * Damage, s.Vitals.Hp);
        Assert.Equal(2, s.AppendHitsLanded);
    }

    [Fact]
    public void AnAppendedHitIsSkippedWhenTheTargetIsNotTheHeroOrTheOwnerIsNotOnTheMonsterLine_OrTheHeroIsDead()
    {
        using var arena = Open();
        RoomCombatSession s = arena.Session;
        IActEntity monster = arena.Monster.ActSelf;

        World(s).AppendHit(monster, monster, 0f, 0f, 0f);                // 目标是怪物自己:没有怪物打怪物
        World(s).AppendHit(s.HeroEntity, s.HeroEntity, 0f, 0f, 0f);      // 拥有者是主角这一边的对象:不落地,只计数
        Assert.Equal((0, 2, 1000), (s.AppendHitsLanded, s.AppendHitsSkipped, s.Vitals.Hp));

        Assert.True(s.Vitals.Damage(5000));                              // 主角死了
        Assert.True(s.HeroDead);
        World(s).AppendHit(monster, s.HeroEntity, 0f, 0f, 0f);
        Assert.Equal((0, 3), (s.AppendHitsLanded, s.AppendHitsSkipped));
        Assert.Equal(0, s.Vitals.Hp);
    }

    [Fact]
    public void ARevivedHeroIsNotHitWhileInvincibleAndIsHitTheStepItEnds()
    {
        using var arena = Open(hpMax: 20, down: true);
        RoomCombatSession s = arena.Session;
        s.Revive = new HeroRevive(DungeonCoinRules.Default, new ReviveCoins(2));
        arena.RunToFirstHit();                                           // 怪物自己打死主角(25 点 > 血量上限 20)
        Assert.True(s.HeroDead);
        Assert.True(arena.RunUntil(() => s.Revive.Open));
        s.Update(10, 0, 0, true, false);                                 // 攻击键 = 用币复活,复活后一段时间无敌
        Assert.False(s.HeroDead);
        Assert.True(s.HeroInvincible);

        World(s).AppendHit(arena.Monster.ActSelf, s.HeroEntity, 340f, 100f, 0f);
        Assert.Equal((0, 1, s.Vitals.HpMax), (s.AppendHitsLanded, s.AppendHitsSkipped, s.Vitals.Hp));   // 无敌:挡住

        Assert.True(arena.RunUntil(() => !s.HeroInvincible));
        World(s).AppendHit(arena.Monster.ActSelf, s.HeroEntity, 340f, 100f, 0f);
        Assert.Equal((1, 1), (s.AppendHitsLanded, s.AppendHitsSkipped));
        Assert.True(s.HeroDead);                                         // 25 > 20:这一击又打死了
    }

    [Fact]
    public void ACompiledAppendHitFromARealMonsterRunnerReachesTheHeroThroughTheSessionHost()
    {
        using var arena = Open();
        RoomCombatSession s = arena.Session;
        ActProgram program = ActProgram.Compile("t.act", ActParser.Parse(new[]
        {
            ML("[TRIGGER]"), ML("[FRAME]", 0, 0), ML("[WHICH]"), ML("[ALL ENEMY]"), ML("[DO BEHAVIOR]"), ML("[CHECKUP OBJECT]", 0), ML("[/TRIGGER]"),
            ML("[BEHAVIOR]"), ML("[APPEND HIT]", 10, 0, 0), ML("[/BEHAVIOR]"),
        }));
        new ActRunner(program, arena.Monster.ActSelf, s.ActHost).Update(0, 0.016f);

        Assert.Equal((1, 1000 - Damage), (s.AppendHitsLanded, s.Vitals.Hp));
        Assert.Empty(s.UnsupportedBehaviorCounts);                        // 不再算没做的行为
    }
}
