using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;
using static Dnf.Play.Tests.RoomCombatTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 增益类技能(M48,证据 E-141):增益容器 <see cref="HeroBuffs"/>(同技能替换并重新计时、属性加和、到点失效、清空、换房间抄过去),
/// 槽里的持续时间与数值取自 .skl 的哪一处、<see cref="RoomCombatSession"/> 里的整条链(放出来 → 播施法动作 → 施法正常结束才挂 → 属性消费者 → 到点摘掉;
/// 被打断没有增益、倒下清空、再放换新的)、指令放增益与随人走房间。数据全是合成的(见 <see cref="AvatarFixture.JumpBuffSkill"/> 一带),与原版数据无关。
/// </summary>
public sealed class HeroBuffTests
{
    // ------------------------------------------------------------------ 增益容器

    [Fact]
    public void ApplyingABuffRecordsItAndANonPositiveDurationNeverSticks()
    {
        var buffs = new HeroBuffs();
        Assert.Null(buffs.Apply(1, BuffStat.JumpPower, 30, 0));
        Assert.Null(buffs.Apply(1, BuffStat.JumpPower, 30, -5));
        Assert.Equal((0, 0), (buffs.Count, buffs.Applied));

        HeroBuff b = buffs.Apply(7, BuffStat.MagicalAttack, 40, 5000);
        Assert.NotNull(b);
        Assert.Equal((7, BuffStat.MagicalAttack, 40.0, 5000.0, 5000.0), (b.SkillId, b.Stat, b.Amount, b.DurationMs, b.RemainingMs));
        Assert.Equal(1.0, b.Fraction);
        Assert.True(buffs.Has(7));
        Assert.Same(b, buffs.Find(7));
        Assert.False(buffs.Has(8));
        Assert.Null(buffs.Find(8));
        Assert.Equal((1, 1), (buffs.Count, buffs.Applied));
    }

    [Fact]
    public void RecastingTheSameSkillReplacesTheOldBuffAndRestartsTheClock()
    {
        var buffs = new HeroBuffs();
        HeroBuff first = buffs.Apply(7, BuffStat.JumpPower, 30, 3000);
        buffs.Advance(2000);
        Assert.Equal(1000.0, first.RemainingMs);

        HeroBuff second = buffs.Apply(7, BuffStat.JumpPower, 30, 3000);       // 同一个技能再放:换掉旧的,时间重新数
        Assert.NotSame(first, second);
        Assert.Equal(1, buffs.Count);
        Assert.Same(second, buffs.Find(7));
        Assert.Equal(3000.0, second.RemainingMs);
        Assert.Equal(2, buffs.Applied);
        Assert.Equal(30.0, buffs.Total(BuffStat.JumpPower));                   // 不叠加

        // 再放时数值与时长可以变(升了级):以新的为准
        buffs.Apply(7, BuffStat.JumpPower, 50, 4000);
        Assert.Equal((1, 50.0, 4000.0), (buffs.Count, buffs.Total(BuffStat.JumpPower), buffs.Find(7).RemainingMs));
    }

    [Fact]
    public void TotalsAddUpPerStatAndDifferentSkillsStack()
    {
        var buffs = new HeroBuffs();
        Assert.Equal(0.0, buffs.Total(BuffStat.JumpPower));
        buffs.Apply(1, BuffStat.JumpPower, 30, 3000);
        buffs.Apply(2, BuffStat.JumpPower, 12, 3000);                          // 两个技能同一个属性:加和
        buffs.Apply(3, BuffStat.MagicalAttack, 40, 3000);
        Assert.Equal(42.0, buffs.Total(BuffStat.JumpPower));
        Assert.Equal(40.0, buffs.Total(BuffStat.MagicalAttack));
        Assert.Equal(new[] { 1, 2, 3 }, buffs.Active.Select(b => b.SkillId).ToArray());   // 按挂上的先后
    }

