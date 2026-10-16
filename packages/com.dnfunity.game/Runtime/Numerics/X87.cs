using System;
using System.Numerics;

namespace Dnf.Game
{
    /// <summary>
    /// x87 扩展精度运算的精确模型(证据 E-062;与 tools/py/x87.py 逐运算一致)。
    /// 原版服务端是 32 位 x86 程序,浮点走 x87:一条 C 语句的中间结果留在 80 位寄存器里(64 位有效位),
    /// 只有 FSTP 落到 float/double 时才舍入,FISTP 取整时舍入控制被临时改成截断。用 double 复现会在"整数边界附近"差 1。
    /// 这里用"有符号整数尾数 × 2 的指数次方"精确表示每个值,每次运算先得到精确结果(除法带粘滞位)再按 64 位有效位就近舍入(平局取偶)。
    /// 只处理有限值与正常范围(能力数值函数的输入都是整数与小浮点,不会出现无穷、非数或次正规数)。
    /// </summary>
    public readonly struct X87Value
    {
        /// <summary>有符号尾数;值 = Mantissa × 2^Exponent。</summary>
        public BigInteger Mantissa { get; }

        public int Exponent { get; }

        public X87Value(BigInteger mantissa, int exponent)
        {
            Mantissa = mantissa;
            Exponent = exponent;
        }

        public bool IsZero => Mantissa.IsZero;
    }

    /// <summary>x87 运算:载入(FILD、FLD)、算术(FADD、FMUL、FDIV)、落地(FSTP float)、取整(FISTP,截断)。</summary>
    public static class X87
    {
        /// <summary>扩展精度的有效位数。</summary>
        public const int ExtendedBits = 64;

        /// <summary>单精度(float)的有效位数。</summary>
        public const int SingleBits = 24;

        /// <summary>FILD:32 位整数精确载入。</summary>
        public static X87Value FromInt(int value)
        {
            return new X87Value(new BigInteger(value), 0);
        }

