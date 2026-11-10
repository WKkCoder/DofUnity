using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:块规则是对全库 63,776 件装备脚本的观察(2026-11 普查),不是服务端读码的转抄:
// 深度 0 的属性标签与块内同名标签并存(如 [level section ability]、[set item]、[level section ani] 里);
// 全部装备脚本按本规则切开后,文件结束时块栈都是空的;有 3 件带孤立的收尾标签(原数据里多写的 [/then] 或笔误),被忽略后不影响深度。
// 服务端装备脚本导入函数(ImportEquipmentScript 0x08991aeb)的静态读码(只读反汇编,没有运行)与此相合:它用一个局部变量记着"当前接收属性的参数结构",
// 开头指向装备自己的那一份(this+0x1c0,0x08991d5f);读到 [set ability](0x08999196)、[piece set ability](0x08999036)、
// [level section ability](0x0899b9e2)就把目标换成另一份参数结构(前两个取自 this+0x678 的映射,后一个压进向量取末项,0x0899baa4),
// 对应的收尾标签([/set ability] 0x0899930a、[/piece set ability] 0x08999397、[/level section ability] 0x0899bafa)再换回 this+0x1c0;
// 属性参数导入函数 importEquipmentParameterInfo(0x089a75ea)被它在两处调用(目标是装备自己的 0x08994e02,是别的结构的 0x08994e37)。
// 没读到的:[level linear ability](507 件有这个块)在该函数里没有字符串引用,服务端怎么读它不知道;这里按"有收尾标签即块"的数据规则把它当块。
namespace Dnf.Game
{
    /// <summary>
    /// 物品脚本里的"块"。脚本是平铺的标记流,没有显式的块语法:文件里<b>同时有</b> <c>[x]</c> 与 <c>[/x]</c> 的 <c>[x]</c> 才算块的开头
    /// (只有开头没有收尾的标签,如 <c>[animation job]</c>,是普通标签);<c>[/x]</c> 把块栈弹到最近的 <c>[x]</c>,栈里没有 <c>[x]</c> 的收尾标签忽略。
    /// 块里面(深度 &gt; 0)的标签属于那个块,不是物品自己的字段——套装、等级段、强化段里会重复写同名的属性标签。
    /// 同一套规则在 <c>tools/py/equip_stats_ref.py</c> 里有独立的 Python 实现,全库逐件对拍。
    /// </summary>
    internal static class ScriptBlocks
    {
        /// <summary>
        /// 每个标签标记所在的块深度(块开头标签自己算外层的深度,收尾标签算弹出之后的深度);数组与 <paramref name="tokens"/> 等长,
        /// 不是标签的标记存 −1。字符串池下标越界的标签不是任何已知标签,不改变块栈。标签名不区分大小写。
        /// </summary>
        public static int[] Depths(ScriptToken[] tokens, PvfStringTable strings)
        {
            var depths = new int[tokens.Length];
            HashSet<string> openers = null;
            for (int i = 0; i < tokens.Length; i++)
            {
                depths[i] = -1;
                if (tokens[i].Type != (byte)TokenType.Tag)
                {
                    continue;
                }
                string tag = TagAt(strings, tokens[i].Value);
                if (IsCloser(tag))
                {
                    if (openers == null)
                    {
                        openers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    openers.Add(OpenerOf(tag));
                }
            }
            var stack = new List<string>();
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Type != (byte)TokenType.Tag)
                {
                    continue;
                }
                string tag = TagAt(strings, tokens[i].Value);
                if (tag == null)
                {
                    depths[i] = stack.Count;
                    continue;
                }
                if (IsCloser(tag))
                {
                    int at = LastIndexOf(stack, OpenerOf(tag));
                    if (at >= 0)
                    {
                        stack.RemoveRange(at, stack.Count - at);
                    }
                    depths[i] = stack.Count;
                    continue;
                }
                depths[i] = stack.Count;
                if (openers != null && openers.Contains(tag))
                {
                    stack.Add(tag);
                }
            }
            return depths;
        }

        /// <summary>标签标记的文本;字符串池下标越界返回 null。</summary>
        public static string TagAt(PvfStringTable strings, int index)
        {
            return (uint)index < (uint)strings.Count ? strings[index] : null;
        }

        private static bool IsCloser(string tag)
        {
            return tag != null && tag.Length >= 2 && tag[0] == '[' && tag[1] == '/';
        }

        private static string OpenerOf(string closer)
        {
            return "[" + closer.Substring(2);
        }

        private static int LastIndexOf(List<string> stack, string name)
        {
            for (int i = stack.Count - 1; i >= 0; i--)
            {
                if (string.Equals(stack[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
