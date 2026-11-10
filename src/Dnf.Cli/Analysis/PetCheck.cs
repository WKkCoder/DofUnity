using System.Globalization;
using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf pet-check [--dump &lt;文件&gt;]`:用 Dnf.Game 的宠物加载器读全库的宠物定义(`creature.lst` 里的全部 `.cre`)、通用配置、经验表,
/// 以及装备脚本上的宠物字段(`[creature species]` 等)。
///   控制台只输出一致性计数(多少只、成长型多少、成长一节提交与否、饱食三项是否与引擎字面量相同……),不输出原版数值;
///   --dump 逐对象写出读到的字段(一行一个对象,排序后与 `python3 tools/py/pet_ref.py --dump` 的输出逐行比较,应完全一致),
///   用来对拍两份独立实现。--dump 文件放 out/,不入库。
/// </summary>
internal static class PetCheck
{
    public static int Run(DnfLocation loc, Options opts)
    {
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var data = new GameData(new PvfScriptSource(pvf));
        var catalog = new PetCatalog(data);
        var lines = new List<string>();

        int ok = 0, failed = 0, normal = 0, grow = 0;
        int blocks = 0, committed = 0, abandoned = 0, withDiscard = 0;
        int red = 0, blue = 0, green = 0;
        int evoTotal = 0, evoUnresolved = 0;
        HashSet<int> ids = new(catalog.Ids);
        foreach (int id in catalog.Ids)
        {
            PetDef? def = catalog.Get(id);
            PetScriptReport? report = catalog.ReportOf(id);
            if (def == null || report == null)
            {
                failed++;
                continue;
            }
            ok++;
            if (def.Kind == PetKind.Grow)
            {
                grow++;
            }
            else
            {
                normal++;
            }
            blocks += report.GrowBlocks;
            committed += report.GrowCommitted;
            abandoned += report.GrowAbandoned;
            withDiscard += report.DiscardedTokens > 0 ? 1 : 0;
            red += (def.ArtifactColors & PetArtifactColors.Red) != 0 ? 1 : 0;
            blue += (def.ArtifactColors & PetArtifactColors.Blue) != 0 ? 1 : 0;
            green += (def.ArtifactColors & PetArtifactColors.Green) != 0 ? 1 : 0;
            if (def.EvolutionCreatureId != 0)
            {
                evoTotal++;
                evoUnresolved += ids.Contains(def.EvolutionCreatureId) ? 0 : 1;
            }
            lines.Add(RenderDef(def, report));
        }

        PetCommon? common = catalog.Common;
        bool? fillMatch = null, feedEqual = null;
        if (common != null)
        {
            fillMatch = common.FillTermsMatchEngineLiterals;
            feedEqual = common.FillPointToEatFeed == (int)PetRules.FeedPoints;
            lines.Add("common\tresp=" + common.ResponseDelay + "\tspend=" + common.SecondsPerPointSpend + "\tcharge=" + common.SecondsPerPointCharge
                + "\tmax=" + common.MaxFillPoint + "\tfeed=" + common.FillPointToEatFeed + "\tdying=" + common.DyingDelay);
        }

        PetExpTable? table = catalog.ExpTable;
        bool? monotone = null;
        int? tableRead = null;
        if (table != null)
        {
            tableRead = table.ReadCount;
            bool mono = true;
            var values = new StringBuilder();
            for (int i = 0; i < PetRules.ExpTableEntries; i++)
            {
                values.Append(i == 0 ? "" : ",").Append(table.Entry(i).ToString(CultureInfo.InvariantCulture));
                if (i > 0 && i < table.ReadCount && table.Entry(i - 1) > table.Entry(i))
                {
                    mono = false;
                }
            }
            monotone = mono;
            lines.Add("exp\tread=" + table.ReadCount + "\t" + values);
        }

        int withFields = 0, speciesUnresolved = 0;
        Dictionary<int, string>? equipment = data.ReadList(ItemCatalog.EquipmentListPath);
        if (equipment != null)
        {
            PvfStringTable? strings = data.Strings;
            foreach (int code in equipment.Keys.OrderBy(c => c))
            {
                string path = PvfPath.ResolveRelativeTo(ItemCatalog.EquipmentListPath, equipment[code]);
                if (strings == null || !data.TryReadBytes(path, out byte[] bytes))
                {
                    continue;
                }
                PetItemData? item = PetItemData.Parse(bytes, strings);
                if (item == null || (item.Species == 0 && item.MinimumLevel == 0 && item.ExperienceRate == 0f))
                {
                    continue;
                }
                withFields++;
                if (item.Species != 0 && !ids.Contains(item.Species))
                {
                    speciesUnresolved++;
                }
                lines.Add("item\t" + code + "\tspecies=" + item.Species + "\tminlv=" + item.MinimumLevel + "\trate="
                    + BitConverter.SingleToInt32Bits(item.ExperienceRate).ToString("x8", CultureInfo.InvariantCulture));
            }
        }

        string yesNo(bool? v) => v == null ? "无" : v.Value ? "是" : "否";
        Console.WriteLine("读取 " + ok + " 个宠物脚本(读不出 " + failed + " 个);普通型 " + normal + "、成长型 " + grow);
        Console.WriteLine("  成长一节 " + blocks + " 个:提交 " + committed + "、未提交退出 " + abandoned + ";读取时越过过标记的脚本 " + withDiscard + " 个(含没建模的标签的取值,不是脏数据的证据)");
        Console.WriteLine("  神器颜色(出现在多少只宠物上):红 " + red + "、蓝 " + blue + "、绿 " + green);
        Console.WriteLine("  进化目标 " + evoTotal + " 处,指向列表外 " + evoUnresolved + " 处");
        Console.WriteLine("通用配置:饱食三项与引擎字面量相同 " + yesNo(fillMatch) + ";\"吃饲料所需\"与引擎喂食增量相同 " + yesNo(feedEqual));
        Console.WriteLine("经验表:整数读取成功 " + (tableRead?.ToString() ?? "无") + " 次(固定读 " + PetRules.ExpTableEntries + ");非降 " + yesNo(monotone));
        Console.WriteLine("物品:带宠物字段 " + withFields + " 件;species 指向列表外 " + speciesUnresolved + " 件");

        if (opts.DumpFile != null)
        {
            lines.Sort(string.CompareOrdinal);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.DumpFile))!);
            using var w = new StreamWriter(opts.DumpFile, false, new UTF8Encoding(false)) { NewLine = "\n" };
            foreach (string line in lines)
            {
                w.WriteLine(line);
            }
            Console.WriteLine("已写出 " + lines.Count + " 行 -> " + opts.DumpFile);
        }
        return 0;
    }

    /// <summary>一只宠物定义的逐字段输出;与 tools/py/pet_ref.py 的 render_def 逐字一致。</summary>
    private static string RenderDef(PetDef def, PetScriptReport report)
    {
        PetGrowInfo g = def.Grow;
        var sb = new StringBuilder();
        sb.Append("def\t").Append(def.Id).Append('\t').Append(def.Path.ToLowerInvariant());
        sb.Append("\tname=").Append(Convert.ToHexString(Encoding.Latin1.GetBytes(def.Name)).ToLowerInvariant());
        sb.Append("\tstart=").Append(def.StartLevel).Append("\tperm=").Append(def.PermissionLevel).Append("\tmax=").Append(def.MaxLevel)
            .Append("\tpmax=").Append(def.ParentMaxLevel).Append("\tart=").Append((int)def.ArtifactColors);
        sb.Append("\tevoq=").Append(def.EvolutionQuest).Append("\tevoc=").Append(def.EvolutionCreatureId).Append("\tevol=").Append(def.EvolutionLevel);
        sb.Append("\tgrow=").Append(g.UsesExpItem ? 1 : 0).Append(',').Append(g.SelectLevel).Append(',').Append(g.ATypeCreatureId).Append(',')
            .Append(g.BTypeCreatureId).Append(',').Append(g.ATypeItemIndex).Append(',').Append(g.BTypeItemIndex).Append(',').Append(g.ItemsPerLevel);
        sb.Append("\tkind=").Append(def.Kind == PetKind.Grow ? 'g' : 'n');
        sb.Append("\tdisc=").Append(report.DiscardedTokens);
        sb.Append("\tgb=").Append(report.GrowBlocks).Append('/').Append(report.GrowCommitted).Append('/').Append(report.GrowAbandoned);
        return sb.ToString();
    }
}
