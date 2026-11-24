using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 结算菜单"鼠标悬停行"(<see cref="OnlineSettlementController.HoverRow"/>,公开属性)的默认值与跟随规则:
/// 新建控制器、开了结算但还没挪过鼠标、结算收尾复位之后、鼠标在菜单外,全是 -1;鼠标移到第 n 行是 n;再离开回到 -1;
/// 已经有待决选择时挪鼠标不改悬停行。
/// 菜单几何(来自 <see cref="SettlementArt"/>):面板横向 [512, 512+270),四行纵向从 76 起每行高 30,即 [76, 196);
/// 点 (x, y) 在面板内且 y 落在第 n 行内时,悬停行 = (y-76)/30 的整数部分,否则 -1。
/// 全部用 <see cref="DirectRig"/>(手动逻辑时钟),期望值是手算的字面量。
/// </summary>
public sealed class OnlineSettlementGapA2HoverRowTests
{
    private const int Left = SettlementArt.PanelX;                          // 512
    private const int Right = SettlementArt.PanelX + SettlementArt.PanelWidth;   // 782(不含)
    private const int Top = SettlementArt.RowTop;                           // 76
    private const int Bottom = SettlementArt.RowTop + SettlementArt.RowHeight * SettlementArt.RowCount;   // 196(不含)

    private static int RowY(int row) => Top + SettlementArt.RowHeight * row + 5;

    private static void ReachMenu(DirectRig rig, long startAtMs)
    {
        AutoAdvance(rig.Transport, CardRightBytes(1), payload =>
        {
            rig.Transport.Push(PacketClass.Cmd, 75, EplpOk(payload[1]));
            PushNoti2(rig.Transport);
        });
        rig.PollAt(startAtMs);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
    }

    [Fact]
    public void A_new_controller_and_a_freshly_started_settlement_have_no_hover_row_before_any_pointer_event()
    {
        // 保护:悬停行的初始值是 -1(不是别的负数):新建控制器后什么都没做就读是 -1;
        // 开了一次结算、推进到菜单就绪,期间一次鼠标事件都没有,读到的仍是 -1(开始结算本身不会替它设值,所以这里读到的就是构造时的默认值)。
        using var rig = new DirectRig();
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        Assert.False(rig.Ctrl.IsActive);

        rig.PollAt(1000);
        Assert.Equal(-1, rig.Ctrl.HoverRow);   // 空闲轮询不改悬停行

        AutoAdvance(rig.Transport, CardRightBytes(1));
        rig.StartSettlement();
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        Assert.Equal(OnlineSettlementPhase.MenuReady, rig.Ctrl.Phase);
    }

    [Fact]
    public void A_controller_disposed_without_any_settlement_still_reads_minus_one()
    {
        // 保护:没做过任何结算的控制器直接释放,悬停行读出来仍是 -1(释放走的复位也把它放回 -1)。
        var rig = new DirectRig();
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        rig.Dispose();
        Assert.Equal(-1, rig.Ctrl.HoverRow);
    }

