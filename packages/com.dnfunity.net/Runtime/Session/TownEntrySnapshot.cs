namespace Dnf.Net.Session
{
    /// <summary>
    /// "登录 → 选角 → 进城"回程明文帧解析出的只读快照(E-359)。
    /// 只收明文里确有把握的字段;没把握的字段保持为空(<c>null</c>),缺口与原因写在 <see cref="TownEntryParser"/> 的说明里。
    /// 空壳阶段:字段待分析结论落定后补齐。
    /// </summary>
    public sealed class TownEntrySnapshot
    {
        /// <summary>什么也没解出来的快照(没有帧、全是未知帧号或全部错位时返回它)。</summary>
        public static readonly TownEntrySnapshot Empty = new TownEntrySnapshot();

        private TownEntrySnapshot()
        {
        }
    }
}
