using Dnf.Render;
using static Dnf.Play.Tests.DungeonSessionTests;

namespace Dnf.Play.Tests;

/// <summary>
/// 副本里的金币收集与结算战果(证据 E-116):自动机清房后先把金币捡完再去出口,首领清空后在结算菜单选择之前继续捡,
/// 结算画面左侧列出本局的战果(占位版式)。表是合成的(每次击杀必掉一堆,金额 = 怪物等级 × 10)。
/// </summary>
public class GoldCollectTests
{
    private static (int X, int Y)[] Monsters(int cx, int cy)
    {
        return cx == 0 && cy == 0 ? new[] { (340, 100), (420, 120) }
            : cx == 1 && cy == 0 ? new[] { (300, 200) }
            : cx == 1 && cy == 1 ? new[] { (400, 150), (500, 180) }
            : None();
    }

    private static void GiveDrops(RoomCombatSession s)
    {
        s.UseDrops(GoldDropSessionTests.Table(), GoldDropSessionTests.Gold(), 5);
    }

    [Fact]
    public void TheBotPicksEveryPileUpIncludingTheBossRoomOnesAfterTheClear()
    {
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40, setup: GiveDrops);
        var bot = new DungeonBot(true);
        bot.RunUntil(d.Session, 600_000, 12_000);
        DungeonSession s = d.Session;
        Assert.True(s.BossCleared);
        Assert.Equal(5, s.Kills);
        Assert.True(s.GoldDropped >= 4, "每次击杀掷一次、概率 100%,5 次击杀至少掉 4 堆(掷到上界的万分之一除外)");
        Assert.Equal((s.GoldDropped, s.GoldDroppedAmount), (s.GoldPicked, s.GoldEarned));          // 掉出来的全捡了
        Assert.Empty(s.Current.Drops);
        Assert.False(bot.WantsToCollect(s));
        foreach (RoomCombatSession room in d.Rooms.Values)
        {
            Assert.Empty(room.Drops);
        }
    }

    [Fact]
    public void WithoutDropsTheBotLeavesTheHeroStillAfterTheClearAsBefore()
    {
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40);
        var bot = new DungeonBot(true);
        bot.RunUntil(d.Session, 600_000);
        Assert.True(d.Session.BossCleared);
        Assert.False(bot.WantsToCollect(d.Session));
        Assert.Equal(0, d.Session.GoldDropped);
        Assert.False(bot.Next(d.Session, out int dirX, out int dirY));
        Assert.Equal((0, 0), (dirX, dirY));
    }

    [Fact]
    public void ThePickupStopsBeingWantedOnceTheSettlementChoiceIsMade()
    {
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40, setup: GiveDrops);
        var bot = new DungeonBot(true);
        DungeonSession s = d.Session;
        // 走到首领清空为止:不继续往下捡
        double t = 0;
        while (t < 600_000 && !s.Finished)
        {
            bool attack = bot.Next(s, out int dx, out int dy);
            s.Update(CombatScript.StepMs, dx, dy, attack);
            t += CombatScript.StepMs;
        }
        Assert.True(s.BossCleared);
        // 地上若还有首领掉的金币,这时自动机还想捡;选了结算菜单之后就不管了
        FieldDrop pile = s.Current.Drops.FirstOrDefault();
        Assert.Equal(pile != null && !s.HeroDead, bot.WantsToCollect(s));
        for (int i = 0; i < 400 && !s.SettlementActive; i++)
        {
            s.Update(CombatScript.StepMs, 0, 0);
        }
        Assert.True(s.SettlementActive);
        s.Choose(SettlementChoice.ReturnToTown);
        Assert.False(bot.WantsToCollect(s));
        Assert.False(bot.WantsToCollect(null));
    }

    [Fact]
    public void TheCollectionIsTimeBoxedWhenAPileCannotBeReached()
    {
        using var d = new Dungeon(RingLayout(), Monsters, hp: 40, setup: GiveDrops);
        var bot = new DungeonBot(true);
        DungeonSession s = d.Session;
        while (!s.Finished)
        {
            bool attack = bot.Next(s, out int dx, out int dy);
            s.Update(CombatScript.StepMs, dx, dy, attack);
        }
        // 在首领房里放一堆金币在可走区域外(进深很远),自动机追不到:追 GoldChaseSteps 步后放弃
        RoomCombatSession last = s.Current;
        last.SpawnGold(10, last.Hero.X, last.Hero.Y + 5000);
        last.Update(1500, 0, 0);                         // 让它落地
        Assert.True(last.Drops.All(p => p.Landed));
        int guard = 0;
        while (bot.WantsToCollect(s) && guard++ < DungeonBot.GoldChaseSteps + 50)
        {
            bool attack = bot.Next(s, out int dx, out int dy);
            Assert.False(attack);
            s.Update(CombatScript.StepMs, dx, dy, attack);
        }
        Assert.False(bot.WantsToCollect(s));
        Assert.True(guard <= DungeonBot.GoldChaseSteps + 1, "追了 " + guard + " 步");
    }

    [Fact]
    public void TheRewardPanelCharactersAreAllInThePixelFont()
    {
        foreach (char c in "CLEAR TIME 0123456789: KILLS EXP + GOLD ITEMS")
        {
            Assert.True(OverlayArt.HasGlyph(c), "字库里没有 '" + c + "'");
        }
    }

    [Fact]
    public void TheSettlementShowsFivePlaceholderLinesOfTheRunTotalsAndNotBefore()
    {
        using var d = new Dungeon(TwoRoomBossLayout(), (cx, cy) => None());
        DungeonSession s = d.Session;
        for (int i = 0; i < 400 && !s.BossCleared; i++)
        {
            s.Update(CombatScript.StepMs, 1, 0);
        }
        Assert.True(s.BossCleared);
        Assert.DoesNotContain(s.Build(0), x => x.AnimationPath == "reward");
        for (int i = 0; i < 400 && !s.SettlementActive; i++)
        {
            s.Update(CombatScript.StepMs, 0, 0);
        }
        Assert.True(s.SettlementActive);
        List<SceneSprite> after = s.Build(0);
        List<SceneSprite> lines = after.Where(x => x.AnimationPath == "reward").ToList();
        Assert.Equal(5, lines.Count);
        Assert.All(lines, x => Assert.Equal((DungeonSession.RewardPanelLeft, OverlayArt.UiRank), (x.ScreenX, x.LayerRank)));
        Assert.Equal(DungeonSession.RewardPanelTop, lines[0].ScreenY);
        Assert.True(lines[0].ScreenY < lines[1].ScreenY && lines[1].ScreenY < lines[2].ScreenY && lines[2].ScreenY < lines[3].ScreenY && lines[3].ScreenY < lines[4].ScreenY);
    }
}
