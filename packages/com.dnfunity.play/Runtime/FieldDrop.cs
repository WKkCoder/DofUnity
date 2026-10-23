using System;
using Dnf.Game;
using Dnf.Render;

// 证据:E-116(客户端 0x007ad760 是"地上的金币"的初始化:按金额选 Item/FieldImage.img 的帧与声音名;
// 0x006e6870 是它的构造函数,0x007c19d0 在"加入地面物品"时造它)。落地的抛物线、拾取范围、弹跳都没有读到客户端代码,下面的数值全是推断。
// 地上的物品(E-117)与金币共用这套抛出、落地与拾取:图是物品脚本 [field image] 写的那一帧,声音取 [move wav](拾取时放,推断)。
namespace Dnf.Play
{
    /// <summary>
    /// 地面上的一堆金币。
    /// <para>
    /// 证据(客户端 <c>0x007ad760</c>):金额 &lt; 100 用图包 <c>Item/FieldImage.img</c> 的第 26 帧、声音名 <c>GOLD_DROP1</c>;
    /// 金额 &lt; 500 用第 27 帧、&lt; 1000 用第 25 帧、&lt; 1500 用第 36 帧、再多用第 37 帧,声音名都是 <c>GOLD_DROP2</c>。
    /// 这个声音名放在哪一刻没有查明(落地或拾取):它们在 audio.xml 里的文件在 <c>sounds\stak\material</c> 下(物品"移动"音效那一类),这里按"拾取时播放"处理。
    /// </para>
    /// <para>
    /// <b>推断</b>(没有读到客户端的物理与拾取代码):从怪物身上以向上的初速抛出、水平落点在怪物周围随机、受重力落地、落地后小弹一下停住;
    /// 落地后主角走到它附近(进深与横向各一个范围)就拾取,拾取要求主角离地不高;不会消失(原版有没有时限没有读到)。
    /// </para>
    /// <para>
    /// 地上也可以是一件物品(<see cref="Item"/> 不为 null,证据 E-117):画的是物品脚本 <c>[field image]</c> 的图,声音是它的 <c>[move wav]</c>,
    /// 物理与拾取范围同金币;<b>推断</b>:原版的物品要按键才捡(这里与金币一样走到就捡),背包放不下时留在地上(<see cref="Blocked"/>)。
    /// </para>
    /// </summary>
    public sealed class FieldDrop
    {
        /// <summary>地面物品的图包(客户端 <c>Item/FieldImage.img</c> 的小写路径)。</summary>
        public const string ImagePath = "sprite/item/fieldimage.img";

        /// <summary>金额低于 100 时的帧。</summary>
        public const int FrameBelow100 = 26;

        /// <summary>金额在 100 到 499 时的帧。</summary>
        public const int FrameBelow500 = 27;

        /// <summary>金额在 500 到 999 时的帧。</summary>
        public const int FrameBelow1000 = 25;

        /// <summary>金额在 1000 到 1499 时的帧。</summary>
        public const int FrameBelow1500 = 36;

        /// <summary>金额在 1500 以上时的帧。</summary>
        public const int FrameAbove = 37;

        /// <summary>金额低于 100 的声音名;其余用 <see cref="LargeSound"/>。</summary>
        public const string SmallSound = "GOLD_DROP1";

        public const string LargeSound = "GOLD_DROP2";

        // ----- 以下全是推断
        /// <summary>重力(像素/毫秒²;推断)。</summary>
        public const double Gravity = 0.0016;

        /// <summary>抛出时向上的初速(像素/毫秒;推断)。</summary>
        public const double LaunchSpeed = 0.42;

        /// <summary>怪物身上的出发高度(像素;推断:大约腰的位置)。</summary>
        public const double StartHeight = 40.0;

        /// <summary>水平落点相对怪物的最大横向偏移(像素;推断)。</summary>
        public const int ScatterX = 70;

        /// <summary>落点相对怪物的最大进深偏移(像素;推断)。</summary>
        public const int ScatterY = 14;

        /// <summary>第一次落地后弹起保留的速度比例(推断)。</summary>
        public const double BounceKeep = 0.35;

        /// <summary>弹起速度小于它就不再弹(像素/毫秒;推断)。</summary>
        public const double MinBounceSpeed = 0.07;

        /// <summary>拾取范围:横向半宽(像素;推断)。</summary>
        public const int PickupHalfWidth = 44;

