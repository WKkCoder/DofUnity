using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 投掷类技能(客户端状态 0x0d 一族,<see cref="ThrowRecipe"/>)的槽、放出与房间里的运行。数据全是合成的:
/// 借夹具里的波系技能(<see cref="AvatarFixture.WaveSkill"/>:[static data] (250, 160, −320),[level info] 三列 (1 级 11、100、222,2 级 12、101、333),消耗魔法 15、冷却 2500)
/// 与蓄力跳跃技能(<see cref="AvatarFixture.HopSkill"/>:[static data] 十个数,前两个是 100、500,第 2 个起都不为 0,[level info] 一列,消耗魔法 10、冷却 3500)
/// 当"投掷类技能"用,配方由测试自己登记;姿势动画用夹具里的合成动画,标签由测试加进角色脚本。时间按 10 毫秒一步推进,
/// 放出那一步主角也走 10 毫秒,所以第 n 步结束时主角自己的时间是 10n 毫秒。
/// </summary>
public sealed class SkillThrowTests
{
    private const int HeroX = 300;
    private const int HeroY = 100;
    private const int Damage = 25;

    // ------------------------------------------------------------------ 搭数据

    private static CharacterDef WithPoseTags(CharacterDef chr, int selector, string charge, string shoot)
    {
        var lines = new List<ScriptLine>(chr.Tags.Lines);
        lines.Add(new ScriptLine("[throw motion " + selector + "-1]", new[] { new ScriptValue(ScriptValueKind.String, 0, "/" + charge) }));
        lines.Add(new ScriptLine("[throw motion " + selector + "-2]", new[] { new ScriptValue(ScriptValueKind.String, 0, "/" + shoot) }));
        return new CharacterDef(chr.Job, chr.Path, lines.ToArray());
    }

    private static SkillObjectSpec Obj(int rateColumn, int offsetY = 0)
    {
        return new SkillObjectSpec(AvatarFixture.WaveObjectId, 0, 0, damageRateColumn: rateColumn, damageFixedColumn: rateColumn, offsetY: offsetY);
    }

    /// <summary>波系技能当投掷技能:阶段 0 取 [static data] 第 <paramref name="charge"/> 个(默认 250)、阶段 1 取第 <paramref name="shoot"/> 个(默认 160)、霸体开关取第 <paramref name="armor"/> 个(−320,非 0)。</summary>
    private static SkillRecipes WaveRecipes(int charge = 0, int shoot = 1, int armor = 2, int selector = 1, SkillObjectSpec[] objects = null)
    {
        return new SkillRecipes(new SkillRecipe[0], null, null, new[]
        {
            new ThrowRecipe(AvatarFixture.WaveSkill, charge, shoot, armor, selector, objects ?? new[] { Obj(2), Obj(1, offsetY: 50), Obj(0, offsetY: -50) }),
        });
    }

    /// <summary>蓄力跳跃技能当投掷技能(只放一个对象,百分比列只有第 0 列):阶段 0、阶段 1 与霸体开关的下标由参数给。</summary>
    private static SkillRecipes HopRecipes(int charge, int shoot, int armor)
    {
        return new SkillRecipes(new SkillRecipe[0], null, null, new[]
        {
            new ThrowRecipe(AvatarFixture.HopSkill, charge, shoot, armor, 1, new[] { Obj(0) }),
        });
    }

