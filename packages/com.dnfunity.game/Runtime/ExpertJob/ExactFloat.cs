using System;
using System.Numerics;

// 服务端静态读码:分解产物个数的算式在 32 位 x87 上做(<c>FILD</c> 整数、<c>FMUL</c>/<c>FDIV</c> 单精度、<c>FISTP</c> 前把舍入方式改成"向零",
// 例如 get_disjoint_result_ 0x085d234a 的 0x085d246c 到 0x085d2496 与 0x085d250d 到 0x085d253d)。
// x87 的扩展精度有 64 位尾数,"32 位整数 × 24 位单精度"的乘积在里面是精确的;这里用大整数把同样的运算当有理数精确地算出来再向零取整,
// 不依赖平台的浮点宽度。整数不定值(超出 32 位、不是数、除以零)与 <c>FISTP</c> 一致,给 <see cref="int.MinValue"/>。
namespace Dnf.Game
{
    internal static class ExactFloat
    {
        /// <summary>向零取整(<c>a × f</c>)。</summary>
        public static int TruncMul(long a, float f)
        {
            return TruncMulDiv(a, f, 1.0f);
        }

        /// <summary>向零取整(<c>a × f ÷ d</c>);<paramref name="f"/> 与 <paramref name="d"/> 按单精度的精确值算。</summary>
        public static int TruncMulDiv(long a, float f, float d)
        {
            if (!Decompose(f, out BigInteger mf, out int xf) || !Decompose(d, out BigInteger md, out int xd) || md.IsZero)
            {
                return int.MinValue;
            }
            BigInteger numerator = new BigInteger(a) * mf;
            BigInteger denominator = md;
            int shift = xf - xd;
            if (shift >= 0)
            {
                numerator <<= shift;
            }
            else
            {
                denominator <<= -shift;
            }
            // BigInteger.Divide 本身向零取整(分子分母任何一个为负都一样),不需要先把分母的符号挪到分子上。
            return Narrow(BigInteger.Divide(numerator, denominator));
        }

        /// <summary>向零取整(<c>a + f</c>)(<c>FILD</c> 一个整数再 <c>FADD</c> 一个单精度)。</summary>
        public static int TruncAdd(long a, float f)
        {
            if (!Decompose(f, out BigInteger mf, out int xf))
            {
                return int.MinValue;
            }
            if (xf >= 0)
            {
                return Narrow(new BigInteger(a) + (mf << xf));
            }
            BigInteger numerator = (new BigInteger(a) << -xf) + mf;
            return Narrow(BigInteger.Divide(numerator, BigInteger.One << -xf));
        }

        /// <summary>单精度 → 有符号尾数与二进制指数(值 = 尾数 × 2^指数);无穷与非数返回 false。</summary>
        private static bool Decompose(float value, out BigInteger mantissa, out int exponent)
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            int biased = (bits >> 23) & 0xFF;
            long fraction = bits & 0x7FFFFF;
            if (biased == 0xFF)
            {
                mantissa = BigInteger.Zero;
                exponent = 0;
                return false;
            }
            if (biased == 0)
            {
                exponent = -149;
            }
            else
            {
                fraction |= 0x800000;
                exponent = biased - 150;
            }
            mantissa = new BigInteger(bits < 0 ? -fraction : fraction);
            return true;
        }

        private static int Narrow(BigInteger v)
        {
            if (v < int.MinValue || v > int.MaxValue)
            {
                return int.MinValue;
            }
            return (int)v;
        }
    }
}
