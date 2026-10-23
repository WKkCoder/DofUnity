using Dnf.Game;
using Dnf.Game.Tests;
using Dnf.Render;
using static Dnf.Play.Tests.RoomCombatTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 合成的金币图:地面物品图包里五个档位的帧(宽度 = 帧号,高 6,偏移 (1, 2),画布 (帧号 + 2) × 8,红 = 帧号),
/// 飘字图包的数字 0 到 9(画布 10 × 14,块 8 × 12、偏移 (1, 1),红 = 数字)与第 10 帧的"金币"标记(画布 14 × 14,块 12 × 12、偏移 (0, 1),红 = 100)。
/// 数据与原版图包无关。
/// </summary>
internal sealed class DropSprites : ISpriteSource
{
    private readonly Dictionary<(string, int), SpriteFrame> _frames = new Dictionary<(string, int), SpriteFrame>();

    /// <param name="field">有地面物品图。</param>
    /// <param name="digits">有飘字的数字与标记。</param>
    /// <param name="missingDigit">数字里缺的一帧(−1 不缺)。</param>
    public DropSprites(bool field = true, bool digits = true, int missingDigit = -1)
    {
        if (field)
        {
            foreach (int frame in new[] { 25, 26, 27, 36, 37 })
            {
                _frames[(FieldDrop.ImagePath, frame)] = Block(frame, 6, 1, 2, frame + 2, 8, frame);
            }
        }
        if (digits)
        {
            for (int d = 0; d < 10; d++)
            {
                if (d != missingDigit)
                {
                    _frames[(GainPopup.ImagePath, d)] = Block(8, 12, 1, 1, 10, 14, d);
                }
            }
            _frames[(GainPopup.ImagePath, GainPopup.LabelFrame)] = Block(12, 12, 0, 1, 14, 14, 100);
        }
    }

    private static SpriteFrame Block(int w, int h, int offsetX, int offsetY, int canvasW, int canvasH, int red)
    {
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = (byte)red;
            rgba[i * 4 + 3] = 255;
        }
        return new SpriteFrame(w, h, offsetX, offsetY, canvasW, canvasH, rgba);
    }

    public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
    {
        return _frames.TryGetValue((imageName.ToLowerInvariant(), frame), out sprite);
    }
}

/// <summary>
/// 地面上的一堆金币(证据 E-116):按金额选帧与声音(客户端的分档)、抛出落地的运动(推断)、拾取范围、精灵的位置。数据全是合成的。
/// </summary>
public class FieldDropTests
{
    private static FieldDrop Launched(int amount = 10, int seed = 1, Func<double, double, bool> canStand = null)
    {
        return FieldDrop.Launch(1, amount, 300, 100, new DropRandom(seed), canStand);
    }

    [Theory]
    [InlineData(0, 26)]
    [InlineData(1, 26)]
    [InlineData(99, 26)]
    [InlineData(100, 27)]
    [InlineData(499, 27)]
    [InlineData(500, 25)]
    [InlineData(999, 25)]
    [InlineData(1000, 36)]
    [InlineData(1499, 36)]
    [InlineData(1500, 37)]
    [InlineData(2000000000, 37)]
    public void TheFrameFollowsTheAmountBuckets(int amount, int frame)
    {
        Assert.Equal(frame, FieldDrop.FrameFor(amount));
    }

    [Theory]
    [InlineData(1, "GOLD_DROP1")]
    [InlineData(99, "GOLD_DROP1")]
    [InlineData(100, "GOLD_DROP2")]
    [InlineData(5000, "GOLD_DROP2")]
    public void TheSoundNameSplitsAtOneHundred(int amount, string sound)
    {
        Assert.Equal(sound, FieldDrop.SoundFor(amount));
    }

    [Fact]
    public void ALaunchLandsAtTheScatterTargetAfterTheFlightTime()
    {
        var expected = new DropRandom(7);
        int dx = expected.Range(-FieldDrop.ScatterX, FieldDrop.ScatterX);       // 掷骰的顺序:先横向、后进深
        int dy = expected.Range(-FieldDrop.ScatterY, FieldDrop.ScatterY);
        FieldDrop d = Launched(seed: 7);
        double flight = FieldDrop.FlightMs(FieldDrop.StartHeight, FieldDrop.LaunchSpeed);
        Assert.Equal((FieldDrop.StartHeight, false), (d.Z, d.Landed));
        d.Advance(flight - 1);
        Assert.False(d.Landed);
        Assert.True(d.Z > 0);
        d.Advance(2);
        Assert.True(d.Landed);
        Assert.Equal(300 + dx, d.X, 6);
        Assert.Equal(100 + dy, d.Y, 6);
    }

