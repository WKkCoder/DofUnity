using System;
using System.Collections.Generic;

// 证据:服务端道具使用的派发(静态读码,没运行)。服务端没有一张总表,而是每类使用各有一个 Dispatcher(按包派发:客户端按物品的种类与属性选发哪个包),
// 读包、查条件,最后都落到同一个消耗步骤 CUser::use_stackable 0x0865e0ae(查冷却 CheckCoolTimeItem 0x0865e994 → CInventory::use_item 0x084ff8de 扣一件 → 登记冷却)。
// 这张表是"可叠加物品的种类编号 → 使用类"的归并:种类编号是 ImportStackableScript 0x089aeec8 里"字符串 → 赋值"那些分支的编号(见 StackableTypes),
// 对应的 Dispatcher 只校验自己要的那类物品,所以对应关系要反推(全部地址与每类的结论卡见 out/itemuse/NOTES.md):
//   服务端直接比较了种类或物品编号的(观察):回复药 DisPatcher_UseStack 0x081f63c0(城镇只放行种类 3、22、24、35)、随机箱 Dispatcher_UseBoosterItem 0x08208d40(16、34)、
//   礼包 Dispatcher_CeraPackageOpen 0x081e788a(38)、多重箱 DisPatcher_MultiBoxLottery 0x0820b802(26、27)、限定魔方 Dispatcher_UseLimitCube 0x081d3d38(29)、
//   传送 Dispatcher_Teleport 0x081d07bc(按编号认三个传送道具);
//   有脚本数据旁证的归类(推断):12 与带 [shared effect] 的 7 个脚本一一对应,13 与只出现在种类 13 里的 [lottery use cost],24 的 27 个脚本里有 19 个带 [action type],
//   染色 Dispatcher_UseDye 0x081e69ea 按 [dye info] 认(种类 36 的 36 个脚本都有);只按标签名字归类、没有旁证的(推断):22、23。其余没读到的种类标"未读",使用被拒、什么也不动。
namespace Dnf.Game
{
    /// <summary>使用类:一件可叠加物品"用一下"走哪一类处理。同一类的物品共用同一套条件与效果。</summary>
    public enum ItemUseClass
    {
        /// <summary>没有使用入口(或这个版本还没读到它的使用路径,见 <see cref="ItemUseStatus.Unread"/>)。</summary>
        None = 0,

        /// <summary>回复药:回复生命、魔法([waste] 与 [throw] 里写了 [hp recovery]/[mp recovery] 的);背包与快捷栏共用,已做。</summary>
        Recovery,

        /// <summary>随机箱([booster]):按块抽取,每块抽 N 次、有放回、按权重;只在城镇开。</summary>
        OpenBox,

        /// <summary>固定礼包([usable cera package]):<c>[package data]</c> 里的内容全给,没有权重;只在城镇开。</summary>
        OpenPackage,

        /// <summary>选择箱([booster selection]):客户端选类别后再抽,需要选择界面。</summary>
        OpenSelection,

        /// <summary>只在商城购买时就地开的([cera package]、[cera booster]、[booster random]):背包里没有使用入口。</summary>
        ShopOnly,

        /// <summary>多重箱([multi upgradable legacy] 一类):百万分之一的绝对概率加保底项,上锁的要钥匙。</summary>
        MultiBox,

        /// <summary>抽奖券:按花费表扣金币、抽奖励(DisPatcher_Lottery 0x081f8380 → CUser::lottery_item 0x086733a2)。种类 13 归这一类有旁证:[lottery use cost] 只出现在种类 13 的脚本里。</summary>
        Lottery,

        /// <summary>传送道具([teleport potion]):选城镇去向后传送;本地只有桩(见 <see cref="TeleportDesk"/>)。</summary>
        Teleport,

        /// <summary>染色剂([dye])。</summary>
        Dye,

        /// <summary>
        /// 伪装([disguise])。服务端 Dispatcher_DisguiseRequest 0x0821b256 只设置伪装状态、不碰物品(状态 3、不在个人商店、不是幽灵、不在突击场地、尚未伪装);
        /// 物品本身走通用的 UseStack(城镇放行种类 22)。本地只登记。
        /// </summary>
        Disguise,

        /// <summary>
        /// 效果类([only effect]、[global effect]):种类 12 的 7 个脚本都带 [shared effect],对应服务端 Dispatcher_UseSharedEffectItem 0x08220484(状态 3,扣一件);
        /// 种类 23 只是按名字归在这里、没有旁证(它在自动售货机 Dispatcher_UseVendingMachine 0x0821c2e6 里只作为奖品的种类出现)。本地只登记。
        /// </summary>
        Effect,

