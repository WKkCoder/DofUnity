using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 蓄力跳跃类技能(M45,证据 E-138):按住技能键蓄力、松开或蓄满才出招,蓄力档位决定跳跃的距离、高度、动画变速与命中窗口数,
/// 第一个窗口与重武装的窗口各用各的攻击信息,落地按窗口数震屏,技能键"按着"的通道从时间表与触屏一路送到会话。
/// 数据全是合成的(<see cref="AvatarFixture.HopSkill"/>):蓄力最短 100、最长 500 毫秒,距离 100 到 300、高度 60 到 120、变速百分比 100 到 200、窗口数 1 到 3,
/// 档位 = 夹住的整数插值(0, 100, 蓄力毫秒 − 100, 400)。所以按住 300 毫秒是档位 50(距离 200、高度 90、变速 1.5 倍、窗口数 2),
/// 按到 500 毫秒是档位 100(距离 300、高度 120、变速 2 倍、窗口数 3),不按住是档位 0(距离 100、高度 60、原速、窗口数 1)。
/// </summary>
public class SkillHopTests
{
    private static SkillRecipes Recipes()
    {
        return new SkillRecipes(new[]
        {
            new SkillRecipe(AvatarFixture.HopSkill,
                new[]
                {
                    new SkillStageSpec(motionIndex: 5, attackInfoIndex: -1, damageRateColumn: -1, damageFixedColumn: -1, effectIndex: -1, effectFrame: 0),
                    new SkillStageSpec(motionIndex: 6, attackInfoIndex: 3, damageRateColumn: 0, damageFixedColumn: -1, effectIndex: 0, effectFrame: 0, moreEffects: new[] { 1 }),
                },
                slideSpeedIndex: -1, slideAccelIndex: -1, forwardSpeedIndex: -1, forwardAccelIndex: -1, chainFromFrame: int.MaxValue, slideFollowsHeldDirection: false,
                hop: new SkillHopSpec(holdMinIndex: 0, holdMaxIndex: 1, distanceMinIndex: 2, distanceMaxIndex: 3, heightMinIndex: 4, heightMaxIndex: 5,
                    speedMinIndex: 6, speedMaxIndex: 7, hitsMinIndex: 8, hitsMaxIndex: 9, riseFrame: 2, landFrame: 4, hitStartFrame: 3, hitEndFrame: 5,
                    repeatAttackInfoIndex: 4)),
        });
    }

    private static HeroSkills Load(GameData data, int level = 2)
    {
        CharacterDef chr = data.GetCharacter(0);
        var book = new SkillBook(SkillCatalog.Load(data, 0));
        book.SetGiven(SkillGrants.Read(chr.Tags.Lines, 0));
        var levels = book.Learned.ToList();
        levels.Add(new KeyValuePair<int, int>(AvatarFixture.HopSkill, level));
        book.Restore(levels, new[] { new KeyValuePair<int, int>(0, AvatarFixture.HopSkill) }, 0, 0);     // 蓄力跳跃技能放在第 1 格(键 A)
        return HeroSkills.Create(data, chr, 0, book, Recipes());
    }

    private static HeroSkills Factory(GameData data)
    {
        return Load(data);
    }

    private static RoomCombatTests.Arena NewArena(int monsterX)
    {
        return new RoomCombatTests.Arena(new[] { (monsterX, 100) }, hp: 100000, chr: AvatarFixture.ActionChrPath, monsterSpeed: 0, skillsFactory: Factory);
    }

    private static void Step(RoomCombatSession s, SkillKeys held, SkillKeys pressed = SkillKeys.None)
    {
        s.SkillsHeld = held;
        s.Update(10, 0, 0, false, false, pressed);
    }

