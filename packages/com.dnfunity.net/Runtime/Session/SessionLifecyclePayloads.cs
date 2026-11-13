using System;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 副本中途撤退（<c>1/45 GIVEUP_GAME</c>）、退回选角界面（<c>1/7 RETURN_SELECT_CHARACTER</c>）及
    /// 服务端踢线原因通知（<c>0/199 DISCONNECT_REASON</c>）的载荷构造/解析器。
    /// <para>
    /// 闭环必需性：
    /// 1) 副本未通关时需发送 <c>1/45 GIVEUP_GAME</c> 才会调用 <c>GameWorld::out_from_dungeon</c> 将状态reset回城（<c>state = 3</c>），否则角色将一直被锁在副本内；
    /// 2) 会话内在不重连 TCP 的前提下保存角色并退回选角界面（<c>state = 2</c>）必须发送 <c>1/7</c>；
    /// 3) 保活命令 <c>1/0 CHECK_CONNECTION</c> 已在 <see cref="OnlineHeartbeat"/> 实现（本类不重复）；若长期断发 <c>1/0</c>，
    /// 服务端 <c>TimerCheckConn</c> 会在连续超时后以原因码 8 调用 <c>CUser::DisConnSig</c> 下发 <c>0/199</c> 并断线。
    /// </para>
    /// </summary>
    public static class SessionLifecyclePayloads
    {
        /// <summary><c>1/7 RETURN_SELECT_CHARACTER</c> 命令包号（类 1，0x07）。</summary>
        public const ushort ReturnToSelectCharacterPacketId = 7;

        /// <summary><c>1/45 GIVEUP_GAME</c> 命令包号（类 1，0x2d）。</summary>
        public const ushort GiveUpGamePacketId = 45;

        /// <summary>服务端 <c>CUser::DisConnSig</c> 踢线原因通知包号（类 0，即 <see cref="PacketClass.Noti"/>，0xC7）。</summary>
        public const int DisconnectReasonPacketId = 199;

        /// <summary><c>TimerCheckConn</c> 连续超时触发的踢线原因码（0x08）。</summary>
        public const int HeartbeatTimeoutReason = 8;

        /// <summary>
        /// 构造 <c>1/45 GIVEUP_GAME</c> 上行载荷（0 字节空包）。
        /// <para>服务端 <c>DisPatcher_GiveUpGame::send</c>（0x08125a98）为空桩，成功无 <c>(1, 45)</c> 回包；回城结果通过 <c>0/23 USER_AREA</c> 与 <c>0/24 AREA_USERS</c> 下发。</para>
        /// </summary>
        public static byte[] BuildGiveUpGame()
        {
            return Array.Empty<byte>();
        }

        /// <summary>构造 <c>1/7 RETURN_SELECT_CHARACTER</c> 上行载荷（0 字节空包）。</summary>
        public static byte[] BuildReturnToSelectCharacter()
        {
            return Array.Empty<byte>();
        }

        /// <summary>
        /// 解析 <c>(1, 7) RETURN_SELECT_CHARACTER</c> 服务端回复（<c>DisPatcher_ReturnToSelectCharacter::dispatch_sig</c> 0x081fd25c）。
        /// <para>成功回 <c>[u8 1]</c>（1 字节）；失败回 <c>[u8 0, u8 errorCode]</c>（2 字节，例如状态或城镇非法时为 0x13）。</para>
        /// </summary>
        /// <param name="payload">回复明文载荷；允许为 null。</param>
        /// <param name="notice">成功时返回只读记录；失败时为 null。</param>
        /// <returns>符合成功或失败布局时返回 true；null、截断或未知状态码返回 false 且不抛异常。</returns>
        public static bool TryParseReturnToSelectReply(byte[] payload, out ReturnToSelectReplyNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte status))
            {
                return false;
            }

            if (status == 1)
            {
                notice = new ReturnToSelectReplyNotice(true, 0, reader.Remaining);
                return true;
            }

            if (status == 0)
            {
                if (!reader.TryGetByte(out byte errorCode))
                {
                    return false;
                }
                notice = new ReturnToSelectReplyNotice(false, errorCode, reader.Remaining);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 解析 <c>0/199 (0xC7) DISCONNECT_REASON</c> 服务端踢线原因通知（<c>CUser::DisConnSig</c> 0x08648fda：<c>i32 reason</c>，4 字节小端）。
        /// </summary>
        /// <param name="payload">通知明文载荷；允许为 null。</param>
        /// <param name="notice">成功时返回只读记录；失败时为 null。</param>
        /// <returns>不少于 4 字节时返回 true；其余情况返回 false 且不抛异常。</returns>
        public static bool TryParseDisconnectReason(byte[] payload, out DisconnectReasonNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetInt(out int reason))
            {
                return false;
            }

            notice = new DisconnectReasonNotice(reason, reader.Remaining);
            return true;
        }
    }

    /// <summary><c>(1, 7) RETURN_SELECT_CHARACTER</c> 服务端回复只读记录。</summary>
    public sealed class ReturnToSelectReplyNotice
    {
        internal ReturnToSelectReplyNotice(bool succeeded, byte errorCode, int trailingByteCount)
        {
            Succeeded = succeeded;
            ErrorCode = errorCode;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>是否成功退回选角界面。</summary>
        public bool Succeeded { get; }

        /// <summary>失败时的服务端错误码（如 0x13）；成功时为 0。</summary>
        public byte ErrorCode { get; }

        /// <summary>读完变长回复后剩余的尾字节数。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary><c>0/199 (0xC7)</c> 服务端踢线原因通知只读记录。</summary>
    public sealed class DisconnectReasonNotice
    {
        internal DisconnectReasonNotice(int reason, int trailingByteCount)
        {
            Reason = reason;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>踢线原因码（如 8 表示 <c>TimerCheckConn</c> 连续超时）。</summary>
        public int Reason { get; }

        /// <summary>是否为心跳超时（原因码 8）。</summary>
        public bool IsHeartbeatTimeout => Reason == SessionLifecyclePayloads.HeartbeatTimeoutReason;

        /// <summary>读完 4 字节后剩余的尾字节数。</summary>
        public int TrailingByteCount { get; }
    }
}
