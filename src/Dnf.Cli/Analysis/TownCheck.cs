using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// town-check:城镇图册的检查(证据 E-095)。读 town/town.lst 与每座 .twn,再读每块区域地图的可走区域与出口,汇总:
/// 城镇与区域的个数、区域类型分布、地图读不读得到、出口的去处(区域 / 世界地图 / 无去处)、去处是否存在、对端是否有回来的出口(对称性)、
/// 出口矩形是否与可走区域相交(走不走得到)、每块区域能否沿出口走到地下城入口。--town N 再逐区域打印细节(本机查看,数值不入库)。
/// </summary>
internal static class TownCheck
{
    public static int Run(DnfLocation loc, Options opts)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        TownAtlas atlas = TownAtlas.Open(assets);
        List<int> ids = atlas.TownIds.OrderBy(i => i).ToList();

        int towns = 0, unreadableTowns = 0, areas = 0, unreadableMaps = 0;
        var kinds = new SortedDictionary<string, int>();
        int exits = 0, toArea = 0, toWorld = 0, noTarget = 0, targetMissing = 0, symmetric = 0, asymmetric = 0, reachable = 0, unreachable = 0;
        int areasWithGateRoute = 0, areasWithoutGateRoute = 0, townsWithGate = 0;
        int withCutscene = 0, withTitle = 0, titleMissing = 0, cutsceneMissing = 0;

        foreach (int id in ids)
        {
            TownDef? def = atlas.GetTown(id);
            if (def == null)
            {
                unreadableTowns++;
                continue;
            }
            towns++;
            if (def.CutsceneImage != null)
            {
                withCutscene++;
                if (!assets.TryGetFrame(def.CutsceneImage, def.CutsceneFrame, out _))
                {
                    cutsceneMissing++;
                }
            }
            if (def.EnteringTitle != null)
            {
                withTitle++;
                if (assets.LoadAni(def.EnteringTitle) == null)
                {
                    titleMissing++;
                }
            }
            if (atlas.FindDungeonGate(id) != null)
            {
                townsWithGate++;
            }
            foreach (TownArea a in def.Areas)
            {
                areas++;
                kinds[a.Kind.ToString()] = kinds.GetValueOrDefault(a.Kind.ToString()) + 1;
                TownAreaInfo? info = atlas.GetArea(id, a.Id);
                if (info == null)
                {
                    unreadableMaps++;
                    continue;
                }
                foreach (TownExit e in info.Exits)
                {
                    exits++;
                    if (e.IsDungeonGate)
                    {
                        toWorld++;
                    }
                    else if (e.HasTarget)
                    {
                        toArea++;
                        TownAreaInfo? target = atlas.GetArea(e.Town, e.Area);
                        if (target == null)
                        {
                            targetMissing++;
                        }
                        else if (target.TryFindExitTo(id, a.Id, out _))
                        {
                            symmetric++;
                        }
                        else
                        {
                            asymmetric++;
                        }
                    }
                    else
                    {
                        noTarget++;
                    }
                    if (e.HasTarget || e.IsDungeonGate)
                    {
                        bool hit = info.Walkable.Any(r => Overlaps(r, e.Rect));
                        if (hit)
                        {
                            reachable++;
                        }
                        else
                        {
                            unreachable++;
                        }
                    }
                }
                if (atlas.TryRouteToDungeonGate(new TownPlace(id, a.Id), out _))
                {
                    areasWithGateRoute++;
                }
                else
                {
                    areasWithoutGateRoute++;
                }
            }
        }