    [Fact]
    public void TheScatterStaysInsideTheBox()
    {
        for (int seed = 1; seed <= 200; seed++)
        {
            FieldDrop d = Launched(seed: seed);
            d.Advance(5000);
            Assert.InRange(d.X, 300 - FieldDrop.ScatterX, 300 + FieldDrop.ScatterX);
            Assert.InRange(d.Y, 100 - FieldDrop.ScatterY, 100 + FieldDrop.ScatterY);
        }
    }

    [Fact]
    public void ABlockedLandingSpotHalvesTheOffsetAndFallsBackToTheOrigin()
    {
        // 横向离怪物不超过 20 才能站:偏移折半再试,最后一定落在 20 以内
        for (int seed = 1; seed <= 50; seed++)
        {
            FieldDrop near = Launched(seed: seed, canStand: (x, y) => Math.Abs(x - 300) <= 20);
            near.Advance(5000);
            Assert.InRange(Math.Abs(near.X - 300), 0, 20);
        }
        // 到处都不能站:退回怪物脚下
        FieldDrop none = Launched(seed: 3, canStand: (x, y) => false);
        none.Advance(5000);
        Assert.Equal((300.0, 100.0), (none.X, none.Y));
    }

    [Fact]
    public void ADropCannotBePickedUpInTheAirButCanOnceLanded()
    {
        FieldDrop d = Launched(seed: 5);
        d.Advance(100);
        Assert.False(d.IsInReach(d.X, d.Y, 0));          // 主角正好站在下面,但它还在空中
        d.Advance(5000);
        Assert.True(d.IsInReach(d.X, d.Y, 0));
    }

    [Fact]
    public void TheReachBoxHasInclusiveEdgesAndAJumpHeightLimit()
    {
        var d = new FieldDrop(1, 5, 200, 100, 0, 0, 0, 0);
        d.Advance(1);
        Assert.True(d.Landed);
        Assert.True(d.IsInReach(200 + FieldDrop.PickupHalfWidth, 100 + FieldDrop.PickupHalfDepth, FieldDrop.PickupMaxHeroZ));
        Assert.True(d.IsInReach(200 - FieldDrop.PickupHalfWidth, 100 - FieldDrop.PickupHalfDepth, 0));
        Assert.False(d.IsInReach(200 + FieldDrop.PickupHalfWidth + 1, 100, 0));
        Assert.False(d.IsInReach(200 - FieldDrop.PickupHalfWidth - 1, 100, 0));
        Assert.False(d.IsInReach(200, 100 + FieldDrop.PickupHalfDepth + 1, 0));
        Assert.False(d.IsInReach(200, 100 - FieldDrop.PickupHalfDepth - 1, 0));
        Assert.False(d.IsInReach(200, 100, FieldDrop.PickupMaxHeroZ + 1));
    }

    [Fact]
    public void ADropBouncesNeverGoesBelowTheGroundAndEventuallyRests()
    {
        FieldDrop d = Launched(seed: 2);
        bool restedAt = false;
        for (int t = 0; t < 3000; t++)
        {
            d.Advance(1);
            Assert.True(d.Z >= 0.0);
            if (d.Rested)
            {
                restedAt = true;
                break;
            }
        }
        Assert.True(restedAt);
        Assert.Equal(0.0, d.Z);
        (double x, double y) = (d.X, d.Y);
        d.Advance(1000);
        Assert.Equal((x, y, 0.0), (d.X, d.Y, d.Z));      // 停住以后不再动
    }

    [Fact]
    public void ADropHopsAtLeastOnceBeforeResting()
    {
        FieldDrop d = Launched(seed: 2);
        double flight = FieldDrop.FlightMs(FieldDrop.StartHeight, FieldDrop.LaunchSpeed);
        d.Advance(flight + 20);
        Assert.True(d.Landed);
        Assert.False(d.Rested);
        Assert.True(d.Z > 0.0);                          // 落地后弹起一小段
    }

