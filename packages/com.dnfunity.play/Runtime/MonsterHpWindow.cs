using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Game;
using Dnf.Render;

// 证据:目标怪物血条窗口的布局、血条长度、掉血动画、显示与隐藏时机见 E-106(静态读码 + 与基线里一只普通怪物的血条逐像素核对;
// 精英与首领的窗口只读了码,没有基线核对)。
namespace Dnf.Play
{
    /// <summary>
    /// 目标血条窗口要的一只怪物的资料:种类决定用哪个窗口(普通、精英、首领),头像与类别牌来自 .mob,血条总长由 <see cref="HpGauge"/> 算出。
    /// </summary>
    public sealed class MonsterPanelInfo
    {
        /// <summary>怪物种类:首领用首领窗口,精英(1、2)用精英窗口,其余用普通窗口。</summary>
        public MonsterKind Kind { get; }

        /// <summary>头像所在的图包名(形如 sprite/monster/monsterface.img,已经小写);没有为 null。</summary>
        public string FaceImage { get; }

        public int FaceIndex { get; }

        /// <summary>.mob [category] 块里的类别字符串(原样,按文件顺序),用来挑类别牌。</summary>
        public IReadOnlyList<string> Categories { get; }

        /// <summary>血条总长(像素,<see cref="HpGauge.Length"/>);不小于 0。</summary>
        public int GaugeLength { get; }

        public MonsterPanelInfo(MonsterKind kind, string faceImage, int faceIndex, IReadOnlyList<string> categories, int gaugeLength)
        {
            Kind = kind;
            FaceImage = faceImage;
            FaceIndex = faceIndex;
            Categories = categories ?? Array.Empty<string>();
            GaugeLength = gaugeLength < 0 ? 0 : gaugeLength;
        }

        /// <summary>
        /// 从 .mob 与游戏数据造:头像图包名是 [face image] 的路径前面加 sprite/ 再全小写(与技能图标同一规则);
        /// 血条总长按 <see cref="HpGauge.LengthFor"/>,层数取公共基础属性表,系数取难度表的 [adjust hp gauge]。
        /// </summary>
        public static MonsterPanelInfo From(MonsterDef def, MonsterKind kind, int level, int hpMax, GameData data)
        {
            if (def == null)
            {
                throw new ArgumentNullException(nameof(def));
            }
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            string face = def.FaceImage == null ? null : "sprite/" + def.FaceImage.ToLowerInvariant();
            int gauge = HpGauge.LengthFor(kind, hpMax, level, data.MonsterBaseTables[MonsterBaseTables.Common], data.HpGaugeFactors);
            return new MonsterPanelInfo(kind, face, def.FaceIndex, def.CategoryNames, gauge);
        }
    }

    /// <summary>
    /// 战斗画面左上角的"目标怪物血条"窗口(证据 E-106):头像、类别牌、"Lv 等级"、血条,用原版的图(<c>monster_hp.img</c> 等)画。
    /// <list type="bullet">
    /// <item>什么时候出现:主角打中一只怪物(<see cref="SetTarget"/>);打中同一只不重置;换一只怪物就整个换掉,历史清空。</item>
    /// <item>什么时候消失:目标被打死(生命 0)后满 1000 毫秒,并且掉血历史已经播完(没有闪光与缩短的段了)。目标死后头像上叠一个 X,每 250 毫秒闪一次。</item>
    /// <item>血条:总长 <see cref="MonsterPanelInfo.GaugeLength"/>(<see cref="HpGauge"/>),当前填充用单精度插值;
    /// 一次最多画 540 像素,更长的用叠层(5 种颜色轮换)表示。每一次生命下降,把"掉下去的那一段"记成历史:
    /// 先 500 毫秒变暗的红色加一层白闪(不透明度线性降到 0),再每 5 毫秒缩短 1 像素(总跨度 min(5 × 总长, 6000) 毫秒)直到消失。</item>
    /// <item>窗口按种类分三种:普通、精英、首领,各有自己的底框、端帽、血条起点与类别牌位置(常量见下)。首领的血条是横向滚动的 38 像素图块(每 50 毫秒挪 1 像素)。</item>
    /// </list>
    /// 没读通的部分(证据里标"推断"或"未决"):多段历史怎样接续(按"前一段的上沿接下一段"的链式画法)、层数取公共表哪一行(按怪物等级找)、
    /// 怪物名字(要文字表与中文字体,这里只写 "Lv 等级")。数字和文字用我们自己的 5×7 点阵,不是原版字体。
    /// </summary>
    public sealed class MonsterHpWindow
    {
        public const string WindowImage = "sprite/interface/newstyle/windows/monster_hp/monster_hp.img";
        public const string CategoryImage = "sprite/common/etc/category.img";
        public const string BaseImage = "sprite/interface/base.img";

