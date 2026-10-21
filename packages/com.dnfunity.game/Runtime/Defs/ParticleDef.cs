using System;
using Dnf.Data;

// 证据:E-103(.ptl 的标签、枚举字符串与字段位置取自客户端的 .ptl 加载函数;飞行的运行时语义是推断,见 E-103 的未决项)。
namespace Dnf.Game
{
    /// <summary>一个轴上的运动方式([x axis]、[y axis]、[z axis] 之后的 [move type])。取值顺序是客户端枚举的编号。</summary>
    public enum ParticleMoveType
    {
        Stop = 0,
        StraightLine = 1,
        Sin = 2,
        Cos = 3,
    }

    /// <summary>[landing type](落地后的处理)。取值顺序是客户端枚举的编号;读不出时按 <see cref="Pierce"/>。</summary>
    public enum ParticleLanding
    {
        Pierce = 0,
        Slide = 1,
        Stop = 2,
        Vanish = 3,
    }

    /// <summary>[changing type](变化到头之后怎么办)。取值顺序是客户端枚举的编号。</summary>
    public enum ParticleChanging
    {
        KeepChanging = 0,
        KeepLastValue = 1,
        PingPong = 2,
    }

    /// <summary>
    /// 一个轴上的运动:[move type] 与两对 [move variable](各两个整数)。直线运动里,第一个 [move variable 1] 的两个数是速度(像素/秒,推断;写成相同的两个数时没有歧义)。
    /// </summary>
    public readonly struct ParticleAxis
    {
        public ParticleMoveType Type { get; }
        public int Var1A { get; }
        public int Var1B { get; }
        public int Var2A { get; }
        public int Var2B { get; }
        public ParticleChanging Changing { get; }
        public int ChangingTerm { get; }

        public ParticleAxis(ParticleMoveType type, int var1A, int var1B, int var2A, int var2B, ParticleChanging changing, int changingTerm)
        {
            Type = type;
            Var1A = var1A;
            Var1B = var1B;
            Var2A = var2A;
            Var2B = var2B;
            Changing = changing;
            ChangingTerm = changingTerm;
        }

        /// <summary>直线运动的速度(像素/秒):两个数相同就取它,不同取第一个(推断:两个数是起止速度或取值范围,单个怪物的投掷里都是相同的)。</summary>
        public double StraightSpeed => Type == ParticleMoveType.StraightLine ? Var1A : 0.0;
    }

    /// <summary>
    /// 一个 .ptl 粒子定义里"怎么飞"的部分(被动对象创建时带上它,放出来的东西按它运动)。证据 E-103。
    /// 只读三个轴的运动、[life time]、[landing type]、[gravity] 与 [creation pos];[object] 块(粒子发射器的子对象)与其余标签不解释。
    /// 标签顺序有意义:[x axis]、[y axis]、[z axis] 把后面的 [move type]、[move variable 1/2]、[changing type]、[changing term] 归到那个轴上(没写轴时是 x)。
    /// </summary>
    public sealed class ParticleDef
    {
        private readonly ParticleAxis[] _axes;

        public string Path { get; }

        /// <summary>[life time] 的整数;没写为 -1。单位按毫秒用(推断)。</summary>
        public int LifeTimeMs { get; }

        public ParticleLanding Landing { get; }
        public int Gravity { get; }

        public ParticleDef(string path, ParticleAxis x, ParticleAxis y, ParticleAxis z, int lifeTimeMs, ParticleLanding landing, int gravity)
        {
            Path = path;
            _axes = new[] { x, y, z };
            LifeTimeMs = lifeTimeMs;
            Landing = landing;
            Gravity = gravity;
        }

        public ParticleAxis X => _axes[0];
        public ParticleAxis Y => _axes[1];
        public ParticleAxis Z => _axes[2];

        /// <summary>只在 x 轴上做直线运动(y 轴、z 轴都不动):本实现能照原样飞的那一类。</summary>
        public bool IsPlainHorizontalShot => X.Type == ParticleMoveType.StraightLine && Y.Type == ParticleMoveType.Stop && Z.Type == ParticleMoveType.Stop;

