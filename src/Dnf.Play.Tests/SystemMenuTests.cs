using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 系统菜单(<see cref="SystemMenu"/>、<see cref="SystemMenuArt"/>,Esc 打开;本项目自己的占位设计)与它在流程里的执行(<see cref="GameFlow.ToggleMenu"/>、<see cref="GameFlow.Poll"/>)。
/// 会话是假的,画法只看精灵的组成,验证的是规则。
/// </summary>
public class SystemMenuTests
{
    private sealed class FakeRun : GameFlowTests.FakeHero, IRunSession
    {
        public FakeRun(string path, int run) : base("run " + path + " #" + run)
        {
        }

        public SettlementChoice Choice { get; set; }

        public bool Failed { get; set; }
    }

    private static readonly DungeonEntry A = new DungeonEntry("dungeon/test/a.dgn", "AAA");

    private static GameFlow NewFlow()
    {
        return new GameFlow(() => new GameFlowTests.FakeHero("town"), (path, run) => new FakeRun(path, run), new[] { A });
    }

    // ------------------------------------------------------------------ 菜单本身

    [Fact]
    public void TheItemsDependOnTheStageAndOnlyTownAndDungeonHaveAMenu()
    {
        Assert.Equal(new[] { SystemMenuAction.Resume, SystemMenuAction.ReturnToTown, SystemMenuAction.Quit }, SystemMenu.ItemsFor(GameStage.Dungeon));
        Assert.Equal(new[] { SystemMenuAction.Resume, SystemMenuAction.CharacterSelect, SystemMenuAction.Quit }, SystemMenu.ItemsFor(GameStage.Town));
        foreach (GameStage stage in new[] { GameStage.Loading, GameStage.DungeonSelect, GameStage.CharacterSelect })
        {
            var menu = new SystemMenu();
            Assert.Empty(SystemMenu.ItemsFor(stage));
            Assert.False(menu.Show(stage));
            Assert.False(menu.Open);
        }
    }

    [Fact]
    public void EveryItemHasALabelAndNoneHasNone()
    {
        foreach (SystemMenuAction a in new[] { SystemMenuAction.Resume, SystemMenuAction.ReturnToTown, SystemMenuAction.CharacterSelect, SystemMenuAction.Quit })
        {
            Assert.NotEmpty(SystemMenu.LabelOf(a));
            Assert.All(SystemMenu.LabelOf(a), c => Assert.True(OverlayArt.HasGlyph(c), "字库里没有 '" + c + "'"));
        }
        Assert.Equal(string.Empty, SystemMenu.LabelOf(SystemMenuAction.None));
    }

    [Fact]
    public void ShowOpensOnTheFirstRowAndMoveWrapsAroundOnlyWhileOpen()
    {
        var menu = new SystemMenu();
        menu.Move(1);
        Assert.Equal((false, 0), (menu.Open, menu.Selected));                // 没开:不动

        Assert.True(menu.Show(GameStage.Dungeon));
        Assert.Equal((true, 0, 3), (menu.Open, menu.Selected, menu.Items.Count));
        menu.Move(1);
        menu.Move(1);
        Assert.Equal(2, menu.Selected);
        menu.Move(1);
        Assert.Equal(0, menu.Selected);                                       // 到底回绕
        menu.Move(-1);
        Assert.Equal(2, menu.Selected);
        menu.Move(-4);
        Assert.Equal(1, menu.Selected);
    }

    [Fact]
    public void ChooseClosesTheMenuAndHandsOverTheActionExactlyOnce()
    {
        var menu = new SystemMenu();
        menu.Show(GameStage.Dungeon);
        menu.Move(1);
        menu.Choose();
        Assert.Equal((false, 0), (menu.Open, menu.Items.Count));
        Assert.Equal(SystemMenuAction.ReturnToTown, menu.TakeAction());
        Assert.Equal(SystemMenuAction.None, menu.TakeAction());               // 只给一次
        menu.Choose();                                                        // 没开:什么也不发生
        Assert.Equal(SystemMenuAction.None, menu.TakeAction());
    }

    [Fact]
    public void NumberKeysAreOneBasedAndOutOfRangeNumbersAreIgnored()
    {
        var menu = new SystemMenu();
        menu.Show(GameStage.Town);
        menu.ChooseNumber(0);
        menu.ChooseNumber(4);
        Assert.True(menu.Open);
        menu.ChooseNumber(2);
        Assert.Equal(SystemMenuAction.CharacterSelect, menu.TakeAction());

        menu.Show(GameStage.Town);
        menu.ChooseNumber(3);
        Assert.Equal(SystemMenuAction.Quit, menu.TakeAction());
    }

