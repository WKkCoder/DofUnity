namespace Dnf.Play
{
    /// <summary>
    /// 命中停顿(证据 E-077,静态读码,没有运行时验证):一次命中让攻击方与受击方各自"定住"同样多的毫秒。
    /// 客户端的做法:攻击方在 onAttack 里、受击方在 onDamage 里,各自调"暂停"(类型 1,时长取攻击信息的 +0x108,即 [hit info] 的整数);
    /// 对象已经被同类暂停时,剩余时间比新时长短才改,所以是"长者胜",不叠加。暂停期间对象当前的动画、
    /// 附属动画与子动画都停住(逐个冻结)。
    /// 这里的毫秒数怎么来:[hit info] 的整数不是 -1 就用它;是 -1(或根本没写 [hit info])就取角色 .chr [weapon hit info] 里
    /// 这件武器那一项的整数;文件里写了 [is use weapon hit delay rate] 时改为"武器的整数 × 文件里的整数";
    /// 最后大于 0 的结果再乘属性组里的一个系数 (2 - 属性),属性默认 1(属性组复位时写 1.0),所以默认不变。
    /// </summary>
    public static class HitStop
    {
        /// <summary>那个属性的默认值(属性组 Reset 写入 1.0;装备、增益怎样改它没有读)。</summary>
        public const float DefaultStat = 1.0f;

        /// <summary>
        /// 一次命中的停顿毫秒数。<paramref name="hitValue"/> 是攻击信息 [hit info] 的整数(没写是 -1),
        /// <paramref name="weaponStunMs"/> 是持武器那一项的停顿(没有武器表时传 0)。结果 ≤ 0 表示不停。
        /// 复现客户端的整数与单精度浮点运算:先取默认、再按标志相乘(整数相乘)、最后 (int)((2 - stat) * 毫秒)(向零取整)。
        /// </summary>
        public static int StunMs(int hitValue, bool useWeaponHitDelayRate, int weaponStunMs, float stat = DefaultStat)
        {
            int stun = hitValue;
            if (stun == -1)
            {
                stun = weaponStunMs;
            }
            if (useWeaponHitDelayRate)
            {
                stun = unchecked(weaponStunMs * hitValue);
            }
            if (stun > 0)
            {
                stun = (int)((2.0f - stat) * (float)stun);
            }
            return stun;
        }
    }
}
