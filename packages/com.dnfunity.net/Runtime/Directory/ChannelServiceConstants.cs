namespace Dnf.Net.ChannelDirectory
{
    /// <summary>
    /// 选频道目录服务(Channel Service,默认端口 7001)协议常量与客户端分级阈值常量。
    /// <para>
    /// 【观】<c>DNF.exe</c> <c>0x010f5c50..0x010f647c</c> 与 <c>df_game_r.orig</c> <c>CmmChannelProxy</c>(<c>0x0810619e..0x08106c94</c>):
    /// 11 字节小端帧头 + AES-128-ECB 零填充(初始密钥 <c>"Neople"</c>) + zlib(上限 0x32000 字节)。
    /// </para>
    /// </summary>
    public static class ChannelServiceConstants
    {
        /// <summary>默认频道目录服务端口(<c>DNF.exe</c> <c>0x01a3fc38</c>=7001)。</summary>
        public const int DefaultPort = 7001;

        /// <summary>固定 11 字节帧头长度。</summary>
        public const int HeaderLength = 11;

        /// <summary>帧头第 0 字节版本号(0x00)。</summary>
        public const byte ProtocolByte0 = 0x00;

        /// <summary>标准发送标志位(0x01)。</summary>
        public const byte DefaultFlag = 0x01;

        /// <summary>命令号:请求频道列表(C→S,空载荷)。</summary>
        public const byte CmdChannelListRequest = 0x01;

        /// <summary>命令号:频道列表响应(S→C,zlib + AES-128-ECB)。</summary>
        public const byte CmdChannelListReply = 0x03;

        /// <summary>命令号:检查 ChannelScript 版本(C→S,16 字节零填充版本串 AES 密文)。</summary>
        public const byte CmdVersionCheckRequest = 0x05;

        /// <summary>命令号:版本检查结果(S→C,1..256 字节 AES 密文:u32 upToDate + 可选 newVersion16)。</summary>
        public const byte CmdVersionCheckReply = 0x06;

        /// <summary>命令号:拉取最新 ChannelScript.pvf(C→S,空载荷)。</summary>
        public const byte CmdScriptRequest = 0x09;

        /// <summary>命令号:请求会话 AES 密钥(C→S,32 字节 <c>"dnf_game"</c> 零填充密文)。</summary>
        public const byte CmdKeyExchangeRequest = 0x0b;

        /// <summary>命令号:会话 AES 密钥响应(S→C,36 字节明文:u32 status + byte[32] nextKey)。</summary>
        public const byte CmdKeyExchangeReply = 0x0c;

        /// <summary>AES-128-ECB 块长(字节)。</summary>
        public const int AesBlockSize = 16;

        /// <summary>AES-128 密钥长度(字节)。</summary>
        public const int AesKeySize = 16;

        /// <summary>密钥交换响应体固定长度(4 + 32 字节)。</summary>
        public const int KeyExchangeReplyBodyLength = 36;

        /// <summary>版本检查响应体最大长度(<c>DNF.exe</c> <c>0x010f5025</c>)。</summary>
        public const int MaxVersionReplyBodyLength = 256;

        /// <summary>压缩/解压缓冲区上限(0x32000 = 204800 字节,<c>DNF.exe</c> <c>0x010f5185</c>/<c>0x010f5358</c>)。</summary>
        public const int MaxPayloadBytes = 0x32000;

        /// <summary>频道组标签固定字节数。</summary>
        public const int GroupTagLength = 20;

        /// <summary>频道名称固定字节数。</summary>
        public const int ChannelNameLength = 20;

        /// <summary>频道 IP 字符串固定字节数。</summary>
        public const int ChannelHostLength = 16;

        /// <summary>单条频道记录线长(20 + 4 + 4 + 16 + 4 = 48 字节)。</summary>
        public const int ChannelRecordLength = 48;

        /// <summary>默认满载阈值(<c>[channel rate full]</c> 与客户端默认 <c>[0x15abf98]=95.0f</c>)。</summary>
        public const float DefaultFullThresholdPercent = 95.0f;

        /// <summary>有效容量缩放系数(<c>[0x1562254]=0.7f</c>,<c>DNF.exe</c> <c>0x010f5870</c>)。</summary>
        public const float EffectiveCapacityFactor = 0.7f;

        /// <summary>默认本地脚本版本号。</summary>
        public const string DefaultScriptVersion = "0";

        /// <summary>初始握手预共享 16 字节 AES-128 密钥(<c>"Neople"</c> + 10 个 0x00)。</summary>
        public static byte[] CreateInitialAesKey()
        {
            byte[] key = new byte[AesKeySize];
            key[0] = (byte)'N';
            key[1] = (byte)'e';
            key[2] = (byte)'o';
            key[3] = (byte)'p';
            key[4] = (byte)'l';
            key[5] = (byte)'e';
            return key;
        }

        /// <summary>构造密钥交换请求(0x0b)的 32 字节零填充问候明文(<c>"dnf_game"</c> + 24 个 0x00)。</summary>
        public static byte[] CreateGreetingPlaintext()
        {
            byte[] plain = new byte[32];
            byte[] tag = System.Text.Encoding.ASCII.GetBytes("dnf_game");
            System.Buffer.BlockCopy(tag, 0, plain, 0, tag.Length);
            return plain;
        }
    }
}
