using System;
using System.Globalization;
using System.Text;

// 本项目自己的设计:成长系统(疲劳)的存档字段。原版的疲劳存在服务器数据库里,客户端不存;这里只存数字:已耗点数与上次玩的时刻。
// 上限不存(由规则给,存档里写了也不认);等级无关(服务端读码,见 Dnf.Game 的 Fatigue)。
namespace Dnf.Play
{
    public sealed partial class HeroSave
    {
        /// <summary>疲劳已耗的点数(键 <c>fatigue 已耗 时刻</c> 的第一个值;旧存档没有这一行 = 0)。</summary>
        public int FatigueConsumed { get; set; }

        /// <summary>上次玩的时刻(本地时间,键 <c>fatigue</c> 的第二个值;没有这一行或值为 0 = 没记过,下次刷新当作到点恢复)。存档里只到秒。</summary>
        public DateTime? FatigueLastPlay { get; set; }
    }

    /// <summary>疲劳存档行的文本读写(挂在 <see cref="HeroSave.ToText"/> 与存档读取的行解析上;键不认得的旧程序会忽略它)。</summary>
    internal static class GrowthSaveFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public const string FatigueKey = "fatigue";

        /// <summary>时刻的文本写法:年月日时分秒连写成 14 位数字(如 <c>20260101060000</c>),0 表示没有。</summary>
        public const string StampFormat = "yyyyMMddHHmmss";

        /// <summary>已耗的最大写法(服务端是 16 位整数)。</summary>
        public const int MaxConsumed = 65535;

        public static bool Handles(string key)
        {
            return key == FatigueKey;
        }

        /// <summary>只在有内容(已耗不为 0 或记过时刻)时写一行,所以从没碰过疲劳的存档文本与旧版本一字不差。</summary>
        public static void Write(HeroSave save, StringBuilder sb)
        {
            if (save.FatigueConsumed == 0 && !save.FatigueLastPlay.HasValue)
            {
                return;
            }
            sb.Append(FatigueKey).Append(' ').Append(save.FatigueConsumed.ToString(Inv)).Append(' ')
                .Append(save.FatigueLastPlay.HasValue ? save.FatigueLastPlay.Value.ToString(StampFormat, Inv) : "0").Append('\n');
        }

        public static bool Parse(HeroSave r, string key, string[] f, int no, out string error)
        {
            error = null;
            if (f.Length != 3)
            {
                error = "第 " + no + " 行:" + key + " 需要 2 个值";
                return false;
            }
            if (!long.TryParse(f[1], NumberStyles.Integer, Inv, out long consumed))
            {
                error = "第 " + no + " 行:" + key + " 的值不是整数";
                return false;
            }
            DateTime? stamp = null;
            if (f[2] != "0")
            {
                if (!DateTime.TryParseExact(f[2], StampFormat, Inv, DateTimeStyles.None, out DateTime parsed))
                {
                    error = "第 " + no + " 行:" + key + " 的时刻写坏了";
                    return false;
                }
                stamp = parsed;
            }
            if (consumed < 0 || consumed > MaxConsumed)
            {
                error = "第 " + no + " 行:" + key + " 的值超出范围";
                return false;
            }
            r.FatigueConsumed = (int)consumed;
            r.FatigueLastPlay = stamp;
            return true;
        }
    }
}