        Console.WriteLine("城镇图册:" + ids.Count + " 座城镇(town.lst),读得到 " + towns + " 座(读不到 " + unreadableTowns + "),区域 " + areas + " 块,地图读不到 " + unreadableMaps + " 块");
        Console.WriteLine("  区域类型:" + string.Join("、", kinds.Select(kv => kv.Key + " " + kv.Value)));
        Console.WriteLine("  加载画面:写了过场图的城镇 " + withCutscene + " 座(图读不到 " + cutsceneMissing + "),写了标题动画的 " + withTitle + " 座(读不到 " + titleMissing + ")");
        Console.WriteLine("  出口:" + exits + " 个,去别的区域 " + toArea + "、去世界地图 " + toWorld + "、无去处 " + noTarget + ";去处区域读不到 " + targetMissing + ",对端有回来的出口 " + symmetric + "、没有 " + asymmetric);
        Console.WriteLine("  能触发的出口(有去处或通世界地图)里,矩形与可走区域相交的 " + reachable + " 个,不相交(走不到)的 " + unreachable + " 个");
        Console.WriteLine("  地下城入口:有入口区域的城镇 " + townsWithGate + " 座;能沿出口走到入口的区域 " + areasWithGateRoute + " 块,走不到的 " + areasWithoutGateRoute + " 块");

        if (opts.TownId >= 0)
        {
            Console.WriteLine();
            PrintTown(atlas, opts.TownId);
        }
        bool clean = unreadableTowns == 0 && unreadableMaps == 0 && targetMissing == 0 && cutsceneMissing == 0 && titleMissing == 0;
        return clean ? 0 : 2;
    }

    private static void PrintTown(TownAtlas atlas, int id)
    {
        TownDef? def = atlas.GetTown(id);
        if (def == null)
        {
            Console.WriteLine("城镇 " + id + " 读不到");
            return;
        }
        Console.WriteLine("城镇 " + id + " " + def.Path + "  过场图 " + def.CutsceneImage + "#" + def.CutsceneFrame + "  标题 " + def.EnteringTitle);
        foreach (TownArea a in def.Areas.OrderBy(x => x.Id))
        {
            TownAreaInfo? info = atlas.GetArea(id, a.Id);
            Console.WriteLine("  区域 " + a.Id + " " + a.Kind + (a.Params.Count > 0 ? "(" + string.Join(",", a.Params) + ")" : string.Empty) + "  " + (info?.MapPath ?? "(地图读不到)"));
            if (info == null)
            {
                continue;
            }
            WorldRect? b = info.Bounds;
            Console.WriteLine("    可走区域 " + info.Walkable.Count + " 块,外包矩形 " + (b.HasValue ? R(b.Value) : "无"));
            var tracker = new TownExitTracker(info.Exits, info.Bounds);
            foreach (TownExit e in info.Exits)
            {
                tracker.OutwardOf(e, out int dx, out int dy);
                string kind = e.IsDungeonGate ? "世界地图" : e.HasTarget ? e.Town + "/" + e.Area : "无去处";
                bool hit = info.Walkable.Any(r => Overlaps(r, e.Rect));
                Console.WriteLine("    出口 " + R(e.Rect) + " → " + kind + "  往外推 (" + dx + "," + dy + ")" + (hit ? string.Empty : "  [与可走区域不相交]"));
            }
            foreach (TownExit e in info.Exits.Where(x => x.HasTarget))
            {
                TownAreaInfo? target = atlas.GetArea(e.Town, e.Area);
                if (target == null)
                {
                    continue;
                }
                TownArrival arr = atlas.Arrive(new TownPlace(e.Town, e.Area, TownEntry.Exit, id, a.Id));
                Console.WriteLine("    走到 " + e.Town + "/" + e.Area + " 落脚 (" + arr.X.ToString("0.#") + "," + arr.Y.ToString("0.#") + ") 朝" + (arr.FacingLeft ? "左" : "右"));
            }
        }
    }

    private static bool Overlaps(WorldRect a, WorldRect b)
    {
        return a.X <= b.X + b.Width && b.X <= a.X + a.Width && a.Y <= b.Y + b.Height && b.Y <= a.Y + a.Height;
    }

    private static string R(WorldRect r) => "(" + r.X + "," + r.Y + "," + r.Width + "," + r.Height + ")";
}
