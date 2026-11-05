using Dnf.Game.Act;

namespace Dnf.Play
{
    // M107 怪物线 A:被动对象与主角在 .act 里的符号槽。受击盒开关读数这里不给(留给接口默认的"未知"):
    // 被动对象的 [SET DAMAGE BOX] 是空操作(没有受击盒模型),主角的也是。

    public sealed partial class PassiveObject
    {
        private int _actSymbol = -1;
        private int _actSymbolOption = -1;

        int IActEntity.ActSymbol => _actSymbol;

        void IActEntity.SetActSymbol(int value)
        {
            _actSymbol = value;
        }

        int IActEntity.ActSymbolOption => _actSymbolOption;

        void IActEntity.SetActSymbolOption(int value)
        {
            _actSymbolOption = value;
        }
    }

    public sealed partial class HeroActEntity
    {
        private int _actSymbol = -1;
        private int _actSymbolOption = -1;

        int IActEntity.ActSymbol => _actSymbol;

        void IActEntity.SetActSymbol(int value)
        {
            _actSymbol = value;
        }

        int IActEntity.ActSymbolOption => _actSymbolOption;

        void IActEntity.SetActSymbolOption(int value)
        {
            _actSymbolOption = value;
        }
    }
}
