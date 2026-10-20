using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Play
{
    /// <summary>一击普攻带的静态数据(取自角色 .chr 的 [attack info] 指向的 .atk;取不到时全是默认值)。</summary>
    public readonly struct HeroStrikeInfo
    {
        /// <summary>[damage bonus]:这一击相对基础伤害的加成百分数(可以是负数)。</summary>
        public int DamageBonus { get; }

        /// <summary>[push aside]:水平击退的像素数(单位按像素理解,没有核对)。</summary>
        public int PushAside { get; }

        /// <summary>[damage reaction] 是倒地。</summary>
        public bool KnockDown { get; }

        /// <summary>
        /// 技能伤害的两个数(技能 .skl 的 [level info] 取当前等级的第 0、1 列,证据 E-076):按攻击力百分之几、外加固定值。
        /// 两个都是 0 表示不是技能伤害(普攻按 <see cref="DamageBonus"/> 算)。
        /// </summary>
        public int SkillRatePercent { get; }

        public int SkillFixed { get; }

        /// <summary>伤害按技能的两个数算(<see cref="SkillRatePercent"/>、<see cref="SkillFixed"/>)。</summary>
        public bool IsSkillDamage => SkillRatePercent != 0 || SkillFixed != 0;

        // 内部存"值 + 1":default(HeroStrikeInfo) 里整数是 0,正好对应"没写"(-1)
        private readonly int _hitValuePlusOne;

        /// <summary>[hit info] 的整数(命中停顿毫秒,证据 E-077);没写是 -1,表示取武器表。</summary>
        public int HitValue => _hitValuePlusOne - 1;

        /// <summary>[is use weapon hit delay rate]:为真时停顿 = 武器的停顿 × <see cref="HitValue"/>。</summary>
        public bool UseWeaponHitDelayRate { get; }

        private readonly float _hitScale;
        private readonly bool _hasHitScale;

        /// <summary>[hit info] 的浮点项(命中延迟倍率,受击硬直要乘它,证据 E-080);没写是 -1,表示取武器表。</summary>
        public float HitScale => _hasHitScale ? _hitScale : -1f;

        /// <summary>[knuck back] 的类型(0 普通、1 击退、2 短击退、3 按像素、-1 不后退;证据 E-080)。</summary>
        public int KnockBackType { get; }

        /// <summary>命中模式为 [burn](客户端的 2):受击不击退(证据 E-080)。</summary>
        public bool NoKnockBack { get; }

        /// <summary>[force hit stun time]:非 0 时直接当作受击硬直(毫秒)。</summary>
        public int ForceHitStunMs { get; }

        /// <summary>[attack type] 是魔法:受击硬直的倍率再乘 0.7(证据 E-080)。</summary>
        public bool Magical { get; }

        /// <summary>[lift up]:倒地的抬升量(类型 1 到 3 当初速、按体重缩放,类型 0 当高度;证据 E-079、E-083)。</summary>
        public int LiftUp { get; }

        /// <summary>[bounce] 非 0:倒地时先砸地再弹起飞第二次(证据 E-079、E-083)。</summary>
        public bool Bounce { get; }

        /// <summary>[ignore weight] 非 0:倒地的击飞不受怪物体重影响(类型 0 或 2;证据 E-079)。</summary>
        public bool IgnoreWeight { get; }

        /// <summary>[damage reaction] 是普通受击(1,或按 1 处理的 3):怪物在空中或已在倒地中时也走倒地分支(证据 E-079 第 6' 步)。</summary>
        public bool ReactionDamage { get; }

        public HeroStrikeInfo(int damageBonus, int pushAside, bool knockDown, int skillRatePercent = 0, int skillFixed = 0,
            int hitValue = -1, bool useWeaponHitDelayRate = false, float hitScale = -1f, int knockBackType = 0, bool noKnockBack = false,
            int forceHitStunMs = 0, bool magical = false, int liftUp = 0, bool bounce = false, bool ignoreWeight = false, bool reactionDamage = false)
        {
            DamageBonus = damageBonus;
            PushAside = pushAside;
            KnockDown = knockDown;
            SkillRatePercent = skillRatePercent;
            SkillFixed = skillFixed;
            _hitValuePlusOne = unchecked(hitValue + 1);
            UseWeaponHitDelayRate = useWeaponHitDelayRate;
            _hitScale = hitScale;
            _hasHitScale = true;
            KnockBackType = knockBackType;
            NoKnockBack = noKnockBack;
            ForceHitStunMs = forceHitStunMs;
            Magical = magical;
            LiftUp = liftUp;
            Bounce = bounce;
            IgnoreWeight = ignoreWeight;
            ReactionDamage = reactionDamage;
        }

        /// <summary>读一份 .atk 里命中要用的字段;路径为 null、读不到时全是默认值。</summary>
        public static HeroStrikeInfo Read(GameData data, string atkPath)
        {
            AttackInfoData info = atkPath == null || data == null ? null : data.GetAttackInfo(atkPath)?.Data;
            return info == null
                ? new HeroStrikeInfo(0, 0, false)
                : new HeroStrikeInfo(info.DamageBonus, info.PushAside, info.Reaction == DamageReaction.Down, 0, 0, info.HitValue, info.UseWeaponHitDelayRate,
                    info.HitScale, info.KnockBackType, info.HitType == HitKind.Burn, info.ForceHitStunTime, info.AttackType == AttackKind.Magic,
                    info.LiftUp, info.Bounce != 0, info.IgnoreWeight != 0,
                    info.Reaction == DamageReaction.Damage || info.Reaction == DamageReaction.DamageExceptHuman);
        }

        /// <summary>同一份攻击信息,换上技能伤害的两个数。</summary>
        public HeroStrikeInfo WithSkillDamage(int ratePercent, int fixedDamage)
        {
            return new HeroStrikeInfo(DamageBonus, PushAside, KnockDown, ratePercent, fixedDamage, HitValue, UseWeaponHitDelayRate,
                HitScale, KnockBackType, NoKnockBack, ForceHitStunMs, Magical, LiftUp, Bounce, IgnoreWeight, ReactionDamage);
        }
    }

    /// <summary>
    /// 占位的伤害数:真正的伤害公式(<see cref="DamageFormula"/>)需要完整的角色面板,还没有接进来(P5.M3)。
    /// 基础值 25.3 是照基线录像标的:一级哥布林挨第一击(加成 −15%)时屏幕上出现 21 与 22,即 25.3 × 0.85 = 21.5 上下取整;
    /// 之后每一击按 [damage bonus] 缩放,小数部分按概率进位(同一份基础值给出 21 与 22 两种读数)。证据 E-069。
    /// </summary>
    public static class PlaceholderDamage
    {
        public const double BaseHit = 25.3;

        public static int Roll(int damageBonusPercent, Random random)
        {
            return Round(BaseHit * (100 + damageBonusPercent) / 100.0, random);
        }

        /// <summary>
        /// 技能的基础值:照基线里一级鬼斩打在一级哥布林身上显示 146 标的(把 .skl 一级的两个数代入 <see cref="RollSkill"/> 的式子反推,
        /// 得 31.15 上下;与普攻的 <see cref="BaseHit"/> 不同,可能是魔法/物理攻击力的差别,也可能式子本身不是这样,只有一个样本,证据 E-076)。
        /// </summary>
        public const double SkillBaseHit = 31.15;

        /// <summary>
        /// 技能一击的占位伤害:<see cref="SkillBaseHit"/> × 百分比 / 100 + 固定值(两个数取自技能 .skl 的 [level info]),小数部分按概率进位。
        /// 真实公式要等完整角色面板接进来(P5.M3)。
        /// </summary>
        public static int RollSkill(int ratePercent, int fixedDamage, Random random)
        {
            return Round(SkillBaseHit * ratePercent / 100.0 + fixedDamage, random);
        }

        private static int Round(double v, Random random)
        {
            double floor = Math.Floor(v);
            int result = (int)floor + (random.NextDouble() < v - floor ? 1 : 0);
            return result < 1 ? 1 : result;
        }
    }

    /// <summary>屏幕上浮起的一个伤害数字。</summary>
    public sealed class HitPopup
    {
        public string Text { get; }
        public double WorldX { get; }
        public double WorldY { get; }
        public int Z { get; }
        public double AgeMs { get; set; }

        /// <summary>主角挨的伤害(画成红色);否则是怪物挨的(画成黄色)。</summary>
        public bool OnHero { get; }

        public HitPopup(string text, double worldX, double worldY, int z, bool onHero = false)
        {
            Text = text;
            WorldX = worldX;
            WorldY = worldY;
            Z = z;
            OnHero = onHero;
        }
    }

    /// <summary>
    /// 主角的受击框:基础动作 .ani 当前帧里的类型 14 框(基础动作的 .ani 只有受击框,攻击框在武器图层里,证据 E-069),换成世界坐标;
    /// 朝向与离地高度都算进去。受击姿势(受击动作)的帧也有自己的受击框。
    /// </summary>
    public static class HeroHurtBoxes
    {
        public static List<Aabb> At(TownHero hero)
        {
            var result = new List<Aabb>();
            if (hero == null)
            {
                return result;
            }
            AniFile ani = hero.CurrentAni;
            int frame = hero.CurrentFrame;
            if (ani == null || (uint)frame >= (uint)ani.Frames.Length)
            {
                return result;
            }
            int direction = hero.FacingLeft ? Direction.Left : Direction.Right;
            int wx = (int)Math.Floor(hero.X), wy = (int)Math.Floor(hero.Y), wz = (int)Math.Round(hero.Z);
            foreach (AniBox box in ani.Frames[frame].Boxes)
            {
                if (box.Type == AniBoxType.Damage)
                {
                    result.Add(Combat.ToWorld(box, wx, wy, wz, direction));
                }
            }
            return result;
        }

        /// <summary>头顶的世界高度(受击框的最高点);没有受击框时用 <paramref name="fallback"/>。</summary>
        public static int HeadHeight(TownHero hero, int fallback)
        {
            int top = int.MinValue;
            foreach (Aabb b in At(hero))
            {
                top = Math.Max(top, b.MaxZ);
            }
            return top == int.MinValue ? fallback : top;
        }
    }

    /// <summary>主角受击的几个占位值:没有受击硬直表(Etc/EtcParameter.etc)时用。有表时硬直与击退都按客户端的公式算(<see cref="HitStun"/>,证据 E-080)。</summary>
    public static class HeroHurtTuning
    {
        /// <summary>没有硬直表时的受击硬直(毫秒,未量)。</summary>
        public const int FallbackStunMs = 350;

        /// <summary>没有硬直表时的击退(像素与毫秒,取公式默认值的量级)。</summary>
        public const int FallbackKnockPixels = 30;

        public const int FallbackKnockMs = 150;

        /// <summary>主角被打死后停在受击姿势上的"硬直"(毫秒):实际上是永远。</summary>
        public const double DeadHoldMs = 1.0e12;
    }

    /// <summary>
    /// 主角普攻的攻击框:每个武器图层有自己的动作动画(与基础动作同帧数),攻击框(类型 15)写在武器图层的动画里;
    /// 基础动作里的类型 15 也一并算(例如第三击的基础动画自己带攻击框)。证据 E-069。
    /// </summary>
    public sealed class HeroStrikeBoxes
    {
        private readonly DnfAssets _assets;
        private readonly Dictionary<string, List<AniFile>> _layerAnis = new Dictionary<string, List<AniFile>>(StringComparer.Ordinal);

        public HeroStrikeBoxes(DnfAssets assets)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        }

        /// <summary>主角此刻这一击在第 <paramref name="frame"/> 帧的攻击框(世界坐标)。不在攻击或这一帧没有攻击框时返回空表。</summary>
        public List<Aabb> At(TownHero hero, int frame)
        {
            var result = new List<Aabb>();
            if (hero == null || !hero.IsAttacking)
            {
                return result;
            }
            int direction = hero.FacingLeft ? Direction.Left : Direction.Right;
            int wx = (int)Math.Floor(hero.X), wy = (int)Math.Floor(hero.Y);
            int wz = (int)Math.Round(hero.Z);   // 空中攻击的判定框跟着主角离地的高度
            AddBoxes(result, hero.CurrentAni, frame, wx, wy, wz, direction);
            string file = hero.AttackMotionFile;
            if (file == null)
            {
                return result;
            }
            foreach (AniFile ani in LayerAnimations(hero.Outfit, file))
            {
                AddBoxes(result, ani, frame, wx, wy, wz, direction);
            }
            return result;
        }

        private List<AniFile> LayerAnimations(AvatarOutfit outfit, string motionFile)
        {
            List<string> paths = outfit.WeaponAnimationPaths(motionFile);
            string key = string.Join("|", paths);
            if (_layerAnis.TryGetValue(key, out List<AniFile> cached))
            {
                return cached;
            }
            var anis = new List<AniFile>();
            foreach (string path in paths)
            {
                AniFile ani = _assets.LoadAni(path);
                if (ani != null)
                {
                    anis.Add(ani);
                }
            }
            _layerAnis[key] = anis;
            return anis;
        }

        private static void AddBoxes(List<Aabb> into, AniFile ani, int frame, int wx, int wy, int wz, int direction)
        {
            if (ani == null || (uint)frame >= (uint)ani.Frames.Length)
            {
                return;
            }
            foreach (AniBox box in ani.Frames[frame].Boxes)
            {
                if (box.Type == AniBoxType.Attack)
                {
                    into.Add(Combat.ToWorld(box, wx, wy, wz, direction));
                }
            }
        }
    }

    /// <summary>
    /// 一场能打的副本房间:房间场景 + 主角 + 房间地图 [monster] 里的怪物。每帧 <see cref="Update"/> 推进主角、怪物与命中判定,
    /// <see cref="Build"/> 把怪物、受击对象的血条、伤害数字与连击数叠进绘制列表。
    /// 已做:主角三段普攻(.chr [attack motion])的攻击框与怪物受击框相交判定(同一击对同一怪只算一次)、
    /// 怪物生命值取自 <see cref="MonsterAbility"/>、受击硬直与击退(E-080)、倒地(E-082、E-083)、死亡(E-084)、命中停顿(E-077)。
    /// 技能(证据 E-076):A 鬼斩、S 上挑、D 后跳三个快捷槽(<see cref="Skills"/>),冷却取自技能脚本;鬼斩与上挑能打中怪物,后跳是位移;
    /// 鬼斩放出来时还有两层斩击光影(<see cref="Effects"/>,<see cref="AnimatedEffect"/>)。
    /// 怪物还手(证据 E-086):怪物追到"这一招的攻击框够得着主角受击框"的位置再出招,招式动画里的攻击框(类型 15)与主角当前动作的受击框(类型 14)相交就算打中,
    /// 一次挥击只打一下;伤害交给原版公式(<see cref="DamageFormula"/>,输入是占位值,见 <see cref="MonsterDamage"/>);主角有生命与魔法(<see cref="Vitals"/>)、
    /// 受击硬直与击退、左下角的占位血条、红色伤害数字;生命归零后主角停在受击姿势、画面上显示 DEAD,不再接受输入。
    /// 构造时不给 <see cref="Vitals"/> 就是旧的行为:主角打不到、怪物按固定距离出招、没有血条(命令行 --no-damage、播放器 -dnfnodamage 用它,按时刻对拍的回归检查靠它保持原来的时刻)。
    /// 魔法值(证据 E-087):放技能时扣 .skl 的 [consume MP],不够就放不出来;按 .chr 的 [MP regen speed](每分钟,单位是推断的)连续恢复。
    /// 没做:主角打怪的真实伤害公式(仍用 <see cref="PlaceholderDamage"/>)、主角的真实面板(等级、装备、成长)、主角倒地起身与空中受击、
    /// 复活与结算、技能音效与语音、掉落。出口开关与换房间在 <see cref="DungeonSession"/>。证据 E-069。
    /// </summary>
    public sealed class RoomCombatSession : ISkillSession
    {
        /// <summary>一小步的最长时间(毫秒):比任何动画帧都短,保证每一帧的判定框至少被检查一次。</summary>
        public const double MaxStepMs = 10;

        public const double PopupMs = 800;
        public const double PopupRisePixels = 30;
        public const double TargetBarMs = 3500;
        public const double ComboMs = 2600;

        public const int TargetBarX = 36;
        public const int TargetBarY = 118;
        public const int TargetBarWidth = 112;
        public const int TargetBarHeight = 12;

        private readonly DnfAssets _assets;
        private readonly RoomSession _room;
        private readonly List<MonsterActor> _monsters;
        private readonly HeroStrikeInfo[] _strikes;
        private readonly HeroStrikeInfo _dashStrike;
        private readonly HeroStrikeInfo _jumpStrike;
        private readonly HeroStrikeBoxes _boxes;
        private readonly OverlayArt _art = new OverlayArt();

        /// <summary>原版的底栏(血魔球、技能槽等,证据 E-088);图包里没有时为 null,退回占位的色块血条。</summary>
        private readonly HudArt _hud;
        private readonly List<HitPopup> _popups = new List<HitPopup>();
        private readonly List<AnimatedEffect> _effects = new List<AnimatedEffect>();
        private readonly HashSet<MonsterActor> _hitThisSwing = new HashSet<MonsterActor>();
        private readonly Random _random;

        /// <summary>怪物打主角的伤害种子用的随机数,与主角打怪物的伤害序列分开,免得互相改变对方的序列。</summary>
        private readonly Random _monsterRandom;
        private readonly DamageConstants _damageConstants;

        /// <summary>倒地的随机抖动(水平量 ±20%、高度 ±10%;E-083)用的随机数,与伤害的随机数分开,免得改变伤害序列。</summary>
        private readonly Random _physicsRandom;
        private long _evalSwing = -1;
        private int _evalFrame = -1;
        private double _targetMs = double.MaxValue;
        private double _comboMs = double.MaxValue;
        private double _heroPauseMs;
        private bool _bufferedAttack;
        private bool _bufferedJump;
        private SkillKeys _bufferedSkills;

        public RoomSession Room => _room;
        public IReadOnlyList<MonsterActor> Monsters => _monsters;
        public IReadOnlyList<HitPopup> Popups => _popups;

        /// <summary>正在播的技能特效(鬼斩的斩击光影等)。</summary>
        public IReadOnlyList<AnimatedEffect> Effects => _effects;

        /// <summary>最近一次被打中的怪物(用来画左上角的血条);久未再中就不画。</summary>
        public MonsterActor Target { get; private set; }

        public int Hits { get; private set; }
        public int Kills { get; private set; }
        public int Combo { get; private set; }
        public long TotalDamage { get; private set; }

        /// <summary>放出来的技能次数(冷却中、忙碌或没绑定的按键不算)。</summary>
        public int SkillCasts { get; private set; }

        public string MapPath => _room.MapPath;
        public TownHero Hero => _room.Hero;
        public TownCamera Camera => _room.Camera;
        public int LayerCount => _room.LayerCount;
        public int NpcCount => _monsters.Count;

        /// <summary>没打死、还站着的怪物个数。</summary>
        public int AliveCount
        {
            get
            {
                int n = 0;
                foreach (MonsterActor m in _monsters)
                {
                    if (m.IsAlive)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>A、S、D 三个技能快捷槽与冷却(证据 E-076);构造时没给就是空的(按技能键没有反应)。</summary>
        public HeroSkills Skills { get; }

        /// <summary>
        /// 主角手上这件武器的命中停顿毫秒数(角色 .chr [weapon hit info] 里按装备 [sub type] 取的那一项,证据 E-077)。
        /// 攻击信息的 [hit info] 整数是 -1 或没写时用它;0 表示没有这张表(那就不停顿)。
        /// </summary>
        public int WeaponStunMs { get; }

        /// <summary>
        /// 主角手上这件武器的命中延迟倍率(同一项 [weapon hit info] 里的浮点,证据 E-077、E-080):受击一侧的硬直要乘它。
        /// 攻击信息的 [hit info] 浮点项是 -1 或没写时用它;-1 表示没有这张表。
        /// </summary>
        public float WeaponDelayRate { get; }

        /// <summary>
        /// 受击硬直的三张表(取自 Etc/EtcParameter.etc,证据 E-080);null 表示没有,怪物挨打退回占位硬直。
        /// </summary>
        public HitStunTable StunTable { get; }

        /// <summary>主角还要定住多少毫秒(命中停顿,证据 E-077);0 表示没在停顿。</summary>
        public double HeroPauseMs => _heroPauseMs;

        /// <summary>
        /// 主角的生命与魔法(占位,<see cref="HeroVitals"/>)。为 null 时主角不会受伤、怪物只按固定距离出招、没有血条(旧的行为)。
        /// </summary>
        public HeroVitals Vitals { get; }

        /// <summary>主角被怪物打中的次数(不含被挡住的)。</summary>
        public int HeroHits { get; private set; }

        /// <summary>主角累计挨的伤害。</summary>
        public long HeroDamageTaken { get; private set; }

        /// <summary>主角已经被打死(血量归零)。死后不再接受输入,怪物待机。</summary>
        public bool HeroDead => Vitals != null && !Vitals.IsAlive;

        /// <summary>伤害按原版公式算(读到了 Etc/EtcParameter.etc 的公式常量);否则用占位的"攻击力 × 加成"。</summary>
        public bool HasDamageConstants => _damageConstants != null;

        public RoomCombatSession(DnfAssets assets, RoomSession room, IReadOnlyList<MonsterActor> monsters, IReadOnlyList<HeroStrikeInfo> strikes, int seed = 1,
            HeroStrikeInfo dashStrike = default, HeroStrikeInfo jumpStrike = default, HeroSkills skills = null, int weaponStunMs = 0,
            float weaponDelayRate = -1f, HitStunTable stunTable = null, HeroVitals vitals = null, DamageConstants damageConstants = null)
        {
            Skills = skills ?? new HeroSkills(new HeroSkillSlot[0]);
            WeaponStunMs = weaponStunMs;
            WeaponDelayRate = weaponDelayRate;
            StunTable = stunTable;
            Vitals = vitals;
            _damageConstants = damageConstants;
            _monsterRandom = new Random(unchecked(seed * 131 + 29));
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _room = room ?? throw new ArgumentNullException(nameof(room));
            _monsters = new List<MonsterActor>(monsters ?? new List<MonsterActor>());
            _strikes = new HeroStrikeInfo[strikes == null ? 0 : strikes.Count];
            for (int i = 0; i < _strikes.Length; i++)
            {
                _strikes[i] = strikes[i];
            }
            _dashStrike = dashStrike;
            _jumpStrike = jumpStrike;
            _boxes = new HeroStrikeBoxes(assets);
            _hud = HudArt.For(assets);
            _random = new Random(seed);
            _physicsRandom = new Random(unchecked(seed * 31 + 17));
            SyncStance();
        }

        /// <summary>
        /// 房间里还有活着的怪物就站战斗待机动作,杀光之后站普通待机(证据 E-070,基线整局观察:有怪时主角站的是 [waiting motion],清完怪之后是 [rest motion])。
        /// 换姿态发生在最后一只怪物被打死的那一刻(死亡动画开始时);原版是在那一刻还是在怪物消失(0.5 秒后)切换没有量过。
        /// </summary>
        private void SyncStance()
        {
            _room.Hero.BattleStance = AliveCount > 0;
        }

        /// <summary>
        /// 为房间造出怪物与普攻数据:怪物按地图 [monster] 在出生点摆好(朝左,与原版进图画面一致,证据 E-069),
        /// 等级取 .mob 的 [level] 第一个数,生命值由 <see cref="MonsterAbility.Compute"/> 算(首领按首领种类);
        /// 普攻数据读 swordman 的 .chr [attack info]。缺图或缺脚本的怪物跳过。
        /// <paramref name="heroVitals"/> 为假时主角没有生命值(<see cref="Vitals"/> 为 null):怪物照样走近、出招,但打不到主角,没有血条(自动化检查用)。
        /// </summary>
        public static RoomCombatSession Create(DnfAssets assets, RoomSession room, int seed = 1, bool heroVitals = true)
        {
            var data = new GameData(new PvfScriptSource(assets.Pvf));
            var strikes = new List<HeroStrikeInfo>();
            CharacterDef chr = data.GetCharacter(0);
            for (int i = 0; chr != null && i < room.Hero.AttackCount; i++)
            {
                strikes.Add(HeroStrikeInfo.Read(data, chr.AttackInfoPath(i)));
            }
            // 奔跑攻击与空中攻击的攻击信息:.chr 的 [dashattack info]、[jumpattack info](证据 E-072)
            HeroStrikeInfo dash = HeroStrikeInfo.Read(data, chr?.MotionPath("[dashattack info]"));
            HeroStrikeInfo jump = HeroStrikeInfo.Read(data, chr?.MotionPath("[jumpattack info]"));

            MonsterCatalog catalog = MonsterCatalog.Read(assets);
            var models = new Dictionary<int, MonsterModel>();
            var actors = new List<MonsterActor>();
            int index = 0;
            foreach (RoomMonsterSpawn spawn in room.Map.Monsters)
            {
                if (!models.TryGetValue(spawn.Code, out MonsterModel model))
                {
                    string mobPath = catalog.PathOf(spawn.Code);
                    model = mobPath == null ? null : MonsterModel.Read(assets, mobPath);
                    models[spawn.Code] = model;
                }
                MonsterDef def = data.GetMonster(spawn.Code);
                if (model == null || def == null || model.Waiting == null)
                {
                    continue;
                }
                int level = def.Tags.IntOr("[level]", 1);
                MonsterKind kind = spawn.IsBoss ? MonsterKind.Boss : MonsterKind.Normal;
                MonsterStats stats = MonsterAbility.Compute(def, data.MonsterBaseTables, data.GetMonsterOwnTable(def), level, 0, kind);
                var actor = new MonsterActor(index++, spawn, model, level, stats.HpMax, WalkSpeed(room.Hero, stats.MoveSpeed > 0 ? stats.MoveSpeed : def.Tags.IntOr("[move speed]", 0)),
                    def.Tags.IntOr("[attack delay]", 0), def.Tags.IntOr("[attack speed]", 0), stats.HitRecovery, stats.Weight);
                // 出招的攻击信息:[attack info] 与 [attack motion] 同下标,出招固定用第 0 个动作(证据 E-086)
                actor.Strike = model.AttackInfoPaths.Count > 0 && !string.IsNullOrEmpty(model.AttackInfoPaths[0])
                    ? HeroStrikeInfo.Read(data, model.AttackInfoPaths[0])
                    : new HeroStrikeInfo(0, 0, false);
                actor.PhysicalAttack = stats.PhysicalAttack;
                actor.MagicalAttack = stats.MagicalAttack;
                actors.Add(actor);
            }
            // 受击硬直的三张表:Etc/EtcParameter.etc(证据 E-080);读不到就退回占位硬直
            ScriptLine[] etc = data.ReadScript(HitStunTablePath);
            HitStunTable stunTable = etc == null ? null : HitStunTable.FromEtcParameter(new TagTable(etc));
            bool hasWeapon = TryWeaponHit(chr, room.Hero, out WeaponHitInfo weapon);
            // 主角的生命值:没有就是旧行为(怪物只按固定距离出招、打不到主角、没有血条)——自动化检查里需要"不会被打"的主角时用
            HeroVitals vitals = heroVitals && chr != null ? HeroVitals.FromChr(chr) : null;
            return new RoomCombatSession(assets, room, actors, strikes, seed, dash, jump, HeroSkills.Create(data, chr, 0), hasWeapon ? weapon.StunMs : 0,
                hasWeapon ? weapon.DelayRate : -1f, stunTable, vitals, vitals != null ? MonsterDamage.LoadConstants(assets) : null);
        }

        /// <summary>受击硬直的三张表所在的脚本(证据 E-080)。</summary>
        public const string HitStunTablePath = "Etc/EtcParameter.etc";

        /// <summary>主角穿的武器([equipment type] 为 [weapon] 的那件)的 [sub type] 在 .chr 的 [weapon hit info] 里对应的命中停顿;取不到是 0。</summary>
        public static int WeaponStunOf(CharacterDef chr, TownHero hero)
        {
            return TryWeaponHit(chr, hero, out WeaponHitInfo info) ? info.StunMs : 0;
        }

        /// <summary>主角穿的武器在 .chr 的 [weapon hit info] 里那一项(命中停顿与命中延迟倍率);取不到返回 false。</summary>
        public static bool TryWeaponHit(CharacterDef chr, TownHero hero, out WeaponHitInfo info)
        {
            info = default;
            if (chr == null || hero?.Outfit == null)
            {
                return false;
            }
            foreach (AvatarItem item in hero.Outfit.Items)
            {
                if (string.Equals(item.EquipmentType, "[weapon]", StringComparison.OrdinalIgnoreCase) && chr.TryGetWeaponHitInfo(item.SubType, out info))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 怪物的水平行走速度(像素/秒):主角实测的 <see cref="TownHero.WalkSpeedX"/> 乘以怪物与主角的 [move speed] 之比(证据 E-066、E-069)。
        /// 把怪物的数当成与主角同一个单位是假设;没有数据时取与主角同速。
        /// </summary>
        public static double WalkSpeed(TownHero hero, int monsterMoveSpeed)
        {
            if (hero.MoveSpeedStat <= 0 || monsterMoveSpeed <= 0)
            {
                return hero.WalkSpeedX;
            }
            return hero.WalkSpeedX * monsterMoveSpeed / hero.MoveSpeedStat;
        }

        public void Update(double deltaMs, int dirX, int dirY)
        {
            Update(deltaMs, dirX, dirY, false, false);
        }

        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed)
        {
            Update(deltaMs, dirX, dirY, attackPressed, false);
        }

        /// <summary>
        /// 推进 <paramref name="deltaMs"/> 毫秒。长帧(卡顿)会被切成不超过 <see cref="MaxStepMs"/> 的小步,
        /// 每一小步都推进主角、怪物并判定命中,这样挥击的最后一帧不会因为一帧过长而被跳过、怪物的追击与击退也与帧率无关。
        /// 攻击键与跳跃键是"刚按下"的边沿,只在第一小步里生效。
        /// </summary>
        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed)
        {
            Update(deltaMs, dirX, dirY, attackPressed, jumpPressed, SkillKeys.None);
        }

        /// <summary>
        /// 同上,多一组技能键(A、S、D 的"刚按下"边沿,同样只在第一小步里生效):这一小步里技能放出来了,同一小步的攻击键与跳跃键就作废
        /// (一次按键只做一件事)。
        /// </summary>
        public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed, SkillKeys skillsPressed)
        {
            if (deltaMs <= 0)
            {
                Step(0, dirX, dirY, attackPressed, jumpPressed, skillsPressed);
                return;
            }
            double remaining = deltaMs;
            bool first = true;
            while (remaining > 0)
            {
                double step = remaining < MaxStepMs ? remaining : MaxStepMs;
                Step(step, dirX, dirY, first && attackPressed, first && jumpPressed, first ? skillsPressed : SkillKeys.None);
                first = false;
                remaining -= step;
            }
        }

        private void Step(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed, SkillKeys skillsPressed)
        {
            Skills.Advance(deltaMs);
            Vitals?.RegenMp(deltaMs);   // 魔法值按墙钟恢复(证据 E-087);死了不恢复在 RegenMp 里判断

            if (HeroDead)
            {
                // 主角死了:不再接受任何输入(占位:没有复活与结算)
                dirX = 0;
                dirY = 0;
                attackPressed = false;
                jumpPressed = false;
                skillsPressed = SkillKeys.None;
                _bufferedAttack = false;
                _bufferedJump = false;
                _bufferedSkills = SkillKeys.None;
            }

            // 命中停顿(证据 E-077):主角被定住时,他自己、他的技能特效的时间都不走;冷却、伤害数字、连击计时是墙钟,照常走。
            // 这一小步里停顿结束的话,剩下的时间照常推进。定住期间按下的键存起来,停顿一结束就生效(假设,原版的输入缓冲没读)。
            double heroDelta = deltaMs;
            bool heroFrozen = false;
            if (_heroPauseMs > 0)
            {
                double used = Math.Min(_heroPauseMs, deltaMs);
                _heroPauseMs -= used;
                heroDelta = deltaMs - used;
                heroFrozen = heroDelta <= 0;
                // 受击硬直是不被停顿冻结的秒表(与怪物一样,证据 E-080)
                _room.Hero.AddHurtClock(used);
            }
            if (heroFrozen)
            {
                _bufferedAttack |= attackPressed;
                _bufferedJump |= jumpPressed;
                _bufferedSkills |= skillsPressed;
            }
            else
            {
                attackPressed |= _bufferedAttack;
                jumpPressed |= _bufferedJump;
                skillsPressed |= _bufferedSkills;
                _bufferedAttack = false;
                _bufferedJump = false;
                _bufferedSkills = SkillKeys.None;
                HeroSkillSlot cast = skillsPressed == SkillKeys.None ? null : Skills.TryCast(skillsPressed, _room.Hero, Vitals);
                if (cast != null)
                {
                    attackPressed = false;
                    jumpPressed = false;
                    SkillCasts++;
                    SpawnEffects(cast);
                }
                _room.Update(heroDelta, dirX, dirY, attackPressed, jumpPressed);
            }
            TownHero hero = _room.Hero;
            // 有主角的生命值时,怪物按"这一击能不能打到主角的受击框"决定靠近与出招,主角死了就待机;没有时按固定距离(旧行为)
            bool heroAlive = !HeroDead;
            List<Aabb> heroBoxes = Vitals == null ? null : HeroHurtBoxes.At(hero);
            foreach (MonsterActor m in _monsters)
            {
                m.Update(deltaMs, hero.X, hero.Y, heroAlive, hero.Walk, heroBoxes);
            }
            ResolveHits();
            ResolveMonsterHits();
            SyncStance();
            for (int i = _popups.Count - 1; i >= 0; i--)
            {
                _popups[i].AgeMs += deltaMs;
                if (_popups[i].AgeMs >= PopupMs)
                {
                    _popups.RemoveAt(i);
                }
            }
            for (int i = _effects.Count - 1; i >= 0; i--)
            {
                _effects[i].Advance(heroDelta);
                if (_effects[i].Finished)
                {
                    _effects.RemoveAt(i);
                }
            }
            _targetMs += deltaMs;
            _comboMs += deltaMs;
            if (_comboMs >= ComboMs)
            {
                Combo = 0;
            }
            if (_targetMs >= TargetBarMs)
            {
                Target = null;
            }
        }

        /// <summary>技能放出来的那一刻,在主角前方 <see cref="HeroSkillSlot.EffectForwardX"/> 像素、同一条地面线上放这个技能的特效动画(朝向跟主角)。画在主角前面一层。</summary>
        private void SpawnEffects(HeroSkillSlot slot)
        {
            TownHero hero = _room.Hero;
            double x = hero.X + (hero.FacingLeft ? -slot.EffectForwardX : slot.EffectForwardX);
            foreach (string path in slot.EffectPaths)
            {
                AnimatedEffect effect = AnimatedEffect.Create(_assets, path, x, hero.Y, 0.0, hero.FacingLeft);
                if (effect != null)
                {
                    _effects.Add(effect);
                }
            }
        }

        private void ResolveHits()
        {
            TownHero hero = _room.Hero;
            if (!hero.IsAttacking)
            {
                _evalSwing = -1;
                return;
            }
            if (hero.SwingNumber != _evalSwing)
            {
                _evalSwing = hero.SwingNumber;
                _evalFrame = -1;
                _hitThisSwing.Clear();
            }
            int frame = hero.CurrentFrame;
            for (int f = _evalFrame + 1; f <= frame; f++)
            {
                EvaluateFrame(hero, f);
            }
            if (frame > _evalFrame)
            {
                _evalFrame = frame;
            }
        }

        private void EvaluateFrame(TownHero hero, int frame)
        {
            List<Aabb> strike = _boxes.At(hero, frame);
            if (strike.Count == 0)
            {
                return;
            }
            foreach (MonsterActor m in _monsters)
            {
                if (!m.IsAlive || _hitThisSwing.Contains(m))
                {
                    continue;
                }
                if (Touches(strike, m.DamageBoxes()))
                {
                    _hitThisSwing.Add(m);
                    Strike(hero, m);
                }
            }
        }

        private static bool Touches(List<Aabb> strike, List<Aabb> hurt)
        {
            foreach (Aabb a in strike)
            {
                foreach (Aabb b in hurt)
                {
                    if (a.Overlaps(b))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private void Strike(TownHero hero, MonsterActor monster)
        {
            HeroStrikeInfo info;
            switch (hero.AttackKind)
            {
                case HeroAttackKind.Dash:
                    info = _dashStrike;
                    break;
                case HeroAttackKind.Jump:
                    info = _jumpStrike;
                    break;
                case HeroAttackKind.Skill:
                    // 有自己攻击信息与伤害数的技能(鬼斩)用自己的;借普攻动作放的技能(上挑)沿用那一段普攻的攻击信息
                    HeroSkillSlot slot = Skills.SlotOfSkill(hero.SkillId);
                    if (slot != null && slot.Strike.HasValue)
                    {
                        info = slot.Strike.Value;
                        break;
                    }
                    goto default;
                default:
                    info = hero.AttackStage < _strikes.Length ? _strikes[hero.AttackStage] : new HeroStrikeInfo(0, 0, false);
                    break;
            }
            int damage = info.IsSkillDamage
                ? PlaceholderDamage.RollSkill(info.SkillRatePercent, info.SkillFixed, _random)
                : PlaceholderDamage.Roll(info.DamageBonus, _random);
            // 受击硬直与击退按客户端的公式算(证据 E-080);没有硬直表时 StunInput 无效,怪物走占位值。
            // 倒地(以及空中、倒地中又挨打)按客户端的开始处理算:体重缩放的击飞、z 抛物线、落地后颠一下再躺下(证据 E-082、E-083)
            bool killed = monster.ApplyHit(damage, info.KnockDown, info.PushAside, hero.FacingLeft, StunInput(info), DownInput(info));
            int stun = HitStop.StunMs(info.HitValue, info.UseWeaponHitDelayRate, WeaponStunMs);
            if (stun > 0)
            {
                // 攻击方与受击方定住同样久,已经在停顿的取剩余更长的那个(E-077)
                monster.Pause(stun);
                if (stun > _heroPauseMs)
                {
                    _heroPauseMs = stun;
                }
            }
            Hits++;
            Combo++;
            _comboMs = 0;
            TotalDamage += damage;
            Target = monster;
            _targetMs = 0;
            if (killed)
            {
                Kills++;
            }
            _popups.Add(new HitPopup(damage.ToString(System.Globalization.CultureInfo.InvariantCulture), monster.X, monster.Y, monster.HeadHeight()));
        }

        // ------------------------------------------------------------------ 怪物打主角(P5.M5,证据 E-086)

        /// <summary>
        /// 怪物出招时,出招动画每一帧的攻击框与主角此刻的受击框相交判定(与主角打怪物同一套相交盒规则);
        /// 同一次出招只打中一次,按"上次判到哪一帧"补判中间跳过的帧。
        /// </summary>
        private void ResolveMonsterHits()
        {
            if (Vitals == null || !Vitals.IsAlive)
            {
                return;
            }
            TownHero hero = _room.Hero;
            foreach (MonsterActor m in _monsters)
            {
                if (!m.IsAlive || m.Phase != MonsterPhase.Attack)
                {
                    continue;
                }
                int frame = m.CurrentFrame;
                for (int f = m.AttackEvalFrame + 1; f <= frame && !m.HitHeroThisSwing; f++)
                {
                    List<Aabb> strike = m.AttackBoxesAt(f);
                    if (strike.Count > 0 && Touches(strike, HeroHurtBoxes.At(hero)))
                    {
                        m.HitHeroThisSwing = true;
                        StrikeHero(m, hero);
                    }
                }
                if (frame > m.AttackEvalFrame)
                {
                    m.AttackEvalFrame = frame;
                }
            }
        }

        /// <summary>
        /// 怪物这一击打中了主角:伤害按 <see cref="MonsterDamage"/>(原版公式,输入是推断)、扣血、飘红字;
        /// 主角进受击硬直(硬直与击退按客户端的公式,受击方是主角,攻击方没有所属者所以攻击方恢复项为 0;命中模式 [burn] 不击退,证据 E-080),
        /// 命中停顿按攻击信息的 [hit info] 整数(怪物没有武器表,没写就是 0,不停顿,证据 E-077);血量归零主角停在受击姿势上(占位的死亡)。
        /// 倒地类的攻击(<see cref="HeroStrikeInfo.KnockDown"/>)打主角的倒地流程还没做,这里与普通受击一样处理。
        /// </summary>
        private void StrikeHero(MonsterActor monster, TownHero hero)
        {
            HeroStrikeInfo info = monster.Strike;
            int attack = info.Magical ? monster.MagicalAttack : monster.PhysicalAttack;
            int damage = _damageConstants != null
                ? MonsterDamage.Roll(info, monster.Level, attack, Vitals, _damageConstants, unchecked((uint)_monsterRandom.Next()))
                : Math.Max(1, (int)Math.Round(attack * (100.0 + info.DamageBonus) / 100.0));
            bool killed = Vitals.Damage(damage);
            HeroHits++;
            HeroDamageTaken += damage;
            double stunMs = HeroHurtTuning.FallbackStunMs, knockPixels = HeroHurtTuning.FallbackKnockPixels, knockMs = HeroHurtTuning.FallbackKnockMs;
            if (StunTable != null)
            {
                float rate = HitStun.Rate(info.HitScale, -1f, info.UseWeaponHitDelayRate, info.Magical);
                HitStunResult r = HitStun.Damage(StunTable, Vitals.HitRecovery, 0, rate, info.ForceHitStunMs, info.KnockBackType, info.NoKnockBack);
                stunMs = r.StunMs;
                knockPixels = r.KnockPixels;
                knockMs = r.KnockMs;
            }
            hero.BeginHurt(killed ? HeroHurtTuning.DeadHoldMs : stunMs, knockPixels, knockMs, monster.FacingLeft);
            int pause = HitStop.StunMs(info.HitValue, info.UseWeaponHitDelayRate, 0);
            if (pause > 0)
            {
                monster.Pause(pause);
                if (pause > _heroPauseMs)
                {
                    _heroPauseMs = pause;
                }
            }
            _popups.Add(new HitPopup(damage.ToString(System.Globalization.CultureInfo.InvariantCulture), hero.X, hero.Y, HeroHurtBoxes.HeadHeight(hero, 80), true));
        }

        /// <summary>这一击的倒地输入(证据 E-083):攻击信息的抬升、弹起、不受体重,外加两个抖动用的随机数。</summary>
        private KnockdownInput DownInput(HeroStrikeInfo info)
        {
            return new KnockdownInput(info.LiftUp, info.Bounce, info.IgnoreWeight, info.ReactionDamage, _physicsRandom.Next(1000), _physicsRandom.Next(1000));
        }

        /// <summary>
        /// 这一击的受击硬直输入(证据 E-080):倍率取攻击信息的命中延迟倍率([hit info] 的浮点项,没写取武器表),按客户端的两次取整换成 rate;
        /// 没有硬直表时返回无效值,怪物走占位硬直。
        /// </summary>
        private HitStunInput StunInput(HeroStrikeInfo info)
        {
            if (StunTable == null)
            {
                return default;
            }
            float rate = HitStun.Rate(info.HitScale, WeaponDelayRate, info.UseWeaponHitDelayRate, info.Magical);
            return new HitStunInput(StunTable, rate, info.ForceHitStunMs, info.KnockBackType, info.NoKnockBack);
        }

        public List<SceneSprite> Build(long timeMs)
        {
            return Build(timeMs, null);
        }

        /// <summary>同 <see cref="Build(long)"/>,另外把 <paramref name="hud"/>(副本的小地图等外部拼好的屏幕空间精灵)叠在最上层。</summary>
        public List<SceneSprite> Build(long timeMs, IReadOnlyList<SceneSprite> hud)
        {
            TownProjection projection = _room.Projection;
            var extra = new List<SceneSprite>(_monsters.Count + 8 + (hud == null ? 0 : hud.Count));
            foreach (MonsterActor m in _monsters)
            {
                SceneSprite s = m.Sprite(_assets, projection);
                if (s != null)
                {
                    extra.Add(s);
                }
            }
            foreach (AnimatedEffect e in _effects)
            {
                SceneSprite s = e.Sprite(_assets, projection);
                if (s != null)
                {
                    extra.Add(s);
                }
            }
            AddOverlay(extra, projection);
            if (hud != null)
            {
                extra.AddRange(hud);
            }
            return _room.Build(timeMs, extra);
        }

        private void AddOverlay(List<SceneSprite> into, TownProjection projection)
        {
            int order = 0;
            MonsterActor target = Target;
            if (target != null)
            {
                int filled = (int)Math.Round((double)(TargetBarWidth - 2) * target.Hp / target.HpMax);
                if (target.Hp > 0 && filled < 1)
                {
                    filled = 1;
                }
                into.Add(OverlayArt.Place(_art.Bar(TargetBarWidth, TargetBarHeight, filled), TargetBarX, TargetBarY, order++, "target-bar"));
                SpriteFrame label = _art.Text("Lv." + target.Level.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "  " + target.Hp.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + target.HpMax.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    2, OverlayArt.White);
                into.Add(OverlayArt.Place(label, TargetBarX, TargetBarY - OverlayArt.TextHeight(2) - 2, order++, "target-label"));
            }
            if (Vitals != null)
            {
                if (_hud != null)
                {
                    AddHud(into, ref order);
                }
                else
                {
                    AddHeroBars(into, ref order);
                }
                if (!Vitals.IsAlive)
                {
                    SpriteFrame dead = _art.Text("DEAD", 8, HeroPopupColor);
                    into.Add(OverlayArt.Place(dead, (800 - dead.Width) / 2, 220, order++, "hero-dead"));
                }
            }
            foreach (HitPopup p in _popups)
            {
                SpriteFrame text = _art.Text(p.Text, 3, p.OnHero ? HeroPopupColor : OverlayArt.Yellow);
                double rise = PopupRisePixels * Math.Min(1.0, p.AgeMs / (PopupMs * 0.5));
                int sx = (int)Math.Floor(p.WorldX) - projection.CameraX - text.Width / 2;
                int sy = (int)Math.Floor(p.WorldY) + projection.GroundY - p.Z - text.Height - 6 - (int)rise;
                into.Add(OverlayArt.Place(text, sx, sy, order++, "damage"));
            }
            if (Combo > 1)
            {
                SpriteFrame combo = _art.Text(Combo.ToString(System.Globalization.CultureInfo.InvariantCulture) + " HIT", 4, OverlayArt.Orange);
                into.Add(OverlayArt.Place(combo, 800 - combo.Width - 24, 236, order++, "combo"));
            }
        }

        /// <summary>主角受伤飘字的颜色(红)。</summary>
        public const uint HeroPopupColor = 0xFF5050u;

        /// <summary>主角生命条与魔法条(占位的叠加层,不是原版的界面):屏幕左下角,红色与蓝色,上面各一行 "HP 当前/上限"。只在图包里没有原版底栏时用(<see cref="HudArt"/>);死后居中显示 DEAD(两种底栏都有)。</summary>
        public const int HeroBarX = 24;
        public const int HeroBarY = 540;
        public const int HeroBarWidth = 180;
        public const int HeroBarHeight = 12;

        private void AddHeroBars(List<SceneSprite> into, ref int order)
        {
            HeroVitals v = Vitals;
            int hpFill = FilledWidth(v.Hp, v.HpMax);
            into.Add(OverlayArt.Place(_art.Bar(HeroBarWidth, HeroBarHeight, hpFill), HeroBarX, HeroBarY, order++, "hero-hp"));
            SpriteFrame hpLabel = _art.Text("HP " + v.Hp.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/"
                + v.HpMax.ToString(System.Globalization.CultureInfo.InvariantCulture), 2, OverlayArt.White);
            into.Add(OverlayArt.Place(hpLabel, HeroBarX, HeroBarY - OverlayArt.TextHeight(2) - 2, order++, "hero-hp-label"));
            if (v.MpMax > 0)
            {
                int mpY = HeroBarY + HeroBarHeight + 14;
                into.Add(OverlayArt.Place(_art.Bar(HeroBarWidth, HeroBarHeight, FilledWidth(v.Mp, v.MpMax), OverlayArt.BlueBright, OverlayArt.BlueDark, OverlayArt.BlueBack),
                    HeroBarX, mpY, order++, "hero-mp"));
                SpriteFrame mpLabel = _art.Text("MP " + v.Mp.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/"
                    + v.MpMax.ToString(System.Globalization.CultureInfo.InvariantCulture), 2, OverlayArt.White);
                into.Add(OverlayArt.Place(mpLabel, HeroBarX, mpY - OverlayArt.TextHeight(2) - 2, order++, "hero-mp-label"));
            }
        }

        /// <summary>
        /// 原版底栏(<see cref="HudArt"/>,证据 E-088):血球、魔法球、等级牌与 A S D 技能槽(能放亮、冷却或魔法不够时灰)。
        /// 技能槽按 <see cref="Skills"/> 的次序对应 A、S、D。
        /// </summary>
        private void AddHud(List<SceneSprite> into, ref int order)
        {
            HeroVitals v = Vitals;
            var states = new List<HudSkillState>(Skills.Slots.Count);
            for (int i = 0; i < Skills.Slots.Count; i++)
            {
                HeroSkillSlot s = Skills.Slots[i];
                bool usable = s.Ready && v.IsAlive && v.Mp >= s.ConsumeMp;
                states.Add(new HudSkillState(i, s.Def, usable));
            }
            _hud.Build(into, ref order, v.Level, v.Hp, v.HpMax, v.Mp, v.MpMax, states);
        }

        private static int FilledWidth(int value, int max)
        {
            int filled = (int)Math.Round((double)(HeroBarWidth - 2) * value / Math.Max(1, max));
            return value > 0 && filled < 1 ? 1 : filled;
        }
    }

    /// <summary>演示用的房间战斗:在 <see cref="DemoRoom"/> 的房间上摆怪物。</summary>
    public static class DemoCombat
    {
        public static RoomCombatSession CreateSession(DnfAssets assets, string mapPath, string weaponEqu = DemoHero.StarterWeapon, int seed = 1,
            bool heroVitals = true)
        {
            RoomSession room = DemoRoom.CreateSession(assets, mapPath, weaponEqu);
            return RoomCombatSession.Create(assets, room, seed, heroVitals);
        }
    }
}
