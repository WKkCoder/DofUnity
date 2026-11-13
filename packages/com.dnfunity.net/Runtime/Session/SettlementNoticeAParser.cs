using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 通关结果通知(类 0、包号 <see cref="PacketId"/>,包名表里叫 PLAY_RESULT)的明文载荷解析器:把静态读码定案的读序落成只读记录 <see cref="SettlementNoticeA"/>。
    /// 它是结算两条下行通知里先到的那条(另一条是 <see cref="SettlementNoticeBParser"/> 解的通关奖励),整队共用同一份载荷(<c>send_to_party</c>)。
    /// <para>
    /// 范围:纯明文解析。解析器不看包类与包号(调用方先认,包号常量见 <see cref="PacketId"/>),不接会话,不碰信封,也不接 <see cref="OnlineDungeonCombat"/> 状态机。
    /// </para>
    /// <para>
    /// 约定(与 <see cref="DungeonPermissionParser"/>、<see cref="DungeonStartParser"/> 同风格):短包、读越界、条数声明超出载荷一律返回 false 且 notice 为 null,
    /// 绝不抛异常(包括 null 载荷);读完最后一个条目即停,容忍尾部多余字节(只在 <see cref="SettlementNoticeA.TrailingByteCount"/> 报告个数,不核对,
    /// 不要求"读完恰好等于载荷末尾");字段取值不做范围检查;不做任何字符串转换。
    /// </para>
    /// <para>
    /// 写序(服务端 <c>DisPatcher_SetPlayResult::SendResult</c> 0x081f5db6,settlenoti_NOTES §2.3 的【观】,该笔记被 .gitignore 忽略,不入库):
    /// <c>put_header(0, 0x22)</c> 0x081f5ddf;<c>put_byte</c> 评分值(0x081f5df8);<c>put_int</c> 通关用时(0x081f5e0d);<c>put_byte</c> 用时分(0x081f5e26);
    /// <c>put_byte</c> 基础分(0x081f5e3f);随后调 <c>CParty::makeBestClearTimePacket</c> 0x085be1f8 续写:<c>put_byte</c> 任务迷宫标志(0x085be22a)、
    /// <c>put_byte</c> 条数 = <c>get_member_count</c>(0x085be244)、槽位 0 到 3 里每个有效队员各一条 <c>put_short</c> 唯一号(0x085be345)、
    /// <c>put_int</c> 最佳用时(0x085be357)、<c>put_byte</c> 新纪录标志(0x085be378);回到 <c>SendResult</c> <c>finalize</c>(0x081f5e6e),
    /// <c>CParty::send_to_party</c>(0x081f5e80)。线上读序即 <c>u8, i32, i8, i8, u8, u8 条数, 条数 × { u16, i32, u8 }</c>,总长 = 9 + 7 × 条数,
    /// 没有别的变长段。
    /// </para>
    /// <para>
    /// 读序对账(客户端处理器 0x0073b500..0x0073b82f,登记点 0x0075719a 调登记函数 0x0074e790:包号 0x22 → 该处理器;settlenoti_NOTES §3.2 的【观】):
    /// 读原语 0x011b9470(u8,传 (指针, 1))、0x011b94a0(u16)、0x011b94d0(u32);读点依次是 u8(0x0073b688,读完<b>不再使用</b>)、u32 通关用时(0x0073b691)、
    /// u8 用时分(0x0073b69c)、u8 基础分(0x0073b6a7)、u8 任务迷宫标志(0x0073b6b2)、u8 条数(0x0073b6bd),随后循环
    /// "条数 &gt; 已读个数 且 已读个数 &lt; 4"(0x0073b6c7、0x0073b6d0),每轮 u16(0x0073b6d9)、u32(0x0073b6e2)、u8(0x0073b6ed)。
    /// 宽度与次序同服务端逐项吻合;差别只有三处:(1)客户端不用第 1 个字节;(2)客户端<b>最多只读 4 条</b>并按无符号看条数(<c>MOVZX</c> 0x0073b743),
    /// 服务端最多写 4 条(槽位 0 到 3,0x085be38d),正常流程里两边一致;(3)客户端读完不核对尾巴。
    /// 处理器之后<b>不回包</b>:只落状态(+0x468 置 1,0x0073b797)并在末尾调用受保护的函数(0x0073b824 调 0x0229128e,在受保护节里,虚拟化不可读【未】)。
    /// </para>
    /// <para>
    /// 条数字节写的是 <c>get_member_count</c>,条目却按 <c>checkValidUser</c> 的有效队员写(0x085be26d),两者在"有队员已离开但计数未减"这类时刻是否相等
    /// 没有读通【未】;不相等时多出来的"条目"会吃掉载荷尾部(折叠哈希或补零),表现为条目取值异常而不是失败,解析器无法区分。
    /// 本解析器<b>不设 4 条上限</b>:按声明条数读(客户端只读前 4 条的行为不模拟)。
    /// </para>
    /// <para>
    /// 7 字节全零形(<see cref="TryParseTimeoutFallback"/>;"超时兜底"是这个形状早期的叫法,标识符沿用):服务端 <c>CParty::forced_set_play_result</c> 0x085b26e8 只发
    /// <c>put_header(0, 0x22)</c>(0x085b2720)加四个写点(<c>put_byte</c> 0、<c>put_int</c> 0、<c>put_byte</c> 0、<c>put_byte</c> 0,
    /// 0x085b2733、0x085b2746、0x085b2759、0x085b276c),<c>finalize</c> 0x085b277f,<c>send_to_party</c> 0x085b2791:载荷是 7 个全零字节【观】;
    /// 该函数在 <c>party+0x6a</c> 已为 3(<c>set_play_result</c> 已跑过)时什么也不发(0x085b26f9)【观】。
    /// 它只有一个调用者:定时器分发器 <c>TimerSetResult::dispatch_sig</c>(0x08633c58,按计时键类型 0xc 校验后调用,0x08633cb0)【观】。
    /// 而结算命令处理里登记的等待定时器是另一种(类型 0x29,0x081f5451),它的分发器 <c>TimerSetPlayResult::dispatch_sig</c> 0x08636398 到点直接调
    /// <c>CParty::SetPlayResult</c>(0x086363ff),走的是完整的正常形(加通关奖励通知),不是这个 7 字节形【观】。
    /// 类型 0xc 的计时键生成点与定时器登记点用立即数检索一个也没找到(以变量传类型的调用者只出现在血战与锦标赛相关函数里)【观,检索范围有限】,
    /// 所以这个 7 字节形在真实服务端是否会出现没有读通,按"没有登记点"看多半是死代码【推】。
    /// 它没有任务迷宫标志与条数,而客户端处理器至少读 9 字节;若真出现,客户端会把载荷之后的字节(折叠哈希或补零)当作迷宫标志与条数读,
    /// 结果取决于这些字节,实机行为没有样本可对【未】。这条路径也<b>不发</b>通关奖励通知(0x23)。
    /// </para>
    /// </summary>
    public static class SettlementNoticeAParser
    {
        /// <summary>通关结果通知的包号(类 0,即 <see cref="PacketClass.Noti"/>)。</summary>
        public const int PacketId = 0x22;

        // 每条 7 字节:u16 唯一号 + i32 最佳用时 + u8 新纪录标志。
        private const int EntryLength = 7;

        // 7 字节全零形的固定长度:u8 + i32 + u8 + u8。
        private const int FallbackLength = 7;

        /// <summary>
        /// 解析一份正常结算形状的载荷(从第一个字段起;9 字节头 + 条目表)。
        /// </summary>
        /// <param name="payload">通知载荷;可以为 null。</param>
        /// <param name="notice">成功时是只读记录(<see cref="SettlementNoticeA.HasBestTimeBlock"/> 为 true);失败时为 null。</param>
        /// <returns>读完头与全部条目为 true;载荷为 null、过短或条数声明超出载荷为 false。不抛异常。</returns>
        public static bool TryParse(byte[] payload, out SettlementNoticeA notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte rankValue)
                || !reader.TryGetInt(out int clearTime)
                || !reader.TryGetByte(out byte timeBonus)
                || !reader.TryGetByte(out byte baseScore)
                || !reader.TryGetByte(out byte questMazeFlag)
                || !reader.TryGetByte(out byte count))
            {
                return false;
            }

            // 条数声明超出载荷时直接失败,不先按声明分配(u8 条数最多 255 条,几个字节的坏包不该换来一块内存)。
            if ((long)count * EntryLength > reader.Remaining)
            {
                return false;
            }

            var entries = new SettlementBestTimeEntry[count];
            for (int i = 0; i < entries.Length; i++)
            {
                if (!reader.TryGetUShort(out ushort uniqueId) || !reader.TryGetInt(out int bestTime) || !reader.TryGetByte(out byte newRecord))
                {
                    return false;
                }
                entries[i] = new SettlementBestTimeEntry(uniqueId, bestTime, newRecord == 1);
            }

            notice = new SettlementNoticeA(rankValue, clearTime, unchecked((sbyte)timeBonus), unchecked((sbyte)baseScore), questMazeFlag,
                true, entries, reader.Remaining);
            return true;
        }

        /// <summary>
        /// 解析一份 7 字节全零形状的载荷:前 7 个字节(u8、i32、u8、u8)必须<b>全为 0</b>(服务端强制结算发送点写的就是四个 0,见类说明),其后的字节算尾巴。
        /// 与 <see cref="TryParse"/> 是两个独立入口,由调用方按场景选(解析器自己不猜):前 7 个字节不是全零的载荷即使有 7 字节以上也返回 false,
        /// 以免把一份被截坏的正常载荷误认成这个形状。
        /// </summary>
        /// <param name="payload">通知载荷;可以为 null。</param>
        /// <param name="notice">成功时是只读记录(<see cref="SettlementNoticeA.HasBestTimeBlock"/> 为 false,各字段全为 0、条目表为空);失败时为 null。</param>
        /// <returns>载荷至少 7 字节且前 7 字节全零为 true;载荷为 null、不足 7 字节或前 7 字节里有非零字节为 false。不抛异常。</returns>
        public static bool TryParseTimeoutFallback(byte[] payload, out SettlementNoticeA notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            var reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte rankValue)
                || !reader.TryGetInt(out int clearTime)
                || !reader.TryGetByte(out byte timeBonus)
                || !reader.TryGetByte(out byte baseScore))
            {
                return false;
            }

            if (rankValue != 0 || clearTime != 0 || timeBonus != 0 || baseScore != 0)
            {
                return false;
            }

            notice = new SettlementNoticeA(0, 0, 0, 0, 0, false, new SettlementBestTimeEntry[0], payload.Length - FallbackLength);
            return true;
        }
    }
}
