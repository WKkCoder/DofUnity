using System.Collections.Generic;
using Dnf.Data;

// 证据:E-125(服务端加载函数 0x08913c7e 读 Etc/UltimateDungeonList.etc,静态读码,非 REA;CParty::DungeonPermission 0x085b12f8 用它决定哪些副本能开放难度 3)。
namespace Dnf.Game
{
    /// <summary>
    /// 有"难度 3"(服务端叫 ultimate)的副本清单。文件 <c>etc/ultimatedungeonlist.etc</c> 里 <c>[apply ultimate]</c> 到 <c>[/apply ultimate]</c> 之间是副本编号。
    /// 服务端只在清单里的副本通关难度 2 时才有机会记下"难度 3 开放"(见 <see cref="DungeonClears"/>),清单外的副本最高只能选到难度 2。
    /// 清单的内容是用户自己的数据,这里不记录。
    /// </summary>
    public sealed class UltimateDungeonList
    {
        public const string Path = "etc/ultimatedungeonlist.etc";

        public const string Tag = "[apply ultimate]";

        public static readonly UltimateDungeonList Empty = new UltimateDungeonList(null);

        private readonly HashSet<int> _ids;

        private UltimateDungeonList(IEnumerable<int> ids)
        {
            _ids = ids == null ? new HashSet<int>() : new HashSet<int>(ids);
        }

        /// <summary>清单里有几个副本。</summary>
        public int Count => _ids.Count;

        /// <summary>这个副本有没有难度 3。</summary>
        public bool Contains(int dungeonId)
        {
            return _ids.Contains(dungeonId);
        }

        /// <summary>读 <c>[apply ultimate]</c> 块里的全部整数(多个块也合并;<paramref name="lines"/> 为 null 返回 <see cref="Empty"/>)。</summary>
        public static UltimateDungeonList Parse(ScriptLine[] lines)
        {
            if (lines == null)
            {
                return Empty;
            }
            var ids = new List<int>();
            foreach (ScriptLine line in lines)
            {
                if (line.TagIs(Tag))
                {
                    ids.AddRange(line.Ints());
                }
            }
            return ids.Count == 0 ? Empty : new UltimateDungeonList(ids);
        }
    }
}
