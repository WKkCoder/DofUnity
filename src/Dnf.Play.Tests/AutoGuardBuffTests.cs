using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 自动防御类增益(职业专属增益,证据 E-141、E-142、E-164):放出来像别的增益一样挂上(概率取 [level info] 的一列、时长取另一列),
/// 生效期间主角站着、走着、跑着(腾得出手、不在姿态里)时挨到一击能挡的攻击,每次掷一次概率;掷中就像自己按了防御姿态——
/// 转向攻击方、进借用的防御姿态技能的姿态、伤害乘那个技能的穿过百分比、没有受击反应、后滑;没掷中、主角在出招或受伤、防御姿态技能没学、
/// 增益到期、这一击没有受击反应、被打死的一击都照常受击。
/// 数据全是合成的:909(<see cref="AvatarFixture.ProcBuffSkill"/>,两列千分比 / 毫秒)在这里被配成自动防御增益,借用 914(<see cref="AvatarFixture.GuardSkill"/>)的姿态;
/// 迷你职业站在 (300, 100) 朝左,会伤人的迷你怪物每一击 25 点伤害(公式常量与 <see cref="SkillGuardTests"/> 一样);迎面是怪物站在左边 (260, 100)、背后是站在右边 (340, 100)。
/// </summary>
public sealed class AutoGuardBuffTests
{
    private const int HeroX = 300;
    private const int HeroY = 100;

    private static DamageConstants Constants()
    {
        return new DamageConstants { PhysicalDamageRate = 1000f, MagicalDamageRate = 1000f, PhysicalReduceRate = 100f, MagicalReduceRate = 100f };
    }

    /// <summary>怪物的出招信息:[damage bonus] 25;<paramref name="none"/> 是 [damage reaction] none,其余是普通受击;<paramref name="magical"/> 是魔法攻击。</summary>
    private static HeroStrikeInfo Strike(bool none = false, bool magical = false, int hitValue = -1)
    {
        return new HeroStrikeInfo(AvatarFixture.StrikerDamageBonus, 0, false, 0, 0, hitValue, false, -1f, 0, false, 0, magical, 300, false, false,
            reactionDamage: !none, hitWav: null, reactionNone: none);
    }

    /// <summary>
    /// 909 配成自动防御增益:概率取第 0 列(千分比 × <paramref name="scale"/>)、时长取第 1 列、借用 <paramref name="guardSkillId"/> 的姿态;
    /// 914 是防御姿态配方。<paramref name="classBuff"/> 为假时不登记 909。
    /// </summary>
    private static SkillRecipes Recipes(double scale = 0.001, int guardSkillId = AvatarFixture.GuardSkill, bool classBuff = true)
    {
        var guards = new[]
        {
            new GuardRecipe(AvatarFixture.GuardSkill, motionIndex: AvatarFixture.GuardEtcIndex, minHoldStaticIndex: 0, recoilSpeedColumn: 0, recoilMsColumn: 1,
                physicalColumn: 2, magicalColumn: 3),
        };
        var buffs = classBuff
            ? new[] { new BuffRecipe(AvatarFixture.ProcBuffSkill, BuffStat.AutoGuardChance, durationColumn: 1, amountStaticIndex: -1, amountColumn: 0, amountScale: scale, guardSkillId: guardSkillId) }
            : new BuffRecipe[0];
        return new SkillRecipes(new SkillRecipe[0], null, guards, buffs);
    }

