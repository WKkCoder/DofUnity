namespace Dnf.Net.Session
{
    /// <summary>
    /// "登录 → 选角 → 进城"回程明文帧解析出的只读快照(E-358、E-359)。
    /// 原则:只收明文帧里确有把握的字段;没把握的保持为空(<c>null</c>),缺口逐项写在对应属性的说明里。
    /// 由 <see cref="TownEntryParser"/> 生成;这个类没有任何可写成员,也不持有帧数据。
    /// </summary>
    public sealed class TownEntrySnapshot
    {
        /// <summary>什么也没解出来的快照(没有帧、全是未知帧号或全部错位时:各字段为空/假/0)。</summary>
        public static readonly TownEntrySnapshot Empty = new TownEntrySnapshot(null, false, null, null, 0, 0, 0);

        internal TownEntrySnapshot(int? slot, bool enterComplete, int? serverHour, int? serverMinute,
            int framesSeen, int framesParsed, int framesMalformed)
        {
            Slot = slot;
            EnterComplete = enterComplete;
            ServerHour = serverHour;
            ServerMinute = serverMinute;
            FramesSeen = framesSeen;
            FramesParsed = framesParsed;
            FramesMalformed = framesMalformed;
        }

        // ---- 目标字段一:槽号(请求侧给定) ----

        /// <summary>
        /// 进城用的槽号。<b>不是从明文帧里解析出来的</b>:选角成功回包被封进 561 信封,明文回程里没有任何一帧回显槽号,
        /// 所以这个值就是调用方给 <see cref="TownEntryParser.Parse"/> 的选角请求槽号(没给就是 null)。
        /// </summary>
        public int? Slot { get; }

        // ---- 目标字段二到五:明文里没有承载帧(缺口,恒为 null) ----

        /// <summary>
        /// 角色等级。<b>缺口,本版恒为 null。</b>据服务端静态读码,等级在用户信息通知(类 0、包号 2)类型 0 的 u8 里,
        /// 角色列表条目里也有;这几类包在进城流里全部走 561 信封,明文帧逐帧核对没有任何一帧带等级。信封内层读通之前保持为空。
        /// </summary>
        public int? Level => null;

        /// <summary>
        /// 职业(角色信息偏移 0 的那一项,E-046)。<b>缺口,本版恒为 null。</b>同 <see cref="Level"/>:承载包被封进信封。
        /// </summary>
        public int? Job => null;

        /// <summary>
        /// 成长类型(转职方向,0 是转职前;角色信息偏移 0xc 的那一项,E-046)。<b>缺口,本版恒为 null。</b>同 <see cref="Level"/>。
        /// </summary>
        public int? GrowType => null;

        /// <summary>
        /// 区域编号(进城落脚的村庄/区域)。<b>缺口,本版恒为 null。</b>据服务端静态读码,选角成功回包(类 1、包号 4)里有一个村庄字节,
        /// 但该回包被封进信封;区域类通知(类 0、包号 23)不在进城流内,要等客户端进区之后才下发。
        /// </summary>
        public int? AreaId => null;

        // ---- 明文里确认下来的字段 ----

        /// <summary>
        /// 是否见到进城完成通知(类 0、包号 124,空载荷;E-359)。只认包类与包号,不看载荷。
        /// </summary>
        public bool EnterComplete { get; }

        /// <summary>
        /// 服务端时间同步通知(类 0、包号 300,载荷 u16 时 + u16 分)里的"时"(0 到 23);没收到或这一帧错位为 null。
        /// 证据级别:字段序来自服务端静态读码,实测一帧的时与分都与探针落盘时刻吻合(单样本)。该通知由外部调度服务器触发(E-309),不保证出现。
        /// </summary>
        public int? ServerHour { get; }

        /// <summary>时间同步通知里的"分"(0 到 59);规则同 <see cref="ServerHour"/>。</summary>
        public int? ServerMinute { get; }

        // ---- 诊断计数(给集成方看布局是否漂移,不参与业务判断) ----

        /// <summary>解析器看过的帧数(空引用的列表项不算)。</summary>
        public int FramesSeen { get; }

        /// <summary>被认出并成功读出字段的帧数。</summary>
        public int FramesParsed { get; }

        /// <summary>包号认得但载荷太短或取值不合(错位)、被跳过的帧数;持续非 0 说明布局与实测不一致。</summary>
        public int FramesMalformed { get; }
    }
}
