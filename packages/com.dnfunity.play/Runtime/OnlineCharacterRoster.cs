using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Game;

namespace Dnf.Play
{
    /// <summary>
    /// 服务端角色列表(中性镜像 <see cref="OnlineCharacterList"/>)→ 选角界面的名册(<see cref="CharacterRoster"/>)。
    /// 一个条目一个角色、顺序不变:<b>名册里的序号就是选角命令的槽号</b>(<see cref="CharacterSlotOnline.Slot"/>),不用 <see cref="OnlineCharacterEntry.Uid"/>。
    /// 角色数不设上限(多于 <see cref="CharacterRoster.Capacity"/> 个时界面滚动),一个条目也不丢。
    /// <para>各字段怎么用:名字字节按客户端的规则转文字(<see cref="CharacterNames"/>);等级是 u8(服务端是 u16 的低字节,256 级以上会绕回去,0 显示成 1 级);
    /// 职业称号由 <see cref="ICharacterTitles"/> 按"职业号 + 一转 + 觉醒"给,查不到用英文占位(<see cref="PlaceholderClassName"/>);
    /// 能不能进入见 <see cref="BlockOf"/>。装备记录原样挂在 <see cref="CharacterSlotOnline.Equips"/>。</para>
    /// </summary>
    public static class OnlineCharacterRoster
    {
        /// <summary>状态字节等于它时客户端拦住"开始游戏"【观,charre 证据 03 的 0x10fea80】。</summary>
        public const byte BlockedStatus = 8;

        /// <summary>X1 等于它时客户端拦住"开始游戏"。</summary>
        public const byte BlockedNameFlag = 1;

        /// <summary>把角色列表适配成名册;列表为 null 当没有角色。<paramref name="titles"/> 为 null 用 <see cref="PlaceholderTitles"/>(职业称号全是英文占位)。</summary>
        public static CharacterRoster From(OnlineCharacterList list, ICharacterTitles titles = null)
        {
            var slots = new List<CharacterSlot>();
            if (list != null)
            {
                for (int i = 0; i < list.Entries.Count; i++)
                {
                    slots.Add(SlotOf(list.Entries[i], i, titles));
                }
            }
            return CharacterRoster.Scrollable(slots);
        }

        /// <summary>一个条目 → 一个角色;<paramref name="slot"/> 是它在列表里的位置。</summary>
        public static CharacterSlot SlotOf(OnlineCharacterEntry entry, int slot, ICharacterTitles titles = null)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }
            ICharacterTitles source = titles ?? PlaceholderTitles.Instance;
            string name = CharacterNames.Decode(entry.NameBytes, source.DefaultName, out CharacterNameStatus _);
            int growType = entry.GrowTypeByte & 0x0F;
            int awakening = (entry.GrowTypeByte >> 4) & 0x07;
            string className = source.ClassName(entry.Job, growType, awakening) ?? PlaceholderClassName(entry.Job, growType, awakening);
            var online = new CharacterSlotOnline(slot, entry, entry.Job, growType, awakening, BlockOf(entry));
            return new CharacterSlot(name, className, entry.Level, online);
        }

        /// <summary>
        /// 外壳当前选中的角色(<see cref="OnlineShellFlow.SelectedSlot"/> 那一个)适配成名册里的角色;没选返回 null。
        /// 选角成功交给进城那一线的就是它:名字、职业称号、等级、<see cref="CharacterSlot.Online"/>(槽号、职业、一转、觉醒、装备记录、原始条目)。
        /// </summary>
        public static CharacterSlot SelectedSlotOf(OnlineShellFlow flow, ICharacterTitles titles = null)
        {
            if (flow == null)
            {
                throw new ArgumentNullException(nameof(flow));
            }
            OnlineCharacterEntry entry = flow.SelectedCharacter;
            return entry == null ? null : SlotOf(entry, flow.SelectedSlot, titles);
        }

        /// <summary>
        /// 为什么不能进入(能进入返回 <see cref="CharacterBlock.None"/>)。多个条件同时成立时按客户端检查的先后取第一个:
        /// 待删除(入口先过的那一关),然后 X1,然后状态字节。
        /// </summary>
        public static CharacterBlock BlockOf(OnlineCharacterEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }
            if (entry.D2 != 0)
            {
                return CharacterBlock.PendingDelete;
            }
            if (entry.X1 == BlockedNameFlag)
            {
                return CharacterBlock.NameFlag;
            }
            return entry.Status == BlockedStatus ? CharacterBlock.Status : CharacterBlock.None;
        }

        /// <summary>
        /// 查不到中文称号时的英文占位:职业名(<see cref="EquipRules.JobTag"/> 去掉括号、大写)加 <c>" G"</c> 一转、<c>" A"</c> 觉醒阶段(为 0 时不写);
        /// 职业号超出 11 个职业时是 <c>JOB</c> 加号码。
        /// </summary>
        public static string PlaceholderClassName(int job, int growType, int awakening)
        {
            string tag = EquipRules.JobTag(job);
            string text = tag == null
                ? "JOB" + job.ToString(CultureInfo.InvariantCulture)
                : tag.Trim('[', ']').ToUpperInvariant();
            if (growType > 0)
            {
                text += " G" + growType.ToString(CultureInfo.InvariantCulture);
            }
            if (awakening > 0)
            {
                text += " A" + awakening.ToString(CultureInfo.InvariantCulture);
            }
            return text;
        }
    }
}
