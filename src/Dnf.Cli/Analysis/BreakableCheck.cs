using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// breakable-check:可破坏物件与物件掉物品的检查(M34,证据 E-126)。三部分:
/// 1. 物件掉落表 Etc/ItemDropInfo_Object.etc 读得到(格式同怪物掉落表),并能建出物品池;
/// 2. 全部地图的 [special passive object] 文法:每张地图按 <see cref="SpecialPlacement.Parse"/> 读出的项,与 <see cref="RoomMap.Read"/> 的结果一致,
///    没读完的值只停在认得的"另有写法的关键字"上(不能停在别的东西上);按每座副本(<see cref="DungeonRoomPool"/>)再校验每个 [item] 动作的掉落项下标落在
///    这座副本 .dgn 的 [special passive object item] 范围内(服务端装载时也这样校验);
/// 3. 实机:在 --dungeon(没有可破坏物件就依次换别的副本)的布局里挑出有可破坏物件的房间(最多 --rooms 个),
///    用与播放器同一条路径造出房间的战斗会话(带这座副本的物件掉落数据),把主角摆到每个物件左边一下一下地挥,看打中、打碎、触发与掉出的物品。
/// 数值全是用户自己的数据,只打印在本机的终端上(不要抄进入库的文件);通过与否只看结构与流程。
/// </summary>
internal static class BreakableCheck
{
    /// <summary>服务端对它另有一套写法的特殊项(自己的结束标签),这一版不读;数据里只有它会让 [special passive object] 的值读不完。</summary>
    private const string KnownSeparateSyntax = "[hellparty]";

    /// <summary>实机部分为了见到一次掉物品最多造多少个房间。</summary>
    private const int MaxRooms = 40;

    private const int MaxSwingsPerObject = 12;
    private const int SwingMs = 700;

    /// <summary>--pick 的挑选条件:格数不超过它(打得快)、除首领格外至少有这么多个房间摆着可破坏物件、这些房间里物件合计至少这么多个。</summary>
    private const int PickMaxCells = 8;
    private const int PickMinRooms = 2;
    private const int PickMinObjects = 8;
    private static readonly int[] HeroOffsets = { -45, -25, -65, 15 };

    private sealed class MapInfo
    {
        public bool Missing;
        public List<SpecialPlacement> Placements = new();
        public int Leftover;
        public int LeftoverNotKeyword;
        public string? StopKeyword;
        public string StopDescription = "";
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        if (opts.Pick)
        {
            return PickDungeon(loc);
        }
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        int bad = 0;

        void Report(bool ok, string text)
        {
            Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + text);
            if (!ok)
            {
                bad++;
            }
        }

        // 1. 物件掉落表
        MonsterDrops drops = MonsterDrops.Load(assets);
        Console.WriteLine("物件掉落表 " + MonsterDrops.ObjectPath + ":" + (drops.ObjectTable != null ? "已读到" : "没有读到"));
        Report(drops.ObjectTable != null, "物件掉落表读得到(格式同怪物掉落表)");
        ItemPool? objectPool = drops.ObjectPool;
        Report(objectPool != null && objectPool.Items > 0, "按它建出了物品池(池里 " + (objectPool?.Items ?? 0) + " 件)");

        // 2. 全部地图的特殊物件
        PassiveObjectCatalog catalog = PassiveObjectCatalog.Read(assets);
        var defs = new Dictionary<int, Dnf.Render.PassiveObjectDef?>();
        Dnf.Render.PassiveObjectDef? DefOf(int id)
        {
            if (!defs.TryGetValue(id, out Dnf.Render.PassiveObjectDef? def))
            {
                def = Dnf.Render.PassiveObjectDef.Read(assets, catalog, id);
                defs[id] = def;
            }
            return def;
        }

        var infos = new Dictionary<string, MapInfo>(StringComparer.OrdinalIgnoreCase);
        MapInfo InfoOf(string mapPath)
        {
            if (!infos.TryGetValue(mapPath, out MapInfo? cached))
            {
                cached = ReadInfo(assets, mapPath);
                infos[mapPath] = cached;
            }
            return cached;
        }

