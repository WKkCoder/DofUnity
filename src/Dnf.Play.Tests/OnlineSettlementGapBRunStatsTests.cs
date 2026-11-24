using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 联网房间托管(<see cref="HostedRoomRun"/>)整局累计统计 <c>Local*</c> 与外部覆盖层钩子 <see cref="HostedRoomRun.HudOverlay"/> 的补测(区域 B):
/// 每个累计属性各断言两种状态——"单房间非零值"(只有当前房间的值)与"换房之后第二间叠加"(第一间冻结进累计、第二间继续加);
/// 覆盖层只返回一个精灵时也要并入输出,返回 null/空列表时输出与不设覆盖层逐项相同。
/// 房间是合成的迷你房间(<see cref="RoomCombatTests.Arena"/> / <see cref="HeroKnockdownTests.Arena"/>),数据全是合成的。
/// </summary>
public sealed class OnlineSettlementGapBRunStatsTests
{
    private static HostedRoomRun NewRun(RoomCombatSession room)
    {
        return new HostedRoomRun(room, _ => true, () => false);
    }

    private static HeroProgress SyntheticProgress()
    {
        var expTable = new ExpTable(new[] { 1000, 3000, 7000, 15000, 30000 });
        var spTable = new SpTable(new[] { 0, 10, 20, 30, 40, 50 });
        return new HeroProgress(expTable, spTable, CharacterGrowth.Parse(null), MobRewardTable.Placeholder(expTable), ExpRates.Neutral, 1);
    }

    /// <summary>每步 10 毫秒不按键地推进(经托管器),直到条件成立(最多 <paramref name="maxSteps"/> 步);不成立让测试失败。</summary>
    private static void StepUntil(HostedRoomRun run, Func<bool> condition, string what, int maxSteps = 800)
    {
        for (int i = 0; i < maxSteps && !condition(); i++)
        {
            run.Update(10, 0, 0, false, false);
        }
        Assert.True(condition(), what);
    }

    /// <summary>一直按着攻击键、每步 10 毫秒(经托管器),直到条件成立(最多 <paramref name="maxSteps"/> 步)。</summary>
    private static void SwingUntil(HostedRoomRun run, Func<bool> condition, string what, int maxSteps = 1500)
    {
        for (int i = 0; i < maxSteps && !condition(); i++)
        {
            run.Update(10, 0, 0, true, false);
        }
        Assert.True(condition(), what);
    }

    // ---- 击杀 / 连击击杀 / 最高连击 ----

