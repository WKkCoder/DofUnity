using System;
using System.Collections.Generic;

// 证据:服务端静态读码(df_game_r,只读反汇编与反编译,没有运行),地址见各处注释;数据形态来自本机全库统计,数值与编号不入库。
//  · 分类:convertTabTypeStringToEnum 0x08aa7d98("general"→0、"specific"→1、"despair"→2、"event"→3);
//    各分类的格数上限(70、200、120、60)取自 CTitleBook::checkError 0x08641fac 的范围检查,与 DB_LoadEtc::LoadTitleBook 0x08406092 里
//    四段压缩数据的解压后长度(70/200/120/60 乘以每格 61 字节)相合。
//  · 图鉴脚本 titlebook 的导入:importTitleBookEtcScript 0x08aa8110、importTitleSection 0x08aa8d65、importRewards 0x08aa8b79。
namespace Dnf.Game
{
    /// <summary>称号图鉴的四个分类;数值与服务端枚举一致。</summary>
    public enum TitleCategory
    {
        General = 0,
        Specific = 1,
        Despair = 2,
        Event = 3,
    }

    /// <summary>分类的名字与格数上限。</summary>
    public static class TitleCategories
    {
        /// <summary>分类数。</summary>
        public const int Count = 4;

        private static readonly string[] Names = { "general", "specific", "despair", "event" };

        // CTitleBook::checkError 0x08641fac:general 下标 0 到 69、specific 0 到 199、despair 0 到 119、event 0 到 59。
        private static readonly int[] Capacities = { 70, 200, 120, 60 };

        /// <summary>分类对应的脚本关键字;越界返回 null。</summary>
        public static string NameOf(TitleCategory category)
        {
            return IsValid(category) ? Names[(int)category] : null;
        }

        /// <summary>脚本关键字(不区分大小写)对应的分类;不认识返回 false(服务端这时得到 −1,后面的取用都落空)。</summary>
        public static bool TryParse(string name, out TitleCategory category)
        {
            if (name != null)
            {
                for (int i = 0; i < Names.Length; i++)
                {
                    if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        category = (TitleCategory)i;
                        return true;
                    }
                }
            }
            category = default;
            return false;
        }

        public static bool IsValid(TitleCategory category)
        {
            return (uint)category < (uint)Count;
        }

        /// <summary>这个分类存档里最多有几格(服务端的格数上限,比图鉴定义里列出的格数多)。越界返回 0。</summary>
        public static int CapacityOf(TitleCategory category)
        {
            return IsValid(category) ? Capacities[(int)category] : 0;
        }

