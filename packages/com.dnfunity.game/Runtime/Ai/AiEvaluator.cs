using System;
using Dnf.Data;

// 证据:求值循环、比较运算符 0–13 的语义与区域判断见 E-052、E-053、E-128(静态读码,对着样本脚本核方向);
// 随机数用客户端的 rand(0)(宿主给);没读通的条件交给宿主。
namespace Dnf.Game.Ai
{
    /// <summary>
    /// 区域判断的几何(客户端把"以自己为原点的矩形"换算成世界坐标的那几步):位置与参数都先取整(向零截断),
    /// Y 方向 [自己Y − p2, 自己Y + p3];X 方向 p0 是"身后"的距离、p1 是"身前"的距离,朝右(原版朝向 0)时 [自己X − p0, 自己X + p1],朝左时 [自己X − p1, 自己X + p0]。
    /// </summary>
    public static class AiArea
    {
        /// <summary>算出相对区域的世界坐标闭区间。</summary>
        public static void Relative(float selfX, float selfY, bool facingLeft, float behind, float front, float down, float up,
            out float xMin, out float xMax, out float yMin, out float yMax)
        {
            int sx = (int)selfX;
            int sy = (int)selfY;
            int b = (int)behind;
            int f = (int)front;
            if (facingLeft)
            {
                xMin = sx - f;
                xMax = sx + b;
            }
            else
            {
                xMin = sx - b;
                xMax = sx + f;
            }
            yMin = sy - (int)down;
            yMax = sy + (int)up;
        }

        /// <summary>点 (<paramref name="x"/>, <paramref name="y"/>) 取整后落在区间里(闭区间,X/Y 都只看平面坐标)。</summary>
        public static bool Contains(float xMin, float xMax, float yMin, float yMax, float x, float y)
        {
            int tx = (int)x;
            int ty = (int)y;
            return tx >= xMin && tx <= xMax && ty >= yMin && ty <= yMax;
        }
    }

    /// <summary>
    /// .ai 决策树的求值器(证据 E-052、E-053、E-128)。一次求值:从脚本的根开始,按每个 [think] 结点的条件走真或假分支,
    /// 碰到 [import ai] 就换成同一个目录里的另一个脚本接着走(不再检查条件),走到 [return] 或 [return str] 停下;没写的分支得 -1。
    /// 条件分三类:比较(读暂存槽,<see cref="AiScratch"/>)、函数(<c>[void]</c>,问宿主 <see cref="IAiHost"/>)、其余带专用码的关键字(交宿主)。
    /// </summary>
    public sealed class AiEvaluator
    {
        /// <summary>[import ai] 最多套这么多层(原版没有这条限制;防互相导入的坏数据死循环)。</summary>
        public const int MaxImportDepth = 16;

        private readonly Func<string, AiScript> _loader;

        /// <param name="loader">按 PVF 路径取已解析的脚本;文件不存在时返回 null(这一支就得 -1)。</param>
        public AiEvaluator(Func<string, AiScript> loader)
        {
            _loader = loader;
        }

        /// <summary>
        /// 求一个脚本的结果:返回叶子(数值、名字或默认值 -1)。<paramref name="path"/> 是脚本在 PVF 里的路径,[import ai] 的文件名相对它所在的目录。
        /// <paramref name="scratch"/> 由调用方在求值前清空并填好;函数条件会改写它的 [last result] 槽。
        /// </summary>
        public AiResult Evaluate(string path, AiScript script, IAiHost host, AiScratch scratch)
        {
            if (script == null)
            {
                return AiResult.Default;
            }
            return Run(path, script.Root, host, scratch);
        }

        private AiResult Run(string path, AiResult result, IAiHost host, AiScratch scratch)
        {
            int depth = 0;
            while (result != null)
            {
                switch (result.Kind)
                {
                    case AiResultKind.Number:
                    case AiResultKind.Name:
                        return result;
                    case AiResultKind.Think:
                        AiNode node = result.Node;
                        result = EvaluateNode(node, host, scratch) ? node.True : node.False;
                        break;
                    case AiResultKind.Import:
                        if (++depth > MaxImportDepth || string.IsNullOrEmpty(result.Name))
                        {
                            return AiResult.Default;
                        }
                        string next = PvfPath.ResolveRelativeTo(path, result.Name);
                        AiScript script = _loader?.Invoke(next);
                        if (script == null)
                        {
                            return AiResult.Default;
                        }
                        path = next;
                        result = script.Root;
                        break;
                    default:
                        return AiResult.Default;
                }
            }
            return AiResult.Default;
        }

        /// <summary>
        /// 这个结点的条件是不是求值器自己就能算(比较运算符与区域、计数、冷却一类的函数);否则交给宿主(<see cref="IAiHost.Other"/>)。
        /// 覆盖度检查用它统计"还有多少条件写法没读通"。
        /// </summary>
        public static bool IsBuiltin(AiNode node)
        {
            int code = node.Code;
            return (code >= 0 && code <= 13) || (code >= AiVocabulary.FirstFunctionCode && code <= 0x1b);
        }

