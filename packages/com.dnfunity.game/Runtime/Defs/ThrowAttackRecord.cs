using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-103([throw attack] 块的结构与默认值取自客户端加载 .mob 的函数;含义取自"投掷"一族函数对各项的读法)。
namespace Dnf.Game
{
    /// <summary>
    /// 怪物 .mob 的一个 [throw attack] 块(证据 E-103):出招动画播到第几帧时,在身边放几个被动对象(石头、飞刀一类的投射物)。
    /// 块写成一个标签加若干"子标签名(字符串)+ 值"的对,到 [/throw attack] 为止;客户端为每个块建一条记录,各项的缺省值如下(下面的命名是我们自己起的):
    /// <list type="bullet">
    /// <item>[attack index] 哪一招(<c>[attack motion]</c> 的下标)带这个投掷,缺省 -1(不属于任何一招);</item>
    /// <item>[passive object number] 一次放几个,缺省 -1(放不出东西);</item>
    /// <item>[passive object filename] 每个投射物的 .ptl 路径(相对 .mob 所在目录),个数应与上一项相同;</item>
    /// <item>[passive object index] 被动对象编号(passiveobject.lst),缺省 0,为 0 时这条记录不会发射;</item>
    /// <item>[power] 传给创建函数的威力,缺省 -1;[throw frame] 发射帧(动画当前帧超过它才发射),缺省 -1;</item>
    /// <item>[passive object start x/y/z cood] 每个投射物相对出招者的起点偏移(各一个整数表,x 随朝向镜像);</item>
    /// <item>[attack number] 一次出招里连放几次(第一次放完若还剩 2 次以上就重新出招),缺省 -1;</item>
    /// <item>[throw sound] 发射时放的声音标签,缺省空(不放);[object type]、[start kind] 缺省 0;[direction] 每个投射物一个整数,0 或不写 = 与出招者同向,非 0 = 反向。</item>
    /// </list>
    /// 原版数据里的取值不进仓库(本类不含任何原版内容)。
    /// </summary>
    public sealed class ThrowAttackRecord
    {
        public const string BlockTag = "[throw attack]";

        /// <summary>单个块里最多放几个投射物(防御:损坏的数据不会放出成千上万个)。</summary>
        public const int MaxShots = 64;

        private static readonly IReadOnlyList<int> NoInts = Array.Empty<int>();
        private static readonly IReadOnlyList<string> NoStrings = Array.Empty<string>();

        public int AttackIndex { get; }
        public int PassiveObjectNumber { get; }

        /// <summary>每个投射物的 .ptl 路径(原样,没有规范化;相对 .mob 所在目录)。</summary>
        public IReadOnlyList<string> ParticleFiles { get; }

        public int ObjectType { get; }
        public int PassiveObjectIndex { get; }
        public int Power { get; }
        public int ThrowFrame { get; }
        public IReadOnlyList<int> StartX { get; }
        public IReadOnlyList<int> StartY { get; }
        public IReadOnlyList<int> StartZ { get; }
        public int AttackNumber { get; }

        /// <summary>[throw sound] 的声音标签;没写或写成空串时为 null(客户端不放)。</summary>
        public string ThrowSound { get; }

        public int StartKind { get; }
        public IReadOnlyList<int> Directions { get; }

        public ThrowAttackRecord(int attackIndex = -1, int passiveObjectNumber = -1, IReadOnlyList<string> particleFiles = null, int objectType = 0,
            int passiveObjectIndex = 0, int power = -1, int throwFrame = -1, IReadOnlyList<int> startX = null, IReadOnlyList<int> startY = null,
            IReadOnlyList<int> startZ = null, int attackNumber = -1, string throwSound = null, int startKind = 0, IReadOnlyList<int> directions = null)
        {
            AttackIndex = attackIndex;
            PassiveObjectNumber = passiveObjectNumber;
            ParticleFiles = particleFiles ?? NoStrings;
            ObjectType = objectType;
            PassiveObjectIndex = passiveObjectIndex;
            Power = power;
            ThrowFrame = throwFrame;
            StartX = startX ?? NoInts;
            StartY = startY ?? NoInts;
            StartZ = startZ ?? NoInts;
            AttackNumber = attackNumber;
            ThrowSound = string.IsNullOrEmpty(throwSound) ? null : throwSound;
            StartKind = startKind;
            Directions = directions ?? NoInts;
        }

        /// <summary>一次发射放出的投射物个数:[passive object number](负数或 0 为 0),至多 <see cref="MaxShots"/>。</summary>
        public int ShotCount => PassiveObjectNumber <= 0 ? 0 : Math.Min(PassiveObjectNumber, MaxShots);

        /// <summary>第 <paramref name="slot"/> 个投射物的起点偏移(表比个数短时,缺的项按 0)。</summary>
        public (int X, int Y, int Z) StartOffset(int slot)
        {
            return (At(StartX, slot), At(StartY, slot), At(StartZ, slot));
        }

