using System;
using System.Collections.Generic;

// 证据:E-120(服务端静态读码:CUser::use_stackable 0x0865e0ae 的使用次序 = 先按物品编号查冷却(CheckCoolTimeItem 0x0865e994)再消耗、
// 登记冷却(_processCoolTimeItem 0x0865eb0c);CStackableItem::quickslot_priority 0x0850f732 只认 [waste] 与 [throw] 两种;
// CInventory::insertInvenItemIntoQuickSlot 0x08502470 的快捷栏是 6 格、放进第一个空格。回血回蓝的发放在客户端:
// 魔法回复的逐帧更新 0x009820a0 用客户端的整数线性插值 0x011b98c0(与血条同一个函数,这里复用 HpGauge.Lerp)把总量按持续时间线性发完)。
namespace Dnf.Game
{
    /// <summary>用一件消耗品的结果。<see cref="Ok"/> 以外都没有任何变化(没扣物品、没起冷却、没回复)。</summary>
    public enum UseResult
    {
        Ok = 0,

        /// <summary>快捷栏的这一格是空的。</summary>
        EmptySlot,

        /// <summary>不是消耗品(不是可叠加物品,或种类不是 [waste]、[throw])。</summary>
        NotConsumable,

        /// <summary>是消耗品,但这个版本只做了 [hp recovery]、[mp recovery] 两种效果,它两样都没有。</summary>
        NoEffect,

        /// <summary>背包里一件也没有。</summary>
        NotInBag,

        /// <summary>冷却中(同一个冷却组或同一个编号还在冷却)。</summary>
        Cooling,

        LevelTooLow,

        WrongJob,

        /// <summary>现在用不了(没有战斗中的生命魔法值:在城镇里,或主角已倒下;开启类与传送类不在城镇)。</summary>
        Unavailable,

        /// <summary>开启类:背包放不下要发的全部物品(先拿走这一件、再放下全部奖励,试过放不下)。没扣、没发、没起冷却。</summary>
        BagFull,

        /// <summary>这一类使用本地还没做(派发表里是登记或未读,见 <see cref="ItemUseStatus"/>),或开启类的内容这个版本发不了(商城币、宠物、读不出的编号、数据有坏)。</summary>
        NotYet,

        /// <summary>传送类的桩:使用记下来了,去向要等城镇线接上(<see cref="TeleportDesk"/>);没扣物品、没起冷却。</summary>
        Pending,
    }

    /// <summary>
    /// 消耗品脚本里 <c>[hp recovery]</c> / <c>[mp recovery]</c> 一块的内容:<c>"+"|"%" 数量 持续毫秒 "myself"</c>。
    /// 符号 <c>"%"</c> 表示数量是上限的百分比,否则是定额;持续毫秒是总量发放的时间(客户端的魔法回复更新是按已过时间把总量线性发完,证据 E-120)。
    /// </summary>
    public sealed class RecoveryEffect
    {
        public RecoveryEffect(bool percent, int amount, int durationMs)
        {
            Percent = percent;
            Amount = amount;
            DurationMs = durationMs < 0 ? 0 : durationMs;
        }

        /// <summary>数量是上限的百分比(符号是 "%");否则是定额。</summary>
        public bool Percent { get; }

        public int Amount { get; }

        /// <summary>总量发放的时间(毫秒);0 表示立刻发完。</summary>
        public int DurationMs { get; }

        /// <summary>
        /// 对上限为 <paramref name="max"/> 的生命(或魔法)一共回复多少:定额就是 <see cref="Amount"/>;百分比是 <c>max × Amount / 100</c> 向零取整
        /// (客户端建这个效果时的取整方式没有读到,按整数除法做,是推断)。
        /// </summary>
        public int TotalFor(int max)
        {
            if (!Percent)
            {
                return Amount;
            }
            return (int)((long)max * Amount / 100);
        }
    }