    /// <summary>909 学到 <paramref name="buffLevel"/> 级、914 学到 <paramref name="guardLevel"/> 级(0 就不学);都不在快捷栏,只靠指令放。</summary>
    private static HeroSkills Load(GameData data, int buffLevel, int guardLevel, SkillRecipes recipes)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        if (buffLevel > 0)
        {
            levels.Add(new KeyValuePair<int, int>(AvatarFixture.ProcBuffSkill, buffLevel));
        }
        if (guardLevel > 0)
        {
            levels.Add(new KeyValuePair<int, int>(AvatarFixture.GuardSkill, guardLevel));
        }
        book.Restore(levels, new List<KeyValuePair<int, int>>(), 0, 0);
        return HeroSkills.Create(data, chr, 0, book, recipes);
    }

    // ------------------------------------------------------------------ 配方与槽

    [Fact]
    public void TheClassBuffRecipeLivesInItsOwnTableAndTheSlotReadsItsNumbersByLevel()
    {
        SkillRecipes recipes = Recipes();
        Assert.Equal((1, 0), (recipes.ClassBuffCount, recipes.BuffCount));                   // 职业专属增益单放一张表,不算通用增益
        Assert.Same(Assert.Single(recipes.AllClassBuffs), recipes.FindClassBuff(AvatarFixture.ProcBuffSkill));
        Assert.Null(recipes.FindBuff(AvatarFixture.ProcBuffSkill));
        Assert.Null(recipes.FindClassBuff(AvatarFixture.ProcBuffSkill + 1));
        Assert.Equal(AvatarFixture.GuardSkill, recipes.FindClassBuff(AvatarFixture.ProcBuffSkill).GuardSkillId);
        Assert.Equal(0, new BuffRecipe(1, BuffStat.JumpPower, 0, 0, -1).GuardSkillId);        // 别的增益不借姿态

        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        HeroSkillSlot level1 = Assert.Single(Load(data, 1, 1, recipes).CommandSlots, s => s.SkillId == AvatarFixture.ProcBuffSkill);
        Assert.Equal(BuffStat.AutoGuardChance, level1.Buff.Stat);
        Assert.Equal(AvatarFixture.ProcBuffPermille1 / 1000.0, level1.BuffAmount, 9);         // 千分比换成 0 到 1
        Assert.Equal((double)AvatarFixture.ProcBuffMs1, level1.BuffDurationMs);
        Assert.False(level1.StageHasOwnStrike(0));                                            // 施法动作里主角不打人

        HeroSkillSlot level2 = Assert.Single(Load(data, 2, 1, recipes).CommandSlots, s => s.SkillId == AvatarFixture.ProcBuffSkill);
        Assert.Equal(AvatarFixture.ProcBuffPermille2 / 1000.0, level2.BuffAmount, 9);
        Assert.Equal((double)AvatarFixture.ProcBuffMs2, level2.BuffDurationMs);

        // 表里没登记它(别的表也没有):这个技能没有放法,不进指令槽
        Assert.DoesNotContain(Load(data, 1, 1, Recipes(classBuff: false)).CommandSlots, s => s.SkillId == AvatarFixture.ProcBuffSkill);

        // 登记在通用增益表里的同一个配方:槽照样读到数,但表的个数记在另一边
        var ordinary = new SkillRecipes(new SkillRecipe[0], recipes.AllClassBuffs);
        Assert.Equal((0, 1), (ordinary.ClassBuffCount, ordinary.BuffCount));
        Assert.NotNull(Assert.Single(Load(data, 1, 1, ordinary).CommandSlots, s => s.SkillId == AvatarFixture.ProcBuffSkill).Buff);
    }

    [Fact]
    public void TheDefaultTableHoldsOneClassBuffThatBorrowsAnImplementedGuardStanceAndKeepsTheOrdinaryTableUntouched()
    {
        BuffRecipe real = Assert.Single(SkillRecipes.Default.AllClassBuffs);
        Assert.Equal(1, SkillRecipes.Default.ClassBuffCount);
        Assert.Same(real, SkillRecipes.Default.FindClassBuff(real.SkillId));
        Assert.Null(SkillRecipes.Default.FindBuff(real.SkillId));                              // 通用增益表里没有它
        Assert.DoesNotContain(SkillRecipes.Default.AllBuffs, b => b.Stat == BuffStat.AutoGuardChance);
        Assert.True(HeroSkills.IsImplemented(real.SkillId));

        // 概率取第 0 列、百分数换成 0 到 1;时长取第 1 列;借用的是配方表里的防御姿态技能
        Assert.Equal((BuffStat.AutoGuardChance, 1, 0, -1, 0.01), (real.Stat, real.DurationColumn, real.AmountColumn, real.AmountStaticIndex, real.AmountScale));
        Assert.NotNull(SkillRecipes.Default.FindGuard(real.GuardSkillId));
        Assert.Equal(101, (int)BuffStat.AutoGuardChance);                                      // 内部编号,不与服务端枚举号(1、8)和霸体(100)混
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

        /// <param name="strike">怪物的出招信息。</param>
        /// <param name="monsterX">怪物站的位置:260 在主角朝向(朝左)的那一侧(迎面),340 在背后,很远的位置(900)是怪物够不着、要走过来。</param>
        /// <param name="attackDelayMs">怪物两次出招之间的冷却;默认很长,只打一次。</param>
        /// <param name="buffLevel">909 学到的等级(0 不学);914 学到 <paramref name="guardLevel"/> 级(0 不学)。</param>
        /// <param name="thrower">怪物换成投掷手(合成的扔东西的迷你怪物):它的一击是投射物,打中主角时没有怪物当攻击方。</param>
        /// <param name="seed">会话的随机种子(掷骰的结果由它决定)。</param>
        public Arena(HeroStrikeInfo strike, int hpMax = 1000, int attackDelayMs = 100000, int buffLevel = 1, int guardLevel = 1, int monsterX = 260,
            bool heroFacingLeft = true, bool thrower = false, int seed = 1, SkillRecipes recipes = null)
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
            TownHero hero = TownHero.Create(Assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), HeroX, HeroY, heroFacingLeft, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));

            MonsterModel model = MonsterModel.Read(Assets, thrower ? AvatarFixture.BlobThrowerMobPath : AvatarFixture.BlobStrikerMobPath);
            var spawn = new RoomMonsterSpawn(1, 0, 0, monsterX, HeroY, 0, 1, 0, "[fixed]", "[normal]");
            Monster = new MonsterActor(0, spawn, model, 1, 100000, 222.5, attackDelayMs, 0, 0);
            Monster.PhysicalAttack = 40;
            Monster.MagicalAttack = 40;
            Monster.Strike = strike;
            if (thrower)
            {
                var data = new GameData(new PvfScriptSource(Assets.Pvf));
                Monster.SetThrows(MonsterThrows.Load(Assets, data, AvatarFixture.BlobThrowerMobPath,
                    ThrowAttackRecord.Parse(new TagTable(data.ReadScript(AvatarFixture.BlobThrowerMobPath)))));
            }

            HeroSkills skills = Load(new GameData(new PvfScriptSource(Assets.Pvf)), buffLevel, guardLevel, recipes ?? Recipes());
            var plain = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
            Session = new RoomCombatSession(Assets, room, new[] { Monster }, plain, seed, default, default, skills, 0, -1f, new HitStunTable(10, 330, 1000), Vitals, Constants())
            {
                CommandsEnabled = true,
            };
        }

        /// <summary>挂一个概率为 <paramref name="chance"/> 的自动防御增益(绕过施法,直接放进容器;归在 909 名下,借姿态的配方从 909 的槽里读)。</summary>
        public void GiveBuff(double chance, double durationMs = 600000)
        {
            Assert.NotNull(Session.Buffs.Apply(AvatarFixture.ProcBuffSkill, BuffStat.AutoGuardChance, chance, durationMs));
        }

        private void Step(int dirX, int dirY, bool attack, SkillKeys pressed = SkillKeys.None)
        {
            Session.Update(10, dirX, dirY, attack, false, pressed);
        }

        /// <summary>按指令"上,增益"放出 909:先按上,再按增益键(各一步)。</summary>
        public void CastBuff()
        {
            Step(0, -1, false);
            Step(0, 0, false, SkillKeys.CommandBuff);
        }

        /// <summary>按指令"下、下、攻击"手动放 914 的防御姿态:下点一下松开、再按住下、按攻击键(攻击键之后一直按着)。</summary>
        public void TypeGuardCommand()
        {
            Session.SkillsHeld = SkillKeys.None;
            Step(0, 1, false);
            Step(0, 0, false);
            Step(0, 1, false);
            Session.SkillsHeld = SkillKeys.Attack;
            Step(0, 1, true);
        }

        /// <summary>按一下攻击键(一步):第一段普攻开始,主角进入出招状态。</summary>
        public void Attack()
        {
            Step(0, 0, true);
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
                Step(0, 0, false);
            }
            return condition();
        }

        /// <summary>每 10 毫秒一步推进 <paramref name="ms"/> 毫秒(不按任何键;<see cref="RoomCombatSession.SkillsHeld"/> 保持原样)。</summary>
        public void Run(int ms)
        {
            for (int t = 0; t < ms; t += 10)
            {
                Step(0, 0, false);
            }
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    /// <summary>没有任何增益时同一击的伤害(对照用):怪物打一次、读主角损失的血量。</summary>
    private static int UnguardedDamage(HeroStrikeInfo strike, int monsterX = 260)
    {
        using var arena = new Arena(strike, monsterX: monsterX);
        Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
        return arena.Vitals.HpMax - arena.Vitals.Hp;
    }

    /// <summary>没有任何增益时怪物的第一击落在第几步(每步 10 毫秒;对照用,用来卡增益到期的边界)。</summary>
    private static int StepsToFirstHit(HeroStrikeInfo strike, int monsterX = 260)
    {
        using var arena = new Arena(strike, monsterX: monsterX);
        int steps = 0;
        while (arena.Session.HeroHits == 0 && steps < 1000)
        {
            arena.Run(10);
            steps++;
        }
        Assert.True(arena.Session.HeroHits > 0);
        return steps;
    }

    // ------------------------------------------------------------------ 放出来挂上

    [Fact]
    public void CastingTheBuffByItsCommandHangsTheChanceFromTheLevelColumnAndItLastsExactlyItsDuration()
    {
        using var arena = new Arena(Strike(), monsterX: 900);
        RoomCombatSession s = arena.Session;
        arena.CastBuff();
        Assert.Equal(1, s.BuffCasts);
        Assert.Equal(0, s.Buffs.Count);                                                        // 施法动作放完才挂上
        Assert.True(arena.RunUntil(() => s.BuffsApplied == 1));

        HeroBuff buff = Assert.Single(s.Buffs.Active);
        Assert.Equal((AvatarFixture.ProcBuffSkill, BuffStat.AutoGuardChance), (buff.SkillId, buff.Stat));
        Assert.Equal(AvatarFixture.ProcBuffPermille1 / 1000.0, s.Buffs.Total(BuffStat.AutoGuardChance), 9);
        Assert.Equal((double)AvatarFixture.ProcBuffMs1, buff.DurationMs);

        // 持续时间:7000 毫秒里一直在,过了 7000 毫秒失效(两侧各留 100 毫秒)
        arena.Run(AvatarFixture.ProcBuffMs1 - 100);
        Assert.Equal(1, s.Buffs.Count);
        arena.Run(200);
        Assert.Equal(0, s.Buffs.Count);
        Assert.Equal(0.0, s.Buffs.Total(BuffStat.AutoGuardChance));
    }

    [Fact]
    public void ACastInterruptedByAHitHangsNothingAndThenNothingGuardsTheHero()
    {
        // 怪物迎面、紧挨着:施法动作里挨了一击(这时还没有增益,谈不上防御),施法被打断,增益挂不上
        using var arena = new Arena(Strike(), monsterX: 260);
        RoomCombatSession s = arena.Session;
        arena.CastBuff();
        Assert.True(arena.RunUntil(() => s.HeroHits > 0));
        arena.Run(500);
        Assert.Equal((0, 1, 0), (s.BuffsApplied, s.BuffsInterrupted, s.Buffs.Count));
        Assert.Equal((0, 0), (s.AutoGuardBlocks, s.GuardBlocks));
        Assert.Equal(1, arena.Hero.HurtCount);
    }

    // ------------------------------------------------------------------ 掷骰

    [Fact]
    public void ACertainChanceGuardsEveryFrontalHitAndAZeroChanceOrNoBuffNeverDoes()
    {
        for (int seed = 1; seed <= 12; seed++)
        {
            using var sure = new Arena(Strike(), seed: seed);
            sure.GiveBuff(1.0);
            Assert.True(sure.RunUntil(() => sure.Session.HeroHits > 0));
            Assert.Equal((1, 1, 0), (sure.Session.AutoGuardBlocks, sure.Session.GuardBlocks, sure.Hero.HurtCount));

            // 概率 0:挂得上(时长为正),但永远掷不中
            using var zero = new Arena(Strike(), seed: seed);
            zero.GiveBuff(0.0);
            Assert.True(zero.RunUntil(() => zero.Session.HeroHits > 0));
            Assert.Equal((0, 0, 1), (zero.Session.AutoGuardBlocks, zero.Session.GuardBlocks, zero.Hero.HurtCount));

            // 没有增益:照常受击
            using var none = new Arena(Strike(), seed: seed);
            Assert.True(none.RunUntil(() => none.Session.HeroHits > 0));
            Assert.Equal((0, 0, 1), (none.Session.AutoGuardBlocks, none.Session.GuardBlocks, none.Hero.HurtCount));
        }
    }

    [Fact]
    public void AQuarterChanceGuardsAboutAQuarterOfTheHitsEachHitRollingOnceAndTheRestAreHurtAsUsual()
    {
        // 同一个开局换 60 个随机种子:每一局只挨一击,掷骰各自独立;概率 25% 时期望 15 次(标准差约 3.4),
        // 掷骰方向反了会到 45 次上下,概率没换算(按百分之一的量级)几乎一次也掷不中
        int guarded = 0, hurt = 0;
        for (int seed = 1; seed <= 60; seed++)
        {
            using var arena = new Arena(Strike(), seed: seed);
            arena.GiveBuff(0.25);
            Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
            guarded += arena.Session.AutoGuardBlocks;
            hurt += arena.Hero.HurtCount;
        }
        Assert.InRange(guarded, 4, 28);
        Assert.Equal(60 - guarded, hurt);                                                      // 掷中的一击没有受击反应,其余每一击各受击一次
    }

    [Fact]
    public void EveryHitRollsAgainSoACertainChanceGuardsAllOfSeveralHitsInARow()
    {
        using var arena = new Arena(Strike(), attackDelayMs: 600);
        RoomCombatSession s = arena.Session;
        arena.GiveBuff(1.0);
        Assert.True(arena.RunUntil(() => s.HeroHits >= 3, 3000));
        Assert.Equal((s.HeroHits, s.HeroHits, 0), (s.AutoGuardBlocks, s.GuardBlocks, arena.Hero.HurtCount));   // 每一击各挡一次,没有一击漏掉

        // 增益摘掉之后下一击就照常受击:自动防御不会留到后面
        int blocks = s.AutoGuardBlocks;
        s.Buffs.Clear();
        Assert.True(arena.RunUntil(() => s.HeroHits >= blocks + 1, 3000));
        Assert.Equal((blocks, 1), (s.AutoGuardBlocks, arena.Hero.HurtCount));
    }

    // ------------------------------------------------------------------ 触发条件

    [Fact]
    public void WithoutTheBorrowedStanceSkillLearnedOrWithAnotherSkillNamedTheBuffGuardsNothing()
    {
        // 防御姿态技能没学:增益挂得上,但没有姿态可借
        using var unlearned = new Arena(Strike(), guardLevel: 0);
        unlearned.GiveBuff(1.0);
        Assert.True(unlearned.RunUntil(() => unlearned.Session.HeroHits > 0));
        Assert.Equal((0, 0, 1), (unlearned.Session.AutoGuardBlocks, unlearned.Session.GuardBlocks, unlearned.Hero.HurtCount));

        // 配方借的是别的技能号(没有这个技能):914 学了也不借
        using var other = new Arena(Strike(), recipes: Recipes(guardSkillId: AvatarFixture.GuardSkill + 1));
        other.GiveBuff(1.0);
        Assert.True(other.RunUntil(() => other.Session.HeroHits > 0));
        Assert.Equal((0, 0, 1), (other.Session.AutoGuardBlocks, other.Session.GuardBlocks, other.Hero.HurtCount));

        // 对照:都对上就挡得下
        using var both = new Arena(Strike());
        both.GiveBuff(1.0);
        Assert.True(both.RunUntil(() => both.Session.HeroHits > 0));
        Assert.Equal((1, 0), (both.Session.AutoGuardBlocks, both.Hero.HurtCount));
    }

    [Fact]
    public void ABuffThatExpiresJustBeforeTheHitGuardsNothingAndOneThatExpiresJustAfterStillDoes()
    {
        int hitMs = StepsToFirstHit(Strike()) * 10;
        Assert.True(hitMs > 100);

        using var late = new Arena(Strike());
        late.GiveBuff(1.0, hitMs + 50);                                                        // 挨打之后 50 毫秒才到期
        Assert.True(late.RunUntil(() => late.Session.HeroHits > 0));
        Assert.Equal((1, 0), (late.Session.AutoGuardBlocks, late.Hero.HurtCount));

        using var early = new Arena(Strike());
        early.GiveBuff(1.0, hitMs - 50);                                                       // 挨打之前 50 毫秒就到期
        Assert.True(early.RunUntil(() => early.Session.HeroHits > 0));
        Assert.Equal(0, early.Session.Buffs.Count);
        Assert.Equal((0, 1), (early.Session.AutoGuardBlocks, early.Hero.HurtCount));
    }

    [Fact]
    public void AHeroInTheMiddleOfAnAttackIsNotGuardedAndGetsHurtAsUsualWhileAStandingOneIs()
    {
        // 主角正在出招(第一段普攻):腾不出手进姿态,照常受击(怪物在背后 340,主角朝左打不到它)
        using var busy = new Arena(Strike(), monsterX: 340);
        busy.GiveBuff(1.0);
        busy.Attack();
        Assert.True(busy.RunUntil(() => busy.Session.HeroHits > 0));
        Assert.Equal((0, 0, 1), (busy.Session.AutoGuardBlocks, busy.Session.GuardBlocks, busy.Hero.HurtCount));
        Assert.Equal(1000 - UnguardedDamage(Strike(), 340), busy.Vitals.Hp);                   // 没有折算,整击扣血

        // 对照:同一个位置、站着不动的主角挡得下(转身迎向背后的怪物)
        using var idle = new Arena(Strike(), monsterX: 340);
        idle.GiveBuff(1.0);
        Assert.True(idle.RunUntil(() => idle.Session.HeroHits > 0));
        Assert.Equal((1, 0), (idle.Session.AutoGuardBlocks, idle.Hero.HurtCount));
    }

    [Fact]
    public void AHeroAlreadyInAHandMadeStanceKeepsTheHandMadeRulesAndTheBuffNeitherDoubleRollsNorRescuesTheBackHit()
    {
        // 手按的姿态迎面挡下:算手按的一次,自动防御不再插手
        using var front = new Arena(Strike(), monsterX: 260);
        front.GiveBuff(1.0);
        front.TypeGuardCommand();
        Assert.True(front.Hero.Guarding);
        Assert.True(front.RunUntil(() => front.Session.HeroHits > 0));
        Assert.Equal((1, 0), (front.Session.GuardBlocks, front.Session.AutoGuardBlocks));
        Assert.Equal(0, front.Hero.HurtCount);

        // 手按的姿态里被打到背面:姿态挡不下,也不由自动防御接手——照常受击、姿态被打断
        using var back = new Arena(Strike(), monsterX: 340);
        back.GiveBuff(1.0);
        back.TypeGuardCommand();
        Assert.True(back.RunUntil(() => back.Session.HeroHits > 0));
        Assert.Equal((0, 0, 1), (back.Session.GuardBlocks, back.Session.AutoGuardBlocks, back.Hero.HurtCount));
        Assert.False(back.Hero.Guarding);
    }

    [Fact]
    public void AHitWithoutAReactionIsNeverGuardedAndAHitWithOneIs()
    {
        int unguarded = UnguardedDamage(Strike(none: true));
        using var none = new Arena(Strike(none: true));
        none.GiveBuff(1.0);
        Assert.True(none.RunUntil(() => none.Session.HeroHits > 0));
        Assert.Equal((0, 0, 0), (none.Session.AutoGuardBlocks, none.Session.GuardBlocks, none.Hero.HurtCount));   // 这一击本来就没有受击反应
        Assert.False(none.Hero.Guarding);
        Assert.Equal(none.Vitals.HpMax - unguarded, none.Vitals.Hp);                           // 整击扣血,没有折算

        using var ordinary = new Arena(Strike());
        ordinary.GiveBuff(1.0);
        Assert.True(ordinary.RunUntil(() => ordinary.Session.HeroHits > 0));
        Assert.Equal(1, ordinary.Session.AutoGuardBlocks);
    }

    // ------------------------------------------------------------------ 挡下的效果

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void ABlockedHitIsReducedByTheBorrowedSkillsColumnForItsLevelAndKindAndProducesNoReaction(int guardLevel, bool magical)
    {
        int unguarded = UnguardedDamage(Strike(magical: magical, hitValue: 200));
        Assert.True(unguarded > 1);

        // 穿过的百分比 = 100 减物理(第 2 列)或魔法(第 3 列),列值按借来的技能自己的等级取;增益等级只管概率与时长
        int reduce = magical
            ? (guardLevel == 1 ? AvatarFixture.GuardMagical1 : AvatarFixture.GuardMagical2)
            : (guardLevel == 1 ? AvatarFixture.GuardPhysical1 : AvatarFixture.GuardPhysical2);
        int expected = unguarded * (100 - reduce) / 100;                                       // 整数截断

        using var arena = new Arena(Strike(magical: magical, hitValue: 200), guardLevel: guardLevel, buffLevel: 3 - guardLevel);
        RoomCombatSession s = arena.Session;
        arena.GiveBuff(1.0);
        Assert.True(arena.RunUntil(() => s.HeroHits > 0));

        Assert.Equal((1, 1, 1), (s.HeroHits, s.GuardBlocks, s.AutoGuardBlocks));
        Assert.Equal(arena.Vitals.HpMax - expected, arena.Vitals.Hp);
        Assert.Equal(expected, s.HeroDamageTaken);
        Assert.Equal(unguarded - expected, s.GuardDamagePrevented);

        // 没有受击反应:没硬直、没倒地、不命中停顿(这一击带命中停顿,主角不被定住)、不改变姿态
        Assert.Equal((0, 0), (arena.Hero.HurtCount, arena.Hero.DownCount));
        Assert.False(arena.Hero.IsHurt);
        Assert.Equal(0.0, s.HeroPauseMs);
    }

    [Fact]
    public void TheAutomaticStanceIsTheBorrowedSkillsStanceFreeOfCostAndLastsOnlyUntilTheRecoilEndsWhenTheKeyIsNotHeld()
    {
        using var arena = new Arena(Strike(), monsterX: 260);
        RoomCombatSession s = arena.Session;
        arena.GiveBuff(1.0);
        Assert.False(arena.Hero.Guarding);
        Assert.True(arena.RunUntil(() => s.HeroHits > 0));

        Assert.True(arena.Hero.Guarding);
        Assert.Equal((HeroMotion.Skill, AvatarFixture.GuardSkill), (arena.Hero.Motion, arena.Hero.SkillId));
        Assert.Equal(1, arena.Hero.GuardBlocks);
        Assert.True(arena.Hero.GuardRecoiling);
        Assert.Equal(1, arena.Hero.CurrentFrame);                                              // 后滑阶段动画在第 1 帧

        // 不走施放流程:不耗蓝、不进冷却、不记施放次数
        Assert.Equal((50, 0, 0), (s.Vitals.Mp, s.SkillCasts, s.CommandCasts));
        Assert.Equal(0.0, s.Skills.SlotOfSkill(AvatarFixture.GuardSkill).CooldownLeftMs);

        // 触发键(攻击键)没按着:挡下一击后最短保持清零,后滑 200 毫秒一结束姿态就结束(两侧各留 50 毫秒)
        arena.Run(AvatarFixture.GuardRecoilMs1 - 50);
        Assert.True(arena.Hero.Guarding);
        arena.Run(100);
        Assert.False(arena.Hero.Guarding);
        Assert.Equal((HeroMotion.Rest, 0), (arena.Hero.Motion, arena.Hero.SkillId));
    }

    [Fact]
    public void HoldingTheStanceKeyKeepsTheAutomaticStanceAfterTheRecoilJustLikeAHandMadeOne()
    {
        using var arena = new Arena(Strike(), monsterX: 260);
        arena.GiveBuff(1.0);
        arena.Session.SkillsHeld = SkillKeys.Attack;                                           // 防御姿态的扳机键(指令"下、下、攻击"的攻击键)按着
        Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
        arena.Run(1000);
        Assert.True(arena.Hero.Guarding);
        Assert.False(arena.Hero.GuardRecoiling);                                               // 后滑早放完了,回到第 0 帧的姿态
        Assert.Equal(0, arena.Hero.CurrentFrame);

        arena.Session.SkillsHeld = SkillKeys.None;                                             // 松开:最短保持早已清零,下一步就结束
        arena.Run(20);
        Assert.False(arena.Hero.Guarding);
    }

    // ------------------------------------------------------------------ 转向与后滑

    [Fact]
    public void AnAttackerInFrontIsFacedAlreadySoTheHeroKeepsItsFacingAndRecoilsBackwardByHalfSpeedTimesDuration()
    {
        using var arena = new Arena(Strike(), monsterX: 260);
        arena.GiveBuff(1.0);
        Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
        Assert.Equal(1, arena.Session.AutoGuardBlocks);
        Assert.True(arena.Hero.FacingLeft);                                                    // 怪物在左边,主角本来就朝左,不转

        // 后滑:朝向的反方向(往右),加速度 = −速度 × 1000 / 毫秒、恰好在 200 毫秒减到 0,距离 = 300 × 0.2 / 2 = 30 像素
        double x = arena.Hero.X;
        arena.Run(AvatarFixture.GuardRecoilMs1 + 100);
        Assert.Equal(AvatarFixture.GuardSpeed1 * AvatarFixture.GuardRecoilMs1 / 2000.0, arena.Hero.X - x, 0);
        Assert.True(arena.Hero.FacingLeft);
    }

    [Fact]
    public void AnAttackerBehindIsTurnedToFaceAndTheRecoilGoesTheOtherWayRoundWhileAnAttackerAlreadyInFrontNeverTurnsTheHero()
    {
        int unguarded = UnguardedDamage(Strike(), 340);
        using var arena = new Arena(Strike(), monsterX: 340);
        RoomCombatSession s = arena.Session;
        arena.GiveBuff(1.0);
        Assert.True(arena.Hero.FacingLeft);
        Assert.True(arena.RunUntil(() => s.HeroHits > 0));

        Assert.Equal((1, 1, 0), (s.AutoGuardBlocks, s.GuardBlocks, arena.Hero.HurtCount));
        Assert.False(arena.Hero.FacingLeft);                                                   // 怪物在右边:主角转过去朝右
        Assert.Equal(arena.Vitals.HpMax - unguarded * (100 - AvatarFixture.GuardPhysical1) / 100, arena.Vitals.Hp);

        double x = arena.Hero.X;                                                               // 后滑往朝向的反方向:朝右就往左
        arena.Run(AvatarFixture.GuardRecoilMs1 + 100);
        Assert.Equal(-AvatarFixture.GuardSpeed1 * AvatarFixture.GuardRecoilMs1 / 2000.0, arena.Hero.X - x, 0);
        Assert.False(arena.Hero.FacingLeft);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AShotComesFromTheLeftSoTheHeroEndsFacingLeftWhicheverWayItFacedBefore(bool heroFacingLeft)
    {
        // 投射物没有怪物当攻击方:按"朝着主角飞来"判。投掷手站在主角左边 (260, 100) 朝右扔——主角朝右时背对它,自动防御转过身来迎向它
        using var arena = new Arena(Strike(), monsterX: 260, heroFacingLeft: heroFacingLeft, thrower: true);
        arena.GiveBuff(1.0);
        Assert.Equal(heroFacingLeft, arena.Hero.FacingLeft);
        Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
        Assert.Equal((1, 1, 0), (arena.Session.AutoGuardBlocks, arena.Session.GuardBlocks, arena.Hero.HurtCount));
        Assert.True(arena.Hero.FacingLeft);
        Assert.True(arena.Hero.Guarding);
    }

    // ------------------------------------------------------------------ 致命的一击

    [Fact]
    public void AGuardedHitThatLeavesTheHeroAliveByOneHitPointIsBlockedAndOneThatKillsEvenAfterTheReductionIsNot()
    {
        int unguarded = UnguardedDamage(Strike());
        int reduced = unguarded * (100 - AvatarFixture.GuardPhysical1) / 100;
        Assert.True(reduced >= 2 && reduced < unguarded);

        // 折算后的伤害比血量少 1 点:活下来(血量剩 1),算挡下
        using var alive = new Arena(Strike(), hpMax: reduced + 1);
        alive.GiveBuff(1.0);
        Assert.True(alive.RunUntil(() => alive.Session.HeroHits > 0));
        Assert.Equal((1, 1, false, 1), (alive.Session.AutoGuardBlocks, alive.Session.GuardBlocks, alive.Session.HeroDead, alive.Vitals.Hp));
        Assert.Equal(0, alive.Hero.DownCount);

        // 折算后的伤害刚好等于血量:被打死,死亡走普通的受击死亡,没有挡下也没有进姿态
        using var dead = new Arena(Strike(), hpMax: reduced);
        dead.GiveBuff(1.0);
        Assert.True(dead.RunUntil(() => dead.Session.HeroHits > 0));
        Assert.Equal((0, 0, true), (dead.Session.AutoGuardBlocks, dead.Session.GuardBlocks, dead.Session.HeroDead));
        Assert.False(dead.Hero.Guarding);
        Assert.Equal(HeroMotion.Down, dead.Hero.Motion);
    }

    // ------------------------------------------------------------------ 放出来再挨打

    [Fact]
    public void ACastBuffWithACertainChanceFromTheLevelColumnGuardsTheNextHitWhenTheMonsterWalksUpAndLands()
    {
        // 缩放配成 0.004:第 1 级的 250 千分比 × 0.004 = 100%,放出来挂上以后必中;怪物从右边(背后)走过来,施法放完才到
        using var arena = new Arena(Strike(), monsterX: 560, recipes: Recipes(scale: 0.004));
        RoomCombatSession s = arena.Session;
        arena.CastBuff();
        Assert.True(arena.RunUntil(() => s.BuffsApplied == 1));
        Assert.Equal(1.0, s.Buffs.Total(BuffStat.AutoGuardChance), 6);

        Assert.True(arena.RunUntil(() => s.HeroHits > 0, 3000));
        Assert.Equal((1, 1, 0), (s.AutoGuardBlocks, s.GuardBlocks, arena.Hero.HurtCount));
        Assert.Equal(AvatarFixture.GuardSkill, arena.Hero.SkillId);
    }
}
