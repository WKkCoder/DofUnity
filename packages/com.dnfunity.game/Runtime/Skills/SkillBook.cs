using System;
using System.Collections.Generic;

// 证据:E-127(服务端技能槽:学新技能 master_new_skill、退技能 refund_skill、重置 SkillInit、给技能与快捷槽;静态读码,各条规则见 docs/flow.md 第 21 节)。
// 快捷栏的摆法、新学技能自动上栏、一次只学/退一级的界面行为是这个项目自己的设计(原版客户端的技能窗口没有读)。
namespace Dnf.Game
{
    /// <summary>
    /// 一个角色学会的技能:每个技能的等级、技能点与特殊点(觉醒点)的余额、六格技能快捷栏。纯逻辑,不碰界面。
    /// 学、退、重置各走服务端的规则(<see cref="SkillRules"/>):检查的次序、点数怎么扣怎么还、白送技能是退技能的下限、被别的技能依赖的技能不能退到依赖的等级以下。
    /// 主角等级与成长类型不存在这里(由调用方传入),所以升级、转职不需要通知它。
    /// </summary>
    public sealed class SkillBook
    {
        /// <summary>技能快捷栏的格数(键 A S D F G H)。</summary>
        public const int QuickSlots = 6;

        private readonly SkillCatalog _catalog;
        private readonly SortedDictionary<int, int> _levels = new SortedDictionary<int, int>();
        private readonly Dictionary<int, int> _given = new Dictionary<int, int>();
        private readonly int[] _quick = new int[QuickSlots];