    /// <summary>
    /// 保护的行为:整局击杀、连击击杀、最高连击是各房间累计(击杀与连击击杀相加,最高连击取最大),
    /// 单房间时就是当前房间的值,换房之后第一间的值冻结进累计、第二间继续叠加。
    /// 场景:第一间一只 40 血的怪(一击约 25 点,两击必死:击杀 1、第二击时连击数 2 已显示 → 连击击杀 1、最高连击 2);
    /// 第二间一只 80 血的怪(四击:击杀 1、第四击时连击数 4 → 连击击杀 1、最高连击 4)。
    /// </summary>
    [Fact]
    public void Kill_and_combo_totals_cover_a_single_room_and_add_the_second_room_after_a_room_change()
    {
        using var arena0 = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 40);
        using var arena1 = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 80);
        RoomCombatSession room0 = arena0.Session;
        RoomCombatSession room1 = arena1.Session;
        HostedRoomRun run = NewRun(room0);

        Assert.Equal((0, 0, 0), (run.LocalKills, run.LocalComboKills, run.LocalMaxCombo));

        // 单房间:第一间打死一只
        SwingUntil(run, () => room0.Kills == 1, "第一间的怪被打死");
        Assert.Equal((1, 1, 2), (room0.Kills, room0.ComboKills, room0.MaxCombo));
        Assert.Equal(1, run.LocalKills);
        Assert.Equal(1, run.LocalComboKills);
        Assert.Equal(2, run.LocalMaxCombo);

        // 换房:第二间刚进来(各项为 0),累计保持第一间的值
        run.EnterRoom(room1, roomIndex: 1, mapId: 0, entry: null, exit: null);
        Assert.Equal((0, 0, 0), (room1.Kills, room1.ComboKills, room1.MaxCombo));
        Assert.Equal(1, run.LocalKills);
        Assert.Equal(1, run.LocalComboKills);
        Assert.Equal(2, run.LocalMaxCombo);

        // 第二间打死一只:击杀与连击击杀相加(1 + 1),最高连击取最大(max(2, 4) = 4)
        SwingUntil(run, () => room1.Kills == 1, "第二间的怪被打死");
        Assert.Equal((1, 1, 4), (room1.Kills, room1.ComboKills, room1.MaxCombo));
        Assert.Equal(2, run.LocalKills);
        Assert.Equal(2, run.LocalComboKills);
        Assert.Equal(4, run.LocalMaxCombo);

        // 战绩快照里同样是累计值(击杀、连击击杀、最高连击各在自己的字段里)
        RunStats stats = run.SnapshotLocalStats();
        Assert.Equal(2, stats.Kills);
        Assert.Equal(2, stats.ComboKills);
        Assert.Equal(4, stats.MaxCombo);
        Assert.Equal(0, stats.HitsTaken);
        Assert.Equal(0, stats.RevivesUsed);
        Assert.Equal((int)Math.Round(run.ClockMs), stats.ClearMs);
    }

    /// <summary>保护的行为:最高连击取各房间最大值——第一间连击更高、第二间更低时,累计仍是第一间的值。</summary>
    [Fact]
    public void Max_combo_keeps_the_higher_room_value_when_the_later_room_is_lower()
    {
        using var arena0 = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 80);
        using var arena1 = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 40);
        RoomCombatSession room0 = arena0.Session;
        RoomCombatSession room1 = arena1.Session;
        HostedRoomRun run = NewRun(room0);

        SwingUntil(run, () => room0.Kills == 1, "第一间的怪被打死");
        Assert.Equal(4, room0.MaxCombo);
        Assert.Equal(4, run.LocalMaxCombo);

        run.EnterRoom(room1, roomIndex: 1, mapId: 0, entry: null, exit: null);
        SwingUntil(run, () => room1.Kills == 1, "第二间的怪被打死");
        Assert.Equal(2, room1.MaxCombo);
        Assert.Equal(4, run.LocalMaxCombo);   // max(4, 2)
        Assert.Equal(2, run.LocalKills);
    }

    // ---- 受击 / 复活 ----

    /// <summary>
    /// 保护的行为:整局主角受击次数与复活币使用次数是各房间之和:单房间就是当前房间的值,换房之后叠加第二间。
    /// 场景:每间房一只怪出一次招,一击 25 点把 20 点血的主角打倒(受击 1 次);躺平后复活窗口打开、按攻击键用一枚币复活(复活 1 次);
    /// 复活窗口全局共用一个(两间房共 2 枚币,第一间用 1 枚,第二间再用 1 枚)。
    /// </summary>
    [Fact]
    public void Hit_and_revive_totals_cover_a_single_room_and_add_the_second_room_after_a_room_change()
    {
        using var arena0 = new HeroKnockdownTests.Arena(HeroKnockdownTests.Strike(down: true), hpMax: 20);
        using var arena1 = new HeroKnockdownTests.Arena(HeroKnockdownTests.Strike(down: true), hpMax: 20);
        RoomCombatSession room0 = arena0.Session;
        RoomCombatSession room1 = arena1.Session;
        var revive = new HeroRevive(null, new ReviveCoins(2));
        room0.Revive = revive;
        HostedRoomRun run = NewRun(room0);

        Assert.Equal((0, 0), (run.LocalHitsTaken, run.LocalRevivesUsed));

        // 单房间:被打中一次
        StepUntil(run, () => room0.HeroHits > 0, "第一间的主角被怪物打中");
        Assert.Equal(1, room0.HeroHits);
        Assert.True(room0.HeroDead);
        Assert.Equal(1, run.LocalHitsTaken);
        Assert.Equal(0, run.LocalRevivesUsed);

        // 单房间:复活一次
        StepUntil(run, () => revive.Open, "第一间的复活窗口在主角躺平后打开");
        run.Update(10, 0, 0, true, false);
        Assert.Equal(1, room0.Revived);
        Assert.False(room0.HeroDead);
        Assert.Equal(1, run.LocalHitsTaken);
        Assert.Equal(1, run.LocalRevivesUsed);

        // 换房:第二间刚进来(各项为 0),累计保持;复活窗口沿用同一个(第二间自己没有)
        run.EnterRoom(room1, roomIndex: 1, mapId: 0, entry: null, exit: null);
        Assert.Same(revive, room1.Revive);
        Assert.Equal((0, 0), (room1.HeroHits, room1.Revived));
        Assert.Equal(1, run.LocalHitsTaken);
        Assert.Equal(1, run.LocalRevivesUsed);

        // 第二间:同样被打倒一次、复活一次 → 累计 2、2
        StepUntil(run, () => room1.HeroHits > 0, "第二间的主角被怪物打中");
        Assert.Equal(1, room1.HeroHits);
        Assert.Equal(2, run.LocalHitsTaken);
        Assert.Equal(1, run.LocalRevivesUsed);
        StepUntil(run, () => revive.Open, "第二间的复活窗口在主角躺平后打开");
        run.Update(10, 0, 0, true, false);
        Assert.Equal(1, room1.Revived);
        Assert.Equal(2, run.LocalHitsTaken);
        Assert.Equal(2, run.LocalRevivesUsed);

        RunStats stats = run.SnapshotLocalStats();
        Assert.Equal(2, stats.HitsTaken);
        Assert.Equal(2, stats.RevivesUsed);
        Assert.Equal(0, stats.Kills);
    }

    // ---- 经验 / 金币 / 物品 ----

    /// <summary>保护的行为:整局本地经验累计是各房间之和(单房间 = 当前房间,换房后叠加);经验 0 不计。</summary>
    [Fact]
    public void Exp_total_covers_a_single_room_and_adds_the_second_room_after_a_room_change()
    {
        HeroProgress progress = SyntheticProgress();
        using var arena0 = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 40, progress: progress);
        using var arena1 = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 40, progress: progress);
        RoomCombatSession room0 = arena0.Session;
        RoomCombatSession room1 = arena1.Session;
        HostedRoomRun run = NewRun(room0);
        Assert.Equal(0L, run.LocalExpEarned);

        // 单房间:120 点(升级需要 1000 点,不会升级)
        room0.GainExp(120);
        room0.GainExp(0);
        Assert.Equal(120L, room0.ExpEarned);
        Assert.Equal(120L, run.LocalExpEarned);

        // 换房:累计保持 120;第二间再得 30 → 150
        run.EnterRoom(room1, roomIndex: 1, mapId: 0, entry: null, exit: null);
        Assert.Equal(0L, room1.ExpEarned);
        Assert.Equal(120L, run.LocalExpEarned);
        room1.GainExp(30);
        Assert.Equal(30L, room1.ExpEarned);
        Assert.Equal(150L, run.LocalExpEarned);
    }

    /// <summary>
    /// 保护的行为:整局本地拾取金币累计是各房间之和(单房间 = 当前房间,换房后叠加):
    /// 第一间捡 25 与 10 两堆(35),第二间捡 7(累计 42)。
    /// </summary>
    [Fact]
    public void Gold_total_covers_a_single_room_and_adds_the_second_room_after_a_room_change()
    {
        using var arena0 = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 40);
        using var arena1 = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 40);
        RoomCombatSession room0 = arena0.Session;
        RoomCombatSession room1 = arena1.Session;
        HostedRoomRun run = NewRun(room0);
        Assert.Equal(0L, run.LocalGoldEarned);

        CollectGold(run, 25);
        Assert.Equal(25L, run.LocalGoldEarned);
        CollectGold(run, 10);
        Assert.Equal(35L, room0.GoldEarned);
        Assert.Equal(35L, run.LocalGoldEarned);

        run.EnterRoom(room1, roomIndex: 1, mapId: 0, entry: null, exit: null);
        Assert.Equal(0L, room1.GoldEarned);
        Assert.Equal(35L, run.LocalGoldEarned);
        CollectGold(run, 7);
        Assert.Equal(7L, room1.GoldEarned);
        Assert.Equal(42L, run.LocalGoldEarned);
    }

    /// <summary>
    /// 保护的行为:整局本地拾取物品件数累计是各房间之和(单房间 = 当前房间,换房后叠加):
    /// 第一间捡 2 件,第二间捡 1 件(累计 3)。
    /// </summary>
    [Fact]
    public void Item_total_covers_a_single_room_and_adds_the_second_room_after_a_room_change()
    {
        using var arena0 = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 40);
        using var arena1 = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 40);
        RoomCombatSession room0 = arena0.Session;
        RoomCombatSession room1 = arena1.Session;
        HostedRoomRun run = NewRun(room0);
        Assert.Equal(0, run.LocalItemsPicked);

        PickUpItem(run, 9901);
        Assert.Equal(1, run.LocalItemsPicked);
        PickUpItem(run, 9902);
        Assert.Equal(2, room0.ItemsPicked);
        Assert.Equal(2, run.LocalItemsPicked);

        run.EnterRoom(room1, roomIndex: 1, mapId: 0, entry: null, exit: null);
        Assert.Equal(0, room1.ItemsPicked);
        Assert.Equal(2, run.LocalItemsPicked);
        PickUpItem(run, 9903);
        Assert.Equal(1, room1.ItemsPicked);
        Assert.Equal(3, run.LocalItemsPicked);
    }

    /// <summary>在主角脚下抛一堆金币,等它落地、让主角站到落点上,下一步自动捡起;断言本房间金币累计恰好多了 <paramref name="amount"/>。</summary>
    private static void CollectGold(HostedRoomRun run, int amount)
    {
        RoomCombatSession room = run.Room;
        long before = room.GoldEarned;
        FieldDrop drop = room.SpawnGold(amount, room.Hero.X, room.Hero.Y);
        StepUntil(run, () => drop.Landed, "金币落地", 300);
        room.Hero.Place(drop.X, drop.Y, false, HeroMotion.Rest, 0);
        run.Update(10, 0, 0, false, false);
        Assert.Equal(before + amount, room.GoldEarned);
    }

    /// <summary>在主角脚下抛一件物品,等它落地、让主角站到落点上,攻击键即拾取;断言本房间物品件数恰好多了 1。</summary>
    private static void PickUpItem(HostedRoomRun run, int code)
    {
        RoomCombatSession room = run.Room;
        int before = room.ItemsPicked;
        FieldDrop drop = room.SpawnItem(OnlineSettlementGapBKit.StackItem(code), room.Hero.X, room.Hero.Y);
        StepUntil(run, () => drop.Landed, "物品落地", 300);
        room.Hero.Place(drop.X, drop.Y, false, HeroMotion.Rest, 0);
        run.Update(10, 0, 0, false, false);
        Assert.Same(drop, room.SelectedDrop);
        run.Update(10, 0, 0, true, false);
        Assert.Equal(before + 1, room.ItemsPicked);
    }

    // ---- 外部覆盖层钩子 ----

    private static List<(string Path, int X, int Y, int Sort, int Rank, int AniFrame, bool Mirrored, int Alpha, int NpcId)> Project(IEnumerable<SceneSprite> sprites)
    {
        return sprites.Select(s => (s.AnimationPath, s.ScreenX, s.ScreenY, s.SortKey, s.LayerRank, s.AniFrame, s.Mirrored, s.TintAlpha, s.NpcId)).ToList();
    }

    /// <summary>
    /// 保护的行为:<see cref="HostedRoomRun.Build"/> 在覆盖层只返回一个精灵时也把它并入输出(不是至少两个才并),
    /// 覆盖层收到的是这个 run 自己与 Build 的时间参数;覆盖层为 null、未设或返回 null/空列表时输出与不设覆盖层逐项相同。
    /// </summary>
    [Fact]
    public void Build_merges_a_single_overlay_sprite_and_changes_nothing_for_a_null_or_empty_overlay()
    {
        using var arena = new RoomCombatTests.Arena(new[] { (340, 100) }, hp: 40);
        HostedRoomRun run = NewRun(arena.Session);
        run.Update(16, 0, 0);

        // 基线:没设覆盖层
        var baseline = Project(run.Build(500));
        Assert.NotEmpty(baseline);
        Assert.DoesNotContain(baseline, s => s.Path == "gapb-hud-marker");

        // 返回 null / 空列表:逐项相同
        run.HudOverlay = (r, t) => null;
        Assert.Equal(baseline, Project(run.Build(500)));
        run.HudOverlay = (r, t) => Array.Empty<SceneSprite>();
        Assert.Equal(baseline, Project(run.Build(500)));

        // 只返回一个精灵:并入输出,位置与排序值原样;其余项与基线相同
        HostedRoomRun seenRun = null;
        long seenTime = -1;
        int calls = 0;
        SceneSprite marker = new SceneSprite(OnlineSettlementGapBKit.Pixel(), 321, 123, 45, 0, "gapb-hud-marker", 6, layerRank: OverlayArt.UiRank);
        run.HudOverlay = (r, t) =>
        {
            seenRun = r;
            seenTime = t;
            calls++;
            return new[] { marker };
        };
        List<SceneSprite> withOverlay = run.Build(777);
        Assert.Equal(1, calls);
        Assert.Same(run, seenRun);
        Assert.Equal(777L, seenTime);
        Assert.Equal(baseline.Count + 1, withOverlay.Count);
        SceneSprite found = Assert.Single(withOverlay, s => s.AnimationPath == "gapb-hud-marker");
        Assert.Same(marker, found);
        Assert.Equal((321, 123, 45, OverlayArt.UiRank, 6), (found.ScreenX, found.ScreenY, found.SortKey, found.LayerRank, found.AniFrame));
        Assert.Equal(baseline, Project(withOverlay.Where(s => s.AnimationPath != "gapb-hud-marker")));

        // 清掉覆盖层:又回到基线
        run.HudOverlay = null;
        Assert.Equal(baseline, Project(run.Build(500)));
    }
}
