using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 通关奖励通知(类 0、包号 <see cref="PacketId"/>,包名表里叫 CLEAR_DUNGEON_REWARD)的明文载荷解析器:把静态读码定案的写序落成只读记录
    /// <see cref="SettlementNoticeB"/>。它是结算两条下行通知里后到的那条(先到的是 <see cref="SettlementNoticeAParser"/> 解的通关结果),
    /// 服务端对每个有效队员各自拼一份单发(<c>CUser::Send</c> 0x085b0d68)。
    /// <para>
    /// 范围:纯明文解析。解析器不看包类与包号(调用方先认,包号常量见 <see cref="PacketId"/>),不接会话,不碰信封,也不接 <see cref="OnlineDungeonCombat"/> 状态机。
    /// </para>
    /// <para>
    /// 约定(与 <see cref="DungeonPermissionParser"/>、<see cref="SettlementNoticeAParser"/> 同风格):短包、读越界、物品个数声明超出载荷、奖励信息状态取值非法
    /// 一律返回 false 且 notice 为 null,绝不抛异常(包括 null 载荷);读完最后一个标志即停,容忍尾部多余字节(只在 <see cref="SettlementNoticeB.TrailingByteCount"/>
    /// 报告个数,不核对,不要求"读完恰好等于载荷末尾");字段取值不做范围检查;不做任何字符串转换。
    /// </para>
    /// <para>
    /// 写序(服务端 <c>CParty::clear_reward</c> 0x085ae0d4,settlenoti_NOTES §2.4 的【观】,该笔记被 .gitignore 忽略,不入库),线上读序即
    /// <c>i32 × 4, u8, i32 × 12, u8 物品个数, 个数 × 15 字节物品条目, 4 × 奖励信息, i32, u8 × 4, 4 × 奖励信息, i32, u8 × 3</c>:
    /// <list type="number">
    /// <item><description><c>put_header(0, 0x23)</c> 0x085b0192。</description></item>
    /// <item><description>经验分项:<c>put_int</c> 4 个(0x085b01b3、0x085b01cb、0x085b0344、0x085b035c),<c>put_byte</c> 活动类型(0x085b0374),
    /// <c>put_int</c> 12 个(0x085b039d 起每个相隔 0x18 到 0x085b04a5);共 16 个 i32 与 1 个 u8,65 字节。</description></item>
    /// <item><description>物品个数占位:<c>get_index</c> 0x085b04b7 记下位置,<c>put_byte(0)</c> 0x085b04d3 先写 0;
    /// 遍历 20 个任务槽,每个任务的清图物品表里每一项:创建物品并放进背包,放进去了才写一条 15 字节条目
    /// (<c>put_short</c> 槽位 0x085b07cd、<c>put_int</c> 编号 0x085b07e5、<c>put_int</c> 附加信息 0x085b0805、<c>put_short</c> 耐久 0x085b0821、
    /// <c>put_byte</c> 增幅类型 0x085b0847、<c>put_short</c> 增幅值 0x085b086d);循环后按<b>回填</b>写入真实个数(<c>put_byte(int&amp;, int)</c> 0x085b097f,
    /// 只改占位那个字节,不推进写位置)。</description></item>
    /// <item><description>第一组奖励信息:槽位 0 到 3,无人写 <c>put_byte(0)</c>(0x085b09b7),有人调 <c>CParty::_makeRewardItemInfo</c> 0x085ad0be(调用点 0x085b0a00)。</description></item>
    /// <item><description><c>put_int</c> 接收者槽位对应的数组项(0x085b0a32);<c>put_byte(0)</c> 4 次(0x085b0a40 起的循环,两个分支都写 0)。</description></item>
    /// <item><description>第二组奖励信息:槽位 0 到 3,无人或该队员的特权类型 0xc 没生效写 <c>put_byte(0)</c>(0x085b0acc、0x085b0b5b),否则调
    /// <c>_makeRewardItemInfo</c>(调用点 0x085b0b43)。</description></item>
    /// <item><description><c>put_int</c> 比例(0x085b0bd0);<c>put_byte</c> 三个标志(0x085b0c44 或 0x085b0c5c、0x085b0cd0 或 0x085b0ce8、0x085b0d11 或 0x085b0d29);
    /// <c>finalize</c> 0x085b0d3f,<c>CUser::Send</c> 0x085b0d68。</description></item>
    /// </list>
    /// <c>_makeRewardItemInfo</c> 的写序:状态字节(2 或 1,0x085ad1ae、0x085ad1c3、0x085ad1fb)、<c>put_int(0)</c>(0x085ad20e)、
    /// <c>put_int</c> 第一件物品的附加信息(0x085ad228);第二件物品编号非 0 时再 <c>put_int</c> 编号(0x085ad24f)与附加信息(0x085ad26c)。
    /// 所以奖励信息只有三种长度:状态 0 为 1 字节,状态 1 为 9 字节,状态 2 为 17 字节。
    /// 载荷总长 = 89 + 15 × 物品个数 + 各奖励信息的额外字节(状态 1 加 8,状态 2 加 16;共 8 个奖励信息)。
    /// </para>
    /// <para>
    /// 【客户端读序没有读到】客户端处理器入口 0x0074ef60(登记点 0x007571a7 调登记函数 0x0074e790:包号 0x23 → 该入口)的第一条指令就是
    /// <c>JMP 0x029b332b</c>,跳进受保护节(#3 到 #8,虚拟化),后面不是真指令;静态读不出它读哪些字节、读几个、读完做什么【未】。
    /// 所以这条通知<b>没有"服务端写序与客户端读序的对账"</b>,只有服务端写序这一侧;"客户端多读少读"无从判断,
    /// 写序的字段边界以服务端为准,解析器按它读。读出客户端读序需要动态手段(见 settlenoti_NOTES §3.3、§8 的解法路径),本线不做。
    /// </para>
    /// <para>
    /// 【服务端的几个已知不确定处】(1)物品个数字节是回填的,个数大于 255 时回绕,解析器按线上字节读;
    /// (2)奖励信息状态字节只可能是 0、1、2,取别的值时无法确定后续长度,解析器返回 false 而不是猜;
    /// (3)比例字段 <see cref="SettlementNoticeB.Percent"/> 是浮点除法的结果,除数为 0 时服务端写出的值不确定,解析器照读;
    /// (4)只有 7 字节全零形那条强制路径(<c>CParty::forced_set_play_result</c>,按调用关系多半发不出)<b>不发</b>这条通知;
    /// 等待结算命令超时走的是完整结算,两条通知照发。状态机在"只收到通关结果、没有通关奖励"时仍应有自己的超时,
    /// 见 <see cref="SettlementNoticeAParser"/> 的 7 字节全零形说明与 settlenoti_NOTES §2.5、§7(该笔记被 .gitignore 忽略,不入库)。
    /// </para>
    /// </summary>
    public static class SettlementNoticeBParser
    {
        /// <summary>通关奖励通知的包号(类 0,即 <see cref="PacketClass.Noti"/>)。</summary>
        public const int PacketId = 0x23;

        // 每条任务物品 15 字节:u16 槽位 + i32 编号 + i32 附加信息 + u16 耐久 + u8 增幅类型 + u16 增幅值。
        private const int ItemEntryLength = 15;

        // 经验分项里夹着 u8 活动类型的位置:先读 4 个 i32,再读这个 u8,再读 12 个 i32。
        private const int ExpKindAfter = 4;

        /// <summary>
        /// 解析一份载荷(从第一个字段起)。
        /// </summary>
        /// <param name="payload">通知载荷;可以为 null。</param>
        /// <param name="notice">成功时是只读记录;失败时为 null。</param>
        /// <returns>读完全部字段为 true;载荷为 null、过短、物品个数声明超出载荷或奖励信息状态非法为 false。不抛异常。</returns>
        public static bool TryParse(byte[] payload, out SettlementNoticeB notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);

            // 经验分项:4 个 i32 + u8 + 12 个 i32。
            var expValues = new int[SettlementNoticeB.ExpValueCount];
            for (int i = 0; i < ExpKindAfter; i++)
            {
                if (!reader.TryGetInt(out expValues[i]))
                {
                    return false;
                }
            }

            if (!reader.TryGetByte(out byte expKind))
            {
                return false;
            }

            for (int i = ExpKindAfter; i < expValues.Length; i++)
            {
                if (!reader.TryGetInt(out expValues[i]))
                {
                    return false;
                }
            }

            // 物品个数声明超出载荷时直接失败,不先按声明分配(u8 个数最多 255 条,几个字节的坏包不该换来一块内存)。
            if (!reader.TryGetByte(out byte itemCount) || (long)itemCount * ItemEntryLength > reader.Remaining)
            {
                return false;
            }

            var questItems = new SettlementItemEntry[itemCount];
            for (int i = 0; i < questItems.Length; i++)
            {
                if (!reader.TryGetUShort(out ushort slot)
                    || !reader.TryGetInt(out int itemId)
                    || !reader.TryGetInt(out int data)
                    || !reader.TryGetUShort(out ushort durability)
                    || !reader.TryGetByte(out byte amplifyType)
                    || !reader.TryGetUShort(out ushort amplifyValue))
                {
                    return false;
                }
                questItems[i] = new SettlementItemEntry(slot, itemId, data, durability, amplifyType, amplifyValue);
            }

            var rewards = new SettlementRewardInfo[SettlementNoticeB.RewardSlotCount];
            for (int i = 0; i < rewards.Length; i++)
            {
                if (!TryReadRewardInfo(reader, out rewards[i]))
                {
                    return false;
                }
            }

            var reservedBytes = new byte[SettlementNoticeB.RewardSlotCount];
            if (!reader.TryGetInt(out int receiverValue))
            {
                return false;
            }

            for (int i = 0; i < reservedBytes.Length; i++)
            {
                if (!reader.TryGetByte(out reservedBytes[i]))
                {
                    return false;
                }
            }

            var premiumRewards = new SettlementRewardInfo[SettlementNoticeB.RewardSlotCount];
            for (int i = 0; i < premiumRewards.Length; i++)
            {
                if (!TryReadRewardInfo(reader, out premiumRewards[i]))
                {
                    return false;
                }
            }

            if (!reader.TryGetInt(out int percent)
                || !reader.TryGetByte(out byte returnUserFlag)
                || !reader.TryGetByte(out byte eventCharacFlag)
                || !reader.TryGetByte(out byte deadUserPenaltyFlag))
            {
                return false;
            }

            notice = new SettlementNoticeB(expValues, expKind, questItems, rewards, receiverValue, reservedBytes, premiumRewards, percent,
                returnUserFlag != 0, eventCharacFlag != 0, deadUserPenaltyFlag != 0, reader.Remaining);
            return true;
        }

        // 读一个奖励信息:状态 0 只有 1 个字节;状态 1 再读 i32 保留位与 i32 数值;状态 2 在此之后再读 i32 物品编号与 i32 物品附加信息;别的状态无法确定长度,失败。
        private static bool TryReadRewardInfo(PacketReader reader, out SettlementRewardInfo info)
        {
            info = default;
            if (!reader.TryGetByte(out byte status) || status > 2)
            {
                return false;
            }

            if (status == 0)
            {
                info = new SettlementRewardInfo(0, 0, 0, 0, 0);
                return true;
            }

            if (!reader.TryGetInt(out int reserved) || !reader.TryGetInt(out int value))
            {
                return false;
            }

            if (status == 1)
            {
                info = new SettlementRewardInfo(1, reserved, value, 0, 0);
                return true;
            }

            if (!reader.TryGetInt(out int itemId) || !reader.TryGetInt(out int itemData))
            {
                return false;
            }

            info = new SettlementRewardInfo(2, reserved, value, itemId, itemData);
            return true;
        }
    }
}
