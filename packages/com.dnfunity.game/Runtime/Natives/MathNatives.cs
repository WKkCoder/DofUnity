using System;

namespace Dnf.Game
{
    /// <summary>数学与插值类的原生接口(不需要对象)。</summary>
    public sealed partial class GameWorld
    {
        /// <summary>脚本随机数的默认种子:固定,所以同样的输入总得到同样的结果(不是原版的做法,原版的随机序列没有观察)。</summary>
        public const int DefaultRandomSeed = 12345;

        private Random _random = new Random(DefaultRandomSeed);
        /// <summary>sq_getRandomUnique 的"剩余候选"数组(客户端的一个静态整数数组,0x01A4E74C;E-048)。</summary>
        private readonly System.Collections.Generic.List<int> _uniquePool = new System.Collections.Generic.List<int>();

        /// <summary>脚本使用的随机数发生器(种子固定,测试里行为可重复)。</summary>
        public Random ScriptRandom => _random;

        /// <summary>把脚本随机数序列重置到起点(并清空"不重复随机数"的剩余候选)。逐技能扫描每个技能开始前调用,结果就不依赖前面跑过哪些技能。</summary>
        public void ResetScriptRandom(int seed = DefaultRandomSeed)
        {
            _random = new Random(seed);
            _uniquePool.Clear();
        }

