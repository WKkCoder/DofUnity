using Dnf.Game.Ai;
using Dnf.Render;
using Dnf.Render.Tests;

namespace Dnf.Play.Tests;

/// <summary>
/// 触发器开关跨出招保留(M41,证据 E-134):客户端里 [TRIGGER] n [ON]/[OFF] 改的是怪物招式条目上的开关字节,出招开始的复位只清单次位与计数,
/// 所以首领写的"每个血量段只用一次"(.ai 查 is attack action enable,.act 末尾把自己的触发器关掉)靠它成立。
/// 怪物与 .act 都是合成的:summononce(帧 1 召唤一只怪物,随后 [TRIGGER] 0 [OFF])对照 summon(同样的召唤,不关触发器)。
/// </summary>
public partial class PassiveObjectTests
{
    /// <summary>每步 10 毫秒推进 <paramref name="steps"/> 步,返回期间怪物进入出招阶段的次数。</summary>
    private static int CountAttacks(ActArena arena, int steps)
    {
        int attacks = 0;
        bool inAttack = arena.Monster.Phase == MonsterPhase.Attack;
        for (int i = 0; i < steps; i++)
        {
            arena.Steps(1);
            bool now = arena.Monster.Phase == MonsterPhase.Attack;
            if (now && !inAttack)
            {
                attacks++;
            }
            inAttack = now;
        }
        return attacks;
    }

    [Fact]
    public void ATriggerSwitchedOffByTheScriptStaysOffForTheMonstersNextAttacks()
    {
        using var arena = new ActArena("summononce", useVitals: false);
        RoomCombatSession s = arena.Session;
        s.UseSummons(BlobFactory(arena.Assets));
        MonsterActor m = arena.Monster;
        Assert.Equal(true, m.ActTriggerState(0, 0));                // 出生:[ENABLE] 的初值(没写算开)
        Assert.Null(m.ActTriggerState(1, 0));                       // 没有这一招
        Assert.Null(m.ActTriggerState(0, 1));                       // 这一招没有第 1 个触发器
        Assert.Null(m.ActTriggerState(-1, 0));

        Assert.True(arena.Until(() => s.SummonsSpawned > 0) > 0);
        Assert.Equal(false, m.ActTriggerState(0, 0));               // 召唤完,脚本把自己的触发器关了

        int attacks = CountAttacks(arena, 900);
        Assert.True(attacks >= 2, "之后怪物又出了几次招:" + attacks);
        Assert.Equal(1, s.SummonsSpawned);                          // 关着的触发器不再召唤
        Assert.Equal(false, m.ActTriggerState(0, 0));
    }

    [Fact]
    public void WithoutTheSwitchOffTheSameAttackKeepsSummoningOnEveryRun()
    {
        using var arena = new ActArena("summon", useVitals: false);
        RoomCombatSession s = arena.Session;
        s.UseSummons(BlobFactory(arena.Assets));
        Assert.True(arena.Until(() => s.SummonsSpawned > 0) > 0);
        Assert.Equal(true, arena.Monster.ActTriggerState(0, 0));    // 没人关它:一直开着

        Assert.True(CountAttacks(arena, 900) >= 2);
        Assert.True(s.SummonsSpawned >= 2, "对照:不关触发器每次出招都召唤:" + s.SummonsSpawned);
    }

    [Fact]
    public void TheAiScriptIsGivenTheMonstersLiveSwitchesAndLosesThemWhenTheActsAreRemoved()
    {
        using var arena = new ActArena("summononce", useVitals: false);
        arena.Session.UseSummons(BlobFactory(arena.Assets));
        MonsterActor m = arena.Monster;
        Assert.True(arena.Until(() => arena.Session.SummonsSpawned > 0) > 0);

        var script = new MonsterScript(_ => null, new string[MonsterScript.RoleCount], new AiScript[MonsterScript.RoleCount], null, 1, () => 0)
        {
            TriggerEnabled = (action, trigger) => true,           // .act 里 [ENABLE] 的初值:开
        };
        Assert.Null(script.LiveTrigger);
        m.SetScript(script);
        Assert.NotNull(script.LiveTrigger);
        Assert.Equal(false, script.LiveTrigger(0, 0));            // 跑着的开关已经被脚本关了:盖过静态初值
        Assert.Null(script.LiveTrigger(1, 0));                    // 这一招没有触发器程序:交回静态初值

        m.SetActs(null, null);                                    // 撤掉脚本程序:不再有跑着的开关
        Assert.Null(script.LiveTrigger);
        Assert.Null(m.ActTriggerState(0, 0));
    }

    [Fact]
    public void ReattachingTheActsStartsTheSwitchesFromTheScriptsInitialValuesAgain()
    {
        using var arena = new ActArena("summononce", useVitals: false);
        arena.Session.UseSummons(BlobFactory(arena.Assets));
        Assert.True(arena.Until(() => arena.Session.SummonsSpawned > 0) > 0);
        Assert.Equal(false, arena.Monster.ActTriggerState(0, 0));

        arena.Session.AttachActs(null);
        Assert.Null(arena.Monster.ActTriggerState(0, 0));
        arena.Session.AttachActs(new ActLibrary(arena.Assets, DataOf(arena.Assets)));
        Assert.Equal(true, arena.Monster.ActTriggerState(0, 0));  // 重新装:开关从初值起步
    }

    // ------------------------------------------------------------------ [RESTORE]:自己扣血直到死(召唤型首领的写法)

    [Fact]
    public void ASelfRestoreWithANegativePercentWearsTheMonsterDownAndKillsItWithoutCredit()
    {
        using var arena = new ActArena("restore", useVitals: false);
        RoomCombatSession s = arena.Session;
        MonsterActor m = arena.Monster;
        Assert.Equal((100, 100), (m.Hp, m.HpMax));

        Assert.True(arena.Until(() => m.Hp < 100) > 0);
        Assert.Equal(50, m.Hp);                                     // 第一次出招:最大生命的 50% 没了
        Assert.True(m.IsAlive);

        Assert.True(arena.Until(() => !m.IsAlive) > 0);             // 第二次出招:降到 0,死(与 [SET HP] 0 同一条路)
        Assert.Equal(0, m.Hp);
        Assert.True(m.Phase == MonsterPhase.Dying || m.Phase == MonsterPhase.Gone);
        Assert.Equal((0, 0), (s.Kills, s.SummonedKills));           // 自己扣死的不算主角的击杀
        arena.Steps(200);
        Assert.Equal(0, s.AliveCount);                              // 房间里没有活着的了
    }
}
