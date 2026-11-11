using System;
using System.Collections.Generic;

// 证据:服务端静态读码(df_game_r,只读,没有运行)——
//  · CAchievement::setTrigger 0x0828b678 的次序与回复码:任务不存在、不是分类 7、或角色等级低于任务最低等级 → 回复 1(无效);已完成 → 2;_verifyTrigger 0x0828b800 不过 → 2;
//    通过后 _setTrigger 0x0828b896 → 回复 0 → _checkComplete 0x0828bbae(完成才发奖);
//  · _setTrigger:没有记录就用 Quest::get_init_achievement_trigger 0x08352fb4 的初值建一条,三个 16 位计数各自"小于触发值清零,否则相减";
//  · 完成 = 三个计数全为 0(stAchievement::isComplete 0x0828d8a2);没有记录就是没完成(isClearedAchievement 0x0828cbe4);
//  · 存档:DB_LoadEtc::LoadAchievement 0x08406422(最多 0x200 条,每条 12 字节:成就编号 4 字节 + 三个 16 位计数 + 2 字节填充);
//  · _verifyTrigger 对三个硬编码的成就另加条件(两个要角色等级 ≥ 70,一个要服务端本地时刻 ≥ 21 点;编号不记),其余恒通过——这里留一个委托,宿主要做可以自己挂。
namespace Dnf.Game
{
    /// <summary>触发的回复码(数值与服务端一致)。</summary>
    public enum AchievementTriggerCode
    {
        /// <summary>计入进度。</summary>
        Ok = 0,

        /// <summary>无效:成就不存在(不是分类 [title] 的任务),或角色等级低于最低等级。</summary>
        Invalid = 1,

        /// <summary>拒绝:已经完成,或额外条件(<see cref="AchievementTracker.TriggerGate"/>)不过。</summary>
        Rejected = 2,
    }

    /// <summary>一个成就此刻的进度:三个"还差多少"的计数,全 0 就是完成。</summary>
    public readonly struct AchievementProgress
    {
        public AchievementProgress(int id, int remaining0, int remaining1, int remaining2)
        {
            Id = id;
            Remaining0 = remaining0;
            Remaining1 = remaining1;
            Remaining2 = remaining2;
        }

        public int Id { get; }

        public int Remaining0 { get; }

        public int Remaining1 { get; }

        public int Remaining2 { get; }

        public bool IsComplete => Remaining0 == 0 && Remaining1 == 0 && Remaining2 == 0;
    }

    /// <summary>成就进度簿:每个成就一条三个计数的记录,按服务端的触发规则累减。<see cref="Version"/> 每次有变化加一。</summary>
    public sealed class AchievementTracker
    {
        /// <summary>计数与触发值的上限(16 位)。</summary>
        public const int MaxCount = 65535;

        /// <summary>服务端存档一个角色最多这么多条成就记录(0x200)。</summary>
        public const int MaxEntries = 0x200;

        private readonly AchievementCatalog _catalog;
        private readonly SortedDictionary<int, int[]> _entries = new SortedDictionary<int, int[]>();

