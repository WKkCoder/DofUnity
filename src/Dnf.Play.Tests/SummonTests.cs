using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Act;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;
using static Dnf.Play.Tests.ExpTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 脚本召唤怪物在房间会话与构造器两头的规则(M41,证据 E-134):主角打死召唤物没有击杀战绩、经验与连击杀;
/// <see cref="RoomMonsterBuilder"/> 造怪(等级覆盖、缺图缺脚本不造)与召唤等级的上限。数据全是合成的。
/// </summary>
public class SummonTests
{
    private static GameData DataOf(DnfAssets assets) => new GameData(new PvfScriptSource(assets.Pvf));

    /// <summary>会话里要有一只一直活着的怪物(房间里没有活怪时不再召唤),放得远远的;再把程序库与工厂接上。</summary>
    private static RoomCombatTests.Arena ArenaWithSummons(HeroProgress progress, int summonHp = 1)
    {
        var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, progress: progress, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        s.AttachActs(new ActLibrary(arena.Assets, DataOf(arena.Assets)));
        MonsterModel model = MonsterModel.Read(arena.Assets, AvatarFixture.BlobMobPath);
        s.UseSummons((code, level, index) =>
            new MonsterActor(index, new RoomMonsterSpawn(code, 0, 0, 0, 0, 0, 1, 0, "[fixed]", "[normal]"), model, level, summonHp, 222.5, 1500, 0));
        return arena;
    }

    [Fact]
    public void KillingASummonedMonsterGivesNoKillCreditExperienceOrComboKill()
    {
        HeroProgress progress = Progress(level: 10, mobExp: 160, mobLevel: 10);
        using RoomCombatTests.Arena arena = ArenaWithSummons(progress);
        RoomCombatSession s = arena.Session;
        s.ActHost.SummonMonster(s.Monsters[0].ActSelf, 7, 10, 340, 100, 0, true, 0);   // 站在主角面前(主角在 300 朝右)
        s.Update(10, 0, 0, true);                                                      // 攻击键按下,召唤的怪物在这一步末尾出现
        for (int i = 0; i < 100 && s.SummonedKills == 0; i++)
        {
            s.Update(10, 0, 0, false);
        }

        Assert.Equal(1, s.SummonedKills);
        Assert.Equal(1, s.Hits);                                                       // 打中是打中了
        Assert.Equal((0, 0, 0), (s.Kills, s.ComboKills, s.LastKillExp));               // 但没有战绩、没有连击杀、没有经验
        Assert.Equal((0L, 0, 0), (s.ExpEarned, progress.Exp - 4500, s.LevelUps));
        Assert.Equal(1, s.AliveCount);                                                 // 那只远处的怪物还活着
        Assert.True(s.Monsters[1].IsSummoned);
        Assert.False(s.Monsters[1].IsAlive);
    }