        public SkillBook(SkillCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public SkillCatalog Catalog => _catalog;

        /// <summary>剩下的技能点(升级得到,学技能花掉,退技能还回)。</summary>
        public int Sp { get; private set; }

        /// <summary>剩下的特殊点(觉醒点;特殊技能花它)。</summary>
        public int Sfp { get; private set; }

        /// <summary>变化计数:学、退、重置、点数增减、快捷栏变动都加一;界面与战斗会话据此重建。</summary>
        public int Version { get; private set; }

        /// <summary>学会了几个技能(等级不小于 1 的)。</summary>
        public int LearnedCount => _levels.Count;

        /// <summary>某个技能现在的等级;没学是 0。</summary>
        public int LevelOf(int skillId)
        {
            return _levels.TryGetValue(skillId, out int level) ? level : 0;
        }

        /// <summary>某个技能的白送等级(开局或转职给的,退技能的下限);没有是 0。</summary>
        public int GivenLevelOf(int skillId)
        {
            return _given.TryGetValue(skillId, out int level) ? level : 0;
        }

        /// <summary>学会的技能与等级,按技能编号从小到大。</summary>
        public IEnumerable<KeyValuePair<int, int>> Learned => _levels;

        /// <summary>快捷栏第 <paramref name="slot"/> 格的技能编号(空格与越界为 0)。</summary>
        public int QuickAt(int slot)
        {
            return (uint)slot < (uint)QuickSlots ? _quick[slot] : 0;
        }

        /// <summary>这个技能在快捷栏的第几格;不在栏里为 -1。</summary>
        public int QuickSlotOf(int skillId)
        {
            if (skillId == 0)
            {
                return -1;
            }
            for (int i = 0; i < QuickSlots; i++)
            {
                if (_quick[i] == skillId)
                {
                    return i;
                }
            }
            return -1;
        }

        // ---------------- 点数 ----------------

        /// <summary>增加(或用负数减少)技能点与特殊点;余额不会低于 0。</summary>
        public void AddPoints(int sp, int sfp)
        {
            if (sp == 0 && sfp == 0)
            {
                return;
            }
            Sp = Math.Max(0, Sp + sp);
            Sfp = Math.Max(0, Sfp + sfp);
            Version++;
        }

        /// <summary>直接设余额(读存档、重置用);小于 0 按 0。</summary>
        public void SetPoints(int sp, int sfp)
        {
            Sp = Math.Max(0, sp);
            Sfp = Math.Max(0, sfp);
            Version++;
        }

        // ---------------- 学 ----------------

        /// <summary>
        /// 检查能不能把技能 <paramref name="skillId"/> 学高 <paramref name="delta"/> 级,不改任何状态;<paramref name="cost"/> 是要花的点数(出错时为 0)。
        /// 次序与服务端 master_new_skill 一致:找技能 → 目标等级能不能学(<see cref="SkillRules.CanLearn"/>)→ 花费 → 点数够不够 → 前置技能。
        /// </summary>
        public SkillOpResult CheckLearn(int skillId, int heroLevel, int growtype, int delta, out int cost)
        {
            return CheckLearn(skillId, heroLevel, growtype, 0, delta, out cost);
        }

        /// <summary>同 <see cref="CheckLearn(int, int, int, int, out int)"/>,最高级按觉醒 <paramref name="awakening"/> 以后的算(<see cref="SkillRules.TypeMaxLevel(SkillDef, int, int)"/>)。</summary>
        public SkillOpResult CheckLearn(int skillId, int heroLevel, int growtype, int awakening, int delta, out int cost)
        {
            cost = 0;
            SkillDef def = _catalog.Find(skillId);
            if (def == null || delta < 1)
            {
                return SkillOpResult.Unknown;
            }
            int current = LevelOf(skillId);
            int target = current + delta;
            if (!SkillRules.CanLearn(def, heroLevel, growtype, awakening, target))
            {
                return SkillOpResult.NotAllowed;
            }
            int need = SkillRules.NeedPoints(def, current, target);
            if (need < 0)
            {
                return SkillOpResult.InvalidCost;
            }
            if ((def.IsSpecial ? Sfp : Sp) < need)
            {
                return SkillOpResult.NotEnoughPoints;
            }
            foreach (SkillPrereq pre in def.PreRequired)
            {
                if (LevelOf(pre.SkillId) < pre.Level)
                {
                    return SkillOpResult.NotAllowed;
                }
            }
            cost = need;
            return SkillOpResult.Ok;
        }

        /// <summary>
        /// 把技能学高 <paramref name="delta"/> 级(通过 <see cref="CheckLearn"/> 才动手):扣点数、设等级;
        /// 第一次学会的主动技能自动放进快捷栏第一个空格(栏满了就不放)。<paramref name="spent"/> 是花掉的点数。
        /// </summary>
        public SkillOpResult Learn(int skillId, int heroLevel, int growtype, int delta, out int spent)
        {
            return Learn(skillId, heroLevel, growtype, 0, delta, out spent);
        }

        /// <summary>同 <see cref="Learn(int, int, int, int, out int)"/>,最高级按觉醒 <paramref name="awakening"/> 以后的算。</summary>
        public SkillOpResult Learn(int skillId, int heroLevel, int growtype, int awakening, int delta, out int spent)
        {
            SkillOpResult result = CheckLearn(skillId, heroLevel, growtype, awakening, delta, out spent);
            if (result != SkillOpResult.Ok)
            {
                spent = 0;
                return result;
            }
            SkillDef def = _catalog.Find(skillId);
            int before = LevelOf(skillId);
            if (def.IsSpecial)
            {
                Sfp -= spent;
            }
            else
            {
                Sp -= spent;
            }
            _levels[skillId] = before + delta;
            if (before == 0)
            {
                AutoPlace(def);
            }
            Version++;
            return SkillOpResult.Ok;
        }

        // ---------------- 退 ----------------

        /// <summary>
        /// 检查能不能把技能 <paramref name="skillId"/> 退掉 <paramref name="delta"/> 级,不改任何状态;<paramref name="refund"/> 是会还回的点数。
        /// 规则(服务端 refund_skill):得学过;退后的等级不低于白送的等级;已经学了的别的技能要求它的等级不能被退破。
        /// </summary>
        public SkillOpResult CheckRefund(int skillId, int delta, out int refund)
        {
            refund = 0;
            SkillDef def = _catalog.Find(skillId);
            if (def == null || delta < 1)
            {
                return SkillOpResult.Unknown;
            }
            int current = LevelOf(skillId);
            if (current < 1)
            {
                return SkillOpResult.NotLearned;
            }
            int target = current - delta;
            if (target < 0 || target < GivenLevelOf(skillId))
            {
                return SkillOpResult.BelowGiven;
            }
            foreach (SkillDependent dep in _catalog.DependentsOf(skillId))
            {
                if (LevelOf(dep.SkillId) >= 1 && target < dep.RequiresLevel)
                {
                    return SkillOpResult.NeededByOther;
                }
            }
            int back = SkillRules.NeedPoints(def, target, current);
            if (back < 0)
            {
                return SkillOpResult.InvalidCost;
            }
            refund = back;
            return SkillOpResult.Ok;
        }

        /// <summary>退掉 <paramref name="delta"/> 级(通过 <see cref="CheckRefund"/> 才动手):点数还回对应的池子,退到 0 级就从技能表与快捷栏里拿掉。</summary>
        public SkillOpResult Refund(int skillId, int delta, out int refunded)
        {
            SkillOpResult result = CheckRefund(skillId, delta, out refunded);
            if (result != SkillOpResult.Ok)
            {
                refunded = 0;
                return result;
            }
            SkillDef def = _catalog.Find(skillId);
            if (def.IsSpecial)
            {
                Sfp += refunded;
            }
            else
            {
                Sp += refunded;
            }
            int target = LevelOf(skillId) - delta;
            if (target <= 0)
            {
                _levels.Remove(skillId);
                int slot = QuickSlotOf(skillId);
                if (slot >= 0)
                {
                    _quick[slot] = 0;
                }
            }
            else
            {
                _levels[skillId] = target;
            }
            Version++;
            return SkillOpResult.Ok;
        }

        // ---------------- 白送与重置 ----------------

        /// <summary>
        /// 设定白送的技能(<see cref="SkillGrants"/>):它们的等级成为退技能的下限,当前等级不足的补到白送的等级,
        /// 新得到的主动技能按给出的次序放进快捷栏的空格。不认得的技能(目录里没有)忽略。
        /// </summary>
        public void SetGiven(IEnumerable<SkillGrant> grants)
        {
            _given.Clear();
            ApplyGrants(grants);
        }

        /// <summary>
        /// 在已有的白送技能之上再给一批(觉醒时给的,服务端 set_grow_type 对第二成长类型 &gt; 0 的处理:只对这一批调 growtype_skill,不清已学的技能,点数也不动):
        /// 这批的等级并入退技能的下限(同一个技能取较高的),当前等级不足的补到给出的等级,新得到的主动技能放进快捷栏的空格。不认得的技能忽略。
        /// </summary>
        public void GrantGiven(IEnumerable<SkillGrant> grants)
        {
            ApplyGrants(grants);
        }

        private void ApplyGrants(IEnumerable<SkillGrant> grants)
        {
            if (grants != null)
            {
                foreach (SkillGrant grant in grants)
                {
                    SkillDef def = _catalog.Find(grant.SkillId);
                    if (def == null || grant.Level < 1)
                    {
                        continue;
                    }
                    int old;
                    if (!_given.TryGetValue(grant.SkillId, out old) || grant.Level > old)
                    {
                        _given[grant.SkillId] = grant.Level;
                    }
                    if (LevelOf(grant.SkillId) < grant.Level)
                    {
                        bool fresh = LevelOf(grant.SkillId) == 0;
                        _levels[grant.SkillId] = grant.Level;
                        if (fresh)
                        {
                            AutoPlace(def);
                        }
                    }
                }
            }
            Version++;
        }

        /// <summary>
        /// 重置(服务端 SkillInit 与转职时的整理):所有技能清回白送的那几个,快捷栏清空后按白送次序重新放,
        /// 点数回到 <paramref name="sp"/>、<paramref name="sfp"/>(调用方算出的"到现在等级为止应有的总点数")。
        /// </summary>
        public void Reset(IEnumerable<SkillGrant> grants, int sp, int sfp)
        {
            _levels.Clear();
            Array.Clear(_quick, 0, _quick.Length);
            SetGiven(grants);
            Sp = Math.Max(0, sp);
            Sfp = Math.Max(0, sfp);
            Version++;
        }

        // ---------------- 快捷栏 ----------------

        /// <summary>
        /// 把学会的主动技能放进快捷栏第 <paramref name="slot"/> 格:这个技能已经在别的格就两格互换位置(空格的话就是挪过去),
        /// 不然顶掉那一格原来的技能(它回到"没上栏")。没学会、不是主动技能、格号越界返回 false。
        /// </summary>
        public bool AssignQuick(int slot, int skillId)
        {
            SkillDef def = _catalog.Find(skillId);
            if ((uint)slot >= (uint)QuickSlots || def == null || !def.IsActive || LevelOf(skillId) < 1)
            {
                return false;
            }
            int from = QuickSlotOf(skillId);
            if (from == slot)
            {
                return true;
            }
            if (from >= 0)
            {
                _quick[from] = _quick[slot];
            }
            _quick[slot] = skillId;
            Version++;
            return true;
        }

        /// <summary>清空快捷栏第 <paramref name="slot"/> 格;本来就空或越界返回 false。</summary>
        public bool ClearQuick(int slot)
        {
            if ((uint)slot >= (uint)QuickSlots || _quick[slot] == 0)
            {
                return false;
            }
            _quick[slot] = 0;
            Version++;
            return true;
        }

        private void AutoPlace(SkillDef def)
        {
            if (!def.IsActive || QuickSlotOf(def.Id) >= 0)
            {
                return;
            }
            for (int i = 0; i < QuickSlots; i++)
            {
                if (_quick[i] == 0)
                {
                    _quick[i] = def.Id;
                    return;
                }
            }
        }

        // ---------------- 存档 ----------------

        /// <summary>
        /// 从存档还原:技能等级与快捷栏整个换掉,点数设成 <paramref name="sp"/>、<paramref name="sfp"/>。
        /// 目录里没有的技能丢掉;等级夹在 1 到 <see cref="SkillDef.SaveCeilingLevel"/>(有效最高级与觉醒后的最高级里最大的)之内;低于白送等级的补回白送等级(先 <see cref="SetGiven"/> 再还原);
        /// 快捷栏里只留学会了的主动技能,同一个技能只留第一格。返回丢掉了几条(技能与快捷栏格各算一条)。
        /// </summary>
        public int Restore(IEnumerable<KeyValuePair<int, int>> levels, IEnumerable<KeyValuePair<int, int>> quick, int sp, int sfp)
        {
            int dropped = 0;
            _levels.Clear();
            Array.Clear(_quick, 0, _quick.Length);
            if (levels != null)
            {
                foreach (KeyValuePair<int, int> row in levels)
                {
                    SkillDef def = _catalog.Find(row.Key);
                    if (def == null || row.Value < 1)
                    {
                        dropped++;
                        continue;
                    }
                    int cap = Math.Max(1, def.SaveCeilingLevel);
                    _levels[row.Key] = Math.Min(row.Value, cap);
                }
            }
            foreach (KeyValuePair<int, int> grant in _given)
            {
                if (LevelOf(grant.Key) < grant.Value)
                {
                    _levels[grant.Key] = grant.Value;
                }
            }
            if (quick != null)
            {
                foreach (KeyValuePair<int, int> row in quick)
                {
                    SkillDef def = _catalog.Find(row.Value);
                    if ((uint)row.Key >= (uint)QuickSlots || def == null || !def.IsActive || LevelOf(row.Value) < 1
                        || _quick[row.Key] != 0 || QuickSlotOf(row.Value) >= 0)
                    {
                        dropped++;
                        continue;
                    }
                    _quick[row.Key] = row.Value;
                }
            }
            Sp = Math.Max(0, sp);
            Sfp = Math.Max(0, sfp);
            Version++;
            return dropped;
        }
    }
}