    [Fact]
    public void ThePointerSelectsRowsAndAClickChoosesOneButAClickElsewhereDoesNothing()
    {
        var menu = new SystemMenu();
        menu.Show(GameStage.Dungeon);
        (int x, int y, int w, int h) = menu.RowRect(1);
        menu.SetPointer(x + w / 2, y + h / 2);
        Assert.Equal(1, menu.Selected);
        menu.SetPointer(2, 2);                                                // 不在任何一行:选中行不变
        Assert.Equal(1, menu.Selected);

        menu.Click(2, 2);
        Assert.Equal((true, SystemMenuAction.None), (menu.Open, menu.TakeAction()));   // 点在别处没有反应,也不关菜单
        (int x2, int y2, int w2, int h2) = menu.RowRect(2);
        menu.Click(x2 + 1, y2 + h2 - 1);
        Assert.Equal((false, SystemMenuAction.Quit), (menu.Open, menu.TakeAction()));
    }

    [Fact]
    public void TheRowsSitInsideACenteredPanelAndDoNotOverlap()
    {
        var menu = new SystemMenu();
        menu.Show(GameStage.Dungeon);
        Assert.Equal(SystemMenu.ScreenWidth, menu.PanelX * 2 + SystemMenu.PanelWidth);
        Assert.Equal(SystemMenu.ScreenHeight, menu.PanelY * 2 + menu.PanelHeight);
        int previousBottom = menu.PanelY + SystemMenu.TitleHeight;
        for (int i = 0; i < menu.Items.Count; i++)
        {
            (int x, int y, int w, int h) = menu.RowRect(i);
            Assert.True(x >= menu.PanelX && x + w <= menu.PanelX + SystemMenu.PanelWidth);
            Assert.True(y >= previousBottom);
            Assert.True(y + h <= menu.PanelY + menu.PanelHeight);
            previousBottom = y + h;
            Assert.Equal(i, menu.RowAt(x, y));
            Assert.Equal(i, menu.RowAt(x + w - 1, y + h - 1));
            Assert.Equal(-1, menu.RowAt(x + w, y));
        }
    }

    [Fact]
    public void ShowAndCloseDropAnUntakenChoice()
    {
        var menu = new SystemMenu();
        menu.Show(GameStage.Dungeon);
        menu.Choose();
        menu.Show(GameStage.Dungeon);                                         // 重新打开:上一次没取走的选择作废
        Assert.Equal(SystemMenuAction.None, menu.TakeAction());
        menu.Choose();
        menu.Close();
        Assert.Equal(SystemMenuAction.None, menu.TakeAction());
        Assert.Equal((false, 0), (menu.Open, menu.Selected));
    }

    // ------------------------------------------------------------------ 画法

