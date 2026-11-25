using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Dnf.Net.Frame;
using Dnf.Net.Session;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Client
{
    /// <summary>
    /// 城镇实时物品同步器(Stage 2):
    /// <list type="bullet">
    /// <item>实现 <see cref="IItemKindResolver"/>(内部委托 <see cref="OnlineItemKindLookup.ResolveKindCode"/>,不引用 <c>Dnf.Game</c>);</item>
    /// <item>用与服务端同构的权威影子槽表维护背包(槽 3..311)、身上装备(槽 10..21)、金币与未建模空间计数;</item>
    /// <item>在 <see cref="OnlineClientSession.FrameReceived"/> 后台回调线程<b>仅做过滤与入队</b>(<see cref="ConcurrentQueue{T}"/>),绝不直接改写 <see cref="HeroProgress"/>;</item>
    /// <item>由主线程在 <see cref="OnlineTownController.Poll"/> 里调 <see cref="DrainAndApply"/> 排空、按线上顺序应用并投影到 <see cref="HeroProgress"/>。</item>
    /// </list>
    /// </summary>
    public sealed class OnlineItemLiveSync : IItemKindResolver, IDisposable
    {
        private readonly OnlineClientSession _session;
        private readonly DnfAssets _assets;
        private readonly IItemKindResolver _overrideResolver;
        private readonly ushort? _selfUid;
        private readonly ConcurrentQueue<OnlineReceivedFrame> _queue = new ConcurrentQueue<OnlineReceivedFrame>();

        private readonly Dictionary<ushort, OnlineItemSlotEntry> _bagSlots = new Dictionary<ushort, OnlineItemSlotEntry>();
        private readonly Dictionary<ushort, OnlineItemSlotEntry> _wornSlots = new Dictionary<ushort, OnlineItemSlotEntry>();
        private readonly HashSet<ushort> _avatarBagSlots = new HashSet<ushort>();
        private readonly HashSet<ushort> _cargoSlots = new HashSet<ushort>();
        private readonly HashSet<ushort> _petBagSlots = new HashSet<ushort>();
        private readonly HashSet<ushort> _accountCargoSlots = new HashSet<ushort>();

        private long? _gold;
        private bool _hasBagData;
        private bool _hasEquipData;
        private int _unmodeledExpandEquipCount;
        private volatile bool _disposed;

        public OnlineItemLiveSync(
            OnlineClientSession session,
            ItemSyncSnapshot initialSnapshot,
            ushort? selfUid,
            DnfAssets assets)
            : this(session, initialSnapshot, selfUid, assets, null)
        {
        }

        internal OnlineItemLiveSync(
            OnlineClientSession session,
            ItemSyncSnapshot initialSnapshot,
            ushort? selfUid,
            DnfAssets assets,
            IItemKindResolver overrideResolver)
        {
            _session = session;
            _selfUid = selfUid;
            _assets = assets;
            _overrideResolver = overrideResolver;

            Seed(initialSnapshot);

            if (_session != null)
            {
                _session.FrameReceived += OnFrameReceived;
            }
        }

        public int QueuedCount => _queue.Count;
        public int AppliedFrames { get; private set; }
        public int IgnoredFrames { get; private set; }

        public ResolvedItemKind Resolve(uint itemId)
        {
            if (_overrideResolver != null)
            {
                return _overrideResolver.Resolve(itemId);
            }
            switch (OnlineItemKindLookup.ResolveKindCode(_assets, itemId))
            {
                case OnlineItemKindLookup.KindOther:
                    return ResolvedItemKind.Other;
                case OnlineItemKindLookup.KindAvatar:
                    return ResolvedItemKind.Avatar;
                case OnlineItemKindLookup.KindPet:
                    return ResolvedItemKind.Pet;
                default:
                    return ResolvedItemKind.Unknown;
            }
        }

        public OnlineItemSyncData ProjectData()
        {
            var quickEntries = new List<OnlineItemSlotEntry>();
            var bagEntries = new List<OnlineItemSlotEntry>();
            OnlineItemSlotEntry[] sortedBag = OnlineItemSyncAdapter.SortedEntries(_bagSlots);
            for (int i = 0; i < sortedBag.Length; i++)
            {
                OnlineItemSlotEntry entry = sortedBag[i];
                if (entry.Slot >= OnlineItemSyncAdapter.QuickSlotFirst && entry.Slot <= OnlineItemSyncAdapter.QuickSlotLast)
                {
                    quickEntries.Add(entry);
                }
                else if (entry.Slot >= OnlineItemSyncAdapter.BagItemSlotFirst)
                {
                    bagEntries.Add(entry);
                }
            }

            OnlineItemSlotEntry[] wornEquips = OnlineItemSyncAdapter.SortedEntries(_wornSlots);
            return new OnlineItemSyncData(
                hasGold: _gold.HasValue,
                gold: _gold ?? 0L,
                bagEntries: bagEntries,
                quickEntries: quickEntries,
                wornEquips: wornEquips,
                hasBagData: _hasBagData,
                hasEquipData: _hasEquipData,
                unmodeledAvatarBagCount: _avatarBagSlots.Count,
                unmodeledCargoCount: _cargoSlots.Count,
                unmodeledPetBagCount: _petBagSlots.Count,
                unmodeledAccountCargoCount: _accountCargoSlots.Count,
                unmodeledExpandEquipCount: _unmodeledExpandEquipCount);
        }

        /// <summary>把当前权威影子状态投影到 <paramref name="progress"/>(并同步 <paramref name="profile"/> 标志)。</summary>
        public OnlineItemSyncReport ApplyCurrent(HeroProgress progress, OnlineHeroProfile profile = null)
        {
            if (progress == null)
            {
                throw new ArgumentNullException(nameof(progress));
            }
            OnlineItemSyncData data = ProjectData();
            OnlineItemSyncReport report = progress.ApplyOnlineItems(data);
            profile?.ApplyItemSyncState(report.GoldSynced, report.InventorySynced, report.EquipsSynced);
            return report;
        }

        /// <summary>
        /// 仅在主线程调用:排空后台入队的 <c>NOTI/13</c> 与 <c>NOTI/14</c> 帧,按线序更新影子槽表并投影到 <paramref name="progress"/>。
        /// 没有任何有效物品包生效时返回 null(不改动 <paramref name="progress"/>)。
        /// </summary>
        public OnlineItemSyncReport DrainAndApply(HeroProgress progress, OnlineHeroProfile profile = null)
        {
            if (progress == null)
            {
                throw new ArgumentNullException(nameof(progress));
            }
            if (_disposed)
            {
                return null;
            }
            bool changed = false;
            while (_queue.TryDequeue(out OnlineReceivedFrame frame))
            {
                if (frame == null || frame.PacketClass != PacketClass.Noti)
                {
                    IgnoredFrames++;
                    continue;
                }
                if (frame.PacketId == ItemListParser.PacketId)
                {
                    if (!ItemListParser.TryParse(frame.Payload, this, out ItemListNotice notice, out _))
                    {
                        IgnoredFrames++;
                        continue;
                    }
                    AppliedFrames++;
                    ApplySpaceNotice(notice);
                    changed = true;
                }
                else if (frame.PacketId == ItemUpdateParser.PacketId)
                {
                    if (!ItemUpdateParser.TryParse(frame.Payload, this, out ItemUpdateNotice notice, out _))
                    {
                        IgnoredFrames++;
                        continue;
                    }
                    AppliedFrames++;
                    if (ApplyUpdateNotice(notice))
                    {
                        changed = true;
                    }
                }
                else
                {
                    IgnoredFrames++;
                }
            }
            return changed ? ApplyCurrent(progress, profile) : null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_session != null)
            {
                _session.FrameReceived -= OnFrameReceived;
            }
            while (_queue.TryDequeue(out _))
            {
            }
        }

        private void OnFrameReceived(OnlineReceivedFrame frame)
        {
            if (_disposed || frame == null || frame.PacketClass != PacketClass.Noti)
            {
                return;
            }
            if (frame.PacketId == ItemListParser.PacketId || frame.PacketId == ItemUpdateParser.PacketId)
            {
                _queue.Enqueue(frame);
            }
        }

        private void Seed(ItemSyncSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }
            if (snapshot.Currency != null && snapshot.Currency.Gold.HasValue)
            {
                _gold = snapshot.Currency.Gold.Value;
            }
            if (snapshot.Bag != null)
            {
                _hasBagData = true;
                IReadOnlyList<ItemEntry> bag = snapshot.Bag.Entries;
                for (int i = 0; i < bag.Count; i++)
                {
                    ItemEntry entry = bag[i];
                    if (entry != null && entry.Slot >= OnlineItemSyncAdapter.QuickSlotFirst)
                    {
                        _bagSlots[entry.Slot] = OnlineItemSyncAdapter.ToSlotEntry(entry, isEquip: false);
                    }
                }
            }
            if (snapshot.SelfView != null)
            {
                _hasEquipData = true;
                IReadOnlyList<ItemEntry> equips = snapshot.SelfView.DetailedEquips;
                for (int i = 0; i < equips.Count; i++)
                {
                    ItemEntry entry = equips[i];
                    if (entry != null && OnlineItemSyncAdapter.IsWornEquipSlot(entry.Slot))
                    {
                        _wornSlots[entry.Slot] = OnlineItemSyncAdapter.ToSlotEntry(entry, isEquip: true);
                    }
                }
            }
            if (snapshot.BodyEquip != null)
            {
                _hasEquipData = true;
                IReadOnlyList<ItemEntry> body = snapshot.BodyEquip.Entries;
                for (int i = 0; i < body.Count; i++)
                {
                    ItemEntry entry = body[i];
                    if (entry != null && OnlineItemSyncAdapter.IsWornEquipSlot(entry.Slot))
                    {
                        _wornSlots[entry.Slot] = OnlineItemSyncAdapter.ToSlotEntry(entry, isEquip: true);
                    }
                }
            }
            SeedSpaceSlots(_avatarBagSlots, snapshot.AvatarBag);
            SeedSpaceSlots(_cargoSlots, snapshot.Cargo);
            SeedSpaceSlots(_petBagSlots, snapshot.PetBag);
            SeedSpaceSlots(_accountCargoSlots, snapshot.AccountCargo);
            _unmodeledExpandEquipCount = OnlineItemSyncAdapter.CountExpandEquips(snapshot.ExpandEquips, _selfUid);
        }

        private static void SeedSpaceSlots(HashSet<ushort> target, ItemSpaceState state)
        {
            if (state == null)
            {
                return;
            }
            IReadOnlyList<ItemEntry> entries = state.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i] != null)
                {
                    target.Add(entries[i].Slot);
                }
            }
        }

        private void ApplySpaceNotice(ItemListNotice notice)
        {
            if (notice.Space == ItemSpace.Bag)
            {
                _hasBagData = true;
                _bagSlots.Clear();
                IReadOnlyList<ItemEntry> entries = notice.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    ItemEntry entry = entries[i];
                    if (entry != null && entry.Slot >= OnlineItemSyncAdapter.QuickSlotFirst)
                    {
                        _bagSlots[entry.Slot] = OnlineItemSyncAdapter.ToSlotEntry(entry, isEquip: false);
                    }
                }
                if (notice.Currency != null && notice.Currency.Gold.HasValue)
                {
                    _gold = notice.Currency.Gold.Value;
                }
                return;
            }

            HashSet<ushort> unmodeled = UnmodeledSetOf(notice.Space);
            if (unmodeled != null)
            {
                unmodeled.Clear();
                IReadOnlyList<ItemEntry> entries = notice.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i] != null)
                    {
                        unmodeled.Add(entries[i].Slot);
                    }
                }
            }
        }

        private bool ApplyUpdateNotice(ItemUpdateNotice notice)
        {
            if (notice.Space == ItemSpace.Bag)
            {
                bool changed = false;
                if (notice.Currency != null && notice.Currency.Gold.HasValue)
                {
                    _gold = notice.Currency.Gold.Value;
                    changed = true;
                }
                IReadOnlyList<ItemUpdateOperation> ops = notice.Operations;
                if (ops.Count > 0)
                {
                    _hasBagData = true;
                    changed = true;
                    for (int i = 0; i < ops.Count; i++)
                    {
                        ItemUpdateOperation op = ops[i];
                        if (op.Slot < OnlineItemSyncAdapter.QuickSlotFirst)
                        {
                            continue;
                        }
                        if (op.IsDelete)
                        {
                            _bagSlots.Remove(op.Slot);
                        }
                        else if (op.Entry != null)
                        {
                            _bagSlots[op.Slot] = OnlineItemSyncAdapter.ToSlotEntry(op.Entry, isEquip: false);
                        }
                    }
                }
                return changed;
            }

            if (notice.Space == ItemSpace.BodyEquip)
            {
                _hasEquipData = true;
                IReadOnlyList<ItemUpdateOperation> ops = notice.Operations;
                for (int i = 0; i < ops.Count; i++)
                {
                    ItemUpdateOperation op = ops[i];
                    if (!OnlineItemSyncAdapter.IsWornEquipSlot(op.Slot))
                    {
                        continue;
                    }
                    if (op.IsDelete)
                    {
                        _wornSlots.Remove(op.Slot);
                    }
                    else if (op.Entry != null)
                    {
                        _wornSlots[op.Slot] = OnlineItemSyncAdapter.ToSlotEntry(op.Entry, isEquip: true);
                    }
                }
                return true;
            }

            HashSet<ushort> unmodeled = UnmodeledSetOf(notice.Space);
            if (unmodeled != null && notice.Operations.Count > 0)
            {
                IReadOnlyList<ItemUpdateOperation> ops = notice.Operations;
                for (int i = 0; i < ops.Count; i++)
                {
                    ItemUpdateOperation op = ops[i];
                    if (op.IsDelete)
                    {
                        unmodeled.Remove(op.Slot);
                    }
                    else
                    {
                        unmodeled.Add(op.Slot);
                    }
                }
                return true;
            }

            return false;
        }

        private HashSet<ushort> UnmodeledSetOf(ItemSpace space)
        {
            switch (space)
            {
                case ItemSpace.AvatarBag: return _avatarBagSlots;
                case ItemSpace.Cargo: return _cargoSlots;
                case ItemSpace.PetBag: return _petBagSlots;
                case ItemSpace.AccountCargo: return _accountCargoSlots;
                default: return null;
            }
        }
    }
}