        public static ParticleDef Parse(string path, ScriptLine[] lines)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }
            var type = new ParticleMoveType[3];
            var v1a = new int[3];
            var v1b = new int[3];
            var v2a = new int[3];
            var v2b = new int[3];
            var changing = new ParticleChanging[3];
            var term = new int[3];
            int axis = 0;
            int life = -1, gravity = 0;
            ParticleLanding landing = ParticleLanding.Pierce;
            int depth = 0;
            foreach (ScriptLine line in lines)
            {
                if (line.Tag == null)
                {
                    continue;
                }
                if (line.TagIs("[object]"))
                {
                    depth++;
                    continue;
                }
                if (line.TagIs("[/object]"))
                {
                    if (depth > 0)
                    {
                        depth--;
                    }
                    continue;
                }
                if (depth > 0)
                {
                    continue;
                }
                switch (line.Tag.ToLowerInvariant())
                {
                    case "[x axis]":
                        axis = 0;
                        break;
                    case "[y axis]":
                        axis = 1;
                        break;
                    case "[z axis]":
                        axis = 2;
                        break;
                    case "[move type]":
                        switch (line.FirstString()?.ToLowerInvariant())
                        {
                            case "[stop]":
                                type[axis] = ParticleMoveType.Stop;
                                break;
                            case "[straight line]":
                                type[axis] = ParticleMoveType.StraightLine;
                                break;
                            case "[sin]":
                                type[axis] = ParticleMoveType.Sin;
                                break;
                            case "[cos]":
                                type[axis] = ParticleMoveType.Cos;
                                break;
                        }
                        break;
                    case "[move variable 1]":
                        ReadPair(line, out v1a[axis], out v1b[axis]);
                        break;
                    case "[move variable 2]":
                        ReadPair(line, out v2a[axis], out v2b[axis]);
                        break;
                    case "[changing type]":
                        switch (line.FirstString()?.ToLowerInvariant())
                        {
                            case "[keep changing]":
                                changing[axis] = ParticleChanging.KeepChanging;
                                break;
                            case "[keep last value]":
                                changing[axis] = ParticleChanging.KeepLastValue;
                                break;
                            case "[ping pong]":
                                changing[axis] = ParticleChanging.PingPong;
                                break;
                        }
                        break;
                    case "[changing term]":
                        term[axis] = FirstInt(line, 0);
                        break;
                    case "[life time]":
                        life = FirstInt(line, -1);
                        break;
                    case "[gravity]":
                        gravity = FirstInt(line, 0);
                        break;
                    case "[landing type]":
                        switch (line.FirstString()?.ToLowerInvariant())
                        {
                            case "[pierce]":
                                landing = ParticleLanding.Pierce;
                                break;
                            case "[slide]":
                                landing = ParticleLanding.Slide;
                                break;
                            case "[stop]":
                                landing = ParticleLanding.Stop;
                                break;
                            case "[vanish]":
                                landing = ParticleLanding.Vanish;
                                break;
                        }
                        break;
                }
            }
            var axes = new ParticleAxis[3];
            for (int i = 0; i < 3; i++)
            {
                axes[i] = new ParticleAxis(type[i], v1a[i], v1b[i], v2a[i], v2b[i], changing[i], term[i]);
            }
            return new ParticleDef(path, axes[0], axes[1], axes[2], life, landing, gravity);
        }

        private static void ReadPair(ScriptLine line, out int a, out int b)
        {
            a = 0;
            b = 0;
            int n = 0;
            foreach (ScriptValue v in line.Values)
            {
                if (!v.IsNumber)
                {
                    continue;
                }
                if (n == 0)
                {
                    a = v.Int;
                }
                else if (n == 1)
                {
                    b = v.Int;
                }
                n++;
            }
        }

        private static int FirstInt(ScriptLine line, int fallback)
        {
            foreach (ScriptValue v in line.Values)
            {
                if (v.IsNumber)
                {
                    return v.Int;
                }
            }
            return fallback;
        }
    }
}
