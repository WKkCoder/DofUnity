using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Game.Act
{
    /// <summary>
    /// 把一个 .act 的"一个标签一行"(<see cref="ScriptDocument"/>)搭成语法树。
    /// 规则(证据 E-050,在全库 19,079 个 .act 上验证):一个标签是块,当且仅当文件里它后面有与之配对的闭合标签 [/名字];
    /// 配对按"最近的同名开标签"(栈匹配),没被闭合的标签都是叶子。
    /// 少数笔误闭合(如 [/INDEX]、[/TEAM]、[/TIRGGER])与任何开标签都对不上,且名字本身不是已知的块标签:
    /// 按"最内层的、可以做块的开标签"配对;名字是已知块标签却找不到开标签的(多写的 [/BEHAVIOR] 等)直接丢弃。
    /// </summary>
    public static class ActParser
    {
        /// <summary>可以做块的标签(全库里出现过闭合的 62 个,加上加载函数里认识闭合的另外 6 个)。</summary>
        private static readonly HashSet<string> BlockTags = new HashSet<string>(StringComparer.Ordinal)
        {
            "[ANI]", "[APPENDAGE]", "[ATTRACTION FORCE APPENDAGE]", "[BEHAVIOR]", "[BOOSTER APPENDAGE]",
            "[BOOSTING ANI]", "[CALL FUNC]", "[CAMERA TRACE]", "[CASTING EX]", "[CASTING]", "[CHECK FUNC]",
            "[CHECK PLAYER NUM]", "[CHECKUP]", "[COLOR CHANGE EFFECT]", "[CREATE PASSIVEOBJECT]",
            "[CUT SCENE]", "[DAMAGE]", "[DEFAULT ANI]", "[DELETE APPENDAGE]", "[DIM]", "[EVENT RHYTHM ACTION]",
            "[FLASH SCREEN]", "[GUARD APPENDAGE]", "[HOLD AND DELAY DIE APPENDAGE]", "[HP APPENDAGE]", "[IF]",
            "[INTERPOLATION]", "[IS CATEGORY]", "[IS IN AREA]", "[IS INDEX]", "[IS NOT INDEX]",
            "[IS NOT TARGET STATE]", "[IS OBJECT TYPE]", "[IS TEAM]", "[MASTER APPENDAGE]", "[MOTION]",
            "[MOVE DIR]", "[MOVE PATTERN]", "[NO BUFF APPENDAGE]", "[PARTY TARGET]", "[PASSIVE OBJECT INDEX]",
            "[PULL APPENDAGE EX]", "[PULL APPENDAGE]", "[RANDOM GENERATE PASSIVE OBJECT]", "[RANDOM SELECT]",
            "[RANDOM]", "[RELEASE ANI]", "[RHYTHM ACTION]", "[SET MOVABLE AREA]", "[SET MOVEVELS]",
            "[SET POS FORCE]", "[SET SPEED CONTROL]", "[SET SPEED]", "[SHOW HITGAUGE]", "[SOUND PROBABILITY]",
            "[SOUND]", "[SPEECH POS]", "[SPEECH]", "[START KEYSTROKE MODE]", "[STRAIGHT MOVE]",
            "[SUB ANI WITH XYZ]", "[SUB ANI WITH XY]", "[SUB ANI]", "[SUFFOCATION APPENDAGE]", "[SUMMON APC]",
            "[SUMMON MONSTER]", "[TELEPORT]", "[TRIGGER]",
        };

        /// <summary>这个标签名是否可以做块。</summary>
        public static bool IsBlockCapable(string tag) => tag != null && BlockTags.Contains(tag);

        public static ActScript Parse(ScriptLine[] lines)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            int n = lines.Length;
            int leadingValues = 0;

            // 第一遍:栈匹配。closes[i] = 闭合行 i 所关闭的开标签行;opens[j] = 开标签行 j 的闭合行。
            var opens = new Dictionary<int, int>();
            var closes = new Dictionary<int, int>();
            var stack = new List<int>();
            int stray = 0;
            int repaired = 0;
            for (int i = 0; i < n; i++)
            {
                string tag = lines[i].Tag;
                if (tag == null)
                {
                    leadingValues += lines[i].Values.Length;
                    continue;
                }
                if (!IsCloser(tag))
                {
                    stack.Add(i);
                    continue;
                }
                string name = "[" + tag.Substring(2);
                int at = -1;
                for (int j = stack.Count - 1; j >= 0; j--)
                {
                    if (string.Equals(lines[stack[j]].Tag, name, StringComparison.Ordinal))
                    {
                        at = j;
                        break;
                    }
                }
                bool fixedUp = false;
                if (at < 0 && !BlockTags.Contains(name))
                {
                    for (int j = stack.Count - 1; j >= 0; j--)
                    {
                        if (BlockTags.Contains(lines[stack[j]].Tag))
                        {
                            at = j;
                            fixedUp = true;
                            break;
                        }
                    }
                }
                if (at < 0)
                {
                    stray++;
                    continue;
                }
                if (fixedUp)
                {
                    repaired++;
                }
                opens[stack[at]] = i;
                closes[i] = stack[at];
                stack.RemoveRange(at, stack.Count - at);
            }

            // 第二遍:建树。块节点入栈,闭合行把栈弹到对应的块。
            var roots = new List<ActNode>();
            var nodeAt = new Dictionary<int, ActNode>();
            var open = new List<ActNode>();
            for (int i = 0; i < n; i++)
            {
                string tag = lines[i].Tag;
                if (tag == null)
                {
                    continue;
                }
                if (IsCloser(tag))
                {
                    if (closes.TryGetValue(i, out int openLine) && nodeAt.TryGetValue(openLine, out ActNode block))
                    {
                        block.Closer = tag;
                        int idx = open.LastIndexOf(block);
                        if (idx >= 0)
                        {
                            open.RemoveRange(idx, open.Count - idx);
                        }
                    }
                    continue;
                }
                var node = new ActNode(tag, lines[i].Values);
                if (open.Count > 0)
                {
                    open[open.Count - 1].Add(node);
                }
                else
                {
                    roots.Add(node);
                }
                if (opens.ContainsKey(i))
                {
                    node.IsBlock = true;
                    nodeAt[i] = node;
                    open.Add(node);
                }
            }
            return new ActScript(roots, stray, repaired, leadingValues);
        }

        /// <summary>直接从 PVF 里取出的脚本字节解析。</summary>
        public static ActScript Parse(ReadOnlySpan<byte> data, PvfStringTable strings) =>
            Parse(ScriptDocument.Parse(data, strings));

        private static bool IsCloser(string tag) => tag.Length > 2 && tag[0] == '[' && tag[1] == '/';
    }
}