    /// <summary>按下 A 并按住 <paramref name="holdSteps"/> 步(每步 10 毫秒,第 0 步是按下那一步);第 holdSteps 步松开。不按住就是 0:按下那一步就没按着。</summary>
    private static void CastAndRelease(RoomCombatSession s, int holdSteps)
    {
        Step(s, holdSteps > 0 ? SkillKeys.A : SkillKeys.None, SkillKeys.A);
        for (int i = 1; i < holdSteps; i++)
        {
            Step(s, SkillKeys.A);
        }
        if (holdSteps > 0)
        {
            Step(s, SkillKeys.None);
        }
    }

    // ------------------------------------------------------------------ 槽与起手

    [Fact]
    public void ASlotReadsTheJumpStrikeTheRepeatStrikeAndTheEffectsAndTheKeyDoesNotChain()
    {
        using var fx = new AvatarFixture();
        using DnfAssets assets = DnfAssets.Open(fx.Root);
        var data = new GameData(new PvfScriptSource(assets.Pvf));
        HeroSkillSlot slot = Load(data, 2).SlotOf(SkillKeys.A);

        Assert.Equal((AvatarFixture.HopSkill, 2, 3500, 10), (slot.SkillId, slot.Level, slot.CooldownMs, slot.ConsumeMp));
        Assert.NotNull(slot.Recipe?.Hop);
        Assert.Equal(2, slot.StageStrikes.Count);
        HeroStrikeInfo jump = slot.StageStrikes[1];                                  // 第一个窗口(窗口数大于 1 时)
        Assert.Equal((true, AvatarFixture.HopRate2, 0, AvatarFixture.HopFirstPushAside, false), (jump.IsSkillDamage, jump.SkillRatePercent, jump.SkillFixed, jump.PushAside, jump.KnockDown));
        Assert.True(slot.HopRepeatStrike.HasValue);                                  // 重武装窗口(窗口数为 1 时唯一的窗口):击倒,伤害数用跳跃段的同一列
        HeroStrikeInfo repeat = slot.HopRepeatStrike.Value;
        Assert.Equal((true, AvatarFixture.HopRate2, 0, AvatarFixture.HopRepeatPushAside, true), (repeat.IsSkillDamage, repeat.SkillRatePercent, repeat.SkillFixed, repeat.PushAside, repeat.KnockDown));
        Assert.Equal(new[] { AvatarFixture.ChainEffect1, AvatarFixture.ChainEffect2 }, slot.StageEffects.ToArray());
        Assert.Equal(AvatarFixture.HopRate1, Load(data, 1).SlotOf(SkillKeys.A).StageStrikes[1].SkillRatePercent);

        // 起手进蓄力姿势:没有位移、没有高度、还没有窗口;这类技能不接段,再按一下什么都不发生
        HeroSkills skills = Load(data, 2);
        TownHero hero = NewHero(assets);
        Assert.Equal(SkillCastResult.Cast, skills.Cast(skills.SlotOf(SkillKeys.A), hero, null, 1, 0));
        Assert.Equal((HeroMotion.Skill, AvatarFixture.HopSkill, 0), (hero.Motion, hero.SkillId, hero.SkillStage));
        Assert.Equal((AvatarFixture.HopReadyAni, 0.0, 0.0, 0), (hero.CurrentAnimationPath, hero.SkillSlideSpeed, hero.Z, hero.HopHits));
        hero.Update(250, 0, 0);
        Assert.Null(skills.TryChain(SkillKeys.A, hero, 0, 0));
        Assert.Equal(0, hero.SkillStage);
    }

    private static TownHero NewHero(DnfAssets assets)
    {
        AvatarClass cls = AvatarClass.FromChr(assets, AvatarFixture.ChrPath, AvatarFixture.JobTag);
        AvatarOutfit outfit = AvatarOutfit.CreateDefault(assets, cls, AvatarFixture.EquipRoot, AvatarFixture.WeaponEqu);
        return TownHero.Create(assets, AvatarFixture.ActionChrPath, outfit, new List<WorldRect>(), 100, 50, false, TownHero.DungeonRestTag);
    }

    // ------------------------------------------------------------------ 点按:档位 0

