using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 出招指令放技能与倒地起身(M47,证据 E-140):<see cref="HeroSkills.TryCastCommand"/>(长指令优先、放不成就落到短指令)、
/// <see cref="HeroSkills.TryChainCommand"/>(只看扳机)、只靠指令放的槽、<see cref="RoomCombatSession"/> 里指令键(Z)的接线与躺着时的倒地起身。
/// 数据全是合成的(见 <see cref="AvatarFixture"/>):901 是两段技能,指令"右,技能"(先后);903 是单段技能,指令"技能";
/// 190 是合成的倒地起身类技能(指令、起身时长、耗蓝与冷却都是 <see cref="AvatarFixture.QuickStandSkill"/> 一带的合成常数,与原版数据无关)。
/// </summary>
public sealed class CommandCastTests
{
    private static SkillRecipes Recipes()
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.ChainSkill,
                new[]
                {
                    new SkillStageSpec(motionIndex: 2, attackInfoIndex: 1, damageRateColumn: 0, damageFixedColumn: 2, effectIndex: -1, effectFrame: 0),
                    new SkillStageSpec(motionIndex: 3, attackInfoIndex: 2, damageRateColumn: 1, damageFixedColumn: 3, effectIndex: -1, effectFrame: 0),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 2),
            new SkillRecipe(AvatarFixture.WaveSkill,
                new[] { new SkillStageSpec(motionIndex: 3, attackInfoIndex: 2, damageRateColumn: 0, damageFixedColumn: 1, effectIndex: -1, effectFrame: 0) },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1),
            new SkillRecipe(AvatarFixture.JumpCommandSkill,
                new[] { new SkillStageSpec(motionIndex: 2, attackInfoIndex: 1, damageRateColumn: 0, damageFixedColumn: 1, effectIndex: -1, effectFrame: 0) },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: 1),
        });
    }

    /// <summary>901 放在快捷栏第 1 格(键 A);按参数再学 903(只靠指令)与 190(可选放进快捷栏的某一格)。</summary>
    private static HeroSkills Load(GameData data, bool learnWave = true, bool learnQuickStand = false, int quickStandPosition = -1, bool learnJump = false)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.ChainSkill, 2));
        if (learnWave)
        {
            levels.Add(new KeyValuePair<int, int>(AvatarFixture.WaveSkill, 1));
        }
        if (learnQuickStand)
        {
            levels.Add(new KeyValuePair<int, int>(AvatarFixture.QuickStandSkill, 1));
        }
        if (learnJump)
        {
            levels.Add(new KeyValuePair<int, int>(AvatarFixture.JumpCommandSkill, 1));
        }
        var quick = new List<KeyValuePair<int, int>> { new KeyValuePair<int, int>(0, AvatarFixture.ChainSkill) };
        if (quickStandPosition >= 0)
        {
            quick.Add(new KeyValuePair<int, int>(quickStandPosition, AvatarFixture.QuickStandSkill));
        }
        book.Restore(levels, quick, 0, 0);
        return HeroSkills.Create(data, chr, 0, book, Recipes());
    }

    private static TownHero NewHero(DnfAssets assets, bool facingLeft = false)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), 100, 50, facingLeft, TownHero.DungeonRestTag);
    }

    private static void Press(CommandInput input, int dirX = 0, int dirY = 0, bool skill = false, bool jump = false, bool buff = false, double ms = 16.0)
    {
        input.Update(ms, dirX, dirY, false, jump, skill, buff);
    }

    // ------------------------------------------------------------------ 槽

    [Fact]
    public void CommandSlotsHoldTheLearnedImplementedSkillsThatAreNotInTheQuickBar()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)), learnQuickStand: true);

        Assert.Equal(new[] { AvatarFixture.ChainSkill }, skills.Slots.Select(s => s.SkillId).ToArray());   // 快捷栏里只有 901
        // 只靠指令的槽:学了、有指令、有放法、不在快捷栏——被动、没有指令的(夹具里的三个旧技能)都不进来
        Assert.Equal(new[] { AvatarFixture.QuickStandSkill, AvatarFixture.WaveSkill }, skills.CommandSlots.Select(s => s.SkillId).ToArray());
        HeroSkillSlot wave = skills.SlotOfSkill(AvatarFixture.WaveSkill);
        Assert.NotNull(wave);
        Assert.Same(wave, skills.CommandSlots[1]);
        Assert.Equal((SkillKeys.None, -1, 1), (wave.Key, wave.Position, wave.Level));
        Assert.Equal(15, wave.ConsumeMp);
        Assert.Equal(2500, wave.CooldownMs);
        Assert.Equal("Skill", wave.Def.Command.ToString());
        Assert.Equal("Right+Skill", skills.SlotOf(SkillKeys.A).Def.Command.ToString());

        // 没学的不在
        HeroSkills without = Load(new GameData(new PvfScriptSource(assets.Pvf)), learnWave: false);
        Assert.Empty(without.CommandSlots);
        Assert.Null(without.SlotOfSkill(AvatarFixture.WaveSkill));

        // 放进快捷栏的技能共用同一个槽,不会在指令槽里再出现一份
        HeroSkills bound = Load(new GameData(new PvfScriptSource(assets.Pvf)), learnQuickStand: true, quickStandPosition: 1);
        Assert.Equal(new[] { AvatarFixture.ChainSkill, AvatarFixture.QuickStandSkill }, bound.Slots.Select(s => s.SkillId).ToArray());
        Assert.Equal(new[] { AvatarFixture.WaveSkill }, bound.CommandSlots.Select(s => s.SkillId).ToArray());
        Assert.Same(bound.Slots[1], bound.SlotOfSkill(AvatarFixture.QuickStandSkill));
    }

    [Fact]
    public void ADuplicateSkillInTheCommandListIsDroppedAndTheTwoListsShareCooldownBookkeeping()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkills a = Load(data);
        HeroSkillSlot quick = a.Slots[0];
        HeroSkillSlot command = a.CommandSlots[0];
        var dup = new HeroSkills(new[] { quick }, new[] { command, new HeroSkillSlot(SkillKeys.None, quick.SkillId, 1, quick.Def), null });
        Assert.Single(dup.CommandSlots);                                  // 同编号的、空的都不收
        Assert.Same(quick, dup.SlotOfSkill(quick.SkillId));

        // 冷却倒数与换房间带过去都管两种槽(各放一次得到满冷却:901 是 3000、903 是 2500)
        Assert.Equal(SkillCastResult.Cast, a.Cast(quick, NewHero(assets)));
        Assert.Equal(SkillCastResult.Cast, a.Cast(command, NewHero(assets)));
        a.Advance(300);
        Assert.Equal((2700.0, 2200.0), (quick.CooldownLeftMs, command.CooldownLeftMs));
        HeroSkills b = Load(data);
        b.CopyCooldownsFrom(a);
        Assert.Equal((2700.0, 2200.0), (b.Slots[0].CooldownLeftMs, b.CommandSlots[0].CooldownLeftMs));
    }

    [Fact]
    public void HoldKeysAddTheTriggerKeyOfTheCommandToTheQuickKey()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkills skills = Load(data);
        Assert.Equal(SkillKeys.A | SkillKeys.CommandSkill, skills.SlotOf(SkillKeys.A).HoldKeys);            // 快捷键加扳机
        Assert.Equal(SkillKeys.CommandSkill, skills.SlotOfSkill(AvatarFixture.WaveSkill).HoldKeys);          // 只靠指令:只有扳机
        // 没有指令的槽只有快捷键;没有技能定义的槽也是
        SkillDef noCommand = data.GetSkill(0, AvatarFixture.HardAttackSkill);
        Assert.Equal(SkillKeys.S, new HeroSkillSlot(SkillKeys.S, AvatarFixture.HardAttackSkill, 1, noCommand).HoldKeys);
        Assert.Equal(SkillKeys.D, new HeroSkillSlot(SkillKeys.D, 1, 1, null).HoldKeys);
        Assert.Equal(SkillKeys.None, new HeroSkillSlot(SkillKeys.None, 1, 1, null).HoldKeys);
    }

    // ------------------------------------------------------------------ 放

    [Fact]
    public void TheLongestMatchingCommandWinsAndAShorterOneIsTheFallbackWhileItIsCooling()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)));
        TownHero hero = NewHero(assets);
        var input = new CommandInput();

        // 只按技能键:只有"技能"(903)打出来了
        Press(input, skill: true);
        HeroSkillSlot first = skills.TryCastCommand(input, hero, null, 0, 0);
        Assert.Equal(AvatarFixture.WaveSkill, first.SkillId);
        Assert.Equal((HeroMotion.Skill, AvatarFixture.WaveSkill), (hero.Motion, hero.SkillId));
        Assert.Equal((2500.0, SkillCastResult.Cast, true), (first.CooldownLeftMs, skills.LastCastResult, skills.CommandMatched));
        hero.Update(1000, 0, 0);                                         // 放完回到站立
        Assert.Equal(HeroMotion.Rest, hero.Motion);

        // 按着朝前的方向键再按技能键:"右,技能"(901)比"技能"长,先放它
        skills.Advance(3000);                                            // 冷却走完
        Press(input, dirX: 1);
        Press(input, dirX: 1, skill: true);
        HeroSkillSlot second = skills.TryCastCommand(input, hero, null, 1, 0);
        Assert.Equal(AvatarFixture.ChainSkill, second.SkillId);
        Assert.Equal((3000.0, 0.0), (second.CooldownLeftMs, skills.SlotOfSkill(AvatarFixture.WaveSkill).CooldownLeftMs));
        hero.Update(1000, 0, 0);

        // 901 在冷却:同一串按键落到"技能"(903)上
        Press(input, dirX: 1);
        Press(input, dirX: 1, skill: true);
        HeroSkillSlot third = skills.TryCastCommand(input, hero, null, 1, 0);
        Assert.Equal(AvatarFixture.WaveSkill, third.SkillId);
        Assert.Equal(SkillCastResult.Cast, skills.LastCastResult);
        hero.Update(1000, 0, 0);

        // 两个都在冷却:放不出来,打出了指令,最后的结果是冷却
        Press(input, dirX: 1);
        Press(input, dirX: 1, skill: true);
        Assert.Null(skills.TryCastCommand(input, hero, null, 1, 0));
        Assert.Equal((true, SkillCastResult.Cooldown), (skills.CommandMatched, skills.LastCastResult));
        Assert.Equal(HeroMotion.Rest, hero.Motion);
    }

    [Fact]
    public void AMatchedCommandThatCannotStartReportsWhyAndChangesNothingElse()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkills skills = Load(data);
        TownHero hero = NewHero(assets);
        var input = new CommandInput();

        // 没打出任何指令:没有结果、不改上一次的结果
        Press(input, dirX: 1);
        Assert.Null(skills.TryCastCommand(input, hero));
        Assert.Equal((false, SkillCastResult.NotBound), (skills.CommandMatched, skills.LastCastResult));
        Assert.Null(skills.TryCastCommand(null, hero));
        Assert.Null(skills.TryCastCommand(input, null));

        // 魔法值不够:901 要 20、903 要 15,只有 10 点,两个都放不出来,不扣蓝
        var poor = new HeroVitals(1, 100, 10, 0, 0, 0);
        Press(input, dirX: 1);
        Press(input, dirX: 1, skill: true);
        Assert.Null(skills.TryCastCommand(input, hero, poor, 1, 0));
        Assert.Equal((true, SkillCastResult.NoMp, 10), (skills.CommandMatched, skills.LastCastResult, poor.Mp));
        Assert.All(skills.Slots.Concat(skills.CommandSlots), s => Assert.Equal(0.0, s.CooldownLeftMs));

        // 17 点:901 要 20 不够,落到 903(15)放得出来,扣 15
        var some = new HeroVitals(1, 100, 17, 0, 0, 0);
        Press(input, dirX: 1);
        Press(input, dirX: 1, skill: true);
        HeroSkillSlot cast = skills.TryCastCommand(input, hero, some, 1, 0);
        Assert.Equal(AvatarFixture.WaveSkill, cast.SkillId);
        Assert.Equal(2, some.Mp);

        // 正在放别的技能:主角忙,放不出来,不进冷却
        skills.Advance(5000);
        TownHero busy = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), busy));
        Press(input, skill: true);
        Assert.Null(skills.TryCastCommand(input, busy));
        Assert.Equal((true, SkillCastResult.Busy, 0.0), (skills.CommandMatched, skills.LastCastResult, skills.SlotOfSkill(AvatarFixture.WaveSkill).CooldownLeftMs));
    }

    [Fact]
    public void ACastClearsTheInputHistoryAndTheSameKeysDoNotCastTwice()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)), learnWave: false);
        TownHero hero = NewHero(assets);
        var input = new CommandInput();
        Press(input, dirX: 1);
        Press(input, dirX: 1, skill: true);
        Assert.NotNull(skills.TryCastCommand(input, hero, null, 1, 0));
        Assert.False(input.JustPressed(CommandKey.Skill));               // 历史清了
        hero.Update(1000, 0, 0);
        skills.Advance(5000);
        Assert.Null(skills.TryCastCommand(input, hero, null, 1, 0));     // 同一串按键不会再放一次
        Assert.False(skills.CommandMatched);
    }

    [Fact]
    public void TheForwardKeyIsReadFromTheFacingAfterThisStepsTurn()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)));

        // 朝左的主角按右键:这一步他转向右,右键就是朝前,"右,技能"打得出来(传入的方向 1 就是这一步的转向)
        TownHero hero = NewHero(assets, facingLeft: true);
        var input = new CommandInput();
        Press(input, dirX: 1);
        Press(input, dirX: 1, skill: true);
        Assert.Equal(AvatarFixture.ChainSkill, skills.TryCastCommand(input, hero, null, 1, 0).SkillId);

        // 方向没传进来(主角被什么定住转不了身,朝向还是左):同样的按键是朝后,"右,技能"打不出来,落到"技能"
        skills.Advance(5000);
        TownHero locked = NewHero(assets, facingLeft: true);
        var again = new CommandInput();
        Press(again, dirX: 1);
        Press(again, dirX: 1, skill: true);
        Assert.Equal(AvatarFixture.WaveSkill, skills.TryCastCommand(again, locked, null, 0, 0).SkillId);
    }

    [Fact]
    public void TheTwoKeyJumpCommandIsCastableButTheQuickStandSkillIsNeverCastByCommand()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 以跳跃键当扳机而指令有两个键("下,跳跃"):按指令放出来
        HeroSkills withJump = Load(data, learnJump: true);
        TownHero hero = NewHero(assets);
        var input = new CommandInput();
        Press(input, dirY: 1);
        Press(input, dirY: 1, jump: true);
        HeroSkillSlot jump = withJump.TryCastCommand(input, hero, null, 0, 1);
        Assert.Equal(AvatarFixture.JumpCommandSkill, jump.SkillId);
        Assert.Equal((HeroMotion.Skill, AvatarFixture.JumpCommandSkill, 1000.0), (hero.Motion, hero.SkillId, jump.CooldownLeftMs));

        // 倒地起身类技能的指令("下 & 跳跃",单靠跳跃键当扳机):指令打出来了也不通过 TryCastCommand 放(由房间会话在躺着时处理)
        HeroSkills withQuickStand = Load(data, learnQuickStand: true);
        TownHero another = NewHero(assets);
        var again = new CommandInput();
        Press(again, dirY: 1);
        Press(again, dirY: 1, jump: true);
        Assert.Null(withQuickStand.TryCastCommand(again, another));
        Assert.False(withQuickStand.CommandMatched);
        Assert.Equal(HeroMotion.Rest, another.Motion);

        // 增益键当扳机的指令没有对应的技能:没打出来
        Press(again, buff: true);
        Assert.Null(withQuickStand.TryCastCommand(again, another));
        Assert.False(withQuickStand.CommandMatched);
    }

    // ------------------------------------------------------------------ 接段

    [Fact]
    public void AMultiStageSkillChainsOnTheTriggerKeyAloneOnceTheFrameIsReached()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)), learnWave: false);
        TownHero hero = NewHero(assets);
        var input = new CommandInput();

        Assert.Null(skills.TryChainCommand(input, hero));                // 没在放技能
        Press(input, dirX: 1);
        Press(input, dirX: 1, skill: true);
        HeroSkillSlot slot = skills.TryCastCommand(input, hero, null, 1, 0);
        Assert.Equal((AvatarFixture.ChainSkill, 0), (slot.SkillId, hero.SkillStage));

        hero.Update(100, 0, 0);                                          // 第 1 帧:还没到接段的帧(第 2 帧)
        Press(input, skill: true);
        Assert.Null(skills.TryChainCommand(input, hero));
        hero.Update(100, 0, 0);                                          // 第 2 帧
        Press(input, buff: true);                                        // 别的键不接
        Assert.Null(skills.TryChainCommand(input, hero));
        Press(input);                                                    // 这一步没有新按下
        Assert.Null(skills.TryChainCommand(input, hero));
        Press(input, skill: true);                                       // 只按扳机、没有方向键:接下一段
        Assert.Same(slot, skills.TryChainCommand(input, hero));
        Assert.Equal((1, 0), (hero.SkillStage, hero.CurrentFrame));
        Assert.Equal(3000.0, slot.CooldownLeftMs);                       // 接段不重置冷却

        Press(input, skill: true);                                       // 最后一段后面没有下一段
        Assert.Null(skills.TryChainCommand(input, hero));
        Assert.Null(skills.TryChainCommand(null, hero));
        Assert.Null(skills.TryChainCommand(input, null));
    }

    [Fact]
    public void ASingleStageSkillNeverChainsAndACommandOnlySlotHasNoQuickKeyToChainOn()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)));
        TownHero hero = NewHero(assets);
        var input = new CommandInput();

        // 903 只有一段:再按技能键没有下一段
        Press(input, skill: true);
        Assert.NotNull(skills.TryCastCommand(input, hero));
        hero.Update(100, 0, 0);
        Press(input, skill: true);
        Assert.Null(skills.TryChainCommand(input, hero));

        // 只靠指令的槽没有快捷键:快捷键这条路接不了段
        Assert.Null(skills.TryChain(SkillKeys.A | SkillKeys.S, hero));
    }

    // ------------------------------------------------------------------ 房间会话

    private sealed class CommandArena : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public RoomCombatSession Session { get; }
        public HeroVitals Vitals { get; }
        public TownHero Hero => Session.Hero;
        public HeroSkills Skills { get; }

        public CommandArena(bool? commands = true, bool learnQuickStand = false, int quickStandPosition = -1, int mp = 50)
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
            TownHero hero = TownHero.Create(Assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), 300, 100, false, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));
            Skills = Load(new GameData(new PvfScriptSource(Assets.Pvf)), true, learnQuickStand, quickStandPosition);
            var plain = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
            Session = new RoomCombatSession(Assets, room, new List<MonsterActor>(), plain, 1, default, default, Skills, 0, -1f, null, Vitals, null);
            if (commands.HasValue)
            {
                Session.CommandsEnabled = commands.Value;
            }
        }

        /// <summary>每 10 毫秒一步推进 <paramref name="ms"/> 毫秒;第一步按下 <paramref name="keys"/> 与 <paramref name="jump"/>。</summary>
        public void Run(int ms, int dirX = 0, int dirY = 0, SkillKeys keys = SkillKeys.None, bool jump = false)
        {
            for (int t = 0; t < ms; t += 10)
            {
                Session.Update(10, dirX, dirY, false, t == 0 && jump, t == 0 ? keys : SkillKeys.None);
            }
        }

        /// <summary>把主角击倒并推进到躺着(阶段 2)。</summary>
        public void KnockDown(int maxMs = 3000)
        {
            Assert.True(Session.HeroDown.Begin(new KnockdownInput(300, false, false, false, 500, 500), 0, false, false));
            for (int t = 0; t < maxMs && Session.HeroDown.Stage != 2; t += 10)
            {
                Session.Update(10, 0, 0, false, false, SkillKeys.None);
            }
            Assert.Equal(2, Session.HeroDown.Stage);
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    [Fact]
    public void TheCommandKeyWithADirectionCastsTheCommandSkillAndCommandsAreOffByDefault()
    {
        using (var off = new CommandArena(commands: false))
        {
            off.Run(30, dirX: 1, keys: SkillKeys.CommandSkill);
            Assert.Equal((0, 0), (off.Session.SkillCasts, off.Session.CommandCasts));      // 开关关着:Z 键没有用
            Assert.NotEqual(HeroMotion.Skill, off.Hero.Motion);
        }
        using var arena = new CommandArena();
        using (var untouched = new CommandArena(commands: null))
        {
            Assert.False(untouched.Session.CommandsEnabled);                                // 默认关
        }
        arena.Run(10, dirX: 1, keys: SkillKeys.CommandSkill);
        Assert.Equal((1, 1, 0), (arena.Session.SkillCasts, arena.Session.CommandCasts, arena.Session.SkillChains));
        Assert.Equal((HeroMotion.Skill, AvatarFixture.ChainSkill, 0), (arena.Hero.Motion, arena.Hero.SkillId, arena.Hero.SkillStage));
        Assert.Equal(30, arena.Vitals.Mp);                                                  // 901 耗蓝 20
    }

    [Fact]
    public void AHeroWhoIsBusyCastingCannotTurnAround()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        HeroSkills skills = Load(new GameData(new PvfScriptSource(assets.Pvf)));
        TownHero hero = NewHero(assets);
        hero.TurnTo(true);
        Assert.True(hero.FacingLeft);                                                       // 腾得出手:转得了
        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOfSkill(AvatarFixture.WaveSkill), hero));
        hero.TurnTo(false);
        Assert.True(hero.FacingLeft);                                                       // 正在放技能:转不了
    }

    [Fact]
    public void TurningAroundAndPressingTheCommandKeyOnTheSameStepCountsTheNewFacing()
    {
        using var arena = new CommandArena();
        arena.Hero.Place(300, 100, true, HeroMotion.Rest, 0);                               // 朝左站着
        Assert.True(arena.Hero.FacingLeft);
        arena.Run(10, dirX: 1, keys: SkillKeys.CommandSkill);                               // 右键 + 技能键:这一步转向右,右键是朝前,放"右,技能"
        Assert.Equal((HeroMotion.Skill, AvatarFixture.ChainSkill), (arena.Hero.Motion, arena.Hero.SkillId));
        Assert.False(arena.Hero.FacingLeft);
    }

    [Fact]
    public void TheCommandKeyAloneCastsTheShortCommandSkillAndAFailedCommandPlaysTheCooldownVoice()
    {
        using var arena = new CommandArena();
        arena.Run(10, keys: SkillKeys.CommandSkill);
        Assert.Equal(AvatarFixture.WaveSkill, arena.Hero.SkillId);
        Assert.Equal(1, arena.Session.CommandCasts);

        // 技能放完以后,903 在冷却:再按只有"技能"的指令,放不出来,播冷却中的语音
        arena.Run(1000);
        var drained = new List<SoundCue>();
        arena.Session.DrainSounds(drained);
        drained.Clear();
        arena.Run(10, keys: SkillKeys.CommandSkill);
        arena.Session.DrainSounds(drained);
        Assert.Equal(1, arena.Session.CommandCasts);
        Assert.Contains(drained, c => c.Kind == SoundCueKind.Voice);
        Assert.NotEqual(HeroMotion.Skill, arena.Hero.Motion);
    }

    [Fact]
    public void TheCommandKeyChainsTheMultiStageSkillThroughTheSession()
    {
        using var arena = new CommandArena();
        arena.Run(10, dirX: 1, keys: SkillKeys.CommandSkill);
        Assert.Equal(0, arena.Hero.SkillStage);
        arena.Run(100);
        arena.Run(10, keys: SkillKeys.CommandSkill);                                        // 帧不够:不接段(也不会按"技能"重新起手,903 没有被打出来之外的理由)
        Assert.Equal(0, arena.Hero.SkillStage);
        Assert.Equal(0, arena.Session.SkillChains);
        arena.Run(100);                                                                     // 到第 2 帧以后
        arena.Run(10, keys: SkillKeys.CommandSkill);
        Assert.Equal((1, 1), (arena.Session.SkillChains, arena.Hero.SkillStage));
        Assert.Equal(1, arena.Session.SkillCasts);                                          // 接段不算新的一次施放
    }

    // ------------------------------------------------------------------ 倒地起身

    [Fact]
    public void ALyingHeroWhoPlaysTheQuickStandCommandGetsUpAfterTheQuickStandTimeInsteadOfTheLyingTime()
    {
        using var arena = new CommandArena(learnQuickStand: true);
        arena.KnockDown();
        RoomCombatSession s = arena.Session;
        Assert.True(s.HeroDown.CanQuickStand);

        // 只按跳跃键:指令是"下 & 跳跃",没打出来,不起身
        arena.Run(10, jump: true);
        Assert.False(s.HeroDown.QuickStanding);
        Assert.Equal((0, 50), (s.QuickStands, arena.Vitals.Mp));

        // 按住下键再按跳跃键:起身,扣蓝,冷却开始,仍是倒地姿势
        arena.Run(10, dirY: 1);
        arena.Run(10, dirY: 1, jump: true);
        Assert.Equal((true, 1, 45), (s.HeroDown.QuickStanding, s.QuickStands, arena.Vitals.Mp));
        Assert.Equal(4000.0, arena.Skills.SlotOfSkill(AvatarFixture.QuickStandSkill).CooldownLeftMs);
        Assert.True(arena.Hero.IsDown);
        Assert.Equal(HeroMotion.Down, arena.Hero.Motion);

        // 150 毫秒的倒计时:140 毫秒后还在,再 20 毫秒起身站定;躺着的正常时长是 400 毫秒,这时早就该比它短
        arena.Run(130);
        Assert.True(arena.Hero.IsDown);
        arena.Run(30);
        Assert.False(arena.Hero.IsDown);
        Assert.Equal(HeroMotion.Rest, arena.Hero.Motion);
        Assert.Equal((false, 1), (s.HeroDown.QuickStanding, s.HeroDown.QuickStands));
    }

    [Fact]
    public void WithoutQuickStandTheHeroLiesForTheWholeAnimationAndTheKeysDoNothing()
    {
        // 对照:没学这个技能,同样的按键躺满整个动画(400 毫秒)才起身
        using var arena = new CommandArena(learnQuickStand: false);
        arena.KnockDown();
        arena.Run(10, dirY: 1);
        arena.Run(10, dirY: 1, jump: true);
        Assert.Equal((false, 0), (arena.Session.HeroDown.QuickStanding, arena.Session.QuickStands));
        arena.Run(200, dirY: 1);
        Assert.True(arena.Hero.IsDown);
        arena.Run(300);
        Assert.False(arena.Hero.IsDown);
    }

    [Fact]
    public void QuickStandWaitsForTheLyingPhaseAndTheCooldownAndTheMana()
    {
        using var arena = new CommandArena(learnQuickStand: true);
        RoomCombatSession s = arena.Session;
        Assert.True(s.HeroDown.Begin(new KnockdownInput(300, false, false, false, 500, 500), 0, false, false));
        arena.Run(10, dirY: 1);
        arena.Run(10, dirY: 1, jump: true);                                                 // 腾空阶段按:不起身
        Assert.Equal((false, 0, 0), (s.HeroDown.QuickStanding, s.QuickStands, s.HeroDown.Stage));
        for (int t = 0; t < 3000 && s.HeroDown.Stage != 2; t += 10)
        {
            arena.Run(10);
        }
        Assert.Equal(2, s.HeroDown.Stage);

        arena.Run(10, dirY: 1);
        arena.Run(10, dirY: 1, jump: true);
        Assert.Equal(1, s.QuickStands);
        arena.Run(200);
        Assert.False(arena.Hero.IsDown);

        // 冷却中:再次躺下按指令不起身
        arena.KnockDown();
        arena.Run(10, dirY: 1);
        arena.Run(10, dirY: 1, jump: true);
        Assert.Equal((false, 1), (s.HeroDown.QuickStanding, s.QuickStands));
        arena.Run(500);
        Assert.False(arena.Hero.IsDown);

        // 冷却走完再躺下:能起身
        arena.Skills.Advance(5000);
        arena.KnockDown();
        arena.Run(10, dirY: 1);
        arena.Run(10, dirY: 1, jump: true);
        Assert.Equal((true, 2, 40), (s.HeroDown.QuickStanding, s.QuickStands, arena.Vitals.Mp));
        arena.Run(200);

        // 魔法值不够 5 点:不起身
        arena.Skills.Advance(5000);
        arena.Vitals.TrySpendMp(arena.Vitals.Mp - 3);
        arena.KnockDown();
        arena.Run(10, dirY: 1);
        arena.Run(10, dirY: 1, jump: true);
        Assert.Equal((false, 2, 3), (s.HeroDown.QuickStanding, s.QuickStands, arena.Vitals.Mp));
    }

    [Fact]
    public void ABoundQuickKeyAlsoQuickStandsAndCommandsOffDoNotBlockIt()
    {
        // 190 放在快捷栏第 2 格(键 S):躺着按 S 就起身,不依赖指令开关,按键用掉
        using var arena = new CommandArena(commands: false, learnQuickStand: true, quickStandPosition: 1);
        arena.KnockDown();
        arena.Run(10, dirY: 1, jump: true);                                                 // 指令开关关着:"下 & 跳跃"不起作用
        Assert.False(arena.Session.HeroDown.QuickStanding);
        arena.Run(10, keys: SkillKeys.S);
        Assert.Equal((true, 1), (arena.Session.HeroDown.QuickStanding, arena.Session.QuickStands));
        Assert.Equal(0, arena.Session.SkillCasts);                                          // 起身不算施放技能
        arena.Run(200);
        Assert.False(arena.Hero.IsDown);
    }

    [Fact]
    public void StandingHeroWhoPlaysTheQuickStandCommandJustJumps()
    {
        using var arena = new CommandArena(learnQuickStand: true);
        arena.Run(10, dirY: 1, jump: true);
        Assert.Equal(0, arena.Session.QuickStands);
        Assert.NotEqual(HeroMotion.Down, arena.Hero.Motion);
        Assert.Equal(50, arena.Vitals.Mp);
        Assert.Equal(0.0, arena.Skills.SlotOfSkill(AvatarFixture.QuickStandSkill).CooldownLeftMs);   // 没躺着:没有消耗,没有冷却
        Assert.Equal(AvatarFixture.QuickStandSkill, SkillIds.QuickStand);
        Assert.True(HeroSkills.IsImplemented(SkillIds.QuickStand));
    }
}
