namespace Dnf.Game.Act
{
    /// <summary>M107:符号与绝对读条行为的字段(读码依据见 ActRunner.Symbols.cs)。</summary>
    public sealed partial class ActBehavior
    {
        /// <summary>[SET SYMBOL]、[SET SYMBOL OPTION]、[SET PARTY SYMBOL] 的整数(客户端直接读成一个整数,不是值表达式)。</summary>
        public int SymbolValue { get; internal set; }

        /// <summary>[ABS CASTING] 的第一个整数。</summary>
        public int AbsCastFirst { get; internal set; }

        /// <summary>[ABS CASTING] 的第二个整数。</summary>
        public int AbsCastSecond { get; internal set; }
    }
}