    /// <summary>
    /// 一条回复通道(生命或魔法):一批还在发放的"总量 + 持续时间"。每一步按已过时间把每一批线性发一部分,返回这一步一共发了多少;
    /// 发完的批移除。线性插值用 <see cref="HpGauge.Lerp"/>(客户端 0x011b98c0,单精度、向零取整):已过时间不小于持续时间就取总量,
    /// 持续时间不大于 0 的批一出现就发完。多批可以同时存在(同时喝两瓶各发各的)。
    /// </summary>
    public sealed class RecoveryChannel
    {
        private sealed class Entry
        {
            public int Total;
            public int DurationMs;
            public double ElapsedMs;
            public int Delivered;
        }

        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>还在发放的批数。</summary>
        public int Active => _entries.Count;

        /// <summary>还没发出去的总量(各批总量减已发,可能为负:负数是扣)。</summary>
        public long Pending
        {
            get
            {
                long sum = 0;
                foreach (Entry e in _entries)
                {
                    sum += e.Total - e.Delivered;
                }
                return sum;
            }
        }

        /// <summary>加一批;总量是 0 不加。</summary>
        public void Start(int total, int durationMs)
        {
            if (total == 0)
            {
                return;
            }
            _entries.Add(new Entry { Total = total, DurationMs = durationMs < 0 ? 0 : durationMs });
        }

        /// <summary>
        /// 过了 <paramref name="deltaMs"/> 毫秒:每一批的"已过时间"加上它,发出 <c>Lerp(0, 总量, 已过毫秒, 持续毫秒)</c> 与已发之差。
        /// 返回这一步发出的净量(可以是负数,也可以是 0)。<paramref name="deltaMs"/> 不大于 0 时已过时间不变,只处理持续时间为 0 的批。
        /// </summary>
        public int Advance(double deltaMs)
        {
            if (_entries.Count == 0)
            {
                return 0;
            }
            double step = deltaMs > 0.0 ? deltaMs : 0.0;
            int sum = 0;
            for (int i = 0; i < _entries.Count;)
            {
                Entry e = _entries[i];
                e.ElapsedMs += step;
                double clamped = e.ElapsedMs > int.MaxValue ? int.MaxValue : e.ElapsedMs;
                int through = HpGauge.Lerp(0, e.Total, (int)clamped, e.DurationMs);
                sum += through - e.Delivered;
                e.Delivered = through;
                if ((int)clamped >= e.DurationMs)
                {
                    _entries.RemoveAt(i);          // 已过时间到了持续时间:Lerp 取的就是总量,这一批发完了
                }
                else
                {
                    i++;
                }
            }
            return sum;
        }

        /// <summary>丢掉所有还没发完的批(主角倒下、复活、重置)。</summary>
        public void Clear()
        {
            _entries.Clear();
        }