    [Fact]
    public void ATapHopsAtOnceAtTheLowestChargeAndHitsOnceWithTheRepeatInfoAndShakesLightly()
    {
        using var arena = NewArena(460);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        MonsterActor m = s.Monsters[0];
        double startX = hero.X;

        Step(s, SkillKeys.None, SkillKeys.A);                                        // 按下就松开:这一步就跳
        Assert.Equal((1, 0), (s.SkillCasts, s.SkillChains));
        Assert.Equal((HeroMotion.Skill, 1, 1, true), (hero.Motion, hero.SkillStage, hero.HopHits, hero.HopAirborne));
        Assert.Equal(2, s.Effects.Count);                                            // 跳跃一开始就放预载表里的两个特效
        Assert.Equal(AvatarFixture.HopRepeatPushAside, s.CurrentHeroStrike.PushAside);   // 窗口数为 1:唯一的窗口用重武装那一项(击倒)
        Assert.True(s.CurrentHeroStrike.KnockDown);

        long shakeStrength = -1;
        int maxZ = 0;
        for (int i = 0; i < 100 && hero.Motion == HeroMotion.Skill; i++)
        {
            Step(s, SkillKeys.None);
            maxZ = Math.Max(maxZ, (int)hero.Z);
            if (hero.HopLandings == 1 && shakeStrength < 0)
            {
                shakeStrength = s.Shake.Active ? s.Shake.Strength : 0;
            }
        }
        Assert.Equal((1, 1, 1, 1), (s.Hits, s.HopWindows, hero.HopLandings, s.SkillCasts));
        Assert.True(m.IsKnockedDown);
        Assert.Equal(2, shakeStrength);                                              // 一个窗口:强度 2
        Assert.InRange(maxZ, 55, 60);                                                // 最大高度 60(10 毫秒一步,最高那一步前后各差几像素)
        Assert.Equal(startX + 99, hero.X);                                           // 落地前最后一步摆到了 99 像素
        Assert.Equal((HeroMotion.Rest, 0.0), (hero.Motion, hero.Z));
    }

    // ------------------------------------------------------------------ 按住:档位随时间

    [Fact]
    public void HoldingKeepsTheChargePoseAndReleasingSetsTheChargeFromTheHeldTime()
    {
        using var arena = NewArena(560);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        MonsterActor m = s.Monsters[0];
        double startX = hero.X;

        // 按下并按住 300 毫秒:一直在蓄力姿势里(第 0 段),没有位移、没有高度、没有命中
        Step(s, SkillKeys.A, SkillKeys.A);
        for (int i = 1; i < 30; i++)
        {
            Step(s, SkillKeys.A, i == 10 ? SkillKeys.A : SkillKeys.None);            // 蓄力中再按一次同一个键:不接段、不重放(也还在冷却)
        }
        Assert.Equal((1, 0, 0, 0, 0), (s.SkillCasts, s.SkillChains, hero.SkillStage, s.Hits, s.Effects.Count));
        Assert.Equal((HeroMotion.Skill, startX, 0.0), (hero.Motion, hero.X, hero.Z));
        Assert.Equal(AvatarFixture.HopReadyAni, hero.CurrentAnimationPath);

        // 第 31 步松开:已蓄 300 毫秒(第 0 步开始算,这一步开头是第 300 毫秒),档位 50
        Step(s, SkillKeys.None);
        Assert.Equal((1, 2, true), (hero.SkillStage, hero.HopHits, hero.HopAirborne));
        Assert.Equal(AvatarFixture.HopAni, hero.CurrentAnimationPath);
        Assert.Equal(15, hero.AttackAnimationMs);                                    // 1.5 倍:这一步走了 10 毫秒,动画走了 15
        Assert.Equal(2, s.Effects.Count);
        Assert.Equal((AvatarFixture.HopFirstPushAside, false), (s.CurrentHeroStrike.PushAside, s.CurrentHeroStrike.KnockDown));   // 窗口数 2:第一个窗口用跳跃段的信息

        var knockedAtHit = new List<bool>();
        int lastHits = s.Hits;
        int rearmStrikePush = 0;
        long shake = -1;
        for (int i = 0; i < 100 && hero.Motion == HeroMotion.Skill; i++)
        {
            Step(s, SkillKeys.None);
            if (s.Hits != lastHits)
            {
                lastHits = s.Hits;
                knockedAtHit.Add(m.IsKnockedDown);
            }
            if (hero.HopRearmsDone == 1 && rearmStrikePush == 0)
            {
                rearmStrikePush = s.CurrentHeroStrike.PushAside;
            }
            if (hero.HopLandings == 1 && shake < 0)
            {
                shake = s.Shake.Active ? s.Shake.Strength : 0;
            }
        }
        // 窗口数 2:第一个窗口(第 3 帧开头,1.5 倍速下 200 毫秒)打一下不击倒,计时器响一次后第二个窗口再打同一个怪一下,击倒
        Assert.Equal((2, 2), (s.Hits, s.HopWindows));
        Assert.Equal(new[] { false, true }, knockedAtHit.ToArray());
        Assert.Equal(AvatarFixture.HopRepeatPushAside, rearmStrikePush);
        Assert.Equal(3, shake);                                                      // 两个窗口:强度 3
        Assert.Equal((startX + 198, 0.0, HeroMotion.Rest), (hero.X, hero.Z, hero.Motion));
    }

