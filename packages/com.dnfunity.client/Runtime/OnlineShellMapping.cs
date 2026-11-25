using System.Collections.Generic;
using Dnf.Net.Gateway;
using Dnf.Net.Session;
using Dnf.Play;

namespace Dnf.Client
{
    /// <summary>
    /// 联网层的结果 → 联网外壳(Play 层,<see cref="OnlineShellFlow"/>)认的提示类别与名册记录。全是纯函数,不碰网络也不碰引擎:
    /// 外壳不引用 Net 程序集,所以"网关到底回了什么、该给玩家说哪一句"的翻译只在这一处做,测试也只在这一处核对。
    /// <para>登录失败分类表(网关结局 → 界面提示类别):</para>
    /// <list type="table">
    /// <item><term>业务错误 口令错 / 账号被封 / 暂不开放注册 / 账号已存在 / 账号不合规 / 口令不合规</term><description>对应的同名类别</description></item>
    /// <item><term>业务错误 其它(qq 不合规、通用失败、不认识的错误键)</term><description><see cref="OnlineNoticeKind.Rejected"/></description></item>
    /// <item><term>被限流</term><description><see cref="OnlineNoticeKind.RateLimited"/>(带服务端建议的等待秒数,界面倒计时)</description></item>
    /// <item><term>传输错误(网络异常、超时、非 2xx)</term><description><see cref="OnlineNoticeKind.NetworkError"/></description></item>
    /// <item><term>协议错误(解不开、形状不对、登录成功却没有令牌)</term><description><see cref="OnlineNoticeKind.ProtocolError"/></description></item>
    /// <item><term>本地字段检查没过(没发出请求)</term><description><see cref="OnlineNoticeKind.InvalidUsername"/>(口令只做 MD5,唯一会不过的是账号的形状)</description></item>
    /// <item><term>取游戏服地址:服务端没配置(404)或回的地址不可用</term><description><see cref="OnlineNoticeKind.NoGameServer"/></description></item>
    /// </list>
    /// </summary>
    public static class OnlineShellMapping
    {
        /// <summary>网关业务错误 → 类别。</summary>
        public static OnlineNoticeKind ServerError(GatewayErrorCode code)
        {
            switch (code)
            {
                case GatewayErrorCode.WrongCredentials: return OnlineNoticeKind.WrongCredentials;
                case GatewayErrorCode.AccountBanned: return OnlineNoticeKind.AccountBanned;
                case GatewayErrorCode.RegistrationClosed: return OnlineNoticeKind.RegistrationClosed;
                case GatewayErrorCode.UserExists: return OnlineNoticeKind.UserExists;
                case GatewayErrorCode.InvalidUsername: return OnlineNoticeKind.InvalidUsername;
                case GatewayErrorCode.InvalidPassword: return OnlineNoticeKind.InvalidPassword;
                default: return OnlineNoticeKind.Rejected;      // InvalidQq / Fail / Unknown / None:服务端拒绝了,但没有更具体的说法
            }
        }

        /// <summary>网关一次调用的结局 → 类别;成功返回 <see cref="OnlineNoticeKind.None"/>。</summary>
        public static OnlineNoticeKind Gateway(GatewayOutcome outcome, GatewayErrorCode code)
        {
            switch (outcome)
            {
                case GatewayOutcome.Success: return OnlineNoticeKind.None;
                case GatewayOutcome.ServerError: return ServerError(code);
                case GatewayOutcome.RateLimited: return OnlineNoticeKind.RateLimited;
                case GatewayOutcome.TransportError: return OnlineNoticeKind.NetworkError;
                case GatewayOutcome.ProtocolError: return OnlineNoticeKind.ProtocolError;
                case GatewayOutcome.InvalidInput: return OnlineNoticeKind.InvalidUsername;
                case GatewayOutcome.NotConfigured: return OnlineNoticeKind.NoGameServer;
                default: return OnlineNoticeKind.Unexpected;
            }
        }

