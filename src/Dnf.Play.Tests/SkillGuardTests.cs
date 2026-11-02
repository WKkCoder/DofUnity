using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 防御姿态类技能(M66,证据 E-164,客户端状态 0x13):指令"下、下、攻击"(攻击键当扳机,要一直按着才维持),主角原地摆姿势、动画定在第 0 帧、朝向锁定;
/// 最短保持时间过了而且键松开了姿态才结束。姿态里迎面挨到能挡的一击就挡下:伤害乘穿过的百分比(物理取 [level info] 第 2 列、魔法取第 3 列,都是 100 减那一列),
/// 不产生受击反应(没有硬直、倒地、受击语音、异常状态,受击方也不命中停顿),主角沿朝向的反方向后滑(速度第 0 列、持续毫秒第 1 列,动画定在第 1 帧),滑完回到姿态;
/// 背后挨的、不能挡的(没有受击反应的)一击照常受击,被打死的一击也照常死。
/// 数据全是合成的(见 <see cref="AvatarFixture.GuardSkill"/>):迷你职业站在 (300, 100) 朝左,会伤人的迷你怪物每一击 25 点伤害
/// (公式常量与 <see cref="HeroSuperArmorTests"/> 一样);迎面是怪物站在左边 (260, 100)、背后是站在右边 (340, 100)。
/// </summary>
public sealed class SkillGuardTests
{
    private const int HeroX = 300;
    private const int HeroY = 100;

    private static DamageConstants Constants()
    {
        return new DamageConstants { PhysicalDamageRate = 1000f, MagicalDamageRate = 1000f, PhysicalReduceRate = 100f, MagicalReduceRate = 100f };
    }

    /// <summary>怪物的出招信息:[damage bonus] 25;<paramref name="none"/> 是 [damage reaction] none,<paramref name="knockDown"/> 是倒地类,其余是普通受击;<paramref name="magical"/> 是魔法攻击。</summary>
    private static HeroStrikeInfo Strike(bool none = false, bool magical = false, bool knockDown = false, int hitValue = -1)
    {
        return new HeroStrikeInfo(AvatarFixture.StrikerDamageBonus, 0, knockDown, 0, 0, hitValue, false, -1f, 0, false, 0, magical, 300, false, false,
            reactionDamage: !none && !knockDown, hitWav: null, reactionNone: none);
    }

    private static SkillRecipes Recipes()
    {
        return new SkillRecipes(new SkillRecipe[0], null, new[]
        {
            new GuardRecipe(AvatarFixture.GuardSkill, motionIndex: AvatarFixture.GuardEtcIndex, minHoldStaticIndex: 0, recoilSpeedColumn: 0, recoilMsColumn: 1,
                physicalColumn: 2, magicalColumn: 3),
        });
    }