        /// <summary>目标死后叠在头像上的 X(base.img 里的一帧)。</summary>
        public const int DeadMarkFrame = 91;

        /// <summary>monster_hp.img 里的帧:普通窗口的底框与端帽、精英的、首领的。</summary>
        public const int OrdinaryBodyFrame = 1, OrdinaryCapFrame = 8, ChampionBodyFrame = 9, ChampionCapFrame = 10, BossBodyFrame = 11, BossCapFrame = 12;

        /// <summary>血条的五种叠层颜色(第 n 层用第 n % 5 个)与白闪覆盖帧。</summary>
        public const int BarLayerFirstFrame = 2, BarLayerKinds = 5, FlashOverlayFrame = 7;

        /// <summary>首领血条的图块(五种)与它们的白闪覆盖帧。</summary>
        public const int BossTileFirstFrame = 13, BossFlashOverlayFrame = 18;

        /// <summary>变暗用的乘数(原版传 0xff969696:每个颜色通道乘 0x96 / 255)。</summary>
        public const int DarkMultiplier = 150;

        /// <summary>首领血条图块滚动:每 50 毫秒挪 1 像素。</summary>
        public const int BossScrollMs = 50;

        /// <summary>目标死后窗口还留多久(毫秒,原版判断"超过 999")。</summary>
        public const int DeadHoldMs = 1000;

        /// <summary>目标死后头像上 X 的闪烁周期(毫秒):偶数个周期画,奇数个周期不画。</summary>
        public const int DeadMarkBlinkMs = 250;

        /// <summary>三种窗口:左上角、血条起点离窗口左边的距离(也是端帽离血条末端的距离)、血条的纵坐标。</summary>
        private enum Variant
        {
            Ordinary,
            Champion,
            Boss,
        }

        private readonly struct Layout
        {
            public Layout(int x, int y, int barInset, int barY, int bodyFrame, int capFrame, int capDy, int tagsDx, int tagsDy)
            {
                X = x;
                Y = y;
                BarInset = barInset;
                BarY = barY;
                BodyFrame = bodyFrame;
                CapFrame = capFrame;
                CapDy = capDy;
                TagsDx = tagsDx;
                TagsDy = tagsDy;
            }

            public int X { get; }
            public int Y { get; }

            /// <summary>血条起点 = 窗口 X + 这个数;端帽的 X = 血条末端(窗口 X + 这个数 + 血条长)。</summary>
            public int BarInset { get; }

            public int BarY { get; }
            public int BodyFrame { get; }
            public int CapFrame { get; }

            /// <summary>端帽的纵坐标 = 窗口 Y + 这个数。</summary>
            public int CapDy { get; }

            /// <summary>类别牌起点 = (窗口 X + TagsDx, 窗口 Y + TagsDy)。</summary>
            public int TagsDx { get; }

            public int TagsDy { get; }
        }