        var listed = WorldMapDef.ParseList(assets.ReadScript(DungeonRoomPool.DungeonListPath), "dungeon/").ToList();
        int dungeons = 0, pairs = 0, itemActions = 0, badIndex = 0, skippedLevel = 0, missingMaps = 0;
        int rolledActions = 0, rolledWithItems = 0, rolledItems = 0, specificActions = 0, randomActions = 0;
        foreach (KeyValuePair<int, string> kv in listed)
        {
            ScriptLine[]? script = assets.ReadScript(kv.Value);
            if (script == null)
            {
                continue;
            }
            dungeons++;
            DungeonObjectItems items = DungeonObjectItems.Parse(script);
            int standard = GoldDrop.StandardLevel(script);
            var setup = new ObjectDropSetup(items, standard, 0, drops);
            var rolls = new DropRandom(opts.Seed);
            foreach (RoomCandidate room in DungeonRoomPool.ReadFor(assets, kv.Value).Rooms)
            {
                MapInfo info = InfoOf(room.MapPath);
                if (info.Missing)
                {
                    missingMaps++;
                    continue;
                }
                pairs++;
                foreach (SpecialPlacement p in info.Placements)
                {
                    foreach (SpecialAction a in p.Actions)
                    {
                        if (a.Kind != SpecialActionKind.Item)
                        {
                            continue;
                        }
                        itemActions++;
                        if (a.P1 < 0 || a.P1 >= items.Entries.Count)
                        {
                            badIndex++;
                        }
                        else if (items.Entries[a.P1].Level == -1 && (standard < 1 || standard > 200))
                        {
                            skippedLevel++;
                        }
                        else
                        {
                            // 抽样:每个合法的 [item] 动作按这座副本的数据掷一次(种子固定),看出得了东西的有多少
                            List<ItemInfo> got = setup.Roll(a, rolls);
                            rolledActions++;
                            rolledItems += got.Count;
                            rolledWithItems += got.Count > 0 ? 1 : 0;
                            specificActions += (sbyte)a.P2 > 0 ? 1 : 0;
                            randomActions += (sbyte)a.P3 > 0 ? 1 : 0;
                        }
                    }
                }
            }
        }

