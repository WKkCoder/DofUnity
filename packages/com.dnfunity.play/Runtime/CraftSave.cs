using System.Collections.Generic;
using System.Globalization;
using System.Text;

// 本项目自己的设计:配方簿的存档字段。服务端的"已学配方"是副职业状态里的一段位图(CCharacExpertJob::set_recipe 0x0832b264,存在角色数据库里),
// 客户端不存;这里只存数字——每个学会的配方一行,内容就是配方物品的编号,不含任何原版内容。
namespace Dnf.Play
{
    public sealed partial class HeroSave
    {
        /// <summary>学会的配方(键 <c>recipe 配方物品编号</c>,每个一行,编号从小到大;旧存档没有这些行)。</summary>
        public List<int> Recipes { get; } = new List<int>();
    }

    /// <summary>配方存档行的文本读写(挂在 <see cref="HeroSave.ToText"/> 与存档读取的行解析上;键不认得的旧程序会忽略这些行)。</summary>
    internal static class CraftSaveFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const string RecipeKey = "recipe";

        public static bool Handles(string key)
        {
            return key == RecipeKey;
        }

        public static void Write(HeroSave save, StringBuilder sb)
        {
            foreach (int code in save.Recipes)
            {
                sb.Append(RecipeKey).Append(' ').Append(code.ToString(Inv)).Append('\n');
            }
        }

        public static bool Parse(HeroSave r, string[] f, int no, out string error)
        {
            error = null;
            if (f.Length != 2)
            {
                error = "第 " + no + " 行:" + RecipeKey + " 需要 1 个值";
                return false;
            }
            if (!long.TryParse(f[1], NumberStyles.Integer, Inv, out long code))
            {
                error = "第 " + no + " 行:" + RecipeKey + " 的值不是整数";
                return false;
            }
            if (code < 1 || code > int.MaxValue)
            {
                error = "第 " + no + " 行:" + RecipeKey + " 的值超出范围";
                return false;
            }
            r.Recipes.Add((int)code);
            return true;
        }
    }
}
