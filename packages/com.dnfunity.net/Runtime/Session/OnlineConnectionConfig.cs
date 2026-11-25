using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Dnf.Net.Gateway;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 连接配置读不出来的原因。每一项只点名缺的或不合规的那个配置项,不带任何值(网关地址、密钥、鼠标口令密文都是用户自己的服务器配置)。
    /// 一次只报第一个问题,顺序固定:网关地址、网关密钥、鼠标口令密文、数据版本、频道端口、客户端版本号。
    /// </summary>
    public enum OnlineConnectionProblem
    {
        /// <summary>没有问题。</summary>
        None = 0,

        /// <summary>配置文件不存在。</summary>
        FileMissing,

        /// <summary>配置文件在,但读不了(权限、被占用等)。</summary>
        FileUnreadable,

        /// <summary>没写网关地址(server_url)。</summary>
        GatewayUrlMissing,

        /// <summary>网关地址形状不对(要求与 <see cref="GatewayOptions"/> 一致:绝对的 http/https 地址,不带查询、片段与用户信息)。</summary>
        GatewayUrlInvalid,

        /// <summary>没写网关密钥(aes_key)。</summary>
        AesKeyMissing,

        /// <summary>网关密钥形状不对(要求与 <see cref="GatewayOptions.TryParseAesKeyHex"/> 一致:恰好 64 个十六进制字符)。</summary>
        AesKeyInvalid,

        /// <summary>没写鼠标口令密文(mouse_cipher)。</summary>
        MouseCipherMissing,

        /// <summary>鼠标口令密文形状不对(恰好 16 个十六进制字符 = 8 字节)。</summary>
        MouseCipherInvalid,

        /// <summary>数据版本(data_version)不是 0 到 4294967295 的十进制整数。</summary>
        DataVersionInvalid,

        /// <summary>频道端口(channel_port)不是 1 到 65535 的十进制整数。</summary>
        ChannelPortInvalid,

        /// <summary>客户端版本号(client_version)不是 0 到 4294967295 的十进制整数。</summary>
        ClientVersionInvalid,
    }

    /// <summary>
    /// 正式分发的联网入口配置:从一个简单的 <c>键 = 值</c> 文本文件读出网关与游戏频道的连接参数(不含账号与口令:那是登录界面里输入的)。
    /// 这些值不在仓库、不在构建里,由用户自己放在应用的持久数据目录下(文件名 <see cref="FileName"/>)。
    /// <para>
    /// 文件格式:每行一项,<c>键 = 值</c>,值两侧的空白与成对的引号会去掉;空行与 <c>#</c> 开头的行忽略;键不分大小写;重复的键以后写的为准;不认识的键忽略。键名:
    /// <c>server_url</c>(必填,网关地址)、<c>aes_key</c>(必填,64 个十六进制字符)、<c>mouse_cipher</c>(必填,16 个十六进制字符 = 8 字节)、
    /// <c>data_version</c>(可选,缺省 <see cref="LocalDevEntrySettings.DefaultDataVersion"/>)、<c>channel_port</c>(可选,缺省 <see cref="DefaultChannelPort"/>,1 到 65535)、
    /// <c>client_version</c>(可选,缺省 0:登录请求里的客户端版本号,0 到 4294967295 的十进制整数,E-453;空值当没写,值怎么折出来见探针的 <c>client-version</c> 子命令)。
    /// <c>server_url</c> 与 <c>aes_key</c> 的写法与游戏数据目录里 Config.toml 的同名项一样,可以直接抄过来。
    /// </para>
    /// <para>
    /// 校验口径与 <see cref="LocalDevEntrySettings"/> 一致:网关走 <see cref="GatewayOptions.TryCreate"/>(超时同为 15 秒),鼠标口令密文是 8 字节
    /// (<see cref="LoginRequest.MouseCipherBytes"/>),数据版本与频道端口的缺省值同它与 <see cref="OnlineEntrySettings"/>。唯一的差别是鼠标口令密文这里必填:
    /// 本机联调读法里缺了它只是发全零(服务端大概率回失败,E-357),正式配置里缺了就直接提示,免得用户对着一个看不出原因的"游戏登录被拒绝"猜。
    /// </para>
    /// 解析是纯函数(<see cref="TryParse"/>),文件读取只是外面薄薄的一层(<see cref="TryLoad"/>)。失败消息(<see cref="Describe"/>)只点名配置项,不带值。
    /// </summary>
    public sealed class OnlineConnectionConfig
    {
        /// <summary>配置文件名(放在应用的持久数据目录下)。</summary>
        public const string FileName = "dnf-online.conf";

        public const string KeyGatewayUrl = "server_url";
        public const string KeyAesKey = "aes_key";
        public const string KeyMouseCipher = "mouse_cipher";
        public const string KeyDataVersion = "data_version";
        public const string KeyChannelPort = "channel_port";
        public const string KeyClientVersion = "client_version";

        /// <summary>频道端口缺省值(与 <see cref="OnlineEntrySettings.ChannelPort"/> 的缺省值相同)。</summary>
        public const int DefaultChannelPort = 30011;

        /// <summary>网关请求超时(与 <see cref="LocalDevEntrySettings"/> 用的相同)。</summary>
        public static readonly TimeSpan GatewayTimeout = TimeSpan.FromSeconds(15);

        // 用来分开判断网关地址与密钥各自合不合规的、已知合规的占位值(只在本类内部用,不是任何真实配置;密钥现拼,源码里不留 64 位十六进制常量)
        private const string ProbeUrl = "http://localhost";
        private static readonly string ProbeKey = new string('0', GatewayProtocol.AesKeyHexLength);

        private readonly byte[] _mouseCipher;

        private OnlineConnectionConfig(GatewayOptions gateway, byte[] mouseCipher, uint dataVersion, int channelPort, uint clientVersion)
        {
            Gateway = gateway;
            _mouseCipher = mouseCipher;
            DataVersion = dataVersion;
            ChannelPort = channelPort;
            ClientVersion = clientVersion;
        }

        /// <summary>网关连接参数(地址与密钥)。</summary>
        public GatewayOptions Gateway { get; }

        /// <summary>数据版本。</summary>
        public uint DataVersion { get; }

        /// <summary>默认频道端口。</summary>
        public int ChannelPort { get; }

        /// <summary>登录请求里的客户端版本号(缺省 0)。</summary>
        public uint ClientVersion { get; }

        /// <summary>8 字节鼠标口令密文的副本。</summary>
        public byte[] CopyMouseCipher()
        {
            return (byte[])_mouseCipher.Clone();
        }

        /// <summary>
        /// 组成联网会话要的入参(账号与口令留空:登录时由界面输入交给会话)。每次返回新对象,鼠标口令密文是副本。
        /// </summary>
        public OnlineEntrySettings ToSettings()
        {
            return new OnlineEntrySettings
            {
                Gateway = Gateway,
                MouseCipher = CopyMouseCipher(),
                DataVersion = DataVersion,
                ChannelPort = ChannelPort,
                ClientVersion = ClientVersion,
            };
        }

        /// <summary>配置文件在 <paramref name="directory"/> 下的完整路径。</summary>
        public static string PathUnder(string directory)
        {
            return Path.Combine(directory ?? string.Empty, FileName);
        }

        /// <summary>读文件再解析。文件不存在、读不了、内容有问题都返回 false 并给出 <paramref name="problem"/>;不抛异常。</summary>
        public static bool TryLoad(string path, out OnlineConnectionConfig config, out OnlineConnectionProblem problem)
        {
            config = null;
            string text;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    problem = OnlineConnectionProblem.FileMissing;
                    return false;
                }
                text = File.ReadAllText(path);
            }
            catch (IOException)
            {
                problem = OnlineConnectionProblem.FileUnreadable;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                problem = OnlineConnectionProblem.FileUnreadable;
                return false;
            }
            return TryParse(text, out config, out problem);
        }

        /// <summary>解析配置文本(纯函数,不碰文件)。成功时 <paramref name="problem"/> 是 <see cref="OnlineConnectionProblem.None"/>。</summary>
        public static bool TryParse(string text, out OnlineConnectionConfig config, out OnlineConnectionProblem problem)
        {
            config = null;
            Dictionary<string, string> values = ParseKeyValues(text);

            values.TryGetValue(KeyGatewayUrl, out string url);
            if (string.IsNullOrEmpty(url))
            {
                problem = OnlineConnectionProblem.GatewayUrlMissing;
                return false;
            }
            if (!GatewayOptions.TryCreate(url, ProbeKey, null, out _, out _))
            {
                problem = OnlineConnectionProblem.GatewayUrlInvalid;
                return false;
            }

            values.TryGetValue(KeyAesKey, out string aesKey);
            if (string.IsNullOrEmpty(aesKey))
            {
                problem = OnlineConnectionProblem.AesKeyMissing;
                return false;
            }
            if (!GatewayOptions.TryCreate(ProbeUrl, aesKey, null, out _, out _))
            {
                problem = OnlineConnectionProblem.AesKeyInvalid;
                return false;
            }

            values.TryGetValue(KeyMouseCipher, out string cipherHex);
            if (string.IsNullOrEmpty(cipherHex))
            {
                problem = OnlineConnectionProblem.MouseCipherMissing;
                return false;
            }
            if (!TryParseMouseCipher(cipherHex, out byte[] mouseCipher))
            {
                problem = OnlineConnectionProblem.MouseCipherInvalid;
                return false;
            }

            uint dataVersion = LocalDevEntrySettings.DefaultDataVersion;
            if (values.TryGetValue(KeyDataVersion, out string versionText) && !string.IsNullOrEmpty(versionText)
                && !uint.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out dataVersion))
            {
                problem = OnlineConnectionProblem.DataVersionInvalid;
                return false;
            }

            int channelPort = DefaultChannelPort;
            if (values.TryGetValue(KeyChannelPort, out string portText) && !string.IsNullOrEmpty(portText)
                && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out channelPort)
                    || channelPort < 1 || channelPort > 65535))
            {
                problem = OnlineConnectionProblem.ChannelPortInvalid;
                return false;
            }

            uint clientVersion = 0;
            if (values.TryGetValue(KeyClientVersion, out string clientVersionText) && !string.IsNullOrEmpty(clientVersionText)
                && !OnlineProbeKnobParser.TryParseClientVersion(clientVersionText, out clientVersion))
            {
                problem = OnlineConnectionProblem.ClientVersionInvalid;
                return false;
            }

            if (!GatewayOptions.TryCreate(url, aesKey, GatewayTimeout, out GatewayOptions gateway, out _))
            {
                // 地址与密钥分开都合规、合起来却不行:不会发生;保险起见按地址问题报,不放过
                problem = OnlineConnectionProblem.GatewayUrlInvalid;
                return false;
            }

            config = new OnlineConnectionConfig(gateway, mouseCipher, dataVersion, channelPort, clientVersion);
            problem = OnlineConnectionProblem.None;
            return true;
        }

        /// <summary>
        /// 给人看的一句话,只点名配置项(不带任何值)。<see cref="OnlineConnectionProblem.None"/> 返回空串。
        /// </summary>
        public static string Describe(OnlineConnectionProblem problem)
        {
            switch (problem)
            {
                case OnlineConnectionProblem.FileMissing:
                    return "找不到连接配置文件 " + FileName;
                case OnlineConnectionProblem.FileUnreadable:
                    return "读不了连接配置文件 " + FileName;
                case OnlineConnectionProblem.GatewayUrlMissing:
                    return "缺少 " + KeyGatewayUrl;
                case OnlineConnectionProblem.GatewayUrlInvalid:
                    return KeyGatewayUrl + " 不合规(要 http 或 https 开头的完整地址)";
                case OnlineConnectionProblem.AesKeyMissing:
                    return "缺少 " + KeyAesKey;
                case OnlineConnectionProblem.AesKeyInvalid:
                    return KeyAesKey + " 不合规(要 64 个十六进制字符)";
                case OnlineConnectionProblem.MouseCipherMissing:
                    return "缺少 " + KeyMouseCipher;
                case OnlineConnectionProblem.MouseCipherInvalid:
                    return KeyMouseCipher + " 不合规(要 16 个十六进制字符)";
                case OnlineConnectionProblem.DataVersionInvalid:
                    return KeyDataVersion + " 不合规(要 0 到 4294967295 的整数)";
                case OnlineConnectionProblem.ChannelPortInvalid:
                    return KeyChannelPort + " 不合规(要 1 到 65535 的整数)";
                case OnlineConnectionProblem.ClientVersionInvalid:
                    return KeyClientVersion + " 不合规(要 0 到 4294967295 的整数)";
                default:
                    return string.Empty;
            }
        }

        private static Dictionary<string, string> ParseKeyValues(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text))
            {
                return result;
            }
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0])
                {
                    value = value.Substring(1, value.Length - 2).Trim();
                }
                if (key.Length > 0)
                {
                    result[key] = value;
                }
            }
            return result;
        }

        /// <summary>鼠标口令密文:恰好 16 个十六进制字符(大小写都收,不 trim)→ 8 字节。</summary>
        private static bool TryParseMouseCipher(string hex, out byte[] cipher)
        {
            cipher = null;
            if (hex == null || hex.Length != 2 * LoginRequest.MouseCipherBytes)
            {
                return false;
            }
            var bytes = new byte[LoginRequest.MouseCipherBytes];
            for (int i = 0; i < bytes.Length; i++)
            {
                int hi = HexValue(hex[2 * i]);
                int lo = HexValue(hex[2 * i + 1]);
                if (hi < 0 || lo < 0)
                {
                    return false;
                }
                bytes[i] = (byte)((hi << 4) | lo);
            }
            cipher = bytes;
            return true;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }
            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }
            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }
            return -1;
        }
    }
}
