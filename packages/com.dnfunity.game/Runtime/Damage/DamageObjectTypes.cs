namespace Dnf.Game
{
    /// <summary>
    /// 伤害计算里对象种类码的掩码判断(证据 E-049)。
    /// 种类码与脚本头文件的 OBJECTTYPE_* 一致(每一级占 4 位,见 QueryNatives.ObjectTypeCode):
    /// 角色 0x111、怪物 0x211、被动对象 0x21、召唤物 0x81;0x11 是"活动对象"(角色与怪物共有的上层类型)。
    /// 原版判断函数的形状是 (码 &amp; 掩码) == 掩码(服务端 isObjectType @0x080950E8,观察),
    /// 与脚本接口 isObjectType 的"逐级一致"写法不同,所以单独放一个判断函数,不复用 QueryNatives 的那个。
    /// </summary>
    public static class DamageObjectTypes
    {
        /// <summary>活动对象:角色与怪物。防御方为活动对象时才套用防御项,攻击方为活动对象时才套用攻击者的各项加成。</summary>
        public const uint Active = 0x11;

        /// <summary>角色。</summary>
        public const uint Character = 0x111;

        /// <summary>怪物(同样带"活动对象"的位 0x11)。</summary>
        public const uint Monster = 0x211;

        /// <summary>召唤物(宠物之类)。命中对象为召唤物时走召唤物的伤害公式。</summary>
        public const uint Creature = 0x81;

        /// <summary>被动对象(飞行道具等)。</summary>
        public const uint Passive = 0x21;

        /// <summary>(码 &amp; 掩码) == 掩码。</summary>
        public static bool Is(uint code, uint mask)
        {
            return (code & mask) == mask;
        }
    }
}