    [Fact]
    public void AMapMonsterKilledTheSameWayStillCountsAsAKill()
    {
        // 对照:同一套装置里,不是召唤出来的怪物被打死照常有战绩与经验
        HeroProgress progress = Progress(level: 10, mobExp: 160, mobLevel: 10);
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 1, progress: progress, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        s.Update(10, 0, 0, true);
        for (int i = 0; i < 100 && s.Kills == 0; i++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.Equal((1, 0), (s.Kills, s.SummonedKills));
        Assert.Equal(80, s.LastKillExp);
        Assert.False(s.Monsters[0].IsSummoned);
    }

    // ------------------------------------------------------------------ 构造器

    private static RoomMonsterBuilder BuilderOf(RoomCombatTests.Arena arena, int difficulty = 0)
    {
        return new RoomMonsterBuilder(arena.Assets, DataOf(arena.Assets), MonsterCatalog.Read(arena.Assets), arena.Hero, 1120, 1, difficulty, null, null);
    }

    [Fact]
    public void TheBuilderMakesMonstersFromTheCatalogAndTheLevelOverrideWins()
    {
        using var arena = new RoomCombatTests.Arena(System.Array.Empty<(int X, int Y)>());
        RoomMonsterBuilder builder = BuilderOf(arena);
        var spawn = new RoomMonsterSpawn(1, 0, 0, 100, 50, 0, 1, 0, "[fixed]", "[normal]");

        MonsterActor map = builder.Build(4, spawn);
        Assert.NotNull(map);
        Assert.Equal((4, 1, 1, 100.0, 50.0), (map.Index, map.Code, map.Level, map.X, map.Y));    // 合成的 .mob 没写 [level]:取 1
        Assert.False(map.IsSummoned);
        Assert.NotNull(map.Panel);
        Assert.NotNull(map.Defense);

        MonsterActor summoned = builder.Build(5, spawn, 9);
        Assert.Equal((5, 9), (summoned.Index, summoned.Level));
        Assert.False(summoned.IsSummoned);                                          // 标成召唤物是会话放出来时的事,构造器不管

        Assert.True(builder.CanBuild(1));
        Assert.False(builder.CanBuild(3));                                          // 目录指向的 .mob 不存在
        Assert.False(builder.CanBuild(999));                                        // 目录里没有
        Assert.Null(builder.Build(6, new RoomMonsterSpawn(3, 0, 0, 0, 0, 0, 1, 0, "[fixed]", "[normal]")));
        Assert.Null(builder.Build(7, new RoomMonsterSpawn(999, 0, 0, 0, 0, 0, 1, 0, "[fixed]", "[normal]")));
    }

    [Fact]
    public void TheBuilderHandsItsComputedStatsToTheActorSoAppendagesRecomputeFromThem()
    {
        using var arena = new RoomCombatTests.Arena(System.Array.Empty<(int X, int Y)>());
        MonsterActor m = BuilderOf(arena).Build(4, new RoomMonsterSpawn(1, 0, 0, 100, 50, 0, 1, 0, "[fixed]", "[normal]"), 7);
        Assert.Equal(7, m.Level);
        Assert.NotNull(m.BaseStats);                                                 // 造怪时算出来的那一块(M63,E-161)
        DefenderPanel original = m.Defense;
        int physical = original.PhysicalDefense;
        int hpMax = m.HpMax;

        var self = m.ActSelf;
        m.AddStatAppendage(new StatChange(StatChangeTypes.EquipmentPhysicalDefense, false, 50), 0, self);
        Assert.Equal(physical + 50, m.Defense.PhysicalDefense);
        Assert.Equal(m.Level, m.Defense.Level);
        m.AddStatAppendage(new StatChange(StatChangeTypes.HpMax, false, 40), 0, self);
        Assert.Equal(System.Math.Max(1, hpMax + 40), m.HpMax);
        m.RemoveStatAppendage(new StatChange(StatChangeTypes.EquipmentPhysicalDefense, false, 50), self);
        m.RemoveStatAppendage(new StatChange(StatChangeTypes.HpMax, false, 40), self);
        Assert.Same(original, m.Defense);
        Assert.Equal(hpMax, m.HpMax);
    }

    [Fact]
    public void ASummonLevelIsCappedByTheDifficultyTableRowsAndTheClientCeiling()
    {
        Assert.Equal(7, RoomMonsterBuilder.ClampSummonLevel(7, 0));                 // 没有难度表:只有 200 的上限
        Assert.Equal(200, RoomMonsterBuilder.ClampSummonLevel(500, 0));
        Assert.Equal(60, RoomMonsterBuilder.ClampSummonLevel(150, 60));             // 超过这一档的行数就取行数
        Assert.Equal(40, RoomMonsterBuilder.ClampSummonLevel(40, 60));
        Assert.Equal(200, RoomMonsterBuilder.ClampSummonLevel(500, 300));           // 行数比 200 多时,仍受 200 的上限

        using var arena = new RoomCombatTests.Arena(System.Array.Empty<(int X, int Y)>());
        Assert.Equal(200, BuilderOf(arena).ClampSummonLevel(1000));                 // 合成数据里没有难度表
    }

    [Fact]
    public void PreloadedSummonCodesComeFromTheSummonBehaviorsOfTheProgramsThatWerePreloaded()
    {
        using var arena = new RoomCombatTests.Arena(System.Array.Empty<(int X, int Y)>());
        var library = new ActLibrary(arena.Assets, DataOf(arena.Assets));
        Assert.Empty(library.SummonCodes);
        library.Preload(library.Program("monster/blob/action/actsummon.act"));
        Assert.Equal(new[] { AvatarFixture.SummonedCode }, library.SummonCodes.ToArray());
        library.Preload(library.Program("monster/blob/action/actplain.act"));        // 没有召唤的程序不加
        Assert.Single(library.SummonCodes);
    }
}