        /// <summary>
        /// [action type] 驱动的效果(服务端 Dispatcher_StackableActionUse 0x0825e11e,按物品的 [action type] 认、不按种类):[expert town potion] 的 27 个脚本里有 19 个带它,
        /// 归在这一类有旁证但不是全部;其余带 [action type] 的脚本分布在 [etc]、[waste] 等种类里,这张表不按它们归类。
        /// </summary>
        Action,

        /// <summary>限定魔方([upgrade limit cube])。</summary>
        LimitCube,

        /// <summary>宠物类:孵化([creature])、进化物品([creature expitem])、喂食([feed],本线不做)。</summary>
        Creature,

        /// <summary>设计图([recipe]):副职业设计图的使用是学会配方(CAlchemist::OnUseStackableItem 0x085d1712 里的 learnRecipe);其余设计图在制作界面里用(推断)。归制作线,本地只登记。</summary>
        Recipe,
    }

    /// <summary>一类使用在这个版本里做到哪一步。</summary>
    public enum ItemUseStatus
    {
        /// <summary>做了(回复药是既有实现;随机箱与礼包是这一线的)。</summary>
        Implemented = 0,

        /// <summary>桩:只记"用过、去向待定",不扣物品、不起冷却(传送)。</summary>
        Stub,

        /// <summary>登记:服务端的使用路径读过、知道它是哪一类,但本地没做;使用被拒、什么也不动。</summary>
        Registered,

        /// <summary>未读:服务端有没有直接的使用路径还没查清,先当没有;使用被拒、什么也不动。</summary>
        Unread,
    }

    /// <summary>
    /// 允许使用的地点(服务端按用户状态:3 城镇、5 副本)。回复药只在副本:服务端 DisPatcher_UseStack 0x081f63c0 在城镇(状态 3、6)只放行几个种类(3、22、24、35)与按编号写死的少数物品,
    /// 回复药的种类(0、4)不在放行的种类里(本地的回复药在城镇本来就被拒,见 <see cref="UseResult.Unavailable"/>;按编号写死的那几个是否含药水没逐个核对);
    /// 随机箱、礼包、传送的专用处理器要求状态 3,只在城镇。
    /// </summary>
    [Flags]
    public enum ItemPlaces
    {
        None = 0,
        Town = 1,
        Dungeon = 2,
        Any = Town | Dungeon,
    }

    /// <summary>派发表的一行:一个可叠加物品种类编号对应的使用类、做到哪一步、能在哪里用。</summary>
    public sealed class ItemUseRow
    {
        public ItemUseRow(int stackableType, string tag, ItemUseClass useClass, ItemUseStatus status, ItemPlaces places)
        {
            StackableType = stackableType;
            Tag = tag;
            Class = useClass;
            Status = status;
            Places = places;
        }

        /// <summary>可叠加物品的种类编号(<see cref="StackableTypes"/>)。</summary>
        public int StackableType { get; }

        /// <summary><c>[stackable type]</c> 后面的字符串(<see cref="StackableTypes.FromTag"/> 认的那个标签)。</summary>
        public string Tag { get; }

        public ItemUseClass Class { get; }

        public ItemUseStatus Status { get; }

        /// <summary>读到的允许地点;没读到(登记、未读)是 <see cref="ItemPlaces.None"/>。</summary>
        public ItemPlaces Places { get; }
    }