        /// <summary>
        /// 登录这一步(网关登录加取游戏服地址)的结果。<paramref name="failure"/> 是联网层的失败位置:
        /// 登录调用本身失败时按网关结局归类(结局是成功却没有令牌 = 协议异常);取地址失败时,调用成功但地址不可用算"没有游戏服地址",其余按网关结局归类。
        /// </summary>
        public static OnlineAuthOutcome Login(OnlineFailure failure, GatewayOutcome outcome, GatewayErrorCode code, int retryAfterSeconds)
        {
            OnlineNoticeKind kind;
            switch (failure)
            {
                case OnlineFailure.None:
                    return OnlineAuthOutcome.Ok();
                case OnlineFailure.GatewayLoginFailed:
                    kind = outcome == GatewayOutcome.Success ? OnlineNoticeKind.ProtocolError : Gateway(outcome, code);
                    break;
                case OnlineFailure.NoGameServerAddress:
                    kind = outcome == GatewayOutcome.Success ? OnlineNoticeKind.NoGameServer : Gateway(outcome, code);
                    break;
                default:
                    kind = OnlineNoticeKind.Unexpected;
                    break;
            }
            return OnlineAuthOutcome.Fail(kind, kind == OnlineNoticeKind.RateLimited ? retryAfterSeconds : 0);
        }

        /// <summary>注册与改密(只有网关一步)的结果。</summary>
        public static OnlineAuthOutcome Auth(GatewayOutcome outcome, GatewayErrorCode code, int retryAfterSeconds)
        {
            if (outcome == GatewayOutcome.Success)
            {
                return OnlineAuthOutcome.Ok();
            }
            OnlineNoticeKind kind = Gateway(outcome, code);
            return OnlineAuthOutcome.Fail(kind, kind == OnlineNoticeKind.RateLimited ? retryAfterSeconds : 0);
        }

        /// <summary>连频道、启动包与游戏登录失败的位置 → 类别。</summary>
        public static OnlineNoticeKind ConnectFailure(OnlineFailure failure)
        {
            switch (failure)
            {
                case OnlineFailure.ConnectFailed: return OnlineNoticeKind.ConnectFailed;
                case OnlineFailure.BootstrapFailed: return OnlineNoticeKind.ConnectFailed;
                case OnlineFailure.Rejected: return OnlineNoticeKind.Rejected;
                case OnlineFailure.GameLoginRejected: return OnlineNoticeKind.GameLoginRejected;
                case OnlineFailure.GameLoginNoReply: return OnlineNoticeKind.NetworkError;   // 游戏登录没等到回包:对玩家来说是超时
                default: return OnlineNoticeKind.Unexpected;
            }
        }

        /// <summary>
        /// 连频道的结果。成功要有角色列表:列表没到或没解开(<see cref="OnlineCharacterListStatus"/> 不是 Received)算失败,
        /// 不伪装成"账号下没有角色"(空列表是 Received,会得到一个空名册)。
        /// </summary>
        public static OnlineConnectOutcome Connect(OnlineFailure failure, OnlineCharacterListStatus listStatus, UserInfoNotice list)
        {
            if (failure != OnlineFailure.None)
            {
                return OnlineConnectOutcome.Fail(ConnectFailure(failure));
            }
            if (listStatus != OnlineCharacterListStatus.Received || list == null)
            {
                return OnlineConnectOutcome.Fail(OnlineNoticeKind.CharacterListMissing);
            }
            return OnlineConnectOutcome.Ok(CharacterList(list));
        }

        /// <summary>选角的结果:进去了,或这个角色进不去(<paramref name="mayTryAnotherSlot"/> 为 false 表示连接已经不可用)。</summary>
        public static OnlineSelectOutcome Select(bool entered, bool mayTryAnotherSlot)
        {
            return entered
                ? OnlineSelectOutcome.Ok()
                : OnlineSelectOutcome.Fail(OnlineNoticeKind.SelectFailed, mayTryAnotherSlot);
        }

