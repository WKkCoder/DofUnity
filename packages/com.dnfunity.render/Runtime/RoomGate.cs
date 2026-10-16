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
    /// [string data] 依次是若干动画,可能以一个 .til(出入口通行格,这里暂不使用)结尾;[int data] 与它们一一对应,每个是 (种类, 变体号) 一对,
    /// 带 .til 的那一对是 (列, 行) 在门的通行格里的锚点。图层取自 [layer]。
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

        public GateObject(int objectId, string scriptPath, string layer, IReadOnlyList<GatePart> parts, string doorTilePath)
        {
            ObjectId = objectId;
            ScriptPath = scriptPath;
            Layer = layer;
            Parts = parts ?? throw new ArgumentNullException(nameof(parts));
            DoorTilePath = doorTilePath;
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
            for (int i = 0; i < strings.Count; i++)
            {
                string resolved = PvfPath.ResolveRelativeTo(path, strings[i]);
                if (resolved.EndsWith(".til", StringComparison.Ordinal))
                {
                    tile = resolved;
                    continue;
                }
                int kind = 2 * i < ints.Count ? ints[2 * i] : 0;
                int variant = 2 * i + 1 < ints.Count ? ints[2 * i + 1] : 1;
                parts.Add(new GatePart(kind, variant, resolved));
            }
            return new GateObject(objectId, path, layer, parts, tile);
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

        public RoomGate(DnfAssets assets, GateObject obj, GateSide side, int x, int y)
        {
            _object = obj ?? throw new ArgumentNullException(nameof(obj));
            Side = side;
            X = x;
            Y = y;
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
}
