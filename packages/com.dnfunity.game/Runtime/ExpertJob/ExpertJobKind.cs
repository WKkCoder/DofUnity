using System;

// 服务端静态读码(df_game_r.orig):副职业类型的串表 [expert job none]=0 [magic enchant]=1 [alchemist]=2 [disjointer]=3 [doll controller]=4;
// character 目录下的 expertjob.lst 里,编号 1 到 4 依次是 enchanter / alchemist / disjointer / doll_controller 四个 .exj(initExpertJobScript 0x0896d830 →
// CExpertJobList::init 0x0896d8ec,以列表里的编号作键);物品脚本侧 [expert type] 的值写 "[enchanter]" "[alchemist]" "[disjointer]" "[doll_controller]",
// [expertjob only] 的名字写不带括号的同名。这一版只执行"分解"(类型 3),其余三条线只存状态与接口(见 ExpertJobKinds.IsExecutedHere)。
namespace Dnf.Game
{
    /// <summary>副职业的种类(服务端 ENUM_EXPERT_JOB_TYPE 的取值)。</summary>
    public enum ExpertJobKind
    {
        /// <summary>没有副职业。</summary>
        None = 0,

        /// <summary>附魔师(附魔的执行在强化线,这里只存状态)。</summary>
        Enchanter = 1,

        /// <summary>炼金术师(制作的执行在制作线,这里只存状态)。</summary>
        Alchemist = 2,

        /// <summary>分解师:这一版完整实现分解链。</summary>
        Disjointer = 3,

        /// <summary>人偶操纵师(这里只存状态)。</summary>
        DollController = 4,
    }

    /// <summary>副职业种类的名字、编号与能力范围。</summary>
    public static class ExpertJobKinds
    {
        /// <summary>种类个数(含"没有")。</summary>
        public const int Count = 5;

        /// <summary>
        /// 物品脚本 <c>[expertjob only]</c> 里的"通配"种类编号:<c>CItem::isExpertJobUsable</c>(0x084e95ce)认这个编号并且要求副职业不是"没有"。
        /// 全库没有物品用到(观察),照服务端的判断写出来。
        /// </summary>
        public const int AnyJob = 5;

        /// <summary>副职业的几份脚本所在的目录;列表里的脚本路径相对于它。</summary>
        public const string Folder = "character";

        /// <summary>副职业列表脚本 expertjob.lst(在 <see cref="Folder"/> 下)的路径:无标签,编号与脚本路径成对。</summary>
        public const string ListPath = Folder + "/expertjob.lst";

        /// <summary>副职业杂项脚本 expertjob.etc(在 <see cref="Folder"/> 下)的路径:等级上限与分解大成功率。</summary>
        public const string EtcPath = Folder + "/expertjob.etc";

        /// <summary>编号是不是一个认得的种类(0 到 4)。</summary>
        public static bool IsKnown(int type) => type >= 0 && type < Count;

        /// <summary>是不是真有副职业(1 到 4)。</summary>
        public static bool IsJob(ExpertJobKind kind) => kind >= ExpertJobKind.Enchanter && kind <= ExpertJobKind.DollController;

        /// <summary>这一版是否执行这条线的玩法:只有分解。其余三条线只存状态与接口,执行在制作线、强化线或另一轮。</summary>
        public static bool IsExecutedHere(ExpertJobKind kind) => kind == ExpertJobKind.Disjointer;

        /// <summary>物品脚本 <c>[expert type]</c> 的值("[enchanter]" 之类,带括号,不分大小写);不认得返回 <see cref="ExpertJobKind.None"/>。</summary>
        public static ExpertJobKind FromTypeTag(string tag)
        {
            if (tag == null || tag.Length < 3 || tag[0] != '[' || tag[tag.Length - 1] != ']')
            {
                return ExpertJobKind.None;
            }
            return FromName(tag.Substring(1, tag.Length - 2));
        }

        /// <summary>
        /// <c>[expertjob only]</c> 里的名字对应的种类编号(<c>GetExpertJobTypeList</c> 0x089bf6ca 的名字表):enchanter 1、alchemist 2、disjointer 3、doll_controller 4、all 5(<see cref="AnyJob"/>);
        /// 不认得返回 −1。
        /// </summary>
        public static int UsableTypeOf(string name)
        {
            if (name != null && name.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return AnyJob;
            }
            ExpertJobKind kind = FromName(name);
            return kind == ExpertJobKind.None ? -1 : (int)kind;
        }

        /// <summary>物品脚本 <c>[expertjob only]</c> 的名字("enchanter" 之类,不带括号,不分大小写);不认得返回 <see cref="ExpertJobKind.None"/>。</summary>
        public static ExpertJobKind FromName(string name)
        {
            if (name != null)
            {
                if (name.Equals("enchanter", StringComparison.OrdinalIgnoreCase))
                {
                    return ExpertJobKind.Enchanter;
                }
                if (name.Equals("alchemist", StringComparison.OrdinalIgnoreCase))
                {
                    return ExpertJobKind.Alchemist;
                }
                if (name.Equals("disjointer", StringComparison.OrdinalIgnoreCase))
                {
                    return ExpertJobKind.Disjointer;
                }
                if (name.Equals("doll_controller", StringComparison.OrdinalIgnoreCase))
                {
                    return ExpertJobKind.DollController;
                }
            }
            return ExpertJobKind.None;
        }
    }
}
