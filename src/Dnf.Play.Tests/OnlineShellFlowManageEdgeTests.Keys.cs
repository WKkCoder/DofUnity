namespace Dnf.Play.Tests;

/// <summary>
/// 模态开着按空格的补充测试(r20fix-createcharui,簇 5b;夹具在 <c>OnlineShellFlowManageEdgeTests.cs</c>)。
/// 模态的 <c>Key</c> 对空格返回 false 会让按键落到选角画面自己的"空格 = 确认"上,那次确认被模态压住、
/// 等模态一关就会在下一次 <c>Poll</c> 里自己进游戏——所以关掉模态之后还要再多 <c>Poll</c> 几次才看得出来。
/// </summary>
public sealed partial class OnlineShellFlowManageEdgeTests
{
    [Fact]
    public void Space_pressed_while_the_create_modal_is_open_never_becomes_a_select_after_the_modal_closes()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        rig.ClickCreate();
        Assert.NotNull(rig.Flow.CreateModal);

        rig.Flow.Key(OnlineShellKey.Space);
        rig.Pump(3);
        Assert.True(rig.Flow.HasCharacterModalOpen);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Pump(6);

        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.SelectCalls);
        Assert.Equal(-1, rig.Flow.SelectedSlot);
        Assert.Single(rig.Flow.Characters.Entries);
    }

    [Fact]
    public void Space_pressed_while_the_delete_modal_is_open_never_becomes_a_select_after_the_modal_closes()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        rig.ClickDelete();
        Assert.NotNull(rig.Flow.DeleteModal);

        rig.Flow.Key(OnlineShellKey.Space);
        rig.Pump(3);
        Assert.True(rig.Flow.HasCharacterModalOpen);

        rig.Flow.Key(OnlineShellKey.Escape);
        rig.Pump(6);

        Assert.False(rig.Flow.HasCharacterModalOpen);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(0, rig.Backend.SelectCalls);
        Assert.Equal(-1, rig.Flow.SelectedSlot);
    }
}
