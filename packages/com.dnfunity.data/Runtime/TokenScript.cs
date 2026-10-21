using System;
using System.Buffers.Binary;

// 证据:标记流脚本的语法(5 字节标记、类型 2 到 10)见 E-003;全库对拍见 E-008。
namespace Dnf.Data
{
    /// <summary>标记流脚本里的标记类型。含义来自对全库的统计与结构验证,个别名字是推断。</summary>
    public enum TokenType : byte
    {
        /// <summary>整数。</summary>
        Int = 2,

        /// <summary>整数(只在 Unicode 范围表里见到)。</summary>
        UInt = 3,

        /// <summary>浮点:把 i32 的位模式当 f32。</summary>
        Float = 4,

        /// <summary>标签,值是字符串池下标,字符串形如 [name] 或 [/name]。</summary>
        Tag = 5,

        /// <summary>括号词,如 (UP)、(SKILL)。</summary>
        Paren = 6,

        /// <summary>字符串或关键字,如路径、[fixed]。值是字符串池下标。</summary>
        String = 7,

        /// <summary>分隔符 ","。</summary>
        Separator = 8,

        /// <summary>字符串引用的前半(与 <see cref="StringRefKey"/> 总是成对出现;全库只有 25 种固定值)。</summary>
        StringRefKind = 9,

        /// <summary>字符串引用的后半(键)。</summary>
        StringRefKey = 10,
    }

    public readonly struct ScriptToken
    {
        public byte Type { get; }
        public int Value { get; }

        public ScriptToken(byte type, int value)
        {
            Type = type;
            Value = value;
        }

        public float AsFloat => BitConverter.Int32BitsToSingle(Value);
    }

    /// <summary>
    /// 标记流脚本:B0 D0 + N 个 5 字节标记,标记 = u8 类型 + i32 值(小端)。
    /// 全库 147,273 个这类文件的正文长度都能被 5 整除。
    /// </summary>
    public static class TokenScript
    {
        public const byte Magic0 = 0xB0;
        public const byte Magic1 = 0xD0;

        public static bool IsTokenScript(ReadOnlySpan<byte> data)
        {
            return data.Length >= 2 && data[0] == Magic0 && data[1] == Magic1;
        }

        public static ScriptToken[] Parse(ReadOnlySpan<byte> data)
        {
            if (!IsTokenScript(data))
            {
                throw new DnfFormatException("不是标记流脚本(缺少 B0 D0)");
            }
            int body = data.Length - 2;
            if (body % 5 != 0)
            {
                throw new DnfFormatException("标记流正文长度不是 5 的倍数: " + body);
            }
            var tokens = new ScriptToken[body / 5];
            for (int i = 0; i < tokens.Length; i++)
            {
                ReadOnlySpan<byte> cell = data.Slice(2 + 5 * i, 5);
                tokens[i] = new ScriptToken(cell[0], BinaryPrimitives.ReadInt32LittleEndian(cell.Slice(1)));
            }
            return tokens;
        }
    }
}
