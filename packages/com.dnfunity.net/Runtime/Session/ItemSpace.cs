namespace Dnf.Net.Session
{
    /// <summary>
    /// 物品空间号:物品空间包(类 0、包号 13)与物品增量包(类 0、包号 14)的第一个字节。
    /// 数值就是线上字节,同时是服务端的 <c>ENUM_ITEMSPACE</c> 与 <c>send_itemspace</c> 的实参(笔记 §1.2、§2.1.2、§2.3.2);
    /// 注意它<b>不是</b>服务端的 <c>INVEN_TYPE</c>(1/2/3),别混用。
    /// </summary>
    public enum ItemSpace : byte
    {
        /// <summary>背包:312 格数组,格 0 到 2 是货币伪条目(E-410)。</summary>
        Bag = 0,

        /// <summary>装扮背包:格 0 到 104;条目的 Data 位是剩余时长,多两个块。</summary>
        AvatarBag = 1,

        /// <summary>仓库:格 0 到 151。</summary>
        Cargo = 2,

        /// <summary>
        /// 身上装备。<b>只在物品增量包出现</b>(物品空间包没有写入者,E-411);客户端不走容器,走"穿脱"分支。
        /// </summary>
        BodyEquip = 3,

        /// <summary>宠物栏:格 0 到 241;宠物类条目多一个 u32。</summary>
        PetBag = 7,

        /// <summary>账号仓库:本机抓帧里没有(账号仓库不存在时服务端不发),只有读写序对账。</summary>
        AccountCargo = 0x0C,
    }

    /// <summary>物品空间包、物品增量包解析的结论。</summary>
    public enum ItemParseStatus
    {
        /// <summary>读完全部条目。</summary>
        Ok = 0,

        /// <summary>
        /// 布局读不下来:载荷为 null、为空、过短,个数声明超出载荷,条目读到一半被截断,
        /// 或取值不合(镶嵌块长度不是 30 与 4、随机选项个数大于 3、货币伪条目的物品号不等于槽号)。
        /// </summary>
        Malformed = 1,

        /// <summary>空间号不在这个包支持的集合里:整包拒绝,绝不按别的空间的布局去读。</summary>
        UnsupportedSpace = 2,

        /// <summary>
        /// 条目需要物品类型才能读下去,但调用方没有给判定回调,或回调对该物品号回答 <see cref="ResolvedItemKind.Unknown"/>:整包拒绝。
        /// 条数为 0 的包与只含删除标记的增量包不需要类型,不受影响。
        /// </summary>
        KindUnresolved = 3,
    }
}
