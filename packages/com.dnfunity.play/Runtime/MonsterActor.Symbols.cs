using Dnf.Game.Act;

namespace Dnf.Play
{
    // M107 怪物线 A:怪物在 .act 里的符号槽,以及 [IS DAMAGE ENABLE] / [IS DAMAGEBOX ENABLE] 读到的受击盒开关。
    public sealed partial class MonsterActor
    {
        /// <summary>[SET SYMBOL] 写、[CHECK SYMBOL] 读的槽(客户端对象 +0x200,观察)。出生值 -1 是推断:对象复位函数写 -1,脚本里到处是 "== -1" 的写法;没有证据说别的值。</summary>
        private int _actSymbol = -1;

        /// <summary>[SET SYMBOL OPTION] 写的槽(客户端对象 +0x204,观察)。读它的地方没找到(未知),所以这里只存不用。</summary>
        private int _actSymbolOption = -1;

        private sealed partial class ActBody
        {
            public int ActSymbol => _m._actSymbol;

            public void SetActSymbol(int value)
            {
                _m._actSymbol = value;
            }

            public int ActSymbolOption => _m._actSymbolOption;

            public void SetActSymbolOption(int value)
            {
                _m._actSymbolOption = value;
            }

            /// <summary>
            /// 受击盒开着吗:沿用 <see cref="DamageBoxOff"/>([SET DAMAGE BOX] [ON]/[OFF] 写的那个状态,E-133)。客户端里 [IS DAMAGEBOX ENABLE] 与 [IS DAMAGE ENABLE]
            /// 读同一个虚函数(槽 0x36c),写它的是 [SET DAMAGE BOX] 用的槽 0x368(观察);出生时开着是 E-133 就采用的假设,构造函数里写的是 0(未知)。
            /// </summary>
            public bool IsActDamageBoxEnabled => !_m._damageBoxOff;

            public bool? ActDamageEnabled => !_m._damageBoxOff;
        }
    }
}