    [Fact]
    public void AdvanceCountsDownAndRemovesExpiredBuffsInOrder()
    {
        var buffs = new HeroBuffs();
        buffs.Apply(1, BuffStat.JumpPower, 30, 1000);
        buffs.Apply(2, BuffStat.MagicalAttack, 40, 2500);
        buffs.Advance(0);                                                      // 零与负的步长什么也不做
        buffs.Advance(-10);
        Assert.Equal((1000.0, 2500.0), (buffs.Find(1).RemainingMs, buffs.Find(2).RemainingMs));

        buffs.Advance(400);
        Assert.Equal(0.6, buffs.Find(1).Fraction, 6);
        Assert.Equal(2100.0, buffs.Find(2).RemainingMs);
        buffs.Advance(599);
        Assert.True(buffs.Has(1));                                             // 还剩 1 毫秒
        buffs.Advance(1);                                                      // 正好走到 0:失效
        Assert.False(buffs.Has(1));
        Assert.Equal((1, 1), (buffs.Count, buffs.Expired));
        Assert.Equal(0.0, buffs.Total(BuffStat.JumpPower));
        buffs.Advance(5000);
        Assert.Equal((0, 2), (buffs.Count, buffs.Expired));
        buffs.Advance(10);                                                     // 空容器照常走
        Assert.Equal(2, buffs.Expired);
    }

    [Fact]
    public void ClearDropsEverythingWithoutCountingItAsExpiry()
    {
        var buffs = new HeroBuffs();
        buffs.Apply(1, BuffStat.JumpPower, 30, 1000);
        buffs.Apply(2, BuffStat.MagicalAttack, 40, 2000);
        buffs.Clear();
        Assert.Equal((0, 2, 0), (buffs.Count, buffs.Applied, buffs.Expired));
        Assert.Equal(0.0, buffs.Total(BuffStat.MagicalAttack));
    }

    [Fact]
    public void CopyStateCarriesTheRemainingTimeIntoAnIndependentContainer()
    {
        var from = new HeroBuffs();
        from.Apply(1, BuffStat.JumpPower, 30, 3000);
        from.Apply(2, BuffStat.MagicalAttack, 40, 5000);
        from.Advance(1200);

        var to = new HeroBuffs();
        to.Apply(9, BuffStat.JumpPower, 1, 100);                               // 目标里原有的先清掉
        to.CopyStateFrom(from);
        Assert.Equal(new[] { 1, 2 }, to.Active.Select(b => b.SkillId).ToArray());
        Assert.Equal((1800.0, 3800.0), (to.Find(1).RemainingMs, to.Find(2).RemainingMs));
        Assert.Equal((3000.0, 30.0, BuffStat.JumpPower), (to.Find(1).DurationMs, to.Find(1).Amount, to.Find(1).Stat));
        Assert.Equal(1, to.Applied);                                           // 计数不抄(只有它自己挂过的那一次)

        to.Advance(1800);                                                      // 抄过去的两份各走各的
        Assert.False(to.Has(1));
        Assert.True(from.Has(1));
        Assert.Equal(1800.0, from.Find(1).RemainingMs);

        to.CopyStateFrom(null);                                                // 没有来源、来源是自己:什么也不变
        to.CopyStateFrom(to);
        Assert.Equal(new[] { 2 }, to.Active.Select(b => b.SkillId).ToArray());
    }

    // ------------------------------------------------------------------ 配方表与槽

    private static SkillRecipes Recipes()
    {
        return new SkillRecipes(new SkillRecipe[0], new[]
        {
            new BuffRecipe(AvatarFixture.JumpBuffSkill, BuffStat.JumpPower, durationColumn: 0, amountStaticIndex: 0, amountColumn: -1),
            new BuffRecipe(AvatarFixture.MagicBuffSkill, BuffStat.MagicalAttack, durationColumn: 0, amountStaticIndex: -1, amountColumn: 1),
        });
    }