    [Fact]
    public void TheOutcomeDoesNotDependOnHowTimeIsSliced()
    {
        FieldDrop whole = Launched(seed: 9);
        FieldDrop ones = Launched(seed: 9);
        FieldDrop sixteens = Launched(seed: 9);
        whole.Advance(700);
        for (int i = 0; i < 700; i++)
        {
            ones.Advance(1);
        }
        for (int i = 0; i < 43; i++)
        {
            sixteens.Advance(16);
        }
        sixteens.Advance(700 - 43 * 16);
        foreach (FieldDrop other in new[] { ones, sixteens })
        {
            Assert.Equal(whole.X, other.X, 6);
            Assert.Equal(whole.Y, other.Y, 6);
            Assert.Equal(whole.Z, other.Z, 6);
            Assert.Equal((whole.Landed, whole.Rested), (other.Landed, other.Rested));
        }
    }

    [Fact]
    public void ADropBornOnTheGroundRestsAtOnce()
    {
        var d = new FieldDrop(1, 5, 10, 20, 0, 0, 0, 0);
        Assert.False(d.Landed);
        d.Advance(1);
        Assert.True(d.Landed);
        Assert.True(d.Rested);
        Assert.Equal((10.0, 20.0, 0.0), (d.X, d.Y, d.Z));
    }

    [Fact]
    public void TheSpriteSitsAtTheProjectedPositionAndSortsJustBehindTheActors()
    {
        var d = new FieldDrop(1, 250, 200, 100, 5, 0, 0, 0);          // 金额 250 → 第 27 帧(宽 27,偏移 (1, 2))
        var projection = new TownProjection(50, 200);
        SceneSprite s = d.Sprite(new DropSprites(), projection);
        Assert.NotNull(s);
        Assert.Equal(27, s.AniFrame);
        Assert.Equal(27, s.Frame.Width);
        Assert.Equal(FieldDrop.ImagePath, s.AnimationPath);
        Assert.Equal(200 - 50 + 1, s.ScreenX);                        // 世界 x − 相机 x + 帧偏移 x
        Assert.Equal(100 - 5 + 200 + 2, s.ScreenY);                   // 世界 y − z + 地面常数 + 帧偏移 y
        Assert.Equal(99, s.SortKey);
        Assert.Equal(TownScene.NormalRank, s.LayerRank);
    }

    [Fact]
    public void TheSpriteIsNullWithoutTheFieldArt()
    {
        var d = new FieldDrop(1, 250, 200, 100, 0, 0, 0, 0);
        var projection = new TownProjection(0, 0);
        Assert.Null(d.Sprite(null, projection));
        Assert.Null(d.Sprite(new DropSprites(field: false), projection));
    }
}

/// <summary>拾取飘字(证据 E-116):金额的数字帧加"金币"标记,居中在主角头顶,淡出与上升是推断。数据全合成。</summary>
public class GainPopupTests
{
    private static readonly TownProjection Projection = new TownProjection(100, 200);

    [Fact]
    public void TheTextIsTheAmountFollowedByTheLabel()
    {
        Assert.Equal("1250G", new GainPopup(1250, 0, 0, 0).Text);
        Assert.Equal("7G", new GainPopup(7, 0, 0, 0).Text);
    }

    [Fact]
    public void TheDigitsAndTheLabelAreCenteredOnTheHero()
    {
        var popup = new GainPopup(305, 400, 120, 0);
        var into = new List<SceneSprite>();
        int order = 10;
        Assert.True(popup.Build(into, ref order, new DropSprites(), Projection));
        Assert.Equal(4, into.Count);
        Assert.Equal(14, order);                                              // 每个精灵占一个序号
        Assert.Equal(new[] { 10, 11, 12, 13 }, into.Select(s => s.SortKey).ToArray());
        Assert.All(into, s => Assert.Equal("gain", s.AnimationPath));
        Assert.All(into, s => Assert.Equal(OverlayArt.UiRank, s.LayerRank));
        // 帧顺序:3、0、5、标记(合成图里数字帧的红色就是数字,标记是 100)
        Assert.Equal(new[] { 3, 0, 5, 100 }, into.Select(s => (int)s.Frame.Rgba[0]).ToArray());
        // 总宽 = 3 × 10 + 14 = 44;起点 = 世界 x − 相机 x − 44 / 2 = 400 − 100 − 22 = 278;每个帧按画布宽度推进,块再加自己的偏移
        Assert.Equal(new[] { 279, 289, 299, 308 }, into.Select(s => s.ScreenX).ToArray());
        // 顶 = 世界 y + 地面常数 − z − 头高 − 上升 = 120 + 200 − 0 − 118 − 0;块加偏移 y 1
        Assert.All(into, s => Assert.Equal(120 + 200 - GainPopup.HeadHeight + 1, s.ScreenY));
    }

