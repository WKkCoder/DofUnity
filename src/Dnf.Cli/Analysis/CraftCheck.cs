using System.Globalization;
using System.Text;
using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf craft-check [--dump &lt;文件&gt;]`:用 Dnf.Game 的配方加载器读全库的配方(`stackable.lst` 里 `[stackable type]` 为 `[recipe]` 的物品),
/// 只在控制台输出一致性计数(多少个配方、各种类多少、带成功率的多少、产物编号查不到的多少……),不输出原版数值;
/// --dump 逐配方写出读到的字段(一行一个配方,排序后与 `python3 tools/py/craft_ref.py --dump` 的输出逐行比较,应完全一致),
/// 用来对拍两份独立实现(`craft_ref.py compare` 只报一致与不一致的个数)。--dump 文件放 out/,不入库。
/// </summary>
internal static class CraftCheck
{
    public static int Run(DnfLocation loc, Options opts)
    {
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var data = new GameData(new PvfScriptSource(pvf));
        var items = new ItemCatalog(data);
        var catalog = new RecipeCatalog(data, items);
        var lines = new List<string>();

        int ok = 0, malformed = 0;
        int normal = 0, premium = 0, expert = 0;
        int withRate = 0, withFail = 0, withBroadcast = 0, withMaintain = 0, withBead = 0, withMaking = 0, withGold = 0, withSkills = 0;
        int emptyOutputs = 0, unknownOutputs = 0, unknownInputs = 0, equipmentOutputs = 0, avatarInputs = 0, multiEquipment = 0;
        int duplicateInputs = 0, zeroCountInputs = 0, withoutCategory = 0;

        IReadOnlyList<int> codes = catalog.Codes();
        foreach (int code in codes)
        {
            RecipeDef? def = catalog.Get(code);
            if (def == null)
            {
                continue;
            }
            ok++;
            malformed += def.Malformed ? 1 : 0;
            switch (def.Kind)
            {
                case RecipeKind.Premium:
                    premium++;
                    break;
                case RecipeKind.ExpertJob:
                    expert++;
                    break;
                default:
                    normal++;
                    break;
            }
            withRate += def.HasSuccessRoll ? 1 : 0;
            withFail += def.FailOutputs.Count > 0 ? 1 : 0;
            withBroadcast += def.BroadcastType != 0 ? 1 : 0;
            withMaintain += def.MaintainAbility != 0 ? 1 : 0;
            withBead += def.ExtraInputs.Count > 0 ? 1 : 0;
            withMaking += def.MakingLevel != 0 ? 1 : 0;
            withSkills += def.Requirements.Count > 0 ? 1 : 0;
            withoutCategory += def.Categories.Count == 0 ? 1 : 0;
            emptyOutputs += def.Outputs.Count == 0 ? 1 : 0;

            bool gold = false, unknownOut = false, unknownIn = false, equipOut = false, avatarIn = false, multiEquip = false, zero = false;
            var seen = new HashSet<int>();
            bool duplicate = false;
            foreach (RecipeItem input in def.AllInputs)
            {
                gold |= input.IsGold;
                zero |= input.Count == 0;
                duplicate |= !seen.Add(input.Code);
                if (!input.IsGold)
                {
                    ItemInfo? info = items.Get(input.Code);
                    unknownIn |= info == null;
                    avatarIn |= info != null && info.IsAvatar;
                }
            }
            foreach (RecipeItem output in def.Outputs)
            {
                ItemInfo? info = output.IsGold ? null : items.Get(output.Code);
                unknownOut |= info == null;
                equipOut |= info != null && info.IsEquipment;
                multiEquip |= info != null && info.IsEquipment && output.Count > 1;
            }
            withGold += gold ? 1 : 0;
            unknownOutputs += unknownOut ? 1 : 0;
            unknownInputs += unknownIn ? 1 : 0;
            equipmentOutputs += equipOut ? 1 : 0;
            avatarInputs += avatarIn ? 1 : 0;
            multiEquipment += multiEquip ? 1 : 0;
            duplicateInputs += duplicate ? 1 : 0;
            zeroCountInputs += zero ? 1 : 0;
            lines.Add(Render(def));
        }

        Console.WriteLine("读取 " + ok + " 个配方(配方物品 " + codes.Count + " 个);数据残缺 " + malformed + " 个");
        Console.WriteLine("  种类:普通 " + normal + "、会员专属 " + premium + "、副职业 " + expert);
        Console.WriteLine("  带成功率 " + withRate + "、带失败产物 " + withFail + "、带播报 " + withBroadcast + "、带保留词条 " + withMaintain
            + "、带额外材料 " + withBead + "、带制作熟练度 " + withMaking + "、带技能要求 " + withSkills + "、没有制作类别 " + withoutCategory);
        Console.WriteLine("  材料含金币 " + withGold + "、含时装 " + avatarInputs + "、含查不到的编号 " + unknownInputs + "、同编号重复 " + duplicateInputs
            + "、含零数量 " + zeroCountInputs);
        Console.WriteLine("  产物为空 " + emptyOutputs + "、含查不到的编号 " + unknownOutputs + "、含装备 " + equipmentOutputs
            + "、装备数量多于一件 " + multiEquipment);

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

    /// <summary>一个配方的逐字段输出;与 tools/py/craft_ref.py 的 render 逐字一致(空表写成 "-")。</summary>
    private static string Render(RecipeDef r)
    {
        var sb = new StringBuilder();
        sb.Append("recipe\t").Append(r.Code.ToString(CultureInfo.InvariantCulture)).Append('\t').Append((r.Path ?? "").ToLowerInvariant());
        sb.Append("\tkind=").Append(((int)r.Kind).ToString(CultureInfo.InvariantCulture));
        sb.Append("\tin=").Append(Pairs(r.Inputs));
        sb.Append("\tout=").Append(Pairs(r.Outputs));
        sb.Append("\treq=");
        if (r.Requirements.Count == 0)
        {
            sb.Append('-');
        }
        for (int i = 0; i < r.Requirements.Count; i++)
        {
            sb.Append(i == 0 ? "" : ",").Append(r.Requirements[i].Skill.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(r.Requirements[i].Level.ToString(CultureInfo.InvariantCulture));
        }
        sb.Append("\tbead=").Append(Pairs(r.ExtraInputs));
        sb.Append("\trate=").Append(r.SuccessRate.ToString(CultureInfo.InvariantCulture));
        sb.Append("\tfail=").Append(Pairs(r.FailOutputs));
        sb.Append("\tbcast=").Append(r.BroadcastType.ToString(CultureInfo.InvariantCulture));
        sb.Append("\tmaint=").Append(r.MaintainAbility.ToString(CultureInfo.InvariantCulture));
        sb.Append("\tmk=").Append(r.MakingType.ToString(CultureInfo.InvariantCulture)).Append(':').Append(r.MakingLevel.ToString(CultureInfo.InvariantCulture));
        sb.Append("\tcat=");
        if (r.Categories.Count == 0)
        {
            sb.Append('-');
        }
        for (int i = 0; i < r.Categories.Count; i++)
        {
            sb.Append(i == 0 ? "" : ",").Append(Convert.ToHexString(Encoding.Latin1.GetBytes(r.Categories[i])).ToLowerInvariant());
        }
        sb.Append("\ttidx=").Append(r.TypeIndex.ToString(CultureInfo.InvariantCulture));
        sb.Append("\tbad=").Append(r.Malformed ? 1 : 0);
        return sb.ToString();
    }

    private static string Pairs(IReadOnlyList<RecipeItem> items)
    {
        if (items.Count == 0)
        {
            return "-";
        }
        var sb = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            sb.Append(i == 0 ? "" : ",").Append(items[i].Code.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(items[i].Count.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
