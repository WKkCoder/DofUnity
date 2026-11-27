using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// r22-giveup-signal(F3 契约):逐帧日志新增的几类行必须能过探针的转行白名单(<see cref="OnlineFightLog.ProbeText"/>),白名单仍然挡住其余行。
/// 白名单放在 Net 层、有测试守着,探针只管调用它;新行全部以 <c>DNF_NET stage=combat </c> 开头,不需要放宽白名单(这里守住"没放宽")。
/// </summary>
public class OnlineFrameLogContractTests
{
    private const string P = "DNF_NET stage=combat";

    public static TheoryData<string> NewLines()
    {
        return new TheoryData<string>
        {
            OnlineCombatLog.Frame(1, PacketClass.Noti, 9, 32, false),
            OnlineCombatLog.Frame(5, PacketClass.Noti, 3, 23, true),
            OnlineCombatLog.Frame(7, PacketClass.Cmd, 45, 13, false),
            OnlineCombatLog.EnvelopeOpened(4, PacketClass.Noti, 3, 23),
            OnlineCombatLog.EnvelopeUnopened(1, OnlineCombatLog.ReasonNoUnwrap),
            OnlineCombatLog.EnvelopeUnopened(1, OnlineCombatLog.ReasonUnwrapThrew),
            OnlineCombatLog.EnvelopeUnopened(1, OnlineCombatLog.ReasonUnwrapNull),
            OnlineCombatLog.EnvelopeUnopened(1, OnlineCombatLog.ReasonInnerHeaderBad),
            OnlineCombatLog.EnvelopeUnopenedCompressed(8, PacketClass.Noti, 2, 72),
            OnlineCombatLog.FramesOmitted(12),
        };
    }

    [Theory]
    [MemberData(nameof(NewLines))]
    public void The_probe_forwards_every_new_line_without_the_net_prefix(string netLine)
    {
        Assert.StartsWith(P + " ", netLine, StringComparison.Ordinal);
        Assert.Equal(netLine.Substring("DNF_NET ".Length), OnlineFightLog.ProbeText(netLine));
        Assert.StartsWith("stage=combat ", OnlineFightLog.ProbeText(netLine), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DNF_NET stage=combatx frame n=1 NOTI/9(32)")]          // 类别名后面必须跟空格
    [InlineData("DNF_NET stage=frame n=1 NOTI/9(32)")]                  // 不是 combat 类
    [InlineData("DNF_NET stage=envelope n=1 opened inner=NOTI/3(23)")]
    [InlineData("DNF_NET frame n=1 NOTI/9(32)")]                        // 没有 stage= 的不认
    [InlineData("DNF_NET envelope n=1 unopened reason=noUnwrap")]
    [InlineData("stage=combat frame n=1 NOTI/9(32)")]                   // 没有 DNF_NET 前缀的不认
    [InlineData(" DNF_NET stage=combat frame n=1 NOTI/9(32)")]
    [InlineData("PROBE_FIGHT stage=combat frame n=1 NOTI/9(32)")]
    [InlineData("DNF_NET stage=town step=Login")]
    [InlineData("DNF_NET stage=dungeon begin dungeon=901 mode=2")]       // 回显命令行里的副本编号与模式,仍然挡住
    [InlineData("DNF_NET stage=settle-menu step=72")]
    [InlineData("DNF_NET heartbeat answered=3")]
    [InlineData(null)]
    public void The_probe_still_blocks_every_other_line(string netLine)
    {
        Assert.Null(OnlineFightLog.ProbeText(netLine));
    }
}