    /// <summary>914 学到 <paramref name="level"/> 级;<paramref name="quickSlot"/> 为真时放在第 1 格(键 A),否则只靠指令放。</summary>
    private static HeroSkills Load(GameData data, int level, bool quickSlot = false, SkillRecipes recipes = null)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.GuardSkill, level));
        var quick = quickSlot ? new List<KeyValuePair<int, int>> { new KeyValuePair<int, int>(0, AvatarFixture.GuardSkill) } : new List<KeyValuePair<int, int>>();
        book.Restore(levels, quick, 0, 0);
        return HeroSkills.Create(data, chr, 0, book, recipes ?? Recipes());
    }

    // ------------------------------------------------------------------ 配方与槽

    [Fact]
    public void TheDefaultTableHasOneGuardRecipeWithTheEngineConstants()
    {
        GuardRecipe real = Assert.Single(SkillRecipes.Default.AllGuards);
        Assert.Equal(1, SkillRecipes.Default.GuardCount);
        Assert.Same(real, SkillRecipes.Default.FindGuard(real.SkillId));
        Assert.Null(SkillRecipes.Default.FindGuard(real.SkillId + 1000));
        Assert.True(HeroSkills.IsImplemented(real.SkillId));

        // 动作取 [etc motion] 第 0 项、最短保持取 [static data] 第 0 个数;[level info] 列:后滑速度 0、后滑毫秒 1、物理 2、魔法 3
        Assert.Equal((0, 0, 0, 1, 2, 3), (real.MotionIndex, real.MinHoldStaticIndex, real.RecoilSpeedColumn, real.RecoilMsColumn, real.PhysicalColumn, real.MagicalColumn));

        // 它不是配方技能也不是增益类技能
        Assert.Null(SkillRecipes.Default.Find(real.SkillId));
        Assert.Null(SkillRecipes.Default.FindBuff(real.SkillId));
        Assert.Equal(0, new SkillRecipes(new SkillRecipe[0]).GuardCount);
    }

    [Fact]
    public void ASlotReadsTheStanceNumbersByLevelAndTheCommandIsTriggeredByTheAttackKey()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        HeroSkillSlot level1 = Assert.Single(Load(data, 1).CommandSlots, s => s.SkillId == AvatarFixture.GuardSkill);
        Assert.NotNull(level1.Guard);
        Assert.Equal("Down+Down+Attack", level1.Def.Command.ToString());
        Assert.Equal(CommandKey.Attack, level1.Def.Command.Trigger);
        Assert.True(HeroSkills.CommandCasts(level1.Def.Command));
        Assert.Equal(AvatarFixture.GuardMinHoldMs, level1.GuardMinHoldMs);
        Assert.Equal((AvatarFixture.GuardSpeed1, AvatarFixture.GuardRecoilMs1), (level1.GuardRecoilSpeed, level1.GuardRecoilMs));
        Assert.Equal(100 - AvatarFixture.GuardPhysical1, level1.GuardPassPercent(AttackKind.Physic));
        Assert.Equal(100 - AvatarFixture.GuardMagical1, level1.GuardPassPercent(AttackKind.Magic));
        Assert.Equal(100, level1.GuardPassPercent(AttackKind.IgnoreDefense));               // 无视防御的一击不折算
        Assert.Equal((AvatarFixture.GuardMp, AvatarFixture.GuardCool), (level1.ConsumeMp, level1.CooldownMs));
        Assert.False(level1.StageHasOwnStrike(0));                                         // 姿势里身体不打人
        Assert.True((level1.HoldKeys & SkillKeys.Attack) != SkillKeys.None);               // 扳机是攻击键:要按着攻击键才维持

        HeroSkillSlot level2 = Assert.Single(Load(data, 2).CommandSlots, s => s.SkillId == AvatarFixture.GuardSkill);
        Assert.Equal((AvatarFixture.GuardSpeed2, AvatarFixture.GuardRecoilMs2), (level2.GuardRecoilSpeed, level2.GuardRecoilMs));
        Assert.Equal(100 - AvatarFixture.GuardPhysical2, level2.GuardPassPercent(AttackKind.Physic));
        Assert.Equal(100 - AvatarFixture.GuardMagical2, level2.GuardPassPercent(AttackKind.Magic));

        // 快捷槽里的同一个技能:快捷键与攻击键都算"按着"
        HeroSkillSlot quick = Load(data, 1, quickSlot: true).SlotOf(SkillKeys.A);
        Assert.NotNull(quick);
        Assert.Equal(AvatarFixture.GuardSkill, quick.SkillId);
        Assert.Equal(SkillKeys.A | SkillKeys.Attack, quick.HoldKeys);

        // 不是防御姿态类技能的槽:默认值(不折算、没有后滑、不挡)
        var plain = new HeroSkillSlot(SkillKeys.None, AvatarFixture.ChainSkill, 1, data.GetSkill(0, AvatarFixture.ChainSkill));
        Assert.Null(plain.Guard);
        Assert.Equal((0, 0, 0, 100, 100), (plain.GuardMinHoldMs, plain.GuardRecoilSpeed, plain.GuardRecoilMs, plain.GuardPassPercent(AttackKind.Physic),
            plain.GuardPassPercent(AttackKind.Magic)));
    }

    [Fact]
    public void ThePassPercentNeverGoesBelowZero()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        // 借列号:第 0 列是后滑速度 300,比 100 大——配成"物理列 = 速度列"时穿过的百分比钳在 0(防御不会变成治疗)
        var weird = new SkillRecipes(new SkillRecipe[0], null, new[]
        {
            new GuardRecipe(AvatarFixture.GuardSkill, AvatarFixture.GuardEtcIndex, 0, 0, 1, physicalColumn: 0, magicalColumn: 3),
        });
        HeroSkillSlot slot = Assert.Single(Load(data, 1, recipes: weird).CommandSlots, s => s.SkillId == AvatarFixture.GuardSkill);
        Assert.Equal(0, slot.GuardPassPercent(AttackKind.Physic));
        Assert.Equal(100 - AvatarFixture.GuardMagical1, slot.GuardPassPercent(AttackKind.Magic));
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
        /// <param name="monsterX">怪物站的位置:260 在主角朝向的那一侧(迎面),340 在背后,很远的位置(700)是怪物够不着。</param>
        /// <param name="attackDelayMs">怪物两次出招之间的冷却;默认很长,只打一次。</param>
        /// <param name="skillLevel">914 学到的等级。</param>
        /// <param name="quickSlot">914 放在快捷栏第 1 格(键 A)。</param>
        /// <param name="thrower">怪物换成投掷手(合成的扔东西的迷你怪物):它的一击是投射物,打中主角时没有怪物当攻击方。</param>
        public Arena(HeroStrikeInfo strike, int hpMax = 1000, int attackDelayMs = 100000, int skillLevel = 1, int monsterX = 260, bool quickSlot = false, bool heroFacingLeft = true,
            bool thrower = false)
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

            HeroSkills skills = Load(new GameData(new PvfScriptSource(Assets.Pvf)), skillLevel, quickSlot);
            var plain = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
            Session = new RoomCombatSession(Assets, room, new[] { Monster }, plain, 1, default, default, skills, 0, -1f, new HitStunTable(10, 330, 1000), Vitals, Constants())
            {
                CommandsEnabled = true,
            };
        }

        public HeroSkillSlot Slot => Session.Skills.SlotOfSkill(AvatarFixture.GuardSkill);

        private void Step(int dirX, int dirY, bool attack, SkillKeys pressed = SkillKeys.None)
        {
            Session.Update(10, dirX, dirY, attack, false, pressed);
        }

        /// <summary>按指令"下、下、攻击"(下点一下松开、再按住下、按攻击键)放防御姿态,攻击键之后一直按着(<paramref name="hold"/>)或只点一下。</summary>
        public void TypeCommand(bool hold = true)
        {
            Session.SkillsHeld = SkillKeys.None;
            Step(0, 1, false);
            Step(0, 0, false);
            Step(0, 1, false);
            Session.SkillsHeld = hold ? SkillKeys.Attack : SkillKeys.None;
            Step(0, 1, true);
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

        public void Press(int dirX, int dirY, bool attack, SkillKeys pressed = SkillKeys.None)
        {
            Step(dirX, dirY, attack, pressed);
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    /// <summary>没有防御姿态时同一击的伤害(对照用):怪物打一次、读主角损失的血量。</summary>
    private static int UnguardedDamage(HeroStrikeInfo strike)
    {
        using var arena = new Arena(strike, monsterX: 340);
        Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
        return arena.Vitals.HpMax - arena.Vitals.Hp;
    }

    // ------------------------------------------------------------------ 起手与保持

    [Fact]
    public void TheCommandDownDownAttackStartsTheStanceOnTheFirstFrameWithoutAnAttackOrAnyDamage()
    {
        using var arena = new Arena(Strike(), monsterX: 700);
        RoomCombatSession s = arena.Session;
        arena.TypeCommand();

        Assert.True(arena.Hero.Guarding);
        Assert.Equal((HeroMotion.Skill, AvatarFixture.GuardSkill), (arena.Hero.Motion, arena.Hero.SkillId));
        Assert.Equal((1, 1), (s.SkillCasts, s.CommandCasts));
        Assert.Equal(0, arena.Hero.CurrentFrame);                                          // 姿态阶段动画定在第 0 帧(动画自己每帧停 10000 毫秒)
        Assert.True(arena.Hero.FrameHeld);
        Assert.Equal(50 - AvatarFixture.GuardMp, s.Vitals.Mp);                              // 耗蓝在放出来的那一刻扣
        Assert.True(arena.Slot.CooldownLeftMs > 0);
        Assert.Equal(0, s.Hits);                                                           // 身体不打人
        Assert.Equal(0, arena.Hero.AttackStage);                                           // 那一下攻击键不再是普攻

        // 之后每一步都停在姿态里的第 0 帧,哪怕动画自己的第 0 帧只有 10000 毫秒
        arena.Run(500);
        Assert.True(arena.Hero.Guarding);
        Assert.Equal(0, arena.Hero.CurrentFrame);
    }

    [Fact]
    public void AttackAloneOrADownTapWithoutTheSecondDownIsAnOrdinaryAttackNotAStance()
    {
        using var arena = new Arena(Strike(), monsterX: 700);
        arena.Press(0, 0, true);                                                           // 单按攻击键:普攻(第一段)
        Assert.Equal(HeroMotion.Attack, arena.Hero.Motion);
        Assert.False(arena.Hero.Guarding);
        Assert.Equal(0, arena.Session.SkillCasts);

        using var one = new Arena(Strike(), monsterX: 700);
        one.Session.SkillsHeld = SkillKeys.Attack;
        one.Press(0, 1, false);
        one.Press(0, 1, true);                                                             // 下 + 攻击:只有一个下
        Assert.False(one.Hero.Guarding);
        Assert.Equal(0, one.Session.SkillCasts);
        Assert.Equal(HeroMotion.Attack, one.Hero.Motion);
    }

    [Fact]
    public void TheStanceLastsAsLongAsTheAttackKeyIsHeldAndEndsOnceItIsReleasedAfterTheMinimumHold()
    {
        using var arena = new Arena(Strike(), monsterX: 700);
        arena.TypeCommand(hold: true);
        arena.Run(3000);                                                                   // 按着不放:远超最短保持,姿态还在
        Assert.True(arena.Hero.Guarding);

        arena.Session.SkillsHeld = SkillKeys.None;                                         // 松开:最短保持早过了,下一步就结束
        arena.Run(20);
        Assert.False(arena.Hero.Guarding);
        Assert.Equal(HeroMotion.Rest, arena.Hero.Motion);
        Assert.Equal(0, arena.Hero.SkillId);
    }

    [Fact]
    public void ReleasingTheKeyEarlyKeepsTheStanceUntilTheMinimumHoldIsOver()
    {
        using var arena = new Arena(Strike(), monsterX: 700);
        arena.TypeCommand(hold: false);                                                    // 攻击键只点一下:最短保持 200 毫秒之内姿态还在
        arena.Run(AvatarFixture.GuardMinHoldMs - 40);
        Assert.True(arena.Hero.Guarding);
        Assert.Equal(HeroMotion.Skill, arena.Hero.Motion);

        arena.Run(80);                                                                     // 过了最短保持:结束
        Assert.False(arena.Hero.Guarding);
        Assert.Equal(HeroMotion.Rest, arena.Hero.Motion);
    }

    [Fact]
    public void TheFacingAndPositionAreLockedInsideTheStance()
    {
        using var arena = new Arena(Strike(), monsterX: 700);
        arena.TypeCommand(hold: true);
        bool facingLeft = arena.Hero.FacingLeft;
        double x = arena.Hero.X;
        for (int i = 0; i < 30; i++)
        {
            arena.Press(1, 0, false);                                                      // 一直按着反方向的右键
        }
        Assert.True(arena.Hero.Guarding);
        Assert.Equal(facingLeft, arena.Hero.FacingLeft);
        Assert.Equal(x, arena.Hero.X);
        Assert.Equal(0, arena.Hero.AttackStage);

        for (int i = 0; i < 5; i++)
        {
            arena.Press(0, 0, true);                                                       // 再按攻击键也不出普攻
        }
        Assert.True(arena.Hero.Guarding);
        Assert.Equal(HeroMotion.Skill, arena.Hero.Motion);
    }

    [Fact]
    public void TheStanceCannotStartInTheAirOrWithoutMagicOrWhileOnCooldown()
    {
        // 腾空时不能起手(同别的技能),不扣蓝、不进冷却
        using var air = new Arena(Strike(), monsterX: 700);
        air.Session.Update(10, 0, 0, false, true, SkillKeys.None);                         // 起跳
        Assert.True(air.RunUntil(() => air.Hero.IsAirborne, 100));
        air.TypeCommand();
        Assert.False(air.Hero.Guarding);
        Assert.Equal(0, air.Session.SkillCasts);
        Assert.Equal(50, air.Session.Vitals.Mp);
        Assert.Equal(0.0, air.Slot.CooldownLeftMs);

        // 魔法不够
        using var dry = new Arena(Strike(), monsterX: 700);
        dry.Session.Vitals.TrySpendMp(48);
        dry.TypeCommand();
        Assert.False(dry.Hero.Guarding);
        Assert.Equal(SkillCastResult.NoMp, dry.Session.Skills.LastCastResult);

        // 冷却中:姿态结束之后立刻再打指令放不出来
        using var cool = new Arena(Strike(), monsterX: 700);
        cool.TypeCommand(hold: false);
        cool.Run(400);
        Assert.False(cool.Hero.Guarding);
        cool.TypeCommand(hold: false);
        Assert.False(cool.Hero.Guarding);
        Assert.Equal(SkillCastResult.Cooldown, cool.Session.Skills.LastCastResult);
    }

    [Fact]
    public void AQuickSlotKeyStartsTheStanceAndItsKeyOrTheAttackKeyHoldsIt()
    {
        using var arena = new Arena(Strike(), monsterX: 700, quickSlot: true);
        arena.Session.SkillsHeld = SkillKeys.A;
        arena.Press(0, 0, false, SkillKeys.A);                                             // 快捷键 A 放出来
        Assert.True(arena.Hero.Guarding);
        arena.Run(1000);                                                                   // A 键按着:一直在
        Assert.True(arena.Hero.Guarding);

        arena.Session.SkillsHeld = SkillKeys.Attack;                                       // 换成攻击键按着:也算(槽的"按着"键包括指令扳机)
        arena.Run(500);
        Assert.True(arena.Hero.Guarding);

        arena.Session.SkillsHeld = SkillKeys.None;
        arena.Run(20);
        Assert.False(arena.Hero.Guarding);
    }

    // ------------------------------------------------------------------ 挡下

    [Fact]
    public void AFrontalPhysicalHitIsReducedByThePhysicalColumnAndProducesNoReactionAndTheHeroSlidesBack()
    {
        int unguarded = UnguardedDamage(Strike());
        Assert.True(unguarded > 1);

        using var arena = new Arena(Strike(hitValue: 200), monsterX: 260);
        RoomCombatSession s = arena.Session;
        arena.TypeCommand();
        double x0 = arena.Hero.X;
        Assert.True(arena.RunUntil(() => s.HeroHits > 0));

        int expected = unguarded * (100 - AvatarFixture.GuardPhysical1) / 100;
        Assert.Equal((1, 1), (s.HeroHits, s.GuardBlocks));
        Assert.Equal(arena.Vitals.HpMax - expected, arena.Vitals.Hp);                       // 整数截断
        Assert.Equal(unguarded - expected, s.GuardDamagePrevented);
        Assert.Equal(expected, s.HeroDamageTaken);

        // 没有受击反应:没硬直、没倒地、不命中停顿(同一击的攻击信息带命中停顿,主角不被定住)、没有受击语音
        Assert.Equal((0, 0), (arena.Hero.HurtCount, arena.Hero.DownCount));
        Assert.False(arena.Hero.IsHurt);
        Assert.Equal(0.0, s.HeroPauseMs);
        Assert.Equal(HeroMotion.Skill, arena.Hero.Motion);
        Assert.True(arena.Hero.Guarding);
        Assert.Equal(1, arena.Hero.GuardBlocks);

        // 后滑阶段:动画换到第 1 帧,沿朝向的反方向(主角朝左,往右)滑
        Assert.True(arena.Hero.GuardRecoiling);
        Assert.Equal(1, arena.Hero.CurrentFrame);
        arena.Run(AvatarFixture.GuardRecoilMs1 + 50);
        Assert.False(arena.Hero.GuardRecoiling);
        Assert.Equal(0, arena.Hero.CurrentFrame);                                          // 滑完回到姿态
        Assert.True(arena.Hero.Guarding);
        Assert.True(arena.Hero.X > x0 + 1);
        Assert.True(arena.Hero.FacingLeft);                                                // 朝向没变
    }

    [Fact]
    public void ARecoilSlidesTheSpeedTimesDurationOverTwoAndStopsExactlyAtTheEnd()
    {
        // 加速度 = −速度 × 1000 / 毫秒(整数除法),所以从速度 300 在 200 毫秒里匀减速到 0:距离 = 300 × 0.2 / 2 = 30 像素
        using var arena = new Arena(Strike(), monsterX: 260);
        arena.TypeCommand();
        Assert.True(arena.RunUntil(() => arena.Session.GuardBlocks > 0));
        double x = arena.Hero.X;
        arena.Run(AvatarFixture.GuardRecoilMs1 + 100);
        Assert.Equal(AvatarFixture.GuardSpeed1 * AvatarFixture.GuardRecoilMs1 / 2000.0, arena.Hero.X - x, 0);
        double stopped = arena.Hero.X;
        arena.Run(200);
        Assert.Equal(stopped, arena.Hero.X, 6);                                           // 滑完不再动
    }

    [Fact]
    public void TheRecoilAccelerationIsAnIntegerDivisionAsInTheClient()
    {
        // 速度 100、300 毫秒:加速度 = −100 × 1000 / 300 = −333(整数除法,不是 −333.33…),滑完的距离 = 100 × 0.3 − 333 × 0.3² / 2 = 15.015 像素(而不是 15)
        using var arena = new Arena(Strike(), monsterX: 900);
        arena.TypeCommand();
        Assert.True(arena.Hero.Guarding);
        double x = arena.Hero.X;
        Assert.True(arena.Hero.BlockHit(100, 300));
        arena.Run(400);
        Assert.Equal(15.015, arena.Hero.X - x, 3);
        Assert.Equal(1, arena.Hero.GuardBlocks);
    }

    [Fact]
    public void ARecoilDurationThatIsNotPositiveFallsBackToTheClientDefaultOfAHundredMilliseconds()
    {
        foreach (int bad in new[] { 0, -50 })
        {
            using var arena = new Arena(Strike(), monsterX: 900);
            arena.TypeCommand();
            double x = arena.Hero.X;
            Assert.True(arena.Hero.BlockHit(300, bad));
            arena.Run(300);
            Assert.Equal(300 * TownHero.GuardDefaultRecoilMs / 2000.0, arena.Hero.X - x, 3);      // 300 × 0.1 / 2 = 15 像素
        }
    }

    [Fact]
    public void AfterABlockTheMinimumHoldIsGoneSoReleasingTheKeyEndsTheStanceWhenTheRecoilEnds()
    {
        // 最短保持 1000 毫秒的姿态:挡下一击后(进入块只在前一个状态不是自己时才设最短保持)最短保持清零,键松着的话后滑一结束姿态就结束
        using var arena = new Arena(Strike(), monsterX: 900);
        Assert.True(arena.Hero.BeginGuard(AvatarFixture.GuardSkill, AvatarFixture.GuardEtcIndex, 1000));
        arena.Session.SkillsHeld = SkillKeys.None;
        arena.Run(50);
        Assert.True(arena.Hero.Guarding);                                                  // 最短保持还没过,键松着也维持
        Assert.True(arena.Hero.BlockHit(300, 100));
        arena.Run(150);
        Assert.False(arena.Hero.Guarding);
        Assert.Equal(HeroMotion.Rest, arena.Hero.Motion);
    }

    [Fact]
    public void TheAttackerSideComparesIntegerPositionsAndAnEqualPairFollowsTheReversedAttackerFacing()
    {
        Assert.True(RoomCombatSession.AttackerIsOnRight(301.0, 300.0, false));
        Assert.False(RoomCombatSession.AttackerIsOnRight(299.0, 300.0, true));
        // 整数部分相等就算相等(小数的大小不管):朝左的攻击方当作在右边,朝右的当作在左边
        Assert.True(RoomCombatSession.AttackerIsOnRight(300.9, 300.1, true));
        Assert.False(RoomCombatSession.AttackerIsOnRight(300.9, 300.1, false));
        Assert.True(RoomCombatSession.AttackerIsOnRight(300.1, 300.9, true));
        Assert.False(RoomCombatSession.AttackerIsOnRight(300.1, 300.9, false));
        Assert.True(RoomCombatSession.AttackerIsOnRight(300.0, 300.0, true));
        Assert.False(RoomCombatSession.AttackerIsOnRight(300.0, 300.0, false));
    }

    [Fact]
    public void AShotIsBlockedWhenItFliesAtTheHeroFaceAndNotWhenItComesFromBehind()
    {
        // 投射物没有怪物当攻击方:按"朝着主角飞来"判——投掷手站在主角左边 (260, 100) 朝右扔,主角朝左(迎面)挡得下,朝右(背后)挡不下
        foreach (bool heroFacingLeft in new[] { true, false })
        {
            using var arena = new Arena(Strike(), monsterX: 260, heroFacingLeft: heroFacingLeft, thrower: true);
            arena.TypeCommand();
            Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
            Assert.Equal(heroFacingLeft ? 1 : 0, arena.Session.GuardBlocks);
            Assert.Equal(heroFacingLeft, arena.Hero.Guarding);                                // 背后挨的一击把姿态打断
        }
    }

    [Fact]
    public void AMagicalHitUsesTheMagicalColumnAndTheLevelPicksTheColumnValues()
    {
        int unguarded = UnguardedDamage(Strike(magical: true));
        Assert.True(unguarded > 1);

        using var arena = new Arena(Strike(magical: true), monsterX: 260, skillLevel: 2);
        arena.TypeCommand();
        Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
        int expected = unguarded * (100 - AvatarFixture.GuardMagical2) / 100;
        Assert.Equal(1, arena.Session.GuardBlocks);
        Assert.Equal(arena.Vitals.HpMax - expected, arena.Vitals.Hp);
    }

    [Fact]
    public void AKnockDownHitFromTheFrontIsBlockedToo()
    {
        using var arena = new Arena(Strike(knockDown: true), monsterX: 260);
        arena.TypeCommand();
        Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
        Assert.Equal(1, arena.Session.GuardBlocks);
        Assert.Equal((0, 0), (arena.Hero.HurtCount, arena.Hero.DownCount));
        Assert.False(arena.Session.HeroDown.Active);
        Assert.True(arena.Hero.Guarding);
    }

    [Fact]
    public void AHitFromBehindIsNotBlockedAndInterruptsTheStanceWithFullDamage()
    {
        int unguarded = UnguardedDamage(Strike());

        using var arena = new Arena(Strike(), monsterX: 340);                              // 主角朝左,怪物在右边(背后)
        RoomCombatSession s = arena.Session;
        arena.TypeCommand();
        Assert.True(arena.RunUntil(() => s.HeroHits > 0));

        Assert.Equal(0, s.GuardBlocks);
        Assert.Equal(arena.Vitals.HpMax - unguarded, arena.Vitals.Hp);                      // 全额伤害
        Assert.Equal(1, arena.Hero.HurtCount);                                             // 照常受击:姿态被打断
        Assert.False(arena.Hero.Guarding);
        Assert.Equal(HeroMotion.Hurt, arena.Hero.Motion);
    }

    [Fact]
    public void TheFrontIsWhicheverSideTheHeroFacesSoAHeroFacingRightBlocksTheMonsterOnTheRight()
    {
        using var arena = new Arena(Strike(), monsterX: 340, heroFacingLeft: false);
        arena.TypeCommand();
        Assert.True(arena.Hero.Guarding);
        Assert.False(arena.Hero.FacingLeft);
        Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
        Assert.Equal(1, arena.Session.GuardBlocks);

        using var behind = new Arena(Strike(), monsterX: 260, heroFacingLeft: false);
        behind.TypeCommand();
        Assert.True(behind.RunUntil(() => behind.Session.HeroHits > 0));
        Assert.Equal(0, behind.Session.GuardBlocks);
        Assert.Equal(1, behind.Hero.HurtCount);
    }

    [Fact]
    public void AHitWithNoDamageReactionCannotBeBlockedSoItIsNotReducedEither()
    {
        int unguarded = UnguardedDamage(Strike(none: true));

        using var arena = new Arena(Strike(none: true), monsterX: 260);
        arena.TypeCommand();
        Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
        Assert.Equal(0, arena.Session.GuardBlocks);
        Assert.Equal(arena.Vitals.HpMax - unguarded, arena.Vitals.Hp);
        Assert.True(arena.Hero.Guarding);                                                  // 本来就没有受击反应,姿态不被打断
        Assert.False(arena.Hero.GuardRecoiling);                                           // 但也没有挡下的后滑
    }

    [Fact]
    public void ABlockedHitThatWouldStillKillTheHeroIsAnOrdinaryDeath()
    {
        using var arena = new Arena(Strike(), hpMax: 10, monsterX: 260);                   // 25 点伤害打到 50% 还有 12,血只有 10
        arena.TypeCommand();
        Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
        Assert.Equal(0, arena.Session.GuardBlocks);
        Assert.True(arena.Session.HeroDead);
        Assert.False(arena.Hero.Guarding);
    }

    [Fact]
    public void EveryFrontalHitRestartsTheRecoilAndTheStanceSurvivesARepeatedBeating()
    {
        using var arena = new Arena(Strike(), attackDelayMs: 300, monsterX: 260);
        RoomCombatSession s = arena.Session;
        arena.TypeCommand();
        int unguarded = UnguardedDamage(Strike());
        int each = unguarded * (100 - AvatarFixture.GuardPhysical1) / 100;
        arena.Run(4000);

        Assert.True(s.GuardBlocks >= 3);
        Assert.Equal(s.GuardBlocks, s.HeroHits);                                           // 挨的每一击都挡下了
        Assert.Equal(arena.Vitals.HpMax - each * s.GuardBlocks, arena.Vitals.Hp);
        Assert.Equal(s.GuardBlocks, arena.Hero.GuardBlocks);
        Assert.Equal((0, 0), (arena.Hero.HurtCount, arena.Hero.DownCount));
        Assert.True(arena.Hero.Guarding);
    }

    [Fact]
    public void ReleasingTheKeyOutsideTheRecoilEndsTheStanceAndTheNextHitIsAnOrdinaryHit()
    {
        using var arena = new Arena(Strike(), attackDelayMs: 100000, monsterX: 560);       // 怪物远在 560,走过来要一阵子(姿态早就结束了)
        RoomCombatSession s = arena.Session;
        arena.TypeCommand(hold: false);
        arena.Run(AvatarFixture.GuardMinHoldMs + 100);
        Assert.False(arena.Hero.Guarding);

        // 站着挨打:没有姿态,照常受击
        Assert.True(arena.RunUntil(() => s.HeroHits > 0, 1500));
        Assert.Equal((0, 1), (s.GuardBlocks, arena.Hero.HurtCount));
    }

    // ------------------------------------------------------------------ 时间表

    [Fact]
    public void TheScriptActionHoldxHoldsTheAttackKeyAndTurnsTheCommandLayerOn()
    {
        CombatScript script = CombatScript.Parse("1:down,1.1:none,1.2:down,1.3:down+attack+holdx,2:down+holdx,3:none");
        Assert.True(script.UsesCommands);

        // 第 1.3 秒到 3 秒之间攻击键一直按着;"刚按下"的攻击键只在 1.3 秒给一次
        script.Poll(1300.0, out _, out _, out _, out _);
        Assert.Equal(SkillKeys.Attack, script.HeldSkills);
        script.Poll(2500.0, out _, out _, out _, out _);
        Assert.Equal(SkillKeys.Attack, script.HeldSkills);
        script.Poll(3100.0, out _, out _, out _, out _);
        Assert.Equal(SkillKeys.None, script.HeldSkills);
    }
}