    [Fact]
    public void HoldingToTheLongestHopsByItselfWithAllThreeWindowsAndTheHeaviestShake()
    {
        using var arena = NewArena(700);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        double startX = hero.X;

        // 一直按着:第 50 步(已蓄 500 毫秒)到了最长时间,不松手也自动出招,档位 100
        int steps = 1;
        Step(s, SkillKeys.A, SkillKeys.A);
        while (hero.SkillStage == 0 && steps < 100)
        {
            Step(s, SkillKeys.A);
            steps++;
        }
        Assert.Equal(51, steps);
        Assert.Equal((1, 3, true), (hero.SkillStage, hero.HopHits, hero.HopAirborne));
        Assert.Equal(20, hero.AttackAnimationMs);                                    // 2 倍速

        long shake = -1;
        int maxZ = 0;
        for (int i = 0; i < 100 && hero.Motion == HeroMotion.Skill; i++)
        {
            Step(s, SkillKeys.A);                                                    // 跳起来以后键还按着也没有影响
            maxZ = Math.Max(maxZ, (int)hero.Z);
            if (hero.HopLandings == 1 && shake < 0)
            {
                shake = s.Shake.Active ? s.Shake.Strength : 0;
            }
        }
        Assert.Equal((3, 1), (s.HopWindows, s.SkillCasts));
        Assert.True(s.Hits >= 2);                                                    // 窗口里每开一次都能再打同一个怪(怪被打退、击倒后还在框里的次数不在这里细数)
        Assert.Equal(5, shake);
        Assert.InRange(maxZ, 112, 120);
        Assert.Equal((startX + 294, 0.0), (hero.X, hero.Z));
    }

    [Theory]
    [InlineData(0, 1, 2)]       // 点按:档位 0,一个窗口,强度 2
    [InlineData(25, 1, 2)]      // 已蓄 250 毫秒:档位 37,窗口数 (int)(1 + 2 × 0.37) = 1
    [InlineData(30, 2, 3)]      // 300 毫秒:档位 50,窗口数 2,强度 3
    [InlineData(40, 2, 3)]      // 400 毫秒:档位 75,窗口数 (int)(1 + 2 × 0.75) = 2
    [InlineData(47, 2, 3)]      // 470 毫秒:档位 92,窗口数 (int)(1 + 2 × 0.92) = 2:差一点蓄满还是两个窗口
    [InlineData(50, 3, 5)]      // 500 毫秒:蓄满,档位 100,窗口数 3,强度 5
    [InlineData(60, 3, 5)]      // 一直按着:到 500 毫秒自动出招,后面按不按都一样
    public void TheChargeLevelFixesTheWindowCountAndTheLandingShakeFollowsIt(int holdSteps, int windows, int strength)
    {
        using var arena = NewArena(560);
        RoomCombatSession s = arena.Session;
        TownHero hero = s.Hero;
        CastAndRelease(s, holdSteps);
        Assert.Equal((HeroMotion.Skill, 1, windows), (hero.Motion, hero.SkillStage, hero.HopHits));
        long shake = -1;
        for (int i = 0; i < 100 && hero.Motion == HeroMotion.Skill; i++)
        {
            Step(s, SkillKeys.None);
            if (hero.HopLandings == 1 && shake < 0)
            {
                shake = s.Shake.Active ? s.Shake.Strength : 0;
            }
        }
        Assert.Equal(strength, shake);
        Assert.Equal(windows, s.HopWindows);
    }

