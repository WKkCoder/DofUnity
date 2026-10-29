using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 概率触发类增益与霸体(M49,证据 E-142):配方里千分比概率换成 0 到 1、槽按等级读出概率与持续时间、按指令放出来挂上;
/// 主角正在出招时每挨一击掷一次概率,掷中这一击不产生受击反应(伤害照扣),掷不中、主角没在出招(站着、正在放增益)、这一击无视霸体、被打死的一击都照常反应。
/// 数据全是合成的(见 <see cref="AvatarFixture.ProcBuffSkill"/>):迷你职业站在 (300, 100) 朝左,会伤人的迷你怪物站在 (340, 100) 朝左,
/// 每一击 25 点伤害(公式常量与 <see cref="HeroKnockdownTests"/> 一样);"正在出招"用第一段普攻(<see cref="HeroMotion.Attack"/>,动画带受击框,
/// 怪物在第 16 步前后打得中;主角朝左、怪物在右边,主角打不到它)。
/// </summary>
public sealed class HeroSuperArmorTests
{
    private const int HeroX = 300;
    private const int HeroY = 100;
    private const int Damage = 25;

    private static DamageConstants Constants()
    {
        return new DamageConstants { PhysicalDamageRate = 1000f, MagicalDamageRate = 1000f, PhysicalReduceRate = 100f, MagicalReduceRate = 100f };
    }

    /// <summary>怪物的出招信息:[damage bonus] 25、普通受击(1);<paramref name="none"/> 是 none,<paramref name="ignore"/> 是 [ignore super armor]。</summary>
    private static HeroStrikeInfo Strike(bool none = false, bool ignore = false)
    {
        return new HeroStrikeInfo(AvatarFixture.StrikerDamageBonus, 0, false, 0, 0, -1, false, -1f, 0, false, 0, false, 300, false, false,
            reactionDamage: !none, hitWav: null, reactionNone: none, ignoreSuperArmor: ignore);
    }

    private static SkillRecipes Recipes()
    {
        return new SkillRecipes(new SkillRecipe[0], new[]
        {
            new BuffRecipe(AvatarFixture.ProcBuffSkill, BuffStat.SuperArmorChance, durationColumn: 1, amountStaticIndex: -1, amountColumn: 0, amountScale: 0.001),
        });
    }

