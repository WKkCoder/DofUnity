using System;
using Dnf.Game;
using Dnf.Render;

// 本项目自己的接线设计(M28):原版里使用消耗品是客户端请求、服务端扣一件并登记冷却、回血回蓝在客户端发放(证据 E-120);这里把它拆成"会话里用"。
namespace Dnf.Play
{
    /// <summary>
    /// 能用背包里消耗品的会话(副本里的战斗会话与副本会话)。城镇会话没有:城镇里没有生命值与魔法值可回复,键 1 到 6 在城镇里什么也不做。
    /// </summary>
    public interface IItemSession
    {
        /// <summary>用快捷栏第 <paramref name="slot"/> 格(键 1 到 6 是第 0 到 5 格)的消耗品;结果见 <see cref="UseResult"/>。</summary>
        UseResult TryUseQuickSlot(int slot);

        /// <summary>用这件物品(背包窗口里按回车);结果见 <see cref="UseResult"/>。</summary>
        UseResult TryUseItem(ItemInfo item);
    }

    /// <summary>
    /// 宿主层的入口:Unity 的宿主程序集不引用 <c>Dnf.Game</c>,所以结果用名字符串(<see cref="UseResult"/> 的枚举名)交接。
    /// </summary>
    public static class ItemUse
    {
        /// <summary>用成功的结果名(<see cref="UseResult.Ok"/>)。</summary>
        public const string OkName = nameof(UseResult.Ok);

        /// <summary>现在用不了的结果名(<see cref="UseResult.Unavailable"/>):会话不支持用物品(城镇),或没有生命魔法值可回复。</summary>
        public const string UnavailableName = nameof(UseResult.Unavailable);

        /// <summary>冷却中的结果名(<see cref="UseResult.Cooling"/>)。</summary>
        public const string CoolingName = nameof(UseResult.Cooling);

        /// <summary>用 <paramref name="session"/> 里快捷栏第 <paramref name="slot"/> 格的消耗品,返回结果名;会话没有物品功能返回 <see cref="UnavailableName"/>。</summary>
        public static string QuickSlot(IHeroSession session, int slot)
        {
            UseResult result = session is IItemSession items ? items.TryUseQuickSlot(slot) : UseResult.Unavailable;
            return result.ToString();
        }

        /// <summary>
        /// 日志用的一行状态:主角此刻的生命与魔法(现值/上限)、还没发完的回复、从存档起用过几件、快捷栏有几格有药、几种药在冷却;
        /// 会话不是副本会话或没有生命值对象时是 <c>none</c>。只有数,不带物品名与编号。
        /// </summary>
        public static string Status(IHeroSession session)
        {
            HeroVitals v;
            HeroProgress p;
            if (session is DungeonSession dungeon)
            {
                v = dungeon.Vitals;
                p = dungeon.Progress;
            }
            else if (session is RoomCombatSession room)
            {
                v = room.Vitals;
                p = room.Progress;
            }
            else
            {
                return "none";
            }
            if (v == null)
            {
                return "none";
            }
            return "hp=" + v.Hp + "/" + v.HpMax + " mp=" + v.Mp + "/" + v.MpMax + " pending=" + v.PendingHp + "/" + v.PendingMp
                + " used=" + (p == null ? 0 : p.ItemsUsed) + " quick=" + (p == null ? 0 : p.QuickFilled) + " cooling=" + (p == null ? 0 : p.CooldownsActive);
        }

        /// <summary>
        /// 探针用:把主角的生命与魔法压到上限的一个百分比(生命至少留 1 点,不会倒下);返回有没有压成。
        /// </summary>
        public static bool Hurt(IHeroSession session, int hpPercent, int mpPercent)
        {
            HeroVitals v = VitalsOf(session);
            if (v == null || !v.IsAlive)
            {
                return false;
            }
            int hpLeft = Math.Max(1, (int)((long)v.HpMax * Math.Max(0, Math.Min(100, hpPercent)) / 100));
            int mpLeft = (int)((long)v.MpMax * Math.Max(0, Math.Min(100, mpPercent)) / 100);
            if (v.Hp > hpLeft)
            {
                v.Damage(v.Hp - hpLeft);
            }
            if (v.Mp > mpLeft)
            {
                v.TrySpendMp(v.Mp - mpLeft);
            }
            return true;
        }

        /// <summary>探针用:主角身上还有没发完的回复(药水还在一点点回血回蓝);会话没有生命值对象返回 false。</summary>
        public static bool Recovering(IHeroSession session)
        {
            HeroVitals v = VitalsOf(session);
            return v != null && v.Recovering;
        }

        private static HeroVitals VitalsOf(IHeroSession session)
        {
            return session is DungeonSession dungeon ? dungeon.Vitals : (session as RoomCombatSession)?.Vitals;
        }
    }

    /// <summary>
    /// 探针用的物品准备(宿主的 -dnfpotionat 无人值守检查):整库里找一种这个进度能用的纯回血药和一种纯回蓝药放进背包,让快捷栏有药可用。
    /// 有冷却时间的优先(检查"冷却中"这一步才有东西可看);只用来跑自己的检查,不进任何文档或日志里的物品名与编号。
    /// </summary>
    public static class ItemProbe
    {
        /// <summary>
        /// 往 <paramref name="progress"/> 的背包放回血药与回蓝药各 <paramref name="perKind"/> 件(各找一种);返回放进去了几种(0 到 2)。
        /// 最多看 <paramref name="maxScan"/> 种可叠加物品。
        /// </summary>
        public static int GrantPotions(DnfAssets assets, HeroProgress progress, int perKind = 3, int maxScan = 30000)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            if (progress == null)
            {
                throw new ArgumentNullException(nameof(progress));
            }
            var catalog = new ItemCatalog(new GameData(new Dnf.Script.PvfScriptSource(assets.Pvf)));
            ItemInfo hp = null;
            ItemInfo mp = null;
            int scanned = 0;
            foreach (int code in catalog.StackableCodes())
            {
                if (++scanned > maxScan || (hp != null && hp.CoolTimeMs > 0 && mp != null && mp.CoolTimeMs > 0))
                {
                    break;
                }
                ItemInfo item = catalog.Get(code);
                if (item == null || ConsumableRules.Check(item, progress.Job, progress.Level) != UseResult.Ok)
                {
                    continue;
                }
                bool pureHp = item.HpRecovery != null && item.HpRecovery.Amount != 0 && item.MpRecovery == null;
                bool pureMp = item.MpRecovery != null && item.MpRecovery.Amount != 0 && item.HpRecovery == null;
                if (pureHp && (hp == null || (hp.CoolTimeMs <= 0 && item.CoolTimeMs > 0)))
                {
                    hp = item;
                }
                else if (pureMp && (mp == null || (mp.CoolTimeMs <= 0 && item.CoolTimeMs > 0)))
                {
                    mp = item;
                }
            }
            int kinds = 0;
            if (hp != null && progress.AddItem(hp, perKind))
            {
                kinds++;
            }
            if (mp != null && progress.AddItem(mp, perKind))
            {
                kinds++;
            }
            return kinds;
        }
    }
}
