using System;
using System.Globalization;

namespace Dnf.Net.Gateway
{
    /// <summary>
    /// 网关客户端配置:基地址、AES 密钥(64 个十六进制字符 → 32 字节,直接作密钥,不做派生)、请求超时。
    /// 密钥与主机地址都不进 <see cref="ToString"/> 与异常消息:它们是用户自己的服务器配置,不该出现在日志里。
    /// </summary>
    public sealed class GatewayOptions
    {
        /// <summary>默认请求超时。</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

        /// <summary>允许配置的最长超时(客户端自设,避免误配成"几乎永不超时")。</summary>
        public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);

        private readonly byte[] _aesKey;

        private enum Problem
        {
            None,
            Url,
            Key,
            Timeout,
        }

        /// <summary>校验并创建;失败抛 <see cref="ArgumentException"/>(超时越界抛 <see cref="ArgumentOutOfRangeException"/>),消息不含密钥与地址。</summary>
        public GatewayOptions(string baseUrl, string aesKeyHex, TimeSpan? timeout = null)
        {
            string url;
            byte[] key;
            TimeSpan effectiveTimeout;
            string message;
            Problem problem = Validate(baseUrl, aesKeyHex, timeout, out url, out key, out effectiveTimeout, out message);
            if (problem == Problem.Timeout)
            {
                throw new ArgumentOutOfRangeException("timeout", message);
            }
            if (problem != Problem.None)
            {
                throw new ArgumentException(message);
            }
            BaseUrl = url;
            _aesKey = key;
            Timeout = effectiveTimeout;
        }

        /// <summary>规范化后的基地址:http 或 https,去掉末尾 '/',可带路径前缀(反向代理),不含查询、片段、用户信息。</summary>
        public string BaseUrl { get; }

        /// <summary>请求超时(含读完响应体)。</summary>
        public TimeSpan Timeout { get; }

        /// <summary>不抛异常的创建方式;失败时 <paramref name="problem"/> 是不含密钥与地址的原因。</summary>
        public static bool TryCreate(string baseUrl, string aesKeyHex, TimeSpan? timeout, out GatewayOptions options, out string problem)
        {
            options = null;
            string url;
            byte[] key;
            TimeSpan effectiveTimeout;
            if (Validate(baseUrl, aesKeyHex, timeout, out url, out key, out effectiveTimeout, out problem) != Problem.None)
            {
                return false;
            }
            options = new GatewayOptions(baseUrl, aesKeyHex, effectiveTimeout);
            return true;
        }

        /// <summary>密钥字节的副本(调用方改它不影响配置)。</summary>
        public byte[] CopyAesKey()
        {
            return (byte[])_aesKey.Clone();
        }

        /// <summary>拼出路由的完整地址:基地址 + 路由(路由以 '/' 开头)。</summary>
        public string UrlFor(string route)
        {
            if (route == null)
            {
                throw new ArgumentNullException(nameof(route));
            }
            return BaseUrl + route;
        }

        /// <summary>十六进制密钥解析:必须恰好 64 个十六进制字符(大小写都收,不 trim);成功得到 32 字节。</summary>
        public static bool TryParseAesKeyHex(string hex, out byte[] key)
        {
            key = null;
            if (hex == null || hex.Length != GatewayProtocol.AesKeyHexLength)
            {
                return false;
            }
            byte[] bytes = new byte[GcmAes.KeySize];
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
            key = bytes;
            return true;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return "GatewayOptions(timeout " + Timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s, key set)";
        }

        private static Problem Validate(string baseUrl, string aesKeyHex, TimeSpan? timeout,
            out string normalizedUrl, out byte[] key, out TimeSpan effectiveTimeout, out string message)
        {
            key = null;
            effectiveTimeout = timeout ?? DefaultTimeout;
            message = null;
            if (!TryNormalizeBaseUrl(baseUrl, out normalizedUrl))
            {
                message = "base url must be an absolute http or https address without query, fragment or user info";
                return Problem.Url;
            }
            if (!TryParseAesKeyHex(aesKeyHex, out key))
            {
                message = "aes key must be exactly " + GatewayProtocol.AesKeyHexLength + " hex characters";
                return Problem.Key;
            }
            if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > MaxTimeout)
            {
                message = "timeout must be greater than zero and at most " + MaxTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " seconds";
                return Problem.Timeout;
            }
            return Problem.None;
        }

        private static bool TryNormalizeBaseUrl(string baseUrl, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return false;
            }
            Uri uri;
            if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out uri))
            {
                return false;
            }
            bool schemeOk = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
            if (!schemeOk || uri.Host.Length == 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            {
                return false;
            }
            normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
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
