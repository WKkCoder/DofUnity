namespace Dnf.Net.Frame
{
    /// <summary>
    /// 包类(帧头第 0 字节):0 = 通知(NOTI),1 = 命令(CMD);E-202。
    /// 服务端接收时不按类拒收(类与包号一起进入限速与去重比较),所以这里是枚举而不是闭集:
    /// 收到别的取值要原样保留,别当错误。
    /// </summary>
    public enum PacketClass : byte
    {
        /// <summary>通知(NOTI)。包号空间与命令各自独立。</summary>
        Noti = 0,

        /// <summary>命令(CMD)。</summary>
        Cmd = 1,
    }
}
