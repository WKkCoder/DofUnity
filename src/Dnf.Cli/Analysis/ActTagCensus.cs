using Dnf.Game.Act;

namespace Dnf.Cli.Analysis;

/// <summary>
/// "运行器还没落实的标签波及多少个对象"的普查(汇总数,本机脚本扫描,非 REA;只输出标签名与对象个数,不输出路径、编号与脚本里的值)。
/// 同一个对象里同一个标签出现多少次都只算一个对象;编译器认得、运行器不落实的行为(<see cref="ActBehaviorKind.Unsupported"/> 以外的种类)不在这里,
/// 所以一个标签一旦被落实,它就从这张表里消失,"落实前后"的对比就是两次输出的差。
/// </summary>
internal sealed class ActTagCensus
{
    private readonly Dictionary<string, HashSet<int>> _behavior = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _trigger = new(StringComparer.Ordinal);

    /// <summary>把一份 .act 里读不懂的触发器标签与行为标签记到对象 <paramref name="id"/> 名下。</summary>
    public void Note(ActProgram program, int id)
    {
        foreach (ActTrigger t in program.Triggers)
        {
            if (t.Supported)
            {
                continue;
            }
            var tags = new SortedSet<string>(t.UnsupportedTags, StringComparer.Ordinal);
            foreach (ActRecord r in t.Records)
            {
                if (r.Kind == ActRecordKind.Unsupported && r.Tag != null)
                {
                    tags.Add(r.Tag);
                }
            }
            foreach (string tag in tags)
            {
                Mark(_trigger, tag, id);
            }
        }
        foreach (ActBehaviorGroup g in program.Behaviors)
        {
            foreach (string tag in g.UnsupportedTags)
            {
                Mark(_behavior, tag, id);
            }
        }
    }

    private static void Mark(Dictionary<string, HashSet<int>> map, string tag, int id)
    {
        if (!map.TryGetValue(tag, out HashSet<int>? set))
        {
            set = new HashSet<int>();
            map[tag] = set;
        }
        set.Add(id);
    }

    /// <summary>某个行为标签波及的对象个数(没有就是 0)。</summary>
    public int BehaviorObjects(string tag) => _behavior.TryGetValue(tag, out HashSet<int>? set) ? set.Count : 0;

    /// <summary>打印一张"标签 对象数"表(按对象数降序,同数按标签名),最多 <paramref name="limit"/> 项。</summary>
    public static string Join(Dictionary<string, HashSet<int>> map, int limit)
    {
        if (map.Count == 0)
        {
            return "无";
        }
        var items = new List<KeyValuePair<string, int>>();
        foreach (KeyValuePair<string, HashSet<int>> kv in map)
        {
            items.Add(new KeyValuePair<string, int>(kv.Key, kv.Value.Count));
        }
        items.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
        var parts = new List<string>();
        for (int i = 0; i < items.Count && i < limit; i++)
        {
            parts.Add(items[i].Key + " " + items[i].Value);
        }
        return string.Join(",", parts) + (items.Count > limit ? ",…共 " + items.Count + " 种" : string.Empty);
    }

    public void Print(string title, int limit)
    {
        Console.WriteLine("读不懂的标签波及的" + title + "数(同一对象只算一次):");
        Console.WriteLine("  触发器标签:" + Join(_trigger, limit));
        Console.WriteLine("  行为标签:" + Join(_behavior, limit));
    }

    /// <summary>怪物一侧的同一张表(<see cref="ActRunCheck"/> 里已经按怪物攒好了,这里只是全量打印,不截断到前几名)。</summary>
    public static void PrintMonsterBehaviors(Dictionary<string, HashSet<int>> map, int limit)
    {
        Console.WriteLine("读不懂的行为标签波及的怪物数(出招与其余动作合计,全量表,同一怪物只算一次):" + Join(map, limit));
    }
}
