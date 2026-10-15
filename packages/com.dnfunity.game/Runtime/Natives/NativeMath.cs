using System;

namespace Dnf.Game
{
    /// <summary>
    /// 原生接口里不依赖世界状态的数学函数,按客户端的实现逐步写成(E-046)。
    /// 单独成静态类,是为了可以脱离脚本直接测试。
    /// 共同的特点(都是从客户端汇编读出来的):参数是 32 位整数;插值先转成<b>单精度浮点</b>、每一步都按单精度(SSE 指令)算,
    /// 最后<b>向零截断</b>成整数(不是四舍五入)。所以 25.0 附近的结果可能是 24(单精度误差落在 24.99999 一侧),这是客户端本来的样子。
    /// 二次曲线那个函数的最后几步走的是 x87,按双精度算(客户端把 x87 精度控制在 53 位是 MSVC 的默认,推断)。
    /// </summary>
    public static class NativeMath
    {
        /// <summary>
        /// 脚本里 <c>sq_GetDistance(x1, y1, x2, y2, false)</c> 用的纵向缩放:客户端把纵向差除以"一个全局值 × 0.01",
        /// 全局值在文件里的初值是 29(0x01A39E60)。运行时是否改写没有观察;游戏脚本里 4 处调用都传 true(不缩放),所以这条路径没有被用到。
        /// </summary>
        public const int DepthScalePercent = 29;

        /// <summary>
        /// 匀速插值(客户端 0x011B98C0):经过的时间 <paramref name="t"/> 不到 1 取起点,够了 <paramref name="total"/> 取终点,
        /// 中间按 <c>from + (to - from) * t / total</c> 线性插值。结果向零截断。
        /// </summary>
        public static int Uniform(int from, int to, int t, int total)
        {
            float start = from;
            float end = to;
            if (t < total)
            {
                if (t < 1)
                {
                    return (int)start;
                }
                end = Lerp(start, end, t, total);
            }
            return (int)end;
        }

        /// <summary>(end - start) * t / total + start,每一步都按单精度算(客户端的 SSE 序列)。</summary>
        private static float Lerp(float start, float end, int t, int total)
        {
            float span = end - start;
            float scaled = span * (float)t;
            float ratio = scaled / (float)total;
            return ratio + start;
        }

        /// <summary>
        /// 往返插值(客户端 0x011B9960):<paramref name="total"/> 是<b>单程</b>用的时间,不是整个来回的周期。
        /// 时间对 2×total 取余:前半程 from 到 to 匀速,后半程 to 回到 from 匀速。
        /// </summary>
        public static int Shuttle(int from, int to, int t, int total)
        {
            if (total == 0)
            {
                return from; // 客户端这里会除零崩溃;脚本不会传 0
            }
            t %= total * 2; // C 的取余:余数的符号跟随被除数,时间为负时得到负数,下面按"不到 1"取起点
            float start;
            float end;
            if (t < total)
            {
                start = from;
                end = to;
                if (t < 1)
                {
                    return (int)start;
                }
            }
            else
            {
                t -= total;
                start = to;
                end = from;
                if (total <= t)
                {
                    return (int)end;
                }
                if (t < 1)
                {
                    return (int)start;
                }
            }
            end = Lerp(start, end, t, total);
            return (int)end;
        }

        /// <summary>
        /// 二次曲线插值(客户端 0x011B9A50 与它的包装 0x011B9EF0)。
        /// <paramref name="easeOut"/> 为真:先快后慢(速度从 2×平均降到 0),位置 = from + (to - from) × (2u - u²),u = t / total;
        /// 为假:先慢后快(从 0 加速),位置 = from + (to - from) × u²。
        /// 跳起上升用真、下落用假,与脚本里的用法一致(上升减速、下落加速)。结果向零截断。
        /// </summary>
        public static int Accel(int from, int to, int t, int total, bool easeOut)
        {
            if (total <= t)
            {
                return (int)(float)to;
            }
            if (t < 1)
            {
                return (int)(float)from;
            }
            float start = from;
            float end = to;
            int elapsed = t;
            if (!easeOut)
            {
                // 反向走一遍:从终点往起点,用剩余时间;下面的公式给出的是离"出发点"的距离
                elapsed = total - t;
                start = to;
                end = from;
            }
            // 前半段是单精度(SSE),a = |end - start| * 2 / total,b = -(a / total),都存成单精度;
            // 后半段是 x87:travelled = (b * 0.5 * elapsed + a) * elapsed,即 |end - start| × (2u - u²),u = elapsed / total
            float span = end - start;
            float twice = Math.Abs(span) * 2.0f;
            float a = twice / (float)total;
            float perStep = a / (float)total;
            float b = -perStep;
            double travelled = ((double)b * 0.5 * elapsed + a) * elapsed;
            double result = start < end ? travelled + start : start - travelled;
            return (int)result;
        }

        /// <summary>
        /// 两点的平面距离(客户端 0x007FB7C0):纵向差先除以缩放(<paramref name="flat"/> 为真时缩放为 1,否则为 <see cref="DepthScalePercent"/>%)
        /// 并截断成整数,再与横向差一起开方,结果向零截断。
        /// </summary>
        public static int Distance(int x1, int y1, int x2, int y2, bool flat)
        {
            float scale = flat ? 1.0f : (float)DepthScalePercent * 0.01f;
            int dy = (int)((float)(y1 - y2) / scale);
            int dx = x1 - x2;
            return (int)Math.Sqrt((double)(dy * dy + dx * dx));
        }

        /// <summary>
        /// 整数度的正弦/余弦查表的下标(客户端 0x011E46D0、0x011E4710):度数对 360 取"非负余数"。
        /// </summary>
        public static int DegreeIndex(int degrees)
        {
            return ((1 - degrees / 360) * 360 + degrees) % 360;
        }

        /// <summary>绝对值(客户端 0x005AF5A0):只处理 32 位整数;最小的负数还是它自己。</summary>
        public static int Abs(int v)
        {
            int s = v >> 31;
            return unchecked((v ^ s) - s);
        }

        /// <summary>
        /// 颜色(客户端 0x005ADAE0):打包成 0xFFBBGGRR(不透明,蓝在高位)。
        /// </summary>
        public static int Rgb(int r, int g, int b)
        {
            return unchecked((int)(0xFF000000u | ((uint)(b & 0xFF) << 16) | ((uint)(g & 0xFF) << 8) | (uint)(r & 0xFF)));
        }

        /// <summary>
        /// 透明度(客户端 0x005ADB00):打包成 0xAAFFFFFF,颜色部分是白色。
        /// </summary>
        public static int Alpha(int a)
        {
            return unchecked((int)(((uint)(a & 0xFF) << 24) | 0x00FFFFFFu));
        }
    }
}
