using System.Diagnostics;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// render-town --scene:用 <see cref="TownScene.BuildScene"/> 合成整个场景(地图图层 + NPC),与原版截图逐像素对照。
/// 截图里还有 HUD、聊天文字、主角与名牌,这些我们没有画,所以总体吻合率不会是 100%;
/// 网格表按 100×100 的格子给出吻合率,用来看哪些区域是场景本身、哪些是界面。
/// </summary>
internal static class SceneCompare
{
    public static int Run(DnfLocation loc, Options opts)
    {
        if (opts.Screenshot == null && opts.PngFile == null)
        {
            throw new UserError("--scene 需要 --shot <截图.png>(与原版截图对照),或者 --png <输出.png>(只输出我们的 800×600 合成结果)");
        }
        var sw = Stopwatch.StartNew();
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        Raster? shotOrNull = opts.Screenshot == null ? null : PngReader.Load(opts.Screenshot);
        var projection = new TownProjection(opts.CameraX, opts.GroundY - (opts.Room ? opts.CameraY : 0));
        List<SceneSprite> sprites;
        if (opts.Room)
        {
            RoomMap room = RoomMap.Read(assets, opts.MapPath);
            Console.WriteLine("房间 " + opts.MapPath + ":类型 " + room.Type + ",瓦片 " + room.Tiles.Count + " 块(宽 " + room.Width + "),扩展 " + room.ExtendedRowCount + " 排(镜头 y 范围 0.." + room.ExtendedHeightPixels + "),视差 远/中/近 "
                + room.FarScroll + "/" + room.MiddleScroll + "/" + room.NearScroll + ",背景动画 " + room.Backgrounds.Count + " 个,图层动画 " + room.Animations.Count + " 个");
            List<SceneSprite>? heroSprites = null;
            if (opts.Hero != null)
            {
                TownHero heroInRoom = CreateHero(assets, opts);
                heroSprites = heroInRoom.Sprites(assets, projection);
            }
            if (opts.Monsters != null)
            {
                heroSprites ??= new List<SceneSprite>();
                MonsterCatalog catalog = MonsterCatalog.Read(assets);
                var models = new Dictionary<int, MonsterModel?>();
                int index = 0;
                foreach (RoomMonsterSpawn spawn in room.Monsters)
                {
                    if (!models.TryGetValue(spawn.Code, out MonsterModel? model))
                    {
                        string? mobPath = catalog.PathOf(spawn.Code);
                        model = mobPath == null ? null : MonsterModel.Read(assets, mobPath);
                        models[spawn.Code] = model;
                    }
                    SceneSprite? sprite = model == null ? null : MonsterSprites.SpriteAt(assets, model.Waiting, model.PathOf(model.Waiting), 0, projection,
                        spawn.X, spawn.Y, spawn.Z, opts.Monsters == "right", MonsterSprites.ObjectIdBase + index);
                    Console.WriteLine("怪物 " + spawn.Code + " 在 (" + spawn.X + "," + spawn.Y + "," + spawn.Z + ") " + spawn.Class + (sprite == null ? "  没有画面" : "  左上角(" + sprite.ScreenX + "," + sprite.ScreenY + ")"));
                    if (sprite != null)
                    {
                        heroSprites.Add(sprite);
                    }
                    index++;
                }
            }
            RoomContent content = RoomContent.Load(assets, room, BuildGates(assets, room, opts));
            Console.WriteLine("被动物件 " + room.PassiveObjects.Count + " 个,其中画出的障碍物 " + content.Objects.Count + " 个");
            sprites = RoomScene.Build(assets, content, opts.CameraX, opts.GroundY, opts.TimeMs, heroSprites, opts.CameraY);
        }
        else if (opts.Hero == null)
        {
            sprites = TownScene.BuildScene(assets, opts.MapPath, projection, opts.TimeMs);
        }
        else
        {
            TownHero hero = CreateHero(assets, opts);
            Console.WriteLine("主角:世界 (" + hero.X + "," + hero.Y + "),朝" + (hero.FacingLeft ? "左" : "右") + "," + hero.Motion + " 已播放 " + hero.MotionMs
                + " ms → " + hero.CurrentAnimationPath + " 第 " + hero.CurrentFrame + " 帧;换装图层 " + hero.Outfit.Layers.Count + " 个");
            foreach (AvatarLayer layer in hero.Outfit.Layers)
            {
                Console.WriteLine("    图层键 " + layer.Key.ToString().PadLeft(5) + "  " + layer.Name.PadRight(12) + layer.ImagePath);
            }
            sprites = TownScene.BuildResolvedScene(assets, TownScene.ResolveLayers(assets, opts.MapPath),
                TownScene.Resolve(assets, TownScene.ReadMapNpcs(assets, opts.MapPath)), projection, opts.TimeMs, hero.Sprites(assets, projection));
        }
        Console.WriteLine("地图 " + opts.MapPath + ",时间 " + opts.TimeMs + " ms:绘制列表 " + sprites.Count + " 个精灵");
        foreach (SceneSprite s in sprites)
        {
            string who = s.NpcId == TownHero.ObjectId ? "主角" : s.NpcId >= 0 ? "NPC " + s.NpcId : "图层";
            Console.WriteLine("  [" + s.LayerRank + "] " + who.PadRight(8) + s.AnimationPath.PadRight(46) + "帧 " + s.AniFrame + "  "
                + (s.Blend == SpriteBlend.Additive ? "加法 A=" + s.TintAlpha : "普通").PadRight(12) + " 左上角(" + s.ScreenX + "," + s.ScreenY + ")  " + s.Frame.Width + "×" + s.Frame.Height);
        }

        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 40, 40, 255);
        TownScene.Render(canvas, sprites);

