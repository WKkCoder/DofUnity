using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Ai;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf ai-eval [--json &lt;文件&gt;]`(证据 E-128):从怪物出发看 .ai 脚本。
///   第一部分:按 .mob 的 [ai pattern] 找到每个怪物(各难度)用到的 .ai,沿 [import ai] 追到底,
///   统计"哪种条件写法(关键字加运算符或函数名)被多少个怪物用到",按角色(事件、选目的地、动作、移动)分开。
///   只输出个数与标签名、函数名,不输出任何脚本里的值;输出放 out/,不入库。
/// </summary>
internal static class AiEval
{
    private static readonly string[] RoleNames = { "event", "destination", "action", "move" };

    public static int Run(DnfLocation loc, Options opts)
    {
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var data = new GameData(new PvfScriptSource(pvf));
        int[] ids = data.MonsterIds();

        int withAi = 0, noScript = 0, missingFile = 0, unreadable = 0;
        var pathCountHistogram = new SortedDictionary<int, int>();
        // 条件写法 → 用到它的怪物个数 / 结点个数(按脚本去重)
        var monsters = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var nodeCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var scriptsSeen = new HashSet<string>(StringComparer.Ordinal);
        var returnShapes = new SortedDictionary<string, long>(StringComparer.Ordinal);
        // 求值器没读通、交给宿主按"假"处理的条件写法(设置类 [set …] 不算):写法 → 用到的怪物数;以及至少用到一种的怪物数(按角色)
        var unsupported = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var monstersWithUnsupported = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (int id in ids)
        {
            MonsterDef? def = data.GetMonster(id);
            if (def == null)
            {
                noScript++;
                continue;
            }
            if (def.AiPatterns.Count == 0)
            {
                continue;
            }
            withAi++;
            var mine = new HashSet<string>(StringComparer.Ordinal);
            var mineBad = new HashSet<string>(StringComparer.Ordinal);
            foreach (string difficulty in MonsterDef.AiDifficultyNames)
            {
                string[] paths = def.AiPaths(difficulty);
                if (paths.Length == 0)
                {
                    continue;
                }
                pathCountHistogram.TryGetValue(paths.Length, out int pc);
                pathCountHistogram[paths.Length] = pc + 1;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < paths.Length; i++)
                {
                    string role = i < RoleNames.Length ? RoleNames[i] : "extra";
                    Collect(paths[i], role, data, visited, mine, mineBad, ref missingFile, ref unreadable);
                }
                foreach (string v in visited)
                {
                    scriptsSeen.Add(v);
                }
            }
            foreach (string key in mine)
            {
                monsters.TryGetValue(key, out int m);
                monsters[key] = m + 1;
            }
            foreach (string key in mineBad)
            {
                unsupported.TryGetValue(key, out int m);
                unsupported[key] = m + 1;
            }
            foreach (string role in mineBad.Select(k => k.Substring(0, k.IndexOf(' '))).Distinct())
            {
                monstersWithUnsupported.TryGetValue(role, out int m);
                monstersWithUnsupported[role] = m + 1;
            }
        }

        // 结点个数:按脚本(去重后)统计
        foreach (string scriptKey in scriptsSeen)
        {
            int bar = scriptKey.IndexOf('|');
            string role = scriptKey.Substring(0, bar);
            string path = scriptKey.Substring(bar + 1);
            AiScript? script = data.GetAiScript(path);
            if (script == null)
            {
                continue;
            }
            WalkNodes(script.Root, node =>
            {
                string key = role + " " + Signature(node);
                nodeCounts.TryGetValue(key, out long c);
                nodeCounts[key] = c + 1;
            }, leaf =>
            {
                string key = role + " " + LeafShape(leaf);
                returnShapes.TryGetValue(key, out long c);
                returnShapes[key] = c + 1;
            });
        }

        ModelCoverage(loc, data, ids);
        Console.WriteLine("monster/monster.lst:" + ids.Length + " 项;读不到的 " + noScript + " 个;带 [ai pattern] 的 " + withAi + " 个");
        Console.WriteLine("  每个难度列出的 .ai 个数(难度档数):" + string.Join(",  ", pathCountHistogram.Select(p => p.Key + " 个 " + p.Value)));
        Console.WriteLine("  去重后的 (角色, .ai) 对 " + scriptsSeen.Count + " 个;文件缺失 " + missingFile + " 次,读不出 " + unreadable + " 次");
        foreach (string role in RoleNames.Concat(new[] { "extra" }))
        {
            Console.WriteLine("  [" + role + "] 条件写法(用到的怪物数 / 结点数,前 40):");
            foreach (KeyValuePair<string, int> kv in monsters
                         .Where(p => p.Key.StartsWith(role + " ", StringComparison.Ordinal))
                         .OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(40))
            {
                nodeCounts.TryGetValue(kv.Key, out long nc);
                Console.WriteLine("      " + kv.Key.Substring(role.Length + 1).PadRight(54) + kv.Value.ToString().PadLeft(6) + " " + nc.ToString().PadLeft(8));
            }
        }
        Console.WriteLine("  求值器没读通的条件(交给宿主按假处理;设置类不算),角色内至少用到一种的怪物数:"
            + string.Join(",  ", monstersWithUnsupported.Select(p => p.Key + " " + p.Value)));
        foreach (KeyValuePair<string, int> kv in unsupported.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(40))
        {
            Console.WriteLine("      " + kv.Key.PadRight(54) + kv.Value.ToString().PadLeft(6));
        }
        Console.WriteLine("  叶子形状(角色 形状 结点数;N 负数 P 非负数 S 名字 D 没写):");
        foreach (KeyValuePair<string, long> kv in returnShapes)
        {
            Console.WriteLine("      " + kv.Key + " " + kv.Value);
        }
        return 0;
    }

    /// <summary>
    /// 怪物外观覆盖(M36-1):每个怪物的 <see cref="MonsterModel"/> 读出了哪些动画。待机动画读不出的怪物在房间里不出场,
    /// 所以"能出场"= 待机动画不为空。按"带不带 [ai pattern]"分开数,只输出个数。
    /// </summary>
    private static void ModelCoverage(DnfLocation loc, GameData data, int[] ids)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        MonsterCatalog catalog = MonsterCatalog.Read(assets);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Add(string key)
        {
            counts.TryGetValue(key, out int c);
            counts[key] = c + 1;
        }
        foreach (int id in ids)
        {
            MonsterDef? def = data.GetMonster(id);
            string? mobPath = catalog.PathOf(id);
            if (def == null || mobPath == null)
            {
                continue;
            }
            string group = def.AiPatterns.Count > 0 ? "脚本怪物" : "旧式怪物";
            MonsterModel? model = MonsterModel.Read(assets, mobPath);
            Add(group + " 总数");
            if (model == null)
            {
                Add(group + " 读不出模型");
                continue;
            }
            if (model.Waiting != null)
            {
                Add(group + " 能出场(待机动画非空)");
            }
            if (model.Move != null)
            {
                Add(group + " 有行走动画");
            }
            if (model.Damage1 != null)
            {
                Add(group + " 有受击动画");
            }
            if (model.Down != null)
            {
                Add(group + " 有倒地动画");
            }
            if (model.Attacks.Count > 0)
            {
                Add(group + " 有攻击列表");
                if (model.Attacks.Any(a => a != null))
                {
                    Add(group + " 至少一招有动画");
                }
                if (model.Attacks.Any(a => a == null))
                {
                    Add(group + " 有的招没有动画");
                }
            }
            if (model.ActionPaths.Any(p => p != null))
            {
                Add(group + " 动画取自 .act");
            }
            // 受击框审计:动画里任何一帧都没有受击框的状态,主角的攻击打不到它
            if (model.Waiting != null && !HasDamageBox(model.Waiting))
            {
                Add(group + " 待机动画没有受击框");
            }
            if (model.Move != null && !HasDamageBox(model.Move))
            {
                Add(group + " 行走动画没有受击框");
            }
            if (model.Waiting != null && !HasDamageBox(model.Waiting) && (model.Move == null || !HasDamageBox(model.Move))
                && !model.Attacks.Any(a => a != null && HasDamageBox(a)) && !HasDamageBox(model.Damage1))
            {
                Add(group + " 待机、行走、受击、出招动画都没有受击框");
            }
            if (group == "脚本怪物" && model.Waiting != null && !HasDamageBox(model.Waiting) && model.ActionPaths[0] != null)
            {
                // 主动画没有受击框时,受击框会不会在 .act 的 [SUB ANI] 叠加动画里
                bool inSub = false;
                ScriptLine[]? doc = assets.ReadScript(model.ActionPaths[0]);
                if (doc != null)
                {
                    foreach (ScriptLine line in doc)
                    {
                        string? subRel = line.TagIs("[SUB ANI]") ? line.FirstString() : null;
                        if (!string.IsNullOrEmpty(subRel))
                        {
                            AniFile? sub = assets.LoadAni(PvfPath.ResolveRelativeTo(model.ActionPaths[0], subRel));
                            if (HasDamageBox(sub))
                            {
                                inSub = true;
                            }
                        }
                    }
                }
                Add(group + " 待机主动画没有受击框、待机 .act 的 SUB ANI 有:" + (inSub ? "是" : "否"));
            }
            int boxless = model.Attacks.Count(a => a != null && !HasDamageBox(a));
            if (boxless > 0)
            {
                Add(group + " 有出招动画没有受击框");
            }
        }
        Console.WriteLine("怪物外观覆盖(M36-1):");
        foreach (KeyValuePair<string, int> kv in counts)
        {
            Console.WriteLine("      " + kv.Key.PadRight(32) + kv.Value);
        }
    }

    private static bool HasDamageBox(AniFile? ani)
    {
        if (ani == null)
        {
            return false;
        }
        foreach (AniFrame f in ani.Frames)
        {
            foreach (AniBox b in f.Boxes)
            {
                if (b.Type == AniBoxType.Damage)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>沿 [import ai] 把一个脚本及其导入的脚本里出现的条件写法登记到 mine。</summary>
    private static void Collect(string path, string role, GameData data, HashSet<string> visited, HashSet<string> mine, HashSet<string> mineBad,
        ref int missingFile, ref int unreadable)
    {
        string key = role + "|" + path;
        if (!visited.Add(key))
        {
            return;
        }
        AiScript? script = data.GetAiScript(path);
        if (script == null)
        {
            if (data.TryReadBytes(path, out _))
            {
                unreadable++;
            }
            else
            {
                missingFile++;
            }
            return;
        }
        var imports = new List<string>();
        WalkNodes(script.Root, node =>
        {
            mine.Add(role + " " + Signature(node));
            if (!AiEvaluator.IsBuiltin(node) && !(node.Keyword?.StartsWith("[set ", StringComparison.Ordinal) ?? false))
            {
                mineBad.Add(role + " " + Signature(node));
            }
        }, leaf =>
        {
            if (leaf.Kind == AiResultKind.Import && leaf.Name != null)
            {
                imports.Add(leaf.Name);
            }
        });
        foreach (string import in imports)
        {
            Collect(PvfPath.ResolveRelativeTo(path, import), role, data, visited, mine, mineBad, ref missingFile, ref unreadable);
        }
    }

    private static string Signature(AiNode node)
    {
        string prefix = node.StatChangePrefix ? "[stat change] " : "";
        return prefix + node.Keyword + (node.Operator != null ? " " + node.Operator : "");
    }

    private static string LeafShape(AiResult r)
    {
        switch (r.Kind)
        {
            case AiResultKind.Default:
                return "D";
            case AiResultKind.Number:
                return r.Number < 0 ? "N" : "P";
            case AiResultKind.Name:
                return "S";
            case AiResultKind.Import:
                return "I";
            default:
                return "T";
        }
    }

    /// <summary>遍历一个结果树:每个 [think] 结点调 onNode,每个非嵌套的叶子调 onLeaf。</summary>
    internal static void WalkNodes(AiResult result, Action<AiNode> onNode, Action<AiResult> onLeaf)
    {
        if (result.Kind != AiResultKind.Think)
        {
            onLeaf(result);
            return;
        }
        AiNode node = result.Node!;
        onNode(node);
        WalkNodes(node.True, onNode, onLeaf);
        WalkNodes(node.False, onNode, onLeaf);
    }
}
