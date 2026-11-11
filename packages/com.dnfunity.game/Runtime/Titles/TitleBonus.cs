using System;
using System.Collections.Generic;

// 证据与边界:
//  · 服务端读码(观察):图鉴文件里的"默认奖励"与各页的属性奖励、表情奖励,服务端读进数据表之后没有任何读取点(按字段偏移搜过,只有 _getSectionIdx 与 adjust 读页的起止与邮件奖励),
//    所以这些属性在哪里生效、是否生效,服务端读码里没有答案;邮件奖励则由 _sendCompleteReward 0x0828bf2a 在整页收齐时发出。
//  · 本类的"收齐一页就加这一页的属性奖励、默认奖励一直加"是对客户端行为的推断(标记名和数据形态——"+"、绝对值——是观察),默认不接进摘要,宿主明确打开才叠加。
//  · 取值方式不是 "+" 的、属性标签不在装备属性表(<see cref="EquipStats.TryFromTag"/>)里的奖励只计数、不折算。
namespace Dnf.Game
{
    /// <summary>图鉴收集带来的属性加成(只含属性奖励),折成 <see cref="EquipStats"/> 好在调用侧与装备属性相加。</summary>
    public sealed class TitleBonus
    {
        private TitleBonus(EquipStats stats, int pages, int applied, int skipped)
        {
            Stats = stats;
            PagesCounted = pages;
            RewardsApplied = applied;
            RewardsSkipped = skipped;
        }

        /// <summary>什么加成都没有。</summary>
        public static TitleBonus None => new TitleBonus(EquipStats.Empty, 0, 0, 0);

        /// <summary>加成的属性集(各条奖励逐项相加)。</summary>
        public EquipStats Stats { get; }

        /// <summary>计入的页数(收齐、且至少有一格绑了成就的页)。</summary>
        public int PagesCounted { get; }

        /// <summary>折进 <see cref="Stats"/> 的属性奖励条数(含默认奖励)。</summary>
        public int RewardsApplied { get; }

        /// <summary>只计数没折算的属性奖励条数(属性标签不在装备属性表里,或取值方式不是 "+")。</summary>
        public int RewardsSkipped { get; }

        /// <summary>
        /// 现在的收集加成:<paramref name="includeDefault"/> 为真时先加默认奖励;再对每一页——收齐了(<see cref="TitleBookDef.IsSectionComplete"/>)
        /// 且这一页里至少有一格绑了成就(一页没有任何成就格时服务端的判断是"空洞为真",不能因此白送加成)——加上这一页的属性奖励。
        /// </summary>
        public static TitleBonus Compute(TitleBookDef def, Func<int, bool> isCleared, bool includeDefault = true)
        {
            if (def == null)
            {
                throw new ArgumentNullException(nameof(def));
            }
            if (isCleared == null)
            {
                throw new ArgumentNullException(nameof(isCleared));
            }
            EquipStats total = EquipStats.Empty;
            int pages = 0;
            int applied = 0;
            int skipped = 0;
            if (includeDefault)
            {
                Add(def.DefaultRewards, ref total, ref applied, ref skipped);
            }
            foreach (TitleSectionDef section in def.Sections)
            {
                if (!HasBoundSlot(def, section) || !def.IsSectionComplete(section, isCleared))
                {
                    continue;
                }
                pages++;
                Add(section.Rewards, ref total, ref applied, ref skipped);
            }
            return new TitleBonus(total, pages, applied, skipped);
        }

        private static bool HasBoundSlot(TitleBookDef def, TitleSectionDef section)
        {
            IReadOnlyList<TitleSlotDef> slots = def.SlotsOf(section.Category);
            for (int i = section.Start; i < section.End && i < slots.Count; i++)
            {
                if (!slots[i].IsEmpty && slots[i].IsBound)
                {
                    return true;
                }
            }
            return false;
        }

        private static void Add(IEnumerable<TitleReward> rewards, ref EquipStats total, ref int applied, ref int skipped)
        {
            foreach (TitleReward reward in rewards)
            {
                if (reward.Kind != TitleRewardKind.Stat)
                {
                    continue;
                }
                if (reward.IsAbsolute && reward.TryGetStat(out EquipStat stat))
                {
                    total = total.Plus(EquipStats.Empty.With(stat, reward.Value));
                    applied++;
                }
                else
                {
                    skipped++;
                }
            }
        }
    }
}
