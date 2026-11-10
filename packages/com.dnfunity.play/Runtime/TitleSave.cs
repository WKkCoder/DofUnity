using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dnf.Game;

// 本项目自己的设计:称号图鉴与成就进度的存档字段,只存数字(分类、格、物品编号;成就编号与三个剩余计数),不含任何原版内容。
// 取舍参照服务端存盘字段(静态读码):图鉴是四段按格排的称号(DB_LoadEtc::LoadTitleBook 0x08406092),成就是"编号 + 三个 16 位计数"的记录(LoadAchievement 0x08406422,每角色最多 0x200 条)。
// 穿着的称号不在这里:它在装备栏的 11 号格,随既有的 worn 行存取。
namespace Dnf.Play
{
    /// <summary>存档里图鉴的一格:分类(0 到 3)、格下标、称号物品编号。</summary>
    public readonly struct SavedTitle
    {
        public SavedTitle(int category, int slot, int code)
        {
            Category = category;
            Slot = slot;
            Code = code;
        }

        public int Category { get; }

        public int Slot { get; }

        public int Code { get; }
    }

    /// <summary>存档里一个成就的进度:成就(任务)编号与三个"还差多少"的计数(全 0 就是完成)。</summary>
    public readonly struct SavedAchievement
    {
        public SavedAchievement(int id, int remaining0, int remaining1, int remaining2)
        {
            Id = id;
            Remaining0 = remaining0;
            Remaining1 = remaining1;
            Remaining2 = remaining2;
        }

        public int Id { get; }

        public int Remaining0 { get; }

        public int Remaining1 { get; }

        public int Remaining2 { get; }
    }

    public sealed partial class HeroSave
    {
        /// <summary>称号图鉴里放着的称号(键 <c>title 分类 格 物品编号</c>,每件一行,分类与格从小到大;旧存档没有这些行)。</summary>
        public List<SavedTitle> Titles { get; } = new List<SavedTitle>();

        /// <summary>成就进度(键 <c>achv 编号 计数0 计数1 计数2</c>,每个有记录的成就一行,编号从小到大;旧存档没有这些行)。</summary>
        public List<SavedAchievement> Achievements { get; } = new List<SavedAchievement>();
    }

    /// <summary>称号存档行的文本读写(挂在 <see cref="HeroSave.ToText"/> 与存档读取的行解析上;键不认得的旧程序会忽略它们)。</summary>
    internal static class TitleSaveFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const string TitleKey = "title";
        public const string AchievementKey = "achv";

        public static bool Handles(string key)
        {
            return key == TitleKey || key == AchievementKey;
        }

        public static void Write(HeroSave save, StringBuilder sb)
        {
            foreach (SavedTitle title in save.Titles)
            {
                sb.Append(TitleKey).Append(' ').Append(title.Category.ToString(Inv)).Append(' ').Append(title.Slot.ToString(Inv)).Append(' ')
                    .Append(title.Code.ToString(Inv)).Append('\n');
            }
            foreach (SavedAchievement achievement in save.Achievements)
            {
                sb.Append(AchievementKey).Append(' ').Append(achievement.Id.ToString(Inv)).Append(' ').Append(achievement.Remaining0.ToString(Inv)).Append(' ')
                    .Append(achievement.Remaining1.ToString(Inv)).Append(' ').Append(achievement.Remaining2.ToString(Inv)).Append('\n');
            }
        }

        public static bool Parse(HeroSave r, string key, string[] f, int no, out string error)
        {
            error = null;
            int arity = key == TitleKey ? 4 : 5;
            if (f.Length != arity)
            {
                error = "第 " + no + " 行:" + key + " 需要 " + (arity - 1) + " 个值";
                return false;
            }
            var v = new long[arity - 1];
            for (int i = 0; i < v.Length; i++)
            {
                if (!long.TryParse(f[i + 1], NumberStyles.Integer, Inv, out v[i]))
                {
                    error = "第 " + no + " 行:" + key + " 的值不是整数";
                    return false;
                }
            }
            bool ok;
            if (key == TitleKey)
            {
                ok = InRange(v[0], 0, TitleCategories.Count - 1) && InRange(v[1], 0, int.MaxValue) && InRange(v[2], 1, int.MaxValue)
                    && TitleCategories.IsValidSlot((TitleCategory)(int)v[0], (int)v[1]);
                if (ok)
                {
                    r.Titles.Add(new SavedTitle((int)v[0], (int)v[1], (int)v[2]));
                }
            }
            else
            {
                ok = InRange(v[0], 1, int.MaxValue) && InRange(v[1], 0, AchievementTracker.MaxCount) && InRange(v[2], 0, AchievementTracker.MaxCount)
                    && InRange(v[3], 0, AchievementTracker.MaxCount);
                if (ok)
                {
                    r.Achievements.Add(new SavedAchievement((int)v[0], (int)v[1], (int)v[2], (int)v[3]));
                }
            }
            if (!ok)
            {
                error = "第 " + no + " 行:" + key + " 的值超出范围";
            }
            return ok;
        }

        private static bool InRange(long value, long min, long max)
        {
            return value >= min && value <= max;
        }
    }
}
