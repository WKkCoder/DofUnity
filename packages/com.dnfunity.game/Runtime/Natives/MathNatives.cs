using System;

namespace Dnf.Game
{
    /// <summary>数学与插值类的原生接口(不需要对象)。</summary>
    public sealed partial class GameWorld
    {
        /// <summary>脚本随机数的默认种子:固定,所以同样的输入总得到同样的结果(不是原版的做法,原版的随机序列没有观察)。</summary>
        public const int DefaultRandomSeed = 12345;

        private Random _random = new Random(DefaultRandomSeed);
        private readonly System.Collections.Generic.HashSet<int> _uniqueUsed = new System.Collections.Generic.HashSet<int>();

        /// <summary>脚本使用的随机数发生器(种子固定,测试里行为可重复)。</summary>
        public Random ScriptRandom => _random;

        /// <summary>把脚本随机数序列重置到起点(并清空"不重复随机数"的已用记录)。逐技能扫描每个技能开始前调用,结果就不依赖前面跑过哪些技能。</summary>
        public void ResetScriptRandom(int seed = DefaultRandomSeed)
        {
            _random = new Random(seed);
            _uniqueUsed.Clear();
        }

        private void RegisterMathNatives()
        {
            const NativeStatus I = NativeStatus.Inferred;
            const NativeStatus V = NativeStatus.Verified;

            DefineFree("sq_Abs", V, "绝对值(参数是浮点时返回浮点)", c =>
                c.TypeOf(0) == Dnf.Squirrel.SqType.Float ? c.Return(Math.Abs(c.Float(0))) : c.Return(Math.Abs(c.Int(0))));
            DefineFree("sq_getRandom", I, "随机整数 [下限, 上限](含上限,依据 sq_getRandom(0,1) 用作二选一与 (最小元素, 最大元素) 的用法)", c =>
            {
                long lo = c.Int(0);
                long hi = c.Int(1);
                if (hi < lo)
                {
                    long tmp = lo;
                    lo = hi;
                    hi = tmp;
                }
                return c.Return(lo + (long)(_random.NextDouble() * (hi - lo + 1)));
            });
            DefineAny("sq_GetDistancePos", I, "从 x 沿朝向前进 distance:向左减、向右加、其余不变 (x, 朝向, 距离)", c =>
            {
                int x = c.Int32(0);
                int dir = c.Int32(1);
                int d = c.Int32(2);
                return c.Return(dir == Direction.Left ? x - d : dir == Direction.Right ? x + d : x);
            });
            DefineFree("sq_GetShuttleValue", I,
                "在 [from, to] 之间往返的三角波 (from, to, 当前毫秒, 周期毫秒):周期的前半程由 from 到 to,后半程回来。形状是推断,未验证", c =>
                {
                    long from = c.Int(0);
                    long to = c.Int(1);
                    long t = c.Int(2);
                    long period = c.Int(3);
                    if (period <= 0)
                    {
                        return c.Return(from);
                    }
                    double phase = (double)(((t % period) + period) % period) / period;
                    double k = phase < 0.5 ? phase * 2 : 2 - phase * 2;
                    return c.Return((long)Math.Round(from + (to - from) * k));
                });
            DefineFree("sq_GetUniformVelocity", I, "匀速插值:从 from 到 to,经过 t / T (from, to, t, T);t 超出范围时取端点", c =>
            {
                double k = Ratio(c.Int(2), c.Int(3));
                return c.Return((long)Math.Round(c.Int(0) + (c.Int(1) - c.Int(0)) * k));
            });
            DefineFree("sq_GetAccel", I,
                "加速插值 (from, to, t, T, 加速):第 5 个参数为真取先慢后快(二次),为假取先快后慢。曲线形状是推断,未验证", c =>
                {
                    double k = Ratio(c.Int(2), c.Int(3));
                    k = c.BoolOr(4, true) ? k * k : 1 - (1 - k) * (1 - k);
                    return c.Return((long)Math.Round(c.Int(0) + (c.Int(1) - c.Int(0)) * k));
                });
            DefineFree("sq_Sin", I, "正弦(参数是弧度)", c => c.Return((float)Math.Sin(c.Float(0))));
            DefineFree("sq_Cos", I, "余弦(参数是弧度)", c => c.Return((float)Math.Cos(c.Float(0))));
            DefineFree("sq_SinTable", I, "正弦(参数是整数度,查表;这里直接计算)", c => c.Return((float)Math.Sin(c.Int(0) * Math.PI / 180.0)));
            DefineFree("sq_CosTable", I, "余弦(参数是整数度,查表;这里直接计算)", c => c.Return((float)Math.Cos(c.Int(0) * Math.PI / 180.0)));
            DefineFree("sq_Atan2", I, "反正切 (y, x),结果是弧度", c => c.Return((float)Math.Atan2(c.Float(0), c.Float(1))));
            DefineFree("sq_ToDegree", I, "弧度转角度", c => c.Return((float)(c.Float(0) * 180.0 / Math.PI)));
            DefineFree("sq_GetDistance", I, "两点距离 (x1, y1, x2, y2[, 标志]):直线距离,取整;最后一个标志的含义未知,忽略", c =>
            {
                double dx = c.Float(0) - c.Float(2);
                double dy = c.Float(1) - c.Float(3);
                return c.Return((int)Math.Sqrt(dx * dx + dy * dy));
            });
            DefineFree("sq_getRandomUnique", I,
                "不重复的随机整数 (是否重新开始, 下限, 上限):范围含两端;取尽后重新开始(依据 sq_getRandomUnique(false, 0, i % len) 的用法推断上限含在内)", c =>
                {
                    if (c.BoolOr(0, false))
                    {
                        _uniqueUsed.Clear();
                    }
                    int lo = c.Int32(1);
                    int hi = Math.Max(lo, c.Int32(2));
                    if (_uniqueUsed.Count >= hi - lo + 1)
                    {
                        _uniqueUsed.Clear();
                    }
                    for (int guard = 0; guard < 1000; guard++)
                    {
                        int v = lo + _random.Next(hi - lo + 1);
                        if (_uniqueUsed.Add(v))
                        {
                            return c.Return(v);
                        }
                    }
                    return c.Return(lo);
                });
            DefineFree("sq_RGB", NativeStatus.Trace, "颜色:打包成 (r<<16|g<<8|b);格式未验证,只用来原样传给别的接口", c =>
                c.Return(((c.Int(0) & 255) << 16) | ((c.Int(1) & 255) << 8) | (c.Int(2) & 255)));
            DefineFree("sq_ALPHA", NativeStatus.Trace, "颜色的透明度:sq_ALPHA(a) 或 sq_ALPHA(r, g, b, a),格式未验证", c =>
                c.ArgCount >= 4
                    ? c.Return(((c.Int(3) & 255) << 24) | ((c.Int(0) & 255) << 16) | ((c.Int(1) & 255) << 8) | (c.Int(2) & 255))
                    : c.Return((c.Int(0) & 255) << 24));
            DefineFree("printc", NativeStatus.Trace, "脚本的调试输出:丢弃", c => c.ReturnNothing());
            DefineFree("isSameObject", I, "两个脚本对象是不是同一个宿主对象", c =>
            {
                NativeObject a = c.Arg<NativeObject>(0);
                NativeObject b = c.Arg<NativeObject>(1);
                return c.Return(a != null && ReferenceEquals(a, b));
            });
        }

        private static double Ratio(long t, long total)
        {
            if (total <= 0)
            {
                return 1;
            }
            double k = (double)t / total;
            return k < 0 ? 0 : k > 1 ? 1 : k;
        }
    }
}
