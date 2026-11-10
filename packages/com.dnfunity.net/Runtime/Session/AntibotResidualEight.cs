using System;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 原二进制在 <c>Ten*</c> 尾路径通过 <c>CTsLocalImp</c> 虚表 +0xc 调 <c>Seattos</c> 处理 <c>长度%16</c> 余量;
    /// 其中余 8 字节时与单独跑 8 字节 Seattos 一致(与分组核心、keylen 实参无关)。
    /// </summary>
    internal static class AntibotResidualEight
    {
        internal static void Apply(byte[] key, int keyLength, bool encrypt, byte[] buf, int offset)
        {
            byte[] tail = new byte[8];
            Buffer.BlockCopy(buf, offset, tail, 0, 8);
            AntibotSeattos.Transform(key, encrypt, tail);
            Buffer.BlockCopy(tail, 0, buf, offset, 8);
        }
    }
}
