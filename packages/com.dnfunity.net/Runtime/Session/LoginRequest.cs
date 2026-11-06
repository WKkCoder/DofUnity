using System;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>登录请求没通过本地检查的原因(都是服务端分发函数 0x081e8c78 会以"断开、不回包"处理的情形,E-299)。</summary>
    public enum LoginRequestProblem
    {
        /// <summary>没有问题。</summary>
        None = 0,

        /// <summary>补丁时间超过 0xE10。</summary>
        PatchTimeTooLarge,

        /// <summary>令牌为空或超过 0x800 字节,或含 0 字节(服务端按字符串读取,遇 0 提前结束,后面的字段会错位)。</summary>
        BadToken,

        /// <summary>用户名超过 0x18 字节,或含 0 字节。</summary>
        BadUserName,

        /// <summary>口令块超过 0x83 字节,或含 0 字节。</summary>
        BadPasswordBlock,

        /// <summary>登录模式不在 0 到 2。</summary>
        BadLoginMode,

        /// <summary>鼠标口令密文不是 8 字节。</summary>
        BadMouseCipher,

        /// <summary>服务器组不在 0 到 0x11。</summary>
        BadServerGroup,
    }

    /// <summary>
    /// 登录请求(CMD 1/1)的请求体。字段顺序与长度上限读自服务端分发函数 0x081e8c78(E-298),全部小端:
    /// u32 补丁时间(不大于 0xE10)、u32 令牌长度加令牌(不大于 0x800 字节)、u32 用户名长度加用户名(不大于 0x18)、
    /// u32 口令块长度加口令块(不大于 0x83)、u8 登录模式(0 到 2)、u32 8 加 8 字节鼠标口令密文、u32 客户端数据版本、
    /// u16 服务器组(0 到 0x11)、u32 本机地址、可选 u8 登出状态。
    /// 字符串字段服务端按 <c>get_str</c> 读(遇 0 字节提前结束,见 <see cref="PacketReader.TryGetString"/>),所以令牌必须是文本
    /// (网关给的 344 个 base64 字符),不是 256 字节的原始签名——后者几乎必然含 0 字节,会让后面的字段错位。
    /// 用户名与口令块服务端只做长度检查并写日志,身份只由令牌决定(E-298),这里只要求长度合规。
    /// </summary>
    public sealed class LoginRequest
    {
        /// <summary>登录包的包号(类 CMD,包号 1)。</summary>
        public const ushort PacketId = 1;

        /// <summary>补丁时间上限。</summary>
        public const uint MaxPatchTime = 0xE10;

        /// <summary>令牌最大字节数。</summary>
        public const int MaxTokenBytes = 0x800;

        /// <summary>用户名最大字节数。</summary>
        public const int MaxUserNameBytes = 0x18;

        /// <summary>口令块最大字节数。</summary>
        public const int MaxPasswordBlockBytes = 0x83;

        /// <summary>鼠标口令密文的字节数(服务端要求长度字段恒为 8)。</summary>
        public const int MouseCipherBytes = 8;

        /// <summary>登录模式最大值。</summary>
        public const int MaxLoginMode = 2;

        /// <summary>服务器组最大值。</summary>
        public const int MaxServerGroup = 0x11;

        /// <summary>补丁时间(秒,不大于 <see cref="MaxPatchTime"/>)。</summary>
        public uint PatchTime { get; set; }

        /// <summary>令牌文本的字节(网关登录响应里 0| 之后的那段)。</summary>
        public byte[] Token { get; set; } = Array.Empty<byte>();

        /// <summary>用户名字节(只检查长度)。</summary>
        public byte[] UserName { get; set; } = Array.Empty<byte>();

        /// <summary>口令块字节(只检查长度)。</summary>
        public byte[] PasswordBlock { get; set; } = Array.Empty<byte>();

        /// <summary>登录模式,0 到 2。</summary>
        public byte LoginMode { get; set; }

        /// <summary>8 字节鼠标口令密文(服务端用全局 TEA 对象解密后要求明文长度不超过 6、逐字节只能是数字或小写 a,E-329)。</summary>
        public byte[] MouseCipher { get; set; } = new byte[MouseCipherBytes];

        /// <summary>客户端数据版本(服务端期望值来自它自己的数据文件,E-328;填错回码 0x17)。</summary>
        public uint DataVersion { get; set; }

        /// <summary>服务器组,0 到 0x11。</summary>
        public ushort ServerGroup { get; set; }

        /// <summary>本机地址(服务端只记录)。</summary>
        public uint LocalAddress { get; set; }

        /// <summary>可选的登出状态字节;为空就不写。</summary>
        public byte? LogoutState { get; set; }

        /// <summary>本地检查:返回第一个不合规的字段,全部合规返回 <see cref="LoginRequestProblem.None"/>。</summary>
        public LoginRequestProblem Validate()
        {
            if (PatchTime > MaxPatchTime) return LoginRequestProblem.PatchTimeTooLarge;
            if (!IsText(Token, 1, MaxTokenBytes)) return LoginRequestProblem.BadToken;
            if (!IsText(UserName, 0, MaxUserNameBytes)) return LoginRequestProblem.BadUserName;
            if (!IsText(PasswordBlock, 0, MaxPasswordBlockBytes)) return LoginRequestProblem.BadPasswordBlock;
            if (LoginMode > MaxLoginMode) return LoginRequestProblem.BadLoginMode;
            if (MouseCipher == null || MouseCipher.Length != MouseCipherBytes) return LoginRequestProblem.BadMouseCipher;
            if (ServerGroup > MaxServerGroup) return LoginRequestProblem.BadServerGroup;
            return LoginRequestProblem.None;
        }

        /// <summary>按字段顺序写出请求体(明文,不含帧头与折叠哈希);字段不合规时抛 <see cref="InvalidOperationException"/>。</summary>
        public byte[] Build()
        {
            LoginRequestProblem problem = Validate();
            if (problem != LoginRequestProblem.None)
            {
                throw new InvalidOperationException("登录请求字段不合规: " + problem);
            }
            PacketWriter w = new PacketWriter(0);
            Put(w, w.PutUInt(PatchTime));
            Put(w, w.PutUInt((uint)Token.Length));
            Put(w, w.PutBinary(Token));
            PutLengthPrefixed(w, UserName);
            PutLengthPrefixed(w, PasswordBlock);
            Put(w, w.PutByte(LoginMode));
            Put(w, w.PutUInt(MouseCipherBytes));
            Put(w, w.PutBinary(MouseCipher));
            Put(w, w.PutUInt(DataVersion));
            Put(w, w.PutShort(ServerGroup));
            Put(w, w.PutUInt(LocalAddress));
            if (LogoutState.HasValue)
            {
                Put(w, w.PutByte(LogoutState.Value));
            }
            return w.ToArray();
        }

        private static void PutLengthPrefixed(PacketWriter w, byte[] data)
        {
            Put(w, w.PutUInt((uint)data.Length));
            if (data.Length > 0)
            {
                Put(w, w.PutBinary(data));       // 空块在原语里是失败,长度为 0 时只写长度字段
            }
        }

        private static void Put(PacketWriter w, bool ok)
        {
            if (!ok)
            {
                throw new InvalidOperationException("登录请求写入失败(超过写游标上限)");
            }
        }

        private static bool IsText(byte[] data, int minLength, int maxLength)
        {
            if (data == null || data.Length < minLength || data.Length > maxLength) return false;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == 0) return false;
            }
            return true;
        }
    }
}