        /// <summary>服务端角色列表(Net 的解析结果)→ 外壳的名册记录:头部四个字段、全部条目与每个条目的每个字段、尾部字节数,一个不丢、一个不改,也不截断。</summary>
        public static OnlineCharacterList CharacterList(UserInfoNotice notice)
        {
            var entries = new List<OnlineCharacterEntry>(notice.Entries.Count);
            foreach (UserInfoEntry entry in notice.Entries)
            {
                var equips = new List<OnlineCharacterEquip>(entry.Equips.Count);
                foreach (UserInfoEquip equip in entry.Equips)
                {
                    equips.Add(new OnlineCharacterEquip(equip.Slot, equip.ItemId, equip.Blob, equip.Packed, equip.ClearAvatar, equip.Extra));
                }
                var name = new byte[entry.NameBytes.Count];
                for (int i = 0; i < name.Length; i++)
                {
                    name[i] = entry.NameBytes[i];
                }
                entries.Add(new OnlineCharacterEntry(
                    entry.Uid, name, entry.X1, entry.Job, entry.GrowTypeByte, entry.Level, entry.D, entry.Status,
                    equips, entry.G, entry.H, entry.I, entry.J, entry.Premium, entry.K, entry.A2, entry.B2, entry.C2, entry.D2));
            }
            return new OnlineCharacterList(notice.A, notice.B, notice.C, notice.D, entries, notice.TrailingByteCount);
        }

        /// <summary>Net 层角色创建失败枚举 → Play 层状态枚举。</summary>
        public static OnlineCharacterManageStatus CreateFailure(CharacterCreateFailure failure)
        {
            switch (failure)
            {
                case CharacterCreateFailure.DatabaseOrUnknownError: return OnlineCharacterManageStatus.DatabaseOrUnknownError;
                case CharacterCreateFailure.SlotLimitReached: return OnlineCharacterManageStatus.SlotLimitReached;
                case CharacterCreateFailure.ClientRestricted: return OnlineCharacterManageStatus.ClientRestricted;
                case CharacterCreateFailure.CreationRestrictedEmptyAccount: return OnlineCharacterManageStatus.CreationRestrictedEmptyAccount;
                case CharacterCreateFailure.CreationRestrictedByEvent: return OnlineCharacterManageStatus.CreationRestrictedByEvent;
                case CharacterCreateFailure.DuplicateActiveName: return OnlineCharacterManageStatus.DuplicateActiveName;
                case CharacterCreateFailure.StateLocked: return OnlineCharacterManageStatus.StateLocked;
                case CharacterCreateFailure.EventCharacterConditionFailed: return OnlineCharacterManageStatus.EventCharacterConditionFailed;
                case CharacterCreateFailure.ClientSilentIgnored: return OnlineCharacterManageStatus.ClientSilentIgnored;
                case CharacterCreateFailure.CreationBlocked: return OnlineCharacterManageStatus.CreationBlocked;
                case CharacterCreateFailure.DuplicateDeletedName: return OnlineCharacterManageStatus.DuplicateDeletedName;
                case CharacterCreateFailure.DailyCreationLimitReached: return OnlineCharacterManageStatus.DailyCreationLimitReached;
                case CharacterCreateFailure.ChannelNotAllowed: return OnlineCharacterManageStatus.ChannelNotAllowed;
                case CharacterCreateFailure.InvalidName: return OnlineCharacterManageStatus.InvalidName;
                case CharacterCreateFailure.ServiceRestricted: return OnlineCharacterManageStatus.ServiceRestricted;
                default: return OnlineCharacterManageStatus.UnknownError;
            }
        }

