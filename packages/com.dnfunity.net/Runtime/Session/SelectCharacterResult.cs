namespace Dnf.Net.Session
{
    /// <summary>
    /// 选角失败码的映射(只用于失败体,即成功标志为 0 时的次字节)。枚举成员的数值不是线上码,线上原码在 <see cref="SelectCharacterResult.FailureCode"/>。
    /// 真正会从本地服务端送达客户端的失败码只有四个(packetread_NOTES §3.2 的 <c>check_error</c> 全表【观】):
    /// 0x13、4、0x9f、8;其余失败要么静默(不回包),要么服务端直接断开连接,不会有失败体。
    /// </summary>
    public enum SelectCharacterFailure
    {
        /// <summary>没有失败(成功体)。</summary>
        None = 0,

        /// <summary>码 0x13:服务端 <c>isLockedCreateCharac()</c> 为真(建角被锁)。</summary>
        CreationLocked,

        /// <summary>码 4:请求里的角色下标不在 [0, 0x23]。</summary>
        SlotOutOfRange,

        /// <summary>码 0x9f:该角色名命中敏感词过滤(<c>CSyncSlangFilter::HasSlangName</c> 或 <c>HasSlang</c>)。</summary>
        NameHasSlang,

        /// <summary>码 8:该角色名命中禁用前缀(<c>CDataManager::hasPreventPrefixCharacName</c>);客户端对 8 会弹提示。</summary>
        NamePrefixForbidden,

        /// <summary>
        /// 码 0x60:客户端对它有特判(弹提示),但本地服务端<b>没有</b>任何会发 (1,4)+0x60 的路径【观】(全量分类 727 个发送点,
        /// 以立即数 0x60 作 err 的为 0;客户端也不自合成)。它只可能来自本地没有的另一个服务端版本或进程【推】,来源未知【未】。
        /// 收到它不代表本地服务端出了问题,按"客户端特判码"原样映射,不猜含义。
        /// </summary>
        ClientSpecial,

        /// <summary>其它任何码(包括 0):本地服务端不会送达,原码保留在 <see cref="SelectCharacterResult.FailureCode"/>。</summary>
        Unknown,
    }

    /// <summary>
    /// 选角回包(类 1、包号 4)解出的只读记录,由 <see cref="SelectCharacterResultParser"/> 生成:结果码,加上成功时的载入字段。
    /// 没有任何可写成员,也不持有载荷数组。
    /// <para>
    /// 读法照客户端:首字节是成功标志,非 0 为成功(不是"等于 1":服务端只写 1,但客户端判的是非 0);为 0 时再读一个字节的失败码,失败体到此为止。
    /// 成功体的字段在 <see cref="Load"/>;失败体 <see cref="Load"/> 为 null。
    /// </para>
    /// </summary>
    public sealed class SelectCharacterResult
    {
        internal SelectCharacterResult(byte flag, byte failureCode, SelectCharacterLoadInfo load, int trailingByteCount)
        {
            Flag = flag;
            FailureCode = failureCode;
            Failure = flag != 0 ? SelectCharacterFailure.None : SelectCharacterResultParser.MapFailureCode(failureCode);
            Load = load;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>首字节的原始值(成功标志)。</summary>
        public byte Flag { get; }

        /// <summary>是否成功:<see cref="Flag"/> 非 0(客户端的判法)。</summary>
        public bool IsSuccess => Flag != 0;

        /// <summary>失败码的原始值(失败体的次字节);成功体恒为 0,不要拿它判成败,看 <see cref="IsSuccess"/>。</summary>
        public byte FailureCode { get; }

        /// <summary>失败码的映射;成功体为 <see cref="SelectCharacterFailure.None"/>。</summary>
        public SelectCharacterFailure Failure { get; }

        /// <summary>成功体的载入字段;失败体为 null。</summary>
        public SelectCharacterLoadInfo Load { get; }

        /// <summary>
        /// 读完最后一个字段之后载荷里还剩的字节数。解析器不消费、不核对这段尾巴。
        /// 真实帧里尾部可能有折叠哈希(服务端对载荷长度 1 到 0x190 追加 4 字节;成功体本身 170 + 5N + b 字节,不大于 0x190 时带它)与会话加密补零,
        /// 只体现在这个数里,只供对账,不参与解析成败。
        /// </summary>
        public int TrailingByteCount { get; }
    }
}
