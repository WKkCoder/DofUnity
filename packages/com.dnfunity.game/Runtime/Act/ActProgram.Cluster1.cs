namespace Dnf.Game.Act
{
    public sealed partial class ActRecord
    {
        /// <summary>[DELAY DO BEHAVIOR] 的延迟毫秒值表达式(支持常量与随机表达式/[/RANDOM SELECT] 后置值)。</summary>
        public ActValue DelayMs { get; internal set; }
    }

    public sealed partial class ActBehavior
    {
        /// <summary>[SET VISIBLITY] / [SET GRABABLE] / [SET DAMAGE CHECK] / [SET UNBEATABLE] 的开关或判断结果。</summary>
        public bool RestFlag { get; internal set; }

        /// <summary>[SET DAMAGE CHECK] / [SET UNBEATABLE] / [CHANGE TEAM] 的持续时间(毫秒)。</summary>
        public int RestDurationMs { get; internal set; }

        /// <summary>[SET PASSTYPE] 解析出的脚本码(0..5)。</summary>
        public int PassType { get; internal set; }

        /// <summary>[SET WIDTH] 第一个尺寸值表达式。</summary>
        public ActValue WidthX { get; internal set; }

        /// <summary>[SET WIDTH] 第二个尺寸值表达式。</summary>
        public ActValue WidthY { get; internal set; }

        /// <summary>[CHANGE TEAM] 的目标队号。</summary>
        public int RestTeam { get; internal set; }

        /// <summary>[CHANGE TEAM] 的生命上限百分比变化量。</summary>
        public int HpMaxPercent { get; internal set; }
    }
}
