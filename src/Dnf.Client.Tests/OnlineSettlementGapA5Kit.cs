using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;
using static Dnf.Client.Tests.OnlineSettlementGapAKit;

namespace Dnf.Client.Tests;

/// <summary>
/// 联网结算控制器"区域 A 第五轮缺口补测"(OnlineSettlementGapA5*)共用夹具:
/// 控制器交给宿主绘制钩子的视图快照读取小工具(倒计时条是否画出、倒计时文字、四行菜单的颜色、提示条是否有)、
/// 带自己画布的 <see cref="DirectRig"/> 构造、发送失败/服务端回城信号的小帮手。
/// 场景台复用前几轮的 <see cref="OnlineSettlementGapAKit.DirectRig"/> 与 <see cref="OnlineSettlementGapAKit.FakeSettlementTransport"/>(只用不改);
/// 所有编号、数值都是合成的,期望值由注释里的算式手算得出。
/// </summary>
internal static class OnlineSettlementGapA5Kit
{
    // ----- 视图精灵标签(来自覆盖层/菜单面板的占位绘制,无字体、无图包时的版本) -----
    public const string CountdownBarTag = "settle-countdown-bg";
    public const string CountdownTextTag = "settle-countdown";
    public const string LabelTag = "settle-label";
    public const string NoticeTag = "onlinesettle-notice";

    /// <summary>禁用行的灰色(占位面板里对"不可选"的行统一用它)。</summary>
    public const uint Gray = 0x808080u;

    /// <summary>倒计时剩余 10 秒及以内的"紧迫"颜色与其余时候的颜色。</summary>
    public const uint UrgentColor = 0xFFB060u;
    public const uint CalmColor = 0xFFE9A0u;

    /// <summary>占位面板四行的文字(重试 / 快速组队 / 选副本 / 回城)。</summary>
    public static readonly string[] RowNames = { "RETRY", "PARTY", "SELECT", "TOWN" };

    /// <summary>造一个带"自己的画布"的场景台(控制器与测试读到的是同一块画布,同文字同倍数同颜色拿到同一个帧对象)。</summary>
    public static DirectRig NewRig(out OverlayArt art, Action<OnlineSettlementOptions> configure = null)
    {
        OverlayArt own = new OverlayArt();
        art = own;
        return new DirectRig(o =>
        {
            o.Art = own;
            configure?.Invoke(o);
        });
    }

    /// <summary>取控制器当前交给宿主的视图精灵列表(控制器空闲时是 null)。</summary>
    public static List<SceneSprite> Hud(DirectRig rig, long timeMs = 0) => rig.Ctrl.BuildHud(null, timeMs);

    private static int CountTag(List<SceneSprite> hud, string tag) => hud.Count(s => s.AnimationPath == tag);

    /// <summary>倒计时条(底块与文字)一个都没画。</summary>
    public static void AssertCountdownHidden(List<SceneSprite> hud, string where)
    {
        Assert.NotNull(hud);
        Assert.True(CountTag(hud, CountdownBarTag) == 0, where + ": 倒计时底块不应出现");
        Assert.True(CountTag(hud, CountdownTextTag) == 0, where + ": 倒计时文字不应出现");
    }

    /// <summary>倒计时条画出了、文字恰是 <paramref name="seconds"/> 秒(无字体版本:"AUTO RETURN TO TOWN: 〈秒〉S",1 倍点阵;≤10 秒用紧迫色)。</summary>
    public static void AssertCountdownShown(OverlayArt art, List<SceneSprite> hud, int seconds, string where)
    {
        Assert.NotNull(hud);
        Assert.True(CountTag(hud, CountdownBarTag) == 1, where + ": 倒计时底块应恰有一个");
        SceneSprite text = Assert.Single(hud, s => s.AnimationPath == CountdownTextTag);
        uint color = seconds <= 10 ? UrgentColor : CalmColor;
        SpriteFrame expected = art.Text("AUTO RETURN TO TOWN: " + seconds + "S", 1, color);
        Assert.True(ReferenceEquals(expected, text.Frame), where + ": 倒计时文字应是 " + seconds + " 秒(色 0x" + color.ToString("X6") + ")");
    }

    /// <summary>四行菜单文字(占位面板)逐行恰是给定颜色:禁用灰、悬停黄、可选白。</summary>
    public static void AssertRows(OverlayArt art, List<SceneSprite> hud, uint c0, uint c1, uint c2, uint c3, string where)
    {
        Assert.NotNull(hud);
        List<SceneSprite> labels = hud.Where(s => s.AnimationPath == LabelTag).ToList();
        Assert.True(labels.Count == 4, where + ": 菜单文字应有 4 行,实际 " + labels.Count);
        uint[] colors = { c0, c1, c2, c3 };
        for (int i = 0; i < 4; i++)
        {
            SpriteFrame expected = art.Text(RowNames[i], 2, colors[i]);
            Assert.True(ReferenceEquals(expected, labels[i].Frame), where + ": 第 " + i + " 行 " + RowNames[i] + " 应是色 0x" + colors[i].ToString("X6"));
        }
    }

    /// <summary>顶部提示条有没有画出来。</summary>
    public static bool HasNoticeBar(List<SceneSprite> hud)
    {
        Assert.NotNull(hud);
        return hud.Any(s => s.AnimationPath == NoticeTag);
    }

    /// <summary>发送失败的假回调:把对某包号的发送变成"非 Ok 状态"(其余包都成功,不自动回包)。</summary>
    public static void FailSendOf(FakeSettlementTransport transport, ushort failingPacketId)
    {
        transport.SetSendHandler((id, _) => Task.FromResult(id == failingPacketId ? FrameStatus.NoCipher : FrameStatus.Ok));
    }

    /// <summary>推送(0,23)用户区域通知:本人(<see cref="OnlineSettlementGapAKit.SelfUid"/>)落在 <paramref name="village"/>/<paramref name="area"/>。</summary>
    public static void PushUserArea(FakeSettlementTransport t, byte village, byte area, ushort x = 300, ushort y = 400)
    {
        t.Push(PacketClass.Noti, TownAreaPayloads.UserAreaPacketId, UserAreaBytes(SelfUid, village, area, x, y));
    }

    /// <summary>推送(0,24)区域用户列表通知(与 0/23 成对确认回城)。</summary>
    public static void PushAreaUsers(FakeSettlementTransport t, byte village, byte area)
    {
        t.Push(PacketClass.Noti, TownAreaPayloads.AreaUsersPacketId, AreaUsersBytes(village, area));
    }
}
