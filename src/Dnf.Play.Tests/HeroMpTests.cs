using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 主角的魔法值(证据 E-087):放技能扣 [consume MP]、不够放不出来、按 [MP regen speed](每分钟,推断的单位)连续恢复。
/// 数全是合成的:魔法上限 50、恢复速度 60 点/分钟 = 每秒 1 点,方便心算。
/// </summary>
public class HeroMpTests
{
    private static ScriptValue F(float value) => new ScriptValue(ScriptValueKind.Float, BitConverter.SingleToInt32Bits(value), null);

    private static ScriptValue I(int value) => new ScriptValue(ScriptValueKind.Int, value, null);

    private static HeroVitals Vitals(int mpMax = 50, double regenPerMinute = 60.0)
    {
        return new HeroVitals(1, 100, mpMax, 0, 0, 0, regenPerMinute);
    }

    // ------------------------------------------------------------------ HeroVitals

    [Fact]
    public void SpendingTakesMpWhenThereIsEnoughAndRefusesWithoutTouchingItWhenThereIsNot()
    {
        HeroVitals v = Vitals();
        Assert.True(v.TrySpendMp(13));
        Assert.Equal(37, v.Mp);
        Assert.True(v.TrySpendMp(37));                                   // 正好花光
        Assert.Equal(0, v.Mp);
        Assert.False(v.TrySpendMp(1));                                   // 不够:不扣
        Assert.Equal(0, v.Mp);
        Assert.True(v.TrySpendMp(0));                                    // 零与负数的消耗恒成功、不动魔法值
        Assert.True(v.TrySpendMp(-5));
        Assert.Equal(0, v.Mp);
    }

    [Fact]
    public void ADeadHeroCannotSpendMpEvenIfHeStillHasSome()
    {
        HeroVitals v = Vitals();
        Assert.True(v.Damage(1000));
        Assert.False(v.IsAlive);
        Assert.Equal(50, v.Mp);
        Assert.False(v.TrySpendMp(1));
        Assert.Equal(50, v.Mp);
    }

    [Fact]
    public void RegenAddsWholePointsAndKeepsTheFractionForTheNextStep()
    {
        HeroVitals v = Vitals(regenPerMinute: 60.0);                     // 每秒 1 点
        v.TrySpendMp(20);
        for (int i = 0; i < 9; i++)
        {
            v.RegenMp(100);                                              // 累计 900 毫秒:还不到 1 点
        }
        Assert.Equal(30, v.Mp);
        v.RegenMp(100);                                                  // 满 1000 毫秒:加 1 点
        Assert.Equal(31, v.Mp);
        v.RegenMp(2500);                                                 // 一步里攒出两点半:加 2,留 0.5
        Assert.Equal(33, v.Mp);
        v.RegenMp(500);
        Assert.Equal(34, v.Mp);                                          // 留下的 0.5 加上这一步的 0.5
    }

    [Fact]
    public void RegenStopsAtTheMaximumAndDoesNotBankPointsWhileFull()
    {
        HeroVitals v = Vitals();
        v.RegenMp(60000);                                                // 满的时候不攒
        Assert.Equal(50, v.Mp);
        v.TrySpendMp(1);
        v.RegenMp(500);                                                  // 这一点要从头攒,不会有预存的
        Assert.Equal(49, v.Mp);
        v.RegenMp(500);
        Assert.Equal(50, v.Mp);
        v.RegenMp(100000);
        Assert.Equal(50, v.Mp);
    }