        /// <summary>拾取范围:进深半宽(像素;推断)。</summary>
        public const int PickupHalfDepth = 18;

        /// <summary>主角离地高度不超过它才能拾取(像素;推断)。</summary>
        public const int PickupMaxHeroZ = 30;

        private double _vx;
        private double _vy;
        private double _vz;
        private readonly bool _hasTarget;
        private readonly double _targetX;
        private readonly double _targetY;

        /// <param name="landX">给了落点,第一次触地时位置就取它(免得浮点误差让像素位置差 1);不给则按速度积分的结果。</param>
        /// <param name="landY">同上,进深。</param>
        /// <param name="item">地上是一件物品时给它(<paramref name="amount"/> 此时按 1 件);金币堆不给。</param>
        public FieldDrop(int id, int amount, double x, double y, double z, double vx, double vy, double vz, double? landX = null, double? landY = null,
            ItemInfo item = null)
        {
            Id = id;
            Amount = amount;
            Item = item;
            X = x;
            Y = y;
            Z = z;
            _vx = vx;
            _vy = vy;
            _vz = vz;
            _hasTarget = landX.HasValue && landY.HasValue;
            _targetX = landX ?? 0.0;
            _targetY = landY ?? 0.0;
        }

        /// <summary>这一堆的序号(一个房间里从 1 数起,日志与测试用)。</summary>
        public int Id { get; }

        /// <summary>金额(物品时是 1,表示一件)。</summary>
        public int Amount { get; }

        /// <summary>地上的物品(证据 E-117);金币堆是 null。</summary>
        public ItemInfo Item { get; }

        /// <summary>是物品不是金币。</summary>
        public bool IsItem => Item != null;

        /// <summary>主角够得着了,但背包放不下,留在地上(<see cref="RoomCombatSession"/> 置位,用来只提示一次)。</summary>
        public bool Blocked { get; set; }

        public double X { get; private set; }

        /// <summary>进深(世界 y)。</summary>
        public double Y { get; private set; }

        /// <summary>离地高度。</summary>
        public double Z { get; private set; }

        /// <summary>第一次触地之后为 true(之前在空中,不能拾取)。</summary>
        public bool Landed { get; private set; }

        /// <summary>弹完停住了。</summary>
        public bool Rested { get; private set; }

        /// <summary>出生以来的毫秒数。</summary>
        public double AgeMs { get; private set; }

        /// <summary>按金额选的图包帧(客户端 <c>0x007ad760</c> 的分档)。</summary>
        public static int FrameFor(int amount)
        {
            if (amount < 100)
            {
                return FrameBelow100;
            }
            if (amount < 500)
            {
                return FrameBelow500;
            }
            if (amount < 1000)
            {
                return FrameBelow1000;
            }
            return amount < 1500 ? FrameBelow1500 : FrameAbove;
        }

        /// <summary>按金额选的声音名(见类说明:放在哪一刻是推断)。</summary>
        public static string SoundFor(int amount)
        {
            return amount < 100 ? SmallSound : LargeSound;
        }

        /// <summary>拾取时放的声音名:金币按金额(<see cref="SoundFor"/>),物品用脚本的 <c>[move wav]</c>(可能是 null)。</summary>
        public string PickupSound => Item != null ? Item.MoveSound : SoundFor(Amount);

        /// <summary>
        /// 从 (<paramref name="x"/>, <paramref name="y"/>) 抛出一堆:水平偏移在 ±<see cref="ScatterX"/> 与 ±<see cref="ScatterY"/> 里取,
        /// 落点不能站人(<paramref name="canStand"/> 返回 false)时把偏移折半再试,最后退回原地。水平速度按"刚好在落地那一刻到落点"算。
        /// </summary>
        public static FieldDrop Launch(int id, int amount, double x, double y, IDropRandom random, Func<double, double, bool> canStand = null)
        {
            return LaunchCore(id, amount, null, x, y, random, canStand);
        }

