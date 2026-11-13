namespace Dnf.Net.Session
{
    /// <summary>
    /// 清图通知(类 0、包号 0x1f,包名表里叫 ENABLE_CLEAR_DUNGEON)明文载荷解出的只读记录,由 <see cref="DungeonClearParser"/> 生成。
    /// 这个通知<b>没有任何载荷字段</b>:服务端两个发送点都只写包头就 finalize(<see cref="DungeonClearParser"/> 的说明有地址),
    /// 客户端处理器也不读载荷【观】;所以记录里只有"尾巴还剩几个字节"。它的意义全在"到达这件事"本身:
    /// 客户端一收到就自己拼装并发出结算命令(类 1、包号 49),见 <see cref="DungeonClearParser"/>。
    /// </summary>
    public sealed class DungeonClearNotice
    {
        internal DungeonClearNotice(int trailingByteCount)
        {
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>
        /// 载荷里剩下的字节数(因为没有任何字段,等于载荷总长)。解析器不消费、不核对这段尾巴。
        /// 折叠哈希只在载荷长度 1 到 0x190 时追加 4 字节,长度 0 的包不追加;会话加密补零仍可能存在,所以真实帧里这个数可能不是 0,
        /// 它只供对账,不参与解析成败。
        /// </summary>
        public int TrailingByteCount { get; }
    }
}