    [Fact]
    public void RegenIgnoresZeroSpeedNonPositiveTimeAndTheDead()
    {
        HeroVitals none = Vitals(regenPerMinute: 0.0);
        none.TrySpendMp(10);
        none.RegenMp(60000);
        Assert.Equal(40, none.Mp);

        HeroVitals v = Vitals();
        v.TrySpendMp(10);
        v.RegenMp(0);
        v.RegenMp(-1000);
        Assert.Equal(40, v.Mp);

        Assert.True(v.Damage(1000));                                     // 死了不恢复
        v.RegenMp(60000);
        Assert.Equal(40, v.Mp);

        var negative = new HeroVitals(1, 100, 50, 0, 0, 0, -5.0);        // 负数与 NaN 当作不恢复
        negative.TrySpendMp(10);
        negative.RegenMp(60000);
        Assert.Equal((0.0, 40), (negative.MpRegenPerMinute, negative.Mp));
        Assert.Equal(0.0, new HeroVitals(1, 100, 50, 0, 0, 0, double.NaN).MpRegenPerMinute);
    }

    [Fact]
    public void RestoreRefillsAndDropsTheBankedFraction()
    {
        HeroVitals v = Vitals();
        v.TrySpendMp(10);
        v.RegenMp(400);                                                  // 攒了 0.4
        v.Restore();
        Assert.Equal(50, v.Mp);
        v.TrySpendMp(1);
        v.RegenMp(600);                                                  // 如果还带着 0.4,这里会凑够 1 点
        Assert.Equal(49, v.Mp);
    }

    [Fact]
    public void CopyStateCarriesTheBankedFractionUnlessTheOtherHeroIsFull()
    {
        HeroVitals from = Vitals();
        from.TrySpendMp(10);
        from.RegenMp(400);                                               // 40/50,攒了 0.4
        HeroVitals to = Vitals();
        to.CopyStateFrom(from);
        Assert.Equal(40, to.Mp);
        to.RegenMp(600);                                                 // 0.4 + 0.6 = 1 点
        Assert.Equal(41, to.Mp);

        HeroVitals full = Vitals();
        full.TrySpendMp(10);
        full.RegenMp(400);                                               // 自己攒着 0.4
        full.CopyStateFrom(Vitals());                                    // 抄一个满的:魔法值变满,攒的量清掉
        Assert.Equal(50, full.Mp);
        full.TrySpendMp(1);
        full.RegenMp(600);                                               // 清掉了:这一点要从头攒,不会凑够
        Assert.Equal(49, full.Mp);
    }

    [Fact]
    public void FromChrReadsTheFirstMpRegenSpeedWhetherStoredAsAnIntegerOrAFloat()
    {
        var floaty = new CharacterDef(0, "character/test/floaty.chr", new[]
        {
            new ScriptLine("[MP MAX]", new[] { F(60f) }),
            new ScriptLine("[MP regen speed]", new[] { F(12.5f) }),
            new ScriptLine("[MP regen speed]", new[] { F(999f) }),       // 成长表里的同名标签:只取第一次出现
        });
        Assert.Equal(12.5, HeroVitals.FromChr(floaty).MpRegenPerMinute);

        var integer = new CharacterDef(0, "character/test/int.chr", new[] { new ScriptLine("[MP regen speed]", new[] { I(30) }) });
        Assert.Equal(30.0, HeroVitals.FromChr(integer).MpRegenPerMinute);

        var bare = new CharacterDef(0, "character/test/bare.chr", new[] { new ScriptLine("[MP MAX]", new[] { I(10) }) });
        Assert.Equal(0.0, HeroVitals.FromChr(bare).MpRegenPerMinute);    // 没写:不恢复
        Assert.Equal(0.0, HeroVitals.FromChr(null).MpRegenPerMinute);
    }

    // ------------------------------------------------------------------ 技能耗蓝

    /// <summary>一个没有怪物的迷你房间:主角带 A、S、D 三个技能(合成数据:鬼斩耗 13、上挑 3 级耗 12、后跳耗 2)。</summary>
    private sealed class MpArena : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public RoomCombatSession Session { get; }
        public HeroVitals Vitals { get; }

