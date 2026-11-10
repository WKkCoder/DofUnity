using System;
using Dnf.Data;

// 证据:服务端 importGoldLimitByLevel 0x088fdb69(整张表先清零 400 字节,再在 [gold limit from level] 后面最多读 100 对"等级 上限",写进下标等级处;读完不要求结束标签)、
// CDataManager::GetMoneyLimitPerLevel 0x08360ce0(等级先夹到 1 到 200 再按下标取)、CUser::CheckMoney 0x0866af1c(现有金币 + 要取的数 不超过上限才通过,等于也通过)。
namespace Dnf.Game
{
    /// <summary>
    /// 角色按等级的金币携带上限(<c>Etc/GoldLimitByLevel.etc</c>)。服务端的表只有 100 项(下标 0 到 99),查询时等级夹到 1 到 200:
    /// 下标超过 99 时服务端会读到表后面的内存,读出来是什么没有查明(<b>未知</b>);这里取表尾(最后一项)。上限的具体数值是原版数据。
    /// </summary>
    public sealed class GoldLimitTable
    {
        public const string Path = "Etc/GoldLimitByLevel.etc";

        /// <summary>表有几项(服务端数组 400 字节,每项 4 字节)。</summary>
        public const int Entries = 100;

        /// <summary>查询时等级的下限(服务端夹到 1)。</summary>
        public const int MinLevel = 1;

        /// <summary>查询时等级的上限(服务端夹到 200)。</summary>
        public const int MaxLevel = 200;

        private readonly int[] _limits = new int[Entries];

        /// <summary>用给定的上限构造(第 i 项是等级 i 的上限,不足 100 项的位置补 0,多出的忽略);测试用。</summary>
        public GoldLimitTable(params int[] limits)
        {
            int n = Math.Min(limits == null ? 0 : limits.Length, _limits.Length);
            for (int i = 0; i < n; i++)
            {
                _limits[i] = limits[i];
            }
        }

        /// <summary>表里第 <paramref name="index"/> 项的原始值(0 到 99;没写的是 0)。</summary>
        public int Entry(int index)
        {
            return (uint)index < (uint)_limits.Length ? _limits[index] : 0;
        }

        /// <summary>等级 <paramref name="level"/> 的携带上限:等级夹到 1 到 200,再按下标取;超出表尾的取最后一项。</summary>
        public long LimitOf(int level)
        {
            int clamped = level < MinLevel ? MinLevel : level > MaxLevel ? MaxLevel : level;
            return _limits[clamped < Entries ? clamped : Entries - 1];
        }

        /// <summary>读一份脚本的原始字节;不是标记流脚本返回 null。</summary>
        public static GoldLimitTable Parse(ReadOnlySpan<byte> data, PvfStringTable strings)
        {
            if (strings == null || !TokenScript.IsTokenScript(data))
            {
                return null;
            }
            return Parse(TokenScript.Parse(data), strings);
        }

        /// <summary>
        /// 读一份已经拆成标记的脚本:遇到 <c>[gold limit from level]</c> 就读"等级 上限"的对子,最多 100 对、读到读不出整数为止;
        /// 等级不在 0 到 99 的对子服务端会写到表外(没有边界检查),这里只读掉、不存。
        /// </summary>
        public static GoldLimitTable Parse(ScriptToken[] tokens, PvfStringTable strings)
        {
            var cursor = new ScriptCursor(tokens, strings);
            var table = new GoldLimitTable();
            while (cursor.ScanType(out string name))
            {
                if (!string.Equals(name, "[gold limit from level]", StringComparison.Ordinal))
                {
                    continue;
                }
                for (int i = 0; i < Entries; i++)
                {
                    int level = cursor.ScanInt(out bool ok);
                    if (!ok)
                    {
                        break;
                    }
                    int limit = cursor.ScanInt(out bool _);
                    if ((uint)level < (uint)Entries)
                    {
                        table._limits[level] = limit;
                    }
                }
            }
            return table;
        }

        /// <summary>从数据里读上限表;读不到返回 null(调用方可以不查携带上限)。</summary>
        public static GoldLimitTable Read(GameData data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            PvfStringTable strings = data.Strings;
            if (strings == null || !data.TryReadBytes(Path, out byte[] bytes))
            {
                return null;
            }
            try
            {
                return Parse(bytes, strings);
            }
            catch (DnfFormatException)
            {
                return null;
            }
        }
    }
}
