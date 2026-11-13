using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 物品数据下行包的聚合快照(只读):由 <see cref="ItemSyncParser.Parse"/> 把一串解码帧里的物品类包按帧序归约出来。
    /// <para>
    /// 归约规则(与 <see cref="TownEntrySnapshot"/> 同一风格,都是纯函数、不抛异常):
    /// 物品空间包(NOTI/13)是某个空间的<b>整份状态</b>,同一空间后到者整体覆盖先到者;
    /// 物品增量包(NOTI/14)是<b>逐槽补丁</b>,按线上顺序叠在已有状态上(清空标记去掉该槽,其余槽原样保留),不是"后到者覆盖整个空间";
    /// 先增量、后空间包时,空间包整体覆盖增量叠出的内容(帧序即语义)。
    /// 货币(背包槽 0、1、2 的伪条目)单独存放在 <see cref="Currency"/>:有值的槽取较新者,缺席的槽保持原值——
    /// 这一条对"整份背包包里缺货币槽"的处理是本线的选择(真实背包包恒带三个货币槽,本机抓帧里没有缺席的样本),不是读码结论。
    /// 其余小包(点券、装扮币应答、冷却/效果恢复、物品锁、装备等级上限、快捷键选项、宠物属性表)各是整份状态,后到的有效帧覆盖先到的;
    /// 备用装备组(NOTI/339)与用户信息公共视图每个角色一份,按到达顺序全部保留,"是不是本人"由消费方筛;
    /// 用户信息本人详细视图只有一份,后到的覆盖先到的。
    /// </para>
    /// <para>
    /// 诊断计数:<see cref="FramesSeen"/>(非空帧)、<see cref="FramesParsed"/>(认得的物品类帧且解析成功)、<see cref="FramesMalformed"/>
    /// (认得的物品类帧但读不通,含缺物品类型回调而被拒的整包)。不认得的帧(别的包号、别的类、用户信息的模式 2 列表)只计入 <see cref="FramesSeen"/>。
    /// 错位的帧不会冲掉之前已读到的有效状态,也不会半个生效(整包要么全应用要么不应用)。
    /// </para>
    /// 不可变;构造入口只给 <see cref="ItemSyncParser"/>。
    /// </summary>
    public sealed class ItemSyncSnapshot
    {
        private readonly Dictionary<ItemSpace, ItemSpaceState> _spaces;
        private readonly IReadOnlyList<UserInfoViewNotice> _publicViews;
        private readonly IReadOnlyList<ExpandEquipNotice> _expandEquips;

        internal ItemSyncSnapshot(
            Dictionary<ItemSpace, ItemSpaceState> spaces,
            ItemCurrency currency,
            CeraNotice cera,
            AvatarCoinNotice avatarCoin,
            ContinuousItemListNotice coolTimeItems,
            ContinuousItemListNotice effectItems,
            ItemLockNotice itemLocks,
            MaxEquipLevelNotice maxEquipLevel,
            HotkeyOptionNotice hotkeyOption,
            UserInfoViewNotice[] publicViews,
            UserInfoViewNotice selfView,
            ExpandEquipNotice[] expandEquips,
            CreatureAttributeNotice creatures,
            int framesSeen,
            int framesParsed,
            int framesMalformed,
            int framesKindUnresolved)
        {
            _spaces = new Dictionary<ItemSpace, ItemSpaceState>(spaces);
            Currency = currency ?? ItemCurrency.Empty;
            Cera = cera;
            AvatarCoin = avatarCoin;
            CoolTimeItems = coolTimeItems;
            EffectItems = effectItems;
            ItemLocks = itemLocks;
            MaxEquipLevel = maxEquipLevel;
            HotkeyOption = hotkeyOption;
            _publicViews = Array.AsReadOnly(publicViews ?? new UserInfoViewNotice[0]);
            SelfView = selfView;
            _expandEquips = Array.AsReadOnly(expandEquips ?? new ExpandEquipNotice[0]);
            Creatures = creatures;
            FramesSeen = framesSeen;
            FramesParsed = framesParsed;
            FramesMalformed = framesMalformed;
            FramesKindUnresolved = framesKindUnresolved;
        }

        /// <summary>背包的归约状态(不含货币伪条目);没有任何背包类帧生效时为 null。</summary>
        public ItemSpaceState Bag => GetSpace(ItemSpace.Bag);

        /// <summary>装扮背包的归约状态;没有相关帧时为 null。</summary>
        public ItemSpaceState AvatarBag => GetSpace(ItemSpace.AvatarBag);

        /// <summary>仓库的归约状态;没有相关帧时为 null。</summary>
        public ItemSpaceState Cargo => GetSpace(ItemSpace.Cargo);

        /// <summary>宠物栏的归约状态;没有相关帧时为 null。</summary>
        public ItemSpaceState PetBag => GetSpace(ItemSpace.PetBag);

        /// <summary>账号仓库的归约状态;没有相关帧时为 null。</summary>
        public ItemSpaceState AccountCargo => GetSpace(ItemSpace.AccountCargo);

        /// <summary>身上装备(只由增量包叠出,<see cref="ItemSpaceState.HasFullState"/> 恒为假);没有相关帧时为 null。</summary>
        public ItemSpaceState BodyEquip => GetSpace(ItemSpace.BodyEquip);

        /// <summary>按空间号取归约状态;没有相关帧或空间号不在支持集合里时为 null。</summary>
        public ItemSpaceState GetSpace(ItemSpace space)
        {
            return _spaces.TryGetValue(space, out ItemSpaceState state) ? state : null;
        }

        /// <summary>背包货币(金币、硬币合计、胜点);从未出现过的槽为 null,永不为 null 对象。</summary>
        public ItemCurrency Currency { get; }

        /// <summary>最近一个点券通知(NOTI/53);没有为 null。</summary>
        public CeraNotice Cera { get; }

        /// <summary>最近一个装扮币数量应答(类 1、587);没有为 null。</summary>
        public AvatarCoinNotice AvatarCoin { get; }

        /// <summary>最近一个物品冷却恢复表(NOTI/172);没有为 null。</summary>
        public ContinuousItemListNotice CoolTimeItems { get; }

        /// <summary>最近一个物品效果恢复表(NOTI/174);没有为 null。</summary>
        public ContinuousItemListNotice EffectItems { get; }

        /// <summary>最近一个物品锁表(NOTI/251);没有为 null。</summary>
        public ItemLockNotice ItemLocks { get; }

        /// <summary>最近一个装备等级上限(NOTI/438);没有为 null。</summary>
        public MaxEquipLevelNotice MaxEquipLevel { get; }

        /// <summary>最近一个快捷键选项(NOTI/452);没有为 null。</summary>
        public HotkeyOptionNotice HotkeyOption { get; }

        /// <summary>用户信息公共视图(模式 0),按到达顺序,每个角色一帧。</summary>
        public IReadOnlyList<UserInfoViewNotice> PublicViews => _publicViews;

        /// <summary>用户信息本人详细视图(模式 1),最近一个;没有为 null。</summary>
        public UserInfoViewNotice SelfView { get; }

        /// <summary>备用装备组通知(NOTI/339),按到达顺序全部保留;"是不是本人"由消费方按唯一号筛。</summary>
        public IReadOnlyList<ExpandEquipNotice> ExpandEquips => _expandEquips;

        /// <summary>最近一个宠物属性表(NOTI/105);没有为 null。</summary>
        public CreatureAttributeNotice Creatures { get; }

        /// <summary>看过的非空帧个数。</summary>
        public int FramesSeen { get; }

        /// <summary>认得且解析成功的物品类帧个数。</summary>
        public int FramesParsed { get; }

        /// <summary>认得但读不通的物品类帧个数(含缺物品类型回调而被整包拒绝的)。</summary>
        public int FramesMalformed { get; }

        /// <summary>
        /// <see cref="FramesMalformed"/> 里因为"需要物品类型却没有判定回调(或回调回答不认识)"而被整包拒绝的个数(宠物栏、身上装备增量)。
        /// 它不为 0 通常说明调用方没有注入 <see cref="IItemKindResolver"/>。
        /// </summary>
        public int FramesKindUnresolved { get; }
    }
}
