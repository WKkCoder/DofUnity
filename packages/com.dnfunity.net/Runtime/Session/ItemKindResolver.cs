namespace Dnf.Net.Session
{
    /// <summary>
    /// 物品的"形状类":决定条目后半段有没有可选块。线上<b>不带</b>这个信息(E-410、E-412:物品定义级字段不下发,
    /// 条目里只有物品号),所以解析器要靠 <see cref="IItemKindResolver"/> 由调用方按物品号告知,绝不凭字节猜。
    /// <para>
    /// 类名特意不叫 <c>ItemKind</c>:游戏层(<c>Dnf.Game</c>)已有同名的物品种类枚举,接线时同一个文件往往要同时引入两个命名空间
    /// (典型场景就是用游戏层的物品目录实现 <see cref="IItemKindResolver"/>),同名会让简单名字有歧义。这里只有"是不是装扮、是不是宠物"
    /// 两个对线格式有影响的判定,与游戏层的种类枚举不是一回事。
    /// </para>
    /// </summary>
    public enum ResolvedItemKind
    {
        /// <summary>不认识这个物品号:判定方给不出答案。需要类型的条目遇到它整包拒绝(装备详细记录除外,见 <see cref="ItemEntryShape.EquipDetail"/>)。</summary>
        Unknown = 0,

        /// <summary>既不是装扮也不是宠物:条目后半段没有可选块。</summary>
        Other = 1,

        /// <summary>
        /// 装扮类物品(服务端 <c>Inven_Item::isAvatarItemType</c> 0x08150800,类型字节为 8):条目多两个"u32 长度 + 字节"块
        /// (30 字节镶嵌块、4 字节扩展块)。
        /// </summary>
        Avatar = 2,

        /// <summary>
        /// 宠物类物品(服务端物品类型字节为 5,判定点 0x084fea89;客户端判据是物品种类字段等于 0x16,见 0x007532cc):
        /// 条目多一个 u32(宠物剩余时长)。
        /// </summary>
        Pet = 3,
    }

    /// <summary>
    /// 物品类型判定回调:按物品号回答它是装扮、宠物还是别的。
    /// 条目后半段的可选块取决于物品类型(笔记 §2.2.4、§4.3 第 3 条),而线上不带类型,所以解析器把"查类型"外包给调用方
    /// (联网端拿不到离线的物品目录时,宁可整包拒绝也不猜)。实现必须无副作用、不抛异常;不认识的物品号返回 <see cref="ResolvedItemKind.Unknown"/>。
    /// </summary>
    public interface IItemKindResolver
    {
        /// <summary>按物品号给出类型;不认识返回 <see cref="ResolvedItemKind.Unknown"/>。</summary>
        ResolvedItemKind Resolve(uint itemId);
    }
}
