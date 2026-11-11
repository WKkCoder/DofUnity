using System.Collections.Generic;
using System.Globalization;
using System.Text;

// 本项目自己的设计:副职业的存档字段。原版的副职业数据在服务器数据库里(静态读码:charac_info.expert_job 种类、charac_stat.expert_job_exp 经验、
// charac_expert_job 表的放弃次数、机器耐久、机器档与配方位图),客户端不存;这里只存数字,不含任何原版内容。
// 存什么按服务端存盘字段的取舍:种类、经验、放弃次数、耐久、机器档、配方位图(拆成"学会的配方序号"一行一个);副职业等级不存、读档时由经验重算。
namespace Dnf.Play
{
    /// <summary>存档里的副职业状态:种类(0 到 4)、经验(放弃后是 −1)、放弃次数、分解机耐久与机器档。</summary>
    public readonly struct SavedExpertJob
    {
        public SavedExpertJob(int kind, int exp, int giveupCount, int endurance, int grade)
        {
            Kind = kind;
            Exp = exp;
            GiveupCount = giveupCount;
            Endurance = endurance;
            Grade = grade;
        }

        public int Kind { get; }

        public int Exp { get; }

        public int GiveupCount { get; }

        public int Endurance { get; }

        public int Grade { get; }
    }

    public sealed partial class HeroSave
    {
        /// <summary>副职业状态(键 <c>expertjob 种类 经验 放弃次数 耐久 机器档</c>,一行;没有这一行或旧存档为 null,读成"没有接触过副职业")。</summary>
        public SavedExpertJob? ExpertJob { get; set; }

        /// <summary>学会的副职业配方序号(键 <c>exrecipe 序号</c>,每个一行,从小到大;旧存档没有这些行)。配方怎么学、做什么归制作线。</summary>
        public List<int> ExpertRecipes { get; } = new List<int>();
    }

    /// <summary>副职业存档行的文本读写(挂在 <see cref="HeroSave.ToText"/> 与存档读取的行解析上;键不认得的旧程序会忽略它们)。</summary>
    internal static class ExpertJobSaveFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const string JobKey = "expertjob";
        public const string RecipeKey = "exrecipe";

        /// <summary>种类的上限(分解师之外还有三个,编号 1 到 4)。</summary>
        public const int MaxKind = 4;

        /// <summary>放弃次数的上限(服务端 16 位)。</summary>
        public const int MaxGiveup = ushort.MaxValue;

        /// <summary>机器档的上限(服务端按一个字节查表)。</summary>
        public const int MaxGrade = byte.MaxValue;

        /// <summary>配方序号的个数(30 字节位图)。</summary>
        public const int RecipeCount = 240;

        public static bool Handles(string key)
        {
            return key == JobKey || key == RecipeKey;
        }

        public static void Write(HeroSave save, StringBuilder sb)
        {
            if (save.ExpertJob.HasValue)
            {
                SavedExpertJob j = save.ExpertJob.Value;
                sb.Append(JobKey).Append(' ').Append(j.Kind.ToString(Inv)).Append(' ').Append(j.Exp.ToString(Inv)).Append(' ')
                    .Append(j.GiveupCount.ToString(Inv)).Append(' ').Append(j.Endurance.ToString(Inv)).Append(' ').Append(j.Grade.ToString(Inv)).Append('\n');
            }
            foreach (int index in save.ExpertRecipes)
            {
                sb.Append(RecipeKey).Append(' ').Append(index.ToString(Inv)).Append('\n');
            }
        }

        public static bool Parse(HeroSave r, string key, string[] f, int no, out string error)
        {
            error = null;
            int arity = key == JobKey ? 6 : 2;
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
            if (key == JobKey)
            {
                ok = InRange(v[0], 0, MaxKind) && InRange(v[1], -1, int.MaxValue) && InRange(v[2], 0, MaxGiveup)
                    && InRange(v[3], 0, int.MaxValue) && InRange(v[4], 0, MaxGrade);
                if (ok)
                {
                    r.ExpertJob = new SavedExpertJob((int)v[0], (int)v[1], (int)v[2], (int)v[3], (int)v[4]);
                }
            }
            else
            {
                ok = InRange(v[0], 0, RecipeCount - 1);
                if (ok)
                {
                    r.ExpertRecipes.Add((int)v[0]);
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
