using System;
using Dnf.Data;

namespace Dnf.Game
{
    /// <summary>[homing follow] 的追踪对象种类(客户端 0x0129d570 的解析:关键字 [ME]=0、[ENEMY]=1、[MONSTER] 带整数=2、[CHARACTER] 带整数=3;E-165)。</summary>
    public enum HomingFollow : byte
    {
        /// <summary>[ME]:对象自己的所有者(链条上最早那个创建者)。</summary>
        Me = 0,

        /// <summary>[ENEMY]:所有者的当前目标(怪物放出的就是它盯着的主角)。没写 [homing follow] 时的缺省。</summary>
        Enemy = 1,

        /// <summary>[MONSTER] 编号:场上第一只这个编号的怪物。</summary>
        Monster = 2,

        /// <summary>[CHARACTER] 编号:场上的玩家角色(多人时按编号选;这一版只有一个主角)。</summary>
        Character = 3,
    }

    /// <summary>
    /// 被动对象 .obj 里 [homing] ... [/homing] 块的配置(E-165;客户端加载函数 0x0129d570,缺省值函数 0x01297030,拷进控制器的位置在对象初始化函数 0x00805cd0):
    /// 对象一生下来(<see cref="Use"/> 为真)就按这些参数自己转向、追目标;脚本里 [SET USE HOMING] 可以随时开关。
    /// 只读配置,不含运行状态(状态在 Play 层的 <c>PassiveHoming</c>)。没写的项取客户端缺省值:追踪种类 [ENEMY]、检查间隔 500 毫秒、
    /// 逐帧转角差上限 180 度、朝向模式 -1(不改朝向)、其余全 0。
    /// </summary>
    public sealed class HomingDef
    {
        /// <summary>检查间隔的缺省毫秒数(0x01297030 里的缺省值 500)。</summary>
        public const int DefaultCheckGapMs = 500;

        /// <summary>追踪容许的最大偏角缺省(度;0x01297030 里的 180.0)。</summary>
        public const float DefaultDiffRotation = 180f;

        private HomingDef()
        {
        }

        /// <summary>[homing use]:非 0 才启用(缺省 0)。</summary>
        public bool Use { get; private set; }

        /// <summary>[homing follow] 的种类(缺省 <see cref="HomingFollow.Enemy"/>)。</summary>
        public HomingFollow Follow { get; private set; } = HomingFollow.Enemy;

        /// <summary>[homing follow] [MONSTER]/[CHARACTER] 后面的编号(缺省 0)。</summary>
        public int FollowParam { get; private set; }

        /// <summary>[homing velocity] 的第一个数:初始速度(像素每秒)。客户端把读到的小数截成整数再用(初始化函数 0x00805cd0)。</summary>
        public int Speed { get; private set; }

        /// <summary>[homing velocity] 的第二个数:速度的加速度(像素每秒的平方,可负,速度不会降到 0 以下;同样截成整数)。</summary>
        public int Acceleration { get; private set; }

        /// <summary>[homing check gap]:两次"重新瞄准"之间的毫秒数(缺省 <see cref="DefaultCheckGapMs"/>)。</summary>
        public int CheckGapMs { get; private set; } = DefaultCheckGapMs;

        /// <summary>[sync animation rotation]:非 0 时对象的画面跟着飞行方向转(并且不再按追踪改朝向)。</summary>
        public bool SyncAnimationRotation { get; private set; }

        /// <summary>[max rotation]:每秒最多转多少度;0 表示不限(检查时一步转到目标方向)。</summary>
        public float MaxRotation { get; private set; }

        /// <summary>[init rotation]:出生时的飞行方向(度,数学角,0 朝右;朝左的对象在这个角上再加 180)。</summary>
        public float InitRotation { get; private set; }

        /// <summary>[diff rotation]:当前方向与目标方向差超过这个度数就不再转(目标在追踪锥外,保持原方向;缺省 180 即永远追)。</summary>
        public float DiffRotation { get; private set; } = DefaultDiffRotation;

        /// <summary>[homing time]:启用后追多少毫秒(0 不限)。</summary>
        public int TimeMs { get; private set; }

        /// <summary>[homing z axis]:客户端读进来但追踪逻辑不用它(全库没有写它的对象),只保留。</summary>
        public int ZAxis { get; private set; }

        /// <summary>
        /// [homing direction]:缺省 -1。0 到 3 时出生就把对象的朝向码设成它(0 左、1 右,出生方向的 180 度修正按它算,而不是按对象本来的朝向);
        /// 瞄准点在 0 时是目标 x 加 <see cref="KeepDistance"/>、在 1 时是目标 x 减 <see cref="KeepDistance"/>(追到目标的一侧停住);
        /// 只有 -1 时每次检查才让对象朝向目标(别的值不改朝向)。
        /// </summary>
        public int Direction { get; private set; } = -1;