    [Fact]
    public void AMissingFrameAddsNothingAndReportsFailure()
    {
        var popup = new GainPopup(305, 400, 120, 0);
        var into = new List<SceneSprite>();
        int order = 3;
        Assert.False(popup.Build(into, ref order, new DropSprites(missingDigit: 5), Projection));
        Assert.Empty(into);
        Assert.Equal(3, order);
        Assert.False(popup.Build(into, ref order, new DropSprites(digits: false), Projection));
        Assert.False(popup.Build(into, ref order, null, Projection));
        Assert.False(popup.Build(null, ref order, new DropSprites(), Projection));
    }

    [Fact]
    public void ThePopupRisesAndFadesWithAge()
    {
        var p = new GainPopup(10, 0, 0, 0);
        Assert.Equal(255, p.AlphaAt(0));
        Assert.Equal(255, p.AlphaAt(GainPopup.LifeMs * GainPopup.FadeStart));
        Assert.Equal(0, p.AlphaAt(GainPopup.LifeMs));
        Assert.Equal(0, p.AlphaAt(GainPopup.LifeMs * 3));
        double mid = GainPopup.LifeMs * (GainPopup.FadeStart + 1.0) / 2.0;
        Assert.InRange(p.AlphaAt(mid), 126, 129);
        int last = 256;
        for (double t = 0; t <= GainPopup.LifeMs; t += 10)
        {
            Assert.True(p.AlphaAt(t) <= last);                                   // 淡出只减不增
            last = p.AlphaAt(t);
        }
        Assert.Equal(0, p.RiseAt(0));
        Assert.Equal(GainPopup.RisePixels, p.RiseAt(GainPopup.LifeMs));
        Assert.Equal(GainPopup.RisePixels, p.RiseAt(GainPopup.LifeMs * 2));
        Assert.Equal(0, p.RiseAt(-5));
        p.AgeMs = GainPopup.LifeMs - 1;
        Assert.False(p.Finished);
        p.AgeMs = GainPopup.LifeMs;
        Assert.True(p.Finished);
    }

    [Fact]
    public void AFullyFadedPopupBuildsNothingButStillSucceeds()
    {
        var p = new GainPopup(10, 0, 0, 0) { AgeMs = GainPopup.LifeMs };
        var into = new List<SceneSprite>();
        int order = 0;
        Assert.True(p.Build(into, ref order, new DropSprites(), Projection));
        Assert.Empty(into);
    }

    [Fact]
    public void TheRiseMovesTheDigitsUp()
    {
        var young = new GainPopup(10, 0, 0, 0);
        var old = new GainPopup(10, 0, 0, 0) { AgeMs = GainPopup.LifeMs * GainPopup.FadeStart };
        var a = new List<SceneSprite>();
        var b = new List<SceneSprite>();
        int order = 0;
        young.Build(a, ref order, new DropSprites(), Projection);
        old.Build(b, ref order, new DropSprites(), Projection);
        Assert.Equal(old.RiseAt(old.AgeMs), a[0].ScreenY - b[0].ScreenY);
        Assert.True(a[0].ScreenY > b[0].ScreenY);
    }
}

/// <summary>主角身上的金币(证据 E-116):加算只认正数,花费不够不扣。</summary>
public class HeroGoldTests
{
    [Fact]
    public void GoldAddsUpAndOnlyPositiveAmountsCount()
    {
        HeroProgress p = ExpTests.Progress();
        Assert.Equal((0L, 0L), (p.Gold, p.GoldGained));
        p.AddGold(120);
        p.AddGold(0);
        p.AddGold(-30);
        p.AddGold(5);
        Assert.Equal((125L, 125L), (p.Gold, p.GoldGained));
        Assert.Equal(0, p.Version);                                   // 金币不算等级与经验的变化
    }

