using System.Collections.Generic;
using Dnf.Net.Frame;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 物品数据下行包的聚合解析器:输入解码后的帧序列,输出只读的 <see cref="ItemSyncSnapshot"/>(风格同 <see cref="TownEntryParser"/>)。
    /// <para>
    /// 约定:短包、错位、未知帧号一律跳过(只计数),绝不抛异常;空引用的列表项直接忽略。
    /// 按帧序逐帧归约,规则见 <see cref="ItemSyncSnapshot"/>:物品空间包整份覆盖、物品增量包逐槽补丁、货币单独存放。
    /// </para>
    /// <para>
    /// 认得的帧(类 0 通知,除非另注):2 用户信息(只认模式 0 公共视图与模式 1 本人视图,模式 2 角色列表归 <see cref="UserInfoParser"/>,在这里算"不认得");
    /// 13 物品空间包;14 物品增量包;53 点券;105 宠物属性表;172 物品冷却恢复;174 物品效果恢复;251 物品锁表;339 备用装备组;438 装备等级上限;452 快捷键选项;
    /// 类 1 的 587 装扮币数量应答。其它一律跳过,只计入 <see cref="ItemSyncSnapshot.FramesSeen"/>。
    /// </para>
    /// <para>
    /// 物品类型回调(<paramref name="kindResolver"/>):条目后半段的可选块取决于物品类型,线上不带,联网端拿不到离线物品目录时由调用方注入。
    /// 宠物栏包、身上装备增量包里的非删除条目没有回调(或回调回答不认识)就整包拒绝并计入错位;条数为 0 的包不受影响;
    /// 装备详细记录(用户信息本人视图、备用装备组)没有回调时按槽位近似(槽 ≤ 9 为装扮类、槽 22 为宠物类),该近似样本从未触发过、未经验证。
    /// 回调必须无副作用、不抛异常(解析器不会捕获它的异常)。
    /// </para>
    /// </summary>
    public static class ItemSyncParser
    {
        private enum Outcome
        {
            Skipped,
            Parsed,
            Malformed,
            KindUnresolved,
        }

        /// <summary>
        /// 解析一段帧。
        /// </summary>
        /// <param name="frames">按到达顺序排列的解码帧(进城回程的选角窗口加尾窗口等);可以为 null 或空。</param>
        /// <param name="kindResolver">物品类型回调;可以为 null,含义见类说明。</param>
        /// <returns>永不为 null;什么也没认出来时各状态为空,诊断计数照常给出。</returns>
        public static ItemSyncSnapshot Parse(IReadOnlyList<DecodedFrame> frames, IItemKindResolver kindResolver = null)
        {
            Reducer reducer = new Reducer();
            int seen = 0;
            int parsed = 0;
            int malformed = 0;
            int unresolved = 0;

            if (frames != null)
            {
                for (int i = 0; i < frames.Count; i++)
                {
                    DecodedFrame frame = frames[i];
                    if (frame == null)
                    {
                        continue;
                    }
                    seen++;
                    switch (Dispatch(frame, kindResolver, reducer))
                    {
                        case Outcome.Parsed:
                            parsed++;
                            break;
                        case Outcome.Malformed:
                            malformed++;
                            break;
                        case Outcome.KindUnresolved:
                            malformed++;
                            unresolved++;
                            break;
                    }
                }
            }

            return reducer.Build(seen, parsed, malformed, unresolved);
        }

        private static Outcome Dispatch(DecodedFrame frame, IItemKindResolver kindResolver, Reducer reducer)
        {
            byte[] payload = frame.Payload;
            int id = frame.Header.PacketId;

            if (frame.Header.Class == PacketClass.Cmd)
            {
                if (id == AvatarCoinParser.PacketId)
                {
                    if (AvatarCoinParser.TryParse(payload, out AvatarCoinNotice coin))
                    {
                        reducer.AvatarCoin = coin;
                        return Outcome.Parsed;
                    }
                    return Outcome.Malformed;
                }
                return Outcome.Skipped;
            }

            if (frame.Header.Class != PacketClass.Noti)
            {
                return Outcome.Skipped;
            }

            switch (id)
            {
                case ItemListParser.PacketId:
                    if (ItemListParser.TryParse(payload, kindResolver, out ItemListNotice space, out ItemParseStatus spaceStatus))
                    {
                        reducer.ApplySpace(space);
                        return Outcome.Parsed;
                    }
                    return spaceStatus == ItemParseStatus.KindUnresolved ? Outcome.KindUnresolved : Outcome.Malformed;

                case ItemUpdateParser.PacketId:
                    if (ItemUpdateParser.TryParse(payload, kindResolver, out ItemUpdateNotice update, out ItemParseStatus updateStatus))
                    {
                        reducer.ApplyUpdate(update);
                        return Outcome.Parsed;
                    }
                    return updateStatus == ItemParseStatus.KindUnresolved ? Outcome.KindUnresolved : Outcome.Malformed;

                case CeraParser.PacketId:
                    if (CeraParser.TryParse(payload, out CeraNotice cera))
                    {
                        reducer.Cera = cera;
                        return Outcome.Parsed;
                    }
                    return Outcome.Malformed;

                case CreatureAttributeParser.PacketId:
                    if (CreatureAttributeParser.TryParse(payload, out CreatureAttributeNotice creatures))
                    {
                        reducer.Creatures = creatures;
                        return Outcome.Parsed;
                    }
                    return Outcome.Malformed;

                case ContinuousItemListParser.CoolTimePacketId:
                    if (ContinuousItemListParser.TryParse(payload, out ContinuousItemListNotice coolTime))
                    {
                        reducer.CoolTimeItems = coolTime;
                        return Outcome.Parsed;
                    }
                    return Outcome.Malformed;

                case ContinuousItemListParser.EffectPacketId:
                    if (ContinuousItemListParser.TryParse(payload, out ContinuousItemListNotice effect))
                    {
                        reducer.EffectItems = effect;
                        return Outcome.Parsed;
                    }
                    return Outcome.Malformed;

                case ItemLockParser.PacketId:
                    if (ItemLockParser.TryParse(payload, out ItemLockNotice locks))
                    {
                        reducer.ItemLocks = locks;
                        return Outcome.Parsed;
                    }
                    return Outcome.Malformed;

                case ExpandEquipParser.PacketId:
                    if (ExpandEquipParser.TryParse(payload, kindResolver, out ExpandEquipNotice expand))
                    {
                        reducer.ExpandEquips.Add(expand);
                        return Outcome.Parsed;
                    }
                    return Outcome.Malformed;

                case MaxEquipLevelParser.PacketId:
                    if (MaxEquipLevelParser.TryParse(payload, out MaxEquipLevelNotice maxLevel))
                    {
                        reducer.MaxEquipLevel = maxLevel;
                        return Outcome.Parsed;
                    }
                    return Outcome.Malformed;

                case HotkeyOptionParser.PacketId:
                    if (HotkeyOptionParser.TryParse(payload, out HotkeyOptionNotice hotkey))
                    {
                        reducer.HotkeyOption = hotkey;
                        return Outcome.Parsed;
                    }
                    return Outcome.Malformed;

                case UserInfoViewParser.PacketId:
                    if (UserInfoViewParser.TryParse(payload, kindResolver, out UserInfoViewNotice view, out UserInfoViewStatus viewStatus))
                    {
                        if (view.IsPublicView)
                        {
                            reducer.PublicViews.Add(view);
                        }
                        else
                        {
                            reducer.SelfView = view;
                        }
                        return Outcome.Parsed;
                    }
                    // 模式 2(角色列表)等不是本线的包:不算错位,只计入"看过"。
                    return viewStatus == UserInfoViewStatus.UnsupportedMode ? Outcome.Skipped : Outcome.Malformed;

                default:
                    return Outcome.Skipped;
            }
        }

        /// <summary>某个空间归约过程中的可变状态(只在一次 <see cref="Parse"/> 内存在)。</summary>
        private sealed class SpaceBuilder
        {
            public readonly Dictionary<ushort, ItemEntry> Slots = new Dictionary<ushort, ItemEntry>();
            public bool HasFullState;
            public bool HasCapacity;
            public ushort Capacity;
            public bool HasAccountCargoHeader;
            public ushort AccountCargoWord;
            public uint AccountCargoDword;
            public int FullPackets;
            public int UpdatePackets;
        }

        /// <summary>一次 <see cref="Parse"/> 内的归约器:帧序里每个有效帧依次 Apply。</summary>
        private sealed class Reducer
        {
            private readonly Dictionary<ItemSpace, SpaceBuilder> _spaces = new Dictionary<ItemSpace, SpaceBuilder>();
            private ItemCurrency _currency = ItemCurrency.Empty;

            public CeraNotice Cera;
            public AvatarCoinNotice AvatarCoin;
            public ContinuousItemListNotice CoolTimeItems;
            public ContinuousItemListNotice EffectItems;
            public ItemLockNotice ItemLocks;
            public MaxEquipLevelNotice MaxEquipLevel;
            public HotkeyOptionNotice HotkeyOption;
            public UserInfoViewNotice SelfView;
            public CreatureAttributeNotice Creatures;
            public readonly List<UserInfoViewNotice> PublicViews = new List<UserInfoViewNotice>();
            public readonly List<ExpandEquipNotice> ExpandEquips = new List<ExpandEquipNotice>();

            private SpaceBuilder Builder(ItemSpace space)
            {
                if (!_spaces.TryGetValue(space, out SpaceBuilder builder))
                {
                    builder = new SpaceBuilder();
                    _spaces[space] = builder;
                }
                return builder;
            }

            /// <summary>物品空间包:整份状态,整体覆盖该空间已有的槽;货币只覆盖有值的槽。</summary>
            public void ApplySpace(ItemListNotice notice)
            {
                SpaceBuilder builder = Builder(notice.Space);
                builder.Slots.Clear();
                for (int i = 0; i < notice.Entries.Count; i++)
                {
                    ItemEntry entry = notice.Entries[i];
                    builder.Slots[entry.Slot] = entry;
                }
                builder.HasFullState = true;
                builder.HasCapacity = notice.HasCapacity;
                builder.Capacity = notice.Capacity;
                builder.HasAccountCargoHeader = notice.HasAccountCargoHeader;
                builder.AccountCargoWord = notice.AccountCargoWord;
                builder.AccountCargoDword = notice.AccountCargoDword;
                builder.FullPackets++;
                _currency = _currency.Overlay(notice.Currency);
            }

            /// <summary>物品增量包:逐槽补丁,按线上顺序叠在已有状态上;货币刷新只覆盖有值的槽。</summary>
            public void ApplyUpdate(ItemUpdateNotice notice)
            {
                SpaceBuilder builder = Builder(notice.Space);
                for (int i = 0; i < notice.Operations.Count; i++)
                {
                    ItemUpdateOperation operation = notice.Operations[i];
                    if (operation.IsDelete)
                    {
                        builder.Slots.Remove(operation.Slot);
                    }
                    else
                    {
                        builder.Slots[operation.Slot] = operation.Entry;
                    }
                }
                builder.UpdatePackets++;
                if (notice.Space == ItemSpace.Bag)
                {
                    _currency = _currency.Overlay(notice.Currency);
                }
            }

            public ItemSyncSnapshot Build(int seen, int parsed, int malformed, int kindUnresolved)
            {
                Dictionary<ItemSpace, ItemSpaceState> states = new Dictionary<ItemSpace, ItemSpaceState>();
                foreach (KeyValuePair<ItemSpace, SpaceBuilder> pair in _spaces)
                {
                    SpaceBuilder b = pair.Value;
                    states[pair.Key] = new ItemSpaceState(
                        pair.Key,
                        b.HasFullState,
                        b.HasCapacity,
                        b.Capacity,
                        b.HasAccountCargoHeader,
                        b.AccountCargoWord,
                        b.AccountCargoDword,
                        b.Slots,
                        b.FullPackets,
                        b.UpdatePackets);
                }
                return new ItemSyncSnapshot(
                    states,
                    _currency,
                    Cera,
                    AvatarCoin,
                    CoolTimeItems,
                    EffectItems,
                    ItemLocks,
                    MaxEquipLevel,
                    HotkeyOption,
                    PublicViews.ToArray(),
                    SelfView,
                    ExpandEquips.ToArray(),
                    Creatures,
                    seen,
                    parsed,
                    malformed,
                    kindUnresolved);
            }
        }
    }
}
