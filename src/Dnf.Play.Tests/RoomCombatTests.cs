using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Render.Tests;

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
        public Arena(IReadOnlyList<(int X, int Y)> monsters, HeroStrikeInfo[] strikes = null, bool facingLeft = false, int hp = 100,
            bool frozen = false, int seed = 1)
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
            TownHero hero = TownHero.Create(Assets, AvatarFixture.ChrPath, outfit, area, HeroX, HeroY, facingLeft, TownHero.DungeonRestTag);
            var room = new RoomSession(Assets, content, hero, new TownCamera(0, map.MaxCameraX, hero.X));

            MonsterModel model = MonsterModel.Read(Assets, AvatarFixture.BlobMobPath);
            var actors = new List<MonsterActor>();
            for (int i = 0; i < monsters.Count; i++)
            {
                var spawn = new RoomMonsterSpawn(1, 0, 0, monsters[i].X, monsters[i].Y, 0, 1, 0, "[fixed]", "[normal]");
                actors.Add(new MonsterActor(i, spawn, model, 1, hp, 222.5, 1500, 0));
            }
            Session = new RoomCombatSession(Assets, room, actors, strikes ?? Plain(), seed);
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
        var strikes = new[] { new HeroStrikeInfo(0, 30, true), new HeroStrikeInfo(0, 0, false), new HeroStrikeInfo(0, 0, false) };
        using var arena = new Arena(new[] { (340, 100) }, strikes);
        RoomCombatSession s = arena.Session;
        arena.Run(300, 0);                                                // 第 0 段 200 毫秒命中,推 30 像素用 300 毫秒走完
        MonsterActor m = s.Monsters[0];
        Assert.Equal(1, s.Hits);
        Assert.Equal(AvatarFixture.BlobAnimationDir + "/down.ani", m.CurrentAnimationPath);
        arena.Run(200);
        Assert.Equal(340 + 30.0, m.X, 6);                                 // 朝右的挥击把怪物往右推

        using var left = new Arena(new[] { (260, 100) }, strikes, facingLeft: true);
        left.Run(600, 0);
        Assert.Equal(1, left.Session.Hits);
        Assert.Equal(260 - 30.0, left.Session.Monsters[0].X, 6);
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
}
