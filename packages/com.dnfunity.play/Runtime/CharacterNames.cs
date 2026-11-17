using System.Collections.Generic;
using System.Text;

namespace Dnf.Play
{
    /// <summary>角色名字节转文字的结果类别(<see cref="CharacterNames.Decode"/>)。</summary>
    public enum CharacterNameStatus
    {
        /// <summary>正常解出(第一个字节就是 0 时解出的是空串,也算正常)。</summary>
        Ok = 0,

        /// <summary>长度为 0:客户端用默认名;这里返回调用者给的占位名。</summary>
        Empty = 1,

        /// <summary>长度超过 <see cref="CharacterNames.MaxBytes"/>:真客户端不读字节、之后整个包错位,这里无法照做,返回占位名(见 <see cref="CharacterNames.Decode"/> 的说明)。</summary>
        TooLong = 2,

        /// <summary>字节不是合法的 UTF-8(首字节 0xF0 以上、序列被截断,或本类额外拒绝的畸形序列):名字是空串。</summary>
        Invalid = 3,
    }

    /// <summary>
    /// 角色名(服务端列表里的原始字节)转成文字,规则取自客户端读名字的两个函数(charre 证据 03:读名字 0x11346f0 与 UTF-8 转 UTF-16 的 0x11a02d0):
    /// <list type="bullet">
    /// <item>长度 1 到 <see cref="MaxBytes"/> 才读字节;长度 0 用默认名【观】。</item>
    /// <item>读进来的字节按 C 字符串处理,第一个 0 字节处截断【观:转换前先数 strlen】。</item>
    /// <item>转换只认 1、2、3 字节序列:首字节小于 0x80 一个字节、小于 0xE0 两个字节、小于 0xF0 三个字节;首字节 0xF0 以上(4 字节序列)或序列被截断时转换失败【观】,
    /// 调用方不检查返回值,名字就是空串【推断:转换的目标串事先是空的】。</item>
    /// </list>
    /// 与客户端不同的三处(都是为了不显示乱码或破坏版面,不影响合法名字):
    /// (1) 长度超过 <see cref="MaxBytes"/> 时客户端只读那个长度前缀、之后整个包错位,模拟不了,这里按"名字不可读"给占位名;
    /// (2) 客户端的转换对后续字节只校验个数、不校验是否 10xxxxxx(解码那一段没读到),这里把畸形序列(多余的续字节、过长编码、代理项)一律当失败;
    /// (3) 控制字符(C0、DEL、C1、行分隔符)换成问号,免得换行把一行名字排成两行。
    /// </summary>
    public static class CharacterNames
    {
        /// <summary>客户端读名字的字节数上限(缓冲 0x1F,留一个结尾 0)。</summary>
        public const int MaxBytes = 30;

        /// <summary>
        /// 把名字字节转成文字。<paramref name="placeholder"/> 是长度为 0 或过长时的占位名(为 null 当空串)。
        /// </summary>
        public static string Decode(IReadOnlyList<byte> bytes, string placeholder, out CharacterNameStatus status)
        {
            int length = bytes == null ? 0 : bytes.Count;
            if (length == 0)
            {
                status = CharacterNameStatus.Empty;
                return placeholder ?? string.Empty;
            }
            if (length > MaxBytes)
            {
                status = CharacterNameStatus.TooLong;
                return placeholder ?? string.Empty;
            }

            int end = 0;                                    // C 字符串:第一个 0 字节之前
            while (end < length && bytes[end] != 0)
            {
                end++;
            }

            var text = new StringBuilder(end);
            int i = 0;
            while (i < end)
            {
                int lead = bytes[i];
                int cp;
                if (lead < 0x80)
                {
                    cp = lead;
                    i += 1;
                }
                else if (lead < 0xE0)
                {
                    if (i + 1 >= end || lead < 0xC2 || (bytes[i + 1] & 0xC0) != 0x80)
                    {
                        return Failed(out status);
                    }
                    cp = ((lead & 0x1F) << 6) | (bytes[i + 1] & 0x3F);
                    i += 2;
                }
                else if (lead < 0xF0)
                {
                    if (i + 2 >= end || (bytes[i + 1] & 0xC0) != 0x80 || (bytes[i + 2] & 0xC0) != 0x80)
                    {
                        return Failed(out status);
                    }
                    cp = ((lead & 0x0F) << 12) | ((bytes[i + 1] & 0x3F) << 6) | (bytes[i + 2] & 0x3F);
                    if (cp < 0x800 || (cp >= 0xD800 && cp <= 0xDFFF))
                    {
                        return Failed(out status);          // 过长编码、代理项
                    }
                    i += 3;
                }
                else
                {
                    return Failed(out status);              // 4 字节序列:客户端的转换拒绝
                }
                text.Append(IsControl(cp) ? '?' : (char)cp);
            }
            status = CharacterNameStatus.Ok;
            return text.ToString();
        }

        private static string Failed(out CharacterNameStatus status)
        {
            status = CharacterNameStatus.Invalid;
            return string.Empty;
        }

        private static bool IsControl(int cp)
        {
            return cp < 0x20 || (cp >= 0x7F && cp <= 0x9F) || cp == 0x2028 || cp == 0x2029;
        }
    }
}