        /// <summary>Net 层角色删除失败枚举 → Play 层状态枚举。</summary>
        public static OnlineCharacterManageStatus DeleteFailure(CharacterDeleteFailure failure)
        {
            switch (failure)
            {
                case CharacterDeleteFailure.DatabaseOrUnknownError: return OnlineCharacterManageStatus.DatabaseOrUnknownError;
                case CharacterDeleteFailure.StateLocked: return OnlineCharacterManageStatus.StateLocked;
                case CharacterDeleteFailure.EmptySlot: return OnlineCharacterManageStatus.EmptySlot;
                case CharacterDeleteFailure.CooldownOrActiveCharacter: return OnlineCharacterManageStatus.CooldownOrActiveCharacter;
                case CharacterDeleteFailure.GuildMasterCannotDelete: return OnlineCharacterManageStatus.GuildMasterCannotDelete;
                case CharacterDeleteFailure.ClientDeletionBlocked: return OnlineCharacterManageStatus.ClientDeletionBlocked;
                case CharacterDeleteFailure.TradePunished: return OnlineCharacterManageStatus.TradePunished;
                case CharacterDeleteFailure.RestingUserRestricted: return OnlineCharacterManageStatus.RestingUserRestricted;
                case CharacterDeleteFailure.ServiceRestricted: return OnlineCharacterManageStatus.ServiceRestricted;
                case CharacterDeleteFailure.ItemLockActive: return OnlineCharacterManageStatus.ItemLockActive;
                case CharacterDeleteFailure.TradePunishRestricted: return OnlineCharacterManageStatus.TradePunishRestricted;
                case CharacterDeleteFailure.RestorePunishRestricted: return OnlineCharacterManageStatus.RestorePunishRestricted;
                case CharacterDeleteFailure.SecurityServiceRequired: return OnlineCharacterManageStatus.SecurityServiceRequired;
                case CharacterDeleteFailure.SharedSecurityRestriction: return OnlineCharacterManageStatus.SharedSecurityRestriction;
                default: return OnlineCharacterManageStatus.UnknownError;
            }
        }

        /// <summary>Net 层角色创建/删除报告 → Play 层只读结局。</summary>
        internal static OnlineCharacterManageOutcome Manage(OnlineCharacterManageReport report)
        {
            switch (report.Outcome)
            {
                case OnlineCharacterManageOutcomeKind.Succeeded:
                    if (report.CharacterListStatus != OnlineCharacterListStatus.Received || report.CharacterList == null)
                    {
                        return OnlineCharacterManageOutcome.Fail(
                            OnlineCharacterManageStatus.RosterMissing,
                            commandSucceeded: true);
                    }
                    return OnlineCharacterManageOutcome.Ok(
                        CharacterList(report.CharacterList),
                        report.EventCharacterFlagCleared);
                case OnlineCharacterManageOutcomeKind.Rejected:
                    OnlineCharacterManageStatus status = report.Operation == OnlineCharacterManageKind.Create
                        ? CreateFailure(report.CreateFailure)
                        : DeleteFailure(report.DeleteFailure);
                    return OnlineCharacterManageOutcome.Fail(status, report.RawErrorCode);
                case OnlineCharacterManageOutcomeKind.NoReply:
                    return OnlineCharacterManageOutcome.Fail(OnlineCharacterManageStatus.NoReply);
                case OnlineCharacterManageOutcomeKind.RosterMissing:
                    return OnlineCharacterManageOutcome.Fail(OnlineCharacterManageStatus.RosterMissing, commandSucceeded: true);
                case OnlineCharacterManageOutcomeKind.RosterMalformed:
                    return OnlineCharacterManageOutcome.Fail(OnlineCharacterManageStatus.RosterMalformed, commandSucceeded: true);
                case OnlineCharacterManageOutcomeKind.Disconnected:
                    return OnlineCharacterManageOutcome.Fail(OnlineCharacterManageStatus.Disconnected);
                case OnlineCharacterManageOutcomeKind.FrameError:
                    return OnlineCharacterManageOutcome.Fail(OnlineCharacterManageStatus.InvalidInput);
                default:
                    return OnlineCharacterManageOutcome.Fail(OnlineCharacterManageStatus.ProtocolError);
            }
        }
    }
}