        int mapsWithSpecial = 0, placements = 0, mismatch = 0, leftoverMaps = 0, leftoverNotKeyword = 0, readFailures = 0, unknownKeyword = 0;
        var kinds = new int[4];
        var stopKeywords = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var objectIds = new HashSet<int>();
        var bySubject = new int[4, 3];           // [动作种类, 挂在 0 = 可破坏物件 / 1 = 别的读得出动画的物件 / 2 = 读不出基础动画的物件]
        var firstStops = new List<string>();
        foreach (KeyValuePair<string, MapInfo> kv in infos)
        {
            MapInfo info = kv.Value;
            if (info.Placements.Count == 0 && info.Leftover == 0)
            {
                continue;
            }
            mapsWithSpecial++;
            placements += info.Placements.Count;
            foreach (SpecialPlacement p in info.Placements)
            {
                objectIds.Add(p.Id);
                Dnf.Render.PassiveObjectDef? subject = DefOf(p.Id);
                foreach (SpecialAction a in p.Actions)
                {
                    kinds[(int)a.Kind]++;
                    bySubject[(int)a.Kind, subject == null ? 2 : subject.Breakable != null ? 0 : 1]++;
                }
            }
            if (info.Leftover > 0)
            {
                leftoverMaps++;
                leftoverNotKeyword += info.LeftoverNotKeyword;
                if (info.LeftoverNotKeyword > 0 && firstStops.Count < 5)
                {
                    firstStops.Add(kv.Key + " " + info.StopDescription);
                }
                if (info.StopKeyword != null)
                {
                    stopKeywords.Add(info.StopKeyword);
                    if (!string.Equals(info.StopKeyword, KnownSeparateSyntax, StringComparison.OrdinalIgnoreCase))
                    {
                        unknownKeyword++;
                    }
                }
            }
            try
            {
                if (RoomMap.Read(assets, kv.Key).SpecialObjects.Count != info.Placements.Count)
                {
                    mismatch++;
                }
            }
            catch (DnfFormatException)
            {
                readFailures++;
            }
        }
        int resolved = 0, breakable = 0;
        foreach (int id in objectIds)
        {
            Dnf.Render.PassiveObjectDef? def = DefOf(id);
            if (def != null)
            {
                resolved++;
                if (def.Breakable != null)
                {
                    breakable++;
                }
            }
        }
        Console.WriteLine("副本定义 " + dungeons + " 个,(副本,地图)对 " + pairs + " 个;含特殊物件的地图 " + mapsWithSpecial + " 张,共 " + placements + " 项;动作 [monster] "
            + kinds[(int)SpecialActionKind.Monster] + "、[trap] " + kinds[(int)SpecialActionKind.Trap] + "、[item] " + kinds[(int)SpecialActionKind.Item] + "、[quest] "
            + kinds[(int)SpecialActionKind.Quest]);
        Console.WriteLine("物件编号 " + objectIds.Count + " 种:读得出基础动画的 " + resolved + " 种,其中可破坏物件 " + breakable + " 种;没读完的值停在这些关键字上:"
            + (stopKeywords.Count == 0 ? "(无)" : string.Join(" ", stopKeywords)));
        foreach (SpecialActionKind kind in new[] { SpecialActionKind.Monster, SpecialActionKind.Trap, SpecialActionKind.Item, SpecialActionKind.Quest })
        {
            Console.WriteLine("  动作 " + kind + " 挂在:可破坏物件 " + bySubject[(int)kind, 0] + " 个、别的物件 " + bySubject[(int)kind, 1] + " 个、读不出基础动画的物件 " + bySubject[(int)kind, 2] + " 个");
        }
        foreach (string text in firstStops)
        {
            Console.WriteLine("  停在别处:" + text);
        }
        Report(missingMaps == 0 && readFailures == 0, "池里的地图都读得到(读不到 " + missingMaps + " 张,RoomMap 读失败 " + readFailures + " 张)");
        Report(mismatch == 0, "SpecialPlacement.Parse 与 RoomMap.Read 读出的项数一致(不一致 " + mismatch + " 张)");
        Report(leftoverNotKeyword == 0 && unknownKeyword == 0, "没读完的值都是为 " + KnownSeparateSyntax + " 另写的那一套(停在别的关键字 " + unknownKeyword + " 张,剩下的里面没有关键字 "
            + leftoverNotKeyword + " 张;没读完的地图共 " + leftoverMaps + " 张)");
        Console.WriteLine("  [item] 动作按各自副本的数据掷一次(固定种子):掷了 " + rolledActions + " 个,出得了物品的 " + rolledWithItems + " 个,共 " + rolledItems + " 件;其中要出指定物品的 "
            + specificActions + " 个、要出随机物品的 " + randomActions + " 个");
        Report(rolledWithItems > 0, "按真实数据掷 [item] 动作能掷出物品");
        Report(badIndex == 0, "每个 [item] 动作的掉落项下标都在所属副本的 [special passive object item] 范围内(越界 " + badIndex + " 个,动作共 " + itemActions
            + " 个;项等级 -1 而副本标准等级不在 1 到 200 的 " + skippedLevel + " 个)");