        /// <summary>[homing keep distance]:配合 <see cref="Direction"/> 的横向偏移(像素)。</summary>
        public int KeepDistance { get; private set; }

        /// <summary>[sync target move]:有这个标签就算(加载函数只置一个字节,不读值):追踪时目标走了多少,对象也跟着平移多少(只在有 <see cref="MaxRotation"/> 的追踪里生效)。</summary>
        public bool SyncTargetMove { get; private set; }

        /// <summary>[straight homing]:有这个标签就算(同样不读值):第一次检查时朝目标(三维)直线瞄一下,之后不再追。</summary>
        public bool Straight { get; private set; }

        /// <summary>从 .obj 的标签表读 [homing] 块;没有这个块返回 null。块里没写的项取缺省。</summary>
        public static HomingDef Read(TagTable tags)
        {
            if (tags == null || !tags.Has("[homing]"))
            {
                return null;
            }
            var d = new HomingDef();
            if (tags.TryGetLine("[homing use]", out ScriptLine use))
            {
                d.Use = Truthy(use);
            }
            if (tags.TryGetLine("[homing follow]", out ScriptLine follow))
            {
                ReadFollow(d, follow);
            }
            if (tags.TryGetLine("[homing velocity]", out ScriptLine velocity))
            {
                double[] v = Numbers(velocity);
                d.Speed = v.Length > 0 ? Truncate(v[0]) : 0;
                d.Acceleration = v.Length > 1 ? Truncate(v[1]) : 0;
            }
            d.CheckGapMs = IntOr(tags, "[homing check gap]", DefaultCheckGapMs);
            d.SyncAnimationRotation = FlagOr(tags, "[sync animation rotation]");
            d.MaxRotation = FloatOr(tags, "[max rotation]", 0f);
            d.InitRotation = FloatOr(tags, "[init rotation]", 0f);
            d.DiffRotation = FloatOr(tags, "[diff rotation]", DefaultDiffRotation);
            d.TimeMs = IntOr(tags, "[homing time]", 0);
            d.ZAxis = IntOr(tags, "[homing z axis]", 0);
            d.Direction = IntOr(tags, "[homing direction]", -1);
            d.KeepDistance = IntOr(tags, "[homing keep distance]", 0);
            d.SyncTargetMove = tags.Has("[sync target move]");
            d.Straight = tags.Has("[straight homing]");
            return d;
        }

        private static void ReadFollow(HomingDef d, ScriptLine line)
        {
            string kind = line.FirstString();
            if (kind == null)
            {
                return;
            }
            int param = 0;
            foreach (int i in line.Ints())
            {
                param = i;
                break;
            }
            if (string.Equals(kind, "[ME]", StringComparison.OrdinalIgnoreCase))
            {
                d.Follow = HomingFollow.Me;
            }
            else if (string.Equals(kind, "[ENEMY]", StringComparison.OrdinalIgnoreCase))
            {
                d.Follow = HomingFollow.Enemy;
            }
            else if (string.Equals(kind, "[MONSTER]", StringComparison.OrdinalIgnoreCase))
            {
                d.Follow = HomingFollow.Monster;
                d.FollowParam = param;
            }
            else if (string.Equals(kind, "[CHARACTER]", StringComparison.OrdinalIgnoreCase))
            {
                d.Follow = HomingFollow.Character;
                d.FollowParam = param;
            }
        }

        private static bool Truthy(ScriptLine line)
        {
            double[] v = Numbers(line);
            return v.Length > 0 && v[0] != 0.0;
        }

        private static bool FlagOr(TagTable tags, string tag)
        {
            return tags.TryGetLine(tag, out ScriptLine line) && Truthy(line);
        }

        private static int IntOr(TagTable tags, string tag, int fallback)
        {
            if (!tags.TryGetLine(tag, out ScriptLine line))
            {
                return fallback;
            }
            double[] v = Numbers(line);
            return v.Length > 0 ? Truncate(v[0]) : fallback;
        }

        private static float FloatOr(TagTable tags, string tag, float fallback)
        {
            if (!tags.TryGetLine(tag, out ScriptLine line))
            {
                return fallback;
            }
            double[] v = Numbers(line);
            return v.Length > 0 ? (float)v[0] : fallback;
        }

        /// <summary>一行里所有数值(整数、十六进制整数、浮点)按出现顺序;同一个属性在不同脚本里可能写成整数或小数。</summary>
        private static double[] Numbers(ScriptLine line)
        {
            var list = new System.Collections.Generic.List<double>(line.Values.Length);
            foreach (ScriptValue v in line.Values)
            {
                if (v.IsNumber)
                {
                    list.Add(v.Int);
                }
                else if (v.Kind == ScriptValueKind.Float)
                {
                    list.Add(v.Float);
                }
            }
            return list.ToArray();
        }

        private static int Truncate(double value)
        {
            return value >= int.MaxValue ? int.MaxValue : value <= int.MinValue ? int.MinValue : (int)value;
        }
    }
}