        /// <summary>抛出一件物品(证据 E-117):与 <see cref="Launch"/> 同一套落点与物理,<paramref name="item"/> 不能为 null。</summary>
        public static FieldDrop LaunchItem(int id, ItemInfo item, double x, double y, IDropRandom random, Func<double, double, bool> canStand = null)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }
            return LaunchCore(id, 1, item, x, y, random, canStand);
        }

        private static FieldDrop LaunchCore(int id, int amount, ItemInfo item, double x, double y, IDropRandom random, Func<double, double, bool> canStand)
        {
            double dx = random.Range(-ScatterX, ScatterX);
            double dy = random.Range(-ScatterY, ScatterY);
            if (canStand != null)
            {
                for (int i = 0; i < 6 && !canStand(x + dx, y + dy); i++)
                {
                    dx /= 2.0;
                    dy /= 2.0;
                }
                if (!canStand(x + dx, y + dy))
                {
                    dx = 0;
                    dy = 0;
                }
            }
            double flight = FlightMs(StartHeight, LaunchSpeed);
            return new FieldDrop(id, amount, x, y, StartHeight, dx / flight, dy / flight, LaunchSpeed, x + dx, y + dy, item);
        }

        /// <summary>从高度 <paramref name="z"/> 以竖直速度 <paramref name="vz"/>(向上为正)出发,多少毫秒后落到地面(高度 0;已在地面且不往上走是 0)。</summary>
        public static double FlightMs(double z, double vz)
        {
            double t = (vz + Math.Sqrt(Math.Max(0.0, vz * vz + 2.0 * Gravity * Math.Max(0.0, z)))) / Gravity;
            return t < 0.0 ? 0.0 : t;
        }

        /// <summary>
        /// 推进 <paramref name="deltaMs"/> 毫秒。空中走匀加速的精确解:一步到落地时刻为止(水平位移也只算到落地那一刻),
        /// 落地后弹起速度乘 <see cref="BounceKeep"/>,小于 <see cref="MinBounceSpeed"/> 就停住;结果与怎么切分时间无关。
        /// </summary>
        public void Advance(double deltaMs)
        {
            if (deltaMs <= 0.0)
            {
                return;
            }
            AgeMs += deltaMs;
            double remaining = deltaMs;
            for (int guard = 0; remaining > 1e-9 && !Rested && guard < 64; guard++)
            {
                double toLand = FlightMs(Z, _vz);
                if (toLand > remaining)
                {
                    if (!Landed)
                    {
                        X += _vx * remaining;
                        Y += _vy * remaining;
                    }
                    Z += _vz * remaining - 0.5 * Gravity * remaining * remaining;
                    _vz -= Gravity * remaining;
                    return;
                }
                if (!Landed)
                {
                    X += _vx * toLand;
                    Y += _vy * toLand;
                }
                double impact = _vz - Gravity * toLand;      // 落地那一刻的速度(向下,不大于 0)
                remaining -= toLand;
                Z = 0.0;
                if (!Landed && _hasTarget)
                {
                    X = _targetX;
                    Y = _targetY;
                }
                Landed = true;
                _vx = 0.0;
                _vy = 0.0;
                double bounce = -impact * BounceKeep;
                if (bounce < MinBounceSpeed)
                {
                    _vz = 0.0;
                    Rested = true;
                }
                else
                {
                    _vz = bounce;
                }
            }
        }

        /// <summary>主角在 (<paramref name="heroX"/>, <paramref name="heroY"/>, <paramref name="heroZ"/>) 时够得着吗(已落地、在范围内、主角离地不高)。</summary>
        public bool IsInReach(double heroX, double heroY, double heroZ)
        {
            return Landed
                && Math.Abs(X - heroX) <= PickupHalfWidth
                && Math.Abs(Y - heroY) <= PickupHalfDepth
                && heroZ <= PickupMaxHeroZ;
        }

        /// <summary>此刻的精灵;图包里没有这一帧时返回 null。画在同进深的角色后面一点(排序键减 1)。</summary>
        public SceneSprite Sprite(ISpriteSource sprites, TownProjection projection)
        {
            string path = Item != null ? Item.FieldImageKey : ImagePath;
            int frame = Item != null ? Item.FieldFrame : FrameFor(Amount);
            if (path == null || frame < 0 || sprites == null || !sprites.TryGetFrame(path, frame, out SpriteFrame f) || f == null || f.Width == 0 || f.Height == 0)
            {
                return null;
            }
            projection.TopLeft((int)Math.Floor(X), (int)Math.Floor(Y), (int)Math.Round(Z), 0, 0, f.OffsetX, f.OffsetY, f.Width, false, out int sx, out int sy);
            return new SceneSprite(f, sx, sy, (int)Math.Floor(Y) - 1, -1, path, frame);
        }
    }
}
