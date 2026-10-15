namespace Dnf.Game
{
    /// <summary>
    /// 伤害计算专用的伪随机数(证据 E-049):与 C 库的 rand_r(glibc 实现)是同一个公式。
    /// 种子推进三次线性同余 x = x * 0x41C64E6D + 0x3039,结果由三次高位片段拼成 31 位非负整数。
    /// 客户端(0x00E50ED0)与服务端(0x0808BD3F)的实现一致,所以同一个种子两边算出同一个数——服务端复算伤害靠的就是这一点。
    /// </summary>
    public static class SdcRandom
    {
        private const uint Multiplier = 0x41C64E6D;
        private const uint Increment = 0x3039;

        /// <summary>取下一个随机数并推进种子。返回值落在 [0, 2^31)。</summary>
        public static int Next(ref uint seed)
        {
            unchecked
            {
                uint x1 = seed * Multiplier + Increment;
                uint x2 = x1 * Multiplier + Increment;
                uint x3 = x2 * Multiplier + Increment;
                seed = x3;
                uint high = (((x1 >> 16) & 0x7FF) << 10) ^ ((x2 >> 16) & 0x3FF);
                uint result = (high << 10) ^ ((x3 >> 16) & 0x3FF);
                return (int)result;
            }
        }
    }
}
