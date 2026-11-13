using System;
using System.Collections.Generic;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 城镇换区命令（类 1、包号 <see cref="SetUserAreaPacketId"/>，<c>SET_USER_AREA</c>）及其配对服务端回复与区通知的载荷构造/解析器。
    /// <para>
    /// 闭环必需性：进城后若需切换到副本门口区（服务端 <c>DisPatcher_StartGame</c> 检查 <c>*(area + 0x68) == 5 || 6</c>）或其它城镇区域，
    /// 必须发送 <c>1/38</c>；不发则始终停留在初始区域，无法进入副本门口区开局，也不会触发目标区的 <c>0/23</c> 与 <c>0/24</c> 下发。
    /// </para>
    /// <para>
    /// 服务端依据：<c>DisPatcher_SetArea::read</c>（0x081c91e6）、<c>check_error</c>（0x081c93e8）、<c>process</c>（0x081c951c）、
    /// <c>send</c>（0x081c9656）、<c>GameWorld::move_area</c>（0x086c5a84）、<c>Area::insert_user</c>（0x086c25a6）与
    /// <c>Area::send_area_users</c>（0x086c2780）；客户端发送点位于 0x0103cd8d、0x01112b81、0x01116aa8。
    /// </para>
    /// </summary>
    public static class TownAreaPayloads
    {
        /// <summary>城镇换区命令包号（类 1，即 <see cref="PacketClass.Cmd"/>，0x26）。</summary>
        public const ushort SetUserAreaPacketId = 38;

        /// <summary><c>1/38 SET_USER_AREA</c> 上行载荷固定长度（11 字节，小端）。</summary>
        public const int SetUserAreaPayloadLength = 11;

        /// <summary>客户端三个换区发送点写入的默认标志字节。</summary>
        public const byte DefaultSetAreaFlags = 5;

        /// <summary>禁止换区惩罚错误码（0x44），该错误码后额外附带 4 字节剩余分钟数。</summary>
        public const byte ForbiddenMoveErrorCode = 0x44;

        /// <summary>单人进/出区通知包号（类 0，即 <see cref="PacketClass.Noti"/>，0x17）。</summary>
        public const int UserAreaPacketId = 23;

        /// <summary><c>0/23 USER_AREA</c> 固定载荷长度（10 字节，小端）。</summary>
        public const int UserAreaPayloadLength = 10;

        /// <summary>区域用户列表通知包号（类 0，即 <see cref="PacketClass.Noti"/>，0x18）。</summary>
        public const int AreaUsersPacketId = 24;

        /// <summary><c>0/24 AREA_USERS</c> 固定头部字节数（village + area + count）。</summary>
        public const int AreaUsersHeaderLength = 4;

        /// <summary><c>0/24 AREA_USERS</c> 单条用户记录字节数（uniqueId + x + y + direction + visible）。</summary>
        public const int AreaUserEntryLength = 8;

        /// <summary>
        /// 构造 <c>1/38 SET_USER_AREA</c> 请求载荷（11 字节，小端）。
        /// <para>写序：<c>u8 targetVillage, u8 targetArea, u16 x, u16 y, u8 flags, u16 srcVillage, u16 srcArea</c>。</para>
        /// </summary>
        /// <param name="targetVillage">目标城镇编号。</param>
        /// <param name="targetArea">目标区域编号。</param>
        /// <param name="x">目标 X 坐标。</param>
        /// <param name="y">目标 Y 坐标。</param>
        /// <param name="srcVillage">源城镇编号（无明确来源时可填 0）。</param>
        /// <param name="srcArea">源区域编号（无明确来源时可填 0）。</param>
        /// <param name="flags">移动标志字节，客户端默认写 <see cref="DefaultSetAreaFlags"/>（5）。</param>
        /// <returns>11 字节小端明文载荷。</returns>
        public static byte[] BuildSetUserArea(
            byte targetVillage,
            byte targetArea,
            ushort x,
            ushort y,
            ushort srcVillage = 0,
            ushort srcArea = 0,
            byte flags = DefaultSetAreaFlags)
        {
            var writer = new PacketWriter(0);
            writer.PutByte(targetVillage);
            writer.PutByte(targetArea);
            writer.PutShort(x);
            writer.PutShort(y);
            writer.PutByte(flags);
            writer.PutShort(srcVillage);
            writer.PutShort(srcArea);
            return writer.ToArray();
        }

        /// <summary>
        /// 解析 <c>(1, 38) SET_USER_AREA</c> 服务端错误回复。
        /// <para>
        /// 服务端在换区成功或 <c>state != 3</c>（0x7fffffff）时不发 <c>(1, 38)</c>，仅在失败时下发：
        /// 常规失败为 <c>u8 0, u8 errorCode, u8 arg</c>（3 字节）；
        /// 当 <c>errorCode == 0x44</c> 时为 <c>u8 0, u8 0x44, u8 arg, i32 remainingMinutes</c>（7 字节，分钟数由毫秒除以 60000 得到）。
        /// </para>
        /// </summary>
        /// <param name="payload">回复明文载荷；允许为 null。</param>
        /// <param name="notice">成功时返回只读错误记录；失败时为 null。</param>
        /// <returns>满足对应错误码最小长度且首字节为 0 时返回 true；其余情况返回 false 且不抛异常。</returns>
        public static bool TryParseSetUserAreaError(byte[] payload, out SetUserAreaErrorNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte status)
                || status != 0
                || !reader.TryGetByte(out byte errorCode)
                || !reader.TryGetByte(out byte arg))
            {
                return false;
            }

            int? remainingMinutes = null;
            if (errorCode == ForbiddenMoveErrorCode)
            {
                if (!reader.TryGetInt(out int minutes))
                {
                    return false;
                }
                remainingMinutes = minutes;
            }

            notice = new SetUserAreaErrorNotice(errorCode, arg, remainingMinutes, reader.Remaining);
            return true;
        }

        /// <summary>
        /// 解析 <c>0/23 USER_AREA</c> 通知载荷（10 字节，小端）。
        /// <para>读序（<c>Area::insert_user</c> 0x086c25a6 / <c>Area::delete_user</c> 0x086c2a38）：<c>u16 uniqueId, u8 village, u8 area, u16 x, u16 y, u8 direction, u8 visible</c>。</para>
        /// </summary>
        /// <param name="payload">通知明文载荷；允许为 null。</param>
        /// <param name="notice">成功时返回只读记录；失败时为 null。</param>
        /// <returns>不少于 10 字节时返回 true；短包或 null 返回 false 且不抛异常。</returns>
        public static bool TryParseUserArea(byte[] payload, out UserAreaNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetUShort(out ushort uniqueId)
                || !reader.TryGetByte(out byte village)
                || !reader.TryGetByte(out byte area)
                || !reader.TryGetUShort(out ushort x)
                || !reader.TryGetUShort(out ushort y)
                || !reader.TryGetByte(out byte direction)
                || !reader.TryGetByte(out byte visible))
            {
                return false;
            }

            notice = new UserAreaNotice(uniqueId, village, area, x, y, direction, visible, reader.Remaining);
            return true;
        }

        /// <summary>
        /// 解析 <c>0/24 AREA_USERS</c> 通知载荷（小端）。
        /// <para>
        /// 读序（<c>Area::send_area_users</c> 0x086c2780）：<c>u8 village, u8 area, u16 count, count × { u16 uniqueId, u16 x, u16 y, u8 direction, u8 visible }</c>。
        /// </para>
        /// </summary>
        /// <param name="payload">通知明文载荷；允许为 null。</param>
        /// <param name="notice">成功时返回只读记录；失败时为 null。</param>
        /// <returns>头部与全部条目均读通时返回 true；越界或声明数目超出剩余字节时返回 false 且不抛异常。</returns>
        public static bool TryParseAreaUsers(byte[] payload, out AreaUsersNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte village)
                || !reader.TryGetByte(out byte area)
                || !reader.TryGetUShort(out ushort count))
            {
                return false;
            }

            if (count > reader.Remaining / AreaUserEntryLength)
            {
                return false;
            }

            var entries = count == 0 ? Array.Empty<AreaUserEntry>() : new AreaUserEntry[count];
            for (int i = 0; i < count; i++)
            {
                if (!reader.TryGetUShort(out ushort uniqueId)
                    || !reader.TryGetUShort(out ushort x)
                    || !reader.TryGetUShort(out ushort y)
                    || !reader.TryGetByte(out byte direction)
                    || !reader.TryGetByte(out byte visible))
                {
                    return false;
                }
                entries[i] = new AreaUserEntry(uniqueId, x, y, direction, visible);
            }

            notice = new AreaUsersNotice(village, area, entries, reader.Remaining);
            return true;
        }
    }

    /// <summary><c>(1, 38) SET_USER_AREA</c> 服务端错误回复的只读记录。</summary>
    public sealed class SetUserAreaErrorNotice
    {
        internal SetUserAreaErrorNotice(byte errorCode, byte arg, int? forbiddenMoveRemainingMinutes, int trailingByteCount)
        {
            ErrorCode = errorCode;
            Arg = arg;
            ForbiddenMoveRemainingMinutes = forbiddenMoveRemainingMinutes;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>服务端错误码（如 0x13/0x15/0x3c/0x44/0xbf/4/7/8）。</summary>
        public byte ErrorCode { get; }

        /// <summary>第 3 字节原始参数（通常为目标或限制相关城镇编号/参数字节）。</summary>
        public byte Arg { get; }

        /// <summary>服务端按有符号扩展前的 8 位参数视图（部分失败路径写入 -1 即 0xFF）。</summary>
        public sbyte SignedArg => unchecked((sbyte)Arg);

        /// <summary>仅当 <see cref="ErrorCode"/> 为 0x44 时存在，表示禁止移动剩余分钟数；其它错误码为 null。</summary>
        public int? ForbiddenMoveRemainingMinutes { get; }

        /// <summary>读完错误字段后剩余未消费的尾字节数。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary><c>0/23 USER_AREA</c> 通知解析得到的只读记录。</summary>
    public sealed class UserAreaNotice
    {
        internal UserAreaNotice(
            ushort uniqueId,
            byte village,
            byte area,
            ushort x,
            ushort y,
            byte direction,
            byte visible,
            int trailingByteCount)
        {
            UniqueId = uniqueId;
            Village = village;
            Area = area;
            X = x;
            Y = y;
            Direction = direction;
            Visible = visible;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>角色唯一编号（u16）。</summary>
        public ushort UniqueId { get; }

        /// <summary>所在城镇编号（u8）。</summary>
        public byte Village { get; }

        /// <summary>所在区域编号（u8）。</summary>
        public byte Area { get; }

        /// <summary>角色 X 坐标（u16）。</summary>
        public ushort X { get; }

        /// <summary>角色 Y 坐标（u16）。</summary>
        public ushort Y { get; }

        /// <summary>角色朝向（u8）。</summary>
        public byte Direction { get; }

        /// <summary>可见标志（u8）。</summary>
        public byte Visible { get; }

        /// <summary>读完 10 字节后剩余的尾字节数。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary><c>0/24 AREA_USERS</c> 中的单条角色状态记录。</summary>
    public sealed class AreaUserEntry
    {
        internal AreaUserEntry(ushort uniqueId, ushort x, ushort y, byte direction, byte visible)
        {
            UniqueId = uniqueId;
            X = x;
            Y = y;
            Direction = direction;
            Visible = visible;
        }

        /// <summary>角色唯一编号（u16）。</summary>
        public ushort UniqueId { get; }

        /// <summary>角色 X 坐标（u16）。</summary>
        public ushort X { get; }

        /// <summary>角色 Y 坐标（u16）。</summary>
        public ushort Y { get; }

        /// <summary>角色朝向（u8）。</summary>
        public byte Direction { get; }

        /// <summary>可见标志（u8）。</summary>
        public byte Visible { get; }
    }

    /// <summary><c>0/24 AREA_USERS</c> 区域用户快照通知解析得到的只读记录。</summary>
    public sealed class AreaUsersNotice
    {
        internal AreaUsersNotice(byte village, byte area, AreaUserEntry[] entries, int trailingByteCount)
        {
            Village = village;
            Area = area;
            Entries = Array.AsReadOnly(entries);
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>区域所属城镇编号（u8）。</summary>
        public byte Village { get; }

        /// <summary>区域编号（u8）。</summary>
        public byte Area { get; }

        /// <summary>区域内角色条目只读列表。</summary>
        public IReadOnlyList<AreaUserEntry> Entries { get; }

        /// <summary>读完列表后剩余的尾字节数。</summary>
        public int TrailingByteCount { get; }
    }
}
