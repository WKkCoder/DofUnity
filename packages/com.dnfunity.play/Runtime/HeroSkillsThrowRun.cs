namespace Dnf.Play
{
    /// <summary>
    /// 投掷类技能一次施放的阶段机(客户端状态 0x0d 一族的基类逐帧块 0x0086916b 与换阶段函数 0x00687480):纯计时,不碰主角与房间。
    /// 阶段 0 → 阶段 1 → 结束:阶段 0 的状态计时到了阶段 0 的时长换阶段 1,阶段 1 的状态计时到了阶段 1 的时长就结束(回站立);
    /// 每次换阶段状态计时清零,不把多出来的时间带进下一阶段;时长为 -1 表示不自动换(客户端用 -1 当"永不")。
    /// 霸体只在阶段 0(进入块 setSuperArmor(1,1,-1)),换阶段 1 时关掉(setSuperArmor(0,1,-1))。
    /// </summary>
    public sealed class ThrowRun
    {
        /// <summary>施法速度的基准(千分比,1000 即不加速;客户端 +0x3194,和 E-135 的施法速度同一个字段,本仓库没有这个属性所以固定取基准)。</summary>
        public const int DefaultCastSpeed = 1000;

        /// <summary>阶段 0 的时长折算后是 0 时,客户端改成 70 毫秒(基类进入块 0x00863369 一带的兜底)。</summary>
        public const int ZeroChargeFallbackMs = 70;

        public ThrowRun(int chargeRawMs, int shootRawMs, bool armorInCharge, int castSpeedPermille = DefaultCastSpeed)
        {
            int speed = castSpeedPermille > 0 ? castSpeedPermille : DefaultCastSpeed;
            ChargeMs = Scaled(chargeRawMs, speed);
            if (ChargeMs == 0)
            {
                ChargeMs = ZeroChargeFallbackMs;
            }
            ShootMs = Scaled(shootRawMs, speed);
            ArmorInCharge = armorInCharge;
        }

        /// <summary>折算后阶段 0 的时长(毫秒);-1 不自动换阶段。</summary>
        public int ChargeMs { get; }

        /// <summary>折算后阶段 1 的时长(毫秒);-1 不自动结束。</summary>
        public int ShootMs { get; }

        /// <summary>这个技能在阶段 0 里有霸体。</summary>
        public bool ArmorInCharge { get; }

        /// <summary>当前阶段:0、1;2 表示已结束。</summary>
        public int Phase { get; private set; }

        /// <summary>已结束(阶段 1 的时间到了)。</summary>
        public bool Finished => Phase >= 2;

        /// <summary>当前阶段已经过的毫秒数(换阶段清零)。</summary>
        public double PhaseMs { get; private set; }

        /// <summary>此刻霸体是否生效:有霸体的技能,只在阶段 0。</summary>
        public bool ArmorActive => ArmorInCharge && Phase == 0;

        /// <summary>
        /// 前进 <paramref name="deltaMs"/> 毫秒(不为正的数不动)。一次最多换一个阶段(客户端每帧只判一次),
        /// 超出的部分不带进下一阶段。
        /// </summary>
        public void Advance(double deltaMs)
        {
            if (Finished || !(deltaMs > 0))
            {
                return;
            }
            PhaseMs += deltaMs;
            if (Phase == 0 && ChargeMs >= 0 && PhaseMs >= ChargeMs)
            {
                Phase = 1;
                PhaseMs = 0;
            }
            else if (Phase == 1 && ShootMs >= 0 && PhaseMs >= ShootMs)
            {
                Phase = 2;
                PhaseMs = 0;
            }
        }

        /// <summary>毫秒数按施法速度折算:乘 1000 除以速度,整数除法截断;负数(-1)原样保留。</summary>
        private static int Scaled(int ms, int speed)
        {
            return ms < 0 ? -1 : (int)((long)ms * 1000 / speed);
        }
    }
}
