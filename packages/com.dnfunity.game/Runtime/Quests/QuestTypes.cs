using System;

// 证据:E-123(服务端静态读码,非 REA:.qst 导入函数 0x08a61813 注册的关键字与数值次序,QuestList::select_quest 0x08355726 的分类取法,
// UserQuest::check_quest_type_condition 0x086ace46 / check_quest_subtype_condition 0x086acb52 的类型分派)。
namespace Dnf.Game
{
    /// <summary>
    /// 任务分类,<c>.qst</c> 里 <c>[grade]</c> 的值。数值是服务端 <c>ENUM_QUEST_GRADE</c> 的次序;
    /// 服务端按分类把任务放进不同的表(史诗、训练、成就、每日、可重复、普通唯一、特别、称号、紧急)。
    /// </summary>
    public enum QuestGrade
    {
        /// <summary>史诗(主线;<c>[epic]</c>)。</summary>
        Epic = 0,

        /// <summary>训练(<c>[training]</c>)。</summary>
        Training = 1,

        /// <summary>成就(<c>[achievement]</c>)。</summary>
        Achievement = 2,

        /// <summary>每日(<c>[daily]</c>、<c>[daily random]</c>、<c>[special daily]</c> 都归这一类)。</summary>
        Daily = 3,

        /// <summary>可重复(<c>[normaly repeat]</c>,原版拼写如此):完成后还能再接,不给经验。</summary>
        NormalRepeat = 4,

        /// <summary>普通唯一(<c>[common unique]</c>):每个角色只能完成一次的支线。</summary>
        CommonUnique = 5,

        /// <summary>特别(没有对应的关键字出现在本地数据里,只保留服务端的编号)。</summary>
        Special = 6,

        /// <summary>称号(<c>[title]</c>)。</summary>
        Title = 7,

        /// <summary>紧急(<c>[urgent]</c>),同样算"可重复"。</summary>
        Urgent = 8,
    }

    /// <summary>
    /// 任务类型,<c>.qst</c> 里 <c>[type]</c> 的值(完成条件的种类)。数值是服务端 <c>ENUM_QUEST_TYPE</c> 的次序;
    /// 不认识的类型是 <see cref="Unknown"/>。
    /// </summary>
    public enum QuestKind
    {
        Unknown = -1,

        /// <summary>收集物品(<c>[seeking]</c>):交任务时背包里要有这些物品,交出去时扣掉。</summary>
        Seeking = 0,

        /// <summary>通关条件(<c>[condition under clear]</c>):在某个副本、某个难度下通关,有的还带条件(限时等)。</summary>
        ConditionUnderClear = 1,

        /// <summary>猎杀怪物(<c>[hunt monster]</c>):在指定副本、难度击杀指定怪物若干只。</summary>
        HuntMonster = 2,

        /// <summary>通过房间(<c>[clear map]</c>):打通指定地图(房间)。</summary>
        ClearMap = 3,

        /// <summary>拜访 NPC(<c>[meet npc]</c>)。</summary>
        MeetNpc = 4,

        PvpRank = 5,

        /// <summary>猎杀指定类型的敌人(<c>[hunt enemy]</c>):在 <see cref="HuntMonster"/> 的基础上多一个敌人种类。</summary>
        HuntEnemy = 6,

        PowerWarWin = 7,
        PowerWarPoint = 8,
        Mobile = 9,

        /// <summary>使用物品(<c>[use item]</c>)。</summary>
        UseItem = 10,

        /// <summary>取得分数(<c>[get score]</c>)。</summary>
        GetScore = 11,

        /// <summary>完成任务(<c>[clear quest]</c>)。</summary>
        ClearQuest = 12,

        CheckTime = 13,
        CheckLife = 14,
        AmplifyItem = 15,
        DisjointItem = 16,
        EquippedItem = 17,
        GetItem = 18,
        CustomQuest = 19,
        SendChatting = 20,
        TurnGoldCard = 21,
        UseFortuneCoin = 22,
        MeetSecretNpc = 23,
        UiClick = 24,

        /// <summary>找到并拜访 NPC(<c>[seek n meet npc]</c>)。</summary>
        SeekAndMeetNpc = 25,

        AssaultCount = 26,
    }

    /// <summary><c>.qst</c> 里关键字(<c>[epic]</c>、<c>[hunt monster]</c>……)与枚举的对应。</summary>
    public static class QuestNames
    {
        private static readonly string[] KindTags =
        {
            "[seeking]", "[condition under clear]", "[hunt monster]", "[clear map]", "[meet npc]", "[pvp rank]",
            "[hunt enemy]", "[powerwar win]", "[powerwar point]", "[mobile]", "[use item]", "[get score]",
            "[clear quest]", "[check time]", "[check life]", "[amplify item]", "[disjoint item]", "[equipped item]",
            "[get item]", "[custom quest]", "[send chatting]", "[turn gold card]", "[use fortune coin]",
            "[meet secret npc]", "[ui click]", "[seek n meet npc]", "[assault count]",
        };

        /// <summary><c>[grade]</c> 的关键字换成分类;认不出来返回 false。</summary>
        public static bool TryGrade(string tag, out QuestGrade grade)
        {
            grade = QuestGrade.NormalRepeat;
            if (tag == null)
            {
                return false;
            }
            switch (tag.ToLowerInvariant())
            {
                case "[epic]":
                    grade = QuestGrade.Epic;
                    return true;
                case "[training]":
                    grade = QuestGrade.Training;
                    return true;
                case "[achievement]":
                    grade = QuestGrade.Achievement;
                    return true;
                case "[daily]":
                case "[daily random]":
                case "[special daily]":
                    grade = QuestGrade.Daily;
                    return true;
                case "[normaly repeat]":
                case "[normal repeat]":
                    grade = QuestGrade.NormalRepeat;
                    return true;
                case "[common unique]":
                    grade = QuestGrade.CommonUnique;
                    return true;
                case "[special]":
                    grade = QuestGrade.Special;
                    return true;
                case "[title]":
                    grade = QuestGrade.Title;
                    return true;
                case "[urgent]":
                    grade = QuestGrade.Urgent;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary><c>[type]</c> 的关键字换成类型;认不出来返回 <see cref="QuestKind.Unknown"/>。</summary>
        public static QuestKind KindOf(string tag)
        {
            if (tag != null)
            {
                for (int i = 0; i < KindTags.Length; i++)
                {
                    if (string.Equals(KindTags[i], tag, StringComparison.OrdinalIgnoreCase))
                    {
                        return (QuestKind)i;
                    }
                }
            }
            return QuestKind.Unknown;
        }

        /// <summary>"可重复"的分类:服务端 <c>isRepeatableQuest</c>(0x08352bee)只认可重复与紧急两类;完成后不进"已完成"表,能再接。</summary>
        public static bool IsRepeatable(QuestGrade grade)
        {
            return grade == QuestGrade.NormalRepeat || grade == QuestGrade.Urgent;
        }
    }
}
