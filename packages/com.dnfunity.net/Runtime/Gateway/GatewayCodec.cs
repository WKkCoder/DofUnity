using System;
using System.Text;

namespace Dnf.Net.Gateway
{
    /// <summary>
    /// 网关的线上格式:<c>base64( nonce(12) ‖ 密文 ‖ 标签(16) )</c>,标准字母表、带 <c>=</c> 填充,无 AAD,密钥直接用 32 字节、不派生。
    /// 请求与响应用同一个格式、同一个密钥(响应的 nonce 由服务端自己生成)。
    /// 发送侧严格(标准字母表、带填充);解析侧宽容(见 <see cref="TryDecodeBytes"/>),因为部署中的网关与公开版本是否完全一致未核实,
    /// 而放宽 base64 的细节不会削弱认证——内容是否可信完全由 GCM 标签决定。
    /// </summary>
    public static class GatewayCodec
    {
        /// <summary>线上二进制的最小长度:nonce + 标签(明文为空时恰好等于它)。</summary>
        public const int MinWireLength = GcmAes.NonceSize + GcmAes.TagSize;

        // 发送:无效代理项替换成 U+FFFD,不抛(口令与账号字段在更早处已经限定为 ASCII,自由字段如 qq 不值得为它中断登录流程)。
        private static readonly UTF8Encoding Utf8Lenient = new UTF8Encoding(false, false);

        // 接收:严格解码,服务端发来的不是合法 UTF-8 就当解码失败,不把乱码交给上层。
        private static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);

        /// <summary>把文本明文(UTF-8)加密成可以直接作为请求体的 base64 文本。</summary>
        public static string Encode(byte[] key, string plaintext, INonceSource nonceSource = null)
        {
            if (plaintext == null)
            {
                throw new ArgumentNullException(nameof(plaintext));
            }
            return EncodeBytes(key, Utf8Lenient.GetBytes(plaintext), nonceSource);
        }

        /// <summary>字节明文版本。<paramref name="nonceSource"/> 为 null 时用随机 nonce。</summary>
        public static string EncodeBytes(byte[] key, byte[] plaintext, INonceSource nonceSource = null)
        {
            byte[] nonce = (nonceSource ?? RandomNonceSource.Default).NextNonce();
            if (nonce == null || nonce.Length != GcmAes.NonceSize)
            {
                throw new InvalidOperationException("nonce source returned a nonce of the wrong size");
            }
            byte[] sealedData = GcmAes.Seal(key, nonce, plaintext);
            byte[] wire = new byte[nonce.Length + sealedData.Length];
            Buffer.BlockCopy(nonce, 0, wire, 0, nonce.Length);
            Buffer.BlockCopy(sealedData, 0, wire, nonce.Length, sealedData.Length);
            return Convert.ToBase64String(wire);
        }

        /// <summary>解码并解密为文本;任何格式或认证问题(含明文不是合法 UTF-8)都返回 false,不抛。key 长度不对仍抛 <see cref="ArgumentException"/>。</summary>
        public static bool TryDecode(byte[] key, string base64, out string plaintext)
        {
            plaintext = null;
            byte[] bytes;
            if (!TryDecodeBytes(key, base64, out bytes))
            {
                return false;
            }
            try
            {
                plaintext = Utf8Strict.GetString(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        /// <summary>
        /// 解码并解密为字节。拒绝:非法 base64、解出来短于 12+16 字节、标签不对(含 nonce/密文被改)。
        /// 宽容:忽略空白(响应末尾常带换行)、接受缺失的 <c>=</c> 填充、接受 URL 安全字母表(<c>-</c>、<c>_</c>)。
        /// </summary>
        public static bool TryDecodeBytes(byte[] key, string base64, out byte[] plaintext)
        {
            plaintext = null;
            if (key == null || key.Length != GcmAes.KeySize)
            {
                throw new ArgumentException("key must be " + GcmAes.KeySize + " bytes (AES-256)");
            }
            byte[] wire;
            if (!TryFromBase64Lenient(base64, out wire) || wire.Length < MinWireLength)
            {
                return false;
            }
            byte[] nonce = new byte[GcmAes.NonceSize];
            Buffer.BlockCopy(wire, 0, nonce, 0, nonce.Length);
            byte[] sealedData = new byte[wire.Length - nonce.Length];
            Buffer.BlockCopy(wire, nonce.Length, sealedData, 0, sealedData.Length);
            return GcmAes.TryOpen(key, nonce, sealedData, out plaintext);
        }

        /// <summary>宽容的 base64 解析:去空白、URL 安全字母表换回标准字母表、补齐 <c>=</c> 填充;其余不合法的一律失败。</summary>
        private static bool TryFromBase64Lenient(string text, out byte[] data)
        {
            data = null;
            if (text == null)
            {
                return false;
            }
            StringBuilder sb = new StringBuilder(text.Length + 3);
            foreach (char ch in text)
            {
                if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n')
                {
                    continue;
                }
                sb.Append(ch == '-' ? '+' : ch == '_' ? '/' : ch);
            }
            int length = sb.Length;
            while (length > 0 && sb[length - 1] == '=')
            {
                length--;
            }
            if (sb.Length - length > 2)
            {
                return false;
            }
            // 剩余 1 个字符的长度不可能是合法 base64:补成 "X===" 后由 FromBase64String 拒绝(FormatException),所以这里不必单独判断
            int rest = length % 4;
            sb.Length = length;
            if (rest != 0)
            {
                sb.Append('=', 4 - rest);
            }
            try
            {
                data = Convert.FromBase64String(sb.ToString());
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
