using System.Collections.Generic;

namespace Dnf.Script
{
    /// <summary>
    /// 原版客户端在引擎里直接登记给 Squirrel 的整数常量(静态读客户端的原生接口注册函数得到,证据 E-045)。
    /// 脚本头文件(dnf_enum_header.nut 等)里一个也没有这些名字,脚本直接把它们当全局整数用。
    /// 只收录从注册函数里读出了取值的名字;名字之外没有别的含义依据(取值是不是连续的枚举、其余成员是什么,未知)。
    /// </summary>
    public static class EngineConstants
    {
        /// <summary>
        /// 依附体编号 APID_*。注册函数里每一项都是"名字 + 立即数"的形状,取值直接读出;
        /// 唯一例外是 APID_COMMON:它的取值来自一个此前被清零、之后没有改过的寄存器(推断为 0,没有别的形状可印证)。
        /// 212 个脚本里一共用到这 10 个名字,全部在表里。
        /// </summary>
        public static readonly IReadOnlyDictionary<string, int> AppendageIds = new Dictionary<string, int>
        {
            { "APID_COMMON", 0 },
            { "APID_SKILL_LIGHT_ENCHANT_WEAPON", 1 },
            { "APID_SKILL_ELEMENTAL_CHANGE", 188 },
            { "APID_MAGIC_SHIELD", 190 },
            { "APID_RESONANCE", 191 },
            { "APID_SKILL_ELEMENTAL_CHAIN_FIRE", 192 },
            { "APID_SKILL_ELEMENTAL_CHAIN_WATER", 193 },
            { "APID_SKILL_ELEMENTAL_CHAIN_DARK", 194 },
            { "APID_SKILL_ELEMENTAL_CHAIN_LIGHT", 195 },
            { "APID_AT_MAGE_ELEMENT_SHIELD", 328 },
        };
    }
}
