using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 副本房间里的战斗会话(证据 E-069):主角普攻的攻击框与怪物受击框相交判定、同一击对同一怪只算一次、伤害按段的 [damage bonus] 缩放、
/// 击退、死亡、血条/伤害数字/连击的叠加层。数据全是合成的:迷你职业三段普攻(400、300、400 毫秒),
/// 攻击框 —— 第 0 段在第 2 帧(200 到 300 毫秒)、第 1 段在第 1 帧(100 到 200 毫秒)、第 2 段在基础动画的第 1 帧(200 到 400 毫秒)。
/// 主角站在 (300, 100) 朝右时,第 0 段与第 1 段的攻击框(武器图层 swordb)是世界 x ∈ [310, 360]、y ∈ [90, 110]、z ∈ [0, 60];
/// 迷你怪物朝左、站在 (340, 100) 时受击框是 x ∈ [310, 350]、y ∈ [94, 106]、z ∈ [0, 60],两者相交。
/// </summary>
public class RoomCombatTests
{
    private const int HeroX = 300;
    private const int HeroY = 100;

    private static HeroStrikeInfo[] Plain()
    {
        return new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
    }

    /// <summary>一个迷你房间(五块 14 × 6 全可走的瓦片)、主角、若干怪物(不射击主角,只追击与出招)。</summary>
    private sealed class Arena : IDisposable
    {
        private readonly AvatarFixture _fx = new AvatarFixture();

        public DnfAssets Assets { get; }
        public RoomCombatSession Session { get; }
        public TownHero Hero => Session.Hero;

        /// <param name="frozen">为真时主角的可走区域只是脚下一小块:怪物的每一步都会落在区域外,所以所有怪物都原地不动(也推不动)。</param>
        /// <param name="withSkills">为真时按合成的角色脚本([skill] 初始技能表)排出技能快捷槽(要用带跳跃的 <see cref="AvatarFixture.ActionChrPath"/>)。</param>
        public Arena(IReadOnlyList<(int X, int Y)> monsters, HeroStrikeInfo[] strikes = null, bool facingLeft = false, int hp = 100,
            bool frozen = false, int seed = 1, string chr = null, HeroStrikeInfo dash = default, HeroStrikeInfo jump = default, double monsterSpeed = 222.5,
            bool withSkills = false, int weaponStun = 0, HitStunTable stunTable = null, int monsterRecovery = 0, float weaponDelay = -1f)
        {
            Assets = DnfAssets.Open(_fx.Root);
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
            var area = frozen ? new List<WorldRect> { new WorldRect(HeroX - 5, HeroY - 5, 10, 10) } : new List<WorldRect>();
            TownHero hero = TownHero.Create(Assets, chr ?? AvatarFixture.ChrPath, outfit, area, HeroX, HeroY, facingLeft, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));

            MonsterModel model = MonsterModel.Read(Assets, AvatarFixture.BlobMobPath);
            var actors = new List<MonsterActor>();
            for (int i = 0; i < monsters.Count; i++)
            {
                var spawn = new RoomMonsterSpawn(1, 0, 0, monsters[i].X, monsters[i].Y, 0, 1, 0, "[fixed]", "[normal]");
                actors.Add(new MonsterActor(i, spawn, model, 1, hp, monsterSpeed, 1500, 0, monsterRecovery));
            }
            HeroSkills skills = null;
            if (withSkills)
            {
                var data = new GameData(new PvfScriptSource(Assets.Pvf));
                skills = HeroSkills.Create(data, data.GetCharacter(0), 0);
            }
            Session = new RoomCombatSession(Assets, room, actors, strikes ?? Plain(), seed, dash, jump, skills, weaponStun, weaponDelay, stunTable);
        }

        /// <summary>每 10 毫秒一步推进:前 <paramref name="steps"/> 步按 (dirX, dirY) 走,攻击键与跳跃键在 <paramref name="attackAt"/>、<paramref name="jumpAt"/> 那一步按下(从 0 数)。</summary>
        public void Steps(int steps, int dirX = 0, int dirY = 0, int attackAt = -1, int jumpAt = -1)
        {
            for (int i = 0; i < steps; i++)
            {
                Session.Update(10, dirX, dirY, i == attackAt, i == jumpAt);
            }
        }

        /// <summary>每 10 毫秒一步推进 <paramref name="ms"/> 毫秒;<paramref name="pressAt"/> 里的时刻(毫秒,从这次调用起算)那一步按下攻击键。</summary>
        public void Run(int ms, params int[] pressAt)
        {
            for (int t = 0; t < ms; t += 10)
            {
                Session.Update(10, 0, 0, Array.IndexOf(pressAt, t) >= 0);
            }
        }

