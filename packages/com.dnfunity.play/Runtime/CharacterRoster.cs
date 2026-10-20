using System;
using System.Collections.Generic;

namespace Dnf.Play
{
    /// <summary>选角界面上的一个角色:名字、职业名与等级(都是占位字库画得出来的大写英文与数字)。</summary>
    public sealed class CharacterSlot
    {
        public string Name { get; }

        /// <summary>职业名;原版画的是中文系统字体,这里用占位字库(只有大写字母与数字)。</summary>
        public string JobName { get; }

        public int Level { get; }

        public CharacterSlot(string name, string jobName, int level)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            JobName = jobName ?? throw new ArgumentNullException(nameof(jobName));
            Level = Math.Max(1, level);
        }

        /// <summary>第一行字:等级加名字,例如 LV1 HERO。</summary>
        public string Caption => "LV" + Level + " " + Name.ToUpperInvariant();
    }

    /// <summary>
    /// 选角界面的角色名册(证据 E-096):按槽位顺序连续排列(第 0 个角色在左上第一格),最多 <see cref="Capacity"/> 个(6 列 × 2 行,
    /// 基线 S02 上同时看得见的格子数)。登录占位:<see cref="Local"/> 给出"本地存档"里的一个 1 级鬼剑士,名册只在进程内,不读写文件;
    /// 以后接上真实的角色存档或服务器角色列表,换掉这个来源即可,界面只认名册。
    /// </summary>
    public sealed class CharacterRoster
    {
        public const int Columns = 6;
        public const int Rows = 2;
        public const int Capacity = Columns * Rows;

        /// <summary>本地存档占位里那个鬼剑士的名字与职业名(合成的占位,与任何真实账号无关)。</summary>
        public const string LocalName = "Hero";

        public const string LocalJob = "Slayer";

        private readonly List<CharacterSlot> _slots;

        public CharacterRoster(IEnumerable<CharacterSlot> slots)
        {
            _slots = new List<CharacterSlot>(slots ?? throw new ArgumentNullException(nameof(slots)));
            if (_slots.Count > Capacity)
            {
                throw new ArgumentException("角色数超过槽位数 " + Capacity, nameof(slots));
            }
            foreach (CharacterSlot slot in _slots)
            {
                if (slot == null)
                {
                    throw new ArgumentException("名册里有空角色", nameof(slots));
                }
            }
        }

        public int Count => _slots.Count;

        public CharacterSlot this[int index] => _slots[index];

        public IReadOnlyList<CharacterSlot> Slots => _slots;

        /// <summary>登录占位:本地存档里一个 1 级的鬼剑士。</summary>
        public static CharacterRoster Local()
        {
            return new CharacterRoster(new[] { new CharacterSlot(LocalName, LocalJob, 1) });
        }

        /// <summary>槽位在网格里的列、行(第 0 个在左上,先填满一行再换行)。</summary>
        public static void Cell(int index, out int column, out int row)
        {
            column = index % Columns;
            row = index / Columns;
        }
    }
}
