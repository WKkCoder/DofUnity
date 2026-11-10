using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:服务端静态读码(df_game_r,只读反汇编与反编译,没有运行)——
//  · 导入:importTitleBookEtcScript 0x08aa8110(顶层按标签分派:[default reward]、[title collection info]、[section]、[custom condition message]、
//    [adjust list]、[view titlebook eventmark];别的标签不读)、importTitleSection 0x08aa8d65、importRewards 0x08aa8b79、getTitleCollection 0x08aa94e1;
//  · 取用规则:CTitleBook::_getFitSlot 0x08642baa、_checkFitSlot 0x08642d42、_checkMovetoInven 0x08641c52,CAchievement::_getSectionIdx 0x0828bcb8、_getTabComplete 0x0828bdc6。
// 数据形态来自本机全库统计(件数只记在笔记里),代码与测试里的编号全是合成的。
namespace Dnf.Game
{
    /// <summary>
    /// 称号图鉴的定义(图鉴脚本 titlebook):各分类的格定义、分页与奖励、默认奖励、特殊条件文字、补发清单,以及从定义里查"这件称号放哪一格"一类的取用规则。
    /// 取用规则逐条照服务端,包括几处写法上的怪异(见各方法的注释)。
    /// </summary>
    public sealed class TitleBookDef
    {
        private readonly List<TitleSlotDef>[] _slots = new List<TitleSlotDef>[TitleCategories.Count];
        private readonly List<TitleReward> _default = new List<TitleReward>();
        private readonly List<TitleSectionDef> _sections = new List<TitleSectionDef>();
        private readonly List<TitleConditionMessage> _messages = new List<TitleConditionMessage>();
        private readonly List<TitleAdjust> _adjusts = new List<TitleAdjust>();

