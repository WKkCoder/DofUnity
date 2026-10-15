using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game.Ai
{
    /// <summary>
    /// AI 脚本里"用名字写的数":元素、状态、对象种类、阵营这类枚举。名表不在代码里,
    /// 而是游戏数据文件 AICharacter/OperatingValue.lst(证据 E-052):一串"名字 数值"对,
    /// 客户端第一次需要时读入一张"名字 → 浮点数"的表,最多读 1000 对,遇到读不下去的标记就停;
    /// 同名只认第一次出现的。所以 Unity 端也运行时读用户自己的数据,仓库里不放这张表的内容。
    /// </summary>
    public sealed class AiConstants
    {
        /// <summary>名表在数据包里的路径。</summary>
        public const string ListPath = "aicharacter/operatingvalue.lst";

        /// <summary>原版最多读这么多对。</summary>
        public const int MaxPairs = 1000;

        private readonly Dictionary<string, float> _values = new Dictionary<string, float>(StringComparer.Ordinal);

        public int Count => _values.Count;

        public bool TryGet(string name, out float value) => _values.TryGetValue(name ?? string.Empty, out value);

        /// <summary>登记一个名字(测试和手工构造用);同名保留先登记的,与读文件时的规则一致。</summary>
        public void Add(string name, float value)
        {
            if (name != null && !_values.ContainsKey(name))
            {
                _values.Add(name, value);
            }
        }

        /// <summary>读 OperatingValue.lst 的内容(标记流脚本)。</summary>
        public static AiConstants Parse(ReadOnlySpan<byte> raw, PvfStringTable strings)
        {
            var constants = new AiConstants();
            var cursor = new ScriptCursor(TokenScript.Parse(raw), strings);
            for (int i = 0; i < MaxPairs; i++)
            {
                if (!cursor.ScanStr(out string name))
                {
                    break;
                }
                float value = cursor.ScanFloat(out bool ok);
                if (!ok)
                {
                    break;
                }
                constants.Add(name, value);
            }
            return constants;
        }
    }
}