        /// <summary>第 <paramref name="slot"/> 个投射物是不是与出招者反向。</summary>
        public bool IsReversed(int slot)
        {
            return At(Directions, slot) != 0;
        }

        /// <summary>第 <paramref name="slot"/> 个投射物的 .ptl 路径;没有写则为 null。</summary>
        public string ParticleFile(int slot)
        {
            return (uint)slot < (uint)ParticleFiles.Count ? ParticleFiles[slot] : null;
        }

        private static int At(IReadOnlyList<int> list, int slot)
        {
            return (uint)slot < (uint)list.Count ? list[slot] : 0;
        }

        /// <summary>读 .mob 里所有的 [throw attack] 块,按出现顺序;没有则返回空表。</summary>
        public static IReadOnlyList<ThrowAttackRecord> Parse(TagTable tags)
        {
            if (tags == null)
            {
                throw new ArgumentNullException(nameof(tags));
            }
            var result = new List<ThrowAttackRecord>();
            foreach (ScriptLine line in tags.Lines)
            {
                if (line.TagIs(BlockTag))
                {
                    result.Add(ParseBlock(line.Values));
                }
            }
            return result;
        }

        /// <summary>
        /// 读一个块里的值。子标签名是形如 "[xxx]" 的字符串值,它后面的值(直到下一个子标签名)属于它;
        /// 客户端按已读到的个数取表项,这里按"到下一个子标签名为止"取,在写法规整的数据里两者一致。认不得的子标签名连同它的值一起跳过。
        /// </summary>
        public static ThrowAttackRecord ParseBlock(ScriptValue[] values)
        {
            int attackIndex = -1, number = -1, objectType = 0, objectIndex = 0, power = -1, frame = -1, attackNumber = -1, startKind = 0;
            IReadOnlyList<string> files = null;
            IReadOnlyList<int> sx = null, sy = null, sz = null, dirs = null;
            string sound = null;
            int i = 0;
            while (i < values.Length)
            {
                if (!IsSubTag(values[i]))
                {
                    i++;
                    continue;
                }
                string name = values[i].Text;
                int start = i + 1;
                int end = start;
                while (end < values.Length && !IsSubTag(values[end]))
                {
                    end++;
                }
                switch (name.ToLowerInvariant())
                {
                    case "[attack index]":
                        attackIndex = FirstInt(values, start, end, attackIndex);
                        break;
                    case "[passive object number]":
                        number = FirstInt(values, start, end, number);
                        break;
                    case "[passive object filename]":
                        files = Strings(values, start, end);
                        break;
                    case "[object type]":
                        objectType = FirstInt(values, start, end, objectType);
                        break;
                    case "[passive object index]":
                        objectIndex = FirstInt(values, start, end, objectIndex);
                        break;
                    case "[power]":
                        power = FirstInt(values, start, end, power);
                        break;
                    case "[throw frame]":
                        frame = FirstInt(values, start, end, frame);
                        break;
                    case "[passive object start x cood]":
                        sx = Ints(values, start, end);
                        break;
                    case "[passive object start y cood]":
                        sy = Ints(values, start, end);
                        break;
                    case "[passive object start z cood]":
                        sz = Ints(values, start, end);
                        break;
                    case "[attack number]":
                        attackNumber = FirstInt(values, start, end, attackNumber);
                        break;
                    case "[throw sound]":
                        sound = FirstString(values, start, end);
                        break;
                    case "[start kind]":
                        startKind = FirstInt(values, start, end, startKind);
                        break;
                    case "[direction]":
                        dirs = Ints(values, start, end);
                        break;
                }
                i = end;
            }
            return new ThrowAttackRecord(attackIndex, number, files, objectType, objectIndex, power, frame, sx, sy, sz, attackNumber, sound, startKind, dirs);
        }

        private static bool IsSubTag(ScriptValue v)
        {
            return v.Kind == ScriptValueKind.String && v.Text != null && v.Text.Length > 2 && v.Text[0] == '[' && v.Text[v.Text.Length - 1] == ']';
        }

        private static int FirstInt(ScriptValue[] values, int start, int end, int fallback)
        {
            for (int i = start; i < end; i++)
            {
                if (values[i].IsNumber)
                {
                    return values[i].Int;
                }
            }
            return fallback;
        }

        private static string FirstString(ScriptValue[] values, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                if (values[i].Kind == ScriptValueKind.String)
                {
                    return values[i].Text;
                }
            }
            return null;
        }

        private static IReadOnlyList<int> Ints(ScriptValue[] values, int start, int end)
        {
            var list = new List<int>();
            for (int i = start; i < end; i++)
            {
                if (values[i].IsNumber)
                {
                    list.Add(values[i].Int);
                }
            }
            return list;
        }

        private static IReadOnlyList<string> Strings(ScriptValue[] values, int start, int end)
        {
            var list = new List<string>();
            for (int i = start; i < end; i++)
            {
                if (values[i].Kind == ScriptValueKind.String)
                {
                    list.Add(values[i].Text);
                }
            }
            return list;
        }
    }
}