    // ------------------------------------------------------------------ 按键通道

    private sealed class HoldRecorder : ISkillHoldSession
    {
        public readonly List<(SkillKeys Pressed, SkillKeys Held)> Calls = new();

        public SkillKeys SkillsHeld { get; set; }

        public string MapPath => "x";
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;

        public void Update(double deltaMs, int dirX, int dirY)
        {
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed)
        {
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed, SkillKeys skillsPressed)
        {
            Calls.Add((skillsPressed, SkillsHeld));
        }

        public List<SceneSprite> Build(long timeMs)
        {
            return new List<SceneSprite>();
        }
    }

    [Fact]
    public void TheScriptHoldsAKeyFromOneItemUntilTheNextAndAFreshPressStillFiresOnce()
    {
        var rec = new HoldRecorder();
        CombatScript script = CombatScript.Parse("0.05:a+holda,0.25:none,0.4:holdd");
        for (int i = 0; i < 30; i++)
        {
            script.Step(rec);
        }
        // 第 3 步(50.0 毫秒)按下 A 并开始按住;250 毫秒(第 15 步附近)松开;400 毫秒(第 24 步附近)起按住 D(不触发按下)
        Assert.Equal(SkillKeys.None, rec.Calls[2].Held);
        Assert.Equal((SkillKeys.A, SkillKeys.A), rec.Calls[3]);
        Assert.Equal(SkillKeys.A, rec.Calls[4].Held);
        Assert.Equal(SkillKeys.None, rec.Calls[4].Pressed);                          // 只在第一步是"刚按下"
        Assert.All(rec.Calls.Skip(3).Take(11), c => Assert.Equal(SkillKeys.A, c.Held));     // 第 3 到 13 步(≤ 216 毫秒)一直按着
        Assert.All(rec.Calls.Skip(16).Take(7), c => Assert.Equal(SkillKeys.None, c.Held));  // 第 16 到 22 步(≥ 266 毫秒、< 400 毫秒)已松开
        Assert.Equal(SkillKeys.D, rec.Calls[26].Held);
        Assert.All(rec.Calls, c => Assert.Equal(SkillKeys.None, c.Pressed & SkillKeys.D));
        Assert.Equal(SkillKeys.D, script.HeldSkills);
        Assert.Throws<FormatException>(() => CombatScript.Parse("1:holdq"));
    }

    [Fact]
    public void ASessionThatTakesNoHeldKeysIsStillDrivenByTheScript()
    {
        // 不实现按键通道的会话(旧的城镇会话等)照常被时间表推进
        CombatScript script = CombatScript.Parse("0:holdf,0.1:none");
        var plain = new PlainRecorder();
        for (int i = 0; i < 10; i++)
        {
            script.Step(plain);
        }
        Assert.Equal(10, plain.Updates);
    }

    private sealed class PlainRecorder : IActionSession
    {
        public int Updates;

        public string MapPath => "x";
        public TownHero Hero => null;
        public TownCamera Camera => null;
        public int LayerCount => 0;
        public int NpcCount => 0;

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Updates++;
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed)
        {
            Updates++;
        }

        public List<SceneSprite> Build(long timeMs)
        {
            return new List<SceneSprite>();
        }
    }
}