        /// <summary>分类与下标合起来合不合法(服务端 <c>checkError</c> 的范围检查)。</summary>
        public static bool IsValidSlot(TitleCategory category, int slot)
        {
            return IsValid(category) && slot >= 0 && slot < Capacities[(int)category];
        }
    }

    /// <summary>字符串引用(<c>(4,"键")</c> 这类"种类 + 键"的值):交给字符串库按种类与键取文字。</summary>
    public readonly struct TitleText
    {
        public TitleText(int kind, string key)
        {
            Kind = kind;
            Key = key;
        }

        public int Kind { get; }

        public string Key { get; }

        public bool IsEmpty => string.IsNullOrEmpty(Key);
    }

    /// <summary>图鉴奖励的种类。服务端奖励基类的第一个字段是种类号,<c>_getPostalReward</c> 0x0828bd5e 只认 3(邮件奖励);另两个的号没有读到,这里不假设。</summary>
    public enum TitleRewardKind
    {
        /// <summary>属性奖励(<c>[hp max] "+" 150</c> 这类:属性标签、取值方式、数值)。</summary>
        Stat,

        /// <summary>表情奖励(<c>[give emotion] 编号</c>)。</summary>
        Emotion,

        /// <summary>邮件奖励(<c>[send postal] 物品 数量 文字 文字 文字</c>):整页收齐时发到邮箱(服务端数字 3)。</summary>
        Postal,
    }

    /// <summary>
    /// 图鉴(或某一页)的一项奖励。服务端按标签分三类读(<c>importRewards</c> 0x08aa8b79):属性标签(<c>loadAppendageType</c> 认得的)读一个取值方式字符串与一个浮点数;
    /// <c>[give emotion]</c> 读一个整数;<c>[send postal]</c> 读两个整数与三个字符串。
    /// </summary>
    public sealed class TitleReward
    {
        private TitleReward(TitleRewardKind kind)
        {
            Kind = kind;
        }

        public TitleRewardKind Kind { get; }

        /// <summary>属性奖励的标签(如 <c>[hp max]</c>);其他种类为 null。</summary>
        public string StatTag { get; private set; }

        /// <summary>属性奖励的取值方式字符串(数据里见到的是 <c>"+"</c>,按绝对值加);其他种类为 null。</summary>
        public string ValueType { get; private set; }

        /// <summary>属性奖励的数值。</summary>
        public double Value { get; private set; }

        /// <summary>表情奖励的表情编号。</summary>
        public int EmotionId { get; private set; }

        /// <summary>邮件奖励的物品编号。</summary>
        public int ItemCode { get; private set; }

        /// <summary>邮件奖励的物品数量。</summary>
        public int ItemCount { get; private set; }

        /// <summary>邮件奖励的三段文字(服务端发信只用前两段;第三段读进来没有被用到)。</summary>
        public TitleText[] Texts { get; private set; } = Array.Empty<TitleText>();

        /// <summary>取值方式是 <c>"+"</c>(按绝对值加)。</summary>
        public bool IsAbsolute => string.Equals(ValueType, "+", StringComparison.Ordinal);

        /// <summary>属性奖励的标签在装备属性表里有对应项(<see cref="EquipStats.TryFromTag"/>)。</summary>
        public bool TryGetStat(out EquipStat stat)
        {
            if (Kind == TitleRewardKind.Stat && EquipStats.TryFromTag(StatTag, out stat))
            {
                return true;
            }
            stat = default;
            return false;
        }

        public static TitleReward ForStat(string tag, string valueType, double value)
        {
            return new TitleReward(TitleRewardKind.Stat) { StatTag = tag, ValueType = valueType, Value = value };
        }

        public static TitleReward ForEmotion(int emotionId)
        {
            return new TitleReward(TitleRewardKind.Emotion) { EmotionId = emotionId };
        }

        public static TitleReward ForPostal(int itemCode, int count, params TitleText[] texts)
        {
            return new TitleReward(TitleRewardKind.Postal) { ItemCode = itemCode, ItemCount = count, Texts = texts ?? Array.Empty<TitleText>() };
        }
    }

    /// <summary>
    /// 图鉴里的一格定义(<c>[title collection info]</c> 的一组:序号 种类标志 [成就编号 个数 物品…])。
    /// 服务端读法(0x08aa8539 起):序号读出来就丢掉(格的下标是它在向量里的位置);种类标志为 −1 的格后面什么也不读(空格);
    /// 否则读成就编号、个数与这么多个物品编号。
    /// </summary>
    public sealed class TitleSlotDef
    {
        public TitleSlotDef(int index, int flag, int achievementId, IEnumerable<int> items)
        {
            Index = index;
            Flag = flag;
            AchievementId = achievementId;
            Items = new List<int>(items ?? Array.Empty<int>()).AsReadOnly();
        }

        /// <summary>这一格在分类里的下标(向量位置)。</summary>
        public int Index { get; }

        /// <summary>种类标志;−1 是空格。数据里还见到 0(成就格)与 1(活动格)。</summary>
        public int Flag { get; }

        public bool IsEmpty => Flag == -1;

        /// <summary>绑定的成就(任务)编号;−1 表示这一格不绑成就(空格,或活动格)。</summary>
        public int AchievementId { get; }

        public bool IsBound => AchievementId != -1;

        /// <summary>这一格列出的称号物品编号(活动格里是占位的 −1)。</summary>
        public IReadOnlyList<int> Items { get; }

        public bool Lists(int itemCode)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i] == itemCode)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 这一格收不收这件物品(服务端 <c>_checkFitSlot</c> 0x08642d42 的末两步):列出了就收;没列出,但这一格不绑成就(成就编号 −1)也收。
        /// 注意空格(没定义)的成就编号也是 −1,所以按服务端这一条它什么都收——图鉴界面不会把空格提供给玩家。
        /// </summary>
        public bool Accepts(int itemCode)
        {
            return Lists(itemCode) || AchievementId == -1;
        }
    }

    /// <summary>
    /// 图鉴的一页(<c>[section]</c> 里的一个 <c>[section icon]…[/reward type]</c> 段)。服务端读法(importTitleSection 0x08aa8d65):
    /// 一页占 10 格 [序号×10, 序号×10+10),序号是同分类前面已有几页;读完后把终点截到这个范围里第一个空格的位置(0x08aa87ad 起的循环)。
    /// </summary>
    public sealed class TitleSectionDef
    {
        public TitleSectionDef(TitleCategory category, int pageIndex)
        {
            Category = category;
            PageIndex = pageIndex;
            Start = pageIndex * 10;
            End = Start + 10;
        }

        public TitleCategory Category { get; }

        /// <summary>同分类里这是第几页(从 0 起)。</summary>
        public int PageIndex { get; }

        /// <summary>起点(含)。</summary>
        public int Start { get; }

        /// <summary>终点(不含,已截到第一个空格);<see cref="TitleBookDef.SectionIndexAt"/> 却按"≤ 终点"比(服务端的写法)。</summary>
        public int End { get; internal set; }

        /// <summary><c>[section name]</c>:页签名。</summary>
        public TitleText SectionName { get; internal set; }

        /// <summary><c>[section icon]</c>:页签图标(图包路径与帧号)。</summary>
        public string IconPath { get; internal set; }

        public int IconFrame { get; internal set; }

        /// <summary><c>[frame type]</c>。</summary>
        public int FrameType { get; internal set; }

        /// <summary><c>[lock message]</c>:不为空表示这一页锁着,放入称号被拒(服务端 <c>putTitle</c> 返回 12)。</summary>
        public TitleText LockMessage { get; internal set; }

        /// <summary><c>[name]</c>:奖励栏的名字。</summary>
        public TitleText RewardName { get; internal set; }

        /// <summary><c>[image path]</c>:奖励图(图包路径与帧号)。</summary>
        public string ImagePath { get; internal set; }

        public int ImageFrame { get; internal set; }

        /// <summary><c>[effect path]</c>。</summary>
        public string EffectPath { get; internal set; }

        /// <summary>这一页的奖励(属性、表情、邮件),按脚本次序。</summary>
        public List<TitleReward> Rewards { get; } = new List<TitleReward>();

        public bool IsLocked => !LockMessage.IsEmpty;

        /// <summary>这一页的邮件奖励(服务端 <c>_getPostalReward</c> 0x0828bd5e:第一个种类为邮件的奖励);没有返回 null。</summary>
        public TitleReward PostalReward()
        {
            foreach (TitleReward reward in Rewards)
            {
                if (reward.Kind == TitleRewardKind.Postal)
                {
                    return reward;
                }
            }
            return null;
        }
    }

    /// <summary><c>[custom condition message]</c> 的一项(分类、格下标、文字):界面上显示这一格的特殊条件说明,规则不读。</summary>
    public readonly struct TitleConditionMessage
    {
        public TitleConditionMessage(TitleCategory category, int slot, TitleText text)
        {
            Category = category;
            Slot = slot;
            Text = text;
        }

        public TitleCategory Category { get; }

        public int Slot { get; }

        public TitleText Text { get; }
    }

    /// <summary>
    /// <c>[adjust list]</c> 的一项(三个整数):任务编号、称号物品编号、成就编号。服务端 <c>CAchievement::adjust</c> 0x0828c976 只用第一个与第三个:
    /// 角色已经完成了那个任务,就把那个成就补成完成并放入称号(中间的物品编号没有被读)。
    /// </summary>
    public readonly struct TitleAdjust
    {
        public TitleAdjust(int questId, int itemCode, int achievementId)
        {
            QuestId = questId;
            ItemCode = itemCode;
            AchievementId = achievementId;
        }

        public int QuestId { get; }

        public int ItemCode { get; }

        public int AchievementId { get; }
    }
}