        /// <summary>FLD float:float 精确载入。无穷与非数不支持。</summary>
        public static X87Value FromSingle(float value)
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            int exp = (bits >> 23) & 0xFF;
            int frac = bits & 0x7FFFFF;
            if (exp == 0xFF)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "x87 模型不处理无穷与非数");
            }
            long mantissa;
            int exponent;
            if (exp == 0)
            {
                mantissa = frac;
                exponent = -149;
            }
            else
            {
                mantissa = frac | 0x800000;
                exponent = exp - 150;
            }
            if (bits < 0)
            {
                mantissa = -mantissa;
            }
            return new X87Value(new BigInteger(mantissa), exponent);
        }

        /// <summary>FADDP:精确相加后按 64 位有效位舍入。</summary>
        public static X87Value Add(X87Value a, X87Value b)
        {
            if (a.IsZero)
            {
                return b;
            }
            if (b.IsZero)
            {
                return a;
            }
            int e = Math.Min(a.Exponent, b.Exponent);
            BigInteger m = (a.Mantissa << (a.Exponent - e)) + (b.Mantissa << (b.Exponent - e));
            return Round(m, e, false, ExtendedBits);
        }

        /// <summary>FMULP:精确相乘后按 64 位有效位舍入。</summary>
        public static X87Value Mul(X87Value a, X87Value b)
        {
            return Round(a.Mantissa * b.Mantissa, a.Exponent + b.Exponent, false, ExtendedBits);
        }

        /// <summary>FDIVP:a / b,按 64 位有效位舍入。除数为 0 不支持。</summary>
        public static X87Value Div(X87Value a, X87Value b)
        {
            if (b.IsZero)
            {
                throw new DivideByZeroException();
            }
            if (a.IsZero)
            {
                return a;
            }
            BigInteger na = BigInteger.Abs(a.Mantissa);
            BigInteger nb = BigInteger.Abs(b.Mantissa);
            // 把被除数放大到商至少有 ExtendedBits + 2 位,余数是否为 0 作为粘滞位参与舍入
            int shift = ExtendedBits + 2 + BitLength(nb) - BitLength(na);
            if (shift < 0)
            {
                shift = 0;
            }
            BigInteger q = BigInteger.DivRem(na << shift, nb, out BigInteger rem);
            bool negative = (a.Mantissa.Sign < 0) != (b.Mantissa.Sign < 0);
            return Round(negative ? -q : q, a.Exponent - b.Exponent - shift, !rem.IsZero, ExtendedBits);
        }

        /// <summary>FSTP float:舍入到 24 位有效位(就近、平局取偶),返回对应的 float。</summary>
        public static float ToSingle(X87Value value)
        {
            if (value.IsZero)
            {
                return 0f;
            }
            X87Value r = Round(value.Mantissa, value.Exponent, false, SingleBits);
            bool negative = r.Mantissa.Sign < 0;
            long m = (long)BigInteger.Abs(r.Mantissa);
            int e = r.Exponent;
            // 规范到 2^23 <= m < 2^24:舍入进位可能得到 2^24(右移不丢位);尾数不足 24 位时左移补齐
            while (m < 0x800000)
            {
                m <<= 1;
                e--;
            }
            while (m >= 0x1000000)
            {
                m >>= 1;
                e++;
            }
            int biased = e + 23 + 127;
            if (biased >= 255)
            {
                return negative ? float.NegativeInfinity : float.PositiveInfinity;
            }
            if (biased <= 0)
            {
                // 次正规或下溢:能力数值用不到,按双精度近似处理
                double d = (double)m * Math.Pow(2.0, e);
                return (float)(negative ? -d : d);
            }
            int bits = (biased << 23) | (int)(m & 0x7FFFFF);
            if (negative)
            {
                bits |= unchecked((int)0x80000000);
            }
            return BitConverter.Int32BitsToSingle(bits);
        }

        /// <summary>
        /// FISTP(舍入控制临时设成截断):向零取整;超出 32 位有符号范围得 int.MinValue(整数不确定值)。
        /// </summary>
        public static int TruncateToInt(X87Value value)
        {
            if (value.IsZero)
            {
                return 0;
            }
            bool negative = value.Mantissa.Sign < 0;
            BigInteger a = BigInteger.Abs(value.Mantissa);
            BigInteger magnitude = value.Exponent >= 0 ? a << value.Exponent : a >> -value.Exponent;
            BigInteger signed = negative ? -magnitude : magnitude;
            if (signed > int.MaxValue || signed < int.MinValue)
            {
                return int.MinValue;
            }
            return (int)signed;
        }

        /// <summary>把"尾数 × 2^指数(再加上不为零的粘滞位)"按 bits 位有效位就近舍入(平局取偶)。</summary>
        private static X87Value Round(BigInteger mantissa, int exponent, bool sticky, int bits)
        {
            if (mantissa.IsZero)
            {
                return new X87Value(BigInteger.Zero, 0);
            }
            bool negative = mantissa.Sign < 0;
            BigInteger a = BigInteger.Abs(mantissa);
            int length = BitLength(a);
            if (length > bits)
            {
                int drop = length - bits;
                BigInteger q = a >> drop;
                BigInteger rem = a - (q << drop);
                BigInteger half = BigInteger.One << (drop - 1);
                int cmp = rem.CompareTo(half);
                bool up = cmp > 0 || (cmp == 0 && (sticky || !q.IsEven));
                if (up)
                {
                    q += BigInteger.One;
                }
                a = q;
                exponent += drop;
            }
            return new X87Value(negative ? -a : a, exponent);
        }

        /// <summary>正整数的二进制位数。</summary>
        private static int BitLength(BigInteger value)
        {
            byte[] bytes = value.ToByteArray();
            int i = bytes.Length - 1;
            while (i > 0 && bytes[i] == 0)
            {
                i--;
            }
            int bits = i * 8;
            int top = bytes[i];
            while (top != 0)
            {
                bits++;
                top >>= 1;
            }
            return bits;
        }
    }
}
