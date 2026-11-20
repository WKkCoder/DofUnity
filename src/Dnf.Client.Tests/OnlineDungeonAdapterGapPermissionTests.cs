using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Net.Tests;
using Dnf.Play;

namespace Dnf.Client.Tests;

/// <summary>
/// r18-dungeonflow 副本许可适配器(<see cref="OnlineDungeonPermissionAdapter"/>:实时 (0,5) 帧 → <see cref="OnlineDungeonPermissionTable"/>)
/// 与结算就绪事件参数(<see cref="OnlineDungeonSettlementReady"/>)的"缺口"测试。
/// 现有测试只走过"一帧合法许可帧被应用"的单条路径;这里把帧过滤的两个条件(类、包号)、空帧/空表防御、
/// 好包与坏包各自的返回值与"是否解析成功"标志、以及结算就绪参数的默认值逐个钉死。
/// 许可帧载荷由手拼字节(u16 条数 + 条数 × { u16 副本号, u8 档位 })经联网层真解析器解出,没有真实编号。
/// </summary>
public sealed class OnlineDungeonAdapterGapPermissionTests
{
    private static byte[] Payload(params (ushort DungeonId, byte Tier)[] entries)
    {
        NoticePayloadBuilder b = new NoticePayloadBuilder().U16(entries.Length);
        foreach ((ushort dungeonId, byte tier) in entries)
        {
            b.U16(dungeonId).U8(tier);
        }
        return b.ToArray();
    }

    private static OnlineReceivedFrame Frame(PacketClass packetClass, int packetId, byte[] payload) =>
        NetObjects.Frame(packetClass, packetId, payload);

    private static void AssertTableUntouched(OnlineDungeonPermissionTable table)
    {
        Assert.False(table.HasSnapshot);
        Assert.Equal(0, table.Version);
        Assert.Equal(0, table.NoticeCount);
        Assert.Equal(0, table.MalformedNoticeCount);
        Assert.Equal(0, table.EntryCount);
    }

    /// <summary>
    /// 保护:类为通知、包号为 5 的合法许可帧被识别为许可帧(返回 true),载荷合并进表且"解析成功"标志为真;
    /// 后到的帧覆盖先到的档位并继续累计通知数。表里原有的、帧里没提到的副本保持不变。
    /// </summary>
    [Fact]
    public void ValidPermissionFrameIsAppliedAndReportedAsParsed()
    {
        var table = new OnlineDungeonPermissionTable();
        table.LoadInitial(new[] { new KeyValuePair<int, byte>(9300, 1) }, hasSnapshot: true, noticeCount: 4);
        int versionAfterLoad = table.Version;
        OnlineReceivedFrame first = Frame(PacketClass.Noti, DungeonPermissionParser.PacketId, Payload((9301, 2), (9302, 3)));

        bool handled = OnlineDungeonPermissionAdapter.TryApplyFrame(table, first, out bool parsed);

        Assert.True(handled);
        Assert.True(parsed);
        Assert.True(table.HasSnapshot);
        Assert.Equal(5, table.NoticeCount); // 初始 4 + 本帧 1
        Assert.Equal(0, table.MalformedNoticeCount);
        Assert.Equal(3, table.EntryCount); // 原有 9300 + 新来的 9301、9302
        Assert.Equal((byte)1, table.GetTier(9300));
        Assert.Equal((byte)2, table.GetTier(9301));
        Assert.Equal((byte)3, table.GetTier(9302));
        Assert.Equal(versionAfterLoad + 1, table.Version);

        OnlineReceivedFrame second = Frame(PacketClass.Noti, DungeonPermissionParser.PacketId, Payload((9301, 1)));
        Assert.True(OnlineDungeonPermissionAdapter.TryApplyFrame(table, second, out bool parsedAgain));
        Assert.True(parsedAgain);
        Assert.Equal((byte)1, table.GetTier(9301)); // 后到覆盖先到
        Assert.Equal((byte)3, table.GetTier(9302));
        Assert.Equal(6, table.NoticeCount);
        Assert.Equal(3, table.EntryCount);
        Assert.Equal(versionAfterLoad + 2, table.Version);
    }

    /// <summary>
    /// 保护:包号为 5、类为通知但载荷读不通的帧仍被认作许可帧(返回 true),但"解析成功"为假,
    /// 只累计坏帧数、已有条目与通知数保持不变,表仍被视为"收到过许可帧"。
    /// </summary>
    [Fact]
    public void MalformedPermissionFrameIsHandledButReportedUnparsedAndKeepsTheTable()
    {
        var table = new OnlineDungeonPermissionTable();
        table.LoadInitial(new[] { new KeyValuePair<int, byte>(9301, 2) }, hasSnapshot: false);
        int version = table.Version;
        // 声明 2 条、实际只有 1 个字节:读不通
        OnlineReceivedFrame bad = Frame(PacketClass.Noti, DungeonPermissionParser.PacketId, new byte[] { 2, 0, 0x55 });

        bool handled = OnlineDungeonPermissionAdapter.TryApplyFrame(table, bad, out bool parsed);

        Assert.True(handled);
        Assert.False(parsed);
        Assert.True(table.HasSnapshot);
        Assert.Equal(1, table.MalformedNoticeCount);
        Assert.Equal(0, table.NoticeCount);
        Assert.Equal(1, table.EntryCount);
        Assert.Equal((byte)2, table.GetTier(9301));
        Assert.Equal(version, table.Version); // 坏帧不动版本

        // 直接喂载荷的入口:空数组、空引用、过短都算坏包;好包算成功。三种返回值都是 true
        Assert.True(OnlineDungeonPermissionAdapter.TryApplyNotificationPayload(table, new byte[0], out bool parsedEmpty));
        Assert.False(parsedEmpty);
        Assert.True(OnlineDungeonPermissionAdapter.TryApplyNotificationPayload(table, null, out bool parsedNull));
        Assert.False(parsedNull);
        Assert.Equal(3, table.MalformedNoticeCount);
        Assert.Equal(0, table.NoticeCount);

        Assert.True(OnlineDungeonPermissionAdapter.TryApplyNotificationPayload(table, Payload((9305, 3)), out bool parsedGood));
        Assert.True(parsedGood);
        Assert.Equal(1, table.NoticeCount);
        Assert.Equal(3, table.MalformedNoticeCount);
        Assert.Equal((byte)3, table.GetTier(9305));
        Assert.Equal((byte)2, table.GetTier(9301));
    }

