using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-123(服务端静态读码,非 REA)。QuestList::load_list 0x08353d98 读 n_quest/quest.lst(编号 + 路径)再逐个导入 .qst;
// QuestList::insert_NPC 0x08354f02 按"发布任务的 NPC"建表,allowable_questlist_as_npc 0x0835556c 取出某个 NPC 能给的任务。
namespace Dnf.Game
{
    /// <summary>
    /// 任务目录:<c>n_quest/quest.lst</c> 列出所有任务脚本,按编号懒加载并缓存;按发布与交付的 NPC 建索引(第一次用到时整库解一遍)。
    /// </summary>
    public sealed class QuestCatalog
    {
        /// <summary>任务列表的路径。</summary>
        public const string ListPath = "n_quest/quest.lst";

        private static readonly IReadOnlyList<QuestDef> None = new List<QuestDef>();

        private readonly GameData _data;
        private readonly Dictionary<int, QuestDef> _cache = new Dictionary<int, QuestDef>();
        private Dictionary<int, string> _paths;
        private Dictionary<int, List<QuestDef>> _byOffer;
        private Dictionary<int, List<QuestDef>> _byComplete;
        private QuestParameters _parameters;

        public QuestCatalog(GameData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        private Dictionary<int, string> Paths
        {
            get
            {
                if (_paths == null)
                {
                    _paths = new Dictionary<int, string>();
                    Dictionary<int, string> raw = _data.ReadList(ListPath);
                    if (raw != null)
                    {
                        foreach (KeyValuePair<int, string> pair in raw)
                        {
                            _paths[pair.Key] = PvfPath.ResolveRelativeTo(ListPath, pair.Value);
                        }
                    }
                }
                return _paths;
            }
        }

        /// <summary>列表里列了多少个任务。</summary>
        public int ListedCount => Paths.Count;

        /// <summary>奖励参数表(第一次用到时读;脚本不存在得到空参数)。</summary>
        public QuestParameters Parameters
        {
            get
            {
                if (_parameters == null)
                {
                    _parameters = QuestParameters.Parse(_data.ReadScript(QuestParameters.ScriptPath));
                }
                return _parameters;
            }
        }

        /// <summary>列表里的任务编号(升序)。</summary>
        public IReadOnlyList<int> Ids()
        {
            var list = new List<int>(Paths.Keys);
            list.Sort();
            return list;
        }

        /// <summary>按编号取一个任务(缓存);编号不在列表里、脚本读不到返回 null。</summary>
        public QuestDef Get(int id)
        {
            if (_cache.TryGetValue(id, out QuestDef cached))
            {
                return cached;
            }
            QuestDef def = null;
            if (Paths.TryGetValue(id, out string path))
            {
                ScriptLine[] lines = _data.ReadScript(path);
                if (lines != null)
                {
                    def = QuestScript.Parse(lines, id, path);
                }
            }
            _cache[id] = def;
            return def;
        }

        /// <summary>列表里所有读得到的任务(按编号升序;第一次调用要把整库解一遍)。</summary>
        public IReadOnlyList<QuestDef> All()
        {
            var all = new List<QuestDef>();
            foreach (int id in Ids())
            {
                QuestDef q = Get(id);
                if (q != null)
                {
                    all.Add(q);
                }
            }
            return all;
        }

        /// <summary>这个 NPC 发布的任务(按编号升序);没有返回空表。</summary>
        public IReadOnlyList<QuestDef> OfferedBy(int npcId)
        {
            EnsureIndex();
            return _byOffer.TryGetValue(npcId, out List<QuestDef> list) ? list : None;
        }

        /// <summary>要回到这个 NPC 那里交的任务(<c>[complete npc index]</c> 等于它;按编号升序);没有返回空表。</summary>
        public IReadOnlyList<QuestDef> CompletedAt(int npcId)
        {
            EnsureIndex();
            return _byComplete.TryGetValue(npcId, out List<QuestDef> list) ? list : None;
        }

        private void EnsureIndex()
        {
            if (_byOffer != null)
            {
                return;
            }
            var offer = new Dictionary<int, List<QuestDef>>();
            var complete = new Dictionary<int, List<QuestDef>>();
            foreach (QuestDef q in All())
            {
                Add(offer, q.NpcId, q);
                int done = q.CompleteNpcId;
                if (q.Kind == QuestKind.MeetNpc && q.MeetNpcId() >= 0)
                {
                    done = q.MeetNpcId();
                }
                Add(complete, done, q);
            }
            _byOffer = offer;
            _byComplete = complete;
        }

        private static void Add(Dictionary<int, List<QuestDef>> map, int npc, QuestDef q)
        {
            if (npc < 0)
            {
                return;
            }
            if (!map.TryGetValue(npc, out List<QuestDef> list))
            {
                list = new List<QuestDef>();
                map[npc] = list;
            }
            list.Add(q);
        }
    }
}
