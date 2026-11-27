using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网外壳"创建/删除角色"流程的收尾补充测试(r21-createcharui2,同一个类的部分文件,共用 <c>OnlineShellFlowManageEdgeTests.cs</c> 里的夹具):
/// 点击要经流程送进已打开的模态;打开模态要把选角界面上的旧提示收掉。
/// 全部走内存里的假后端与合成数据,名字都是编的,不触发任何真实创建或删除。
/// </summary>
public sealed partial class OnlineShellFlowManageEdgeTests
{
    // ------------------------------------------------------------------ 点击经流程送进已开的模态

    /// <summary>
    /// 创建模态开着时,流程把点击交给它:点职业格选中那一格,点确定按钮提交;
    /// 提交带出去的职业与名字是点选之后的值。流程若把点击吞掉不转交,模态就收不到任何点击。
    /// </summary>
    [Fact]
    public void Clicks_on_an_open_create_modal_reach_its_job_cells_and_its_confirm_button_through_the_flow()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));
        rig.ClickCreate();
        Assert.NotNull(rig.Flow.CreateModal);
        Assert.Equal(0, rig.Flow.CreateModal.SelectedJob);

        ClickCenter(rig.Flow, CharacterCreateModal.JobRect(4));
        Assert.Equal(4, rig.Flow.CreateModal.SelectedJob);

        rig.Flow.Type("HeroNew");
        ClickCenter(rig.Flow, CharacterCreateModal.ConfirmButtonRect);
        rig.Pump(3);

        Assert.Equal(1, rig.Backend.CreateCalls);
        Assert.Equal((byte)4, rig.Backend.LastCreateJob);
        Assert.Equal("HeroNew", rig.Backend.LastCreateName);
    }

    /// <summary>删除模态开着时同理:输完确认名,点确定按钮提交,请求带的是目标槽位与输入的确认名。</summary>
    [Fact]
    public void A_click_on_the_confirm_button_of_an_open_delete_modal_reaches_it_through_the_flow()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        rig.Flow.Key(OnlineShellKey.Right);
        rig.ClickDelete();
        Assert.NotNull(rig.Flow.DeleteModal);
        rig.Flow.Type("HeroB");

        ClickCenter(rig.Flow, CharacterDeleteModal.ConfirmButtonRect);
        rig.Pump(3);

        Assert.Equal(1, rig.Backend.DeleteCalls);
        Assert.Equal((byte)1, rig.Backend.LastDeleteSlot);
        Assert.Equal("HeroB", rig.Backend.LastDeleteConfirmName);
    }

    /// <summary>两个模态的取消按钮,点一下都要把模态关掉,而且不碰后端。</summary>
    [Fact]
    public void Clicking_cancel_on_either_open_modal_closes_it_without_touching_the_backend()
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10));

        rig.ClickCreate();
        Assert.NotNull(rig.Flow.CreateModal);
        ClickCenter(rig.Flow, CharacterCreateModal.CancelButtonRect);
        rig.Pump(2);
        Assert.False(rig.Flow.HasCharacterModalOpen);

        rig.ClickDelete();
        Assert.NotNull(rig.Flow.DeleteModal);
        ClickCenter(rig.Flow, CharacterDeleteModal.CancelButtonRect);
        rig.Pump(2);
        Assert.False(rig.Flow.HasCharacterModalOpen);

        Assert.Equal(0, rig.Backend.CreateCalls);
        Assert.Equal(0, rig.Backend.DeleteCalls);
    }

    // ------------------------------------------------------------------ 打开模态收掉选角界面上的旧提示

    /// <summary>
    /// 选角失败后界面上留着一条提示(选不了这个角色)。打开创建或删除模态时,流程要把它收掉:
    /// 模态是新的一轮操作,上一轮的提示不能留在它下面。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Opening_either_modal_clears_the_notice_left_on_the_select_screen_by_a_failed_select(bool create)
    {
        Rig rig = CreateRig(allowWrite: true, wireManage: true, ("HeroA", 0, 10), ("HeroB", 1, 20));
        rig.Backend.OnSelect = (slot, ct) => Task.FromResult(OnlineSelectOutcome.Fail(OnlineNoticeKind.SelectFailed, true));
        rig.Flow.Key(OnlineShellKey.Enter);
        rig.Pump(3);
        Assert.Equal(OnlineShellStage.CharacterSelect, rig.Flow.Stage);
        Assert.Equal(OnlineNoticeKind.SelectFailed, rig.Flow.Notice.Kind);

        if (create)
        {
            rig.ClickCreate();
        }
        else
        {
            rig.ClickDelete();
        }

        Assert.True(rig.Flow.HasCharacterModalOpen);
        Assert.Null(rig.Flow.Notice);
    }
}