    /// <summary>
    /// 保护:只有"类 = 通知 且 包号 = 5"的帧才算许可帧——空帧、类不对但包号对、类对但包号不对的帧,
    /// 一律返回 false、"解析成功"为假,且表原封不动(哪怕它们的载荷本身是一份合法的许可载荷)。
    /// </summary>
    [Fact]
    public void FramesThatAreNotPermissionNotificationsAreIgnoredAndLeaveTheTableUntouched()
    {
        byte[] valid = Payload((9301, 2), (9302, 3));
        OnlineReceivedFrame[] others =
        {
            null,
            Frame(PacketClass.Cmd, DungeonPermissionParser.PacketId, valid), // 包号对、类是命令
            Frame(PacketClass.Noti, DungeonPermissionParser.PacketId + 1, valid), // 类对、包号差一
            Frame(PacketClass.Noti, DungeonPermissionParser.PacketId - 1, valid), // 类对、包号差一(另一侧)
            Frame(PacketClass.Noti, 0, new byte[0]), // 心跳形状的通知
            Frame(PacketClass.Cmd, 0, valid), // 类、包号都不对
        };

        foreach (OnlineReceivedFrame frame in others)
        {
            var table = new OnlineDungeonPermissionTable();

            bool handled = OnlineDungeonPermissionAdapter.TryApplyFrame(table, frame, out bool parsed);

            string label = frame == null ? "null" : frame.PacketClass + "/" + frame.PacketId;
            Assert.False(handled, label);
            Assert.False(parsed, label);
            AssertTableUntouched(table);
        }
    }

    /// <summary>
    /// 保护:表为空引用时三个入口都抛参数为空异常,并且 <see cref="OnlineDungeonPermissionAdapter.TryApplyFrame"/>
    /// 先检查表、再看帧——即使帧本身是空帧或无关帧(不会走到应用那一步)也照抛,而不是悄悄返回 false。
    /// </summary>
    [Fact]
    public void NullTableIsRejectedBeforeTheFrameIsEvenInspected()
    {
        OnlineReceivedFrame valid = Frame(PacketClass.Noti, DungeonPermissionParser.PacketId, Payload((9301, 2)));
        OnlineReceivedFrame unrelated = Frame(PacketClass.Cmd, 7, new byte[0]);

        Assert.Equal("table", Assert.Throws<ArgumentNullException>(() => { OnlineDungeonPermissionAdapter.TryApplyFrame(null, null, out bool _); }).ParamName);
        Assert.Equal("table", Assert.Throws<ArgumentNullException>(() => { OnlineDungeonPermissionAdapter.TryApplyFrame(null, unrelated, out bool _); }).ParamName);
        Assert.Equal("table", Assert.Throws<ArgumentNullException>(() => { OnlineDungeonPermissionAdapter.TryApplyFrame(null, valid, out bool _); }).ParamName);
        Assert.Equal("table", Assert.Throws<ArgumentNullException>(() => { OnlineDungeonPermissionAdapter.TryApplyNotificationPayload(null, new byte[0], out bool _); }).ParamName);
        Assert.Equal("table", Assert.Throws<ArgumentNullException>(() => { OnlineDungeonPermissionAdapter.LoadSnapshot(null, null); }).ParamName);
    }

    /// <summary>
    /// 保护:结算就绪事件参数默认不接管去留(外部结算 UI 不主动要求,流程就不会被卡在副本画面);
    /// 设为 true 后可读回;玩家选择经 <see cref="OnlineDungeonSettlementReady.CompleteChoice"/> 原样转给回调;
    /// 数据与会话原样保存(会话允许为空),数据与回调为空时构造抛参数为空异常。
    /// </summary>
    [Fact]
    public void SettlementReadyDoesNotHoldForMenuByDefaultAndForwardsTheChoice()
    {
        OnlineDungeonSettlementData data = OnlineDungeonSettlementMapper.Map(9511, "dungeon/test/ready.dgn", 2, 3, 1, 2, null);
        var chosen = new List<SettlementChoice>();
        var ready = new OnlineDungeonSettlementReady(data, null, chosen.Add);

        Assert.False(ready.HoldForMenu);
        Assert.Same(data, ready.Data);
        Assert.Null(ready.Session);

        ready.HoldForMenu = true;
        Assert.True(ready.HoldForMenu);
        ready.HoldForMenu = false;
        Assert.False(ready.HoldForMenu);

        ready.CompleteChoice(SettlementChoice.Retry);
        ready.CompleteChoice(SettlementChoice.SelectDungeon);
        Assert.Equal(new[] { SettlementChoice.Retry, SettlementChoice.SelectDungeon }, chosen.ToArray());

        Assert.Equal("data", Assert.Throws<ArgumentNullException>(() => new OnlineDungeonSettlementReady(null, null, _ => { })).ParamName);
        Assert.Equal("complete", Assert.Throws<ArgumentNullException>(() => new OnlineDungeonSettlementReady(data, null, null)).ParamName);
    }
}