        // 窗口位置:普通 (4, 98),精英与首领 (2, 96)(原版调用时传的实参);头像一律在 (6, 100)。
        private static readonly Layout OrdinaryLayout = new Layout(4, 98, 29, 115, OrdinaryBodyFrame, OrdinaryCapFrame, 15, 31, -1);
        private static readonly Layout ChampionLayout = new Layout(2, 96, 36, 115, ChampionBodyFrame, ChampionCapFrame, 16, 32, 0);
        private static readonly Layout BossLayout = new Layout(2, 96, 33, 99, BossBodyFrame, BossCapFrame, 0, 37, 8);

        public const int FaceX = 6, FaceY = 100;

        /// <summary>类别牌:名字 → category.img 的帧号(没有的类别不画)。取自客户端"类别序号 → 帧号"表与"名字 → 序号"的对照。</summary>
        private static readonly Dictionary<string, int> CategoryFrames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "human", 0 }, { "beast", 1 }, { "plant", 2 }, { "insect", 3 }, { "machine", 4 }, { "hybrid", 5 }, { "undead", 6 },
            { "devil", 7 }, { "spirit", 8 }, { "dragon", 9 }, { "oneside move", 10 }, { "human armor", 11 }, { "fort armor", 12 }, { "angel", 14 },
        };

        private readonly struct LostSegment
        {
            public LostSegment(int value, double timeMs)
            {
                Value = value;
                TimeMs = timeMs;
            }

            /// <summary>掉血之前的填充像素数(这一段的上沿)。</summary>
            public int Value { get; }

            public double TimeMs { get; }
        }

        private readonly struct Segment
        {
            public Segment(int from, int to, int mode, int age)
            {
                From = from;
                To = to;
                Mode = mode;
                Age = age;
            }

            public int From { get; }
            public int To { get; }

            /// <summary>1 = 变暗加白闪,2 = 只变暗。</summary>
            public int Mode { get; }

            public int Age { get; }
        }

        private readonly ISpriteSource _sprites;
        private readonly OverlayArt _art;
        private readonly Dictionary<string, SpriteFrame> _frames = new Dictionary<string, SpriteFrame>();
        private readonly Dictionary<string, SpriteFrame> _crops = new Dictionary<string, SpriteFrame>();
        private readonly List<LostSegment> _history = new List<LostSegment>();
        private readonly List<Segment> _segments = new List<Segment>();

        private MonsterActor _target;
        private bool _visible;
        private bool _dead;
        private double _deadSinceMs;
        private double _nowMs;
        private int _gauge;
        private int _value;
        private int _prev;

        private MonsterHpWindow(ISpriteSource sprites, OverlayArt art)
        {
            _sprites = sprites;
            _art = art;
        }

        /// <summary>
        /// 造窗口;图包里没有 monster_hp.img 的普通底框与血条叠层(最基本的两帧)就返回 null,调用方退回占位的色块血条。
        /// <paramref name="art"/> 用来排 "Lv" 文字(与会话共用同一个缓存)。
        /// </summary>
        public static MonsterHpWindow For(ISpriteSource sprites, OverlayArt art)
        {
            if (sprites == null || art == null)
            {
                return null;
            }
            return sprites.TryGetFrame(WindowImage, OrdinaryBodyFrame, out SpriteFrame body) && body != null
                && sprites.TryGetFrame(WindowImage, BarLayerFirstFrame, out SpriteFrame bar) && bar != null
                ? new MonsterHpWindow(sprites, art)
                : null;
        }

        /// <summary>现在的目标(没有为 null)。目标死后窗口消失了目标还在,直到换一个。</summary>
        public MonsterActor Target => _target;

        public bool Visible => _visible && _target != null;

        /// <summary>血条当前的填充像素数(经过 <see cref="Update"/> 才变)。</summary>
        public int Value => _value;

        /// <summary>血条总长(像素)。</summary>
        public int Gauge => _gauge;

        /// <summary>还没播完的掉血历史段数。</summary>
        public int HistoryCount => _history.Count;

        /// <summary>目标死后是不是已经进入"死亡"状态(头像叠 X)。</summary>
        public bool Dead => _dead;

        /// <summary>
        /// 主角打中怪物时调用。同一只不重置;换一只就清空历史、显示出来,血条的初始值取"这一刻"的生命
        /// (所以要在这一击的伤害结算<b>之前</b>调,这一击才会产生掉血的闪光)。<paramref name="monster"/> 为 null 只是把窗口隐藏。
        /// </summary>
        public void SetTarget(MonsterActor monster, double nowMs)
        {
            if (monster == null)
            {
                _visible = false;
                _target = null;
                return;
            }
            if (ReferenceEquals(_target, monster))
            {
                return;
            }
            _target = monster;
            _visible = true;
            _nowMs = nowMs;
            _gauge = monster.Panel != null ? monster.Panel.GaugeLength : HpGauge.Length(monster.HpMax, HpGauge.DefaultLayers, HpGauge.DefaultScale);
            _history.Clear();
            _segments.Clear();
            // 死亡标志在换目标时也只在"变了"的时候翻转并重置计时(客户端设置目标函数里与每帧路由是同一段比较);
            // 上一只死了、换到另一只也已经死了的话,计时接着走。
            bool dead = monster.Hp <= 0;
            if (dead != _dead)
            {
                _dead = dead;
                _deadSinceMs = nowMs;
            }
            _value = HpGauge.Fill(_gauge, monster.Hp, monster.HpMax);
            _prev = _value;
        }

        /// <summary>每一步结束时调用:更新死亡状态、血条填充与掉血历史,算好这一帧要画的段;死后满 1000 毫秒且历史播完时把窗口隐藏。</summary>
        public void Update(double nowMs)
        {
            _nowMs = nowMs;
            _segments.Clear();
            if (!_visible || _target == null)
            {
                return;
            }
            bool dead = _target.Hp <= 0;
            if (dead != _dead)
            {
                _dead = dead;
                _deadSinceMs = nowMs;
            }
            _value = HpGauge.Fill(_gauge, _target.Hp, _target.HpMax);
            UpdateHistory(nowMs);
            PlanSegments(nowMs);
            if (_dead && nowMs - _deadSinceMs >= DeadHoldMs && _history.Count == 0)
            {
                _visible = false;
            }
        }

        /// <summary>
        /// 生命下降:把下降前的值记成一段历史(最新的在最前面),上一次的值改成现在的;
        /// 生命回升:把上沿不高于现在值的历史段都去掉。
        /// </summary>
        private void UpdateHistory(double nowMs)
        {
            if (_value < _prev)
            {
                _history.Insert(0, new LostSegment(_prev, nowMs));
                _prev = _value;
                return;
            }
            if (_value > _prev)
            {
                while (_history.Count > 0 && _history[0].Value <= _value)
                {
                    _history.RemoveAt(0);
                }
                _prev = _value;
            }
        }

        /// <summary>
        /// 算历史段怎么画。从最新的开始,还在白闪期(不到 500 毫秒)的段按"上一段的上沿 → 这一段的上沿"依次画成 变暗 + 白闪;
        /// 遇到第一个闪完的段,它的上沿按 <see cref="HpGauge.Shrunk"/> 往下缩,还有剩余就画成只变暗并停下;
        /// 缩没了就把它和更老的段都删掉。
        /// </summary>
        private void PlanSegments(double nowMs)
        {
            int from = _value;
            int i = 0;
            for (; i < _history.Count; i++)
            {
                int age = (int)(nowMs - _history[i].TimeMs);
                if (age >= HpGauge.FlashMs)
                {
                    break;
                }
                _segments.Add(new Segment(from, _history[i].Value, 1, age));
                from = _history[i].Value;
            }
            if (i < _history.Count)
            {
                int age = (int)(nowMs - _history[i].TimeMs);
                int top = _history[i].Value - HpGauge.Shrunk(_gauge, age - HpGauge.FlashMs);
                if (from < top)
                {
                    _segments.Add(new Segment(from, top, 2, 0));
                }
                else
                {
                    _history.RemoveRange(i, _history.Count - i);
                }
            }
        }

        /// <summary>把窗口加进精灵列表(叠加层,按画的先后顺序);没有目标或已隐藏时什么也不加。</summary>
        public void Build(List<SceneSprite> into, ref int order)
        {
            if (!Visible)
            {
                return;
            }
            MonsterPanelInfo info = _target.Panel;
            Variant variant = VariantOf(info);
            Layout layout = variant == Variant.Boss ? BossLayout : variant == Variant.Champion ? ChampionLayout : OrdinaryLayout;
            int cap = Math.Min(_gauge, HpGauge.LayerWidth);

            // 底框(只画到血条末端再加一段)、端帽
            SpriteFrame body = Frame(WindowImage, layout.BodyFrame);
            if (body != null)
            {
                SpriteFrame clipped = Crop(WindowImage, layout.BodyFrame, 0, Math.Min(body.Width, cap + layout.BarInset), false);
                Add(into, ref order, clipped, layout.X, layout.Y, "mhp-body", 255);
            }
            SpriteFrame capFrame = Frame(WindowImage, layout.CapFrame);
            if (capFrame != null)
            {
                Add(into, ref order, capFrame, layout.X + layout.BarInset + cap, layout.Y + layout.CapDy, "mhp-cap", 255);
            }

            // 头像,目标死后叠 X(每 250 毫秒闪一次)
            if (info != null && info.FaceImage != null)
            {
                Add(into, ref order, Frame(info.FaceImage, info.FaceIndex), FaceX, FaceY, "mhp-face", 255);
            }
            if (_dead && ((long)((_nowMs - _deadSinceMs) / DeadMarkBlinkMs) & 1) == 0)
            {
                Add(into, ref order, Frame(BaseImage, DeadMarkFrame), FaceX, FaceY, "mhp-dead", 255);
            }

            // 血条:当前值,再是掉血历史
            int barX = layout.X + layout.BarInset;
            AddSpan(into, ref order, variant, barX, layout.BarY, 0, _value, 0, 0);
            foreach (Segment s in _segments)
            {
                AddSpan(into, ref order, variant, barX, layout.BarY, s.From, s.To, s.Mode, s.Age);
            }

            // 类别牌与文字
            int tagX = layout.X + layout.TagsDx;
            int tagY = layout.Y + layout.TagsDy;
            if (info != null)
            {
                foreach (string name in info.Categories)
                {
                    if (!TryCategoryFrame(name, out int frameIndex))
                    {
                        continue;
                    }
                    // 牌子画完,下一块的起点右移 1 + 这块的宽;帧取不到或是宽 0 的空帧(原版有一个类别指向空帧)时宽按 0 算
                    SpriteFrame tag = Frame(CategoryImage, frameIndex);
                    if (tag != null && tag.Width > 0)
                    {
                        Add(into, ref order, tag, tagX, tagY, "mhp-tag", 255);
                    }
                    tagX += 1 + (tag == null ? 0 : tag.Width);
                }
            }
            SpriteFrame text = _art.Text("Lv" + _target.Level.ToString(CultureInfo.InvariantCulture), 1, 0xFFFFFFu);
            if (text != null)
            {
                // 文字的原点是 (牌子末端 + 2, 牌子行 + 3);我们的字高 7、带 1 像素边,放进那一行的中间
                into.Add(OverlayArt.Place(text, tagX + 2 - 1, tagY + 3 + 2 - 1, order++, "mhp-text"));
            }
        }

        /// <summary>类别字符串(形如 "[human]" 或 "human")有没有类别牌;有就给出 category.img 的帧号。</summary>
        public static bool TryCategoryFrame(string name, out int frame)
        {
            frame = -1;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string key = name.Trim();
            if (key.Length >= 2 && key[0] == '[' && key[key.Length - 1] == ']')
            {
                key = key.Substring(1, key.Length - 2).Trim();
            }
            return CategoryFrames.TryGetValue(key, out frame);
        }

        private static Variant VariantOf(MonsterPanelInfo info)
        {
            if (info == null)
            {
                return Variant.Ordinary;
            }
            switch (info.Kind)
            {
                case MonsterKind.Boss:
                    return Variant.Boss;
                case MonsterKind.CommonChampion:
                case MonsterKind.SuperChampion:
                    return Variant.Champion;
                default:
                    return Variant.Ordinary;
            }
        }

        // ------------------------------------------------------------------ 血条的一段

        /// <summary>
        /// 画血条上 [<paramref name="from"/>, <paramref name="to"/>) 这一段(像素,从血条起点算)。血条一层宽 <see cref="HpGauge.LayerWidth"/>:
        /// 只需要画最上面两层——跨层时先画下一层(只露出没被上层盖住的那部分),再画最上层。
        /// <paramref name="mode"/>:0 正常颜色;1 变暗再按 <paramref name="age"/> 盖一层白闪;2 只变暗。
        /// </summary>
        private void AddSpan(List<SceneSprite> into, ref int order, Variant variant, int barX, int barY, int from, int to, int mode, int age)
        {
            if (to <= from)
            {
                return;
            }
            int width = HpGauge.LayerWidth;
            int fromLayer = Math.Max(0, (from - 1) / width);
            int fromRem = (from - 1) % width + 1;
            int toLayer = Math.Max(0, (to - 1) / width);
            int toRem = (to - 1) % width + 1;
            int passes = fromLayer < toLayer ? 2 : 1;
            for (int pass = 0; pass < passes; pass++)
            {
                int left = fromRem;
                int right = toRem;
                int layer = fromLayer;
                if (fromLayer < toLayer)
                {
                    if (pass == 0)
                    {
                        if (fromLayer == toLayer - 1)
                        {
                            right = width;
                        }
                        else
                        {
                            left = toRem;
                            right = width;
                            layer = toLayer - 1;
                        }
                    }
                    else
                    {
                        left = 0;
                        layer = toLayer;
                    }
                }
                if (left < right)
                {
                    if (variant == Variant.Boss)
                    {
                        AddBossTiles(into, ref order, barX, barY, layer, left, right, mode, age);
                    }
                    else
                    {
                        AddBarPiece(into, ref order, barX, barY, layer, left, right, mode, age);
                    }
                }
            }
        }

        /// <summary>普通与精英的血条:取第 layer % 5 种叠层图的 [left, right) 列,摆在血条起点右边 left 像素处。</summary>
        private void AddBarPiece(List<SceneSprite> into, ref int order, int barX, int barY, int layer, int left, int right, int mode, int age)
        {
            int frameIndex = BarLayerFirstFrame + layer % BarLayerKinds;
            SpriteFrame whole = Frame(WindowImage, frameIndex);
            if (whole == null)
            {
                return;
            }
            right = Math.Min(right, whole.Width);
            if (left >= right)
            {
                return;
            }
            Add(into, ref order, Crop(WindowImage, frameIndex, left, right, mode != 0), barX + left, barY, mode == 0 ? "mhp-bar" : "mhp-lost", 255);
            if (mode == 1 && age < HpGauge.FlashMs)
            {
                SpriteFrame overlay = Frame(WindowImage, FlashOverlayFrame);
                if (overlay != null)
                {
                    int r = Math.Min(right, overlay.Width);
                    if (left < r)
                    {
                        Add(into, ref order, Crop(WindowImage, FlashOverlayFrame, left, r, false), barX + left, barY, "mhp-flash", HpGauge.FlashAlpha(age));
                    }
                }
            }
        }

        /// <summary>首领的血条:38 像素宽的图块从血条起点往左偏 (时间 / 50) % 38 像素处开始铺满,按 [left, right) 裁。</summary>
        private void AddBossTiles(List<SceneSprite> into, ref int order, int barX, int barY, int layer, int left, int right, int mode, int age)
        {
            int frameIndex = BossTileFirstFrame + layer % BarLayerKinds;
            SpriteFrame tile = Frame(WindowImage, frameIndex);
            if (tile == null || tile.Width < 1)
            {
                return;
            }
            int scroll = (int)((long)(_nowMs / BossScrollMs) % tile.Width);
            int clipLeft = barX + left;
            int clipRight = barX + right;
            for (int tileX = barX - scroll; tileX < clipRight; tileX += tile.Width)
            {
                int a = Math.Max(clipLeft, tileX) - tileX;
                int b = Math.Min(clipRight, tileX + tile.Width) - tileX;
                if (a >= b)
                {
                    continue;
                }
                int x = tileX + a;
                Add(into, ref order, Crop(WindowImage, frameIndex, a, b, mode != 0), x, barY, mode == 0 ? "mhp-bar" : "mhp-lost", 255);
                if (mode == 1 && age < HpGauge.FlashMs)
                {
                    SpriteFrame overlay = Frame(WindowImage, BossFlashOverlayFrame);
                    if (overlay != null && a < overlay.Width)
                    {
                        Add(into, ref order, Crop(WindowImage, BossFlashOverlayFrame, a, Math.Min(b, overlay.Width), false), x, barY, "mhp-flash", HpGauge.FlashAlpha(age));
                    }
                }
            }
        }

        // ------------------------------------------------------------------ 取帧与裁剪

        private static void Add(List<SceneSprite> into, ref int order, SpriteFrame frame, int x, int y, string tag, int alpha)
        {
            if (frame == null)
            {
                return;
            }
            into.Add(OverlayArt.Place(frame, x, y, order++, tag, alpha));
        }

        private SpriteFrame Frame(string image, int index)
        {
            if (image == null || index < 0)
            {
                return null;
            }
            string key = image + "#" + index.ToString(CultureInfo.InvariantCulture);
            if (_frames.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            _sprites.TryGetFrame(image, index, out SpriteFrame sprite);
            _frames[key] = sprite;
            return sprite;
        }

        /// <summary>
        /// 把图 <paramref name="image"/> 的第 <paramref name="index"/> 帧裁出 [left, right) 列(像素块位置由调用方按列号加上);
        /// <paramref name="dark"/> 为真时每个颜色通道乘 <see cref="DarkMultiplier"/> / 255(原版的 0xff969696 调色)。同样的参数返回同一个对象。
        /// </summary>
        private SpriteFrame Crop(string image, int index, int left, int right, bool dark)
        {
            SpriteFrame source = Frame(image, index);
            if (source == null)
            {
                return null;
            }
            left = Math.Max(0, left);
            right = Math.Min(source.Width, right);
            if (left >= right)
            {
                return null;
            }
            if (left == 0 && right == source.Width && !dark)
            {
                return source;
            }
            string key = image + "#" + index.ToString(CultureInfo.InvariantCulture) + ":" + left.ToString(CultureInfo.InvariantCulture) + "-"
                + right.ToString(CultureInfo.InvariantCulture) + (dark ? "d" : "n");
            if (_crops.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            if (_crops.Count >= 1024)
            {
                _crops.Clear();
            }
            int width = right - left;
            var rgba = new byte[width * source.Height * 4];
            for (int row = 0; row < source.Height; row++)
            {
                Buffer.BlockCopy(source.Rgba, (row * source.Width + left) * 4, rgba, row * width * 4, width * 4);
            }
            if (dark)
            {
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    rgba[i] = Darken(rgba[i]);
                    rgba[i + 1] = Darken(rgba[i + 1]);
                    rgba[i + 2] = Darken(rgba[i + 2]);
                }
            }
            var frame = new SpriteFrame(width, source.Height, source.OffsetX + left, source.OffsetY, source.CanvasWidth, source.CanvasHeight, rgba);
            _crops[key] = frame;
            return frame;
        }

        private static byte Darken(byte channel)
        {
            return (byte)((channel * DarkMultiplier + 127) / 255);
        }
    }
}
