using System;
using System.Text;

// 证据:客户端内嵌文字资源的 "SC01" 容器(解码函数 0x014051e0):前 4 字节魔数 "SC01",随后小端 u32 P(明文字节数),其后是密文体;
// 解密是按小端 32 位字的"链式异或":key 初值 0x5819AF17,明文字 = 密文字 ^ key,下一个字的 key = 这个字的<b>密文</b>。
namespace Dnf.Data
{
    /// <summary>
    /// "SC01" 内嵌文字容器的解码(产品路径只解不编)与 UTF-8 文本化。
    /// <para>
    /// 布局:字节 [0..4) 是 ASCII <c>S C 0 1</c>;[4..8) 是小端 u32 P(明文字节数;真实资源里恰好等于总长减 8);[8..) 是密文体。
    /// 按小端 32 位字逐个处理:<c>key</c> 初值 <see cref="InitialKey"/>;对每个密文字 <c>c</c>,明文字 <c>p = c ^ key</c>,然后 <c>key = c</c>
    /// (等价于 <c>p[i] = c[i] ^ c[i-1]</c>,<c>c[-1] = InitialKey</c>)。体长不是 4 的倍数时,最后一个字按补零处理,输出只取前 P 字节。
    /// </para>
    /// <para>
    /// 实现的选择:P 超过体长一律判为坏容器(客户端自己不检查,我们拒绝);P 小于体长时多出的尾巴忽略;P 为 0 是合法的空文本。
    /// 明文按 UTF-8 解成字符串,开头的 UTF-8 BOM 先去掉;坏字节换成替换字符,不抛异常。
    /// </para>
    /// </summary>
    public static class EmbeddedTextContainer
    {
        /// <summary>解密链的初始密钥。</summary>
        public const uint InitialKey = 0x5819AF17u;

        /// <summary>魔数 4 字节 + 明文字节数 4 字节。</summary>
        public const int HeaderSize = 8;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        /// <summary>解码并按 UTF-8 变成文本。魔数不对、长度不足 8、P 超过体长返回 false(<paramref name="text"/> 为 null)。</summary>
        public static bool TryDecode(byte[] container, out string text)
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }
            text = null;
            if (!TryDecodeBytes(container, out byte[] plain))
            {
                return false;
            }
            int skip = plain.Length >= 3 && plain[0] == 0xEF && plain[1] == 0xBB && plain[2] == 0xBF ? 3 : 0;
            text = Utf8.GetString(plain, skip, plain.Length - skip);
            return true;
        }

        /// <summary>只做解密,不做文本化:返回明文字节(长度正好是 P)。失败条件同 <see cref="TryDecode"/>。</summary>
        public static bool TryDecodeBytes(byte[] container, out byte[] plain)
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }
            plain = null;
            if (container.Length < HeaderSize
                || container[0] != (byte)'S' || container[1] != (byte)'C' || container[2] != (byte)'0' || container[3] != (byte)'1')
            {
                return false;
            }
            uint declared = (uint)(container[4] | (container[5] << 8) | (container[6] << 16) | (container[7] << 24));
            long bodyLength = container.Length - HeaderSize;
            if (declared > bodyLength)
            {
                return false;
            }
            int size = (int)declared;
            var output = new byte[size];
            uint key = InitialKey;
            for (int pos = 0; pos < size; pos += 4)
            {
                // 取一个小端密文字;体里剩下不足 4 字节时高位补零。
                int at = HeaderSize + pos;
                int available = Math.Min(4, container.Length - at);
                uint cipher = 0;
                for (int k = 0; k < available; k++)
                {
                    cipher |= (uint)container[at + k] << (8 * k);
                }
                uint word = cipher ^ key;
                key = cipher;
                int count = Math.Min(4, size - pos);
                for (int k = 0; k < count; k++)
                {
                    output[pos + k] = (byte)(word >> (8 * k));
                }
            }
            plain = output;
            return true;
        }

        /// <summary>
        /// 把文本按 UTF-8(无 BOM)编成 "SC01" 容器:<c>c[i] = p[i] ^ c[i-1]</c>(<c>c[-1] = InitialKey</c>),体长正好等于明文字节数 P(与真实资源一致)。
        /// <b>仅供合成数据与测试,产品路径只解不编。</b>
        /// </summary>
        public static byte[] Encode(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            byte[] plain = Utf8.GetBytes(text);
            var container = new byte[HeaderSize + plain.Length];
            container[0] = (byte)'S';
            container[1] = (byte)'C';
            container[2] = (byte)'0';
            container[3] = (byte)'1';
            uint length = (uint)plain.Length;
            container[4] = (byte)length;
            container[5] = (byte)(length >> 8);
            container[6] = (byte)(length >> 16);
            container[7] = (byte)(length >> 24);
            uint key = InitialKey;
            for (int pos = 0; pos < plain.Length; pos += 4)
            {
                int count = Math.Min(4, plain.Length - pos);
                uint word = 0;
                for (int k = 0; k < count; k++)
                {
                    word |= (uint)plain[pos + k] << (8 * k);
                }
                uint cipher = word ^ key;
                key = cipher;
                for (int k = 0; k < count; k++)
                {
                    container[HeaderSize + pos + k] = (byte)(cipher >> (8 * k));
                }
            }
            return container;
        }
    }
}