    /// <summary>
    /// 道具使用的派发表:可叠加物品种类 → 使用类。背包窗口、快捷栏资格、使用检查都从这里查,不各自再写一遍"哪些物品能用"。
    /// 种类编号 0 到 38 每个都有一行(没读到的标 <see cref="ItemUseStatus.Unread"/>),表外的种类(装备、标签不认识的 −1)没有行。
    /// </summary>
    public static class ItemUseTable
    {
        private static readonly ItemUseRow[] RowList =
        {
            Row(0, "[waste]", ItemUseClass.Recovery, ItemUseStatus.Implemented, ItemPlaces.Dungeon),
            Row(1, "[material]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(2, "[material expert job]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(3, "[recipe]", ItemUseClass.Recipe, ItemUseStatus.Registered, ItemPlaces.None),
            Row(4, "[throw]", ItemUseClass.Recovery, ItemUseStatus.Implemented, ItemPlaces.Dungeon),
            Row(5, "[set]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(6, "[legacy]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(7, "[stackable legacy]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(8, "[quest]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(9, "[creature]", ItemUseClass.Creature, ItemUseStatus.Registered, ItemPlaces.None),
            Row(10, "[feed]", ItemUseClass.Creature, ItemUseStatus.Registered, ItemPlaces.None),
            Row(11, "[etc]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(12, "[only effect]", ItemUseClass.Effect, ItemUseStatus.Registered, ItemPlaces.None),
            Row(13, "[upgradable legacy]", ItemUseClass.Lottery, ItemUseStatus.Registered, ItemPlaces.None),
            Row(14, "[cera package]", ItemUseClass.ShopOnly, ItemUseStatus.Registered, ItemPlaces.None),
            Row(15, "[cera booster]", ItemUseClass.ShopOnly, ItemUseStatus.Registered, ItemPlaces.None),
            Row(16, "[booster]", ItemUseClass.OpenBox, ItemUseStatus.Implemented, ItemPlaces.Town),
            Row(17, "[booster random]", ItemUseClass.ShopOnly, ItemUseStatus.Registered, ItemPlaces.None),
            Row(18, "[unlimited waste]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(19, "[quest receive]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(20, "[avatar emblem]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(21, "[unlimited etc]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(22, "[disguise]", ItemUseClass.Disguise, ItemUseStatus.Registered, ItemPlaces.None),
            Row(23, "[global effect]", ItemUseClass.Effect, ItemUseStatus.Registered, ItemPlaces.None),
            Row(24, "[expert town potion]", ItemUseClass.Action, ItemUseStatus.Registered, ItemPlaces.None),
            Row(25, "[contract]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(26, "[multi upgradable legacy]", ItemUseClass.MultiBox, ItemUseStatus.Registered, ItemPlaces.None),
            Row(27, "[multi upgradable legacy bonus cera]", ItemUseClass.MultiBox, ItemUseStatus.Registered, ItemPlaces.None),
            Row(28, "[enchant waste]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(29, "[upgrade limit cube]", ItemUseClass.LimitCube, ItemUseStatus.Registered, ItemPlaces.None),
            Row(30, "[tarot card lottery]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(31, "[tarot card]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(32, "[town and dungeon]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(33, "[unlimited town and dungeon]", ItemUseClass.None, ItemUseStatus.Unread, ItemPlaces.None),
            Row(34, "[booster selection]", ItemUseClass.OpenSelection, ItemUseStatus.Registered, ItemPlaces.None),
            Row(35, "[creature expitem]", ItemUseClass.Creature, ItemUseStatus.Registered, ItemPlaces.None),
            Row(36, "[dye]", ItemUseClass.Dye, ItemUseStatus.Registered, ItemPlaces.None),
            Row(37, "[teleport potion]", ItemUseClass.Teleport, ItemUseStatus.Stub, ItemPlaces.Town),
            Row(38, "[usable cera package]", ItemUseClass.OpenPackage, ItemUseStatus.Implemented, ItemPlaces.Town),
        };

        private static ItemUseRow Row(int type, string tag, ItemUseClass useClass, ItemUseStatus status, ItemPlaces places)
        {
            return new ItemUseRow(type, tag, useClass, status, places);
        }

        /// <summary>全部行(按种类编号升序,0 到 38 各一行)。</summary>
        public static IReadOnlyList<ItemUseRow> Rows => RowList;

        /// <summary>某个种类编号的行;不在 0 到 38 里返回 null。</summary>
        public static ItemUseRow RowOf(int stackableType)
        {
            return (uint)stackableType < (uint)RowList.Length ? RowList[stackableType] : null;
        }

        /// <summary>这件物品的行;不是可叠加物品或种类不认识返回 null。</summary>
        public static ItemUseRow RowOf(ItemInfo item)
        {
            return item != null && item.IsStackable ? RowOf(item.StackableType) : null;
        }

        /// <summary>这件物品的使用类(装备、种类不认识、没读到的都是 <see cref="ItemUseClass.None"/>)。</summary>
        public static ItemUseClass ClassOf(ItemInfo item)
        {
            ItemUseRow row = RowOf(item);
            return row == null ? ItemUseClass.None : row.Class;
        }

        /// <summary>这件物品的使用类做到哪一步;不在表里按 <see cref="ItemUseStatus.Unread"/> 算。</summary>
        public static ItemUseStatus StatusOf(ItemInfo item)
        {
            ItemUseRow row = RowOf(item);
            return row == null ? ItemUseStatus.Unread : row.Status;
        }

        /// <summary>
        /// 能不能进快捷栏:只有回复药(服务端 <c>quickslot_priority</c> 0x0850f732 只认 [waste] 与 [throw]);开启类、传送与登记类的物品一律不进快捷栏。
        /// </summary>
        public static bool QuickSlotClass(ItemInfo item)
        {
            return ClassOf(item) == ItemUseClass.Recovery;
        }

        /// <summary>是开启类(随机箱、固定礼包):用一件换一批物品。</summary>
        public static bool IsOpening(ItemInfo item)
        {
            ItemUseClass c = ClassOf(item);
            return c == ItemUseClass.OpenBox || c == ItemUseClass.OpenPackage;
        }

        /// <summary>这一类允许在 <paramref name="place"/>(单个地点)使用;没读到允许地点的类一律不许。</summary>
        public static bool AllowedAt(ItemInfo item, ItemPlaces place)
        {
            ItemUseRow row = RowOf(item);
            return row != null && place != ItemPlaces.None && (row.Places & place) == place;
        }
    }
}