        // 3. 实机
        Console.WriteLine();
        Console.WriteLine("实机(最多 " + opts.Rooms + " 个房间,先看 " + opts.Dungeon + ",种子 " + opts.Seed + "):");
        AvatarOutfit outfit = DemoRoom.DefaultOutfit(assets, opts.HeroWeapon);
        var candidates = new List<string> { opts.Dungeon };
        candidates.AddRange(listed.Select(kv => kv.Value).Where(p => !string.Equals(p, opts.Dungeon, StringComparison.OrdinalIgnoreCase)));
        int rooms = 0, tried = 0, hit = 0, destroyed = 0, triggered = 0, unreachable = 0, dropped = 0, objectHits = 0, actionsRun = 0;
        // 先凑够 --rooms 个房间;一件物品也没掉出来就接着找(最多 MaxRooms 个),确认从打到掉的整条路径真的走通过
        bool More() => rooms < opts.Rooms || (dropped == 0 && rooms < MaxRooms);
        foreach (string dungeon in candidates)
        {
            if (!More())
            {
                break;
            }
            DungeonMaze? maze;
            DungeonLayout layout;
            try
            {
                maze = DungeonMaze.Read(assets, dungeon, null);
                if (maze == null)
                {
                    continue;
                }
                layout = DungeonLayout.Create(maze, DungeonRoomPool.ReadFor(assets, dungeon), null);
            }
            catch (DnfFormatException)
            {
                continue;
            }
            ScriptLine[]? script = assets.ReadScript(dungeon);
            var setup = new ObjectDropSetup(DungeonObjectItems.Parse(script), GoldDrop.StandardLevel(script), 0, drops);
            DungeonRoomFactory factory = DungeonSession.CreateAssetFactory(assets, layout, outfit, opts.Seed, opts.HeroId, heroVitals: false, objectDrops: setup);
            for (int y = 0; y < maze.Rows && More(); y++)
            {
                for (int x = 0; x < maze.Cols && More(); x++)
                {
                    if (maze.ExitsAt(x, y) == 0)
                    {
                        continue;
                    }
                    RoomCandidate cand = layout.RoomAt(x, y);
                    MapInfo info = InfoOf(cand.MapPath);
                    // 优先挑带 [item] 动作的可破坏物件(这一版唯一会真正执行的动作)
                    if (!info.Placements.Any(p => p.Has(SpecialActionKind.Item) && DefOf(p.Id)?.Breakable != null))
                    {
                        continue;
                    }
                    RoomCombatSession session = factory(x, y);
                    if (session.Breakables.Count == 0)
                    {
                        continue;
                    }
                    rooms++;
                    Drive(session);
                    int roomTriggered = session.Breakables.Count(b => b.Triggered && !b.Destroyed);
                    int roomUnreachable = session.Breakables.Count(b => b.HitsTaken == 0);
                    Console.WriteLine("  " + dungeon + " 格 (" + x + "," + y + "):物件 " + session.Breakables.Count + " 个,打中 " + session.ObjectsHit + " 次,打碎 "
                        + session.ObjectsDestroyed + " 个,触发后留下 " + roomTriggered + " 个,打不到 " + roomUnreachable + " 个;执行 [item] 动作 " + session.ObjectItemActions
                        + " 个,掉出物品 " + session.ObjectItemsDropped + " 件(落在地上 " + session.Drops.Count(d => d.IsItem) + " 件)");
                    tried += session.Breakables.Count;
                    hit += session.Breakables.Count - roomUnreachable;
                    destroyed += session.ObjectsDestroyed;
                    triggered += roomTriggered;
                    unreachable += roomUnreachable;
                    dropped += session.ObjectItemsDropped;
                    actionsRun += session.ObjectItemActions;
                    objectHits += session.ObjectsHit;
                }
            }
        }
        Console.WriteLine("合计:房间 " + rooms + " 个,物件 " + tried + " 个,被打中的 " + hit + " 个(共 " + objectHits + " 次),打碎 " + destroyed + " 个,触发后留下 " + triggered
            + " 个,打不到 " + unreachable + " 个;执行 [item] 动作 " + actionsRun + " 个,掉出物品 " + dropped + " 件");
        Report(rooms > 0, "找到了有可破坏物件的房间并造出了战斗会话");
        Report(hit > 0, "至少打中过一个物件(受击框与攻击框相交)");
        Report(destroyed + triggered > 0, "至少有一个物件打够了(打碎或触发)");
        Report(actionsRun > 0, "打够的物件执行了它们的 [item] 动作");
        Report(dropped > 0, "至少掉出了一件物品(落在地上的物品 = 动作掷出的物品)");

        Console.WriteLine(bad == 0 ? "可破坏物件检查通过。" : "可破坏物件检查失败 " + bad + " 项。");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// --pick:从副本列表里按列表顺序挑第一座适合"自动机清房后打翻物件"的无人值守检查的副本,只把它的路径打印在标准输出(给 tools/unity-build-town.sh 的链路检查用,
    /// 脚本里不写死具体是哪一座);条件见 <see cref="PickMaxCells"/> 一带——格数少、首领格以外有几个房间摆着可破坏物件。找不到时标准输出为空、退出码 1。
    /// </summary>
    private static int PickDungeon(DnfLocation loc)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        PassiveObjectCatalog catalog = PassiveObjectCatalog.Read(assets);
        var breakableIds = new Dictionary<int, bool>();
        bool IsBreakable(int id)
        {
            if (!breakableIds.TryGetValue(id, out bool value))
            {
                value = Dnf.Render.PassiveObjectDef.Read(assets, catalog, id)?.Breakable != null;
                breakableIds[id] = value;
            }
            return value;
        }

