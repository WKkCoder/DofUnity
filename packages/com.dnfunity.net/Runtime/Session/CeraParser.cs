using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 点券通知(类 0、包号 53)的解析结果。点券类货币不走背包伪条目,单独走这个包(笔记 §2.6)。
    /// 三个数的名字来自服务端取值函数(<c>GetCera</c>、<c>GetMileage</c>、<c>GetCeraPoint</c>),一律按 u32 原值给出,不做业务解释。
    /// 不可变;构造入口只给 <see cref="CeraParser"/>。
    /// </summary>
    public sealed class CeraNotice
    {
        internal CeraNotice(byte flag, uint cera, uint mileage, uint ceraPoint, int trailingByteCount)
        {
            Flag = flag;
            Cera = cera;
            Mileage = mileage;
            CeraPoint = ceraPoint;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>
        /// 开头的 u8 原值。服务端恒写 1(<c>SendCashData</c> 0x08650859 一带);客户端先读它,三个 u32 无论取值都照读(0x007348c5..0x007348fe),
        /// 它只影响读完之后的界面刷新。解析器同样不据它拒包,原值照给。
        /// </summary>
        public byte Flag { get; }

        /// <summary>点券(<c>GetCera</c>)。选角成功体里同一个数以 i32 下发一次(<see cref="SelectCharacterResult"/>),非负时两者相等。</summary>
        public uint Cera { get; }

        /// <summary>里程(<c>GetMileage</c>)。</summary>
        public uint Mileage { get; }

        /// <summary>点券点(<c>GetCeraPoint</c>)。</summary>
        public uint CeraPoint { get; }

        /// <summary>读完三个 u32 之后剩余的字节数(包尾校验与补零,只计数)。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>
    /// 点券通知(类 0、包号 53)解析器。纯函数,任何畸形输入都只返回假,不抛异常。
    /// 布局(小端;客户端处理器 0x007348b0,服务端 <c>CUser::SendCashData(bool)</c> 0x0865082a,笔记 §2.6):
    /// <c>u8 标志 + u32 点券 + u32 里程 + u32 点券点</c>,正文 13 字节。类 0 通知的载荷从第一个字段起,标志字节就是载荷的第一个字节。
    /// 尾部容忍:读完即停,之后的字节(包尾校验与补零)只计数。
    /// </summary>
    public static class CeraParser
    {
        /// <summary>点券通知的包号(类 0)。</summary>
        public const int PacketId = 53;

        /// <summary>解析。</summary>
        public static bool TryParse(byte[] payload, out CeraNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }
            PacketReader reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte flag)
                || !reader.TryGetUInt(out uint cera)
                || !reader.TryGetUInt(out uint mileage)
                || !reader.TryGetUInt(out uint ceraPoint))
            {
                return false;
            }
            notice = new CeraNotice(flag, cera, mileage, ceraPoint, reader.Remaining);
            return true;
        }
    }
}