    /// <summary>907 放在快捷栏第 1 格(键 A)、学到 <paramref name="jumpLevel"/> 级;908 学到 <paramref name="magicLevel"/> 级,不在快捷栏(只靠指令)。</summary>
    private static HeroSkills Load(GameData data, int jumpLevel = 1, int magicLevel = 2, SkillRecipes recipes = null)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.JumpBuffSkill, jumpLevel));
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.MagicBuffSkill, magicLevel));
        var quick = new List<KeyValuePair<int, int>> { new KeyValuePair<int, int>(0, AvatarFixture.JumpBuffSkill) };
        book.Restore(levels, quick, 0, 0);
        return HeroSkills.Create(data, chr, 0, book, recipes ?? Recipes());
    }

    [Fact]
    public void TheRecipeTableKeepsBuffsApartFromMultiStageRecipes()
    {
        SkillRecipes recipes = Recipes();
        Assert.Equal((0, 2), (recipes.Count, recipes.BuffCount));
        Assert.Equal(BuffStat.JumpPower, recipes.FindBuff(AvatarFixture.JumpBuffSkill).Stat);
        Assert.Null(recipes.FindBuff(AvatarFixture.ChainSkill));
        Assert.Null(recipes.Find(AvatarFixture.JumpBuffSkill));
        Assert.Equal(2, recipes.AllBuffs.Count());

        // 默认表里有三个增益类技能,改的属性各是一种(第三个是 M49 的概率触发类,E-142)
        Assert.Equal(3, SkillRecipes.Default.BuffCount);
        Assert.Equal(new[] { BuffStat.MagicalAttack, BuffStat.JumpPower, BuffStat.SuperArmorChance }.OrderBy(s => (int)s),
            SkillRecipes.Default.AllBuffs.Select(b => b.Stat).OrderBy(s => (int)s));
    }

    [Fact]
    public void BuffSlotsReadTheirDurationAndAmountFromTheSkillDataByLevel()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        HeroSkills level1 = Load(data, jumpLevel: 1, magicLevel: 1);
        HeroSkillSlot jump = level1.SlotOf(SkillKeys.A);
        Assert.NotNull(jump.Buff);
        Assert.Equal((AvatarFixture.JumpBuffSkill, 12, 2000), (jump.SkillId, jump.ConsumeMp, jump.CooldownMs));
        Assert.Equal((AvatarFixture.JumpBuffMs1, AvatarFixture.JumpBuffAmount), (jump.BuffDurationMs, jump.BuffAmount));   // 数值在 [static data],时长在 [level info]
        Assert.False(jump.StageHasOwnStrike(0));                                                                              // 施法动作里主角不打人

        HeroSkills level2 = Load(data, jumpLevel: 2, magicLevel: 2);
        Assert.Equal((AvatarFixture.JumpBuffMs2, AvatarFixture.JumpBuffAmount), (level2.SlotOf(SkillKeys.A).BuffDurationMs, level2.SlotOf(SkillKeys.A).BuffAmount));

        // 908 不在快捷栏:只靠指令放;数值与时长都是 [level info] 的列
        HeroSkillSlot magic1 = Assert.Single(level1.CommandSlots, s => s.SkillId == AvatarFixture.MagicBuffSkill);
        Assert.Equal((AvatarFixture.MagicBuffMs1, AvatarFixture.MagicBuffAmount1), (magic1.BuffDurationMs, magic1.BuffAmount));
        HeroSkillSlot magic2 = Assert.Single(level2.CommandSlots, s => s.SkillId == AvatarFixture.MagicBuffSkill);
        Assert.Equal((AvatarFixture.MagicBuffMs2, AvatarFixture.MagicBuffAmount2), (magic2.BuffDurationMs, magic2.BuffAmount));
        Assert.Equal("Down+Buff", magic2.Def.Command.ToString());
        Assert.Equal("Buff", jump.Def.Command.ToString());

        // 没有增益配方的表:同样的技能"没实现",快捷槽放不出来,也不进指令槽
        HeroSkills none = Load(data, recipes: new SkillRecipes(new SkillRecipe[0]));
        Assert.Null(none.SlotOf(SkillKeys.A).Buff);
        Assert.Equal(0.0, none.SlotOf(SkillKeys.A).BuffDurationMs);
        Assert.Empty(none.CommandSlots);
    }

    // ------------------------------------------------------------------ 房间会话

    private sealed class BuffArena : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public RoomCombatSession Session { get; }
        public HeroVitals Vitals { get; }
        public TownHero Hero => Session.Hero;
        public HeroSkills Skills { get; }

        public BuffArena(int mp = 50, bool commands = true, string chr = AvatarFixture.ActionChrPath, int jumpLevel = 1, int magicLevel = 2)
        {
            Assets = DnfAssets.Open(_fx.Root);
            Vitals = new HeroVitals(1, 1000, mp, 0, 0, 0);
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
            TownHero hero = TownHero.Create(Assets, chr, outfit, new List<WorldRect>(), 300, 100, false, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));
            Skills = Load(new GameData(new PvfScriptSource(Assets.Pvf)), jumpLevel, magicLevel);
            var plain = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
            Session = new RoomCombatSession(Assets, room, new List<MonsterActor>(), plain, 1, default, default, Skills, 0, -1f, null, Vitals, null)
            {
                CommandsEnabled = commands,
            };
        }

        /// <summary>每 10 毫秒一步推进 <paramref name="ms"/> 毫秒;第一步按下 <paramref name="keys"/>。</summary>
        public void Run(int ms, int dirX = 0, int dirY = 0, SkillKeys keys = SkillKeys.None, bool jump = false)
        {
            for (int t = 0; t < ms; t += 10)
            {
                Session.Update(10, dirX, dirY, false, t == 0 && jump, t == 0 ? keys : SkillKeys.None);
            }
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    [Fact]
    public void ABuffIsAppliedWhenTheCastMotionEndsNotWhenItStarts()
    {
        using var arena = new BuffArena();
        RoomCombatSession s = arena.Session;
        arena.Run(10, keys: SkillKeys.A);
        Assert.Equal((1, 1, 0, 0), (s.SkillCasts, s.BuffCasts, s.BuffsApplied, s.Buffs.Count));
        Assert.Equal((HeroMotion.Skill, AvatarFixture.JumpBuffSkill), (arena.Hero.Motion, arena.Hero.SkillId));
        Assert.Equal(AvatarFixture.BuffAni, arena.Hero.CurrentAnimationPath);
        Assert.Equal(50 - 12, arena.Vitals.Mp);                                // 魔法值在放出来时就扣了
        Assert.Equal(2000, s.Skills.SlotOfSkill(AvatarFixture.JumpBuffSkill).CooldownMs);
        Assert.False(s.Skills.SlotOfSkill(AvatarFixture.JumpBuffSkill).Ready); // 冷却也是放出来时开始

        arena.Run(200);                                                        // 施法动作 300 毫秒,播到一半:还没有增益
        Assert.Equal(HeroMotion.Skill, arena.Hero.Motion);
        Assert.Equal((0, 0.0, 0.0), (s.Buffs.Count, s.Buffs.Total(BuffStat.JumpPower), arena.Hero.JumpPowerBonus));

        arena.Run(200);                                                        // 播完:这一步挂上
        Assert.NotEqual(HeroMotion.Skill, arena.Hero.Motion);
        HeroBuff b = Assert.Single(s.Buffs.Active);
        Assert.Equal((AvatarFixture.JumpBuffSkill, BuffStat.JumpPower, (double)AvatarFixture.JumpBuffAmount, (double)AvatarFixture.JumpBuffMs1),
            (b.SkillId, b.Stat, b.Amount, b.DurationMs));
        Assert.Equal((1, 1, 0), (s.BuffsApplied, s.BuffCasts, s.BuffsInterrupted));
        Assert.True(b.RemainingMs > AvatarFixture.JumpBuffMs1 - 300);          // 挂上之后才开始计时(挂上与现在隔不了几步)

        arena.Run(10);
        Assert.Equal((double)AvatarFixture.JumpBuffAmount, arena.Hero.JumpPowerBonus);    // 跳跃力增益每步摆进主角
        Assert.Equal(0, s.Hits);                                               // 增益施法不打人
    }

    [Fact]
    public void TheBuffExpiresAfterItsDurationAndTheJumpPowerGoesBack()
    {
        using var arena = new BuffArena();
        RoomCombatSession s = arena.Session;
        arena.Run(10, keys: SkillKeys.A);
        arena.Run(400);
        Assert.True(s.Buffs.Has(AvatarFixture.JumpBuffSkill));

        arena.Run(AvatarFixture.JumpBuffMs1 - 600);                            // 还没到点
        Assert.True(s.Buffs.Has(AvatarFixture.JumpBuffSkill));
        Assert.Equal((double)AvatarFixture.JumpBuffAmount, arena.Hero.JumpPowerBonus);

        arena.Run(700);                                                        // 过了时长
        Assert.False(s.Buffs.Has(AvatarFixture.JumpBuffSkill));
        Assert.Equal(1, s.Buffs.Expired);
        arena.Run(10);
        Assert.Equal(0.0, arena.Hero.JumpPowerBonus);
    }

    [Fact]
    public void AJumpWithTheBuffGoesHigherThanOneWithout()
    {
        double Peak(BuffArena arena)
        {
            double peak = 0.0;
            for (int t = 0; t < 2000; t += 10)
            {
                arena.Session.Update(10, 0, 0, false, t == 0, SkillKeys.None);
                peak = Math.Max(peak, arena.Hero.Z);
                if (t > 100 && !arena.Hero.IsAirborne)
                {
                    break;
                }
            }
            return peak;
        }

        using var plain = new BuffArena();
        double without = Peak(plain);
        using var boosted = new BuffArena();
        boosted.Run(10, keys: SkillKeys.A);
        boosted.Run(400);
        Assert.True(boosted.Session.Buffs.Has(AvatarFixture.JumpBuffSkill));
        double with = Peak(boosted);
        Assert.True(without > 0);
        Assert.True(with > without);
    }

    [Fact]
    public void TheCommandKeyCastsABuffByItsCommandAndThePowerComesFromTheLevelInfoColumns()
    {
        using var arena = new BuffArena();
        RoomCombatSession s = arena.Session;
        arena.Run(10, dirY: 1);                                                // 指令"下,增益":先按下,再按增益键
        arena.Run(10, keys: SkillKeys.CommandBuff);
        Assert.Equal((1, 1, 1), (s.SkillCasts, s.CommandCasts, s.BuffCasts));
        Assert.Equal(AvatarFixture.MagicBuffSkill, arena.Hero.SkillId);
        Assert.Equal(50 - 8, arena.Vitals.Mp);

        arena.Run(400);
        HeroBuff b = Assert.Single(s.Buffs.Active);
        Assert.Equal((AvatarFixture.MagicBuffSkill, BuffStat.MagicalAttack, (double)AvatarFixture.MagicBuffAmount2, (double)AvatarFixture.MagicBuffMs2),
            (b.SkillId, b.Stat, b.Amount, b.DurationMs));                       // 学到 2 级:取 [level info] 第 2 行

        // 指令开关关着:增益键没有用
        using var off = new BuffArena(commands: false);
        off.Run(10, dirY: 1);
        off.Run(10, keys: SkillKeys.CommandBuff);
        Assert.Equal((0, 0), (off.Session.SkillCasts, off.Session.BuffCasts));
    }

    [Fact]
    public void ACastInterruptedByAHitLeavesNoBuffButStillCostsManaAndCooldown()
    {
        using var arena = new BuffArena();
        RoomCombatSession s = arena.Session;
        arena.Run(10, keys: SkillKeys.A);
        arena.Run(100);
        Assert.Equal(HeroMotion.Skill, arena.Hero.Motion);
        Assert.True(arena.Hero.BeginHurt(300, 0, 0, true));                    // 施法中被打:施法动作停下
        arena.Run(20);
        Assert.Equal((1, 0, 1, 0), (s.BuffCasts, s.BuffsApplied, s.BuffsInterrupted, s.Buffs.Count));
        arena.Run(1000);
        Assert.Equal(0, s.Buffs.Count);                                        // 后面也不会补挂
        Assert.Equal(50 - 12, arena.Vitals.Mp);
        Assert.False(s.Skills.SlotOfSkill(AvatarFixture.JumpBuffSkill).Ready);
    }

    [Fact]
    public void RecastingRefreshesTheBuffWithoutStackingAndEachCastCostsMana()
    {
        using var arena = new BuffArena();
        RoomCombatSession s = arena.Session;
        arena.Run(10, keys: SkillKeys.A);
        arena.Run(400);
        double firstRemaining = s.Buffs.Find(AvatarFixture.JumpBuffSkill).RemainingMs;

        arena.Run(1700);                                                       // 冷却 2000 毫秒走完之后再放
        Assert.True(s.Skills.SlotOfSkill(AvatarFixture.JumpBuffSkill).Ready);
        double beforeRecast = s.Buffs.Find(AvatarFixture.JumpBuffSkill).RemainingMs;
        Assert.True(beforeRecast < firstRemaining - 1500);
        arena.Run(10, keys: SkillKeys.A);
        Assert.Equal(2, s.BuffCasts);
        Assert.Equal(50 - 24, arena.Vitals.Mp);
        // 重放的施法期间旧增益照常倒数;施法结束再挂,换成新的、满时长
        arena.Run(400);
        Assert.Equal(1, s.Buffs.Count);
        Assert.Equal((2, 2), (s.BuffsApplied, s.Buffs.Applied));
        Assert.True(s.Buffs.Find(AvatarFixture.JumpBuffSkill).RemainingMs > beforeRecast);
        Assert.Equal((double)AvatarFixture.JumpBuffAmount, s.Buffs.Total(BuffStat.JumpPower));
    }

    [Fact]
    public void DyingClearsTheBuffsAndTheCastInProgress()
    {
        using var arena = new BuffArena();
        RoomCombatSession s = arena.Session;
        arena.Run(10, keys: SkillKeys.A);
        arena.Run(400);
        Assert.Equal(1, s.Buffs.Count);

        // 又在施另一个增益的途中倒下:已有的摘掉,正在施的也不会再挂
        arena.Run(10, dirY: 1);
        arena.Run(10, keys: SkillKeys.CommandBuff);
        Assert.Equal(AvatarFixture.MagicBuffSkill, arena.Hero.SkillId);
        arena.Vitals.Damage(arena.Vitals.Hp);
        Assert.True(s.HeroDead);
        arena.Run(600);
        Assert.Equal((0, 2, 1), (s.Buffs.Count, s.BuffCasts, s.BuffsInterrupted));
        Assert.Equal(1, s.BuffsApplied);
    }

    [Fact]
    public void WithoutABuffMotionTheCastIsRefusedAndCostsNothing()
    {
        using var arena = new BuffArena(chr: AvatarFixture.ChrPath);           // 这个角色脚本没有 [buff motion]
        arena.Run(10, keys: SkillKeys.A);
        Assert.Equal((0, 0, 50), (arena.Session.SkillCasts, arena.Session.BuffCasts, arena.Vitals.Mp));
        Assert.NotEqual(HeroMotion.Skill, arena.Hero.Motion);
        Assert.True(arena.Session.Skills.SlotOfSkill(AvatarFixture.JumpBuffSkill).Ready);
        Assert.Equal(SkillCastResult.Busy, arena.Skills.LastCastResult);
    }

    [Fact]
    public void ABuffCastIsRefusedWhenTheManaIsShortOrTheSkillIsCoolingDown()
    {
        using var poor = new BuffArena(mp: 11);                                // 耗蓝 12
        poor.Run(10, keys: SkillKeys.A);
        Assert.Equal((0, 11), (poor.Session.BuffCasts, poor.Vitals.Mp));
        Assert.Equal(SkillCastResult.NoMp, poor.Skills.LastCastResult);

        using var arena = new BuffArena();
        arena.Run(10, keys: SkillKeys.A);
        arena.Run(400);
        arena.Run(10, keys: SkillKeys.A);                                      // 冷却 2000 毫秒还没走完
        Assert.Equal((1, SkillCastResult.Cooldown), (arena.Session.BuffCasts, arena.Skills.LastCastResult));
    }

    // ------------------------------------------------------------------ 魔法攻击力增益与算伤害

    private static DamageConstants Constants()
    {
        return new DamageConstants
        {
            PhysicalDamageRate = 100f,
            MagicalDamageRate = 100f,
            PhysicalReduceRate = 20f,
            MagicalReduceRate = 20f,
            CommonElementBase = 100f,
        };
    }

    private static HeroStrikeInfo MagicStrike()
    {
        return new HeroStrikeInfo(0, 0, false, source: new HitInput(0f, true, AttackKind.Magic, 0f, 0, -1000f));
    }

    [Fact]
    public void TheMagicalAttackBuffRaisesTheFormulaDamageOfAMagicHitAndLeavesThePanelAlone()
    {
        // 迷你主角:物攻 50、魔攻 100,装备魔攻 20;一次不带加成的魔法攻击 = (0 + 20) × (魔攻 / 100 + 1)
        var panel = new HeroPanel(10, 50, 100, new EquipmentStats(30, 20));
        var strikes = new[] { MagicStrike(), MagicStrike(), MagicStrike() };
        using var arena = new Arena(new[] { (340, 100) }, strikes, hp: 1000, heroPanel: panel, damageConstants: Constants(), defense: new DefenderPanel { Level = 60 });
        RoomCombatSession s = arena.Session;
        MonsterActor m = s.Monsters[0];
        Assert.Equal(40, s.RollFormulaDamage(MagicStrike(), m, 1u, out _));

        s.Buffs.Apply(AvatarFixture.MagicBuffSkill, BuffStat.MagicalAttack, 40, 5000);   // 魔攻 140:20 × (1.4 + 1) = 48
        for (uint seed = 1; seed <= 3; seed++)
        {
            Assert.Equal(48, s.RollFormulaDamage(MagicStrike(), m, seed, out _));        // 每次都是 48:加成只在这一击里临时加,不累积
        }
        // 物理一击不吃魔攻增益
        var physical = new HeroStrikeInfo(0, 0, false, source: new HitInput(0f, true, AttackKind.Physic, 0f, 0, -1000f));
        Assert.Equal(45, s.RollFormulaDamage(physical, m, 1u, out _));

        s.Buffs.Clear();                                                      // 摘掉增益:回到面板原值
        Assert.Equal(40, s.RollFormulaDamage(MagicStrike(), m, 1u, out _));
        Assert.Equal(1000, m.Hp);                                             // 只算,不改怪物
    }

    // ------------------------------------------------------------------ 换房间

    /// <summary>起点格有一只离主角 40 像素远的弱怪物,其余格没有怪物。</summary>
    private static (int X, int Y)[] OneWeakAtStart(int cx, int cy) => cx == 0 && cy == 0 ? new[] { (340, 100) } : DungeonSessionTests.None();

    private static void RunMs(DungeonSession s, int ms, int dirX = 0, int dirY = 0, bool attackFirst = false)
    {
        for (int t = 0; t < ms; t += 10)
        {
            s.Update(10, dirX, dirY, attackFirst && t == 0);
        }
    }

    private static void RunToCell(DungeonSession s, int cellX, int cellY, int dirX, int dirY, int maxMs = 20000)
    {
        for (int t = 0; t < maxMs && !(s.CellX == cellX && s.CellY == cellY); t += 10)
        {
            s.Update(10, dirX, dirY, false);
        }
        Assert.Equal((cellX, cellY), (s.CellX, s.CellY));
    }

    [Fact]
    public void BuffsTravelWithTheHeroFromRoomToRoomAndBack()
    {
        using var d = new DungeonSessionTests.Dungeon(DungeonSessionTests.RingLayout(), OneWeakAtStart);
        DungeonSession s = d.Session;
        RoomCombatSession start = s.Current;
        start.Buffs.Apply(AvatarFixture.JumpBuffSkill, BuffStat.JumpPower, 30, 30000);
        start.Buffs.Apply(AvatarFixture.MagicBuffSkill, BuffStat.MagicalAttack, 40, 600);

        RunMs(s, 1000, 0, 0, true);                                           // 清房,出门去右边的房间
        RunToCell(s, 1, 0, 1, 0);
        RoomCombatSession next = s.Current;
        Assert.NotSame(start, next);
        Assert.True(next.Buffs.Has(AvatarFixture.JumpBuffSkill));              // 增益跟着人走
        double carried = next.Buffs.Find(AvatarFixture.JumpBuffSkill).RemainingMs;
        Assert.InRange(carried, 1.0, 30000.0);
        Assert.False(next.Buffs.Has(AvatarFixture.MagicBuffSkill));            // 路上已经到点的那个不在了(它的时长只有 600 毫秒,走到门口早过了)
        Assert.Equal(1, next.Buffs.Count);

        RunToCell(s, 0, 0, -1, 0);                                            // 走回去:起点格那一份也换成最新的(时间更少)
        Assert.Same(start, s.Current);
        Assert.True(start.Buffs.Has(AvatarFixture.JumpBuffSkill));
        Assert.True(start.Buffs.Find(AvatarFixture.JumpBuffSkill).RemainingMs < carried);
    }
}
