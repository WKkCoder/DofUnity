using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 装扮币数量应答(类 1、包号 587)的解析结果。成功时给出装扮币数量;失败体只有错误码。不可变;构造入口只给 <see cref="AvatarCoinParser"/>。
    /// </summary>
    public sealed class AvatarCoinNotice
    {
        internal AvatarCoinNotice(byte flag, byte errorCode, uint coin, int trailingByteCount)
        {
            Flag = flag;
            ErrorCode = errorCode;
            Coin = coin;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>成功标志(载荷第一个字节原值;0 为失败,与 <see cref="SelectCharacterResult"/> 同口径)。</summary>
        public byte Flag { get; }

        /// <summary>读到了装扮币数量(标志非 0)。</summary>
        public bool Success => Flag != 0;

        /// <summary>失败码(只在标志为 0 时有值,否则为 0)。</summary>
        public byte ErrorCode { get; }

        /// <summary>装扮币数量(<c>AvatarCoin::Get</c>,u32 原值;失败时为 0)。</summary>
        public uint Coin { get; }

        /// <summary>读完之后剩余的字节数(包尾校验与补零,只计数)。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>
    /// 装扮币数量应答(类 1、包号 587)解析器。纯函数,任何畸形输入都只返回假,不抛异常。
    /// <para>
    /// 布局(小端;客户端处理器 0x00713b10,服务端 <c>AvatarCoin::SendSyncPacket</c> 0x0817ffe4 依次写包头(类 1、包号 587)、1 个字节的成功标志(恒为 1)、1 个 u32 的装扮币数量,
    /// 笔记 §2.6):<c>u8 成功标志;标志为 0 时再 u8 错误码,失败体到此为止;否则 u32 装扮币数量</c>,成功体正文 5 字节。
    /// </para>
    /// <para>
    /// <b>成功标志字节是否已被吃掉?——没有。</b>客户端的类 1 应答分发层先读走这个字节再调处理器(处理器直接收到这个标志,见 0x00713b23),
    /// 但本仓库的 <see cref="DecodedFrame.Payload"/> 是解密(与解压)之后、从包体第一个字段起的字节,不做分发层的剥离:
    /// <see cref="SelectCharacterResultParser"/>(类 1 包号 4,分发层读标志 0x00707b94)就是自己从载荷第一个字节读标志、为 0 再读错误码。
    /// 本解析器与它同口径:载荷第一个字节就是标志。测试里用解码器走真实解码路径钉死了这一点(载荷 = 标志 + u32,而不是只有 u32)。
    /// </para>
    /// <para>尾部容忍:读完即停,之后的字节只计数。</para>
    /// </summary>
    public static class AvatarCoinParser
    {
        /// <summary>装扮币数量应答的包号(类 1)。</summary>
        public const int PacketId = 587;

        /// <summary>解析。</summary>
        public static bool TryParse(byte[] payload, out AvatarCoinNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }
            PacketReader reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte flag))
            {
                return false;
            }
            if (flag == 0)
            {
                if (!reader.TryGetByte(out byte errorCode))
                {
                    return false;
                }
                notice = new AvatarCoinNotice(flag, errorCode, 0, reader.Remaining);
                return true;
            }
            if (!reader.TryGetUInt(out uint coin))
            {
                return false;
            }
            notice = new AvatarCoinNotice(flag, 0, coin, reader.Remaining);
            return true;
        }
    }
}
