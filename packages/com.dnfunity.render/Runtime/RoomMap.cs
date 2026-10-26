using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>
    /// 副本地图用的地面瓦片(.til)。一块瓦片宽 224 像素;脚本给出它的图(IMG 帧)和一张通行表。
    /// 通行表是 14 列(每列 16 像素宽)乘若干行的整数表,行优先;0 表示能走,2 是不能走(1、5、6 极少见,按不能走处理)。
    /// 行高 12 像素、行 0 的上边在世界 y = 0(证据 E-067):在洛兰的房间里,通行表里能走的行恰好构成一条横带,
    /// 带的上沿与下沿等于该地图 [pathgate pos] 里上门与下门的 y,这是定下行高与原点的依据。
    /// </summary>
    public sealed class TileFile
    {
        public const int TileWidth = 224;
        public const int Columns = 14;
        public const int CellWidth = TileWidth / Columns;
        public const int CellHeight = 12;

        /// <summary>没有 [img pos] 时图像上边的屏幕 y(全库 [img pos] 的取值有 80、120、30,没写的地图按 80;洛兰量到的是 80,证据 E-067)。</summary>
        public const int DefaultImagePosY = 80;

        public string Path { get; }

        /// <summary>每个 [IMAGE]:NPK 里的图名("sprite/…")与帧号。</summary>
        public IReadOnlyList<KeyValuePair<string, int>> Images { get; }

        public int ImagePosY { get; }

        /// <summary>通行表,行优先,每行 <see cref="Columns"/> 个。</summary>
        public IReadOnlyList<byte> Pass { get; }

        public int Rows => Pass.Count / Columns;

        public TileFile(string path, IReadOnlyList<KeyValuePair<string, int>> images, int imagePosY, IReadOnlyList<byte> pass)
        {
            Path = path;
            Images = images ?? throw new ArgumentNullException(nameof(images));
            ImagePosY = imagePosY;
            Pass = pass ?? throw new ArgumentNullException(nameof(pass));
        }

        /// <summary>读一个 .til;PVF 里没有或不是标记流脚本时返回 null。</summary>
        public static TileFile Read(DnfAssets assets, string tilPath)
        {
            ScriptLine[] doc = assets.ReadScript(tilPath);
            if (doc == null)
            {
                return null;
            }
            var images = new List<KeyValuePair<string, int>>();
            var pass = new List<byte>();
            int imagePos = DefaultImagePosY;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[IMAGE]"))
                {
                    string name = line.FirstString();
                    List<int> n = line.Ints();
                    if (name != null)
                    {
                        images.Add(new KeyValuePair<string, int>("sprite/" + name.Replace('\\', '/').ToLowerInvariant(), n.Count > 0 ? n[0] : 0));
                    }
                }
                else if (line.TagIs("[img pos]"))
                {
                    List<int> n = line.Ints();
                    if (n.Count > 0)
                    {
                        imagePos = n[0];
                    }
                }
                else if (line.TagIs("[pass type]"))
                {
                    foreach (int v in line.Ints())
                    {
                        pass.Add((byte)(v < 0 || v > 255 ? 2 : v));
                    }
                }
            }
            // 客户端(0x013224C0)每行固定读 14 个值,第一个读得到才算一行;读完后不足的值取 0:数值个数不是 14 的倍数时,最后一行用 0(可走)补满
            while (pass.Count % Columns != 0)
            {
                pass.Add(0);
            }
            return new TileFile(tilPath, images, imagePos, pass);
        }

        /// <summary>某个格子是否能走;表外一律不能走。</summary>
        public bool CanWalk(int column, int row)
        {
            if (column < 0 || column >= Columns || row < 0 || row >= Rows)
            {
                return false;
            }
            return Pass[row * Columns + column] == 0;
        }

        /// <summary>通行表里某个格子的原始值;表外为 2(挡路,与客户端 0x007D77D0 在取不到瓦片时的返回值一致)。</summary>
        public byte PassValue(int column, int row)
        {
            if (column < 0 || column >= Columns || row < 0 || row >= Rows)
            {
                return 2;
            }
            return Pass[row * Columns + column];
        }

        /// <summary>
        /// 某个格子是否挡路。与 <see cref="CanWalk(int, int)"/> 不同,表外算"不挡"(出入口的门洞、门框只在表里画出自己的那一块,表外什么也没有)。
        /// 证据 E-072:出入口 .obj 自带的 .til 当作主角的碰撞体用,通行值非 0 的格子挡路。
        /// </summary>
        public bool IsSolid(int column, int row)
        {
            if (column < 0 || column >= Columns || row < 0 || row >= Rows)
            {
                return false;
            }
            return Pass[row * Columns + column] != 0;
        }
    }

    /// <summary>
    /// 地图 [monster] 里的一项(证据 E-069)。真实数据里每一项是 10 个值:怪物编号、两个标志(含义未知)、x、y、z、等级档(1 普通,2 首领)、
    /// 一个标志(含义未知)、摆放方式字符串("[fixed]")、种类字符串("[normal]" 或 "[boss]")。怪物编号查 monster/monster.lst(<see cref="MonsterCatalog"/>)。
    /// </summary>
    public readonly struct RoomMonsterSpawn
    {
        public int Code { get; }
        public int FlagA { get; }
        public int FlagB { get; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int Grade { get; }
        public int FlagC { get; }
        public string Placement { get; }
        public string Class { get; }

        public bool IsBoss => string.Equals(Class, "[boss]", StringComparison.OrdinalIgnoreCase);

        public RoomMonsterSpawn(int code, int flagA, int flagB, int x, int y, int z, int grade, int flagC, string placement, string monsterClass)
        {
            Code = code;
            FlagA = flagA;
            FlagB = flagB;
            X = x;
            Y = y;
            Z = z;
            Grade = grade;
            FlagC = flagC;
            Placement = placement ?? string.Empty;
            Class = monsterClass ?? string.Empty;
        }
    }

    /// <summary>地图 [background animation] 里的一项([ani info] 块):动画、图层与相对位置的写法。</summary>
    public sealed class RoomBackground
    {
        public string AnimationPath { get; }
        public string Layer { get; }
        public string Order { get; }

        public RoomBackground(string animationPath, string layer, string order)
        {
            AnimationPath = animationPath;
            Layer = layer;
            Order = order;
        }
    }

    /// <summary>四个出口的位置(世界坐标,脚本的 [pathgate pos] 里依次是 左、右、上、下)。</summary>
    public enum GateSide
    {
        Left = 0,
        Right = 1,
        Up = 2,
        Down = 3,
    }

    /// <summary>
    /// 副本里一个房间的地图脚本(.map)里与场景有关的字段:瓦片列表、三层视差滚动系数、背景动画、图层动画、出口位置与出生区。
    /// 瓦片一块 224 像素宽,房间宽度 = 瓦片数 × 224。
    /// </summary>
    public sealed class RoomMap
    {
        public const int ScreenWidth = 800;

        /// <summary>副本定义 [background pos] 的值(全库 326 个 .dgn 都是 80;洛兰量到瓦片图像与远景的上边都以它为基准,证据 E-067)。</summary>
        public const int DefaultBackgroundPos = 80;

        public string MapPath { get; }

        /// <summary>"[normal]" 或 "[boss]" 等;没写为空串。</summary>
        public string Type { get; }

        public IReadOnlyList<TileFile> Tiles { get; }

        /// <summary>
        /// [extended tile]:接在基础瓦片(<see cref="Tiles"/>)下面的扩展瓦片,行优先,每行的列数 = 基础瓦片数,每块的通行表只看前 <see cref="ExtendedTileRows"/> 行
        /// (证据 E-113,客户端 0x007D77D0、0x007D7110、0x0055DEE0;没有这个标签的地图为空列表)。
        /// 用途:基础瓦片的通行表是整张图最上面的 <see cref="BaseRows"/> 行,扩展瓦片接着往下排,所以带它的地图比基础瓦片高、可走的地方也在它上面。
        /// 缺少的瓦片文件在这里换成空瓦片占位(不让后面的瓦片错位),占位的格子挡路、没有图。
        /// </summary>
        public IReadOnlyList<TileFile> ExtendedTiles { get; }

        /// <summary>每块扩展瓦片占的通行表行数(客户端写死 10 行,即 120 像素)。</summary>
        public const int ExtendedTileRows = 10;

        /// <summary>基础瓦片的通行表行数(取第一块瓦片的行数;没有瓦片时为 0)。</summary>
        public int BaseRows => Tiles.Count == 0 ? 0 : Tiles[0].Rows;

        /// <summary>扩展瓦片排了几行:扩展瓦片数 ÷ 基础瓦片数(整数除法,与客户端一致;没有基础瓦片时为 0)。</summary>
        public int ExtendedRowCount => Tiles.Count == 0 ? 0 : ExtendedTiles.Count / Tiles.Count;

        /// <summary>舞台格的总行数:基础行数 + 扩展行数 × <see cref="ExtendedTileRows"/>。</summary>
        public int TotalRows => BaseRows + ExtendedRowCount * ExtendedTileRows;

        /// <summary>整张图的高(像素,通行表那一部分,不含图像上方留的空)。</summary>
        public int HeightPixels => TotalRows * TileFile.CellHeight;

        /// <summary>扩展瓦片部分的高(像素);镜头在纵向上能挪的范围就是 [0, 它](客户端 0x007D7510、0x007D7650)。</summary>
        public int ExtendedHeightPixels => ExtendedRowCount * ExtendedTileRows * TileFile.CellHeight;

        /// <summary>远景、中景、近景的视差系数(百分数;镜头每动 100 像素,该层动多少像素)。</summary>
        public int FarScroll { get; }

        public int MiddleScroll { get; }
        public int NearScroll { get; }

        public IReadOnlyList<RoomBackground> Backgrounds { get; }
        public IReadOnlyList<MapAnimation> Animations { get; }

        /// <summary>四个出口 (x, y),按 <see cref="GateSide"/> 排;脚本里没有写的出口为 null(数组长度恒为 4)。</summary>
        public IReadOnlyList<int[]> Gates { get; }

        /// <summary>
        /// [pathgate recognize range]:四个出口的识别矩形的 (宽, 高),按 <see cref="GateSide"/> 排;脚本没写的出口为 null,宽或高不大于 0 的分量用缺省值(见 <see cref="GateAreas"/>)。
        /// 数组长度恒为 4。
        /// </summary>
        public IReadOnlyList<int[]> GateRanges { get; }

        /// <summary>四个出口的识别矩形(证据 E-074,见 <see cref="Render.GateAreas"/>),按 <see cref="GateSide"/> 排;没有这个出口位置的为 null。</summary>
        public IReadOnlyList<WorldRect?> GateRects => _gateRects ?? (_gateRects = GateAreas.Compute(Gates, GateRanges));

        private WorldRect?[] _gateRects;

        /// <summary>[dungeon start area] 的矩形;没有时为 null。</summary>
        public WorldRect? StartArea { get; }

        /// <summary>[passive object] 的摆放(编号、x、y、z);粒子光源与障碍物都在这里,按编号查 <see cref="PassiveObjectCatalog"/>。</summary>
        public IReadOnlyList<PassivePlacement> PassiveObjects { get; }

        /// <summary>
        /// [special passive object] 的摆放(证据 E-126):和 [passive object] 一样是编号加坐标,但每一项还挂着被破坏时触发的动作(出怪、掉物品……)。
        /// 没有这个标签是空列表。
        /// </summary>
        public IReadOnlyList<SpecialPlacement> SpecialObjects { get; }

        /// <summary>[monster] 的摆放(进房间时的怪物与位置,证据 E-069)。</summary>
        public IReadOnlyList<RoomMonsterSpawn> Monsters { get; }

        /// <summary>
        /// [sound] 里的声音标签(audio.xml 的 ID,按脚本顺序,证据 E-089):背景音乐、环境声、偶尔响的音效混在一起,
        /// 种类以 audio.xml 里的条目为准(MUSIC、AMBIENT、其余),位置并不固定。没有 [sound] 就是空列表。
        /// </summary>
        public IReadOnlyList<string> Sounds { get; }

        public int Width => Tiles.Count * TileFile.TileWidth;

        /// <summary>镜头 x 的最大值(房间宽减屏宽,不小于 0)。</summary>
        public int MaxCameraX => Math.Max(0, Width - ScreenWidth);

        public RoomMap(string mapPath, string type, IReadOnlyList<TileFile> tiles, int farScroll, int middleScroll, int nearScroll,
            IReadOnlyList<RoomBackground> backgrounds, IReadOnlyList<MapAnimation> animations, IReadOnlyList<int[]> gates, WorldRect? startArea,
            IReadOnlyList<PassivePlacement> passiveObjects = null, IReadOnlyList<RoomMonsterSpawn> monsters = null,
            IReadOnlyList<int[]> gateRanges = null, IReadOnlyList<string> sounds = null, IReadOnlyList<TileFile> extendedTiles = null,
            IReadOnlyList<SpecialPlacement> specialObjects = null)
        {
            SpecialObjects = specialObjects ?? new List<SpecialPlacement>();
            Sounds = sounds ?? new List<string>();
            GateRanges = gateRanges ?? new List<int[]> { null, null, null, null };
            MapPath = mapPath;
            Type = type ?? string.Empty;
            Tiles = tiles ?? throw new ArgumentNullException(nameof(tiles));
            ExtendedTiles = extendedTiles ?? new List<TileFile>();
            FarScroll = farScroll;
            MiddleScroll = middleScroll;
            NearScroll = nearScroll;
            Backgrounds = backgrounds ?? throw new ArgumentNullException(nameof(backgrounds));
            Animations = animations ?? throw new ArgumentNullException(nameof(animations));
            Gates = gates ?? throw new ArgumentNullException(nameof(gates));
            StartArea = startArea;
            PassiveObjects = passiveObjects ?? new List<PassivePlacement>();
            Monsters = monsters ?? new List<RoomMonsterSpawn>();
        }

        /// <summary>
        /// 一张地图脚本里 [sound] 的全部声音标签(按脚本顺序;空白字符串与数被略过)。<see cref="Read"/> 用的就是这条规则,命令行的全库覆盖检查也用它,
        /// 不必为了数声音把每张地图的瓦片与动画都读一遍。
        /// </summary>
        public static List<string> ReadSounds(IEnumerable<ScriptLine> doc)
        {
            var sounds = new List<string>();
            if (doc == null)
            {
                return sounds;
            }
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[sound]"))
                {
                    AddSounds(line, sounds);
                }
            }
            return sounds;
        }

        private static void AddSounds(ScriptLine line, List<string> into)
        {
            foreach (ScriptValue v in line.Values)
            {
                if (v.Kind == ScriptValueKind.String && !string.IsNullOrWhiteSpace(v.Text))
                {
                    into.Add(v.Text);
                }
            }
        }

        /// <summary>读一张副本房间地图。缺少的瓦片文件按"没有这块瓦片"丢弃(所以宽度会变小),不抛异常。</summary>
        public static RoomMap Read(DnfAssets assets, string mapPath)
        {
            ScriptLine[] doc = assets.ReadScript(mapPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有地图脚本: " + mapPath);
            }
            string type = string.Empty;
            var tiles = new List<TileFile>();
            var extended = new List<TileFile>();
            int far = 0, middle = 0, near = 0;
            var backgrounds = new List<RoomBackground>();
            var gates = new List<int[]> { null, null, null, null };
            var ranges = new List<int[]> { null, null, null, null };
            WorldRect? start = null;
            var passive = new List<PassivePlacement>();
            var special = new List<SpecialPlacement>();
            var monsters = new List<RoomMonsterSpawn>();
            var sounds = new List<string>();

            bool inInfo = false;
            string infoFile = null, infoLayer = null, infoOrder = null;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[type]"))
                {
                    type = line.FirstString() ?? string.Empty;
                }
                else if (line.TagIs("[tile]"))
                {
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind != ScriptValueKind.String || v.Text == null)
                        {
                            continue;
                        }
                        TileFile tile = TileFile.Read(assets, PvfPath.ResolveRelativeTo(mapPath, v.Text));
                        if (tile != null)
                        {
                            tiles.Add(tile);
                        }
                    }
                }
                else if (line.TagIs("[extended tile]"))
                {
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind != ScriptValueKind.String || v.Text == null)
                        {
                            continue;
                        }
                        string tilePath = PvfPath.ResolveRelativeTo(mapPath, v.Text);
                        // 缺的瓦片换成空瓦片占位:扩展瓦片按下标排成网格,丢掉一块会让后面的全部错位
                        extended.Add(TileFile.Read(assets, tilePath) ?? new TileFile(tilePath, new List<KeyValuePair<string, int>>(), TileFile.DefaultImagePosY, new List<byte>()));
                    }
                }
                else if (line.TagIs("[far sight scroll]"))
                {
                    far = FirstInt(line, far);
                }
                else if (line.TagIs("[middle sight scroll]"))
                {
                    middle = FirstInt(line, middle);
                }
                else if (line.TagIs("[near sight scroll]"))
                {
                    near = FirstInt(line, near);
                }
                else if (line.TagIs("[ani info]"))
                {
                    inInfo = true;
                    infoFile = infoLayer = infoOrder = null;
                }
                else if (line.TagIs("[/ani info]"))
                {
                    if (inInfo && infoFile != null)
                    {
                        backgrounds.Add(new RoomBackground(PvfPath.ResolveRelativeTo(mapPath, infoFile), (infoLayer ?? string.Empty).ToLowerInvariant(),
                            (infoOrder ?? string.Empty).ToLowerInvariant()));
                    }
                    inInfo = false;
                }
                else if (inInfo && line.TagIs("[filename]"))
                {
                    infoFile = line.FirstString();
                }
                else if (inInfo && line.TagIs("[layer]"))
                {
                    infoLayer = line.FirstString();
                }
                else if (inInfo && line.TagIs("[order]"))
                {
                    infoOrder = line.FirstString();
                }
                else if (line.TagIs("[pathgate pos]"))
                {
                    List<int> n = line.Ints();
                    for (int side = 0; side < 4 && side * 2 + 1 < n.Count; side++)
                    {
                        gates[side] = new[] { n[side * 2], n[side * 2 + 1] };
                    }
                }
                else if (line.TagIs("[pathgate recognize range]"))
                {
                    List<int> n = line.Ints();
                    for (int side = 0; side < 4 && side * 2 + 1 < n.Count; side++)
                    {
                        ranges[side] = new[] { n[side * 2], n[side * 2 + 1] };
                    }
                }
                else if (line.TagIs("[dungeon start area]"))
                {
                    List<int> n = line.Ints();
                    if (n.Count >= 4)
                    {
                        start = new WorldRect(n[0], n[1], n[2], n[3]);
                    }
                }
                else if (line.TagIs("[passive object]"))
                {
                    List<int> n = line.Ints();
                    for (int i = 0; i + 3 < n.Count; i += 4)
                    {
                        passive.Add(new PassivePlacement(n[i], n[i + 1], n[i + 2], n[i + 3]));
                    }
                }
                else if (line.TagIs("[special passive object]"))
                {
                    int items = 0;
                    foreach (SpecialPlacement s in special)
                    {
                        items += s.ItemIndex >= 0 ? 1 : 0;
                    }
                    special.AddRange(SpecialPlacement.Parse(line.Values, special.Count, items));
                }
                else if (line.TagIs("[monster]"))
                {
                    ReadMonsters(line.Values, monsters);
                }
                else if (line.TagIs("[sound]"))
                {
                    AddSounds(line, sounds);
                }
            }
            return new RoomMap(mapPath, type, tiles, far, middle, near, backgrounds, MapScript.ReadAnimations(assets, mapPath), gates, start, passive, monsters, ranges, sounds, extended,
                special);
        }

        /// <summary>
        /// [monster] 的值:每项 8 个数加 2 个字符串。数不够 8 个、或后面没有字符串的残项被丢弃(没有这样的数据,是容错)。
        /// </summary>
        private static void ReadMonsters(ScriptValue[] v, List<RoomMonsterSpawn> into)
        {
            int i = 0;
            while (i + 9 < v.Length)
            {
                bool ok = true;
                for (int k = 0; k < 8 && ok; k++)
                {
                    ok = v[i + k].IsNumber;
                }
                ok = ok && v[i + 8].Kind == ScriptValueKind.String && v[i + 9].Kind == ScriptValueKind.String;
                if (!ok)
                {
                    i++;
                    continue;
                }
                into.Add(new RoomMonsterSpawn(v[i].Int, v[i + 1].Int, v[i + 2].Int, v[i + 3].Int, v[i + 4].Int, v[i + 5].Int, v[i + 6].Int, v[i + 7].Int,
                    v[i + 8].Text, v[i + 9].Text));
                i += 10;
            }
        }

        private static int FirstInt(ScriptLine line, int fallback)
        {
            List<int> n = line.Ints();
            return n.Count > 0 ? n[0] : fallback;
        }

        /// <summary>
        /// 离 (x, y) 最近(直线距离)的能走的整数点;(x, y) 本身能走就原样返回,整个房间都没有能走的格子时也原样返回。
        /// 由近到远一圈圈(正方形环)找,找到第一个之后再多找几圈,直到环的最小距离超过已找到的最近距离。
        /// </summary>
        public void NearestWalkable(double x, double y, out double outX, out double outY)
        {
            outX = x;
            outY = y;
            if (CanWalk(x, y))
            {
                return;
            }
            int cx = (int)Math.Floor(x), cy = (int)Math.Floor(y);
            int maxRadius = Math.Max(Width, 600);
            double bestSquared = double.MaxValue;
            int bestX = 0, bestY = 0;
            for (int r = 1; r <= maxRadius; r++)
            {
                if (bestSquared < double.MaxValue && (double)r * r > bestSquared)
                {
                    break;
                }
                for (int d = -r; d <= r; d++)
                {
                    Consider(cx + d, cy - r, x, y, ref bestSquared, ref bestX, ref bestY);
                    Consider(cx + d, cy + r, x, y, ref bestSquared, ref bestX, ref bestY);
                    Consider(cx - r, cy + d, x, y, ref bestSquared, ref bestX, ref bestY);
                    Consider(cx + r, cy + d, x, y, ref bestSquared, ref bestX, ref bestY);
                }
            }
            if (bestSquared < double.MaxValue)
            {
                outX = bestX;
                outY = bestY;
            }
        }

        private void Consider(int px, int py, double x, double y, ref double bestSquared, ref int bestX, ref int bestY)
        {
            if (!CanWalk(px, py))
            {
                return;
            }
            double dx = px - x, dy = py - y;
            double squared = dx * dx + dy * dy;
            if (squared < bestSquared)
            {
                bestSquared = squared;
                bestX = px;
                bestY = py;
            }
        }

        /// <summary>世界 (x, y) 是否在能走的格子里(x 在所有瓦片之外或 y 在通行表之外都不能走)。</summary>
        public bool CanWalk(double x, double y)
        {
            if (x < 0 || y < 0)
            {
                return false;
            }
            return PassAt((int)Math.Floor(x) / TileFile.CellWidth, (int)Math.Floor(y) / TileFile.CellHeight) == 0;
        }

        /// <summary>
        /// 舞台格 (<paramref name="column"/>, <paramref name="row"/>)(一格 16 × 12 像素,列数 = 基础瓦片数 × 14)的通行值,取不到瓦片时为 2(挡路)。
        /// 客户端 0x007D77D0 的规则(证据 E-113):行号小于 <see cref="BaseRows"/> 的取基础瓦片(列 ÷ 14 号)的那一行;
        /// 否则行号减去 <see cref="BaseRows"/> 后,每 <see cref="ExtendedTileRows"/> 行是一排扩展瓦片,排号 = 商,排内行号 = 余数,
        /// 扩展瓦片的下标 = 瓦片列号 + 基础瓦片数 × 排号,下标越界就挡路。
        /// </summary>
        public byte PassAt(int column, int row)
        {
            if (column < 0 || row < 0 || Tiles.Count == 0)
            {
                return 2;
            }
            int tileColumn = column / TileFile.Columns;
            int inTile = column % TileFile.Columns;
            if (tileColumn >= Tiles.Count)
            {
                return 2;
            }
            int baseRows = Tiles[0].Rows;
            if (row < baseRows)
            {
                return Tiles[tileColumn].PassValue(inTile, row);
            }
            int extendedRow = (row - baseRows) / ExtendedTileRows;
            int index = tileColumn + Tiles.Count * extendedRow;
            if (extendedRow >= ExtendedRowCount || index >= ExtendedTiles.Count)
            {
                return 2;
            }
            return ExtendedTiles[index].PassValue(inTile, (row - baseRows) % ExtendedTileRows);
        }
    }
}