    [Fact]
    public void SpendingNeedsEnoughGoldAndKeepsTheLifetimeTotal()
    {
        HeroProgress p = ExpTests.Progress();
        p.AddGold(100);
        Assert.False(p.TrySpendGold(101));
        Assert.False(p.TrySpendGold(0));
        Assert.False(p.TrySpendGold(-1));
        Assert.Equal(100L, p.Gold);
        Assert.True(p.TrySpendGold(40));
        Assert.True(p.TrySpendGold(60));
        Assert.Equal((0L, 100L), (p.Gold, p.GoldGained));
    }
}

/// <summary>
/// 战斗会话里的金币(证据 E-116):击杀 → 掷骰 → 抛出落地 → 主角走近拾取;表是合成的(一行覆盖等级 1 到 200,
/// 种类/难度/队伍加成都是 1,基础金额 = 怪物等级 × 10、浮动 0),概率可调。
/// </summary>
public class GoldDropSessionTests
{
    private const int HeroAtX = 300;

    internal static MonsterDropTable Table(int probability = 10000, bool boss = false, int rowFrom = 1)
    {
        var b = new TokenStreamBuilder();
        if (rowFrom == 1)
        {
            b.Tag("[drop prob count]").Int(1);
            b.Tag("[drop prob]").Ints(1, 200, probability, 0, 0, 0, 0);
        }
        else
        {
            // 两行:1 到 rowFrom − 1 概率 0,rowFrom 到 200 取 probability
            b.Tag("[drop prob count]").Int(2);
            b.Tag("[drop prob]").Ints(1, rowFrom - 1, 0, 0, 0, 0, 0).Ints(rowFrom, 200, probability, 0, 0, 0, 0);
        }
        if (boss)
        {
            b.Tag("[condition rate]").Ints(100, 0, 100, 0, 100, 0, 100, 0);       // 一个人:不触发 100、触发 0 → 总走第一组
            b.Tag("[gold quantity]").Ints(3, 3, 5, 5);                             // 第一组恰好 3 堆
            b.Tag("[gold volume]").Ints(100, 100);
        }
        MonsterDropTable table = MonsterDropTable.Load(b.Cursor(), out string error);
        Assert.Null(error);
        return table;
    }

    internal static GoldGenTable Gold()
    {
        var b = new TokenStreamBuilder().Tag("[gold drop ref table]");
        for (int n = 1; n <= 200; n++)
        {
            b.Ints(n, n * 10, 0);
        }
        GoldGenTable table = GoldGenTable.Load(b.Cursor(), out string error);
        Assert.Null(error);
        return table;
    }

    /// <summary>按下攻击键、每 10 毫秒一步推到击杀(最多 1 秒)。</summary>
    private static void SwingUntilKill(RoomCombatSession s)
    {
        s.Update(10, 0, 0, true);
        for (int i = 0; i < 100 && s.Kills == 0; i++)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.Equal(1, s.Kills);
    }

    /// <summary>把主角挪到远处,免得他站在原地、金币一落地就被捡走(拾取范围见 <see cref="FieldDrop.PickupHalfWidth"/>)。</summary>
    private static void Away(RoomCombatSession s)
    {
        s.Hero.Place(60, 100, false, HeroMotion.Rest, 0);
    }

    private static void LandAll(RoomCombatSession s)
    {
        for (int i = 0; i < 300 && s.Drops.Any(d => !d.Landed); i++)
        {
            s.Update(10, 0, 0, false);
        }
    }

