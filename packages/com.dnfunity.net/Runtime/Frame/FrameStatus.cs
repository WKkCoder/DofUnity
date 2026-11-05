namespace Dnf.Net.Frame
{
    /// <summary>整帧编码与解码的结果。除 <see cref="Ok"/> 外都表示该帧不可用,不抛异常。</summary>
    public enum FrameStatus
    {
        /// <summary>成功。</summary>
        Ok = 0,

        /// <summary>解码:字节数不足一个帧头。</summary>
        TruncatedHeader,

        /// <summary>解码:头里的整帧长度与给定字节数不等。</summary>
        LengthMismatch,

        /// <summary>解码:校验值与密文不符。</summary>
        ChecksumMismatch,

        /// <summary>解码:需要会话密码但还没有设置(收到启动包之前)。</summary>
        NoCipher,

        /// <summary>解码:会话解密失败(密文长度不是块长的整数倍,或包号选不出算法槽)。</summary>
        CipherFailed,

        /// <summary>解码:压缩包的载荷不是合法的 zlib 数据。</summary>
        InflateFailed,

        /// <summary>解码:解压后超过单包缓冲上限。</summary>
        PayloadTooLarge,

        /// <summary>编码:会话加密失败(包号选不出算法槽)。</summary>
        EncryptFailed,

        /// <summary>编码:整帧长度超出布局允许的上限。</summary>
        FrameTooLarge,
    }
}
