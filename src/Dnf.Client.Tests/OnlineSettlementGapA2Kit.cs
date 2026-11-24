using Dnf.Net.Frame;
using Dnf.Net.Session;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网结算控制器"区域 A 第二轮缺口补测"(OnlineSettlementGapA2*)共用的小夹具:
/// 脱敏日志行的字面量常量(整行、含前缀,手算写出,不从生产代码取)、整份日志序列的拼接小工具、几种"发送失败"任务的构造。
/// 驱动控制器的场景台复用上一轮的 <see cref="OnlineSettlementGapAKit.DirectRig"/> 与 <see cref="OnlineSettlementGapAKit.FakeSettlementTransport"/>(只用不改)。
/// 所有编号、数值都是合成的。
/// </summary>
internal static class OnlineSettlementGapA2Kit
{
    // ----- 结算开始 -----
    /// <summary>非超时结算开始的"展示"日志(<see cref="DirectRig"/> 里没有联网房间局:解析状态记"不适用"、没有掉落、没有差异字段)。</summary>
    public const string Show = "DNF_ONLINE stage=settle event=show timedout=false result=NotApplicable reward=NotApplicable drops=0 diffs=0";

    /// <summary>"结算通知超时"那种数据的展示日志(解析状态都是"不适用"、没有掉落、没有差异字段)。</summary>
    public const string ShowTimedOut = "DNF_ONLINE stage=settle event=show timedout=true result=NotApplicable reward=NotApplicable drops=0 diffs=0";

    /// <summary>"结算通知超时"路径开局写的失败行。</summary>
    public const string TimeoutNotice = "DNF_ONLINE stage=settle event=fail reason=settlement_timeout step=notices";

    // ----- 自动推进 1/72 → 1/73 → 1/74 -----
    public const string Sent72 = "DNF_ONLINE stage=settle event=advance step=score_scroll status=sent";
    public const string Ok72 = "DNF_ONLINE stage=settle event=advance step=score_scroll status=ok";
    public const string Sent73 = "DNF_ONLINE stage=settle event=advance step=card_right status=sent";
    public const string Ok73 = "DNF_ONLINE stage=settle event=advance step=card_right status=ok";
    public const string Sent74 = "DNF_ONLINE stage=settle event=advance step=select_card status=sent";
    public const string Ok74 = "DNF_ONLINE stage=settle event=advance step=select_card status=ok";
    public const string SkipNoRight = "DNF_ONLINE stage=settle event=advance step=card_skip status=no_right";

    // ----- 玩家选择 -----
    public const string ChooseRetryByPlayer = "DNF_ONLINE stage=settle event=choose choice=Retry source=player";
    public const string ChooseSelectDungeonByPlayer = "DNF_ONLINE stage=settle event=choose choice=SelectDungeon source=player";
    public const string ChooseReturnByPlayer = "DNF_ONLINE stage=settle event=choose choice=ReturnToTown source=player";
    public const string ChooseReturnByCountdown = "DNF_ONLINE stage=settle event=choose choice=ReturnToTown source=timeout";

    // ----- 常见收尾 -----
    public const string TownConfirmedByNoti2 = "DNF_ONLINE stage=settle event=town reason=confirmed area=pre_dungeon signal=noti2";

    /// <summary>标准推进成功的七行:展示 → 72 发/收 → 73 发/收 → 74 发 → 74 发送成功(此后菜单就绪)。</summary>
    public static readonly string[] StandardPreamble = { Show, Sent72, Ok72, Sent73, Ok73, Sent74, Ok74 };

    /// <summary>把若干行(或行数组)按顺序拼成期望的整份日志序列。</summary>
    public static string[] Seq(params object[] parts)
    {
        var list = new List<string>();
        foreach (object part in parts)
        {
            if (part is string line)
            {
                list.Add(line);
            }
            else if (part is IEnumerable<string> lines)
            {
                list.AddRange(lines);
            }
            else
            {
                throw new ArgumentException("只接受字符串或字符串序列", nameof(parts));
            }
        }
        return list.ToArray();
    }

    /// <summary>
    /// 标准推进到菜单就绪:服务端对 72 回 <c>[1]</c>、对 73 回"第 0 槽有权",对 1/75 的应答交给 <paramref name="onEplp"/>(null 表示静默)。
    /// 时间线:空闲轮询 1000、开始结算 1000;1016 排空 72/73 回复并发 74;1032 读到 74 发送成功,菜单就绪、倒计时起算(到点 6032)。
    /// </summary>
    public static void ReachMenu(DirectRig rig, Action<byte[]> onEplp = null)
    {
        AutoAdvance(rig.Transport, CardRightBytes(1), onEplp);
        rig.PollAt(1000);
        rig.StartSettlement();
        rig.PollUntil(() => rig.Ctrl.Phase == OnlineSettlementPhase.MenuReady);
        Assert.Equal(1032, rig.NowMs);
        Assert.Equal(StandardPreamble, AllLogs(rig));
    }

    /// <summary>控制器写出的全部日志(按写出顺序)。</summary>
    public static string[] AllLogs(DirectRig rig) => rig.Logs.ToArray();

    /// <summary>已发出命令的包号序列。</summary>
    public static ushort[] SentIds(DirectRig rig) => rig.Transport.Sent.Select(s => s.PacketId).ToArray();

    /// <summary>
    /// 服务端对 1/75 的"标准确认":回 <c>[1, 1, cmd]</c>(执行确认)并推 0/2(回城信号)。
    /// </summary>
    public static void ConfirmWithNoti2(FakeSettlementTransport transport, byte[] eplpPayload)
    {
        transport.Push(PacketClass.Cmd, 75, EplpOk(eplpPayload[1]));
        PushNoti2(transport);
    }

    /// <summary>五种"这次发送失败了"的任务:非 Ok 状态、任务出错、任务被取消、返回空任务、同步抛异常(同步抛由调用者在回调里直接调用本方法的 throw 分支)。</summary>
    public static Task<FrameStatus> FailedSend(string kind)
    {
        switch (kind)
        {
            case "status":
                return Task.FromResult(FrameStatus.NoCipher);
            case "faulted":
                return Task.FromException<FrameStatus>(new IOException("synthetic write failure"));
            case "canceled":
                return Task.FromCanceled<FrameStatus>(new CancellationToken(true));
            case "null":
                return null;
            default:
                throw new InvalidOperationException("synthetic synchronous failure");
        }
    }
}