    private static HeroSkills Load(GameData data, int skillId, SkillRecipes recipes, bool poses = true, int level = 2, string charge = AvatarFixture.ChainAAni,
        string shoot = AvatarFixture.ChainBAni, int selector = 1)
    {
        CharacterDef chr = data.GetCharacter(0);
        if (poses)
        {
            chr = WithPoseTags(chr, selector, charge, shoot);
        }
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(skillId, level));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, skillId) }, 0, 0);       // 放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, recipes);
    }

    /// <summary>两个姿势都用"每帧停留 10 秒、带站立高度受击框"的合成动画:主角在姿势里挨得到打(别的姿势动画没有受击框),动画也不会在阶段结束前播完。</summary>
    private static HeroSkills LoadHurtable(GameData data, int skillId, SkillRecipes recipes)
    {
        return Load(data, skillId, recipes, charge: AvatarFixture.GuardAni, shoot: AvatarFixture.GuardAni);
    }

    private static TownHero NewHero(DnfAssets assets)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), HeroX, HeroY, false, TownHero.DungeonRestTag);
    }

    private static SkillDef Def(int id, params int[] statics)
    {
        return new SkillDef(id, "skill/t/z.skl", new[]
        {
            new ScriptLine("[type]", new[] { new ScriptValue(ScriptValueKind.String, 0, "[active]") }),
            new ScriptLine("[static data]", statics.Select(v => new ScriptValue(ScriptValueKind.Int, v, null)).ToArray()),
        });
    }

    private static void Attach(RoomCombatSession session, DnfAssets assets)
    {
        session.AttachActs(new ActLibrary(assets, new GameData(new PvfScriptSource(assets.Pvf))));
    }

    private static void Steps(RoomCombatSession s, int count, SkillKeys keys = SkillKeys.None)
    {
        for (int i = 0; i < count; i++)
        {
            s.Update(10, 0, 0, false, false, i == 0 ? keys : SkillKeys.None);
        }
    }

    // ------------------------------------------------------------------ 槽

    [Fact]
    public void ASlotReadsEachObjectsStrikeFromItsOwnColumnAndTheTwoPosesFromTheCharacterTags()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        HeroSkillSlot slot = Load(data, AvatarFixture.WaveSkill, WaveRecipes()).SlotOf(SkillKeys.A);
        Assert.NotNull(slot.Throw);
        Assert.Null(slot.Recipe);
        Assert.Equal((AvatarFixture.WaveSkill, 2, 2500, 15), (slot.SkillId, slot.Level, slot.CooldownMs, slot.ConsumeMp));

        // 2 级的 [level info] 一行 (12, 101, 333):三个对象各取自己的列 2、1、0,百分比与固定值都取这一列
        Assert.Equal(3, slot.ObjectStrikes.Count);
        Assert.Equal(new[] { 333, 101, 12 }, slot.ObjectStrikes.Select(o => o.SkillRatePercent).ToArray());
        Assert.Equal(new[] { 333, 101, 12 }, slot.ObjectStrikes.Select(o => o.SkillFixed).ToArray());
        Assert.All(slot.ObjectStrikes, o => Assert.Equal((true, AvatarFixture.WavePushAside, true), (o.IsSkillDamage, o.PushAside, o.KnockDown)));
        Assert.Equal(AvatarFixture.WaveHitSound, slot.ObjectStrikes[0].HitWav);                // 攻击信息取自对象 .obj 的 [attack info]
        // 1 级的一行 (11, 100, 222)
        Assert.Equal(new[] { 222, 100, 11 }, Load(data, AvatarFixture.WaveSkill, WaveRecipes(), level: 1).SlotOf(SkillKeys.A).ObjectStrikes.Select(o => o.SkillRatePercent).ToArray());
        Assert.Equal(new[] { 222, 100, 11 }, Load(data, AvatarFixture.WaveSkill, WaveRecipes(), level: 1).SlotOf(SkillKeys.A).ObjectStrikes.Select(o => o.SkillFixed).ToArray());

        // 姿势按标签解析成 PVF 路径:阶段 0 的是 [throw motion 1-1],阶段 1 的是 [throw motion 1-2]
        Assert.Equal(new[] { AvatarFixture.ChainAAni, AvatarFixture.ChainBAni }, slot.ThrowPoses.ToArray());

        // 主角的身体在任何一段都不命中
        Assert.False(slot.StageHasOwnStrike(0));
        Assert.False(slot.StageHasOwnStrike(5));

        // 阶段机:阶段 0 取 [static data] 第 0 个数 250、阶段 1 取第 1 个数 160、霸体开关第 2 个数 −320 非 0
        ThrowRun run = slot.NewThrowRun();
        Assert.Equal((250, 160, true), (run.ChargeMs, run.ShootMs, run.ArmorInCharge));
        Assert.True(slot.ThrowArmor);
    }

    [Fact]
    public void ThePoseTagsComeFromTheRecipesSelectorAndBothMustBeThere()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 选择号 2:读 [throw motion 2-1]、[throw motion 2-2]
        HeroSkillSlot two = Load(data, AvatarFixture.WaveSkill, WaveRecipes(selector: 2), selector: 2, charge: AvatarFixture.ChainBAni, shoot: AvatarFixture.ChainAAni).SlotOf(SkillKeys.A);
        Assert.Equal(new[] { AvatarFixture.ChainBAni, AvatarFixture.ChainAAni }, two.ThrowPoses.ToArray());

        // 配方要选择号 2、角色脚本只有选择号 1 的两个标签:读不到
        Assert.Null(Load(data, AvatarFixture.WaveSkill, WaveRecipes(selector: 2), selector: 1).SlotOf(SkillKeys.A).ThrowPoses);

        // 角色脚本里完全没有这两个标签(夹具的角色脚本本来就没有):读不到
        Assert.Null(Load(data, AvatarFixture.WaveSkill, WaveRecipes(), poses: false).SlotOf(SkillKeys.A).ThrowPoses);

        // 只有一个标签:读不到(两段姿势缺一不可)
        CharacterDef chr = data.GetCharacter(0);
        var onlyFirst = new CharacterDef(chr.Job, chr.Path, chr.Tags.Lines.Concat(new[]
        {
            new ScriptLine("[throw motion 1-1]", new[] { new ScriptValue(ScriptValueKind.String, 0, "/" + AvatarFixture.ChainAAni) }),
        }).ToArray());
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(onlyFirst.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.WaveSkill, 2));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.WaveSkill) }, 0, 0);
        Assert.Null(HeroSkills.Create(data, onlyFirst, 0, book, WaveRecipes()).SlotOf(SkillKeys.A).ThrowPoses);
    }

    [Fact]
    public void TheArmorSwitchIsAnyNonZeroStaticValueAndAMissingSwitchOrZeroMeansNoArmor()
    {
        var recipe = new ThrowRecipe(901, 0, 1, armorStaticIndex: 2, poseSelector: 1, objects: new[] { Obj(0) });
        var noSwitch = new ThrowRecipe(901, 0, 1, armorStaticIndex: -1, poseSelector: 1, objects: new[] { Obj(0) });

        Assert.True(new HeroSkillSlot(SkillKeys.None, 901, 1, Def(901, 100, 200, 7)) { Throw = recipe }.ThrowArmor);       // 7:非 0
        Assert.True(new HeroSkillSlot(SkillKeys.None, 901, 1, Def(901, 100, 200, -5)) { Throw = recipe }.ThrowArmor);      // 负数也是非 0
        Assert.False(new HeroSkillSlot(SkillKeys.None, 901, 1, Def(901, 100, 200, 0)) { Throw = recipe }.ThrowArmor);      // 0:没有
        Assert.False(new HeroSkillSlot(SkillKeys.None, 901, 1, Def(901, 100, 200)) { Throw = recipe }.ThrowArmor);         // 下标超出 [static data]:读到 0
        Assert.False(new HeroSkillSlot(SkillKeys.None, 901, 1, Def(901, 100, 200, 7)) { Throw = noSwitch }.ThrowArmor);    // 配方没有开关项
        Assert.False(new HeroSkillSlot(SkillKeys.None, 901, 1, Def(901, 100, 200, 7)).ThrowArmor);                         // 不是投掷类技能

        // 阶段机的时长与霸体直接来自这些数;不是投掷类技能没有阶段机
        ThrowRun run = new HeroSkillSlot(SkillKeys.None, 901, 1, Def(901, 100, 200, 7)) { Throw = recipe }.NewThrowRun();
        Assert.Equal((100, 200, true), (run.ChargeMs, run.ShootMs, run.ArmorInCharge));
        Assert.Null(new HeroSkillSlot(SkillKeys.None, 901, 1, Def(901, 100, 200, 7)).NewThrowRun());
    }

    [Fact]
    public void TheArmorSwitchMayLiveAtStaticIndexZeroAndOnlyAMissingIndexMeansNoSwitch()
    {
        // 开关取第 0 个数、阶段 0 取第 1 个、阶段 1 取第 2 个:下标 0 是合法下标(只有负数表示"没有开关项")
        var atZero = new ThrowRecipe(901, 1, 2, armorStaticIndex: 0, poseSelector: 1, objects: new[] { Obj(0) });

        Assert.True(new HeroSkillSlot(SkillKeys.None, 901, 1, Def(901, 7, 100, 200)) { Throw = atZero }.ThrowArmor);     // 第 0 个数 7:非 0
        Assert.False(new HeroSkillSlot(SkillKeys.None, 901, 1, Def(901, 0, 100, 200)) { Throw = atZero }.ThrowArmor);    // 第 0 个数 0:没有霸体
        ThrowRun run = new HeroSkillSlot(SkillKeys.None, 901, 1, Def(901, 7, 100, 200)) { Throw = atZero }.NewThrowRun();
        Assert.Equal((100, 200, true), (run.ChargeMs, run.ShootMs, run.ArmorInCharge));
    }

    // ------------------------------------------------------------------ 放出

    [Fact]
    public void CastingPutsTheHeroInThePhaseZeroPoseSpendsMagicAndStartsTheCooldown()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkills skills = Load(data, AvatarFixture.HopSkill, HopRecipes(1, 0, 2));
        HeroSkillSlot slot = skills.SlotOf(SkillKeys.A);
        TownHero hero = NewHero(assets);
        var vitals = new HeroVitals(1, 1000, 50, 100, 0, 0);

        Assert.Equal(SkillCastResult.Cast, skills.Cast(slot, hero, vitals));
        Assert.Equal((HeroMotion.Skill, AvatarFixture.HopSkill, "chaina.ani", 0), (hero.Motion, hero.SkillId, hero.SkillMotionName, hero.CurrentFrame));
        Assert.Equal((HeroX, HeroY), (hero.X, hero.Y));                     // 原地摆姿势
        Assert.Equal(50 - 10, vitals.Mp);                                    // 消耗魔法 10
        Assert.Equal(3500, slot.CooldownLeftMs);                             // 冷却 3500

        // 正在放时再放:先卡在冷却上;冷却走完后主角还在放技能,腾不出手,同样不扣蓝
        Assert.Equal(SkillCastResult.Cooldown, skills.Cast(slot, hero, vitals));
        skills.Advance(4000);
        Assert.Equal(SkillCastResult.Busy, skills.Cast(slot, hero, vitals));
        Assert.Equal(40, vitals.Mp);
    }

    [Fact]
    public void WithoutBothPosesOrWithABusyHeroTheCastIsRefusedAndNothingIsSpent()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));

        // 读不到姿势:放不出来,主角不动,不扣蓝,不进冷却
        HeroSkills noPoses = Load(data, AvatarFixture.HopSkill, HopRecipes(1, 0, 2), poses: false);
        HeroSkillSlot slot = noPoses.SlotOf(SkillKeys.A);
        TownHero hero = NewHero(assets);
        var vitals = new HeroVitals(1, 1000, 50, 100, 0, 0);
        Assert.Equal(SkillCastResult.Busy, noPoses.Cast(slot, hero, vitals));
        Assert.Equal((HeroMotion.Rest, 50, 0), (hero.Motion, vitals.Mp, slot.CooldownLeftMs));

        // 姿势动画路径指到库里没有的文件:同样放不出来
        HeroSkills missing = Load(data, AvatarFixture.HopSkill, HopRecipes(1, 0, 2), charge: "character/test/animation/nothing.ani");
        Assert.Equal(SkillCastResult.Busy, missing.Cast(missing.SlotOf(SkillKeys.A), hero, vitals));
        Assert.Equal((HeroMotion.Rest, 50), (hero.Motion, vitals.Mp));

        // 对照:姿势都在,但主角正在受击硬直里:腾不出手,同样不扣蓝
        HeroSkills good = Load(data, AvatarFixture.HopSkill, HopRecipes(1, 0, 2));
        Assert.True(hero.BeginHurt(500, 0, 0, false));
        Assert.Equal(SkillCastResult.Busy, good.Cast(good.SlotOf(SkillKeys.A), hero, vitals));
        Assert.Equal((HeroMotion.Hurt, 50, 0), (hero.Motion, vitals.Mp, good.SlotOf(SkillKeys.A).CooldownLeftMs));

        // 魔法值不够(5 < 10):魔法不足,不放
        TownHero fresh = NewHero(assets);
        var poor = new HeroVitals(1, 1000, 5, 100, 0, 0);
        Assert.Equal(SkillCastResult.NoMp, good.Cast(good.SlotOf(SkillKeys.A), fresh, poor));
        Assert.Equal((HeroMotion.Rest, 5), (fresh.Motion, poor.Mp));
    }

    // ------------------------------------------------------------------ 房间里:时间线与对象

    [Fact]
    public void ThePhasesRunByTimeWithThePoseSwitchingAndTheHeroEndsOnTheGround()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, AvatarFixture.WaveSkill, WaveRecipes()));
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;

        s.Update(10, 0, 0, false, false, SkillKeys.A);                                   // 第 1 步:放出,主角时间 10
        Assert.Equal((1, 1, 0, true), (s.SkillCasts, s.ThrowCasts, s.ThrowPhase, s.ThrowActive));
        Assert.Equal((HeroMotion.Skill, "chaina.ani"), (hero.Motion, hero.SkillMotionName));

        Steps(s, 23);                                                                    // 第 24 步:240 < 250
        Assert.Equal((0, "chaina.ani"), (s.ThrowPhase, hero.SkillMotionName));
        Steps(s, 1);                                                                     // 第 25 步:250 ≥ 250 换阶段 1,姿势换第二个
        Assert.Equal((1, HeroMotion.Skill, "chainb.ani"), (s.ThrowPhase, hero.Motion, hero.SkillMotionName));

        // 放着的时候再按一次技能键:不会再放一次(冷却中)
        s.Update(10, 0, 0, false, false, SkillKeys.A);                                   // 第 26 步
        Assert.Equal((1, 1), (s.SkillCasts, s.ThrowCasts));

        Steps(s, 14);                                                                    // 第 40 步:阶段 1 走了 150 < 160
        Assert.Equal((true, 1, HeroMotion.Skill), (s.ThrowActive, s.ThrowPhase, hero.Motion));
        Steps(s, 1);                                                                     // 第 41 步:160 ≥ 160,结束
        Assert.Equal((false, -1, HeroMotion.Rest, 0), (s.ThrowActive, s.ThrowPhase, hero.Motion, hero.SkillId));
        Assert.Equal((HeroX, HeroY), (hero.X, hero.Y));
        Assert.Equal(0, s.ThrowCancels);
    }

    [Fact]
    public void APoseShorterThanItsPhaseKeepsTheHeroInTheSkillStateOnItsLastFrame()
    {
        // 阶段 0 的姿势用四帧各 50 毫秒的合成动画(共 200 毫秒),阶段 0 却要 250 毫秒:动画播完之后主角仍在放技能,停在最后一帧
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, AvatarFixture.WaveSkill, WaveRecipes(), charge: AvatarFixture.WaveObjAni));
        RoomCombatSession s = arena.Session;
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Steps(s, 19);                                                                    // 第 20 步:200 毫秒,动画刚好播完
        Assert.Equal((HeroMotion.Skill, "wave.ani", true), (s.Hero.Motion, s.Hero.SkillMotionName, s.ThrowActive));
        Steps(s, 4);                                                                     // 第 24 步:240 毫秒,还在阶段 0
        Assert.Equal((HeroMotion.Skill, 0, 3), (s.Hero.Motion, s.ThrowPhase, s.Hero.CurrentFrame));
        Steps(s, 1);                                                                     // 第 25 步:换阶段 1,摆第二个姿势
        Assert.Equal((HeroMotion.Skill, 1, "chainb.ani"), (s.Hero.Motion, s.ThrowPhase, s.Hero.SkillMotionName));
    }

    [Fact]
    public void EveryObjectIsPlacedOnceAtTheCastWithItsOwnOffsetAndHitsByItsOwnColumn()
    {
        // 主角 (300, 100) 朝右。三个对象:放在 (300, 100)、(300, 150)、(300, 50),攻击框 x 从 +20 起宽 120、y 各 ±20、高 0 到 100;
        // 怪物受击框 x 为 [x−10, x+30]、y 为 [y−6, y+6]:三只怪物 (340, 100)、(340, 150)、(340, 50) 各被对应的那一个对象打中,别的够不着
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100), (340, 150), (340, 50) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, AvatarFixture.WaveSkill, WaveRecipes()));
        Attach(arena.Session, arena.Assets);
        RoomCombatSession s = arena.Session;

        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Assert.Equal(3, s.HeroObjectsSpawned);                                           // 放出的这一步三个全放
        Assert.Equal(new[] { (300.0, 100.0), (300.0, 150.0), (300.0, 50.0) }, s.HeroObjects.Select(o => (o.X, o.Y)).ToArray());
        Assert.All(s.HeroObjects, o => Assert.Equal((false, AvatarFixture.WaveSkill, 0.0), (o.FacingLeft, o.SkillId, o.Z)));

        Steps(s, 20);
        Assert.Equal((3, 3, 3), (s.HeroObjectHits, s.Hits, s.HeroObjectsSpawned));       // 一个对象打一只,之后不再放
        Assert.Equal(new[] { true, true, true }, s.Monsters.Select(m => m.IsKnockedDown).ToArray());

        // 伤害 = 31.15 × 百分比 / 100 + 固定值(两个数取同一列),小数部分按概率进位:
        // 列值 333 → 103.73 + 333 = 436.73(436 或 437)、101 → 31.46 + 101 = 132.46(132 或 133)、12 → 3.74 + 12 = 15.74(15 或 16)
        int[] taken = s.Monsters.Select(m => 1000 - m.Hp).ToArray();
        Assert.InRange(taken[0], 436, 437);
        Assert.InRange(taken[1], 132, 133);
        Assert.InRange(taken[2], 15, 16);
    }

    [Fact]
    public void TheHeroBodyNeverStrikesDuringAThrowEvenWhenAMonsterStandsInsideThePoseAnimationsAttackBox()
    {
        // 两个姿势动画里都带一个"身体"攻击框(第一个在第 2 帧 200 毫秒起、第二个在第 1 帧 100 毫秒起):x 从 +20 起宽 60,y ±10,z 0 到 50;
        // 怪物 (340, 100) 的受击框 x 为 [330, 370]、y 为 [94, 106],正好在框里。唯一的对象放得离它很远(纵深偏 90),所以任何命中都只能来自主角身体
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, AvatarFixture.WaveSkill, WaveRecipes(objects: new[] { Obj(0, offsetY: 90) })));
        Attach(arena.Session, arena.Assets);
        RoomCombatSession s = arena.Session;

        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Steps(s, 45);                                                                    // 460 毫秒:两个姿势的攻击框时间(200 到 250、350 到 410)都走过了
        Assert.Equal((1, false, 0), (s.HeroObjectsSpawned, s.ThrowActive, s.Hits));
        Assert.Equal(1000, s.Monsters[0].Hp);
    }

    [Fact]
    public void AFacingLeftHeroPlacesTheObjectsMirroredAndWithoutAnActLibraryNothingIsPlacedButTheStateStillRuns()
    {
        // 朝左:前方偏移是 0,位置不变,朝向跟主角(对象朝左)
        using (var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, facingLeft: true, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, AvatarFixture.WaveSkill, WaveRecipes())))
        {
            Attach(arena.Session, arena.Assets);
            arena.Session.Update(10, 0, 0, false, false, SkillKeys.A);
            Assert.Equal(3, arena.Session.HeroObjectsSpawned);
            Assert.All(arena.Session.HeroObjects, o => Assert.True(o.FacingLeft));
        }

        // 没接 .act 程序库:拿不到对象模板,不放;阶段机照常走完(41 步)
        using var bare = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, AvatarFixture.WaveSkill, WaveRecipes()));
        RoomCombatSession s = bare.Session;
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Steps(s, 39);
        Assert.Equal((0, 0, true), (s.HeroObjectsSpawned, s.HeroObjects.Count, s.ThrowActive));    // 第 40 步
        Steps(s, 1);
        Assert.Equal((false, HeroMotion.Rest), (s.ThrowActive, s.Hero.Motion));                    // 第 41 步
    }

    [Fact]
    public void AfterTheCooldownTheSkillCanBeCastAgainAndEachCastPlacesItsOwnObjects()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (900, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath,
            skillsFactory: data => Load(data, AvatarFixture.WaveSkill, WaveRecipes()));
        Attach(arena.Session, arena.Assets);
        RoomCombatSession s = arena.Session;
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Steps(s, 40);                                                                    // 第 41 步结束
        Assert.False(s.ThrowActive);

        // 冷却 2500 毫秒还没走完:放不出来(冷却)
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Assert.Equal((1, 1, 3), (s.SkillCasts, s.ThrowCasts, s.HeroObjectsSpawned));
        s.Skills.Advance(3000);
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Assert.Equal((2, 2, 6, 0), (s.SkillCasts, s.ThrowCasts, s.HeroObjectsSpawned, s.ThrowPhase));
    }

    // ------------------------------------------------------------------ 房间里:霸体、打断与命中停顿

    /// <summary>会伤人的迷你怪物站在主角右边 (340, 100)(主角朝左,打不到它),每一击 25 点,第一击大约在第 16 步前后到(用 <see cref="RunUntilHit"/> 等);公式常量同 <see cref="HeroSuperArmorTests"/>。</summary>
    private sealed class StrikeArena : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public RoomCombatSession Session { get; }
        public HeroVitals Vitals { get; }
        public TownHero Hero => Session.Hero;

        public StrikeArena(HeroStrikeInfo strike, Func<GameData, HeroSkills> skills, int hpMax = 1000)
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
            var spawn = new RoomMonsterSpawn(1, 0, 0, 340, HeroY, 0, 1, 0, "[fixed]", "[normal]");
            var monster = new MonsterActor(0, spawn, model, 1, 100000, 222.5, 100000, 0, 0);
            monster.PhysicalAttack = 40;
            monster.Strike = strike;

            var plain = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
            var constants = new DamageConstants { PhysicalDamageRate = 1000f, MagicalDamageRate = 1000f, PhysicalReduceRate = 100f, MagicalReduceRate = 100f };
            Session = new RoomCombatSession(Assets, room, new[] { monster }, plain, 1, default, default, skills(new GameData(new PvfScriptSource(Assets.Pvf))), 0, -1f,
                new HitStunTable(10, 330, 1000), Vitals, constants);
            Attach(Session, Assets);
        }

        public void Cast()
        {
            Session.Update(10, 0, 0, false, false, SkillKeys.A);
        }

        /// <summary>每步 10 毫秒推进到条件成立(最多 <paramref name="maxSteps"/> 步),返回成立与否。</summary>
        public bool RunUntil(Func<bool> condition, int maxSteps = 700)
        {
            for (int i = 0; i < maxSteps && !condition(); i++)
            {
                Session.Update(10, 0, 0, false, false, SkillKeys.None);
            }
            return condition();
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    /// <summary>怪物的出招信息:[damage bonus] 25、普通受击;<paramref name="ignore"/> 是 [ignore super armor];<paramref name="hitValue"/> 是 [hit info](命中停顿毫秒)。</summary>
    private static HeroStrikeInfo Strike(bool ignore = false, int hitValue = -1)
    {
        return new HeroStrikeInfo(AvatarFixture.StrikerDamageBonus, 0, false, 0, 0, hitValue, false, -1f, 0, false, 0, false, 300, false, false,
            reactionDamage: true, hitWav: null, reactionNone: false, ignoreSuperArmor: ignore);
    }

    [Fact]
    public void AHitDuringPhaseZeroOfAnArmoredSkillIsAbsorbedAndTheRunGoesOn()
    {
        // 阶段 0 取第 1 个数 500、阶段 1 取第 0 个数 100、霸体开关取第 2 个数(100,非 0):第一击在 200 毫秒以内到,落在阶段 0
        using var arena = new StrikeArena(Strike(), data => LoadHurtable(data, AvatarFixture.HopSkill, HopRecipes(charge: 1, shoot: 0, armor: 2)));
        RoomCombatSession s = arena.Session;
        arena.Cast();
        Assert.True(arena.RunUntil(() => s.HeroHits > 0));

        Assert.Equal((0, true), (s.ThrowPhase, s.ThrowActive));                          // 被打的那一刻还在阶段 0
        Assert.Equal((1, 1, 0, HeroMotion.Skill), (s.HeroHits, s.SuperArmorAbsorbed, arena.Hero.HurtCount, arena.Hero.Motion));
        Assert.Equal(1000 - Damage, arena.Vitals.Hp);                                    // 伤害照扣

        Assert.True(arena.RunUntil(() => !s.ThrowActive));                               // 阶段 0 与阶段 1 照常走完
        Assert.Equal((0, HeroMotion.Rest, 1), (s.ThrowCancels, arena.Hero.Motion, s.HeroHits));
    }

    [Fact]
    public void TheSameHitInPhaseOneInterruptsTheStateAndKeepsTheObjectsAlreadyPlaced()
    {
        // 阶段 0 只有 100 毫秒、阶段 1 有 500 毫秒:第一击(第 16 步前后)落在阶段 1,霸体已关
        using var arena = new StrikeArena(Strike(), data => LoadHurtable(data, AvatarFixture.HopSkill, HopRecipes(charge: 0, shoot: 1, armor: 2)));
        RoomCombatSession s = arena.Session;
        arena.Cast();
        Assert.True(arena.RunUntil(() => s.HeroHits > 0));

        Assert.Equal(1, s.ThrowPhase);
        Assert.Equal((1, 0, 1, HeroMotion.Hurt), (s.HeroHits, s.SuperArmorAbsorbed, arena.Hero.HurtCount, arena.Hero.Motion));
        Assert.True(s.ThrowActive);                                                      // 阶段机下一步才发现主角被打断
        arena.Session.Update(10, 0, 0, false, false, SkillKeys.None);
        Assert.Equal((false, 1, 1), (s.ThrowActive, s.ThrowCancels, s.HeroObjectsSpawned));   // 打断了;已经放出的对象留着,也不会补放

        arena.RunUntil(() => false, 100);
        Assert.Equal(1, s.HeroObjectsSpawned);
    }

    [Fact]
    public void AHeroPutIntoTheDownPoseCancelsTheRunEvenWithoutTheKnockdownMachineRunning()
    {
        // 倒地姿态只由倒地状态机进入;这里直接把主角摆进倒地姿态(状态机没在跑),打断条件里的"主角在倒地姿态"一项仍然要生效
        using var arena = new StrikeArena(Strike(), data => LoadHurtable(data, AvatarFixture.HopSkill, HopRecipes(charge: 1, shoot: 0, armor: 2)));
        RoomCombatSession s = arena.Session;
        arena.Cast();
        Steps(s, 2);
        Assert.Equal((true, 0, false), (s.ThrowActive, s.ThrowCancels, s.HeroDown.Active));   // 还在阶段 0,没有被打,倒地状态机没动

        Assert.True(arena.Hero.BeginDown());
        Assert.Equal((true, false), (arena.Hero.IsDown, arena.Hero.IsHurt));                  // 只有"倒地姿态"一项为真
        Steps(s, 1);
        Assert.Equal((false, 1), (s.ThrowActive, s.ThrowCancels));
    }

    [Fact]
    public void WithoutAnArmorSwitchOrAgainstAnAttackThatIgnoresArmorOrAKillingHitThePhaseZeroHitStillInterrupts()
    {
        // 没有霸体开关(下标 -1):阶段 0 里也没有霸体
        using (var arena = new StrikeArena(Strike(), data => LoadHurtable(data, AvatarFixture.HopSkill, HopRecipes(charge: 1, shoot: 0, armor: -1))))
        {
            arena.Cast();
            Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
            Assert.Equal((0, 0, 1), (arena.Session.ThrowPhase, arena.Session.SuperArmorAbsorbed, arena.Hero.HurtCount));
        }

        // 开关项读出来是 0(下标越过 [static data] 的十个数):同样没有
        using (var arena = new StrikeArena(Strike(), data => LoadHurtable(data, AvatarFixture.HopSkill, HopRecipes(charge: 1, shoot: 0, armor: 40))))
        {
            arena.Cast();
            Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
            Assert.Equal((0, 0, 1), (arena.Session.ThrowPhase, arena.Session.SuperArmorAbsorbed, arena.Hero.HurtCount));
        }

        // 有霸体,但这一击无视霸体(档位 1 挡不住,E-142):照常打断
        using (var arena = new StrikeArena(Strike(ignore: true), data => LoadHurtable(data, AvatarFixture.HopSkill, HopRecipes(charge: 1, shoot: 0, armor: 2))))
        {
            arena.Cast();
            Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
            Assert.Equal((0, 0, 1), (arena.Session.ThrowPhase, arena.Session.SuperArmorAbsorbed, arena.Hero.HurtCount));
        }

        // 有霸体,但这一击把主角打死(生命只有 20 < 25):霸体不挡死亡,主角死了、状态被打断
        using (var arena = new StrikeArena(Strike(), data => LoadHurtable(data, AvatarFixture.HopSkill, HopRecipes(charge: 1, shoot: 0, armor: 2)), hpMax: 20))
        {
            arena.Cast();
            Assert.True(arena.RunUntil(() => arena.Session.HeroHits > 0));
            arena.Session.Update(10, 0, 0, false, false, SkillKeys.None);
            Assert.Equal((true, 0, false, 1), (arena.Session.HeroDead, arena.Session.SuperArmorAbsorbed, arena.Session.ThrowActive, arena.Session.ThrowCancels));
        }
    }

    [Fact]
    public void AHitPauseFreezesTheHeroAndTheRunWithIt()
    {
        // 波系技能当投掷技能:阶段 0 取 250、阶段 1 取 160,有霸体;第一击落在阶段 0 里被挡下。无停顿时第 41 步结束(对照);
        // 这一击的 [hit info] 是 200:挡下的一击也定住主角 200 毫秒((2 − 1.0) × 200),定住的 20 步里阶段机不走,所以第 61 步才结束
        int FinishStep(int hitValue)
        {
            using var arena = new StrikeArena(Strike(hitValue: hitValue), data => LoadHurtable(data, AvatarFixture.WaveSkill, WaveRecipes()));
            RoomCombatSession s = arena.Session;
            arena.Cast();
            int step = 1;
            while (s.ThrowActive && step < 400)
            {
                s.Update(10, 0, 0, false, false, SkillKeys.None);
                step++;
            }
            Assert.Equal((1, 1, 0), (s.HeroHits, s.SuperArmorAbsorbed, arena.Hero.HurtCount));      // 两次都是阶段 0 里被挡下
            return step;
        }

        Assert.Equal(41, FinishStep(-1));
        Assert.Equal(41 + 20, FinishStep(200));
    }
}