        private void RegisterMathNatives()
        {
            const NativeStatus I = NativeStatus.Inferred;
            const NativeStatus V = NativeStatus.Verified;

            DefineFree("sq_Abs", V, "绝对值:只处理整数,浮点参数先截断成整数,返回整数(客户端 0x005af5a0 是整数的位运算写法,E-046)", c =>
                c.Return(NativeMath.Abs(c.Int32(0))));
            DefineFree("sq_getRandom", V,
                "随机整数 [下限, 上限](含上限);上限不大于下限时直接返回下限,不交换(客户端 0x005adba0:下限 + 随机数 % (上限 - 下限 + 1);随机数是客户端自己的线性同余发生器,每次调用前还混入毫秒计时,没有固定序列,这里的序列不同,E-046)", c =>
            {
                int lo = c.Int32(0);
                int hi = c.Int32(1);
                if (hi <= lo)
                {
                    return c.Return(lo);
                }
                return c.Return(lo + (long)(_random.NextDouble() * ((long)hi - lo + 1)));
            });
            DefineAny("sq_GetDistancePos", NativeStatus.Verified,
                "从 x 沿朝向前进 distance (x, 朝向, 距离):朝向是向左(0)就减,其余一律加(包括向上、向下、居中;客户端 0x005adfa0 与 0x00bb1470 只比较是否为 0,E-046)", c =>
            {
                int x = c.Int32(0);
                int dir = c.Int32(1);
                int d = c.Int32(2);
                return c.Return(dir == Direction.Left ? x - d : x + d);
            });
            DefineFree("sq_GetShuttleValue", V,
                "往返插值 (from, to, 当前毫秒, 单程毫秒):第 4 个参数是单程用的时间,不是整个来回的周期;时间对 2×单程取余,前半程 from 到 to,后半程回来,结果向零截断(客户端 0x011b9960,E-046)", c =>
                c.Return(NativeMath.Shuttle(c.Int32(0), c.Int32(1), c.Int32(2), c.Int32(3))));
            DefineFree("sq_GetUniformVelocity", V,
                "匀速插值 (from, to, t, T):t 不到 1 取 from,t 够了 T 取 to,中间线性插值,单精度计算、向零截断(客户端 0x011b98c0,E-046)", c =>
                c.Return(NativeMath.Uniform(c.Int32(0), c.Int32(1), c.Int32(2), c.Int32(3))));
            DefineFree("sq_GetAccel", V,
                "二次曲线插值 (from, to, t, T, 先快后慢):第 5 个参数为真取先快后慢(from + (to-from)×(2u-u²)),为假取先慢后快(from + (to-from)×u²),u=t/T;跳起上升用真、下落用假。向零截断(客户端 0x011b9a50 与包装 0x011b9ef0,E-046)", c =>
                c.Return(NativeMath.Accel(c.Int32(0), c.Int32(1), c.Int32(2), c.Int32(3), c.BoolOr(4, false))));
            DefineFree("sq_Sin", I, "正弦(参数是弧度)", c => c.Return((float)Math.Sin(c.Float(0))));
            DefineFree("sq_Cos", I, "余弦(参数是弧度)", c => c.Return((float)Math.Cos(c.Float(0))));
            DefineFree("sq_SinTable", I,
                "正弦(参数是整数度,浮点参数先截断;度数对 360 取非负余数后查 360 项的单精度表,客户端 0x011e46d0,表的内容是运行时填的、没有读到,这里直接计算,E-046)", c =>
                c.Return((float)Math.Sin(NativeMath.DegreeIndex(c.Int32(0)) * Math.PI / 180.0)));
            DefineFree("sq_CosTable", I,
                "余弦(参数是整数度;同 sq_SinTable,客户端 0x011e4710,E-046)", c =>
                c.Return((float)Math.Cos(NativeMath.DegreeIndex(c.Int32(0)) * Math.PI / 180.0)));
            DefineFree("sq_Atan2", I, "反正切 (y, x),结果是弧度", c => c.Return((float)Math.Atan2(c.Float(0), c.Float(1))));
            DefineFree("sq_ToDegree", I, "弧度转角度", c => c.Return((float)(c.Float(0) * 180.0 / Math.PI)));
            DefineFree("sq_GetDistance", V,
                "两点距离 (x1, y1, x2, y2, 平面):纵向差先除以缩放再与横向差开方,结果向零截断。最后一个参数为真时纵向不缩放(脚本里的 4 处调用都传真);为假时纵向差除以 0.29(文件里的初值 29 × 0.01,运行时取值未观察)(客户端 0x007fb7c0,E-046)", c =>
                c.Return(NativeMath.Distance(c.Int32(0), c.Int32(1), c.Int32(2), c.Int32(3), c.BoolOr(4, true))));
            DefineFree("sq_getRandomUnique", V,
                "不重复的随机整数 (是否重新开始, 下限, 上限):客户端(0x005d71d0,E-048)有一个全局的“剩余候选”数组:下限为负直接返回 -1(什么也不动);第一个参数为真先清空数组;"
                + "数组是空的就按 [下限, 上限) 补满(上限不含);再从数组里随机取一项、把它删掉并返回。所以在数组没取空之前,下限和上限都不起作用;"
                + "数组补满后又为空(下限不小于上限)时客户端会除零崩溃,这里返回 -1。脚本里的用法是 sq_getRandomUnique(true, 0, 个数) 之后连续取下标。随机数本身不重现原版的序列", c =>
                {
                    int lo = c.Int32(1);
                    if (lo < 0)
                    {
                        return c.Return(-1);
                    }
                    if (c.BoolOr(0, false))
                    {
                        _uniquePool.Clear();
                    }
                    if (_uniquePool.Count == 0)
                    {
                        int hi = Math.Min(c.Int32(2), lo + (1 << 20));
                        for (int i = lo; i < hi; i++)
                        {
                            _uniquePool.Add(i);
                        }
                    }
                    if (_uniquePool.Count == 0)
                    {
                        return c.Return(-1);
                    }
                    int pick = _random.Next(_uniquePool.Count);
                    int value = _uniquePool[pick];
                    _uniquePool.RemoveAt(pick);
                    return c.Return(value);
                });
            DefineFree("sq_RGB", V,
                "颜色:sq_RGB(r, g, b) 打包成 0xFFBBGGRR(不透明,红在低位)(客户端 0x005adae0,E-046)", c =>
                c.Return(NativeMath.Rgb(c.Int32(0), c.Int32(1), c.Int32(2))));
            DefineFree("sq_ALPHA", V,
                "透明度:sq_ALPHA(a) 打包成 0xAAFFFFFF,颜色部分是白色;客户端只有 1 个参数(0x005adb00),脚本里有一处写成 4 个参数(po_aticeorbexprickle),多出的参数被忽略(E-046)", c =>
                c.Return(NativeMath.Alpha(c.Int32(0))));
            DefineFree("printc", V, "脚本的调试输出:什么也不做(客户端的实现 0x0115a430 只有一条返回指令,E-046)", c => c.ReturnNothing());
            DefineFree("isSameObject", V,
                "两个脚本对象是不是同一个宿主对象:客户端只比较两个指针是否相等(0x005af1d0),所以两个都是 null 时也为真(E-046)", c =>
            {
                if (c.IsNull(0) && c.IsNull(1))
                {
                    return c.Return(true);
                }
                NativeObject a = c.Arg<NativeObject>(0);
                NativeObject b = c.Arg<NativeObject>(1);
                return c.Return(a != null && ReferenceEquals(a, b));
            });
        }
    }
}