    [Fact]
    public void WithoutDropDataAKillDropsNothing()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        SwingUntilKill(arena.Session);
        Assert.Null(arena.Session.DropTable);
        Assert.Empty(arena.Session.Drops);
        Assert.Equal((0, 0L), (arena.Session.GoldDropped, arena.Session.GoldDroppedAmount));
    }

    [Fact]
    public void AKillDropsOnePileOfTheTableAmountAndTheHeroPicksItUp()
    {
        HeroProgress progress = ExpTests.Progress(level: 10, mobExp: 160, mobLevel: 10);
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, progress: progress, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        s.UseDrops(Table(), Gold(), 4);
        SwingUntilKill(s);
        Away(s);
        Assert.Equal((1, 100L), (s.GoldDropped, s.GoldDroppedAmount));       // 怪物 10 级:基础金额 100,浮动 0
        Assert.Single(s.Drops);
        Assert.Equal(0, s.GoldPicked);
        LandAll(s);
        FieldDrop drop = s.Drops[0];
        Assert.True(drop.Landed);
        s.Hero.Place(drop.X, drop.Y, false, HeroMotion.Rest, 0);
        s.Update(10, 0, 0, false);
        Assert.Empty(s.Drops);
        Assert.Equal((1, 100L, 100), (s.GoldPicked, s.GoldEarned, s.LastGoldPickup));
        Assert.Equal((100L, 100L), (progress.Gold, progress.GoldGained));
        Assert.Single(s.Gains);
        Assert.Equal(100, s.Gains[0].Amount);
    }

    [Fact]
    public void ThePickupSoundFollowsTheAmount()
    {
        foreach ((int level, string sound) in new[] { (5, "GOLD_DROP1"), (10, "GOLD_DROP2") })
        {
            using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: level);
            RoomCombatSession s = arena.Session;
            s.UseDrops(Table(), Gold(), 4);
            SwingUntilKill(s);
            Away(s);
            LandAll(s);
            s.Hero.Place(s.Drops[0].X, s.Drops[0].Y, false, HeroMotion.Rest, 0);
            var cues = new List<SoundCue>();
            s.DrainSounds(cues);
            cues.Clear();
            s.Update(10, 0, 0, false);
            s.DrainSounds(cues);
            Assert.Equal(new[] { sound }, cues.Where(c => c.Key.StartsWith("GOLD_")).Select(c => c.Key).ToArray());
        }
    }

    [Fact]
    public void ThePickupNeedsALandedPileAndTheHeroNearIt()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        s.UseDrops(Table(), Gold(), 4);
        SwingUntilKill(s);
        FieldDrop drop = s.Drops[0];
        // 还在空中时主角站在它下面也捡不到
        Assert.False(drop.Landed);
        s.Hero.Place(drop.X, drop.Y, false, HeroMotion.Rest, 0);
        s.Update(10, 0, 0, false);
        Assert.False(drop.Landed);
        Assert.Equal(0, s.GoldPicked);
        Away(s);
        LandAll(s);
        Assert.True(drop.Landed);
        s.Hero.Place(drop.X + FieldDrop.PickupHalfWidth + 40, drop.Y, false, HeroMotion.Rest, 0);   // 落地后主角在范围外
        s.Update(10, 0, 0, false);
        Assert.Equal((0, 1), (s.GoldPicked, s.Drops.Count));
        s.Hero.Place(drop.X, drop.Y, false, HeroMotion.Rest, 0);
        s.Update(10, 0, 0, false);
        Assert.Equal((1, 0), (s.GoldPicked, s.Drops.Count));
    }

    [Fact]
    public void AZeroProbabilityTableDropsNothing()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        arena.Session.UseDrops(Table(probability: 0), Gold(), 4);
        SwingUntilKill(arena.Session);
        Assert.Empty(arena.Session.Drops);
        Assert.Equal(0, arena.Session.GoldDropped);
    }

    [Fact]
    public void TheDropRateScalesTheProbability()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        arena.Session.UseDrops(Table(), Gold(), 4);
        arena.Session.DropRate = 0;
        SwingUntilKill(arena.Session);
        Assert.Equal(0, arena.Session.GoldDropped);
    }

    [Fact]
    public void TheDungeonStandardLevelChoosesTheProbabilityRow()
    {
        // 行 1 的等级区间是 1 到 49(概率 0),行 2 是 50 到 200(概率 10000);怪物只有 5 级
        using (var none = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 5))
        {
            none.Session.UseDrops(Table(rowFrom: 50), Gold(), 4);
            SwingUntilKill(none.Session);
            Assert.Equal(0, none.Session.GoldDropped);                // 没有副本等级:按怪物自己的 5 级选行 → 概率 0
        }
        using (var dungeon = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 5))
        {
            dungeon.Session.UseDrops(Table(rowFrom: 50), Gold(), 4);
            dungeon.Session.DropDungeonLevel = 60;
            SwingUntilKill(dungeon.Session);
            Assert.Equal((1, 50L), (dungeon.Session.GoldDropped, dungeon.Session.GoldDroppedAmount));   // 按副本等级 60 选到第二行;金额仍按怪物等级 5
        }
    }

    [Fact]
    public void ABossDropsSeveralPiles()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10, monsterBoss: true);
        arena.Session.UseDrops(Table(boss: true), Gold(), 4);
        SwingUntilKill(arena.Session);
        Assert.Equal((3, 300L), (arena.Session.GoldDropped, arena.Session.GoldDroppedAmount));
        Assert.Equal(new[] { 100, 100, 100 }, arena.Session.Drops.Select(d => d.Amount).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, arena.Session.Drops.Select(d => d.Id).ToArray());
    }

    [Fact]
    public void ThePilesLandOnWalkableGroundOrAtTheMonsterWhenThereIsNone()
    {
        // 主角的可走区域只是脚下一小块(frozen):任何落点都站不了,金币退回怪物脚下
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10, monsterBoss: true, frozen: true);
        arena.Session.UseDrops(Table(boss: true), Gold(), 4);
        SwingUntilKill(arena.Session);
        Away(arena.Session);
        LandAll(arena.Session);
        Assert.Equal(3, arena.Session.Drops.Count);
        Assert.All(arena.Session.Drops, d => Assert.Equal((340.0, 100.0), (d.X, d.Y)));
    }

    [Fact]
    public void TheSceneShowsTheLandedPilesAndTheGainPopup()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        s.UseDrops(Table(), Gold(), 4);
        s.UseDropArt(new DropSprites());
        SwingUntilKill(s);
        Away(s);
        LandAll(s);
        List<SceneSprite> scene = s.Build(0);
        SceneSprite pile = Assert.Single(scene, x => x.AnimationPath == FieldDrop.ImagePath);
        Assert.Equal(27, pile.AniFrame);                                // 金额 100 → 第 27 帧
        Assert.DoesNotContain(scene, x => x.AnimationPath == "gain");
        s.Hero.Place(s.Drops[0].X, s.Drops[0].Y, false, HeroMotion.Rest, 0);
        s.Update(10, 0, 0, false);
        scene = s.Build(0);
        Assert.DoesNotContain(scene, x => x.AnimationPath == FieldDrop.ImagePath);
        Assert.Equal(4, scene.Count(x => x.AnimationPath == "gain"));     // "100" 三位数加标记
    }

    [Fact]
    public void WithoutTheGainArtThePopupFallsBackToPlaceholderText()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        s.UseDrops(Table(), Gold(), 4);
        s.UseDropArt(new DropSprites(digits: false));
        SwingUntilKill(s);
        Away(s);
        LandAll(s);
        s.Hero.Place(s.Drops[0].X, s.Drops[0].Y, false, HeroMotion.Rest, 0);
        s.Update(10, 0, 0, false);
        List<SceneSprite> scene = s.Build(0);
        Assert.Single(scene, x => x.AnimationPath == "gain");           // 一张占位文字图
    }

    [Fact]
    public void ThePopupExpiresAfterItsLifetime()
    {
        using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10);
        RoomCombatSession s = arena.Session;
        s.UseDrops(Table(), Gold(), 4);
        SwingUntilKill(s);
        Away(s);
        LandAll(s);
        s.Hero.Place(s.Drops[0].X, s.Drops[0].Y, false, HeroMotion.Rest, 0);
        s.Update(10, 0, 0, false);
        Assert.Single(s.Gains);
        for (int t = 0; t < GainPopup.LifeMs + 100; t += 10)
        {
            s.Update(10, 0, 0, false);
        }
        Assert.Empty(s.Gains);
    }

    [Fact]
    public void TwoSessionsWithTheSameSeedDropTheSameWay()
    {
        var positions = new List<(double, double)>();
        for (int run = 0; run < 2; run++)
        {
            using var arena = new Arena(new[] { (340, 100) }, hp: 1, monsterLevel: 10, monsterBoss: true);
            arena.Session.UseDrops(Table(boss: true), Gold(), 77);
            SwingUntilKill(arena.Session);
            Away(arena.Session);
            LandAll(arena.Session);
            foreach (FieldDrop d in arena.Session.Drops)
            {
                positions.Add((d.X, d.Y));
            }
        }
        Assert.Equal(positions.Take(3), positions.Skip(3));
    }
}