        /// <summary>整个换成 <paramref name="other"/> 那一份的内容(深拷贝;换房间时回复跟着人走)。</summary>
        public void CopyFrom(RecoveryChannel other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }
            if (ReferenceEquals(this, other))
            {
                return;
            }
            _entries.Clear();
            foreach (Entry e in other._entries)
            {
                _entries.Add(new Entry { Total = e.Total, DurationMs = e.DurationMs, ElapsedMs = e.ElapsedMs, Delivered = e.Delivered });
            }
        }
    }

    /// <summary>
    /// 消耗品的冷却表(证据 E-120):服务端按物品编号登记冷却(<c>[cool time]</c> 毫秒,<c>_processCoolTimeItem</c>);数据里的 <c>[cooltime group]</c> 是冷却的归类
    /// (生命药剂大多一组、魔法药剂大多一组),这里把同组的物品当成共用一个冷却(推断),没写组的物品各按自己的编号。
    /// 剩余时间随战斗会话的墙钟推进(<see cref="Advance"/>),城镇里不走。
    /// </summary>
    public sealed class ItemCooldowns
    {
        private readonly Dictionary<long, double> _remaining = new Dictionary<long, double>();
        private readonly Dictionary<long, int> _total = new Dictionary<long, int>();
        private readonly List<long> _scratch = new List<long>();

        /// <summary>冷却的键:有冷却组按组(取负数,与物品编号不重),否则按物品编号。</summary>
        public static long KeyOf(ItemInfo item)
        {
            if (item == null)
            {
                return 0;
            }
            return item.CoolGroup >= 0 ? -1L - item.CoolGroup : item.Code;
        }

        /// <summary>正在冷却的键数(含没有走完的)。</summary>
        public int Active => _remaining.Count;

        /// <summary>这件物品现在是不是在冷却中。</summary>
        public bool IsCooling(ItemInfo item)
        {
            return item != null && _remaining.TryGetValue(KeyOf(item), out double left) && left > 0.0;
        }

        /// <summary>这件物品的冷却还剩多少毫秒(向上取整;没在冷却是 0)。</summary>
        public int RemainingMs(ItemInfo item)
        {
            return item != null && _remaining.TryGetValue(KeyOf(item), out double left) && left > 0.0 ? (int)Math.Ceiling(left) : 0;
        }

        /// <summary>冷却还剩的比例(0 到 1;没在冷却是 0):画快捷栏的冷却遮罩用。</summary>
        public double Fraction(ItemInfo item)
        {
            if (item == null)
            {
                return 0.0;
            }
            long key = KeyOf(item);
            if (!_remaining.TryGetValue(key, out double left) || left <= 0.0 || !_total.TryGetValue(key, out int total) || total <= 0)
            {
                return 0.0;
            }
            double f = left / total;
            return f > 1.0 ? 1.0 : f;
        }

        /// <summary>用了这件物品:开始它的冷却(<see cref="ItemInfo.CoolTimeMs"/> 不大于 0 就没有冷却)。</summary>
        public void Start(ItemInfo item)
        {
            if (item == null || item.CoolTimeMs <= 0)
            {
                return;
            }
            long key = KeyOf(item);
            _remaining[key] = item.CoolTimeMs;
            _total[key] = item.CoolTimeMs;
        }

        /// <summary>过了 <paramref name="deltaMs"/> 毫秒;走完的冷却移除。</summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs <= 0.0 || _remaining.Count == 0)
            {
                return;
            }
            _scratch.Clear();
            _scratch.AddRange(_remaining.Keys);       // 一边改值一边枚举会出错,先抄下键;复用同一个表,每一小步不分配
            foreach (long key in _scratch)
            {
                double left = _remaining[key] - deltaMs;
                if (left <= 0.0)
                {
                    _remaining.Remove(key);
                    _total.Remove(key);
                }
                else
                {
                    _remaining[key] = left;
                }
            }
        }

        public void Clear()
        {
            _remaining.Clear();
            _total.Clear();
        }
    }

    /// <summary>
    /// 快捷栏(证据 E-120):6 格(服务端 <c>insertInvenItemIntoQuickSlot</c> 把消耗品放进背包下标 3 到 8 的 6 个快捷位,放进第一个空格,没有空格返回 −2),
    /// 每格记一种物品(药水仍在背包里,按编号数数量:一格显示背包里这种物品的总件数)。键 1 到 6 对应第 0 到 5 格。
    /// </summary>
    public sealed class QuickSlots
    {
        public const int Count = 6;

        private readonly ItemInfo[] _slots = new ItemInfo[Count];

        public ItemInfo this[int slot] => (uint)slot < (uint)Count ? _slots[slot] : null;

        /// <summary>放了几格。</summary>
        public int Filled
        {
            get
            {
                int n = 0;
                foreach (ItemInfo item in _slots)
                {
                    if (item != null)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>
        /// 能不能自动进快捷栏:服务端只认 [waste] 与 [throw] 两种(<c>quickslot_priority</c>),也就是派发表里的回复药类(<see cref="ItemUseTable.QuickSlotClass"/>);
        /// 这个版本再加一条"有生命或魔法回复"(别的效果还没做,放进去也用不了)。开启类、传送类与登记类不进快捷栏。
        /// </summary>
        public static bool Eligible(ItemInfo item)
        {
            return ItemUseTable.QuickSlotClass(item) && ConsumableRules.HasRecovery(item);
        }

        /// <summary>这件物品在第几格(按编号找,没有返回 −1)。</summary>
        public int IndexOf(int code)
        {
            for (int i = 0; i < Count; i++)
            {
                if (_slots[i] != null && _slots[i].Code == code)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 自动放进第一个空格(服务端的做法);已经在快捷栏里的返回它所在的格、不重复放;不合格(<see cref="Eligible"/>)或没有空格返回 −1。
        /// </summary>
        public int Place(ItemInfo item)
        {
            if (!Eligible(item))
            {
                return -1;
            }
            int existing = IndexOf(item.Code);
            if (existing >= 0)
            {
                return existing;
            }
            for (int i = 0; i < Count; i++)
            {
                if (_slots[i] == null)
                {
                    _slots[i] = item;
                    return i;
                }
            }
            return -1;
        }

        /// <summary>直接把某一格设成某件物品(存档还原用;<paramref name="item"/> 为 null 清空这一格);格号越界或已经放在别的格返回 false。</summary>
        public bool Set(int slot, ItemInfo item)
        {
            if ((uint)slot >= (uint)Count)
            {
                return false;
            }
            if (item != null)
            {
                int existing = IndexOf(item.Code);
                if (existing >= 0 && existing != slot)
                {
                    return false;
                }
            }
            _slots[slot] = item;
            return true;
        }

        public void Clear(int slot)
        {
            if ((uint)slot < (uint)Count)
            {
                _slots[slot] = null;
            }
        }

        public void ClearAll()
        {
            for (int i = 0; i < Count; i++)
            {
                _slots[i] = null;
            }
        }
    }

    /// <summary>消耗品的使用检查(纯规则,不看背包与冷却;背包与冷却在 <c>HeroProgress.UseConsumable</c> 里按服务端次序接上)。</summary>
    public static class ConsumableRules
    {
        /// <summary>种类是 [waste] 或 [throw](服务端 <c>quickslot_priority</c> 的条件,证据 E-120),即派发表里的回复药类。</summary>
        public static bool IsConsumableType(ItemInfo item)
        {
            return ItemUseTable.QuickSlotClass(item);
        }

        /// <summary>有生命或魔法回复。</summary>
        public static bool HasRecovery(ItemInfo item)
        {
            return item != null && (item.HpRecovery != null || item.MpRecovery != null);
        }

        /// <summary>
        /// 这个等级、这个职业的主角能不能用这件物品(不看背包里有没有、冷却):不是消耗品、没有回复效果、等级不够、职业不对分别返回对应的结果。
        /// <c>[usable job]</c> 没写就谁都能用(装备那边没写是谁也穿不了,消耗品不同:很多药水不写职业)。
        /// </summary>
        public static UseResult Check(ItemInfo item, int job, int level)
        {
            if (!IsConsumableType(item))
            {
                return UseResult.NotConsumable;
            }
            if (!HasRecovery(item))
            {
                return UseResult.NoEffect;
            }
            return CheckUser(item, job, level);
        }

        /// <summary>
        /// 等级与职业这两道检查,回复药与开启类共用(先等级、后职业,次序同 <see cref="Check"/>):<c>[minimum level]</c> 高于主角等级是 <see cref="UseResult.LevelTooLow"/>,
        /// <c>[usable job]</c> 写了职业而主角不在其中是 <see cref="UseResult.WrongJob"/>,否则 <see cref="UseResult.Ok"/>。不看种类、背包、冷却。
        /// </summary>
        public static UseResult CheckUser(ItemInfo item, int job, int level)
        {
            if (item.MinimumLevel > level)
            {
                return UseResult.LevelTooLow;
            }
            if (item.UsableJobs.Count > 0 && !EquipRules.UsableBy(item, job))
            {
                return UseResult.WrongJob;
            }
            return UseResult.Ok;
        }
    }
}
