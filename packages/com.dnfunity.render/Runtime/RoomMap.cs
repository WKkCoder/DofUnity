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

        /// <summary>远景、中景、近景的视差系数(百分数;镜头每动 100 像素,该层动多少像素)。</summary>
        public int FarScroll { get; }

        public int MiddleScroll { get; }
        public int NearScroll { get; }

        public IReadOnlyList<RoomBackground> Backgrounds { get; }
        public IReadOnlyList<MapAnimation> Animations { get; }

        /// <summary>四个出口 (x, y),按 <see cref="GateSide"/> 排;脚本里没有写的出口为 null(数组长度恒为 4)。</summary>
        public IReadOnlyList<int[]> Gates { get; }

        /// <summary>[dungeon start area] 的矩形;没有时为 null。</summary>
        public WorldRect? StartArea { get; }

        /// <summary>[passive object] 的摆放(编号、x、y、z);粒子光源与障碍物都在这里,按编号查 <see cref="PassiveObjectCatalog"/>。</summary>
        public IReadOnlyList<PassivePlacement> PassiveObjects { get; }

        public int Width => Tiles.Count * TileFile.TileWidth;

        /// <summary>镜头 x 的最大值(房间宽减屏宽,不小于 0)。</summary>
        public int MaxCameraX => Math.Max(0, Width - ScreenWidth);

        public RoomMap(string mapPath, string type, IReadOnlyList<TileFile> tiles, int farScroll, int middleScroll, int nearScroll,
            IReadOnlyList<RoomBackground> backgrounds, IReadOnlyList<MapAnimation> animations, IReadOnlyList<int[]> gates, WorldRect? startArea,
            IReadOnlyList<PassivePlacement> passiveObjects = null)
        {
            MapPath = mapPath;
            Type = type ?? string.Empty;
            Tiles = tiles ?? throw new ArgumentNullException(nameof(tiles));
            FarScroll = farScroll;
            MiddleScroll = middleScroll;
            NearScroll = nearScroll;
            Backgrounds = backgrounds ?? throw new ArgumentNullException(nameof(backgrounds));
            Animations = animations ?? throw new ArgumentNullException(nameof(animations));
            Gates = gates ?? throw new ArgumentNullException(nameof(gates));
            StartArea = startArea;
            PassiveObjects = passiveObjects ?? new List<PassivePlacement>();
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
            int far = 0, middle = 0, near = 0;
            var backgrounds = new List<RoomBackground>();
            var gates = new List<int[]> { null, null, null, null };
            WorldRect? start = null;
            var passive = new List<PassivePlacement>();

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
            }
            return new RoomMap(mapPath, type, tiles, far, middle, near, backgrounds, MapScript.ReadAnimations(assets, mapPath), gates, start, passive);
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
            int ix = (int)Math.Floor(x);
            int tile = ix / TileFile.TileWidth;
            if (tile >= Tiles.Count)
            {
                return false;
            }
            return Tiles[tile].CanWalk((ix % TileFile.TileWidth) / TileFile.CellWidth, (int)Math.Floor(y) / TileFile.CellHeight);
        }
    }
}
