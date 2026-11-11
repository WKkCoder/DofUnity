using System;
using System.Collections.Generic;

// 证据:服务端静态读码(df_game_r,只读,没有运行)——完成后的发奖次序 CAchievement::_checkComplete 0x0828bbae:
//   称号奖励(get_title_reward 非 0 才 putTitleOnly,返回值不看)→ 任务自带邮件(_sendEachAchieveReward 0x0828c086)→ 整页邮件(_sendCompleteReward 0x0828bf2a)→ 通知与历史日志;
//   整页邮件用"称号落在的 (分类, 格)"去找页(_getSectionIdx),页有邮件奖励且整页收齐(_getTabComplete)才发;称号没放进去(没有奖励或找不到格)时 (分类, 格) 保持调用前的 (0, 0),
//   所以这时服务端拿"一般分类第 0 格所在的页"去判——读码写法,这里照做,笔记里记作服务端的怪异之处。
//  · 登录核对 _verifyAchievement 0x0828d092 与补发 adjust 0x0828c976、强制完成 _forceClear 0x0828cc56:见 Verify、Adjust、ForceClear 的注释。
//  · 没做:三个硬编码成就的特例(核对与额外触发条件)、_forceReset 0x0828ce40(只被硬编码特例调用)、通知包与历史日志、称号的过期清理。
namespace Dnf.Game
{
    /// <summary>邮件的来源。</summary>
    public enum TitlePostalSource
    {
        /// <summary>成就自带的邮件奖励。</summary>
        Achievement,

        /// <summary>整页收齐的邮件奖励。</summary>
        Section,
    }

    /// <summary>发给玩家邮箱的一封奖励邮件(宿主取走后真正投递;本类库不管邮箱)。</summary>
    public sealed class TitlePostal
    {
        public TitlePostal(TitlePostalSource source, int sourceId, int itemCode, int count, TitleText title, TitleText body)
        {
            Source = source;
            SourceId = sourceId;
            ItemCode = itemCode;
            Count = count;
            Title = title;
            Body = body;
        }

        public TitlePostalSource Source { get; }

        /// <summary>来源编号:成就邮件是成就(任务)编号;整页邮件是 <see cref="TitleBookDef.Sections"/> 里这一页的下标。</summary>
        public int SourceId { get; }

        public int ItemCode { get; }

        public int Count { get; }

        public TitleText Title { get; }

        public TitleText Body { get; }
    }

    /// <summary>一次成就触发的结果。</summary>
    public sealed class TitleTriggerOutcome
    {
        internal TitleTriggerOutcome(AchievementTriggerCode code, bool completed, int titleCode, bool placed, TitleCategory category, int slot,
            IReadOnlyList<TitlePostal> postals)
        {
            Code = code;
            Completed = completed;
            TitleCode = titleCode;
            TitlePlaced = placed;
            Category = category;
            Slot = slot;
            Postals = postals;
        }

        /// <summary>回复码(<see cref="AchievementTriggerCode"/>)。</summary>
        public AchievementTriggerCode Code { get; }

        /// <summary>这一次触发把成就做完了。</summary>
        public bool Completed { get; }

        /// <summary>称号奖励的物品编号(没有为 0)。</summary>
        public int TitleCode { get; }

        /// <summary>称号放进了图鉴。</summary>
        public bool TitlePlaced { get; }

        /// <summary>称号落到的分类与格(没有放进、也没找到格时是 (一般, 0),见文件头)。</summary>
        public TitleCategory Category { get; }

        public int Slot { get; }

        /// <summary>这一次排进邮箱的邮件。</summary>
        public IReadOnlyList<TitlePostal> Postals { get; }
    }

    /// <summary>
    /// 称号与成就的整体:图鉴(<see cref="TitleBook"/>)、成就进度(<see cref="AchievementTracker"/>)与待投递的邮件。触发、登录核对、补发都在这里把它们串起来。
    /// 物品按编号取(<c>lookup</c>,找不到的称号放不进图鉴)。不含背包与装备栏(那是宿主层的 <c>TitleSession</c>)。
    /// </summary>
    public sealed class TitleSystem
    {
        private readonly Func<int, ItemInfo> _lookup;
        private readonly List<TitlePostal> _outbox = new List<TitlePostal>();