        public TitleBookDef()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = new List<TitleSlotDef>();
            }
        }

        /// <summary><c>[default reward]</c>:默认奖励(数据里是生命与魔法上限)。服务端读进来之后没有任何读取点(按偏移搜过),是否生效不明。</summary>
        public IReadOnlyList<TitleReward> DefaultRewards => _default;

        /// <summary>所有页,按脚本次序(服务端向量的次序,<see cref="SectionIndexAt"/> 返回的就是这里的下标)。</summary>
        public IReadOnlyList<TitleSectionDef> Sections => _sections;

        /// <summary><c>[custom condition message]</c>。</summary>
        public IReadOnlyList<TitleConditionMessage> ConditionMessages => _messages;

        /// <summary><c>[adjust list]</c>:补发清单(见 <see cref="TitleAdjust"/>)。</summary>
        public IReadOnlyList<TitleAdjust> Adjusts => _adjusts;

        /// <summary><c>[view titlebook eventmark]</c>(服务端读成布尔,没有读取点;界面用)。</summary>
        public bool ViewEventMark { get; internal set; }

        /// <summary><c>[titlebook event minlevel]</c>:服务端不读这个标记,只有客户端用;没写为 false。</summary>
        public bool HasEventMinLevel { get; internal set; }

        public int EventMinLevel { get; internal set; }

        /// <summary>顶层不认识而跳过的标签行数(含各种结束标签)。</summary>
        public int SkippedTags { get; internal set; }

        /// <summary>格定义读不全(整数不够)的次数。</summary>
        public int TruncatedSlotLists { get; internal set; }

        /// <summary>格定义里写的序号与它在向量里的位置不一致的次数(服务端把序号丢掉、只认位置)。</summary>
        public int SlotIndexMismatches { get; internal set; }

        /// <summary>分类关键字不认识而整段跳过的 <c>[title collection info]</c> 与 <c>[section]</c> 个数。</summary>
        public int SkippedUnknownCategory { get; internal set; }

        /// <summary>这个分类里定义了几格(含空格)。</summary>
        public IReadOnlyList<TitleSlotDef> SlotsOf(TitleCategory category)
        {
            return TitleCategories.IsValid(category) ? _slots[(int)category] : (IReadOnlyList<TitleSlotDef>)Array.Empty<TitleSlotDef>();
        }

        internal List<TitleSlotDef> MutableSlots(TitleCategory category) => _slots[(int)category];

        internal List<TitleReward> MutableDefault => _default;

        internal List<TitleSectionDef> MutableSections => _sections;

        internal List<TitleConditionMessage> MutableMessages => _messages;

        internal List<TitleAdjust> MutableAdjusts => _adjusts;

        /// <summary>一格的定义;分类或下标越界返回 null。</summary>
        public TitleSlotDef SlotAt(TitleCategory category, int slot)
        {
            if (!TitleCategories.IsValid(category))
            {
                return null;
            }
            List<TitleSlotDef> list = _slots[(int)category];
            return (uint)slot < (uint)list.Count ? list[slot] : null;
        }

        /// <summary>定义里有物品编号的格数(不是空格的格数)。</summary>
        public int DefinedSlotCount(TitleCategory category)
        {
            int n = 0;
            foreach (TitleSlotDef slot in SlotsOf(category))
            {
                if (!slot.IsEmpty)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>
        /// 这件称号该放哪一格(服务端 <c>_getFitSlot</c> 0x08642baa):按分类从小到大、格从小到大,第一个"列出了这个物品编号"的格。
        /// 找不到返回 false。(活动格列的是占位的 −1,所以物品编号 −1 会在活动分类里找到第一个活动格——调用方不会拿 −1 来问。)
        /// </summary>
        public bool TryFindFitSlot(int itemCode, out TitleCategory category, out int slot)
        {
            for (int c = 0; c < TitleCategories.Count; c++)
            {
                List<TitleSlotDef> list = _slots[c];
                for (int s = 0; s < list.Count; s++)
                {
                    if (list[s].Lists(itemCode))
                    {
                        category = (TitleCategory)c;
                        slot = s;
                        return true;
                    }
                }
            }
            category = default;
            slot = -1;
            return false;
        }

        /// <summary>这一格收不收这件称号(服务端 <c>_checkFitSlot</c> 0x08642d42):分类有定义、下标在定义的格数里、<see cref="TitleSlotDef.Accepts"/>。</summary>
        public bool CheckFitSlot(int itemCode, TitleCategory category, int slot)
        {
            TitleSlotDef def = SlotAt(category, slot);
            return def != null && def.Accepts(itemCode);
        }

        /// <summary>这一格里的称号能不能拿回背包(服务端 <c>_checkMovetoInven</c> 0x08641c52):格在定义里,且这一格不绑成就。绑了成就的格里的称号只能直接穿上。</summary>
        public bool CanTakeBack(TitleCategory category, int slot)
        {
            TitleSlotDef def = SlotAt(category, slot);
            return def != null && def.AchievementId == -1;
        }

        /// <summary>
        /// 这一格属于哪一页(服务端 <c>_getSectionIdx</c> 0x0828bcb8),返回 <see cref="Sections"/> 的下标,没有返回 −1:按脚本次序找第一个分类相同、
        /// 起点 ≤ 格 ≤ <b>终点</b> 的页。终点是"不含"的,这里却用"≤"(服务端的写法):一页写满 10 格时,下一页的第一格会被判给这一页(数据里有没有这种页由检查命令数)。
        /// </summary>
        public int SectionIndexAt(TitleCategory category, int slot)
        {
            for (int i = 0; i < _sections.Count; i++)
            {
                TitleSectionDef section = _sections[i];
                if (section.Category == category && section.Start <= slot && slot <= section.End)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>这一格所在页;没有返回 null。</summary>
        public TitleSectionDef SectionAt(TitleCategory category, int slot)
        {
            int at = SectionIndexAt(category, slot);
            return at < 0 ? null : _sections[at];
        }

        /// <summary>
        /// 一页是不是收齐了(服务端 <c>_getTabComplete</c> 0x0828bdc6):页里每个"不是空格且绑了成就"的格,它的成就都已完成。
        /// 不绑成就的格(活动格)不参与;一页里一个绑了成就的格也没有时算收齐(空洞真);这个分类没有任何定义时也算收齐。
        /// 服务端没有检查 <c>i &lt; 格数</c>,越过定义的格会读到向量外面;这里把它们当作不参与。
        /// </summary>
        public bool IsSectionComplete(TitleSectionDef section, Func<int, bool> isCleared)
        {
            if (section == null)
            {
                throw new ArgumentNullException(nameof(section));
            }
            if (isCleared == null)
            {
                throw new ArgumentNullException(nameof(isCleared));
            }
            List<TitleSlotDef> list = _slots[(int)section.Category];
            if (list.Count == 0)
            {
                return true;
            }
            for (int i = section.Start; i < section.End; i++)
            {
                if (i >= list.Count)
                {
                    continue;
                }
                TitleSlotDef slot = list[i];
                if (!slot.IsEmpty && slot.AchievementId != -1 && !isCleared(slot.AchievementId))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>读图鉴脚本 titlebook。</summary>
    public static class TitleBookScript
    {
        /// <summary>
        /// 把脚本的各行解成 <see cref="TitleBookDef"/>。读法与服务端导入函数一致:顶层标签不认识的跳过;<c>[section]</c> 内一页一页读,
        /// 以 <c>[/reward type]</c> 结束一页、以 <c>[/section]</c> 结束整段;一页里认得的标签之外一律当奖励标签读(读不出奖励的就是没有效果)。
        /// </summary>
        public static TitleBookDef Parse(ScriptLine[] lines)
        {
            var def = new TitleBookDef();
            if (lines == null)
            {
                return def;
            }
            int i = 0;
            while (i < lines.Length)
            {
                ScriptLine line = lines[i++];
                switch (Lower(line.Tag))
                {
                    case null:
                        break;
                    case "[default reward]":
                        while (i < lines.Length && !Is(lines[i], "[/default reward]"))
                        {
                            AddReward(def.MutableDefault, lines[i]);
                            i++;
                        }
                        if (i < lines.Length)
                        {
                            i++;
                        }
                        break;
                    case "[title collection info]":
                        ReadCollection(def, line);
                        break;
                    case "[section]":
                        i = ReadSections(def, lines, i, line);
                        break;
                    case "[custom condition message]":
                        ReadConditionMessages(def, line);
                        break;
                    case "[adjust list]":
                        ReadAdjusts(def, line);
                        break;
                    case "[view titlebook eventmark]":
                    {
                        List<int> v = line.Ints();
                        def.ViewEventMark = v.Count > 0 && v[0] != 0;
                        break;
                    }
                    case "[titlebook event minlevel]":
                    {
                        List<int> v = line.Ints();
                        if (v.Count > 0)
                        {
                            def.HasEventMinLevel = true;
                            def.EventMinLevel = v[0];
                        }
                        break;
                    }
                    default:
                        def.SkippedTags++;
                        break;
                }
            }
            return def;
        }

        private static string Lower(string tag) => tag?.ToLowerInvariant();

        private static bool Is(ScriptLine line, string tag) => line.TagIs(tag);

        /// <summary>
        /// <c>[title collection info] "分类" 格数 { 序号 种类标志 [成就编号 个数 物品…] }…</c>(0x08aa8539 起):序号读出来就丢,
        /// 种类标志为 −1 的格没有后文;读到的格追加到这个分类的向量末尾(同一分类写了多段就接着排)。
        /// </summary>
        private static void ReadCollection(TitleBookDef def, ScriptLine line)
        {
            if (!TitleCategories.TryParse(line.FirstString(), out TitleCategory category))
            {
                def.SkippedUnknownCategory++;
                return;
            }
            List<int> v = line.Ints();
            if (v.Count == 0)
            {
                def.TruncatedSlotLists++;
                return;
            }
            int total = v[0];
            int at = 1;
            List<TitleSlotDef> slots = def.MutableSlots(category);
            for (int k = 0; k < total; k++)
            {
                if (at + 2 > v.Count)
                {
                    def.TruncatedSlotLists++;
                    return;
                }
                int index = v[at++];
                int flag = v[at++];
                int achievement = -1;
                var items = new List<int>();
                if (flag != -1)
                {
                    if (at + 2 > v.Count)
                    {
                        def.TruncatedSlotLists++;
                        return;
                    }
                    achievement = v[at++];
                    int count = v[at++];
                    for (int j = 0; j < count; j++)
                    {
                        if (at >= v.Count)
                        {
                            def.TruncatedSlotLists++;
                            return;
                        }
                        items.Add(v[at++]);
                    }
                }
                if (index != slots.Count)
                {
                    def.SlotIndexMismatches++;
                }
                slots.Add(new TitleSlotDef(slots.Count, flag, achievement, items));
            }
        }

        /// <summary>
        /// <c>[section] "分类"</c> 后面一页一页读(importTitleBookEtcScript 的循环 + importTitleSection):页序号是同分类前面已有几页;
        /// 读完一页把终点截到起点到终点之间第一个空格;<c>[/section]</c>(或脚本读完)结束整段。返回下一个要处理的行下标。
        /// </summary>
        private static int ReadSections(TitleBookDef def, ScriptLine[] lines, int i, ScriptLine head)
        {
            bool known = TitleCategories.TryParse(head.FirstString(), out TitleCategory category);
            if (!known)
            {
                def.SkippedUnknownCategory++;
            }
            while (i < lines.Length)
            {
                int page = 0;
                if (known)
                {
                    foreach (TitleSectionDef existing in def.MutableSections)
                    {
                        if (existing.Category == category)
                        {
                            page++;
                        }
                    }
                }
                var section = new TitleSectionDef(known ? category : TitleCategory.General, page);
                bool pageEnded = ReadPage(section, lines, ref i);
                if (!pageEnded)
                {
                    break;
                }
                if (!known)
                {
                    continue;
                }
                List<TitleSlotDef> slots = def.MutableSlots(category);
                if (slots.Count > 0)
                {
                    for (int k = section.Start; k < section.End; k++)
                    {
                        if (k < slots.Count && slots[k].IsEmpty)
                        {
                            section.End = k;
                        }
                    }
                }
                def.MutableSections.Add(section);
            }
            return i;
        }

        /// <summary>读一页(importTitleSection 0x08aa8d65):返回 true 表示以 <c>[/reward type]</c> 结束(这一页算数);false 表示以 <c>[/section]</c> 或脚本读完结束。</summary>
        private static bool ReadPage(TitleSectionDef section, ScriptLine[] lines, ref int i)
        {
            while (i < lines.Length)
            {
                ScriptLine line = lines[i++];
                switch (Lower(line.Tag))
                {
                    case null:
                        break;
                    case "[/section]":
                        return false;
                    case "[/reward type]":
                        return true;
                    case "[section name]":
                        section.SectionName = TextOf(line);
                        break;
                    case "[section icon]":
                        section.IconPath = line.FirstString();
                        section.IconFrame = FirstInt(line, 0);
                        break;
                    case "[frame type]":
                        section.FrameType = FirstInt(line, 0);
                        break;
                    case "[lock message]":
                        section.LockMessage = TextOf(line);
                        break;
                    case "[name]":
                        section.RewardName = TextOf(line);
                        break;
                    case "[image path]":
                        section.ImagePath = line.FirstString();
                        section.ImageFrame = FirstInt(line, 0);
                        break;
                    case "[effect path]":
                        section.EffectPath = line.FirstString();
                        break;
                    default:
                        AddReward(section.Rewards, line);
                        break;
                }
            }
            return false;
        }

        /// <summary>
        /// 奖励标签(importRewards 0x08aa8b79):<c>[give emotion] 编号</c>;<c>[send postal] 物品 数量 文字 文字 文字</c>;
        /// 其余标签只要写成"一个字符串(取值方式)+ 一个数"就当属性奖励(服务端是 <c>loadAppendageType</c> 认得的标签,这张表没有读;
        /// 不认得的标签服务端什么也不做,这里同样不产生奖励)。
        /// </summary>
        private static void AddReward(List<TitleReward> into, ScriptLine line)
        {
            string tag = Lower(line.Tag);
            if (tag == null)
            {
                return;
            }
            if (tag == "[give emotion]")
            {
                into.Add(TitleReward.ForEmotion(FirstInt(line, 0)));
                return;
            }
            if (tag == "[send postal]")
            {
                List<int> ints = line.Ints();
                var texts = new List<TitleText>();
                foreach (ScriptValue value in line.Values)
                {
                    if (value.Kind == ScriptValueKind.Keyed)
                    {
                        texts.Add(new TitleText(value.Int, value.Text));
                    }
                    else if (value.Kind == ScriptValueKind.String)
                    {
                        texts.Add(new TitleText(-1, value.Text));
                    }
                }
                into.Add(TitleReward.ForPostal(ints.Count > 0 ? ints[0] : 0, ints.Count > 1 ? ints[1] : 0, texts.ToArray()));
                return;
            }
            ScriptValue[] v = line.Values;
            if (v.Length >= 2 && v[0].Kind == ScriptValueKind.String && (v[1].IsNumber || v[1].Kind == ScriptValueKind.Float))
            {
                double number = v[1].Kind == ScriptValueKind.Float ? v[1].Float : v[1].Int;
                into.Add(TitleReward.ForStat(line.Tag, v[0].Text, number));
            }
        }

        /// <summary><c>[custom condition message] "分类" 格 (种类,"键") …</c>:三个一组。</summary>
        private static void ReadConditionMessages(TitleBookDef def, ScriptLine line)
        {
            ScriptValue[] v = line.Values;
            for (int at = 0; at + 3 <= v.Length; at += 3)
            {
                // 服务端依次读 字符串、整数、字符串;类型对不上就读不下去(这里同样停在第一个对不上的位置)。
                if (v[at].Kind != ScriptValueKind.String || !v[at + 1].IsNumber || !TitleCategories.TryParse(v[at].Text, out TitleCategory category))
                {
                    return;
                }
                ScriptValue text = v[at + 2];
                TitleText t = text.Kind == ScriptValueKind.Keyed ? new TitleText(text.Int, text.Text)
                    : text.Kind == ScriptValueKind.String ? new TitleText(-1, text.Text) : default;
                def.MutableMessages.Add(new TitleConditionMessage(category, v[at + 1].Int, t));
            }
        }

        /// <summary><c>[adjust list]</c>:三个整数一组(任务编号、称号物品编号、成就编号),不成组的尾巴丢掉。</summary>
        private static void ReadAdjusts(TitleBookDef def, ScriptLine line)
        {
            List<int> v = line.Ints();
            for (int at = 0; at + 3 <= v.Count; at += 3)
            {
                def.MutableAdjusts.Add(new TitleAdjust(v[at], v[at + 1], v[at + 2]));
            }
        }

        private static TitleText TextOf(ScriptLine line)
        {
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.Keyed)
                {
                    return new TitleText(v.Int, v.Text);
                }
                if (v.Kind == ScriptValueKind.String)
                {
                    return new TitleText(-1, v.Text);
                }
            }
            return default;
        }

        private static int FirstInt(ScriptLine line, int fallback)
        {
            foreach (ScriptValue v in line.Values)
            {
                if (v.IsNumber)
                {
                    return v.Int;
                }
            }
            return fallback;
        }
    }
}
