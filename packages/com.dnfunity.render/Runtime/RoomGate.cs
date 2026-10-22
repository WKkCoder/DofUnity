using System;
using System.Collections.Generic;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>passiveobject/passiveobject.lst:没有标签,值是 "编号 路径" 一对一对,路径相对 passiveobject/ 目录。</summary>
    public sealed class PassiveObjectCatalog
    {
        public const string Path = "passiveobject/passiveobject.lst";
        public const string Root = "passiveobject/";

        private readonly Dictionary<int, string> _paths;

        private PassiveObjectCatalog(Dictionary<int, string> paths)
        {
            _paths = paths;
        }

        public int Count => _paths.Count;

        public static PassiveObjectCatalog Read(DnfAssets assets)
        {
            ScriptLine[] doc = assets.ReadScript(Path);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有 " + Path);
            }
            var map = new Dictionary<int, string>();
            foreach (ScriptLine line in doc)
            {
                ScriptValue[] v = line.Values;
                for (int i = 0; i + 1 < v.Length; i++)
                {
                    if (v[i].IsNumber && v[i + 1].Kind == ScriptValueKind.String && v[i + 1].Text != null)
                    {
                        map[v[i].Int] = Root + v[i + 1].Text.Replace('\\', '/').ToLowerInvariant();
                        i++;
                    }
                }
            }
            return new PassiveObjectCatalog(map);
        }

        /// <summary>编号对应脚本的 PVF 路径(小写);没有则返回 null。</summary>
        public string PathOf(int id)
        {
            return _paths.TryGetValue(id, out string path) ? path : null;
        }
    }

    /// <summary>门的状态。关着时画关门动画;开了之后画开门动画(淡出)与光。</summary>
    public enum GateState
    {
        Closed,
        Open,
    }

    /// <summary>门这个被动物件里的一段动画:一个 .ani、它的"种类"与"变体号"。</summary>
    public sealed class GatePart
    {
        /// <summary>种类 0:一直画(树、门框、灌木);1:门关着时画(关门);2:门开了画(开门淡出、光)。取自 [int data] 每对的第一个数,含义是对洛兰几个门对照出来的(证据 E-067)。</summary>
        public int Kind { get; }

        /// <summary>变体号,补进动画里图片名的 "%d%d"(写成 "0" 加变体号,例如 1 → "01";普通门是 1,首领门的门框与门是 5)。</summary>
        public int Variant { get; }

        public string AnimationPath { get; }

        public GatePart(int kind, int variant, string animationPath)
        {
            Kind = kind;
            Variant = variant;
            AnimationPath = animationPath;
        }
    }

    /// <summary>
    /// 副本里的出口(.obj,passiveobject.lst 里编号 361 到 370 是洛兰的 左/右/上/下 × 普通/首领/墙):
    /// [string data] 依次是若干动画,可能以一个 .til(出入口的通行格,当作主角的碰撞体,见 <see cref="GateBody"/>)结尾;
    /// [int data] 与它们一一对应,每个是 (种类, 变体号) 一对,带 .til 的那一对是 (列, 行):门的位置在通行格里的锚点(证据 E-072)。图层取自 [layer]。
    /// </summary>
    public sealed class GateObject
    {
        public int ObjectId { get; }
        public string ScriptPath { get; }

        /// <summary>[layer],小写带方括号,例如 "[bottom]"。</summary>
        public string Layer { get; }

        public IReadOnlyList<GatePart> Parts { get; }

        /// <summary>出入口通行格 .til 的 PVF 路径;没有为 null。</summary>
        public string DoorTilePath { get; }

        /// <summary>门的位置落在通行格的哪一格(列):通行格的左上角 x = 门 x − 列 × 16。没有 .til 时为 0。</summary>
        public int DoorAnchorColumn { get; }

        /// <summary>门的位置落在通行格的哪一格(行):通行格的左上角 y = 门 y − 行 × 12。没有 .til 时为 0。</summary>
        public int DoorAnchorRow { get; }

        public GateObject(int objectId, string scriptPath, string layer, IReadOnlyList<GatePart> parts, string doorTilePath,
            int doorAnchorColumn = 0, int doorAnchorRow = 0)
        {
            ObjectId = objectId;
            ScriptPath = scriptPath;
            Layer = layer;
            Parts = parts ?? throw new ArgumentNullException(nameof(parts));
            DoorTilePath = doorTilePath;
            DoorAnchorColumn = doorAnchorColumn;
            DoorAnchorRow = doorAnchorRow;
        }

        /// <summary>读一个门物件;编号不在目录里、或脚本缺失时返回 null。</summary>
        public static GateObject Read(DnfAssets assets, PassiveObjectCatalog catalog, int objectId)
        {
            string path = catalog.PathOf(objectId);
            ScriptLine[] doc = path == null ? null : assets.ReadScript(path);
            if (doc == null)
            {
                return null;
            }
            string layer = "[normal]";
            var strings = new List<string>();
            var ints = new List<int>();
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[layer]"))
                {
                    layer = (line.FirstString() ?? layer).ToLowerInvariant();
                }
                else if (line.TagIs("[string data]"))
                {
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind == ScriptValueKind.String && v.Text != null)
                        {
                            strings.Add(v.Text);
                        }
                    }
                }
                else if (line.TagIs("[int data]"))
                {
                    ints.AddRange(line.Ints());
                }
            }
            var parts = new List<GatePart>();
            string tile = null;
            int anchorColumn = 0, anchorRow = 0;
            for (int i = 0; i < strings.Count; i++)
            {
                string resolved = PvfPath.ResolveRelativeTo(path, strings[i]);
                int kind = 2 * i < ints.Count ? ints[2 * i] : 0;
                int variant = 2 * i + 1 < ints.Count ? ints[2 * i + 1] : 1;
                if (resolved.EndsWith(".til", StringComparison.Ordinal))
                {
                    tile = resolved;
                    anchorColumn = 2 * i < ints.Count ? ints[2 * i] : 0;
                    anchorRow = 2 * i + 1 < ints.Count ? ints[2 * i + 1] : 0;
                    continue;
                }
                parts.Add(new GatePart(kind, variant, resolved));
            }
            return new GateObject(objectId, path, layer, parts, tile, anchorColumn, anchorRow);
        }

        /// <summary>把动画里的图片名模板(例如 "Map/PathGate/GranFlorisSideGate%d%d.img")按变体号补成真实图名,小写。没有 "%d" 时原样返回。</summary>
        public static string FormatImageName(string template, int variant)
        {
            string s = template.Replace('\\', '/');
            int first = s.IndexOf("%d", StringComparison.Ordinal);
            if (first < 0)
            {
                return s.ToLowerInvariant();
            }
            s = s.Substring(0, first) + "0" + s.Substring(first + 2);
            int second = s.IndexOf("%d", StringComparison.Ordinal);
            if (second >= 0)
            {
                s = s.Substring(0, second) + variant.ToString(System.Globalization.CultureInfo.InvariantCulture) + s.Substring(second + 2);
            }
            return s.ToLowerInvariant();
        }
    }

    /// <summary>一个摆在房间里的出口:门物件 + 位置 + 已解析好的动画。</summary>
    public sealed class RoomGate
    {
        private readonly GateObject _object;
        private readonly List<AniFile> _anis = new List<AniFile>();
        private readonly List<AniTimeline> _timelines = new List<AniTimeline>();
        private readonly List<GatePart> _parts = new List<GatePart>();

        public GateSide Side { get; }
        public int X { get; }
        public int Y { get; }
        public GateObject Object => _object;

        public GateState State { get; private set; } = GateState.Closed;

        /// <summary>门开的那一刻的时钟读数(毫秒),开门动画与光从这里起算。</summary>
        public long OpenedAtMs { get; private set; }

        public void Open(long timeMs)
        {
            if (State != GateState.Open)
            {
                State = GateState.Open;
                OpenedAtMs = timeMs;
            }
        }

        /// <summary>开门动画当作早已播完的"开着"状态的起算时刻(一小时前):进房间时本来就没有怪物的出口、与回到已经清空的房间时用。</summary>
        public const long SettledOpenedAtMs = -3600000;

        /// <summary>直接变成开着,不播开门的淡出(淡出 0.5 秒是一次性的,光的脉动照常循环)。</summary>
        public void OpenSettled()
        {
            State = GateState.Open;
            OpenedAtMs = SettledOpenedAtMs;
        }

        public void Close()
        {
            State = GateState.Closed;
        }

        /// <summary>摆放用的图层大类(<see cref="TownScene.LayerRank"/>)。</summary>
        public int Rank { get; }

        /// <summary>出口的碰撞体(门框与墙);出口脚本没有 .til(上下出口)或读不到时为 null。</summary>
        public GateBody Body { get; private set; }

        /// <summary><see cref="BodyReachesGate"/> 找门体时,从出口位置那一行往上下各看多少像素。</summary>
        public const int PushRowReach = 20;

        /// <summary>
        /// 门体是不是真盖着出口位置所在的那几行(上下 <see cref="PushRowReach"/> 像素内、占位盒压得到):压得到,主角顶着走就会被它挡住,"顶进门体"可以当触发;
        /// 压不到,门体与出口无关,左右出口按没有门体的规则触发。通行表按客户端的每行 14 列排好后,锚点落在表外、表根本不在出口那一行附近的出口物件
        /// 全库有 21 个(共 416 个带门体的物件,汇总见 E-113);客户端对它们的触发代码没有读到,这条是兜底推断。
        /// </summary>
        public bool BodyReachesGate(int bodyWidth, int bodyDepth)
        {
            if (Body == null)
            {
                return false;
            }
            for (int d = 0; d <= PushRowReach; d++)
            {
                if (Body.Overlaps(X, Y + d, bodyWidth, bodyDepth) || Body.Overlaps(X, Y - d, bodyWidth, bodyDepth))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 出口的识别矩形(证据 E-074,见 <see cref="GateAreas"/>):服务端用它校验过门时主角的位置,进房间的落点落在对侧出口的这个矩形里,
        /// 上下出口的触发也要求主角在里面。造出口时没给就按缺省大小从出口位置算。
        /// </summary>
        public WorldRect Area { get; }

        public RoomGate(DnfAssets assets, GateObject obj, GateSide side, int x, int y, WorldRect? area = null)
        {
            _object = obj ?? throw new ArgumentNullException(nameof(obj));
            Side = side;
            X = x;
            Y = y;
            Area = area ?? GateAreas.For(side, x, y);
            Rank = TownScene.LayerRank(obj.Layer);
            foreach (GatePart part in obj.Parts)
            {
                AniFile ani = assets.LoadAni(part.AnimationPath);
                if (ani == null || ani.Frames.Length == 0)
                {
                    continue;
                }
                _parts.Add(part);
                _anis.Add(ani);
                _timelines.Add(new AniTimeline(ani));
            }
            if (obj.DoorTilePath != null)
            {
                TileFile tile = TileFile.Read(assets, obj.DoorTilePath);
                if (tile != null && tile.Pass.Count > 0)
                {
                    SetBody(tile, obj.DoorAnchorColumn, obj.DoorAnchorRow);
                }
            }
        }

        /// <summary>
        /// 给出口装上碰撞体:<paramref name="tile"/> 的通行格,出口位置所在的那一格(出口 x / 16、y / 12 取整)落在它的 (<paramref name="anchorColumn"/>, <paramref name="anchorRow"/>) 格上,
        /// 所以通行格左上角那一格是 (x / 16 − 列, y / 12 − 行)。证据 E-072、E-074(客户端 0x00A43770 先把出口坐标换成格子再对齐,不是像素对齐)。
        /// </summary>
        public void SetBody(TileFile tile, int anchorColumn, int anchorRow)
        {
            Body = tile == null ? null : new GateBody(tile, X / TileFile.CellWidth - anchorColumn, Y / TileFile.CellHeight - anchorRow, Side);
        }

        /// <summary>
        /// 门此刻的精灵,按 [string data] 的先后。关着时画种类 0 与 1,开着时画种类 0 与 2;
        /// 种类 2 的动画(开门淡出、光)从开门那一刻起算,种类 0 与 1 用 <paramref name="timeMs"/>。
        /// </summary>
        public List<SceneSprite> Sprites(DnfAssets assets, TownProjection projection, long timeMs)
        {
            GateState state = State;
            long openMs = Math.Max(0, timeMs - OpenedAtMs);
            var result = new List<SceneSprite>();
            for (int i = 0; i < _parts.Count; i++)
            {
                GatePart part = _parts[i];
                bool show = part.Kind == 0 || (part.Kind == 1 && state == GateState.Closed) || (part.Kind == 2 && state == GateState.Open);
                if (!show)
                {
                    continue;
                }
                long t = part.Kind == 2 ? openMs : timeMs;
                AniTimeline timeline = _timelines[i];
                AniSample sample = timeline.Sample(t);
                if (sample.Frame < 0)
                {
                    continue;
                }
                AniFile ani = _anis[i];
                AniFrame f = ani.Frames[sample.Frame];
                if (f.ImageIndex < 0 || f.ImageIndex >= ani.ImagePaths.Length)
                {
                    continue;
                }
                string img = "sprite/" + GateObject.FormatImageName(ani.ImagePaths[f.ImageIndex], part.Variant);
                if (!assets.TryGetFrame(img, f.FrameIndex, out SpriteFrame sprite))
                {
                    continue;
                }
                bool mirrored = f.TryGetScale(out float scaleX, out _) && scaleX < 0f;
                projection.TopLeft(X, Y, 0, f.X, f.Y, sprite.OffsetX, sprite.OffsetY, sprite.Width, mirrored, out int sx, out int sy);
                bool additive = f.TryGetGraphicEffect(out int effect) && effect == AniGraphicEffect.Additive;
                result.Add(new SceneSprite(sprite, sx, sy, Y, -1, part.AnimationPath, sample.Frame, mirrored,
                    additive ? SpriteBlend.Additive : SpriteBlend.Normal, timeline.TintAlphaAt(t), Rank));
            }
            return result;
        }
    }

    /// <summary>
    /// 出口的碰撞体(证据 E-072、E-074):出口 .obj 自带的 .til 的通行表,盖在舞台的格子上(客户端 0x00A43770):格子 16 × 12 像素,
    /// 通行表左上角那一格是 (<see cref="OriginColumn"/>, <see cref="OriginRow"/>) = (出口 x / 16 − 锚点列, 出口 y / 12 − 锚点行),
    /// 表里的值与舞台格子里原有的值取较大者;左出口的表第 0 列的值再沿用到地图左缘,右出口第 13 列的值沿用到地图右缘(<see cref="ValueAt"/>)。
    /// 主角站不站得下由占位盒([width] 的宽与深,左右上下各取一半,边界像素算在内)覆盖到的格子有没有非 0 值决定(<see cref="Overlaps(int, int, int, int)"/>,
    /// 与 <see cref="RoomGrid.IsFree"/> 同一个规则)。门开着或关着碰撞体都在(开门只是换了图):关着的门挡路,开着的门洞是"楔形"的,
    /// 主角顶着门洞的斜边走进去就触发换房间。
    /// </summary>
    public sealed class GateBody
    {
        public TileFile Tile { get; }

        /// <summary>通行表左上角那一格的列(整张地图的格子坐标,可以为负)。</summary>
        public int OriginColumn { get; }

        /// <summary>通行表左上角那一格的行。</summary>
        public int OriginRow { get; }

        /// <summary>这个出口在房间的哪一侧(决定通行表的值往地图边缘延伸的方向:左出口向左、右出口向右)。</summary>
        public GateSide Side { get; }

        public GateBody(TileFile tile, int originColumn, int originRow, GateSide side)
        {
            Tile = tile ?? throw new ArgumentNullException(nameof(tile));
            OriginColumn = originColumn;
            OriginRow = originRow;
            Side = side;
        }

        /// <summary>
        /// 门体盖在舞台格 (<paramref name="column"/>, <paramref name="row"/>) 上的值(0 = 没盖到或盖的是空格)。
        /// 表内的格子取表里的值;表所在的行里,左出口表左边的格子取第 0 列的值、右出口表右边的格子取第 13 列的值(延伸到地图边缘)。
        /// </summary>
        public byte ValueAt(int column, int row)
        {
            int r = row - OriginRow;
            if (r < 0 || r >= Tile.Rows)
            {
                return 0;
            }
            int c = column - OriginColumn;
            if (c < 0)
            {
                return Side == GateSide.Left && column >= 0 ? Tile.Pass[r * TileFile.Columns] : (byte)0;
            }
            if (c >= TileFile.Columns)
            {
                return Side == GateSide.Right ? Tile.Pass[r * TileFile.Columns + TileFile.Columns - 1] : (byte)0;
            }
            return Tile.Pass[r * TileFile.Columns + c];
        }

        /// <summary>
        /// 占位盒 <paramref name="bodyWidth"/> × <paramref name="bodyDepth"/>、脚下原点在 (<paramref name="x"/>, <paramref name="y"/>) 时,盒子覆盖的格子里有没有被门体盖成非 0 的
        /// (列 = 像素 / 16,行 = 像素 / 12,盒子左右各 宽 / 2、上下各 深 / 2,边界像素算在内)。
        /// </summary>
        public bool Overlaps(int x, int y, int bodyWidth, int bodyDepth)
        {
            int c0 = Math.Max((x - (bodyWidth >> 1)) / TileFile.CellWidth, 0);
            int c1 = (x + (bodyWidth >> 1)) / TileFile.CellWidth;
            int r0 = Math.Max((y - (bodyDepth >> 1)) / TileFile.CellHeight, 0);
            int r1 = (y + (bodyDepth >> 1)) / TileFile.CellHeight;
            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    if (ValueAt(c, r) != 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>同 <see cref="Overlaps(int, int, int, int)"/>,坐标取整(向下)。</summary>
        public bool Overlaps(double x, double y, int bodyWidth, int bodyDepth)
        {
            return Overlaps((int)Math.Floor(x), (int)Math.Floor(y), bodyWidth, bodyDepth);
        }
    }

    /// <summary>一个房间里所有出口的碰撞体,给主角当障碍(<see cref="TownHero.Blocker"/>)。<see cref="IHeroBlocker.BlockedBy"/> 返回压到的那个 <see cref="RoomGate"/>。</summary>
    public sealed class GateBodies : IHeroBlocker
    {
        private readonly List<RoomGate> _gates = new List<RoomGate>();
        private readonly int _bodyWidth;
        private readonly int _bodyDepth;

        /// <param name="gates">房间里的出口(没有碰撞体的跳过)。</param>
        /// <param name="bodyWidth">主角占位盒的宽(像素,<see cref="TownHero.BodyWidth"/>)。</param>
        /// <param name="bodyDepth">主角占位盒的深(像素,<see cref="TownHero.BodyDepth"/>)。</param>
        public GateBodies(IEnumerable<RoomGate> gates, int bodyWidth = TownHero.DefaultBodyWidth, int bodyDepth = TownHero.DefaultBodyDepth)
        {
            _bodyWidth = bodyWidth;
            _bodyDepth = bodyDepth;
            if (gates == null)
            {
                return;
            }
            foreach (RoomGate g in gates)
            {
                if (g != null && g.Body != null)
                {
                    _gates.Add(g);
                }
            }
        }

        public int Count => _gates.Count;

        public object BlockedBy(double x, double y)
        {
            foreach (RoomGate g in _gates)
            {
                if (g.Body.Overlaps(x, y, _bodyWidth, _bodyDepth))
                {
                    return g;
                }
            }
            return null;
        }
    }
}
