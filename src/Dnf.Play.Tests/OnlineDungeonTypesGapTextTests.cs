using Dnf.Play;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 联网副本文案与日志行的"缺口"测试:入口状态的提示文案、提示种类 → 色调/文案映射、
/// <see cref="OnlineDungeonLog"/> 各行日志的完整字面量(字段顺序与分隔符)。
/// 现有覆盖(<c>OnlineDungeonSelectTests</c>)只检查了日志"以前缀开头、不含路径",这里逐字比对整行与整句。
/// </summary>
public class OnlineDungeonTypesGapTextTests
{
    // ------------------------------------------------------------------------------------------
    // DungeonEntryStatus:是否禁用与提示文案
    // ------------------------------------------------------------------------------------------

    /// <summary>保护:只有 Kind 为 None 的状态不是禁用,其余每一种都是禁用;默认值(default)等同"允许"。</summary>
    [Fact]
    public void EntryStatusIsDisabledForEveryKindExceptNone()
    {
        Assert.False(DungeonEntryStatus.Allowed.Disabled);
        Assert.False(default(DungeonEntryStatus).Disabled);
        Assert.False(new DungeonEntryStatus(DungeonEntryBlockKind.None).Disabled);
        Assert.True(new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected).Disabled);
        Assert.True(new DungeonEntryStatus(DungeonEntryBlockKind.MissingDungeonId).Disabled);
        Assert.True(new DungeonEntryStatus(DungeonEntryBlockKind.MissingLocalData).Disabled);
        Assert.True(new DungeonEntryStatus(DungeonEntryBlockKind.Custom).Disabled);
    }

    /// <summary>
    /// 保护:服务端拒绝的提示文案——有字体时中文、无字体(占位字库)时大写英文;
    /// 有拒绝码时括注十进制码(含 0 与 255),没有拒绝码时不带括注。四种组合逐字比对。
    /// </summary>
    [Fact]
    public void ServerRejectedReasonTextCoversFontAndCodeCombinations()
    {
        var noCode = new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected);
        Assert.Equal("服务端暂不允许进入该副本", noCode.ReasonText(hasFonts: true));
        Assert.Equal("SERVER REJECTED", noCode.ReasonText(hasFonts: false));

        var code14 = new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, 14);
        Assert.Equal("服务端暂不允许进入该副本 (拒绝码 14)", code14.ReasonText(hasFonts: true));
        Assert.Equal("SERVER REJECTED (14)", code14.ReasonText(hasFonts: false));

        var code0 = new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, 0);
        Assert.Equal("服务端暂不允许进入该副本 (拒绝码 0)", code0.ReasonText(hasFonts: true));
        Assert.Equal("SERVER REJECTED (0)", code0.ReasonText(hasFonts: false));

        var code255 = new DungeonEntryStatus(DungeonEntryBlockKind.ServerRejected, 255);
        Assert.Equal("服务端暂不允许进入该副本 (拒绝码 255)", code255.ReasonText(hasFonts: true));
        Assert.Equal("SERVER REJECTED (255)", code255.ReasonText(hasFonts: false));
    }

    /// <summary>保护:缺编号映射、缺本地数据与"其他不可进入"(Custom 无自定义文案)三种原因的中英文案逐字比对。</summary>
    [Fact]
    public void MissingIdMissingDataAndGenericReasonTexts()
    {
        var noId = new DungeonEntryStatus(DungeonEntryBlockKind.MissingDungeonId);
        Assert.Equal("缺少副本编号映射，暂不可进入", noId.ReasonText(hasFonts: true));
        Assert.Equal("MISSING DUNGEON ID", noId.ReasonText(hasFonts: false));

        var noData = new DungeonEntryStatus(DungeonEntryBlockKind.MissingLocalData);
        Assert.Equal("本地缺少该副本数据，暂不可进入", noData.ReasonText(hasFonts: true));
        Assert.Equal("MISSING LOCAL DATA", noData.ReasonText(hasFonts: false));

        var generic = new DungeonEntryStatus(DungeonEntryBlockKind.Custom);
        Assert.Equal("当前不可进入该副本", generic.ReasonText(hasFonts: true));
        Assert.Equal("DUNGEON UNAVAILABLE", generic.ReasonText(hasFonts: false));
    }

    /// <summary>
    /// 保护:自定义文案优先于任何种类的内置文案(有无字体都原样返回);空字符串视为"没有自定义文案"而退回内置;
    /// 但"允许进入"状态不论有无自定义文案都返回空串。
    /// </summary>
    [Fact]
    public void CustomMessageOverridesBuiltInTextAndAllowedStatusStaysEmpty()
    {
        foreach (DungeonEntryBlockKind kind in new[]
        {
            DungeonEntryBlockKind.ServerRejected,
            DungeonEntryBlockKind.MissingDungeonId,
            DungeonEntryBlockKind.MissingLocalData,
            DungeonEntryBlockKind.Custom,
        })
        {
            var custom = new DungeonEntryStatus(kind, 9, "合成自定义文案");
            Assert.Equal("合成自定义文案", custom.ReasonText(hasFonts: true));
            Assert.Equal("合成自定义文案", custom.ReasonText(hasFonts: false));
        }

        var emptyCustom = new DungeonEntryStatus(DungeonEntryBlockKind.MissingLocalData, null, string.Empty);
        Assert.Equal("MISSING LOCAL DATA", emptyCustom.ReasonText(hasFonts: false));

        Assert.Equal(string.Empty, DungeonEntryStatus.Allowed.ReasonText(hasFonts: true));
        Assert.Equal(string.Empty, DungeonEntryStatus.Allowed.ReasonText(hasFonts: false));
        Assert.Equal(string.Empty, new DungeonEntryStatus(DungeonEntryBlockKind.None, null, "被忽略").ReasonText(hasFonts: true));
    }

    // ------------------------------------------------------------------------------------------
    // DungeonSelectNotice:色调与文案
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:提示种类 → 色调——等待类(进入中、放弃中)为 Wait,Info 为 Info,其余(含 None)全是 Error。
    /// 把每个枚举值都过一遍,避免只测其中几个。
    /// </summary>
    [Theory]
    [InlineData(DungeonSelectNoticeKind.None, ShellTone.Error)]
    [InlineData(DungeonSelectNoticeKind.Entering, ShellTone.Wait)]
    [InlineData(DungeonSelectNoticeKind.GivingUp, ShellTone.Wait)]
    [InlineData(DungeonSelectNoticeKind.Rejected, ShellTone.Error)]
    [InlineData(DungeonSelectNoticeKind.Timeout, ShellTone.Error)]
    [InlineData(DungeonSelectNoticeKind.RoomFailed, ShellTone.Error)]
    [InlineData(DungeonSelectNoticeKind.Disconnected, ShellTone.Error)]
    [InlineData(DungeonSelectNoticeKind.Aborted, ShellTone.Error)]
    [InlineData(DungeonSelectNoticeKind.Failed, ShellTone.Error)]
    [InlineData(DungeonSelectNoticeKind.Info, ShellTone.Info)]
    public void NoticeToneMapsEveryKind(DungeonSelectNoticeKind kind, ShellTone expected)
    {
        Assert.Equal(expected, new DungeonSelectNotice(kind).Tone);
    }

    /// <summary>
    /// 保护:每种提示在"有字体 / 无字体"下的完整文案(逐字比对);None 与 Info 没有内置文案,返回空串。
    /// </summary>
    [Theory]
    [InlineData(DungeonSelectNoticeKind.None, "", "")]
    [InlineData(DungeonSelectNoticeKind.Entering, "正在请求进入联网副本…", "ENTERING ONLINE DUNGEON...")]
    [InlineData(DungeonSelectNoticeKind.GivingUp, "正在放弃副本并返回城镇…", "RETURNING TO TOWN...")]
    [InlineData(DungeonSelectNoticeKind.Rejected, "服务端拒绝进入该副本", "ENTRY REJECTED BY SERVER")]
    [InlineData(DungeonSelectNoticeKind.Timeout, "等待服务端响应超时", "SERVER RESPONSE TIMED OUT")]
    [InlineData(DungeonSelectNoticeKind.RoomFailed, "无法生成联网副本房间", "FAILED TO BUILD ONLINE ROOM")]
    [InlineData(DungeonSelectNoticeKind.Disconnected, "与频道服务器的连接已断开", "CHANNEL CONNECTION LOST")]
    [InlineData(DungeonSelectNoticeKind.Aborted, "联网副本已中止，已返回城镇", "ONLINE RUN ABORTED")]
    [InlineData(DungeonSelectNoticeKind.Failed, "进入联网副本失败", "ONLINE DUNGEON ENTRY FAILED")]
    [InlineData(DungeonSelectNoticeKind.Info, "", "")]
    public void NoticeMessageTextForEveryKind(DungeonSelectNoticeKind kind, string withFonts, string withoutFonts)
    {
        var notice = new DungeonSelectNotice(kind);
        Assert.Equal(withFonts, notice.Message(hasFonts: true));
        Assert.Equal(withoutFonts, notice.Message(hasFonts: false));
    }

    /// <summary>
    /// 保护:自定义文案优先于内置文案(有无字体都原样);空串视为没有自定义文案而退回内置;
    /// 构造参数原样保存(过期时刻缺省为 0、自定义文案缺省为 null)。
    /// </summary>
    [Fact]
    public void NoticeCustomMessageOverridesAndConstructorArgumentsAreKept()
    {
        var custom = new DungeonSelectNotice(DungeonSelectNoticeKind.Timeout, "合成提示", 4321);
        Assert.Equal(DungeonSelectNoticeKind.Timeout, custom.Kind);
        Assert.Equal("合成提示", custom.CustomMessage);
        Assert.Equal(4321L, custom.ExpiresAtMs);
        Assert.Equal("合成提示", custom.Message(hasFonts: true));
        Assert.Equal("合成提示", custom.Message(hasFonts: false));

        var infoCustom = new DungeonSelectNotice(DungeonSelectNoticeKind.Info, "合成信息");
        Assert.Equal("合成信息", infoCustom.Message(hasFonts: false));
        Assert.Equal(ShellTone.Info, infoCustom.Tone);

        var emptyCustom = new DungeonSelectNotice(DungeonSelectNoticeKind.Rejected, string.Empty);
        Assert.Equal("ENTRY REJECTED BY SERVER", emptyCustom.Message(hasFonts: false));

        var plain = new DungeonSelectNotice(DungeonSelectNoticeKind.Failed);
        Assert.Null(plain.CustomMessage);
        Assert.Equal(0L, plain.ExpiresAtMs);
    }

    // ------------------------------------------------------------------------------------------
    // OnlineDungeonLog:整行字面量
    // ------------------------------------------------------------------------------------------

    /// <summary>保护:日志前缀常量与每一行日志的开头一致("DNF_ONLINE stage=dungeon")。</summary>
    [Fact]
    public void LogPrefixIsTheFixedStageTag()
    {
        Assert.Equal("DNF_ONLINE stage=dungeon", OnlineDungeonLog.Prefix);
    }

    /// <summary>
    /// 保护:选择日志行的字段顺序 outcome → difficulty → max →(可选)reason;
    /// 难度与最高难度取不同的值以防互相串位;空结局名写成 unknown;原因为空(null 或空串)时整段省略。
    /// </summary>
    [Fact]
    public void SelectLogLineHasFixedFieldOrderAndOptionalReason()
    {
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=select outcome=enter difficulty=1 max=3",
            OnlineDungeonLog.Select("enter", 1, 3));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=select outcome=blocked difficulty=2 max=0 reason=ServerRejected",
            OnlineDungeonLog.Select("blocked", 2, 0, "ServerRejected"));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=select outcome=unknown difficulty=0 max=2",
            OnlineDungeonLog.Select(null, 0, 2, null));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=select outcome=unknown difficulty=3 max=1",
            OnlineDungeonLog.Select(string.Empty, 3, 1, string.Empty));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=select outcome=back difficulty=-1 max=-2",
            OnlineDungeonLog.Select("back", -1, -2));
    }

    /// <summary>保护:进入日志行的字段顺序 areakind → entrance → difficulty;门区标志写 true/false;空区域种类写 unknown。</summary>
    [Fact]
    public void EnterLogLineHasFixedFieldOrder()
    {
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=enter areakind=DungeonGate entrance=true difficulty=2",
            OnlineDungeonLog.Enter("DungeonGate", true, 2));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=enter areakind=Normal entrance=false difficulty=0",
            OnlineDungeonLog.Enter("Normal", false, 0));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=enter areakind=unknown entrance=false difficulty=3",
            OnlineDungeonLog.Enter(null, false, 3));
    }

    /// <summary>
    /// 保护:房间日志行的字段顺序 status → multi → spawned → expected → rooms;多房间开关写 on/off;
    /// rooms 缺省为 1;已生成数与期望数取不同值以防串位。
    /// </summary>
    [Fact]
    public void RoomLogLineHasFixedFieldOrderAndDefaultRoomCount()
    {
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=room status=Ready multi=on spawned=4 expected=5 rooms=3",
            OnlineDungeonLog.Room("Ready", true, 4, 5, 3));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=room status=NoRoom multi=off spawned=0 expected=2 rooms=1",
            OnlineDungeonLog.Room("NoRoom", false, 0, 2));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=room status=unknown multi=off spawned=7 expected=6 rooms=2",
            OnlineDungeonLog.Room(null, false, 7, 6, 2));
    }

    /// <summary>
    /// 保护:离开日志行的字段顺序 reason → reported → dropped → rooms →(可选)result →(可选)reward;
    /// 两段解析状态各自独立地可省略;上报数与丢弃数取不同值以防串位。
    /// </summary>
    [Fact]
    public void LeaveLogLineHasFixedFieldOrderAndIndependentOptionalParseTags()
    {
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=leave reason=ReturnReady reported=4 dropped=1 rooms=3 result=Succeeded reward=Failed",
            OnlineDungeonLog.Leave("ReturnReady", 4, 1, 3, "Succeeded", "Failed"));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=leave reason=GaveUp reported=2 dropped=0 rooms=1",
            OnlineDungeonLog.Leave("GaveUp", 2, 0, 1));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=leave reason=Timeout reported=5 dropped=6 rooms=2 result=Failed",
            OnlineDungeonLog.Leave("Timeout", 5, 6, 2, "Failed", null));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=leave reason=Timeout reported=5 dropped=6 rooms=2 reward=Succeeded",
            OnlineDungeonLog.Leave("Timeout", 5, 6, 2, string.Empty, "Succeeded"));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=leave reason=unknown reported=0 dropped=0 rooms=0",
            OnlineDungeonLog.Leave(null, 0, 0, 0));
    }

    /// <summary>保护:回城区域核对日志行 "event=return area=<匹配结果>";空结果写 unknown。</summary>
    [Fact]
    public void ReturnAreaLogLine()
    {
        Assert.Equal("DNF_ONLINE stage=dungeon event=return area=match", OnlineDungeonLog.ReturnArea("match"));
        Assert.Equal("DNF_ONLINE stage=dungeon event=return area=mismatch", OnlineDungeonLog.ReturnArea("mismatch"));
        Assert.Equal("DNF_ONLINE stage=dungeon event=return area=unknown", OnlineDungeonLog.ReturnArea(null));
        Assert.Equal("DNF_ONLINE stage=dungeon event=return area=unknown", OnlineDungeonLog.ReturnArea(string.Empty));
    }

    /// <summary>
    /// 保护:失败日志行的字段顺序 reason →(可选)step →(可选)code;拒绝码为 0 时仍要写出(有值即写),
    /// 只有 reason / reason+step / reason+code / 三项齐全 / 空 reason 各取一例。
    /// </summary>
    [Fact]
    public void FailLogLineHasFixedFieldOrderAndOptionalStepAndCode()
    {
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=fail reason=Rejected step=WaitStartMap code=14",
            OnlineDungeonLog.Fail("Rejected", "WaitStartMap", 14));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=fail reason=Timeout",
            OnlineDungeonLog.Fail("Timeout"));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=fail reason=Timeout step=WaitLoading",
            OnlineDungeonLog.Fail("Timeout", "WaitLoading"));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=fail reason=Rejected code=0",
            OnlineDungeonLog.Fail("Rejected", null, 0));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=fail reason=Rejected code=255",
            OnlineDungeonLog.Fail("Rejected", string.Empty, 255));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=fail reason=unknown step=Step code=9",
            OnlineDungeonLog.Fail(null, "Step", 9));
    }

    /// <summary>保护:许可更新日志行 "event=perms entries=<条目数> malformed=<坏帧数>",两个数取不同值以防串位。</summary>
    [Fact]
    public void PermissionUpdateLogLineKeepsEntriesAndMalformedApart()
    {
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=perms entries=12 malformed=3",
            OnlineDungeonLog.PermissionUpdate(12, 3));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=perms entries=0 malformed=2",
            OnlineDungeonLog.PermissionUpdate(0, 2));
        Assert.Equal(
            "DNF_ONLINE stage=dungeon event=perms entries=5 malformed=0",
            OnlineDungeonLog.PermissionUpdate(5, 0));
    }
}