        /// <summary>一个结点的条件值。</summary>
        public static bool EvaluateNode(AiNode node, IAiHost host, AiScratch scratch)
        {
            int code = node.Code;
            if (code >= 0 && code <= 13)
            {
                return Compare(node, code, host, scratch);
            }
            if (code >= AiVocabulary.FirstFunctionCode && code <= 0x1b)
            {
                return CallFunction(node, code, host, scratch);
            }
            return host.Other(node, scratch);
        }

        private static float Param(AiNode node, int index)
        {
            AiParam[] p = node.Params;
            return p != null && index < p.Length ? p[index].Value : 0f;
        }

        /// <summary>随机数除以 <paramref name="divisor"/> 的余数(C 的 fmod,被除数先转成单精度)。</summary>
        private static float Mod(int rand, float divisor)
        {
            return (float)rand % divisor;
        }

        private static float Percent(float value, float max)
        {
            return value / max * 100f;
        }

        /// <summary>
        /// 比较类条件(内部码 0–13):值与"最大值"取自节点类型号对应的暂存槽。
        /// 0 掷骰:rand 对 p1 取余小于 p0;1 带加成的掷骰:余数小于 槽值 + p0;2/7/8/10/12 百分比比较(槽值 ÷ 最大值 × 100);
        /// 3/4/9/11/13 数值比较;5 落在 [p0, p1] 内;6 取随机数写进 [last result] 且恒为真。
        /// </summary>
        private static bool Compare(AiNode node, int code, IAiHost host, AiScratch s)
        {
            int slot = node.KindId;
            float v = s.Value(slot);
            float p0 = Param(node, 0);
            switch (code)
            {
                case 0:
                    return Mod(host.Rand(), Param(node, 1)) < p0;
                case 1:
                    return Mod(host.Rand(), Param(node, 1)) < v + p0;
                case 2:
                    return p0 <= Percent(v, s.Max(slot));
                case 3:
                    return p0 <= v;
                case 4:
                    return v == p0;
                case 5:
                    return p0 <= v && v <= Param(node, 1);
                case 6:
                    s.Set(AiSlot.LastResult, p0 + Mod(host.Rand(), Param(node, 1)), s.Max(AiSlot.LastResult));
                    return true;
                case 7:
                    return Percent(v, s.Max(slot)) == p0;
                case 8:
                    return p0 < Percent(v, s.Max(slot));
                case 9:
                    return p0 < v;
                case 10:
                    return Percent(v, s.Max(slot)) <= p0;
                case 11:
                    return v <= p0;
                case 12:
                    return Percent(v, s.Max(slot)) < p0;
                case 13:
                    return v < p0;
                default:
                    return false;
            }
        }

        /// <summary>
        /// [void] 函数条件(内部码 0x0e–0x1b)。求值器自己做的:区域内有没有目标(0x0e)、区域里的敌人数或对象数(0x12、0x14,个数写进 [last result] 并返回真)、
        /// 招式冷却(0x13)、攻击动作开关(0x16、0x17)、受击检查(0x18)、自由状态(0x1b);盒形区域(0x0f)按基础区域近似(多出的两个微调参数没用,全库 65 处);
        /// 环绕、绝对坐标区域等没做的交给宿主。
        /// </summary>
        private static bool CallFunction(AiNode node, int code, IAiHost host, AiScratch s)
        {
            switch (code)
            {
                case 0x0e:
                case 0x0f:
                    if (!host.HasTarget)
                    {
                        return false;
                    }
                    AiArea.Relative(host.SelfX, host.SelfY, host.SelfFacingLeft, Param(node, 0), Param(node, 1), Param(node, 2), Param(node, 3),
                        out float xMin, out float xMax, out float yMin, out float yMax);
                    return AiArea.Contains(xMin, xMax, yMin, yMax, host.TargetX, host.TargetY);
                case 0x12:
                case 0x14:
                    AiArea.Relative(host.SelfX, host.SelfY, host.SelfFacingLeft, Param(node, 0), Param(node, 1), Param(node, 2), Param(node, 3),
                        out float cx0, out float cx1, out float cy0, out float cy1);
                    int count = code == 0x12
                        ? host.CountInArea(cx0, cx1, cy0, cy1, -1, AiObjectKind.All, -1)
                        : host.CountInArea(cx0, cx1, cy0, cy1, (int)Param(node, 4), (int)Param(node, 5), (int)Param(node, 6));
                    s.Set(AiSlot.LastResult, count, s.Max(AiSlot.LastResult));
                    return true;
                case 0x13:
                    return host.IsAttackCooling((int)Param(node, 0));
                case 0x16:
                    return host.IsAttackEnabled((int)Param(node, 0), (int)Param(node, 1));
                case 0x17:
                    return host.IsCustomEnabled((int)Param(node, 0), (int)Param(node, 1));
                case 0x18:
                    return host.DamageCheck((int)Param(node, 0));
                case 0x1b:
                    return host.IsFreeState();
                default:
                    return host.Other(node, s);
            }
        }
    }
}