        const int W = 800, H = 600;
        if (shotOrNull == null)
        {
            // 没有原版截图:只输出合成结果,用作 Unity 播放器渲染的 CPU 参照
            PngWriter.Write(opts.PngFile!, W, H, canvas.Pixels);
            Console.WriteLine("已写 " + opts.PngFile + "(我们的 800×600 合成结果,背景 40,40,40)");
            return 0;
        }
        Raster shot = shotOrNull;
        int exact = 0, near = 0, total = W * H;
        const int Cell = 100;
        int cols = W / Cell, rows = H / Cell;
        var cellExact = new int[cols, rows];
        var mask = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int o = (y * W + x) * 4;
                uint c = shot.Pixels[y * shot.Width + x];
                int dr = Math.Abs(canvas.Pixels[o] - (int)((c >> 16) & 0xFF));
                int dg = Math.Abs(canvas.Pixels[o + 1] - (int)((c >> 8) & 0xFF));
                int db = Math.Abs(canvas.Pixels[o + 2] - (int)(c & 0xFF));
                int d = Math.Max(dr, Math.Max(dg, db));
                bool isExact = d == 0;
                bool isNear = d <= 2;
                if (isExact)
                {
                    exact++;
                    cellExact[x / Cell, y / Cell]++;
                }
                if (isNear)
                {
                    near++;
                }
                mask[o] = (byte)(isExact ? 40 : isNear ? 230 : 230);
                mask[o + 1] = (byte)(isExact ? 220 : isNear ? 200 : 40);
                mask[o + 2] = 40;
                mask[o + 3] = 255;
            }
        }
        Console.WriteLine();
        Console.WriteLine("整屏:精确一致 " + exact + "/" + total + " (" + (100.0 * exact / total).ToString("F1") + "%),每通道差 ≤2 的 "
            + near + " (" + (100.0 * near / total).ToString("F1") + "%)");
        Console.WriteLine("按 100×100 的格子,精确一致的百分比(行 = y,列 = x):");
        Console.Write("      ");
        for (int cx = 0; cx < cols; cx++)
        {
            Console.Write((cx * Cell).ToString().PadLeft(5));
        }
        Console.WriteLine();
        for (int cy = 0; cy < rows; cy++)
        {
            Console.Write((cy * Cell).ToString().PadLeft(5) + " ");
            for (int cx = 0; cx < cols; cx++)
            {
                Console.Write((100.0 * cellExact[cx, cy] / (Cell * Cell)).ToString("F0").PadLeft(5));
            }
            Console.WriteLine();
        }

        if (opts.Hero != null)
        {
            ReportHeroOnly(assets, opts, projection, shot);
        }

        foreach ((int x0, int y0, int x1, int y1) in opts.EvalRects)
        {
            int n = 0, e = 0, nr = 0;
            for (int y = Math.Max(0, y0); y < Math.Min(H, y1); y++)
            {
                for (int x = Math.Max(0, x0); x < Math.Min(W, x1); x++)
                {
                    int o = (y * W + x) * 4;
                    uint c = shot.Pixels[y * shot.Width + x];
                    int d = Math.Max(Math.Abs(canvas.Pixels[o] - (int)((c >> 16) & 0xFF)),
                        Math.Max(Math.Abs(canvas.Pixels[o + 1] - (int)((c >> 8) & 0xFF)), Math.Abs(canvas.Pixels[o + 2] - (int)(c & 0xFF))));
                    n++;
                    if (d == 0)
                    {
                        e++;
                    }
                    if (d <= 2)
                    {
                        nr++;
                    }
                }
            }
            Console.WriteLine("区域 [" + x0 + "," + y0 + ")-[" + x1 + "," + y1 + "):精确一致 " + e + "/" + n + " (" + (100.0 * e / Math.Max(1, n)).ToString("F2")
                + "%),差 ≤2 的 " + nr + " (" + (100.0 * nr / Math.Max(1, n)).ToString("F2") + "%)");
        }

        if (opts.PngFile != null)
        {
            var outBuf = new byte[3 * W * H * 4];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int dst = (y * 3 * W + x) * 4;
                    int src = (y * W + x) * 4;
                    Buffer.BlockCopy(canvas.Pixels, src, outBuf, dst, 4);
                    uint c = shot.Pixels[y * shot.Width + x];
                    int mid = dst + W * 4;
                    outBuf[mid] = (byte)(c >> 16);
                    outBuf[mid + 1] = (byte)(c >> 8);
                    outBuf[mid + 2] = (byte)c;
                    outBuf[mid + 3] = 255;
                    Buffer.BlockCopy(mask, src, outBuf, dst + 2 * W * 4, 4);
                }
            }
            PngWriter.Write(opts.PngFile, 3 * W, H, outBuf);
            Console.WriteLine("已写 " + opts.PngFile + "(左=我们 中=原版 右=精确一致为绿、差 ≤2 为黄、其余为红)");
        }
        Console.Error.WriteLine("用时 " + sw.Elapsed.TotalSeconds.ToString("F1") + "s");
        return 0;
    }

    /// <summary>按 --gate 的描述(left|right|up|down:编号[:closed|open])造出房间的出口,位置取自地图的 [pathgate pos]。</summary>
    internal static List<RoomGate> BuildGates(DnfAssets assets, RoomMap room, Options opts)
    {
        var gates = new List<RoomGate>();
        if (opts.DemoGates)
        {
            if (!DemoRoom.TryGetSlice(opts.MapPath, out RoomSlice slice))
            {
                throw new UserError("--demo-gates:地图 " + opts.MapPath + " 没有量过的出口摆法");
            }
            gates.AddRange(DemoRoom.BuildGates(assets, room, slice));
            foreach (RoomGate g in gates)
            {
                Console.WriteLine("门(演示摆法) " + g.Side + " 编号 " + g.Object.ObjectId + " 位置(" + g.X + "," + g.Y + ")");
            }
        }
        if (opts.Gates.Count == 0)
        {
            return gates;
        }
        PassiveObjectCatalog catalog = PassiveObjectCatalog.Read(assets);
        foreach (string spec in opts.Gates)
        {
            string[] g = spec.Split(':');
            if (g.Length < 2 || !Enum.TryParse(g[0], true, out GateSide side) || !int.TryParse(g[1], out int objectId))
            {
                throw new UserError("--gate 需要 left|right|up|down:编号[:closed|open],例如 right:364:closed");
            }
            int[]? pos = room.Gates[(int)side];
            if (pos == null)
            {
                throw new UserError("地图 " + opts.MapPath + " 的 [pathgate pos] 没有 " + side + " 的位置");
            }
            GateObject obj = GateObject.Read(assets, catalog, objectId) ?? throw new UserError("找不到门物件 " + objectId);
            var gate = new RoomGate(assets, obj, side, pos[0], pos[1]);
            if (g.Length >= 3 && g[2] == "open")
            {
                gate.Open(0);
            }
            gates.Add(gate);
            Console.WriteLine("门 " + side + " 编号 " + objectId + " " + obj.ScriptPath + " 位置(" + pos[0] + "," + pos[1] + ") 图层 " + obj.Layer + " 动画段 " + obj.Parts.Count);
        }
        return gates;
    }

    /// <summary>只统计主角自己的像素的吻合率(方法见 <see cref="HeroMatcher"/>)。</summary>
    private static void ReportHeroOnly(DnfAssets assets, Options opts, TownProjection projection, Raster shot)
    {
        TownHero hero = CreateHero(assets, opts);
        (int opaque, int exact, int near) = new HeroMatcher().Measure(hero.Sprites(assets, projection), shot);
        Console.WriteLine();
        Console.WriteLine("主角不透明像素 " + opaque + " 个:与截图精确一致 " + exact + " (" + (100.0 * exact / Math.Max(1, opaque)).ToString("F1")
            + "%),差 ≤2 的 " + near + " (" + (100.0 * near / Math.Max(1, opaque)).ToString("F1") + "%)");
    }

    /// <summary>解析 --hero x,y,left|right,rest|move,动作毫秒,按默认外观(游戏数据自带的 *_default 装备加一件武器)造出主角。</summary>
    private static TownHero CreateHero(DnfAssets assets, Options opts)
    {
        string[] parts = opts.Hero!.Split(',');
        if (parts.Length != 5 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x)
            || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y)
            || parts[2] is not ("left" or "right") || !HeroFind.IsMotionSpec(parts[3]) || !long.TryParse(parts[4], out long ms) || ms < 0)
        {
            throw new UserError("--hero 需要 x,y,left|right,动作,动作毫秒(动作:rest、move、dashattack、attack<N>、etc<N>;例如 615,234,right,rest,600)");
        }
        TownHero hero = BuildDefaultHero(assets, opts.MapPath, opts.HeroWeapon, x, y, parts[2] == "left", opts.HeroWait);
        HeroFind.PlaceMotion(hero, parts[3], x, y, parts[2] == "left", ms);
        return hero;
    }

    /// <summary>默认外观的主角(游戏数据自带的 *_default 装备加一件武器),可走区域取自地图的 [virtual movable area]。</summary>
    internal static TownHero BuildDefaultHero(DnfAssets assets, string mapPath, string weaponEqu, double x, double y, bool facingLeft, bool wait = false)
    {
        return DemoHero.CreateSwordman(assets, mapPath, x, y, facingLeft, weaponEqu, wait ? TownHero.DungeonRestTag : TownHero.TownRestTag);
    }
}