        public void Dispose()
        {
            Assets.Dispose();
            _fx.Dispose();
        }
    }

    [Fact]
    public void ASwingHitsAMonsterInsideTheStrikeBoxOnceAtTheFrameWithTheAttackBox()
    {
        using var arena = new Arena(new[] { (340, 100) }, new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) });
        RoomCombatSession s = arena.Session;
        long hitAt = -1;
        for (int t = 0; t < 600; t += 10)
        {
            s.Update(10, 0, 0, t == 0);
            if (s.Hits > 0 && hitAt < 0)
            {
                hitAt = s.Hero.MotionMs;
            }
        }
        // 攻击框在武器图层第 0 段动画的第 2 帧:挥击开始后 200 毫秒;整段 400 毫秒挥完,同一击只算一次
        Assert.InRange(hitAt, 200, 209);
        Assert.Equal((1, 1, 0), (s.Hits, s.Combo, s.Kills));
        Assert.InRange(s.TotalDamage, 25, 26);                            // 加成 0:25.3 概率进位
        MonsterActor m = s.Monsters[0];
        Assert.Equal(100 - (int)s.TotalDamage, m.Hp);
        Assert.Same(m, s.Target);
        Assert.Equal(1, s.AliveCount);
    }

    // ------------------------------------------------------------------ 命中停顿(证据 E-077)

    private static HeroStrikeInfo[] Stunning(int firstStageMs)
    {
        return new[] { new HeroStrikeInfo(0, 0, false, hitValue: firstStageMs), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
    }

    /// <summary>按下攻击键后每 10 毫秒一步,推到第一次命中的那一步为止。</summary>
    private static void RunToFirstHit(RoomCombatSession s, int hits = 1)
    {
        s.Update(10, 0, 0, true);
        for (int i = 0; i < 200 && s.Hits < hits; i++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.Equal(hits, s.Hits);
    }

    [Fact]
    public void AHitFreezesTheHeroAndTheMonsterForTheStunTimeThenBothResume()
    {
        using var arena = new Arena(new[] { (340, 100) }, Stunning(100));
        RoomCombatSession s = arena.Session;
        RunToFirstHit(s);
        MonsterActor m = s.Monsters[0];
        Assert.Equal(100.0, s.HeroPauseMs, 9);
        Assert.Equal(100.0, m.PauseMs, 9);
        long motion = s.Hero.MotionMs;
        long phase = m.PhaseMs;
        int frame = s.Hero.CurrentFrame;
        for (int i = 0; i < 10; i++)
        {
            s.Update(10, 0, 0, false);
            Assert.Equal(motion, s.Hero.MotionMs);                       // 主角的动作时间不走
            Assert.Equal(frame, s.Hero.CurrentFrame);
            Assert.Equal(phase, m.PhaseMs);                              // 怪物的硬直计时也不走
        }
        Assert.Equal(0.0, s.HeroPauseMs, 9);
        Assert.Equal(0.0, m.PauseMs, 9);
        s.Update(10, 0, 0, false);
        Assert.Equal(motion + 10, s.Hero.MotionMs);
        Assert.Equal(phase + 10, m.PhaseMs);
        Assert.Equal(1, s.Hits);                                          // 停顿不会让同一击再命中一次
    }

    [Fact]
    public void AHitMakesTheSwingTakeExactlyTheStunTimeLongerAndPopupsKeepTheirOwnClock()
    {
        // 第 0 段整段 400 毫秒;不停顿时 400 毫秒挥完,停 100 毫秒时 500 毫秒挥完
        using var plain = new Arena(new[] { (340, 100) });
        using var stunned = new Arena(new[] { (340, 100) }, Stunning(100));
        long plainEnd = EndOfFirstSwing(plain.Session);
        long stunnedEnd = EndOfFirstSwing(stunned.Session);
        Assert.InRange(stunnedEnd - plainEnd, 100, 110);

        // 伤害数字走自己的墙钟:命中后 100 毫秒的停顿里它照样上飘与计时
        using var arena = new Arena(new[] { (340, 100) }, Stunning(100));
        RunToFirstHit(arena.Session);
        double age0 = arena.Session.Popups[0].AgeMs;
        for (int i = 0; i < 5; i++)
        {
            arena.Session.Update(10, 0, 0, false);
        }
        Assert.Equal(age0 + 50, arena.Session.Popups[0].AgeMs, 9);
    }

    private static long EndOfFirstSwing(RoomCombatSession s)
    {
        s.Update(10, 0, 0, true);
        long t = 10;
        while (s.Hero.IsAttacking && t < 3000)
        {
            s.Update(10, 0, 0, false);
            t += 10;
        }
        Assert.False(s.Hero.IsAttacking);
        return t;
    }

    [Fact]
    public void TwoMonstersHitByTheSameSwingStillFreezeTheHeroOnlyOnceAsLong()
    {
        using var arena = new Arena(new[] { (340, 100), (345, 100) }, Stunning(100));
        RoomCombatSession s = arena.Session;
        RunToFirstHit(s, hits: 2);
        Assert.Equal(100.0, s.HeroPauseMs, 9);                           // 不叠加成 200
        Assert.All(s.Monsters, m => Assert.Equal(100.0, m.PauseMs, 9));
    }

    [Fact]
    public void AnAttackThatSaysMinusOneUsesTheWeaponStunAndAnExplicitNumberOverridesIt()
    {
        // 第 0 段没写命中停顿(-1):取武器的 44;第 0 段写了 25:用 25(不管武器)
        using var weapon = new Arena(new[] { (340, 100) }, weaponStun: 44);
        RunToFirstHit(weapon.Session);
        Assert.Equal(44.0, weapon.Session.HeroPauseMs, 9);
        Assert.Equal(44, weapon.Session.WeaponStunMs);

        using var explicitOverride = new Arena(new[] { (340, 100) }, Stunning(25), weaponStun: 44);
        RunToFirstHit(explicitOverride.Session);
        Assert.Equal(25.0, explicitOverride.Session.HeroPauseMs, 9);

        // 没有武器表(0)、也没写命中停顿:不停
        using var none = new Arena(new[] { (340, 100) });
        RunToFirstHit(none.Session);
        Assert.Equal(0.0, none.Session.HeroPauseMs, 9);
        Assert.Equal(0.0, none.Session.Monsters[0].PauseMs, 9);

        // 写了 0:不停,也不回落到武器表(只有 -1 才回落)
        using var zero = new Arena(new[] { (340, 100) }, Stunning(0), weaponStun: 44);
        RunToFirstHit(zero.Session);
        Assert.Equal(0.0, zero.Session.HeroPauseMs, 9);
    }

    // ------------------------------------------------------------------ 受击硬直公式接进战斗(证据 E-080;表、倍率与受击恢复全是合成数值)

    private static readonly HitStunTable SyntheticStunTable = new HitStunTable(10, 330, 1000);

    [Fact]
    public void WithAStunTableAHitHurtsForTheFormulaTimeScaledByTheDelayRate()
    {
        // 受击恢复 500:表给出 170;武器的命中延迟倍率 0.5、攻击信息没写 → 倍率 0.5 → 85
        using var weapon = new Arena(new[] { (340, 100) }, stunTable: SyntheticStunTable, monsterRecovery: 500, weaponDelay: 0.5f);
        RunToFirstHit(weapon.Session);
        MonsterActor m = weapon.Session.Monsters[0];
        Assert.True(m.HurtFromFormula);
        Assert.Equal(85.0, m.HurtSpanMs);
        Assert.Equal(0.5f, weapon.Session.WeaponDelayRate);

        // 没有武器表、攻击信息也没写:倍率 1 → 170
        using var plain = new Arena(new[] { (340, 100) }, stunTable: SyntheticStunTable, monsterRecovery: 500);
        RunToFirstHit(plain.Session);
        Assert.Equal(170.0, plain.Session.Monsters[0].HurtSpanMs);

        // 攻击信息写了自己的倍率 2.0:用它(不管武器)
        var own = new[] { new HeroStrikeInfo(0, 0, false, hitScale: 2.0f), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
        using var scaled = new Arena(new[] { (340, 100) }, own, stunTable: SyntheticStunTable, monsterRecovery: 500, weaponDelay: 0.5f);
        RunToFirstHit(scaled.Session);
        Assert.Equal(340.0, scaled.Session.Monsters[0].HurtSpanMs);

        // 写了 [is use weapon hit delay rate]:武器 0.5 × 文件 2.0 = 1.0 → 170
        var product = new[] { new HeroStrikeInfo(0, 0, false, useWeaponHitDelayRate: true, hitScale: 2.0f), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
        using var both = new Arena(new[] { (340, 100) }, product, stunTable: SyntheticStunTable, monsterRecovery: 500, weaponDelay: 0.5f);
        RunToFirstHit(both.Session);
        Assert.Equal(170.0, both.Session.Monsters[0].HurtSpanMs);
    }

    [Fact]
    public void ForcedStunMagicBurnAndKnockBackTypesReachTheMonster()
    {
        HeroStrikeInfo[] Only(HeroStrikeInfo first) => new[] { first, new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };

        // [force hit stun time] 直接当硬直
        using var forced = new Arena(new[] { (340, 100) }, Only(new HeroStrikeInfo(0, 0, false, forceHitStunMs: 77)), stunTable: SyntheticStunTable, monsterRecovery: 500);
        RunToFirstHit(forced.Session);
        Assert.Equal(77.0, forced.Session.Monsters[0].HurtSpanMs);

        // 魔法攻击的倍率再乘 0.7:170 的硬直变短,但不会短到 100 以下
        using var magic = new Arena(new[] { (340, 100) }, Only(new HeroStrikeInfo(0, 0, false, magical: true)), stunTable: SyntheticStunTable, monsterRecovery: 500);
        RunToFirstHit(magic.Session);
        Assert.InRange(magic.Session.Monsters[0].HurtSpanMs, 118.0, 119.0);

        // [knuck back] 类型 1:硬直 = int(rate × 333),不看表
        using var strong = new Arena(new[] { (340, 100) }, Only(new HeroStrikeInfo(0, 0, false, knockBackType: 1)), stunTable: SyntheticStunTable, monsterRecovery: 500);
        RunToFirstHit(strong.Session);
        Assert.Equal(333.0, strong.Session.Monsters[0].HurtSpanMs);

        // 命中模式灼烧:受击但不击退
        using var burn = new Arena(new[] { (340, 100) }, Only(new HeroStrikeInfo(0, 40, false, noKnockBack: true)), stunTable: SyntheticStunTable, monsterRecovery: 0);
        double before = burn.Session.Monsters[0].X;
        RunToFirstHit(burn.Session);
        burn.Run(300);
        Assert.Equal(before, burn.Session.Monsters[0].X);
    }

    [Fact]
    public void WithoutAStunTableTheHurtStaysOnThePlaceholderAndWithOneThePushAsideIsIgnoredForAHurtHit()
    {
        // 没有表:占位硬直,[push aside] 照旧当击退
        using var none = new Arena(new[] { (340, 100) }, new[] { new HeroStrikeInfo(0, 40, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) }, monsterRecovery: 500);
        RunToFirstHit(none.Session);
        Assert.False(none.Session.Monsters[0].HurtFromFormula);
        Assert.Equal(MonsterTuning.HurtHoldMs, none.Session.Monsters[0].HurtSpanMs);

        // 有表:同一击的击退是公式的约 30 像素,不是 [push aside] 的 40
        using var formula = new Arena(new[] { (340, 100) }, new[] { new HeroStrikeInfo(0, 40, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) },
            stunTable: SyntheticStunTable, monsterRecovery: 0);
        double start = formula.Session.Monsters[0].X;
        RunToFirstHit(formula.Session);
        formula.Run(400);
        double moved = Math.Abs(formula.Session.Monsters[0].X - start);
        Assert.InRange(moved, 25.0, 31.0);
    }

    [Fact]
    public void StrikeInfoRemembersTheStunFieldsAndUnsetMeansMinusOne()
    {
        Assert.Equal(-1f, default(HeroStrikeInfo).HitScale);
        Assert.Equal(-1f, new HeroStrikeInfo(0, 0, false).HitScale);
        var info = new HeroStrikeInfo(5, 6, true, 7, 8, 9, true, 0.25f, 3, true, 11, true);
        Assert.Equal((0.25f, 3, true, 11, true), (info.HitScale, info.KnockBackType, info.NoKnockBack, info.ForceHitStunMs, info.Magical));
        HeroStrikeInfo skill = info.WithSkillDamage(100, 200);
        Assert.Equal((100, 200, 9, true), (skill.SkillRatePercent, skill.SkillFixed, skill.HitValue, skill.UseWeaponHitDelayRate));
        Assert.Equal((0.25f, 3, true, 11, true), (skill.HitScale, skill.KnockBackType, skill.NoKnockBack, skill.ForceHitStunMs, skill.Magical));
    }

    [Fact]
    public void KeysPressedWhileTheHeroIsFrozenTakeEffectWhenTheStopEnds()
    {
        using var arena = new Arena(new[] { (340, 100) }, Stunning(100));
        RoomCombatSession s = arena.Session;
        RunToFirstHit(s);
        long swing = s.Hero.SwingNumber;
        // 停顿开始后 20 毫秒按一下攻击键(排队第二段):停顿里不起效,主角还在第 0 段的同一帧
        s.Update(10, 0, 0, false);
        s.Update(10, 0, 0, true);
        Assert.Equal(swing, s.Hero.SwingNumber);
        Assert.Equal(0, s.Hero.AttackStage);
        for (int i = 0; i < 40; i++)                                      // 80 毫秒停顿 + 剩下 200 毫秒挥完第 0 段,再多走一点
        {
            s.Update(10, 0, 0, false);
        }
        // 第 0 段挥完后,排队的这一击接上第 1 段(没有被停顿吞掉)
        Assert.Equal(swing + 1, s.Hero.SwingNumber);
        Assert.Equal(1, s.Hero.AttackStage);
    }

    [Fact]
    public void ASkillEffectFreezesWithTheHeroDuringTheStopWhileTheCooldownKeepsRunning()
    {
        // 鬼斩的两层特效属于主角:主角被定住时它们的播放时间也不走(推断:客户端的暂停会逐个冻结子动画);技能冷却是墙钟,照常走。
        // 夹具里鬼斩的攻击信息没有 [hit info],所以停顿取武器的 80
        using var arena = new Arena(new[] { (340, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, withSkills: true, weaponStun: 80);
        RoomCombatSession s = arena.Session;
        HeroSkillSlot hard = s.Skills.SlotOf(SkillKeys.A);
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        for (int i = 0; i < 100 && s.Hits == 0; i++)
        {
            s.Update(10, 0, 0, false, false, SkillKeys.None);
        }
        Assert.Equal(1, s.Hits);
        Assert.Equal(80.0, s.HeroPauseMs, 9);
        Assert.Equal(2, s.Effects.Count);

        double[] ages = s.Effects.Select(e => e.AgeMs).ToArray();
        double cooldown = hard.CooldownLeftMs;
        long motion = s.Hero.MotionMs;
        for (int i = 0; i < 8; i++)
        {
            s.Update(10, 0, 0, false, false, SkillKeys.None);
            Assert.Equal(ages, s.Effects.Select(e => e.AgeMs).ToArray());
            Assert.Equal(motion, s.Hero.MotionMs);
            Assert.Equal(cooldown - 10 * (i + 1), hard.CooldownLeftMs, 6);
        }
        s.Update(10, 0, 0, false, false, SkillKeys.None);
        Assert.Equal(ages.Select(a => a + 10).ToArray(), s.Effects.Select(e => e.AgeMs).ToArray());
        Assert.Equal(motion + 10, s.Hero.MotionMs);
    }

    [Fact]
    public void ALongFrameCannotSkipTheHitBecauseTheUpdateIsSplitIntoSmallSteps()
    {
        using var arena = new Arena(new[] { (340, 100) });
        RoomCombatSession s = arena.Session;
        s.Update(16, 0, 0, true);
        s.Update(600, 0, 0, false);                                       // 一帧 600 毫秒:挥击在这一帧里开始与结束
        Assert.Equal(1, s.Hits);
        Assert.False(s.Hero.IsAttacking);

        // 按键只在第一小步里生效(边沿);零长度的一帧也要处理按键
        using var other = new Arena(new[] { (340, 100) });
        other.Session.Update(0, 0, 0, true);
        Assert.True(other.Hero.IsAttacking);
        Assert.Equal(1L, other.Hero.SwingNumber);
        other.Session.Update(-5, 0, 0, false);                            // 负的时间当 0
        Assert.True(other.Hero.IsAttacking);
    }

    [Fact]
    public void AMonsterOutsideTheStrikeBoxIsNotHitInAnyAxis()
    {
        // 第一个太远(水平);第二个在竖直方向的视野之外也没有被攻击框覆盖(y = 270 离主角 170 > 视野 160,原地不动);第三个在主角身后
        using var arena = new Arena(new[] { (700, 100), (340, 270), (240, 100) });
        arena.Run(600, 0);
        RoomCombatSession s = arena.Session;
        Assert.Equal((0, 0, 0), (s.Hits, s.Combo, s.Kills));
        Assert.Empty(s.Popups);
        Assert.Null(s.Target);
        Assert.All(s.Monsters, m => Assert.Equal(100, m.Hp));
    }

    [Fact]
    public void FacingLeftMirrorsTheStrikeBoxAboutTheHero()
    {
        // 主角朝左:攻击框是世界 x ∈ [240, 290]。(260, 100) 的怪物(主角在它右边,它朝右,受击框 x ∈ [250, 290])被打中;(340, 100) 的没有
        using var arena = new Arena(new[] { (260, 100), (340, 100) }, facingLeft: true);
        arena.Run(600, 0);
        RoomCombatSession s = arena.Session;
        Assert.Equal(1, s.Hits);
        Assert.True(s.Monsters[0].Hp < 100);
        Assert.Equal(100, s.Monsters[1].Hp);
    }

    [Fact]
    public void AnAttackBoxFromAnyWeaponLayerCountsBecauseTheLayersAreUnioned()
    {
        // 这是一条假设:武器各图层的攻击框取并集(原版里各层是否都带框没有核对,证据 E-069)。
        // 夹具里 swordc 层第 0 段第 2 帧有一个小框 (x ∈ [310, 320], y ∈ [130, 140], z ∈ [0, 10]),swordb 层的框 y ∈ [90, 110] 碰不到 y = 135 的怪物;
        // 怪物原地不动(主角的可走区域只有脚下一小块),所以只可能是 swordc 的框打中
        using var arena = new Arena(new[] { (330, 135) }, frozen: true);
        arena.Run(600, 0);
        Assert.Equal(1, arena.Session.Hits);
    }

    [Fact]
    public void HeroStrikeBoxesCombineTheBaseAnimationAndEveryWeaponLayerAndMirrorWithTheFacing()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        TownHero hero = TownHero.Create(assets, AvatarFixture.ChrPath, outfit, new List<WorldRect>(), HeroX, HeroY, false, TownHero.DungeonRestTag);
        var boxes = new HeroStrikeBoxes(assets);

        Assert.Empty(boxes.At(hero, 2));                                  // 不在挥击:没有框
        Assert.Empty(boxes.At(null, 2));

        hero.PlaceAttack(HeroX, HeroY, false, 0, 250);                    // 第 0 段第 2 帧:swordb 与 swordc 各一个框
        Assert.Equal(2, hero.CurrentFrame);
        List<Aabb> stage0 = boxes.At(hero, hero.CurrentFrame);
        Assert.Equal(new[] { new Aabb(310, 360, 90, 110, 0, 60), new Aabb(310, 320, 130, 140, 0, 10) }, stage0.ToArray());
        Assert.Empty(boxes.At(hero, 0));                                  // 别的帧没有框
        Assert.Empty(boxes.At(hero, 3));
        Assert.Empty(boxes.At(hero, -1));
        Assert.Empty(boxes.At(hero, 99));

        hero.PlaceAttack(HeroX, HeroY, true, 0, 250);                     // 朝左:x 镜像
        List<Aabb> mirrored = boxes.At(hero, 2);
        Assert.Equal(new[] { new Aabb(240, 290, 90, 110, 0, 60), new Aabb(HeroX - 20, HeroX - 10, 130, 140, 0, 10) }, mirrored.ToArray());

        // 第 2 段:武器图层没有这一段的动画,框来自基础动画自己(第 1 帧)
        hero.PlaceAttack(HeroX, HeroY, false, 2, 250);
        Assert.Equal(1, hero.CurrentFrame);
        Assert.Equal(new[] { new Aabb(320, 360, 90, 110, 0, 50) }, boxes.At(hero, 1).ToArray());
    }

    [Fact]
    public void DamageFollowsTheStageBonusAndTheSecondSwingKillsAWeakMonster()
    {
        var strikes = new[] { new HeroStrikeInfo(-15, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(100, 0, true) };
        using var arena = new Arena(new[] { (340, 100) }, strikes, hp: 30);
        RoomCombatSession s = arena.Session;
        var damages = new List<int>();
        var stagesAtHit = new List<int>();
        for (int t = 0; t < 900; t += 10)
        {
            int before = s.Hits;
            // 第 0 毫秒起手,100 毫秒时再按一次(排队第二段)
            s.Update(10, 0, 0, t == 0 || t == 100);
            if (s.Hits > before)
            {
                damages.Add((int)(s.TotalDamage - damages.Sum()));
                stagesAtHit.Add(s.Hero.AttackStage);
            }
        }
        Assert.Equal(2, damages.Count);
        Assert.InRange(damages[0], 21, 22);                               // 25.3 × 0.85 = 21.5
        Assert.InRange(damages[1], 25, 26);                               // 25.3 × 1.0
        Assert.Equal((2, 2, 1), (s.Hits, s.Combo, s.Kills));
        Assert.Equal(0, s.AliveCount);
        Assert.Equal(0, s.Monsters[0].Hp);
        Assert.Equal(MonsterPhase.Dying, s.Monsters[0].Phase);
        // 第一击发生在第 0 段,第二击发生在第 1 段(攻击框在第 1 帧,起点是第一段结束后的 100 毫秒);按各段的加成缩放伤害
        Assert.Equal(new[] { 0, 1 }, stagesAtHit.ToArray());
    }

    [Fact]
    public void ThePushFollowsTheHeroFacingAndTheKnockDownFlagSelectsTheFallingAnimation()
    {
        // 倒地按客户端的开始处理算(E-083):[push aside] 是水平初速的来源,[lift up] 给腾空。体重 0、类型 1:
        // 推 60 → 缩放 121 → 一半 60 → 再 × [0.8, 1.2];抬升 300 → 缩放 606 → 高度 122,腾空约 0.8 秒,落地后再颠一下:一共被推出去大约 45 到 80 像素
        var strikes = new[] { new HeroStrikeInfo(0, 60, true, liftUp: 300), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
        using var arena = new Arena(new[] { (340, 100) }, strikes);
        RoomCombatSession s = arena.Session;
        arena.Run(300, 0);                                                // 第 0 段 200 毫秒命中
        MonsterActor m = s.Monsters[0];
        Assert.Equal(1, s.Hits);
        Assert.Equal(AvatarFixture.BlobAnimationDir + "/down.ani", m.CurrentAnimationPath);
        Assert.True(m.IsKnockedDown);
        Assert.True(m.FacingLeft);                                        // 攻击方在怪物左边:怪物转向攻击方(原本也朝左)
        for (int i = 0; i < 400 && m.IsKnockedDown; i++)
        {
            arena.Run(10);
        }
        Assert.False(m.IsKnockedDown);
        Assert.InRange(m.X - 340, 35, 90);                                // 朝右的挥击把怪物往右推

        using var left = new Arena(new[] { (260, 100) }, strikes, facingLeft: true);
        left.Run(600, 0);
        Assert.Equal(1, left.Session.Hits);
        MonsterActor lm = left.Session.Monsters[0];
        Assert.False(lm.FacingLeft);                                      // 攻击方在怪物右边:怪物转向攻击方
        for (int i = 0; i < 400 && lm.IsKnockedDown; i++)
        {
            left.Run(10);
        }
        Assert.InRange(260 - lm.X, 35, 90);
    }

    [Fact]
    public void PopupsTheTargetBarAndTheComboExpireOnTheirOwnClocks()
    {
        using var arena = new Arena(new[] { (340, 100) });
        RoomCombatSession s = arena.Session;
        arena.Run(250, 0);                                                // 命中发生在 200 毫秒
        Assert.Equal(1, s.Hits);
        HitPopup popup = Assert.Single(s.Popups);
        Assert.True(int.TryParse(popup.Text, out int shown));
        Assert.Equal((int)s.TotalDamage, shown);
        Assert.Equal(60, popup.Z);                                        // 浮在怪物头顶(受击框顶 = 60)
        Assert.Equal((340.0, 100.0), (popup.WorldX, popup.WorldY));
        Assert.NotNull(s.Target);

        arena.Run(800);
        Assert.Empty(s.Popups);                                           // 800 毫秒后数字消失
        Assert.NotNull(s.Target);
        Assert.Equal(1, s.Combo);
        arena.Run(1800);                                                  // 距命中 2650 毫秒 > 2600:连击清零
        Assert.Equal(0, s.Combo);
        Assert.NotNull(s.Target);
        arena.Run(1000);                                                  // 距命中 3650 毫秒 > 3500:血条消失
        Assert.Null(s.Target);
        Assert.Equal(1, s.Hits);                                          // 累计命中数不清零
    }

    [Fact]
    public void BuildAddsTheMonstersAndTheOverlayAboveTheScene()
    {
        using var arena = new Arena(new[] { (340, 100), (700, 100) });
        RoomCombatSession s = arena.Session;
        List<SceneSprite> before = s.Build(0);
        Assert.Contains(before, x => x.NpcId == TownHero.ObjectId);
        Assert.Contains(before, x => x.NpcId == MonsterSprites.ObjectIdBase);
        Assert.Contains(before, x => x.NpcId == MonsterSprites.ObjectIdBase + 1);
        Assert.DoesNotContain(before, x => x.NpcId == -3);                // 没有命中:没有血条、数字、连击

        arena.Run(250, 0);
        List<SceneSprite> after = s.Build(0);
        List<SceneSprite> overlay = after.Where(x => x.LayerRank == OverlayArt.UiRank).ToList();
        Assert.Equal(new[] { "target-bar", "target-label", "damage" }, overlay.Select(x => x.AnimationPath).ToArray());
        Assert.All(overlay, x => Assert.Equal(-3, x.NpcId));
        // 叠加层排在所有地图物体、怪物与主角之后
        int firstOverlay = after.FindIndex(x => x.LayerRank == OverlayArt.UiRank);
        Assert.True(after.Skip(firstOverlay).All(x => x.LayerRank == OverlayArt.UiRank));

        // 血条:112 × 12,填充部分是亮红、其余是暗红;怪物 100 → (100 − 伤害)
        SceneSprite bar = overlay[0];
        Assert.Equal((RoomCombatSession.TargetBarWidth, RoomCombatSession.TargetBarHeight), (bar.Frame.Width, bar.Frame.Height));
        Assert.Equal((RoomCombatSession.TargetBarX, RoomCombatSession.TargetBarY), (bar.ScreenX, bar.ScreenY));
        MonsterActor m = s.Monsters[0];
        int filled = (int)Math.Round((double)(RoomCombatSession.TargetBarWidth - 2) * m.Hp / m.HpMax);
        Assert.Equal(0xE2, bar.Frame.Rgba[(1 * bar.Frame.Width + filled) * 4]);               // 最后一个填充像素(x = filled)
        Assert.Equal(0x3A, bar.Frame.Rgba[(1 * bar.Frame.Width + filled + 1) * 4]);           // 第一个空像素

        // 第二击命中(排队的第二段)后连击数大于 1,出现连击叠加
        arena.Run(500, 0);
        Assert.Equal(2, s.Combo);
        Assert.Contains(s.Build(0), x => x.AnimationPath == "combo");
    }

    [Fact]
    public void AMonsterThatHasVanishedLeavesTheSceneAndIsNotHitAgain()
    {
        using var arena = new Arena(new[] { (340, 100) }, new[] { new HeroStrikeInfo(2000, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) },
            hp: 10);
        RoomCombatSession s = arena.Session;
        arena.Run(300, 0);                                                // 加成 2000%:一击秒杀
        Assert.Equal((1, 1), (s.Hits, s.Kills));
        MonsterActor m = s.Monsters[0];
        Assert.Equal(MonsterPhase.Dying, m.Phase);
        Assert.Contains(s.Build(0), x => x.NpcId == MonsterSprites.ObjectIdBase);

        arena.Run(600);
        Assert.True(m.IsGone);
        Assert.DoesNotContain(s.Build(0), x => x.NpcId == MonsterSprites.ObjectIdBase);
        Assert.Equal(0, s.AliveCount);

        arena.Run(600, 0);                                                // 再挥一击:没有目标
        Assert.Equal((1, 1), (s.Hits, s.Kills));
    }

    [Fact]
    public void TheSameSeedGivesTheSameDamageAndDifferentSeedsEventuallyDiffer()
    {
        long Total(int seed)
        {
            using var arena = new Arena(new[] { (340, 100) }, seed: seed);
            arena.Run(600, 0, 100);
            return arena.Session.TotalDamage;
        }
        Assert.Equal(Total(7), Total(7));
        var seen = new HashSet<long>();
        for (int seed = 1; seed <= 40; seed++)
        {
            seen.Add(Total(seed));
        }
        Assert.True(seen.Count >= 2);                                     // 小数部分按概率进位:不同种子会给出不同的和
    }

    [Fact]
    public void MonsterWalkSpeedScalesTheHeroSpeedByTheRatioOfTheMoveSpeedStats()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        TownHero hero = TownHero.Create(assets, AvatarFixture.ChrPath, outfit, new List<WorldRect>(), 0, 0, false, TownHero.DungeonRestTag);
        Assert.Equal(AvatarFixture.MoveSpeedStat, hero.MoveSpeedStat);

        double heroSpeed = hero.WalkSpeedX;
        Assert.Equal(heroSpeed, RoomCombatSession.WalkSpeed(hero, AvatarFixture.MoveSpeedStat), 9);          // 同样的数 → 同速
        Assert.Equal(heroSpeed / 2, RoomCombatSession.WalkSpeed(hero, AvatarFixture.MoveSpeedStat / 2), 9);   // 一半 → 一半速度
        Assert.Equal(heroSpeed * 3, RoomCombatSession.WalkSpeed(hero, AvatarFixture.MoveSpeedStat * 3), 9);
        Assert.Equal(heroSpeed, RoomCombatSession.WalkSpeed(hero, 0), 9);                                     // 怪物没有数 → 与主角同速

        TownHero noStat = TownHero.Create(assets, AvatarFixture.NoAttackChrPath, outfit, new List<WorldRect>(), 0, 0, false, TownHero.DungeonRestTag);
        Assert.Equal(0, noStat.MoveSpeedStat);
        Assert.Equal(noStat.WalkSpeedX, RoomCombatSession.WalkSpeed(noStat, 123), 9);                         // 主角没有数 → 与主角同速
    }

    [Fact]
    public void ADashAttackStrikesWithItsOwnBoxAndItsOwnStrikeInfoWhileTheChainStaysUntouched()
    {
        // 奔跑攻击的攻击信息:加成 +100%(伤害 ≈ 50.6)、推 40、抬升 300、倒地;连招各段的加成都是 0
        var dash = new HeroStrikeInfo(100, 40, true, liftUp: 300);
        using var arena = new Arena(new[] { (400, 100) }, chr: AvatarFixture.ActionChrPath, dash: dash, monsterSpeed: 0);   // 怪物原地不动
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        hero.Update(50, 1, 0);                                           // 双击右进入奔跑(这三步主角自己走到 x ≈ 323)
        hero.Update(50, 0, 0);
        hero.Update(50, 1, 0);
        Assert.Equal(HeroMotion.Dash, hero.Motion);

        s.Update(10, 1, 0, true, false);                                 // 奔跑中按攻击键:奔跑攻击
        Assert.Equal(HeroAttackKind.Dash, hero.AttackKind);
        for (int i = 0; i < 40 && hero.IsAttacking; i++)
        {
            s.Update(10, 0, 0, false, false);
        }
        Assert.False(hero.IsAttacking);
        Assert.Equal((1, 0), (s.Hits, s.Kills));
        Assert.InRange(s.TotalDamage, 50, 51);                           // 25.3 × (100 + 100) / 100
        MonsterActor m = s.Monsters[0];
        Assert.Equal(AvatarFixture.BlobAnimationDir + "/down.ani", m.CurrentAnimationPath);   // 倒地
        Assert.True(m.IsKnockedDown);
        for (int i = 0; i < 400 && m.IsKnockedDown; i++)
        {
            arena.Steps(1);
        }
        Assert.False(m.IsKnockedDown);
        Assert.InRange(m.X - 400, 20, 70);                               // 朝右的奔跑攻击把怪物往右推(水平初速 [32, 48] 像素/秒,腾空约 0.8 秒加颠一下)

        // 同一份数据里站着出的第一段普攻不受奔跑攻击信息的影响
        using var walker = new Arena(new[] { (340, 100) }, chr: AvatarFixture.ActionChrPath, dash: dash, monsterSpeed: 0);
        walker.Steps(60, attackAt: 0);
        Assert.Equal(1, walker.Session.Hits);
        Assert.InRange(walker.Session.TotalDamage, 25, 26);
    }

    [Fact]
    public void AJumpAttackFollowsTheHeroHeightAndUsesTheJumpStrikeInfo()
    {
        // 空中攻击的攻击信息:加成 −50%(伤害 ≈ 12.65);攻击框 z ∈ [Z, Z + 50],怪物受击框 z ∈ [0, 60]
        var jump = new HeroStrikeInfo(-50, 0, false);

        // 起跳要先蹲 100 毫秒(第 0~9 步),离地后第一步(第 10 步)出招:挥击的第 1 帧在出招后 100 毫秒,
        // 那时腾空 100 毫秒、离地 ≈ 51 像素,框 z ∈ [51, 101] 与 [0, 60] 相交
        using var low = new Arena(new[] { (340, 100) }, chr: AvatarFixture.ActionChrPath, jump: jump, monsterSpeed: 0);
        low.Steps(60, jumpAt: 0, attackAt: 10);
        RoomCombatSession s = low.Session;
        Assert.Equal(1, s.Hits);
        Assert.InRange(s.TotalDamage, 12, 13);

        // 蹲姿期间按攻击键没有用(还没离地)
        using var crouching = new Arena(new[] { (340, 100) }, chr: AvatarFixture.ActionChrPath, jump: jump, monsterSpeed: 0);
        crouching.Steps(5, jumpAt: 0, attackAt: 3);
        Assert.Equal((HeroMotion.Jump, 0L), (crouching.Hero.Motion, crouching.Hero.SwingNumber));

        // 在上升途中出招(第 40 步,腾空 310 毫秒):挥击第 1 帧时腾空 400 毫秒、离地 ≈ 122 像素,框 z ∈ [122, 172] 够不着 [0, 60]
        using var high = new Arena(new[] { (340, 100) }, chr: AvatarFixture.ActionChrPath, jump: jump, monsterSpeed: 0);
        high.Steps(70, jumpAt: 0, attackAt: 40);
        Assert.Equal(HeroMotion.Jump, high.Hero.Motion);                  // 已经挥完回到跳跃动作
        Assert.Equal((0, 1L), (high.Session.Hits, high.Hero.SwingNumber));
    }

    [Fact]
    public void StrikeBoxesOfAnAirAttackAreLiftedByTheHeroHeight()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        TownHero hero = TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), HeroX, HeroY, false, TownHero.DungeonRestTag);
        var boxes = new HeroStrikeBoxes(assets);

        hero.Update(10, 0, 0, false, true);
        for (int i = 0; i < 36; i++)
        {
            hero.Update(10, 0, 0);                                       // 第 37 步:蹲 0.1 秒 + 腾空 0.27 秒
        }
        hero.Update(10, 0, 0, true);                                     // 第 38 步出招:腾空 0.28 秒,离地 v0 × 0.28 − g/2 × 0.28² ≈ 108 像素
        Assert.Equal(HeroMotion.JumpAttack, hero.Motion);
        Assert.Empty(boxes.At(hero, 0));                                 // 第 0 帧没有框
        int wz = (int)Math.Round(hero.Z);
        Assert.Equal((int)Math.Round(hero.JumpTakeoffSpeed * 0.28 - 0.5 * hero.JumpGravity * 0.28 * 0.28), wz);
        Assert.InRange(wz, 100, 115);
        List<Aabb> list = boxes.At(hero, 1);
        Assert.Equal(new[] { new Aabb(HeroX + 20, HeroX + 60, HeroY - 10, HeroY + 10, wz, wz + 50) }, list.ToArray());
    }

    // ---- 技能键(证据 E-076) ----

    /// <summary>每 10 毫秒一步推进 <paramref name="ms"/> 毫秒;<paramref name="pressAt"/> 里的 (时刻, 键) 在那一步按下技能键。</summary>
    private static void RunSkills(RoomCombatSession s, int ms, params (int At, SkillKeys Keys)[] pressAt)
    {
        for (int t = 0; t < ms; t += 10)
        {
            SkillKeys keys = SkillKeys.None;
            foreach ((int at, SkillKeys k) in pressAt)
            {
                if (at == t)
                {
                    keys |= k;
                }
            }
            s.Update(10, 0, 0, false, false, keys);
        }
    }

    [Fact]
    public void TheUpperSlashKeyHitsWithTheThirdStagesBoxAndDamageBonusAndItsCooldownBlocksTheNextPress()
    {
        // 第三段(下标 2)的 [damage bonus] 是 +100%:25.3 × 2 = 50.6;普攻前两段是 0
        var strikes = new[] { new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(100, 0, false) };
        using var arena = new Arena(new[] { (340, 100) }, strikes, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, withSkills: true);
        RoomCombatSession s = arena.Session;
        HeroSkillSlot upper = s.Skills.SlotOf(SkillKeys.S);

        long hitAt = -1;
        for (int t = 0; t < 600; t += 10)
        {
            s.Update(10, 0, 0, false, false, t == 0 ? SkillKeys.S : SkillKeys.None);
            if (s.Hits > 0 && hitAt < 0)
            {
                hitAt = s.Hero.MotionMs;
            }
        }
        // 第三段的基础动画第 1 帧(200 到 400 毫秒)带攻击框;整段 400 毫秒挥完,同一击只算一次
        Assert.InRange(hitAt, 200, 209);
        Assert.Equal((1, 0), (s.Hits, s.Kills));
        Assert.InRange(s.TotalDamage, 50, 51);
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);

        // 冷却 1778 毫秒:从按键起算 1000 毫秒时再按没有反应,过了 1778 毫秒才能再放
        Assert.False(upper.Ready);
        int hits = s.Hits;
        RunSkills(s, 400, (0, SkillKeys.S));                             // 此时约 1000 毫秒:冷却中,按了没有反应
        Assert.Equal((hits, HeroMotion.Rest), (s.Hits, s.Hero.Motion));
        RunSkills(s, 800);                                               // 此时约 1800 毫秒:冷却好了
        Assert.True(upper.Ready);
        RunSkills(s, 600, (0, SkillKeys.S));                             // 再放一次,再命中一次
        Assert.Equal(2, s.Hits);
        Assert.Equal(HeroMotion.Rest, s.Hero.Motion);
    }

    [Fact]
    public void ASkillKeyCancelsTheAttackAndJumpPressedInTheSameStep()
    {
        using var arena = new Arena(new[] { (340, 100) }, chr: AvatarFixture.ActionChrPath, withSkills: true);
        RoomCombatSession s = arena.Session;
        s.Update(10, 0, 0, true, true, SkillKeys.S);                      // 同一步按下 X、C、S:只放上挑
        Assert.Equal((HeroMotion.Skill, AvatarFixture.UpperSlashSkill, false), (s.Hero.Motion, s.Hero.SkillId, s.Hero.IsAirborne));

        // 技能放不出来(冷却中)时,这一步的攻击键照常生效
        using var other = new Arena(new[] { (340, 100) }, chr: AvatarFixture.ActionChrPath, withSkills: true);
        RunSkills(other.Session, 500, (0, SkillKeys.S));                  // 放一次上挑(400 毫秒挥完),冷却 1778 毫秒
        Assert.Equal(HeroMotion.Rest, other.Session.Hero.Motion);
        other.Session.Update(10, 0, 0, true, false, SkillKeys.S);
        Assert.Equal(HeroMotion.Attack, other.Session.Hero.Motion);
    }

    [Fact]
    public void TheHardAttackKeyPlaysItsMotionInPlaceAndHitsOnceWithItsOwnBoxAndSkillDamage()
    {
        // 动作 600 毫秒,第 2、3 帧(200 到 400 毫秒)各有一个攻击框;两帧的框都碰到怪物,同一击仍只算一次。
        // 普攻的 [damage bonus] 故意给成 +2000%:如果误用普攻的数,伤害会是几百
        var strikes = new[] { new HeroStrikeInfo(2000, 0, false), new HeroStrikeInfo(2000, 0, false), new HeroStrikeInfo(2000, 0, false) };
        using var arena = new Arena(new[] { (340, 100) }, strikes, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, withSkills: true);
        RoomCombatSession s = arena.Session;
        HeroSkillSlot hard = s.Skills.SlotOf(SkillKeys.A);

        long hitAt = -1;
        for (int t = 0; t < 700; t += 10)
        {
            s.Update(10, 0, 0, false, false, t == 0 ? SkillKeys.A : SkillKeys.None);
            if (s.Hits > 0 && hitAt < 0)
            {
                hitAt = s.Hero.MotionMs;
            }
        }
        Assert.InRange(hitAt, 200, 209);
        Assert.Equal((1, 0), (s.Hits, s.Kills));
        // 伤害 = 技能基础值 × 百分比 / 100 + 固定值(一级:200%、40),小数部分按概率进位
        double expected = PlaceholderDamage.SkillBaseHit * AvatarFixture.HardAttackRate / 100.0 + AvatarFixture.HardAttackFixed;
        Assert.InRange(s.TotalDamage, (long)Math.Floor(expected), (long)Math.Floor(expected) + 1);
        Assert.Equal((HeroMotion.Rest, (double)HeroX), (s.Hero.Motion, s.Hero.X));
        Assert.InRange(hard.CooldownLeftMs, AvatarFixture.HardAttackCool - 691.0, AvatarFixture.HardAttackCool - 689.0);   // 冷却从按键那一步起算,之后又走了 69 步(690 毫秒)
        Assert.False(hard.Ready);

        // 冷却中再按没有反应
        RunSkills(s, 100, (0, SkillKeys.A));
        Assert.Equal((HeroMotion.Rest, 1), (s.Hero.Motion, s.Hits));
        Assert.Equal(1, s.SkillCasts);                                      // 冷却中那一下不算放出
    }

    [Fact]
    public void TheHardAttackKeyAlsoPlaysItsTwoEffectLayersInFrontOfTheHeroAndTheyEndOnTheirOwn()
    {
        using var arena = new Arena(new[] { (700, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, withSkills: true);
        RoomCombatSession s = arena.Session;
        Assert.Empty(s.Effects);

        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Assert.Equal(2, s.Effects.Count);
        Assert.Equal(new[] { AvatarFixture.HardAttackEffect1, AvatarFixture.HardAttackEffect2 }, s.Effects.Select(e => e.Path).ToArray());
        Assert.All(s.Effects, e => Assert.Equal((HeroX + HeroSkills.HardAttackEffectForwardX, HeroY, false), (e.X, e.Y, e.FacingLeft)));   // 主角朝右:放在世界 x 前方(右)

        // 特效开头是 150 毫秒的空帧:前 150 毫秒画面里只有主角的精灵,之后才多出两层特效(主角的世界 y 是 100,特效的排序键 101)
        int heroId = TownHero.ObjectId;
        Assert.Contains(s.Build(0), x => x.NpcId == heroId);
        Assert.DoesNotContain(s.Build(0), x => x.AnimationPath == AvatarFixture.HardAttackEffect1);
        RunSkills(s, 150);
        List<SceneSprite> drawn = s.Build(0);
        SceneSprite plain = Assert.Single(drawn, x => x.AnimationPath == AvatarFixture.HardAttackEffect1);
        SceneSprite glow = Assert.Single(drawn, x => x.AnimationPath == AvatarFixture.HardAttackEffect2);
        Assert.Equal((SpriteBlend.Normal, SpriteBlend.Additive, HeroY + 1), (plain.Blend, glow.Blend, plain.SortKey));
        // 画家排序之后特效排在主角的精灵后面(盖在前面)
        int lastHero = drawn.FindLastIndex(x => x.NpcId == heroId);
        Assert.True(drawn.IndexOf(plain) > lastHero);
        Assert.True(drawn.IndexOf(glow) > lastHero);

        RunSkills(s, 300);                                               // 350 毫秒的特效播完就从表里去掉
        Assert.Empty(s.Effects);
        Assert.DoesNotContain(s.Build(0), x => x.AnimationPath == AvatarFixture.HardAttackEffect1);

        // 上挑、后跳没有特效
        using var other = new Arena(new[] { (700, 100) }, chr: AvatarFixture.ActionChrPath, withSkills: true);
        RunSkills(other.Session, 100, (0, SkillKeys.S));
        Assert.Empty(other.Session.Effects);
    }

    [Fact]
    public void TheHardAttackEffectsSitInFrontOfTheHeroWhicheverWayHeFaces()
    {
        using var arena = new Arena(new[] { (30, 100) }, hp: 1000, frozen: true, chr: AvatarFixture.ActionChrPath, withSkills: true);
        RoomCombatSession s = arena.Session;
        s.Update(50, -1, 0, false, false);                                 // 先往左走一小步,主角转向左
        TownHero hero = s.Hero;
        Assert.True(hero.FacingLeft);
        s.Update(10, 0, 0, false, false, SkillKeys.A);
        Assert.Equal(2, s.Effects.Count);
        Assert.All(s.Effects, e => Assert.Equal((hero.X - HeroSkills.HardAttackEffectForwardX, hero.Y, true), (e.X, e.Y, e.FacingLeft)));
    }

    [Fact]
    public void TheBackStepKeyHopsBackwardsWithoutHittingAnythingAndSessionsWithoutSkillsIgnoreTheKeys()
    {
        using var arena = new Arena(new[] { (340, 100) }, chr: AvatarFixture.ActionChrPath, withSkills: true);
        RoomCombatSession s = arena.Session;
        RunSkills(s, 100, (0, SkillKeys.D));
        TownHero hero = s.Hero;
        Assert.Equal((HeroMotion.Jump, true), (hero.Motion, hero.IsBackStep));
        Assert.True(hero.X < HeroX);                                      // 朝右,往左退
        Assert.True(hero.Z > 0);
        RunSkills(s, 900);
        Assert.Equal((HeroMotion.Rest, 0.0, 0), (hero.Motion, hero.Z, s.Hits));

        using var bare = new Arena(new[] { (340, 100) }, chr: AvatarFixture.ActionChrPath);
        Assert.Empty(bare.Session.Skills.Slots);
        RunSkills(bare.Session, 100, (0, SkillKeys.D), (0, SkillKeys.S), (0, SkillKeys.A));
        Assert.Equal(HeroMotion.Rest, bare.Session.Hero.Motion);
    }
}
