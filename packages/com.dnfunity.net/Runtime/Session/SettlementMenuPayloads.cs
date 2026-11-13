using System;
using System.Collections.Generic;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary><c>1/75 EPLP_COMMAND</c> 的结算后菜单命令枚举。</summary>
    public enum EplpCommand : byte
    {
        /// <summary>0：再来一次（触发 3 秒后回城重建上下文，再由客户端发 <c>1/15 + 1/16</c> 进同一副本）。</summary>
        Retry = 0,

        /// <summary>1：选择其它副本（触发 3 秒后回城到 <c>state == 3</c>）。</summary>
        SelectOtherDungeon = 1,

        /// <summary>2：返回城镇（触发 1 秒后回城到 <c>state == 3</c>）。</summary>
        ReturnToTown = 2,

        /// <summary>3：打开神秘商店并暂停 60 秒自动回城定时器。</summary>
        OpenSecretShop = 3,
    }

    /// <summary>
    /// 副本结算后快速推进与菜单选择命令（<c>1/72..1/75</c>）及配对服务端回复的载荷构造/解析器。
    /// <para>
    /// 闭环必需性：通关进入结算阶段后，服务端先经历评分卷轴、翻牌资格与翻牌阶段，随后才进入 <c>EPLPState == 2</c> 并启动 60 秒等待定时器；
    /// 若不发 <c>1/72</c>、<c>1/73</c>、<c>1/74</c>，需干等 30 秒 + 4 秒/30 秒服务端定时器，提前发送 <c>1/75</c> 会因 <c>EPLPState != 2</c> 被拒绝；
    /// 进入 <c>EPLPState == 2</c> 后必须发送 <c>1/75</c> 才能立即执行回城/再来一次/选其他副本，否则要干等 60 秒超时（<c>1/135 BACK_2_VILLAGE</c> 在 <c>EPLPState != 0</c> 时会被服务端直接拒绝）。
    /// </para>
    /// <para>
    /// 服务端依据：<c>Dispatcher_Score_Scroll_State::dispatch_sig</c>（0x081ff344）、<c>Dispatcher_Card_Select_Right_State::dispatch_sig</c>（0x081ff504）、
    /// <c>Dispatcher_Select_Card::dispatch_sig</c>（0x081ff7de）、<c>Dispatcher_EPLPCommand::dispatch_sig</c>（0x081feedc）、
    /// <c>_BroadCastPacket</c>、<c>_SendEPLPError</c>（0x081fed4c）与 <c>CParty::ReturnToVillage</c>（0x085aca60）。
    /// </para>
    /// </summary>
    public static class SettlementMenuPayloads
    {
        /// <summary><c>1/72 SCORE_SCROLL_STATE</c> 命令包号（类 1，0x48）。</summary>
        public const ushort ScoreScrollStatePacketId = 72;

        /// <summary><c>1/73 CARD_SELECT_RIGHT_STATE</c> 命令包号（类 1，0x49）。</summary>
        public const ushort CardSelectRightStatePacketId = 73;

        /// <summary><c>1/74 SELECT_CARD</c> 命令包号（类 1，0x4a）。</summary>
        public const ushort SelectCardPacketId = 74;

        /// <summary><c>1/75 EPLP_COMMAND</c> 命令包号（类 1，0x4b）。</summary>
        public const ushort EplpCommandPacketId = 75;

        /// <summary><c>1/74 SELECT_CARD</c> 模式字段上界（服务端仅接受 0 或 1）。</summary>
        public const byte MaxSelectCardMode = 1;

        /// <summary><c>1/75 EPLP_COMMAND</c> 命令字段上界（0..3）。</summary>
        public const byte MaxEplpCommand = (byte)EplpCommand.OpenSecretShop;

        /// <summary><c>1/75 EPLP_COMMAND</c> 标志位：执行所选命令。</summary>
        public const byte EplpFlagExecute = 1;

        /// <summary><c>1/75 EPLP_COMMAND</c> 标志位：仅 UI 光标同步。</summary>
        public const byte EplpFlagUiSync = 2;

        /// <summary><c>(1, 73)</c> 回包固定包含的队伍槽位数。</summary>
        public const int CardSelectRightSlotCount = 4;

        /// <summary>构造 <c>1/72 SCORE_SCROLL_STATE</c> 上行载荷（0 字节空包）。</summary>
        public static byte[] BuildScoreScrollState()
        {
            return Array.Empty<byte>();
        }

        /// <summary>
        /// 解析 <c>(1, 72) SCORE_SCROLL_STATE</c> 服务端广播回复（<c>[u8 1]</c>，1 字节）。
        /// </summary>
        /// <param name="payload">回复明文载荷；允许为 null。</param>
        /// <param name="notice">成功时返回只读记录；失败时为 null。</param>
        /// <returns>读取到状态字节 1 时返回 true；null、空包或首字节不为 1 时返回 false。</returns>
        public static bool TryParseScoreScrollReply(byte[] payload, out ScoreScrollReplyNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte status) || status != 1)
            {
                return false;
            }

            notice = new ScoreScrollReplyNotice(status, reader.Remaining);
            return true;
        }

        /// <summary>构造 <c>1/73 CARD_SELECT_RIGHT_STATE</c> 上行载荷（0 字节空包）。</summary>
        public static byte[] BuildCardSelectRightState()
        {
            return Array.Empty<byte>();
        }

        /// <summary>
        /// 解析 <c>(1, 73) CARD_SELECT_RIGHT_STATE</c> 服务端广播回复（<c>[u8 1, i16×4]</c>，9 字节小端）。
        /// <para>每个槽位值为 -1（无成员）、0（无翻牌权/疲劳耗尽）或 1（可翻牌）。</para>
        /// </summary>
        /// <param name="payload">回复明文载荷；允许为 null。</param>
        /// <param name="notice">成功时返回只读记录；失败时为 null。</param>
        /// <returns>状态字节为 1 且完整读出 4 个槽位时返回 true；其余情况返回 false。</returns>
        public static bool TryParseCardSelectRightReply(byte[] payload, out CardSelectRightReplyNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte status) || status != 1)
            {
                return false;
            }

            var slots = new short[CardSelectRightSlotCount];
            for (int i = 0; i < CardSelectRightSlotCount; i++)
            {
                if (!reader.TryGetShort(out short slotState))
                {
                    return false;
                }
                slots[i] = slotState;
            }

            notice = new CardSelectRightReplyNotice(slots, reader.Remaining);
            return true;
        }

        /// <summary>
        /// 构造 <c>1/74 SELECT_CARD</c> 上行载荷（2 字节：<c>u8 mode, u8 cardIndex</c>）。
        /// </summary>
        /// <param name="mode">翻牌区模式，服务端在 0x081ff8c2 后校验须为 0 或 1。</param>
        /// <param name="cardIndex">牌位序号。</param>
        /// <returns>2 字节明文载荷。</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> 大于 1 时抛出。</exception>
        public static byte[] BuildSelectCard(byte mode, byte cardIndex)
        {
            if (!TryBuildSelectCard(mode, cardIndex, out byte[] payload))
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "SELECT_CARD mode must be 0 or 1.");
            }
            return payload;
        }

        /// <summary>
        /// 尝试构造 <c>1/74 SELECT_CARD</c> 上行载荷（2 字节：<c>u8 mode, u8 cardIndex</c>）。
        /// </summary>
        /// <param name="mode">翻牌区模式（0 或 1）。</param>
        /// <param name="cardIndex">牌位序号。</param>
        /// <param name="payload">成功时输出 2 字节载荷；模式越界时为 null。</param>
        /// <returns>参数合法时返回 true；<paramref name="mode"/> 超出 0..1 返回 false。</returns>
        public static bool TryBuildSelectCard(byte mode, byte cardIndex, out byte[] payload)
        {
            payload = null;
            if (mode > MaxSelectCardMode)
            {
                return false;
            }

            var writer = new PacketWriter(0);
            writer.PutByte(mode);
            writer.PutByte(cardIndex);
            payload = writer.ToArray();
            return true;
        }

        /// <summary>
        /// 构造 <c>1/75 EPLP_COMMAND</c> 上行载荷（2 字节：<c>u8 flag, u8 command</c>）。
        /// </summary>
        /// <param name="flag">标志位（1=执行，2=UI 同步）。</param>
        /// <param name="command">菜单命令（0..3）。</param>
        /// <returns>2 字节明文载荷。</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="command"/> 超出 0..3 时抛出。</exception>
        public static byte[] BuildEplpCommand(byte flag, EplpCommand command)
        {
            if (!TryBuildEplpCommand(flag, (byte)command, out byte[] payload))
            {
                throw new ArgumentOutOfRangeException(nameof(command), command, "EPLP command must be 0..3.");
            }
            return payload;
        }

        /// <summary>
        /// 尝试构造 <c>1/75 EPLP_COMMAND</c> 上行载荷（2 字节：<c>u8 flag, u8 commandRaw</c>）。
        /// </summary>
        /// <param name="flag">标志位（1=执行，2=UI 同步）。</param>
        /// <param name="commandRaw">原始命令值（0..3）。</param>
        /// <param name="payload">成功时输出 2 字节载荷；命令越界时为 null。</param>
        /// <returns><paramref name="commandRaw"/> 在 0..3 范围内时返回 true，否则返回 false。</returns>
        public static bool TryBuildEplpCommand(byte flag, byte commandRaw, out byte[] payload)
        {
            payload = null;
            if (commandRaw > MaxEplpCommand)
            {
                return false;
            }

            var writer = new PacketWriter(0);
            writer.PutByte(flag);
            writer.PutByte(commandRaw);
            payload = writer.ToArray();
            return true;
        }

        /// <summary>
        /// 解析 <c>(1, 75) EPLP_COMMAND</c> 服务端回复（3 字节）。
        /// <para>
        /// 写序歧义已由服务端反汇编确认：
        /// 成功广播（<c>_BroadCastPacket</c>）为 <c>[u8 1, u8 flag, u8 command]</c>；
        /// 失败错误（<c>_SendEPLPError</c> 0x081fed4c）为 <c>[u8 0, u8 errorCode, u8 command]</c>（第 2 字节为错误码，第 3 字节才是命令）。
        /// </para>
        /// </summary>
        /// <param name="payload">回复明文载荷；允许为 null。</param>
        /// <param name="notice">成功时返回只读记录；失败时为 null。</param>
        /// <returns>长度不少于 3 字节且首字节为 0 或 1 时返回 true；其余情况返回 false。</returns>
        public static bool TryParseEplpReply(byte[] payload, out EplpReplyNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte status)
                || !reader.TryGetByte(out byte second)
                || !reader.TryGetByte(out byte commandRaw))
            {
                return false;
            }

            if (status == 1)
            {
                notice = new EplpReplyNotice(true, second, 0, commandRaw, reader.Remaining);
                return true;
            }

            if (status == 0)
            {
                notice = new EplpReplyNotice(false, 0, second, commandRaw, reader.Remaining);
                return true;
            }

            return false;
        }
    }

    /// <summary><c>(1, 72) SCORE_SCROLL_STATE</c> 广播回复只读记录。</summary>
    public sealed class ScoreScrollReplyNotice
    {
        internal ScoreScrollReplyNotice(byte status, int trailingByteCount)
        {
            Status = status;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>状态字节（固定为 1）。</summary>
        public byte Status { get; }

        /// <summary>读完状态字节后剩余的尾字节数。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary><c>(1, 73) CARD_SELECT_RIGHT_STATE</c> 广播回复只读记录。</summary>
    public sealed class CardSelectRightReplyNotice
    {
        internal CardSelectRightReplyNotice(short[] slotStates, int trailingByteCount)
        {
            SlotStates = Array.AsReadOnly(slotStates);
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>4 个队伍槽位的翻牌权限（-1=无成员，0=无权，1=有权）。</summary>
        public IReadOnlyList<short> SlotStates { get; }

        /// <summary>读完 9 字节后剩余的尾字节数。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary><c>(1, 75) EPLP_COMMAND</c> 服务端回复只读记录（包含成功广播与错误回包两种变体）。</summary>
    public sealed class EplpReplyNotice
    {
        internal EplpReplyNotice(bool succeeded, byte flag, byte errorCode, byte commandRaw, int trailingByteCount)
        {
            Succeeded = succeeded;
            Flag = flag;
            ErrorCode = errorCode;
            CommandRaw = commandRaw;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>是否成功广播（true=首字节 1；false=首字节 0 表示失败）。</summary>
        public bool Succeeded { get; }

        /// <summary>成功时的标志位（1=执行，2=UI 同步）；失败变体中无此字段，固定为 0。</summary>
        public byte Flag { get; }

        /// <summary>失败时的服务端错误码（如 8=非队长，0x85=神秘商店未购买，1=非法命令）；成功时为 0。</summary>
        public byte ErrorCode { get; }

        /// <summary>第 3 字节原始命令值。</summary>
        public byte CommandRaw { get; }

        /// <summary>当 <see cref="CommandRaw"/> 落在 0..3 内时返回强类型枚举；否则为 null。</summary>
        public EplpCommand? Command => CommandRaw <= SettlementMenuPayloads.MaxEplpCommand
            ? (EplpCommand)CommandRaw
            : (EplpCommand?)null;

        /// <summary>读完 3 字节后剩余的尾字节数。</summary>
        public int TrailingByteCount { get; }
    }
}