        public MpArena(HeroVitals vitals, bool giveVitals = true)
        {
            Assets = DnfAssets.Open(_fx.Root);
            Vitals = vitals;
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
            TownHero hero = TownHero.Create(Assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), 300, 100, false, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));
            var data = new GameData(new PvfScriptSource(Assets.Pvf));
            HeroSkills skills = HeroSkills.Create(data, data.GetCharacter(0), 0);
            var plain = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
            Session = new RoomCombatSession(Assets, room, new List<MonsterActor>(), plain, 1, default, default, skills, 0, -1f, null,
                giveVitals ? vitals : null, null);
        }

        /// <summary>每 10 毫秒一步推进 <paramref name="ms"/> 毫秒;第一步按下 <paramref name="keys"/>。</summary>
        public void Run(int ms, SkillKeys keys = SkillKeys.None)
        {
            for (int t = 0; t < ms; t += 10)
            {
                Session.Update(10, 0, 0, false, false, t == 0 ? keys : SkillKeys.None);
            }
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    [Fact]
    public void CastingTakesTheSkillsConsumeMpAtTheMomentItStarts()
    {
        using var arena = new MpArena(Vitals(regenPerMinute: 0.0));
        arena.Run(10, SkillKeys.A);                                      // 鬼斩:耗 13
        Assert.Equal(1, arena.Session.SkillCasts);
        Assert.Equal(AvatarFixture.HardAttackMp, 50 - arena.Vitals.Mp);

        arena.Run(10, SkillKeys.S);                                      // 鬼斩还在放:上挑起不来(忙),不扣蓝
        Assert.Equal(1, arena.Session.SkillCasts);
        Assert.Equal(50 - AvatarFixture.HardAttackMp, arena.Vitals.Mp);
    }

    [Fact]
    public void ASkillWithoutEnoughMpDoesNotCastDoesNotCostAnythingAndDoesNotStartTheCooldown()
    {
        var poor = Vitals(regenPerMinute: 0.0);
        poor.TrySpendMp(45);                                             // 剩 5:不够鬼斩的 13(也不够上挑的 12),但够后跳的 2
        using var arena = new MpArena(poor);
        HeroSkillSlot hard = arena.Session.Skills.SlotOf(SkillKeys.A);

        arena.Run(10, SkillKeys.A);
        Assert.Equal(0, arena.Session.SkillCasts);
        Assert.Equal(5, poor.Mp);
        Assert.True(hard.Ready);                                         // 没起冷却
        Assert.Equal(HeroMotion.Rest, arena.Session.Hero.Motion);        // 主角没有动

        arena.Run(10, SkillKeys.D);                                      // 后跳耗 2:够
        Assert.Equal(1, arena.Session.SkillCasts);
        Assert.Equal(3, poor.Mp);
    }

    [Fact]
    public void CastResultSaysNoMpAndABusyHeroNeitherSpendsNorCools()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkills skills = HeroSkills.Create(data, data.GetCharacter(0), 0);
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        TownHero hero = TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), 100, 50, false, TownHero.DungeonRestTag);
        HeroSkillSlot hard = skills.SlotOf(SkillKeys.A);
        HeroSkillSlot back = skills.SlotOf(SkillKeys.D);

        HeroVitals broke = Vitals(regenPerMinute: 0.0);
        broke.TrySpendMp(50);
        Assert.Equal(SkillCastResult.NoMp, skills.Cast(hard, hero, broke));
        Assert.Equal((0.0, 0), (hard.CooldownLeftMs, broke.Mp));
        Assert.Equal(HeroMotion.Rest, hero.Motion);

        // 没给魔法值对象:不耗蓝(旧行为)
        HeroVitals rich = Vitals(regenPerMinute: 0.0);
        Assert.Equal(SkillCastResult.Cast, skills.Cast(hard, hero));
        Assert.Equal(50, rich.Mp);

        // 够用但此刻忙(鬼斩还在放):返回 Busy,不扣蓝、不起冷却
        Assert.Equal(SkillCastResult.Busy, skills.Cast(back, hero, rich));
        Assert.Equal((50, 0.0), (rich.Mp, back.CooldownLeftMs));

        // 死了的主角放不出来
        HeroVitals dead = Vitals();
        dead.Damage(1000);
        Assert.Equal(SkillCastResult.NoMp, skills.Cast(back, hero, dead));
    }

    [Fact]
    public void TheSessionRegeneratesMpOnTheWallClockAndAFullHeroStaysFull()
    {
        var v = Vitals(regenPerMinute: 60.0);
        v.TrySpendMp(30);
        using var arena = new MpArena(v);
        arena.Run(3000);                                                 // 每秒 1 点,3 秒 +3
        Assert.Equal(23, v.Mp);
        arena.Run(30000);                                                // 再过 30 秒:回满,不会超过上限
        Assert.Equal(50, v.Mp);
    }

    [Fact]
    public void WithoutVitalsThereIsNoMpAtAllAndSkillsCastForFree()
    {
        using var arena = new MpArena(Vitals(), giveVitals: false);
        arena.Run(10, SkillKeys.A);
        Assert.Equal(1, arena.Session.SkillCasts);
        Assert.Equal(50, arena.Vitals.Mp);                               // 魔法值对象没有接进会话,所以一点没动
    }

    [Fact]
    public void ADeadHeroNeitherCastsNorRegenerates()
    {
        var v = Vitals(regenPerMinute: 60.0);
        v.TrySpendMp(30);
        using var arena = new MpArena(v);
        v.Damage(1000);
        arena.Run(3000, SkillKeys.A);
        Assert.Equal(0, arena.Session.SkillCasts);
        Assert.Equal(20, v.Mp);
    }

    // ------------------------------------------------------------------ 技能槽的显示状态(证据 E-112)

    private static HudSkillState Slot(MpArena arena, SkillKeys key)
    {
        HeroSkills skills = arena.Session.Skills;
        int index = skills.Slots.ToList().IndexOf(skills.SlotOf(key));
        return arena.Session.SkillSlotStates().Single(s => s.Slot == index);
    }

    [Fact]
    public void AnIdleHeroWithMpShowsEverySlotLitWithAGoldLabelAndNoShade()
    {
        using var arena = new MpArena(Vitals(regenPerMinute: 0.0));
        IReadOnlyList<HudSkillState> states = arena.Session.SkillSlotStates();
        Assert.Equal(arena.Session.Skills.Slots.Count, states.Count);
        Assert.True(states.Count >= 3);
        foreach (HudSkillState s in states)
        {
            Assert.True(s.Usable);
            Assert.False(s.LabelGray);
            Assert.Equal(0.0, s.CooldownFraction);
            Assert.True(s.Level >= 1);
        }
    }

    [Fact]
    public void WhileTheHeroCastsEverySlotGoesGrayWithAGrayLabelAndOnlyTheCastSkillShowsTheShade()
    {
        using var arena = new MpArena(Vitals(regenPerMinute: 0.0));
        arena.Run(10, SkillKeys.A);
        Assert.True(arena.Session.Hero.IsAttacking);

        HudSkillState hard = Slot(arena, SkillKeys.A);
        Assert.False(hard.Usable);
        Assert.True(hard.LabelGray);
        Assert.InRange(hard.CooldownFraction, 0.99, 1.0);                // 刚起冷却:几乎整格都盖着
        Assert.InRange(HudArt.CooldownRows(hard.CooldownFraction), HudArt.SkillIconSize - 1, HudArt.SkillIconSize);

        foreach (SkillKeys other in new[] { SkillKeys.S, SkillKeys.D })
        {
            HudSkillState s = Slot(arena, other);
            Assert.False(s.Usable);                                      // 主角在忙:没在冷却的槽也变灰(基线 S08 的 5 到 15 帧)
            Assert.True(s.LabelGray);
            Assert.Equal(0.0, s.CooldownFraction);
        }
    }

    [Fact]
    public void AfterTheCastEndsTheOtherSlotsLightUpAgainAndTheCastSkillKeepsAGoldLabelUnderTheShadeWhileItCools()
    {
        using var arena = new MpArena(Vitals(regenPerMinute: 0.0));
        arena.Run(10, SkillKeys.A);
        for (int i = 0; i < 400 && arena.Session.Hero.IsAttacking; i++)
        {
            arena.Run(10);
        }
        Assert.False(arena.Session.Hero.IsAttacking);

        HudSkillState hard = Slot(arena, SkillKeys.A);
        Assert.False(hard.Usable);                                       // 冷却中:图标还是灰的
        Assert.False(hard.LabelGray);                                    // 但小字回到金色(基线 S08 的 16 到 29 帧)
        Assert.InRange(hard.CooldownFraction, 0.01, 0.99);
        foreach (SkillKeys other in new[] { SkillKeys.S, SkillKeys.D })
        {
            HudSkillState s = Slot(arena, other);
            Assert.True(s.Usable);
            Assert.False(s.LabelGray);
        }

        // 冷却走完:遮罩没了,图标亮了
        arena.Run(AvatarFixture.HardAttackCool);
        hard = Slot(arena, SkillKeys.A);
        Assert.True(hard.Usable);
        Assert.Equal(0.0, hard.CooldownFraction);
    }

    [Fact]
    public void NotEnoughMpOrADeadOrAirborneHeroGraysTheLabelToo()
    {
        var poor = Vitals(regenPerMinute: 0.0);
        poor.TrySpendMp(45);                                             // 剩 5:够后跳的 2,不够鬼斩的 13
        using (var arena = new MpArena(poor))
        {
            HudSkillState hard = Slot(arena, SkillKeys.A);
            Assert.False(hard.Usable);
            Assert.True(hard.LabelGray);
            Assert.Equal(0.0, hard.CooldownFraction);                    // 没在冷却:不盖遮罩
            Assert.True(Slot(arena, SkillKeys.D).Usable);
            Assert.False(Slot(arena, SkillKeys.D).LabelGray);
        }

        var dead = Vitals(regenPerMinute: 0.0);
        using (var arena = new MpArena(dead))
        {
            dead.Damage(1000);
            Assert.All(arena.Session.SkillSlotStates(), s => Assert.True(!s.Usable && s.LabelGray));
        }

        using (var arena = new MpArena(Vitals(regenPerMinute: 0.0)))
        {
            arena.Session.Update(10, 0, 0, false, true, SkillKeys.None);   // 起跳
            for (int i = 0; i < 20 && !arena.Session.Hero.IsAirborne; i++)
            {
                arena.Session.Update(10, 0, 0, false, false, SkillKeys.None);
            }
            Assert.True(arena.Session.Hero.IsAirborne);
            Assert.All(arena.Session.SkillSlotStates(), s => Assert.True(!s.Usable && s.LabelGray));
        }
    }

    [Fact]
    public void WithoutVitalsThereAreNoSlotStatesBecauseThereIsNoHud()
    {
        using var arena = new MpArena(Vitals(), giveVitals: false);
        Assert.Empty(arena.Session.SkillSlotStates());
    }

    [Fact]
    public void TheHeroMayBeginASkillOnlyWhenOnTheGroundAndNotSwingingHurtOrDown()
    {
        using var arena = new MpArena(Vitals(regenPerMinute: 0.0));
        TownHero hero = arena.Session.Hero;
        Assert.True(hero.CanBeginSkill);
        arena.Run(10, SkillKeys.A);                                      // 放技能:在挥击里
        Assert.True(hero.IsAttacking);
        Assert.False(hero.CanBeginSkill);
        hero.Place(300, 100, false, HeroMotion.Rest, 0);
        Assert.True(hero.CanBeginSkill);
        Assert.True(hero.BeginHurt(300, 0, 0, false));                   // 被打中:受击硬直里也不能放
        Assert.True(hero.IsHurt);
        Assert.False(hero.CanBeginSkill);
        hero.Place(300, 100, false, HeroMotion.Rest, 0);
        Assert.True(hero.BeginDown());                                   // 倒地
        Assert.True(hero.IsDown);
        Assert.False(hero.CanBeginSkill);
    }
}
