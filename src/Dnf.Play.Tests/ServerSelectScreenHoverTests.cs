using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 验收后补的选服务器界面悬停测试(变异存活项):指针从一个入口移到按钮、空白处或画面外之后,入口的悬停光必须灭掉
/// (而不是退回"悬停在 0 号入口")。精灵标签与位置按 <see cref="ServerSelectScreen.Build"/> 的现状;位置用手算的字面量。
/// </summary>
public sealed class ServerSelectScreenHoverTests
{
    private static DirectoryServer Ok(string name) => PickerData.Server(name, 2);

    private static ServerSelectScreen Ready(ServerDirectory directory, int select, ShellFakeSprites art)
    {
        var screen = new ServerSelectScreen(new ServerPickerScene(art));
        screen.SetDirectory(directory, select);
        return screen;
    }

    [Fact]
    public void The_hover_light_goes_out_when_the_pointer_leaves_an_entry_for_a_button_blank_space_or_the_outside()
    {
        ShellFakeSprites art = PickerSprites.Full();
        // 三个都能选的入口,选中 2 号:0 号与 1 号都是"可选、没选中",指到谁谁就该亮;指到别处就一个都不该亮
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("希洛克"), Ok("卡恩"), Ok("普雷")), 2, art);
        Assert.Equal(2, screen.Selected);
        Assert.False(PickerRig.Has(screen.Build(0), "server-entry-hover"));

        (int X, int Y)[] elsewhere =
        {
            (400, 540),         // "游戏开始"按钮里(按钮 337..461 × 526..568)
            (5, 5),             // 画面左上角的空白
            (506, 230),         // 0 号与 1 号入口之间的缝(0 号到 501,1 号从 512 起)
            (340, 400),         // 入口下面的空地
        };
        foreach ((int x, int y) in elsewhere)
        {
            screen.Hover(520, 220);                                       // 先指到 1 号入口:恰好一个悬停光,落在 1 号入口的左上角
            SceneSprite lit = PickerSprites.One(screen.Build(0), "server-entry-hover");
            Assert.Equal((512, 214), (lit.ScreenX, lit.ScreenY));

            screen.Hover(x, y);                                           // 挪到别处:悬停光要灭,尤其不能落到 0 号入口 (332,214) 上
            Assert.False(PickerRig.Has(screen.Build(0), "server-entry-hover"), "指针在 (" + x + "," + y + ") 时不该有入口悬停光");
        }

        screen.Hover(340, 220);                                           // 指到 0 号入口:它没选中、能选,亮在 (332,214)
        SceneSprite first = PickerSprites.One(screen.Build(0), "server-entry-hover");
        Assert.Equal((332, 214), (first.ScreenX, first.ScreenY));
        screen.Hover(520, 220);
        SceneSprite second = PickerSprites.One(screen.Build(0), "server-entry-hover");
        Assert.Equal((512, 214), (second.ScreenX, second.ScreenY));       // 换到 1 号:光跟着走,不会两个一起亮
        screen.HoverNone();
        Assert.False(PickerRig.Has(screen.Build(0), "server-entry-hover"));
    }

    [Fact]
    public void A_fresh_screen_and_a_pointer_that_never_touched_an_entry_have_no_entry_hover_light()
    {
        ShellFakeSprites art = PickerSprites.Full();
        ServerSelectScreen screen = Ready(PickerData.Directory(Ok("希洛克"), Ok("卡恩"), Ok("普雷")), 2, art);

        screen.Hover(400, 540);                                           // 第一次就指到按钮上:没有入口被悬停,0 号入口也不例外
        Assert.False(PickerRig.Has(screen.Build(0), "server-entry-hover"));
        screen.Hover(5, 5);
        Assert.False(PickerRig.Has(screen.Build(0), "server-entry-hover"));
    }
}