        var infos = new Dictionary<string, MapInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<int, string> kv in WorldMapDef.ParseList(assets.ReadScript(DungeonRoomPool.DungeonListPath), "dungeon/"))
        {
            DungeonMaze? maze;
            DungeonLayout layout;
            try
            {
                maze = DungeonMaze.Read(assets, kv.Value, null);
                if (maze == null)
                {
                    continue;
                }
                layout = DungeonLayout.Create(maze, DungeonRoomPool.ReadFor(assets, kv.Value), null);
            }
            catch (DnfFormatException)
            {
                continue;
            }
            int cells = 0, rooms = 0, objects = 0;
            for (int y = 0; y < maze.Rows; y++)
            {
                for (int x = 0; x < maze.Cols; x++)
                {
                    if (maze.ExitsAt(x, y) == 0)
                    {
                        continue;
                    }
                    cells++;
                    if (maze.IsBossCell(x, y))
                    {
                        continue;
                    }
                    string mapPath = layout.RoomAt(x, y).MapPath;
                    if (!infos.TryGetValue(mapPath, out MapInfo? info))
                    {
                        info = ReadInfo(assets, mapPath);
                        infos[mapPath] = info;
                    }
                    int n = info.Placements.Count(p => IsBreakable(p.Id));
                    objects += n;
                    rooms += n > 0 ? 1 : 0;
                }
            }
            if (cells > 0 && cells <= PickMaxCells && rooms >= PickMinRooms && objects >= PickMinObjects)
            {
                Console.WriteLine(kv.Value);
                return 0;
            }
        }
        Console.Error.WriteLine("没有找到适合的副本(格数不超过 " + PickMaxCells + "、首领格以外至少 " + PickMinRooms + " 个房间共 " + PickMinObjects + " 个可破坏物件)");
        return 1;
    }

    /// <summary>读一张地图里所有的 [special passive object] 项,并记下没读完的值停在哪里(见 <see cref="MapInfo"/>)。</summary>
    private static MapInfo ReadInfo(DnfAssets assets, string mapPath)
    {
        var info = new MapInfo();
        ScriptLine[]? doc = assets.ReadScript(mapPath);
        if (doc == null)
        {
            info.Missing = true;
        }
        else
        {
            foreach (ScriptLine line in doc)
            {
                if (!line.TagIs("[special passive object]"))
                {
                    continue;
                }
                List<SpecialPlacement> parsed = SpecialPlacement.Parse(line.Values);
                int consumed = parsed.Sum(p => 5 + 6 * p.Actions.Count);
                if (consumed < line.Values.Length)
                {
                    // 没读完:停在一个认不得的动作关键字上(服务端对 [hellparty] 另有一套写法,那一项连同它之后的值整个不读);在剩下的值里找第一个关键字
                    info.Leftover += line.Values.Length - consumed;
                    string? keyword = null;
                    for (int k = consumed; k < line.Values.Length && keyword == null; k++)
                    {
                        ScriptValue v = line.Values[k];
                        if (v.Kind == ScriptValueKind.String && v.Text != null && v.Text.StartsWith('['))
                        {
                            keyword = v.Text;
                        }
                    }
                    if (keyword != null)
                    {
                        info.StopKeyword = keyword;
                    }
                    else
                    {
                        info.LeftoverNotKeyword++;
                        info.StopDescription = "已读 " + parsed.Count + " 项、" + consumed + " 个值,共 " + line.Values.Length + " 个值,剩下的里面没有关键字";
                    }
                }
                info.Placements.AddRange(parsed);
            }
        }
        return info;
    }

    /// <summary>把主角依次摆到每个物件左边挥刀,直到打碎、或触发后又多挨了一下、或挥满 <see cref="MaxSwingsPerObject"/> 次(摆的位置每次换一个)。</summary>
    private static void Drive(RoomCombatSession session)
    {
        foreach (BreakableObject b in session.Breakables.ToList())
        {
            for (int attempt = 0; attempt < MaxSwingsPerObject && !b.Destroyed; attempt++)
            {
                if (b.Triggered && b.HitsTaken > b.Data.HitsNeeded)
                {
                    break;
                }
                int offset = HeroOffsets[attempt % HeroOffsets.Length];
                session.Hero.Place(b.Object.X + offset, b.Object.Y, offset > 0, HeroMotion.Rest, 0);
                session.Update(10, 0, 0, true);
                for (int t = 10; t < SwingMs; t += 10)
                {
                    session.Update(10, 0, 0, false);
                }
            }
        }
    }
}
