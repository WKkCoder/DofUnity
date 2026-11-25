using System.Globalization;
using System.Text;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 进城物品同步快照的探针脱敏格式化器(纯函数):只输出计数与 0/1 标志,
    /// 绝不输出物品编号、金额、槽号、强化/增幅/随机选项数值、角色名或任何载荷字节。
    /// </summary>
    public static class OnlineItemSyncLog
    {
        /// <summary>探针物品汇总行的前缀。</summary>
        public const string ProbePrefix = "PROBE_ITEMS";

        /// <summary>
        /// 把 <see cref="ItemSyncSnapshot"/> 格式化成单行 <c>PROBE_ITEMS ...</c>。
        /// <paramref name="snapshot"/> 为 null 时输出全零的同构行。
        /// <para>
        /// 口径说明:<c>tailBytes</c> 汇总快照里保留的 11 类小包/视图的 <c>TrailingByteCount</c>
        /// (<c>Cera</c>、<c>AvatarCoin</c>、<c>CoolTimeItems</c>、<c>EffectItems</c>、<c>ItemLocks</c>、
        /// <c>MaxEquipLevel</c>、<c>HotkeyOption</c>、<c>PublicViews</c>、<c>SelfView</c>、<c>ExpandEquips</c>、<c>Creatures</c>);
        /// <see cref="ItemSpaceState"/> 不保留 NOTI/13、14 的尾随字节数,故不计入。
        /// <c>ItemSyncParser</c> 对畸形物品包整包拒绝并计入 <c>malformed</c>,不会把异常条目放入空间状态;
        /// 目前唯一会进入快照的"解析成功但非 Ok"状态是装扮币失败应答(<c>AvatarCoin != null &amp;&amp; !AvatarCoin.Success</c>),计入 <c>nonOkEntries</c>。
        /// </para>
        /// </summary>
        public static string FormatProbeLine(ItemSyncSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return ProbePrefix
                    + " seen=0 parsed=0 malformed=0 unresolved=0"
                    + " bag=0 avatarBag=0 cargo=0 petBag=0 bodyEquip=0 accountCargo=0 selfEquip=0"
                    + " gold=0 coin=0 winPoint=0 cera=0 avatarCoin=0"
                    + " tailBytes=0 nonOkEntries=0";
            }

            ItemCurrency currency = snapshot.Currency;
            int selfEquip = snapshot.SelfView != null ? snapshot.SelfView.DetailedEquips.Count : 0;
            bool hasAvatarCoin = snapshot.AvatarCoin != null && snapshot.AvatarCoin.Success;
            int nonOkEntries = snapshot.AvatarCoin != null && !snapshot.AvatarCoin.Success ? 1 : 0;

            var sb = new StringBuilder();
            sb.Append(ProbePrefix);
            sb.Append(" seen=").Append(Number(snapshot.FramesSeen));
            sb.Append(" parsed=").Append(Number(snapshot.FramesParsed));
            sb.Append(" malformed=").Append(Number(snapshot.FramesMalformed));
            sb.Append(" unresolved=").Append(Number(snapshot.FramesKindUnresolved));
            sb.Append(" bag=").Append(Number(SpaceCount(snapshot.Bag)));
            sb.Append(" avatarBag=").Append(Number(SpaceCount(snapshot.AvatarBag)));
            sb.Append(" cargo=").Append(Number(SpaceCount(snapshot.Cargo)));
            sb.Append(" petBag=").Append(Number(SpaceCount(snapshot.PetBag)));
            sb.Append(" bodyEquip=").Append(Number(SpaceCount(snapshot.BodyEquip)));
            sb.Append(" accountCargo=").Append(Number(SpaceCount(snapshot.AccountCargo)));
            sb.Append(" selfEquip=").Append(Number(selfEquip));
            sb.Append(" gold=").Append(Flag(currency != null && currency.Gold.HasValue));
            sb.Append(" coin=").Append(Flag(currency != null && currency.CoinTotal.HasValue));
            sb.Append(" winPoint=").Append(Flag(currency != null && currency.WinPoint.HasValue));
            sb.Append(" cera=").Append(Flag(snapshot.Cera != null));
            sb.Append(" avatarCoin=").Append(Flag(hasAvatarCoin));
            sb.Append(" tailBytes=").Append(Number(SumTailBytes(snapshot)));
            sb.Append(" nonOkEntries=").Append(Number(nonOkEntries));
            return sb.ToString();
        }

        private static int SpaceCount(ItemSpaceState space)
        {
            return space != null ? space.Count : 0;
        }

        private static int SumTailBytes(ItemSyncSnapshot snapshot)
        {
            int total = 0;
            if (snapshot.Cera != null) total += snapshot.Cera.TrailingByteCount;
            if (snapshot.AvatarCoin != null) total += snapshot.AvatarCoin.TrailingByteCount;
            if (snapshot.CoolTimeItems != null) total += snapshot.CoolTimeItems.TrailingByteCount;
            if (snapshot.EffectItems != null) total += snapshot.EffectItems.TrailingByteCount;
            if (snapshot.ItemLocks != null) total += snapshot.ItemLocks.TrailingByteCount;
            if (snapshot.MaxEquipLevel != null) total += snapshot.MaxEquipLevel.TrailingByteCount;
            if (snapshot.HotkeyOption != null) total += snapshot.HotkeyOption.TrailingByteCount;
            if (snapshot.SelfView != null) total += snapshot.SelfView.TrailingByteCount;
            if (snapshot.Creatures != null) total += snapshot.Creatures.TrailingByteCount;
            for (int i = 0; i < snapshot.PublicViews.Count; i++)
            {
                UserInfoViewNotice view = snapshot.PublicViews[i];
                if (view != null)
                {
                    total += view.TrailingByteCount;
                }
            }
            for (int i = 0; i < snapshot.ExpandEquips.Count; i++)
            {
                ExpandEquipNotice expand = snapshot.ExpandEquips[i];
                if (expand != null)
                {
                    total += expand.TrailingByteCount;
                }
            }
            return total;
        }

        private static string Flag(bool value)
        {
            return value ? "1" : "0";
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
