namespace Dnf.Net.Frame
{
    /// <summary>
    /// 会话密码的接缝:帧层只认"某个包号的载荷怎么加密、怎么解密",不关心里面是哪个算法。
    /// 约定:失败(包号选不出算法槽、密文长度不合法)返回 null,不抛异常;空载荷返回空数组(服务端 <c>Cipher::Encrypt</c> 0x08097436 对 n&lt;1 写长度 0 并成功)。
    /// </summary>
    public interface IPayloadCipher
    {
        /// <summary>
        /// 加密 <paramref name="payload"/>。实现可以把输出补到块长的整数倍(服务端的补齐字节由调用者缓冲区里紧跟载荷的内容决定,
        /// 发给客户端的帧补 0x00);调用者要自己知道补了多少,因为帧里没有明文长度字段。
        /// </summary>
        byte[] Encrypt(int packetId, byte[] payload);

        /// <summary>解密 <paramref name="cipherText"/>;输出与输入等长(不去掉补齐字节)。长度不合法返回 null。</summary>
        byte[] Decrypt(int packetId, byte[] cipherText);

        /// <summary>
        /// 长度为 <paramref name="length"/> 的载荷加密后的长度(即补齐后的长度;空载荷为 0);包号选不出算法时返回 -1。
        /// 调用者据此自己补齐字节再加密,补齐字节也在校验范围内。
        /// </summary>
        int PaddedLength(int packetId, int length);
    }

    /// <summary>不加密(测试用与"无会话密钥"的占位):原样复制。</summary>
    public sealed class NullPayloadCipher : IPayloadCipher
    {
        /// <inheritdoc />
        public byte[] Encrypt(int packetId, byte[] payload)
        {
            return payload == null ? null : (byte[])payload.Clone();
        }

        /// <inheritdoc />
        public byte[] Decrypt(int packetId, byte[] cipherText)
        {
            return cipherText == null ? null : (byte[])cipherText.Clone();
        }

        /// <inheritdoc />
        public int PaddedLength(int packetId, int length)
        {
            return length;
        }
    }
}