        public TitleSystem(TitleBookDef definition, AchievementCatalog achievements, Func<int, ItemInfo> lookup)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }
            if (achievements == null)
            {
                throw new ArgumentNullException(nameof(achievements));
            }
            _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
            Book = new TitleBook(definition);
            Achievements = new AchievementTracker(achievements);
        }

        public TitleBookDef Definition => Book.Definition;

        public TitleBook Book { get; }

        public AchievementTracker Achievements { get; }

        /// <summary>图鉴与成就进度的变化次数之和(存档要不要重写、界面要不要刷新看它)。</summary>
        public int Version => Book.Version + Achievements.Version;

        /// <summary>还没有被取走的邮件。</summary>
        public IReadOnlyList<TitlePostal> Outbox => _outbox;

        /// <summary>取走全部待投递的邮件(取完清空)。</summary>
        public List<TitlePostal> DrainOutbox()
        {
            var taken = new List<TitlePostal>(_outbox);
            _outbox.Clear();
            return taken;
        }

        /// <summary>
        /// 一次成就触发(服务端 <c>setTrigger</c> + 完成后的 <c>_checkComplete</c>):计数规则见 <see cref="AchievementTracker.Trigger"/>;做完时先放称号、再排任务邮件、
        /// 再判整页邮件(文件头)。<paramref name="charLevel"/> 是角色等级。
        /// </summary>
        public TitleTriggerOutcome Trigger(int questId, int a, int b, int c, int charLevel)
        {
            AchievementTriggerCode code = Achievements.Trigger(questId, a, b, c, charLevel, out bool completed);
            if (code != AchievementTriggerCode.Ok || !completed)
            {
                return new TitleTriggerOutcome(code, false, 0, false, TitleCategory.General, 0, Array.Empty<TitlePostal>());
            }
            AchievementDef def = Achievements.Catalog.Get(questId);
            int title = def.TitleItemCode;
            TitleCategory category = TitleCategory.General;
            int slot = 0;
            bool placed = false;
            if (title != 0)
            {
                placed = Book.PutOnly(title, _lookup, out category, out slot);
            }
            var postals = new List<TitlePostal>();
            if (!def.Postal.IsEmpty)
            {
                postals.Add(new TitlePostal(TitlePostalSource.Achievement, questId, def.Postal.ItemCode, def.Postal.Count, def.Postal.Title, def.Postal.Body));
            }
            int sectionIndex = Definition.SectionIndexAt(category, slot);
            if (sectionIndex >= 0)
            {
                TitleSectionDef section = Definition.Sections[sectionIndex];
                TitleReward reward = section.PostalReward();
                if (reward != null && Definition.IsSectionComplete(section, Achievements.IsCleared))
                {
                    postals.Add(new TitlePostal(TitlePostalSource.Section, sectionIndex, reward.ItemCode, reward.ItemCount,
                        reward.Texts.Length > 0 ? reward.Texts[0] : default, reward.Texts.Length > 1 ? reward.Texts[1] : default));
                }
            }
            _outbox.AddRange(postals);
            return new TitleTriggerOutcome(code, true, title, placed, category, slot, postals);
        }

        /// <summary>
        /// 强制完成(服务端 <c>_forceClear</c>):成就必须有称号奖励(没有就什么都不做)。已经完成的,称号既不在它该在的图鉴格里、也不是现在穿着的那件
        /// (<paramref name="wornTitleCode"/>,没穿是 0)就补发一次;没完成的,补成完成并放入称号。不发邮件、不发通知。返回有没有改动。
        /// </summary>
        public bool ForceClear(int questId, int wornTitleCode)
        {
            AchievementDef def = Achievements.Catalog.Get(questId);
            if (def == null || def.TitleItemCode == 0)
            {
                return false;
            }
            int title = def.TitleItemCode;
            int before = Version;
            if (Achievements.IsCleared(questId))
            {
                int inBook = Definition.TryFindFitSlot(title, out TitleCategory category, out int slot) ? Book.CodeAt(category, slot) : 0;
                if (inBook == 0 && wornTitleCode != title)
                {
                    Book.PutOnly(title, _lookup, out _, out _);
                }
            }
            else
            {
                Achievements.MarkComplete(questId);
                Book.PutOnly(title, _lookup, out _, out _);
            }
            return Version != before;
        }

        /// <summary>
        /// 登录核对(服务端 <c>_verifyAchievement</c> 去掉三个硬编码特例):依次(分类、格从小到大)看图鉴定义里每个绑了成就的格,
        /// 成就类型是 <see cref="QuestKind.ClearQuest"/> 且它要求的任务已完成(<paramref name="isQuestCleared"/>)的,强制完成。返回改动了几个成就。
        /// </summary>
        public int Verify(Func<int, bool> isQuestCleared, int wornTitleCode)
        {
            if (isQuestCleared == null)
            {
                throw new ArgumentNullException(nameof(isQuestCleared));
            }
            int changed = 0;
            for (int c = 0; c < TitleCategories.Count; c++)
            {
                foreach (TitleSlotDef slot in Definition.SlotsOf((TitleCategory)c))
                {
                    if (slot.IsEmpty || !slot.IsBound)
                    {
                        continue;
                    }
                    AchievementDef def = Achievements.Catalog.Get(slot.AchievementId);
                    if (def == null || !def.IsVerifiable || !isQuestCleared(def.ClearQuestId))
                    {
                        continue;
                    }
                    if (ForceClear(slot.AchievementId, wornTitleCode))
                    {
                        changed++;
                    }
                }
            }
            return changed;
        }

        /// <summary>
        /// 补发(服务端 <c>adjust</c> 去掉等级特例):对补发清单(<see cref="TitleBookDef.Adjusts"/>)里每一项,它的任务已完成(<paramref name="isQuestCleared"/>)时,
        /// 把对应的成就补成完成并放入称号——不看成就是不是已经完成(所以重复补发会重复放入称号,和服务端一样)。返回处理了几项。
        /// </summary>
        public int Adjust(Func<int, bool> isQuestCleared)
        {
            if (isQuestCleared == null)
            {
                throw new ArgumentNullException(nameof(isQuestCleared));
            }
            int handled = 0;
            foreach (TitleAdjust item in Definition.Adjusts)
            {
                if (!isQuestCleared(item.QuestId))
                {
                    continue;
                }
                AchievementDef def = Achievements.Catalog.Get(item.AchievementId);
                if (def == null)
                {
                    continue;
                }
                Achievements.MarkComplete(item.AchievementId);
                Book.PutOnly(def.TitleItemCode, _lookup, out _, out _);
                handled++;
            }
            return handled;
        }

        /// <summary>图鉴收集的属性加成(见 <see cref="TitleBonus"/>:推断,宿主明确要才叠加)。</summary>
        public TitleBonus Bonus(bool includeDefault = true)
        {
            return TitleBonus.Compute(Definition, Achievements.IsCleared, includeDefault);
        }

        /// <summary>清空图鉴与成就进度、丢掉待投递的邮件(读档前用)。</summary>
        public void Clear()
        {
            Book.Clear();
            Achievements.Clear();
            _outbox.Clear();
        }
    }
}
