namespace Dnf.Net.Crypto
{
    /// <summary>
    /// 游戏服会话算法槽里的一个块加解密(14 个槽,槽号是包号 % 14,E-204;密钥块 334 字节按槽顺序切开,E-262)。
    /// 每个槽一个实现:块长与密钥长由槽决定(块长是客户端虚函数的实测值),密钥在连接建立后一次设好。
    /// 这里只定接口;各槽的算法细节与向量见 E-263(槽 1 与标准 AES 一致,槽 2 与标准 Blowfish 不一致)。
    /// </summary>
    public abstract class BlockCipher
    {
        /// <summary>槽号,0 到 13。</summary>
        public abstract int Slot { get; }

        /// <summary>块长(字节)。</summary>
        public abstract int BlockSize { get; }

        /// <summary>密钥长度(字节);设密钥时从 key 的 offset 起取这么多字节。</summary>
        public abstract int KeyLength { get; }

        /// <summary>从 <paramref name="key"/> 的 <paramref name="offset"/> 起取 <see cref="KeyLength"/> 字节作为密钥。</summary>
        public abstract void SetKey(byte[] key, int offset);

        /// <summary>加密一个块(<see cref="BlockSize"/> 字节);输入与输出可以是同一个数组的同一位置。</summary>
        public abstract void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset);

        /// <summary>解密一个块(<see cref="BlockSize"/> 字节)。</summary>
        public abstract void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset);
    }
}
