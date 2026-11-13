using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 选角回包(类 1、包号 <see cref="PacketId"/>,即选角命令 4 的应答)的明文载荷解析器:把静态读码定案的读序落成只读记录 <see cref="SelectCharacterResult"/>
    /// ——结果码,以及成功时的载入字段 <see cref="SelectCharacterLoadInfo"/>。包名表里它叫 SELECT_CHARACTER(台账 E-382)。
    /// <para>
    /// 范围:纯明文解析。进城后这个回包在探针的明文流里被封在 561 信封内,信封内层读通之后(会话层把内层帧紧跟在信封帧后面交付),
    /// 把内层帧的载荷直接喂给 <see cref="TryParse"/>;解析器不看包类与包号(调用方先认,包号常量见 <see cref="PacketId"/>),不接会话,不碰信封。
    /// 它不改选角的流程语义:判定"这个槽进没进城"的仍是 <see cref="SlotJudge"/>,解析结果只是随判定一起送出去的附加信息(见 <see cref="SlotOutcome.SelectResult"/>)。
    /// </para>
    /// <para>
    /// 约定(与 <see cref="DungeonStartParser"/>、<see cref="TownEntryParser"/> 同风格):短包、读越界一律返回 false 且 result 为 null,绝不抛异常(包括 null 载荷);
    /// 读完最后一个字段即停,容忍尾部多余字节(只在 <see cref="SelectCharacterResult.TrailingByteCount"/> 报告个数,不核对,不要求"读完恰好等于载荷末尾");
    /// 字段取值不做范围检查。<b>与 <see cref="SlotJudge"/> 的口径不同</b>:判定只看首字节与次字节,所以 <c>[1, 9, 9]</c> 这样的短回包它判"进城",
    /// 这里整包读不通,返回 false(成功体必须读全,不能因为首字节非 0 就当作有载入字段)。
    /// </para>
    /// <para>
    /// 读序(小端;packetread_NOTES §3.3 的【观】,客户端处理器 0x71afa0..0x71b7f0,派发层读成功标志 0x707b94、失败码 0x707be9;该笔记被 .gitignore 忽略,不入库):
    /// <c>u8 flag</c>;flag 为 0 时再 <c>u8 err</c>,失败体到此为止。成功体接着是
    /// <c>u32, u32, u16 × 4, u8 N, N × {u8, u32}, u32, 20 × {u16, u32}, 5 × u32, u8, u8, u8 b, b × u8, u16, u16, u8</c>,
    /// 字段含义与客户端落点见 <see cref="SelectCharacterLoadInfo"/>。总长 = 1 + 169 + 5 × N + b(成功体),失败体 2 字节。
    /// 服务端的写序(<c>Inter_LoadEtc::SendResult</c> 0x84c2b82..0x84c311a,唯一写点 0x84c2bab)与它逐字段吻合。
    /// </para>
    /// <para>
    /// 【客户端行为,解析器都不模拟】(1)教程下标表:客户端只读 min(b, 0x4e) 个,b 超过时它自己的后续字段整体错位(客户端数组只占 0x50 字节;
    /// 运行期唯一的置位入口只接受下标不大于 0x4d,所以正常路径上 b 不超过 0x4e);解析器按服务端语义读满 b 个,不复刻这个上限。
    /// (2)金额型字段为负时客户端重写为 0:解析器给带符号原值。(3)读原语越界时客户端只记日志、存一份包副本并继续读垃圾:解析器每次读前检查剩余长度,不够就整包丢弃。
    /// 所以"解析成功"只表示布局读得下来,不表示客户端会按同一个字节边界消费这个包。
    /// </para>
    /// <para>
    /// 失败体:服务端对失败只发 <c>[0][err]</c>;会真正送达客户端的 err 只有 0x13、4、0x9f、8(<see cref="MapFailureCode"/> 映射);其余失败静默或断开连接,
    /// 不要等一个失败体。客户端另对 0x60 特判,本地服务端没有它的发送路径(来源未知),映射里单列。
    /// 成功回包之后服务端紧跟一个独立的 (1, 0x24b) 回包(<c>AVATAR_COIN_COUNT</c>),它不属于这个包的载荷,这里不解析。
    /// </para>
    /// </summary>
    public static class SelectCharacterResultParser
    {
        /// <summary>选角回包的包号(类 1,即 <see cref="PacketClass.Cmd"/>),与选角命令同号。</summary>
        public const int PacketId = SlotJudge.SelectPacketId;

        /// <summary>任务表的固定项数(服务端写 20 项,客户端向量上限也是 20)。</summary>
        public const int QuestSlotCount = 20;

        /// <summary>任务提醒值的固定个数。</summary>
        public const int QuestNotifyValueCount = 5;

        /// <summary>
        /// 解析一份载荷(从第一个字段起)。
        /// </summary>
        /// <param name="payload">回包载荷;可以为 null。</param>
        /// <param name="result">成功时是只读记录;失败时为 null。</param>
        /// <returns>
        /// 读完全部字段为 true(失败体读完 flag 与 err 即为 true);载荷为 null、过短或某个个数声明超出载荷为 false。不抛异常。
        /// </returns>
        public static bool TryParse(byte[] payload, out SelectCharacterResult result)
        {
            result = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte flag))
            {
                return false;
            }

            if (flag == 0)
            {
                if (!reader.TryGetByte(out byte failureCode))
                {
                    return false;
                }
                result = new SelectCharacterResult(flag, failureCode, null, reader.Remaining);
                return true;
            }

            if (!TryReadLoadInfo(reader, out SelectCharacterLoadInfo load))
            {
                return false;
            }
            result = new SelectCharacterResult(flag, 0, load, reader.Remaining);
            return true;
        }

        /// <summary>
        /// 把失败码映射成 <see cref="SelectCharacterFailure"/>:0x13、4、0x9f、8 是本地服务端会送达的四个码,0x60 是客户端特判码,其余(含 0)为 Unknown。
        /// 只看码本身,不看成功标志(成功体不调用它)。
        /// </summary>
        public static SelectCharacterFailure MapFailureCode(byte failureCode)
        {
            switch (failureCode)
            {
                case 0x13:
                    return SelectCharacterFailure.CreationLocked;
                case 4:
                    return SelectCharacterFailure.SlotOutOfRange;
                case 0x9f:
                    return SelectCharacterFailure.NameHasSlang;
                case 8:
                    return SelectCharacterFailure.NamePrefixForbidden;
                case 0x60:
                    return SelectCharacterFailure.ClientSpecial;
                default:
                    return SelectCharacterFailure.Unknown;
            }
        }

        private static bool TryReadLoadInfo(PacketReader reader, out SelectCharacterLoadInfo load)
        {
            load = null;
            if (!reader.TryGetUInt(out uint regTime)
                || !reader.TryGetUInt(out uint createTime)
                || !reader.TryGetUShort(out ushort uniqueId)
                || !reader.TryGetUShort(out ushort totalFatigue)
                || !reader.TryGetUShort(out ushort totalMaxFatigue)
                || !reader.TryGetUShort(out ushort usedFatigue)
                || !TryReadPremiums(reader, out SelectCharacterPremium[] premiums)
                || !reader.TryGetInt(out int cera)
                || !TryReadQuestSlots(reader, out SelectCharacterQuestSlot[] questSlots)
                || !TryReadQuestNotifyValues(reader, out uint[] questNotifyValues)
                || !reader.TryGetByte(out byte village)
                || !reader.TryGetByte(out byte tutorialSkipable)
                || !TryReadTutorialFlagIndices(reader, out byte[] tutorialFlagIndices)
                || !reader.TryGetUShort(out ushort fatigueBatteryCharging)
                || !reader.TryGetUShort(out ushort fatigueGrownUpBuff)
                || !reader.TryGetByte(out byte tradePunishOverThirtyDays))
            {
                return false;
            }

            load = new SelectCharacterLoadInfo(
                regTime, createTime, uniqueId,
                totalFatigue, totalMaxFatigue, usedFatigue,
                premiums, cera,
                questSlots, questNotifyValues,
                village, tutorialSkipable, tutorialFlagIndices,
                fatigueBatteryCharging, fatigueGrownUpBuff, tradePunishOverThirtyDays);
            return true;
        }

        // u8 个数 N + N 条 {u8 类型, i32 剩余秒},每条 5 字节。
        private static bool TryReadPremiums(PacketReader reader, out SelectCharacterPremium[] premiums)
        {
            premiums = null;
            if (!reader.TryGetByte(out byte count))
            {
                return false;
            }

            var list = new SelectCharacterPremium[count];
            for (int i = 0; i < list.Length; i++)
            {
                if (!reader.TryGetByte(out byte id) || !reader.TryGetInt(out int remainingSeconds))
                {
                    return false;
                }
                list[i] = new SelectCharacterPremium(id, remainingSeconds);
            }

            premiums = list;
            return true;
        }

        // 固定 20 项 {u16 键, u32 值},每项 6 字节;空槽(键 0xFFFF)的值照读,不跳过。
        private static bool TryReadQuestSlots(PacketReader reader, out SelectCharacterQuestSlot[] slots)
        {
            slots = null;
            var list = new SelectCharacterQuestSlot[QuestSlotCount];
            for (int i = 0; i < list.Length; i++)
            {
                if (!reader.TryGetUShort(out ushort key) || !reader.TryGetUInt(out uint value))
                {
                    return false;
                }
                list[i] = new SelectCharacterQuestSlot(key, value);
            }

            slots = list;
            return true;
        }

        // 固定 5 个 u32。
        private static bool TryReadQuestNotifyValues(PacketReader reader, out uint[] values)
        {
            values = null;
            var list = new uint[QuestNotifyValueCount];
            for (int i = 0; i < list.Length; i++)
            {
                if (!reader.TryGetUInt(out uint value))
                {
                    return false;
                }
                list[i] = value;
            }

            values = list;
            return true;
        }

        // u8 个数 b + b 个 u8(读满 b,不套客户端的 min(b, 0x4e),见类说明)。
        private static bool TryReadTutorialFlagIndices(PacketReader reader, out byte[] indices)
        {
            indices = null;
            if (!reader.TryGetByte(out byte count))
            {
                return false;
            }

            var list = new byte[count];
            for (int i = 0; i < list.Length; i++)
            {
                if (!reader.TryGetByte(out byte index))
                {
                    return false;
                }
                list[i] = index;
            }

            indices = list;
            return true;
        }
    }
}