    /// <summary>909 学到 <paramref name="level"/> 级,不在快捷栏(只靠指令放)。</summary>
    private static HeroSkills Load(GameData data, int level)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ProcBuffSkill, level));
        book.Restore(levels, new List<KeyValuePair<int, int>>(), 0, 0);
        return HeroSkills.Create(data, chr, 0, book, Recipes());
    }

    // ------------------------------------------------------------------ 配方与槽

    [Fact]
    public void TheRecipeScalesThePermilleColumnIntoAProbabilityAndTheDefaultTableHasOne()
    {
        BuffRecipe recipe = Recipes().FindBuff(AvatarFixture.ProcBuffSkill);
        Assert.Equal((BuffStat.SuperArmorChance, 1, 0, -1, 0.001), (recipe.Stat, recipe.DurationColumn, recipe.AmountColumn, recipe.AmountStaticIndex, recipe.AmountScale));

        // 别的增益不缩放
        Assert.Equal(1.0, new BuffRecipe(1, BuffStat.JumpPower, 0, 0, -1).AmountScale);

        // 默认表里正好有一个概率触发类:概率在第 0 列、时长在第 1 列、千分比
        BuffRecipe real = Assert.Single(SkillRecipes.Default.AllBuffs, b => b.Stat == BuffStat.SuperArmorChance);
        Assert.Equal((1, 0, -1, 0.001), (real.DurationColumn, real.AmountColumn, real.AmountStaticIndex, real.AmountScale));
        Assert.Equal(100, (int)BuffStat.SuperArmorChance);                                  // 内部编号,不与服务端枚举号(1、8)混
    }

    [Fact]
    public void ASlotReadsTheProbabilityAndDurationByLevel()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        HeroSkillSlot level1 = Assert.Single(Load(data, 1).CommandSlots, s => s.SkillId == AvatarFixture.ProcBuffSkill);
        Assert.Equal(AvatarFixture.ProcBuffPermille1 / 1000.0, level1.BuffAmount, 9);       // 千分比换成 0 到 1
        Assert.Equal((double)AvatarFixture.ProcBuffMs1, level1.BuffDurationMs);
        Assert.Equal((6, 1200), (level1.ConsumeMp, level1.CooldownMs));
        Assert.Equal("Up+Buff", level1.Def.Command.ToString());
        Assert.False(level1.StageHasOwnStrike(0));                                          // 施法动作里主角不打人

        HeroSkillSlot level2 = Assert.Single(Load(data, 2).CommandSlots, s => s.SkillId == AvatarFixture.ProcBuffSkill);
        Assert.Equal(AvatarFixture.ProcBuffPermille2 / 1000.0, level2.BuffAmount, 9);
        Assert.Equal((double)AvatarFixture.ProcBuffMs2, level2.BuffDurationMs);
    }

    // ------------------------------------------------------------------ 房间会话

    private sealed class Arena : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public RoomCombatSession Session { get; }
        public HeroVitals Vitals { get; }
        public MonsterActor Monster { get; }
        public TownHero Hero => Session.Hero;

        /// <param name="attackDelayMs">怪物两次出招之间的冷却;默认很长,只打一次。</param>
        /// <param name="skillLevel">909 学到的等级(只靠指令放)。</param>
        /// <param name="monsterX">怪物站的位置;默认紧挨着主角,很快就出招,放增益的测试放远一点,先走过来才打。</param>
        /// <param name="seed">会话的随机种子(掷骰的结果由它决定)。</param>
        public Arena(HeroStrikeInfo strike, int hpMax = 1000, int attackDelayMs = 100000, int skillLevel = 1, int monsterX = 340, int seed = 1)
        {
            Assets = DnfAssets.Open(_fx.Root);
            Vitals = new HeroVitals(1, hpMax, 50, 100, 0, 0);
            var tiles = new List<TileFile>();
            for (int i = 0; i < 5; i++)
            {
                tiles.Add(new TileFile("t" + i, new List<KeyValuePair<string, int>>(), 80, new byte[14 * 6]));
            }
            var map = new RoomMap("map/test/arena.map", "[normal]", tiles, 50, 80, 120, new List<RoomBackground>(), new List<MapAnimation>(),
                new List<int[]> { null, null, null, null }, null);
            var content = new RoomContent(map, null, null, null, null);
            AvatarClass cls = AvatarClass.FromChr(Assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
            AvatarOutfit outfit = AvatarOutfit.CreateDefault(Assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
            TownHero hero = TownHero.Create(Assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), HeroX, HeroY, true, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));

            MonsterModel model = MonsterModel.Read(Assets, AvatarFixture.BlobStrikerMobPath);
            var spawn = new RoomMonsterSpawn(1, 0, 0, monsterX, HeroY, 0, 1, 0, "[fixed]", "[normal]");
            Monster = new MonsterActor(0, spawn, model, 1, 100000, 222.5, attackDelayMs, 0, 0);
            Monster.PhysicalAttack = 40;
            Monster.Strike = strike;

            HeroSkills skills = Load(new GameData(new PvfScriptSource(Assets.Pvf)), skillLevel);
            var plain = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
            Session = new RoomCombatSession(Assets, room, new[] { Monster }, plain, seed, default, default, skills, 0, -1f, new HitStunTable(10, 330, 1000), Vitals, Constants())
            {
                CommandsEnabled = true,
            };
        }

        /// <summary>按指令"上,增益"放出 909:先按上,再按增益键(各一步)。</summary>
        public void Cast()
        {
            Session.Update(10, 0, -1, false, false, SkillKeys.None);
            Session.Update(10, 0, 0, false, false, SkillKeys.CommandBuff);
        }

        /// <summary>按一下攻击键(一步):第一段普攻开始。</summary>
        public void Attack()
        {
            Session.Update(10, 0, 0, true, false, SkillKeys.None);
        }

        /// <summary>推进到条件成立为止(最多 <paramref name="maxSteps"/> 步,每步 10 毫秒);成立返回 true。</summary>
        public bool RunUntil(Func<bool> condition, int maxSteps = 600)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                if (condition())
                {
                    return true;
                }
                Session.Update(10, 0, 0, false, false, SkillKeys.None);
            }
            return condition();
        }

        /// <summary>每 10 毫秒一步推进 <paramref name="ms"/> 毫秒。</summary>
        public void Run(int ms)
        {
            for (int t = 0; t < ms; t += 10)
            {
                Session.Update(10, 0, 0, false, false, SkillKeys.None);
            }
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    /// <summary>挂一个概率为 <paramref name="chance"/> 的增益(绕过施法,直接放进容器)。</summary>
    private static void GiveBuff(RoomCombatSession session, double chance)
    {
        Assert.NotNull(session.Buffs.Apply(AvatarFixture.ProcBuffSkill, BuffStat.SuperArmorChance, chance, 600000));
    }

    [Fact]
    public void AHitWhileAttackingWithACertainBuffDoesNotInterruptTheAttackButStillCostsHealth()
    {
        using var arena = new Arena(Strike());
        RoomCombatSession s = arena.Session;
        GiveBuff(s, 1.0);
        arena.Attack();
        Assert.True(arena.RunUntil(() => s.HeroHits > 0));

        Assert.True(arena.Hero.IsAttacking);                                 // 被打的那一刻还在出招里,没被打断
        Assert.Equal(HeroMotion.Attack, arena.Hero.Motion);
        Assert.Equal((1, 1, 1, 0, 0), (s.HeroHits, s.SuperArmorProcs, s.SuperArmorAbsorbed, arena.Hero.HurtCount, arena.Hero.DownCount));
        Assert.Equal(1000 - Damage, s.Vitals.Hp);                           // 伤害照扣

        arena.Run(600);                                                      // 普攻放完,回到站立;增益还在(时长还没走完)
        Assert.NotEqual(HeroMotion.Attack, arena.Hero.Motion);
        Assert.Equal((0, 1), (arena.Hero.HurtCount, s.Buffs.Count));
    }

    [Fact]
    public void WithoutTheBuffTheSameHitInterruptsTheAttack()
    {
        using var arena = new Arena(Strike());
        RoomCombatSession s = arena.Session;
        arena.Attack();
        Assert.True(arena.RunUntil(() => s.HeroHits > 0));

        Assert.Equal((1, 0, 0, 1), (s.HeroHits, s.SuperArmorProcs, s.SuperArmorAbsorbed, arena.Hero.HurtCount));
        Assert.True(arena.Hero.IsHurt);
        Assert.Equal(1000 - Damage, s.Vitals.Hp);
    }

    [Fact]
    public void AZeroChanceNeverProcsAndAnIdleHeroNeverRolls()
    {
        using var zero = new Arena(Strike());
        GiveBuff(zero.Session, 0.0);                                         // 概率 0:挂得上(时长为正),但永远掷不中(合计为 0 就不掷)
        zero.Attack();
        Assert.True(zero.RunUntil(() => zero.Session.HeroHits > 0));
        Assert.Equal((0, 0, 1), (zero.Session.SuperArmorProcs, zero.Session.SuperArmorAbsorbed, zero.Hero.HurtCount));

        // 概率 100% 但主角站着不动:不在出招,不掷,照常被打断
        using var idle = new Arena(Strike());
        GiveBuff(idle.Session, 1.0);
        Assert.True(idle.RunUntil(() => idle.Session.HeroHits > 0));
        Assert.False(idle.Hero.IsAttacking);
        Assert.Equal(1, idle.Session.Buffs.Count);
        Assert.Equal((0, 0, 1), (idle.Session.SuperArmorProcs, idle.Session.SuperArmorAbsorbed, idle.Hero.HurtCount));
    }

    [Fact]
    public void CastingABuffIsNotAnAttackSoTheProbabilityIsNotRolledAndTheCastIsInterrupted()
    {
        // 放增益的施法状态里主角没有"当前攻击信息"(推断,E-142):就算身上有 100% 的概率增益,被打也照常打断施法,增益挂不上
        using var arena = new Arena(Strike());
        RoomCombatSession s = arena.Session;
        GiveBuff(s, 1.0);
        arena.Cast();
        Assert.Equal(1, s.BuffCasts);
        Assert.True(arena.RunUntil(() => s.HeroHits > 0));

        Assert.Equal((0, 0, 1), (s.SuperArmorProcs, s.SuperArmorAbsorbed, arena.Hero.HurtCount));   // 施法动作也是技能动作,但不掷,被打断
        Assert.True(arena.Hero.IsHurt);
        arena.Run(500);
        Assert.Equal((0, 1), (s.BuffsApplied, s.BuffsInterrupted));
    }

    [Fact]
    public void AnAttackThatIgnoresSuperArmorStillInterruptsAProccedHero()
    {
        using var arena = new Arena(Strike(ignore: true));
        RoomCombatSession s = arena.Session;
        GiveBuff(s, 1.0);
        arena.Attack();
        Assert.True(arena.RunUntil(() => s.HeroHits > 0));

        Assert.Equal((1, 1, 0, 1), (s.HeroHits, s.SuperArmorProcs, s.SuperArmorAbsorbed, arena.Hero.HurtCount));   // 掷中了,但这一击无视霸体档位 1
    }

    [Fact]
    public void ANoReactionHitNeverCountsAsAbsorbedAndAKillingBlowStillKnocksTheHeroDown()
    {
        using var none = new Arena(Strike(none: true));
        GiveBuff(none.Session, 1.0);
        none.Attack();
        Assert.True(none.RunUntil(() => none.Session.HeroHits > 0));
        Assert.Equal((1, 0, 0), (none.Session.SuperArmorProcs, none.Session.SuperArmorAbsorbed, none.Hero.HurtCount));   // 决策函数第 1 步就没有反应,轮不到霸体

        // 被打死的一击:第 3 步先处理,霸体不救命
        using var killed = new Arena(Strike(), hpMax: Damage - 5);
        GiveBuff(killed.Session, 1.0);
        killed.Attack();
        Assert.True(killed.RunUntil(() => killed.Session.HeroHits > 0));
        Assert.True(killed.Session.HeroDead);
        Assert.Equal((1, 0), (killed.Session.SuperArmorProcs, killed.Session.SuperArmorAbsorbed));
        Assert.Equal(HeroMotion.Down, killed.Hero.Motion);
    }

    [Fact]
    public void AHalfChanceRollsOncePerHitSoAboutHalfTheHitsAreAbsorbed()
    {
        // 同一个开局换 40 个随机种子:每一局只挨一击,掷骰各自独立
        int procs = 0, absorbed = 0, hurt = 0;
        for (int seed = 1; seed <= 40; seed++)
        {
            using var arena = new Arena(Strike(), seed: seed);
            GiveBuff(arena.Session, 0.5);
            arena.Attack();
            Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
            procs += arena.Session.SuperArmorProcs;
            absorbed += arena.Session.SuperArmorAbsorbed;
            hurt += arena.Hero.HurtCount;
        }
        Assert.InRange(procs, 8, 32);
        Assert.Equal(procs, absorbed);                                       // 掷中的每一击都没有反应(没有无视、没有死亡)
        Assert.Equal(40 - procs, hurt);                                      // 其余每一击各受击一次
    }

    [Fact]
    public void EveryHitRollsAgainAndTheArmorOfOneHitDoesNotStayForTheNext()
    {
        // 怪物每 400 毫秒左右打一次,主角一直按着攻击键、连招不断;概率 100%:每一击都掷中、每一击都不被打断
        using var arena = new Arena(Strike(), attackDelayMs: 400);
        RoomCombatSession s = arena.Session;
        GiveBuff(s, 1.0);
        for (int i = 0; i < 300 && s.HeroHits < 2; i++)
        {
            arena.Attack();
        }
        Assert.Equal(2, s.HeroHits);
        Assert.Equal((2, 2, 0), (s.SuperArmorProcs, s.SuperArmorAbsorbed, arena.Hero.HurtCount));

        // 概率摘掉以后,下一击就照常受击:霸体不会留到后面
        s.Buffs.Clear();
        for (int i = 0; i < 300 && s.HeroHits < 3; i++)
        {
            arena.Attack();
        }
        Assert.Equal(3, s.HeroHits);
        Assert.Equal((2, 1), (s.SuperArmorProcs, arena.Hero.HurtCount));
    }

    [Fact]
    public void CastingTheBuffByItsCommandHangsTheProbabilityFromTheLevelColumnAndItHoldsForItsDuration()
    {
        using var arena = new Arena(Strike(), skillLevel: 2, monsterX: 560);
        RoomCombatSession s = arena.Session;
        arena.Cast();
        Assert.Equal((1, 1, 1), (s.SkillCasts, s.CommandCasts, s.BuffCasts));
        Assert.Equal(AvatarFixture.ProcBuffSkill, arena.Hero.SkillId);
        Assert.Equal(50 - 6, arena.Vitals.Mp);
        Assert.Equal(0, s.Buffs.Count);                                      // 施法动作播完才挂

        Assert.True(arena.RunUntil(() => s.Buffs.Count == 1, maxSteps: 200));
        HeroBuff b = s.Buffs.Find(AvatarFixture.ProcBuffSkill);
        Assert.Equal((BuffStat.SuperArmorChance, AvatarFixture.ProcBuffPermille2 / 1000.0, (double)AvatarFixture.ProcBuffMs2),
            (b.Stat, b.Amount, b.DurationMs));
        Assert.Equal(0, s.SuperArmorProcs);

        Assert.NotEqual(HeroMotion.Skill, arena.Hero.Motion);                // 这时施法动作已经放完
        arena.Run(AvatarFixture.ProcBuffMs2 + 100);
        Assert.False(s.Buffs.Has(AvatarFixture.ProcBuffSkill));              // 时长走完:增益失效
        Assert.Equal(1, s.Buffs.Expired);
    }

    [Fact]
    public void TheBuffIsGoneWithTheDeathOfTheHero()
    {
        using var arena = new Arena(Strike(), hpMax: Damage - 5);
        RoomCombatSession s = arena.Session;
        GiveBuff(s, 1.0);
        arena.Attack();
        Assert.True(arena.RunUntil(() => s.HeroDead));
        arena.Run(20);
        Assert.Equal(0, s.Buffs.Count);                                      // 倒下摘掉全部增益(E-141,推断)
    }
}
