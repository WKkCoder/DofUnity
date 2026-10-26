using System;
using System.Collections.Generic;

// 证据:E-123(服务端静态读码,非 REA)。UserQuest::accept_quest 0x086abf38(接受:不能已在进行中、可重复类之外不能已完成、
// 进行中的任务有上限)、finish_quest 0x086ac854(完成:触发计数归零才行;非可重复类进"已完成"表)、giveup_quest 0x086ac532(放弃:释放位置)、
// set_trigger 0x086ac6ac(进度计数)。进行中的上限 20 是服务端表的容量。
namespace Dnf.Game
{
    /// <summary>一个进行中的任务与它的进度:每个目标还差多少(猎杀类每组一项,通关、过房间、拜访只有一项)。收集类没有计数,条件看背包。</summary>
    public sealed class ActiveQuest
    {
        public ActiveQuest(int id, int[] remaining)
        {
            Id = id;
            Remaining = remaining ?? Array.Empty<int>();
        }

        public int Id { get; }

        /// <summary>每个目标还差多少次;全部为 0 才算达成。</summary>
        public int[] Remaining { get; }

        /// <summary>计数是否已经全部归零(收集类没有计数,恒为 true;是否真的能交还要看背包)。</summary>
        public bool CountersDone
        {
            get
            {
                foreach (int r in Remaining)
                {
                    if (r > 0)
                    {
                        return false;
                    }
                }
                return true;
            }
        }
    }

    /// <summary>主角的任务进度:进行中的任务(带计数)与已完成的任务编号。不含任何规则,规则在 <see cref="QuestRules"/> 与 <see cref="QuestTracker"/>。</summary>
    public sealed class QuestLog
    {
        /// <summary>同时进行的任务上限(服务端表的容量)。</summary>
        public const int MaxActive = 20;

        private readonly List<ActiveQuest> _active = new List<ActiveQuest>();
        private readonly HashSet<int> _cleared = new HashSet<int>();

        /// <summary>进行中的任务(按接受的先后)。</summary>
        public IReadOnlyList<ActiveQuest> Active => _active;

        /// <summary>已完成的任务编号。</summary>
        public IReadOnlyCollection<int> Cleared => _cleared;

        public bool IsActive(int id) => Find(id) != null;

        public bool IsCleared(int id) => _cleared.Contains(id);

        public bool HasFreeSlot => _active.Count < MaxActive;

        /// <summary>找进行中的任务;没有返回 null。</summary>
        public ActiveQuest Find(int id)
        {
            foreach (ActiveQuest a in _active)
            {
                if (a.Id == id)
                {
                    return a;
                }
            }
            return null;
        }

        /// <summary>加一个进行中的任务;已经在进行中、没有空位返回 null(不改动)。</summary>
        public ActiveQuest Add(int id, int[] remaining)
        {
            if (IsActive(id) || !HasFreeSlot)
            {
                return null;
            }
            var a = new ActiveQuest(id, remaining);
            _active.Add(a);
            return a;
        }

        /// <summary>放弃或完成后把任务从进行中拿掉;没有这个任务返回 false。</summary>
        public bool Remove(int id)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                if (_active[i].Id == id)
                {
                    _active.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>记为已完成。</summary>
        public void MarkCleared(int id)
        {
            _cleared.Add(id);
        }

        /// <summary>清空(读档前用)。</summary>
        public void Clear()
        {
            _active.Clear();
            _cleared.Clear();
        }
    }
}
