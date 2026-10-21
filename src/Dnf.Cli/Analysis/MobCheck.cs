using System.Globalization;
using System.Text;
using System.Text.Json;
using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf mob-check [--dump &lt;文件&gt;] [--json &lt;文件&gt;]`(证据 E-062):用 Dnf.Game 的 MonsterDef 读全库 monster/monster.lst 列出的 .mob,
///   统计:列表项数、文件缺失、不是标记流、成对标签(两个整数)的不规整、[level] 范围的形状、[ability category] 的类别与运算符直方图、
///   基础属性表的选择与命中情况、引用到的 .act/.ani/.atk/.ai 路径是否存在(按相对 .mob 目录解析),
///   [attack kind] 的结构(整数个数是 12 的倍数、三个概率在 0 到 100、射程非负;记录条数与 [attack motion] 个数的对应只报告,证据 E-100),
///   以及按等级算出属性后的基本合理性(生命上限为正)。
///   --dump 写出每个怪物在若干等级与种类下算出的完整数值块(每行 编号、等级、种类、整数列表),
///   可与 `tools/py/mob_ability_ref.py --dump` 的输出逐行对拍(两份独立实现)。
///   控制台与 --json 只输出个数、标签名与类别名,不输出任何原版数值;--dump 文件含原版派生数值,放 out/,不入库。
/// </summary>
internal static class MobCheck
{
    private static readonly string[] PairTags =
    {
        "[level]", "[HP regen speed]", "[MP regen speed]", "[move speed]", "[attack speed]", "[cast speed]", "[hit recovery]",
        "[jump power]", "[jump speed]", "[normal dash speed]", "[slant dash speed]", "[weight]", "[rigidity]", "[width]",
    };

    private static readonly string[] SingleAssetTags =
    {
        "[waiting motion]", "[move motion]", "[sit motion]", "[damage motion 1]", "[damage motion 2]", "[down motion]",
        "[overturn motion]", "[jump motion]", "[waiting action]", "[move action]", "[sit action]", "[damage action 1]",
        "[damage action 2]", "[down action]", "[overturn action]", "[jump action]", "[dash action]", "[moveback action]",
    };

    private sealed class AssetCounter
    {
        public int Refs;
        public int Missing;
        public readonly List<string> Samples = new();
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var source = new PvfScriptSource(pvf);
        var data = new GameData(source);
        int[] ids = data.MonsterIds();
        Console.WriteLine("monster/monster.lst:" + ids.Length + " 项");

        int missingFile = 0, notParsed = 0, parsed = 0;
        var pairIrregular = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var pairPresent = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int levelMissing = 0, levelInverted = 0, levelFlat = 0, levelRange = 0;
        int categoryDefs = 0, categoryEntries = 0, categoryDroppedEntries = 0, categoryBadShape = 0;
        var categoryNames = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var categoryOps = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var tableChosen = new SortedDictionary<int, int>();
        int ownTableDefs = 0, ownTableLoaded = 0, ownTableShorterThanCommon = 0;
        int rowMissingMin = 0, rowMissingMax = 0, hpNonPositiveMin = 0, hpNonPositiveMax = 0, computed = 0;
        int statusResistanceTagsAbsentAll = 0;
        // [attack kind](E-100):有标签的文件数、整数个数不是 12 的倍数的文件数、记录总数、记录条数与 [attack motion] 个数对不上的文件数、
        // 有射程范围的记录数、有攻击动作却没有这个标签的文件数、概率超出 0 到 100 的记录数。只统计个数,不输出任何数值。
        int attackKindDefs = 0, attackKindBadShape = 0, attackKindRecords = 0, attackKindCountMismatch = 0, attackKindWithExtents = 0;
        int attackKindMissing = 0, attackKindChanceOutOfRange = 0, attackKindNegativeReach = 0;
        var assets = new SortedDictionary<string, AssetCounter>(StringComparer.Ordinal);
        var dump = new StringBuilder();

        AssetCounter Asset(string name)
        {
            if (!assets.TryGetValue(name, out AssetCounter? c))
            {
                c = new AssetCounter();
                assets.Add(name, c);
            }
            return c;
        }

        void Check(string kind, MonsterDef def, string? resolved)
        {
            AssetCounter c = Asset(kind);
            c.Refs++;
            if (resolved == null || !pvf.TryGetEntry(resolved, out _))
            {
                c.Missing++;
                if (c.Samples.Count < 5)
                {
                    c.Samples.Add(def.Path + " → " + (resolved ?? "(null)"));
                }
            }
        }

        MonsterBaseTables baseTables = data.MonsterBaseTables;
        for (int i = 0; i < MonsterBaseTables.TableCount; i++)
        {
            Console.WriteLine("基础属性表 " + MonsterBaseTables.Paths[i] + ":" + baseTables[i].Count + " 行"
                + (baseTables[i].Count == 0 ? "(空或缺失)" : ""));
        }

        foreach (int id in ids)
        {
            string? path = data.GetMonsterPath(id);
            if (path == null || !pvf.TryGetEntry(path, out _))
            {
                missingFile++;
                continue;
            }
            MonsterDef? def = data.GetMonster(id);
            if (def == null)
            {
                notParsed++;
                continue;
            }
            parsed++;

            foreach (string tag in PairTags)
            {
                if (!def.Tags.Has(tag))
                {
                    continue;
                }
                pairPresent.TryGetValue(tag, out int pp);
                pairPresent[tag] = pp + 1;
                if (def.Tags.Ints(tag).Length != 2)
                {
                    pairIrregular.TryGetValue(tag, out int pi);
                    pairIrregular[tag] = pi + 1;
                }
            }
            bool anyStatus = false;
            foreach (string tag in MonsterDef.StatusResistanceTags)
            {
                if (def.Tags.Has(tag))
                {
                    anyStatus = true;
                    if (def.Tags.Ints(tag).Length != 2)
                    {
                        pairIrregular.TryGetValue(tag, out int pi);
                        pairIrregular[tag] = pi + 1;
                    }
                }
            }
            statusResistanceTagsAbsentAll += anyStatus ? 0 : 1;
            foreach (string tag in MonsterDef.ElementResistanceTags)
            {
                if (def.Tags.Has(tag) && def.Tags.Ints(tag).Length != 2)
                {
                    pairIrregular.TryGetValue(tag, out int pi);
                    pairIrregular[tag] = pi + 1;
                }
            }

            if (!def.Tags.Has("[level]"))
            {
                levelMissing++;
            }
            else if (def.Level.Min > def.Level.Max)
            {
                levelInverted++;
            }
            else if (def.Level.Min == def.Level.Max)
            {
                levelFlat++;
            }
            else
            {
                levelRange++;
            }

            if (def.Tags.Has("[ability category]"))
            {
                categoryDefs++;
                // 逐个三元组核对:名字、运算符、数值
                ScriptValue[] v = def.Tags.TryGetLine("[ability category]", out ScriptLine line) ? line.Values : Array.Empty<ScriptValue>();
                int triples = v.Length / 3;
                if (v.Length % 3 != 0)
                {
                    categoryBadShape++;
                }
                for (int k = 0; k + 2 < v.Length; k += 3)
                {
                    string name = v[k].Text ?? "?";
                    string op = v[k + 1].Text ?? "?";
                    categoryNames.TryGetValue(name, out int cn);
                    categoryNames[name] = cn + 1;
                    categoryOps.TryGetValue(op, out int co);
                    categoryOps[op] = co + 1;
                }
                categoryEntries += triples;
                categoryDroppedEntries += triples - def.AbilityCategories.Length;
            }

            if (def.Tags.TryGetLine("[attack kind]", out ScriptLine kindLine))
            {
                attackKindDefs++;
                if (kindLine.Values.Length % AttackKindRecord.TokensPerRecord != 0)
                {
                    attackKindBadShape++;
                }
                AttackKindRecord[] kinds = AttackKindRecord.Parse(def.Tags);
                attackKindRecords += kinds.Length;
                if (kinds.Length != def.AttackMotions.Length)
                {
                    attackKindCountMismatch++;
                }
                foreach (AttackKindRecord rec in kinds)
                {
                    attackKindWithExtents += rec.HasExtents ? 1 : 0;
                    if (rec.ChanceInRange < 0 || rec.ChanceInRange > 100 || rec.ChanceAnywhere < 0 || rec.ChanceAnywhere > 100
                        || rec.ChanceObject < 0 || rec.ChanceObject > 100)
                    {
                        attackKindChanceOutOfRange++;
                    }
                    if (rec.FrontReach < 0 || rec.BackReach < 0 || rec.YMinusReach < 0 || rec.YPlusReach < 0)
                    {
                        attackKindNegativeReach++;
                    }
                }
            }
            else if (def.AttackMotions.Length > 0)
            {
                attackKindMissing++;
            }

            MonsterBaseTable? own = null;
            if (def.BaseTablePaths.Length > 0)
            {
                ownTableDefs++;
                own = data.GetMonsterOwnTable(def);
                if (own != null)
                {
                    ownTableLoaded++;
                }
                foreach (string rel in def.BaseTablePaths)
                {
                    Check("[base table]", def, PvfPath.ResolveRelativeTo(GameData.MonsterListPath, rel));
                }
            }

            int levelMin = def.Level.Min, levelMax = def.Level.Max;
            int chosen = MonsterAbility.SelectTable(def, 0, MonsterKind.Normal, false, false);
            tableChosen.TryGetValue(chosen, out int tc);
            tableChosen[chosen] = tc + 1;
            if (own != null && own.Count < baseTables[chosen].Count)
            {
                ownTableShorterThanCommon++;
            }
            int[] levels = { levelMin, (levelMin + levelMax) / 2, levelMax };
            foreach (MonsterKind kind in new[] { MonsterKind.Normal, MonsterKind.Boss })
            {
                for (int li = 0; li < levels.Length; li++)
                {
                    if (li == 1 && levels[1] == levels[0])
                    {
                        continue;
                    }
                    MonsterStats s = MonsterAbility.Compute(def, baseTables, own, levels[li], 0, kind);
                    if (kind == MonsterKind.Normal && li != 1)
                    {
                        computed++;
                        int table = MonsterAbility.SelectTable(def, 0, kind, false, false);
                        if (baseTables[table].IndexOfLevel(levels[li]) < 0)
                        {
                            if (li == 0)
                            {
                                rowMissingMin++;
                            }
                            else
                            {
                                rowMissingMax++;
                            }
                        }
                        else if (s.HpMax <= 0)
                        {
                            if (li == 0)
                            {
                                hpNonPositiveMin++;
                            }
                            else
                            {
                                hpNonPositiveMax++;
                            }
                        }
                    }
                    if (opts.DumpFile != null)
                    {
                        dump.Append(id.ToString(CultureInfo.InvariantCulture)).Append('\t')
                            .Append(levels[li].ToString(CultureInfo.InvariantCulture)).Append('\t')
                            .Append(((int)kind).ToString(CultureInfo.InvariantCulture)).Append('\t');
                        for (int k = 0; k < MonsterStats.Size; k++)
                        {
                            if (k > 0)
                            {
                                dump.Append(',');
                            }
                            dump.Append(s.Raw[k].ToString(CultureInfo.InvariantCulture));
                        }
                        dump.Append('\n');
                    }
                }
            }

            // 引用到的资源路径(相对 .mob 目录解析)
            foreach (string tag in SingleAssetTags)
            {
                string? s = def.Tags.FirstString(tag);
                if (string.IsNullOrEmpty(s))
                {
                    continue;
                }
                Check(tag, def, def.ActionPath(tag));
            }
            for (int k = 0; k < def.AttackActions.Length; k++)
            {
                if (!string.IsNullOrEmpty(def.AttackActions[k]))
                {
                    Check("[attack action]", def, def.AttackActionPath(k));
                }
            }
            for (int k = 0; k < def.AttackMotions.Length; k++)
            {
                if (!string.IsNullOrEmpty(def.AttackMotions[k]))
                {
                    Check("[attack motion]", def, def.AttackMotionPath(k));
                }
            }
            for (int k = 0; k < def.AttackInfos.Length; k++)
            {
                if (!string.IsNullOrEmpty(def.AttackInfos[k]))
                {
                    Check("[attack info]", def, def.AttackInfoPath(k));
                }
            }
            for (int k = 0; k < def.EtcMotions.Length; k++)
            {
                if (!string.IsNullOrEmpty(def.EtcMotions[k]))
                {
                    Check("[etc motion]", def, def.CustomAnimationPath(k));
                }
            }
            for (int k = 0; k < def.EtcAttackInfos.Length; k++)
            {
                if (!string.IsNullOrEmpty(def.EtcAttackInfos[k]))
                {
                    Check("[etc attack info]", def, def.CustomAttackInfoPath(k));
                }
            }
            foreach (string difficulty in MonsterDef.AiDifficultyNames)
            {
                foreach (string ai in def.AiPaths(difficulty))
                {
                    Check("[ai pattern]", def, ai);
                }
            }
        }

        Console.WriteLine("已解析 " + parsed + " 个 .mob;列表里的文件不存在 " + missingFile + ";读不出 " + notParsed);
        Console.WriteLine("[level]:缺 " + levelMissing + ",最小大于最大 " + levelInverted + ",最小等于最大 " + levelFlat + ",有范围 " + levelRange);
        Console.WriteLine("一个异常状态抗性标签也没有的怪物:" + statusResistanceTagsAbsentAll);
        Console.WriteLine("成对标签的整数个数不是 2 的次数(有该标签的文件数/不规整数):");
        foreach (KeyValuePair<string, int> kv in pairPresent)
        {
            pairIrregular.TryGetValue(kv.Key, out int bad);
            Console.WriteLine("  " + kv.Key + ":" + kv.Value + " / " + bad);
        }
        foreach (KeyValuePair<string, int> kv in pairIrregular)
        {
            if (!pairPresent.ContainsKey(kv.Key))
            {
                Console.WriteLine("  " + kv.Key + ":不规整 " + kv.Value);
            }
        }
        Console.WriteLine("[ability category]:" + categoryDefs + " 个文件,共 " + categoryEntries + " 项,值数目不是 3 的倍数 " + categoryBadShape
            + " 个文件,被解析器丢弃(不认识的类别名)" + categoryDroppedEntries + " 项");
        Console.WriteLine("  类别名:" + string.Join(",", categoryNames.Select(kv => kv.Key + "×" + kv.Value)));
        Console.WriteLine("  运算符:" + string.Join(",", categoryOps.Select(kv => "\"" + kv.Key + "\"×" + kv.Value)));
        Console.WriteLine("[attack kind](E-100):" + attackKindDefs + " 个文件,共 " + attackKindRecords + " 条记录,整数个数不是 12 的倍数 " + attackKindBadShape
            + " 个文件,记录条数与 [attack motion] 个数对不上 " + attackKindCountMismatch + " 个文件,有射程范围的记录 " + attackKindWithExtents
            + " 条,概率超出 0 到 100 的记录 " + attackKindChanceOutOfRange + " 条,射程为负的记录 " + attackKindNegativeReach
            + " 条;有攻击动作却没有这个标签的文件 " + attackKindMissing);
        Console.WriteLine("选基础属性表(等级取最小、普通种类、不带副本参数):" + string.Join(",", tableChosen.Select(kv => "表" + kv.Key + "×" + kv.Value)));
        Console.WriteLine("带 [base table] 的怪物:" + ownTableDefs + ",其中读到表的 " + ownTableLoaded + ",自带表比公共表行数少的 " + ownTableShorterThanCommon);
        Console.WriteLine("按等级算属性:共 " + computed + " 次(最小/最大等级各一次)。查不到表行:最小 " + rowMissingMin + "、最大 " + rowMissingMax
            + ";查到行但生命上限不为正:最小 " + hpNonPositiveMin + "、最大 " + hpNonPositiveMax);
        Console.WriteLine("引用到的资源路径(引用数/不存在数):");
        foreach (KeyValuePair<string, AssetCounter> kv in assets)
        {
            Console.WriteLine("  " + kv.Key + ":" + kv.Value.Refs + " / " + kv.Value.Missing);
            foreach (string sample in kv.Value.Samples)
            {
                Console.WriteLine("      例:" + sample);
            }
        }

        if (opts.DumpFile != null)
        {
            File.WriteAllText(opts.DumpFile, dump.ToString(), new UTF8Encoding(false));
            Console.WriteLine("已写出 " + opts.DumpFile + "(含原版派生数值,不入库)");
        }
        if (opts.JsonFile != null)
        {
            var summary = new SortedDictionary<string, object>(StringComparer.Ordinal)
            {
                ["listed"] = ids.Length,
                ["parsed"] = parsed,
                ["missingFile"] = missingFile,
                ["notParsed"] = notParsed,
                ["levelMissing"] = levelMissing,
                ["levelInverted"] = levelInverted,
                ["levelFlat"] = levelFlat,
                ["levelRange"] = levelRange,
                ["pairPresent"] = pairPresent,
                ["pairIrregular"] = pairIrregular,
                ["categoryDefs"] = categoryDefs,
                ["categoryEntries"] = categoryEntries,
                ["categoryDroppedEntries"] = categoryDroppedEntries,
                ["categoryNames"] = categoryNames,
                ["categoryOps"] = categoryOps,
                ["attackKindDefs"] = attackKindDefs,
                ["attackKindBadShape"] = attackKindBadShape,
                ["attackKindRecords"] = attackKindRecords,
                ["attackKindCountMismatch"] = attackKindCountMismatch,
                ["attackKindWithExtents"] = attackKindWithExtents,
                ["attackKindMissing"] = attackKindMissing,
                ["attackKindChanceOutOfRange"] = attackKindChanceOutOfRange,
                ["attackKindNegativeReach"] = attackKindNegativeReach,
                ["tableChosen"] = tableChosen.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value),
                ["ownTableDefs"] = ownTableDefs,
                ["ownTableLoaded"] = ownTableLoaded,
                ["rowMissingMin"] = rowMissingMin,
                ["rowMissingMax"] = rowMissingMax,
                ["hpNonPositiveMin"] = hpNonPositiveMin,
                ["hpNonPositiveMax"] = hpNonPositiveMax,
                ["assets"] = assets.ToDictionary(kv => kv.Key, kv => new { refs = kv.Value.Refs, missing = kv.Value.Missing }),
            };
            File.WriteAllText(opts.JsonFile, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            Console.WriteLine("已写出 " + opts.JsonFile);
        }

        // [attack kind] 的结构应当规整:整数个数是 12 的倍数、三个概率在 0 到 100、四个射程非负(这是 E-100 对各项含义的旁证);记录条数与动作个数对不上只报告
        bool ok = missingFile == 0 && notParsed == 0 && categoryDroppedEntries == 0
            && attackKindBadShape == 0 && attackKindChanceOutOfRange == 0 && attackKindNegativeReach == 0;
        return ok ? 0 : 1;
    }

    /// <summary>
    /// `dnf mob-check --cases &lt;文件&gt;`:逐行读 tools/py/mob_ability_oracle.py --write-cases 写出的合成用例(输入全是随机合成的,
    /// 期望输出是服务端原版函数在 Unicorn 里算出来的),用 C# 的 MonsterAbility 重算并比较 188 个数值槽(槽 5 是字节,只比低字节)。
    /// 五张公共基础属性表读自 PVF,所以期望输出含原版派生数值,用例文件放 out/,不入库。
    /// 每行七个制表符分隔的字段:编号、种类(0 或 3)、等级、副本参数、脚本(以 | 分隔的 标签=值,值…;整数或带引号的字符串)、
    /// 自带表(- 或逗号分隔的整数,每行 24 个)、期望输出(188 个整数)。
    /// </summary>
    public static int RunCases(DnfLocation loc, Options opts)
    {
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var data = new GameData(new PvfScriptSource(pvf));
        MonsterBaseTables tables = data.MonsterBaseTables;
        int ok = 0, bad = 0, shown = 0;
        var slotHistogram = new SortedDictionary<int, int>();
        foreach (string line in File.ReadLines(opts.CasesFile!))
        {
            if (line.Length == 0)
            {
                continue;
            }
            string[] f = line.Split('\t');
            if (f.Length != 7)
            {
                throw new UserError("用例行应有 7 个制表符分隔的字段:" + line.Substring(0, Math.Min(60, line.Length)));
            }
            int id = int.Parse(f[0], CultureInfo.InvariantCulture);
            var kind = (MonsterKind)int.Parse(f[1], CultureInfo.InvariantCulture);
            int level = int.Parse(f[2], CultureInfo.InvariantCulture);
            int dungeon = int.Parse(f[3], CultureInfo.InvariantCulture);

            var scriptLines = new List<ScriptLine>();
            foreach (string entry in f[4].Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = entry.IndexOf('=');
                string tag = entry.Substring(0, eq);
                var values = new List<ScriptValue>();
                foreach (string token in SplitValues(entry.Substring(eq + 1)))
                {
                    if (token.StartsWith("\"", StringComparison.Ordinal))
                    {
                        values.Add(new ScriptValue(ScriptValueKind.String, 0, token.Substring(1, token.Length - 2)));
                    }
                    else
                    {
                        values.Add(new ScriptValue(ScriptValueKind.Int, int.Parse(token, CultureInfo.InvariantCulture), null));
                    }
                }
                scriptLines.Add(new ScriptLine(tag, values.ToArray()));
            }
            var def = new MonsterDef(id, "synthetic/case.mob", scriptLines.ToArray());

            MonsterBaseTable? own = null;
            if (f[5] != "-")
            {
                var ownValues = f[5].Split(',').Select(t => new ScriptValue(ScriptValueKind.Int, int.Parse(t, CultureInfo.InvariantCulture), null)).ToArray();
                own = MonsterBaseTable.Parse(new[] { new ScriptLine(null!, ownValues) });
            }

            int[] expected = f[6].Split(',').Select(t => int.Parse(t, CultureInfo.InvariantCulture)).ToArray();
            MonsterStats got = MonsterAbility.Compute(def, tables, own, level, dungeon, kind);
            var diffs = new List<int>();
            for (int i = 0; i < MonsterStats.Size; i++)
            {
                int g = got.Raw[i];
                int w = expected[i];
                if (i == MonsterStats.IxFlag)
                {
                    g &= 0xFF;
                    w &= 0xFF;
                }
                if (g != w)
                {
                    diffs.Add(i);
                }
            }
            if (diffs.Count == 0)
            {
                ok++;
                continue;
            }
            bad++;
            foreach (int i in diffs)
            {
                slotHistogram.TryGetValue(i, out int c);
                slotHistogram[i] = c + 1;
            }
            if (shown++ < 10)
            {
                Console.WriteLine("  不一致 编号 " + id + " 等级 " + level + " 种类 " + (int)kind + " 副本参数 " + dungeon
                    + " 槽号 " + string.Join(",", diffs.Take(6).Select(i => "0x" + i.ToString("X", CultureInfo.InvariantCulture))));
            }
        }
        Console.WriteLine("合成用例对拍(期望值来自服务端原版函数):一致 " + ok + ",不一致 " + bad);
        if (bad > 0)
        {
            Console.WriteLine("不一致的槽号分布:" + string.Join(",", slotHistogram.Select(kv => "0x" + kv.Key.ToString("X", CultureInfo.InvariantCulture) + "×" + kv.Value)));
        }
        return bad == 0 ? 0 : 1;
    }

    /// <summary>按逗号切开,带引号的字符串里的逗号不算(用例里的字符串不含逗号,这里仍按引号处理)。</summary>
    private static IEnumerable<string> SplitValues(string text)
    {
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            if (c == ',' && !quoted)
            {
                yield return sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }
}
