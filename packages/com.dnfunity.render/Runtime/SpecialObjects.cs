using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-126(地图 [special passive object] 的文法取自服务端 procSpecialPassiveObject 0x089e7958 与 ImportMapScript 0x089dcf54 里的装载;
// 动作种类的编号取自 CMap::set_map 0x0834e18a 里对动作记录第一个数的分支;全库扫描只数结构,不记任何数值)。
namespace Dnf.Render
{
    /// <summary>
    /// 特殊物件上挂的一种动作。编号是服务端动作记录里的种类码(<c>CMap::set_map</c> 按它分支):
    /// 0 = <c>[monster]</c>(物件被破坏时出怪)、2 = <c>[item]</c>(物件被破坏时掉物品)是服务端处理的两种;
    /// 1 = <c>[trap]</c>、3 = <c>[quest]</c> 服务端不处理,留给客户端(这里只解析、不执行)。
    /// </summary>
    public enum SpecialActionKind
    {
        Monster = 0,
        Trap = 1,
        Item = 2,
        Quest = 3,
    }

    /// <summary>
    /// 一个动作:关键字加五个整数(五个数的含义随种类而不同,见 <see cref="SpecialActionKind"/> 与 <see cref="SpecialPlacement"/>)。
    /// <c>[monster]</c> 是 (怪物编号, 等级, 分子, 分母, 保留);<c>[item]</c> 是 (副本里的掉落项下标, 指定物品件数, 随机物品件数, 保留, 保留)。
    /// 这两个的含义读自服务端对它们的使用(<c>CMap::set_map</c>、<c>CBattle_Field::add_item_curr_map</c>),不是猜的;<c>[trap]</c> 与 <c>[quest]</c> 的五个数没有读到用处。
    /// </summary>
    public readonly struct SpecialAction
    {
        public SpecialActionKind Kind { get; }
        public int P1 { get; }
        public int P2 { get; }
        public int P3 { get; }
        public int P4 { get; }
        public int P5 { get; }

        public SpecialAction(SpecialActionKind kind, int p1, int p2, int p3, int p4, int p5)
        {
            Kind = kind;
            P1 = p1;
            P2 = p2;
            P3 = p3;
            P4 = p4;
            P5 = p5;
        }
    }

    /// <summary>
    /// 地图 <c>[special passive object]</c> 里的一项(证据 E-126):被动物件编号与世界坐标(同 <see cref="PassivePlacement"/>),
    /// 外加挂在这个物件上的若干动作。脚本里每项是 编号 x y z n 五个数,后面跟 n 个动作(关键字加五个数)。
    /// 服务端装载时按出现顺序给每一项编号(<see cref="Ordinal"/>,出怪动作绑定的就是它),
    /// 并给"挂了 <c>[item]</c> 动作"的物件另外编一个序号(<see cref="ItemIndex"/>,物品绑定的是它)。
    /// </summary>
    public sealed class SpecialPlacement
    {
        public int Ordinal { get; }
        public int Id { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public IReadOnlyList<SpecialAction> Actions { get; }

        /// <summary>挂了 <c>[item]</c> 动作的物件之间的序号(从 0 数);没有 <c>[item]</c> 动作是 -1。</summary>
        public int ItemIndex { get; }

        public SpecialPlacement(int ordinal, int id, int x, int y, int z, IReadOnlyList<SpecialAction> actions, int itemIndex)
        {
            Ordinal = ordinal;
            Id = id;
            X = x;
            Y = y;
            Z = z;
            Actions = actions ?? new List<SpecialAction>();
            ItemIndex = itemIndex;
        }

        public bool Has(SpecialActionKind kind)
        {
            foreach (SpecialAction a in Actions)
            {
                if (a.Kind == kind)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 解析一个 <c>[special passive object]</c> 标签的全部值。<paramref name="firstOrdinal"/> 与 <paramref name="firstItemIndex"/> 是编号的起点
        /// (一张地图里这个标签出现多次时接着数)。遇到不认识的关键字(<c>[hellparty]</c> 一类服务端另有一套写法的特殊项)、数不够或不是数的值就停下,
        /// 已经完整读出的项保留、半截的丢弃——这样一张地图至多少几个物件,不会读歪。
        /// </summary>
        public static List<SpecialPlacement> Parse(IReadOnlyList<ScriptValue> values, int firstOrdinal = 0, int firstItemIndex = 0)
        {
            var result = new List<SpecialPlacement>();
            if (values == null)
            {
                return result;
            }
            int ordinal = firstOrdinal;
            int itemIndex = firstItemIndex;
            int i = 0;
            var header = new int[5];
            while (true)
            {
                if (!ReadNumbers(values, ref i, header, 5))
                {
                    break;
                }
                int count = header[4];
                if (count < 0 || count > values.Count)
                {
                    break;
                }
                var actions = new List<SpecialAction>(count);
                bool ok = true;
                bool hasItem = false;
                for (int a = 0; a < count && ok; a++)
                {
                    if (i >= values.Count || values[i].Kind != ScriptValueKind.String || !TryKind(values[i].Text, out SpecialActionKind kind))
                    {
                        ok = false;
                        break;
                    }
                    i++;
                    var p = new int[5];
                    if (!ReadNumbers(values, ref i, p, 5))
                    {
                        ok = false;
                        break;
                    }
                    actions.Add(new SpecialAction(kind, p[0], p[1], p[2], p[3], p[4]));
                    hasItem |= kind == SpecialActionKind.Item;
                }
                if (!ok)
                {
                    break;
                }
                result.Add(new SpecialPlacement(ordinal++, header[0], header[1], header[2], header[3], actions, hasItem ? itemIndex++ : -1));
            }
            return result;
        }

        private static bool ReadNumbers(IReadOnlyList<ScriptValue> values, ref int index, int[] into, int count)
        {
            if (index + count > values.Count)
            {
                return false;
            }
            for (int k = 0; k < count; k++)
            {
                ScriptValue v = values[index + k];
                if (!v.IsNumber)
                {
                    return false;
                }
                into[k] = v.Int;
            }
            index += count;
            return true;
        }

        private static bool TryKind(string text, out SpecialActionKind kind)
        {
            switch ((text ?? string.Empty).ToLowerInvariant())
            {
                case "[monster]":
                    kind = SpecialActionKind.Monster;
                    return true;
                case "[trap]":
                    kind = SpecialActionKind.Trap;
                    return true;
                case "[item]":
                    kind = SpecialActionKind.Item;
                    return true;
                case "[quest]":
                    kind = SpecialActionKind.Quest;
                    return true;
                default:
                    kind = SpecialActionKind.Monster;
                    return false;
            }
        }
    }

    /// <summary>
    /// 可破坏物件(<c>passiveobject/mapobject/breakableobject/</c> 下的 .obj)里和"被打"有关的资料:
    /// <c>[int data]</c>、<c>[string data]</c> 的各槽与 <c>[etc motion]</c> 的受击动画(路径已按 .obj 所在目录解析,空槽为 null)。
    /// 槽位含义是推断(客户端的读取代码没有读到,证据 E-126):整数槽 1 是"打几下"、槽 2 为 1 表示打够了就破坏(为 0 的物件只做受击反应,打够后触发它挂的动作);
    /// 字符串槽 4 是受击声、槽 5 是破坏声(<c>audio.xml</c> 的标签),槽 1、2 是受击与破坏的粒子(.ptl,还没有粒子系统,不画);其余槽没有用处。
    /// 依据:本地数据里可破坏物件这几个槽的取值与它们的动画配置(受击动画有几个、带不带粒子与声音)对照得到,不是客户端读码的结果(非 REA)。
    /// </summary>
    public sealed class BreakableData
    {
        public const int HitsSlot = 1;
        public const int DestroySlot = 2;
        public const int HitSoundSlot = 4;
        public const int DestroySoundSlot = 5;

        public IReadOnlyList<int> Ints { get; }
        public IReadOnlyList<string> Strings { get; }
        public IReadOnlyList<string> ReactionMotions { get; }

        public BreakableData(IReadOnlyList<int> ints, IReadOnlyList<string> strings, IReadOnlyList<string> reactionMotions)
        {
            Ints = ints ?? new List<int>();
            Strings = strings ?? new List<string>();
            ReactionMotions = reactionMotions ?? new List<string>();
        }

        /// <summary>打几下才算"打够":整数槽 1,至少 1;没有这个槽按 1。</summary>
        public int HitsNeeded => Ints.Count > HitsSlot && Ints[HitsSlot] > 0 ? Ints[HitsSlot] : 1;

        /// <summary>打够之后物件是不是消失:整数槽 2 非 0 才消失;没有这个槽(只有两个数的草丛一类)按不消失。</summary>
        public bool DestroyedWhenDone => Ints.Count > DestroySlot && Ints[DestroySlot] != 0;

        /// <summary>受击声的标签(空串或没有槽是 null)。</summary>
        public string HitSound => StringAt(HitSoundSlot);

        /// <summary>破坏声的标签(空串或没有槽是 null)。</summary>
        public string DestroySound => StringAt(DestroySoundSlot);

        private string StringAt(int slot)
        {
            return slot < Strings.Count && !string.IsNullOrEmpty(Strings[slot]) ? Strings[slot] : null;
        }

        /// <summary>第 <paramref name="index"/> 个受击动画的路径;越界或是空槽返回 null。</summary>
        public string ReactionMotion(int index)
        {
            return (uint)index < (uint)ReactionMotions.Count ? ReactionMotions[index] : null;
        }

        /// <summary>读一个 .obj 的这几项;<paramref name="doc"/> 里没有 <c>[int data]</c> 时返回 null(不是可破坏物件)。</summary>
        public static BreakableData Read(string scriptPath, IEnumerable<ScriptLine> doc)
        {
            if (doc == null)
            {
                return null;
            }
            List<int> ints = null;
            var strings = new List<string>();
            var motions = new List<string>();
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[int data]"))
                {
                    ints = line.Ints();
                }
                else if (line.TagIs("[string data]"))
                {
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind == ScriptValueKind.String || v.Kind == ScriptValueKind.Keyed)
                        {
                            strings.Add(v.Text ?? string.Empty);
                        }
                    }
                }
                else if (line.TagIs("[etc motion]"))
                {
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind == ScriptValueKind.String)
                        {
                            motions.Add(string.IsNullOrEmpty(v.Text) ? null : PvfPath.ResolveRelativeTo(scriptPath, v.Text));
                        }
                    }
                }
            }
            return ints == null ? null : new BreakableData(ints, strings, motions);
        }
    }
}
