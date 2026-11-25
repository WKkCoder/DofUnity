using System.Globalization;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 客户端程序文件版本资源里的四段 16 位数(文件版本,记 A、B、C、D;选服务器页右下角的版本标签显示的就是这四段原始数)。
    /// 登录请求里的"客户端版本号"不是它们原样,而是 <see cref="ClientVersionFolding.Fold"/> 折出的一个 u32(台账 E-451)。
    /// </summary>
    public readonly struct ClientFileVersion
    {
        public ClientFileVersion(ushort a, ushort b, ushort c, ushort d)
        {
            A = a;
            B = b;
            C = c;
            D = d;
        }

        public ushort A { get; }

        public ushort B { get; }

        public ushort C { get; }

        public ushort D { get; }

        /// <summary>折成登录请求里的客户端版本号(算式见 <see cref="ClientVersionFolding.Fold"/>)。</summary>
        public uint Fold()
        {
            return ClientVersionFolding.Fold(A, B, C, D);
        }

        /// <summary>四段数折算时是否没有超出 32 位(超出时 <see cref="Fold"/> 与客户端一样按 32 位回绕)。</summary>
        public bool FoldsExactly()
        {
            return ClientVersionFolding.IsExact(A, B, C, D);
        }

        /// <summary>点号分隔的四段十进制数,如 <c>1.2.3.4</c>(不带字母后缀)。</summary>
        public override string ToString()
        {
            return string.Concat(
                A.ToString(CultureInfo.InvariantCulture), ".",
                B.ToString(CultureInfo.InvariantCulture), ".",
                C.ToString(CultureInfo.InvariantCulture), ".",
                D.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// 原版客户端把"文件版本四段数"折成登录请求里那个 u32 的固定算式(客户端静态读码,台账 E-451;汇编逐条核对,全部是 32 位整数运算):
    /// <c>((((A×100 + B)×10 + ⌊C÷100⌋)×100 + (C 除以 100 的余数))×100 + D)</c>。
    /// 运算与客户端一样是 32 位的:四段数大到折出的数超过 4294967295 时按 32 位回绕(<see cref="IsExact"/> 报 false),
    /// 不抛异常、不悄悄截成别的数。这里只有算式,没有任何一个真实版本取值。
    /// </summary>
    public static class ClientVersionFolding
    {
        /// <summary>按客户端的算式折算(32 位回绕)。</summary>
        public static uint Fold(ushort a, ushort b, ushort c, ushort d)
        {
            unchecked
            {
                uint x = (uint)a * 100u + b;
                x = x * 10u + (uint)(c / 100);
                x = x * 100u + (uint)(c % 100);
                x = x * 100u + d;
                return x;
            }
        }

        /// <summary>同一算式在 64 位下算,折出的数是否仍不超过 4294967295(没回绕)。</summary>
        public static bool IsExact(ushort a, ushort b, ushort c, ushort d)
        {
            ulong x = (ulong)a * 100UL + b;
            x = x * 10UL + (ulong)(c / 100);
            x = x * 100UL + (ulong)(c % 100);
            x = x * 100UL + d;
            return x <= uint.MaxValue;
        }
    }
}
