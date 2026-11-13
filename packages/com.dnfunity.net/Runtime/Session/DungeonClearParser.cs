using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 清图通知(类 0、包号 <see cref="PacketId"/>)的明文载荷解析器:把静态读码定案的"空载荷"落成只读记录 <see cref="DungeonClearNotice"/>。
    /// 包名表里它叫 ENABLE_CLEAR_DUNGEON。地图里最后一只怪被清掉、服务端判定可以结算时,逐个队员发一份。
    /// <para>
    /// 范围:纯明文解析。解析器不看包类与包号(调用方先认,包号常量见 <see cref="PacketId"/>),不接会话,不碰信封。
    /// </para>
    /// <para>
    /// 约定(与 <see cref="DungeonStartParser"/> 同风格):绝不抛异常(包括 null 载荷),null 载荷返回 false 且 notice 为 null;
    /// 没有任何字段可读,所以任何非 null 载荷都成功,全部字节都算尾巴(只在 <see cref="DungeonClearNotice.TrailingByteCount"/> 报告个数,不核对)。
    /// </para>
    /// <para>
    /// 写序(服务端 <c>CParty::ClearDungeon</c> 0x085a9330,发送点 <c>put_header(0, 0x1f)</c> 在 0x085a9fa5,随即 <c>finalize</c> 0x085a9fb8,
    /// 再对每个在队队员 <c>CUser::Send</c> 0x085aa101;另一个发送点是 <c>CParty::OnTimeoutBossDie</c> 0x085aa5d4 里的 0x085aaaa8,同样只有包头,
    /// 再 <c>CParty::send_to_party</c> 0x085aaacd。按立即数参数检索全 .text 里 <c>put_header(0, 0x1f)</c> 的调用点只有这两处【观】,
    /// 此法看不到以非立即数方式给出包号的调用,所以"只有这两处"对立即数写法成立,对别的写法是【推】)。
    /// 两处都没有任何 <c>put_*</c>,载荷长度为 0【观】。客户端处理器 0x0073b010..0x0073b4f4(登记点 0x00757170 调登记函数 0x0074e790:包号 0x1f → 该处理器)
    /// 不调用任何读原语(0x011b9470 / 0x011b94a0 / 0x011b94d0 一个都没有),所以多读少读都不存在【观】。
    /// 这两份证据所在的笔记(settlenoti_NOTES §2.1、§3.1)被 .gitignore 忽略,不入库。
    /// </para>
    /// <para>
    /// 【客户端行为,解析器都不模拟】客户端收到它就<b>自己发结算命令</b>:拼装类 1、包号 0x31(= 49)的包并 <c>Send</c>
    /// (<c>MakePacket(0x31)</c> 0x0073b0a1,<c>PutByte</c>/<c>PutShort</c>/<c>PutInt</c> 若干,<c>Send</c> 0x0073b350),
    /// 随后把状态位 <c>[0x1a6a874]+0x468</c> 置 1(0x0073b35a)并做界面与消息的收尾调用;处理器开头若 <c>0x00493df0 → 0x00492e10</c> 判定为真则
    /// 先走一段别的收尾而<b>不发</b>(0x0073b071..0x0073b094 后跳到 0x0073b4d9;这条分支的判定条件没读通【未】)。命令各字段的布局见 <see cref="OnlineCombatPayloads"/> 里
    /// 结算命令的说明;状态机侧"收到清图就自动回结算命令"由 <see cref="OnlineDungeonCombat"/> 负责,这里不做。
    /// </para>
    /// </summary>
    public static class DungeonClearParser
    {
        /// <summary>清图通知的包号(类 0,即 <see cref="PacketClass.Noti"/>)。</summary>
        public const int PacketId = 0x1f;

        /// <summary>
        /// 解析一份载荷(没有字段,所有字节都算尾巴)。
        /// </summary>
        /// <param name="payload">通知载荷;可以为 null。</param>
        /// <param name="notice">成功时是只读记录;失败时为 null。</param>
        /// <returns>载荷非 null 为 true;载荷为 null 为 false。不抛异常。</returns>
        public static bool TryParse(byte[] payload, out DungeonClearNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }

            notice = new DungeonClearNotice(payload.Length);
            return true;
        }
    }
}
