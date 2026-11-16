using System;
using System.Collections.Generic;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 快捷键选项(类 0、包号 452)的解析结果:一个类型字节加 94 个 u16 的绑定表。
    /// 这是<b>快捷键绑定</b>,不是物品快捷栏(物品快捷栏是背包槽 3..8,随物品空间包下发,笔记 §2.6)。不可变;构造入口只给 <see cref="HotkeyOptionParser"/>。
    /// </summary>
    public sealed class HotkeyOptionNotice
    {
        private readonly IReadOnlyList<ushort> _bindings;

        internal HotkeyOptionNotice(byte type, ushort[] bindings, int trailingByteCount)
        {
            Type = type;
            _bindings = Array.AsReadOnly(bindings ?? new ushort[0]);
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>类型字节(u8 原值;客户端读后交给 0x004dce10,语义未读)。</summary>
        public byte Type { get; }

        /// <summary>94 个绑定值(u16 原值;0xFFFF 表示空;客户端对大于 0x86 的值走另一分支 0x004de58e,含义未读)。</summary>
        public IReadOnlyList<ushort> Bindings => _bindings;

        /// <summary>读完之后剩余的字节数(包尾校验与补零,只计数)。</summary>
        public int TrailingByteCount { get; }
    }

    /// <summary>
    /// 快捷键选项(类 0、包号 452)解析器。纯函数,任何畸形输入都只返回假,不抛异常。
    /// 布局(小端;客户端处理器 0x0072f520,服务端 <c>Inter_LoadCharacTypeHotKey::dispatch_sig</c> 0x084e87de / <c>LoadCharacTypeHotKey</c> 0x08692d2a,笔记 §2.6):
    /// <c>u8 类型 + u32 长度 + 94 × u16</c>。长度前缀客户端要求恰为 0xBC(188 = 94 × 2),不是就拒绝整包(<b>不</b>按声明长度去读)。
    /// 尾部容忍:读完即停,之后的字节只计数。
    /// </summary>
    public static class HotkeyOptionParser
    {
        /// <summary>快捷键选项的包号(类 0)。</summary>
        public const int PacketId = 452;

        /// <summary>长度前缀的唯一合法值。</summary>
        public const uint BlockLength = 0xBC;

        /// <summary>绑定个数:94。</summary>
        public const int BindingCount = 94;

        /// <summary>解析。</summary>
        public static bool TryParse(byte[] payload, out HotkeyOptionNotice notice)
        {
            notice = null;
            if (payload == null)
            {
                return false;
            }
            PacketReader reader = new PacketReader(payload);
            if (!reader.TryGetByte(out byte type)
                || !reader.TryGetUInt(out uint length)
                || length != BlockLength
                || BindingCount * 2 > reader.Remaining)
            {
                return false;
            }

            ushort[] bindings = new ushort[BindingCount];
            for (int i = 0; i < BindingCount; i++)
            {
                if (!reader.TryGetUShort(out bindings[i]))
                {
                    return false;
                }
            }
            notice = new HotkeyOptionNotice(type, bindings, reader.Remaining);
            return true;
        }
    }
}