    private static bool HasInk(SpriteFrame frame, uint rgb)
    {
        for (int i = 0; i + 3 < frame.Rgba.Length; i += 4)
        {
            if (frame.Rgba[i + 3] != 0 && frame.Rgba[i] == (byte)(rgb >> 16) && frame.Rgba[i + 1] == (byte)(rgb >> 8) && frame.Rgba[i + 2] == (byte)rgb)
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void AClosedMenuDrawsNothingAndAnOpenOneDrawsDimFramePanelTitleAndRows()
    {
        var art = new OverlayArt();
        var menu = new SystemMenu();
        Assert.Empty(SystemMenuArt.Build(menu, art));

        menu.Show(GameStage.Dungeon);
        menu.Move(1);
        List<SceneSprite> sprites = SystemMenuArt.Build(menu, art);
        string[] tags = sprites.Select(s => s.AnimationPath ?? string.Empty).ToArray();
        Assert.Equal(new[] { "menu-dim", "menu-frame", "menu-panel", "menu-title", "menu-row", "menu-label", "menu-row-selected", "menu-label", "menu-row", "menu-label" }, tags);

        SceneSprite panel = sprites.First(s => s.AnimationPath == "menu-panel");
        Assert.Equal((menu.PanelX, menu.PanelY), (panel.ScreenX, panel.ScreenY));
        SceneSprite selected = sprites.First(s => s.AnimationPath == "menu-row-selected");
        (int rx, int ry, _, _) = menu.RowRect(1);
        Assert.Equal((rx, ry), (selected.ScreenX, selected.ScreenY));
        // 选中行的字是黄的,别的行是白的
        List<SceneSprite> labels = sprites.Where(s => s.AnimationPath == "menu-label").ToList();
        Assert.True(HasInk(labels[1].Frame, OverlayArt.Yellow));
        Assert.False(HasInk(labels[0].Frame, OverlayArt.Yellow));
        Assert.True(HasInk(labels[0].Frame, OverlayArt.White));
    }

    [Fact]
    public void TheLabelsAreCenteredInTheirRows()
    {
        var art = new OverlayArt();
        var menu = new SystemMenu();
        menu.Show(GameStage.Town);
        List<SceneSprite> labels = SystemMenuArt.Build(menu, art).Where(s => s.AnimationPath == "menu-label").ToList();
        Assert.Equal(3, labels.Count);
        for (int i = 0; i < labels.Count; i++)
        {
            (int x, int y, int w, int h) = menu.RowRect(i);
            Assert.Equal(x + (w - labels[i].Frame.Width) / 2, labels[i].ScreenX);
            Assert.Equal(y + (h - labels[i].Frame.Height) / 2, labels[i].ScreenY);
        }
    }

    // ------------------------------------------------------------------ 在流程里

    [Fact]
    public void EscOnlyOpensTheMenuInTownAndDungeonAndTogglesItShut()
    {
        GameFlow flow = NewFlow();
        Assert.False(flow.ToggleMenu());                                      // 还没开始

        flow.EnterTown("start");
        Assert.True(flow.ToggleMenu());
        Assert.True(flow.Menu.Open);
        Assert.True(flow.ToggleMenu());
        Assert.False(flow.Menu.Open);

        flow.EnterSelect();
        Assert.False(flow.ToggleMenu());                                      // 副本选择界面里 Esc 另有含义(返回城镇)
        Assert.False(flow.Menu.Open);

        flow.StartDungeon(A.Path, "start");
        Assert.True(flow.ToggleMenu());
        Assert.Equal(SystemMenu.ItemsFor(GameStage.Dungeon), flow.Menu.Items);
    }

    [Fact]
    public void ResumeJustClosesTheMenuAndTheStageStaysTheSame()
    {
        GameFlow flow = NewFlow();
        flow.StartDungeon(A.Path, "start");
        int serial = flow.StageSerial;
        flow.ToggleMenu();
        flow.Menu.ChooseNumber(1);
        Assert.False(flow.Poll());
        Assert.Equal((GameStage.Dungeon, serial, 0, 0), (flow.Stage, flow.StageSerial, flow.GiveUps, flow.Failures));
    }

    [Fact]
    public void ReturnToTownFromTheMenuGivesUpTheRunWithoutASettlementOrAFailure()
    {
        GameFlow flow = NewFlow();
        flow.StartDungeon(A.Path, "start");
        flow.ToggleMenu();
        flow.Menu.ChooseNumber(2);
        Assert.True(flow.Poll());
        Assert.Equal(GameStage.Town, flow.Stage);
        Assert.Null(flow.Run);
        Assert.Equal((1, 0, 0, 1), (flow.GiveUps, flow.Settlements, flow.Failures, flow.Runs));
        Assert.Equal("menu return", flow.History[^1].Reason);
        Assert.False(flow.Menu.Open);
        Assert.False(flow.Poll());                                            // 选择只执行一次
    }

    [Fact]
    public void QuitOnlyRaisesTheFlagAndLeavesTheStageAlone()
    {
        GameFlow flow = NewFlow();
        flow.EnterTown("start");
        flow.ToggleMenu();
        flow.Menu.ChooseNumber(3);
        Assert.False(flow.Poll());
        Assert.Equal((GameStage.Town, true), (flow.Stage, flow.QuitRequested));
    }

    [Fact]
    public void CharacterSelectFromTheTownMenuIsIgnoredWhenTheFlowHasNoSuchScreen()
    {
        GameFlow flow = NewFlow();
        flow.EnterTown("start");
        flow.ToggleMenu();
        flow.Menu.ChooseNumber(2);
        Assert.False(flow.Poll());
        Assert.Equal(GameStage.Town, flow.Stage);
    }

    [Fact]
    public void AnOpenMenuHoldsBackTheStageResultsUntilItIsClosed()
    {
        GameFlow flow = NewFlow();
        flow.StartDungeon(A.Path, "start");
        var run = (FakeRun)flow.Run;
        flow.ToggleMenu();
        run.Choice = SettlementChoice.ReturnToTown;
        run.Failed = true;
        Assert.False(flow.Poll());                                            // 菜单开着:游戏暂停,失败与结算选择先不处理
        Assert.Equal((GameStage.Dungeon, 0), (flow.Stage, flow.Failures));

        flow.ToggleMenu();
        Assert.True(flow.Poll());
        Assert.Equal((GameStage.Town, 1, 0), (flow.Stage, flow.Failures, flow.Settlements));   // 失败优先于结算选择
    }

    [Fact]
    public void ASwitchOfStageClosesTheMenuAndDropsAnUntakenChoice()
    {
        GameFlow flow = NewFlow();
        flow.EnterTown("start");
        flow.ToggleMenu();
        flow.EnterSelect();
        Assert.False(flow.Menu.Open);

        flow.EnterTown("back");
        flow.ToggleMenu();
        flow.Menu.ChooseNumber(3);                                            // 选了退出,还没轮询
        flow.EnterSelect();                                                   // 这时换了阶段:选择作废
        Assert.False(flow.Poll());
        Assert.False(flow.QuitRequested);
    }
}
