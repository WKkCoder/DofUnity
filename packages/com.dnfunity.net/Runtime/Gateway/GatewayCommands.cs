using System;
using System.Security.Cryptography;
using System.Text;

namespace Dnf.Net.Gateway
{
    /// <summary>
    /// 认证命令的明文构造与字段校验。明文是竖线分隔、恰好 4 段的字符串:
    /// <c>login|用户名|口令MD5|mac</c>、<c>regedit|用户名|口令MD5|qq</c>、<c>forget|用户名|qq|新口令MD5</c>、<c>repasswd|用户名|旧口令MD5|新口令MD5</c>。
    /// 校验不过抛 <see cref="ArgumentException"/>,消息只写字段名与规则,绝不回显字段值(口令 MD5 等价于口令)。
    /// 发送侧严格:服务端会把含竖线的字段判为格式错误;MD5 只收小写十六进制(规格写的就是小写,
    /// 注册时一个大写的 MD5 会被原样存进库里,之后与小写比对的地方都会对不上),所以宁可在这里挡下。
    /// </summary>
    public static class GatewayCommands
    {
        /// <summary>登录:<c>login|用户名|md5|mac</c>。<paramref name="mac"/> 服务端不使用,为 null 时用占位串。</summary>
        public static string Login(string username, string md5Hex, string mac = null)
        {
            RequireUsername(username);
            RequireMd5(md5Hex, "password md5");
            if (mac == null)
            {
                mac = GatewayProtocol.DefaultMac;
            }
            RequireFreeField(mac, "mac");
            return Join(GatewayProtocol.LoginCommand, username, md5Hex, mac);
        }

        /// <summary>注册:<c>regedit|用户名|md5|qq</c>。qq 可为空(null 按空串);其余内容留给服务端判定(<c>invalid_qq</c>)。</summary>
        public static string Register(string username, string md5Hex, string qq = null)
        {
            RequireUsername(username);
            RequireMd5(md5Hex, "password md5");
            string q = qq ?? string.Empty;
            RequireFreeField(q, "qq");
            return Join(GatewayProtocol.RegisterCommand, username, md5Hex, q);
        }

        /// <summary>找回口令:<c>forget|用户名|qq|新md5</c>。</summary>
        public static string ForgotPassword(string username, string qq, string newMd5Hex)
        {
            RequireUsername(username);
            string q = qq ?? string.Empty;
            RequireFreeField(q, "qq");
            RequireMd5(newMd5Hex, "new password md5");
            return Join(GatewayProtocol.ForgotCommand, username, q, newMd5Hex);
        }

        /// <summary>修改口令:<c>repasswd|用户名|旧md5|新md5</c>。</summary>
        public static string ChangePassword(string username, string oldMd5Hex, string newMd5Hex)
        {
            RequireUsername(username);
            RequireMd5(oldMd5Hex, "old password md5");
            RequireMd5(newMd5Hex, "new password md5");
            return Join(GatewayProtocol.ChangePasswordCommand, username, oldMd5Hex, newMd5Hex);
        }

        /// <summary>文本的 MD5(UTF-8 字节),32 个小写十六进制字符。口令只在客户端本地算它,不发明文。</summary>
        public static string Md5Hex(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            byte[] digest;
            using (MD5 md5 = MD5.Create())
            {
                digest = md5.ComputeHash(new UTF8Encoding(false).GetBytes(text));
            }
            const string digits = "0123456789abcdef";
            char[] chars = new char[digest.Length * 2];
            for (int i = 0; i < digest.Length; i++)
            {
                chars[2 * i] = digits[digest[i] >> 4];
                chars[2 * i + 1] = digits[digest[i] & 15];
            }
            return new string(chars);
        }

        /// <summary>用户名规则:4–32 个字符,仅 ASCII 字母、数字与下划线。</summary>
        public static bool IsValidUsername(string username)
        {
            if (username == null || username.Length < GatewayProtocol.UsernameMinLength || username.Length > GatewayProtocol.UsernameMaxLength)
            {
                return false;
            }
            foreach (char c in username)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>MD5 文本规则:恰好 32 个小写十六进制字符。</summary>
        public static bool IsMd5Hex(string text)
        {
            if (text == null || text.Length != GatewayProtocol.Md5HexLength)
            {
                return false;
            }
            foreach (char c in text)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>自由文本字段(qq、mac)规则:不含竖线,不含控制字符;长度不设限(整个请求体的上限由调用方按 4096 字节检查)。</summary>
        public static bool IsSafeFreeField(string text)
        {
            if (text == null)
            {
                return false;
            }
            foreach (char c in text)
            {
                if (c == GatewayProtocol.FieldSeparator || c < ' ' || c == '\u007f')
                {
                    return false;
                }
            }
            return true;
        }

        private static string Join(string a, string b, string c, string d)
        {
            char sep = GatewayProtocol.FieldSeparator;
            return a + sep + b + sep + c + sep + d;
        }

        private static void RequireUsername(string username)
        {
            if (!IsValidUsername(username))
            {
                throw new ArgumentException("username must be " + GatewayProtocol.UsernameMinLength + "-" + GatewayProtocol.UsernameMaxLength
                    + " characters of A-Z, a-z, 0-9 and _");
            }
        }

        private static void RequireMd5(string md5Hex, string what)
        {
            if (!IsMd5Hex(md5Hex))
            {
                throw new ArgumentException(what + " must be exactly " + GatewayProtocol.Md5HexLength + " lowercase hex characters");
            }
        }

        private static void RequireFreeField(string value, string what)
        {
            if (!IsSafeFreeField(value))
            {
                throw new ArgumentException(what + " must not contain '" + GatewayProtocol.FieldSeparator + "' or control characters");
            }
        }
    }
}
