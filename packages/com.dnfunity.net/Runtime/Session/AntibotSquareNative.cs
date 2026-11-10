using System;

namespace Dnf.Net.Session
{
    /// <summary>
    /// Square 单块加解密：微程序解释器，行为对齐 VA 0x202e10 / 0x203c10。
    /// </summary>
    internal static class AntibotSquareNative
    {
        internal static void Encrypt(ref uint w0, ref uint w1, ref uint w2, ref uint w3, uint[] workKeys)
        {
            uint[] st = { w0, w1, w2, w3 };
            AntibotSquareVm.RunEncrypt(st, workKeys);
            w0 = st[0];
            w1 = st[1];
            w2 = st[2];
            w3 = st[3];
        }

        internal static void Decrypt(ref uint w0, ref uint w1, ref uint w2, ref uint w3, uint[] compactKeys)
        {
            uint[] st = { w0, w1, w2, w3 };
            AntibotSquareVm.RunDecrypt(st, compactKeys);
            w0 = st[0];
            w1 = st[1];
            w2 = st[2];
            w3 = st[3];
        }
    }
}