        public AchievementTracker(AchievementCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public AchievementCatalog Catalog => _catalog;

        public int Version { get; private set; }

        /// <summary>
        /// 额外的触发条件(参数:成就编号、角色等级;返回 false 就拒绝)。没挂就恒通过(服务端对绝大多数成就就是这样)。
        /// </summary>
        public Func<int, int, bool> TriggerGate { get; set; }

        /// <summary>有进度记录的成就数。</summary>
        public int EntryCount => _entries.Count;

        /// <summary>已完成的成就数。</summary>
        public int ClearedCount
        {
            get
            {
                int n = 0;
                foreach (int[] counts in _entries.Values)
                {
                    if (IsZero(counts))
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>这个成就完成了没有(没有记录就是没完成)。</summary>
        public bool IsCleared(int id)
        {
            return _entries.TryGetValue(id, out int[] counts) && IsZero(counts);
        }

        /// <summary>有记录的成就的进度;没有记录返回 false。</summary>
        public bool TryGet(int id, out AchievementProgress progress)
        {
            if (_entries.TryGetValue(id, out int[] counts))
            {
                progress = new AchievementProgress(id, counts[0], counts[1], counts[2]);
                return true;
            }
            progress = default;
            return false;
        }

        /// <summary>
        /// 一个成就现在的进度:有记录就是记录;没有记录就是定义的初始计数(界面显示"还差多少");成就不存在返回全 0 的占位(<see cref="AchievementCatalog.Contains"/> 先判)。
        /// </summary>
        public AchievementProgress ProgressOf(int id)
        {
            if (TryGet(id, out AchievementProgress progress))
            {
                return progress;
            }
            AchievementDef def = _catalog.Get(id);
            return def == null
                ? new AchievementProgress(id, 0, 0, 0)
                : new AchievementProgress(id, def.InitialCount(0), def.InitialCount(1), def.InitialCount(2));
        }

        /// <summary>全部有记录的成就,按编号升序。</summary>
        public IReadOnlyList<AchievementProgress> Entries()
        {
            var list = new List<AchievementProgress>(_entries.Count);
            foreach (KeyValuePair<int, int[]> pair in _entries)
            {
                list.Add(new AchievementProgress(pair.Key, pair.Value[0], pair.Value[1], pair.Value[2]));
            }
            return list;
        }

        /// <summary>
        /// 一次触发(服务端 <c>setTrigger</c> 去掉发奖的部分):次序与回复码见文件头。通过时三个计数各自饱和地减去 <paramref name="a"/>、<paramref name="b"/>、<paramref name="c"/>,
        /// <paramref name="completedNow"/> 表示这一次把它减到了完成(发奖由调用方做)。触发值须在 0 到 <see cref="MaxCount"/>(服务端的包字段是 16 位)。
        /// </summary>
        public AchievementTriggerCode Trigger(int id, int a, int b, int c, int charLevel, out bool completedNow)
        {
            if (a < 0 || a > MaxCount || b < 0 || b > MaxCount || c < 0 || c > MaxCount)
            {
                throw new ArgumentOutOfRangeException(nameof(a), "触发值须在 0 到 65535 之间");
            }
            completedNow = false;
            AchievementDef def = _catalog.Get(id);
            if (def == null || def.MinLevel > charLevel)
            {
                return AchievementTriggerCode.Invalid;
            }
            if (IsCleared(id))
            {
                return AchievementTriggerCode.Rejected;
            }
            if (TriggerGate != null && !TriggerGate(id, charLevel))
            {
                return AchievementTriggerCode.Rejected;
            }
            if (!_entries.TryGetValue(id, out int[] counts))
            {
                counts = new[] { def.InitialCount(0), def.InitialCount(1), def.InitialCount(2) };
                _entries.Add(id, counts);
            }
            counts[0] = Decrease(counts[0], a);
            counts[1] = Decrease(counts[1], b);
            counts[2] = Decrease(counts[2], c);
            Version++;
            completedNow = IsZero(counts);
            return AchievementTriggerCode.Ok;
        }

        /// <summary>
        /// 直接补成完成(服务端 <c>_forceClear</c> 与 <c>adjust</c> 的做法:用初始计数建记录、再减去同样的初始计数,三个都成 0)。
        /// 成就不存在返回 false;已经完成返回 true 且不算变化。
        /// </summary>
        public bool MarkComplete(int id)
        {
            AchievementDef def = _catalog.Get(id);
            if (def == null)
            {
                return false;
            }
            if (_entries.TryGetValue(id, out int[] counts))
            {
                if (IsZero(counts))
                {
                    return true;
                }
                counts[0] = counts[1] = counts[2] = 0;
            }
            else
            {
                _entries.Add(id, new int[3]);
            }
            Version++;
            return true;
        }

        /// <summary>清空全部记录(有记录才算一次变化)。</summary>
        public void Clear()
        {
            if (_entries.Count > 0)
            {
                _entries.Clear();
                Version++;
            }
        }

        /// <summary>
        /// 读档用:直接放一条记录。成就不存在(数据变了)、计数越界、或已经满了 <see cref="MaxEntries"/> 条返回 false;同一编号后一条覆盖前一条。
        /// </summary>
        public bool Restore(int id, int remaining0, int remaining1, int remaining2)
        {
            if (!_catalog.Contains(id) || remaining0 < 0 || remaining0 > MaxCount || remaining1 < 0 || remaining1 > MaxCount
                || remaining2 < 0 || remaining2 > MaxCount)
            {
                return false;
            }
            if (!_entries.ContainsKey(id) && _entries.Count >= MaxEntries)
            {
                return false;
            }
            _entries[id] = new[] { remaining0, remaining1, remaining2 };
            Version++;
            return true;
        }

        private static int Decrease(int count, int by)
        {
            return count < by ? 0 : count - by;
        }

        private static bool IsZero(int[] counts)
        {
            return counts[0] == 0 && counts[1] == 0 && counts[2] == 0;
        }
    }
}