    [Fact]
    public void The_hover_row_follows_the_pointer_over_each_menu_row_and_is_minus_one_outside_the_menu()
    {
        // 保护:菜单显示期间,鼠标在第 n 行内悬停行就是 n(四行都测,含行与行的交界与面板四边的内外两侧);
        // 在面板外(左/右/上/下各一个点)或离开菜单是 -1;ClearPointer 也回到 -1。
        using var rig = new DirectRig();
        ReachMenu(rig, 1000);

        for (int row = 0; row < SettlementArt.RowCount; row++)
        {
            rig.Ctrl.SetPointer(Left + 10, RowY(row));
            Assert.Equal(row, rig.Ctrl.HoverRow);
            rig.Ctrl.SetPointer(Left + 10, Top - 1);    // 离开(在第一行上方)
            Assert.Equal(-1, rig.Ctrl.HoverRow);
        }

        // 面板四边:内侧最后一个像素在某一行里,外侧第一个像素在菜单外
        rig.Ctrl.SetPointer(Left, Top);
        Assert.Equal(0, rig.Ctrl.HoverRow);                                   // 左上角内侧 (512, 76)
        rig.Ctrl.SetPointer(Right - 1, Bottom - 1);
        Assert.Equal(3, rig.Ctrl.HoverRow);                                   // 右下角内侧 (781, 195)
        rig.Ctrl.SetPointer(Left - 1, RowY(0));
        Assert.Equal(-1, rig.Ctrl.HoverRow);                                  // 左边外侧 511
        rig.Ctrl.SetPointer(Right, RowY(0));
        Assert.Equal(-1, rig.Ctrl.HoverRow);                                  // 右边外侧 782
        rig.Ctrl.SetPointer(Left + 10, Top - 1);
        Assert.Equal(-1, rig.Ctrl.HoverRow);                                  // 上边外侧 75
        rig.Ctrl.SetPointer(Left + 10, Bottom);
        Assert.Equal(-1, rig.Ctrl.HoverRow);                                  // 下边外侧 196

        // 行与行的交界:第 0 行最后一个像素 (76+30-1 = 105) 仍是 0,下一个像素 106 就是第 1 行
        rig.Ctrl.SetPointer(Left + 10, Top + SettlementArt.RowHeight - 1);
        Assert.Equal(0, rig.Ctrl.HoverRow);
        rig.Ctrl.SetPointer(Left + 10, Top + SettlementArt.RowHeight);
        Assert.Equal(1, rig.Ctrl.HoverRow);

        // 禁用的行(第 1 行快速组队)照样有悬停行号(是否高亮由渲染决定,不由控制器决定)
        rig.Ctrl.SetPointer(Left + 10, RowY(1));
        Assert.Equal(1, rig.Ctrl.HoverRow);

        rig.Ctrl.SetPointer(Left + 10, RowY(2));
        Assert.Equal(2, rig.Ctrl.HoverRow);
        rig.Ctrl.ClearPointer();
        Assert.Equal(-1, rig.Ctrl.HoverRow);
    }

    [Fact]
    public void A_pending_choice_freezes_the_hover_row_at_minus_one_and_a_finished_settlement_resets_it()
    {
        // 保护:发出选择那一刻悬停行复位成 -1,之后(等回复/等回城期间)鼠标怎么挪都保持 -1;
        // 结算收尾复位后读到 -1,空闲时挪鼠标也是 -1;同一控制器的第二次结算开始时仍是 -1,第二次菜单里照常跟随鼠标。
        using var rig = new DirectRig();
        ReachMenu(rig, 1000);

        rig.Ctrl.SetPointer(Left + 10, RowY(3));
        Assert.Equal(3, rig.Ctrl.HoverRow);
        Assert.True(rig.Ctrl.Choose(SettlementChoice.ReturnToTown));
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        rig.Ctrl.SetPointer(Left + 10, RowY(0));
        Assert.Equal(-1, rig.Ctrl.HoverRow);   // 有待决选择:不跟随
        rig.Ctrl.SetPointer(Left + 10, RowY(2));
        Assert.Equal(-1, rig.Ctrl.HoverRow);

        rig.Poll();   // 读到 1/75 成功 + 0/2:结算收尾
        Assert.Equal(new[] { SettlementChoice.ReturnToTown }, rig.Completed);
        Assert.False(rig.Ctrl.IsActive);
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        rig.Ctrl.SetPointer(Left + 10, RowY(2));
        Assert.Equal(-1, rig.Ctrl.HoverRow);   // 空闲:不跟随

        // 第二次结算
        rig.Transport.ClearSent();
        AutoAdvance(rig.Transport, CardRightBytes(1));
        rig.StartSettlement();
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        rig.Ctrl.SetPointer(Left + 10, RowY(2));
        Assert.Equal(2, rig.Ctrl.HoverRow);
        rig.Ctrl.SetPointer(Left + 10, RowY(0));
        Assert.Equal(0, rig.Ctrl.HoverRow);
    }

    [Fact]
    public void A_settlement_replaced_while_the_pointer_hovers_a_row_starts_with_no_hover_row()
    {
        // 保护:菜单里鼠标正悬停在某一行时,同一控制器又收到下一次结算(上一次没结束):旧结算整体复位,
        // 新结算开始时悬停行是 -1(旧的悬停行不残留)。
        using var rig = new DirectRig();
        ReachMenu(rig, 1000);
        rig.Ctrl.SetPointer(Left + 10, RowY(2));
        Assert.Equal(2, rig.Ctrl.HoverRow);

        rig.Transport.ClearSent();
        AutoAdvance(rig.Transport, CardRightBytes(1));
        rig.StartSettlement();
        Assert.Equal(OnlineSettlementPhase.AdvancingScoreScroll, rig.Ctrl.Phase);
        Assert.Equal(-1, rig.Ctrl.HoverRow);
        Assert.Empty(rig.Completed);   // 被替换的那次结算没有交还选择
    }
}
